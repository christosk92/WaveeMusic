// ── Wavee.Tests/ArtistReadinessTests.cs — what the artist page may paint yet (ch 08 §7) ──────────────────────────────
//
// The readiness column of ch 08 §7, as facts: the overview is a unit, the chart is gated on the chart transport having
// ANSWERED plus a complete edge plus every row's list fields; play counts fill after reveal, and a shelf is the edge's
// readiness with Complete-and-empty meaning "absent", never "loading".

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ArtistReadinessRuleTests
{
    [Theory]
    [InlineData(false, EdgeState.Complete, false)]
    [InlineData(true, EdgeState.Unknown, false)]
    [InlineData(true, EdgeState.Partial, false)]
    [InlineData(true, EdgeState.Complete, true)]
    public void The_chart_needs_the_answer_AND_a_complete_edge(bool knowsChart, EdgeState state, bool expected)
        => Assert.Equal(expected, ArtistReadiness.Chart(knowsChart, state));

    [Fact]
    public void One_row_without_its_play_count_does_not_hold_back_the_chart()
    {
        uint row = (uint)TrackFields.Row;
        Assert.True(ArtistReadiness.Chart(true, EdgeState.Complete, [row, row, row]));
        Assert.True(ArtistReadiness.Chart(true, EdgeState.Complete, [row, row, row]));
        Assert.False(ArtistReadiness.Chart(true, EdgeState.Complete, [row, row & ~(uint)TrackFields.Image, row]));
        Assert.True(ArtistReadiness.Chart(true, EdgeState.Complete, []));   // answered and genuinely empty
    }

    [Theory]
    [InlineData(EdgeState.Unknown, 0, true)]
    [InlineData(EdgeState.Failed, 0, true)]
    [InlineData(EdgeState.Partial, 3, true)]
    [InlineData(EdgeState.Complete, 4, true)]
    [InlineData(EdgeState.Complete, 0, false)]
    public void A_shelf_is_absent_only_when_complete_and_empty(EdgeState readiness, int count, bool present)
        => Assert.Equal(present, ArtistReadiness.ShelfPresent(readiness, count));

    // ── the chart's FAILURE gate (2026-09-16: the shimmer that never became a Retry) ────────────────────────────────
    //
    // Rows have no Failed state; the chart derives one from the four marks the way Search does: known ⇒ fine, asked and
    // in flight ⇒ pending, asked with nothing in flight ⇒ concluded without it, not asked (after we asked) ⇒ un-asked by
    // a failure. Any of the three things the chart waits on concluding without its data fails the chart.

    const uint Chart = (uint)ArtistFields.Chart;
    const uint Row = (uint)TrackFields.Row;
    const uint Stamp = 1u;                                          // Fetch.Stamp(epoch 0)

    [Fact]
    public void ChartFailed_when_the_popular_edge_failed()
    {
        Assert.True(ArtistReadiness.ChartFailed(true, Chart, Stamp, EdgeState.Failed, [], [], []));
        Assert.True(ArtistReadiness.ChartFailed(false, Chart, Stamp, EdgeState.Failed, [], [], []));
        Assert.False(ArtistReadiness.ChartFailed(true, Chart, 0, EdgeState.Complete, [], [], []));   // known and complete
    }

    [Fact]
    public void ChartFailed_when_the_chart_group_was_un_asked_after_the_batch_concluded()
    {
        // The defect's exact shape: overview 200, top tracks 401 ⇒ the answer un-asks Chart and clears the in-flight
        // mark. Not known, not asked, nothing out: nothing is coming.
        Assert.True(ArtistReadiness.ChartFailed(false, 0, 0, EdgeState.Complete, [], [], []));
        // The 404 shape: the batch settled with Chart still asked and nothing in flight — concluded without it.
        Assert.True(ArtistReadiness.ChartFailed(false, Chart, 0, EdgeState.Complete, [], [], []));
        // The edge still Unknown changes neither verdict: the chart bit alone is past hope.
        Assert.True(ArtistReadiness.ChartFailed(false, 0, 0, EdgeState.Unknown, [], [], []));
    }

    [Fact]
    public void ChartFailed_when_a_target_concluded_without_its_row()
    {
        uint identity = (uint)TrackFields.Identity;
        // One target has its row; the other was asked, its request settled, and Row is still short of PlayCount.
        Assert.True(ArtistReadiness.ChartFailed(true, Chart, 0, EdgeState.Complete, [Row, identity], [Row, Row], [0, 0]));
        // A target un-asked by a terminal failure (asked bits gone) concludes the same way.
        Assert.True(ArtistReadiness.ChartFailed(true, Chart, 0, EdgeState.Complete, [Row, identity], [Row, 0], [0, 0]));
        // Every target knowing its row is not a failure, whatever the marks say about groups the chart does not paint.
        Assert.False(ArtistReadiness.ChartFailed(true, Chart, 0, EdgeState.Complete, [Row, Row], [0, 0], [0, 0]));
    }

    [Fact]
    public void ChartFailed_stays_pending_while_something_is_in_flight()
    {
        uint identity = (uint)TrackFields.Identity;
        // The chart bit is asked and the artist's request is out.
        Assert.False(ArtistReadiness.ChartFailed(false, Chart, Stamp, EdgeState.Complete, [], [], []));
        // A target's missing groups are asked and its request is out.
        Assert.False(ArtistReadiness.ChartFailed(true, Chart, 0, EdgeState.Complete, [identity], [Row], [Stamp]));
        // In flight for the row but a missing bit nobody asks for any more: that bit is not coming.
        Assert.True(ArtistReadiness.ChartFailed(true, Chart, 0, EdgeState.Complete, [identity], [identity], [Stamp]));
        // A Partial edge with everything asked and out is loading, not failed.
        Assert.False(ArtistReadiness.ChartFailed(false, Chart, Stamp, EdgeState.Partial, [identity], [Row], [Stamp]));
    }
}

