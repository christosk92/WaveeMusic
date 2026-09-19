// ── Wavee.Tests/DetailRailPolicyTests.cs — which surfaces resize their rail, and which persisted pair they read ─────
//
// Ported from _old/Wavee.Tests/DetailRailPolicyTests.cs onto `Detail.RailPolicy` (Entities/Detail.cs); every 0.2.9
// assertion is kept, only the call shape changed. Added in 0.3 (G-190): the rail/page CONVERGENCE facts — the persisted
// key defaults, the policy's authored defaults and the design tokens are one number per scope — and `KeysFor`, which
// maps each scope to its own key pair (the "ten orphaned keys" had no reader before it).
//
// The shell used to decide the grip with a kind test and fall every other kind through to the ALBUM pair — so Liked had
// a seam where the grip should be, and a one-word widening of that predicate would have made it share the album's 280.
//
// The narrow arms resize too now: mode 0 needs 820 DIP of page, so opening a transcript/lyrics side rail dropped the
// frame to mode 1/2 and `Detail.UI.cs` stopped composing the grip child at all — the rail became unresizable on every
// detail page. The second region below is that fix's contract: `ComposesRail`/`ResizableFor`, the PAGE-AWARE ceiling
// `MaxWidthForPage` (quantized, content-floor-preserving), `RestingWidth` (the breakpoint rail until the scope has been
// dragged, then the persisted width — clamped, never written back) and `CommitWidth`.

using Xunit;
using Breakpoints = Wavee.Detail.Breakpoints;
using RailPolicy = Wavee.Detail.RailPolicy;

namespace Wavee.Tests;

public class DetailRailPolicyTests
{
    /// <summary>EVERY two-column arm resizes. The narrow arms used to compose a FIXED rail and drop the grip child
    /// outright, so opening a transcript/lyrics side rail — which pushes a detail page under the 820 DIP mode 0 needs —
    /// silently took the grip away on every detail page: album, playlist, Liked, artist, show, episode.</summary>
    [Theory]
    [InlineData(0, true)]    // wide two-column: rests at the scope's persisted width
    [InlineData(1, true)]    // mid: rests at 224 until dragged — but the grip is there
    [InlineData(2, true)]    // narrow: rests at 188 until dragged — but the grip is there
    [InlineData(Breakpoints.VerticalMode, false)]   // vertical: one column, no rail at all
    [InlineData(4, false)]   // off the ladder: no rail composed, so nothing to resize
    [InlineData(-1, false)]
    public void EveryTwoColumnModeResizes_TheSingleColumnOneDoesNot(int mode, bool expected)
    {
        Assert.Equal(expected, RailPolicy.ComposesRail(mode));
        Assert.Equal(expected, RailPolicy.ResizableFor(railResizable: true, mode));
    }

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

    // ── the page-aware maximum, and resizing in the NARROW arms ──────────────────────────────────────────────────────
    //
    // The narrow arms resize now, so the rail needs a ceiling that knows how wide the PAGE is: a 480-DIP rail on a
    // 560-DIP page would leave the content column 64. Every fact below is one pure function away from the frame.

    /// <summary>Every (width, mode) pair the frame can actually RENDER — `ModeFor(w, m) == m` is exactly "mode m is a
    /// fixed point at width w", so this walks the ladder including its hysteresis instead of restating the numbers.</summary>
    static IEnumerable<(float W, int Mode)> ReachableTwoColumnStates()
    {
        for (float w = 300f; w <= 2400f; w += 1f)
            for (int mode = 0; mode < Breakpoints.VerticalMode; mode++)
                if (Breakpoints.ModeFor(w, mode, initialized: true) == mode)
                    yield return (w, mode);
    }

    /// <summary>THE guard: whatever the page width and the mode, a rail at its maximum still leaves the content column
    /// its 300-DIP floor — and the maximum itself never leaves the authored 180…480 band.</summary>
    [Fact]
    public void MaxWidthForPage_AlwaysLeavesTheContentColumnItsFloor()
    {
        int seen = 0;
        foreach (var (w, mode) in ReachableTwoColumnStates())
        {
            float max = RailPolicy.MaxWidthForPage(w, mode);
            Assert.InRange(max, RailPolicy.MinWidth, RailPolicy.MaxWidth);
            float content = RailPolicy.ContentWidthFor(w, max, RailPolicy.GripStripW);
            Assert.True(content >= Breakpoints.ContentMinWidthForMode(mode),
                $"mode {mode} at {w}: a {max}-DIP rail leaves the content column {content}");
            seen++;
        }
        Assert.True(seen > 1000, $"the ladder walk covered only {seen} states");
    }

