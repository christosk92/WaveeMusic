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

// ── stage O5: the COST contract of the offline library search ────────────────────────────────────────────────────────
// LibrarySearchTests / LibrarySearchColdTests pin WHAT the search returns. This file pins what it COSTS, which used to be
// "hydrate the entire library, per keystroke, uninterruptibly":
//   (a) cancellation is real — a superseded keystroke stops mid-walk instead of running to completion holding the cold
//       store's read lock,
//   (b) matching is THIN-FIRST — an entity the cold rows cannot match costs zero point-reads and zero deserializes,
//   (c) the candidate corpus is cached across keystrokes on StoreLibrarySource and invalidated by the store-change seam
//       (but NOT by the search's own hot-tier promotions, which would otherwise re-invalidate it every keystroke).
public class LibrarySearchPerfTests
{

    // The facade every StoreLibrarySource read goes through. Offline = store-only, never networks (design 1.3).
    static SwitchableEntityHydrator Offline(IStore store) => new(new Wavee.Backend.Hydration.OfflineEntityHydrator(store));
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-searchperf-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    static ArtistRef Ref(string id, string name) => new(id, "spotify:artist:" + id, name);

    static Track Trk(string id, string title, ArtistRef artist, string albumUri, string albumName) =>
        new(id, "spotify:track:" + id, title, [artist], new AlbumRef("", albumUri, albumName), 200_000, false, null);

    static Album Alb(string uri, string name, ArtistRef artist, int year, params Track[] tracks) =>
        new("id" + uri, uri, name, null, [artist], year, tracks.Length, tracks, Hydration: AlbumHydrationLevel.Tracks);

    // Three followed artists, one album + one track each. Only Michael Jackson's tree contains "billie".
    static void SeedThree(IStore store)
    {
        Add(store, Ref("mj", "Michael Jackson"), "spotify:album:thriller", "Thriller", 1982, "bj", "Billie Jean");
        Add(store, Ref("ab", "ABBA"), "spotify:album:arrival", "Arrival", 1976, "dq", "Dancing Queen");
        Add(store, Ref("q", "Queen"), "spotify:album:opera", "A Night at the Opera", 1975, "br", "Bohemian Rhapsody");
    }

    static void Add(IStore store, ArtistRef artist, string albumUri, string albumName, int year, string trackId, string trackTitle)
    {
        var album = Alb(albumUri, albumName, artist, year, Trk(trackId, trackTitle, artist, albumUri, albumName));
        store.SetSaved("artists", artist.Uri, true, SyncState.Confirmed);   // save FIRST — the cold write gate reads the pin
        store.UpsertArtist(new Artist(artist.Id, artist.Uri, artist.Name, null, TopAlbums: [album]));
        store.UpsertAlbum(album);
        foreach (var t in album.Tracks!) store.UpsertTrack(t);
    }

