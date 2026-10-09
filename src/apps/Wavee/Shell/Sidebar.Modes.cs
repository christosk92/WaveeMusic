// ── Shell/Sidebar.Modes.cs ─────────────────────────────────────────────────────────────────────────────────────────
// Library V3's engine-free vocabulary: the view-state enums, the fixed chrome metrics and labels, the search host's
// arithmetic, the filter rail's chip model, the tree and drill-in window over the shaped projection, and the state
// struct the Library V3 pane is a function of.
//
// Role: CORE
// Owner: J
// Wave: 4
// Budget: named partial of Sidebar.cs; the Library V3 half only (the design vocabulary, Classic's locked document and
//         the nav band moved out with the modes they served).
// Spec: ch 25 §8 (§ V3) + ch 25 §0.13
//
// Nothing here renders anything. `LibraryV3DocState` is the plain-values input the Library V3 pane's section and
// display mapping reads, so that mapping stays unit-testable with no signal, no Loc, no Element and no width.

using FluentGpu.Foundation;

namespace Wavee;

// The Library-V3 view state, persisted as ints (no enum arm in the settings store).
public enum SidebarV3Filter : byte { All = 0, Playlists = 1, Podcasts = 2, Albums = 3, Artists = 4 }
public enum SidebarV3Qualifier : byte { Any = 0, ByYou = 1, BySpotify = 2, Mixed = 3 }
public enum SidebarV3Sort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, Custom = 4 }
public enum SidebarV3View : byte { CompactList = 0, List = 1, CompactGrid = 2, Grid = 3 }

// ── LIBRARY V3 — PURE RULES (METRICS, SEARCH, CHIPS, VIEW, STATE) ────────────────────────────────────────────────────
//
// Library V3's engine-free decisions, ported verbatim in behaviour: the fixed chrome band heights and width
// thresholds (LibraryV3Metrics/Labels), the search host's escape/blur/width arithmetic (LibraryV3SearchRules), the
// filter rail's idle/filtered/fused chip model (LibraryV3ChipStrip), the tree re-grouping + drill-in view over the
// already-shaped projection (LibraryV3View/LibraryV3Window), and the view state the pane is a function of
// (LibraryV3DocState). Nothing here renders anything.

/// <summary>V3's pure chrome geometry: the fixed band heights (§3.2.2's vertical stack), the two width thresholds
/// the chrome branches on, the derived grid column rule, and the persisted-code coercion every reader shares.
/// <para>The row/grid CONTENT geometry is deliberately NOT here — content is the one SidebarPane's own
/// SidebarPaneMetrics/SidebarRowMetrics ladder; re-adding it here would recreate the second ladder that made the
/// same Cozy row 44 in one mode and 48 in another.</para></summary>
public static class LibraryV3Metrics
{
    // The expanded chrome stack, top to bottom: nav band (30) → header (44) → toolbar (36) → chip rail (40) →
    // (rule) → breadcrumb (32, narrow/drawer only).
    /// <summary>The library-destination word rail (#85 H4): five words between Home and the library rule. Shorter
    /// than a list row (<see cref="NavRowHeight"/>, 40) because it is a band of type, not a list.</summary>
    public const float DestinationRailH = 30f;
    public const float HeaderHeight = 44f;
    public const float ToolbarHeight = 36f;
    /// <summary>The ONE filter rail: a facet's sub-filter fuses INTO its pill instead of a second (qualifier) band,
    /// so 40 — vs the pills' own 28 — is deliberate slack for the fused pill's raised inner segment and its shadow.</summary>
    public const float ChipRailHeight = 40f;
    /// <summary>The drill-in breadcrumb band (narrow/drawer only — the folder amendment).</summary>
    public const float BreadcrumbHeight = 32f;

    /// <summary>The nav band's own row height — CHROME above the header, so it does not have to match the 44
    /// content row; Spotify's own "Your Library" nav rows are 40.</summary>
    public const float NavRowHeight = 40f;
    /// <summary>Inter-word gap on the destination rail (<see cref="Design.Type.DenseTitle"/> / DenseMeta at 13).</summary>
    public const float DestinationWordGap = 14f;
    /// <summary>The edge fade a clipped destination word peeks through — the affordance that says the rail
    /// scrolls. Labels never truncate to a glyph; the rail scrolls instead. Painted from the rail's LIVE scroll
    /// geometry per render, never a hardcoded side — a fade with nothing behind it is a lie.</summary>
    public const float DestinationRailFade = 20f;
    /// <summary>Diameter of the hover-revealed pager chevrons — smaller than the standard icon-button size so a
    /// pager large enough to brush the band's own edges never reads as enlarging it.</summary>
    public const float DestinationRailChevronSize = 20f;
    public const float DestinationRailChevronGlyph = 10f;
    /// <summary>Fraction of the rail's live viewport width one pager click scrolls. Less than 1 so the outgoing
    /// page's trailing word still peeks at the opposite edge — continuity, not a jump-cut.</summary>
    public const float DestinationRailPageStep = 0.8f;

