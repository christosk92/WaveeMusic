using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Session;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;
using ZstdSharp;

namespace Wavee.Core.Http;

/// <summary>
/// Client for Spotify's extended-metadata API with SQLite caching.
/// Handles fetching extension data for any entity type with automatic caching.
/// </summary>
public sealed class ExtendedMetadataClient : IExtendedMetadataClient
{
    private string? _baseUrl; // resolved lazily from session
    private readonly ISession _session;
    private readonly HttpClient _httpClient;
    private readonly IMetadataDatabase _database;
    private readonly ClientTokenManager _clientTokenManager;
    private readonly ILogger? _logger;

    private const int MaxRetries = 3;
    private const long DefaultTtlSeconds = 3600; // 1 hour fallback
    private const string ExtendedMetadataContentType = "application/protobuf";
    private const int AudioAssociationsExtensionKindValue = 98;
    private const int VideoAssociationsExtensionKindValue = 99;
    private const string CollectionMetadataClientFeatureId = "collection";
    private const string PlayerMetadataClientFeatureId = "player_mdata";
    private static readonly string DesktopUserAgent = SpotifyClientIdentity.GetUserAgent();

    /// <summary>
    /// Creates a new ExtendedMetadataClient.
    /// </summary>
    /// <param name="session">Spotify session (base URL resolved lazily from SpClient).</param>
    /// <param name="httpClient">HTTP client for requests.</param>
    /// <param name="database">Metadata database for caching.</param>
    /// <param name="logger">Optional logger.</param>
    public ExtendedMetadataClient(
        ISession session,
        HttpClient httpClient,
        IMetadataDatabase database,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(database);

        _session = session;
        _httpClient = httpClient;
        _database = database;
        _logger = logger;
        _clientTokenManager = new ClientTokenManager(httpClient, session.Config, logger);
    }

    private string GetBaseUrl()
    {
        if (_baseUrl != null) return _baseUrl;

        var raw = _session.SpClient.BaseUrl;
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            _baseUrl = raw.TrimEnd('/');
        else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            _baseUrl = "https" + raw.Substring(4).TrimEnd('/');
        else
            _baseUrl = $"https://{raw.Split(':')[0]}";

        return _baseUrl;
    }

    /// <summary>
    /// Gets extension data for a single entity, using cache when available.
    /// </summary>
    /// <param name="entityUri">The Spotify entity URI.</param>
    /// <param name="extensionKind">The extension kind to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The extension data bytes, or null if not found.</returns>
    public async Task<byte[]?> GetExtensionAsync(
        string entityUri,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken = default)
    {
        // Check cache first
        var cached = await _database.GetExtensionAsync(entityUri, extensionKind, cancellationToken);
        if (cached.HasValue)
        {
            _logger?.LogDebug("Cache hit for {EntityUri}:{ExtensionKind}", entityUri, extensionKind);
            return cached.Value.Data;
        }

        // Fetch from API
        var response = await FetchExtendedMetadataAsync(
            new[] { (entityUri, (IEnumerable<ExtensionKind>)new[] { extensionKind }) },
            cancellationToken);

        // Extract and return the data
        var extData = response.GetExtensionData(entityUri, extensionKind);
        if (extData?.ExtensionData != null)
        {
            return extData.ExtensionData.Value.ToByteArray();
        }

        return null;
    }

    /// <summary>
    /// Gets audio files for a track.
    /// </summary>
    /// <param name="trackUri">The track URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Track with audio files, or null if not found.</returns>
    public async Task<Track?> GetTrackAudioFilesAsync(
        string trackUri,
        CancellationToken cancellationToken = default)
    {
        var data = await GetExtensionAsync(trackUri, ExtensionKind.TrackV4, cancellationToken);
        if (data == null)
            return null;

        try
        {
            return Track.Parser.ParseFrom(data);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse Track from AUDIO_FILES extension data");
            return null;
        }
    }


