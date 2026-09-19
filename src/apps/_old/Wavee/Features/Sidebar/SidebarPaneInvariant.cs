using System;

namespace Wavee;

/// <summary>A settled docked-pane observation. This is deliberately separate from
/// <see cref="SidebarPaneSnapshot"/>, which is the persisted preference triple; these are rendered terminal-state facts
/// captured by the shell after a transition settles.</summary>
public readonly record struct SidebarPaneFrameSnapshot(
    SidebarDesign Design,
    bool UserCollapsed,
    bool PresentedCompact,
    float PreferredExpandedWidth,
    float RenderedPaneWidth,
    float ExpandedOpacity,
    float RailOpacity,
    bool ExpandedHitTestVisible,
    bool RailHitTestVisible);

/// <summary>All terminal-state violations detected in one observation. Flags make one diagnostic edge sufficient even
/// when one bad state breaks width, opacity, and hit testing at the same time.</summary>
[Flags]
public enum SidebarPaneInvariantFault : ushort
{
    None = 0,
    NonFiniteValue = 1 << 0,
    PreferredWidthOutOfRange = 1 << 1,
    CompactWidthMismatch = 1 << 2,
    ExpandedWidthOutOfRange = 1 << 3,
    ExpandedWidthMismatch = 1 << 4,
    LayerOpacityMismatch = 1 << 5,
    HitTestOwnerMismatch = 1 << 6,
}

/// <summary>Pure terminal-state validator behind the screenshot/layout probe and the runtime edge diagnostic. It does
/// not attempt to validate an in-flight animation: callers invoke it only after the pane transition settles.</summary>
public static class SidebarPaneInvariant
{
    public const float Tolerance = 0.5f;

    public static SidebarPaneInvariantFault Inspect(in SidebarPaneFrameSnapshot state)
    {
        if (!float.IsFinite(state.PreferredExpandedWidth)
            || !float.IsFinite(state.RenderedPaneWidth)
            || !float.IsFinite(state.ExpandedOpacity)
            || !float.IsFinite(state.RailOpacity))
            return SidebarPaneInvariantFault.NonFiniteValue;

        SidebarPaneInvariantFault fault = SidebarPaneInvariantFault.None;
        if (!InExpandedRange(state.PreferredExpandedWidth))
            fault |= SidebarPaneInvariantFault.PreferredWidthOutOfRange;

        if (state.PresentedCompact)
        {
            if (!Near(state.RenderedPaneWidth, ShellResponsiveLayout.CompactRailW))
                fault |= SidebarPaneInvariantFault.CompactWidthMismatch;
            if (!Near(state.ExpandedOpacity, 0f) || !Near(state.RailOpacity, 1f))
                fault |= SidebarPaneInvariantFault.LayerOpacityMismatch;
            if (state.ExpandedHitTestVisible || !state.RailHitTestVisible)
                fault |= SidebarPaneInvariantFault.HitTestOwnerMismatch;
        }
        else
        {
            if (!InExpandedRange(state.RenderedPaneWidth))
                fault |= SidebarPaneInvariantFault.ExpandedWidthOutOfRange;
            if (!Near(state.RenderedPaneWidth, state.PreferredExpandedWidth))
                fault |= SidebarPaneInvariantFault.ExpandedWidthMismatch;
            if (!Near(state.ExpandedOpacity, 1f) || !Near(state.RailOpacity, 0f))
                fault |= SidebarPaneInvariantFault.LayerOpacityMismatch;
            if (!state.ExpandedHitTestVisible || state.RailHitTestVisible)
                fault |= SidebarPaneInvariantFault.HitTestOwnerMismatch;
        }

        return fault;
    }

    public static bool IsValid(in SidebarPaneFrameSnapshot state) => Inspect(state) == SidebarPaneInvariantFault.None;

    public static string FaultName(SidebarPaneInvariantFault fault)
    {
        if (fault == SidebarPaneInvariantFault.None) return "none";
        if (fault == SidebarPaneInvariantFault.NonFiniteValue) return "non_finite";
        return ((ushort)fault).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    static bool InExpandedRange(float width) =>
        width >= ShellResponsiveLayout.NavPaneMinW - Tolerance
        && width <= ShellResponsiveLayout.NavPaneMaxW + Tolerance;

    static bool Near(float actual, float expected) => MathF.Abs(actual - expected) <= Tolerance;
}