    /// <summary>Grid cell gap, restated from the pane's own grid-strip spacing so the derived column count and the
    /// strip that renders it cannot disagree.</summary>
    public const float GridGap = 8f;

    /// <summary>Below this pane width the sort/view trigger renders icon-only so the search field gets the row.</summary>
    public const float SortIconOnlyWidth = 280f;

    /// <summary>At/above this pane width folders disclose INLINE (recursive, indented); below it — and always in
    /// the overlay drawer — folders NAVIGATE (drill-in, breadcrumb + back). A 240-320 pane cannot carry four indent
    /// levels and still show a playlist name.</summary>
    public const float DrillInWidth = 320f;

    public static bool IsGrid(int view) => view >= (int)SidebarV3View.CompactGrid;
    public static bool IsList(int view) => view <= (int)SidebarV3View.List;

    /// <summary>The folder rule, made mechanical: folder rows exist only under the All/Playlists lenses, only in a
    /// list view, only with an empty search, and never at a drilled-in level.</summary>
    public static bool FoldersApply(in LibraryV3DocState state)
        => IsList(state.View) && !state.Searching && !state.Drilled
           && (state.Filter == (int)SidebarV3Filter.All || state.Filter == (int)SidebarV3Filter.Playlists);

    /// <summary>The reducer clamps a persisted grid's columns to [2,4]; a derived count must land in the same range,
    /// and a pane too narrow for two columns still gets two (the strip wraps rather than overflowing).</summary>
    public static int ClampColumns(int columns) => columns < 2 ? 2 : columns > 4 ? 4 : columns;

    /// <summary>Minimum grid cell edge per view — the input to the derived column count.</summary>
    public static float MinCellWidth(int view) => view == (int)SidebarV3View.CompactGrid ? 84f : 116f;

    /// <summary>The derived column count: floor((cross + gap) / (min + gap)), never less than 1. The cell size is
    /// DERIVED from the pane width, never chosen — no S/M/L row, and the persisted V3GridSize stays unread.</summary>
    public static int Columns(int view, float cross)
    {
        if (!float.IsFinite(cross) || cross <= 0f) return 1;
        float min = MinCellWidth(view);
        int n = (int)MathF.Floor((cross + GridGap) / (min + GridGap));
        return n < 1 ? 1 : n;
    }

    /// <summary>Whether the library-only search box holds a real query, without allocating a trimmed copy.</summary>
    public static bool HasQuery(string? raw)
    {
        if (raw is null) return false;
        for (int i = 0; i < raw.Length; i++)
            if (!char.IsWhiteSpace(raw[i])) return true;
        return false;
    }

    // Persisted-code coercion — "auto-corrected on load".
    public static int NormalizeView(int v) => (uint)v <= 3 ? v : (int)SidebarV3View.List;
    public static int NormalizeFilter(int v) => (uint)v <= 4 ? v : (int)SidebarV3Filter.All;
    public static int NormalizeSort(int v) => (uint)v <= 4 ? v : (int)SidebarV3Sort.Recents;
    public static int NormalizeQualifier(int v) => (uint)v <= 3 ? v : (int)SidebarV3Qualifier.Any;
}

/// <summary>V3's label vocabulary, as LOC KEYS — CORE never calls into the localization engine; the UI layer
/// resolves these dotted keys. Separate from <see cref="LibraryV3Metrics"/> so strings and geometry review
/// independently, and so no caller hand-writes a key for a chip/sort/view that already has one.</summary>
public static class LibraryV3Labels
{
    public static string Filter(int filter) => filter switch
    {
        (int)SidebarV3Filter.Playlists => "sidebar.v3.filter.playlists",
        (int)SidebarV3Filter.Podcasts => "sidebar.v3.filter.podcasts",
        (int)SidebarV3Filter.Albums => "sidebar.v3.filter.albums",
        (int)SidebarV3Filter.Artists => "sidebar.v3.filter.artists",
        _ => "sidebar.v3.title",
    };

    public static string Qualifier(int qualifier) => qualifier switch
    {
        (int)SidebarV3Qualifier.ByYou => "sidebar.v3.qualifier.byYou",
        (int)SidebarV3Qualifier.BySpotify => "sidebar.v3.qualifier.bySpotify",
        (int)SidebarV3Qualifier.Mixed => "sidebar.v3.qualifier.mixed",
        _ => "",
    };