    /// <summary>The wide arm needs 820 DIP and 480 + 16 + 300 = 796, so its answer is ALWAYS the absolute ceiling: this
    /// rule changes nothing at mode 0. An unmeasured page and the single-column arm answer the ceiling too.</summary>
    [Theory]
    [InlineData(820f)]
    [InlineData(1000f)]
    [InlineData(1600f)]
    [InlineData(2400f)]   // past PageMaxW — the row is capped, so the answer cannot keep growing
    public void MaxWidthForPage_TheWideArm_IsAlwaysTheAbsoluteCeiling(float pageWidth)
        => Assert.Equal(RailPolicy.MaxWidth, RailPolicy.MaxWidthForPage(pageWidth, RailPolicy.WideMode));

    [Theory]
    [InlineData(0f, 0)]                            // unmeasured: the pre-measure seed
    [InlineData(-1f, 1)]
    [InlineData(1200f, Breakpoints.VerticalMode)]  // one column: no rail, so no page-aware cap
    public void MaxWidthForPage_UnmeasuredOrSingleColumn_IsTheAbsoluteCeiling(float pageWidth, int mode)
        => Assert.Equal(RailPolicy.MaxWidth, RailPolicy.MaxWidthForPage(pageWidth, mode));

    /// <summary>The narrow arms' cap is the page less the grip strip and the content floor, floored to an 8-DIP step so
    /// a per-pixel window resize cannot churn the frame's render — and floored DOWN, so it never over-promises.</summary>
    [Theory]
    [InlineData(660f, 1, 344f)]   // 660 − 16 − 300
    [InlineData(700f, 1, 384f)]
    [InlineData(796f, 1, 480f)]   // the band where the cap finally reaches the absolute ceiling
    [InlineData(810f, 1, 480f)]
    [InlineData(560f, 2, 240f)]   // 244 of room, floored to the 8-DIP step below
    [InlineData(540f, 2, 224f)]   // the narrowest two-column page there is
    [InlineData(651f, 2, 328f)]   // 335 of room, floored to the 8-DIP step below
    public void MaxWidthForPage_TheNarrowArms_AreThePageLessTheSeamAndTheFloor(float pageWidth, int mode, float expected)
    {
        Assert.Equal(expected, RailPolicy.MaxWidthForPage(pageWidth, mode));
        Assert.Equal(0f, expected % RailPolicy.MaxQuantum);
    }

    /// <summary>Monotone in the page width and quantized: a window that only gets wider never narrows the rail's cap.</summary>
    [Fact]
    public void MaxWidthForPage_IsMonotoneAndQuantized()
    {
        for (int mode = 0; mode < Breakpoints.VerticalMode; mode++)
        {
            float prev = 0f;
            for (float w = 540f; w <= 2400f; w += 1f)
            {
                float max = RailPolicy.MaxWidthForPage(w, mode);
                Assert.Equal(0f, max % RailPolicy.MaxQuantum);
                Assert.True(max >= prev, $"mode {mode} at {w}: the cap fell from {prev} to {max}");
                prev = max;
            }
        }
    }

    /// <summary>Never dragged in this scope ⇒ the narrow arms open at the breakpoint rail (224 / 188) exactly as they
    /// did when they were fixed; the wide arm still opens at the scope's own default.</summary>
    [Theory]
    [InlineData(RailScope.Album)]
    [InlineData(RailScope.Playlist)]
    [InlineData(RailScope.Liked)]
    [InlineData(RailScope.Show)]
    [InlineData(RailScope.Uniform)]
    public void RestingWidth_NeverDragged_KeepsTheBreakpointRailInTheNarrowArms(RailScope scope)
    {
        float stored = RailPolicy.DefaultWidthFor(scope);
        Assert.False(RailPolicy.HasDraggedWidth(stored, scope));

        Assert.Equal(stored, RailPolicy.RestingWidth(stored, scope, 0, RailPolicy.MaxWidthForPage(1200f, 0)));
        Assert.Equal(RailPolicy.MidRestWidth, RailPolicy.RestingWidth(stored, scope, 1, RailPolicy.MaxWidthForPage(700f, 1)));
        Assert.Equal(RailPolicy.NarrowRestWidth, RailPolicy.RestingWidth(stored, scope, 2, RailPolicy.MaxWidthForPage(600f, 2)));
    }

    /// <summary>Once dragged, the persisted width applies in the narrow arms too — clamped to what the page can give.</summary>
    [Fact]
    public void RestingWidth_OnceDragged_ThePersistedWidthAppliesInEveryArm()
    {
        const RailScope scope = RailScope.Show;   // the podcast family — the show and episode pages share one pair
        const float dragged = 320f;
        Assert.True(RailPolicy.HasDraggedWidth(dragged, scope));

        Assert.Equal(dragged, RailPolicy.RestingWidth(dragged, scope, 0, RailPolicy.MaxWidthForPage(1200f, 0)));
        Assert.Equal(dragged, RailPolicy.RestingWidth(dragged, scope, 1, RailPolicy.MaxWidthForPage(800f, 1)));
        // 600 DIP of page at mode 2 caps the rail at 280, so the 320 is HELD BACK here…
        Assert.Equal(280f, RailPolicy.MaxWidthForPage(600f, 2));
        Assert.Equal(280f, RailPolicy.RestingWidth(dragged, scope, 2, RailPolicy.MaxWidthForPage(600f, 2)));
    }

