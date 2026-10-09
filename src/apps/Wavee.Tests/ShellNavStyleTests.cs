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

    [Fact] public void ShowsPins_NeedsTheSettingAndTheMinimumViewport()
    {
        Assert.False(ZuneNavRules.ShowsPins(true, 719f));
        Assert.True(ZuneNavRules.ShowsPins(true, 720f));
        Assert.False(ZuneNavRules.ShowsPins(false, 1200f));
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
    [InlineData("browse:spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", ZuneSubRow.Title)]        // a genre: not a top category
    [InlineData("artist:spotify:artist:abc", ZuneSubRow.Context)]
    [InlineData("user:spotify:user:abc", ZuneSubRow.Context)]
    [InlineData("episode:spotify:episode:abc", ZuneSubRow.Context)]
    [InlineData("show:spotify:show:abc", ZuneSubRow.Context)]
    [InlineData("album:spotify:album:abc", ZuneSubRow.Context)]
    [InlineData("pl:spotify:playlist:abc", ZuneSubRow.Context)]
    [InlineData("history", ZuneSubRow.Title)]
    [InlineData("logs", ZuneSubRow.Title)]
    [InlineData("concerts", ZuneSubRow.Title)]
    [InlineData("whatsnew", ZuneSubRow.Title)]
    [InlineData("home-section:spotify:section:abc", ZuneSubRow.Title)]
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
        Assert.Equal((0, 1), ZuneNavRules.ContextSeed(Shell.RouteKind.Album));
        Assert.Equal((0, 1), ZuneNavRules.ContextSeed(Shell.RouteKind.Playlist));
    }

    // ── the band's rhythm and the two Zune type roles ──

    [Fact] public void BandHeight_IsBuiltFromTheNamedRhythmTerms()
    {
        Assert.Equal(ZuneNavRules.PivotTop + ZuneNavRules.PivotLine + ZuneNavRules.PivotToSub
                     + ZuneNavRules.SubRowHeight + ZuneNavRules.SubToCard, ZuneNavRules.BandHeight(ShellNavStyle.Zune));
        Assert.Equal(84f, ZuneNavRules.BandHeight(ShellNavStyle.Zune));
        Assert.Equal(44f, ZuneNavRules.PivotRowHeight);
        Assert.Equal(0f, ZuneNavRules.BandHeight(ShellNavStyle.Classic));
        Assert.Equal(0f, ZuneNavRules.BandHeight(ShellNavStyle.Library));
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

    [Fact] public void BandHeight_IsTheSameOnEveryRoute_AndZeroOutsideZune()
    {
        var routes = new List<string>(ZuneNavRules.Top);
        routes.AddRange(ZuneNavRules.LibraryPages);
        routes.AddRange(["settings", "album:x", "artist:x", "search", "browse:pop", "local", "home-section:x"]);
        foreach (var _ in routes)
        {
            Assert.Equal(84f, ZuneNavRules.BandHeight(ShellNavStyle.Zune));
            Assert.Equal(0f, ZuneNavRules.BandHeight(ShellNavStyle.Classic));
            Assert.Equal(0f, ZuneNavRules.BandHeight(ShellNavStyle.Library));
        }
        Assert.Equal(ZuneNavRules.PivotRowHeight + ZuneNavRules.SubRowHeight + ZuneNavRules.SubToCard, ZuneNavRules.BandHeight(ShellNavStyle.Zune));
    }
}
