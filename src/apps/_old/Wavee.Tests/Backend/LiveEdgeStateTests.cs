using Wavee.Backend.Playback;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The live-edge hysteresis machine. Every case here is one a real broadcast produces several times a second and none
/// of them can be reproduced on demand against a live stream, which is the whole reason <see cref="LiveEdgeState"/> is
/// a pure fold: the observed defect was the player bar's right slot FLICKERING between the LIVE mark and
/// "GO LIVE −0:06" while a YouTube HLS playhead rode its natural 5–8 s inside the window's end.
/// </summary>
public class LiveEdgeStateTests
{
    static LiveEdgeState Feed(LiveEdgeState from, bool hasWindow, params long[] behindMs)
    {
        LiveEdgeState s = from;
        foreach (long b in behindMs) s = LiveEdgeState.Next(s, b, hasWindow);
        return s;
    }

    static LiveEdgeState Feed(params long[] behindMs) => Feed(LiveEdgeState.AtEdge, true, behindMs);

    // ── the thresholds themselves ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The two lines are asymmetric, and the gap between them IS the hysteresis. Pinned as VALUES because the
    /// whole fix is that they are far enough apart to sit either side of a healthy playhead's ride.</summary>
    [Fact]
    public void TheTwoLines_AreWideApart_AndTheEnterLineIsTheHigherOne()
    {
        Assert.Equal(15_000L, LiveEdgeState.EnterBehindMs);
        Assert.Equal(5_000L, LiveEdgeState.ReturnToEdgeMs);
        Assert.True(LiveEdgeState.ReturnToEdgeMs < LiveEdgeState.EnterBehindMs);
        Assert.Equal(2, LiveEdgeState.ConfirmReports);
    }

    /// <summary>A playable starts AT the edge — the honest default, and what every non-live playable stays.</summary>
    [Fact]
    public void TheDefaultState_IsAtTheEdge()
    {
        Assert.False(LiveEdgeState.AtEdge.IsBehind);
        Assert.Equal(0, LiveEdgeState.AtEdge.PendingReports);
        Assert.False(default(LiveEdgeState).IsBehind);
        Assert.True(LiveEdgeState.Behind.IsBehind);
    }

    // ── entering BEHIND ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE defect, as a test. A live playhead sitting 5–8 s behind a window that republishes four times a
    /// second must never leave the AT-EDGE state — under the old 6 s comparison this exact sequence flipped the slot
    /// between the mark and the action on nearly every report.</summary>
    [Theory]
    [InlineData(5_000L)]
    [InlineData(6_000L)]
    [InlineData(8_000L)]
    [InlineData(15_000L)]   // the enter line is inclusive-at-edge: AT 15 s you are still at the edge
    public void AHealthyRide_NeverLeavesTheEdge_HoweverManyReportsArrive(long behindMs)
    {
        LiveEdgeState s = LiveEdgeState.AtEdge;
        for (int i = 0; i < 50; i++)
        {
            s = LiveEdgeState.Next(s, behindMs, hasWindow: true);
            Assert.False(s.IsBehind);
        }
    }

    /// <summary>The natural wobble — a playhead breathing either side of the enter line — also never flips, because
    /// one report back inside CLEARS the confirmation counter. Only consecutive reports count.</summary>
    [Fact]
    public void AWobbleAcrossTheEnterLine_NeverConfirms()
    {
        LiveEdgeState s = Feed(16_000, 7_000, 16_000, 8_000, 20_000, 6_000, 18_000);
        Assert.False(s.IsBehind);
    }

    /// <summary>ONE report past the line is a window that jumped, not a playhead that fell — it arms the confirmation
    /// and nothing else.</summary>
    [Fact]
    public void OneReportPastTheLine_DoesNotEnterBehind_ButArmsTheConfirmation()
    {
        LiveEdgeState s = LiveEdgeState.Next(LiveEdgeState.AtEdge, 60_000, hasWindow: true);
        Assert.False(s.IsBehind);
        Assert.Equal(1, s.PendingReports);
    }

    /// <summary>TWO consecutive reports past the line is a playhead. That is the whole confirmation rule.</summary>
    [Fact]
    public void TwoConsecutiveReportsPastTheLine_EnterBehind()
    {
        LiveEdgeState s = Feed(60_000, 60_000);
        Assert.True(s.IsBehind);
        Assert.Equal(0, s.PendingReports);   // settled: the counter is bookkeeping, not a running total
    }

    /// <summary>One millisecond either side of the enter line — the rung is exact.</summary>
    [Fact]
    public void TheEnterLine_IsExact()
    {
        Assert.False(Feed(LiveEdgeState.EnterBehindMs, LiveEdgeState.EnterBehindMs).IsBehind);
        Assert.True(Feed(LiveEdgeState.EnterBehindMs + 1, LiveEdgeState.EnterBehindMs + 1).IsBehind);
    }

