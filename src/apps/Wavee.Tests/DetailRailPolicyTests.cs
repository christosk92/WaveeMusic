// ── Wavee.Tests/DetailRailPolicyTests.cs — which surfaces resize their rail, and which persisted pair they read ─────
//
// Ported from _old/Wavee.Tests/DetailRailPolicyTests.cs onto `Detail.RailPolicy` (Entities/Detail.cs); every 0.2.9
// assertion is kept, only the call shape changed. Added in 0.3 (G-190): the rail/page CONVERGENCE facts — the persisted
// key defaults, the policy's authored defaults and the design tokens are one number per scope — and `KeysFor`, which
// maps each scope to its own key pair (the "ten orphaned keys" had no reader before it).
//
// The shell used to decide the grip with a kind test and fall every other kind through to the ALBUM pair — so Liked had
// a seam where the grip should be, and a one-word widening of that predicate would have made it share the album's 280.

using Xunit;
using Breakpoints = Wavee.Detail.Breakpoints;
using RailPolicy = Wavee.Detail.RailPolicy;

namespace Wavee.Tests;

public class DetailRailPolicyTests
{
    [Theory]
    [InlineData(0, true)]    // wide two-column: the grip exists
    [InlineData(1, false)]   // mid: breakpoint rail (224), no grip
    [InlineData(2, false)]   // narrow: breakpoint rail (188), no grip
    [InlineData(Breakpoints.VerticalMode, false)]   // vertical: no rail at all
    public void OnlyTheWideModeResizes(int mode, bool expected)
        => Assert.Equal(expected, RailPolicy.ResizableFor(railResizable: true, mode));

    [Fact]
    public void AConfigThatOptsOut_NeverResizes()
    {
        for (int mode = 0; mode <= Breakpoints.VerticalMode; mode++)
            Assert.False(RailPolicy.ResizableFor(railResizable: false, mode));
    }

    /// <summary>Liked opens at the PLAYLIST width, a show at the ALBUM width — and neither is the other's fallback.</summary>
    [Fact]
    public void DefaultWidths_FollowTheSurfaceFamily()
    {
        Assert.Equal(Design.Size.RailPlaylist, RailPolicy.DefaultWidthFor(RailScope.Liked));
        Assert.Equal(Design.Size.RailPlaylist, RailPolicy.DefaultWidthFor(RailScope.Playlist));
        Assert.Equal(Design.Size.RailAlbum, RailPolicy.DefaultWidthFor(RailScope.Album));
        Assert.Equal(Design.Size.RailAlbum, RailPolicy.DefaultWidthFor(RailScope.Show));
    }

    /// <summary>A stored width from another build (or a hand-edited store) is clamped to the live grip bounds, per scope.</summary>
    [Theory]
    [InlineData(RailScope.Album)]
    [InlineData(RailScope.Playlist)]
    [InlineData(RailScope.Liked)]
    [InlineData(RailScope.Show)]
    public void StoredWidths_AreClampedToTheGripBounds(RailScope scope)
    {
        float min = RailPolicy.MinWidthFor(scope);
        Assert.Equal(min, RailPolicy.ClampStored(10f, scope));
        Assert.Equal(RailPolicy.MaxWidth, RailPolicy.ClampStored(9999f, scope));
        Assert.Equal(300f, RailPolicy.ClampStored(300f, scope));
        float dflt = RailPolicy.DefaultWidthFor(scope);
        Assert.Equal(dflt, RailPolicy.ClampStored(dflt, scope));
    }

    /// <summary>"Keep left-rail same size" OFF: each surface resolves to its OWN scope.</summary>
    [Theory]
    [InlineData(RailScope.Album)]
    [InlineData(RailScope.Playlist)]
    [InlineData(RailScope.Liked)]
    [InlineData(RailScope.Show)]
    public void ScopeFor_Uniform_Off_KeepsTheRequestedScope(RailScope requested)
        => Assert.Equal(requested, RailPolicy.ScopeFor(requested, uniform: false));

    /// <summary>"Keep left-rail same size" ON: every request collapses onto the ONE shared scope.</summary>
    [Theory]
    [InlineData(RailScope.Album)]
    [InlineData(RailScope.Playlist)]
    [InlineData(RailScope.Liked)]
    [InlineData(RailScope.Show)]
    public void ScopeFor_Uniform_On_CollapsesEveryRequestToOneSharedScope(RailScope requested)
        => Assert.Equal(RailScope.Uniform, RailPolicy.ScopeFor(requested, uniform: true));

    [Fact]
    public void ScopeFor_Uniform_StillClampsThroughTheGripBounds()
    {
        var scope = RailPolicy.ScopeFor(RailScope.Liked, uniform: true);
        Assert.Equal(RailScope.Uniform, scope);
        Assert.Equal(RailPolicy.MinWidthFor(scope), RailPolicy.ClampStored(10f, scope));
        Assert.Equal(RailPolicy.MaxWidth, RailPolicy.ClampStored(9999f, scope));
    }

