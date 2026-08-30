using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// ── P3: rootlist folder CRUD ─────────────────────────────────────────────────────────────────────────────────────────
// A folder is a balanced marker pair in the flat rootlist item stream, so every folder op is an index ADD/REM. The three
// builders are pinned BYTE-EXACT against the desktop captures (a164 create, b037/b128 rename); the delete shape is
// reference-inferred (no capture exists) and is pinned behaviourally instead: end marker first, children untouched.
public class RootlistFolderOpsTests
{
    static readonly SessionContext Ctx = new("bob", "US", "premium", "en", Tier.Premium, false);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class RecTransport(Func<string, byte[], int, Resp> respond) : ITransport
    {
        public readonly List<(string Route, string Method, byte[] Body)> Sent = new();
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            var b = body.ToArray();
            Sent.Add((route, method ?? (body.IsEmpty ? "GET" : "POST"), b));
            return Task.FromResult(respond(route, b, Sent.Count));
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }

    static PlaylistMutationSource Source(IStore store, ITransport transport)
    {
        var engine = new MutationEngine(store, new IMutationStrategy[]
        {
            new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()),
        });
        var http = new FakeExchange((_, _) => new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>()));
        return new PlaylistMutationSource(engine, transport, http, () => Ctx,
            () => "https://spclient.wg.spotify.com", new UserPlaylistSource(), new RootlistLane(), store);
    }

    static IReadOnlyList<RootlistEntry> Entries(params (string Uri, long Ts)[] rows)
    {
        var uris = new string[rows.Length];
        var stamps = new long[rows.Length];
        for (int i = 0; i < rows.Length; i++) { uris[i] = rows[i].Uri; stamps[i] = rows[i].Ts; }
        return RootlistTreeBuilder.EntriesFromUris(uris, stamps);
    }

    static byte[] RootlistBody(IReadOnlyList<PlaylistOp> ops, byte[] baseRev, string user, long nowMs, long nonce)
        => PlaylistWireMapper.BuildRootlistChanges(baseRev, ops, user, nowMs, nonce);

    // ── 1. create: ONE delta, TWO ADDs, byte-exact against a164 ─────────────────────────────────────────────────────
    [Fact]
    public void Create_TwoAddsOneDelta_GoldenA164()
    {
        var captured = Golden.Bytes("a164-folder-create");
        var changes = Golden.Changes("a164-folder-create");
        var delta = Assert.Single(changes.Deltas);
        // Everything the capture fixes: the group id, the name (space encoded as '+'), the create timestamp, the index,
        // the signing user and the per-session nonce.
        long createdAt = delta.Ops[0].Add.Items[0].Attributes.Timestamp;

        var ops = RootlistOps.BuildCreateFolder(Array.Empty<RootlistEntry>(), "edb339e10aebcf38", "New Folder", insertAt: 0, nowMs: createdAt);
        var rebuilt = RootlistBody(ops, changes.BaseRevision.ToByteArray(), delta.Info.User, delta.Info.Timestamp, nonce: 2);

        Assert.Equal(captured, rebuilt);
        Assert.Equal(2, ops.Count);
        Assert.Equal("spotify:start-group:edb339e10aebcf38:New+Folder", ops[0].Items![0].ItemUri);
        Assert.Equal("spotify:end-group:edb339e10aebcf38", ops[1].Items![0].ItemUri);
        Assert.Equal(0, ops[0].FromIndex);
        Assert.Equal(1, ops[1].FromIndex);
        // and NO public attribute on a folder marker (that is a playlist-row attribute — a042)
        Assert.All(delta.Ops, op => Assert.False(op.Add.Items[0].Attributes.HasPublic));
    }

    // ── 2. rename: REM without items + ADD carrying the ORIGINAL create timestamp ────────────────────────────────────
    [Fact]
    public void Rename_RemNoItems_AddOriginalTs_GoldenB037()
    {
        var captured = Golden.Bytes("b037-folder-rename");
        var changes = Golden.Changes("b037-folder-rename");
        var delta = Assert.Single(changes.Deltas);
        long originalTs = delta.Ops[1].Add.Items[0].Attributes.Timestamp;

        // the folder sits at index 2 of the marker stream, carrying the timestamp it was created with
        var entries = Entries(
            ("spotify:playlist:a", 0),
            ("spotify:playlist:b", 0),
            ("spotify:start-group:edb339e10aebcf38:New+Folder", originalTs),
            ("spotify:end-group:edb339e10aebcf38", originalTs));

        var ops = RootlistOps.BuildRenameFolder(entries, "edb339e10aebcf38", "named folder update", nowMs: 1)!;
        var rebuilt = RootlistBody(ops, changes.BaseRevision.ToByteArray(), delta.Info.User, delta.Info.Timestamp, nonce: 16);

        Assert.Equal(captured, rebuilt);
        Assert.Equal(PlaylistOpKind.Remove, ops[0].Kind);
        Assert.Equal(2, ops[0].FromIndex);
        Assert.Equal(1, ops[0].Length);
        Assert.Null(ops[0].Items);                               // a rename REM names nothing — it is purely positional
        Assert.Equal(originalTs, ops[1].Items![0].AddedAt);      // NOT "now": the create timestamp is resent verbatim
        Assert.NotEqual(originalTs, delta.Info.Timestamp);
    }

    [Fact]
    public void Rename_Outer_GoldenB128()
    {
        var captured = Golden.Bytes("b128-folder-rename-outer");
        var changes = Golden.Changes("b128-folder-rename-outer");
        var delta = Assert.Single(changes.Deltas);
        long originalTs = delta.Ops[1].Add.Items[0].Attributes.Timestamp;

        var entries = Entries(
            ("spotify:start-group:3dd9e795c88ae3e4:root+folder", originalTs),
            ("spotify:playlist:child", 0),
            ("spotify:end-group:3dd9e795c88ae3e4", originalTs));

        var ops = RootlistOps.BuildRenameFolder(entries, "3dd9e795c88ae3e4", "root folder updated name", nowMs: 1)!;
        var rebuilt = RootlistBody(ops, changes.BaseRevision.ToByteArray(), delta.Info.User, delta.Info.Timestamp, nonce: 18);

        Assert.Equal(captured, rebuilt);
        Assert.Equal(0, ops[0].FromIndex);
    }

    // ── 3. delete: end marker first, children stay ──────────────────────────────────────────────────────────────────
    [Fact]
    public void Delete_RemEndThenStart_ChildrenStay()
    {
        var entries = Entries(
            ("spotify:playlist:before", 0),
            ("spotify:start-group:g1:Trips", 100),
            ("spotify:playlist:inside", 0),
            ("spotify:end-group:g1", 100),
            ("spotify:playlist:after", 0));

        var ops = RootlistOps.BuildDeleteFolder(entries, "g1")!;

        Assert.Equal(2, ops.Count);
        Assert.All(ops, op => Assert.Equal(PlaylistOpKind.Remove, op.Kind));
        Assert.Equal(3, ops[0].FromIndex);   // the END marker first — removing the start first would shift this by one
        Assert.Equal(1, ops[0].Length);
        Assert.Equal(1, ops[1].FromIndex);
        Assert.Equal(1, ops[1].Length);
        Assert.All(ops, op => Assert.Null(op.Items));

        var after = RootlistOps.ApplyLocally(entries, ops);
        Assert.Equal(new[] { "spotify:playlist:before", "spotify:playlist:inside", "spotify:playlist:after" },
            System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(after, e => e.Uri)));
        Assert.All(after, e => Assert.Equal(0, e.Depth));   // the child moved up a level, it was not deleted
    }

    [Fact]
    public void Delete_NestedFolder_TakesTheWholeSubtreeWithIt_AsMarkersOnly()
    {
        var entries = Entries(
            ("spotify:start-group:outer:Outer", 100),
            ("spotify:start-group:inner:Inner", 200),
            ("spotify:playlist:deep", 0),
            ("spotify:end-group:inner", 200),
            ("spotify:end-group:outer", 100));

        var ops = RootlistOps.BuildDeleteFolder(entries, "outer")!;
        var after = RootlistOps.ApplyLocally(entries, ops);

        Assert.Equal(4, ops[0].FromIndex);                   // outer's END
        Assert.Equal(0, ops[1].FromIndex);                   // then outer's START
        Assert.Equal(3, after.Count);                        // the inner folder and its child survive, one level up
        Assert.Equal("spotify:start-group:inner:Inner", after[0].Uri);
        Assert.Equal(0, after[0].Depth);
        Assert.Equal(1, after[1].Depth);
    }

    [Fact]
    public void Builders_ReturnNull_WhenTheFolderIsGone()
    {
        var entries = Entries(("spotify:playlist:a", 0));
        Assert.Null(RootlistOps.BuildRenameFolder(entries, "nope", "x", nowMs: 1));
        Assert.Null(RootlistOps.BuildDeleteFolder(entries, "nope"));
        Assert.Equal(-1, RootlistOps.PlacementIndex(entries, new RootlistPlacement("nope")));
        Assert.Equal(0, RootlistOps.PlacementIndex(entries, default));
    }

    [Fact]
    public void FolderName_EncodesSpacesAsPlus_AndEscapesTheRest()
    {
        Assert.Equal("spotify:start-group:g:New+Folder", RootlistOps.StartGroupUri("g", "New Folder"));
        Assert.Equal("spotify:start-group:g:a%2Fb", RootlistOps.StartGroupUri("g", "a/b"));
        // and the parse side reads it back (the tree builder unescapes; '+' is a literal there, which is desktop's own
        // asymmetry — the name shown for "New Folder" round-trips through EntriesFromUris as "New+Folder").
        Assert.Equal("spotify:end-group:g", RootlistOps.EndGroupUri("g"));
    }

    // ── 4. the source: online-only, lane-serialized, one rebase on 409 ───────────────────────────────────────────────
    [Fact]
    public async Task CreateFolder_PostsOnce_AndAppliesTheTreeLocally()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:playlist:a", 0)), Rev24(1));
        var t = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        string groupId = await source.CreateFolderAsync("Trips", default, Ct);

        var req = Assert.Single(t.Sent);
        Assert.Equal("/playlist/v2/user/bob/rootlist/changes", req.Route);
        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(req.Body).Deltas);
        Assert.Equal(2, delta.Ops.Count);
        Assert.Equal($"spotify:start-group:{groupId}:Trips", delta.Ops[0].Add.Items[0].Uri);

        var rows = store.Rootlist();
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0].Kind);                                     // the folder landed at the top
        Assert.Equal("Trips", rows[0].GroupName);
        Assert.True(rows[0].AddedAtMs > 0);
        Assert.Equal(2, rows[1].Kind);
        Assert.Equal("spotify:playlist:a", rows[2].Uri);
        Assert.Equal(Rev24(1), store.RootlistRevision());                  // the reply advanced nothing; the rev stands
    }

    [Fact]
    public async Task CreateFolder_InsideAnotherFolder_LandsAfterItsStartMarker()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:start-group:outer:Outer", 5), ("spotify:end-group:outer", 5)), Rev24(1));
        var t = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        string groupId = await source.CreateFolderAsync("Inner", new RootlistPlacement("outer"), Ct);

        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(Assert.Single(t.Sent).Body).Deltas);
        Assert.Equal(1, delta.Ops[0].Add.FromIndex);
        Assert.Equal(2, delta.Ops[1].Add.FromIndex);
        var rows = store.Rootlist();
        Assert.Equal($"spotify:start-group:{groupId}:Inner", rows[1].Uri);
        Assert.Equal(1, rows[1].Depth);
    }

    [Fact]
    public async Task Rename_409_RebasesOnce()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:start-group:g1:Old", 77)), Rev24(1));
        // 1st POST → 409; the bootstrap GET returns the rootlist as the server has it (the folder moved down one row);
        // the 2nd POST must carry the RECOMPUTED index and still the original timestamp.
        var bootstrap = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(2)) };
        bootstrap.Contents = new Pl.ListItems();
        bootstrap.Contents.Items.Add(new Pl.Item { Uri = "spotify:playlist:new" });
        bootstrap.Contents.Items.Add(new Pl.Item
        {
            Uri = "spotify:start-group:g1:Old",
            Attributes = new Pl.ItemAttributes { Timestamp = 77 },
        });
        var t = new RecTransport((route, body, call) => call switch
        {
            1 => new Resp(false, Array.Empty<byte>(), 409),
            2 => new Resp(true, bootstrap.ToByteArray(), 200),        // the bootstrap GET
            _ => new Resp(true, Array.Empty<byte>(), 200),
        });
        var source = Source(store, t);

        await source.RenameFolderAsync("g1", "New", Ct);

        Assert.Equal(3, t.Sent.Count);
        Assert.Equal("GET", t.Sent[1].Method);
        var retry = Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[2].Body).Deltas);
        Assert.Equal(1, retry.Ops[0].Rem.FromIndex);                 // recomputed against the bootstrapped stream
        Assert.Equal(1, retry.Ops[1].Add.FromIndex);
        Assert.Equal("spotify:start-group:g1:New", retry.Ops[1].Add.Items[0].Uri);
        Assert.Equal(77L, retry.Ops[1].Add.Items[0].Attributes.Timestamp);
        Assert.Equal("New", store.Rootlist()[1].GroupName);
    }

    [Fact]
    public async Task Rename_MissingTimestamp_BootstrapsFirst()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:start-group:g1:Old", 0)), Rev24(1));   // no timestamp captured
        var bootstrap = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(1)) };
        bootstrap.Contents = new Pl.ListItems();
        bootstrap.Contents.Items.Add(new Pl.Item
        {
            Uri = "spotify:start-group:g1:Old",
            Attributes = new Pl.ItemAttributes { Timestamp = 4242 },
        });
        var t = new RecTransport((route, body, call) => call == 1
            ? new Resp(true, bootstrap.ToByteArray(), 200)
            : new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        await source.RenameFolderAsync("g1", "New", Ct);

        Assert.Equal(2, t.Sent.Count);
        Assert.Equal("GET", t.Sent[0].Method);                                     // the bootstrap came FIRST
        Assert.StartsWith("/playlist/v2/user/bob/rootlist?", t.Sent[0].Route, StringComparison.Ordinal);
        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[1].Body).Deltas);
        Assert.Equal(4242L, delta.Ops[1].Add.Items[0].Attributes.Timestamp);       // the ts the GET carried, not "now"
    }

    [Fact]
    public async Task DeleteFolder_RemovesBothMarkers_AndKeepsChildren()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(
            ("spotify:start-group:g1:Trips", 9),
            ("spotify:playlist:inside", 0),
            ("spotify:end-group:g1", 9)), Rev24(1));
        var t = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        await source.DeleteFolderAsync("g1", Ct);

        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(Assert.Single(t.Sent).Body).Deltas);
        Assert.Equal(2, delta.Ops[0].Rem.FromIndex);
        Assert.Equal(0, delta.Ops[1].Rem.FromIndex);
        Assert.Equal("spotify:playlist:inside", Assert.Single(store.Rootlist()).Uri);
    }

    [Fact]
    public async Task FolderOps_AreOnlineOnly()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:start-group:g1:Trips", 9), ("spotify:end-group:g1", 9)), Rev24(1));
        var source = Source(store, new StubTransport());   // the named offline stand-in

        var create = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.CreateFolderAsync("x", default, Ct));
        var rename = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.RenameFolderAsync("g1", "x", Ct));
        var delete = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.DeleteFolderAsync("g1", Ct));

        Assert.Equal(PlaylistMutationFailure.Offline, create.Kind);
        Assert.Equal(PlaylistMutationFailure.Offline, rename.Kind);
        Assert.Equal(PlaylistMutationFailure.Offline, delete.Kind);
    }

    [Fact]
    public async Task RenameFolder_Gone_IsDeleted_NotSilent()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:playlist:a", 0)), Rev24(1));
        var t = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.RenameFolderAsync("gone", "x", Ct));

        Assert.Equal(PlaylistMutationFailure.Deleted, ex.Kind);
    }

    // ── 5. optimistic-first: the folder is there before the wire answers, and gone again if it never does ────────────
    // Same three moves as MoveRootlistItemAsync — apply locally, POST, roll back if it did not land. The reply carries
    // no contents, so "the tree the server now holds" is only ever the tree we computed.

    /// <summary>Parks the POST so the test decides when it completes.</summary>
    sealed class ParkedTransport : ITransport
    {
        public readonly TaskCompletionSource<Resp> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Posted.TrySetResult();
            return Answer.Task;
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    [Fact]
    public async Task CreateFolder_AppliesTheTree_BEFORE_ThePostCompletes()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:playlist:a", 0)), Rev24(1));
        var t = new ParkedTransport();
        var source = Source(store, t);

        var create = source.CreateFolderAsync("Trips", default, Ct);
        await t.Posted.Task;

        Assert.False(create.IsCompleted);
        Assert.Equal(3, store.Rootlist().Count);                  // the marker pair is already in the sidebar's tree
        Assert.Equal("Trips", store.Rootlist()[0].GroupName);
        Assert.Equal(Rev24(1), store.RootlistRevision());         // …with the revision we still trust

        t.Answer.SetResult(new Resp(true, Array.Empty<byte>(), 200));
        await create;
        Assert.Equal(3, store.Rootlist().Count);
    }

    [Fact]
    public async Task CreateFolder_TransportFault_RollsTheTreeBack()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:playlist:a", 0)), Rev24(1));
        var t = new RecTransport((_, _, _) => new Resp(false, Array.Empty<byte>(), 503));   // valid op, dead wire
        var source = Source(store, t);

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.CreateFolderAsync("Trips", default, Ct));

        Assert.Equal(PlaylistMutationFailure.Unknown, ex.Kind);
        Assert.Equal("spotify:playlist:a", Assert.Single(store.Rootlist()).Uri);   // no phantom folder left behind
        Assert.Equal(Rev24(1), store.RootlistRevision());
    }

    [Fact]
    public async Task DeleteFolder_TransportFault_PutsTheFolderBack()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(
            ("spotify:start-group:g1:Trips", 9),
            ("spotify:playlist:inside", 0),
            ("spotify:end-group:g1", 9)), Rev24(1));
        var source = Source(store, new RecTransport((_, _, _) => new Resp(false, Array.Empty<byte>(), 503)));

        await Assert.ThrowsAsync<PlaylistMutationException>(() => source.DeleteFolderAsync("g1", Ct));

        Assert.Equal(3, store.Rootlist().Count);
        Assert.Equal(1, store.Rootlist()[0].Kind);
    }

    // ── 6. deleting a playlist takes its rootlist row with it, locally ──────────────────────────────────────────────
    [Fact]
    public async Task DeletePlaylist_RemovesTheRootlistRowLocally_AndRestoresItOnFailure()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:playlist:keep", 0), ("spotify:playlist:doomed", 0)), Rev24(1));
        var ok = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));

        await Source(store, ok).DeletePlaylistAsync("spotify:playlist:doomed", Ct);

        // The reply has no contents, so the row can only leave the tree because WE removed it.
        Assert.Equal("spotify:playlist:keep", Assert.Single(store.Rootlist()).Uri);
        Assert.False(store.IsSaved("playlists", "spotify:playlist:doomed"));

        var store2 = new InMemoryStore();
        store2.SetRootlist(Entries(("spotify:playlist:keep", 0), ("spotify:playlist:doomed", 0)), Rev24(1));
        var dead = new RecTransport((_, _, _) => new Resp(false, Array.Empty<byte>(), 503));

        await Assert.ThrowsAsync<PlaylistMutationException>(
            () => Source(store2, dead).DeletePlaylistAsync("spotify:playlist:doomed", Ct));

        Assert.Equal(2, store2.Rootlist().Count);                  // the row is back — nothing was deleted anywhere
    }

    [Fact]
    public void LocalSource_RefusesFolderOps_ByName()
    {
        var local = new LocalPlaylistMutationSource(new UserPlaylistSource());
        Assert.Throws<NotSupportedException>(() => { _ = local.CreateFolderAsync("x", default); });
        Assert.Throws<NotSupportedException>(() => { _ = local.RenameFolderAsync("g", "x"); });
        Assert.Throws<NotSupportedException>(() => { _ = local.DeleteFolderAsync("g"); });
    }

    // -- batch moves: ONE Delta, sequential index math ---------------------------------------------------------------
    // A multi-selection drop is not N drops. The ops of ONE Delta are applied by the server in order, each against the
    // state the previous ones left, so the builder has to do the same - building all N against the original stream
    // posts indices that are already stale by the second op. RootlistOps.TryBuildMoves is that loop, over the SAME
    // TryBuildMove index math a single drop uses (there is no second implementation to drift).

    static string Pl_(string slug) => "spotify:playlist:" + slug;
    static RootlistItemRef PRef(string slug) => new(Pl_(slug), IsFolder: false);
    static RootlistItemRef FRef(string id) => new(id, IsFolder: true);
    static RootlistMove Mv(string source, RootlistItemRef target, RootlistDropPlacement placement)
        => new(PRef(source), target, placement);

    /// <summary>[A,B,C,D,E] - five top-level playlists, the stream every ordering example below is worked on.</summary>
    static IReadOnlyList<RootlistEntry> Abcde() => Entries(
        (Pl_("a"), 0), (Pl_("b"), 0), (Pl_("c"), 0), (Pl_("d"), 0), (Pl_("e"), 0));

    /// <summary>The playlist slugs of a stream, folder markers spelled as <c>[name</c> / <c>]</c>.</summary>
    static string[] Shape(IReadOnlyList<RootlistEntry> entries) => entries.Select(e => e.Kind switch
    {
        1 => "[" + (e.GroupName ?? ""),
        2 => "]",
        _ => e.Uri["spotify:playlist:".Length..],
    }).ToArray();

    static string[] Applied(IReadOnlyList<RootlistEntry> entries, IReadOnlyList<PlaylistOp> ops)
        => Shape(RootlistOps.ApplyLocally(entries, ops));

    [Fact]
    public void TryBuildMoves_BuildsEachOpAgainstTheStreamThePrecedingOpsLeft()
    {
        // Two moves, and the SECOND one's indices only exist after the first has been applied: d starts at index 3 of
        // [A,B,C,D,E], but its op is built against the [A,C,D,E,B] the first op left - where d is at 2. Building both
        // against the ORIGINAL stream would have posted from=3, which by then is e.
        var entries = Abcde();
        Assert.True(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("b", PRef("e"), RootlistDropPlacement.After),
            Mv("d", PRef("a"), RootlistDropPlacement.Before),
        }, out var ops, out var reason));

        Assert.Equal(RootlistMoveCheck.Ok, reason);
        Assert.Equal(2, ops.Count);
        Assert.All(ops, op => Assert.Equal(PlaylistOpKind.Move, op.Kind));
        Assert.Equal((1, 1, 5), (ops[0].FromIndex, ops[0].Length, ops[0].ToIndex));   // b: 1 -> after e (index 5)
        Assert.Equal((2, 1, 0), (ops[1].FromIndex, ops[1].Length, ops[1].ToIndex));   // d sits at 2 NOW, after b left
        Assert.Equal(new[] { "d", "a", "c", "e", "b" }, Applied(entries, ops));
    }

    // -- the three ordering examples, worked through dest = to > from ? to - Length : to -----------------------------
    // WHICH order a same-target batch is handed over is the UI's rule (RootlistBatchOrder); the seam executes the list
    // it is given. These pin what each order actually produces, so the rule can be checked against reality.

    [Fact]
    public void After_InReverseTreeOrder_KeepsTheSelectionsOwnOrder()
    {
        var entries = Abcde();
        // {B,D} dropped AFTER E, submitted in REVERSE tree order (d, then b) - each op lands adjacent to the anchor.
        Assert.True(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("d", PRef("e"), RootlistDropPlacement.After),
            Mv("b", PRef("e"), RootlistDropPlacement.After),
        }, out var reverse, out _));
        Assert.Equal(new[] { "a", "c", "e", "b", "d" }, Applied(entries, reverse));

        // ...and forward order is exactly why the rule exists: the pair lands reversed.
        Assert.True(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("b", PRef("e"), RootlistDropPlacement.After),
            Mv("d", PRef("e"), RootlistDropPlacement.After),
        }, out var forward, out _));
        Assert.Equal(new[] { "a", "c", "e", "d", "b" }, Applied(entries, forward));
    }

    [Fact]
    public void Before_InTreeOrder_KeepsTheSelectionsOwnOrder()
    {
        var entries = Abcde();
        Assert.True(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("b", PRef("a"), RootlistDropPlacement.Before),
            Mv("d", PRef("a"), RootlistDropPlacement.Before),
        }, out var ops, out _));

        Assert.Equal(new[] { "b", "d", "a", "c", "e" }, Applied(entries, ops));
    }

    [Fact]
    public void Inside_InTreeOrder_AppendsBeforeTheEndMarker_InOrder()
    {
        // [f: x ] b c - Inside always lands immediately before the folder's end marker, so tree order appends.
        var entries = Entries(
            (RootlistOps.StartGroupUri("f", "Folder"), 100), (Pl_("x"), 0), (RootlistOps.EndGroupUri("f"), 100),
            (Pl_("b"), 0), (Pl_("c"), 0));

        Assert.True(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("b", FRef("f"), RootlistDropPlacement.Inside),
            Mv("c", FRef("f"), RootlistDropPlacement.Inside),
        }, out var ops, out _));

        var after = RootlistOps.ApplyLocally(entries, ops);
        Assert.Equal(new[] { "[Folder", "x", "b", "c", "]" }, Shape(after));
        Assert.Equal(new[] { 1, 1, 1 }, after.Skip(1).Take(3).Select(e => e.Depth).ToArray());
    }

    [Fact]
    public void ASourceThatIsTheTarget_IsAGather_NotASameItemRefusal()
    {
        // Dropping a multi-selection right after one of its OWN members is a legal "gather": the anchor stays put and
        // the others close up around it. Only the self-pair is dropped - the batch is not refused.
        var entries = Abcde();
        Assert.True(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("d", PRef("b"), RootlistDropPlacement.After),
            Mv("b", PRef("b"), RootlistDropPlacement.After),          // the anchor, dropped up front
        }, out var ops, out var reason));

        Assert.Equal(RootlistMoveCheck.Ok, reason);
        Assert.Single(ops);
        Assert.Equal(new[] { "a", "b", "d", "c", "e" }, Applied(entries, ops));

        // ...but a batch that is ONLY self-pairs is still the single-item answer, unchanged.
        Assert.False(RootlistOps.TryBuildMoves(entries, new[] { Mv("b", PRef("b"), RootlistDropPlacement.After) },
                                               out var none, out var self));
        Assert.Equal(RootlistMoveCheck.SameItem, self);
        Assert.Empty(none);
    }

    [Fact]
    public void TwoRealOpsThatNetToIdentity_AreOneNoOp_WithNothingToPost()
    {
        // {B,C} Before D on [A,B,C,D,E]: both ops are individually legal and each one really moves a row, yet the
        // stream they leave behind is the stream they started from. Per-op checks cannot see that - the FINAL stream is
        // compared to the input, so the batch spends no write.
        var entries = Abcde();
        Assert.False(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("b", PRef("d"), RootlistDropPlacement.Before),
            Mv("c", PRef("d"), RootlistDropPlacement.Before),
        }, out var ops, out var reason));

        Assert.Equal(RootlistMoveCheck.NoOp, reason);
        Assert.Empty(ops);
    }

    [Fact]
    public void APerMoveNoOp_IsSkipped_ButTheRestOfTheBatchStillLands()
    {
        // c is already right after b: that member has nothing to do, and it must not veto the members that do.
        var entries = Abcde();
        Assert.True(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("c", PRef("b"), RootlistDropPlacement.After),          // already there
            Mv("e", PRef("b"), RootlistDropPlacement.After),
        }, out var ops, out var reason));

        Assert.Equal(RootlistMoveCheck.Ok, reason);
        Assert.Single(ops);
        Assert.Equal(new[] { "a", "b", "e", "c", "d" }, Applied(entries, ops));
    }

    [Fact]
    public void ACycleOrAMissingRow_AnywhereInTheBatch_RefusesTheWholeBatch()
    {
        // [g: b ] a c - a legal first move, then the folder filed into its own subtree. Half a filing is worse than
        // none: the batch is refused whole, with the offending move's own reason.
        var entries = Entries(
            (RootlistOps.StartGroupUri("g", "G"), 100), (Pl_("b"), 0), (RootlistOps.EndGroupUri("g"), 100),
            (Pl_("a"), 0), (Pl_("c"), 0));

        Assert.False(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("a", PRef("b"), RootlistDropPlacement.Before),                     // legal - a joins the folder
            new RootlistMove(FRef("g"), PRef("b"), RootlistDropPlacement.Before), // g into its own subtree
        }, out var ops, out var reason));
        Assert.Equal(RootlistMoveCheck.Cycle, reason);
        Assert.Empty(ops);

        Assert.False(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("a", PRef("c"), RootlistDropPlacement.After),
            Mv("ghost", PRef("c"), RootlistDropPlacement.After),
        }, out _, out var missing));
        Assert.Equal(RootlistMoveCheck.Missing, missing);

        Assert.False(RootlistOps.TryBuildMoves(entries, new[]
        {
            Mv("a", PRef("c"), RootlistDropPlacement.Inside),                     // Inside a PLAYLIST is inexpressible
        }, out _, out var invalid));
        Assert.Equal(RootlistMoveCheck.Invalid, invalid);
    }

    [Fact]
    public void CheckMoves_IsTheBuilderWithTheOpsDiscarded()
    {
        // ONE legality authority: the cue asks the same code the writer runs, so they cannot disagree.
        var entries = Abcde();
        var legal = new[] { Mv("b", PRef("e"), RootlistDropPlacement.After) };
        var identity = new[]
        {
            Mv("b", PRef("d"), RootlistDropPlacement.Before),
            Mv("c", PRef("d"), RootlistDropPlacement.Before),
        };

        Assert.Equal(RootlistMoveCheck.Ok, RootlistOps.CheckMoves(entries, legal));
        Assert.Equal(RootlistMoveCheck.NoOp, RootlistOps.CheckMoves(entries, identity));
        Assert.Equal(RootlistMoveCheck.NoOp, RootlistOps.CheckMoves(entries, Array.Empty<RootlistMove>()));
        // and the N=1 answer is byte-for-byte the single-move checker's
        Assert.Equal(RootlistOps.CheckMove(entries, PRef("b"), PRef("e"), RootlistDropPlacement.After),
                     RootlistOps.CheckMoves(entries, legal));
    }

    [Fact]
    public async Task LocalSource_AcceptsTheBatchSeam_AsANoOp()
    {
        // Local playlists are not in the rootlist at all - the batch form mirrors the single one rather than throwing,
        // so the offline shell keeps the same seam shape.
        var local = new LocalPlaylistMutationSource(new UserPlaylistSource());
        await local.MoveRootlistItemsAsync(new[] { Mv("a", PRef("b"), RootlistDropPlacement.After) }, Ct);
        await local.MoveRootlistItemAsync(PRef("a"), PRef("b"), RootlistDropPlacement.After, Ct);
    }
}
