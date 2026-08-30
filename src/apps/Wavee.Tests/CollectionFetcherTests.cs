using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Spotify;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

// The live collection fetcher: POST /collection/v2/{paging|delta} per WIRE set → apply items onto every logical set the
// wire set carries + hydrate + (only off a VERIFIED walk) sweep and store the wire set's sync token. HTTP faked; the
// proto request/response shapes are exercised for real. The reconcile pass (shadow walk → drift → repage) is here too.
public class CollectionFetcherTests
{
    const long NowMs = 1_800_000_000_000L;   // the fetcher's clock in every test that cares about the recency shield
    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);
    static HttpResp Fail() => new(500, new Dictionary<string, string>(), Array.Empty<byte>());

    static Col.PageResponse Page(string syncToken, string next, params (string Uri, int AddedAt)[] items)
    {
        var p = new Col.PageResponse { SyncToken = syncToken, NextPageToken = next };
        foreach (var (uri, at) in items) p.Items.Add(new Col.CollectionItem { Uri = uri, AddedAt = at });
        return p;
    }

    sealed class Rig
    {
        public readonly InMemoryStore Store = new();
        public readonly Dictionary<string, string?> Revs = new();
        public readonly List<string> Hydrated = new();
        public readonly List<HttpReq> Requests = new();
        public readonly CapturingWaveeLog Log = new();
        public readonly CollectionFetcher Fetcher;

        public Rig(Func<HttpReq, int, HttpResp> respond, Func<string, string, bool>? hasPending = null, long nowMs = NowMs)
        {
            var http = new FakeExchange((req, n) => { Requests.Add(req); return respond(req, n); });
            Fetcher = new CollectionFetcher(http, () => "https://spclient.test", () => "bob", Store,
                s => Revs.TryGetValue(s, out var r) ? r : null, (s, r) => Revs[s] = r,
                (uris, ct) => { Hydrated.AddRange(uris); return Task.CompletedTask; },
                hasPending, new WaveeLogger(Log, "sync"), () => nowMs);
        }

        public IEnumerable<string> Events(string eventId) => Log.Entries.Where(e => e.EventId == eventId).Select(e => e.EventId);
        public string SentSet(int i) => Col.PageRequest.Parser.ParseFrom(Requests[i].Body).Set;
        public string SentPageToken(int i) => Col.PageRequest.Parser.ParseFrom(Requests[i].Body).PaginationToken;
    }

    [Fact]
    public async Task FetchWireSet_FullPage_AppliesItems_Hydrates_AndAdvancesRevision()
    {
        var rig = new Rig((_, _) => Ok(Page("tok-1", "", ("spotify:album:a", 1), ("spotify:album:b", 2)).ToByteArray()));

        var outcome = await rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionFetchOutcome.Snapshot, outcome);
        var captured = Assert.Single(rig.Requests);
        Assert.Equal("POST", captured.Method);
        Assert.Contains("/collection/v2/paging", captured.Url);
        // The collection2v2 route only accepts its vendor media type — `application/protobuf` 400s. Guard both headers.
        Assert.Equal("application/vnd.collection-v2.spotify.proto", captured.Headers["Content-Type"]);
        Assert.Equal("application/vnd.collection-v2.spotify.proto", captured.Headers["Accept"]);
        Assert.True(rig.Store.IsSaved("albums", "spotify:album:a"));
        Assert.True(rig.Store.IsSaved("albums", "spotify:album:b"));
        Assert.Equal("tok-1", rig.Revs["collection"]);   // the token is the WIRE set's
        Assert.False(rig.Revs.ContainsKey("albums"));    // never keyed by logical set any more
        Assert.Equal(2, rig.Hydrated.Count);

        var sent = Col.PageRequest.Parser.ParseFrom(captured.Body);   // the request body is a real PageRequest
        Assert.Equal("bob", sent.Username);
        Assert.Equal("collection", sent.Set);
    }

    [Fact]
    public async Task FetchWireSet_FetchesCollectionOnce_ForLikedAndAlbums()
    {
        // One wire "collection" page mixes tracks + albums; ONE walk lands "liked" (spotify:track:) and "albums"
        // (spotify:album:) — the two logical sets no longer walk (and sweep) the same snapshot twice.
        var rig = new Rig((_, _) => Ok(Page("tok-1", "", ("spotify:track:t1", 1), ("spotify:album:al1", 2), ("spotify:track:t2", 3)).ToByteArray()));

        await rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken);

        Assert.Single(rig.Requests);
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t2"));
        Assert.False(rig.Store.IsSaved("liked", "spotify:album:al1"));      // album filtered OUT of liked
        Assert.True(rig.Store.IsSaved("albums", "spotify:album:al1"));
        Assert.False(rig.Store.IsSaved("albums", "spotify:track:t1"));      // track filtered OUT of albums
        Assert.Equal("tok-1", rig.Revs["collection"]);
    }

    [Fact]
    public async Task FetchWireSet_WireSetNames_AreTheSingularServerNames()
    {
        // Regression guard for the other half of the 400: the server sets are singular ("artist"/"show") + "listenlater",
        // and each lands in its plural logical set.
        foreach (var (wire, logical, uri) in new[] { ("artist", "artists", "spotify:artist:a"), ("show", "shows", "spotify:show:s"), ("listenlater", "episodes", "spotify:episode:e") })
        {
            var rig = new Rig((_, _) => Ok(Page("t", "", (uri, 1)).ToByteArray()));
            await rig.Fetcher.FetchWireSetAsync(wire, TestContext.Current.CancellationToken);
            Assert.Equal(wire, rig.SentSet(0));
            Assert.True(rig.Store.IsSaved(logical, uri));
            Assert.Equal("t", rig.Revs[wire]);
        }
    }

    [Fact]
    public async Task FetchWireSet_UnknownWireSet_Throws()
    {
        var rig = new Rig((_, _) => Fail());
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Fetcher.FetchWireSetAsync("ylpin", TestContext.Current.CancellationToken));
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public async Task FetchWireSet_Delta_WhenPriorTokenPresent_FansOutToEveryLogicalSet()
    {
        var delta = new Col.DeltaResponse { DeltaUpdatePossible = true, SyncToken = "tok-2" };
        delta.Items.Add(new Col.CollectionItem { Uri = "spotify:track:new", AddedAt = 9 });
        delta.Items.Add(new Col.CollectionItem { Uri = "spotify:track:old", IsRemoved = true });
        delta.Items.Add(new Col.CollectionItem { Uri = "spotify:album:al", AddedAt = 10 });
        var rig = new Rig((_, _) => Ok(delta.ToByteArray()));
        // Non-zero addedAt — otherwise the timestamp-less heal clears the sync token and the fetcher falls through to
        // paging; this test's fake only returns a DeltaResponse, so paging would spin forever.
        rig.Store.SetSaved("liked", "spotify:track:old", true, SyncState.Confirmed, addedAtMs: 1_700_000_000_000);
        rig.Revs["collection"] = "tok-1";   // prior WIRE token → delta path

        var outcome = await rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionFetchOutcome.Delta, outcome);
        var captured = Assert.Single(rig.Requests);
        Assert.Contains("/collection/v2/delta", captured.Url);
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:new"));
        Assert.False(rig.Store.IsSaved("liked", "spotify:track:old"));
        Assert.True(rig.Store.IsSaved("albums", "spotify:album:al"));    // one delta, both logical sets
        Assert.False(rig.Store.IsSaved("liked", "spotify:album:al"));
        Assert.Equal("tok-2", rig.Revs["collection"]);

        var sent = Col.DeltaRequest.Parser.ParseFrom(captured.Body);
        Assert.Equal("collection", sent.Set);
        Assert.Equal("tok-1", sent.LastSyncToken);
    }

    [Fact]
    public async Task FetchWireSet_DeltaNotPossible_FallsThroughToAVerifiedWalk()
    {
        var rig = new Rig((req, _) => req.Url.Contains("/delta")
            ? Ok(new Col.DeltaResponse { DeltaUpdatePossible = false }.ToByteArray())
            : Ok(Page("walk-tok", "", ("spotify:track:t1", 1)).ToByteArray()));
        rig.Store.SetSaved("liked", "spotify:track:stale", true, SyncState.Confirmed, addedAtMs: 1_700_000_000_000);
        rig.Revs["collection"] = "too-old";

        var outcome = await rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionFetchOutcome.Snapshot, outcome);
        Assert.Equal(2, rig.Requests.Count);
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.False(rig.Store.IsSaved("liked", "spotify:track:stale"));   // the verified walk swept it
        Assert.Equal("walk-tok", rig.Revs["collection"]);
        Assert.Single(rig.Events("collection.token.reset"));
    }

    // ── the verified-snapshot discipline ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FetchWireSet_TwoPageMixed_AppliesPage2LikedTracks_AndAdvancesToken()
    {
        // The 274-vs-293 shape: the newest likes live on the LAST page. Both pages land, the cursor is threaded through
        // the second request, and the stored token is PAGE ONE's (the earliest — the next delta re-ships anything that
        // moved while page two was walking; re-applying is idempotent).
        var rig = new Rig((_, n) => n == 1
            ? Ok(Page("tok-A", "p2", ("spotify:track:t1", 1), ("spotify:album:al1", 2)).ToByteArray())
            : Ok(Page("tok-B", "", ("spotify:track:t2", 3), ("spotify:track:t3", 4)).ToByteArray()));

        var outcome = await rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionFetchOutcome.Snapshot, outcome);
        Assert.Equal(2, rig.Requests.Count);
        Assert.Equal("", rig.SentPageToken(0));
        Assert.Equal("p2", rig.SentPageToken(1));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t2"));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t3"));
        Assert.True(rig.Store.IsSaved("albums", "spotify:album:al1"));
        Assert.Equal("tok-A", rig.Revs["collection"]);
        Assert.Single(rig.Events("collection.snapshot.pages"));
        Assert.Empty(rig.Events("collection.snapshot.unverified"));
    }

    [Fact]
    public async Task FetchWireSet_OverlapAcrossPages_AppliesAdds_NoSweep_TokenNotAdvanced()
    {
        // t1 shows up on BOTH pages: the cursor shifted under the walk, so the walk may equally have skipped a uri.
        // Adds are real and land; nothing is swept; no token is stored (the next fetch walks again).
        var rig = new Rig((_, n) => n == 1
            ? Ok(Page("tok-A", "p2", ("spotify:track:t1", 1)).ToByteArray())
            : Ok(Page("tok-B", "", ("spotify:track:t1", 1), ("spotify:track:t2", 2)).ToByteArray()));
        rig.Store.SetSaved("liked", "spotify:track:gone", true, SyncState.Confirmed);   // absent from the walk

        var outcome = await rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionFetchOutcome.SnapshotUnverified, outcome);
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t2"));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:gone"));   // NOT swept
        Assert.False(rig.Revs.ContainsKey("collection"));                // NOT advanced
        Assert.Single(rig.Events("collection.snapshot.unverified"));
        Assert.Empty(rig.Events("collection.snapshot.sweep"));
    }

    [Fact]
    public async Task FetchWireSet_TerminalWithoutToken_NoSweep_TokenNotAdvanced()
    {
        var rig = new Rig((_, _) => Ok(Page("", "", ("spotify:track:t1", 1)).ToByteArray()));
        rig.Store.SetSaved("liked", "spotify:track:gone", true, SyncState.Confirmed);

        var outcome = await rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionFetchOutcome.SnapshotUnverified, outcome);
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:gone"));
        Assert.False(rig.Revs.ContainsKey("collection"));
    }

    [Fact]
    public async Task FetchWireSet_MidPagingThrow_LeavesPartial_NoSweep_TokenNotAdvanced()
    {
        var rig = new Rig((_, n) => n == 1 ? Ok(Page("t1", "p2", ("spotify:album:a", 1)).ToByteArray()) : Fail());
        rig.Store.SetSaved("albums", "spotify:album:gone", true, SyncState.Confirmed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken));

        Assert.True(rig.Store.IsSaved("albums", "spotify:album:a"));       // partial page applied
        Assert.True(rig.Store.IsSaved("albums", "spotify:album:gone"));    // NOT swept (partial loop)
        Assert.False(rig.Revs.ContainsKey("collection"));                  // token NOT advanced → next attempt re-pages fully
    }

    [Fact]
    public async Task FetchWireSet_VerifiedWalk_Sweeps_KeepsPending_AndRecentlyAckedLike()
    {
        // Three absent members, three fates: a pending intent is shielded, a like added a minute before the walk is
        // shielded by the recency window (collection2v2 pages lag /write), an hour-old one is genuinely gone.
        var rig = new Rig((_, _) => Ok(Page("t2", "", ("spotify:track:t1", 1)).ToByteArray()),
            hasPending: (s, u) => u == "spotify:track:pending");
        rig.Store.SetSaved("liked", "spotify:track:pending", true, SyncState.Pending, NowMs - 3_600_000);
        rig.Store.SetSaved("liked", "spotify:track:recent", true, SyncState.Confirmed, NowMs - 60_000);
        rig.Store.SetSaved("liked", "spotify:track:old", true, SyncState.Confirmed, NowMs - 3_600_000);

        var outcome = await rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionFetchOutcome.Snapshot, outcome);
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:pending"));   // §7.2 shield
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:recent"));    // recency shield
        Assert.False(rig.Store.IsSaved("liked", "spotify:track:old"));      // swept
        Assert.Equal("t2", rig.Revs["collection"]);
        Assert.Equal(2, rig.Events("collection.snapshot.sweep").Count());   // one line per logical set (liked, albums)
    }

    [Fact]
    public async Task FetchWireSet_PendingShield_SkipsInboundApplyToo()
    {
        // The server page carries a uri whose local intent is still in flight (an unsave being drained): the walk must
        // not flip it back to Confirmed under the user's action — the drain decides it.
        var rig = new Rig((_, _) => Ok(Page("t", "", ("spotify:track:unsaving", 1), ("spotify:track:t1", 2)).ToByteArray()),
            hasPending: (s, u) => u == "spotify:track:unsaving");

        await rig.Fetcher.FetchWireSetAsync("collection", TestContext.Current.CancellationToken);

        Assert.False(rig.Store.IsSaved("liked", "spotify:track:unsaving"));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t1"));
    }

    // ── the reconcile pass ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_Drift_AppliesSnapshot_Sweeps_AndSetsToken()
    {
        // Local: t1 + an hour-old "gone". Server: t1, t2, al1. Drift on both logical sets (liked missing t2 / extra
        // gone; albums missing al1) → the walked snapshot is applied off the SAME walk, swept, tokened; only the members
        // we did not have are hydrated.
        var rig = new Rig((_, _) => Ok(Page("rec-tok", "", ("spotify:track:t1", 1), ("spotify:track:t2", 2), ("spotify:album:al1", 3)).ToByteArray()));
        rig.Store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed, 1_000);
        rig.Store.SetSaved("liked", "spotify:track:gone", true, SyncState.Confirmed, NowMs - 3_600_000);
        rig.Revs["collection"] = "stale";

        var outcome = await rig.Fetcher.ReconcileWireSetAsync("collection", "test", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionReconcileOutcome.Repaged, outcome);
        Assert.Single(rig.Requests);                                        // one walk, not two
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t2"));
        Assert.True(rig.Store.IsSaved("albums", "spotify:album:al1"));
        Assert.False(rig.Store.IsSaved("liked", "spotify:track:gone"));
        Assert.Equal("rec-tok", rig.Revs["collection"]);
        Assert.Equal(new[] { "spotify:track:t2", "spotify:album:al1" }, rig.Hydrated);
        Assert.Equal(2, rig.Events("collection.reconcile.drift").Count());  // liked + albums each reported
        Assert.Empty(rig.Events("collection.reconcile.pass"));
    }

    [Fact]
    public async Task Reconcile_NoDrift_WritesNothing_TokenUntouched()
    {
        var rig = new Rig((_, _) => Ok(Page("newer", "", ("spotify:track:t1", 1), ("spotify:album:al1", 2)).ToByteArray()));
        rig.Store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed, 1_000);
        rig.Store.SetSaved("albums", "spotify:album:al1", true, SyncState.Confirmed, 2_000);
        rig.Revs["collection"] = "keep";
        var changes = new ChangeCollector();
        using var sub = rig.Store.Changes.Subscribe(changes);
        lock (changes.All) changes.All.Clear();   // Store.Changes replays its LATEST change to a new subscriber (SimpleSubject); only what the reconcile emits counts

        var outcome = await rig.Fetcher.ReconcileWireSetAsync("collection", "test", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionReconcileOutcome.NoDrift, outcome);
        Assert.Equal("keep", rig.Revs["collection"]);
        Assert.Empty(rig.Hydrated);
        lock (changes.All) Assert.Empty(changes.All);
        Assert.Single(rig.Events("collection.reconcile.pass"));
    }

    [Fact]
    public async Task Reconcile_ShieldedMembers_AreNotDrift()
    {
        // A pending like the server has not paged yet and a one-minute-old ack'd like are the user's own in-flight
        // actions, not corruption: the pass reports clean and touches nothing.
        var rig = new Rig((_, _) => Ok(Page("t", "", ("spotify:track:t1", 1)).ToByteArray()),
            hasPending: (s, u) => u == "spotify:track:pending");
        rig.Store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed, 1_000);
        rig.Store.SetSaved("liked", "spotify:track:pending", true, SyncState.Pending, NowMs);
        rig.Store.SetSaved("liked", "spotify:track:recent", true, SyncState.Confirmed, NowMs - 60_000);

        Assert.Equal(CollectionReconcileOutcome.NoDrift, await rig.Fetcher.ReconcileWireSetAsync("collection", "test", TestContext.Current.CancellationToken));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:pending"));
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:recent"));
    }

    [Fact]
    public async Task Reconcile_UnverifiedWalk_LogsDrift_ButDoesNotApply()
    {
        // Overlapping pages → unverified. The drift is reported (so the log shows "server says 2, we hold 1") but the
        // local baseline is left alone: an unprovable walk is not a better authority than the delta stream.
        var rig = new Rig((_, n) => n == 1
            ? Ok(Page("tok-A", "p2", ("spotify:track:t1", 1)).ToByteArray())
            : Ok(Page("tok-B", "", ("spotify:track:t1", 1), ("spotify:track:t2", 2)).ToByteArray()));
        rig.Store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed, 1_000);
        rig.Revs["collection"] = "keep";

        var outcome = await rig.Fetcher.ReconcileWireSetAsync("collection", "test", TestContext.Current.CancellationToken);

        Assert.Equal(CollectionReconcileOutcome.SkippedUnverified, outcome);
        Assert.False(rig.Store.IsSaved("liked", "spotify:track:t2"));      // NOT applied
        Assert.Equal("keep", rig.Revs["collection"]);                      // untouched
        Assert.Empty(rig.Hydrated);
        Assert.Single(rig.Events("collection.reconcile.drift"));
        Assert.Single(rig.Events("collection.snapshot.unverified"));
    }
}
