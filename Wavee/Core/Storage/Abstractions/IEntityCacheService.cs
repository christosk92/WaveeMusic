namespace Wavee.Core.Storage.Abstractions;

/// <summary>
/// Generic cache service interface for a specific entity type.
/// Provides hot cache + database caching with automatic promotion.
/// </summary>
/// <typeparam name="TEntry">Cache entry type implementing ICacheEntry.</typeparam>
public interface IEntityCacheService<TEntry> : IAsyncDisposable where TEntry : class, ICacheEntry
{
    /// <summary>
    /// The entity type this cache service handles.
    /// </summary>
    EntityType EntityType { get; }

    /// <summary>
    /// Gets a cache entry by URI.
    /// </summary>
    /// <param name="uri">Entity URI to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached entry, or null if not found.</returns>
    Task<TEntry?> GetAsync(string uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets multiple cache entries by URIs.
    /// </summary>
    /// <param name="uris">Entity URIs to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary of found entries (missing URIs not included).</returns>
    Task<Dictionary<string, TEntry>> GetManyAsync(IEnumerable<string> uris, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a cache entry.
    /// </summary>
    /// <param name="uri">Entity URI.</param>
    /// <param name="entry">Cache entry to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(string uri, TEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores multiple cache entries.
    /// </summary>
    /// <param name="entries">Entries to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetManyAsync(IEnumerable<(string Uri, TEntry Entry)> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates a cache entry by URI.
    /// </summary>
    /// <param name="uri">Entity URI to invalidate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InvalidateAsync(string uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all entries from the hot cache.
    /// </summary>
    void ClearHotCache();

    /// <summary>
    /// Gets the current number of entries in the hot cache.
    /// </summary>
    int HotCacheCount { get; }

    /// <summary>
    /// Gets the maximum hot cache size.
    /// </summary>
    int HotCacheMaxSize { get; }
}
