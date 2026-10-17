// ── Wavee.Tests/BrowseTaxonomyTests.cs — the directory's band map, the chart ids and the skeleton seeds (ch 13 §8) ──
//
// Ported verbatim from 0.2.9's `BrowseTaxonomyTests.cs` (class `BrowseChartTaxonomyTests`) and the `BrowseTaxonomyTests`
// class of `WireAdornmentTests.cs`. Both classes keep their 0.2.9 names: 0.3 has no WireAdornmentTests file, so the
// CS0101 collision that forced the rename in 0.2.9 is gone and the two sets can live side by side here. The types are
// Entities/Browse.cs's (`BrowseCategory`, `BrowseTaxonomy`, `ChartPages`, `ChartSections`, `BrowseDirectorySeeds`) —
// same names, same shapes; only the namespace changed.

using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// BrowseTaxonomy is a hand-maintained uri -> band map over Spotify's flat, ungrouped browseAll response ("Keyed by page
/// URI, NEVER by title" + "Anything unmapped lands in More"). These tests pin three things a silent edit could break
/// without a reviewer noticing anything wrong in a diff of prose:
///   1. the captured WIRE ids themselves (<see cref="ChartPages"/> / <see cref="ChartSections"/>) — an edited
///      literal silently changes which server resource the Home Charts hub strip and the Browse Charts band read;
///   2. that the map is genuinely KEYED BY the <see cref="ChartPages"/> constants (not a second, re-typed copy of
///      the same id that could drift from the first); and
///   3. <see cref="BrowseTaxonomy.Grouped"/>'s contract INCLUDING the Charts band — fixed band order, empty
///      bands omitted, Top kept in SERVER order, everything else alphabetised.
/// </summary>
public class BrowseChartTaxonomyTests
{
    // ── the captured wire ids ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChartPageAndSectionIds_AreThePinnedWireValues()
    {
        Assert.Equal("spotify:page:0JQ5DAudkNjCgYMM0TZXDw", ChartPages.Charts);
        Assert.Equal("spotify:page:0JQ5DAB3zgCauRwnvdEQjJ", ChartPages.PodcastCharts);
        Assert.Equal("spotify:section:0JQ5DAzQHECxDlYNI6xD1g", ChartSections.Featured);
    }

    // ── taxonomy membership ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BothChartPages_GroupUnderCharts_KeyedByTheSharedConstant()
    {
        // GroupOf must resolve through the SAME ChartPages.* constant the Map is keyed by — a re-typed literal here
        // (even one that is byte-for-byte identical today) is exactly the kind of duplication that drifts later.
        Assert.Equal(BrowseGroup.Charts, BrowseTaxonomy.GroupOf(new BrowseCategory(ChartPages.Charts, "Charts", null)));
        Assert.Equal(BrowseGroup.Charts,
            BrowseTaxonomy.GroupOf(new BrowseCategory(ChartPages.PodcastCharts, "Podcast Charts", null)));
    }

    [Fact]
    public void UnmappedCategory_LandsInMore_RatherThanVanishing()
    {
        var uncurated = new BrowseCategory("spotify:page:brandNewCategoryNobodyCuratedYet", "New Thing", null);
        Assert.Equal(BrowseGroup.More, BrowseTaxonomy.GroupOf(uncurated));
    }

    // ── Grouped: band order, empty-band omission, Top order, alphabetisation ───────────────────────────────────────

    [Fact]
    public void Grouped_OrdersBandsFixed_AndOmitsBandsWithNoCategories()
    {
        var categories = new[]
        {
            new BrowseCategory("spotify:page:0JQ5DAqbMKFETqK4t8f1n3", "Audiobooks", null),   // Top
            new BrowseCategory("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", "Music", null),        // Top
            new BrowseCategory(ChartPages.Charts, "Charts", null),                            // Charts
            new BrowseCategory("spotify:page:0JQ5DAqbMKFDXXwE9BDJAr", "Rock", null),          // Genres
            new BrowseCategory("spotify:page:totallyUnmapped", "Mystery", null),              // More
            // Deliberately no ForYou, no MoodActivity category in this input — those bands must not appear at all.
        };

        var grouped = BrowseTaxonomy.Grouped(categories);

        Assert.Equal([BrowseGroup.Top, BrowseGroup.Charts, BrowseGroup.Genres, BrowseGroup.More],
            grouped.Select(g => g.Group));
    }

