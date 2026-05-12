using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Input;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

/// <summary>
/// Context for a sidebar playlist entry's menu, shaped to match Spotify's layout:
/// Add to queue · Add to profile · Report · Remove/Save · Create playlist · Create folder
/// · Exclude from taste profile · Move to folder ▶ · Share ▶.
/// </summary>
public sealed class SidebarPlaylistMenuContext
{
    public required string PlaylistId { get; init; }
    public required string PlaylistName { get; init; }
    public bool IsInLibrary { get; init; }
    public bool IsOwner { get; init; }

    public ICommand? AddToQueueCommand { get; init; }
    public Action? AddToProfileAction { get; init; }
    public Action? ReportAction { get; init; }
    public Action? ToggleLibraryAction { get; init; }
    public Action? CreatePlaylistAction { get; init; }
    public Action? CreateFolderAction { get; init; }
    public Action? ExcludeFromTasteAction { get; init; }
    public Action? DeleteAction { get; init; }

    public Func<IReadOnlyList<ContextMenuItemModel>>? BuildMoveTargets { get; init; }
    public Func<IReadOnlyList<ContextMenuItemModel>>? BuildShareTargets { get; init; }
}

public static class SidebarPlaylistContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(SidebarPlaylistMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var uri = "spotify:playlist:" + ctx.PlaylistId;

        // ── Quick actions ─────────────────────────────────────────────────
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.AddToQueue,
            AccentIconStyleKey = "App.AccentIcons.Media.PlayAfter",
            Command = ctx.AddToQueueCommand,
            CommandParameter = uri,
            IsPrimary = true,
            Invoke = ctx.AddToQueueCommand is null
                ? () => Debug.WriteLine($"AddPlaylistToQueue: {uri}")
                : null
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsInLibrary
                ? "SidebarMenu_RemoveFromLibrary"
                : "SidebarMenu_SaveToLibrary"),
            Glyph = ctx.IsInLibrary ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            AccentIconStyleKey = ctx.IsInLibrary ? "App.AccentIcons.Media.Saved" : "App.AccentIcons.Media.Save",
            IsPrimary = true,
            // Owners delete instead of unfollowing — hide the heart for them.
            ShowItem = !ctx.IsOwner,
            Invoke = ctx.ToggleLibraryAction ?? (() => Debug.WriteLine($"ToggleLibrary: {uri}"))
        });

        // ── Primary dropdown items ────────────────────────────────────────
        items.Add(ContextMenuItemModel.Separator);

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToProfile"),
            Glyph = FluentGlyphs.AddToProfile,
            Invoke = ctx.AddToProfileAction ?? (() => Debug.WriteLine($"AddToProfile: {uri}"))
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_Report"),
            Glyph = FluentGlyphs.Report,
            // Reporting your own playlist is nonsense — hide for owners.
            ShowItem = !ctx.IsOwner,
            Invoke = ctx.ReportAction ?? (() => Debug.WriteLine($"Report: {uri}"))
        });

        // ── Creation shortcuts ────────────────────────────────────────────
        items.Add(ContextMenuItemModel.Separator);

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_CreatePlaylist"),
            Glyph = FluentGlyphs.CreatePlaylist,
            Invoke = ctx.CreatePlaylistAction ?? (() => Debug.WriteLine("CreatePlaylist (sidebar)"))
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_CreateFolder"),
            Glyph = FluentGlyphs.CreateFolder,
            Invoke = ctx.CreateFolderAction ?? (() => Debug.WriteLine("CreateFolder (sidebar)"))
        });

        // ── Taste profile & move ──────────────────────────────────────────
        items.Add(ContextMenuItemModel.Separator);

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_ExcludeFromTaste"),
            Glyph = FluentGlyphs.Exclude,
            // Taste-profile signal is owner-implicit; hide for owned playlists.
            ShowItem = !ctx.IsOwner,
            Invoke = ctx.ExcludeFromTasteAction ?? (() => Debug.WriteLine($"ExcludeFromTaste: {uri}"))
        });

        if (ctx.BuildMoveTargets is not null)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_MoveToFolder"),
                Glyph = FluentGlyphs.MoveTo,
                Items = ctx.BuildMoveTargets()
            });
        }

        // ── Share ▶ ──────────────────────────────────────────────────────
        if (ctx.BuildShareTargets is not null)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_Share"),
                Glyph = FluentGlyphs.Share,
                Items = ctx.BuildShareTargets()
            });
        }

        // ── Delete (owned playlists only, destructive, last) ─────────────
        if (ctx.IsOwner)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_Delete"),
                Glyph = FluentGlyphs.Delete,
                IsDestructive = true,
                Invoke = ctx.DeleteAction ?? (() => Debug.WriteLine($"DeletePlaylist: {uri}"))
            });
        }

        return items;
    }
}
