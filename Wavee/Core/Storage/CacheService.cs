using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage;

/// <summary>
/// Unified cache service providing single interface for all caching operations.
/// Automatically handles Hot cache → SQLite fallback.
/// </summary>
public interface ICacheService : IAsyncDisposable
{
    // Track operations
    Task<TrackCacheEntry?> GetTrackAsync(string uri, CancellationToken ct = default);
    Task<Dictionary<string, TrackCacheEntry>> GetTracksAsync(IEnumerable<string> uris, CancellationToken ct = default);
    Task SetTrackAsync(string uri, TrackCacheEntry entry, CancellationToken ct = default);
    Task SetTracksAsync(IEnumerable<(string Uri, TrackCacheEntry Entry)> tracks, CancellationToken ct = default);

    // Audio key operations (keyed by trackUri + fileId)
    Task<byte[]?> GetAudioKeyAsync(string trackUri, FileId fileId, CancellationToken ct = default);
    Task SetAudioKeyAsync(string trackUri, FileId fileId, byte[] key, CancellationToken ct = default);

    // CDN URL operations (keyed by fileId, has TTL)
    Task<CdnCacheEntry?> GetCdnUrlAsync(FileId fileId, CancellationToken ct = default);
    Task SetCdnUrlAsync(FileId fileId, string url, TimeSpan ttl, CancellationToken ct = default);

    // Head file operations
    Task<byte[]?> GetHeadDataAsync(FileId fileId, CancellationToken ct = default);
    Task SetHeadDataAsync(FileId fileId, byte[] headData, CancellationToken ct = default);

    // Cache management
    CacheStatistics GetStatistics();
    Task ClearAsync(CancellationToken ct = default);
    Task CleanupExpiredAsync(CancellationToken ct = default);
}

/// <summary>
/// Unified cache service implementation with hot cache + SQLite backend.
/// </summary>
public sealed class CacheService : ICacheService
{
    private readonly IHotCache<TrackCacheEntry> _hotCache;
    private readonly IMetadataDatabase _database;
    private readonly ILogger? _logger;
    private bool _disposed;

    // Separate hot caches for audio keys and CDN (smaller, frequently accessed)
    private readonly Dictionary<string, byte[]> _audioKeyCache = new();
    private readonly Dictionary<string, CdnCacheEntry> _cdnCache = new();
    private readonly Dictionary<string, byte[]> _headDataCache = new();
    private readonly object _auxCacheLock = new();
    private const int MaxAuxCacheSize = 1000;

    /// <summary>
    /// Creates a new CacheService with an internal hot cache.
    /// </summary>
    public CacheService(
        IMetadataDatabase database,
        int hotCacheSize = 10_000,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _hotCache = new HotCache<TrackCacheEntry>(hotCacheSize, logger);
        _logger = logger;

        _logger?.LogInformation("CacheService initialized with hot cache size {Size}", hotCacheSize);
    }

    /// <summary>
    /// Creates a new CacheService with an injected hot cache (for DI).
    /// </summary>
    public CacheService(
        IMetadataDatabase database,
        IHotCache<TrackCacheEntry> hotCache,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(hotCache);

        _database = database;
        _hotCache = hotCache;
        _logger = logger;

        _logger?.LogInformation("CacheService initialized with injected hot cache");
    }

    #region Track Operations

    public async Task<TrackCacheEntry?> GetTrackAsync(string uri, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Check hot cache first (O(1))
        var hot = _hotCache.Get(uri);
        if (hot != null)
        {
            _logger?.LogTrace("Hot cache hit: {Uri}", uri);
            return hot;
        }

        // Check SQLite
        var dbEntity = await _database.GetEntityAsync(uri, ct);
        if (dbEntity != null)
        {
            var entry = MapToTrackCacheEntry(dbEntity);

            // Enrich with audio data if available
            entry = await EnrichFromCacheAsync(entry, ct);

            // Promote to hot cache
            _hotCache.Set(uri, entry);

            _logger?.LogTrace("SQLite cache hit, promoted to hot: {Uri}", uri);
            return entry;
        }

        return null;
    }

