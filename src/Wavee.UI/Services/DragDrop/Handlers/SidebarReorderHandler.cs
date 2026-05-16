using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Services.DragDrop.Payloads;

namespace Wavee.UI.Services.DragDrop.Handlers;

/// <summary>
/// Handlers for sidebar drag-drop:
/// <list type="bullet">
///   <item><c>(SidebarItem, PlaylistRow)</c> — reorder before / after the target.</item>
///   <item><c>(SidebarItem, FolderRow)</c> — drop inside the folder, or reorder if before/after.</item>
///   <item><c>(SidebarItem, SidebarRoot)</c> — move a row out of its current folder to the top level.</item>
///   <item><c>(Playlist, FolderRow)</c> — nest a playlist card into a folder.</item>
/// </list>
/// </summary>
public static class SidebarReorderHandler
{
    public static async Task<DropResult> ReorderAsync(
        IPlaylistDragDropMediator mediator,
        DropContext ctx,
        CancellationToken ct)
    {
        if (ctx.Payload is not SidebarReorderPayload p || string.IsNullOrEmpty(ctx.TargetId))
            return DropResult.NoHandler;
        if (string.Equals(p.SourceUri, ctx.TargetId, StringComparison.OrdinalIgnoreCase))
            return DropResult.NoHandler;

        // Center drop on a PLAYLIST row = copy source playlist's tracks into
        // the target. Top/Bottom edge drops keep the existing rootlist reorder
        // semantics. Folder→playlist copy is nonsensical so folder sources
        // always reorder regardless of position.
        if (ctx.Position == DropPosition.Inside
            && p.ItemKind == SidebarItemKind.Playlist
            && ctx.TargetId.StartsWith("spotify:playlist:", StringComparison.Ordinal))
        {
            try
            {
                var uris = await mediator.GetPlaylistTrackUrisAsync(p.SourceUri, ct).ConfigureAwait(false);
                if (uris.Count == 0) return DropResult.Failed("Source playlist is empty");
                await mediator.AddTracksAsync(ctx.TargetId, uris, ct).ConfigureAwait(false);
                return DropResult.Ok(uris.Count, $"Adding {uris.Count} track{(uris.Count == 1 ? string.Empty : "s")}…");
            }
            catch (Exception ex)
            {
                return DropResult.Failed(ex.Message);
            }
        }

        // Top / Bottom (or any non-Inside) → reorder the rootlist position.
        try
        {
            await mediator.MovePlaylistInRootlistAsync(p.SourceUri, ctx.TargetId, ctx.Position, ct).ConfigureAwait(false);
            return DropResult.Ok(1);
        }
        catch (Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }

    public static async Task<DropResult> NestOrReorderAsync(
        IPlaylistDragDropMediator mediator,
        DropContext ctx,
        CancellationToken ct)
    {
        if (ctx.Payload is not SidebarReorderPayload p || string.IsNullOrEmpty(ctx.TargetId))
            return DropResult.NoHandler;
        if (string.Equals(p.SourceUri, ctx.TargetId, StringComparison.OrdinalIgnoreCase))
            return DropResult.NoHandler;

        try
        {
            // Inside-of-folder drop = nest. Before/After = reorder relative to the folder.
            if (ctx.Position == DropPosition.Inside && p.ItemKind == SidebarItemKind.Playlist)
                await mediator.MovePlaylistIntoFolderAsync(p.SourceUri, ctx.TargetId, ct).ConfigureAwait(false);
            else
                await mediator.MovePlaylistInRootlistAsync(p.SourceUri, ctx.TargetId, ctx.Position, ct).ConfigureAwait(false);
            return DropResult.Ok(1);
        }
        catch (Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Handler for <c>(Playlist, FolderRow)</c>: dragging a playlist card into
    /// a sidebar folder. Always nests, regardless of drop position.
    /// </summary>
    public static async Task<DropResult> NestPlaylistAsync(
        IPlaylistDragDropMediator mediator,
        DropContext ctx,
        CancellationToken ct)
    {
        if (ctx.Payload is not PlaylistDragPayload p || string.IsNullOrEmpty(ctx.TargetId))
            return DropResult.NoHandler;

        try
        {
            if (ctx.Position == DropPosition.Inside)
                await mediator.MovePlaylistIntoFolderAsync(p.PlaylistUri, ctx.TargetId, ct).ConfigureAwait(false);
            else
                await mediator.MovePlaylistInRootlistAsync(p.PlaylistUri, ctx.TargetId, ctx.Position, ct).ConfigureAwait(false);
            return DropResult.Ok(1);
        }
        catch (Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }

    public static bool CanDropPlaylistOnPlaylistRow(DropContext ctx) =>
        ctx.Payload is PlaylistDragPayload p
        && !string.IsNullOrEmpty(ctx.TargetId)
        && ctx.TargetId.StartsWith("spotify:playlist:", StringComparison.Ordinal)
        && !string.Equals(p.PlaylistUri, ctx.TargetId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Playlist card/sidebar-row payload on a playlist row. Center copies the
    /// source playlist's tracks into the target; edge drops move the playlist
    /// before/after the target in the sidebar rootlist.
    /// </summary>
    public static async Task<DropResult> PlaylistOnPlaylistRowAsync(
        IPlaylistDragDropMediator mediator,
        DropContext ctx,
        CancellationToken ct)
    {
        if (ctx.Payload is not PlaylistDragPayload p || string.IsNullOrEmpty(ctx.TargetId))
            return DropResult.NoHandler;
        if (string.Equals(p.PlaylistUri, ctx.TargetId, StringComparison.OrdinalIgnoreCase))
            return DropResult.NoHandler;

        if (ctx.Position == DropPosition.Inside)
            return await AddContextTracksToPlaylistHandler.HandleAsync(mediator, ctx, ct).ConfigureAwait(false);

        try
        {
            await mediator.MovePlaylistInRootlistAsync(p.PlaylistUri, ctx.TargetId, ctx.Position, ct).ConfigureAwait(false);
            return DropResult.Ok(1);
        }
        catch (Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Move a sidebar row to the top level. <see cref="DropContext.TargetIndex"/>
    /// supplies the destination 0-based top-level index.
    /// </summary>
    public static async Task<DropResult> MoveToRootAsync(
        IPlaylistDragDropMediator mediator,
        DropContext ctx,
        CancellationToken ct)
    {
        if (ctx.Payload is not SidebarReorderPayload p) return DropResult.NoHandler;
        var destIdx = ctx.TargetIndex ?? 0;

        try
        {
            await mediator.MovePlaylistOutOfFolderAsync(p.SourceUri, destIdx, ct).ConfigureAwait(false);
            return DropResult.Ok(1);
        }
        catch (Exception ex)
        {
            return DropResult.Failed(ex.Message);
        }
    }
}
