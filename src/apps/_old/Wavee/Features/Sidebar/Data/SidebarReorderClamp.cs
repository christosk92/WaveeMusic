namespace Wavee;

/// <summary>
/// The displacement hint for a reorder gesture whose destination was CLAMPED (see
/// <c>SidebarPaneConfig.ClampReorderSlot</c>).
///
/// <para>Normally the engine's own <c>ReorderList.OffsetFor</c> answers this — and it still does everywhere no clamp is
/// configured. But a clamp says "the gap opens HERE, not where the pointer is", and the engine's hint is computed from
/// the target it holds internally, which the app cannot set without reaching into the control's gesture state. So the
/// one clamped case reproduces the hint from the snapped destination instead: the sidebar's bands lift ONE row
/// (<c>Reorderable</c> begins a single-item gesture), at one uniform band extent, with no inter-row spacing — which is
/// exactly the shape <c>ReorderList.OffsetFor</c> reduces to there.</para>
///
/// <para>Engine-free, so <c>LibraryV3ClampTests</c> can pin the gap against the same fixture the slot clamp uses.</para>
/// </summary>
static class SidebarReorderClamp
{
    /// <summary>Where sibling <paramref name="slot"/> sits while the row at <paramref name="from"/> is heading for
    /// <paramref name="to"/>: the rows between the two close the gap the lift left (or part to make room), everything
    /// else — and the lifted row itself — stays put.</summary>
    public static float Offset(int slot, int from, int to, float extent)
    {
        if (slot < 0 || from < 0 || to < 0 || from == to || slot == from) return 0f;
        if (to > from && slot > from && slot <= to) return -extent;
        if (to < from && slot >= to && slot < from) return extent;
        return 0f;
    }
}
