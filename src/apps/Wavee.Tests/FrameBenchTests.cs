// ── Wavee.Tests/FrameBenchTests.cs — the `--frame-bench` arm's pure half (Screens/Diagnostics.FrameBench.cs) ─────────────
//
// The argv (scenario filter, window knobs, the explicit real-data switch and its --profile guard, explicit uris, the opt-in pass
// timing), the real-data target pick from a profile's play-recency/history documents, and the summary maths over synthetic
// frame-ledger windows: presents per second, the UI / render / other CPU split (overlap attribution, unclamped), painted-frame
// CPU, the exit / wake / turn census, the percentiles, GPU busy, allocations, per-second GC and memory, the off/on A/B, and the
// JSON the compare tool reads. Nothing here starts the engine or touches a file.

using System.Text.Json;
using FluentGpu.Hosting;
using Xunit;

namespace Wavee.Tests;

public class FrameBenchOptionsTests
{
    [Fact]
    public void Off_without_the_flag()
    {
        var o = Diagnostics.FrameBenchOptions.Parse(["--fake", "--perf-bench"]);
        Assert.False(o.Enabled);
        Assert.False(o.Real);
        Assert.False(o.GpuPasses);
    }

    [Fact]
    public void Bare_flag_runs_every_scenario_with_the_default_windows()
    {
        var o = Diagnostics.FrameBenchOptions.Parse(["--fake", "--frame-bench", "--probe-out", "x"]);
        Assert.True(o.Enabled);
        Assert.Equal(Diagnostics.FrameBenchScenarios.All, o.Scenarios);
        Assert.Equal(10, o.MeasureSec);
        Assert.Equal(2, o.WarmupSec);
        Assert.Equal("", o.Label);
        Assert.Empty(o.Uris);
        Assert.False(o.GpuPasses);                                     // pass timing is opt-in
    }

    [Fact]
    public void Hide_restore_runs_only_when_named_and_has_its_own_knobs()
    {
        Assert.DoesNotContain("hide-restore", Diagnostics.FrameBenchOptions.Parse(["--frame-bench"]).Scenarios);
        var o = Diagnostics.FrameBenchOptions.Parse(["--frame-bench=idle,hide-restore", "--bench-hide-cycles", "10", "--bench-hidden-sec", "45"]);
        Assert.Equal(["idle", "hide-restore"], o.Scenarios);
        Assert.Equal(10, o.HideCycles);
        Assert.Equal(45, o.HiddenSec);
        Assert.Empty(Diagnostics.FrameBenchOptions.UnknownNames(["--frame-bench=hide-restore"]));
        var d = Diagnostics.FrameBenchOptions.Parse(["--frame-bench=hide-restore", "--bench-hide-cycles", "0", "--bench-hidden-sec", "1"]);
        Assert.Equal(3, d.HideCycles);   // out of range falls back to the default
        Assert.Equal(30, d.HiddenSec);
    }

    [Theory]
    [InlineData("cycles")]
    [InlineData("presentChecked")]
    [InlineData("damageValidated")]
    [InlineData("minimize.noPresent")]
    public void Hide_restore_counts_are_neutral_not_regressions(string key)
        => Assert.Equal(0, Diagnostics.FrameBenchMath.Better(key));

    [Fact]
    public void Gpu_pass_timing_is_opt_in()
        => Assert.True(Diagnostics.FrameBenchOptions.Parse(["--frame-bench", "--bench-gpu-passes"]).GpuPasses);

    [Theory]
    [InlineData(new[] { "--frame-bench=lyrics,idle-playing" }, 2)]
    [InlineData(new[] { "--frame-bench", "idle-playing,lyrics" }, 2)]
    public void A_filter_keeps_run_order_not_argument_order(string[] args, int count)
    {
        var o = Diagnostics.FrameBenchOptions.Parse(args);
        Assert.Equal(count, o.Scenarios.Length);
        Assert.Equal(["idle-playing", "lyrics"], o.Scenarios);
    }

    [Fact]
    public void Unknown_filter_names_are_reported()
    {
        string[] args = ["--frame-bench=idle,bogus,lyrics"];
        Assert.Equal(["idle", "lyrics"], Diagnostics.FrameBenchOptions.Parse(args).Scenarios);
        Assert.Equal(["bogus"], Diagnostics.FrameBenchOptions.UnknownNames(args));
    }

