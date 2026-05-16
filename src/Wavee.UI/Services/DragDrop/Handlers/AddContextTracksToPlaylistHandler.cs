using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Services.DragDrop.Payloads;

namespace Wavee.UI.Services.DragDrop.Handlers;

/// <summary>
/// Handler for <c>(Album | Artist | Playlist | LikedSongs | Show, PlaylistRow)</c>:
/// resolves the source's track URIs via the mediator, then appends them to
/// the target playlist through <see cref="IPlaylistDragDropMediator.AddTracksAsync"/>
/// (which now enqueues onto the shared outbox — caller returns immediately).
/// </summary>
public static class AddContextTracksToPlaylistHandler
{
    public static bool CanDrop(DropContext ctx) =>
        !string.IsNullOrEmpty(ctx.TargetId)
        && ctx.TargetId.StartsWith("spotify:playlist:", StringComparison.Ordinal)
        && ctx.Payload is AlbumDragPayload or ArtistDragPayload or PlaylistDragPayload or LikedSongsDragPayload or ShowDragPayload;

    public static async Task<DropResult> HandleAsync(
        IPlaylistDragDropMediator mediator,
        DropContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.TargetId))
            return DropResult.NoHandler;

        IReadOnlyList<string> uris;
        try
        {
            uris = ctx.Payload switch
            {
                AlbumDragPayload    a => await mediator.GetAlbumTrackUrisAsync(a.AlbumUri, ct).ConfigureAwait(false),
                ArtistDragPayload   r => await mediator.GetArtistTopTrackUrisAsync(r.ArtistUri, ct).ConfigureAwait(false),
                PlaylistDragPayload p => await mediator.GetPlaylistTrackUrisAsync(p.PlaylistUri, ct).ConfigureAwait(false),
                LikedSongsDragPayload => await mediator.GetLikedSongUrisAsync(ct).ConfigureAwait(false),
                ShowDragPayload     s => await mediator.GetShowEpisodeUrisAsync(s.ShowUri, ct).ConfigureAwait(false),
                _                     => Array.Empty<string>(),
            };
        }
        catch (Exception ex)
        {
            return DropResult.Failed($"Couldn't load source tracks: {ex.Message}");
        }

        if (uris.Count == 0)
            return DropResult.Failed("Source has no tracks to add");

        try
        {
            await mediator.AddTracksAsync(ctx.TargetId, uris, ct).ConfigureAwait(false);
            return DropResult.Ok(uris.Count, $"Adding {uris.Count} track{(uris.Count == 1 ? string.Empty : "s")}…");
        }
        catch (Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }
}
