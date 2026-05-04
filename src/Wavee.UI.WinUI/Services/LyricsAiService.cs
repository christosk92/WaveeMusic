using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Wraps Phi Silica for the lyrics pilot:
///   - <see cref="ExplainLineAsync"/>: per-line "what does this lyric line mean?" via
///     <c>LanguageModel.GenerateResponseAsync</c> with a tightly-scoped prompt.
///   - <see cref="GetLyricsMeaningAsync"/>: whole-song lyrics meaning through
///     <c>LanguageModel.GenerateResponseAsync</c> with a short interpretive prompt.
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
/// </summary>
public sealed class LyricsAiService
{
    private readonly AiCapabilities _capabilities;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<(string trackUri, int lineIndex, string line), string> _explanationCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<LyricsAiResult>>> _lyricsMeaningRequests =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Conservative output ceiling for the per-line explanation. Phi Silica respects
    /// "in one or two sentences" reliably, but we also clamp at the API level to
    /// avoid pathological model outputs that ramble. Max ~80 tokens ≈ 320 chars.
    /// </summary>
    private const int MaxExplanationCharacters = 360;
    private const int MaxFallbackLyricsCharacters = 3200;

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

        var prompt = BuildExplainPrompt(line, lineIndex, fullLyric);

        try
        {
            var response = await GenerateAsync(prompt, deltaProgress, ct);
            if (ShouldRetryWithCompactPrompt(response))
            {
                _logger?.LogInformation(
                    "ExplainLineAsync retrying with compact quote-free prompt after Phi Silica status {Status}.",
                    response.Status);
                response = await GenerateAsync(BuildExplainFallbackPrompt(line, lineIndex, fullLyric), deltaProgress, ct);
            }

            if (response.Status != LanguageModelGeneratedTextStatus.Complete)
                return ToFailureResult(response, "ExplainLineAsync");

            var stripped = StripEvidenceLines(response.Text);
            if (string.IsNullOrWhiteSpace(stripped))
            {
                _logger?.LogInformation("ExplainLineAsync retrying after Phi Silica returned no usable explanation text.");
                response = await GenerateAsync(BuildExplainFallbackPrompt(line, lineIndex, fullLyric), deltaProgress, ct);
                if (response.Status != LanguageModelGeneratedTextStatus.Complete)
                    return ToFailureResult(response, "ExplainLineAsync fallback");

                stripped = StripEvidenceLines(response.Text);
            }

            if (string.IsNullOrWhiteSpace(stripped))
                return LyricsAiResult.Error("Phi Silica returned an empty explanation.");

            var trimmed = ClampLength(stripped, MaxExplanationCharacters);
            _explanationCache[explanationCacheKey] = trimmed;
            return LyricsAiResult.Ok(trimmed, fromCache: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TypeLoadException ex)
        {
            _logger?.LogWarning(ex, "ExplainLineAsync hit TypeLoadException — AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogWarning(ex, "ExplainLineAsync hit FileNotFoundException — AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "ExplainLineAsync hit UnauthorizedAccessException; Windows denied access to the LanguageModel limited access feature.");
            return LyricsAiResult.Unavailable;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ExplainLineAsync failed for {TrackUri}#{LineIndex}.", trackUri, lineIndex);
            return LyricsAiResult.Error(ex.Message);
        }
    }

