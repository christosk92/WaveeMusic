// ── Wavee.Tests/HomeUi/ClusterFoldTests.cs — Wave 1, owner A1 (rewritten for D2/F32) ─────────────────────────────────
//
// D2: a HomeBaseline section's own card is its header (what the cluster tile opens to); its ROWS are the section's
// feedBaselineLookup preview tracks, not the section's card list. An untitled section whose header is an Album is
// the Following facet's release-feed shape and is pulled out via the `following` out-param instead of becoming a
// (title-less) cluster.

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public class ClusterFoldTests
{
    static readonly ZoneTitles Titles = new(
        MadeForYou: "Made for you", BecauseYouLike: "Because you like…", MoreForYou: "More for you",
        RadioAndMixes: "Radio & mixes for you", Browse: "Browse", JumpBackIn: "Jump back in",
        RecentlyPlayed: "Recently played", NewEpisodes: "New episodes", ContinueListening: "Continue listening",
        VideosYouMightLike: "Videos you might like", EpisodesYouMightLike: "Episodes you might like",
        BecauseYouListenTo: "Because you listen to…", FromArtistsYouFollow: "From artists you follow",
        YourShows: "Your shows", ShowsYouMightLike: "Shows you might like");

    static SectionInput Of(Section s) => SectionReader.Of(s);

    [Fact]
    public void Titled_section_becomes_one_cluster_with_the_card_as_header()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.BandWithPreview("More like Avril Lavigne", SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:cf-1", "Avril Lavigne Radio")],
            [HomeUiFixtures.Track("spotify:track:cf-t1", "Complicated"), HomeUiFixtures.Track("spotify:track:cf-t2", "Sk8er Boi")]));

        var clusters = ClusterFold.Fold([s], Titles);
        var c = Assert.Single(clusters);
        Assert.Equal("More like Avril Lavigne", c.Over);
        Assert.Equal("Avril Lavigne Radio", c.Name);
        Assert.Equal(2, c.Rows.Count);
        Assert.NotNull(c.Header);
        Assert.Equal("spotify:playlist:cf-1", c.Header!.Value.Uri);
    }

    [Fact]
    public void Rows_are_capped_at_four()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.BandWithPreview("More like Shawn Mendes", SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:cf-r0", "Shawn Mendes Radio")],
            [
                HomeUiFixtures.Track("spotify:track:cf-r1", "1"), HomeUiFixtures.Track("spotify:track:cf-r2", "2"),
                HomeUiFixtures.Track("spotify:track:cf-r3", "3"), HomeUiFixtures.Track("spotify:track:cf-r4", "4"),
                HomeUiFixtures.Track("spotify:track:cf-r5", "5"),
            ]));

        var clusters = ClusterFold.Fold([s], Titles);
        var c = Assert.Single(clusters);
        Assert.Equal(4, c.Rows.Count);
    }

    [Fact]
    public void Section_with_no_preview_yet_contributes_no_cluster()
    {
        TestScope.Fresh();
        // The lookup races the plan (D2): a baseline section whose feedBaselineLookup answer has not landed has an
        // empty Preview, and the fold must show nothing for it rather than a card with no rows.
        var s = Of(HomeUiFixtures.BandWithPreview("More like Henry Moodie", SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:cf-np", "Henry Moodie Radio")], []));

        var clusters = ClusterFold.Fold([s], Titles, out var following);
        Assert.Empty(clusters);
        Assert.Empty(following);
    }

    [Fact]
    public void Section_with_no_usable_header_contributes_nothing()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.BandWithPreview("More like Nobody", SectionKind.HomeBaseline,
            [], [HomeUiFixtures.Track("spotify:track:cf-nh1", "Orphaned")]));

        var clusters = ClusterFold.Fold([s], Titles, out var following);
        Assert.Empty(clusters);
        Assert.Empty(following);
    }

    [Fact]
    public void Untitled_playlist_section_falls_back_to_more_for_you_title()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.BandWithPreview(null, SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:cf-u1", "Ambient Relaxation")],
            [HomeUiFixtures.Track("spotify:track:cf-ut1", "Breathe")]));

        var clusters = ClusterFold.Fold([s], Titles);
        var c = Assert.Single(clusters);
        Assert.Equal(Titles.MoreForYou, c.Over);
        Assert.Equal("Ambient Relaxation", c.Name);
    }

    [Fact]
    public void Untitled_album_section_becomes_a_following_release_row_not_a_cluster()
    {
        TestScope.Fresh();
        // The "music-following-chip" shape (F32): an untitled baseline section whose card is an Album.
        var s = Of(HomeUiFixtures.BandWithPreview(null, SectionKind.HomeBaseline,
            [HomeUiFixtures.Album("spotify:album:cf-fol1", "Short n' Sweet", releasedAtMs: 1_700_000_000_000)],
            [HomeUiFixtures.Track("spotify:track:cf-fol1t", "Please Please Please")]));

        var clusters = ClusterFold.Fold([s], Titles, out var following);
        Assert.Empty(clusters);
        var release = Assert.Single(following);
        Assert.Equal("spotify:album:cf-fol1", release.Uri);
    }

    [Fact]
    public void Titled_album_section_is_still_a_cluster_not_a_release_row()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.BandWithPreview("Because you like Sabrina Carpenter", SectionKind.HomeBaseline,
            [HomeUiFixtures.Album("spotify:album:cf-tita1", "Short n' Sweet")],
            [HomeUiFixtures.Track("spotify:track:cf-tita1t", "Espresso")]));

        var clusters = ClusterFold.Fold([s], Titles, out var following);
        Assert.Empty(following);
        var c = Assert.Single(clusters);
        Assert.Equal("Short n' Sweet", c.Name);
    }

    [Fact]
    public void Multiple_following_sections_all_join_the_release_list()
    {
        TestScope.Fresh();
        var a = Of(HomeUiFixtures.BandWithPreview(null, SectionKind.HomeBaseline,
            [HomeUiFixtures.Album("spotify:album:cf-mf1", "1")], [HomeUiFixtures.Track("spotify:track:cf-mf1t", "t")]));
        var b = Of(HomeUiFixtures.BandWithPreview(null, SectionKind.HomeBaseline,
            [HomeUiFixtures.Album("spotify:album:cf-mf2", "2")], [HomeUiFixtures.Track("spotify:track:cf-mf2t", "t")]));

        var clusters = ClusterFold.Fold([a, b], Titles, out var following);
        Assert.Empty(clusters);
        Assert.Equal(2, following.Count);
    }

    [Fact]
    public void Clusters_are_ordered_by_row_count_descending_then_server_order()
    {
        TestScope.Fresh();
        var small = Of(HomeUiFixtures.BandWithPreview("More like A", SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:cf-o0", "A Radio")],
            [HomeUiFixtures.Track("spotify:track:cf-o1", "1"), HomeUiFixtures.Track("spotify:track:cf-o2", "2")]));
        var big = Of(HomeUiFixtures.BandWithPreview("More like B", SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:cf-p0", "B Radio")],
            [
                HomeUiFixtures.Track("spotify:track:cf-o3", "1"), HomeUiFixtures.Track("spotify:track:cf-o4", "2"),
                HomeUiFixtures.Track("spotify:track:cf-o5", "3"), HomeUiFixtures.Track("spotify:track:cf-o6", "4"),
            ]));

        var clusters = ClusterFold.Fold([small, big], Titles);
        Assert.Equal(["More like B", "More like A"], clusters.Select(c => c.Over).ToArray());
    }
}
