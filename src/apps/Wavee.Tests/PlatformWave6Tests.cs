// ── Wavee.Tests/PlatformWave6Tests.cs — owner S's Platform CORE (Platform/Platform.Settings.cs) ──────────────────────
//
// The settings isolation rule (a `--fake` or `--profile` run never reads or writes HKCU), the profile-file store, the
// developer-mode signals, NetworkPolicy (ch 29 §8: "no test today — add one"), the ambient power verdict and its debounce
// (ch 29 §8), LogCapturePolicy, WaveeVersionInfo (G-198), the run marker and the crash prompt (G-094), the factory-reset
// marker plan, and WaveeLogSessions' line and session parse (ch 27 §8). Pure: no registry, no real profile — the one file
// store writes under the temp directory and deletes it; `Platform`'s statics are put back by every fact that moves them.

using System.Globalization;
using FluentGpu.WindowsApi.Network;
using FluentGpu.WindowsApi.Power;
using Xunit;

namespace Wavee.Tests;

// ── the settings isolation ───────────────────────────────────────────────────────────────────────────────────────────

[Collection(PlatformCollection.Name)]
public class SettingsIsolationTests
{
    [Fact]
    public void A_fake_run_is_in_memory_even_with_a_profile()
    {
        Assert.Equal(SettingsBacking.Memory, Platform.SettingsBackingFor(fake: true, profileRoot: ""));
        Assert.Equal(SettingsBacking.Memory, Platform.SettingsBackingFor(fake: true, profileRoot: @"C:\scratch\profile"));
    }

    [Fact]
    public void A_profile_run_keeps_its_settings_in_the_profile_and_only_a_plain_launch_uses_the_registry()
    {
        Assert.Equal(SettingsBacking.ProfileFile, Platform.SettingsBackingFor(fake: false, profileRoot: @"C:\scratch\profile"));
        Assert.Equal(SettingsBacking.Registry, Platform.SettingsBackingFor(fake: false, profileRoot: ""));
        Assert.Equal(SettingsBacking.Registry, Platform.SettingsBackingFor(fake: false, profileRoot: null));
    }

    [Fact]
    public void The_defaults_only_store_answers_defaults_and_drops_writes()
    {
        var store = DefaultsOnlySettings.Instance;
        store.Set(Platform.Keys.SidebarDesign, 2);
        Assert.Equal(Platform.Keys.SidebarDesign.Default, store.Get(Platform.Keys.SidebarDesign));
    }

    [Fact]
    public void The_fake_store_keeps_a_demo_write_in_memory_over_the_defaults()
    {
        var fake = new Diagnostics.Headless.OverlaySettings(DefaultsOnlySettings.Instance);
        Assert.Equal(0, fake.Get(Platform.Keys.SidebarDesign));                 // a real profile's Library V3 never leaks in
        fake.Set(Platform.Keys.SidebarDesign, 1);
        Assert.Equal(1, fake.Get(Platform.Keys.SidebarDesign));
        Assert.Equal(Platform.Keys.ThemeMode.Default, fake.Get(Platform.Keys.ThemeMode));
    }

    static string TempFile() => Path.Combine(Path.GetTempPath(), "wavee-tests-" + Guid.NewGuid().ToString("N"), Platform.ProfileSettingsFileName);