[Collection(EntitiesCollection.Name)]
public class ArtistReadinessTests
{
    const string Uri = "spotify:artist:readiness";

    static Artist ArtistOf() => Entities.Artist(EntityUri.Parse(Uri.AsSpan()));

    [Fact]
    public void The_overview_is_ready_only_when_every_group_is_known()
    {
        TestScope.Fresh();
        var a = ArtistOf();
        Assert.False(ArtistReadiness.Overview(a));

        var s = Staging.Rent();
        ref var row = ref s.Artists.RowFor(new StagedId(s.Text(Uri)), Authority.Full,
            (uint)(ArtistFields.Overview & ~ArtistFields.Tour));
        row.Name = s.Text("Readiness");
        TestScope.CommitAndPublish(s);
        Assert.False(ArtistReadiness.Overview(a));                           // one group short is not a unit

        var tour = Staging.Rent();
        tour.Artists.RowFor(new StagedId(tour.Text(Uri)), Authority.Full, (uint)ArtistFields.Tour);
        TestScope.CommitAndPublish(tour);
        Assert.True(ArtistReadiness.Overview(a));
    }

    [Fact]
    public void The_chart_waits_for_every_rows_list_fields_on_the_live_tables()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var artist = new StagedId(s.Text(Uri));
        int mark = s.PopularMark;
        s.PopularTracks.Add() = new StagedId(s.Text("spotify:track:ready-1"));
        s.PopularTracks.Add() = new StagedId(s.Text("spotify:track:ready-2"));
        s.EndPopular(artist, mark, extension: true);
        s.Artists.RowFor(artist, Authority.Full, (uint)ArtistFields.Chart);
        ref var one = ref s.Tracks.RowFor(new StagedId(s.Text("spotify:track:ready-1")), Authority.Full, (uint)TrackFields.Row);
        one.Title = s.Text("One");
        one.PlayCount = 10;
        TestScope.CommitAndPublish(s);

        var a = ArtistOf();
        Assert.False(ArtistReadiness.Chart(a));                              // ready-2 has no list fields yet

        var more = Staging.Rent();
        ref var two = ref more.Tracks.RowFor(new StagedId(more.Text("spotify:track:ready-2")), Authority.Full, (uint)TrackFields.Row);
        two.Title = more.Text("Two");
        two.PlayCount = 5;
        TestScope.CommitAndPublish(more);
        Assert.True(ArtistReadiness.Chart(a));
    }

    [Fact]
    public void A_failed_shelf_reads_failed_and_an_answered_empty_one_reads_complete()
    {
        TestScope.Fresh();
        var edges = Entities.Current.Edges;
        int a = ArtistOf().Slot;
        edges.ArtistRelated.MarkFailed(a, 0, 503);
        Assert.Equal(EdgeState.Failed, ArtistReadiness.Shelf(edges.ArtistRelated, a));

        edges.ArtistRelated.ReplaceRun(a, [], default);
        Assert.Equal(EdgeState.Complete, ArtistReadiness.Shelf(edges.ArtistRelated, a));
    }
}
