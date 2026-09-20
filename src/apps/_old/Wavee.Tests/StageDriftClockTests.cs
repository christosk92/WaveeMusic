using Xunit;

namespace Wavee.Tests;

public class StageDriftClockTests
{
    [Fact]
    public void InitialAndPausedStageDoNotAdvance()
    {
        var clock = new StageDriftClock();
        Assert.Equal(0d, clock.Sample(100));
        clock.SetRunning(true, 100);
        Assert.Equal(0d, clock.Sample(101)); // first visible tick starts at identity
        Assert.Equal(2d, clock.Sample(103));
        clock.SetRunning(false, 103.1);
        Assert.False(clock.Running);
        Assert.Equal(2d, clock.Sample(1000)); // preserves the last sampled phase, not a new pose at the pause edge
    }

    [Fact]
    public void ResumeContinuesThePhaseWithoutSpendingPausedTime()
    {
        var clock = new StageDriftClock();
        clock.SetRunning(true, 10);
        clock.Sample(10);
        Assert.Equal(7d, clock.Sample(17));
        clock.SetRunning(false, 17.02);
        clock.SetRunning(true, 10_000);
        Assert.Equal(7d, clock.Sample(10_000));
        Assert.Equal(7.5d, clock.Sample(10_000.5));
        clock.SetRunning(true, 10_001); // an unrelated render with unchanged state must not reset the clock
        Assert.Equal(8d, clock.Sample(10_001));
    }

    [Fact]
    public void RepeatedPauseResumeCyclesPreserveOnlyPlayingTime()
    {
        var clock = new StageDriftClock();
        clock.SetRunning(true, 0);
        clock.Sample(0);
        for (int cycle = 0; cycle < 100; cycle++)
        {
            double start = cycle * 100d;
            clock.SetRunning(true, start);
            Assert.Equal(cycle + 1d, clock.Sample(start + 1));
            clock.SetRunning(false, start + 1);
            Assert.Equal(cycle + 1d, clock.Sample(start + 99));
        }
    }

    [Fact]
    public void ResetRestoresIdentityAndAFreshFirstTick()
    {
        var clock = new StageDriftClock();
        clock.SetRunning(true, 1);
        clock.Sample(1);
        clock.Sample(15);
        clock.Reset();
        Assert.False(clock.Running);
        Assert.Equal(0d, clock.Sample(100));
        clock.SetRunning(true, 100);
        Assert.Equal(0d, clock.Sample(101));
        Assert.Equal(1d, clock.Sample(102));
    }
}