    /// <summary>
    /// Gets full track metadata (TRACK_V4) including audio files.
    /// Also stores queryable properties in the database.
    /// </summary>
    /// <param name="trackUri">The track URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Track metadata, or null if not found.</returns>
    public async Task<Track?> GetTrackAsync(
        string trackUri,
        CancellationToken cancellationToken = default)
    {
        var data = await GetExtensionAsync(trackUri, ExtensionKind.TrackV4, cancellationToken);
        if (data == null)
            return null;

        try
        {
            var track = Track.Parser.ParseFrom(data);

            // Store queryable properties
            await StoreTrackPropertiesAsync(trackUri, track, cancellationToken);

            return track;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse Track from TRACK_V4 extension data");
            return null;
        }
    }

    /// <summary>
    /// Fetches multiple extensions for multiple entities in a single batch request.
    /// More efficient than individual calls.
    /// </summary>
    /// <param name="requests">List of (entityUri, extensionKinds) tuples.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Batched extension response.</returns>
    public async Task<BatchedExtensionResponse> GetBatchedExtensionsAsync(
        IEnumerable<(string EntityUri, IEnumerable<ExtensionKind> Extensions)> requests,
        CancellationToken cancellationToken = default)
    {
        var requestList = requests.ToList();

        // Collect cached results and determine what needs fetching
        var cachedData = new Dictionary<(string, ExtensionKind), byte[]>();
        var uncachedRequests = new List<(string EntityUri, List<(ExtensionKind Kind, string? Etag)> Extensions)>();

        foreach (var (entityUri, extensions) in requestList)
        {
            var uncachedExtensions = new List<(ExtensionKind, string?)>();

            foreach (var ext in extensions)
            {
                var cached = await _database.GetExtensionAsync(entityUri, ext, cancellationToken);
                if (cached.HasValue)
                {
                    cachedData[(entityUri, ext)] = cached.Value.Data;
                }
                else
                {
                    // Get etag for conditional request even if expired
                    var etag = await _database.GetExtensionEtagAsync(entityUri, ext, cancellationToken);
                    uncachedExtensions.Add((ext, etag));
                }
            }

            if (uncachedExtensions.Count > 0)
            {
                uncachedRequests.Add((entityUri, uncachedExtensions));
            }
        }

        // Ensure cached entities have rows in SQLite entities table (required for FK constraints)
        if (cachedData.Count > 0)
        {
            await EnsureEntitiesFromCacheAsync(cachedData, cancellationToken);
        }

        // If everything is cached, return synthetic response
        if (uncachedRequests.Count == 0)
        {
            _logger?.LogDebug("All {Count} extensions served from cache",
                requestList.Sum(r => r.Extensions.Count()));

            return BuildResponseFromCache(cachedData);
        }

        // Fetch uncached data
        var response = await FetchExtendedMetadataAsync(
            uncachedRequests.Select(r => (r.EntityUri, r.Extensions.Select(e => e.Kind))),
            cancellationToken);

        // Merge cached entries into the response. Without this, a mixed batch
        // (some cached, some not) returns only the uncached entries — callers
        // iterating response.GetExtensionData see nulls for the cached keys,
        // so SQLite-persisted data never surfaces to the caller. With the
        // ExtendedMetadataStore batching unrelated requests into the same
        // 50ms window, this bug was firing on nearly every batch.
        if (cachedData.Count > 0)
        {
            MergeCachedIntoResponse(response, cachedData);
        }

        return response;
    }

    /// <summary>
    /// Invalidates cached data for an entity.
    /// </summary>
    public Task InvalidateCacheAsync(string entityUri, CancellationToken cancellationToken = default)
    {
        return _database.InvalidateEntityAsync(entityUri, cancellationToken);
    }

    /// <summary>
    /// Cleans up expired cache entries.
    /// </summary>
    public Task<int> CleanupExpiredCacheAsync(CancellationToken cancellationToken = default)
    {
        return _database.CleanupExpiredExtensionsAsync(cancellationToken);
    }

    #region Private Methods

