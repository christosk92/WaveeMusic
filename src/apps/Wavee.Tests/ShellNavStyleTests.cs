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

    [Fact] public void ShowsSub_OnlyUnderLibrary()
    {
        Assert.True(ZuneNavRules.ShowsSub("artists"));
        Assert.False(ZuneNavRules.ShowsSub("home"));
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

    [Theory]
    [InlineData("albums", ZuneSubRow.Library)]
    [InlineData("local", ZuneSubRow.Library)]
    [InlineData("home", ZuneSubRow.PageViews)]
    [InlineData("recents", ZuneSubRow.PageViews)]
    [InlineData("browse", ZuneSubRow.None)]
    [InlineData("settings", ZuneSubRow.None)]
    [InlineData("album:x", ZuneSubRow.None)]
    public void SubRowOf_IsRouteOnly(string route, ZuneSubRow expected)
        => Assert.Equal(expected, ZuneNavRules.SubRowOf(route));

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
        var routes = new List<string>(ZuneNavRules.LibraryPages) { "home", "browse", "recents", "local", "search", "settings" };
        foreach (string route in routes)
        {
            // A route is a Library page, a published-views page or neither: never two kinds at once.
            var kind = ZuneNavRules.SubRowOf(route);
            Assert.Equal(ZuneNavRules.TopOf(route) == ZuneNavRules.LibraryPivot, kind == ZuneSubRow.Library);
            Assert.Equal(route is "home" or "recents", kind == ZuneSubRow.PageViews);
        }
        // Every top pivot lands on a route whose row kind is defined.
        foreach (string pivot in ZuneNavRules.Top)
            Assert.True(Enum.IsDefined(ZuneNavRules.SubRowOf(ZuneNavRules.LandingOf(pivot))));
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
        Assert.Equal(ZuneNavRules.PivotRowHeight + ZuneNavRules.SubRowHeight, ZuneNavRules.BandHeight(ShellNavStyle.Zune));
    }
}
