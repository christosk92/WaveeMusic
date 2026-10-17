// ── Wavee.ShellTests/TierLayoutTests.Discovery.cs — Classic's discovery pages at every tier size and threshold edge (themes plan 3.5; TC4) ────────
//
// Home, Browse, a Browse category and Search, each mounted through the real frame at the plan 3.5 sizes (400 x 860, 800 x 330, 800 x 600, 1440 x 960,
// 2560 x 1440 and, for Home, 3840 x 2160) and at every threshold edge of Classic's ladder, with `TierLayoutTests.AssertPage`'s frame assertions (the tier in the
// FIRST frame, no overlap of the four regions, no page-level horizontal overflow, Compact title-bar targets, the 210 ShortFloor) and, on top, the page's
// own: the record in use is the structure's record for the content tier, the page column holds its cap, and where the record says so the band sits
// where the board draws it. Asserted on the records and the node geometry, never on pixels.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    /// <summary>Home at every size, including the 3840 key surface (plan 3.5).</summary>
    [Theory, MemberData(nameof(FrameSizes))]
    public void Home_holds_its_record_at_every_tier(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var layout = ClassicHomeTiers.Of(tiers);
        Assert.Equal(tiers.Content > WidthTier.Huge ? WidthTier.Huge : tiers.Content, layout.Tier);   // the record is the content tier's
        Assert.Equal(tiers.Height == HeightTier.Short, layout.Short);
        var scene = shell.Host.Scene;
        var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
        var column = scene.AbsoluteRect(Node(shell, "home:column"));
        // The page column never runs past its tier's cap (Standard 1600, Huge 2400; Compact and Small fill the region) or its card.
        if (!float.IsNaN(layout.PageMaxW)) Assert.True(column.W <= layout.PageMaxW + 0.5f, $"{size}: the page column is {column.W}, over its cap {layout.PageMaxW}");
        Assert.True(column.W <= card.W + 0.5f, $"{size}: the page column is wider than its card");
        // The page gutter is the content tier's: the column starts on it (Compact 16, Small 32, Standard and Huge 36) unless a cap centres it.
        if (float.IsNaN(layout.PageMaxW)) Assert.Equal(card.W, column.W, 1.5f);
        // Short: the hero is a strip no taller than the 60 % cap.
        foreach (var (node, _) in shell.FindPrefix("daylist-copy"))
            if (layout.Daylist.Form == ClassicDaylistForm.Strip) Assert.True(scene.AbsoluteRect(node).H <= ClassicHomeTiers.ShortHeroH + 0.5f, $"{size}: the Short daylist is taller than its cap");
        // The hero and the tiles share ONE band at every tier (so a crossing moves them instead of remounting them); only Huge sets them side by side, never
        // in a Short window.
        foreach (var (_, key) in shell.FindPrefix("zone:").Where(static z => z.Key.Contains('+')))
        {
            int plus = key.IndexOf('+');
            var hero = scene.AbsoluteRect(Node(shell, key[..plus]));
            var tiles = scene.AbsoluteRect(Node(shell, "zone:" + key[(plus + 1)..]));
            bool beside = Math.Abs(hero.Y - tiles.Y) < 1f && tiles.X > hero.X + hero.W - 0.5f;
            Assert.True(beside == layout.DaylistBeside, $"{size}: the hero {(beside ? "sits beside" : "is stacked over")} the tiles at {layout.Tier}");
        }
        AssertFacetRowEnd(shell, layout, card, size);
    });

    /// <summary>The facet row's end (C-Home-Compact): the 40 x 40 Friend activity button only where the record draws it (Compact), inside the card.</summary>
    static void AssertFacetRowEnd(Shell.HeadlessShell shell, ClassicHomeLayout layout, RectF card, string size)
    {
        var scene = shell.Host.Scene;
        if (shell.Find("home:facet:row").IsNull) return;   // the row is not composed yet (a first frame with no model)
        var friends = shell.FindPrefix("home:facet:friends");
        Assert.True(friends.Count == (layout.FriendsButton ? 1 : 0), $"{size}: {friends.Count} Friend activity buttons, the record says {layout.FriendsButton}");
        foreach (var (node, _) in friends)
        {
            var r = scene.AbsoluteRect(node);
            Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{size}: the Friend activity button is {r.W} x {r.H}, under 40 x 40");
            Assert.True(r.X >= card.X - 0.5f && r.X + r.W <= card.X + card.W + 0.5f, $"{size}: the Friend activity button runs outside the card ({r})");
        }
        // The Following toggle on every word (Huge) is pinned on the pure rule (FacetPivotTests.FollowingWord_*) and the record (ClassicDiscoveryTierTests).
    }

    /// <summary>Open the next mount on <paramref name="route"/> (a remount opens on the page the last root showed), so the page under test is the only one in the
    /// frame: no Home left parked beside it.</summary>
    void OpenOn(Shell.Route route) => host.Run(() => Shell.GoTo(route));

    [Theory, MemberData(nameof(PageSizes))]
    public void Browse_holds_its_record_at_every_tier(string size)
    {
        var browse = new TierPage("browse", new Shell.Route(Shell.RouteKind.Browse), "browse-band:");
        OpenOn(browse.Route);
        AssertPage(browse, size, (shell, tiers) =>
        {
            var layout = ClassicBrowseTiers.Of(tiers);
            Assert.Equal(tiers.Content > WidthTier.Huge ? WidthTier.Huge : tiers.Content, layout.Tier);
            var scene = shell.Host.Scene;
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            // Every band lies inside the card; the genre grid has the tier's fixed column count.
            foreach (var (node, key) in shell.FindPrefix("browse-band:"))
            {
                var r = scene.AbsoluteRect(node);
                Assert.True(r.W <= card.W + 0.5f, $"{size}: {key} is wider than the card");
            }
            AssertGenreColumns(shell, layout, size);
        });
    }

    /// <summary>Every genre link grid (one key at every tier) lays out the tier's fixed column count.</summary>
    static void AssertGenreColumns(Shell.HeadlessShell shell, ClassicBrowseLayout layout, string size)
    {
        foreach (var (node, _) in shell.FindPrefix("browse-link-grid"))
        {
            Assert.True(shell.Host.Scene.TryGetGrid(node, out var grid), $"{size}: the genre grid is not a grid");
            Assert.True(grid.Columns.Length == layout.GenreColumns(), $"{size}: the genre grid has {grid.Columns.Length} columns, not {layout.GenreColumns()}");
        }
    }

    [Theory, MemberData(nameof(PageSizes))]
    public void A_browse_category_holds_its_record_at_every_tier(string size)
    {
        Shell.Route route = default;
        host.Run(() => route = BrowseTiles.PageRoute("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", "Pop"));
        var category = new TierPage("browse-category", route, "page:");
        OpenOn(route);
        AssertPage(category, size, (shell, tiers) =>
        {
            var layout = ClassicBrowseTiers.Of(tiers);
            Assert.Equal(tiers.Content > WidthTier.Huge ? WidthTier.Huge : tiers.Content, layout.Tier);
            AssertGenreColumns(shell, layout, size);
        });
    }

    [Theory, MemberData(nameof(PageSizes))]
    public void Search_holds_its_record_at_every_tier(string size)
    {
        Shell.Route route = default;
        host.Run(() => route = Search.GenreRoute("spotify:genre:", "daft punk"));
        var page = new TierPage("search", route, "facets");
        OpenOn(route);
        AssertPage(page, size, (shell, tiers) =>
        {
            var layout = ClassicSearchTiers.Of(tiers);
            Assert.Equal(tiers.Content > WidthTier.Huge ? WidthTier.Huge : tiers.Content, layout.Tier);
            Assert.Equal(ClassicTiers.PageOf(tiers).Gutter, layout.Gutter);
            var scene = shell.Host.Scene;
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            // The facet row: a strip (one row, the tier's height) below Standard, a wrapping row from it.
            foreach (var (node, key) in shell.FindPrefix("facets"))
            {
                var r = scene.AbsoluteRect(node);
                Assert.True(r.X >= card.X - 0.5f && r.X + r.W <= card.X + card.W + 0.5f, $"{size}: the facet row runs outside the card ({r})");
                if (!layout.TabsWrap) Assert.True(r.H <= layout.TabStripH + 0.5f, $"{size}: {key} is {r.H} tall, over the strip's {layout.TabStripH}");
            }
            // C-Search-Huge: the avatar top result (under its own header, the record's 700 wide) and the Artists rail are Huge's alone. Below Huge neither
            // mounts; at Huge each is checked once the answer carries it (the fake catalogue's hits decide whether there is an artist to rail).
            var top = shell.Find("search:top-result");
            var artists = shell.Find("search:artists");
            if (layout.HeroForm != ClassicSearchHeroForm.Avatar) Assert.True(top.IsNull, $"{size}: the avatar top result below Huge");
            if (layout.ArtistRail is null) Assert.True(artists.IsNull, $"{size}: the Artists rail below Huge");
            if (!top.IsNull)
            {
                var r = scene.AbsoluteRect(top);
                Assert.Equal(layout.HeroBesideW, r.W, 1f);
                Assert.True(r.H >= layout.HeroH + ClassicSearchTiers.AvatarHeaderH - 0.5f, $"{size}: the avatar column is {r.H} tall, under its header and card");
            }
            if (!artists.IsNull)
            {
                var r = scene.AbsoluteRect(artists);
                Assert.True(r.X >= card.X - 0.5f && r.X + r.W <= card.X + card.W + 0.5f, $"{size}: the Artists rail runs outside the card ({r})");
            }
        });
    }
}
