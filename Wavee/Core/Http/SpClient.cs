using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Session;
using Wavee.Protocol.Storage;

namespace Wavee.Core.Http;

/// <summary>
/// Client for Spotify's spclient HTTP API (metadata, lyrics, context, etc).
/// </summary>
/// <remarks>
/// Requires access tokens from login5.
/// Endpoints are resolved dynamically via ApResolver.
/// </remarks>
public sealed class SpClient
{
    private readonly string _baseUrl;
    private const int MaxRetries = 3;

    private readonly ISession _session;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new SpClient.
    /// </summary>
    /// <param name="session">Active Spotify session for obtaining access tokens.</param>
    /// <param name="httpClient">HTTP client for making requests.</param>
    /// <param name="baseUrl">Resolved SpClient endpoint (e.g., "spclient.wg.spotify.com:443").</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    internal SpClient(ISession session, HttpClient httpClient, string baseUrl, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _session = session;
        _httpClient = httpClient;
        _logger = logger;

        // Normalize base URL: remove port suffix and ensure https:// prefix
        var hostOnly = baseUrl.Split(':')[0];
        _baseUrl = $"https://{hostOnly}";
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

        var url = $"{_baseUrl}/metadata/4/track/{trackId}";
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

        var url = $"{_baseUrl}/metadata/4/album/{albumId}";
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

        var url = $"{_baseUrl}/metadata/4/artist/{artistId}";
        return await GetProtobufAsync(url, cancellationToken);
    }

    /// <summary>
    /// Resolves CDN URLs for an audio file.
    /// </summary>
    /// <remarks>
    /// This endpoint returns a list of CDN URLs where the audio file can be downloaded.
    /// The URLs are time-limited and include authentication tokens.
    /// </remarks>
    /// <param name="fileId">The audio file ID (20 bytes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>StorageResolveResponse containing CDN URLs.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<StorageResolveResponse> ResolveAudioStorageAsync(
        FileId fileId,
        CancellationToken cancellationToken = default)
    {
        if (!fileId.IsValid)
            throw new ArgumentException("FileId is not valid", nameof(fileId));

        var url = $"{_baseUrl}/storage-resolve/files/audio/interactive/{fileId.ToBase16()}";
        var responseBytes = await GetProtobufAsync(url, cancellationToken);

        var response = StorageResolveResponse.Parser.ParseFrom(responseBytes);

        _logger?.LogDebug("Resolved audio storage for {FileId}: {UrlCount} CDN URLs, result={Result}",
            fileId.ToBase16(), response.Cdnurl.Count, response.Result);

        if (response.Result == StorageResolveResponse.Types.Result.Restricted)
        {
            throw new SpClientException(
                SpClientFailureReason.Unauthorized,
                $"Audio file {fileId.ToBase16()} is restricted");
        }

        return response;
    }

    /// <summary>
    /// Announces device availability via Spotify Connect.
    /// </summary>
    /// <remarks>
    /// This endpoint is used by Spotify Connect to announce device presence.
    /// Spotify uses this information to show the device in the "Available Devices" list.
    /// Requires a connection ID from the dealer WebSocket connection.
    /// </remarks>
    /// <param name="deviceId">Device ID from session config.</param>
    /// <param name="connectionId">Connection ID from dealer WebSocket (from hm://pusher/v1/connections/).</param>
    /// <param name="request">PUT state request with device info and state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<byte[]> PutConnectStateAsync(
        string deviceId,
        string connectionId,
        Protocol.Player.PutStateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(request);

        // Get access token (auto-refreshes if needed)
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{_baseUrl}/connect-state/v1/devices/{deviceId}";

        // Build request
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.Add("X-Spotify-Connection-Id", connectionId);
        httpRequest.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        // Serialize protobuf to byte array
        var protobufBytes = request.ToByteArray();
        httpRequest.Content = new ByteArrayContent(protobufBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        // Send request with retry logic
        var response = await SendWithRetryAsync(httpRequest, cancellationToken);

        // Check response status
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(
                    SpClientFailureReason.Unauthorized,
                    "Access token invalid or expired");
            case HttpStatusCode.BadRequest:
                throw new SpClientException(
                    SpClientFailureReason.RequestFailed,
                    "Invalid PUT state request");
            case HttpStatusCode.Forbidden:
                throw new SpClientException(
                    SpClientFailureReason.Unauthorized,
                    "Device not authorized");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(
                SpClientFailureReason.ServerError,
                $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        // Read response body - Spotify returns a ClusterUpdate protobuf
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        _logger?.LogDebug("Successfully updated connect state for device {DeviceId}, response size: {Size} bytes",
            deviceId, responseBody.Length);

        return responseBody;
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

        // Add Accept-Language header if locale is available
        var locale = GetEffectiveLocale();
        if (!string.IsNullOrEmpty(locale))
        {
            request.Headers.AcceptLanguage.ParseAdd(locale);
        }

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
    /// Gets the effective locale for API requests.
    /// </summary>
    /// <returns>Locale string (e.g., "en", "es", "fr") or null if not available.</returns>
    private string? GetEffectiveLocale()
    {
        // 1. Use session override if set (via UpdateLocaleAsync)
        var sessionLocale = _session.GetPreferredLocale();
        if (!string.IsNullOrEmpty(sessionLocale))
        {
            return sessionLocale;
        }

        // 2. Fall back to Spotify's locale from ProductInfo packet
        var userData = _session.GetUserData();
        return userData?.PreferredLocale;
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