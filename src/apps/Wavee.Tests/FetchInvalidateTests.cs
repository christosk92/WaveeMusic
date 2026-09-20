// ── Wavee.Tests/FetchInvalidateTests.cs — the fifth mark: Invalidate re-asks what IS known, without a skeleton ──────
//
// `Refresh` re-asks what is NOT known; `Invalidate` re-asks what IS: a daylist past its rollover, a chart past its week.
// The row keeps its values (`Known` stays set, the surface keeps rendering) while `Table.Stale` takes the group out of
// the planner's `Settled` set, so the next plan asks for it and any wire authority may replace it. Every fact below is
// a column read over real rows in a real scope, with the transport stood in by FetchTests' `RecordingProvider`.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class FetchInvalidateTests : IDisposable
{
    const uint Identity = (uint)TrackFields.Identity;

    public FetchInvalidateTests()
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

    static Scope SignedIn()
    {
        Entities.Boot(new CatalogScope("spotify", "christos", "en-US", "US", 0, true));
        return Entities.Current;
    }

    static int[] Rows(Table table, int count)
    {
        var slots = new int[count];
        for (int i = 0; i < count; i++) slots[i] = table.Slot(("spotify:track:" + Guid.NewGuid().ToString("n")).AsSpan());
        return slots;
    }

    /// <summary>Commit Identity for one row at <paramref name="auth"/> through the real answer path.</summary>
    static void AnswerIdentity(uint ticket, EntityId id, Authority auth, string title)
    {
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(id, auth, Identity);
        row.Title = s.Text(title);
        Fetch.Answer(ticket, s);
    }

    /// <summary>A row whose Identity is known at Full authority, with the batch that answered it settled.</summary>
    static (Scope Scope, RecordingProvider Provider, TrackTable Table, int Slot) KnownRow()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int slot = Rows(t, 1)[0];
        Fetch.Plan(scope, t, [slot], Identity, FetchPriority.Visible);
        Fetch.Drain();                                              // the host's tick (wave D4): a plan never sends
        AnswerIdentity(provider.Seen[0].Ticket, t.Id[slot], Authority.Full, "the ended edition");
        provider.Seen.Clear();
        Assert.True(t.Knows(slot, Identity));
        Assert.False(t.IsStale(slot, Identity));
        return (scope, provider, t, slot);
    }

    [Fact]
    public void Invalidate_keeps_the_row_known_marks_it_stale_and_asks_once()
    {
        var (_, provider, t, slot) = KnownRow();
        uint version = t.Version[slot];

        Entities.Invalidate(t, [slot], Identity);
        Fetch.Drain();

        Assert.True(t.Knows(slot, Identity));                       // still rendering: never a skeleton
        Assert.True(t.IsStale(slot, Identity));
        Assert.Equal(0u, t.Settled(slot) & Identity);
        Assert.Equal(Identity, t.Asked[slot] & Identity);           // re-asked by the plan that followed
        Assert.True(t.Version[slot] > version);
        Assert.Single(provider.Seen);
        Assert.Equal(Identity, provider.Seen[0].Wanted);
    }

    [Fact]
    public void A_stale_group_is_a_need_while_a_known_one_is_not()
    {
        // The pure decision, over the marks alone: `NeedOf` reads `Settled = Known & ~Stale`, so a filled group that
        // has been marked stale is asked for exactly like a missing one.
        Scope scope = Boot();
        TrackTable t = scope.Tracks;
        int slot = Rows(t, 1)[0];
        t.Known[slot] |= Identity;
        Assert.Equal(0u, Fetch.NeedOf(t, slot, Identity));

        t.Stale[slot] |= Identity;

        Assert.Equal(Identity, Fetch.NeedOf(t, slot, Identity));
        Assert.True(t.Knows(slot, Identity));
        Assert.True(t.IsStale(slot, Identity));
    }

    [Fact]
    public void A_thin_re_answer_lands_on_a_stale_group_and_clears_the_mark()
    {
        var (_, provider, t, slot) = KnownRow();
        var before = t.Title[slot];

        Entities.Invalidate(t, [slot], Identity);
        Fetch.Drain();
        AnswerIdentity(provider.Seen[0].Ticket, t.Id[slot], Authority.Thin, "the new edition");

        Assert.NotEqual(before, t.Title[slot]);                     // a stale group is a hole: Thin over Full lands
        Assert.False(t.IsStale(slot, Identity));
        Assert.True(t.Knows(slot, Identity));
        Assert.Equal(Identity, t.Settled(slot) & Identity);
    }

    [Fact]
    public void A_terminal_failure_after_invalidate_un_asks_so_a_later_ensure_re_plans()
    {
        var (_, provider, t, slot) = KnownRow();

        Entities.Invalidate(t, [slot], Identity);
        Fetch.Drain();
        Fetch.Failed(provider.Seen[0].Ticket, 404, 0);

        Assert.Equal(0u, t.Asked[slot] & Identity);                 // stale is unsettled, so the failure un-asks it
        Assert.True(t.IsStale(slot, Identity));
        Assert.True(t.Knows(slot, Identity));

        Entities.Ensure(t, [slot], Identity);
        Fetch.Drain();
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(Identity, provider.Seen[1].Wanted);
    }

    [Fact]
    public void Without_a_provider_invalidate_is_harmless()
    {
        Scope scope = Boot();
        TrackTable t = scope.Tracks;
        int slot = Rows(t, 1)[0];
        t.Known[slot] |= Identity;

        Entities.Invalidate(t, [slot], Identity);                   // nothing registered: the plan has nowhere to send

        Assert.True(t.Knows(slot, Identity));
        Assert.True(t.IsStale(slot, Identity));
    }

    [Fact]
    public void Invalidate_skips_none_and_out_of_range_slots()
    {
        var (_, provider, t, _) = KnownRow();

        Entities.Invalidate(t, [Table.None, t.Count, t.Count + 7], Identity);
        Fetch.Drain();

        Assert.Empty(provider.Seen);
    }

    [Fact]
    public void InvalidateEdge_on_an_unknown_relation_plans_nothing()
    {
        Scope scope = SignedIn();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Assert.Equal(EdgeState.Unknown, scope.Edges.Rootlist.State(scope.MeSlot));

        Entities.InvalidateEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();

        Assert.Empty(provider.Seen);
    }

    [Fact]
    public void InvalidateEdge_on_a_complete_relation_asks_again_and_keeps_the_rows()
    {
        Scope scope = SignedIn();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        var rootlist = scope.Edges.Rootlist;

        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();
        var s = Staging.Rent();
        ref var list = ref s.Rootlists.Add();
        list.Parent = scope.Users.Id[scope.MeSlot];
        list.Start = 0;
        list.Length = 0;
        Fetch.Answer(provider.Seen[0].Ticket, s);                   // an empty rootlist is a Complete answer
        Assert.Equal(EdgeState.Complete, rootlist.State(scope.MeSlot));
        int count = rootlist.Count(scope.MeSlot);

        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();
        Assert.Single(provider.Seen);                               // sealed for the scope…

        Entities.InvalidateEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();

        Assert.Equal(2, provider.Seen.Count);                       // …until the edition ends
        Assert.Equal(FetchPriority.Prefetch, provider.Seen[1].Priority);
        Assert.Equal(EdgeState.Complete, rootlist.State(scope.MeSlot));   // never Clear: the rows keep rendering
        Assert.Equal(count, rootlist.Count(scope.MeSlot));
    }
}
