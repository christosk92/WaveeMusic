using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// Which detail surfaces get a resizable rail, and at which layout mode. The shell used to decide this with a kind test
/// (<c>Album || Playlist</c>) and fall every other kind through to the ALBUM width/collapse pair — so Liked Songs had a
/// seam where the grip should be, and a one-word widening of that predicate would have made it share the album's 280.
/// </summary>
public class DetailRailPolicyTests
{
    [Theory]
    [InlineData(0, true)]    // wide two-column: the grip exists
    [InlineData(1, false)]   // mid: breakpoint rail (224), no grip
    [InlineData(2, false)]   // narrow: breakpoint rail (188), no grip
    [InlineData(DetailLayoutBreakpoints.VerticalMode, false)]   // vertical: no rail at all
    public void OnlyTheWideModeResizes(int mode, bool expected)
        => Assert.Equal(expected, DetailRailPolicy.ResizableFor(railResizable: true, mode));

    [Fact]
    public void AConfigThatOptsOut_NeverResizes()
    {
        for (int mode = 0; mode <= DetailLayoutBreakpoints.VerticalMode; mode++)
            Assert.False(DetailRailPolicy.ResizableFor(railResizable: false, mode));
    }

    /// <summary>Liked opens at the PLAYLIST width (it is a list-like surface), a show at the ALBUM width — and neither
    /// is the other's fallback: each scope is its own persisted pair.</summary>
    [Fact]
    public void DefaultWidths_FollowTheSurfaceFamily()
    {
        Assert.Equal(WaveeSize.RailPlaylist, DetailRailPolicy.DefaultWidthFor(RailScope.Liked));
        Assert.Equal(WaveeSize.RailPlaylist, DetailRailPolicy.DefaultWidthFor(RailScope.Playlist));
        Assert.Equal(WaveeSize.RailAlbum, DetailRailPolicy.DefaultWidthFor(RailScope.Album));
        Assert.Equal(WaveeSize.RailAlbum, DetailRailPolicy.DefaultWidthFor(RailScope.Show));
    }

    /// <summary>A stored width from another build (or a hand-edited store) is clamped to the live grip bounds before it
    /// may seed the layout — per scope, so a raised Liked floor would clamp Liked alone.</summary>
    [Theory]
    [InlineData(RailScope.Album)]
    [InlineData(RailScope.Playlist)]
    [InlineData(RailScope.Liked)]
    [InlineData(RailScope.Show)]
    public void StoredWidths_AreClampedToTheGripBounds(RailScope scope)
    {
        float min = DetailRailPolicy.MinWidthFor(scope);
        Assert.Equal(min, DetailRailPolicy.ClampStored(10f, scope));
        Assert.Equal(DetailRailPolicy.MaxWidth, DetailRailPolicy.ClampStored(9999f, scope));
        Assert.Equal(300f, DetailRailPolicy.ClampStored(300f, scope));
        // The authored default is always inside the bounds, so a first launch never seeds a clamped value.
        float dflt = DetailRailPolicy.DefaultWidthFor(scope);
        Assert.Equal(dflt, DetailRailPolicy.ClampStored(dflt, scope));
    }
}
