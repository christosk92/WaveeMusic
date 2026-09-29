// ── Wavee.Tests/HomeUi/PageDedupeTests.cs — Wave 1, owner A1 ──────────────────────────────────────────────────────

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public class PageDedupeTests
{
    static SectionInput Of(Section s) => SectionReader.Of(s);

    [Fact]
    public void A_card_already_placed_is_skipped()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Shelf", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:pd-1", "One"),
            HomeUiFixtures.Playlist("spotify:playlist:pd-2", "Two")));

        var placed = new HashSet<long>();
        placed.Add(s.Cards[0].DedupeKey);                                  // simulate the card already claimed above

        var result = PageDedupe.Place(placed, s.Cards, 0);
        Assert.Single(result);
        Assert.Equal("spotify:playlist:pd-2", result[0].Uri);
    }

    [Fact]
    public void Two_calls_never_place_the_same_card_twice()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Shelf", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:pd-3", "One"),
            HomeUiFixtures.Playlist("spotify:playlist:pd-4", "Two")));

        var placed = new HashSet<long>();
        var first = PageDedupe.Place(placed, s.Cards, 0);
        var second = PageDedupe.Place(placed, s.Cards, 0);

        Assert.Equal(2, first.Count);
        Assert.Empty(second);
    }

    [Fact]
    public void Cap_bounds_the_result_top_down()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Shelf", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:pd-5", "One"),
            HomeUiFixtures.Playlist("spotify:playlist:pd-6", "Two"),
            HomeUiFixtures.Playlist("spotify:playlist:pd-7", "Three")));

        var placed = new HashSet<long>();
        var result = PageDedupe.Place(placed, s.Cards, 2);
        Assert.Equal(2, result.Count);
        Assert.Equal(["spotify:playlist:pd-5", "spotify:playlist:pd-6"], result.Select(c => c.Uri).ToArray());
    }

    [Fact]
    public void Unplayable_and_dj_cards_never_count_against_the_cap()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Shelf", SectionKind.HomeGeneric,
            new HomeUiFixtures.CardSpec("spotify:playlist:pd-ghost", "Ghost", Unplayable: true),
            new HomeUiFixtures.CardSpec("spotify:playlist:pd-dj", "DJ", Format: "dj"),
            HomeUiFixtures.Playlist("spotify:playlist:pd-real", "Real")));

        var placed = new HashSet<long>();
        var result = PageDedupe.Place(placed, s.Cards, 1);
        Assert.Single(result);
        Assert.Equal("spotify:playlist:pd-real", result[0].Uri);
    }

    [Fact]
    public void Zero_or_negative_cap_is_unbounded()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Shelf", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:pd-8", "One"),
            HomeUiFixtures.Playlist("spotify:playlist:pd-9", "Two")));

        var placed = new HashSet<long>();
        Assert.Equal(2, PageDedupe.Place(placed, s.Cards, 0).Count);
    }
}
