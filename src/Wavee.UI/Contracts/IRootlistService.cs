using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Models;

namespace Wavee.UI.Contracts;

/// <summary>
/// Folder + playlist-tree mutations on the user's rootlist. Carved out of
/// <c>ILibraryDataService</c> in Phase 2. Pure top-level reorders; the
/// per-playlist content mutations live in <c>IPlaylistMutationService</c>.
/// </summary>
public interface IRootlistService
{
    /// <summary>
    /// Creates an empty sidebar folder at the top of the user's rootlist.
    /// </summary>
    Task<PlaylistSummaryDto> CreateFolderAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Moves <paramref name="sourceUri"/> to the position implied by
    /// <paramref name="targetUri"/> + <paramref name="position"/>. Accepts
    /// both playlist and folder URIs on either side. For a drag-from-outside
    /// where the source isn't yet in the rootlist, follows + places in one shot.
    /// </summary>
    Task MovePlaylistInRootlistAsync(
        string sourceUri,
        string targetUri,
        DropPosition position,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts the playlist at the top of the supplied folder (just after the
    /// <c>spotify:start-group:…</c> marker).
    /// </summary>
    Task MovePlaylistIntoFolderAsync(
        string playlistUri,
        string folderStartUri,
        CancellationToken ct = default);

    /// <summary>
    /// Lifts a playlist out of whatever folder it sits in and inserts it at
    /// the supplied absolute root index.
    /// </summary>
    Task MovePlaylistOutOfFolderAsync(
        string playlistUri,
        int destinationRootIndex,
        CancellationToken ct = default);

    /// <summary>
    /// Renames a sidebar folder. Targets the <c>spotify:start-group:…</c>
    /// marker that opens the folder in the rootlist.
    /// </summary>
    Task RenameFolderAsync(
        string folderStartUri,
        string newName,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a sidebar folder. Its children are lifted to top-level at
    /// the folder's current position (matches Spotify's web/desktop behavior —
    /// users don't expect contained playlists to disappear).
    /// </summary>
    Task DeleteFolderAsync(
        string folderStartUri,
        CancellationToken ct = default);
}