// ── Wavee.Tests/AudioStressTests.cs — the `--stress-audio` arm's pure decisions (playback-smoothness plan §4.18; V-PA39; #167) ──
//
// `Screens/Diagnostics.Probe.cs` (its last section) drives the process, the pump and the OS; every decision it makes is PURE and
// pinned here: the argv parse and the `smoke` label (a run through the paced silent endpoint is never the on-box gate), the burner
// classes and the allocator plan, the pass rule, the run's state machine and its incident tally, and the printed ledger summary
// with its JSON line. Nothing here starts a thread, opens a device, touches a profile or reads the engine.

using System.Text.Json;
using Xunit;

namespace Wavee.Tests;

public class AudioStressTests
{
    static Diagnostics.StressOptions Parse(params string[] args)
    {
        Assert.True(Diagnostics.StressOptions.TryParse(args, out Diagnostics.StressOptions options, out string usage), usage);
        return options;
    }

    // ── option parsing ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_flags_is_a_sixty_second_smoke_run_with_no_load()
    {
        var o = Parse("--stress-audio");

        Assert.Equal("", o.FilePath);
        Assert.Equal("", o.TrackUri);
        Assert.Equal(0, o.Burners);
        Assert.Equal(0, o.MemoryMib);
        Assert.False(o.Minimize);
        Assert.False(o.BatterySaver);
        Assert.Equal(60, o.Seconds);
        Assert.False(o.AllowOne);
        Assert.False(o.Fake);
        Assert.False(o.HasSource);
        Assert.False(o.NeedsLogin);
        Assert.True(o.Smoke);
        Assert.Equal("smoke", o.Label);
    }

    [Fact]
    public void Every_knob_is_read()
    {
        var o = Parse("--stress-audio", "--file", @"C:\music\a.flac", "--burners", "2", "--memory-mib", "2048", "--minimize",
            "--battery-saver", "--seconds", "90", "--allow-one", "--profile", @"C:\scratch\profile");

        Assert.Equal(@"C:\music\a.flac", o.FilePath);
        Assert.Equal(2, o.Burners);
        Assert.Equal(2048, o.MemoryMib);
        Assert.True(o.Minimize);
        Assert.True(o.BatterySaver);
        Assert.Equal(90, o.Seconds);
        Assert.True(o.AllowOne);
        Assert.Equal(@"C:\scratch\profile", o.Profile);
        Assert.True(o.HasSource);
        Assert.False(o.NeedsLogin);
    }

    [Fact]
    public void A_file_or_a_track_makes_it_a_device_run_and_a_track_needs_the_session()
    {
        var file = Parse("--stress-audio", "--file", @"C:\music\a.flac");
        var track = Parse("--stress-audio", "--track", "spotify:track:11dFghVXANMlKmJXsNCbNl");

        Assert.False(file.Smoke);
        Assert.Equal("device", file.Label);
        Assert.False(file.NeedsLogin);
        Assert.False(track.Smoke);
        Assert.Equal("device", track.Label);
        Assert.True(track.NeedsLogin);
    }

    [Fact]
    public void Fake_makes_every_run_a_smoke_run_even_with_a_file()
    {
        // V-PA39: --fake goes through PacedSilentEndpoint, so whatever it plays it is not the on-box gate.
        var o = Parse("--stress-audio", "--fake", "--file", @"C:\music\a.flac");

        Assert.True(o.HasSource);
        Assert.True(o.Fake);
        Assert.True(o.Smoke);
        Assert.Equal("smoke", o.Label);
    }

