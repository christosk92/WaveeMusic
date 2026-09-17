// ── Wavee.Tests/StoreEdgeTests.cs — the five persisted library relations: round trip, the stride guard, Warm ──────
//
// Store.cs's `RegisterLibraryEdges` wires appliers for Liked/SavedAlbums/FollowedArtists/SavedShows/Pins (the
// relations whose payload is `LibraryEdge`) and `SaveLibraryEdgesTouchedBy` gives `SaveEdges` its call site off
// `WriteBehind`. These tests exercise both against a real temp-db file — the same harness `StoreTests` uses, because
// the thing under test IS the file: a page whose on-disk payload width disagrees with `sizeof(LibraryEdge)` must be
// DROPPED, never reinterpreted (the payload is not covered by the schema fingerprint — `SaveEdges`'s own doc), an
// unregistered relation (Rootlist, deliberately) must come back with nothing crashing, and `Store.Warm` must
// repopulate a relation from a brand-new, empty table set — a real restart, not just a same-process re-read.

using System.Collections.Concurrent;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>A payload shape `LibraryEdge` never had: three longs, 24 bytes, where `LibraryEdge` is far narrower.
/// Stands in for a build downgrade or a struct whose fields changed shape without moving the schema fingerprint —
/// exactly the hazard <c>Store.SaveEdges</c>'s doc names, since the payload's shape is not part of the DDL text.</summary>
readonly record struct BogusWideEdge(long A, long B, long C);

