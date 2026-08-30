using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The Library-V3 mode's SESSION state — everything the chrome components share that is neither a persisted preference
/// (that is <see cref="SidebarPreferences"/>) nor part of the document (that is <see cref="LibraryV3Document"/>). Created
/// ONCE by <see cref="LibraryV3Sidebar"/> in a <c>UseMemo</c> and handed to each chrome component as a ctor field: a
/// reference-stable object is a legal frozen prop, and every mutable thing inside it is either a
/// <see cref="Signal{T}"/> or a field the mode root refreshes each render (the landed <c>WaveeSidebar._acts</c> pattern).
///
/// <para>R3.0.3 — what LEFT this class when V3 moved onto the ONE pane renderer: the shared index map and bound item source
/// (the pane plans its own rows), and the live grid-cell edge (the pane's grid strip derives the cell from the real pane
/// inset). What ARRIVED: <see cref="View"/>, the pure content order the pane's planner input is shaped from, and
/// <see cref="ActivateFolder"/>, the one place a folder row's activation is decided.</para>
///
/// <para>EVERYTHING HERE IS EPHEMERAL (§3.2.17): the drill-in stack and the service handles. Nothing in this class is ever
/// persisted — a relaunch opens at the library root.</para>
/// </summary>
sealed class LibraryV3Session
{
    public LibraryV3Session(Signal<Route> route, Action<string, string?> go, Signal<float> width, bool inDrawer)
    {
        Route = route; Go = go; Width = width; InDrawer = inDrawer;
    }

    // ── the mode's frozen inputs (signals + stable delegates only) ─────────────────────────────────────────────────────
    public Signal<Route> Route { get; }
    public Action<string, string?> Go { get; }
    public Signal<float> Width { get; }
    public bool InDrawer { get; }

    // ── services, refreshed by the mode root each render (never captured at mount) ─────────────────────────────────────
    public SidebarPreferences? Prefs;
    public LibraryBridge? Library;
    /// <summary>The ambient action bag — the ONE create path (<c>PlaylistCreateFlow</c>) and the folder verbs read it.</summary>
    public ActionServices? Acts;

    /// <summary>The built content ORDER (re-grouped / drilled). Owned here because both the mode root (which builds it while
    /// shaping the planner input) and the chrome (which asks whether it is empty) need the same instance.</summary>
    public LibraryV3View View { get; } = new();

    /// <summary>The grid column count derived from the pane width, published by the mode root as a MEMO (equality-gated, so
    /// a seam drag writes nothing until the count actually changes). Read through <see cref="ReadState"/>, which is what
    /// makes a column change re-plan the pane and re-skin its strips.</summary>
    public IReadSignal<int>? Columns;

    /// <summary>Revision 2's folder mode as a SIGNAL: at or above <see cref="LibraryV3Metrics.DrillInWidth"/> folders
    /// disclose INLINE; below it — and always in the overlay drawer — they NAVIGATE (session-only drill-in with a
    /// breadcrumb). Written by the mode root from a quantized memo, so a seam drag costs one write at the threshold rather
    /// than one per frame.</summary>
    public Signal<bool> NarrowFolders { get; } = new(false);

