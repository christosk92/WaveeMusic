// ── Wavee.Tests/InspectTraceTests.cs — the `--inspect` trace verb's pure half (argument parse, wheel schedule, summary detectors) ──
//
// Pins `Screens/Diagnostics.Inspect.Trace.cs` (InspectTrace) on hand-built rows: no engine, no host, no thread.

using System.Text.Json;
using FluentGpu.Foundation;
using Xunit;
using I = Wavee.Diagnostics.Inspect;
using T = Wavee.Diagnostics.InspectTrace;

namespace Wavee.Tests;

public class InspectTraceTests
{
    static I.Args Args(string line)
    {
        Assert.True(I.TryParse(line, out I.Command cmd, out string error), error);
        return cmd.Args;
    }

    static T.Options Parse(string line, int frames = 30, string scroll = "")
    {
        Assert.True(T.TryParse(Args(line), frames, scroll, out T.Options o, out string error), error);
        return o;
    }

    static RectF R(float x, float y, float w = 100, float h = 50) => new(x, y, w, h);

    static T.Row Row(int f, Action<T.Row>? edit = null, params T.NodeSample[] nodes)
    {
        var r = new T.Row { Frame = f, Abs = f, Rendered = true };
        r.Nodes.AddRange(nodes);
        edit?.Invoke(r);
        return r;
    }

    static T.NodeSample Node(float x, float y, bool track, RectF? laid = null, bool shown = true, int w = 0)
        => new(w, shown, laid ?? R(x, y), R(x, y), track, 1f);

    static T.Row Scroll(int f, double offset, double max = 1000, string label = "main@1", Action<T.Row>? edit = null)
        => Row(f, r => { r.Scroll = new T.ScrollSample(label, offset, 0, max, 3000, 800, offset, "Fling", true); edit?.Invoke(r); });

    static T.Summary Sum(IReadOnlyList<T.Row> rows, string[]? watch = null, T.Options? o = null)
        => T.Summarize(rows, watch ?? ["a"], o ?? new T.Options());

    // ── the parse ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_defaults_trace_every_frame_with_twenty_nodes_and_no_input()
    {
        T.Options o = Parse("trace");
        Assert.Equal(30, o.Frames);
        Assert.Equal(1, o.Every);
        Assert.Equal(20, o.MaxNodes);
        Assert.Empty(o.Wheel);
        Assert.Equal("", o.Scroll);
        Assert.True(o.Rows);
        Assert.False(o.WatchesNodes);
    }

    [Fact]
    public void Keys_prefixes_scroll_every_and_the_thresholds_parse()
    {
        T.Options o = Parse("trace keys=a,b prefix=zune-home.,shell. scroll=main every=3 max=7 jump=4 snap=1 rows=false");
        Assert.Equal(["a", "b"], o.Keys);
        Assert.Equal(["zune-home.", "shell."], o.Prefixes);
        Assert.Equal("main", o.Scroll);
        Assert.Equal(3, o.Every);
        Assert.Equal(7, o.MaxNodes);
        Assert.Equal(4f, o.JumpDip);
        Assert.Equal(1f, o.SnapDip);
        Assert.False(o.Rows);
        Assert.True(o.WatchesNodes);
    }

    [Fact]
    public void The_json_form_takes_keys_as_an_array()
    {
        Assert.True(I.TryParse("""{"cmd":"trace","args":{"frames":90,"keys":["x","y"],"scroll":"list"}}""", out I.Command cmd, out string e), e);
        Assert.True(T.TryParse(cmd.Args, 90, "", out T.Options o, out e), e);
        Assert.Equal(["x", "y"], o.Keys);
        Assert.Equal("list", o.Scroll);
        Assert.Equal(90, o.Frames);
    }

    [Fact]
    public void An_action_trace_uses_the_default_scroller_unless_scroll_is_given()
    {
        Assert.Equal("main", Parse("goto home trace=40", 40, "main").Scroll);
        Assert.Equal("lib", Parse("goto home trace=40 scroll=lib", 40, "main").Scroll);
        Assert.Equal("", Parse("goto home trace=40 scroll=none", 40, "main").Scroll);
    }

    [Fact]
    public void A_frame_count_outside_the_range_is_refused()
    {
        Assert.False(T.TryParse(Args("trace"), 0, "", out _, out string e));
        Assert.Contains("frames", e);
        Assert.False(T.TryParse(Args("trace"), T.MaxFrames + 1, "", out _, out _));
    }

