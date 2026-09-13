using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Library;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Core;
using EntityKind = Wavee.Backend.Metadata.EntityKind;   // the PERSISTED transport vocabulary (Wavee.Core.EntityKind is the routing one)
using Xunit;

namespace Wavee.Tests;

// ── Wave D: the SQL-backed offline library search + the recent_surfaces pin reason ───────────────────────────────────
// What is pinned here:
//   (a) route → (uri, kind) classification for `recent_surfaces` (and the routes that must NOT be recorded),
//   (b) a saved entity that is on DISK but not resident is findable offline — the Wave-B "only the warm head-set is
//       searchable" regression is gone — with the highlight offsets the UI draws,
//   (c) the inclusion cascade (artist matched → all its albums → all their tracks) survives the artist thin split,
//       which means it now runs over `entity_refs` + `entity.album_uri` rather than a resident object graph,
//   (d) OrdinalIgnoreCase semantics survive the SQL path — including the non-ASCII folds SQLite's NOCASE cannot do,
//   (e) ranking parity: the same corpus searched with and without the SQL candidates ranks identically,
//   (f) the offline QueryTracks fallback sees cold playlist membership,
//   (g) WarmComplete is the gate InitialHydrate can safely wait on (it completes even when the warm pass fails).
public class LibrarySearchColdTests
{
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-search-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    static readonly ArtistRef MjRef = new("mj", "spotify:artist:mj", "Michael Jackson");

    static Track Trk(string id, string title, string albumUri, string albumName) =>
        new(id, "spotify:track:" + id, title, [MjRef], new AlbumRef("", albumUri, albumName), 200_000, false, null);

    static Album Alb(string uri, string name, int year, params Track[] tracks) =>
        new("id" + uri, uri, name, null, [MjRef], year, tracks.Length, tracks, Hydration: AlbumHydrationLevel.Tracks);

    // One saved artist ▸ two albums ▸ three tracks, persisted through the REAL pin gate (saved artist ⇒ its albums are
    // pin-reachable ⇒ their tracks are). Returns the album/track shapes so callers can assert against them.
    static (Album Thriller, Album Bad) Seed(CachedStore store)
    {
        var thriller = Alb("spotify:album:thriller", "Thriller", 1982,
            Trk("bj", "Billie Jean", "spotify:album:thriller", "Thriller"),
            Trk("bi", "Beat It", "spotify:album:thriller", "Thriller"));
        var bad = Alb("spotify:album:bad", "Bad", 1987,
            Trk("smooth", "Smooth Criminal", "spotify:album:bad", "Bad"));

        store.SetSaved("artists", MjRef.Uri, true, SyncState.Confirmed);
        store.UpsertArtist(new Artist("mj", MjRef.Uri, "Michael Jackson", null, TopAlbums: [thriller, bad]));
        store.UpsertAlbum(thriller);
        store.UpsertAlbum(bad);
        foreach (var t in thriller.Tracks!) store.UpsertTrack(t);
        foreach (var t in bad.Tracks!) store.UpsertTrack(t);
        store.Flush();
        return (thriller, bad);
    }

    // ── (a) route classification ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("album:spotify:album:x", "spotify:album:x", EntityKind.Album)]
    [InlineData("pl:spotify:playlist:x", "spotify:playlist:x", EntityKind.Playlist)]
    [InlineData("artist:spotify:artist:x", "spotify:artist:x", EntityKind.Artist)]
    [InlineData("show:spotify:show:x", "spotify:show:x", EntityKind.Show)]
    public void RecentSurfaceRoute_ClassifiesEveryDetailSurface(string route, string uri, EntityKind kind)
    {
        Assert.True(RecentSurfaceRoute.TryClassify(route, out var got, out var gotKind));
        Assert.Equal(uri, got);
        Assert.Equal(kind, gotKind);
    }

    // `liked` is a SET (pinned by collection_items itself) and `local` resolves to a synthetic uri no pin table holds —
    // recording either would burn one of the 50 LRU slots on a row that can never be read back.
    [Theory]
    [InlineData("home")]
    [InlineData("liked")]
    [InlineData("local")]
    [InlineData("search")]
    [InlineData("settings")]
    [InlineData("history")]
    [InlineData("album:")]     // prefix with no uri
    [InlineData("")]
    [InlineData(null)]
    public void RecentSurfaceRoute_IgnoresEverythingThatPinsNothing(string? route)
    {
        Assert.False(RecentSurfaceRoute.TryClassify(route, out var uri, out var kind));
        Assert.Equal("", uri);
        Assert.Equal(EntityKind.Unknown, kind);
    }

