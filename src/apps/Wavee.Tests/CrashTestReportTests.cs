// ── Wavee.Tests/CrashTestReportTests.cs — Settings › Developer › "Send a test crash report" (#165) ─────────────────
//
// The pure facts behind the developer button (Platform/Crash.TestReport.cs, Crash.Handler.DumpKinds): the D line's kind
// tokens, which kinds latch the child's exit-code inference (a crash's request does; the live `test` snapshot's must
// not, or a real non-zero exit later in the same run would go unrecorded), that the simulated exception is really
// thrown and caught with frames of its own, and that it titles its summary like any managed crash. No child process,
// no disk, no window.

using Xunit;

namespace Wavee.Tests;

// ── 1. the D line's kinds ───────────────────────────────────────────────────────────────────────────────────────────

public class CrashDumpKindTests
{
    [Theory]
    [InlineData("managed", Crash.Handler.DumpKind.Managed)]
    [InlineData("native", Crash.Handler.DumpKind.Native)]
    [InlineData("test", Crash.Handler.DumpKind.Test)]
    public void A_known_token_parses_to_its_kind(string token, Crash.Handler.DumpKind kind)
        => Assert.Equal(kind, Crash.Handler.DumpKinds.Parse(token));

    /// <summary>The protocol is lower-case and ordinal: anything else — a future kind, a different case, stray
    /// whitespace, nothing at all — is Unknown, never silently one of the known kinds.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Managed")]
    [InlineData("TEST")]
    [InlineData("test ")]
    [InlineData("hang")]
    [InlineData("unknown")]
    public void Any_other_token_is_unknown(string? token)
        => Assert.Equal(Crash.Handler.DumpKind.Unknown, Crash.Handler.DumpKinds.Parse(token));

    [Fact]
    public void The_wire_tokens_are_pinned()
    {
        Assert.Equal("managed", Crash.Handler.DumpKinds.Token(Crash.Handler.DumpKind.Managed));
        Assert.Equal("native", Crash.Handler.DumpKinds.Token(Crash.Handler.DumpKind.Native));
        Assert.Equal("test", Crash.Handler.DumpKinds.Token(Crash.Handler.DumpKind.Test));
    }

    /// <summary>What the parent writes is what the child reads, for every kind the parent can send.</summary>
    [Theory]
    [InlineData(Crash.Handler.DumpKind.Managed)]
    [InlineData(Crash.Handler.DumpKind.Native)]
    [InlineData(Crash.Handler.DumpKind.Test)]
    public void Every_sendable_kind_round_trips_through_its_token(Crash.Handler.DumpKind kind)
        => Assert.Equal(kind, Crash.Handler.DumpKinds.Parse(Crash.Handler.DumpKinds.Token(kind)));

    /// <summary>A crash's request IS the crash, already bundled, so the exit-code bundle is suppressed; the test
    /// snapshot's parent keeps running, so the inference stays armed. Unknown latches, as every request did before the
    /// kinds were told apart.</summary>
    [Theory]
    [InlineData(Crash.Handler.DumpKind.Managed, true)]
    [InlineData(Crash.Handler.DumpKind.Native, true)]
    [InlineData(Crash.Handler.DumpKind.Unknown, true)]
    [InlineData(Crash.Handler.DumpKind.Test, false)]
    public void Only_the_test_kind_leaves_the_exit_code_inference_armed(Crash.Handler.DumpKind kind, bool latches)
        => Assert.Equal(latches, Crash.Handler.DumpKinds.LatchesBundle(kind));

    [Fact]
    public void A_test_request_parsed_off_the_wire_does_not_latch()
        => Assert.False(Crash.Handler.DumpKinds.LatchesBundle(Crash.Handler.DumpKinds.Parse("test")));
}

// ── 2. the simulated exception and its summary ──────────────────────────────────────────────────────────────────────

public class SimulatedCrashTests
{
    [Fact]
    public void ThrowAndCatch_returns_a_really_thrown_exception_with_frames_of_its_own()
    {
        var ex = Crash.SimulatedCrashException.ThrowAndCatch();
        Assert.Equal(Crash.SimulatedCrashException.Text, ex.Message);
        Assert.False(string.IsNullOrEmpty(ex.StackTrace));
        // the three no-inline throw frames plus the catching one
        Assert.True(new System.Diagnostics.StackTrace(ex, false).FrameCount >= 3);
    }

    [Fact]
    public void The_summary_is_a_managed_one_titled_by_the_simulated_exception()
    {
        var s = Crash.Report.BuildSummary(Crash.Kind.Managed, Crash.SimulatedCrashException.ThrowAndCatch(), 0);
        Assert.Equal(Crash.Kind.Managed, s.Kind);
        Assert.Equal(typeof(Crash.SimulatedCrashException).FullName, s.ExceptionType);
        Assert.Contains("SimulatedCrashException", s.ExceptionType, StringComparison.Ordinal);
        Assert.Equal(Crash.SimulatedCrashException.Text, s.ExceptionMessage);
        Assert.Equal(0, s.ExitCode);
        Assert.Equal(0u, s.ExceptionCode);
        Assert.False(s.HasDump);   // the child finalizes hasDump/dumpBytes after it writes the dump
        Assert.Equal(32, s.ReportId.Length);
    }

    [Fact]
    public void The_report_text_carries_the_exception()
    {
        string report = Crash.Report.Describe(Crash.SimulatedCrashException.ThrowAndCatch());
        Assert.Contains(typeof(Crash.SimulatedCrashException).FullName!, report, StringComparison.Ordinal);
        Assert.Contains(Crash.SimulatedCrashException.Text, report, StringComparison.Ordinal);
    }
}

// ── 3. the row is a developer-only one ──────────────────────────────────────────────────────────────────────────────

public class TestCrashReportRowTests
{
    [Fact]
    public void The_row_is_composed_only_in_developer_mode()
    {
        Assert.False(Settings.Catalog.RowVisible(Settings.Tab.General, "sendTestCrashReport", developerMode: false));
        Assert.True(Settings.Catalog.RowVisible(Settings.Tab.General, "sendTestCrashReport", developerMode: true));
    }
}
