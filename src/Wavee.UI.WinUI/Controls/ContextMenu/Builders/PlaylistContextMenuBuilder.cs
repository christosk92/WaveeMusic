using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Input;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

public sealed class PlaylistMenuContext
{
    public required string PlaylistId { get; init; }
    public required string PlaylistName { get; init; }
    public bool IsOwner { get; init; }
    public bool IsPinned { get; init; }
    public bool IsSaved { get; init; }

    public ICommand? PlayCommand { get; init; }
    public ICommand? ShuffleCommand { get; init; }
    public ICommand? AddToQueueCommand { get; init; }
    public ICommand? ToggleSaveCommand { get; init; }
    public ICommand? TogglePinCommand { get; init; }
    public ICommand? EditDetailsCommand { get; init; }
    public ICommand? DownloadCommand { get; init; }
    public ICommand? ShareCommand { get; init; }
    public ICommand? DeleteCommand { get; init; }
}

public static class PlaylistContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(PlaylistMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var uri = "spotify:playlist:" + ctx.PlaylistId;

        // Primary: Play · Shuffle · Pin/Unpin
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Play"),
            Glyph = FluentGlyphs.Play,
            AccentIconStyleKey = "App.AccentIcons.Media.Play",
            Command = ctx.PlayCommand,
            Invoke = ctx.PlayCommand is null ? () => Debug.WriteLine($"PlayPlaylist: {uri}") : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("PlaylistMenu_Shuffle"),
            Glyph = FluentGlyphs.Shuffle,
            AccentIconStyleKey = "App.AccentIcons.Media.Shuffle",
            Command = ctx.ShuffleCommand,
            Invoke = ctx.ShuffleCommand is null ? () => Debug.WriteLine($"ShufflePlaylist: {uri}") : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsPinned ? "SidebarMenu_UnpinFolder" : "SidebarMenu_PinFolder"),
            Glyph = ctx.IsPinned ? FluentGlyphs.Unpin : FluentGlyphs.Pin,
            Command = ctx.TogglePinCommand,
            Invoke = ctx.TogglePinCommand is null ? () => Debug.WriteLine($"TogglePinPlaylist: {uri}") : null,
            IsPrimary = true
        });

        items.Add(ContextMenuItemModel.Separator);

        // Secondary list
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.AddToQueue,
            Command = ctx.AddToQueueCommand,
            Invoke = ctx.AddToQueueCommand is null ? () => Debug.WriteLine($"AddPlaylistToQueue: {uri}") : null
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsSaved ? "SidebarMenu_RemoveFromLibrary" : "SidebarMenu_SaveToLibrary"),
            Glyph = ctx.IsSaved ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            Command = ctx.ToggleSaveCommand,
            Invoke = ctx.ToggleSaveCommand is null ? () => Debug.WriteLine($"TogglePlaylistLibrary: {uri}") : null
        });

        if (ctx.IsOwner)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("PlaylistMenu_EditDetails"),
                Glyph = FluentGlyphs.Edit,
                Command = ctx.EditDetailsCommand,
                Invoke = ctx.EditDetailsCommand is null ? () => Debug.WriteLine($"EditPlaylist: {uri}") : null
            });
        }

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("PlaylistMenu_Download"),
            Glyph = FluentGlyphs.Download,
            Command = ctx.DownloadCommand,
            Invoke = ctx.DownloadCommand is null ? () => Debug.WriteLine($"DownloadPlaylist: {uri}") : null
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Share"),
            Glyph = FluentGlyphs.Share,
            Command = ctx.ShareCommand,
            Invoke = ctx.ShareCommand is null ? () => Debug.WriteLine($"SharePlaylist: {uri}") : null
        });

        // Destructive
        if (ctx.IsOwner)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_Delete"),
                Glyph = FluentGlyphs.Delete,
                IsDestructive = true,
                Command = ctx.DeleteCommand,
                Invoke = ctx.DeleteCommand is null ? () => Debug.WriteLine($"DeletePlaylist: {uri}") : null
            });
        }

        return items;
    }
}
