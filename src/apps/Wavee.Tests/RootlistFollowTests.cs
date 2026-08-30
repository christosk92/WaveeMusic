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
public class RootlistFollowTests
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

    static Resp Ok200(string route, string method, byte[] body, int call) => new(true, Array.Empty<byte>(), 200);
    static byte[] SlcRev(byte[] rev) => new Pl.SelectedListContent { Revision = ByteString.CopyFrom(rev) }.ToByteArray();
    // I1: a revision only enters the store when it is the real 24-byte playlist4 head, so every response fixture
    // that is meant to be ADOPTED has to carry one (a short stand-in is now refused and the old value kept).
    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }
    static MutationEngine RootlistEngine(IStore store, Func<DateTime>? now = null)
        => new(store, new IMutationStrategy[] { new SetReplayStrategy(), new RootlistFollowStrategy(store, new RootlistLane()) }, null, now);

    static string TempDb() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { System.IO.File.Delete(f); } catch { } } }

    // ── 1. routing + optimistic ──────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Follow_RoutesToRootlistOp_NeverLikedSet_AndAppliesOptimistically()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Array.Empty<RootlistEntry>(), new byte[] { 9 });   // seed a rev so drain needs no bootstrap
        var eng = RootlistEngine(store);
        var t = new RecTransport(Ok200);
        var src = new EngineMutationSource(store, eng, t, () => Ctx);

        // optimistic (no drain): a "rootlist"-typed pending op, the pill flips, the entry lands at position 0 — NOT the liked set.
        eng.Follow("spotify:playlist:x", true);
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
    public void FollowThenUnfollow_BeforeDrain_CoalescesToOneOp_LatestWins()
    {
        var store = new InMemoryStore();
        var eng = RootlistEngine(store);

        eng.Follow("spotify:playlist:x", true);
        eng.Follow("spotify:playlist:x", false);   // toggle back before any drain

        Assert.Equal(1, eng.Pending);                                         // coalesced — toggles don't stack
        Assert.False(store.IsSaved("playlists", "spotify:playlist:x"));       // latest end-state = unfollowed (optimistic)
    }

    // ── 3. Replay POSTs rootlist/changes with the first-party headers + the full body shape ──────────────────────────
    [Fact]
    public async Task Replay_Follow_PostsRootlistChanges_WithHeadersAndBody()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Array.Empty<RootlistEntry>(), new byte[] { 9, 9 });   // rev present → single POST, no bootstrap
        var strat = new RootlistFollowStrategy(store, new RootlistLane());
        var t = new RecTransport(Ok200);

        var ok = await strat.Replay(new OutboxOp(1, "rootlist", "spotify:playlist:x", "playlists", true, 1, 0), t, Ctx, Ct);

        Assert.True(ok);
        var req = Assert.Single(t.Sent);
        Assert.Equal("/playlist/v2/user/bob/rootlist/changes", req.Route);
        Assert.Equal("POST", req.Method);
        Assert.Equal("application/x-www-form-urlencoded", req.Headers!["Content-Type"]);
        Assert.Equal("CAk=", req.Headers!["spotify-playlist-sync-reason"]);
        Assert.Equal("dummy", req.Headers!["spotify-accept-geoblock"]);
        Assert.Equal("false", req.Headers!["spotify-dsa-mode-enabled"]);

        var lc = Pl.ListChanges.Parser.ParseFrom(req.Body);
        Assert.Equal(new byte[] { 9, 9 }, lc.BaseRevision.ToByteArray());     // base_revision = the stored rootlist rev
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
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[] { "spotify:playlist:a", "spotify:playlist:b", "spotify:playlist:x" }), new byte[] { 1 });
        store.SetSaved("playlists", "spotify:playlist:x", true, SyncState.Confirmed);
        var eng = RootlistEngine(store);
        var t = new RecTransport(Ok200);

        eng.Follow("spotify:playlist:x", false);                              // optimistic: the local row is removed HERE
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
        store.SetRootlist(Array.Empty<RootlistEntry>(), new byte[] { 1 });
        var eng = RootlistEngine(store);
        var t = new RecTransport(Ok200);

        eng.Follow("spotify:playlist:x", true);
        await eng.Drain(t, Ctx);
        eng.Follow("spotify:playlist:x", false);
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
    public void KeyedRem_AppliesByUri_And_OptimisticEditsRenumber()
    {
        var list = new List<PlaylistMember> { new("i1", "spotify:playlist:a", null, 0), new("i2", "spotify:playlist:x", null, 0), new("i3", "spotify:playlist:b", null, 0) };
        PlaylistDiffApplier.Apply(list, new[] { new PlaylistOp(PlaylistOpKind.Remove, Items: new[] { new PlaylistMember("", "spotify:playlist:x", null, 0) }, ItemsAsKey: true) });
        Assert.Equal(new[] { "spotify:playlist:a", "spotify:playlist:b" }, list.ConvertAll(m => m.ItemUri));
        // absent uri → no-op, no throw
        PlaylistDiffApplier.Apply(list, new[] { new PlaylistOp(PlaylistOpKind.Remove, Items: new[] { new PlaylistMember("", "spotify:playlist:zzz", null, 0) }, ItemsAsKey: true) });
        Assert.Equal(2, list.Count);

        // optimistic follow/unfollow keep rootlist positions contiguous
        var store = new InMemoryStore();
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[] { "spotify:playlist:a", "spotify:playlist:b" }), new byte[] { 1 });
        var eng = RootlistEngine(store);
        eng.Follow("spotify:playlist:x", true);
        var rl = store.Rootlist();
        for (int i = 0; i < rl.Count; i++) Assert.Equal(i, rl[i].Position);
        eng.Follow("spotify:playlist:a", false);
        rl = store.Rootlist();
        for (int i = 0; i < rl.Count; i++) Assert.Equal(i, rl[i].Position);
    }

    // ── 5. 409 → refetch base + leave pending; the next drain rebases + succeeds ──────────────────────────────────────
    [Fact]
    public async Task Replay_409_RefetchesBase_ThenNextDrainSucceeds()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Array.Empty<RootlistEntry>(), new byte[] { 1 });   // stale base
        var clock = DateTime.UtcNow;
        var eng = RootlistEngine(store, () => clock);
        int posts = 0, gets = 0;
        var t = new RecTransport((route, method, body, n) =>
        {
            if (method == "GET") { gets++; return new Resp(true, SlcRev(Rev24(2)), 200); }   // refetch → fresh 24-B rev
            posts++;
            return posts == 1 ? new Resp(false, Array.Empty<byte>(), 409) : new Resp(true, Array.Empty<byte>(), 200);
        });

        eng.Follow("spotify:playlist:x", true);
        await eng.Drain(t, Ctx);                                   // POST→409 → GET refetch → false; op stays pending
        Assert.Equal(1, eng.Pending);
        Assert.Equal(1, gets);
        Assert.Equal(Rev24(2), store.RootlistRevision());          // base advanced to the fresh rev

        clock = clock.AddSeconds(2);                               // past the §8.3 backoff
        await eng.Drain(t, Ctx);                                   // POST(base {2})→200 → success
        Assert.Equal(0, eng.Pending);
        Assert.Equal(2, posts);
        var last = Pl.ListChanges.Parser.ParseFrom(t.Sent[^1].Body);
        Assert.Equal(Rev24(2), last.BaseRevision.ToByteArray());   // rebased against the fresh base
    }

    // ── 6. no stored rootlist revision → Replay bootstraps via GET first ─────────────────────────────────────────────
    [Fact]
    public async Task Replay_NoStoredRevision_BootstrapsViaGetFirst()
    {
        var store = new InMemoryStore();   // no rootlist revision
        var strat = new RootlistFollowStrategy(store, new RootlistLane());
        var t = new RecTransport((route, method, body, n) => method == "GET" ? new Resp(true, SlcRev(Rev24(5)), 200) : new Resp(true, Array.Empty<byte>(), 200));

        await strat.Replay(new OutboxOp(1, "rootlist", "spotify:playlist:x", "playlists", true, 1, 0), t, Ctx, Ct);

        Assert.Equal(2, t.Sent.Count);
        Assert.Equal("GET", t.Sent[0].Method);                              // bootstrap first
        Assert.Contains("/playlist/v2/user/bob/rootlist?decorate=revision", t.Sent[0].Route);
        Assert.Equal("POST", t.Sent[1].Method);
        Assert.Equal(Rev24(5), Pl.ListChanges.Parser.ParseFrom(t.Sent[1].Body).BaseRevision.ToByteArray());   // used the bootstrapped base
    }

    // ── 7. Saved union fold: Bulk (rootlist fold) + incremental single-uri ───────────────────────────────────────────
    [Fact]
    public async Task SavedUnion_IncludesFollowedPlaylists_BulkAndIncremental()
    {
        await using var h = new SyncHarness(RootlistFoldResponder);
        var src = new EngineMutationSource(h.Store, h.Mut, h.Dealer, () => Ctx);   // subscribed BEFORE the fold (Bulk path)

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
        await done.Task;

        Assert.Contains("spotify:playlist:p1", src.Saved);                 // folded via the Bulk StoreChange
        Assert.Contains("spotify:playlist:p2", src.Saved);

        h.Store.SetSaved("playlists", "spotify:playlist:p3", true, SyncState.Confirmed);   // single-uri change
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
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:new" });
        slc.Contents = contents;
        var respBytes = slc.ToByteArray();

        foreach (var (label, body) in new[] { ("plain", respBytes), ("zstd", Zstd(respBytes)) })
        {
            var store = new InMemoryStore();
            store.SetMembership("spotify:playlist:p", new[] { new PlaylistMember("old", "spotify:track:old", null, 0) }, new byte[] { 1 });
            var strat = new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue());
            var t = new RecTransport((route, method, b, n) => new Resp(true, body, 200));
            var op = new OutboxOp(1, "oprebase", "spotify:playlist:p", "spotify:playlist:p", false, 1, 0,
                new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { new PlaylistMember("", "spotify:track:new", null, 0) }) }, new byte[] { 1 });

            var ok = await strat.Replay(op, t, Ctx, Ct);

            Assert.True(ok);
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
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[] { "spotify:playlist:a" }), new byte[] { 1 });
        var clock = DateTime.UtcNow;
        var eng = RootlistEngine(store, () => clock);

        eng.Follow("spotify:playlist:x", true);
        Assert.True(store.IsSaved("playlists", "spotify:playlist:x"));                 // optimistic pill
        Assert.Equal("spotify:playlist:x", store.Rootlist()[0].Uri);                    // optimistic entry at 0

        var t = new RecTransport((route, method, body, n) => new Resp(false, Array.Empty<byte>(), 500));   // always fails (not 409)
        for (int i = 0; i < 12 && eng.Pending > 0; i++) { await eng.Drain(t, Ctx); clock = clock.AddSeconds(120); }

        Assert.Equal(0, eng.Pending);
        Assert.Single(eng.DeadLetter);
        Assert.False(store.IsSaved("playlists", "spotify:playlist:x"));                // pill rolled back
        Assert.DoesNotContain(store.Rootlist(), e => e.Uri == "spotify:playlist:x");   // optimistic entry undone
    }

    // ── 10. SqliteColdStore round-trips a "rootlist" op (Load after Save) ────────────────────────────────────────────
    [Fact]
    public void SqliteColdStore_RoundTrips_RootlistOp()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path))
            {
                var store = new CachedStore(cold);
                var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy(), new RootlistFollowStrategy(store, new RootlistLane()) }, cold);
                eng.Follow("spotify:playlist:x", true);
                Assert.Equal(1, eng.Pending);
            }
            using (var cold2 = new SqliteColdStore(path))
            {
                var store2 = new CachedStore(cold2);
                var eng2 = new MutationEngine(store2, new IMutationStrategy[] { new SetReplayStrategy(), new RootlistFollowStrategy(store2, new RootlistLane()) }, cold2);
                Assert.Equal(1, eng2.Pending);                                     // the rootlist op restored from SQLite
                Assert.True(eng2.HasPending("playlists", "spotify:playlist:x"));   // with the rootlist|playlists|{uri} shape intact
            }
        }
        finally { TryDelete(path); }
    }

    // ── 11. I2: the outbox replay takes the SAME rootlist lane as the direct ops (move/delete/visibility/create) ──────
    // Two writers on the rootlist is how a positional MOV gets rebased against marker indices that moved underneath it.
    [Fact]
    public async Task RootlistFollow_TakesRootlistLane()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Array.Empty<RootlistEntry>(), Rev24(1));
        var lane = new RootlistLane();
        var strat = new RootlistFollowStrategy(store, lane);
        var t = new RecTransport(Ok200);

        await lane.WaitAsync(Ct);                                   // a direct rootlist op holds the lane
        var replay = strat.Replay(new OutboxOp(1, "rootlist", "spotify:playlist:x", "playlists", true, 1, 0), t, Ctx, Ct);
        await Task.Delay(50, Ct);
        Assert.False(replay.IsCompleted);                            // blocked — no interleaved write
        Assert.Empty(t.Sent);

        lane.Release();
        Assert.True(await replay);                                   // released → the POST goes out
        Assert.Single(t.Sent);
    }
}
