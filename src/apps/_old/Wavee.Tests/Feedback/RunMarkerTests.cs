using Xunit;

namespace Wavee.Tests;

// RunMarker (Diagnostics/RunMarker.cs): the one-value "was the previous run clean" breadcrumb Program.cs brackets
// every launch with. MemoryAppSettings stands in for IAppSettings exactly like SetupGatingTests.
public class RunMarkerTests
{
    [Fact]
    public void Begin_OnAFreshInstall_ReturnsUnknown_AndWritesRunning()
    {
        var settings = new MemoryAppSettings();

        var outcome = RunMarker.Begin(settings);

        Assert.Equal(RunOutcome.Unknown, outcome);
        Assert.Equal(RunMarker.Running, settings.Get(WaveeSettings.RunMarker));
    }

    [Fact]
    public void Begin_End_Begin_ReturnsClean()
    {
        var settings = new MemoryAppSettings();

        RunMarker.Begin(settings);
        RunMarker.End(settings);
        var outcome = RunMarker.Begin(settings);

        Assert.Equal(RunOutcome.Clean, outcome);
    }

    [Fact]
    public void Begin_Begin_WithNoInterveningEnd_ReturnsUnclean()
    {
        var settings = new MemoryAppSettings();

        RunMarker.Begin(settings);
        var outcome = RunMarker.Begin(settings);   // the previous process never got to End()

        Assert.Equal(RunOutcome.Unclean, outcome);
    }

    /// <summary>A managed handler already wrote its own crash report for this run — <c>End</c> (from ProcessExit /
    /// the app-loop return) must never downgrade that to "clean", or the crash prompt loses its strongest signal.</summary>
    [Fact]
    public void MarkCrashed_ThenEnd_KeepsCrashed()
    {
        var settings = new MemoryAppSettings();
        RunMarker.Begin(settings);

        RunMarker.MarkCrashed(settings);
        RunMarker.End(settings);

        Assert.Equal(RunMarker.Crashed, settings.Get(WaveeSettings.RunMarker));
    }

    /// <summary>The once-per-streak gate: a launch that offered the evidence-free prompt latches UncleanExitOffered;
    /// a further killed run must decide None, and only an orderly End (or a reported crash) re-arms it.</summary>
    [Fact]
    public void UncleanStreak_OffersOnce_UntilACleanExitReArms()
    {
        var settings = new MemoryAppSettings();
        RunMarker.Begin(settings);                               // run 1 — killed (no End)

        var run2 = RunMarker.Begin(settings);                    // run 2 sees the stale "running"
        var offered = CrashPromptPolicy.Decide("", null, run2, optOut: false, versionChanged: false,
            settings.Get(WaveeSettings.UncleanExitOffered));
        Assert.Equal(CrashSource.UncleanExit, offered.Source);
        settings.Set(WaveeSettings.UncleanExitOffered, true);    // Program.cs latches at decision time
                                                                 // run 2 — killed too (the IDE-stop workflow)
        var run3 = RunMarker.Begin(settings);
        var silent = CrashPromptPolicy.Decide("", null, run3, optOut: false, versionChanged: false,
            settings.Get(WaveeSettings.UncleanExitOffered));
        Assert.Equal(RunOutcome.Unclean, run3);
        Assert.Equal(CrashSource.None, silent.Source);           // not re-asked

        RunMarker.End(settings);                                 // run 3 exits cleanly → the streak is over
        Assert.False(settings.Get(WaveeSettings.UncleanExitOffered));
        RunMarker.Begin(settings);                               // run 4 — killed
        var run5 = RunMarker.Begin(settings);
        var again = CrashPromptPolicy.Decide("", null, run5, optOut: false, versionChanged: false,
            settings.Get(WaveeSettings.UncleanExitOffered));
        Assert.Equal(CrashSource.UncleanExit, again.Source);     // a fresh streak is offered once more
    }

    [Fact]
    public void MarkCrashed_ReArmsTheUncleanExitOffer()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.UncleanExitOffered, true);

        RunMarker.MarkCrashed(settings);

        Assert.False(settings.Get(WaveeSettings.UncleanExitOffered));
        Assert.Equal(RunMarker.Crashed, settings.Get(WaveeSettings.RunMarker));
    }

    /// <summary>The next launch after a managed crash reads as UNCLEAN: when the handler managed to write its report,
    /// CrashPromptPolicy ranks ManagedReport above UncleanExit so nothing is double-counted; when the write failed, this
    /// is the only signal left that a crash happened at all.</summary>
    [Fact]
    public void Begin_AfterCrashed_ReturnsUnclean()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.RunMarker, RunMarker.Crashed);

        var outcome = RunMarker.Begin(settings);

        Assert.Equal(RunOutcome.Unclean, outcome);
        Assert.Equal(RunMarker.Running, settings.Get(WaveeSettings.RunMarker));
    }
}
