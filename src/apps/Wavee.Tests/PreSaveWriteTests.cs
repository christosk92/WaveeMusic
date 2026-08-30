using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Library;
using Wavee.Core;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

// ── The pre-save write path ──────────────────────────────────────────────────────────────────────────────────────────
// A pre-save is an ordinary collection write against a `spotify:prerelease:` entity. Everything downstream (optimistic
// signal, SQLite outbox, backoff → dead-letter → rollback) is existing machinery; what is NEW is the routing:
// uri kind → logical set "prerelease" → wire set "collection".
//
// The wire `set` string is INFERRED (the capture proves the endpoint, never the set). These tests pin the inference so
// that if a live 400 forces a revision, exactly one place changes and exactly these fail.
public class PreSaveWriteTests
{

    // The facade every StoreLibrarySource read goes through. Offline = store-only, never networks (design §1.3).
    static SwitchableEntityHydrator Offline(IStore store) => new(new Wavee.Backend.Hydration.OfflineEntityHydrator(store));
    const string PreUri = "spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh";
    static SessionContext Ctx => new("bob", "US", "premium", "en", Tier.Premium, false);

    // ── the set mapping ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreRelease_RidesTheCollectionWireSet()
        => Assert.Equal("collection", CollectionSets.WireSet("prerelease"));

    [Fact]
    public void PreRelease_IsDisambiguatedByItsOwnUriPrefix()
    {
        // Three logical sets share the "collection" wire set; the prefix is what keeps them apart client-side.
        Assert.Equal("spotify:prerelease:", CollectionSets.UriPrefix("prerelease"));
        Assert.Equal("spotify:track:", CollectionSets.UriPrefix("liked"));
        Assert.Equal("spotify:album:", CollectionSets.UriPrefix("albums"));
    }

    [Fact]
    public void TheOtherSetMappings_AreUnregressed()
    {
        Assert.Equal("collection", CollectionSets.WireSet("liked"));
        Assert.Equal("collection", CollectionSets.WireSet("albums"));
        Assert.Equal("artist", CollectionSets.WireSet("artists"));
        Assert.Equal("show", CollectionSets.WireSet("shows"));
        Assert.Equal("listenlater", CollectionSets.WireSet("episodes"));
    }

    [Fact]
    public void InboundSyncIsDELIBERATELYNotWired_ForPreRelease()
    {
        // THE SCOPE NOTE, asserted. Adding "prerelease" to LogicalSetsForWireSet before a live capture confirms the set
        // would let CollectionFetcher's mark-and-sweep unsave every local pre-save: it would fetch "collection", not see
        // the pre-saved uris in the server's answer, and sweep them away. Pre-saves made in Wavee still REACH the server
        // (outbound is unaffected); they just do not sync back in from another device yet.
        Assert.Equal(new[] { "liked", "albums" }, CollectionSets.LogicalSetsForWireSet("collection"));
        Assert.DoesNotContain("prerelease", CollectionSets.LogicalSetsForWireSet("collection"));

        // …and the per-item attribution therefore cannot claim a prerelease uri for a logical set.
        Assert.Null(CollectionSets.LogicalSetForItem("collection", PreUri));
        Assert.Equal("liked", CollectionSets.LogicalSetForItem("collection", "spotify:track:t"));
        Assert.Equal("albums", CollectionSets.LogicalSetForItem("collection", "spotify:album:a"));
    }

    // ── the write body ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildWrite_ForAPreSave_TargetsCollection_WithThePreReleaseUri()
    {
        var body = CollectionWriteMapper.BuildWrite("bob", "prerelease", PreUri, saved: true,
                                                    nowUnixSeconds: 1_780_000_000, clientUpdateId: "cuid");

        var wr = Col.WriteRequest.Parser.ParseFrom(body);
        Assert.Equal("bob", wr.Username);
        Assert.Equal("collection", wr.Set);                       // the INFERRED wire set
        var item = Assert.Single(wr.Items);
        Assert.Equal(PreUri, item.Uri);                           // the prerelease entity, never a synthesised album uri
        Assert.False(item.IsRemoved);
        Assert.Equal(1_780_000_000, item.AddedAt);                // UNIX SECONDS (the collection trap)
        Assert.Equal("cuid", wr.ClientUpdateId);
    }

