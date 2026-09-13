using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// <see cref="SidebarNavBandModel"/> — the shortcut band's pure SHAPING rules, as a model.
///
/// <para><b>This suite is named for the model, not for a component.</b> The bespoke <c>SidebarNavBand</c> component it
/// was originally written against is GONE: Phase 1 / Decision A materialises the band as an ordinary "Shortcuts"
/// <c>StaticLinks</c> section at the head of every design's document (<c>SidebarShortcutsSection</c>), so the band is
/// planned, rowed, railed and reordered by the one <c>SidebarPane</c> with no second render path. The MODEL survives
/// because its four rules are the band's, not the widget's, and they map 1:1 onto the planner's Route / Entity / Track
/// / Action arms: item target → tile shape, tile → the route key a selection mark reads, document order, and the
/// truncation bound that keeps a hand-edited over-long band from outgrowing the pane.</para>
///
/// <para>The band's CONTENT and every mutation of it are pinned elsewhere and deliberately not restated here:
/// <c>SidebarLayoutReducerTests</c> owns the AddTopBarItem/MoveTopBarItem/RemoveTopBarItem arms and the cap,
/// <c>SidebarLayoutJsonTests</c> owns the wire, and <c>SidebarShortcutsSectionTests</c> owns the materialisation into a
/// section plus the sentinel-id command routing.</para>
/// </summary>
public class SidebarNavBandTests
{
    static SidebarItemSpec Route(string key, string id = "itm_route")
        => new(id, SidebarItemTarget.Route, key);

    static SidebarItemSpec Entity(string uri, SidebarEntityKind kind = SidebarEntityKind.Playlist,
                                  string id = "itm_entity")
        => new(id, SidebarItemTarget.Entity, uri, kind);

    static SidebarItemSpec Track(string uri, string id = "itm_track")
        => new(id, SidebarItemTarget.Track, uri);

    static SidebarItemSpec Action(string id = "itm_action")
        => new(id, SidebarItemTarget.Action, "", Action: SidebarActionBinding.Simple("wavee", "play"));

    static List<SidebarNavBandTile> Shape(IReadOnlyList<SidebarItemSpec>? band, int cap = SidebarNavBandModel.MaxTiles)
    {
        var into = new List<SidebarNavBandTile>();
        int n = SidebarNavBandModel.Shape(band, into, cap);
        Assert.Equal(n, into.Count);
        return into;
    }

    // ── the empty-collapse (both forms draw nothing) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptiedBand_RendersNothing()
    {
        Assert.False(SidebarNavBandModel.Renders(System.Array.Empty<SidebarItemSpec>()));
        Assert.Empty(Shape(System.Array.Empty<SidebarItemSpec>()));
    }

    [Fact]
    public void NullBand_RendersNothing()
    {
        Assert.False(SidebarNavBandModel.Renders(null));
        Assert.Empty(Shape(null));
    }

    [Fact]
    public void DefaultBand_IsTheSingleHomeTile()
    {
        var band = SidebarCustomLayout.DefaultTopBar;
        Assert.True(SidebarNavBandModel.Renders(band));

        var tiles = Shape(band);
        var tile = Assert.Single(tiles);
        Assert.Equal(SidebarNavBandTileKind.Route, tile.Kind);
        Assert.Equal("home", tile.Key);
        Assert.Equal("home", tile.RouteKey);
        Assert.Equal(SidebarIds.TopBarHomeItem, tile.ItemId);
        Assert.Equal(0, tile.Index);
    }

    /// <summary>A never-customized document resolves to the built-in band, so a fresh install still gets exactly one
    /// Home shortcut — now as the first row of the materialised "Shortcuts" section.</summary>
    [Fact]
    public void NeverCustomizedLayout_ResolvesToTheDefaultBand()
    {
        var layout = new SidebarCustomLayout("curated", System.Array.Empty<SidebarSectionSpec>());
        Assert.Null(layout.TopBar);
        Assert.True(SidebarNavBandModel.Renders(layout.EffectiveTopBar));
        Assert.Same(SidebarCustomLayout.DefaultTopBar, layout.EffectiveTopBar);
    }

    // ── ordering ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shape_KeepsDocumentOrderAndIndexes()
    {
        var band = new[] { Route("home", "a"), Route("search", "b"), Route("liked", "c") };
        var tiles = Shape(band);

        Assert.Equal(3, tiles.Count);
        Assert.Equal(new[] { "a", "b", "c" }, new[] { tiles[0].ItemId, tiles[1].ItemId, tiles[2].ItemId });
        Assert.Equal(new[] { 0, 1, 2 }, new[] { tiles[0].Index, tiles[1].Index, tiles[2].Index });
    }

    [Fact]
    public void Shape_ClearsTheCallerScratch()
    {
        var into = new List<SidebarNavBandTile> { new(9, "stale", SidebarNavBandTileKind.Track, "x", null) };
        SidebarNavBandModel.Shape(new[] { Route("home") }, into);

        var tile = Assert.Single(into);
        Assert.Equal("itm_route", tile.ItemId);
    }

