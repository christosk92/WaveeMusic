// ── Wavee.Tests/FetchDedupTests.cs — the in-flight ROUTE index: a row ask and an edge ask that resolve to the SAME
//    wire call must produce ONE request, not two (docs/plans/wavee's fetch-runner de-dupe gap).
//
// THE BUG THIS FILE GATES. A row bucket and an edge bucket are — by construction — two different `Demand`s: the
// bucket key is `(provider, subject, kind, edge)` and `FetchSubject.Entity` can never equal
// `FetchSubject.Edge`, so the planner cannot merge them. But `FetchRoutes` can send BOTH of them to the very same
// transport (an album's row asks `Metadata(AlbumV4)` and its `AlbumTracks` edge asks the very same route at offset 0;
// a playlist's row and its `PlaylistTracks` edge both ask `Spclient(PlaylistRead)`; an artist's row and BOTH its
// `ArtistPopular`/`ArtistRelated` edges ask `Pathfinder(ArtistOverview)`). Opening any of those three pages therefore
// fired the request twice (three times for the artist) — this file is the in-flight route index that stops it, and
// the re-plan path that keeps the dropped ask from turning into a skeleton forever.
//
// SINCE WAVE D4 a door never sends: the page's row ask and its edge ask land in their buckets and leave together on the
// tick's `Fetch.Drain()`, which every fact below runs where the host's tick would. Within one priority the drain sends
// ROW buckets before EDGE buckets — the row batch is what indexes the route the edge dedupes against — so the facts
// hold whichever of the two asks a page happens to make first.
//
// These tests never read production source (house rule): everything here goes through the public door —
// `Entities.Ensure` / `Entities.EnsureEdge`, `Fetch.Answer` / `Fetch.Failed` — and a fake transport that records what
// it was actually handed, the same shape `FetchTests.RecordingProvider` uses for the row-only facts.

using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>What one <see cref="FetchBatch"/> carried, captured BY VALUE at <c>Start</c>: the runner recycles the
/// batch object the moment it settles, so holding the reference would be holding a lie.</summary>
readonly record struct SeenBatch(uint Ticket, FetchSubject Subject, EntityKind Kind, FetchEdge Edge, int Offset,
                                  uint Wanted, int Count, FetchPriority Priority);

/// <summary>A transport that records every batch it is handed and answers nothing until the test says so — the twin
/// of <c>FetchTests.RecordingProvider</c>, widened to the fields a dedupe test needs in order to tell a ROW batch
/// from an EDGE one (<see cref="FetchBatch.Subject"/>, <see cref="FetchBatch.Edge"/>).</summary>
sealed class DedupProvider(EntityProvider provider) : FetchProvider
{
    public override EntityProvider Provider { get; } = provider;
    public readonly List<SeenBatch> Seen = new();

    public override void Start(FetchBatch batch) => Seen.Add(new SeenBatch(
        batch.Ticket, batch.Subject, batch.Kind, batch.Edge, batch.Offset, batch.Wanted, batch.Count, batch.Priority));
}