    [Fact]
    public void Grouped_KeepsTopInServerOrder_ButAlphabetisesEveryOtherBand()
    {
        var categories = new[]
        {
            // Server order: Music, Podcasts, Audiobooks, Live Events — a deliberate ranking, NOT alphabetical.
            new BrowseCategory("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", "Music", null),
            new BrowseCategory("spotify:page:0JQ5DArNBzkmxXHCqFLx2J", "Podcasts", null),
            new BrowseCategory("spotify:page:0JQ5DAqbMKFETqK4t8f1n3", "Audiobooks", null),
            new BrowseCategory("spotify:concerts", "Live Events", null),
            // Genres, fed out of alphabetical order.
            new BrowseCategory("spotify:page:0JQ5DAqbMKFDXXwE9BDJAr", "Rock", null),
            new BrowseCategory("spotify:page:0JQ5DAqbMKFFtlLYUHv8bT", "Alternative", null),
            new BrowseCategory("spotify:page:0JQ5DAqbMKFPrEiAOxgac3", "Classical", null),
        };

        var grouped = BrowseTaxonomy.Grouped(categories);

        var top = grouped.Single(g => g.Group == BrowseGroup.Top).Items;
        Assert.Equal(["Music", "Podcasts", "Audiobooks", "Live Events"], top.Select(c => c.Title));

        var genres = grouped.Single(g => g.Group == BrowseGroup.Genres).Items;
        Assert.Equal(["Alternative", "Classical", "Rock"], genres.Select(c => c.Title));
    }

    // ── the named Chart section constants ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChartSections_All_IsTheFetchList_FeaturedFirst()
    {
        // Home's Charts row and Browse's Charts band fetch exactly All, in this order. Featured is All[0] so a
        // null there is the fail-loud signal; later shelves that come back null/empty are omitted.
        Assert.Equal(
            [ChartSections.Featured, ChartSections.Weekly, ChartSections.Daily,
             ChartSections.NowAvailable, ChartSections.Podcast],
            ChartSections.All);
        Assert.Equal(ChartSections.All.Count, ChartSections.All.Distinct().Count());
    }

    [Fact]
    public void ChartSections_Contains_IsAnExactMatchInBothSpellings()
    {
        // 0.3 addition: the decoder's chart bit and the fake seed ask this. Base62 ids are case-sensitive, so a case
        // variant is a DIFFERENT section.
        foreach (var uri in ChartSections.All)
        {
            Assert.True(ChartSections.Contains(uri));
            Assert.True(ChartSections.Contains(uri.AsSpan()));
        }
        Assert.False(ChartSections.Contains("spotify:section:0JQ5DAzQHECxDlYNI6xD1G"));
        Assert.False(ChartSections.Contains(ChartPages.Charts));
        Assert.False(ChartSections.Contains((string?)null));
        Assert.False(ChartSections.Contains(""));
        Assert.False(ChartSections.Contains(ReadOnlySpan<char>.Empty));
    }

    // ── BandOrder: the ONE spelling of the directory's band sequence (T9 dedup) ─────────────────────────────────────

    [Fact]
    public void BandOrder_IsTheExactPinnedSequence()
    {
        // The directory body walks this list directly (no local copy of its own) to decide top → charts → for you
        // → genres → mood → more — a reordering here silently reorders the rendered directory.
        Assert.Equal(
            [BrowseGroup.Top, BrowseGroup.Charts, BrowseGroup.ForYou,
             BrowseGroup.Genres, BrowseGroup.MoodActivity, BrowseGroup.More],
            BrowseTaxonomy.BandOrder);
    }

