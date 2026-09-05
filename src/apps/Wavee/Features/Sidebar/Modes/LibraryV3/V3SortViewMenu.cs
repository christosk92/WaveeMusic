using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>
/// §3.2.6's sort + view rows, now reached as two submenus of the header's "…" overflow instead of a standalone
/// always-visible pill (<c>V3SortViewTrigger</c>/<c>V3SortViewPanel</c>, deleted). The decision logic — which sort
/// codes exist, when Custom order is offered, the four view densities — is carried over verbatim; only the
/// PRESENTATION changed, from a custom-drawn pill/panel to plain <see cref="MenuFlyoutItem"/> rows.
///
/// <para>Direction lost its one-tap "tap the active sort again to flip" gesture — a flat menu has no room for a
/// second meaning on the same row — and became a separate "Reversed" toggle row instead, shown only when the active
/// sort supports a direction at all (Custom order pins it off, same as before).</para>
/// </summary>
static class V3SortViewMenu
{
    public static List<MenuFlyoutItem> SortRows(SidebarPreferences prefs)
    {
        int sort = LibraryV3Metrics.NormalizeSort(prefs.V3Sort.Value);
        bool desc = prefs.V3Desc.Value;
        int filter = LibraryV3Metrics.NormalizeFilter(prefs.V3Filter.Value);
        // §3.2.6's availability edge case: Custom order exists only under Playlists.
        bool customAvailable = filter == (int)SidebarV3Filter.Playlists;

        var rows = new List<MenuFlyoutItem>(7)
        {
            SortRow(prefs, (int)SidebarV3Sort.Recents, sort),
            SortRow(prefs, (int)SidebarV3Sort.RecentlyAdded, sort),
            SortRow(prefs, (int)SidebarV3Sort.Alphabetical, sort),
            SortRow(prefs, (int)SidebarV3Sort.Creator, sort),
        };
        if (customAvailable) rows.Add(SortRow(prefs, (int)SidebarV3Sort.Custom, sort));

        bool directional = SidebarSort.SupportsDirection((SidebarV3Sort)sort);
        rows.Add(MenuFlyoutItem.Separator);
        rows.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Sidebar.V3.Sort.Reversed), desc,
            () => prefs.SetV3Sort(prefs.V3Sort.Peek(), !prefs.V3Desc.Peek()), enabled: directional));
        return rows;
    }

    /// <summary>Picking a DIFFERENT sort always resets direction to the sort's natural order — reversing carries no
    /// meaning across two different orderings.</summary>
    static MenuFlyoutItem SortRow(SidebarPreferences prefs, int key, int active) =>
        MenuFlyoutItem.RadioItem(LibraryV3Labels.Sort(key), active == key, () => prefs.SetV3Sort(key, false));

    /// <summary>The 4 view densities — <c>LibrarySortPanel.ViewToggles</c>'s codes, now icon+label menu rows instead
    /// of a bare 4-cell icon bank (the user's call: a small leading glyph beside the text, not text-only).</summary>
    public static List<MenuFlyoutItem> ViewRows(SidebarPreferences prefs)
    {
        int view = LibraryV3Metrics.NormalizeView(prefs.V3View.Value);
        return new List<MenuFlyoutItem>(4)
        {
            ViewRow(prefs, (int)SidebarV3View.CompactList, view, Icons.ViewList, Loc.Get(Strings.Library.View.CompactList)),
            ViewRow(prefs, (int)SidebarV3View.List, view, Icons.ViewList, Loc.Get(Strings.Library.View.List)),
            ViewRow(prefs, (int)SidebarV3View.CompactGrid, view, Icons.ViewGrid, Loc.Get(Strings.Library.View.CompactGrid)),
            ViewRow(prefs, (int)SidebarV3View.Grid, view, Icons.ViewGrid, Loc.Get(Strings.Library.View.Grid)),
        };
    }

    static MenuFlyoutItem ViewRow(SidebarPreferences prefs, int key, int active, string glyph, string label) =>
        MenuFlyoutItem.RadioItem(label, active == key, () => prefs.SetV3View(key), glyph);
}
