using System;
using Wavee.Core.Sidebar;

namespace Wavee;

// THE ONE ROW-GEOMETRY LADDER, in the engine-free layer so it is unit-testable.
//
// WHY IT MOVED HERE. `SidebarRowMetrics` (Shared/SidebarEntityRow.cs) used to own the height/indent ladder outright, but
// that file is engine-bound (`SidebarCover`, `Icon`, `BoxEl`) and is therefore NOT source-included by Wavee.Tests — so the
// single most load-bearing number in the sidebar ("Cozy+subtitle = 44 = Classic's row") could not be asserted anywhere.
// The arithmetic now lives here (System + Wavee.Core.Sidebar only) and `SidebarRowMetrics` DELEGATES, so there is still
// exactly one ladder and a test can now pin Classic's document and a Curated template to the same number.
//
// It also owns the two PURE geometry primitives the pane needs over a planned row list: the cumulative content-space Y of
// a row (the analytic offset drop placement / bring-into-view uses) and the selection TRAVEL
// DIRECTION between two plan indices, which is what makes the row's selection indicator move *toward* the new selection
// instead of cross-fading in place.
static class SidebarRowGeometry
{
    /// <summary>The Classic entity-row height (44) — the number every landed sidebar row already uses.</summary>
    public const float ClassicHeight = 44f;

    // ── THE ONE CONTENT LANE ─────────────────────────────────────────────────────────────────────────────────────────
    // The pane had a RAGGED LEFT EDGE because two families of surface computed their own inset from raw literals: the
    // virtualized ROWS landed at PanePad.Left + IndentFor(0) = 14, while every FIXED CHROME BAND mounted above the list
    // (Library V3's header / toolbar / chip rail / rule / breadcrumb) padded to a bare 8 and landed 6 DIP short of them.
    // Naming the lane once — here, in the engine-free layer, where a test can reach it — is what stops the next band
    // from inventing a third number: a band expresses its padding as ContentLane/ContentLaneEnd, never as a literal.

    /// <summary>The pane's horizontal edge inset (8) — <c>SidebarPaneMetrics.PanePad</c>'s left/right. It is applied
    /// ONCE, around the virtualized list, so a band mounted ABOVE that list sits outside it and must reproduce it as
    /// part of <see cref="ContentLane"/> rather than padding to this number on its own.</summary>
    public const float PaneEdge = 8f;

    /// <summary>A row's OWN leading padding at depth 0 (6) — the base term of <see cref="IndentFor"/>.</summary>
    public const float RowInsetLeft = 6f;

    /// <summary>A row's OWN trailing padding (8) — the right-hand half of <c>SidebarPaneMetrics.RowInset</c>.</summary>
    public const float RowInsetRight = 8f;

    /// <summary>THE CONTENT LANE (14): the single x at which pane content begins — a row's selection gutter, a section
    /// header's title, a chrome band's first glyph, a divider's hairline. Rows reach it as
    /// <see cref="PaneEdge"/> + <see cref="IndentFor"/>(0); a fixed band above the list pads to it directly.</summary>
    public const float ContentLane = PaneEdge + RowInsetLeft;

    /// <summary>The lane's TRAILING twin (16): <see cref="PaneEdge"/> + <see cref="RowInsetRight"/>. It is 2 DIP wider
    /// than <see cref="ContentLane"/> because the landed row padding is asymmetric (6 leading / 8 trailing) — carried
    /// forward as-is, not re-derived, so nothing shifts horizontally while the lane is being named.</summary>
    public const float ContentLaneEnd = PaneEdge + RowInsetRight;

    /// <summary>Row height by density. Compact suppresses subtitles outright (no room for a second line), so the three
    /// canonical heights are 32 (compact) / 40 (cozy) / 44 (cozy with subtitle); Comfortable adds 4 DIP on top of the
    /// cozy pair (44 without a subtitle — Classic's shortcut row — and 48 with one).</summary>
    public static float HeightFor(SidebarDensity density, bool hasSubtitle) => density switch
    {
        SidebarDensity.Compact => 32f,
        SidebarDensity.Comfortable => hasSubtitle ? 48f : 44f,
        _ => hasSubtitle ? 44f : 40f,
    };