    // ── BrowseDirectorySeeds: the skeleton's shape IS these seeds' taxonomy grouping ───────────────────────────────

    [Fact]
    public void DirectorySkeletonSeeds_GroupIntoEveryBand_WithTheirIntendedCounts()
    {
        // Read against the REAL taxonomy (BrowseTaxonomy.Grouped), not a re-derived expectation: a Map edit that
        // moves one of these seed uris to a different band must fail this test, because that is exactly the silent
        // reshaping of the loading directory this pin exists to catch.
        var grouped = BrowseTaxonomy.Grouped(BrowseDirectorySeeds.Categories);
        var counts = grouped.ToDictionary(g => g.Group, g => g.Items.Count);

        // Every band the design calls for is present — Top (incl. the Live Events client feature), For you, Genres,
        // Mood & activity, and More (the deliberately-unmapped tail). The mapped bands carry EVERY member the taxonomy
        // maps, so the loading directory wraps to the same rows as the loaded one; More's size is the server's, so it
        // keeps a few placeholders.
        Assert.Equal(4, counts[BrowseGroup.Top]);
        Assert.Equal(10, counts[BrowseGroup.ForYou]);
        Assert.Equal(25, counts[BrowseGroup.Genres]);
        Assert.Equal(14, counts[BrowseGroup.MoodActivity]);
        Assert.Equal(3, counts[BrowseGroup.More]);
        Assert.False(counts.ContainsKey(BrowseGroup.Charts));   // Charts is chrome, never a seed category

        Assert.Equal(BrowseDirectorySeeds.Categories.Count, counts.Values.Sum());
    }

    [Fact]
    public void DirectorySkeletonSeeds_MirrorTheMapEntryForEntry_AndOnlyLiveEventsIsAFeature()
    {
        // 0.3 addition: the seeds are BUILT from UrisOf, so a band's seed uris are its map uris in map order.
        foreach (var band in new[] { BrowseGroup.Top, BrowseGroup.ForYou, BrowseGroup.Genres, BrowseGroup.MoodActivity })
        {
            var seeded = BrowseDirectorySeeds.Categories.Where(c => BrowseTaxonomy.GroupOf(c) == band).Select(c => c.Uri);
            Assert.Equal(BrowseTaxonomy.UrisOf(band), seeded);
        }
        Assert.All(BrowseDirectorySeeds.Categories, c => Assert.Equal(" ", c.Title));
        Assert.Equal("spotify:concerts", Assert.Single(BrowseDirectorySeeds.Categories, c => c.IsClientFeature).Uri);
        Assert.Equal(2, BrowseTaxonomy.UrisOf(BrowseGroup.Charts).Count);
        Assert.Empty(BrowseTaxonomy.UrisOf(BrowseGroup.More));
    }
}

public class BrowseTaxonomyTests
{
    static BrowseCategory Cat(string uri, string title) => new(uri, title, null);

    [Fact]
    public void Grouped_PlacesKnownUrisInTheirBand_AndKeepsTopInServerOrder()
    {
        var cats = new[]
        {
            Cat("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", "Music"),
            Cat("spotify:page:0JQ5DArNBzkmxXHCqFLx2J", "Podcasts"),
            Cat("spotify:page:0JQ5DAqbMKFDXXwE9BDJAr", "Rock"),
            Cat("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", "Pop"),
        };

        var groups = BrowseTaxonomy.Grouped(cats);

        var top = groups.First(g => g.Group == BrowseGroup.Top);
        Assert.Equal(new[] { "Music", "Podcasts" }, top.Items.Select(i => i.Title));   // server order, NOT alphabetical

        var genres = groups.First(g => g.Group == BrowseGroup.Genres);
        Assert.Equal(new[] { "Pop", "Rock" }, genres.Items.Select(i => i.Title));      // alphabetised within the band
    }

