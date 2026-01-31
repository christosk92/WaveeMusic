using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Service for retrieving user library data.
/// </summary>
public interface ILibraryDataService
{
    /// <summary>
    /// Gets library statistics for sidebar badges.
    /// </summary>
    Task<LibraryStatsDto> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all library items. Sorting/filtering is done client-side.
    /// </summary>
    Task<IReadOnlyList<LibraryItemDto>> GetAllItemsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets recently played items.
    /// </summary>
    Task<IReadOnlyList<LibraryItemDto>> GetRecentlyPlayedAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Gets user's playlists.
    /// </summary>
    Task<IReadOnlyList<PlaylistSummaryDto>> GetUserPlaylistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all albums in the user's library.
    /// </summary>
    Task<IReadOnlyList<LibraryAlbumDto>> GetAlbumsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all artists in the user's library.
    /// </summary>
    Task<IReadOnlyList<LibraryArtistDto>> GetArtistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets top tracks for a specific artist.
    /// </summary>
    Task<IReadOnlyList<LibraryArtistTopTrackDto>> GetArtistTopTracksAsync(string artistId, CancellationToken ct = default);

    /// <summary>
    /// Gets albums for a specific artist.
    /// </summary>
    Task<IReadOnlyList<LibraryArtistAlbumDto>> GetArtistAlbumsAsync(string artistId, CancellationToken ct = default);

    /// <summary>
    /// Gets all liked/saved songs in the user's library.
    /// </summary>
    Task<IReadOnlyList<LikedSongDto>> GetLikedSongsAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a new playlist and returns the created playlist summary.
    /// </summary>
    Task<PlaylistSummaryDto> CreatePlaylistAsync(string name, IReadOnlyList<string>? trackIds = null, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed information about a specific playlist.
    /// </summary>
    Task<PlaylistDetailDto> GetPlaylistAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Gets tracks for a specific playlist.
    /// </summary>
    Task<IReadOnlyList<PlaylistTrackDto>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Removes tracks from a playlist.
    /// </summary>
    Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default);

    /// <summary>
    /// Event raised when playlists change (created, deleted, updated).
    /// </summary>
    event EventHandler? PlaylistsChanged;
}
