namespace Wavee.Core.Storage.Abstractions;

/// <summary>
/// Interface for caches that participate in periodic background cleanup.
/// </summary>
public interface ICleanableCache
{
    /// <summary>
    /// Display name for logging.
    /// </summary>
    string CacheName { get; }

    /// <summary>
    /// Current number of entries in the cache.
    /// </summary>
    int CurrentCount { get; }

    /// <summary>
    /// Removes entries not accessed within the specified time window.
    /// </summary>
    /// <returns>Number of entries removed.</returns>
    Task<int> CleanupStaleEntriesAsync(TimeSpan maxAge, CancellationToken ct = default);
}
