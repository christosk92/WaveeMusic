// ── Wavee.Tests/EdgeDoorTests.cs — the relation half of the planner (gap batch B1: G-042, G-050) ───────────────────
//
// `Entities.EnsureEdge` is the door relations never had: every library, liked and sidebar surface skeletoned forever
// because nothing ever ASKED for `Edges.Rootlist`/`Liked`/…. These facts drive the real planner over a real scope with
// a recording transport, the same way FetchTests drives the row door.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EdgeDoorTests : IDisposable
{
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
    }

    /// <summary>A live-shaped scope: the account is a bare username, as the session hands it over.</summary>
    static Scope SignedIn()
    {
        Entities.Boot(new CatalogScope("spotify", "christos", "en-US", "US", 0, true));
        return Entities.Current;
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
        Assert.Equal(2, provider.Seen.Count);

        Entities.RefreshEdge(FetchEdge.Liked, scope.MeSlot);
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
        Fetch.Failed(provider.Seen[0].Ticket, 403, 0);

        Assert.Equal(EdgeState.Failed, rootlist.Readiness(scope.MeSlot));   // G-050: the banner, not a skeleton
        Assert.Equal(403, rootlist.FailureOf(scope.MeSlot));
        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
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
        Fetch.Failed(provider.Seen[0].Ticket, 401, 0);
        Assert.Equal(EdgeState.Failed, rootlist.Readiness(scope.MeSlot));
        Assert.Single(provider.Seen);

        Fetch.Resume();

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
        Fetch.Failed(provider.Seen[0].Ticket, 503, 0);
        Entities.EnsureEdge(FetchEdge.Recents, scope.MeSlot);

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
        Fetch.Answer(provider.Seen[0].Ticket, null);                        // the route had nothing
        Assert.Equal(EdgeState.Failed, rootlist.Readiness(scope.MeSlot));
        Assert.Equal(EdgeTableBase.NoRoute, rootlist.FailureOf(scope.MeSlot));
        Entities.EnsureEdge(FetchEdge.Rootlist, scope.MeSlot);
        Assert.Single(provider.Seen);                                       // …and it is not asked again per mount

        Entities.RefreshEdge(FetchEdge.Rootlist, scope.MeSlot);
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

        Assert.Equal(1, provider.Batches);
        FetchBatch batch = provider.Last!;
        Assert.Equal(FetchEdge.HomeSections, batch.Edge);
        Assert.Equal(Home.FeedUri, batch.Uri(0));
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