    /// <summary>Sort keys REUSE library.sort.* for codes 0-3 (index-aligned with the library sort view); only
    /// V3's own Custom order is a new key.</summary>
    public static string Sort(int sort) => sort switch
    {
        (int)SidebarV3Sort.RecentlyAdded => "library.sort.recentlyAdded",
        (int)SidebarV3Sort.Alphabetical => "library.sort.alphabetical",
        (int)SidebarV3Sort.Creator => "library.sort.creator",
        (int)SidebarV3Sort.Custom => "sidebar.v3.sort.custom",
        _ => "library.sort.recents",
    };

    /// <summary>The view toggle's glyph — an engine icon NAME string (CORE is engine-free: no Icons reference).</summary>
    public static string ViewGlyph(int view) => view >= (int)SidebarV3View.CompactGrid ? "ViewGrid" : "ViewList";
}

/// <summary>The library search host's pure decisions: the Escape ladder, the blur-close rule and the open-width
/// arithmetic — the morph's three fiddliest rules, reviewable and testable without a component, a signal or a
/// frame.</summary>
public static class LibraryV3SearchRules
{
    /// <summary>The closed host's width — the same box the magnifier button always was, so the morph's start/end
    /// frame never jumps on open/close.</summary>
    public const float ClosedWidth = 32f;

    /// <summary>The sort/view trigger's icon-only box — what <see cref="OpenWidth"/> must leave room for so the
    /// open field never overlaps the pill it shares the toolbar row with.</summary>
    public const float SortIconOnlyWidth = 28f;

    /// <summary>The toolbar's own gap between the search host and the sort/view trigger.</summary>
    public const float Gap = 4f;

    /// <summary>At/above this pane width the field is INLINE — always expanded, sharing the toolbar row with the
    /// full sort/view pill. Below it the field collapses to the 32-DIP magnifier and a click morphs it open while
    /// the sort pill drops to icon-only. Sits above <see cref="LibraryV3Metrics.SortIconOnlyWidth"/> (280) on
    /// purpose: an inline field never coexists with an icon-only pill, so the row has exactly two shapes.</summary>
    public const float InlineWidth = 300f;

    /// <summary>The toolbar row's shape for one (pane width, user opened it, has text) triple.</summary>
    /// <param name="Inline">The field is permanently expanded (wide pane) — no button, no tooltip, no morph.</param>
    /// <param name="Expanded">The field is showing (inline, or opened/holding text on a narrow pane).</param>
    /// <param name="SortIconOnly">The sort/view pill shows only its glyph.</param>
    public readonly record struct Layout(bool Inline, bool Expanded, bool SortIconOnly);

    /// <summary>Resolve the row's shape. Narrow + text keeps the field open even if the user never "opened" it (a
    /// query typed while wide must survive a seam drag past the threshold); narrow + empty + not opened is the
    /// button.</summary>
    public static Layout Resolve(float paneWidth, bool openedByUser, bool hasText)
    {
        bool inline = paneWidth >= InlineWidth;
        if (inline) return new Layout(true, true, SortIconOnly: false);
        bool expanded = openedByUser || hasText;
        return new Layout(false, expanded, SortIconOnly: expanded || paneWidth < 280f);
    }

    public enum EscapeAction : byte { None, Clear, Close }

    /// <summary>One Escape clears the query (mirroring the WinUI TextBox DeleteButton); a SECOND Escape, on an
    /// already-empty field, closes it. Never <see cref="EscapeAction.None"/> — the host is only reachable while
    /// open, and an open host always has something to do with Escape.</summary>
    public static EscapeAction OnEscape(string text) => text.Length > 0 ? EscapeAction.Clear : EscapeAction.Close;

    /// <summary>Focus left the editor: an EMPTY field closes; a field carrying a query stays open (Spotify keeps an
    /// active filter on screen even after the pointer moves to a row).</summary>
    public static bool ClosesOnBlur(string text) => text.Length == 0;

    /// <summary>The open host's width: the toolbar's own content lane minus the icon-only sort pill and the one gap
    /// between them, so the field's trailing edge lands exactly where the pill's leading edge would otherwise sit.
    /// Floored at <see cref="ClosedWidth"/> so a pane narrower than pill+gap still yields a host, not a negative
    /// width.</summary>
    public static float OpenWidth(float paneWidth, float toolbarPadH)
        => MathF.Max(ClosedWidth, paneWidth - toolbarPadH - SortIconOnlyWidth - Gap);
}

