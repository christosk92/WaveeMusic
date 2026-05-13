using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Diagnostics;
using Wavee.Core;
using Wavee.Core.Audio;
using Wavee.Core.Http.InspiredByMix;
using Wavee.Core.Http.Lyrics;
using Wavee.Core.Http.Presence;
using Wavee.Core.Http.RadioApollo;
using Wavee.Core.Session;
using Wavee.Protocol.Collection;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Playplay;
using Wavee.Protocol.Resumption;
using Wavee.Protocol.Storage;

namespace Wavee.Core.Http;

/// <summary>
/// Client for Spotify's spclient HTTP API (metadata, lyrics, context, etc).
/// </summary>
/// <remarks>
/// Requires access tokens from login5.
/// Endpoints are resolved dynamically via ApResolver.
/// </remarks>
public sealed class SpClient : ISpClient
{
    private readonly string _baseUrl;
    private const int MaxRetries = 3;
    private const string CollectionContentType = "application/vnd.collection-v2.spotify.proto";

    // Spotify's first-party desktop-client identity strings, captured from
    // Fiddler. Required by /playlist/v2/.../signals — without the matching
    // Spotify-App-Version / App-Platform / spotify-playlist-sync-reason set
    // the gateway routes to a passive read-only handler that 200-OKs but
    // never runs the signal pipeline. Keep these in one place so any future
    // endpoint that needs the same identity can reuse them.
    private const string SpotifyAppVersion = "128800483";
    // Always identify as x86_64 — Spotify ships an x64 binary even on ARM
    // Windows hosts (runs under x64 emulation), so Spotify's server allowlist
    // for App-Platform / play-history attribution only knows Win32_x86_64.
    // Any other value (e.g. Win32_ARM64) routes the request to a non-attribut-
    // ing read-only handler. See SpotifyClientIdentity.GetUserAgent for the
    // matching OS descriptor that uses x64[native:ARM] on ARM hosts.
    private static readonly string SpotifyClientUserAgent = SpotifyClientIdentity.GetUserAgent();

    private readonly ISession _session;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly ClientTokenManager? _clientTokenManager;
    private readonly IRemoteStateRecorder? _remoteStateRecorder;
    private const string ExtendedMetadataContentType = "application/protobuf";
    private const string PlayerMetadataClientFeatureId = "player_mdata";

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
    internal SpClient(ISession session, HttpClient httpClient, string baseUrl,
        ClientTokenManager? clientTokenManager = null, ILogger? logger = null,
        IRemoteStateRecorder? remoteStateRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _session = session;
        _httpClient = httpClient;
        _clientTokenManager = clientTokenManager;
        _logger = logger;
        _remoteStateRecorder = remoteStateRecorder;

        // Normalize base URL: remove port suffix and ensure https:// prefix
        var hostOnly = baseUrl.Split(':')[0];
        _baseUrl = $"https://{hostOnly}";
    }

