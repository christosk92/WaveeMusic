using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Models.Playlist;

namespace Wavee.UI.WinUI.Data.Contracts.Services;

/// <summary>
/// Service for fetching and managing playlists.
/// </summary>
public interface IPlaylistService
{
    /// <summary>
    /// Gets playlist details.
    /// </summary>
    Task<PlaylistDetailsModel?> GetPlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets playlist tracks with pagination.
    /// </summary>
    Task<IReadOnlyList<PlaylistTrackModel>> GetPlaylistTracksAsync(
        string playlistId,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current user's playlists.
    /// </summary>
    Task<IReadOnlyList<PlaylistDetailsModel>> GetUserPlaylistsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new playlist.
    /// </summary>
    Task<PlaylistDetailsModel?> CreatePlaylistAsync(
        string name,
        string? description = null,
        bool isPublic = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds tracks to a playlist.
    /// </summary>
    Task AddTracksAsync(
        string playlistId,
        IEnumerable<string> trackUris,
        int? position = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes tracks from a playlist.
    /// </summary>
    Task RemoveTracksAsync(
        string playlistId,
        IEnumerable<string> trackUris,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a playlist is followed/saved by the current user.
    /// </summary>
    Task<bool> IsFollowingAsync(
        string playlistId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Follows or unfollows a playlist.
    /// </summary>
    Task SetFollowingAsync(
        string playlistId,
        bool follow,
        CancellationToken cancellationToken = default);
}
