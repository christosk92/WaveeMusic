namespace Wavee;

/// <summary>The pinned band's drop geometry, as pure functions over the SAME resolver the rootlist uses. A pinned row
/// has no depth ladder and no folder semantics for a PIN payload — the only questions are "which edge" and, for an
/// editable-playlist row, "is the centre a track deposit". Engine-free so <c>PinBandSlotsTests</c> drives the real
/// rule (Wavee.Tests source-includes Features/Sidebar/Data).</summary>
public static class PinBandSlots
{
    /// <summary>The row facts a pinned row hands the resolver. Never a folder (a pinned folder is a pin, not a
    /// filing target, for a PIN payload); <paramref name="centerDeposits"/> is "this row is an editable playlist AND
    /// the payload offers tracks".</summary>
    public static SidebarRowFacts Facts(int depth, bool centerDeposits) => new(
        IsFolder: false, FolderExpanded: false, FolderHasChildren: false,
        Depth: depth, NextVisibleDepth: depth,          // no outdent ladder: After stays at the row's own depth
        CenterAccepts: centerDeposits,
        SourceIsSelf: false,                            // a pin dragged onto itself is a MOVE, never a refusal
        SortedNonCustom: false, RootlistLoaded: true);

    /// <summary>Pointer → slot for one pinned row. Delegates the zone geometry (edge bands, centre) to
    /// <see cref="RootlistSlotResolver.Resolve"/> so the two surfaces can never disagree about where an edge is.</summary>
    public static SidebarDropSlot Resolve(int planIndex, float t, float rowHeight, int depth, bool centerDeposits)
        => RootlistSlotResolver.Resolve(planIndex, t, xInRow: float.PositiveInfinity, rowHeight,
                                        Facts(depth, centerDeposits), SidebarDropSlot.None);

    /// <summary>The whole-gutter slot of the band's closing row.</summary>
    public static SidebarDropSlot EndSlot(int planIndex) => new(planIndex, SidebarDropKind.EndOfList, 0, SidebarDropRefusal.None);

    /// <summary>The pin-store index a published slot means, for a row at <paramref name="bandSlot"/> in a band of
    /// <paramref name="bandCount"/>. <c>Into</c> on a pinned row is a deposit, not a pin — callers must not ask.</summary>
    public static int InsertIndex(in SidebarDropSlot slot, int bandSlot, int bandCount) => slot.Kind switch
    {
        SidebarDropKind.Before => bandSlot,
        SidebarDropKind.After => bandSlot + 1,
        SidebarDropKind.EndOfList => bandCount,
        _ => -1,
    };
}
