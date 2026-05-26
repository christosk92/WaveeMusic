using System;
using System.Collections.Generic;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

/// <summary>
/// Context passed to the sidebar folder menu — mirrors Spotify's folder menu: Rename, Delete,
/// Pin folder, Create playlist, Create folder, Move to folder ▶.
///
/// Entries that need folder-specific state (Rename, Delete, TogglePin) are only
/// added when the caller wires an explicit action — a menu that does nothing
/// is worse than a menu that omits the entry.
/// </summary>
public sealed class SidebarFolderMenuContext
{
    public required string FolderId { get; init; }
    public required string FolderName { get; init; }
    public bool IsPinned { get; init; }

    public Action? RenameAction { get; init; }
    public Action? DeleteAction { get; init; }
    public Action? TogglePinAction { get; init; }
    public Action? CreatePlaylistAction { get; init; }
    public Action? CreateFolderAction { get; init; }
    public Func<IReadOnlyList<ContextMenuItemModel>>? BuildMoveTargets { get; init; }
}

public static class SidebarFolderContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(SidebarFolderMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();

        // Quick actions: Pin/Unpin, Rename — wire only when supplied.
        if (ctx.TogglePinAction is { } togglePin)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString(ctx.IsPinned ? "SidebarMenu_UnpinFolder" : "SidebarMenu_PinFolder"),
                Glyph = ctx.IsPinned ? FluentGlyphs.Unpin : FluentGlyphs.Pin,
                IsPrimary = true,
                Invoke = togglePin
            });
        }

        if (ctx.RenameAction is { } rename)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_Rename"),
                Glyph = FluentGlyphs.Rename,
                IsPrimary = true,
                Invoke = rename
            });
        }

        // Creation shortcuts — these have safe navigation defaults that don't
        // depend on folder-id context.
        if (items.Count > 0)
            items.Add(ContextMenuItemModel.Separator);

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_CreatePlaylist"),
            Glyph = FluentGlyphs.CreatePlaylist,
            Invoke = ctx.CreatePlaylistAction ?? (() => NavigationHelpers.OpenCreatePlaylist(isFolder: false))
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_CreateFolder"),
            Glyph = FluentGlyphs.CreateFolder,
            Invoke = ctx.CreateFolderAction ?? (() => NavigationHelpers.OpenCreatePlaylist(isFolder: true))
        });

        // Move to folder ▶
        if (ctx.BuildMoveTargets is not null)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_MoveToFolder"),
                Glyph = FluentGlyphs.MoveTo,
                Items = ctx.BuildMoveTargets()
            });
        }

        // Delete (destructive, last) — only when wired.
        if (ctx.DeleteAction is { } delete)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_Delete"),
                Glyph = FluentGlyphs.Delete,
                IsDestructive = true,
                Invoke = delete
            });
        }

        return items;
    }
}
