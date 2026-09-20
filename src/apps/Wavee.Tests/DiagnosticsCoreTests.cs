// ── Wavee.Tests/DiagnosticsCoreTests.cs — Screens/Diagnostics.cs (CORE) ─────────────────────────────────────────────
//
// ch 27 §8's `LogView` (16 facts in 0.2.9, ported and extended), the runtime report text (`BuildReport`, "no test today
// — add one"), the module report's two state rules, the crash-report file rule and the NativeAOT frame parse (G-094),
// the frame watch's pure half and the sidebar-pane invariant edge (G-183, G-197), and the Connect traces' store. Pure: no
// engine, no window, no file.

using Xunit;

namespace Wavee.Tests;

public class LogViewTests
{
    static WaveeLogEntry E(long seq, WaveeLogLevel level, string cat, string msg, string evt = "", string? op = null,
        WaveeLogField[]? fields = null, long unixMs = 1_700_000_000_000, long elapsed = -1, string? ex = null)
        => new(seq, unixMs, level, cat, evt, msg, op, 4, elapsed, fields, ex);

    static readonly WaveeLogEntry[] Session =
    [
        E(1, WaveeLogLevel.Info, "app", "boot"),
        E(2, WaveeLogLevel.Debug, "audio", "tick"),
        E(3, WaveeLogLevel.Debug, "audio", "tick"),
        E(4, WaveeLogLevel.Warning, "Lyrics", "slow", fields: [WaveeLogField.Of("provider", "amll")]),
        E(5, WaveeLogLevel.Error, "audio", "boom", op: "7f2a"),
        E(6, WaveeLogLevel.Critical, "crash", "dead"),
    ];

    static Diagnostics.LogViewResult Build(Diagnostics.LogViewQuery q) => Diagnostics.LogView.Build(Session, q);

