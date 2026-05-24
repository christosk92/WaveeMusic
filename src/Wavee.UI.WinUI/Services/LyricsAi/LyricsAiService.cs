using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<LyricsAiResult>>> _lyricsMeaningRequests =
        new(StringComparer.Ordinal);

    public LyricsAiService(AiCapabilities capabilities, ILogger<LyricsAiService>? logger = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
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

        // Structured generation intentionally does not stream progress because
        // intermediate deltas may be partial JSON, not user-facing prose.
        // Concurrent callers share the same final in-flight result.
        var created = new Lazy<Task<LyricsAiResult>>(
            () => GenerateLyricsMeaningCoreAsync(fullLyric, trackTitle, artistName),
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
        string? artistName)
    {
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

        return await PhiSilicaStructuredTextPipeline.GenerateAsync(
            new PhiSilicaStructuredTextRequest(
                "GetLyricsMeaningAsync",
                LyricsAiPrompts.BuildLyricsMeaningPrompt(numberedLyrics.Text, trackContext),
                LyricsAiPrompts.BuildLyricsMeaningFallbackPrompt(fallbackLyrics.Text, trackContext),
                // Greedy decoding (temperature 0) — faster under constrained JSON
                // schemas and produces tighter output for instructive prompts.
                0.0f,
                text => LyricsAiOutputNormalizer.NormalizeLyricsMeaningOutput(
                    LyricsAiOutputNormalizer.StripEvidenceLines(text)),
                "Phi Silica returned an empty lyrics meaning.")
            {
                JsonSchema = LyricsAiEvidenceParser.LyricsMeaningEvidenceJsonSchema,
                BuildSuccessResult = (response, text) =>
                    LyricsAiEvidenceParser.BuildLyricsMeaningSuccessResult(response, text, numberedLyrics.LineCount),
            },
            _logger,
            CancellationToken.None);
    }

    private static string NormalizeTrackUri(string trackUri)
        => string.IsNullOrWhiteSpace(trackUri) ? "spotify:track:unknown" : trackUri.Trim();
}