    /// <summary>The whole point of clamping on READ: a persisted width wider than the page allows is held back, never
    /// rewritten — so the same stored number comes back in full once the window is wide again.</summary>
    [Fact]
    public void RestingWidth_AWidthTooBigForThePage_IsHeldBack_AndTheMemorySurvives()
    {
        const RailScope scope = RailScope.Album;
        const float remembered = 460f;   // the user's own drag, from a wide window

        float narrow = RailPolicy.RestingWidth(remembered, scope, 2, RailPolicy.MaxWidthForPage(560f, 2));
        Assert.Equal(240f, narrow);
        Assert.True(narrow < remembered);

        float mid = RailPolicy.RestingWidth(remembered, scope, 1, RailPolicy.MaxWidthForPage(700f, 1));
        Assert.Equal(384f, mid);

        // …and the remembered number is still the remembered number: widening restores it in full.
        Assert.Equal(remembered, RailPolicy.RestingWidth(remembered, scope, 0, RailPolicy.MaxWidthForPage(1200f, 0)));
        Assert.Equal(remembered, RailPolicy.RestingWidth(remembered, scope, 1, RailPolicy.MaxWidthForPage(810f, 1)));
    }

    /// <summary>The minimum is exactly what it was: 180, in every arm and for every stored value.</summary>
    [Fact]
    public void RestingWidth_NeverDropsBelowTheMinimum_AndNeverPassesThePageCap()
    {
        float[] stored = [0f, 10f, 120f, 180f, 224f, 240f, 280f, 400f, 480f, 9999f];
        foreach (var (w, mode) in ReachableTwoColumnStates())
        {
            float max = RailPolicy.MaxWidthForPage(w, mode);
            for (var scope = RailScope.Album; scope <= RailScope.Uniform; scope++)
                foreach (float s in stored)
                {
                    float rest = RailPolicy.RestingWidth(s, scope, mode, max);
                    Assert.InRange(rest, RailPolicy.MinWidthFor(scope), max);
                    Assert.True(RailPolicy.ContentWidthFor(w, rest, RailPolicy.GripStripW) >= Breakpoints.ContentMinWidthForMode(mode),
                        $"{scope} mode {mode} at {w}: stored {s} rested at {rest}");
                }
        }
    }

    /// <summary>A drag that merely parked against the PAGE's cap keeps the wider remembered width; any other release is
    /// the user's own number and replaces it.</summary>
    [Theory]
    [InlineData(240f, 460f, 240f, 460f)]   // parked on the cap, a wider memory → the memory survives
    [InlineData(240f, 200f, 240f, 240f)]   // …unless the memory was not actually wider
    [InlineData(240f, 460f, 200f, 200f)]   // dragged well inside the cap → that is the new number
    [InlineData(480f, 480f, 480f, 480f)]   // the wide arm: the cap IS the absolute ceiling, nothing to preserve
    public void CommitWidth_ADragParkedOnThePageCap_KeepsTheRememberedWidth(
        float max, float stored, float dragged, float expected)
        => Assert.Equal(expected, RailPolicy.CommitWidth(dragged, stored, max));

    /// <summary>The collapse affordance survives the narrow arms: the 96-DIP identity strip plus its 20-DIP re-open grip
    /// leave the content column far more than its floor, at every two-column width — never negative, never sub-minimum.</summary>
    [Fact]
    public void TheCollapsedArm_AlwaysLeavesTheContentColumnItsFloor()
    {
        foreach (var (w, mode) in ReachableTwoColumnStates())
        {
            float content = RailPolicy.CollapsedContentWidthFor(w);
            Assert.True(content >= Breakpoints.ContentMinWidthForMode(mode),
                $"mode {mode} at {w}: collapsing leaves the content column {content}");
        }
        // The narrowest two-column page there is: 540 − 96 − 20 = 424.
        Assert.Equal(424f, RailPolicy.CollapsedContentWidthFor(540f));
    }

    /// <summary>The two breakpoint rails and the seam are ONE number each, shared by the policy and the frame.</summary>
    [Fact]
    public void TheBreakpointRails_AndTheSeam_AreOneNumberEach()
    {
        Assert.Equal(224f, RailPolicy.MidRestWidth);
        Assert.Equal(188f, RailPolicy.NarrowRestWidth);
        Assert.Equal(RailPolicy.MidRestWidth, RailPolicy.NarrowRestWidthFor(1));
        Assert.Equal(RailPolicy.NarrowRestWidth, RailPolicy.NarrowRestWidthFor(2));
        Assert.Equal(FluentGpu.Controls.Splitter.StripW, RailPolicy.GripStripW);
        Assert.Equal(300f, Breakpoints.TwoColumnContentMinW);
    }
}
