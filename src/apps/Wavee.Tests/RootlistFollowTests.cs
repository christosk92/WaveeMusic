using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Wavee.Core;
using Xunit;
using Col = Wavee.Protocol.Collection;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// Phase 4 (§2.5–§2.8, RC3) — following a playlist is a rootlist ADD/REM, not a collection write. Routing, per-uri
// coalescing, the rootlist /changes wire body (Delta.Info + want-flags + nonce + ADD item attributes), the 409 rebase, the
// bootstrap GET, the Saved-union fold, the OpRebase response capture (incl. zstd), and durable round-trip.
public class RootlistFollowTests : IAsyncLifetime
{
    static SessionContext Ctx => new("bob", "US", "premium", "en", Tier.Premium, false);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Records every request (route/method/body/headers) and scripts the response by (route, method, body, 1-based call#).
    sealed class RecTransport(Func<string, string, byte[], int, Resp> respond) : ITransport
    {
        public readonly List<(string Route, string Method, byte[] Body, IReadOnlyDictionary<string, string>? Headers)> Sent = new();
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            var m = method ?? (body.IsEmpty ? "GET" : "POST");
            var b = body.ToArray();
            Sent.Add((route, m, b, headers is null ? null : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(respond(route, m, b, Sent.Count));
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default) => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static Resp Ok200(string route, string method, byte[] body, int call) => new(true, SlcRev(Rev24((byte)(call + 10))), 200);
    static byte[] SlcRev(byte[] rev) => new Pl.SelectedListContent { Revision = ByteString.CopyFrom(rev) }.ToByteArray();
    // I1: a revision only enters the store when it is the real 24-byte playlist4 head, so every response fixture
    // that is meant to be ADOPTED has to carry one (a short stand-in is now refused and the old value kept).
    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }
    readonly List<ReplicaTestHost> _hosts = [];
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() { foreach (var host in _hosts) await host.DisposeAsync(); }
    async Task<ReplicaTestHost> RootlistHost(IStore store, Func<DateTime>? now = null)
    {
        var host = new ReplicaTestHost(store: store, clock: now);
        _hosts.Add(host);
        if (store.RootlistRevision() is { } revision) await host.SeedRootlistAsync(store.Rootlist(), revision);
        return host;
    }
    static FakeExchange NoReads() => new((_, _) => new HttpResp(500, new Dictionary<string, string>(), []));
    static byte[] RootSnapshot(byte[] revision) => new Pl.SelectedListContent
        { Revision = ByteString.CopyFrom(revision), Contents = new Pl.ListItems { Pos = 0, Truncated = false } }.ToByteArray();


    static string TempDb() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { System.IO.File.Delete(f); } catch { } } }

    // ── 1. routing + optimistic ──────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Follow_RoutesToRootlistOp_NeverLikedSet_AndAppliesOptimistically()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Array.Empty<RootlistEntry>(), Rev24(9));   // seed a rev so drain needs no bootstrap
        var host = await RootlistHost(store);
        var eng = host.Mutations;
        var t = new RecTransport(Ok200);
        using var src = new EngineMutationSource(store, eng, host.AttachSync(NoReads(), t));

        // optimistic (no drain): a "rootlist"-typed pending op, the pill flips, the entry lands at position 0 — NOT the liked set.
        await eng.FollowAsync("spotify:playlist:x", true);
        Assert.True(eng.HasPending("playlists", "spotify:playlist:x"));       // rootlist|playlists|{uri} key
        Assert.True(store.IsSaved("playlists", "spotify:playlist:x"));        // pill on (Pending)
        Assert.False(store.IsSaved("liked", "spotify:playlist:x"));           // never the liked set
        Assert.Contains("spotify:playlist:x", src.Saved);                     // EngineMutationSource.Saved (incremental)
        var rl = store.Rootlist();
        Assert.Equal("spotify:playlist:x", rl[0].Uri);
        Assert.Equal(0, rl[0].Position);
        Assert.Equal(0, rl[0].Kind);