    [Fact]
    public void The_profile_file_store_round_trips_every_scalar_and_survives_a_reopen()
    {
        string path = TempFile();
        try
        {
            var a = new FileAppSettings(path);
            a.Set(Platform.Keys.ThemeMode, 2);
            a.Set(Platform.Keys.MarqueeEnabled, false);
            a.Set(Platform.Keys.ZoomLevel, 1.25f);
            a.Set(Platform.Keys.UiCulture, "nl-NL");
            a.Set(Platform.Keys.UpdateLastCheckedMs, 1234567890123L);
            a.Set(Platform.Keys.VideoCustomAspectRatio, 2.35);

            var b = new FileAppSettings(path);
            Assert.Equal(2, b.Get(Platform.Keys.ThemeMode));
            Assert.False(b.Get(Platform.Keys.MarqueeEnabled));
            Assert.Equal(1.25f, b.Get(Platform.Keys.ZoomLevel));
            Assert.Equal("nl-NL", b.Get(Platform.Keys.UiCulture));
            Assert.Equal(1234567890123L, b.Get(Platform.Keys.UpdateLastCheckedMs));
            Assert.Equal(2.35, b.Get(Platform.Keys.VideoCustomAspectRatio));
            Assert.Equal(Platform.Keys.SidebarDesign.Default, b.Get(Platform.Keys.SidebarDesign));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void A_missing_or_corrupt_profile_file_answers_defaults_and_never_throws()
    {
        string path = TempFile();
        try
        {
            Assert.Equal(Platform.Keys.ThemeMode.Default, new FileAppSettings(path).Get(Platform.Keys.ThemeMode));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            var corrupt = new FileAppSettings(path);
            Assert.Equal(Platform.Keys.ThemeMode.Default, corrupt.Get(Platform.Keys.ThemeMode));
            corrupt.Set(Platform.Keys.ThemeMode, 1);   // a fresh start, not a crash
            Assert.Equal(1, new FileAppSettings(path).Get(Platform.Keys.ThemeMode));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void A_value_of_the_wrong_shape_in_the_file_answers_the_default()
    {
        string path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"theme.mode\":\"dark\"}");
            Assert.Equal(Platform.Keys.ThemeMode.Default, new FileAppSettings(path).Get(Platform.Keys.ThemeMode));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } }
    }
}

// ── developer mode ───────────────────────────────────────────────────────────────────────────────────────────────────

[Collection(PlatformCollection.Name)]
public class DeveloperModeTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void The_fps_hud_needs_both_switches(bool developer, bool overlay, bool shows)
        => Assert.Equal(shows, Platform.Developer.ShowsFpsOverlay(developer, overlay));

    [Fact]
    public void Load_seeds_the_signals_and_the_writers_persist_then_publish()
    {
        var store = new MemoryAppSettings();
        store.Set(Platform.Keys.DeveloperMode, true);
        Platform.UseSettings(store);
        try
        {
            Platform.Developer.Load(store);
            Assert.True(Platform.Developer.Enabled.Peek());
            Assert.False(Platform.Developer.FpsOverlay.Peek());
            Platform.Developer.SetStageRects(true);
            Assert.True(store.Get(Platform.Keys.StageRects));
            Assert.True(Diagnostics.StageRects.Peek());
            Platform.Developer.Set(false);
            Assert.False(store.Get(Platform.Keys.DeveloperMode));
            Assert.False(Platform.Developer.Enabled.Peek());
        }
        finally
        {
            Platform.UseSettings(null);
            Platform.Developer.Load(DefaultsOnlySettings.Instance);
        }
    }
}

// ── NetworkPolicy (ch 29 W9, §8) ─────────────────────────────────────────────────────────────────────────────────────

[Collection(PlatformCollection.Name)]
public class NetworkPolicyTests
{
    static readonly NetworkCost Metered = new(NetworkCostKind.Fixed, false, false, false);
    static readonly NetworkCost Variable = new(NetworkCostKind.Variable, false, false, true);
    static readonly NetworkCost Unrestricted = new(NetworkCostKind.Unrestricted, false, false, false);

    [Theory]
    [InlineData(2, 1, 1)]   // 320 on a metered link with a 160 cap → 160
    [InlineData(0, 2, 0)]   // the cap never RAISES a lower choice
    [InlineData(3, 2, 2)]   // lossless is capped exactly like 320 (D21)
    [InlineData(9, 9, 3)]   // both clamp into the ladder
    [InlineData(-4, 1, 0)]
    public void A_metered_link_caps_the_rung(int user, int cap, int expected)
    {
        Assert.Equal(expected, Platform.Network.EffectiveQuality(user, cap, in Metered));
        Assert.Equal(expected, Platform.Network.EffectiveQuality(user, cap, in Variable));
    }

