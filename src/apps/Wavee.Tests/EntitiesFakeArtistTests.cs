// ── Wavee.Tests/EntitiesFakeArtistTests.cs — the artist / discography seed's contract (Entities.Fake.Artist.cs) ──────
//
// WP-5.N contract §8 as facts: the per-artist matrix (pick, upcoming, videos, playlists, merch, cities, gallery,
// appears-on, verified, world rank), the showcase's > 300 singles and its era bands, the chart, the no-image artist, the
// tour arms on the fixed seed clock, determinism (two boots, one answer; a different now0 moves only the clock-derived
// facts), and the ch 31 §0.8 agreement rule (a facet album's TrackCount IS its tracklist's length). Everything drives the
// real `Entities.SeedFake` and reads HANDLES — the expected counts below were computed from the contract's formulas with
// the ch 31 §8 string hash (h = 17; h = h·31 + c; h & 0x7fffffff) over the core artist names, and are pinned as literals.

using System.Globalization;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EntitiesFakeArtistTests
{
    const long Now0 = 1_788_000_000;   // Platform.Clock.FixedSeedEpoch, restated so this file has no Platform dependency
    const int Seeded = 13;
    const int Showcase = 3;

    static void Seed(long now0 = Now0)
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(now0);
    }

    static string UriOf(int i) => i < 12 ? "spotify:artist:ar" + i.ToString(CultureInfo.InvariantCulture) : "spotify:artist:arnoimg";
    static Artist ArtistOf(int i) => Entities.Artist(EntityUri.Parse(UriOf(i)));
    static string Text(StringId id) => Entities.Strings.Resolve(id);

    // (pick, upcoming, videos, playlists, merch, cities, gallery, appearsOn, verified, worldRank) per seeded artist.
    static readonly (bool Pick, bool Upcoming, int Videos, int Playlists, int Merch, int Cities, int Gallery, int AppearsOn,
                     bool Verified, ushort Rank)[] s_matrix =
    [
        (true,  false, 4, 4, 5, 0, 6, 0, false, 0),     // ar0  Christos
        (false, false, 4, 0, 0, 5, 7, 6, true,  189),   // ar1  Alex Rivers
        (false, false, 0, 4, 6, 0, 0, 0, true,  380),   // ar2  Mia Solace
        (true,  true,  4, 4, 5, 5, 4, 6, true,  11),    // ar3  The Wavee Collective (the showcase)
        (false, false, 4, 4, 0, 0, 8, 0, true,  435),   // ar4  Nova Kite
        (true,  false, 4, 0, 5, 5, 0, 6, false, 411),   // ar5  Sable & Sun
        (true,  false, 4, 4, 3, 0, 7, 0, true,  369),   // ar6  Half Moon Radio
        (false, true,  0, 0, 0, 5, 5, 6, true,  0),     // ar7  Kimmuseum
        (true,  false, 4, 4, 4, 0, 0, 0, true,  142),   // ar8  Dry Season
        (true,  false, 4, 0, 4, 5, 5, 6, true,  362),   // ar9  The Longtime
        (false, false, 4, 4, 0, 0, 6, 0, false, 483),   // ar10 Iris Overdrive
        (true,  false, 4, 0, 6, 5, 0, 6, true,  232),   // ar11 Coral Hours
        (true,  false, 0, 4, 6, 0, 6, 0, true,  228),   // arnoimg The Unpictured
    ];

    [Fact]
    public void Every_seeded_artist_answers_the_whole_overview_and_the_chart()
    {
        Seed();
        for (int i = 0; i < Seeded; i++)
        {
            var a = ArtistOf(i);
            Assert.True(a.Knows(ArtistFields.All), UriOf(i) + " is missing an artist group");
            Assert.True(ArtistReadiness.Overview(a));
            Assert.True(ArtistReadiness.Chart(a), UriOf(i) + "'s chart is not paintable");
            Assert.Equal(i == Showcase ? 10 : 5, a.PopularSlots.Length);
            Assert.False(Text(a.BioLeadId).Length == 0);
        }
    }

    [Fact]
    public void The_matrix_is_the_contracts()
    {
        Seed();
        var e = Entities.Current.Edges;
        for (int i = 0; i < Seeded; i++)
        {
            var a = ArtistOf(i);
            var m = s_matrix[i];
            string who = UriOf(i);
            Assert.True(m.Pick == a.HasPick, who + " pick");
            Assert.True(m.Upcoming == a.HasPreRelease, who + " pre-release");
            Assert.True(m.Upcoming == a.HasUpcoming, who + " upcoming bit");
            Assert.Equal(m.Videos, a.VideoSlots.Length);
            Assert.Equal(m.Playlists, a.PlaylistSlots.Length);
            Assert.Equal(m.Merch, a.MerchSlots.Length);
            Assert.Equal(m.Cities, a.TopCities.Length);
            Assert.Equal(m.Gallery, a.GallerySlots.Length);
            Assert.Equal(m.AppearsOn, a.AppearsOnSlots.Length);
            Assert.Equal(6, a.RelatedSlots.Length);
            Assert.Equal(3, a.Links.Length);
            Assert.True(m.Verified == a.IsVerified, who + " verified");
            Assert.Equal(m.Rank, a.WorldRank);

            // Absent is ANSWERED: every list the page reads is Complete, empty or not (never a skeleton forever).
            Assert.Equal(EdgeState.Complete, e.ArtistVideos.State(a.Slot));
            Assert.Equal(EdgeState.Complete, e.ArtistPlaylists.State(a.Slot));
            Assert.Equal(EdgeState.Complete, e.ArtistMerch.State(a.Slot));
            Assert.Equal(EdgeState.Complete, e.ArtistCities.State(a.Slot));
            Assert.Equal(EdgeState.Complete, e.ArtistGallery.State(a.Slot));
            Assert.Equal(EdgeState.Complete, e.ArtistAppearsOn.State(a.Slot));
            Assert.Equal(EdgeState.Complete, e.ArtistRelated.State(a.Slot));
            Assert.Equal(EdgeState.Complete, e.ArtistConcerts.State(a.Slot));
            foreach (int related in a.RelatedSlots) Assert.NotEqual(a.Slot, related);
        }
    }

    [Fact]
    public void The_showcase_carries_more_than_three_hundred_singles_and_its_eras_group()
    {
        Seed();
        var a = ArtistOf(Showcase);
        Assert.Equal((64, 320, 12), (Artist.FacetTotal(a, DiscoFacet.Albums), Artist.FacetTotal(a, DiscoFacet.Singles),
                                     Artist.FacetTotal(a, DiscoFacet.Compilations)));
        Assert.True(a.SingleSlots.Length >= 300);
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.ArtistSingles.State(a.Slot));

        var singles = a.SingleSlots;
        var years = new ushort[singles.Length];
        for (int k = 0; k < singles.Length; k++) years[k] = new Album(singles[k]).Year;
        for (int k = 1; k < years.Length; k++) Assert.True(years[k] <= years[k - 1], "singles are DATE_DESC");
        var eras = Assert.IsType<DiscographyEraBand[]>(DiscographyEraBands.PlanYears(years));
        Assert.InRange(eras.Length, 2, 8);

        // An ordinary artist's small catalogue stays flat.
        var small = ArtistOf(0);
        Assert.Equal((2, 3, 1), (small.AlbumSlots.Length, small.SingleSlots.Length, small.CompilationSlots.Length));
    }

    [Fact]
    public void A_facet_album_agrees_with_its_tracklist_and_its_facet()
    {
        Seed();
        var tracks = Entities.Current.Edges.AlbumTracks;
        for (int i = 0; i < Seeded; i++)
        {
            var a = ArtistOf(i);
            for (int f = 0; f < 3; f++)
            {
                var facet = (DiscoFacet)f;
                foreach (int slot in Artist.FacetEdge(facet).Targets(a.Slot))
                {
                    var album = new Album(slot);
                    Assert.True(album.Knows(AlbumFields.DiscoCard), album.Uri.Text + " card");   // facet cards claim no billed artists
                    Assert.True(ArtistCatalog.KindMatches(album.Kind, facet), album.Uri.Text + " kind");
                    Assert.Equal(EdgeState.Complete, tracks.State(slot));
                    Assert.Equal(album.TrackCount, tracks.Count(slot));      // ch 31 §0.8: the header and its membership agree
                }
            }
            var latest = a.Latest;
            Assert.True(latest.Knows(AlbumFields.DiscoCard));   // the fake latest release claims no billed artists either
            Assert.Equal(i % 2 == 0 ? a.AlbumSlots[0] : a.SingleSlots[0], latest.Slot);
        }
    }

    [Fact]
    public void The_chart_is_ranked_by_plays_and_every_row_is_credited()
    {
        Seed();
        var t = Entities.Current.Tracks;
        for (int i = 0; i < Seeded; i++)
        {
            var popular = ArtistOf(i).PopularSlots;
            for (int k = 0; k < popular.Length; k++)
            {
                Assert.True(t.PlayCount[popular[k]] > 0);
                if (k > 0) Assert.True(t.PlayCount[popular[k]] <= t.PlayCount[popular[k - 1]]);
                Assert.Equal(EdgeState.Complete, Entities.Current.Edges.TrackArtists.State(popular[k]));
            }
        }
    }

    [Fact]
    public void The_no_image_artist_has_neither_an_avatar_nor_a_header()
    {
        Seed();
        var a = ArtistOf(12);
        Assert.Equal("The Unpictured", a.Name);
        Assert.True(a.ImageId.IsEmpty);
        Assert.True(a.HeaderId.IsEmpty);
        Assert.True(a.PaletteImageId.IsEmpty);
        Assert.False(ArtistOf(0).PaletteImageId.IsEmpty);
    }

    [Fact]
    public void The_tour_arms_are_derived_on_the_fixed_seed_clock()
    {
        Seed();
        // ar3: 41 dates, the next at now0 + 2 d → on tour now (live). ar1: 5 dates from +21 d → an upcoming tour.
        // ar2: one date → a show. ar5: three dates → dates. ar0: none → no banner.
        AssertArm(Showcase, "artist.tour.onTourNow", live: true);
        AssertArm(1, "artist.tour.upcomingTour", live: false);
        AssertArm(2, "artist.tour.upcomingShow", live: false);
        AssertArm(5, "artist.tour.upcomingDates", live: false);
        var none = ArtistOf(0);
        Assert.True(none.TourEyebrowId.IsEmpty);
        Assert.False(none.IsTourLive);
        Assert.True(none.Knows(ArtistFields.Tour));

        static void AssertArm(int i, string key, bool live)
        {
            var a = ArtistOf(i);
            Assert.Equal(Loc.Get(key), Text(a.TourEyebrowId));
            Assert.False(a.TourHeadlineId.IsEmpty);
            Assert.False(a.TourSublineId.IsEmpty);
            Assert.Equal(live, a.IsTourLive);
        }
    }

    [Fact]
    public void The_upcoming_release_counts_down_from_now0()
    {
        Seed();
        var a = ArtistOf(Showcase);
        Assert.Equal(Now0 + 9 * 86_400 + 4 * 3_600, (long)a.PreRelease.ReleaseAt);
        Assert.Equal("spotify:album:dg3u0", Text(a.PreRelease.Uri));
    }

    [Fact]
    public void Two_boots_seed_the_same_artists_and_now0_moves_only_the_clock()
    {
        Seed();
        string first = Snapshot(clockFacts: true);
        Seed();
        Assert.Equal(first, Snapshot(clockFacts: true));

        Seed(Now0 + 86_400);
        Assert.Equal(Now0 + 86_400 + 9 * 86_400 + 4 * 3_600, (long)ArtistOf(Showcase).PreRelease.ReleaseAt);
        string later = Snapshot(clockFacts: false);
        Seed(Now0);
        Assert.Equal(Snapshot(clockFacts: false), later);
    }

    static string Snapshot(bool clockFacts)
    {
        var sb = new StringBuilder();
        var t = Entities.Current.Tracks;
        for (int i = 0; i < Seeded; i++)
        {
            var a = ArtistOf(i);
            sb.Append(a.Name).Append('|').Append(a.MonthlyListeners).Append('|').Append(a.Followers).Append('|')
              .Append(a.WorldRank).Append('|').Append(Text(a.BioLeadId)).Append('|').Append(Text(a.Pick.Title)).Append('|');
            foreach (int p in a.PopularSlots) sb.Append(new Track(p).Uri.Text).Append(':').Append(t.PlayCount[p]).Append(',');
            foreach (int s in a.SingleSlots) sb.Append(new Album(s).Uri.Text).Append(new Album(s).Year).Append(',');
            foreach (var g in a.GallerySlots) sb.Append(Text(g)).Append(',');
            foreach (var c in a.TopCities) sb.Append(Text(c.City)).Append(c.Listeners).Append(',');
            foreach (int m in a.MerchSlots) sb.Append(Text(Album.MerchAt(m).Name)).Append(',');
            if (clockFacts)
                sb.Append(Text(a.TourEyebrowId)).Append(Text(a.TourSublineId)).Append(a.IsTourLive).Append(a.PreRelease.ReleaseAt);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
