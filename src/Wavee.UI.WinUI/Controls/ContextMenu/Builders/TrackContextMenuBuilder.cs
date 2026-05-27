using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

/// <summary>
/// Hosts optional commands / callbacks a caller can feed the track menu builder.
/// Drop-in replacement for the old TrackContextMenuOptions.
/// </summary>
public sealed class TrackMenuContext
{
    public ICommand? PlayCommand { get; init; }
    public ICommand? PlayNextCommand { get; init; }
    public ICommand? AddToQueueCommand { get; init; }
    public ICommand? RemoveCommand { get; init; }
    public string? RemoveLabel { get; init; }
    public ICommand? ToggleLikeCommand { get; init; }
    public ICommand? StartRadioCommand { get; init; }
    public ICommand? ShareCommand { get; init; }
    public Action? ShowCreditsAction { get; init; }
    public Action<DetailsBackgroundMode>? SetBackgroundModeAction { get; init; }
    public bool HasCanvas { get; init; }
    public DetailsBackgroundMode CurrentBackgroundMode { get; init; }

    /// <summary>
    /// When set, the menu shows a "Select" entry that puts the host track list
    /// into multi-select mode with the right-tapped track selected. Supplied by
    /// grid-hosted track rows (<c>TrackDataGrid</c>); unset elsewhere.
    /// </summary>
    public Action? EnterSelectionAction { get; init; }

    /// <summary>
    /// Extra items appended to the built menu (under a separator). Lets callers inject
    /// surface-specific rows without baking those into the shared builder.
    /// </summary>
    public IReadOnlyList<ContextMenuItemModel>? ExtraItems { get; init; }
}

