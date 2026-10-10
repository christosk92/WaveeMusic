// ── Shell/Sidebar.Library.cs ───────────────────────────────────────────────────────────────────────────────────────
// Your Library's head (design P.2a) and list shaping: the page dropdown, the chips, the toolbar's search state, the
// grid's columns, the head↔list keyboard handoff — and the list shaper (ex LibraryV3View) and the pin window
//
// Role: CORE · Spec: sidebar-rework-implementation.md §P5.1 · design P.2, P.2a, V.11, Q11-Q13

using FluentGpu.Signals;

namespace Wavee;

/// <summary>One library page in the "Your Library ▾" menu: its route key, the kind item it stands for, its title key,
/// its glyph and its count — null while unknown (the menu then shows no number, design D9), a real 0 once known. The menu
/// NAVIGATES; it never filters (design P.2a).</summary>
public readonly record struct SidebarLibraryPage(string Route, string Item, string TitleKey, string Glyph, int? Count);

/// <summary>The four kind counts the dropdown shows (from the binder's full projection, before any chip or search).
/// <see cref="Known"/> is false until the library projection is Ready: <c>default</c> is "unknown", never "0 albums".</summary>
public readonly record struct SidebarLibraryCounts(int Albums, int Artists, int Podcasts, int Audiobooks, bool Known)
{
    public int? Of(string item) => !Known ? null : item switch
    {
        "albums" => Albums,
        "artists" => Artists,
        "podcasts" => Podcasts,
        "audiobooks" => Audiobooks,
        _ => 0,
    };
}

public static class SidebarLibraryHeadRules
{
    public const float HomeRowHeight = 40f;       // row A 36 + 2·2 margin
    public const float SeparatorHeight = 8f;
    public const float DropdownRowHeight = 40f;
    public const float ChipRowHeight = 40f;
    public const float ChipHeight = 32f;
    public const float ChipGap = 8f;
    public const float ToolbarHeight = 40f;
    public const float ToolbarControl = 32f;
    public const float RuleHeight = 1f;
    /// <summary>The dropdown title's x in pane space (design P.2a: "at the header's x", pane 14) and its 8-px hit inset.</summary>
    public const float TitleX = SidebarRowGeometry.PaneEdge + SidebarRowGeometry.HeaderTextX;
    public const float TitleHitInset = 8f;
    public const float TitleChevron = 10f;
    /// <summary>The chip row's and the toolbar's lead/trail inset in pane space (the chips' first edge sits under the title).</summary>
    public const float BandInsetX = 12f;
    /// <summary>The whole head above the list: 3 (top inset) + 40 + 8 + 40 + 40 + 40 + 1 = 172.</summary>
    public const float HeadHeight = SidebarRowGeometry.PaneTopInset + HomeRowHeight + SeparatorHeight + DropdownRowHeight
                                    + ChipRowHeight + ToolbarHeight + RuleHeight;

    /// <summary>The grid's columns (design P.2: 2–4 from the pane width, no size picker): as many 116-px cells with 8-px
    /// gaps as the lane fits, clamped to [2, 4]. The lane is the pane minus the list's 4+4 pad and the 8+8 strip inset.</summary>
    public const float GridMinCell = 116f;
    public const float GridGap = 8f;
    public static int GridColumns(float paneWidth)
    {
        float lane = paneWidth - 8f - 16f;
        if (!float.IsFinite(lane) || lane <= 0f) return 2;
        int n = (int)System.MathF.Floor((lane + GridGap) / (GridMinCell + GridGap));
        return System.Math.Clamp(n, 2, 4);
    }

    /// <summary>The kind items in their catalogue order ("albums", "artists", "podcasts", "audiobooks").</summary>
    static readonly string[] Items = ["albums", "artists", "podcasts", "audiobooks"];
    static readonly string[] TitleKeys = ["nav.albums", "nav.artists", "nav.podcasts", "nav.audiobooks"];

