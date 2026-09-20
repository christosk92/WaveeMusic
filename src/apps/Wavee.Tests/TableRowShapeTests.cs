using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>`TableRowShape.TemplateOf`/`HeightOf` — pure, engine-free, no source-text reads.</summary>
public class TableRowShapeTests
{
    [Fact]
    public void TemplateOf_reads_episode_and_defaults_everything_else_to_track()
    {
        Assert.Equal(RowTemplate.Episode, TableRowShape.TemplateOf(EntityKind.Episode));
        Assert.Equal(RowTemplate.Track, TableRowShape.TemplateOf(EntityKind.Track));
        Assert.Equal(RowTemplate.Track, TableRowShape.TemplateOf(EntityKind.Unknown));
        Assert.Equal(RowTemplate.Track, TableRowShape.TemplateOf(EntityKind.Album));
    }

    [Theory]
    [InlineData(0, 88f)]
    [InlineData(1, 96f)]
    [InlineData(2, 104f)]
    [InlineData(3, 112f)]
    public void HeightOf_episode_is_density_aware_and_taller_than_a_track_row(int density, float expected)
    {
        Assert.Equal(expected, TableRowShape.HeightOf(RowTemplate.Episode, density, classic: false));
        Assert.True(TableRowShape.HeightOf(RowTemplate.Episode, density, classic: false)
                    > TableRowShape.HeightOf(RowTemplate.Track, density, classic: false));
    }

    [Fact]
    public void HeightOf_track_forwards_to_TableRules_RowHeightFor()
    {
        Assert.Equal(Track.TableRules.RowHeightFor(1, classic: false), TableRowShape.HeightOf(RowTemplate.Track, 1, classic: false));
        Assert.Equal(Track.TableRules.RowHeightFor(1, classic: true), TableRowShape.HeightOf(RowTemplate.Track, 1, classic: true));
    }
}
