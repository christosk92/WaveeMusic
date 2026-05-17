using Wavee.UI.WinUI.Views;
using Wavee.UI.WinUI.Views.Local;

namespace Wavee.UI.WinUI.Controls.PageHost;

/// <summary>
/// Central registration of all <c>Page</c> factories. Called once during app
/// startup before any <see cref="PageHost.Navigate"/> fires. Adding a new page
/// to the app means adding one line here.
/// </summary>
internal static class PageRegistration
{
    public static void RegisterAll()
    {
        PageRegistry.Register(() => new ShellPage());

        // Main content pages
        PageRegistry.Register(() => new HomePage());
        PageRegistry.Register(() => new StartPage());
        PageRegistry.Register(() => new LibraryPage());
        PageRegistry.Register(() => new SearchPage());
        PageRegistry.Register(() => new BrowsePage());

        // Detail pages
        PageRegistry.Register(() => new AlbumPage());
        PageRegistry.Register(() => new PlaylistPage());
        PageRegistry.Register(() => new ArtistPage());
        PageRegistry.Register(() => new ArtistDiscographyPage());
        PageRegistry.Register(() => new ShowPage());
        PageRegistry.Register(() => new EpisodePage());
        PageRegistry.Register(() => new ConcertPage());
        PageRegistry.Register(() => new ProfilePage());

        // Podcast
        PageRegistry.Register(() => new PodcastBrowsePage());

        // Composition / wizard
        PageRegistry.Register(() => new CreatePlaylistPage());

        // Media
        PageRegistry.Register(() => new VideoPlayerPage());

        // App utility
        PageRegistry.Register(() => new SettingsPage());
        PageRegistry.Register(() => new DebugPage());
        PageRegistry.Register(() => new FeedbackPage());

        // Local-library tree
        PageRegistry.Register(() => new LocalLibraryPage());
        PageRegistry.Register(() => new LocalLikedSongsPage());
        PageRegistry.Register(() => new LocalMusicPage());
        PageRegistry.Register(() => new LocalMusicVideosPage());
        PageRegistry.Register(() => new LocalShowsPage());
        PageRegistry.Register(() => new LocalShowDetailPage());
        PageRegistry.Register(() => new LocalMoviesPage());
        PageRegistry.Register(() => new LocalMovieDetailPage());
        PageRegistry.Register(() => new LocalCollectionDetailPage());
        PageRegistry.Register(() => new LocalPersonDetailPage());
        PageRegistry.Register(() => new LocalOtherPage());
    }
}
