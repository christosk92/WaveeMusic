using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Wavee.Core;
using Xunit;
using Col = Wavee.Protocol.Collection;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// A live LibrarySync over a faked HTTP exchange + a StubTransport (dealer pushes + the mutation transport). The loop is a
// background consumer; tests await completion via a command's Done TCS (InitialHydrate/OpenPlaylist) or WaitForIdleAsync.
sealed class SyncHarness : IAsyncDisposable
{
    public readonly InMemoryStore Store = new();
    public readonly StubTransport Dealer = new();
    public readonly MutationEngine Mut;
    public readonly CollectionEchoRing Echo;
    public readonly ReplicaTestHost Host;
    public readonly ITransport Transport;
    public Dictionary<string, string?> Revs => CollectionSets.WireSets.Select(w => (Wire: w,
        Token: Host.Replicas.ReadConfirmedCollection(CollectionSets.LogicalSetsForWireSet(w)[0]).WireRevision))
        .Where(x => x.Token is not null).ToDictionary(x => x.Wire, x => x.Token);
    public int PlaylistGets, RootlistGets, CollectionPosts;
    public readonly LibrarySync Sync;
    public readonly PlaylistResyncQueue Resync = new();
    public readonly List<string> TransportRoutes = new();
    public static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);
    public SyncHarness(Func<HttpReq, HttpResp> responder, Func<string, Resp>? transportRespond = null)
    {
        Host = new ReplicaTestHost(store: Store);
        Mut = Host.Mutations; Echo = Host.Echo;
        var http = new FakeExchange((req, _) =>
        {
            if (req.Url.Contains("/rootlist")) RootlistGets++;
            else if (req.Url.Contains("/playlist/v2/")) PlaylistGets++;
            else if (req.Url.Contains("/collection/v2/")) CollectionPosts++;
            return responder(req);
        });
        Transport = new HarnessTransport(Dealer, TransportRoutes, transportRespond);
        Sync = Host.AttachSync(http, Transport, resync: Resync);
    }
    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

// The mutation transport the loop drains + seeds permissions over. Dealer pushes still ride the real StubTransport (the
// router subscribes to it directly); only Request() is scriptable, so a test can answer the permission GET.
sealed class HarnessTransport(StubTransport inner, List<string> routes, Func<string, Resp>? respond) : ITransport
{
    public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
        string? method = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        lock (routes) routes.Add(route);
        return respond is null
            ? inner.Request(ch, route, body, ct, method, headers)
            : Task.FromResult(respond(route));
    }

    public IObservable<WireEvent> Events(string topicPrefix) => inner.Events(topicPrefix);
    public IObservable<WireRequest> Requests(string identPrefix) => inner.Requests(identPrefix);
    public Task Reply(string requestId, RequestResult result) => inner.Reply(requestId, result);
    public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
        => inner.Publish(deviceId, connectionId, putState, ct);
}

sealed class ChangeCollector : IObserver<StoreChange>
{
    public readonly List<StoreChange> All = new();
    public void OnNext(StoreChange v) { lock (All) All.Add(v); }
    public void OnCompleted() { }
    public void OnError(Exception e) { }
}

sealed class ChangeObserver(Action<StoreChange> onChange) : IObserver<StoreChange>
{
    public void OnNext(StoreChange v) => onChange(v);
    public void OnCompleted() { }
    public void OnError(Exception e) { }
}

sealed class FailTransport : ITransport
{
    public int Calls;
    public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
        string? method = null, IReadOnlyDictionary<string, string>? headers = null)
    { Calls++; return Task.FromResult(new Resp(false, Array.Empty<byte>(), 500)); }
    public IObservable<WireEvent> Events(string topicPrefix) => throw new NotImplementedException();
    public IObservable<WireRequest> Requests(string identPrefix) => throw new NotImplementedException();
    public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
    public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default) => throw new NotImplementedException();
}