    [Theory]
    [InlineData(new[] { "--stress-audio", "--burners" }, "--burners wants a value")]
    [InlineData(new[] { "--stress-audio", "--burners", "many" }, "--burners wants a whole number from 0 to 64")]
    [InlineData(new[] { "--stress-audio", "--burners", "65" }, "from 0 to 64")]
    [InlineData(new[] { "--stress-audio", "--memory-mib", "16385" }, "from 0 to 16384")]
    [InlineData(new[] { "--stress-audio", "--memory-mib", "-1" }, "--memory-mib wants a whole number")]
    [InlineData(new[] { "--stress-audio", "--seconds", "0" }, "--seconds wants a whole number from 1 to 3600")]
    [InlineData(new[] { "--stress-audio", "--seconds", "3601" }, "from 1 to 3600")]
    [InlineData(new[] { "--stress-audio", "--file", "--minimize" }, "--file wants a value")]
    [InlineData(new[] { "--stress-audio", "--bogus" }, "unknown argument '--bogus'")]
    [InlineData(new[] { "--stress-audio", "--file", "a.flac", "--track", "spotify:track:abc" }, "--file and --track cannot be combined")]
    [InlineData(new[] { "--stress-audio", "--track", "banana" }, "--track wants a spotify:track:<id> uri")]
    [InlineData(new[] { "--stress-audio", "--track", "spotify:track:" }, "--track wants a spotify:track:<id> uri")]
    [InlineData(new[] { "--stress-audio", "--fake", "--track", "spotify:track:abc" }, "--fake has no Spotify session")]
    public void A_bad_command_line_is_refused_with_its_reason_and_the_usage(string[] args, string reason)
    {
        Assert.False(Diagnostics.StressOptions.TryParse(args, out _, out string usage));
        Assert.Contains(reason, usage);
        Assert.EndsWith(Diagnostics.StressOptions.Usage, usage);
    }

    // ── the load plan ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_burner_classes_are_the_four_the_plan_names_and_the_count_is_per_class()
    {
        Assert.Equal(
            new[] { ThreadPriority.BelowNormal, ThreadPriority.Normal, ThreadPriority.AboveNormal, ThreadPriority.Highest },
            Diagnostics.StressRules.BurnerClasses);
        Assert.Equal(8, Diagnostics.StressRules.BurnerThreads(2));
        Assert.Equal(0, Diagnostics.StressRules.BurnerThreads(0));
        Assert.Equal(0, Diagnostics.StressRules.BurnerThreads(-3));
    }

    [Fact]
    public void No_memory_pressure_is_no_allocator()
    {
        Assert.True(Diagnostics.AllocatorPlan.For(0).IsNone);
        Assert.True(Diagnostics.AllocatorPlan.For(-5).IsNone);
        Assert.Equal(0, Diagnostics.AllocatorPlan.For(0).ResidentBytes);
    }

    [Fact]
    public void Two_gibibytes_is_512_chunks_of_four_with_a_slice_of_them_replaced_every_50_ms()
    {
        var plan = Diagnostics.AllocatorPlan.For(2048);

        Assert.Equal(4 * 1024 * 1024, plan.ChunkBytes);
        Assert.Equal(512, plan.ChunkCount);
        Assert.Equal(6, plan.ReplacePerTick);                     // 512 / 80: about one full turnover every four seconds
        Assert.Equal(50, plan.TickMs);
        Assert.Equal(2048L * 1024 * 1024, plan.ResidentBytes);
    }

