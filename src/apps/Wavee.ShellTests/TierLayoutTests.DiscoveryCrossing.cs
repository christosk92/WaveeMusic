// ── Wavee.ShellTests/TierLayoutTests.DiscoveryCrossing.cs — Browse's Charts band and Search's Best matches across a tier crossing (themes plan 3.3) ──────
//
// Two discovery parts that used to snap on a crossing (fix/classic-gap-discovery):
//
//   · Browse > Charts draws a paged deck at Standard and Huge and a stack of full-width tiles at Compact and Small (C-Browse, C-Browse-Small,
//     C-Browse-Compact). The two forms are different trees, so the crossing between them cross-fades one into the other (ClassicTierMotion.SwapFade) while
//     the band around them reflows its height; every band keeps its node and glides, and a crossing that keeps the form (528) keeps the form's node.
//   · Search > Best matches re-forms its rows in place: the shelf and its rows are the same nodes after the crossing, and each row is the record's row for
//     the tier it is drawn in (Compact's 60, the tuned 64: C-Search-Compact, C-Search).
//
// The rig is TierLayoutTests.Crossing's: the frame mounted at one width, a resized frame that keeps the old tier, then the first frame that is not resized,
// where the crossing commits. The window crossings (528, 1008) move the content card, which is then the one projection root (its descendants ride its glide);
// the content-only crossing (the Standard window between 1100 and 1500, where only the content tier changes) runs the parts' own recipes, so there the band's
// reflow and the forms' cross-fade are asserted on the nodes themselves.

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
    // ── Browse: the Charts band ──────────────────────────────────────────────────────────────────────────────────────────

    static string ChartsFormKey(ClassicBrowseLayout layout) => layout.Charts.Stacked ? ClassicBrowseTiers.ChartsStackKey : ClassicBrowseTiers.ChartsDeckKey;

    /// <summary>The Charts band's form on screen is the record's: the record's form is mounted, the other one is not (an exit orphan that outlived its fade
    /// would be a leftover).</summary>
    static void AssertChartsForm(Shell.HeadlessShell shell, ClassicBrowseLayout layout, string what)
    {
        string want = ChartsFormKey(layout);
        string other = layout.Charts.Stacked ? ClassicBrowseTiers.ChartsDeckKey : ClassicBrowseTiers.ChartsStackKey;
        Assert.False(shell.Find(want).IsNull, $"{what}: the Charts band draws no {want} at {layout.Tier}");
        Assert.True(shell.Find(other).IsNull, $"{what}: the Charts band still draws {other} at {layout.Tier}");
    }

    /// <summary>Browse on screen at <paramref name="from"/>, settled with the Charts form composed (the band's core host answers after the directory).</summary>
    void OnBrowse(float from, Action<Shell.HeadlessShell> body)
        => OnPage(new Shell.Route(Shell.RouteKind.Browse), "browse-band:charts", from, shell =>
        {
            Until(shell, () => !shell.Find(ClassicBrowseTiers.ChartsDeckKey).IsNull || !shell.Find(ClassicBrowseTiers.ChartsStackKey).IsNull, "the Charts form");
            shell.RunFrames(30);
            body(shell);
        });

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_window_crossing_keeps_the_browse_bands_and_swaps_the_charts_form_only_where_the_record_does(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"Browse {from} -> {to}";
        OnBrowse(from, shell =>
        {
            var before = ClassicBrowseTiers.Of(Tiers);
            AssertChartsForm(shell, before, what + " (before)");
            var form = PartOf(shell, ChartsFormKey(before));
            var bands = PartsOf(shell, "browse-band:");
            Assert.NotEmpty(bands);

            ResizedFrame(shell, to, narrowing);
            Assert.True(Tiers.Window == TierAt(from), $"{what}: the resized frame already crossed");
            var parts = Restate(shell, bands);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            var after = ClassicBrowseTiers.Of(Tiers);

            // Every band is the same node and glides (by its own reflow, or inside the content card's glide).
            AssertKeptAndGliding(shell, parts, what);
            // A crossing that keeps the form (528: the stack at Compact and Small) keeps the form's node: it re-fits, it is not rebuilt.
            if (before.Charts.Stacked == after.Charts.Stacked) AssertSameNode(shell, form, ChartsFormKey(after), what);

            shell.RunFrames(60);
            foreach (var p in parts) Assert.True(shell.Host.Scene.IsLive(p.Node), $"{what}: {p.Name} did not survive the settle");
            AssertChartsForm(shell, after, what + " (after)");
        });
    }

    /// <summary>The Standard window on both sides of the content edge between Small and Standard (the pane beside the card): only the content tier crosses,
    /// so the card stays put and the band's own recipes run.</summary>
    public static TheoryData<float, float> ContentCrossings() => new() { { 1100f, 1500f }, { 1500f, 1100f } };

    [Theory, MemberData(nameof(ContentCrossings))]
    public void A_content_crossing_cross_fades_the_charts_deck_and_stack_and_glides_the_bands(float from, float to)
    {
        bool narrowing = to < from;
        string what = $"Browse charts {from} -> {to}";
        OnBrowse(from, shell =>
        {
            var before = ClassicBrowseTiers.Of(Tiers);
            var form = PartOf(shell, ChartsFormKey(before));
            var band = PartOf(shell, "browse-band:charts");
            var bands = PartsOf(shell, "browse-band:");

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, bands);
            CrossingFrame(shell, narrowing);
            var after = ClassicBrowseTiers.Of(Tiers);
            if (before.Charts.Stacked == after.Charts.Stacked)
                Assert.Skip($"{what}: the content tier did not cross the deck/stack edge here ({before.Tier} -> {after.Tier}; the pane may be collapsed)");
            Assert.True(Tiers.Window == WidthTier.Standard, $"{what}: the window left Standard ({Tiers.Window})");

            // The band is the same node and reflows on its own (the card did not move): its height change glides, so the bands below are pushed along a
            // glide, never a jump.
            AssertKeptAndGliding(shell, parts, what);
            AssertSameNode(shell, band, "browse-band:charts", what);
            Assert.True(shell.Host.Animation.HasTracks(band.Node), $"{what}: the Charts band snapped to its new height (no reflow tracks)");

            // The swap is a cross-fade, not a snap: the new form fades in while the old one is still on screen, fading out.
            var incoming = shell.Find(ChartsFormKey(after));
            Assert.False(incoming.IsNull, $"{what}: the new form {ChartsFormKey(after)} is not mounted on the crossing frame");
            Assert.True(AnyTracksUnder(shell, incoming), $"{what}: the new form appeared without its fade");
            Assert.True(shell.Host.Scene.IsLive(form.Node), $"{what}: the old form vanished on the crossing frame (no fade out)");
            Assert.True(shell.Host.Animation.HasTracks(form.Node), $"{what}: the old form left without its fade");

            shell.RunFrames(60);
            Assert.False(shell.Host.Scene.IsLive(form.Node), $"{what}: the old form outlived its cross-fade");
            AssertChartsForm(shell, after, what + " (after)");
        });
    }

    // ── Search: Best matches ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The first column's top two rows of the mounted Best matches shelf (items 0 and 1: the shelf lays its pages out column by column, so they stay
    /// on the first page at every column count), as parts.</summary>
    static List<Part> BestMatchRows(Shell.HeadlessShell shell, NodeHandle shelf)
    {
        var scene = shell.Host.Scene;
        var under = new HashSet<NodeHandle>(Subtree(scene, shelf));
        var rows = shell.FindPrefix("hit:").Where(r => under.Contains(r.Node)).Select(r => (r.Node, r.Key, Rect: scene.AbsoluteRect(r.Node))).ToList();
        if (rows.Count == 0) return [];
        float left = rows.Min(static r => r.Rect.X);
        return rows.Where(r => r.Rect.X < left + 0.5f).OrderBy(static r => r.Rect.Y).Take(2).Select(r => PartOf(shell, r.Key, r.Node)).ToList();
    }

    /// <summary>Each row is the record's row for its tier: the shelf's row height (Compact's 60, the tuned 64).</summary>
    static void AssertBestMatchRows(Shell.HeadlessShell shell, IEnumerable<Part> rows, ClassicSearchLayout layout, string what)
    {
        foreach (var r in rows)
        {
            float h = shell.Host.Scene.Bounds(r.Node).H;
            Assert.True(MathF.Abs(h - layout.BestMatchRowH) <= 1f, $"{what}: {r.Name} is {h} tall, not the {layout.Tier} row's {layout.BestMatchRowH}");
        }
    }

    /// <summary>The page grid the shelf shows: the columns that lie whole inside its viewport, and the rows in the first of them. A page holds
    /// <c>columns x rows</c> items laid out column by column, so the first column of the page is full unless the list runs out first.</summary>
    static (int Columns, int Rows, int Shown) BestMatchGrid(Shell.HeadlessShell shell, NodeHandle shelf)
    {
        var scene = shell.Host.Scene;
        var viewport = ScrollUnder(shell, shelf);
        Assert.False(viewport.IsNull, "Best matches has a viewport");
        var vr = scene.AbsoluteRect(viewport);
        var under = new HashSet<NodeHandle>(Subtree(scene, shelf));
        var shown = shell.FindPrefix("hit:").Where(r => under.Contains(r.Node)).Select(r => scene.AbsoluteRect(r.Node))
            .Where(r => r.W > 0f && r.X >= vr.X - 0.5f && r.X + r.W <= vr.X + vr.W + 0.5f).ToList();
        if (shown.Count == 0) return (0, 0, 0);
        int columns = shown.Select(static r => MathF.Round(r.X)).Distinct().Count();
        float left = shown.Min(static r => r.X);
        return (columns, shown.Count(r => r.X < left + 0.5f), shown.Count);
    }

    /// <summary>The rows per page follow the record for the column count on screen (three at one column, two at two, three at three).</summary>
    static void AssertBestMatchPaging(Shell.HeadlessShell shell, NodeHandle shelf, ClassicSearchLayout layout, string what)
    {
        var (columns, rows, shown) = BestMatchGrid(shell, shelf);
        Assert.True(columns > 0, $"{what}: Best matches shows no whole column");
        int want = layout.BestMatchRowsFor(columns);
        if (shown < columns * want && rows < want) return;   // a short list: the page is not full, its first column may hold fewer
        Assert.True(rows == want, $"{what}: Best matches pages {rows} rows at {columns} columns, the record says {want}");
    }

    [Theory, MemberData(nameof(WindowCrossings))]
    public void A_crossing_re_forms_the_best_matches_rows_in_place(float from, float to)
    {
        Shell.Route route = default;
        host.Run(() => route = Search.GenreRoute("spotify:genre:", "daft punk"));
        bool narrowing = to < from;
        string what = $"Best matches {from} -> {to}";
        OnPage(route, "facets", from, shell =>
        {
            shell.RunFrames(30);
            var shelfNode = shell.Find(ClassicSearchTiers.BestMatchesKey);
            if (shelfNode.IsNull) Assert.Skip("the fixture search draws no Best matches");
            var shelf = PartOf(shell, ClassicSearchTiers.BestMatchesKey, shelfNode);
            var rows = BestMatchRows(shell, shelfNode);
            Assert.NotEmpty(rows);
            var before = ClassicSearchTiers.Of(Tiers);
            AssertBestMatchRows(shell, rows, before, what + " (before)");
            AssertBestMatchPaging(shell, shelfNode, before, what + " (before)");

            ResizedFrame(shell, to, narrowing);
            var parts = Restate(shell, [shelf, .. rows]);
            CrossingFrame(shell, narrowing);
            AssertCrossedTo(to, what);
            var after = ClassicSearchTiers.Of(Tiers);

            // The shelf and its rows are the same nodes (one key at every tier, the row form a live read of the tier), and what moved or resized glides.
            AssertKeptAndGliding(shell, parts, what);
            AssertSameNode(shell, shelf, ClassicSearchTiers.BestMatchesKey, what);

            shell.RunFrames(60);
            foreach (var r in rows) Assert.True(shell.Host.Scene.IsLive(r.Node), $"{what}: {r.Name} was rebuilt after the crossing");
            AssertBestMatchRows(shell, rows, after, what + " (after)");
            // The rows per page are a live prop: the SAME shelf re-pages to the record's count for its new column count (no remount).
            Assert.True(shell.Find(ClassicSearchTiers.BestMatchesKey).Equals(shelfNode), $"{what}: the Best matches shelf was remounted");
            AssertBestMatchPaging(shell, shelfNode, after, what + " (after)");
        });
    }
}