    /// <summary>A section's uniform row height straight from its persisted display options — the shape a document /
    /// template comparison needs (the renderer's <c>SidebarPaneMetrics.RowHeight</c> is this call).</summary>
    public static float HeightFor(SidebarDisplayOptions? opts)
    {
        var o = opts ?? SidebarDisplayOptions.Default;
        return HeightFor(o.Density, o.Subtitles);
    }

    /// <summary>ONE nesting level of indent (12). Named because the drop resolver reads the ladder BACKWARDS — it turns
    /// a pointer X into a depth — and a second literal there would let the cue's indent and the row's disagree.</summary>
    public const float IndentStep = 12f;

    /// <summary>The deepest level the indent ladder honours; beyond it rows stop marching right.</summary>
    public const int MaxIndentDepth = 4;

    // ── THE ONE TREE-CONTENT ORIGIN ──────────────────────────────────────────────────────────────────────────────────
    // A TREE row is not laid out on `IndentFor(depth)`: `SidebarEntityRow.TreeLeading` pads the row ONCE at IndentFor(0)
    // and then spends real cells — the selection gutter, one connector cell per level, and a fixed disclosure cell —
    // before the row's art begins. The caret used to be translated by `IndentFor(depth)` and `PickDepth` used to read
    // the same ladder BACKWARDS, so the line painted roughly one whole level left of what it meant and the outdent band
    // (x < 12) was practically unreachable with a pointer (F2/F3). The three constants below are the layout's own, they
    // live HERE because this file is the engine-free one a test can reach, and `TreeLeading` now CONSUMES them — so the
    // rendered row, the caret and the depth pick cannot disagree by construction.

    /// <summary>The 3-DIP selection-accent reserve every row leads with (<c>SidebarEntityRow.SelGutter</c>).</summary>
    public const float SelGutterWidth = 3f;

    /// <summary>One tree connector cell (12 — the engine's <c>Spacing.M</c>). Equal to <see cref="IndentStep"/> by
    /// design: a tree level and an indent level are the same step, drawn two different ways.</summary>
    public const float TreeGuideStep = IndentStep;

    /// <summary>The fixed disclosure cell every tree row reserves at EVERY depth (16 — the engine's <c>Spacing.L</c>),
    /// so folder and leaf art share one column.</summary>
    public const float TreeChevronCell = 16f;

    /// <summary>THE x at which a tree row's CONTENT (its art, and therefore the caret that means "insert at this depth")
    /// begins: <c>IndentFor(0) + SelGutter + depth·TreeGuideStep + TreeChevronCell</c> — 25, 37, 49, … Row-relative,
    /// like the pointer x the resolver reads.</summary>
    public static float TreeContentX(int depth)
    {
        int d = depth < 0 ? 0 : depth > MaxIndentDepth ? MaxIndentDepth : depth;
        return IndentFor(0) + SelGutterWidth + d * TreeGuideStep + TreeChevronCell;
    }

    /// <summary>Left padding for a nesting depth: <see cref="RowInsetLeft"/> base + <see cref="IndentStep"/> per level,
    /// clamped at <see cref="MaxIndentDepth"/> levels.</summary>
    public static float IndentFor(int depth)
        => RowInsetLeft + (depth < 0 ? 0 : depth > MaxIndentDepth ? MaxIndentDepth : depth) * IndentStep;

    /// <summary>The section header band's own height (28) — <c>SidebarSectionHeader.Height</c> delegates here so the
    /// analytic row ladder and the rendered header cannot drift.</summary>
    public const float HeaderHeight = 28f;

    /// <summary>R3.1.3 — the vertical air above a section header that is not the pane's first row, and the gap between a
    /// header and its first body row. <c>SidebarPaneMetrics.SectionGap</c>/<c>HeaderBodyGap</c> delegate here.</summary>
    public const float SectionGap = 8f;
    /// <inheritdoc cref="SectionGap"/>
    public const float HeaderBodyGap = 2f;

