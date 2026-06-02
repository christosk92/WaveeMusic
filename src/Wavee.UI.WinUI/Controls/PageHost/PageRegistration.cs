using Wavee.UI.WinUI.Views;
using Wavee.UI.WinUI.Views.Local;

namespace Wavee.UI.WinUI.Controls.PageHost;

/// <summary>
/// Central registration of all <c>Page</c> factories. Called once during app
/// startup before any <see cref="PageHost.Navigate"/> fires. Adding a new page
/// to the app means adding one line here.
///
/// Each page is registered twice: once with <see cref="PageRegistry"/> (the
/// factory used by PageHost.Navigate) and once with
/// <see cref="PageTypeRegistry"/> (the string-key ↔ Type lookup used by
/// TabItemParameter persistence). Skipping the PageTypeRegistry call means
/// the page cannot be restored as an active tab across an app restart — the
/// rest of the navigation surface still works.
/// </summary>
internal static class PageRegistration
{
    public static void RegisterAll()
    {
        // Files-app model: a tab keeps every page it visits resident (PageHost
        // caches by Type, so one instance per page type per tab) until the tab
        // closes. There is no proactive eviction — pages are reclaimed only on
        // tab close or under memory-budget pressure (PageHostCacheCleanupAdapter).
        Register(() => new ShellPage(), nameof(ShellPage));

        // Main content pages
        Register(() => new HomePage(), nameof(HomePage));
        Register(() => new StartPage(), nameof(StartPage));
        Register(() => new LibraryPage(), nameof(LibraryPage));
        Register(() => new SearchPage(), nameof(SearchPage));
        Register(() => new BrowsePage(), nameof(BrowsePage));

        // Detail pages
        Register(() => new AlbumPage(), nameof(AlbumPage));
        Register(() => new PlaylistPage(), nameof(PlaylistPage));
        Register(() => new ArtistPage(), nameof(ArtistPage));
        Register(() => new ArtistDiscographyPage(), nameof(ArtistDiscographyPage));
        Register(() => new ShowPage(), nameof(ShowPage));
        Register(() => new EpisodePage(), nameof(EpisodePage));
        Register(() => new ConcertPage(), nameof(ConcertPage));
        Register(() => new ProfilePage(), nameof(ProfilePage));

        // Podcast
        Register(() => new PodcastBrowsePage(), nameof(PodcastBrowsePage));

        // Composition / wizard
        Register(() => new CreatePlaylistPage(), nameof(CreatePlaylistPage));
        Register(() => new RefreshPlaylistPage(), nameof(RefreshPlaylistPage));

        // Media
        Register(() => new VideoPlayerPage(), nameof(VideoPlayerPage));

        // App utility
        Register(() => new CrashRecoveryPage(), nameof(CrashRecoveryPage));
        Register(() => new SettingsPage(), nameof(SettingsPage));
        Register(() => new DebugPage(), nameof(DebugPage));
        Register(() => new FeedbackPage(), nameof(FeedbackPage));

        // Local-library tree
        Register(() => new LocalLibraryPage(), nameof(LocalLibraryPage));
        Register(() => new LocalLikedSongsPage(), nameof(LocalLikedSongsPage));
        Register(() => new LocalMusicPage(), nameof(LocalMusicPage));
        Register(() => new LocalMusicVideosPage(), nameof(LocalMusicVideosPage));
        Register(() => new LocalShowsPage(), nameof(LocalShowsPage));
        Register(() => new LocalShowDetailPage(), nameof(LocalShowDetailPage));
        Register(() => new LocalMoviesPage(), nameof(LocalMoviesPage));
        Register(() => new LocalMovieDetailPage(), nameof(LocalMovieDetailPage));
        Register(() => new LocalCollectionDetailPage(), nameof(LocalCollectionDetailPage));
        Register(() => new LocalPersonDetailPage(), nameof(LocalPersonDetailPage));
        Register(() => new LocalOtherPage(), nameof(LocalOtherPage));
    }

    private static void Register<TPage>(System.Func<TPage> factory, string key)
        where TPage : Microsoft.UI.Xaml.Controls.UserControl
    {
        PageRegistry.Register(factory);
        PageTypeRegistry.Register(key, typeof(TPage));
    }
}
