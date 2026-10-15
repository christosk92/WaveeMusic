// ── Shell/NavModel.Library.cs ──────────────────────────────────────────────────────────────────────────────────────
// Your Library as model state and verbs: the drill stack, the shaping state, the empty-state facts, the chip commit, the
// search (open, clear, Esc, blur), the folder-activation rule and the reorder rules
//
// Role: CORE · Plan: technical-plan.md §5.1 row C10 (BP X27) · design P.2, P.2a, V.11
//
// A `NavLibrary` is ONE mount's session (the docked pane and the narrow-shell drawer each open their own: the drill stack and
// the narrow flag are per mount). It holds no node, no hook and no pixel: the head's focus targets, the pane reference and the
// pill flag stay with the Classic view's `LibrarySession`, which composes one of these. Never persisted.
//
// THREADING: UI thread only, like the model it opens from.

using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public sealed partial class NavModel
{
    /// <summary>Open a Library session for one mount (the docked pane, or the drawer with <paramref name="inDrawer"/>).</summary>
    public NavLibrary OpenLibrary(bool inDrawer) => new(this, inDrawer);
}

/// <summary>What the Library head needs to decide its empty state, read in one go: the options and chip the list was shaped
/// with, the load state of the published list, the scroller's row count, the query the LIST was shaped with, and the verdict.</summary>
public readonly record struct NavLibraryFacts(
    SidebarLibraryOptions Options,
    SidebarLibraryFilter Filter,
    LoadState Load,
    bool AnyPending,
    int Rows,
    string? ShapedSearch,
    SidebarLibraryEmpty Empty);

/// <summary>One Library mount's session. See the file header.</summary>
public sealed class NavLibrary
{
    /// <summary>0.2.9's binder-pump search debounce (<c>SidebarBinderPump.SearchDebounceMs</c>): the view debounces the search
    /// text by this much before it asks <see cref="Resync"/>.</summary>
    public const float SearchDebounceMs = 90f;

    readonly NavModel _model;
    readonly Signal<bool> _narrowFolders = new(false);   // below DrillInWidth (or in the drawer) folders DRILL
    readonly Signal<int> _drillVersion = new(0);         // bumped by every drill / back / reset: one re-plan, no remount
    readonly List<string> _folderIds = new(4), _folderNames = new(4);
    long _viewEpoch = long.MinValue;                     // the pane shapes its input twice per plan (list + rail)

    internal NavLibrary(NavModel model, bool inDrawer)
    {
        _model = model;
        InDrawer = inDrawer;
    }

    /// <summary>This mount is the narrow-shell drawer (always expanded, always drills).</summary>
    public bool InDrawer { get; }

    /// <summary>The derived grid column count (a memo the view owns, because it is a function of the viewport). Null until set.</summary>
    public IReadSignal<int>? Columns { get; set; }

    /// <summary>The built order, shared by the input shaper and the head.</summary>
    public SidebarLibraryShaper View { get; } = new();

    /// <summary>Bumped by every drill, back and reset.</summary>
    public IReadSignal<int> DrillVersion => _drillVersion;

    // ── the published cell, the width ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The published library list cell (shared by every mount).</summary>
    public SidebarEntries Cell => _model.LibraryItems;

    /// <summary>Rebuild the projection now if any trigger moved (one struct compare when nothing did).</summary>
    public void Resync() => _model.Sync();

    /// <summary>The width the library is presented at: the drawer's overlay width, or the docked pane's presented width. The
    /// stored preference is not it: a narrow viewport clamps the pane below it.</summary>
    public static float PresentedWidth(bool inDrawer, float vpW) => inDrawer
        ? SidebarPaneModeRules.OverlayWidth(Sidebar.Width.Value, vpW)
        : Sidebar.PresentedWidth.Value;

    /// <summary>The narrow flag (the view resolves it from the presented width): a wide pane shows the inline tree, so it
    /// never stands inside a drilled folder. Inline disclosure and a drill stack answer the same question, so only one may be live.</summary>
    public void SetNarrow(bool narrow)
    {
        _narrowFolders.SetIfChanged(narrow);
        if (!narrow) ResetDrill();
    }