    // ── (a) cancellation ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreCancelledToken_AbortsBeforeAnyHydration()
    {
        var inner = new InMemoryStore();
        SeedThree(inner);
        var store = new CountingStore(inner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => { LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, "a", cts.Token); });
        Assert.Equal(0, store.ArtistReads);   // not one point-read was paid for a query that was already superseded
    }

    [Fact]
    public void CancellingMidWalk_StopsTheWalk_InsteadOfRunningToCompletion()
    {
        var inner = new InMemoryStore();
        SeedThree(inner);
        using var cts = new CancellationTokenSource();
        var store = new CountingStore(inner);
        store.OnArtistRead = () => cts.Cancel();   // the next keystroke lands while artist #1 is being built

        Assert.ThrowsAny<OperationCanceledException>(
            () => { LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, "a", cts.Token); });
        Assert.Equal(1, store.ArtistReads);   // it stopped inside the first artist — it did NOT walk the other two
    }

    [Fact]
    public async Task SearchLibraryAsync_PropagatesCancellationFromTheCaller()
    {
        var inner = new InMemoryStore();
        SeedThree(inner);
        using var src = new StoreLibrarySource(inner, Offline(inner), OfflineOnlineCatalog.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => src.SearchLibraryAsync("a", LibrarySearchScope.Artists, cts.Token));
    }

    // ── (b) thin-first: no hydration for entities the cold rows cannot match ─────────────────────────────────────────

    [Fact]
    public async Task ThinFirst_HydratesOnlyTheMatchingArtist()
    {
        var path = TempDb();
        try
        {
            using (var seed = new CachedStore(new SqliteColdStore(path))) { SeedThree(seed); seed.Flush(); }

            using var cold = new CachedStore(new SqliteColdStore(path));
            await cold.WarmComplete;
            var store = new CountingStore(cold);

            var r = LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, "billie", CancellationToken.None);

            Assert.Equal("spotify:artist:mj", Assert.Single(r.Artists).Uri);
            Assert.Equal(1, store.ArtistReads);      // ABBA and Queen were rejected from the THIN rows alone …
            Assert.True(store.AlbumReads <= 2,       // … and only the album that owns the matching track was opened
                $"expected the walk to open at most the matching album, opened {store.AlbumReads}");
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task ThinFirst_AlbumsScope_SkipsNonMatchingAlbums()
    {
        var path = TempDb();
        try
        {
            using (var seed = new CachedStore(new SqliteColdStore(path)))
            {
                foreach (var (uri, name, year, id, title) in new[]
                {
                    ("spotify:album:thriller", "Thriller", 1982, "bj", "Billie Jean"),
                    ("spotify:album:arrival", "Arrival", 1976, "dq", "Dancing Queen"),
                    ("spotify:album:opera", "A Night at the Opera", 1975, "br", "Bohemian Rhapsody"),
                })
                {
                    var artist = Ref("mj", "Michael Jackson");
                    var album = Alb(uri, name, artist, year, Trk(id, title, artist, uri, name));
                    seed.SetSaved("albums", uri, true, SyncState.Confirmed);
                    seed.UpsertAlbum(album);
                    foreach (var t in album.Tracks!) seed.UpsertTrack(t);
                }
                seed.Flush();
            }

            using var cold = new CachedStore(new SqliteColdStore(path));
            await cold.WarmComplete;
            var store = new CountingStore(cold);

            var r = LibrarySearchIndex.Run(store, LibrarySearchScope.Albums, "thril", CancellationToken.None);

            Assert.Equal("spotify:album:thriller", Assert.Single(r.Albums).Uri);
            Assert.Equal(1, store.AlbumReads);   // the other two never left their thin rows
        }
        finally { TryDelete(path); }
    }

    // ── (c) the corpus cache ─────────────────────────────────────────────────────────────────────────────────────────

    static ColdCandidates FakeCorpus() => new(
        Roots: [new ColdThinRow("spotify:artist:mj", EntityKind.Artist, "Michael Jackson", null, null, 0, 0, null)],
        Albums: [new ColdThinRow("spotify:album:thriller", EntityKind.Album, "Thriller", null, null, 0, 0, null)],
        Tracks: [new ColdThinRow("spotify:track:bj", EntityKind.Track, "Billie Jean", null, null, 0, 0, "spotify:album:thriller")],
        Edges: [new ColdRefEdge("spotify:artist:mj", "spotify:album:thriller")]);

    [Fact]
    public async Task Corpus_IsStreamedOncePerKeystrokeBurst_AndInvalidatedByTheStoreChangeSeam()
    {
        var inner = new InMemoryStore();
        SeedThree(inner);
        var store = new CountingStore(inner) { Candidates = FakeCorpus() };
        using var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        await src.SearchLibraryAsync("mic", LibrarySearchScope.Artists);
        await src.SearchLibraryAsync("mich", LibrarySearchScope.Artists);
        await src.SearchLibraryAsync("micha", LibrarySearchScope.Artists);
        Assert.Equal(1, store.CandidateLoads);   // one corpus, three keystrokes

        // The Albums scope is a DIFFERENT corpus — cached separately, so it costs its own single load.
        await src.SearchLibraryAsync("thri", LibrarySearchScope.Albums);
        Assert.Equal(2, store.CandidateLoads);

        // A library mutation lands on the very seam that already drives CollectionsChanged → the corpus is stale.
        inner.SetSaved("artists", "spotify:artist:new", true, SyncState.Confirmed);
        await src.SearchLibraryAsync("michae", LibrarySearchScope.Artists);
        Assert.Equal(3, store.CandidateLoads);
    }

    [Fact]
    public async Task Corpus_SurvivesTheSearchesOwnHydration()
    {
        var path = TempDb();
        try
        {
            using (var seed = new CachedStore(new SqliteColdStore(path))) { SeedThree(seed); seed.Flush(); }

            using var cold = new CachedStore(new SqliteColdStore(path));
            await cold.WarmComplete;   // the warm pass ends with a Bulk change — settle it BEFORE the source subscribes
            var store = new CountingStore(cold);
            using var src = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

            // This query hydrates: the albums are cold-only, so each survivor is promoted into the hot tier, and every
            // promotion raises a StoreChange. Without the thread-scoped suppression that would invalidate the corpus the
            // walk had just built — every keystroke would pay for the full three-statement reload again.
            var first = await src.SearchLibraryAsync("michael", LibrarySearchScope.Artists);
            Assert.NotEmpty(first.Artists);
            Assert.Equal(1, store.CandidateLoads);

            var second = await src.SearchLibraryAsync("michae", LibrarySearchScope.Artists);
            Assert.NotEmpty(second.Artists);
            Assert.Equal(1, store.CandidateLoads);
        }
        finally { TryDelete(path); }
    }
}