    /// <summary>
    /// Explains the meaning of the whole lyric. Per-track requests are cached as
    /// Lazy tasks so all UI surfaces share one in-flight model call and one result.
    /// </summary>
    public async Task<LyricsAiResult> GetLyricsMeaningAsync(
        string trackUri, string fullLyric,
        IProgress<string>? deltaProgress = null,
        CancellationToken ct = default)
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
        // Only the FIRST caller's deltaProgress sees the live stream — concurrent
        // callers attaching to the same Lazy task receive the final string when
        // the underlying core call resolves. This is fine: the rare case of two
        // visible lyrics surfaces simultaneously requesting the same track's
        // meaning ends with both showing the cached final result.
        var created = new Lazy<Task<LyricsAiResult>>(
            () => GenerateLyricsMeaningCoreAsync(normalizedTrackUri, fullLyric, deltaProgress),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var request = _lyricsMeaningRequests.GetOrAdd(normalizedTrackUri, created);
        var fromExistingRequest = !ReferenceEquals(request, created);

        try
        {
            var result = await request.Value.WaitAsync(ct);
            if (result.Kind != LyricsAiResultKind.Ok)
                _lyricsMeaningRequests.TryRemove(normalizedTrackUri, out _);

            return result.Kind == LyricsAiResultKind.Ok
                ? LyricsAiResult.Ok(result.Text, fromExistingRequest || result.FromCache)
                : result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TypeLoadException ex)
        {
            _lyricsMeaningRequests.TryRemove(normalizedTrackUri, out _);
            _logger?.LogWarning(ex, "SummarizeSongAsync hit TypeLoadException — AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (FileNotFoundException ex)
        {
            _lyricsMeaningRequests.TryRemove(normalizedTrackUri, out _);
            _logger?.LogWarning(ex, "SummarizeSongAsync hit FileNotFoundException — AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (UnauthorizedAccessException ex)
        {
            _lyricsMeaningRequests.TryRemove(normalizedTrackUri, out _);
            _logger?.LogWarning(ex, "GetLyricsMeaningAsync hit UnauthorizedAccessException; Windows denied access to the LanguageModel limited access feature.");
            return LyricsAiResult.Unavailable;
        }
        catch (Exception ex)
        {
            _lyricsMeaningRequests.TryRemove(normalizedTrackUri, out _);
            _logger?.LogWarning(ex, "SummarizeSongAsync failed for {TrackUri}.", trackUri);
            return LyricsAiResult.Error(ex.Message);
        }
    }

    public Task<LyricsAiResult> SummarizeSongAsync(
        string trackUri, string fullLyric,
        IProgress<string>? deltaProgress = null,
        CancellationToken ct = default)
        => GetLyricsMeaningAsync(trackUri, fullLyric, deltaProgress, ct);

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

        result = LyricsAiResult.Ok(cached.Text, fromCache: true);
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

    // ── Prompt construction ────────────────────────────────────────────────

    // Title/artist intentionally omitted — see BuildLyricsMeaningPrompt for the why.
    private static string BuildExplainPrompt(string line, int lineIndex, string? fullLyric)
    {
        var markedLyrics = BuildMarkedLyricsContext(fullLyric, lineIndex, line);

        return
            "You interpret song lyrics as evidence. Read only the lyrics provided — " +
            "do not use outside knowledge of any song, artist, or title.\n\n" +
            "Explain the marked lyric line " +
            "(between >>> and <<<) in 2 to 4 plain sentences. Connect it to other parts " +
            "of the lyrics when the connection is supported. Name the speaker/addressee " +
            "dynamic, emotion, image, wordplay, or conflict, but do not summarize the " +
            "whole song. Do not use bullets, headings, markdown, or generic phrases like " +
            "\"emotional depth\". For Korean or other non-English lyrics, interpret the " +
            "meaning in English when possible. Do not quote or repeat lyric text verbatim; " +
            "paraphrase instead. If the marked line is too short or ambiguous even with " +
            "context, say that plainly.\n\n" +
            "EXAMPLE\n" +
            "LYRICS:\n" +
            "i wake up and i still feel tired\n" +
            ">>> the kind of tired sleep cannot fix <<<\n" +
            "i wonder if i am hard to love\n" +
            "The speaker describes an exhaustion that is emotional, not physical — sleep " +
            "does not reach it. Following the line about being hard to love, this points " +
            "to depression and self-doubt rather than a busy schedule. The marked line " +
            "names that distinction in one phrase.\n\n" +
            "LYRICS:\n" +
            markedLyrics;
    }

    private static string BuildMarkedLyricsContext(string? fullLyric, int lineIndex, string line)
    {
        if (string.IsNullOrWhiteSpace(fullLyric))
            return $">>> {line.Trim()} <<<";

        var lines = fullLyric
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        if (lineIndex >= 0 && lineIndex < lines.Length)
            lines[lineIndex] = $">>> {lines[lineIndex].Trim()} <<<";
        else
            return $">>> {line.Trim()} <<<\n\n{fullLyric.Trim()}";

        return string.Join("\n", lines).Trim();
    }

    private static string BuildExplainFallbackPrompt(string line, int lineIndex, string? fullLyric)
    {
        var markedLyrics = BuildNearbyLyricsContext(fullLyric, lineIndex, line);

        return
            "Explain the marked song lyric line in English in 1 to 2 plain sentences. " +
            "Use only the lyrics shown here. The lyrics may be Korean or another " +
            "non-English language. Do not quote, copy, romanize, or repeat any lyric text; " +
            "paraphrase only. If the meaning is unclear, say so plainly.\n\n" +
            "LYRICS:\n" +
            markedLyrics;
    }

    private static string BuildNearbyLyricsContext(string? fullLyric, int lineIndex, string line)
    {
        if (string.IsNullOrWhiteSpace(fullLyric))
            return $">>> {line.Trim()} <<<";

        var lines = fullLyric
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        if (lineIndex < 0 || lineIndex >= lines.Length)
            return $">>> {line.Trim()} <<<";

        var start = Math.Max(0, lineIndex - 4);
        var end = Math.Min(lines.Length - 1, lineIndex + 4);
        var window = new string[end - start + 1];
        for (var source = start; source <= end; source++)
        {
            var target = source - start;
            window[target] = source == lineIndex
                ? $">>> {lines[source].Trim()} <<<"
                : lines[source];
        }

        return string.Join("\n", window).Trim();
    }

    // ── Phi Silica bridges ────────────────────────────────────────────────
    //
    // Reflection-isolated like AiCapabilities — see notes there. These methods
    // are the only place we touch Microsoft.Windows.AI.Text types.

    // [MethodImpl(NoInlining)]: critical so the JIT only loads
    // Microsoft.Windows.AI.* metadata when this method is invoked (inside the
    // outer try/catch in ExplainLineAsync). Without it the JIT can inline the
    // body into the caller and lift the type-load failure out of the
    // catchable region. See AiCapabilities for the longer write-up.
    private async Task<LyricsAiResult> GenerateLyricsMeaningCoreAsync(
        string trackUri,
        string fullLyric,
        IProgress<string>? deltaProgress)
    {
        try
        {
            if (!await _capabilities.EnsureLanguageModelReadyAsync())
            {
                _logger?.LogWarning("GetLyricsMeaningAsync unavailable: EnsureLanguageModelReadyAsync returned false. {Diagnostics}",
                    _capabilities.DescribeDiagnosticState());
                return LyricsAiResult.Unavailable;
            }

            var prompt = BuildLyricsMeaningPrompt(fullLyric);
            var response = await GenerateAsync(prompt, deltaProgress, CancellationToken.None);
            if (ShouldRetryWithCompactPrompt(response))
            {
                _logger?.LogInformation(
                    "GetLyricsMeaningAsync retrying with compact quote-free prompt after Phi Silica status {Status}.",
                    response.Status);
                response = await GenerateAsync(BuildLyricsMeaningFallbackPrompt(fullLyric), deltaProgress, CancellationToken.None);
            }

            if (response.Status != LanguageModelGeneratedTextStatus.Complete)
                return ToFailureResult(response, "GetLyricsMeaningAsync");

            var stripped = StripEvidenceLines(response.Text);
            if (string.IsNullOrWhiteSpace(stripped))
            {
                _logger?.LogInformation("GetLyricsMeaningAsync retrying after Phi Silica returned no usable meaning text.");
                response = await GenerateAsync(BuildLyricsMeaningFallbackPrompt(fullLyric), deltaProgress, CancellationToken.None);
                if (response.Status != LanguageModelGeneratedTextStatus.Complete)
                    return ToFailureResult(response, "GetLyricsMeaningAsync fallback");

                stripped = StripEvidenceLines(response.Text);
            }

            if (string.IsNullOrWhiteSpace(stripped))
                return LyricsAiResult.Error("Phi Silica returned an empty lyrics meaning.");

            var normalized = NormalizeLyricsMeaningOutput(stripped);
            return LyricsAiResult.Ok(normalized.Trim(), fromCache: false);
        }
        catch (TypeLoadException ex)
        {
            _logger?.LogWarning(ex, "GetLyricsMeaningAsync hit TypeLoadException; AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogWarning(ex, "GetLyricsMeaningAsync hit FileNotFoundException; AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "GetLyricsMeaningAsync hit UnauthorizedAccessException; Windows denied access to the LanguageModel limited access feature.");
            return LyricsAiResult.Unavailable;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetLyricsMeaningAsync failed for {TrackUri}.", trackUri);
            return LyricsAiResult.Error(ex.Message);
        }
    }

    // Title/artist are intentionally NOT interpolated into the prompt: with a small
    // on-device model, the title alone biases the interpretation toward whatever
    // cliché the title evokes (e.g. "I wish u knew" → confession), short-circuiting
    // a real reading of the lyrics. The interpretation has to stand on the lyrics
    // alone. Quote-free one-shot because repeating multilingual lyrics can trip
    // Phi Silica's moderation even when the lyric is benign.
    private static string BuildLyricsMeaningPrompt(string fullLyric)
    {
        return
            "You interpret song lyrics as evidence. Read only the lyrics provided — " +
            "do not use outside knowledge of any song, artist, or title.\n\n" +
            "Write one paragraph (3 to 5 sentences, " +
            "around 80 to 120 words) describing who is speaking, to whom, what they " +
            "feel, and what they want, using only the lyrics as evidence. " +
            "Do not use bullets, numbered lists, headings, markdown, or line breaks " +
            "in the paragraph. For Korean or other non-English lyrics, interpret the " +
            "meaning in English when possible. Do not quote or repeat lyric text verbatim; " +
            "paraphrase instead.\n\n" +
            "EXAMPLE\n" +
            "LYRICS:\n" +
            "i'm so tired of hating who i am\n" +
            "nothing i do feels like enough\n" +
            "please don't go, you're the only thing keeping me here\n" +
            "The speaker is talking to someone close while struggling with self-loathing " +
            "and exhaustion. They confess that nothing they do feels worthwhile and admit " +
            "the listener is what keeps them going. The mood is desperate dependence on " +
            "another person, not romance. The song is about depression and the weight of " +
            "needing one person to stay alive.\n\n" +
            "LYRICS:\n" +
            fullLyric;
    }

    private static string BuildLyricsMeaningFallbackPrompt(string fullLyric)
    {
        return
            "Interpret these song lyrics in English in one short paragraph. Use only " +
            "the lyrics shown here. The lyrics may be Korean or another non-English " +
            "language. Do not quote, copy, romanize, or repeat any lyric text; paraphrase " +
            "only. If there is not enough understandable context, say so plainly.\n\n" +
            "LYRICS:\n" +
            TrimLyricsForFallback(fullLyric);
    }

    private static string NormalizeTrackUri(string trackUri)
        => string.IsNullOrWhiteSpace(trackUri) ? "spotify:track:unknown" : trackUri.Trim();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<LanguageModelGeneratedText> GenerateAsync(
        string prompt,
        IProgress<string>? deltaProgress,
        CancellationToken ct)
    {
        using var languageModel = await Microsoft.Windows.AI.Text.LanguageModel.CreateAsync();

        // We pass a custom ContentFilterOptions here.
        // The type's namespace location is not stable across WinAppSDK versions
        // (1.7.x and 1.8.x: Microsoft.Windows.AI.ContentSafety), and our build
        // target now ships that projection. Phi Silica's built-in defaults
        // (Medium severity for hate / sexual / violent /
        // self-harm) are too aggressive for some benign multilingual lyrics.
        // High remains inside Phi Silica's safety system while reducing false
        // positives for lyrics interpretation.
        //
        // We DO pass Temperature/TopP. Phi Silica's defaults are tuned for
        // chat-style explorativeness; for grounded lyric interpretation we
        // want literal-leaning output that follows the supplied evidence.
        // Temperature/TopP live on LanguageModelOptions itself in the same
        // assembly as LanguageModel — no extra projection dependency. The
        // try/catch is belt-and-braces in case Microsoft relocates them.
        var op = TryBuildOptions() is { } opts
            ? languageModel.GenerateResponseAsync(prompt, opts)
            : languageModel.GenerateResponseAsync(prompt);

        // op is IAsyncOperationWithProgress<LanguageModelResponseResult, string>
        // — the progress payload is the next chunk of generated text. Wiring the
        // callback here gives the UI live token-by-token streaming, matching
        // Microsoft's PhiSilicaBasic sample (AI Dev Gallery,
        // Samples/WCRAPIs/PhiSilicaBasic.xaml.cs ~line 148).
        if (deltaProgress is not null)
        {
            op.Progress = (_, delta) =>
            {
                if (!string.IsNullOrEmpty(delta))
                    deltaProgress.Report(delta);
            };
        }

        using var ctReg = ct.Register(() =>
        {
            try { op.Cancel(); } catch { /* ignore — op may have completed */ }
        });

        var result = await op;
        if (result is null)
        {
            return new LanguageModelGeneratedText(
                LanguageModelGeneratedTextStatus.Error,
                string.Empty,
                "LanguageModel returned no result.");
        }

        return new LanguageModelGeneratedText(
            result.Status switch
            {
                Microsoft.Windows.AI.Text.LanguageModelResponseStatus.Complete =>
                    LanguageModelGeneratedTextStatus.Complete,
                Microsoft.Windows.AI.Text.LanguageModelResponseStatus.InProgress =>
                    LanguageModelGeneratedTextStatus.InProgress,
                Microsoft.Windows.AI.Text.LanguageModelResponseStatus.BlockedByPolicy =>
                    LanguageModelGeneratedTextStatus.BlockedByPolicy,
                Microsoft.Windows.AI.Text.LanguageModelResponseStatus.PromptLargerThanContext =>
                    LanguageModelGeneratedTextStatus.PromptLargerThanContext,
                Microsoft.Windows.AI.Text.LanguageModelResponseStatus.PromptBlockedByContentModeration =>
                    LanguageModelGeneratedTextStatus.PromptBlockedByContentModeration,
                Microsoft.Windows.AI.Text.LanguageModelResponseStatus.ResponseBlockedByContentModeration =>
                    LanguageModelGeneratedTextStatus.ResponseBlockedByContentModeration,
                Microsoft.Windows.AI.Text.LanguageModelResponseStatus.Error =>
                    LanguageModelGeneratedTextStatus.Error,
                _ => LanguageModelGeneratedTextStatus.Error,
            },
            result.Text ?? string.Empty,
            result.ExtendedError is null
                ? null
                : $"{result.ExtendedError.GetType().Name}: 0x{result.ExtendedError.HResult:X8} {result.ExtendedError.Message}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Microsoft.Windows.AI.Text.LanguageModelOptions? TryBuildOptions()
    {
        try
        {
            return new Microsoft.Windows.AI.Text.LanguageModelOptions
            {
                Temperature = 0.3f,
                TopP = 0.9f,
                ContentFilterOptions = new Microsoft.Windows.AI.ContentSafety.ContentFilterOptions
                {
                    PromptMaxAllowedSeverityLevel = new Microsoft.Windows.AI.ContentSafety.TextContentFilterSeverity(
                        Microsoft.Windows.AI.ContentSafety.SeverityLevel.High),
                    ResponseMaxAllowedSeverityLevel = new Microsoft.Windows.AI.ContentSafety.TextContentFilterSeverity(
                        Microsoft.Windows.AI.ContentSafety.SeverityLevel.High),
                },
            };
        }
        catch (TypeLoadException) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    private LyricsAiResult ToFailureResult(LanguageModelGeneratedText generated, string operation)
    {
        _logger?.LogWarning(
            "{Operation} returned Phi Silica status {Status}. {ErrorMessage}",
            operation,
            generated.Status,
            string.IsNullOrWhiteSpace(generated.ErrorMessage) ? "<no extended error>" : generated.ErrorMessage);

        return generated.Status switch
        {
            LanguageModelGeneratedTextStatus.BlockedByPolicy => LyricsAiResult.Unavailable,
            LanguageModelGeneratedTextStatus.PromptBlockedByContentModeration => LyricsAiResult.Filtered,
            LanguageModelGeneratedTextStatus.ResponseBlockedByContentModeration => LyricsAiResult.Filtered,
            LanguageModelGeneratedTextStatus.PromptLargerThanContext =>
                LyricsAiResult.Error("Prompt exceeded Phi Silica's context window."),
            _ => LyricsAiResult.Error(generated.ErrorMessage ?? generated.Status.ToString()),
        };
    }

    private static bool ShouldRetryWithCompactPrompt(LanguageModelGeneratedText generated)
        => generated.Status is LanguageModelGeneratedTextStatus.PromptLargerThanContext
               or LanguageModelGeneratedTextStatus.PromptBlockedByContentModeration
               or LanguageModelGeneratedTextStatus.ResponseBlockedByContentModeration
           || (generated.Status == LanguageModelGeneratedTextStatus.Complete
               && string.IsNullOrWhiteSpace(generated.Text));

    private static string TrimLyricsForFallback(string fullLyric)
    {
        if (string.IsNullOrWhiteSpace(fullLyric))
            return string.Empty;

        var normalized = fullLyric.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length <= MaxFallbackLyricsCharacters)
            return normalized;

        var headLength = (int)(MaxFallbackLyricsCharacters * 0.65);
        var tailLength = MaxFallbackLyricsCharacters - headLength;
        return normalized[..headLength].TrimEnd() +
               "\n...\n" +
               normalized[^tailLength..].TrimStart();
    }

    private static string ClampLength(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s.Trim();
        var truncated = s[..max];
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > max / 2)
            truncated = truncated[..lastSpace];
        return truncated.TrimEnd('.', ',', ';', ':') + "…";
    }
    private static string NormalizeLyricsMeaningOutput(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var lines = s.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length <= 1)
            return s.Trim();

        var allBullets = true;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryStripListPrefix(lines[i], out _))
            {
                allBullets = false;
                break;
            }
        }

        if (!allBullets)
            return s.Trim();

        var normalized = string.Empty;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryStripListPrefix(lines[i], out var content) || string.IsNullOrWhiteSpace(content))
                continue;

            normalized = string.IsNullOrEmpty(normalized)
                ? content.Trim()
                : normalized + " " + content.Trim();
        }

        return normalized.Trim();
    }

