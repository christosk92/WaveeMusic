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
            Text = AppLocalization.GetString(ctx.IsPinned ? "SidebarMenu_UnpinFolder" : "SidebarMenu_PinFolder"),
            Glyph = ctx.IsPinned ? FluentGlyphs.Unpin : FluentGlyphs.Pin,
            Command = ctx.TogglePinCommand,
            Invoke = ctx.TogglePinCommand is null ? () => TogglePinDefault(uri, ctx.IsPinned) : null,
            IsPrimary = true
        });

        items.Add(ContextMenuItemModel.Separator);

        // Secondary list
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("SidebarMenu_AddToQueue"),
            Glyph = FluentGlyphs.Queue,
            Command = ctx.AddToQueueCommand,
            Invoke = ctx.AddToQueueCommand is null ? () => AddContextToQueueDefault(uri) : null
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(ctx.IsSaved ? "SidebarMenu_RemoveFromLibrary" : "SidebarMenu_SaveToLibrary"),
            Glyph = ctx.IsSaved ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            Command = ctx.ToggleSaveCommand,
            Invoke = ctx.ToggleSaveCommand is null ? () => ToggleFollowDefault(uri, ctx.IsSaved) : null
        });

        if (ctx.IsOwner)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("PlaylistMenu_EditDetails"),
                Glyph = FluentGlyphs.Edit,
                Command = ctx.EditDetailsCommand,
                Invoke = ctx.EditDetailsCommand is null ? () => OpenPlaylistPageDefault(ctx.PlaylistId, ctx.PlaylistName) : null
            });
        }

        // Download is a Spotify Premium feature Wavee doesn't yet implement
        // (no offline cache). Only show the entry when a host page wires its
        // own command — otherwise omit (a fallback that does nothing would
        // mislead the user).
        if (ctx.DownloadCommand is not null)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("PlaylistMenu_Download"),
                Glyph = FluentGlyphs.Download,
                Command = ctx.DownloadCommand
            });
        }

        if (ctx.ShareCommand is not null)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_Share"),
                Glyph = FluentGlyphs.Share,
                Command = ctx.ShareCommand
            });
        }

        // Destructive — Delete only makes sense for owned playlists.
        if (ctx.IsOwner)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("SidebarMenu_Delete"),
                Glyph = FluentGlyphs.Delete,
                IsDestructive = true,
                Command = ctx.DeleteCommand,
                Invoke = ctx.DeleteCommand is null ? () => DeletePlaylistDefault(uri) : null
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

    private static void AddContextToQueueDefault(string uri)
        => Ioc.Default.GetService<IPlaybackStateService>()?.AddToQueue(uri);

    private static async void ToggleFollowDefault(string uri, bool wasSaved)
    {
        var mutations = Ioc.Default.GetService<IPlaylistMutationService>();
        if (mutations is null) return;
        try
        {
            await mutations.SetPlaylistFollowedAsync(uri, !wasSaved).ConfigureAwait(true);
        }
        catch
        {
            Ioc.Default.GetService<INotificationService>()?.Show(
                wasSaved ? "Couldn't remove from library" : "Couldn't save to library",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
        }
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

    private static void OpenPlaylistPageDefault(string playlistId, string playlistName)
        => NavigationHelpers.OpenPlaylist(playlistId, playlistName);

    private static async void DeletePlaylistDefault(string uri)
    {
        var mutations = Ioc.Default.GetService<IPlaylistMutationService>();
        if (mutations is null) return;
        try
        {
            await mutations.DeletePlaylistAsync(uri).ConfigureAwait(true);
        }
        catch
        {
            Ioc.Default.GetService<INotificationService>()?.Show(
                "Couldn't delete playlist",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
        }
    }
}
