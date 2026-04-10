using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Builds context menus for media cards (artists, albums, playlists).
/// Similar to TrackContextMenu but for card-level actions.
/// </summary>
public static class CardContextMenu
{
    public static MenuFlyout CreateForArtist(string artistUri, string artistName)
    {
        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("CardMenu_OpenArtist"),
            Icon = new FontIcon { Glyph = "\uE77B" },
            Tag = new CardNavInfo(artistUri, artistName)
        };
        openItem.Click += OpenArtist_Click;
        flyout.Items.Add(openItem);

        var openInNewTabItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("CardMenu_OpenInNewTab"),
            Icon = new FontIcon { Glyph = "\uE8A7" },
            Tag = new CardNavInfo(artistUri, artistName)
        };
        openInNewTabItem.Click += OpenArtistInNewTab_Click;
        flyout.Items.Add(openInNewTabItem);

        return flyout;
    }

    public static MenuFlyout CreateForAlbum(string albumUri, string albumName)
    {
        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("CardMenu_OpenAlbum"),
            Icon = new FontIcon { Glyph = "\uE93C" },
            Tag = new CardNavInfo(albumUri, albumName)
        };
        openItem.Click += OpenAlbum_Click;
        flyout.Items.Add(openItem);

        var openInNewTabItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("CardMenu_OpenInNewTab"),
            Icon = new FontIcon { Glyph = "\uE8A7" },
            Tag = new CardNavInfo(albumUri, albumName)
        };
        openInNewTabItem.Click += OpenAlbumInNewTab_Click;
        flyout.Items.Add(openInNewTabItem);

        return flyout;
    }

    public static MenuFlyout CreateForPlaylist(string playlistUri, string playlistName)
    {
        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("CardMenu_OpenPlaylist"),
            Icon = new FontIcon { Glyph = "\uE8FD" },
            Tag = new CardNavInfo(playlistUri, playlistName)
        };
        openItem.Click += OpenPlaylist_Click;
        flyout.Items.Add(openItem);

        var openInNewTabItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("CardMenu_OpenInNewTab"),
            Icon = new FontIcon { Glyph = "\uE8A7" },
            Tag = new CardNavInfo(playlistUri, playlistName)
        };
        openInNewTabItem.Click += OpenPlaylistInNewTab_Click;
        flyout.Items.Add(openInNewTabItem);

        return flyout;
    }

    private static void OpenArtist_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is CardNavInfo info)
        {
            var id = ExtractId(info.Uri, "spotify:artist:");
            NavigationHelpers.OpenArtist(id, info.Name);
        }
    }

    private static void OpenArtistInNewTab_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is CardNavInfo info)
        {
            var id = ExtractId(info.Uri, "spotify:artist:");
            NavigationHelpers.OpenArtist(id, info.Name, openInNewTab: true);
        }
    }

    private static void OpenAlbum_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is CardNavInfo info)
        {
            var id = ExtractId(info.Uri, "spotify:album:");
            NavigationHelpers.OpenAlbum(id, info.Name);
        }
    }

    private static void OpenAlbumInNewTab_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is CardNavInfo info)
        {
            var id = ExtractId(info.Uri, "spotify:album:");
            NavigationHelpers.OpenAlbum(id, info.Name, openInNewTab: true);
        }
    }

    private static void OpenPlaylist_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is CardNavInfo info)
        {
            var id = ExtractId(info.Uri, "spotify:playlist:");
            NavigationHelpers.OpenPlaylist(id, info.Name);
        }
    }

    private static void OpenPlaylistInNewTab_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is CardNavInfo info)
        {
            var id = ExtractId(info.Uri, "spotify:playlist:");
            NavigationHelpers.OpenPlaylist(id, info.Name, openInNewTab: true);
        }
    }

    /// <summary>
    /// Extracts the ID from a Spotify URI by removing the prefix.
    /// If the URI doesn't contain the prefix, returns it as-is (assumed to already be an ID).
    /// </summary>
    private static string ExtractId(string uri, string prefix)
    {
        return uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
    }

    /// <summary>
    /// Holds the URI and display name for card navigation.
    /// </summary>
    private sealed record CardNavInfo(string Uri, string Name);
}
