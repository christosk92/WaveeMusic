// ── Wavee.Tests/HomeContractTests.cs — Home's published keys, Classic's registration and its rules (themes plan 6.0, T10a1) ──
//
// Home is the structure's picture over core hosts: the page (`Pages.Home`), the facet row, the daylist card and clock, the zone bodies
// (`Home.Zone.<Kind>`), the Charts block, the drill page and the customizer. What is pure about the contract: the ids (a published id never
// changes), every zone kind that is not hosted by core state having a body key, Classic registering all of them under distinct template ids, the
// empty facet's copy mapping that stayed core, and the arithmetic of the chapter geometry the page and the zones share.

using Wavee.HomeUi;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class HomeContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Pages.Home", ViewKeys.HomePage.Id);
        Assert.Equal("Home.FacetRow", ViewKeys.HomeFacetRow.Id);
        Assert.Equal("Home.Daylist", ViewKeys.HomeDaylist.Id);
        Assert.Equal("Home.DaylistClock", ViewKeys.HomeDaylistClock.Id);
        Assert.Equal("Home.Zone.RecentGrid", ViewKeys.HomeZoneRecentGrid.Id);
        Assert.Equal("Home.Charts", ViewKeys.HomeCharts.Id);
        Assert.Equal("Pages.HomeSection", ViewKeys.HomeSection.Id);
        Assert.Equal("Pages.HomeCustomize", ViewKeys.HomeCustomize.Id);
    }

    [Fact]
    public void Every_zone_kind_has_a_body_key_except_the_two_that_have_their_own_props()
    {
        foreach (var kind in Enum.GetValues<ZoneKind>())
        {
            bool has = HomeZoneKeys.TryKeyOf(kind, out var key);
            if (kind is ZoneKind.Daylist or ZoneKind.RecentGrid)
            {
                Assert.False(has);
                continue;
            }
            Assert.True(has);
            Assert.Equal("Home.Zone." + kind, key.Id);
        }
    }

    [Fact]
    public void Classic_registers_every_home_key_under_a_distinct_template_id()
    {
        var classic = ClassicTheme.Create();
        var ids = new List<string>
        {
            ViewKeys.HomePage.Id, ViewKeys.HomeFacetRow.Id, ViewKeys.HomeDaylist.Id, ViewKeys.HomeDaylistClock.Id,
            ViewKeys.HomeZoneRecentGrid.Id, ViewKeys.HomeCharts.Id, ViewKeys.HomeSection.Id, ViewKeys.HomeCustomize.Id,
        };
        foreach (var kind in Enum.GetValues<ZoneKind>())
            if (HomeZoneKeys.TryKeyOf(kind, out var key)) ids.Add(key.Id);

        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string template = classic.Registry.TemplateIdOf(id) ?? "";
            Assert.NotEqual("", template);
            // The customizer is a Common block since T11c (one copy for every theme); the rest of Home is Classic's own.
            Assert.StartsWith(id == ViewKeys.HomeCustomize.Id ? "common." : "classic.home.", template, StringComparison.Ordinal);
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.Equal(22, ids.Count);
        Assert.Empty(classic.Validate());
    }

    [Theory]
    [InlineData("home:audiobooks:empty", "Audiobooks")]
    [InlineData("podcasts:empty", "Podcasts")]
    [InlineData("home:empty:music-chip", "Generic")]
    public void The_empty_facet_words_follow_the_planners_zone_key(string zoneKey, string family)
    {
        var (title, body, action) = EmptyFacetCopy.Keys(zoneKey);
        var expected = family switch
        {
            "Audiobooks" => (Strings.Home.Empty.AudiobooksTitle, Strings.Home.Empty.AudiobooksBody, Strings.Home.Empty.AudiobooksAction),
            "Podcasts" => (Strings.Home.Empty.PodcastsTitle, Strings.Home.Empty.PodcastsBody, Strings.Home.Empty.PodcastsAction),
            _ => (Strings.Home.Empty.GenericTitle, Strings.Home.Empty.GenericBody, Strings.Home.Empty.GenericAction),
        };
        Assert.Equal(expected, (title, body, action));
    }

    [Fact]
    public void The_chapter_geometry_is_built_from_the_pinned_band()
    {
        Assert.Equal(ClassicHomeRules.SelectorH + ClassicHomeRules.BarH, ClassicHomeRules.FacetRowH);
        Assert.Equal(ClassicHomeRules.StuckInset + ClassicHomeRules.FacetRowH, ClassicHomeRules.StuckBottom);
        Assert.Equal(ClassicHomeRules.StuckBottom + ClassicHomeRules.HeaderH + ClassicHomeRules.HeaderGap, ClassicHomeRules.BodyClipInset);
        Assert.Equal(40f, ClassicHomeRules.SelectorH);
        Assert.Equal(3f, ClassicHomeRules.BarH);
    }
}