    /// <summary>The dropdown's pages: every kind not hidden through Filters (Q12), in catalogue order, with its count.
    /// Glyphs are the destinations' own (the caller passes <paramref name="glyphOf"/> = <c>Shell.Dest(route).Glyph</c>).</summary>
    public static void Pages(SidebarLibraryKinds hidden, in SidebarLibraryCounts counts, System.Func<string, string> glyphOf,
                             List<SidebarLibraryPage> into)
    {
        into.Clear();
        for (int i = 0; i < Items.Length; i++)
        {
            if ((hidden & SidebarCatalogue.KindFlagOf(Items[i])) != 0) continue;
            into.Add(new SidebarLibraryPage(Items[i], Items[i], TitleKeys[i], glyphOf(Items[i]), counts.Of(Items[i])));
        }
    }

    /// <summary>Is the title a menu button? Not when every kind is hidden through Filters (a legal state: the library
    /// section is locked, so hiding kinds never hides it): with no page to offer, the title is plain text — no chevron, no
    /// button role, no empty flyout.</summary>
    public static bool DropdownIsMenu(int pageCount) => pageCount > 0;

    /// <summary>The page the current route is, or null. A hidden kind's page is not a library page (its route still works).</summary>
    public static string? PageOf(string route, SidebarLibraryKinds hidden)
    {
        for (int i = 0; i < Items.Length; i++)
            if (string.Equals(route, Items[i], System.StringComparison.Ordinal))
                return (hidden & SidebarCatalogue.KindFlagOf(Items[i])) != 0 ? null : Items[i];
        return null;
    }

    /// <summary>The dropdown carries the pill (pill rule 3, the lowest visible ancestor) only while the route is one of
    /// its pages AND no list row already carries it (a route pin "albums" is an exact match, rule 1).</summary>
    public static bool DropdownCarriesPill(string route, SidebarLibraryKinds hidden, bool rowCarriesPill)
        => !rowCarriesPill && PageOf(route, hidden) is not null;

    /// <summary>The chips (Q12: a hidden kind has none), Playlists first.</summary>
    public static void Chips(SidebarLibraryKinds hidden, List<SidebarLibraryFilter> into)
    {
        into.Clear();
        for (var f = SidebarLibraryFilter.Playlists; f <= SidebarLibraryFilter.Audiobooks; f++)
            if (SidebarLibraryFilters.HasChip(f, hidden)) into.Add(f);
    }

    /// <summary>Click an active chip (or its ✕) to clear it; any other chip replaces it. One active at a time.</summary>
    public static SidebarLibraryFilter Toggle(SidebarLibraryFilter current, SidebarLibraryFilter chip)
        => current == chip ? SidebarLibraryFilter.None : chip;

    /// <summary>The chip's label key.</summary>
    public static string ChipKey(SidebarLibraryFilter f) => f switch
    {
        SidebarLibraryFilter.Playlists => "sidebar.chip.playlists",
        SidebarLibraryFilter.Albums => "nav.albums",
        SidebarLibraryFilter.Artists => "nav.artists",
        SidebarLibraryFilter.Podcasts => "nav.podcasts",
        SidebarLibraryFilter.Audiobooks => "nav.audiobooks",
        _ => "",
    };

    public enum SearchEscape : byte { Clear = 0, Close = 1 }

    /// <summary>Esc in the box: a query clears first; an empty box closes back to the toolbar (design P.2a).</summary>
    public static SearchEscape OnEscape(string text) => text.Length > 0 ? SearchEscape.Clear : SearchEscape.Close;

    /// <summary>An empty box that lost focus closes; a query stays on screen.</summary>
    public static bool ClosesOnBlur(string text) => text.Length == 0;

    /// <summary>The toolbar's sort label key (the button reads "Recents", "A–Z", …).</summary>
    public static string SortKey(SidebarLibrarySort sort) => "sidebar.sort." + SidebarStoreV3.SortName(sort);