    public async Task<Dictionary<string, TrackCacheEntry>> GetTracksAsync(
        IEnumerable<string> uris,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var uriList = uris.ToList();
        var result = new Dictionary<string, TrackCacheEntry>();
        var missedUris = new List<string>();

        // Batch check hot cache
        var hotResults = _hotCache.GetMany(uriList);
        foreach (var (uri, entry) in hotResults)
        {
            result[uri] = entry;
        }

        // Collect misses
        foreach (var uri in uriList)
        {
            if (!result.ContainsKey(uri))
            {
                missedUris.Add(uri);
            }
        }

        _logger?.LogDebug("GetTracksAsync: {HotHits} hot hits, {Misses} SQLite lookups",
            result.Count, missedUris.Count);

        // Batch fetch from SQLite for misses
        if (missedUris.Count > 0)
        {
            foreach (var uri in missedUris)
            {
                var dbEntity = await _database.GetEntityAsync(uri, ct);
                if (dbEntity != null)
                {
                    var entry = MapToTrackCacheEntry(dbEntity);
                    entry = await EnrichFromCacheAsync(entry, ct);
                    result[uri] = entry;
                    _hotCache.Set(uri, entry);
                }
            }
        }

        return result;
    }

    public async Task SetTrackAsync(string uri, TrackCacheEntry entry, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Set in hot cache
        _hotCache.Set(uri, entry);

        // Persist basic metadata to SQLite
        await _database.UpsertEntityAsync(
            uri: uri,
            entityType: EntityType.Track,
            title: entry.Title,
            artistName: entry.Artist,
            albumName: entry.Album,
            albumUri: entry.AlbumUri,
            durationMs: entry.DurationMs,
            trackNumber: entry.TrackNumber,
            discNumber: entry.DiscNumber,
            releaseYear: entry.ReleaseYear,
            imageUrl: entry.ImageUrl,
            cancellationToken: ct);

        // Store audio key if present
        if (entry.AudioKey != null && entry.PreferredFileId != null)
        {
            await SetAudioKeyAsync(uri, entry.PreferredFileId.Value, entry.AudioKey, ct);
        }

        // Store CDN URL if present and valid
        if (entry.CdnUrl != null && entry.CdnExpiry != null && entry.PreferredFileId != null)
        {
            var ttl = entry.CdnExpiry.Value - DateTimeOffset.UtcNow;
            if (ttl > TimeSpan.Zero)
            {
                await SetCdnUrlAsync(entry.PreferredFileId.Value, entry.CdnUrl, ttl, ct);
            }
        }

        // Store head data if present
        if (entry.HeadData != null && entry.PreferredFileId != null)
        {
            await SetHeadDataAsync(entry.PreferredFileId.Value, entry.HeadData, ct);
        }

        _logger?.LogTrace("Set track in cache: {Uri}", uri);
    }

    public async Task SetTracksAsync(
        IEnumerable<(string Uri, TrackCacheEntry Entry)> tracks,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var trackList = tracks.ToList();
        _hotCache.SetMany(trackList);

        foreach (var (uri, entry) in trackList)
        {
            await _database.UpsertEntityAsync(
                uri: uri,
                entityType: EntityType.Track,
                title: entry.Title,
                artistName: entry.Artist,
                albumName: entry.Album,
                albumUri: entry.AlbumUri,
                durationMs: entry.DurationMs,
                trackNumber: entry.TrackNumber,
                discNumber: entry.DiscNumber,
                releaseYear: entry.ReleaseYear,
                imageUrl: entry.ImageUrl,
                cancellationToken: ct);
        }

        _logger?.LogDebug("Set {Count} tracks in cache", trackList.Count);
    }

    #endregion

    #region Audio Key Operations

