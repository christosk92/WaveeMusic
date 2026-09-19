// ── Wavee.Tests/FetchMissUnservedTests.cs — a group NO route serves is a seal, never an omission (Fetch.Miss.cs) ─────
//
// THE REGRESSION THIS FILE GATES (2026-09-19 → 20). `ReviewMisses` counted every wanted group a 200 answer left
// unsettled on an unchanged row as "the body omitted this entity" — including groups this build has no transport
// for at all. `EpisodeFields.Progress` is one: it is the login hydrate's, not a per-episode ask, so `FetchRoutes.For`
// seals it and the provider answers WITHOUT it by construction. Every show page asking `Row | About | Progress` then
// "missed" Progress on every never-played episode: un-asked, re-asked after 1 s and 2 s (three answers of nothing per
// row, 1,356 `fetch.miss` lines in one evening's log), then sealed with a FALSE `Table.Failed` mark on a group that
// was correctly sealed from the first plan.
//
// `FetchMissPolicy.Missed` is the pure mask (wanted, settled, the routes that did not answer, the groups no route
// serves) asserted directly; the table half runs a real `EpisodeTable` batch through `Fetch.Plan` / `Fetch.Drain` /
// `Fetch.Answer` / `Fetch.Pump` with `RecordingProvider` (FetchTests.cs) as the transport. No source text is read.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class FetchMissUnservedPureTests
{
    const uint Identity = 0x1F, About = 0x100, Progress = 0x200;

    [Fact]
    public void Missed_is_wanted_minus_settled_minus_routes_that_did_not_answer_minus_groups_no_route_serves()
    {
        // Nothing settled, nothing unfilled, Progress unserved: only the served groups can be an omission.
        Assert.Equal(Identity | About, FetchMissPolicy.Missed(Identity | About | Progress, 0, 0, Progress));
        // The paint groups landed: the routeless remainder is a seal, not a miss.
        Assert.Equal(0u, FetchMissPolicy.Missed(Identity | About | Progress, Identity | About, 0, Progress));
        // A route that did not answer (Answer's `unfilled`) is Unask's business, not a miss either.
        Assert.Equal(Identity, FetchMissPolicy.Missed(Identity | About, 0, About, 0));
        // A batch made ONLY of unserved groups can miss nothing.
        Assert.Equal(0u, FetchMissPolicy.Missed(Progress, 0, 0, Progress));
        // Every route answered every group: nothing.
        Assert.Equal(0u, FetchMissPolicy.Missed(Identity, Identity, 0, 0));
    }

    [Fact]
    public void Episode_progress_has_no_route_and_so_is_sealed_by_the_walk()
    {
        Span<FetchRoute> routes = stackalloc FetchRoute[FetchRoutes.MaxRoutes];
        _ = FetchRoutes.For(FetchSubject.Entity, EntityKind.Episode, (uint)(EpisodeFields.Row | EpisodeFields.About | EpisodeFields.Progress), routes, out uint sealedGroups);
        Assert.Equal((uint)EpisodeFields.Progress, sealedGroups);
    }
}

[Collection(EntitiesCollection.Name)]
public class FetchMissUnservedTests : IDisposable
{
    const uint Paint = (uint)(EpisodeFields.Identity | EpisodeFields.About);
    const uint Progress = (uint)EpisodeFields.Progress;

    public FetchMissUnservedTests()
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
        for (int i = 0; i < count; i++) slots[i] = table.Slot(("spotify:episode:" + (i + 1).ToString().PadLeft(22, '0')).AsSpan());
        return slots;
    }

    [Fact]
    public void A_progress_only_batch_answers_without_a_miss_and_stays_sealed()
    {
        // The shape today's log was full of: rows whose identity was already known (the disk had it), asking only for
        // the one group nothing serves. The provider answers at once with nothing.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        EpisodeTable t = scope.Episodes;
        int[] slots = Rows(t, 3);

        Fetch.Plan(scope, t, slots, Progress, FetchPriority.Visible);
        Fetch.Drain();
        var sent = Assert.Single(provider.Seen);
        Fetch.Answer(sent.Ticket, null);

        foreach (int slot in slots)
        {
            Assert.Equal(Progress, t.Asked[slot] & Progress);        // the plan's seal holds
            Assert.False(t.IsFailed(slot, Progress));                 // and it is not a failure
            Assert.Equal(0u, t.Inflight[slot]);
        }
        Assert.Equal(0, Fetch.Pending);                                // nothing queued a retry of nothing
        Entities.Now = 100;
        Fetch.Pump();
        Assert.Single(provider.Seen);

        // A re-plan of the same rows for the same group asks nothing: sealed really means sealed.
        Fetch.Plan(scope, t, slots, Progress, FetchPriority.Visible);
        Fetch.Drain();
        Assert.Single(provider.Seen);
    }

    [Fact]
    public void A_mixed_batch_misses_only_the_served_groups_the_body_omitted()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        EpisodeTable t = scope.Episodes;
        int[] slots = Rows(t, 2);
        int named = slots[0], omitted = slots[1];

        Fetch.Plan(scope, t, slots, Paint | Progress, FetchPriority.Visible);
        Fetch.Drain();
        var s = Staging.Rent();
        ref var row = ref s.Episodes.RowFor(t.Id[named], Authority.Full, Paint);
        row.Title = s.Text("named");
        Fetch.Answer(provider.Seen[0].Ticket, s);

        // The named row: paint landed, Progress sealed, nothing missed.
        Assert.True(t.Knows(named, (uint)EpisodeFields.Title));
        Assert.Equal(Progress, t.Asked[named] & Progress);
        Assert.False(t.IsFailed(named, Progress));

        // The omitted row: the SERVED groups are un-asked for a retry; Progress is untouched — still the plan's seal.
        Assert.Equal(0u, t.Asked[omitted] & Paint);
        Assert.Equal(Progress, t.Asked[omitted] & Progress);
        Assert.False(t.IsFailed(omitted, Progress));
        Assert.Equal(1, Fetch.Pending);

        Entities.Now = 1;
        Fetch.Pump();
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(1, provider.Seen[1].Count);
        Assert.Equal(Paint, provider.Seen[1].Wanted);                  // the retry never carries the routeless group
    }
}
