using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Services.DragDrop.Payloads;

namespace Wavee.UI.Services.DragDrop.Handlers;

/// <summary>
/// Handler for <c>(Tracks, PlaylistTrackList)</c>: move a contiguous block of
/// tracks within the same playlist. The drop context supplies the playlist URI
/// (TargetId) and the destination row index (TargetIndex); the payload's
/// <see cref="TrackDragPayload.SourceIndices"/>-equivalent comes from the
/// caller's pre-drag capture which is written into the payload as the
/// adjacent uri sequence — the WinUI side passes <see cref="TrackDragPayload.SourceContextUri"/>
/// equal to the playlist URI when the source is intra-list.
/// </summary>
public static class ReorderPlaylistTracksHandler
{
    public static bool CanDrop(DropContext ctx) =>
        ctx.Payload is TrackDragPayload p
        && !string.IsNullOrEmpty(ctx.TargetId)
        && ctx.TargetId.StartsWith("spotify:playlist:", System.StringComparison.Ordinal)
        // Only intra-playlist drags qualify as reorder; cross-playlist drags hit
        // the (Tracks, PlaylistRow) Add handler instead.
        && string.Equals(p.SourceContextUri, ctx.TargetId, System.StringComparison.Ordinal)
        && ctx.TargetIndex is not null;

    public static async Task<DropResult> HandleAsync(
        IPlaylistDragDropMediator mediator,
        DropContext ctx,
        CancellationToken ct)
    {
        if (ctx.Payload is not TrackDragPayload tracks
            || string.IsNullOrEmpty(ctx.TargetId)
            || ctx.TargetIndex is not int toIndex)
            return DropResult.NoHandler;

        // SourceIndices are encoded in the payload's track-uri ordering. The WinUI
        // adapter populates them as contiguous ascending integers before the drag
        // starts so this handler doesn't need a separate channel for "where did
        // these tracks come from in the list".
        var fromIndex = tracks.SourceStartIndex ?? 0;
        var length = tracks.ItemCount;

        try
        {
            await mediator.ReorderTracksAsync(ctx.TargetId, fromIndex, length, toIndex, ct).ConfigureAwait(false);
            return DropResult.Ok(length);
        }
        catch (System.Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }
}