/// <summary>What one rendered position of the Library V3 filter rail IS — a pure function of (filter, qualifier,
/// whether the data evidences a qualifier). Generalises the Home facet strip for a rail with exactly ONE
/// selectable facet at a time: a leading Clear slot stands in for "All", because clearing here is a distinct
/// GESTURE (a ✕ that pops in), not another tab in the row.</summary>
public enum V3ChipKind : byte { Clear, Facet, Fused, Option }

/// <summary>One rendered position of the rail. <see cref="SelectFilter"/>/<see cref="SelectQualifier"/> is exactly
/// what a tap (or Space/Enter on the roved chip) writes back to the persisted filter/qualifier — one write path for
/// every kind instead of one per shape.
/// <para><see cref="Key"/> is the node key the renderer must use. A <see cref="V3ChipKind.Facet"/> slot and the
/// <see cref="V3ChipKind.Fused"/> slot of the SAME code share it ("v3f{code}") — that shared identity IS the
/// loose-pill ⇄ fused-pill morph; a distinct key per shape would unmount one and mount the other with nothing left
/// to reflow from.</para>
/// <para><see cref="Route"/> (issue #85, H4) — the destination page this chip's KIND has, or null when it has
/// none. A plain tap always writes the filter/qualifier; a non-null route is only a secondary affordance (a
/// double-click) the renderer may offer. Playlists has no "all playlists" page, so its Facet/Fused slots carry a
/// null route like Clear/Option always do.</para></summary>
public readonly record struct V3ChipSlot(
    V3ChipKind Kind, int Code, bool Selected, string Key, int SelectFilter, int SelectQualifier, string? Route = null);

/// <summary>The Library V3 filter rail's layout, as a pure function of (persisted filter, persisted qualifier,
/// whether the data evidences ≥2 qualifier flavors).
/// <para>THREE SHAPES, ONE MODEL. <b>Idle</b> (no filter): the four facets, unselected, no ✕ (nothing to clear).
/// <b>Filtered</b> (a facet picked, no qualifier fused): a leading Clear slot, then the selected facet — plus, ONLY
/// under Playlists with ≥2 provenance flavors evidenced, the three qualifier options spilled right after it.
/// <b>Fused</b> (a qualifier is ALSO picked — only possible under Playlists): Clear, then one Fused slot sharing
/// the facet's own key — the loose facet pill and the fused pill are the SAME node across that transition, which
/// is the entire point of the shared key.</para></summary>
public static class LibraryV3ChipStrip
{
    const int All = (int)SidebarV3Filter.All;
    const int Playlists = (int)SidebarV3Filter.Playlists;
    const int Any = (int)SidebarV3Qualifier.Any;

    public static readonly int[] Facets =
    [
        (int)SidebarV3Filter.Playlists, (int)SidebarV3Filter.Podcasts,
        (int)SidebarV3Filter.Albums, (int)SidebarV3Filter.Artists,
    ];

    public static readonly int[] Qualifiers =
    [
        (int)SidebarV3Qualifier.ByYou, (int)SidebarV3Qualifier.BySpotify, (int)SidebarV3Qualifier.Mixed,
    ];

    /// <summary>idle: F F F F. filtered: X [F*] (+ its options — Playlists only, and only when the data evidences
    /// them). fused: X [F*│Q] — the fused pill and the loose facet share key "v3f{code}" (the morph).</summary>
    public static List<V3ChipSlot> Slots(int filter, int qualifier, bool qualifiersAvailable)
    {
        var slots = new List<V3ChipSlot>(8);
        if (filter == All)
        {
            foreach (var f in Facets) slots.Add(Facet(f, false));
            return slots;
        }

        slots.Add(new V3ChipSlot(V3ChipKind.Clear, 0, false, "v3-clear", All, Any));

        bool owns = filter == Playlists && qualifiersAvailable;
        if (owns && qualifier != Any)
        {
            // Fused: tapping the compound pill is ONE step back (drop the qualifier), not all the way to All.
            // Route is always null here in practice (only Playlists fuses, and Playlists has no route), threaded
            // through anyway so a future fuseable facet is not silently dropped from the route contract.
            slots.Add(new V3ChipSlot(V3ChipKind.Fused, filter, true, "v3f" + filter, filter, Any, RouteFor(filter)));
        }
        else
        {
            slots.Add(Facet(filter, true));
            if (owns)
                foreach (var q in Qualifiers)
                    slots.Add(new V3ChipSlot(V3ChipKind.Option, q, false, "v3q" + q, filter, q));
        }
        return slots;
    }

