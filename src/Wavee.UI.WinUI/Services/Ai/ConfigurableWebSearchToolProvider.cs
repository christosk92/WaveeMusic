using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.AI.Tools;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services.Ai;

public sealed class ConfigurableWebSearchToolProvider : IWebSearchToolProvider, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogger? _logger;
    private readonly HttpClient _http = new();

    public ConfigurableWebSearchToolProvider(
        ISettingsService settings,
        ILogger<ConfigurableWebSearchToolProvider>? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsAvailable =>
        _settings.Settings.AiOnlineToolsEnabled
        && !string.IsNullOrWhiteSpace(_settings.Settings.AiWebSearchEndpoint);

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        WebSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
            return [];

        options ??= new WebSearchOptions();
        var endpoint = BuildEndpoint(_settings.Settings.AiWebSearchEndpoint!, query, options);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

        var apiKey = _settings.Settings.AiWebSearchApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseSearchResults(document.RootElement, options.MaxResults);
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

    public void Dispose() => _http.Dispose();
}