    // A category Spotify adds tomorrow must still appear — unmapped falls to More rather than vanishing.
    [Fact]
    public void Grouped_UnknownUriFallsToMoreInsteadOfDisappearing()
    {
        var groups = BrowseTaxonomy.Grouped(new[] { Cat("spotify:page:brand-new", "Something New") });
        var more = Assert.Single(groups);
        Assert.Equal(BrowseGroup.More, more.Group);
        Assert.Equal("Something New", Assert.Single(more.Items).Title);
    }

    [Fact]
    public void Grouped_EmptyInputYieldsNoBands() => Assert.Empty(BrowseTaxonomy.Grouped(System.Array.Empty<BrowseCategory>()));

    [Fact]
    public void GroupOf_ClientFeatureLiveEventsIsTop()
        => Assert.Equal(BrowseGroup.Top, BrowseTaxonomy.GroupOf(new BrowseCategory("spotify:concerts", "Live Events", null, null, true)));

    [Fact]
    public void GroupOf_TheUriOverloadAgreesWithTheCategoryOverload()
    {
        // 0.3 addition: a page resolves a tile's band from its uri without building a category.
        Assert.Equal(BrowseGroup.Genres, BrowseTaxonomy.GroupOf("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw"));
        Assert.Equal(BrowseGroup.MoodActivity, BrowseTaxonomy.GroupOf("spotify:page:0JQ5DAqbMKFCbimwdOYlsl"));
        Assert.Equal(BrowseGroup.More, BrowseTaxonomy.GroupOf("spotify:page:0jq5daqbmkfec4wftonrpw"));   // never by a case fold
    }
}

// ── the bands as a route argument and as page-chrome row 2 (P5 ZC1) ──────────────────────────────────────────────────

/// <summary>
/// Browse's bands (top, for you, genres, charts, concerts) are sibling ROUTES, not page state: the Browse route carries `foryou` or `genres` in
/// its arg, Charts is the Charts category page and Concerts the hub. These pin the id to route to id round trip, the codec (the arg folds, a hostile
/// one is the plain directory, a band switch is a new place but the same keep-alive slot), the lit rule, the row-2 views, the directory filter,
/// and that Classic's band order is unchanged.
/// </summary>
[Collection(EntitiesCollection.Name)]
public class BrowseBandRouteTests
{
    const string PopGenre = "spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw";         // Genres (by the genre spelling of the page uri)
    const string PopPage = "spotify:page:0JQ5DAqbMKFEC4WFtoNRpw";
    const string MadeForYou = "spotify:page:0JQ5DAt0tbjZptfcdMSKl3";        // ForYou
    const string Music = "spotify:page:0JQ5DAqbMKFSi39LMRT0Cy";             // Top
    const string Focus = "spotify:page:0JQ5DAqbMKFCbimwdOYlsl";             // Mood & activity

    static Shell.Route Category(string uri) => BrowseTiles.PageRoute(uri, "x");

    [Fact]
    public void Classics_band_order_is_unchanged()
        => Assert.Equal(
            new[] { BrowseGroup.Top, BrowseGroup.Charts, BrowseGroup.ForYou, BrowseGroup.Genres, BrowseGroup.MoodActivity, BrowseGroup.More },
            BrowseTaxonomy.BandOrder.ToArray());

    [Fact]
    public void Row_2_is_top_for_you_genres_charts_concerts()
        => Assert.Equal(new[] { "top", "foryou", "genres", "charts", "concerts" }, BrowseBands.All.ToArray());

    [Theory]
    [InlineData("foryou", "foryou")]
    [InlineData("genres", "genres")]
    [InlineData(" ForYou ", "foryou")]
    [InlineData("GENRES", "genres")]
    [InlineData("top", "")]
    [InlineData("charts", "")]
    [InlineData("concerts", "")]
    [InlineData("", "")]
    [InlineData("nonsense", "")]
    [InlineData("spotify:page:abc", "")]
    public void Canonical_keeps_the_two_filtering_bands_and_folds_everything_else_to_the_plain_directory(string arg, string expected)
        => Assert.Equal(expected, BrowseBands.Canonical(arg));