    /// <summary>Roving focus survives relayout by CODE, not index: the slot whose key matches
    /// <paramref name="focusedKey"/>, or 0 when nothing matches (the focused chip left the rail entirely).</summary>
    public static int FocusIndex(List<V3ChipSlot> slots, string? focusedKey)
    {
        if (focusedKey is not null)
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].Key == focusedKey) return i;
        return 0;
    }

    /// <summary>A facet slot. Unselected (idle): tapping SELECTS it. Selected (filtered, not fused): tapping
    /// CLEARS back to All — the pill itself is the "remove this filter" affordance while only one thing is active.</summary>
    static V3ChipSlot Facet(int f, bool selected)
        => new(V3ChipKind.Facet, f, selected, "v3f" + f, selected ? All : f, Any, RouteFor(f));

    /// <summary>Issue #85 (H4) — the chip's "open this as a page" destination. Only Albums/Artists/Podcasts have an
    /// actual library page; Playlists has none, so its chip stays filter-only like Clear/Option.</summary>
    public static string? RouteFor(int filter) => filter switch
    {
        (int)SidebarV3Filter.Albums => "albums",
        (int)SidebarV3Filter.Artists => "artists",
        (int)SidebarV3Filter.Podcasts => "podcasts",
        _ => null,
    };
}

/// <summary>The V3 content ORDER, as one pure pass over the published projection. The projection's own sort is
/// FLAT (a nested playlist can land above its containing folder); this re-groups folders among siblings by the
/// active sort with each folder's children ordered the same way WITHIN it, and also owns the DRILL LEVEL (a
/// drilled-in level is one folder's direct children, flattened to depth 0 — a BUILD INPUT, never a renderer
/// branch). It does not filter, sort, search or decide what is pinned — that already happened upstream.
/// <para>ALLOCATION: one output List plus pooled per-folder buckets, reused across rebuilds — once per plan
/// (a projection publish, a state change, a drill push/pop), never per frame or per row.</para></summary>
public sealed class LibraryV3View
{
    /// <summary>Hard recursion guard for the inline folder walk — the rootlist is unbounded and a cyclic or absurd
    /// tree must not be able to stack-overflow the UI thread. 32 is 0.2.9's number (its `SidebarTree.MaxDepth`); the
    /// deepest folder nesting Spotify itself allows is far below it, so this only ever fires on corrupt data.</summary>
    public const int MaxDepth = 32;

    readonly List<SidebarLibraryEntry> _rows = new(256);

    // folder id → parent folder id ("" at top level), walked off the binder's fully flattened tree slice. A
    // projected entry knows its CONTAINING folder for every kind except a folder row (whose FolderId is its own
    // id), so a folder's parent is only recoverable from the tree walk — memoised on the binder revision.
    readonly Dictionary<string, string> _parentOfFolder = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<int>> _buckets = new(StringComparer.Ordinal);
    readonly List<List<int>> _bucketPool = new();
    readonly List<int> _top = new();
    readonly HashSet<string> _folderRows = new(StringComparer.Ordinal);
    string[]? _folderStack;
    int _parentRevision = int.MinValue;

    /// <summary>The built order. Every entry carries a REWRITTEN Depth (display indent) and SourceOrder (its
    /// position here), which is what lets the planner's CustomOrder comparator reproduce this order verbatim.</summary>
    public IReadOnlyList<SidebarLibraryEntry> Rows => _rows;

    public int Count => _rows.Count;

    /// <summary>True when the drilled-into folder is no longer present (unfollowed, filtered away, reloaded) — the
    /// mode component pops the stack rather than showing a level whose breadcrumb points at nothing.
    /// "Missing" means the FOLDER ROW is gone, not that it has no children: an empty folder is a legitimate level.</summary>
    public bool DrillTargetMissing { get; private set; }