    [Fact]
    public void The_trace_verb_is_listed_and_takes_frames_positionally()
    {
        Assert.Equal(I.Verb.Trace, I.VerbOf("trace"));
        Assert.Contains("trace", I.VerbNames);
        Assert.Equal(90, Args("trace 90").Int("frames", 0));
    }

    // ── the wheel schedule ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_wheel_sequence_cycles_and_is_due_every_nth_frame_until_the_count_is_sent()
    {
        T.Options o = Parse("trace wheel=3,3,-6 wheelEvery=4 wheelCount=5");
        Assert.Equal([3d, 3d, -6d], o.Wheel);
        Assert.Equal(4, o.WheelEvery);
        Assert.Equal(5, o.WheelCount);
        var due = new List<int>();
        int sent = 0;
        for (int f = 0; f < 40; f++)
            if (T.WheelDue(f, o.WheelEvery, o.WheelCount, sent)) { due.Add(f); sent++; }
        Assert.Equal([0, 4, 8, 12, 16], due);
        Assert.Equal(3d, T.WheelNotches(o.Wheel, 3));
        Assert.Equal(-6d, T.WheelNotches(o.Wheel, 5));
    }

    [Fact]
    public void A_single_wheel_value_is_one_event_by_default_and_a_bad_list_is_refused()
    {
        T.Options o = Parse("trace wheel=3");
        Assert.Equal(1, o.WheelCount);
        Assert.False(T.TryParse(Args("trace wheel=up"), 10, "", out _, out string e));
        Assert.Contains("wheel", e);
    }

    [Fact]
    public void A_pan_contact_feeds_deltas_for_its_frames_then_lifts_and_repeats()
    {
        T.Options o = Parse("trace pan=10,8 panFrames=3 panStart=2 panEvery=10 panCount=2");
        Assert.Equal([10d, 8d], o.Pan);
        var seen = new List<string>();
        for (int f = 0; f < 20; f++)
        {
            T.PanAt(o, f, out bool down, out double dy, out bool lift);
            if (down) seen.Add(f + ":" + dy);
            if (lift) seen.Add(f + ":lift");
        }
        Assert.Equal(["2:10", "3:8", "4:10", "5:lift", "12:10", "13:8", "14:10", "15:lift"], seen);
        Assert.False(Parse("trace pan=5 panKind=touch").PanTouch is false);
        Assert.False(T.TryParse(Args("trace pan=x"), 10, "", out _, out _));
    }

    // ── relayouts and timing ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_root_solve_is_an_escape_that_was_not_absorbed_locally()
    {
        var rows = new[]
        {
            Row(0, r => { r.Escapes = 2; r.LocalResolves = 2; }),
            Row(1, r => { r.Escapes = 3; r.LocalResolves = 1; }),
            Row(2),
        };
        T.Summary s = Sum(rows);
        Assert.Equal([1], s.RootSolveFrames);
        Assert.Equal([0, 1], s.EscapeFrames);
        Assert.Equal(2, rows[1].RootSolves);
    }

    [Fact]
    public void The_full_layout_path_is_inferred_from_measures_with_no_dirty_marks()
    {
        var rows = new[] { Row(0, r => { r.Measure = 900; r.ScopedMarks = 0; }), Row(1, r => { r.Measure = 40; r.ScopedMarks = 3; }), Row(2) };
        T.Summary s = Sum(rows);
        Assert.Equal([0], s.FullLayoutFrames);
        Assert.Equal(940, s.MeasureTotal);
        Assert.Equal(2, s.FramesWithMeasure);
        Assert.Equal(0, s.MaxMeasureFrame);
    }

    [Fact]
    public void The_slowest_layout_and_cpu_frames_are_named_and_budgets_counted()
    {
        var rows = new[]
        {
            Row(0, r => { r.Layout = 1; r.Flush = 1; }),
            Row(1, r => { r.Layout = 9; r.Flush = 2; r.Record = 3; }),
            Row(2, r => { r.Layout = 4; r.Flush = 30; }),
        };
        T.Summary s = Sum(rows);
        Assert.Equal(1, s.MaxLayoutFrame);
        Assert.Equal(9d, s.MaxLayoutMs);
        Assert.Equal(2, s.MaxCpuFrame);
        Assert.Equal(34d, s.MaxCpuMs);
        Assert.Equal(2, s.CpuOver8);    // frames 1 (14) and 2 (34)
        Assert.Equal(1, s.CpuOver16);
    }