    [Theory]
    [InlineData("15", 15)]
    [InlineData("2", 10)]      // below the floor
    [InlineData("999", 10)]    // above the ceiling
    [InlineData("abc", 10)]
    public void Bench_sec_is_range_checked(string value, int expected)
        => Assert.Equal(expected, Diagnostics.FrameBenchOptions.Parse(["--frame-bench", "--bench-sec", value]).MeasureSec);

    [Fact]
    public void Real_data_label_and_uris_are_explicit()
    {
        var o = Diagnostics.FrameBenchOptions.Parse(["--frame-bench", "--bench-real", "--bench-label", "cold",
            "--bench-uris", "lyrics=spotify:track:a, big=liked,bad,=x"]);
        Assert.True(o.Real);
        Assert.Equal("cold", o.Label);
        Assert.Equal(2, o.Uris.Count);
        Assert.Equal("spotify:track:a", o.Uris["lyrics"]);
        Assert.Equal("liked", o.Uris["big"]);
    }

    [Theory]
    [InlineData(true, false, "", false)]                  // --fake: always fine
    [InlineData(true, true, "", false)]
    [InlineData(false, false, @"C:\bench", true)]         // no --fake and no --bench-real: refuse
    [InlineData(false, true, "", true)]                   // --bench-real without --profile: refuse (the owner's profile)
    [InlineData(false, true, @"C:\bench", false)]         // --bench-real with --profile: go
    public void Real_data_needs_the_switch_and_a_profile(bool fake, bool real, string profileRoot, bool refused)
    {
        string? why = Diagnostics.FrameBenchOptions.RealRefusal(fake, real, profileRoot);
        Assert.Equal(refused, why is not null);
        if (fake == false && real && profileRoot.Length == 0) Assert.Contains("--profile", why);
    }
}

public class FrameBenchTargetsTests
{
    [Fact]
    public void Recency_orders_newest_first_and_history_fills_in()
    {
        const string recency = """{"spotify:track:old":100,"spotify:track:new":300,"spotify:album:a1":200,"spotify:playlist:p1":50,"spotify:show:x":999}""";
        const string history = """[{"name":"albums"},{"name":"artist:spotify:artist:r1","arg":"R"},{"name":"pl:spotify:playlist:p2","arg":"P"}]""";
        var t = Diagnostics.FrameBenchTargets.FromProfileText(recency, history, new Dictionary<string, string> { ["lyrics"] = "spotify:track:w" });
        Assert.Equal(["spotify:track:w", "spotify:track:new", "spotify:track:old"], t.Tracks);
        Assert.Equal(["spotify:album:a1"], t.Albums);
        Assert.Equal(["spotify:playlist:p1", "spotify:playlist:p2"], t.Playlists);
        Assert.Equal(["spotify:artist:r1"], t.Artists);
        Assert.Equal("liked", t.Big);
    }

    [Fact]
    public void Missing_or_broken_documents_yield_empty_lists()
    {
        var t = Diagnostics.FrameBenchTargets.FromProfileText(null, "{not json", new Dictionary<string, string>());
        Assert.Empty(t.Tracks);
        Assert.Empty(t.Playlists);
    }

    [Fact]
    public void The_fake_catalogue_has_every_kind()
    {
        var t = Diagnostics.FrameBenchTargets.Fake;
        Assert.Equal("spotify:track:tr0", t.Tracks[0]);
        Assert.Equal(13, t.Albums.Length);
        Assert.Equal(7, t.Playlists.Length);
        Assert.Equal(12, t.Artists.Length);
        Assert.Equal("liked", t.Big);
    }
}

public class FrameBenchMathTests
{
    const long F = 10_000_000;        // QPC ticks per second
    const long Ms = F / 1000;         // QPC ticks per ms
    const double Cpm = 1_000_000;     // cycles per ms

