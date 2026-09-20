// ── Wavee.Tests/ProbeArmsTests.cs — the Wave-6 probe arms' pure decisions (G-016, G-218) ────────────────────────────────
//
// `Screens/Diagnostics.Probe.Arms.cs` drives the process, the frame loop and the OS; every decision it makes is in that
// file's PURE section (§4: `ProbeOptions`, `SimRules`, `Bench`, `QrPng`) and pinned here: the argv parse that replaced 0.2.9's environment knobs, the notification
// simulator's verdict table, the bench math and the JSON `ops/build/bench-wavee.ps1` reads, and the `--qr-dump` PNG writer.
// Nothing here starts the engine, opens a window, touches a settings store or writes outside memory.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Wavee.Tests;

public class ProbeOptionsTests
{
    [Fact]
    public void No_flags_means_no_arm_and_every_knob_at_its_default()
    {
        var o = Diagnostics.ProbeOptions.Parse([]);
        Assert.False(o.PerfBench);
        Assert.False(o.StartupBench);
        Assert.False(o.LyricsAdvance);
        Assert.Equal("", o.CrashProbe);
        Assert.Equal("", o.ProbeOut);
        Assert.False(o.WantsConsole);
        Assert.Equal(5400, o.PlaybackFrames);
        Assert.Equal(3600, o.LyricsFrames);
        Assert.Equal(10, o.IdleSec);
        Assert.Equal(12, o.NavHops);
        Assert.Equal(8, o.OpenHops);
    }

    [Fact]
    public void Each_arm_flag_is_read_and_asks_for_the_console()
    {
        Assert.True(Diagnostics.ProbeOptions.Parse(["--fake", "--perf-bench"]).PerfBench);
        Assert.True(Diagnostics.ProbeOptions.Parse(["--startup-bench"]).StartupBench);
        var lyrics = Diagnostics.ProbeOptions.Parse(["--lyrics-advance-probe"]);
        Assert.True(lyrics.LyricsAdvance);
        Assert.True(lyrics.WantsConsole);
    }

    [Theory]
    [InlineData(new[] { "--crash-probe" }, "throw")]
    [InlineData(new[] { "--crash-probe", "failfast" }, "failfast")]
    [InlineData(new[] { "--crash-probe", "throw" }, "throw")]
    [InlineData(new[] { "--crash-probe", "--fake" }, "throw")]
    [InlineData(new[] { "--crash-probe", "banana" }, "throw")]
    public void Crash_probe_defaults_to_throw_and_knows_only_two_modes(string[] args, string expected)
        => Assert.Equal(expected, Diagnostics.ProbeOptions.Parse(args).CrashProbe);

    [Fact]
    public void Probe_out_takes_its_value_but_never_swallows_the_next_flag()
    {
        Assert.Equal(@"C:\bench", Diagnostics.ProbeOptions.Parse(["--perf-bench", "--probe-out", @"C:\bench"]).ProbeOut);
        Assert.Equal("", Diagnostics.ProbeOptions.Parse(["--probe-out", "--perf-bench"]).ProbeOut);
        Assert.Equal("", Diagnostics.ProbeOptions.Parse(["--probe-out"]).ProbeOut);
    }

    [Theory]
    [InlineData("--bench-idle-sec", "30", 30)]
    [InlineData("--bench-idle-sec", "2", 10)]       // below the floor → the default, not a clamp (0.2.9's EnvInt)
    [InlineData("--bench-idle-sec", "121", 10)]
    [InlineData("--bench-idle-sec", "ten", 10)]
    public void Idle_seconds_in_range_or_the_default(string flag, string value, int expected)
        => Assert.Equal(expected, Diagnostics.ProbeOptions.Parse([flag, value]).IdleSec);

