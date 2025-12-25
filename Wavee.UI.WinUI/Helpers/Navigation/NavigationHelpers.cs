using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.ViewModels;
using Wavee.UI.WinUI.Views;
using Windows.System;
using Windows.UI.Core;

namespace Wavee.UI.WinUI.Helpers.Navigation;

public static class NavigationHelpers
{
    /// <summary>
    /// Check if Ctrl key is pressed (open in new tab modifier)
    /// </summary>
    public static bool IsCtrlPressed()
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
    }

    /// <summary>
    /// Navigate to home - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenHome(bool openInNewTab = false)
    {
        Navigate(typeof(HomePage), null, "Home", new SymbolIconSource { Symbol = Symbol.Home }, openInNewTab);
    }

    /// <summary>
    /// Open a new tab with the Start Page
    /// </summary>
    public static void OpenNewTab()
    {
        Navigate(typeof(StartPage), null, "New Tab", new SymbolIconSource { Symbol = Symbol.Add }, openInNewTab: true);
    }

    /// <summary>
    /// Navigate to artist - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenArtist(string artistId, string artistName, bool openInNewTab = false)
    {
        Navigate(typeof(ArtistPage), artistId, artistName, new SymbolIconSource { Symbol = Symbol.Contact }, openInNewTab);
    }

    /// <summary>
    /// Navigate to album - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenAlbum(string albumId, string albumName, bool openInNewTab = false)
    {
        Navigate(typeof(AlbumPage), albumId, albumName, new SymbolIconSource { Symbol = Symbol.Audio }, openInNewTab);
    }

    /// <summary>
    /// Navigate to search - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenSearch(string? query = null, bool openInNewTab = false)
    {
        Navigate(typeof(SearchPage), query, "Search", new SymbolIconSource { Symbol = Symbol.Find }, openInNewTab);
    }

    /// <summary>
    /// Navigate to library - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenLibrary(bool openInNewTab = false)
    {
        Navigate(typeof(LibraryPage), null, "Library", new SymbolIconSource { Symbol = Symbol.Library }, openInNewTab);
    }

    /// <summary>
    /// Navigate to playlist - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenPlaylist(string playlistId, string playlistName, bool openInNewTab = false)
    {
        Navigate(typeof(PlaylistPage), playlistId, playlistName, new SymbolIconSource { Symbol = Symbol.MusicInfo }, openInNewTab);
    }

    /// <summary>
    /// Navigate to albums library - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenAlbums(bool openInNewTab = false)
    {
        Navigate(typeof(LibraryPage), "albums", "Albums", new SymbolIconSource { Symbol = Symbol.Audio }, openInNewTab);
    }

    /// <summary>
    /// Navigate to artists library - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenArtists(bool openInNewTab = false)
    {
        Navigate(typeof(LibraryPage), "artists", "Artists", new SymbolIconSource { Symbol = Symbol.Contact }, openInNewTab);
    }

    /// <summary>
    /// Navigate to liked songs - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenLikedSongs(bool openInNewTab = false)
    {
        Navigate(typeof(LibraryPage), "likedsongs", "Liked Songs", new SymbolIconSource { Symbol = Symbol.Like }, openInNewTab);
    }

    /// <summary>
    /// Navigate to user profile - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenProfile(bool openInNewTab = false)
    {
        Navigate(typeof(ProfilePage), null, "Profile", new SymbolIconSource { Symbol = Symbol.Contact }, openInNewTab);
    }

    /// <summary>
    /// Open create playlist/folder page - always opens in new tab
    /// </summary>
    public static void OpenCreatePlaylist(bool isFolder = false)
    {
        var header = isFolder ? "New Folder" : "New Playlist";
        var glyph = isFolder ? "\uE8F4" : "\uE93F";
        var fontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["MediaPlayerIconsFontFamily"];

        var iconSource = new FontIconSource
        {
            FontFamily = fontFamily,
            Glyph = glyph
        };

        Navigate(typeof(CreatePlaylistPage), isFolder, header, iconSource, openInNewTab: true);
    }

    private static void Navigate(Type pageType, object? parameter, string header, IconSource icon, bool openInNewTab)
    {
        // Always open in new tab if no tabs exist
        if (openInNewTab || ShellViewModel.TabInstances.Count == 0)
        {
            AddNewTab(pageType, parameter, header, icon);
        }
        else
        {
            NavigateInCurrentTab(pageType, parameter, header, icon);
        }

        // Update navigation state after frame has navigated (deferred to next UI tick)
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            var shellViewModel = Ioc.Default.GetService<ShellViewModel>();
            if (shellViewModel != null)
            {
                shellViewModel.UpdateNavigationState();

                // Clear sidebar selection for non-library pages
                if (pageType != typeof(LibraryPage))
                {
                    shellViewModel.SelectedSidebarItem = null;
                }
            }
        });
    }

    private static void NavigateInCurrentTab(Type pageType, object? parameter, string header, IconSource icon)
    {
        var currentIndex = App.AppModel.TabStripSelectedIndex;
        if (currentIndex < 0 || currentIndex >= ShellViewModel.TabInstances.Count)
        {
            // Fallback to new tab if no valid current tab
            AddNewTab(pageType, parameter, header, icon);
            return;
        }

        var currentTab = ShellViewModel.TabInstances[currentIndex];
        currentTab.Header = header;
        currentTab.IconSource = icon;
        currentTab.ToolTipText = header;
        currentTab.Navigate(pageType, parameter);
    }

    private static void AddNewTab(Type pageType, object? parameter, string header, IconSource icon)
    {
        var tab = new TabBarItem
        {
            Header = header,
            IconSource = icon,
            ToolTipText = header
        };

        tab.Navigate(pageType, parameter);
        ShellViewModel.TabInstances.Add(tab);
        ShellViewModel.SelectTab(ShellViewModel.TabInstances.Count - 1);
    }

    /// <summary>
    /// Handle content changes from pages to update tab header
    /// </summary>
    public static void Control_ContentChanged(object? sender, Data.Parameters.TabItemParameter e)
    {
        if (sender is TabBarItem tabItem && !string.IsNullOrEmpty(e.Title))
        {
            tabItem.Header = e.Title;
            tabItem.ToolTipText = e.Title;
        }
    }
}