    private async Task<BatchedExtensionResponse> FetchExtendedMetadataAsync(
        IEnumerable<(string EntityUri, IEnumerable<ExtensionKind> Extensions)> requests,
        CancellationToken cancellationToken)
    {
        var requestList = requests.ToList();
        if (requestList.Count == 0)
        {
            return new BatchedExtensionResponse();
        }

        // Get country and catalogue for header
        var countryCode = await _session.GetCountryCodeAsync(cancellationToken);
        var accountType = await _session.GetAccountTypeAsync(cancellationToken);
        var catalogue = MapAccountTypeToCatalogue(accountType);

        var usesPlayerMetadata = requestList
            .SelectMany(static r => r.Extensions)
            .Any(static extension =>
                (int)extension == AudioAssociationsExtensionKindValue ||
                (int)extension == VideoAssociationsExtensionKindValue);

        // Build request
        var request = new BatchedEntityRequest
        {
            Header = new BatchedEntityRequestHeader
            {
                Country = countryCode,
                Catalogue = catalogue,
                TaskId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16))
            }
        };

        foreach (var (entityUri, extensions) in requestList)
        {
            var entityRequest = new EntityRequest { EntityUri = entityUri };
            foreach (var ext in extensions)
            {
                entityRequest.Query.Add(new ExtensionQuery { ExtensionKind = ext });
            }
            request.EntityRequest.Add(entityRequest);
        }

        // Get access token
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken);

        var url = $"{GetBaseUrl()}/extended-metadata/v0/extended-metadata";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Version = HttpVersion.Version11;
        httpRequest.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        await AddExtendedMetadataHeadersAsync(
            httpRequest,
            accessToken.Token,
            usesPlayerMetadata ? PlayerMetadataClientFeatureId : CollectionMetadataClientFeatureId,
            cancellationToken);

        var protobufBytes = request.ToByteArray();
        httpRequest.Content = new ByteArrayContent(protobufBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(ExtendedMetadataContentType);

        _logger?.LogDebug("POST extended-metadata: {Count} entities, country={Country}, catalogue={Catalogue}, feature={Feature}",
            requestList.Count, countryCode, catalogue, usesPlayerMetadata ? PlayerMetadataClientFeatureId : CollectionMetadataClientFeatureId);

        // Send with retry
        var httpResponse = await SendWithRetryAsync(httpRequest, cancellationToken);

        switch (httpResponse.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new SpClientException(SpClientFailureReason.NotFound, "Extended metadata endpoint not found");
            case HttpStatusCode.Unauthorized:
                throw new SpClientException(SpClientFailureReason.Unauthorized, "Access token invalid or expired");
            case HttpStatusCode.BadRequest:
                throw new SpClientException(SpClientFailureReason.RequestFailed, "Invalid extended metadata request");
        }

        if ((int)httpResponse.StatusCode >= 500)
        {
            throw new SpClientException(SpClientFailureReason.ServerError, $"Server error: {httpResponse.StatusCode}");
        }

        httpResponse.EnsureSuccessStatusCode();

        var responseBytes = await ReadResponseBytesAsync(httpResponse, cancellationToken);
        var response = BatchedExtensionResponse.Parser.ParseFrom(responseBytes);

        _logger?.LogDebug("Extended metadata response: {Count} extension arrays", response.ExtendedMetadata.Count);

        // Cache the results
        await CacheResponseAsync(response, cancellationToken);

        return response;
    }

    internal static async Task<byte[]> ReadResponseBytesAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        await using var baseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var decodedStream = CreateDecodedStream(baseStream, response.Content.Headers.ContentEncoding);
        using var buffer = new MemoryStream();
        await decodedStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static Stream CreateDecodedStream(Stream baseStream, ICollection<string> encodings)
    {
        Stream current = baseStream;

        foreach (var encoding in encodings.Reverse())
        {
            current = encoding.Trim().ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(current, CompressionMode.Decompress, leaveOpen: false),
                "deflate" => new DeflateStream(current, CompressionMode.Decompress, leaveOpen: false),
                "br" => new BrotliStream(current, CompressionMode.Decompress, leaveOpen: false),
                "zstd" => new DecompressionStream(current),
                _ => current
            };
        }

        return current;
    }

    private string? GetEffectiveLocale()
    {
        var sessionLocale = _session.GetPreferredLocale();
        if (!string.IsNullOrEmpty(sessionLocale))
        {
            return sessionLocale;
        }

        var userData = _session.GetUserData();
        return userData?.PreferredLocale;
    }

    private async Task AddExtendedMetadataHeadersAsync(
        HttpRequestMessage request,
        string accessToken,
        string clientFeatureId,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ExtendedMetadataContentType));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("zstd"));
        request.Headers.Connection.Add("keep-alive");
        request.Headers.TryAddWithoutValidation("Accept-Language", GetMetadataRequestLanguage());
        request.Headers.TryAddWithoutValidation("App-Platform", SpotifyClientIdentity.AppPlatform);
        request.Headers.TryAddWithoutValidation("Spotify-App-Version", SpotifyClientIdentity.AppVersionHeader);
        request.Headers.TryAddWithoutValidation("client-feature-id", clientFeatureId);
        request.Headers.TryAddWithoutValidation("Origin", GetBaseUrl());
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);

        try
        {
            var clientToken = await _clientTokenManager.GetClientTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(clientToken))
            {
                request.Headers.TryAddWithoutValidation("client-token", clientToken);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get client token for extended metadata request, continuing without");
        }
    }

    private string GetMetadataRequestLanguage()
    {
        var locale = GetEffectiveLocale();
        if (string.IsNullOrWhiteSpace(locale))
        {
            return "en";
        }

        var separatorIndex = locale.IndexOfAny(['-', '_']);
        var language = separatorIndex > 0 ? locale[..separatorIndex] : locale;
        return language.ToLowerInvariant();
    }

    private async Task CacheResponseAsync(BatchedExtensionResponse response, CancellationToken cancellationToken)
    {
        foreach (var extArray in response.ExtendedMetadata)
        {
            var extensionKind = extArray.ExtensionKind;
            var arrayHeader = extArray.Header;

            foreach (var extData in extArray.ExtensionData)
            {
                if (extData.ExtensionData == null)
                    continue;

                var entityUri = extData.EntityUri;
                var header = extData.Header;

                // Determine TTL
                var ttlSeconds = header?.CacheTtlInSeconds ?? arrayHeader?.CacheTtlInSeconds ?? DefaultTtlSeconds;
                var etag = header?.Etag;

                // Store in cache
                var data = extData.ExtensionData.Value.ToByteArray();
                await _database.SetExtensionAsync(entityUri, extensionKind, data, etag, ttlSeconds, cancellationToken);

                // Store queryable properties for supported entity types
                try
                {
                    if (extensionKind == ExtensionKind.TrackV4)
                    {
                        var track = Track.Parser.ParseFrom(data);
                        await StoreTrackPropertiesAsync(entityUri, track, cancellationToken);
                    }
                    else if (extensionKind == ExtensionKind.AlbumV4)
                    {
                        var album = Album.Parser.ParseFrom(data);
                        await StoreAlbumPropertiesAsync(entityUri, album, cancellationToken);
                    }
                    else if (extensionKind == ExtensionKind.ArtistV4)
                    {
                        var artist = Artist.Parser.ParseFrom(data);
                        await StoreArtistPropertiesAsync(entityUri, artist, cancellationToken);
                    }
                    else if (extensionKind == ExtensionKind.ShowV4)
                    {
                        var show = Show.Parser.ParseFrom(data);
                        await StoreShowPropertiesAsync(entityUri, show, cancellationToken);
                    }
                    else if (extensionKind == ExtensionKind.ShowV5)
                    {
                        var show = Show.Parser.ParseFrom(data);
                        await StoreShowPropertiesAsync(entityUri, show, cancellationToken);
                    }
                    else if (extensionKind == ExtensionKind.EpisodeV4)
                    {
                        var episode = Episode.Parser.ParseFrom(data);
                        await StoreEpisodePropertiesAsync(entityUri, episode, cancellationToken);
                    }
                }
                catch
                {
                    // Ignore parse errors for property extraction
                }
            }
        }
    }

    private async Task StoreTrackPropertiesAsync(string uri, Track track, CancellationToken cancellationToken)
    {
        string? artistName = null;
        if (track.Artist.Count > 0)
        {
            artistName = string.Join(", ", track.Artist.Select(a => a.Name));
        }

        string? albumName = null;
        string? albumUri = null;
        int? releaseYear = null;
        string? imageUrl = null;
        string? tags = track.Tags.Count > 0 ? string.Join(", ", track.Tags) : null;

        if (track.Album != null)
        {
            albumName = track.Album.Name;
            if (track.Album.Gid != null && track.Album.Gid.Length > 0)
            {
                albumUri = $"spotify:album:{SpotifyId.FromRaw(track.Album.Gid.Span, SpotifyIdType.Album).ToBase62()}";
            }
            if (track.Album.Date != null)
            {
                releaseYear = track.Album.Date.Year;
            }
            if (track.Album.CoverGroup?.Image.Count > 0)
            {
                var image = track.Album.CoverGroup.Image
                    .OrderByDescending(i => i.Size == Image.Types.Size.Default ? 2 :
                                            i.Size == Image.Types.Size.Large ? 1 : 0)
                    .FirstOrDefault();
                if (image != null)
                {
                    var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
                    imageUrl = $"https://i.scdn.co/image/{imageId}";
                }
            }
        }

        await _database.UpsertEntityAsync(
            uri: uri,
            entityType: EntityType.Track,
            title: track.Name,
            artistName: artistName,
            albumName: albumName,
            albumUri: albumUri,
            durationMs: track.Duration,
            trackNumber: track.Number,
            discNumber: track.DiscNumber,
            releaseYear: releaseYear,
            imageUrl: imageUrl,
            genre: tags,
            cancellationToken: cancellationToken);
    }

    private async Task StoreAlbumPropertiesAsync(string uri, Album album, CancellationToken cancellationToken)
    {
        string? artistName = null;
        if (album.Artist.Count > 0)
        {
            artistName = string.Join(", ", album.Artist.Select(a => a.Name));
        }

        int? releaseYear = null;
        if (album.Date != null)
        {
            releaseYear = album.Date.Year;
        }

        string? imageUrl = null;
        if (album.CoverGroup?.Image.Count > 0)
        {
            var image = album.CoverGroup.Image
                .OrderByDescending(i => i.Size == Image.Types.Size.Default ? 2 :
                                        i.Size == Image.Types.Size.Large ? 1 : 0)
                .FirstOrDefault();
            if (image != null)
            {
                var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
                imageUrl = $"https://i.scdn.co/image/{imageId}";
            }
        }

        // Count total tracks across all discs
        int? trackCount = null;
        if (album.Disc.Count > 0)
        {
            trackCount = album.Disc.Sum(d => d.Track.Count);
        }

        await _database.UpsertEntityAsync(
            uri: uri,
            entityType: EntityType.Album,
            title: album.Name,
            artistName: artistName,
            releaseYear: releaseYear,
            imageUrl: imageUrl,
            trackCount: trackCount,
            cancellationToken: cancellationToken);
    }

    private async Task StoreArtistPropertiesAsync(string uri, Artist artist, CancellationToken cancellationToken)
    {
        string? imageUrl = null;
        if (artist.PortraitGroup?.Image.Count > 0)
        {
            var image = artist.PortraitGroup.Image
                .OrderByDescending(i => i.Size == Image.Types.Size.Default ? 2 :
                                        i.Size == Image.Types.Size.Large ? 1 : 0)
                .FirstOrDefault();
            if (image != null)
            {
                var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
                imageUrl = $"https://i.scdn.co/image/{imageId}";
            }
        }

        await _database.UpsertEntityAsync(
            uri: uri,
            entityType: EntityType.Artist,
            title: artist.Name,
            imageUrl: imageUrl,
            cancellationToken: cancellationToken);
    }

    private async Task StoreShowPropertiesAsync(string uri, Show show, CancellationToken cancellationToken)
    {
        string? imageUrl = null;
        if (show.CoverImage?.Image.Count > 0)
        {
            var image = show.CoverImage.Image
                .OrderByDescending(i => i.Size == Image.Types.Size.Default ? 2 :
                                        i.Size == Image.Types.Size.Large ? 1 : 0)
                .FirstOrDefault();
            if (image != null)
            {
                var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
                imageUrl = $"https://i.scdn.co/image/{imageId}";
            }
        }

        await _database.UpsertEntityAsync(
            uri: uri,
            entityType: EntityType.Show,
            title: show.Name,
            imageUrl: imageUrl,
            publisher: show.Publisher,
            description: show.Description,
            episodeCount: show.Episode.Count,
            cancellationToken: cancellationToken);
    }

    private async Task StoreEpisodePropertiesAsync(string uri, Episode episode, CancellationToken cancellationToken)
    {
        var show = episode.Show;
        var showUri = show?.Gid is { Length: > 0 } gid
            ? $"spotify:show:{SpotifyId.FromRaw(gid.Span, SpotifyIdType.Show).ToBase62()}"
            : null;

        var imageUrl = GetImageUrl(episode.CoverImage) ?? GetImageUrl(show?.CoverImage);

        await _database.UpsertEntityAsync(
            uri: uri,
            entityType: EntityType.Episode,
            title: episode.Name,
            artistName: show?.Publisher,
            albumName: show?.Name,
            albumUri: showUri,
            durationMs: episode.Duration,
            trackNumber: episode.Number,
            releaseYear: episode.PublishTime?.Year,
            imageUrl: imageUrl,
            publisher: show?.Publisher,
            description: episode.Description,
            cancellationToken: cancellationToken);
    }

    private static string? GetImageUrl(ImageGroup? imageGroup)
    {
        if (imageGroup?.Image.Count > 0 != true)
            return null;

        var image = imageGroup.Image
            .OrderByDescending(i => i.Size == Image.Types.Size.Default ? 2 :
                                    i.Size == Image.Types.Size.Large ? 1 : 0)
            .FirstOrDefault();
        if (image?.FileId is not { Length: > 0 } fileId)
            return null;

        var imageId = Convert.ToHexString(fileId.ToByteArray()).ToLowerInvariant();
        return $"https://i.scdn.co/image/{imageId}";
    }

    private async Task EnsureEntitiesFromCacheAsync(
        Dictionary<(string, ExtensionKind), byte[]> cachedData,
        CancellationToken cancellationToken)
    {
        foreach (var ((entityUri, extensionKind), data) in cachedData)
        {
            // Check if entity already exists
            var existingEntity = await _database.GetEntityAsync(entityUri, cancellationToken);
            if (existingEntity != null)
                continue;

            // Entity doesn't exist - parse cached data and create it
            try
            {
                if (extensionKind == ExtensionKind.TrackV4)
                {
                    var track = Track.Parser.ParseFrom(data);
                    await StoreTrackPropertiesAsync(entityUri, track, cancellationToken);
                }
                else if (extensionKind == ExtensionKind.AlbumV4)
                {
                    var album = Album.Parser.ParseFrom(data);
                    await StoreAlbumPropertiesAsync(entityUri, album, cancellationToken);
                }
                else if (extensionKind == ExtensionKind.ArtistV4)
                {
                    var artist = Artist.Parser.ParseFrom(data);
                    await StoreArtistPropertiesAsync(entityUri, artist, cancellationToken);
                }
                else if (extensionKind == ExtensionKind.ShowV4)
                {
                    var show = Show.Parser.ParseFrom(data);
                    await StoreShowPropertiesAsync(entityUri, show, cancellationToken);
                }
                else if (extensionKind == ExtensionKind.ShowV5)
                {
                    var show = Show.Parser.ParseFrom(data);
                    await StoreShowPropertiesAsync(entityUri, show, cancellationToken);
                }
                else if (extensionKind == ExtensionKind.EpisodeV4)
                {
                    var episode = Episode.Parser.ParseFrom(data);
                    await StoreEpisodePropertiesAsync(entityUri, episode, cancellationToken);
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }
    }

    // Merges cached (uri, kind) -> bytes entries into an existing response
    // whose ExtendedMetadata arrays were populated from a network fetch of
    // uncached keys. Preserves existing arrays for kinds already present and
    // appends new EntityExtensionData entries for cached URIs; creates a new
    // EntityExtensionDataArray when a kind appears only in cachedData.
    private static void MergeCachedIntoResponse(
        BatchedExtensionResponse response,
        Dictionary<(string, ExtensionKind), byte[]> cachedData)
    {
        foreach (var kindGroup in cachedData.GroupBy(kvp => kvp.Key.Item2))
        {
            var extArray = response.ExtendedMetadata
                .FirstOrDefault(a => a.ExtensionKind == kindGroup.Key);
            if (extArray is null)
            {
                extArray = new EntityExtensionDataArray { ExtensionKind = kindGroup.Key };
                response.ExtendedMetadata.Add(extArray);
            }

            foreach (var ((entityUri, _), data) in kindGroup)
            {
                extArray.ExtensionData.Add(new EntityExtensionData
                {
                    EntityUri = entityUri,
                    ExtensionData = new Google.Protobuf.WellKnownTypes.Any
                    {
                        Value = ByteString.CopyFrom(data)
                    }
                });
            }
        }
    }

    private static BatchedExtensionResponse BuildResponseFromCache(
        Dictionary<(string, ExtensionKind), byte[]> cachedData)
    {
        var response = new BatchedExtensionResponse
        {
            Header = new BatchedExtensionResponseHeader()
        };

        // Group by extension kind
        var byKind = cachedData.GroupBy(kvp => kvp.Key.Item2);

        foreach (var kindGroup in byKind)
        {
            var extArray = new EntityExtensionDataArray
            {
                ExtensionKind = kindGroup.Key
            };

            foreach (var ((entityUri, _), data) in kindGroup)
            {
                // Match the network path's Any shape: .Value holds the raw Track/Album/etc.
                // bytes directly. Wrapping in BytesValue made UnpackAs<T> parse BytesValue
                // wire bytes as T and silently fail, which blanked cached rows.
                var extData = new EntityExtensionData
                {
                    EntityUri = entityUri,
                    ExtensionData = new Google.Protobuf.WellKnownTypes.Any
                    {
                        Value = ByteString.CopyFrom(data)
                    }
                };
                extArray.ExtensionData.Add(extData);
            }

            response.ExtendedMetadata.Add(extArray);
        }

        return response;
    }

    private static string MapAccountTypeToCatalogue(AccountType accountType)
    {
        return accountType switch
        {
            AccountType.Premium => "premium",
            AccountType.Family => "premium",
            AccountType.Free => "free",
            AccountType.Artist => "premium",
            _ => "premium"
        };
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                // Clone request for retry (original can't be reused)
                using var clonedRequest = await CloneHttpRequestAsync(request);
                var response = await _httpClient.SendAsync(clonedRequest, cancellationToken);

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _logger?.LogWarning(
                        "Extended metadata request failed with {StatusCode}, retrying in {Delay}s (attempt {Attempt}/{MaxRetries})",
                        response.StatusCode, delay.TotalSeconds, attempt + 1, MaxRetries);

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
                    "Extended metadata HTTP request failed (attempt {Attempt}/{MaxRetries})",
                    attempt + 1, MaxRetries);

                if (attempt < MaxRetries - 1)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        throw new SpClientException(
            SpClientFailureReason.RequestFailed,
            $"Extended metadata request failed after {MaxRetries} attempts",
            lastException);
    }

    private static async Task<HttpRequestMessage> CloneHttpRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content != null)
        {
            var content = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(content);

            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    #endregion
}

