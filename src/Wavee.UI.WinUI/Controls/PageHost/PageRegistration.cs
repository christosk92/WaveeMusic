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
        // `pinned: true` ⇒ created once per tab and reused for the tab's lifetime,
        // never evicted/recreated mid-session (see PageRegistry.IsPinned). Applied
        // to the frequently-visited and expensive-to-build content pages so heavy
        // browsing stops re-paying `new Page()` + InitializeComponent + VM-init —
        // the cause of progressive nav slowdown. Off-screen pinned pages still
        // hibernate + shed GPU surfaces, so the standing cost is just a managed
        // tree. Rare / one-shot / utility pages stay on LRU eviction. To pin or
        // unpin a page, flip the flag on its line — one place.
        Register(() => new ShellPage(), nameof(ShellPage));

        // Main content pages
        Register(() => new HomePage(), nameof(HomePage), pinned: true);
        Register(() => new StartPage(), nameof(StartPage));
        Register(() => new LibraryPage(), nameof(LibraryPage), pinned: true);
        Register(() => new SearchPage(), nameof(SearchPage), pinned: true);
        Register(() => new BrowsePage(), nameof(BrowsePage), pinned: true);

        // Detail pages
        Register(() => new AlbumPage(), nameof(AlbumPage), pinned: true);
        Register(() => new PlaylistPage(), nameof(PlaylistPage), pinned: true);
        Register(() => new ArtistPage(), nameof(ArtistPage), pinned: true);
        Register(() => new ArtistDiscographyPage(), nameof(ArtistDiscographyPage), pinned: true);
        Register(() => new ShowPage(), nameof(ShowPage), pinned: true);
        Register(() => new EpisodePage(), nameof(EpisodePage), pinned: true);
        Register(() => new ConcertPage(), nameof(ConcertPage), pinned: true);
        Register(() => new ProfilePage(), nameof(ProfilePage));

        // Podcast
        Register(() => new PodcastBrowsePage(), nameof(PodcastBrowsePage), pinned: true);

        // Composition / wizard
        Register(() => new CreatePlaylistPage(), nameof(CreatePlaylistPage));

        // Media
        Register(() => new VideoPlayerPage(), nameof(VideoPlayerPage));

        // App utility
        Register(() => new CrashRecoveryPage(), nameof(CrashRecoveryPage));
        Register(() => new SettingsPage(), nameof(SettingsPage));
        Register(() => new DebugPage(), nameof(DebugPage));
        Register(() => new FeedbackPage(), nameof(FeedbackPage));

        // Local-library tree — pin the main hub; deep detail pages stay on LRU.
        Register(() => new LocalLibraryPage(), nameof(LocalLibraryPage), pinned: true);
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

    private static void Register<TPage>(System.Func<TPage> factory, string key, bool pinned = false)
        where TPage : Microsoft.UI.Xaml.Controls.UserControl
    {
        PageRegistry.Register(factory, pinned);
        PageTypeRegistry.Register(key, typeof(TPage));
    }
}