    [Fact]
    public void An_unmetered_or_unknown_link_is_never_capped()
    {
        Assert.Equal(2, Platform.Network.EffectiveQuality(2, 0, in Unrestricted));
        var unknown = NetworkCost.Unknown;
        Assert.Equal(3, Platform.Network.EffectiveQuality(3, 0, in unknown));
        Assert.False(Platform.Network.ShouldDeferPrefetch(in unknown));
        Assert.True(Platform.Network.ShouldDeferPrefetch(in Metered));
    }

    [Fact]
    public void The_video_cap_is_unlimited_unless_metered_with_a_positive_stored_cap()
    {
        Assert.Equal(480, Platform.Network.EffectiveVideoMaxHeight(480, in Metered));
        Assert.Equal(int.MaxValue, Platform.Network.EffectiveVideoMaxHeight(0, in Metered));    // 0 IN means unlimited
        Assert.Equal(int.MaxValue, Platform.Network.EffectiveVideoMaxHeight(480, in Unrestricted));
    }

    [Fact]
    public void Apply_publishes_a_change_once_and_the_live_snapshot_follows()
    {
        try
        {
            Assert.True(Platform.Network.Apply(Variable));
            Assert.Equal(Variable, Platform.Network.Current);
            Assert.True(Platform.Network.IsMetered);
            Assert.True(Platform.Network.Metered.Peek());
            Assert.Equal(Variable, Platform.Network.Cost.Peek());
            Assert.False(Platform.Network.Apply(Variable));   // the 60 s poll republishing the same cost is silent
        }
        finally { Platform.Network.Apply(NetworkCost.Unknown); }
        Assert.False(Platform.Network.IsMetered);
    }
}

// ── the ambient power verdict (ch 29 W25, §8) ────────────────────────────────────────────────────────────────────────

public class AmbientPowerPolicyTests
{
    static PowerStatus S(PowerSource source, bool battery, bool saver) => new(source, battery, battery ? 80 : null, false, saver, null);

    [Fact]
    public void The_verdict_table()
    {
        Assert.True(Platform.AmbientPower.Plugged(true, S(PowerSource.Ac, battery: true, saver: false)));
        Assert.False(Platform.AmbientPower.Plugged(true, S(PowerSource.Dc, battery: true, saver: false)));
        Assert.True(Platform.AmbientPower.Plugged(true, S(PowerSource.Dc, battery: false, saver: false)));      // a desktop
        Assert.True(Platform.AmbientPower.Plugged(true, S(PowerSource.Unknown, battery: true, saver: false)));  // unknown line
        Assert.False(Platform.AmbientPower.Plugged(true, S(PowerSource.Ac, battery: true, saver: true)));       // energy saver
        Assert.False(Platform.AmbientPower.Plugged(true, S(PowerSource.Ac, battery: false, saver: true)));
        Assert.True(Platform.AmbientPower.Plugged(false, default));                                             // a failed read
    }

    [Fact]
    public void The_loop_rate_is_the_design_cadence()
    {
        Assert.Equal((float)Design.Cadence.PluggedLoopHz, Platform.AmbientPower.LoopHzFor(true));
        Assert.Equal((float)Design.Cadence.BatteryLoopHz, Platform.AmbientPower.LoopHzFor(false));
    }

    [Fact]
    public void A_reading_must_hold_for_the_debounce_before_the_cadence_flips()
    {
        const long F = 1000;   // ticks per second
        var d = Platform.AmbientPower.Debounce.Start(true, now: 0);
        Assert.True(d.Applied);
        Assert.False(Platform.AmbientPower.Step(ref d, reading: false, now: 1000, F));   // first DC read re-arms the window
        Assert.False(Platform.AmbientPower.Step(ref d, reading: false, now: 2500, F));   // held 1.5 s: not yet
        Assert.True(Platform.AmbientPower.Step(ref d, reading: false, now: 3000, F));    // held 2 s: apply, once
        Assert.False(d.Applied);
        Assert.False(Platform.AmbientPower.Step(ref d, reading: false, now: 9000, F));
    }

