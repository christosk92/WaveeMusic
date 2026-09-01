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

    /// <summary>The next launch after a managed crash still reads as a clean start (the crash was already reported
    /// and counted — <see cref="RunOutcome.Unclean"/> would double-count it as a second, weaker signal).</summary>
    [Fact]
    public void Begin_AfterCrashed_ReturnsClean()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.RunMarker, RunMarker.Crashed);

        var outcome = RunMarker.Begin(settings);

        Assert.Equal(RunOutcome.Clean, outcome);
        Assert.Equal(RunMarker.Running, settings.Get(WaveeSettings.RunMarker));
    }
}
