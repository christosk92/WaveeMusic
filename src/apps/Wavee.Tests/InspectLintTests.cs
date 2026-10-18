// ── Wavee.Tests/InspectLintTests.cs — the `--inspect` lint rules on hand-built snapshots (agent-inspect plan, lane A3) ───────────
//
// Pins the pure half of the lint (`Screens/Diagnostics.Inspect.Lint.cs`, `Diagnostics.Inspect.Lint.Scene.cs` option parsing and report writing):
// each rule on a tree small enough to read, the severities, the measured and expected numbers, the options (rules, min, cap, floors, allow lists)
// and the JSON shape of a finding. No engine, no host, no thread: the nodes are built by hand.

using System.Text.Json;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using I = Wavee.Diagnostics.Inspect;
using L = Wavee.Diagnostics.InspectLint;

namespace Wavee.Tests;

public class InspectLintTests
{
    // ── the builders ────────────────────────────────────────────────────────────────────────────────────────────────────

    static L.Node Box(int i, int parent, float x, float y, float w, float h, string? key = null)
        => new(i, parent, key, "p" + i, new RectF(x, y, w, h));

    static L.Node Words(int i, int parent, float x, float y, float w, float h, string text, float size, string? key = null, Func<L.TextRun, L.TextRun>? run = null)
    {
        L.TextRun t = new(text, size);
        if (run is not null) t = run(t);
        return Box(i, parent, x, y, w, h, key) with { Text = t, TextCount = t.Glyph ? 0 : 1 };
    }

    static L.Node Target(int i, int parent, float x, float y, float w, float h, string? key, string label = "", string role = "Button")
        => Box(i, parent, x, y, w, h, key) with { Interactive = true, Label = label, Role = role };

    static L.Snapshot Snap(WidthTier tier, params L.Node[] nodes) => new(1000f, 800f, tier, nodes);

    static L.Snapshot Snap(params L.Node[] nodes) => Snap(WidthTier.Standard, nodes);

    static L.Options Only(params string[] rules) => new() { Rules = rules };

    static List<L.Finding> Of(L.Report report, string rule) => report.Findings.Where(f => f.Rule == rule).ToList();

    static double Value(IReadOnlyList<L.Metric> metrics, string name) => metrics.First(m => m.Name == name).Value;

    // ── gutter and right-edge ───────────────────────────────────────────────────────────────────────────────────────────

    static readonly LintEdge s_pivotEdge = new(L.Rules.Gutter, "band.first-pivot", "the band's first pivot", LintSide.Left, 48f, 1f, Prefixes: ["pivot:"]);