    [Fact]
    public void A_blip_shorter_than_the_window_never_flips_the_cadence()
    {
        const long F = 1000;
        var d = Platform.AmbientPower.Debounce.Start(true, now: 0);
        Assert.False(Platform.AmbientPower.Step(ref d, false, 2000, F));
        Assert.False(Platform.AmbientPower.Step(ref d, true, 3000, F));    // back on AC before the hold completed
        Assert.False(Platform.AmbientPower.Step(ref d, true, 9000, F));
        Assert.True(d.Applied);
    }
}

// ── LogCapturePolicy (ch 27 §8) ──────────────────────────────────────────────────────────────────────────────────────

[Collection(PlatformCollection.Name)]
public class LogCapturePolicyTests
{
    [Fact]
    public void Minus_one_is_the_build_default_and_anything_else_clamps_to_trace_through_error()
    {
        Assert.Equal(WaveeLogLevel.Info, LogCapturePolicy.Resolve(-1, WaveeLogLevel.Info));
        Assert.Equal(WaveeLogLevel.Debug, LogCapturePolicy.Resolve(-1, WaveeLogLevel.Debug));
        Assert.Equal(WaveeLogLevel.Warning, LogCapturePolicy.Resolve((int)WaveeLogLevel.Warning, WaveeLogLevel.Info));
        Assert.Equal(WaveeLogLevel.Error, LogCapturePolicy.Resolve((int)WaveeLogLevel.Critical, WaveeLogLevel.Info));   // never offered
        Assert.Equal(WaveeLogLevel.Error, LogCapturePolicy.Resolve(99, WaveeLogLevel.Info));
    }

    [Fact]
    public void The_build_default_is_stored_as_minus_one_so_a_later_default_change_carries_forward()
    {
        Assert.Equal(-1, LogCapturePolicy.ToSetting(WaveeLogLevel.Info, WaveeLogLevel.Info));
        Assert.Equal((int)WaveeLogLevel.Trace, LogCapturePolicy.ToSetting(WaveeLogLevel.Trace, WaveeLogLevel.Info));
    }

    [Fact]
    public void Verbose_is_derived_from_the_live_level_and_off_returns_to_the_build_default()
    {
        Assert.True(LogCapturePolicy.IsVerbose(WaveeLogLevel.Debug));
        Assert.True(LogCapturePolicy.IsVerbose(WaveeLogLevel.Trace));
        Assert.False(LogCapturePolicy.IsVerbose(WaveeLogLevel.Info));
        Assert.Equal(WaveeLogLevel.Trace, LogCapturePolicy.VerboseTarget(true, WaveeLogLevel.Info));
        Assert.Equal(WaveeLogLevel.Debug, LogCapturePolicy.VerboseTarget(false, WaveeLogLevel.Debug));
    }

    [Fact]
    public void The_file_level_is_upward_only_against_the_ring()
    {
        Assert.Equal(WaveeLogLevel.Warning, LogCapturePolicy.EffectiveFileLevel(WaveeLogLevel.Warning, WaveeLogLevel.Debug));
        Assert.Equal(WaveeLogLevel.Error, LogCapturePolicy.EffectiveFileLevel(WaveeLogLevel.Info, WaveeLogLevel.Error));
    }

    [Fact]
    public void The_writers_move_the_running_log_and_the_setting_together()
    {
        var store = new MemoryAppSettings();
        var min = Log.MinLevel;
        var file = Log.FileMinLevel;
        try
        {
            LogCapturePolicy.SetVerbose(store, true);
            Assert.Equal(WaveeLogLevel.Trace, Log.MinLevel);
            Assert.Equal((int)WaveeLogLevel.Trace, store.Get(Platform.Keys.LogMinLevel));
            LogCapturePolicy.SetMinLevel(store, LogCapturePolicy.BuildDefaultMinLevel);
            Assert.Equal(-1, store.Get(Platform.Keys.LogMinLevel));
            LogCapturePolicy.SetFileLevel(store, WaveeLogLevel.Warning);
            Assert.Equal(WaveeLogLevel.Warning, Log.FileMinLevel);
            Assert.Equal((int)WaveeLogLevel.Warning, store.Get(Platform.Keys.LogFileMinLevel));
        }
        finally { Log.MinLevel = min; Log.FileMinLevel = file; }
    }
}