    // ── the view state ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE Library view state in one value. Reading it SUBSCRIBES the caller (document, mode epoch, head) to every
    /// signal the document is a function of, so none can render a state the others disagree with. Allocation-free.</summary>
    public SidebarLibraryState ReadState()
    {
        _ = Sidebar.LayoutVersion.Value;
        var lib = Sidebar.Doc.Library;
        var filter = SidebarLibraryFilters.Effective((int)Sidebar.LibraryFilter.Value, lib.HiddenKinds);
        _ = _drillVersion.Value;          // a drill / back re-slices the view
        return new SidebarLibraryState(filter, lib.Sort, lib.Descending, lib.View, Columns?.Value ?? 2,
            SidebarLibraryMetrics.HasQuery(Sidebar.LibrarySearch.Value), DrillActive ? CurrentFolderId : null,
            Sidebar.Pins.Count > 0);
    }

    public int ReadModeEpoch() => ReadState().GetHashCode();

    /// <summary>The published projection (filtered, searched, sorted, matching pins FIRST) re-grouped into tree order or
    /// sliced to one folder level. While Pinned is shown the planner draws the pins from <c>input.Pins</c> (§P3.5), so the
    /// list skips the published pin head; with Pinned hidden the pins stay in their natural place.</summary>
    public SidebarProjectionInput ShapeInput(SidebarProjectionInput input)
    {
        var state = ReadState();
        var cell = Cell;
        var published = cell.Current;
        int pinCount = Math.Clamp(cell.PinCount, 0, published.Count);
        int skip = Sidebar.Doc.Find(SidebarSectionKind.Pinned) is { Hidden: false } && !state.Drilled ? pinCount : 0;
        bool group = state.FoldersApply;
        int revision = _model.Revision;

        // The view's own re-bucket gate: `cell.Version` covers every input `View.Build` reads from the projection, `skip`
        // covers the pin band, `group`/`drill` cover the view state. `revision` still goes INTO `View.Build` below, as
        // `EnsureParentMap`'s own cheap internal memo key for the folder→parent walk over the tree slice.
        long epoch = ViewEpoch(cell.Version.Peek(), skip, group, state.DrillFolderId);
        if (epoch != _viewEpoch)
        {
            _viewEpoch = epoch;
            View.Build(published, skip, input.PlaylistTree, revision, state.DrillFolderId, group);
        }
        // ExpandedFolders is KEPT: the shaped list is already depth-stamped (the planner never re-filters it), and
        // PlanLibrary reads the set for one thing only — an expanded PINNED folder's children (§P3.5).
        return input with { Library = View.Rows, LibraryIsTree = group };
    }

    public SidebarPlanOptions ShapeOptions(SidebarPlanOptions o)
        => o with
        {
            Filter = SidebarLibraryFilters.Effective((int)Sidebar.LibraryFilter.Value, Sidebar.Doc.Library.HiddenKinds),
            GridColumns = Columns?.Value ?? 2,
            FoldersInline = DisclosesFoldersInline() && !DrillActive,   // §P3.5: a pinned folder opens in place
            Drilled = DrillActive,                                       // §P3.5: a folder level has no pins / Liked
        };

    static long ViewEpoch(int entriesVersion, int skip, bool group, string? drill)
    {
        unchecked
        {
            long h = entriesVersion;
            h = h * 1099511628211L + skip;
            h = h * 1099511628211L + (group ? 1 : 0);
            h = h * 1099511628211L + (drill is { Length: > 0 } d ? StringComparer.Ordinal.GetHashCode(d) : 0);
            return h;
        }
    }

    // ── the empty-state facts ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A failed library with no rows (a list-only count is enough): the head logs it once.</summary>
    public bool FailedEmpty => Cell.State == LoadState.Failed && View.Count == 0;