    [Fact]
    public void BuildWrite_ForAnUndonePreSave_InvertsIsRemoved()
    {
        var body = CollectionWriteMapper.BuildWrite("bob", "prerelease", PreUri, saved: false, 1_780_000_000, "cuid");

        Assert.True(Col.WriteRequest.Parser.ParseFrom(body).Items[0].IsRemoved);
    }

    [Fact]
    public async Task Replay_PostsTheVendorWrite_AgainstThePreReleaseUri()
    {
        var strat = new SetReplayStrategy(new CollectionEchoRing());
        var t = new StubTransport();
        var op = new OutboxOp(1, "set", PreUri, "prerelease", true, 1, 0);

        var ok = await strat.Replay(op, t, Ctx, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal("/collection/v2/write", t.LastRequestRoute);
        Assert.Equal("POST", t.LastRequestMethod);
        Assert.Equal("application/vnd.collection-v2.spotify.proto", t.LastRequestHeaders!["Content-Type"]);

        var wr = Col.WriteRequest.Parser.ParseFrom(t.LastRequestBody);
        Assert.Equal("collection", wr.Set);
        Assert.Equal(PreUri, Assert.Single(wr.Items).Uri);
        Assert.False(wr.Items[0].IsRemoved);
    }

    [Fact]
    public void Replay_AppliesTheOptimisticHeart_UnderTheLogicalSet()
    {
        var store = new InMemoryStore();
        new SetReplayStrategy().ApplyOptimistic(new OutboxOp(1, "set", PreUri, "prerelease", true, 1, 0), store);

        Assert.True(store.IsSaved("prerelease", PreUri));
        Assert.False(store.IsSaved("albums", PreUri));            // the logical sets do not bleed into one another
    }

    [Fact]
    public void Rollback_RevertsTheHeart_WhenTheWriteDeadLetters()
    {
        // The safety net for the inferred set: a 400 backs off, dead-letters, and rolls the heart back. Visible,
        // reversible, never corrupting.
        var store = new InMemoryStore();
        var strat = new SetReplayStrategy();
        var op = new OutboxOp(1, "set", PreUri, "prerelease", true, 1, 0);

        strat.ApplyOptimistic(op, store);
        Assert.True(store.IsSaved("prerelease", PreUri));

        strat.Rollback(op, store);
        Assert.False(store.IsSaved("prerelease", PreUri));
    }

    // ── the uri → set inference (EngineMutationSource.SetForUri, reached through the public seam) ─────────────────────

    static EngineMutationSource Source(InMemoryStore store) =>
        new(store, new MutationEngine(store, [new SetReplayStrategy()]), new StubTransport(), () => Ctx);

    [Fact]
    public async Task APreReleaseUri_RoutesToThePreReleaseSet_NotAlbums()
    {
        var store = new InMemoryStore();
        var src = Source(store);

        await src.SetSavedAsync(PreUri, true);

        Assert.True(store.IsSaved("prerelease", PreUri));
        Assert.False(store.IsSaved("albums", PreUri));
        Assert.False(store.IsSaved("liked", PreUri));
        Assert.True(src.IsSaved(PreUri));                         // and it joins the one aggregated snapshot
        Assert.Contains(PreUri, src.Saved);
    }

    [Fact]
    public async Task ThePreSaveReachesTheWire_AsACollectionWrite()
    {
        var store = new InMemoryStore();
        var stub = new StubTransport();
        var src = new EngineMutationSource(store, new MutationEngine(store, [new SetReplayStrategy()]), stub, () => Ctx);

        await src.SetSavedAsync(PreUri, true);

        Assert.Equal("/collection/v2/write", stub.LastRequestRoute);
        var wr = Col.WriteRequest.Parser.ParseFrom(stub.LastRequestBody);
        Assert.Equal("collection", wr.Set);
        Assert.Equal(PreUri, Assert.Single(wr.Items).Uri);
    }

    [Fact]
    public async Task UnPreSaving_RemovesTheHeart_AndSendsTheRemoval()
    {
        var store = new InMemoryStore();
        var stub = new StubTransport();
        var src = new EngineMutationSource(store, new MutationEngine(store, [new SetReplayStrategy()]), stub, () => Ctx);

        await src.SetSavedAsync(PreUri, true);
        await src.SetSavedAsync(PreUri, false);

        Assert.False(store.IsSaved("prerelease", PreUri));
        Assert.False(src.IsSaved(PreUri));
        Assert.True(Col.WriteRequest.Parser.ParseFrom(stub.LastRequestBody).Items[0].IsRemoved);
    }

    [Fact]
    public async Task TheOtherUriKinds_StillRouteExactlyAsBefore()
    {
        var store = new InMemoryStore();
        var src = Source(store);

        await src.SetSavedAsync("spotify:track:t", true);
        await src.SetSavedAsync("spotify:album:a", true);
        await src.SetSavedAsync("spotify:artist:r", true);
        await src.SetSavedAsync("spotify:show:s", true);
        await src.SetSavedAsync("spotify:episode:e", true);

        Assert.True(store.IsSaved("liked", "spotify:track:t"));
        Assert.True(store.IsSaved("albums", "spotify:album:a"));
        Assert.True(store.IsSaved("artists", "spotify:artist:r"));
        Assert.True(store.IsSaved("shows", "spotify:show:s"));
        Assert.True(store.IsSaved("episodes", "spotify:episode:e"));
        Assert.False(store.IsSaved("prerelease", "spotify:album:a"));
    }

    // ── BuildUnion: the heart survives a restart ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void APersistedPreSave_IsRestoredIntoTheSavedUnionAtConstruction()
    {
        // AllSets carries "prerelease" for exactly this: at boot the persisted collection_items rows are re-read, and
        // without the set in that list the pre-save heart would come back grey even though the write succeeded.
        var store = new InMemoryStore();
        store.SetSaved("prerelease", PreUri, true, SyncState.Confirmed);   // what a cold load replays

        var src = Source(store);

        Assert.True(src.IsSaved(PreUri));
        Assert.Contains(PreUri, src.Saved);
    }

    [Fact]
    public void ABulkReload_KeepsThePreSaveInTheUnion()
    {
        var store = new InMemoryStore();
        var src = Source(store);
        Assert.False(src.IsSaved(PreUri));

        using (store.BeginBulk())
            store.SetSaved("prerelease", PreUri, true, SyncState.Confirmed);

        Assert.True(src.IsSaved(PreUri));      // the bulk signal triggers the full BuildUnion re-read
    }

    // ── no library fan-out ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APreSaveDoesNotWakeAnyLibraryCollection()
    {
        // StoreLibrarySource.KindOfUri deliberately returns null for spotify:prerelease: — no library page lists
        // pre-saves, so there is no collection to invalidate. The heart re-skins through LibraryBridge's per-URI signal.
        var store = new InMemoryStore();
        using var lib = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);
        var woken = new List<CollectionKind>();
        using var sub = lib.CollectionsChanged.Subscribe(Observers.From<CollectionKind>(woken.Add));

        store.SetSaved("prerelease", PreUri, true, SyncState.Confirmed);
        Assert.Empty(woken);

        store.SetSaved("albums", "spotify:album:a", true, SyncState.Confirmed);
        Assert.Contains(CollectionKind.Albums, woken);      // …while an ordinary album save still does
    }
}
