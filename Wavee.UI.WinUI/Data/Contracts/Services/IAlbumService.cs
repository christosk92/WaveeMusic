using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Models.Album;

namespace Wavee.UI.WinUI.Data.Contracts.Services;

/// <summary>
/// Service for fetching album data.
/// </summary>
public interface IAlbumService
{
    /// <summary>
    /// Gets full album details including tracks.
    /// </summary>
    Task<AlbumDetailsModel?> GetAlbumAsync(
        string albumId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets album tracks with pagination support.
    /// </summary>
    Task<IReadOnlyList<AlbumTrackModel>> GetAlbumTracksAsync(
        string albumId,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if album is saved in user's library.
    /// </summary>
    Task<bool> IsSavedAsync(
        string albumId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves or removes album from user's library.
    /// </summary>
    Task SetSavedAsync(
        string albumId,
        bool saved,
        CancellationToken cancellationToken = default);
}