    [Theory]
    [InlineData("foryou")]
    [InlineData("genres")]
    public void A_filtering_band_round_trips_through_the_route_key_and_arg(string band)
    {
        var route = BrowseBands.RouteOf(band);

        Assert.Equal(Shell.RouteKind.Browse, route.Kind);
        Assert.Equal("browse", Shell.NameOf(route));
        Assert.Equal(band, Shell.ArgOf(route));
        Assert.Equal(route, Shell.Parse(Shell.NameOf(route), Shell.ArgOf(route)));
        Assert.Equal(band, BrowseBands.BandOf(route));
    }

    [Fact]
    public void Top_is_the_plain_directory_with_no_arg()
    {
        var route = BrowseBands.RouteOf("top");

        Assert.Equal(Shell.RouteKind.Browse, route.Kind);
        Assert.Null(Shell.ArgOf(route));
        Assert.Equal(route, Shell.Parse("browse"));
        Assert.Equal("top", BrowseBands.BandOf(route));
    }

    [Fact]
    public void Charts_is_the_charts_category_page_and_concerts_is_the_hub()
    {
        var charts = BrowseBands.RouteOf("charts");
        Assert.Equal(Shell.RouteKind.BrowseCategory, charts.Kind);
        Assert.Equal(ChartPages.Charts, charts.Subject.Text);
        Assert.Equal("charts", BrowseBands.BandOf(charts));

        var concerts = BrowseBands.RouteOf("concerts");
        Assert.Equal(Shell.RouteKind.Concerts, concerts.Kind);
        Assert.Equal("concerts", BrowseBands.BandOf(concerts));

        Assert.True(BrowseBands.RouteOf("bogus").IsNone);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("charts")]
    [InlineData("concerts")]
    [InlineData("top")]
    [InlineData("")]
    public void A_stray_arg_on_the_browse_key_parses_to_the_plain_directory(string arg)
    {
        var route = Shell.Parse("browse", arg);

        Assert.Equal(Shell.RouteKind.Browse, route.Kind);
        Assert.True(route.Arg.IsEmpty);
        Assert.Null(Shell.ArgOf(route));
    }

    [Fact]
    public void The_arg_parses_case_insensitively_to_its_canonical_spelling()
        => Assert.Equal("genres", Shell.ArgOf(Shell.Parse("browse", " GeNres ")));

    [Fact]
    public void A_deep_link_carries_the_band()
    {
        var verb = Shell.DeepLink("wavee://open?route=browse&arg=genres");

        Assert.Equal(Shell.DeepLinkKind.Open, verb.Kind);
        Assert.Equal(BrowseBands.RouteOf("genres"), verb.Route);

        var junk = Shell.DeepLink("wavee://open?route=browse&arg=junk");
        Assert.Equal(Shell.RouteKind.Browse, junk.Route.Kind);
        Assert.True(junk.Route.Arg.IsEmpty);
    }

    [Fact]
    public void A_band_switch_is_a_new_place_in_the_same_keep_alive_slot_and_never_a_title()
    {
        var all = new Shell.Route(Shell.RouteKind.Browse);
        var genres = BrowseBands.RouteOf("genres");

        Assert.True(Shell.Row(Shell.RouteKind.Browse).PlaceByArg);
        Assert.False(Shell.Row(Shell.RouteKind.Browse).KeyedByArg);
        Assert.False(Shell.SameSlot(all, genres));                                   // history, Back and the tab's stored route see a new place
        Assert.Equal(Shell.SlotKey(all), Shell.SlotKey(genres));                     // ...but the mounted directory is reused
        Assert.True(Shell.SameSlot(genres, BrowseBands.RouteOf("genres")));
        Assert.Equal(Shell.Dest(all).Title, Shell.Dest(genres).Title);               // the tab and history word is "Browse", never "genres"
    }