// ── WaveeVersionInfo (G-198) ─────────────────────────────────────────────────────────────────────────────────────────

public class WaveeVersionInfoTests
{
    static Dictionary<string, string> Meta(string channel = "beta", string quad = "0.3.0.17") => new()
    {
        ["Channel"] = channel, ["PackageVersion"] = quad, ["Codename"] = "Crest", ["Commit"] = "d4227b3",
        ["BuildDate"] = "2026-09-13", ["FeedRelease"] = "", ["UpdateBaseUrl"] = "http://127.0.0.1:8099", ["StoreId"] = "9NJ",
    };

    [Fact]
    public void An_unstamped_build_is_a_dev_build_with_the_default_feed()
    {
        var v = WaveeVersionInfo.Parse(null, null);
        Assert.Equal("dev", v.SemVer);
        Assert.Equal("dev", v.Channel);
        Assert.True(v.IsDev);
        Assert.Equal("wavee-stable", v.FeedRelease);
        Assert.Equal(WaveeVersionInfo.DefaultUpdateBaseUrl, v.UpdateBaseUrl);
        Assert.Equal("Wavee dev", v.Display);
        Assert.Equal("dev", v.LastRunKey);
    }

    [Fact]
    public void A_stamped_beta_parses_every_field()
    {
        var v = WaveeVersionInfo.Parse("0.3.0-beta.1+build.17.sha.d4227b3", Meta());
        Assert.Equal("0.3.0-beta.1", v.SemVer);
        Assert.Equal("0.3.0", v.Core);
        Assert.Equal(1, v.Beta);
        Assert.Equal("0.3.0.17", v.Quad);
        Assert.False(v.IsDev);
        Assert.False(v.IsStore);
        Assert.Equal("wavee-stable", v.FeedRelease);
        Assert.Equal("http://127.0.0.1:8099/", v.UpdateBaseUrl);
        Assert.Equal("Wavee 0.3.0 “Crest” · Beta 1", v.Display);
        Assert.Equal("0.3.0.17", v.LastRunKey);
        Assert.Equal("Wavee 0.3.0 “Crest” · Beta 1 · build 0.3.0.17 · d4227b3 · 2026-09-13 · arm64", v.OneLine("arm64"));
    }

    [Fact]
    public void A_store_build_and_an_unstamped_codename()
    {
        var meta = Meta(channel: "store");
        meta["Codename"] = "";
        var v = WaveeVersionInfo.Parse("0.3.0", meta);
        Assert.True(v.IsStore);
        Assert.Equal("Wavee 0.3.0", v.Display);   // no empty quotes
    }

    [Theory]
    [InlineData(null, WaveeVersionInfo.DefaultUpdateBaseUrl)]
    [InlineData("  ", WaveeVersionInfo.DefaultUpdateBaseUrl)]
    [InlineData("http://x/feed", "http://x/feed/")]
    [InlineData(" http://x/feed/ ", "http://x/feed/")]
    public void The_base_url_always_ends_in_a_slash(string? raw, string expected)
        => Assert.Equal(expected, WaveeVersionInfo.NormalizeUpdateBaseUrl(raw));
}

// ── the run marker and the crash prompt (G-094) ──────────────────────────────────────────────────────────────────────

