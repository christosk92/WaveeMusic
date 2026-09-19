using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// The outbox is durable: pending intents persist to SQLite and a fresh engine over the same store replays them — so an
// offline save/edit survives a restart.
public class DurableOutboxTests
{
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    [Fact]
    public async Task SetSaves_SurviveRestart_AndReplay()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path))
            {
                var store = new CachedStore(cold);
                var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy() }, cold);
                eng.Save("liked", "spotify:track:a", true);
                eng.Save("albums", "spotify:album:b", true);
                Assert.Equal(2, eng.Pending);
            }   // dispose → outbox already durable (synchronous)

            using (var cold2 = new SqliteColdStore(path))
            {
                var store2 = new CachedStore(cold2);
                var eng2 = new MutationEngine(store2, new IMutationStrategy[] { new SetReplayStrategy() }, cold2);
                Assert.Equal(2, eng2.Pending);   // restored from disk on construction

                await eng2.Drain(new StubTransport(), SessionContext.LoggedOut);
                Assert.Equal(0, eng2.Pending);   // replayed + cleared (and removed from the durable outbox)
            }

            using (var cold3 = new SqliteColdStore(path))
            {
                var eng3 = new MutationEngine(new CachedStore(cold3), new IMutationStrategy[] { new SetReplayStrategy() }, cold3);
                Assert.Equal(0, eng3.Pending);   // the drained outbox stays empty across the next restart
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task PlaylistEdit_SurvivesRestart_WithOpsAndBaseRev()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path))
            {
                var store = new CachedStore(cold);
                store.SetMembership("spotify:playlist:p", new[] { new PlaylistMember("a", "spotify:track:a", null, 0), new PlaylistMember("b", "spotify:track:b", null, 0) }, new byte[] { 7 });
                var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()) }, cold);
                eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) }, new byte[] { 7 });
                Assert.Equal(1, eng.Pending);
            }

            using (var cold2 = new SqliteColdStore(path))
            {
                var store2 = new CachedStore(cold2);
                var eng2 = new MutationEngine(store2, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store2, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()) }, cold2);
                Assert.Equal(1, eng2.Pending);   // the op-rebase edit (ops + base_rev) round-tripped through SQLite
                await eng2.Drain(new StubTransport(), SessionContext.LoggedOut);
                Assert.Equal(0, eng2.Pending);
            }
        }
        finally { TryDelete(path); }
    }

    // ── P3: an offline create is a durable intent like any other ─────────────────────────────────────────────────────
    // The create carries its NAME as an UPDATE_LIST op through the same outbox.op blob column as a playlist edit, and
    // its rootlist ADD carries the FOLDER it belongs in (never an index — that moves while the op waits). Both have to
    // come back after a restart, or an offline "New playlist" is silently lost.
    [Fact]
    public void CreatePlaylist_PersistsThroughSqliteOutbox()
    {
        var path = TempDb();
        try
        {
            string uri = "spotify:playlist:37i9minted";
            using (var cold = new SqliteColdStore(path))
            {
                var store = new CachedStore(cold);
                var eng = new MutationEngine(store, new IMutationStrategy[]
                {
                    new CreatePlaylistStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()),
                    new RootlistFollowStrategy(store, new RootlistLane()),
                }, cold);
                eng.Create(uri, "Offline mix");
                eng.Follow(uri, true, "folder99");
                Assert.Equal(2, eng.Pending);
            }

            using (var cold2 = new SqliteColdStore(path))
            {
                var store2 = new CachedStore(cold2);
                var reloaded = cold2.Load();
                Assert.Equal(2, reloaded.Count);

                var create = Assert.Single(reloaded, o => o.Type == "create");
                Assert.Equal(uri, create.EntityKey);
                Assert.Equal("Offline mix", CreatePlaylistStrategy.NameOf(create.Ops));
                Assert.Equal(PlaylistRevisions.NewCreateBase(), create.BaseRev);   // the fixed 8-byte create base

                var follow = Assert.Single(reloaded, o => o.Type == "rootlist");
                Assert.Equal("folder99", follow.ParentFolderId);
                Assert.True(follow.Id > create.Id);                                // and it still drains AFTER the create

                var eng2 = new MutationEngine(store2, new IMutationStrategy[]
                {
                    new CreatePlaylistStrategy(store2, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()),
                    new RootlistFollowStrategy(store2, new RootlistLane()),
                }, cold2);
                Assert.Equal(2, eng2.Pending);
            }
        }
        finally { TryDelete(path); }
    }


    // ── P1: terminal failures dead-letter IMMEDIATELY (no 10-attempt burn) ────────────────────────────────────────────

    sealed class StatusTransport(int status) : ITransport
    {
        public int Calls;
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        { Calls++; return Task.FromResult(new Resp(status == 200, Array.Empty<byte>(), status)); }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    sealed class CollectingObserver(List<string> sink) : IObserver<string>
    {
        public void OnNext(string v) { lock (sink) sink.Add(v); }
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }
    // Row ids are real 16-hex item_ids (that is what the wire carries and what BuildChanges serializes), derived
    // deterministically from the readable name so assertions can still talk about "a"/"b"/"mine".
    static PlaylistMember M(string id) => new(HexId(id), "spotify:track:" + id, null, 0);
    static string HexId(string name)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in name) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    static MutationEngine EditEngine(IStore store) => new(store, new IMutationStrategy[]
    {
        new SetReplayStrategy(),
        new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()),
    });

    // 403 = we are not allowed to edit this list any more. Retrying nine more times only keeps the edit visibly
    // "pending" before failing anyway — dead-letter on the FIRST attempt and put the rows back.
    [Fact]
    public async Task Replay_403_DeadLettersImmediately_RollsBack()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        var eng = EditEngine(store);

        long edit = eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        Assert.Single(store.Membership("spotify:playlist:p"));   // optimistic

        var t = new StatusTransport(403);
        await eng.Drain(t, SessionContext.LoggedOut);

        Assert.Equal(1, t.Calls);                                // ONE attempt, not ten
        Assert.Equal(0, eng.Pending);
        Assert.Single(eng.DeadLetter);
        Assert.Equal(2, store.Membership("spotify:playlist:p").Count);   // rolled back to the pre-edit snapshot
        Assert.True(eng.TryTakeTerminal(edit, out var kind));
        Assert.Equal(PlaylistMutationFailure.Forbidden, kind);
    }

    [Fact]
    public async Task Replay_404_DeadLettersImmediately_AsDeleted()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var eng = EditEngine(store);

        long edit = eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        await eng.Drain(new StatusTransport(404), SessionContext.LoggedOut);

        Assert.Equal(0, eng.Pending);
        Assert.True(eng.TryTakeTerminal(edit, out var kind));
        Assert.Equal(PlaylistMutationFailure.Deleted, kind);
    }

    // A 409 is NOT terminal — it is the ordinary revision conflict, and it must still retry (rebased) next drain.
    [Fact]
    public async Task Replay_409_StaysQueued_ForRetry()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var eng = EditEngine(store);

        long edit = eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        await eng.Drain(new StatusTransport(409), SessionContext.LoggedOut);

        Assert.Equal(1, eng.Pending);
        Assert.False(eng.TryTakeTerminal(edit, out _));
    }

    // The tombstone latched on the header BEFORE the drain ran: the edit never even reaches the wire.
    [Fact]
    public async Task Edit_OnTombstonedPlaylist_DeadLettersImmediately()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "Mix", null, "bob", null, 2));
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        var eng = EditEngine(store);

        long edit = eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        store.UpsertPlaylist(store.GetPlaylist("spotify:playlist:p")! with { DeletedByOwner = true });

        var t = new StatusTransport(200);
        await eng.Drain(t, SessionContext.LoggedOut);

        Assert.Equal(0, t.Calls);                                        // never hit the wire
        Assert.Equal(0, eng.Pending);
        Assert.Equal(2, store.Membership("spotify:playlist:p").Count);   // rolled back
        Assert.True(eng.TryTakeTerminal(edit, out var kind));
        Assert.Equal(PlaylistMutationFailure.Deleted, kind);
    }

    // I3(b) — "add offline, reconnect, someone else edited, drain" must not visibly revert the add. A network snapshot
    // replace re-applies every still-pending op on top of the fresh rows.
    [Fact]
    public void SnapshotReplace_ReappliesPendingOps()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var eng = EditEngine(store);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("mine") }) });
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:mine" },
            store.Membership("spotify:playlist:p").Select(m => m.ItemUri).ToArray());

        // The server's fresh truth: someone else added "theirs" and our add has not landed yet.
        eng.AdoptSnapshot("spotify:playlist:p", new[] { M("a"), M("theirs") }, Rev24(5));

        Assert.Equal(new[] { "spotify:track:a", "spotify:track:theirs", "spotify:track:mine" },
            store.Membership("spotify:playlist:p").Select(m => m.ItemUri).ToArray());
        Assert.Equal(Rev24(5), store.PlaylistRevision("spotify:playlist:p"));
        Assert.Equal(1, eng.PendingFor("spotify:playlist:p"));   // still ours to send
    }

    // A pending op that no longer fits the fresh snapshot is TERMINAL — Conflict, not a silent skip and not a replay
    // against a list it cannot describe.
    [Fact]
    public void SnapshotReplace_TornPendingOp_DeadLettersWithConflict()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b"), M("c") }, Rev24(1));
        var eng = EditEngine(store);

        long edit = eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 2, Length: 1) });
        eng.AdoptSnapshot("spotify:playlist:p", new[] { M("a") }, Rev24(5));   // the list shrank under it

        Assert.Equal(0, eng.PendingFor("spotify:playlist:p"));
        Assert.Single(eng.DeadLetter);
        Assert.True(eng.TryTakeTerminal(edit, out var kind));
        Assert.Equal(PlaylistMutationFailure.Conflict, kind);
        Assert.Equal("spotify:track:a", Assert.Single(store.Membership("spotify:playlist:p")).ItemUri);
    }

    // I1 folded into the chokepoint: a snapshot whose revision is not the 24-byte head keeps the baseline we trust.
    [Fact]
    public void AdoptSnapshot_MalformedRevision_KeepsTheStoredOne()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var eng = EditEngine(store);

        eng.AdoptSnapshot("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 1, 2, 3 });

        Assert.Equal(2, store.Membership("spotify:playlist:p").Count);       // rows still land
        Assert.Equal(Rev24(1), store.PlaylistRevision("spotify:playlist:p")); // revision does not
    }

    // I6 — a keyed ADD is idempotent by item_id: re-applying an op whose row is ALREADY in the snapshot (our write DID
    // land, we just have not seen the ack) must not duplicate it.
    [Fact]
    public void SnapshotReplace_KeyedAddAlreadyPresent_IsIdempotent()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var eng = EditEngine(store);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("mine") }) });
        eng.AdoptSnapshot("spotify:playlist:p", new[] { M("a"), M("mine") }, Rev24(5));   // it landed after all

        Assert.Equal(new[] { "spotify:track:a", "spotify:track:mine" },
            store.Membership("spotify:playlist:p").Select(m => m.ItemUri).ToArray());
    }

    // ── I5: an index op is BASE-BOUND ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records every request body so the rebased wire shape can be inspected.</summary>
    sealed class RecordingTransport(int status) : ITransport
    {
        public readonly List<byte[]> Bodies = new();
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        { Bodies.Add(body.ToArray()); return Task.FromResult(new Resp(status == 200, Array.Empty<byte>(), status)); }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static Pl.Op SentOp(RecordingTransport t)
        => Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(Assert.Single(t.Bodies)).Deltas).Ops);

    // Build an insert against [a,b,c] that lands after "b", then let a foreign edit shift everything by one before the
    // drain. Replaying from_index=2 verbatim would drop the row in the wrong place; the recorded anchor re-finds "b".
    [Fact]
    public async Task Insert_RebasedAfterRemoteChange_RecomputesFromAnchor()
    {
        const string uri = "spotify:playlist:p";
        var store = new InMemoryStore();
        store.SetMembership(uri, new[] { M("a"), M("b"), M("c") }, Rev24(1));
        var eng = EditEngine(store);

        var insert = new PlaylistOp(PlaylistOpKind.Add, FromIndex: 2, Items: new[] { M("mine") },
            Anchor: new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, M("b").ItemId));
        eng.Edit(uri, new[] { insert }, Rev24(1));
        Assert.Equal(new[] { "a", "b", "mine", "c" },
            store.Membership(uri).Select(m => m.ItemUri.Replace("spotify:track:", "")).ToArray());

        // Someone else inserted a row at the head and the head moved: index 2 now names a different position.
        store.SetMembership(uri, new[] { M("x"), M("a"), M("b"), M("mine"), M("c") }, Rev24(5));

        var t = new RecordingTransport(200);
        await eng.Drain(t, SessionContext.LoggedOut);

        var sent = SentOp(t);
        Assert.Equal(Pl.Op.Types.Kind.Add, sent.Kind);
        Assert.Equal(3, sent.Add.FromIndex);          // recomputed: "b" sits at 2 now, so the row goes after it
        Assert.False(sent.Add.HasAddLast);
    }

    // The anchor row itself was deleted remotely. There is no honest position left, so the insert appends rather than
    // guessing an index — the row still reaches the playlist, which is what the user asked for.
    [Fact]
    public async Task Insert_RebasedWithVanishedAnchor_Appends()
    {
        const string uri = "spotify:playlist:p";
        var store = new InMemoryStore();
        store.SetMembership(uri, new[] { M("a"), M("b"), M("c") }, Rev24(1));
        var eng = EditEngine(store);

        eng.Edit(uri, new[]
        {
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 2, Items: new[] { M("mine") },
                Anchor: new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, M("b").ItemId)),
        }, Rev24(1));
        store.SetMembership(uri, new[] { M("a"), M("mine"), M("c") }, Rev24(5));   // "b" is gone

        var t = new RecordingTransport(200);
        await eng.Drain(t, SessionContext.LoggedOut);

        var sent = SentOp(t);
        Assert.True(sent.Add.HasAddLast && sent.Add.AddLast);
        Assert.False(sent.Add.HasFromIndex);
    }

    // An index REM whose rows all carry ids is re-expressed as the KEYED form on rebase — no index survives, so the
    // foreign edit cannot make it delete the wrong track.
    [Fact]
    public async Task IndexRemove_RebasedWithIds_BecomesKeyed()
    {
        const string uri = "spotify:playlist:p";
        var store = new InMemoryStore();
        store.SetMembership(uri, new[] { M("a"), M("b"), M("c") }, Rev24(1));
        var eng = EditEngine(store);

        eng.Edit(uri, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 1, Length: 1, Items: new[] { M("b") }),
        }, Rev24(1));
        store.SetMembership(uri, new[] { M("x"), M("a"), M("c") }, Rev24(5));

        var t = new RecordingTransport(200);
        await eng.Drain(t, SessionContext.LoggedOut);

        var sent = SentOp(t);
        Assert.Equal(Pl.Op.Types.Kind.Rem, sent.Kind);
        Assert.True(sent.Rem.ItemsAsKey);
        Assert.False(sent.Rem.HasFromIndex);
        Assert.Equal(M("b").ItemId, Golden.Hex(Assert.Single(sent.Rem.Items).Attributes.ItemId));
    }

    // …but a row with no id at all cannot be re-expressed. That is TERMINAL: roll the rows back and report Conflict,
    // never send a stale index.
    [Fact]
    public async Task IndexRemove_RebasedWithoutIds_DeadLettersWithConflict()
    {
        const string uri = "spotify:playlist:p";
        var store = new InMemoryStore();
        var unkeyed = new PlaylistMember("", "spotify:track:b", null, 0);
        store.SetMembership(uri, new[] { M("a"), unkeyed, M("c") }, Rev24(1));
        var eng = EditEngine(store);

        long edit = eng.Edit(uri, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 1, Length: 1, Items: new[] { unkeyed }),
        }, Rev24(1));
        Assert.Equal(2, store.Membership(uri).Count);                     // optimistic
        store.SetMembership(uri, new[] { M("x"), M("a"), M("c") }, Rev24(5));

        var t = new RecordingTransport(200);
        await eng.Drain(t, SessionContext.LoggedOut);

        Assert.Empty(t.Bodies);                                           // never reached the wire
        Assert.Equal(0, eng.Pending);
        Assert.True(eng.TryTakeTerminal(edit, out var kind));
        Assert.Equal(PlaylistMutationFailure.Conflict, kind);
    }

    // The anchor is not a wire field — it has to survive the SQLite outbox blob, or a restart turns every queued
    // insert back into a stale index.
    [Fact]
    public void Insert_AnchorPersistsThroughSqliteOutbox()
    {
        var path = TempDb();
        try
        {
            const string uri = "spotify:playlist:p";
            var anchor = new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, M("b").ItemId);
            using (var cold = new SqliteColdStore(path))
            {
                var store = new CachedStore(cold);
                store.SetMembership(uri, new[] { M("a"), M("b") }, Rev24(1));
                var eng = new MutationEngine(store, new IMutationStrategy[]
                {
                    new SetReplayStrategy(),
                    new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()),
                }, cold);
                eng.Edit(uri, new[]
                {
                    new PlaylistOp(PlaylistOpKind.Add, FromIndex: 2, Items: new[] { M("mine") }, Anchor: anchor),
                }, Rev24(1));
            }

            using var cold2 = new SqliteColdStore(path);
            var reloaded = Assert.Single(cold2.Load());
            var op = Assert.Single(reloaded.Ops!);
            Assert.Equal(PlaylistOpKind.Add, op.Kind);
            Assert.Equal(2, op.FromIndex);
            Assert.Equal(anchor, op.Anchor);
            Assert.Equal(Rev24(1), reloaded.BaseRev);

            // A First anchor ("at the head") round-trips as its own value, distinct from "no anchor recorded".
            using var cold3 = new SqliteColdStore(path);
            var head = PlaylistWireMapper.ParseOutboxBlob(PlaylistWireMapper.BuildOutboxBlob(Rev24(1), new[]
            {
                new PlaylistOp(PlaylistOpKind.Add, FromIndex: 0, Items: new[] { M("mine") },
                    Anchor: new PlaylistMoveAnchor(PlaylistMoveAnchorKind.First)),
            })).Ops;
            Assert.Equal(new PlaylistMoveAnchor(PlaylistMoveAnchorKind.First), Assert.Single(head).Anchor);
        }
        finally { TryDelete(path); }
    }

    // PendingChanged is what drives the per-playlist "syncing" chip: it fires on enqueue AND on ack.
    [Fact]
    public async Task PendingChanged_FiresOnEnqueueAndAck()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var eng = EditEngine(store);
        var seen = new List<string>();
        using var sub = eng.PendingChanged.Subscribe(new CollectingObserver(seen));

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        Assert.Equal(new[] { "spotify:playlist:p" }, seen.ToArray());
        Assert.Equal(1, eng.PendingFor("spotify:playlist:p"));

        await eng.Drain(new StatusTransport(200), SessionContext.LoggedOut);

        Assert.Equal(2, seen.Count);
        Assert.Equal(0, eng.PendingFor("spotify:playlist:p"));
    }
}
