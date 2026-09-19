// ── Wavee.Tests/FetchMissPolicyTests.cs — re-ask an entity a successful batch omitted (ledger 2a, plan §4.1) ───────
//
// THE BUG THIS FILE GATES. `Fetch.Answer`'s seal ("groups the answer did not fill stay ASKED") is correct for a
// route that answered every row it named and simply does not carry a bit — but a batch can also answer 200 while
// its BODY leaves an entity out entirely (a show missing from a page of shows, an episode a paged read dropped).
// Before this file, that row was `Asked` with `Inflight` cleared and nothing else: the exact "asked, nothing
// coming" shape a surface already renders as a blank title and 0:00, forever, until an unrelated `Refresh`.
//
// Two halves, two kinds of fact:
//   · `FetchMissPolicy.Decide` is pure arithmetic over an `int` — no `Table`, no `Scope`, no I/O — asserted directly.
//   · the sequence a real omitted-entity batch produces (asked → answered without the row → retried → sealed) goes
//     through the SAME public doors every other Fetch test does: `Fetch.Plan` / `Fetch.Drain` / `Fetch.Answer` /
//     `Fetch.Pump`, over a real `TrackTable` row, with `RecordingProvider` standing in for the transport (the same
//     shape `FetchTests.cs` and `FetchInvalidateTests.cs` already use). Nothing here reads production source text.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class FetchMissPolicyPureTests
{
    [Theory]
    [InlineData(0, FetchMissPolicy.Verdict.Retry)]    // the first miss: two retries are still owed
    [InlineData(1, FetchMissPolicy.Verdict.Retry)]    // the second: one retry left
    [InlineData(2, FetchMissPolicy.Verdict.Seal)]     // the third: the budget (MaxMisses = 2) is spent
    [InlineData(9, FetchMissPolicy.Verdict.Seal)]     // well past it: still Seal, never a retry that never ends
    public void Decide_retries_twice_then_seals(int missesSoFar, FetchMissPolicy.Verdict expected)
        => Assert.Equal(expected, FetchMissPolicy.Decide(missesSoFar));

    [Fact]
    public void Decide_clamps_a_negative_count_to_the_first_miss()
        => Assert.Equal(FetchMissPolicy.Verdict.Retry, FetchMissPolicy.Decide(-1));

    [Fact]
    public void MaxMisses_is_two()
        => Assert.Equal(2, FetchMissPolicy.MaxMisses);
}

[Collection(EntitiesCollection.Name)]
public class FetchMissPolicyTests : IDisposable
{
    const uint Identity = (uint)TrackFields.Identity;

    public FetchMissPolicyTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
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

    /// <summary>Answer a batch's ticket while omitting <paramref name="omit"/> from the staging entirely — the
    /// "route answered 200, the body just did not name this row" case, built with a real <c>Staging</c> the same way
    /// <c>FetchInvalidateTests.AnswerIdentity</c> does for the row it DOES want to land.</summary>
    static void AnswerOmitting(uint ticket, TrackTable table, int keep, int omit)
    {
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(table.Id[keep], Authority.Full, Identity);
        row.Title = s.Text("kept");
        Fetch.Answer(ticket, s);
        _ = omit;   // documents which slot this call deliberately leaves out; nothing to assert against it here
    }

    /// <summary>A batch that lands NOTHING at all — the other shape a 200-with-an-empty-body takes (an empty page,
    /// a filtered-out entity). <c>Fetch.Answer</c> accepts a null staging for exactly this ("the provider had
    /// nothing for these uris").</summary>
    static void AnswerNothing(uint ticket) => Fetch.Answer(ticket, null);

