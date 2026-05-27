using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ContextMenu.Builders;

/// <summary>
/// Entity-aware context menu dispatcher for any card surface (ContentCard /
/// BaselineHomeCard / ShortsPill / HomePage shelves).
///
/// Parses the Spotify URI, hydrates state from the same DI services the rest
/// of the app reads (<see cref="ITrackLikeService"/>, <see cref="IPinService"/>,
/// <see cref="ILibraryDataService"/>), and delegates to the matching builder:
/// album / artist / playlist / show / episode. An "Add to playlist" submenu is
/// injected for every URI whose tracks can be resolved via
/// <see cref="IPlaylistDragDropMediator"/> — the same resolver the drag-drop
/// path uses, so the menu offers exactly the targets a drag would.
/// </summary>
public static class CardContextMenuBuilder
{
    /// <summary>
    /// Builds the menu for any card-shaped UI element.
    /// </summary>
    /// <param name="uri">Spotify URI of the entity behind the card.</param>
    /// <param name="title">Display name (for share toasts + Add-to-playlist labels).</param>
    /// <param name="imageUrl">Cover image URL (unused by the entity menus today; retained for future surface tweaks).</param>
    /// <param name="openAction">
    /// Per-surface "Open" / "Open in new tab" routing. When non-null this
    /// wins over the URI-parsing fallback (some hosts route via a
    /// <c>HomeSectionItem</c> rather than the raw URI).
    /// </param>
    public static IReadOnlyList<ContextMenuItemModel> BuildForUri(
        string uri,
        string title,
        string? imageUrl,
        Action<bool>? openAction)
    {
        if (string.IsNullOrEmpty(uri))
            return BuildMinimalMenu(openAction);

        var parts = uri.Split(':');
        if (parts.Length < 2)
            return BuildMinimalMenu(openAction);

        var likeService = Ioc.Default.GetService<ITrackLikeService>();
        var pinService = Ioc.Default.GetService<IPinService>();
        var library = Ioc.Default.GetService<ILibraryDataService>();
        var mediator = Ioc.Default.GetService<IPlaylistDragDropMediator>();

        switch (parts[1])
        {
            case "album":
            {
                var albumId = parts.Length >= 3 ? parts[2] : string.Empty;
                var menu = AlbumContextMenuBuilder.Build(new AlbumMenuContext
                {
                    AlbumId = albumId,
                    AlbumName = title,
                    IsSaved = likeService?.IsSaved(SavedItemType.Album, uri) ?? false,
                    IsPinned = pinService?.IsPinned(uri) ?? false,
                });
                return InjectContextEntries(
                    menu,
                    title,
                    mediator is null
                        ? null
                        : ct => mediator.GetAlbumTrackUrisAsync(uri, ct));
            }

            case "artist":
            {
                var artistId = parts.Length >= 3 ? parts[2] : string.Empty;
                var menu = ArtistContextMenuBuilder.Build(new ArtistMenuContext
                {
                    ArtistId = artistId,
                    ArtistName = title,
                    IsFollowing = likeService?.IsSaved(SavedItemType.Artist, uri) ?? false,
                    IsPinned = pinService?.IsPinned(uri) ?? false,
                });
                return InjectContextEntries(
                    menu,
                    title,
                    mediator is null
                        ? null
                        : ct => mediator.GetArtistTopTrackUrisAsync(uri, ct));
            }

            case "playlist":
            {
                var playlistId = parts.Length >= 3 ? parts[2] : string.Empty;
                var menu = PlaylistContextMenuBuilder.Build(new PlaylistMenuContext
                {
                    PlaylistId = playlistId,
                    PlaylistName = title,
                    IsOwner = library?.IsOwnedByCurrentUser(uri) ?? false,
                    IsSaved = library?.IsInUserRootlist(uri) ?? false,
                    IsPinned = pinService?.IsPinned(uri) ?? false,
                });
                return InjectContextEntries(
                    menu,
                    title,
                    mediator is null
                        ? null
                        : ct => mediator.GetPlaylistTrackUrisAsync(uri, ct),
                    addToPlaylistLabel: AppLocalization.GetString("PlaylistMenu_CopyContentsToPlaylist"));
            }

            case "show":
            {
                var showId = parts.Length >= 3 ? parts[2] : string.Empty;
                var menu = ShowContextMenuBuilder.Build(new ShowMenuContext
                {
                    ShowId = showId,
                    ShowName = title,
                    IsSaved = likeService?.IsSaved(SavedItemType.Show, uri) ?? false,
                    IsPinned = pinService?.IsPinned(uri) ?? false,
                });
                return InjectContextEntries(
                    menu,
                    title,
                    mediator is null
                        ? null
                        : ct => mediator.GetShowEpisodeUrisAsync(uri, ct));
            }

            case "episode":
            {
                var episodeId = parts.Length >= 3 ? parts[2] : string.Empty;
                return EpisodeContextMenuBuilder.Build(new EpisodeMenuContext
                {
                    EpisodeId = episodeId,
                    EpisodeName = title,
                    IsPinned = pinService?.IsPinned(uri) ?? false,
                });
            }

            case "collection":
                return BuildLikedSongsMenu(openAction, mediator);

            case "user" when uri.Contains(":collection", StringComparison.OrdinalIgnoreCase):
                return BuildLikedSongsMenu(openAction, mediator);

            default:
                // page / section / genre / user / unknown — minimal Open / Open in new tab.
                return BuildMinimalMenu(openAction);
        }
    }

