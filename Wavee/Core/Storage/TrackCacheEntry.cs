using Wavee.Core.Audio;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage;

/// <summary>
/// Comprehensive track cache entry containing all cached data for a track.
/// Used by CacheService for unified caching.
/// </summary>
public sealed record TrackCacheEntry : ICacheEntry
{
    // Basic metadata (from ExtendedMetadata TRACK_V4)
    public required string Uri { get; init; }

    /// <inheritdoc />
    public EntityType EntityType => EntityType.Track;
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumUri { get; init; }
    public int? DurationMs { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public int? ReleaseYear { get; init; }
    public string? ImageUrl { get; init; }
    public bool IsExplicit { get; init; }
    public bool IsPlayable { get; init; } = true;

    // Audio data (for playback)
    public FileId? PreferredFileId { get; init; }
    public IReadOnlyList<FileId>? AvailableFiles { get; init; }

    // Cached audio key (decryption key for the preferred file)
    public byte[]? AudioKey { get; init; }

    // CDN data (with TTL)
    public string? CdnUrl { get; init; }
    public DateTimeOffset? CdnExpiry { get; init; }

    // Head file data (for instant start - first ~16KB pre-decrypted)
    public byte[]? HeadData { get; init; }

    // Timestamps
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAccessedAt { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }

    /// <summary>
    /// Gets whether the CDN URL is still valid.
    /// </summary>
    public bool IsCdnValid => CdnUrl != null && CdnExpiry.HasValue && CdnExpiry.Value > DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets whether the track has all data needed for instant playback.
    /// </summary>
    public bool HasInstantPlaybackData => HeadData != null && AudioKey != null && IsCdnValid;

    /// <summary>
    /// Creates a copy with updated CDN data.
    /// </summary>
    public TrackCacheEntry WithCdn(string url, DateTimeOffset expiry) =>
        this with { CdnUrl = url, CdnExpiry = expiry };

    /// <summary>
    /// Creates a copy with updated audio key.
    /// </summary>
    public TrackCacheEntry WithAudioKey(byte[] key) =>
        this with { AudioKey = key };

    /// <summary>
    /// Creates a copy with updated head data.
    /// </summary>
    public TrackCacheEntry WithHeadData(byte[] data) =>
        this with { HeadData = data };

    /// <summary>
    /// Creates a copy with updated last played timestamp.
    /// </summary>
    public TrackCacheEntry WithLastPlayed(DateTimeOffset timestamp) =>
        this with { LastPlayedAt = timestamp };
}

/// <summary>
/// CDN URL cache entry with expiry.
/// </summary>
public sealed record CdnCacheEntry(string Url, DateTimeOffset Expiry)
{
    /// <summary>
    /// Gets whether the CDN URL is still valid.
    /// </summary>
    public bool IsValid => Expiry > DateTimeOffset.UtcNow;
}

/// <summary>
/// Cache statistics for monitoring.
/// </summary>
public sealed record CacheStatistics(
    int HotCacheCount,
    int HotCacheMaxSize,
    long SqliteCacheBytes,
    int AudioKeysCount,
    int CdnUrlsCount,
    int HeadFilesCount
)
{
    public double HotCacheUtilization => HotCacheMaxSize > 0 ? (double)HotCacheCount / HotCacheMaxSize : 0;
    public double SqliteCacheSizeMB => SqliteCacheBytes / (1024.0 * 1024.0);
}
