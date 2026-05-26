using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.AI.Generation;
using Wavee.AI.Tools;

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
    private readonly IWebSearchToolProvider? _webSearch;
    private readonly IWikipediaLookup? _wikipedia;
    private readonly IMusicGroundingProvider? _musicGrounding;
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
        IMusicGroundingProvider? musicGrounding = null,
        IWebSearchToolProvider? webSearch = null,
        IWikipediaLookup? wikipedia = null,
        ILogger<ArtistBioSummarizer>? logger = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _musicGrounding = musicGrounding;
        _webSearch = webSearch;
        _wikipedia = wikipedia;
        _logger = logger;
    }

    /// <summary>
    /// Asks Phi Silica for a fuller neutral biography of the artist. Cached
    /// in-memory per <paramref name="artistUri"/>; concurrent callers share the
    /// same in-flight Lazy task. Grounded with the Spotify biography (when
    /// available) and music-specific metadata snippets; Wikipedia is only a
    /// fallback inside the grounding provider.
    /// </summary>
    public Task<LyricsAiResult> SummarizeBioAsync(
        string artistUri,
        string artistName,
        IReadOnlyList<string>? genres = null,
        string? monthlyListenersDisplay = null,
        IReadOnlyList<string>? topTrackNames = null,
        string? spotifyBiography = null,
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
            () => SummarizeBioCoreAsync(key, artistName, genres, monthlyListenersDisplay, topTrackNames, spotifyBiography, deltaProgress),
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

        result = cached.WithCacheState(fromCache: true);
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
        string? spotifyBiography,
        IProgress<string>? deltaProgress)
    {
        // Short-circuit: if a previous call for this artist URI came back
        // BlockedByPolicy from Phi Silica, skip the entire prompt-build +
        // Phi Silica round-trip on subsequent calls.
        if (_knownBlocked.ContainsKey(artistUri))
        {
            _logger?.LogDebug(
                "SummarizeBioAsync short-circuited: {ArtistUri} previously BlockedByPolicy.",
                artistUri);
            return LyricsAiResult.Unavailable;
        }

        // Kick off external grounding in parallel with the Phi Silica readiness
        // check. Both calls catch their own failures and degrade to null/empty
        // rather than blocking the AI surface.
        var groundingTask = FetchMusicGroundingAsync(artistName);

        var grounding = await groundingTask.ConfigureAwait(false);
        if (!HasUsableBiographyEvidence(spotifyBiography, grounding))
        {
            _logger?.LogDebug(
                "SummarizeBioAsync skipped for {ArtistName}: insufficient grounded artist evidence.",
                artistName);
            return LyricsAiResult.UnavailableWithReason("insufficient_grounding");
        }

        if (!await _capabilities.EnsureLanguageModelReadyAsync())
        {
            _logger?.LogDebug("SummarizeBioAsync unavailable: EnsureLanguageModelReadyAsync returned false. {Diagnostics}",
                _capabilities.DescribeDiagnosticState());
            return LyricsAiResult.Unavailable;
        }

        return await GeneratePlainTextBioAsync(
            artistUri,
            artistName,
            BuildBioPrompt(artistName, genres, monthlyListenersDisplay, topTrackNames, spotifyBiography, grounding),
            BuildBioFallbackPrompt(artistName, topTrackNames, spotifyBiography, grounding),
            spotifyBiography,
            topTrackNames,
            grounding.Sources,
            deltaProgress);
    }

    private async Task<MusicGroundingResult> FetchMusicGroundingAsync(string artistName)
    {
        if (_musicGrounding?.IsAvailable == true)
        {
            try
            {
                return await _musicGrounding.GetGroundingAsync(
                        new MusicGroundingRequest(
                            MusicGroundingKind.Artist,
                            ArtistName: artistName,
                            MaxSources: 5))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Artist music grounding failed for {Artist}", artistName);
            }
        }

        var webResults = await FetchWebGroundingAsync(artistName).ConfigureAwait(false);
        var sources = webResults
            .Where(r => !string.IsNullOrWhiteSpace(r.Title) && !string.IsNullOrWhiteSpace(r.Url))
            .Take(5)
            .Select(r => new MusicGroundingSource(
                r.Title,
                r.Url,
                r.Snippet,
                r.Source ?? SourceNameFromUrl(r.Url),
                MusicGroundingKind.Artist,
                IsMusicSpecific: true,
                Reliability: 0.5))
            .ToList();

        if (sources.Count == 0)
        {
            var wikipedia = await FetchWikipediaAsync(artistName).ConfigureAwait(false);
            if (wikipedia is { } wiki && !string.IsNullOrWhiteSpace(wiki.Extract) && !string.IsNullOrWhiteSpace(wiki.Url))
            {
                sources.Add(new MusicGroundingSource(
                    wiki.Title,
                    wiki.Url!,
                    TrimForPrompt(wiki.Extract, 320),
                    "Wikipedia",
                    MusicGroundingKind.Artist,
                    IsMusicSpecific: false,
                    Reliability: 0.45));
            }
        }

        return sources.Count == 0 ? MusicGroundingResult.Empty : new MusicGroundingResult(sources);
    }

    private static bool HasUsableBiographyEvidence(string? spotifyBiography, MusicGroundingResult grounding)
    {
        if (!string.IsNullOrWhiteSpace(spotifyBiography) && spotifyBiography.Trim().Length >= 120)
            return true;

        foreach (var source in grounding.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Snippet) || source.Snippet.Trim().Length < 80)
                continue;

            if (source.Reliability >= 0.6)
                return true;

            if (!source.IsMusicSpecific
                && string.Equals(source.SourceName, "Wikipedia", StringComparison.OrdinalIgnoreCase)
                && source.Snippet.Trim().Length >= 160)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<WebSearchResult>> FetchWebGroundingAsync(string artistName)
    {
        if (_webSearch is null || !_webSearch.IsAvailable)
            return [];

        var query = $"{artistName} musician biography career";
        try
        {
            return await _webSearch
                .SearchAsync(query, new WebSearchOptions(MaxResults: 5))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Artist bio web grounding failed for query {Query}", query);
            return [];
        }
    }

    private async Task<WikipediaSummary?> FetchWikipediaAsync(string artistName)
    {
        if (_wikipedia is null)
            return null;

        try
        {
            return await _wikipedia.LookupArtistAsync(artistName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Wikipedia lookup failed for {Artist}", artistName);
            return null;
        }
    }

    private async Task<LyricsAiResult> GeneratePlainTextBioAsync(
        string artistUri,
        string artistName,
        string prompt,
        string fallbackPrompt,
        string? spotifyBiography,
        IReadOnlyList<string>? topTrackNames,
        IReadOnlyList<MusicGroundingSource> sources,
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
        if ((string.IsNullOrWhiteSpace(text)
             || AiGeneratedTextGuard.IsInvalidGeneratedText(text)
             || !IsGroundedInEvidence(text, artistName, spotifyBiography, topTrackNames, sources))
            && !usedFallback)
        {
            response = await _model.GenerateTextAsync(
                new AiTextGenerationRequest(fallbackPrompt, 0.3f, "SummarizeArtistBioFallbackEmpty"),
                deltaProgress,
                CancellationToken.None);

            if (response.Status != AiGenerationStatus.Complete)
                return ToPlainTextFailureResult(artistUri, response);

            text = CleanBio(response.Text);
        }

        if (AiGeneratedTextGuard.IsInvalidGeneratedText(text)
            || !IsGroundedInEvidence(text, artistName, spotifyBiography, topTrackNames, sources))
        {
            return LyricsAiResult.UnavailableWithReason("invalid_generation");
        }

        return string.IsNullOrWhiteSpace(text)
            ? LyricsAiResult.Error("Phi Silica returned an empty biography.")
            : LyricsAiResult.Ok(text, fromCache: false, sources);
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
        IReadOnlyList<string>? topTrackNames,
        string? spotifyBiography,
        MusicGroundingResult grounding)
    {
        var sb = new StringBuilder();
        sb.Append("Write one specific, grounded artist-page paragraph (3 to 5 sentences, around 90 to 140 words) ");
        sb.Append("for an artist page. Treat the supplied evidence hierarchically: ");
        sb.Append("SPOTIFY_BIOGRAPHY is authoritative — paraphrase its verifiable facts. ");
        sb.Append("MUSIC_GROUNDING is supporting music metadata and page snippets; use it only when consistent with the above. ");
        sb.Append("Trained music-domain knowledge is last and only when broadly known. ");
        sb.Append("If the evidence is not enough to write the paragraph, output exactly NO_ARTIST_SUMMARY and nothing else. ");
        sb.Append("Never mention the prompt, instructions, evidence names, missing Spotify/Wikipedia/web data, or inability to answer. ");
        sb.Append("Do not invent specific biographical facts (debut dates, label history, member counts, awards, hometown) ");
        sb.Append("unless they appear in the supplied evidence. Be concrete about sound, era, scene, signature appeal, ");
        sb.Append("or why listeners know the artist. Avoid stock promotional phrasing; tie every descriptive claim to supplied evidence. ");
        sb.Append("When naming songs, EPs, or albums, prefer titles from the supplied POPULAR_TRACKS list or the SPOTIFY_BIOGRAPHY text — those are the artist's actual catalog. ");
        sb.Append("Mention three to five such titles by name to anchor the description, written in straight double quotes (e.g. \"Track Name\") so they can be cross-referenced. ");
        sb.Append("Do not invent track or album names that don't appear in the supplied evidence. ");
        sb.Append("Do not use bullets, headings, or markdown. Plain prose only.\n\n");

        sb.Append("ARTIST: ").Append(artistName).Append('\n');

        if (!string.IsNullOrWhiteSpace(spotifyBiography))
        {
            sb.Append("SPOTIFY_BIOGRAPHY:\n");
            sb.AppendLine(TrimForPrompt(spotifyBiography!, 1400));
        }

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
            sb.Append("POPULAR_TRACKS (artist's actual catalog — quote these by name):\n");
            foreach (var title in topTrackNames.Take(20))
                sb.Append("- \"").Append(title).Append("\"\n");
        }

        AppendMusicGroundingBlock(sb, grounding.Sources);

        return sb.ToString();
    }

    private static string BuildBioFallbackPrompt(
        string artistName,
        IReadOnlyList<string>? topTrackNames,
        string? spotifyBiography,
        MusicGroundingResult grounding)
    {
        var sb = new StringBuilder();
        sb.Append("Write one specific, grounded paragraph (3 to 4 sentences, around 80 to 120 words) introducing the artist named \"");
        sb.Append(artistName);
        sb.Append("\". Use the supplied evidence first (Spotify > music grounding), then trained knowledge for broad context. ");
        sb.Append("If there is not enough evidence, output exactly NO_ARTIST_SUMMARY and nothing else. ");
        sb.Append("Never mention the prompt, instructions, missing evidence, Spotify biography, Wikipedia, web results, or inability to answer. ");
        sb.Append("Be specific about sound, era, scene, or listener appeal where possible, avoid stock promo language, and do not invent detailed biographical facts. ");
        sb.Append("Plain prose, no markdown, no bullets.\n\n");

        if (!string.IsNullOrWhiteSpace(spotifyBiography))
        {
            sb.Append("SPOTIFY_BIOGRAPHY:\n");
            sb.AppendLine(TrimForPrompt(spotifyBiography!, 1200));
        }

        if (topTrackNames is { Count: > 0 })
        {
            sb.Append("POPULAR_TRACKS (artist's actual catalog - quote these by name if useful):\n");
            foreach (var title in topTrackNames.Take(20))
                sb.Append("- \"").Append(title).Append("\"\n");
        }

        AppendMusicGroundingBlock(sb, grounding.Sources);

        return sb.ToString();
    }

    private static bool IsGroundedInEvidence(
        string? text,
        string artistName,
        string? spotifyBiography,
        IReadOnlyList<string>? topTrackNames,
        IReadOnlyList<MusicGroundingSource>? sources)
    {
        var normalizedText = NormalizeForEvidence(text);
        if (normalizedText.Length == 0)
            return false;

        if (ContainsKnownTitle(normalizedText, topTrackNames))
            return true;

        if (HasEvidenceTokenOverlap(normalizedText, spotifyBiography, artistName, requiredMatches: 4))
            return true;

        if (sources is null)
            return false;

        foreach (var source in sources)
        {
            if (HasEvidenceTokenOverlap(normalizedText, source.Title, artistName, requiredMatches: 2)
                || HasEvidenceTokenOverlap(normalizedText, source.Snippet, artistName, requiredMatches: 3))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsKnownTitle(string normalizedText, IReadOnlyList<string>? topTrackNames)
    {
        if (topTrackNames is null || topTrackNames.Count == 0)
            return false;

        var paddedText = $" {normalizedText} ";
        foreach (var title in topTrackNames)
        {
            var normalizedTitle = NormalizeForEvidence(title);
            if (normalizedTitle.Length < 4)
                continue;

            if (paddedText.Contains($" {normalizedTitle} ", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool HasEvidenceTokenOverlap(
        string normalizedText,
        string? evidence,
        string artistName,
        int requiredMatches)
    {
        var evidenceTokens = SignificantEvidenceTokens(evidence, artistName);
        if (evidenceTokens.Count == 0)
            return false;

        var paddedText = $" {normalizedText} ";
        var matches = 0;
        foreach (var token in evidenceTokens)
        {
            if (!paddedText.Contains($" {token} ", StringComparison.Ordinal))
                continue;

            matches++;
            if (matches >= requiredMatches)
                return true;
        }

        return false;
    }

    private static HashSet<string> SignificantEvidenceTokens(string? value, string artistName)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var artistTokens = NormalizeForEvidence(artistName)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var token in NormalizeForEvidence(value).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 4
                || artistTokens.Contains(token)
                || IsEvidenceStopWord(token))
            {
                continue;
            }

            tokens.Add(token);
        }

        return tokens;
    }

    private static string NormalizeForEvidence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                sb.Append(' ');
                previousWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static bool IsEvidenceStopWord(string token)
        => token is "about"
            or "across"
            or "after"
            or "album"
            or "albums"
            or "also"
            or "and"
            or "artist"
            or "artists"
            or "before"
            or "between"
            or "biography"
            or "known"
            or "listeners"
            or "music"
            or "musical"
            or "official"
            or "over"
            or "profile"
            or "release"
            or "released"
            or "song"
            or "songs"
            or "spotify"
            or "their"
            or "them"
            or "there"
            or "they"
            or "this"
            or "through"
            or "track"
            or "tracks"
            or "under"
            or "video"
            or "where"
            or "which"
            or "with"
            or "within";

    private static void AppendMusicGroundingBlock(StringBuilder sb, IReadOnlyList<MusicGroundingSource> sources)
    {
        if (sources is null || sources.Count == 0)
            return;

        var emitted = 0;
        var header = false;
        foreach (var result in sources)
        {
            if (emitted >= 5) break;
            if (string.IsNullOrWhiteSpace(result.Title)) continue;

            if (!header)
            {
                sb.Append("MUSIC_GROUNDING:\n");
                header = true;
            }

            var snippet = (result.Snippet ?? string.Empty).Trim();
            if (snippet.Length > 280) snippet = snippet[..280];

            sb.Append("- ").Append(result.Title.Trim());
            if (!string.IsNullOrWhiteSpace(snippet))
                sb.Append(" — ").Append(snippet);
            if (!string.IsNullOrWhiteSpace(result.SourceName))
                sb.Append(" (").Append(result.SourceName).Append(')');
            sb.AppendLine();
            emitted++;
        }

        if (header) sb.AppendLine();
    }

    private static string TrimForPrompt(string value, int maxCharacters)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters)
            return value;

        var trimmed = value[..maxCharacters];
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > maxCharacters / 2)
            trimmed = trimmed[..lastSpace];
        return trimmed.TrimEnd() + "...";
    }

    private static string SourceNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "Web";

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        return host switch
        {
            "musicbrainz.org" => "MusicBrainz",
            "genius.com" => "Genius",
            "musixmatch.com" => "Musixmatch",
            "discogs.com" => "Discogs",
            "allmusic.com" => "AllMusic",
            "last.fm" => "Last.fm",
            "bandcamp.com" => "Bandcamp",
            "songfacts.com" => "Songfacts",
            _ => host,
        };
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
