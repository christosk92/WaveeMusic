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
    /// Maximum entries in the album hot cache.
    /// Default: 2,000
    /// </summary>
    public int AlbumHotCacheSize { get; set; } = 2_000;

    /// <summary>
    /// Maximum entries in the artist hot cache.
    /// Default: 1,000
    /// </summary>
    public int ArtistHotCacheSize { get; set; } = 1_000;

    /// <summary>
    /// Maximum entries in the playlist hot cache.
    /// Default: 500
    /// </summary>
    public int PlaylistHotCacheSize { get; set; } = 500;

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
}