    [Fact]
    public void Settling_is_the_frame_after_the_last_animating_one()
    {
        T.Summary done = Sum([Row(0, r => r.Animating = true), Row(1, r => r.Animating = true), Row(2), Row(3)]);
        Assert.Equal(2, done.SettledAt);
        Assert.Equal(1, done.LastAnimatingFrame);
        T.Summary running = Sum([Row(0, r => r.Animating = true), Row(1, r => r.Animating = true)]);
        Assert.Equal(-1, running.SettledAt);
        Assert.Equal(0, Sum([Row(0), Row(1)]).SettledAt);
    }

    // ── snaps and jumps ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_laid_out_move_with_no_track_is_a_snap()
    {
        T.Summary s = Sum([Row(0, null, Node(0, 100, false)), Row(1, null, Node(0, 100, false)), Row(2, null, Node(0, 160, false))]);
        T.Move m = Assert.Single(s.Snaps);
        Assert.Equal(2, m.Frame);
        Assert.Equal(60f, m.Dy);
        Assert.Empty(s.Jumps);
    }

    [Fact]
    public void A_presented_only_move_such_as_a_scroll_translate_is_neither_snap_nor_jump()
    {
        RectF laid = R(0, 100);
        T.Summary s = Sum([Row(0, null, Node(0, 100, false, laid)), Row(1, null, Node(0, 40, false, laid))]);
        Assert.Empty(s.Snaps);
        Assert.Empty(s.Jumps);
    }

    [Fact]
    public void A_big_step_under_a_track_is_a_jump_and_a_smooth_one_is_not()
    {
        var smooth = new List<T.Row>();
        for (int f = 0; f < 10; f++) smooth.Add(Row(f, null, Node(f * 1.5f, 0, true)));
        Assert.Empty(Sum(smooth).Jumps);

        var jumpy = new List<T.Row>();
        for (int f = 0; f < 5; f++) jumpy.Add(Row(f, null, Node(f * 1.5f, 0, true)));
        jumpy.Add(Row(5, null, Node(80, 0, true)));
        T.Move m = Assert.Single(Sum(jumpy).Jumps);
        Assert.Equal(5, m.Frame);
        Assert.True(m.Mag > 70f);
        Assert.Equal(1.5f, m.PrevMag, 3);
        Assert.True(m.Track);
    }

    [Fact]
    public void A_node_that_is_hidden_or_absent_resets_its_history()
    {
        T.Summary s = Sum([
            Row(0, null, Node(0, 0, false)),
            Row(1, null, Node(0, 500, false, shown: false)),
            Row(2, null, Node(0, 900, false)),
            Row(3),
            Row(4, null, Node(0, 10, false)),
        ]);
        Assert.Empty(s.Snaps);
        Assert.Equal([("a", 0, 4)], s.NodeLife);
    }

    [Fact]
    public void Each_watched_node_is_tracked_on_its_own()
    {
        T.Summary s = Sum([
            Row(0, null, Node(0, 0, false, w: 0), Node(0, 0, false, w: 1)),
            Row(1, null, Node(0, 0, false, w: 0), Node(0, 50, false, w: 1)),
        ], ["a", "b"]);
        Assert.Equal("b", Assert.Single(s.Snaps).Key);
    }

    // ── scroll ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_smooth_scroll_has_no_reversal_overshoot_stall_or_uneven_frame()
    {
        var rows = new List<T.Row>();
        double off = 0;
        for (int f = 0; f < 20; f++) { off += 40 - f * 1.5; rows.Add(Scroll(f, off)); }
        T.Summary s = Sum(rows);
        Assert.Empty(s.Reversals);
        Assert.Empty(s.Overshoots);
        Assert.Empty(s.Stalls);
        Assert.Empty(s.Uneven);
        Assert.Equal(19, s.Deltas.Count);
        Assert.Equal(38.5d, s.Deltas.Max);
    }

    [Fact]
    public void A_direction_flip_is_a_reversal_and_the_flip_back_is_another()
    {
        double[] offsets = [0, 10, 20, 30, 24, 18, 30, 40];
        T.Summary s = Sum(offsets.Select((o, f) => Scroll(f, o)).ToList());
        Assert.Equal([4, 6], s.Reversals.Select(e => e.Frame).ToArray());
        Assert.Equal(-6d, s.Reversals[0].Delta);
    }

    [Fact]
    public void An_offset_past_either_bound_is_an_overshoot()
    {
        double[] offsets = [0, -12, 0.2, 400, 1000, 1030, 1000];
        T.Summary s = Sum(offsets.Select((o, f) => Scroll(f, o)).ToList());
        Assert.Equal([1, 5], s.Overshoots.Select(e => e.StartFrame).ToArray());
        Assert.Equal("before-start", s.Overshoots[0].Kind);
        Assert.Equal("past-end", s.Overshoots[1].Kind);
        Assert.Equal(30d, s.Overshoots[1].MaxOver);
    }

