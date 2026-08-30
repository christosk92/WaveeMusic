using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class AlbumDrawerRowsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(10, 10)]
    [InlineData(24, 10)]
    public void KnownTrackCount_DrivesLoadingRows(int trackCount, int expected)
        => Assert.Equal(expected, AlbumDrawerRows.PendingCount(trackCount));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnknownTrackCount_UsesCompactFallback(int trackCount)
        => Assert.Equal(3, AlbumDrawerRows.PendingCount(trackCount));
}
