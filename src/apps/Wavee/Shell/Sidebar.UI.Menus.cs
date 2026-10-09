// ── Shell/Sidebar.UI.Menus.cs ──────────────────────────────────────────────────────────────────────────────────────
// NAMED PARTIAL of Sidebar.UI.cs (J1): every row menu the pane opens, the navbar extras (Move up/down · Move to folder…
// · Select), the "+" create flyouts, the quick layout menu, the "Move to folder…" destination picker — composed from
// owner I's menu vocabulary (Platform/Actions.UI.cs)
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 700 lines (part of Sidebar.UI.cs's 7,500)
// Spec: ch 25 §6 (menus, exact rows in order; keyboard), W24 (the picker), W10 (the popover)
//
// EVERY MENU IS BUILT AT OPEN TIME (a `Func<ContextMenuModel?>` the row hands to `ContextMenu.Attach`): labels resolve
// then, so no row subscribes to the culture epoch, and every positional verb is decided against the LIVE plan and tree
// rather than the render that built the row. A verb that would do nothing is ABSENT, never present-and-dead
// (`RootlistTreeNav.HasDestinations` gates "Move to folder…"; `PinRowRule` gates Pin/Unpin; a verb whose `ActionId` no
// entity file has registered yet is simply not a row — `Actions.Menu.Row` returns null).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>Fill the two ActionServices seams owner J owns — the pin pair a bound "Pin to sidebar" / "Unpin from
    /// sidebar" descriptor resolves through. Called once by the composition root (idempotent).</summary>
    public static void InstallActionSeams()
    {
        var s = Actions.Services;
        s.IsPinned = static route => SidebarPinId.FromRoute(Shell.NameOf(route)) is { } id && IsPinned(id);
        s.SetPinned = static (route, pinned) =>
        {
            if (AccountKey.Length == 0) return;   // signed out: nowhere to keep a pin
            string key = Shell.NameOf(route);
            if (SidebarPinId.FromRoute(key) is not { } id) return;
            if (!pinned) { PaneView.UnpinWithToast(id); return; }
            string uri = SidebarPinId.UriOf(id);
            PaneView.PinWithToast(id, SidebarPinId.KindOf(id), uri, Shell.Dest(route).Title);
        };
    }

    /// <summary>"Classic" / "Classic · modified" (design C.1). The ONE layout name: the pane menu, the menu mapper
    /// (P4 <c>SidebarMenus</c>), the reset toast and Settings › Sidebar all read it.</summary>
    internal static string LayoutName(SidebarLayoutId layout)
    {
        string name = Loc.Get(layout == SidebarLayoutId.Library ? "sidebar.layoutName.library" : "sidebar.layoutName.classic");
        return SidebarLayoutRules.IsModified(State.Of(layout)) ? Loc.Format("sidebar.layoutName.modified", ("layout", name)) : name;
    }

    // ══ THE QUICK LAYOUT MENU ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The pane's layout menu: ◉ Classic · ◉ Library · ─ · Reset layout · ─ · Reset width. Built at open time, so
    /// the rows read the live layout and width without subscribing.</summary>
    internal static class LayoutMenu
    {
        public static IReadOnlyList<MenuFlyoutItem> Rows()
        {
            var layout = Sidebar.Layout.Peek();   // open time: never subscribe
            bool editing = Sidebar.Editing.Peek();
            return new List<MenuFlyoutItem>(5)
            {
                MenuFlyoutItem.RadioItem(LayoutName(SidebarLayoutId.Classic), layout == SidebarLayoutId.Classic,
                    static () => SwitchLayout(SidebarLayoutId.Classic)) with { Enabled = !editing },
                MenuFlyoutItem.RadioItem(LayoutName(SidebarLayoutId.Library), layout == SidebarLayoutId.Library,
                    static () => SwitchLayout(SidebarLayoutId.Library)) with { Enabled = !editing },
                MenuFlyoutItem.Separator,
                new(Loc.Get("sidebar.menu.resetLayout"), default, SidebarLayoutRules.IsModified(Sidebar.State.Of(layout)),
                    static () => Sidebar.Dispatch(new ResetLayout(Sidebar.Layout.Peek()))),
                new(Loc.Get("sidebar.menu.resetWidth"), default,
                    MathF.Abs(Width.Peek() - SidebarPaneBounds.DefaultWidth) > 0.5f, ResetWidth),
            };
        }

        public static ContextMenuModel? Model()
            => new ContextMenuModel(Rows(), new ContextMenuHeader(null, Loc.Get("sidebar.layout.menuTitle"), null));
    }

    internal sealed partial class PaneView
    {
        /// <summary>The app's one reference-stable seam bag (owner I).</summary>
        static ActionServices? ActionServicesOrNull() => Actions.Services;

        bool HasMenus => !Controls.IsNullOverlay(MenuOverlay) && Acts is not null;

        // ══ THE PANE MENU ══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The global pane menu (the footer ⋯, the seam's and the background's right-click). P2: today's layout
        /// menu minus the rail-size rows; P4: <c>SidebarMenuModel.Pane</c>.</summary>
        internal ContextMenuModel? PaneMenu() => LayoutMenu.Model();

        OverlayHandle? _paneMenu;

        /// <summary>Toggle the pane menu from the footer's ⋯: a second press closes it.</summary>
        internal void OpenPaneMenu(Func<NodeHandle> anchor)
        {
            if (Controls.IsNullOverlay(MenuOverlay)) return;
            if (_paneMenu is { IsOpen: true } open) { open.Close(); return; }
            var items = PaneMenu()?.Rows ?? [];
            _paneMenu = MenuOverlay.Open(anchor, () => MenuFlyout.Create(items, () => _paneMenu?.Close()),
                FlyoutPlacement.TopEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = true });
            _paneMenu.ClosedAction = () => _paneMenu = null;
        }

        // ══ ROW MENUS ══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>A projected entry's menu (playlist / album / artist / show / route / track) + the pane's extras.</summary>
        internal Func<ContextMenuModel?>? EntryMenu(SidebarSection section, int planIndex, in SidebarLibraryEntry entry, string rowKey)
        {
            if (!HasMenus) return null;
            var snapshot = entry;
            return () => EntryModel(in snapshot, null, false, NavExtras(section, planIndex, rowKey));
        }

        /// <summary>A folder row's menu: Expand/Collapse · New playlist in this folder · New folder inside · ─ ·
        /// Organize ▸ · Rename folder · ─ · Delete folder.</summary>
        internal Func<ContextMenuModel?>? FolderMenu(SidebarSection section, int planIndex, in SidebarLibraryEntry folder,
                                                      Action activate, bool expanded, string rowKey)
        {
            if (!HasMenus) return null;
            var snapshot = folder;
            return () => EntryModel(in snapshot, activate, IsFolderExpanded(snapshot.FolderId),
                                    NavExtras(section, planIndex, rowKey));
        }

        /// <summary>A hand-placed route row: Open · Pin + the extras.</summary>
        internal Func<ContextMenuModel?>? RouteMenu(SidebarSection section, string routeKey, int planIndex)
        {
            if (!HasMenus) return null;
            return () =>
            {
                var entry = SidebarLibraryEntry.ForRoute(routeKey, Shell.Dest(Shell.Parse(routeKey)).Title);
                return EntryModel(in entry, null, false, NavExtras(section, planIndex, routeKey));
            };
        }

        /// <summary>A folder pin the rootlist lost: exactly ONE verb — Unpin.</summary>
        internal Func<ContextMenuModel?>? MissingFolderMenu(in SidebarLibraryEntry folder)
        {
            if (Controls.IsNullOverlay(MenuOverlay)) return null;
            string id = folder.Id, name = folder.Name;
            if (PinRowRule.Decide(true, id, IsPinned(id)) != PinRowKind.Unpin) return null;
            return () => new ContextMenuModel(
            [
                new MenuFlyoutItem(Loc.Get("sidebar.pin.unpin"), ActionIcons.Resolve(ActionIcons.Unpin), true,
                    () => UnpinWithToast(id, name)),
            ]);
        }

        internal Func<ContextMenuModel?>? GridCellMenu(in SidebarLibraryEntry entry)
        {
            if (!HasMenus) return null;
            var snapshot = entry;
            return () => EntryModel(in snapshot, null, false, default);
        }

        // ── the per-kind composition (0.2.9 `Menus.SidebarEntry`) ──────────────────────────────────────────────────

        ContextMenuModel? EntryModel(in SidebarLibraryEntry e, Action? toggleFolder, bool folderExpanded, Actions.Menu.Extras extras)
        {
            var s = Acts ?? Actions.Services;
            switch (e.Kind)
            {
                case SidebarEntryKind.Playlist:
                    return Actions.Menu.WithLayoutExtras(PlaylistModel(s, in e, extras.Organize), extras.Trailing);
                case SidebarEntryKind.Folder:
                    return Actions.Menu.WithLayoutExtras(FolderModel(in e, toggleFolder, folderExpanded, extras.Organize),
                                                         extras.Trailing);
            }
            ContextMenuModel? menu = e.Kind switch
            {
                SidebarEntryKind.Album => ContainerModel(s, ActionTarget.ForAlbum(EntityUri.Parse(e.Uri), e.Name), in e,
                    e.Creator.Length > 0 ? e.Creator : null),
                SidebarEntryKind.Artist => ContainerModel(s, ActionTarget.ForArtist(EntityUri.Parse(e.Uri), e.Name), in e, null),
                SidebarEntryKind.Show => ContainerModel(s, ActionTarget.ForShow(EntityUri.Parse(e.Uri), e.Name), in e,
                    e.Publisher is { Length: > 0 } p ? p : Loc.Get("sidebar.v3.kind.show")),
                SidebarEntryKind.AppRoute => RouteModel(in e),
                SidebarEntryKind.Track => TrackModel(s, in e),
                _ => null,
            };
            return Actions.Menu.WithLayoutExtras(menu, extras.Flat());
        }

        /// <summary>The playlist card menu (<c>Menus.Container</c>) with the sidebar's own rows as extras: Organize ▸ takes
        /// the Pin slot (pin lives inside it), and the owner's Rename · Delete trail behind a separator. Liked Songs drops
        /// Save exactly as a card does.</summary>
        ContextMenuModel PlaylistModel(ActionServices s, in SidebarLibraryEntry e, IReadOnlyList<MenuFlyoutItem>? organize)
        {
            var caps = PlaylistCaps.CanView
                       | (e.CanEdit ? PlaylistCaps.CanEditItems : PlaylistCaps.None)
                       | (e.IsOwner ? PlaylistCaps.IsOwner | PlaylistCaps.CanEditMetadata | PlaylistCaps.CanAdministratePermissions : PlaylistCaps.None)
                       | (e.CanEdit && !e.IsOwner ? PlaylistCaps.IsCollaborative : PlaylistCaps.None);
            var uri = EntityUri.Parse(e.Uri);
            var target = ActionTarget.ForPlaylist(uri, e.Name, new PlaylistHost(uri, caps, Array.Empty<int>()));
            var ctx = new ActionContext(target, s);
            var entry = e;
            var tail = new List<MenuFlyoutItem>(3);
            if (e.IsOwner && Actions.Menu.Row(ActionId.RenamePlaylist, in ctx) is { } rename) tail.Add(rename);
            if (e.IsOwner && Actions.Menu.Row(ActionId.DeletePlaylist, in ctx) is { } delete)
            {
                if (tail.Count > 0) tail.Add(MenuFlyoutItem.Separator);
                tail.Add(delete);
            }
            string subtitle = e.OwnerName is { Length: > 0 } owner ? owner : Loc.Get("sidebar.v3.kind.playlist");
            return Menus.Container(in target, ArtOf(in e), subtitle, new ContainerExtras
            {
                Services = s,
                Liked = EntityUri.IsLikedCollection(e.Uri),
                Pin = () => Actions.Menu.Organize(organize, MoveOutRow(in entry), PinRow(in entry)),
                PinStartsGroup = true,
                Tail = tail,
            }) ?? new ContextMenuModel(tail, Actions.Menu.Header(ArtOf(in e), e.Name, subtitle));
        }

        /// <summary>The folder verb set — no strip (nothing to play), destructive last.</summary>
        ContextMenuModel FolderModel(in SidebarLibraryEntry e, Action? toggle, bool expanded, IReadOnlyList<MenuFlyoutItem>? organize)
        {
            string folderId = e.FolderId, name = e.Name;
            int childCount = e.ChildCount;
            var writes = LibraryWrites;
            var rows = new List<MenuFlyoutItem>(9);
            if (toggle is not null)
                rows.Add(new MenuFlyoutItem(Loc.Get(expanded ? "sidebar.item.collapseFolder" : "sidebar.item.expandFolder"),
                    new IconRef { Glyph = expanded ? Icons.ChevronUp : Icons.ChevronDown, Font = Theme.IconFont }, true, toggle));
            rows.Add(new MenuFlyoutItem(Loc.Get("sidebar.newPlaylistHere"), ActionIcons.Resolve(ActionIcons.Add),
                writes?.CreatePlaylist is not null && folderId.Length > 0, () => LibraryWrites?.CreatePlaylist?.Invoke(folderId, true)));
            rows.Add(new MenuFlyoutItem(Loc.Get("sidebar.newFolderInside"), ActionIcons.Resolve(ActionIcons.Folder),
                writes?.NewFolderWith is not null && folderId.Length > 0,
                () => LibraryWrites?.NewFolderWith?.Invoke(folderId, Array.Empty<RootlistItemRef>())));
            rows.Add(MenuFlyoutItem.Separator);
            Actions.Menu.Group(rows, Actions.Menu.Organize(organize, MoveOutRow(in e), PinRow(in e)));
            rows.Add(new MenuFlyoutItem(Loc.Get("sidebar.renameFolder"), ActionIcons.Resolve(ActionIcons.Rename),
                writes?.RenameFolder is not null && folderId.Length > 0, () => PromptRenameFolder(folderId, name)));
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(new MenuFlyoutItem(Loc.Get("sidebar.deleteFolder"), ActionIcons.Resolve(ActionIcons.Delete),
                writes?.DeleteFolder is not null && folderId.Length > 0,
                () => LibraryWrites?.DeleteFolder?.Invoke(folderId, name, childCount)));
            return new ContextMenuModel(rows,
                Actions.Menu.Header(null, name, Loc.Format("sidebar.v3.itemCount", ("count", childCount))));
        }

        /// <summary>Album / artist / show: the card menu (<c>Menus.Container</c>); the sidebar's own pin ids fill the Pin slot.</summary>
        ContextMenuModel ContainerModel(ActionServices s, ActionTarget target, in SidebarLibraryEntry e, string? subtitle)
        {
            var entry = e;
            return Menus.Container(in target, ArtOf(in e), subtitle,
                       new ContainerExtras { Services = s, Pin = () => PinRow(in entry) })
                   ?? new ContextMenuModel([], Actions.Menu.Header(ArtOf(in e), e.Name, subtitle));
        }

        /// <summary>A pinned / authored app route: Open · Pin.</summary>
        ContextMenuModel RouteModel(in SidebarLibraryEntry e)
        {
            string? route = e.RouteKey;
            string name = e.Name;
            var rows = new List<MenuFlyoutItem>(2)
            {
                new(Loc.Get("menu.open"), ActionIcons.Resolve(ActionIcons.Open), route is { Length: > 0 }, () => Navigate(route!, name)),
            };
            if (PinRow(in e) is { } pin) rows.Add(pin);
            return new ContextMenuModel(rows, Actions.Menu.Header(null, name, null));
        }

        /// <summary>A feed TRACK row: the track grammar as plain rows; never pinnable, never an Open row. An EPISODE row
        /// (the queue / now-playing feeds emit both, G-173) has no track verbs to offer — building a track target over an
        /// episode id would allocate a Track row for it — so it opens no menu until the episode grammar lands.</summary>
        ContextMenuModel? TrackModel(ActionServices s, in SidebarLibraryEntry e)
        {
            if (!EntityId.TryParse(e.Uri, out var id) || id.Kind != EntityKind.Track) return null;
            var ctx = new ActionContext(ActionTarget.ForTracks([Entities.Track(id)]), s);
            var rows = new List<MenuFlyoutItem>(10);
            Actions.Menu.AddRows(rows, in ctx, [ActionId.Play, ActionId.PlayNext, ActionId.AddToQueue, ActionId.ToggleLike,
                                                ActionId.AddToPlaylist, ActionId.GoToAlbum, ActionId.GoToArtist]);
            if (Actions.Menu.Share(in ctx) is { } share) rows.Add(share);
            return rows.Count == 0 ? null : new ContextMenuModel(rows, Actions.Menu.Header(ArtOf(in e), e.Name,
                e.FirstArtistName is { Length: > 0 } artist ? artist : e.Creator.Length > 0 ? e.Creator : null));
        }

        static string? ArtOf(in SidebarLibraryEntry e) => e.Cover.IsEmpty ? null : Controls.ArtUrl(e.Cover);

        /// <summary>Pin / Unpin (the absolute pair) or nothing, by <see cref="PinRowRule"/>.</summary>
        static MenuFlyoutItem? PinRow(in SidebarLibraryEntry e)
        {
            string? id = SidebarPinId.FromEntry(in e);
            var kind = PinRowRule.Decide(true, id, id is not null && IsPinned(id));
            if (kind == PinRowKind.None) return null;
            string pinId = id!, uri = e.Uri, name = e.Name;
            var entryKind = e.Kind;
            return Actions.Menu.Pin(kind == PinRowKind.Unpin,
                () => PinWithToast(pinId, entryKind, uri, name),
                () => UnpinWithToast(pinId, name));
        }

        /// <summary>"Move out of {parent}" — nested rows only (absent at top level); the destination re-reads the live
        /// tree at invoke time and is the folder CONTAINING the parent ("" = Your Library).</summary>
        MenuFlyoutItem? MoveOutRow(in SidebarLibraryEntry e)
        {
            if (e.ParentFolderId.Length == 0) return null;
            string entryId = e.Id;
            return Actions.Menu.MoveOutOf(MenuLabel.Clip(e.ParentFolderName), LibraryWrites?.MoveRootlist is not null, () =>
            {
                var tree = RootlistTree;
                if (!RootlistTreeNav.TryEntry(tree, entryId, out var live) || live.ParentFolderId.Length == 0) return;
                string destination = RootlistTreeNav.TryFolder(tree, live.ParentFolderId, out var parent) ? parent.ParentFolderName : "";
                CommitMove([RootlistTreeNav.RefOf(in live)], [entryId],
                    new RootlistItemRef(live.ParentFolderId, IsFolder: true), RootlistDropPlacement.After, destination);
            });
        }

        // ══ THE NAVBAR EXTRAS ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Move up · Move down (band or pin store) · the ROOTLIST Move up/down · Move to folder… (or "Move N to
        /// folder…" for a row inside a ≥2 selection) · Select. Built at open time from the live plan index.</summary>
        Actions.Menu.Extras NavExtras(SidebarSection section, int planIndex, string key)
        {
            int at = -1, count = 0;
            if (TryBandOf(planIndex, out var band) && string.Equals(band.SectionId, section.Id, StringComparison.Ordinal))
            {
                at = planIndex - band.Start;
                count = band.Count;
            }
            else if (section.Kind == SidebarSectionKind.Pinned)
            {
                string id = SidebarPinId.Canonical(key) ?? key;
                at = Pins.IndexOf(id);
                count = Pins.Count;
            }
            var layout = SidebarNavLayout.Decide(at, count, removable: false);
            var (tree, entryId) = TreeMoves(section, planIndex);
            if (layout.IsEmpty && tree.IsEmpty && entryId.Length == 0) return default;

            var rows = new List<MenuFlyoutItem>(4);
            string sectionId = section.Id;
            Actions.Menu.AddMoveRows(rows,
                layout.MoveUp ? () => MoveRowByKey(sectionId, key, -1) : null,
                layout.MoveDown ? () => MoveRowByKey(sectionId, key, 1) : null);
            // A row INSIDE a multi-selection addresses the selection: the positional verbs mean nothing for N rows.
            bool batch = entryId.Length > 0 && TreeSelection.Count >= 2 && TreeSelection.Contains(entryId);
            if (!batch)
            {
                Actions.Menu.AddMoveRows(rows,
                    tree.MoveUp ? () => MoveSibling(entryId, -1) : null,
                    tree.MoveDown ? () => MoveSibling(entryId, 1) : null);
                if (tree.MoveToFolder)
                    rows.Add(new MenuFlyoutItem(Loc.Get("menu.moveToFolder"), ActionIcons.Resolve(ActionIcons.Folder), true,
                        () => OpenFolderPicker([entryId])));
            }
            else
            {
                int n = TreeSelection.Count;
                rows.Add(new MenuFlyoutItem(Loc.Format("menu.moveManyToFolder", ("count", n)), ActionIcons.Resolve(ActionIcons.Folder),
                    true, () => OpenFolderPicker(OrderedTreeSelection())));
            }
            // SELECT — the one pointer entry into check mode (a permanent lane would cost every row 24 DIP).
            if (entryId.Length > 0 && !TreeSelection.CheckLaneVisible)
                rows.Add(new MenuFlyoutItem(Loc.Get("sidebar.select"),
                    new IconRef { Glyph = Icons.Check, Font = Theme.IconFont }, true, () => BeginTreeCheckMode(entryId)));

            return new Actions.Menu.Extras(rows.Count > 0 ? rows : null, null);
        }

        /// <summary>Which ROOTLIST verbs a tree row offers (structure from the full tree, legality from the marker
        /// stream) — empty for a non-rootlist row and for a row inside a reorder band (its Reorderable owns ordering).</summary>
        (SidebarTreeNavLayout Layout, string EntryId) TreeMoves(SidebarSection section, int planIndex)
        {
            if (!(section.Kind == SidebarSectionKind.Playlists
                  || (section.Kind == SidebarSectionKind.Library && Config.TreeSortedNonCustom?.Invoke() == false))
                || TryBandOf(planIndex, out _)) return (default, "");
            var rows = Plan.Rows;
            var entries = Plan.Entries;
            if ((uint)planIndex >= (uint)rows.Count) return (default, "");
            var row = rows[planIndex];
            if (row.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader)) return (default, "");
            if ((uint)row.EntryIndex >= (uint)entries.Count) return (default, "");
            var entry = entries[row.EntryIndex];
            if (entry.Kind is not (SidebarEntryKind.Playlist or SidebarEntryKind.Folder) || entry.Id.Length == 0) return (default, "");
            var tree = RootlistTree;
            var run = RootlistTreeNav.Siblings(tree, entry.Id);
            bool writable = LibraryWrites?.MoveRootlist is not null;
            var layout = SidebarTreeNavLayout.Decide(in run, writable && RootlistTreeNav.HasDestinations(tree, RootlistMarkers, entry.Id));
            return (writable ? layout : layout with { MoveUp = false, MoveDown = false }, entry.Id);
        }

        /// <summary>One signed step through the SIBLING run (Move up lands before the previous sibling; Move down after the
        /// next, stepping OVER a folder rather than into it) — the menu rows and Alt+↑/↓ share it.</summary>
        void MoveSibling(string entryId, int delta)
        {
            var tree = RootlistTree;
            if (!RootlistTreeNav.TryEntry(tree, entryId, out var entry)) return;
            var run = RootlistTreeNav.Siblings(tree, entryId);
            if (delta < 0 ? !run.CanMoveUp : !run.CanMoveDown) return;
            var target = delta < 0 ? run.Previous : run.Next;
            var placement = delta < 0 ? RootlistDropPlacement.Before : RootlistDropPlacement.After;
            CommitMove([RootlistTreeNav.RefOf(in entry)], [entryId], target, placement, entry.ParentFolderName);
        }

        /// <summary>The ONE commit the menu, the keyboard and the picker share with a drop: legality asked of the same
        /// authority, undo anchors captured before the move, a refusal said out loud.</summary>
        void CommitMove(IReadOnlyList<RootlistItemRef> refs, IReadOnlyList<string> ids, RootlistItemRef target,
                        RootlistDropPlacement placement, string destinationName)
        {
            var check = RootlistDropDecision.Check(RootlistMarkers, refs, target, placement);
            if (check != RootlistMoveCheck.Ok)
            {
                RefuseDrop(RootlistDropDecision.RefusalFor(check), "menu move " + check);
                return;
            }
            if (LibraryWrites?.MoveRootlist is not { } move)
            {
                RefuseDrop(SidebarDropRefusal.Unavailable, "no rootlist seam");
                return;
            }
            RootlistUndoAnchors.TryResolveMany(RootlistTree, ids, out var undo);
            move(refs, target, placement, destinationName, undo);
        }

        // ══ "MOVE TO FOLDER…" (W24) ════════════════════════════════════════════════════════════════════════════════

        /// <summary>The keyboard-accessible counterpart to tree drag: a ContentDialog (the menu that launched it is gone,
        /// so there is no anchor). The list is a SNAPSHOT taken at open; the commit re-reads the LIVE tree, so a mid-flight
        /// rootlist change resolves to nothing rather than to the wrong folder. Opens nothing when there is nowhere legal.</summary>
        internal void OpenFolderPicker(IReadOnlyList<string> entryIds)
        {
            if (Controls.IsNullOverlay(MenuOverlay)) return;
            var tree = RootlistTree;
            var selection = RootlistSelection.Normalize(tree, entryIds);
            if (selection.Count == 0) return;
            var ids = new string[selection.Count];
            for (int i = 0; i < selection.Count; i++) ids[i] = selection[i].Id;
            var destinations = new List<RootlistFolderChoice>();
            RootlistTreeNav.PickerDestinations(tree, RootlistMarkers, ids, destinations);
            if (destinations.Count == 0) return;

            string topLevel = Loc.Get("sidebar.topLevel");
            var items = new Actions.PickerItem[destinations.Count];
            for (int i = 0; i < destinations.Count; i++)
            {
                var c = destinations[i];
                items[i] = c.IsTopLevel
                    ? new Actions.PickerItem("top-level", topLevel, Glyph: Icons.List, Pinned: true)
                    : new Actions.PickerItem(c.FolderId, c.Name, Glyph: Icons.Folder, Depth: c.Depth);
            }
            // The body names WHAT is moving: one row by name, a selection by its count.
            string subject = selection.Count == 1 ? selection[0].Name : Loc.Format("sidebar.itemCount", ("count", selection.Count));
            Actions.OpenPicker(MenuOverlay, new Actions.PickerSpec(Loc.Get("sidebar.moveToFolderTitle"), items,
                picked => CommitPicked(ids, picked))
            {
                Body = Loc.Format("sidebar.moveToFolderBody", ("name", subject)),
                Placeholder = Loc.Get("sidebar.findFolder"),
                EmptyText = Loc.Get("sidebar.noFolders"),
                PanelPadding = 0f,
            });
        }

        void CommitPicked(IReadOnlyList<string> ids, Actions.PickerItem picked)
        {
            var tree = RootlistTree;
            var live = RootlistSelection.Normalize(tree, ids);
            if (live.Count == 0) return;
            var refs = RootlistSelection.Refs(live);
            if (string.Equals(picked.Key, "top-level", StringComparison.Ordinal))
            {
                // "After everything at depth 0" — the exclusive end lands it after a TRAILING folder, not inside it.
                if (RootlistTreeNav.TryTopLevelAnchor(tree, RootlistMarkers, ids, out var anchor))
                    CommitMove(refs, ids, anchor, RootlistDropPlacement.After, "");
                return;
            }
            CommitMove(refs, ids, new RootlistItemRef(picked.Key, IsFolder: true), RootlistDropPlacement.Inside, picked.Label);
        }

        // ══ THE "+" FLYOUTS, RENAME, ALT+ARROWS ════════════════════════════════════════════════════════════════════

        /// <summary>The header "+" flyout. The slot offers the Playlists header "+" only when the mode supplies a
        /// create verb; Library V3's own chrome "+" reaches this with none, so it is not gated here.</summary>
        internal ContextMenuModel? CreateMenu() => CreateRootMenu();

        /// <summary>[New playlist · New folder] at the tree root — the header "+" and Classic's rail "+". Null (no seam)
        /// makes the button fall back to its plain click rather than open an empty flyout.</summary>
        internal static ContextMenuModel? CreateRootMenu()
        {
            if (LibraryWrites is not { } writes) return null;
            return new ContextMenuModel(new List<MenuFlyoutItem>(2)
            {
                new(Loc.Get("detail.newPlaylist"), ActionIcons.Resolve(ActionIcons.Add), writes.CreatePlaylist is not null,
                    CreatePlaylistFlow),
                new(Loc.Get("sidebar.createFolder"), ActionIcons.Resolve(ActionIcons.Folder), writes.NewFolderWith is not null,
                    static () => LibraryWrites?.NewFolderWith?.Invoke(null, Array.Empty<RootlistItemRef>())),
            });
        }

        /// <summary>The folder row "+"'s plain click: a new playlist inside that folder, navigated to — or, with no create
        /// seam, the refusal sentence (G-170: this was a silent <c>?.Invoke</c>).</summary>
        internal void NewPlaylistInFolder(string folderId)
        {
            if (folderId.Length == 0) return;
            if (LibraryWrites?.CreatePlaylist is { } create) create(folderId, true);
            else RefuseWrite("new playlist in folder");
        }

        /// <summary>"Rename folder" / F2 on a folder (G-171, 0.2.9 <c>FolderActions.Rename</c>): the shared single-field
        /// prompt, seeded with the current name; the seam receives the NEW name, and only a real change
        /// (<see cref="SidebarFolderRename.Commit"/>) — the group id is untouched, so expansion and pins ride through.
        /// Re-reads the seam at commit: the dialog outlives the menu that opened it.</summary>
        internal void PromptRenameFolder(string folderId, string currentName)
        {
            if (folderId.Length == 0) return;
            if (LibraryWrites?.RenameFolder is null) { RefuseWrite("rename folder"); return; }
            Controls.Prompt(MenuOverlay, Loc.Get("sidebar.renameFolder"), Loc.Get("menu.rename"), currentName, typed =>
            {
                if (SidebarFolderRename.Commit(typed, currentName) is not { } next) return;
                if (LibraryWrites?.RenameFolder is { } rename) rename(folderId, next);
                else RefuseWrite("rename folder");
            });
        }

        /// <summary>[New playlist in this folder · New folder inside] — the folder row "+".</summary>
        internal ContextMenuModel? FolderCreateMenu(string folderId)
        {
            if (LibraryWrites is not { } writes || folderId.Length == 0) return null;
            return new ContextMenuModel(new List<MenuFlyoutItem>(2)
            {
                new(Loc.Get("sidebar.newPlaylistHere"), ActionIcons.Resolve(ActionIcons.Add), writes.CreatePlaylist is not null,
                    () => LibraryWrites?.CreatePlaylist?.Invoke(folderId, true)),
                new(Loc.Get("sidebar.newFolderInside"), ActionIcons.Resolve(ActionIcons.Folder), writes.NewFolderWith is not null,
                    () => LibraryWrites?.NewFolderWith?.Invoke(folderId, Array.Empty<RootlistItemRef>())),
            });
        }

        /// <summary>F2 — the same Rename the row menu offers (a folder through the seam; a playlist through its
        /// registered owner-only verb), or null when the row has nothing to rename.</summary>
        internal Action? RenameAction(in SidebarLibraryEntry entry)
        {
            if (entry.Kind == SidebarEntryKind.Folder)
            {
                if (entry.FolderId.Length == 0 || LibraryWrites?.RenameFolder is null) return null;
                string folderId = entry.FolderId, name = entry.Name;
                return () => PromptRenameFolder(folderId, name);
            }
            if (entry.Kind != SidebarEntryKind.Playlist || !entry.IsOwner || entry.Uri.Length == 0) return null;
            if (AppActions.Find(ActionId.RenamePlaylist) is not { } rename || Acts is not { } s) return null;
            var uri = EntityUri.Parse(entry.Uri);
            var ctx = new ActionContext(ActionTarget.ForPlaylist(uri, entry.Name,
                new PlaylistHost(uri, PlaylistCaps.CanView | PlaylistCaps.IsOwner | PlaylistCaps.CanEditMetadata, Array.Empty<int>())), s);
            if (!rename.EnabledFor(in ctx)) return null;
            return () => rename.Execute(ctx);
        }

        /// <summary>Alt+↑/↓ on a TREE row: one sibling step (a nudge — deliberately single-row even inside a selection).</summary>
        internal Action<int>? TreeMoveAction(in SidebarLibraryEntry entry)
        {
            if (entry.Kind is not (SidebarEntryKind.Playlist or SidebarEntryKind.Folder) || entry.Id.Length == 0) return null;
            string id = entry.Id;
            return delta => MoveSibling(id, delta);
        }
    }
}
