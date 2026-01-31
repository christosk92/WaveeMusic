using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Navigation;

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
            Text = "Play",
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
            Text = "Play next",
            Icon = new FontIcon { Glyph = "\uE71A" }
        };
        // TODO: Wire up play next command when available
        menu.Items.Add(playNextItem);

        // Add to queue
        var addToQueueItem = new MenuFlyoutItem
        {
            Text = "Add to queue",
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        if (options.AddToQueueCommand != null)
        {
            addToQueueItem.Command = options.AddToQueueCommand;
            addToQueueItem.CommandParameter = track;
        }
        menu.Items.Add(addToQueueItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Add to playlist (submenu)
        var addToPlaylistItem = new MenuFlyoutSubItem
        {
            Text = "Add to playlist",
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        // TODO: Populate from ILibraryDataService.GetPlaylistsAsync()
        addToPlaylistItem.Items.Add(new MenuFlyoutItem { Text = "Create new playlist..." });
        menu.Items.Add(addToPlaylistItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Go to artist
        var goToArtistItem = new MenuFlyoutItem
        {
            Text = "Go to artist",
            Icon = new FontIcon { Glyph = "\uE77B" },
            Tag = track
        };
        goToArtistItem.Click += GoToArtist_Click;
        menu.Items.Add(goToArtistItem);

        // Go to album
        var goToAlbumItem = new MenuFlyoutItem
        {
            Text = "Go to album",
            Icon = new FontIcon { Glyph = "\uE93C" },
            Tag = track
        };
        goToAlbumItem.Click += GoToAlbum_Click;
        menu.Items.Add(goToAlbumItem);

        // Remove (if command provided)
        if (options.RemoveCommand != null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var removeItem = new MenuFlyoutItem
            {
                Text = options.RemoveLabel ?? "Remove",
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
