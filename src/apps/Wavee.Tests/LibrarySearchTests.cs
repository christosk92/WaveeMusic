// ── Wavee.Tests/LibrarySearchTests.cs — the cache-only hierarchical library search (Entities/User.cs §10) ─────────────
//
// A port of 0.2.9's LibrarySearchTests (LibrarySearchIndex: artist ▸ matching albums ▸ matching tracks). The decisions
// are unchanged; the library is 0.3's — rows in the scope's tables and the relations as edges (FollowedArtists /
// SavedAlbums off the account row, ArtistAlbums, AlbumTracks) instead of an InMemoryStore — and a hit is a SLOT with
// ranges into the result's flat runs instead of a record tree.

using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class LibrarySearchTests
{
    public LibrarySearchTests()
    {
        TestScope.Fresh();
        Entities.Current.MeSlot = Entities.Current.Users.Slot("spotify:user:library-search".AsSpan());
    }

    static int Me => Entities.Current.MeSlot;
    static Edges E => Entities.Current.Edges;

    static int ArtistRow(string key, string name)
    {
        var t = Entities.Current.Artists;
        int slot = t.Slot(("spotify:artist:" + key).AsSpan());
        t.SetText(ref t.Name, slot, Entities.Strings.Intern(name));
        return slot;
    }

    static int AlbumRow(string key, string title, int year = 1982)
    {
        var t = Entities.Current.Albums;
        int slot = t.Slot(("spotify:album:" + key).AsSpan());
        t.SetText(ref t.Title, slot, Entities.Strings.Intern(title));
        t.Year[slot] = (ushort)year;
        t.Known[slot] |= (uint)AlbumFields.Year;
        return slot;
    }

    static int TrackRow(string key, string title)
    {
        var t = Entities.Current.Tracks;
        int slot = t.Slot(("spotify:track:" + key).AsSpan());
        t.SetText(ref t.Title, slot, Entities.Strings.Intern(title));
        return slot;
    }

    record struct ArtistLibrary(int Mj, int Thriller, int Bad, int BillieJean, int BeatIt, int Smooth);

    static ArtistLibrary SeedArtistLibrary()
    {
        int mj = ArtistRow("mj", "Michael Jackson");
        int thriller = AlbumRow("thriller", "Thriller");
        int bad = AlbumRow("bad", "Bad");
        int bj = TrackRow("bj", "Billie Jean"), bi = TrackRow("bi", "Beat It"), smooth = TrackRow("smooth", "Smooth Criminal");
        E.AlbumTracks.ReplaceRun(thriller, [bj, bi], default);
        E.AlbumTracks.ReplaceRun(bad, [smooth], default);
        E.ArtistAlbums.ReplaceRun(mj, [thriller, bad], default);
        E.FollowedArtists.ReplaceRun(Me, [mj], default);

        // Queen is resident but NOT followed → must never surface in an artists-scope search.
        int queen = ArtistRow("q", "Queen");
        int opera = AlbumRow("opera", "A Night at the Opera");
        int bohemian = TrackRow("bohemian", "Bohemian Rhapsody");
        E.AlbumTracks.ReplaceRun(opera, [bohemian], default);
        E.ArtistAlbums.ReplaceRun(queen, [opera], default);

        return new ArtistLibrary(mj, thriller, bad, bj, bi, smooth);
    }

    static LibraryHits Run(LibrarySearchScope scope, string query) => User.SearchLibrary(scope, query);

    [Fact]
    public void ArtistNameMatch_HighlightsArtist_AndShowsAllAlbums()
    {
        var lib = SeedArtistLibrary();
        var r = Run(LibrarySearchScope.Artists, "michael");
        Assert.Equal(1, r.Artists.Length);
        var a = r.Artists[0];
        Assert.Equal(lib.Mj, a.Slot);
        Assert.True(a.MatchLen > 0);                          // the artist name itself matched → highlighted
        Assert.Equal(2, r.AlbumsOf(a).Length);                // artist matched → ALL albums shown (browse the artist)
        Assert.Equal(LibraryMatchKind.None, a.Match.Kind);    // name hit → no "why" caption (highlight is self-evident)
    }

    [Fact]
    public void AlbumNameMatch_SurfacesArtist_HighlightsAlbum_ShowsAllItsTracks()
    {
        var lib = SeedArtistLibrary();
        var r = Run(LibrarySearchScope.Artists, "thriller");
        Assert.Equal(1, r.Artists.Length);
        var a = r.Artists[0];
        Assert.Equal(0, a.MatchLen);                          // artist present via its album, name not highlighted
        Assert.Equal(LibraryMatchKind.Album, a.Match.Kind);   // "why": surfaced through a name-matched album …
        Assert.Equal("Thriller", a.Match.Term);               // … and the caption quotes that album
        var albums = r.AlbumsOf(a);
        Assert.Equal(1, albums.Length);
        var al = albums[0];
        Assert.Equal(lib.Thriller, al.Slot);
        Assert.True(al.MatchLen > 0);                         // album name matched → highlighted
        Assert.Equal(2, r.TracksOf(al).Length);               // album matched → all its tracks shown
    }

    [Fact]
    public void TrackMatch_SurfacesArtistAndAlbum_ShowsOnlyMatchingTracks()
    {
        var lib = SeedArtistLibrary();
        var r = Run(LibrarySearchScope.Artists, "billie");
        Assert.Equal(1, r.Artists.Length);
        var a = r.Artists[0];
        Assert.Equal(LibraryMatchKind.Track, a.Match.Kind);   // "why": surfaced through a title-matched track …
        Assert.Equal("Billie Jean", a.Match.Term);            // … and the caption quotes that track
        var albums = r.AlbumsOf(a);
        Assert.Equal(1, albums.Length);                       // only Thriller (contains the match), not Bad
        var al = albums[0];
        Assert.Equal(lib.Thriller, al.Slot);
        Assert.Equal(0, al.MatchLen);                         // album present via a track, not highlighted
        var tracks = r.TracksOf(al);
        Assert.Equal(1, tracks.Length);                       // only the matching track, not the whole tracklist
        Assert.Equal(lib.BillieJean, tracks[0].Slot);
        Assert.True(tracks[0].MatchLen > 0);
    }

    [Fact]
    public void ExcludesUnfollowedArtistsContent()
    {
        SeedArtistLibrary();
        Assert.True(Run(LibrarySearchScope.Artists, "queen").IsEmpty);
        Assert.True(Run(LibrarySearchScope.Artists, "bohemian").IsEmpty);
        Assert.True(Run(LibrarySearchScope.Artists, "opera").IsEmpty);
    }

    [Fact]
    public void NoMatch_IsEmpty()
    {
        SeedArtistLibrary();
        Assert.True(Run(LibrarySearchScope.Artists, "zzzznope").IsEmpty);
    }

    [Fact]
    public void EmptyQuery_IsEmpty()
    {
        SeedArtistLibrary();
        Assert.True(Run(LibrarySearchScope.Artists, "   ").IsEmpty);
    }

    [Fact]
    public void AlbumsRankBeforeAlbumsMatchedOnlyByTrack()
    {
        int mj = ArtistRow("mj", "Michael Jackson");
        int thriller = AlbumRow("thriller", "Thriller");
        int bad = AlbumRow("bad", "Bad");
        E.AlbumTracks.ReplaceRun(thriller, [TrackRow("bj", "Billie Jean")], default);
        E.AlbumTracks.ReplaceRun(bad, [TrackRow("tr", "Thriller Reprise")], default);   // matches on TRACK only
        E.ArtistAlbums.ReplaceRun(mj, [bad, thriller], default);
        E.FollowedArtists.ReplaceRun(Me, [mj], default);

        var r = Run(LibrarySearchScope.Artists, "thriller");
        Assert.Equal(1, r.Artists.Length);
        var albums = r.AlbumsOf(r.Artists[0]);
        Assert.Equal(2, albums.Length);
        Assert.Equal(thriller, albums[0].Slot);               // name match ranks ahead of the track-only match
        Assert.Equal(bad, albums[1].Slot);
    }

    [Fact]
    public void AlbumScope_MatchesSavedAlbumsAndTheirTracks()
    {
        int thriller = AlbumRow("thriller", "Thriller");
        int bj = TrackRow("bj", "Billie Jean");
        E.AlbumTracks.ReplaceRun(thriller, [bj], default);
        E.SavedAlbums.ReplaceRun(Me, [thriller], default);

        var byName = Run(LibrarySearchScope.Albums, "thril");
        Assert.Equal(0, byName.Artists.Length);
        Assert.Equal(1, byName.Albums.Length);
        Assert.Equal(thriller, byName.Albums[0].Slot);
        Assert.Equal(LibraryMatchKind.None, byName.Albums[0].Match.Kind);   // album name hit → no caption

        var byTrack = Run(LibrarySearchScope.Albums, "billie");
        Assert.Equal(1, byTrack.Albums.Length);
        var al = byTrack.Albums[0];
        Assert.Equal(LibraryMatchKind.Track, al.Match.Kind);                // "why": album surfaced through a track match …
        Assert.Equal("Billie Jean", al.Match.Term);                         // … quoted in the caption
        var tracks = byTrack.TracksOf(al);
        Assert.Equal(1, tracks.Length);
        Assert.Equal(bj, tracks[0].Slot);
        Assert.Equal(0, tracks[0].AlbumIndex);
    }

    // ── 0.3 additions: the result is a reused buffer, and no account means no library ─────────────────────────────

    [Fact]
    public void AReusedResult_IsRewrittenInPlace_AndItsGenerationMoves()
    {
        SeedArtistLibrary();
        var hits = new LibraryHits();
        Assert.Same(hits, User.SearchLibrary(LibrarySearchScope.Artists, "michael", hits));
        uint first = hits.Generation;
        Assert.Equal(1, hits.Artists.Length);

        User.SearchLibrary(LibrarySearchScope.Artists, "zzzznope", hits);
        Assert.True(hits.IsEmpty);
        Assert.NotEqual(first, hits.Generation);               // the page's memo compares this value
        Assert.Equal("zzzznope", hits.Query);
    }

    [Fact]
    public void WithNoSignedInAccount_TheSearchIsEmpty()
    {
        SeedArtistLibrary();
        Entities.Current.MeSlot = Table.None;
        Assert.True(Run(LibrarySearchScope.Artists, "michael").IsEmpty);
    }
}