    /// <summary>THE ONE OWNER of Your Library's empty state (the planner plans no Empty row in this layout, §P3.5): the
    /// scroller's row count exactly as PlanLibrary builds it — the pins ShowsPinInLibrary keeps, the Liked row, the list — over
    /// the query the LIST was shaped with (the binder's, never the live box text, which runs ahead by the debounce).
    /// SUBSCRIBES the caller to the layout, the binder's input edge and the cell's version: without the cell read a search
    /// with no results never shows its band, and a cleared search keeps a stale one.</summary>
    public NavLibraryFacts ReadFacts()
    {
        _ = Sidebar.LayoutVersion.Value;
        // The kind counts AND the query the list was shaped with publish through the binder's input edge (§P5.2).
        _ = _model.InputVersion.Value;
        var cell = Cell;
        _ = cell.Version.Value;
        var doc = Sidebar.Doc;
        var lib = doc.Library;
        var filter = SidebarLibraryFilters.Effective((int)Sidebar.LibraryFilter.Value, lib.HiddenKinds);
        var load = cell.State;
        bool anyPending = cell.AnyContributingKindPending;
        var input = _model.Projection;
        string? shapedSearch = SidebarSearch.Normalize(input.Search) is { Length: > 0 } q ? q : null;
        bool pinnedShown = doc.Find(SidebarSectionKind.Pinned) is { Hidden: false };
        int rows = SidebarLibraryEmptyRules.ScrollerRows(input.Pins, pinnedShown, lib, filter, shapedSearch,
                                                         Loc.Get("nav.likedSongs"), View.Count, DrillActive);
        var empty = SidebarLibraryEmptyRules.Of(rows, load, anyPending, filter, shapedSearch is not null);
        return new NavLibraryFacts(lib, filter, load, anyPending, rows, shapedSearch, empty);
    }

    // ── reorder rules. Pins reorder through the pin store; the library reorders only as a ROOTLIST move under
    //    Playlists · Custom order (the pane's resource drop → WaveeResourceDrop.MoveRootlist), never a local overlay. ────

    bool CanReorderCustom()
        => Sidebar.LibraryFilter.Peek() == SidebarLibraryFilter.Playlists && Sidebar.Doc.Library.Sort == SidebarLibrarySort.CustomOrder
           && Sidebar.LibrarySearch.Peek().Length == 0 && Sidebar.Doc.Library.View == SidebarLibraryView.List && !DrillActive;

    /// <summary>D10 — the tree shows a SORTED view, so positional drops refuse with "clear sorting to reorder" while Into
    /// stays legal.</summary>
    public bool TreeSortedNonCustom() => !CanReorderCustom();

    // ── drill ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    // A STACK of ids AND names: nesting is genuine, and a folder that vanishes mid-session must still label sanely.

    public bool DrillActive => _folderIds.Count > 0;
    public string CurrentFolderId => _folderIds.Count == 0 ? "" : _folderIds[^1];
    public string CurrentFolderName => _folderNames.Count == 0 ? Loc.Get(Strings.Sidebar.V3.Title) : _folderNames[^1];
    /// <summary>The level BACK returns to — the breadcrumb's accessible name.</summary>
    public string ParentName => _folderNames.Count <= 1 ? Loc.Get(Strings.Sidebar.V3.Title) : _folderNames[^2];

    /// <summary>Enter a folder level. It also EXPANDS the folder: the projection omits a collapsed folder's children, so the
    /// level would be empty — and widening back to inline leaves you standing inside it.</summary>
    public void Drill(string? folderId, string? name)
    {
        if (string.IsNullOrEmpty(folderId)) return;
        _model.SetFolderExpanded(folderId, true);
        _folderIds.Add(folderId);
        _folderNames.Add(name is { Length: > 0 } ? name : Loc.Get(Strings.Sidebar.V3.Kind.Folder));
        BumpDrill();
        Resync();
    }

    /// <summary>Leave the current folder level.</summary>
    public void Back()
    {
        if (_folderIds.Count == 0) return;
        _folderIds.RemoveAt(_folderIds.Count - 1);
        _folderNames.RemoveAt(_folderNames.Count - 1);
        BumpDrill();
    }

    /// <summary>Leave drill-in entirely (pane widened, a query landed, the kind filter changed).</summary>
    public void ResetDrill()
    {
        if (_folderIds.Count == 0) return;
        _folderIds.Clear();
        _folderNames.Clear();
        BumpDrill();
    }

    void BumpDrill() => _drillVersion.Value = _drillVersion.Peek() + 1;

    /// <summary>WHAT A FOLDER ROW DOES — the one decision point, so the renderer never learns about drill levels: a GRID cannot
    /// disclose, so switch to the list and open it; a NARROW pane drills; a WIDE pane toggles inline.</summary>
    public void ActivateFolder(string folderId, string name)
    {
        if (folderId.Length == 0) return;
        if (Sidebar.Doc.Library.View == SidebarLibraryView.Grid)
        {
            SetView(SidebarLibraryView.List);
            _model.SetFolderExpanded(folderId, true);
        }
        else if (_narrowFolders.Peek()) Drill(folderId, name);
        else _model.ToggleFolder(folderId);
        Resync();
    }

