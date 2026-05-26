using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.AI.Generation;
using Wavee.AI.Tools;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Wraps Phi Silica for the lyrics-meaning feature:
///   - <see cref="GetLyricsMeaningAsync"/>: whole-song lyrics meaning through
///     structured JSON generation with a short interpretive prompt + citations.
///
/// All inference is on-device (NPU) via Microsoft Foundry on Windows. Calls are gated
/// through <see cref="AiCapabilities"/>: if the user hasn't opted in or the hardware
/// isn't a Copilot+ PC, every method returns <see cref="LyricsAiResult.Unavailable"/>
/// without touching the model.
///
/// Caching: in-memory per-track via shared <see cref="Lazy{T}"/> task — all UI surfaces
/// asking about the same track reuse the same in-flight or completed result.
///
/// Cancellation: cancels only the caller's wait; the shared in-flight task keeps
/// running so another visible lyrics surface can reuse the same result instead of
/// starting a duplicate call.
///
/// Prompt construction, evidence JSON parsing, and output normalization live in
/// <see cref="LyricsAiPrompts"/>, <see cref="LyricsAiEvidenceParser"/>, and
/// <see cref="LyricsAiOutputNormalizer"/> respectively. Public DTOs live in
/// <see cref="LyricsAiResult"/>.
/// </summary>
public sealed class LyricsAiService
{
    private readonly AiCapabilities _capabilities;
    private readonly ILanguageModelClient _model;
    private readonly IMusicGroundingProvider? _musicGrounding;
    private readonly IWebSearchToolProvider? _webSearch;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<LyricsAiResult>>> _lyricsMeaningRequests =
        new(StringComparer.Ordinal);

    public LyricsAiService(
        AiCapabilities capabilities,
        ILanguageModelClient model,
        IMusicGroundingProvider? musicGrounding = null,
        IWebSearchToolProvider? webSearch = null,
        ILogger<LyricsAiService>? logger = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _musicGrounding = musicGrounding;
        _webSearch = webSearch;
        _logger = logger;
    }

