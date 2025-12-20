using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Core.Storage.Abstractions;

/// <summary>
/// Interface for SQLite-backed metadata storage.
/// </summary>
public interface IMetadataDatabase : IAsyncDisposable
{
    #region Entity Operations

    /// <summary>
    /// Upserts an entity with its metadata properties.
    /// </summary>
    Task UpsertEntityAsync(
        string uri,
        EntityType entityType,
        string? title = null,
        string? artistName = null,
        string? albumName = null,
        string? albumUri = null,
        int? durationMs = null,
        int? trackNumber = null,
        int? discNumber = null,
        int? releaseYear = null,
        string? imageUrl = null,
        int? trackCount = null,
        int? followerCount = null,
        string? publisher = null,
        int? episodeCount = null,
        string? description = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an entity by URI.
    /// </summary>
    Task<CachedEntity?> GetEntityAsync(string uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets multiple entities by URIs.
    /// </summary>
    Task<List<CachedEntity>> GetEntitiesAsync(IEnumerable<string> uris, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries entities with optional filters.
    /// </summary>
    Task<List<CachedEntity>> QueryEntitiesAsync(
        EntityType? entityType = null,
        string? artistNameContains = null,
        string? albumNameContains = null,
        string? titleContains = null,
        int? minDurationMs = null,
        int? maxDurationMs = null,
        int? minYear = null,
        int? maxYear = null,
        string? orderBy = null,
        bool descending = false,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entity by URI.
    /// </summary>
    Task DeleteEntityAsync(string uri, CancellationToken cancellationToken = default);

    #endregion

    #region Extension Cache Operations

    /// <summary>
    /// Gets cached extension data, checking hot cache first then SQLite.
    /// </summary>
    /// <returns>Cached data and etag, or null if not found or expired.</returns>
    Task<(byte[] Data, string? Etag)?> GetExtensionAsync(
        string entityUri,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the etag for cached extension data (for conditional requests).
    /// Returns etag even if data is expired.
    /// </summary>
    Task<string?> GetExtensionEtagAsync(
        string entityUri,
        ExtensionKind extensionKind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores extension data in both hot cache and SQLite.
    /// </summary>
    Task SetExtensionAsync(
        string entityUri,
        ExtensionKind extensionKind,
        byte[] data,
        string? etag,
        long ttlSeconds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates all cached data for an entity.
    /// </summary>
    Task InvalidateEntityAsync(string entityUri, CancellationToken cancellationToken = default);

    #endregion

    #region Database Maintenance

    /// <summary>
    /// Removes expired extension cache entries.
    /// </summary>
    /// <returns>Number of entries removed.</returns>
    Task<int> CleanupExpiredExtensionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets database statistics.
    /// </summary>
    Task<DatabaseStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Compacts the database file (VACUUM).
    /// </summary>
    Task VacuumAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all data from the database.
    /// </summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);

    #endregion
}
