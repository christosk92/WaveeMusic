// ── Wavee.Tests/ZoomTierTests.cs — the large-display Auto zoom clamp, the settled zoom and the Manual floor (C1b, plan 3.2) ──
//
// THE RULE (owner NQ1 revised): Auto zoom keeps `ZoomAutoPolicy`'s curve, except it never pushes a window whose unzoomed width
// is Huge (>= `ZoomTierReference.HugeFrom` = 1920) back down into Standard:
//
//     W < 1920 ? Suggest(W, H) : min(Suggest(W, H), plateauDown(W / 1920))
//
// Both arms are non-decreasing in W and meet at 1920, so the zoom is monotonic (widening a window never lowers it) and the
// layout width W / zoom is at least 1920 for every W >= 1920. Manual zoom stops before the Compact floor (300 DIP of layout).
//
// Pure rules only: nothing here touches the engine's live zoom, the settings store or a mounted frame (this assembly runs its
// facts in parallel). What is pinned:
//   · every plateau boundary of the clamp, the monitor table of the plan and the 1440p @100 % / 4K @150 % rows the CHANGELOG
//     names, the extra DPI rows between them, minimised / zero / non-finite extents, and monotonicity over width and height
//     sweeps (the contract `ZoomAutoPolicy` states and the first draft of the clamp broke);
//   · the clamp is THEME-INDEPENDENT (one core edge) and does what it is for under the shipped ladder shapes: every Huge-sized
//     window lays out in Huge or above, whatever edges the structure draws;
//   · `InitialZoom` is the same rule, so the first frame is already the settled zoom;
//   · the arrival classifier: an isolated size is settled (applied in the same layout phase), a stream waits for the debounce,
//     the modal size-move loop holds, a minimised window and our own re-entrant tick change nothing;
//   · the Manual floor: no step, rung or persisted level takes the layout under 300 DIP, and a refused step stays put.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ZoomTierTests
{
    static float Auto(float w, float h) => ZoomTierReference.Suggest(w, h, ZoomAutoMode.Auto);

    // ── the reference ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_reference_is_the_core_huge_edge_and_the_window_minimum()
    {
        Assert.Equal(1920f, ZoomTierReference.HugeFrom);
        Assert.Equal(300f, ZoomTierReference.CompactFloor);
        Assert.Equal(TierPolicy.WindowFloor, ZoomTierReference.CompactFloor);
    }

    [Theory]
    [InlineData(0.5f, 0.75f)]     // held inside the plateau range
    [InlineData(1f, 1f)]
    [InlineData(1.2f, 1f)]        // snaps DOWN, never to the nearer rung
    [InlineData(1.25f, 1.25f)]
    [InlineData(1.6f, 1.5f)]
    [InlineData(2.4f, 2f)]        // held at the ceiling
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    public void The_plateau_snap_the_clamp_uses_is_the_policys_own(float ratio, float expected)
        => Assert.Equal(expected, ZoomAutoPolicy.PlateauAtOrBelow(ratio));

    // ── the plan's monitor table and the rows between ───────────────────────────────────────────────────────────────────

    [Theory]
    // The plan 3.2 table (maximized; DIP = pixels / OS scale).
    [InlineData(1920f, 1080f, 1f)]                    // 1080p @100 %: unchanged
    [InlineData(2560f, 1440f, 1.25f)]                 // 1440p @100 % AND 4K @150 % (3840 / 1.5): was 1.5 (layout 1707, Standard), now 1.25 (2048, Huge)
    [InlineData(3840f, 2160f, 2f)]                    // 4K @100 %: unchanged
    [InlineData(1280f, 720f, 1f)]                     // 1080p @150 %: unchanged
    [InlineData(2560f / 1.5f, 1440f / 1.5f, 1f)]      // 1440p @150 %: unchanged
    // The same monitors with the taskbar taken off the height: the width decides, the answers hold.
    [InlineData(1920f, 1032f, 1f)]
    [InlineData(2560f, 1392f, 1.25f)]
    [InlineData(3840f, 2112f, 2f)]
    // The scales the table does not list.
    [InlineData(2048f, 1152f, 1f)]                    // 1440p @125 %: was 1.25 (2048), now 1.0 (2048): the layout is the same
    [InlineData(3072f, 1728f, 1.5f)]                  // 4K @125 %: was 1.75 (1755), now 1.5 (2048)
    [InlineData(3840f / 1.75f, 2160f / 1.75f, 1f)]    // 4K @175 %: was 1.25 (1755), now 1.0 (2194)
    // Wide monitors: the height binds below the clamp, the width above it.
    [InlineData(3440f, 1440f, 1.5f)]
    [InlineData(5120f, 1440f, 1.5f)]
    [InlineData(3840f, 1080f, 1f)]
    public void Auto_zoom_per_reference_monitor_is_the_plans_table(float width, float height, float expected)
    {
        float zoom = Auto(width, height);
        Assert.Equal(expected, zoom);
        if (width >= ZoomTierReference.HugeFrom)
            Assert.True(width / zoom >= ZoomTierReference.HugeFrom - 0.01f, "a Huge-sized window must lay out Huge: " + width / zoom);
    }

    [Theory]
    [InlineData(1919f, 1080f)]
    [InlineData(1280f, 720f)]
    [InlineData(1180f, 760f)]
    [InlineData(300f, 330f)]
    [InlineData(1500f, 1400f)]
    public void A_window_narrower_than_the_reference_keeps_the_policys_zoom(float width, float height)
        => Assert.Equal(ZoomAutoPolicy.Suggest(width, height, ZoomAutoMode.Auto), Auto(width, height));

    // ── every boundary ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1919f, 1f)]
    [InlineData(1920f, 1f)]
    [InlineData(1999f, 1f)]     // the policy would say 1.0 here already
    [InlineData(2000f, 1f)]     // the policy says 1.25 from here; the clamp holds 1.0 until 2400
    [InlineData(2399f, 1f)]
    [InlineData(2400f, 1.25f)]  // 2400 / 1920 = 1.25
    [InlineData(2879f, 1.25f)]
    [InlineData(2880f, 1.5f)]   // 2880 / 1920 = 1.5
    [InlineData(3359f, 1.5f)]
    [InlineData(3360f, 1.75f)]  // 3360 / 1920 = 1.75
    [InlineData(3839f, 1.75f)]
    [InlineData(3840f, 2f)]     // 3840 / 1920 = 2
    [InlineData(5000f, 2f)]
    public void The_clamp_flips_exactly_at_the_plateau_widths(float width, float expected)
        => Assert.Equal(expected, Auto(width, 2160f));   // tall enough that the height never binds

    [Fact]
    public void A_short_window_stays_at_the_height_bound_below_the_clamp()
    {
        // 3840 wide, 1080 tall: the height binds (1.2 -> 1.0); the clamp (2.0) never raises it.
        Assert.Equal(1f, Auto(3840f, 1080f));
        Assert.Equal(1.5f, Auto(5120f, 1440f));
    }

    // ── monotonic: the contract of ZoomAutoPolicy ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ZoomAutoMode.Auto)]
    [InlineData(ZoomAutoMode.Dense)]
    public void A_width_sweep_never_lowers_the_zoom_and_never_lays_a_huge_window_out_below_the_edge(ZoomAutoMode mode)
    {
        foreach (float height in new[] { 330f, 600f, 900f, 1080f, 1440f, 2160f })
        {
            float last = 0f;
            for (float width = 300f; width <= 4000f; width += 1f)
            {
                float zoom = ZoomTierReference.Suggest(width, height, mode);
                Assert.True(zoom >= last, $"{mode} {width} x {height}: {zoom} fell from {last}");
                Assert.True(zoom <= ZoomAutoPolicy.Suggest(width, height, mode), $"{mode} {width} x {height}: the clamp may only lower the zoom");
                if (width >= ZoomTierReference.HugeFrom)
                    Assert.True(width / zoom >= ZoomTierReference.HugeFrom - 0.01f, $"{mode} {width} x {height}: lays out at {width / zoom}");
                last = zoom;
            }
        }
    }

    [Fact]
    public void A_height_sweep_never_lowers_the_zoom()
    {
        foreach (float width in new[] { 640f, 1920f, 2560f, 3440f, 3840f })
        {
            float last = 0f;
            for (float height = 200f; height <= 2400f; height += 1f)
            {
                float zoom = Auto(width, height);
                Assert.True(zoom >= last, $"{width} x {height}: {zoom} fell from {last}");
                last = zoom;
            }
        }
    }

    // ── the clamp does what it is for, under any structure's ladder ─────────────────────────────────────────────────────

    static TierPolicy PolicyWithWindowEdges(params TierEdge[] edges) => new(
        TierLadder.Of(edges), TierLadder.Of(edges), TierLadder.Of(new TierEdge(680f, NarrowHold: 0f, WidenHold: 40f)),
        CompactFloor: 300f, ShortFloor: 0f,
        Rungs: ImmutableArray<NamedRung>.Empty, Ladders: ImmutableArray<NamedLadder>.Empty);

    /// <summary>The window ladders the boards draw: every shipped structure's Huge edge is the core reference.</summary>
    public static IEnumerable<object[]> BoardLadders()
    {
        yield return ["Classic", PolicyWithWindowEdges(new TierEdge(528f, 0f, 40f), new TierEdge(1008f, 0f, 40f), new TierEdge(1920f, 40f, 0f))];
        yield return ["WinUI", PolicyWithWindowEdges(new TierEdge(641f, 24f), new TierEdge(1008f, 24f), new TierEdge(1920f, 24f))];
        yield return ["Zune", PolicyWithWindowEdges(new TierEdge(640f, 24f), new TierEdge(1008f, 24f), new TierEdge(1920f, 24f), new TierEdge(3200f, 24f))];
    }

    [Theory]
    [MemberData(nameof(BoardLadders))]
    public void Every_huge_sized_window_lays_out_in_huge_or_above_under_every_structures_ladder(string name, TierPolicy policy)
    {
        foreach (float height in new[] { 900f, 1080f, 1440f, 2160f })
        {
            for (float width = ZoomTierReference.HugeFrom; width <= 4000f; width += 8f)
            {
                float zoom = Auto(width, height);
                var tiers = WindowTiers.Resolve(policy, width / zoom, height / zoom, width / zoom);
                Assert.True(tiers.Window >= WidthTier.Huge, $"{name} {width} x {height} at {zoom}: {tiers.Window}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(BoardLadders))]
    public void The_policys_own_curve_would_have_folded_huge_back_into_standard_and_the_clamp_does_not(string name, TierPolicy policy)
    {
        // 1440p @100 %: the policy zooms to 1.5 (layout 1707, Standard); the clamp keeps 2048 (Huge). Same on 4K @150 %.
        foreach (float width in new[] { 2560f, 3840f / 1.5f })
        {
            float height = width * 9f / 16f;
            float policyZoom = ZoomAutoPolicy.Suggest(width, height, ZoomAutoMode.Auto);
            Assert.Equal(WidthTier.Standard, WindowTiers.Resolve(policy, width / policyZoom, height / policyZoom, width / policyZoom).Window);
            float clamped = Auto(width, height);
            WidthTier tier = WindowTiers.Resolve(policy, width / clamped, height / clamped, width / clamped).Window;
            Assert.True(tier == WidthTier.Huge, name + ": " + tier);
        }
    }

    // ── minimised, zero and non-finite extents ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f, 1080f)]
    [InlineData(1920f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(-5f, 1080f)]
    [InlineData(2560f, -1f)]
    [InlineData(float.NaN, 1080f)]
    [InlineData(2560f, float.NaN)]
    [InlineData(float.PositiveInfinity, 1080f)]
    [InlineData(2560f, float.PositiveInfinity)]
    [InlineData(float.PositiveInfinity, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, 1080f)]
    public void A_degenerate_extent_is_the_neutral_zoom_in_every_mode(float width, float height)
    {
        Assert.Equal(1f, ZoomTierReference.Suggest(width, height, ZoomAutoMode.Auto));
        Assert.Equal(1f, ZoomTierReference.Suggest(width, height, ZoomAutoMode.Dense));
        Assert.Equal(ZoomService.ZoomStepKind.None, ZoomService.AutoZoom(ZoomAutoMode.Auto, width, height, 1.5f, 1.5f, true).Kind);
    }

    [Fact]
    public void Dense_is_only_clamped_where_auto_is()
    {
        Assert.Equal(0.75f, ZoomTierReference.Suggest(1200f, 675f, ZoomAutoMode.Dense));       // below the edge: untouched
        Assert.Equal(1.25f, ZoomTierReference.Suggest(2560f, 1440f, ZoomAutoMode.Dense));
    }

    // ── DPI changes: the same pixels at another OS scale ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3840f, 2160f, 1f, 2f)]
    [InlineData(3840f, 2160f, 1.25f, 1.5f)]
    [InlineData(3840f, 2160f, 1.5f, 1.25f)]
    [InlineData(3840f, 2160f, 1.75f, 1f)]
    [InlineData(3840f, 2160f, 2f, 1f)]
    [InlineData(2560f, 1440f, 1f, 1.25f)]
    [InlineData(2560f, 1440f, 1.25f, 1f)]
    [InlineData(2560f, 1440f, 1.5f, 1f)]
    [InlineData(2560f, 1440f, 2f, 1f)]
    [InlineData(1920f, 1080f, 1f, 1f)]
    [InlineData(1920f, 1080f, 1.5f, 1f)]
    public void A_dpi_change_applies_the_rule_to_the_new_base_extent(float pixelsW, float pixelsH, float osScale, float expected)
        => Assert.Equal(expected, Auto(pixelsW / osScale, pixelsH / osScale));

    [Fact]
    public void A_higher_os_scale_never_raises_the_auto_zoom()
    {
        foreach (var (pixelsW, pixelsH) in new[] { (3840f, 2160f), (2560f, 1440f), (3440f, 1440f), (1920f, 1080f) })
        {
            float last = float.MaxValue;
            for (float scale = 1f; scale <= 3f; scale += 0.25f)
            {
                float zoom = Auto(pixelsW / scale, pixelsH / scale);
                Assert.True(zoom <= last, $"{pixelsW} px at {scale}: {zoom} rose from {last}");
                last = zoom;
            }
        }
    }

    // ── InitialZoom is the same rule ────────────────────────────────────────────────────────────────────────────────────

    static readonly ZoomMonitor Fhd100 = new(1920f, 1040f);
    static readonly ZoomMonitor Qhd100 = new(2560f, 1400f);
    static readonly ZoomMonitor Uhd100 = new(3840f, 2120f);
    static readonly ZoomMonitor Uhd150 = new(2560f, 1413f);

    [Fact]
    public void The_first_frame_opens_at_the_clamped_zoom_on_a_large_monitor()
    {
        // A window as big as the monitor lets it be (the work area bounds the request).
        Assert.Equal(1f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 1f, 4000f, 4000f, Fhd100));
        Assert.Equal(1.25f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 1f, 4000f, 4000f, Qhd100));    // 1440p @100 %
        Assert.Equal(1.25f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 1.5f, 4000f, 4000f, Uhd150));  // 4K @150 %, a persisted 150 % replaced
        Assert.Equal(2f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 1f, 4000f, 4000f, Uhd100));       // 4K @100 %
        Assert.Equal(1f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 1.5f, 1180f, 760f, Qhd100));      // the default window: Standard, 100 %
    }

    [Theory]
    [InlineData(1180f, 760f)]
    [InlineData(2560f, 1392f)]
    [InlineData(3200f, 1800f)]
    [InlineData(3840f, 2160f)]
    [InlineData(2399f, 1500f)]
    [InlineData(2400f, 1500f)]
    public void The_first_frame_equals_the_zoom_the_loop_settles_on(float windowW, float windowH)
    {
        var monitor = Uhd100;
        float initial = ZoomService.InitialZoom(ZoomAutoMode.Auto, 1f, windowW, windowH, monitor);
        var first = ZoomService.AutoZoom(ZoomAutoMode.Auto, windowW, windowH, 1f, lastAuto: 0f, seeded: false);
        float settled = first.Kind == ZoomService.ZoomStepKind.Apply ? first.Zoom : 1f;
        Assert.Equal(settled, initial);
        Assert.Equal(Auto(windowW, windowH), initial);
        // ... so the loop's next tick at that zoom has nothing to do (no re-zoom after the first frame).
        Assert.Equal(ZoomService.ZoomStepKind.None, ZoomService.AutoZoom(ZoomAutoMode.Auto, windowW, windowH, initial, first.LastAuto, first.Seeded).Kind);
    }

    [Fact]
    public void The_zoom_rows_auto_label_is_the_same_rule()
    {
        // The live viewport is already zoomed: x zoom recovers the base the rule is written against.
        Assert.Equal(1.25f, ZoomService.AutoSuggestion(2048f, 1152f, 1.25f));     // 1440p @100 % at its settled zoom
        Assert.Equal(1.25f, ZoomService.AutoSuggestion(1706.6666f, 960f, 1.5f));  // the same window at the old 150 %
        Assert.Equal(2f, ZoomService.AutoSuggestion(1920f, 1080f, 2f));
    }

    // ── the arrival classifier: when the loop may step ──────────────────────────────────────────────────────────────────

    const float W = 2560f, H = 1392f;

    [Fact]
    public void The_first_size_is_settled_and_its_own_reentrant_tick_is_not()
    {
        var loop = new ZoomService.AutoLoop();
        Assert.False(loop.TryGetLatest(out _, out _));
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(0, W, H, inModalLoop: false));
        Assert.True(loop.TryGetLatest(out float w, out float h));
        Assert.Equal(W, w);
        Assert.Equal(H, h);
        // Our own zoom write re-fires the frame effect with the same zoom-invariant base (float noise only): nothing to do.
        Assert.Equal(ZoomService.SizeArrival.Unchanged, loop.Arrive(5, W + 0.001f, H - 0.001f, false));
        Assert.Equal(ZoomService.SizeArrival.Unchanged, loop.Arrive(5_000, W, H, false));
    }

    [Fact]
    public void A_maximize_is_settled_and_steps_in_its_own_frame()
    {
        var loop = new ZoomService.AutoLoop();
        var posted = new List<Action>();
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(0, 1180f, 760f, false));
        Assert.Equal(ZoomService.ZoomStepKind.None, loop.Step(ZoomAutoMode.Auto, 1180f, 760f, 1f, posted.Add));   // seeds at 100 %
        Assert.Empty(posted);

        // Maximize on a 1440p monitor at 100 %, long after: a settled size, and the step it allows decides 1.25 at once.
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(60_000, W, H, false));
        Assert.Equal(ZoomService.ZoomStepKind.Apply, loop.Step(ZoomAutoMode.Auto, W, H, 1f, posted.Add));
        Assert.Single(posted);
        Assert.Equal(1.25f, ZoomService.AutoZoom(ZoomAutoMode.Auto, W, H, 1f, 1f, true).Zoom);

        // The zoom write re-fires the effect at the new layout width: the same base, nothing left to do.
        Assert.Equal(ZoomService.SizeArrival.Unchanged, loop.Arrive(60_016, W, H, false));
        Assert.Equal(ZoomService.ZoomStepKind.None, loop.Step(ZoomAutoMode.Auto, W, H, 1.25f, posted.Add));
        Assert.Single(posted);

        // Restore: another isolated size.
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(90_000, 1180f, 760f, false));
        Assert.Equal(ZoomService.ZoomStepKind.Apply, loop.Step(ZoomAutoMode.Auto, 1180f, 760f, 1.25f, posted.Add));
        Assert.Equal(2, posted.Count);
    }

    [Fact]
    public void A_stream_of_sizes_waits_for_the_quiet_and_an_isolated_size_after_it_does_not()
    {
        var loop = new ZoomService.AutoLoop();
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(1_000, 1180f, 760f, false));
        Assert.Equal(ZoomService.SizeArrival.Stream, loop.Arrive(1_100, 1300f, 800f, false));
        Assert.Equal(ZoomService.SizeArrival.Stream, loop.Arrive(1_250, 1500f, 900f, false));
        // Each size re-arms the window: 499 ms after the last is still a stream, 500 ms is not.
        Assert.Equal(ZoomService.SizeArrival.Stream, loop.Arrive(1_749, 1700f, 950f, false));
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(2_249, 2560f, 1392f, false));
        // The stream's trailing step runs against the newest size, not the one the debounce armed on.
        Assert.True(loop.TryGetLatest(out float w, out float h));
        Assert.Equal(2560f, w);
        Assert.Equal(1392f, h);
    }

    [Fact]
    public void The_modal_size_move_loop_holds_and_its_end_is_always_settled()
    {
        var loop = new ZoomService.AutoLoop();
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(0, 1180f, 760f, false));

        // A drag: every size inside the loop is held, whatever it is, and none is remembered.
        Assert.Equal(ZoomService.SizeArrival.Held, loop.Arrive(100, 1500f, 900f, true));
        Assert.Equal(ZoomService.SizeArrival.Held, loop.Arrive(200, 2560f, 1392f, true));
        Assert.True(loop.TryGetLatest(out float w, out _));
        Assert.Equal(1180f, w);

        // Release inside the stream window: still ONE settled size, not a stream (the drag is over).
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(300, 2560f, 1392f, false));
        // The next size inside 500 ms is a stream again.
        Assert.Equal(ZoomService.SizeArrival.Stream, loop.Arrive(600, 2600f, 1392f, false));
    }

    [Fact]
    public void A_drag_that_ends_at_the_same_size_still_gets_one_settled_step_for_a_monitor_move()
    {
        var loop = new ZoomService.AutoLoop();
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(0, W, H, false));
        Assert.Equal(ZoomService.SizeArrival.Held, loop.Arrive(10_000, W, H, true));
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(12_000, W, H, false));
        Assert.Equal(ZoomService.SizeArrival.Unchanged, loop.Arrive(12_016, W, H, false));
    }

    [Fact]
    public void A_drag_release_is_settled_even_when_the_modal_edge_is_seen_before_the_final_size()
    {
        var loop = new ZoomService.AutoLoop();
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(0, 1180f, 760f, false));
        Assert.Equal(ZoomService.SizeArrival.Held, loop.Arrive(100, 1500f, 900f, true));
        // The edge lands first, on the base the drag started from: one step now, and it stamps no time ...
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(200, 1180f, 760f, false));
        // ... so the final size, 300 ms after the first arrival, is still the drag's settled size and not a stream.
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(300, 2560f, 1392f, false));
        Assert.Equal(ZoomService.SizeArrival.Stream, loop.Arrive(400, 2600f, 1392f, false));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, 760f)]
    [InlineData(float.NaN, 760f)]
    [InlineData(1180f, float.PositiveInfinity)]
    [InlineData(-1f, -1f)]
    public void A_minimised_or_unusable_size_changes_nothing_and_a_restore_to_the_same_size_changes_nothing(float width, float height)
    {
        var loop = new ZoomService.AutoLoop();
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(0, 1180f, 760f, false));
        Assert.Equal(ZoomService.SizeArrival.Unchanged, loop.Arrive(10_000, width, height, false));
        Assert.Equal(ZoomService.SizeArrival.Unchanged, loop.Arrive(10_001, width, height, true));   // not even a modal edge
        Assert.True(loop.TryGetLatest(out float w, out float h));
        Assert.Equal(1180f, w);
        Assert.Equal(760f, h);
        // Restored to where it was: the zoom is already right, nothing to apply (no flash).
        Assert.Equal(ZoomService.SizeArrival.Unchanged, loop.Arrive(20_000, 1180f, 760f, false));
        // Restored onto another monitor (another base extent): an ordinary settled size.
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(30_000, 2560f, 1392f, false));
    }

    [Fact]
    public void A_monitor_hop_between_dpis_is_a_settled_size_that_decides_the_new_zoom()
    {
        var loop = new ZoomService.AutoLoop();
        var posted = new List<Action>();
        // A maximized window on a 4K monitor at 100 %: 2.0.
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(0, 3840f, 2112f, false));
        Assert.Equal(ZoomService.ZoomStepKind.None, loop.Step(ZoomAutoMode.Auto, 3840f, 2112f, 2f, posted.Add));
        // Dragged onto a 1080p monitor at 150 % (the modal loop ends there): base 1280 x 672 -> 1.0, applied once.
        Assert.Equal(ZoomService.SizeArrival.Held, loop.Arrive(10_000, 1280f, 672f, true));
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(10_400, 1280f, 672f, false));
        Assert.Equal(ZoomService.ZoomStepKind.Apply, loop.Step(ZoomAutoMode.Auto, 1280f, 672f, 2f, posted.Add));
        Assert.Single(posted);
    }

    [Fact]
    public void Manual_zoom_overrides_auto_at_every_size()
    {
        var loop = new ZoomService.AutoLoop();
        var posted = new List<Action>();
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(0, W, H, false));
        Assert.Equal(ZoomService.ZoomStepKind.None, loop.Step(ZoomAutoMode.Manual, W, H, 1.75f, posted.Add));
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(10_000, 3840f, 2112f, false));
        Assert.Equal(ZoomService.ZoomStepKind.None, loop.Step(ZoomAutoMode.Manual, 3840f, 2112f, 1.75f, posted.Add));
        Assert.Empty(posted);
    }

    [Fact]
    public void A_zoom_the_user_moved_in_auto_is_kept_not_undone_by_the_next_settled_size()
    {
        // The pure decisions only (Step would write the settings store to pin Manual).
        var loop = new ZoomService.AutoLoop();
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(0, W, H, false));
        var first = ZoomService.AutoZoom(ZoomAutoMode.Auto, W, H, 1f, lastAuto: 0f, seeded: false);
        Assert.Equal(ZoomService.ZoomStepKind.Apply, first.Kind);                  // Auto picks 1.25
        // Ctrl+- took the live zoom to 1.1; the next settled size must pin Manual, not apply over the pick.
        Assert.Equal(ZoomService.SizeArrival.Settled, loop.Arrive(10_000, 3840f, 2112f, false));
        var next = ZoomService.AutoZoom(ZoomAutoMode.Auto, 3840f, 2112f, 1.1f, first.LastAuto, first.Seeded);
        Assert.Equal(ZoomService.ZoomStepKind.PinManual, next.Kind);
        Assert.Equal(1.1f, next.Zoom);
    }

    // ── the Manual floor ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(-10f)]
    public void An_unknown_width_bounds_nothing(float baseWidth)
    {
        Assert.Equal(2.5f, ZoomService.ManualStep(2f, +1, baseWidth));
        Assert.Equal(2.5f, ZoomTierReference.FloorManual(2.5f, baseWidth));
    }

    [Theory]
    // base width, live zoom, direction, the zoom that results
    [InlineData(1200f, 2f, +1, 2.5f)]       // 1200 / 2.5 = 480
    [InlineData(750f, 2f, +1, 2.5f)]        // 750 / 2.5 = 300: exactly the floor fits
    [InlineData(749f, 2f, +1, 2f)]          // 299.6: refused, stays put
    [InlineData(600f, 2f, +1, 2f)]          // 2.5 would give 240
    [InlineData(300f, 1f, +1, 1f)]          // the window minimum: no zoom in at all
    [InlineData(330f, 1f, +1, 1.1f)]        // 300 exactly at 1.1
    [InlineData(330f, 1.1f, +1, 1.1f)]      // and no further
    [InlineData(300f, 1f, -1, 0.9f)]        // zooming out is never refused
    [InlineData(400f, 2f, +1, 2f)]          // already past the floor (200): a refused step stays put...
    [InlineData(400f, 2f, -1, 1.75f)]       // ... and zooming out is allowed
    [InlineData(400f, 2.5f, 0, 1f)]         // reset is allowed
    [InlineData(300f, 0.5f, 0, 1f)]         // reset to 100 % on the minimum window: layout 300
    public void A_manual_step_that_would_take_the_layout_under_the_floor_is_refused(float baseWidth, float live, int direction, float expected)
        => Assert.Equal(expected, ZoomService.ManualStep(live, direction, baseWidth));

    [Fact]
    public void Held_zoom_in_never_takes_any_window_under_the_floor()
    {
        foreach (float baseWidth in new[] { 300f, 301f, 330f, 400f, 640f, 750f, 1000f, 1180f, 1920f, 3840f })
        {
            float zoom = 1f;
            for (int press = 0; press < 20; press++)
            {
                float next = ZoomService.ManualStep(zoom, +1, baseWidth);
                Assert.True(next >= zoom);
                zoom = next;
                Assert.True(baseWidth / zoom >= ZoomTierReference.CompactFloor - 0.01f, $"{baseWidth} at {zoom}: {baseWidth / zoom}");
            }
        }
    }

    [Theory]
    [InlineData(2.5f, 1f, 600f, 2f)]         // a rung picked in Settings: the largest that fits
    [InlineData(2.5f, 1f, 1200f, 2.5f)]
    [InlineData(2.5f, 2.5f, 600f, 2.5f)]     // already there: the pick does not lower it
    [InlineData(2f, 2.5f, 600f, 2f)]         // a lower pick is honoured
    [InlineData(2.5f, 1.75f, 600f, 2f)]      // from 1.75 it can reach 2, not 2.5
    public void A_picked_rung_lands_on_the_largest_one_that_fits(float target, float live, float baseWidth, float expected)
        => Assert.Equal(expected, ZoomTierReference.ClampManual(target, live, baseWidth));

    [Theory]
    [InlineData(2.5f, 1180f, 760f, 2.5f)]     // 472 DIP of layout: fine
    [InlineData(2.5f, 600f, 700f, 2f)]        // a persisted level too big for the window: held inside the floor
    [InlineData(5f, 1180f, 760f, 2.5f)]       // beyond the ladder
    [InlineData(1.3f, 300f, 700f, 1f)]        // off the ladder and over the floor: never below 100 %
    [InlineData(0.5f, 300f, 700f, 0.5f)]      // below 100 % widens the layout: untouched
    [InlineData(2f, 1180f, 0f, 2f)]           // unknown height is irrelevant, the width decides
    public void A_persisted_manual_level_opens_inside_the_floor(float persisted, float windowW, float windowH, float expected)
    {
        Assert.Equal(expected, ZoomService.InitialZoom(ZoomAutoMode.Manual, persisted, windowW, windowH, default));
    }

    [Fact]
    public void The_manual_floor_is_bounded_by_the_monitor_like_the_auto_rule()
    {
        // A 3200-wide request on a 1280 DIP work area: the window can be no wider than the screen.
        var small = new ZoomMonitor(1280f, 693f);
        Assert.Equal(2.5f, ZoomService.InitialZoom(ZoomAutoMode.Manual, 2.5f, 3200f, 1800f, small));     // 512 DIP: fits
        var narrow = new ZoomMonitor(600f, 700f);
        Assert.Equal(2f, ZoomService.InitialZoom(ZoomAutoMode.Manual, 2.5f, 3200f, 1800f, narrow));      // 600 / 2.5 = 240: held at 2
    }

    [Fact]
    public void The_manual_floor_does_not_touch_auto()
    {
        // Auto never reaches the floor on its own: its largest zoom (2.0) on the smallest window that suggests it.
        Assert.True(3840f / Auto(3840f, 2160f) >= ZoomTierReference.CompactFloor);
        Assert.Equal(ZoomAutoPolicy.Suggest(1180f, 760f, ZoomAutoMode.Auto), ZoomService.InitialZoom(ZoomAutoMode.Auto, 2.5f, 1180f, 760f, default));
    }
}
