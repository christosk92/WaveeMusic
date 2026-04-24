using System;
using System.Collections.Generic;
using System.Diagnostics;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

/// <summary>
/// Context passed to the sidebar folder menu — mirrors Spotify's folder menu: Rename, Delete,
/// Pin folder, Create playlist, Create folder, Move to folder ▶.
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

        // Quick actions: Pin/Unpin, Rename
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsPinned ? "SidebarMenu_UnpinFolder" : "SidebarMenu_PinFolder"),
            Glyph = ctx.IsPinned ? FluentGlyphs.Unpin : FluentGlyphs.Pin,
            IsPrimary = true,
            Invoke = ctx.TogglePinAction ?? (() => Debug.WriteLine($"TogglePinFolder: {ctx.FolderId}"))
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_Rename"),
            Glyph = FluentGlyphs.Rename,
            IsPrimary = true,
            Invoke = ctx.RenameAction ?? (() => Debug.WriteLine($"RenameFolder: {ctx.FolderId}"))
        });

        // Creation shortcuts
        items.Add(ContextMenuItemModel.Separator);
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_CreatePlaylist"),
            Glyph = FluentGlyphs.CreatePlaylist,
            Invoke = ctx.CreatePlaylistAction ?? (() => Debug.WriteLine($"CreatePlaylistInFolder: {ctx.FolderId}"))
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_CreateFolder"),
            Glyph = FluentGlyphs.CreateFolder,
            Invoke = ctx.CreateFolderAction ?? (() => Debug.WriteLine($"CreateFolderInFolder: {ctx.FolderId}"))
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

        // Delete (destructive, last)
        items.Add(ContextMenuItemModel.Separator);
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_Delete"),
            Glyph = FluentGlyphs.Delete,
            IsDestructive = true,
            Invoke = ctx.DeleteAction ?? (() => Debug.WriteLine($"DeleteFolder: {ctx.FolderId}"))
        });

        return items;
    }
}
