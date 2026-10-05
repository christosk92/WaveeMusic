// ── Wavee.Tests/FrameBenchTests.cs — the `--frame-bench` arm's pure half (Screens/Diagnostics.FrameBench.cs) ─────────────
//
// The argv (scenario filter, window knobs, the explicit real-data switch, explicit uris), the real-data target pick from a
// profile's play-recency/history documents, and the summary maths over a synthetic frame-ledger window: presents per second,
// the UI / render / other CPU split, the percentiles, GPU busy, allocations, GC and memory, and the JSON the compare tool
// reads. Nothing here starts the engine or touches a file.

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
    }

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
    const double Cpm = 1_000_000;     // cycles per ms

    /// <summary>One second of 100 frames at 10 ms: each UI frame 2 ms of CPU, each render turn 1 ms, the process 5 ms per
    /// interval (so other = 5 - 2 - 1 = 2 ms), a 1.5 ms GPU frame each.</summary>
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
                Seq = (ulong)i, StartQpc = t, EndQpc = t + F / 1000 * 3, PaintQpc = t + 1, Flags = (ushort)LedgerUiFlags.Painted,
                UiCycles = 2_000_000, UiCyclesTotal = (ulong)(i + 1) * 2_000_000, ProcessCyclesTotal = (ulong)(i + 1) * 5_000_000,
                AllocBytes = i == 50 ? 1000 : 0, Gc0Total = i >= 50 ? 1 : 0, GcPauseTicksTotal = i >= 50 ? 20_000 : 0,
                MissedVsyncsTotal = i >= 90 ? 2 : 0,
            };
            rt[i] = new LedgerRenderTurn
            {
                Seq = (ulong)i, StartQpc = t + F / 1000 * 4, SlotOpenQpc = t + F / 1000 * 4, DoneQpc = t + F / 1000 * 5, EndQpc = t + F / 1000 * 5,
                Cycles = 1_000_000, Kind = (byte)LedgerTurnKind.Fresh, Flags = (byte)LedgerTurnFlags.Presented,
            };
            gpu[i] = new LedgerGpuFrame { Seq = (ulong)i, GpuMs = 1.5f, PassCount = 2, TileRasterMs = 0.5f, CompositeMs = 1f };
        }
        return new LedgerSnapshot
        {
            QpcFrequency = F, CyclesPerMs = Cpm, Processors = 10, OriginQpc = F, EndQpc = 2 * F,
            Ui = ui, Render = rt, Gpu = gpu,
            Memory = [new LedgerMemorySample { Qpc = F, WorkingSetBytes = 100 << 20, ProcessCpuTicksTotal = 0, TotalAllocatedBytes = 0 },
                      new LedgerMemorySample { Qpc = 2 * F, WorkingSetBytes = 300 << 20, ProcessCpuTicksTotal = 5_000_000, TotalAllocatedBytes = 4096 }],
            Audio = [new LedgerAudioSample { Qpc = F, DeviceDryEdgesTotal = 3, XrunsTotal = 1, PaddingMinFrames = 4800, Rate = 48000 },
                     new LedgerAudioSample { Qpc = 2 * F, DeviceDryEdgesTotal = 5, XrunsTotal = 1, PaddingMinFrames = 480, Rate = 48000 }],
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
    }

    [Fact]
    public void The_cpu_split_adds_up_to_the_process()
    {
        var s = Diagnostics.FrameBenchMath.Summarize("x", Window());
        Assert.Equal(2, s["uiCpuMs.p50"], 6);
        Assert.Equal(1, s["renderCpuMs.p95"], 6);
        Assert.Equal(2, s["otherCpuMs.p50"], 6);
        // 99 intervals of 10 ms: ui 2 / render 1 / process 5 ms each.
        Assert.Equal(0.2, s["uiCores"], 6);
        Assert.Equal(0.1, s["renderCores"], 6);
        Assert.Equal(0.2, s["otherCores"], 6);
        Assert.Equal(0.5, s["processCores"], 6);
        Assert.Equal(5.0, s["processCpuPct"], 6);                          // 0.5 cores of 10
        Assert.Equal(5.0, s["processCpuPctTimes"], 6);                     // 500 ms of CPU over 1 s × 10
        Assert.Equal(s["processCores"], s["uiCores"] + s["renderCores"] + s["otherCores"], 6);
    }

    [Fact]
    public void Gpu_allocations_gc_memory_and_audio()
    {
        var s = Diagnostics.FrameBenchMath.Summarize("x", Window());
        Assert.Equal(1.5, s["gpuMs.p50"], 5);
        Assert.Equal(15.0, s["gpuBusyPct"], 4);                           // 100 × 1.5 ms over 1000 ms
        Assert.Equal(0.5, s["gpuPass.tileRasterMs"], 5);
        Assert.Equal(1.0, s["gpuPass.compositeMs"], 5);
        Assert.Equal(10, s["uiAllocBytesPerFrame"], 6);
        Assert.Equal(4096, s["processAllocBytesPerSec"], 6);
        Assert.Equal(1, s["gc0"]);
        Assert.Equal(2, s["gcPauseMs"], 6);
        Assert.Equal(2, s["missedVsyncs"]);
        Assert.Equal(200, s["workingSetMB.avg"], 6);
        Assert.Equal(300, s["workingSetMB.peak"], 6);
        Assert.Equal(2, s["audioDeviceDryEdges"]);
        Assert.Equal(0, s["audioXruns"]);
        Assert.Equal(10, s["audioPaddingMinMs"], 6);
    }

    [Fact]
    public void An_empty_window_is_NaN_not_a_crash()
    {
        var s = Diagnostics.FrameBenchMath.Summarize("idle", new LedgerSnapshot { QpcFrequency = F, CyclesPerMs = Cpm, OriginQpc = F, EndQpc = 2 * F });
        Assert.Equal(0, s["frames"]);
        Assert.True(double.IsNaN(s["uiCpuMs.p50"]));
        Assert.True(double.IsNaN(s["uiCores"]));
    }

    [Fact]
    public void Json_carries_every_metric_and_nulls_NaN()
    {
        var a = Diagnostics.FrameBenchMath.Summarize("home-scroll", Window());
        var b = new Diagnostics.FrameBenchScenarioSummary("lyrics-line", "no line-synced lyrics", []);
        var c = Diagnostics.FrameBenchMath.Summarize("idle", new LedgerSnapshot { QpcFrequency = F, CyclesPerMs = Cpm, OriginQpc = F, EndQpc = 2 * F });
        string json = Diagnostics.FrameBenchMath.Json("0.3.3", "warm", real: true, 12, 120, "1770x1140", 10, 2, Cpm, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), [a, b, c]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wavee-frame-bench/1", root.GetProperty("schema").GetString());
        Assert.Equal("real", root.GetProperty("data").GetString());
        Assert.Equal("warm", root.GetProperty("label").GetString());
        var sc = root.GetProperty("scenarios");
        Assert.Equal(3, sc.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, sc[2].GetProperty("metrics").GetProperty("uiCpuMs.p50").ValueKind);
        var metrics = sc[0].GetProperty("metrics");
        Assert.Equal(a.Metrics.Count, metrics.EnumerateObject().Count());
        Assert.Equal(100, metrics.GetProperty("presentsPerSec").GetDouble(), 6);
        Assert.Equal(0, metrics.GetProperty("gpuPass.offscreenMs").GetDouble());
        Assert.Equal("no line-synced lyrics", sc[1].GetProperty("skipped").GetString());
        string table = Diagnostics.FrameBenchMath.Table([a, b]);
        Assert.Contains("home-scroll", table);
        Assert.Contains("lyrics-line       skipped: no line-synced lyrics", table);
        var o = new Diagnostics.FrameBenchScenarioSummary("ledger-overhead", null,
            [new("framesPerSec.off", 120), new("framesPerSec.on", 120), new("overheadUiCpuUsPerFrame", 12.5)]);
        Assert.Contains("(+12.5)", Diagnostics.FrameBenchMath.Table([o]));
    }

    [Theory]
    [InlineData("presentsPerSec", 1)]
    [InlineData("frames", 0)]
    [InlineData("uiCpuMs.p99", -1)]
    [InlineData("gpuBusyPct", -1)]
    public void Metric_direction(string key, int better) => Assert.Equal(better, Diagnostics.FrameBenchMath.Better(key));
}
