// ── Wavee.Tests/HomeUi/ZonePlannerTests.cs — Wave 1, owner A1 ─────────────────────────────────────────────────────

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public class ZonePlannerTests
{
    const long NowMs = 1_800_000_000_000L;
    const long Day = 24 * 60 * 60 * 1000L;

    static readonly ZoneTitles Titles = new(
        MadeForYou: "Made for you", BecauseYouLike: "Because you like…", MoreForYou: "More for you",
        RadioAndMixes: "Radio & mixes for you", Browse: "Browse", JumpBackIn: "Jump back in",
        RecentlyPlayed: "Recently played", NewEpisodes: "New episodes", ContinueListening: "Continue listening",
        VideosYouMightLike: "Videos you might like", EpisodesYouMightLike: "Episodes you might like",
        BecauseYouListenTo: "Because you listen to…", FromArtistsYouFollow: "From artists you follow",
        YourShows: "Your shows", ShowsYouMightLike: "Shows you might like");

    static SectionInput Of(Section s) => SectionReader.Of(s);

    // ── All: exact zone order ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void All_zone_order_is_exact()
    {
        TestScope.Fresh();

        var daylist = Of(HomeUiFixtures.Band("scream teen pop friday morning", SectionKind.HomeSpotlight,
            HomeUiFixtures.Playlist("spotify:playlist:daylist", "scream teen pop friday morning", "daylist")));

        var recents = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed,
            HomeUiFixtures.Playlist("spotify:playlist:recent1", "Liked Songs"),
            HomeUiFixtures.Artist("spotify:artist:recent2", "Rich Brian")));

        var madeForYou = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:dw", "Discover Weekly", "discover-weekly", headerImage: "https://img/dw.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:dm1", "Daily Mix 1", "daily-mix"),
            HomeUiFixtures.Playlist("spotify:playlist:dm2", "Daily Mix 2", "daily-mix")));

        var wide = Of(HomeUiFixtures.Band("It's New Music Friday!", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:nmf1", "New Music Friday", "editorial", headerImage: "https://img/nmf1.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:nmf2", "More New Music", "editorial", headerImage: "https://img/nmf2.jpg")));

        var cluster = Of(HomeUiFixtures.BandWithPreview("More like Avril Lavigne", SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:avril1", "Avril Lavigne Radio")],
            [HomeUiFixtures.Track("spotify:track:avril-t1", "Complicated"), HomeUiFixtures.Track("spotify:track:avril-t2", "Sk8er Boi")]));

        var jumpBackIn = Of(HomeUiFixtures.Band("Jump back in", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:jbi1", "Some Mix"),
            HomeUiFixtures.Album("spotify:album:jbi2", "Some Album")));

        var radio = Of(HomeUiFixtures.Band("Recommended Stations", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:radio1", "Coldplay Radio", "radio")));

        var browse = Of(HomeUiFixtures.Band("Browse", SectionKind.HomeShorts,
            HomeUiFixtures.Playlist("spotify:playlist:browse1", "Some Tile")));

        var sections = new[] { daylist, recents, madeForYou, wide, cluster, jumpBackIn, radio, browse };

        var zones = ZonePlanner.Plan(sections, "", Titles, null, NowMs);

        Assert.Equal(
            [ZoneKind.Daylist, ZoneKind.RecentGrid, ZoneKind.CoverShelf, ZoneKind.WideTiles, ZoneKind.ClusterCards,
              ZoneKind.MixedCovers, ZoneKind.RadioShelf, ZoneKind.BrowseTiles],
            zones.Select(z => z.Kind).ToArray());
    }

    [Fact]
    public void All_folds_leftover_editorial_bands_into_browse_tiles()
    {
        TestScope.Fresh();

        var jumpBackIn = Of(HomeUiFixtures.Band("Jump back in", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:jbi1", "Some Mix"),
            HomeUiFixtures.Album("spotify:album:jbi2", "Some Album")));
        var weekend = Of(HomeUiFixtures.Band("Geniet van de zaterdag", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:wk1", "Summer 2026"),
            HomeUiFixtures.Playlist("spotify:playlist:wk2", "Chill Dance")));
        var more = Of(HomeUiFixtures.Band("More of what you like", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:more1", "Mega Hit Mix")));
        var browse = Of(HomeUiFixtures.Band("Browse", SectionKind.HomeShorts,
            HomeUiFixtures.Playlist("spotify:playlist:browse1", "Some Tile")));

        var zones = ZonePlanner.Plan([jumpBackIn, weekend, more, browse], "", Titles, null, NowMs);

        // One Jump back in shelf, then ONE Browse zone holding the server's browse band first and the two editorial
        // bands after it — never a cover shelf per editorial band.
        Assert.Equal([ZoneKind.MixedCovers, ZoneKind.BrowseTiles], zones.Select(z => z.Kind).ToArray());
        Assert.Equal(["Some Tile", "Summer 2026", "Chill Dance", "Mega Hit Mix"], zones[1].Items.Select(c => c.Title).ToArray());
    }

    [Fact]
    public void Browse_caps_at_three_rows_of_four_tiles()
    {
        TestScope.Fresh();
        var cards = Enumerable.Range(0, 20)
            .Select(i => HomeUiFixtures.Playlist("spotify:playlist:b" + i, "Tile " + i)).ToArray();
        var browse = Of(HomeUiFixtures.Band("Browse", SectionKind.HomeShorts, cards));

        var zones = ZonePlanner.Plan([browse], "", Titles, null, NowMs);

        Assert.Equal(ZonePlanner.BrowseTilesMax, Assert.Single(zones).Items.Count);
    }

    // ── No Daylist/Recents on Music ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Music_never_shows_daylist_or_recents()
    {
        TestScope.Fresh();

        var daylist = Of(HomeUiFixtures.Band("scream teen pop", SectionKind.HomeSpotlight,
            HomeUiFixtures.Playlist("spotify:playlist:m-daylist", "scream teen pop", "daylist")));
        var recents = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed,
            HomeUiFixtures.Playlist("spotify:playlist:m-recent", "Liked Songs")));
        var generic = Of(HomeUiFixtures.Band("Made For Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:m-dm1", "Daily Mix 1", "daily-mix")));

        var zones = ZonePlanner.Plan([daylist, recents, generic], "music-chip", Titles, null, NowMs);

        Assert.DoesNotContain(zones, z => z.Kind is ZoneKind.Daylist or ZoneKind.RecentGrid);
        Assert.Contains(zones, z => z.Kind == ZoneKind.CoverShelf);
    }

    // ── Discover Weekly leads only when it carries a header image ──────────────────────────────────────────────

    [Fact]
    public void Discover_weekly_leads_only_with_header_image()
    {
        TestScope.Fresh();
        var made = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:dm-a", "Daily Mix 1", "daily-mix"),
            HomeUiFixtures.Playlist("spotify:playlist:dw-noheader", "Discover Weekly", "discover-weekly")));

        var zones = ZonePlanner.Plan([made], "", Titles, null, NowMs);
        var shelf = Assert.Single(zones, z => z.Kind == ZoneKind.CoverShelf);
        Assert.Null(shelf.Lead);
    }

    [Fact]
    public void Discover_weekly_leads_when_it_has_a_header_image()
    {
        TestScope.Fresh();
        var made = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:dm-b", "Daily Mix 1", "daily-mix"),
            HomeUiFixtures.Playlist("spotify:playlist:dw-header", "Discover Weekly", "discover-weekly", headerImage: "https://img/dw.jpg")));

        var zones = ZonePlanner.Plan([made], "", Titles, null, NowMs);
        var shelf = Assert.Single(zones, z => z.Kind == ZoneKind.CoverShelf);
        Assert.NotNull(shelf.Lead);
        Assert.Equal("spotify:playlist:dw-header", shelf.Lead!.Value.Uri);
        Assert.Equal(shelf.Lead!.Value.Uri, shelf.Items[0].Uri);
    }

    // ── Page-wide dedupe: a card placed once never reappears lower on the page ─────────────────────────────────

    [Fact]
    public void A_card_placed_once_never_reappears()
    {
        TestScope.Fresh();
        const string dupeUri = "spotify:playlist:dupe";

        var personal = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist(dupeUri, "Daily Mix 1", "daily-mix")));
        var wide = Of(HomeUiFixtures.Band("New Music Friday", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist(dupeUri, "Daily Mix 1", "editorial", headerImage: "https://img/x.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:other", "Other", "editorial", headerImage: "https://img/y.jpg")));

        var zones = ZonePlanner.Plan([personal, wide], "", Titles, null, NowMs);
        int occurrences = zones.SelectMany(z => z.Items).Count(c => c.Uri == dupeUri);
        Assert.Equal(1, occurrences);
    }

    // ── Unplayable ("UnknownType") and DJ cards never appear ───────────────────────────────────────────────────

    [Fact]
    public void Unplayable_cards_are_dropped()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Browse", SectionKind.HomeShorts,
            new HomeUiFixtures.CardSpec("spotify:playlist:unplayable", "Ghost", Unplayable: true)));

        var zones = ZonePlanner.Plan([s], "", Titles, null, NowMs);
        Assert.DoesNotContain(zones, z => z.Kind == ZoneKind.BrowseTiles);
    }

    [Fact]
    public void Dj_cards_are_dropped_everywhere()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:dj", "DJ", "dj"),
            HomeUiFixtures.Playlist("spotify:playlist:dm", "Daily Mix 1", "daily-mix")));

        var zones = ZonePlanner.Plan([s], "", Titles, null, NowMs);
        Assert.DoesNotContain(zones.SelectMany(z => z.Items), c => c.Uri == "spotify:playlist:dj");
        Assert.Contains(zones.SelectMany(z => z.Items), c => c.Uri == "spotify:playlist:dm");
    }

    // ── D2 (F32): one cluster per baseline section, its rows the feedBaselineLookup preview ──────────────────────

    [Fact]
    public void Titled_single_card_section_becomes_its_own_cluster()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.BandWithPreview("For fans of Acda en de Munnik", SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:singleton", "90's Allerbeste")],
            [HomeUiFixtures.Track("spotify:track:singleton-t1", "Het Regent Zonnestralen")]));

        var zones = ZonePlanner.Plan([s], "", Titles, null, NowMs);
        var cluster = Assert.Single(zones, z => z.Kind == ZoneKind.ClusterCards);
        var group = Assert.Single(cluster.Clusters!);
        Assert.Equal("For fans of Acda en de Munnik", group.Over);
        Assert.Equal("90's Allerbeste", group.Name);
    }

    [Fact]
    public void Baseline_BecomesOneClusterPerSection()
    {
        TestScope.Fresh();
        var a = Of(HomeUiFixtures.BandWithPreview("More like Avril Lavigne", SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:bl-a", "Avril Lavigne Radio")],
            [HomeUiFixtures.Track("spotify:track:bl-a1", "Complicated")]));
        var b = Of(HomeUiFixtures.BandWithPreview("For fans of Henry Moodie", SectionKind.HomeBaseline,
            [HomeUiFixtures.Playlist("spotify:playlist:bl-b", "Henry Moodie Radio")],
            [HomeUiFixtures.Track("spotify:track:bl-b1", "Sad Lover")]));

        var zones = ZonePlanner.Plan([a, b], "", Titles, null, NowMs);
        var zone = Assert.Single(zones, z => z.Kind == ZoneKind.ClusterCards);
        Assert.Equal(2, zone.Clusters!.Count);
    }

    [Fact]
    public void Following_AlbumsBecomeReleaseList()
    {
        TestScope.Fresh();
        var sections = new SectionInput[3];
        for (int i = 0; i < 3; i++)
            sections[i] = Of(HomeUiFixtures.BandWithPreview(null, SectionKind.HomeBaseline,
                [HomeUiFixtures.Album($"spotify:album:fol{i}", $"Album {i}")],
                [HomeUiFixtures.Track($"spotify:track:folt{i}", "t")]));

        var zones = ZonePlanner.Plan(sections, "music-following-chip", Titles, null, NowMs);
        var releaseList = Assert.Single(zones, z => z.Kind == ZoneKind.ReleaseList);
        Assert.Equal(3, releaseList.Items.Count);
        Assert.DoesNotContain(zones, z => z.Kind == ZoneKind.ClusterCards);
    }

    [Fact]
    public void WideEditorial_FiresOnHeaderedGeneric()
    {
        // D1 (F31): header_image_url_desktop now writes HeaderImageUrl for every playlist card, not just the
        // daylist — a generic shelf whose cards are mostly headered (>= 2/3 usable) fires WideTiles on the live
        // feed's own shape (a near miss at 6/10 before D1; this fixture is 7/10).
        TestScope.Fresh();
        var cards = new HomeUiFixtures.CardSpec[10];
        for (int i = 0; i < 7; i++)
            cards[i] = HomeUiFixtures.Playlist($"spotify:playlist:we-h{i}", $"Headered {i}", "release-radar", headerImage: $"https://img/h{i}.jpg");
        for (int i = 7; i < 10; i++)
            cards[i] = HomeUiFixtures.Playlist($"spotify:playlist:we-p{i}", $"Plain {i}");

        var s = Of(HomeUiFixtures.Band("It's New Music Friday!", SectionKind.HomeGeneric, cards));

        var zones = ZonePlanner.Plan([s], "", Titles, null, NowMs);
        Assert.Contains(zones, z => z.Kind == ZoneKind.WideTiles);
    }

    // ── Hidden zones are removed ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hidden_zone_kind_is_removed()
    {
        TestScope.Fresh();
        var radio = Of(HomeUiFixtures.Band("Recommended Stations", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:hr1", "Coldplay Radio", "radio")));

        var shown = ZonePlanner.Plan([radio], "", Titles, null, NowMs);
        Assert.Contains(shown, z => z.Kind == ZoneKind.RadioShelf);

        var hidden = ZonePlanner.Plan([radio], "", Titles, k => k == ZoneKind.RadioShelf, NowMs);
        Assert.DoesNotContain(hidden, z => z.Kind == ZoneKind.RadioShelf);
    }

    // ── An empty browse pool produces no zone ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_browse_pool_produces_no_zone()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Browse", SectionKind.HomeShorts,
            new HomeUiFixtures.CardSpec("spotify:playlist:empty1", "Ghost1", Unplayable: true),
            new HomeUiFixtures.CardSpec("spotify:playlist:empty2", "DJ", Format: "dj")));

        var zones = ZonePlanner.Plan([s], "", Titles, null, NowMs);
        Assert.DoesNotContain(zones, z => z.Kind == ZoneKind.BrowseTiles);
    }

    // ── Podcasts is delegated (ZonePlanner never renders it) ───────────────────────────────────────────────────

    [Fact]
    public void Podcasts_facet_returns_empty_zone_list()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("New episodes", SectionKind.HomeGeneric,
            new HomeUiFixtures.CardSpec("spotify:episode:e1", "Some Episode")));

        var zones = ZonePlanner.Plan([s], "podcasts-chip", Titles, null, NowMs);
        Assert.Empty(zones);
    }

    // ── Audiobooks: any section renders; nothing → EmptyFacet ──────────────────────────────────────────────────

    [Fact]
    public void Audiobooks_with_sections_renders_cover_shelves()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Audiobooks for you", SectionKind.HomeGeneric,
            new HomeUiFixtures.CardSpec("spotify:show:ab1", "Some Audiobook")));

        var zones = ZonePlanner.Plan([s], "audiobooks-chip", Titles, null, NowMs);
        Assert.Contains(zones, z => z.Kind == ZoneKind.CoverShelf);
    }

    [Fact]
    public void Audiobooks_with_nothing_usable_is_empty_facet()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Audiobooks for you", SectionKind.HomeGeneric,
            new HomeUiFixtures.CardSpec("spotify:show:ab2", "Ghost", Unplayable: true)));

        var zones = ZonePlanner.Plan([s], "audiobooks-chip", Titles, null, NowMs);
        var only = Assert.Single(zones);
        Assert.Equal(ZoneKind.EmptyFacet, only.Kind);
    }

    // ── D6: a daylist card hoisted out of a Made-for-you shelf (no HomeSpotlight band at all) ─────────────────────

    [Fact]
    public void Daylist_hoists_out_of_a_made_for_you_shelf_with_no_spotlight_band()
    {
        TestScope.Fresh();
        var madeForYou = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:shelf-daylist", "late night chill", "daylist"),
            HomeUiFixtures.Playlist("spotify:playlist:dm1", "Daily Mix 1", "daily-mix"),
            HomeUiFixtures.Playlist("spotify:playlist:dm2", "Daily Mix 2", "daily-mix")));

        var zones = ZonePlanner.Plan([madeForYou], "", Titles, null, NowMs);

        var daylist = Assert.Single(zones, z => z.Kind == ZoneKind.Daylist);
        Assert.Single(daylist.Items);
        Assert.Equal("spotify:playlist:shelf-daylist", daylist.Items[0].Uri);

        // The hoisted card leaves the shelf it was found in — the Made-for-you CoverShelf keeps only the two
        // daily mixes.
        var shelf = Assert.Single(zones, z => z.Kind == ZoneKind.CoverShelf);
        Assert.DoesNotContain(shelf.Items, c => c.Uri == "spotify:playlist:shelf-daylist");
        Assert.Equal(2, shelf.Items.Count);
    }

    [Fact]
    public void No_daylist_card_anywhere_means_no_daylist_zone()
    {
        TestScope.Fresh();
        var madeForYou = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:dm1", "Daily Mix 1", "daily-mix")));

        var zones = ZonePlanner.Plan([madeForYou], "", Titles, null, NowMs);
        Assert.DoesNotContain(zones, z => z.Kind == ZoneKind.Daylist);
    }

    // ── Recently played caps at 8 ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Recents_zone_caps_at_eight_cards()
    {
        TestScope.Fresh();
        var cards = new HomeUiFixtures.CardSpec[10];
        for (int i = 0; i < cards.Length; i++) cards[i] = HomeUiFixtures.Artist($"spotify:artist:r{i}", $"Artist {i}");
        var recents = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed, cards));

        var zones = ZonePlanner.Plan([recents], "", Titles, null, NowMs);
        var grid = Assert.Single(zones, z => z.Kind == ZoneKind.RecentGrid);
        Assert.Equal(8, grid.Items.Count);
        Assert.Equal(ZonePlanner.RecentsCap, grid.Items.Count);
    }

    // ── Data work #3: an optional "music-following-chip" document folds into the same ReleaseList on All ────────

    [Fact]
    public void Following_sections_fold_into_the_release_list_when_provided()
    {
        TestScope.Fresh();
        var following = Of(HomeUiFixtures.BandWithPreview(null, SectionKind.HomeBaseline,
            [HomeUiFixtures.Album("spotify:album:followed1", "Followed Album")],
            [HomeUiFixtures.Track("spotify:track:followed1-t", "t")]));

        var zones = ZonePlanner.Plan([], "", Titles, null, NowMs, following: [following]);

        var releaseList = Assert.Single(zones, z => z.Kind == ZoneKind.ReleaseList);
        Assert.Contains(releaseList.Items, c => c.Uri == "spotify:album:followed1");
    }

    [Fact]
    public void Following_sections_are_omitted_entirely_when_null()
    {
        TestScope.Fresh();
        var zones = ZonePlanner.Plan([], "", Titles, null, NowMs);
        Assert.DoesNotContain(zones, z => z.Kind == ZoneKind.ReleaseList);
    }

    [Fact]
    public void Following_sections_never_duplicate_a_card_the_page_already_placed()
    {
        TestScope.Fresh();
        const string sharedUri = "spotify:album:shared-release";
        // A releases-role section (>= 3 usable cards, all albums, all released within the 42-day window) already
        // places `sharedUri` — the same card also shows up in the separately-fetched `following` document.
        var releases = Of(HomeUiFixtures.Band("New releases for you", SectionKind.HomeGeneric,
            HomeUiFixtures.Album(sharedUri, "Shared", NowMs - 3 * 24 * 60 * 60 * 1000L),
            HomeUiFixtures.Album("spotify:album:other1", "Other 1", NowMs - 2 * 24 * 60 * 60 * 1000L),
            HomeUiFixtures.Album("spotify:album:other2", "Other 2", NowMs - 1 * 24 * 60 * 60 * 1000L)));
        var following = Of(HomeUiFixtures.BandWithPreview(null, SectionKind.HomeBaseline,
            [HomeUiFixtures.Album(sharedUri, "Shared")],
            [HomeUiFixtures.Track("spotify:track:shared-t", "t")]));

        var zones = ZonePlanner.Plan([releases], "", Titles, null, NowMs, following: [following]);

        Assert.Equal(1, zones.SelectMany(z => z.Items).Count(c => c.Uri == sharedUri));
    }

    // ── Data work #6: zoneSub fallback only fires when the server band itself has no subtitle ──────────────────

    [Fact]
    public void ZoneSubtitles_fallback_fills_a_shelf_with_no_server_subtitle()
    {
        TestScope.Fresh();
        var madeForYou = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:zs-dm1", "Daily Mix 1", "daily-mix")));
        var subs = new ZoneSubtitles(MadeForYou: "Daily Mixes and moods picked for you");

        var zones = ZonePlanner.Plan([madeForYou], "", Titles, null, NowMs, subs: subs);

        var shelf = Assert.Single(zones, z => z.Kind == ZoneKind.CoverShelf);
        Assert.Equal("Daily Mixes and moods picked for you", shelf.Subtitle);
    }

    [Fact]
    public void ZoneSubtitles_never_label_a_server_named_wide_shelf()
    {
        TestScope.Fresh();
        // A headered editorial band is WideTiles under the SERVER's title: the New-music fallback line ("Fresh
        // playlists and releases…") would mislabel it, so a subtitle-less server band stays without one.
        var editorial = Of(HomeUiFixtures.Band("Summer hits", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:m1", "Hot Hits", "editorial", headerImage: "https://img/m1.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:m2", "Pop Rising", "editorial", headerImage: "https://img/m2.jpg")));
        var subs = new ZoneSubtitles(NewMusic: "Fresh playlists and releases from artists you follow");

        var zones = ZonePlanner.Plan([editorial], "", Titles, null, NowMs, subs: subs);

        var wide = Assert.Single(zones, z => z.Kind == ZoneKind.WideTiles);
        Assert.Equal("Summer hits", wide.Title);
        Assert.Null(wide.Subtitle);
    }

    [Fact]
    public void Soundtrack_mood_mixes_join_made_for_you_even_when_headered()
    {
        TestScope.Fresh();
        // The live "Soundtrack your…" band: generated `descripto` mixes that now carry desktop header images. They are
        // personal mixes (the "moods" in Made for you), never a 16:9 editorial shelf.
        var mood = Of(HomeUiFixtures.Band("Soundtrack your Tuesday morning", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:mood1", "Workout Pop Mix", "descripto", headerImage: "https://img/mood1.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:mood2", "Hopeless Romantic Love Mix", "descripto", headerImage: "https://img/mood2.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:mood3", "Good Mood Mix", "descripto", headerImage: "https://img/mood3.jpg")));

        var zones = ZonePlanner.Plan([mood], "", Titles, null, NowMs);

        Assert.DoesNotContain(zones, z => z.Kind == ZoneKind.WideTiles);
        var shelf = Assert.Single(zones, z => z.Kind == ZoneKind.CoverShelf);
        Assert.Contains(shelf.Items, c => c.Uri == "spotify:playlist:mood1");
    }

    [Fact]
    public void ZoneSubtitles_fallback_is_absent_without_a_subs_argument()
    {
        TestScope.Fresh();
        var madeForYou = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:zs-dm3", "Daily Mix 1", "daily-mix")));

        var zones = ZonePlanner.Plan([madeForYou], "", Titles, null, NowMs);

        var shelf = Assert.Single(zones, z => z.Kind == ZoneKind.CoverShelf);
        Assert.Null(shelf.Subtitle);
    }

    [Fact]
    public void ZoneSubtitles_fills_the_radio_and_browse_zones_too()
    {
        TestScope.Fresh();
        var radio = Of(HomeUiFixtures.Band("Recommended Stations", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:zs-radio", "Coldplay Radio", "radio")));
        var browse = Of(HomeUiFixtures.Band("Browse", SectionKind.HomeShorts,
            HomeUiFixtures.Playlist("spotify:playlist:zs-browse", "Some Tile")));
        var subs = new ZoneSubtitles(Radio: "Non-stop music based on the artists and songs you play",
            Browse: "Official Spotify playlists and this week's editorial picks");

        var zones = ZonePlanner.Plan([radio, browse], "", Titles, null, NowMs, subs: subs);

        Assert.Equal(subs.Radio, Assert.Single(zones, z => z.Kind == ZoneKind.RadioShelf).Subtitle);
        Assert.Equal(subs.Browse, Assert.Single(zones, z => z.Kind == ZoneKind.BrowseTiles).Subtitle);
    }
}