    /// <summary>
    /// POSTs a batched playback-event payload to gabo-receiver-service. The
    /// payload is a serialized <c>spotify.event_sender.PublishEventsRequest</c>;
    /// inside it lives one or more <c>EventEnvelope</c>s carrying RawCoreStream /
    /// AudioSessionEvent / etc.
    /// </summary>
    /// <remarks>
    /// This is what populates Spotify's Recently Played + play counts. The
    /// older <c>event-service/v1/events</c> path (HTTPS and Mercury both) is
    /// dead — Spotify routes play history exclusively through gabo now.
    /// Headers mirror the desktop client byte-for-byte; missing client-token /
    /// App-Platform / Spotify-App-Version is enough for Spotify to route the
    /// request to a passive handler that 200-OKs but never ingests.
    /// </remarks>
    /// <param name="body">Serialized PublishEventsRequest bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PostGaboEventAsync(
        byte[] body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = $"{_baseUrl}/gabo-receiver-service/v3/events/";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.TryAddWithoutValidation("App-Platform", "Win32_x86_64");
        request.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyAppVersion);
        request.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        request.Headers.TryAddWithoutValidation("Origin", _baseUrl);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        if (_clientTokenManager != null)
        {
            try
            {
                var ctok = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(ctok))
                    request.Headers.TryAddWithoutValidation("client-token", ctok);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "client-token fetch failed for gabo POST — continuing without");
            }
        }

        // Desktop sends Content-Encoding: gzip on every gabo POST (verified in
        // 032_c.txt headers from the SAZ capture). Match the wire to avoid
        // any gzip-only validation path on Spotify's ingestion side.
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                await gz.WriteAsync(body, cancellationToken);
            compressed = ms.ToArray();
        }

        var content = new ByteArrayContent(compressed);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");
        request.Content = content;

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("gabo-receiver returned {Status} for {Bytes}-byte body ({Compressed} gzipped)",
                    response.StatusCode, body.Length, compressed.Length);
            }
            else
            {
                _logger?.LogDebug("gabo event delivered: {Bytes} bytes ({Compressed} gzipped), status={Status}",
                    body.Length, compressed.Length, response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "gabo POST failed");
        }
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
    /// Modern Spotify clients fetch this through extended-metadata EPISODE_V4;
    /// the legacy /metadata/4/episode/{hex} route returns 404 for some valid
    /// episodes.
    /// </remarks>
    /// <param name="episodeId">Spotify episode ID. Accepts spotify:episode URI, 22-char base62, or 32-char hex.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protobuf-encoded episode metadata.</returns>
    /// <exception cref="SpClientException">Thrown if the request fails.</exception>
    public async Task<byte[]> GetEpisodeMetadataAsync(
        string episodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeId);

        var entityUri = NormalizeEpisodeMetadataUri(episodeId);
        return await GetSingleExtendedMetadataAsync(entityUri, ExtensionKind.EpisodeV4, cancellationToken);
    }

    private static string NormalizeEpisodeMetadataUri(string episodeId)
    {
        var id = episodeId.Trim();
        if (id.StartsWith("spotify:episode:", StringComparison.OrdinalIgnoreCase))
            return $"spotify:episode:{SpotifyId.FromUri(id).ToBase62()}";

        if (id.Length == SpotifyId.RawLength * 2 && id.All(static c => Uri.IsHexDigit(c)))
            return $"spotify:episode:{SpotifyId.FromBase16(id, SpotifyIdType.Episode).ToBase62()}";

        if (id.Length == SpotifyId.Base62Length)
            return $"spotify:episode:{SpotifyId.FromBase62(id, SpotifyIdType.Episode).ToBase62()}";

        return id;
    }

    private async Task<byte[]> GetSingleExtendedMetadataAsync(
        string entityUri,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken)
    {
        var countryCode = await _session.GetCountryCodeAsync(cancellationToken);
        var accountType = await _session.GetAccountTypeAsync(cancellationToken);
        var catalogue = accountType switch
        {
            AccountType.Premium => "premium",
            AccountType.Family => "premium",
            AccountType.Free => "free",
            AccountType.Artist => "premium",
            _ => "premium"
        };

        var requestBody = new BatchedEntityRequest
        {
            Header = new BatchedEntityRequestHeader
            {
                Country = countryCode,
                Catalogue = catalogue,
                TaskId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16))
            }
        };

        var entityRequest = new EntityRequest { EntityUri = entityUri };
        entityRequest.Query.Add(new ExtensionQuery { ExtensionKind = extensionKind });
        requestBody.EntityRequest.Add(entityRequest);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = $"{_baseUrl}/extended-metadata/v0/extended-metadata";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Version = HttpVersion.Version11;
        httpRequest.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ExtendedMetadataContentType));
        httpRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        httpRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        httpRequest.Headers.Connection.Add("keep-alive");
        httpRequest.Headers.TryAddWithoutValidation("Accept-Language", GetMetadataRequestLanguage());
        httpRequest.Headers.TryAddWithoutValidation("App-Platform", SpotifyClientIdentity.AppPlatform);
        httpRequest.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyClientIdentity.AppVersionHeader);
        httpRequest.Headers.TryAddWithoutValidation("client-feature-id", PlayerMetadataClientFeatureId);
        httpRequest.Headers.TryAddWithoutValidation("Origin", _baseUrl);
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        httpRequest.Headers.TryAddWithoutValidation("User-Agent", SpotifyClientIdentity.GetUserAgent());

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(clientToken))
                    httpRequest.Headers.TryAddWithoutValidation("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get client token for extended metadata request, continuing without");
            }
        }

        httpRequest.Content = new ByteArrayContent(requestBody.ToByteArray());
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(ExtendedMetadataContentType);

        var httpResponse = await SendWithRetryAsync(httpRequest, cancellationToken);
        switch (httpResponse.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Extended metadata not found: {entityUri}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.BadRequest:
                throw new SpClientException(SpClientFailureReason.RequestFailed, $"Invalid extended metadata request: {entityUri}");
            case HttpStatusCode.TooManyRequests:
                throw new SpClientException(SpClientFailureReason.RateLimited, "Rate limit exceeded");
        }

        if ((int)httpResponse.StatusCode >= 500)
        {
            throw new SpClientException(
                SpClientFailureReason.ServerError,
                $"Server error: {httpResponse.StatusCode}");
        }

        httpResponse.EnsureSuccessStatusCode();

        var responseBytes = await ExtendedMetadataClient.ReadResponseBytesAsync(httpResponse, cancellationToken);
        var response = BatchedExtensionResponse.Parser.ParseFrom(responseBytes);
        var extensionData = response.GetExtensionData(entityUri, extensionKind);
        if (extensionData?.ExtensionData is null)
            throw new SpClientException(SpClientFailureReason.NotFound, $"Extended metadata not found: {entityUri}");

        return extensionData.ExtensionData.Value.ToByteArray();
    }

    private string GetMetadataRequestLanguage()
    {
        var locale = GetEffectiveLocale();
        if (string.IsNullOrWhiteSpace(locale))
            return "en";

        var separatorIndex = locale.IndexOfAny(['-', '_']);
        var language = separatorIndex > 0 ? locale[..separatorIndex] : locale;
        return language.ToLowerInvariant();
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

    /// <inheritdoc />
    public async Task<byte[]> ResolvePlayPlayObfuscatedKeyAsync(
        FileId fileId,
        ReadOnlyMemory<byte> playPlayToken = default,
        CancellationToken cancellationToken = default)
    {
        if (playPlayToken.IsEmpty)
            throw new ArgumentException("playPlayToken is required", nameof(playPlayToken));
        var token = playPlayToken.ToArray();
        if (!fileId.IsValid)
            throw new ArgumentException("FileId is not valid", nameof(fileId));

        var url = $"{_baseUrl}/playplay/v1/key/{fileId.ToBase16()}";

        // 403 is a soft failure; brief exponential backoff caps total wait at ~7s.
        const int playPlayMaxAttempts = 3;
        Exception? lastError = null;

        for (int attempt = 0; attempt < playPlayMaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken);
            }

            var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

            var body = new PlayPlayLicenseRequest
            {
                Version = 5,
                Token = ByteString.CopyFrom(token),
                Interactivity = Interactivity.Interactive,
                ContentType = ContentType.AudioTrack,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));

            // Use the first-party desktop client identity for proper attribution.
            request.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
            request.Headers.TryAddWithoutValidation("App-Platform", SpotifyClientIdentity.AppPlatform);
            request.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyAppVersion);

            if (_clientTokenManager != null)
            {
                try
                {
                    var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(clientToken))
                        request.Headers.Add("client-token", clientToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to get client token, continuing without");
                }
            }

            request.Content = new ByteArrayContent(body.ToByteArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

            var response = await SendWithRetryAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                lastError = new SpClientException(
                    SpClientFailureReason.Unauthorized,
                    $"PlayPlay license forbidden for {fileId.ToBase16()}");
                _logger?.LogWarning(
                    "PlayPlay license 403 for {FileId} (attempt {Attempt}/{Max})",
                    fileId.ToBase16(), attempt + 1, playPlayMaxAttempts);
                continue;
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    throw new SpClientException(
                        SpClientFailureReason.NotFound,
                        $"PlayPlay license not found for {fileId.ToBase16()}");
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

            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var parsed = PlayPlayLicenseResponse.Parser.ParseFrom(responseBytes);

            if (parsed.ObfuscatedKey is null || parsed.ObfuscatedKey.Length != 16)
            {
                throw new SpClientException(
                    SpClientFailureReason.InvalidResponse,
                    $"PlayPlay response had unexpected obfuscated_key length: {parsed.ObfuscatedKey?.Length ?? 0}");
            }

            _logger?.LogDebug(
                "Resolved PlayPlay obfuscated key for {FileId} (attempts={Attempts})",
                fileId.ToBase16(), attempt + 1);

            return parsed.ObfuscatedKey.ToByteArray();
        }

        throw lastError ?? new SpClientException(
            SpClientFailureReason.Unauthorized,
            $"PlayPlay license forbidden for {fileId.ToBase16()}");
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

        var startTicks = Environment.TickCount64;

        // Get access token (auto-refreshes if needed)
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{_baseUrl}/connect-state/v1/devices/{deviceId}";

        // Build request — match the desktop client's wire envelope exactly so
        // server-side handlers route us to the full play-history pipeline:
        //   - Content-Type "application/protobuf" (no x- prefix)
        //   - Body gzip-compressed and signaled via X-Transfer-Encoding (not
        //     Content-Encoding — this is the non-standard header desktop uses)
        //   - app-platform / spotify-app-version / desktop User-Agent / Origin
        //   - client-token alongside the bearer token
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.Add("X-Spotify-Connection-Id", connectionId);
        httpRequest.Headers.Add("X-Transfer-Encoding", "gzip");
        httpRequest.Headers.Add("App-Platform", SpotifyClientIdentity.AppPlatform);
        httpRequest.Headers.Add("Spotify-App-Version", SpotifyClientIdentity.AppVersionHeader);
        httpRequest.Headers.UserAgent.ParseAdd(SpotifyClientIdentity.GetUserAgent());
        httpRequest.Headers.AcceptLanguage.ParseAdd(GetEffectiveLocale() ?? "en");
        httpRequest.Headers.Add("Origin", _baseUrl);
        httpRequest.Headers.Add("Sec-Fetch-Site", "same-origin");
        httpRequest.Headers.Add("Sec-Fetch-Mode", "no-cors");
        httpRequest.Headers.Add("Sec-Fetch-Dest", "empty");

        // client-token header — tied to the same identity the access token
        // belongs to. Best-effort: if it fails, the bearer alone is enough for
        // the PUT to succeed at the wire level (still HTTP 200), but pairing
        // both matches what desktop emits.
        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    httpRequest.Headers.Add("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "PutConnectState: failed to obtain client-token, continuing without");
            }
        }

        if (_remoteStateRecorder != null)
        {
            string? json = null;
            string? notes = null;
            try
            {
                json = Google.Protobuf.JsonFormatter.Default.Format(request);
            }
            catch (Exception ex)
            {
                notes = $"failed to format PutState request: {ex.Message}";
            }

            _remoteStateRecorder.Record(
                kind: RemoteStateEventKind.PutStateRequest,
                direction: RemoteStateDirection.Outbound,
                summary: $"PutState reason={request.PutStateReason} active={request.IsActive} track={request.Device?.PlayerState?.Track?.Uri ?? "<none>"} pos={request.Device?.PlayerState?.Position ?? 0}ms",
                correlationId: request.MessageId.ToString(),
                jsonBody: json,
                notes: notes);
        }

        // Serialize + gzip the protobuf body
        var protobufBytes = request.ToByteArray();
        using var gzippedBody = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(gzippedBody, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(protobufBytes, 0, protobufBytes.Length);
        }
        var gzippedBytes = gzippedBody.ToArray();
        httpRequest.Content = new ByteArrayContent(gzippedBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");

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
        var elapsedMs = Environment.TickCount64 - startTicks;

        if (_remoteStateRecorder != null)
        {
            string? json = null;
            string? notes = null;
            try
            {
                var cluster = Wavee.Protocol.Player.Cluster.Parser.ParseFrom(responseBody);
                json = Google.Protobuf.JsonFormatter.Default.Format(cluster);
            }
            catch (Exception ex)
            {
                notes = $"failed to parse Cluster: {ex.Message}";
            }

            _remoteStateRecorder.Record(
                kind: RemoteStateEventKind.PutStateResponse,
                direction: RemoteStateDirection.Inbound,
                summary: $"PutState response corrId={request.MessageId} bytes={responseBody.Length} elapsedMs={elapsedMs}",
                correlationId: request.MessageId.ToString(),
                elapsedMs: elapsedMs,
                payloadBytes: responseBody.Length,
                jsonBody: json,
                notes: notes);
        }

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

        // Add client-token header for spclient authentication
        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get client token, continuing without");
            }
        }

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

    /// <inheritdoc />
    public async Task<Protocol.Context.Context> ResolveAutoplayAsync(
        string contextUri,
        IReadOnlyList<string> recentTrackUris,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextUri);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        // POST /context-resolve/v1/autoplay with an AutoplayContextRequest
        // protobuf body. The prior GET-with-$-separated-path format (ported from
        // old librespot) is stale — current Spotify rejects it. Request shape
        // matches Spotify 1.2.52.442's player.proto.
        var url = $"{_baseUrl}/context-resolve/v1/autoplay";

        var body = new Protocol.Playback.AutoplayContextRequest
        {
            ContextUri = contextUri,
            IsVideo = false
        };
        foreach (var uri in recentTrackUris)
        {
            if (!string.IsNullOrEmpty(uri))
                body.RecentTrackUri.Add(uri);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");
        request.Content = new ByteArrayContent(body.ToByteArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        _logger?.LogDebug("Resolving autoplay for context: {ContextUri} with {TrackCount} recent tracks",
            contextUri, recentTrackUris.Count);

        var response = await SendWithRetryAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new SpClientException(SpClientFailureReason.NotFound, $"Autoplay not available for: {contextUri}");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
        if ((int)response.StatusCode >= 500)
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrEmpty(jsonContent))
            throw new SpClientException(SpClientFailureReason.NotFound, $"Autoplay returned empty body for: {contextUri}");

        var context = Google.Protobuf.JsonParser.Default.Parse<Protocol.Context.Context>(jsonContent);

        _logger?.LogDebug("Autoplay resolved: {Uri}, tracks={TrackCount}",
            context.Uri,
            context.Pages.Count > 0 ? context.Pages[0].Tracks.Count : 0);

        return context;
    }

    /// <inheritdoc />
    public async Task<RadioApolloResponse> GetRadioApolloAutoplayAsync(
        string seedTrackId,
        IReadOnlyList<string> prevTrackIds,
        int count = 50,
        int pageNum = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedTrackId);

        // Path contains a Spotify URI with literal colons. RFC 3986 allows
        // colons inside path segments (only forbidden at the start of the
        // first path segment), so we don't escape them — matches what desktop
        // sends on the wire.
        var prevTracksCsv = string.Join(',', prevTrackIds.Where(s => !string.IsNullOrEmpty(s)));
        var salt = Random.Shared.Next(100_000, 999_999);
        var url =
            $"{_baseUrl}/radio-apollo/v3/tracks/spotify:station:track:{seedTrackId}" +
            $"?salt={salt}&autoplay=true&count={count}&isVideo=false" +
            $"&prev_tracks={prevTracksCsv}&pageNum={pageNum}&minimal=true";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        _logger?.LogDebug(
            "Resolving radio-apollo autoplay for seed: spotify:station:track:{Seed} with {PrevCount} prev tracks",
            seedTrackId, prevTrackIds.Count);

        var response = await SendWithRetryAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new SpClientException(SpClientFailureReason.NotFound, $"Radio-apollo not available for: {seedTrackId}");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
        if ((int)response.StatusCode >= 500)
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrEmpty(json))
            throw new SpClientException(SpClientFailureReason.NotFound, $"Radio-apollo returned empty body for: {seedTrackId}");

        var raw = JsonSerializer.Deserialize(json, RadioApolloJsonContext.Default.RadioApolloRawResponse)
                  ?? throw new SpClientException(SpClientFailureReason.ServerError, "Radio-apollo returned malformed JSON");

        var tracks = (raw.Tracks ?? new List<RadioApolloRawTrack>())
            .Where(t => !string.IsNullOrEmpty(t.Uri))
            .Select(t => new RadioApolloTrack(t.Uri!, t.Uid, t.Metadata?.DecisionId))
            .ToList();

        _logger?.LogDebug(
            "Radio-apollo resolved: tracks={Count}, nextPage={HasNext}",
            tracks.Count, !string.IsNullOrEmpty(raw.NextPageUrl));

        return new RadioApolloResponse(tracks, raw.NextPageUrl, raw.CorrelationId);
    }

    /// <inheritdoc />
    public async Task<string?> GetInspiredByMixPlaylistAsync(
        string seedUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedUri);

        // Spotify accepts literal colons in the path (e.g. spotify:artist:<id>) —
        // RFC 3986 allows ':' inside path segments. Same handling as radio-apollo.
        var url = $"{_baseUrl}/inspiredby-mix/v2/seed_to_playlist/{seedUri}?response-format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        _logger?.LogDebug("Resolving inspired-by-mix playlist for seed: {Seed}", seedUri);

        var response = await SendWithRetryAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
        if ((int)response.StatusCode >= 500)
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        var json = await ReadStringContentLenientAsync(response.Content, cancellationToken);
        if (string.IsNullOrEmpty(json)) return null;

        var raw = JsonSerializer.Deserialize(json, InspiredByMixJsonContext.Default.InspiredByMixRawResponse);
        var playlistUri = raw?.MediaItems?.FirstOrDefault(m => !string.IsNullOrEmpty(m.Uri))?.Uri;

        _logger?.LogDebug("Inspired-by-mix resolved: seed={Seed} → {Playlist}",
            seedUri, playlistUri ?? "<none>");

        return playlistUri;
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

        // Lyrics endpoint doesn't require client-token when using Android platform.
        // Keep the Android platform header (lyrics CDN differentiates) but align the
        // version with current desktop so we're not flagged as a stale mobile build.
        request.Headers.TryAddWithoutValidation("app-platform", "Android");
        request.Headers.TryAddWithoutValidation("spotify-app-version", SpotifyClientIdentity.AppVersionHeader);

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

    /// <inheritdoc cref="ISpClient.ListCurrentStatesAsync"/>
    public async Task<ListCurrentStatesResponse> ListCurrentStatesAsync(
        DateTimeOffset updatedAfter,
        int limit = 1021,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var timestamp = updatedAfter
            .UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var request = new ListCurrentStatesRequest
        {
            Limit = limit,
            Filter = $"cs.resume_point_revisions.exists(revision, revision.update_time > timestamp('{timestamp}'))"
        };

        var url = $"{_baseUrl}/herodotus/spotify.resumption.v1.CurrentStateService/ListCurrentStates";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.AcceptLanguage.ParseAdd("en");
        httpRequest.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        httpRequest.Headers.TryAddWithoutValidation("App-Platform", SpotifyClientIdentity.AppPlatform);
        httpRequest.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyAppVersion);
        httpRequest.Headers.TryAddWithoutValidation("Origin", _baseUrl);
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    httpRequest.Headers.TryAddWithoutValidation("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "ListCurrentStates: failed to obtain client-token, continuing without");
            }
        }

        httpRequest.Content = new ByteArrayContent(request.ToByteArray());
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var response = await SendWithRetryAsync(httpRequest, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.TooManyRequests:
                throw new SpClientException(SpClientFailureReason.RateLimited, "Rate limit exceeded");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var payload = ListCurrentStatesResponse.Parser.ParseFrom(bytes);
        _logger?.LogDebug(
            "Herodotus current states fetched: states={Count}, updatedAfter={UpdatedAfter:o}",
            payload.States.Count,
            updatedAfter);
        return payload;
    }

    /// <inheritdoc cref="ISpClient.CreateResumePointRevisionAsync"/>
    public async Task<CreateResumePointRevisionResponse> CreateResumePointRevisionAsync(
        string episodeUri,
        TimeSpan? resumePosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeUri);
        if (!episodeUri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            episodeUri = $"spotify:episode:{episodeUri}";

        var revisionValue = new CurrentStateValue();
        if (resumePosition.HasValue)
        {
            revisionValue.ResumePoint = new ResumePoint
            {
                PositionSeconds = (uint)Math.Max(0, Math.Round(resumePosition.Value.TotalSeconds))
            };
        }

        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var request = new CreateResumePointRevisionRequest
        {
            EntityUri = episodeUri,
            Revision = new CurrentStateRevision
            {
                Value = revisionValue,
                CreateTime = now
            }
        };

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = $"{_baseUrl}/herodotus/spotify.resumption.v1.ResumePointRevisionService/CreateResumePointRevision";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        httpRequest.Headers.AcceptLanguage.ParseAdd("en");
        httpRequest.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        httpRequest.Headers.TryAddWithoutValidation("App-Platform", SpotifyClientIdentity.AppPlatform);
        httpRequest.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyAppVersion);
        httpRequest.Headers.TryAddWithoutValidation("Origin", _baseUrl);
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        httpRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    httpRequest.Headers.TryAddWithoutValidation("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "CreateResumePointRevision: failed to obtain client-token, continuing without");
            }
        }

        httpRequest.Content = new ByteArrayContent(request.ToByteArray());
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var response = await SendWithRetryAsync(httpRequest, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.TooManyRequests:
                throw new SpClientException(SpClientFailureReason.RateLimited, "Rate limit exceeded");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var payload = CreateResumePointRevisionResponse.Parser.ParseFrom(bytes);
        _logger?.LogDebug(
            "Herodotus resume point saved: episode={EpisodeUri}, position={PositionSeconds}",
            episodeUri,
            resumePosition.HasValue ? Math.Round(resumePosition.Value.TotalSeconds) : null);
        return payload;
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

        // URL-shape matches Spotify's first-party client:
        //   ?revision=<escaped>&handlesContent=&hint_revision=<escaped>
        // The comma in "counter,hash" MUST be percent-encoded (%2C); the
        // gateway rejects unencoded commas with a non-standard 509 even
        // though RFC 3986 allows them in query components. `handlesContent=`
        // is required (empty value). `hint_revision` reuses `revisionStr`
        // on the first pass — we don't track the prior revision locally;
        // Spotify only seems to validate the pair's presence, not their
        // relation.
        var encodedRevision = Uri.EscapeDataString(revisionStr);
        var url = $"{_baseUrl}/playlist/v2/{path}/diff?revision={encodedRevision}&handlesContent=&hint_revision={encodedRevision}";

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
            // Diff endpoint commonly returns 509 on editorial mixes in our logs —
            // dump the server's reason phrase + a short slice of the response body
            // so we can tell whether it's "revision too stale", a protobuf error
            // envelope, or a real server fault. Debug-level on purpose; production
            // can drop this once understood.
            string? bodyPreview = null;
            try
            {
                var errBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (errBytes.Length > 0)
                {
                    // Try UTF-8 first (Spotify error envelopes are usually JSON/text);
                    // fall back to hex of first 64 bytes for binary payloads.
                    var asText = System.Text.Encoding.UTF8.GetString(errBytes);
                    bodyPreview = IsProbablyPrintable(asText)
                        ? asText.Length > 512 ? asText[..512] + "…" : asText
                        : Convert.ToHexString(errBytes.AsSpan(0, Math.Min(64, errBytes.Length))).ToLowerInvariant();
                }
            }
            catch { /* already failing; don't compound */ }
            _logger?.LogWarning(
                "Diff endpoint failed: status={Status} reason='{Reason}' uri={Uri} rev={Rev} body={Body}",
                (int)response.StatusCode, response.ReasonPhrase, playlistUri, revisionStr, bodyPreview ?? "<empty>");
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

        using var request = BuildPlaylistV2Request(url, accessToken.Token);
        request.Content = new ByteArrayContent(changes.ToByteArray());
        // Spotify's gateway expects form-urlencoded Content-Type on the playlist v2
        // mutation endpoints despite the body being binary protobuf — anything else
        // routes to a different (passive) handler.
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        if (_clientTokenManager != null)
            await TryAttachClientTokenAsync(request, cancellationToken);

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
        responseBytes = MaybeDecompressZstd(responseBytes, response.Content.Headers.ContentEncoding);
        return Protocol.Playlist.SelectedListContent.Parser.ParseFrom(responseBytes);
    }

    /// <summary>
    /// Spotify returns playlist /changes responses as zstd-compressed protobuf
    /// (Content-Encoding: zstd). HttpClient doesn't auto-decompress zstd unless
    /// the handler explicitly enables it; do it here so we don't need to fiddle
    /// with the shared handler config. If the response isn't zstd-encoded we
    /// pass the bytes through unchanged.
    /// </summary>
    private static byte[] MaybeDecompressZstd(byte[] bytes, ICollection<string> contentEncoding)
    {
        var isZstd = contentEncoding.Contains("zstd", StringComparer.OrdinalIgnoreCase);
        // Belt-and-braces: also sniff the zstd magic in case the header was stripped
        // by something in the pipeline.
        if (!isZstd && bytes.Length >= 4 &&
            bytes[0] == 0x28 && bytes[1] == 0xB5 && bytes[2] == 0x2F && bytes[3] == 0xFD)
        {
            isZstd = true;
        }

        if (!isZstd) return bytes;

        using var input = new MemoryStream(bytes);
        using var decompressor = new ZstdSharp.DecompressionStream(input);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return output.ToArray();
    }

    /// <inheritdoc />
    public async Task<string> UploadPlaylistImageAsync(
        ReadOnlyMemory<byte> jpegBytes,
        CancellationToken cancellationToken = default)
    {
        if (jpegBytes.IsEmpty)
            throw new ArgumentException("jpegBytes must not be empty", nameof(jpegBytes));

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://image-upload.spotify.com/v4/playlist");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.Accept.ParseAdd("application/json");
        if (_clientTokenManager != null)
            await TryAttachClientTokenAsync(request, cancellationToken);

        request.Content = new ByteArrayContent(jpegBytes.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        _logger?.LogDebug("Uploading playlist image: {Bytes} bytes", jpegBytes.Length);

        var response = await SendWithRetryAsync(request, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.Forbidden:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Cannot upload playlist image");
        }
        if ((int)response.StatusCode >= 500)
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("uploadToken", out var tokenEl) || tokenEl.GetString() is not { Length: > 0 } token)
            throw new SpClientException(SpClientFailureReason.RequestFailed, "uploadToken missing from image-upload response");
        return token;
    }

    /// <inheritdoc />
    public async Task<byte[]> RegisterPlaylistImageAsync(
        string playlistId,
        string uploadToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadToken);

        // Accept either a raw 22-char playlist id or a full URI; the endpoint wants the bare id.
        var bareId = playlistId.StartsWith("spotify:playlist:", StringComparison.Ordinal)
            ? playlistId["spotify:playlist:".Length..]
            : playlistId;

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = $"{_baseUrl}/playlist/v2/playlist/{bareId}/register-image";

        using var request = BuildPlaylistV2Request(url, accessToken.Token);
        request.Headers.Accept.ParseAdd("application/json");
        // AOT-safe: anonymous-type Serialize trips IL2026/IL3050 under strict AOT.
        var payload = new JsonObject { ["uploadToken"] = uploadToken }.ToJsonString();
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        if (_clientTokenManager != null)
            await TryAttachClientTokenAsync(request, cancellationToken);

        _logger?.LogDebug("Registering playlist image: {Id}", bareId);

        var response = await SendWithRetryAsync(request, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Playlist not found: {bareId}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.Forbidden:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Cannot register playlist image");
        }
        if ((int)response.StatusCode >= 500)
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("picture", out var picEl) || picEl.GetString() is not { Length: > 0 } b64)
            throw new SpClientException(SpClientFailureReason.RequestFailed, "picture missing from register-image response");
        return Convert.FromBase64String(b64);
    }

    /// <inheritdoc />
    public async Task<Protocol.Playlist.CreateListReply> CreateEmptyPlaylistAsync(
        string name,
        string canonicalUsername,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalUsername);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = $"{_baseUrl}/playlist/v2/playlist";

        var body = new Protocol.Playlist.ListUpdateRequest
        {
            Attributes = new Protocol.Playlist.ListAttributes { Name = name },
            Info = new Protocol.Playlist.ChangeInfo
            {
                User = canonicalUsername,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        };

        using var request = BuildPlaylistV2Request(url, accessToken.Token);
        request.Content = new ByteArrayContent(body.ToByteArray());
        // Spotify's gateway expects form-urlencoded Content-Type on these endpoints
        // even though the body is binary protobuf — anything else routes wrong.
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        if (_clientTokenManager != null)
            await TryAttachClientTokenAsync(request, cancellationToken);

        _logger?.LogDebug("Creating empty playlist: {Name}", name);

        var response = await SendWithRetryAsync(request, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.Forbidden:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Cannot create playlist");
        }
        if ((int)response.StatusCode >= 500)
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Protocol.Playlist.CreateListReply.Parser.ParseFrom(responseBytes);
    }

    /// <inheritdoc />
    public async Task<Protocol.Playlist.SelectedListContent> PostRootlistChangesAsync(
        string canonicalUsername,
        Protocol.Playlist.ListChanges changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalUsername);
        ArgumentNullException.ThrowIfNull(changes);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = $"{_baseUrl}/playlist/v2/user/{canonicalUsername}/rootlist/changes";

        using var request = BuildPlaylistV2Request(url, accessToken.Token);
        request.Content = new ByteArrayContent(changes.ToByteArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        if (_clientTokenManager != null)
            await TryAttachClientTokenAsync(request, cancellationToken);

        _logger?.LogDebug("Posting rootlist changes for user: {User}", canonicalUsername);

        var response = await SendWithRetryAsync(request, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, "Rootlist not found");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.Forbidden:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Cannot modify rootlist");
            case HttpStatusCode.Conflict:
                throw new SpClientException(SpClientFailureReason.RequestFailed, "Rootlist revision conflict - refetch and retry");
        }
        if ((int)response.StatusCode >= 500)
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Protocol.Playlist.SelectedListContent.Parser.ParseFrom(responseBytes);
    }

    /// <summary>
    /// Builds an authenticated POST request to a /playlist/v2/* endpoint with the
    /// full first-party identity header set. Spotify's gateway gates these routes
    /// on a matching tuple of (Spotify-App-Version, App-Platform, User-Agent,
    /// spotify-playlist-sync-reason) — generic requests 200-OK against a read-only
    /// handler that doesn't actually mutate state. Same identity is used by
    /// <see cref="SendPlaylistSignalAsync"/>; reuse keeps it consistent.
    /// </summary>
    private HttpRequestMessage BuildPlaylistV2Request(string url, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.TryAddWithoutValidation("App-Platform", "Win32_ARM64");
        request.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyAppVersion);
        request.Headers.TryAddWithoutValidation("spotify-accept-geoblock", "dummy");
        request.Headers.TryAddWithoutValidation("spotify-dsa-mode-enabled", "false");
        request.Headers.TryAddWithoutValidation("spotify-playlist-sync-reason", "CAk=");
        request.Headers.TryAddWithoutValidation("Origin", _baseUrl);
        return request;
    }

    private async Task TryAttachClientTokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var clientToken = await _clientTokenManager!.GetClientTokenAsync(cancellationToken);
            if (!string.IsNullOrEmpty(clientToken))
                request.Headers.Add("client-token", clientToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get client token for playlist v2 request, continuing without");
        }
    }

    /// <summary>
    /// Sends a session-control-display chip selection to the playlist signals endpoint.
    /// The server re-personalises the playlist to the chosen option and returns a fresh
    /// <see cref="Protocol.Playlist.SelectedListContent"/>.
    /// </summary>
    /// <param name="playlistUri">Playlist URI (e.g. "spotify:playlist:37i...").</param>
    /// <param name="revision">Current 24-byte playlist revision from <c>CachedPlaylist.Revision</c>.</param>
    /// <param name="signalKey">
    /// Fully-formatted key, e.g.
    /// <c>session_control_display$&lt;group_id&gt;$&lt;option_key&gt;</c>.
    /// </param>
    /// <param name="requestId">Fresh GUID for correlation.</param>
    public async Task<Protocol.Playlist.SelectedListContent> SendPlaylistSignalAsync(
        string playlistUri,
        ReadOnlyMemory<byte> revision,
        string signalKey,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var path = playlistUri.Replace("spotify:", "").Replace(":", "/");

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{_baseUrl}/playlist/v2/{path}/signals";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

        // Mirror Spotify's first-party desktop POST verbatim. The gateway gates
        // /signals routing on the full identity (Content-Type, App-Platform,
        // Spotify-App-Version, spotify-playlist-sync-reason) — a request that
        // looks generic 200-OKs against a read-only handler that echoes the
        // playlist without recording the signal.
        request.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.TryAddWithoutValidation("App-Platform", "Win32_ARM64");
        request.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyAppVersion);
        request.Headers.TryAddWithoutValidation("spotify-accept-geoblock", "dummy");
        request.Headers.TryAddWithoutValidation("spotify-dsa-mode-enabled", "false");
        request.Headers.TryAddWithoutValidation("spotify-playlist-sync-reason", "CA8QAQ==");
        request.Headers.TryAddWithoutValidation("Origin", _baseUrl);

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get client token for playlist signal, continuing without");
            }
        }

        var body = new Protocol.PlaylistSignals.PlaylistSignalsRequest
        {
            Revision = ByteString.CopyFrom(revision.Span),
            Signal = new Protocol.PlaylistSignals.Signal
            {
                SignalKey = signalKey,
                Correlation = new Protocol.PlaylistSignals.CorrelationId
                {
                    RequestId = requestId
                }
            }
        };

        var protobufBytes = body.ToByteArray();
        request.Content = new ByteArrayContent(protobufBytes);
        // Body is binary protobuf — the form-urlencoded MIME is what Spotify's
        // gateway expects on this endpoint despite the body type. Anything
        // else routes to the wrong handler chain.
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
       // request.Headers.Accept.Clear();
       // request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _logger?.LogDebug("Sending playlist signal: {Uri} key={Key}", playlistUri, signalKey);

        var response = await SendWithRetryAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, $"Playlist not found: {playlistUri}");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.Forbidden:
                throw new SpClientException(SpClientFailureReason.Unauthorized, $"Cannot post signal: {playlistUri}");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var parsed = Protocol.Playlist.SelectedListContent.Parser.ParseFrom(responseBytes);

        // Diagnostic: how much state does Spotify ship in the /signals
        // response? We need to know whether it carries fresh items inline
        // (so we can apply them directly) or just an ack envelope (so we
        // still need to re-GET).
        var firstThree = parsed.Contents?.Items?.Take(3).Select(i => i.Uri ?? "<no-uri>")
            ?? Enumerable.Empty<string>();
        _logger?.LogInformation(
            "[signals-response] uri={Uri} bytes={Bytes} length={Length} hasContents={HasContents} itemCount={ItemCount} signals={Signals} first3={First3}",
            playlistUri,
            responseBytes.Length,
            parsed.Length,
            parsed.Contents != null,
            parsed.Contents?.Items?.Count ?? 0,
            parsed.Contents?.AvailableSignals?.Count ?? 0,
            string.Join(",", firstThree));

        return parsed;
    }

    /// <summary>
    /// Formats a revision for the playlist API query string.
    /// </summary>
    /// <remarks>
    /// Wire format: 4-byte BIG-ENDIAN int32 counter followed by a 20-byte
    /// server-computed SHA-1 hash (24 bytes total). Output is the
    /// canonical "{counter_decimal},{hash_lowercase_hex}" form Spotify's
    /// other clients send (e.g. "14,290adc01f15b96ab840355ee465c1657e8df437f").
    /// Earlier versions used <see cref="BitConverter.ToInt32(byte[], int)"/>
    /// which decodes as little-endian on x86/x64, producing nonsense
    /// counters like 234881024 instead of 14 — Spotify's diff endpoint
    /// usually still reconciles via the hash, but mismatched counters
    /// likely contributed to spurious "diff too stale" fallbacks.
    /// </remarks>
    private static bool IsProbablyPrintable(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        // Quick heuristic — if more than 90% of the first 128 chars are printable
        // ASCII (letters/digits/punct/whitespace), treat as text.
        int end = Math.Min(128, s.Length);
        int printable = 0;
        for (int i = 0; i < end; i++)
        {
            var c = s[i];
            if (c >= 0x20 && c < 0x7F || c == '\n' || c == '\r' || c == '\t') printable++;
        }
        return printable * 10 >= end * 9;
    }

    private static string FormatRevision(byte[] revision)
    {
        if (revision.Length < 4)
            return Convert.ToHexString(revision).ToLowerInvariant();

        var counter = BinaryPrimitives.ReadInt32BigEndian(revision.AsSpan(0, 4));
        var hash = Convert.ToHexString(revision.AsSpan(4)).ToLowerInvariant();
        return $"{counter},{hash}";
    }

    #endregion

    /// <summary>
    /// Fetches a user's profile via the spclient profile endpoint.
    /// Uses Login5 token + client-token (no public Web API, no 429 issues).
    /// </summary>
    public async Task<SpotifyUserProfile> GetUserProfileAsync(
        string username, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var url = $"{_baseUrl}/user-profile-view/v3/profile/{Uri.EscapeDataString(username)}?playlist_limit=10&artist_limit=10&episode_limit=10&market=from_token";
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Add client-token header
        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch { /* Continue without client-token */ }
        }

        var response = await SendWithRetryAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SpClientException(
                SpClientFailureReason.ServerError,
                $"Failed to get user profile: {response.StatusCode} - {body}");
        }

        var json = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(json, SpotifyUserProfileJsonContext.Default.SpotifyUserProfile, cancellationToken)
            ?? throw new SpClientException(SpClientFailureReason.InvalidResponse, "Empty profile response");
    }

    /// <summary>
    /// Fetches a user's following list via the spclient profile endpoint.
    /// </summary>
    public async Task<SpotifyFollowingResponse> GetUserFollowingAsync(
        string username, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var url = $"{_baseUrl}/user-profile-view/v3/profile/{Uri.EscapeDataString(username)}/following?market=from_token";
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch { }
        }

        var response = await SendWithRetryAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SpClientException(
                SpClientFailureReason.ServerError,
                $"Failed to get user following: {response.StatusCode} - {body}");
        }

        var json = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(json, SpotifyUserProfileJsonContext.Default.SpotifyFollowingResponse, cancellationToken)
            ?? throw new SpClientException(SpClientFailureReason.InvalidResponse, "Empty following response");
    }

    /// <inheritdoc />
    public Task<bool> FollowUserAsync(string usernameOrUri, CancellationToken cancellationToken = default)
        => SetUserFollowAsync(usernameOrUri, follow: true, cancellationToken);

    /// <inheritdoc />
    public Task<bool> UnfollowUserAsync(string usernameOrUri, CancellationToken cancellationToken = default)
        => SetUserFollowAsync(usernameOrUri, follow: false, cancellationToken);

    private async Task<bool> SetUserFollowAsync(string usernameOrUri, bool follow, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usernameOrUri);

        var id = usernameOrUri.StartsWith("spotify:user:", StringComparison.Ordinal)
            ? usernameOrUri["spotify:user:".Length..]
            : usernameOrUri;
        if (string.IsNullOrWhiteSpace(id)) return false;

        var url = $"https://api.spotify.com/v1/me/following?type=user&ids={Uri.EscapeDataString(id)}";
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(follow ? HttpMethod.Put : HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Content = new StringContent(string.Empty);

        var response = await SendWithRetryAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger?.LogWarning("{Verb} /me/following user={Id} failed: {Status} {Body}",
                follow ? "PUT" : "DELETE", id, response.StatusCode, body);
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    public async Task<long> GetPlaylistFollowerCountAsync(
        string playlistId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        // Endpoint accepts the bare base62 id; tolerate full URI by stripping prefix.
        var bareId = playlistId;
        const string prefix = "spotify:playlist:";
        if (bareId.StartsWith(prefix, StringComparison.Ordinal))
            bareId = bareId[prefix.Length..];

        var url = $"{_baseUrl}/popcount/v2/playlist/{Uri.EscapeDataString(bareId)}/count";
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch { }
        }

        var response = await SendWithRetryAsync(request, cancellationToken);

        // Hidden / unknown counts surface as 404 in this endpoint; treat as 0.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return 0;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SpClientException(
                SpClientFailureReason.ServerError,
                $"Failed to get playlist follower count: {response.StatusCode} - {body}");
        }

        // Response shape: { "count": "95038", "truncated": true, "rawCount": "95038" }
        // count is a string-encoded long. Prefer rawCount (un-truncated) when present.
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("rawCount", out var rawProp)
            && long.TryParse(rawProp.GetString(), out var rawCount))
            return rawCount;

        if (root.TryGetProperty("count", out var countProp)
            && long.TryParse(countProp.GetString(), out var count))
            return count;

        return 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpotifyPlaylistMember>> GetPlaylistMembersAsync(
        string playlistId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        var bareId = playlistId;
        const string prefix = "spotify:playlist:";
        if (bareId.StartsWith(prefix, StringComparison.Ordinal))
            bareId = bareId[prefix.Length..];

        var url = $"{_baseUrl}/playlist-permission/v1/playlist/{Uri.EscapeDataString(bareId)}/permission/members";
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch { }
        }

        var response = await SendWithRetryAsync(request, cancellationToken);

        // 401/403/404 mean we can't see the member list (non-owner viewing a
        // non-collab playlist, or the playlist doesn't exist). Treat as empty.
        if (response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound)
            return Array.Empty<SpotifyPlaylistMember>();

        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogDebug("GetPlaylistMembersAsync: non-success {Status} for {Id}", response.StatusCode, bareId);
            return Array.Empty<SpotifyPlaylistMember>();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        // Endpoint shape isn't publicly documented; parse defensively. Walk the
        // top-level looking for a "members" array (or any array of objects with
        // a username field) and synthesise an Owner row from "ownerUsername" if
        // present.
        var results = new List<SpotifyPlaylistMember>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? ownerUsername = null;
            if (root.TryGetProperty("ownerUsername", out var ownerProp)
                && ownerProp.ValueKind == JsonValueKind.String)
            {
                ownerUsername = ownerProp.GetString();
            }

            if (root.TryGetProperty("members", out var members)
                && members.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in members.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    var username = TryGetString(m, "username")
                                   ?? TryGetString(m, "userId");
                    if (string.IsNullOrEmpty(username)) continue;
                    results.Add(new SpotifyPlaylistMember
                    {
                        UserId = TryGetString(m, "userId") ?? username,
                        Username = username,
                        PermissionLevel = TryGetString(m, "permissionLevel") ?? "VIEWER",
                        DisplayName = TryGetString(m, "displayName"),
                        ImageUrl = TryGetString(m, "imageUrl"),
                    });
                }
            }

            if (!string.IsNullOrEmpty(ownerUsername)
                && !results.Any(m => string.Equals(m.Username, ownerUsername, StringComparison.OrdinalIgnoreCase)))
            {
                results.Insert(0, new SpotifyPlaylistMember
                {
                    UserId = ownerUsername!,
                    Username = ownerUsername!,
                    PermissionLevel = "OWNER",
                });
            }
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "GetPlaylistMembersAsync: failed to parse response for {Id}", bareId);
            return Array.Empty<SpotifyPlaylistMember>();
        }

        return results;

        static string? TryGetString(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out var prop)
               && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
    }

    /// <inheritdoc />
    public async Task<List<string>> GetArtistTopTrackExtensionsAsync(
        string artistUri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artistUri);

        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = $"{_baseUrl}/artistplaycontext/v1/page/spotify/artist-top-tracks-extensions/{Uri.EscapeDataString(artistUri)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await SendWithRetryAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogWarning("Failed to get artist top track extensions: {Status}", response.StatusCode);
            return [];
        }

        using var json = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken);

        var uris = new List<string>();
        if (doc.RootElement.TryGetProperty("tracks", out var tracks))
        {
            foreach (var track in tracks.EnumerateArray())
            {
                if (track.TryGetProperty("uri", out var uri) && uri.GetString() is { } u)
                    uris.Add(u);
            }
        }
        return uris;
    }

    /// <summary>
    /// Gets the current server timestamp from Spotify's melody time-sync endpoint.
    /// Used for clock offset estimation to improve playback position extrapolation.
    /// </summary>
    public async Task<long> GetMelodyTimeAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{_baseUrl}/melody/v1/time";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("timestamp").GetInt64();
    }

    /// <inheritdoc />
    public async Task<RecentlyPlayedResponse> GetRecentlyPlayedAsync(
        string userId,
        int limit = 50,
        string filter = "default,collection-new-episodes",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var filterParam = string.IsNullOrWhiteSpace(filter) ? "default,collection-new-episodes" : filter;
        var url = $"{_baseUrl}/recently-played/v3/user/{Uri.EscapeDataString(userId)}/recently-played?format=json&offset=0&limit={limit}&filter={Uri.EscapeDataString(filterParam)}&market=from_token";
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch { /* Continue without client-token */ }
        }

        var response = await SendWithRetryAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SpClientException(
                SpClientFailureReason.ServerError,
                $"Failed to get recently played: {response.StatusCode} - {body}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(stream, RecentlyPlayedJsonContext.Default.RecentlyPlayedResponse, cancellationToken)
            ?? throw new SpClientException(SpClientFailureReason.InvalidResponse, "Empty recently-played response");
    }

    /// <inheritdoc />
    public async Task<LikedSongsContentFiltersResult> GetLikedSongsContentFiltersAsync(
        string? ifNoneMatch = null,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{_baseUrl}/content-filter/v1/liked-songs?subjective=true&market=from_token";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        var locale = GetEffectiveLocale();
        if (!string.IsNullOrEmpty(locale))
        {
            request.Headers.AcceptLanguage.ParseAdd(locale);
        }

        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get client token for liked-songs content filters, continuing without");
            }
        }

        var response = await SendWithRetryAsync(request, cancellationToken);
        var etag = GetHeaderValue(response, "ETag");

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _logger?.LogDebug("Liked-songs content filters not modified");
            return new LikedSongsContentFiltersResult
            {
                ETag = etag ?? ifNoneMatch,
                IsNotModified = true
            };
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(
                    SpClientFailureReason.NotFound,
                    "Liked-songs content filters endpoint not found");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(
                    SpClientFailureReason.Unauthorized,
                    "Access token invalid or expired");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(
                SpClientFailureReason.ServerError,
                $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync(
            stream,
            LikedSongsContentFiltersJsonContext.Default.LikedSongsContentFiltersResponse,
            cancellationToken);

        if (payload == null)
        {
            throw new SpClientException(
                SpClientFailureReason.InvalidResponse,
                "Empty liked-songs content-filters response");
        }

        return new LikedSongsContentFiltersResult
        {
            Filters = payload.ContentFilters,
            ETag = etag,
            IsNotModified = false
        };
    }

    /// <inheritdoc />
    public async Task<FriendFeedEntry?> GetFriendPresenceAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var url = $"{_baseUrl}/presence-view/v1/user/{Uri.EscapeDataString(userId)}";
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get client token for friend presence, continuing without");
            }
        }

        var response = await SendWithRetryAsync(request, cancellationToken);

        // Friend no longer visible (private/unfollowed) — caller should drop their row.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            _logger?.LogDebug("Friend presence {UserId}: {Status} (removed)", userId, response.StatusCode);
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SpClientException(SpClientFailureReason.RateLimited, "Rate limit exceeded");
        if ((int)response.StatusCode >= 500)
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var entry = await JsonSerializer.DeserializeAsync(
            stream, FriendFeedJsonContext.Default.FriendFeedEntry, cancellationToken);

        if (entry == null)
        {
            _logger?.LogDebug("Friend presence {UserId}: empty body", userId);
            return null;
        }

        _logger?.LogDebug("Friend presence {UserId}: {Track} by {Artist}",
            userId, entry.Track?.Name ?? "<none>", entry.Track?.Artist?.Name ?? "<none>");
        return entry;
    }

    /// <inheritdoc />
    public async Task<FriendFeedResponse> GetFriendFeedAsync(
        string connectionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        var url = $"{_baseUrl}/presence-view/v2/init-friend-feed/{Uri.EscapeDataString(connectionId)}";
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"Wavee/{GetType().Assembly.GetName().Version}");

        if (_clientTokenManager != null)
        {
            try
            {
                var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(clientToken))
                    request.Headers.Add("client-token", clientToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get client token for friend feed, continuing without");
            }
        }

        var response = await SendWithRetryAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, "Friend feed endpoint not found");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.TooManyRequests:
                throw new SpClientException(SpClientFailureReason.RateLimited, "Rate limit exceeded");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync(
            stream, FriendFeedJsonContext.Default.FriendFeedResponse, cancellationToken);

        if (payload == null)
        {
            throw new SpClientException(SpClientFailureReason.InvalidResponse, "Empty friend-feed response");
        }

        _logger?.LogDebug("Friend feed fetched: {Count} entries", payload.Friends?.Count ?? 0);
        return payload;
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
            // If the caller's token is already cancelled on a retry pass,
            // skip straight to the throw. Continuing the loop here would
            // fire another SendAsync → another cancellation → another
            // first-chance exception on top of the one the caller already
            // saw. One OCE per cancelled request instead of MaxRetries.
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var response = await _httpClient.SendAsync(request, cancellationToken);

                // Only these are retryable status codes. Every other
                // response (including 4xx NotFound / Unauthorized / Forbidden)
                // is returned to the caller immediately so it can throw a
                // specific SpClientException with the right reason.
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancelled the request mid-flight — propagate without
                // retrying. Wrapping this in a retry loop turns one cancel
                // into MaxRetries first-chance OCE throws under a debugger.
                throw;
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

    private static async Task<string> ReadStringContentLenientAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
            return string.Empty;

        var charset = content.Headers.ContentType?.CharSet?.Trim().Trim('"');
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset)
            && !charset.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }

        return encoding.GetString(bytes);
    }

    /// <inheritdoc cref="ISpClient.GetVideoManifestAsync"/>
    public async Task<string> GetVideoManifestAsync(
        string manifestId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = $"{_baseUrl}/manifests/v9/json/sources/{manifestId}/options/supports_drm";
        _logger?.LogDebug("[DRM] Video manifest GET {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        // Mimic the desktop client's xpui webview request shape — the
        // /manifests endpoint is CORS-fenced and refuses requests that don't
        // present the same Origin/Referer pair the in-app webview sends.
        request.Headers.TryAddWithoutValidation("Origin", "https://xpui.app.spotify.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://xpui.app.spotify.com/");

        if (_clientTokenManager != null)
        {
            try
            {
                var ctok = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(ctok))
                    request.Headers.TryAddWithoutValidation("client-token", ctok);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[DRM] client-token fetch failed for video manifest - continuing without");
            }
        }

        var response = await SendWithRetryAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogWarning("[DRM] Video manifest failed status={StatusCode} reason={ReasonPhrase}",
                (int)response.StatusCode,
                response.ReasonPhrase);
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger?.LogDebug("[DRM] Video manifest response status={StatusCode} bytes={Bytes}",
            (int)response.StatusCode,
            json.Length);
        return json;
    }

    /// <inheritdoc cref="ISpClient.GetVideoSegmentBytesAsync"/>
    public async Task<byte[]> GetVideoSegmentBytesAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("Video segment URI must be absolute.", nameof(uri));

        _logger?.LogDebug("[DRM] Video segment GET {Url}", SanitizeLogUri(uri));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.TryAddWithoutValidation("Origin", "https://xpui.app.spotify.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://xpui.app.spotify.com/");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogWarning("[DRM] Video segment failed status={StatusCode} reason={ReasonPhrase} url={Url}",
                (int)response.StatusCode,
                response.ReasonPhrase,
                SanitizeLogUri(uri));
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        _logger?.LogDebug("[DRM] Video segment response status={StatusCode} bytes={Bytes} url={Url}",
            (int)response.StatusCode,
            bytes.Length,
            SanitizeLogUri(uri));
        return bytes;
    }

    /// <inheritdoc cref="ISpClient.PostPlayReadyLicenseAsync"/>
    public async Task<byte[]> PostPlayReadyLicenseAsync(
        byte[] challenge,
        string? licenseServerEndpoint = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = BuildWebgateUrl(licenseServerEndpoint ?? "/playready-license");
        _logger?.LogDebug("[DRM] PlayReady license POST endpoint={Endpoint} challengeBytes={Bytes} requestHeaders={HeaderCount}",
            licenseServerEndpoint ?? "/playready-license",
            challenge.Length,
            requestHeaders?.Count ?? 0);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.TryAddWithoutValidation("App-Platform", SpotifyClientIdentity.AppPlatform);
        request.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyAppVersion);
        request.Headers.TryAddWithoutValidation("Origin", "https://xpui.app.spotify.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://xpui.app.spotify.com/");

        if (_clientTokenManager != null)
        {
            try
            {
                var ctok = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(ctok))
                    request.Headers.TryAddWithoutValidation("client-token", ctok);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[DRM] client-token fetch failed for PlayReady license - continuing without");
            }
        }

        request.Content = new ByteArrayContent(challenge);
        if (requestHeaders is not null)
        {
            foreach (var header in requestHeaders)
            {
                if (string.IsNullOrWhiteSpace(header.Key) || string.IsNullOrWhiteSpace(header.Value))
                    continue;

                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (request.Content.Headers.ContentType is null)
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");

        var response = await SendWithRetryAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogWarning("[DRM] PlayReady license failed status={StatusCode} reason={ReasonPhrase}",
                (int)response.StatusCode,
                response.ReasonPhrase);
        }

        response.EnsureSuccessStatusCode();
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        _logger?.LogDebug("[DRM] PlayReady license response status={StatusCode} bytes={Bytes}",
            (int)response.StatusCode,
            responseBytes.Length);
        return responseBytes;
    }

    /// <inheritdoc cref="ISpClient.PostWidevineLicenseAsync"/>
    public async Task<byte[]> PostWidevineLicenseAsync(
        byte[] challenge,
        string? licenseServerEndpoint = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);
        var url = BuildWebgateUrl(licenseServerEndpoint ?? "/widevine-license/v1/video/license");
        _logger?.LogDebug("[DRM] Widevine license POST endpoint={Endpoint} challengeBytes={Bytes} requestHeaders={HeaderCount}",
            licenseServerEndpoint ?? "/widevine-license/v1/video/license",
            challenge.Length,
            requestHeaders?.Count ?? 0);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.UserAgent.ParseAdd(SpotifyClientUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.TryAddWithoutValidation("App-Platform", SpotifyClientIdentity.AppPlatform);
        request.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyAppVersion);
        request.Headers.TryAddWithoutValidation("Origin", "https://xpui.app.spotify.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://xpui.app.spotify.com/");

        if (_clientTokenManager != null)
        {
            try
            {
                var ctok = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(ctok))
                    request.Headers.TryAddWithoutValidation("client-token", ctok);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[DRM] client-token fetch failed for Widevine license - continuing without");
            }
        }

        request.Content = new ByteArrayContent(challenge);
        if (requestHeaders is not null)
        {
            foreach (var header in requestHeaders)
            {
                if (string.IsNullOrWhiteSpace(header.Key) || string.IsNullOrWhiteSpace(header.Value))
                    continue;

                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (request.Content.Headers.ContentType is null)
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await SendWithRetryAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogWarning("[DRM] Widevine license failed status={StatusCode} reason={ReasonPhrase}",
                (int)response.StatusCode,
                response.ReasonPhrase);
        }

        response.EnsureSuccessStatusCode();
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        _logger?.LogDebug("[DRM] Widevine license response status={StatusCode} bytes={Bytes}",
            (int)response.StatusCode,
            responseBytes.Length);
        return responseBytes;
    }

    private string BuildWebgateUrl(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return $"{_baseUrl}/playready-license";

        if (endpoint.StartsWith("https://@webgate", StringComparison.OrdinalIgnoreCase))
            return _baseUrl + endpoint["https://@webgate".Length..];

        if (endpoint.StartsWith("@webgate", StringComparison.OrdinalIgnoreCase))
            return _baseUrl + endpoint["@webgate".Length..];

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return endpoint[0] == '/'
            ? _baseUrl + endpoint
            : $"{_baseUrl}/{endpoint}";
    }

    private static string SanitizeLogUri(Uri uri)
    {
        var text = uri.ToString();
        var queryIndex = text.IndexOf('?');
        return queryIndex >= 0 ? text[..queryIndex] : text;
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string headerName)
    {
        if (response.Headers.TryGetValues(headerName, out var values))
        {
            foreach (var value in values)
            {
                return value;
            }
        }

        return null;
    }
}