    // ── leaving BEHIND ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Between the two lines the state HOLDS. A playable that fell to 60 s behind and has been dragged back to
    /// 9 s is still BEHIND — leaving at the same line it entered by is exactly what chatters.</summary>
    [Theory]
    [InlineData(14_000L)]
    [InlineData(9_000L)]
    [InlineData(5_001L)]
    public void BetweenTheTwoLines_ABehindPlayableStaysBehind(long behindMs)
    {
        LiveEdgeState s = Feed(60_000, 60_000);
        Assert.True(s.IsBehind);
        for (int i = 0; i < 20; i++)
        {
            s = LiveEdgeState.Next(s, behindMs, hasWindow: true);
            Assert.True(s.IsBehind);
        }
    }

    /// <summary>At or under the RETURN line it is back at the edge — and immediately, with no confirmation: the cost of
    /// showing the mark a beat early is nothing, the cost of leaving a "GO LIVE −0:02" button on screen is a lie.</summary>
    [Theory]
    [InlineData(5_000L)]
    [InlineData(1_000L)]
    [InlineData(0L)]
    public void AtOrUnderTheReturnLine_ItIsBackAtTheEdge_OnTheFirstReport(long behindMs)
    {
        LiveEdgeState s = Feed(60_000, 60_000);
        s = LiveEdgeState.Next(s, behindMs, hasWindow: true);
        Assert.False(s.IsBehind);
        Assert.Equal(0, s.PendingReports);
    }

    /// <summary>One millisecond either side of the return line — the rung is exact.</summary>
    [Fact]
    public void TheReturnLine_IsExact()
    {
        LiveEdgeState behind = Feed(60_000, 60_000);
        Assert.False(LiveEdgeState.Next(behind, LiveEdgeState.ReturnToEdgeMs, true).IsBehind);
        Assert.True(LiveEdgeState.Next(behind, LiveEdgeState.ReturnToEdgeMs + 1, true).IsBehind);
    }

    /// <summary>Having returned, the machine needs the FULL confirmation again to fall back — a return does not leave a
    /// half-armed counter behind it.</summary>
    [Fact]
    public void AfterReturning_TheConfirmationStartsOver()
    {
        LiveEdgeState s = Feed(60_000, 60_000, 2_000);
        Assert.False(s.IsBehind);
        s = LiveEdgeState.Next(s, 60_000, true);
        Assert.False(s.IsBehind);                      // one report is not enough, again
        s = LiveEdgeState.Next(s, 60_000, true);
        Assert.True(s.IsBehind);
    }

    // ── the GoLive reset, and nothing-to-rewind ─────────────────────────────────────────────────────────────────────

    /// <summary>GO LIVE puts the machine at the edge outright (<c>PlaybackBridge.GoLive</c> assigns
    /// <see cref="LiveEdgeState.AtEdge"/>), and from there it takes the full two-report confirmation to fall back —
    /// so the in-flight seek's own stale reports cannot put the button straight back on screen.</summary>
    [Fact]
    public void AGoLiveReset_ClearsBothTheStateAndTheConfirmation()
    {
        LiveEdgeState s = Feed(60_000, 60_000);
        Assert.True(s.IsBehind);

        s = LiveEdgeState.AtEdge;                       // ← what GoLive does
        Assert.False(s.IsBehind);
        s = LiveEdgeState.Next(s, 60_000, true);        // one stale pre-seek report
        Assert.False(s.IsBehind);
    }

    /// <summary>A station with nothing to rewind can never be behind: there is no way back and nothing to go back to.
    /// The same answer covers every non-live playable, and it CLEARS the machine, so a half-confirmed fall can never
    /// ride across a variant switch into the next playable.</summary>
    [Fact]
    public void WithNothingToRewind_ItIsAlwaysAtTheEdge_AndTheMachineIsCleared()
    {
        Assert.False(LiveEdgeState.Next(LiveEdgeState.Behind, 600_000, hasWindow: false).IsBehind);
        Assert.Equal(0, LiveEdgeState.Next(new LiveEdgeState(false, 1), 600_000, hasWindow: false).PendingReports);
        Assert.False(Feed(LiveEdgeState.AtEdge, false, 60_000, 60_000, 60_000).IsBehind);
    }

    /// <summary>The fold is a pure function of (previous, behind, hasWindow) — replaying the same report sequence from
    /// the same start always lands on the same state, which is what lets the bar trust one signal.</summary>
    [Fact]
    public void TheFold_IsDeterministic()
    {
        long[] reports = [7_000, 16_000, 4_000, 40_000, 40_000, 12_000, 3_000, 20_000, 20_000];
        Assert.Equal(Feed(reports), Feed(reports));
    }
}
