using Xunit;

namespace Wavee.Tests;

// CrashPromptPolicy.Decide (Diagnostics/CrashPromptPolicy.cs): the priority table over the three crash signals
// (a managed report beats a WER dump beats an unclean run marker), plus the opt-out -> Toast downgrade and the
// versionChanged suppression that applies ONLY to the weakest signal.
public class CrashPromptPolicyTests
{
    [Fact]
    public void NoSignal_DecidesNone()
    {
        var d = CrashPromptPolicy.Decide("", null, RunOutcome.Clean, optOut: false, versionChanged: false, uncleanExitOffered: false);

        Assert.Equal(CrashPromptMode.None, d.Mode);
        Assert.Equal(CrashSource.None, d.Source);
        Assert.Null(d.ReportPath);
        Assert.Null(d.DumpPath);
    }

    [Fact]
    public void ManagedReport_WinsOverEverything()
    {
        var d = CrashPromptPolicy.Decide("crash-report-20260901-101500.txt", "C:\\dump.dmp", RunOutcome.Unclean, optOut: false, versionChanged: false, uncleanExitOffered: false);

        Assert.Equal(CrashSource.ManagedReport, d.Source);
        Assert.Equal(CrashPromptMode.Dialog, d.Mode);
        Assert.Equal("crash-report-20260901-101500.txt", d.ReportPath);
        // Only the managed report's own path is carried when it's the source; the dump is a weaker, unused signal here.
        Assert.Equal("C:\\dump.dmp", d.DumpPath);
    }

    [Fact]
    public void WerDump_WinsOverUncleanExit_WhenNoManagedReport()
    {
        var d = CrashPromptPolicy.Decide("", "C:\\dump.dmp", RunOutcome.Unclean, optOut: false, versionChanged: false, uncleanExitOffered: false);

        Assert.Equal(CrashSource.WerDump, d.Source);
        Assert.Equal(CrashPromptMode.Dialog, d.Mode);
        Assert.Null(d.ReportPath);   // the report path is null unless the source IS ManagedReport
        Assert.Equal("C:\\dump.dmp", d.DumpPath);
    }

    [Fact]
    public void UncleanExit_IsTheWeakestSignal()
    {
        var d = CrashPromptPolicy.Decide("", null, RunOutcome.Unclean, optOut: false, versionChanged: false, uncleanExitOffered: false);

        Assert.Equal(CrashSource.UncleanExit, d.Source);
        Assert.Equal(CrashPromptMode.Dialog, d.Mode);
        Assert.Null(d.ReportPath);
        Assert.Null(d.DumpPath);
    }

    [Theory]
    [InlineData(RunOutcome.Clean)]
    [InlineData(RunOutcome.Unknown)]
    public void CleanOrUnknownPreviousRun_WithNoOtherSignal_DecidesNone(RunOutcome previousRun)
    {
        var d = CrashPromptPolicy.Decide("", null, previousRun, optOut: false, versionChanged: false, uncleanExitOffered: false);

        Assert.Equal(CrashPromptMode.None, d.Mode);
        Assert.Equal(CrashSource.None, d.Source);
    }

    /// <summary>versionChanged suppresses ONLY UncleanExit — the previous process was killed by an update
    /// deployment, not a crash.</summary>
    [Fact]
    public void VersionChanged_SuppressesOnlyUncleanExit()
    {
        var suppressed = CrashPromptPolicy.Decide("", null, RunOutcome.Unclean, optOut: false, versionChanged: true, uncleanExitOffered: false);
        Assert.Equal(CrashSource.None, suppressed.Source);
        Assert.Equal(CrashPromptMode.None, suppressed.Mode);
    }

    [Fact]
    public void VersionChanged_NeverSuppressesAManagedReport()
    {
        var d = CrashPromptPolicy.Decide("crash-report-x.txt", null, RunOutcome.Unclean, optOut: false, versionChanged: true, uncleanExitOffered: false);
        Assert.Equal(CrashSource.ManagedReport, d.Source);
        Assert.Equal(CrashPromptMode.Dialog, d.Mode);
    }

