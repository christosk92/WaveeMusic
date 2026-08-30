using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class ChartTitleMatchTests
{
    [Fact]
    public void Arg_HighlightsArgentina()
    {
        Assert.True(ChartTitleMatch.TryFind("Top Songs - Argentina", "arg", out int start, out int length));
        Assert.Equal("Top Songs - Argentina".IndexOf("Arg", System.StringComparison.Ordinal), start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void EmptyQuery_DoesNotMatch()
    {
        Assert.False(ChartTitleMatch.TryFind("Top Songs - Argentina", "  ", out _, out _));
        Assert.False(ChartTitleMatch.TryFind("Top Songs - Argentina", null, out _, out _));
    }

    [Fact]
    public void Filter_KeepsOnlyTitleHits()
    {
        HomeCard[] cards =
        [
            new("spotify:playlist:a", "Top Songs - Argentina", null, null, HomeCardKind.Playlist),
            new("spotify:playlist:b", "Top Songs - Belgium", null, null, HomeCardKind.Playlist),
            new("spotify:playlist:c", "Top Songs - Global", null, null, HomeCardKind.Playlist),
        ];
        var hits = ChartTitleMatch.Filter(cards, "bel");
        var one = Assert.Single(hits);
        Assert.Equal("spotify:playlist:b", one.Uri);
    }
}