public class RunMarkerTests
{
    [Theory]
    [InlineData("", RunOutcome.Unknown)]
    [InlineData(RunMarker.Clean, RunOutcome.Clean)]
    [InlineData(RunMarker.Running, RunOutcome.Unclean)]
    [InlineData(RunMarker.Crashed, RunOutcome.Unclean)]
    public void Begin_reads_the_previous_run_and_marks_this_one_running(string previous, RunOutcome expected)
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.RunMarker, previous);
        Assert.Equal(expected, RunMarker.Begin(s));
        Assert.Equal(RunMarker.Running, s.Get(Platform.Keys.RunMarker));
    }

    [Fact]
    public void End_downgrades_only_our_own_running_mark_and_ends_the_unclean_streak()
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.RunMarker, RunMarker.Crashed);
        s.Set(Platform.Keys.UncleanExitOffered, true);
        RunMarker.End(s);
        Assert.Equal(RunMarker.Crashed, s.Get(Platform.Keys.RunMarker));   // never stomps a handler's "crashed"
        Assert.False(s.Get(Platform.Keys.UncleanExitOffered));
        s.Set(Platform.Keys.RunMarker, RunMarker.Running);
        RunMarker.End(s);
        Assert.Equal(RunMarker.Clean, s.Get(Platform.Keys.RunMarker));
    }

    [Fact]
    public void MarkCrashed_writes_crashed_and_rearms_the_offer()
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.UncleanExitOffered, true);
        RunMarker.MarkCrashed(s);
        Assert.Equal(RunMarker.Crashed, s.Get(Platform.Keys.RunMarker));
        Assert.False(s.Get(Platform.Keys.UncleanExitOffered));
    }
}

public class CrashPromptPolicyTests
{
    [Fact]
    public void A_managed_report_outranks_a_dump_and_an_unclean_exit()
    {
        var d = CrashPromptPolicy.Decide("C:\\logs\\crash-report-1.txt", "C:\\dumps\\w.dmp", RunOutcome.Unclean, false, false, false);
        Assert.Equal(CrashSource.ManagedReport, d.Source);
        Assert.Equal(CrashPromptMode.Dialog, d.Mode);
        Assert.Equal("C:\\logs\\crash-report-1.txt", d.ReportPath);
        Assert.Equal("C:\\dumps\\w.dmp", d.DumpPath);
    }

    [Fact]
    public void A_dump_without_a_report_is_the_second_rung()
    {
        var d = CrashPromptPolicy.Decide("", "C:\\dumps\\w.dmp", RunOutcome.Clean, false, false, false);
        Assert.Equal(CrashSource.WerDump, d.Source);
        Assert.Null(d.ReportPath);
    }

    [Fact]
    public void An_unclean_exit_is_offered_once_per_streak_never_after_an_update_and_never_when_opted_out()
    {
        Assert.Equal(CrashSource.UncleanExit, CrashPromptPolicy.Decide("", null, RunOutcome.Unclean, false, false, false).Source);
        Assert.Equal(CrashSource.None, CrashPromptPolicy.Decide("", null, RunOutcome.Unclean, false, false, uncleanExitOffered: true).Source);
        Assert.Equal(CrashSource.None, CrashPromptPolicy.Decide("", null, RunOutcome.Unclean, false, versionChanged: true, false).Source);
        Assert.Equal(CrashSource.None, CrashPromptPolicy.Decide("", null, RunOutcome.Unclean, optOut: true, false, false).Source);
        Assert.Equal(CrashSource.None, CrashPromptPolicy.Decide("", null, RunOutcome.Clean, false, false, false).Source);
    }

    [Fact]
    public void A_windows_crash_dump_never_prompts_a_profile_that_has_not_run_before()
    {
        // The CrashDumps folder is shared by every Wavee.exe on the machine; a fresh profile has no run of its own to blame.
        Assert.Equal(CrashSource.None, CrashPromptPolicy.Decide("", @"C:\dumps\w.dmp", RunOutcome.Unknown, false, false, false).Source);
        Assert.Equal(CrashSource.WerDump, CrashPromptPolicy.Decide("", @"C:\dumps\w.dmp", RunOutcome.Unclean, false, false, false).Source);
    }

    [Fact]
    public void Opting_out_turns_real_evidence_into_a_passive_toast()
    {
        var d = CrashPromptPolicy.Decide("r.txt", null, RunOutcome.Unclean, optOut: true, versionChanged: true, uncleanExitOffered: true);
        Assert.Equal(CrashSource.ManagedReport, d.Source);
        Assert.Equal(CrashPromptMode.Toast, d.Mode);
    }
}