    /// <summary>An explicit <c>Divider</c> section's band height (16) — the hairline centred 8 DIP below the previous row.</summary>
    public const float DividerHeight = 16f;

    /// <summary>The quiet empty hint's band height (32). <c>SidebarPaneMetrics.EmptyHintHeight</c> delegates here.</summary>
    public const float EmptyHintHeight = 32f;

    /// <summary>The Pinned section's empty state IS its drop zone, and it rests at 56 (it grows to 72 only while a
    /// compatible drag is live — a transient the measured layout corrects on its own).</summary>
    public const float PinDropZoneRestHeight = 56f;

    /// <summary>The inline filter-chip strip a header carries when an editable <c>EntityList</c> asks for it: a 26-DIP
    /// pill row + its 2-DIP bottom padding, joined to the header by a 4-DIP gap. It WRAPS at a narrow pane, so this is
    /// the one term of the ladder that is an honest approximation rather than an identity — the measured seam corrects
    /// it on realize (which is exactly what the analytic ladder is a SEED for).</summary>
    public const float ChipHeight = 26f;
    /// <inheritdoc cref="ChipHeight"/>
    public const float ChipStripHeight = ChipHeight + 2f;
    /// <inheritdoc cref="ChipStripHeight"/>
    public const float ChipStripGap = 4f;

    /// <summary>The EntityEmbed hero card's height ladder (Compact 56 / Cozy 72 / Comfortable 88) plus the 2+2 DIP of
    /// vertical breathing room the card carries as a margin. <c>SidebarPaneMetrics.CardHeight</c> owns the ladder's
    /// unmargined half.</summary>
    public static float CardHeightFor(SidebarDensity density) => density switch
    {
        SidebarDensity.Compact => 56f,
        SidebarDensity.Comfortable => 88f,
        _ => 72f,
    };

    /// <summary>The actionable degraded state's band (48, or 56 when it carries a reason line).</summary>
    public static float PromptHeight(bool hasReason) => hasReason ? 56f : 48f;

    /// <summary>The <c>TreeEnd</c> chrome row's extent (24): the "top level, at the end" target the tree never had, and
    /// small enough that it reads as the tree's closing gutter rather than as an item.</summary>
    public const float TreeEndHeight = 24f;

    /// <summary>Subtitles are never rendered at Compact density.</summary>
    public static bool SubtitleVisible(SidebarDensity density, string? subtitle)
        => density != SidebarDensity.Compact && subtitle is { Length: > 0 };

    // ── pure plan geometry ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The CONTENT-SPACE top of plan row <paramref name="index"/>: the sum of every earlier row's extent. The
    /// pane's rows are contiguous inside one virtualized list (no inter-row spacing), so this prefix sum IS the row's Y —
    /// which is what lets drop placement and navigation geometry survive recycling. Returns 0 for a negative index and
    /// clamps an index past the end to the total extent.
    /// <para><paramref name="extentOf"/> must report the row's MEASURED height including any rhythm padding the slot
    /// wraps around it, or the result drifts from the rendered layout row by row.</para></summary>
    public static float ContentYOf(int index, int count, Func<int, float> extentOf)
    {
        if (extentOf is null) throw new ArgumentNullException(nameof(extentOf));
        if (index <= 0) return 0f;
        int stop = index < count ? index : count;
        float y = 0f;
        for (int i = 0; i < stop; i++)
        {
            float e = extentOf(i);
            if (float.IsNaN(e) || e <= 0f) continue;   // a zero/degenerate row contributes nothing
            y += e;
        }
        return y;
    }

