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

public sealed class AlbumMenuContext
{
    public required string AlbumId { get; init; }
    public required string AlbumName { get; init; }
    public string? ArtistId { get; init; }
    public string? ArtistName { get; init; }
    public bool IsSaved { get; init; }
    public bool IsPinned { get; init; }

    public ICommand? PlayCommand { get; init; }
    public ICommand? ShuffleCommand { get; init; }
    public ICommand? ToggleSaveCommand { get; init; }
    public ICommand? TogglePinCommand { get; init; }
    public ICommand? AddToQueueCommand { get; init; }
    public ICommand? ShareCommand { get; init; }
}

public static class AlbumContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(AlbumMenuContext ctx)
    {
        var items = new List<ContextMenuItemModel>();
        var uri = "spotify:album:" + ctx.AlbumId;

        // Primary: Play · Shuffle · Save
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Play"),
            Glyph = FluentGlyphs.Play,
            AccentIconStyleKey = "App.AccentIcons.Media.Play",
            Command = ctx.PlayCommand,
            Invoke = ctx.PlayCommand is null ? () => PlayContextDefault(uri, shuffle: false) : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("PlaylistMenu_Shuffle"),
            Glyph = FluentGlyphs.Shuffle,
            AccentIconStyleKey = "App.AccentIcons.Media.Shuffle",
            Command = ctx.ShuffleCommand,
            Invoke = ctx.ShuffleCommand is null ? () => PlayContextDefault(uri, shuffle: true) : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            // Short labels for the 4-column primary row — full
            // "Save to library / Remove from library" overflows. Matches the
            // track menu's TrackMenu_SaveShort / SavedShort precedent.
            Text = AppLocalization.GetString(ctx.IsSaved
                ? "TrackMenu_SavedShort"
                : "TrackMenu_SaveShort"),
            Glyph = ctx.IsSaved ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            AccentIconStyleKey = ctx.IsSaved ? "App.AccentIcons.Media.Saved" : "App.AccentIcons.Media.Save",
            Command = ctx.ToggleSaveCommand,
            Invoke = ctx.ToggleSaveCommand is null ? () => ToggleAlbumSaveDefault(uri, ctx.IsSaved) : null,
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

        // Secondary
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.Queue,
            Command = ctx.AddToQueueCommand,
            Invoke = ctx.AddToQueueCommand is null ? () => AddAlbumToQueueDefault(uri) : null
        });

        if (!string.IsNullOrEmpty(ctx.ArtistId))
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_GoToArtist"),
                Glyph = FluentGlyphs.Artist,
                Invoke = () => NavigationHelpers.OpenArtist(ctx.ArtistId!, ctx.ArtistName ?? string.Empty)
            });
        }

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("AlbumMenu_AlbumRadio"),
            Glyph = FluentGlyphs.Radio,
            Invoke = () => _ = Ioc.Default.GetService<IPlaybackStateService>()
                                ?.StartRadioAsync(uri, ctx.AlbumName is { Length: > 0 } name ? $"{name} Radio" : "Album Radio")
        });

        // Share — host pages that have a clipboard-copy implementation pass
        // a ShareCommand. Surfaces that don't (rare) simply omit the entry.
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

    private static void PlayContextDefault(string uri, bool shuffle)
    {
        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null) return;
        if (shuffle)
            Ioc.Default.GetService<IPlaybackStateService>()?.SetShuffle(true);
        _ = playback.PlayContextAsync(uri);
    }

    private static async void ToggleAlbumSaveDefault(string uri, bool wasSaved)
    {
        var svc = Ioc.Default.GetService<ITrackLikeService>();
        if (svc is null) return;
        svc.ToggleSave(SavedItemType.Album, uri, wasSaved);
        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(true);
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

    private static void AddAlbumToQueueDefault(string uri)
    {
        // The album is added as a context — Spotify Connect resolves the album
        // URI server-side and inserts its tracks at the end of the user queue.
        Ioc.Default.GetService<IPlaybackStateService>()?.AddToQueue(uri);
    }
}
