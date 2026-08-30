using FluentGpu.Localization;
using Wavee.Features.Browse;
using Xunit;

namespace Wavee.Tests
{
    // Pins the product rule for DrillTrail.Of: the breadcrumb is a PURE FUNCTION of (routeName, routeArg, liveTitle) —
    // never of navigation history (no "how did I get here" state leaks in), roots never grow a trail, and a trail
    // whose current-page label would be blank renders as nothing rather than a lone chevron. Also pins the one
    // decision that makes the class worth having: a Home-minted section (HomeSectionRoutes) trails back to Home,
    // but a BROWSE section/category (BrowseSectionRoutes / BrowseRoutes) trails back to Browse — even though Home
    // can be the shortcut that opened it, Home was never its ancestor in the IA.
    public class DrillTrailTests
    {
        [Theory]
        [InlineData("home")]
        [InlineData("search")]
        [InlineData("browse")]
        [InlineData("recents")]
        [InlineData("history")]
        [InlineData("albums")]
        [InlineData("artists")]
        [InlineData("podcasts")]
        [InlineData("liked")]
        [InlineData("local")]
        [InlineData("settings")]
        [InlineData("home-customize")]
        [InlineData("sidebar-customize")]
        public void Roots_HaveNoTrail(string route)
        {
            Assert.Empty(DrillTrail.Of(route, null, null));
        }

        // Even a root that WOULD carry a display title (e.g. a search query) stays trail-less: a root is a root
        // regardless of whether it has a label to show — the trail answers "what is above this", and nothing is.
        [Fact]
        public void Root_WithALiveTitle_StillHasNoTrail()
        {
            Assert.Empty(DrillTrail.Of("search", null, "daft punk"));
        }

        [Fact]
        public void HomeSection_TrailsBackToHome()
        {
            var route = HomeSectionRoutes.Page("spotify:section:1");
            var crumbs = DrillTrail.Of(route, null, "Made For You");

            Assert.Equal(2, crumbs.Count);
            Assert.Equal(Loc.Get(Strings.Nav.Home), crumbs[0].Label);
            Assert.Equal("home", crumbs[0].RouteName);
            Assert.Null(crumbs[0].RouteArg);
            Assert.Equal("Made For You", crumbs[1].Label);
            Assert.Null(crumbs[1].RouteName);
        }

        // The real scenario this class exists for: a Home Charts Fold is rendered through a browse-section: route
        // (BrowseSectionRoutes), not a home-section: one — even though the tile that opened it lived on the Home
        // page. Its trail must say Browse, not Home: Home was a shortcut into Browse's IA, never an ancestor of it.
        [Fact]
        public void HomeChartsFold_DrillingIntoABrowseSection_TrailsBackToBrowseNotHome()
        {
            var route = BrowseSectionRoutes.Page("spotify:section:charts");
            var crumbs = DrillTrail.Of(route, null, "Top 50 - Global");

            Assert.Equal(2, crumbs.Count);
            Assert.Equal(Loc.Get(Strings.Browse.HomeTitle), crumbs[0].Label);
            Assert.Equal(BrowseRoutes.Home, crumbs[0].RouteName);
            Assert.Equal("Top 50 - Global", crumbs[1].Label);
            Assert.Null(crumbs[1].RouteName);
            Assert.DoesNotContain(crumbs, c => c.Label == Loc.Get(Strings.Nav.Home));
        }

        [Fact]
        public void BrowseCategory_TrailsBackToBrowse()
        {
            var route = BrowseRoutes.Page("spotify:page:music");
            var crumbs = DrillTrail.Of(route, null, "Music");

            Assert.Equal(2, crumbs.Count);
            Assert.Equal(Loc.Get(Strings.Browse.HomeTitle), crumbs[0].Label);
            Assert.Equal(BrowseRoutes.Home, crumbs[0].RouteName);
            Assert.Equal("Music", crumbs[1].Label);
            Assert.Null(crumbs[1].RouteName);
        }

        [Fact]
        public void LiveTitle_WinsOverRouteArg()
        {
            var route = HomeSectionRoutes.Page("spotify:section:1");
            var crumbs = DrillTrail.Of(route, "stale arg title", "Fresh Live Title");

            Assert.Equal("Fresh Live Title", crumbs[^1].Label);
        }

