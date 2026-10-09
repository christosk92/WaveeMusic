// ── Wavee.Tests/SidebarResizeRulesTests.cs — the seam's drag, step and toggle rules ───────────────────────────────────
//
// docs/plans/wavee/sidebar-rework-implementation.md §P2.1 (design V.1 "Seam"). Drag resizes 180-460; below 116 the pane
// collapses to the 48-px rail (writing the user's collapse); from the rail, past 140 it expands; while editing it resizes
// but never flips. Double-click / Enter toggles; ←/→ step 8 (Shift 40). No detents, no flick.
//
// HYSTERESIS IS HISTORY-DEPENDENT: `Track`/`Resolve` take the previous state, so a fact about a raw width states which
// side of the collapse edge the pointer came from. The numbers are worked by hand from the plan, not copied from the code:
//   · RailEnterW = 180 − 64 = 116, ExpandedEnterW = 116 + 24 = 140;
//   · the splitter fade runs from 1 at 180 down to 0.35 at the rail edge (116).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarResizeRulesTests
{
    static SidebarResizeRules.State Expanded(float width = 300f) => new(false, width);

    static SidebarResizeRules.State Collapsed(float expandedWidth = 300f) => new(true, expandedWidth);

    static void Near(float expected, float actual, float tol = 1e-3f)
        => Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected}, actual {actual}");

    [Fact]
    public void Track_BelowTheRailEdge_Collapses()
    {
        var live = SidebarResizeRules.Track(100f, Expanded(), editing: false);
        Assert.True(live.Collapsed);
        Assert.Equal(SidebarRowGeometry.RailWidth, live.PresentedWidth);
        Assert.Equal(1f, live.Fade);
    }

    [Fact]
    public void Track_AboveTheRailEdge_ClampsToTheExpandedRange_AndFadesIn()
    {
        var mid = SidebarResizeRules.Track(200f, Expanded(), editing: false);
        Assert.False(mid.Collapsed);
        Assert.Equal(200f, mid.PresentedWidth);
        Assert.Equal(1f, mid.Fade);

        Assert.Equal(460f, SidebarResizeRules.Track(500f, Expanded(), editing: false).PresentedWidth);

        var edge = SidebarResizeRules.Track(116f, Expanded(), editing: false);
        Assert.False(edge.Collapsed);
        Assert.Equal(180f, edge.PresentedWidth);
        Near(0.35f, edge.Fade);
    }

    [Fact]
    public void Track_FromCollapsed_ExpandsPastTheExpandedEdge()
    {
        Assert.True(SidebarResizeRules.Track(139f, Collapsed(), editing: false).Collapsed);
        Assert.False(SidebarResizeRules.Track(140f, Collapsed(), editing: false).Collapsed);
    }

    [Fact]
    public void Track_WhileEditing_NeverCollapses()
    {
        var live = SidebarResizeRules.Track(100f, Expanded(), editing: true);
        Assert.False(live.Collapsed);
        Assert.Equal(180f, live.PresentedWidth);
        Assert.Equal(1f, live.Fade);
    }

    [Fact]
    public void Resolve_Collapsing_KeepsTheExpandedMemory()
    {
        var settle = SidebarResizeRules.Resolve(100f, Expanded(300f), editing: false);
        Assert.True(settle.UserCollapsed);
        Assert.Equal(300f, settle.ExpandedWidth);
        Assert.Equal(SidebarRowGeometry.RailWidth, settle.TargetWidth);
    }

    [Fact]
    public void Resolve_Expanded_SettlesOnTheDraggedWidth()
    {
        var settle = SidebarResizeRules.Resolve(250f, Expanded(300f), editing: false);
        Assert.Equal(new SidebarResizeRules.Settle(false, 250f, 250f), settle);
    }

    [Fact]
    public void Step_NudgesByEightOrFortyAndClampsToTheRange()
    {
        Assert.Equal(new SidebarResizeRules.Settle(false, 308f, 308f),
            SidebarResizeRules.Step(Expanded(300f), +1, large: false, editing: false));
        Assert.Equal(new SidebarResizeRules.Settle(false, 340f, 340f),
            SidebarResizeRules.Step(Expanded(300f), +1, large: true, editing: false));
        Assert.Equal(new SidebarResizeRules.Settle(false, 182f, 182f),
            SidebarResizeRules.Step(Expanded(190f), -1, large: false, editing: false));
        Assert.Equal(460f, SidebarResizeRules.Step(Expanded(455f), +1, large: true, editing: false).TargetWidth);
    }

    [Fact]
    public void Step_CollapsesAtTheFloor_ButNotWhileEditing()
    {
        Assert.Equal(new SidebarResizeRules.Settle(true, 180f, SidebarRowGeometry.RailWidth),
            SidebarResizeRules.Step(Expanded(180f), -1, large: false, editing: false));
        Assert.Equal(new SidebarResizeRules.Settle(false, 180f, 180f),
            SidebarResizeRules.Step(Expanded(180f), -1, large: false, editing: true));
    }

    [Fact]
    public void Step_FromTheRail_ExpandsOnlyOnAForwardNudge()
    {
        Assert.Equal(new SidebarResizeRules.Settle(false, 300f, 300f),
            SidebarResizeRules.Step(Collapsed(300f), +1, large: false, editing: false));
        Assert.True(SidebarResizeRules.Step(Collapsed(300f), -1, large: false, editing: false).UserCollapsed);
        Assert.True(SidebarResizeRules.Step(Collapsed(300f), +1, large: false, editing: true).UserCollapsed);
    }

    [Fact]
    public void Toggle_FlipsAndKeepsTheWidth()
    {
        Assert.Equal(new SidebarResizeRules.Settle(true, 300f, SidebarRowGeometry.RailWidth),
            SidebarResizeRules.Toggle(Expanded(300f)));
        Assert.Equal(new SidebarResizeRules.Settle(false, 300f, 300f),
            SidebarResizeRules.Toggle(Collapsed(300f)));
    }
}
