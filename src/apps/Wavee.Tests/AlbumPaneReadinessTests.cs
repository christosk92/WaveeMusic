// ── Wavee.Tests/AlbumPaneReadinessTests.cs — the album pane's readiness rule (Entities/User.cs §9b) ───────────────────
//
// 0.2.10's defect A was "skeleton rows forever": a pane whose only two states were "ready" and "not ready" cannot tell a
// list that is still on the wire from one whose ask died, so it shimmers until the user navigates away. The rework's pane
// has FOUR states and they are a pure rule over the album's identity bit, its tracks edge, whether that edge failed, and
// the two ROW facts (`AlbumRowFacts`), so the decision is a test rather than a per-render probe.
//
// The 2026-09-18 correction is the second half of that defect. The row fact used to be one flag, "unnamed", that mixed
// "this row has no title" with "a credited artist of this row has no name" — and NOBODY demands `ArtistFields.Name` for a
// track's credits, so the second half can never land and the pane shimmered forever on a perfectly renderable tracklist.
// Only the TITLE gates now; the unnamed credit is a notice over a rendered list (`Detail.NoticeRules`).
//
// Pinned here: the whole state table exhaustively, the named cases the defect is about, and the counted shimmer's floor.

using Xunit;

namespace Wavee.Tests;

public class AlbumPaneReadinessTests
{
    /// <summary>The RICH rule: the untitled gate is live.</summary>
    static AlbumPaneState Of(bool id, EdgeState tracks, bool edgeFailed, bool untitled, bool rowsFailed)
        => AlbumPaneReadiness.Of(id, tracks, edgeFailed, new AlbumRowFacts(untitled, rowsFailed));

    // ── the four states, one named fact each ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoIdentityYet_IsTheHeaderSkeleton()
    {
        // The navigator has already demanded identity for every saved row, so this is short-lived — and it outranks
        // everything below it: there is no title to draw a failure strip under.
        Assert.Equal(AlbumPaneState.Header, Of(false, EdgeState.Unknown, false, false, false));
        Assert.Equal(AlbumPaneState.Header, Of(false, EdgeState.Complete, true, true, true));
    }

    [Fact]
    public void IdentityKnown_ButTheListIsStillComing_IsTheCountedShimmer()
    {
        Assert.Equal(AlbumPaneState.Rows, Of(true, EdgeState.Unknown, false, false, false));
        Assert.Equal(AlbumPaneState.Rows, Of(true, EdgeState.Partial, false, true, false));
    }

    [Fact]
    public void AFailedTracksEdge_IsTheRetryStrip()
    {
        Assert.Equal(AlbumPaneState.Failed, Of(true, EdgeState.Unknown, true, false, false));
        Assert.Equal(AlbumPaneState.Failed, Of(true, EdgeState.Partial, true, true, false));
    }

    [Fact]
    public void ACompleteEdgeWhoseEveryRowIsTitled_IsReady()
    {
        Assert.Equal(AlbumPaneState.Ready, Of(true, EdgeState.Complete, false, false, false));
        // rowsFailed is stale news once every row carries its title (an earlier batch died, a later one answered).
        Assert.Equal(AlbumPaneState.Ready, Of(true, EdgeState.Complete, false, false, true));
    }

    [Fact]
    public void ACompleteEdgeWithAnUntitledRow_IsTheShimmerUntilThatRowBatchFails()
    {
        // THIS is defect A's row, and the pair is the whole point of the `Failed` column: without it both of these read
        // the same and the pane shimmers forever. There is no "minified album" notice on this surface — an untitled row
        // is either still loading or a Retry.
        Assert.Equal(AlbumPaneState.Rows, Of(true, EdgeState.Complete, false, true, false));
        Assert.Equal(AlbumPaneState.Failed, Of(true, EdgeState.Complete, false, true, true));
    }

    [Fact]
    public void ARowBatchFailureBeforeTheListIsComplete_DoesNotStrandThePane()
    {
        // The edge is still arriving, so the rows the failed batch carried may yet be re-asked with the next page: the
        // pane keeps shimmering rather than offering a Retry for a list that is not finished being listed.
        Assert.Equal(AlbumPaneState.Rows, Of(true, EdgeState.Unknown, false, true, true));
        Assert.Equal(AlbumPaneState.Rows, Of(true, EdgeState.Partial, false, true, true));
    }