    [Fact]
    public void Every_numeric_knob_honours_its_own_range()
    {
        var o = Diagnostics.ProbeOptions.Parse(["--bench-nav-hops", "60", "--bench-open-hops", "2",
            "--probe-playback-frames", "120", "--probe-lyrics-frames", "36000"]);
        Assert.Equal(60, o.NavHops);
        Assert.Equal(2, o.OpenHops);
        Assert.Equal(120, o.PlaybackFrames);
        Assert.Equal(36000, o.LyricsFrames);

        var bad = Diagnostics.ProbeOptions.Parse(["--bench-nav-hops", "3", "--bench-open-hops", "41",
            "--probe-playback-frames", "-5", "--probe-lyrics-frames"]);
        Assert.Equal(12, bad.NavHops);
        Assert.Equal(8, bad.OpenHops);
        Assert.Equal(5400, bad.PlaybackFrames);
        Assert.Equal(3600, bad.LyricsFrames);
    }
}

public class NotificationSimulatorRulesTests
{
    static DateTimeOffset At(int hour) => new(2026, 9, 13, hour, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(NotifyTopic.NewAlbums)]
    [InlineData(NotifyTopic.ReleaseDrops)]
    [InlineData(NotifyTopic.LibraryActivity)]
    public void Off_drops_every_topic_before_anything_else(NotifyTopic topic)
        => Assert.Equal(Diagnostics.SimPath.Dropped, Diagnostics.SimRules.PathFor(topic, NotifyLevel.Off));

    [Fact]
    public void The_path_follows_the_topic()
    {
        Assert.Equal(Diagnostics.SimPath.Scheduled, Diagnostics.SimRules.PathFor(NotifyTopic.ReleaseDrops, NotifyLevel.Windows));
        Assert.Equal(Diagnostics.SimPath.Scheduled, Diagnostics.SimRules.PathFor(NotifyTopic.DaylistRefresh, NotifyLevel.InApp));
        Assert.Equal(Diagnostics.SimPath.Activity, Diagnostics.SimRules.PathFor(NotifyTopic.LibraryActivity, NotifyLevel.InApp));
        Assert.Equal(Diagnostics.SimPath.Live, Diagnostics.SimRules.PathFor(NotifyTopic.Concerts, NotifyLevel.InApp));
        Assert.Equal(Diagnostics.SimPath.Live, Diagnostics.SimRules.PathFor(NotifyTopic.AppUpdates, NotifyLevel.Windows));
    }

    [Fact]
    public void A_scheduled_topic_below_windows_is_recorded_in_app_and_never_reaches_the_os()
    {
        var on = new NotificationPolicy(true, true, QuietHours.Off);
        var off = new NotificationPolicy(false, true, QuietHours.Off);
        Assert.Equal(Diagnostics.SimOutcome.RecordedInApp, Diagnostics.SimRules.ScheduledPrecheck(in on, NotifyLevel.InApp, true)!.Value.Outcome);
        Assert.Equal(Diagnostics.SimOutcome.RecordedInApp, Diagnostics.SimRules.ScheduledPrecheck(in off, NotifyLevel.Windows, true)!.Value.Outcome);
        Assert.Equal(Diagnostics.SimOutcome.Unavailable, Diagnostics.SimRules.ScheduledPrecheck(in on, NotifyLevel.Windows, false)!.Value.Outcome);
        Assert.Null(Diagnostics.SimRules.ScheduledPrecheck(in on, NotifyLevel.Windows, true));
    }

    [Fact]
    public void A_scheduled_result_carries_the_os_delivery_time()
    {
        var at = At(9);
        Assert.Equal(new Diagnostics.SimResult(Diagnostics.SimOutcome.Scheduled, at), Diagnostics.SimRules.ScheduledOutcome(at));
        Assert.Equal(Diagnostics.SimOutcome.Unavailable, Diagnostics.SimRules.ScheduledOutcome(null).Outcome);
    }

    [Fact]
    public void A_raised_row_is_a_banner()
    {
        var policy = new NotificationPolicy(true, true, new QuietHours(true, 22, 8));
        Assert.Equal(new Diagnostics.SimResult(Diagnostics.SimOutcome.Banner, null),
            Diagnostics.SimRules.LiveOutcome(true, in policy, NotifyLevel.Windows, At(23)));
    }

    [Fact]
    public void Quiet_hours_at_the_windows_level_defer_the_banner_to_the_next_audible_moment()
    {
        var policy = new NotificationPolicy(true, true, new QuietHours(true, 22, 8));
        var r = Diagnostics.SimRules.LiveOutcome(false, in policy, NotifyLevel.Windows, At(23));
        Assert.Equal(Diagnostics.SimOutcome.BannerQuietDeferred, r.Outcome);
        Assert.Equal(8, r.At!.Value.Hour);
        Assert.True(r.At.Value > At(23));
    }

