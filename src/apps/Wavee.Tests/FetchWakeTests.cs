// ── Wavee.Tests/FetchWakeTests.cs — a DUE backoff leaves on the next tick (RCA 2026-09-25 C, fix 4) ─────────────────
//
// THE DEFECT (the owner's log, ticket 74: `waitedMs=12315` behind a 1 s backoff). A backed-off bucket carries
// `ReadyAt` in app SECONDS. `NextWakeAt` counted only buckets with `ReadyAt > now`, so the moment the frame tick moved
// `Entities.Now` onto the deadline second the bucket became invisible: the idle wake was re-armed to "nothing"
// (`int.MaxValue` — the timer cancelled) and `Drain` pumps only when a drain is OWED, which a retry bucket never owes.
// The bucket then sat due until something unrelated pumped — here the user's Retry click, twelve seconds later.
//
// The facts, over the real planner and a recording transport (the host's per-tick call is `Fetch.Drain()`, its idle
// wake is `Fetch.NextWakeAt()`): a due bucket wakes the host NOW and leaves on the next tick's drain — for a miss retry
// and for a transport retry alike; a bucket before its deadline still arms the deadline; a bucket the online gate holds
// is still not a deadline (it waits for the session, and a per-tick wake would be a busy loop).

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class FetchWakeTests : IDisposable
{
    const uint Identity = (uint)TrackFields.Identity;
    readonly Action? _hostWake = Fetch.WakeForDrain;

    public FetchWakeTests()
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

    /// <summary>Answer naming only <paramref name="keep"/>: every other row of the batch is an omitted entity (a miss).</summary>
    static void AnswerOnly(uint ticket, TrackTable table, int keep)
    {
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(table.Id[keep], Authority.Full, Identity);
        row.Title = s.Text("kept");
        Fetch.Answer(ticket, s);
    }

    /// <summary>A miss's retry bucket (`ReviewMisses`, backoff 1 s): at its deadline second the host must wake now, and
    /// the tick's drain must send it.</summary>
    [Fact]
    public void A_due_miss_retry_wakes_the_host_now_and_leaves_on_the_next_drain()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 2);
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();
        AnswerOnly(provider.Seen[0].Ticket, t, slots[1]);          // slots[0] missed → retry at Now + 1

        Assert.Equal(1, Fetch.NextWakeAt());                       // before the deadline: the deadline (unchanged)
        Entities.Now = 1;                                          // the frame tick moves the clock onto it
        Assert.Equal(1, Fetch.NextWakeAt());                       // DUE ⇒ wake now, never "nothing" (the defect)

        Fetch.Drain();                                             // the next tick
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(1, provider.Seen[1].Count);
    }

    /// <summary>The same for `Failed`'s retryable arm (a 503's 1 s backoff).</summary>
    [Fact]
    public void A_due_transport_retry_wakes_the_host_now_and_leaves_on_the_next_drain()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 3);
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();
        Fetch.Failed(provider.Seen[0].Ticket, 503, 0);

        Entities.Now = 1;
        Assert.Equal(1, Fetch.NextWakeAt());
        Fetch.Drain();
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(3, provider.Seen[1].Count);
        Assert.Equal(1, provider.Seen[1].Attempt);
    }

    // ── guards ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A bucket the online gate HOLDS is not a deadline, due or not: it leaves on the session's Online pump.</summary>
    [Fact]
    public void A_held_bucket_is_never_a_wake()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 1);
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();
        Fetch.Failed(provider.Seen[0].Ticket, 503, 0);             // a backed-off bucket…
        Fetch.CanSend = static _ => false;                         // …whose provider may not send

        Entities.Now = 5;
        Assert.Equal(int.MaxValue, Fetch.NextWakeAt());
    }

    /// <summary>Nothing waiting is nothing to wake for, and a drain with nothing due sends nothing.</summary>
    [Fact]
    public void An_idle_planner_never_wakes_and_never_sends()
    {
        Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Fetch.Drain();                                             // the registration's owed drain
        Entities.Now = 10;
        Assert.Equal(int.MaxValue, Fetch.NextWakeAt());
        Fetch.Drain();
        Assert.Empty(provider.Seen);
    }
}
