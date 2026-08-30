using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>
/// The PURE join rules between a planned <see cref="SidebarRow"/> and what the renderer draws from it: which
/// hand-placed item a row was projected from, and whether a row draws itself SELECTED for a given nav route.
///
/// <para><b>Why this exists as its own owner.</b> Selection used to be a raw <c>SelectedRoute</c> signal read inside
/// every realized slot, every pill probe and the rail — so a navigation re-rendered the whole realized window. The pane
/// now sweeps the plan ONCE per route change (<c>SidebarPane.RefreshSelection</c>) and bumps only the rows that flipped,
/// exactly like <c>RefreshPlayState</c> does for now-playing. That sweep and the row's own <c>Selected</c> flag MUST
/// agree, or a row would keep a stale pill; the only way to guarantee that is one implementation both call — this one.</para>
///
/// <para>Engine-free by construction (System + <c>Wavee.Core.Sidebar</c> + the Data\ entry record), like the rest of
/// <c>Features/Sidebar/Data/</c>, so <c>Wavee.Tests</c> drives the REAL rules instead of a copy of them.</para>
/// </summary>
public static class SidebarRowResolve
{
    /// <summary>The hand-placed item a plan row was projected from, by the planner's join rule (a hand-placed row carries
    /// <c>Key == item.Key</c>, unique within its section). Also finds a Pinned OVERRIDE row's side-table entry, which is
    /// what makes an alias/icon override apply to a pinned row.</summary>
    public static SidebarItemSpec? ItemOf(SidebarSectionSpec section, string key)
    {
        var items = section.ItemList;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Hidden) continue;
            if (string.Equals(item.Key, key, StringComparison.Ordinal)) return item;
            if (string.Equals(item.Id, key, StringComparison.Ordinal)) return item;
        }
        return null;
    }

    /// <summary>The ONE selection rule for a PROJECTED entity (an entity row, a grid cell, a card): it draws selected
    /// when its nav route IS the live route. A folder and a track have no route (<c>RouteKey</c> is null), so neither
    /// can ever be the selected row.</summary>
    public static bool EntrySelects(in SidebarLibraryEntry entry, string route)
        => route.Length > 0
           && entry.RouteKey is { Length: > 0 } r
           && string.Equals(r, route, StringComparison.Ordinal);

    /// <summary>Does the plan row draw itself SELECTED for <paramref name="route"/>? Resolved EXACTLY as
    /// <c>SidebarPaneSlot</c> draws it, kind by kind:
    /// <list type="bullet">
    /// <item><c>IconRow</c>/<c>EntityRow</c>/<c>Placeholder</c> — an ACTION item never selects; then the projected entry;
    /// then a hand-placed TRACK never selects and a hand-placed ROUTE selects on its own key; a missing-entity retention
    /// row never selects.</item>
    /// <item><c>EntityCard</c> — the resolved entry's route, or the pin route derived from its uri when unresolved.</item>
    /// <item><c>GridStrip</c> — one route per CELL, so the ROW is "selected" when ANY cell in its range is (that is the
    /// unit the pane's per-row epoch can address).</item>
    /// <item>everything else (headers, dividers, folders, empties, skeletons, create rows, prompts) — never.</item>
    /// </list></summary>
    public static bool SelectsRoute(in SidebarRow row, IReadOnlyList<SidebarLibraryEntry> entries,
                                    SidebarSectionSpec? section, string route)
    {
        if (route.Length == 0 || entries is null) return false;
        bool resolved = row.EntryIndex >= 0 && row.EntryIndex < entries.Count;
        switch (row.Kind)
        {
            case SidebarRowKind.IconRow:
            case SidebarRowKind.EntityRow:
            case SidebarRowKind.Placeholder:
            {
                var item = section is null ? null : ItemOf(section, row.Key);
                if (item is { Target: SidebarItemTarget.Action }) return false;
                if (resolved) return EntrySelects(entries[row.EntryIndex], route);
                if (item is { Target: SidebarItemTarget.Track }) return false;
                return item is { Target: SidebarItemTarget.Route }
                       && string.Equals(item.Key, route, StringComparison.Ordinal);
            }

            case SidebarRowKind.EntityCard:
            {
                string uri = resolved ? entries[row.EntryIndex].Uri : "";
                string? key = resolved ? entries[row.EntryIndex].RouteKey : SidebarPinId.FromUri(uri);
                return key is { Length: > 0 } && string.Equals(key, route, StringComparison.Ordinal);
            }

            case SidebarRowKind.GridStrip:
            {
                int start = row.EntryIndex;
                int count = row.ItemCount;
                if (start < 0 || count <= 0 || start >= entries.Count) return false;
                if (start + count > entries.Count) count = entries.Count - start;
                for (int i = 0; i < count; i++)
                    if (EntrySelects(entries[start + i], route)) return true;
                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>Every plan-row index that draws selected for <paramref name="route"/>, in ASCENDING order (the pane
    /// diffs two of these with a linear merge, so the order is part of the contract). <paramref name="into"/> is
    /// caller-owned and is NOT cleared — a warm sweep therefore allocates nothing.</summary>
    public static void Sweep(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries,
                             Func<string, SidebarSectionSpec?> sectionOf, string route, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (rows is null || entries is null || route.Length == 0) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var section = sectionOf?.Invoke(row.SectionId);
            if (SelectsRoute(in row, entries, section, route)) into.Add(i);
        }
    }

    /// <summary>The SYMMETRIC DIFFERENCE of two ascending index lists — the rows that GAINED or LOST the selection, and
    /// therefore exactly the per-row epochs a route edge must bump. A row that is selected on both sides is deliberately
    /// absent: nothing about its skin changed, and the publish diff already owns any content change it had.
    ///
    /// <para>Both inputs come from <see cref="Sweep"/>, so ascending order is part of the contract and the merge is
    /// linear. <paramref name="into"/> is caller-owned and is NOT cleared.</para></summary>
    public static void Flipped(IReadOnlyList<int> previous, IReadOnlyList<int> next, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        int a = 0, b = 0;
        int na = previous?.Count ?? 0, nb = next?.Count ?? 0;
        while (a < na || b < nb)
        {
            if (b >= nb) { into.Add(previous![a++]); continue; }
            if (a >= na) { into.Add(next![b++]); continue; }
            int x = previous![a], y = next![b];
            if (x == y) { a++; b++; }
            else if (x < y) { into.Add(x); a++; }
            else { into.Add(y); b++; }
        }
    }
}
