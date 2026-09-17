// ── Wavee.Tests/AlbumArtistsBitTests.cs — AlbumFields.Artists, the album variant of bug C (2026-09-15) ──────────────
//
// `AlbumFields.Identity` gained an `Artists` bit that mirrors `TrackFields.Artists` exactly: it names whether the
// decode that staged Identity ALSO closed `Relation.AlbumArtists`, because `AlbumV4` (ext kind 9) is the sole route
// registered for `AlbumFields.Identity` (`Fetch.Routes.cs`) and it emits the billed-artist edge as a side effect —
// a relation, not a `FetchEdge`, asked only by asking the row's own group. Before this bit existed, ANY producer
// that filled Title/Image/Year/TrackCount/Kind was treated as if it had also answered for the artists, and once a
// disk-restored row (or a thin card) claimed the group, `Fetch.NeedOf` never asked `AlbumV4` again — the
// billed-artist line went blank forever. These facts pin the two ends of that contract: the rich decoders that DO
// close the run mark the bit, and the thin ones that do not, don't.
//
// Same pipeline as `AlbumDecodeTests.cs`: `Staging.Rent()` → the real decoder → `TestScope.CommitAndPublish` → read
// back through the `Album`/`Track` handles, never off the staged row. `StoreTests.cs` covers the DISK half
// (`AlbumShape.PersistedIdentity` masking the bit out of Save/Load) — this file covers the LIVE decoders only.

