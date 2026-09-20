// ── Wavee.Tests/ArtistImageBitTests.cs — the "Top artists" chip rail regression (2026-09-15, bug C's artist variant) ──
//
// `ArtistFields.Identity` is `Name | Image` (`Artist.cs`). `CommitArtistRows` used to seal the WHOLE group with the
// bare `ArtistFields.Identity` constant the instant `known` carried EITHER bit — so a billed-artist mention staged
// by `ThinArtist` (`Spotify.Decode.cs`, a track or album's credit run: gid + name, no portrait ever read off the
// wire) walked out of the commit claiming a photo it never saw. `Fetch.NeedOf = wanted & ~Known & ~Asked` then saw
// no hole in `Identity` to fill, and `User.FactsUI.cs`'s "Top artists" rail — which already does the right thing,
// `Entities.Ensure(artists, ArtistFields.Identity)` in `DemandPortraits`, subscribed to `Artists.Changed` — never
// got an answer to paint: the chip fell back to its initials forever, for every artist whose only mention so far
// was a track credit. Names were right ("St-Amour · 6"); the photo was permanently unaskable.
//
// The fix mirrors `CommitTracks`/`CommitAlbums`'s identical fix for the same class of bug: `Applied(slot, known &
// group, …)`, never the bare group constant. Three producers that also declared `Known = Identity` up front, before
// reading whether a portrait actually arrived, get the same mask: `ArtistV4` (`Spotify.Decode.cs`, ext kind 8 — the
// ONE route registered for `ArtistFields.Identity`, `Fetch.Routes.cs`), the legacy, unwired `Decode.ArtistOverview`
// (`Spotify.Decode.Pathfinder.cs`), and `ArtistUnion` (`Spotify.Decode.Artist.cs` — `queryArtistOverview` /
// `queryNpvArtist`, today's live artist-page decoder). `ArtistUnion`'s bug was the widest of the three: the claim
// was unconditional in BOTH its modes, so even a thin `queryNpvArtist` answer — which reads `visuals` and so CAN
// legitimately carry no avatar — sealed `Image` known without ever looking at whether `visuals.avatarImage` was
// present. The fix makes `Image` follow the exact rule `Header` already follows one line below it in that function:
// the WHOLE overview (`overview: true`) speaks for the group, absent or not (an artist verified to have no photo
// is a real fact, same as "no pick"); a thin NPV answer (`overview: false`) only claims what it actually carried.
//
// Same pipeline as `AlbumArtistsBitTests.cs`: `Staging.Rent()` → the real decoder (or a hand-staged row for the
// commit gate itself) → `TestScope.CommitAndPublish` → read back through the `Artist` handle, never off the staged
// row.

