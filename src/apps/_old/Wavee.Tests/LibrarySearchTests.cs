using Wavee.Backend;
using Wavee.Backend.Library;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Offline, cache-only HIERARCHICAL library search (LibrarySearchIndex): artist ▸ matching albums ▸ matching tracks.
public class LibrarySearchTests
{
    static readonly ArtistRef MjRef = new("mj", "spotify:artist:mj", "Michael Jackson");
    static readonly ArtistRef OtherRef = new("q", "spotify:artist:q", "Queen");

    static Track Trk(string id, string title, ArtistRef artist, string albumUri, string albumName) =>
        new(id, "spotify:track:" + id, title, [artist], new AlbumRef("", albumUri, albumName), 200_000, false, null);

    static Album Alb(string uri, string name, ArtistRef artist, params Track[] tracks) =>
        new("id" + uri, uri, name, null, [artist], 1982, tracks.Length, tracks, Hydration: AlbumHydrationLevel.Tracks);

    static InMemoryStore SeedArtistLibrary()
    {
        var store = new InMemoryStore();

        var thriller = Alb("spotify:album:thriller", "Thriller", MjRef,
            Trk("bj", "Billie Jean", MjRef, "spotify:album:thriller", "Thriller"),
            Trk("bi", "Beat It", MjRef, "spotify:album:thriller", "Thriller"));
        var bad = Alb("spotify:album:bad", "Bad", MjRef,
            Trk("smooth", "Smooth Criminal", MjRef, "spotify:album:bad", "Bad"));
        store.UpsertAlbum(thriller);
        store.UpsertAlbum(bad);
        store.UpsertArtist(new Artist("mj", "spotify:artist:mj", "Michael Jackson", null, TopAlbums: [thriller, bad]));
        store.SetSaved("artists", "spotify:artist:mj", true, SyncState.Confirmed);

        // Queen is resident but NOT followed → must never surface in an artists-scope search.
        var queenAlbum = Alb("spotify:album:opera", "A Night at the Opera", OtherRef,
            Trk("bohemian", "Bohemian Rhapsody", OtherRef, "spotify:album:opera", "A Night at the Opera"));
        store.UpsertAlbum(queenAlbum);
        store.UpsertArtist(new Artist("q", "spotify:artist:q", "Queen", null, TopAlbums: [queenAlbum]));

        return store;
    }

    [Fact]
    public void ArtistNameMatch_HighlightsArtist_AndShowsAllAlbums()
    {
        var r = LibrarySearchIndex.Run(SeedArtistLibrary(), LibrarySearchScope.Artists, "michael");
        var a = Assert.Single(r.Artists);
        Assert.Equal("spotify:artist:mj", a.Uri);
        Assert.True(a.MatchLen > 0);                 // the artist name itself matched → highlighted
        Assert.Equal(2, a.Albums.Count);             // artist matched → ALL albums shown (browse the artist)
        Assert.Equal(LibraryMatchKind.None, a.Match.Kind);   // name hit → no "why" caption (highlight is self-evident)
    }

    [Fact]
    public void AlbumNameMatch_SurfacesArtist_HighlightsAlbum_ShowsAllItsTracks()
    {
        var r = LibrarySearchIndex.Run(SeedArtistLibrary(), LibrarySearchScope.Artists, "thriller");
        var a = Assert.Single(r.Artists);
        Assert.Equal(0, a.MatchLen);                 // artist present via its album, name not highlighted
        Assert.Equal(LibraryMatchKind.Album, a.Match.Kind);  // "why": surfaced through a name-matched album …
        Assert.Equal("Thriller", a.Match.Term);              // … and the caption quotes that album
        var al = Assert.Single(a.Albums);
        Assert.Equal("Thriller", al.Name);
        Assert.True(al.MatchLen > 0);                // album name matched → highlighted
        Assert.Equal(2, al.Tracks.Count);            // album matched → all its tracks shown
    }

