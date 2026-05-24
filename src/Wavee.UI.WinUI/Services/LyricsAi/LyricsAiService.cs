using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Wraps Phi Silica for the lyrics pilot:
///   - <see cref="ExplainLineAsync"/>: per-line "what does this lyric line mean?" via
///     structured JSON generation with a tightly-scoped prompt.
///   - <see cref="GetLyricsMeaningAsync"/>: whole-song lyrics meaning through
///     structured JSON generation with a short interpretive prompt.
///
/// All inference is on-device (NPU) via Microsoft Foundry on Windows. Calls are gated
/// through <see cref="AiCapabilities"/>: if the user hasn't opted in or the hardware
/// isn't a Copilot+ PC, every method returns <see cref="LyricsAiResult.Unavailable"/>
/// without touching the model.
///
/// Caching: in-memory only for the pilot (per-process, lost on restart). Keyed by
/// <c>(trackUri, lineIndex, line)</c> for explanations and <c>trackUri</c> for lyrics meaning.
/// TODO: persist via <c>IMetadataDatabase</c> once a cache table is added — current
/// IMetadataDatabase contract has lyrics + extended-metadata tables but no generic AI
/// blob store. Cache hit ratios should be high in a single session anyway (a user
/// re-clicking the same line replays from RAM).
///
/// Cancellation: line explanations cancel the model operation. Lyrics meaning
/// cancels only the caller's wait; the shared in-flight task keeps running so
/// another visible lyrics surface can reuse the same result instead of starting
/// a duplicate call.
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

    private readonly ConcurrentDictionary<(string trackUri, int lineIndex, string line), string> _explanationCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<LyricsAiResult>>> _lyricsMeaningRequests =
        new(StringComparer.Ordinal);

    public LyricsAiService(AiCapabilities capabilities, ILogger<LyricsAiService>? logger = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _logger = logger;
    }

    /// <summary>
    /// Asks Phi Silica to explain a single lyric line in 1-2 sentences. Returns the
    /// explanation text on success; <see cref="LyricsAiResult.Unavailable"/> if the
    /// feature is gated off; <see cref="LyricsAiResult.Filtered"/> if the model's
    /// content filter blocked the output.
    /// </summary>
    /// <param name="trackUri">Spotify URI for cache keying (e.g. "spotify:track:xyz").</param>
    /// <param name="lineIndex">Index of the line in the lyric, used as the cache key.</param>
    /// <param name="line">The lyric line to explain.</param>
    public async Task<LyricsAiResult> ExplainLineAsync(
        string trackUri, int lineIndex, string line, string? fullLyric,
        IProgress<string>? deltaProgress = null,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsLyricsExplainEnabled)
        {
            _logger?.LogWarning("ExplainLineAsync unavailable before model call. {Diagnostics}",
                _capabilities.DescribeDiagnosticState());
            return LyricsAiResult.Unavailable;
        }
        if (string.IsNullOrWhiteSpace(line))
            return LyricsAiResult.Empty;

        var normalizedTrackUri = NormalizeTrackUri(trackUri);
        var explanationCacheKey = (normalizedTrackUri, lineIndex, line);
        if (_explanationCache.TryGetValue(explanationCacheKey, out var cached))
            return LyricsAiResult.Ok(cached, fromCache: true);

        if (!await _capabilities.EnsureLanguageModelReadyAsync())
        {
            _logger?.LogWarning("ExplainLineAsync unavailable: EnsureLanguageModelReadyAsync returned false. {Diagnostics}",
                _capabilities.DescribeDiagnosticState());
            return LyricsAiResult.Unavailable;
        }

        var result = await PhiSilicaStructuredTextPipeline.GenerateAsync(
            new PhiSilicaStructuredTextRequest(
                "ExplainLineAsync",
                LyricsAiPrompts.BuildExplainPrompt(line, lineIndex, fullLyric),
                LyricsAiPrompts.BuildExplainFallbackPrompt(line, lineIndex, fullLyric),
                0.3f,
                text => PhiSilicaStructuredTextPipeline.ClampLength(
                    LyricsAiOutputNormalizer.StripEvidenceLines(text),
                    LyricsAiOutputNormalizer.MaxExplanationCharacters),
                "Phi Silica returned an empty explanation."),
            _logger,
            ct);

        if (result.Kind == LyricsAiResultKind.Ok)
            _explanationCache[explanationCacheKey] = result.Text;

        return result;
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
    /// Drops cached explanations and lyrics meanings (e.g. on logout, or as a manual reset
    /// affordance in Settings). Cheap.
    /// </summary>
    public void ClearCache()
    {
        _explanationCache.Clear();
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
                0.3f,
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
