using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Narrow contract a drag-drop handler needs to mutate a playlist or rootlist.
/// Implemented by an adapter in <c>Wavee.UI.WinUI</c> that delegates to
/// <c>ILibraryDataService</c>. Lives here so handlers stay framework-neutral
/// and mockable.
///
/// Methods land on the server (Spotify) — callers should expect them to throw
/// on network / permission failure. The drag-drop registry surfaces failures
/// as <see cref="DropResult"/> with <c>Success=false</c>.
/// </summary>
public interface IPlaylistDragDropMediator
{
    /// <summary>Append tracks to a playlist's tail.</summary>
    Task AddTracksAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default);

    /// <summary>Move a contiguous block of tracks within a playlist (Op-Mov on /playlist/v2 changes).</summary>
    Task ReorderTracksAsync(string playlistUri, int fromIndex, int length, int toIndex, CancellationToken ct = default);

    /// <summary>
    /// Move a sidebar entry (playlist or folder start-group/end-group pair) to
    /// land before or after another rootlist entry.
    /// </summary>
    Task MovePlaylistInRootlistAsync(string sourceUri, string targetUri, DropPosition position, CancellationToken ct = default);

    /// <summary>Move a playlist into a folder identified by its start-group URI.</summary>
    Task MovePlaylistIntoFolderAsync(string playlistUri, string folderStartUri, CancellationToken ct = default);

    /// <summary>Move a playlist currently inside a folder out to a top-level rootlist index.</summary>
    Task MovePlaylistOutOfFolderAsync(string playlistUri, int destinationRootIndex, CancellationToken ct = default);

    // ── Context-track resolvers ───────────────────────────────────────────
    // For drag-drop of an album / artist / playlist / liked-songs / show onto
    // a playlist row: resolve the source into its track URIs so the drop
    // routes through AddTracksAsync (which already enqueues via the outbox).

    /// <summary>Returns every track URI on the supplied playlist, in order.</summary>
    Task<IReadOnlyList<string>> GetPlaylistTrackUrisAsync(string playlistUri, CancellationToken ct = default);

    /// <summary>Returns every track URI on the supplied album, in order.</summary>
    Task<IReadOnlyList<string>> GetAlbumTrackUrisAsync(string albumUri, CancellationToken ct = default);

    /// <summary>Returns the artist's top tracks (Spotify's "top tracks" list).</summary>
    Task<IReadOnlyList<string>> GetArtistTopTrackUrisAsync(string artistUri, CancellationToken ct = default);

    /// <summary>Returns every track URI in the user's Liked Songs collection.</summary>
    Task<IReadOnlyList<string>> GetLikedSongUrisAsync(CancellationToken ct = default);

    /// <summary>Returns every episode URI on the supplied podcast show.</summary>
    Task<IReadOnlyList<string>> GetShowEpisodeUrisAsync(string showUri, CancellationToken ct = default);
}