    [Fact]
    public void TheEdgeStateValueIsNeverTheFailureItself_TheReadinessBoolIs()
    {
        // `EdgeState.Failed` is never stored (only `EdgeTableBase.Readiness` answers it), so the pane passes it as the
        // separate bool. A caller that forgot the bool gets a shimmer, not a wrong "ready".
        Assert.Equal(AlbumPaneState.Failed, Of(true, EdgeState.Failed, true, false, false));
        Assert.Equal(AlbumPaneState.Rows, Of(true, EdgeState.Failed, false, false, false));
    }

    // ── the whole table, exhaustively ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheStateTableIsExhaustive_AndNoInputCombinationIsUndecided()
    {
        // 2 × 4 × 2 × 2 × 2 = 64 combinations. The expectation is written as the SENTENCE the pane's summary makes —
        // "no identity is a header; a failed edge is a failure; an incomplete edge is rows; a complete edge is ready
        // unless a row is untitled, and then it is a failure only if that row's batch failed" — so a reordering of the
        // rule's guards (the bug that would strand the pane) shows up here as a mismatch.
        EdgeState[] states = [EdgeState.Unknown, EdgeState.Partial, EdgeState.Complete, EdgeState.Failed];
        int seen = 0;
        foreach (bool id in Bools)
        foreach (var tracks in states)
        foreach (bool edgeFailed in Bools)
        foreach (bool untitled in Bools)
        foreach (bool rowsFailed in Bools)
        {
            AlbumPaneState expected =
                !id ? AlbumPaneState.Header
                : edgeFailed ? AlbumPaneState.Failed
                : tracks != EdgeState.Complete ? AlbumPaneState.Rows
                : !untitled ? AlbumPaneState.Ready
                : rowsFailed ? AlbumPaneState.Failed
                : AlbumPaneState.Rows;

            Assert.Equal(expected, Of(id, tracks, edgeFailed, untitled, rowsFailed));
            seen++;
        }
        Assert.Equal(64, seen);
    }

    static readonly bool[] Bools = [false, true];

    [Fact]
    public void EveryStateIsReachable()
    {
        // A state nothing can produce is a state the pane renders for nobody — and an unreachable `Failed` is exactly
        // what ch 15 W24 shipped.
        var reached = new HashSet<AlbumPaneState>();
        EdgeState[] states = [EdgeState.Unknown, EdgeState.Partial, EdgeState.Complete, EdgeState.Failed];
        foreach (bool id in Bools)
        foreach (var tracks in states)
        foreach (bool edgeFailed in Bools)
        foreach (bool untitled in Bools)
        foreach (bool rowsFailed in Bools)
            reached.Add(Of(id, tracks, edgeFailed, untitled, rowsFailed));

        Assert.Equal(4, reached.Count);
        Assert.Contains(AlbumPaneState.Header, reached);
        Assert.Contains(AlbumPaneState.Rows, reached);
        Assert.Contains(AlbumPaneState.Failed, reached);
        Assert.Contains(AlbumPaneState.Ready, reached);
    }

    // ── the counted shimmer ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 13, 13, 13)]      // the album knows its count: shimmer exactly that many rows
    [InlineData(true, 13, 0, 13)]       // …even before the edge has listed anything
    [InlineData(false, 13, 9, 9)]       // the count is not known: the listed edge length is the honest guess
    [InlineData(true, 0, 9, 9)]         // a KNOWN count of 0 is not an answer for a shimmer — fall through
    [InlineData(false, 0, 0, 6)]        // neither has answered: the default block
    [InlineData(true, 0, 0, 6)]
    [InlineData(false, 99, 0, 6)]       // a track count nobody has confirmed is not read
    [InlineData(true, -1, 0, 6)]        // a nonsense count never becomes a negative array length
    public void ShimmerRows_IsCountedAndNeverZero(bool knowsCount, int trackCount, int listed, int expected)
        => Assert.Equal(expected, AlbumPaneReadiness.ShimmerRows(knowsCount, trackCount, listed));

    [Fact]
    public void ShimmerRows_IsAtLeastOne_ForEveryInputThePaneCanPass()
    {
        // A zero-row shimmer is a blank pane, and a blank pane reads as an empty album rather than as loading.
        for (int count = -2; count <= 40; count++)
        for (int listed = 0; listed <= 40; listed++)
        {
            Assert.True(AlbumPaneReadiness.ShimmerRows(true, count, listed) >= 1);
            Assert.True(AlbumPaneReadiness.ShimmerRows(false, count, listed) >= 1);
        }
    }
}
