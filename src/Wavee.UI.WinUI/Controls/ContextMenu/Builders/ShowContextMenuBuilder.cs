using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

public sealed class ShowMenuContext
{
    public required string ShowId { get; init; }
    public required string ShowName { get; init; }
    public bool IsSaved { get; init; }
    public bool IsPinned { get; init; }

    public ICommand? PlayCommand { get; init; }
    public ICommand? AddToQueueCommand { get; init; }
    public ICommand? ToggleSaveCommand { get; init; }
    public ICommand? TogglePinCommand { get; init; }
    public ICommand? ShareCommand { get; init; }
}

public static class ShowContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(ShowMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var uri = "spotify:show:" + ctx.ShowId;

        // Primary: Play · Follow / Following · Pin
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Play"),
            Glyph = FluentGlyphs.Play,
            AccentIconStyleKey = "App.AccentIcons.Media.Play",
            Command = ctx.PlayCommand,
            Invoke = ctx.PlayCommand is null ? () => PlayContextDefault(uri) : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = ctx.IsSaved
                ? AppLocalization.GetString("ShowMenu_Following")
                : AppLocalization.GetString("ShowMenu_Follow"),
            Glyph = ctx.IsSaved ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            AccentIconStyleKey = ctx.IsSaved ? "App.AccentIcons.Media.Saved" : "App.AccentIcons.Media.Save",
            Command = ctx.ToggleSaveCommand,
            Invoke = ctx.ToggleSaveCommand is null ? () => ToggleSaveDefault(uri, ctx.IsSaved) : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsPinned ? "CardMenu_Pinned" : "CardMenu_Pin"),
            Glyph = ctx.IsPinned ? FluentGlyphs.Unpin : FluentGlyphs.Pin,
            Command = ctx.TogglePinCommand,
            Invoke = ctx.TogglePinCommand is null ? () => TogglePinDefault(uri, ctx.IsPinned) : null,
            IsPrimary = true
        });

        items.Add(ContextMenuItemModel.Separator);

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.Queue,
            Command = ctx.AddToQueueCommand,
            Invoke = ctx.AddToQueueCommand is null ? () => AddToQueueDefault(uri) : null
        });

        if (ctx.ShareCommand is not null)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_Share"),
                Glyph = FluentGlyphs.Share,
                Command = ctx.ShareCommand
            });
        }
        else
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_Share"),
                Glyph = FluentGlyphs.Share,
                Invoke = () => ShareDefault(ctx.ShowId)
            });
        }

        return items;
    }

    private static void PlayContextDefault(string uri)
    {
        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null) return;
        _ = playback.PlayContextAsync(uri);
    }

    private static void AddToQueueDefault(string uri)
        => Ioc.Default.GetService<IPlaybackStateService>()?.AddToQueue(uri);

    private static void ToggleSaveDefault(string uri, bool wasSaved)
    {
        var svc = Ioc.Default.GetService<ITrackLikeService>();
        if (svc is null) return;
        svc.ToggleSave(SavedItemType.Show, uri, wasSaved);
    }

    private static async void TogglePinDefault(string uri, bool wasPinned)
    {
        var pinService = Ioc.Default.GetService<IPinService>();
        if (pinService is null) return;
        try
        {
            if (wasPinned) await pinService.UnpinAsync(uri).ConfigureAwait(true);
            else await pinService.PinAsync(uri).ConfigureAwait(true);
        }
        catch
        {
            Ioc.Default.GetService<INotificationService>()?.Show(
                wasPinned ? "Couldn't unpin" : "Couldn't pin",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
        }
    }

    private static void ShareDefault(string showId)
    {
        if (string.IsNullOrEmpty(showId)) return;
        try
        {
            var bareId = showId.StartsWith("spotify:show:", StringComparison.Ordinal)
                ? showId["spotify:show:".Length..]
                : showId;
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText($"https://open.spotify.com/show/{bareId}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Ioc.Default.GetService<INotificationService>()?.Show(
                "Link copied",
                NotificationSeverity.Success,
                TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Clipboard access can fail when the window has lost focus or
            // another process holds the clipboard. Quietly drop the share —
            // the toast service would mask the actual problem.
        }
    }
}
