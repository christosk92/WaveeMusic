using Xunit;

namespace Wavee.Tests;

// W4 — the Slot subtitle GRAMMAR, tested as pure data (SidebarSubtitleShape), never as rendered text: `Format` lives in
// Pane/SidebarPaneText.cs, which is engine-bound and deliberately NOT source-included here (pitfalls.md "Tests" — the
// rest of Pane/ is excluded). This file therefore pins exactly what SidebarSubtitleRules.cs and Data/SidebarRowGeometry.cs
// promise: the SHAPE a kind/detail pair resolves to, never the loc string it eventually becomes.
public sealed class SidebarSubtitleRulesTests
{
    // ── fixture (SidebarRowPlannerTests.Entry's shape, extended with the fields this rule table reads) ─────────────────

    static SidebarLibraryEntry Entry(SidebarEntryKind kind, string id = "e1", string name = "Entry", string creator = "",
        int childCount = 0, SidebarPlaylistFlavor flavor = SidebarPlaylistFlavor.None, bool isOwner = false,
        string firstArtistName = "")
        => new(Id: id, Kind: kind, Uri: "spotify:x:" + id, Name: name, Creator: creator, Cover: null, MosaicTiles: null,
            ChildCount: childCount, AddedAtMs: 0, SortStamp: 1, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: flavor)
        { IsOwner = isOwner, FirstArtistName = firstArtistName };

    // ── SidebarSubtitleShape itself ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_IsTheBareNoneNoneShape()
    {
        Assert.True(SidebarSubtitleShape.Empty.IsEmpty);
        Assert.Equal(SidebarSubtitleShape.Empty, SidebarSubtitleShape.Of(SidebarSubtitleKind.None));
        Assert.Equal(SidebarSubtitleShape.Empty, SidebarSubtitleShape.Of(SidebarSubtitleKind.None, ""));
    }

    [Fact]
    public void Of_WithEmptyText_CollapsesToTheBareKind_NeverADanglingSeparator()
    {
        var shape = SidebarSubtitleShape.Of(SidebarSubtitleKind.Album, "");
        Assert.Equal(SidebarSubtitleKind.Album, shape.Kind);
        Assert.Equal(SidebarSubtitleDetail.None, shape.Detail);
        Assert.False(shape.IsEmpty);   // a real kind with no detail is NOT the empty shape
    }

    [Fact]
    public void Of_WithText_CarriesTheKindAndTheText()
    {
        var shape = SidebarSubtitleShape.Of(SidebarSubtitleKind.Show, "Gimlet Media");
        Assert.Equal(SidebarSubtitleKind.Show, shape.Kind);
        Assert.Equal(SidebarSubtitleDetail.Text, shape.Detail);
        Assert.Equal("Gimlet Media", shape.Text);
    }

    [Fact]
    public void Songs_And_Items_CarryTheCount()
    {
        var songs = SidebarSubtitleShape.Songs(SidebarSubtitleKind.Playlist, 12);
        Assert.Equal(SidebarSubtitleDetail.SongCount, songs.Detail);
        Assert.Equal(12, songs.Count);

        var items = SidebarSubtitleShape.Items(SidebarSubtitleKind.Folder, 4);
        Assert.Equal(SidebarSubtitleDetail.ItemCount, items.Detail);
        Assert.Equal(4, items.Count);
    }

    // ── playlists — ShowsOwner and its four inputs ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Playlist_MyOwn_ShowsSongCount_NotOwner()
    {
        var mine = Entry(SidebarEntryKind.Playlist, creator: "Me", childCount: 30, isOwner: true);
        Assert.False(SidebarSubtitleRules.ShowsOwner(in mine));
        Assert.Equal(SidebarSubtitleShape.Songs(SidebarSubtitleKind.Playlist, 30), SidebarSubtitleRules.For(in mine));
    }

