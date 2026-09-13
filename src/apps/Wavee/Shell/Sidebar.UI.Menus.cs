// ── Shell/Sidebar.UI.Menus.cs ──────────────────────────────────────────────────────────────────────────────────────
// NAMED PARTIAL of Sidebar.UI.cs (J1): every row menu the pane opens, the navbar extras (Move up/down · Move to folder…
// · Select · Remove), the "+" create flyouts, the quick layout menu, the "Move to folder…" destination picker, the edit
// card "…" menu and the section options popover — composed from owner I's menu vocabulary (Platform/Actions.UI.cs)
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 700 lines (part of Sidebar.UI.cs's 7,500)
// Spec: ch 25 §6 (menus, exact rows in order; keyboard), W24 (the picker), W10 (the popover); ch 26 §6.5 (the two
//       menus that bracket the customizer)
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
            string key = Shell.NameOf(route);
            if (SidebarPinId.FromRoute(key) is not { } id) return;
            if (!pinned) { PaneView.UnpinWithToast(id); return; }
            string uri = SidebarPinId.UriOf(id);
            PaneView.PinWithToast(id, SidebarPinId.KindOf(id), uri, Shell.Dest(route).Title);
        };
    }

    // ══ THE QUICK LAYOUT MENU ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ONE route into the customizer (header button · rail button · pane background · V3's overflow
    /// submenu): ◉ Spotify Classic · ◉ LibraryV3 · ◉ Custom · ─ · Customize sidebar… · ─ · Reset width.</summary>
    internal static class LayoutMenu
    {
        public const string CustomizeRoute = "sidebar-customize";

        /// <summary>The ⧉ button — always visible, tooltip-named, its flyout built at OPEN time.</summary>
        public static Element Button(float box = 28f) => Embed.Comp(() => new LayoutMenuButton(box, box <= 24f ? 14f : 16f));

        public static IReadOnlyList<MenuFlyoutItem> Rows()
        {
            var design = Sidebar.Design.Peek();   // open time: never subscribe
            return new List<MenuFlyoutItem>(7)
            {
                MenuFlyoutItem.RadioItem(Loc.Get("sidebar.layout.classic"), design == SidebarDesign.Classic,
                    static () => SwitchDesign(SidebarDesign.Classic)),
                MenuFlyoutItem.RadioItem(Loc.Get("sidebar.layout.libraryV3"), design == SidebarDesign.LibraryV3,
                    static () => SwitchDesign(SidebarDesign.LibraryV3)),
                MenuFlyoutItem.RadioItem(Loc.Get("sidebar.layout.curated"), design == SidebarDesign.Curated,
                    static () => SwitchDesign(SidebarDesign.Curated)),
                MenuFlyoutItem.Separator,
                // The customizer edits the CURATED document, so it always switches first.
                new(Loc.Get("sidebar.layout.customize"), ActionIcons.Resolve(ActionIcons.Rename), true, OpenCustomizerRoute),
                MenuFlyoutItem.Separator,
                // Dead unless a committed seam drag pinned the width.
                new(Loc.Get("sidebar.menu.resetWidth"), default, WidthUserSet, ResetWidth),
            };
        }

        public static ContextMenuModel? Model()
            => new ContextMenuModel(Rows(), new ContextMenuHeader(null, Loc.Get("sidebar.layout.menuTitle"), null));
    }

    sealed class LayoutMenuButton(float box, float glyph) : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);

            void Toggle()
            {
                if (svc is null || svc is NullOverlayService) return;
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = LayoutMenu.Rows();
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return ToolTip.Wrap(new BoxEl
            {
                Width = box, Height = box, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnRealized = h => anchor.Value = h,
                OnClick = Toggle,
                Children = [Icon(Icons.SplitView, glyph, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle), Loc.Get("sidebar.layout.tooltip"));
        }
    }

    internal sealed partial class PaneView
    {
        /// <summary>The app's one reference-stable seam bag (owner I).</summary>
        static ActionServices? ActionServicesOrNull() => Actions.Services;

        bool HasMenus => MenuOverlay is not NullOverlayService && Acts is not null;

        // ══ ROW MENUS ══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>A projected entry's menu (playlist / album / artist / show / route / track) + the pane's extras.</summary>
        internal Func<ContextMenuModel?>? EntryMenu(SidebarSectionSpec section, int planIndex, in SidebarLibraryEntry entry,
                                                     SidebarItemSpec? item, string rowKey)
        {
            if (!HasMenus) return null;
            var snapshot = entry;
            return () => EntryModel(in snapshot, null, false, NavExtras(section, planIndex, item, rowKey));
        }

        /// <summary>A folder row's menu: Expand/Collapse · New playlist in this folder · New folder inside · ─ ·
        /// Organize ▸ · Rename folder · ─ · Delete folder.</summary>
        internal Func<ContextMenuModel?>? FolderMenu(SidebarSectionSpec section, int planIndex, in SidebarLibraryEntry folder,
                                                      Action activate, bool expanded, string rowKey)
        {
            if (!HasMenus) return null;
            var snapshot = folder;
            return () => EntryModel(in snapshot, activate, IsFolderExpanded(snapshot.FolderId),
                                    NavExtras(section, planIndex, PaneText.ItemOf(section, rowKey), rowKey));
        }

        /// <summary>A hand-placed route row: Open · Pin + the extras.</summary>
        internal Func<ContextMenuModel?>? RouteMenu(SidebarSectionSpec section, SidebarItemSpec item, int planIndex)
        {
            if (!HasMenus) return null;
            string key = item.Key;
            return () =>
            {
                var entry = SidebarLibraryEntry.ForRoute(key, Shell.Dest(Shell.Parse(key)).Title);
                return EntryModel(in entry, null, false, NavExtras(section, planIndex, item, key));
            };
        }

        /// <summary>An action shortcut / a hand-placed track: the extras alone, or null (nothing to open).</summary>
        internal Func<ContextMenuModel?>? LayoutOnlyMenu(SidebarSectionSpec section, SidebarItemSpec item, int planIndex, string key)
        {
            if (MenuOverlay is NullOverlayService || NavExtras(section, planIndex, item, key).IsEmpty) return null;
            return () => Actions.Menu.WithLayoutExtras(null, NavExtras(section, planIndex, item, key).Flat());
        }

        /// <summary>A missing entity: exactly ONE verb — Remove (absent under a locked document).</summary>
        internal Func<ContextMenuModel?>? MissingItemMenu(string sectionId, SidebarItemSpec? item)
        {
            if (Config.ReadOnly || MenuOverlay is NullOverlayService || item is not { Id.Length: > 0 } present) return null;
            string itemId = present.Id;
            return () => new ContextMenuModel(
            [
                new MenuFlyoutItem(Loc.Get("sidebar.customizer.itemRemove"), ActionIcons.Resolve(ActionIcons.Remove), true,
                    () => Dispatch(SidebarItemCommands.Remove(sectionId, itemId))),
            ]);
        }

        /// <summary>A folder pin the rootlist lost: exactly ONE verb — Unpin.</summary>
        internal Func<ContextMenuModel?>? MissingFolderMenu(in SidebarLibraryEntry folder)
        {
            if (MenuOverlay is NullOverlayService) return null;
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

        /// <summary>A rail tile: the entry menu; a FOLDER tile gets the full folder menu including Expand folder — the
        /// pane-expanding gesture the tile's click used to be.</summary>
        internal Func<ContextMenuModel?>? RailTileMenu(string sectionId, in SidebarLibraryEntry entry)
        {
            if (!HasMenus) return null;
            var snapshot = entry;
            if (snapshot.IsFolder)
                return () => EntryModel(in snapshot, () => ExpandFolderInPane(snapshot.FolderId), false, default);
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
                SidebarEntryKind.Show => ShowModel(s, in e),
                SidebarEntryKind.AppRoute => RouteModel(in e),
                SidebarEntryKind.Track => TrackModel(s, in e),
                _ => null,
            };
            return Actions.Menu.WithLayoutExtras(menu, extras.Flat());
        }

        /// <summary>[ Play · Play next · Play after · Saved ] strip over: Add to playlist ▸ · Open · ─ · Organize ▸ ·
        /// Rename · ─ · Share ▸ · ─ · Delete (owner). Liked Songs drops Save exactly as a card does.</summary>
        ContextMenuModel PlaylistModel(ActionServices s, in SidebarLibraryEntry e, IReadOnlyList<MenuFlyoutItem>? organize)
        {
            var caps = PlaylistCaps.CanView
                       | (e.CanEdit ? PlaylistCaps.CanEditItems : PlaylistCaps.None)
                       | (e.IsOwner ? PlaylistCaps.IsOwner | PlaylistCaps.CanEditMetadata | PlaylistCaps.CanAdministratePermissions : PlaylistCaps.None)
                       | (e.CanEdit && !e.IsOwner ? PlaylistCaps.IsCollaborative : PlaylistCaps.None);
            var uri = EntityUri.Parse(e.Uri);
            var ctx = new ActionContext(ActionTarget.ForPlaylist(uri, e.Name, new PlaylistHost(uri, caps, Array.Empty<int>())), s);
            bool liked = EntityUri.IsLikedCollection(e.Uri);
            AppBarCommand[] strip = liked
                ? Actions.Menu.Strip(in ctx, [ActionId.PlayContext, ActionId.PlayContextNext, ActionId.AddContextToQueue])
                : Actions.Menu.Strip(in ctx, [ActionId.PlayContext, ActionId.PlayContextNext, ActionId.AddContextToQueue, ActionId.SaveContext]);

            var rows = new List<MenuFlyoutItem>(10);
            if (Actions.Menu.Row(ActionId.AddContextToPlaylist, in ctx) is { } add) rows.Add(add);
            if (Actions.Menu.Row(ActionId.OpenItem, in ctx) is { } open) rows.Add(open);
            Actions.Menu.Group(rows, Actions.Menu.Organize(organize, MoveOutRow(in e), PinRow(in e)));
            if (e.IsOwner && Actions.Menu.Row(ActionId.RenamePlaylist, in ctx) is { } rename) rows.Add(rename);
            Actions.Menu.OpenGroup(rows);
            if (Actions.Menu.Share(in ctx) is { } share) rows.Add(share);
            if (e.IsOwner && Actions.Menu.Row(ActionId.DeletePlaylist, in ctx) is { } delete)
            {
                rows.Add(MenuFlyoutItem.Separator);
                rows.Add(delete);
            }
            string subtitle = e.OwnerName is { Length: > 0 } owner ? owner : Loc.Get("sidebar.v3.kind.playlist");
            return new ContextMenuModel(strip, rows, Actions.Menu.Header(ArtOf(in e), e.Name, subtitle));
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
                writes?.RenameFolder is not null && folderId.Length > 0, () => LibraryWrites?.RenameFolder?.Invoke(folderId, name)));
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(new MenuFlyoutItem(Loc.Get("sidebar.deleteFolder"), ActionIcons.Resolve(ActionIcons.Delete),
                writes?.DeleteFolder is not null && folderId.Length > 0,
                () => LibraryWrites?.DeleteFolder?.Invoke(folderId, name, childCount)));
            return new ContextMenuModel(rows,
                Actions.Menu.Header(null, name, Loc.Format("sidebar.v3.itemCount", ("count", childCount))));
        }

        /// <summary>Album / artist: the card menu's rows — Play · Save/Follow · (artist radio) · Open · Pin · Share ▸.</summary>
        ContextMenuModel ContainerModel(ActionServices s, ActionTarget target, in SidebarLibraryEntry e, string? subtitle)
        {
            var ctx = new ActionContext(target, s);
            var rows = new List<MenuFlyoutItem>(8);
            if (target.Kind == TargetKind.Artist)
                Actions.Menu.AddRows(rows, in ctx, [ActionId.PlayContext, ActionId.SaveContext, ActionId.GoToArtistRadio, ActionId.OpenItem]);
            else
                Actions.Menu.AddRows(rows, in ctx, [ActionId.PlayContext, ActionId.SaveContext, ActionId.OpenItem]);
            if (PinRow(in e) is { } pin) rows.Add(pin);
            if (Actions.Menu.Share(in ctx) is { } share) rows.Add(share);
            return new ContextMenuModel(rows, Actions.Menu.Header(ArtOf(in e), e.Name,
                subtitle ?? Actions.Menu.KindWord(target.Kind), circular: e.Circular || e.Kind == SidebarEntryKind.Artist));
        }

        /// <summary>A show: Play · Open · Pin · Share ▸ (explicit rows — there is no Show target kind).</summary>
        ContextMenuModel ShowModel(ActionServices s, in SidebarLibraryEntry e)
        {
            string uri = e.Uri, name = e.Name;
            string? route = e.RouteKey;
            var rows = new List<MenuFlyoutItem>(5)
            {
                new(Loc.Get("detail.play"), ActionIcons.Resolve(ActionIcons.Play), uri.Length > 0, () => Play(uri, asTrack: false)),
                new(Loc.Get("menu.open"), ActionIcons.Resolve(ActionIcons.Open), route is { Length: > 0 }, () => Navigate(route!, name)),
            };
            if (PinRow(in e) is { } pin) rows.Add(pin);
            var ctx = new ActionContext(new ActionTarget(TargetKind.None, Array.Empty<Track>(), EntityUri.Parse(uri), name, PlaylistHost.None), s);
            if (Actions.Menu.Share(in ctx) is { } share) rows.Add(share);
            return new ContextMenuModel(rows, Actions.Menu.Header(ArtOf(in e), name,
                e.Publisher is { Length: > 0 } p ? p : Loc.Get("sidebar.v3.kind.show")));
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

        /// <summary>A feed TRACK row: the track grammar as plain rows; never pinnable, never an Open row.</summary>
        ContextMenuModel? TrackModel(ActionServices s, in SidebarLibraryEntry e)
        {
            if (!EntityId.TryParse(e.Uri, out var id)) return null;
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
        /// folder…" for a row inside a ≥2 selection) · Select — into Organize ▸; Remove (authored items only, never
        /// Pinned, whose remove is Unpin) — trailing. Built at open time from the live plan index.</summary>
        Actions.Menu.Extras NavExtras(SidebarSectionSpec section, int planIndex, SidebarItemSpec? item, string key)
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
            bool removable = !Config.ReadOnly && item is { Id.Length: > 0 }
                             && section.Kind != SidebarSectionKind.Pinned && SidebarSectionKinds.AcceptsItems(section.Kind);
            var layout = SidebarNavLayout.Decide(at, count, removable);
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

            List<MenuFlyoutItem>? trailing = null;
            if (layout.Remove)
            {
                string itemId = item!.Id;
                trailing = [new MenuFlyoutItem(Loc.Get("sidebar.customizer.itemRemove"), ActionIcons.Resolve(ActionIcons.Remove),
                    true, () => Dispatch(SidebarItemCommands.Remove(sectionId, itemId)))];
            }
            return new Actions.Menu.Extras(rows.Count > 0 ? rows : null, trailing);
        }

        /// <summary>Which ROOTLIST verbs a tree row offers (structure from the full tree, legality from the marker
        /// stream) — empty for a non-rootlist row and for a row inside a reorder band (its Reorderable owns ordering).</summary>
        (SidebarTreeNavLayout Layout, string EntryId) TreeMoves(SidebarSectionSpec section, int planIndex)
        {
            if (section.Kind != SidebarSectionKind.PlaylistTree || TryBandOf(planIndex, out _)) return (default, "");
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
            if (MenuOverlay is NullOverlayService) return;
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

        /// <summary>The header "+" flyout. The slot offers the Classic/Curated header "+" only when the mode supplies a
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

        /// <summary>The folder row "+"'s plain click: a new playlist inside that folder, navigated to.</summary>
        internal void NewPlaylistInFolder(string folderId)
        {
            if (folderId.Length > 0) LibraryWrites?.CreatePlaylist?.Invoke(folderId, true);
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
                return () => LibraryWrites?.RenameFolder?.Invoke(folderId, name);
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

        // ══ THE CANVAS: CARD MENU + OPTIONS POPOVER ════════════════════════════════════════════════════════════════

        /// <summary>The card "…" / right-click menu: Move up · Move down · ─ · Hide/Show section · Duplicate section · ─ ·
        /// Remove section. The pinned Shortcuts card has none (every verb would be an UnknownSection rejection).</summary>
        internal ContextMenuModel? EditCardMenu(string sectionId)
        {
            if (Config.ReadOnly || SidebarEditPlan.IsPinnedCard(sectionId)) return null;
            var layout = Layout;
            if (layout.Find(sectionId) is not { } spec) return null;
            var at = layout.Locate(sectionId);
            int siblings = at.Parent is null ? layout.Sections.Count : at.Parent.ChildList.Count;
            string title = PaneText.TitleOf(spec);
            return new ContextMenuModel(new List<MenuFlyoutItem>(7)
            {
                new(Loc.Get("sidebar.customizer.moveUp"), default, at.Index > 0, () => MoveSectionBy(sectionId, -1)),
                new(Loc.Get("sidebar.customizer.moveDown"), default, at.Index >= 0 && at.Index < siblings - 1,
                    () => MoveSectionBy(sectionId, 1)),
                MenuFlyoutItem.Separator,
                new(Loc.Get(spec.Hidden ? "sidebar.customizer.undo.showSection" : "sidebar.customizer.undo.hideSection"),
                    default, true, () => SetSectionHidden(sectionId, !spec.Hidden)),
                new(Loc.Get("sidebar.customizer.undo.duplicateSection"), default, true,
                    () => DuplicateEditSection(sectionId, Loc.Format("sidebar.customizer.duplicateSuffix", ("name", title)))),
                MenuFlyoutItem.Separator,
                new(Loc.Get("sidebar.customizer.undo.removeSection"), new IconRef { Glyph = Icons.Delete, Font = Theme.IconFont },
                    true, () => RemoveEditSection(sectionId)),
            });
        }

        OverlayHandle? _optionsPopover;
        string? _optionsSection;

        /// <summary>The section options popover (W10): 320×520, to the RIGHT of the card, top-aligned, light dismiss +
        /// focus trap, hosting the customizer's property surface over the SAME edit session the companion page drives.
        /// The subject is what was clicked; it is cleared on close so a stale one never opens the next popover on the wrong
        /// section. A second click on the same card closes it.</summary>
        internal void OpenSectionOptions(string sectionId, Func<NodeHandle> anchor)
        {
            if (MenuOverlay is NullOverlayService || sectionId.Length == 0) return;
            if (_optionsPopover is { IsOpen: true } open)
            {
                bool same = string.Equals(_optionsSection, sectionId, StringComparison.Ordinal);
                open.Close();
                if (same) return;
            }
            ISidebarEditHost host = Edit;
            host.Select(sectionId);
            _optionsSection = sectionId;
            _optionsPopover = MenuOverlay.Open(anchor,
                () => new BoxEl
                {
                    Direction = 1, Width = 320f, Height = 520f, MinHeight = 0f, ClipToBounds = true,
                    // Keyed by the subject: the panel's rows freeze their section at mount, so a popover reopened on
                    // another card must remount the whole surface.
                    Children = [PropertyPanel(host, "sidebar.section.props") with { Key = "sec-props:" + sectionId }],
                },
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = true });
            _optionsPopover.ClosedAction = () =>
            {
                _optionsPopover = null;
                if (string.Equals(host.Selected.Peek(), sectionId, StringComparison.Ordinal)) host.Select(null);
                if (string.Equals(_optionsSection, sectionId, StringComparison.Ordinal)) _optionsSection = null;
            };
        }

        // ══ THE BOUND ACTION ROW (ch 26 W22) ═══════════════════════════════════════════════════════════════════════

        /// <summary>What an ACTION shortcut draws, resolved ONLY through the registry: label (override → descriptor →
        /// "Manage extension"), icon, enablement, the reason sentence (never absent while disabled) and the click.
        /// The default reason is assigned before any lookup, so a cold shell frame is disabled-with-a-sentence.</summary>
        internal (string Label, IconRef Icon, bool Enabled, string? Reason, Action? Click) ResolveActionRow(SidebarItemSpec item)
        {
            string label = item.LabelOverride ?? "";
            IconRef icon = default;
            bool enabled = false;
            string? reason = Loc.Get(Actions.LocKeyNotApplicable);
            Action? click = null;
            if (item.Action is not { } docBinding)
                reason = Loc.Get(Actions.LocKeyActionMissing);
            else
            {
                var binding = docBinding.ToActionBinding();
                var registry = Registry;
                if (registry is not null && registry.TryGetAction(in binding, out var descriptor))
                {
                    if (label.Length == 0) label = descriptor.Label();
                    icon = descriptor.Icon();
                }
                if (registry is not null && Acts is { } services)
                {
                    var resolution = registry.Resolve(services, in binding);
                    enabled = resolution.Available;
                    reason = resolution.ReasonLocKey is { } key ? Loc.Get(key) : null;
                    if (enabled) click = () => registry.Execute(services, in binding);
                }
            }
            if (label.Length == 0) label = Loc.Get("sidebar.extension.manage");
            return (label, icon, enabled, reason, click);
        }
    }
}
