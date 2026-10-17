// ── Wavee.ShellTests/BrowseSearchMountedTests.cs — Browse's and Search's views, mounted (themes plan 6.1, T10a2) ──────────────────────────────
//
// The Browse directory, its Charts band, a category page, the section drill page and the Search results page are the structure's since T10a2; the core
// hosts keep the load verdicts, the layout and ranking rules, the readiness of each facet and the demands (`Wavee.Tests` pins the pure ones:
// `BrowseSearchContractTests`, `BrowseLayoutTests`, `BrowseLoadGateTests`, `SearchFacetRulesTests`, `SearchHitDataTests`). What only a mounted tree can say:
//
//   registered      Classic is the registered template of every key
//   category page   a tile block is a LABEL header over the link grid, each tile keyed by its destination's own uri, and the Explore link follows
//   search page     the facet row is the skeleton until the host says the first body answered, then one tab carries the grown underline; the facet body is
//                   keyed by the facet; the query echo opens the page

using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class BrowseSearchMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_browse_and_search_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.BrowseDirectory.Id, "classic.browse.directory"),
                (ViewKeys.BrowseCharts.Id, "classic.browse.charts"),
                (ViewKeys.BrowseCategory.Id, "classic.browse.category"),
                (ViewKeys.BrowseSection.Id, "classic.browse.section"),
                (ViewKeys.SearchPage.Id, "classic.search.page"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    // ── the category page ────────────────────────────────────────────────────────────────────────────────────────────────

    static BrowseCategoryProps CategoryProps(params BrowseBand[] bands) => new()
    {
        Load = BrowseLoad.Ready,
        Title = "Decades",
        PageUri = "spotify:page:decades",
        ScrollKey = "test:browse:decades",
        Mode = BrowsePageLayout.Mode.Shelves,
        Bands = bands,
        Merged = Array.Empty<HomeCard>(),
        Retry = static () => { },
        OpenCategory = static (_, _) => { },
        OpenSection = static _ => { },
        ExploreAll = static () => { },
    };

    [Fact]
    public void A_tile_block_stands_each_tile_under_its_destinations_own_uri()
    {
        var band = new BrowseBand
        {
            Facts = new BrowseSectionFacts("spotify:section:decades", "Decades", BrowseSectionKind.CategoryGrid, CardCount: 0, CategoryCount: 3, Total: 3),
            Tiles =
            [
                new BrowseCategory("spotify:page:00s", "00s", null),
                new BrowseCategory("spotify:page:90s", "90s", 0xFF336699u),
                new BrowseCategory("spotify:page:80s", "80s", null),
            ],
        };
        ViewHarness.With(host, ViewKeys.BrowseCategory, CategoryProps(band),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("browse-cats:spotify:section:decades"), "the block is keyed by its section uri");
                foreach (string uri in new[] { "spotify:page:00s", "spotify:page:90s", "spotify:page:80s" })
                    Assert.True(harness.Contains(uri), "the tile of " + uri);
            }, UnderClassic());
    }

    // ── the search page ──────────────────────────────────────────────────────────────────────────────────────────────────

    static SearchPageProps SearchProps(bool skeletal, SearchFacet facet, int selected) => new()
    {
        Vacant = false,
        Echo = "daft punk",
        ChipsSkeletal = skeletal,
        Tabs = [new SearchTab(SearchFacet.All, 0), new SearchTab(SearchFacet.Tracks, 12), new SearchTab(SearchFacet.Albums, 3)],
        Selected = selected,
        Selectors = [static () => { }, static () => { }, static () => { }],
        Facet = facet,
        Slide = SearchSlide.None,
        ScrollKey = "test:search:" + (int)facet,
        Pending = static () => false,
        Failed = static () => false,
        Body = () => new SearchBodyModel
        {
            Kind = SearchBodyKind.Empty, Facet = facet, Hits = new Search.HitContext("daft punk", null, null),
        },
        Retry = static () => { },
        RetryAll = static () => { },
        SelectPlaylists = static () => { },
        ColsFor = static _ => 2,
    };

    [Fact]
    public void The_facet_row_is_the_skeleton_until_the_first_body_has_answered()
        => ViewHarness.With(host, ViewKeys.SearchPage, SearchProps(skeletal: true, SearchFacet.All, selected: 0),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("facets-skel"), "the eleven placeholder pills");
                Assert.False(harness.Contains("facets"), "no real tabs yet");
                Assert.False(harness.Contains("underline"));
            }, UnderClassic());

    [Fact]
    public void The_real_facet_row_grows_one_underline_under_the_selected_tab_and_keys_the_body_by_the_facet()
        => ViewHarness.With(host, ViewKeys.SearchPage, SearchProps(skeletal: false, SearchFacet.Tracks, selected: 1),
            harness =>
            {
                harness.RunFrames(60);   // the row rides an 8-DIP rise and the underline grows from its left edge: measure at rest
                Assert.True(harness.Contains("facets"));
                Assert.False(harness.Contains("facets-skel"));
                Assert.Single(harness.FindPrefix("underline"));
                Assert.True(harness.Contains("facet-body:" + (int)SearchFacet.Tracks), "the facet body is keyed by its facet");
            }, UnderClassic());

    [Fact]
    public void A_blank_query_is_the_generic_empty_vacancy_with_no_facet_row()
    {
        var props = SearchProps(skeletal: false, SearchFacet.All, selected: 0) with { Vacant = true, Tabs = [] };
        ViewHarness.With(host, ViewKeys.SearchPage, props,
            harness =>
            {
                harness.RunFrames(10);
                Assert.False(harness.Contains("facets"));
                Assert.False(harness.Contains("facets-skel"));
            }, UnderClassic());
    }
}
