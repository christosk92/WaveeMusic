using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Adapter from the framework-neutral <see cref="IPlaylistDragDropMediator"/>
/// to the WinUI-side data services. Lives in WinUI because the data services
/// it depends on do. Registered as singleton in <c>AppLifecycleHelper</c>.
///
/// Adds-to-playlist methods delegate to <see cref="ILibraryDataService.AddTracksToPlaylistAsync"/>
/// which now enqueues onto the outbox — callers return immediately.
/// </summary>
internal sealed class LibraryPlaylistMediator(
    ILibraryDataService library,
    IAlbumService albumService,
    IArtistService artistService,
    IPodcastService podcastService)
    : IPlaylistDragDropMediator
{
    public Task AddTracksAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default)
        => library.AddTracksToPlaylistAsync(playlistUri, trackUris, ct);

    public Task ReorderTracksAsync(string playlistUri, int fromIndex, int length, int toIndex, CancellationToken ct = default)
        => library.ReorderTracksInPlaylistAsync(playlistUri, fromIndex, length, toIndex, ct);

    public Task MovePlaylistInRootlistAsync(string sourceUri, string targetUri, DropPosition position, CancellationToken ct = default)
        => library.MovePlaylistInRootlistAsync(sourceUri, targetUri, position, ct);

    public Task MovePlaylistIntoFolderAsync(string playlistUri, string folderStartUri, CancellationToken ct = default)
        => library.MovePlaylistIntoFolderAsync(playlistUri, folderStartUri, ct);

    public Task MovePlaylistOutOfFolderAsync(string playlistUri, int destinationRootIndex, CancellationToken ct = default)
        => library.MovePlaylistOutOfFolderAsync(playlistUri, destinationRootIndex, ct);

    // ── Context-track resolvers ────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetPlaylistTrackUrisAsync(string playlistUri, CancellationToken ct = default)
    {
        var tracks = await library.GetPlaylistTracksAsync(playlistUri, ct).ConfigureAwait(false);
        return tracks
            .Select(t => string.IsNullOrEmpty(t.Uri) ? null : t.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetAlbumTrackUrisAsync(string albumUri, CancellationToken ct = default)
    {
        var tracks = await albumService.GetTracksAsync(albumUri, ct).ConfigureAwait(false);
        return tracks
            .Select(t => t.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetArtistTopTrackUrisAsync(string artistUri, CancellationToken ct = default)
    {
        var tracks = await artistService.GetExtendedTopTracksAsync(artistUri, ct).ConfigureAwait(false);
        return tracks
            .Select(t => t.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetLikedSongUrisAsync(CancellationToken ct = default)
    {
        var likes = await library.GetLikedSongsAsync(ct).ConfigureAwait(false);
        return likes
            .Select(l => l.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetShowEpisodeUrisAsync(string showUri, CancellationToken ct = default)
    {
        var detail = await podcastService.GetShowDetailAsync(showUri, ct).ConfigureAwait(false);
        return detail?.EpisodeUris ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}
