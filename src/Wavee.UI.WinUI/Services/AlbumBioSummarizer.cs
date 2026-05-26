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
/// Generates a short on-device "About this album" paragraph for the album page.
/// Mirrors the <see cref="ArtistBioSummarizer"/> pattern: gates through
/// <see cref="AiCapabilities"/>, uses plain-text Phi Silica streaming, caches
/// per album URI, and grounds output with music-specific metadata snippets
/// alongside the supplied Spotify metadata (tracklist, release year, label,
/// total duration). If no album-specific external source is found, the caller
/// hides the card instead of asking the model to improvise.
/// </summary>
public sealed class AlbumBioSummarizer
{
    private readonly AiCapabilities _capabilities;
    private readonly ILanguageModelClient _model;
    private readonly IWebSearchToolProvider? _webSearch;
    private readonly IWikipediaLookup? _wikipedia;
    private readonly IMusicGroundingProvider? _musicGrounding;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<LyricsAiResult>>> _requests =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _knownBlocked =
        new(StringComparer.Ordinal);

    /// <summary>~90–140 words plus punctuation/headroom.</summary>
    private const int MaxAlbumBioCharacters = 1300;

    public AlbumBioSummarizer(
        AiCapabilities capabilities,
        ILanguageModelClient model,
        IMusicGroundingProvider? musicGrounding = null,
        IWebSearchToolProvider? webSearch = null,
        IWikipediaLookup? wikipedia = null,
        ILogger<AlbumBioSummarizer>? logger = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _musicGrounding = musicGrounding;
        _webSearch = webSearch;
        _wikipedia = wikipedia;
        _logger = logger;
    }

    /// <summary>
    /// Asks Phi Silica for a short editorial paragraph about the album. Cached
    /// in-memory per <paramref name="albumUri"/>; concurrent callers share one
    /// in-flight Lazy task. Music grounding feeds the prompt as supporting
    /// evidence behind the supplied tracklist + label.
    /// </summary>
    public Task<LyricsAiResult> SummarizeAlbumAsync(
        string albumUri,
        string albumTitle,
        string? artistName = null,
        int? releaseYear = null,
        IReadOnlyList<string>? trackTitles = null,
        string? label = null,
        string? totalDurationDisplay = null,
        IProgress<string>? deltaProgress = null,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsAlbumBioSummarizeEnabled)
        {
            _logger?.LogDebug("SummarizeAlbumAsync gated off. {Diagnostics}",
                _capabilities.DescribeDiagnosticState());
            return Task.FromResult(LyricsAiResult.Unavailable);
        }
        if (string.IsNullOrWhiteSpace(albumTitle))
            return Task.FromResult(LyricsAiResult.Empty);

        var key = NormalizeAlbumUri(albumUri);
        var created = new Lazy<Task<LyricsAiResult>>(
            () => SummarizeAlbumCoreAsync(key, albumTitle, artistName, releaseYear, trackTitles, label, totalDurationDisplay, deltaProgress),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var request = _requests.GetOrAdd(key, created);
        var fromExistingRequest = !ReferenceEquals(request, created);

        return AwaitRequestAsync(request, key, fromExistingRequest, ct);
    }

    public bool TryGetCached(string albumUri, out LyricsAiResult result)
    {
        result = default;
        var key = NormalizeAlbumUri(albumUri);
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
            "SummarizeAlbumAsync",
            _logger,
            ct);

