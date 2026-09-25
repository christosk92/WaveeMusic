// ── Wavee.Tests/ArtistChartGateTests.cs — the chart gate waits for what a row PAINTS, and says why (RCA 2026-09-25 A) ──
//
// THE DEFECT (the owner's log, artist spotify:artist:7h8ac6sOa2cH1sANLaRDz5, navId=14): one charted track (slot 8682)
// never got a play count — kind 185 answered 200 without it — and the Top tracks card turned into "Something went wrong".
// The gate read `TrackFields.Row` (Identity | PlayCount | Availability) for every target, and its failure twin read
// "PlayCount missing, asked, nothing in flight" as "nothing is coming". Its own doc says the opposite: TrackV4 carries
// what a chart row paints, and play counts are ALLOWED to fill into an already visible row.
//
// Pure facts over the marks (`ArtistReadiness`'s pure overloads); nothing reads production source.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ArtistChartGateTests
{
    const uint Chart = (uint)ArtistFields.Chart;
    const uint Row = (uint)TrackFields.Row;
    const uint Stamp = 1u;                                            // Fetch.Stamp(epoch 0)
    const uint Face = (uint)TrackFields.Face;
    const uint Availability = (uint)TrackFields.Availability;
    const uint Identity = (uint)TrackFields.Identity;

    // ── failing first: the gate (fix 2) ────────────────────────────────────────────────────────────────────────────

    /// <summary>A row that knows its face and its availability verdict paints; its play count fills in later.</summary>
    [Fact]
    public void A_row_without_its_play_count_does_not_hold_back_the_chart()
    {
        Assert.True(ArtistReadiness.Chart(true, EdgeState.Complete, [Row, Face | Availability, Row]));
        // A disk-restored row withholds Artists (TrackShape.PersistedIdentity) and still paints.
        Assert.True(ArtistReadiness.Chart(true, EdgeState.Complete, [Face | Availability]));
    }

    /// <summary>The owner's slot 8682: known face + availability, PlayCount asked and its batch settled without it. That
    /// is a row with no play count, not a chart that failed.</summary>
    [Fact]
    public void A_settled_ask_that_left_only_the_play_count_missing_is_not_a_failure()
        => Assert.False(ArtistReadiness.ChartFailed(true, Chart, 0, EdgeState.Complete,
            [Row, Identity | Availability], [Row, Row], [0, 0]));

    // ── what must hold before AND after the fix ────────────────────────────────────────────────────────────────────

    /// <summary>A row still missing part of what it paints (its title) is exactly as blocking as before.</summary>
    [Fact]
    public void A_row_missing_its_face_still_holds_the_chart()
    {
        uint noTitle = Row & ~(uint)TrackFields.Title;
        Assert.False(ArtistReadiness.Chart(true, EdgeState.Complete, [Row, noTitle]));
        Assert.True(ArtistReadiness.ChartFailed(true, Chart, 0, EdgeState.Complete, [Row, noTitle], [Row, Row], [0, 0]));
        Assert.False(ArtistReadiness.ChartFailed(true, Chart, 0, EdgeState.Complete, [Row, noTitle], [Row, Row], [0, Stamp]));
    }

    /// <summary>The evidence line's explainer names the deciding row and its marks, in the order the gate tests them.</summary>
    [Fact]
    public void Explain_names_the_deciding_row_and_its_marks()
    {
        uint noTitle = Row & ~(uint)TrackFields.Title;

        var failed = ArtistReadiness.Explain(true, Chart, 0, EdgeState.Complete, EdgeState.Complete,
            [Row, noTitle, noTitle], [Row, Row, Row], [0, 0, 0]);
        Assert.Equal(ArtistReadiness.ChartGateReason.TargetNothingComing, failed.Reason);
        Assert.Equal(1, failed.Index);
        Assert.Equal((uint)TrackFields.Title, failed.Missing & (uint)TrackFields.Title);
        Assert.Equal(Row, failed.Asked);
        Assert.Equal(0u, failed.Inflight);

        var pending = ArtistReadiness.Explain(true, Chart, 0, EdgeState.Complete, EdgeState.Complete,
            [Row, noTitle], [Row, Row], [0, Stamp]);
        Assert.Equal(ArtistReadiness.ChartGateReason.TargetPending, pending.Reason);
        Assert.Equal(1, pending.Index);

        Assert.Equal(ArtistReadiness.ChartGateReason.EdgeFailed,
            ArtistReadiness.Explain(true, Chart, 0, EdgeState.Unknown, EdgeState.Failed, [], [], []).Reason);
        Assert.Equal(ArtistReadiness.ChartGateReason.ChartNothingComing,
            ArtistReadiness.Explain(false, 0, 0, EdgeState.Complete, EdgeState.Complete, [], [], []).Reason);
        Assert.Equal(ArtistReadiness.ChartGateReason.ChartPending,
            ArtistReadiness.Explain(false, Chart, Stamp, EdgeState.Complete, EdgeState.Complete, [], [], []).Reason);
        Assert.Equal(ArtistReadiness.ChartGateReason.EdgePending,
            ArtistReadiness.Explain(true, Chart, 0, EdgeState.Partial, EdgeState.Partial, [], [], []).Reason);
        Assert.Equal(ArtistReadiness.ChartGateReason.Ready,
            ArtistReadiness.Explain(true, Chart, 0, EdgeState.Complete, EdgeState.Complete, [Row, Row], [0, 0], [0, 0]).Reason);
    }

    /// <summary>The explainer and the gate read the SAME group: whatever the explainer calls Ready the gate reveals.</summary>
    [Fact]
    public void Explain_and_the_gate_agree_on_ready()
    {
        uint[][] shapes = [[Row, Row], [Face | Availability], [Identity], [Row & ~(uint)TrackFields.Image]];
        foreach (uint[] known in shapes)
        {
            bool gate = ArtistReadiness.Chart(true, EdgeState.Complete, known);
            var trace = ArtistReadiness.Explain(true, Chart, 0, EdgeState.Complete, EdgeState.Complete, known,
                new uint[known.Length], new uint[known.Length]);
            Assert.Equal(gate, trace.Reason == ArtistReadiness.ChartGateReason.Ready);
        }
    }
}
