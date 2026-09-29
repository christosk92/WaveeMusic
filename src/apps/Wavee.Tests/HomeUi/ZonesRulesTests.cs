// ── Wavee.Tests/HomeUi/ZonesRulesTests.cs — Home/Zones.Rules.cs (Wave 3, owner B2) ─────────────────────────────────────
//
// ReleaseListRules' type badge / date column, and ShelfLead's lead-merge normalisation (both ZonePlanner shapes:
// AddCoverShelf's lead-already-at-Items[0], and RadioMerge's lead-removed-from-Items).

using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public sealed class ReleaseListRulesTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(HomeCardKind.Album, "Album")]
    [InlineData(HomeCardKind.Playlist, "Playlist")]
    [InlineData(HomeCardKind.Artist, "Artist")]
    [InlineData(HomeCardKind.Track, "Song")]
    [InlineData(HomeCardKind.Episode, "Episode")]
    [InlineData(HomeCardKind.Podcast, "Podcast")]
    [InlineData(HomeCardKind.Audiobook, "Audiobook")]
    public void TypeLabel_NamesEveryKind(HomeCardKind kind, string expected)
        => Assert.Equal(expected, ReleaseListRules.TypeLabel(kind));

    [Fact]
    public void TypeLabel_Liked_IsBlank()
        => Assert.Equal("", ReleaseListRules.TypeLabel(HomeCardKind.Liked));

    [Fact]
    public void DateLabel_Unknown_IsBlank()
        => Assert.Equal("", ReleaseListRules.DateLabel(0, 1_000_000, Utc, En));

    [Fact]
    public void DateLabel_Future_RendersAShortCalendarDate_NotAWhenCaptionRung()
    {
        // 2025-01-15T00:00:00Z, "now" a day earlier — an upcoming release.
        long released = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long now = new DateTimeOffset(2025, 1, 14, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("15 Jan", ReleaseListRules.DateLabel(released, now, Utc, En));
    }

    [Fact]
    public void DateLabel_Past_UsesTheWhenCaptionLadder()
    {
        long now = new DateTimeOffset(2025, 1, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long released = new DateTimeOffset(2025, 1, 19, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("Yesterday", ReleaseListRules.DateLabel(released, now, Utc, En));
    }
}

[Collection(EntitiesCollection.Name)]
public sealed class ShelfLeadTests
{
    [Fact]
    public void NoLead_ReturnsItemsUnchanged()
    {
        TestScope.Fresh();
        var section = HomeUiFixtures.Band(null, SectionKind.HomeGeneric,
            HomeUiFixtures.Album("spotify:album:a1", "A1"),
            HomeUiFixtures.Album("spotify:album:a2", "A2"));
        var items = CardsOf(section);

        var merged = ShelfLead.Merge(items, null);

        Assert.Same(items, merged);
    }

    [Fact]
    public void LeadAlreadyAtIndexZero_IsNotDuplicated()
    {
        TestScope.Fresh();
        // AddCoverShelf's shape: the lead is re-inserted into Items[0] by the planner already.
        var section = HomeUiFixtures.Band(null, SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:dw", "Discover Weekly", headerImage: "https://img/dw.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:dm1", "Daily Mix 1"));
        var items = CardsOf(section);
        var lead = items[0];

        var merged = ShelfLead.Merge(items, lead);

        Assert.Equal(2, merged.Count);
        Assert.Equal(lead.Uri, merged[0].Uri);
    }

    [Fact]
    public void LeadRemovedFromItems_IsPrependedExactlyOnce()
    {
        TestScope.Fresh();
        // RadioMerge's shape: the lead was removed from Items and carried only via Zone.Lead.
        var section = HomeUiFixtures.Band(null, SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:r1", "Station 1"),
            HomeUiFixtures.Playlist("spotify:playlist:r2", "Station 2", headerImage: "https://img/r2.jpg"));
        var all = CardsOf(section);
        var lead = all[1];
        var withoutLead = new List<HomeCard> { all[0] };

        var merged = ShelfLead.Merge(withoutLead, lead);

        Assert.Equal(2, merged.Count);
        Assert.Equal(lead.Uri, merged[0].Uri);
        Assert.Equal(all[0].Uri, merged[1].Uri);
    }

    static IReadOnlyList<HomeCard> CardsOf(Section s) => SectionReader.Of(s).Cards;

    [Fact]
    public void SquareWidth_SplitsTheLeadColumnAcrossTheSpanMinusItsGaps()
        => Assert.Equal(165f, ShelfLead.SquareWidth(342f, 12f, 2));

    [Fact]
    public void LeadAspect_MatchesTheStackedSquaresHeight()
        => Assert.Equal(326f / 149f, ShelfLead.LeadAspect(342f, 12f, 2, 8f), precision: 5);

    [Fact]
    public void LeadAspect_OneToOne_WhenLeadWidthEqualsTheSpanOfSquares()
    {
        // A single-column span (span=1): the lead IS the one square, so its aspect is always 1 regardless of pad.
        float squareW = ShelfLead.SquareWidth(200f, 12f, 1);
        Assert.Equal(200f, squareW);
        Assert.Equal(1f, ShelfLead.LeadAspect(200f, 12f, 1, 8f), precision: 5);
    }
}
