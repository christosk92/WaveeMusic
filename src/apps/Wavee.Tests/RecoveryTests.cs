// ── Wavee.Tests/RecoveryTests.cs — Screens/Recovery.cs (WP-D, engine-free recovery mode) ────────────────────────────
//
// Actions.For's per-launch button matrix (docs/plans/wavee/crash-diagnostics-implementation.md §I "WP-D": "Send only
// when hasBundle && hasIngest; View/Copy only when hasBundle"), the button-id <-> ActionKind round trip, the
// boot-loop body wording (the approved prototype's exact "twice in a row" phrasing at the default threshold, count
// 2), and LastReport's invariant-culture formatting. Pure: no window, no Win32 call — the one Loc lookup Recovery.Text
// performs is a harmless best-effort no-op here (no `crash.recovery*` key exists yet; every assertion below is
// against the literal English fallback, which is exactly what a machine with no loc folder at all would show).

using System;
using Xunit;

namespace Wavee.Tests;

public class RecoveryActionsTests
{
    [Fact]
    public void No_bundle_no_ingest_offers_start_reset_open_folder_quit_only()
    {
        var a = Recovery.Actions.For(Recovery.Reason.BootLoop, hasBundle: false, hasIngest: false);
        Assert.Equal(new[] { Recovery.ActionKind.Start, Recovery.ActionKind.Reset, Recovery.ActionKind.OpenFolder, Recovery.ActionKind.Quit }, a);
    }

    [Fact]
    public void Bundle_without_ingest_adds_view_and_copy_but_never_send()
    {
        var a = Recovery.Actions.For(Recovery.Reason.BootLoop, hasBundle: true, hasIngest: false);
        Assert.Equal(new[]
        {
            Recovery.ActionKind.Start, Recovery.ActionKind.Reset, Recovery.ActionKind.View,
            Recovery.ActionKind.OpenFolder, Recovery.ActionKind.Copy, Recovery.ActionKind.Quit,
        }, a);
        Assert.DoesNotContain(Recovery.ActionKind.Send, a);
    }

    [Fact]
    public void Ingest_alone_never_offers_send_without_a_bundle()
    {
        var a = Recovery.Actions.For(Recovery.Reason.BootFailed, hasBundle: false, hasIngest: true);
        Assert.DoesNotContain(Recovery.ActionKind.Send, a);
    }

    [Fact]
    public void Bundle_and_ingest_together_add_send_right_after_start()
    {
        var a = Recovery.Actions.For(Recovery.Reason.Switch, hasBundle: true, hasIngest: true);
        Assert.Equal(new[]
        {
            Recovery.ActionKind.Start, Recovery.ActionKind.Send, Recovery.ActionKind.Reset, Recovery.ActionKind.View,
            Recovery.ActionKind.OpenFolder, Recovery.ActionKind.Copy, Recovery.ActionKind.Quit,
        }, a);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Start_is_always_first_reset_is_always_offered_and_quit_is_always_last(bool hasBundle, bool hasIngest)
    {
        foreach (var reason in Enum.GetValues<Recovery.Reason>())
        {
            var a = Recovery.Actions.For(reason, hasBundle, hasIngest);
            Assert.Equal(Recovery.ActionKind.Start, a[0]);
            Assert.Contains(Recovery.ActionKind.Reset, a);
            Assert.Equal(Recovery.ActionKind.Quit, a[^1]);
            // Open folder never needs a bundle (it opens the crash root as a fallback).
            Assert.Contains(Recovery.ActionKind.OpenFolder, a);
        }
    }
}

public class RecoveryActionForTests
{
    [Theory]
    [InlineData(100, Recovery.ActionKind.Start)]
    [InlineData(101, Recovery.ActionKind.Send)]
    [InlineData(102, Recovery.ActionKind.Reset)]
    [InlineData(200, Recovery.ActionKind.View)]
    [InlineData(201, Recovery.ActionKind.OpenFolder)]
    [InlineData(202, Recovery.ActionKind.Copy)]
    [InlineData(2, Recovery.ActionKind.Quit)]
    public void Known_button_ids_round_trip(int buttonId, Recovery.ActionKind expected)
        => Assert.Equal(expected, Recovery.ActionFor(buttonId));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    public void Unknown_ids_are_null_not_a_throw(int buttonId)
        => Assert.Null(Recovery.ActionFor(buttonId));

    [Fact]
    public void Button_ids_match_the_ids_the_dialog_actually_assigns()
    {
        Assert.Equal(100, Recovery.ButtonIds.Start);
        Assert.Equal(101, Recovery.ButtonIds.Send);
        Assert.Equal(102, Recovery.ButtonIds.Reset);
        Assert.Equal(200, Recovery.ButtonIds.View);
        Assert.Equal(201, Recovery.ButtonIds.OpenFolder);
        Assert.Equal(202, Recovery.ButtonIds.Copy);
        Assert.Equal(2, Recovery.ButtonIds.Quit);   // IDCANCEL: Esc / Alt+F4 / the caption X all resolve here too
    }
}

public class RecoveryTextTests
{
    [Fact]
    public void Body_loop_at_the_default_threshold_is_the_prototypes_exact_wording()
    {
        Assert.Equal(
            "It closed unexpectedly twice in a row, before showing its window. Your music and settings are still here.",
            Recovery.Text.BodyLoop(2));
    }

