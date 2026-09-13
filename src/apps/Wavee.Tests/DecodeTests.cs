// ── Wavee.Tests/DecodeTests.cs — the protobuf half of Spotify.Decode (Wave 2, owner E) ───────────────────────────
//
// Wave 2's gate for `Spotify/Spotify.Decode.cs`: every extended-metadata shape decoded into a `Staging`, committed
// the way one UI drain commits, and read back through the HANDLES the chapters' §7 lists name. A decoder that fills
// the wrong column is a page that paints the wrong thing four waves later, so nothing here asserts on the staged
// row — it asserts on `Track`, `Album`, `Artist`, `Show`, `Episode` after the commit.
//
// THE FIXTURES ARE BUILT, NOT CHECKED IN. `Wavee.Protocol.*` is the canonical encoder (Grpc.Tools compiles
// `Spotify/Protos/*.proto` into the app assembly), so a fixture built here and read by `ProtoReader` pins the
// decoder against the protocol itself rather than against a blob nobody can regenerate. It is also the only way to
// test a shape 0.2.9 never captured: `_old/Wavee.Tests/Fixtures` has playlist `.bin` and cluster `.json` files and
// no extended-metadata payload at all.
//
// The allocation fact at the bottom is the one CORE rule that cannot be read off the code (P1/P8): after warm-up a
// second decode of the same bytes into the same pooled `Staging` must allocate ZERO.