    [Fact]
    public void TrackMatch_SurfacesArtistAndAlbum_ShowsOnlyMatchingTracks()
    {
        var r = LibrarySearchIndex.Run(SeedArtistLibrary(), LibrarySearchScope.Artists, "billie");
        var a = Assert.Single(r.Artists);
        Assert.Equal(LibraryMatchKind.Track, a.Match.Kind);  // "why": surfaced through a title-matched track …
        Assert.Equal("Billie Jean", a.Match.Term);           // … and the caption quotes that track
        var al = Assert.Single(a.Albums);            // only Thriller (contains the match), not Bad
        Assert.Equal("Thriller", al.Name);
        Assert.Equal(0, al.MatchLen);                // album present via a track, not highlighted
        var t = Assert.Single(al.Tracks);            // only the matching track, not the whole tracklist
        Assert.Equal("Billie Jean", t.Title);
        Assert.True(t.MatchLen > 0);
    }

    [Fact]
    public void ExcludesUnfollowedArtistsContent()
    {
        var store = SeedArtistLibrary();
        Assert.True(LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, "queen").IsEmpty);
        Assert.True(LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, "bohemian").IsEmpty);
        Assert.True(LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, "opera").IsEmpty);
    }

    [Fact]
    public void NoMatch_IsEmpty()
        => Assert.True(LibrarySearchIndex.Run(SeedArtistLibrary(), LibrarySearchScope.Artists, "zzzznope").IsEmpty);

    [Fact]
    public void EmptyQuery_IsEmpty()
        => Assert.True(LibrarySearchIndex.Run(SeedArtistLibrary(), LibrarySearchScope.Artists, "   ").IsEmpty);

    [Fact]
    public void AlbumsRankBeforeAlbumsMatchedOnlyByTrack()
    {
        var store = new InMemoryStore();
        var thriller = Alb("spotify:album:thriller", "Thriller", MjRef,
            Trk("bj", "Billie Jean", MjRef, "spotify:album:thriller", "Thriller"));
        var bad = Alb("spotify:album:bad", "Bad", MjRef,
            Trk("tr", "Thriller Reprise", MjRef, "spotify:album:bad", "Bad"));   // matches on TRACK only
        store.UpsertAlbum(thriller);
        store.UpsertAlbum(bad);
        store.UpsertArtist(new Artist("mj", "spotify:artist:mj", "Michael Jackson", null, TopAlbums: [bad, thriller]));
        store.SetSaved("artists", "spotify:artist:mj", true, SyncState.Confirmed);

        var albums = Assert.Single(LibrarySearchIndex.Run(store, LibrarySearchScope.Artists, "thriller").Artists).Albums;
        Assert.Equal(2, albums.Count);
        Assert.Equal("Thriller", albums[0].Name);    // name match ranks ahead of the track-only match
        Assert.Equal("Bad", albums[1].Name);
    }

    [Fact]
    public void AlbumScope_MatchesSavedAlbumsAndTheirTracks()
    {
        var store = new InMemoryStore();
        var thriller = Alb("spotify:album:thriller", "Thriller", MjRef,
            Trk("bj", "Billie Jean", MjRef, "spotify:album:thriller", "Thriller"));
        store.UpsertAlbum(thriller);
        store.SetSaved("albums", "spotify:album:thriller", true, SyncState.Confirmed);

        var byName = LibrarySearchIndex.Run(store, LibrarySearchScope.Albums, "thril");
        Assert.Empty(byName.Artists);
        var byNameAl = Assert.Single(byName.Albums);
        Assert.Equal("Thriller", byNameAl.Name);
        Assert.Equal(LibraryMatchKind.None, byNameAl.Match.Kind);   // album name hit → no caption

        var byTrack = LibrarySearchIndex.Run(store, LibrarySearchScope.Albums, "billie");
        var al = Assert.Single(byTrack.Albums);
        Assert.Equal(LibraryMatchKind.Track, al.Match.Kind);        // "why": album surfaced through a track match …
        Assert.Equal("Billie Jean", al.Match.Term);                 // … quoted in the caption
        var t = Assert.Single(al.Tracks);
        Assert.Equal("Billie Jean", t.Title);
        Assert.Equal(0, t.AlbumIndex);
    }
}