    /// <summary>Rebuild the order.</summary>
    /// <param name="published">The shaped projection.</param>
    /// <param name="skip">Leading entries to drop — the pin band, when rendered as its own section (0 otherwise).</param>
    /// <param name="tree">The binder's fully flattened rootlist tree slice, for the folder→parent map. Null
    /// degrades every folder to top level rather than hiding rows.</param>
    /// <param name="treeRevision">The binder revision <paramref name="tree"/> came from — the parent map's memo key.</param>
    /// <param name="drillFolderId">The folder whose direct children to list, or null/"" for the library root.</param>
    /// <param name="group">Whether to re-group into tree order. False passes the slice through flat at depth 0 —
    /// what a search (already flat) and the grid views (no disclosure) want.</param>
    public void Build(IReadOnlyList<SidebarLibraryEntry>? published, int skip,
                      IReadOnlyList<SidebarLibraryEntry>? tree, int treeRevision,
                      string? drillFolderId, bool group)
    {
        _rows.Clear();
        DrillTargetMissing = false;
        if (published is null || published.Count == 0)
        {
            // An empty projection at a drill level is not a MISSING target (cold library) — reporting "missing"
            // here would pop the stack on every cold start.
            ReleaseBuckets();
            _top.Clear();
            _folderRows.Clear();
            return;
        }

        int n = published.Count;
        if (skip < 0) skip = 0;
        if (skip > n) skip = n;

        bool drill = drillFolderId is { Length: > 0 };

        if (!drill && !group)
        {
            for (int i = skip; i < n; i++) Emit(published[i], 0);
            return;
        }

        EnsureParentMap(tree, treeRevision);
        // A drill level buckets EVERY row (a pinned playlist inside the folder must still appear inside it, and
        // the pin band does not render at a drilled-in level); the root level buckets only the post-pin remainder.
        BuildBuckets(published, drill ? 0 : skip);

        if (drill)
        {
            DrillTargetMissing = !_folderRows.Contains(drillFolderId!);
            if (!DrillTargetMissing && _buckets.TryGetValue(drillFolderId!, out var kids))
                for (int i = 0; i < kids.Count; i++) Emit(published[kids[i]], 0);
            return;
        }

        EmitLevel(published, _top, 0);
    }

    /// <summary>The parent-folder id of a built row — the sibling band a custom-order drag may move WITHIN. "" for
    /// a top-level row.</summary>
    public string ParentOf(int index)
        => (uint)index < (uint)_rows.Count ? ParentKey(_rows[index]) : "";

    /// <summary>Whether two built rows are siblings. A drop aimed across a folder boundary must not commit here:
    /// this overlay is V3's LOCAL custom order, and moving an item between folders is a rootlist write, made only
    /// through the resource-drop seam and folder actions.</summary>
    public bool SameParent(int a, int b)
        => string.Equals(ParentOf(a), ParentOf(b), StringComparison.Ordinal);

    /// <summary>The same boundary, applied DURING the gesture: the slot a drag from <paramref name="from"/> may
    /// actually reach when the pointer asks for <paramref name="to"/>. Snapping the REQUESTED slot to the nearest
    /// one inside the source's sibling run means the gap never opens across a boundary in the first place.
    /// <para>The run is the SET of same-parent rows, not a contiguous span (an expanded folder's children sit
    /// between two top-level siblings), so a top-level drag must be able to travel PAST them. Ties go to the lower
    /// slot — either is equally legal and the choice only needs to be deterministic.</para></summary>
    public int ClampToSiblingRun(int from, int to)
    {
        int n = _rows.Count;
        if (n == 0 || (uint)from >= (uint)n) return to;
        if (to < 0) to = 0;
        else if (to >= n) to = n - 1;
        if (SameParent(from, to)) return to;

        string parent = ParentKey(_rows[from]);
        int below = -1, above = -1;
        for (int i = to - 1; i >= 0; i--)
            if (string.Equals(ParentKey(_rows[i]), parent, StringComparison.Ordinal)) { below = i; break; }
        for (int i = to + 1; i < n; i++)
            if (string.Equals(ParentKey(_rows[i]), parent, StringComparison.Ordinal)) { above = i; break; }
        // `from` is itself in the run, so at least one side always resolves; the fallback is a no-move.
        if (below < 0) return above < 0 ? from : above;
        if (above < 0) return below;
        return to - below <= above - to ? below : above;
    }

    /// <summary>The stable key (entry id) at a built index — what the pane's reorder band reports per slot.</summary>
    public string KeyAt(int index) => (uint)index < (uint)_rows.Count ? _rows[index].Id : "";

