// ── Wavee.ShellTests/ZuneTierLayoutTests.Discovery.cs — Zune's Home, Browse and Search at every tier size and edge (themes plan 3.5, 6.3; P5 ZT4) ──
//
// The three discovery pages on the mounted app at every size of the harness, on the facts and the records, never on pixels. For each page and size:
//
//   · the page's record is its content tier's (`ZuneHomeTiers.Of`, `ZuneBrowseTiers.Of`, `ZuneSearchTiers.Of`), its column stands inside the content
//     region and every chapter / band / section inside the column's gutters (no horizontal overflow, no overlap of the top-level regions: the harness);
//   · what the record decides is what is mounted: Home's daylist at the record's height, art, clock and rings and its play ring at the end where the
//     tier puts it, the quickplay groups (no new group at Compact and Small, the pins capped, the history as tiles or rows, one column at Compact);
//     Browse's charts beside For you or above it, For you as a list or plates, the mood columns; Search's top result beside best matches or over them,
//     its art, where its name stands, its columns;
//   · at Compact every target of the page (a link word, a pin, a ring, a row, a plate) is at least 40 x 40.
//
// And each page across every edge both ways (639 / 640, 1007 / 1008, 1919 / 1920, 3199 / 3200 and the heights, including the daylist's height rung):
// the resized frame keeps the old tier, the crossing frame commits the new one, and the page's parts are the SAME live nodes after it (nothing
// remounted); the daylist strip, whose height changes, glides under the chapter resize (the engine's flow reveal) instead of snapping.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class ZuneTierLayoutTests
{
    // ── the fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A discovery page: its route (built on the UI thread), what to wait for until it is loaded, and the keys of its parts that must survive
    /// a crossing as the same nodes (the ones present are taken).</summary>
    sealed record DiscoveryFixture(string Name, Func<Shell.Route> Route, Action<Shell.HeadlessShell> Wait, string[] Parts, string[] PartPrefixes);

    static readonly DiscoveryFixture HomeFixture = new("home", static () => new Shell.Route(Shell.RouteKind.Home),
        static shell => UntilBackground(shell, () => !shell.Find(ZuneHomeRules.ColumnKey).IsNull && !shell.Find(ZuneHomeRules.DaylistKey).IsNull
            && shell.FindPrefix(ZuneHomeRules.ZonePrefix).Count > 0, "Zune's Home column, its daylist and its zones"),
        [
            ZuneHomeRules.ColumnKey, ZuneHomeRules.QuickplayKey, "zune-home-quick-strip", ZuneHomeRules.DaylistKey, "zune-daylist-art", "zune-daylist-copy",
            "zune-daylist-title", "zune-daylist-eyebrow", "zune-daylist-clock", ZuneHomeRules.QuickGroupPrefix + ZuneHomeRules.HistoryGroup,
        ],
        [ZuneHomeRules.ZonePrefix, ZuneHomeRules.PairPrefix]);

    static readonly DiscoveryFixture BrowseFixture = new("browse", static () => new Shell.Route(Shell.RouteKind.Browse),
        static shell => UntilBackground(shell, () => !shell.Find(ZuneBrowseRules.DirectoryKey).IsNull
            && shell.FindPrefix(ZuneBrowseRules.BandPrefix).Count > 1, "Zune's Browse directory and its bands"),
        [
            ZuneBrowseRules.DirectoryKey, ZuneBrowseRules.BandPrefix + "charts+foryou:band", ZuneBrowseRules.BandPrefix + "charts",
            ZuneBrowseRules.BandPrefix + "foryou", ZuneBrowseRules.BandPrefix + "genres:band", ZuneBrowseRules.BandPrefix + "mood:band",
            "zune-browse-plates:charts", "zune-browse-plates:mood", "zune-browse-list:genres",
        ],
        []);

    static readonly DiscoveryFixture SearchFixture = new("search", static () => Shell.Parse("search", Entities.FakeSearchQuery),
        static shell => UntilBackground(shell, () => !shell.Find(ZuneSearchRules.PageKey).IsNull && !shell.Find("zune-search-top-row").IsNull
            && !shell.Find("zune-search-runs").IsNull, "Zune's search page: the top result, best matches and the word runs"),
        [
            ZuneSearchRules.PageKey, "zune-search-top-row", ZuneSearchRules.TopKey, ZuneSearchRules.BestKey, "zune-search-grid", ZuneSearchRules.RailKey,
            "zune-search-runs", "zune-search-top-card", "zune-search-top-hero",
        ],
        []);

    // ── the page at every size ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(FrameSizes))]
    public void Homes_page_holds_its_tier_record(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        // Pins to fill the strip: more than the widest tier shows, so the cap is the record's (the band lists at most MaxBandPins).
        var pinned = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            string uri = "spotify:album:zt4quick" + i;
            string id = SidebarPinId.AlbumPrefix + uri;
            if (Sidebar.IsPinned(id)) continue;
            if (Sidebar.Model.Pin(id, SidebarEntryKind.Album, uri, "Quick " + i)) pinned.Add(id);
        }
        try { AssertHome(shell, size, tiers, pinned.Count); }
        finally
        {
            foreach (string id in pinned) Sidebar.Model.Unpin(id);
            shell.RunFrames(10);
        }
    });

    void AssertHome(Shell.HeadlessShell shell, string sizeName, TierFacts tiers, int pinned)
    {
        HomeFixture.Wait(shell);
        UntilBackground(shell, () => shell.FindPrefix(ZuneHomeRules.PinTilePrefix).Count > 0, sizeName + ": the pins in the quickplay strip");
        shell.RunFrames(60);   // entrances, the chapter resize and the page's first layout settle before a rect is read
        var scene = shell.Host.Scene;
        var lay = ZuneHomeTiers.Of(tiers);
        Assert.Equal(tiers.Content, lay.Tier);

        // The column stands inside the content region, every chapter inside the column's gutters.
        var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
        var column = scene.AbsoluteRect(Node(shell, ZuneHomeRules.ColumnKey));
        AssertWithin(sizeName, "the Home column", column, content.X, content.X + content.W);
        float left = column.X + lay.Gutter, right = column.X + column.W - lay.Gutter;
        foreach (string prefix in new[] { ZuneHomeRules.QuickplayKey, ZuneHomeRules.DaylistKey, ZuneHomeRules.ZonePrefix, ZuneHomeRules.PairPrefix, ZuneHomeRules.QuickGroupPrefix })
            foreach (var (node, key) in shell.FindPrefix(prefix))
                AssertWithin(sizeName, key, scene.AbsoluteRect(node), left, right);

        // The daylist: the record's height, art, clock, rings.
        var daylist = scene.AbsoluteRect(Node(shell, ZuneHomeRules.DaylistKey));
        Assert.Equal(lay.DaylistH, daylist.H, 1f);
        Assert.Equal(lay.DaylistArtW, scene.AbsoluteRect(Node(shell, "zune-daylist-art")).W, 1f);
        Assert.Equal(lay.ClockW, scene.AbsoluteRect(Node(shell, "zune-daylist-clock")).W, 1f);
        Assert.Equal(lay.PlayAtEnd, !shell.Find("zune-daylist-end").IsNull);
        Assert.Equal(lay.DaylistMeta, !shell.Find("zune-daylist-meta").IsNull);
        Assert.False(shell.Find("zune-daylist-play").IsNull, $"{sizeName}: the play ring");
        Assert.Equal(lay.DaylistRings >= 2, !shell.Find("zune-daylist-shuffle").IsNull);
        Assert.Equal(lay.DaylistRings >= 3, !shell.Find("zune-daylist-save").IsNull);
        Assert.Equal(lay.DaylistRings >= 4, !shell.Find("zune-daylist-more").IsNull);
        // The play ring is the record's disc (its word beside it makes it wider, never taller).
        var play = scene.AbsoluteRect(Node(shell, "zune-daylist-play"));
        Assert.Equal(lay.RingSize, play.H, 1f);
        if (lay.DaylistForm == ZuneDaylistForm.OneRow) Assert.Equal(56f, daylist.H, 1f);

        // The quickplay strip: the pins capped by the record (one tile at its art), no new group where the tier has none, the history in its arm.
        var tiles = shell.FindPrefix(ZuneHomeRules.PinTilePrefix);
        if (pinned >= lay.QuickPins) Assert.Equal(lay.QuickPins, tiles.Count);
        else Assert.InRange(tiles.Count, pinned, lay.QuickPins);
        Assert.Equal(lay.PinTileW, scene.AbsoluteRect(tiles[0].Node).W, 1f);
        if (lay.QuickFresh == 0) Assert.True(shell.Find(ZuneHomeRules.QuickGroupPrefix + ZuneHomeRules.FreshGroup).IsNull, $"{sizeName}: no new group");
        else Assert.True(shell.FindPrefix("zune-new:").Count <= lay.QuickFresh, $"{sizeName}: at most {lay.QuickFresh} new releases");
        if (!shell.Find(ZuneHomeRules.QuickGroupPrefix + ZuneHomeRules.HistoryGroup).IsNull)
        {
            Assert.Equal(lay.HistoryRows, !shell.Find("zune-history-rows").IsNull);
            Assert.Equal(!lay.HistoryRows, !shell.Find("zune-history-tiles").IsNull);
            Assert.True(shell.FindPrefix("zune-history:").Count <= lay.QuickTiles, $"{sizeName}: at most {lay.QuickTiles} plays");
        }
        if (lay.QuickStacked)
        {
            // One column: each group under the one before.
            var pins = scene.AbsoluteRect(Node(shell, ZuneHomeRules.QuickGroupPrefix + ZuneHomeRules.PinsGroup));
            var history = shell.Find(ZuneHomeRules.QuickGroupPrefix + ZuneHomeRules.HistoryGroup);
            if (!history.IsNull) Assert.True(scene.AbsoluteRect(history).Y >= pins.Y + pins.H - 0.5f, $"{sizeName}: the history stands under the pins");
        }

        // A pair stands side by side where the tier says so, stacked where it does not.
        foreach (var (pair, key) in shell.FindPrefix(ZuneHomeRules.PairPrefix))
        {
            var first = scene.FirstChild(pair);
            var second = first.IsNull ? NodeHandle.Null : scene.NextSibling(first);
            if (first.IsNull || second.IsNull) continue;
            var a = scene.AbsoluteRect(first);
            var b = scene.AbsoluteRect(second);
            if (lay.PairBeside) Assert.True(b.X >= a.X + a.W - 0.5f, $"{sizeName}: {key} stands side by side");
            else Assert.True(b.Y >= a.Y + a.H - 0.5f, $"{sizeName}: {key} stands stacked");
        }

        if (tiers.Window == WidthTier.Compact)
            AssertTargets(shell, sizeName, "see-all:", "zune-home-history-link", "zune-home-customize-link", ZuneHomeRules.PinTilePrefix, "zune-daylist-play",
                "zune-chart:", "zune-tile:", "zune-release:", "zune-history:", "zune-cluster-row:");
    }

    [Theory, MemberData(nameof(PageSizes))]
    public void Browses_directory_holds_its_tier_record(string size) => AssertDiscoveryPage(BrowseFixture, size, (shell, tiers) =>
    {
        UntilBackground(shell, () => shell.FindPrefix("zune-plate:").Count > 0, size + ": the directory's plates");
        shell.RunFrames(60);
        var scene = shell.Host.Scene;
        var lay = ZuneBrowseTiers.Of(tiers);
        Assert.Equal(tiers.Content, lay.Tier);

        var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
        var column = scene.AbsoluteRect(Node(shell, ZuneBrowseRules.DirectoryKey));
        AssertWithin(size, "the Browse column", column, content.X, content.X + content.W);
        float left = column.X + lay.Gutter, right = column.X + column.W - lay.Gutter;
        foreach (var (node, key) in shell.FindPrefix(ZuneBrowseRules.BandPrefix))
            AssertWithin(size, key, scene.AbsoluteRect(node), left, right);

        // The charts column beside For you (its width) or above it.
        var charts = shell.Find(ZuneBrowseRules.BandPrefix + "charts");
        var forYou = shell.Find(ZuneBrowseRules.BandPrefix + "foryou");
        if (!charts.IsNull && !forYou.IsNull)
        {
            var c = scene.AbsoluteRect(charts);
            var f = scene.AbsoluteRect(forYou);
            if (lay.ChartsBeside)
            {
                Assert.True(f.X >= c.X + c.W - 0.5f, $"{size}: charts stand beside For you");
                Assert.Equal(lay.ChartsW, c.W, 1f);
            }
            else Assert.True(f.Y >= c.Y + c.H - 0.5f, $"{size}: charts stand above For you");
        }
        // For you is a list, or (Huge) plates; the keys name the arm, never the tier.
        if (!forYou.IsNull)
        {
            Assert.Equal(lay.ForYouPlates, !shell.Find("zune-browse-plates:foryou").IsNull);
            Assert.Equal(!lay.ForYouPlates, !shell.Find("zune-browse-list:foryou").IsNull);
        }
        // The mood plates across the record's columns (the grid fits fewer where the page is narrower).
        var mood = shell.Find("zune-browse-plates:mood");
        if (!mood.IsNull)
        {
            var inMood = new HashSet<NodeHandle>(Subtree(scene, mood));
            var xs = new HashSet<int>();
            foreach (var (n, _) in shell.FindPrefix("zune-plate:"))
            {
                if (!inMood.Contains(n)) continue;
                xs.Add((int)MathF.Round(scene.AbsoluteRect(n).X));
                Assert.Equal(lay.MoodH, scene.AbsoluteRect(n).H, 1f);
            }
            Assert.InRange(xs.Count, 1, lay.MoodCols);
        }

        if (tiers.Window == WidthTier.Compact)
            AssertTargets(shell, size, "zune-plate:", "zune-foryou:", "zune-link:");
    });

    [Theory, MemberData(nameof(PageSizes))]
    public void Searchs_results_page_holds_its_tier_record(string size) => AssertDiscoveryPage(SearchFixture, size, (shell, tiers) =>
    {
        SearchFixture.Wait(shell);
        shell.RunFrames(60);
        var scene = shell.Host.Scene;
        var lay = ZuneSearchTiers.Of(tiers);
        Assert.Equal(tiers.Content, lay.Tier);

        var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
        var page = scene.AbsoluteRect(Node(shell, ZuneSearchRules.PageKey));
        AssertWithin(size, "the Search page", page, content.X, content.X + content.W);
        float left = page.X + lay.Gutter, right = page.X + page.W - lay.Gutter;
        foreach (string key in new[] { "zune-search-top-row", ZuneSearchRules.TopKey, ZuneSearchRules.BestKey, ZuneSearchRules.RailKey, "zune-search-runs" })
            if (!shell.Find(key).IsNull) AssertWithin(size, key, scene.AbsoluteRect(Node(shell, key)), left, right);

        // The top result beside best matches (the record's width) or over them.
        var top = scene.AbsoluteRect(Node(shell, ZuneSearchRules.TopKey));
        var best = scene.AbsoluteRect(Node(shell, ZuneSearchRules.BestKey));
        if (lay.TopBeside)
        {
            Assert.True(best.X >= top.X + top.W - 0.5f, $"{size}: the top result stands beside best matches");
            Assert.Equal(lay.TopW, top.W, 1f);
        }
        else Assert.True(best.Y >= top.Y + top.H - 0.5f, $"{size}: the top result stands over best matches");

        // Its art, and where its name stands.
        var hero = Node(shell, "zune-search-top-hero");
        var heroRect = scene.AbsoluteRect(hero);
        var art = scene.FirstChild(hero);
        var copy = art.IsNull ? NodeHandle.Null : scene.NextSibling(art);
        Assert.False(art.IsNull || copy.IsNull, $"{size}: the hero row holds the art and the copy");
        var artRect = scene.AbsoluteRect(art);
        Assert.Equal(lay.HeroArt, artRect.W, 1f);
        var copyRect = scene.AbsoluteRect(copy);
        if (lay.HeroStacked) Assert.True(copyRect.Y >= artRect.Y + artRect.H - 0.5f, $"{size}: the copy stands under the art");
        else Assert.True(copyRect.X >= artRect.X + artRect.W - 0.5f, $"{size}: the copy stands beside the art");
        var name = scene.AbsoluteRect(Node(shell, "zune-search-top-name"));
        switch (lay.HeroName)
        {
            case ZuneSearchHeroName.Above: Assert.True(name.Y + name.H <= heroRect.Y + 0.5f, $"{size}: the name stands over the art row"); break;
            case ZuneSearchHeroName.Below: Assert.True(name.Y >= heroRect.Y + heroRect.H - 0.5f, $"{size}: the name stands under the art row"); break;
            default: Assert.True(name.X >= copyRect.X - 0.5f && name.Y >= copyRect.Y - 0.5f, $"{size}: the name is the copy's first line"); break;
        }

        // Best matches across the record's columns at most.
        var grid = shell.Find("zune-search-grid");
        if (!grid.IsNull)
        {
            var xs = new HashSet<int>();
            for (var cell = scene.FirstChild(grid); !cell.IsNull; cell = scene.NextSibling(cell)) xs.Add((int)MathF.Round(scene.AbsoluteRect(cell).X));
            Assert.InRange(xs.Count, 1, lay.BestCols);
        }

        if (tiers.Window == WidthTier.Compact)
            AssertTargets(shell, size, "hit:", "zune-search-top-play", "genre:", "rel:", "zune-search-playlists-all");
    });

    // ── the helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mount the app under Zune at <paramref name="sizeName"/> (resizing into it from its <c>From</c> size when it has one), show
    /// <paramref name="page"/>, wait for it, run every frame assertion, then <paramref name="more"/>. The route is built on the UI thread.</summary>
    void AssertDiscoveryPage(DiscoveryFixture page, string sizeName, Action<Shell.HeadlessShell, TierFacts> more)
    {
        var size = Sizes[sizeName];
        ZuneHost.Mounted(host, shell =>
        {
            if (size.Resized)
            {
                shell.RunFrames(20);
                shell.Resize(size.Width, size.Height);
                shell.Host.Paint(0, keepAlive: true);
                shell.Host.RunFrame();
            }
            AssertTier(size);
            shell.Host.RunFrame();
            AssertTier(size);
            try
            {
                Shell.GoTo(page.Route());
                page.Wait(shell);
                shell.RunFrames(60);
                AssertTier(size);
                var tiers = Tiers;
                AssertRecords(shell, size, tiers);
                AssertRegions(shell, size);
                more(shell, tiers);
            }
            finally
            {
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
                shell.RunFrames(10);
            }
        }, size.From ?? size.Width, size.FromHeight ?? size.Height);
    }

    /// <summary>The rect lies inside [<paramref name="from"/>, <paramref name="to"/>] horizontally.</summary>
    static void AssertWithin(string size, string what, RectF r, float from, float to)
    {
        if (r.W <= 0f) return;
        Assert.True(r.X >= from - 1f && r.X + r.W <= to + 1f, $"{size}: {what} runs outside its column horizontally ({r.X}..{r.X + r.W} in {from}..{to})");
    }

    /// <summary>Every node keyed with one of <paramref name="prefixes"/> is a 40 x 40 target at least (Compact).</summary>
    static void AssertTargets(Shell.HeadlessShell shell, string size, params string[] prefixes)
    {
        var scene = shell.Host.Scene;
        foreach (string prefix in prefixes)
            foreach (var (node, key) in shell.FindPrefix(prefix))
            {
                if (scene.InCollapsedSubtree(node)) continue;
                var r = scene.AbsoluteRect(node);
                if (r.W <= 0f || r.H <= 0f) continue;
                Assert.True(r.W >= 39.5f && r.H >= 39.5f, $"{size}: {key} is {r.W} x {r.H}, under 40 x 40");
            }
    }

    // ── the crossings ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Each edge of Zune's ladder both ways at a Regular height (639 / 640, 1007 / 1008, 1919 / 1920, 3199 / 3200; a narrowing one commits 24
    /// below the line), the height edges (700 and the daylist's rung, 480) at Standard, and the Small board's 600 to the Short board's 330.</summary>
    public static TheoryData<float, float, float, float> DiscoveryCrossings() => new()
    {
        { 639f, 760f, 640f, 760f }, { 640f, 760f, 615f, 760f },
        { 1007f, 760f, 1008f, 760f }, { 1008f, 760f, 983f, 760f },
        { 1919f, 760f, 1920f, 760f }, { 1920f, 760f, 1895f, 760f },
        { 3199f, 1440f, 3200f, 1440f }, { 3200f, 1440f, 3175f, 1440f },
        { 1440f, 960f, 1440f, 400f }, { 1440f, 400f, 1440f, 960f },
        { 800f, 600f, 800f, 330f },
    };

    [Theory, MemberData(nameof(DiscoveryCrossings))]
    public void Homes_parts_survive_a_crossing_and_the_daylist_glides(float fromW, float fromH, float toW, float toH)
        => CrossPage(HomeFixture, fromW, fromH, toW, toH, glide: [ZuneHomeRules.DaylistKey]);

    [Theory, MemberData(nameof(DiscoveryCrossings))]
    public void Browses_parts_survive_a_crossing(float fromW, float fromH, float toW, float toH)
        => CrossPage(BrowseFixture, fromW, fromH, toW, toH, glide: []);

    [Theory, MemberData(nameof(DiscoveryCrossings))]
    public void Searchs_parts_survive_a_crossing(float fromW, float fromH, float toW, float toH)
        => CrossPage(SearchFixture, fromW, fromH, toW, toH, glide: []);

    /// <summary>The page is mounted at the from-size, then resized by the plan 3.3 sequence (a resized frame that still lays the OLD tier out, then the first
    /// frame that is not resized, where the crossing commits). The page's parts are the same live nodes after it; the <paramref name="glide"/> parts
    /// whose height changed hold animation tracks (they glide, they do not snap); nothing runs outside the content region once it settles.</summary>
    void CrossPage(DiscoveryFixture page, float fromW, float fromH, float toW, float toH, string[] glide) => Mounted(shell =>
    {
        var scene = shell.Host.Scene;
        try
        {
            shell.RunFrames(20);
            var route = page.Route();
            if (route.Kind != Shell.RouteKind.Home) Shell.GoTo(route);
            page.Wait(shell);
            shell.RunFrames(60);
            var before = Tiers;

            var parts = new List<FramePart>();
            foreach (string key in page.Parts)
            {
                var n = shell.Find(key);
                if (!n.IsNull) parts.Add(new FramePart(key, n, scene.Bounds(n)));
            }
            foreach (string prefix in page.PartPrefixes)
                foreach (var (node, key) in shell.FindPrefix(prefix)) parts.Add(new FramePart(key, node, scene.Bounds(node)));
            Assert.True(parts.Count >= 3, $"{page.Name}: the page's parts are mounted ({parts.Count})");

            // The resized frame: the old tier, laid out at the new size.
            bool narrowing = toW < fromW || toH < fromH;
            shell.Window.Composited = true;
            if (narrowing)
            {
                shell.Window.InModalLoop = true;
                shell.Window.SizedInModalLoop = true;
                shell.Resize(toW, toH);
                shell.Host.Paint(0, keepAlive: true);
                shell.Window.InModalLoop = false;
                shell.Window.SizedInModalLoop = false;
                shell.Host.Paint(0, keepAlive: true);
            }
            else
            {
                shell.Resize(toW, toH);
                shell.Host.Paint(0, keepAlive: true);
            }
            Assert.Equal(before.Window, Tiers.Window);
            Assert.Equal(before.Height, Tiers.Height);
            Assert.Equal(before.Rungs, Tiers.Rungs);
            parts = parts.ConvertAll(p => p with { Local = scene.Bounds(p.Node) });

            // The crossing frame: the new tier commits here.
            if (narrowing) shell.Host.Paint(0, keepAlive: true); else shell.Host.RunFrame();
            var after = Tiers;
            string what = $"{page.Name} {fromW}x{fromH} -> {toW}x{toH}";
            Assert.True(after.Window != before.Window || after.Height != before.Height || after.Rungs != before.Rungs, $"{what}: the crossing committed");

            foreach (var p in parts)
            {
                Assert.True(scene.IsLive(p.Node), $"{what}: {p.Name} was thrown away on the crossing (its node is gone)");
                Assert.True(shell.FindPrefix(p.Name).Exists(f => f.Node.Equals(p.Node)), $"{what}: {p.Name} was remounted on the crossing");
                if (Array.IndexOf(glide, p.Name) < 0 || Design.Reduced) continue;
                var now = scene.Bounds(p.Node);
                if (MathF.Abs(now.H - p.Local.H) <= 0.5f) continue;
                bool glides = false;
                for (var n = p.Node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
                    if (shell.Host.Animation.HasTracks(n)) { glides = true; break; }
                Assert.True(glides, $"{what}: {p.Name} snapped to its new height ({p.Local.H} -> {now.H}) instead of gliding");
            }

            // Settled: the page's parts inside the content region.
            shell.RunFrames(60);
            var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
            foreach (var p in parts)
                AssertWithin(what, p.Name, scene.AbsoluteRect(p.Node), content.X, content.X + content.W);
        }
        finally
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(10);
        }
    }, fromW, fromH);

    // ── the category page ────────────────────────────────────────────────────────────────────────────────────────────────

    static BrowseCategoryProps CategoryFixture() => new()
    {
        Load = BrowseLoad.Ready,
        Title = "Pop",
        PageUri = "spotify:page:pop",
        ScrollKey = "test:zt4:category",
        Mode = BrowsePageLayout.Mode.Shelves,
        Bands =
        [
            new BrowseBand
            {
                Facts = new BrowseSectionFacts("spotify:section:decades", "Decades", BrowseSectionKind.CategoryGrid, CardCount: 0, CategoryCount: 3, Total: 3),
                Tiles =
                [
                    new BrowseCategory("spotify:page:00s", "00s", null),
                    new BrowseCategory("spotify:page:90s", "90s", 0xFF336699u),
                    new BrowseCategory("spotify:page:80s", "80s", null),
                ],
            },
        ],
        Merged = Array.Empty<HomeCard>(),
        Retry = static () => { },
        OpenCategory = static (_, _) => { },
        OpenSection = static _ => { },
        ExploreAll = static () => { },
    };

    [Theory]
    [InlineData(300f, 640f)]
    [InlineData(400f, 860f)]
    [InlineData(800f, 600f)]
    [InlineData(1440f, 960f)]
    [InlineData(2560f, 1440f)]
    [InlineData(3840f, 2160f)]
    public void The_category_page_draws_its_tiers_banner_and_sections_on_the_gutter(float width, float height)
        => ViewHarness.With(host, ViewKeys.BrowseCategory, CategoryFixture(), harness =>
        {
            harness.RunFrames(60);
            var lay = ZuneBrowseTiers.Of(harness.Facts.Peek().Tiers);
            string size = $"{width}x{height}";
            Assert.Equal(ZuneTierAt(width), lay.Tier);

            // The banner at the tier's height, its title and kind inside it.
            var banner = harness.AbsoluteBounds(harness.Node(ZuneBrowseRules.BannerKey));
            Assert.Equal(lay.BannerH, banner.H, 1f);
            var title = harness.AbsoluteBounds(harness.Node(ZuneBrowseRules.BannerTitleKey));
            Assert.True(title.Y >= banner.Y - 0.5f && title.Y < banner.Y + banner.H, $"{size}: the banner's title stands in the banner");

            // The tile block on the gutter, its plates at the mood height across the record's columns at most.
            Assert.True(harness.Contains(ZuneBrowseRules.CategorySectionPrefix + "spotify:section:decades"), $"{size}: the tile block");
            var plates = harness.FindPrefix("zune-plate:");
            Assert.Equal(3, plates.Count);
            var xs = new HashSet<int>();
            foreach (var (node, _) in plates)
            {
                var r = harness.AbsoluteBounds(node);
                Assert.Equal(lay.MoodH, r.H, 1f);
                AssertWithin(size, "a plate", r, lay.Gutter, width - lay.Gutter);
                xs.Add((int)MathF.Round(r.X));
            }
            Assert.InRange(xs.Count, 1, lay.MoodCols);

            // "explore all" is a 40 target at Compact.
            var explore = harness.AbsoluteBounds(harness.Node("zune-browse-explore-all"));
            if (lay.LinkTarget > 0f) Assert.True(explore.W >= 39.5f && explore.H >= 39.5f, $"{size}: explore all is {explore.W} x {explore.H}, under 40 x 40");
        }, new ViewHarnessOptions { Composition = ZuneTheme.Create(), WidthDip = width, HeightDip = height });
}