    [Fact]
    public void VersionChanged_NeverSuppressesAWerDump()
    {
        var d = CrashPromptPolicy.Decide("", "C:\\dump.dmp", RunOutcome.Unclean, optOut: false, versionChanged: true, uncleanExitOffered: false);
        Assert.Equal(CrashSource.WerDump, d.Source);
        Assert.Equal(CrashPromptMode.Dialog, d.Mode);
    }

    [Theory]
    [InlineData(true, CrashPromptMode.Toast)]
    [InlineData(false, CrashPromptMode.Dialog)]
    public void OptOut_DowngradesToToast_ButNeverSuppressesTheSignal(bool optOut, CrashPromptMode expected)
    {
        var d = CrashPromptPolicy.Decide("crash-report-x.txt", null, RunOutcome.Clean, optOut, versionChanged: false, uncleanExitOffered: false);

        Assert.Equal(expected, d.Mode);
        Assert.Equal(CrashSource.ManagedReport, d.Source);
    }

    /// <summary>The evidence-free signal is offered ONCE per unclean streak: once Program.cs has latched
    /// UncleanExitOffered, a further stale-"running" marker (the process was killed again — an IDE stop on every
    /// run) decides None until RunMarker.End re-arms it. This is the "crash prompt on EVERY launch" fix.</summary>
    [Fact]
    public void UncleanExit_AlreadyOffered_DecidesNone()
    {
        var d = CrashPromptPolicy.Decide("", null, RunOutcome.Unclean, optOut: false, versionChanged: false, uncleanExitOffered: true);

        Assert.Equal(CrashSource.None, d.Source);
        Assert.Equal(CrashPromptMode.None, d.Mode);
    }

    [Fact]
    public void UncleanExitOffered_NeverGatesRealEvidence()
    {
        var report = CrashPromptPolicy.Decide("crash-report-x.txt", null, RunOutcome.Unclean, optOut: false, versionChanged: false, uncleanExitOffered: true);
        var dump = CrashPromptPolicy.Decide("", "C:\\dump.dmp", RunOutcome.Unclean, optOut: false, versionChanged: false, uncleanExitOffered: true);

        Assert.Equal(CrashSource.ManagedReport, report.Source);
        Assert.Equal(CrashPromptMode.Dialog, report.Mode);
        Assert.Equal(CrashSource.WerDump, dump.Source);
        Assert.Equal(CrashPromptMode.Dialog, dump.Mode);
    }

    /// <summary>"Don't ask again after a crash" fully suppresses the evidence-free signal (there is nothing to hand
    /// the user but a question), while evidence-backed sources still surface as the passive toast.</summary>
    [Fact]
    public void OptOut_SuppressesUncleanExitOutright()
    {
        var d = CrashPromptPolicy.Decide("", null, RunOutcome.Unclean, optOut: true, versionChanged: false, uncleanExitOffered: false);

        Assert.Equal(CrashSource.None, d.Source);
        Assert.Equal(CrashPromptMode.None, d.Mode);
    }

    [Fact]
    public void OptOut_StillToastsAWerDump()
    {
        var d = CrashPromptPolicy.Decide("", "C:\\dump.dmp", RunOutcome.Unclean, optOut: true, versionChanged: false, uncleanExitOffered: true);

        Assert.Equal(CrashSource.WerDump, d.Source);
        Assert.Equal(CrashPromptMode.Toast, d.Mode);
    }

    [Fact]
    public void Default_DecisionStruct_IsNone()
    {
        // ReportChrome resets CrashPromptPolicy.ThisLaunch to `default` after consuming it once — pin that shape.
        CrashPromptDecision d = default;
        Assert.Equal(CrashPromptMode.None, d.Mode);
        Assert.Equal(CrashSource.None, d.Source);
        Assert.Null(d.ReportPath);
        Assert.Null(d.DumpPath);
    }
}
