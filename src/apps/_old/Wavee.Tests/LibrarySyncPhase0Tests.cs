using System;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Phase 0 groundwork (docs/library-sync-implementation-plan.md §12): store-level no-op elision (§7.4) and the pending-op
// shield primitive (§7.2). No user-visible behavior change — these de-risk the read/write sync phases that follow. The
// TrackList view-cache invalidation (§4.2) is UI-level and verified by construction (the ReferenceEquals guard in Render
// plus the BoundTrackAt current-count guard), so it has no unit here — there is no TrackList component test harness and
// the plan forbids inventing one.
public class LibrarySyncPhase0Tests
{
    sealed class Counter : IObserver<StoreChange>
    {
        public int N;
        public void OnNext(StoreChange v) => N++;
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    [Fact]
    public void SetSaved_SameState_EmitsExactlyOnce()
    {
        var store = new InMemoryStore();
        var c = new Counter();
        using var sub = store.Changes.Subscribe(c);

        store.SetSaved("liked", "spotify:track:a", true, SyncState.Confirmed);   // real write
        store.SetSaved("liked", "spotify:track:a", true, SyncState.Confirmed);   // idempotent echo → elided

        Assert.Equal(1, c.N);
        Assert.True(store.IsSaved("liked", "spotify:track:a"));
    }

    [Fact]
    public void SetSaved_StateTransition_EmitsTwice()
    {
        var store = new InMemoryStore();
        var c = new Counter();
        using var sub = store.Changes.Subscribe(c);

        store.SetSaved("liked", "spotify:track:a", true, SyncState.Pending);     // optimistic
        store.SetSaved("liked", "spotify:track:a", true, SyncState.Confirmed);   // ack: same key, DIFFERENT state → writes again

        Assert.Equal(2, c.N);
        Assert.True(store.IsSaved("liked", "spotify:track:a"));
    }

    [Fact]
    public void SetSaved_UnsaveAbsent_EmitsNothing()
    {
        var store = new InMemoryStore();
        var c = new Counter();
        using var sub = store.Changes.Subscribe(c);

        store.SetSaved("liked", "spotify:track:ghost", false, SyncState.Confirmed);   // never present → no-op

        Assert.Equal(0, c.N);
        Assert.False(store.IsSaved("liked", "spotify:track:ghost"));
    }

    [Fact]
    public void SetSaved_UnsavePresent_EmitsOnce()
    {
        var store = new InMemoryStore();
        store.SetSaved("liked", "spotify:track:a", true, SyncState.Confirmed);
        var c = new Counter();
        using var sub = store.Changes.Subscribe(c);
        c.N = 0;   // discard SimpleSubject's replay-of-last on subscribe (the setup write); count only the removal below

        store.SetSaved("liked", "spotify:track:a", false, SyncState.Confirmed);   // present → real removal

        Assert.Equal(1, c.N);
        Assert.False(store.IsSaved("liked", "spotify:track:a"));
    }

    [Fact]
    public async Task HasPending_TrueAfterSave_FalseAfterDrain_FalseForUnrelated()
    {
        var store = new InMemoryStore();
        var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy() });

        eng.Save("liked", "spotify:track:a", true);
        Assert.True(eng.HasPending("liked", "spotify:track:a"));
        Assert.False(eng.HasPending("albums", "spotify:track:a"));   // unrelated set
        Assert.False(eng.HasPending("liked", "spotify:track:z"));    // unrelated entity

        await eng.Drain(new StubTransport(), SessionContext.LoggedOut);   // stub replay succeeds → outbox cleared
        Assert.False(eng.HasPending("liked", "spotify:track:a"));
    }

    [Fact]
    public void HasPending_FalseWhenNothingPending()
    {
        // The rootlist key shape (rootlist|{set}|{uri}) is checked now for Phase 4, but no strategy populates it yet, so an
        // empty outbox reports false for both the "set" and the "rootlist" key shapes.
        var eng = new MutationEngine(new InMemoryStore(), new IMutationStrategy[] { new SetReplayStrategy() });
        Assert.False(eng.HasPending("playlists", "spotify:playlist:p"));
        Assert.False(eng.HasPending("liked", "spotify:track:a"));
    }
}
