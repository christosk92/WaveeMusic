using System;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Track;

/// <summary>
/// Options for building a track context menu.
/// </summary>
public sealed class TrackContextMenuOptions
{
    public ICommand? PlayCommand { get; init; }
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
}

/// <summary>
/// Static helper to build consistent context menus for tracks.
/// </summary>
public static class TrackContextMenu
{
    public static MenuFlyout Create(ITrackItem track, TrackContextMenuOptions? options = null)
    {
        options ??= new TrackContextMenuOptions();

        var menu = new MenuFlyout();

        // Play
        var playItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("TrackMenu_Play"),
            Icon = new FontIcon { Glyph = "\uE768" }
        };
        if (options.PlayCommand != null)
        {
            playItem.Command = options.PlayCommand;
            playItem.CommandParameter = track;
        }
        menu.Items.Add(playItem);

        // Play after current
        var playNextItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("TrackMenu_PlayNext"),
            Icon = new FontIcon { Glyph = "\uE71A" }
        };
        // TODO: Wire up play next command when available
        menu.Items.Add(playNextItem);

        // Add to queue
        var addToQueueItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("TrackMenu_AddToQueue"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        if (options.AddToQueueCommand != null)
        {
            addToQueueItem.Command = options.AddToQueueCommand;
            addToQueueItem.CommandParameter = track;
        }
        menu.Items.Add(addToQueueItem);

        // Save to Liked Songs / Remove from Liked Songs
        var likeItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString(track.IsLiked ? "TrackMenu_RemoveFromLikedSongs" : "TrackMenu_SaveToLikedSongs"),
            Icon = new FontIcon { Glyph = track.IsLiked ? "\uEB52" : "\uEB51" }
        };
        if (options.ToggleLikeCommand != null)
        {
            likeItem.Command = options.ToggleLikeCommand;
            likeItem.CommandParameter = track;
        }
        menu.Items.Add(likeItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Add to playlist (submenu)
        var addToPlaylistItem = new MenuFlyoutSubItem
        {
            Text = AppLocalization.GetString("TrackMenu_AddToPlaylist"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        // TODO: Populate from ILibraryDataService.GetPlaylistsAsync()
        addToPlaylistItem.Items.Add(new MenuFlyoutItem { Text = AppLocalization.GetString("TrackMenu_CreateNewPlaylist") });
        menu.Items.Add(addToPlaylistItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Go to artist
        var goToArtistItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("TrackMenu_GoToArtist"),
            Icon = new FontIcon { Glyph = "\uE77B" },
            Tag = track
        };
        goToArtistItem.Click += GoToArtist_Click;
        menu.Items.Add(goToArtistItem);

        // Go to album
        var goToAlbumItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("TrackMenu_GoToAlbum"),
            Icon = new FontIcon { Glyph = "\uE93C" },
            Tag = track
        };
        goToAlbumItem.Click += GoToAlbum_Click;
        menu.Items.Add(goToAlbumItem);

        // Start radio
        if (options.StartRadioCommand != null)
        {
            var radioItem = new MenuFlyoutItem
            {
                Text = AppLocalization.GetString("TrackMenu_StartRadio"),
                Icon = new FontIcon { Glyph = "\uEC05" },
                Command = options.StartRadioCommand,
                CommandParameter = track
            };
            menu.Items.Add(radioItem);
        }

        // Details-panel-specific items
        if (options.ShowCreditsAction != null || options.SetBackgroundModeAction != null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            if (options.ShowCreditsAction != null)
            {
                var creditsItem = new MenuFlyoutItem
                {
                    Text = AppLocalization.GetString("TrackMenu_ShowCredits"),
                    Icon = new FontIcon { Glyph = "\uE946" }
                };
                var creditsAction = options.ShowCreditsAction;
                creditsItem.Click += (_, _) => creditsAction();
                menu.Items.Add(creditsItem);
            }

            if (options.SetBackgroundModeAction != null)
            {
                var bgSubMenu = new MenuFlyoutSubItem
                {
                    Text = AppLocalization.GetString("TrackMenu_Background"),
                    Icon = new FontIcon { Glyph = "\uE91B" }
                };

                var noneItem = new ToggleMenuFlyoutItem
                {
                    Text = AppLocalization.GetString("TrackMenu_BackgroundNone"),
                    IsChecked = options.CurrentBackgroundMode == DetailsBackgroundMode.None
                };
                var setAction = options.SetBackgroundModeAction;
                noneItem.Click += (_, _) => setAction(DetailsBackgroundMode.None);
                bgSubMenu.Items.Add(noneItem);

                var blurItem = new ToggleMenuFlyoutItem
                {
                    Text = AppLocalization.GetString("TrackMenu_BackgroundAlbumArt"),
                    IsChecked = options.CurrentBackgroundMode == DetailsBackgroundMode.BlurredAlbumArt
                };
                blurItem.Click += (_, _) => setAction(DetailsBackgroundMode.BlurredAlbumArt);
                bgSubMenu.Items.Add(blurItem);

                if (options.HasCanvas)
                {
                    var canvasItem = new ToggleMenuFlyoutItem
                    {
                        Text = AppLocalization.GetString("TrackMenu_BackgroundCanvas"),
                        IsChecked = options.CurrentBackgroundMode == DetailsBackgroundMode.Canvas
                    };
                    canvasItem.Click += (_, _) => setAction(DetailsBackgroundMode.Canvas);
                    bgSubMenu.Items.Add(canvasItem);
                }

                menu.Items.Add(bgSubMenu);
            }
        }

        // Share
        if (options.ShareCommand != null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var shareItem = new MenuFlyoutItem
            {
                Text = AppLocalization.GetString("TrackMenu_Share"),
                Icon = new FontIcon { Glyph = "\uE72D" },
                Command = options.ShareCommand,
                CommandParameter = track
            };
            menu.Items.Add(shareItem);
        }

        // Remove (if command provided)
        if (options.RemoveCommand != null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var removeItem = new MenuFlyoutItem
            {
                Text = options.RemoveLabel ?? AppLocalization.GetString("TrackMenu_Remove"),
                Icon = new FontIcon { Glyph = "\uE74D" },
                Command = options.RemoveCommand,
                CommandParameter = track
            };
            menu.Items.Add(removeItem);
        }

        return menu;
    }

    private static void GoToArtist_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is ITrackItem track)
        {
            NavigationHelpers.OpenArtist(track.ArtistId, track.ArtistName);
        }
    }

    private static void GoToAlbum_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is ITrackItem track)
        {
            NavigationHelpers.OpenAlbum(track.AlbumId, track.AlbumName);
        }
    }
}