    /// <summary>One second of 100 painted frames at 10 ms: each UI frame 2 ms of CPU, each render turn 1 ms inside its interval, the
    /// process 5 ms per interval (so other = 5 - 2 - 1 = 2 ms), a 1.5 ms GPU frame each; GetThreadTimes agrees with the cycles.</summary>
    static LedgerSnapshot Window(int n = 100)
    {
        var ui = new LedgerUiFrame[n];
        var rt = new LedgerRenderTurn[n];
        var gpu = new LedgerGpuFrame[n];
        for (int i = 0; i < n; i++)
        {
            long t = F + i * F / 100;
            ui[i] = new LedgerUiFrame
            {
                Seq = (ulong)i, StartQpc = t, EndQpc = t + Ms * 3, PaintQpc = t + 1, Flags = (ushort)LedgerUiFlags.Painted,
                UiCycles = 2_000_000, UiCyclesTotal = (ulong)(i + 1) * 2_000_000, ProcessCyclesTotal = (ulong)(i + 1) * 5_000_000,
                UiCpuTimeTotal = (i + 1) * 20_000L, WakeMask = (uint)WakeReasons.FrameNeeded,
                AllocBytes = i == 50 ? 1000 : 0, Gc0Total = i >= 50 ? 1 : 0, GcPauseTicksTotal = i >= 50 ? 20_000 : 0,
                MissedVsyncsTotal = i >= 90 ? 2 : 0,
            };
            rt[i] = new LedgerRenderTurn
            {
                Seq = (ulong)i, StartQpc = t + Ms * 4, SlotOpenQpc = t + Ms * 4, DoneQpc = t + Ms * 5, EndQpc = t + Ms * 5,
                Cycles = 1_000_000, CpuTimeTotal = (i + 1) * 10_000L, Kind = (byte)LedgerTurnKind.Fresh, Flags = (byte)LedgerTurnFlags.Presented,
            };
            gpu[i] = new LedgerGpuFrame { Seq = (ulong)i, GpuMs = 1.5f, PassCount = 2, TileRasterMs = 0.5f, CompositeMs = 1f };
        }
        return new LedgerSnapshot
        {
            QpcFrequency = F, CyclesPerMs = Cpm, Processors = 10, OriginQpc = F, EndQpc = 2 * F,
            Ui = ui, Render = rt, Gpu = gpu,
            Memory = [new LedgerMemorySample { Qpc = F, WorkingSetBytes = 100 << 20, ProcessCpuTicksTotal = 1, ProcessCyclesTotal = 1, TotalAllocatedBytes = 0 },
                      new LedgerMemorySample { Qpc = 2 * F, WorkingSetBytes = 300 << 20, ProcessCpuTicksTotal = 5_000_001, ProcessCyclesTotal = 500_000_001, TotalAllocatedBytes = 4096 }],
            Audio = [new LedgerAudioSample { Qpc = F, DeviceUnderrunsTotal = 3, XrunsTotal = 1, PaddingMinFrames = 4800, Rate = 48000 },
                     new LedgerAudioSample { Qpc = 2 * F, DeviceUnderrunsTotal = 5, XrunsTotal = 1, PaddingMinFrames = 480, Rate = 48000 }],
        };
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        var v = new List<double>();
        for (int i = 1; i <= 100; i++) v.Add(i);
        Assert.Equal(50, Diagnostics.FrameBenchMath.Percentile(v, 0.50));
        Assert.Equal(95, Diagnostics.FrameBenchMath.Percentile(v, 0.95));
        Assert.Equal(99, Diagnostics.FrameBenchMath.Percentile(v, 0.99));
        Assert.Equal(100, Diagnostics.FrameBenchMath.Percentile(v, 1.0));
        Assert.True(double.IsNaN(Diagnostics.FrameBenchMath.Percentile([], 0.5)));
    }

    [Fact]
    public void Presents_frames_and_intervals()
    {
        var s = Diagnostics.FrameBenchMath.Summarize("x", Window());
        Assert.Equal(1.0, s["wallSec"], 6);
        Assert.Equal(100, s["frames"]);
        Assert.Equal(100, s["presents"]);
        Assert.Equal(100, s["presentsPerSec"], 6);
        Assert.Equal(10, s["presentIntervalMs.p50"], 6);
        Assert.Equal(3, s["uiFrameMs.p99"], 6);
        Assert.Equal(100, s["exitPerSec.Painted"], 6);
        Assert.Equal(0, s["exitPerSec.Idle"], 6);
        Assert.Equal(100, s["wakePerSec.FrameNeeded"], 6);
        Assert.Equal(100, s["turnPerSec.Fresh"], 6);
    }