    [Fact]
    public void Playlist_BySpotify_ShowsItsOwner()
    {
        var spotify = Entry(SidebarEntryKind.Playlist, creator: "Spotify", childCount: 50,
            flavor: SidebarPlaylistFlavor.BySpotify, isOwner: false);
        Assert.True(SidebarSubtitleRules.ShowsOwner(in spotify));
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.Playlist, "Spotify"), SidebarSubtitleRules.For(in spotify));
    }

    [Fact]
    public void Playlist_SomeoneElses_UnknownFlavor_StillShowsOwner()
    {
        var unknown = Entry(SidebarEntryKind.Playlist, creator: "Alice", flavor: SidebarPlaylistFlavor.None, isOwner: false);
        Assert.True(SidebarSubtitleRules.ShowsOwner(in unknown));
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.Playlist, "Alice"), SidebarSubtitleRules.For(in unknown));
    }

    [Fact]
    public void Playlist_Collaborative_Mixed_ShowsOwner()
    {
        var mixed = Entry(SidebarEntryKind.Playlist, creator: "Various", flavor: SidebarPlaylistFlavor.Mixed, isOwner: false);
        Assert.True(SidebarSubtitleRules.ShowsOwner(in mixed));
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.Playlist, "Various"), SidebarSubtitleRules.For(in mixed));
    }

    [Fact]
    public void Playlist_NoOwnerName_FallsBackToSongCount_RatherThanADanglingOwnerLine()
    {
        var noOwner = Entry(SidebarEntryKind.Playlist, creator: "", childCount: 7, flavor: SidebarPlaylistFlavor.None, isOwner: false);
        Assert.False(SidebarSubtitleRules.ShowsOwner(in noOwner));
        Assert.Equal(SidebarSubtitleShape.Songs(SidebarSubtitleKind.Playlist, 7), SidebarSubtitleRules.For(in noOwner));
    }

    [Fact]
    public void Playlist_IsOwnerBeatsAStaleFlavor_ShowsSongCount()
    {
        // IsOwner is the authority — a projection that has not (yet) cleared a stale non-ByYou flavor must not show the
        // wrong owner line for the user's own playlist.
        var mineStaleFlavor = Entry(SidebarEntryKind.Playlist, creator: "SomeoneElse",
            flavor: SidebarPlaylistFlavor.BySpotify, isOwner: true, childCount: 9);
        Assert.False(SidebarSubtitleRules.ShowsOwner(in mineStaleFlavor));
        Assert.Equal(SidebarSubtitleShape.Songs(SidebarSubtitleKind.Playlist, 9), SidebarSubtitleRules.For(in mineStaleFlavor));
    }

    [Fact]
    public void ShowsOwner_IsFalseForANonPlaylist()
    {
        var album = Entry(SidebarEntryKind.Album, creator: "Alice", isOwner: false, flavor: SidebarPlaylistFlavor.None);
        Assert.False(SidebarSubtitleRules.ShowsOwner(in album));
    }

    // ── albums — FirstArtistName, then Creator, then bare kind ──────────────────────────────────────────────────────

    [Fact]
    public void Album_PrefersFirstArtistName()
    {
        var libraryAlbum = Entry(SidebarEntryKind.Album, creator: "Various Artists", firstArtistName: "Daft Punk");
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.Album, "Daft Punk"), SidebarSubtitleRules.For(in libraryAlbum));
    }

    [Fact]
    public void Album_FallsBackToCreator_WhenNoFirstArtistName()
    {
        // A FEED album (a new release) carries its one credited name in Creator only.
        var feedAlbum = Entry(SidebarEntryKind.Album, creator: "Daft Punk", firstArtistName: "");
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.Album, "Daft Punk"), SidebarSubtitleRules.For(in feedAlbum));
    }

    [Fact]
    public void Album_WithNeitherArtistNorCreator_IsTheBareKind()
    {
        var bare = Entry(SidebarEntryKind.Album, creator: "", firstArtistName: "");
        var shape = SidebarSubtitleRules.For(in bare);
        Assert.Equal(SidebarSubtitleKind.Album, shape.Kind);
        Assert.Equal(SidebarSubtitleDetail.None, shape.Detail);
    }

    // ── artist / show / folder ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Artist_IsAlwaysTheBareKind()
    {
        var artist = Entry(SidebarEntryKind.Artist, creator: "ignored");
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.Artist), SidebarSubtitleRules.For(in artist));
    }

    [Fact]
    public void Show_WithPublisher_CarriesIt()
    {
        var show = Entry(SidebarEntryKind.Show, creator: "Gimlet Media");   // Publisher aliases Creator for Show
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.Show, "Gimlet Media"), SidebarSubtitleRules.For(in show));
    }

    [Fact]
    public void Show_WithNoPublisher_IsTheBareKind()
    {
        var show = Entry(SidebarEntryKind.Show, creator: "");
        var shape = SidebarSubtitleRules.For(in show);
        Assert.Equal(SidebarSubtitleKind.Show, shape.Kind);
        Assert.Equal(SidebarSubtitleDetail.None, shape.Detail);
    }

    [Fact]
    public void Folder_CarriesItsChildCount()
    {
        var folder = Entry(SidebarEntryKind.Folder, childCount: 4);
        Assert.Equal(SidebarSubtitleShape.Items(SidebarSubtitleKind.Folder, 4), SidebarSubtitleRules.For(in folder));
    }

    // ── the "unknown header" arm (Required change A / problem 1) ────────────────────────────────────────────────────
    // A row whose NAME has not resolved yet must never print "· 0 songs"/"· 0 items" — that reads as a confirmed fact
    // ("this playlist has zero tracks") rather than as the loading state it actually is. Only once the name is known
    // does a genuine zero become a real count.

    [Fact]
    public void Playlist_UnknownHeader_ZeroCount_IsTheBareKind_NeverZeroSongs()
    {
        var unresolved = Entry(SidebarEntryKind.Playlist, name: "", creator: "", childCount: 0, isOwner: true);
        var shape = SidebarSubtitleRules.For(in unresolved);
        Assert.Equal(SidebarSubtitleKind.Playlist, shape.Kind);
        Assert.Equal(SidebarSubtitleDetail.None, shape.Detail);
    }

    [Fact]
    public void Playlist_ResolvedHeader_GenuineZeroSongs_StillShowsTheCount()
    {
        // The exact case the unknown arm must NOT swallow: a real playlist that really has 0 tracks.
        var resolved = Entry(SidebarEntryKind.Playlist, name: "Empty Playlist", creator: "", childCount: 0, isOwner: true);
        Assert.Equal(SidebarSubtitleShape.Songs(SidebarSubtitleKind.Playlist, 0), SidebarSubtitleRules.For(in resolved));
    }

    [Fact]
    public void Folder_UnknownHeader_ZeroCount_IsTheBareKind()
    {
        var unresolved = Entry(SidebarEntryKind.Folder, name: "", childCount: 0);
        var shape = SidebarSubtitleRules.For(in unresolved);
        Assert.Equal(SidebarSubtitleKind.Folder, shape.Kind);
        Assert.Equal(SidebarSubtitleDetail.None, shape.Detail);
    }

    [Fact]
    public void Folder_ResolvedHeader_GenuineZeroItems_StillShowsTheCount()
    {
        var resolved = Entry(SidebarEntryKind.Folder, name: "Empty Folder", childCount: 0);
        Assert.Equal(SidebarSubtitleShape.Items(SidebarSubtitleKind.Folder, 0), SidebarSubtitleRules.For(in resolved));
    }

    // ── track / app route ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Track_WithCreator_HasNoKindWord_JustTheCreator()
    {
        var track = Entry(SidebarEntryKind.Track, creator: "Daft Punk");
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.None, "Daft Punk"), SidebarSubtitleRules.For(in track));
    }

    [Fact]
    public void Track_WithNoCreator_IsEmpty()
    {
        var track = Entry(SidebarEntryKind.Track, creator: "");
        Assert.True(SidebarSubtitleRules.For(in track).IsEmpty);
    }

    [Fact]
    public void AppRoute_Liked_IsTheBarePlaylistKind_RegardlessOfCreator()
    {
        var liked = Entry(SidebarEntryKind.AppRoute, id: "liked", creator: "ignored");
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.Playlist), SidebarSubtitleRules.For(in liked));
    }

    [Fact]
    public void AppRoute_Concert_CarriesItsVenueAsCreator_WithNoKindWord()
    {
        var concert = Entry(SidebarEntryKind.AppRoute, id: "concert:123", creator: "Red Rocks Amphitheatre");
        Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.None, "Red Rocks Amphitheatre"),
            SidebarSubtitleRules.For(in concert));
    }

    [Fact]
    public void AppRoute_WithNoCreator_IsEmpty()
    {
        var route = Entry(SidebarEntryKind.AppRoute, id: "albums", creator: "");
        Assert.True(SidebarSubtitleRules.For(in route).IsEmpty);
    }

    // ── the Liked Songs SYSTEM ROW (SidebarPaneSlot.RouteRow, not a projected entry) ────────────────────────────────

    [Fact]
    public void ForRoute_Liked_WithACount_ShowsSongs()
        => Assert.Equal(SidebarSubtitleShape.Songs(SidebarSubtitleKind.Playlist, 123),
            SidebarSubtitleRules.ForRoute("liked", 123));

    [Fact]
    public void ForRoute_Liked_WithNoCountYet_IsTheBarePlaylistKind()
        => Assert.Equal(SidebarSubtitleShape.Of(SidebarSubtitleKind.Playlist), SidebarSubtitleRules.ForRoute("liked", null));

    [Theory]
    [InlineData("albums")]
    [InlineData("home")]
    [InlineData("")]
    [InlineData(null)]
    public void ForRoute_EveryOtherRoute_IsEmpty(string? routeKey)
        => Assert.True(SidebarSubtitleRules.ForRoute(routeKey, 999).IsEmpty);
}