    [Fact]
    public void HasCustomizedRailPrefs_AllDefaults_IsFalse()
        => Assert.False(RailPolicy.HasCustomizedRailPrefs(
            RailPolicy.DefaultWidthFor(RailScope.Album), false,
            RailPolicy.DefaultWidthFor(RailScope.Playlist), false,
            RailPolicy.DefaultWidthFor(RailScope.Liked), false,
            RailPolicy.DefaultWidthFor(RailScope.Show), false));

    [Fact]
    public void HasCustomizedRailPrefs_OneWidthMoved_IsTrue()
        => Assert.True(RailPolicy.HasCustomizedRailPrefs(
            RailPolicy.DefaultWidthFor(RailScope.Album), false,
            RailPolicy.DefaultWidthFor(RailScope.Playlist), false,
            300f, false,
            RailPolicy.DefaultWidthFor(RailScope.Show), false));

    [Fact]
    public void HasCustomizedRailPrefs_OneCollapsedFlagSet_IsTrue()
        => Assert.True(RailPolicy.HasCustomizedRailPrefs(
            RailPolicy.DefaultWidthFor(RailScope.Album), false,
            RailPolicy.DefaultWidthFor(RailScope.Playlist), true,
            RailPolicy.DefaultWidthFor(RailScope.Liked), false,
            RailPolicy.DefaultWidthFor(RailScope.Show), false));

    // ── rail/page convergence (G-190) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>One number per scope, spelled three ways: the persisted key's default, the policy's authored default and
    /// the design token. A rail that opens at one width and resets to another is exactly this drifting.</summary>
    [Fact]
    public void PersistedDefaults_PolicyDefaults_AndDesignTokens_Converge()
    {
        Assert.Equal(Platform.Keys.DetailAlbumRailWidth.Default, RailPolicy.DefaultWidthFor(RailScope.Album));
        Assert.Equal(Platform.Keys.DetailPlaylistRailWidth.Default, RailPolicy.DefaultWidthFor(RailScope.Playlist));
        Assert.Equal(Platform.Keys.DetailLikedRailWidth.Default, RailPolicy.DefaultWidthFor(RailScope.Liked));
        Assert.Equal(Platform.Keys.DetailShowRailWidth.Default, RailPolicy.DefaultWidthFor(RailScope.Show));

        Assert.Equal(Design.Size.RailAlbum, Platform.Keys.DetailAlbumRailWidth.Default);
        Assert.Equal(Design.Size.RailPlaylist, Platform.Keys.DetailPlaylistRailWidth.Default);
        Assert.Equal(Design.Size.RailPlaylist, Platform.Keys.DetailLikedRailWidth.Default);
        Assert.Equal(Design.Size.RailAlbum, Platform.Keys.DetailShowRailWidth.Default);
    }

    [Fact]
    public void UniformRail_DefaultsToTheListWidth()
    {
        Assert.Equal(240f, Platform.Keys.DetailUniformRailWidth.Default);
        Assert.Equal(Platform.Keys.DetailUniformRailWidth.Default, RailPolicy.DefaultWidthFor(RailScope.Uniform));
        Assert.False(Platform.Keys.DetailUniformRailCollapsed.Default);
    }

    /// <summary>Each scope reads and writes its OWN pair — never another scope's by fallthrough.</summary>
    [Fact]
    public void KeysFor_MapsEachScopeToItsOwnKeyPair()
    {
        Assert.Equal((Platform.Keys.DetailAlbumRailWidth, Platform.Keys.DetailAlbumRailCollapsed), RailPolicy.KeysFor(RailScope.Album));
        Assert.Equal((Platform.Keys.DetailPlaylistRailWidth, Platform.Keys.DetailPlaylistRailCollapsed), RailPolicy.KeysFor(RailScope.Playlist));
        Assert.Equal((Platform.Keys.DetailLikedRailWidth, Platform.Keys.DetailLikedRailCollapsed), RailPolicy.KeysFor(RailScope.Liked));
        Assert.Equal((Platform.Keys.DetailShowRailWidth, Platform.Keys.DetailShowRailCollapsed), RailPolicy.KeysFor(RailScope.Show));
        Assert.Equal((Platform.Keys.DetailUniformRailWidth, Platform.Keys.DetailUniformRailCollapsed), RailPolicy.KeysFor(RailScope.Uniform));
    }

    /// <summary>The five pairs are ten DISTINCT keys, and every pair opens at its scope's authored default, uncollapsed.</summary>
    [Fact]
    public void KeysFor_TenDistinctKeys_EachOpeningAtItsScopesDefault()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var scope = RailScope.Album; scope <= RailScope.Uniform; scope++)
        {
            var (width, collapsed) = RailPolicy.KeysFor(scope);
            Assert.True(names.Add(width.Name), $"width key {width.Name} is shared by another scope");
            Assert.True(names.Add(collapsed.Name), $"collapsed key {collapsed.Name} is shared by another scope");
            Assert.Equal(RailPolicy.DefaultWidthFor(scope), width.Default);
            Assert.False(collapsed.Default);
        }
        Assert.Equal(10, names.Count);
    }
}
