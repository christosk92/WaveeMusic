// ── Wavee.Tests/TrackImageBitTests.cs — the grey row thumbnail (2026-09-18, bug C's track variant) ────────────────
//
// `TrackFields.Identity` is one six-bit constant (`Title | Artists | Album | Duration | Explicit | Image`), and
// `CommitTracks` sealed every bit of it the moment a producer staged the group — including `Image`, over a column the
// answer never filled. A THIN producer is the one that has no business claiming it: a playlist item, a search hit, a
// cluster row and an `AlbumV4` disc row all stage `Identity` and all treat the cover as optional
// (`Spotify.Decode.Playlist.cs`, `Spotify.Decode.Pathfinder.cs`, `Spotify.Decode.cs`'s `DiscTrack`). With the bit
// sealed, `Fetch.NeedOf = wanted & ~Settled & ~Asked` saw no hole, so every demand whose wanted set is Image-shaped and
// nothing wider — `User.Cover.cs`'s `Ensure(Image | Album)` for the profile mosaic, `Sidebar.MosaicTrackFields` — asked
// for nothing at all and the art stayed the flat placeholder tint for the life of the row. Exactly the artist half of
// the same bug (`ArtistImageBitTests`: the "Top artists" chip that fell back to initials forever).
//
// AUTHORITY IS THE GATE, which is the other half of the fix and the half worth the facts below. `Thin` loses the bit;
// `Full`, `Local` and `Seed` keep it, because they SPEAK for the whole entity and "no cover" is their answer — the
// terminal `UnavailableTrack` verdict (`Spotify.Decode.cs`: a track that does not exist for this account), a local
// file's own tags, the fake local-files rows (`Entities.Fake.Library.cs`). Demoting their bit would re-ask nothing
// (`Fetch.Answer` seals `Asked` on success) and would only make `TrackFields.Face` unknowable — the group
// `TableRules.RowHasData` reads — so the row would shimmer as LOADING forever instead of painting the blank row the
// verdict describes.
//
// Same pipeline as `ArtistImageBitTests`: `Staging.Rent()` → a hand-staged row for the commit gate itself →
// `TestScope.CommitAndPublish` → read back through the `Track` handle, never off the staged row.

