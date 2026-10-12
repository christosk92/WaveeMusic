// ── Wavee.Tests/ZoomServiceTests.cs — the first frame's zoom and the loop's carried state (Platform/ZoomService.cs, C1)
//
// THE PROPERTY: `ZoomService.InitialZoom` is the zoom the 500 ms loop would settle on for the same window and monitor.
// Each case below runs the REAL loop rule (`ZoomService.AutoZoom`, the pure step the shell frame feeds) from the state
// the shell starts in (live zoom = the persisted level, unseeded), takes the zoom it settles on, and requires
//   1. the pre-window value equals it, so the first frame is already final, and
//   2. the loop's next tick at that zoom does nothing, so there is no startup re-zoom.
// Only the pure members are driven: nothing here touches the engine's live zoom or the settings store (this assembly
// runs its facts in parallel).

using Xunit;

namespace Wavee.Tests;

public class ZoomServiceTests
{
    // The monitors the plan's table is written against, in raw DIP (work area = the screen minus a ~40 px taskbar).
    static readonly ZoomMonitor Fhd100 = new(1920f, 1040f);
    static readonly ZoomMonitor Fhd150 = new(1280f, 693f);
    static readonly ZoomMonitor Qhd100 = new(2560f, 1400f);
    static readonly ZoomMonitor Uhd100 = new(3840f, 2120f);
    static readonly ZoomMonitor Uhd150 = new(2560f, 1413f);

    /// <summary>The loop as the shell runs it: tick 1 from the persisted zoom, unseeded; apply what it decides; tick 2 at
    /// the zoom it applied. Returns the settled zoom and whether the second tick had anything left to do.</summary>
    static (float Settled, ZoomService.ZoomStepKind SecondTick) SettleViaLoop(
        ZoomAutoMode mode, float persisted, float baseW, float baseH)
    {
        float live = FluentGpu.Foundation.ZoomLadder.Clamp(persisted);   // what the engine seeds the window with
        var first = ZoomService.AutoZoom(mode, baseW, baseH, live, lastAuto: 0f, seeded: false);
        float settled = first.Kind == ZoomService.ZoomStepKind.Apply ? first.Zoom : live;
        var second = ZoomService.AutoZoom(mode, baseW, baseH, settled, first.LastAuto, first.Seeded);
        return (settled, second.Kind);
    }

    [Theory]
    // The startup re-zoom this PR removes: a zoom persisted on a big monitor, a fresh 1180 x 760 window.
    [InlineData(ZoomAutoMode.Auto, 2.0f, 1180f, 760f, 1.0f)]
    [InlineData(ZoomAutoMode.Auto, 1.5f, 1180f, 760f, 1.0f)]
    [InlineData(ZoomAutoMode.Auto, 1.0f, 1180f, 760f, 1.0f)]    // already settled: nothing to do
    [InlineData(ZoomAutoMode.Auto, 0.75f, 1180f, 760f, 1.0f)]   // Auto never goes below 100 %
    // A window the user asked to be big (the screenshot loop's --width / --height) is zoomed for the display it fills, and
    // never below the Huge layout it already has (C1b: 3200 wide is 1.5, not 2.0; 2560 wide is 1.25, not 1.5).
    [InlineData(ZoomAutoMode.Auto, 1.0f, 3840f, 2160f, 2.0f)]
    [InlineData(ZoomAutoMode.Auto, 1.0f, 3200f, 1800f, 1.5f)]
    [InlineData(ZoomAutoMode.Auto, 1.0f, 2560f, 1400f, 1.25f)]
    // Dense may go below 100 %.
    [InlineData(ZoomAutoMode.Dense, 1.0f, 1180f, 760f, 0.75f)]
    // Manual owns the zoom: the persisted level is the first frame's, whatever the window.
    [InlineData(ZoomAutoMode.Manual, 2.0f, 1180f, 760f, 2.0f)]
    [InlineData(ZoomAutoMode.Manual, 1.25f, 3200f, 1800f, 1.25f)]
    public void The_pre_window_zoom_is_what_the_500ms_loop_settles_on(
        ZoomAutoMode mode, float persisted, float windowW, float windowH, float expected)
    {
        float initial = ZoomService.InitialZoom(mode, persisted, windowW, windowH, Uhd100);
        var (settled, secondTick) = SettleViaLoop(mode, persisted, windowW, windowH);

        Assert.Equal(expected, initial);
        Assert.Equal(settled, initial);                                   // the first frame is already final
        Assert.Equal(ZoomService.ZoomStepKind.None, secondTick);          // so the loop has no re-zoom left to do
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void It_equals_the_settled_loop_value_on_every_reference_monitor(
        string monitorName, ZoomAutoMode mode, float persisted, float windowW, float windowH)
    {
        var monitor = MonitorNamed(monitorName);
        float initial = ZoomService.InitialZoom(mode, persisted, windowW, windowH, monitor);

        // The window cannot be larger than the monitor it opens on: the loop sees the bounded extent.
        float baseW = MathF.Min(windowW, monitor.WorkWidthDip), baseH = MathF.Min(windowH, monitor.WorkHeightDip);
        var (settled, secondTick) = SettleViaLoop(mode, persisted, baseW, baseH);

        Assert.Equal(settled, initial);
        Assert.Equal(ZoomService.ZoomStepKind.None, secondTick);
    }

    public static TheoryData<string, ZoomAutoMode, float, float, float> Monitors()
    {
        var data = new TheoryData<string, ZoomAutoMode, float, float, float>();
        foreach (var name in new[] { "fhd100", "fhd150", "qhd100", "uhd100", "uhd150" })
        {
            foreach (float persisted in new[] { 0.75f, 1f, 1.5f, 2f })
            {
                data.Add(name, ZoomAutoMode.Auto, persisted, 1180f, 760f);         // the default window
                data.Add(name, ZoomAutoMode.Auto, persisted, 3200f, 1800f);        // a window bigger than most monitors
                data.Add(name, ZoomAutoMode.Dense, persisted, 1180f, 760f);
                data.Add(name, ZoomAutoMode.Manual, persisted, 3200f, 1800f);
            }
        }
        return data;
    }

    static ZoomMonitor MonitorNamed(string name) => name switch
    {
        "fhd100" => Fhd100,
        "fhd150" => Fhd150,
        "qhd100" => Qhd100,
        "uhd100" => Uhd100,
        "uhd150" => Uhd150,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [Fact]
    public void The_monitor_bounds_the_window_the_loop_would_see()
    {
        // A 3200 x 1800 request on a 1920 x 1040 work area: the first window is no larger than the screen, so Auto
        // settles on that extent (100 %), not on the request (150 %).
        Assert.Equal(1f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 1f, 3200f, 1800f, Fhd100));
        Assert.Equal(1.5f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 1f, 3200f, 1800f, Uhd100));
    }

