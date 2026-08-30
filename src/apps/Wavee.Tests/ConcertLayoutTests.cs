using Wavee.Features.Concerts;
using Xunit;

namespace Wavee.Tests;

public class ConcertLayoutTests
{
    [Fact]
    public void ScheduleWide_UsesSeparateEnterAndLeaveThresholds()
    {
        Assert.False(ConcertLayout.ScheduleWide(740f, wasWide: false));
        Assert.True(ConcertLayout.ScheduleWide(760f, wasWide: false));
        Assert.True(ConcertLayout.ScheduleWide(740f, wasWide: true));
        Assert.False(ConcertLayout.ScheduleWide(719f, wasWide: true));
    }

    [Fact]
    public void DetailWide_UsesSeparateEnterAndLeaveThresholds()
    {
        Assert.False(ConcertLayout.DetailWide(900f, wasWide: false));
        Assert.True(ConcertLayout.DetailWide(920f, wasWide: false));
        Assert.True(ConcertLayout.DetailWide(880f, wasWide: true));
        Assert.False(ConcertLayout.DetailWide(859f, wasWide: true));
    }

    [Theory]
    [InlineData(1000f, 288f, 28f, 3)]
    [InlineData(700f, 240f, 24f, 2)]
    [InlineData(420f, 220f, 20f, 2)]
    public void WideEditorial_ChangesMetricsWithoutChangingComposition(
        float width, float expectedHeight, float expectedPadding, int expectedLines)
    {
        var metrics = ConcertLayout.WideEditorial(width);

        Assert.Equal(expectedHeight, metrics.Height);
        Assert.Equal(expectedPadding, metrics.Padding);
        Assert.Equal(expectedLines, metrics.SubtitleLines);
        Assert.InRange(metrics.ArtworkWidth(width), metrics.ArtworkMin, Math.Min(metrics.ArtworkMax, width));
    }
}
