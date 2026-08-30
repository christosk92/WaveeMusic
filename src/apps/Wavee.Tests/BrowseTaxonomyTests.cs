using System.Linq;
using Wavee.Core;
using Wavee.Features.Browse;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// BrowseTaxonomy is a hand-maintained uri -> band map over Spotify's flat, ungrouped browseAll response (see the
/// class's own doc-comment: "Keyed by page URI, NEVER by title" + "Anything unmapped lands in More"). These tests
/// pin three things a silent edit could break without a reviewer noticing anything wrong in a diff of prose:
///   1. the captured WIRE ids themselves (<see cref="ChartPages"/> / <see cref="ChartSections"/>) — an edited
///      literal silently changes which server resource the Home Charts hub strip and the Browse Charts band read;
///   2. that the map is genuinely KEYED BY the <see cref="ChartPages"/> constants (not a second, re-typed copy of
///      the same id that could drift from the first); and
///   3. <see cref="BrowseTaxonomy.Grouped"/>'s contract INCLUDING the new Charts band — fixed band order, empty
///      bands omitted, Top kept in SERVER order, everything else alphabetised.
///
/// <para>NAMED <c>BrowseChartTaxonomyTests</c> rather than <c>BrowseTaxonomyTests</c> — <c>WireAdornmentTests.cs</c>
/// (a pre-existing file, out of scope for this change) already declares a <c>Wavee.Tests.BrowseTaxonomyTests</c>
/// class with its own (non-Charts) <c>Grouped</c>/<c>GroupOf</c> coverage; a same-named class here would be a
/// duplicate-type compile error (CS0101). This file still lives at the assigned path
/// <c>BrowseTaxonomyTests.cs</c>.</para>
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

    // ── BandOrder: the ONE spelling of the directory's band sequence (T9 dedup) ─────────────────────────────────────

    [Fact]
    public void BandOrder_IsTheExactPinnedSequence()
    {
        // BrowseDirectory.Body walks this list directly (no local copy of its own) to decide top → charts → for you
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

        // Every band the design calls for is present and non-empty — Top (incl. the Live Events client feature),
        // For you, Genres, Mood & activity, and More (the deliberately-unmapped tail).
        Assert.Equal(4, counts[BrowseGroup.Top]);
        Assert.Equal(3, counts[BrowseGroup.ForYou]);
        Assert.Equal(3, counts[BrowseGroup.Genres]);
        Assert.Equal(3, counts[BrowseGroup.MoodActivity]);
        Assert.Equal(3, counts[BrowseGroup.More]);
        Assert.False(counts.ContainsKey(BrowseGroup.Charts));   // Charts is chrome, never a seed category

        Assert.Equal(BrowseDirectorySeeds.Categories.Count, counts.Values.Sum());
    }
}
