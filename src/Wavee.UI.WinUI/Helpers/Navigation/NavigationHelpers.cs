using System;
using System.Collections.Generic;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Wavee.UI.WinUI.Views;
using Windows.System;
using Windows.UI.Core;

namespace Wavee.UI.WinUI.Helpers.Navigation;

public static class NavigationHelpers
{
    private static ShellViewModel? _shellViewModel;

    /// <summary>
    /// Initialize with the ShellViewModel instance to avoid service locator calls.
    /// </summary>
    public static void Initialize(ShellViewModel vm) => _shellViewModel = vm;

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
        Navigate(typeof(HomePage), null, "Home", CreateIconSource(typeof(HomePage), null), openInNewTab);
    }

    /// <summary>
    /// Open a new tab with the Start Page
    /// </summary>
    public static void OpenNewTab()
    {
        Navigate(typeof(StartPage), null, "New Tab", CreateIconSource(typeof(StartPage), null), openInNewTab: true);
    }

    /// <summary>
    /// Navigate to artist - within tab by default, new tab if openInNewTab=true.
    /// Local artist URIs (<c>wavee:local:artist:</c>) route to the local detail
    /// page when one is registered; until then they are silently ignored so the
    /// click is a no-op rather than a Spotify-API miss.
    /// </summary>
    public static void OpenArtist(object parameter, string artistName, bool openInNewTab = false)
    {
        if (parameter is string s && Wavee.Core.PlayableUri.IsLocal(s))
        {
            // TODO: route to LocalArtistPage when added.
            return;
        }
        Navigate(typeof(ArtistPage), parameter, artistName, CreateIconSource(typeof(ArtistPage), parameter), openInNewTab);
    }

    /// <summary>
    /// Navigate to album - within tab by default, new tab if openInNewTab=true.
    /// Local album URIs (<c>wavee:local:album:</c>) route to the local library
    /// page (per-album detail page can land later).
    /// </summary>
    public static void OpenAlbum(object parameter, string albumName, bool openInNewTab = false)
    {
        if (parameter is string s && Wavee.Core.PlayableUri.IsLocal(s))
        {
            OpenLocalLibrary(openInNewTab);
            return;
        }
        Navigate(typeof(AlbumPage), parameter, albumName, CreateIconSource(typeof(AlbumPage), parameter), openInNewTab);
    }

    /// <summary>
    /// Navigate to the local library page — the "View all" target for the
    /// Home page's Local Files section, and a fallback destination for
    /// <c>wavee:local:*</c> URIs that don't yet have dedicated detail pages.
    /// </summary>
    public static void OpenLocalLibrary(bool openInNewTab = false)
    {
        Navigate(typeof(LocalLibraryPage), null, "Local files",
            CreateIconSource(typeof(LocalLibraryPage), null), openInNewTab);
    }

    /// <summary>
    /// Navigate to the video player page. The page hosts a
    /// <c>MediaPlayerElement</c> bound to the app-wide
    /// <c>ILocalMediaPlayer</c> so video frames render here while audio and
    /// transport state continue to flow through the orchestrator.
    /// </summary>
    public static void OpenVideoPlayer(bool openInNewTab = false)
    {
        Navigate(typeof(VideoPlayerPage), null, "Now playing",
            CreateIconSource(typeof(VideoPlayerPage), null), openInNewTab);
    }

    /// <summary>
    /// Navigate to search - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenSearch(string? query = null, bool openInNewTab = false)
    {
        Navigate(typeof(SearchPage), query, "Search", CreateIconSource(typeof(SearchPage), query), openInNewTab);
    }

    /// <summary>
    /// Navigate to library - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenLibrary(bool openInNewTab = false)
    {
        Navigate(typeof(LibraryPage), null, AppLocalization.GetString("Shell_SidebarYourLibrary"), CreateIconSource(typeof(LibraryPage), null), openInNewTab);
    }

    /// <summary>
    /// Navigate to playlist - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenPlaylist(object parameter, string playlistName, bool openInNewTab = false)
    {
        Navigate(typeof(PlaylistPage), parameter, playlistName, CreateIconSource(typeof(PlaylistPage), parameter), openInNewTab);
    }

    /// <summary>
    /// Navigate to albums library - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenConcert(object parameter, string concertName, bool openInNewTab = false)
    {
        Navigate(typeof(ConcertPage), parameter, concertName, CreateIconSource(typeof(ConcertPage), parameter), openInNewTab);
    }

    public static void OpenAlbums(bool openInNewTab = false)
    {
        Navigate(typeof(LibraryPage), "albums", AppLocalization.GetString("Shell_SidebarAlbums"), CreateIconSource(typeof(LibraryPage), "albums"), openInNewTab);
    }

    /// <summary>
    /// Navigate to artists library - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenArtists(bool openInNewTab = false)
    {
        Navigate(typeof(LibraryPage), "artists", AppLocalization.GetString("Shell_SidebarArtists"), CreateIconSource(typeof(LibraryPage), "artists"), openInNewTab);
    }

    /// <summary>
    /// Navigate to liked songs - within tab by default, new tab if openInNewTab=true
    /// </summary>
    public static void OpenLikedSongs(bool openInNewTab = false)
    {
        Navigate(typeof(LibraryPage), "likedsongs", AppLocalization.GetString("Shell_SidebarLikedSongs"), CreateIconSource(typeof(LibraryPage), "likedsongs"), openInNewTab);
    }

    public static void OpenYourEpisodes(bool openInNewTab = false)
    {
        OpenPodcasts(openInNewTab);
    }

    public static void OpenPodcasts(bool openInNewTab = false)
    {
        Navigate(typeof(LibraryPage), "podcasts", AppLocalization.GetString("Shell_SidebarPodcasts"), CreateIconSource(typeof(LibraryPage), "podcasts"), openInNewTab);
    }

    public static void OpenPodcastBrowse(bool openInNewTab = false)
    {
        OpenPodcastBrowse(new ContentNavigationParameter
        {
            Uri = PodcastBrowseViewModel.RootPodcastsUri,
            Title = "Podcasts",
            Subtitle = "Browse shows, charts, and categories."
        }, openInNewTab);
    }

    public static void OpenPodcastBrowse(ContentNavigationParameter parameter, bool openInNewTab = false)
    {
        var header = string.IsNullOrWhiteSpace(parameter.Title)
            ? "Podcasts"
            : parameter.Title!;

        Navigate(typeof(PodcastBrowsePage), parameter, header, CreateIconSource(typeof(PodcastBrowsePage), parameter), openInNewTab);
    }

    /// <summary>
    /// Generic destination for any <c>spotify:page:</c> URI surfaced by Pathfinder
    /// (Music / Podcasts / Audiobooks / Pop / Hip-Hop / Mood / etc.). Renders a
    /// HomePage-style hero band + section shelves driven by the <c>browsePage</c>
    /// persistedQuery. Sub-page tile clicks recurse here.
    /// </summary>
    public static void OpenBrowsePage(ContentNavigationParameter parameter, bool openInNewTab = false)
    {
        var header = string.IsNullOrWhiteSpace(parameter.Title)
            ? "Browse"
            : parameter.Title!;

        Navigate(typeof(BrowsePage), parameter, header, CreateIconSource(typeof(BrowsePage), parameter), openInNewTab);
    }

    /// <summary>
    /// Open a podcast show as a standalone show page. Use this for browse/search
    /// surfaces where the user is exploring, not drilling into their library.
    /// </summary>
    public static void OpenShowPage(ContentNavigationParameter parameter, bool openInNewTab = false)
    {
        var header = string.IsNullOrWhiteSpace(parameter.Title)
            ? "Show"
            : parameter.Title!;

        Navigate(typeof(ShowPage), parameter, header, CreateIconSource(typeof(ShowPage), parameter), openInNewTab);
    }

    public static void OpenShowPage(
        string showUri,
        string? title = null,
        string? subtitle = null,
        string? imageUrl = null,
        bool openInNewTab = false)
    {
        if (string.IsNullOrWhiteSpace(showUri)) return;
        if (showUri.StartsWith("spotify:episode:", StringComparison.Ordinal))
        {
            OpenEpisodePage(showUri, title, openInNewTab: openInNewTab);
            return;
        }

        OpenShowPage(new ContentNavigationParameter
        {
            Uri = showUri,
            Title = title,
            Subtitle = subtitle,
            ImageUrl = imageUrl
        }, openInNewTab);
    }

    /// <summary>
    /// Open a podcast episode detail page. The
    /// <paramref name="showUri"/> / <paramref name="showTitle"/> / <paramref name="showImageUrl"/>
    /// fields seed the breadcrumb and hero before the network resolves; pass null
    /// when the call site doesn't know the parent (Home cards, search hits) and
    /// the page will derive them from the episode metadata. Local episode URIs
    /// (<c>wavee:local:episode:</c>) are a no-op for now.
    /// </summary>
    public static void OpenEpisodePage(
        string episodeUri,
        string? episodeTitle = null,
        string? episodeImageUrl = null,
        string? showUri = null,
        string? showTitle = null,
        string? showImageUrl = null,
        bool openInNewTab = false)
    {
        if (string.IsNullOrWhiteSpace(episodeUri)) return;
        if (Wavee.Core.PlayableUri.IsLocal(episodeUri)) return;

        var parameter = new EpisodeNavigationParameter
        {
            EpisodeUri = episodeUri,
            EpisodeTitle = episodeTitle,
            EpisodeImageUrl = episodeImageUrl,
            ShowUri = showUri,
            ShowTitle = showTitle,
            ShowImageUrl = showImageUrl,
        };
        var header = string.IsNullOrWhiteSpace(episodeTitle) ? "Episode" : episodeTitle!;

        Navigate(typeof(EpisodePage), parameter, header, CreateIconSource(typeof(EpisodePage), parameter), openInNewTab);
    }

    /// <summary>
    /// Start playback for a single podcast episode by URI. Uses the same
    /// queue-loading path as the in-app library views so the episode lands in
    /// the player with a single-item queue and a podcast-context.
    /// </summary>
    public static void PlayEpisode(string episodeUri)
    {
        if (string.IsNullOrWhiteSpace(episodeUri)) return;

        try
        {
            var playback = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<Wavee.UI.Contracts.IPlaybackStateService>();
            if (playback is null)
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationHelpers] PlayEpisode: IPlaybackStateService unavailable for {episodeUri}");
                return;
            }

            var item = new Wavee.UI.Models.QueueItem
            {
                TrackId = episodeUri,
                Title = "",
                ArtistName = "",
                AlbumArt = null,
                DurationMs = 0,
                IsUserQueued = false,
            };

            var context = new Wavee.UI.Models.PlaybackContextInfo
            {
                ContextUri = episodeUri,
                Type = Wavee.UI.Models.PlaybackContextType.Episode,
                Name = "",
                ImageUrl = null,
            };

            playback.LoadQueue(new[] { item }, context, 0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NavigationHelpers] PlayEpisode failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Navigate to a user profile. Pass <paramref name="parameter"/>=null for the
    /// authenticated user's own profile, or a <see cref="ContentNavigationParameter"/>
    /// carrying a <c>spotify:user:{id}</c> URI for someone else.
    /// </summary>
    public static void OpenProfile(ContentNavigationParameter? parameter = null, string? title = null, bool openInNewTab = false)
    {
        var header = parameter?.Title ?? title ?? "Profile";
        Navigate(typeof(ProfilePage), parameter, header, CreateIconSource(typeof(ProfilePage), parameter), openInNewTab);
    }

    /// <summary>
    /// Open create playlist/folder page - always opens in new tab
    /// </summary>
    public static void OpenCreatePlaylist(bool isFolder = false, IReadOnlyList<string>? trackIds = null)
    {
        var header = isFolder ? "New Folder" : "New Playlist";
        var glyph = isFolder ? "\uE8F4" : "\uE93F";
        var fontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["MediaPlayerIconsFontFamily"];

        var iconSource = new FontIconSource
        {
            FontFamily = fontFamily,
            Glyph = glyph
        };

        var parameter = new CreatePlaylistParameter
        {
            IsFolder = isFolder,
            TrackIds = trackIds
        };

        Navigate(typeof(CreatePlaylistPage), parameter, header, iconSource, openInNewTab: true);
    }

    public static void OpenDebug(bool openInNewTab = false)
    {
        Navigate(typeof(DebugPage), null, "Debug", CreateIconSource(typeof(DebugPage), null), openInNewTab);
    }

    public static void OpenSettings(bool openInNewTab = false)
    {
        Navigate(typeof(SettingsPage), null, "Settings", CreateIconSource(typeof(SettingsPage), null), openInNewTab);
    }

    /// <summary>
    /// Open SettingsPage and deep-link to a specific section + group filter — used by
    /// the omnibar's Settings search results so the destination page reuses the existing
    /// in-page <c>NavigateToSearchEntry</c> path.
    /// </summary>
    public static void OpenSettings(SettingsNavigationParameter parameter, bool openInNewTab = false)
    {
        Navigate(typeof(SettingsPage), parameter, "Settings", CreateIconSource(typeof(SettingsPage), null), openInNewTab);
    }

    public static void OpenFeedback(bool openInNewTab = false)
    {
        Navigate(typeof(FeedbackPage), null, "Feedback", CreateIconSource(typeof(FeedbackPage), null), openInNewTab);
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

        FocusMainWindow();

        // Update navigation state after frame has navigated (deferred to next UI tick)
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            if (_shellViewModel != null)
            {
                _shellViewModel.UpdateNavigationState();

                // Sync sidebar selection to the target page:
                //   - PlaylistPage: resolve the parameter (playlist id or URI) to the matching
                //     sidebar row. Clears if the playlist isn't in the user's library.
                //   - LibraryPage: leave alone. LibraryPage.OnSelectedItemChanged keeps
                //     Albums/Artists/LikedSongs/Playlists sub-tab selection in sync.
                //   - Everything else (Search, Concert, Profile, Settings, …): clear.
                if (pageType == typeof(PlaylistPage))
                {
                    _shellViewModel.SyncSidebarSelectionToPlaylist(parameter);
                }
                else if (pageType == typeof(PodcastBrowsePage))
                {
                    _shellViewModel.SyncSidebarSelectionToTag("PodcastBrowse");
                }
                else if (pageType != typeof(LibraryPage))
                {
                    _shellViewModel.SelectedSidebarItem = null;
                }
            }
        });
    }

    private static void FocusMainWindow()
    {
        try
        {
            var window = global::Wavee.UI.WinUI.MainWindow.Instance;
            if (window.AppWindow?.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();

            window.Activate();
        }
        catch
        {
            // Navigation should still succeed if window activation is unavailable.
        }
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

        var suppressTransition =
            pageType == typeof(AlbumPage)
                && ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.AlbumArt)
            || pageType == typeof(PlaylistPage)
                && ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.PlaylistArt)
            || pageType == typeof(ShowPage)
                && ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.PodcastArt)
            || pageType == typeof(EpisodePage)
                && ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.PodcastEpisodeArt);

        var currentTab = ShellViewModel.TabInstances[currentIndex];
        currentTab.Header = header;
        currentTab.IconSource = icon;
        currentTab.ToolTipText = header;
        currentTab.Navigate(pageType, parameter, suppressTransition);
    }

    private static void AddNewTab(Type pageType, object? parameter, string header, IconSource icon)
    {
        var tab = CreateTab(pageType, parameter, header, icon);
        ShellViewModel.TabInstances.Add(tab);
        _shellViewModel!.SelectTab(ShellViewModel.TabInstances.Count - 1);
    }

    public static TabBarItem CreateTab(
        Type pageType,
        object? parameter,
        string header,
        IconSource? icon = null,
        bool isPinned = false,
        bool isCompact = false)
    {
        var tab = new TabBarItem
        {
            Header = header,
            IconSource = icon ?? CreateIconSource(pageType, parameter),
            ToolTipText = header,
            IsPinned = isPinned,
            IsCompact = isCompact
        };

        tab.Navigate(pageType, parameter);
        return tab;
    }

    public static IconSource CreateIconSource(Type pageType, object? parameter)
    {
        if (pageType == typeof(HomePage))
            return new SymbolIconSource { Symbol = Symbol.Home };

        if (pageType == typeof(StartPage))
            return new SymbolIconSource { Symbol = Symbol.Add };

        if (pageType == typeof(SearchPage))
            return new SymbolIconSource { Symbol = Symbol.Find };

        if (pageType == typeof(ArtistPage) || pageType == typeof(ProfilePage))
            return new SymbolIconSource { Symbol = Symbol.Contact };

        if (pageType == typeof(AlbumPage) || pageType == typeof(ConcertPage))
            return new SymbolIconSource { Symbol = Symbol.Audio };

        if (pageType == typeof(ShowPage))
            return new FontIconSource { Glyph = "" };

        if (pageType == typeof(EpisodePage))
            return new FontIconSource { Glyph = "" };

        if (pageType == typeof(PodcastBrowsePage))
            return new FontIconSource { Glyph = "\uEC05" };

        if (pageType == typeof(BrowsePage))
            return new SymbolIconSource { Symbol = Symbol.Globe };

        if (pageType == typeof(LocalLibraryPage))
            return new SymbolIconSource { Symbol = Symbol.Folder };

        if (pageType == typeof(VideoPlayerPage))
            return new SymbolIconSource { Symbol = Symbol.Video };

        if (pageType == typeof(PlaylistPage))
            return new SymbolIconSource { Symbol = Symbol.MusicInfo };

        if (pageType == typeof(SettingsPage))
            return new SymbolIconSource { Symbol = Symbol.Setting };

        if (pageType == typeof(DebugPage))
            return new SymbolIconSource { Symbol = Symbol.Repair };

        if (pageType == typeof(FeedbackPage))
            return new SymbolIconSource { Symbol = Symbol.Comment };

        if (pageType == typeof(LibraryPage))
        {
            return parameter switch
            {
                "albums" => new SymbolIconSource { Symbol = Symbol.Audio },
                "artists" => new SymbolIconSource { Symbol = Symbol.Contact },
                "likedsongs" => new SymbolIconSource { Symbol = Symbol.Like },
                "podcasts" or "yourepisodes" => new FontIconSource { Glyph = "\uEC05" },
                _ => new SymbolIconSource { Symbol = Symbol.Library }
            };
        }

        return new SymbolIconSource { Symbol = Symbol.Document };
    }

    public static string GetDefaultHeader(Type pageType, object? parameter)
    {
        if (pageType == typeof(HomePage))
            return "Home";

        if (pageType == typeof(StartPage))
            return "New Tab";

        if (pageType == typeof(SearchPage))
            return "Search";

        if (pageType == typeof(ProfilePage))
            return (parameter as ContentNavigationParameter)?.Title ?? "Profile";

        if (pageType == typeof(SettingsPage))
            return "Settings";

        if (pageType == typeof(DebugPage))
            return "Debug";

        if (pageType == typeof(FeedbackPage))
            return "Feedback";

        if (pageType == typeof(PodcastBrowsePage))
            return (parameter as ContentNavigationParameter)?.Title ?? "Podcasts";

        if (pageType == typeof(BrowsePage))
            return (parameter as ContentNavigationParameter)?.Title ?? "Browse";

        if (pageType == typeof(LibraryPage))
        {
            return parameter switch
            {
                "albums" => AppLocalization.GetString("Shell_SidebarAlbums"),
                "artists" => AppLocalization.GetString("Shell_SidebarArtists"),
                "likedsongs" => AppLocalization.GetString("Shell_SidebarLikedSongs"),
                "podcasts" or "yourepisodes" => AppLocalization.GetString("Shell_SidebarPodcasts"),
                _ => AppLocalization.GetString("Shell_SidebarYourLibrary")
            };
        }

        return pageType.Name.Replace("Page", "");
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
