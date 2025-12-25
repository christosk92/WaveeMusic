using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Models.Album;
using Wavee.UI.WinUI.Data.Models.Artist;
using Wavee.UI.WinUI.Data.Models.Library;

namespace Wavee.UI.WinUI.Data.Contracts.Services;

/// <summary>
/// UI-specific library service for managing saved content.
/// </summary>
public interface ILibraryService
{
    /// <summary>
    /// Gets saved tracks with pagination.
    /// </summary>
    Task<LibraryPageModel<SavedTrackModel>> GetSavedTracksAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets saved albums with pagination.
    /// </summary>
    Task<LibraryPageModel<AlbumDetailsModel>> GetSavedAlbumsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets followed artists with pagination.
    /// </summary>
    Task<LibraryPageModel<ArtistDetailsModel>> GetFollowedArtistsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a track is saved in the library.
    /// </summary>
    Task<bool> IsTrackSavedAsync(
        string trackId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves or removes a track from the library.
    /// </summary>
    Task SetTrackSavedAsync(
        string trackId,
        bool saved,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if multiple tracks are saved (batch operation).
    /// </summary>
    Task<IReadOnlyDictionary<string, bool>> AreTracksSavedAsync(
        IEnumerable<string> trackIds,
        CancellationToken cancellationToken = default);
}
