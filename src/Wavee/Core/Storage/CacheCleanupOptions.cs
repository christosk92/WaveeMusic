namespace Wavee.Core.Storage;

/// <summary>
/// Configuration for the background cache cleanup service.
/// </summary>
public sealed class CacheCleanupOptions
{
    /// <summary>
    /// How often the cleanup pass runs. Default: 5 minutes.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum age for cache entries. Entries not accessed within this window are evicted.
    /// Default: 30 minutes.
    /// </summary>
    public TimeSpan DefaultMaxAge { get; set; } = TimeSpan.FromMinutes(30);
}
