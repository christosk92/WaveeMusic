using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Generates a short on-device biography for an artist whose ArtistOverview
/// payload has no biography text. Mirrors <see cref="LyricsAiService"/>'s
/// Phi Silica bridging pattern: gates through <see cref="AiCapabilities"/>,
/// isolates Microsoft.Windows.AI.* references behind a <see cref="MethodImplOptions.NoInlining"/>
/// helper so a missing AI projection collapses to <see cref="LyricsAiResult.Unavailable"/>
/// instead of throwing at JIT time, and reuses <see cref="LyricsAiResult"/> as
/// the typed result envelope.
///
/// The prompt is intentionally conservative — it gets the artist name, optional
/// known signals (genres, monthly listeners, top track names), and asks for one
/// neutral ~80-word paragraph. No quotes, no markdown, no bullets. We do NOT
/// claim biographical facts we can't ground in the input signals; the model is
/// instructed to characterise the artist's sound and prominence, not invent
/// label history or member counts.
/// </summary>
public sealed class ArtistBioSummarizer
{
    private readonly AiCapabilities _capabilities;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<LyricsAiResult>>> _requests =
        new(StringComparer.Ordinal);

    /// <summary>~80–120 words ≈ 720 characters cap with headroom.</summary>
    private const int MaxBioCharacters = 900;

    public ArtistBioSummarizer(AiCapabilities capabilities, ILogger<ArtistBioSummarizer>? logger = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _logger = logger;
    }

    /// <summary>
    /// Asks Phi Silica for an ~80-word neutral biography of the artist. Cached
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

    public void ClearCache() => _requests.Clear();

    private async Task<LyricsAiResult> AwaitRequestAsync(
        Lazy<Task<LyricsAiResult>> request, string key, bool fromExistingRequest, CancellationToken ct)
    {
        try
        {
            var result = await request.Value.WaitAsync(ct);
            if (result.Kind != LyricsAiResultKind.Ok)
                _requests.TryRemove(key, out _);

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
            _requests.TryRemove(key, out _);
            _logger?.LogWarning(ex, "SummarizeBioAsync hit TypeLoadException — AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (FileNotFoundException ex)
        {
            _requests.TryRemove(key, out _);
            _logger?.LogWarning(ex, "SummarizeBioAsync hit FileNotFoundException — AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (UnauthorizedAccessException ex)
        {
            _requests.TryRemove(key, out _);
            _logger?.LogWarning(ex, "SummarizeBioAsync hit UnauthorizedAccessException; Windows denied access to the LanguageModel limited access feature.");
            return LyricsAiResult.Unavailable;
        }
        catch (Exception ex)
        {
            _requests.TryRemove(key, out _);
            _logger?.LogWarning(ex, "SummarizeBioAsync failed for {ArtistUri}.", key);
            return LyricsAiResult.Error(ex.Message);
        }
    }

    private async Task<LyricsAiResult> SummarizeBioCoreAsync(
        string artistUri,
        string artistName,
        IReadOnlyList<string>? genres,
        string? monthlyListenersDisplay,
        IReadOnlyList<string>? topTrackNames,
        IProgress<string>? deltaProgress)
    {
        try
        {
            if (!await _capabilities.EnsureLanguageModelReadyAsync())
            {
                _logger?.LogDebug("SummarizeBioAsync unavailable: EnsureLanguageModelReadyAsync returned false. {Diagnostics}",
                    _capabilities.DescribeDiagnosticState());
                return LyricsAiResult.Unavailable;
            }

            var prompt = BuildBioPrompt(artistName, genres, monthlyListenersDisplay, topTrackNames);
            var response = await GenerateAsync(prompt, deltaProgress, CancellationToken.None);
            if (ShouldRetryWithCompactPrompt(response))
            {
                _logger?.LogInformation(
                    "SummarizeBioAsync retrying with compact prompt after Phi Silica status {Status}.",
                    response.Status);
                response = await GenerateAsync(BuildBioFallbackPrompt(artistName), deltaProgress, CancellationToken.None);
            }

            if (response.Status != LanguageModelGeneratedTextStatus.Complete)
                return ToFailureResult(response, "SummarizeBioAsync");

            var stripped = StripBulletsAndHeadings(response.Text);
            if (string.IsNullOrWhiteSpace(stripped))
            {
                _logger?.LogInformation("SummarizeBioAsync retrying after Phi Silica returned no usable text.");
                response = await GenerateAsync(BuildBioFallbackPrompt(artistName), deltaProgress, CancellationToken.None);
                if (response.Status != LanguageModelGeneratedTextStatus.Complete)
                    return ToFailureResult(response, "SummarizeBioAsync fallback");

                stripped = StripBulletsAndHeadings(response.Text);
            }

            if (string.IsNullOrWhiteSpace(stripped))
                return LyricsAiResult.Error("Phi Silica returned an empty biography.");

            var trimmed = ClampLength(stripped, MaxBioCharacters);
            return LyricsAiResult.Ok(trimmed, fromCache: false);
        }
        catch (TypeLoadException ex)
        {
            _logger?.LogWarning(ex, "SummarizeBioAsync hit TypeLoadException — AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogWarning(ex, "SummarizeBioAsync hit FileNotFoundException — AI projection assembly missing at runtime.");
            return LyricsAiResult.Unavailable;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "SummarizeBioAsync hit UnauthorizedAccessException; Windows denied access to the LanguageModel limited access feature.");
            return LyricsAiResult.Unavailable;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SummarizeBioAsync failed for {ArtistUri}.", artistUri);
            return LyricsAiResult.Error(ex.Message);
        }
    }

    // ── Prompt construction ────────────────────────────────────────────────

    private static string BuildBioPrompt(
        string artistName,
        IReadOnlyList<string>? genres,
        string? monthlyListenersDisplay,
        IReadOnlyList<string>? topTrackNames)
    {
        var sb = new StringBuilder();
        sb.Append("Write one neutral paragraph (3 to 5 sentences, around 70 to 100 words) ");
        sb.Append("introducing the artist below. Use only the signals provided — do not invent ");
        sb.Append("biographical facts (debut dates, label history, member counts, awards, hometown). ");
        sb.Append("Characterise the artist's musical style and prominence. ");
        sb.Append("Do not quote song titles or repeat the data verbatim — synthesise. ");
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
            "Write one short neutral paragraph (2 to 3 sentences) introducing the artist named " +
            $"\"{artistName}\". Do not invent biographical facts. Plain prose, no markdown, no bullets.";
    }

    private static string NormalizeArtistUri(string artistUri)
        => string.IsNullOrWhiteSpace(artistUri) ? "spotify:artist:unknown" : artistUri.Trim();

    // ── Phi Silica bridge (JIT-isolated, identical pattern to LyricsAiService) ──

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<LanguageModelGeneratedText> GenerateAsync(
        string prompt,
        IProgress<string>? deltaProgress,
        CancellationToken ct)
    {
        using var languageModel = await Microsoft.Windows.AI.Text.LanguageModel.CreateAsync();

        var op = TryBuildOptions() is { } opts
            ? languageModel.GenerateResponseAsync(prompt, opts)
            : languageModel.GenerateResponseAsync(prompt);

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
            try { op.Cancel(); } catch { /* op may have completed */ }
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
                Temperature = 0.35f,
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
