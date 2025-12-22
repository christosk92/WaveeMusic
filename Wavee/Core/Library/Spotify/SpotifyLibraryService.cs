using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Library.Spotify;

/// <summary>
/// Implementation of ISpotifyLibraryService.
/// </summary>
/// <remarks>
/// TODO: Implement actual sync logic using spclient endpoints.
/// Currently all methods throw NotImplementedException.
/// </remarks>
public sealed class SpotifyLibraryService : ISpotifyLibraryService
{
    private readonly LibraryDatabase _database;
    private readonly ILogger? _logger;
    private readonly Subject<SyncProgress> _progressSubject = new();
    private bool _disposed;

    public SpotifyLibraryService(LibraryDatabase database, ILogger? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger;
    }

    /// <inheritdoc/>
    public IObservable<SyncProgress> SyncProgress => _progressSubject;

    #region Sync Operations

    /// <inheritdoc/>
    public Task SyncAllAsync(CancellationToken ct = default)
    {
        // TODO: Implement - call SyncTracksAsync, SyncAlbumsAsync, SyncPlaylistsAsync, SyncArtistsAsync
        throw new NotImplementedException("Spotify library sync not yet implemented");
    }

    /// <inheritdoc/>
    public Task SyncTracksAsync(CancellationToken ct = default)
    {
        // TODO: Implement - fetch liked songs from spclient
        // Endpoint: /collection/v2/{username}/tracks or similar
        throw new NotImplementedException("Track sync not yet implemented");
    }

    /// <inheritdoc/>
    public Task SyncAlbumsAsync(CancellationToken ct = default)
    {
        // TODO: Implement - fetch saved albums from spclient
        throw new NotImplementedException("Album sync not yet implemented");
    }

    /// <inheritdoc/>
    public Task SyncPlaylistsAsync(CancellationToken ct = default)
    {
        // TODO: Implement - fetch user playlists from spclient
        throw new NotImplementedException("Playlist sync not yet implemented");
    }

    /// <inheritdoc/>
    public Task SyncArtistsAsync(CancellationToken ct = default)
    {
        // TODO: Implement - fetch followed artists from spclient
        throw new NotImplementedException("Artist sync not yet implemented");
    }

    #endregion

    #region Read Operations

    /// <inheritdoc/>
    public Task<IReadOnlyList<LibraryItem>> GetLikedSongsAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        // TODO: Implement - query spotify_library table joined with library_items
        throw new NotImplementedException("GetLikedSongs not yet implemented");
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LibraryItem>> GetSavedAlbumsAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        // TODO: Implement - query spotify_library table for albums
        throw new NotImplementedException("GetSavedAlbums not yet implemented");
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SpotifyPlaylist>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        // TODO: Implement - query spotify_playlists table
        throw new NotImplementedException("GetPlaylists not yet implemented");
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LibraryItem>> GetFollowedArtistsAsync(CancellationToken ct = default)
    {
        // TODO: Implement - query spotify_library table for artists
        throw new NotImplementedException("GetFollowedArtists not yet implemented");
    }

    /// <inheritdoc/>
    public Task<bool> IsTrackLikedAsync(string trackUri, CancellationToken ct = default)
    {
        // TODO: Implement - check if track exists in spotify_library with type=Track
        throw new NotImplementedException("IsTrackLiked not yet implemented");
    }

    /// <inheritdoc/>
    public Task<bool> IsAlbumSavedAsync(string albumUri, CancellationToken ct = default)
    {
        // TODO: Implement - check if album exists in spotify_library with type=Album
        throw new NotImplementedException("IsAlbumSaved not yet implemented");
    }

    #endregion

    #region Write Operations

    /// <inheritdoc/>
    public Task<bool> SaveTrackAsync(string trackUri, CancellationToken ct = default)
    {
        // TODO: Implement - call spclient to add to collection, then update local DB
        throw new NotImplementedException("SaveTrack not yet implemented");
    }

    /// <inheritdoc/>
    public Task<bool> RemoveTrackAsync(string trackUri, CancellationToken ct = default)
    {
        // TODO: Implement - call spclient to remove from collection, then update local DB
        throw new NotImplementedException("RemoveTrack not yet implemented");
    }

    /// <inheritdoc/>
    public Task<bool> SaveAlbumAsync(string albumUri, CancellationToken ct = default)
    {
        // TODO: Implement
        throw new NotImplementedException("SaveAlbum not yet implemented");
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAlbumAsync(string albumUri, CancellationToken ct = default)
    {
        // TODO: Implement
        throw new NotImplementedException("RemoveAlbum not yet implemented");
    }

    /// <inheritdoc/>
    public Task<bool> FollowArtistAsync(string artistUri, CancellationToken ct = default)
    {
        // TODO: Implement
        throw new NotImplementedException("FollowArtist not yet implemented");
    }

    /// <inheritdoc/>
    public Task<bool> UnfollowArtistAsync(string artistUri, CancellationToken ct = default)
    {
        // TODO: Implement
        throw new NotImplementedException("UnfollowArtist not yet implemented");
    }

    #endregion

    #region State

    /// <inheritdoc/>
    public Task<SpotifySyncState> GetSyncStateAsync(CancellationToken ct = default)
    {
        // TODO: Implement - query sync_state table for each collection type
        // For now return empty state
        return Task.FromResult(SpotifySyncState.Empty);
    }

    #endregion

    #region Disposal

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _progressSubject.OnCompleted();
        _progressSubject.Dispose();

        return ValueTask.CompletedTask;
    }

    #endregion
}