        [Fact]
        public void WhitespaceOnlyLiveTitle_FallsBackToTrimmedRouteArg()
        {
            var route = HomeSectionRoutes.Page("spotify:section:1");
            var crumbs = DrillTrail.Of(route, "  Made For You  ", "   ");

            Assert.Equal("Made For You", crumbs[^1].Label);
        }

        [Fact]
        public void BothLiveTitleAndRouteArgBlank_ProducesEmptyTrail_NotALoneChevron()
        {
            var route = HomeSectionRoutes.Page("spotify:section:1");

            Assert.Empty(DrillTrail.Of(route, "   ", "  "));
            Assert.Empty(DrillTrail.Of(route, null, null));
        }

        [Fact]
        public void LastCrumb_IsNeverClickable()
        {
            var homeSection = DrillTrail.Of(HomeSectionRoutes.Page("spotify:section:1"), null, "T");
            var browseSection = DrillTrail.Of(BrowseSectionRoutes.Page("spotify:section:1"), null, "T");
            var browseCategory = DrillTrail.Of(BrowseRoutes.Page("spotify:page:x"), null, "T");

            Assert.Null(homeSection[^1].RouteName);
            Assert.Null(browseSection[^1].RouteName);
            Assert.Null(browseCategory[^1].RouteName);
        }

        [Fact]
        public void UnknownRoute_HasNoTrail()
        {
            Assert.Empty(DrillTrail.Of("some-unregistered-route", null, "Whatever"));
        }

        // Defect G: Browse › Netflix → "New on Netflix" must keep the category parent, not collapse to Browse › section.
        [Fact]
        public void BrowseSection_FromCategory_KeepsCategoryCrumb()
        {
            var category = BrowseRoutes.Page("spotify:page:netflix");
            var section = BrowseSectionRoutes.Page("spotify:section:new-on-netflix");
            var origin = new NavOrigin("Netflix", category, "Netflix");
            var crumbs = DrillTrail.Of(section, null, "New on Netflix", origin);

            Assert.Equal(3, crumbs.Count);
            Assert.Equal(Loc.Get(Strings.Browse.HomeTitle), crumbs[0].Label);
            Assert.Equal(BrowseRoutes.Home, crumbs[0].RouteName);
            Assert.Equal("Netflix", crumbs[1].Label);
            Assert.Equal(category, crumbs[1].RouteName);
            Assert.Equal("New on Netflix", crumbs[2].Label);
            Assert.Null(crumbs[2].RouteName);
        }

        // Defect G: a Pop genre page opened from search results "pop" must trail from the query, not invent Browse.
        [Fact]
        public void GenreFromSearch_TrailsFromQueryNotBrowse()
        {
            var origin = new NavOrigin("pop", "search", "pop");
            var crumbs = DrillTrail.Of(BrowseRoutes.Page("spotify:page:pop"), null, "Pop", origin);

            Assert.Equal(2, crumbs.Count);
            Assert.Equal("pop", crumbs[0].Label);
            Assert.Equal("search", crumbs[0].RouteName);
            Assert.Equal("pop", crumbs[0].RouteArg);
            Assert.Equal("Pop", crumbs[1].Label);
            Assert.Null(crumbs[1].RouteName);
            Assert.DoesNotContain(crumbs, c => c.Label == Loc.Get(Strings.Browse.HomeTitle));
        }

        [Fact]
        public void OriginNull_PreservesEveryIaArm()
        {
            Assert.Equal(2, DrillTrail.Of(HomeSectionRoutes.Page("spotify:section:1"), null, "Made For You").Count);
            Assert.Equal(2, DrillTrail.Of(BrowseSectionRoutes.Page("spotify:section:charts"), null, "Top 50").Count);
            Assert.Equal(2, DrillTrail.Of(BrowseRoutes.Page("spotify:page:music"), null, "Music").Count);
        }

        [Fact]
        public void ConcertsHub_TrailsToBrowse()
        {
            var crumbs = DrillTrail.Of(Wavee.Features.Concerts.ConcertRoutes.Hub, null, null);
            Assert.Equal(2, crumbs.Count);
            Assert.Equal(Loc.Get(Strings.Browse.HomeTitle), crumbs[0].Label);
            Assert.Equal(BrowseRoutes.Home, crumbs[0].RouteName);
            Assert.Equal(Loc.Get(Strings.Concerts.Title), crumbs[1].Label);
            Assert.Null(crumbs[1].RouteName);
        }
    }
}
