using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>The per-row change detector behind <c>SidebarPane</c>'s row epochs.
///
/// <para>A bound row slot is a FROZEN child: re-planning in the pane does not re-render it, so every slot used to
/// subscribe to one pane-wide plan version. That made every publish — a library refresh, a pin mutation, a projection
/// tick — re-render every realized row, whether or not its own content moved. This diff is what lets the pane bump
/// only the indices whose rendered content can actually have changed.</para>
///
/// <para>PURE and engine-free on purpose: it lives under <c>Features/Sidebar/Data/</c> so <c>Wavee.Tests</c> (which
/// source-includes that folder and has no engine reference) can drive the real rules.</para></summary>
public static class SidebarRowDiff
{
    /// <summary>Fill <paramref name="changed"/> with "row i must re-render". The row RECORD is not enough on its own:
    /// a row addresses its entry by INDEX, so a library refresh can leave every row record identical while the entry
    /// behind it gained a name, a cover or a child count. Both are therefore compared.
    ///
    /// <para>A row present in the new plan but not the old one always counts as changed, and so does a row whose entry
    /// index is valid in one plan and not the other.</para></summary>
    /// <param name="changed">Written for indices [0, newRows.Count); the caller sizes it. Any excess is left alone.</param>
    public static void Diff(
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
        IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries,
        Span<bool> changed)
    {
        int n = Math.Min(newRows.Count, changed.Length);
        for (int i = 0; i < n; i++)
            changed[i] = RowChanged(oldRows, oldEntries, newRows, newEntries, i);
    }

    /// <summary>Does row <paramref name="index"/> render differently between the two plans?</summary>
    public static bool RowChanged(
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
        IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries,
        int index)
    {
        if ((uint)index >= (uint)newRows.Count) return false;
        if (index >= oldRows.Count) return true;              // the row is new at this slot
        var row = newRows[index];
        if (!row.Equals(oldRows[index])) return true;
        return !SameEntry(oldEntries, newEntries, row.EntryIndex);
    }

    /// <summary>Compare the entry both plans' row addresses. An out-of-range index on BOTH sides is equal — the row
    /// carries no entry (a header, a divider, a skeleton) and nothing behind it can go stale.</summary>
    static bool SameEntry(IReadOnlyList<SidebarLibraryEntry> oldEntries, IReadOnlyList<SidebarLibraryEntry> newEntries,
                          int entryIndex)
    {
        if (entryIndex < 0) return true;
        bool inOld = entryIndex < oldEntries.Count;
        bool inNew = entryIndex < newEntries.Count;
        if (inOld != inNew) return false;
        return !inNew || oldEntries[entryIndex].Equals(newEntries[entryIndex]);
    }
}