    private async Task<LyricsAiResult> SummarizeAlbumCoreAsync(
        string albumUri,
        string albumTitle,
        string? artistName,
        int? releaseYear,
        IReadOnlyList<string>? trackTitles,
        string? label,
        string? totalDurationDisplay,
        IProgress<string>? deltaProgress)
    {
        if (_knownBlocked.ContainsKey(albumUri))
        {
            _logger?.LogDebug(
                "SummarizeAlbumAsync short-circuited: {AlbumUri} previously BlockedByPolicy.",
                albumUri);
            return LyricsAiResult.Unavailable;
        }

        var grounding = await FetchMusicGroundingAsync(albumTitle, artistName).ConfigureAwait(false);
        if (!HasStrongAlbumGrounding(albumTitle, artistName, grounding))
        {
            _logger?.LogDebug(
                "SummarizeAlbumAsync skipped for {Artist}/{Album}: no album-specific external grounding.",
                artistName,
                albumTitle);
            return LyricsAiResult.UnavailableWithReason("insufficient_grounding");
        }

        if (!await _capabilities.EnsureLanguageModelReadyAsync())
        {
            _logger?.LogDebug("SummarizeAlbumAsync unavailable: EnsureLanguageModelReadyAsync returned false. {Diagnostics}",
                _capabilities.DescribeDiagnosticState());
            return LyricsAiResult.Unavailable;
        }

        return await GeneratePlainTextAlbumBioAsync(
            albumUri,
            BuildAlbumBioPrompt(albumTitle, artistName, releaseYear, trackTitles, label, totalDurationDisplay, grounding),
            BuildAlbumBioFallbackPrompt(albumTitle, artistName, releaseYear, grounding),
            grounding.Sources,
            deltaProgress);
    }

    private async Task<MusicGroundingResult> FetchMusicGroundingAsync(string albumTitle, string? artistName)
    {
        if (_musicGrounding?.IsAvailable == true)
        {
            try
            {
                return await _musicGrounding.GetGroundingAsync(
                        new MusicGroundingRequest(
                            MusicGroundingKind.Album,
                            ArtistName: artistName,
                            AlbumTitle: albumTitle,
                            MaxSources: 5))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Album music grounding failed for {Artist}/{Album}", artistName, albumTitle);
            }
        }

        var webResults = await FetchWebGroundingAsync(albumTitle, artistName).ConfigureAwait(false);
        var sources = webResults
            .Where(r => !string.IsNullOrWhiteSpace(r.Title) && !string.IsNullOrWhiteSpace(r.Url))
            .Where(r => IsAlbumSpecificSource(albumTitle, artistName, r.Title, r.Snippet, r.Url))
            .Take(5)
            .Select(r => new MusicGroundingSource(
                r.Title,
                r.Url,
                r.Snippet,
                r.Source ?? SourceNameFromUrl(r.Url),
                MusicGroundingKind.Album,
                IsMusicSpecific: true,
                Reliability: 0.5))
            .ToList();

        if (sources.Count == 0)
        {
            var wikipedia = await FetchWikipediaAsync(albumTitle, artistName).ConfigureAwait(false);
            if (wikipedia is { } wiki && !string.IsNullOrWhiteSpace(wiki.Extract) && !string.IsNullOrWhiteSpace(wiki.Url))
            {
                sources.Add(new MusicGroundingSource(
                    wiki.Title,
                    wiki.Url!,
                    TrimForPrompt(wiki.Extract, 320),
                    "Wikipedia",
                    MusicGroundingKind.Album,
                    IsMusicSpecific: false,
                    Reliability: 0.45));
            }
        }

        return sources.Count == 0 ? MusicGroundingResult.Empty : new MusicGroundingResult(sources);
    }

    private async Task<IReadOnlyList<WebSearchResult>> FetchWebGroundingAsync(string albumTitle, string? artistName)
    {
        if (_webSearch is null || !_webSearch.IsAvailable)
            return [];

        var query = string.IsNullOrWhiteSpace(artistName)
            ? $"\"{albumTitle}\" album review music"
            : $"{artistName} \"{albumTitle}\" album review music";

        try
        {
            return await _webSearch
                .SearchAsync(query, new WebSearchOptions(MaxResults: 5))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Album bio web grounding failed for query {Query}", query);
            return [];
        }
    }