    /// <summary>
    /// THE V3 VIEW STATE, in one value — the input the ephemeral document and the chrome are both a function of.
    ///
    /// <para>Reading it SUBSCRIBES the calling computation to every signal it touches: the five persisted view signals, the
    /// session-only search text, the projection version (which carries the pin band's length and whether the qualifier chips
    /// are evidenced), the drill version and the derived column count. That is deliberate and load-bearing in three places —
    /// the pane's document provider, the pane's mode epoch (and therefore every realized row's epoch) and the chrome — so
    /// none of them can render a state the others do not agree with.</para>
    ///
    /// <para>Allocation-free: a record struct, and the search test scans the live string instead of normalizing a copy (this
    /// runs once per ROW per epoch check).</para>
    /// </summary>
    public LibraryV3DocState ReadState()
    {
        int columns = Columns is { } c ? c.Value : 2;
        if (Prefs is not { } prefs) return new LibraryV3DocState(GridColumns: columns);

        int filter = LibraryV3Metrics.NormalizeFilter(prefs.V3Filter.Value);
        int qualifier = LibraryV3Metrics.NormalizeQualifier(prefs.V3Qualifier.Value);
        int sort = LibraryV3Metrics.NormalizeSort(prefs.V3Sort.Value);
        bool desc = prefs.V3Desc.Value;
        int view = LibraryV3Metrics.NormalizeView(prefs.V3View.Value);
        bool searching = LibraryV3Metrics.HasQuery(prefs.V3Search.Value);
        _ = prefs.Entries.Version.Value;          // subscribe: PinCount + QualifiersAvailable move with the projection
        _ = DrillVersion.Value;                   // subscribe: a push/pop re-slices the view

        return new LibraryV3DocState(
            filter, qualifier, sort, desc, view, columns, searching,
            DrillActive ? CurrentFolderId : null,
            prefs.Entries.PinCount > 0,
            prefs.IsPinned(LibraryV3Document.LikedRouteKey),
            prefs.Entries.QualifiersAvailable);
    }

    // ── drill-in navigation (Revision 2's folder amendment) ───────────────────────────────────────────────────────────
    // A folder STACK, not a single id: nesting is genuine, so drilling three levels deep and walking back out has to
    // remember the path. Ids AND display names are kept, because the breadcrumb names the level you would return to and a
    // folder that vanished from the library mid-session must still render a sane label.
    readonly List<string> _folderIds = new(4);
    readonly List<string> _folderNames = new(4);

    /// <summary>Bumped by every push/pop/reset. The mode epoch folds it, so a drill is exactly as cheap as a projection
    /// change: one view rebuild, one re-plan, no remount.</summary>
    public Signal<int> DrillVersion { get; } = new(0);

    public int DrillDepth => _folderIds.Count;
    public bool DrillActive => _folderIds.Count > 0;

    /// <summary>The folder whose CHILDREN are currently listed ("" at the library root).</summary>
    public string CurrentFolderId => _folderIds.Count == 0 ? "" : _folderIds[_folderIds.Count - 1];

    /// <summary>The current level's title — the folder name, or "Your Library" at the root.</summary>
    public string CurrentFolderName => _folderNames.Count == 0
        ? Loc.Get(Strings.Sidebar.V3.Title)
        : _folderNames[_folderNames.Count - 1];

    /// <summary>The label of the level BACK would return to (the breadcrumb's back target). Resolved at render time, so it
    /// always reads in the live culture.</summary>
    public string ParentName => _folderNames.Count <= 1
        ? Loc.Get(Strings.Sidebar.V3.Title)
        : _folderNames[_folderNames.Count - 2];

    /// <summary>Enter a folder level.
    ///
    /// <para>It ALSO expands the folder in the shared expansion state, and that is load-bearing rather than tidy: the
    /// published projection omits a COLLAPSED folder's children entirely (the binder projects with
    /// <c>isFolderExpanded</c>), so a drill level whose folder was collapsed would be empty. Expanding it is invisible
    /// while drilled (the level shows only that folder's children) and becomes continuity when the pane widens back to
    /// inline disclosure — you come out standing in the folder you were inside.</para></summary>
    public void PushFolder(string? folderId, string? name)
    {
        if (string.IsNullOrEmpty(folderId)) return;
        Prefs?.SetFolderExpanded(folderId, true);
        _folderIds.Add(folderId);
        _folderNames.Add(name is { Length: > 0 } ? name : Loc.Get(Strings.Sidebar.V3.Kind.Folder));
        Bump();
    }

    public void PopFolder()
    {
        if (_folderIds.Count == 0) return;
        _folderIds.RemoveAt(_folderIds.Count - 1);
        _folderNames.RemoveAt(_folderNames.Count - 1);
        Bump();
    }

