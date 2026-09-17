// ── Wavee.Tests/EntitiesFakeHomeTests.cs — owner P's --fake surfaces (ch 31 §7.1 rows 34-42, 45; WP-5.P contract §8) ──
//
// The Wave 5 gate counts a route only when it reaches its LOADED arm from the seed (ch 31 §0.5). These facts pin the
// relations each owner-P page reads: the Home document and its facets, the Charts deck, the podium, the Browse
// directory and one category page per layout mode, the query "a", and a grouped Recents snapshot — plus the
// determinism rule (§7.2). No source-text tests: every fact drives the real Entities.SeedFake.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EntitiesFakeHomeTests
{
    const long Now0 = 1_788_000_000;   // Platform.Clock.FixedSeedEpoch, restated (no Platform dependency)

    static void Seed()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
    }

    static bool CaptureShipped
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "assets", "spotify", "home.json"));

    [Fact]
    public void The_home_feed_is_answered_and_ends_with_the_seeded_show_shelf()
    {
        Seed();
        var home = Entities.HomeFeed();

        Assert.True(home.Knows(HomeFields.All));
        Assert.Equal(HomeState.Ready, home.State(concluded: true));

        var sections = home.SectionSlots;
        var shows = Entities.Section("spotify:section:wavee-seed-shows".AsSpan());
        Assert.Equal(shows.Slot, sections[^1]);
        Assert.Equal(8, shows.Cards);
        Assert.Equal(8, shows.CardSlots.Length);

        // The bundled capture is 31 sections (ch 31 §2 W1); a stripped output without it still answers the shelf.
        Assert.Equal(CaptureShipped ? 32 : 1, home.SectionCount);
    }

    [Fact]
    public void The_daylist_card_carries_the_pinned_window()
    {
        Seed();
        var daylist = Entities.Playlist(EntityUri.Parse("spotify:playlist:37i9dQZF1EP6YuccBxUcC1"));

        Assert.True(daylist.Knows(PlaylistFields.Daylist));
        Assert.Equal((int)(Now0 + 4 * 3600 + 37 * 60), daylist.DaylistExpiresAt);
    }

    [Fact]
    public void Every_chip_has_its_own_answered_document_and_audiobooks_is_a_real_empty()
    {
        Seed();

        var audiobooks = Entities.HomeFeed("audiobooks-chip".AsSpan());
        Assert.True(audiobooks.Knows(HomeFields.All));
        Assert.Equal(0, audiobooks.SectionCount);
        Assert.Equal(HomeState.Empty, audiobooks.State(concluded: true));

        var podcasts = Entities.HomeFeed("podcasts-chip".AsSpan());
        Assert.Equal(1, podcasts.SectionCount);
        Assert.Equal(Entities.HomeFeed().ChipIds.Length, podcasts.ChipIds.Length);

        var music = Entities.HomeFeed("music-chip".AsSpan());
        Assert.Equal(Entities.HomeFeed().SectionCount - 1, music.SectionCount);
    }

    [Fact]
    public void The_five_chart_sections_hold_cards_carry_the_chart_flag_and_weekly_keeps_its_cursor()
    {
        Seed();
        foreach (string uri in ChartSections.All)
        {
            var section = Entities.BrowseSection(uri.AsSpan());
            Assert.True(section.Knows(SectionFields.Identity));
            Assert.True(section.CardSlots.Length >= 4, uri);
            Assert.True(section.IsChart, uri);
        }

        var weekly = Entities.BrowseSection(ChartSections.Weekly.AsSpan());
        Assert.True(weekly.HasMore);
        Assert.Equal(10, weekly.NextRequest);
    }

    [Fact]
    public void The_podium_ranks_ten_artists_and_ten_tracks()
    {
        Seed();
        int me = Entities.Current.MeSlot;

        Assert.Equal(10, Entities.Current.Edges.UserTopArtists.Count(me));
        Assert.Equal(10, Entities.Current.Edges.UserTopTracks.Count(me));
        Assert.Equal("Christos", Entities.Artist(EntityUri.Parse("spotify:artist:ar0")).Name);
    }

    [Fact]
    public void The_directory_lists_every_band_and_each_layout_mode_has_a_page()
    {
        Seed();
        var edges = Entities.Current.Edges;
        var directory = Entities.BrowseDirectory();

        Assert.True(directory.Knows(BrowseFields.All));
        Assert.Equal(58, edges.BrowseDirectory.Count(directory.Slot));   // 4 + 10 + 25 + 14 + 2 charts + 3 unmapped
        Assert.Equal(EdgeState.Complete, edges.BrowseDirectory.State(directory.Slot));

        var liveEvents = Entities.BrowseNode("spotify:concerts".AsSpan());
        Assert.True(liveEvents.IsClientFeature);

        Assert.Equal(3, edges.BrowseSections.Count(Entities.BrowseNode("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan()).Slot));
        Assert.Equal(1, edges.BrowseSections.Count(Entities.BrowseNode("spotify:page:0JQ5DAt0tbjZptfcdMSKl3".AsSpan()).Slot));
        Assert.Equal(2, edges.BrowseSections.Count(Entities.BrowseNode("spotify:page:0JQ5DAqbMKFz6FAsUtgAab".AsSpan()).Slot));
        Assert.Equal(2, edges.BrowseSections.Count(Entities.BrowseNode("spotify:page:0JQ5DAqbMKFCbimwdOYlsl".AsSpan()).Slot));

        // A genre spelling resolves to the page row the directory seeded (Browse.cs: one node, two spellings).
        var pop = Entities.BrowseNode("spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan());
        Assert.True(pop.Knows(BrowseFields.All));
        Assert.Equal("Pop", Entities.Strings.Resolve(pop.TitleId));
    }

    [Fact]
    public void Search_a_answers_its_chips_a_five_kind_top_list_every_facet_genres_and_related()
    {
        Seed();
        var all = Entities.Search(Entities.FakeSearchQuery.AsSpan());

        Assert.True(all.Knows(SearchFields.All));
        Assert.Equal(10, all.ResultCount);
        var kinds = new HashSet<EntityKind>();
        for (int i = 0; i < all.ResultCount; i++) kinds.Add(all.ResultRef(i).Kind);
        Assert.True(kinds.Count >= 5);
        Assert.Equal(EntityKind.Artist, all.ResultRef(0).Kind);   // the Top Result

        Assert.Equal(30, all.TotalFor(SearchFacet.Tracks, 0));
        Assert.True(all.Advertises(SearchFacet.Genres));
        Assert.False(all.Advertises(SearchFacet.Episodes));

        var tracks = Entities.Search(Entities.FakeSearchQuery.AsSpan(), SearchFacet.Tracks);
        Assert.Equal(30, tracks.ResultCount);
        Assert.Equal(EdgeState.Complete, tracks.ResultState);

        Assert.Equal(8, Entities.Current.Edges.SearchGenres.Count(all.Slot));
        Assert.Equal(5, Entities.Current.Edges.SearchRelated.Count(all.Slot));
    }

    [Fact]
    public void Recents_spans_the_day_buckets_with_groups_truncated_members_and_a_saved_row()
    {
        Seed();
        var recents = Recents.Me;

        Assert.Equal(EdgeState.Complete, recents.State);
        Assert.Equal(10, recents.Count);
        Assert.Equal("0a1b2c", Entities.Strings.Resolve(recents.Revision));

        var rows = recents.Rows;
        Assert.Equal((Now0 - 40 * 60) * 1000L, rows[0].PlayedAtMs);
        Assert.Equal(RecentsRowKind.Group, rows[1].Shape);
        Assert.Equal(5, rows[1].ChildCount);
        Assert.Equal(3, rows[1].MembersLen);
        Assert.Equal(3, recents.Members(rows[1]).Length);

        int saved = 0, podcasts = 0;
        foreach (var row in rows)
        {
            if (row.Why == RecentsReason.Saved) saved++;
            if (row.Axis == RecentsContentType.Podcasts) podcasts++;
        }
        Assert.Equal(1, saved);
        Assert.Equal(2, podcasts);

        // Oldest row is 41 days back: Today / Yesterday / this week / this month / earlier all have evidence (W16).
        Assert.Equal((Now0 - 41 * 86_400L) * 1000L, rows[^1].PlayedAtMs);
    }

    [Fact]
    public void Seeding_twice_produces_the_same_surfaces()
    {
        Seed();
        int homeSections = Entities.HomeFeed().SectionCount;
        var firstHit = Entities.Search(Entities.FakeSearchQuery.AsSpan()).ResultRef(0);
        string firstItemId = Entities.Strings.Resolve(Recents.Me.Rows[0].ItemId);
        int weeklyCards = Entities.BrowseSection(ChartSections.Weekly.AsSpan()).CardSlots.Length;

        Seed();
        Assert.Equal(homeSections, Entities.HomeFeed().SectionCount);
        Assert.Equal(firstHit, Entities.Search(Entities.FakeSearchQuery.AsSpan()).ResultRef(0));
        Assert.Equal(firstItemId, Entities.Strings.Resolve(Recents.Me.Rows[0].ItemId));
        Assert.Equal(weeklyCards, Entities.BrowseSection(ChartSections.Weekly.AsSpan()).CardSlots.Length);
    }
}
