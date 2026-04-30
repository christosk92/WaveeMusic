using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
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
            Invoke = ctx.PlayCommand is null
                ? () => Debug.WriteLine($"Play: {track.Uri}")
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
            Invoke = ctx.PlayNextCommand is null
                ? () => Debug.WriteLine($"PlayNext: {track.Uri}")
                : null,
            IsPrimary = true
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_PlayAfter"),
            Glyph = FluentGlyphs.AddToQueue,
            AccentIconStyleKey = "App.AccentIcons.Media.PlayAfter",
            Command = ctx.AddToQueueCommand,
            CommandParameter = track,
            Invoke = ctx.AddToQueueCommand is null
                ? () => Debug.WriteLine($"PlayAfter: {track.Uri}")
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
        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_AddToPlaylist"),
            Glyph = FluentGlyphs.Add,
            LoadSubMenuAsync = () => LoadUserPlaylistsAsync(track)
        });

        items.Add(new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_SongRadio"),
            Glyph = FluentGlyphs.Radio,
            Command = ctx.StartRadioCommand,
            CommandParameter = track,
            Invoke = ctx.StartRadioCommand is null
                ? () => Debug.WriteLine($"SongRadio: {track.Uri}")
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

        // View credits — always visible (falls back to Debug log when no action provided)
        {
            var creditsAction = ctx.ShowCreditsAction;
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_ViewCredits"),
                Glyph = FluentGlyphs.Credits,
                Invoke = creditsAction ?? (() => Debug.WriteLine($"ViewCredits: {track.Uri}"))
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

    private static async Task<IReadOnlyList<ContextMenuItemModel>> LoadUserPlaylistsAsync(ITrackItem track)
    {
        var library = Ioc.Default.GetService<ILibraryDataService>();
        if (library is null)
        {
            return new[]
            {
                new ContextMenuItemModel
                {
                    Text = AppLocalization.GetString("TrackMenu_CreateNewPlaylist"),
                    Glyph = FluentGlyphs.CreatePlaylist
                }
            };
        }

        var playlists = await library.GetUserPlaylistsAsync();
        var items = new List<ContextMenuItemModel>
        {
            new()
            {
                Text = AppLocalization.GetString("TrackMenu_CreateNewPlaylist"),
                Glyph = FluentGlyphs.CreatePlaylist,
                Invoke = () => AddToNewPlaylist(track)
            },
            ContextMenuItemModel.Separator
        };

        foreach (var p in playlists)
        {
            if (!p.IsOwner) continue;
            var pid = p.Id;
            var pname = p.Name;
            items.Add(new ContextMenuItemModel
            {
                Text = pname,
                Glyph = FluentGlyphs.Playlist,
                Invoke = () => AddToPlaylist(track, pid, pname)
            });
        }

        return items;
    }

    private static void AddToPlaylist(ITrackItem track, string playlistId, string playlistName)
    {
        // TODO: Call _libraryDataService.AddTracksToPlaylistAsync when the mutation method exists.
        Debug.WriteLine($"AddToPlaylist: {track.Uri} -> {playlistId} ({playlistName})");
    }

    private static void AddToNewPlaylist(ITrackItem track)
    {
        Debug.WriteLine($"AddToNewPlaylist: {track.Uri}");
    }

    private static KeyboardAccelerator Accelerator(VirtualKey key, VirtualKeyModifiers modifiers) =>
        new() { Key = key, Modifiers = modifiers };
}