    [Fact]
    public void Newest_first_is_the_default_and_the_counts_describe_the_whole_session()
    {
        var r = Build(new());
        Assert.Equal(6, r.Total);
        Assert.Equal(1, r.WarningCount);
        Assert.Equal(2, r.ErrorCount);     // Error AND Critical
        Assert.Equal(6, r.Rows[0].Entry.Sequence);
        Assert.Equal(5, r.Shown);          // the two ticks grouped
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Adjacent_repeats_group_and_oldest_first_keeps_the_first_of_the_run()
    {
        var r = Build(new(NewestFirst: false));
        Assert.Equal(1, r.Rows[0].Entry.Sequence);
        Assert.Equal(2, r.Rows[1].Entry.Sequence);
        Assert.Equal(2, r.Rows[1].Repeat);
        Assert.Equal(6, Build(new(NewestFirst: false, GroupRepeats: false)).Shown);
    }

    [Fact]
    public void Filtering_runs_before_grouping_so_a_hidden_entry_does_not_split_a_run()
    {
        WaveeLogEntry[] entries = [E(1, WaveeLogLevel.Info, "a", "x"), E(2, WaveeLogLevel.Debug, "a", "y"), E(3, WaveeLogLevel.Info, "a", "x")];
        var grouped = Diagnostics.LogView.Build(entries, new(Level: Diagnostics.LogLevelBucket.InfoPlus, NewestFirst: false));
        Assert.Single(grouped.Rows);   // the filtered Debug line vanished, so the two Infos are now adjacent
        Assert.Equal(2, grouped.Rows[0].Repeat);
    }

    [Fact]
    public void Level_category_and_search_filter_per_entry()
    {
        Assert.Equal(3, Build(new(Level: Diagnostics.LogLevelBucket.Warnings)).Shown);
        Assert.Equal(2, Build(new(Level: Diagnostics.LogLevelBucket.Errors)).Shown);
        Assert.Single(Build(new(Category: "lyrics")).Rows);                       // case-insensitive
        Assert.Single(Build(new(Search: "AMLL")).Rows);                           // a field value
        Assert.Single(Build(new(Search: "7F2A")).Rows);                           // the operation id
        Assert.Empty(Build(new(Search: "nothing-matches")).Rows);
    }

    [Fact]
    public void The_cap_stops_the_walk_and_marks_truncation()
    {
        var r = Build(new(GroupRepeats: false, Cap: 2));
        Assert.Equal(2, r.Shown);
        Assert.True(r.Truncated);
        Assert.Equal(Diagnostics.LogViewResult.Empty, Diagnostics.LogView.Build([], new()));
    }

    [Theory]
    [InlineData(WaveeLogLevel.Trace, Diagnostics.LogLevelBucket.All, true)]
    [InlineData(WaveeLogLevel.Debug, Diagnostics.LogLevelBucket.InfoPlus, false)]
    [InlineData(WaveeLogLevel.Info, Diagnostics.LogLevelBucket.InfoPlus, true)]
    [InlineData(WaveeLogLevel.Info, Diagnostics.LogLevelBucket.Warnings, false)]
    [InlineData(WaveeLogLevel.Critical, Diagnostics.LogLevelBucket.Errors, true)]
    public void The_level_buckets(WaveeLogLevel level, Diagnostics.LogLevelBucket bucket, bool passes)
        => Assert.Equal(passes, Diagnostics.LogView.PassesLevel(level, bucket));

    [Fact]
    public void Two_entries_repeat_only_when_level_category_event_and_message_all_match()
    {
        var a = E(1, WaveeLogLevel.Info, "a", "x", "e");
        Assert.True(Diagnostics.LogView.IsRepeatOf(a, E(2, WaveeLogLevel.Info, "a", "x", "e")));
        Assert.False(Diagnostics.LogView.IsRepeatOf(a, E(2, WaveeLogLevel.Warning, "a", "x", "e")));
        Assert.False(Diagnostics.LogView.IsRepeatOf(a, E(2, WaveeLogLevel.Info, "a", "y", "e")));
    }

    [Fact]
    public void Categories_are_distinct_sorted_case_insensitively_and_the_index_clamps()
    {
        string[] cats = Diagnostics.LogView.Categories(Session);
        Assert.Equal(new[] { "app", "audio", "crash", "Lyrics" }, cats);
        Assert.Equal(0, Diagnostics.LogView.CategoryIndex(cats, null));
        Assert.Equal(2, Diagnostics.LogView.CategoryIndex(cats, "AUDIO"));
        Assert.Equal(0, Diagnostics.LogView.CategoryIndex(cats, "gone"));
    }

    [Fact]
    public void Field_text_copy_text_and_the_meta_line()
    {
        Assert.Equal("", Diagnostics.LogView.FieldText(null));
        Assert.Equal("provider=amll\nms=41", Diagnostics.LogView.FieldText([WaveeLogField.Of("provider", "amll"), WaveeLogField.Of("ms", 41)]));

        var rows = new[] { new Diagnostics.LogViewRow(E(3, WaveeLogLevel.Info, "app", "hi"), 3) };
        Assert.Equal("seq=3 I [app] hi (repeated 3×)\n", Diagnostics.LogView.CopyText(rows));

        var full = E(812, WaveeLogLevel.Info, "lyrics", "m", evt: "fetch", op: "7f2a", elapsed: 41);
        Assert.Equal("#812 · lyrics.fetch · tid 4 · op 7f2a · 41 ms", Diagnostics.LogView.MetaLine(in full));
        var bare = E(5, WaveeLogLevel.Info, "app", "m");
        Assert.Equal("#5 · app · tid 4", Diagnostics.LogView.MetaLine(in bare));
    }

    [Fact]
    public void Times_labels_and_uptime_are_invariant_and_in_the_callers_offset()
    {
        long t = new DateTimeOffset(2026, 9, 13, 18, 24, 27, 445, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("20:24:27.445", Diagnostics.LogView.FormatTime(t, TimeSpan.FromHours(2)));
        Assert.Equal("—", Diagnostics.LogView.FormatTime(0, TimeSpan.Zero));
        Assert.Equal("Sep 13 · 18:24 · 7 events", Diagnostics.LogView.SessionLabel(t, 99, 7, TimeSpan.Zero));
        Assert.Equal("pid 99 · 7 events", Diagnostics.LogView.SessionLabel(0, 99, 7, TimeSpan.Zero));
        Assert.Equal("2 h 5 m", Diagnostics.LogView.Uptime(TimeSpan.FromMinutes(125)));
        Assert.Equal("42 min", Diagnostics.LogView.Uptime(TimeSpan.FromMinutes(42)));
        Assert.Equal("just now", Diagnostics.LogView.Uptime(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void The_remount_key_moves_with_every_set_input_and_never_collides_on_equal_length_searches()
    {
        var q = new Diagnostics.LogViewQuery(Search: "abc");
        string k = Diagnostics.LogView.RemountKey(0, q, 10);
        Assert.NotEqual(k, Diagnostics.LogView.RemountKey(0, q with { Search = "xyz" }, 10));
        Assert.NotEqual(k, Diagnostics.LogView.RemountKey(1, q, 10));
        Assert.NotEqual(k, Diagnostics.LogView.RemountKey(0, q, 11));
        Assert.Equal(k, Diagnostics.LogView.RemountKey(0, q with { Cap = 999 }, 10));   // the cap shows in the count, not the key
    }

    [Fact]
    public void Load_more_the_clear_gate_and_the_sequence_lookup()
    {
        Assert.Equal(1000, Diagnostics.LogView.NextCap(500));
        Assert.Equal(Diagnostics.LogView.MaxRows, Diagnostics.LogView.NextCap(1800));
        Assert.True(Diagnostics.LogView.CanClear(0));
        Assert.False(Diagnostics.LogView.CanClear(2));
        var rows = Build(new()).Rows;
        Assert.Equal(1, Diagnostics.LogView.IndexOfSequence(rows, 5));
        Assert.Equal(-1, Diagnostics.LogView.IndexOfSequence(rows, 42));
    }
}

public class RuntimeReportTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_session_says_so_after_the_status_block()
    {
        var status = new Setup.RuntimeFacts(false, Setup.RuntimeIssue.Missing, Version: "1.2.63");
        string text = Diagnostics.BuildRuntimeReport(null, status, At, null);
        Assert.Contains("captured : 2026-09-13 10:00:00 +00:00", text, StringComparison.Ordinal);
        Assert.Contains("log file : (none)", text, StringComparison.Ordinal);
        Assert.Contains("  issue : Missing", text, StringComparison.Ordinal);
        Assert.Contains("  packId : (none)", text, StringComparison.Ordinal);
        Assert.Contains("[diagnostics]\n  unavailable — no playback session is running.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_report_carries_every_section()
    {
        var sig = new Setup.RuntimeSignature("CN=Spotify", "CN=CA", Setup.RuntimeTrust.Trusted, "ok", At, At.AddYears(1), "AB12", @"C:\r\Spotify.dll");
        var diag = new Diagnostics.RuntimeDiagnostics(true,
            [new("Settings", @"C:\r", true, false), new("Bundled", "", false, false)],
            ProvisioningOutcome.Ready, null, ProvisioningOutcome.Ready, "verified", Setup.RuntimeTrust.Trusted, sig);
        string text = Diagnostics.BuildRuntimeReport(diag, null, At, @"C:\logs\wavee-20260913.log");
        Assert.DoesNotContain("[status]", text, StringComparison.Ordinal);
        Assert.Contains("localPlaybackCompiledIn : yes", text, StringComparison.Ordinal);
        Assert.Contains("[candidates] (2)", text, StringComparison.Ordinal);
        Assert.Contains("dll      : present", text, StringComparison.Ordinal);
        Assert.Contains("dir      : (none)", text, StringComparison.Ordinal);
        Assert.Contains("publisher : CN=Spotify", text, StringComparison.Ordinal);
        Assert.Contains("thumbprint : AB12", text, StringComparison.Ordinal);
        Assert.False(diag.VerifyNeverReached);
    }

    [Fact]
    public void A_build_without_local_playback_reports_that_and_never_reached_verify()
    {
        var diag = Diagnostics.RuntimeDiagnostics.NotCompiledIn("no client");
        Assert.False(diag.CompiledIn);
        Assert.Equal(ProvisioningOutcome.RuntimeUnavailable, diag.LocateOutcome);
        Assert.True(diag.VerifyNeverReached);
        Assert.Contains("localPlaybackCompiledIn : no", Diagnostics.BuildRuntimeReport(diag, null, At, null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData("Ready", true, false)]
    [InlineData("Starting", true, false)]
    [InlineData("Stopped", true, false)]
    [InlineData("Faulted", false, true)]
    [InlineData("Crashed", false, true)]
    public void A_module_is_healthy_or_retryable(string? state, bool healthy, bool retry)
    {
        var m = new Diagnostics.ModuleReport("id", "Name", true, "1.0", 1, "pub", "dir", state, null, "", false, 0, 0, 0, 0, 0, null, null);
        Assert.Equal(healthy, m.ProcessHealthy);
        Assert.Equal(retry, m.CanRetry);
    }

    [Fact]
    public void Blank_values_print_a_dash_and_the_connect_stamps_are_millisecond_invariant()
    {
        Assert.Equal("—", Diagnostics.OrDash("  "));
        Assert.Equal("x", Diagnostics.OrDash("x"));
        Assert.Equal("—", Diagnostics.StampMs(0, TimeSpan.Zero));
        long t = new DateTimeOffset(2026, 9, 13, 10, 0, 0, 5, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("2026-09-13 11:00:00.005", Diagnostics.StampMs(t, TimeSpan.FromHours(1)));
    }
}

public class CrashFilesTests
{
    [Fact]
    public void A_report_name_round_trips_its_stamp_and_a_stray_file_does_not_parse()
    {
        var at = new DateTimeOffset(2026, 9, 13, 18, 5, 9, 123, TimeSpan.Zero);
        string name = Diagnostics.CrashFiles.NameFor(at);
        Assert.Equal("crash-report-20260913-180509-123.txt", name);
        Assert.True(Diagnostics.CrashFiles.TryStamp(name, out var stamp));
        Assert.Equal(new DateTime(2026, 9, 13, 18, 5, 9), stamp);
        Assert.True(Diagnostics.CrashFiles.TryStamp("crash-report-20260913-180509.txt", out _));   // 0.2.x names
        Assert.False(Diagnostics.CrashFiles.TryStamp("crash-report-yesterday.txt", out _));
        Assert.False(Diagnostics.CrashFiles.TryStamp("notes.txt", out _));
        Assert.False(Diagnostics.CrashFiles.TryStamp("crash-report-20260913-180509-12345.txt", out _));
    }

    [Fact]
    public void Newest_name_sorts_first()
    {
        var files = new[] { @"C:\l\crash-report-20260101-000000.txt", @"C:\l\crash-report-20260913-000000.txt", @"C:\l\crash-report-20260501-000000.txt" };
        Array.Sort(files, Diagnostics.CrashFiles.NewestFirst);
        Assert.EndsWith("20260913-000000.txt", files[0], StringComparison.Ordinal);
        Assert.EndsWith("20260101-000000.txt", files[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_nativeaot_frames_parse_in_order_with_duplicates_and_nothing_else_does()
    {
        const string trace = "System.InvalidOperationException: x\n   at Wavee!<BaseAddress>+0x7b1fc6\n   at Wavee!<BaseAddress>+0x10\n"
                           + "--- End of stack trace from previous location ---\n   at Wavee!<BaseAddress>+0x7b1fc6\n   at Foo.Bar() in x.cs:line 3\n"
                           + "   at Wavee!<BaseAddress>+0x\n   at Wavee!<BaseAddress>+0x1234567890abcdef0";
        Assert.Equal(new long[] { 0x7b1fc6, 0x10, 0x7b1fc6 }, Diagnostics.CrashFiles.ParseRvas(trace));
        Assert.Empty(Diagnostics.CrashFiles.ParseRvas(null));
        Assert.Empty(Diagnostics.CrashFiles.ParseRvas("at Foo.Bar() in x.cs:line 3"));
    }
}

public class FrameWatchRulesTests
{
    [Fact]
    public void The_session_totals_count_invalid_and_over_budget_frames()
    {
        var t = new Diagnostics.FrameSessionTotals();
        t.Add(5, 8.33);
        t.Add(12, 8.33);
        t.Add(double.NaN, 8.33);
        t.Add(4, 0);          // an unknown refresh interval counts as invalid, still a frame
        Assert.Equal(4, t.Frames);
        Assert.Equal(1, t.Over83);
        Assert.Equal(1, t.OverRefresh);
        Assert.Equal(2, t.Invalid);
        Assert.Equal(12, t.WorstMs);
    }

    [Fact]
    public void The_budget_falls_back_to_sixty_hertz_and_the_route_key_includes_the_argument()
    {
        Assert.Equal(8.33, Diagnostics.FrameBudgetMs(8.33));
        Assert.Equal(16.67, Diagnostics.FrameBudgetMs(0));
        Assert.Equal("album", Diagnostics.RouteWatchKey("album", null));
        Assert.NotEqual(Diagnostics.RouteWatchKey("album", "a"), Diagnostics.RouteWatchKey("album", "b"));
    }

    [Fact]
    public void The_sidebar_pane_fault_logs_on_its_edge_only()
    {
        var bad = SidebarPaneInvariantFault.LayerOpacityMismatch;
        Assert.True(Diagnostics.PaneFaultEdge(SidebarPaneInvariantFault.None, bad));
        Assert.False(Diagnostics.PaneFaultEdge(bad, bad));                                            // persistent: once
        Assert.True(Diagnostics.PaneFaultEdge(bad, SidebarPaneInvariantFault.HitTestOwnerMismatch));  // a different fault
        Assert.False(Diagnostics.PaneFaultEdge(bad, SidebarPaneInvariantFault.None));                 // recovery is silent
    }

    [Fact]
    public void A_connect_note_updates_the_trace_and_bumps_the_version()
    {
        int before = Diagnostics.Connect.Version.Peek();
        Diagnostics.Connect.NotePut(new Diagnostics.PutTrace(7, "PLAYER_STATE_CHANGED", true, 1, 2, 3));
        Assert.Equal(7u, Diagnostics.Connect.LastPut.MsgId);
        Assert.Equal(before + 1, Diagnostics.Connect.Version.Peek());
    }
}