        // routing via the seam: SetSavedAsync of a playlist uri POSTs rootlist/changes, never /collection/v2/write.
        await src.SetSavedAsync("spotify:playlist:y", true, Ct);
        Assert.All(t.Sent, r => Assert.DoesNotContain("/collection/v2/write", r.Route));
        Assert.Contains(t.Sent, r => r.Route == "/playlist/v2/user/bob/rootlist/changes" && r.Method == "POST");
        Assert.True(store.IsSaved("playlists", "spotify:playlist:y"));        // Confirmed after the drain
        Assert.False(store.IsSaved("liked", "spotify:playlist:y"));
    }

    // ── 2. follow → unfollow before drain coalesces to ONE op (latest end-state) ─────────────────────────────────────
    [Fact]
    public async Task FollowThenUnfollow_BeforeDrain_CoalescesToOneOp_LatestWins()
    {
        var store = new InMemoryStore();
        var host = await RootlistHost(store);
        var eng = host.Mutations;

        await eng.FollowAsync("spotify:playlist:x", true);
        await eng.FollowAsync("spotify:playlist:x", false);   // toggle back before any drain

        Assert.Equal(1, eng.Pending);                                         // coalesced — toggles don't stack
        Assert.False(store.IsSaved("playlists", "spotify:playlist:x"));       // latest end-state = unfollowed (optimistic)
    }

    // ── 3. Replay POSTs rootlist/changes with the first-party headers + the full body shape ──────────────────────────
    [Fact]
    public async Task Replay_Follow_PostsRootlistChanges_WithHeadersAndBody()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Array.Empty<RootlistEntry>(), Rev24(9));   // rev present → single POST, no bootstrap
        var host = await RootlistHost(store);
        var strat = new RootlistFollowStrategy(host.Replicas, () => "https://spclient.test");
        var t = new RecTransport(Ok200);

        var ok = await strat.Replay(strat.Prepare(new OutboxOp(1, "rootlist", "spotify:playlist:x", "playlists", true, 1, 0, CreatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), t, Ctx, Ct);

        Assert.Equal(MutationReplayDisposition.Applied, ok.Disposition);
        var req = Assert.Single(t.Sent);
        Assert.Equal("/playlist/v2/user/bob/rootlist/changes", req.Route);
        Assert.Equal("POST", req.Method);
        Assert.Equal("application/x-www-form-urlencoded", req.Headers!["Content-Type"]);
        Assert.Equal("CAk=", req.Headers!["spotify-playlist-sync-reason"]);
        Assert.Equal("dummy", req.Headers!["spotify-accept-geoblock"]);
        Assert.Equal("false", req.Headers!["spotify-dsa-mode-enabled"]);

        var lc = Pl.ListChanges.Parser.ParseFrom(req.Body);
        Assert.Equal(Rev24(9), lc.BaseRevision.ToByteArray());     // base_revision = the stored rootlist rev
        Assert.True(lc.WantResultingRevisions);
        Assert.True(lc.WantSyncResult);
        Assert.Single(lc.Nonces);
        Assert.True(lc.Nonces[0] >= 1);
        var delta = Assert.Single(lc.Deltas);
        Assert.Equal("bob", delta.Info.User);
        Assert.True(delta.Info.Timestamp > 0);
        // Desktop never repeats the base inside the delta - the envelope base_revision IS the base (P2, verified
        // byte-for-byte against every captured /changes body).
        Assert.False(delta.HasBaseVersion);
        var wop = Assert.Single(delta.Ops);
        Assert.Equal(Pl.Op.Types.Kind.Add, wop.Kind);
        Assert.Equal(0, wop.Add.FromIndex);
        var item = Assert.Single(wop.Add.Items);
        Assert.Equal("spotify:playlist:x", item.Uri);
        Assert.True(item.Attributes.Public);                                  // rootlist ADD carries public=true
        Assert.True(item.Attributes.Timestamp > 0);                          // + a ms timestamp
    }

    // ── 4. unfollow posts a KEYED REM (items_as_key) — through the REAL engine order (optimistic edit BEFORE replay), so
    // the local row is already gone when Replay runs and an index/local-absence gate would silently drop the write ──────
    [Fact]
    public async Task Unfollow_EngineOrder_PostsKeyedRem()
    {
        var store = new InMemoryStore();
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[] { "spotify:playlist:a", "spotify:playlist:b", "spotify:playlist:x" }), Rev24(1));
        store.SetSaved("playlists", "spotify:playlist:x", true, SyncState.Confirmed);
        var host = await RootlistHost(store);
        var eng = host.Mutations;
        var t = new RecTransport(Ok200);

        await eng.FollowAsync("spotify:playlist:x", false);                              // optimistic: the local row is removed HERE
        Assert.DoesNotContain(store.Rootlist(), e => e.Uri == "spotify:playlist:x");
        await eng.Drain(t, Ctx);                                              // replay AFTER the optimistic edit — must still post

        var post = Assert.Single(t.Sent);
        Assert.Equal("/playlist/v2/user/bob/rootlist/changes", post.Route);
        var lc = Pl.ListChanges.Parser.ParseFrom(post.Body);
        var wop = Assert.Single(Assert.Single(lc.Deltas).Ops);
        Assert.Equal(Pl.Op.Types.Kind.Rem, wop.Kind);
        Assert.True(wop.Rem.ItemsAsKey);                                      // keyed, order-independent — never an index
        Assert.Equal("spotify:playlist:x", Assert.Single(wop.Rem.Items).Uri);
        Assert.Equal(0, eng.Pending);                                         // reconciled
    }

    // ── 4b. follow-then-drain then unfollow-then-drain end to end: both POSTs hit the wire ───────────────────────────
    [Fact]
    public async Task FollowThenUnfollow_EndToEnd_BothPost()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Array.Empty<RootlistEntry>(), Rev24(1));
        var host = await RootlistHost(store);
        var eng = host.Mutations;
        var t = new RecTransport(Ok200);

        await eng.FollowAsync("spotify:playlist:x", true);
        await eng.Drain(t, Ctx);
        await eng.FollowAsync("spotify:playlist:x", false);
        await eng.Drain(t, Ctx);

        Assert.Equal(2, t.Sent.Count);
        var add = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[0].Body).Deltas).Ops);
        Assert.Equal(Pl.Op.Types.Kind.Add, add.Kind);
        var rem = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[1].Body).Deltas).Ops);
        Assert.Equal(Pl.Op.Types.Kind.Rem, rem.Kind);
        Assert.True(rem.Rem.ItemsAsKey);
        Assert.DoesNotContain(store.Rootlist(), e => e.Uri == "spotify:playlist:x");
        Assert.False(store.IsSaved("playlists", "spotify:playlist:x"));
    }

    // ── 4c. keyed REM applies locally by uri (applier stays total); positions renumber contiguously on optimistic edits ──
    [Fact]
    public async Task KeyedRem_AppliesByUri_And_OptimisticEditsRenumber()
    {
        var list = new List<PlaylistMember> { new("i1", "spotify:playlist:a", null, 0), new("i2", "spotify:playlist:x", null, 0), new("i3", "spotify:playlist:b", null, 0) };
        PlaylistDiffApplier.Apply(list, new[] { new PlaylistOp(PlaylistOpKind.Remove, Items: new[] { new PlaylistMember("", "spotify:playlist:x", null, 0) }, ItemsAsKey: true) });
        Assert.Equal(new[] { "spotify:playlist:a", "spotify:playlist:b" }, list.ConvertAll(m => m.ItemUri));
        // absent uri → no-op, no throw
        PlaylistDiffApplier.Apply(list, new[] { new PlaylistOp(PlaylistOpKind.Remove, Items: new[] { new PlaylistMember("", "spotify:playlist:zzz", null, 0) }, ItemsAsKey: true) });
        Assert.Equal(2, list.Count);

        // optimistic follow/unfollow keep rootlist positions contiguous
        var store = new InMemoryStore();
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[] { "spotify:playlist:a", "spotify:playlist:b" }), Rev24(1));
        var host = await RootlistHost(store);
        var eng = host.Mutations;
        await eng.FollowAsync("spotify:playlist:x", true);
        var rl = store.Rootlist();
        for (int i = 0; i < rl.Count; i++) Assert.Equal(i, rl[i].Position);
        await eng.FollowAsync("spotify:playlist:a", false);
        rl = store.Rootlist();
        for (int i = 0; i < rl.Count; i++) Assert.Equal(i, rl[i].Position);
    }

    // ── 5. 409 → refetch base + leave pending; the next drain rebases + succeeds ──────────────────────────────────────
    [Fact]
    public async Task Replay_409_RefetchesConfirmedBase_BeforeNextAttempt()
    {
        var store = new InMemoryStore();
        store.SetRootlist([], Rev24(1));
        var clock = DateTime.UtcNow;
        var host = await RootlistHost(store, () => clock);
        var eng = host.Mutations;
        int posts = 0, gets = 0;
        var transport = new RecTransport((_, _, _, _) => ++posts == 1
            ? new Resp(false, [], 409) : new Resp(true, SlcRev(Rev24(3)), 200));
        var http = new FakeExchange((_, _) => { gets++; return SyncHarness.Ok(RootSnapshot(Rev24(2))); });
        var sync = host.AttachSync(http, transport);
        await eng.FollowAsync("spotify:playlist:x", true);
        await sync.DrainWritesAsync(Ct);
        Assert.Equal(1, eng.Pending);
        Assert.Equal(0, gets);
        Assert.Equal(Rev24(1), store.RootlistRevision());
        clock = clock.AddSeconds(2);
        await sync.DrainWritesAsync(Ct);
        Assert.Equal(1, gets);
        Assert.Equal(0, eng.Pending);
        Assert.Equal(2, posts);
        Assert.Equal(Rev24(2), Pl.ListChanges.Parser.ParseFrom(transport.Sent[1].Body).BaseRevision.ToByteArray());
        Assert.Equal(Rev24(3), store.RootlistRevision());
    }

    // ── 6. no stored rootlist revision → Replay bootstraps via GET first ─────────────────────────────────────────────
    [Fact]
    public async Task Replay_NoStoredRevision_BootstrapsViaGetFirst()
    {
        var store = new InMemoryStore();
        var host = await RootlistHost(store);
        var order = new List<string>();
        var transport = new RecTransport((route, method, body, call) =>
        { order.Add(method); return Ok200(route, method, body, call); });
        var http = new FakeExchange((request, _) =>
        {
            Assert.Contains("/rootlist", request.Url);
            order.Add("GET"); return SyncHarness.Ok(RootSnapshot(Rev24(5)));
        });
        var sync = host.AttachSync(http, transport);
        await host.Mutations.FollowAsync("spotify:playlist:x", true);
        await sync.DrainWritesAsync(Ct);
        Assert.Equal(["GET", "POST"], order);
        Assert.Equal(Rev24(5), Pl.ListChanges.Parser.ParseFrom(Assert.Single(transport.Sent).Body).BaseRevision.ToByteArray());
        Assert.Equal(0, host.Mutations.Pending);
    }

    // ── 7. Saved union fold: Bulk (rootlist fold) + incremental single-uri ───────────────────────────────────────────
    [Fact]
    public async Task SavedUnion_IncludesFollowedPlaylists_BulkAndIncremental()
    {
        await using var h = new SyncHarness(RootlistFoldResponder);
        using var src = new EngineMutationSource(h.Store, h.Mut, h.Sync);   // subscribed BEFORE the fold (Bulk path)

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
        await done.Task;

        Assert.Contains("spotify:playlist:p1", src.Saved);                 // folded via the Bulk StoreChange
        Assert.Contains("spotify:playlist:p2", src.Saved);

        await h.Host.SeedRootlistAsync(RootlistTreeBuilder.EntriesFromUris(["spotify:playlist:p1", "spotify:playlist:p2", "spotify:playlist:p3"]), Rev24(2));   // single-uri change
        Assert.Contains("spotify:playlist:p3", src.Saved);                 // incremental path
    }

    static HttpResp RootlistFoldResponder(HttpReq req)
    {
        if (req.Url.Contains("/rootlist"))
        {
            var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(1)) };
            var c = new Pl.ListItems { Pos = 0, Truncated = false };
            c.Items.Add(new Pl.Item { Uri = "spotify:playlist:p1" });
            c.Items.Add(new Pl.Item { Uri = "spotify:playlist:p2" });
            slc.Contents = c;
            return new HttpResp(200, new Dictionary<string, string>(), slc.ToByteArray());
        }
        if (req.Url.Contains("/collection/v2/paging"))
            return new HttpResp(200, new Dictionary<string, string>(), new Col.PageResponse { SyncToken = "t", NextPageToken = "" }.ToByteArray());
        return new HttpResp(200, new Dictionary<string, string>(), Array.Empty<byte>());
    }

    // ── 8. OpRebase response capture: a 200 SelectedListContent (plain + zstd) updates membership + revision ─────────
    [Fact]
    public async Task OpRebase_CapturesChangesResponse_UpdatesMembershipAndRevision_PlainAndZstd()
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(2)) };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:new", Attributes = new Pl.ItemAttributes { ItemId = ByteString.CopyFrom(Convert.FromHexString("0102030405060708")) } });
        slc.Contents = contents;
        var respBytes = slc.ToByteArray();

        foreach (var (label, body) in new[] { ("plain", respBytes), ("zstd", Zstd(respBytes)) })
        {
            var store = new InMemoryStore();
            store.SetMembership("spotify:playlist:p", new[] { new PlaylistMember("1112131415161718", "spotify:track:old", null, 0) }, Rev24(1));
            var host = await RootlistHost(store);
            await host.SeedPlaylistAsync("spotify:playlist:p", store.Membership("spotify:playlist:p"), Rev24(1));
            var transport = new RecTransport((_, _, _, _) => new Resp(true, body, 200));
            await host.Mutations.EditAsync("spotify:playlist:p",
                [new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: [new PlaylistMember("0102030405060708", "spotify:track:new", null, 0)])], Rev24(1));
            await host.Mutations.Drain(transport, host.Context, Ct);
            Assert.Equal(0, host.Mutations.Pending);
            Assert.Equal("spotify:track:new", Assert.Single(store.Membership("spotify:playlist:p")).ItemUri);   // response replaced membership (" + label + ")
            Assert.Equal(Rev24(2), store.PlaylistRevision("spotify:playlist:p"));                               // + advanced the revision
            Assert.True(label == "plain" || body[0] == 0x28);   // the zstd fixture really is a zstd frame
        }
    }

    static byte[] Zstd(byte[] data) { using var c = new ZstdSharp.Compressor(3); return c.Wrap(data).ToArray(); }

    // ── 9. dead-letter rollback: a rootlist op failing MaxAttempts rolls back the pill AND the optimistic entry ───────
    [Fact]
    public async Task DeadLetter_RollsBackPill_AndOptimisticRootlistEntry()
    {
        var store = new InMemoryStore();
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[] { "spotify:playlist:a" }), Rev24(1));
        var clock = DateTime.UtcNow;
        var host = await RootlistHost(store, () => clock);
        var eng = host.Mutations;

        await eng.FollowAsync("spotify:playlist:x", true);
        Assert.True(store.IsSaved("playlists", "spotify:playlist:x"));                 // optimistic pill
        Assert.Equal("spotify:playlist:x", store.Rootlist()[0].Uri);                    // optimistic entry at 0

        var t = new RecTransport((route, method, body, n) => new Resp(false, Array.Empty<byte>(), 429));   // explicitly retryable rate limit
        for (int i = 0; i < 12 && eng.Pending > 0; i++) { await eng.Drain(t, Ctx); clock = clock.AddSeconds(120); }

        Assert.Equal(0, eng.Pending);
        Assert.Single(eng.DeadLetter);
        Assert.False(store.IsSaved("playlists", "spotify:playlist:x"));                // pill rolled back
        Assert.DoesNotContain(store.Rootlist(), e => e.Uri == "spotify:playlist:x");   // optimistic entry undone
    }

    // ── 10. SqliteColdStore round-trips a "rootlist" op (Load after Save) ────────────────────────────────────────────
    [Fact]
    public async Task SqliteColdStore_RoundTrips_RootlistOp()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path))
            {
                await using var host = new ReplicaTestHost(persistence: cold);
                await host.SeedRootlistAsync([], Rev24(1));
                await host.Mutations.FollowAsync("spotify:playlist:x", true);
                Assert.Equal(1, host.Mutations.Pending);
            }
            using (var cold = new SqliteColdStore(path))
            {
                var persisted = await ((IReplicaPersistence)cold).LoadAsync(new ReplicaScope("bob", 1), Ct);
                await using var host = new ReplicaTestHost(persistence: cold, bootstrap: persisted);
                await host.Replicas.PublishInitialAsync(Ct);
                Assert.Equal(1, host.Mutations.Pending);
                Assert.True(host.Mutations.HasPending("playlists", "spotify:playlist:x"));
                Assert.Contains(host.Store.Rootlist(), entry => entry.Uri == "spotify:playlist:x");
            }
        }
        finally { TryDelete(path); }
    }

    // ── 11. I2: the outbox replay takes the SAME rootlist lane as the direct ops (move/delete/visibility/create) ──────
    // Two writers on the rootlist is how a positional MOV gets rebased against marker indices that moved underneath it.
    [Fact]
    public async Task RootlistFollow_SharesTheProtocolQueueWithDirectCommands()
    {
        var store = new InMemoryStore();
        store.SetRootlist([], Rev24(1));
        var host = await RootlistHost(store);
        var transport = new RecTransport(Ok200);
        var sync = host.AttachSync(NoReads(), transport);
        await host.Mutations.FollowAsync("spotify:playlist:x", true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var direct = sync.ExecuteRootlistAsync(async _ => { entered.TrySetResult(); await release.Task; }, Ct);
        await entered.Task;
        var drain = sync.DrainWritesAsync(Ct);
        Assert.False(drain.IsCompleted);
        Assert.Empty(transport.Sent);
        release.TrySetResult();
        await Task.WhenAll(direct, drain);
        Assert.Single(transport.Sent);
        Assert.Equal(0, host.Mutations.Pending);
    }
}