    // Older prompts asked Phi Silica to emit verbatim lyric quotes prefixed
    // "EVIDENCE:" before the actual paragraph. The current prompts are
    // quote-free to avoid moderation false positives on multilingual lyrics,
    // but keep this cleanup for cached or non-compliant model output.
    private static string StripEvidenceLines(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var lines = s.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.AsSpan().TrimStart().StartsWith("EVIDENCE:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
        }

        return sb.ToString().Trim();
    }

    private static bool TryStripListPrefix(string line, out string content)
    {
        content = line.Trim();
        if (content.Length < 2)
            return false;

        if (content[0] is '-' or '*')
        {
            content = content[1..].TrimStart();
            return content.Length > 0;
        }

        var dotIndex = content.IndexOf('.');
        if (dotIndex is > 0 and <= 2 && int.TryParse(content[..dotIndex], out _))
        {
            content = content[(dotIndex + 1)..].TrimStart();
            return content.Length > 0;
        }

        return false;
    }

    private readonly record struct LanguageModelGeneratedText(
        LanguageModelGeneratedTextStatus Status,
        string Text,
        string? ErrorMessage);

    private enum LanguageModelGeneratedTextStatus
    {
        Complete,
        InProgress,
        BlockedByPolicy,
        PromptLargerThanContext,
        PromptBlockedByContentModeration,
        ResponseBlockedByContentModeration,
        Error,
    }
}

