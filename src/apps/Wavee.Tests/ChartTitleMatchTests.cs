// ── Wavee.Tests/ChartTitleMatchTests.cs — the Charts grid's title filter (ported from 0.2.9) ─────────────────────────
//
// 0.2.9 built three HomeCard RECORDS; 0.3 cards are handles, so the three playlists go through HomeFixtures (the real
// commit) and the filter reads their live titles.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ChartTitleMatchTests
{
    [Fact]
    public void Arg_HighlightsArgentina()
    {
        Assert.True(ChartTitleMatch.TryFind("Top Songs - Argentina", "arg", out int start, out int length));
        Assert.Equal("Top Songs - Argentina".IndexOf("Arg", StringComparison.Ordinal), start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void The_query_is_trimmed_before_it_is_matched_and_measured()
    {
        Assert.True(ChartTitleMatch.TryFind("Top Songs - Argentina", "  arg ", out int start, out int length));
        Assert.Equal(12, start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void EmptyQuery_DoesNotMatch()
    {
        Assert.False(ChartTitleMatch.TryFind("Top Songs - Argentina", "  ", out _, out _));
        Assert.False(ChartTitleMatch.TryFind("Top Songs - Argentina", null, out _, out _));
        Assert.False(ChartTitleMatch.TryFind(null, "arg", out _, out _));
        Assert.False(ChartTitleMatch.TryFind("Top Songs - Global", "arg", out _, out _));
    }

    [Fact]
    public void Filter_KeepsOnlyTitleHits()
    {
        TestScope.Fresh();
        var cards = HomeFixtures.Cards("Charts", SectionKind.BrowseShelf,
            HomeFixtures.Playlist("spotify:playlist:chart-match-a", "Top Songs - Argentina"),
            HomeFixtures.Playlist("spotify:playlist:chart-match-b", "Top Songs - Belgium"),
            HomeFixtures.Playlist("spotify:playlist:chart-match-c", "Top Songs - Global"));

        var hits = ChartTitleMatch.Filter(cards, "bel");
        var one = Assert.Single(hits);
        Assert.Equal("spotify:playlist:chart-match-b", one.Uri);
    }

    [Fact]
    public void Filter_with_no_query_is_the_same_list()
    {
        TestScope.Fresh();
        var cards = HomeFixtures.Cards("Charts", SectionKind.BrowseShelf,
            HomeFixtures.Playlist("spotify:playlist:chart-match-d", "Top Songs - Denmark"));
        Assert.Same(cards, ChartTitleMatch.Filter(cards, null));
        Assert.Same(cards, ChartTitleMatch.Filter(cards, "   "));
    }
}
