// ── Wavee.Tests/ShowLedgerTests.cs — the rail's played ledger and "new since you were here" (podcast plan §5.2, §10) ──
//
// Counts are over the RESIDENT episodes (newest first) with the status filter's own predicates; the one extrapolation is
// the episodic tail rule, and a serial never extrapolates. Fresh = unplayed and published strictly after the show's last
// play. Dates are unix seconds; small literals are enough, the rule only compares them.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ShowLedgerTests
{
    // ── the counts ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Counts_PartitionTheResidentEpisodes_WithTheFiltersThresholds()
    {
        var l = ShowLedger.Of([1f, 0.98f, 0.5f, 0.011f, 0.01f, 0f], [60, 50, 40, 30, 20, 10], lastPlayedAt: 0, total: 6,
            ConsumptionOrder.Sequential);
        Assert.Equal(new ShowLedger(Played: 1, InProgress: 3, ToGo: 2, Fresh: 0), l);
    }

    [Fact]
    public void Empty_IsAllZero_EvenWithATotal()
    {
        var none = new ShowLedger(0, 0, 0, 0);
        Assert.Equal(none, ShowLedger.Of([], [], lastPlayedAt: 100, total: 0, ConsumptionOrder.Episodic));
        Assert.Equal(none, ShowLedger.Of([], [], lastPlayedAt: 100, total: 40, ConsumptionOrder.Episodic));
    }

    // ── fresh ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fresh_IsUnplayed_AndPublishedStrictlyAfterTheLastPlay()
    {
        // newest first: two new ones after the last play (500), one started after it, one published AT it, older ones.
        var l = ShowLedger.Of([0f, 0f, 0.3f, 0f, 0f, 1f], [700, 600, 550, 500, 400, 300], lastPlayedAt: 500, total: 6,
            ConsumptionOrder.Episodic);
        Assert.Equal(2, l.Fresh);                                   // 700 and 600 — not the started 550, not 500 itself
        Assert.Equal(4, l.ToGo);                                    // Fresh is a subset of ToGo
    }

    [Fact]
    public void Fresh_NeverPlayedShow_HasNoSince()
        => Assert.Equal(0, ShowLedger.Of([0f, 0f], [700, 600], lastPlayedAt: 0, total: 2, ConsumptionOrder.Episodic).Fresh);

    [Theory]
    [InlineData(0f, 600, 500, true)]
    [InlineData(0.01f, 600, 500, true)]                             // at the floor is still unplayed
    [InlineData(0.02f, 600, 500, false)]                            // started is not news
    [InlineData(1f, 600, 500, false)]
    [InlineData(0f, 500, 500, false)]                               // strictly after
    [InlineData(0f, 0, 500, false)]                                 // undated
    [InlineData(0f, 600, 0, false)]                                 // never played
    public void IsFresh_IsTheOnePredicate(float pct, int publishedAt, int lastPlayedAt, bool expected)
        => Assert.Equal(expected, ShowLedger.IsFresh(pct, publishedAt, lastPlayedAt));

    [Fact]
    public void LastPlayed_IsTheNewestPlayedAt_ZeroWhenNone()
    {
        Assert.Equal(900, ShowLedger.LastPlayed([0, 300, 900, 120]));
        Assert.Equal(0, ShowLedger.LastPlayed([0, 0]));
        Assert.Equal(0, ShowLedger.LastPlayed([]));
    }

    [Fact]
    public void Fresh_ToleratesDatesShorterThanThePcts()
        => Assert.Equal(1, ShowLedger.Of([0f, 0f, 0f], [900], lastPlayedAt: 500, total: 3, ConsumptionOrder.Episodic).Fresh);

    // ── the tail rule ─────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly float[] HeadOfALongShow = [0f, 0.5f, 1f, 1f, 1f];
    static readonly int[] HeadDates = [500, 400, 300, 200, 100];

    [Fact]
    public void Serial_NeverExtrapolates()
    {
        var l = ShowLedger.Of(HeadOfALongShow, HeadDates, lastPlayedAt: 350, total: 50, ConsumptionOrder.Sequential);
        Assert.Equal(new ShowLedger(3, 1, 1, 1), l);
    }

    [Theory]
    [InlineData(ConsumptionOrder.Episodic)]
    [InlineData(ConsumptionOrder.Recent)]
    public void Episodic_PlayedUpToThePagingBoundary_AssumesTheOlderTailPlayed(ConsumptionOrder order)
    {
        var l = ShowLedger.Of(HeadOfALongShow, HeadDates, lastPlayedAt: 350, total: 50, order);
        Assert.Equal(new ShowLedger(Played: 3 + 45, InProgress: 1, ToGo: 1, Fresh: 1), l);
    }

    [Fact]
    public void UnknownOrder_DoesNotExtrapolate()
        => Assert.Equal(3, ShowLedger.Of(HeadOfALongShow, HeadDates, 350, 50, ConsumptionOrder.Unknown).Played);

    [Fact]
    public void Episodic_OldestResidentUnplayed_DoesNotExtrapolate()
    {
        // The listener joined inside the loaded window: nothing says they went further back.
        var l = ShowLedger.Of([1f, 1f, 0f], [300, 200, 100], lastPlayedAt: 300, total: 40, ConsumptionOrder.Episodic);
        Assert.Equal(2, l.Played);
        Assert.False(ShowLedger.TailAssumedPlayed([1f, 1f, 0f], [300, 200, 100], 40, ConsumptionOrder.Episodic));
    }

    [Fact]
    public void Episodic_TheLastResidentIsNotTheOldest_DoesNotExtrapolate()
    {
        // Not a newest-first prefix (a view, or an edge out of date order): the tail cannot be proven older.
        Assert.False(ShowLedger.TailAssumedPlayed([1f, 1f, 1f], [100, 300, 200], 40, ConsumptionOrder.Episodic));
        Assert.False(ShowLedger.TailAssumedPlayed([1f, 1f, 1f], [300, 0, 200], 40, ConsumptionOrder.Episodic));   // an undated one
        Assert.False(ShowLedger.TailAssumedPlayed([1f, 1f, 1f], [300, 200, 0], 40, ConsumptionOrder.Episodic));   // the oldest undated
        Assert.False(ShowLedger.TailAssumedPlayed([1f, 1f, 1f], [300, 200], 40, ConsumptionOrder.Episodic));      // not parallel
    }

    [Fact]
    public void Episodic_NothingUnloaded_NothingToExtrapolate()
    {
        Assert.False(ShowLedger.TailAssumedPlayed([1f, 1f], [200, 100], 2, ConsumptionOrder.Episodic));
        Assert.False(ShowLedger.TailAssumedPlayed([1f, 1f], [200, 100], 0, ConsumptionOrder.Episodic));   // total unknown
        Assert.Equal(2, ShowLedger.Of([1f, 1f], [200, 100], 0, 2, ConsumptionOrder.Episodic).Played);
    }

    [Fact]
    public void Episodic_EqualDatesAtTheBoundary_StillCountAsOldest()
        => Assert.True(ShowLedger.TailAssumedPlayed([1f, 1f], [100, 100], 9, ConsumptionOrder.Episodic));

    [Fact]
    public void CaughtUpEpisodic_LedgerReachesTheTotal()
    {
        var l = ShowLedger.Of([1f, 1f, 1f], [300, 200, 100], lastPlayedAt: 300, total: 61, ConsumptionOrder.Episodic);
        Assert.Equal(new ShowLedger(61, 0, 0, 0), l);
    }
}