    [Fact]
    public void Not_raised_otherwise_is_recorded_in_app()
    {
        var quiet = new NotificationPolicy(true, true, new QuietHours(true, 22, 8));
        var master = new NotificationPolicy(false, true, new QuietHours(true, 22, 8));
        Assert.Equal(Diagnostics.SimOutcome.RecordedInApp, Diagnostics.SimRules.LiveOutcome(false, in quiet, NotifyLevel.InApp, At(23)).Outcome);
        Assert.Equal(Diagnostics.SimOutcome.RecordedInApp, Diagnostics.SimRules.LiveOutcome(false, in quiet, NotifyLevel.Windows, At(12)).Outcome);
        Assert.Equal(Diagnostics.SimOutcome.RecordedInApp, Diagnostics.SimRules.LiveOutcome(false, in master, NotifyLevel.Windows, At(23)).Outcome);
    }

    [Fact]
    public void A_simulated_timestamp_always_beats_both_watermarks_and_now()
    {
        Assert.Equal(1_000L, Diagnostics.SimRules.NextTimestamp(1_000, 500, 900));
        Assert.Equal(1_001L, Diagnostics.SimRules.NextTimestamp(1_000, 1_000, 10));    // the same-millisecond panel open
        Assert.Equal(2_001L, Diagnostics.SimRules.NextTimestamp(1_000, 10, 2_000));
    }

    [Fact]
    public void The_escalator_watermark_is_primed_only_when_it_never_ran()
    {
        Assert.Equal(999L, Diagnostics.SimRules.PrimedWatermark(0, 1_000));
        Assert.Null(Diagnostics.SimRules.PrimedWatermark(42, 1_000));
    }

    [Fact]
    public void The_plan_raises_the_simulated_row_only_by_its_id()
    {
        Notification[] items =
        [
            NotifyRows.ForSocial("real", 3, true, "t", null, SocialActionType.Navigate, null, null, null),
            NotifyRows.ForSocial("sim:follower:1", 2, true, "t", null, SocialActionType.Navigate, null, null, null),
        ];
        Assert.True(Diagnostics.SimRules.PlanRaises([1], items, "sim:follower:1"));
        Assert.False(Diagnostics.SimRules.PlanRaises([0], items, "sim:follower:1"));
        Assert.False(Diagnostics.SimRules.PlanRaises([7], items, "sim:follower:1"));   // an out-of-range index is not a raise
    }
}

public class BenchMathTests
{
    [Fact]
    public void Percentile_is_nearest_rank_over_a_sorted_copy()
    {
        var values = new List<double> { 9, 1, 5, 3, 7 };
        Assert.Equal(5, Diagnostics.Bench.Percentile(values, 0.50));
        Assert.Equal(9, Diagnostics.Bench.Percentile(values, 0.90));
        Assert.Equal(1, Diagnostics.Bench.Percentile(values, 0.0));
        Assert.Equal(new List<double> { 9, 1, 5, 3, 7 }, values);   // the input is not reordered
        Assert.Equal(0, Diagnostics.Bench.Percentile(new List<double>(), 0.5));
    }

    [Fact]
    public void Cpu_percent_is_task_manager_style_and_zero_without_wall_time()
    {
        Assert.Equal(25.0, Diagnostics.Bench.CpuPercent(cpuMs: 2000, elapsedSec: 2, processors: 4), 6);
        Assert.Equal(0.0, Diagnostics.Bench.CpuPercent(10, 0.0005, 4));
    }