[Collection(EntitiesCollection.Name)]
public class FetchDedupTests : IDisposable
{
    public FetchDedupTests()
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
    }

    static Scope Boot()
    {
        Entities.Boot(CatalogScope.Fake());
        return Entities.Current;
    }

    /// <summary>One GID-form row of <paramref name="token"/>'s kind — <c>spotify:&lt;token&gt;:</c> + 22 base62
    /// characters, the shape a real catalog row is made of (<c>FetchTests.GidRows</c>, generalized over the kind
    /// token so an album/artist/playlist row can be built the same deterministic way a track row is).</summary>
    static int GidRow(Table table, string token, int seed)
    {
        string prefix = "spotify:" + token + ":";
        Span<char> uri = stackalloc char[prefix.Length + Base62.GidChars];
        prefix.AsSpan().CopyTo(uri);
        ulong n = (ulong)seed;
        Base62.Encode(new UInt128(n * 0x9E37_79B9_7F4A_7C15UL + 11, n * 0xC2B2_AE3D_27D4_EB4FUL + 3), uri[prefix.Length..]);
        return table.Slot(uri);
    }

    // ── the three known collisions: a row ask and an edge ask that resolve to the same transport ───────────────────

    [Fact]
    public void Album_row_and_its_tracks_edge_collide_into_one_request()
    {
        Scope scope = Boot();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int album = GidRow(scope.Albums, "album", 1);

        Entities.Ensure(scope.Albums, new[] { album }, (uint)AlbumFields.Detail, FetchPriority.Visible);
        Entities.EnsureEdge(FetchEdge.AlbumTracks, album);
        Fetch.Drain();                                             // the tick: row first, then the edge

        // The edge ask is real — `WasAsked` flips, so no OTHER caller re-asks it either — it is just never SENT a
        // second time while the row's own `Metadata(AlbumV4)` batch is already carrying the very same request.
        Assert.True(scope.Edges.AlbumTracks.WasAsked(album, 0));
        Assert.Single(provider.Seen);
        Assert.Equal(FetchSubject.Entity, provider.Seen[0].Subject);
    }

    [Fact]
    public void Playlist_row_and_its_tracks_edge_collide_into_one_request()
    {
        Scope scope = Boot();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int playlist = GidRow(scope.Playlists, "playlist", 2);

        Entities.Ensure(scope.Playlists, new[] { playlist }, (uint)PlaylistFields.All, FetchPriority.Visible);
        Entities.EnsureEdge(FetchEdge.PlaylistTracks, playlist);
        Fetch.Drain();                                             // the tick: row first, then the edge

        // Both resolve to `Spclient(PlaylistRead)` — the full decorated body — so the edge ask must not repeat it.
        Assert.True(scope.Edges.PlaylistTracks.WasAsked(playlist, 0));
        Assert.Single(provider.Seen);
        Assert.Equal(FetchSubject.Entity, provider.Seen[0].Subject);
    }

    [Fact]
    public void Artist_row_and_both_its_edges_collide_into_one_request()
    {
        // Three asks in the real bug — the row plus BOTH `ArtistPopular` and `ArtistRelated` — because all three
        // resolve to the very same pathfinder query (`ArtistOverview`). One row batch must absorb both edges, not
        // just the first one asked.
        Scope scope = Boot();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int artist = GidRow(scope.Artists, "artist", 3);

        Entities.Ensure(scope.Artists, new[] { artist }, (uint)ArtistFields.All, FetchPriority.Visible);
        Entities.EnsureEdge(FetchEdge.ArtistPopular, artist);
        Entities.EnsureEdge(FetchEdge.ArtistRelated, artist);
        Fetch.Drain();                                             // the tick: row first, then the edge

        Assert.True(scope.Edges.ArtistPopular.WasAsked(artist, 0));
        Assert.True(scope.Edges.ArtistRelated.WasAsked(artist, 0));
        Assert.Single(provider.Seen);
        Assert.Equal(FetchSubject.Entity, provider.Seen[0].Subject);
    }

    // ── two genuinely different transports for the same parent are still both sent ─────────────────────────────────

    [Fact]
    public void Two_different_transports_for_the_same_album_are_both_sent()
    {
        // `AlbumMerch` routes to its own pathfinder query — never to `AlbumV4` — so nothing about it collides with
        // the row's metadata request, and the dedupe must leave both alone.
        Scope scope = Boot();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int album = GidRow(scope.Albums, "album", 4);

        Entities.Ensure(scope.Albums, new[] { album }, (uint)AlbumFields.Detail, FetchPriority.Visible);
        Entities.EnsureEdge(FetchEdge.AlbumMerch, album);
        Fetch.Drain();                                             // the tick: row first, then the edge

        Assert.Equal(2, provider.Seen.Count);
        Assert.Contains(provider.Seen, x => x.Subject == FetchSubject.Entity);
        Assert.Contains(provider.Seen, x => x.Subject == FetchSubject.Edge && x.Edge == FetchEdge.AlbumMerch);
    }

    // ── THE CRITICAL PATH: a row answer that stages nothing re-plans the dropped edge ───────────────────────────────

    [Fact]
    public void A_row_answer_that_stages_nothing_replans_the_dropped_edge_and_it_settles()
    {
        Scope scope = Boot();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int album = GidRow(scope.Albums, "album", 5);

        Entities.Ensure(scope.Albums, new[] { album }, (uint)AlbumFields.Detail, FetchPriority.Visible);
        Entities.EnsureEdge(FetchEdge.AlbumTracks, album);
        Fetch.Drain();                                             // the tick: row first, then the edge
        Assert.Single(provider.Seen);                              // the edge ask was absorbed, not sent

        // The row answers with NOTHING for the relation (a bare answer, or one whose decode never reached
        // `AlbumTracks`): the list is still Unknown, so the dropped edge ask must not be lost — it goes back into
        // its own bucket and the very same `Pump` at the end of `Answer` sends it for real.
        Fetch.Answer(provider.Seen[0].Ticket, null);

        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(FetchSubject.Edge, provider.Seen[1].Subject);
        Assert.Equal(FetchEdge.AlbumTracks, provider.Seen[1].Edge);
        Assert.Equal(EdgeState.Unknown, scope.Edges.AlbumTracks.State(album));   // in flight now, not stuck forever

        // …and it reaches a SETTLED state exactly like any ordinary edge answer would (G-050's whole point).
        scope.Edges.AlbumTracks.ReplaceRun(album, ReadOnlySpan<int>.Empty, default);
        Fetch.Answer(provider.Seen[1].Ticket, null);

        Assert.Equal(EdgeState.Complete, scope.Edges.AlbumTracks.State(album));
    }

    [Fact]
    public void A_row_answer_that_already_staged_the_edge_does_not_replan_it()
    {
        // The other half of the critical path: a real AlbumV4 decode carries the disc rows in the SAME answer, so the
        // relation is no longer Unknown by the time the row settles — the dropped ask must stay dropped, or the
        // dedupe would just move the duplicate request from "at once" to "one drain later".
        Scope scope = Boot();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int album = GidRow(scope.Albums, "album", 6);

        Entities.Ensure(scope.Albums, new[] { album }, (uint)AlbumFields.Detail, FetchPriority.Visible);
        Entities.EnsureEdge(FetchEdge.AlbumTracks, album);
        Fetch.Drain();                                             // the tick: row first, then the edge
        Assert.Single(provider.Seen);

        scope.Edges.AlbumTracks.ReplaceRun(album, ReadOnlySpan<int>.Empty, default);   // staged as a side effect…
        Fetch.Answer(provider.Seen[0].Ticket, null);                                   // …of THIS answer

        Assert.Single(provider.Seen);                              // nothing else was ever sent for it
        Assert.Equal(EdgeState.Complete, scope.Edges.AlbumTracks.State(album));
    }

    // ── a dropped ask must survive the row batch FAILING, retryable or not ──────────────────────────────────────────

    [Fact]
    public void A_dropped_edge_ask_is_not_lost_when_the_row_batch_fails_retryably()
    {
        Scope scope = Boot();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int album = GidRow(scope.Albums, "album", 7);

        Entities.Ensure(scope.Albums, new[] { album }, (uint)AlbumFields.Detail, FetchPriority.Visible);
        Entities.EnsureEdge(FetchEdge.AlbumTracks, album);
        Fetch.Drain();                                             // the tick: row first, then the edge
        Assert.Single(provider.Seen);

        Fetch.Failed(provider.Seen[0].Ticket, 503, 0);              // retryable: the ROW itself waits out a backoff…

        // …but the wire slot it held is free right now, and the edge it pre-empted never asked for a backoff of its
        // own — it goes out in the very same settle, not whenever the row's retry eventually fires.
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(FetchSubject.Edge, provider.Seen[1].Subject);
        Assert.Equal(FetchEdge.AlbumTracks, provider.Seen[1].Edge);
    }

    [Fact]
    public void A_dropped_edge_ask_is_not_lost_when_the_row_batch_fails_terminally()
    {
        Scope scope = Boot();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int album = GidRow(scope.Albums, "album", 8);

        Entities.Ensure(scope.Albums, new[] { album }, (uint)AlbumFields.Detail, FetchPriority.Visible);
        Entities.EnsureEdge(FetchEdge.AlbumTracks, album);
        Fetch.Drain();                                             // the tick: row first, then the edge
        Assert.Single(provider.Seen);

        Fetch.Failed(provider.Seen[0].Ticket, 404, 0);              // terminal: the row itself is un-asked outright…

        Assert.Equal(2, provider.Seen.Count);                       // …and the edge it pre-empted still goes out
        Assert.Equal(FetchSubject.Edge, provider.Seen[1].Subject);
        Assert.Equal(FetchEdge.AlbumTracks, provider.Seen[1].Edge);
    }
}
