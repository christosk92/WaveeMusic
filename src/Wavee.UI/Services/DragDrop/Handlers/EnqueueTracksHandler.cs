using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Contracts;
using Wavee.UI.Services.DragDrop.Payloads;

namespace Wavee.UI.Services.DragDrop.Handlers;

/// <summary>
/// Handler for <c>(Tracks, Queue|NowPlaying)</c>: enqueues each track URI in the
/// payload. With <see cref="DropModifiers.Shift"/> the tracks go to the head of
/// the user queue ("Play next"); without modifiers they go to the tail
/// ("Add to queue").
/// </summary>
public static class EnqueueTracksHandler
{
    public static async Task<DropResult> HandleAsync(
        IPlaybackService playback,
        DropContext ctx,
        CancellationToken ct)
    {
        if (ctx.Payload is not TrackDragPayload tracks) return DropResult.NoHandler;

        var playNext = (ctx.Modifiers & DropModifiers.Shift) != 0;
        var uris = tracks.TrackUris;
        if (uris.Count == 0) return DropResult.Ok(0, "No tracks");
        try
        {
            // One batch call — the orchestrator enqueues every track in a single mutation and
            // publishes ONE PutState. Looping the single overload published one PutState per
            // track, flooding the cluster publisher and freezing the UI on a big selection
            // (issue #4). The batch insert preserves payload order for "Play next", so no reverse.
            if (playNext)
                await playback.PlayNextAsync(uris, ct).ConfigureAwait(false);
            else
                await playback.AddToQueueAsync(uris, ct).ConfigureAwait(false);

            var added = uris.Count;
            var verb = playNext ? "Playing next" : "Added to queue";
            return DropResult.Ok(added, $"{verb}: {added} track{(added == 1 ? string.Empty : "s")}");
        }
        catch (System.Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }
}