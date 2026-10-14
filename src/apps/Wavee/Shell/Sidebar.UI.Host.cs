// ── Shell/Sidebar.UI.Host.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the pane as a row slot sees it: `INavRowHost` (Theming/Contracts/NavRowHost.cs), implemented explicitly over the members
// the pane already had
//
// Role: UI · Plan: technical-plan.md §6.1 (T5a)
// NAMED PARTIAL of Sidebar.UI.cs: nothing here decides or draws. Each line forwards a member another partial owns, so the
// structure's row slot (Classic's `ClassicNavRow`) reaches exactly what `PaneSlot` read when it was a nested class of this
// pane, and nothing else.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Sidebar
{
    internal sealed partial class PaneView : INavRowHost
    {
        // ── facts ──
        NavModel INavRowHost.Model => Model;
        SidebarLayoutId INavRowHost.Layout => Config.Layout;
        SidebarRowPlan INavRowHost.Plan => Plan;
        bool INavRowHost.CompactPlan => CompactPlan;
        int INavRowHost.GridColumns => GridColumns;
        string INavRowHost.SearchText => SearchText;
        Signal<float> INavRowHost.ExpandedWidth => ExpandedWidth;
        float INavRowHost.ContentWidth => ContentWidth;
        float INavRowHost.RowExtentOf(int index) => RowExtentOf(index);
        SidebarSection? INavRowHost.SectionOf(string sectionId) => SectionOf(sectionId);
        IOverlayService INavRowHost.MenuOverlay => MenuOverlay;
        bool INavRowHost.HasActions => Acts is not null;
        bool INavRowHost.TouchLast => TouchLast;
        LayoutTransition INavRowHost.RowPlacement => Placement;
        (string Title, string Glyph) INavRowHost.DestOf(string routeKey) => Shell.Dest(Shell.Parse(routeKey));

        // ── what the pane resolved once on behalf of every row ──
        int INavRowHost.SubscribeRowEpoch(int index) => SubscribeRowEpoch(index);
        string INavRowHost.SelectedRoutePeek => SelectedRoutePeek;
        bool INavRowHost.RowSelectsRoute(int index, string route) => RowSelectsRoute(index, route);
        string? INavRowHost.PillRouteOf(int index, string liveRoute) => PillRouteOf(index, liveRoute);
        (bool Playing, bool Animated) INavRowHost.RowPlayState(int index) => RowPlayState(index);
        bool INavRowHost.DisclosureOpen(string id, bool folder, bool fallback) => DisclosureOpen(id, folder, fallback);
        void INavRowHost.RegisterSelectionPill(string route, NodeHandle node, SidebarPillLane lane) => RegisterSelectionPill(route, node, lane);
        bool INavRowHost.TryBandOf(int planIndex, out PaneBand band) => TryBandOf(planIndex, out band);
        Reorderable INavRowHost.ReorderFor(string sectionId) => ReorderFor(sectionId);
        bool INavRowHost.TreeSortedNonCustom => TreeSortedNonCustom;

        // ── verbs ──
        void INavRowHost.Navigate(string routeKey, string? arg) => Navigate(routeKey, arg);
        void INavRowHost.Navigate(string routeKey, string? arg, in SidebarLibraryEntry entry) => Navigate(routeKey, arg, in entry);
        void INavRowHost.Play(string uri, bool asTrack) => Play(uri, asTrack);
        void INavRowHost.ToggleSection(string sectionId, bool collapsed) => ToggleSection(sectionId, collapsed);
        void INavRowHost.ActivateFolder(string folderId, string name, int planIndex) => ActivateFolder(folderId, name, planIndex);
        void INavRowHost.CreatePlaylist() => CreatePlaylist();
        void INavRowHost.NewPlaylistInFolder(string folderId) => NewPlaylistInFolder(folderId);

        // ── the Classic filter box (drawn by the slot) ──
        bool INavRowHost.FilterOpenFor(string sectionId) => FilterOpenFor(sectionId);
        Signal<string> INavRowHost.FilterQuery => _search;
        Signal<int> INavRowHost.FilterTicket => _filterFocus;
        void INavRowHost.CloseFilter() => CloseFilter();

        // ── the tree's multi-selection ──
        SidebarTreeSelection INavRowHost.TreeSelection => TreeSelection;
        Signal<int> INavRowHost.SelectionVersion => SelectionVersion;
        Signal<bool> INavRowHost.ChecksVisible => ChecksVisible;
        void INavRowHost.ActivateTreeRow(string entryId, KeyModifiers mods, Action? plain) => ActivateTreeRow(entryId, mods, plain);
        void INavRowHost.ToggleTreeSelection(string entryId) => ToggleTreeSelection(entryId);
        void INavRowHost.ClearTreeSelection() => ClearTreeSelection();

        // ── menus, drags, drops and flyouts (T5c moved the flyout panels; T5d-f the rest) ──
        Func<ContextMenuModel?>? INavRowHost.HeaderMenu(string sectionId) => HeaderMenu(sectionId);
        ContextMenuModel? INavRowHost.CreateMenu() => CreateMenu();
        ContextMenuModel? INavRowHost.FolderCreateMenu(string folderId) => FolderCreateMenu(folderId);
        Func<ContextMenuModel?>? INavRowHost.EntryMenu(SidebarSection section, int planIndex, in SidebarLibraryEntry entry, string rowKey, bool unavailable)
            => EntryMenu(section, planIndex, in entry, rowKey, unavailable);
        Func<ContextMenuModel?>? INavRowHost.FolderMenu(SidebarSection section, int planIndex, in SidebarLibraryEntry folder, Action activate, bool expanded, string rowKey)
            => FolderMenu(section, planIndex, in folder, activate, expanded, rowKey);
        Func<ContextMenuModel?>? INavRowHost.RouteMenu(SidebarSection section, string routeKey, int planIndex) => RouteMenu(section, routeKey, planIndex);
        Func<ContextMenuModel?>? INavRowHost.MissingFolderMenu(in SidebarLibraryEntry folder) => MissingFolderMenu(in folder);
        Action? INavRowHost.RenameAction(in SidebarLibraryEntry entry) => RenameAction(in entry);
        Action<int>? INavRowHost.TreeMoveAction(in SidebarLibraryEntry entry) => TreeMoveAction(in entry);
        DragPayload INavRowHost.TreeDragPayload(in SidebarLibraryEntry entry) => TreeDragPayload(in entry);
        DragPayload INavRowHost.PayloadOf(in SidebarLibraryEntry entry, bool rootlistItem) => PayloadOf(in entry, rootlistItem);
        DragPayload? INavRowHost.PayloadOfRoute(string routeKey, string title) => PayloadOfRoute(routeKey, title);
        DropTargetSpec INavRowHost.ResourceDropSpec(string sectionId, int slot, string? playlistUri, string? playlistName,
            DragPayload? rootTarget, int rootPlanIndex, Action? onSpringLoad, string? railCueUri, bool isPlaylistRow, SidebarRowFacts rootFacts)
            => ResourceDropSpec(sectionId, slot, playlistUri, playlistName, rootTarget, rootPlanIndex, onSpringLoad, railCueUri, isPlaylistRow, rootFacts);
        DropTargetSpec INavRowHost.HeaderCreateDropSpec() => HeaderCreateDropSpec();
        Signal<bool> INavRowHost.HeaderCreateDropActive => HeaderCreateDropActive;
        DropTargetSpec INavRowHost.FolderCreateDropSpec(string folderId, string folderName, Signal<bool> active) => FolderCreateDropSpec(folderId, folderName, active);
        SidebarDropSlot INavRowHost.DropSlotFor(int planIndex) => DropSlotFor(planIndex);
        void INavRowHost.OpenFolderFlyout(string sectionId, in SidebarLibraryEntry folder, Func<NodeHandle> anchor) => OpenFolderFlyout(sectionId, in folder, anchor);
        void INavRowHost.OpenSectionFlyout(string sectionId, Func<NodeHandle> anchor) => OpenSectionFlyout(sectionId, anchor);
        SidebarRowPlan INavRowHost.PlanSectionExpanded(string sectionId, SidebarPlanBuffers buffers) => PlanSectionExpanded(sectionId, buffers);
        DropTargetSpec INavRowHost.RailFolderDropSpec(in SidebarLibraryEntry folder, DragPayload target) => RailFolderDropSpec(in folder, target);
        bool INavRowHost.IsRailDropActive(string key) => IsRailDropActive(key);
        void INavRowHost.ExpandFolderInPane(string folderId) => ExpandFolderInPane(folderId);
        void INavRowHost.SetTileAnchor(int index, Func<NodeHandle> anchor) => SetTileAnchor(index, anchor);

        Element INavRowHost.GridCell(SidebarSection section, in SidebarLibraryEntry entry, float edge, string selectedRoute)
            => Controls.Surface(SidebarCards.Tile(this, section, in entry, edge, selectedRoute), global::Wavee.Shape.SidebarTile);
    }
}
