// ── Wavee.Tests/FetchOutstandingTests.cs — a row is in flight until its LAST request settles (the chart's Failed flash) ──
//
// THE DEFECT (the verify capture of Oscar Dunbar, spotify:artist:7h8ac6sOa2cH1sANLaRDz5, pid 14080): the artist page asks
// the ArtistPopular EDGE (ticket 21) and the artist ROW's remaining groups incl. Chart (ticket 25, need=0x3e0) at once.
// Ticket 21's answer carries the overview, so its commit writes the artist row through `Table.Applied` — which cleared
// the row's `Inflight` unconditionally while ticket 25 was still out. For ~250 ms the chart gate read "Chart asked, nothing
// in flight" and logged `artist.chart state=Failed reason=ChartNothingComing missing=0x200 asked=0x3ff inflight=0`, then
// ticket 25 answered and it went Ready. One `Inflight` stamp cannot say "one of my TWO requests settled".
//
// The fact: a row carries a count of the requests (bucket seats and in-flight row batches) that name it, and `Inflight`
// clears only when the last one settles — an answer landing through another door (an edge answer, a second batch) is not
// the end of the row's own request.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class FetchOutstandingTests : IDisposable
{
    const uint Chart = (uint)ArtistFields.Chart;
    const uint Identity = (uint)ArtistFields.Identity;
    readonly Action? _hostWake = Fetch.WakeForDrain;

    public FetchOutstandingTests()
    {
        Fetch.Reset();
        Fetch.WakeForDrain = null;
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Fetch.WakeForDrain = _hostWake;
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
    }

    static Scope Boot()
    {
        Entities.Boot(CatalogScope.Fake());
        return Entities.Current;
    }

    static int ArtistRow(Scope scope) => scope.Artists.Slot(("spotify:artist:" + Guid.NewGuid().ToString("n")).AsSpan());

    /// <summary>An answer that writes the artist's identity (what the overview carries) and nothing else.</summary>
    static void AnswerIdentity(uint ticket, ArtistTable table, int slot)
    {
        var s = Staging.Rent();
        ref var row = ref s.Artists.RowFor(table.Id[slot], Authority.Full, Identity);
        row.Name = s.Text("Oscar Dunbar");
        Fetch.Answer(ticket, s);
    }

    static void AnswerChart(uint ticket, ArtistTable table, int slot)
    {
        var s = Staging.Rent();
        s.Artists.RowFor(table.Id[slot], Authority.Full, Chart);
        Fetch.Answer(ticket, s);
    }

    static bool ChartNothingComing(ArtistTable t, int slot)
        => ArtistReadiness.ChartFailed(t.Knows(slot, Chart), t.Asked[slot], t.Inflight[slot], EdgeState.Complete, [], [], []);

    /// <summary>The owner's timeline: the edge answer lands the overview while the row's Chart batch is still out.</summary>
    [Fact]
    public void An_edge_answer_writing_the_row_does_not_end_the_rows_own_request()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        ArtistTable t = scope.Artists;
        int slot = ArtistRow(scope);

        Fetch.PlanEdge(scope, FetchEdge.ArtistPopular, [slot], 0, FetchPriority.Visible);
        Fetch.Plan(scope, t, [slot], Identity | Chart, FetchPriority.Visible);
        Fetch.Drain();
        Assert.Equal(2, provider.Seen.Count);
        uint edge = provider.Seen.First(b => b.Wanted == 0).Ticket;
        uint row = provider.Seen.First(b => b.Wanted != 0).Ticket;

        AnswerIdentity(edge, t, slot);                              // ticket 21: the overview, through Applied
        Assert.True(t.Knows(slot, Identity));
        Assert.NotEqual(0u, t.Inflight[slot]);                      // ticket 25 is still out
        Assert.False(ChartNothingComing(t, slot));                  // no Failed flash

        AnswerChart(row, t, slot);
        Assert.Equal(0u, t.Inflight[slot]);                         // the last request settled
        Assert.True(t.Knows(slot, Chart));
    }

    /// <summary>Two ROW batches for one row (two needs, two buckets): the first answer is not the second's.</summary>
    [Fact]
    public void The_first_of_two_row_batches_answering_leaves_the_row_in_flight()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        ArtistTable t = scope.Artists;
        int slot = ArtistRow(scope);

        Fetch.Plan(scope, t, [slot], Identity, FetchPriority.Visible);
        Fetch.Plan(scope, t, [slot], Chart, FetchPriority.Visible);
        Fetch.Drain();
        Assert.Equal(2, provider.Seen.Count);
        uint first = provider.Seen.First(b => b.Wanted == Identity).Ticket;
        uint second = provider.Seen.First(b => b.Wanted == Chart).Ticket;

        AnswerIdentity(first, t, slot);
        Assert.NotEqual(0u, t.Inflight[slot]);
        Assert.False(ChartNothingComing(t, slot));

        Fetch.Failed(second, 404, 0);                               // the chart's own request ended: now nothing is coming
        Assert.Equal(0u, t.Inflight[slot]);
        Assert.True(ChartNothingComing(t, slot));
    }

    /// <summary>A retryable failure re-queues the same rows: the row stays in flight through the backoff, and the retry's
    /// own settle is what clears it.</summary>
    [Fact]
    public void A_retry_keeps_the_row_in_flight_until_the_retry_settles()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        ArtistTable t = scope.Artists;
        int slot = ArtistRow(scope);

        Fetch.Plan(scope, t, [slot], Chart, FetchPriority.Visible);
        Fetch.Drain();
        Fetch.Failed(provider.Seen[0].Ticket, 503, 0);
        Assert.NotEqual(0u, t.Inflight[slot]);

        Entities.Now = 5;
        Fetch.Pump();
        Assert.Equal(2, provider.Seen.Count);
        AnswerChart(provider.Seen[1].Ticket, t, slot);
        Assert.Equal(0u, t.Inflight[slot]);
    }

    /// <summary>A write that is not the row's own answer, while nothing is out for the row, still clears the stamp —
    /// the disk leg's partial hit and every commit outside a request keep their meaning.</summary>
    [Fact]
    public void An_applied_write_with_nothing_outstanding_clears_the_stamp()
    {
        Scope scope = Boot();
        ArtistTable t = scope.Artists;
        int slot = ArtistRow(scope);
        t.Inflight[slot] = Fetch.Stamp(scope.Epoch);
        t.Applied(slot, Identity, Authority.Full, ref t.IdentityAuthority);
        Assert.Equal(0u, t.Inflight[slot]);
    }
}
