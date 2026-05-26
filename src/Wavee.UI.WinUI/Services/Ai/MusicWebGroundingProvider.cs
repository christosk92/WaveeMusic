using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;
using Wavee.AI.Tools;

namespace Wavee.UI.WinUI.Services.Ai;

/// <summary>
/// Music-first grounding provider for the on-device AI surfaces. It prefers
/// no-key music metadata sources (MusicBrainz plus public music-page metadata)
/// and only falls back to Wikipedia when those return no useful source.
/// </summary>
internal sealed class MusicWebGroundingProvider : IMusicGroundingProvider
{
    private const string ProviderTag = "music-grounding";
    private const string UserAgent = "WaveeMusic/1.0 (https://github.com/ckara/WaveeMusic; music AI grounding)";

    private static readonly string[] AllowedMusicHosts =
    [
        "musicbrainz.org",
        "genius.com",
        "musixmatch.com",
        "discogs.com",
        "allmusic.com",
        "last.fm",
        "bandcamp.com",
        "songfacts.com",
        "officialcharts.com",
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebSearchCache _cache;
    private readonly ConfigurableWebSearchToolProvider _configurableSearch;
    private readonly DuckDuckGoLiteWebSearchProvider _fallbackSearch;
    private readonly IWikipediaLookup? _wikipedia;
    private readonly ILogger? _logger;

    public MusicWebGroundingProvider(
        IHttpClientFactory httpClientFactory,
        WebSearchCache cache,
        ConfigurableWebSearchToolProvider configurableSearch,
        DuckDuckGoLiteWebSearchProvider fallbackSearch,
        IWikipediaLookup? wikipedia = null,
        ILogger<MusicWebGroundingProvider>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _configurableSearch = configurableSearch ?? throw new ArgumentNullException(nameof(configurableSearch));
        _fallbackSearch = fallbackSearch ?? throw new ArgumentNullException(nameof(fallbackSearch));
        _wikipedia = wikipedia;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task<MusicGroundingResult> GetGroundingAsync(
        MusicGroundingRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequest(request);
        if (string.IsNullOrEmpty(normalized))
            return MusicGroundingResult.Empty;

        if (_cache.TryGet<MusicGroundingResult>(ProviderTag, normalized, out var hit))
            return Limit(hit, request.MaxSources);

        var max = Math.Clamp(request.MaxSources, 1, 8);
        var sources = new List<MusicGroundingSource>(max);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await AddMusicBrainzSourcesAsync(request, sources, seen, max, cancellationToken).ConfigureAwait(false);

        foreach (var query in BuildMusicQueries(request))
        {
            if (sources.Count >= max)
                break;

            var results = await SearchAsync(query, Math.Max(3, max), cancellationToken).ConfigureAwait(false);
            foreach (var result in results)
            {
                if (sources.Count >= max)
                    break;
                if (!TryCreateAllowedUri(result.Url, out var uri))
                    continue;
                if (!IsAllowedMusicHost(uri.Host))
                    continue;
                if (!LooksRelevant(request, result.Title, result.Snippet, uri.ToString()))
                    continue;

                var source = await FetchMetadataSourceAsync(request, result, uri, cancellationToken)
                    .ConfigureAwait(false);
                if (source is null)
                    source = FromSearchResult(request, result);
                AddSource(sources, seen, source, max);
            }
        }

        if (sources.Count == 0)
            await AddWikipediaFallbackAsync(request, sources, seen, max, cancellationToken).ConfigureAwait(false);

        var resultValue = new MusicGroundingResult(sources);
        _cache.Set(ProviderTag, normalized, resultValue);
        return resultValue;
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var provider = _configurableSearch.IsAvailable
            ? (IWebSearchToolProvider)_configurableSearch
            : _fallbackSearch;

        try
        {
            return await provider
                .SearchAsync(query, new WebSearchOptions(MaxResults: maxResults), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Music grounding search failed for query {Query}", query);
            return [];
        }
    }

    private async Task AddMusicBrainzSourcesAsync(
        MusicGroundingRequest request,
        List<MusicGroundingSource> sources,
        HashSet<string> seen,
        int max,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = request.Kind switch
            {
                MusicGroundingKind.Artist => await FetchMusicBrainzArtistAsync(request, cancellationToken).ConfigureAwait(false),
                MusicGroundingKind.Album => await FetchMusicBrainzAlbumAsync(request, cancellationToken).ConfigureAwait(false),
                MusicGroundingKind.Track => await FetchMusicBrainzTrackAsync(request, cancellationToken).ConfigureAwait(false),
                _ => null,
            };

            if (source is not null)
                AddSource(sources, seen, source, max);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "MusicBrainz grounding failed for {Kind}", request.Kind);
        }
    }

    private async Task<MusicGroundingSource?> FetchMusicBrainzArtistAsync(
        MusicGroundingRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ArtistName))
            return null;