    /// <summary>
    /// Walks the entity menu and inserts two rows around the existing
    /// "Add to queue" entry (matched by <see cref="FluentGlyphs.Queue"/>):
    /// <list type="bullet">
    ///   <item><b>Play next</b> — directly <i>before</i> Add to queue. Resolves
    ///   the source URI to track URIs via the supplied loader, then calls
    ///   <see cref="IPlaybackStateService.PlayNext(IEnumerable{string})"/>.</item>
    ///   <item><b>Add to playlist▸</b> — directly <i>after</i> Add to queue.
    ///   Opens a folder-aware submenu via <see cref="AddToPlaylistSubmenuBuilder"/>.</item>
    /// </list>
    /// Falls back to "before any trailing destructive separator (or at the
    /// end)" when no Queue row exists. Mediator unavailable in tests / mock
    /// contexts → both rows are omitted rather than shipped as no-ops.
    /// </summary>
    private static IReadOnlyList<ContextMenuItemModel> InjectContextEntries(
        IReadOnlyList<ContextMenuItemModel> menu,
        string sourceLabel,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? trackUrisLoader,
        string? addToPlaylistLabel = null)
    {
        if (trackUrisLoader is null) return menu;

        var playNext = new ContextMenuItemModel
        {
            Text = AppLocalization.GetString("TrackMenu_PlayNext"),
            Glyph = FluentGlyphs.PlayNext,
            Invoke = () => _ = PlayNextFromSource(sourceLabel, trackUrisLoader)
        };

        var addToPlaylist = new ContextMenuItemModel
        {
            // Playlist sources read "Copy contents to playlist" because the
            // tracks are copied — the source isn't nested as a sub-playlist.
            // Other entity types keep the generic "Add to playlist" label.
            Text = addToPlaylistLabel ?? AppLocalization.GetString("TrackMenu_AddToPlaylist"),
            Glyph = FluentGlyphs.Add,
            LoadSubMenuAsync = AddToPlaylistSubmenuBuilder.Loader(sourceLabel, trackUrisLoader)
        };

        var copy = new List<ContextMenuItemModel>(menu.Count + 2);
        copy.AddRange(menu);

        // Find the AddToQueue row (first non-primary item with the Queue glyph).
        var queueIndex = -1;
        for (var i = 0; i < copy.Count; i++)
        {
            var item = copy[i];
            if (item.ItemType == ContextMenuItemType.Item
                && !item.IsPrimary
                && string.Equals(item.Glyph, FluentGlyphs.Queue, StringComparison.Ordinal))
            {
                queueIndex = i;
                break;
            }
        }

        if (queueIndex < 0)
        {
            // No Queue row — insert before any trailing destructive separator
            // (so Delete stays at the bottom). Order: PlayNext, then AddToPlaylist.
            var insertIndex = copy.Count;
            for (var i = copy.Count - 1; i >= 0; i--)
            {
                if (copy[i].ItemType == ContextMenuItemType.Separator)
                {
                    insertIndex = i;
                    break;
                }
            }
            copy.Insert(insertIndex, addToPlaylist);
            copy.Insert(insertIndex, playNext);
        }
        else
        {
            // Insert AddToPlaylist after AddToQueue first, then PlayNext
            // before AddToQueue. Order matters — inserting at queueIndex+1
            // first keeps the index stable for the second insert.
            copy.Insert(queueIndex + 1, addToPlaylist);
            copy.Insert(queueIndex, playNext);
        }

        return copy;
    }

