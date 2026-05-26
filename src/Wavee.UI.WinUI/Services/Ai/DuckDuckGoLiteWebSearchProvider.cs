using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;
using Wavee.AI.Tools;

namespace Wavee.UI.WinUI.Services.Ai;

/// <summary>
/// Default web-search backend that needs no API key and no user configuration.
/// Hits the DuckDuckGo "lite" HTML endpoint and parses its (deliberately
/// minimal) table-based markup for organic result links + snippets.
///
/// Failure modes (rate limit, anti-bot, layout change) return an empty list
/// rather than throwing — AI callers degrade to ungrounded output instead of
/// surfacing a network error to the user.
/// </summary>
internal sealed partial class DuckDuckGoLiteWebSearchProvider : IWebSearchToolProvider
{
    private const string ProviderTag = "ddg-lite";
    private const string Endpoint = "https://lite.duckduckgo.com/lite/";

    // Mimics a recent Firefox UA. DDG lite rejects empty/obviously-bot UAs with
    // an anomaly page; this UA stays well within "casual organic traffic".
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebSearchCache _cache;
    private readonly ILogger? _logger;

    public DuckDuckGoLiteWebSearchProvider(
        IHttpClientFactory httpClientFactory,
        WebSearchCache cache,
        ILogger<DuckDuckGoLiteWebSearchProvider>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        WebSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        options ??= new WebSearchOptions();
        var max = Math.Max(1, options.MaxResults);
        var normalized = query.Trim();

        if (_cache.TryGet<IReadOnlyList<WebSearchResult>>(ProviderTag, normalized, out var hit))
            return TrimResults(hit, max);

        var html = await FetchHtmlAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html))
            return [];

        var parsed = ParseResults(html);
        if (parsed.Count == 0)
        {
            _logger?.LogWarning("DuckDuckGo lite returned unparseable HTML for query {Query}", normalized);
            return [];
        }

        _cache.Set(ProviderTag, normalized, parsed);
        return TrimResults(parsed, max);
    }

    private async Task<string?> FetchHtmlAsync(string query, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient("Wavee");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = query,
                ["kl"] = "us-en",
            }),
        };
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US", 0.9));
        request.Headers.Referrer = new Uri("https://duckduckgo.com/");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("DuckDuckGo lite returned {Status} for query {Query}", response.StatusCode, query);
                return null;
            }
            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogDebug("DuckDuckGo lite timed out for query {Query}", query);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "DuckDuckGo lite failed for query {Query}", query);
            return null;
        }
    }

    private static IReadOnlyList<WebSearchResult> ParseResults(string html)
    {
        // DDG lite emits one result per <a class="result-link">; the snippet
        // follows in a sibling <td class="result-snippet">. Some skins drop the
        // class names — fall back to plain <a href> if needed.
        var primary = LinkAndSnippetRegex().Matches(html);
        if (primary.Count == 0)
            primary = FallbackLinkRegex().Matches(html);

        var results = new List<WebSearchResult>(primary.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in primary)
        {
            var href = DecodeHref(match.Groups["href"].Value);
            if (string.IsNullOrWhiteSpace(href))
                continue;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
                continue;
            if (uri.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seen.Add(uri.ToString()))
                continue;

            var title = DecodeText(match.Groups["title"].Value);
            var snippet = match.Groups["snippet"].Success
                ? DecodeText(match.Groups["snippet"].Value)
                : string.Empty;

            if (string.IsNullOrWhiteSpace(title))
                continue;

            results.Add(new WebSearchResult(
                title,
                uri.ToString(),
                snippet,
                PublishedAt: null,
                Source: uri.Host));
        }

        return results;
    }

    private static string DecodeHref(string value)
    {
        // DDG lite wraps outbound clicks in /l/?uddg=... — unwrap to the real URL.
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var raw = HttpUtility.HtmlDecode(value);
        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = "https:" + raw;

        if (raw.Contains("uddg=", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(raw, raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? UriKind.Absolute
                    : UriKind.RelativeOrAbsolute);
                var query = uri.IsAbsoluteUri ? uri.Query : raw[raw.IndexOf('?')..];
                var parsed = HttpUtility.ParseQueryString(query);
                var unwrapped = parsed["uddg"];
                if (!string.IsNullOrWhiteSpace(unwrapped))
                    return unwrapped;
            }
            catch
            {
                // Fall through to raw value.
            }
        }

        return raw;
    }

    private static string DecodeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var stripped = HtmlTagRegex().Replace(value, " ");
        var decoded = HttpUtility.HtmlDecode(stripped);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    private static IReadOnlyList<WebSearchResult> TrimResults(IReadOnlyList<WebSearchResult> source, int max)
    {
        if (source.Count <= max) return source;
        var trimmed = new WebSearchResult[max];
        for (var i = 0; i < max; i++) trimmed[i] = source[i];
        return trimmed;
    }

    [GeneratedRegex(@"<a[^>]*class=""result-link""[^>]*href=""(?<href>[^""]+)""[^>]*>(?<title>.*?)</a>.*?<td[^>]*class=""result-snippet""[^>]*>(?<snippet>.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex LinkAndSnippetRegex();

    [GeneratedRegex(@"<a[^>]*href=""(?<href>https?://[^""]+|//l/\?[^""]+)""[^>]*>(?<title>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex FallbackLinkRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline, "en-US")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.None, "en-US")]
    private static partial Regex WhitespaceRegex();
}