    [Theory]
    [InlineData(3, 3, 1, 1)]          // smaller than a chunk: one chunk of exactly that size
    [InlineData(4, 4, 1, 1)]
    [InlineData(10, 4, 3, 1)]         // rounds the chunk count UP: at least what was asked for
    [InlineData(16_384, 4, 4096, 51)]
    public void The_allocator_plan_never_holds_less_than_asked_and_always_replaces_something(int memoryMib, int chunkMib, int count, int replace)
    {
        var plan = Diagnostics.AllocatorPlan.For(memoryMib);

        Assert.Equal(chunkMib * 1024 * 1024, plan.ChunkBytes);
        Assert.Equal(count, plan.ChunkCount);
        Assert.Equal(replace, plan.ReplacePerTick);
        Assert.True(plan.ResidentBytes >= memoryMib * 1024L * 1024);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, false)]
    [InlineData(1, true, true)]
    [InlineData(2, true, false)]
    public void The_pass_rule_is_no_incident_or_one_under_allow_one(int incidents, bool allowOne, bool passed)
        => Assert.Equal(passed, Diagnostics.StressRules.Passed(incidents, allowOne));

    [Fact]
    public void The_source_label_names_the_file_never_its_folder()
    {
        Assert.Equal("file:a.flac", Diagnostics.StressRules.SourceLabel(Parse("--stress-audio", "--file", @"C:\private\music\a.flac")));
        Assert.Equal("track:spotify:track:abc", Diagnostics.StressRules.SourceLabel(Parse("--stress-audio", "--track", "spotify:track:abc")));
        Assert.Equal("silent-voice", Diagnostics.StressRules.SourceLabel(Parse("--stress-audio")));
    }

    // ── the printed ledger summary ───────────────────────────────────────────────────────────────────────────────────

    static Diagnostics.StressReport Device(int incidents = 0, int allowed = 0, bool endedEarly = false, int played = 60, string verdict = "clean",
                                           long stall = 120, long xruns = 0, long lost = 0)
        => new("device", "file:a.flac", 60, played, 2, 2048, true, false, incidents, stall, verdict, xruns, lost, allowed, endedEarly);

    [Fact]
    public void A_clean_device_run_prints_the_three_lines_and_passes()
    {
        var report = Device();

        Assert.Equal(string.Join("\n",
            "stress-audio label=device source=file:a.flac seconds=60/60 burners=2x4 memoryMiB=2048 minimized=yes batterySaver=no",
            "ledger incidents=0 longestStallMs=120 verdict=clean xruns=0 xrunFramesLost=0",
            "result PASS incidents=0 allowed=0"), report.ToText());
        Assert.True(report.Passed);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Incidents_fail_the_run_with_exit_code_two_and_name_the_verdict()
    {
        var report = Device(incidents: 2, verdict: "producerStarved", stall: 480, xruns: 2, lost: 46_080);

        Assert.Equal(string.Join("\n",
            "stress-audio label=device source=file:a.flac seconds=60/60 burners=2x4 memoryMiB=2048 minimized=yes batterySaver=no",
            "ledger incidents=2 longestStallMs=480 verdict=producerStarved xruns=2 xrunFramesLost=46080",
            "result FAIL incidents=2 allowed=0"), report.ToText());
        Assert.False(report.Passed);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void Allow_one_tolerates_exactly_one_incident()
    {
        Assert.True(Device(incidents: 1, allowed: 1).Passed);
        Assert.Equal(0, Device(incidents: 1, allowed: 1).ExitCode);
        Assert.False(Device(incidents: 2, allowed: 1).Passed);
        Assert.Equal(2, Device(incidents: 2, allowed: 1).ExitCode);
    }

    [Fact]
    public void A_run_that_ended_early_is_inconclusive_even_with_no_incident()
    {
        var report = Device(endedEarly: true, played: 12);

        Assert.EndsWith("result INCONCLUSIVE ended-early played=12s of 60s", report.ToText());
        Assert.False(report.Passed);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void A_smoke_run_is_labelled_smoke_on_its_first_line_and_says_it_is_not_the_gate()
    {
        // the engine-level smoke run measures no stall length: -1 prints as n/a
        var report = new Diagnostics.StressReport("smoke", "silent-voice", 5, 5, 0, 0, false, false, 0, -1, "clean", 0, 0, 0, false);

        Assert.Equal(string.Join("\n",
            "stress-audio label=smoke source=silent-voice seconds=5/5 burners=0x4 memoryMiB=0 minimized=no batterySaver=no",
            "ledger incidents=0 longestStallMs=n/a verdict=clean xruns=0 xrunFramesLost=0",
            "result PASS incidents=0 allowed=0",
            "note smoke run: the silent endpoint is paced to the wall clock, not a device; this is not the on-box gate"), report.ToText());
    }

    [Fact]
    public void The_json_line_carries_the_kind_the_label_and_the_verdict()
    {
        using JsonDocument doc = JsonDocument.Parse(Device(incidents: 2, verdict: "gcPauses", stall: 90, xruns: 2, lost: 4_320).ToJsonLine(1234));
        JsonElement root = doc.RootElement;

        Assert.Equal(1234, root.GetProperty("t").GetInt64());
        Assert.Equal("stress", root.GetProperty("kind").GetString());
        Assert.Equal("device", root.GetProperty("label").GetString());
        Assert.Equal("file:a.flac", root.GetProperty("source").GetString());
        Assert.Equal(60, root.GetProperty("seconds").GetInt32());
        Assert.Equal(2, root.GetProperty("incidents").GetInt32());
        Assert.Equal(90, root.GetProperty("longestStallMs").GetInt64());
        Assert.Equal("gcPauses", root.GetProperty("verdict").GetString());
        Assert.Equal(2, root.GetProperty("xruns").GetInt64());
        Assert.Equal(4_320, root.GetProperty("xrunFramesLost").GetInt64());
        Assert.True(root.GetProperty("minimized").GetBoolean());
        Assert.False(root.GetProperty("batterySaver").GetBoolean());
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(2, root.GetProperty("code").GetInt32());
    }
}

public class StressRunTests
{
    static Diagnostics.StressOptions Options(params string[] args)
    {
        Assert.True(Diagnostics.StressOptions.TryParse(args, out Diagnostics.StressOptions options, out string usage), usage);
        return options;
    }

    static Diagnostics.StressInput At(long now, string phase = "Playing", bool buffering = false, bool online = true, string fault = "None",
                                      long xruns = 0, long lost = 0, int ledger = 0, long stall = 0, string verdict = "clean")
        => new(now, online, phase, buffering, fault, 0, xruns, lost, ledger, stall, verdict);

    /// <summary>A file run, 60 s window, already past Play and BeginLoad: playing from t = 1000.</summary>
    static Diagnostics.StressRun RunningFile(out long windowStart)
    {
        var run = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac", "--seconds", "60"));
        Assert.Equal(Diagnostics.StressVerb.Play, run.Tick(At(0, phase: "Idle")).Verb);
        Assert.Equal(Diagnostics.StressVerb.BeginLoad, run.Tick(At(1_000)).Verb);
        windowStart = 1_000;
        return run;
    }

    [Fact]
    public void A_file_run_plays_at_once_waits_for_audio_then_opens_the_window()
    {
        var run = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac", "--seconds", "60"));

        Assert.Equal(Diagnostics.StressRun.Stage.Boot, run.Current);
        Assert.Equal(Diagnostics.StressVerb.Play, run.Tick(At(0, phase: "Idle", online: false)).Verb);   // a file needs no session
        Assert.Equal(Diagnostics.StressRun.Stage.WaitingForAudio, run.Current);
        Assert.Equal(Diagnostics.StressVerb.None, run.Tick(At(100, phase: "Loading")).Verb);
        Assert.Equal(Diagnostics.StressVerb.None, run.Tick(At(200, buffering: true)).Verb);               // Playing but still buffering
        Assert.Equal(Diagnostics.StressVerb.BeginLoad, run.Tick(At(300)).Verb);
        Assert.Equal(Diagnostics.StressRun.Stage.Running, run.Current);
    }

    [Fact]
    public void The_window_is_the_requested_seconds_from_the_moment_audio_flowed()
    {
        var run = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac", "--seconds", "60"));
        run.Tick(At(0, phase: "Idle"));
        run.Tick(At(300));                                                         // BeginLoad: the window opens at 300

        Assert.Equal(Diagnostics.StressVerb.None, run.Tick(At(60_299)).Verb);      // 59.999 s
        Diagnostics.StressStep step = run.Tick(At(60_300));                        // 60.000 s
        Assert.Equal(Diagnostics.StressVerb.Finish, step.Verb);
        Assert.Equal(0, step.Code);
        Assert.Equal(Diagnostics.StressRun.Stage.Done, run.Current);
        Diagnostics.StressReport report = run.Report!.Value;
        Assert.Equal("device", report.Label);
        Assert.Equal(60, report.PlayedSeconds);
        Assert.True(report.Passed);
    }

    [Fact]
    public void Incidents_before_the_window_are_not_the_windows_and_incidents_inside_it_are()
    {
        var run = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac", "--seconds", "10"));
        run.Tick(At(0, phase: "Idle"));
        run.Tick(At(100, ledger: 3, xruns: 5));                   // the open itself glitched three times: before the window
        run.Tick(At(200, ledger: 3, xruns: 5));
        Assert.Equal(0, run.Incidents);

        run.Tick(At(300, ledger: 4, xruns: 6, stall: 40, verdict: "deviceLate"));
        run.Tick(At(400, ledger: 4, xruns: 6, stall: 40, verdict: "deviceLate"));
        run.Tick(At(500, ledger: 5, xruns: 7, stall: 250, verdict: "producerStarved"));
        Assert.Equal(2, run.Incidents);
        Assert.Equal(250, run.LongestStallMs);

        Diagnostics.StressStep done = run.Tick(At(10_100, ledger: 5, xruns: 7, stall: 250, verdict: "producerStarved"));
        Assert.Equal(Diagnostics.StressVerb.Finish, done.Verb);
        Assert.Equal(2, done.Code);                               // Assertion: the run had incidents
        Diagnostics.StressReport report = run.Report!.Value;
        Assert.Equal(2, report.Incidents);
        Assert.Equal("producerStarved", report.Verdict);
        Assert.Equal(250, report.LongestStallMs);
        Assert.Equal(2, report.Xruns);
        Assert.False(report.Passed);
    }

    [Fact]
    public void A_new_sessions_ledger_starts_again_from_zero_and_its_incidents_still_count()
    {
        // 0 → 1 (+1); the session is replaced and its ledger Reset (1 → 0: +0); 0 → 2 (+2) — three incidents in all.
        var run = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac", "--seconds", "10"));
        run.Tick(At(0, phase: "Idle"));
        run.Tick(At(100, ledger: 0));
        run.Tick(At(200, ledger: 1));
        run.Tick(At(300, ledger: 0));
        run.Tick(At(400, ledger: 2));

        Assert.Equal(3, run.Incidents);
    }

    [Fact]
    public void An_xrun_the_ledgers_drain_missed_still_counts()
    {
        var run = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac", "--seconds", "10"));
        run.Tick(At(0, phase: "Idle"));
        run.Tick(At(100, xruns: 5, lost: 100));
        run.Tick(At(200, xruns: 7, lost: 9_700));                 // the engine counted two; the ledger stayed at zero

        Assert.Equal(2, run.Incidents);
        Diagnostics.StressStep done = run.Tick(At(10_100, xruns: 7, lost: 9_700));
        Assert.Equal(2, done.Code);
        Assert.Equal(9_600, run.Report!.Value.XrunFramesLost);
    }

    [Fact]
    public void Allow_one_lets_a_single_incident_through_and_two_fail()
    {
        var one = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac", "--seconds", "5", "--allow-one"));
        one.Tick(At(0, phase: "Idle"));
        one.Tick(At(100));
        one.Tick(At(200, ledger: 1));
        Assert.Equal(0, one.Tick(At(5_100, ledger: 1)).Code);

        var two = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac", "--seconds", "5", "--allow-one"));
        two.Tick(At(0, phase: "Idle"));
        two.Tick(At(100));
        two.Tick(At(200, ledger: 2));
        Assert.Equal(2, two.Tick(At(5_100, ledger: 2)).Code);
    }

    [Fact]
    public void A_track_that_ends_before_the_window_is_inconclusive_not_a_pass()
    {
        var run = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac", "--seconds", "60"));
        run.Tick(At(0, phase: "Idle"));
        run.Tick(At(1_000));

        Diagnostics.StressStep step = run.Tick(At(21_000, phase: "Ended"));         // 20 s into a 60 s window

        Assert.Equal(Diagnostics.StressVerb.Finish, step.Verb);
        Assert.Equal(1, step.Code);
        Diagnostics.StressReport report = run.Report!.Value;
        Assert.True(report.EndedEarly);
        Assert.Equal(20, report.PlayedSeconds);
        Assert.False(report.Passed);
    }

    [Fact]
    public void A_playback_fault_fails_the_run_while_waiting_and_while_running()
    {
        var waiting = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac"));
        waiting.Tick(At(0, phase: "Idle"));
        Diagnostics.StressStep a = waiting.Tick(At(100, phase: "Loading", fault: "Unavailable"));
        Assert.Equal(Diagnostics.StressVerb.Fail, a.Verb);
        Assert.Equal(1, a.Code);
        Assert.Contains("Unavailable", a.Reason);

        var running = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac"));
        running.Tick(At(0, phase: "Idle"));
        running.Tick(At(100));
        Diagnostics.StressStep b = running.Tick(At(200, fault: "Network"));
        Assert.Equal(Diagnostics.StressVerb.Fail, b.Verb);
        Assert.Contains("Network", b.Reason);
    }

    [Fact]
    public void No_audio_within_thirty_seconds_fails_with_no_endpoint_on_a_device_run_and_fault_on_a_smoke_run()
    {
        var device = new Diagnostics.StressRun(Options("--stress-audio", "--file", "a.flac"));
        device.Tick(At(0, phase: "Idle"));
        Assert.Equal(Diagnostics.StressVerb.None, device.Tick(At(30_000, phase: "Loading")).Verb);
        Diagnostics.StressStep d = device.Tick(At(30_001, phase: "Loading"));
        Assert.Equal(Diagnostics.StressVerb.Fail, d.Verb);
        Assert.Equal(69, d.Code);                                                  // Headless.ExitCode.NoEndpoint

        var smoke = new Diagnostics.StressRun(Options("--stress-audio", "--fake", "--file", "a.flac"));
        smoke.Tick(At(0, phase: "Idle"));
        Assert.Equal(1, smoke.Tick(At(30_001, phase: "Loading")).Code);
    }

    [Fact]
    public void A_track_run_waits_for_the_session_and_gives_up_after_sixty_seconds()
    {
        var waits = new Diagnostics.StressRun(Options("--stress-audio", "--track", "spotify:track:abc"));
        Assert.Equal(Diagnostics.StressVerb.None, waits.Tick(At(0, phase: "Idle", online: false)).Verb);
        Assert.Equal(Diagnostics.StressVerb.None, waits.Tick(At(60_000, phase: "Idle", online: false)).Verb);
        Diagnostics.StressStep timeout = waits.Tick(At(60_001, phase: "Idle", online: false));
        Assert.Equal(Diagnostics.StressVerb.Fail, timeout.Verb);
        Assert.Equal(75, timeout.Code);                                            // Headless.ExitCode.LoginTimeout

        var online = new Diagnostics.StressRun(Options("--stress-audio", "--track", "spotify:track:abc"));
        Assert.Equal(Diagnostics.StressVerb.None, online.Tick(At(0, phase: "Idle", online: false)).Verb);
        Assert.Equal(Diagnostics.StressVerb.Play, online.Tick(At(500, phase: "Idle", online: true)).Verb);
    }

    [Fact]
    public void Nothing_happens_once_the_run_is_done()
    {
        var run = RunningFile(out long start);
        Assert.Equal(Diagnostics.StressVerb.Finish, run.Tick(At(start + 60_000)).Verb);

        Assert.Equal(Diagnostics.StressVerb.None, run.Tick(At(start + 61_000, ledger: 9)).Verb);
        Assert.Equal(Diagnostics.StressVerb.None, run.Tick(At(start + 62_000, phase: "Ended")).Verb);
    }

    [Fact]
    public void What_the_load_turned_out_to_be_is_carried_into_the_report()
    {
        var run = RunningFile(out long start);
        run.NoteLoad(minimized: true);

        run.Tick(At(start + 60_000));

        Assert.True(run.Report!.Value.Minimized);
        Assert.Equal(60, run.Report!.Value.RequestedSeconds);
    }
}