public class LibrarySyncTests
{
    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);
    static PlaylistMember M(string id, string uri) => new(id, uri, null, 0);
    // I1 — only a 24-byte head is storable; fixtures that expect their revision to be adopted must carry one.
    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }
    static Track Trk(string uri, string title = "Hydrated")
    {
        var id = uri[(uri.LastIndexOf(':') + 1)..];
        return new Track(id, uri, title, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);
    }

    // Route the shared exchange by URL: rootlist GET, playlist GET, collection POST (set-appropriate items by wire set).
    static HttpResp HydrateResponder(HttpReq req)
    {
        if (req.Url.Contains("/rootlist"))
        {
            var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(9)) };
            var c = new Pl.ListItems { Pos = 0, Truncated = false };
            c.Items.Add(new Pl.Item { Uri = "spotify:playlist:p1" });
            c.Items.Add(new Pl.Item { Uri = "spotify:playlist:p2" });
            slc.Contents = c;
            return Ok(slc.ToByteArray());
        }
        if (req.Url.Contains("/collection/v2/delta"))
        {
            // A wire set that already holds a token asks for a delta: answer "nothing changed" (an honoured, empty
            // delta), so a second pass over a converged set is one cheap round-trip rather than a re-walk.
            var set = Col.DeltaRequest.Parser.ParseFrom(req.Body).Set;
            return Ok(new Col.DeltaResponse { DeltaUpdatePossible = true, SyncToken = "tok-" + set }.ToByteArray());
        }
        if (req.Url.Contains("/collection/v2/paging"))
        {
            var set = Col.PageRequest.Parser.ParseFrom(req.Body).Set;
            var p = new Col.PageResponse { SyncToken = "tok-" + set, NextPageToken = "" };
            switch (set)
            {
                case "collection": p.Items.Add(new Col.CollectionItem { Uri = "spotify:track:t1", AddedAt = 1 }); p.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a1", AddedAt = 2 }); break;
                case "artist": p.Items.Add(new Col.CollectionItem { Uri = "spotify:artist:ar1", AddedAt = 1 }); break;
                case "show": p.Items.Add(new Col.CollectionItem { Uri = "spotify:show:s1", AddedAt = 1 }); break;
                case "listenlater": p.Items.Add(new Col.CollectionItem { Uri = "spotify:episode:e1", AddedAt = 1 }); break;
                // A pin set mixes kinds AND carries stray uris this client cannot pin (a track) — CollectionSets.AcceptsUri
                // is what keeps the track out of the "pins" logical set.
                case "ylpin":
                    p.Items.Add(new Col.CollectionItem { Uri = "spotify:playlist:pin1", AddedAt = 1 });
                    p.Items.Add(new Col.CollectionItem { Uri = "spotify:track:notapin", AddedAt = 2 });
                    break;
            }
            return Ok(p.ToByteArray());
        }
        return Ok(Array.Empty<byte>());
    }

    // A full playlist fetch response carrying ITEM attributes (added_by + timestamp) — the attribute-bearing path.
    static byte[] FullSlcWithAttrs(byte[] rev, params (string Uri, string AddedBy, long At)[] items)
    {
        var slc = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(rev),
            OwnerUsername = "someowner",
            Attributes = new Pl.ListAttributes { Name = "Poisoned Mix" },
            Capabilities = new Pl.Capabilities { CanView = true },   // non-default + non-heal shape → no header re-fetch
        };
        var c = new Pl.ListItems { Pos = 0, Truncated = false };
        foreach (var it in items)
            c.Items.Add(new Pl.Item { Uri = it.Uri, Attributes = new Pl.ItemAttributes { AddedBy = it.AddedBy, Timestamp = it.At } });
        slc.Contents = c;
        return slc.ToByteArray();
    }

    // A full playlist fetch response with NO item attributes — a genuinely attribute-less server playlist.
    static byte[] FullSlcNoAttrs(byte[] rev, params string[] uris)
    {
        var slc = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(rev),
            OwnerUsername = "someowner",
            Attributes = new Pl.ListAttributes { Name = "No Attrs" },
            Capabilities = new Pl.Capabilities { CanView = true },
        };
        var c = new Pl.ListItems { Pos = 0, Truncated = false };
        foreach (var u in uris) c.Items.Add(new Pl.Item { Uri = u });
        slc.Contents = c;
        return slc.ToByteArray();
    }

    // ── Finding #3 (library-v3-1-findings-2026-09-06.md Part 2 2.1 #3) ──────────────────────────────────────────────
    // One unreachable playlist must not abort the whole outbox drain.

    [Fact]
    public async Task DrainWrites_OnePlaylist404sDuringRecovery_OtherStillReachesTheWire_FirstNeedsAttention()
    {
        const string p1 = "spotify:playlist:p1", p2 = "spotify:playlist:p2";
        await using var h = new SyncHarness(req =>
            req.Url.Contains("/playlist/p1") ? new HttpResp(404, new Dictionary<string, string>(), Array.Empty<byte>()) : Ok(Array.Empty<byte>()));

        // p2 is seeded as an ALREADY-VERIFIED, resident baseline (the same shape OpenPlaylist_WhileADrainIsInFlight_
        // ServesTheCachedSnapshotImmediately uses) — its intent is replayable from the start, with no fetch of its
        // own needed. That isolates finding #3's actual claim (p1's failed recovery must not stop the drain from
        // reaching an intent that was ALREADY ready to send) from the separate question of whether a fetch-driven
        // recovery within the SAME drain pass can itself be replayed immediately after landing.
        await h.Host.SeedHeaderAsync(new Playlist("p2", p2, "Mine", null, "bob", null, 0));
        await h.Host.SeedPlaylistAsync(p2, new[] { M("a01", "spotify:track:a") }, Rev24(1));

        // Add(AddLast) never goes "out of range" — safe to stage against a playlist with no known baseline yet,
        // unlike a positional Remove (which the projection would tear/reject immediately against empty rows). Item ids
        // are 16 hex chars: PlaylistWireMapper hex-decodes them, and a non-hex id fails BEFORE the transport call.
        await h.Mut.EditAsync(p1, new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("00000000000000a1", "spotify:track:t1") }) });
        await h.Mut.EditAsync(p2, new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("00000000000000a2", "spotify:track:t2") }) });

        await h.Sync.DrainWritesAsync(TestContext.Current.CancellationToken);

        // p1's 404 is definitive: baseline missing, its intent NeedsAttention — but that does not abort the drain.
        var p1Intent = Assert.Single(h.Host.Replicas.Intents, x => x.EntityKey == p1);
        Assert.Equal(ReplicaIntentState.NeedsAttention, p1Intent.State);
        Assert.Equal(ReplicaBaselineState.Missing, h.Host.Replicas.ReadConfirmedPlaylist(p1).State);

        // p2's already-replayable intent still reached the mutation transport — the drain kept going past p1's failure.
        Assert.Contains(h.TransportRoutes, r => r.Contains("playlist/p2"));
    }

    // An op that cannot be encoded for the wire (a non-hex item id here) never reaches the transport. That is a
    // deterministic failure, not a network fault: the intent is rejected with its reason and nothing is retried —
    // the drain used to swallow the exception as a silent Retry and burn ten attempts on it.
    [Fact]
    public async Task DrainWrites_AnUnencodableOp_IsRejectedWithItsReason_AndNeverReachesTheWire()
    {
        const string p = "spotify:playlist:p1";
        await using var h = new SyncHarness(req => Ok(FullSlcWithAttrs(Rev24(1), ("spotify:track:a", "bob", 1))));
        await h.Host.SeedHeaderAsync(new Playlist("p1", p, "Mine", null, "bob", null, 0));
        await h.Host.SeedPlaylistAsync(p, new[] { M("aaaaaaaaaaaaaa01", "spotify:track:a") }, Rev24(1));
        await h.Mut.EditAsync(p, new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("not-hex", "spotify:track:t") }) });

        await h.Sync.DrainWritesAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(h.TransportRoutes, r => r.Contains("/changes"));
        Assert.DoesNotContain(h.Host.Replicas.Intents, x => x.EntityKey == p && x.State == ReplicaIntentState.Pending);
    }

    [Fact]
    public async Task DrainWrites_BaselineRecovery5xx_BothStayPendingAndRedrainIsScheduled()
    {
        const string p1 = "spotify:playlist:p1", p2 = "spotify:playlist:p2";
        await using var h = new SyncHarness(req =>
            req.Url.Contains("/playlist/v2/") ? new HttpResp(503, new Dictionary<string, string>(), Array.Empty<byte>()) : Ok(Array.Empty<byte>()));

        await h.Mut.EditAsync(p1, new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("00000000000000a1", "spotify:track:t1") }) });
        await h.Mut.EditAsync(p2, new[] { new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("00000000000000a2", "spotify:track:t2") }) });

        await h.Sync.DrainWritesAsync(TestContext.Current.CancellationToken);

        // An ambiguous (5xx) baseline-recovery failure leaves BOTH intents Pending, never NeedsAttention.
        Assert.Equal(2, h.Host.Replicas.Intents.Length);
        Assert.All(h.Host.Replicas.Intents, x => Assert.Equal(ReplicaIntentState.Pending, x.State));

        int getsAfterFirstDrain = h.PlaylistGets;
        await PollAsync(h.Sync, () => h.PlaylistGets > getsAfterFirstDrain, TestContext.Current.CancellationToken, timeoutMs: 3000);
        Assert.True(h.PlaylistGets > getsAfterFirstDrain);   // the drain re-armed itself in a `finally` and retried on its own
    }

    // ── Finding #2(b) (library-v3-1-findings-2026-09-06.md Part 2 2.1 #2) ───────────────────────────────────────────
    // A verification fetch that itself fails offline must not count as an attempt toward NeedsAttention.

    [Fact]
    public async Task VerifyIntent_OfflineFetchFails_NeverCountsTowardNeedsAttention()
    {
        const string uri = "spotify:playlist:p";
        await using var h = new SyncHarness(req =>
            req.Url.Contains("/playlist/v2/") ? new HttpResp(503, new Dictionary<string, string>(), Array.Empty<byte>()) : Ok(Array.Empty<byte>()));

        var intent = await h.Host.Replicas.StageAsync("oprebase", uri, uri, false, [],
            initialState: ReplicaIntentState.AwaitingVerification);

        for (int i = 0; i < 3; i++)
        {
            h.Sync.Enqueue(new SyncCommand(SyncKind.VerifyIntent, IntentId: intent.Id));
            await h.Sync.WaitForIdleAsync();
            var current = Assert.Single(h.Host.Replicas.Intents, x => x.Id == intent.Id);
            Assert.Equal(ReplicaIntentState.AwaitingVerification, current.State);   // deferred, never NeedsAttention
            Assert.Equal(0, current.Attempts);                                     // the offline fetch never burned an attempt
        }
    }

    // -- the open page must NEVER queue behind a write --------------------------------------------------------------
    // The sync loop is a SINGLE-READER FIFO, so a DrainWrites command parked on a slow POST holds every later command
    // behind it - OpenPlaylist included. That is exactly why a playlist which already has a membership baseline must
    // not reach LibrarySync.OpenPlaylistAsync on the read path: OpenPolicy hands a baselined open a background-only
    // plan and PlaylistHydration answers it with the fire-and-forget Revalidate, so the page paints the cache NOW and
    // lets the loop's own 5-minute/dirty gates decide whether anything is fetched. Pinned here because the failure mode
    // hides behind a fast server: with a slow one, the page's own optimistic edit waits out the whole write.
    [Fact]
    public async Task OpenPlaylist_WhileADrainIsInFlight_ServesTheCachedSnapshotImmediately()
    {
        const string uri = "spotify:playlist:p1";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new SyncHarness(req => req.Url.Contains("/playlist/v2/")
                ? Ok(FullSlcNoAttrs(Rev24(2), "spotify:track:a", "spotify:track:b")) : HydrateResponder(req),
            // The mutation transport parks: the drain command owns the loop until the test lets go.
            transportRespond: _ => { release.Task.GetAwaiter().GetResult(); return new Resp(true, Array.Empty<byte>(), 200); });
        try
        {
            // A resident baseline plus a LOCAL edit whose optimistic effect is already in the store.
            await h.Host.SeedHeaderAsync(new Playlist("p1", uri, "Mine", null, "bob", null, 0));
            await h.Host.SeedPlaylistAsync(uri, new[] { M("aaaaaaaaaaaaaa01", "spotify:track:a") }, Rev24(1));
            await h.Mut.EditAsync(uri, new[]
            {
                new PlaylistOp(PlaylistOpKind.Add, FromIndex: 1,
                    Items: new[] { new PlaylistMember("aaaaaaaaaaaaaa02", "spotify:track:b", "bob", 7) }),
            }, Rev24(1));

            // The optimistic row is resident BEFORE anything touches the wire - this is what the page has to be able to read.
            Assert.Equal(new[] { "aaaaaaaaaaaaaa01", "aaaaaaaaaaaaaa02" }, h.Store.Membership(uri).Select(m => m.ItemId).ToArray());

            var drain = h.Sync.DrainWritesAsync(TestContext.Current.CancellationToken);
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.False(drain.IsCompleted);                       // the loop really is parked on the write

            // (a) The cached snapshot serves NOW: the read model is complete and unaffected by the parked write.
            Assert.Equal(new[] { "aaaaaaaaaaaaaa01", "aaaaaaaaaaaaaa02" }, h.Store.Membership(uri).Select(m => m.ItemId).ToArray());
            // (b) ...and the on-open plan for a baselined playlist never blocks on that loop.
            Assert.Equal(2, h.Host.Replicas.ReadPlaylist(uri).Members.Length);
            // (c) The distinction matters: the BLOCKING open really does queue behind the drain.
            var queued = h.Sync.OpenPlaylistAsync(uri, TestContext.Current.CancellationToken);
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);

            release.SetResult();
            await drain;
            await queued;
        }
        finally { release.TrySetResult(); }
    }

    // ── the attribute-aware heal gate (Date-added / Added-by regression) ──────────────────────────────────────────────
    // A resident-but-attribute-less membership (rows cached WITHOUT Item.attributes, e.g. the followed "Summer 2016 vibes"
    // playlist) must open through the FULL attribute-bearing fetch, not the /diff revalidate — /diff never re-reads
    // attributes for existing rows, so the poisoned cache would otherwise serve blank added_at/added_by forever.
    [Fact]
    public async Task OpenPlaylist_InvalidRevision_ReplacesUnverifiedRowsWithFullSnapshot()
    {
        const string uri = "spotify:playlist:poisoned";
        int diffs = 0, fulls = 0;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/diff?")) { Interlocked.Increment(ref diffs); return Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()); }
            Interlocked.Increment(ref fulls);
            return Ok(FullSlcWithAttrs(Rev24(2),
                ("spotify:track:t1", "alice", 1_700_000_000_000L), ("spotify:track:t2", "bob", 1_700_000_100_000L)));
        });
        // resident membership recorded WITHOUT item attributes (the poisoned cache) + a revision (so /diff would be taken).
        await h.Host.SeedPlaylistAsync(uri, new[] { M("i1", "spotify:track:t1"), M("i2", "spotify:track:t2") }, new byte[] { 1 });

        await h.Sync.OpenPlaylistAsync(uri, CancellationToken.None);

        Assert.Equal(0, diffs);                                   // NOT the /diff revalidate path
        Assert.Equal(1, fulls);                                   // the full attribute-bearing fetch was chosen
        var healed = h.Store.Membership(uri);
        Assert.Equal(2, healed.Count);
        Assert.Equal("alice", healed[0].AddedBy);                 // rows now carry added_by …
        Assert.True(healed[0].AddedAt > 0);                       // … and added_at
        Assert.Equal("bob", healed[1].AddedBy);
        Assert.True(healed[1].AddedAt > 0);
    }

    // The loop guard: a playlist whose server data GENUINELY has no attributes stays attribute-less after the heal fetch,
    // so it must force the full GET only ONCE per session — never storm one on every open.
    [Fact]
    public async Task OpenPlaylist_InvalidRevision_RefetchesOnce_ThenHonorsFreshnessWithNoAttributes()
    {
        const string uri = "spotify:playlist:noattrs";
        int fulls = 0;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/diff?")) return Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray());
            Interlocked.Increment(ref fulls);
            return Ok(FullSlcNoAttrs(Rev24(2), "spotify:track:t1"));
        });
        await h.Host.SeedPlaylistAsync(uri, new[] { M("i1", "spotify:track:t1") }, new byte[] { 1 });

        await h.Sync.OpenPlaylistAsync(uri, CancellationToken.None);
        Assert.Equal(1, fulls);                                   // forced once

        await h.Sync.OpenPlaylistAsync(uri, CancellationToken.None);
        Assert.Equal(1, fulls);                                   // NOT forced again this session (still attribute-less, but guarded)
    }

    [Fact]
    public async Task InitialHydrate_PopulatesRootlistSetsTokensAndFold_CoalescedIntoBulkSignals()
    {
        await using var h = new SyncHarness(HydrateResponder);
        var col = new ChangeCollector();
        using var sub = h.Store.Changes.Subscribe(col);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
        await done.Task;

        // rootlist + set members landed
        Assert.Equal(2, h.Store.Rootlist().Count);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));
        Assert.True(h.Store.IsSaved("artists", "spotify:artist:ar1"));
        Assert.True(h.Store.IsSaved("shows", "spotify:show:s1"));
        Assert.True(h.Store.IsSaved("episodes", "spotify:episode:e1"));
        // tokens advanced — keyed by WIRE set (one walk of "collection" covers liked + albums), never by logical set
        Assert.Equal("tok-collection", h.Revs["collection"]);
        Assert.Equal("tok-artist", h.Revs["artist"]);
        Assert.Equal("tok-show", h.Revs["show"]);
        Assert.Equal("tok-listenlater", h.Revs["listenlater"]);
        Assert.Equal("tok-ylpin", h.Revs["ylpin"]);
        Assert.False(h.Revs.ContainsKey("liked"));
        // the "playlists" saved-set fold
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:p1"));
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:p2"));
        // the pins set: the playlist landed, the stray track did not (CollectionSets.AcceptsUri)
        Assert.True(h.Store.IsSaved("pins", "spotify:playlist:pin1"));
        Assert.False(h.Store.IsSaved("pins", "spotify:track:notapin"));
        // one Bulk-coalesced signal per burst — no per-uri change leaked (rootlist+fold = 1, then 5 wire sets = 5).
        List<StoreChange> snap; lock (col.All) snap = new List<StoreChange>(col.All);
        Assert.All(snap, c => Assert.True(c.IsBulk));
        Assert.Equal(6, snap.Count);
    }

    [Fact]
    public async Task InitialHydrate_WalksFiveWireSets_NotSixLogicalSets()
    {
        // liked + albums ride the same "collection" snapshot: walking it twice was how each logical set's sweep could
        // run over a truncated copy of the other's. One walk per wire set, one token per wire set.
        await using var h = new SyncHarness(HydrateResponder);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
        await done.Task;

        Assert.Equal(5, h.CollectionPosts);
        Assert.Equal(5, h.Sync.SetFetches);
        Assert.Equal(new[] { "collection", "artist", "show", "listenlater", "ylpin" }, h.Revs.Keys.OrderBy(k => Array.IndexOf(CollectionSets.WireSets, k)).ToArray());
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));
        Assert.Equal(0, h.Sync.ReconcilePasses);   // boot arms the periodic pass; it does not run one
    }

    [Fact]
    public async Task CollectionPush_TwoRapidPushesForSameWireSet_FoldToOneSettledFetch()
    {
        await using var h = new SyncHarness(HydrateResponder);

        // Two pushes for the same WIRE set inside the settle window: the second must fold into the first (dropped), NOT re-arm.
        // Done rides the first push and completes when the single settled fetch finishes — a deterministic barrier, not a sleep.
        // The "collection" wire set carries BOTH liked and albums; the settled fetch walks it ONCE and fans the items out.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Done: done));
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection"));   // within the window → folded
        Assert.True(h.Sync.IsSetSyncing("collection"));                           // syncing from the first push

        await done.Task;

        Assert.Equal(1, h.Sync.SetFetches);              // one settled push → ONE walk of the wire set (liked + albums both land)
        Assert.Equal(1, h.CollectionPosts);              // one HTTP hit — the second push did not re-arm the settle
        Assert.False(h.Sync.IsSetSyncing("collection")); // cleared once the fetch completed
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));
    }

    [Fact]
    public async Task CollectionPushSettle_DoesNotBlockAFollowingPlaylistPush()
    {
        await using var h = new SyncHarness(HydrateResponder);
        var uri = "spotify:playlist:pr";
        var rev0 = Rev24(1);
        var rev1 = Rev24(2);
        await h.Host.SeedPlaylistAsync(uri, new[] { new Wavee.Backend.Playlists.PlaylistMember("id1", "spotify:track:a", null, 0) }, rev0);
        var ops = new[]
        {
            new Wavee.Backend.Playlists.PlaylistOp(Wavee.Backend.Playlists.PlaylistOpKind.Add, AddLast: true,
                Items: new[] { new Wavee.Backend.Playlists.PlaylistMember("id2", "spotify:track:b", null, 0) }),
        };

        // A collection push arms its 250ms settle OFF the consumer; a PlaylistPush enqueued immediately after must apply
        // right away — it is not queued behind the settle. WaitForIdleAsync drains the consumer (playlist push + the idle
        // sentinel) in microseconds, well under the settle, so this is deterministic (no real-time sleep).
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection"));
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, ParentRev: rev0, NewRev: rev1, Ops: ops));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PushApplied);                  // the playlist push applied in place — not blocked
        Assert.Equal(2, h.Store.Membership(uri).Count);       // membership grew (track b added)
        Assert.Equal(0, h.CollectionPosts);                   // the collection settle is still pending — it never stalled the loop
        Assert.True(h.Sync.IsSetSyncing("collection"));       // wire set is still settling off-thread
    }

    [Fact]
    public async Task OpenPlaylistAsync_ConcurrentOpens_DedupToOneFetch()
    {
        int gets = 0;
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(3) };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:x" });
        slc.Contents = contents;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/playlist/v2/")) gets++;
            return Ok(slc.ToByteArray());
        });

        var t1 = h.Sync.OpenPlaylistAsync("spotify:playlist:p", CancellationToken.None);
        var t2 = h.Sync.OpenPlaylistAsync("spotify:playlist:p", CancellationToken.None);
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, gets);                                              // one fetch, both awaiters
        Assert.Single(h.Store.Membership("spotify:playlist:p"));
    }

    // OpenPlaylistAsync_FiresTheHydratedHook_AfterTheTracklistLands is DELETED with LibrarySync.OnPlaylistHydrated
    // (hydration-facade-plan.md 1.6): the hook existed so music-video detection could hang off a live open, and the
    // playlist ladder's post-step owns that now. Replacement: PlaylistHydrationTests.Open_AsksTraitsForEveryMember_EpisodesIncluded
    // (the traits pass runs AFTER the membership is resident, over every member) plus .NeverWritesMembership,
    // which pins the invariant the hook's removal depends on - the ladder asks, LibrarySync writes.

    [Fact]
    public async Task PlaylistPush_PublishesMembershipWithoutMetadataRepair()
    {
        const string uri = "spotify:playlist:p";
        await using var h = new SyncHarness(HydrateResponder);
        await h.Host.SeedPlaylistAsync(uri, [M("old", "spotify:track:old")], Rev24(1));
        var changes = new List<ReplicaChange>();
        using var subscription = h.Host.Replicas.Changes.Subscribe(Observers.From<ReplicaChange>(changes.Add));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, ParentRev: Rev24(1), NewRev: Rev24(2),
            Ops: [new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: [M("new", "spotify:track:new")])], Done: done));
        await done.Task;
        Assert.Equal(2, h.Host.Replicas.ReadPlaylist(uri).Members.Length);
        Assert.Single(changes);
        Assert.Null(h.Host.ReadTrack("spotify:track:new"));
        Assert.Equal(0, h.PlaylistGets);
    }

    [Fact]
    public async Task PlaylistPush_UpdateListAttributes_RefetchesHeader()
    {
        var uri = "spotify:playlist:p";
        var rev0 = Rev24(1);
        var rev1 = Rev24(2);
        var header = new Pl.SelectedListContent { Length = 7, OwnerUsername = "bob" };
        header.Attributes = new Pl.ListAttributes { Name = "Renamed", Description = "fresh" };
        await using var h = new SyncHarness(req => req.Url.Contains("/playlist/v2/") ? Ok(header.ToByteArray()) : Ok(Array.Empty<byte>()));
        await h.Host.SeedHeaderAsync(new Playlist("p", uri, "Old", null, "bob", null, 1));
        await h.Host.SeedPlaylistAsync(uri, new[] { M("old", "spotify:track:old") }, rev0);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, ParentRev: rev0, NewRev: rev1,
            Ops: new[] { new PlaylistOp(PlaylistOpKind.UpdateList) }, Done: done));
        await done.Task;

        Assert.Equal(1, h.PlaylistGets);
        var playlist = h.Host.ReadHeader(uri);
        Assert.NotNull(playlist);
        Assert.Equal("Renamed", playlist.Name);
        Assert.Equal("fresh", playlist.Description);
        Assert.Equal(7, ((Wavee.Core.Catalog.PlaylistHeaderValue)h.Host.Catalog.Peek(
            new(h.Host.Catalog.Scope, uri, Wavee.Core.Catalog.FacetKind.PlaylistHeader)).Value!).TrackCount);
    }

    [Fact]
    public async Task MarkAndSweep_FullPaging_RemovesConfirmedAbsent_AndPreservesPendingProjection()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedCollectionAsync("collection", [new("spotify:album:gone", false, 1)]);
        await host.Mutations.SaveAsync("albums", "spotify:album:pending", true);
        var page = new Col.PageResponse { SyncToken = "t2", NextPageToken = "" };
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 1 });
        var http = new FakeExchange((_, _) => Ok(page.ToByteArray()));
        var fetcher = new CollectionFetcher(http, () => "https://x", () => "bob");
        await host.Replicas.AdoptCollectionAsync(await fetcher.FetchWireSetAsync("collection", null));
        Assert.Equal(new[] { "spotify:album:a", "spotify:album:pending" }, host.Replicas.ReadCollection("albums").Items.Select(x => x.Uri).OrderBy(x => x));
        Assert.Equal("t2", host.Replicas.ReadConfirmedCollection("albums").WireRevision);
    }

    [Fact]
    public async Task MarkAndSweep_MidPagingThrow_LeavesPartial_NoSweep_TokenNotAdvanced()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedCollectionAsync("collection", [new("spotify:album:gone", false, 1)], "prior");
        var page = new Col.PageResponse { SyncToken = "t1", NextPageToken = "p2" };
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 1 });
        int calls = 0;
        var http = new FakeExchange((_, _) => ++calls == 1 ? Ok(page.ToByteArray()) : new HttpResp(500, new Dictionary<string, string>(), []));
        var read = await new CollectionFetcher(http, () => "https://x", () => "bob").FetchWireSetAsync("collection", null);
        Assert.False(read.Verified);
        await host.Replicas.AdoptCollectionAsync(read);
        Assert.Equal(2, host.Replicas.ReadCollection("albums").Items.Length);
        Assert.Equal("prior", host.Replicas.ReadConfirmedCollection("albums").WireRevision);
    }

    // Finding #2 (library-v3-1-findings-2026-09-06.md Part 2 2.1 #2): this used to pin the OLD BUG — a "set" (like)
    // write that got an ambiguous 500 reply flipped to AwaitingVerification, which is exactly the state 3 failed
    // OFFLINE verification fetches turn into permanent NeedsAttention. A "set" write is idempotent, so the correct
    // contract is Retry: it stays Pending (with attempts/backoff climbing) and is resent — never Verify/NeedsAttention.
    // "Sent once" still holds across the two Drain calls here because the backoff timer (min 60s, 2^attempts) blocks
    // the second attempt, not because the intent stopped being replayable.
    [Fact]
    public async Task AmbiguousWrite_IsSentOnce_ThenStaysPendingForRetry()
    {
        await using var host = new ReplicaTestHost();
        await host.Mutations.SaveAsync("liked", "spotify:track:a", true);
        var transport = new FailTransport();
        await host.Mutations.Drain(transport, host.Context);
        await host.Mutations.Drain(transport, host.Context);
        Assert.Equal(1, transport.Calls);
        var intent = Assert.Single(host.Replicas.Intents);
        Assert.Equal(ReplicaIntentState.Pending, intent.State);
        Assert.Equal(1, intent.Attempts);
        Assert.Single(host.Replicas.ReadCollection("liked").Items);
    }

    [Fact]
    public async Task SwitchableTransport_SetInner_RoutesRequestToNewInner()
    {
        var a = new StubTransport();
        var b = new StubTransport();
        var sw = new SwitchableTransport(a);

        await sw.Request(Channel.Spclient, "/x", default);
        Assert.Equal(1, a.RequestCount);
        Assert.Equal(0, b.RequestCount);

        sw.SetInner(b);
        await sw.Request(Channel.Spclient, "/y", default);
        Assert.Equal(1, a.RequestCount);               // old inner untouched
        Assert.Equal(1, b.RequestCount);
        Assert.Equal("/y", b.LastRequestRoute);
    }

    // ── §2.2 E — PubSubUpdate direct-apply + echo suppression + wire→logical translation ──
    static WireEvent ColPush(string wireSet, Col.PubSubUpdate upd) =>
        new("hm://collection/" + wireSet + "/bob", upd.ToByteArray());

    [Fact]
    public async Task CollectionPush_EchoOfOurAcceptedWrite_IsDropped_StoreUntouched()
    {
        await using var h = new SyncHarness(HydrateResponder);

        // A like that goes out and is accepted records its client_update_id in the shared echo ring.
        await h.Mut.SaveAsync("liked", "spotify:track:z", true);
        await h.Mut.Drain(h.Transport, new SessionContext("bob", "US", "premium", "en", Tier.Premium, false),
            TestContext.Current.CancellationToken);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:z"));                        // optimistic → Confirmed on ack
        var cuid = Col.WriteRequest.Parser.ParseFrom(h.Dealer.LastRequestBody).ClientUpdateId;
        Assert.NotEmpty(cuid);

        // The dealer echoes our own write back (same cuid) as a removal — it MUST be dropped before any store work.
        var echo = new Col.PubSubUpdate { Set = "collection", ClientUpdateId = cuid };
        echo.Items.Add(new Col.CollectionItem { Uri = "spotify:track:z", IsRemoved = true, AddedAt = 1 });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Payload: echo.ToByteArray(), Done: done));
        await done.Task;

        Assert.Equal(1, h.Sync.EchoDropped);
        Assert.Equal(0, h.Sync.PushDirectApplied);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:z"));   // the echoed removal was dropped → still saved
        Assert.Equal(0, h.CollectionPosts);                         // zero fetch
    }

    [Fact]
    public async Task CollectionPush_ForeignUpdateWithItems_AppliesDirectly_ShieldsPending_NoFetch()
    {
        await using var h = new SyncHarness(HydrateResponder);
        // A pending local intent shields (liked, t:pending) — a foreign push trying to REMOVE it must be skipped.
        await h.Mut.SaveAsync("liked", "spotify:track:pending", true);

        var upd = new Col.PubSubUpdate { Set = "collection" };   // foreign: no client_update_id
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:track:t9", IsRemoved = false, AddedAt = 5 });
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a9", IsRemoved = false, AddedAt = 6 });
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:track:pending", IsRemoved = true, AddedAt = 7 });

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Payload: upd.ToByteArray(), Done: done));
        await done.Task;

        Assert.Equal(1, h.Sync.PushDirectApplied);
        Assert.Contains("spotify:track:t9", h.Store.SavedUris("liked"));    // track → liked
        Assert.Contains("spotify:album:a9", h.Store.SavedUris("albums"));   // album → albums
        Assert.True(h.Store.IsSaved("liked", "spotify:track:pending"));     // shielded removal skipped → survives
        Assert.Equal(0, h.CollectionPosts);                                 // zero round-trip
        // HydrateUrisAsync (the spec's hydrate path) covers added track/episode uris; albums ride the next delta/on-open fetch.
    }

    [Fact]
    public async Task CollectionPush_PublishesMembershipWithoutMetadataTransport()
    {
        await using var h = new SyncHarness(HydrateResponder);
        var update = new Col.PubSubUpdate { Set = "collection" };
        update.Items.Add(new Col.CollectionItem { Uri = "spotify:track:new", IsRemoved = false, AddedAt = 5 });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Payload: update.ToByteArray(), Done: done));
        await done.Task;
        Assert.Equal("spotify:track:new", Assert.Single(h.Host.Replicas.ReadCollection("liked").Items).Uri);
        Assert.Null(h.Host.ReadTrack("spotify:track:new"));
        Assert.Equal(0, h.CollectionPosts);
    }

    [Fact]
    public async Task CollectionPush_UnparseablePayload_FallsBackToSettledDeltaFetch()
    {
        await using var h = new SyncHarness(HydrateResponder);

        // Garbage payload → not a PubSubUpdate → settle + delta fetch (one wire set → its logical fetches).
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "artist", Payload: new byte[] { 0xFF, 0xFF, 0xFF }, Done: done));
        await done.Task;

        Assert.Equal(0, h.Sync.PushDirectApplied);
        Assert.Equal(1, h.Sync.SetFetches);                                 // "artist" → ["artists"], one fetch after the window
        Assert.Equal(1, h.CollectionPosts);
        Assert.True(h.Store.IsSaved("artists", "spotify:artist:ar1"));
    }

    [Fact]
    public async Task CollectionPush_WireSetTranslation_CollectionFetchesBoth_UnknownFetchesNothing()
    {
        await using var h = new SyncHarness(HydrateResponder);

        // "collection" (no payload) → ONE walk of the wire set lands BOTH liked and albums.
        var done1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Done: done1));
        await done1.Task;
        Assert.Equal(1, h.Sync.SetFetches);
        Assert.Equal(1, h.CollectionPosts);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));

        // "artistban" (an unknown wire set) → ignored, zero fetch.
        var done2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "artistban", Done: done2));
        await done2.Task;
        Assert.Equal(1, h.Sync.SetFetches);      // unchanged — no fetch for the unknown set
        Assert.Equal(1, h.CollectionPosts);
    }

    [Fact]
    public async Task CollectionPush_Ylpin_NeverDirectApplies_FetchesPinsSet()
    {
        // §0.1/§2.3 — ylpin pushes are opaque in practice; they always take the settle + delta-fetch path, NEVER a
        // direct fold, even when the payload happens to parse with items.
        await using var h = new SyncHarness(HydrateResponder);

        // (a) no payload at all.
        var done1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "ylpin", Done: done1));
        await done1.Task;
        Assert.Equal(0, h.Sync.PushDirectApplied);
        Assert.Equal(1, h.Sync.SetFetches);
        Assert.True(h.Store.IsSaved("pins", "spotify:playlist:pin1"));
        Assert.False(h.Store.IsSaved("pins", "spotify:track:notapin"));   // AcceptsUri keeps the stray track out

        // (b) a parseable PubSubUpdate carrying items — still no direct apply, still a fetch (the token is already set
        // from (a), so this is a delta; the responder answers "nothing changed" for a held token).
        var upd = new Col.PubSubUpdate();
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:playlist:pin1", AddedAt = 1 });
        var done2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "ylpin", Payload: upd.ToByteArray(), Done: done2));
        await done2.Task;
        Assert.Equal(0, h.Sync.PushDirectApplied);
        Assert.Equal(2, h.Sync.SetFetches);
    }

    [Fact]
    public async Task CollectionPush_YlpinEcho_IsDropped()
    {
        // The echo check runs BEFORE the direct-apply/PushDirectApplies gate, so our own accepted ylpin write's echo
        // is dropped for free — zero fetch, zero fold.
        const string cuid = "ylpin-cuid-1";
        await using var h = new SyncHarness(HydrateResponder);
        h.Echo.Record(cuid);

        var upd = new Col.PubSubUpdate { ClientUpdateId = cuid };
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:playlist:pin1", AddedAt = 1 });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "ylpin", Payload: upd.ToByteArray(), Done: done));
        await done.Task;

        Assert.Equal(1, h.Sync.EchoDropped);
        Assert.Equal(0, h.Sync.SetFetches);
        Assert.Equal(0, h.CollectionPosts);
    }

    [Fact]
    public async Task CollectionPush_FallbackFetch_WalksWireSetOnce()
    {
        // A parseable PubSubUpdate with ZERO items is the "unknown change shape" case: it falls back to the fetch, and
        // that fetch is one walk of the wire set (the token, when present, is the wire set's too).
        await using var h = new SyncHarness(HydrateResponder);
        var upd = new Col.PubSubUpdate { Set = "collection" };   // no items
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Payload: upd.ToByteArray(), Done: done));
        await done.Task;

        Assert.Equal(0, h.Sync.PushDirectApplied);
        Assert.Equal(1, h.Sync.SetFetches);
        Assert.Equal(1, h.CollectionPosts);
        Assert.Equal("tok-collection", h.Revs["collection"]);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));
    }

    // ── the collection reconcile pass (drift proof) ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconnectResync_RunsReconcilePass()
    {
        // The pass is QUEUED behind the reconnect handler (not inlined), so an idle barrier enqueued before the handler
        // ran completes before the reconcile; a second barrier is what waits for it.
        await using var h = new SyncHarness(HydrateResponder);
        h.Sync.Enqueue(new SyncCommand(SyncKind.ReconnectResync));
        await h.Sync.WaitForIdleAsync();
        await h.Sync.WaitForIdleAsync();
        Assert.Equal(1, h.Sync.ReconcilePasses);
        Assert.Equal(10, h.CollectionPosts);         // (3) one walk per wire set + one shadow walk per wire set (5 wire sets)
        Assert.Equal(0, h.Sync.ReconcileDrifts);     // the reconnect walk just converged them — no drift
        Assert.Equal("tok-collection", h.Revs["collection"]);

        // Inside the minimum gap a second pass is dropped, not re-run.
        h.Sync.ResyncWindow = TimeSpan.Zero;
        h.Sync.Enqueue(new SyncCommand(SyncKind.ReconnectResync));
        await h.Sync.WaitForIdleAsync();
        await h.Sync.WaitForIdleAsync();
        Assert.Equal(1, h.Sync.ReconcilePasses);
        Assert.Equal(1, h.Sync.ReconcilesSkipped);
        Assert.Equal(15, h.CollectionPosts);         // the reconnect's own 5 (now delta) probes ran; the reconcile did not
    }

    [Fact]
    public async Task PeriodicReconcile_Reenqueues_AfterInterval()
    {
        await using var h = new SyncHarness(HydrateResponder);
        h.Sync.ReconcileInterval = TimeSpan.FromMilliseconds(20);
        h.Sync.ReconcileMinGap = TimeSpan.Zero;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
        await done.Task;   // arms the first periodic pass

        // Each pass re-arms the next: at least two passes prove the chain, not just the boot timer.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref h.Sync.ReconcilePasses) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(h.Sync.ReconcilePasses >= 2, "expected the periodic reconcile to re-enqueue itself");
        Assert.Equal(0, h.Sync.ReconcileDrifts);
    }

    [Fact]
    public async Task CollectionPush_ForeignUpdate_ThroughRealDealerRouter_AppliesDirectly()
    {
        await using var h = new SyncHarness(HydrateResponder);
        using var router = new Wavee.Backend.Realtime.DealerRouter(h.Dealer, h.Sync);

        // Full path: a dealer collection MESSAGE carrying a PubSubUpdate → router extracts the wire set from the topic →
        // LibrarySync direct-applies. Verifies the topic-derived wire set ("collection") maps items to the right logical sets.
        var upd = new Col.PubSubUpdate { Set = "collection" };
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:track:router", IsRemoved = false, AddedAt = 1 });
        h.Dealer.PushEvent(ColPush("collection", upd));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PushDirectApplied);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:router"));
        Assert.Equal(0, h.CollectionPosts);
    }

    [Fact]
    public async Task RootlistRevision_RoundTrips_ThroughScopedReplica()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var rev = new byte[] { 1, 2, 3, 0xAB };
            var scope = new ReplicaScope("bob", 1);
            using (var s = new Wavee.Backend.Persistence.SqliteColdStore(path))
            {
                Assert.Null((await s.LoadAsync(scope, TestContext.Current.CancellationToken)).Rootlist.Revision);
                await s.CommitAsync(new ReplicaTransaction(scope, [], new([], rev), [], [], [], [], []), TestContext.Current.CancellationToken);
            }
            using (var s2 = new Wavee.Backend.Persistence.SqliteColdStore(path))
            {
                Assert.Equal(rev, (await s2.LoadAsync(scope, TestContext.Current.CancellationToken)).Rootlist.Revision);
                await s2.CommitAsync(new ReplicaTransaction(scope, [], new([], null), [], [], [], [], []), TestContext.Current.CancellationToken);
                Assert.Null((await s2.LoadAsync(scope, TestContext.Current.CancellationToken)).Rootlist.Revision);
            }
        }
        finally { foreach (var f in new[] { path, path + "-wal", path + "-shm" }) { try { System.IO.File.Delete(f); } catch { } } }
    }

    // ── P0: the dealer/revision correctness gates (I1 + the head-only playlist push) ──────────────────────────────────

    // A rootlist revision persisted by an older build could be the URI BYTES of a misparsed dealer push. It lives in
    // SQLite meta, so it survives restarts; sync start must clear it (rows preserved) and let the hydrate GET rewrite it.
    [Fact]
    public async Task Boot_CorruptRootlistRev_IsHealedThenRefetched()
    {
        var corrupt = System.Text.Encoding.UTF8.GetBytes("spotify:user:bob:rootlist");
        var rows = RootlistTreeBuilder.EntriesFromUris(new[] { "spotify:playlist:p1" });

        // (a) the rootlist GET fails, so ONLY the heal can have cleared the corrupt revision.
        await using (var h = new SyncHarness(req => req.Url.Contains("/rootlist")
            ? new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>())
            : HydrateResponder(req)))
        {
            await h.Host.SeedRootlistAsync(rows, corrupt);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
            await done.Task;

            Assert.Equal(ReplicaBaselineState.NeedsResync, h.Host.Replicas.ReadConfirmedRootlist().State);
            Assert.Null(h.Store.RootlistRevision());                     // the URI bytes are gone
            Assert.Single(h.Store.Rootlist());                            // rows preserved (only the revision was cleared)
        }

        // (b) with the GET answering, the same boot ends on the real 24-byte head.
        await using (var h2 = new SyncHarness(HydrateResponder))
        {
            await h2.Host.SeedRootlistAsync(rows, corrupt);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h2.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
            await done.Task;

            Assert.Equal(ReplicaBaselineState.Verified, h2.Host.Replicas.ReadConfirmedRootlist().State);
            Assert.Equal(Rev24(9), h2.Store.RootlistRevision());
            Assert.True(PlaylistRevisions.IsWellFormed(h2.Store.RootlistRevision()));
        }
    }

    // A head-only push ("the list rolled over, here is the new head") on the OPEN playlist revalidates through the
    // revision-gated /diff — it is not a signal regeneration and must not force a full snapshot.
    [Fact]
    public async Task PlaylistPush_HeadOnly_Open_DiffsNotFullGet()
    {
        const string uri = "spotify:playlist:open";
        int diffs = 0, fulls = 0;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/diff?")) { Interlocked.Increment(ref diffs); return Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()); }
            Interlocked.Increment(ref fulls);
            return Ok(FullSlcWithAttrs(Rev24(2), ("spotify:track:t1", "alice", 1_700_000_000_000L)));
        });
        // attribute-BEARING resident rows: the attr-heal gate must not be what answers this push.
        await h.Host.SeedPlaylistAsync(uri, new[] { new PlaylistMember("i1", "spotify:track:t1", "alice", 1_700_000_000_000L) }, Rev24(1));
        h.Sync.SetOpenContext(uri);

        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, NewRev: Rev24(2), Ops: Array.Empty<PlaylistOp>()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, diffs);
        Assert.Equal(0, fulls);
        Assert.Equal(1, h.Sync.DiffUpToDate);
        Assert.Equal(0, h.Sync.PushMarkedDirty);
    }

    [Fact]
    public async Task PlaylistPush_HeadOnly_Cold_MarksDirtyOnly()
    {
        const string uri = "spotify:playlist:cold";
        await using var h = new SyncHarness(_ => Ok(Array.Empty<byte>()));
        await h.Host.SeedPlaylistAsync(uri, new[] { new PlaylistMember("i1", "spotify:track:t1", "alice", 1_700_000_000_000L) }, Rev24(1));

        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, NewRev: Rev24(2), Ops: Array.Empty<PlaylistOp>()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PushMarkedDirty);
        Assert.Equal(0, h.PlaylistGets);                                  // anti-herd: nothing fetched
        Assert.Equal(Rev24(1), h.Store.PlaylistRevision(uri));            // no head adopted without ops
    }

    // ── P1: tombstone, the pending shield, the on-open permission seed ────────────────────────────────────────────────

    /// <summary>The remote-delete wire shape: UPDATE_LIST new{deleted_by_owner=1}.</summary>
    static PlaylistOp TombstoneOp()
        => new(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(DeletedByOwner: true));

    [Fact]
    public async Task PlaylistPush_Tombstone_RemovesFromRootlist_ClearsMembership_FlagsHeader()
    {
        const string uri = "spotify:playlist:gone";
        await using var h = new SyncHarness(_ => Ok(Array.Empty<byte>()));
        await h.Host.SeedHeaderAsync(new Playlist("gone", uri, "Doomed", null, "bob", null, 1));
        await h.Host.SeedPlaylistAsync(uri, new[] { M("i1", "spotify:track:t1") }, Rev24(1));
        await h.Host.SeedRootlistAsync(new[]
        {
            new RootlistEntry(0, 0, uri, null, 0),
            new RootlistEntry(1, 0, "spotify:playlist:keep", null, 0),
        }, Rev24(5));

        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, NewRev: Rev24(2), Ops: new[] { TombstoneOp() }));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.Tombstones);
        Assert.DoesNotContain(h.Store.Rootlist(), e => e.Uri == uri);              // gone from the sidebar tree …
        Assert.Equal("spotify:playlist:keep", Assert.Single(h.Store.Rootlist()).Uri);
        Assert.Equal(Rev24(5), h.Store.RootlistRevision());                        // … rev-preserving (its own head follows)
        Assert.Empty(h.Store.Membership(uri));                                     // … membership evicted
        Assert.False(h.Store.IsSaved("playlists", uri));                           // … saved pill cleared
        Assert.True(h.Host.ReadHeader(uri)!.DeletedByOwner);                     // … header latched for the page notice
        Assert.Equal(0, h.PlaylistGets);                                           // and NO network at all
    }

    // Once latched, no later header write can un-delete it (the store merge is `incoming || current`).
    [Fact]
    public async Task Tombstone_Latches_AcrossALaterHeaderWrite()
    {
        const string uri = "spotify:playlist:gone";
        await using var h = new SyncHarness(_ => Ok(Array.Empty<byte>()));
        await h.Host.SeedHeaderAsync(new Playlist("gone", uri, "Doomed", null, "bob", null, 1));

        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, NewRev: Rev24(2), Ops: new[] { TombstoneOp() }));
        await h.Sync.WaitForIdleAsync();

        await h.Host.SeedHeaderAsync(new Playlist("gone", uri, "Doomed", null, "bob", null, 1));   // a thin re-upsert
        Assert.True(h.Host.ReadHeader(uri)!.DeletedByOwner);
    }

    // I3(a) — local intent wins until acked. A push that arrives while our own edit is unacked describes a list that
    // does NOT contain that edit, so applying it in place would visibly revert the user's action. Mark dirty instead.
    [Fact]
    public async Task PlaylistPush_WhilePending_AdvancesConfirmedAndReappliesOverlay()
    {
        const string uri = "spotify:playlist:p";
        await using var h = new SyncHarness(_ => Ok(Array.Empty<byte>()));
        await h.Host.SeedPlaylistAsync(uri, new[] { M("i1", "spotify:track:a"), M("i2", "spotify:track:b") }, Rev24(1));

        await h.Mut.EditAsync(uri, new[] { new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true,
            Items: new[] { M("i1", "spotify:track:a") }) }, Rev24(1));
        Assert.Equal(1, h.Mut.PendingFor(uri));

        // A parent-matching, ops-carrying push that WOULD have applied in place (gate 5) if nothing were pending.
        var foreign = new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("i9", "spotify:track:z") });
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, ParentRev: Rev24(1), NewRev: Rev24(2), Ops: new[] { foreign }));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(0, h.Sync.PushDeferredPending);
        Assert.Equal(1, h.Sync.PushApplied);
        Assert.Equal(new[] { "spotify:track:b", "spotify:track:z" }, h.Store.Membership(uri).Select(x => x.ItemUri));
        Assert.Equal(3, h.Host.Replicas.ReadConfirmedPlaylist(uri).Members.Length);
        Assert.Equal(Rev24(2), h.Store.PlaylistRevision(uri));
        Assert.Equal(0, h.PlaylistGets);                                                   // no eager revalidate either
    }

    // P1.3 — opening an OWNED playlist seeds its base permission into the store header. This is the one permission GET
    // in the app; the detail page reads the answer off the store and a later dealer push converges it for free.
    [Fact]
    public async Task SetOpenContext_OwnerPlaylist_SeedsPermissionIntoStore()
    {
        const string uri = "spotify:playlist:mine";
        // The proto dialect (P2): GET .../permission/base answers Permission{revision(8 opaque bytes), level}.
        var proto = new Pl.Permission
        {
            PermissionLevel = Pl.PermissionLevel.Blocked,
            Revision = Google.Protobuf.ByteString.CopyFrom(Convert.FromHexString("3b907c0d29c940a3")),
        }.ToByteArray();
        await using var h = new SyncHarness(_ => Ok(Array.Empty<byte>()),
            transportRespond: _ => new Resp(true, proto, 200));
        await h.Host.SeedHeaderAsync(new Playlist("mine", uri, "Mine", null, "bob", null, 0, IsPublic: true,
            Capabilities: new PlaylistCapabilities(CanView: true, CanEditItems: true, CanEditMetadata: true,
                IsCollaborative: false, IsOwner: true, CanAdministratePermissions: true, Known: true)));

        h.Sync.SetOpenContext(uri);
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PermissionSeeds);
        Assert.Contains(h.TransportRoutes, r => r.Contains("/permission/base"));
        var header = h.Host.ReadHeader(uri)!;
        Assert.False(header.IsPublic);                       // BLOCKED
        Assert.Equal("3b907c0d29c940a3", header.BasePermissionRevision);   // hex, never a playlist4 revision
    }

    // THE cold deep-link regression (finding 12). SetOpenContext gates the seed on IsOwned, which reads the STORE
    // header — and on a cold open (a shared link, a restart onto a playlist page) the page's mount effect runs BEFORE
    // the header has landed. The owner check then said "not mine", nothing was enqueued, and nothing ever re-asked, so
    // a private playlist the user owns rendered with no Private eyebrow until they navigated away and back. The
    // header-landing paths on the loop now re-evaluate it.
    [Fact]
    public async Task ColdOpen_HeaderLandsAfterSetOpenContext_SeedsThePermissionOnce()
    {
        const string uri = "spotify:playlist:mine";
        var perm = new Pl.Permission
        {
            PermissionLevel = Pl.PermissionLevel.Blocked,
            Revision = Google.Protobuf.ByteString.CopyFrom(Convert.FromHexString("3b907c0d29c940a3")),
        }.ToByteArray();
        // An OWNED header on the wire: the server's CanAdministratePermissions flag is what PlaylistFetcher treats as
        // authoritative ownership (the account-name fallback is not available to this harness).
        var owned = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(Rev24(2)),
            OwnerUsername = "bob",
            Attributes = new Pl.ListAttributes { Name = "Mine" },
            Capabilities = new Pl.Capabilities { CanView = true, CanAdministratePermissions = true },
            Contents = new Pl.ListItems { Pos = 0, Truncated = false },
        }.ToByteArray();
        await using var h = new SyncHarness(_ => Ok(owned), transportRespond: _ => new Resp(true, perm, 200));

        // (1) The page mounts first — no header yet, so nothing can be seeded.
        h.Sync.SetOpenContext(uri);
        await h.Sync.WaitForIdleAsync();
        Assert.Equal(0, h.Sync.PermissionSeeds);
        Assert.Empty(h.TransportRoutes);

        // (2) …then the open fetch lands the header. THAT is the first moment the owner check can succeed.
        await h.Sync.OpenPlaylistAsync(uri, CancellationToken.None);
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PermissionSeeds);
        Assert.Contains(h.TransportRoutes, r => r.Contains("/permission/base"));
        var header = h.Host.ReadHeader(uri)!;
        Assert.False(header.IsPublic);                                    // BLOCKED
        Assert.Equal("3b907c0d29c940a3", header.BasePermissionRevision);

        // (3) ONCE per open context: every later revalidate of the open playlist runs through the same hook, and a
        //     permission GET per /diff is exactly the herd the on-open seed was introduced to replace.
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistRevalidate, uri));
        await h.Sync.WaitForIdleAsync();
        Assert.Equal(1, h.Sync.PermissionSeeds);

        // (4) …and re-opening the page IS a new open context, so it seeds again.
        h.Sync.SetOpenContext(uri);
        await h.Sync.WaitForIdleAsync();
        Assert.Equal(2, h.Sync.PermissionSeeds);
    }

    [Fact]
    public async Task ClearOpenContext_OnlyTheCurrentOwnerCanClearTheSlot()
    {
        const string oldUri = "spotify:playlist:old";
        const string currentUri = "spotify:playlist:current";
        int diffs = 0;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/diff?"))
            {
                Interlocked.Increment(ref diffs);
                return Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray());
            }
            return Ok(Array.Empty<byte>());
        });
        await h.Host.SeedPlaylistAsync(currentUri,
            [new PlaylistMember("i1", "spotify:track:t1", "alice", 1_700_000_000_000L)], Rev24(1));

        h.Sync.SetOpenContext(currentUri);
        h.Sync.ClearOpenContext(oldUri);   // delayed cleanup from the outgoing page
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, currentUri,
            NewRev: Rev24(2), Ops: Array.Empty<PlaylistOp>()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, Volatile.Read(ref diffs));
        Assert.Equal(0, h.Sync.PushMarkedDirty);

        h.Sync.ClearOpenContext(currentUri);
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, currentUri,
            NewRev: Rev24(3), Ops: Array.Empty<PlaylistOp>()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, Volatile.Read(ref diffs));
        Assert.Equal(1, h.Sync.PushMarkedDirty);
    }

    // A playlist someone else owns has no editable permission state (and the endpoint 403s) — never spend the GET.
    [Fact]
    public async Task SetOpenContext_ForeignPlaylist_DoesNotSeedPermission()
    {
        const string uri = "spotify:playlist:theirs";
        await using var h = new SyncHarness(_ => Ok(Array.Empty<byte>()));
        await h.Host.SeedHeaderAsync(new Playlist("theirs", uri, "Theirs", null, "someone", null, 0,
            Capabilities: new PlaylistCapabilities(CanView: true, CanEditItems: false, CanEditMetadata: false,
                IsCollaborative: false, IsOwner: false, CanAdministratePermissions: false)));

        h.Sync.SetOpenContext(uri);
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(0, h.Sync.PermissionSeeds);
        Assert.Empty(h.TransportRoutes);
    }

    // ── I4 post-drain resync: retry, resolve, give up (§2.2.2) ─────────────────────────────────────────────────────────

    /// <summary>Poll until <paramref name="until"/> is true (or the deadline passes), letting the loop drain between
    /// checks. The scheduled retry runs on its own <c>Task.Run</c> after <see cref="LibrarySync.ResyncRetryDelay"/>
    /// (collapsed to zero by the caller), so there is nothing else to await directly.</summary>
    static async Task PollAsync(LibrarySync sync, Func<bool> until, CancellationToken ct, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!until() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
            await sync.WaitForIdleAsync();
        }
    }

    [Fact]
    public async Task PostDrainResync_ThatThrows_IsRetried_ThenResolves()
    {
        const string uri = "spotify:playlist:p";
        var header = new Pl.SelectedListContent { Attributes = new Pl.ListAttributes { Name = "Mine" },
            Revision = ByteString.CopyFrom(Rev24(2)), Contents = new Pl.ListItems { Pos = 0, Truncated = false } };
        int playlistCalls = 0;
        await using var h = new SyncHarness(req =>
        {
            if (!req.Url.Contains("/playlist/v2/")) return Ok(Array.Empty<byte>());
            playlistCalls++;
            return playlistCalls == 1 ? new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>()) : Ok(header.ToByteArray());
        });
        h.Sync.ResyncRetryDelay = _ => TimeSpan.Zero;   // collapse the backoff so the test does not sleep for real seconds
        await h.Host.SeedHeaderAsync(new Playlist("p", uri, "Old", null, "bob", null, 0));

        h.Resync.Mark(uri);
        await h.Sync.DrainWritesAsync(TestContext.Current.CancellationToken);   // attempt 0: the GET fails -> Failed, retry scheduled

        await PollAsync(h.Sync, () => h.Resync.Get(uri).Phase == PlaylistResyncQueue.Phase.None, TestContext.Current.CancellationToken);

        Assert.Equal(PlaylistResyncQueue.Phase.None, h.Resync.Get(uri).Phase);
        Assert.Equal(2, h.PlaylistGets);   // one failed attempt, one that converged
    }

    [Fact]
    public async Task PostDrainResync_ExhaustsRetries_StaysFailed()
    {
        const string uri = "spotify:playlist:p";
        await using var h = new SyncHarness(req =>
            req.Url.Contains("/playlist/v2/") ? new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>()) : Ok(Array.Empty<byte>()));
        h.Sync.ResyncRetryDelay = _ => TimeSpan.Zero;
        await h.Host.SeedHeaderAsync(new Playlist("p", uri, "Old", null, "bob", null, 0));

        h.Resync.Mark(uri);
        await h.Sync.DrainWritesAsync(TestContext.Current.CancellationToken);

        await PollAsync(h.Sync, () => h.Resync.Get(uri) is { Phase: PlaylistResyncQueue.Phase.Failed, Attempts: 4 },
            TestContext.Current.CancellationToken);

        Assert.Equal(PlaylistResyncQueue.Phase.Failed, h.Resync.Get(uri).Phase);
        Assert.Equal(4, h.Resync.Get(uri).Attempts);   // LibrarySync.MaxResyncAttempts — stays Failed, no further auto-retry
        int getsAtGiveUp = h.PlaylistGets;
        await Task.Delay(120, TestContext.Current.CancellationToken);
        await h.Sync.WaitForIdleAsync();
        Assert.Equal(getsAtGiveUp, h.PlaylistGets);   // no further GETs once retries are exhausted
    }

    [Fact]
    public async Task AnyConvergencePath_Resolves()
    {
        const string uri = "spotify:playlist:p";
        await using var h = new SyncHarness(req => req.Url.Contains("/playlist/v2/")
            ? Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray())
            : Ok(Array.Empty<byte>()));
        await h.Host.SeedHeaderAsync(new Playlist("p", uri, "Mine", null, "bob", null, 0));
        await h.Host.SeedPlaylistAsync(uri, new[] { M("a01", "spotify:track:a") }, Rev24(1));
        h.Sync.SetOpenContext(uri);

        h.Resync.Mark(uri);
        Assert.Equal(PlaylistResyncQueue.Phase.Marked, h.Resync.Get(uri).Phase);

        // Gate 4 (a new, well-formed head with no usable parent and no ops) on the OPEN uri revalidates directly —
        // no DrainWrites involved. RevalidateCoreAsync resolves the resync entry on every convergence path, not just
        // the post-drain one.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, ParentRev: null, NewRev: Rev24(2), Ops: null, Done: done));
        await done.Task;

        Assert.Equal(PlaylistResyncQueue.Phase.None, h.Resync.Get(uri).Phase);
    }
}
