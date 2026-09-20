// ── Wavee.Tests/BrowseLoadGateTests.cs — Browse's load-boundary rule (Entities/Browse.Page.cs) ─────────────────────
//
// The bug this pins: the Browse directory, a category page and the Charts band each read Pending/Ready/Failed off
// their own ad hoc `if` before this rule existed, and their Retry vacancies called `Entities.Ensure` — which is a
// no-op once the planner has already marked a row Asked-and-unanswered (P4's dedupe: `wanted & ~known & ~asked`
// never re-sends what is already sealed). The failure card was real, but pressing Retry did nothing: the row stayed
// exactly as failed as it was before the click. `BrowseLoadGate` is the truth table the pages now share, and the
// pages' Retry actions call `Entities.Refresh` (which un-asks first) instead of `Demand`'s `Ensure`.
//
// The rule itself has exactly two things to get right, both covered exhaustively below: a Known row is Ready
// WHATEVER it holds (an empty answer is an empty band, never a failure card), and Failed needs BOTH a demand and an
// empty in-flight count — a row that is merely still on the wire must keep shimmering, not flash an error.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class BrowseLoadGateTests
{
    static readonly bool[] Bools = [false, true];

    [Fact]
    public void Known_IsAlwaysReady_RegardlessOfDemandOrInflight()
    {
        // An empty answer is Known too (the caller folds "no items" into Known before asking the gate) — Ready covers
        // it exactly like a full one. Known outranks everything else in the table.
        foreach (bool demanded in Bools)
        foreach (bool inflight in Bools)
            Assert.Equal(BrowseLoad.Ready, BrowseLoadGate.Of(known: true, demanded, inflight));
    }

    [Fact]
    public void NeverDemanded_IsAlwaysPending_EvenIfSomehowInflight()
    {
        foreach (bool inflight in Bools)
            Assert.Equal(BrowseLoad.Pending, BrowseLoadGate.Of(known: false, demanded: false, inflight));
    }

    [Fact]
    public void DemandedAndStillInflight_IsPending_NotFailed()
        // Still on the wire: this must keep shimmering, never flash the error card while an answer is on its way.
        => Assert.Equal(BrowseLoad.Pending, BrowseLoadGate.Of(known: false, demanded: true, inflight: true));

    [Fact]
    public void DemandedAndNothingInflight_IsFailed()
        // Asked, nothing coming: the one shape that is a genuine failure, not a bare 0:00.
        => Assert.Equal(BrowseLoad.Failed, BrowseLoadGate.Of(known: false, demanded: true, inflight: false));

    [Fact]
    public void TheTruthTableIsExhaustive()
    {
        int seen = 0;
        foreach (bool known in Bools)
        foreach (bool demanded in Bools)
        foreach (bool inflight in Bools)
        {
            BrowseLoad expected = known ? BrowseLoad.Ready : demanded && !inflight ? BrowseLoad.Failed : BrowseLoad.Pending;
            Assert.Equal(expected, BrowseLoadGate.Of(known, demanded, inflight));
            seen++;
        }
        Assert.Equal(8, seen);
    }

    [Theory]
    [InlineData(true, false, false, BrowseLoad.Ready)]
    [InlineData(true, true, true, BrowseLoad.Ready)]
    [InlineData(false, false, false, BrowseLoad.Pending)]
    [InlineData(false, true, true, BrowseLoad.Pending)]
    [InlineData(false, true, false, BrowseLoad.Failed)]
    public void NamedScenarios(bool known, bool demanded, bool inflight, BrowseLoad expected)
        => Assert.Equal(expected, BrowseLoadGate.Of(known, demanded, inflight));
}