    /// <summary>
    /// Explains the meaning of the whole lyric. Per-track requests are cached as
    /// Lazy tasks so all UI surfaces share one in-flight model call and one result.
    /// </summary>
    public async Task<LyricsAiResult> GetLyricsMeaningAsync(
        string trackUri, string fullLyric,
        IProgress<string>? deltaProgress = null,
        CancellationToken ct = default,
        string? trackTitle = null,
        string? artistName = null)
    {
        if (!_capabilities.IsLyricsSummarizeEnabled)
        {
            _logger?.LogWarning("GetLyricsMeaningAsync unavailable before model call. {Diagnostics}",
                _capabilities.DescribeDiagnosticState());
            return LyricsAiResult.Unavailable;
        }
        if (string.IsNullOrWhiteSpace(fullLyric))
            return LyricsAiResult.Empty;

        var normalizedTrackUri = NormalizeTrackUri(trackUri);

        var created = new Lazy<Task<LyricsAiResult>>(
            () => GenerateLyricsMeaningCoreAsync(fullLyric, trackTitle, artistName, deltaProgress),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var request = _lyricsMeaningRequests.GetOrAdd(normalizedTrackUri, created);
        var fromExistingRequest = !ReferenceEquals(request, created);

        return await PhiSilicaStructuredTextPipeline.AwaitRequestAsync(
            request,
            fromExistingRequest,
            () => _lyricsMeaningRequests.TryRemove(normalizedTrackUri, out _),
            "GetLyricsMeaningAsync",
            _logger,
            ct);
    }

    public Task<LyricsAiResult> SummarizeSongAsync(
        string trackUri, string fullLyric,
        IProgress<string>? deltaProgress = null,
        CancellationToken ct = default,
        string? trackTitle = null,
        string? artistName = null)
        => GetLyricsMeaningAsync(trackUri, fullLyric, deltaProgress, ct, trackTitle, artistName);

    public bool TryGetCachedLyricsMeaning(string trackUri, out LyricsAiResult result)
    {
        result = default;
        var normalizedTrackUri = NormalizeTrackUri(trackUri);
        if (!_lyricsMeaningRequests.TryGetValue(normalizedTrackUri, out var request)
            || !request.IsValueCreated
            || !request.Value.IsCompletedSuccessfully)
        {
            return false;
        }

        var cached = request.Value.Result;
        if (cached.Kind != LyricsAiResultKind.Ok)
            return false;

        result = cached.WithCacheState(fromCache: true);
        return true;
    }

    /// <summary>
    /// Drops cached lyrics meanings (e.g. on logout, or as a manual reset
    /// affordance in Settings). Cheap.
    /// </summary>
    public void ClearCache()
    {
        _lyricsMeaningRequests.Clear();
    }

    private async Task<LyricsAiResult> GenerateLyricsMeaningCoreAsync(
        string fullLyric,
        string? trackTitle,
        string? artistName,
        IProgress<string>? deltaProgress)
    {
        // Kick off the web grounding search in parallel with the local prompt
        // assembly + Phi Silica readiness check. The search runs whenever the
        // provider thinks it can — failures or empty results collapse to no
        // grounding rather than blocking the AI call.
        var groundingTask = FetchMusicGroundingAsync(trackTitle, artistName);

        if (!await _capabilities.EnsureLanguageModelReadyAsync())
        {
            _logger?.LogWarning("GetLyricsMeaningAsync unavailable: EnsureLanguageModelReadyAsync returned false. {Diagnostics}",
                _capabilities.DescribeDiagnosticState());
            return LyricsAiResult.Unavailable;
        }

        var numberedLyrics = LyricsAiPrompts.BuildNumberedLyricsContext(fullLyric);
        if (numberedLyrics.LineCount == 0)
            return LyricsAiResult.Empty;

        var fallbackLyrics = LyricsAiPrompts.BuildNumberedLyricsContext(
            LyricsAiPrompts.TrimLyricsForFallback(fullLyric));
        var trackContext = LyricsAiPrompts.BuildTrackContext(trackTitle, artistName);
        var grounding = await groundingTask.ConfigureAwait(false);

        return await GeneratePlainTextLyricsMeaningAsync(
            LyricsAiPrompts.BuildLyricsMeaningPlainTextPrompt(numberedLyrics.Text, trackContext, grounding.Sources),
            LyricsAiPrompts.BuildLyricsMeaningPlainTextFallbackPrompt(fallbackLyrics.Text, trackContext, grounding.Sources),
            grounding.Sources,
            deltaProgress);
    }

    private async Task<MusicGroundingResult> FetchMusicGroundingAsync(
        string? trackTitle,
        string? artistName)
    {
        var artist = (artistName ?? string.Empty).Trim();
        var title = (trackTitle ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(artist) && string.IsNullOrEmpty(title))
            return MusicGroundingResult.Empty;

        if (_musicGrounding?.IsAvailable == true)
        {
            try
            {
                return await _musicGrounding.GetGroundingAsync(
                        new MusicGroundingRequest(
                            MusicGroundingKind.Track,
                            ArtistName: artist,
                            TrackTitle: title,
                            MaxSources: 5))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Lyrics music grounding failed for {Artist}/{Track}", artist, title);
            }
        }

        var webResults = await FetchWebGroundingAsync(trackTitle, artistName).ConfigureAwait(false);
        var sources = webResults
            .Where(r => !string.IsNullOrWhiteSpace(r.Title) && !string.IsNullOrWhiteSpace(r.Url))
            .Take(5)
            .Select(r => new MusicGroundingSource(
                r.Title,
                r.Url,
                r.Snippet,
                r.Source ?? "Web",
                MusicGroundingKind.Track,
                IsMusicSpecific: true,
                Reliability: 0.5))
            .ToList();
        return sources.Count == 0 ? MusicGroundingResult.Empty : new MusicGroundingResult(sources);
    }

    private async Task<IReadOnlyList<WebSearchResult>> FetchWebGroundingAsync(
        string? trackTitle,
        string? artistName)
    {
        if (_webSearch is null || !_webSearch.IsAvailable)
            return [];

        var artist = (artistName ?? string.Empty).Trim();
        var title = (trackTitle ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(artist) && string.IsNullOrEmpty(title))
            return [];

        var query = string.IsNullOrEmpty(artist)
            ? $"\"{title}\" song meaning lyrics interpretation"
            : string.IsNullOrEmpty(title)
                ? $"{artist} song meaning lyrics interpretation"
                : $"{artist} - \"{title}\" song meaning lyrics interpretation";

        try
        {
            return await _webSearch
                .SearchAsync(query, new WebSearchOptions(MaxResults: 5))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Lyrics web grounding failed for query {Query}", query);
            return [];
        }
    }

    private async Task<LyricsAiResult> GeneratePlainTextLyricsMeaningAsync(
        string prompt,
        string fallbackPrompt,
        IReadOnlyList<MusicGroundingSource> sources,
        IProgress<string>? deltaProgress)
    {
        var response = await _model.GenerateTextAsync(
            new AiTextGenerationRequest(prompt, 0.25f, "LyricsMeaning"),
            deltaProgress,
            CancellationToken.None);
        var usedFallback = false;

        if (response.Status != AiGenerationStatus.Complete
            && ShouldRetryPlainTextWithFallback(response.Status))
        {
            response = await _model.GenerateTextAsync(
                new AiTextGenerationRequest(fallbackPrompt, 0.2f, "LyricsMeaningFallback"),
                deltaProgress,
                CancellationToken.None);
            usedFallback = true;
        }

        if (response.Status != AiGenerationStatus.Complete)
            return ToPlainTextFailureResult(response);

        var text = CleanPlainTextMeaning(response.Text);
        if (string.IsNullOrWhiteSpace(text) && !usedFallback)
        {
            response = await _model.GenerateTextAsync(
                new AiTextGenerationRequest(fallbackPrompt, 0.2f, "LyricsMeaningFallbackEmpty"),
                deltaProgress,
                CancellationToken.None);

            if (response.Status != AiGenerationStatus.Complete)
                return ToPlainTextFailureResult(response);

            text = CleanPlainTextMeaning(response.Text);
        }

        return string.IsNullOrWhiteSpace(text)
            ? LyricsAiResult.Error("Phi Silica returned an empty lyrics meaning.")
            : LyricsAiResult.Ok(text, fromCache: false, sources);
    }

    private LyricsAiResult ToPlainTextFailureResult(AiGenerationResult generated)
    {
        _logger?.LogWarning(
            "GetLyricsMeaningAsync plaintext returned Phi Silica status {Status}. error={ErrorMessage}; diagnostics={Diagnostics}",
            generated.Status,
            string.IsNullOrWhiteSpace(generated.ErrorMessage) ? "<no extended error>" : generated.ErrorMessage,
            string.IsNullOrWhiteSpace(generated.DiagnosticMessage) ? "<none>" : generated.DiagnosticMessage);

        return generated.Status switch
        {
            AiGenerationStatus.BlockedByPolicy => LyricsAiResult.Filtered,
            AiGenerationStatus.PromptBlockedByContentModeration => LyricsAiResult.Filtered,
            AiGenerationStatus.ResponseBlockedByContentModeration => LyricsAiResult.Filtered,
            AiGenerationStatus.Unavailable => LyricsAiResult.Unavailable,
            AiGenerationStatus.PromptLargerThanContext =>
                LyricsAiResult.Error("Prompt exceeded Phi Silica's context window."),
            _ => LyricsAiResult.Error(generated.ErrorMessage ?? generated.Status.ToString()),
        };
    }

    private static bool ShouldRetryPlainTextWithFallback(AiGenerationStatus status)
        => status is AiGenerationStatus.PromptLargerThanContext
            or AiGenerationStatus.PromptBlockedByContentModeration
            or AiGenerationStatus.ResponseBlockedByContentModeration
            or AiGenerationStatus.BlockedByPolicy
            or AiGenerationStatus.Error;

    private static string CleanPlainTextMeaning(string text)
        => LyricsAiOutputNormalizer.NormalizeLyricsMeaningOutput(
            LyricsAiOutputNormalizer.StripEvidenceLines(text));

    private static string NormalizeTrackUri(string trackUri)
        => string.IsNullOrWhiteSpace(trackUri) ? "spotify:track:unknown" : trackUri.Trim();
}
