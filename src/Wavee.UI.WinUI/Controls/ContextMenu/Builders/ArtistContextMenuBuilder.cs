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

public sealed class ArtistMenuContext
{
    public required string ArtistId { get; init; }
    public required string ArtistName { get; init; }
    public bool IsFollowing { get; init; }
    public bool IsPinned { get; init; }

    public ICommand? PlayCommand { get; init; }
    public ICommand? ToggleFollowCommand { get; init; }
    public ICommand? TogglePinCommand { get; init; }
    public ICommand? AddToQueueCommand { get; init; }
    public ICommand? ShareCommand { get; init; }
}

public static class ArtistContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(ArtistMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var uri = "spotify:artist:" + ctx.ArtistId;

        // Primary: Play · Follow/Unfollow
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
            Text = AppLocalization.GetString(ctx.IsFollowing
                ? "ArtistMenu_Unfollow"
                : "ArtistMenu_Follow"),
            Glyph = ctx.IsFollowing ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            AccentIconStyleKey = ctx.IsFollowing ? "App.AccentIcons.Media.Saved" : "App.AccentIcons.Media.Save",
            Command = ctx.ToggleFollowCommand,
            Invoke = ctx.ToggleFollowCommand is null ? () => ToggleFollowDefault(uri, ctx.IsFollowing) : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsPinned ? "SidebarMenu_UnpinFolder" : "SidebarMenu_PinFolder"),
            Glyph = ctx.IsPinned ? FluentGlyphs.Unpin : FluentGlyphs.Pin,
            Command = ctx.TogglePinCommand,
            Invoke = ctx.TogglePinCommand is null ? () => TogglePinDefault(uri, ctx.IsPinned) : null,
            IsPrimary = true
        });

        items.Add(ContextMenuItemModel.Separator);

        // Secondary
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.Queue,
            Command = ctx.AddToQueueCommand,
            Invoke = ctx.AddToQueueCommand is null ? () => AddContextToQueueDefault(uri) : null
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("ArtistMenu_ArtistRadio"),
            Glyph = FluentGlyphs.Radio,
            Invoke = () => _ = Ioc.Default.GetService<IPlaybackStateService>()
                                ?.StartRadioAsync(uri, ctx.ArtistName is { Length: > 0 } name ? $"{name} Radio" : "Artist Radio")
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

        return items;
    }

    private static void PlayContextDefault(string uri)
    {
        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null) return;
        _ = playback.PlayContextAsync(uri);
    }

    private static void ToggleFollowDefault(string uri, bool isFollowing)
    {
        var svc = Ioc.Default.GetService<ITrackLikeService>();
        if (svc is null) return;
        svc.ToggleSave(SavedItemType.Artist, uri, isFollowing);
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

    private static void AddContextToQueueDefault(string uri)
        => Ioc.Default.GetService<IPlaybackStateService>()?.AddToQueue(uri);
}