    [Fact]
    public void The_cpu_split_adds_up_to_the_process_and_the_thread_times_agree()
    {
        var s = Diagnostics.FrameBenchMath.Summarize("x", Window());
        Assert.Equal(2, s["uiCpuMs.p50"], 6);
        Assert.Equal(2, s["paintedCpuMs.p50"], 6);
        Assert.Equal(1, s["renderCpuMs.p95"], 6);
        Assert.Equal(2, s["otherCpuMs.p50"], 6);
        // 99 intervals of 10 ms: ui 2 / render 1 / process 5 ms each.
        Assert.Equal(0.2, s["uiCores"], 6);
        Assert.Equal(0.1, s["renderCores"], 6);
        Assert.Equal(0.2, s["otherCores"], 6);
        Assert.Equal(0.5, s["processCores"], 6);
        Assert.Equal(s["processCores"], s["uiCores"] + s["renderCores"] + s["otherCores"], 6);
        Assert.Equal(0.2, s["uiCoresTimes"], 6);                           // GetThreadTimes, no rate
        Assert.Equal(0.1, s["renderCoresTimes"], 6);
        Assert.Equal(5.0, s["processCpuPct"], 6);                          // GetProcessTimes: 500 ms of CPU over 1 s × 10 (headline)
        Assert.Equal(5.0, s["processCpuPctCycles"], 6);                    // 0.5 cores of 10
        Assert.Equal(1_000_000, s["cyclesPerMs.window"], 3);               // 5e8 cycles / 500 ms of CPU
    }

    [Fact]
    public void Raw_cycle_metrics_are_rate_free()
    {
        // Window(): 100 frames 10 ms apart; 99 intervals = 0.99 s. Process 5 Mcyc, UI 2 Mcyc, render 1 Mcyc per interval.
        var snap = Window();
        var s = Diagnostics.FrameBenchMath.Summarize("x", snap);
        Assert.Equal(0.5, s["processGcyclesPerSec"], 6);        // 495e6 cycles / 0.99 s
        Assert.Equal(200, s["uiMcyclesPerSec"], 6);
        Assert.Equal(100, s["renderMcyclesPerSec"], 6);
        Assert.Equal(200, s["otherMcyclesPerSec"], 6);
        Assert.Equal(2000, s["uiKcyclesPerPaintedFrame"], 6);
        Assert.Equal(1000, s["renderKcyclesPerTurn"], 6);
        // The rate does not enter: reading the same window at another rate leaves every raw-cycle metric untouched.
        var other = Diagnostics.FrameBenchMath.Summarize("x", snap.WithRate(3_000_000));
        foreach (var k in new[] { "processGcyclesPerSec", "uiMcyclesPerSec", "renderMcyclesPerSec", "otherMcyclesPerSec", "uiKcyclesPerPaintedFrame", "renderKcyclesPerTurn" })
            Assert.Equal(s[k], other[k], 9);
        Assert.NotEqual(s["uiCores"], other["uiCores"]);
        // An empty window is NaN, not a crash.
        var e = Diagnostics.FrameBenchMath.Summarize("e", new LedgerSnapshot { QpcFrequency = F, CyclesPerMs = Cpm, OriginQpc = F, EndQpc = 2 * F });
        Assert.True(double.IsNaN(e["processGcyclesPerSec"]));
        Assert.True(double.IsNaN(e["uiKcyclesPerPaintedFrame"]));
    }

