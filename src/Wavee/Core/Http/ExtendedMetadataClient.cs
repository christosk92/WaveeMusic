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
        var etag = await _database.GetExtensionEtagAsync(entityUri, extensionKind, cancellationToken);
        var response = await FetchExtendedMetadataAsync(
            new[] { (entityUri, (IEnumerable<(ExtensionKind Kind, string? Etag)>)new[] { (extensionKind, etag) }) },
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

        // Group all requested (uri, kind) pairs by kind so we issue exactly
        // one bulk SQLite read per kind (chunked at 500 inside the database)
        // instead of two reads per (uri, kind) pair.
        var byKind = new Dictionary<ExtensionKind, List<string>>();
        foreach (var (uri, exts) in requestList)
        {
            foreach (var kind in exts)
            {
                if (!byKind.TryGetValue(kind, out var list))
                {
                    list = new List<string>();
                    byKind[kind] = list;
                }
                list.Add(uri);
            }
        }

        var cachedData = new Dictionary<(string, ExtensionKind), byte[]>();
        // Per-uri (kind, etag) tuples that need to be sent to Spotify.
        // Etag carries a stale row's etag for conditional fetch.
        var forwardByUri = new Dictionary<string, List<(ExtensionKind Kind, string? Etag)>>(StringComparer.Ordinal);
        int totalFresh = 0, totalStale = 0, totalMissing = 0;

        foreach (var (kind, uris) in byKind)
        {
            var lookup = await _database.GetExtensionsBulkWithEtagAsync(uris, kind, cancellationToken);
            foreach (var uri in uris)
            {
                if (!lookup.TryGetValue(uri, out var entry))
                {
                    AddForward(forwardByUri, uri, kind, null);
                    totalMissing++;
                }
                else if (entry.IsFresh)
                {
                    cachedData[(uri, kind)] = entry.Data;
                    totalFresh++;
                }
                else
                {
                    AddForward(forwardByUri, uri, kind, entry.Etag);
                    totalStale++;
                }
            }
        }

        _logger?.LogDebug(
            "metadata-cache lookup: kinds={KindCount} fresh={Fresh} stale={Stale} missing={Missing}",
            byKind.Count,
            totalFresh,
            totalStale,
            totalMissing);

        // Ensure cached entities have rows in SQLite entities table (required for FK constraints).
        if (cachedData.Count > 0)
        {
            await EnsureEntitiesFromCacheAsync(cachedData, cancellationToken);
        }

        if (forwardByUri.Count == 0)
        {
            return BuildResponseFromCache(cachedData);
        }

        var forwardRequests = forwardByUri
            .Select(kvp => (kvp.Key, (IEnumerable<(ExtensionKind Kind, string? Etag)>)kvp.Value))
            .ToList();

        var response = await FetchExtendedMetadataAsync(forwardRequests, cancellationToken);

        // Merge cached entries into the response. Without this, a mixed batch
        // (some cached, some not) returns only the uncached entries — callers
        // iterating response.GetExtensionData see nulls for the cached keys.
        if (cachedData.Count > 0)
        {
            MergeCachedIntoResponse(response, cachedData);
        }

        return response;
    }

    private static void AddForward(
        Dictionary<string, List<(ExtensionKind Kind, string? Etag)>> map,
        string uri,
        ExtensionKind kind,
        string? etag)
    {
        if (!map.TryGetValue(uri, out var list))
        {
            list = new List<(ExtensionKind, string?)>();
            map[uri] = list;
        }
        list.Add((kind, etag));
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

    private const int HttpChunkSize = 500;

    private async Task<BatchedExtensionResponse> FetchExtendedMetadataAsync(
        IEnumerable<(string EntityUri, IEnumerable<(ExtensionKind Kind, string? Etag)> Extensions)> requests,
        CancellationToken cancellationToken)
    {
        var requestList = requests.ToList();
        if (requestList.Count == 0)
        {
            return new BatchedExtensionResponse();
        }

        if (requestList.Count <= HttpChunkSize)
        {
            return await FetchExtendedMetadataChunkAsync(requestList, cancellationToken);
        }

        // > 500 entities — split into HttpChunkSize-entity POSTs and merge the
        // responses. Spotify's extended-metadata endpoint accepts more, but we
        // cap to keep request bodies small and to bound 304-fallback work.
        var merged = new BatchedExtensionResponse
        {
            Header = new BatchedExtensionResponseHeader()
        };
        for (int offset = 0; offset < requestList.Count; offset += HttpChunkSize)
        {
            var take = Math.Min(HttpChunkSize, requestList.Count - offset);
            var chunk = requestList.GetRange(offset, take);
            var chunkResponse = await FetchExtendedMetadataChunkAsync(chunk, cancellationToken);
            MergeChunkResponseInto(merged, chunkResponse);
        }
        _logger?.LogDebug(
            "extended-metadata chunked: totalEntities={Total} chunks={Chunks}",
            requestList.Count,
            (requestList.Count + HttpChunkSize - 1) / HttpChunkSize);
        return merged;
    }

    private static void MergeChunkResponseInto(BatchedExtensionResponse target, BatchedExtensionResponse source)
    {
        foreach (var sourceArray in source.ExtendedMetadata)
        {
            var existing = target.ExtendedMetadata.FirstOrDefault(a => a.ExtensionKind == sourceArray.ExtensionKind);
            if (existing is null)
            {
                target.ExtendedMetadata.Add(sourceArray);
                continue;
            }
            foreach (var entry in sourceArray.ExtensionData)
            {
                existing.ExtensionData.Add(entry);
            }
        }
    }

    private async Task<BatchedExtensionResponse> FetchExtendedMetadataChunkAsync(
        IReadOnlyList<(string EntityUri, IEnumerable<(ExtensionKind Kind, string? Etag)> Extensions)> requestList,
        CancellationToken cancellationToken)
    {
        // Get country and catalogue for header
        var countryCode = await _session.GetCountryCodeAsync(cancellationToken);
        var accountType = await _session.GetAccountTypeAsync(cancellationToken);
        var catalogue = MapAccountTypeToCatalogue(accountType);

        var usesPlayerMetadata = requestList
            .SelectMany(static r => r.Extensions)
            .Any(static extension =>
                (int)extension.Kind == AudioAssociationsExtensionKindValue ||
                (int)extension.Kind == VideoAssociationsExtensionKindValue);

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
            foreach (var (kind, etag) in extensions)
            {
                var query = new ExtensionQuery { ExtensionKind = kind };
                if (!string.IsNullOrWhiteSpace(etag))
                    query.Etag = etag;
                entityRequest.Query.Add(query);
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

        if (httpResponse.StatusCode == HttpStatusCode.NotModified)
        {
            return await BuildNotModifiedResponseFromCacheAsync(requestList, httpResponse, cancellationToken);
        }

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

        await RehydrateNotModifiedPayloadsAsync(response, cancellationToken);

        // Cache the results
        await CacheResponseAsync(response, cancellationToken);

        return response;
    }

    private async Task<BatchedExtensionResponse> BuildNotModifiedResponseFromCacheAsync(
        IReadOnlyCollection<(string EntityUri, IEnumerable<(ExtensionKind Kind, string? Etag)> Extensions)> requests,
        HttpResponseMessage httpResponse,
        CancellationToken cancellationToken)
    {
        var ttlSeconds = ResolveNotModifiedTtl(httpResponse);

        // Group requested (uri, kind) pairs by kind so we issue one bulk
        // SQLite read per kind instead of two reads (per-row + write) per pair.
        var byKind = new Dictionary<ExtensionKind, List<string>>();
        foreach (var (uri, exts) in requests)
        {
            foreach (var (kind, _) in exts)
            {
                if (!byKind.TryGetValue(kind, out var list))
                {
                    list = new List<string>();
                    byKind[kind] = list;
                }
                list.Add(uri);
            }
        }

        var cachedData = new Dictionary<(string, ExtensionKind), byte[]>();
        var refreshRows = new List<(string EntityUri, ExtensionKind Kind)>();
        var missing = 0;

        foreach (var (kind, uris) in byKind)
        {
            var lookup = await _database.GetExtensionsBulkWithEtagAsync(uris, kind, cancellationToken);
            foreach (var uri in uris)
            {
                if (!lookup.TryGetValue(uri, out var entry))
                {
                    missing++;
                    continue;
                }
                cachedData[(uri, kind)] = entry.Data;
                refreshRows.Add((uri, kind));
            }
        }

        if (refreshRows.Count > 0)
        {
            await _database.RefreshExtensionTtlBulkAsync(refreshRows, ttlSeconds, cancellationToken);
        }

        _logger?.LogDebug(
            "extended-metadata 304: requested={RequestedCount} refreshed={RefreshedCount} missingStale={MissingCount} ttl={Ttl}s",
            requests.Sum(static r => r.Extensions.Count()),
            refreshRows.Count,
            missing,
            ttlSeconds);

        return BuildResponseFromCache(cachedData);
    }

    private async Task RehydrateNotModifiedPayloadsAsync(
        BatchedExtensionResponse response,
        CancellationToken cancellationToken)
    {
        // Collect entity-level 304 entries (extData.ExtensionData == null)
        // grouped by kind so we can bulk-read the stale cache.
        var byKind = new Dictionary<ExtensionKind, (List<string> Uris, List<EntityExtensionData> Targets)>();
        foreach (var extArray in response.ExtendedMetadata)
        {
            var extensionKind = extArray.ExtensionKind;
            foreach (var extData in extArray.ExtensionData)
            {
                if (extData.ExtensionData is not null || string.IsNullOrWhiteSpace(extData.EntityUri))
                    continue;
                if (!byKind.TryGetValue(extensionKind, out var pair))
                {
                    pair = (new List<string>(), new List<EntityExtensionData>());
                    byKind[extensionKind] = pair;
                }
                pair.Uris.Add(extData.EntityUri);
                pair.Targets.Add(extData);
            }
        }

        if (byKind.Count == 0) return;

        var rehydrated = 0;
        var missing = 0;

        foreach (var (kind, (uris, targets)) in byKind)
        {
            var lookup = await _database.GetExtensionsBulkWithEtagAsync(uris, kind, cancellationToken);
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (!lookup.TryGetValue(uris[i], out var entry))
                {
                    missing++;
                    continue;
                }
                target.ExtensionData = new Google.Protobuf.WellKnownTypes.Any
                {
                    Value = ByteString.CopyFrom(entry.Data)
                };
                target.Header ??= new EntityExtensionDataHeader();
                if (string.IsNullOrWhiteSpace(target.Header.Etag))
                {
                    target.Header.Etag = entry.Etag;
                }
                rehydrated++;
            }
        }

        if (rehydrated > 0 || missing > 0)
        {
            _logger?.LogDebug(
                "Rehydrated extended-metadata not-modified payloads from stale cache: rehydrated={RehydratedCount} missing={MissingCount}",
                rehydrated,
                missing);
        }
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
        // Pass 1: collect every extension write into one flat list and accumulate
        // aggregate stats. We avoid per-row SetExtensionAsync/UpsertEntityAsync
        // calls — those are replaced by SetExtensionsBulkAsync + a single
        // BeginWriteBatchAsync transaction in pass 2.
        var writes = new List<ExtensionWriteRecord>();
        // Per-(uri, kind, data) triples that need queryable-property upserts
        // routed through StoreXxxPropertiesAsync inside the batch scope.
        var queryableJobs = new List<(string Uri, ExtensionKind Kind, byte[] Data)>();
        var nullPayloads = 0;
        long? minTtl = null;
        long? maxTtl = null;

        foreach (var extArray in response.ExtendedMetadata)
        {
            var extensionKind = extArray.ExtensionKind;
            var arrayHeader = extArray.Header;

            foreach (var extData in extArray.ExtensionData)
            {
                if (extData.ExtensionData == null)
                {
                    nullPayloads++;
                    continue;
                }

                var entityUri = extData.EntityUri;
                var header = extData.Header;

                var ttlSeconds = ResolveCacheTtl(
                    header?.CacheTtlInSeconds,
                    arrayHeader?.CacheTtlInSeconds);
                var etag = header?.Etag;
                minTtl = minTtl.HasValue ? Math.Min(minTtl.Value, ttlSeconds) : ttlSeconds;
                maxTtl = maxTtl.HasValue ? Math.Max(maxTtl.Value, ttlSeconds) : ttlSeconds;

                var data = extData.ExtensionData.Value.ToByteArray();
                writes.Add(new ExtensionWriteRecord(entityUri, extensionKind, data, etag, ttlSeconds));
                if (IsQueryablePropertyKind(extensionKind))
                {
                    queryableJobs.Add((entityUri, extensionKind, data));
                }
            }
        }

        if (writes.Count == 0)
        {
            if (nullPayloads > 0)
            {
                _logger?.LogDebug(
                    "metadata-cache: no rows to write (nullPayloads={NullPayloads})",
                    nullPayloads);
            }
            return;
        }

        // Pass 2a: parse + compute entity rows OUTSIDE the write lock.
        // The expensive work — protobuf parsing, GID→base62, image-id hex,
        // string joins — must not run with the DB write lock held, or it
        // starves playback's per-row UpsertEntity for seconds.
        var parseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var entityArgs = new List<EntityUpsertArgs>(queryableJobs.Count);
        foreach (var (uri, kind, data) in queryableJobs)
        {
            var args = BuildQueryableArgs(uri, kind, data);
            if (args.HasValue) entityArgs.Add(args.Value);
        }
        var parseMs = System.Diagnostics.Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;
        var storedEntities = entityArgs.Count;

        _logger?.LogDebug(
            "CacheResponseAsync entering scope: writes={WriteCount} entityArgs={EntityArgsCount} parseMs={ParseMs:F0} arrays={ArrayCount}",
            writes.Count,
            entityArgs.Count,
            parseMs,
            response.ExtendedMetadata.Count);

        // Pass 2b: open one transaction; only DB I/O runs under the lock.
        // Operations dispatch through the IWriteBatch returned by
        // BeginWriteBatchAsync, which holds the open conn + tx directly —
        // routing through `_database` would self-acquire the lock and
        // deadlock against the open scope.
        var scopeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        long bulkWriteEnd;
        long upsertsEnd;
        await using (var batch = await _database.BeginWriteBatchAsync(cancellationToken).ConfigureAwait(false))
        {
            await batch.SetExtensionsBulkAsync(writes, cancellationToken).ConfigureAwait(false);
            bulkWriteEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var args in entityArgs)
            {
                await UpsertFromArgsAsync(batch, args, cancellationToken).ConfigureAwait(false);
            }
            upsertsEnd = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        var bulkMs = System.Diagnostics.Stopwatch.GetElapsedTime(scopeStart, bulkWriteEnd).TotalMilliseconds;
        var upsertsMs = System.Diagnostics.Stopwatch.GetElapsedTime(bulkWriteEnd, upsertsEnd).TotalMilliseconds;
        var perRowUs = entityArgs.Count > 0 ? (upsertsMs * 1000.0 / entityArgs.Count) : 0;
        _logger?.LogDebug(
            "CacheResponseAsync inside-scope timing: bulkWriteMs={BulkMs:F0} upsertsMs={UpsertsMs:F0} entityArgs={EntityArgs} perRowUs={PerRowUs:F0}",
            bulkMs,
            upsertsMs,
            entityArgs.Count,
            perRowUs);

        _logger?.LogDebug(
            "Cached extended-metadata response: extensions={ExtensionCount} queryableEntities={EntityCount} nullPayloads={NullPayloads} minTtl={MinTtl}s maxTtl={MaxTtl}s arrays={ArrayCount}",
            writes.Count,
            storedEntities,
            nullPayloads,
            minTtl,
            maxTtl,
            response.ExtendedMetadata.Count);
    }

    private static bool IsQueryablePropertyKind(ExtensionKind kind) => kind switch
    {
        ExtensionKind.TrackV4 or
        ExtensionKind.AlbumV4 or
        ExtensionKind.ArtistV4 or
        ExtensionKind.ShowV4 or
        ExtensionKind.ShowV5 or
        ExtensionKind.EpisodeV4 => true,
        _ => false
    };

    // All work needed to populate the `entities` row for one extension kind.
    // Built outside the write lock (parsing + GID→base62 + image-hex + string
    // joins), then handed to UpsertFromArgsAsync inside the scope so only DB
    // I/O runs under the lock.
    private readonly record struct EntityUpsertArgs(
        string Uri,
        EntityType EntityType,
        string? Title = null,
        string? ArtistName = null,
        string? AlbumName = null,
        string? AlbumUri = null,
        int? DurationMs = null,
        int? TrackNumber = null,
        int? DiscNumber = null,
        int? ReleaseYear = null,
        string? ImageUrl = null,
        string? Genre = null,
        int? TrackCount = null,
        string? Publisher = null,
        int? EpisodeCount = null,
        string? Description = null);

    private static Task UpsertFromArgsAsync(IWriteBatch batch, EntityUpsertArgs a, CancellationToken cancellationToken)
        => batch.UpsertEntityAsync(
            uri: a.Uri,
            entityType: a.EntityType,
            title: a.Title,
            artistName: a.ArtistName,
            albumName: a.AlbumName,
            albumUri: a.AlbumUri,
            durationMs: a.DurationMs,
            trackNumber: a.TrackNumber,
            discNumber: a.DiscNumber,
            releaseYear: a.ReleaseYear,
            imageUrl: a.ImageUrl,
            genre: a.Genre,
            trackCount: a.TrackCount,
            publisher: a.Publisher,
            episodeCount: a.EpisodeCount,
            description: a.Description,
            cancellationToken: cancellationToken);

    // Standalone variant for the per-track GetTrackAsync path that runs
    // outside any IWriteBatch scope. Routes directly through the database
    // so a self-contained UpsertEntity transaction is opened and released.
    private Task UpsertFromArgsStandaloneAsync(EntityUpsertArgs a, CancellationToken cancellationToken)
        => _database.UpsertEntityAsync(
            uri: a.Uri,
            entityType: a.EntityType,
            title: a.Title,
            artistName: a.ArtistName,
            albumName: a.AlbumName,
            albumUri: a.AlbumUri,
            durationMs: a.DurationMs,
            trackNumber: a.TrackNumber,
            discNumber: a.DiscNumber,
            releaseYear: a.ReleaseYear,
            imageUrl: a.ImageUrl,
            genre: a.Genre,
            trackCount: a.TrackCount,
            publisher: a.Publisher,
            episodeCount: a.EpisodeCount,
            description: a.Description,
            cancellationToken: cancellationToken);

    // Pure: parse + compute. Returns null when the kind isn't queryable or
    // when parsing fails. Safe to run on any thread without the write lock.
    private static EntityUpsertArgs? BuildQueryableArgs(string uri, ExtensionKind kind, byte[] data)
    {
        try
        {
            return kind switch
            {
                ExtensionKind.TrackV4 => BuildTrackUpsertArgs(uri, Track.Parser.ParseFrom(data)),
                ExtensionKind.AlbumV4 => BuildAlbumUpsertArgs(uri, Album.Parser.ParseFrom(data)),
                ExtensionKind.ArtistV4 => BuildArtistUpsertArgs(uri, Artist.Parser.ParseFrom(data)),
                ExtensionKind.ShowV4 => BuildShowUpsertArgs(uri, Show.Parser.ParseFrom(data)),
                ExtensionKind.ShowV5 => BuildShowUpsertArgs(uri, Show.Parser.ParseFrom(data)),
                ExtensionKind.EpisodeV4 => BuildEpisodeUpsertArgs(uri, Episode.Parser.ParseFrom(data)),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private Task StoreTrackPropertiesAsync(string uri, Track track, CancellationToken cancellationToken)
        => UpsertFromArgsStandaloneAsync(BuildTrackUpsertArgs(uri, track), cancellationToken);

    private static EntityUpsertArgs BuildTrackUpsertArgs(string uri, Track track)
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

        return new EntityUpsertArgs(
            Uri: uri,
            EntityType: EntityType.Track,
            Title: track.Name,
            ArtistName: artistName,
            AlbumName: albumName,
            AlbumUri: albumUri,
            DurationMs: track.Duration,
            TrackNumber: track.Number,
            DiscNumber: track.DiscNumber,
            ReleaseYear: releaseYear,
            ImageUrl: imageUrl,
            Genre: tags);
    }

    private static long ResolveCacheTtl(long? entityTtlSeconds, long? arrayTtlSeconds)
    {
        if (entityTtlSeconds is > 0)
            return entityTtlSeconds.Value;

        if (arrayTtlSeconds is > 0)
            return arrayTtlSeconds.Value;

        return DefaultTtlSeconds;
    }

    private static long ResolveNotModifiedTtl(HttpResponseMessage response)
    {
        var maxAge = response.Headers.CacheControl?.MaxAge;
        if (maxAge is { TotalSeconds: > 0 })
        {
            return (long)Math.Ceiling(maxAge.Value.TotalSeconds);
        }

        return DefaultTtlSeconds;
    }

    private Task StoreAlbumPropertiesAsync(string uri, Album album, CancellationToken cancellationToken)
        => UpsertFromArgsStandaloneAsync(BuildAlbumUpsertArgs(uri, album), cancellationToken);

    private static EntityUpsertArgs BuildAlbumUpsertArgs(string uri, Album album)
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

        return new EntityUpsertArgs(
            Uri: uri,
            EntityType: EntityType.Album,
            Title: album.Name,
            ArtistName: artistName,
            ReleaseYear: releaseYear,
            ImageUrl: imageUrl,
            TrackCount: trackCount);
    }

    private Task StoreArtistPropertiesAsync(string uri, Artist artist, CancellationToken cancellationToken)
        => UpsertFromArgsStandaloneAsync(BuildArtistUpsertArgs(uri, artist), cancellationToken);

    private static EntityUpsertArgs BuildArtistUpsertArgs(string uri, Artist artist)
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

        return new EntityUpsertArgs(
            Uri: uri,
            EntityType: EntityType.Artist,
            Title: artist.Name,
            ImageUrl: imageUrl);
    }

    private Task StoreShowPropertiesAsync(string uri, Show show, CancellationToken cancellationToken)
        => UpsertFromArgsStandaloneAsync(BuildShowUpsertArgs(uri, show), cancellationToken);

    private static EntityUpsertArgs BuildShowUpsertArgs(string uri, Show show)
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

        return new EntityUpsertArgs(
            Uri: uri,
            EntityType: EntityType.Show,
            Title: show.Name,
            ImageUrl: imageUrl,
            Publisher: show.Publisher,
            Description: show.Description,
            EpisodeCount: show.Episode.Count);
    }

    private Task StoreEpisodePropertiesAsync(string uri, Episode episode, CancellationToken cancellationToken)
        => UpsertFromArgsStandaloneAsync(BuildEpisodeUpsertArgs(uri, episode), cancellationToken);

    private static EntityUpsertArgs BuildEpisodeUpsertArgs(string uri, Episode episode)
    {
        var show = episode.Show;
        var showUri = show?.Gid is { Length: > 0 } gid
            ? $"spotify:show:{SpotifyId.FromRaw(gid.Span, SpotifyIdType.Show).ToBase62()}"
            : null;

        var imageUrl = GetImageUrl(episode.CoverImage) ?? GetImageUrl(show?.CoverImage);

        return new EntityUpsertArgs(
            Uri: uri,
            EntityType: EntityType.Episode,
            Title: episode.Name,
            ArtistName: show?.Publisher,
            AlbumName: show?.Name,
            AlbumUri: showUri,
            DurationMs: episode.Duration,
            TrackNumber: episode.Number,
            ReleaseYear: episode.PublishTime?.Year,
            ImageUrl: imageUrl,
            Publisher: show?.Publisher,
            Description: episode.Description);
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
        if (cachedData.Count == 0) return;

        // Bulk-load existing entity rows in one query rather than N per-row
        // GetEntityAsync calls.
        var allUris = cachedData.Keys
            .Select(k => k.Item1)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var existing = await _database.GetEntitiesAsync(allUris, cancellationToken).ConfigureAwait(false);
        var existingSet = new HashSet<string>(
            existing.Select(e => e.Uri),
            StringComparer.Ordinal);

        var toCreate = cachedData
            .Where(kvp => IsQueryablePropertyKind(kvp.Key.Item2)
                          && !existingSet.Contains(kvp.Key.Item1))
            .ToList();

        if (toCreate.Count == 0) return;

        // Parse + compute entity rows OUTSIDE the write lock first, then upsert
        // them in a single transaction. Keeps lock-hold time to pure DB I/O
        // so playback's per-row UpsertEntity doesn't starve.
        var entityArgs = new List<EntityUpsertArgs>(toCreate.Count);
        foreach (var ((entityUri, extensionKind), data) in toCreate)
        {
            var args = BuildQueryableArgs(entityUri, extensionKind, data);
            if (args.HasValue) entityArgs.Add(args.Value);
        }

        if (entityArgs.Count == 0) return;

        _logger?.LogDebug(
            "EnsureEntitiesFromCacheAsync entering scope: cachedDataCount={CachedCount} toCreate={ToCreate} entityArgs={EntityArgs}",
            cachedData.Count,
            toCreate.Count,
            entityArgs.Count);

        var scopeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await using (var batch = await _database.BeginWriteBatchAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var args in entityArgs)
            {
                await UpsertFromArgsAsync(batch, args, cancellationToken).ConfigureAwait(false);
            }
        }
        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(scopeStart).TotalMilliseconds;
        var perRowUs = entityArgs.Count > 0 ? (elapsedMs * 1000.0 / entityArgs.Count) : 0;
        _logger?.LogDebug(
            "EnsureEntitiesFromCacheAsync scope-elapsed: ms={Ms:F0} entityArgs={EntityArgs} perRowUs={PerRowUs:F0}",
            elapsedMs,
            entityArgs.Count,
            perRowUs);
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