    [Fact]
    public void The_leftmost_matching_node_is_measured_against_the_expected_x()
    {
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 80, 10, 60, 40, "pivot:a"), Box(2, 0, 200, 10, 60, 40, "pivot:b")) with { Edges = [s_pivotEdge] };
        L.Report report = L.Run(snap, Only(L.Rules.Gutter));
        L.Finding f = Assert.Single(report.Findings);
        Assert.Equal(L.Severity.Error, f.Severity);
        Assert.Equal(["pivot:a"], f.Keys);
        Assert.Equal(80d, Value(f.Measured, "x"));
        Assert.Equal(32d, Value(f.Measured, "delta"));
        Assert.Equal(48d, Value(f.Expected, "x"));
        Assert.Contains("80", f.Message);
        Assert.Contains("48", f.Message);
        Assert.Equal(1, report.Checked[L.Rules.Gutter]);
    }

    [Fact]
    public void An_edge_within_its_tolerance_passes_and_one_nothing_matches_is_not_checked()
    {
        var onGutter = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 48.6f, 10, 60, 40, "pivot:a")) with { Edges = [s_pivotEdge] };
        L.Report ok = L.Run(onGutter, Only(L.Rules.Gutter));
        Assert.Empty(ok.Findings);
        Assert.Equal(1, ok.Checked[L.Rules.Gutter]);

        var none = Snap(Box(0, -1, 0, 0, 1000, 800)) with { Edges = [s_pivotEdge] };
        L.Report skipped = L.Run(none, Only(L.Rules.Gutter));
        Assert.Empty(skipped.Findings);
        Assert.Equal(0, skipped.Checked[L.Rules.Gutter]);
    }

    [Fact]
    public void An_advisory_edge_is_a_warning_and_a_node_off_the_window_is_not_an_edge()
    {
        LintEdge advisory = s_pivotEdge with { Advisory = true };
        var miss = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 80, 10, 60, 40, "pivot:a")) with { Edges = [advisory] };
        Assert.Equal(L.Severity.Warn, Assert.Single(L.Run(miss, Only(L.Rules.Gutter)).Findings).Severity);

        var scrolledAway = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, -300, 10, 60, 40, "pivot:a"), Box(2, 0, 48, 10, 60, 40, "pivot:b")) with { Edges = [s_pivotEdge] };
        Assert.Empty(L.Run(scrolledAway, Only(L.Rules.Gutter)).Findings);   // the pivot at -300 is off the window; the one at 48 is the edge
    }

    [Fact]
    public void The_right_edge_rule_measures_the_rightmost_node_and_runs_only_its_own_edges()
    {
        var edge = new LintEdge(L.Rules.RightEdge, "band.last-action", "the band's last row-2 action", LintSide.Right, 952f, 1f, Prefixes: ["action:"]);
        var gutterEdge = s_pivotEdge;
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 900, 10, 68, 32, "action:play"), Box(2, 0, 700, 10, 60, 32, "action:save"), Box(3, 0, 80, 10, 60, 40, "pivot:a"))
            with { Edges = [edge, gutterEdge] };
        L.Report report = L.Run(snap, Only(L.Rules.RightEdge));
        L.Finding f = Assert.Single(report.Findings);
        Assert.Equal(L.Rules.RightEdge, f.Rule);
        Assert.Equal(["action:play"], f.Keys);
        Assert.Equal(968d, Value(f.Measured, "x"));
        Assert.False(report.Checked.ContainsKey(L.Rules.Gutter));
    }

    [Fact]
    public void A_glyphs_only_edge_looks_under_its_key_at_icon_text_alone()
    {
        var edge = new LintEdge(L.Rules.RightEdge, "playbar.last-glyph", "the play bar's last glyph", LintSide.Right, 976f, 1f, UnderKey: "shell.playerbar", GlyphsOnly: true);
        Func<L.TextRun, L.TextRun> glyph = static t => t with { Glyph = true };
        var snap = Snap(
            Box(0, -1, 0, 0, 1000, 800),
            Box(1, 0, 0, 720, 1000, 72, "shell.playerbar"),
            Words(2, 1, 900, 740, 16, 16, "", 16, run: glyph),
            Words(3, 1, 960, 740, 16, 16, "", 16, run: glyph),
            Words(4, 1, 980, 740, 20, 16, "1:23", 13),
            Words(5, 0, 990, 100, 16, 16, "", 16, run: glyph)) with { Edges = [edge] };
        L.Report report = L.Run(snap, Only(L.Rules.RightEdge));
        Assert.Empty(report.Findings);   // 960 + 16 = 976: the plain "1:23" and the glyph outside the bar are not candidates
        Assert.Equal(1, report.Checked[L.Rules.RightEdge]);
    }

    // ── clip ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Text_taller_than_the_box_that_clips_it_is_a_vertical_error()
    {
        var snap = Snap(
            Box(0, -1, 0, 0, 1000, 800),
            Box(1, 0, 0, 0, 300, 50, "title-box") with { Clips = true },
            Words(2, 1, 0, 0, 300, 60, "Settings", 40, key: "title"));
        L.Report report = L.Run(snap, Only(L.Rules.Clip));
        L.Finding f = Assert.Single(report.Findings);
        Assert.Equal(L.Severity.Error, f.Severity);
        Assert.Equal(["title", "title-box"], f.Keys);
        Assert.Equal(60d, Value(f.Measured, "inkBottom"));
        Assert.Equal(10d, Value(f.Measured, "over"));
        Assert.Equal(50d, Value(f.Expected, "clipBottom"));
    }

    [Fact]
    public void A_tight_explicit_line_height_clips_descenders_in_a_box_as_tall_as_the_line()
    {
        L.Node box = Box(1, 0, 0, 0, 200, 48) with { Clips = true };
        var tight = Snap(Box(0, -1, 0, 0, 1000, 800), box, Words(2, 1, 0, 0, 200, 48, "Settings", 40, run: static t => t with { LineHeight = 40f }));
        L.Finding f = Assert.Single(L.Run(tight, Only(L.Rules.Clip)).Findings);
        Assert.Equal(L.Severity.Error, f.Severity);
        Assert.Equal(4d, Value(f.Measured, "over"));   // (1.2 x 40 - 40) / 2 = 4 of ink outside the line box, top and bottom

        var natural = Snap(Box(0, -1, 0, 0, 1000, 800), box, Words(2, 1, 0, 0, 200, 48, "Settings", 40));
        Assert.Empty(L.Run(natural, Only(L.Rules.Clip)).Findings);
    }

    [Fact]
    public void An_ancestor_that_scrolls_an_axis_may_cut_on_it_and_so_may_everything_above_it()
    {
        L.Node outer = Box(1, 0, 0, 0, 200, 48) with { Clips = true };
        L.Node scroller = Box(2, 1, 0, 0, 200, 48) with { Clips = true, ScrollAxis = 2 };
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800), outer, scroller, Words(3, 2, 0, 30, 200, 48, "second row", 15));
        Assert.Empty(L.Run(snap, Only(L.Rules.Clip)).Findings);

        var sideways = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 0, 0, 200, 48) with { Clips = true }, Words(2, 1, 150, 0, 100, 20, "wide", 15));
        L.Finding f = Assert.Single(L.Run(sideways, Only(L.Rules.Clip)).Findings);
        Assert.Equal(L.Severity.Warn, f.Severity);   // not a scroller on x: it is cut, but a horizontal cut is a warning
        Assert.Equal(50d, Value(f.Measured, "over"));
    }

    [Fact]
    public void Text_wholly_outside_a_clip_is_hidden_not_clipped()
    {
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 0, 0, 200, 48) with { Clips = true }, Words(2, 1, 0, 60, 200, 20, "below the fold", 15));
        Assert.Empty(L.Run(snap, Only(L.Rules.Clip)).Findings);
    }

    [Fact]
    public void Sibling_boxes_with_text_that_overlap_are_a_warning_unless_their_parent_is_a_zstack()
    {
        L.Node parent = Box(1, 0, 0, 0, 300, 100, "col");
        var flow = Snap(Box(0, -1, 0, 0, 1000, 800), parent, Words(2, 1, 0, 0, 100, 20, "one", 15, key: "a"), Words(3, 1, 0, 10, 100, 20, "two", 15, key: "b"));
        L.Finding f = Assert.Single(L.Run(flow, Only(L.Rules.Clip)).Findings);
        Assert.Equal(L.Severity.Warn, f.Severity);
        Assert.Equal(["a", "b", "col"], f.Keys);
        Assert.Equal(100d, Value(f.Measured, "overlapW"));
        Assert.Equal(10d, Value(f.Measured, "overlapH"));

        var stacked = Snap(Box(0, -1, 0, 0, 1000, 800), parent with { ZStack = true }, Words(2, 1, 0, 0, 100, 20, "one", 15), Words(3, 1, 0, 10, 100, 20, "two", 15));
        Assert.Empty(L.Run(stacked, Only(L.Rules.Clip)).Findings);

        var textless = Snap(Box(0, -1, 0, 0, 1000, 800), parent, Box(2, 1, 0, 0, 100, 20), Box(3, 1, 0, 10, 100, 20));
        Assert.Empty(L.Run(textless, Only(L.Rules.Clip)).Findings);   // two plain boxes (a pile of avatars) are not content
    }

    // ── trim ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Trimmed_text_is_a_warning_with_its_natural_size_against_its_box()
    {
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800),
            Words(1, 0, 0, 0, 100, 20, "A long title", 15, key: "title", run: static t => t with { Trimmed = true, OverflowX = true, NaturalW = 150f, NaturalH = 20f, Trim = "CharacterEllipsis" }));
        L.Report report = L.Run(snap, Only(L.Rules.Trim));
        L.Finding f = Assert.Single(report.Findings);
        Assert.Equal(L.Severity.Warn, f.Severity);
        Assert.Equal(["title"], f.Keys);
        Assert.Equal(100d, Value(f.Measured, "w"));
        Assert.Equal(150d, Value(f.Expected, "w"));
        Assert.Contains("ellipsised", f.Message);
        Assert.Equal(1, report.Checked[L.Rules.Trim]);
    }

    [Fact]
    public void A_key_prefix_on_the_node_or_an_ancestor_allows_trimming()
    {
        Func<L.TextRun, L.TextRun> trimmed = static t => t with { Trimmed = true, OverflowX = true, NaturalW = 150f, NaturalH = 20f };
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 0, 0, 300, 40, "zune-crumbs"), Words(2, 1, 0, 0, 100, 20, "Home", 13, run: trimmed));
        Assert.Single(L.Run(snap, Only(L.Rules.Trim)).Findings);
        Assert.Empty(L.Run(snap, new L.Options { Rules = [L.Rules.Trim], AllowTrim = ["zune-crumbs"] }).Findings);
        Assert.Empty(L.Run(snap, new L.Options { Rules = [L.Rules.Trim], AllowTrim = ["zune-"] }).Findings);
    }

    // ── type ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_type_scale_counts_each_distinct_style_and_a_size_under_the_floor_is_an_error()
    {
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800),
            Words(1, 0, 0, 0, 100, 20, "a", 15, run: static t => t with { LineHeight = 20f }),
            Words(2, 0, 0, 30, 100, 20, "b", 15, run: static t => t with { LineHeight = 20f }),
            Words(3, 0, 0, 60, 200, 36, "c", 30, run: static t => t with { LineHeight = 36f, Weight = 300 }),
            Words(4, 0, 0, 100, 60, 12, "d", 9, key: "tiny"),
            Words(5, 0, 0, 120, 16, 16, "", 8, run: static t => t with { Glyph = true }));   // an icon: not text
        L.Report report = L.Run(snap, Only(L.Rules.Type));
        List<L.Finding> errors = report.Findings.Where(f => f.Severity == L.Severity.Error).ToList();
        L.Finding floor = Assert.Single(errors);
        Assert.Equal(["tiny"], floor.Keys);
        Assert.Equal(9d, Value(floor.Measured, "size"));
        Assert.Equal(11d, Value(floor.Expected, "minSize"));

        List<L.Finding> scale = report.Findings.Where(f => f.Severity == L.Severity.Info).ToList();
        Assert.Equal(3, scale.Count);
        Assert.Equal(30d, Value(scale[0].Measured, "size"));   // largest first
        Assert.Equal(2d, Value(scale.First(f => Value(f.Measured, "size") == 15d).Measured, "count"));
        Assert.Equal(4, report.Checked[L.Rules.Type]);

        Assert.Empty(L.Run(snap, new L.Options { Rules = [L.Rules.Type], TypeFloor = 8f, Min = L.Severity.Warn }).Findings);
    }

    // ── target ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_target_under_40_is_an_error_at_Compact_and_under_32_a_warning_elsewhere()
    {
        var nodes = new[]
        {
            Box(0, -1, 0, 0, 1000, 800),
            Target(1, 0, 0, 0, 32, 32, "small", "Play"),
            Target(2, 0, 100, 0, 40, 40, "big", "Save"),
            Target(3, 0, 200, 0, 24, 24, "tiny", "More"),
            Target(4, 0, 300, 0, 200, 4, "seek", role: "Slider"),
        };
        L.Report compact = L.Run(Snap(WidthTier.Compact, nodes), Only(L.Rules.Target));
        Assert.Equal(["small", "tiny"], compact.Findings.Select(f => f.Keys[0]).Order().ToArray());
        Assert.All(compact.Findings, f => Assert.Equal(L.Severity.Error, f.Severity));
        Assert.Equal(40d, Value(compact.Findings[0].Expected, "w"));

        L.Report standard = L.Run(Snap(WidthTier.Standard, nodes), Only(L.Rules.Target));
        L.Finding f = Assert.Single(standard.Findings);
        Assert.Equal(["tiny"], f.Keys);
        Assert.Equal(L.Severity.Warn, f.Severity);
        Assert.Equal(32d, Value(f.Expected, "w"));
    }

    [Fact]
    public void A_target_that_fills_its_interactive_parent_is_the_parents_and_disabled_ones_are_skipped()
    {
        var snap = Snap(WidthTier.Compact,
            Box(0, -1, 0, 0, 1000, 800),
            Target(1, 0, 0, 0, 30, 30, "outer", "Like"),
            Target(2, 1, 0, 0, 30, 30, "inner"),
            Target(3, 0, 100, 0, 30, 30, "off", "Share") with { Disabled = true });
        L.Report report = L.Run(snap, Only(L.Rules.Target));
        Assert.Equal(["outer"], Assert.Single(report.Findings).Keys);
    }

    [Fact]
    public void The_target_floors_are_options()
    {
        var snap = Snap(WidthTier.Compact, Box(0, -1, 0, 0, 1000, 800), Target(1, 0, 0, 0, 36, 36, "mid", "Play"));
        Assert.Single(L.Run(snap, Only(L.Rules.Target)).Findings);
        Assert.Empty(L.Run(snap, new L.Options { Rules = [L.Rules.Target], TargetFloorCompact = 32f }).Findings);
    }

    // ── duplicate ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_action_on_two_surfaces_is_a_duplicate_and_the_rows_of_one_list_are_not()
    {
        var twice = Snap(Box(0, -1, 0, 0, 1000, 800),
            Target(1, 0, 0, 0, 40, 40, "zune-band-action:play", "Play"),
            Target(2, 0, 100, 0, 40, 40, "zune.detail.ring:play", "play"));
        L.Report report = L.Run(twice, Only(L.Rules.Duplicate));
        L.Finding f = Assert.Single(report.Findings);
        Assert.Equal(L.Severity.Warn, f.Severity);
        Assert.Equal(["zune-band-action:play", "zune.detail.ring:play"], f.Keys);
        Assert.Equal(2d, Value(f.Measured, "count"));
        Assert.Equal(2d, Value(f.Measured, "homes"));

        var rows = Snap(Box(0, -1, 0, 0, 1000, 800),
            Box(1, 0, 0, 0, 500, 60, "row:1"), Target(2, 1, 0, 0, 40, 40, null, "Play"),
            Box(3, 0, 0, 60, 500, 60, "row:2"), Target(4, 3, 0, 60, 40, 40, null, "Play"));
        Assert.Empty(L.Run(rows, Only(L.Rules.Duplicate)).Findings);
    }

    [Fact]
    public void Chrome_is_skipped_and_an_allowed_label_may_repeat()
    {
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800),
            Target(1, 0, 0, 0, 40, 40, "zune-band-action:play", "Play"),
            Box(2, 0, 0, 720, 1000, 72, "shell.playerbar"), Target(3, 2, 100, 724, 40, 40, "transport-play", "Play"));
        Assert.Empty(L.Run(snap, Only(L.Rules.Duplicate)).Findings);

        var two = Snap(Box(0, -1, 0, 0, 1000, 800), Target(1, 0, 0, 0, 40, 40, "a:x", "See all"), Target(2, 0, 100, 0, 40, 40, "b:x", "See all"));
        Assert.Single(L.Run(two, Only(L.Rules.Duplicate)).Findings);
        Assert.Empty(L.Run(two, new L.Options { Rules = [L.Rules.Duplicate], AllowDuplicate = ["see all"] }).Findings);
    }

    // ── rhythm ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stacked_text_blocks_report_their_gaps_and_a_gap_under_two_is_cramped()
    {
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 0, 0, 300, 100, "col"),
            Words(2, 1, 0, 0, 100, 20, "title", 20, key: "t"), Words(3, 1, 0, 21, 100, 16, "meta", 13, key: "m"), Words(4, 1, 0, 45, 100, 16, "more", 13, key: "n"));
        L.Report report = L.Run(snap, Only(L.Rules.Rhythm));
        L.Finding cramped = Assert.Single(report.Findings, f => f.Severity == L.Severity.Warn);
        Assert.Equal(["t", "m"], cramped.Keys);
        Assert.Equal(1d, Value(cramped.Measured, "gap"));
        Assert.Equal(2d, Value(cramped.Expected, "minGap"));

        L.Finding info = Assert.Single(report.Findings, f => f.Severity == L.Severity.Info);
        Assert.Equal(1d, Value(info.Measured, "gap0"));
        Assert.Equal(8d, Value(info.Measured, "gap1"));
        Assert.Equal(2, report.Checked[L.Rules.Rhythm]);
    }

    [Fact]
    public void Text_side_by_side_or_in_a_row_has_no_vertical_rhythm()
    {
        var row = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 0, 0, 300, 40) with { Column = false },
            Words(2, 1, 0, 0, 100, 20, "left", 15), Words(3, 1, 110, 0, 100, 20, "right", 15));
        Assert.Empty(L.Run(row, Only(L.Rules.Rhythm)).Findings);

        var side = Snap(Box(0, -1, 0, 0, 1000, 800), Box(1, 0, 0, 0, 300, 100),
            Words(2, 1, 0, 0, 100, 20, "left", 15), Words(3, 1, 150, 30, 100, 20, "right", 15));
        Assert.Empty(L.Run(side, Only(L.Rules.Rhythm)).Findings);
    }

    // ── contrast ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static ColorF Rgb(int hex) => new(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f, 1f);

    /// <summary>A 100 x 100 frame at 1 px per DIP: <paramref name="ground"/> everywhere, with the rect (x, y, w, h) painted <paramref name="patch"/>.</summary>
    static L.Pixels Frame(int ground, int patch = -1, int px = 0, int py = 0, int pw = 0, int ph = 0)
    {
        var bgra = new byte[100 * 100 * 4];
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
            {
                int c = patch >= 0 && x >= px && x < px + pw && y >= py && y < py + ph ? patch : ground;
                int i = (y * 100 + x) * 4;
                bgra[i] = (byte)(c & 255); bgra[i + 1] = (byte)((c >> 8) & 255); bgra[i + 2] = (byte)((c >> 16) & 255); bgra[i + 3] = 255;
            }
        return new L.Pixels(bgra, 100, 100, 1f);
    }

    static L.Snapshot Painted(L.Pixels frame, params L.Node[] nodes) => new(100f, 100f, WidthTier.Standard, nodes) { Backdrop = frame };

    static L.Node Ink(int i, float size, int ink, string text = "Hello", float alpha = 1f, Func<L.TextRun, L.TextRun>? run = null)
        => Words(i, -1, 10, 10, 60, 20, text, size, run: t => (run?.Invoke(t) ?? t) with { Color = Rgb(ink) with { A = alpha }, NaturalW = 50f });

    [Fact]
    public void The_ratio_is_the_wcag_one_and_composites_the_inks_alpha_over_the_ground()
    {
        Assert.Equal(21d, L.ContrastOf(Rgb(0x000000), Rgb(0xffffff)), 6);
        Assert.Equal(1d, L.ContrastOf(Rgb(0x808080), Rgb(0x808080)), 6);
        Assert.Equal(4.54, L.ContrastOf(Rgb(0x767676), Rgb(0xffffff)), 2);   // the canonical AA pass on white
        Assert.Equal(4.48, L.ContrastOf(Rgb(0x777777), Rgb(0xffffff)), 2);   // and the canonical fail one step lighter
        Assert.Equal(L.ContrastOf(Rgb(0xffffff), Rgb(0x000000)), L.ContrastOf(Rgb(0x000000), Rgb(0xffffff)), 6);   // symmetric in which is lighter
        // Half-transparent black on white is the 50 % grey (about 3.95 : 1), and opacity multiplies the alpha.
        Assert.Equal(L.ContrastOf(new ColorF(0.5f, 0.5f, 0.5f, 1f), Rgb(0xffffff)), L.ContrastOf(Rgb(0x000000) with { A = 0.5f }, Rgb(0xffffff)), 6);
        Assert.Equal(L.ContrastOf(Rgb(0x000000) with { A = 0.5f }, Rgb(0xffffff)), L.ContrastOf(Rgb(0x000000), Rgb(0xffffff), 0.5f), 6);
    }

    [Fact]
    public void Body_text_needs_4_5_and_large_bold_large_and_glyph_runs_need_3()
    {
        var o = new L.Options();
        Assert.Equal(4.5f, L.ContrastNeeded(new L.TextRun("a", 15), o));
        Assert.Equal(4.5f, L.ContrastNeeded(new L.TextRun("a", 23.9f), o));
        Assert.Equal(3f, L.ContrastNeeded(new L.TextRun("a", 24), o));
        Assert.Equal(4.5f, L.ContrastNeeded(new L.TextRun("a", 18.66f) { Weight = 400 }, o));
        Assert.Equal(3f, L.ContrastNeeded(new L.TextRun("a", 18.66f) { Weight = 700 }, o));
        Assert.Equal(3f, L.ContrastNeeded(new L.TextRun("\ue70d", 12) { Glyph = true }, o));
    }

    [Fact]
    public void A_run_under_its_floor_is_a_finding_with_the_ratio_and_the_ground_and_one_over_it_is_not()
    {
        L.Pixels white = Frame(0xffffff);
        L.Report bad = L.Run(Painted(white, Ink(0, 14, 0x777777)), Only(L.Rules.Contrast));
        L.Finding f = Assert.Single(bad.Findings);
        Assert.Equal(L.Severity.Warn, f.Severity);
        Assert.Equal(4.48, Value(f.Measured, "ratio"), 2);
        Assert.Equal(4.5, Value(f.Expected, "minRatio"));
        Assert.Contains("#ffffff", f.Message);
        Assert.Equal(1, bad.Checked[L.Rules.Contrast]);
        Assert.Empty(L.Run(Painted(white, Ink(0, 14, 0x767676)), Only(L.Rules.Contrast)).Findings);
        // 3.0 : 1 is enough at 28 DIP and not at 15.
        Assert.Empty(L.Run(Painted(white, Ink(0, 28, 0x949494)), Only(L.Rules.Contrast)).Findings);
        Assert.Single(L.Run(Painted(white, Ink(0, 15, 0x949494)), Only(L.Rules.Contrast)).Findings);
    }

    [Fact]
    public void The_ground_is_what_the_rendered_frame_paints_under_the_run_and_alpha_and_opacity_dim_the_ink()
    {
        // The same ink passes on white (5.7 : 1) and fails on the grey the frame paints under the run (3.0 : 1).
        L.Pixels grey = Frame(0xffffff, patch: 0xbbbbbb, px: 0, py: 0, pw: 100, ph: 100);
        Assert.Empty(L.Run(Painted(Frame(0xffffff), Ink(0, 14, 0x666666)), Only(L.Rules.Contrast)).Findings);
        Assert.Single(L.Run(Painted(grey, Ink(0, 14, 0x666666)), Only(L.Rules.Contrast)).Findings);
        // A 40 % ink fails where the opaque one passes; a node opacity of 0.4 does the same.
        L.Pixels white = Frame(0xffffff);
        Assert.Empty(L.Run(Painted(white, Ink(0, 14, 0x000000)), Only(L.Rules.Contrast)).Findings);
        Assert.Single(L.Run(Painted(white, Ink(0, 14, 0x000000, alpha: 0.4f)), Only(L.Rules.Contrast)).Findings);
        Assert.Single(L.Run(Painted(white, Ink(0, 14, 0x000000) with { Opacity = 0.4f }), Only(L.Rules.Contrast)).Findings);
    }

    [Fact]
    public void Runs_the_check_cannot_judge_are_skipped_not_passed()
    {
        L.Pixels white = Frame(0xffffff);
        L.Snapshot Run1(L.Node n) => Painted(white, n);
        Assert.Equal(0, L.Run(Run1(Ink(0, 14, 0x777777) with { Inert = true }), Only(L.Rules.Contrast)).Checked[L.Rules.Contrast]);   // disabled
        Assert.Equal(0, L.Run(Run1(Ink(0, 14, 0x777777) with { Animating = true }), Only(L.Rules.Contrast)).Checked[L.Rules.Contrast]);
        Assert.Equal(0, L.Run(Run1(Ink(0, 14, 0x777777, alpha: 0f)), Only(L.Rules.Contrast)).Checked[L.Rules.Contrast]);   // invisible ink
        Assert.Equal(0, L.Run(Run1(Words(0, -1, 10, 10, 60, 20, "Hello", 14)), Only(L.Rules.Contrast)).Checked[L.Rules.Contrast]);   // no colour known
        Assert.Equal(0, L.Run(Run1(Ink(0, 14, 0x777777) with { Rect = new RectF(200, 10, 60, 20) }), Only(L.Rules.Contrast)).Checked[L.Rules.Contrast]);   // off the frame
        // No frame at all: nothing is checked.
        var noFrame = new L.Snapshot(100f, 100f, WidthTier.Standard, [Ink(0, 14, 0x777777)]);
        Assert.Equal(0, L.Run(noFrame, Only(L.Rules.Contrast)).Checked[L.Rules.Contrast]);
        // A box over a busy background (no colour holds half of it) is skipped: four colours, a quarter each.
        var bgra = new byte[100 * 100 * 4];
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
            {
                int c = (x / 4 + y / 4) % 4 * 0x404040;
                int i = (y * 100 + x) * 4;
                bgra[i] = (byte)(c & 255); bgra[i + 1] = (byte)((c >> 8) & 255); bgra[i + 2] = (byte)((c >> 16) & 255); bgra[i + 3] = 255;
            }
        Assert.Equal(0, L.Run(Painted(new L.Pixels(bgra, 100, 100, 1f), Ink(0, 14, 0x777777)), Only(L.Rules.Contrast)).Checked[L.Rules.Contrast]);
    }

    [Fact]
    public void A_glyph_that_fills_its_own_box_is_judged_on_what_is_left_around_it()
    {
        // A pink heart painted over most of its 20 x 20 box on white: the mode is the ink itself, so the rule looks past it.
        L.Pixels frame = Frame(0xffffff, patch: 0xe0197f, px: 12, py: 12, pw: 16, ph: 16);
        L.Node heart = Words(0, -1, 10, 10, 20, 20, "\ue70d", 12, run: static t => t with { Glyph = true, Color = Rgb(0xe0197f), NaturalW = 20f });
        L.Report report = L.Run(Painted(frame, heart), Only(L.Rules.Contrast));
        Assert.Empty(report.Findings);   // 4.2 : 1 on white clears the 3 : 1 a glyph needs
        Assert.Equal(1, report.Checked[L.Rules.Contrast]);
        // The same heart in a colour that is 2 : 1 on white is a finding.
        L.Node faint = heart with { Text = heart.Text! with { Color = Rgb(0xb0b0b0) } };
        Assert.Single(L.Run(Painted(Frame(0xffffff, patch: 0xb0b0b0, px: 12, py: 12, pw: 16, ph: 16), faint), Only(L.Rules.Contrast)).Findings);
    }

    [Fact]
    public void A_frame_reads_its_pixels_at_the_scale_and_unpremultiplies_them()
    {
        // 200 x 200 px at 2 px per DIP: a 10 x 10 DIP rect at (5, 5) covers px 10..30. Half-alpha premultiplied #ff0000 is (0, 0, 128, 128) BGRA.
        var bgra = new byte[200 * 200 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i + 2] = 128; bgra[i + 3] = 128; }
        var frame = new L.Pixels(bgra, 200, 200, 2f);
        Assert.True(frame.TryBackground(new RectF(5, 5, 10, 10), null, 0f, out ColorF ground, out float share));
        Assert.Equal(1f, ground.R, 2);
        Assert.Equal(0f, ground.G, 2);
        Assert.Equal(1f, share, 3);
        Assert.False(frame.TryBackground(new RectF(500, 500, 10, 10), null, 0f, out _, out _));   // off the frame
        Assert.False(frame.TryBackground(new RectF(5, 5, 10, 10), Rgb(0xff0000), 0.03f, out _, out _));   // everything is the ignored ink
    }

    // ── running: options, order, caps ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_enabled_rules_run_and_errors_come_first_within_a_rule()
    {
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800),
            Words(1, 0, 0, 0, 100, 20, "a", 15, run: static t => t with { Trimmed = true, NaturalW = 200f, NaturalH = 20f }),
            Words(2, 0, 0, 40, 100, 12, "b", 9));
        L.Report all = L.Run(snap);
        Assert.Equal(L.RuleNames.Length, all.Checked.Count);
        Assert.Equal(L.Severity.Error, Of(all, L.Rules.Type)[0].Severity);

        L.Report trim = L.Run(snap, Only(L.Rules.Trim));
        Assert.Single(trim.Checked);
        Assert.All(trim.Findings, f => Assert.Equal(L.Rules.Trim, f.Rule));

        L.Report warns = L.Run(snap, new L.Options { Min = L.Severity.Warn });
        Assert.DoesNotContain(warns.Findings, f => f.Severity == L.Severity.Info);
        Assert.Equal(1, warns.Count(L.Severity.Error));
        Assert.Equal(1, warns.Count(L.Rules.Trim, L.Severity.Warn));
    }

    [Fact]
    public void A_rule_keeps_at_most_its_cap_and_the_report_names_it()
    {
        Func<L.TextRun, L.TextRun> trimmed = static t => t with { Trimmed = true, NaturalW = 200f, NaturalH = 20f };
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800),
            Words(1, 0, 0, 0, 100, 20, "a", 15, run: trimmed), Words(2, 0, 0, 40, 100, 20, "b", 15, run: trimmed), Words(3, 0, 0, 80, 100, 20, "c", 15, run: trimmed));
        L.Report report = L.Run(snap, new L.Options { Rules = [L.Rules.Trim], MaxPerRule = 2 });
        Assert.Equal(2, report.Findings.Count);
        Assert.Equal([L.Rules.Trim], report.Truncated);
    }

    // ── the arguments and the JSON ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_lint_arguments_parse_into_options_in_both_grammars()
    {
        Assert.True(I.TryParse("lint rules=gutter,clip min=warn typeFloor=12 limit=7 allowTrim=zune-crumbs,x", out I.Command text, out string error), error);
        Assert.Equal(I.Verb.Lint, text.Verb);
        Assert.True(Wavee.Diagnostics.InspectLintScene.TryOptions(text.Args, out L.Options o, out error), error);
        Assert.True(o.Enabled(L.Rules.Gutter));
        Assert.True(o.Enabled(L.Rules.Clip));
        Assert.False(o.Enabled(L.Rules.Trim));
        Assert.Equal(L.Severity.Warn, o.Min);
        Assert.Equal(12f, o.TypeFloor);
        Assert.Equal(7, o.MaxPerRule);
        Assert.Equal(["zune-crumbs", "x"], o.AllowTrim);

        Assert.True(I.TryParse("""{"cmd":"lint","args":{"rules":["trim","type"],"allowDuplicate":["See all"]},"id":3}""", out I.Command json, out error), error);
        Assert.True(Wavee.Diagnostics.InspectLintScene.TryOptions(json.Args, out o, out error), error);
        Assert.Equal(["trim", "type"], o.Rules);
        Assert.Equal(["See all"], o.AllowDuplicate);

        Assert.True(I.TryParse("lint", out I.Command bare, out error), error);
        Assert.True(Wavee.Diagnostics.InspectLintScene.TryOptions(bare.Args, out o, out error), error);
        Assert.Equal(L.RuleNames.Length, o.Rules.Count);
        Assert.Equal(11f, o.TypeFloor);
        Assert.Equal(40f, o.TargetFloorCompact);
        Assert.Equal(L.DefaultIgnoreUnder, o.IgnoreUnder);
    }

    [Fact]
    public void An_unknown_rule_or_severity_is_refused_by_name()
    {
        Assert.True(I.TryParse("lint rules=gutter,wobble", out I.Command cmd, out _));
        Assert.False(Wavee.Diagnostics.InspectLintScene.TryOptions(cmd.Args, out _, out string error));
        Assert.Contains("wobble", error);
        Assert.Contains("gutter", error);

        Assert.True(I.TryParse("lint min=loud", out cmd, out _));
        Assert.False(Wavee.Diagnostics.InspectLintScene.TryOptions(cmd.Args, out _, out error));
        Assert.Contains("min", error);
    }

    [Fact]
    public void Lint_and_crossing_are_verbs()
    {
        Assert.Equal(I.Verb.Lint, I.VerbOf("lint"));
        Assert.Equal(I.Verb.Crossing, I.VerbOf("crossing"));
        Assert.Contains("lint", I.VerbNames);
        Assert.Contains("crossing", I.VerbNames);
        Assert.True(I.TryParse("lint gutter,clip", out I.Command positional, out string error), error);
        Assert.Equal("gutter,clip", positional.Args.Str("rules"));
        Assert.True(I.TryParse("crossing from=1440x960 to=1440x400", out I.Command crossing, out error), error);
        Assert.Equal("1440x960", crossing.Args.Str("from"));
        Assert.Equal("1440x400", crossing.Args.Str("to"));
    }

    static JsonElement Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        using JsonDocument doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    [Fact]
    public void A_finding_writes_its_rule_severity_message_keys_and_the_rounded_numbers()
    {
        var finding = new L.Finding(L.Rules.Gutter, L.Severity.Error, "the band is off", ["a", "b"])
        {
            Measured = [new L.Metric("x", 80.1234), new L.Metric("delta", -32.1234)],
            Expected = [new L.Metric("x", 48)],
        };
        JsonElement array = Write(w => { w.WriteStartArray("findings"); Wavee.Diagnostics.InspectLintScene.WriteFinding(w, finding); w.WriteEndArray(); });
        JsonElement f = array.GetProperty("findings")[0];
        Assert.Equal("gutter", f.GetProperty("rule").GetString());
        Assert.Equal("error", f.GetProperty("severity").GetString());
        Assert.Equal("the band is off", f.GetProperty("message").GetString());
        Assert.Equal(new[] { "a", "b" }, f.GetProperty("keys").EnumerateArray().Select(e => e.GetString() ?? "").ToArray());
        Assert.Equal(80.12, f.GetProperty("measured").GetProperty("x").GetDouble());
        Assert.Equal(-32.12, f.GetProperty("measured").GetProperty("delta").GetDouble());
        Assert.Equal(48d, f.GetProperty("expected").GetProperty("x").GetDouble());
    }

    [Fact]
    public void A_report_writes_its_summary_per_rule_counts_and_findings()
    {
        var snap = Snap(Box(0, -1, 0, 0, 1000, 800),
            Words(1, 0, 0, 0, 100, 20, "a", 15, key: "ok", run: static t => t with { LineHeight = 20f }),
            Words(2, 0, 0, 40, 100, 12, "b", 9, key: "tiny"));
        var options = new L.Options { Rules = [L.Rules.Type, L.Rules.Trim] };
        L.Report report = L.Run(snap, options);
        JsonElement root = Write(w => Wavee.Diagnostics.InspectLintScene.WriteReport(w, snap, report, options));
        JsonElement summary = root.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("error").GetInt32());
        Assert.Equal(2, summary.GetProperty("info").GetInt32());
        Assert.Equal(3, summary.GetProperty("nodes").GetInt32());
        JsonElement rules = root.GetProperty("rules");
        Assert.Equal(2, rules.GetProperty("type").GetProperty("checked").GetInt32());
        Assert.Equal(1, rules.GetProperty("type").GetProperty("error").GetInt32());
        Assert.False(rules.TryGetProperty("gutter", out _));   // a rule that did not run is not listed
        Assert.Equal(3, root.GetProperty("findings").GetArrayLength());
        Assert.False(root.TryGetProperty("truncated", out _));
    }
}
