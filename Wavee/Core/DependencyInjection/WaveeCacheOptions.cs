namespace Wavee.Core.DependencyInjection;

/// <summary>
/// Configuration options for Wavee cache services.
/// </summary>
public class WaveeCacheOptions
{
    /// <summary>
    /// Path to the SQLite database file.
    /// Default: %APPDATA%/Wavee/metadata.db
    /// </summary>
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wavee",
        "metadata.db");

    /// <summary>
    /// Root directory for the local-file artwork cache. Backs the
    /// <c>wavee-artwork://{hash}</c> URI scheme. Default:
    /// <c>%LOCALAPPDATA%/Wavee/local-artwork</c>.
    /// </summary>
    public string LocalArtworkDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee",
        "local-artwork");

    /// <summary>
    /// Preferred 2-character Spotify locale for localized metadata cache rows.
    /// Null or empty keeps the shared default-locale cache.
    /// </summary>
    public string? SpotifyMetadataLocale { get; set; }

    /// <summary>
    /// Maximum entries in the metadata database's internal hot cache.
    /// Default: 1000
    /// </summary>
    public int DatabaseHotCacheSize { get; set; } = 1000;

    /// <summary>
    /// Maximum entries in the track hot cache.
    /// Default: 10,000
    /// </summary>
    public int TrackHotCacheSize { get; set; } = 10_000;

    /// <summary>
    /// Maximum entries in the album hot cache. Tuned for the rich
    /// <c>AlbumDetailResult</c> path (track list + merch + alternate releases +
    /// palette) — at ~30 KB/entry, 512 caps worst-case at ~15 MB.
    /// Default: 512
    /// </summary>
    public int AlbumHotCacheSize { get; set; } = 512;

    /// <summary>
    /// Maximum entries in the artist hot cache. Tuned for the rich
    /// <c>ArtistOverviewResult</c> path (palette + releases + concerts +
    /// gallery + biography) — largest of the detail types, ~50 KB/entry, so
    /// 256 caps worst-case at ~13 MB.
    /// Default: 256
    /// </summary>
    public int ArtistHotCacheSize { get; set; } = 256;

    /// <summary>
    /// Maximum entries in the playlist hot cache. The active playlist tier is
    /// <c>PlaylistCacheService</c> (its own SQLite-backed cache); this size
    /// applies to the lean <c>PlaylistCacheEntry</c> hot cache which has no
    /// consumer today. Kept low to match.
    /// Default: 256
    /// </summary>
    public int PlaylistHotCacheSize { get; set; } = 256;

    /// <summary>
    /// Maximum entries in the show hot cache.
    /// Default: 500
    /// </summary>
    public int ShowHotCacheSize { get; set; } = 500;

    /// <summary>
    /// Maximum entries in the episode hot cache.
    /// Default: 2,000
    /// </summary>
    public int EpisodeHotCacheSize { get; set; } = 2_000;

    /// <summary>
    /// Maximum entries in the user hot cache.
    /// Default: 500
    /// </summary>
    public int UserHotCacheSize { get; set; } = 500;

    /// <summary>
    /// Maximum entries in the context hot cache (resolved playlists/albums).
    /// Default: 50 (contexts are larger objects with TTL-based expiry)
    /// </summary>
    public int ContextCacheSize { get; set; } = 50;

    /// <summary>
    /// Maximum entries in each of the CacheService auxiliary caches
    /// (audio keys, CDN URLs, head data). Default: 1000 per cache.
    /// </summary>
    public int AudioAuxCacheSize { get; set; } = 1000;

    /// <summary>
    /// Interval between background cache cleanup passes. Default: 5 minutes.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default TTL for cache entries. Entries not accessed within this window are evicted.
    /// Default: 30 minutes.
    /// </summary>
    public TimeSpan DefaultMaxAge { get; set; } = TimeSpan.FromMinutes(30);
}