    /// <summary>Only a wide LIST discloses in place; grid switches view and narrow drills, so neither seeds an inline
    /// expand/collapse animation.</summary>
    public bool DisclosesFoldersInline() => !_narrowFolders.Peek() && Sidebar.Doc.Library.View == SidebarLibraryView.List;

    // ── filter, sort, view ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Set the chip outright; a chip change leaves any drilled folder.</summary>
    public void SetFilter(SidebarLibraryFilter filter)
    {
        Sidebar.SetLibraryFilter(filter);
        ResetDrill();
        Resync();
    }

    /// <summary>A chip click: the active chip clears on a second click (<see cref="SidebarLibraryHeadRules.Toggle"/>).</summary>
    public void ToggleChip(SidebarLibraryFilter chip) => SetFilter(SidebarLibraryHeadRules.Toggle(Sidebar.LibraryFilter.Peek(), chip));

    /// <summary>The empty state's "clear filter": back to every kind. (A drilled folder stays: this is not a chip change.)</summary>
    public void ClearFilter()
    {
        Sidebar.SetLibraryFilter(SidebarLibraryFilter.None);
        Resync();
    }

    /// <summary>The toolbar's sort (and its direction) through the one layout op.</summary>
    public void SetSort(SidebarLibrarySort sort, bool descending) => Sidebar.SetLibrarySort(sort, descending);

    /// <summary>List ↔ Grid.</summary>
    public void SetView(SidebarLibraryView view) => Sidebar.SetLibraryView(view);

    /// <summary>H3 (#85) — the refusal toast's action: Playlists + Custom order, the one state a positional reorder is legal.</summary>
    public void ShowCustomOrder()
    {
        Sidebar.SetLibraryFilter(SidebarLibraryFilter.Playlists);
        SetSort(SidebarLibrarySort.CustomOrder, false);
        Resync();
    }

    // ── find ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open the search box (the toolbar's tile, the rail's tile, Ctrl+F).</summary>
    public void OpenSearch() => Sidebar.LibrarySearchOpen.SetIfChanged(true);

    /// <summary>Open the search box holding <paramref name="query"/>. The binder follows the debounced text, as it does for typing.</summary>
    public void Find(string query)
    {
        OpenSearch();
        Sidebar.LibrarySearch.SetIfChanged(query);
    }

    /// <summary>Clear the text and ask the binder now (the empty state's "clear search", the box's first Esc).</summary>
    public void ClearSearch()
    {
        Sidebar.LibrarySearch.SetIfChanged("");
        Resync();
    }

    /// <summary>Close AND clear: a closed box showing leftover text next time would read as a bug.</summary>
    public void CloseSearch()
    {
        Sidebar.LibrarySearchOpen.SetIfChanged(false);
        Sidebar.LibrarySearch.SetIfChanged("");
        Resync();
    }

    /// <summary>THE ESC RULE (<see cref="SidebarLibraryHeadRules.OnEscape"/>): Esc in a box holding text clears the text and
    /// keeps the box; Esc in an empty box closes it, after <paramref name="beforeClose"/> (the view's "the magnifier takes
    /// focus when it remounts" latch). Returns what it did.</summary>
    public SidebarLibraryHeadRules.SearchEscape Escape(Action? beforeClose = null)
    {
        var what = SidebarLibraryHeadRules.OnEscape(Sidebar.LibrarySearch.Peek());
        if (what == SidebarLibraryHeadRules.SearchEscape.Clear)
        {
            ClearSearch();
            return what;
        }
        beforeClose?.Invoke();
        CloseSearch();
        return what;
    }

    /// <summary>The box lost focus: an empty box closes (<see cref="SidebarLibraryHeadRules.ClosesOnBlur"/>).</summary>
    public void SearchBlurred()
    {
        if (SidebarLibraryHeadRules.ClosesOnBlur(Sidebar.LibrarySearch.Peek())) CloseSearch();
    }

    // ── shared verbs ───────────────────────────────────────────────────────────────────────────────────────────────────

    public void Navigate(string key, string? arg) => _model.Navigate(key, arg);

    public void CreatePlaylist() => _model.CreatePlaylist();

    /// <summary>The retry banner / error state (S10): ask every failed source AGAIN (a new ask clears its failure, so the list
    /// goes back to loading), then invalidate + sync so the projection reads that at once.</summary>
    public void Retry()
    {
        _model.RetryFailedSources();
        _model.Invalidate();
        _model.Sync();
    }
}
