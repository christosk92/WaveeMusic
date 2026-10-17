// ── Wavee.Tests/PageChromeRowTests.cs — page chrome row 2, the band's lit destination and the page-scroll rule (P5 ZK) ────────
//
// The core half of the page-chrome channel (Shell/Shell.PageChrome.cs) as pure facts:
//
//   ROW 2 OF A LIBRARY ROUTE IS ANSWERED FROM THE ROUTE. Every library kind answers the same five views, in the band's order (liked,
//   albums, artists, podcasts, audiobooks), with exactly its own view lit, before any page mounts; every other route answers none.
//
//   THE PAGE'S OWN VIEWS WIN, AND WITHDRAWING THEM GIVES THE ROUTE'S BACK. The merge is the page's views when it published any, else
//   the route's; the actions and the backdrop are only ever the page's.
//
//   THE BAND LIGHTS BY ROUTE KIND, AND A DETAIL PAGE LIGHTS NOTHING. Concerts live under Browse (ZC1).
//
//   THE SCROLL DIRECTION HAS A DEAD ZONE. A jitter smaller than it never flips the direction; the top is always "none".

using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PageChromeRowTests
{
    static readonly Shell.RouteKind[] s_library =
        [Shell.RouteKind.Liked, Shell.RouteKind.LibraryAlbums, Shell.RouteKind.LibraryArtists, Shell.RouteKind.LibraryPodcasts, Shell.RouteKind.LibraryAudiobooks];

    static string NameOf(Shell.RouteKind kind) => Shell.NameOf(new Shell.Route(kind));

    [Theory]
    [InlineData(Shell.RouteKind.Liked)]
    [InlineData(Shell.RouteKind.LibraryAlbums)]
    [InlineData(Shell.RouteKind.LibraryArtists)]
    [InlineData(Shell.RouteKind.LibraryPodcasts)]
    [InlineData(Shell.RouteKind.LibraryAudiobooks)]
    public void Every_library_route_answers_the_five_kinds_in_the_bands_order_with_itself_lit(Shell.RouteKind kind)
    {
        var views = Shell.PageChromeChannel.RouteViews(new Shell.Route(kind));

        Assert.Equal(s_library.Select(NameOf).ToArray(), views.Select(v => v.Id).ToArray());
        var lit = Assert.Single(views, v => v.Current);
        Assert.Equal(NameOf(kind), lit.Id);
        Assert.All(views, v => Assert.False(string.IsNullOrEmpty(v.Label)));
        Assert.All(views, v => Assert.Null(v.Count));
        Assert.True(User.IsLibraryChromeKind(kind));
    }

    [Theory]
    [InlineData(Shell.RouteKind.Home)]
    [InlineData(Shell.RouteKind.Recents)]
    [InlineData(Shell.RouteKind.Settings)]
    [InlineData(Shell.RouteKind.Search)]
    public void Other_routes_answer_no_route_level_views(Shell.RouteKind kind)
    {
        Assert.Empty(Shell.PageChromeChannel.RouteViews(new Shell.Route(kind)));
        Assert.False(User.IsLibraryChromeKind(kind));
    }

    [Fact]
    public void A_library_routes_chrome_carries_its_row_and_its_own_title_as_the_one_crumb()
    {
        var route = new Shell.Route(Shell.RouteKind.LibraryPodcasts, Tab: 9101);
        var chrome = Shell.PageChromeChannel.ChromeOf(route);

        Assert.Equal(Shell.Dest(route).Title, chrome.Title);
        var crumb = Assert.Single(chrome.Crumbs);
        Assert.Equal(chrome.Title, crumb.Label);
        Assert.Null(crumb.Go);
        Assert.Equal(5, chrome.Views.Count);
        Assert.Equal(NameOf(Shell.RouteKind.LibraryPodcasts), chrome.CurrentView?.Id);
        Assert.Empty(chrome.Actions);
        Assert.True(chrome.Backdrop.IsNone);
    }

    [Fact]
    public void The_pages_views_win_its_actions_and_backdrop_ride_along_and_withdrawing_brings_the_routes_row_back()
    {
        var route = new Shell.Route(Shell.RouteKind.LibraryAlbums, Tab: 9102);
        var tab = new PageView("tab.one", "One", true, static () => { });
        var play = new PageAction("play", "Play", static () => { }, PageActionKind.Play);
        var backdrop = new PageBackdrop("https://i.example/bleed.jpg");
        try
        {
            Assert.True(Shell.PageChromeChannel.Publish(route, new PageChromeRow([tab], [play], backdrop)));
            var chrome = Shell.PageChromeChannel.ChromeOf(route);
            Assert.Same(tab, Assert.Single(chrome.Views));
            Assert.Same(play, Assert.Single(chrome.Actions));
            Assert.Same(backdrop, chrome.Backdrop);

            // Actions alone: the route's views stand beside the page's actions.
            Assert.True(Shell.PageChromeChannel.Publish(route, new PageChromeRow([], [play])));
            chrome = Shell.PageChromeChannel.ChromeOf(route);
            Assert.Equal(5, chrome.Views.Count);
            Assert.Same(play, Assert.Single(chrome.Actions));
            Assert.True(chrome.Backdrop.IsNone);
        }
        finally
        {
            Shell.PageChromeChannel.Publish(route, null);
        }
        var back = Shell.PageChromeChannel.ChromeOf(route);
        Assert.Equal(5, back.Views.Count);
        Assert.Empty(back.Actions);
        // Withdrawing what is not there changes nothing.
        Assert.False(Shell.PageChromeChannel.Publish(route, PageChromeRow.Empty));
    }

    [Fact]
    public void A_publication_is_kept_per_route_and_tab()
    {
        var a = new Shell.Route(Shell.RouteKind.LibraryArtists, Tab: 9103);
        var b = a with { Tab = 9104 };
        var row = new PageChromeRow([new PageView("x", "X", true, static () => { })], []);
        try
        {
            Shell.PageChromeChannel.Publish(a, row);
            Assert.Same(row, Shell.PageChromeChannel.RowFor(a));
            Assert.Null(Shell.PageChromeChannel.RowFor(b));
        }
        finally
        {
            Shell.PageChromeChannel.Publish(a, null);
        }
    }

    [Fact]
    public void A_sink_publishes_under_its_route_and_the_none_sink_drops_everything()
    {
        var route = new Shell.Route(Shell.RouteKind.Liked, Tab: 9105);
        var sink = Shell.PageChromeChannel.SinkFor(route);
        var row = new PageChromeRow([], [new PageAction("shuffle", "Shuffle", static () => { }, PageActionKind.Shuffle)]);
        try
        {
            sink.Publish(row);
            Assert.Same(row, Shell.PageChromeChannel.RowFor(route));
            sink.Publish(null);
            Assert.Null(Shell.PageChromeChannel.RowFor(route));
            PageChromeSink.None.Publish(row);   // goes nowhere, never throws
            Assert.Null(Shell.PageChromeChannel.RowFor(route));
        }
        finally
        {
            Shell.PageChromeChannel.Publish(route, null);
        }
    }

    [Theory]
    [InlineData(Shell.RouteKind.Home, Shell.BandRules.Home)]
    [InlineData(Shell.RouteKind.HomeSection, Shell.BandRules.Home)]
    [InlineData(Shell.RouteKind.Browse, Shell.BandRules.Browse)]
    [InlineData(Shell.RouteKind.BrowseCategory, Shell.BandRules.Browse)]
    [InlineData(Shell.RouteKind.Concerts, Shell.BandRules.Browse)]
    [InlineData(Shell.RouteKind.Concert, Shell.BandRules.Browse)]
    [InlineData(Shell.RouteKind.Liked, Shell.BandRules.Library)]
    [InlineData(Shell.RouteKind.LibraryAudiobooks, Shell.BandRules.Library)]
    [InlineData(Shell.RouteKind.Recents, Shell.BandRules.Recents)]
    [InlineData(Shell.RouteKind.Album, -1)]
    [InlineData(Shell.RouteKind.Artist, -1)]
    [InlineData(Shell.RouteKind.Settings, -1)]
    [InlineData(Shell.RouteKind.Search, -1)]
    public void The_band_lights_a_destination_by_route_kind(Shell.RouteKind kind, int expected)
        => Assert.Equal(expected, Shell.BandRules.IndexOf(kind));

    [Fact]
    public void The_scroll_direction_commits_past_the_dead_zone_and_jitter_never_flips_it()
    {
        float dz = Shell.PageScrollRules.DeadZone;
        var s = Shell.PageScrollRules.State.Top;

        s = Shell.PageScrollRules.Next(in s, dz / 2f);
        Assert.Equal(PageScrollDirection.None, s.Scroll.Direction);            // not yet past the dead zone
        s = Shell.PageScrollRules.Next(in s, 200f);
        Assert.Equal(PageScrollDirection.Down, s.Scroll.Direction);
        Assert.Equal(200f, s.Scroll.Offset);
        s = Shell.PageScrollRules.Next(in s, 200f - dz / 2f);
        Assert.Equal(PageScrollDirection.Down, s.Scroll.Direction);            // jitter back: still down
        s = Shell.PageScrollRules.Next(in s, 200f - dz);
        Assert.Equal(PageScrollDirection.Up, s.Scroll.Direction);              // a dead zone back: up
        s = Shell.PageScrollRules.Next(in s, 200f - dz + dz / 2f);
        Assert.Equal(PageScrollDirection.Up, s.Scroll.Direction);              // jitter forward: still up
        s = Shell.PageScrollRules.Next(in s, 300f);
        Assert.Equal(PageScrollDirection.Down, s.Scroll.Direction);
        s = Shell.PageScrollRules.Next(in s, 0f);
        Assert.Equal(PageScroll.Top, s.Scroll);                                 // the top is always "none"
        s = Shell.PageScrollRules.Next(in s, float.NaN);
        Assert.Equal(PageScroll.Top, s.Scroll);
    }
}