/// <summary>
/// Extension methods for working with extended metadata responses.
/// </summary>
public static class ExtendedMetadataResponseExtensions
{
    /// <summary>
    /// Gets extension data for a specific entity URI and extension kind.
    /// </summary>
    public static EntityExtensionData? GetExtensionData(
        this BatchedExtensionResponse response,
        string entityUri,
        ExtensionKind extensionKind)
    {
        foreach (var extArray in response.ExtendedMetadata)
        {
            if (extArray.ExtensionKind != extensionKind)
                continue;

            foreach (var extData in extArray.ExtensionData)
            {
                if (extData.EntityUri == entityUri)
                    return extData;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all extension data for a specific extension kind.
    /// </summary>
    public static IEnumerable<EntityExtensionData> GetAllExtensionData(
        this BatchedExtensionResponse response,
        ExtensionKind extensionKind)
    {
        foreach (var extArray in response.ExtendedMetadata)
        {
            if (extArray.ExtensionKind != extensionKind)
                continue;

            foreach (var extData in extArray.ExtensionData)
            {
                yield return extData;
            }
        }
    }

    /// <summary>
    /// Unpacks extension data as a specific protobuf message type.
    /// </summary>
    public static T? UnpackAs<T>(this EntityExtensionData extensionData) where T : IMessage<T>, new()
    {
        if (extensionData.ExtensionData == null)
            return default;

        try
        {
            var parser = new MessageParser<T>(() => new T());
            return parser.ParseFrom(extensionData.ExtensionData.Value);
        }
        catch
        {
            return default;
        }
    }
}
