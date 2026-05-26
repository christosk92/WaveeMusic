using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.AI.Tools;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services.Ai;

/// <summary>
/// JSON-shape web search backend driven by user-configured Settings — the
/// "bring your own Brave/Bing/Google endpoint" path. Stays as a fallback for
/// power users who want better quality than the default DuckDuckGo lite
/// scrape. The composite provider routes here whenever
/// <see cref="AppSettings.AiWebSearchEndpoint"/> is non-empty.
/// </summary>
public sealed class ConfigurableWebSearchToolProvider : IWebSearchToolProvider
{
    private const string ProviderTag = "configurable";

    private readonly ISettingsService _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebSearchCache _cache;
    private readonly ILogger? _logger;

    public ConfigurableWebSearchToolProvider(
        ISettingsService settings,
        IHttpClientFactory httpClientFactory,
        WebSearchCache cache,
        ILogger<ConfigurableWebSearchToolProvider>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_settings.Settings.AiWebSearchEndpoint);

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        WebSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
            return [];

        options ??= new WebSearchOptions();
        var max = Math.Max(1, options.MaxResults);
        var normalized = query.Trim();
        if (_cache.TryGet<IReadOnlyList<WebSearchResult>>(ProviderTag, normalized, out var hit))
            return TrimResults(hit, max);

        var endpoint = BuildEndpoint(_settings.Settings.AiWebSearchEndpoint!, normalized, options);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

        var apiKey = _settings.Settings.AiWebSearchApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var http = _httpClientFactory.CreateClient("Wavee");
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var parsed = ParseSearchResults(document.RootElement, options.MaxResults);
            _cache.Set(ProviderTag, normalized, parsed);
            return TrimResults(parsed, max);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogDebug("Configurable web search timed out for endpoint {Endpoint}", endpoint);
            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AI web search failed for endpoint {Endpoint}", endpoint);
            return [];
        }
    }

    private static Uri BuildEndpoint(string endpoint, string query, WebSearchOptions options)
    {
        var encodedQuery = Uri.EscapeDataString(query);
        var url = endpoint.Contains("{query}", StringComparison.OrdinalIgnoreCase)
            ? endpoint.Replace("{query}", encodedQuery, StringComparison.OrdinalIgnoreCase)
            : AppendQuery(endpoint, "q", encodedQuery);

        if (!url.Contains("count=", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("limit=", StringComparison.OrdinalIgnoreCase))
        {
            url = AppendQuery(url, "count", Math.Max(1, options.MaxResults).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return new Uri(url, UriKind.Absolute);
    }

    private static string AppendQuery(string url, string name, string encodedValue)
        => url + (url.Contains('?') ? "&" : "?") + name + "=" + encodedValue;

    private static IReadOnlyList<WebSearchResult> ParseSearchResults(JsonElement root, int maxResults)
    {
        var items = FindResultsArray(root);
        if (items.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<WebSearchResult>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var title = GetString(item, "title") ?? GetString(item, "name");
            var url = GetString(item, "url") ?? GetString(item, "link");
            var snippet = GetString(item, "snippet") ?? GetString(item, "description") ?? GetString(item, "summary");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
                continue;

            results.Add(new WebSearchResult(
                title,
                url,
                snippet ?? string.Empty,
                TryParseDate(GetString(item, "publishedAt") ?? GetString(item, "datePublished")),
                GetString(item, "source") ?? GetString(item, "siteName")));

            if (results.Count >= Math.Max(1, maxResults))
                break;
        }

        return results;
    }

    private static JsonElement FindResultsArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;
        if (TryGetArray(root, "results", out var results))
            return results;
        if (root.TryGetProperty("webPages", out var webPages)
            && TryGetArray(webPages, "value", out var bingValue))
        {
            return bingValue;
        }
        if (TryGetArray(root, "items", out var items))
            return items;

        return default;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement array)
    {
        array = default;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        array = property;
        return true;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? TryParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static IReadOnlyList<WebSearchResult> TrimResults(IReadOnlyList<WebSearchResult> source, int max)
    {
        if (source.Count <= max) return source;
        var trimmed = new WebSearchResult[max];
        for (var i = 0; i < max; i++) trimmed[i] = source[i];
        return trimmed;
    }
}