    [Fact]
    public void Consecutive_frames_past_a_bound_are_one_run_with_the_deepest_point()
    {
        double[] offsets = [0, -10, -30, -20, -5, 0, 5, 1010, 1020];
        T.Summary s = Sum(offsets.Select((o, f) => Scroll(f, o)).ToList());
        Assert.Equal(2, s.Overshoots.Count);
        T.OvershootRun first = s.Overshoots[0];
        Assert.Equal((1, 4, 4, 30d, 2), (first.StartFrame, first.EndFrame, first.Frames, first.MaxOver, first.MaxOverFrame));
        Assert.Equal("past-end", s.Overshoots[1].Kind);
        Assert.Equal(2, s.Overshoots[1].Frames);
    }

    [Fact]
    public void A_zero_delta_inside_a_moving_run_is_a_stall()
    {
        double[] offsets = [0, 20, 40, 40, 60, 80];
        T.Summary s = Sum(offsets.Select((o, f) => Scroll(f, o)).ToList());
        Assert.Equal(3, Assert.Single(s.Stalls).Frame);
    }

    [Fact]
    public void A_delta_far_off_its_neighbours_is_uneven()
    {
        double[] deltas = [20, 20, 5, 20, 20];
        double off = 0;
        var rows = new List<T.Row> { Scroll(0, 0) };
        for (int i = 0; i < deltas.Length; i++) { off += deltas[i]; rows.Add(Scroll(i + 1, off)); }
        T.Summary s = Sum(rows);
        Assert.Equal(3, Assert.Single(s.Uneven).Frame);
    }

    [Fact]
    public void Two_different_scrollers_are_never_compared_with_each_other()
    {
        T.Summary s = Sum([Scroll(0, 0, label: "old@1"), Scroll(1, 100, label: "old@1"), Scroll(2, 5, label: "new@2"), Scroll(3, 105, label: "new@2")]);
        Assert.Empty(s.Reversals);
        Assert.Empty(s.Uneven);
    }

    [Fact]
    public void Layout_work_on_a_frame_where_the_offset_moved_is_reported()
    {
        T.Summary s = Sum([Scroll(0, 0), Scroll(1, 30, edit: r => r.Measure = 12), Scroll(2, 60), Scroll(3, 90, edit: r => r.Escapes = 1)]);
        Assert.Equal([1, 3], s.LayoutDuringScroll.Select(e => e.Frame).ToArray());
    }

    // ── the reply ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_json_carries_rows_watch_and_summary_and_every_thins_the_rows()
    {
        var result = new T.Result { Options = new T.Options { Every = 2 } };
        result.Watch.Add("a");
        for (int f = 0; f < 5; f++) result.Rows.Add(Row(f, r => { r.Layout = 1.23456; r.Escapes = 1; }, Node(f, 0, f % 2 == 0)));
        result.Rows[1].Scroll = new T.ScrollSample("main@1", 12.5, 3, 900, 2000, 800, 12.5, "Wheel", true);
        result.Summary = T.Summarize(result.Rows, result.Watch, result.Options);
        string json = I.Reply.Ok(1, "trace", w => T.Write(w, result));
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(5, root.GetProperty("traced").GetInt32());
        Assert.Equal("a", root.GetProperty("watch")[0].GetString());
        JsonElement rows = root.GetProperty("rows");
        Assert.Equal([0, 2, 4], rows.EnumerateArray().Select(r => r.GetProperty("f").GetInt32()).ToArray());
        Assert.Equal(1.235, rows[0].GetProperty("ms").GetProperty("layout").GetDouble());
        Assert.Equal(1, rows[0].GetProperty("n").GetProperty("rootSolves").GetInt32());
        Assert.Equal(4, rows[0].GetProperty("nodes")[0].GetProperty("pres").GetArrayLength());
        Assert.Equal(5, root.GetProperty("summary").GetProperty("frames").GetInt32());
        Assert.Contains("fake", root.GetProperty("note").GetString());
    }

    [Fact]
    public void Rows_false_leaves_only_the_summary()
    {
        var result = new T.Result { Options = new T.Options { Rows = false } };
        result.Rows.Add(Row(0));
        result.Summary = T.Summarize(result.Rows, result.Watch, result.Options);
        using JsonDocument doc = JsonDocument.Parse(I.Reply.Ok(1, "trace", w => T.Write(w, result)));
        Assert.False(doc.RootElement.TryGetProperty("rows", out _));
        Assert.True(doc.RootElement.TryGetProperty("summary", out _));
    }
}
