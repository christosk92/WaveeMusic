// ── Wavee.Tests/HomeUi/DaylistSourceTests.cs — Wave 1, owner G ──────────────────────────────────────────────────────

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public class DaylistSourceTests
{
    static SectionInput Of(Section s) => SectionReader.Of(s);

    [Fact]
    public void Spotlight_section_card_wins_over_a_shelf_daylist_card()
    {
        TestScope.Fresh();

        var spotlight = Of(HomeUiFixtures.Band("scream teen pop friday morning", SectionKind.HomeSpotlight,
            HomeUiFixtures.Playlist("spotify:playlist:spotlight-daylist", "scream teen pop friday morning", "daylist")));

        var madeForYou = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:shelf-daylist", "late night chill", "daylist"),
            HomeUiFixtures.Playlist("spotify:playlist:dm1", "Daily Mix 1", "daily-mix")));

        var found = DaylistSource.Find([spotlight, madeForYou]);

        Assert.NotNull(found);
        Assert.Equal("spotify:playlist:spotlight-daylist", found!.Value.Card.Uri);
        Assert.Equal(spotlight.Slot, found.Value.SectionSlot);
    }

    [Fact]
    public void No_spotlight_hoists_the_first_daylist_format_card_from_any_section()
    {
        TestScope.Fresh();

        var madeForYou = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:dm1", "Daily Mix 1", "daily-mix"),
            HomeUiFixtures.Playlist("spotify:playlist:shelf-daylist", "late night chill", "daylist"),
            HomeUiFixtures.Playlist("spotify:playlist:dm2", "Daily Mix 2", "daily-mix")));

        var found = DaylistSource.Find([madeForYou]);

        Assert.NotNull(found);
        Assert.Equal("spotify:playlist:shelf-daylist", found!.Value.Card.Uri);
        Assert.Equal(madeForYou.Slot, found.Value.SectionSlot);
    }

    [Fact]
    public void Server_order_picks_the_earliest_section_first()
    {
        TestScope.Fresh();

        var first = Of(HomeUiFixtures.Band("Editors' picks", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:early-daylist", "morning grind", "daylist")));
        var second = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:late-daylist", "late night chill", "daylist")));

        var found = DaylistSource.Find([first, second]);

        Assert.Equal("spotify:playlist:early-daylist", found!.Value.Card.Uri);
    }

    [Fact]
    public void No_daylist_card_anywhere_returns_null()
    {
        TestScope.Fresh();

        var made = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:dm1", "Daily Mix 1", "daily-mix")));

        Assert.Null(DaylistSource.Find([made]));
    }

    [Fact]
    public void An_unplayable_daylist_card_is_skipped()
    {
        TestScope.Fresh();

        var made = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            new HomeUiFixtures.CardSpec("spotify:playlist:unplayable-daylist", "broken", "daylist", Unplayable: true),
            HomeUiFixtures.Playlist("spotify:playlist:good-daylist", "good daylist", "daylist")));

        var found = DaylistSource.Find([made]);

        Assert.NotNull(found);
        Assert.Equal("spotify:playlist:good-daylist", found!.Value.Card.Uri);
    }

    [Fact]
    public void A_spotlight_band_without_a_daylist_card_is_not_the_daylist()
    {
        TestScope.Fresh();
        // Live 2026-09-27: the spotlight band carried a podcast episode promo; the daylist sat in a shelf.
        var spotlight = Of(HomeUiFixtures.Band("Spotlight", SectionKind.HomeSpotlight,
            HomeUiFixtures.Playlist("spotify:playlist:promo", "Is Anthropic Worth Two Trillion Dollars?", "editorial")));
        var shelf = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:real-daylist", "throwback pop sunday afternoon", "daylist")));

        var found = DaylistSource.Find([spotlight, shelf]);

        Assert.NotNull(found);
        Assert.Equal("spotify:playlist:real-daylist", found!.Value.Card.Uri);
    }
}
