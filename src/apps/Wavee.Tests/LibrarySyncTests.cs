using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Hydration;
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
    public readonly StubTransport Dealer = new();          // dealer MESSAGE pushes + the mutation transport
    public readonly MutationEngine Mut;
    public readonly CollectionEchoRing Echo = new();       // §7.1 — shared between the write strategy and the sync loop
    public readonly Dictionary<string, string?> Revs = new();
    public readonly List<string> Hydrated = new();
    public int PlaylistGets, RootlistGets, CollectionPosts;
    public readonly LibrarySync Sync;
    public readonly PlaylistResyncQueue Resync = new();
    /// <summary>Every route the loop asked the TRANSPORT for (the outbox drain + the on-open permission seed), in order.</summary>
    public readonly List<string> TransportRoutes = new();
    readonly CancellationTokenSource _cts = new();

    public static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);

    /// <param name="transportRespond">Answers the loop's TRANSPORT requests (permission seed / drain). Null = the plain
    /// StubTransport answer (200, empty body), which is what every non-permission test wants.</param>
    public SyncHarness(Func<HttpReq, HttpResp> responder, Func<string, string, bool>? hasPending = null,
        Action<InMemoryStore, IReadOnlyList<string>>? onHydrate = null,
        Func<string, Resp>? transportRespond = null)
    {
        var http = new FakeExchange((req, _) =>
        {
            if (req.Url.Contains("/rootlist")) RootlistGets++;
            else if (req.Url.Contains("/playlist/v2/")) PlaylistGets++;
            else if (req.Url.Contains("/collection/v2/")) CollectionPosts++;
            return responder(req);
        });
        Task Hydrate(IReadOnlyList<string> uris, CancellationToken c)
        {
            lock (Hydrated) Hydrated.AddRange(uris);
            onHydrate?.Invoke(Store, uris);
            return Task.CompletedTask;
        }
        var pf = new PlaylistFetcher(http, () => "https://x", Store, Hydrate, () => "");
        var cf = new CollectionFetcher(http, () => "https://x", () => "bob", Store,
            s => Revs.TryGetValue(s, out var r) ? r : null, (s, r) => Revs[s] = r, Hydrate, hasPending);
        Mut = new MutationEngine(Store, new IMutationStrategy[] { new SetReplayStrategy(Echo), new OpRebaseStrategy(Store, () => "https://spclient.wg.spotify.com", Resync), new RootlistFollowStrategy(Store, new RootlistLane()) });
        var transport = new HarnessTransport(Dealer, TransportRoutes, transportRespond);
        Sync = new LibrarySync(Store, pf, cf, Mut, Resync, transport,
            () => new SessionContext("bob", "US", "premium", "en", Tier.Premium, false), () => "bob", default, _cts.Token, Echo);
    }

    public async ValueTask DisposeAsync() { await Sync.DisposeAsync(); _cts.Cancel(); _cts.Dispose(); }
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
        await using var h = new SyncHarness(HydrateResponder,
            // The mutation transport parks: the drain command owns the loop until the test lets go.
            transportRespond: _ => { release.Task.GetAwaiter().GetResult(); return new Resp(true, Array.Empty<byte>(), 200); });

        // A resident baseline plus a LOCAL edit whose optimistic effect is already in the store.
        h.Store.UpsertPlaylist(new Playlist("p1", uri, "Mine", null, "bob", null, 0));
        h.Store.SetMembership(uri, new[] { M("aaaaaaaaaaaaaa01", "spotify:track:a") }, Rev24(1));
        h.Mut.Edit(uri, new[]
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
        var plan = OpenPolicy.For(EntityKind.Playlist, hasBaseline: true);
        Assert.Equal(HydrationLevel.None, plan.Blocking);
        Assert.Equal(HydrationLevel.Open, plan.Background);
        Assert.True(plan.Revalidate);
        // (c) The distinction matters: the BLOCKING open really does queue behind the drain.
        var queued = h.Sync.OpenPlaylistAsync(uri, TestContext.Current.CancellationToken);
        await Task.Delay(80, TestContext.Current.CancellationToken);
        Assert.False(queued.IsCompleted);

        release.SetResult();
        await drain;
        await queued;
    }

    // ── the attribute-aware heal gate (Date-added / Added-by regression) ──────────────────────────────────────────────
    // A resident-but-attribute-less membership (rows cached WITHOUT Item.attributes, e.g. the followed "Summer 2016 vibes"
    // playlist) must open through the FULL attribute-bearing fetch, not the /diff revalidate — /diff never re-reads
    // attributes for existing rows, so the poisoned cache would otherwise serve blank added_at/added_by forever.
    [Fact]
    public async Task OpenPlaylist_AttributeLessMembership_HealsViaFullFetch_NotDiff()
    {
        const string uri = "spotify:playlist:poisoned";
        int diffs = 0, fulls = 0;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/diff?")) { Interlocked.Increment(ref diffs); return Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()); }
            Interlocked.Increment(ref fulls);
            return Ok(FullSlcWithAttrs(new byte[] { 2 },
                ("spotify:track:t1", "alice", 1_700_000_000_000L), ("spotify:track:t2", "bob", 1_700_000_100_000L)));
        });
        // resident membership recorded WITHOUT item attributes (the poisoned cache) + a revision (so /diff would be taken).
        h.Store.SetMembership(uri, new[] { M("i1", "spotify:track:t1"), M("i2", "spotify:track:t2") }, new byte[] { 1 });

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
    public async Task OpenPlaylist_GenuinelyAttributeLess_ForcesFullFetchOnce_ThenDoesNotLoop()
    {
        const string uri = "spotify:playlist:noattrs";
        int fulls = 0;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/diff?")) return Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray());
            Interlocked.Increment(ref fulls);
            return Ok(FullSlcNoAttrs(new byte[] { 2 }, "spotify:track:t1"));   // server returns no item attributes
        });
        h.Store.SetMembership(uri, new[] { M("i1", "spotify:track:t1") }, new byte[] { 1 });

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
        // tokens advanced (per set)
        Assert.Equal("tok-collection", h.Revs["liked"]);
        Assert.Equal("tok-collection", h.Revs["albums"]);
        Assert.Equal("tok-artist", h.Revs["artists"]);
        Assert.Equal("tok-show", h.Revs["shows"]);
        Assert.Equal("tok-listenlater", h.Revs["episodes"]);
        // the "playlists" saved-set fold
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:p1"));
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:p2"));
        // one Bulk-coalesced signal per burst — no per-uri change leaked (rootlist+fold = 1, then 5 sets = 5).
        List<StoreChange> snap; lock (col.All) snap = new List<StoreChange>(col.All);
        Assert.All(snap, c => Assert.True(c.IsBulk));
        Assert.Equal(6, snap.Count);
    }

    [Fact]
    public async Task CollectionPush_TwoRapidPushesForSameWireSet_FoldToOneSettledFetch()
    {
        await using var h = new SyncHarness(HydrateResponder);

        // Two pushes for the same WIRE set inside the settle window: the second must fold into the first (dropped), NOT re-arm.
        // Done rides the first push and completes when the single settled fetch finishes — a deterministic barrier, not a sleep.
        // The "collection" wire set carries BOTH liked and albums, so the settled fetch fans out to two logical delta-fetches.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Done: done));
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection"));   // within the window → folded
        Assert.True(h.Sync.IsSetSyncing("collection"));                           // syncing from the first push

        await done.Task;

        Assert.Equal(2, h.Sync.SetFetches);              // one settled push → both logical sets (liked + albums) fetched once
        Assert.Equal(2, h.CollectionPosts);              // two HTTP hits — the second push did not re-arm the settle
        Assert.False(h.Sync.IsSetSyncing("collection")); // cleared once the fetch completed
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));
    }

    [Fact]
    public async Task CollectionPushSettle_DoesNotBlockAFollowingPlaylistPush()
    {
        await using var h = new SyncHarness(HydrateResponder);
        var uri = "spotify:playlist:pr";
        var rev0 = new byte[] { 1 };
        var rev1 = new byte[] { 2 };
        h.Store.SetMembership(uri, new[] { new Wavee.Backend.Playlists.PlaylistMember("id1", "spotify:track:a", null, 0) }, rev0);
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
    public async Task PlaylistPush_AddHydratesThenEmitsPlaylistBump()
    {
        var uri = "spotify:playlist:p";
        var added = "spotify:track:new";
        var rev0 = new byte[] { 1 };
        var rev1 = new byte[] { 2 };
        await using var h = new SyncHarness(HydrateResponder, onHydrate: (store, uris) =>
        {
            foreach (var u in uris) store.UpsertTrack(Trk(u, "Hydrated " + u));
        });
        h.Store.SetMembership(uri, new[] { M("old", "spotify:track:old") }, rev0);

        var playlistSignals = new List<bool>();
        using var sub = h.Store.Changes.Subscribe(new ChangeObserver(c =>
        {
            if (c.Uri == uri) lock (playlistSignals) playlistSignals.Add(h.Store.GetTrack(added) is not null);
        }));

        var op = new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("new", added) });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, ParentRev: rev0, NewRev: rev1, Ops: new[] { op }, Done: done));
        await done.Task;

        List<bool> snap; lock (playlistSignals) snap = new List<bool>(playlistSignals);
        Assert.True(snap.Count >= 2);
        Assert.False(snap[0]);                       // membership write before metadata hydration
        Assert.Contains(true, snap);                  // post-hydration playlist bump wakes the joined detail read-model
        Assert.NotNull(h.Store.GetTrack(added));
    }

    [Fact]
    public async Task PlaylistPush_UpdateListAttributes_RefetchesHeader()
    {
        var uri = "spotify:playlist:p";
        var rev0 = new byte[] { 1 };
        var rev1 = new byte[] { 2 };
        var header = new Pl.SelectedListContent { Length = 7, OwnerUsername = "bob" };
        header.Attributes = new Pl.ListAttributes { Name = "Renamed", Description = "fresh" };
        await using var h = new SyncHarness(req => req.Url.Contains("/playlist/v2/") ? Ok(header.ToByteArray()) : Ok(Array.Empty<byte>()));
        h.Store.UpsertPlaylist(new Playlist("p", uri, "Old", null, "bob", null, 1));
        h.Store.SetMembership(uri, new[] { M("old", "spotify:track:old") }, rev0);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, ParentRev: rev0, NewRev: rev1,
            Ops: new[] { new PlaylistOp(PlaylistOpKind.UpdateList) }, Done: done));
        await done.Task;

        Assert.Equal(1, h.PlaylistGets);
        var playlist = h.Store.GetPlaylist(uri);
        Assert.NotNull(playlist);
        Assert.Equal("Renamed", playlist.Name);
        Assert.Equal("fresh", playlist.Description);
        Assert.Equal(7, playlist.TrackCount);
    }

    [Fact]
    public async Task MarkAndSweep_FullPaging_RemovesAbsent_KeepsShielded()
    {
        var store = new InMemoryStore();
        store.SetSaved("albums", "spotify:album:gone", true, SyncState.Confirmed);          // absent from the snapshot → swept
        store.SetSaved("albums", "spotify:album:pending", true, SyncState.Pending);         // absent + shielded → survives

        var page = new Col.PageResponse { SyncToken = "t2", NextPageToken = "" };
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 1 });
        var revs = new Dictionary<string, string?>();
        var http = new FakeExchange((req, _) => Ok(page.ToByteArray()));
        var fetcher = new CollectionFetcher(http, () => "https://x", () => "bob", store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, (u, c) => Task.CompletedTask,
            (s, u) => u == "spotify:album:pending");

        await fetcher.FetchSetAsync("albums", TestContext.Current.CancellationToken);

        Assert.True(store.IsSaved("albums", "spotify:album:a"));            // snapshot member
        Assert.False(store.IsSaved("albums", "spotify:album:gone"));        // swept
        Assert.True(store.IsSaved("albums", "spotify:album:pending"));      // shielded survives
        Assert.Equal("t2", revs["albums"]);
    }

    [Fact]
    public async Task MarkAndSweep_MidPagingThrow_LeavesPartial_NoSweep_TokenNotAdvanced()
    {
        var store = new InMemoryStore();
        store.SetSaved("albums", "spotify:album:gone", true, SyncState.Confirmed);

        var page1 = new Col.PageResponse { SyncToken = "t1", NextPageToken = "p2" };         // a second page follows
        page1.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 1 });
        var revs = new Dictionary<string, string?>();
        var http = new FakeExchange((req, n) => n == 1 ? Ok(page1.ToByteArray()) : new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>()));
        var fetcher = new CollectionFetcher(http, () => "https://x", () => "bob", store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, (u, c) => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.FetchSetAsync("albums", TestContext.Current.CancellationToken));

        Assert.True(store.IsSaved("albums", "spotify:album:a"));            // partial page applied
        Assert.True(store.IsSaved("albums", "spotify:album:gone"));         // NOT swept (partial loop)
        Assert.False(revs.ContainsKey("albums"));                          // token NOT advanced → next attempt re-pages fully
    }

    [Fact]
    public async Task DrainBackoff_SkipsNotDueOps_ThenAttempts_CapsAtTenDeadLetters()
    {
        var store = new InMemoryStore();
        var clock = DateTime.UtcNow;
        var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy() }, null, () => clock);
        eng.Save("liked", "spotify:track:a", true);
        var t = new FailTransport();
        var ctx = SessionContext.LoggedOut;

        await eng.Drain(t, ctx);                       // attempt 1 fails → nextAttemptAt = now + 1s
        Assert.Equal(1, t.Calls);
        Assert.Equal(1, eng.Pending);

        await eng.Drain(t, ctx);                       // not due → skipped (no new replay)
        Assert.Equal(1, t.Calls);
        Assert.Equal(1, eng.Pending);

        clock = clock.AddSeconds(1.5);                 // advance past the backoff
        await eng.Drain(t, ctx);                       // attempt 2
        Assert.Equal(2, t.Calls);

        for (int i = 0; i < 15 && eng.Pending > 0; i++) { clock = clock.AddSeconds(120); await eng.Drain(t, ctx); }
        Assert.Equal(0, eng.Pending);                  // 10 attempts exhausted → dead-lettered
        Assert.Single(eng.DeadLetter);
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
        h.Mut.Save("liked", "spotify:track:z", true);
        await h.Mut.Drain(h.Dealer, new SessionContext("bob", "US", "premium", "en", Tier.Premium, false),
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
        h.Mut.Save("liked", "spotify:track:pending", true);

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
        List<string> hyd; lock (h.Hydrated) hyd = new List<string>(h.Hydrated);
        Assert.Contains("spotify:track:t9", hyd);
        Assert.DoesNotContain("spotify:track:pending", hyd);               // shielded item never touched
    }

    [Fact]
    public async Task CollectionPush_DirectApplyHydratesThenEmitsCollectionKindBump()
    {
        var added = "spotify:track:t9";
        await using var h = new SyncHarness(HydrateResponder, onHydrate: (store, uris) =>
        {
            foreach (var u in uris) store.UpsertTrack(Trk(u, "Hydrated " + u));
        });

        var signals = new List<(StoreChange Change, bool TrackKnown)>();
        using var sub = h.Store.Changes.Subscribe(new ChangeObserver(c =>
        {
            lock (signals) signals.Add((c, h.Store.GetTrack(added) is not null));
        }));

        var upd = new Col.PubSubUpdate { Set = "collection" };
        upd.Items.Add(new Col.CollectionItem { Uri = added, IsRemoved = false, AddedAt = 5 });

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Payload: upd.ToByteArray(), Done: done));
        await done.Task;

        List<(StoreChange Change, bool TrackKnown)> snap; lock (signals) snap = new List<(StoreChange, bool)>(signals);
        Assert.Contains(snap, s => s.Change.Uri == added && s.Change.Kind == CollectionKind.Liked && s.TrackKnown);
        Assert.NotNull(h.Store.GetTrack(added));
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

        // "collection" (no payload) → delta fetch fans out to BOTH liked and albums (two routes hit).
        var done1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Done: done1));
        await done1.Task;
        Assert.Equal(2, h.Sync.SetFetches);
        Assert.Equal(2, h.CollectionPosts);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));

        // "ylpin" (an unknown wire set) → ignored, zero fetch.
        var done2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "ylpin", Done: done2));
        await done2.Task;
        Assert.Equal(2, h.Sync.SetFetches);      // unchanged — no fetch for the unknown set
        Assert.Equal(2, h.CollectionPosts);
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
    public void RootlistRevision_RoundTrips_ThroughSqliteMeta()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var rev = new byte[] { 1, 2, 3, 0xAB };
            using (var s = new Wavee.Backend.Persistence.SqliteColdStore(path))
            {
                Assert.Null(s.GetRootlistRevision());  // unset → null
                s.SetRootlistRevision(rev);
            }
            using (var s2 = new Wavee.Backend.Persistence.SqliteColdStore(path))
            {
                Assert.Equal(rev, s2.GetRootlistRevision());   // durable across instances
                s2.SetRootlistRevision(null);                  // null clears
                Assert.Null(s2.GetRootlistRevision());
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
            h.Store.SetRootlist(rows, corrupt);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
            await done.Task;

            Assert.Equal(1, h.Sync.RootlistRevisionsHealed);
            Assert.Null(h.Store.RootlistRevision());                     // the URI bytes are gone
            Assert.Single(h.Store.Rootlist());                            // rows preserved (only the revision was cleared)
        }

        // (b) with the GET answering, the same boot ends on the real 24-byte head.
        await using (var h2 = new SyncHarness(HydrateResponder))
        {
            h2.Store.SetRootlist(rows, corrupt);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h2.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
            await done.Task;

            Assert.Equal(1, h2.Sync.RootlistRevisionsHealed);
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
        h.Store.SetMembership(uri, new[] { new PlaylistMember("i1", "spotify:track:t1", "alice", 1_700_000_000_000L) }, Rev24(1));
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
        h.Store.SetMembership(uri, new[] { new PlaylistMember("i1", "spotify:track:t1", "alice", 1_700_000_000_000L) }, Rev24(1));

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
        h.Store.UpsertPlaylist(new Playlist("gone", uri, "Doomed", null, "bob", null, 1));
        h.Store.SetMembership(uri, new[] { M("i1", "spotify:track:t1") }, Rev24(1));
        h.Store.SetRootlist(new[]
        {
            new RootlistEntry(0, 0, uri, null, 0),
            new RootlistEntry(1, 0, "spotify:playlist:keep", null, 0),
        }, Rev24(5));
        h.Store.SetSaved("playlists", uri, true, SyncState.Confirmed);

        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, NewRev: Rev24(2), Ops: new[] { TombstoneOp() }));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.Tombstones);
        Assert.DoesNotContain(h.Store.Rootlist(), e => e.Uri == uri);              // gone from the sidebar tree …
        Assert.Equal("spotify:playlist:keep", Assert.Single(h.Store.Rootlist()).Uri);
        Assert.Equal(Rev24(5), h.Store.RootlistRevision());                        // … rev-preserving (its own head follows)
        Assert.Empty(h.Store.Membership(uri));                                     // … membership evicted
        Assert.False(h.Store.IsSaved("playlists", uri));                           // … saved pill cleared
        Assert.True(h.Store.GetPlaylist(uri)!.DeletedByOwner);                     // … header latched for the page notice
        Assert.Equal(0, h.PlaylistGets);                                           // and NO network at all
    }

    // Once latched, no later header write can un-delete it (the store merge is `incoming || current`).
    [Fact]
    public async Task Tombstone_Latches_AcrossALaterHeaderWrite()
    {
        const string uri = "spotify:playlist:gone";
        await using var h = new SyncHarness(_ => Ok(Array.Empty<byte>()));
        h.Store.UpsertPlaylist(new Playlist("gone", uri, "Doomed", null, "bob", null, 1));

        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, NewRev: Rev24(2), Ops: new[] { TombstoneOp() }));
        await h.Sync.WaitForIdleAsync();

        h.Store.UpsertPlaylist(new Playlist("gone", uri, "Doomed", null, "bob", null, 1));   // a thin re-upsert
        Assert.True(h.Store.GetPlaylist(uri)!.DeletedByOwner);
    }

    // I3(a) — local intent wins until acked. A push that arrives while our own edit is unacked describes a list that
    // does NOT contain that edit, so applying it in place would visibly revert the user's action. Mark dirty instead.
    [Fact]
    public async Task PlaylistPush_WhilePending_MarksDirty_NoInPlaceApply()
    {
        const string uri = "spotify:playlist:p";
        await using var h = new SyncHarness(_ => Ok(Array.Empty<byte>()));
        h.Store.SetMembership(uri, new[] { M("i1", "spotify:track:a"), M("i2", "spotify:track:b") }, Rev24(1));

        h.Mut.Edit(uri, new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });   // optimistic: [b]
        Assert.Equal(1, h.Mut.PendingFor(uri));

        // A parent-matching, ops-carrying push that WOULD have applied in place (gate 5) if nothing were pending.
        var foreign = new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("i9", "spotify:track:z") });
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, ParentRev: Rev24(1), NewRev: Rev24(2), Ops: new[] { foreign }));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PushDeferredPending);
        Assert.Equal(0, h.Sync.PushApplied);
        Assert.Equal("spotify:track:b", Assert.Single(h.Store.Membership(uri)).ItemUri);   // our optimistic list survives
        Assert.Equal(Rev24(1), h.Store.PlaylistRevision(uri));                             // and the head is NOT adopted
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
        h.Store.UpsertPlaylist(new Playlist("mine", uri, "Mine", null, "bob", null, 0, IsPublic: true,
            Capabilities: new PlaylistCapabilities(CanView: true, CanEditItems: true, CanEditMetadata: true,
                IsCollaborative: false, IsOwner: true, CanAdministratePermissions: true)));

        h.Sync.SetOpenContext(uri);
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PermissionSeeds);
        Assert.Contains(h.TransportRoutes, r => r.Contains("/permission/base"));
        var header = h.Store.GetPlaylist(uri)!;
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
        var header = h.Store.GetPlaylist(uri)!;
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
        h.Store.SetMembership(currentUri,
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
        h.Store.UpsertPlaylist(new Playlist("theirs", uri, "Theirs", null, "someone", null, 0,
            Capabilities: new PlaylistCapabilities(CanView: true, CanEditItems: false, CanEditMetadata: false,
                IsCollaborative: false, IsOwner: false, CanAdministratePermissions: false)));

        h.Sync.SetOpenContext(uri);
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(0, h.Sync.PermissionSeeds);
        Assert.Empty(h.TransportRoutes);
    }
}
