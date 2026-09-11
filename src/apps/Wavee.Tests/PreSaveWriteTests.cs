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

        Assert.Equal(MutationReplayDisposition.Applied, ok.Disposition);
        Assert.Equal("/collection/v2/write", t.LastRequestRoute);
        Assert.Equal("POST", t.LastRequestMethod);
        Assert.Equal("application/vnd.collection-v2.spotify.proto", t.LastRequestHeaders!["Content-Type"]);

        var wr = Col.WriteRequest.Parser.ParseFrom(t.LastRequestBody);
        Assert.Equal("collection", wr.Set);
        Assert.Equal(PreUri, Assert.Single(wr.Items).Uri);
        Assert.False(wr.Items[0].IsRemoved);
    }

    [Fact]
    public async Task Staging_publishes_presave_in_its_logical_set()
    {
        await using var host = new ReplicaTestHost();
        await host.Mutations.SaveAsync("prerelease", PreUri, true);
        Assert.Single(host.Replicas.ReadCollection("prerelease").Items);
        Assert.Empty(host.Replicas.ReadCollection("albums").Items);
        Assert.Empty(host.Replicas.ReadConfirmedCollection("prerelease").Items);
    }

    [Fact]
    public async Task Terminal_rejection_removes_only_pending_presave()
    {
        await using var host = new ReplicaTestHost();
        var op = await host.Replicas.StageAsync("set", PreUri, "prerelease", true);
        Assert.Single(host.Replicas.ReadCollection("prerelease").Items);
        await host.Replicas.RejectAsync(op, PlaylistMutationFailure.Forbidden, "denied");
        Assert.Empty(host.Replicas.ReadCollection("prerelease").Items);
    }

    [Fact]
    public async Task APreReleaseUri_RoutesToThePreReleaseSet_NotAlbums()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok([]));
        using var source = new EngineMutationSource(h.Store, h.Mut, h.Sync);
        await source.SetSavedAsync(PreUri, true);
        Assert.True(h.Store.IsSaved("prerelease", PreUri));
        Assert.False(h.Store.IsSaved("albums", PreUri));
        Assert.False(h.Store.IsSaved("liked", PreUri));
        Assert.True(source.IsSaved(PreUri));
        Assert.Contains(PreUri, source.Saved);
        Assert.Equal("/collection/v2/write", h.Dealer.LastRequestRoute);
        var request = Col.WriteRequest.Parser.ParseFrom(h.Dealer.LastRequestBody);
        Assert.Equal("collection", request.Set);
        Assert.Equal(PreUri, Assert.Single(request.Items).Uri);
    }

    [Fact]
    public async Task UnPreSaving_RemovesTheHeart_AndSendsTheRemoval()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok([]));
        using var source = new EngineMutationSource(h.Store, h.Mut, h.Sync);
        await source.SetSavedAsync(PreUri, true);
        await source.SetSavedAsync(PreUri, false);
        Assert.False(h.Store.IsSaved("prerelease", PreUri));
        Assert.False(source.IsSaved(PreUri));
        Assert.True(Col.WriteRequest.Parser.ParseFrom(h.Dealer.LastRequestBody).Items[0].IsRemoved);
    }

    [Theory]
    [InlineData("spotify:track:t", "liked")]
    [InlineData("spotify:album:a", "albums")]
    [InlineData("spotify:artist:r", "artists")]
    [InlineData("spotify:show:s", "shows")]
    [InlineData("spotify:episode:e", "episodes")]
    public async Task Other_uri_kinds_keep_their_logical_set(string uri, string set)
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok([]));
        using var source = new EngineMutationSource(h.Store, h.Mut, h.Sync);
        await source.SetSavedAsync(uri, true);
        Assert.True(h.Store.IsSaved(set, uri));
        Assert.False(h.Store.IsSaved("prerelease", uri));
    }

    [Fact]
    public async Task Confirmed_presave_restores_the_saved_union()
    {
        await using var host = new ReplicaTestHost(bootstrap: Wavee.Backend.Sync.ReplicaBootstrap.Empty with
        { Collections = [new("prerelease", [new SavedItem(PreUri, 1)])] });
        await host.Replicas.PublishInitialAsync();
        using var source = new EngineMutationSource(host.Store, host.Mutations, new Wavee.Backend.Sync.RootlistCommandRouter(static () => null));
        Assert.True(source.IsSaved(PreUri));
        Assert.Contains(PreUri, source.Saved);
    }

    [Fact]
    public async Task Committed_presave_updates_the_saved_union()
    {
        await using var host = new ReplicaTestHost();
        using var source = new EngineMutationSource(host.Store, host.Mutations, new Wavee.Backend.Sync.RootlistCommandRouter(static () => null));
        Assert.False(source.IsSaved(PreUri));
        var intent = await host.Replicas.StageAsync("set", PreUri, "prerelease", true);
        await host.Replicas.CompleteAsync(intent, null, null, false);
        Assert.True(source.IsSaved(PreUri));
    }
}