// ── Wavee.Tests/HomeUi/ChartTilePickTests.cs — Home's Charts tiles pick their playlists (Home/ChartTiles.Rules.cs) ────
//
// RCA 2026-09-30: "Top 50" / "Viral 50" open the ACTUAL playlists, picked from the Featured Charts section's items —
// the first item in section order whose trimmed title starts with the tile's prefix, ignoring case — and fall back to
// the Charts category page while the section has not landed or carries no match.

using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public sealed class ChartTilePickTests
{
    static FeaturedChartItem P(string id, string? title) => new("spotify:playlist:" + id, title);

    [Theory]
    [InlineData("Top 50 - Global", "Top 50", true)]
    [InlineData("  top 50 – Netherlands ", "Top 50", true)]      // leading/trailing whitespace, lower case
    [InlineData("TOP 50", "Top 50", true)]                        // the prefix alone
    [InlineData("Viral 50 - Global", "Viral 50", true)]
    [InlineData("viral 50 – Netherlands", "  Viral 50 ", true)]   // the prefix is trimmed too
    [InlineData("Top Songs - Global", "Top 50", false)]
    [InlineData("Global Top 50", "Top 50", false)]                 // contains, but does not START with
    [InlineData("Viral 50 - Global", "Top 50", false)]
    [InlineData("", "Top 50", false)]
    [InlineData("   ", "Top 50", false)]
    [InlineData(null, "Top 50", false)]
    [InlineData("Top 50 - Global", "", false)]                     // a blank prefix never matches
    public void Matches_TrimmedCaseInsensitivePrefix(string? title, string prefix, bool expected)
        => Assert.Equal(expected, ChartTilePick.Matches(title, prefix));

    [Fact]
    public void Pick_TakesTheFirstMatchInSectionOrder()
    {
        FeaturedChartItem[] items =
        [
            P("songs", "Top Songs - Global"),
            P("nl", "Top 50 - Netherlands"),
            P("global", "Top 50 - Global"),
        ];

        var target = ChartTilePick.Pick(items, ChartTilePick.Top50Prefix);

        Assert.False(target.IsFallback);
        Assert.Equal("spotify:playlist:nl", target.Uri);
        Assert.Equal("Top 50 - Netherlands", target.Title);
    }

    [Fact]
    public void Pick_CarriesTheTrimmedTitle_AsTheRouteArg()
    {
        var target = ChartTilePick.Pick([P("g", "  Top 50 - Global  ")], ChartTilePick.Top50Prefix);
        Assert.Equal("Top 50 - Global", target.Title);
    }

    [Fact]
    public void Pick_SkipsAnItemWithNoUri_EvenWhenItsTitleMatches()
    {
        FeaturedChartItem[] items = [new("", "Top 50 - Global"), P("nl", "Top 50 - Netherlands")];
        Assert.Equal("spotify:playlist:nl", ChartTilePick.Pick(items, ChartTilePick.Top50Prefix).Uri);
    }

    [Fact]
    public void Pick_NoMatch_IsTheCategoryPageFallback()
    {
        FeaturedChartItem[] items = [P("songs", "Top Songs - Global"), P("albums", "Top Albums - Global")];

        var target = ChartTilePick.Pick(items, ChartTilePick.Viral50Prefix);

        Assert.True(target.IsFallback);
        Assert.Equal(ChartTileTarget.Fallback, target);
    }

    [Fact]
    public void Pick_SectionNotLanded_IsTheCategoryPageFallback()
        => Assert.True(ChartTilePick.Pick([], ChartTilePick.Top50Prefix).IsFallback);

    [Fact]
    public void Of_PicksBothTilesIndependently()
    {
        // The captured Featured Charts shape: four playlists, the Top 50 and Viral 50 among them in any order.
        FeaturedChartItem[] items =
        [
            P("songs", "Top Songs - Global"),
            P("viral", "Viral 50 - Global"),
            P("top", "Top 50 - Global"),
            P("albums", "Top Albums - Global"),
        ];

        var model = ChartTilePick.Of(items);

        Assert.Equal(new ChartTileTarget("spotify:playlist:top", "Top 50 - Global"), model.Top50);
        Assert.Equal(new ChartTileTarget("spotify:playlist:viral", "Viral 50 - Global"), model.Viral50);
    }

    [Fact]
    public void Of_OneTileMatched_TheOtherFallsBack()
    {
        var model = ChartTilePick.Of([P("viral", "Viral 50 - Global")]);

        Assert.True(model.Top50.IsFallback);
        Assert.Equal("spotify:playlist:viral", model.Viral50.Uri);
    }

    [Fact]
    public void Of_EmptySection_IsTheFallbackModel()
        => Assert.Equal(ChartTilesModel.Fallback, ChartTilePick.Of([]));

    [Fact]
    public void Of_EqualInputs_GiveEqualModels_SoTheMemoStaysSilent()
    {
        FeaturedChartItem[] a = [P("top", "Top 50 - Global"), P("viral", "Viral 50 - Global")];
        FeaturedChartItem[] b = [P("top", "Top 50 - Global"), P("viral", "Viral 50 - Global")];
        Assert.Equal(ChartTilePick.Of(a), ChartTilePick.Of(b));
    }
}