    public Task<byte[]?> GetAudioKeyAsync(string trackUri, FileId fileId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = GetAudioKeyCacheKey(trackUri, fileId);

        lock (_auxCacheLock)
        {
            if (_audioKeyCache.TryGetValue(key, out var cached))
            {
                _logger?.LogTrace("Audio key cache hit: {TrackUri}", trackUri);
                return Task.FromResult<byte[]?>(cached);
            }
        }

        // TODO: Check SQLite audio_keys table when implemented
        return Task.FromResult<byte[]?>(null);
    }

    public Task SetAudioKeyAsync(string trackUri, FileId fileId, byte[] audioKey, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = GetAudioKeyCacheKey(trackUri, fileId);

        lock (_auxCacheLock)
        {
            // Simple size-based eviction
            if (_audioKeyCache.Count >= MaxAuxCacheSize)
            {
                // Remove oldest entries (first 10%)
                var keysToRemove = _audioKeyCache.Keys.Take(MaxAuxCacheSize / 10).ToList();
                foreach (var k in keysToRemove)
                {
                    _audioKeyCache.Remove(k);
                }
            }

            _audioKeyCache[key] = audioKey;
        }

        // TODO: Persist to SQLite audio_keys table when implemented
        _logger?.LogTrace("Set audio key: {TrackUri}", trackUri);
        return Task.CompletedTask;
    }

    #endregion

    #region CDN URL Operations

    public Task<CdnCacheEntry?> GetCdnUrlAsync(FileId fileId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = fileId.ToBase16();

        lock (_auxCacheLock)
        {
            if (_cdnCache.TryGetValue(key, out var cached))
            {
                if (cached.IsValid)
                {
                    _logger?.LogTrace("CDN cache hit: {FileId}", key);
                    return Task.FromResult<CdnCacheEntry?>(cached);
                }
                else
                {
                    // Expired, remove
                    _cdnCache.Remove(key);
                }
            }
        }

        // TODO: Check SQLite cdn_cache table when implemented
        return Task.FromResult<CdnCacheEntry?>(null);
    }

    public Task SetCdnUrlAsync(FileId fileId, string url, TimeSpan ttl, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = fileId.ToBase16();
        var expiry = DateTimeOffset.UtcNow + ttl;

        lock (_auxCacheLock)
        {
            // Simple size-based eviction
            if (_cdnCache.Count >= MaxAuxCacheSize)
            {
                // Remove expired entries first
                var expired = _cdnCache.Where(kvp => !kvp.Value.IsValid).Select(kvp => kvp.Key).ToList();
                foreach (var k in expired)
                {
                    _cdnCache.Remove(k);
                }

                // If still over, remove oldest
                if (_cdnCache.Count >= MaxAuxCacheSize)
                {
                    var keysToRemove = _cdnCache
                        .OrderBy(kvp => kvp.Value.Expiry)
                        .Take(MaxAuxCacheSize / 10)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    foreach (var k in keysToRemove)
                    {
                        _cdnCache.Remove(k);
                    }
                }
            }

            _cdnCache[key] = new CdnCacheEntry(url, expiry);
        }

        // TODO: Persist to SQLite cdn_cache table when implemented
        _logger?.LogTrace("Set CDN URL: {FileId}, expires in {TTL}", key, ttl);
        return Task.CompletedTask;
    }

    #endregion

    #region Head Data Operations

    public Task<byte[]?> GetHeadDataAsync(FileId fileId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = fileId.ToBase16();

        lock (_auxCacheLock)
        {
            if (_headDataCache.TryGetValue(key, out var cached))
            {
                _logger?.LogTrace("Head data cache hit: {FileId}", key);
                return Task.FromResult<byte[]?>(cached);
            }
        }

        // TODO: Check SQLite head_data table when implemented
        return Task.FromResult<byte[]?>(null);
    }