    // ── truncation ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MaxTiles_IsTheReducersCap()
        => Assert.Equal(SidebarLayoutReducer.MaxTopBarItems, SidebarNavBandModel.MaxTiles);

    [Fact]
    public void Shape_TruncatesTheTailPastTheCap()
    {
        var band = new List<SidebarItemSpec>();
        for (int i = 0; i < SidebarNavBandModel.MaxTiles + 3; i++) band.Add(Route("r" + i, "itm_" + i));

        var tiles = Shape(band);
        Assert.Equal(SidebarNavBandModel.MaxTiles, tiles.Count);
        // HEAD kept, tail dropped — the user's leading choices survive.
        Assert.Equal("itm_0", tiles[0].ItemId);
        Assert.Equal("itm_" + (SidebarNavBandModel.MaxTiles - 1), tiles[^1].ItemId);
    }

    [Fact]
    public void Shape_HonoursASmallerCap()
    {
        var band = new[] { Route("home", "a"), Route("search", "b"), Route("liked", "c") };
        Assert.Equal(2, Shape(band, cap: 2).Count);
        Assert.Empty(Shape(band, cap: 0));
        Assert.Empty(Shape(band, cap: -1));
    }

    // ── the four tile shapes ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarItemTarget.Route, SidebarNavBandTileKind.Route)]
    [InlineData(SidebarItemTarget.Entity, SidebarNavBandTileKind.Entity)]
    [InlineData(SidebarItemTarget.Track, SidebarNavBandTileKind.Track)]
    [InlineData(SidebarItemTarget.Action, SidebarNavBandTileKind.Action)]
    public void KindOf_MapsEveryTarget(SidebarItemTarget target, SidebarNavBandTileKind expected)
        => Assert.Equal(expected, SidebarNavBandModel.KindOf(new SidebarItemSpec("itm_x", target, "k")));

    /// <summary>An unknown/future target must degrade to the ROUTE shape — the one that can always draw a glyph and a
    /// label — rather than to a hole (the preserve-don't-destroy discipline, at the render site).</summary>
    [Fact]
    public void KindOf_UnknownTargetDegradesToRoute()
        => Assert.Equal(SidebarNavBandTileKind.Route,
                        SidebarNavBandModel.KindOf(new SidebarItemSpec("itm_x", (SidebarItemTarget)99, "k")));

    // ── the destination the selection mark reads ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void RouteKeyOf_RouteItemIsItsOwnKey()
        => Assert.Equal("home", SidebarNavBandModel.RouteKeyOf(Route("home")));

    [Fact]
    public void RouteKeyOf_EmptyRouteKeyHasNoDestination()
        => Assert.Null(SidebarNavBandModel.RouteKeyOf(Route("")));

    /// <summary>The uri → route map is the PIN SCHEME's (one owner): a playlist/album/artist/show id IS its route key.</summary>
    [Fact]
    public void RouteKeyOf_EntityUsesThePinScheme()
    {
        string uri = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";
        Assert.Equal(SidebarPinId.FromUri(uri), SidebarNavBandModel.RouteKeyOf(Entity(uri)));
        Assert.NotNull(SidebarNavBandModel.RouteKeyOf(Entity(uri)));
    }

    /// <summary>A uri the pin scheme refuses (an episode / a track / a hand-edited document) has nowhere to navigate, so
    /// the tile renders visible-but-inert instead of lying about a destination.</summary>
    [Fact]
    public void RouteKeyOf_UnresolvableEntityHasNoDestination()
        => Assert.Null(SidebarNavBandModel.RouteKeyOf(Entity("spotify:episode:abc", SidebarEntityKind.None)));

    [Fact]
    public void RouteKeyOf_TrackAndActionNeverNavigate()
    {
        Assert.Null(SidebarNavBandModel.RouteKeyOf(Track("spotify:track:abc")));
        Assert.Null(SidebarNavBandModel.RouteKeyOf(Action()));
    }

    [Fact]
    public void SelectsRoute_MatchesTheActiveRouteOrdinally()
    {
        var home = Route("home");
        Assert.True(SidebarNavBandModel.SelectsRoute(home, "home"));
        Assert.False(SidebarNavBandModel.SelectsRoute(home, "Home"));
        Assert.False(SidebarNavBandModel.SelectsRoute(home, "search"));
        Assert.False(SidebarNavBandModel.SelectsRoute(home, ""));
        Assert.False(SidebarNavBandModel.SelectsRoute(home, null));
    }

    [Fact]
    public void SelectsRoute_PlayableTilesAreNeverSelected()
    {
        Assert.False(SidebarNavBandModel.SelectsRoute(Track("spotify:track:abc"), "spotify:track:abc"));
        Assert.False(SidebarNavBandModel.SelectsRoute(Action(), "home"));
    }

    [Fact]
    public void SelectsRoute_EntityFollowsItsResolvedRoute()
    {
        string uri = "spotify:album:1";
        var item = Entity(uri, SidebarEntityKind.Album);
        string route = SidebarPinId.FromUri(uri)!;
        Assert.True(SidebarNavBandModel.SelectsRoute(item, route));
        Assert.False(SidebarNavBandModel.SelectsRoute(item, uri));
    }
}
