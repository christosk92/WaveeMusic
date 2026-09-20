// ── Wavee.Tests/ArtistDecodeTests.cs — the artist page's pathfinder folds (Spotify.Decode.Artist.cs) ─────────────────
//
// Read against the ONE captured artist overview the app ships (`Fixtures/spotify/artist-maroon5.json`, the bundled
// `assets/spotify` capture). Every count was measured from the fixture itself: 10 top tracks, 20 related, 20 appears-on,
// 18 / 46 / 2 facet totals, 3 + 8 + 11 playlists (19 unique), 30 + 2 videos, 3 merch, 5 cities, 4 links, 6 gallery
// images, 10 of 12 concerts. Every fact reads a HANDLE after the commit, never a staged row.

using FluentGpu.Foundation;
using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ArtistDecodeTests
{
    const string ArtistUri = "spotify:artist:04gDigrS5kc9YWfZHwBETP";

    static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spotify", "artist-maroon5.json"));
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    static string Text(StringId id) => Entities.Strings.Resolve(id);
    static Artist ArtistOf(string uri = ArtistUri) => Entities.Artist(EntityUri.Parse(uri.AsSpan()));
    static Album AlbumOf(string uri) => Entities.Album(EntityUri.Parse(uri.AsSpan()));
    static int TrackSlot(string uri) => Entities.Current.Tracks.Slot(uri.AsSpan());

    static Artist LoadOverview()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistPage(Fixture(), Utf8(ArtistUri), s);
        TestScope.CommitAndPublish(s);
        return ArtistOf();
    }

    [Fact]
    public void The_overview_fills_identity_stats_bio_and_its_lead_sentence_as_one_unit()
    {
        var a = LoadOverview();
        Assert.Equal("Maroon 5", a.Name);
        Assert.Equal(48_175_721u, a.Followers);
        Assert.Equal(77_819_363u, a.MonthlyListeners);
        Assert.Equal((ushort)19, a.WorldRank);
        Assert.True(a.IsVerified);
        Assert.StartsWith("Maroon 5 -- and, specifically", Text(a.BioId));
        Assert.Equal("Maroon 5 -- and, specifically, its frontman Adam Levine -- became the face of blue-eyed soul in the "
                   + "21st century, managing to navigate shifting trends in music and fashion to be one of the biggest pop "
                   + "bands of their generation.", Text(a.BioLeadId));
        Assert.True(ArtistReadiness.Overview(a));                       // the answer spoke for every group
        Assert.False(a.Knows(ArtistFields.Chart));                      // the chart transport is a different answer
    }

    [Fact]
    public void The_header_is_the_wide_image_and_the_accent_is_the_providers_extracted_colour()
    {
        var a = LoadOverview();
        Assert.Equal("https://image-cdn-fa.spotifycdn.com/image/ab67618600000194af60e9aa0d75133e55c126cd", Text(a.HeaderId));
        Assert.Equal("https://i.scdn.co/image/ab6761610000e5ebf8349dfb619a7f842242de77", Text(a.ImageId));
        Assert.Equal(a.HeaderId, a.PaletteImageId);
        Assert.Equal(0xFF8898A8u, a.HeaderAccent);
    }

    [Fact]
    public void The_pick_points_at_its_item_and_an_absent_upcoming_release_owns_no_row()
    {
        var a = LoadOverview();
        Assert.True(a.HasPick);
        Assert.Equal("spotify:album:4xT3ryrqfutVzV1cJN79Ww", Text(a.Pick.ItemUri));
        Assert.Equal("\"HEROINE\" OUT NOW ! ", Text(a.Pick.Comment));
        Assert.Equal("Heroine", Text(a.Pick.Title));
        Assert.Equal("https://image-cdn-fa.spotifycdn.com/image/ab67616d000075a0e26ed70ca976a8e72ac89dab", Text(a.Pick.Cover));
        Assert.Equal((byte)EntityKind.Album, a.Pick.ItemKind);
        Assert.False(a.HasPreRelease);                                  // preReleaseV2: null — an answer, no side row
        Assert.False(a.HasUpcoming);
    }

    [Fact]
    public void The_latest_release_is_a_card_complete_album_at_day_precision()
    {
        var a = LoadOverview();
        var latest = a.Latest;
        Assert.Equal(AlbumOf("spotify:album:4xT3ryrqfutVzV1cJN79Ww").Slot, latest.Slot);
        // DiscoCard: the overview's release cards never carry billed artists (`ArtistStageRelease` does not parse them),
        // and `Card` now includes `AlbumFields.Artists` — see the album-artists fix in Album.cs.
        Assert.True(latest.Knows(AlbumFields.DiscoCard));
        Assert.Equal(AlbumKind.Single, latest.Kind);
        Assert.Equal(1, latest.TrackCount);
        Assert.Equal((ushort)2026, latest.Year);
        Assert.Equal((byte)2, latest.DatePrecision);
        Assert.Equal("2026-05-01", Text(latest.ReleaseDateIsoId));
    }

    [Fact]
    public void The_chart_seed_lands_complete_in_the_overviews_order_with_play_counts()
    {
        var a = LoadOverview();
        var popular = a.PopularSlots;
        Assert.Equal(10, popular.Length);
        Assert.Equal(TrackSlot("spotify:track:1XGmzt0PVuFgQYYnV2It7A"), popular[0]);
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.ArtistPopular.State(a.Slot));
        var first = new Track(popular[0]);
        Assert.True(first.Knows(TrackFields.Row));
        Assert.Equal(2_731_956_874u, Entities.Current.Tracks.PlayCount[first.Slot]);
    }

    [Fact]
    public void Facet_cards_carry_the_whole_card_group()
    {
        var a = LoadOverview();
        var album = AlbumOf("spotify:album:4hnzOo44FfNzkAjtywCvBL");
        Assert.Equal(album.Slot, a.AlbumSlots[0]);
        Assert.True(album.Knows(AlbumFields.DiscoCard));   // not Card: a facet card carries no billed artists
        Assert.Equal(13, album.TrackCount);
        Assert.Equal(AlbumKind.Album, album.Kind);
        Assert.Equal("2025-08-16", Text(album.ReleaseDateIsoId));

        // The first page of each facet lands at offset 0 with the server's total: Partial until the rest is paged.
        var e = Entities.Current.Edges;
        Assert.Equal((18, 10, EdgeState.Partial), (Artist.FacetTotal(a, DiscoFacet.Albums), a.AlbumSlots.Length, e.ArtistAlbums.State(a.Slot)));
        Assert.Equal((46, 10, EdgeState.Partial), (Artist.FacetTotal(a, DiscoFacet.Singles), a.SingleSlots.Length, e.ArtistSingles.State(a.Slot)));
        Assert.Equal((2, 2, EdgeState.Complete), (Artist.FacetTotal(a, DiscoFacet.Compilations), a.CompilationSlots.Length, e.ArtistCompilations.State(a.Slot)));
    }

    [Fact]
    public void Related_and_appears_on_land_complete()
    {
        var a = LoadOverview();
        var e = Entities.Current.Edges;
        Assert.Equal(20, a.RelatedSlots.Length);
        Assert.Equal(EdgeState.Complete, e.ArtistRelated.State(a.Slot));
        Assert.Equal(ArtistOf("spotify:artist:0du5cEVh5yTK9QJze8zA0C").Slot, a.RelatedSlots[0]);
        Assert.Equal(20, a.AppearsOnSlots.Length);
        Assert.Equal(AlbumOf("spotify:album:3zuiRKPaFalv72BcNJ47Ih").Slot, a.AppearsOnSlots[0]);
    }

    [Fact]
    public void The_three_playlist_lists_concatenate_without_repeats_and_carry_the_owner_as_subtitle()
    {
        var a = LoadOverview();
        Assert.Equal(19, a.PlaylistSlots.Length);
        Assert.Equal("Interscope Records", Text(a.PlaylistSubtitleIds[0]));
    }

    [Fact]
    public void Both_video_envelopes_land_once_each_with_the_16_by_9_still()
    {
        var a = LoadOverview();
        Assert.Equal(32, a.VideoSlots.Length);
        Assert.Equal(TrackSlot("spotify:track:61jkZEG6CFiceGPJKdaYJD"), a.VideoSlots[0]);
        Assert.Equal("https://i.scdn.co/image/ab6742d3000053b756286203e8a1c6a93f37fe80", Text(a.VideoPayload[0].Thumb));
    }

    [Fact]
    public void Merch_cities_links_and_the_gallery_are_owned_payload_rows()
    {
        var a = LoadOverview();
        Assert.Equal(3, a.MerchSlots.Length);
        ref var merch = ref Album.MerchAt(a.MerchSlots[0]);
        Assert.Equal("Love Is Like 2025 Tour Photo Tee", Text(merch.Name));
        Assert.Equal("US$45.00", Text(merch.Price));

        Assert.Equal(5, a.TopCities.Length);
        Assert.Equal("São Paulo", Text(a.TopCities[0].City));
        Assert.Equal("BR", Text(a.TopCities[0].Country));
        Assert.Equal(968_965u, a.TopCities[0].Listeners);

        Assert.Equal(4, a.Links.Length);
        Assert.Equal("Facebook", Text(a.Links[0].Name));
        Assert.Equal((byte)ArtistCatalog.LinkKind.Instagram, a.Links[1].Kind);
        Assert.Equal((byte)ArtistCatalog.LinkKind.Wikipedia, a.Links[3].Kind);

        Assert.Equal(6, a.GallerySlots.Length);
        Assert.Equal("https://i.scdn.co/image/ab6761670000ecd466c4abcd55e08f6ff7bff182", Text(a.GallerySlots[0]));
    }

    [Fact]
    public void A_capped_concert_list_is_not_a_complete_schedule()
    {
        // 10 of 12 dates: the overview does NOT claim the schedule; the ArtistConcerts route answers it.
        var a = LoadOverview();
        Assert.Equal(EdgeState.Unknown, Entities.Current.Edges.ArtistConcerts.State(a.Slot));
    }

    [Fact]
    public void Re_answering_the_overview_does_not_grow_the_interner()
    {
        LoadOverview();
        int settled = Entities.Strings.MapCount;
        for (int i = 0; i < 3; i++)
        {
            var s = Staging.Rent();
            Spotify.Decode.ArtistPage(Fixture(), Utf8(ArtistUri), s);
            TestScope.CommitAndPublish(s);
        }
        Assert.Equal(settled, Entities.Strings.MapCount);
    }

    [Fact]
    public void An_overview_with_nothing_speaks_for_every_group_and_every_list()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistPage(Utf8("{\"data\":{\"artistUnion\":{\"profile\":{\"name\":\"Quiet\"},\"preReleaseV2\":null}}}"),
                                  Utf8("spotify:artist:quiet"), s);
        TestScope.CommitAndPublish(s);

        var a = ArtistOf("spotify:artist:quiet");
        Assert.True(a.Knows(ArtistFields.Pick | ArtistFields.PreRelease | ArtistFields.Latest | ArtistFields.Tour));
        Assert.False(a.HasPick);
        Assert.Equal(Table.None, a.Latest.Slot);
        var e = Entities.Current.Edges;
        Assert.Equal(EdgeState.Complete, e.ArtistGallery.State(a.Slot));
        Assert.Equal(EdgeState.Complete, e.ArtistLinks.State(a.Slot));
        Assert.Equal(EdgeState.Complete, e.ArtistRelated.State(a.Slot));
        Assert.Equal(EdgeState.Complete, e.ArtistPopular.State(a.Slot));
        Assert.Equal(EdgeState.Complete, e.ArtistCompilations.State(a.Slot));
        Assert.False(Artist.HasFacet(a, DiscoFacet.Albums));
    }

    [Fact]
    public void The_npv_artist_answer_lands_only_what_it_owns()
    {
        TestScope.Fresh();
        const string json = """
        { "data": { "artistUnion": {
            "uri": "spotify:artist:A1",
            "profile": { "name": "The Artist", "biography": { "text": "About &amp; bio. Second sentence here." },
                         "externalLinks": { "items": [ { "name": "instagram", "url": "https://instagram.com/theartist" } ] } },
            "onPlatformReputationTrait": { "verification": { "isVerified": true } },
            "stats": { "monthlyListeners": 123456, "followers": 987, "worldRank": 42,
                       "topCities": { "items": [ { "city": "Athens", "country": "GR", "numberOfListeners": 1200 } ] } },
            "visuals": { "avatarImage": { "sources": [ { "url": "https://cdn/avatar" } ] },
                         "headerImage": { "sources": [ { "url": "https://cdn/header" } ] },
                         "gallery": { "items": [ { "sources": [ { "url": "https://cdn/gallery" } ] } ] } },
            "goods": { "merch": { "items": [ { "nameV2": "Artist Hoodie", "price": "$60", "url": "https://shop/hoodie",
                                                "image": { "sources": [ { "url": "https://cdn/hoodie" } ] } } ] } }
        } } }
        """;
        var s = Staging.Rent();
        Spotify.Decode.NpvArtist(Utf8(json), Utf8("spotify:artist:A1"), s);
        TestScope.CommitAndPublish(s);

        var a = ArtistOf("spotify:artist:A1");
        Assert.Equal("The Artist", a.Name);
        Assert.True(a.IsVerified);
        Assert.Equal((ushort)42, a.WorldRank);
        Assert.Equal("https://cdn/header", Text(a.HeaderId));
        Assert.Equal("About & bio. Second sentence here.", Text(a.BioLeadId));   // ". " at index 11 ≤ 20: no cut
        Assert.Equal("Athens", Text(a.TopCities[0].City));
        Assert.Equal((byte)ArtistCatalog.LinkKind.Instagram, a.Links[0].Kind);
        Assert.Equal("Instagram", Text(a.Links[0].Name));
        Assert.Equal("https://cdn/gallery", Text(a.GallerySlots[0]));
        Assert.Equal("Artist Hoodie", Text(Album.MerchAt(a.MerchSlots[0]).Name));
        // NPV does not speak for the pick, the releases, the tour or the chart, and lists it did not carry stay unasked.
        Assert.False(a.Knows(ArtistFields.Pick));
        Assert.False(a.Knows(ArtistFields.Latest));
        Assert.Equal(EdgeState.Unknown, Entities.Current.Edges.ArtistRelated.State(a.Slot));
    }

    [Theory]
    [InlineData("2026-06-25T16:00+02:00", 1_782_396_000_000L, 120)]
    [InlineData("2026-06-25T14:00Z", 1_782_396_000_000L, 0)]
    [InlineData("2026-06-25T09:00:30.250-0500", 1_782_396_030_250L, -300)]
    [InlineData("2026-06-25", 1_782_345_600_000L, 0)]
    public void An_iso_instant_keeps_its_offset(string iso, long unixMs, int offset)
    {
        Assert.True(Spotify.Decode.ArtistIsoInstant(Utf8(iso), out long ms, out short minutes));
        Assert.Equal(unixMs, ms);
        Assert.Equal(offset, (int)minutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-13-01")]
    [InlineData("2026-06-25T25:00Z")]
    [InlineData("2026-06-25T10:00+x")]
    public void A_malformed_iso_instant_is_refused(string iso)
        => Assert.False(Spotify.Decode.ArtistIsoInstant(Utf8(iso), out _, out _));
}
