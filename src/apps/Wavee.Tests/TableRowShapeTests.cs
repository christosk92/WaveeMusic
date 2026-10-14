using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>The episode row's density ladder (`Track.RowMetrics.EpisodeRowHeightFor`) — pure, engine-free.</summary>
public class TableRowShapeTests
{
    [Theory]
    [InlineData(0, 88f)]
    [InlineData(1, 96f)]
    [InlineData(2, 104f)]
    [InlineData(3, 112f)]
    public void The_episode_row_is_density_aware_and_taller_than_a_track_row(int density, float expected)
    {
        Assert.Equal(expected, Track.RowMetrics.EpisodeRowHeightFor(density));
        Assert.True(Track.RowMetrics.EpisodeRowHeightFor(density) > Track.TableRules.RowHeightFor(density, classic: false));
    }
}