    private async Task<WikipediaSummary?> FetchWikipediaAsync(string albumTitle, string? artistName)
    {
        if (_wikipedia is null)
            return null;

        try
        {
            return await _wikipedia.LookupAlbumAsync(albumTitle, artistName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Wikipedia lookup failed for album {Album}", albumTitle);
            return null;
        }
    }

    private async Task<LyricsAiResult> GeneratePlainTextAlbumBioAsync(
        string albumUri,
        string prompt,
        string fallbackPrompt,
        IReadOnlyList<MusicGroundingSource> sources,
        IProgress<string>? deltaProgress)
    {
        var response = await _model.GenerateTextAsync(
            new AiTextGenerationRequest(prompt, 0.35f, "SummarizeAlbumBio"),
            deltaProgress,
            CancellationToken.None);
        var usedFallback = false;

        if (response.Status != AiGenerationStatus.Complete
            && ShouldRetryPlainTextWithFallback(response.Status))
        {
            response = await _model.GenerateTextAsync(
                new AiTextGenerationRequest(fallbackPrompt, 0.3f, "SummarizeAlbumBioFallback"),
                deltaProgress,
                CancellationToken.None);
            usedFallback = true;
        }

        if (response.Status != AiGenerationStatus.Complete)
            return ToPlainTextFailureResult(albumUri, response);

        var text = CleanBio(response.Text);
        if ((string.IsNullOrWhiteSpace(text) || AiGeneratedTextGuard.IsInvalidGeneratedText(text)) && !usedFallback)
        {
            response = await _model.GenerateTextAsync(
                new AiTextGenerationRequest(fallbackPrompt, 0.3f, "SummarizeAlbumBioFallbackEmpty"),
                deltaProgress,
                CancellationToken.None);

            if (response.Status != AiGenerationStatus.Complete)
                return ToPlainTextFailureResult(albumUri, response);

            text = CleanBio(response.Text);
        }

        if (AiGeneratedTextGuard.IsInvalidGeneratedText(text))
            return LyricsAiResult.UnavailableWithReason("invalid_generation");

        return string.IsNullOrWhiteSpace(text)
            ? LyricsAiResult.Error("Phi Silica returned an empty album summary.")
            : LyricsAiResult.Ok(text, fromCache: false, sources);
    }

    private LyricsAiResult ToPlainTextFailureResult(string albumUri, AiGenerationResult generated)
    {
        if (generated.Status == AiGenerationStatus.BlockedByPolicy)
            _knownBlocked.TryAdd(albumUri, 1);

        _logger?.LogWarning(
            "SummarizeAlbumAsync plaintext returned Phi Silica status {Status}. error={ErrorMessage}; diagnostics={Diagnostics}",
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
            MaxAlbumBioCharacters);

    // ── Prompt construction ────────────────────────────────────────────────

    private static string BuildAlbumBioPrompt(
        string albumTitle,
        string? artistName,
        int? releaseYear,
        IReadOnlyList<string>? trackTitles,
        string? label,
        string? totalDurationDisplay,
        MusicGroundingResult grounding)
    {
        var sb = new StringBuilder();
        sb.Append("Write one factual album-page paragraph (2 to 3 sentences, around 50 to 90 words). ");
        sb.Append("Treat the supplied evidence hierarchically: ");
        sb.Append("ALBUM_FACTS (title, artist, year, tracklist, label) are authoritative. ");
        sb.Append("MUSIC_GROUNDING is supporting external metadata; use it only when it is clearly about this same album. ");
        sb.Append("Trained music-domain knowledge is last and only when broadly known. ");
        sb.Append("If the evidence is not enough to write the paragraph, output exactly NO_ALBUM_SUMMARY and nothing else. ");
        sb.Append("Never mention the prompt, instructions, evidence names, missing Spotify/Wikipedia/web data, or inability to answer. ");
        sb.Append("Do not invent producers, recording dates, chart positions, sales numbers, scenes, influence, critical reception, or release impact. ");
        sb.Append("Do not call the album acclaimed, influential, experimental, singular, important, or documented unless the supplied evidence says so. ");
        sb.Append("If evidence is thin, stay terse and descriptive instead of writing a review. ");
        sb.Append("Avoid stock phrases such as \"resonates deeply\", \"captivates listeners\", \"unique blend\", \"musical journey\", \"sonic exploration\", and \"stands as\". ");
        sb.Append("When naming songs from the album, prefer titles from the TRACKLIST below — those are the album's actual tracks. ");
        sb.Append("Mention at most two track titles by name in straight double quotes (e.g. \"Track Name\") so they can be cross-referenced. ");
        sb.Append("Do not invent track names that don't appear in the supplied tracklist. ");
        sb.Append("Do not use bullets, headings, or markdown. Plain prose only.\n\n");

        sb.Append("ALBUM_FACTS:\n");
        sb.Append("Title: ").AppendLine(albumTitle);
        if (!string.IsNullOrWhiteSpace(artistName))
            sb.Append("Artist: ").AppendLine(artistName);
        if (releaseYear is { } year && year > 0)
            sb.Append("Year: ").Append(year).AppendLine();
        if (!string.IsNullOrWhiteSpace(label))
            sb.Append("Label: ").AppendLine(label);
        if (!string.IsNullOrWhiteSpace(totalDurationDisplay))
            sb.Append("Total duration: ").AppendLine(totalDurationDisplay);
        sb.AppendLine();

        if (trackTitles is { Count: > 0 })
        {
            sb.AppendLine("TRACKLIST (album's actual tracks — quote these by name):");
            foreach (var title in trackTitles.Take(30))
                sb.Append("- \"").Append(title).Append("\"\n");
            sb.AppendLine();
        }

        AppendMusicGroundingBlock(sb, grounding.Sources);

        return sb.ToString();
    }

    private static string BuildAlbumBioFallbackPrompt(
        string albumTitle,
        string? artistName,
        int? releaseYear,
        MusicGroundingResult grounding)
    {
        var sb = new StringBuilder();
        sb.Append("Write one factual paragraph (2 sentences, around 45 to 75 words) introducing the album \"");
        sb.Append(albumTitle);
        if (!string.IsNullOrWhiteSpace(artistName))
            sb.Append("\" by ").Append(artistName);
        if (releaseYear is { } year && year > 0)
            sb.Append(" (").Append(year).Append(")");
        sb.Append(". Use only supplied evidence first (album facts > music grounding), then broad music-domain knowledge for genre context only. ");
        sb.Append("If there is not enough evidence, output exactly NO_ALBUM_SUMMARY and nothing else. ");
        sb.Append("Never mention the prompt, instructions, missing evidence, Spotify biography, Wikipedia, web results, or inability to answer. ");
        sb.Append("Avoid review language, hype, reception claims, and invented facts. ");
        sb.Append("Plain prose, no markdown, no bullets.\n\n");

        AppendMusicGroundingBlock(sb, grounding.Sources);

        return sb.ToString();
    }

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

    private static bool HasStrongAlbumGrounding(
        string albumTitle,
        string? artistName,
        MusicGroundingResult grounding)
    {
        if (grounding.Sources.Count == 0)
            return false;

        return grounding.Sources.Any(source =>
            (source.IsMusicSpecific || string.Equals(source.SourceName, "Wikipedia", StringComparison.OrdinalIgnoreCase))
            && source.Reliability >= 0.45
            && IsAlbumSpecificSource(albumTitle, artistName, source.Title, source.Snippet, source.Url));
    }

    private static bool IsAlbumSpecificSource(
        string albumTitle,
        string? artistName,
        string? title,
        string? snippet,
        string? url)
    {
        var haystack = NormalizeForMatch(string.Join(" ", title, snippet, url));
        if (string.IsNullOrEmpty(haystack))
            return false;

        var album = NormalizeForMatch(albumTitle);
        var artist = NormalizeForMatch(artistName);
        if (string.IsNullOrEmpty(album) || !haystack.Contains(album, StringComparison.Ordinal))
            return false;

        return string.IsNullOrEmpty(artist) || haystack.Contains(artist, StringComparison.Ordinal);
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

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        var previousSpace = false;
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                previousSpace = false;
            }
            else if (!previousSpace)
            {
                sb.Append(' ');
                previousSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeAlbumUri(string albumUri)
        => string.IsNullOrWhiteSpace(albumUri) ? "spotify:album:unknown" : albumUri.Trim();

    private static string StripBulletsAndHeadings(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var lines = s.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
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
