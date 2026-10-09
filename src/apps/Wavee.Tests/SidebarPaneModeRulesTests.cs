// ── Wavee.Tests/SidebarPaneModeRulesTests.cs — the sidebar's window bands, pane modes and overlay rules ─────────────────
//
// docs/plans/wavee/sidebar-rework-implementation.md §P2.1-§P2.10 (design V.1). The window decides a hysteretic band; the
// band and the user's collapse decide the mode; the mode decides the presented width; the right rail reads that width.
// Every decision lives in the engine-free `SidebarPaneModeRules`, so each branch is driven with plain numbers.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarPaneModeRulesTests
{
    [Theory]
    [InlineData(1200f, SidebarWindowBand.Wide)]
    [InlineData(659f, SidebarWindowBand.Narrow)]
    [InlineData(527f, SidebarWindowBand.Tiny)]
    public void Band_FromWide(float w, SidebarWindowBand expected)
        => Assert.Equal(expected, SidebarPaneModeRules.BandOf(w, SidebarWindowBand.Wide));

    [Fact] public void Band_NarrowLeavesOnlyAt700()
    {
        Assert.Equal(SidebarWindowBand.Narrow, SidebarPaneModeRules.BandOf(690f, SidebarWindowBand.Narrow));
        Assert.Equal(SidebarWindowBand.Wide, SidebarPaneModeRules.BandOf(700f, SidebarWindowBand.Narrow));
    }

    [Fact] public void Band_TinyLeavesOnlyAt568()
    {
        Assert.Equal(SidebarWindowBand.Tiny, SidebarPaneModeRules.BandOf(560f, SidebarWindowBand.Tiny));
        Assert.Equal(SidebarWindowBand.Narrow, SidebarPaneModeRules.BandOf(568f, SidebarWindowBand.Tiny));
        Assert.Equal(SidebarWindowBand.Wide, SidebarPaneModeRules.BandOf(720f, SidebarWindowBand.Tiny));
    }

    [Fact] public void Band_ZeroWidthNeverMoves()
        => Assert.Equal(SidebarWindowBand.Narrow, SidebarPaneModeRules.BandOf(0f, SidebarWindowBand.Narrow));

    [Fact] public void Mode_ForcedBandsIgnoreTheUser()
    {
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Narrow, false, false));
        Assert.Equal(SidebarPaneMode.Minimal, SidebarPaneModeRules.Resolve(SidebarWindowBand.Tiny, false, true));
    }

    [Fact] public void Mode_WideFollowsTheUser_EditingPresentsExpanded()
    {
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Wide, true, false));
        Assert.Equal(SidebarPaneMode.Expanded, SidebarPaneModeRules.Resolve(SidebarWindowBand.Wide, true, true));
    }

    [Fact] public void UserCollapsed_WrittenOnlyWideAndNotEditing()
    {
        Assert.True(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Wide, false));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Wide, true));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Narrow, false));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Tiny, false));
    }

    [Fact] public void Overlay_PinnedOnlyWhenEditingOutsideWide()
    {
        Assert.True(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Narrow, true));
        Assert.False(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Wide, true));
        Assert.False(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Tiny, false));
        Assert.False(SidebarPaneModeRules.LeafInvokeClosesOverlay(editing: true));
    }

    [Fact] public void PresentedWidth_ClampsToTheContentFloor()
    {
        Assert.Equal(320f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 320f, 1400f));
        Assert.Equal(220f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 320f, 700f));
        Assert.Equal(180f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 320f, 640f));
        Assert.Equal(48f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Compact, 320f, 600f));
        Assert.Equal(0f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Minimal, 320f, 500f));
    }

    [Fact] public void TheRailsSearchTile_OpensTheOverlay_InTheForcedBands()
    {
        // Sidebar.OpenPane's branch: Narrow and Tiny open the overlay (a forced rail never has UserCollapsed set, so
        // clearing it would do nothing); only Wide writes the user's collapse.
        Assert.True(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Narrow));
        Assert.True(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Tiny));
        Assert.False(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Wide));
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Narrow, userCollapsed: false, editing: false));
    }

    [Fact] public void OverlayWidth_LeavesTheRailsWorthOfPage()
        => Assert.Equal(452f, SidebarPaneModeRules.OverlayWidth(460f, 500f));

    [Fact] public void RailFit_ReadsThePresentedPane_NeverChangesTheMode()
    {
        // The right rail at 360 does not fit beside a 320 pane in a 1100 window: it floats, and the pane stays Expanded.
        Assert.False(SidebarPaneModeRules.RailFits(320f, 360f, 1100f));
        Assert.Equal(SidebarPaneMode.Expanded,
            SidebarPaneModeRules.Resolve(SidebarPaneModeRules.BandOf(1100f, SidebarWindowBand.Wide), false, false));
        Assert.True(SidebarPaneModeRules.RailFits(48f, 360f, 1100f));
    }
}
