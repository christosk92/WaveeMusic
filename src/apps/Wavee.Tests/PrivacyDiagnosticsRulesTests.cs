// ── Wavee.Tests/PrivacyDiagnosticsRulesTests.cs — Settings › Privacy & diagnostics' pure decisions ──────────────────────
//
// `Settings.PrivacyRules` (Screens/Settings.Privacy.cs): the saved-reports tally, which reports offer "Send…", the hang
// wording's seconds, the sent-time stamp, the shortened install id, the size formats and the loc-key choices for a
// report's kind, send state and the reporting mode. Plain bundles in memory — no folder, no window, no network.
// (docs/plans/wavee/privacy-diagnostics-tab-implementation.md §5.8, §7.)

using System.Globalization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class PrivacyDiagnosticsRulesTests
{
    static Crash.BundleInfo Bundle(Crash.Kind kind, Crash.SendState state, long dumpBytes = 0, string? sentAtUtc = null,
        string reportId = "3f9ca1e2")
        => new(
            Dir: @"C:\fake\logs\crash\" + reportId,
            Summary: new Crash.Summary(
                ReportId: reportId, InstallId: "8a1d9e0f", Kind: kind, StampUtc: "2026-10-02T13:05:00.000Z",
                Version: "0.3.0", Quad: "0.3.0.41", Commit: "7e209e37", Channel: "stable", Arch: "arm64", OsBuild: "26100",
                Gpu: "NVIDIA GeForce RTX", GpuTier: "Strong", SoftwareAdapter: false, Packaged: true, Locale: "en-US",
                SessionId: "sess-1", UptimeMs: 12_345, BeforeFirstFrame: false, LastRoute: "artist:abc",
                ExceptionType: "System.InvalidOperationException", ExceptionMessage: "boom",
                Rvas: [0x7b1fc6], ModuleBase: 0x140000000, ModuleSize: 0x2000000,
                DebugId: "7E2C1B4A-0000-0000-0000-0000000000AA-1", ExitCode: 0, HasDump: dumpBytes > 0, DumpBytes: dumpBytes),
            StampLocal: new DateTime(2026, 10, 2, 15, 5, 0, DateTimeKind.Unspecified),
            Send: new Crash.SendRecord(state, sentAtUtc, null, 0, false),
            HasDump: dumpBytes > 0,
            DumpBytes: dumpBytes);

    // ── 1. the tally ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tally_counts_sent_unsent_and_closed_and_sums_bytes()
    {
        var bundles = new[]
        {
            Bundle(Crash.Kind.Managed, Crash.SendState.Sent, dumpBytes: 100),
            Bundle(Crash.Kind.Hang, Crash.SendState.NotSent, dumpBytes: 200),
            Bundle(Crash.Kind.Native, Crash.SendState.Failed, dumpBytes: 300),
            Bundle(Crash.Kind.ExitCode, Crash.SendState.Queued, dumpBytes: 400),
            Bundle(Crash.Kind.UncleanExit, Crash.SendState.NotSent, dumpBytes: 500),
            Bundle(Crash.Kind.Managed, Crash.SendState.Sent, dumpBytes: 600),
        };

        var tally = Settings.PrivacyRules.Tally(bundles, static b => b.DumpBytes);

        Assert.Equal(6, tally.Total);
        Assert.Equal(2, tally.Sent);          // the two Sent crashes
        Assert.Equal(3, tally.NotSent);       // not sent + failed + waiting in the outbox
        Assert.Equal(1, tally.Closed);        // the unclean exit
        Assert.Equal(2_100, tally.Bytes);
        Assert.Equal(tally.Total, tally.Sent + tally.NotSent + tally.Closed);
    }

    [Fact]
    public void A_closed_unexpectedly_report_is_only_ever_counted_as_closed()
    {
        // send.json may carry any state for an UncleanExit bundle; the row shows a dash, the tally must not claim "sent".
        var tally = Settings.PrivacyRules.Tally(
            [Bundle(Crash.Kind.UncleanExit, Crash.SendState.Sent), Bundle(Crash.Kind.UncleanExit, Crash.SendState.Failed)],
            static _ => 0L);

        Assert.Equal(new Settings.PrivacyRules.ReportTally(Total: 2, Sent: 0, NotSent: 0, Closed: 2, Bytes: 0), tally);
    }

    [Fact]
    public void An_empty_folder_tallies_to_zero_and_never_asks_for_a_size()
    {
        int sizeCalls = 0;
        var tally = Settings.PrivacyRules.Tally([], _ => { sizeCalls++; return 1L; });

        Assert.Equal(new Settings.PrivacyRules.ReportTally(0, 0, 0, 0, 0), tally);
        Assert.Equal(0, sizeCalls);
    }

    // ── 2. who may send ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Crash.Kind.Managed, Crash.SendState.NotSent, true, true)]
    [InlineData(Crash.Kind.Managed, Crash.SendState.Failed, true, true)]
    [InlineData(Crash.Kind.Native, Crash.SendState.NotSent, true, true)]
    [InlineData(Crash.Kind.Hang, Crash.SendState.Failed, true, true)]
    [InlineData(Crash.Kind.ExitCode, Crash.SendState.NotSent, true, true)]
    [InlineData(Crash.Kind.Managed, Crash.SendState.Sent, true, false)]       // already gone
    [InlineData(Crash.Kind.Managed, Crash.SendState.Queued, true, false)]     // already waiting in the outbox
    [InlineData(Crash.Kind.UncleanExit, Crash.SendState.NotSent, true, false)]   // nothing to send: no exception, no dump
    [InlineData(Crash.Kind.UncleanExit, Crash.SendState.Failed, true, false)]
    [InlineData(Crash.Kind.Managed, Crash.SendState.NotSent, false, false)]   // a build with no crash service
    [InlineData(Crash.Kind.Hang, Crash.SendState.Failed, false, false)]
    public void CanSend_needs_a_configured_build_evidence_and_an_unsent_or_failed_state(
        Crash.Kind kind, Crash.SendState state, bool configured, bool expected)
        => Assert.Equal(expected, Settings.PrivacyRules.CanSend(kind, state, configured));

    // ── 3. the hang wording ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("no UI heartbeat for 23 s", 23)]
    [InlineData("no UI heartbeat for 0 s", 0)]
    [InlineData("no UI heartbeat for 120 s", 120)]
    [InlineData("waited for 3 s, then no UI heartbeat for 45 s", 45)]   // the LAST "for " is the handler's
    [InlineData("", null)]
    [InlineData("boom", null)]
    [InlineData("no UI heartbeat for s", null)]
    [InlineData("no UI heartbeat for 23", null)]            // no unit after the number
    [InlineData("no UI heartbeat for -5 s", null)]
    [InlineData("no UI heartbeat for 2.5 s", null)]
    [InlineData("no UI heartbeat for 99999999999 s", null)]    // overflows an int
    public void HangSeconds_reads_the_handlers_wording_and_rejects_anything_else(string message, int? expected)
        => Assert.Equal(expected, Settings.PrivacyRules.HangSeconds(message));

    // ── 4. the sent time ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-10-02T13:05:00Z", 2, "15:05")]
    [InlineData("2026-10-02T13:05:00.123Z", -5, "08:05")]
    [InlineData("2026-10-02T23:30:00Z", 2, "01:30")]       // crosses midnight
    [InlineData("2026-10-02T13:05:00", 0, "13:05")]        // no zone: the stamp is UTC by contract
    [InlineData("2026-10-02T13:05:00+02:00", 0, "11:05")]  // an explicit offset is honoured, then re-expressed in the caller's
    public void SentTimeLocal_renders_in_the_callers_offset(string sentAtUtc, int offsetHours, string expected)
        => Assert.Equal(expected, Settings.PrivacyRules.SentTimeLocal(sentAtUtc, TimeSpan.FromHours(offsetHours)));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("not a time", "not a time")]
    public void SentTimeLocal_degrades_to_raw_text_and_never_throws(string? sentAtUtc, string expected)
        => Assert.Equal(expected, Settings.PrivacyRules.SentTimeLocal(sentAtUtc, TimeSpan.FromHours(2)));

    // ── 5. the install id and sizes ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("8a1d9e0f3b7c4d2e9f0a1b2c3d4e5f60", "8a1d…5f60")]
    [InlineData("abcdefghijk", "abcd…hijk")]     // 11 characters: the shortest id that is shortened
    [InlineData("abcdefghij", "abcdefghij")]     // 10: shown whole
    [InlineData("abc", "abc")]
    [InlineData("", "")]
    public void ShortInstallId_keeps_four_and_four(string id, string expected)
        => Assert.Equal(expected, Settings.PrivacyRules.ShortInstallId(id));

    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(1_048_576L, "1 MB")]
    [InlineData(1_572_864L, "1.5 MB")]
    [InlineData(2_097_152L, "2 MB")]
    [InlineData(419_430L, "0.4 MB")]
    [InlineData(1_150_000L, "1.1 MB")]
    [InlineData(104_857_600L, "100 MB")]
    public void Mb_formats_binary_megabytes_invariant(long bytes, string expected)
        => Assert.Equal(expected, Settings.PrivacyRules.Mb(bytes));

    [Fact]
    public void Mb_and_WholeMb_ignore_the_current_culture()
    {
        var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaCulture.NumberFormat.NumberDecimalSeparator = ",";
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = commaCulture;
            Assert.Equal("1.5 MB", Settings.PrivacyRules.Mb(1_572_864L));
            Assert.Equal("250", Settings.PrivacyRules.WholeMb(262_144_000L));
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(10_485_760L, "10")]
    [InlineData(262_144_000L, "250")]
    [InlineData(0L, "0")]
    [InlineData(1_048_575L, "0")]     // floors: a number in a sentence that supplies the unit
    public void WholeMb_is_a_bare_floored_number(long bytes, string expected)
        => Assert.Equal(expected, Settings.PrivacyRules.WholeMb(bytes));

    // ── 6. the loc keys ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Kind_state_and_mode_keys_cover_the_matrix()
    {
        // Kind: a managed and a native crash share the Crash label; the other three each have their own.
        Assert.Equal(Strings.Settings.Privacy.Reports.KindCrash, Settings.PrivacyRules.KindKey(Crash.Kind.Managed));
        Assert.Equal(Strings.Settings.Privacy.Reports.KindCrash, Settings.PrivacyRules.KindKey(Crash.Kind.Native));
        Assert.Equal(Strings.Settings.Privacy.Reports.KindHang, Settings.PrivacyRules.KindKey(Crash.Kind.Hang));
        Assert.Equal(Strings.Settings.Privacy.Reports.KindExit, Settings.PrivacyRules.KindKey(Crash.Kind.ExitCode));
        Assert.Equal(Strings.Settings.Privacy.Reports.KindClosed, Settings.PrivacyRules.KindKey(Crash.Kind.UncleanExit));
        var kindKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            Settings.PrivacyRules.KindKey(Crash.Kind.Managed), Settings.PrivacyRules.KindKey(Crash.Kind.Hang),
            Settings.PrivacyRules.KindKey(Crash.Kind.ExitCode), Settings.PrivacyRules.KindKey(Crash.Kind.UncleanExit),
        };
        Assert.Equal(4, kindKeys.Count);

        // State: every real kind answers each send state; Sent answers the PARAMETERISED key (its text carries the time).
        foreach (var kind in new[] { Crash.Kind.Managed, Crash.Kind.Native, Crash.Kind.Hang, Crash.Kind.ExitCode })
        {
            Assert.Equal(Strings.Settings.Privacy.Reports.StateNotSent, Settings.PrivacyRules.StateKey(kind, Crash.SendState.NotSent));
            Assert.Equal(Strings.Settings.Privacy.Reports.StateQueued, Settings.PrivacyRules.StateKey(kind, Crash.SendState.Queued));
            Assert.Equal(Strings.Settings.Privacy.Reports.StateFailed, Settings.PrivacyRules.StateKey(kind, Crash.SendState.Failed));
            Assert.Equal(Strings.Settings.Privacy.Reports.StateSentKey, Settings.PrivacyRules.StateKey(kind, Crash.SendState.Sent));
        }
        // A closed-unexpectedly report has no send state at all (the row shows a dash), whatever send.json says.
        foreach (var state in Enum.GetValues<Crash.SendState>())
            Assert.Null(Settings.PrivacyRules.StateKey(Crash.Kind.UncleanExit, state));

        // Mode: the three modes, and an out-of-range stored value reads as Off.
        Assert.Equal(Strings.Settings.Privacy.Facts.ModeOff, Settings.PrivacyRules.ModeKey(Crash.Reporting.Off));
        Assert.Equal(Strings.Settings.Privacy.Facts.ModeAsk, Settings.PrivacyRules.ModeKey(Crash.Reporting.Ask));
        Assert.Equal(Strings.Settings.Privacy.Facts.ModeAuto, Settings.PrivacyRules.ModeKey(Crash.Reporting.Auto));
        Assert.Equal(Strings.Settings.Privacy.Facts.ModeOff, Settings.PrivacyRules.ModeKey((Crash.Reporting)9));
    }
}
