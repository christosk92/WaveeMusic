// ── Wavee.Tests/FetchDemandDedupeTests.cs — one slot, one seat per request; a queued retry is "coming" (RCA D, fixes 5+6) ──
//
// THE DEFECT (the owner's log, ticket 74: `rows=22` for EIGHT tracks, and three `fetch.miss` lines per track at one
// timestamp — attempts 1, 2, 3 — sealing every one of them in a single answer). Three doors put the same slot into the
// same (provider, subject, kind, need) bucket: the miss retry (`ReviewMisses`), the chart's `DemandTargets` Ensure — which
// asked again because the retry had UN-ASKED the row — and the Retry vacancy's `Refresh`. `Demand.Add` had no dedupe,
// and `ReviewMisses` counted a miss per ROW OCCURRENCE, so one omitted entity burned the whole retry budget at once.
//
// The facts: a slot joins a bucket once, whichever doors ask; a miss is counted once per slot per answer; and a retry
// queued after a miss reads as SOMETHING COMING (asked + in flight) — never un-asked — so a page's next Ensure dedupes
// against it instead of re-adding it, and a readiness gate reading "asked, nothing in flight" does not call it a failure.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class FetchDemandDedupeTests : IDisposable
{
    const uint Identity = (uint)TrackFields.Identity;
    readonly Action? _hostWake = Fetch.WakeForDrain;

    public FetchDemandDedupeTests()
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

    static int[] Rows(Table table, int count)
    {
        var slots = new int[count];
        for (int i = 0; i < count; i++) slots[i] = table.Slot(("spotify:track:" + Guid.NewGuid().ToString("n")).AsSpan());
        return slots;
    }

    static void AnswerOnly(uint ticket, TrackTable table, int keep)
    {
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(table.Id[keep], Authority.Full, Identity);
        row.Title = s.Text("kept");
        Fetch.Answer(ticket, s);
    }

    static int MissLines(uint ticket)
    {
        string t = ticket.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int n = 0;
        foreach (WaveeLogEntry e in Log.Snapshot())
        {
            if (e.EventId is not ("fetch.miss" or "fetch.seal") || e.Fields is not { } fields) continue;
            foreach (WaveeLogField f in fields)
                if (f.Name == "ticket" && f.Value == t && e.EventId == "fetch.miss") { n++; break; }
        }
        return n;
    }

    /// <summary>The Retry vacancy's Refresh arriving while the page's own ask is still bucketed: ONE seat per slot.</summary>
    [Fact]
    public void A_refresh_of_rows_already_waiting_does_not_seat_them_twice()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 8);

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Refresh(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();

        var batch = Assert.Single(provider.Seen);
        Assert.Equal(8, batch.Count);                                // 16 before the fix
    }

    /// <summary>The page's Ensure landing while a miss's retry is queued finds the row still ASKED and asks nothing.</summary>
    [Fact]
    public void An_ensure_during_a_queued_miss_retry_does_not_seat_the_slot_again()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 2);
        int target = slots[0];

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();
        AnswerOnly(provider.Seen[0].Ticket, t, slots[1]);          // target missed → retry queued (backoff 1 s)

        // A queued retry is something coming: asked AND in flight (fix 6).
        Assert.Equal(Identity, t.Asked[target] & Identity);
        Assert.NotEqual(0u, t.Inflight[target]);

        Fetch.Plan(scope, t, [target], Identity, FetchPriority.Visible);   // the chart's DemandTargets, re-running
        Entities.Now = 1;
        Fetch.Pump();
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(1, provider.Seen[1].Count);                     // 2 before the fix
    }

    /// <summary>However a slot came to be seated twice, ONE answer that omits it is ONE miss — never a whole retry budget
    /// spent in a single settle (the log's attempts 1, 2, 3 at one timestamp).</summary>
    [Fact]
    public void One_answer_counts_one_miss_per_slot()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 2);
        int target = slots[0];

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Refresh(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Refresh(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();
        uint ticket = provider.Seen[0].Ticket;
        AnswerOnly(ticket, t, slots[1]);

        Assert.Equal(1, MissLines(ticket));
        Assert.False(t.IsFailed(target, Identity));                  // one miss: nowhere near the seal
    }
}
