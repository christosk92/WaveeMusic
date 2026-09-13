using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

/// <summary>
/// THE WRITE END OF A ROOTLIST DROP: <c>PlaylistMutationSource.MoveRootlistItemAsync</c>.
///
/// <para>It used to <c>return</c> when <c>TryBuildMove</c> refused — so the awaiting caller
/// (<c>WaveeResourceDrop.MoveRootlist</c>) saw a Task complete successfully for a move that was never posted, and did
/// exactly what it does after a real one: announced it, toasted "Moved to …" and offered Undo. A success message for a
/// list that did not change is worse than no message at all, and it is what the user was looking at in screenshot #17.
/// Every refusal is now a typed <see cref="PlaylistMutationException"/> with its own sentence, and a legal move posts
/// exactly ONE request.</para>
/// </summary>
public class RootlistMoveSeamTests
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

    /// <summary>a · [g]{b, c} · d — one folder with two children, so every refusal shape is reachable.</summary>
    static InMemoryStore Store()
    {
        var store = new InMemoryStore();
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[]
        {
            "spotify:playlist:a",
            "spotify:start-group:g:Chill",
            "spotify:playlist:b",
            "spotify:playlist:c",
            "spotify:end-group:g",
            "spotify:playlist:d",
        }), Rev24(1));
        return store;
    }

    static RootlistItemRef P(string slug) => new("spotify:playlist:" + slug, IsFolder: false);
    static RootlistItemRef F(string id) => new(id, IsFolder: true);

    static async Task<PlaylistMutationFailure> Refused(PlaylistMutationSource src, RootlistItemRef from,
                                                       RootlistItemRef to, RootlistDropPlacement placement)
    {
        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(
            () => src.MoveRootlistItemAsync(from, to, placement, Ct));
        return ex.Kind;
    }

    [Fact]
    public async Task ANoOpMove_ThrowsAlreadyThere_AndPostsNothing()
    {
        var store = Store();
        var transport = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var src = Source(store, transport);

        // "after the row right before me" and "before the row right after me" are the two gestures a reorder produces
        // most often — and the two that used to complete "successfully" without a request.
        Assert.Equal(PlaylistMutationFailure.NoOp, await Refused(src, P("c"), P("b"), RootlistDropPlacement.After));
        Assert.Equal(PlaylistMutationFailure.NoOp, await Refused(src, P("b"), P("c"), RootlistDropPlacement.Before));
        // The folder's LAST child appended back into that same folder.
        Assert.Equal(PlaylistMutationFailure.NoOp, await Refused(src, P("c"), F("g"), RootlistDropPlacement.Inside));
        // Onto itself.
        Assert.Equal(PlaylistMutationFailure.NoOp, await Refused(src, P("b"), P("b"), RootlistDropPlacement.After));

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task ACycleOrAnInexpressiblePlacement_ThrowsInvalid_AndPostsNothing()
    {
        var store = Store();
        var transport = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var src = Source(store, transport);

        Assert.Equal(PlaylistMutationFailure.Invalid, await Refused(src, F("g"), P("b"), RootlistDropPlacement.Before));
        Assert.Equal(PlaylistMutationFailure.Invalid, await Refused(src, F("g"), P("c"), RootlistDropPlacement.After));
        // "Inside" a PLAYLIST is not a placement the marker stream can express at all.
        Assert.Equal(PlaylistMutationFailure.Invalid, await Refused(src, P("a"), P("d"), RootlistDropPlacement.Inside));

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task ARowThatIsNoLongerInTheRootlist_ThrowsConflict()
    {
        // The tree moved under the gesture (a desktop edit, a dealer push). "Your library changed while that was
        // saving" is the honest sentence — not silence, and not "something went wrong".
        var src = Source(Store(), new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200)));
        Assert.Equal(PlaylistMutationFailure.Conflict,
                     await Refused(src, P("ghost"), P("a"), RootlistDropPlacement.After));
        Assert.Equal(PlaylistMutationFailure.Conflict,
                     await Refused(src, P("a"), P("ghost"), RootlistDropPlacement.After));
    }

    [Fact]
    public async Task ALegalMove_PostsExactlyOneChange()
    {
        // ONE DROP ⇒ ONE CALL ⇒ ONE REQUEST. The rootlist lane serializes the write and the response is folded back in,
        // so a completed move is a completed move — which is the only thing the caller is allowed to confirm.
        var store = Store();
        var transport = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var src = Source(store, transport);

        await src.MoveRootlistItemAsync(P("d"), F("g"), RootlistDropPlacement.Inside, Ct);

        var one = Assert.Single(transport.Sent);
        Assert.Equal("POST", one.Method);
        Assert.Contains("/rootlist/changes", one.Route, StringComparison.Ordinal);
    }

    // ── THE LOCAL TREE: a move that only POSTs is a move the user never sees ────────────────────────────────────────
    //
    // A rootlist /changes 200 carries revision bookkeeping and NO contents (the a164 golden below is a real one), and
    // the dealer echo that follows is a head-only push AT THAT REVISION — correctly echo-dropped, so no GET ever
    // happens. The seam POSTed, returned success, the toast said "Moved to …", and the sidebar sat there showing the
    // old order until something unrelated forced a refetch. The rows can only come from applying the op ourselves.

    /// <summary>A parked transport: the test decides WHEN the POST completes, which is the only way to prove the tree
    /// moved BEFORE the wire answered rather than because of it.</summary>
    sealed class ParkedTransport : ITransport
    {
        public readonly TaskCompletionSource<Resp> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<(string Route, string Method)> Sent = new();
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Sent.Add((route, method ?? (body.IsEmpty ? "GET" : "POST")));
            Posted.TrySetResult();
            return Answer.Task;
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    sealed class Changes : IObserver<StoreChange>
    {
        public readonly List<StoreChange> All = new();
        public void OnNext(StoreChange v) { lock (All) All.Add(v); }
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    static string[] Uris(IStore store) => store.Rootlist().Select(e => e.Uri).ToArray();

    /// <summary>A REAL revision-only rootlist reply — the captured one. Nothing in it is contents.</summary>
    static byte[] RevOnlyReply() => Golden.Bytes("a164-folder-create-response");
    static byte[] RevOnlyReplyRevision() => Golden.Content("a164-folder-create-response").Revision.ToByteArray();

    [Fact]
    public async Task AMove_ReordersTheStore_BEFORE_ThePostCompletes()
    {
        var store = Store();
        var seen = new Changes();
        using var sub = store.Changes.Subscribe(seen);
        var t = new ParkedTransport();
        var src = Source(store, t);

        var move = src.MoveRootlistItemAsync(P("d"), F("g"), RootlistDropPlacement.Inside, Ct);
        await t.Posted.Task;                                     // the POST is in flight and PARKED

        Assert.False(move.IsCompleted);
        Assert.Equal(new[]
        {
            "spotify:playlist:a", "spotify:start-group:g:Chill", "spotify:playlist:b",
            "spotify:playlist:c", "spotify:playlist:d", "spotify:end-group:g",
        }, Uris(store));                                          // d is already inside the folder
        lock (seen.All)
            Assert.Contains(seen.All, c => c.Uri == "rootlist" && c.Kind == CollectionKind.Playlists);
        Assert.Equal(Rev24(1), store.RootlistRevision());         // …but the revision is NOT advanced until the ack

        t.Answer.SetResult(new Resp(true, RevOnlyReply(), 200));
        await move;

        Assert.Equal("spotify:playlist:d", store.Rootlist()[4].Uri);          // the applied order stands
        Assert.Equal(1, store.Rootlist()[4].Depth);
        Assert.Equal(RevOnlyReplyRevision(), store.RootlistRevision());       // and the reply's revision is adopted
        Assert.Null(Golden.Content("a164-folder-create-response").Contents);  // the reply that could never have told us
    }

    [Fact]
    public async Task AMoveThatCannotBeSent_RestoresTheTree_AndThrowsTyped()
    {
        var store = Store();
        var before = Uris(store);
        // 503 → RootlistPostOutcome.Retry: the op is still valid, the WIRE failed. Nothing was saved, so the row the
        // user watched move has to jump back — silently keeping it would be a lie that survives a restart.
        var t = new RecTransport((_, _, _) => new Resp(false, Array.Empty<byte>(), 503));
        var src = Source(store, t);

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(
            () => src.MoveRootlistItemAsync(P("d"), F("g"), RootlistDropPlacement.Inside, Ct));

        Assert.Equal(PlaylistMutationFailure.Unknown, ex.Kind);
        Assert.Equal(before, Uris(store));
        Assert.Equal(Rev24(1), store.RootlistRevision());
    }

    [Fact]
    public async Task A409_RebuildsAgainstTheBootstrappedRootlist_AndAppliesThat()
    {
        var store = Store();
        // The server holds a DIFFERENT stream: one extra row at the front, so every index the first attempt computed
        // is off by one. Replaying it verbatim would move the wrong row.
        var bootstrap = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(2)) };
        bootstrap.Contents = new Pl.ListItems();
        foreach (var uri in new[]
                 {
                     "spotify:playlist:x", "spotify:playlist:a", "spotify:start-group:g:Chill",
                     "spotify:playlist:b", "spotify:playlist:c", "spotify:end-group:g", "spotify:playlist:d",
                 })
            bootstrap.Contents.Items.Add(new Pl.Item { Uri = uri });

        var t = new RecTransport((route, body, call) => call switch
        {
            1 => new Resp(false, Array.Empty<byte>(), 409),
            2 => new Resp(true, bootstrap.ToByteArray(), 200),      // the bootstrap GET
            _ => new Resp(true, RevOnlyReply(), 200),
        });
        var src = Source(store, t);

        await src.MoveRootlistItemAsync(P("d"), F("g"), RootlistDropPlacement.Inside, Ct);

        Assert.Equal(3, t.Sent.Count);
        Assert.Equal("GET", t.Sent[1].Method);
        var first = Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[0].Body).Deltas).Ops[0].Mov;
        var retry = Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[2].Body).Deltas).Ops[0].Mov;
        Assert.Equal(5, first.FromIndex);
        Assert.Equal(6, retry.FromIndex);                          // recomputed against the stream that really exists
        Assert.Equal(5, retry.ToIndex);
        Assert.Equal(new[]
        {
            "spotify:playlist:x", "spotify:playlist:a", "spotify:start-group:g:Chill", "spotify:playlist:b",
            "spotify:playlist:c", "spotify:playlist:d", "spotify:end-group:g",
        }, Uris(store));                                            // the SERVER's rows, with our move applied on top
        Assert.Equal(RevOnlyReplyRevision(), store.RootlistRevision());
    }

    // ── the four scenario drops, driven through the REAL seam ───────────────────────────────────────────────────────
    // Same tree as RootlistDropScenarioTests, shaped like the report: "Careless" is the FIRST CHILD of "named folder
    // update", which is what made the reported drop land at depth 2.
    //
    //   [root folder updated name]
    //       [named folder update]
    //           Careless
    //           #9
    //       updated playlist name
    //   LoL

    const string Root = "root", Named = "named";

    static InMemoryStore UserStore()
    {
        var store = new InMemoryStore();
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[]
        {
            RootlistOps.StartGroupUri(Root, "root folder updated name"),
            RootlistOps.StartGroupUri(Named, "named folder update"),
            "spotify:playlist:careless",
            "spotify:playlist:nine",
            RootlistOps.EndGroupUri(Named),
            "spotify:playlist:updated",
            RootlistOps.EndGroupUri(Root),
            "spotify:playlist:lol",
        }), Rev24(1));
        return store;
    }

    /// <summary>Rows as (name, depth) — ORDER and NESTING, which is the pair the user was watching not change.</summary>
    static (string, int)[] Shape(IStore store) => store.Rootlist()
        .Where(e => e.Kind != 2)
        .Select(e => (e.Kind == 1 ? "[" + (e.GroupName ?? "") : e.Uri["spotify:playlist:".Length..], e.Depth))
        .ToArray();

    [Fact]
    public async Task TheReportedDrop_LoLOntoCarelessTopBand_LandsAsTheFoldersFirstChild()
    {
        // THE bug report: LoL dragged onto the top band of "Careless" (first child of "named folder update"). The line
        // was drawn at the right depth, the toast said "Moved to named folder update" — and nothing moved.
        var store = UserStore();
        var src = Source(store, new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200)));

        await src.MoveRootlistItemAsync(new RootlistItemRef("spotify:playlist:lol", IsFolder: false),
                                        new RootlistItemRef("spotify:playlist:careless", IsFolder: false),
                                        RootlistDropPlacement.Before, Ct);

        Assert.Equal(new[]
        {
            ("[root folder updated name", 0),
            ("[named folder update", 1),
            ("lol", 2),                       // ← the first child of the folder, exactly where the cue promised
            ("careless", 2),
            ("nine", 2),
            ("updated", 1),
        }, Shape(store));
    }

    [Fact]
    public async Task IntoAFolder_PutsTheRowInside()
    {
        var store = UserStore();
        var src = Source(store, new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200)));

        await src.MoveRootlistItemAsync(new RootlistItemRef("spotify:playlist:updated", IsFolder: false),
                                        new RootlistItemRef(Named, IsFolder: true), RootlistDropPlacement.Inside, Ct);

        Assert.Equal(new[]
        {
            ("[root folder updated name", 0), ("[named folder update", 1),
            ("careless", 2), ("nine", 2), ("updated", 2), ("lol", 0),
        }, Shape(store));
    }

    [Fact]
    public async Task AfterAFolder_OutdentsTheRow_OneLevel()
    {
        var store = UserStore();
        var src = Source(store, new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200)));

        await src.MoveRootlistItemAsync(new RootlistItemRef("spotify:playlist:nine", IsFolder: false),
                                        new RootlistItemRef(Named, IsFolder: true), RootlistDropPlacement.After, Ct);

        Assert.Equal(new[]
        {
            ("[root folder updated name", 0), ("[named folder update", 1),
            ("careless", 2), ("nine", 1), ("updated", 1), ("lol", 0),
        }, Shape(store));
    }

    [Fact]
    public async Task AnAdjacentNoOp_ChangesNothing_AndPostsNothing()
    {
        var store = UserStore();
        var before = Shape(store);
        var t = new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200));
        var src = Source(store, t);

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(
            () => src.MoveRootlistItemAsync(new RootlistItemRef("spotify:playlist:careless", IsFolder: false),
                                            new RootlistItemRef("spotify:playlist:nine", IsFolder: false),
                                            RootlistDropPlacement.Before, Ct));

        Assert.Equal(PlaylistMutationFailure.NoOp, ex.Kind);
        Assert.Empty(t.Sent);
        Assert.Equal(before, Shape(store));                        // a refusal must not disturb the tree either
    }

    // ── the echo that follows our own move must still drop ──────────────────────────────────────────────────────────
    [Fact]
    public async Task AfterOurOwnMove_TheHeadOnlyEcho_DropsWithoutAGet()
    {
        const string User = "31abcdefghijklmnopqrstuvwxyz";
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[]
        {
            "spotify:playlist:a", "spotify:start-group:g:Chill", "spotify:playlist:b",
            "spotify:playlist:c", "spotify:end-group:g", "spotify:playlist:d",
        }), Rev24(1));
        var src = Source(h.Store, new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200)));
        using var router = new DealerRouter(h.Dealer, h.Sync);

        await src.MoveRootlistItemAsync(P("d"), F("g"), RootlistDropPlacement.Inside, Ct);
        var applied = Uris(h.Store);
        Assert.Equal(RevOnlyReplyRevision(), h.Store.RootlistRevision());

        // The dealer's echo of that very write: head-only, AT the revision we just stored.
        var echo = new Pl.PlaylistModificationInfo
        {
            Uri = ByteString.CopyFromUtf8("spotify:user:" + User + ":rootlist"),
            NewRevision = ByteString.CopyFrom(RevOnlyReplyRevision()),
        };
        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/user/" + User + "/rootlist", echo.ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.RootlistEchoDropped);
        Assert.Equal(0, h.RootlistGets);                           // no GET — which is exactly why the rows had to be ours
        Assert.Equal(applied, Uris(h.Store));                      // …and they survive the echo unchanged
    }

    // ── the copy the user actually reads ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRefusalKinds_MapToTheDragChipsOwnSentences_AndAreNotErrors()
    {
        // The cue said "Already there" and refused to arm; if the same gesture somehow reaches the writer, the toast
        // must say the same thing rather than "couldn't save your change". One story, two channels.
        Assert.Equal(Strings.Drag.AlreadyThere,
                     PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.NoOp, PlaylistEditVerb.Reorder));
        Assert.Equal(Strings.Drag.CantMoveHere,
                     PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Invalid, PlaylistEditVerb.Reorder));
        // Nothing was lost, so neither is dressed in the error severity.
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.NoOp));
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.Invalid));
        // …and outside a reorder they fall back to the generic sentence rather than borrowing drag copy.
        Assert.Equal(Strings.Detail.Edit.Failed,
                     PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.NoOp, PlaylistEditVerb.Add));

        // The typed classification still survives a wrapper task / aggregate.
        var wrapped = new AggregateException(new PlaylistMutationException(PlaylistMutationFailure.NoOp, "x"));
        Assert.Equal(PlaylistMutationFailure.NoOp, PlaylistEditErrorKinds.KindOf(wrapped));
    }

    // -- the BATCH form: N items, ONE Delta ---------------------------------------------------------------------------
    //
    // A multi-selection drop is one gesture and must be one write. MoveRootlistItemsAsync is the only implementation
    // (the single-item call above is literally a batch of one), so everything the N=1 pins guarantee - build against
    // the CURRENT stream, apply locally BEFORE the POST, one 409 rebase that re-derives the ops, rollback of `before`,
    // a typed throw for a refusal - holds for N items without a second code path to keep in step.

    static RootlistMove Mv(RootlistItemRef source, RootlistItemRef target, RootlistDropPlacement placement)
        => new(source, target, placement);

    /// <summary>a b c d e - five flat top-level rows, for the net-identity case.</summary>
    static InMemoryStore FlatStore()
    {
        var store = new InMemoryStore();
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[]
        {
            "spotify:playlist:a", "spotify:playlist:b", "spotify:playlist:c",
            "spotify:playlist:d", "spotify:playlist:e",
        }), Rev24(1));
        return store;
    }

    /// <summary>{a, d} filed into the folder, in tree order - the ops of ONE delta, each built against the stream the
    /// previous one left (d is at index 5 to start with and still at 5 when its op is built, because a landed BEFORE
    /// it; the second op's ToIndex is the one that moved).</summary>
    static RootlistMove[] AAndDIntoTheFolder() => new[]
    {
        Mv(P("a"), F("g"), RootlistDropPlacement.Inside),
        Mv(P("d"), F("g"), RootlistDropPlacement.Inside),
    };

    [Fact]
    public async Task ABatch_PostsExactlyOneDelta_WithOneMovPerItem()
    {
        var store = Store();
        var transport = new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200));
        var src = Source(store, transport);

        await src.MoveRootlistItemsAsync(AAndDIntoTheFolder(), Ct);

        // ONE request, ONE ListChanges, ONE Delta - the wire shape BuildCreateFolder's two ADDs already ship.
        var one = Assert.Single(transport.Sent);
        Assert.Equal("POST", one.Method);
        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(one.Body).Deltas);
        Assert.Equal(2, delta.Ops.Count);
        Assert.All(delta.Ops, op => Assert.NotNull(op.Mov));
        Assert.Equal((5, 4), (delta.Ops[1].Mov.FromIndex, delta.Ops[1].Mov.ToIndex));  // built against the post-op-1 stream
        Assert.Equal((0, 4), (delta.Ops[0].Mov.FromIndex, delta.Ops[0].Mov.ToIndex));

        Assert.Equal(new[]
        {
            "spotify:start-group:g:Chill", "spotify:playlist:b", "spotify:playlist:c",
            "spotify:playlist:a", "spotify:playlist:d", "spotify:end-group:g",
        }, Uris(store));
        Assert.Equal(RevOnlyReplyRevision(), store.RootlistRevision());
    }

    [Fact]
    public async Task ABatchWhoseOpsNetToIdentity_ThrowsNoOp_AndPostsNothing()
    {
        // {b,c} Before d on [a,b,c,d,e]: two ops that each really move a row and together change nothing. Per-move
        // checks cannot see it; the final stream compared to the input can.
        var store = FlatStore();
        var before = Uris(store);
        var t = new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200));
        var src = Source(store, t);

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(() => src.MoveRootlistItemsAsync(new[]
        {
            Mv(P("b"), P("d"), RootlistDropPlacement.Before),
            Mv(P("c"), P("d"), RootlistDropPlacement.Before),
        }, Ct));

        Assert.Equal(PlaylistMutationFailure.NoOp, ex.Kind);
        Assert.Empty(t.Sent);
        Assert.Equal(before, Uris(store));
    }

    [Fact]
    public async Task ACycleAnywhereInABatch_RefusesTheWholeBatch_AndPostsNothing()
    {
        // The first move is perfectly legal. It is still not sent: half a filing is worse than none, and the user gets
        // the offending move's own sentence rather than a partially applied tree.
        var store = Store();
        var before = Uris(store);
        var t = new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200));
        var src = Source(store, t);

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(() => src.MoveRootlistItemsAsync(new[]
        {
            Mv(P("a"), P("b"), RootlistDropPlacement.Before),      // legal: a joins the folder
            Mv(F("g"), P("b"), RootlistDropPlacement.Before),      // the folder into its own subtree
        }, Ct));

        Assert.Equal(PlaylistMutationFailure.Invalid, ex.Kind);
        Assert.Empty(t.Sent);
        Assert.Equal(before, Uris(store));
    }

    [Fact]
    public async Task ABatchThatCannotBeSent_RestoresTheWholeTree_AndThrowsTyped()
    {
        // ONE optimistic apply, ONE rollback: both rows jump back together, because they moved together.
        var store = Store();
        var before = Uris(store);
        var t = new RecTransport((_, _, _) => new Resp(false, Array.Empty<byte>(), 503));
        var src = Source(store, t);

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(
            () => src.MoveRootlistItemsAsync(AAndDIntoTheFolder(), Ct));

        Assert.Equal(PlaylistMutationFailure.Unknown, ex.Kind);
        Assert.Equal(before, Uris(store));
        Assert.Equal(Rev24(1), store.RootlistRevision());
    }

    [Fact]
    public async Task A409_ReDerivesEveryOpInTheBatch_AgainstTheBootstrappedRootlist()
    {
        // The whole reason the sequential build lives INSIDE the builder delegate: on the rebase attempt every op is
        // recomputed against the stream that really exists. Replaying the first attempt's indices verbatim would file
        // the wrong rows - and with N ops the damage compounds.
        var store = Store();
        var bootstrap = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(2)) };
        bootstrap.Contents = new Pl.ListItems();
        foreach (var uri in new[]
                 {
                     "spotify:playlist:x", "spotify:playlist:a", "spotify:start-group:g:Chill",
                     "spotify:playlist:b", "spotify:playlist:c", "spotify:end-group:g", "spotify:playlist:d",
                 })
            bootstrap.Contents.Items.Add(new Pl.Item { Uri = uri });

        var t = new RecTransport((route, body, call) => call switch
        {
            1 => new Resp(false, Array.Empty<byte>(), 409),
            2 => new Resp(true, bootstrap.ToByteArray(), 200),      // the bootstrap GET
            _ => new Resp(true, RevOnlyReply(), 200),
        });
        var src = Source(store, t);

        await src.MoveRootlistItemsAsync(AAndDIntoTheFolder(), Ct);

        Assert.Equal(3, t.Sent.Count);
        Assert.Equal("GET", t.Sent[1].Method);
        var first = Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[0].Body).Deltas);
        var retry = Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[2].Body).Deltas);
        Assert.Equal(2, first.Ops.Count);
        Assert.Equal(2, retry.Ops.Count);
        Assert.Equal((0, 4), (first.Ops[0].Mov.FromIndex, first.Ops[0].Mov.ToIndex));
        Assert.Equal((5, 4), (first.Ops[1].Mov.FromIndex, first.Ops[1].Mov.ToIndex));
        Assert.Equal((1, 5), (retry.Ops[0].Mov.FromIndex, retry.Ops[0].Mov.ToIndex));   // every index shifted by the
        Assert.Equal((6, 5), (retry.Ops[1].Mov.FromIndex, retry.Ops[1].Mov.ToIndex));   // extra row the server holds

        Assert.Equal(new[]
        {
            "spotify:playlist:x", "spotify:start-group:g:Chill", "spotify:playlist:b", "spotify:playlist:c",
            "spotify:playlist:a", "spotify:playlist:d", "spotify:end-group:g",
        }, Uris(store));                                            // the SERVER's rows, with our batch applied on top
        Assert.Equal(RevOnlyReplyRevision(), store.RootlistRevision());
    }

    [Fact]
    public async Task TheSingleItemCall_IsABatchOfOne()
    {
        // No second write path: the N=1 sugar produces the very same delta the one-element batch does.
        var oneStore = Store();
        var oneT = new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200));
        await Source(oneStore, oneT).MoveRootlistItemAsync(P("d"), F("g"), RootlistDropPlacement.Inside, Ct);

        var batchStore = Store();
        var batchT = new RecTransport((_, _, _) => new Resp(true, RevOnlyReply(), 200));
        await Source(batchStore, batchT).MoveRootlistItemsAsync(
            new[] { Mv(P("d"), F("g"), RootlistDropPlacement.Inside) }, Ct);

        var single = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(Assert.Single(oneT.Sent).Body).Deltas).Ops).Mov;
        var batch = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(Assert.Single(batchT.Sent).Body).Deltas).Ops).Mov;
        Assert.Equal((single.FromIndex, single.Length, single.ToIndex), (batch.FromIndex, batch.Length, batch.ToIndex));
        Assert.Equal(Uris(oneStore), Uris(batchStore));
    }
}
