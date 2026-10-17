// ── Wavee.ShellTests/TierLayoutTests.Crossing.cs — a tier crossing keeps the parts it keeps (themes plan 3.3; fix/tier-crossing-identity) ──────────────────
//
// The owner's rule: a crossing never throws a part away and builds it fresh. A part that survives keeps its NODE (so its scroll offset, its selection and its
// focus survive with it) and glides into its new box; only a part the new tier genuinely draws as another tree swaps, under the cross-fade. Each fact here
// mounts a page at one size, scrolls it (a long way where it is a list or a grid), focuses what it can, crosses a window step with the sequence plan 3.3
// names (a composited modal drag that settles, or a maximize / restore: a RESIZED frame that keeps the old tier, then the first frame that is not resized,
// where the crossing commits), and then asserts:
//
//   · the surviving parts are the SAME nodes (the same handle, still live): nothing was remounted;
//   · every surviving part that moved or resized on the crossing frame glides there: it, or the box that carries it (the content card, which is the one
//     projection root when the nav pane changes at 528 and 1008), holds animation tracks on that frame;
//   · the scroll offset is kept (a reflowing grid keeps the item at its top), and the focus stays on the same node.
//
// Crossings: 528 both ways (400 <-> 800), 1008 both ways (800 <-> 1440), and for Home 1920 both ways (1440 <-> 2048). Every size is clear of the holds, so
// each resize crosses exactly once; the window and the content ladder cross together (400 is a Compact page, 800 a Small one, 1440 a Standard one, 2048 a
// Huge one).
//
// The rig is `TierResizeTests`'s: the shell host, the headless window with `Composited = true`, `Resize` + a keep-alive paint for a resized frame.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    // ── the crossings ────────────────────────────────────────────────────────────────────────────────────────────────────

    const float CrossH = 760f;

    /// <summary>528 and 1008, each both ways.</summary>
    public static TheoryData<float, float> WindowCrossings() => new()
    {
        { 400f, 800f }, { 800f, 400f },     // 528: Compact <-> Small (the drawer <-> the rail)
        { 800f, 1440f }, { 1440f, 800f },   // 1008: Small <-> Standard (the rail <-> the pane)
    };

    /// <summary>Home's: 528 and 1008 both ways, and 1920 both ways.</summary>
    public static TheoryData<float, float> HomeCrossings()
    {
        var data = WindowCrossings();
        data.Add(1440f, 2048f);   // 1920: Standard <-> Huge (the daylist beside the tiles, the 2400 cap)
        data.Add(2048f, 1440f);
        return data;
    }

    static WidthTier TierAt(float width) => width switch { < 528f => WidthTier.Compact, < 1008f => WidthTier.Small, < 1920f => WidthTier.Standard, _ => WidthTier.Huge };

    /// <summary>The resized frame of a crossing to <paramref name="width"/>: a WIDENING is a maximize (WM_SIZE and one synchronous paint), a NARROWING is a
    /// composited modal edge drag (its ticks paint nothing; the settle paint is the resized frame). Either way the frame lays the OLD tier out at the new size.</summary>
    static void ResizedFrame(Shell.HeadlessShell shell, float width, bool narrowing)
    {
        shell.Window.Composited = true;
        if (narrowing)
        {
            shell.Window.InModalLoop = true;
            shell.Window.SizedInModalLoop = true;
            shell.Resize(width, CrossH);
            shell.Host.Paint(0, keepAlive: true);         // a drag tick: composited, nothing paints
            shell.Window.InModalLoop = false;             // WM_EXITSIZEMOVE
            shell.Window.SizedInModalLoop = false;
            shell.Host.Paint(0, keepAlive: true);         // the settle paint: laid out at the new size, the old tier
        }
        else
        {
            shell.Resize(width, CrossH);
            shell.Host.Paint(0, keepAlive: true);         // maximize: the resized frame keeps the old tier
        }
    }

    /// <summary>The frame that commits the crossing (the first frame that is not resized).</summary>
    static void CrossingFrame(Shell.HeadlessShell shell, bool narrowing)
    {
        if (narrowing) shell.Host.Paint(0, keepAlive: true);
        else shell.Host.RunFrame();
    }

    /// <summary>A part as it stood before the crossing: its node and its box in its parent (the box FLIP compares).</summary>
    readonly record struct Part(string Name, NodeHandle Node, RectF Local);

    static Part PartOf(Shell.HeadlessShell shell, string name, NodeHandle node) => new(name, node, shell.Host.Scene.Bounds(node));

    static Part PartOf(Shell.HeadlessShell shell, string key) => PartOf(shell, key, Node(shell, key));

    /// <summary>Every live node keyed with <paramref name="prefix"/> (and kept by <paramref name="keep"/>), as parts. A key that repeats (a shelf in each
    /// section) is named by its order.</summary>
    static List<Part> PartsOf(Shell.HeadlessShell shell, string prefix, Func<string, bool>? keep = null)
    {
        var parts = new List<Part>();
        foreach (var (node, key) in shell.FindPrefix(prefix))
            if (keep is null || keep(key)) parts.Add(PartOf(shell, key + "#" + parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), node));
        return parts;
    }

    /// <summary>True when <paramref name="node"/> or a box that carries it holds animation tracks: it glides, by its own FLIP or inside its projecting
    /// ancestor's (a crossing that moves the content card snaps the card's descendants onto their final layout under the card's one glide).</summary>
    static bool Glides(Shell.HeadlessShell shell, NodeHandle node)
    {
        var scene = shell.Host.Scene;
        for (var n = node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
            if (shell.Host.Animation.HasTracks(n)) return true;
        return false;
    }

    /// <summary>Re-reads the parts' boxes after the resized frame (the "before" of the crossing frame's FLIP).</summary>
    static List<Part> Restate(Shell.HeadlessShell shell, List<Part> parts)
        => parts.ConvertAll(p => p with { Local = shell.Host.Scene.Bounds(p.Node) });

    /// <summary>On the crossing frame: every part is the same live node, and every part that moved or resized in its parent glides.</summary>
    static void AssertKeptAndGliding(Shell.HeadlessShell shell, IEnumerable<Part> parts, string what)
    {
        var scene = shell.Host.Scene;
        foreach (var p in parts)
        {
            Assert.True(scene.IsLive(p.Node), $"{what}: {p.Name} was thrown away on the crossing (its node is gone)");
            var now = scene.Bounds(p.Node);
            bool moved = MathF.Abs(now.X - p.Local.X) > 0.5f || MathF.Abs(now.Y - p.Local.Y) > 0.5f
                      || MathF.Abs(now.W - p.Local.W) > 0.5f || MathF.Abs(now.H - p.Local.H) > 0.5f;
            if (moved) Assert.True(Glides(shell, p.Node), $"{what}: {p.Name} snapped into its new box ({p.Local} -> {now}) instead of gliding");
        }
    }

    /// <summary>Every part is the same live node (a part that declares no FLIP of its own: its children carry the motion).</summary>
    static void AssertKept(Shell.HeadlessShell shell, IEnumerable<Part> parts, string what)
    {
        foreach (var p in parts) Assert.True(shell.Host.Scene.IsLive(p.Node), $"{what}: {p.Name} was thrown away on the crossing (its node is gone)");
    }

    /// <summary>The same live node is still mounted under <paramref name="key"/>.</summary>
    static void AssertSameNode(Shell.HeadlessShell shell, Part before, string key, string what)
    {
        var now = shell.Find(key);
        Assert.False(now.IsNull, $"{what}: {key} is gone after the crossing");
        Assert.True(now.Equals(before.Node), $"{what}: {key} was remounted on the crossing");
    }

    /// <summary>The content offset of item <paramref name="index"/> of virtual viewport <paramref name="viewport"/> (its row's top); NaN without an extent.</summary>
    static double ItemTop(Shell.HeadlessShell shell, NodeHandle viewport, int index)
        => shell.Host.Scene.ScrollRef(viewport).Extent is { } extent ? extent.OffsetOf(index) : double.NaN;

    static ScrollHandle Handle(Shell.HeadlessShell shell, NodeHandle viewport)
        => shell.Host.TryGetScrollHandle(viewport) ?? throw new InvalidOperationException("no scroll handle on the viewport");

    /// <summary>Scroll <paramref name="viewport"/> to <paramref name="target"/> (held to its extent) and let it rest; the offset it rests at.</summary>
    static double ScrollTo(Shell.HeadlessShell shell, NodeHandle viewport, double target)
    {
        var handle = Handle(shell, viewport);
        handle.ScrollTo(Math.Min(target, handle.MaxOffset), ScrollMove.Immediate);
        shell.RunFrames(5);
        return Handle(shell, viewport).OffsetNow;
    }

    /// <summary>Mount the frame at <paramref name="from"/> on <paramref name="route"/> and wait for <paramref name="ready"/>; the body runs against the settled page.</summary>
    void OnPage(Shell.Route route, string ready, float from, Action<Shell.HeadlessShell> body)
    {
        host.Run(() => Shell.GoTo(route));   // a remount opens on the page the last root showed: the page under test is the frame's only page
        host.Mounted(shell =>
        {
            Until(shell, () => shell.FindPrefix(ready).Count > 0, ready + " on screen");
            shell.RunFrames(90);   // entrances, loads and the pane settle
            body(shell);
        }, from, CrossH);
    }

    static void AssertCrossedTo(float to, string what)
        => Assert.True(Tiers.Window == TierAt(to), $"{what}: the window is {Tiers.Window} after the crossing, not {TierAt(to)}");

    // ── Home: the zones, the shelves, the daylist pair, the page scroll ───────────────────────────────────────────────────

    [Theory, MemberData(nameof(HomeCrossings))]
    public void A_crossing_keeps_homes_zones_and_shelves_and_glides_them(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"Home {from} -> {to}";
        OnPage(new Shell.Route(Shell.RouteKind.Home), "home-shelf:", from, shell =>
        {
            Until(shell, () => shell.FindPrefix("facet:").Count > 0, "the loaded facet column");
            var scroller = Single(shell, "home:scroll:").Node;
            double offset = ScrollTo(shell, scroller, 600.0);
            Assert.True(offset > 50.0, $"{what}: Home scrolled (offset {offset})");

            var column = PartOf(shell, "home:column");
            var zones = PartsOf(shell, "zone:");
            var shelves = PartsOf(shell, "home-shelf:", static k => !k.StartsWith("home-shelf-", StringComparison.Ordinal));
            Assert.NotEmpty(zones);
            Assert.NotEmpty(shelves);
            // The daylist and the Recently played tiles are ONE band at every tier: the band crosses into and out of Huge as the same node.
            var pair = zones.FindAll(static z => z.Name.Contains('+'));

            ResizedFrame(shell, to, narrowing);
            Assert.True(Tiers.Window == TierAt(from), $"{what}: the resized frame already crossed");
            var parts = Restate(shell, [.. zones, .. shelves]);   // the column (no FLIP of its own: its zones carry the motion) is checked for identity
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);
            AssertSameNode(shell, column, "home:column", what);
            foreach (var p in pair) Assert.True(shell.Host.Scene.IsLive(p.Node), $"{what}: the daylist band was rebuilt");

            shell.RunFrames(90);
            Assert.True(Single(shell, "home:scroll:").Node.Equals(scroller), $"{what}: the page scroller was remounted");
            Assert.True(Handle(shell, scroller).OffsetNow > 0.5, $"{what}: the page scroll was reset to the top");
            foreach (var s in shelves) Assert.True(shell.Host.Scene.IsLive(s.Node), $"{what}: {s.Name} did not survive the settle");
        });
    }

    // ── Home's "See all" grid: the grid, its scroll, the item at its top ──────────────────────────────────────────────────

    static readonly Shell.Route SeeAll = Shell.Parse("home-section:spotify:section:wavee-mockup-jump-back-in");

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_crossing_reflows_the_see_all_grid_in_place_and_keeps_its_top_item(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"See all {from} -> {to}";
        OnPage(SeeAll, "home-section-grid", from, shell =>
        {
            var grid = PartOf(shell, "home-section-grid");
            var viewport = ScrollUnder(shell, grid.Node);
            Assert.False(viewport.IsNull, $"{what}: the grid has a viewport");
            if (Handle(shell, viewport).MaxOffset < 100.0) Assert.Skip("the fixture section is too short to scroll at this size");
            double offset = ScrollTo(shell, viewport, 2000.0);   // a long way down
            Assert.True(offset > 50.0, $"{what}: the grid scrolled");
            int anchor = shell.Host.Scene.ScrollRef(viewport).AnchorIndex;
            // Where the anchor item (the first item of the first FULLY visible row) sits on screen: the re-fit keeps it there.
            double anchorScreen = ItemTop(shell, viewport, anchor) - offset;

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, [grid]);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);
            AssertSameNode(shell, grid, "home-section-grid", what);
            Assert.True(ScrollUnder(shell, grid.Node).Equals(viewport), $"{what}: the grid's viewport was remounted");

            shell.RunFrames(30);
            // The re-fit keeps the anchor item where the user saw it, held at the content's start. A widening that pulls the anchor item up into the new
            // FIRST row (800 -> 1440 at the end of the 12-item fixture: the second row's first item at 4 columns falls into the first row at the resized
            // frame's 7 and the new tier's 5) rests at the top by that very rule: the item at the top is still the one the user saw. Only an anchor the
            // re-fit leaves below its old place must scroll.
            double shown = Handle(shell, viewport).OffsetNow;
            double top = ItemTop(shell, viewport, anchor);
            if (top > anchorScreen + 0.5)
                Assert.True(shown > 0.5, $"{what}: the grid's scroll was reset to the top (item {anchor} at {top}, on screen at {anchorScreen} before)");
            float viewportH = shell.Host.Scene.ScrollRef(viewport).ViewportH;
            Assert.True(top >= shown - 0.5 && top < shown + viewportH,
                $"{what}: item {anchor}, at the grid's top before the crossing, left the screen (its row at {top}, the scroll at {shown}, the viewport {viewportH} tall)");
            // The re-fit keeps the anchor ROW where the user sees it: the item at the top is the old one, or one of its row's new neighbours.
            int now = shell.Host.Scene.ScrollRef(viewport).AnchorIndex;
            Assert.True(Math.Abs(now - anchor) <= 12, $"{what}: the grid's top item jumped from {anchor} to {now}");
        });
    }

    // ── Browse category and Search: the shelves and the rail ──────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_crossing_keeps_a_browse_categorys_shelves(float from, float to)
    {
        Shell.Route route = default;
        host.Run(() => route = BrowseTiles.PageRoute("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", "Pop"));
        bool narrowing = to < from;
        string what = $"Browse category {from} -> {to}";
        OnPage(route, "page:", from, shell =>
        {
            var shelves = PartsOf(shell, "browse-shelf:", static k => !k.StartsWith("browse-shelf-card:", StringComparison.Ordinal));
            var grids = PartsOf(shell, "browse-link-grid");
            if (shelves.Count == 0 && grids.Count == 0) Assert.Skip("the fixture category draws no shelf and no link grid");
            var scroller = ScrollUnder(shell, Node(shell, ClassicFrameLayout.ContentCardKey));
            if (!scroller.IsNull) ScrollTo(shell, scroller, 400.0);

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, [.. shelves, .. grids]);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);
        });
    }

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_crossing_keeps_searchs_rail_and_genre_grid(float from, float to)
    {
        Shell.Route route = default;
        host.Run(() => route = Search.GenreRoute("spotify:genre:", "daft punk"));
        bool narrowing = to < from;
        string what = $"Search {from} -> {to}";
        OnPage(route, "facets", from, shell =>
        {
            var parts0 = PartsOf(shell, "search:rail");
            parts0.AddRange(PartsOf(shell, "search-genres-grid"));
            parts0.AddRange(PartsOf(shell, "search-facet-grid"));
            if (parts0.Count == 0) Assert.Skip("the fixture search draws no rail and no grid");

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, parts0);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);
        });
    }

    // ── Recents: the pivot strip and the focused tab ──────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_crossing_keeps_the_recents_pivot_strip_and_its_focused_tab(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"Recents {from} -> {to}";
        OnPage(new Shell.Route(Shell.RouteKind.Recents), "recents-pivots:", from, shell =>
        {
            var (stripNode, stripKey) = Single(shell, "recents-pivots:");
            var strip = PartOf(shell, stripKey, stripNode);
            var scene = shell.Host.Scene;
            var tab = Subtree(scene, stripNode).FirstOrDefault(n => (scene.Flags(n) & NodeFlags.Focusable) != 0 && !scene.HasScroll(n));
            Assert.False(tab.IsNull, $"{what}: the strip has a focusable tab");
            shell.Host.Input.SetFocus(tab, visual: true);
            shell.RunFrames(2);
            Assert.True(shell.Host.Input.Focused.Equals(tab), $"{what}: the tab took focus");

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, [strip, PartOf(shell, "tab", tab)]);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);
            AssertSameNode(shell, strip, stripKey, what);

            shell.RunFrames(30);
            Assert.True(shell.Host.Input.Focused.Equals(tab), $"{what}: the focused tab lost focus on the crossing");
        });
    }

    // ── Settings: the tab strip, the focused tab, the number box, the page scroll ─────────────────────────────────────────

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_crossing_keeps_settings_tab_strip_focus_and_controls(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"Settings {from} -> {to}";
        host.Run(() => Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings)));
        host.Mounted(shell =>
        {
            Settings.Open(Settings.Tab.Playback);
            Until(shell, () => !shell.Find("settings:scroll:playback").IsNull, "the Playback tab");
            shell.RunFrames(90);
            var scene = shell.Host.Scene;

            var tabs = PartOf(shell, "settings:tabs");
            var tab = Subtree(scene, tabs.Node).FirstOrDefault(n => (scene.Flags(n) & NodeFlags.Focusable) != 0 && !scene.HasScroll(n));
            Assert.False(tab.IsNull, $"{what}: the strip has a focusable tab");
            var column = PartOf(shell, "settings:scroll:playback");
            var crossfade = shell.Find("crossfade-seconds");
            var parts0 = new List<Part> { tabs, column, PartOf(shell, "tab", tab) };
            if (!crossfade.IsNull) parts0.Add(PartOf(shell, "crossfade-seconds", crossfade));

            var viewport = ScrollUnder(shell, column.Node);
            double offset = viewport.IsNull ? 0.0 : ScrollTo(shell, viewport, 400.0);
            shell.Host.Input.SetFocus(tab, visual: true);
            shell.RunFrames(2);

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, parts0);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);
            AssertSameNode(shell, tabs, "settings:tabs", what);
            if (!crossfade.IsNull) Assert.True(shell.Find("crossfade-seconds").Equals(crossfade), $"{what}: the crossfade number box was remounted");

            shell.RunFrames(30);
            Assert.True(shell.Host.Input.Focused.Equals(tab), $"{what}: the focused tab lost focus on the crossing");
            if (offset > 50.0) Assert.True(Handle(shell, viewport).OffsetNow > 0.5, $"{what}: the tab's scroll was reset to the top");
        }, from, CrossH);
    }

    // ── the log viewer: the list, its scroll, one line <-> two lines in place ─────────────────────────────────────────────

    [Theory]
    [InlineData(400f, 800f)]
    [InlineData(800f, 400f)]
    [InlineData(800f, 1440f)]
    [InlineData(1440f, 800f)]
    public void A_crossing_re_forms_the_log_rows_in_place_and_keeps_the_list(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"Logs {from} -> {to}";
        OnPage(new Shell.Route(Shell.RouteKind.Logs), "logs:list:", from, shell =>
        {
            var (listNode, listKey) = Single(shell, "logs:list:");
            var list = PartOf(shell, listKey, listNode);
            var header = PartOf(shell, "logs:colhead");
            var viewport = ScrollUnder(shell, listNode);
            Assert.False(viewport.IsNull, $"{what}: the list has a viewport");
            double offset = ScrollTo(shell, viewport, 600.0);

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, [list, header]);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);
            AssertSameNode(shell, list, listKey, what);   // the row form is not in the list's key
            AssertSameNode(shell, header, "logs:colhead", what);

            shell.RunFrames(60);
            Assert.True(ScrollUnder(shell, listNode).Equals(viewport), $"{what}: the list's viewport was remounted");
            if (offset > 50.0) Assert.True(Handle(shell, viewport).OffsetNow > 0.5, $"{what}: the list's scroll was reset to the top");
            // The column header belongs to the one-line rows: folded to no height in the two-line form, its full height in the other.
            bool twoLine = Wavee.Themes.Common.CommonLogsLayout.For(Tiers).TwoLineRows;
            float h = shell.Host.Scene.Bounds(header.Node).H;
            Assert.True(twoLine ? h < 0.5f : h > 0.5f, $"{what}: the column header is {h} tall in the {(twoLine ? "two" : "one")}-line form");
        });
    }

    // ── the artist page: the shelves, the chart, the discography grid, the page scroll ───────────────────────────────────

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_crossing_keeps_the_artist_pages_shelves_chart_and_discography(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"Artist {from} -> {to}";
        OnPage(ArtistPage.Route, "artist-page:", from, shell =>
        {
            // The discography draws a different tree at Compact (C-Artist-Compact: each facet a strip of album cards, `disco-arm:strip`) than above it (the
            // inline grid with its drawer, `disco-arm:grid`): a crossing at 528 swaps the arm under the cross-fade BY DESIGN, so neither the grid nor the
            // strip's shelf survives it. A crossing that keeps the arm (1008) keeps the grid. Every other shelf, and the chart, survives every crossing.
            bool armSwaps = (TierAt(from) == WidthTier.Compact) != (TierAt(to) == WidthTier.Compact);
            string armBefore = TierAt(from) == WidthTier.Compact ? "disco-arm:strip" : "disco-arm:grid";
            string armAfter = TierAt(to) == WidthTier.Compact ? "disco-arm:strip" : "disco-arm:grid";
            var arms = shell.FindPrefix("disco-arm:");
            foreach (var (_, key) in arms) Assert.True(string.Equals(key, armBefore, StringComparison.Ordinal), $"{what}: the discography draws {key} at {from}, not {armBefore}");

            var parts0 = PartsOf(shell, "shelf", static k => k == "shelf").FindAll(p => !UnderAny(shell, p.Node, arms));   // a strip arm's shelf is the arm's
            parts0.AddRange(PartsOf(shell, "chart-shelf:"));
            if (!armSwaps) parts0.AddRange(PartsOf(shell, "disco-lazy"));
            Assert.NotEmpty(parts0);
            var scroller = Single(shell, "artist-scroll:").Node;
            double offset = ScrollTo(shell, scroller, 800.0);

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, parts0);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            AssertKeptAndGliding(shell, parts, what);

            shell.RunFrames(60);
            Assert.True(Single(shell, "artist-scroll:").Node.Equals(scroller), $"{what}: the page scroller was remounted");
            if (offset > 50.0) Assert.True(Handle(shell, scroller).OffsetNow > 0.5, $"{what}: the page scroll was reset to the top");
            foreach (var p in parts) Assert.True(shell.Host.Scene.IsLive(p.Node), $"{what}: {p.Name} did not survive the settle");

            // The arm: kept (the same nodes) where the tier keeps it, swapped for the other one (the old arm gone after its fade) where it does not.
            var armsNow = shell.FindPrefix("disco-arm:");
            Assert.Equal(arms.Count, armsNow.Count);
            foreach (var (_, key) in armsNow) Assert.True(string.Equals(key, armAfter, StringComparison.Ordinal), $"{what}: the discography draws {key} at {to}, not {armAfter}");
            foreach (var (node, key) in arms)
                Assert.True(shell.Host.Scene.IsLive(node) != armSwaps,
                    armSwaps ? $"{what}: {key} outlived its cross-fade" : $"{what}: {key} was rebuilt on a crossing that keeps it");
        });
    }

    /// <summary>True when <paramref name="node"/> is one of <paramref name="roots"/> or inside one.</summary>
    static bool UnderAny(Shell.HeadlessShell shell, NodeHandle node, List<(NodeHandle Node, string Key)> roots)
    {
        var scene = shell.Host.Scene;
        for (var n = node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
            foreach (var (root, _) in roots)
                if (n.Equals(root)) return true;
        return false;
    }

    // ── the release notes page (the loaded fixture): the picker, the cards, the sections ──────────────────────────────────
    //
    // A view mounted alone has no frame root, so the crossing is staged the way the frame stages it: the window resizes and one frame lays the OLD tier out
    // at the new size, then the facts cross and the next frame (not resized) commits.

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_crossing_keeps_the_release_notes_picker_cards_and_sections(float from, float to)
    {
        string what = $"Release notes {from} -> {to}";
        ViewHarness.With(host, ViewKeys.WhatsNewPage, RnPage(), harness =>
        {
            harness.RunFrames(60);
            var shell = harness.Shell;
            var before = ClassicReleaseNotesTiers.Of(harness.Facts.Peek().Tiers);
            // What only has to SURVIVE (it declares no FLIP: its cells carry the motion), and what must also GLIDE when it moves (the section cells, the
            // picker's slot, the row cards: the cells and the row cards FLIP position and size, the slot position).
            var kept0 = new List<Part> { PartOf(shell, "rn:head"), PartOf(shell, "rn:title"), PartOf(shell, "secgrid:0.3.0") };
            kept0.AddRange(PartsOf(shell, "sec:0.3.0:"));
            var parts0 = PartsOf(shell, "secw:");
            Assert.NotEmpty(parts0);
            if (!before.HasRail) { kept0.Add(PartOf(shell, "rn:picker")); parts0.Add(PartOf(shell, "rn:picker-slot")); }
            bool rowCards = before.HighlightForm != ClassicReleaseHighlightForm.Strip;
            if (rowCards) parts0.AddRange(PartsOf(shell, "hl:"));
            var body = harness.Node("rn:body");
            var viewport = ScrollUnder(shell, body);
            double offset = viewport.IsNull ? 0.0 : ScrollTo(shell, viewport, 300.0);

            shell.Resize(to, CrossH);
            shell.Host.Paint(0, keepAlive: true);   // the resized frame: the old tier at the new size
            var parts = Restate(shell, parts0);
            harness.Resize(to, CrossH);             // the facts cross (the window is already this size: the next frame is not resized)
            shell.Host.RunFrame();                  // the crossing frame
            var after = ClassicReleaseNotesTiers.Of(harness.Facts.Peek().Tiers);
            Assert.NotEqual(before.Tier, after.Tier);

            // The head, the title, the sections' grid and every section survive any crossing; the picker survives one that keeps it (528), and the row
            // cards one that keeps their form (528). The rail <-> picker and the poster strip <-> row cards (1008) are genuine swaps: they cross-fade.
            bool pickerKept = !before.HasRail && !after.HasRail;
            bool cardsKept = rowCards && after.HighlightForm != ClassicReleaseHighlightForm.Strip;
            bool Survives(Part p) => (pickerKept || !p.Name.StartsWith("rn:picker", StringComparison.Ordinal))
                                  && (cardsKept || !p.Name.StartsWith("hl:", StringComparison.Ordinal));
            AssertKept(shell, kept0.FindAll(Survives), what);
            AssertKeptAndGliding(shell, parts.FindAll(Survives), what);

            harness.RunFrames(60);
            if (offset > 50.0) Assert.True(Handle(shell, viewport).OffsetNow > 0.5, $"{what}: the page scroll was reset to the top");
        }, new ViewHarnessOptions { Composition = ClassicTheme.Create(), WidthDip = from, HeightDip = CrossH });
    }
}