    /// <summary>Leave drill-in entirely — called when the pane widens past the inline threshold (inline disclosure and a
    /// drill stack are two answers to the same question, so only one may be live), when the search box gets a query (search
    /// FLATTENS the tree, so there is no folder to be inside of) and when the kind filter changes (the folder you were
    /// inside may not even be part of the new kind set).</summary>
    public void ResetDrill()
    {
        if (_folderIds.Count == 0) return;
        _folderIds.Clear();
        _folderNames.Clear();
        Bump();
    }

    void Bump() => DrillVersion.Value = DrillVersion.Peek() + 1;

    /// <summary>WHAT A FOLDER ROW DOES — the single decision point, handed to the pane through
    /// <c>SidebarPaneConfig.ActivateFolder</c> so the renderer itself never learns about drill levels (§3.2.7 + Revision 2):
    /// <list type="bullet">
    /// <item>a GRID view cannot express disclosure, so the affordance stays LIVE instead of dead: switch to the list view
    /// and open that folder;</item>
    /// <item>a NARROW pane (or the drawer) NAVIGATES into the folder — the drill level;</item>
    /// <item>a WIDE pane toggles inline disclosure, which is the shared expansion state Classic and Curated use too.</item>
    /// </list></summary>
    public void ActivateFolder(string folderId, string name)
    {
        if (Prefs is not { } prefs || folderId.Length == 0) return;

        if (LibraryV3Metrics.IsGrid(LibraryV3Metrics.NormalizeView(prefs.V3View.Peek())))
        {
            prefs.SetV3View((int)SidebarV3View.List);
            prefs.SetFolderExpanded(folderId, true);
            return;
        }

        if (NarrowFolders.Peek()) PushFolder(folderId, name);
        else prefs.ToggleFolder(folderId);
    }

    /// <summary>Live renderer seam: only wide list view discloses descendants in place. Grid switches view on activation;
    /// narrow list pushes a drill level, so neither should seed an inline collapse/expand animation.</summary>
    public bool DisclosesFoldersInline()
        => !NarrowFolders.Peek()
           && Prefs is { } prefs
           && !LibraryV3Metrics.IsGrid(LibraryV3Metrics.NormalizeView(prefs.V3View.Peek()));

    // ── shared commands ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Collapse to the 56-DIP rail. The mode ctor carries no <c>toggleCollapse</c> delegate (the landed
    /// <c>SidebarHost</c> shape), so the collapse preference is written directly — which is also the ONE writer the shell's
    /// <c>presentedCompact</c> mirror already watches.</summary>
    public void Collapse() => Prefs?.SetCollapsed(true);

    /// <summary>Expand the pane from the rail (the rail's "Your Library" tile).</summary>
    public void Expand() => Prefs?.SetCollapsed(false);

    /// <summary>Create-playlist — the ONE create path (<see cref="PlaylistCreateFlow"/>), so the three designs cannot
    /// drift on what "+" does, on the numbered name it gets, or on how a rejected create is reported.</summary>
    public void CreatePlaylist()
    {
        if (Acts is not { } acts) return;
        PlaylistCreateFlow.Create(acts, default, navigate: true);
    }

    /// <summary>Clear filter + qualifier + search in one gesture (the overflow menu's "Clear filters").</summary>
    public void ClearAllFilters()
    {
        if (Prefs is not { } prefs) return;
        prefs.SetV3Filter((int)SidebarV3Filter.All);
        prefs.SetV3Qualifier((int)SidebarV3Qualifier.Any);
        prefs.V3Search.SetIfChanged("");
        ResetDrill();
    }

    public bool AnyFilterActive
    {
        get
        {
            if (Prefs is not { } prefs) return false;
            return prefs.V3Filter.Peek() != (int)SidebarV3Filter.All
                || prefs.V3Qualifier.Peek() != (int)SidebarV3Qualifier.Any
                || prefs.V3Search.Peek().Length > 0;
        }
    }

    /// <summary>Re-arm the projection (the chrome's retry banner). Invalidating + syncing the binder re-runs the
    /// contributing warmers, which is the only retry the sidebar itself owns (each store's refresh policy owns the rest).</summary>
    public void Retry()
    {
        if (Prefs?.Binder is not { } binder) return;
        binder.Invalidate();
        binder.Sync();
    }
}
