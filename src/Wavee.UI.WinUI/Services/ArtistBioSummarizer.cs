using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.AI.Generation;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Generates a short on-device biography for an artist page. Mirrors
/// <see cref="LyricsAiService"/>'s
/// Phi Silica bridging pattern: gates through <see cref="AiCapabilities"/>,
/// uses the plain-text Phi Silica path so token progress can be streamed into
/// the artist page, and reuses <see cref="LyricsAiResult"/> as the typed result
/// envelope.
///
/// The prompt is intentionally conservative — it gets the artist name, optional
/// known signals (genres, monthly listeners, top track names), and asks for one
/// neutral ~100-word paragraph. No quotes, no markdown, no bullets. We do NOT
/// claim biographical facts we can't ground in the input signals; the model is
/// instructed to characterise the artist's sound and prominence, not invent
/// label history or member counts.
/// </summary>
public sealed class ArtistBioSummarizer
{
    private readonly AiCapabilities _capabilities;
    private readonly ILanguageModelClient _model;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<LyricsAiResult>>> _requests =
        new(StringComparer.Ordinal);

    // Per-session cache of artist URIs that previously returned BlockedByPolicy
    // from Phi Silica. Next call for the same URI short-circuits — skips the
    // prompt build and the Phi Silica round-trip entirely. byte value is arbitrary;
    // ConcurrentDictionary doesn't have a Set primitive. Cleared by ClearCache.
    private readonly ConcurrentDictionary<string, byte> _knownBlocked =
        new(StringComparer.Ordinal);

    /// <summary>~90–140 words plus punctuation/headroom.</summary>
    private const int MaxBioCharacters = 1300;

    public ArtistBioSummarizer(
        AiCapabilities capabilities,
        ILanguageModelClient model,
        ILogger<ArtistBioSummarizer>? logger = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _logger = logger;
    }