    /// <summary>Materialize the ENTIRE visible order into <paramref name="into"/> as entry ids, with the row at
    /// <paramref name="from"/> moved to <paramref name="to"/> — "on any user move the whole current visible order
    /// is written", which is what keeps later appends stable without ever rewriting the overlay again.</summary>
    public void MaterializeOrder(List<string> into, int from, int to)
    {
        into.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            int slot = MovedIndex(i, from, to);                    // which VIEW slot supplies row i after the move
            if ((uint)slot >= (uint)_rows.Count) continue;
            var e = _rows[slot];
            // An authored route row (Liked Songs) and a track row have no place in a playlist order.
            if (e.Kind is SidebarEntryKind.AppRoute or SidebarEntryKind.Track) continue;
            if (e.Id.Length > 0) into.Add(e.Id);
        }
    }

    // The permutation a single remove-at-from/insert-at-to applies, read backwards (which VIEW slot supplies row i).
    static int MovedIndex(int i, int from, int to)
    {
        if (from == to) return i;
        if (from < to)
        {
            if (i < from || i > to) return i;
            return i == to ? from : i + 1;
        }
        if (i < to || i > from) return i;
        return i == to ? from : i - 1;
    }

    // ── the rebuild ──────────────────────────────────────────────────────────────────────────────────────────────

    // Depth-first emission of one sibling level: a folder row is followed by its children, present in the
    // projection only when the folder is expanded (the binder's own gate) — an expansion test here would be a
    // second, driftable copy of that rule.
    void EmitLevel(IReadOnlyList<SidebarLibraryEntry> src, List<int> level, int depth)
    {
        for (int i = 0; i < level.Count; i++)
        {
            int at = level[i];
            var e = src[at];
            Emit(e, depth);
            if (depth >= MaxDepth || !e.IsFolder) continue;
            if (_buckets.TryGetValue(e.FolderId, out var kids)) EmitLevel(src, kids, depth + 1);
        }
    }

    void BuildBuckets(IReadOnlyList<SidebarLibraryEntry> src, int from)
    {
        _top.Clear();
        ReleaseBuckets();
        _folderRows.Clear();

        for (int i = from; i < src.Count; i++)
            if (src[i].IsFolder && src[i].FolderId.Length > 0) _folderRows.Add(src[i].FolderId);

        for (int i = from; i < src.Count; i++)
        {
            string parent = ParentKey(src[i]);
            // A row whose parent folder is NOT itself a visible row (pinned into the band, dropped by the lens, or
            // a cold tree map) is promoted to top level. Nothing is ever hidden because its container is elsewhere.
            if (parent.Length == 0 || !_folderRows.Contains(parent)) _top.Add(i);
            else Bucket(parent).Add(i);
        }
    }

    string ParentKey(in SidebarLibraryEntry e)
    {
        if (!e.IsFolder) return e.FolderId;
        return _parentOfFolder.TryGetValue(e.FolderId, out var p) ? p : "";
    }

    void Emit(in SidebarLibraryEntry e, int depth)
    {
        int d = depth < 0 ? 0 : depth > MaxDepth ? MaxDepth : depth;
        // Depth is the DISPLAY indent the row planner stamps onto its rows; SourceOrder is this row's position,
        // which is what makes a re-sort by the planner's CustomOrder comparator (SourceOrder ascending) a no-op.
        _rows.Add(e with { Depth = d, SourceOrder = _rows.Count });
    }

    List<int> Bucket(string folderId)
    {
        if (_buckets.TryGetValue(folderId, out var list)) return list;
        if (_bucketPool.Count > 0)
        {
            list = _bucketPool[_bucketPool.Count - 1];
            _bucketPool.RemoveAt(_bucketPool.Count - 1);
            list.Clear();
        }
        else
        {
            list = new List<int>(8);
        }
        _buckets[folderId] = list;
        return list;
    }

    void ReleaseBuckets()
    {
        foreach (var kv in _buckets) _bucketPool.Add(kv.Value);
        _buckets.Clear();
    }

    // The folder→parent map, walked off the binder's tree slice (depth-first, pre-order, folders included, fully
    // flattened regardless of expansion — exactly why it can answer "who contains this folder" when the published
    // list cannot). Memoised on the binder revision: it only moves when the rootlist does.
    void EnsureParentMap(IReadOnlyList<SidebarLibraryEntry>? tree, int revision)
    {
        if (tree is null)
        {
            if (_parentRevision != int.MinValue) { _parentOfFolder.Clear(); _parentRevision = int.MinValue; }
            return;
        }
        if (revision == _parentRevision && _parentOfFolder.Count > 0) return;
        _parentRevision = revision;
        _parentOfFolder.Clear();

        var stack = _folderStack ??= NewStack();
        for (int i = 0; i < stack.Length; i++) stack[i] = "";
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            int d = e.Depth;
            if (d < 0 || d + 1 >= stack.Length) continue;
            if (!e.IsFolder || e.FolderId.Length == 0) continue;
            _parentOfFolder[e.FolderId] = stack[d];
            stack[d + 1] = e.FolderId;
        }
    }

    static string[] NewStack()
    {
        var s = new string[MaxDepth + 2];
        for (int i = 0; i < s.Length; i++) s[i] = "";
        return s;
    }
}

