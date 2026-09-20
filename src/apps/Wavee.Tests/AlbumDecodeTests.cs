// ── Wavee.Tests/AlbumDecodeTests.cs — the album page's pathfinder folds (Spotify.Decode.Album.cs) ────────────────────
//
// NEW in 0.3 (gap register G-045 / G-232, the album half). 0.2.9 captured no album, merch or similar-albums answer as a
// fixture file; its projections were pinned by crafted JSON inline (`AlbumEnrichmentTests.cs`), and these facts reuse
// those documents VERBATIM as the input — the 0.2.9 assertions (skip the uri-less and the unnamed, exclude the album
// itself from its own more-by shelf) read back through the handles after a commit, never off a staged row.
// Every fact decodes, commits and publishes the way one UI drain does (TestScope.CommitAndPublish).

using System.Linq;
using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;
using Md = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class AlbumDecodeTests
{
    static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);
    static Album AlbumOf(string uri) => Entities.Album(EntityUri.Parse(uri));

    // The binary (`AlbumV4`, ext kind 9) half shares the gid shape of `DecodeTests.cs` / `AlbumArtistsBitTests.cs`.
    static byte[] Gid(byte seed)
    {
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return gid;
    }

    static ByteString Bs(byte[] bytes) => ByteString.CopyFrom(bytes);
    static Album AlbumOf(byte seed) => Entities.Album(EntityId.ForGid(EntityKind.Album, Gid(seed)));

    // ── similarAlbumsBasedOnThisTrack (AlbumEnrichmentTests.SimilarAlbumsFromTrack_*) ─────────────────────────────────

    const string SimilarJson = """
        { "data": { "seoRecommendedTrackAlbum": { "items": [
            { "data": { "uri": "spotify:album:A", "name": "Neon", "type": "SINGLE",
                        "date": { "year": 2022 },
                        "coverArt": { "sources": [ { "url": "https://cdn/a", "width": 300, "height": 300 } ] },
                        "artists": { "items": [ { "uri": "spotify:artist:X", "profile": { "name": "Aurora" } } ] } } },
            { "data": { "name": "no uri → skipped" } }
        ] } } }
        """;

    [Fact]
    public void SimilarAlbums_MapsItems_AndSkipsUriless_OnTheAlbumTheCallerNamed()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.SimilarAlbums(Json(SimilarJson), "spotify:album:page"u8, s);
        TestScope.CommitAndPublish(s);

        var page = AlbumOf("spotify:album:page");
        var similar = Entities.Current.Edges.AlbumSimilar;
        Assert.Equal(EdgeState.Complete, similar.State(page.Slot));
        var a = new Album(Assert.Single(similar.Targets(page.Slot).ToArray()));
        Assert.Equal("spotify:album:A", a.Uri.Text);
        Assert.Equal("Neon", a.Title);
        Assert.Equal(AlbumKind.Single, a.Kind);
        Assert.Equal((ushort)2022, a.Year);
        Assert.False(a.ImageId.IsEmpty);
        var artist = new Artist(Assert.Single(a.ArtistSlots.ToArray()));
        Assert.Equal("Aurora", artist.Name);
    }

    [Fact]
    public void SimilarAlbums_MissingPath_IsAnEmptyAnswer()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.SimilarAlbums(Json("""{ "data": {} }"""), "spotify:album:page"u8, s);
        TestScope.CommitAndPublish(s);

        var page = AlbumOf("spotify:album:page");
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumSimilar.State(page.Slot));
        Assert.Equal(0, Entities.Current.Edges.AlbumSimilar.Count(page.Slot));
    }

    // ── queryAlbumMerch (AlbumEnrichmentTests.AlbumMerch_MapsProducts_AndSkipsUnnamed) ───────────────────────────────

    [Fact]
    public void AlbumMerch_MapsProducts_AndSkipsUnnamed()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumMerch(Json("""
            { "data": { "albumUnion": { "merch": { "items": [
                { "nameV2": "Tour Tee", "price": "$25.00", "description": "100% cotton", "url": "https://shop/tee",
                  "image": { "sources": [ { "url": "https://cdn/tee", "width": 640, "height": 640 } ] } },
                { "name": "", "price": "$1" }
            ] } } } }
            """), "spotify:album:merch"u8, s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf("spotify:album:merch");
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumMerch.State(album.Slot));
        int listing = Assert.Single(album.MerchSlots.ToArray());
        ref var m = ref Album.MerchAt(listing);
        Assert.Equal("Tour Tee", Entities.Strings.Resolve(m.Name));
        Assert.Equal("$25.00", Entities.Strings.Resolve(m.Price));
        Assert.Equal("https://shop/tee", Entities.Strings.Resolve(m.ShopUrl));
        Assert.Equal("https://cdn/tee", Entities.Strings.Resolve(m.ImageId));
    }

    [Fact]
    public void AlbumMerch_NoPriceAndNoUrl_StayEmpty_TheBuyAndInertArms()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumMerch(Json("""
            { "data": { "albumUnion": { "merch": { "items": [ { "name": "Poster" } ] } } } }
            """), "spotify:album:bare"u8, s);
        TestScope.CommitAndPublish(s);

        ref var m = ref Album.MerchAt(Assert.Single(AlbumOf("spotify:album:bare").MerchSlots.ToArray()));
        Assert.Equal("Poster", Entities.Strings.Resolve(m.Name));
        Assert.True(m.Price.IsEmpty);      // the view reads `artist.buy`
        Assert.True(m.ShopUrl.IsEmpty);    // an inert listing, not a dead button
    }

    // ── getAlbum: more-by and other versions (AlbumEnrichmentTests.AlbumFromUnion_ProjectsTrackFlags_AndMoreByShelf) ──

    const string AlbumJson = """
        { "data": { "albumUnion": {
            "uri": "spotify:album:MAIN", "name": "Main Release", "type": "ALBUM",
            "date": { "isoString": "2021-05-01T00:00:00Z" },
            "coverArt": { "sources": [ { "url": "https://cdn/main", "width": 640, "height": 640 } ] },
            "artists": { "items": [ { "uri": "spotify:artist:LEAD", "profile": { "name": "Lead" } } ] },
            "tracksV2": { "totalCount": 1, "items": [ { "track": {
                "uri": "spotify:track:T1", "name": "Hit", "duration": { "totalMilliseconds": 210000 },
                "playcount": "98765", "contentRating": { "label": "EXPLICIT" },
                "playability": { "playable": true },
                "artists": { "items": [ { "uri": "spotify:artist:LEAD", "profile": { "name": "Lead" } } ] } } } ] },
            "moreAlbumsByArtist": { "items": [ { "discography": { "popularReleasesAlbums": { "items": [
                { "uri": "spotify:album:OTHER", "name": "Earlier", "type": "ALBUM", "date": { "year": 2019 },
                  "tracks": { "totalCount": 10 },
                  "coverArt": { "sources": [ { "url": "https://cdn/other", "width": 300, "height": 300 } ] } },
                { "uri": "spotify:album:MAIN", "name": "self → excluded", "type": "ALBUM" }
            ] } } } ] },
            "releases": { "items": [
                { "uri": "spotify:album:MAIN", "name": "self → excluded", "type": "ALBUM" },
                { "uri": "spotify:album:DELUXE", "name": "Main Release (Deluxe)", "type": "ALBUM", "date": { "year": 2022 } },
                { "uri": "spotify:album:DELUXE", "name": "duplicate → excluded", "type": "ALBUM" }
            ] }
        } } }
        """;

    [Fact]
    public void AlbumAnswer_FirstPage_LandsIdentity_Tracks_MoreBy_AndVersions()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json(AlbumJson), 0, s);
        TestScope.CommitAndPublish(s);

        var main = AlbumOf("spotify:album:MAIN");
        Assert.Equal("Main Release", main.Title);
        var t = new Track(Assert.Single(main.TrackSlots.ToArray()));
        Assert.True(t.IsExplicit);
        Assert.True(t.IsPlayable);
        Assert.Equal(98765u, t.PlayCount);

        // The reverse-order fact (G-231 follow-up): `artists` arrives BEFORE `tracksV2` here, the ordinary shape, and
        // must stay green — one billed artist, closed as AlbumArtists, never mixed into the tracklist.
        var billed = new Artist(Assert.Single(main.ArtistSlots.ToArray()));
        Assert.Equal("spotify:artist:LEAD", billed.Uri.Text);

        var more = new Album(Assert.Single(main.MoreBySlots.ToArray()));   // the self-reference is excluded
        Assert.Equal("spotify:album:OTHER", more.Uri.Text);
        Assert.Equal("Earlier", more.Title);

        var version = new Album(Assert.Single(main.VersionSlots.ToArray()));   // self and duplicate excluded
        Assert.Equal("spotify:album:DELUXE", version.Uri.Text);
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumVersions.State(main.Slot));
    }

    // ── getAlbum: `tracksV2` BEFORE `uri`/`artists` (G-231 follow-up) ──────────────────────────────────────────────────
    //
    // The field order in `AlbumJson` above (artists, then tracksV2) is the ordinary shape, but it is server-controlled
    // (a persisted GraphQL query) — nothing here guarantees it. When `tracksV2` lands FIRST, the album's billed artists
    // arrive AFTER the tracklist and get pushed onto the SAME pending stack, above the tracks mark. Before the fix,
    // `GetAlbum` only closed artists pushed BEFORE the tracks mark, so this trailing run rode along into
    // `ClosePage(AlbumTracks, …)` and painted as ghost, empty-title track rows (1 real track + 2 fake ones) while the
    // billed artists were lost entirely.

    const string AlbumOrderJson = """
        { "data": { "albumUnion": {
            "tracksV2": { "totalCount": 1, "items": [ { "track": {
                "uri": "spotify:track:ORD1", "name": "Only Song", "duration": { "totalMilliseconds": 180000 } } } ] },
            "uri": "spotify:album:ORDER", "name": "Order Test", "type": "ALBUM",
            "artists": { "items": [
                { "uri": "spotify:artist:ORD_A1", "profile": { "name": "Artist One" } },
                { "uri": "spotify:artist:ORD_A2", "profile": { "name": "Artist Two" } }
            ] }
        } } }
        """;

    [Fact]
    public void AlbumAnswer_TracksV2BeforeArtists_LandsOneTrack_NotThreeGhostRows()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json(AlbumOrderJson), 0, s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf("spotify:album:ORDER");
        var t = new Track(Assert.Single(album.TrackSlots.ToArray()));
        Assert.Equal("Only Song", t.Title);

        var artistUris = album.ArtistSlots.ToArray().Select(slot => new Artist(slot).Uri.Text).ToArray();
        Assert.Equal(2, artistUris.Length);
        Assert.Contains("spotify:artist:ORD_A1", artistUris);
        Assert.Contains("spotify:artist:ORD_A2", artistUris);
    }

    // ── getAlbum: `artists` BEFORE `tracksV2` BEFORE `uri` — the mirror of the order above (FIX 2) ───────────────────
    //
    // `AlbumOrderJson` above has `tracksV2` land first, so the leading-artist-run branch is a no-op there (nothing is
    // pending yet when `tracksV2` is hit). This fixture instead pushes the billed-artist run FIRST — while the
    // album's own `uri` is still empty, since `uri` is spelled last. Before the fix, `CloseArtists` popped (discarded)
    // that run on the spot because the uri was not yet known; the fix defers it and closes it once the uri lands.

    const string AlbumMirrorOrderJson = """
        { "data": { "albumUnion": {
            "artists": { "items": [
                { "uri": "spotify:artist:MIR_A1", "profile": { "name": "Mirror One" } },
                { "uri": "spotify:artist:MIR_A2", "profile": { "name": "Mirror Two" } }
            ] },
            "tracksV2": { "totalCount": 1, "items": [ { "track": {
                "uri": "spotify:track:MIR1", "name": "Mirror Song", "duration": { "totalMilliseconds": 200000 } } } ] },
            "uri": "spotify:album:MIRROR", "name": "Mirror Test", "type": "ALBUM"
        } } }
        """;

    [Fact]
    public void AlbumAnswer_ArtistsBeforeTracksV2BeforeUri_KeepsTheBilledArtists_AndLandsTheTrack()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json(AlbumMirrorOrderJson), 0, s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf("spotify:album:MIRROR");
        var t = new Track(Assert.Single(album.TrackSlots.ToArray()));
        Assert.Equal("Mirror Song", t.Title);

        var artistUris = album.ArtistSlots.ToArray().Select(slot => new Artist(slot).Uri.Text).ToArray();
        Assert.Equal(2, artistUris.Length);
        Assert.Contains("spotify:artist:MIR_A1", artistUris);
        Assert.Contains("spotify:artist:MIR_A2", artistUris);
    }

    // ── getAlbum, offset 0, as the "more by" prefetch (FIX 1) ──────────────────────────────────────────────────────────
    //
    // `FetchEdge.AlbumTracks` at offset 0 routes to `AlbumV4`, never here (`Fetch.Routes.cs`); the ONLY thing that
    // reaches `AlbumAnswer` at offset 0 is `FetchEdge.AlbumMoreBy`'s prefetch. That answer shares the exact same
    // persisted query, so it still carries a `tracksV2` — a short, unrelated one — and landing it would blow away
    // the tracklist `AlbumV4` already staged. `landTracks: false` is how a caller says "this is that prefetch".

    const string AlbumV4LikeJson = """
        { "data": { "albumUnion": {
            "uri": "spotify:album:MB", "name": "V4 Title", "type": "ALBUM",
            "artists": { "items": [ { "uri": "spotify:artist:V4LEAD", "profile": { "name": "V4 Lead" } } ] },
            "tracksV2": { "totalCount": 2, "items": [
                { "track": { "uri": "spotify:track:V4A", "name": "First" } },
                { "track": { "uri": "spotify:track:V4B", "name": "Second" } }
            ] }
        } } }
        """;

    const string AlbumMoreByPrefetchJson = """
        { "data": { "albumUnion": {
            "uri": "spotify:album:MB", "name": "MoreBy Title", "type": "ALBUM",
            "artists": { "items": [ { "uri": "spotify:artist:MBLEAD", "profile": { "name": "MoreBy Lead" } } ] },
            "tracksV2": { "totalCount": 1, "items": [
                { "track": { "uri": "spotify:track:GHOST", "name": "Should Not Land" } }
            ] }
        } } }
        """;

    [Fact]
    public void AlbumAnswer_MoreByPrefetch_DoesNotOverwrite_TheAlbumV4Tracklist()
    {
        TestScope.Fresh();

        var v4 = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json(AlbumV4LikeJson), 0, v4);
        TestScope.CommitAndPublish(v4);

        var album = AlbumOf("spotify:album:MB");
        var tracks = Entities.Current.Edges.AlbumTracks;
        Assert.Equal(2, tracks.Count(album.Slot));
        Assert.Equal("First", new Track(tracks.Targets(album.Slot)[0]).Title);
        Assert.Equal("Second", new Track(tracks.Targets(album.Slot)[1]).Title);

        var moreBy = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json(AlbumMoreByPrefetchJson), 0, moreBy, landTracks: false);
        TestScope.CommitAndPublish(moreBy);

        // The tracklist AlbumV4 landed survives untouched: same count, same ordinals, same identities.
        Assert.Equal(2, tracks.Count(album.Slot));
        Assert.Equal("spotify:track:V4A", new Track(tracks.Targets(album.Slot)[0]).Uri.Text);
        Assert.Equal("First", new Track(tracks.Targets(album.Slot)[0]).Title);
        Assert.Equal("spotify:track:V4B", new Track(tracks.Targets(album.Slot)[1]).Uri.Text);
        Assert.Equal("Second", new Track(tracks.Targets(album.Slot)[1]).Title);

        // The album's identity and billed artists from the SAME more-by answer DID land — only the tracklist page
        // is suppressed, per the fix.
        Assert.Equal("MoreBy Title", album.Title);
        var billed = new Artist(Assert.Single(album.ArtistSlots.ToArray()));
        Assert.Equal("spotify:artist:MBLEAD", billed.Uri.Text);
    }

    // ── getAlbum, offset 0, more-by prefetch WITH a leading artist run — both fixes at once ───────────────────────────
    //
    // Combines FIX 1 (`landTracks: false`) and FIX 2 (the deferred leading-artist close): the billed artists arrive
    // before `tracksV2` and before `uri`, so their run is deferred; the tracks are then popped rather than closed.
    // Both unwinds must leave the pending stack exactly where it started, or the NEXT decode on this staging arena
    // would inherit stray edges and land them under the wrong relation (Edges.Staging.cs's "only the top closes").

    const string AlbumMoreByWithLeadingArtistsJson = """
        { "data": { "albumUnion": {
            "artists": { "items": [ { "uri": "spotify:artist:MB_LEAD2", "profile": { "name": "Lead Two" } } ] },
            "tracksV2": { "totalCount": 1, "items": [ { "track": { "uri": "spotify:track:MB_GHOST2", "name": "Ghost" } } ] },
            "uri": "spotify:album:MB2", "name": "MoreBy Two", "type": "ALBUM"
        } } }
        """;

    [Fact]
    public void AlbumAnswer_MoreByPrefetch_WithLeadingArtists_LeavesNoPendingEdges()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        int mark = s.Edges.PendingMark;

        Spotify.Decode.AlbumAnswer(Json(AlbumMoreByWithLeadingArtistsJson), 0, s, landTracks: false);

        // Nothing leaked onto the pending stack for the next decode on this arena to inherit.
        Assert.Equal(0, s.Edges.Pending(mark));
        TestScope.CommitAndPublish(s);

        var album = AlbumOf("spotify:album:MB2");
        Assert.Equal(0, Entities.Current.Edges.AlbumTracks.Count(album.Slot));   // suppressed, not landed
        var billed = new Artist(Assert.Single(album.ArtistSlots.ToArray()));
        Assert.Equal("spotify:artist:MB_LEAD2", billed.Uri.Text);
    }

    [Fact]
    public void AlbumRelations_AnAnswerWithNoShelves_LandsBothRunsEmpty()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumRelations(Json("""{ "data": { "albumUnion": { "uri": "spotify:album:LONE", "name": "Lone" } } }"""), s);
        TestScope.CommitAndPublish(s);

        var lone = AlbumOf("spotify:album:LONE");
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumMoreBy.State(lone.Slot));
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumVersions.State(lone.Slot));
        Assert.Equal(0, lone.MoreBySlots.Length + lone.VersionSlots.Length);
    }

    [Fact]
    public void AlbumRelations_TheUriAfterTheShelves_StillExcludesTheAlbumItself()
    {
        // Forward-only cannot exclude what it has not read: the uri is read in its own pass first.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumRelations(Json("""
            { "data": { "albumUnion": {
                "releases": { "items": [ { "uri": "spotify:album:LATE", "name": "self" }, { "uri": "spotify:album:V2", "name": "v2" } ] },
                "uri": "spotify:album:LATE"
            } } }
            """), s);
        TestScope.CommitAndPublish(s);

        var version = new Album(Assert.Single(AlbumOf("spotify:album:LATE").VersionSlots.ToArray()));
        Assert.Equal("spotify:album:V2", version.Uri.Text);
    }

    // ── getAlbum past offset 0 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AlbumAnswer_ALaterPage_LandsAtItsOffset_AndStaysPartialUntilTheTotal()
    {
        TestScope.Fresh();
        var first = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json("""
            { "data": { "albumUnion": { "uri": "spotify:album:BOX", "name": "Box",
                "tracksV2": { "totalCount": 3, "items": [
                    { "track": { "uri": "spotify:track:B0", "name": "Zero" } },
                    { "track": { "uri": "spotify:track:B1", "name": "One" } } ] } } } }
            """), 0, first);
        TestScope.CommitAndPublish(first);

        var box = AlbumOf("spotify:album:BOX");
        var edges = Entities.Current.Edges.AlbumTracks;
        Assert.Equal(EdgeState.Partial, edges.State(box.Slot));
        Assert.Equal(2, edges.Count(box.Slot));

        var later = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json("""
            { "data": { "albumUnion": { "uri": "spotify:album:BOX",
                "tracksV2": { "totalCount": 3, "items": [ { "track": { "uri": "spotify:track:B2", "name": "Two" } } ] } } } }
            """), 2, later);
        TestScope.CommitAndPublish(later);

        Assert.Equal(EdgeState.Complete, edges.State(box.Slot));
        Assert.Equal(3, edges.Count(box.Slot));
        Assert.Equal("Zero", new Track(edges.Targets(box.Slot)[0]).Title);   // the first page was NOT overwritten
        Assert.Equal("Two", new Track(edges.Targets(box.Slot)[2]).Title);
    }

    // ── AlbumFields.TrackCount: a count of 0 is "not known yet" (album page, 2026-09-16) ──────────────────────────────
    //
    // The watch-video card on the single "Shot In The Dark" read "0 songs · 3 min · 2020" beside a facts tile saying
    // "1 Song" and a one-row tracklist: the row claimed `AlbumFields.TrackCount` Known while its column held 0.
    // `AlbumV4` staged `Identity` — which includes `TrackCount` — symbolically up front and only incremented the count
    // per decoded disc track, so a disc-less message settled as Known + 0; and the commit's Identity arm wrote the
    // `TrackCount` column unguarded, so a later `Authority.Full` producer that WITHHELD the bit (the pathfinder's
    // `getAlbum` header, whose `tracksV2.totalCount` closes the edge and never reaches the row) zeroed the column
    // while `Known |=` kept the earlier bit. Both ends are pinned here, read back through the handle after a commit.

    [Fact]
    public void AlbumV4_WithoutADisc_DoesNotClaimTrackCount_AndTheColumnStaysZero()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumV4(new Md.Album
        {
            Gid = Bs(Gid(120)),
            Name = "No Discs",
            Type = Md.Album.Types.Type.Single,
            Date = new Md.Date { Year = 2020 },
        }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf(120);
        Assert.Equal("No Discs", album.Title);                       // the rest of Identity still landed
        Assert.False(album.Knows(AlbumFields.TrackCount));
        Assert.Equal(0, album.TrackCount);
    }

    [Fact]
    public void AlbumV4_WithOneDiscTrack_KnowsTrackCount_AsOne()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumV4(new Md.Album
        {
            Gid = Bs(Gid(121)),
            Name = "Shot In The Dark",
            Type = Md.Album.Types.Type.Single,
            Disc = { new Md.Disc { Number = 1, Track = { new Md.Track { Gid = Bs(Gid(122)), Name = "Shot In The Dark", Number = 1, Duration = 180_000 } } } },
        }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf(121);
        Assert.True(album.Knows(AlbumFields.TrackCount));
        Assert.Equal(1, album.TrackCount);
        Assert.Equal(1, Entities.Current.Edges.AlbumTracks.Count(album.Slot));
    }

    [Fact]
    public void AlbumV4_WithoutADisc_KeepsTheCount_AnEarlierThinRowKnew()
    {
        // The merge half: `Applied` ORs `Known`, and a Full answer replaces a Thin one's Identity group. Withholding
        // the bit on the fuller answer must not take the count with it — the thin discography card knew 8, the
        // disc-less full answer knows nothing about it, and 8 stands.
        TestScope.Fresh();
        var thin = Staging.Rent();
        ref var card = ref thin.Albums.Add();
        card.Id = EntityId.ForGid(EntityKind.Album, Gid(123));
        card.Title = thin.AddText("Card"u8);
        card.Image = thin.AddText("img"u8);
        card.Year = 2019;
        card.TrackCount = 8;
        card.Kind = (byte)AlbumKind.Album;
        card.Known = (uint)AlbumFields.DiscoCard;
        card.Authority = Authority.Thin;
        TestScope.CommitAndPublish(thin);
        Assert.Equal(8, AlbumOf(123).TrackCount);

        var full = Staging.Rent();
        Spotify.Decode.AlbumV4(new Md.Album { Gid = Bs(Gid(123)), Name = "Card (full)", Type = Md.Album.Types.Type.Album }.ToByteArray(), full);
        TestScope.CommitAndPublish(full);

        var album = AlbumOf(123);
        Assert.Equal("Card (full)", album.Title);                    // the full answer DID land its Identity
        Assert.True(album.Knows(AlbumFields.TrackCount));
        Assert.Equal(8, album.TrackCount);                           // never zeroed
    }

    [Fact]
    public void GetAlbumHeader_AfterAlbumV4_KeepsTheDiscCount_TheRecordedDefect()
    {
        // The recording's exact order: `AlbumV4` lands the single's one disc track (count 1, Known), then the
        // pathfinder's first `getAlbum` page stages the same album at Authority.Full with no count of its own.
        TestScope.Fresh();
        var v4 = Staging.Rent();
        Spotify.Decode.AlbumV4(new Md.Album
        {
            Gid = Bs(Gid(124)),
            Name = "Shot In The Dark",
            Type = Md.Album.Types.Type.Single,
            Disc = { new Md.Disc { Number = 1, Track = { new Md.Track { Gid = Bs(Gid(125)), Name = "Shot In The Dark", Number = 1, Duration = 180_000 } } } },
        }.ToByteArray(), v4);
        TestScope.CommitAndPublish(v4);
        string uri = AlbumOf(124).Uri.Text;
        Assert.Equal(1, AlbumOf(124).TrackCount);

        var page = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json("""
            { "data": { "albumUnion": { "uri": "__URI__", "name": "Shot In The Dark", "type": "SINGLE",
                "date": { "isoString": "2020-05-01T00:00:00Z" },
                "tracksV2": { "totalCount": 1, "items": [ { "track": { "uri": "spotify:track:SITD", "name": "Shot In The Dark" } } ] } } } }
            """.Replace("__URI__", uri)), 0, page);
        TestScope.CommitAndPublish(page);

        var album = AlbumOf(124);
        Assert.True(album.Knows(AlbumFields.TrackCount));
        Assert.Equal(1, album.TrackCount);                           // not "0 songs" beside a one-row tracklist
    }

    // ── the cover accent (defect 2 of the 2026-09-16 recording: the page painted blue until the palette graded) ──────

    [Fact]
    public void PathfinderAlbumNode_StagesTheCoverColour_AsAnArgbAccent()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.SimilarAlbums(Json("""
            { "data": { "seoRecommendedTrackAlbum": { "items": [
                { "data": { "uri": "spotify:album:tinted", "name": "Tinted", "type": "ALBUM",
                            "coverArt": { "sources": [ { "url": "https://cdn/tinted", "width": 300, "height": 300 } ],
                                          "extractedColors": { "colorRaw": { "hex": "#8898A8" } } } } },
                { "data": { "uri": "spotify:album:plain", "name": "Plain", "type": "ALBUM",
                            "coverArt": { "sources": [ { "url": "https://cdn/plain", "width": 300, "height": 300 } ] } } },
                { "data": { "uri": "spotify:album:garbled", "name": "Garbled", "type": "ALBUM",
                            "coverArt": { "sources": [ { "url": "https://cdn/garbled" } ],
                                          "extractedColors": { "colorRaw": { "hex": "#GGGGGG" } } } } }
            ] } } }
            """), "spotify:album:page"u8, s);
        TestScope.CommitAndPublish(s);

        var tinted = AlbumOf("spotify:album:tinted");
        Assert.Equal(0xFF8898A8u, tinted.Accent);
        Assert.Equal("https://cdn/tinted", Entities.Strings.Resolve(tinted.ImageId));   // the url still lands beside the colour
        Assert.Equal(0u, AlbumOf("spotify:album:plain").Accent);                          // no colour: 0 = none
        Assert.Equal(0u, AlbumOf("spotify:album:garbled").Accent);                        // not hex: 0, never a partial parse
    }

    [Fact]
    public void GetAlbumHeader_StagesTheCoverColour_AndALaterAnswerWithoutOneKeepsIt()
    {
        TestScope.Fresh();
        var first = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json("""
            { "data": { "albumUnion": { "uri": "spotify:album:hdr", "name": "Header", "type": "SINGLE",
                "coverArt": { "sources": [ { "url": "https://cdn/hdr", "width": 640, "height": 640 } ],
                              "extractedColors": { "colorRaw": { "hex": "#1a2B3c" } } },
                "tracksV2": { "totalCount": 1, "items": [ { "track": { "uri": "spotify:track:hdr1", "name": "Header" } } ] } } } }
            """), 0, first);
        TestScope.CommitAndPublish(first);
        Assert.Equal(0xFF1A2B3Cu, AlbumOf("spotify:album:hdr").Accent);

        // A thin mention without a colour (a card elsewhere) must not blank what the header answered.
        var later = Staging.Rent();
        Spotify.Decode.SimilarAlbums(Json("""
            { "data": { "seoRecommendedTrackAlbum": { "items": [
                { "data": { "uri": "spotify:album:hdr", "name": "Header", "type": "SINGLE",
                            "coverArt": { "sources": [ { "url": "https://cdn/hdr" } ] } } }
            ] } } }
            """), "spotify:album:other"u8, later);
        TestScope.CommitAndPublish(later);
        Assert.Equal(0xFF1A2B3Cu, AlbumOf("spotify:album:hdr").Accent);
    }

    [Fact]
    public void CoverArt_picks_the_640_source_not_the_first_64()
    {
        // Spotify lists 64 then 300 then 640. ImageNode used to intern the first URL, so every playlist/album/show
        // hero decoded a 64px JPEG. The stored column is the rendition — Choose(640) is the pick.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.SimilarAlbums(Json("""
            { "data": { "seoRecommendedTrackAlbum": { "items": [
                { "data": { "uri": "spotify:album:A", "name": "Sized", "type": "ALBUM",
                            "date": { "year": 2022 },
                            "coverArt": { "sources": [
                                { "url": "https://cdn/64", "width": 64, "height": 64 },
                                { "url": "https://cdn/300", "width": 300, "height": 300 },
                                { "url": "https://cdn/640", "width": 640, "height": 640 }
                            ] },
                            "artists": { "items": [ { "uri": "spotify:artist:X", "profile": { "name": "Aurora" } } ] } } }
            ] } } }
            """), "spotify:album:page"u8, s);
        TestScope.CommitAndPublish(s);

        var a = new Album(Assert.Single(Entities.Current.Edges.AlbumSimilar.Targets(AlbumOf("spotify:album:page").Slot).ToArray()));
        Assert.Equal("https://cdn/640", Entities.Strings.Resolve(a.ImageId));
    }
}