    /// <summary>The toolbar button's label key: Recents reads "Recents" (design P.2a); the others use their menu label.</summary>
    public static string ToolbarSortKey(SidebarLibrarySort sort)
        => sort == SidebarLibrarySort.Recents ? "sidebar.sort.short.recents" : SortKey(sort);

    /// <summary>Is Custom order offered? Only under the Playlists chip (it IS the rootlist order, design P.2).</summary>
    public static bool CustomOrderOffered(SidebarLibraryFilter filter) => filter == SidebarLibraryFilter.Playlists;

    /// <summary>The sort that applies: Custom order outside the Playlists chip falls back to Recents (the stored sort is kept).</summary>
    public static SidebarLibrarySort Effective(SidebarLibrarySort stored, SidebarLibraryFilter filter)
        => stored == SidebarLibrarySort.CustomOrder && !CustomOrderOffered(filter) ? SidebarLibrarySort.Recents : stored;

    // ── the toolbar's shape at the pane width (design: the sort/view row keeps icons only below 240) ─────────────────

    /// <summary>IconButton's <c>ControlSize.Small</c> box (search, the icon-only sort, ⋯).</summary>
    public const float ToolbarIconButton = 28f;
    public const float ToolbarGap = 4f;
    /// <summary>The labelled sort button's chrome around its label: 8 + 16 glyph + 8 gap + 8.</summary>
    public const float SortButtonChrome = 40f;
    /// <summary>Below this pane width List/Grid leave the toolbar (they stay in ⋯ › View, which LibraryOptions always has).</summary>
    public const float FoldViewBelow = 240f;

    /// <summary>Full: [⌕][⇅ label] · [≡][▦][+][⋯]. IconSort: the sort button is icon-only (as while searching). Folded:
    /// icon-only sort AND no List/Grid toggles — the ⋯ (which holds Filters, View and Show Liked Songs) must never be the
    /// control that gets clipped.</summary>
    public enum ToolbarShape : byte { Full = 0, IconSort = 1, Folded = 2 }

    /// <summary>The toolbar's content lane: the pane minus the head's 4+4 pad and the toolbar's own lead inset (8).</summary>
    public static float ToolbarLane(float paneWidth) => paneWidth - 2f * SidebarRowGeometry.PaneEdge - (BandInsetX - SidebarRowGeometry.PaneEdge);

    /// <summary>The summed control widths plus gaps for a shape (the spacer is 0 at the minimum). The label is measured
    /// with the row label's estimate (<see cref="SidebarLabelFit.AverageCharWidth"/>).</summary>
    public static float ToolbarWidth(ToolbarShape shape, string sortLabel)
    {
        float sort = shape == ToolbarShape.Full ? SortButtonChrome + sortLabel.Length * SidebarLabelFit.AverageCharWidth : ToolbarIconButton;
        float view = shape == ToolbarShape.Folded ? 0f : 2f * ToolbarControl;
        int controls = shape == ToolbarShape.Folded ? 4 : 6;   // search, sort, [List, Grid,] +, ⋯
        return ToolbarIconButton + sort + view + ToolbarControl + ToolbarIconButton + (controls - 1) * ToolbarGap;
    }

    /// <summary>The widest shape that fits: Folded below <see cref="FoldViewBelow"/>; the label only when the whole row
    /// with it fits the lane ("Recently added" at the 320 default does not: icon-only there).</summary>
    public static ToolbarShape ShapeOf(float paneWidth, string sortLabel)
    {
        if (!float.IsFinite(paneWidth) || paneWidth < FoldViewBelow) return ToolbarShape.Folded;
        return ToolbarWidth(ToolbarShape.Full, sortLabel) <= ToolbarLane(paneWidth) ? ToolbarShape.Full : ToolbarShape.IconSort;
    }

    // ── the keyboard ring (design V.11, P.2a) ────────────────────────────────────────────────────────────────────────