/// <summary>
/// A reusable WINDOW over a projection list — [start, start+count) without copying a single entry.
///
/// <para>It is how the mode component hands the pane its pin band: the shaped projection already leads with the
/// surviving pins, so the pin section's rows and the library section's rows are two windows over ONE list and can
/// never disagree about which pins survived the active filter. One instance is reused for the pane's life.</para>
/// </summary>
public sealed class LibraryV3Window : IReadOnlyList<SidebarLibraryEntry>
{
    /// <summary>The out-of-range fallback. NOT <c>default(SidebarLibraryEntry)</c> — a default instance's
    /// positional string members are null, and a row built from one would hand the renderer a null label.</summary>
    static readonly SidebarLibraryEntry Blank = SidebarLibraryEntry.ForRoute("", "");

    IReadOnlyList<SidebarLibraryEntry>? _source;
    int _start;
    int _count;

    public void Set(IReadOnlyList<SidebarLibraryEntry>? source, int start, int count)
    {
        _source = source;
        int n = source?.Count ?? 0;
        if (start < 0) start = 0;
        if (start > n) start = n;
        if (count < 0) count = 0;
        if (start + count > n) count = n - start;
        _start = start;
        _count = count;
    }

    public int Count => _count;

    /// <summary>Bounds-checked against BOTH the window and the LIVE source: the projection publishes into one
    /// reused List, so a window taken before a rebuild that shrank the list must degrade to a blank row, not throw.</summary>
    public SidebarLibraryEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count || _source is not { } src) return Blank;
            int at = _start + index;
            return (uint)at < (uint)src.Count ? src[at] : Blank;
        }
    }

    public IEnumerator<SidebarLibraryEntry> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The V3 view state the document is a function of — plain values only, so the synthesizer stays pure and
/// testable. Every member is what the mode component read from persisted preferences (or derived from the pane
/// width) on the render that built the document.
/// </summary>
/// <param name="Filter">A <see cref="SidebarV3Filter"/> code, already normalized.</param>
/// <param name="Qualifier">A <see cref="SidebarV3Qualifier"/> code, already normalized.</param>
/// <param name="Sort">A <see cref="SidebarV3Sort"/> code, already normalized.</param>
/// <param name="Descending">V3's direction flag — "reverse the sort's natural direction", NOT "descending".</param>
/// <param name="View">A <see cref="SidebarV3View"/> code, already normalized.</param>
/// <param name="GridColumns">The column count derived from the pane width (ignored by the list views).</param>
/// <param name="Searching">Whether the library-only search box holds a non-empty query (flattens the tree).</param>
/// <param name="DrillFolderId">The folder whose direct children are being listed, or null/"" at the library root.</param>
/// <param name="HasPins">Whether any pin SURVIVED the active lens (the projection's pin band is non-empty).</param>
/// <param name="LikedPinned">Whether Liked Songs is itself pinned — rendered as pin #n, never twice.</param>
/// <param name="QualifiersAvailable">Whether the data evidences ≥2 provenance classes.</param>
/// <param name="DragInFlight">Issue #85 (H1) — a Wavee resource drag is live ANYWHERE right now (read by the mode
/// root and folded in here rather than read from this pure struct — see <see cref="PinsBandVisible"/>).</param>
public readonly record struct LibraryV3DocState(
    int Filter = (int)SidebarV3Filter.All,
    int Qualifier = (int)SidebarV3Qualifier.Any,
    int Sort = (int)SidebarV3Sort.Recents,
    bool Descending = false,
    int View = (int)SidebarV3View.List,
    int GridColumns = 2,
    bool Searching = false,
    string? DrillFolderId = null,
    bool HasPins = false,
    bool LikedPinned = false,
    bool QualifiersAvailable = false,
    bool DragInFlight = false)
{
    /// <summary>At a drilled-in level the pane shows exactly one folder's direct children — no pin band, no shortcut.</summary>
    public bool Drilled => DrillFolderId is { Length: > 0 };

    /// <summary>Renders whenever pins survived the lens at the library root with no query — OR (issue #85, H1)
    /// while a drag is live anywhere, so pinning by drag is possible even on a fresh (zero-pin) install; the drop
    /// zone itself downgrades to its resting look for a drag that cannot be pinned. A search dissolves it
    /// regardless — search results are one flat relevance list, matching pins still lead it but not as a band.</summary>
    public bool PinsBandVisible => (HasPins || DragInFlight) && !Drilled && !Searching;

    /// <summary>The exact scope of the Liked Songs shortcut row: the unfiltered library and the Playlists lens,
    /// never while searching (a route row is not a search result), never when it is already a pin, never inside a
    /// folder.</summary>
    public bool LikedVisible
        => !LikedPinned && !Searching && !Drilled
           && (Filter == (int)SidebarV3Filter.All || Filter == (int)SidebarV3Filter.Playlists);
}
