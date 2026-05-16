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
        var added = 0;
        try
        {
            // Walk in reverse for "Play next" so the visible order matches the
            // payload order (each insertion at the head pushes the previous one down).
            if (playNext)
            {
                for (var i = tracks.TrackUris.Count - 1; i >= 0; i--)
                {
                    await playback.PlayNextAsync(tracks.TrackUris[i], ct).ConfigureAwait(false);
                    added++;
                }
            }
            else
            {
                foreach (var uri in tracks.TrackUris)
                {
                    await playback.AddToQueueAsync(uri, ct).ConfigureAwait(false);
                    added++;
                }
            }
            var verb = playNext ? "Playing next" : "Added to queue";
            return DropResult.Ok(added, $"{verb}: {added} track{(added == 1 ? string.Empty : "s")}");
        }
        catch (System.Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }
}
