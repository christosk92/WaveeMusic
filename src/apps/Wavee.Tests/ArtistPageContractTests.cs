// ── Wavee.Tests/ArtistPageContractTests.cs — the artist page's published key and Classic's registration (themes plan 6.0, T10b1) ──
//
// The artist page is the structure's picture since T10b1; `Artist.PageHost` keeps the demands, the readiness, the section plan, the hero metrics, the
// accent, the scroll spy and the verbs. What is pure about the contract: the id (a published id never changes), Classic registering the key under its
// own template id with a complete registry, and the props never comparing equal (the host rebuilds them on every render, so a data-equal record must not
// let the view skip the render and keep older Funcs). The plan and its pivot membership are `ArtistPageRulesTests`; the hero tiers are `ArtistHeroLayoutTests`.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ArtistPageContractTests
{
    [Fact]
    public void The_published_id_is_the_plans()
        => Assert.Equal("Pages.Artist", ViewKeys.ArtistPage.Id);

    [Fact]
    public void Classic_registers_the_artist_page_under_its_own_template_id()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("classic.artist.page", classic.Registry.TemplateIdOf(ViewKeys.ArtistPage.Id));
        Assert.NotEqual(classic.Registry.TemplateIdOf(ViewKeys.BrowseDirectory.Id), classic.Registry.TemplateIdOf(ViewKeys.ArtistPage.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void A_pivot_tab_and_a_section_slot_compare_by_value()
    {
        Action go = static () => { };
        Assert.Equal(new ArtistPivotItem("Albums", go), new ArtistPivotItem("Albums", go));
        Assert.Equal(new ArtistSectionSlot(ArtistSection.Albums, null), new ArtistSectionSlot(ArtistSection.Albums, null));
        Assert.NotEqual(new ArtistSectionSlot(ArtistSection.Albums, null), new ArtistSectionSlot(ArtistSection.Singles, null));
    }
}