    [Fact]
    public void RecordRecentSurface_MirrorsSynchronously_AndPersistsOnTheLane()
    {
        var path = TempDb();
        try
        {
            using (var store = new CachedStore(new SqliteColdStore(path)))
            {
                store.UpsertAlbum(Alb("spotify:album:solo", "Solo", 2001));   // unpinned ⇒ hot-only …
                store.RecordRecentSurface("spotify:album:solo", (int)EntityKind.Album);
                store.Flush();
                // … until the open pins it: the mirror update is synchronous, so the flush lane finds the record and the
                // gate lets every later write through too.
                store.UpsertAlbum(Alb("spotify:album:solo", "Solo", 2001));
                store.Flush();
            }
            using var cold = new SqliteColdStore(path);
            var recent = cold.LoadRecentSurfaces();
            Assert.Equal("spotify:album:solo", Assert.Single(recent).Uri);
            Assert.NotNull(cold.GetEntity("spotify:album:solo"));
        }
        finally { TryDelete(path); }
    }

    // ── (b)(c) the cold-only corpus ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ColdOnlyTrack_IsFoundOffline_WithHighlightOffsets()
    {
        var path = TempDb();
        try
        {
            using (var seed = new CachedStore(new SqliteColdStore(path))) Seed(seed);

            // A FRESH store: the hot tier starts empty and the warm pass only replays the pin HEAD-SET (the saved
            // artist), so the album and its tracks are cold-only — exactly the Wave-B regression case.
            using var store = new CachedStore(new SqliteColdStore(path));
            await store.WarmComplete;

            var r = LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, "billie");
            var artist = Assert.Single(r.Artists);
            Assert.Equal(0, artist.MatchLen);                                  // surfaced through a child, not its name
            Assert.Equal(LibraryMatchKind.Track, artist.Match.Kind);
            var album = Assert.Single(artist.Albums);
            Assert.Equal("spotify:album:thriller", album.Uri);
            Assert.Equal(1982, album.Year);                                    // the cold album row still carries Year
            var track = Assert.Single(album.Tracks);
            Assert.Equal("spotify:track:bj", track.Uri);
            Assert.Equal(0, track.MatchStart);
            Assert.Equal(6, track.MatchLen);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task ColdOnlyCascade_ArtistNameMatch_ReturnsEveryAlbumAndTrack()
    {
        var path = TempDb();
        try
        {
            using (var seed = new CachedStore(new SqliteColdStore(path))) Seed(seed);
            using var store = new CachedStore(new SqliteColdStore(path));
            await store.WarmComplete;

            var r = LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, "michael");
            var artist = Assert.Single(r.Artists);
            Assert.True(artist.MatchLen > 0);
            Assert.Equal(2, artist.Albums.Count);                      // artist matched → ALL albums (browse)
            Assert.Equal("spotify:album:bad", artist.Albums[0].Uri);   // year desc
            Assert.Equal("spotify:album:thriller", artist.Albums[1].Uri);
            Assert.Single(artist.Albums[0].Tracks);                    // …and ALL their tracks, from the cold rows
            Assert.Equal(2, artist.Albums[1].Tracks.Count);
            foreach (var t in artist.Albums[1].Tracks) Assert.Equal(0, t.MatchLen);   // browse context → no highlight
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task ColdOnlyAlbumsScope_FindsSavedAlbumAndItsTracks()
    {
        var path = TempDb();
        try
        {
            using (var seed = new CachedStore(new SqliteColdStore(path)))
            {
                var thriller = Alb("spotify:album:thriller", "Thriller", 1982,
                    Trk("bj", "Billie Jean", "spotify:album:thriller", "Thriller"));
                seed.SetSaved("albums", thriller.Uri, true, SyncState.Confirmed);
                seed.UpsertAlbum(thriller);
                foreach (var t in thriller.Tracks!) seed.UpsertTrack(t);
                seed.Flush();
            }
            using var store = new CachedStore(new SqliteColdStore(path));
            await store.WarmComplete;

            var r = LibrarySearchIndex.Run(store, LibrarySearchScope.Albums, "jean");
            var album = Assert.Single(r.Albums);
            Assert.Equal(0, album.MatchLen);                                   // the album name did not match …
            Assert.Equal(LibraryMatchKind.Track, album.Match.Kind);            // … a track title did
            Assert.Equal("spotify:track:bj", Assert.Single(album.Tracks).Uri);
        }
        finally { TryDelete(path); }
    }

    // ── (d) OrdinalIgnoreCase survives the SQL path ──────────────────────────────────────────────────────────────────
    // SQLite's NOCASE folds ASCII only: a `LIKE` pre-filter would have DROPPED this row. The corpus query therefore does
    // no matching at all — it only bounds the scope.
    [Fact]
    public async Task NonAsciiQuery_StillMatches_ThroughTheSqlPath()
    {
        var path = TempDb();
        try
        {
            using (var seed = new CachedStore(new SqliteColdStore(path)))
            {
                var album = Alb("spotify:album:greek", "Greek", 1999,
                    Trk("om", "ωmega Point", "spotify:album:greek", "Greek"));
                seed.SetSaved("albums", album.Uri, true, SyncState.Confirmed);
                seed.UpsertAlbum(album);
                foreach (var t in album.Tracks!) seed.UpsertTrack(t);
                seed.Flush();
            }
            using var store = new CachedStore(new SqliteColdStore(path));
            await store.WarmComplete;

            var r = LibrarySearchIndex.Run(store, LibrarySearchScope.Albums, "Ω");   // capital omega
            var hit = Assert.Single(Assert.Single(r.Albums).Tracks);
            Assert.Equal("spotify:track:om", hit.Uri);
            Assert.Equal(0, hit.MatchStart);
            Assert.Equal(1, hit.MatchLen);
        }
        finally { TryDelete(path); }
    }

    // ── (e) ranking parity ───────────────────────────────────────────────────────────────────────────────────────────
    // The same store, searched WITH the SQL candidates and WITHOUT them (the pre-Wave-D resident walk). A fully resident
    // library is the same corpus both ways, so every group — and its order — must be identical.
    [Fact]
    public void RankingParity_SqlPathMatchesTheResidentWalk()
    {
        var path = TempDb();
        try
        {
            using var store = new CachedStore(new SqliteColdStore(path));
            Seed(store);
            // A second saved artist so the artist-level ranking (name-match first, then name tiebreak) actually sorts.
            var abba = new ArtistRef("ab", "spotify:artist:ab", "ABBA");
            var arrival = new Album("ida", "spotify:album:arrival", "Arrival", null, [abba], 1976, 1,
                [new Track("dq", "spotify:track:dq", "Dancing Queen", [abba], new AlbumRef("", "spotify:album:arrival", "Arrival"), 1000, false, null)],
                Hydration: AlbumHydrationLevel.Tracks);
            store.SetSaved("artists", abba.Uri, true, SyncState.Confirmed);
            store.UpsertArtist(new Artist("ab", abba.Uri, "ABBA", null, TopAlbums: [arrival]));
            store.UpsertAlbum(arrival);
            foreach (var t in arrival.Tracks!) store.UpsertTrack(t);
            store.Flush();

            foreach (var q in new[] { "a", "an", "i", "the" })
            {
                var sql = LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, q);
                var mem = LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, q, null);
                Assert.Equal(Shape(mem), Shape(sql));
            }
        }
        finally { TryDelete(path); }

        static string Shape(LibrarySearchResults r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var a in r.Artists)
            {
                sb.Append(a.Uri).Append('/').Append(a.MatchStart).Append(':').Append(a.MatchLen).Append('|');
                foreach (var al in a.Albums)
                {
                    sb.Append(' ').Append(al.Uri).Append('/').Append(al.Year).Append('/').Append(al.MatchStart).Append(':').Append(al.MatchLen).Append('|');
                    foreach (var t in al.Tracks) sb.Append("  ").Append(t.Uri).Append('/').Append(t.AlbumIndex).Append('/').Append(t.MatchStart).Append(':').Append(t.MatchLen).Append('|');
                }
            }
            return sb.ToString();
        }
    }

    // Stage O5 raised the stakes on (e): the SQL leg no longer just SOURCES the same corpus, it now DECIDES what to
    // hydrate from the thin rows alone (match thin first, hydrate only survivors), while the null-candidates leg is the
    // pre-O5 "hydrate everything, then match" walk verbatim. So the same equality is now the regression gate for the
    // whole restructure — including the "why" captions, the per-album track sets and the cross-artist ordering that the
    // thin pre-filter could plausibly have changed. Richer fixture: three artists, five albums, seven tracks, and
    // queries that hit at every level (artist name / album name / track title / nothing).
    [Fact]
    public void ThinFirstParity_MatchesTheHydrateEverythingWalk()
    {
        var path = TempDb();
        try
        {
            using var store = new CachedStore(new SqliteColdStore(path));
            Seed(store);   // Michael Jackson ▸ Thriller(1982: Billie Jean, Beat It) + Bad(1987: Smooth Criminal)

            var abba = new ArtistRef("ab", "spotify:artist:ab", "ABBA");
            var arrival = AlbumOf(abba, "spotify:album:arrival", "Arrival", 1976,
                ("dq", "Dancing Queen"), ("mm", "Money, Money, Money"));
            var voulez = AlbumOf(abba, "spotify:album:voulez", "Voulez-Vous", 1979, ("cm", "Chiquitita"));
            Save(store, abba, "ABBA", arrival, voulez);

            var queen = new ArtistRef("qn", "spotify:artist:qn", "Queen");
            var opera = AlbumOf(queen, "spotify:album:opera", "A Night at the Opera", 1975, ("br", "Bohemian Rhapsody"));
            Save(store, queen, "Queen", opera);
            store.Flush();

            // artist-name hits, album-name hits, track-title hits, a hit at every level at once, and misses.
            foreach (var q in new[] { "a", "e", "que", "queen", "arrival", "bohemian", "money", "night", "o", "zzz" })
            {
                var thinFirst = Reasons(LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, q));
                var hydrateAll = Reasons(LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, q, null));
                Assert.True(thinFirst == hydrateAll, $"query '{q}'\n  thin-first : {thinFirst}\n  hydrate-all: {hydrateAll}");
            }
        }
        finally { TryDelete(path); }

        static Album AlbumOf(ArtistRef artist, string uri, string name, int year, params (string Id, string Title)[] tracks)
        {
            var rows = new List<Track>(tracks.Length);
            foreach (var t in tracks)
                rows.Add(new Track(t.Id, "spotify:track:" + t.Id, t.Title, [artist], new AlbumRef("", uri, name), 1000, false, null));
            return new Album("id" + uri, uri, name, null, [artist], year, rows.Count, rows, Hydration: AlbumHydrationLevel.Tracks);
        }

        static void Save(CachedStore store, ArtistRef artist, string name, params Album[] albums)
        {
            store.SetSaved("artists", artist.Uri, true, SyncState.Confirmed);
            store.UpsertArtist(new Artist(artist.Id, artist.Uri, name, null, TopAlbums: albums));
            foreach (var a in albums)
            {
                store.UpsertAlbum(a);
                foreach (var t in a.Tracks!) store.UpsertTrack(t);
            }
        }

        // The Shape() above plus the MATCH-REASON hierarchy (artist ▸ album ▸ track), which is what the thin pre-filter
        // is most likely to disturb: dropping one child changes which child gets attributed as the "why".
        static string Reasons(LibrarySearchResults r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var a in r.Artists)
            {
                sb.Append(a.Uri).Append('/').Append(a.Name).Append('/').Append(a.MatchStart).Append(':').Append(a.MatchLen)
                  .Append('/').Append(a.Match.Kind).Append('=').Append(a.Match.Term).Append('|');
                foreach (var al in a.Albums)
                {
                    sb.Append(' ').Append(al.Uri).Append('/').Append(al.Year).Append('/').Append(al.MatchStart).Append(':').Append(al.MatchLen)
                      .Append('/').Append(al.Match.Kind).Append('=').Append(al.Match.Term).Append('|');
                    foreach (var t in al.Tracks)
                        sb.Append("  ").Append(t.Uri).Append('/').Append(t.AlbumIndex).Append('/').Append(t.MatchStart).Append(':').Append(t.MatchLen).Append('|');
                }
            }
            return sb.ToString();
        }
    }

    // ── (f) the offline QueryTracks fallback ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OfflineTrackSearch_SeesColdPlaylistMembership()
    {
        var path = TempDb();
        try
        {
            using (var seed = new CachedStore(new SqliteColdStore(path)))
            {
                var track = Trk("bj", "Billie Jean", "spotify:album:thriller", "Thriller");
                seed.SetMembership("spotify:playlist:p1", [new PlaylistMember("i0", track.Uri, null, 0)], null);
                seed.UpsertTrack(track);
                seed.Flush();
            }
            using var store = new CachedStore(new SqliteColdStore(path));
            await store.WarmComplete;

            Assert.Empty(store.QueryTracks("billie"));                       // the hot tier has never seen it …
            var hits = LibraryTrackSearch.Search(store, "billie");
            Assert.Equal("spotify:track:bj", Assert.Single(hits).Uri);       // … the cold membership corpus has

            // Artist-name matching (the ", "-joined subtitle, split back apart) works the same way.
            Assert.Equal("spotify:track:bj", Assert.Single(LibraryTrackSearch.Search(store, "jackson")).Uri);
            Assert.Empty(LibraryTrackSearch.Search(store, "nobody"));
        }
        finally { TryDelete(path); }
    }

    // ── (g) the WarmComplete gate InitialHydrate waits on ────────────────────────────────────────────────────────────

    sealed class GatedCold : IColdStore
    {
        public readonly ManualResetEventSlim Release = new(false);
        public readonly bool Throw;
        public GatedCold(bool @throw = false) { Throw = @throw; }
        public IEnumerable<ColdEntity> LoadAllEntities() => Array.Empty<ColdEntity>();
        public IEnumerable<ColdEntity> LoadEntities(IReadOnlyCollection<string> uris)
        {
            Release.Wait(TimeSpan.FromSeconds(10));
            if (Throw) throw new InvalidOperationException("warm exploded");
            return Array.Empty<ColdEntity>();
        }
        public IEnumerable<ColdSaved> LoadAllSaved() => new[] { new ColdSaved("artists", "spotify:artist:mj", SyncState.Confirmed) };
        public void UpsertEntity(string uri, EntityKind kind, byte[] payload) { }
        public void UpsertSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs = 0) { }
        public IEnumerable<ColdVideoAssoc> LoadAllVideoAssociations() => Array.Empty<ColdVideoAssoc>();
        public void UpsertVideoAssociation(string uri, byte[] payload) { }
        public string? GetCollectionRevision(string setId) => null;
        public void SetCollectionRevision(string setId, string? revision, long syncedAt) { }
        public byte[]? GetRootlistRevision() => null;
        public void SetRootlistRevision(byte[]? rev) { }
        public IReadOnlyList<ColdPlaylistItem> LoadMembership(string playlistUri) => Array.Empty<ColdPlaylistItem>();
        public void ReplaceMembership(string playlistUri, IReadOnlyList<ColdPlaylistItem> rows, byte[]? baseRev) { }
        public byte[]? GetPlaylistRevision(string playlistUri) => null;
        public IReadOnlyList<ColdRootlistEntry> LoadRootlist() => Array.Empty<ColdRootlistEntry>();
        public void ReplaceRootlist(IReadOnlyList<ColdRootlistEntry> entries) { }
        public void Flush() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task InitialHydrate_WaitsForWarmComplete()
    {
        var cold = new GatedCold();
        using var store = new CachedStore(cold);
        var order = new List<string>();

        // The LiveSessionHost shape (Addendum A7): DrainWrites goes out immediately, InitialHydrate rides WarmComplete.
        order.Add("drain");
        var gated = Task.Run(async () => { await store.WarmComplete; lock (order) order.Add("hydrate"); });

        await Task.Delay(50);
        lock (order) Assert.Equal(new[] { "drain" }, order);   // still parked on the warm pass
        Assert.False(store.WarmComplete.IsCompleted);

        cold.Release.Set();
        await gated.WaitAsync(TimeSpan.FromSeconds(10));
        lock (order) Assert.Equal(new[] { "drain", "hydrate" }, order);
    }

    // The gate must never wedge the library: a FAILED warm still completes WarmComplete (Wave B's try/finally), because
    // every miss is served by the unconditional cold fallback anyway.
    [Fact]
    public async Task WarmComplete_CompletesEvenWhenTheWarmPassFails()
    {
        var cold = new GatedCold(@throw: true);
        using var store = new CachedStore(cold);
        cold.Release.Set();
        await store.WarmComplete.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(store.WarmComplete.IsCompletedSuccessfully);
    }
}
