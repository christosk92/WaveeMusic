using Xunit;

namespace Wavee.Tests;

// The SMTC timeline latch (App/SmtcTimelineCoalescer.cs, source-included) — the PURE half of SystemMediaControlsBridge.
//
// The bridge itself is untestable here by construction: every one of its methods bails on a null SystemMediaControls,
// which only a real WinRT media session can produce. So the DECISION is extracted — which ticks schedule a flush, which
// value survives a burst, and which pushes the OS actually gets — and that decision is what these drive. The bridge is
// then a two-line adapter over it (`if (_timeline.Push(ms)) _post(_flushTimeline);`), verified live.
//
// What is at stake: every push the latch lets through is a WinRT activation plus a CROSS-PROCESS COM RPC on the UI
// thread. One per second is free. One per queued position tick — which is what a drained backlog produced, since each
// queued tick carries a DIFFERENT whole second and so slipped straight past the per-second dedupe — is a visible hang.
public class SmtcTimelineCoalescerTests
{
    // ── the burst latch: one scheduled flush per burst, newest wins ────────────────────────────────────────────────────

    [Fact]
    public void FirstPushSchedulesAFlush()
    {
        var c = new SmtcTimelineCoalescer();
        Assert.True(c.Push(1_000));
        Assert.True(c.FlushQueued);
    }

    /// <summary>THE COALESCING RULE. N ticks in one burst schedule exactly ONE flush — every tick after the first
    /// returns false, so the bridge posts once and the OS is told once.</summary>
    [Fact]
    public void BurstOfTicksSchedulesExactlyOneFlush()
    {
        var c = new SmtcTimelineCoalescer();
        int scheduled = 0;
        for (int i = 0; i < 500; i++)
            if (c.Push(i * 1_000L)) scheduled++;   // a different whole SECOND each time: the dedupe cannot save us here
        Assert.Equal(1, scheduled);
    }

    /// <summary>Newest wins: the value the single flush carries is the LAST tick of the burst, not the first — an
    /// SMTC scrub bar that resumed at the oldest queued position would visibly jump backwards on restore.</summary>
    [Fact]
    public void BurstFlushCarriesTheNewestPosition()
    {
        var c = new SmtcTimelineCoalescer();
        for (int i = 0; i < 500; i++) c.Push(i * 1_000L);

        Assert.True(c.TryTake(600_000, out long pos));
        Assert.Equal(499_000, pos);
        Assert.False(c.FlushQueued);
    }

    /// <summary>Consuming the latch re-arms the NEXT burst — the flush bit is a one-shot, not a permanent gate.</summary>
    [Fact]
    public void AfterAFlushTheNextTickSchedulesAgain()
    {
        var c = new SmtcTimelineCoalescer();
        c.Push(1_000);
        c.TryTake(300_000, out _);
        Assert.True(c.Push(2_000));
    }

    // ── the steady-state per-second dedupe (the old _lastTimelineSec rule) ─────────────────────────────────────────────

    /// <summary>Sub-second movement inside the same whole second still makes no OS call — the ~1 Hz cadence the OS
    /// expects is unchanged by the coalescing.</summary>
    [Fact]
    public void SameWholeSecondIsDeduped()
    {
        var c = new SmtcTimelineCoalescer();
        c.Push(5_000);
        Assert.True(c.TryTake(300_000, out _));

        c.Push(5_400);
        Assert.False(c.TryTake(300_000, out _));   // still second 5

        c.Push(6_100);
        Assert.True(c.TryTake(300_000, out long pos));
        Assert.Equal(6_100, pos);
    }

    /// <summary>Position 0 of a freshly-started track must push. A latch whose "last second" defaulted to 0 would
    /// swallow the very first timeline of every session.</summary>
    [Fact]
    public void FirstPushAtZeroIsNotDeduped()
    {
        var c = new SmtcTimelineCoalescer();
        c.Push(0);
        Assert.True(c.TryTake(300_000, out long pos));
        Assert.Equal(0, pos);
    }

    // ── clamping + bail-outs ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PositionIsClampedToTheDuration()
    {
        var c = new SmtcTimelineCoalescer();
        c.Push(999_999);
        Assert.True(c.TryTake(120_000, out long pos));
        Assert.Equal(120_000, pos);

        var d = new SmtcTimelineCoalescer();
        d.Push(-5_000);
        Assert.True(d.TryTake(120_000, out long neg));
        Assert.Equal(0, neg);
    }

    [Fact]
    public void UnknownDurationPushesNothing()
    {
        var c = new SmtcTimelineCoalescer();
        c.Push(1_000);
        Assert.False(c.TryTake(0, out _));
    }

    /// <summary>A bail-out must still DISARM. If a flush that found no session left the bit set, the latch would be
    /// wedged and the timeline would never update again for the rest of the process.</summary>
    [Fact]
    public void BailedOutFlushStillClearsTheLatch()
    {
        var c = new SmtcTimelineCoalescer();
        c.Push(1_000);
        Assert.False(c.TryTake(0, out _));   // no session / unknown duration
        Assert.False(c.FlushQueued);
        Assert.True(c.Push(2_000));          // the next tick can schedule again
    }
}
