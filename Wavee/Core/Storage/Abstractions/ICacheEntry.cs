namespace Wavee.Core.Storage.Abstractions;

/// <summary>
/// Base interface for all cache entries.
/// </summary>
public interface ICacheEntry
{
    /// <summary>
    /// Spotify URI of the cached entity (e.g., "spotify:track:xxx").
    /// </summary>
    string Uri { get; }

    /// <summary>
    /// Entity type for this cache entry.
    /// </summary>
    EntityType EntityType { get; }

    /// <summary>
    /// When this entry was cached.
    /// </summary>
    DateTimeOffset CachedAt { get; }

    /// <summary>
    /// When this entry was last accessed (for LRU tracking).
    /// </summary>
    DateTimeOffset? LastAccessedAt { get; }
}
