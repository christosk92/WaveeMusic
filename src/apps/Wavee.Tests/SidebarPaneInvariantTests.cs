using Xunit;

namespace Wavee.Tests;

public class SidebarPaneInvariantTests
{
    static SidebarPaneFrameSnapshot Expanded(float preferred = 320f, float rendered = 320f) => new(
        SidebarDesign.Curated,
        UserCollapsed: false,
        PresentedCompact: false,
        PreferredExpandedWidth: preferred,
        RenderedPaneWidth: rendered,
        ExpandedOpacity: 1f,
        RailOpacity: 0f,
        ExpandedHitTestVisible: true,
        RailHitTestVisible: false);

    static SidebarPaneFrameSnapshot Compact(float rendered = ShellResponsiveLayout.CompactRailW) => new(
        SidebarDesign.LibraryV3,
        UserCollapsed: true,
        PresentedCompact: true,
        PreferredExpandedWidth: 340f,
        RenderedPaneWidth: rendered,
        ExpandedOpacity: 0f,
        RailOpacity: 1f,
        ExpandedHitTestVisible: false,
        RailHitTestVisible: true);

    [Fact]
    public void ExpandedTerminalState_IsValid()
    {
        var state = Expanded();
        Assert.Equal(SidebarPaneInvariantFault.None, SidebarPaneInvariant.Inspect(in state));
    }

    [Fact]
    public void CompactTerminalState_IsValid()
    {
        var state = Compact();
        Assert.True(SidebarPaneInvariant.IsValid(in state));
    }

    [Fact]
    public void ReportedTwentyFourDipSliver_IsRejected()
    {
        var state = Expanded(rendered: 24f);
        var fault = SidebarPaneInvariant.Inspect(in state);
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.ExpandedWidthOutOfRange));
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Theory]
    [InlineData(55.49f, false)]
    [InlineData(55.5f, true)]
    [InlineData(56.5f, true)]
    [InlineData(56.51f, false)]
    public void CompactWidth_UsesHalfDipTolerance(float rendered, bool valid)
    {
        var state = Compact(rendered);
        Assert.Equal(valid, SidebarPaneInvariant.IsValid(in state));
    }

    [Fact]
    public void WrongLayerOwnership_IsRejected()
    {
        var state = Expanded() with
        {
            ExpandedOpacity = 0f,
            RailOpacity = 1f,
            ExpandedHitTestVisible = false,
            RailHitTestVisible = true,
        };
        var fault = SidebarPaneInvariant.Inspect(in state);
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.LayerOpacityMismatch));
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.HitTestOwnerMismatch));
    }

    [Fact]
    public void ExpandedWidthMustMatchTheRememberedPreference()
    {
        var state = Expanded(preferred: 360f, rendered: 320f);
        Assert.True(SidebarPaneInvariant.Inspect(in state)
            .HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Fact]
    public void NonFiniteGeometry_IsRejectedWithoutFurtherClassification()
    {
        var state = Expanded() with { RenderedPaneWidth = float.NaN };
        Assert.Equal(SidebarPaneInvariantFault.NonFiniteValue, SidebarPaneInvariant.Inspect(in state));
    }

    // ── THE ONE CONTENT LANE ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The lane is DERIVED, never typed twice: the pane edge plus a depth-0 row's own indent. If someone
    /// re-tunes <c>IndentFor</c> or the pane pad, the lane moves with them instead of silently disagreeing.</summary>
    [Fact]
    public void ContentLane_IsThePaneEdgePlusTheDepthZeroRowIndent()
    {
        Assert.Equal(SidebarRowGeometry.PaneEdge + SidebarRowGeometry.IndentFor(0), SidebarRowGeometry.ContentLane);
        Assert.Equal(SidebarRowGeometry.PaneEdge + SidebarRowGeometry.RowInsetRight, SidebarRowGeometry.ContentLaneEnd);
        // The landed numbers the screenshots were measured against, pinned so a "harmless" retune is a visible diff.
        Assert.Equal(12f, SidebarRowGeometry.ContentLane);
        Assert.Equal(16f, SidebarRowGeometry.ContentLaneEnd);
    }

    /// <summary>A NESTED row indents from the lane, so a depth-1 child sits exactly one 12-DIP level inside it.</summary>
    [Fact]
    public void NestedRowsIndentFromTheLane()
    {
        Assert.Equal(SidebarRowGeometry.ContentLane + 12f,
                     SidebarRowGeometry.PaneEdge + SidebarRowGeometry.IndentFor(1));
        Assert.Equal(SidebarRowGeometry.IndentFor(4), SidebarRowGeometry.IndentFor(9));   // clamped at 4 levels
    }
}
