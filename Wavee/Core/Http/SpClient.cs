using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Http.Lyrics;
using Wavee.Core.Session;
using Wavee.Protocol.Collection;
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
    private const string CollectionContentType = "application/vnd.collection-v2.spotify.proto";

    private readonly ISession _session;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    /// Gets the base URL for the SpClient API (e.g., "https://spclient.wg.spotify.com").
    /// </summary>
    public string BaseUrl => _baseUrl;

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

        var url = $"{_baseUrl}/metadata/4/track/{trackId}?market=from_token";
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

        var url = $"{_baseUrl}/metadata/4/album/{albumId}?market=from_token";
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

        var url = $"{_baseUrl}/metadata/4/artist/{artistId}?market=from_token";
        return await GetProtobufAsync(url, cancellationToken);
    }

    /// <summary>
    /// Gets episode metadata.
    /// </summary>
    /// <remarks>
    /// Spotify episodes are streamed like tracks - they have file IDs and use
    /// the same CDN/storage mechanism. The metadata includes an audio list
    /// (equivalent to track's file list) with available formats.
    /// </remarks>
    /// <param name="episodeId">Spotify episode ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded episode metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<byte[]> GetEpisodeMetadataAsync(
        string episodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeId);

        var url = $"{_baseUrl}/metadata/4/episode/{episodeId}?market=from_token";
        return await GetProtobufAsync(url, cancellationToken);
    }

    /// <summary>
    /// Gets show (podcast) metadata.
    /// </summary>
    /// <param name="showId">Spotify show ID (base62 format, 22 characters).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded show metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<byte[]> GetShowMetadataAsync(
        string showId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showId);

        var url = $"{_baseUrl}/metadata/4/show/{showId}?market=from_token";
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
    /// Posts a playback event to Spotify's event-service.
    /// </summary>
    /// <remarks>
    /// Events are used for playback reporting (artist payouts).
    /// The event body uses tab-delimited (0x09) fields.
    /// </remarks>
    /// <param name="eventBody">Event body bytes (tab-delimited fields).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task PostEventAsync(
        byte[] eventBody,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventBody);

        // Get access token (auto-refreshes if needed)
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{_baseUrl}/event-service/v1/events";

        // Build request matching librespot-java's format
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Add("Accept-Language", "en");
        request.Headers.Add("X-ClientTimeStamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        request.Content = new ByteArrayContent(eventBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        // Send request (don't retry for events - they're fire-and-forget)
        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Event-service returned {StatusCode}", response.StatusCode);
            }
            else
            {
                _logger?.LogDebug("Event posted successfully to event-service");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Failed to post event to event-service");
            // Don't throw - events are fire-and-forget
        }
    }

    #region Context Resolution

    /// <summary>
    /// Resolves a context URI to get track list (works for playlists, albums, artists, stations, etc.)
    /// </summary>
    /// <remarks>
    /// Uses the context-resolve API which returns JSON-encoded protobuf.
    /// The returned Context contains pages of tracks that can be loaded lazily.
    /// </remarks>
    /// <param name="contextUri">Spotify context URI (e.g., "spotify:playlist:xxx", "spotify:album:xxx").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Context containing track pages.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<Protocol.Context.Context> ResolveContextAsync(
        string contextUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextUri);

        // Get access token
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        // Build request - context-resolve returns JSON
        var url = $"{_baseUrl}/context-resolve/v1/{Uri.EscapeDataString(contextUri)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        _logger?.LogDebug("Resolving context: {ContextUri}", contextUri);

        var response = await SendWithRetryAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Context not found: {contextUri}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.BadRequest:
                throw new SpClientException(SpClientFailureReason.RequestFailed, $"Invalid context URI: {contextUri}");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        // Parse JSON response to protobuf
        var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var context = Google.Protobuf.JsonParser.Default.Parse<Protocol.Context.Context>(jsonContent);

        _logger?.LogDebug("Context resolved: {Uri}, pages={PageCount}, tracks in first page={TrackCount}",
            context.Uri,
            context.Pages.Count,
            context.Pages.Count > 0 ? context.Pages[0].Tracks.Count : 0);

        return context;
    }

    /// <summary>
    /// Fetches time-synced lyrics for a track from Spotify's color-lyrics API.
    /// </summary>
    /// <param name="trackId">Track ID in base62 format (e.g., "4xeugB5MqWh0jwvXZPxahq").</param>
    /// <param name="imageUri">Album image URI (e.g., "spotify:image:ab67616d00001e02...").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Lyrics response with timed lines, or null if no lyrics available.</returns>
    public async Task<LyricsResponse?> GetLyricsAsync(
        string trackId,
        string imageUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageUri);

        // Convert HTTP image URL to spotify:image: format if needed
        // https://i.scdn.co/image/ab67616d00001e02xxx -> spotify:image:ab67616d00001e02xxx
        var normalizedImageUri = imageUri;
        if (imageUri.StartsWith("https://i.scdn.co/image/", StringComparison.OrdinalIgnoreCase))
        {
            var imageId = imageUri.Substring("https://i.scdn.co/image/".Length);
            normalizedImageUri = $"spotify:image:{imageId}";
        }

        // URL encode the image URI (spotify:image:xxx -> spotify%3Aimage%3Axxx)
        var encodedImageUri = Uri.EscapeDataString(normalizedImageUri);
        var url = $"{_baseUrl}/color-lyrics/v2/track/{trackId}/image/{encodedImageUri}?format=json&vocalRemoval=false&market=from_token";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Use Android platform which doesn't require client-token
        request.Headers.TryAddWithoutValidation("app-platform", "Android");
        request.Headers.TryAddWithoutValidation("spotify-app-version", "8.9.0");

        var response = await SendWithRetryAsync(request, cancellationToken);

        // 404 means no lyrics available for this track
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogDebug("No lyrics available for track {TrackId}", trackId);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        // Use source-generated context for AOT compatibility
        var lyrics = JsonSerializer.Deserialize(json, LyricsJsonContext.Default.LyricsResponse);

        _logger?.LogDebug("Fetched lyrics for track {TrackId}: syncType={SyncType}, lines={LineCount}",
            trackId,
            lyrics?.Lyrics?.SyncType ?? "none",
            lyrics?.Lyrics?.Lines.Count ?? 0);

        return lyrics;
    }

    /// <summary>
    /// Fetches the next page of tracks using the page URL from a ContextPage.
    /// </summary>
    /// <remarks>
    /// Page URLs typically use the hm:// scheme which needs to be stripped.
    /// </remarks>
    /// <param name="pageUrl">Page URL from ContextPage.PageUrl or NextPageUrl (hm://... format).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>ContextPage containing more tracks.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<Protocol.Context.ContextPage> GetNextPageAsync(
        string pageUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);

        // Strip "hm://" prefix if present
        var endpoint = pageUrl;
        if (endpoint.StartsWith("hm://", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = endpoint.Substring(5);
        }

        // Get access token
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{_baseUrl}/{endpoint}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        _logger?.LogDebug("Fetching next page: {Endpoint}", endpoint);

        var response = await SendWithRetryAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Page not found: {pageUrl}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        // Parse JSON response
        var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var page = Google.Protobuf.JsonParser.Default.Parse<Protocol.Context.ContextPage>(jsonContent);

        _logger?.LogDebug("Next page fetched: {TrackCount} tracks", page.Tracks.Count);

        return page;
    }

    #endregion

    #region Collection API (Library Sync)

    /// <summary>
    /// Gets a page of items from a user's collection (liked songs, albums, artists).
    /// </summary>
    /// <param name="username">Spotify username.</param>
    /// <param name="set">Collection set: "collection" (tracks), "albums", "artists".</param>
    /// <param name="paginationToken">Token for next page, null for first page.</param>
    /// <param name="limit">Maximum items per page (default 300).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>PageResponse with items and pagination info.</returns>
    public async Task<PageResponse> GetCollectionPageAsync(
        string username,
        string set,
        string? paginationToken = null,
        int limit = 300,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(set);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        // Build request body
        var request = new PageRequest
        {
            Username = username,
            Set = set,
            Limit = limit
        };
        if (!string.IsNullOrEmpty(paginationToken))
        {
            request.PaginationToken = paginationToken;
        }

        var url = $"{_baseUrl}/collection/v2/paging";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(CollectionContentType));

        var protobufBytes = request.ToByteArray();
        httpRequest.Content = new ByteArrayContent(protobufBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(CollectionContentType);

        var response = await SendWithRetryAsync(httpRequest, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Collection not found: {set}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var pageResponse = PageResponse.Parser.ParseFrom(responseBytes);

        _logger?.LogDebug("Collection page fetched: {Set}, items={Count}, hasMore={HasMore}",
            set, pageResponse.Items.Count, !string.IsNullOrEmpty(pageResponse.NextPageToken));

        return pageResponse;
    }

    /// <summary>
    /// Gets incremental changes to a collection since the last sync.
    /// </summary>
    /// <param name="username">Spotify username.</param>
    /// <param name="set">Collection set: "collection" (tracks), "albums", "artists".</param>
    /// <param name="lastSyncToken">Sync token from previous sync.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>DeltaResponse with changes and new sync token.</returns>
    public async Task<DeltaResponse> GetCollectionDeltaAsync(
        string username,
        string set,
        string lastSyncToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(set);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastSyncToken);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var request = new DeltaRequest
        {
            Username = username,
            Set = set,
            LastSyncToken = lastSyncToken
        };

        var url = $"{_baseUrl}/collection/v2/delta";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(CollectionContentType));

        var protobufBytes = request.ToByteArray();
        httpRequest.Content = new ByteArrayContent(protobufBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(CollectionContentType);

        var response = await SendWithRetryAsync(httpRequest, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Collection not found: {set}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var deltaResponse = DeltaResponse.Parser.ParseFrom(responseBytes);

        _logger?.LogDebug("Collection delta fetched: {Set}, deltaUpdatePossible={DeltaUpdatePossible}, changes={Count}",
            set, deltaResponse.DeltaUpdatePossible, deltaResponse.Items.Count);

        return deltaResponse;
    }

    /// <summary>
    /// Adds or removes items from a collection.
    /// </summary>
    /// <param name="username">Spotify username.</param>
    /// <param name="set">Collection set: "collection" (tracks), "albums", "artists".</param>
    /// <param name="items">Items to add/remove (use is_removed flag).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteCollectionAsync(
        string username,
        string set,
        IEnumerable<CollectionItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(set);
        ArgumentNullException.ThrowIfNull(items);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var request = new WriteRequest
        {
            Username = username,
            Set = set,
            ClientUpdateId = Guid.NewGuid().ToString("N")
        };
        request.Items.AddRange(items);

        var url = $"{_baseUrl}/collection/v2/write";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(CollectionContentType));

        var protobufBytes = request.ToByteArray();
        httpRequest.Content = new ByteArrayContent(protobufBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(CollectionContentType);

        var response = await SendWithRetryAsync(httpRequest, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Collection not found: {set}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.BadRequest:
                throw new SpClientException(SpClientFailureReason.RequestFailed, "Invalid write request");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        _logger?.LogDebug("Collection write completed: {Set}, items={Count}", set, request.Items.Count);
    }

    #endregion

    #region Playlist API

    /// <summary>
    /// Gets a playlist's content (metadata and optionally tracks).
    /// </summary>
    /// <remarks>
    /// Uses the playlist v2 API which returns protobuf SelectedListContent.
    /// The rootlist (spotify:user:{username}:rootlist) contains all user playlists.
    /// </remarks>
    /// <param name="playlistUri">Playlist URI (e.g., "spotify:playlist:xxx" or "spotify:user:xxx:rootlist").</param>
    /// <param name="decorate">Fields to include: revision, attributes, length, owner, capabilities.</param>
    /// <param name="start">Start index for tracks (0-based).</param>
    /// <param name="length">Number of tracks to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SelectedListContent with playlist data.</returns>
    public async Task<Protocol.Playlist.SelectedListContent> GetPlaylistAsync(
        string playlistUri,
        string[]? decorate = null,
        int? start = null,
        int? length = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);

        // Convert URI to path: "spotify:playlist:xxx" -> "playlist/xxx"
        // "spotify:user:xxx:rootlist" -> "user/xxx/rootlist"
        var path = playlistUri.Replace("spotify:", "").Replace(":", "/");

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        // Build URL with query params
        var queryParams = new List<string>();
        if (decorate != null && decorate.Length > 0)
        {
            queryParams.Add($"decorate={string.Join(",", decorate)}");
        }
        if (start.HasValue)
        {
            queryParams.Add($"from={start.Value}");
        }
        if (length.HasValue)
        {
            queryParams.Add($"length={length.Value}");
        }

        var queryString = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
        var url = $"{_baseUrl}/playlist/v2/{path}{queryString}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        _logger?.LogDebug("Fetching playlist: {Uri}", playlistUri);

        var response = await SendWithRetryAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Playlist not found: {playlistUri}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.Forbidden:
                throw new SpClientException(SpClientFailureReason.Unauthorized, $"No access to playlist: {playlistUri}");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var content = Protocol.Playlist.SelectedListContent.Parser.ParseFrom(responseBytes);

        _logger?.LogDebug("Playlist fetched: {Uri}, length={Length}, revision={HasRevision}",
            playlistUri, content.Length, content.Revision?.Length > 0);

        return content;
    }

    /// <summary>
    /// Gets changes to a playlist since a specific revision (for incremental sync).
    /// </summary>
    /// <param name="playlistUri">Playlist URI.</param>
    /// <param name="revision">Last known revision (from previous sync).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SelectedListContent with diff information.</returns>
    public async Task<Protocol.Playlist.SelectedListContent> GetPlaylistDiffAsync(
        string playlistUri,
        byte[] revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);
        ArgumentNullException.ThrowIfNull(revision);

        var path = playlistUri.Replace("spotify:", "").Replace(":", "/");
        var revisionStr = FormatRevision(revision);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{_baseUrl}/playlist/v2/{path}/diff?revision={revisionStr}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        _logger?.LogDebug("Fetching playlist diff: {Uri}, revision={Revision}", playlistUri, revisionStr);

        var response = await SendWithRetryAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Playlist not found: {playlistUri}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Protocol.Playlist.SelectedListContent.Parser.ParseFrom(responseBytes);
    }

    /// <summary>
    /// Sends changes to a playlist.
    /// </summary>
    /// <param name="playlistUri">Playlist URI.</param>
    /// <param name="changes">Changes to apply (adds, removes, moves, attribute updates).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SelectedListContent with updated state.</returns>
    public async Task<Protocol.Playlist.SelectedListContent> ChangePlaylistAsync(
        string playlistUri,
        Protocol.Playlist.ListChanges changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);
        ArgumentNullException.ThrowIfNull(changes);

        var path = playlistUri.Replace("spotify:", "").Replace(":", "/");

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{_baseUrl}/playlist/v2/{path}/changes";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        var protobufBytes = changes.ToByteArray();
        request.Content = new ByteArrayContent(protobufBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        _logger?.LogDebug("Sending playlist changes: {Uri}", playlistUri);

        var response = await SendWithRetryAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Playlist not found: {playlistUri}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.Forbidden:
                throw new SpClientException(SpClientFailureReason.Unauthorized, $"Cannot modify playlist: {playlistUri}");
            case HttpStatusCode.Conflict:
                throw new SpClientException(SpClientFailureReason.RequestFailed, "Playlist revision conflict - refetch and retry");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Protocol.Playlist.SelectedListContent.Parser.ParseFrom(responseBytes);
    }

    /// <summary>
    /// Formats a revision for the playlist API query string.
    /// </summary>
    /// <remarks>
    /// Revision format: First 4 bytes are an int32 counter, rest is a hash.
    /// Output: "{counter},{hash_hex}"
    /// </remarks>
    private static string FormatRevision(byte[] revision)
    {
        if (revision.Length < 4)
            return Convert.ToHexString(revision).ToLowerInvariant();

        var counter = BitConverter.ToInt32(revision, 0);
        var hash = Convert.ToHexString(revision.AsSpan(4)).ToLowerInvariant();
        return $"{counter},{hash}";
    }

    #endregion

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