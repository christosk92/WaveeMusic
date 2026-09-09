using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// Playlist edits as durable, op-capable outbox intents: optimistic membership apply, replay via /changes, and a
// snapshot-based rollback on terminal failure. Distinct outbox rows per edit (append, never coalesced).
public class MutationOpRebaseTests
{
    sealed class ScriptedTransport(Func<string, Resp> respond) : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null) => Task.FromResult(respond(route));
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default) => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    // Row ids are real 16-hex item_ids (that is what the wire carries and what BuildChanges serializes), derived
    // deterministically from the readable name so assertions can still talk about "a"/"b"/"mine".
    static PlaylistMember M(string id) => new(HexId(id), "spotify:track:" + id, null, 0);
    static string HexId(string name)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in name) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }
    static MutationEngine Engine(IStore store) => new(store, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()) });

    [Fact]
    public async Task Edit_AppliesOptimistically_AndDrainConfirms()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, null);
        var eng = Engine(store);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        Assert.Equal("spotify:track:b", Assert.Single(store.Membership("spotify:playlist:p")).ItemUri);   // optimistic

        string? route = null;
        var t = new ScriptedTransport(r => { route = r; return new Resp(true, Array.Empty<byte>(), 200); });
        await eng.Drain(t, SessionContext.LoggedOut);

        Assert.Equal(0, eng.Pending);
        Assert.Contains("/playlist/v2/playlist/p/changes", route);   // POSTed the change to the right route
        Assert.Single(store.Membership("spotify:playlist:p"));        // stays applied after confirm
    }

    [Fact]
    public async Task Edit_RollsBackMembership_ToSnapshot_OnTerminalFailure()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 1 });
        var clock = DateTime.UtcNow;
        var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()) }, null, () => clock);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 2) });   // remove all
        Assert.Empty(store.Membership("spotify:playlist:p"));

        var t = new ScriptedTransport(_ => new Resp(false, Array.Empty<byte>(), 409));   // always fails
        for (int i = 0; i < 10; i++) { await eng.Drain(t, SessionContext.LoggedOut); clock = clock.AddSeconds(120); }   // advance past the §8.3 backoff → exhaust MaxAttempts

        Assert.Equal(0, eng.Pending);
        Assert.Single(eng.DeadLetter);
        var m = store.Membership("spotify:playlist:p");
        Assert.Equal(2, m.Count);                       // restored to the pre-edit snapshot
        Assert.Equal("spotify:track:a", m[0].ItemUri);
    }

    [Fact]
    public async Task Edits_AreAppended_NotCoalesced()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b"), M("c") }, null);
        var eng = Engine(store);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });   // -> b,c
        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });   // -> c
        Assert.Equal(2, eng.Pending);   // two distinct rows, not one coalesced row
        Assert.Equal("spotify:track:c", Assert.Single(store.Membership("spotify:playlist:p")).ItemUri);

        await eng.Drain(new ScriptedTransport(_ => new Resp(true, Array.Empty<byte>(), 200)), SessionContext.LoggedOut);
        Assert.Equal(0, eng.Pending);
    }

    // ── I4: never advance a revision past ops you did not apply ───────────────────────────────────────────────────────

    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }

    static Pl.Op WireAdd(string uri, string itemId)
    {
        var add = new Pl.Add { AddLast = true };
        add.Items.Add(new Pl.Item { Uri = uri, Attributes = new Pl.ItemAttributes { ItemId = ByteString.CopyFrom(Convert.FromHexString(itemId)) } });
        return new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = add };
    }

    static byte[] ChangesResponse(byte[]? resultingRevision, Pl.Op[]? syncOps = null,
                                  bool multipleHeads = false, bool requiresResync = false)
    {
        var slc = new Pl.SelectedListContent { MultipleHeads = multipleHeads, ChangesRequireResync = requiresResync };
        if (resultingRevision is not null) slc.ResultingRevisions.Add(ByteString.CopyFrom(resultingRevision));
        if (syncOps is not null)
        {
            var diff = new Pl.Diff { FromRevision = ByteString.CopyFrom(Rev24(1)), ToRevision = ByteString.CopyFrom(resultingRevision ?? Rev24(2)) };
            diff.Ops.AddRange(syncOps);
            slc.SyncResult = diff;
        }
        return slc.ToByteArray();
    }

    // sync_result carries the ops the server ACTUALLY applied (ours rebased onto whatever else landed). They must be
    // folded into the local list BEFORE the new head is adopted — adopting first would make the next dealer echo
    // parent-match against rows that were never updated.
    [Fact]
    public async Task Changes200_WithSyncResult_AppliesOpsBeforeAdvancingRevision()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        var eng = Engine(store);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });   // -> [b]
        var body = ChangesResponse(Rev24(2), new[] { WireAdd("spotify:track:z", "0f") });
        await eng.Drain(new ScriptedTransport(_ => new Resp(true, body, 200)), SessionContext.LoggedOut);

        var m = store.Membership("spotify:playlist:p");
        Assert.Equal(new[] { "spotify:track:b", "spotify:track:z" }, m.Select(x => x.ItemUri).ToArray());
        Assert.Equal(Rev24(2), store.PlaylistRevision("spotify:playlist:p"));   // resulting_revisions[^1]
        Assert.Equal(0, eng.Pending);
    }

    // The server could not express the result against our base. Do NOT advance the revision (we do not know what the
    // list is now) — queue the uri for the sync loop to revalidate right after the drain.
    [Fact]
    public async Task Changes200_MultipleHeads_MarksDirtyNotAdvance()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        var resync = new PlaylistResyncQueue();
        var eng = new MutationEngine(store, new IMutationStrategy[]
        {
            new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", resync),
        });

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        var body = ChangesResponse(Rev24(9), multipleHeads: true);
        await eng.Drain(new ScriptedTransport(_ => new Resp(true, body, 200)), SessionContext.LoggedOut);

        Assert.Equal(Rev24(1), store.PlaylistRevision("spotify:playlist:p"));   // head NOT adopted
        Assert.Equal("spotify:playlist:p", Assert.Single(resync.TakeAll()));    // queued for a full revalidation
    }

    // ASSUMPTION FLAGGED (playlist-sync-convergence plan §2.7, not yet re-validated by a live capture): this pins
    // field 20 (changes_require_resync) as meaning "the accepted delta cannot be expressed against your base —
    // refetch", per the 2026-08-15 capture cited at SpotifyLive/Protos/playlist4_external.proto:222-225. Two readings
    // stay open — (i) set only when the base is missing/stale (the one this test encodes), or (ii) set on every
    // /changes reply, in which case every edit costs a full GET and the fix is to adopt resulting_revisions[^1] when
    // the reply also carries a well-formed head and our own ops are the only delta. Do not change this assertion
    // until that capture exists; if reading (ii) holds, add the adopt-head arm and a second test for it.
    [Fact]
    public async Task Changes200_ChangesRequireResync_MarksDirtyNotAdvance()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var resync = new PlaylistResyncQueue();
        var eng = new MutationEngine(store, new IMutationStrategy[]
        {
            new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", resync),
        });

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("q") }) });
        var body = ChangesResponse(Rev24(9), requiresResync: true);
        await eng.Drain(new ScriptedTransport(_ => new Resp(true, body, 200)), SessionContext.LoggedOut);

        Assert.Equal(Rev24(1), store.PlaylistRevision("spotify:playlist:p"));
        Assert.Equal(PlaylistResyncQueue.Phase.Marked, resync.Get("spotify:playlist:p").Phase);
        Assert.Equal("spotify:playlist:p", Assert.Single(resync.TakeAll()));
    }

    // §2.1 — a 2xx /changes reply with NO body at all cannot be folded: neither a revision, nor ops, nor contents.
    // Never a resync mark (there is nothing "torn" to revalidate) — just a stored revision that stays where it was.
    // The observable in production is the ChangesEmptyBody Warn log; here we pin the store/queue state it protects.
    [Fact]
    public async Task Changes200_EmptyBody_DoesNotAdvance_AndDoesNotMark()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var resync = new PlaylistResyncQueue();
        var eng = new MutationEngine(store, new IMutationStrategy[]
        {
            new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", resync),
        });

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("q") }) });
        await eng.Drain(new ScriptedTransport(_ => new Resp(true, Array.Empty<byte>(), 200)), SessionContext.LoggedOut);

        Assert.Equal(Rev24(1), store.PlaylistRevision("spotify:playlist:p"));   // unchanged — nothing to adopt
        Assert.Equal(PlaylistResyncQueue.Phase.None, resync.Get("spotify:playlist:p").Phase);   // not a torn reply — not marked
    }

    // A parseable /changes reply carrying neither resulting_revisions nor a sync_result/contents: the fold has nothing
    // to adopt and nothing to apply. Same consequence as the empty-body case — the revision cannot advance — but this
    // path is NOT torn either, so it is not queued for revalidation (the new ChangesRevisionMissing Warn covers it).
    [Fact]
    public async Task Changes200_NoResultingRevision_DoesNotAdvance()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var resync = new PlaylistResyncQueue();
        var eng = new MutationEngine(store, new IMutationStrategy[]
        {
            new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", resync),
        });

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("q") }) });
        var body = ChangesResponse(resultingRevision: null);   // no resulting_revisions, no sync_result, no contents
        await eng.Drain(new ScriptedTransport(_ => new Resp(true, body, 200)), SessionContext.LoggedOut);

        Assert.Equal(Rev24(1), store.PlaylistRevision("spotify:playlist:p"));
        Assert.Equal(PlaylistResyncQueue.Phase.None, resync.Get("spotify:playlist:p").Phase);
    }

    // The common case: the server accepted exactly what we sent, so there is nothing to fold — just take the head.
    [Fact]
    public async Task Changes200_RevOnly_EmptySyncResult_AdvancesOnly()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, Rev24(1));
        var eng = Engine(store);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });   // -> [b]
        await eng.Drain(new ScriptedTransport(_ => new Resp(true, ChangesResponse(Rev24(4)), 200)), SessionContext.LoggedOut);

        Assert.Equal("spotify:track:b", Assert.Single(store.Membership("spotify:playlist:p")).ItemUri);   // rows untouched
        Assert.Equal(Rev24(4), store.PlaylistRevision("spotify:playlist:p"));
    }

    // A sync_result we cannot apply means our baseline drifted. The revision stays put and the uri is queued — the
    // alternative (advance anyway) is exactly the silent divergence I4 exists to prevent.
    [Fact]
    public async Task Changes200_TornSyncResult_DoesNotAdvance_AndQueuesResync()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a") }, Rev24(1));
        var resync = new PlaylistResyncQueue();
        var eng = new MutationEngine(store, new IMutationStrategy[]
        {
            new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", resync),
        });

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("q") }) });
        var torn = new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = new Pl.Rem { FromIndex = 40, Length = 5 } };
        var body = ChangesResponse(Rev24(7), new[] { torn });
        await eng.Drain(new ScriptedTransport(_ => new Resp(true, body, 200)), SessionContext.LoggedOut);

        Assert.Equal(Rev24(1), store.PlaylistRevision("spotify:playlist:p"));
        Assert.Equal("spotify:playlist:p", Assert.Single(resync.TakeAll()));
    }
}
