// ── Wavee.Tests/ShowVisitTests.cs — which first screen a show's visitor gets (podcast plan §5.2, §10; D-1) ─────────
//
// `ShowVisit.Of` decides the show page's head (start-here doors · continue · caught up) and the rail's primary (Follow ·
// Resume · Play latest). Progress beats follow state — follow is not even an input — and caught-up needs progress.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ShowVisitTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(40, 0)]
    [InlineData(0, 3)]
    public void NoProgress_IsNew_WhateverTheCounts(int unplayed, int inProgress)
        => Assert.Equal(ShowVisitKind.New, ShowVisit.Of(anyProgress: false, unplayed, inProgress));

    [Fact]
    public void ProgressBeatsFollowState_FortyEpisodesInIsReturning()
    {
        // Someone 40 episodes in who never followed: the page greets them with "continue", not "start here" (D-1).
        Assert.Equal(ShowVisitKind.Returning, ShowVisit.Of(anyProgress: true, unplayed: 88, inProgress: 0));
        Assert.Equal(ShowVisitKind.Returning, ShowVisit.Of(anyProgress: true, unplayed: 0, inProgress: 1));
    }

    [Fact]
    public void CaughtUp_NeedsProgress_AndNothingUnplayedOrInProgress()
    {
        Assert.Equal(ShowVisitKind.CaughtUp, ShowVisit.Of(anyProgress: true, unplayed: 0, inProgress: 0));
        // An empty show nobody has played is not "caught up" — there is nothing to be caught up WITH.
        Assert.Equal(ShowVisitKind.New, ShowVisit.Of(anyProgress: false, unplayed: 0, inProgress: 0));
    }

    [Fact]
    public void FedTheLedgersCounts_TheHeadAgreesWithTheLedgerLine()
    {
        // The contract the page codes against: anyProgress = Played + InProgress > 0, unplayed = ToGo.
        var ledger = ShowLedger.Of([1f, 1f, 1f], [300, 200, 100], lastPlayedAt: 400, total: 3, ConsumptionOrder.Episodic);
        Assert.Equal(ShowVisitKind.CaughtUp,
            ShowVisit.Of(ledger.Played + ledger.InProgress > 0, ledger.ToGo, ledger.InProgress));

        ledger = ShowLedger.Of([0f, 0f], [200, 100], lastPlayedAt: 0, total: 2, ConsumptionOrder.Episodic);
        Assert.Equal(ShowVisitKind.New, ShowVisit.Of(ledger.Played + ledger.InProgress > 0, ledger.ToGo, ledger.InProgress));
    }
}
