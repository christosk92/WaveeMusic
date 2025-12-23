using Wavee.Core.Library.Spotify;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Core.Storage.Abstractions;

/// <summary>
/// Interface for SQLite-backed unified metadata and library storage.
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
        SourceType sourceType = SourceType.Spotify,
        string? title = null,
        string? artistName = null,
        string? albumName = null,
        string? albumUri = null,
        int? durationMs = null,
        int? trackNumber = null,
        int? discNumber = null,
        int? releaseYear = null,
        string? imageUrl = null,
        string? genre = null,
        int? trackCount = null,
        int? followerCount = null,
        string? publisher = null,
        int? episodeCount = null,
        string? description = null,
        string? filePath = null,
        string? streamUrl = null,
        long? expiresAt = null,
        long? addedAt = null,
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

    #region Spotify Library Operations

    /// <summary>
    /// Adds or updates an item in the Spotify library (liked songs, saved albums, etc.).
    /// </summary>
    /// <param name="itemUri">The entity URI.</param>
    /// <param name="itemType">The type of item (track, album, artist, show).</param>
    /// <param name="addedAt">When the item was added to the library (Unix seconds).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddToSpotifyLibraryAsync(
        string itemUri,
        SpotifyLibraryItemType itemType,
        long addedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an item from the Spotify library.
    /// </summary>
    Task RemoveFromSpotifyLibraryAsync(string itemUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an item is in the Spotify library.
    /// </summary>
    Task<bool> IsInSpotifyLibraryAsync(string itemUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all items of a specific type from the Spotify library with their entity metadata.
    /// </summary>
    /// <param name="itemType">The type of items to retrieve.</param>
    /// <param name="limit">Maximum number of items.</param>
    /// <param name="offset">Number of items to skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of entities that are in the library.</returns>
    Task<List<CachedEntity>> GetSpotifyLibraryItemsAsync(
        SpotifyLibraryItemType itemType,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of items of a specific type in the Spotify library.
    /// </summary>
    Task<int> GetSpotifyLibraryCountAsync(SpotifyLibraryItemType itemType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all items of a specific type from the Spotify library.
    /// </summary>
    Task ClearSpotifyLibraryAsync(SpotifyLibraryItemType? itemType = null, CancellationToken cancellationToken = default);

    #endregion

    #region Sync State Operations

    /// <summary>
    /// Gets the sync state for a collection type.
    /// </summary>
    /// <param name="collectionType">The collection identifier (e.g., "tracks", "albums").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SyncStateEntry?> GetSyncStateAsync(string collectionType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the sync state for a collection type.
    /// </summary>
    /// <param name="collectionType">The collection identifier.</param>
    /// <param name="revision">The revision token from Spotify.</param>
    /// <param name="itemCount">The number of items in the collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetSyncStateAsync(
        string collectionType,
        string? revision,
        int itemCount,
        CancellationToken cancellationToken = default);

    #endregion

    #region Play History Operations

    /// <summary>
    /// Records a play event.
    /// </summary>
    /// <param name="itemUri">The entity URI that was played.</param>
    /// <param name="durationPlayedMs">How long the item was played in milliseconds.</param>
    /// <param name="completed">Whether playback completed.</param>
    /// <param name="sourceContext">Optional context (playlist, album URI).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordPlayAsync(
        string itemUri,
        int durationPlayedMs,
        bool completed,
        string? sourceContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent play history with entity metadata.
    /// </summary>
    /// <param name="limit">Maximum number of entries.</param>
    /// <param name="offset">Number of entries to skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<PlayHistoryEntry>> GetPlayHistoryAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total play count for an item.
    /// </summary>
    Task<int> GetPlayCountAsync(string itemUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total play time for an item in milliseconds.
    /// </summary>
    Task<long> GetTotalPlayTimeAsync(string itemUri, CancellationToken cancellationToken = default);

    #endregion

    #region Spotify Playlist Operations

    /// <summary>
    /// Upserts a playlist (inserts or updates if exists).
    /// </summary>
    /// <param name="playlist">The playlist to upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertPlaylistAsync(SpotifyPlaylist playlist, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a playlist by URI.
    /// </summary>
    /// <param name="playlistUri">The playlist URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The playlist, or null if not found.</returns>
    Task<SpotifyPlaylist?> GetPlaylistAsync(string playlistUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all playlists.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of all playlists.</returns>
    Task<List<SpotifyPlaylist>> GetAllPlaylistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a playlist by URI.
    /// </summary>
    /// <param name="playlistUri">The playlist URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeletePlaylistAsync(string playlistUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all playlists from the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearAllPlaylistsAsync(CancellationToken cancellationToken = default);

    #endregion
}

/// <summary>
/// Type of item in the Spotify library.
/// </summary>
public enum SpotifyLibraryItemType
{
    Track = 1,
    Album = 2,
    Playlist = 3,
    Artist = 4,
    Show = 5,
    Ban = 6,
    ArtistBan = 7,
    ListenLater = 8,
    YlPin = 9,
    Enhanced = 10
}

/// <summary>
/// Sync state for a collection.
/// </summary>
public sealed record SyncStateEntry(
    string CollectionType,
    string? Revision,
    DateTimeOffset LastSyncAt,
    int ItemCount);

/// <summary>
/// Play history entry with entity metadata.
/// </summary>
public sealed record PlayHistoryEntry(
    long Id,
    string ItemUri,
    DateTimeOffset PlayedAt,
    int DurationPlayedMs,
    bool Completed,
    string? SourceContext,
    CachedEntity? Entity);
