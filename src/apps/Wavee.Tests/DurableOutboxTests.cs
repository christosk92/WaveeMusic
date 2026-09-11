using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Google.Protobuf;
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

    static async Task<ReplicaTestHost> Open(SqliteColdStore cold)
    {
        var bootstrap = await ((IReplicaPersistence)cold).LoadAsync(new ReplicaScope("bob", 1), TestContext.Current.CancellationToken);
        var host = new ReplicaTestHost(store: new InMemoryStore(), persistence: cold, bootstrap: bootstrap);
        await host.Replicas.PublishInitialAsync();
        return host;
    }

    [Fact]
    public async Task SetSaves_SurviveRestart_AndReplay()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path))
            {
                await using var host = await Open(cold);
                await host.Mutations.SaveAsync("liked", "spotify:track:a", true);
                await host.Mutations.SaveAsync("albums", "spotify:album:b", true);
                Assert.Equal(2, host.Mutations.Pending);
            }
            using (var cold = new SqliteColdStore(path))
            {
                await using var host = await Open(cold);
                Assert.Equal(2, host.Mutations.Pending);
                Assert.True(host.Store.IsSaved("liked", "spotify:track:a"));
                await host.Mutations.Drain(new StatusTransport(200), host.Context);
                Assert.Equal(0, host.Mutations.Pending);
            }
            using (var cold = new SqliteColdStore(path))
            {
                await using var host = await Open(cold);
                Assert.Equal(0, host.Mutations.Pending);
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
                await using var host = await Open(cold);
                await host.SeedPlaylistAsync("spotify:playlist:p", [M("a"), M("b")], Rev24(7));
                await host.Mutations.EditAsync("spotify:playlist:p",
                    [new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1)], Rev24(7));
                Assert.Equal(1, host.Mutations.Pending);
            }
            using (var cold = new SqliteColdStore(path))
            {
                await using var host = await Open(cold);
                await host.Replicas.EnsurePlaylistCachedAsync("spotify:playlist:p");
                var intent = Assert.Single(host.Replicas.Intents);
                Assert.Equal(Rev24(7), intent.BaseRev);
                Assert.Equal(PlaylistOpKind.Remove, Assert.Single(intent.Ops!).Kind);
                Assert.Equal("spotify:track:b", Assert.Single(host.Store.Membership("spotify:playlist:p")).ItemUri);
                await host.Mutations.Drain(new StatusTransport(200), host.Context);
                Assert.Equal(0, host.Mutations.Pending);
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task CreatePlaylist_PersistsThroughSqliteOutbox()
    {
        var path = TempDb();
        try
        {
            const string uri = "spotify:playlist:37i9minted";
            using (var cold = new SqliteColdStore(path))
            {
                await using var host = await Open(cold);
                await host.Mutations.CreateAsync(uri, "Offline mix",
                    new Playlist("37i9minted", uri, "Offline mix", null, "bob", null, 0), "folder99");
                Assert.Equal(2, host.Mutations.Pending);
            }
            using (var cold = new SqliteColdStore(path))
            {
                await using var host = await Open(cold);
                var reloaded = host.Replicas.Intents;
                Assert.Equal(2, reloaded.Length);
                var create = Assert.Single(reloaded, o => o.Type == "create");
                Assert.Equal(uri, create.EntityKey);
                Assert.Equal("Offline mix", CreatePlaylistStrategy.NameOf(create.Ops));
                Assert.Equal(PlaylistRevisions.NewCreateBase(), create.BaseRev);
                var follow = Assert.Single(reloaded, o => o.Type == "rootlist");
                Assert.Equal("folder99", follow.ParentFolderId);
                Assert.True(follow.Id > create.Id);
                Assert.Equal(2, host.Mutations.Pending);
            }
        }
        finally { TryDelete(path); }
    }

    sealed class StatusTransport(int status) : ITransport
    {
        public int Calls;
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        { Calls++; return Task.FromResult(new Resp(status == 200, status == 200 ? new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(9)) }.ToByteArray() : Array.Empty<byte>(), status)); }
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

    static async Task<ReplicaTestHost> EditHost(IStore store)
    {
        var host = new ReplicaTestHost(store: store);
        const string uri = "spotify:playlist:p";
        await host.SeedPlaylistAsync(uri, store.Membership(uri), store.PlaylistRevision(uri));
        return host;
    }

    // 403 = we are not allowed to edit this list any more. Retrying nine more times only keeps the edit visibly
    // "pending" before failing anyway — dead-letter on the FIRST attempt and put the rows back.
    [Fact]
    public async Task Replay_403_DeadLettersImmediately_RollsBack()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        long edit = await eng.EditAsync("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        Assert.Single(store.Membership("spotify:playlist:p"));   // optimistic

        var t = new StatusTransport(403);
        await eng.Drain(t, host.Context);

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
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        long edit = await eng.EditAsync("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        await eng.Drain(new StatusTransport(404), host.Context);

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
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        long edit = await eng.EditAsync("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        await eng.Drain(new StatusTransport(409), host.Context);

        Assert.Equal(1, eng.Pending);
        Assert.False(eng.TryTakeTerminal(edit, out _));
    }

    // The tombstone latched on the header BEFORE the drain ran: the edit never even reaches the wire.
    [Fact]
    public async Task Edit_OnTombstonedPlaylist_DeadLettersImmediately()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        await host.SeedHeaderAsync(new Playlist("p", "spotify:playlist:p", "Mix", null, "bob", null, 2));
        long edit = await eng.EditAsync("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        await host.SeedHeaderAsync(host.ReadHeader("spotify:playlist:p")! with { DeletedByOwner = true });

        var t = new StatusTransport(200);
        await eng.Drain(t, host.Context);

        Assert.Equal(0, t.Calls);                                        // never hit the wire
        Assert.Equal(0, eng.Pending);
        Assert.Equal(2, store.Membership("spotify:playlist:p").Count);   // rolled back
        Assert.True(eng.TryTakeTerminal(edit, out var kind));
        Assert.Equal(PlaylistMutationFailure.Deleted, kind);
    }

    // I3(b) — "add offline, reconnect, someone else edited, drain" must not visibly revert the add. A network snapshot
    // replace re-applies every still-pending op on top of the fresh rows.
    [Fact]
    public async Task SnapshotReplace_ReappliesPendingOps()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        await eng.EditAsync("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("mine") }) });
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:mine" },
            store.Membership("spotify:playlist:p").Select(m => m.ItemUri).ToArray());

        // The server's fresh truth: someone else added "theirs" and our add has not landed yet.
        await host.SeedPlaylistAsync("spotify:playlist:p", new[] { M("a"), M("theirs") }, Rev24(5));

        Assert.Equal(new[] { "spotify:track:a", "spotify:track:theirs", "spotify:track:mine" },
            store.Membership("spotify:playlist:p").Select(m => m.ItemUri).ToArray());
        Assert.Equal(Rev24(5), store.PlaylistRevision("spotify:playlist:p"));
        Assert.Equal(1, eng.PendingFor("spotify:playlist:p"));   // still ours to send
    }

    // A pending op that no longer fits the fresh snapshot is TERMINAL — Conflict, not a silent skip and not a replay
    // against a list it cannot describe.
    [Fact]
    public async Task SnapshotReplace_TornPendingOp_DeadLettersWithConflict()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b"), M("c") }, Rev24(1));
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        long edit = await eng.EditAsync("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 2, Length: 1) });
        await host.SeedPlaylistAsync("spotify:playlist:p", new[] { M("a") }, Rev24(5));   // the list shrank under it

        Assert.Equal(0, eng.PendingFor("spotify:playlist:p"));
        Assert.Single(eng.DeadLetter);
        Assert.True(eng.TryTakeTerminal(edit, out var kind));
        Assert.Equal(PlaylistMutationFailure.Conflict, kind);
        Assert.Equal("spotify:track:a", Assert.Single(store.Membership("spotify:playlist:p")).ItemUri);
    }

    // I1 folded into the chokepoint: a snapshot whose revision is not the 24-byte head keeps the baseline we trust.
    [Fact]
    public async Task AdoptSnapshot_MalformedRevision_ClearsReplayHeadUntilResync()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        await host.SeedPlaylistAsync("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 1, 2, 3 });

        Assert.Equal(2, store.Membership("spotify:playlist:p").Count);       // rows still land
        Assert.Null(store.PlaylistRevision("spotify:playlist:p"));
        Assert.Equal(ReplicaBaselineState.NeedsResync, host.Replicas.ReadConfirmedPlaylist("spotify:playlist:p").State);
    }

    // I6 — a keyed ADD is idempotent by item_id: re-applying an op whose row is ALREADY in the snapshot (our write DID
    // land, we just have not seen the ack) must not duplicate it.
    [Fact]
    public async Task SnapshotReplace_KeyedAddAlreadyPresent_IsIdempotent()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        await eng.EditAsync("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("mine") }) });
        await host.SeedPlaylistAsync("spotify:playlist:p", new[] { M("a"), M("mine") }, Rev24(5));   // it landed after all

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
        { Bodies.Add(body.ToArray()); return Task.FromResult(new Resp(status == 200, status == 200 ? new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(9)) }.ToByteArray() : Array.Empty<byte>(), status)); }
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
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        var insert = new PlaylistOp(PlaylistOpKind.Add, FromIndex: 2, Items: new[] { M("mine") },
            Anchor: new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, M("b").ItemId));
        await eng.EditAsync(uri, new[] { insert }, Rev24(1));
        Assert.Equal(new[] { "a", "b", "mine", "c" },
            store.Membership(uri).Select(m => m.ItemUri.Replace("spotify:track:", "")).ToArray());

        // Someone else inserted a row at the head and the head moved: index 2 now names a different position.
        await host.SeedPlaylistAsync(uri, new[] { M("x"), M("a"), M("b"), M("mine"), M("c") }, Rev24(5));

        var t = new RecordingTransport(200);
        await eng.Drain(t, host.Context);

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
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        await eng.EditAsync(uri, new[]
        {
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 2, Items: new[] { M("mine") },
                Anchor: new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, M("b").ItemId)),
        }, Rev24(1));
        await host.SeedPlaylistAsync(uri, new[] { M("a"), M("mine"), M("c") }, Rev24(5));   // "b" is gone

        var t = new RecordingTransport(200);
        await eng.Drain(t, host.Context);

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
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        await eng.EditAsync(uri, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 1, Length: 1, Items: new[] { M("b") }),
        }, Rev24(1));
        await host.SeedPlaylistAsync(uri, new[] { M("x"), M("a"), M("b"), M("c") }, Rev24(5));

        var t = new RecordingTransport(200);
        await eng.Drain(t, host.Context);

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
        await using var host = await EditHost(store);
        var eng = host.Mutations;

        long edit = await eng.EditAsync(uri, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 1, Length: 1, Items: new[] { unkeyed }),
        }, Rev24(1));
        Assert.Equal(2, store.Membership(uri).Count);                     // optimistic
        await host.SeedPlaylistAsync(uri, new[] { M("x"), M("a"), M("c") }, Rev24(5));

        var t = new RecordingTransport(200);
        await eng.Drain(t, host.Context);

        Assert.Empty(t.Bodies);                                           // never reached the wire
        Assert.Equal(0, eng.Pending);
        Assert.True(eng.TryTakeTerminal(edit, out var kind));
        Assert.Equal(PlaylistMutationFailure.Conflict, kind);
    }

    // The anchor is not a wire field — it has to survive the SQLite outbox blob, or a restart turns every queued
    // insert back into a stale index.
    [Fact]
    public async Task Insert_AnchorPersistsThroughSqliteOutbox()
    {
        var path = TempDb();
        try
        {
            const string uri = "spotify:playlist:p";
            var anchor = new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, M("b").ItemId);
            using (var cold = new SqliteColdStore(path))
            {
                await using var host = await Open(cold);
                await host.SeedPlaylistAsync(uri, [M("a"), M("b")], Rev24(1));
                var eng = host.Mutations;
                await eng.EditAsync(uri, new[]
                {
                    new PlaylistOp(PlaylistOpKind.Add, FromIndex: 2, Items: new[] { M("mine") }, Anchor: anchor),
                }, Rev24(1));
            }

            using var cold2 = new SqliteColdStore(path);
            var state = await ((IReplicaPersistence)cold2).LoadAsync(new ReplicaScope("bob", 1), TestContext.Current.CancellationToken);
            var reloaded = Assert.Single(state.Intents);
            var op = Assert.Single(reloaded.Ops!);
            Assert.Equal(PlaylistOpKind.Add, op.Kind);
            Assert.Equal(2, op.FromIndex);
            Assert.Equal(anchor, op.Anchor);
            Assert.Equal(Rev24(1), reloaded.BaseRev);

            // A First anchor ("at the head") round-trips as its own value, distinct from "no anchor recorded".
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
        await using var host = await EditHost(store);
        var eng = host.Mutations;
        var seen = new List<string>();
        using var sub = eng.PendingChanged.Subscribe(new CollectingObserver(seen));

        await eng.EditAsync("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        Assert.Equal(new[] { "spotify:playlist:p" }, seen.ToArray());
        Assert.Equal(1, eng.PendingFor("spotify:playlist:p"));

        await eng.Drain(new StatusTransport(200), host.Context);

        Assert.Equal(2, seen.Count);
        Assert.Equal(0, eng.PendingFor("spotify:playlist:p"));
    }

    // ── Finding #2 (library-v3-1-findings-2026-09-06.md Part 2 2.1 #2) ──────────────────────────────────────────────
    // Transport failures on a queued write must stay Pending with backoff, never become NeedsAttention.

    [Theory]
    [InlineData(0, false, MutationReplyClass.Retry)]      // no response reached the wire
    [InlineData(500, false, MutationReplyClass.Retry)]
    [InlineData(503, false, MutationReplyClass.Retry)]
    [InlineData(429, false, MutationReplyClass.Retry)]
    [InlineData(409, false, MutationReplyClass.Verify)]   // conflict: an earlier attempt may already have landed
    [InlineData(400, false, MutationReplyClass.Rejected)]
    [InlineData(403, false, MutationReplyClass.Rejected)]
    [InlineData(404, false, MutationReplyClass.Rejected)]
    [InlineData(412, false, MutationReplyClass.Rejected)]
    [InlineData(200, true, MutationReplyClass.Retry)]     // an exception (no confirmed response) always wins
    public void MutationReplyRules_Classify_Table(int status, bool withException, MutationReplyClass expected)
        => Assert.Equal(expected, MutationReplyRules.Classify(status, withException ? new HttpRequestException("offline") : null));

    // Only a transport-class exception is ambiguous. A deterministic failure on our side (the op cannot even be
    // encoded) means the request was never sent, so it must be rejected with its reason, never retried as a fault.
    [Theory]
    [InlineData(typeof(HttpRequestException), MutationReplyClass.Retry)]
    [InlineData(typeof(System.IO.IOException), MutationReplyClass.Retry)]
    [InlineData(typeof(TimeoutException), MutationReplyClass.Retry)]
    [InlineData(typeof(FormatException), MutationReplyClass.Rejected)]
    [InlineData(typeof(ArgumentException), MutationReplyClass.Rejected)]
    [InlineData(typeof(InvalidOperationException), MutationReplyClass.Rejected)]
    public void MutationReplyRules_OnlyTransportExceptionsRetry(Type exceptionType, MutationReplyClass expected)
        => Assert.Equal(expected, MutationReplyRules.Classify(0, (Exception)Activator.CreateInstance(exceptionType, "boom")!));

    [Fact]
    public void MutationReplyRules_ATransportFailureWrappedInAnotherException_StillRetries()
        => Assert.Equal(MutationReplyClass.Retry, MutationReplyRules.Classify(0, new InvalidOperationException("wrapped", new HttpRequestException("offline"))));

    // A transport that throws (offline, DNS, socket reset) must never short-circuit to Verify/NeedsAttention — it
    // stays Pending, attempts/backoff climb, and once the transport recovers the queued write is finally sent.
    sealed class FlakyTransport(int failCount) : ITransport
    {
        public int Calls;
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Calls++;
            if (Calls <= failCount) throw new HttpRequestException("offline");
            return Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    [Fact]
    public async Task Save_TransportThrows_StaysPendingWithBackoff_ThenSendsOnceOnline()
    {
        var store = new InMemoryStore();
        var clock = DateTime.UtcNow;
        await using var host = new ReplicaTestHost(account: "bob", store: store, clock: () => clock);
        var eng = host.Mutations;
        await eng.SaveAsync("liked", "spotify:track:a", true);

        var flaky = new FlakyTransport(failCount: 2);

        await eng.Drain(flaky, host.Context);
        clock = clock.AddSeconds(5);   // past the min(60, 2^attempts) backoff every time
        Assert.Equal(1, eng.Pending);
        var afterFirst = Assert.Single(host.Replicas.Intents);
        Assert.Equal(ReplicaIntentState.Pending, afterFirst.State);   // never Verify/NeedsAttention
        Assert.Equal(1, afterFirst.Attempts);

        await eng.Drain(flaky, host.Context);
        clock = clock.AddSeconds(5);
        Assert.Equal(1, eng.Pending);
        Assert.Equal(2, Assert.Single(host.Replicas.Intents).Attempts);

        await eng.Drain(flaky, host.Context);   // the transport is back — the queued like finally reaches the wire
        Assert.Equal(0, eng.Pending);
        Assert.True(store.IsSaved("liked", "spotify:track:a"));
        Assert.Equal(3, flaky.Calls);
    }

    // The 3-verification-attempt escalation to NeedsAttention is legitimate for a genuine (non-offline) verification
    // that keeps finding the write unresolved — e.g. a create's minted-uri 409 (CreatePlaylistStrategy: "may already
    // have landed, verify instead of minting again"). Only an OFFLINE verification fetch must not count (LibrarySync
    // VerifyIntentAsync — see LibrarySyncTests).
    [Fact]
    public async Task Create_DefinitiveConflict_ThreeGenuineVerifications_BecomesNeedsAttention()
    {
        const string uri = "spotify:playlist:minted1";
        await using var host = new ReplicaTestHost(account: "bob");
        var (createId, _) = await host.Mutations.CreateAsync(uri, "Mine", new Playlist("minted1", uri, "Mine", null, "bob", null, 0));

        await host.Mutations.Drain(new StatusTransport(409), host.Context);   // CreatePlaylistStrategy: 409 → Verify
        Assert.Equal(ReplicaIntentState.AwaitingVerification, host.Replicas.Intents.Single(x => x.Id == createId).State);

        for (int i = 0; i < 2; i++)
        {
            await host.Mutations.VerificationFailedAsync(host.Replicas.Intents.Single(x => x.Id == createId), TestContext.Current.CancellationToken);
            Assert.Equal(ReplicaIntentState.AwaitingVerification, host.Replicas.Intents.Single(x => x.Id == createId).State);
        }
        await host.Mutations.VerificationFailedAsync(host.Replicas.Intents.Single(x => x.Id == createId), TestContext.Current.CancellationToken);
        Assert.Equal(ReplicaIntentState.NeedsAttention, host.Replicas.Intents.Single(x => x.Id == createId).State);
    }
}
