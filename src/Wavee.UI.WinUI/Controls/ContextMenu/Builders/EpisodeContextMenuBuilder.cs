using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

public sealed class EpisodeMenuContext
{
    public required string EpisodeId { get; init; }
    public required string EpisodeName { get; init; }
    public string? ShowId { get; init; }
    public string? ShowName { get; init; }
    public bool IsPinned { get; init; }

    public ICommand? PlayCommand { get; init; }
    public ICommand? AddToQueueCommand { get; init; }
    public ICommand? TogglePinCommand { get; init; }
    public ICommand? ShareCommand { get; init; }
}

public static class EpisodeContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(EpisodeMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var uri = "spotify:episode:" + ctx.EpisodeId;

        // Primary: Play · Add to queue
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
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.Queue,
            AccentIconStyleKey = "App.AccentIcons.Media.PlayAfter",
            Command = ctx.AddToQueueCommand,
            Invoke = ctx.AddToQueueCommand is null ? () => AddToQueueDefault(uri) : null,
            IsPrimary = true
        });

        items.Add(ContextMenuItemModel.Separator);

        // Play next (secondary row, mirrors the order in track / card menus).
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_PlayNext"),
            Glyph = FluentGlyphs.PlayNext,
            Invoke = () => PlayNextDefault(uri)
        });

        if (!string.IsNullOrEmpty(ctx.ShowId))
        {
            var showId = ctx.ShowId!;
            var showUri = showId.StartsWith("spotify:show:", StringComparison.Ordinal)
                ? showId
                : "spotify:show:" + showId;
            var showName = ctx.ShowName ?? string.Empty;
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("EpisodeMenu_GoToPodcast"),
                Glyph = FluentGlyphs.Episode,
                Invoke = () => NavigationHelpers.OpenShowPage(showUri, showName)
            });
        }

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsPinned ? "CardMenu_Pinned" : "CardMenu_Pin"),
            Glyph = ctx.IsPinned ? FluentGlyphs.Unpin : FluentGlyphs.Pin,
            Command = ctx.TogglePinCommand,
            Invoke = ctx.TogglePinCommand is null ? () => TogglePinDefault(uri, ctx.IsPinned) : null
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
                Invoke = () => ShareDefault(ctx.EpisodeId)
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

    private static void PlayNextDefault(string uri)
        => Ioc.Default.GetService<IPlaybackStateService>()?.PlayNext(uri);

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

    private static void ShareDefault(string episodeId)
    {
        if (string.IsNullOrEmpty(episodeId)) return;
        try
        {
            var bareId = episodeId.StartsWith("spotify:episode:", StringComparison.Ordinal)
                ? episodeId["spotify:episode:".Length..]
                : episodeId;
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText($"https://open.spotify.com/episode/{bareId}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Ioc.Default.GetService<INotificationService>()?.Show(
                "Link copied",
                NotificationSeverity.Success,
                TimeSpan.FromSeconds(2));
        }
        catch
        {
            // See ShowContextMenuBuilder.ShareDefault for rationale.
        }
    }
}