    [Fact]
    public void Mixed_exits_and_straddling_turns_split_by_overlap_without_a_clamp()
    {
        // Three frames ending at 10 / 20 / 30 ms: painted, idle, painted. One 2 ms render turn straddles the 20 ms boundary
        // evenly. The process spends 5 ms in the first interval and only 1.5 ms in the second (counter skew), the UI 1 ms in each.
        long b = F;
        var ui = new LedgerUiFrame[]
        {
            new() { StartQpc = b + 9 * Ms, EndQpc = b + 10 * Ms, Flags = (ushort)LedgerUiFlags.Painted, PaintQpc = 1, UiCycles = 1_000_000,
                    UiCyclesTotal = 10_000_000, ProcessCyclesTotal = 50_000_000, Exit = (byte)LedgerFrameExit.Painted, AllocBytes = 300 },
            new() { StartQpc = b + 19 * Ms, EndQpc = b + 20 * Ms, UiCycles = 100_000, Exit = (byte)LedgerFrameExit.Idle,
                    UiCyclesTotal = 11_000_000, ProcessCyclesTotal = 55_000_000, WakeMask = (uint)WakeReasons.ImageCrossfades },
            new() { StartQpc = b + 29 * Ms, EndQpc = b + 30 * Ms, Flags = (ushort)LedgerUiFlags.Painted, PaintQpc = 1, UiCycles = 1_000_000,
                    UiCyclesTotal = 12_000_000, ProcessCyclesTotal = 56_500_000, Exit = (byte)LedgerFrameExit.Painted, AllocBytes = 100 },
        };
        var rt = new LedgerRenderTurn[]
        {
            new() { StartQpc = b + 19 * Ms, SlotOpenQpc = b + 19 * Ms, DoneQpc = b + 21 * Ms, EndQpc = b + 21 * Ms, Cycles = 2_000_000,
                    Kind = (byte)LedgerTurnKind.Motion, Flags = (byte)LedgerTurnFlags.Presented },
        };
        var snap = new LedgerSnapshot { QpcFrequency = F, CyclesPerMs = Cpm, Processors = 10, OriginQpc = b, EndQpc = 2 * F, Ui = ui, Render = rt };
        var s = Diagnostics.FrameBenchMath.Summarize("mixed", snap);
        Assert.Equal(2, s["paintedFrames"]);
        Assert.Equal(1, s["exitPerSec.Idle"], 6);
        Assert.Equal(2, s["exitPerSec.Painted"], 6);
        Assert.Equal(1, s["wakePerSec.ImageCrossfades"], 6);
        Assert.Equal(1, s["turnPerSec.Motion"], 6);
        Assert.Equal(1, s["paintedCpuMs.p50"], 6);                           // the idle frame's 0.1 ms is not a painted frame's cost
        Assert.Equal(1, s["paintedCpuMs.max"], 6);
        Assert.Equal(200, s["paintedAllocBytesPerFrame"], 6);
        // Interval 1: 5 - 1 - 1 (half the turn) = 3; interval 2: 1.5 - 1 - 1 = -0.5 (kept: the mean stays unbiased).
        Assert.Equal(1.25, s["otherCpuMs.avg"], 6);
        Assert.Equal(-0.5, Diagnostics.FrameBenchMath.Percentile([3, -0.5], 0.01));
        Assert.Equal(0.1, s["renderCores"], 6);                              // the whole turn, 2 ms over 20 ms
        Assert.Equal(0.1, s["uiCores"], 6);
        Assert.Equal(0.325, s["processCores"], 6);                           // 6.5 ms over 20 ms
    }

    [Fact]
    public void Gpu_allocations_gc_memory_and_audio_per_second()
    {
        var s = Diagnostics.FrameBenchMath.Summarize("x", Window());
        Assert.Equal(1.5, s["gpuMs.p50"], 5);
        Assert.Equal(15.0, s["gpuBusyPct"], 4);                           // 100 × 1.5 ms over 1000 ms
        Assert.Equal(0.5, s["gpuPass.tileRasterMs"], 5);
        Assert.Equal(1.0, s["gpuPass.compositeMs"], 5);
        Assert.Equal(10, s["uiAllocBytesPerFrame"], 6);
        Assert.Equal(4096, s["processAllocBytesPerSec"], 6);
        Assert.Equal(1, s["gc0PerSec"], 6);
        Assert.Equal(2, s["gcPauseMsPerSec"], 6);
        Assert.Equal(2, s["missedVsyncsPerSec"], 6);
        Assert.Equal(200, s["workingSetMB.avg"], 6);
        Assert.Equal(300, s["workingSetMB.peak"], 6);
        Assert.Equal(2, s["audioDeviceUnderrunsPerSec"], 6);
        Assert.Equal(0, s["audioXrunsPerSec"], 6);
        Assert.Equal(10, s["audioPaddingMinMs"], 6);
    }

    [Fact]
    public void A_window_read_at_another_rate_scales_its_cycle_figures_only()
    {
        var w = Window();
        var a = Diagnostics.FrameBenchMath.Summarize("x", w);
        var b = Diagnostics.FrameBenchMath.Summarize("x", w.WithRate(2 * Cpm));
        Assert.Equal(a["uiCpuMs.p50"] / 2, b["uiCpuMs.p50"], 6);
        Assert.Equal(a["processCpuPct"], b["processCpuPct"], 6);           // GetProcessTimes: no rate
        Assert.Equal(a["uiCoresTimes"], b["uiCoresTimes"], 6);
    }

    [Fact]
    public void An_empty_window_is_NaN_not_a_crash()
    {
        var s = Diagnostics.FrameBenchMath.Summarize("idle", new LedgerSnapshot { QpcFrequency = F, CyclesPerMs = Cpm, OriginQpc = F, EndQpc = 2 * F });
        Assert.Equal(0, s["frames"]);
        Assert.True(double.IsNaN(s["uiCpuMs.p50"]));
        Assert.True(double.IsNaN(s["uiCores"]));
        Assert.True(double.IsNaN(s["gc0PerSec"]));
    }

