// ── Theming/Contracts/NavRowHost.cs — what a row of the navigation pane may ask of the pane (themes plan 6.1, T5a) ───────────
//
// The pane (`Sidebar.PaneView`, core) plans, publishes and sweeps; a row SLOT (the structure's, `NavRowKit.Slot`) draws one plan
// row and wires its gestures. This is the whole surface a slot reads, so a structure never reaches into the pane: it asks
// the host for the published plan, for the facts the pane resolves once on behalf of every row (the selected route, the
// playing rows, the pill target, the band a row reorders in), and for the pieces of the surfaces that are not the rows' yet --
// menus, drop specs, flyouts, the grid cell -- which T5c-f give to the structure one sub-surface at a time and the slot then
// calls directly. The heads (T5b) are the structure's now: the header's chrome and the filter box are drawn by the slot, and the
// pane keeps the state they edit (the disclosure channel, the filter's query, ticket and close).
//
// Every member is live (a method or a signal read inside a render, an epoch or a bound thunk), never a value captured at
// mount: a slot recycles across rows.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The pane, as a row slot sees it. Implemented by <c>Sidebar.PaneView</c>.</summary>
internal interface INavRowHost
{
    // ── facts ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The nav model: labels, the folder expansion set, the age badge. A slot reads facts, it never writes a store.</summary>
    NavModel Model { get; }

    /// <summary>The layout this pane renders (the mode's, frozen at mount).</summary>
    SidebarLayoutId Layout { get; }

    /// <summary>The published plan (rows and the entries they address).</summary>
    SidebarRowPlan Plan { get; }

    /// <summary>The compact (rail) flag THE PUBLISHED PLAN WAS BUILT WITH: a tile, not a row.</summary>
    bool CompactPlan { get; }

    /// <summary>The grid column count the published plan's strips were cut with.</summary>
    int GridColumns { get; }

    /// <summary>The normalized query that built the published plan.</summary>
    string SearchText { get; }

    /// <summary>The pane's measured open width (SUBSCRIBES a reader: a grid strip re-flows with the seam).</summary>
    Signal<float> ExpandedWidth { get; }

    /// <summary>A row's content width (bound-safe signal read).</summary>
    float ContentWidth { get; }

    /// <summary>The row's live laid-out height (bound-safe), 44 on any degenerate answer.</summary>
    float RowExtentOf(int index);

    /// <summary>The section a plan row belongs to, or null.</summary>
    SidebarSection? SectionOf(string sectionId);

    /// <summary>The overlay service the row menus open on.</summary>
    IOverlayService MenuOverlay { get; }

    /// <summary>Are the pane's action services present (a row menu and the header "+" need them)?</summary>
    bool HasActions { get; }

    /// <summary>The last pointer was a finger: header buttons stay visible.</summary>
    bool TouchLast { get; }

    /// <summary>Reorderable rows ride this placement transition.</summary>
    LayoutTransition RowPlacement { get; }

    /// <summary>The title and glyph of an app route key (a route row's label follows the UI culture).</summary>
    (string Title, string Glyph) DestOf(string routeKey);

    // ── what the pane resolved once on behalf of every row ───────────────────────────────────────────────────────────

    /// <summary>Subscribe the CALLING computation to ONE row's epoch (bumped by a publish, a selection sweep or a play-state
    /// sweep only for the rows that changed). Load-bearing: a bound slot is a frozen child.</summary>
    int SubscribeRowEpoch(int index);

    /// <summary>The selected route key WITHOUT subscribing (rows re-render on their own epoch).</summary>
    string SelectedRoutePeek { get; }

    /// <summary>Does row <paramref name="index"/> draw SELECTED for <paramref name="route"/>? The ONE rule the sweep also uses.</summary>
    bool RowSelectsRoute(int index, string route);

    /// <summary>The route a pill drawn on row <paramref name="index"/> registers under.</summary>
    string? PillRouteOf(int index, string liveRoute);

    /// <summary>Is row <paramref name="index"/> playing, and animated (the equalizer)?</summary>
    (bool Playing, bool Animated) RowPlayState(int index);

    /// <summary>Is the section or folder disclosure open right now (an in-flight disclosure answers for the persisted flag)?</summary>
    bool DisclosureOpen(string id, bool folder, bool fallback);

    /// <summary>Bind a realized indicator node to the route it draws for (the selection travel finds it by route).</summary>
    void RegisterSelectionPill(string route, NodeHandle node, SidebarPillLane lane);

    /// <summary>The reorder band the row sits in, when its section reorders in place.</summary>
    bool TryBandOf(int planIndex, out Sidebar.PaneBand band);

    /// <summary>The section's <c>Reorderable</c> (it owns the drag source, the keyboard lift and the position track).</summary>
    Reorderable ReorderFor(string sectionId);

    /// <summary>A non-custom sort refuses positional drops.</summary>
    bool TreeSortedNonCustom { get; }

    // ── verbs ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open a route (the nav verb).</summary>
    void Navigate(string routeKey, string? arg);

    /// <summary>Open a route for an entry (the entry's handle is the shared row the destination reads).</summary>
    void Navigate(string routeKey, string? arg, in SidebarLibraryEntry entry);

    /// <summary>Play a uri (a track row plays instead of navigating).</summary>
    void Play(string uri, bool asTrack);

    /// <summary>Collapse or expand a section through the disclosure channel.</summary>
    void ToggleSection(string sectionId, bool collapsed);

    /// <summary>A folder activation through the mode seam (inline disclosure, or the mode's own drill).</summary>
    void ActivateFolder(string folderId, string name, int planIndex);

    /// <summary>The ONE create-playlist flow every "+" shares.</summary>
    void CreatePlaylist();

