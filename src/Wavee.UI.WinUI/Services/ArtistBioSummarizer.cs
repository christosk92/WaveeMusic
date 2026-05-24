using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Generates a short on-device biography for an artist whose ArtistOverview
/// payload has no biography text. Mirrors <see cref="LyricsAiService"/>'s
/// Phi Silica bridging pattern: gates through <see cref="AiCapabilities"/>,
/// uses the shared structured-response helper so a missing AI projection collapses
/// to <see cref="LyricsAiResult.Unavailable"/>
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

    // Per-session cache of artist URIs that previously returned BlockedByPolicy
    // from Phi Silica. Next call for the same URI short-circuits — skips the
    // prompt build and the Phi Silica round-trip entirely. byte value is arbitrary;
    // ConcurrentDictionary doesn't have a Set primitive. Cleared by ClearCache.
    private readonly ConcurrentDictionary<string, byte> _knownBlocked =
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
        // Structured generation does not stream progress: intermediate deltas
        // may be partial JSON rather than final biography text.
        var created = new Lazy<Task<LyricsAiResult>>(
            () => SummarizeBioCoreAsync(key, artistName, genres, monthlyListenersDisplay, topTrackNames),
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
        IReadOnlyList<string>? topTrackNames)
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

        return await PhiSilicaStructuredTextPipeline.GenerateAsync(
            new PhiSilicaStructuredTextRequest(
                "SummarizeBioAsync",
                BuildBioPrompt(artistName, genres, monthlyListenersDisplay, topTrackNames),
                BuildBioFallbackPrompt(artistName),
                0.35f,
                text => PhiSilicaStructuredTextPipeline.ClampLength(
                    StripBulletsAndHeadings(text),
                    MaxBioCharacters),
                "Phi Silica returned an empty biography.")
            {
                ObserveTerminalStatus = status =>
                {
                    if (status == PhiSilicaStructuredGenerationStatus.BlockedByPolicy)
                        _knownBlocked.TryAdd(artistUri, 1);
                },
            },
            _logger,
            CancellationToken.None);
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
