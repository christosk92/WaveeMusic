// ── Wavee.Tests/ShellNavStyleTests.cs — the navigation style's stored mapping and the Zune pivot rules ───────────────────
//
// docs/plans/wavee/sidebar-rework-implementation.md §P10 (NAV 3). The stored id (sidebar.layout.id: 0 Classic · 1 Library ·
// 2 Zune) maps to a ShellNavStyle; Zune's pivots, sub-pivots and pin tiles are pure rules, driven here with plain values.

using System;
using System.Collections.Generic;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class ShellNavStyleTests
{
    [Theory]
    [InlineData(0, ShellNavStyle.Classic)]
    [InlineData(1, ShellNavStyle.Library)]
    [InlineData(2, ShellNavStyle.Zune)]
    [InlineData(-1, ShellNavStyle.Classic)]
    [InlineData(9, ShellNavStyle.Classic)]
    public void FromStored_MapsTheIdAndFallsBackToClassic(int stored, ShellNavStyle expected)
        => Assert.Equal(expected, ShellNavStyleRules.FromStored(stored));

    [Fact] public void LayoutOf_ZuneKeepsTheCurrentLayout()
        => Assert.Equal(SidebarLayoutId.Library, ShellNavStyleRules.LayoutOf(ShellNavStyle.Zune, SidebarLayoutId.Library));

    [Theory]
    [InlineData(1, SidebarLayoutId.Library)]
    [InlineData(0, SidebarLayoutId.Classic)]
    [InlineData(2, SidebarLayoutId.Classic)]
    [InlineData(-5, SidebarLayoutId.Classic)]
    public void PaneLayoutFromStored_OnlyOneIsLibrary(int stored, SidebarLayoutId expected)
        => Assert.Equal(expected, ShellNavStyleRules.PaneLayoutFromStored(stored));

    [Fact] public void LayoutOf_ZuneKeepsTheStoredPaneLayout()
        => Assert.Equal(SidebarLayoutId.Library, ShellNavStyleRules.LayoutOf(ShellNavStyle.Zune, ShellNavStyleRules.PaneLayoutFromStored(1)));

    [Fact] public void LastPaneKey_DefaultsToClassic()
        => Assert.Equal(0, Platform.Keys.SidebarLastPane.Default);

    [Fact] public void LayoutOf_ClassicAndLibraryAreTheirOwnLayouts()
    {
        Assert.Equal(SidebarLayoutId.Classic, ShellNavStyleRules.LayoutOf(ShellNavStyle.Classic, SidebarLayoutId.Library));
        Assert.Equal(SidebarLayoutId.Library, ShellNavStyleRules.LayoutOf(ShellNavStyle.Library, SidebarLayoutId.Classic));
    }

    [Fact] public void Of_LibraryIsLibrary()
        => Assert.Equal(ShellNavStyle.Library, ShellNavStyleRules.Of(SidebarLayoutId.Library));

    [Fact] public void HidesPane_OnlyForZune()
    {
        Assert.True(ShellNavStyleRules.HidesPane(ShellNavStyle.Zune));
        Assert.False(ShellNavStyleRules.HidesPane(ShellNavStyle.Classic));
        Assert.False(ShellNavStyleRules.HidesPane(ShellNavStyle.Library));
    }

    [Theory]
    [InlineData("home", "home")]
    [InlineData("home-section:x", "home")]
    [InlineData("browse", "browse")]
    [InlineData("browse:cat", "browse")]
    [InlineData("search", "browse")]
    [InlineData("albums", "library")]
    [InlineData("liked", "library")]
    [InlineData("local", "library")]
    [InlineData("recents", "recents")]
    [InlineData("album:spotify:album:1", null)]
    [InlineData("settings", null)]
    public void TopOf_MapsARouteToItsPivot(string route, string? expected)
        => Assert.Equal(expected, ZuneNavRules.TopOf(route));

    [Fact] public void LandingOf_LibraryLandsOnLikedSongs()
    {
        Assert.Equal("liked", ZuneNavRules.LandingOf("library"));
        Assert.Equal("home", ZuneNavRules.LandingOf("home"));
    }

    [Fact] public void ShowsPins_NeedsTheSettingAndTheMinimumBandWidth()
    {
        Assert.False(ZuneNavRules.ShowsPins(true, 719f));
        Assert.True(ZuneNavRules.ShowsPins(true, 720f));
        Assert.False(ZuneNavRules.ShowsPins(false, 1200f));
    }

    [Fact] public void ShowsPins_DecidesFromThePageColumnNotTheWindow_WhenTheRailIsInline()
    {
        // An 830 DIP window, rail closed: the band spans the window and the pins fit beside the pivots.
        Assert.True(ZuneNavRules.ShowsPins(true, Shell.FrameRules.CardWidth(830f, 0f, 0f, 0f)));
        // The same window with the 360 DIP rail inline (plus its 8 DIP gap): the band is 462 wide, so the pins give way to the pivots.
        Assert.False(ZuneNavRules.ShowsPins(true, Shell.FrameRules.CardWidth(830f, 0f, 8f, 360f)));
        // A 1200 DIP window with the rail inline keeps them (832 wide).
        Assert.True(ZuneNavRules.ShowsPins(true, Shell.FrameRules.CardWidth(1200f, 0f, 8f, 360f)));
    }

    [Fact] public void PinsTakeRowWidth_OnlyAGroupThatIsStayingTakesWidth()
    {
        Assert.True(ZuneNavRules.PinsTakeRowWidth(target: true, mounted: true));
        Assert.False(ZuneNavRules.PinsTakeRowWidth(target: false, mounted: true));   // fading out: out of flow, the strip keeps the whole band
        Assert.False(ZuneNavRules.PinsTakeRowWidth(target: true, mounted: false));
        Assert.False(ZuneNavRules.PinsTakeRowWidth(target: false, mounted: false));
    }

    [Fact] public void PinsMounted_WaitsForTheBandToSettleBeforeMountingAndBeforeDropping()
    {
        // Widening (target on): not mounted yet while the band still eases; mounted once it has settled; stays mounted after.
        Assert.False(ZuneNavRules.PinsMounted(target: true, mounted: false, settled: false));
        Assert.True(ZuneNavRules.PinsMounted(target: true, mounted: false, settled: true));
        Assert.True(ZuneNavRules.PinsMounted(target: true, mounted: true, settled: false));
        // Narrowing (target off): the mounted group stays (faded by opacity) until the band has settled, then it is dropped.
        Assert.True(ZuneNavRules.PinsMounted(target: false, mounted: true, settled: false));
        Assert.False(ZuneNavRules.PinsMounted(target: false, mounted: true, settled: true));
        Assert.False(ZuneNavRules.PinsMounted(target: false, mounted: false, settled: false));
        Assert.False(ZuneNavRules.PinsMounted(target: false, mounted: false, settled: true));
    }

    [Fact] public void PinTiles_SkipsFoldersKeepsOrderAndCapsAtSix()
    {
        var pins = new List<SidebarPin>
        {
            new("home", SidebarEntryKind.AppRoute, "", "Home", 0),
            new("albums", SidebarEntryKind.AppRoute, "", "Albums", 0),
            new("folder:abc", SidebarEntryKind.Folder, "", "Folder", 0),
            new("artists", SidebarEntryKind.AppRoute, "", "Artists", 0),
            new("podcasts", SidebarEntryKind.AppRoute, "", "Podcasts", 0),
            new("audiobooks", SidebarEntryKind.AppRoute, "", "Audiobooks", 0),
            new("recents", SidebarEntryKind.AppRoute, "", "Recents", 0),
            new("search", SidebarEntryKind.AppRoute, "", "Search", 0),
        };
        var tiles = new List<SidebarPin> { pins[0] };          // stale content: PinTiles clears it first
        ZuneNavRules.PinTiles(pins, tiles);
        Assert.Equal(new[] { "home", "albums", "artists", "podcasts", "audiobooks", "recents" }, tiles.ConvertAll(p => p.Id).ToArray());
    }

    // ── what row 2 carries, decided by the ROUTE ──

    [Theory]
    [InlineData("liked", ZuneSubRow.Library)]
    [InlineData("albums", ZuneSubRow.Library)]
    [InlineData("artists", ZuneSubRow.Library)]
    [InlineData("podcasts", ZuneSubRow.Library)]
    [InlineData("audiobooks", ZuneSubRow.Library)]
    [InlineData("local", ZuneSubRow.Library)]
    [InlineData("home", ZuneSubRow.Views)]
    [InlineData("recents", ZuneSubRow.Views)]
    [InlineData("settings", ZuneSubRow.Views)]
    [InlineData("people:0:spotify:user:abc", ZuneSubRow.Views)]
    [InlineData("people:1:spotify:user:abc", ZuneSubRow.Views)]
    [InlineData("disco:0:spotify:artist:abc", ZuneSubRow.Views)]
    [InlineData("disco:2:spotify:artist:abc", ZuneSubRow.Views)]
    [InlineData("browse", ZuneSubRow.Categories)]
    [InlineData("browse:spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", ZuneSubRow.Categories)]
    [InlineData("browse:spotify:page:0JQ5DArNBzkmxXHCqFLx2J", ZuneSubRow.Categories)]
    [InlineData("browse:spotify:page:0JQ5DAqbMKFETqK4t8f1n3", ZuneSubRow.Categories)]
    [InlineData("browse:spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", ZuneSubRow.None)]         // a genre: not a top category
    [InlineData("artist:spotify:artist:abc", ZuneSubRow.Context)]
    [InlineData("user:spotify:user:abc", ZuneSubRow.Context)]
    [InlineData("episode:spotify:episode:abc", ZuneSubRow.Context)]
    [InlineData("show:spotify:show:abc", ZuneSubRow.Context)]
    [InlineData("album:spotify:album:abc", ZuneSubRow.None)]          // keeps its in-page band: row 2 would only repeat the title
    [InlineData("prerelease:spotify:prerelease:abc", ZuneSubRow.None)]
    [InlineData("pl:spotify:playlist:abc", ZuneSubRow.None)]
    [InlineData("history", ZuneSubRow.None)]
    [InlineData("logs", ZuneSubRow.None)]
    [InlineData("concerts", ZuneSubRow.None)]
    [InlineData("whatsnew", ZuneSubRow.None)]
    [InlineData("home-section:spotify:section:abc", ZuneSubRow.None)]
    public void SubRowOf_IsRouteOnly(string name, ZuneSubRow expected)
        => Assert.Equal(expected, ZuneNavRules.SubRowOf(Shell.Parse(name)));

    [Fact] public void SubRowOf_SearchWithAQueryCarriesItsViews()
        => Assert.Equal(ZuneSubRow.Views, ZuneNavRules.SubRowOf(Shell.Parse("search".AsSpan(), "abc".AsSpan())));

    [Fact] public void SubRowOf_EveryTopCategoryThatParsesIsCategories()
    {
        int parsed = 0;
        foreach (string uri in BrowseTaxonomy.TopUris)
        {
            var route = BrowseTiles.PageRoute(uri, null);
            if (route.Subject.Text != uri) continue;          // a client feature (spotify:concerts) is not a page uri
            parsed++;
            Assert.Equal(ZuneSubRow.Categories, ZuneNavRules.SubRowOf(route));
        }
        Assert.True(parsed >= 3);
    }

    [Fact] public void SubRowOf_EveryRouteKindMapsToOneKindWithoutThrowing()
    {
        foreach (Shell.RouteKind kind in Enum.GetValues<Shell.RouteKind>())
            Assert.True(Enum.IsDefined(ZuneNavRules.SubRowOf(new Shell.Route(kind))));
    }

    [Fact] public void BrowseTaxonomy_TopUris_AreTheFourTopEntriesInMapOrder()
    {
        Assert.Equal(4, BrowseTaxonomy.TopUris.Count);
        Assert.Equal("spotify:concerts", BrowseTaxonomy.TopUris[3]);
        Assert.Equal(BrowseTaxonomy.UrisOf(BrowseGroup.Top), BrowseTaxonomy.TopUris);
        Assert.Equal(BrowseTaxonomy.TopUris.Count, ZuneNavRules.CategoryLabelKeys.Length);
    }

    // ── the seeds ──

    [Fact] public void ViewSeedKeys_FilledForViewKindsAndNullForSearch()
    {
        foreach (var kind in new[] { Shell.RouteKind.Home, Shell.RouteKind.Recents, Shell.RouteKind.Settings,
                     Shell.RouteKind.ProfileList, Shell.RouteKind.Discography })
        {
            var keys = ZuneNavRules.ViewSeedKeys(kind);
            Assert.NotNull(keys);
            Assert.NotEmpty(keys!);
        }
        Assert.Null(ZuneNavRules.ViewSeedKeys(Shell.RouteKind.Search));
        Assert.Null(ZuneNavRules.ViewSeedKeys(Shell.RouteKind.History));
    }

    [Fact] public void SettingsSeed_HasAsManyWordsAsTheTabBar()
    {
        Assert.Equal(Settings.TabLabels().Length, ZuneNavRules.ViewSeedKeys(Shell.RouteKind.Settings)!.Length);
        Assert.Equal(Settings.TabSlugs.Length, ZuneNavRules.SettingsTabKeys.Length);
    }

    [Fact] public void RecentsSeed_IsTheViewsInPivotOrder_AndSelectsTheRouteArgsPivot()
    {
        Assert.Equal(RecentsView.PivotOrder.Length, ZuneNavRules.RecentsViewKeys.Length);
        foreach (string? token in RecentsView.PivotOrder)
        {
            var route = new Shell.Route(Shell.RouteKind.Recents, Arg: token is null ? default : Entities.Strings.Intern(token));
            Assert.Equal(RecentsView.PivotIndexOf(token), ZuneNavRules.SeedSelected(route));
        }
    }

    [Fact] public void ProfileListAndDiscographySeedsSelectTheFacetInTheRoute()
    {
        Assert.Equal(0, ZuneNavRules.SeedSelected(Shell.Parse("people:0:spotify:user:abc")));
        Assert.Equal(1, ZuneNavRules.SeedSelected(Shell.Parse("people:1:spotify:user:abc")));
        Assert.Equal(0, ZuneNavRules.SeedSelected(Shell.Parse("disco:0:spotify:artist:abc")));
        Assert.Equal(2, ZuneNavRules.SeedSelected(Shell.Parse("disco:2:spotify:artist:abc")));
        Assert.Equal(ZuneNavRules.ProfileListViewKeys.Length, ProfileListFacets.Order.Length);
    }

    [Fact] public void ContextSeed_IsTheSkeletonWordCountPerEntity()
    {
        Assert.Equal((3, 2), ZuneNavRules.ContextSeed(Shell.RouteKind.Artist));
        Assert.Equal((2, 1), ZuneNavRules.ContextSeed(Shell.RouteKind.User));
        Assert.Equal((4, 0), ZuneNavRules.ContextSeed(Shell.RouteKind.Episode));
        Assert.Equal((0, 0), ZuneNavRules.ContextSeed(Shell.RouteKind.Show));
        Assert.Equal((0, 0), ZuneNavRules.ContextSeed(Shell.RouteKind.Album));        // no row 2 to seed
        Assert.Equal((0, 0), ZuneNavRules.ContextSeed(Shell.RouteKind.Prerelease));
        Assert.Equal((0, 0), ZuneNavRules.ContextSeed(Shell.RouteKind.Playlist));
    }

    // ── the band's rhythm and the two Zune type roles ──

    [Fact] public void BandHeight_IsBuiltFromTheNamedRhythmTerms()
    {
        Assert.Equal(ZuneNavRules.PivotTop + ZuneNavRules.PivotLine + ZuneNavRules.PivotToSub
                     + ZuneNavRules.SubRowHeight + ZuneNavRules.SubToCard, ZuneNavRules.BandHeight(ShellNavStyle.Zune, ZuneSubRow.Views));
        Assert.Equal(84f, ZuneNavRules.BandHeight(ShellNavStyle.Zune, ZuneSubRow.Views));
        Assert.Equal(44f, ZuneNavRules.PivotRowHeight);
        // Row 2 collapsed: the pivot row and the gap above the card only (44 + 8).
        Assert.Equal(ZuneNavRules.PivotRowHeight + ZuneNavRules.SubToCard, ZuneNavRules.BandHeight(ShellNavStyle.Zune, ZuneSubRow.None));
        Assert.Equal(52f, ZuneNavRules.BandHeight(ShellNavStyle.Zune, ZuneSubRow.None));
        foreach (var row in Enum.GetValues<ZuneSubRow>())
        {
            Assert.Equal(0f, ZuneNavRules.BandHeight(ShellNavStyle.Classic, row));
            Assert.Equal(0f, ZuneNavRules.BandHeight(ShellNavStyle.Library, row));
        }
    }

    [Fact] public void ThePinColumnFitsInsideThePivotRow()
        => Assert.True(ZuneNavRules.PinTile + ZuneNavRules.PinDotGap + ZuneNavRules.PinDot <= ZuneNavRules.PivotRowHeight);

    [Fact] public void PivotLine_IsTheTopPivotRolesLineHeight()
    {
        Assert.Equal(ZuneNavRules.PivotLine, Design.Type.ZunePivot("x", false).LineHeight);
        Assert.Equal(Design.Type.ZunePivot("x", false).LineHeight, Design.Type.ZunePivot("x", true).LineHeight);
        Assert.Equal(Design.Type.ZunePivot("x", false).Size, Design.Type.ZunePivot("x", true).Size);
    }

    [Fact] public void SubPivotRole_IsTwentyAndFitsRowTwo()
    {
        Assert.Equal(20f, Design.Type.ZuneSubPivot("x", false).LineHeight);
        Assert.True(Design.Type.ZuneSubPivot("x", false).LineHeight <= ZuneNavRules.SubRowHeight);
        Assert.Equal(Design.Type.ZuneSubPivot("x", false).LineHeight, Design.Type.ZuneSubPivot("x", true).LineHeight);
        Assert.Equal(Design.Type.ZuneSubPivot("x", false).Size, Design.Type.ZuneSubPivot("x", true).Size);
    }

    [Theory]
    [InlineData("home")] [InlineData("browse")] [InlineData("recents")] [InlineData("liked")] [InlineData("albums")]
    [InlineData("artists")] [InlineData("podcasts")] [InlineData("audiobooks")]
    public void IsPivotDestination_TrueForPivotRoutes(string route) => Assert.True(ZuneNavRules.IsPivotDestination(route));

    [Theory]
    [InlineData("local")] [InlineData("home-section:x")] [InlineData("browse:pop")] [InlineData("browse-section:x")]
    [InlineData("search")] [InlineData("settings")] [InlineData("album:x")]
    public void IsPivotDestination_FalseForTheRest(string route) => Assert.False(ZuneNavRules.IsPivotDestination(route));

    [Fact] public void PinShowsPlaying_NeedsAUriAnActiveContextAndTheRelation()
    {
        const string uri = "spotify:album:1";
        Assert.False(ZuneNavRules.PinShowsPlaying("", true, true));
        Assert.False(ZuneNavRules.PinShowsPlaying(uri, false, true));
        Assert.False(ZuneNavRules.PinShowsPlaying(uri, true, false));
        Assert.True(ZuneNavRules.PinShowsPlaying(uri, true, true));
    }

    [Fact] public void SubRowOf_ResolvesEveryZuneRouteToExactlyOneRowKind()
    {
        var routes = new List<string>(ZuneNavRules.LibraryPages) { "home", "browse", "recents", "local", "settings" };
        foreach (string name in routes)
        {
            // A route is a Library page or it is not: the library pages (and Local) always carry the sub-pivots.
            var kind = ZuneNavRules.SubRowOf(Shell.Parse(name));
            Assert.Equal(ZuneNavRules.TopOf(name) == ZuneNavRules.LibraryPivot, kind == ZuneSubRow.Library);
            Assert.Equal(name is "home" or "recents" or "settings", kind == ZuneSubRow.Views);
        }
        // Every top pivot lands on a route whose row kind is defined.
        foreach (string pivot in ZuneNavRules.Top)
            Assert.True(Enum.IsDefined(ZuneNavRules.SubRowOf(Shell.Parse(ZuneNavRules.LandingOf(pivot)))));
    }

    [Theory]
    [InlineData("home", 84f)]
    [InlineData("recents", 84f)]
    [InlineData("liked", 84f)]                  // Liked Songs keeps Library's sub-pivots
    [InlineData("albums", 84f)]
    [InlineData("local", 84f)]
    [InlineData("settings", 84f)]
    [InlineData("search", 84f)]
    [InlineData("people:0:spotify:user:abc", 84f)]
    [InlineData("disco:0:spotify:artist:abc", 84f)]
    [InlineData("browse", 84f)]
    [InlineData("browse:spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", 84f)]
    [InlineData("artist:spotify:artist:abc", 84f)]
    [InlineData("user:spotify:user:abc", 84f)]
    [InlineData("show:spotify:show:abc", 84f)]
    [InlineData("episode:spotify:episode:abc", 84f)]
    [InlineData("album:spotify:album:abc", 52f)]
    [InlineData("prerelease:spotify:prerelease:abc", 52f)]
    [InlineData("pl:spotify:playlist:abc", 52f)]
    [InlineData("concerts", 52f)]
    [InlineData("history", 52f)]
    [InlineData("logs", 52f)]
    [InlineData("home-section:spotify:section:abc", 52f)]
    [InlineData("browse:spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", 52f)]   // a genre page
    public void BandHeight_FollowsTheRoutesRowKind_AndIsZeroOutsideZune(string name, float zuneExpected)
    {
        var row = ZuneNavRules.SubRowOf(Shell.Parse(name));
        Assert.Equal(zuneExpected, ZuneNavRules.BandHeight(ShellNavStyle.Zune, row));
        Assert.Equal(0f, ZuneNavRules.BandHeight(ShellNavStyle.Classic, row));
        Assert.Equal(0f, ZuneNavRules.BandHeight(ShellNavStyle.Library, row));
    }

    [Fact] public void TheBootRoute_SeedsHomeAtTheFullBand()
    {
        // FrameRoot seeds Shell.Ui.PresentedSubRow from the boot route: Home carries its views, so it opens at 84, never animating from 52.
        var row = ZuneNavRules.SubRowOf(Shell.Parse("home"));
        Assert.Equal(ZuneSubRow.Views, row);
        Assert.Equal(84f, ZuneNavRules.BandHeight(ShellNavStyle.Zune, row));
    }
}