// ── the factory-reset marker plan (G-094) ────────────────────────────────────────────────────────────────────────────

public class FactoryResetPlanTests
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "wavee-reset-plan", "Wavee");

    [Fact]
    public void The_marker_lists_only_extra_roots_outside_the_defaults()
    {
        string relocated = Path.Combine(Path.GetTempPath(), "wavee-reset-plan", "AudioCache");
        var lines = FactoryResetPlan.MarkerLines([relocated, Path.Combine(Root, "cache"), "  ", ""], [Root]);
        Assert.Equal(new[] { Path.GetFullPath(relocated) }, lines);
    }

    [Fact]
    public void The_roots_are_the_defaults_then_every_trimmed_non_blank_marker_line()
    {
        var roots = FactoryResetPlan.Roots([Root], ["  D:\\Cache  ", "", "   "]);
        Assert.Equal(new[] { Root, "D:\\Cache" }, roots);
    }

    [Fact]
    public void Under_is_case_insensitive_and_respects_the_segment_boundary()
    {
        Assert.True(FactoryResetPlan.IsUnder(Path.Combine(Root, "logs"), Root));
        Assert.True(FactoryResetPlan.IsUnder(Root.ToUpperInvariant(), Root));
        Assert.False(FactoryResetPlan.IsUnder(Root + "2", Root));   // "Wavee2" is not inside "Wavee"
    }
}

// ── WaveeLogSessions — the line and session parse (ch 27 §8) ─────────────────────────────────────────────────────────

public class WaveeLogSessionsTests
{
    static string L(long seq, long t, string sid, int pid, string rest, char level = 'I', string cat = "app")
        => "seq=" + seq + " tid=1 t=" + t + " sid=" + sid + " pid=" + pid + " " + level + " [" + cat + "] " + rest;

    static string Start(long t, string sid, int pid) => L(1, t, sid, pid, "startup - Wavee starting pid=" + pid);

    static Func<string, IEnumerable<string>> Reader(Dictionary<string, string[]> files) => path => files[path];

