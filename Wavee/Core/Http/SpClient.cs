using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;

namespace Wavee.Core.Http;

/// <summary>
/// Client for Spotify's spclient HTTP API (metadata, lyrics, context, etc).
/// </summary>
/// <remarks>
/// Requires access tokens from login5.
/// Base URL: https://spclient.wg.spotify.com
/// </remarks>
public sealed class SpClient
{
    private const string BaseUrl = "https://spclient.wg.spotify.com";
    private const int MaxRetries = 3;

    private readonly Session.Session _session;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new SpClient.
    /// </summary>
    /// <param name="session">Active Spotify session for obtaining access tokens.</param>
    /// <param name="httpClient">HTTP client for making requests.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    internal SpClient(Session.Session session, HttpClient httpClient, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(httpClient);

        _session = session;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Gets track metadata.
    /// </summary>
    /// <param name="trackId">Spotify track ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded track metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<byte[]> GetTrackMetadataAsync(
        string trackId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);

        var url = $"{BaseUrl}/metadata/4/track/{trackId}";
        return await GetProtobufAsync(url, cancellationToken);
    }

    /// <summary>
    /// Gets album metadata.
    /// </summary>
    /// <param name="albumId">Spotify album ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded album metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<byte[]> GetAlbumMetadataAsync(
        string albumId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumId);

        var url = $"{BaseUrl}/metadata/4/album/{albumId}";
        return await GetProtobufAsync(url, cancellationToken);
    }

    /// <summary>
    /// Gets artist metadata.
    /// </summary>
    /// <param name="artistId">Spotify artist ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded artist metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<byte[]> GetArtistMetadataAsync(
        string artistId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artistId);

        var url = $"{BaseUrl}/metadata/4/artist/{artistId}";
        return await GetProtobufAsync(url, cancellationToken);
    }

    /// <summary>
    /// Makes an authenticated GET request for protobuf data.
    /// </summary>
    private async Task<byte[]> GetProtobufAsync(
        string url,
        CancellationToken cancellationToken)
    {
        // Get access token (auto-refreshes if needed)
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        // Build request
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        // TODO: Add client-token header from ClientToken manager when implemented

        // Send request with retry logic
        var response = await SendWithRetryAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            // Check response status
            case HttpStatusCode.NotFound:
                throw new SpClientException(
                    SpClientFailureReason.NotFound,
                    $"Resource not found: {url}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(
                    SpClientFailureReason.Unauthorized,
                    "Access token invalid or expired");
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

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Sends an HTTP request with exponential backoff retry logic.
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
                var response = await _httpClient.SendAsync(request, cancellationToken);

                // Check for retryable status codes
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                    _logger?.LogWarning(
                        "SpClient request failed with {StatusCode}, retrying in {Delay}s (attempt {Attempt}/{MaxRetries})",
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
                    "SpClient HTTP request failed (attempt {Attempt}/{MaxRetries})",
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
            $"Failed after {MaxRetries} attempts",
            lastException);
    }
}