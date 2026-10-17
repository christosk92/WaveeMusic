// ── Wavee.Tests/ConcertContractTests.cs — the concert surfaces' published keys and Classic's registration (themes plan 6.0, T10b3) ───────────────
//
// The hub, an artist's tour dashboard and the event page are the structure's pictures since T10b3; `Concert.HubHost`, `ScheduleHost` and `DetailHost` keep the
// demands, the filter tuple and its feed subject, the append gate, the readiness and the shaping. What is pure about the contract: the ids (a published id
// never changes), Classic registering every key under its own template id with a complete registry, the three page records never comparing equal (the host
// rebuilds them on every render, so a data-equal record must not let the view skip the render), the values that compare by value (a tail state, a shelf item,
// a feed block's key) and the skeleton's silhouette of the event page. The shaping, the hub's decisions and the offers are `ConcertSchedulePageTests`,
// `ConcertHubModelTests` and `ConcertDetailModelTests`; the width ladders are `ConcertLayoutTests`.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ConcertContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Pages.Concerts", ViewKeys.ConcertsHub.Id);
        Assert.Equal("Pages.ArtistConcerts", ViewKeys.ArtistConcerts.Id);
        Assert.Equal("Pages.Concert", ViewKeys.ConcertDetail.Id);
        Assert.Equal("Concert.Tail", ViewKeys.ConcertTail.Id);
        Assert.Equal("Concert.PlacePicker", ViewKeys.ConcertPlacePicker.Id);
    }

    [Fact]
    public void Classic_registers_every_concert_key_under_its_own_template_id()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("classic.concerts.hub", classic.Registry.TemplateIdOf(ViewKeys.ConcertsHub.Id));
        Assert.Equal("classic.concerts.artist-schedule", classic.Registry.TemplateIdOf(ViewKeys.ArtistConcerts.Id));
        Assert.Equal("classic.concerts.detail", classic.Registry.TemplateIdOf(ViewKeys.ConcertDetail.Id));
        Assert.Equal("classic.concerts.tail", classic.Registry.TemplateIdOf(ViewKeys.ConcertTail.Id));
        Assert.Equal("classic.concerts.place-picker", classic.Registry.TemplateIdOf(ViewKeys.ConcertPlacePicker.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void A_detail_props_record_never_compares_equal_to_another_instance()
    {
        Func<bool> pending = static () => false;
        Func<ConcertDetailView> detail = static () => ConcertDetailView.Seed;
        var a = new ConcertDetailProps { RouteKey = "r", ScrollScope = "s", Pending = pending, Unavailable = false, Detail = detail };
        var b = new ConcertDetailProps { RouteKey = "r", ScrollScope = "s", Pending = pending, Unavailable = false, Detail = detail };
        Assert.Equal(a, a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_tail_state_and_a_shelf_item_compare_by_value()
    {
        Assert.Equal(new ConcertTailProps(ConcertTailState.Spacer), new ConcertTailProps(ConcertTailState.Spacer));
        Assert.NotEqual(new ConcertTailProps(ConcertTailState.Spacer), new ConcertTailProps(ConcertTailState.Shimmer));
        Assert.Equal(new ConcertShelfItem(4, 7u), new ConcertShelfItem(4, 7u));
        Assert.NotEqual(new ConcertShelfItem(4, 7u), new ConcertShelfItem(4, 8u));
    }

    [Fact]
    public void The_event_pages_skeleton_is_the_loaded_silhouette()
    {
        var seed = ConcertDetailView.Seed;
        Assert.Equal(2, seed.Offers.Length);
        Assert.Equal(3, seed.Lineup.Length);
        Assert.Empty(seed.Related);
        Assert.NotNull(seed.Offers[0].Url);        // the first offer's Buy button stands in the shimmer
        Assert.Null(seed.Offers[1].Url);           // the second one is the "not available online" line
        Assert.False(seed.Lineup[2].Navigable);    // the last act is a billing-only row
    }
}
