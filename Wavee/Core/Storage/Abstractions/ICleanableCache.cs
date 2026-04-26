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

    /// <summary>
    /// Removes every entry from the cache. Used by the in-app memory diagnostics
    /// panel to drop the entire warm tier on demand.
    /// </summary>
    /// <returns>Number of entries that were present before the clear.</returns>
    Task<int> ClearAsync(CancellationToken ct = default);
}