    /// <summary>The head's ring part (Home ↔ dropdown). Down from the dropdown leaves the head for the list's first row;
    /// with an empty list it stays. Up from Home stays.</summary>
    public enum HeadStop : byte { Home = 0, Dropdown = 1, List = 2 }

    public static HeadStop Next(HeadStop from, int direction, int listCount) => (from, direction) switch
    {
        (HeadStop.Home, > 0) => HeadStop.Dropdown,
        (HeadStop.Dropdown, > 0) => listCount > 0 ? HeadStop.List : HeadStop.Dropdown,
        (HeadStop.Dropdown, < 0) => HeadStop.Home,
        (HeadStop.List, < 0) => HeadStop.Dropdown,
        _ => from,
    };

    // ── the Tab order (Q11: "match the screen; add the test") ────────────────────────────────────────────────────────

    /// <summary>One Tab stop of Your Library's head, in screen order. The chips strip is ONE stop (it roves inside).</summary>
    public enum HeadTabStop : byte { Home = 0, Dropdown = 1, Chips = 2, SearchBox = 3, Search = 4, Sort = 5, ListView = 6, GridView = 7, Create = 8, More = 9, List = 10 }

    /// <summary>The head's Tab order — exactly its left-to-right, top-to-bottom screen order (Q11): Home, the dropdown, the
    /// chips, the toolbar (search or the open box, sort, List, Grid, +, ⋯; the view toggles only when the shape keeps
    /// them; the open box replaces search and the right-hand group), then the list. <c>LibraryToolbar</c> BUILDS its
    /// controls by walking this list, so the order the fact pins is the order on screen.</summary>
    public static void TabOrder(ToolbarShape shape, bool searching, List<HeadTabStop> into)
    {
        into.Clear();
        into.Add(HeadTabStop.Home);
        into.Add(HeadTabStop.Dropdown);
        into.Add(HeadTabStop.Chips);
        if (searching)
        {
            into.Add(HeadTabStop.SearchBox);
            into.Add(HeadTabStop.Sort);
        }
        else
        {
            into.Add(HeadTabStop.Search);
            into.Add(HeadTabStop.Sort);
            if (shape != ToolbarShape.Folded)
            {
                into.Add(HeadTabStop.ListView);
                into.Add(HeadTabStop.GridView);
            }
            into.Add(HeadTabStop.Create);
            into.Add(HeadTabStop.More);
        }
        into.Add(HeadTabStop.List);
    }
}

/// <summary>Why Your Library's scroller is empty — or <see cref="None"/> when it has a row (a pin, Liked Songs, a list row) or
/// is still loading (skeletons).</summary>
public enum SidebarLibraryEmpty : byte { None = 0, Library = 1, Filter = 2, Search = 3, Failed = 4 }

/// <summary>Your Library's empty state has ONE owner, the head (§P5.5); the planner plans no Empty row in this layout
/// (§P3.5). Both read the same rows: <see cref="ScrollerRows"/> counts exactly what <c>SidebarRowPlanner.PlanLibrary</c>
/// emits, so "No albums" never sits above a list, and "Your library is empty" never sits above Liked Songs.</summary>
public static class SidebarLibraryEmptyRules
{
    /// <summary>The scroller's content rows: the pins the band keeps (<see cref="SidebarVisibilityRules.ShowsPinInLibrary"/>,
    /// only while Pinned is shown), the Liked row (<see cref="SidebarVisibilityRules.ShowsLiked"/>), the shaped list. While
    /// <paramref name="drilled"/> the level is the folder's children only — no pins, no Liked — exactly as PlanLibrary
    /// plans it under <c>SidebarPlanOptions.Drilled</c>, so an empty folder reads as empty.</summary>
    public static int ScrollerRows(IReadOnlyList<SidebarLibraryEntry>? pins, bool pinnedShown, SidebarLibraryOptions options,
                                   SidebarLibraryFilter filter, string? search, string? likedTitle, int listCount,
                                   bool drilled = false)
    {
        int rows = listCount;
        if (SidebarVisibilityRules.ShowsLiked(options, filter, search, likedTitle, drilled)) rows++;
        if (pinnedShown && !drilled && pins is not null)
            for (int i = 0; i < pins.Count; i++)
            {
                var pin = pins[i];
                if (SidebarVisibilityRules.ShowsPinInLibrary(in pin, filter, search, options.HiddenKinds)) rows++;
            }
        return rows;
    }

