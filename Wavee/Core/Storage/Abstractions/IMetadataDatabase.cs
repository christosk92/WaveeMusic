using Wavee.Core.Library.Spotify;
using Wavee.Core.Storage.Entities;
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
    /// Bulk-reads cached extension data for many URIs in one connection.
    /// Includes zero-length blobs (used as a "known absent" negative cache marker).
    /// Omits URIs that are not present or expired.
    /// </summary>
    Task<IReadOnlyDictionary<string, byte[]>> GetExtensionsBulkAsync(
        IReadOnlyList<string> entityUris,
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

    /// <summary>
    /// Wipes every user-bound table in one transaction: entities, localized_entities,
    /// extension_cache, localized_extension_cache, spotify_library, sync_state,
    /// spotify_playlists, rootlist_cache, library_outbox. Used when the signed-in
    /// Spotify user changes — the previous user's library/playlists/sync revisions
    /// must not bleed into the new session.
    /// </summary>
    Task WipeAllUserDataAsync(CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Gets URIs in spotify_library that have no corresponding row in the entities table.
    /// Used by the metadata backfill step after sync.
    /// </summary>
    Task<List<string>> GetLibraryUrisMissingMetadataAsync(
        SpotifyLibraryItemType itemType,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Clears all sync_state rows, forcing a full re-sync of every collection type on the next sync.
    /// </summary>
    Task ClearAllSyncStateAsync(CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Upserts a cached playlist snapshot used by PlaylistCacheService.
    /// </summary>
    Task UpsertPlaylistCacheEntryAsync(PlaylistCacheEntry playlist, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a cached playlist snapshot.
    /// </summary>
    Task<PlaylistCacheEntry?> GetPlaylistCacheEntryAsync(
        string playlistUri,
        bool touchAccess = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the last-accessed timestamp for a cached playlist snapshot.
    /// </summary>
    Task TouchPlaylistCacheEntryAsync(
        string playlistUri,
        DateTimeOffset accessedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recently accessed cached playlists.
    /// </summary>
    Task<List<PlaylistCacheEntry>> GetRecentPlaylistCacheEntriesAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the user's cached rootlist snapshot.
    /// </summary>
    Task UpsertRootlistCacheEntryAsync(RootlistCacheEntry rootlist, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user's cached rootlist snapshot.
    /// </summary>
    Task<RootlistCacheEntry?> GetRootlistCacheEntryAsync(
        string rootlistUri,
        bool touchAccess = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the last-accessed timestamp for the cached rootlist snapshot.
    /// </summary>
    Task TouchRootlistCacheEntryAsync(
        string rootlistUri,
        DateTimeOffset accessedAt,
        CancellationToken cancellationToken = default);

    #endregion

    #region Color Cache Operations

    /// <summary>
    /// Caches album track list as serialized JSON.
    /// </summary>
    Task SetAlbumTracksCacheAsync(string albumUri, string jsonData, CancellationToken ct = default);

    /// <summary>
    /// Gets cached album track list JSON, or null if not cached.
    /// </summary>
    Task<string?> GetAlbumTracksCacheAsync(string albumUri, CancellationToken ct = default);

    /// <summary>
    /// Caches extracted colors for an image URL.
    /// </summary>
    Task SetColorCacheAsync(string imageUrl, string? darkHex, string? lightHex, string? rawHex, CancellationToken ct = default);

    /// <summary>
    /// Gets cached extracted colors for an image URL.
    /// </summary>
    /// <returns>Color hex values, or null if not cached.</returns>
    Task<(string? DarkHex, string? LightHex, string? RawHex)?> GetColorCacheAsync(string imageUrl, CancellationToken ct = default);

    #endregion

    #region Lyrics Cache Operations

    /// <summary>
    /// Gets cached lyrics JSON for a track URI.
    /// </summary>
    /// <returns>JSON data and provider name, or null if not cached.</returns>
    Task<(string JsonData, string? Provider)?> GetLyricsCacheAsync(string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Caches lyrics JSON for a track URI.
    /// </summary>
    Task SetLyricsCacheAsync(string trackUri, string? provider, string jsonData, bool hasSyllableSync, CancellationToken ct = default);

    /// <summary>
    /// Deletes cached lyrics for a track URI.
    /// </summary>
    Task DeleteLyricsCacheAsync(string trackUri, CancellationToken ct = default);

    #endregion

    #region Audio Key + Head Data Persistence

    // Both AudioKey (16 bytes) and HeadData (~128 KB) are safe to persist
    // permanently: FileIds never change their content on Spotify's side, and the
    // AES key never rotates for a given file. Caching them across restarts cuts
    // cold-start latency and makes offline playback of previously heard tracks
    // possible even if the session is briefly offline when the user presses play.

    /// <summary>
    /// Gets a persisted AudioKey (16-byte AES key) for a FileId, or null if not stored.
    /// </summary>
    Task<byte[]?> GetPersistedAudioKeyAsync(string fileIdHex, CancellationToken ct = default);

    /// <summary>
    /// Persists an AudioKey. Overwrites any existing entry.
    /// </summary>
    Task SetPersistedAudioKeyAsync(string fileIdHex, string? trackUri, byte[] keyBytes, CancellationToken ct = default);

    /// <summary>
    /// Gets a persisted PlayPlay obfuscated key (16 bytes), or null if not stored.
    /// Useless without the cipher implementation, so safe to persist alongside AudioKeys.
    /// </summary>
    Task<byte[]?> GetPersistedPlayPlayObfuscatedKeyAsync(string fileIdHex, CancellationToken ct = default);

    /// <summary>
    /// Persists a PlayPlay obfuscated key. Overwrites any existing entry.
    /// </summary>
    Task SetPersistedPlayPlayObfuscatedKeyAsync(string fileIdHex, byte[] obfuscatedKey, CancellationToken ct = default);

    /// <summary>
    /// Gets persisted head file data for a FileId (~128 KB of encrypted audio
    /// prefix used for instant-start playback), or null if not stored.
    /// </summary>
    Task<byte[]?> GetPersistedHeadDataAsync(string fileIdHex, CancellationToken ct = default);

    /// <summary>
    /// Persists head file data. Overwrites any existing entry.
    /// </summary>
    Task SetPersistedHeadDataAsync(string fileIdHex, byte[] headData, CancellationToken ct = default);

    #endregion

    #region Media Override Operations

    /// <summary>
    /// Gets the local override/sync state for a media asset.
    /// </summary>
    Task<MediaOverrideEntry?> GetMediaOverrideAsync(
        MediaOverrideAssetType assetType,
        string entityKey,
        CancellationToken ct = default);

    /// <summary>
    /// Upserts the local override/sync state for a media asset.
    /// </summary>
    Task SetMediaOverrideAsync(MediaOverrideEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Deletes the local override/sync state for a media asset.
    /// </summary>
    Task DeleteMediaOverrideAsync(
        MediaOverrideAssetType assetType,
        string entityKey,
        CancellationToken ct = default);

    #endregion

    #region Library Outbox Operations

    /// <summary>
    /// Enqueues a library operation for background API sync.
    /// If a pending op for the same URI exists, it is replaced.
    /// </summary>
    Task EnqueueLibraryOpAsync(string itemUri, SpotifyLibraryItemType itemType, LibraryOutboxOperation operation, CancellationToken ct = default);

    /// <summary>
    /// Dequeues pending library operations ordered by creation time.
    /// </summary>
    Task<List<LibraryOutboxEntry>> DequeueLibraryOpsAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Marks a library outbox entry as completed (deletes it).
    /// </summary>
    Task CompleteLibraryOpAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Marks a library outbox entry as failed, incrementing retry count.
    /// </summary>
    Task FailLibraryOpAsync(long id, string? error, CancellationToken ct = default);

    #endregion
}

/// <summary>
/// Outbox operation type.
/// </summary>
public enum LibraryOutboxOperation { Save = 0, Remove = 1 }

/// <summary>
/// Media asset categories that can be locally overridden and synced.
/// </summary>
public enum MediaOverrideAssetType
{
    AlbumArt = 1,
    ArtistImage = 2,
    ArtistHeroImage = 3,
    DetailsCanvas = 4,
}

/// <summary>
/// Origin of the currently effective local asset snapshot.
/// </summary>
public enum MediaOverrideSource
{
    None = 0,
    UpstreamSnapshot = 1,
    ManualOverride = 2,
}

/// <summary>
/// Persisted local state for a media override and its upstream review lifecycle.
/// </summary>
public sealed record MediaOverrideEntry
{
    public MediaOverrideAssetType AssetType { get; init; }
    public required string EntityKey { get; init; }
    public string? EffectiveAssetUrl { get; init; }
    public MediaOverrideSource EffectiveSource { get; init; }
    public string? LastSeenUpstreamUrl { get; init; }
    public string? PendingAssetUrl { get; init; }
    public string? LastReviewedUpstreamUrl { get; init; }
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
}

/// <summary>
/// A pending library operation in the outbox.
/// </summary>
public sealed record LibraryOutboxEntry
{
    public long Id { get; init; }
    public required string ItemUri { get; init; }
    public SpotifyLibraryItemType ItemType { get; init; }
    public LibraryOutboxOperation Operation { get; init; }
    public long CreatedAt { get; init; }
    public int RetryCount { get; init; }
    public string? LastError { get; init; }
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
