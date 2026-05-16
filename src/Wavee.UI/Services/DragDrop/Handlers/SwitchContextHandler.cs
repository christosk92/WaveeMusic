using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Contracts;
using Wavee.UI.Services.DragDrop.Payloads;

namespace Wavee.UI.Services.DragDrop.Handlers;

/// <summary>
/// Handler for <c>(Album|Playlist|Artist, NowPlaying)</c>: switch the active
/// playback context to the dragged item. Also exposes <see cref="EnqueueAsync"/>
/// for <c>(Album|Playlist|Artist, Queue)</c> — currently a no-op until
/// context-track-expansion is wired through the mediator (Phase 4 follow-up).
/// </summary>
public static class SwitchContextHandler
{
    public static async Task<DropResult> HandleAsync(
        IPlaybackService playback,
        DropContext ctx,
        CancellationToken ct)
    {
        var contextUri = ResolveContextUri(ctx.Payload);
        if (contextUri is null) return DropResult.NoHandler;

        try
        {
            await playback.PlayContextAsync(contextUri, ct: ct).ConfigureAwait(false);
            return DropResult.Ok(1, "Playing");
        }
        catch (System.Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// "Enqueue this whole context" — for now we just switch context (most users
    /// expect the dragged item to start playing). Real track-by-track enqueue
    /// requires expanding the context to a track list, which is a library
    /// concern that hasn't been added to <see cref="IPlaylistDragDropMediator"/>
    /// yet. Tracked as Phase 4 follow-up in the plan.
    /// </summary>
    public static Task<DropResult> EnqueueAsync(
        IPlaybackService playback,
        DropContext ctx,
        CancellationToken ct) => HandleAsync(playback, ctx, ct);

    private static string? ResolveContextUri(IDragPayload payload) => payload switch
    {
        AlbumDragPayload a    => a.AlbumUri,
        PlaylistDragPayload p => p.PlaylistUri,
        ArtistDragPayload r   => r.ArtistUri,
        ShowDragPayload s     => s.ShowUri,
        LikedSongsDragPayload => LikedSongsDragPayload.LikedSongsUri,
        _ => null,
    };
}
