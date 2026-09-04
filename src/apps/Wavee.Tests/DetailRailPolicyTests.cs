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

    /// <summary>"Keep left-rail same size" OFF: RailFor keeps resolving each surface to its OWN scope — the status quo,
    /// unchanged.</summary>
    [Theory]
    [InlineData(RailScope.Album)]
    [InlineData(RailScope.Playlist)]
    [InlineData(RailScope.Liked)]
    [InlineData(RailScope.Show)]
    public void ScopeFor_Uniform_Off_KeepsTheRequestedScope(RailScope requested)
        => Assert.Equal(requested, DetailRailPolicy.ScopeFor(requested, uniform: false));

    /// <summary>"Keep left-rail same size" ON: every one of the four per-surface requests collapses onto the SAME
    /// shared scope — the whole point of the setting (resize once, applies everywhere).</summary>
    [Theory]
    [InlineData(RailScope.Album)]
    [InlineData(RailScope.Playlist)]
    [InlineData(RailScope.Liked)]
    [InlineData(RailScope.Show)]
    public void ScopeFor_Uniform_On_CollapsesEveryRequestToOneSharedScope(RailScope requested)
        => Assert.Equal(RailScope.Uniform, DetailRailPolicy.ScopeFor(requested, uniform: true));

    /// <summary>The Uniform scope is a real <see cref="RailScope"/>, not a special case: ClampStored/MinWidthFor still
    /// apply to it exactly as they do to the four surface scopes.</summary>
    [Fact]
    public void ScopeFor_Uniform_StillClampsThroughTheGripBounds()
    {
        var scope = DetailRailPolicy.ScopeFor(RailScope.Liked, uniform: true);
        Assert.Equal(RailScope.Uniform, scope);
        Assert.Equal(DetailRailPolicy.MinWidthFor(scope), DetailRailPolicy.ClampStored(10f, scope));
        Assert.Equal(DetailRailPolicy.MaxWidth, DetailRailPolicy.ClampStored(9999f, scope));
    }

    /// <summary>The reset button's enable gate: every per-scope preference still at its authored default (and every
    /// collapse flag false) means there is nothing to clear.</summary>
    [Fact]
    public void HasCustomizedRailPrefs_AllDefaults_IsFalse()
        => Assert.False(DetailRailPolicy.HasCustomizedRailPrefs(
            DetailRailPolicy.DefaultWidthFor(RailScope.Album), false,
            DetailRailPolicy.DefaultWidthFor(RailScope.Playlist), false,
            DetailRailPolicy.DefaultWidthFor(RailScope.Liked), false,
            DetailRailPolicy.DefaultWidthFor(RailScope.Show), false));

    [Fact]
    public void HasCustomizedRailPrefs_OneWidthMoved_IsTrue()
        => Assert.True(DetailRailPolicy.HasCustomizedRailPrefs(
            DetailRailPolicy.DefaultWidthFor(RailScope.Album), false,
            DetailRailPolicy.DefaultWidthFor(RailScope.Playlist), false,
            300f, false,
            DetailRailPolicy.DefaultWidthFor(RailScope.Show), false));

    [Fact]
    public void HasCustomizedRailPrefs_OneCollapsedFlagSet_IsTrue()
        => Assert.True(DetailRailPolicy.HasCustomizedRailPrefs(
            DetailRailPolicy.DefaultWidthFor(RailScope.Album), false,
            DetailRailPolicy.DefaultWidthFor(RailScope.Playlist), true,
            DetailRailPolicy.DefaultWidthFor(RailScope.Liked), false,
            DetailRailPolicy.DefaultWidthFor(RailScope.Show), false));
}