    [Fact]
    public void Overhead_ab_reads_both_arms_at_one_rate()
    {
        var off = new Diagnostics.OverheadArm(100, 50_000_000, 60, 1000, 400);
        var on = new Diagnostics.OverheadArm(100, 52_000_000, 62, 1000, 420, 3.0);
        var s = Diagnostics.FrameBenchMath.Overhead("ledger-overhead", off, on, Cpm, 10, "n");
        Assert.Equal(500, s["uiCpuUsPerFrame.off"], 6);
        Assert.Equal(20, s["overheadUiCpuUsPerFrame"], 6);
        Assert.Equal(20, s["overheadRunFrameWallUs"], 6);
        Assert.Equal(0.2, s["overheadProcessCpuPct"], 6);
        Assert.True(double.IsNaN(s["overheadGpuMs"]));                     // the off arm had no ledger to read GPU from
        Assert.Contains("(+20.0)", Diagnostics.FrameBenchMath.Table([s]));
        var sum = off.Add(off);
        Assert.Equal(200, sum.Frames);
        Assert.Equal(2000, sum.WallMs, 6);
    }

    [Fact]
    public void Json_carries_every_metric_the_run_rate_and_nulls_NaN()
    {
        var a = Diagnostics.FrameBenchMath.Summarize("home-scroll", Window(), extra: [new("scrollViewports", 12.5)]);
        var b = new Diagnostics.FrameBenchScenarioSummary("lyrics-line", "no line-synced lyrics", []);
        var c = Diagnostics.FrameBenchMath.Summarize("idle", new LedgerSnapshot { QpcFrequency = F, CyclesPerMs = Cpm, OriginQpc = F, EndQpc = 2 * F });
        string json = Diagnostics.FrameBenchMath.Json("0.3.3", "warm", real: true, 12, 120, "1770x1140", 10, 2, 2_900_000,
            new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), [a, b, c], gpuPasses: true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wavee-frame-bench/2.1", root.GetProperty("schema").GetString());
        Assert.Equal("real", root.GetProperty("data").GetString());
        Assert.Equal("warm", root.GetProperty("label").GetString());
        Assert.True(root.GetProperty("gpuPasses").GetBoolean());
        Assert.Equal(2_900_000, root.GetProperty("cyclesPerMs").GetDouble());
        var sc = root.GetProperty("scenarios");
        Assert.Equal(3, sc.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, sc[2].GetProperty("metrics").GetProperty("uiCpuMs.p50").ValueKind);
        var metrics = sc[0].GetProperty("metrics");
        Assert.Equal(a.Metrics.Count, metrics.EnumerateObject().Count());
        Assert.Equal(100, metrics.GetProperty("presentsPerSec").GetDouble(), 6);
        Assert.Equal(12.5, metrics.GetProperty("scrollViewports").GetDouble(), 6);
        Assert.Equal(0, metrics.GetProperty("gpuPass.offscreenMs").GetDouble());
        Assert.Equal("no line-synced lyrics", sc[1].GetProperty("skipped").GetString());
        string table = Diagnostics.FrameBenchMath.Table([a, b]);
        Assert.Contains("home-scroll", table);
        Assert.Contains("lyrics-line       skipped: no line-synced lyrics", table);
    }

    [Theory]
    [InlineData("presentsPerSec", 1)]
    [InlineData("audioPaddingMinMs", 1)]
    [InlineData("frames", 0)]
    [InlineData("framesPerSec", 0)]
    [InlineData("exitPerSec.Idle", 0)]
    [InlineData("wakePerSec.Anim", 0)]
    [InlineData("uiCpuUsPerFrame.on", 0)]
    [InlineData("scrollViewports", 0)]
    [InlineData("paintedCpuMs.p99", 0)]
    [InlineData("processCpuPct", 0)]
    [InlineData("uiCoresTimes", 0)]
    [InlineData("renderCoresTimes", 0)]
    [InlineData("processGcyclesPerSec", -1)]
    [InlineData("uiMcyclesPerSec", -1)]
    [InlineData("renderMcyclesPerSec", -1)]
    [InlineData("otherMcyclesPerSec", -1)]
    [InlineData("uiKcyclesPerPaintedFrame", -1)]
    [InlineData("renderKcyclesPerTurn", -1)]
    [InlineData("renderCpuMs.p99", 0)]
    [InlineData("gc0PerSec", -1)]
    [InlineData("gpuBusyPct", -1)]
    public void Metric_direction(string key, int better) => Assert.Equal(better, Diagnostics.FrameBenchMath.Better(key));
}
