namespace Wavee;

/// <summary>Width breakpoint for the library master–detail: below <see cref="CollapseBelow"/> the multi-column row
/// collapses to a single-column breadcrumb drill-in. Pure static (source-included by Wavee.Tests), with resize
/// hysteresis so a window hovering on the boundary doesn't flip-flop (mirrors <c>DetailLayoutBreakpoints</c>).</summary>
public static class LibraryLayoutBreakpoints
{
    public const float CollapseBelow = 640f;   // ~NavigationView.CompactModeThresholdWidth
    public const float Hysteresis = 24f;

    /// <summary>Collapse when the content area is narrow; un-collapse only once it is comfortably wide again
    /// (<see cref="Hysteresis"/> past the threshold), so a jiggle at the edge is stable.</summary>
    public static bool Collapsed(float w, bool wasCollapsed)
    {
        if (w <= 0f) return wasCollapsed;
        return wasCollapsed ? w < CollapseBelow + Hysteresis : w < CollapseBelow;
    }
}