/// <summary>An <see cref="IStore"/> pass-through that COUNTS the reads the library search is supposed to have stopped
/// making, and can stand in for the cold candidate seam. Everything else forwards verbatim, so the store under test is
/// the real one (an <see cref="InMemoryStore"/> or a <see cref="CachedStore"/>) — never a mock of it.</summary>
public sealed class CountingStore : IStore, ILibraryCandidateStore
{
    readonly IStore _inner;
    public CountingStore(IStore inner) => _inner = inner;

    public int ArtistReads, AlbumReads, TrackReads, CandidateLoads;

    /// <summary>Fires INSIDE the first artist point-read — the hook the mid-walk cancellation test uses.</summary>
    public Action? OnArtistRead;

    /// <summary>When set, served instead of the inner store's candidates (an inner store that has none then still gets a
    /// corpus). Counted either way.</summary>
    public ColdCandidates? Candidates;

    public ColdCandidates LoadLibraryCandidates(ColdCandidateScope scope)
    {
        Interlocked.Increment(ref CandidateLoads);
        if (Candidates is { } c) return c;
        return _inner is ILibraryCandidateStore s ? s.LoadLibraryCandidates(scope) : ColdCandidates.Empty;
    }

    public Artist? GetArtist(string uri) { Interlocked.Increment(ref ArtistReads); OnArtistRead?.Invoke(); return _inner.GetArtist(uri); }
    public Album? GetAlbum(string uri) { Interlocked.Increment(ref AlbumReads); return _inner.GetAlbum(uri); }
    public Track? GetTrack(string uri) { Interlocked.Increment(ref TrackReads); return _inner.GetTrack(uri); }

    public void UpsertTrack(Track t) => _inner.UpsertTrack(t);
    public IReadOnlyList<Track> QueryTracks(string? text = null, TrackSort sort = TrackSort.None, int limit = 200) => _inner.QueryTracks(text, sort, limit);
    public void UpsertAlbum(Album a) => _inner.UpsertAlbum(a);
    public void UpsertArtist(Artist a) => _inner.UpsertArtist(a);
    public void UpsertPlaylist(Playlist p) => _inner.UpsertPlaylist(p);
    public Playlist? GetPlaylist(string uri) => _inner.GetPlaylist(uri);
    public void UpsertShow(Show s) => _inner.UpsertShow(s);
    public Show? GetShow(string uri) => _inner.GetShow(uri);
    public void UpsertEpisode(Episode e) => _inner.UpsertEpisode(e);
    public Episode? GetEpisode(string uri) => _inner.GetEpisode(uri);
    public void UpsertOwner(Owner o) => _inner.UpsertOwner(o);
    public Owner? GetOwner(string userUriOrId) => _inner.GetOwner(userUriOrId);
    public void UpsertVideoAssociation(VideoAssociation a) => _inner.UpsertVideoAssociation(a);
    public VideoAssociation? GetVideoAssociation(string uri) => _inner.GetVideoAssociation(uri);
    public void UpsertVideoOverride(VideoOverride o) => _inner.UpsertVideoOverride(o);
    public void RemoveVideoOverride(string uri) => _inner.RemoveVideoOverride(uri);
    public VideoOverride? GetVideoOverride(string uri) => _inner.GetVideoOverride(uri);
    public IReadOnlyList<VideoOverride> VideoOverrides() => _inner.VideoOverrides();
    public void SetSaved(string setId, string uri, bool saved, SyncState sync) => _inner.SetSaved(setId, uri, saved, sync);
    public void SetSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs) => _inner.SetSaved(setId, uri, saved, sync, addedAtMs);
    public bool IsSaved(string setId, string uri) => _inner.IsSaved(setId, uri);
    public IReadOnlyList<string> SavedUris(string setId) => _inner.SavedUris(setId);
    public IReadOnlyList<SavedItem> SavedItems(string setId) => _inner.SavedItems(setId);
    public void SetMembership(string playlistUri, IReadOnlyList<PlaylistMember> rows, byte[]? baseRev) => _inner.SetMembership(playlistUri, rows, baseRev);
    public bool HasMembership(string playlistUri) => _inner.HasMembership(playlistUri);
    public IReadOnlyList<PlaylistMember> Membership(string playlistUri) => _inner.Membership(playlistUri);
    public byte[]? PlaylistRevision(string playlistUri) => _inner.PlaylistRevision(playlistUri);
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries) => _inner.SetRootlist(entries);
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev) => _inner.SetRootlist(entries, rev);
    public byte[]? RootlistRevision() => _inner.RootlistRevision();
    public IReadOnlyList<RootlistEntry> Rootlist() => _inner.Rootlist();
    public long Version(string uri) => _inner.Version(uri);
    public void Bump(string uri, CollectionKind? kind = null) => _inner.Bump(uri, kind);
    public IObservable<StoreChange> Changes => _inner.Changes;
    public IDisposable BeginBulk() => _inner.BeginBulk();
}
