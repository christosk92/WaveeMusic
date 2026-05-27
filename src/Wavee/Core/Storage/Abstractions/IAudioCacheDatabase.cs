namespace Wavee.Core.Storage.Abstractions;

/// <summary>
/// Persistent store for the small set of cache rows the audio resolver
/// reads and writes on the hot path: the first ~128 KB of encrypted audio
/// (<c>head_data</c>) used for instant-start playback, and signed CDN URLs
/// with their expiries (<c>cdn_cache</c>) used for warm-start CDN resolve.
///
/// Lives in its own SQLite file separate from <see cref="IMetadataDatabase"/>
/// so the audio pipeline never queues behind a library-sync or
/// playlist-cache writer holding the metadata write lock.
/// </summary>
public interface IAudioCacheDatabase : IAsyncDisposable
{
    /// <summary>
    /// Gets persisted head file data for a FileId (~128 KB of encrypted audio
    /// prefix used for instant-start playback), or null if not stored.
    /// </summary>
    Task<byte[]?> GetPersistedHeadDataAsync(string fileIdHex, CancellationToken ct = default);

    /// <summary>
    /// Persists head file data. Overwrites any existing entry.
    /// </summary>
    Task SetPersistedHeadDataAsync(string fileIdHex, byte[] headData, CancellationToken ct = default);

    /// <summary>
    /// Gets a persisted CDN URL + expiry for a FileId, or null if not stored
    /// or expired. Expired rows are swept opportunistically without failing
    /// the read.
    /// </summary>
    Task<(string Url, DateTimeOffset Expiry)?> GetPersistedCdnUrlAsync(string fileIdHex, CancellationToken ct = default);

    /// <summary>
    /// Persists a CDN URL with expiry. Overwrites any existing entry.
    /// </summary>
    Task SetPersistedCdnUrlAsync(string fileIdHex, string url, DateTimeOffset expiry, CancellationToken ct = default);
}