    /// <summary>A new playlist in a folder (the folder row's "+").</summary>
    void NewPlaylistInFolder(string folderId);

    // ── the Classic filter box (the header hosts it; the slot draws it, T5b) ─────────────────────────────────────────

    /// <summary>Is the transient Playlists filter open under this section's header?</summary>
    bool FilterOpenFor(string sectionId);

    /// <summary>The filter's query: the pane's own search signal (the box edits it, the planner reads it).</summary>
    Signal<string> FilterQuery { get; }

    /// <summary>The focus ticket: bumped when the box opens and on a second Ctrl+F. A box that sees it move focuses its field.</summary>
    Signal<int> FilterTicket { get; }

    /// <summary>Closes the filter box and clears its query (Esc, or blur while the query is empty).</summary>
    void CloseFilter();

    // ── the tree's multi-selection ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The playlist tree's multi-selection (per pane instance).</summary>
    SidebarTreeSelection TreeSelection { get; }

    /// <summary>Bumped on every selection write: the check lane's bound reads subscribe to it.</summary>
    Signal<int> SelectionVersion { get; }

    /// <summary>Is the check lane visible?</summary>
    Signal<bool> ChecksVisible { get; }

    /// <summary>A plain click clears the selection and runs <paramref name="plain"/>; Ctrl/Shift select.</summary>
    void ActivateTreeRow(string entryId, KeyModifiers mods, Action? plain);

    /// <summary>Toggle one row in the selection (the check lane).</summary>
    void ToggleTreeSelection(string entryId);

    /// <summary>Clear the selection (Esc).</summary>
    void ClearTreeSelection();

    // ── menus, drags, drops and flyouts: the sub-surfaces T5c-f move ─────────────────────────────────────────────────

    /// <summary>A section header's ⋯ menu, or null for a kind with none.</summary>
    Func<ContextMenuModel?>? HeaderMenu(string sectionId);

    /// <summary>The Playlists header "+" menu.</summary>
    ContextMenuModel? CreateMenu();

    /// <summary>A folder row's "+" menu.</summary>
    ContextMenuModel? FolderCreateMenu(string folderId);

    /// <summary>A projected entry's menu.</summary>
    Func<ContextMenuModel?>? EntryMenu(SidebarSection section, int planIndex, in SidebarLibraryEntry entry, string rowKey, bool unavailable = false);

    /// <summary>A folder row's menu.</summary>
    Func<ContextMenuModel?>? FolderMenu(SidebarSection section, int planIndex, in SidebarLibraryEntry folder, Action activate, bool expanded, string rowKey);

    /// <summary>A hand-placed route row's menu.</summary>
    Func<ContextMenuModel?>? RouteMenu(SidebarSection section, string routeKey, int planIndex);

    /// <summary>A folder pin the rootlist lost: exactly one verb, Unpin.</summary>
    Func<ContextMenuModel?>? MissingFolderMenu(in SidebarLibraryEntry folder);

    /// <summary>F2 on a row that can really be renamed.</summary>
    Action? RenameAction(in SidebarLibraryEntry entry);

    /// <summary>Alt+Up / Alt+Down on a tree row.</summary>
    Action<int>? TreeMoveAction(in SidebarLibraryEntry entry);

    /// <summary>The drag payload of a tree row (a row inside the multi-selection lifts the whole selection).</summary>
    DragPayload TreeDragPayload(in SidebarLibraryEntry entry);

    /// <summary>A projection entry as the drag payload every destination reads.</summary>
    DragPayload PayloadOf(in SidebarLibraryEntry entry, bool rootlistItem);

    /// <summary>A hand-placed ROUTE row as a pin payload, or null when the route is not a durable destination.</summary>
    DragPayload? PayloadOfRoute(string routeKey, string title);

    /// <summary>One resource destination: a rootlist filing slot, a playlist track deposit and a pinned-band insertion at once.</summary>
    DropTargetSpec ResourceDropSpec(string sectionId, int slot, string? playlistUri, string? playlistName,
        DragPayload? rootTarget = null, int rootPlanIndex = -1, Action? onSpringLoad = null, string? railCueUri = null,
        bool isPlaylistRow = false, SidebarRowFacts rootFacts = default);

    /// <summary>The Playlists header "+" as a drop destination.</summary>
    DropTargetSpec HeaderCreateDropSpec();

    /// <summary>Is the header "+" the armed drop destination?</summary>
    Signal<bool> HeaderCreateDropActive { get; }

    /// <summary>A folder row's "+" as a drop destination.</summary>
    DropTargetSpec FolderCreateDropSpec(string folderId, string folderName, Signal<bool> active);

    /// <summary>The drop slot this row publishes (bound-safe: one signal read, two int compares).</summary>
    SidebarDropSlot DropSlotFor(int planIndex);

    /// <summary>Open a compact folder tile's flyout, anchored on the tile.</summary>
    void OpenFolderFlyout(string sectionId, in SidebarLibraryEntry folder, Func<NodeHandle> anchor);

    /// <summary>Open a collapsed section tile's flyout, anchored on the tile.</summary>
    void OpenSectionFlyout(string sectionId, Func<NodeHandle> anchor);

    /// <summary>Register a rail tile's anchor by plan index, so Enter on the focused tile opens the flyout its click opens.</summary>
    void SetTileAnchor(int index, Func<NodeHandle> anchor);

    /// <summary>One cell of a Grid-presentation section: the shared media surface at the tile shape, <paramref name="edge"/> wide.</summary>
    Element GridCell(SidebarSection section, in SidebarLibraryEntry entry, float edge, string selectedRoute);
}