    /// <summary>The empty state, in V3's priority order: a failure, then the search, then the chip, then the library itself.
    /// Pending (or any contributing kind pending) shows skeletons, never an empty state.</summary>
    public static SidebarLibraryEmpty Of(int scrollerRows, LoadState load, bool anyPending, SidebarLibraryFilter filter, bool searching)
    {
        if (scrollerRows > 0 || anyPending || load == LoadState.Pending) return SidebarLibraryEmpty.None;
        if (load == LoadState.Failed) return SidebarLibraryEmpty.Failed;
        if (searching) return SidebarLibraryEmpty.Search;
        return filter != SidebarLibraryFilter.None ? SidebarLibraryEmpty.Filter : SidebarLibraryEmpty.Library;
    }
}

/// <summary>Your Library's fixed metrics: the drill-in width, the breadcrumb band, and the query test the search box reads.</summary>
public static class SidebarLibraryMetrics
{
    /// <summary>Below this pane width Your Library drills into folders instead of disclosing them inline (design P.2: 240).
    /// The drawer always drills.</summary>
    public const float DrillInWidth = 240f;

    /// <summary>The drill-in breadcrumb band (narrow/drawer only — the folder amendment).</summary>
    public const float BreadcrumbHeight = 32f;

    /// <summary>Whether the library-only search box holds a real query, without allocating a trimmed copy.</summary>
    public static bool HasQuery(string? raw)
    {
        if (raw is null) return false;
        for (int i = 0; i < raw.Length; i++)
            if (!char.IsWhiteSpace(raw[i])) return true;
        return false;
    }
}

/// <summary>The session's shaping state (replaces LibraryV3DocState): the chip, the stored sort, the view, searching,
/// the drill and the pin presence.</summary>
public readonly record struct SidebarLibraryState(
    SidebarLibraryFilter Filter,
    SidebarLibrarySort Sort,
    bool Descending,
    SidebarLibraryView View,
    int GridColumns,
    bool Searching,
    string? DrillFolderId,
    bool HasPins)
{
    public bool Drilled => DrillFolderId is { Length: > 0 };

    /// <summary>Folders group (inline tree) only for a playlist-capable chip, a list view, no search and no drill (a drilled
    /// level is one folder's direct children) — the old <c>LibraryV3Document.FoldersApply</c> body, retyped.</summary>
    public bool FoldersApply
        => !Searching && !Drilled && View == SidebarLibraryView.List
           && Filter is SidebarLibraryFilter.None or SidebarLibraryFilter.Playlists;
}

/// <summary>Your Library's content ORDER, as one pure pass over the published projection. The projection's own sort is
/// FLAT (a nested playlist can land above its containing folder); this re-groups folders among siblings by the
/// active sort with each folder's children ordered the same way WITHIN it, and also owns the DRILL LEVEL (a
/// drilled-in level is one folder's direct children, flattened to depth 0 — a BUILD INPUT, never a renderer
/// branch). It does not filter, sort, search or decide what is pinned — that already happened upstream.
/// <para>ALLOCATION: one output List plus pooled per-folder buckets, reused across rebuilds — once per plan
/// (a projection publish, a state change, a drill push/pop), never per frame or per row.</para></summary>
public sealed class SidebarLibraryShaper
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
    /// this overlay is Your Library's LOCAL custom order, and moving an item between folders is a rootlist write, made only
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
public sealed class SidebarLibraryWindow : IReadOnlyList<SidebarLibraryEntry>
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