using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;
using Ca = Wavee.Protocol.ContentAgnostic;
using De = Wavee.Protocol.DescriptorExtension;
using Aa = Wavee.Protocol.AudioAttributes;
using Af = Wavee.Protocol.Audiofiles;
using Md = Wavee.Protocol.Metadata;
using Pl = Wavee.Protocol.Playlist;
using Wf = Wavee.Waveforms;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class DecodeTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A deterministic 16-byte gid. The decoder turns it into the canonical uri and the commit turns that
    /// back into the same packed id, so a test can ask for the row by the same gid it encoded.</summary>
    static byte[] Gid(byte seed)
    {
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return gid;
    }

    static ByteString Bs(byte[] bytes) => ByteString.CopyFrom(bytes);

    static Track TrackOf(byte seed) => Entities.Track(EntityId.ForGid(EntityKind.Track, Gid(seed)));
    static Album AlbumOf(byte seed) => Entities.Album(EntityId.ForGid(EntityKind.Album, Gid(seed)));
    static Artist ArtistOf(byte seed) => Entities.Artist(EntityId.ForGid(EntityKind.Artist, Gid(seed)));
    static Show ShowOf(byte seed) => Entities.Show(EntityId.ForGid(EntityKind.Show, Gid(seed)));
    static Episode EpisodeOf(byte seed) => Entities.Episode(EntityId.ForGid(EntityKind.Episode, Gid(seed)));

    static string UriOf(EntityKind kind, byte seed) => EntityId.ForGid(kind, Gid(seed)).Text;

    static Md.ImageGroup Cover(byte seed) => new()
    {
        Image =
        {
            new Md.Image { FileId = Bs(Gid((byte)(seed + 1))), Size = Md.Image.Types.Size.Small },
            new Md.Image { FileId = Bs(Gid(seed)), Size = Md.Image.Types.Size.Default },
        },
    };

    /// <summary>The TrackV4 every fact below reads: two artists, an album, an isrc and a duration.</summary>
    static byte[] TrackV4Bytes(long earliestLive = 0) => new Md.Track
    {
        Gid = Bs(Gid(10)),
        Name = "Cold Brew Chapters",
        Duration = 234_959,
        Explicit = true,
        Album = new Md.Album { Gid = Bs(Gid(20)), Name = "Let's work slow and easy", CoverGroup = Cover(30) },
        Artist = { new Md.Artist { Gid = Bs(Gid(40)), Name = "roti." }, new Md.Artist { Gid = Bs(Gid(50)), Name = "KIMMUSEUM" } },
        ExternalId = { new Md.ExternalId { Type = "isrc", Id = "GBKPL2500123" } },
        CanonicalUri = UriOf(EntityKind.Track, 60),
        EarliestLiveTimestamp = earliestLive,
    }.ToByteArray();

    // ── TrackV4 (ch 01 §7) ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackV4_fills_the_identity_group_at_full_authority()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.TrackV4(TrackV4Bytes(), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(10);
        Assert.True(track.Knows(TrackFields.Identity));
        Assert.Equal("Cold Brew Chapters", track.Title);
        Assert.Equal(234_959, track.DurationMs);
        Assert.True(track.IsExplicit);
        Assert.Equal(UriOf(EntityKind.Album, 20), track.Album.Uri.Text);
        // The DEFAULT render wins, not the first one the group lists (`Cover`, ported from 0.2.9's
        // `ExtendedMetadataSource.PickImage:577` — `if (img.Size == Size.Default) chosen ??= img;`). `Cover(30)` puts
        // the SMALL render (Gid 31) first and the DEFAULT one (Gid 30) second precisely so this fact pins the rule
        // rather than the wire order; `metadata.proto` is proto2, so an explicit `Size = DEFAULT` IS on the wire.
        Assert.Equal("https://i.scdn.co/image/" + Convert.ToHexStringLower(Gid(30)), Entities.Strings.Resolve(track.Album.ImageId));
    }

    [Fact]
    public void TrackV4_joins_the_credit_line_once_and_stages_every_artist_thin()
    {
        // ch 01 GAP 2: the credit line is ONE interned string computed at commit, and the per-artist click targets
        // come off the TrackArtists edge — both, never one or the other.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.TrackV4(TrackV4Bytes(), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(10);
        Assert.Equal("roti., KIMMUSEUM", Entities.Strings.Resolve(track.ArtistLineId));

        var artists = track.ArtistSlots;
        Assert.Equal(2, artists.Length);
        Assert.Equal("roti.", new Artist(artists[0]).Name);
        Assert.Equal("KIMMUSEUM", new Artist(artists[1]).Name);
        Assert.Equal(ArtistOf(40).Slot, artists[0]);
    }

    [Fact]
    public void TrackV4_isrc_and_canonical_uri_land_as_their_own_groups()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.TrackV4(TrackV4Bytes(), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(10);
        Assert.True(track.Knows(TrackFields.Isrc));
        Assert.Equal("GBKPL2500123", Entities.Strings.Resolve(track.IsrcId));
        Assert.True(track.Knows(TrackFields.Canonical));
        Assert.Equal(TrackOf(60).Slot, track.Canonical.Slot);
    }

    [Fact]
    public void An_unreleased_track_is_dimmed_with_a_date_and_a_released_one_is_not()
    {
        // ch 01 §7's not-yet-out dim: "unavailable" is a VERDICT, and `earliest_live_timestamp` is the only one
        // TrackV4 states. A row nobody ruled on reads as playable.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.TrackV4(TrackV4Bytes(earliestLive: 1_800_000_000), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(10);
        Assert.True(track.Knows(TrackFields.Availability));
        Assert.False(track.IsPlayable);
        Assert.Equal(1_800_000_000, track.AvailableAt);

        TestScope.Fresh();
        var plain = Staging.Rent();
        Spotify.Decode.TrackV4(TrackV4Bytes(), plain);
        TestScope.CommitAndPublish(plain);
        Assert.True(TrackOf(10).IsPlayable);
    }

    // ── AlbumV4 (ch 05 §7) ──────────────────────────────────────────────────────────────────────────────────────────

    static byte[] AlbumV4Bytes() => new Md.Album
    {
        Gid = Bs(Gid(20)),
        Name = "Let's work slow and easy",
        Type = Md.Album.Types.Type.Single,
        Label = "Interscope Records",
        Date = new Md.Date { Year = 2025, Month = 6, Day = 2 },
        CoverGroup = Cover(30),
        Artist = { new Md.Artist { Gid = Bs(Gid(40)), Name = "roti." } },
        Copyright =
        {
            new Md.Copyright { Type = Md.Copyright.Types.Type.C, Text = "© 2025 Interscope" },
            new Md.Copyright { Type = Md.Copyright.Types.Type.P, Text = "℗ 2025 Interscope" },
        },
        Disc =
        {
            new Md.Disc
            {
                Number = 1,
                Track =
                {
                    new Md.Track { Gid = Bs(Gid(70)), Name = "One", Number = 1, Duration = 1000, Artist = { new Md.Artist { Gid = Bs(Gid(40)), Name = "roti." } } },
                    new Md.Track { Gid = Bs(Gid(80)), Name = "Two", Number = 2, Duration = 2000, Explicit = true },
                },
            },
            new Md.Disc { Number = 2, Track = { new Md.Track { Gid = Bs(Gid(90)), Name = "Three", Number = 1, Duration = 3000 } } },
        },
    }.ToByteArray();

    [Fact]
    public void AlbumV4_lands_the_whole_tracklist_as_one_complete_run_with_disc_and_number()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumV4(AlbumV4Bytes(), s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf(20);
        var edges = Entities.Current.Edges.AlbumTracks;
        Assert.Equal(EdgeState.Complete, edges.State(album.Slot));
        Assert.Equal(3, edges.Count(album.Slot));

        var targets = edges.Targets(album.Slot);
        var payload = edges.Payload(album.Slot);
        Assert.Equal(TrackOf(70).Slot, targets[0]);
        Assert.Equal(new AlbumTrackEdge(1, 1), payload[0]);
        Assert.Equal(new AlbumTrackEdge(1, 2), payload[1]);
        Assert.Equal(new AlbumTrackEdge(2, 1), payload[2]);
        Assert.Equal(3, album.TrackCount);
        Assert.Equal(2, album.DiscCount);
    }

    [Fact]
    public void AlbumV4_fills_identity_release_and_the_publishing_panel()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumV4(AlbumV4Bytes(), s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf(20);
        Assert.True(album.Knows(AlbumFields.Card));
        Assert.Equal(AlbumKind.Single, album.Kind);
        Assert.Equal(2025, album.Year);
        Assert.Equal(2, album.DatePrecision);                 // a full Y-M-D
        Assert.Equal(Spotify.Decode.Seconds(2025, 6, 2), album.ReleaseAt);
        Assert.True(album.Knows(AlbumFields.Publishing));
        Assert.Equal("Interscope Records", Entities.Strings.Resolve(album.LabelId));
        Assert.Equal("© 2025 Interscope", Entities.Strings.Resolve(album.CopyrightId));
        Assert.Equal("℗ 2025 Interscope", Entities.Strings.Resolve(album.CourtesyId));
    }

    [Fact]
    public void An_album_that_named_no_disc_leaves_its_tracklist_unknown_and_not_empty()
    {
        // ch 03 §7: "empty" and "nobody asked" render differently, so a tracklist nobody answered for must not
        // settle Complete-with-zero.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumV4(new Md.Album { Gid = Bs(Gid(21)), Name = "Bare" }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        Assert.Equal(EdgeState.Unknown, Entities.Current.Edges.AlbumTracks.State(AlbumOf(21).Slot));
    }

    // ── ArtistV4 / ShowV4 / EpisodeV4 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArtistV4_fills_identity_and_the_runs_but_never_claims_the_overview()
    {
        // The rich half of an artist page is `artistOverview`'s. A kind-8 row that set `ArtistFields.Overview` would
        // stop the page ever asking for the real one.
        TestScope.Fresh();
        var proto = new Md.Artist
        {
            Gid = Bs(Gid(40)),
            Name = "roti.",
            PortraitGroup = Cover(30),
            TopTrack = { new Md.TopTracks { Country = "GB", Track = { new Md.Track { Gid = Bs(Gid(70)) }, new Md.Track { Gid = Bs(Gid(80)) } } } },
            AlbumGroup = { new Md.AlbumGroup { Album = { new Md.Album { Gid = Bs(Gid(20)) } } } },
            Related = { new Md.Artist { Gid = Bs(Gid(50)), Name = "KIMMUSEUM" } },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.ArtistV4(proto, s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(40);
        Assert.True(artist.Knows(ArtistFields.Identity));
        Assert.False(artist.Knows(ArtistFields.Bio));
        Assert.False(artist.Knows(ArtistFields.Stats));
        Assert.Equal(2, Entities.Current.Edges.ArtistPopular.Count(artist.Slot));
        Assert.Equal(1, Entities.Current.Edges.ArtistAlbums.Count(artist.Slot));
        Assert.Equal(1, Entities.Current.Edges.ArtistRelated.Count(artist.Slot));
    }

    [Fact]
    public void ShowV4_fills_the_header_and_leaves_its_episode_list_partial()
    {
        // ch 09 §7: every transport that serves a show's episodes pages them, so one answer is a PAGE — never the
        // whole list, which is what would stop the page asking for the rest.
        TestScope.Fresh();
        var proto = new Md.Show
        {
            Gid = Bs(Gid(100)),
            Name = "The Wavee Hour",
            Publisher = "Wavee",
            Description = "A show about a decoder.",
            CoverImage = Cover(30),
            Episode = { new Md.Episode { Gid = Bs(Gid(110)) }, new Md.Episode { Gid = Bs(Gid(120)) } },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.ShowV4(proto, s);
        TestScope.CommitAndPublish(s);

        var show = ShowOf(100);
        Assert.True(show.Knows(ShowFields.Identity | ShowFields.About));
        Assert.Equal("Wavee", Entities.Strings.Resolve(show.PublisherId));
        Assert.Equal(EdgeState.Partial, Entities.Current.Edges.ShowEpisodes.State(show.Slot));
        Assert.Equal(2, Entities.Current.Edges.ShowEpisodes.Count(show.Slot));
    }

    [Fact]
    public void EpisodeV4_stages_its_show_thin_so_the_row_can_name_it_before_the_fetch()
    {
        TestScope.Fresh();
        var proto = new Md.Episode
        {
            Gid = Bs(Gid(110)),
            Name = "Episode One",
            Duration = 3_600_000,
            Description = "The first one.",
            PublishTime = new Md.Date { Year = 2026, Month = 1, Day = 5 },
            CoverImage = Cover(30),
            Show = new Md.Show { Gid = Bs(Gid(100)), Name = "The Wavee Hour", Publisher = "Wavee" },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.EpisodeV4(proto, s);
        TestScope.CommitAndPublish(s);

        var episode = EpisodeOf(110);
        Assert.True(episode.Knows(EpisodeFields.Identity));
        Assert.Equal(3_600_000, episode.DurationMs);
        Assert.Equal(Spotify.Decode.Seconds(2026, 1, 5), episode.PublishedAt);
        Assert.Equal(ShowOf(100).Slot, episode.Show.Slot);
        Assert.Equal("The Wavee Hour", ShowOf(100).Title);
    }

    // ── the trait projectors ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AudioAttributes_encodes_both_rings_one_based_so_zero_still_means_unknown()
    {
        // ch 04 DATA GAPS asks owner M for exact `KeyLabel(byte)` / `CamelotLabel(byte)` tables over closed rings.
        // This is the encoding those tables have to agree with — and the reason it is 1-based: a 0-based pitch class
        // would make every track that has a tempo and no key read as "C".
        TestScope.Fresh();
        var proto = new Aa.AudioAttributes
        {
            Tempo = 128.4,
            Key = new Aa.MusicalKey { Name = "F#", Mode = 2, Camelot = new Aa.Camelot { Code = "11B", Color = "#56d9f8" } },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.AudioAttributes(proto, Encoding.UTF8.GetBytes(UriOf(EntityKind.Track, 10)), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(10);
        Assert.True(track.Knows(TrackFields.Audio));
        Assert.Equal(1284, track.Tempo);                       // ×10
        Assert.Equal(7, track.Key);                            // F# = pitch class 6, one-based
        Assert.Equal(22, track.Camelot);                       // 11B = (11-1)*2 + 1 + 1
        Assert.Equal(0xFF56D9F8u, track.CamelotColor);
        Assert.Equal(0, TrackOf(11).Key);                      // an untouched row still reads "unknown"
    }

    [Fact]
    public void Descriptors_keep_wire_order_and_stop_at_six()
    {
        // Wire order is descending weight, so the first few ARE the track's identity — no re-sorting, and the tail
        // is noise for a chip bar (`DescriptorProjector.MaxTagsPerTrack`).
        TestScope.Fresh();
        var data = new De.ExtensionDescriptorData();
        for (int i = 0; i < 9; i++)
            data.Descriptors.Add(new De.ExtensionDescriptor { Text = "tag" + i, DisplayName = "Tag " + i, Weight = 1f - i * 0.1f });

        var s = Staging.Rent();
        Spotify.Decode.Descriptors(data.ToByteArray(), Encoding.UTF8.GetBytes(UriOf(EntityKind.Track, 10)), s);
        TestScope.CommitAndPublish(s);

        var tags = TrackOf(10).Tags;
        Assert.Equal(Spotify.Decode.MaxTagsPerTrack, tags.Length);
        Assert.Equal("Tag 0", Entities.Strings.Resolve(tags[0]));
        Assert.Equal("Tag 5", Entities.Strings.Resolve(tags[5]));
    }

    [Fact]
    public void A_descriptor_answer_with_nothing_in_it_is_still_an_answer()
    {
        // Finding 27: a descriptor message with no descriptors serializes to zero bytes, so "answered with nothing"
        // and "answered with an empty list" are the same thing on the wire — and both mean the track has no tags.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.Descriptors(default, Encoding.UTF8.GetBytes(UriOf(EntityKind.Track, 10)), s);
        TestScope.CommitAndPublish(s);

        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackTags.State(TrackOf(10).Slot));
        Assert.Equal(0, Entities.Current.Edges.TrackTags.Count(TrackOf(10).Slot));
    }

    [Fact]
    public void A_descriptor_answer_marks_the_tags_group_known_so_the_planner_stops_asking()
    {
        // The chips land on the EDGE, but `wanted & ~known` is a question about the ROW (P3) — so kind 6 also writes
        // `TrackFields.Tags`. Without this arm the group was never known and every page mount re-requested kind 6 for
        // the life of the session, whether the track had descriptors or not.
        TestScope.Fresh();
        var uri = Encoding.UTF8.GetBytes(UriOf(EntityKind.Track, 10));
        Assert.False(TrackOf(10).Knows(TrackFields.Tags));

        var data = new De.ExtensionDescriptorData();
        data.Descriptors.Add(new De.ExtensionDescriptor { Text = "chill", DisplayName = "Chill", Weight = 1f });
        var s = Staging.Rent();
        Spotify.Decode.Descriptors(data.ToByteArray(), uri, s);
        TestScope.CommitAndPublish(s);
        Assert.True(TrackOf(10).Knows(TrackFields.Tags));

        // …and a track with NO descriptors knows it just as firmly (finding 27).
        var empty = Staging.Rent();
        Spotify.Decode.Descriptors(default, Encoding.UTF8.GetBytes(UriOf(EntityKind.Track, 11)), empty);
        TestScope.CommitAndPublish(empty);
        Assert.True(TrackOf(11).Knows(TrackFields.Tags));
    }

    [Fact]
    public void PlayCount_reads_field_three_and_a_zero_is_not_a_count()
    {
        TestScope.Fresh();
        var uri = Encoding.UTF8.GetBytes(UriOf(EntityKind.Track, 10));
        var s = Staging.Rent();
        Spotify.Decode.PlayCount(Varint(3, 147_606), uri, s);
        TestScope.CommitAndPublish(s);

        Assert.True(TrackOf(10).Knows(TrackFields.PlayCount));
        Assert.Equal(147_606u, TrackOf(10).PlayCount);

        TestScope.Fresh();
        var zero = Staging.Rent();
        Spotify.Decode.PlayCount(Varint(3, 0), uri, zero);
        TestScope.CommitAndPublish(zero);
        Assert.False(TrackOf(10).Knows(TrackFields.PlayCount));

        // One varint field, hand-encoded: kind 185 has no .proto in the tree and is read by field number.
        static byte[] Varint(int field, ulong value)
        {
            var bytes = new List<byte> { (byte)(field << 3) };
            while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; }
            bytes.Add((byte)value);
            return bytes.ToArray();
        }
    }

    [Fact]
    public void Publishing_is_thin_so_a_getAlbum_answer_always_wins()
    {
        // 0.2.9's `PublishingProjector` was "additive only": kind 183 carries no label and must never blank one.
        // D16 says that in one word — a Thin write fills a group nobody has filled and never overwrites a Full one.
        TestScope.Fresh();
        var full = Staging.Rent();
        Spotify.Decode.AlbumV4(AlbumV4Bytes(), full);
        TestScope.CommitAndPublish(full);

        var trait = new Ca.PublishingMetadataTrait
        {
            Date = new Ca.PublishingMetadataTrait.Types.Date { Year = 1999, Month = 1, Day = 1 },
            Copyright = { "© 1999 Someone Else" },
        }.ToByteArray();

        var thin = Staging.Rent();
        Spotify.Decode.Publishing(trait, Encoding.UTF8.GetBytes(UriOf(EntityKind.Album, 20)), thin);
        TestScope.CommitAndPublish(thin);

        var album = AlbumOf(20);
        Assert.Equal("Interscope Records", Entities.Strings.Resolve(album.LabelId));
        Assert.Equal("© 2025 Interscope", Entities.Strings.Resolve(album.CopyrightId));
        Assert.Equal(2025, album.Year);
    }

    [Fact]
    public void Publishing_fills_an_album_nobody_has_answered_for()
    {
        TestScope.Fresh();
        var trait = new Ca.PublishingMetadataTrait
        {
            Date = new Ca.PublishingMetadataTrait.Types.Date { Year = 2019 },
            Copyright = { "© 2019 Label", "℗ 2019 Label" },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.Publishing(trait, Encoding.UTF8.GetBytes(UriOf(EntityKind.Album, 22)), s);
        TestScope.CommitAndPublish(s);

        var album = AlbumOf(22);
        Assert.True(album.Knows(AlbumFields.Publishing));
        Assert.Equal("© 2019 Label", Entities.Strings.Resolve(album.CopyrightId));
        Assert.Equal("℗ 2019 Label", Entities.Strings.Resolve(album.CourtesyId));
        Assert.Equal(2019, album.Year);
        Assert.Equal(0, album.DatePrecision);                  // a bare year stays a year
    }

    [Fact]
    public void VideoAssociations_lands_the_counterpart_and_a_negative_is_a_real_answer()
    {
        TestScope.Fresh();
        var proto = new Xm.VideoAssociations
        {
            Association = new Xm.Association
            {
                AssociatedUri = UriOf(EntityKind.Track, 130),
                Files = new Xm.VideoFileGroup { File = { new Xm.VideoFile { FileId = Bs(Gid(140)), Width = 1280, Height = 720 } } },
            },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.VideoAssociations(proto, Encoding.UTF8.GetBytes(UriOf(EntityKind.Track, 10)), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(10);
        Assert.True(track.Knows(TrackFields.Video));
        Assert.True(track.HasVideo);
        Assert.Equal(TrackOf(130).Slot, track.VideoCounterpart.Slot);

        TestScope.Fresh();
        var none = Staging.Rent();
        Spotify.Decode.VideoAssociations(default, Encoding.UTF8.GetBytes(UriOf(EntityKind.Track, 10)), none);
        TestScope.CommitAndPublish(none);
        Assert.True(TrackOf(10).Knows(TrackFields.Video));      // the row now KNOWS there is none
        Assert.False(TrackOf(10).HasVideo);
    }

    [Fact]
    public void VisualIdentity_tints_by_the_image_the_payload_names_and_not_by_the_entity()
    {
        // That pairing is the whole point of the trait: one track's answer also tints its album's grid card and its
        // playlist's hero, because they show the same cover.
        TestScope.Fresh();
        const string url = "https://i.scdn.co/image/ab67616d00001e02fb61774eb00000001";
        var proto = new Ca.VisualIdentityTrait
        {
            VisualIdentity = new Ca.VisualIdentity
            {
                Images = { new Ca.ImageEntry { Image = new Ca.ImageRef { Url = url }, Size = 300 } },
                Colors = new Ca.ColorSet
                {
                    Base = new Ca.ColorScheme
                    {
                        BackgroundBase = new Ca.Rgba { R = 16, G = 16, B = 64, A = 255 },
                        TextBase = new Ca.Rgba { R = 255, G = 255, B = 255, A = 255 },
                    },
                    Flat = new Ca.Rgba { R = 172, G = 184, B = 245, A = 255 },
                },
            },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.VisualIdentity(proto, s);
        TestScope.CommitAndPublish(s);

        Assert.True(Palette.TryScheme(url.AsSpan(), lightTheme: false, out var scheme));
        Assert.Equal(0xFF101040u, scheme.BackgroundBase);       // base.background_base, NOT colors.flat
    }

    // ── kind 5: the format ladder, on the DERIVED audio entity (FLAC plan §5.1, §5.2) ───────────────────────────────

    /// <summary>A 20-byte file id — the length the decoder uses to tell a rung the account was OFFERED from one the
    /// wire merely named (<c>Decode.FileIdBytes</c>).</summary>
    static byte[] FileId(byte seed)
    {
        var id = new byte[Spotify.Decode.FileIdBytes];
        for (int i = 0; i < id.Length; i++) id[i] = (byte)(seed + i);
        return id;
    }

    /// <summary>An <c>AudioFilesExtensionResponse</c> with the rungs in the order given, each with a real file id. The
    /// normalization params are on the message on purpose: they are the OPENER's, and nothing here may stage them.</summary>
    static byte[] AudioFilesBytes(params (Md.AudioFile.Types.Format Format, int Bitrate)[] rungs)
    {
        var response = new Af.AudioFilesExtensionResponse
        {
            AudioId = Bs(Gid(99)),
            DefaultFileNormalizationParams = new Af.NormalizationParams { LoudnessDb = -8.2f, TruePeakDb = -0.4f },
        };
        foreach ((Md.AudioFile.Types.Format format, int bitrate) in rungs)
            response.Files.Add(new Af.ExtendedAudioFile
            {
                File = new Md.AudioFile { FileId = Bs(FileId((byte)format)), Format = format },
                AverageBitrate = bitrate,
            });
        return response.ToByteArray();
    }

    static StagedId StagedTrackId(byte seed) => new(EntityId.ForGid(EntityKind.Track, Gid(seed)));

    [Fact]
    public void AudioFiles_lands_the_ladder_in_wire_order_with_both_lossless_bits()
    {
        // The one answer that says whether an account can have lossless (plan §5.1): TRACK_V4 lists Ogg and AAC and
        // never a FLAC row, so this payload IS the entitlement. Wire order is kept — it is the order the account was
        // offered the rungs in, and the drawer sorts its own copy by bitrate (§5.3).
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AudioFiles(
            AudioFilesBytes((Md.AudioFile.Types.Format.OggVorbis320, 320_000),
                            (Md.AudioFile.Types.Format.FlacFlac, 705_000),
                            (Md.AudioFile.Types.Format.FlacFlac24Bit, 1_411_000)),
            StagedTrackId(10), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(10);
        Assert.True(track.Knows(TrackFields.Files));
        Assert.True(track.IsLossless);
        Assert.True(track.IsLossless24);

        var ladder = track.Formats;
        Assert.Equal(3, ladder.Length);
        Assert.Equal(new FormatEdge(2, 320), ladder[0]);
        Assert.Equal(new FormatEdge(Spotify.Decode.FlacFormat, 705), ladder[1]);
        Assert.Equal(new FormatEdge(Spotify.Decode.Flac24Format, 1411), ladder[2]);
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackFormats.State(track.Slot));
        // "The row is the payload": a rung is not a row anywhere, so the relation carries no targets (Edges.cs).
        var targets = Entities.Current.Edges.TrackFormats.Targets(track.Slot);
        Assert.Equal(3, targets.Length);
        for (int i = 0; i < targets.Length; i++) Assert.Equal(Table.None, targets[i]);
    }

    [Fact]
    public void An_ogg_only_ladder_leaves_both_lossless_bits_clear_and_still_answers_the_group()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AudioFiles(
            AudioFilesBytes((Md.AudioFile.Types.Format.OggVorbis96, 96_000),
                            (Md.AudioFile.Types.Format.OggVorbis160, 160_000)),
            StagedTrackId(11), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(11);
        Assert.True(track.Knows(TrackFields.Files));
        Assert.False(track.IsLossless);
        Assert.False(track.IsLossless24);
        Assert.Equal(2, track.Formats.Length);
    }

    [Fact]
    public void An_audio_files_answer_with_no_files_is_still_an_answer()
    {
        // An account without lossless in this market gets a ladder with no FLAC rung — or nothing at all — and both
        // mean "no FLAC for you". Empty run, group known, planner stops asking (finding 27's rule, as for kind 6).
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AudioFiles(AudioFilesBytes(), StagedTrackId(12), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(12);
        Assert.True(track.Knows(TrackFields.Files));
        Assert.False(track.IsLossless);
        Assert.Equal(0, track.Formats.Length);
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackFormats.State(track.Slot));
    }

    [Fact]
    public void A_rung_the_wire_named_without_a_file_id_is_not_a_rung()
    {
        // A format with no file id cannot be opened, so it was never offered. A stated bitrate of 0 IS kept, though:
        // "the wire did not say" sorts last in the drawer rather than being invented.
        TestScope.Fresh();
        var response = new Af.AudioFilesExtensionResponse
        {
            Files =
            {
                new Af.ExtendedAudioFile
                {
                    File = new Md.AudioFile { Format = Md.AudioFile.Types.Format.FlacFlac },
                    AverageBitrate = 705_000,
                },
                new Af.ExtendedAudioFile
                {
                    File = new Md.AudioFile { FileId = Bs(FileId(7)), Format = Md.AudioFile.Types.Format.OggVorbis320 },
                },
            },
        };

        var s = Staging.Rent();
        Spotify.Decode.AudioFiles(response.ToByteArray(), StagedTrackId(13), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(13);
        Assert.False(track.IsLossless);
        Assert.Equal(1, track.Formats.Length);
        Assert.Equal(new FormatEdge(2, 0), track.Formats[0]);
    }

    [Fact]
    public void TrackV4_keeps_the_audio_key_and_a_payload_without_one_seals_the_files_group()
    {
        // §5.2: the uuid is the ONLY route to the ladder and it rides this payload alone. A TrackV4 that names none has
        // no audio entity behind it, so the group is answered EMPTY here — the planner cannot tell "never had one" from
        // "not fetched yet" and would re-derive the same impossibility on every page mount.
        TestScope.Fresh();
        byte[] uuid = Gid(77);
        var s = Staging.Rent();
        Spotify.Decode.TrackV4(new Md.Track
        {
            Gid = Bs(Gid(10)),
            Name = "Keyed",
            OriginalAudio = new Md.Audio { Uuid = Bs(uuid) },
        }.ToByteArray(), s);
        Spotify.Decode.TrackV4(new Md.Track { Gid = Bs(Gid(20)), Name = "Keyless" }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);

        var keyed = TrackOf(10);
        Assert.Equal(Base62.ReadBytes(uuid), keyed.OriginalAudio);
        Assert.False(keyed.Knows(TrackFields.Files));                   // the KEY landed; the ladder did not
        // …and the uri derived from it is the one `Spotify.Audio` POSTs per open: base62 over the same 16 bytes.
        Span<char> uri = stackalloc char[Fetch.AudioUriChars];
        Assert.Equal(Fetch.AudioUriChars, Fetch.WriteAudioUri(keyed.OriginalAudio, uri));
        Assert.Equal("spotify:audio:" + UriOf(EntityKind.Track, 77)["spotify:track:".Length..], new string(uri));

        var keyless = TrackOf(20);
        Assert.Equal(UInt128.Zero, keyless.OriginalAudio);
        Assert.True(keyless.Knows(TrackFields.Files));                  // sealed: there is no ladder to fetch, ever
        Assert.False(keyless.IsLossless);
    }

    [Fact]
    public void A_thin_answer_cannot_blank_the_audio_key_a_full_one_learned()
    {
        // The key is not a rendered value and no two answers disagree about it — but a search hit and a playlist item
        // carry none, and neither may erase the one route to the ladder (Track.cs's commit note).
        TestScope.Fresh();
        byte[] uuid = Gid(88);
        var full = Staging.Rent();
        Spotify.Decode.TrackV4(new Md.Track
        {
            Gid = Bs(Gid(10)),
            Name = "Keyed",
            OriginalAudio = new Md.Audio { Uuid = Bs(uuid) },
        }.ToByteArray(), full);
        TestScope.CommitAndPublish(full);

        var thin = Staging.Rent();
        ref var row = ref thin.Tracks.Add();
        row.Id = EntityId.ForGid(EntityKind.Track, Gid(10));
        row.Title = thin.Text("A search hit");
        row.Known = (uint)TrackFields.Identity;
        row.Authority = Authority.Thin;
        TestScope.CommitAndPublish(thin);

        Assert.Equal(Base62.ReadBytes(uuid), TrackOf(10).OriginalAudio);
    }

    [Fact]
    public void The_envelope_routes_kind_five_back_to_the_track_that_asked_for_the_audio_uri()
    {
        // Why the fold takes a track explicitly: the envelope's entity_uri is `spotify:audio:<uuid>`, an entity with no
        // row and no table, and the uuid is NOT the track's gid. The batch that asked is the only witness (§5.2) — and
        // with no batch in hand the kind is skipped rather than guessed at.
        TestScope.Fresh();
        EntityId track = EntityId.ForGid(EntityKind.Track, Gid(10));
        Span<char> buffer = stackalloc char[Fetch.AudioUriChars];
        Fetch.WriteAudioUri(Base62.ReadBytes(Gid(99)), buffer);
        string audioUri = new(buffer);

        byte[] response = new Xm.BatchedExtensionResponse
        {
            ExtendedMetadata =
            {
                new Xm.EntityExtensionDataArray
                {
                    ExtensionKind = Xm.ExtensionKind.AudioFiles,
                    ExtensionData =
                    {
                        new Xm.EntityExtensionData
                        {
                            Header = new Xm.EntityExtensionDataHeader { StatusCode = 200 },
                            EntityUri = audioUri,
                            ExtensionData = new Google.Protobuf.WellKnownTypes.Any
                            {
                                TypeUrl = "type.googleapis.com/spotify.extendedmetadata.audiofiles.AudioFilesExtensionResponse",
                                Value = ByteString.CopyFrom(AudioFilesBytes((Md.AudioFile.Types.Format.FlacFlac, 705_000))),
                            },
                        },
                    },
                },
            },
        }.ToByteArray();

        var blind = Staging.Rent();
        Spotify.Decode.ExtendedMetadata(response, blind);
        TestScope.CommitAndPublish(blind);
        Assert.False(TrackOf(10).Knows(TrackFields.Files));

        var batch = new FetchBatch
        {
            Ids = [track],
            Text = [audioUri],
            Slots = [Table.None],
            Count = 1,
            Kind = EntityKind.Track,
            Wanted = (uint)TrackFields.Files,
            Extension = Fetch.AudioFilesKind,
        };
        Assert.Equal(track, batch.IdFor(Encoding.UTF8.GetBytes(audioUri)));
        // A uri nobody in this batch asked for is not a row: the caller must drop the entity, never guess at one.
        Assert.True(batch.IdFor(Encoding.UTF8.GetBytes("spotify:audio:0000000000000000000000")).IsEmpty);

        var s = Staging.Rent();
        Spotify.Decode.ExtendedMetadata(response, s, batch);
        TestScope.CommitAndPublish(s);

        Assert.True(TrackOf(10).Knows(TrackFields.Files));
        Assert.True(TrackOf(10).IsLossless);
    }

    // ── the envelope ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_extended_metadata_envelope_fans_out_by_kind_and_drops_a_failed_entity()
    {
        TestScope.Fresh();
        var response = new Xm.BatchedExtensionResponse
        {
            ExtendedMetadata =
            {
                Array(Xm.ExtensionKind.TrackV4, UriOf(EntityKind.Track, 10), TrackV4Bytes(), status: 200),
                Array(Xm.ExtensionKind.TrackV4, UriOf(EntityKind.Track, 150), TrackV4Bytes(), status: 404),
                Array(Xm.ExtensionKind.AlbumV4, UriOf(EntityKind.Album, 20), AlbumV4Bytes(), status: 200),
            },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.ExtendedMetadata(response, s);
        TestScope.CommitAndPublish(s);

        Assert.True(TrackOf(10).Knows(TrackFields.Identity));
        Assert.True(AlbumOf(20).Knows(AlbumFields.Identity));
        Assert.Equal(3, Entities.Current.Edges.AlbumTracks.Count(AlbumOf(20).Slot));
        Assert.False(TrackOf(150).Knows(TrackFields.Identity));

        static Xm.EntityExtensionDataArray Array(Xm.ExtensionKind kind, string uri, byte[] payload, int status) => new()
        {
            ExtensionKind = kind,
            ExtensionData =
            {
                new Xm.EntityExtensionData
                {
                    Header = new Xm.EntityExtensionDataHeader { StatusCode = status },
                    EntityUri = uri,
                    ExtensionData = new Google.Protobuf.WellKnownTypes.Any
                    {
                        TypeUrl = "type.googleapis.com/spotify.metadata." + kind,
                        Value = ByteString.CopyFrom(payload),
                    },
                },
            },
        };
    }

    // ── the manifest id and the video's shape (G-056, G-057, G-058) ─────────────────────────────────────────────────

    static string HexOf(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void TrackV4_keeps_the_first_renditions_gid_as_the_manifest_id_and_a_later_answer_never_clears_it()
    {
        TestScope.Fresh();
        var withVideo = new Md.Track
        {
            Gid = Bs(Gid(10)),
            Name = "a self-contained music video",
            OriginalVideo = { new Md.Video { Gid = Bs(Gid(77)) }, new Md.Video { Gid = Bs(Gid(88)) } },
        }.ToByteArray();
        var s = Staging.Rent();
        Spotify.Decode.TrackV4(withVideo, s);
        TestScope.CommitAndPublish(s);
        Assert.Equal(HexOf(Gid(77)), Entities.Strings.Resolve(TrackOf(10).VideoGidId));

        // A TrackV4 that names no rendition (a re-answer, a thinner payload) is not an answer about the key.
        var again = Staging.Rent();
        Spotify.Decode.TrackV4(TrackV4Bytes(), again);
        TestScope.CommitAndPublish(again);
        Assert.Equal(HexOf(Gid(77)), Entities.Strings.Resolve(TrackOf(10).VideoGidId));
    }

    [Fact]
    public void Renditions_with_no_counterpart_are_still_a_video_and_the_largest_states_its_shape()
    {
        // A self-contained music-video track answers kind 99 with FILES and no `associated_uri` (0.2.9's verdict:
        // files > 0 or a counterpart). Reading only the counterpart hid the film lane for exactly the tracks that ARE videos.
        TestScope.Fresh();
        var proto = new Xm.VideoAssociations
        {
            Association = new Xm.Association
            {
                Files = new Xm.VideoFileGroup
                {
                    File =
                    {
                        new Xm.VideoFile { FileId = Bs(Gid(141)), Width = 640, Height = 360 },
                        new Xm.VideoFile { FileId = Bs(Gid(142)), Width = 1080, Height = 1920 },
                        new Xm.VideoFile { FileId = Bs(Gid(143)), Width = 720, Height = 1280 },
                    },
                },
            },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.VideoAssociations(proto, Encoding.UTF8.GetBytes(UriOf(EntityKind.Track, 11)), s);
        TestScope.CommitAndPublish(s);

        var track = TrackOf(11);
        Assert.True(track.HasVideo);
        Assert.Equal(Table.None, track.VideoCounterpart.Slot);
        Assert.Equal(1080, track.VideoWidth);                  // a vertical video opens vertical, not 16:9
        Assert.Equal(1920, track.VideoHeight);
        Assert.False(track.IsLiveVideo);                       // the default is FINITE; only the video host flips it
    }

    // ── the trait kinds the decoder used to drop (G-044) ────────────────────────────────────────────────────────────

    static byte[] UriBytes(EntityKind kind, byte seed) => Encoding.UTF8.GetBytes(UriOf(kind, seed));

    [Fact]
    public void Credits_land_in_the_servers_order_with_the_linked_artist_and_the_unlinked_name()
    {
        TestScope.Fresh();
        var proto = new Ca.CreditsTrait
        {
            Rows =
            {
                new Ca.CreditRow { Name = "roti.", Role = "Main Artist", ArtistUri = UriOf(EntityKind.Artist, 40),
                                   Group = new Ca.CreditRow.Types.Group { Name = "Performers" } },
                new Ca.CreditRow { Name = "A Session Cellist", Role = "Cello",
                                   Group = new Ca.CreditRow.Types.Group { Name = "Performers" } },
                new Ca.CreditRow { Role = "a row that names nobody" },
            },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.Credits(proto, UriBytes(EntityKind.Track, 12), s);
        TestScope.CommitAndPublish(s);

        var credits = Entities.Current.Edges.TrackCredits;
        int track = TrackOf(12).Slot;
        Assert.Equal(EdgeState.Complete, credits.State(track));
        Assert.Equal(2, credits.Count(track));
        Assert.Equal(ArtistOf(40).Slot, credits.Targets(track)[0]);
        Assert.Equal(Table.None, credits.Targets(track)[1]);          // an unlinked contributor keeps its row
        var cellist = credits.Payload(track)[1];
        Assert.Equal("A Session Cellist", Entities.Strings.Resolve(cellist.Name));
        Assert.Equal("Cello", Entities.Strings.Resolve(cellist.Role));
        Assert.Equal("Performers", Entities.Strings.Resolve(cellist.Group));

        // "This track has no credits" is an answer: the relation settles Complete and the drawer stops asking.
        var none = Staging.Rent();
        Spotify.Decode.Credits(default, UriBytes(EntityKind.Track, 13), none);
        TestScope.CommitAndPublish(none);
        Assert.Equal(EdgeState.Complete, credits.State(TrackOf(13).Slot));
        Assert.Equal(0, credits.Count(TrackOf(13).Slot));
    }

    [Fact]
    public void Recommended_playlists_are_playlists_only_and_stop_at_twelve()
    {
        TestScope.Fresh();
        var proto = new Xm.RecommendedPlaylists();
        proto.Recommendation.Add(new Xm.RecommendedPlaylists.Types.Item { Uri = UriOf(EntityKind.Album, 1) });   // not a playlist
        for (byte i = 0; i < 14; i++)
            proto.Recommendation.Add(new Xm.RecommendedPlaylists.Types.Item { Uri = UriOf(EntityKind.Playlist, (byte)(100 + i)) });

        var s = Staging.Rent();
        Spotify.Decode.RecommendedPlaylists(proto.ToByteArray(), UriBytes(EntityKind.Album, 20), s);
        TestScope.CommitAndPublish(s);

        var recommendations = Entities.Current.Edges.AlbumRecommendations;
        int album = AlbumOf(20).Slot;
        Assert.Equal(Spotify.Decode.MaxRecommendedPlaylists, recommendations.Count(album));
        Assert.Equal(Entities.Playlist(EntityId.ForGid(EntityKind.Playlist, Gid(100))).Slot, recommendations.Targets(album)[0]);
    }

    [Fact]
    public void An_audio_association_lands_the_recordings_audio_rendition_as_a_version()
    {
        TestScope.Fresh();
        var proto = new Xm.VideoAssociations
        {
            Association = new Xm.Association { AssociatedUri = UriOf(EntityKind.Track, 31) },
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.AudioAssociations(proto, UriBytes(EntityKind.Track, 30), s);
        TestScope.CommitAndPublish(s);

        var versions = Entities.Current.Edges.TrackVersions;
        int track = TrackOf(30).Slot;
        Assert.Equal(TrackOf(31).Slot, Assert.Single(versions.Targets(track).ToArray()));
        Assert.Equal(TrackVersionKind.Audio, versions.Payload(track)[0].Kind);
    }

    [Fact]
    public void A_waveform_is_reduced_once_to_220_columns_with_each_band_walked_across_its_own_length()
    {
        TestScope.Fresh();
        // band_low is LONGER than the others on the wire; a spike at the very end of each band must land in the LAST
        // column for all three — one shared cursor would put the shorter bands' spikes early.
        var low = new byte[12_886];
        var mid = new byte[12_466];
        var high = new byte[12_466];
        low[^1] = 200; mid[^1] = 200; high[^1] = 200;
        low[0] = 10;
        var proto = new Wf.ThreeBandWaveforms
        {
            SampleRate = 44_100, HopMs = 20, BandLow = Bs(low), BandMid = Bs(mid), BandHigh = Bs(high),
        }.ToByteArray();

        var s = Staging.Rent();
        Spotify.Decode.Waveform(proto, UriBytes(EntityKind.Track, 40), s);
        TestScope.CommitAndPublish(s);

        var columns = Entities.Current.Edges.TrackWaveform.Payload(TrackOf(40).Slot);
        Assert.Equal(Spotify.Decode.WaveformColumns, columns.Length);
        Assert.Equal(255, columns[^1]);                                    // the loudest column is the ceiling
        Assert.Equal((byte)((10 * 255 + 300) / 600), columns[0]);          // normalised against the track's own peak
        Assert.Equal(0, columns[110]);

        // Silence is an answer too — an empty waveform, Complete.
        var quiet = Staging.Rent();
        Spotify.Decode.Waveform(new Wf.ThreeBandWaveforms { BandLow = Bs(new byte[64]) }.ToByteArray(), UriBytes(EntityKind.Track, 41), quiet);
        TestScope.CommitAndPublish(quiet);
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackWaveform.State(TrackOf(41).Slot));
        Assert.Equal(0, Entities.Current.Edges.TrackWaveform.Count(TrackOf(41).Slot));
    }

    /// <summary>The kind-15 body as captured: every scalar in a wrapper, images repeated.</summary>
    static byte[] ProfileBody(string username, string name, params (int W, int H, string Url)[] images)
    {
        using var stream = new MemoryStream();
        var o = new CodedOutputStream(stream);
        o.WriteTag(1, WireFormat.WireType.LengthDelimited); o.WriteBytes(Wrapped(1, username));
        o.WriteTag(2, WireFormat.WireType.LengthDelimited); o.WriteBytes(Wrapped(1, name));
        foreach (var (w, h, url) in images)
        {
            using var image = new MemoryStream();
            var io = new CodedOutputStream(image);
            io.WriteTag(1, WireFormat.WireType.Varint); io.WriteInt32(w);
            io.WriteTag(2, WireFormat.WireType.Varint); io.WriteInt32(h);
            io.WriteTag(3, WireFormat.WireType.LengthDelimited); io.WriteString(url);
            io.Flush();
            o.WriteTag(3, WireFormat.WireType.LengthDelimited); o.WriteBytes(ByteString.CopyFrom(image.ToArray()));
        }
        o.WriteTag(11, WireFormat.WireType.LengthDelimited); o.WriteBytes(Wrapped(1, "ignored"));
        o.Flush();
        return stream.ToArray();

        static ByteString Wrapped(int field, string value)
        {
            using var inner = new MemoryStream();
            var w = new CodedOutputStream(inner);
            w.WriteTag(field, WireFormat.WireType.LengthDelimited);
            w.WriteString(value);
            w.Flush();
            return ByteString.CopyFrom(inner.ToArray());
        }
    }

    [Fact]
    public void A_user_profile_is_protobuf_the_largest_avatar_wins_and_the_row_is_the_one_that_asked()
    {
        TestScope.Fresh();
        var body = ProfileBody("Christos", "Christos K",
            (64, 64, "https://i.scdn.co/image/small"), (300, 300, "https://i.scdn.co/image/large"));

        var s = Staging.Rent();
        Spotify.Decode.Extension(Spotify.Decode.Ext.UserProfile, "spotify:user:christos"u8, body, s);
        TestScope.CommitAndPublish(s);

        var user = Entities.User(EntityUri.Parse("spotify:user:christos".AsSpan()));
        Assert.True(user.Knows(UserFields.Identity));
        Assert.Equal("Christos K", Entities.Strings.Resolve(user.NameId));
        Assert.Equal("https://i.scdn.co/image/large", Entities.Strings.Resolve(user.ImageId));
    }

    [Fact]
    public void A_profile_answer_in_json_reads_both_spellings_and_one_with_neither_is_still_an_answer()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.Profile("""{"username":"x","display_name":"Web Name","images":[{"url":"https://i/web.jpg"}]}"""u8,
                               "spotify:user:web"u8, s);
        Spotify.Decode.Profile("""{"username":"y","name":"Spclient Name","image_url":"https://i/sp.jpg"}"""u8,
                               "spotify:user:spclient"u8, s);
        Spotify.Decode.UserProfile(" {\"name\":\"sniffed\"}"u8, "spotify:user:sniffed"u8, s);
        Spotify.Decode.Profile("{}"u8, "spotify:user:private"u8, s);
        TestScope.CommitAndPublish(s);

        static User U(string uri) => Entities.User(EntityUri.Parse(uri.AsSpan()));
        Assert.Equal("Web Name", Entities.Strings.Resolve(U("spotify:user:web").NameId));
        Assert.Equal("https://i/web.jpg", Entities.Strings.Resolve(U("spotify:user:web").ImageId));
        Assert.Equal("Spclient Name", Entities.Strings.Resolve(U("spotify:user:spclient").NameId));
        Assert.Equal("sniffed", Entities.Strings.Resolve(U("spotify:user:sniffed").NameId));   // 0x20 then '{' is JSON
        Assert.True(U("spotify:user:private").Knows(UserFields.Identity));                     // learned: no public name
    }

    // ── the rootlist marker stream (G-046, G-047) ───────────────────────────────────────────────────────────────────

    const string GroupId = "edb339e10aebcf38";

    static byte[] RootlistBytes()
    {
        static Pl.Item Row(string uri, long ms) => new() { Uri = uri, Attributes = new Pl.ItemAttributes { Timestamp = ms } };
        var revision = new byte[] { 0, 0, 1, 44, 0xde, 0xad, 0xbe, 0xef };
        return new Pl.SelectedListContent
        {
            Revision = Bs(revision),
            Contents = new Pl.ListItems
            {
                Pos = 0,
                Truncated = false,
                Items =
                {
                    Row(UriOf(EntityKind.Playlist, 1), 1_700_000_000_000),
                    Row("spotify:start-group:" + GroupId + ":Work+%26+Study", 1_700_000_100_000),
                    Row(UriOf(EntityKind.Playlist, 2), 0),
                    Row("spotify:start-group:0011223344556677:Deep%3Anested", 0),
                    Row(UriOf(EntityKind.Playlist, 3), 0),
                    Row("spotify:end-group:0011223344556677", 0),
                    Row("spotify:end-group:" + GroupId, 0),
                    Row("spotify:collection:tracks", 0),                    // not a playlist: skipped, position kept
                    Row(UriOf(EntityKind.Playlist, 4), 0),
                },
            },
        }.ToByteArray();
    }

    [Fact]
    public void A_rootlist_lands_every_folder_as_a_marker_pair_with_its_group_id_and_its_name()
    {
        TestScope.Fresh();
        var me = Encoding.UTF8.GetBytes("spotify:user:christos");
        var s = Staging.Rent();
        Spotify.Decode.Rootlist(RootlistBytes(), me, s);
        TestScope.CommitAndPublish(s);

        var user = Entities.User(EntityUri.Parse("spotify:user:christos".AsSpan()));
        var rootlist = Entities.Current.Edges.Rootlist;
        var rows = rootlist.Payload(user.Slot);
        var targets = rootlist.Targets(user.Slot);
        Assert.Equal(EdgeState.Complete, rootlist.State(user.Slot));
        Assert.Equal(8, rows.Length);

        Assert.Equal(RootlistKind.Item, (RootlistKind)rows[0].Kind);
        Assert.Equal(Entities.Playlist(EntityId.ForGid(EntityKind.Playlist, Gid(1))).Slot, targets[0]);
        Assert.Equal((int)(1_700_000_000_000 / 1000), rows[0].AddedAt);            // milliseconds on the wire

        Assert.Equal(RootlistKind.FolderStart, (RootlistKind)rows[1].Kind);
        Assert.Equal(Table.None, targets[1]);
        Assert.Equal(GroupId, Entities.Strings.Resolve(rows[1].FolderId));        // D10: the bare group id
        Assert.Equal("Work & Study", Entities.Strings.Resolve(rows[1].FolderName));
        Assert.Equal(0, rows[1].Depth);
        Assert.Equal(1, rows[1].Position);

        Assert.Equal(1, rows[2].Depth);                                            // inside the folder
        Assert.Equal("Deep:nested", Entities.Strings.Resolve(rows[3].FolderName));
        Assert.Equal(2, rows[4].Depth);
        Assert.Equal(RootlistKind.FolderEnd, (RootlistKind)rows[5].Kind);
        Assert.Equal(1, rows[5].Depth);                                            // the end marker sits at its start's depth
        Assert.Equal(RootlistKind.FolderEnd, (RootlistKind)rows[6].Kind);
        Assert.Equal(GroupId, Entities.Strings.Resolve(rows[6].FolderId));
        Assert.Equal(0, rows[6].Depth);
        Assert.Equal(8, rows[7].Position);                                         // the skipped collection kept its index

        Assert.Equal("300,deadbeef", Entities.Strings.Resolve(Entities.Current.Edges.RootlistRevision(user.Slot)));
    }

    [Fact]
    public void A_re_answered_rootlist_keeps_its_folder_ids_alive()
    {
        // The commit AddRefs the new list BEFORE it releases the old one: a folder whose id did not change must not be
        // reclaimed in between (Edges.cs's owned-text rule).
        TestScope.Fresh();
        var me = Encoding.UTF8.GetBytes("spotify:user:christos");
        for (int i = 0; i < 3; i++)
        {
            var s = Staging.Rent();
            Spotify.Decode.Rootlist(RootlistBytes(), me, s);
            TestScope.CommitAndPublish(s);
        }
        var user = Entities.User(EntityUri.Parse("spotify:user:christos".AsSpan()));
        var row = Entities.Current.Edges.Rootlist.Payload(user.Slot)[1];
        Assert.Equal(GroupId, Entities.Strings.Resolve(row.FolderId));
        Assert.Equal(row.FolderId, Entities.Strings.Intern(GroupId));              // still the live id, not a re-mint
    }

    [Theory]
    [InlineData("New+Folder", "New Folder")]
    [InlineData("A%2BB+C", "A+B C")]                  // `+` is a space FIRST, then the escapes: %2B survives as '+'
    [InlineData("caf%C3%A9+mix", "café mix")]
    [InlineData("has%3Acolon+too", "has:colon too")]
    [InlineData("broken%zz", "broken%zz")]            // a malformed escape is kept verbatim
    public void A_folder_name_decodes_plus_first_then_percent(string escaped, string expected)
    {
        var s = Staging.Rent();
        var name = Spotify.Decode.FolderName(s, Encoding.UTF8.GetBytes(escaped));
        Assert.Equal(expected, Encoding.UTF8.GetString(s.Utf8(name)));
        Staging.Return(s);
    }

    // ── recents: the wire token is the label (G-060) ────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_recents_content_type_keeps_its_wire_token_and_a_known_one_costs_no_text()
    {
        static Pl.Item Played(string uri, string contentType) => new()
        {
            Uri = uri,
            Attributes = new Pl.ItemAttributes { FormatAttributes = { new Pl.FormatListAttribute { Key = contentType } } },
        };
        var page = new Pl.SelectedListContent
        {
            Contents = new Pl.ListItems
            {
                Pos = 0,
                Truncated = false,
                Items = { Played(UriOf(EntityKind.Album, 1), "content_type_audiobooks"), Played(UriOf(EntityKind.Album, 2), "content_type_music") },
            },
        }.ToByteArray();

        var s = Staging.Rent();
        var items = new Spotify.Decode.RecentsItem[4];
        int n = Spotify.Decode.RecentsPage(page, s, items);

        Assert.Equal(2, n);
        Assert.Equal(RecentsContentType.None, items[0].ContentType);
        Assert.Equal("audiobooks", Encoding.UTF8.GetString(s.Utf8(items[0].RawContentType)));
        Assert.Equal(RecentsContentType.Music, items[1].ContentType);
        Assert.True(items[1].RawContentType.IsEmpty);
        Staging.Return(s);
    }

    // ── the allocation gate (P1, P8) ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_warm_decode_of_the_same_bytes_allocates_nothing()
    {
        // The one CORE rule that cannot be read off the code. Two warm-up passes grow the staging arena, the row
        // lists and the edge buffer to their high-water mark; the third must cost zero bytes.
        TestScope.Fresh();
        var album = AlbumV4Bytes();
        var s = Staging.Rent();
        for (int i = 0; i < 2; i++) { Spotify.Decode.AlbumV4(album, s); s.Reset(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Spotify.Decode.AlbumV4(album, s);
        long after = GC.GetAllocatedBytesForCurrentThread();
        s.Reset();
        Staging.Return(s);

        Assert.Equal(0L, after - before);
    }
}