    /// <summary>
    /// Asks Phi Silica for a fuller neutral biography of the artist. Cached
    /// in-memory per <paramref name="artistUri"/>; concurrent callers share the
    /// same in-flight Lazy task.
    /// </summary>
    public Task<LyricsAiResult> SummarizeBioAsync(
        string artistUri,
        string artistName,
        IReadOnlyList<string>? genres = null,
        string? monthlyListenersDisplay = null,
        IReadOnlyList<string>? topTrackNames = null,
        IProgress<string>? deltaProgress = null,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsArtistBioSummarizeEnabled)
        {
            _logger?.LogDebug("SummarizeBioAsync gated off. {Diagnostics}",
                _capabilities.DescribeDiagnosticState());
            return Task.FromResult(LyricsAiResult.Unavailable);
        }
        if (string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(LyricsAiResult.Empty);

        var key = NormalizeArtistUri(artistUri);
        var created = new Lazy<Task<LyricsAiResult>>(
            () => SummarizeBioCoreAsync(key, artistName, genres, monthlyListenersDisplay, topTrackNames, deltaProgress),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var request = _requests.GetOrAdd(key, created);
        var fromExistingRequest = !ReferenceEquals(request, created);

        return AwaitRequestAsync(request, key, fromExistingRequest, ct);
    }

    public bool TryGetCached(string artistUri, out LyricsAiResult result)
    {
        result = default;
        var key = NormalizeArtistUri(artistUri);
        if (!_requests.TryGetValue(key, out var req)
            || !req.IsValueCreated
            || !req.Value.IsCompletedSuccessfully)
        {
            return false;
        }

        var cached = req.Value.Result;
        if (cached.Kind != LyricsAiResultKind.Ok)
            return false;

        result = LyricsAiResult.Ok(cached.Text, fromCache: true);
        return true;
    }

    public void ClearCache()
    {
        _requests.Clear();
        _knownBlocked.Clear();
    }

    private Task<LyricsAiResult> AwaitRequestAsync(
        Lazy<Task<LyricsAiResult>> request, string key, bool fromExistingRequest, CancellationToken ct)
        => PhiSilicaStructuredTextPipeline.AwaitRequestAsync(
            request,
            fromExistingRequest,
            () => _requests.TryRemove(key, out _),
            "SummarizeBioAsync",
            _logger,
            ct);

    private async Task<LyricsAiResult> SummarizeBioCoreAsync(
        string artistUri,
        string artistName,
        IReadOnlyList<string>? genres,
        string? monthlyListenersDisplay,
        IReadOnlyList<string>? topTrackNames,
        IProgress<string>? deltaProgress)
    {
        // Short-circuit: if a previous call for this artist URI came back
        // BlockedByPolicy from Phi Silica, skip the entire prompt-build +
        // Phi Silica round-trip on subsequent calls. Avoids 1 KB+ of managed
        // heap per call x the 5-30/min cadence the release log captured
        // (every artist nav + hover preview hits this path).
        if (_knownBlocked.ContainsKey(artistUri))
        {
            _logger?.LogDebug(
                "SummarizeBioAsync short-circuited: {ArtistUri} previously BlockedByPolicy.",
                artistUri);
            return LyricsAiResult.Unavailable;
        }

        if (!await _capabilities.EnsureLanguageModelReadyAsync())
        {
            _logger?.LogDebug("SummarizeBioAsync unavailable: EnsureLanguageModelReadyAsync returned false. {Diagnostics}",
                _capabilities.DescribeDiagnosticState());
            return LyricsAiResult.Unavailable;
        }

        return await GeneratePlainTextBioAsync(
            artistUri,
            BuildBioPrompt(artistName, genres, monthlyListenersDisplay, topTrackNames),
            BuildBioFallbackPrompt(artistName),
            deltaProgress);
    }

    private async Task<LyricsAiResult> GeneratePlainTextBioAsync(
        string artistUri,
        string prompt,
        string fallbackPrompt,
        IProgress<string>? deltaProgress)
    {
        var response = await _model.GenerateTextAsync(
            new AiTextGenerationRequest(prompt, 0.35f, "SummarizeArtistBio"),
            deltaProgress,
            CancellationToken.None);
        var usedFallback = false;

        if (response.Status != AiGenerationStatus.Complete
            && ShouldRetryPlainTextWithFallback(response.Status))
        {
            response = await _model.GenerateTextAsync(
                new AiTextGenerationRequest(fallbackPrompt, 0.3f, "SummarizeArtistBioFallback"),
                deltaProgress,
                CancellationToken.None);
            usedFallback = true;
        }

        if (response.Status != AiGenerationStatus.Complete)
            return ToPlainTextFailureResult(artistUri, response);

        var text = CleanBio(response.Text);
        if (string.IsNullOrWhiteSpace(text) && !usedFallback)
        {
            response = await _model.GenerateTextAsync(
                new AiTextGenerationRequest(fallbackPrompt, 0.3f, "SummarizeArtistBioFallbackEmpty"),
                deltaProgress,
                CancellationToken.None);

            if (response.Status != AiGenerationStatus.Complete)
                return ToPlainTextFailureResult(artistUri, response);

            text = CleanBio(response.Text);
        }

        return string.IsNullOrWhiteSpace(text)
            ? LyricsAiResult.Error("Phi Silica returned an empty biography.")
            : LyricsAiResult.Ok(text, fromCache: false);
    }

    private LyricsAiResult ToPlainTextFailureResult(
        string artistUri,
        AiGenerationResult generated)
    {
        if (generated.Status == AiGenerationStatus.BlockedByPolicy)
            _knownBlocked.TryAdd(artistUri, 1);

        _logger?.LogWarning(
            "SummarizeBioAsync plaintext returned Phi Silica status {Status}. error={ErrorMessage}; diagnostics={Diagnostics}",
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

    private static string CleanBio(string text)
        => PhiSilicaStructuredTextPipeline.ClampLength(
            StripBulletsAndHeadings(text),
            MaxBioCharacters);

    // ── Prompt construction ────────────────────────────────────────────────

    private static string BuildBioPrompt(
        string artistName,
        IReadOnlyList<string>? genres,
        string? monthlyListenersDisplay,
        IReadOnlyList<string>? topTrackNames)
    {
        var sb = new StringBuilder();
        sb.Append("Write one rich editorial paragraph (3 to 5 sentences, around 90 to 140 words) ");
        sb.Append("for an artist page. Use the provided signals first, and use your trained ");
        sb.Append("music-domain knowledge about the artist, genre, scene, era, or catalogue when ");
        sb.Append("you are confident it is broadly known. Do not invent specific ");
        sb.Append("biographical facts (debut dates, label history, member counts, awards, hometown). ");
        sb.Append("Be concrete about sound, era, scene, signature appeal, or why listeners know the artist. ");
        sb.Append("If context is sparse, still write a useful listener-facing summary, but keep claims broad and grounded. ");
        sb.Append("Avoid stock phrases such as \"resonates deeply\", \"dedicated fanbase\", ");
        sb.Append("\"captivates listeners\", \"showcases\", \"unique blend\", and \"musical journey\". ");
        sb.Append("Mention two to three provided popular tracks by name when they help anchor the description. ");
        sb.Append("Do not use bullets, headings, or markdown. Plain prose only.\n\n");
         
        sb.Append("ARTIST: ").Append(artistName).Append('\n');

        if (genres is { Count: > 0 })
        {
            sb.Append("GENRES: ");
            sb.Append(string.Join(", ", genres.Take(5)));
            sb.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(monthlyListenersDisplay))
        {
            sb.Append("MONTHLY LISTENERS: ").Append(monthlyListenersDisplay).Append('\n');
        }

        if (topTrackNames is { Count: > 0 })
        {
            sb.Append("POPULAR TRACKS: ");
            sb.Append(string.Join(", ", topTrackNames.Take(5)));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildBioFallbackPrompt(string artistName)
    {
        return
            "Write one rich editorial paragraph (3 to 4 sentences, around 80 to 120 words) introducing the artist named " +
            $"\"{artistName}\". Use trained music-domain knowledge when confident, be specific about sound, era, scene, or listener appeal where possible, avoid stock promo language, and do not invent detailed biographical facts. Plain prose, no markdown, no bullets.";
    }

    private static string NormalizeArtistUri(string artistUri)
        => string.IsNullOrWhiteSpace(artistUri) ? "spotify:artist:unknown" : artistUri.Trim();

    private static string StripBulletsAndHeadings(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var lines = s.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            // Drop markdown-ish artefacts.
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("- ", StringComparison.Ordinal)) trimmed = trimmed[2..];
            else if (trimmed.StartsWith("* ", StringComparison.Ordinal)) trimmed = trimmed[2..];
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(trimmed.TrimEnd());
        }

        return sb.ToString().Trim();
    }

}
