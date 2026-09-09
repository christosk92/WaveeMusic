using Xunit;

namespace Wavee.Tests;

// W2 — the lens row's shape is decided by LibraryV3LensRules (System-only, engine-free), never by the component
// that renders it: visibility while drilled, which facet gets a page link, and whether the view-density control
// has room, are all pinned here without a component, a signal or a frame (the LibraryV3HeaderRules pattern).
public sealed class LibraryV3LensRulesTests
{
    const int All = (int)SidebarV3Filter.All;
    const int Playlists = (int)SidebarV3Filter.Playlists;
    const int Podcasts = (int)SidebarV3Filter.Podcasts;
    const int Albums = (int)SidebarV3Filter.Albums;
    const int Artists = (int)SidebarV3Filter.Artists;
    const int Any = (int)SidebarV3Qualifier.Any;
    const int ByYou = (int)SidebarV3Qualifier.ByYou;

    // ── Visible ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Drilled_IsNeverVisible()
    {
        var shape = LibraryV3LensRules.Resolve(All, Any, searching: false, drilled: true, paneWidth: 400f);
        Assert.False(shape.Visible);
    }

    [Fact]
    public void NotDrilled_IsVisible()
    {
        var shape = LibraryV3LensRules.Resolve(All, Any, searching: false, drilled: false, paneWidth: 400f);
        Assert.True(shape.Visible);
    }

    // ── PageRoute ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Albums, "albums")]
    [InlineData(Artists, "artists")]
    [InlineData(Podcasts, "podcasts")]
    [InlineData(All, null)]
    [InlineData(Playlists, null)]
    public void PageRoute_MatchesChipStripRouteFor_ExceptWhileSearching(int filter, string? expected)
    {
        var shape = LibraryV3LensRules.Resolve(filter, Any, searching: false, drilled: false, paneWidth: 400f);
        Assert.Equal(expected, shape.PageRoute);
        Assert.Equal(LibraryV3ChipStrip.RouteFor(filter), shape.PageRoute);
    }

    [Fact]
    public void PageRoute_IsNull_WhileSearching_EvenForAFacetWithAPage()
    {
        var shape = LibraryV3LensRules.Resolve(Albums, Any, searching: true, drilled: false, paneWidth: 400f);
        Assert.Null(shape.PageRoute);
    }

    // ── ShowsView ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShowsView_AtTheBoundary_199IsHidden_200IsShown()
    {
        var below = LibraryV3LensRules.Resolve(All, Any, searching: false, drilled: false, paneWidth: 199f);
        Assert.False(below.ShowsView);

        var at = LibraryV3LensRules.Resolve(All, Any, searching: false, drilled: false, paneWidth: 200f);
        Assert.True(at.ShowsView);
    }

    // ── the record carries the inputs through untouched ──────────────────────────────────────────────────────────

    [Fact]
    public void Shape_CarriesFilterQualifierAndSearching_Through()
    {
        var shape = LibraryV3LensRules.Resolve(Playlists, ByYou, searching: true, drilled: false, paneWidth: 400f);
        Assert.Equal(Playlists, shape.Filter);
        Assert.Equal(ByYou, shape.Qualifier);
        Assert.True(shape.Searching);
    }
}