public static class TrackContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuItemModel> Build(ITrackItem track, TrackMenuContext? ctx = null)
    {
        ctx ??= new TrackMenuContext();
        var items = new List<ContextMenuItemModel>();

        // ── Primary row (icon + label, equal widths) — Play · Play Next · Play After · Save
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_Play"),
            Glyph = FluentGlyphs.Play,
            AccentIconStyleKey = "App.AccentIcons.Media.Play",
            Command = ctx.PlayCommand,
            CommandParameter = track,
            // Fallback: load a one-track queue. Mirrors what every host page's
            // PlayCommand ultimately does — preserves "right-click → Play"
            // working everywhere even on surfaces that didn't bind an explicit
            // command (e.g. floating mini cards).
            Invoke = ctx.PlayCommand is null
                ? () => PlayDefault(track)
                : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_PlayNext"),
            Glyph = FluentGlyphs.PlayNext,
            AccentIconStyleKey = "App.AccentIcons.Media.PlayNext",
            Command = ctx.PlayNextCommand,
            CommandParameter = track,
            // Fallback: direct call into IPlaybackStateService.PlayNext when the
            // page didn't bind an explicit command. Inserts at head of user queue
            // (plays right after current track, then context resumes).
            Invoke = ctx.PlayNextCommand is null
                ? () => PlayNextDefault(track)
                : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_AddToQueue"),
            Glyph = FluentGlyphs.Queue,
            AccentIconStyleKey = "App.AccentIcons.Media.PlayAfter",
            Command = ctx.AddToQueueCommand,
            CommandParameter = track,
            // Fallback: direct call into IPlaybackStateService.AddToQueue when
            // the page didn't bind an explicit command. Appends to post-context
            // bucket (plays after the current context exhausts).
            Invoke = ctx.AddToQueueCommand is null
                ? () => AddToQueueDefault(track)
                : null,
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            KeyboardAccelerator = Accelerator(VirtualKey.Enter, VirtualKeyModifiers.Control),
            IsPrimary = true
        });

        // Short label in the primary row so the column doesn't break on "Remove from Liked Songs".
        // The full phrase still lives in the tooltip (set by ContextMenuHost when building primaries).
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString(track.IsLiked ? "TrackMenu_SavedShort" : "TrackMenu_SaveShort"),
            Glyph = track.IsLiked ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            AccentIconStyleKey = track.IsLiked ? "App.AccentIcons.Media.Saved" : "App.AccentIcons.Media.Save",
            Command = ctx.ToggleLikeCommand,
            CommandParameter = track,
            Invoke = ctx.ToggleLikeCommand is null ? () => ToggleLikeDefault(track) : null,
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+L",
            KeyboardAccelerator = Accelerator(VirtualKey.L, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
            IsPrimary = true
        });

        // ── Single separator before the grouped secondary list
        items.Add(ContextMenuItemModel.Separator);

        // ── Secondary list (single group, no internal separators) ────────────

        // Select — enters the host list's multi-select mode (keyboard-free).
        // Only present when a grid-hosted row supplied the action.
        if (ctx.EnterSelectionAction is { } enterSelection)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = "Select",
                Glyph = FluentGlyphs.SelectAll,
                Invoke = enterSelection
            });
        }

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_AddToPlaylist"),
            Glyph = FluentGlyphs.Add,
            // Shared folder-aware loader — the card menu uses the same helper,
            // so any change to folder rendering / owner filter / toast wording
            // lands here once and applies everywhere.
            LoadSubMenuAsync = AddToPlaylistSubmenuBuilder.Loader(
                sourceLabel: track.Title ?? "track",
                trackUrisLoader: _ => Task.FromResult<IReadOnlyList<string>>(
                    string.IsNullOrEmpty(track.Uri) ? Array.Empty<string>() : new[] { track.Uri }))
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_SongRadio"),
            Glyph = FluentGlyphs.Radio,
            Command = ctx.StartRadioCommand,
            CommandParameter = track,
            Invoke = ctx.StartRadioCommand is null
                ? () => _ = Ioc.Default.GetService<IPlaybackStateService>()
                            ?.StartRadioAsync(track.Uri, track.Title is { Length: > 0 } title ? $"{title} Radio" : null)
                : null
        });

        if (!string.IsNullOrEmpty(track.ArtistId))
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_GoToArtist"),
                Glyph = FluentGlyphs.Artist,
                Invoke = () => NavigationHelpers.OpenArtist(track.ArtistId, track.ArtistName)
            });
        }

        if (!string.IsNullOrEmpty(track.AlbumId))
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_GoToAlbum"),
                Glyph = FluentGlyphs.Album,
                Invoke = () => NavigationHelpers.OpenAlbum(track.AlbumId, track.AlbumName)
            });
        }

        // View credits — only when the host page supplies the action. Hosts
        // that have no credits surface (e.g. some local-file rows) simply omit
        // the entry rather than show a menu item that silently does nothing.
        if (ctx.ShowCreditsAction is { } creditsAction)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_ViewCredits"),
                Glyph = FluentGlyphs.Credits,
                Invoke = creditsAction
            });
        }

        // Share (when provided)
        if (ctx.ShareCommand is not null)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_Share"),
                Glyph = FluentGlyphs.Share,
                Command = ctx.ShareCommand,
                CommandParameter = track,
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
                KeyboardAccelerator = Accelerator(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)
            });
        }

        // Hide song — toggles Spotify's "ban" collection for this track URI.
        // Server-side state syncs across devices via the same collection-v2
        // pipeline that powers Liked / Pinned / Saved.
        if (!string.IsNullOrEmpty(track.Uri) && track.Uri.StartsWith("spotify:track:", StringComparison.Ordinal))
        {
            var filter = Ioc.Default.GetService<IContentFilterService>();
            var hidden = filter?.IsTrackHidden(track.Uri) == true;
            items.Add(new ContextMenuItemModel
            {
                Text = hidden ? "Unhide song" : "Hide song",
                Glyph = hidden ? FluentGlyphs.ShowFilled : FluentGlyphs.HideOutline,
                Invoke = () => _ = ToggleHiddenAsync(track.Uri!, hidden)
            });
        }

        // Block artist — toggles Spotify's "artistban" collection.
        if (!string.IsNullOrEmpty(track.ArtistId))
        {
            var artistUri = track.ArtistId.StartsWith("spotify:artist:", StringComparison.Ordinal)
                ? track.ArtistId
                : "spotify:artist:" + track.ArtistId;
            var filter = Ioc.Default.GetService<IContentFilterService>();
            var blocked = filter?.IsArtistBlocked(artistUri) == true;
            items.Add(new ContextMenuItemModel
            {
                Text = blocked ? "Stop ignoring this artist" : "Don't play this artist",
                Glyph = blocked ? FluentGlyphs.Unblock : FluentGlyphs.Block,
                Invoke = () => _ = ToggleArtistBlockedAsync(artistUri, blocked)
            });
        }

        // Background mode ▶ — details-panel only (gated by SetBackgroundModeAction)
        if (ctx.SetBackgroundModeAction is not null)
        {
            var setBg = ctx.SetBackgroundModeAction;
            var current = ctx.CurrentBackgroundMode;
            var bgChildren = new List<ContextMenuItemModel>
            {
                new()
                {
                    Text = AppLocalization.GetString("TrackMenu_BackgroundNone"),
                    ItemType = ContextMenuItemType.Toggle,
                    IsChecked = current == DetailsBackgroundMode.None,
                    Invoke = () => setBg(DetailsBackgroundMode.None)
                },
                new()
                {
                    Text = AppLocalization.GetString("TrackMenu_BackgroundAlbumArt"),
                    ItemType = ContextMenuItemType.Toggle,
                    IsChecked = current == DetailsBackgroundMode.BlurredAlbumArt,
                    Invoke = () => setBg(DetailsBackgroundMode.BlurredAlbumArt)
                }
            };
            if (ctx.HasCanvas)
            {
                bgChildren.Add(new ContextMenuItemModel
                {
                    Text = AppLocalization.GetString("TrackMenu_BackgroundCanvas"),
                    ItemType = ContextMenuItemType.Toggle,
                    IsChecked = current == DetailsBackgroundMode.Canvas,
                    Invoke = () => setBg(DetailsBackgroundMode.Canvas)
                });
            }

            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_Background"),
                Glyph = FluentGlyphs.Background,
                Items = bgChildren
            });
        }

        // Caller-supplied extras (e.g. Canvas submenu in details panel)
        if (ctx.ExtraItems is { Count: > 0 })
        {
            foreach (var extra in ctx.ExtraItems) items.Add(extra);
        }

        // ── Remove (destructive, last, preceded by separator)
        if (ctx.RemoveCommand is not null)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = ctx.RemoveLabel ?? AppLocalization.GetString("TrackMenu_Remove"),
                Glyph = FluentGlyphs.Remove,
                Command = ctx.RemoveCommand,
                CommandParameter = track,
                IsDestructive = true
            });
        }

        return items;
    }

    private static void PlayDefault(ITrackItem track)
    {
        if (string.IsNullOrEmpty(track.Uri)) return;
        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null) return;
        _ = playback.PlayContextAsync(track.Uri);
    }

    private static async Task ToggleHiddenAsync(string trackUri, bool currentlyHidden)
    {
        var filter = Ioc.Default.GetService<IContentFilterService>();
        if (filter is null) return;
        try
        {
            await filter.SetTrackHiddenAsync(trackUri, !currentlyHidden).ConfigureAwait(true);
            Ioc.Default.GetService<INotificationService>()?.Show(
                currentlyHidden ? "Unhid track" : "Hid track",
                NotificationSeverity.Success,
                TimeSpan.FromSeconds(3));
        }
        catch
        {
            Ioc.Default.GetService<INotificationService>()?.Show(
                "Couldn't update hidden tracks",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
        }
    }

    private static async Task ToggleArtistBlockedAsync(string artistUri, bool currentlyBlocked)
    {
        var filter = Ioc.Default.GetService<IContentFilterService>();
        if (filter is null) return;
        try
        {
            await filter.SetArtistBlockedAsync(artistUri, !currentlyBlocked).ConfigureAwait(true);
            Ioc.Default.GetService<INotificationService>()?.Show(
                currentlyBlocked ? "Unblocked artist" : "Blocked artist",
                NotificationSeverity.Success,
                TimeSpan.FromSeconds(3));
        }
        catch
        {
            Ioc.Default.GetService<INotificationService>()?.Show(
                "Couldn't update blocked artists",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
        }
    }

    private static void PlayNextDefault(ITrackItem track)
    {
        if (string.IsNullOrEmpty(track.Uri)) return;
        Ioc.Default.GetService<IPlaybackStateService>()?.PlayNext(track.Uri);
    }

    private static void AddToQueueDefault(ITrackItem track)
    {
        if (string.IsNullOrEmpty(track.Uri)) return;
        Ioc.Default.GetService<IPlaybackStateService>()?.AddToQueue(track.Uri);
    }

    private static void ToggleLikeDefault(ITrackItem track)
    {
        if (track is NowPlayingTrackAdapter)
        {
            _ = ToggleCurrentPlaybackLikeAsync();
            return;
        }

        var svc = Ioc.Default.GetService<ITrackLikeService>();
        if (svc is null || string.IsNullOrEmpty(track.Uri)) return;
        svc.ToggleSave(SavedItemType.Track, track.Uri, track.IsLiked);
    }

    private static async Task ToggleCurrentPlaybackLikeAsync()
    {
        var playback = Ioc.Default.GetService<IPlaybackStateService>();
        var svc = Ioc.Default.GetService<ITrackLikeService>();
        var musicVideoMetadata = Ioc.Default.GetService<IMusicVideoMetadataService>();
        if (playback is null || svc is null) return;

        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(playback, musicVideoMetadata)
            .ConfigureAwait(true);
        if (string.IsNullOrEmpty(uri)) return;

        var isSaved = svc.IsSaved(SavedItemType.Track, uri);
        svc.ToggleSave(SavedItemType.Track, uri, isSaved);
    }

    private static KeyboardAccelerator Accelerator(VirtualKey key, VirtualKeyModifiers modifiers) =>
        new() { Key = key, Modifiers = modifiers };
}