    private static async Task PlayNextFromSource(
        string sourceLabel,
        Func<CancellationToken, Task<IReadOnlyList<string>>> trackUrisLoader)
    {
        var playback = Ioc.Default.GetService<IPlaybackStateService>();
        var notifications = Ioc.Default.GetService<INotificationService>();
        if (playback is null) return;

        IReadOnlyList<string> uris;
        try
        {
            uris = await trackUrisLoader(CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            notifications?.Show(
                $"Couldn't load tracks from {(string.IsNullOrEmpty(sourceLabel) ? "source" : sourceLabel)}",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(3));
            return;
        }

        if (uris.Count == 0)
        {
            notifications?.Show(
                $"Nothing to play next from {(string.IsNullOrEmpty(sourceLabel) ? "source" : sourceLabel)}",
                NotificationSeverity.Informational,
                TimeSpan.FromSeconds(3));
            return;
        }

        playback.PlayNext(uris);
        var noun = uris.Count == 1 ? "track" : "tracks";
        notifications?.Show(
            $"{uris.Count} {noun} will play next",
            NotificationSeverity.Success,
            TimeSpan.FromSeconds(3));
    }

    private static IReadOnlyList<ContextMenuItemModel> BuildLikedSongsMenu(
        Action<bool>? openAction,
        IPlaylistDragDropMediator? mediator)
    {
        var items = new List<ContextMenuItemModel>
        {
            new()
            {
                Text = AppLocalization.GetString("TrackMenu_Play"),
                Glyph = FluentGlyphs.Play,
                AccentIconStyleKey = "App.AccentIcons.Media.Play",
                IsPrimary = true,
                Invoke = () => PlayLikedSongsDefault(shuffle: false)
            },
            new()
            {
                Text = AppLocalization.GetString("PlaylistMenu_Shuffle"),
                Glyph = FluentGlyphs.Shuffle,
                AccentIconStyleKey = "App.AccentIcons.Media.Shuffle",
                IsPrimary = true,
                Invoke = () => PlayLikedSongsDefault(shuffle: true)
            },
            ContextMenuItemModel.Separator,
            new()
            {
                Text = AppLocalization.GetString("CardMenu_Open"),
                Glyph = FluentGlyphs.Open,
                Invoke = () => OpenLikedSongs(openAction, openInNewTab: false)
            },
            new()
            {
                Text = AppLocalization.GetString("CardMenu_OpenInNewTab"),
                Glyph = FluentGlyphs.OpenInNewTab,
                Invoke = () => OpenLikedSongs(openAction, openInNewTab: true)
            },
        };

        if (mediator is not null)
        {
            items.Add(new ContextMenuItemModel
            {
                Text = AppLocalization.GetString("TrackMenu_AddToPlaylist"),
                Glyph = FluentGlyphs.Add,
                LoadSubMenuAsync = AddToPlaylistSubmenuBuilder.Loader(
                    sourceLabel: "Liked Songs",
                    trackUrisLoader: ct => mediator.GetLikedSongUrisAsync(ct))
            });
        }

        return items;
    }

    private static IReadOnlyList<ContextMenuItemModel> BuildMinimalMenu(Action<bool>? openAction)
    {
        return new List<ContextMenuItemModel>
        {
            new()
            {
                Text = AppLocalization.GetString("CardMenu_Open"),
                Glyph = FluentGlyphs.Open,
                IsPrimary = true,
                Invoke = () => openAction?.Invoke(false)
            },
            new()
            {
                Text = AppLocalization.GetString("CardMenu_OpenInNewTab"),
                Glyph = FluentGlyphs.OpenInNewTab,
                IsPrimary = true,
                Invoke = () => openAction?.Invoke(true)
            }
        };
    }

    private static void PlayLikedSongsDefault(bool shuffle)
    {
        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null) return;
        if (shuffle)
            Ioc.Default.GetService<IPlaybackStateService>()?.SetShuffle(true);
        // The collection URI Spotify uses for the user's Liked Songs context.
        _ = playback.PlayContextAsync("spotify:collection:tracks");
    }

    private static void OpenLikedSongs(Action<bool>? openAction, bool openInNewTab)
    {
        if (openAction is not null) openAction(openInNewTab);
        else NavigationHelpers.OpenLikedSongs(openInNewTab);
    }
}