[Collection(EntitiesCollection.Name)]
public class StoreEdgeTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-v3-store-edge-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public StoreEdgeTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Post = a => _posted.Enqueue(a);      // the UI drain, run by the test thread where it belongs
        Store.RegisterLibraryEdges();
        Store.Use(_dbPath);
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

    /// <summary>A scope with a REAL account: <c>CatalogScope.Fake()</c> (as <c>StoreTests</c> uses it) has an empty
    /// account, so <c>Entities.ResolveMe</c> leaves <c>MeSlot</c> at <see cref="Table.None"/> — no library relation
    /// hangs off nothing (Store.cs's <c>Warm</c>). These tests need a real row.</summary>
    static CatalogScope AccountScope(string account = "edge-tester") => new("wavee-test", account, "en-US", "US", 0, true);

    Scope Boot(CatalogScope? key = null)
    {
        Entities.Boot(key ?? AccountScope());
        Store.Flush();                              // let Warm resolve the scope id (and warm meta/edges) first
        return Entities.Current;
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    static string TrackUri(string token) => "wavee:test:track:" + token;
    static string AlbumUri(string token) => "wavee:test:album:" + token;

    // ── the round trip: two of the five relations ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_liked_edge_round_trips_through_the_file()
    {
        Scope scope = Boot();
        int me = scope.MeSlot;
        Assert.NotEqual(Table.None, me);
        EntityId meId = scope.Users.Id[me];
        StagedId parent = meId;

        int t1 = scope.Tracks.Slot(TrackUri("a").AsSpan());
        int t2 = scope.Tracks.Slot(TrackUri("b").AsSpan());

        Staging s = Staging.Rent();
        var liked = s.Run(Relation.Liked);
        liked.Add(scope.Tracks.Id[t1]).At = 1_700_000_000;
        liked.Add(scope.Tracks.Id[t2]).At = 1_600_000_000;
        liked.End(in parent);
        Entities.Commit(s);
        Assert.True(Store.WriteBehind(s));   // the call site: SaveLibraryEdgesTouchedBy sees the Liked run and persists it
        Store.Flush();

        Assert.Equal(EdgeState.Complete, scope.Edges.Liked.State(me));
        Assert.True(scope.Edges.Liked.Targets(me).SequenceEqual(new[] { t1, t2 }));

        // Forget the in-memory answer entirely and ask the disk for it back — the round trip the applier exists for.
        scope.Edges.Liked.Clear(me);
        Assert.Equal(EdgeState.Unknown, scope.Edges.Liked.State(me));

        Assert.True(Store.ReadEdges(scope, EdgeRelation.Liked, meId));
        Store.Flush();
        DrainPosts();

        Assert.Equal(EdgeState.Complete, scope.Edges.Liked.State(me));
        Assert.True(scope.Edges.Liked.Targets(me).SequenceEqual(new[] { t1, t2 }));
        Assert.Equal(1_700_000_000, scope.Edges.Liked.Payload(me)[0].AddedAt);
    }

    [Fact]
    public void A_saved_albums_edge_round_trips_through_the_file()
    {
        Scope scope = Boot();
        int me = scope.MeSlot;
        EntityId meId = scope.Users.Id[me];
        StagedId parent = meId;

        int a1 = scope.Albums.Slot(AlbumUri("x").AsSpan());

        Staging s = Staging.Rent();
        var saved = s.Run(Relation.SavedAlbums);
        saved.Add(scope.Albums.Id[a1]).At = 42;
        saved.End(in parent);
        Entities.Commit(s);
        Assert.True(Store.WriteBehind(s));
        Store.Flush();

        scope.Edges.SavedAlbums.Clear(me);
        Assert.True(Store.ReadEdges(scope, EdgeRelation.SavedAlbums, meId));
        Store.Flush();
        DrainPosts();

        Assert.Equal(EdgeState.Complete, scope.Edges.SavedAlbums.State(me));
        Assert.True(scope.Edges.SavedAlbums.Targets(me).SequenceEqual(new[] { a1 }));
        Assert.Equal(42, scope.Edges.SavedAlbums.Payload(me)[0].AddedAt);
    }

    // ── the stride guard: the one SaveEdges's doc calls out by name ─────────────────────────────────────────────────

    [Fact]
    public void A_page_whose_stride_disagrees_with_the_payload_struct_is_dropped_not_reinterpreted()
    {
        Scope scope = Boot();
        int me = scope.MeSlot;
        EntityId meId = scope.Users.Id[me];
        int t1 = scope.Tracks.Slot(TrackUri("stride-a").AsSpan());

        // Nothing this build registers ever writes THIS shape under kind=Liked; it stands in for a stale build's
        // bytes (Store.cs's SaveEdges doc: the payload's shape is not covered by the schema fingerprint).
        var bogus = new EdgeTable<BogusWideEdge>();
        bogus.Replace(me, new[] { t1 }, new BogusWideEdge[] { new(1, 2, 3) }, EdgeState.Complete, 1);
        Assert.True(Store.SaveEdges(EdgeRelation.Liked, bogus, me, meId, scope.Tracks));
        Store.Flush();

        Assert.Equal(EdgeState.Unknown, scope.Edges.Liked.State(me));   // nothing has landed in the REAL relation yet

        Assert.True(Store.ReadEdges(scope, EdgeRelation.Liked, meId));
        Store.Flush();
        DrainPosts();

        // The applier saw Stride != sizeof(LibraryEdge) and dropped the whole page rather than reinterpreting 24
        // bytes per edge as an 8-ish-byte LibraryEdge.
        Assert.Equal(EdgeState.Unknown, scope.Edges.Liked.State(me));
        Assert.Equal(0, scope.Edges.Liked.Count(me));
    }

    // ── no registered applier drops cleanly (Rootlist, deliberately unregistered) ───────────────────────────────────

    [Fact]
    public void A_relation_with_no_registered_applier_drops_cleanly()
    {
        Scope scope = Boot();
        int me = scope.MeSlot;
        EntityId meId = scope.Users.Id[me];
        int t1 = scope.Tracks.Slot(TrackUri("no-applier-a").AsSpan());

        // Rootlist is deliberately never registered (RegisterLibraryEdges' own doc: RootlistEdge owns two StringIds,
        // an interner index a page cannot restore) — SaveEdges itself does not know or care which relations have an
        // applier, so the row lands on disk exactly as any other relation's would.
        var bogus = new EdgeTable<NoEdge>();
        bogus.Replace(me, new[] { t1 }, ReadOnlySpan<NoEdge>.Empty, EdgeState.Complete, 1);
        Assert.True(Store.SaveEdges(EdgeRelation.Rootlist, bogus, me, meId, scope.Tracks));
        Store.Flush();

        var before = Store.Stats;
        Assert.True(Store.ReadEdges(scope, EdgeRelation.Rootlist, meId));
        Store.Flush();
        DrainPosts();

        Assert.Equal(EdgeState.Unknown, scope.Edges.Rootlist.State(me));   // s_edgeAppliers[Rootlist] is null: never applied
        Assert.Equal(before.Faults, Store.Stats.Faults);                  // and that is not a fault — nobody was told, on purpose
    }

    // ── Store.Warm repopulates a relation at boot, from a brand-new table set ───────────────────────────────────────

    [Fact]
    public void Store_Warm_repopulates_the_edge_table_at_boot()
    {
        CatalogScope key = AccountScope("warm-tester");
        Scope scope = Boot(key);
        int me = scope.MeSlot;
        EntityId meId = scope.Users.Id[me];
        StagedId parent = meId;
        int t1 = scope.Tracks.Slot(TrackUri("warm-a").AsSpan());
        int t2 = scope.Tracks.Slot(TrackUri("warm-b").AsSpan());

        Staging s = Staging.Rent();
        var liked = s.Run(Relation.Liked);
        liked.Add(scope.Tracks.Id[t1]).At = 111;
        liked.Add(scope.Tracks.Id[t2]).At = 222;
        liked.End(in parent);
        Entities.Commit(s);
        Assert.True(Store.WriteBehind(s));
        Store.Flush();

        // Close the store and re-open the SAME file behind a brand-new, empty table set — a real restart, not a
        // same-process re-read: `scope2` has never heard of these two tracks before this test resumes.
        Store.Shutdown();
        Store.Use(_dbPath);
        Scope scope2 = Boot(key);
        DrainPosts();          // Warm's ReadEdgesCore posts its applier call back through Store.Post

        int me2 = scope2.MeSlot;
        Assert.NotEqual(Table.None, me2);
        Assert.Equal(EdgeState.Complete, scope2.Edges.Liked.State(me2));

        int t1Again = scope2.Tracks.Slot(TrackUri("warm-a").AsSpan());
        int t2Again = scope2.Tracks.Slot(TrackUri("warm-b").AsSpan());
        Assert.True(scope2.Edges.Liked.Targets(me2).SequenceEqual(new[] { t1Again, t2Again }));
    }

    // ── meta survives the same reboot (bonus over StoreTests' synchronous round trip) ───────────────────────────────

    [Fact]
    public void A_meta_value_survives_a_reboot()
    {
        Boot();
        Store.MetaSet("library.sync-token", "token-1");
        Store.Flush();      // the durable write is enqueued behind the cache write (MetaSet's own doc) — let it land
        Store.Shutdown();

        Store.Use(_dbPath);
        Boot();
        // `Boot()` flushes the STORE thread, but the warmed meta rows come back to the UI side through `Store.Post`
        // (`WarmMetaCore` reads on the store thread and hands the dictionary over, so `MetaGet` can stay a pure
        // in-memory lookup callable from the UI thread at any time). Without draining that post, `s_meta` is still
        // the empty dictionary `Boot` cleared and the lookup answers null — the store did its half correctly.
        DrainPosts();

        Assert.Equal("token-1", Store.MetaGet("library.sync-token"));
    }
}
