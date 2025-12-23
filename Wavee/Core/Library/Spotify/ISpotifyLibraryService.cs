namespace Wavee.Core.Library.Spotify;

/// <summary>
/// Service for syncing and managing the user's Spotify library.
/// </summary>
/// <remarks>
/// Handles:
/// - Liked Songs (saved tracks)
/// - Saved Albums
/// - User Playlists (owned + followed)
/// - Followed Artists
///
/// Supports incremental sync using Spotify's revision-based diff protocol.
/// </remarks>
public interface ISpotifyLibraryService : IAsyncDisposable
{
    #region Sync Operations

    /// <summary>
    /// Syncs all library collections (tracks, albums, playlists, artists).
    /// </summary>
    Task SyncAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs liked songs (saved tracks).
    /// </summary>
    Task SyncTracksAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs saved albums.
    /// </summary>
    Task SyncAlbumsAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs user playlists (owned and followed).
    /// </summary>
    Task SyncPlaylistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs followed artists.
    /// </summary>
    Task SyncArtistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs subscribed podcast shows.
    /// </summary>
    Task SyncShowsAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs banned tracks.
    /// </summary>
    Task SyncBansAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs banned artists.
    /// </summary>
    Task SyncArtistBansAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs listen later queue.
    /// </summary>
    Task SyncListenLaterAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs Your Library pinned items.
    /// </summary>
    Task SyncYlPinsAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs enhanced playlist tracks.
    /// </summary>
    Task SyncEnhancedAsync(CancellationToken ct = default);

    #endregion

    #region Read Operations

    /// <summary>
    /// Gets liked songs from the local database.
    /// </summary>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <param name="offset">Number of items to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of liked songs as LibraryItems.</returns>
    Task<IReadOnlyList<LibraryItem>> GetLikedSongsAsync(int limit = 50, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Gets saved albums from the local database.
    /// </summary>
    Task<IReadOnlyList<LibraryItem>> GetSavedAlbumsAsync(int limit = 50, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Gets user playlists from the local database.
    /// </summary>
    Task<IReadOnlyList<SpotifyPlaylist>> GetPlaylistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets followed artists from the local database.
    /// </summary>
    Task<IReadOnlyList<LibraryItem>> GetFollowedArtistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if a track is in the user's liked songs.
    /// </summary>
    /// <param name="trackUri">The track URI to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the track is liked.</returns>
    Task<bool> IsTrackLikedAsync(string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Checks if an album is saved.
    /// </summary>
    Task<bool> IsAlbumSavedAsync(string albumUri, CancellationToken ct = default);

    #endregion

    #region Write Operations (Modify Spotify's Library)

    /// <summary>
    /// Saves a track to liked songs (both locally and on Spotify).
    /// </summary>
    /// <param name="trackUri">The track URI to save.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if successful.</returns>
    Task<bool> SaveTrackAsync(string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Removes a track from liked songs.
    /// </summary>
    Task<bool> RemoveTrackAsync(string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Saves an album to the library.
    /// </summary>
    Task<bool> SaveAlbumAsync(string albumUri, CancellationToken ct = default);

    /// <summary>
    /// Removes an album from the library.
    /// </summary>
    Task<bool> RemoveAlbumAsync(string albumUri, CancellationToken ct = default);

    /// <summary>
    /// Follows an artist.
    /// </summary>
    Task<bool> FollowArtistAsync(string artistUri, CancellationToken ct = default);

    /// <summary>
    /// Unfollows an artist.
    /// </summary>
    Task<bool> UnfollowArtistAsync(string artistUri, CancellationToken ct = default);

    #endregion

    #region State

    /// <summary>
    /// Gets the current sync state for all collections.
    /// </summary>
    Task<SpotifySyncState> GetSyncStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Observable for sync progress updates.
    /// </summary>
    IObservable<SyncProgress> SyncProgress { get; }

    #endregion
}

/// <summary>
/// Progress update for library sync operations.
/// </summary>
/// <param name="CollectionType">The collection being synced (tracks, albums, playlists, artists).</param>
/// <param name="Current">Current item count processed.</param>
/// <param name="Total">Total items to process (0 if unknown).</param>
/// <param name="Message">Status message.</param>
public sealed record SyncProgress(
    string CollectionType,
    int Current,
    int Total,
    string Message);