    [Fact]
    public void Body_loop_at_one_reads_singular_not_zero_times()
    {
        Assert.Equal(
            "It closed unexpectedly before showing its window. Your music and settings are still here.",
            Recovery.Text.BodyLoop(1));
        // Zero/negative counts (defensive) degrade to the same singular wording, never a throw.
        Assert.Equal(Recovery.Text.BodyLoop(1), Recovery.Text.BodyLoop(0));
    }

    [Fact]
    public void Body_loop_beyond_two_names_the_count()
    {
        string s = Recovery.Text.BodyLoop(5);
        Assert.Contains("5 times in a row", s);
        Assert.Contains("Your music and settings are still here.", s);
    }

    [Fact]
    public void Body_for_dispatches_by_reason()
    {
        Assert.Equal(Recovery.Text.BodySwitch, Recovery.Text.BodyFor(Recovery.Reason.Switch, 0));
        Assert.Equal(Recovery.Text.BodyBootFailed, Recovery.Text.BodyFor(Recovery.Reason.BootFailed, 0));
        Assert.Equal(Recovery.Text.BodyLoop(2), Recovery.Text.BodyFor(Recovery.Reason.BootLoop, 2));
        Assert.Equal(Recovery.Text.BodyLoop(3), Recovery.Text.BodyFor(Recovery.Reason.BootLoop, 3));
    }

    [Fact]
    public void Last_report_formats_under_invariant_culture()
    {
        var bundle = Bundle(reportId: "3f9ca1e2b3c4d5e6", stamp: new DateTime(2026, 9, 24, 14, 30, 0, DateTimeKind.Unspecified), version: "0.3.0");

        string text = Recovery.Text.LastReport(bundle);

        Assert.Equal("Report saved 24 Sep 2026, 14:30 \u00b7 Wavee 0.3.0 \u00b7 id 3f9c-a1e2", text);
    }

    [Fact]
    public void Last_report_shows_a_short_id_as_is_rather_than_indexing_out_of_range()
    {
        var bundle = Bundle(reportId: "ab12", stamp: new DateTime(2026, 1, 1, 9, 5, 0, DateTimeKind.Unspecified), version: "0.3.0");

        string text = Recovery.Text.LastReport(bundle);

        Assert.Contains("id ab12", text);
        Assert.DoesNotContain("ab12-", text);
    }

    [Fact]
    public void Last_report_never_reads_a_different_month_name_under_another_culture()
    {
        // The repo builds with InvariantGlobalization (no named culture can even be constructed), so "another
        // culture" is a clone of invariant with its month names swapped — enough to prove LastReport hardcodes
        // CultureInfo.InvariantCulture rather than reading Thread.CurrentThread.CurrentCulture.
        var swapped = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        swapped.DateTimeFormat.AbbreviatedMonthNames = ["ein", "zwei", "drei", "vier", "fuenf", "sechs", "sieben", "acht", "SEPTEMBER-NOT", "zehn", "elf", "zwoelf", ""];
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = swapped;
            var bundle = Bundle(reportId: "3f9ca1e2", stamp: new DateTime(2026, 9, 24, 14, 30, 0, DateTimeKind.Unspecified), version: "0.3.0");
            Assert.Contains("24 Sep 2026, 14:30", Recovery.Text.LastReport(bundle));
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = original; }
    }

    static Crash.BundleInfo Bundle(string reportId, DateTime stamp, string version) => new(
        Dir: @"C:\fake\logs\crash\20260924-143012-118-managed",
        Summary: new Crash.Summary(
            ReportId: reportId, InstallId: "8a1d9e0f", Kind: Crash.Kind.Managed, StampUtc: "2026-09-24T14:30:12.118Z",
            Version: version, Quad: version + ".41", Commit: "7e209e37", Channel: "stable", Arch: "arm64", OsBuild: "26100",
            Gpu: "NVIDIA GeForce RTX", GpuTier: "Strong", SoftwareAdapter: false, Packaged: true, Locale: "en-US",
            SessionId: "sess-1", UptimeMs: 12_345, BeforeFirstFrame: false, LastRoute: "artist:abc",
            ExceptionType: "System.InvalidOperationException", ExceptionMessage: "--crash-probe",
            Rvas: [0x7b1fc6], ModuleBase: 0x140000000, ModuleSize: 0x2000000,
            DebugId: "7E2C1B4A-0000-0000-0000-0000000000AA-1", ExitCode: 0, HasDump: true, DumpBytes: 4_200_000),
        StampLocal: stamp,
        Send: new Crash.SendRecord(Crash.SendState.NotSent, null, null, 0, false),
        HasDump: true,
        DumpBytes: 4_200_000);
}