    [Fact]
    public void A_current_file_line_round_trips()
    {
        var e = new WaveeLogEntry(17, 1720512345678, WaveeLogLevel.Warning, "connect", "session.start", "started", null, 4, 12, null, null);
        Assert.True(WaveeLogSessions.TryParseLine(Log.FormatFileLine(in e), out var back, out bool start, out int pid, out string sid));
        Assert.False(start);
        Assert.Equal(17, back.Sequence);
        Assert.Equal(1720512345678, back.UnixMs);
        Assert.Equal(WaveeLogLevel.Warning, back.Level);
        Assert.Equal("connect", back.Category);
        Assert.Equal(4, back.ThreadId);
        Assert.Equal(Log.SessionId, sid);
        Assert.Equal(Environment.ProcessId, pid);
        Assert.StartsWith("session.start", back.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_shapes_parse_and_garbage_does_not()
    {
        Assert.True(WaveeLogSessions.TryParseLine("2026-01-02 03:04:05.678Z seq=3 tid=2 E [audio] boom", out var iso, out _, out _, out string noSid));
        Assert.Equal(WaveeLogLevel.Error, iso.Level);
        Assert.True(iso.UnixMs > 0);
        Assert.Equal("", noSid);
        Assert.True(WaveeLogSessions.TryParseLine("seq=9 tid=1 I [app] startup - Wavee starting pid=4242", out _, out bool start, out int pid, out _));
        Assert.True(start);
        Assert.Equal(4242, pid);   // recovered from the message body
        Assert.False(WaveeLogSessions.TryParseLine("hello world", out _, out _, out _, out _));
        Assert.False(WaveeLogSessions.TryParseLine("seq=1 tid=1 X [app] nope", out _, out _, out _, out _));
    }

    [Fact]
    public void Sessions_split_on_the_sid_span_files_drop_this_run_and_come_newest_first()
    {
        var files = new Dictionary<string, string[]>
        {
            ["a"] = [Start(1000, "aaaaaaaa", 10), L(2, 1001, "aaaaaaaa", 10, "x"), Start(2000, "bbbbbbbb", 11)],
            ["b"] = [L(2, 2001, "bbbbbbbb", 11, "y"), L(3, 2002, "bbbbbbbb", 11, "z"), Start(3000, "cccccccc", 12)],
        };
        var sessions = WaveeLogSessions.Split(["a", "b"], Reader(files), currentSessionId: "cccccccc", currentPid: 12);
        Assert.Equal(2, sessions.Count);
        Assert.Equal("bbbbbbbb", sessions[0].SessionId);   // newest first
        Assert.Equal(3, sessions[0].EntryCount);            // it spans the file boundary
        Assert.Equal(2000, sessions[0].StartUnixMs);
        Assert.Equal("aaaaaaaa", sessions[1].SessionId);
        Assert.Equal(2, sessions[1].EntryCount);

        var lines = WaveeLogSessions.RawLines(sessions[0], Reader(files));
        Assert.Equal(3, lines.Count);
        Assert.Equal(3, WaveeLogSessions.Load(sessions[0], Reader(files)).Length);
        Assert.Equal(2, WaveeLogSessions.Load(sessions[0], Reader(files), maxEntries: 2).Length);   // the LAST n
    }

    [Fact]
    public void Legacy_sid_less_files_split_on_the_start_marker_or_a_sequence_reset_and_exclude_this_pid()
    {
        var files = new Dictionary<string, string[]>
        {
            ["old"] =
            [
                "seq=1 tid=1 I [app] startup - Wavee starting pid=7", "seq=2 tid=1 I [app] x",
                "seq=1 tid=1 I [app] y",                                                         // a reset, no marker
                "seq=1 tid=1 I [app] startup - Wavee starting pid=99",
            ],
        };
        var sessions = WaveeLogSessions.Split(["old"], Reader(files), currentSessionId: "zzzzzzzz", currentPid: 99);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(1, sessions[0].EntryCount);
        Assert.Equal(7, sessions[1].Pid);
    }

    [Fact]
    public void An_unreadable_file_skips_only_itself()
    {
        var files = new Dictionary<string, string[]> { ["ok"] = [Start(1, "aaaaaaaa", 1), L(2, 2, "aaaaaaaa", 1, "x")] };
        IEnumerable<string> Read(string path) => path == "bad" ? throw new IOException("locked") : files[path];
        var sessions = WaveeLogSessions.Split(["bad", "ok"], Read, "zzzzzzzz", 0);
        Assert.Single(sessions);
        Assert.Equal(2, sessions[0].EntryCount);
    }

    [Fact]
    public void The_file_order_is_chronological_then_the_live_file()
    {
        var ordered = WaveeLogSessions.Chronological(["wavee-20260913.log", "wavee-20260912.log", "wavee-20260913-101010.log"], "wavee.log");
        Assert.Equal(new[] { "wavee-20260912.log", "wavee-20260913-101010.log", "wavee-20260913.log", "wavee.log" }, ordered);
    }

    [Fact]
    public void A_session_key_is_its_sid_or_pid_and_start_and_null_finds_the_newest()
    {
        var withSid = new WaveeLogSessions.Info([], 0, 0, 0, 1, 5000, 9, 3, "abcdef12");
        var legacy = new WaveeLogSessions.Info([], 0, 0, 0, 1, 4000, 7, 2, "");
        Assert.Equal("abcdef12", WaveeLogSessions.KeyOf(withSid));
        Assert.Equal("pid7@4000", WaveeLogSessions.KeyOf(legacy));
        var list = new List<WaveeLogSessions.Info> { withSid, legacy };
        Assert.Same(withSid, WaveeLogSessions.Find(list, null));
        Assert.Same(legacy, WaveeLogSessions.Find(list, "pid7@4000"));
        Assert.Null(WaveeLogSessions.Find(list, "nope"));
        Assert.Null(WaveeLogSessions.Find([], null));
    }
}