        var query = $"artist:\"{request.ArtistName}\"";
        var root = await FetchMusicBrainzJsonAsync("artist", query, cancellationToken).ConfigureAwait(false);
        if (!TryFirstArrayItem(root, "artists", out var item))
            return null;

        var name = GetString(item, "name") ?? request.ArtistName!;
        if (!LooksRelevant(request, name, GetString(item, "disambiguation"), null))
            return null;

        var id = GetString(item, "id");
        var type = GetString(item, "type");
        var country = GetString(item, "country");
        var lifeSpan = GetObject(item, "life-span");
        var begin = lifeSpan is { } ls ? GetString(ls, "begin") : null;
        var disambiguation = GetString(item, "disambiguation");

        var parts = new List<string>();
        AddPart(parts, type);
        AddPart(parts, country);
        if (!string.IsNullOrWhiteSpace(begin))
            parts.Add("active from " + begin);
        AddPart(parts, disambiguation);

        return new MusicGroundingSource(
            name,
            string.IsNullOrWhiteSpace(id) ? "https://musicbrainz.org/" : $"https://musicbrainz.org/artist/{id}",
            parts.Count == 0 ? "MusicBrainz artist metadata." : string.Join("; ", parts),
            "MusicBrainz",
            MusicGroundingKind.Artist,
            Reliability: 0.88);
    }

    private async Task<MusicGroundingSource?> FetchMusicBrainzAlbumAsync(
        MusicGroundingRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AlbumTitle))
            return null;

        var query = string.IsNullOrWhiteSpace(request.ArtistName)
            ? $"releasegroup:\"{request.AlbumTitle}\""
            : $"releasegroup:\"{request.AlbumTitle}\" AND artist:\"{request.ArtistName}\"";
        var root = await FetchMusicBrainzJsonAsync("release-group", query, cancellationToken).ConfigureAwait(false);
        if (!TryFirstArrayItem(root, "release-groups", out var item))
            return null;

        var title = GetString(item, "title") ?? request.AlbumTitle!;
        if (!LooksRelevant(request, title, ArtistCreditText(item), null))
            return null;

        var id = GetString(item, "id");
        var firstRelease = GetString(item, "first-release-date");
        var primaryType = GetString(item, "primary-type") ?? GetString(item, "type");
        var artistCredit = ArtistCreditText(item);
        var disambiguation = GetString(item, "disambiguation");

        var parts = new List<string>();
        AddPart(parts, artistCredit);
        AddPart(parts, primaryType);
        if (!string.IsNullOrWhiteSpace(firstRelease))
            parts.Add("first release " + firstRelease);
        AddPart(parts, disambiguation);

        return new MusicGroundingSource(
            title,
            string.IsNullOrWhiteSpace(id) ? "https://musicbrainz.org/" : $"https://musicbrainz.org/release-group/{id}",
            parts.Count == 0 ? "MusicBrainz release-group metadata." : string.Join("; ", parts),
            "MusicBrainz",
            MusicGroundingKind.Album,
            Reliability: 0.9);
    }

    private async Task<MusicGroundingSource?> FetchMusicBrainzTrackAsync(
        MusicGroundingRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TrackTitle))
            return null;

        var query = string.IsNullOrWhiteSpace(request.ArtistName)
            ? $"recording:\"{request.TrackTitle}\""
            : $"recording:\"{request.TrackTitle}\" AND artist:\"{request.ArtistName}\"";
        var root = await FetchMusicBrainzJsonAsync("recording", query, cancellationToken).ConfigureAwait(false);
        if (!TryFirstArrayItem(root, "recordings", out var item))
            return null;

        var title = GetString(item, "title") ?? request.TrackTitle!;
        if (!LooksRelevant(request, title, ArtistCreditText(item), null))
            return null;

        var id = GetString(item, "id");
        var artistCredit = ArtistCreditText(item);
        var firstRelease = GetString(item, "first-release-date");
        var disambiguation = GetString(item, "disambiguation");

        var parts = new List<string>();
        AddPart(parts, artistCredit);
        if (!string.IsNullOrWhiteSpace(firstRelease))
            parts.Add("first release " + firstRelease);
        AddPart(parts, disambiguation);

        return new MusicGroundingSource(
            title,
            string.IsNullOrWhiteSpace(id) ? "https://musicbrainz.org/" : $"https://musicbrainz.org/recording/{id}",
            parts.Count == 0 ? "MusicBrainz recording metadata." : string.Join("; ", parts),
            "MusicBrainz",
            MusicGroundingKind.Track,
            Reliability: 0.88);
    }

    private async Task<JsonElement> FetchMusicBrainzJsonAsync(
        string entity,
        string query,
        CancellationToken cancellationToken)
    {
        var url = $"https://musicbrainz.org/ws/2/{entity}/?query={Uri.EscapeDataString(query)}&fmt=json&limit=3";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var http = _httpClientFactory.CreateClient("Wavee");
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return default;

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private async Task<MusicGroundingSource?> FetchMetadataSourceAsync(
        MusicGroundingRequest request,
        WebSearchResult searchResult,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        httpRequest.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var http = _httpClientFactory.CreateClient("Wavee");
            using var response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return null;

            var html = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (html.Length > 512_000)
                html = html[..512_000];

            var title = FirstNonEmpty(
                ExtractMeta(html, "og:title"),
                ExtractJsonLdValue(html, "name"),
                ExtractTitle(html),
                searchResult.Title);
            var description = FirstNonEmpty(
                ExtractMeta(html, "og:description"),
                ExtractMeta(html, "description"),
                ExtractJsonLdValue(html, "description"),
                searchResult.Snippet);

            title = CleanText(title);
            description = CleanText(description);
            if (string.IsNullOrWhiteSpace(title))
                return null;
            if (!LooksRelevant(request, title, description, uri.ToString()))
                return null;

            return new MusicGroundingSource(
                title,
                uri.ToString(),
                TrimSnippet(description),
                HostLabel(uri.Host),
                request.Kind,
                IsMusicSpecific: true,
                Reliability: ReliabilityForHost(uri.Host));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Metadata fetch failed for {Url}", uri);
            return null;
        }
    }

    private async Task AddWikipediaFallbackAsync(
        MusicGroundingRequest request,
        List<MusicGroundingSource> sources,
        HashSet<string> seen,
        int max,
        CancellationToken cancellationToken)
    {
        if (_wikipedia is null)
            return;

        WikipediaSummary? wiki = null;
        if (request.Kind == MusicGroundingKind.Artist && !string.IsNullOrWhiteSpace(request.ArtistName))
        {
            wiki = await _wikipedia.LookupArtistAsync(request.ArtistName!, cancellationToken).ConfigureAwait(false);
        }
        else if (request.Kind == MusicGroundingKind.Album && !string.IsNullOrWhiteSpace(request.AlbumTitle))
        {
            wiki = await _wikipedia.LookupAlbumAsync(request.AlbumTitle!, request.ArtistName, cancellationToken)
                .ConfigureAwait(false);
        }

        if (wiki is null || string.IsNullOrWhiteSpace(wiki.Extract) || string.IsNullOrWhiteSpace(wiki.Url))
            return;

        AddSource(
            sources,
            seen,
            new MusicGroundingSource(
                wiki.Title,
                wiki.Url!,
                TrimSnippet(wiki.Extract),
                "Wikipedia",
                request.Kind,
                IsMusicSpecific: false,
                Reliability: 0.45),
            max);
    }

    private static MusicGroundingSource FromSearchResult(MusicGroundingRequest request, WebSearchResult result)
    {
        var host = Uri.TryCreate(result.Url, UriKind.Absolute, out var uri)
            ? HostLabel(uri.Host)
            : result.Source ?? "Web";
        return new MusicGroundingSource(
            CleanText(result.Title),
            result.Url,
            TrimSnippet(CleanText(result.Snippet)),
            host,
            request.Kind,
            IsMusicSpecific: true,
            Reliability: Uri.TryCreate(result.Url, UriKind.Absolute, out var sourceUri)
                ? ReliabilityForHost(sourceUri.Host)
                : 0.5);
    }

    private static IReadOnlyList<string> BuildMusicQueries(MusicGroundingRequest request)
    {
        var artist = request.ArtistName?.Trim();
        var album = request.AlbumTitle?.Trim();
        var track = request.TrackTitle?.Trim();

        return request.Kind switch
        {
            MusicGroundingKind.Artist when !string.IsNullOrWhiteSpace(artist) =>
            [
                $"\"{artist}\" musician biography music",
                $"\"{artist}\" artist profile site:genius.com",
                $"\"{artist}\" artist profile site:last.fm",
            ],
            MusicGroundingKind.Album when !string.IsNullOrWhiteSpace(album) && !string.IsNullOrWhiteSpace(artist) =>
            [
                $"\"{artist}\" \"{album}\" album music",
                $"\"{artist}\" \"{album}\" site:musicbrainz.org",
                $"\"{artist}\" \"{album}\" site:discogs.com",
                $"\"{artist}\" \"{album}\" site:genius.com",
            ],
            MusicGroundingKind.Album when !string.IsNullOrWhiteSpace(album) =>
            [
                $"\"{album}\" album music",
                $"\"{album}\" site:musicbrainz.org",
                $"\"{album}\" site:discogs.com",
            ],
            MusicGroundingKind.Track when !string.IsNullOrWhiteSpace(track) && !string.IsNullOrWhiteSpace(artist) =>
            [
                $"\"{artist}\" \"{track}\" song meaning music",
                $"\"{artist}\" \"{track}\" site:genius.com",
                $"\"{artist}\" \"{track}\" site:musixmatch.com",
                $"\"{artist}\" \"{track}\" site:songfacts.com",
            ],
            MusicGroundingKind.Track when !string.IsNullOrWhiteSpace(track) =>
            [
                $"\"{track}\" song meaning music",
                $"\"{track}\" site:genius.com",
                $"\"{track}\" site:musixmatch.com",
            ],
            _ => [],
        };
    }

    private static bool LooksRelevant(MusicGroundingRequest request, string? title, string? snippet, string? url)
    {
        var haystack = NormalizeText(string.Join(" ", title, snippet, url));
        if (string.IsNullOrWhiteSpace(haystack))
            return false;

        var artist = NormalizeText(request.ArtistName);
        var album = NormalizeText(request.AlbumTitle);
        var track = NormalizeText(request.TrackTitle);

        return request.Kind switch
        {
            MusicGroundingKind.Artist => ContainsToken(haystack, artist),
            MusicGroundingKind.Album => ContainsToken(haystack, album)
                                        && (string.IsNullOrEmpty(artist) || ContainsToken(haystack, artist)),
            MusicGroundingKind.Track => ContainsToken(haystack, track)
                                        && (string.IsNullOrEmpty(artist) || ContainsToken(haystack, artist)),
            _ => false,
        };
    }

    private static void AddSource(
        List<MusicGroundingSource> sources,
        HashSet<string> seen,
        MusicGroundingSource source,
        int max)
    {
        if (sources.Count >= max)
            return;
        if (string.IsNullOrWhiteSpace(source.Title) || string.IsNullOrWhiteSpace(source.Url))
            return;
        if (!seen.Add(source.Url))
            return;

        sources.Add(source with
        {
            Title = TrimTitle(source.Title),
            Snippet = TrimSnippet(source.Snippet),
        });
    }

    private static MusicGroundingResult Limit(MusicGroundingResult result, int maxSources)
    {
        var max = Math.Clamp(maxSources, 1, 8);
        return result.Sources.Count <= max
            ? result
            : new MusicGroundingResult(result.Sources.Take(max).ToList());
    }

    private static string NormalizeRequest(MusicGroundingRequest request)
        => string.Join("|",
            request.Kind,
            request.ArtistName?.Trim(),
            request.AlbumTitle?.Trim(),
            request.TrackTitle?.Trim(),
            Math.Clamp(request.MaxSources, 1, 8));

    private static bool TryCreateAllowedUri(string? url, out Uri uri)
    {
        uri = default!;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsAllowedMusicHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        return AllowedMusicHosts.Any(allowed =>
            host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));
    }

    private static string HostLabel(string host)
    {
        host = host.Trim().ToLowerInvariant();
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
            "officialcharts.com" => "Official Charts",
            _ => host,
        };
    }

    private static double ReliabilityForHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host.Contains("musicbrainz.org", StringComparison.Ordinal)) return 0.9;
        if (host.Contains("discogs.com", StringComparison.Ordinal)) return 0.75;
        if (host.Contains("allmusic.com", StringComparison.Ordinal)) return 0.72;
        if (host.Contains("last.fm", StringComparison.Ordinal)) return 0.68;
        if (host.Contains("genius.com", StringComparison.Ordinal)) return 0.62;
        if (host.Contains("musixmatch.com", StringComparison.Ordinal)) return 0.6;
        return 0.55;
    }

    private static bool TryFirstArrayItem(JsonElement root, string name, out JsonElement item)
    {
        item = default;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var candidate in array.EnumerateArray())
        {
            if (candidate.ValueKind == JsonValueKind.Object)
            {
                item = candidate.Clone();
                return true;
            }
        }

        return false;
    }

    private static JsonElement? GetObject(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ArtistCreditText(JsonElement item)
    {
        if (!item.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array)
            return null;

        var names = new List<string>(3);
        foreach (var entry in credit.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            if (entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                AddPart(names, name.GetString());
            else if (entry.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object)
                AddPart(names, GetString(artist, "name"));
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static void AddPart(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add(value.Trim());
    }

    private static string? ExtractTitle(string html)
        => MatchGroup(html, @"<title[^>]*>(?<v>.*?)</title>");

    private static string? ExtractMeta(string html, string name)
    {
        var escaped = Regex.Escape(name);
        return MatchGroup(
            html,
            $@"<meta\s+[^>]*(?:property|name)=[""']{escaped}[""'][^>]*content=[""'](?<v>[^""']*)[""'][^>]*>")
            ?? MatchGroup(
                html,
                $@"<meta\s+[^>]*content=[""'](?<v>[^""']*)[""'][^>]*(?:property|name)=[""']{escaped}[""'][^>]*>");
    }

    private static string? ExtractJsonLdValue(string html, string propertyName)
    {
        foreach (Match match in Regex.Matches(
                     html,
                     @"<script[^>]+type=[""']application/ld\+json[""'][^>]*>(?<json>.*?)</script>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var json = HttpUtility.HtmlDecode(match.Groups["json"].Value);
            try
            {
                using var document = JsonDocument.Parse(json);
                if (TryFindJsonLdString(document.RootElement, propertyName, out var value))
                    return value;
            }
            catch
            {
                // Ignore malformed page metadata.
            }
        }

        return null;
    }

    private static bool TryFindJsonLdString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }

            foreach (var child in element.EnumerateObject())
            {
                if (TryFindJsonLdString(child.Value, propertyName, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (TryFindJsonLdString(child, propertyName, out value))
                    return true;
            }
        }

        return false;
    }

    private static string? MatchGroup(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decoded = HttpUtility.HtmlDecode(value);
        decoded = Regex.Replace(decoded, "<[^>]+>", " ", RegexOptions.Singleline);
        decoded = WebUtility.HtmlDecode(decoded);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string TrimTitle(string value)
        => TrimTo(value, 140);

    private static string TrimSnippet(string? value)
        => TrimTo(value ?? string.Empty, 320);

    private static string TrimTo(string value, int max)
    {
        value = CleanText(value);
        if (value.Length <= max)
            return value;
        var trimmed = value[..max];
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > max / 2)
            trimmed = trimmed[..lastSpace];
        return trimmed.TrimEnd() + "...";
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.ToLowerInvariant();
        value = Regex.Replace(value, @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static bool ContainsToken(string haystack, string needle)
        => string.IsNullOrEmpty(needle) || haystack.Contains(needle, StringComparison.Ordinal);
}
