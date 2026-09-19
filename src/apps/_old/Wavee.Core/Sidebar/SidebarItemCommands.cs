namespace Wavee.Core.Sidebar;

// PHASE 1 — SENTINEL-AWARE ITEM ADDRESSING, IN ONE PLACE.
//
// Two item lists live on `SidebarCustomLayout`: a SECTION's `Items`, and the flat global band `TopBar`. They are edited
// by two disjoint command families on purpose (`SidebarLayoutCommands.cs` documents why: the band has its own cap and
// none of the per-KIND rules the item commands carry — AcceptsItems, EntityEmbed's retarget, the lazy Pinned-override
// prune). Since Decision A the band ALSO renders as an ordinary section — the sentinel `SidebarIds.TopBarSection` — so
// every UI that edits "the items of the section I am looking at" now has to pick the right family.
//
// That choice used to be spelled out at each call site (`Curated/SidebarPropertyPanel.cs` had two copies). One more
// copy would have landed in the pane's reorder commit and one in the customizer, and the first divergence would be a
// silent `UnknownSection` rejection — a drag that snaps back with no message. It is therefore ONE decision, HERE, in
// Wavee.Core beside `SidebarIds` and the commands themselves, which is also what makes it reachable from Wavee.Tests
// ("a move inside the Shortcuts section emits MoveTopBarItem, not MoveItem").
//
// The three factories return `SidebarCommand`, not a result: dispatching stays the caller's (the pane dispatches through
// `SidebarPreferences`, the customizer through its own reject-surfacing wrapper).
public static class SidebarItemCommands
{
    /// <summary>Insert an item into a section's list — or into the shell's shortcut band when the section id is the
    /// sentinel. <paramref name="index"/> is clamped by the reducer in both families.</summary>
    public static SidebarCommand Add(string sectionId, SidebarItemSpec item, int index)
        => SidebarIds.IsTopBar(sectionId)
            ? new AddTopBarItem(item, index)
            : new AddItem(sectionId, item, index);

    /// <summary>Reorder WITHIN one list. <paramref name="toIndex"/> is interpreted AFTER the removal in both families
    /// (the standard <c>Reorderable.OnReorder</c> contract), so the two arms are genuinely interchangeable.
    ///
    /// <para>There is deliberately no cross-section form here: <c>MoveItem</c> can move between two sections, but the
    /// band is not a section and no command moves an item across that boundary. A caller that wants "drag out of the
    /// band into a section" must compose Remove + Add and own the two-step undo.</para></summary>
    public static SidebarCommand Move(string sectionId, int fromIndex, int toIndex)
        => SidebarIds.IsTopBar(sectionId)
            ? new MoveTopBarItem(fromIndex, toIndex)
            : new MoveItem(sectionId, fromIndex, sectionId, toIndex);

    /// <summary>Drop an item by id. Removing the band's last shortcut leaves an EMPTY list, never null — the reducer
    /// owns that distinction.</summary>
    public static SidebarCommand Remove(string sectionId, string itemId)
        => SidebarIds.IsTopBar(sectionId)
            ? new RemoveTopBarItem(itemId)
            : new RemoveItem(sectionId, itemId);

    /// <summary>The item list a section id addresses: the band for the sentinel, the section's own items otherwise, and
    /// an EMPTY list for an id the document does not contain. The read-side twin of the three factories, so a panel
    /// that edits through <see cref="Remove"/> reads through the same rule and the two cannot disagree about which list
    /// the id means.</summary>
    public static IReadOnlyList<SidebarItemSpec> ItemsIn(SidebarCustomLayout? layout, string? sectionId)
    {
        if (layout is null || sectionId is null or { Length: 0 }) return Array.Empty<SidebarItemSpec>();
        return SidebarIds.IsTopBar(sectionId)
            ? layout.EffectiveTopBar
            : layout.Find(sectionId)?.ItemList ?? Array.Empty<SidebarItemSpec>();
    }

    /// <summary>Locate an item by id inside whichever list <paramref name="sectionId"/> addresses, or -1.</summary>
    public static SidebarItemSpec? FindItem(SidebarCustomLayout? layout, string? sectionId, string? itemId)
    {
        if (itemId is null or { Length: 0 }) return null;
        var items = ItemsIn(layout, sectionId);
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].Id, itemId, StringComparison.Ordinal)) return items[i];
        return null;
    }
}