/// <summary>
/// Result envelope for <see cref="LyricsAiService"/>. Distinguishes the four
/// outcomes the UI cares about: success / not available / filtered / error.
/// </summary>
public readonly record struct LyricsAiResult(LyricsAiResultKind Kind, string Text, bool FromCache, string? ErrorMessage)
{
    public static readonly LyricsAiResult Unavailable = new(LyricsAiResultKind.Unavailable, string.Empty, false, null);
    public static readonly LyricsAiResult Empty = new(LyricsAiResultKind.Empty, string.Empty, false, null);
    public static readonly LyricsAiResult Filtered = new(LyricsAiResultKind.Filtered, string.Empty, false, null);
    public static LyricsAiResult Ok(string text, bool fromCache) => new(LyricsAiResultKind.Ok, text, fromCache, null);
    public static LyricsAiResult Error(string message) => new(LyricsAiResultKind.Error, string.Empty, false, message);

    public bool IsSuccess => Kind == LyricsAiResultKind.Ok;
}

public enum LyricsAiResultKind
{
    /// <summary>Generation succeeded (text in <see cref="LyricsAiResult.Text"/>).</summary>
    Ok,
    /// <summary>Feature gated off (no Copilot+ PC, region, or user opted out).</summary>
    Unavailable,
    /// <summary>Input was empty / whitespace.</summary>
    Empty,
    /// <summary>Content filter blocked the prompt or response.</summary>
    Filtered,
    /// <summary>Model invocation threw.</summary>
    Error,
}
