using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.AI.Tools;

namespace Wavee.UI.WinUI.Services.Ai;

/// <summary>
/// Free MediaWiki REST API summary lookup for artist and album grounding.
/// Tries the plain name first, then a sequence of disambiguators ("(musician)",
/// "(band)" for artists; "({artist} album)", "(album)" for albums) to land on
/// the right page. The /page/summary endpoint returns a clean lead-paragraph
/// "extract" — high signal for biographical grounding without HTML scraping.
///
/// All requests share the WebSearchCache so repeat artist/album visits don't
/// re-hit Wikipedia. Failures (404, network) collapse to null; callers degrade
/// gracefully by emitting nothing in their prompt.
/// </summary>
internal sealed class WikipediaArticleLookup : IWikipediaLookup
{
    private const string ArtistProviderTag = "wikipedia:artist";
    private const string AlbumProviderTag = "wikipedia:album";
    private const string UserAgent = "WaveeMusic/1.0 (https://github.com/ckara/WaveeMusic; on-device AI grounding)";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebSearchCache _cache;
    private readonly ILogger? _logger;

    public WikipediaArticleLookup(
        IHttpClientFactory httpClientFactory,
        WebSearchCache cache,
        ILogger<WikipediaArticleLookup>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;
    }

    public Task<WikipediaSummary?> LookupArtistAsync(
        string artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult<WikipediaSummary?>(null);

        var normalized = artistName.Trim();
        var candidates = new[]
        {
            normalized,
            normalized + " (musician)",
            normalized + " (band)",
            normalized + " (singer)",
        };

        return ResolveAsync(ArtistProviderTag, normalized, candidates, cancellationToken);
    }

    public Task<WikipediaSummary?> LookupAlbumAsync(
        string albumTitle,
        string? artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return Task.FromResult<WikipediaSummary?>(null);

        var title = albumTitle.Trim();
        var artist = artistName?.Trim();
        var cacheKey = string.IsNullOrEmpty(artist) ? title : $"{title}|{artist}";

        var candidates = new List<string>(5) { title };
        if (!string.IsNullOrEmpty(artist))
            candidates.Add($"{title} ({artist} album)");
        candidates.Add($"{title} (album)");
        candidates.Add($"{title} (EP)");

        return ResolveAsync(AlbumProviderTag, cacheKey, candidates, cancellationToken);
    }

    private async Task<WikipediaSummary?> ResolveAsync(
        string providerTag,
        string cacheKey,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGet<WikipediaSummary>(providerTag, cacheKey, out var hit))
            return hit;

        var http = _httpClientFactory.CreateClient("Wavee");
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dto = await FetchSummaryAsync(http, candidate, cancellationToken).ConfigureAwait(false);
            if (dto is null) continue;

            // Skip disambiguation stubs — they don't help grounding.
            if (string.Equals(dto.Type, "disambiguation", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(dto.Extract))
                continue;

            var result = new WikipediaSummary(
                dto.Title ?? candidate,
                dto.Extract!,
                dto.Url,
                dto.Lang,
                dto.Description);

            _cache.Set(providerTag, cacheKey, result);
            return result;
        }

        return null;
    }

    private async Task<WikipediaSummaryDto?> FetchSummaryAsync(
        HttpClient http,
        string title,
        CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(title.Replace(' ', '_'));
        var url = $"https://en.wikipedia.org/api/rest_v1/page/summary/{encoded}?redirect=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("Wikipedia lookup for {Title} returned {Status}", title, response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var root = document.RootElement;

            return new WikipediaSummaryDto(
                GetString(root, "title"),
                GetString(root, "extract"),
                GetString(root, "description"),
                GetString(root, "type"),
                GetUrl(root),
                GetString(root, "lang"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogDebug("Wikipedia lookup timed out for {Title}", title);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Wikipedia lookup failed for {Title}", title);
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? GetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("content_urls", out var urls) || urls.ValueKind != JsonValueKind.Object)
            return null;
        if (!urls.TryGetProperty("desktop", out var desktop) || desktop.ValueKind != JsonValueKind.Object)
            return null;
        return desktop.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.String
            ? page.GetString()
            : null;
    }

    private sealed record WikipediaSummaryDto(
        string? Title,
        string? Extract,
        string? Description,
        string? Type,
        string? Url,
        string? Lang);
}