    /// <summary>The first plan index whose row resolves to <paramref name="route"/>, or -1. <paramref name="routeAt"/> is
    /// the caller's row→route projection (a projected entry's RouteKey, or a hand-placed Route item's key); null/empty
    /// means "this row is not a navigation target".</summary>
    public static int IndexOfRoute(int count, Func<int, string?> routeAt, string? route)
    {
        if (routeAt is null) throw new ArgumentNullException(nameof(routeAt));
        if (string.IsNullOrEmpty(route)) return -1;
        for (int i = 0; i < count; i++)
        {
            string? r = routeAt(i);
            if (r is { Length: > 0 } && string.Equals(r, route, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>Which way the selection TRAVELLED: +1 when the new row sits BELOW the old one, -1 when it sits above,
    /// 0 when the direction is unknowable (either row is off-plan, or it is the same row).
    /// <para>0 is a first-class answer, not a failure: a selection that arrives from off-plan (a deep link, a row inside a
    /// collapsed section, the first paint) has no travel direction, and the indicator must then simply fade in rather
    /// than slide from an invented side.</para></summary>
    public static int DirectionOf(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return 0;
        return toIndex > fromIndex ? 1 : -1;
    }

    /// <summary>The plan index of a <c>PlaylistTree</c> folder's header row, or −1. The folder is addressed by its OWN
    /// group id (<c>entry.FolderId</c>), never by the row key, so a renamed/re-keyed folder still resolves.</summary>
    public static int FolderHeaderIndexOf(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries,
                                          string folderId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrEmpty(folderId)) return -1;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind != SidebarRowKind.FolderHeader || (uint)row.EntryIndex >= (uint)entries.Count) continue;
            if (string.Equals(entries[row.EntryIndex].FolderId, folderId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>Resolve the contiguous PREORDER BAND a planned folder header owns — every row after it, in the same
    /// section, that is DEEPER than the folder. That band is exactly what a disclosure inserts on expand and removes on
    /// collapse, so the pane's choreography and the tests share this one resolver.</summary>
    public static bool TryFolderDescendantRange(IReadOnlyList<SidebarRow> rows,
                                                IReadOnlyList<SidebarLibraryEntry> entries,
                                                string folderId, out int firstIndex, out int count)
    {
        firstIndex = count = 0;
        int folderIndex = FolderHeaderIndexOf(rows, entries, folderId);
        if (folderIndex < 0) return false;
        var folder = rows[folderIndex];
        if ((uint)folder.EntryIndex >= (uint)entries.Count) return false;
        int depth = entries[folder.EntryIndex].Depth;
        int end = folderIndex + 1;
        while (end < rows.Count)
        {
            var row = rows[end];
            if (!string.Equals(row.SectionId, folder.SectionId, StringComparison.Ordinal)
                || (uint)row.EntryIndex >= (uint)entries.Count
                || entries[row.EntryIndex].Depth <= depth) break;
            end++;
        }
        firstIndex = folderIndex + 1;
        count = end - firstIndex;
        return count > 0;
    }

    /// <summary>Resolve the contiguous BODY owned by one planned section header. A different section at the same or a
    /// shallower depth is a structural sibling and terminates the band even when that sibling is a divider or has no
    /// header of its own. Deeper rows belong to a nested CustomGroup subtree and stay inside the parent disclosure.</summary>
    public static bool TrySectionBodyRange(IReadOnlyList<SidebarRow> rows, string sectionId,
                                           out int firstIndex, out int count)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrEmpty(sectionId))
        {
            firstIndex = count = 0;
            return false;
        }

        int header = -1;
        byte depth = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind != SidebarRowKind.SectionHeader
                || !string.Equals(row.SectionId, sectionId, StringComparison.Ordinal)) continue;
            header = i;
            depth = row.Depth;
            break;
        }

        if (header < 0)
        {
            firstIndex = count = 0;
            return false;
        }

        int end = header + 1;
        while (end < rows.Count)
        {
            var row = rows[end];
            if (row.Depth <= depth && !string.Equals(row.SectionId, sectionId, StringComparison.Ordinal)) break;
            end++;
        }

        firstIndex = header + 1;
        count = end - firstIndex;
        return count > 0;
    }
}
