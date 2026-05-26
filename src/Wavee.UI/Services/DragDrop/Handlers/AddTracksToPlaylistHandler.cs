using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Services.DragDrop.Payloads;

namespace Wavee.UI.Services.DragDrop.Handlers;

/// <summary>
/// Handler for <c>(Tracks, PlaylistRow)</c>: append every track URI in the
/// payload to the playlist identified by <see cref="DropContext.TargetId"/>.
/// </summary>
public static class AddTracksToPlaylistHandler
{
    public static bool CanDrop(DropContext ctx) =>
        ctx.Payload is TrackDragPayload
        && !string.IsNullOrEmpty(ctx.TargetId)
        && ctx.TargetId.StartsWith("spotify:playlist:", System.StringComparison.Ordinal);

    public static async Task<DropResult> HandleAsync(
        IPlaylistDragDropMediator mediator,
        DropContext ctx,
        CancellationToken ct)
    {
        if (ctx.Payload is not TrackDragPayload tracks || string.IsNullOrEmpty(ctx.TargetId))
            return DropResult.NoHandler;

        try
        {
            await mediator.AddTracksAsync(ctx.TargetId, tracks.TrackUris, ct).ConfigureAwait(false);
            var playlistName = await mediator.GetDisplayNameAsync(ctx.TargetId, ct).ConfigureAwait(false);
            var n = tracks.ItemCount;
            var nounTrack = n == 1 ? "track" : "tracks";
            var message = string.IsNullOrEmpty(playlistName)
                ? $"Added {n} {nounTrack} to playlist"
                : $"Added {n} {nounTrack} to {playlistName}";
            return DropResult.Ok(n, message);
        }
        catch (System.Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }
}
