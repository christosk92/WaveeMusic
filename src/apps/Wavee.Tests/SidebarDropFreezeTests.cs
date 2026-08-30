using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE MID-DRAG FREEZE (D, phase C). While a rootlist filing is live, the sidebar must not re-project: a fresh
/// projection re-keys the very rows the drag is aiming at, and the drop then lands somewhere the user did not aim. The
/// pane parks the newest stage in a <see cref="SidebarStageHold{TStage}"/> and flushes it on SESSION END.
///
/// <para>These drive the real state machine (the pane's own <c>PlanStage</c> is a private nested record, and none of
/// these rules depend on what a stage IS). What they pin is the four ways the bay can go wrong: a stage published
/// mid-session escaping the hold, a burst of publishes applying anything but the newest, a flush that runs twice, and a
/// stage that arrives after the session ended waiting for a flush that will never come.</para>
/// </summary>
public class SidebarDropFreezeTests
{
    static SidebarStageHold<string> Bay() => new();

    [Fact]
    public void AStagePublishedMidSession_IsHeld_NotPublished()
    {
        var bay = Bay();
        Assert.True(bay.TryHold(sessionLive: true, "stage-1"));
        Assert.True(bay.HasHeld);
    }

    [Fact]
    public void ABurstDuringOneSession_ConvergesToTheNewestStage()
    {
        var bay = Bay();
        // A dealer push, our own ack and a background revalidate can all land inside one gesture. Only the last one
        // describes the library the user will be looking at when the drag ends.
        Assert.True(bay.TryHold(sessionLive: true, "stage-1"));
        Assert.True(bay.TryHold(sessionLive: true, "stage-2"));
        Assert.True(bay.TryHold(sessionLive: true, "stage-3"));

        Assert.True(bay.TryFlush(out string? flushed));
        Assert.Equal("stage-3", flushed);
    }

    [Fact]
    public void TheFlush_HappensExactlyOnce()
    {
        var bay = Bay();
        bay.TryHold(sessionLive: true, "stage-1");

        Assert.True(bay.TryFlush(out string? first));
        Assert.Equal("stage-1", first);
        // The watcher's layout effect is edge-keyed, but the pane also discards on every publish — flushing the same
        // stage twice would re-publish a plan the list has already diffed against.
        Assert.False(bay.TryFlush(out string? second));
        Assert.Null(second);
        Assert.False(bay.HasHeld);
    }

    [Fact]
    public void AStageThatArrivesAfterTheSessionEnded_PublishesNormally()
    {
        var bay = Bay();
        // THE RACE THAT MATTERS. Nothing will flush this one — no session end is coming — so the hold must decline it
        // and let the caller publish on the spot.
        Assert.False(bay.TryHold(sessionLive: false, "stage-late"));
        Assert.False(bay.HasHeld);
        Assert.False(bay.TryFlush(out _));
    }

    [Fact]
    public void AFlushWithNothingParked_IsANoOp()
    {
        // Every session end calls the flush, and the overwhelming majority of drags never held anything (a track drag is
        // not frozen at all).
        Assert.False(Bay().TryFlush(out string? stage));
        Assert.Null(stage);
    }

    [Fact]
    public void Discard_DropsTheParkedStage_SoAnUnmountMidSessionLeaksNothing()
    {
        var bay = Bay();
        bay.TryHold(sessionLive: true, "stage-1");
        bay.Discard();

        Assert.False(bay.HasHeld);
        Assert.False(bay.TryFlush(out _));
    }

    [Fact]
    public void ASessionThatEndsAndBeginsAgain_HoldsAndFlushesIndependently()
    {
        var bay = Bay();
        bay.TryHold(sessionLive: true, "first-session");
        Assert.True(bay.TryFlush(out string? first));
        Assert.Equal("first-session", first);

        bay.TryHold(sessionLive: true, "second-session");
        Assert.True(bay.TryFlush(out string? second));
        Assert.Equal("second-session", second);
    }
}