    [Fact]
    public void An_unknown_monitor_leaves_the_requested_extent_alone()
    {
        Assert.False(default(ZoomMonitor).IsKnown);
        Assert.Equal(1.5f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 1f, 3200f, 1800f, default));
        Assert.Equal(1f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 2f, 1180f, 760f, default));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void A_corrupt_persisted_level_opens_at_one_hundred_percent_in_manual(float persisted)
        => Assert.Equal(1f, ZoomService.InitialZoom(ZoomAutoMode.Manual, persisted, 1180f, 760f, Uhd100));

    [Fact]
    public void A_degenerate_window_extent_keeps_the_persisted_level()
        => Assert.Equal(1.5f, ZoomService.InitialZoom(ZoomAutoMode.Auto, 1.5f, 0f, 760f, Uhd100));

    // ── the Zoom row's "Auto (N%)" ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1920f, 1080f, 2f, 2f)]       // the live viewport is already zoomed: x zoom recovers the 3840 x 2160 base
    [InlineData(3840f, 2160f, 1f, 2f)]
    [InlineData(1180f, 760f, 1f, 1f)]
    [InlineData(1280f, 720f, 1.5f, 1f)]      // 1920 x 1080 base: the coin-flip that snaps down
    public void The_auto_label_is_the_suggestion_for_the_base_extent(float viewportW, float viewportH, float liveZoom, float expected)
        => Assert.Equal(expected, ZoomService.AutoSuggestion(viewportW, viewportH, liveZoom));

    // ── the loop's carried state ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_loop_applies_through_the_poster_then_finds_its_own_zoom_settled()
    {
        var loop = new ZoomService.AutoLoop();
        var posted = new List<Action>();

        // Tick 1: a 3840 x 2160 base at the live 100 % — Auto wants 200 %, posted for the UI thread (not run here).
        Assert.Equal(ZoomService.ZoomStepKind.Apply, loop.Step(ZoomAutoMode.Auto, 3840f, 2160f, 1f, posted.Add));
        Assert.Single(posted);

        // Tick 2: our own apply re-fires the effect at 200 % with the same (zoom-invariant) base — a no-op.
        Assert.Equal(ZoomService.ZoomStepKind.None, loop.Step(ZoomAutoMode.Auto, 3840f, 2160f, 2f, posted.Add));
        Assert.Single(posted);
    }

    [Fact]
    public void Manual_mode_posts_nothing()
    {
        var loop = new ZoomService.AutoLoop();
        var posted = new List<Action>();
        Assert.Equal(ZoomService.ZoomStepKind.None, loop.Step(ZoomAutoMode.Manual, 3200f, 1800f, 1f, posted.Add));
        Assert.Empty(posted);
    }
}
