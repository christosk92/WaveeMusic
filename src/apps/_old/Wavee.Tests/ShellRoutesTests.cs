using Xunit;

namespace Wavee.Tests
{
    // ShellRoutes.IsKnown is the shell's "is there a page behind this key" answer — the one ShellNav.Dest deliberately
    // cannot give (it labels EVERY string, falling back to "Your Library"). Three callers depend on it being closed:
    // the wavee:// deep-link intake refuses an unknown key outright, the history rows dim an entry whose destination no
    // longer exists, and ContentHost's not-found page is what a key that slips through lands on.
    public class ShellRoutesTests
    {
        [Theory]
        [InlineData("home")]
        [InlineData("browse")]
        [InlineData("search")]
        [InlineData("albums")]
        [InlineData("artists")]
        [InlineData("liked")]
        [InlineData("podcasts")]
        [InlineData("local")]
        [InlineData("history")]
        [InlineData("recents")]
        [InlineData("settings")]
        [InlineData("api-console")]
        [InlineData("playback-diagnostics")]
        [InlineData("sidebar-customize")]
        [InlineData("home-customize")]
        public void ExactRoutes_AreKnown(string key) => Assert.True(ShellRoutes.IsKnown(key));

        [Theory]
        [InlineData("album:spotify:album:1")]
        [InlineData("pl:spotify:playlist:1")]
        [InlineData("artist:spotify:artist:1")]
        [InlineData("show:spotify:show:1")]
        [InlineData("prerelease:spotify:album:1")]
        [InlineData("disco:1:spotify:artist:1")]
        [InlineData("module:wavee:module:youtube:abc")]
        [InlineData("browse:spotify:page:music")]
        [InlineData("home-section:spotify:section:1")]
        [InlineData("browse-section:spotify:section:1")]
        public void PrefixFamilies_AreKnown(string key) => Assert.True(ShellRoutes.IsKnown(key));

        // A bare prefix addresses no entity. Accepting it would open a detail page with nothing to load — exactly the
        // dead tab the deep-link check exists to prevent.
        [Theory]
        [InlineData("album:")]
        [InlineData("pl:")]
        [InlineData("artist:")]
        [InlineData("show:")]
        [InlineData("prerelease:")]
        [InlineData("disco:")]
        [InlineData("module:")]
        [InlineData("browse:")]
        [InlineData("home-section:")]
        [InlineData("browse-section:")]
        public void BarePrefixWithNoEntity_IsNotKnown(string key) => Assert.False(ShellRoutes.IsKnown(key));

        [Theory]
        [InlineData("concerts")]                       // ConcertRoutes.Hub
        [InlineData("artist-concerts:artist-1")]       // ConcertRoutes.ArtistSchedulePrefix
        [InlineData("concert:concert-1")]              // ConcertRoutes.DetailPrefix
        public void ConcertRoutes_AreKnown(string key) => Assert.True(ShellRoutes.IsKnown(key));

        // The concert family owns its own emptiness rules (ConcertRoutes.TryParse rejects a blank entity id); IsKnown
        // must inherit them rather than re-deciding with a prefix test of its own.
        [Theory]
        [InlineData("artist-concerts:")]
        [InlineData("concert:")]
        [InlineData("concert:   ")]
        public void ConcertRoutesWithNoEntity_AreNotKnown(string key) => Assert.False(ShellRoutes.IsKnown(key));

        [Theory]
        [InlineData("no-such-route")]
        [InlineData("Home")]                           // ordinal, not case-insensitive
        [InlineData("spotify:concerts")]               // a client-feature uri is not a route key
        [InlineData("albumspotify:album:1")]           // missing the ':' separator — not the album family
        [InlineData("home-section")]                   // the prefix's name without its ':' is nothing
        [InlineData("")]
        [InlineData(null)]
        public void UnknownKeys_AreNotKnown(string? key) => Assert.False(ShellRoutes.IsKnown(key));

        // The legacy Recents route persisted in older session/history files. NavRouteNormalizer rewrites it to
        // "recents", but it must not be refused before it gets there — it is a home-section key, so the family covers it.
        [Fact]
        public void LegacyRecentsRoute_IsKnown()
            => Assert.True(ShellRoutes.IsKnown(NavRouteNormalizer.LegacyRecentsRoute));

        // Every pinnable route is, by definition, a destination the sidebar offers to return to. One that IsKnown
        // rejected would render as a pinned row the history page dims and a deep link refuses.
        [Fact]
        public void EveryPinnableRoute_IsKnown()
        {
            foreach (var route in SidebarPinId.PinnableRoutes) Assert.True(ShellRoutes.IsKnown(route), route);
            foreach (var route in SidebarPinId.AlsoPinnableRoutes) Assert.True(ShellRoutes.IsKnown(route), route);
        }
    }
}
