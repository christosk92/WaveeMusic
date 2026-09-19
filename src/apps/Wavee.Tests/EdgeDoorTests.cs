// ── Wavee.Tests/EdgeDoorTests.cs — the relation half of the planner (gap batch B1: G-042, G-050) ───────────────────
//
// `Entities.EnsureEdge` is the door relations never had: every library, liked and sidebar surface skeletoned forever
// because nothing ever ASKED for `Edges.Rootlist`/`Liked`/…. These facts drive the real planner over a real scope with
// a recording transport, the same way FetchTests drives the row door. The last three drive its DISK LEG (wave D2) over
// a real store file: a playlist's persisted membership lands first, and only then does the network ask go out.
// Since wave D4 a door never sends: every fact runs `Fetch.Drain()` — the host's tick — where the tick would come, and
// the "nothing went out" facts drain first, so their emptiness is the door's verdict and not an undrained bucket.

using System.Collections.Concurrent;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EdgeDoorTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-v3-edge-door-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public EdgeDoorTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    /// <summary>A live-shaped scope: the account is a bare username, as the session hands it over.</summary>
    static Scope SignedIn()
    {
        Entities.Boot(new CatalogScope("spotify", "christos", "en-US", "US", 0, true));
        return Entities.Current;
    }

    /// <summary>The same scope over a REAL store file, its posts drained by this thread (the disk-leg facts).</summary>
    Scope SignedInWithStore()
    {
        Store.Post = a => _posted.Enqueue(a);
        Store.Use(_dbPath);
        return Opened();
    }

    /// <summary>A real restart over the same file: a brand-new table set that has never seen the persisted list.</summary>
    Scope Relaunch()
    {
        Store.Shutdown();
        Store.Use(_dbPath);
        return Opened();
    }

    Scope Opened()
    {
        Scope scope = SignedIn();
        Store.Flush();
        DrainPosts();
        return scope;
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    /// <summary>Persist a two-member playlist the way a full read does: commit, then the write-behind.</summary>
    static void PersistList(string playlist, string revision)
    {
        Staging answer = ListAnswers.FullRead(playlist, revision,
            [ListAnswers.Member(ListAnswers.TrackUri(9_001), "9001"), ListAnswers.Member(ListAnswers.TrackUri(9_002), "9002")]);
        Entities.Commit(answer);
        Assert.True(Store.WriteBehind(answer));
        Store.Flush();
    }

    /// <summary>A transport that records, BY VALUE, each batch's relation and the revision its first parent carried —
    /// the one thing the disk leg changes about the network ask. Batches are never settled here.</summary>
    sealed class RevisionProvider(EntityProvider provider) : FetchProvider
    {
        public override EntityProvider Provider { get; } = provider;
        public readonly List<(FetchEdge Edge, string? Revision)> Seen = new();

        public override void Start(FetchBatch batch)
            => Seen.Add((batch.Edge, batch.Count > 0 ? batch.Revisions[0] : null));
    }

    [Fact]
    public void The_account_row_is_the_uri_every_library_answer_names_it_with()
    {
        // A library answer is staged against `spotify:user:<name>`; keyed by the bare name, the account row was a SECOND
        // user row with no provider, and every relation landed on the wrong parent.
        Scope scope = SignedIn();
        Assert.NotEqual(Table.None, scope.MeSlot);
        Assert.Equal("spotify:user:christos", scope.Users.Id[scope.MeSlot].Text);
        Assert.Equal(EntityProvider.Spotify, scope.Users.Id[scope.MeSlot].Provider);
        Assert.Equal(scope.MeSlot, Entities.User(EntityUri.Parse("spotify:user:christos".AsSpan())).Slot);
    }

    [Fact]
    public void Asking_a_relation_twice_in_a_scope_sends_one_request()
    {
        Scope scope = SignedIn();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);

        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();

        Assert.Equal(1, provider.Batches);
        FetchBatch batch = provider.Last!;
        Assert.Equal(FetchSubject.Edge, batch.Subject);
        Assert.Equal(FetchEdge.Rootlist, batch.Edge);
        Assert.Equal(0, batch.Offset);
        Assert.Equal(0u, batch.Wanted);
        Assert.Equal("spotify:user:christos", batch.Uri(0));                 // the PARENT, as text the Api can path
        Assert.Equal(1, Fetch.Deduped);
    }

    [Fact]
    public void The_next_page_is_a_new_ask_and_a_refresh_asks_again()
    {
        Scope scope = SignedIn();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);

        Entities.EnsureEdge(FetchEdge.Liked, scope.MeSlot);
        Entities.EnsureEdge(FetchEdge.Liked, scope.MeSlot, offset: 300);
        Entities.EnsureEdge(FetchEdge.Liked, scope.MeSlot, offset: 300);
        Fetch.Drain();
        Assert.Equal(2, provider.Seen.Count);

        Entities.RefreshEdge(FetchEdge.Liked, scope.MeSlot);
        Fetch.Drain();
        Assert.Equal(3, provider.Seen.Count);
    }

    [Fact]
    public void A_held_revision_rides_the_batch_so_the_provider_can_read_the_diff()
    {
        Scope scope = SignedIn();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        scope.Edges.SetRootlistRevision(scope.MeSlot, Entities.Strings.Intern("300,deadbeef"));

        Entities.RefreshEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();

        Assert.Equal("300,deadbeef", provider.Last!.Revisions[0]);
    }

    [Fact]
    public void Different_relations_of_one_parent_never_share_a_request()
    {
        Scope scope = SignedIn();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);

        Entities.EnsureEdge(FetchEdge.SavedAlbums, scope.MeSlot);
        Entities.EnsureEdge(FetchEdge.FollowedArtists, scope.MeSlot);
        Fetch.Drain();                                                        // one tick, still two relations

        Assert.Equal(2, provider.Seen.Count);
    }

    [Fact]
    public void A_terminal_failure_surfaces_as_failed_and_the_next_mount_retries()
    {
        Scope scope = SignedIn();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        var rootlist = scope.Edges.Rootlist;

        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();
        Fetch.Failed(provider.Seen[0].Ticket, 403, 0);

        Assert.Equal(EdgeState.Failed, rootlist.Readiness(scope.MeSlot));   // G-050: the banner, not a skeleton
        Assert.Equal(403, rootlist.FailureOf(scope.MeSlot));
        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(EdgeState.Unknown, rootlist.Readiness(scope.MeSlot));  // asking again is loading again
    }

    [Fact]
    public void An_edge_refused_before_the_session_authorised_is_re_planned_when_it_resumes()
    {
        // The row door's `Resume` covers parents too: a 401 on the rootlist at boot (asked before the session adopted
        // its scope) is un-asked and recorded, and the Online transition asks it again — page and urgency kept.
        Scope scope = SignedIn();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        var rootlist = scope.Edges.Rootlist;

        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot, priority: FetchPriority.Prefetch);
        Fetch.Drain();
        Fetch.Failed(provider.Seen[0].Ticket, 401, 0);
        Assert.Equal(EdgeState.Failed, rootlist.Readiness(scope.MeSlot));
        Assert.Single(provider.Seen);

        Fetch.Resume();
        Fetch.Pump();                                                       // the Online transition's pump

        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(FetchSubject.Edge, provider.Seen[1].Subject);
        Assert.Equal(FetchEdge.Rootlist, provider.Seen[1].Edge);
        Assert.Equal(0, provider.Seen[1].Offset);
        Assert.Equal(FetchPriority.Prefetch, provider.Seen[1].Priority);
        Assert.Equal(EdgeState.Unknown, rootlist.Readiness(scope.MeSlot));  // asked again: loading, not failed
        Assert.Equal(0, Fetch.Refused);
    }

    [Fact]
    public void A_retryable_failure_keeps_the_ask_and_waits_out_the_backoff()
    {
        Scope scope = SignedIn();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);

        Entities.EnsureEdge(FetchEdge.Recents, scope.MeSlot);
        Fetch.Drain();
        Fetch.Failed(provider.Seen[0].Ticket, 503, 0);
        Entities.EnsureEdge(FetchEdge.Recents, scope.MeSlot);
        Fetch.Drain();

        Assert.Single(provider.Seen);
        Assert.False(scope.Edges.Recents.IsFailed(scope.MeSlot));
        Entities.Now = 60;
        Fetch.Pump();
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(1, provider.Seen[1].Attempt);
    }

    [Fact]
    public void An_answer_that_landed_the_list_clears_the_failure_and_one_that_did_not_is_a_vacancy()
    {
        Scope scope = SignedIn();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        var rootlist = scope.Edges.Rootlist;

        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();
        Fetch.Answer(provider.Seen[0].Ticket, null);                        // the route had nothing
        Assert.Equal(EdgeState.Failed, rootlist.Readiness(scope.MeSlot));
        Assert.Equal(EdgeTableBase.NoRoute, rootlist.FailureOf(scope.MeSlot));
        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();
        Assert.Single(provider.Seen);                                       // …and it is not asked again per mount

        Entities.RefreshEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();
        var s = Staging.Rent();
        ref var list = ref s.Rootlists.Add();
        list.Parent = scope.Users.Id[scope.MeSlot];
        list.Start = 0;
        list.Length = 0;
        Fetch.Answer(provider.Seen[1].Ticket, s);                           // an empty rootlist is an answer

        Assert.Equal(EdgeState.Complete, rootlist.Readiness(scope.MeSlot));
        Assert.False(rootlist.IsFailed(scope.MeSlot));
    }

    [Fact]
    public void A_parent_nobody_can_answer_for_is_a_vacancy_at_once()
    {
        // The offline demo scope: a "me" row keyed by a bare literal routes nowhere. It must not skeleton forever.
        Entities.Boot(new CatalogScope("offline", "wavee-listener", "en-US", "US", 0, true));
        Scope scope = Entities.Current;
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);

        Entities.EnsureEdge(FetchEdge.Pins, scope.MeSlot);
        Fetch.Drain();

        Assert.Empty(provider.Seen);
        Assert.Equal(EdgeState.Failed, scope.Edges.Pins.Readiness(scope.MeSlot));
    }

    [Fact]
    public void A_synthetic_parent_routes_to_the_scopes_catalogue()
    {
        Entities.Boot(CatalogScope.Fake());
        Scope scope = Entities.Current;
        var provider = new HoldingProvider(EntityProvider.Fake);
        Fetch.Register(provider);
        int home = Entities.HomeFeed().Slot;

        Entities.EnsureEdge(FetchEdge.HomeSections, home);
        Fetch.Drain();

        Assert.Equal(1, provider.Batches);
        FetchBatch batch = provider.Last!;
        Assert.Equal(FetchEdge.HomeSections, batch.Edge);
        Assert.Equal(Home.FeedUri, batch.Uri(0));
    }

    // ── the disk leg (wave D2) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE D2 PRIZE, at the door: a playlist nothing has answered this session is offered to the disk FIRST —
    /// no request leaves while the read is out — its persisted list lands Complete, and only THEN does one network ask
    /// go out, carrying the restored revision: the provider reads the <c>/diff</c>, not the playlist in full. Asked once
    /// per session: a second ask finds the parent asked and sends nothing, to the disk or the network.</summary>
    [Fact]
    public void An_unknown_playlist_lands_from_disk_and_then_asks_the_network_with_its_revision()
    {
        SignedInWithStore();
        string playlist = ListAnswers.PlaylistUri(901);
        PersistList(playlist, ListAnswers.RevisionA);

        Scope scope = Relaunch();
        var provider = new RevisionProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int slot = scope.Playlists.Slot(playlist.AsSpan());

        Entities.EnsureEdge(FetchEdge.PlaylistTracks, slot);
        Fetch.Drain();
        Assert.Empty(provider.Seen);                                        // the disk first: nothing is out yet
        Assert.Equal(1, Fetch.ToDisk);

        Store.Flush();
        DrainPosts();                                                       // the list lands, THEN the network leg…
        Fetch.Drain();                                                      // …on the tick after it

        Assert.Equal(EdgeState.Complete, scope.Edges.PlaylistTracks.State(slot));
        Assert.Equal(2, scope.Edges.PlaylistTracks.Count(slot));
        var ask = Assert.Single(provider.Seen);
        Assert.Equal(FetchEdge.PlaylistTracks, ask.Edge);
        Assert.Equal(ListAnswers.RevisionA, ask.Revision);                 // the /diff, not the full read

        Entities.EnsureEdge(FetchEdge.PlaylistTracks, slot);
        Store.Flush();
        DrainPosts();
        Fetch.Drain();
        Assert.Single(provider.Seen);
        Assert.Equal(1, Fetch.ToDisk);
    }

    /// <summary>A disk miss is an answer too: the parent goes to the network exactly once, with no revision (nothing is
    /// held, so it is the full read), and the list stays Unknown until that answer lands.</summary>
    [Fact]
    public void A_disk_miss_asks_the_network_once_without_a_revision()
    {
        Scope scope = SignedInWithStore();
        var provider = new RevisionProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int slot = scope.Playlists.Slot(ListAnswers.PlaylistUri(902).AsSpan());

        Entities.EnsureEdge(FetchEdge.PlaylistTracks, slot);
        Fetch.Drain();
        Assert.Empty(provider.Seen);
        Store.Flush();
        DrainPosts();
        Fetch.Drain();

        var ask = Assert.Single(provider.Seen);
        Assert.Null(ask.Revision);
        Assert.Equal(EdgeState.Unknown, scope.Edges.PlaylistTracks.State(slot));
        Assert.True(scope.Edges.PlaylistTracks.WasDiskAsked(slot));

        Entities.EnsureEdge(FetchEdge.PlaylistTracks, slot);
        Store.Flush();
        DrainPosts();
        Fetch.Drain();
        Assert.Single(provider.Seen);
    }

    /// <summary>A refresh is a question for the server: it goes straight out and never reads the disk, even with a
    /// persisted list sitting there — so the list stays Unknown until the network answers it.</summary>
    [Fact]
    public void A_refresh_asks_the_network_and_never_reads_the_disk()
    {
        SignedInWithStore();
        string playlist = ListAnswers.PlaylistUri(903);
        PersistList(playlist, ListAnswers.RevisionA);

        Scope scope = Relaunch();
        var provider = new RevisionProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int slot = scope.Playlists.Slot(playlist.AsSpan());

        Entities.RefreshEdge(FetchEdge.PlaylistTracks, slot);
        Fetch.Drain();
        var ask = Assert.Single(provider.Seen);                            // out on the first tick, no disk in between
        Assert.Null(ask.Revision);
        Store.Flush();
        DrainPosts();

        Assert.Equal(0, Fetch.ToDisk);
        Assert.False(scope.Edges.PlaylistTracks.WasDiskAsked(slot));
        Assert.Equal(EdgeState.Unknown, scope.Edges.PlaylistTracks.State(slot));
    }

    [Fact]
    public void Every_door_relation_names_a_table_and_a_parent_table()
    {
        Scope scope = SignedIn();
        foreach (FetchEdge edge in Enum.GetValues<FetchEdge>())
        {
            if (edge == FetchEdge.None) continue;
            Assert.NotNull(Fetch.EdgeTableOf(scope, edge));
            Assert.NotNull(Fetch.ParentTableOf(scope, edge));
            Assert.NotEqual(RouteTransport.None, FetchRoutes.ForEdge(edge).Transport);
        }
    }
}