using Google.Protobuf;
using Wavee;
using Xunit;
using Md = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class AlbumArtistsBitTests
{
    static byte[] Gid(byte seed)
    {
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return gid;
    }

    static ByteString Bs(byte[] bytes) => ByteString.CopyFrom(bytes);
    static byte[] Json(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    static Album AlbumOf(byte seed) => Entities.Album(EntityId.ForGid(EntityKind.Album, Gid(seed)));
    static Album AlbumOf(string uri) => Entities.Album(EntityUri.Parse(uri));

    // ── AlbumV4 (ext kind 9, the binary route) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AlbumV4_with_a_billed_artist_marks_ArtistsKnown_and_closes_the_edge()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumV4(new Md.Album
        {
            Gid = Bs(Gid(50)),
            Name = "Full Answer",
            Type = Md.Album.Types.Type.Album,
            Artist = { new Md.Artist { Gid = Bs(Gid(51)), Name = "Billed One" } },
            Disc = { new Md.Disc { Number = 1, Track = { new Md.Track { Gid = Bs(Gid(52)), Name = "T1", Number = 1 } } } },
        }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf(50);
        Assert.True(album.Knows(AlbumFields.Artists));
        Assert.True(album.Knows(AlbumFields.Identity));       // the whole group, Artists included
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumArtists.State(album.Slot));
        var billed = new Artist(Assert.Single(album.ArtistSlots.ToArray()));
        Assert.Equal("Billed One", billed.Name);
    }

    [Fact]
    public void AlbumV4_with_no_artist_entries_still_closes_the_run_Complete_and_empty()
    {
        // `artists.End(in id)` runs unconditionally at the bottom of `AlbumV4` — an album that genuinely named no
        // artist still gets a real (empty) answer, the same "an answer that names nothing is still an answer"
        // rule `Relation.AlbumMerch`/`AlbumSimilar` already follow. The bit must still read Known: nothing further
        // will ever tell this row about its artists, and re-asking would be a wasted trip forever.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumV4(new Md.Album
        {
            Gid = Bs(Gid(53)),
            Name = "No Artists Named",
            Type = Md.Album.Types.Type.Album,
        }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf(53);
        Assert.True(album.Knows(AlbumFields.Artists));
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumArtists.State(album.Slot));
        Assert.Equal(0, Entities.Current.Edges.AlbumArtists.Count(album.Slot));
    }

    // ── the thin album row TrackV4 stages for its own embedded album (Spotify.Decode.cs's `ThinAlbum`) ──────────────

    [Fact]
    public void TrackV4s_thin_embedded_album_never_marks_ArtistsKnown_or_the_whole_Identity()
    {
        // `ThinAlbum` (Spotify.Decode.cs, used by `TrackV4` case 3) stages only Title/Image/Year(+Release) — never
        // TrackCount or Kind, and never a `Relation.AlbumArtists` run. It must not claim Artists, and — since it
        // never reaches the other Identity bits either — `Knows(Identity)` must stay false too, so a page that
        // wants the full group still asks `AlbumV4` for this album's real answer.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.TrackV4(new Md.Track
        {
            Gid = Bs(Gid(60)),
            Name = "Some Song",
            Album = new Md.Album
            {
                Gid = Bs(Gid(61)),
                Name = "Embedded Album",
                Date = new Md.Date { Year = 2020 },
            },
        }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf(61);
        Assert.True(album.Knows(AlbumFields.Title));           // the thin row DOES carry a title …
        Assert.False(album.Knows(AlbumFields.Artists));        // … but never the billed artists …
        Assert.False(album.Knows(AlbumFields.Identity));       // … and therefore never the whole group
        Assert.Equal(EdgeState.Unknown, Entities.Current.Edges.AlbumArtists.State(album.Slot));
    }

    // ── the pathfinder's `getAlbum` (Spotify.Decode.Pathfinder.cs's `GetAlbum`, via `Spotify.Decode.AlbumAnswer`) ────

    const string GetAlbumJson = """
        { "data": { "albumUnion": {
            "uri": "spotify:album:PF1", "name": "Pathfinder Album", "type": "ALBUM",
            "artists": { "items": [ { "uri": "spotify:artist:PF1A", "profile": { "name": "PF Artist" } } ] },
            "tracksV2": { "totalCount": 1, "items": [ { "track": {
                "uri": "spotify:track:PF1T", "name": "PF Track", "duration": { "totalMilliseconds": 100000 } } } ] }
        } } }
        """;

    [Fact]
    public void GetAlbum_pathfinder_marks_ArtistsKnown_when_the_answer_carried_billed_artists()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json(GetAlbumJson), 0, s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf("spotify:album:PF1");
        Assert.True(album.Knows(AlbumFields.Artists));
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumArtists.State(album.Slot));
    }

    // `artists` arrives BEFORE `tracksV2`, which arrives before `uri` — the deferred-leading-artists path
    // (`GetAlbum`'s `deferredLeadingArtists`), the one case where the close happens AFTER `Stage` already ran and
    // needs the post-hoc re-take by index (mirrors `AlbumV4`'s own cover derivation, `Spotify.Decode.cs`).
    const string GetAlbumDeferredJson = """
        { "data": { "albumUnion": {
            "artists": { "items": [ { "uri": "spotify:artist:PF2A", "profile": { "name": "PF Deferred Artist" } } ] },
            "tracksV2": { "totalCount": 1, "items": [ { "track": {
                "uri": "spotify:track:PF2T", "name": "PF Deferred Track", "duration": { "totalMilliseconds": 100000 } } } ] },
            "uri": "spotify:album:PF2", "name": "Pathfinder Deferred Album", "type": "ALBUM"
        } } }
        """;

    [Fact]
    public void GetAlbum_pathfinder_deferred_leading_artists_still_marks_ArtistsKnown()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json(GetAlbumDeferredJson), 0, s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf("spotify:album:PF2");
        Assert.True(album.Knows(AlbumFields.Artists));
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.AlbumArtists.State(album.Slot));
        var billed = new Artist(Assert.Single(album.ArtistSlots.ToArray()));
        Assert.Equal("PF Deferred Artist", billed.Name);
    }

    const string GetAlbumNoArtistsJson = """
        { "data": { "albumUnion": {
            "uri": "spotify:album:PF3", "name": "Pathfinder No Artists", "type": "ALBUM"
        } } }
        """;

    [Fact]
    public void GetAlbum_pathfinder_with_no_artists_field_never_marks_ArtistsKnown()
    {
        // No `artists` field at all → nothing was ever pushed → `CloseArtists` never fires (`Pending == 0`) → the
        // run stays Unknown, not Complete-and-empty (unlike `AlbumV4`'s unconditional close). `Stage`'s
        // `n.ArtistsClosed` must therefore stay false and the bit must not be claimed.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Json(GetAlbumNoArtistsJson), 0, s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf("spotify:album:PF3");
        Assert.False(album.Knows(AlbumFields.Artists));
        Assert.False(album.Knows(AlbumFields.Identity));
        Assert.Equal(EdgeState.Unknown, Entities.Current.Edges.AlbumArtists.State(album.Slot));
    }

    // ── AlbumFields.DiscoCard (Artist.Discography.cs's grid `Ensure`/gate, narrowed off Card so a thin
    //    `ArtistStageRelease` row does not add a third AlbumV4 hop behind bug D's two) ────────────────────────────────

    [Fact]
    public void DiscoCard_is_Card_minus_Artists_and_nothing_else_moved()
    {
        // Pure bit arithmetic — no entity, no decoder, no commit: pins the constant itself against drift.
        Assert.Equal((uint)AlbumFields.Card & ~(uint)AlbumFields.Artists, (uint)AlbumFields.DiscoCard);
        Assert.Equal(0u, (uint)AlbumFields.DiscoCard & (uint)AlbumFields.Artists);          // Artists excluded …
        Assert.Equal((uint)AlbumFields.DiscoCard, (uint)AlbumFields.DiscoCard & (uint)AlbumFields.Card);   // … a subset of Card …
        Assert.Equal((uint)AlbumFields.Title | (uint)AlbumFields.Image | (uint)AlbumFields.Year
                   | (uint)AlbumFields.TrackCount | (uint)AlbumFields.Kind | (uint)AlbumFields.Release,
                     (uint)AlbumFields.DiscoCard);                                          // … exactly what a card paints
    }

    [Fact]
    public void A_thin_ArtistStageRelease_shaped_row_satisfies_DiscoCard_without_Artists_but_not_Card_or_Identity()
    {
        // `ArtistStageRelease`'s discography-card row (Spotify.Decode.Artist.cs) never parses billed artists at
        // all — Title/Image/Year/Kind unconditionally, TrackCount and Release conditionally — so a thin row with
        // exactly DiscoCard's bits, and nothing more, is the REAL shape landing on the grid every time.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Albums.Add();
        row.Id = EntityId.ForGid(EntityKind.Album, Gid(70));
        row.Title = s.AddText("Thin Release"u8);
        row.Image = s.AddText("img"u8);
        row.Year = 2024;
        row.TrackCount = 8;
        row.Kind = (byte)AlbumKind.Album;
        row.ReleaseDateIso = s.AddText("2024-03-01"u8);
        row.DatePrecision = 2;
        row.Known = (uint)AlbumFields.DiscoCard;
        row.Authority = Authority.Thin;
        TestScope.CommitAndPublish(s);

        var album = AlbumOf(70);
        Assert.True(album.Knows(AlbumFields.DiscoCard));    // the grid's own gate: satisfied
        Assert.False(album.Knows(AlbumFields.Artists));     // never claimed — nothing in the payload backs it
        Assert.False(album.Knows(AlbumFields.Card));        // Card still wants Artists, so this row does NOT satisfy it
        Assert.False(album.Knows(AlbumFields.Identity));    // same reason, one level up
    }
}