    public Task SetHeadDataAsync(FileId fileId, byte[] headData, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = fileId.ToBase16();

        lock (_auxCacheLock)
        {
            // Simple size-based eviction
            if (_headDataCache.Count >= MaxAuxCacheSize)
            {
                var keysToRemove = _headDataCache.Keys.Take(MaxAuxCacheSize / 10).ToList();
                foreach (var k in keysToRemove)
                {
                    _headDataCache.Remove(k);
                }
            }

            _headDataCache[key] = headData;
        }

        // TODO: Persist to SQLite head_data table when implemented
        _logger?.LogTrace("Set head data: {FileId}, size={Size}", key, headData.Length);
        return Task.CompletedTask;
    }

    #endregion

    #region Cache Management

    public CacheStatistics GetStatistics()
    {
        lock (_auxCacheLock)
        {
            return new CacheStatistics(
                HotCacheCount: _hotCache.Count,
                HotCacheMaxSize: _hotCache.MaxSize,
                SqliteCacheBytes: 0, // TODO: Get from database
                AudioKeysCount: _audioKeyCache.Count,
                CdnUrlsCount: _cdnCache.Count,
                HeadFilesCount: _headDataCache.Count
            );
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _hotCache.Clear();

        lock (_auxCacheLock)
        {
            _audioKeyCache.Clear();
            _cdnCache.Clear();
            _headDataCache.Clear();
        }

        await _database.ClearAllAsync(ct);
        _logger?.LogInformation("Cache cleared");
    }

    public Task CleanupExpiredAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_auxCacheLock)
        {
            // Remove expired CDN entries
            var expired = _cdnCache.Where(kvp => !kvp.Value.IsValid).Select(kvp => kvp.Key).ToList();
            foreach (var key in expired)
            {
                _cdnCache.Remove(key);
            }

            if (expired.Count > 0)
            {
                _logger?.LogDebug("Cleaned up {Count} expired CDN cache entries", expired.Count);
            }
        }

        return _database.CleanupExpiredExtensionsAsync(ct);
    }

    #endregion

    #region Private Helpers

    private static TrackCacheEntry MapToTrackCacheEntry(CachedEntity entity)
    {
        return new TrackCacheEntry
        {
            Uri = entity.Uri,
            Title = entity.Title,
            Artist = entity.ArtistName,
            Album = entity.AlbumName,
            AlbumUri = entity.AlbumUri,
            DurationMs = entity.DurationMs,
            TrackNumber = entity.TrackNumber,
            DiscNumber = entity.DiscNumber,
            ReleaseYear = entity.ReleaseYear,
            ImageUrl = entity.ImageUrl,
            IsPlayable = true,
            CachedAt = entity.UpdatedAt
        };
    }

    private async Task<TrackCacheEntry> EnrichFromCacheAsync(TrackCacheEntry entry, CancellationToken ct)
    {
        // Try to enrich with cached audio data
        if (entry.PreferredFileId != null)
        {
            var audioKey = await GetAudioKeyAsync(entry.Uri, entry.PreferredFileId.Value, ct);
            if (audioKey != null)
            {
                entry = entry.WithAudioKey(audioKey);
            }

            var cdn = await GetCdnUrlAsync(entry.PreferredFileId.Value, ct);
            if (cdn != null && cdn.IsValid)
            {
                entry = entry.WithCdn(cdn.Url, cdn.Expiry);
            }

            var headData = await GetHeadDataAsync(entry.PreferredFileId.Value, ct);
            if (headData != null)
            {
                entry = entry.WithHeadData(headData);
            }
        }

        return entry;
    }

    private static string GetAudioKeyCacheKey(string trackUri, FileId fileId)
    {
        return $"{trackUri}:{fileId.ToBase16()}";
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _hotCache.Dispose();

        lock (_auxCacheLock)
        {
            _audioKeyCache.Clear();
            _cdnCache.Clear();
            _headDataCache.Clear();
        }

        _logger?.LogDebug("CacheService disposed");
        await Task.CompletedTask;
    }

    #endregion
}