    [Fact]
    public void Aggregate_averages_levels_and_peaks_the_rest()
    {
        var samples = new List<Diagnostics.Bench.Sample>
        {
            new(1, WorkingSetMB: 100, PrivateMB: 80, ManagedMB: 20, CpuPct: 10, FrameMsP50: 4, Images: 3, SceneLive: 900),
            new(2, WorkingSetMB: 140, PrivateMB: 95, ManagedMB: 25, CpuPct: 30, FrameMsP50: 5, Images: 7, SceneLive: 800),
        };
        var frames = new List<double> { 2, 4, 6, 8, 40 };
        var r = Diagnostics.Bench.Aggregate("home", 5, 1.5, samples, frames);
        Assert.Equal("home", r.Name);
        Assert.Equal(20, r.CpuAvgPct, 6);
        Assert.Equal(30, r.CpuPeakPct, 6);
        Assert.Equal(120, r.WorkingSetAvgMB, 6);
        Assert.Equal(140, r.WorkingSetPeakMB, 6);
        Assert.Equal(95, r.PrivatePeakMB, 6);
        Assert.Equal(25, r.ManagedPeakMB, 6);
        Assert.Equal(6, r.FrameMsP50, 6);
        Assert.Equal(40, r.FrameMsMax, 6);
        Assert.Equal(7, r.ImagesPeak);
        Assert.Equal(900, r.SceneLivePeak);
    }

    [Fact]
    public void The_perf_json_keeps_the_property_names_and_is_invariant_under_a_comma_culture()
    {
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = comma;
            var scenario = new Diagnostics.Bench.Scenario("idle", 600, 10.25, 1.5, 3.25, 180.5, 190.125, 150, 40, 1.5, 2.5, 16.75, 12, 3400);
            string json = Diagnostics.Bench.PerfJson("0.3.0-dev \"x\"", 8, [scenario]);
            foreach (string name in new[] { "\"version\":", "\"processors\":8", "\"scenarios\":[", "\"name\":\"idle\"", "\"frames\":600",
                         "\"durationSec\":10.25", "\"cpuAvgPct\":1.5", "\"cpuPeakPct\":3.25", "\"workingSetAvgMB\":180.5",
                         "\"workingSetPeakMB\":190.125", "\"privatePeakMB\":150", "\"managedPeakMB\":40", "\"frameMsP50\":1.5",
                         "\"frameMsP90\":2.5", "\"frameMsMax\":16.75", "\"imagesPeak\":12", "\"sceneLivePeak\":3400" })
                Assert.Contains(name, json);
            Assert.DoesNotContain("10,25", json);
            Assert.Contains("0.3.0-dev \\\"x\\\"", json);
            using var doc = System.Text.Json.JsonDocument.Parse(json);   // well-formed
            Assert.Equal(1, doc.RootElement.GetProperty("scenarios").GetArrayLength());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}

public class QrPngTests
{
    [Fact]
    public void Crc32_matches_the_standard_check_value_across_the_type_and_data_split()
        => Assert.Equal(0xCBF43926u, Diagnostics.QrPng.Crc32("1234"u8, "56789"u8));

    [Fact]
    public void The_png_has_the_signature_the_ihdr_and_a_well_formed_iend()
    {
        bool[,] m = { { true, false }, { false, true } };
        byte[] png = Diagnostics.QrPng.Encode(m, quiet: 1, scale: 3);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(new byte[] { 0, 0, 0, 13 }, png[8..12]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        int size = (2 + 2) * 3;
        Assert.Equal(size, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(size, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        Assert.Equal(8, png[24]);   // bit depth
        Assert.Equal(0, png[25]);   // colour type: grayscale
        uint ihdrCrc = (uint)((png[29] << 24) | (png[30] << 16) | (png[31] << 8) | png[32]);
        Assert.Equal(Diagnostics.QrPng.Crc32("IHDR"u8, png.AsSpan(16, 13)), ihdrCrc);
        Assert.Equal(new byte[] { 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82 }, png[^12..]);
    }

    [Fact]
    public void The_pixels_are_the_matrix_inside_its_quiet_zone()
    {
        bool[,] m = { { true } };   // one dark module
        byte[] png = Diagnostics.QrPng.Encode(m, quiet: 1, scale: 1);
        int idatLength = (png[33] << 24) | (png[34] << 16) | (png[35] << 8) | png[36];
        Assert.Equal("IDAT", Encoding.ASCII.GetString(png, 37, 4));
        using var z = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        // 3×3 image: each scanline is a filter byte (0) then three gray samples; only the centre is dark.
        Assert.Equal(new byte[] { 0, 255, 255, 255, 0, 255, 0, 255, 0, 255, 255, 255 }, raw.ToArray());
    }
}
