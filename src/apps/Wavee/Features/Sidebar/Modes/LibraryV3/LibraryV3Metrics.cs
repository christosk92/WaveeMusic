using System;
using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

// §3.2.8 — Library V3's PURE metrics + label rules. No engine state, no hooks, no signals: everything here is a function of
// (view code, pane width, entry), which is what keeps V3's chrome geometry reviewable on its own.
//
// R3.0.3 — WHAT LEFT THIS FILE. The row/grid geometry (row extent, cover size, indent, the virtualized layout, the recycle
// content type, the pane wrapper padding) is GONE: V3's content is now planned and drawn by the ONE `SidebarPane`, whose
// metrics are `SidebarPaneMetrics` + `SidebarRowMetrics` (the single height ladder: Compact 32 · Cozy 40/44 ·
// Comfortable 44/48, art 20/32/40). Re-introducing any of it here would recreate the second ladder that made the same Cozy
// row 44 in one mode and 48 in another.
//
// WHAT STAYS: the fixed CHROME band heights (§3.2.2's vertical stack), the two width thresholds V3's chrome branches on,
// the derived grid column rule, and the persisted-code coercion every reader shares.

static class LibraryV3Metrics
{
    // ── the expanded chrome stack (§3.2.2) ────────────────────────────────────────────────────────────────────────────
    public const float HeaderHeight = 44f;
    public const float ToolbarHeight = 36f;
    /// <summary>The ONE filter rail. There is no second (qualifier) band any more: the selected facet's sub-filter fuses
    /// INTO its pill (<c>LibraryV3Chips</c>'s morphing facet-chip grammar, copied from <c>HomeFacetChips</c>), so the
    /// chrome stack lost a 32-DIP row. The 40 (vs the pills' 28) is deliberate slack: the compound pill's raised inner
    /// segment carries a shadow, and the rail is a clipping scroll viewport.</summary>
    public const float ChipRailHeight = 40f;
    /// <summary>The drill-in breadcrumb band (narrow/drawer only — Revision 2's folder amendment).</summary>
    public const float BreadcrumbHeight = 32f;

    /// <summary>Gap between grid cells — the same <c>Spacing.S</c> the pane's grid strip lays out with, restated here so the
    /// derived column count and the strip that renders it cannot disagree.</summary>
    public const float GridGap = 8f;

    /// <summary>Below this pane width the sort/view trigger renders icon-only so the search field gets the row.</summary>
    public const float SortIconOnlyWidth = 280f;

    /// <summary>Revision 2's folder amendment: at or above this pane width folders disclose INLINE (recursive, indented);
    /// below it — and always in the overlay drawer — folders NAVIGATE (session-only drill-in with breadcrumb + back).
    /// A 240–320 DIP pane cannot carry four indent levels and still show a playlist name.</summary>
    public const float DrillInWidth = 320f;

    public static bool IsGrid(int view) => view >= (int)SidebarV3View.CompactGrid;
    public static bool IsList(int view) => view <= (int)SidebarV3View.List;

    /// <summary>Minimum grid cell edge per view — the input to the derived column count.</summary>
    public static float MinCellWidth(int view) => view == (int)SidebarV3View.CompactGrid ? 84f : 116f;

    /// <summary>The derived column count: floor((cross + gap) / (min + gap)), never less than 1. §3.2.8 — the sidebar's cell
    /// size is DERIVED from the pane width, never chosen (which is why the sort/view flyout has no S/M/L row and why the
    /// persisted <c>V3GridSize</c> is deliberately unread).</summary>
    public static int Columns(int view, float cross)
    {
        if (!float.IsFinite(cross) || cross <= 0f) return 1;
        float min = MinCellWidth(view);
        int n = (int)MathF.Floor((cross + GridGap) / (min + GridGap));
        return n < 1 ? 1 : n;
    }

    /// <summary>Whether the library-only search box holds a real query — <c>SidebarSearch.Normalize(raw).Length &gt; 0</c>
    /// without the trimmed COPY that would allocate. This is read once per row per epoch check, so the difference matters.</summary>
    public static bool HasQuery(string? raw)
    {
        if (raw is null) return false;
        for (int i = 0; i < raw.Length; i++)
            if (!char.IsWhiteSpace(raw[i])) return true;
        return false;
    }

    // ── persisted-code coercion (§3.2.17 "auto-corrected on load") ────────────────────────────────────────────────────
    public static int NormalizeView(int v) => (uint)v <= 3 ? v : (int)SidebarV3View.List;
    public static int NormalizeFilter(int v) => (uint)v <= 4 ? v : (int)SidebarV3Filter.All;
    public static int NormalizeSort(int v) => (uint)v <= 4 ? v : (int)SidebarV3Sort.Recents;
    public static int NormalizeQualifier(int v) => (uint)v <= 3 ? v : (int)SidebarV3Qualifier.Any;
}

/// <summary>The V3 surface's label rules. Separate from the metrics so the strings and the geometry can be reviewed
/// independently, and so a caller never hand-writes a <c>Loc.Get</c> for a chip/sort/view that already has one.
///
/// <para>R3.0.3 — the per-ROW subtitle rules are gone: rows are the shared pane's, and their subtitle comes from
/// <c>SidebarPaneText.SubtitleOf</c> (the one owner for every design). What remains is the CHROME's vocabulary.</para></summary>
static class LibraryV3Labels
{
    public static string Filter(int filter) => filter switch
    {
        (int)SidebarV3Filter.Playlists => Loc.Get(Strings.Sidebar.V3.Filter.Playlists),
        (int)SidebarV3Filter.Podcasts => Loc.Get(Strings.Sidebar.V3.Filter.Podcasts),
        (int)SidebarV3Filter.Albums => Loc.Get(Strings.Sidebar.V3.Filter.Albums),
        (int)SidebarV3Filter.Artists => Loc.Get(Strings.Sidebar.V3.Filter.Artists),
        _ => Loc.Get(Strings.Sidebar.V3.Title),
    };

    public static string Qualifier(int qualifier) => qualifier switch
    {
        (int)SidebarV3Qualifier.ByYou => Loc.Get(Strings.Sidebar.V3.Qualifier.ByYou),
        (int)SidebarV3Qualifier.BySpotify => Loc.Get(Strings.Sidebar.V3.Qualifier.BySpotify),
        (int)SidebarV3Qualifier.Mixed => Loc.Get(Strings.Sidebar.V3.Qualifier.Mixed),
        _ => "",
    };

    /// <summary>Sort labels REUSE <c>library.sort.*</c> for codes 0–3 (§3.2.6 keeps the numbering index-aligned with
    /// <c>LibrarySortView.SortLabel</c>); only the sidebar's own Custom order is a new key.</summary>
    public static string Sort(int sort) => sort switch
    {
        (int)SidebarV3Sort.RecentlyAdded => Loc.Get(Strings.Library.Sort.RecentlyAdded),
        (int)SidebarV3Sort.Alphabetical => Loc.Get(Strings.Library.Sort.Alphabetical),
        (int)SidebarV3Sort.Creator => Loc.Get(Strings.Library.Sort.Creator),
        (int)SidebarV3Sort.Custom => Loc.Get(Strings.Sidebar.V3.Sort.Custom),
        _ => Loc.Get(Strings.Library.Sort.Recents),
    };

    public static string ViewGlyph(int view) => view >= (int)SidebarV3View.CompactGrid ? Icons.ViewGrid : Icons.ViewList;
}