using FluentGpu.Foundation;   // StringId — the interner handle the row hands back for its cover
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class TrackImageBitTests
{
    static byte[] Gid(byte seed)
    {
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return gid;
    }

    static EntityId TrackId(byte seed) => EntityId.ForGid(EntityKind.Track, Gid(seed));
    static EntityId AlbumId(byte seed) => EntityId.ForGid(EntityKind.Album, Gid(seed));
    static Track TrackOf(byte seed) => Entities.Track(TrackId(seed));
    static string Text(StringId id) => Entities.Strings.Resolve(id);

    // ── the commit gate (`CommitTracks`, `Entities/Track.cs`) ────────────────────────────────────────────────────────

    [Fact]
    public void A_thin_answer_with_no_cover_marks_the_rest_of_Identity_but_never_Image()
    {
        // Decoder-agnostic: pins the bit arithmetic the commit performs, whichever producer staged the row. A playlist
        // item, a search hit and a disc row under a cover-less album all reach the commit in exactly this shape.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(TrackId(60), Authority.Thin, (uint)TrackFields.Identity);
        row.Title = s.Text("Mentioned Only");
        row.DurationMs = 214_000;
        // row.Image is deliberately left default — a mention that read no cover has none to offer.
        TestScope.CommitAndPublish(s);

        var track = TrackOf(60);
        Assert.True(track.Knows(TrackFields.Title));
        Assert.Equal("Mentioned Only", track.Title);
        Assert.True(track.Knows(TrackFields.Duration));
        Assert.False(track.Knows(TrackFields.Image));         // the bug: this used to read true
        Assert.False(track.Knows(TrackFields.Identity));      // … so the whole group stays a hole the planner can fill
        Assert.True(track.ImageId.IsEmpty);
        // AND THEREFORE the row does not yet know its `Face` — which is what makes it re-askable. It is transient by
        // construction: every surface that lists rows demands `Identity` (`Playlist.Page.cs`, `Album.Page.cs`), the
        // hole makes that demand real again, and `TrackV4` answers with the album cover as its own fallback.
        Assert.False(track.Knows(TrackFields.Face));
    }

    [Fact]
    public void A_thin_answer_that_carries_a_cover_claims_the_whole_group()
    {
        // The positive case, so the mask is not mistaken for "a thin row can never know its cover": the shape a
        // playlist item with a cover, or a disc row under an album that has one, actually produces.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(TrackId(61), Authority.Thin, (uint)TrackFields.Identity);
        row.Title = s.Text("With A Cover");
        row.Image = s.Text("https://i.scdn.co/image/thin61");
        TestScope.CommitAndPublish(s);

        var track = TrackOf(61);
        Assert.True(track.Knows(TrackFields.Image));
        Assert.True(track.Knows(TrackFields.Identity));
        Assert.True(track.Knows(TrackFields.Face));
        Assert.Equal("https://i.scdn.co/image/thin61", Text(track.ImageId));
    }

    [Fact]
    public void A_later_answer_fills_the_Image_hole_a_cover_less_mention_left()
    {
        // The end-to-end shape of the fix mattering: the row is mentioned first with no cover, and the answer the hole
        // provoked fills it. `Accepts` must let the second answer complete the group instead of finding it sealed.
        TestScope.Fresh();
        var mention = Staging.Rent();
        ref var thin = ref mention.Tracks.RowFor(TrackId(62), Authority.Thin, (uint)TrackFields.Identity);
        thin.Title = mention.Text("Filled Later");
        TestScope.CommitAndPublish(mention);
        Assert.False(TrackOf(62).Knows(TrackFields.Image));

        var answer = Staging.Rent();
        ref var full = ref answer.Tracks.RowFor(TrackId(62), Authority.Full, (uint)TrackFields.Identity);
        full.Title = answer.Text("Filled Later");
        full.Image = answer.Text("https://i.scdn.co/image/full62");
        TestScope.CommitAndPublish(answer);

        var track = TrackOf(62);
        Assert.True(track.Knows(TrackFields.Image));
        Assert.True(track.Knows(TrackFields.Identity));
        Assert.Equal("https://i.scdn.co/image/full62", Text(track.ImageId));
    }

    [Fact]
    public void A_thin_answer_with_no_cover_keeps_the_bit_a_cover_already_on_the_row_earned()
    {
        // The resident-column half of the guard. The Image WRITE has always been guarded ("nothing legitimately removes
        // an image"), so a later cover-less answer leaves the column alone — the bit has to follow the same rule, or a
        // second mention of a row whose cover already landed would demote a fact the row can plainly see.
        TestScope.Fresh();
        var first = Staging.Rent();
        ref var withCover = ref first.Tracks.RowFor(TrackId(63), Authority.Thin, (uint)TrackFields.Identity);
        withCover.Title = first.Text("Keeps Its Cover");
        withCover.Image = first.Text("https://i.scdn.co/image/thin63");
        TestScope.CommitAndPublish(first);

        var second = Staging.Rent();
        ref var bare = ref second.Tracks.RowFor(TrackId(63), Authority.Thin, (uint)TrackFields.Identity);
        bare.Title = second.Text("Keeps Its Cover");
        TestScope.CommitAndPublish(second);

        var track = TrackOf(63);
        Assert.True(track.Knows(TrackFields.Image));
        Assert.True(track.Knows(TrackFields.Identity));
        Assert.Equal("https://i.scdn.co/image/thin63", Text(track.ImageId));
    }

    [Fact]
    public void A_full_answer_with_no_cover_still_speaks_for_the_whole_group()
    {
        // UNCHANGED by the fix, and deliberately so — the track counterpart of `ArtistImageBitTests`'s "the overview
        // spoke for it: verified, no photo". This is `UnavailableTrack`'s shape (`Spotify.Decode.cs`: Identity AND
        // Availability at Full authority, an empty title, no cover, no release instant): a terminal verdict, nothing
        // left to ask. It must keep `Face` known, or `TableRules.RowHasData` turns every ruled-unavailable row into a
        // shimmer that never resolves instead of the blank row the verdict describes.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(TrackId(64), Authority.Full,
            (uint)(TrackFields.Identity | TrackFields.Availability));
        row.Flags |= (uint)TrackFlags.Unavailable;
        TestScope.CommitAndPublish(s);

        var track = TrackOf(64);
        Assert.True(track.Knows(TrackFields.Image));
        Assert.True(track.Knows(TrackFields.Identity));
        Assert.True(track.Knows(TrackFields.Face));
        Assert.True(track.Unplayable());
        Assert.True(track.ImageId.IsEmpty);
    }

    [Fact]
    public void A_seed_row_with_no_cover_still_speaks_for_the_whole_group()
    {
        // `Entities.Fake.Library.cs`'s fourteen imported-files rows, and the real local-file shape behind them: a
        // cover-less mp3 is a FACT, not a hole, and `--fake` has no provider to ask anyway.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(TrackId(65), Authority.Seed,
            (uint)(TrackFields.Identity | TrackFields.Availability));
        row.Title = s.Text("Imported File");
        row.Flags = (uint)TrackFlags.Local;
        TestScope.CommitAndPublish(s);

        var track = TrackOf(65);
        Assert.True(track.Knows(TrackFields.Image));
        Assert.True(track.Knows(TrackFields.Face));
        Assert.True(track.ImageId.IsEmpty);
    }

    [Fact]
    public void The_withheld_bit_leaves_no_stale_or_failed_mark_behind()
    {
        // A withheld bit is a HOLE, not a stale or a failed one. `Applied` clears `Stale`/`Failed` only for the bits it
        // is handed, so the commit clears them for the bit it withholds — otherwise an invalidated row (`Invalidate`
        // marks the whole group stale, `FetchInvalidateTests`) re-answered by a thin, cover-less answer would read
        // `IsStale(Identity)` true forever (that predicate is "any bit"), and a retried failure would still claim a
        // terminal refusal the answer just retired.
        TestScope.Fresh();
        var t = Entities.Current.Tracks;
        int slot = t.Slot(TrackId(66));
        t.Known[slot] |= (uint)TrackFields.Identity;
        t.Stale[slot] |= (uint)TrackFields.Identity;
        t.Failed[slot] |= (uint)TrackFields.Identity;

        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(TrackId(66), Authority.Thin, (uint)TrackFields.Identity);
        row.Title = s.Text("Re-answered, still no cover");
        TestScope.CommitAndPublish(s);

        Assert.False(t.IsStale(slot, (uint)TrackFields.Identity));
        Assert.Equal(0u, t.Failed[slot] & (uint)TrackFields.Identity);
        Assert.Equal((uint)TrackFields.Identity, t.Settled(slot) & (uint)TrackFields.Identity);
    }

    // ── the row's painted cover (`Track.RowArtUrl`, `Entities/Track.UI.cs`) ──────────────────────────────────────────

    [Fact]
    public void A_row_with_no_cover_of_its_own_paints_its_albums()
    {
        // The other half of the grey thumbnail, and the half the user sees: a row is an entry IN an album, so its art
        // falls back to the album's sleeve (ch 01 GAP — the same fallback `Track.Table.Chrome.cs`'s pane thumbs and
        // `Artist.Page.cs`'s video rail already make). The bind re-fires when a late album row lands because
        // `RowPresentation` carries the album's `Version`.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var album = ref s.Albums.RowFor(AlbumId(80), Authority.Full, (uint)AlbumFields.Identity);
        album.Title = s.Text("Sleeve");
        album.Image = s.Text("albumcover80");
        ref var row = ref s.Tracks.RowFor(TrackId(81), Authority.Thin, (uint)TrackFields.Identity);
        row.Title = s.Text("No Cover Of Its Own");
        row.AlbumUri = AlbumId(80);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(81);
        Assert.True(track.ImageId.IsEmpty);
        Assert.True(track.Album.IsValid);
        Assert.Equal("albumcover80", Text(track.Album.ImageId));
        Assert.Equal("https://i.scdn.co/image/albumcover80", Track.RowArtUrl(track));
    }

    [Fact]
    public void A_rows_own_cover_wins_over_its_albums()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var album = ref s.Albums.RowFor(AlbumId(82), Authority.Full, (uint)AlbumFields.Identity);
        album.Title = s.Text("Sleeve");
        album.Image = s.Text("albumcover82");
        ref var row = ref s.Tracks.RowFor(TrackId(83), Authority.Thin, (uint)TrackFields.Identity);
        row.Title = s.Text("Its Own Cover");
        row.Image = s.Text("trackcover83");
        row.AlbumUri = AlbumId(82);
        TestScope.CommitAndPublish(s);

        Assert.Equal("https://i.scdn.co/image/trackcover83", Track.RowArtUrl(TrackOf(83)));
    }

    [Fact]
    public void A_row_with_no_cover_and_no_album_paints_nothing_rather_than_reading_a_none_slot()
    {
        // Null, not "" and not a read of `Column<T>[Table.None]`: the placeholder tint and the `ImageEl`'s empty source
        // are what a coverless row paints, and the album handle is checked before it is dereferenced.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(TrackId(84), Authority.Thin, (uint)TrackFields.Identity);
        row.Title = s.Text("Nothing To Paint");
        TestScope.CommitAndPublish(s);

        var track = TrackOf(84);
        Assert.False(track.Album.IsValid);
        Assert.Null(Track.RowArtUrl(track));
    }
}
