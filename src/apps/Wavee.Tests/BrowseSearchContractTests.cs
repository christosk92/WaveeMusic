// ── Wavee.Tests/BrowseSearchContractTests.cs — Browse's and Search's published keys, Classic's registration and the page rules (themes plan 6.0, T10a2) ──
//
// The Browse directory, its Charts band, a category page, the section drill page and the Search results page are the structure's pictures since T10a2; the
// core hosts keep the load verdicts, the layout and ranking rules, the readiness of each facet and the demands. What is pure about the contract: the ids (a
// published id never changes), Classic registering every key under a distinct template id, the two card-grid facets being the only ones that own their scroll,
// and the Classic Browse ladders staying pinned (`BrowseLayoutTests` pins the column bands themselves).

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class BrowseSearchContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Pages.Browse", ViewKeys.BrowseDirectory.Id);
        Assert.Equal("Browse.Charts", ViewKeys.BrowseCharts.Id);
        Assert.Equal("Pages.BrowseCategory", ViewKeys.BrowseCategory.Id);
        Assert.Equal("Pages.BrowseSection", ViewKeys.BrowseSection.Id);
        Assert.Equal("Pages.Search", ViewKeys.SearchPage.Id);
    }

    [Fact]
    public void Classic_registers_every_browse_and_search_key_under_a_distinct_template_id()
    {
        var classic = ClassicTheme.Create();
        (string Id, string Template)[] expected =
        [
            (ViewKeys.BrowseDirectory.Id, "classic.browse.directory"),
            (ViewKeys.BrowseCharts.Id, "classic.browse.charts"),
            (ViewKeys.BrowseCategory.Id, "classic.browse.category"),
            (ViewKeys.BrowseSection.Id, "classic.browse.section"),
            (ViewKeys.SearchPage.Id, "classic.search.page"),
        ];
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, template) in expected)
        {
            Assert.Equal(template, classic.Registry.TemplateIdOf(id));
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void The_browse_section_drill_page_is_the_home_drill_template_under_its_own_id()
    {
        var classic = ClassicTheme.Create();
        Assert.NotEqual(classic.Registry.TemplateIdOf(ViewKeys.HomeSection.Id), classic.Registry.TemplateIdOf(ViewKeys.BrowseSection.Id));
    }

    [Theory]
    [InlineData(SearchFacet.Albums, true)]
    [InlineData(SearchFacet.Playlists, true)]
    [InlineData(SearchFacet.All, false)]
    [InlineData(SearchFacet.Tracks, false)]
    [InlineData(SearchFacet.Artists, false)]
    [InlineData(SearchFacet.Genres, false)]
    public void Only_the_card_grid_facets_own_their_scroll(SearchFacet facet, bool grid)
        => Assert.Equal(grid, Search.IsGridFacet(facet));

    [Fact]
    public void The_hero_numbers_are_the_cards_geometry()
    {
        Assert.Equal(228f, ClassicSearchRules.HeroHeight);
        Assert.Equal(300f, ClassicSearchRules.HeroCoverW);
        Assert.Equal(260f, ClassicSearchRules.HeroCoverH);
        Assert.Equal(3f, ClassicSearchRules.FacetUnderlineH);
    }
}