using System.Text;
using FluentGpu.Foundation;   // StringId — the interner handle the artist row hands back for its name
using Google.Protobuf;
using Wavee;
using Xunit;
using Md = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ArtistImageBitTests
{
    static byte[] Gid(byte seed)
    {
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return gid;
    }

    static ByteString Bs(byte[] bytes) => ByteString.CopyFrom(bytes);
    static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);
    static string Text(FluentGpu.Foundation.StringId id) => Entities.Strings.Resolve(id);

    static Artist ArtistOf(byte seed) => Entities.Artist(EntityId.ForGid(EntityKind.Artist, Gid(seed)));
    static string UriOf(byte seed) => EntityId.ForGid(EntityKind.Artist, Gid(seed)).Text;

    static Md.ImageGroup Cover(byte seed) => new()
    {
        Image =
        {
            new Md.Image { FileId = Bs(Gid((byte)(seed + 1))), Size = Md.Image.Types.Size.Small },
            new Md.Image { FileId = Bs(Gid(seed)), Size = Md.Image.Types.Size.Default },
        },
    };

    // ── the commit gate itself (`CommitArtistRows`, `Entities/Artist.cs`) ──────────────────────────────────────────

    [Fact]
    public void CommitArtistRows_never_claims_the_whole_Identity_group_from_a_row_that_only_staged_Name()
    {
        // Decoder-agnostic: pins the exact bit arithmetic the commit performs, independent of which producer staged
        // the row. `known = ArtistFields.Name` only — never `Identity` — is the honest shape of `ThinArtist`'s
        // output, and every other Name-only producer in the app (`Spotify.Decode.Playlist.cs`'s recommended-track
        // credits, `Spotify.Decode.Pathfinder.cs`'s `EntityNode`/`Stage` artist arms) stages the identical shape.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Artists.RowFor(EntityId.ForGid(EntityKind.Artist, Gid(90)), Authority.Thin, (uint)ArtistFields.Name);
        row.Name = s.AddText("Name Only"u8);
        // row.Image is deliberately left default — no producer that only read a name has a portrait to offer.
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(90);
        Assert.True(artist.Knows(ArtistFields.Name));
        Assert.Equal("Name Only", artist.Name);
        Assert.False(artist.Knows(ArtistFields.Image));       // the bug: this used to read true
        Assert.False(artist.Knows(ArtistFields.Identity));    // … and therefore the whole group must stay unknown
    }

    [Fact]
    public void CommitArtistRows_claims_the_whole_Identity_group_when_the_row_actually_carries_both()
    {
        // The positive case, so the mask fix (`known & Identity`) is not mistaken for "Image can never be known":
        // a row that genuinely staged both bits — the shape a real `ArtistV4`/pathfinder overview answer produces —
        // still seals the group in one commit, exactly as before the fix.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Artists.RowFor(EntityId.ForGid(EntityKind.Artist, Gid(91)), Authority.Full, (uint)ArtistFields.Identity);
        row.Name = s.AddText("Full Answer"u8);
        row.Image = s.AddText("https://i.scdn.co/image/full91"u8);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(91);
        Assert.True(artist.Knows(ArtistFields.Identity));
        Assert.Equal("Full Answer", artist.Name);
        Assert.Equal("https://i.scdn.co/image/full91", Entities.Strings.Resolve(Entities.Current.Artists.Image[artist.Slot]));
    }

    [Fact]
    public void A_later_full_answer_fills_the_hole_a_thin_credit_left_in_Image()
    {
        // The end-to-end shape of the fix actually mattering: a track credit mentions the artist first (Name only,
        // Thin), and a later, fuller answer (Full) fills the portrait. Accepts' rule ("a Thin write never overwrites
        // a Full one, but it does fill a group nobody has filled") must let the second answer complete the group
        // instead of finding it already sealed.
        TestScope.Fresh();
        var s1 = Staging.Rent();
        ref var thin = ref s1.Artists.RowFor(EntityId.ForGid(EntityKind.Artist, Gid(92)), Authority.Thin, (uint)ArtistFields.Name);
        thin.Name = s1.AddText("Credited Artist"u8);
        TestScope.CommitAndPublish(s1);

        var afterCredit = ArtistOf(92);
        Assert.True(afterCredit.Knows(ArtistFields.Name));
        Assert.False(afterCredit.Knows(ArtistFields.Identity));

        var s2 = Staging.Rent();
        ref var full = ref s2.Artists.RowFor(EntityId.ForGid(EntityKind.Artist, Gid(92)), Authority.Full, (uint)ArtistFields.Identity);
        full.Name = s2.AddText("Credited Artist"u8);
        full.Image = s2.AddText("https://i.scdn.co/image/full92"u8);
        TestScope.CommitAndPublish(s2);

        var afterFull = ArtistOf(92);
        Assert.True(afterFull.Knows(ArtistFields.Identity));
        Assert.Equal("https://i.scdn.co/image/full92", Entities.Strings.Resolve(Entities.Current.Artists.Image[afterFull.Slot]));
    }

    // ── `ThinArtist` (`Spotify.Decode.cs`, a track's billed-artist run — the actual chip-rail regression path) ───────

    [Fact]
    public void TrackV4s_billed_artist_run_marks_Name_but_never_Image_or_the_whole_Identity()
    {
        // `ThinArtist` reads exactly two fields off the wire (gid, name) and hands the caller the name back for the
        // credit line — it never touches a portrait, because a track payload's nested artist message has none to
        // give. This is `User.Facts.cs`'s `TopArtists()` input: every ranked artist starts life exactly this thin.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.TrackV4(new Md.Track
        {
            Gid = Bs(Gid(10)),
            Name = "Some Song",
            Artist = { new Md.Artist { Gid = Bs(Gid(11)), Name = "St-Amour" } },
        }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(11);
        Assert.True(artist.Knows(ArtistFields.Name));
        Assert.Equal("St-Amour", artist.Name);
        Assert.False(artist.Knows(ArtistFields.Image));
        Assert.False(artist.Knows(ArtistFields.Identity));
    }

    // ── `ArtistV4` (`Spotify.Decode.cs`, ext kind 8 — the sole route `Fetch.Routes.cs` registers for `Identity`) ─────

    [Fact]
    public void ArtistV4_with_a_portrait_marks_the_whole_Identity_group_known()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistV4(new Md.Artist
        {
            Gid = Bs(Gid(40)),
            Name = "roti.",
            PortraitGroup = Cover(30),
        }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(40);
        Assert.True(artist.Knows(ArtistFields.Identity));
        Assert.True(artist.Knows(ArtistFields.Image));
    }

    [Fact]
    public void ArtistV4_with_no_portrait_marks_Name_known_but_never_Image_or_the_whole_Identity()
    {
        // The producer-side hardening: `ArtistV4` used to declare `Known = Identity` before reading a single field,
        // so an answer that never carried extension 17 (no portrait on file) still sealed `Image` known with an
        // empty column behind it — unlike `TrackV4`, which guarantees its Image bit via the album-cover fallback,
        // this route has no fallback at all.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistV4(new Md.Artist
        {
            Gid = Bs(Gid(41)),
            Name = "No Portrait",
        }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(41);
        Assert.True(artist.Knows(ArtistFields.Name));
        Assert.Equal("No Portrait", artist.Name);
        Assert.False(artist.Knows(ArtistFields.Image));
        Assert.False(artist.Knows(ArtistFields.Identity));
    }

    // ── `ArtistUnion` (`Spotify.Decode.Artist.cs`) — today's live decoder for `queryArtistOverview` (`ArtistPage`,
    //    `overview: true`) and `queryNpvArtist` (`NpvArtist`, `overview: false`). Both share one fold; the bug was in
    //    the fold, not either wrapper. ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArtistPage_overview_with_an_avatar_marks_the_whole_Identity_group_known()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistPage(Json("""
            { "data": { "artistUnion": {
                "profile": { "name": "Overview Artist" },
                "visuals": { "avatarImage": { "sources": [ { "url": "https://i.scdn.co/image/avatar-overview" } ] } }
            } } }
            """), Utf8(UriOf(42)), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(42);
        Assert.True(artist.Knows(ArtistFields.Name));
        Assert.True(artist.Knows(ArtistFields.Image));
        Assert.True(artist.Knows(ArtistFields.Identity));
        Assert.Equal("https://i.scdn.co/image/avatar-overview", Text(artist.ImageId));
    }

    [Fact]
    public void ArtistPage_overview_with_no_avatar_still_marks_Identity_known_the_overview_speaks_for_the_group()
    {
        // UNCHANGED by the fix, and deliberately so: `overview: true` is the WHOLE page, and this decoder's own file
        // header states the rule for every one of its groups — "no pick … is an ANSWER". A `queryArtistOverview`
        // that names no avatar at all is the same kind of definitive fact as "no pick", not a hole to keep re-asking
        // about (`Fetch`'s `Asked` never clears on success anyway — re-asking would be a no-op forever regardless).
        // The bug was never in this branch; this fact exists so a future change to the `overview ||` gate cannot
        // silently start treating "no avatar" as unknown without a red test.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistPage(Json("""
            { "data": { "artistUnion": {
                "profile": { "name": "Overview No Avatar" }
            } } }
            """), Utf8(UriOf(43)), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(43);
        Assert.True(artist.Knows(ArtistFields.Name));
        Assert.True(artist.Knows(ArtistFields.Image));         // the overview spoke for it: verified, no photo
        Assert.True(artist.Knows(ArtistFields.Identity));
        Assert.True(artist.ImageId.IsEmpty);
    }

    [Fact]
    public void NpvArtist_thin_answer_with_an_avatar_and_header_fills_both_holes()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.NpvArtist(Json("""
            { "data": { "artistUnion": {
                "profile": { "name": "Npv Artist" },
                "visuals": {
                    "avatarImage": { "sources": [ { "url": "https://i.scdn.co/image/avatar-npv" } ] },
                    "headerImage": { "sources": [ { "url": "https://i.scdn.co/image/header-npv" } ] }
                }
            } } }
            """), Utf8(UriOf(44)), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(44);
        Assert.True(artist.Knows(ArtistFields.Name));
        Assert.True(artist.Knows(ArtistFields.Image));
        Assert.True(artist.Knows(ArtistFields.Identity));
        Assert.True(artist.Knows(ArtistFields.Header));
        Assert.Equal("https://i.scdn.co/image/avatar-npv", Text(artist.ImageId));
        Assert.Equal("https://i.scdn.co/image/header-npv", Text(artist.HeaderId));
    }

    [Fact]
    public void NpvArtist_thin_answer_with_no_visuals_never_claims_Image_Header_or_the_whole_Identity()
    {
        // THE actual regression: `queryNpvArtist` reads `visuals` (it is one of the sections this thinner answer
        // carries, per its own doc comment) and so CAN legitimately have nothing in it — unlike `overview: true`,
        // a thin NPV mention does not get to declare "verified, no photo" for the whole artist. Before the fix this
        // sealed `Image` known off `Name` alone regardless of `overview`, exactly like the chip-rail bug; `Header`
        // already had the correct `overview ||` guard and is asserted here only to pin that it still does.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.NpvArtist(Json("""
            { "data": { "artistUnion": {
                "profile": { "name": "Npv No Visuals" }
            } } }
            """), Utf8(UriOf(45)), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(45);
        Assert.True(artist.Knows(ArtistFields.Name));
        Assert.False(artist.Knows(ArtistFields.Image));        // the bug: this used to read true
        Assert.False(artist.Knows(ArtistFields.Header));
        Assert.False(artist.Knows(ArtistFields.Identity));
    }

    // -- the TRACK twin of the same bug (the blank covers on an artist's top tracks) ---------------------------------

    /// <summary><c>TrackFields.Identity</c> is a GROUP - Title|Artists|Album|Duration|Explicit|Image - and
    /// <c>Pathfinder.Stage</c>'s track arm sealed the whole of it on the strength of a NAME. A hit that carried no
    /// cover (a top-tracks entry, a thin search row) therefore walked out claiming an image it never saw, and
    /// <c>Fetch.NeedOf = wanted &amp; ~Known</c> saw no hole to fill: nothing ever asked for the artwork again, so the
    /// row painted a blank square for as long as it stayed cached. Exactly the chip-rail bug above, one table over.</summary>
    [Fact]
    public void A_track_hit_with_a_name_but_no_cover_does_not_claim_the_image_bit()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        string uri = EntityId.ForGid(EntityKind.Track, Gid(60)).Text;

        var node = new Spotify.Decode.Node { Uri = new StagedId(s.Text(uri)), Name = s.Text("Named, uncovered") };
        Spotify.Decode.Stage(s, in node, Authority.Full);
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse(uri.AsSpan()));
        Assert.True(track.Knows(TrackFields.Title));
        Assert.False(track.Knows(TrackFields.Image));          // the bug: this used to read true
        Assert.False(track.Knows(TrackFields.Identity));       // ...so the group is a hole the fetcher can still fill
    }

    /// <summary>...and a hit that DID carry a cover still seals the whole group, so a covered row is never re-asked.</summary>
    [Fact]
    public void A_track_hit_that_carried_a_cover_still_seals_the_identity_group()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        string uri = EntityId.ForGid(EntityKind.Track, Gid(61)).Text;

        var node = new Spotify.Decode.Node
        {
            Uri = new StagedId(s.Text(uri)),
            Name = s.Text("Named and covered"),
            Image = s.Text("https://i.scdn.co/image/ab67616d0000b273deadbeef"),
        };
        Spotify.Decode.Stage(s, in node, Authority.Full);
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse(uri.AsSpan()));
        Assert.True(track.Knows(TrackFields.Image));
        Assert.True(track.Knows(TrackFields.Identity));
    }
}