    [Fact]
    public void A_row_a_200_batch_omits_is_unsealed_and_retried_not_left_blank_forever()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 2);
        int target = slots[0], companion = slots[1];

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();
        uint ticket = provider.Seen[0].Ticket;

        // The route answers 200 (this IS an Answer, not a Failed) and names `companion` but never `target`.
        AnswerOmitting(ticket, t, companion, target);

        // Before this fix `target` would now be Asked with Inflight cleared and Known still 0 — sealed, forever.
        Assert.False(t.Knows(target, Identity));
        Assert.Equal(0u, t.Inflight[target]);
        Assert.Equal(0u, t.Asked[target] & Identity);                // un-asked like `Unask` does for `unfilled`
        Assert.False(t.IsFailed(target, Identity));                  // not sealed yet — one miss, budget is two
        Assert.Equal(1, Fetch.Pending);                               // queued again, on its own, no second `Ensure`
        Assert.True(t.Knows(companion, Identity));                   // the row the answer DID name is unaffected
    }

    [Fact]
    public void A_second_miss_retries_again_a_third_seals_with_a_failure_mark()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 2);
        int target = slots[0], companion = slots[1];

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();
        AnswerOmitting(provider.Seen[0].Ticket, t, companion, target);   // miss #1 → Retry

        Assert.Equal(1, Fetch.NextWakeAt());                         // the retry's backoff, the same idiom the
        Entities.Now = 1;                                            // retryable-`Failed` fact uses (Backoff(0,0,0) = 1)
        Fetch.Pump();
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(1, provider.Seen[1].Count);                     // only `target` rides the retry, not `companion`

        AnswerNothing(provider.Seen[1].Ticket);                      // miss #2 → still Retry (1 < MaxMisses)
        Assert.False(t.IsFailed(target, Identity));
        Assert.Equal(0u, t.Asked[target] & Identity);
        Assert.Equal(1, Fetch.Pending);

        Entities.Now = 4;                                            // Backoff(1,0,0) = 2 → ready at 1 + 2 = 3
        Fetch.Pump();
        Assert.Equal(3, provider.Seen.Count);

        AnswerNothing(provider.Seen[2].Ticket);                      // miss #3 → Seal (MaxMisses spent)

        Assert.True(t.IsFailed(target, Identity));                   // the row twin of a terminal `Fetch.Failed`
        Assert.Equal(Identity, t.Asked[target] & Identity);          // the seal holds, same as an exhausted ask today
        Assert.Equal(0, Fetch.Pending);                               // nothing queued a fourth attempt
        Entities.Now = 1000;
        Fetch.Pump();
        Assert.Equal(3, provider.Seen.Count);                        // sealed really means sealed: no further send
    }

    [Fact]
    public void A_refresh_between_misses_resets_the_count_so_two_more_misses_retry_before_sealing()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 2);
        int target = slots[0], companion = slots[1];

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();
        AnswerOmitting(provider.Seen[0].Ticket, t, companion, target);   // miss #1 (count → 1)
        Entities.Now = 1;
        Fetch.Pump();
        Assert.Equal(2, provider.Seen.Count);

        // A deliberate Retry vacancy (a Refresh) is a clean slate for the miss count, not just for `Asked`.
        Fetch.Refresh(scope, t, [target], Identity, FetchPriority.Visible);
        Fetch.Drain();
        Assert.Equal(3, provider.Seen.Count);                         // Refresh's own immediate ask

        // If the count had carried over from before the refresh, this single miss would already be the SECOND and
        // the next one would seal. It does not: two more misses are owed before a third seals.
        AnswerNothing(provider.Seen[2].Ticket);                       // miss (count → 1 again, not 2)
        Assert.False(t.IsFailed(target, Identity));
        Entities.Now = 2;
        Fetch.Pump();
        Assert.Equal(4, provider.Seen.Count);

        AnswerNothing(provider.Seen[3].Ticket);                       // second miss since the refresh → still Retry
        Assert.False(t.IsFailed(target, Identity));
        Assert.Equal(1, Fetch.Pending);
    }

    [Fact]
    public void A_route_that_answers_every_row_it_names_tracks_no_miss()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = Rows(t, 1);

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Drain();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(t.Id[slots[0]], Authority.Full, Identity);
        row.Title = s.Text("all present");
        Fetch.Answer(provider.Seen[0].Ticket, s);

        Assert.True(t.Knows(slots[0], Identity));
        Assert.Equal(0, Fetch.Pending);                                // nothing queued: there was no miss to retry
        Assert.False(t.IsFailed(slots[0], Identity));
    }
}
