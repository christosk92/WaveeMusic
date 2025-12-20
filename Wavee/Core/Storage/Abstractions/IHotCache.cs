namespace Wavee.Core.Storage.Abstractions;

/// <summary>
/// Generic hot cache interface for O(1) LRU caching.
/// </summary>
/// <typeparam name="TEntry">Cache entry type implementing ICacheEntry.</typeparam>
public interface IHotCache<TEntry> : IDisposable where TEntry : class, ICacheEntry
{
    /// <summary>
    /// Gets the current number of entries in the cache.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the maximum cache size.
    /// </summary>
    int MaxSize { get; }

    /// <summary>
    /// Gets a cache entry by URI. Updates LRU order.
    /// </summary>
    /// <param name="uri">Entity URI to look up.</param>
    /// <returns>The cached entry, or null if not found.</returns>
    TEntry? Get(string uri);

    /// <summary>
    /// Gets multiple cache entries by URIs. Updates LRU order.
    /// </summary>
    /// <param name="uris">Entity URIs to look up.</param>
    /// <returns>Dictionary of found entries (missing URIs not included).</returns>
    Dictionary<string, TEntry> GetMany(IEnumerable<string> uris);

    /// <summary>
    /// Tries to get a cache entry without updating LRU order.
    /// </summary>
    /// <param name="uri">Entity URI to look up.</param>
    /// <param name="entry">The cached entry if found.</param>
    /// <returns>True if found, false otherwise.</returns>
    bool TryPeek(string uri, out TEntry? entry);

    /// <summary>
    /// Adds or updates a cache entry.
    /// </summary>
    /// <param name="uri">Entity URI.</param>
    /// <param name="entry">Cache entry to store.</param>
    void Set(string uri, TEntry entry);

    /// <summary>
    /// Adds or updates multiple cache entries.
    /// </summary>
    /// <param name="entries">Entries to store.</param>
    void SetMany(IEnumerable<(string Uri, TEntry Entry)> entries);

    /// <summary>
    /// Removes a specific entry from the cache.
    /// </summary>
    /// <param name="uri">Entity URI to remove.</param>
    /// <returns>True if removed, false if not found.</returns>
    bool Remove(string uri);

    /// <summary>
    /// Checks if an entry exists in the cache.
    /// </summary>
    /// <param name="uri">Entity URI to check.</param>
    /// <returns>True if exists, false otherwise.</returns>
    bool Contains(string uri);

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    void Clear();

    /// <summary>
    /// Gets all URIs currently in the cache (for debugging).
    /// </summary>
    /// <returns>List of URIs in LRU order (most recent first).</returns>
    IReadOnlyList<string> GetAllUris();
}