    // ── the lit rule ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChartPages.Charts, "charts")]
    [InlineData(ChartPages.PodcastCharts, "charts")]
    [InlineData(PopGenre, "genres")]
    [InlineData(PopPage, "genres")]
    [InlineData(MadeForYou, "foryou")]
    [InlineData(Music, "top")]
    [InlineData(Focus, "top")]                                                        // Mood & activity stays in the whole directory
    [InlineData("spotify:page:brandNewCategoryNobodyCuratedYet", "top")]              // More, and anything unmapped
    public void A_category_page_lights_the_band_of_its_taxonomy_group(string uri, string band)
        => Assert.Equal(band, BrowseBands.BandOf(Category(uri)));

    [Theory]
    [InlineData(Shell.RouteKind.Home)]
    [InlineData(Shell.RouteKind.Search)]
    [InlineData(Shell.RouteKind.Album)]
    [InlineData(Shell.RouteKind.Concert)]
    [InlineData(Shell.RouteKind.ArtistConcerts)]
    [InlineData(Shell.RouteKind.BrowseSection)]
    public void Other_routes_light_no_band(Shell.RouteKind kind)
    {
        Assert.Null(BrowseBands.BandOf(new Shell.Route(kind)));
        Assert.False(BrowseBands.IsChromeKind(kind));
        Assert.Empty(BrowseBands.ChromeViews(new Shell.Route(kind)));
    }

    [Fact]
    public void Browse_the_category_pages_and_the_concerts_hub_keep_the_browse_destination_lit_in_the_band()
    {
        Assert.Equal(Shell.BandRules.Browse, Shell.BandRules.IndexOf(Shell.RouteKind.Browse));
        Assert.Equal(Shell.BandRules.Browse, Shell.BandRules.IndexOf(Shell.RouteKind.BrowseCategory));
        Assert.Equal(Shell.BandRules.Browse, Shell.BandRules.IndexOf(Shell.RouteKind.Concerts));
    }

    // ── row 2 ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("top")]
    [InlineData("foryou")]
    [InlineData("genres")]
    [InlineData("charts")]
    [InlineData("concerts")]
    public void Every_browse_family_route_answers_the_five_bands_in_order_with_its_own_lit(string band)
    {
        var views = BrowseBands.ChromeViews(BrowseBands.RouteOf(band));

        Assert.Equal(new[] { "top", "foryou", "genres", "charts", "concerts" }, views.Select(v => v.Id).ToArray());
        var lit = Assert.Single(views, v => v.Current);
        Assert.Equal(band, lit.Id);
        Assert.All(views, v => Assert.False(string.IsNullOrEmpty(v.Label)));
        Assert.All(views, v => Assert.Null(v.Count));
    }

    [Fact]
    public void The_verbs_are_cached_so_an_equal_row_compares_equal()
    {
        var a = BrowseBands.ChromeViews(new Shell.Route(Shell.RouteKind.Browse));
        var b = BrowseBands.ChromeViews(new Shell.Route(Shell.RouteKind.Concerts));

        for (int i = 0; i < a.Count; i++) Assert.Same(a[i].Go, b[i].Go);
    }

    // ── the directory filter ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_directory_filter_keeps_everything_for_the_whole_directory_and_one_group_for_a_filtering_band()
    {
        var groups = BrowseTaxonomy.Grouped(new[]
        {
            new BrowseCategory(Music, "Music", null),
            new BrowseCategory(MadeForYou, "Made For You", null),
            new BrowseCategory(PopPage, "Pop", null),
            new BrowseCategory(ChartPages.Charts, "Charts", null),
            new BrowseCategory(Focus, "Focus", null),
        });

        Assert.Same(groups, BrowseBands.Filter(groups, ""));                         // the host's memo keeps its reference

        var forYou = Assert.Single(BrowseBands.Filter(groups, "foryou"));
        Assert.Equal(BrowseGroup.ForYou, forYou.Group);
        Assert.Equal("Made For You", Assert.Single(forYou.Items).Title);

        var genres = Assert.Single(BrowseBands.Filter(groups, "genres"));
        Assert.Equal(BrowseGroup.Genres, genres.Group);

        Assert.Empty(BrowseBands.Filter(BrowseTaxonomy.Grouped(new[] { new BrowseCategory(Music, "Music", null) }), "genres"));
    }
}
