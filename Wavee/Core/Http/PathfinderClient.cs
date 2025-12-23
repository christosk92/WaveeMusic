using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;

namespace Wavee.Core.Http;

/// <summary>
/// Client for Spotify's Pathfinder GraphQL API (search, browse, etc).
/// </summary>
/// <remarks>
/// Uses the api-partner.spotify.com endpoint for GraphQL queries.
/// Requires access tokens from login5.
/// </remarks>
public sealed class PathfinderClient : IPathfinderClient
{
    private const int MaxRetries = 3;
    private const string DefaultBaseUrl = "https://api-partner.spotify.com";

    private readonly ISession _session;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new PathfinderClient.
    /// </summary>
    /// <param name="session">Active Spotify session for obtaining access tokens.</param>
    /// <param name="httpClient">HTTP client for making requests.</param>
    /// <param name="baseUrl">Base URL for the Pathfinder API (default: https://api-partner.spotify.com).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public PathfinderClient(
        ISession session,
        HttpClient httpClient,
        string? baseUrl = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(httpClient);

        _session = session;
        _httpClient = httpClient;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SearchResult> SearchAsync(
        string query,
        int limit = 10,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        // Create the GraphQL request
        var request = PathfinderRequest.CreateSearchRequest(query, limit, offset);

        // Serialize to JSON using source-generated context
        var jsonBody = JsonSerializer.Serialize(request, PathfinderJsonContext.Default.PathfinderRequest);

        _logger?.LogDebug("Searching for: {Query} (limit={Limit}, offset={Offset})", query, limit, offset);

        // Get access token
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        // Build HTTP request
        var url = $"{_baseUrl}/pathfinder/v2/query";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Headers.AcceptLanguage.ParseAdd("en");
        httpRequest.Headers.TryAddWithoutValidation("app-platform", "Win32_x86_64");
        httpRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        // Send with retry
        var response = await SendWithRetryAsync(httpRequest, cancellationToken);

        // Handle errors
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(
                    SpClientFailureReason.Unauthorized,
                    "Access token invalid or expired");
            case HttpStatusCode.BadRequest:
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogWarning("Search request failed: {Error}", errorContent);
                throw new SpClientException(
                    SpClientFailureReason.RequestFailed,
                    $"Invalid search request: {errorContent}");
            case HttpStatusCode.TooManyRequests:
                throw new SpClientException(
                    SpClientFailureReason.RateLimited,
                    "Rate limit exceeded");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(
                SpClientFailureReason.ServerError,
                $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        // Parse response
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var searchResponse = JsonSerializer.Deserialize(
            responseJson,
            PathfinderJsonContext.Default.PathfinderSearchResponse);

        if (searchResponse == null)
        {
            throw new SpClientException(
                SpClientFailureReason.RequestFailed,
                "Failed to parse search response");
        }

        var result = SearchResult.FromResponse(searchResponse);

        _logger?.LogDebug(
            "Search completed: {Query} - found {Tracks} tracks, {Artists} artists, {Albums} albums, {Playlists} playlists",
            query,
            result.TotalTracks,
            result.TotalArtists,
            result.TotalAlbums,
            result.TotalPlaylists);

        return result;
    }

    /// <summary>
    /// Sends an HTTP request with retry logic for transient failures.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                // Clone the request for retries (request can only be sent once)
                HttpRequestMessage requestToSend;
                if (attempt > 0)
                {
                    requestToSend = await CloneRequestAsync(request);
                }
                else
                {
                    requestToSend = request;
                }

                var response = await _httpClient.SendAsync(requestToSend, cancellationToken);

                // Check for retryable status codes
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                    _logger?.LogWarning(
                        "Pathfinder request failed with {StatusCode}, retrying in {Delay}s (attempt {Attempt}/{MaxRetries})",
                        response.StatusCode,
                        delay.TotalSeconds,
                        attempt + 1,
                        MaxRetries);

                    if (attempt < MaxRetries - 1)
                    {
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }
                }

                return response;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger?.LogWarning(ex,
                    "Pathfinder HTTP request failed (attempt {Attempt}/{MaxRetries})",
                    attempt + 1,
                    MaxRetries);

                if (attempt < MaxRetries - 1)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
            }
        }

        throw new SpClientException(
            SpClientFailureReason.RequestFailed,
            $"Pathfinder request failed after {MaxRetries} retries",
            lastException);
    }

    /// <summary>
    /// Clones an HTTP request for retry (original request cannot be reused after sending).
    /// </summary>
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        // Copy headers
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content
        if (request.Content != null)
        {
            var content = await request.Content.ReadAsStringAsync();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }
}
