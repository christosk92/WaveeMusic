// ── Wavee.Tests/HomeUi/HomeCardGridShapeTests.cs — what a HomeCard grid cell shows per kind (Home/Items.Rules.cs) ───
//
// #157: the section drill grid renders the app's ONE media surface through `HomeCards.GridCardData`; the per-kind
// decisions that adapter makes (round art, the chart's missing subtitle, who drags, who has a menu) are this pure value.

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public sealed class HomeCardGridShapeTests
{
    [Fact]
    public void Artist_IsCircular_OthersAreNot()
    {
        Assert.True(HomeCardGridShape.Of(HomeCardKind.Artist, charts: false).Circular);
        Assert.False(HomeCardGridShape.Of(HomeCardKind.Playlist, charts: false).Circular);
        Assert.False(HomeCardGridShape.Of(HomeCardKind.Album, charts: false).Circular);
        Assert.False(HomeCardGridShape.Of(HomeCardKind.Podcast, charts: false).Circular);
    }

    [Fact]
    public void Charts_HideTheSubtitle()
    {
        Assert.False(HomeCardGridShape.Of(HomeCardKind.Playlist, charts: true).ShowSubtitle);
        Assert.True(HomeCardGridShape.Of(HomeCardKind.Playlist, charts: false).ShowSubtitle);
    }

    [Theory]
    [InlineData(HomeCardKind.Track, false)]
    [InlineData(HomeCardKind.Episode, false)]
    [InlineData(HomeCardKind.Playlist, true)]
    [InlineData(HomeCardKind.Album, true)]
    [InlineData(HomeCardKind.Artist, true)]
    [InlineData(HomeCardKind.Liked, true)]
    [InlineData(HomeCardKind.Podcast, true)]
    [InlineData(HomeCardKind.Audiobook, true)]
    public void TrackAndEpisode_AreNotDragSources(HomeCardKind kind, bool canDrag)
        => Assert.Equal(canDrag, HomeCardGridShape.Of(kind, charts: false).CanDrag);

    [Theory]
    [InlineData(HomeCardKind.Episode, false)]
    [InlineData(HomeCardKind.Track, true)]
    [InlineData(HomeCardKind.Playlist, true)]
    [InlineData(HomeCardKind.Album, true)]
    [InlineData(HomeCardKind.Artist, true)]
    [InlineData(HomeCardKind.Liked, true)]
    [InlineData(HomeCardKind.Podcast, true)]
    [InlineData(HomeCardKind.Audiobook, true)]
    public void Episode_HasNoMenu_TrackDoes(HomeCardKind kind, bool canMenu)
        => Assert.Equal(canMenu, HomeCardGridShape.Of(kind, charts: false).CanMenu);
}
