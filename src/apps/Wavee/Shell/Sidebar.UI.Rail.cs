// ── Shell/Sidebar.UI.Rail.cs ───────────────────────────────────────────────────────────────────────────────────────
// the collapsed 56-DIP rail (tiles, dividers, pending stack, footer) and the rail folder flyout
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 550 lines
// Spec: ch 25 §0.9-11, §2 W5/W6, §3 (rail rows), §5, §6, §9
// NAMED PARTIAL of Sidebar.UI.cs (J1): Rail (Build + IconTile/ArtTile/Divider) and RailFolderFlyout

using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>THE ONE 56-DIP RAIL. Its content is data: the rail plan (`SidebarRowPlanner.BuildRail` — `ShowInRail`
    /// sections in document order, caps applied, dividers collapsed); this class only draws tiles, so no design's rail can
    /// drift. Not virtualized: the planner caps the strip at `RailTileCap` = 40, bounded by construction.
    /// <para>The pane MEMOIZES <see cref="Build"/> (plan version, theme, culture, rail head, route), so a cue that needed a
    /// render would never appear: every drop cue here is a BOUND prop. Selection, on the other hand, rides the memo's
    /// rebuild as a plain VALUE — a bind wires at mount only, so a selection captured inside a thunk would go stale on the
    /// reused tile node (ch 25 §9 traps). That is why a drop cue lives on its own always-mounted overlay node.</para></summary>
    internal static class Rail
    {
        /// <summary>The rail tile box (40×40 inside the 56-DIP strip).</summary>
        public const float Box = 40f;

        /// <summary>The art edge inside an <see cref="ArtTile"/> (the 2-DIP accent ring needs the 2-DIP inset).</summary>
        public const float ArtEdge = 36f;

        const int PendingTiles = 4;

        public static Element Build(PaneView owner, SidebarRowPlan plan)
        {
            // PEEKED: the route is in the pane's memo dep key; a subscription here would re-render the pane per navigation.
            string sel = owner.SelectedRoutePeek;
            var rows = plan.Rows;
            var kids = new List<Element>(rows.Count + 6);

            // A mode whose nav band is chrome, not a section (Library V3), hands the rail its own tiles, ahead of the plan.
            if (owner.Config.RailHead?.Invoke() is { } head)
            {
                kids.Add(head);
                kids.Add(Divider());
            }

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Kind == SidebarRowKind.Divider) { kids.Add(Divider()); continue; }
                if (owner.SectionOf(row.SectionId) is not { } section) continue;
                if (Tile(owner, section, in row, plan.Entries, sel) is { } tile) kids.Add(tile);
            }

            // Pending and nothing resolved ⇒ the rail shimmers instead of looking like an empty pane.
            if (kids.Count == 0 && Binder is null) kids.Add(Skeletons.RailStack(PendingTiles));

            // The mode's own rail affordance (Classic's "+", V3's Expand + "+"): authored chrome a plan cannot express.
            if (owner.Config.RailFooter?.Invoke() is { } footer)
            {
                kids.Add(Divider());
                kids.Add(footer);
            }

            // The quick layout menu, so a collapsed pane can still switch designs (V3 embeds its rows in its overflow).
            if (owner.Config.RailLayoutMenu)
            {
                kids.Add(Divider());
                kids.Add(LayoutMenu.Button(Box));
            }

            return new BoxEl
            {
                // Grow fills the strip's WIDTH so AlignItems=Center centres the 40-DIP tiles (the body wrapper is a ROW).
                Grow = 1f,
                Direction = 1, Gap = 6f, Padding = new Edges4(0f, 8f, 0f, 12f), AlignItems = FlexAlign.Center,
                Children = [.. kids],
            };
        }

        static Element? Tile(PaneView owner, SidebarSectionSpec section, in SidebarRow row,
                             IReadOnlyList<SidebarLibraryEntry> entries, string sel)
        {
            string sectionId = row.SectionId;
            switch (row.Kind)
            {
                case SidebarRowKind.EntityRow when (uint)row.EntryIndex < (uint)entries.Count:
                    return EntryTile(owner, sectionId, entries[row.EntryIndex], sel);
                case SidebarRowKind.FolderHeader when (uint)row.EntryIndex < (uint)entries.Count:
                    return FolderTile(owner, sectionId, entries[row.EntryIndex]);
                // A hand-placed route shortcut / link, or a whole feed-shaped section as one tile.
                case SidebarRowKind.IconRow:
                    return section.Kind is SidebarSectionKind.CollectionShortcuts or SidebarSectionKind.StaticLinks
                        ? RouteTile(owner, section, row.Key, sel)
                        : SectionTile(owner, section, sectionId);
                default:
                    return null;
            }
        }

        /// <summary>A projected entry: an app route is a glyph tile; everything else is its cover (circular for an
        /// artist). A TRACK plays (it has no route); everything else navigates. Only an EDITABLE playlist is a deposit
        /// target at 56 DIP (and it reuses the expanded row's spec, so the two cannot diverge); every other cover stays pure
        /// navigation, and the drop machinery keeps a drag crossing it transparent.</summary>
        static Element EntryTile(PaneView owner, string sectionId, SidebarLibraryEntry entry, string sel)
        {
            string key = PaneView.RailTileKey(in entry);
            if (entry.Kind == SidebarEntryKind.AppRoute)
            {
                string routeKey = entry.Id;
                var dest = Shell.Dest(Shell.Parse(routeKey));
                return IconTile(key, dest.Glyph, string.Equals(routeKey, sel, StringComparison.Ordinal),
                    () => owner.Navigate(routeKey, null), dest.Title);
            }

            string label = entry.Name.Length > 0 ? entry.Name : PaneText.ShortUri(entry.Uri);
            var art = Cover.Art(entry.Cover, entry.MosaicTiles, entry.Id, ArtEdge,
                circular: entry.Circular || entry.Kind == SidebarEntryKind.Artist);
            string? route = entry.RouteKey;
            Action? click = null;
            if (entry.IsTrack) click = () => owner.PlayTrack(entry.Uri);
            else if (route is { Length: > 0 } r) click = () => owner.Navigate(r, entry.Name, in entry);
            bool selected = route is { Length: > 0 } && string.Equals(route, sel, StringComparison.Ordinal);

            if (entry.Kind != SidebarEntryKind.Playlist || !entry.CanEdit)
                return ArtTile(key, art, selected, click, label);

            string uri = entry.Uri;
            var drop = owner.ResourceDropSpec(sectionId, slot: -1, uri, entry.Name, railCueUri: uri);
            return ArtTile(key, art, selected, click, label, drop, () => owner.IsRailDropActive(uri));
        }

        /// <summary>A folder cannot disclose in a 56-DIP strip, so its tile opens the side flyout (the pane's
        /// <c>OpenRailFolderFlyout</c>, anchored through the node this tile registers). It is also a real destination —
        /// Into, and only Into — and right-click is the FULL folder menu, whose Expand verb is the pane-expanding gesture
        /// the click used to be. The tooltip carries name AND count: the strip has room for neither.</summary>
        static Element FolderTile(PaneView owner, string sectionId, SidebarLibraryEntry folder)
        {
            string key = PaneView.RailTileKey(in folder);
            string cueKey = folder.Id;
            DropTargetSpec? drop = folder.FolderId.Length > 0
                ? owner.RailFolderDropSpec(in folder, PaneView.PayloadOf(in folder, rootlistItem: true))
                : null;
            string name = folder.Name.Length > 0 ? folder.Name : PaneText.ShortUri(folder.Id);
            string tip = name + " · " + Strings.Sidebar.V3.ItemCount(folder.ChildCount);
            return IconTile(key, Icons.Folder, false,
                () => owner.OpenRailFolderFlyout(sectionId, in folder), tip,
                drop, drop is null ? null : () => owner.IsRailDropActive(cueKey),
                onRealized: h => owner.RegisterRailNode(key, h),
                menuOverlay: owner.MenuOverlay, menu: owner.RailTileMenu(sectionId, in folder));
        }

        static Element? RouteTile(PaneView owner, SidebarSectionSpec section, string key, string sel)
        {
            var item = PaneText.ItemOf(section, key);
            // An ACTION or TRACK shortcut is omitted: a text-less strip cannot say what it would do (the pane owns it).
            if (item is { Target: SidebarItemTarget.Action or SidebarItemTarget.Track }) return null;
            var dest = Shell.Dest(Shell.Parse(key));
            string label = item?.LabelOverride is { Length: > 0 } alias ? alias : dest.Title;
            return IconTile("rail:" + key, PaneText.Glyph(item, dest.Glyph),
                string.Equals(key, sel, StringComparison.Ordinal), () => owner.Navigate(key, null), label);
        }

        /// <summary>A whole SECTION as one tile. Concerts navigates to its hub; every other feed-shaped section (an
        /// extension contribution) EXPANDS the pane — a strip cannot express a list, and an unresolved contribution must
        /// not be able to fill the rail with prompts.</summary>
        static Element SectionTile(PaneView owner, SidebarSectionSpec section, string sectionId)
        {
            bool concerts = section.Kind == SidebarSectionKind.Concerts;
            Action click = concerts ? () => owner.Navigate("concerts", null) : static () => SetCollapsed(false);
            return IconTile("rail:" + sectionId, concerts ? Icons.Calendar : Icons.Grid, false, click,
                PaneText.TitleOf(section));
        }

        /// <summary>A GLYPH tile (a shortcut, an app route, a folder, a V3 nav tile) with the selection-aware ramp: a
        /// selected tile darkens on hover instead of flattening. <paramref name="drop"/> makes it a destination; its cue
        /// <paramref name="dropActive"/> is an accent@0.35 wash on an always-mounted, hit-transparent underlay (BOUND).
        /// <paramref name="onRealized"/> hands the node to a caller that outlives the memoized rebuild (a folder tile's
        /// flyout anchor). <paramref name="menuOverlay"/>/<paramref name="menu"/> are a PAIR (a half-wired call attaches
        /// nothing); the factory runs at OPEN time, and ContextMenu CHAINS onto <c>OnRealized</c>.</summary>
        public static Element IconTile(string key, string glyph, bool selected, Action? onClick, string? tooltip = null,
                                       DropTargetSpec? drop = null, Func<bool>? dropActive = null,
                                       Action<NodeHandle>? onRealized = null,
                                       IOverlayService? menuOverlay = null, Func<ContextMenuModel?>? menu = null)
        {
            Element mark = Ui.Icon(glyph, 16f, selected ? Tok.TextPrimary : Tok.TextSecondary);
            bool armed = drop is not null && dropActive is not null;
            var tile = new BoxEl
            {
                OnRealized = onRealized,
                Key = key,
                Width = Box, Height = Box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(6f),
                // The selection ladder: accent plate at rest, states only ever go UP.
                Fill = selected ? global::Wavee.Design.Colors.SelectedRest : ColorF.Transparent,
                HoverFill = selected ? global::Wavee.Design.Colors.SelectedHover : Tok.FillSubtleSecondary,
                PressedFill = selected ? global::Wavee.Design.Colors.SelectedPressed : Tok.FillSubtleTertiary,
                Role = onClick is null ? AutomationRole.None : AutomationRole.Button,
                OnClick = onClick,
                DropTarget = drop,
                ZStack = armed,
                Children = armed ? new Element[] { Wash(6f, dropActive!, ring: false), mark } : new Element[] { mark },
            };
            if (menuOverlay is not null && menu is not null) tile = tile.WithContextMenu(menuOverlay, menu);
            return Tip(tile, tooltip);
        }

        /// <summary>An ART tile (a cover at <see cref="ArtEdge"/>); selection is the 2-DIP accent ring (the pill is
        /// expanded-only). An armed drop borrows the same ring plus an accent@0.35 wash over the cover, both on a BOUND
        /// overlay painted exactly over the tile's own ring, so a tile that is selected AND armed still shows one ring.
        /// The tooltip is the tile's only label and a drag never shows it — the chip caption names the target.</summary>
        public static Element ArtTile(string key, Element art, bool selected, Action? onClick, string? tooltip = null,
                                      DropTargetSpec? drop = null, Func<bool>? dropActive = null)
        {
            bool armed = drop is not null && dropActive is not null;
            var tile = new BoxEl
            {
                Key = key,
                Width = Box, Height = Box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(8f),
                BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent,
                BorderWidth = selected || armed ? 2f : 0f,
                Role = onClick is null ? AutomationRole.None : AutomationRole.Button,
                OnClick = onClick,
                DropTarget = armed ? drop : null,
                ZStack = armed,
                Children = armed ? new Element[] { art, Wash(8f, dropActive!, ring: true) } : new Element[] { art },
            };
            return Tip(tile.Interactive(Interaction.Subtle), tooltip);
        }

        /// <summary>The short centred rule between rail bands (24×1, <c>Tok.TextTertiary</c> @ A 0.30, margin 4/4).</summary>
        public static Element Divider() => new BoxEl
        {
            Width = 24f, Height = 1f, Margin = new Edges4(0f, 4f, 0f, 4f), Fill = Tok.TextTertiary with { A = 0.3f },
        };

        /// <summary>One pending rail tile (40×40 r8 <c>FillSubtleSecondary</c>) — the unit of the pending stack.</summary>
        public static Element Skeleton() => new BoxEl
        {
            Width = Box, Height = Box, Corners = CornerRadius4.All(8f), Fill = Tok.FillSubtleSecondary,
        };

        // The drop cue overlay: always mounted (a structural change would need a render), hit-transparent (it can never
        // steal the tile's own drop hit), transparent at rest, and its only live inputs are the two bound thunks.
        static BoxEl Wash(float radius, Func<bool> active, bool ring) => new()
        {
            Width = Box, Height = Box, Corners = CornerRadius4.All(radius), HitTestVisible = false,
            Fill = Prop.Of(() => active() ? Tok.AccentDefault with { A = 0.35f } : ColorF.Transparent),
            BorderWidth = ring ? 2f : 0f,
            BorderColor = ring ? Prop.Of(() => active() ? Tok.AccentDefault : ColorF.Transparent)
                               : (Prop<ColorF>)ColorF.Transparent,
        };

        // The tooltip IS the tile's label (a 56-DIP strip has no room for text).
        static Element Tip(BoxEl tile, string? tooltip)
            => tooltip is { Length: > 0 } t ? ToolTip.Wrap(tile, t) : tile;
    }

    /// <summary>The rail folder flyout's content (W6): ONE panel, one page at a time, a <c>Key</c>ed body carrying
    /// <c>MotionRecipes.PageSlideForward/Back</c> and a back chevron above level 1 — the concert date flyout's model, with
    /// the unbounded stack rules in the pure <see cref="SidebarFolderFlyoutNav"/>. Mounted fresh by
    /// <c>PaneView.OpenRailFolderFlyout</c> on every open, so the stack always starts at the clicked folder.
    /// <para>LIVE, not a snapshot: <c>Render</c> re-reads <c>Binder.CurrentInput.PlaylistTree</c> every pass and subscribes
    /// to <c>Entries.Version</c> + <c>FolderVersion</c>, so a push indexes the current tree and a playlist created, renamed,
    /// moved or deleted while the panel is open shows up in it. The props below are reference-stable mount seeds.</para>
    /// <para>Rows are <see cref="EntityRow"/> specs with the pane's own menus and drop specs, so a row here and a row in
    /// the expanded pane cannot look or behave differently.</para></summary>
    internal sealed class RailFolderFlyout : Component
    {
        public required PaneView Owner;
        /// <summary>The section the tile came from: the menus' section and the drop spec's reorder identity (ignored at
        /// <c>slot &lt; 0</c>, which every row here passes).</summary>
        public required string SectionId;
        public required string RootFolderId;
        public required string RootFolderName;
        public required Action Close;

        const float PanelW = 300f, MaxListH = 420f, HeaderH = 40f;
        const string BackKey = "sidebar.rail.folderFlyoutBack";
        const string FolderKey = "sidebar.v3.kind.folder";
        const string EmptyKey = "sidebar.section.empty";   // the pane's own empty copy — no second wording

        SidebarFolderFlyoutNav? _nav;
        readonly Signal<int> _epoch = new(0);      // bumped on push/pop — the one structural re-render
        readonly Signal<int> _cursor = new(-1);    // the keyboard cursor; -1 = nothing highlighted
        bool _forward = true;                      // last drill direction → which page-slide recipe
        readonly List<SidebarLibraryEntry> _children = new(16);

        public override Element Render()
        {
            var nav = _nav ??= new SidebarFolderFlyoutNav(RootFolderId, RootFolderName);
            _ = _epoch.Value;
            _ = (Binder?.Entries ?? Entries).Version.Value + FolderVersion.Value;   // THE live subscription: the binder's cell, which is the one that publishes (read, never peeked)
            var tree = Binder?.CurrentInput.PlaylistTree;

            int count = SidebarFolderTree.Children(tree, nav.Current.FolderId, _children);
            int cursor = count == 0 ? -1 : Math.Clamp(_cursor.Value, -1, count - 1);

            var body = new BoxEl
            {
                // KEYED per level: the drill-in is a page transition, not a content swap.
                Key = "rail-folder:" + nav.PageKey,
                Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
                Direction = 1, MinWidth = 0f,
                Children = [count == 0 ? EmptyHint() : ListOf(tree, cursor)],
            };

            return new BoxEl
            {
                Direction = 1, Width = PanelW, ClipToBounds = true, MinWidth = 0f,
                Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, Spacing.XS),
                // The panel is the focus stop and the ONE key handler (keys route from the focused node upward); the popup's
                // FocusTrap focuses it on open, so rows never become N tab stops.
                Focusable = true,
                OnKeyDown = OnKey,
                Children = [Header(nav, count), body],
            };
        }

        /// <summary>Name + item count, with a back chevron only above the root — absent, not disabled, at level 1.</summary>
        Element Header(SidebarFolderFlyoutNav nav, int count)
        {
            var title = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
                Children =
                [
                    Ui.BodyStrong(nav.Current.Name.Length > 0 ? nav.Current.Name : Loc.Get(FolderKey)) with
                    {
                        Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    Ui.Caption(Strings.Sidebar.V3.ItemCount(count)) with { Color = Tok.TextSecondary, MaxLines = 1 },
                ],
            };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinHeight = HeaderH, MinWidth = 0f,
                Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, Spacing.XS),
                Children = nav.CanGoBack ? new Element[] { BackButton(nav.Parent.Name), title } : new Element[] { title },
            };
        }

        Element BackButton(string parentName) => ToolTip.Wrap(new BoxEl
        {
            Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnClick = GoBack,
            Children = [Ui.Icon(Icons.ChevronLeft, 14f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle),
            parentName.Length > 0 ? parentName : Loc.Get(BackKey));

        Element ListOf(IReadOnlyList<SidebarLibraryEntry>? tree, int cursor)
        {
            string sel = Owner.SelectedRoute;   // subscribes: the selected row's ramp follows navigation
            var section = Owner.SectionOf(SectionId);
            var rows = new Element[_children.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = Row(tree, section, _children[i], sel, i == cursor);
            return new ScrollEl
            {
                ContentSized = true, MaxHeight = MaxListH,
                Content = new BoxEl { Direction = 1, Gap = 1f, MinWidth = 0f, Children = rows },
            };
        }

        Element Row(IReadOnlyList<SidebarLibraryEntry>? tree, SidebarSectionSpec? section, SidebarLibraryEntry entry,
                    string sel, bool cursored)
        {
            var owner = Owner;
            bool folder = entry.Kind == SidebarEntryKind.Folder;
            string? route = entry.RouteKey;
            // The route selection and the keyboard cursor share the row's one highlight: both mean "the row in play".
            bool selected = cursored || (route is { Length: > 0 } && string.Equals(route, sel, StringComparison.Ordinal));

            // The SAME menus the pane builds. A folder's Expand verb still means the PANE's disclosure, not this drill-in.
            Func<ContextMenuModel?>? menu = section is null ? null
                : folder ? owner.FolderMenu(section, -1, in entry, () => owner.ExpandFolderInPane(entry.FolderId),
                                            IsFolderExpanded(entry.FolderId), entry.Id)
                : owner.EntryMenu(section, -1, in entry, null, entry.Id);

            // A rootlist member either way: a playlist drags out onto any sidebar/rootlist target, a sub-folder files
            // elsewhere. Drops are the pane's own specs: Into-only filing on a sub-folder, the whole-row track deposit on a
            // playlist (slot -1) — exactly one outcome each, so one bound boolean is the whole cue.
            var payload = PaneView.PayloadOf(in entry, rootlistItem: true);
            DropTargetSpec? drop = folder
                ? (entry.FolderId.Length > 0 ? owner.RailFolderDropSpec(in entry, payload) : null)
                : entry.Kind == SidebarEntryKind.Playlist
                    ? owner.ResourceDropSpec(SectionId, slot: -1, entry.CanEdit ? entry.Uri : null, entry.Name,
                                             railCueUri: entry.Uri, isPlaylistRow: true)
                    : null;
            string cueKey = folder ? entry.Id : entry.Uri;

            return EntityRow.Create(new RowSpec
            {
                Key = entry.Id,
                Label = entry.Name.Length > 0 ? entry.Name : PaneText.ShortUri(entry.Id),
                // A sub-folder's count is the count of the very list a drill-in shows (ParentFolderId containment), never
                // the projection's ChildCount — the second definition that once rendered a full folder as "0 items".
                Subtitle = folder
                    ? Strings.Sidebar.V3.ItemCount(SidebarFolderTree.ChildCount(tree, entry.FolderId))
                    : PaneText.SubtitleOf(in entry),
                Selected = selected,
                Density = SidebarDensity.Cozy,
                Leading = Cover.ForEntry(in entry, SidebarRowGeometry.ArtFor(SidebarDensity.Cozy)),
                // A sub-folder announces that it drills IN — the pointer twin of the → key.
                Trailing = folder ? Ui.Icon(Icons.ChevronRight, 12f, Tok.TextTertiary) with { Shrink = 0f } : null,
                OnClick = () => Activate(in entry),
                Overflow = menu is not null,
                MenuOverlay = owner.MenuOverlay,
                Menu = menu,
                Drag = payload,
                DropTarget = drop,
                DropActive = drop is null ? null : () => owner.IsRailDropActive(cueKey),
            });
        }

        static Element EmptyHint() => new BoxEl
        {
            MinHeight = 44f, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
            Children =
            [
                Ui.Body(Loc.Get(EmptyKey)) with
                {
                    Color = Tok.TextSecondary, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };

        void Activate(in SidebarLibraryEntry entry)
        {
            if (entry.Kind == SidebarEntryKind.Folder) { Drill(entry.FolderId, entry.Name); return; }
            if (entry.RouteKey is not { Length: > 0 } route) return;
            Owner.Navigate(route, entry.Name, in entry);
            Close();
        }

        void Drill(string folderId, string name)
        {
            if (_nav is not { } nav || !nav.Push(folderId, name)) return;
            _forward = true;
            _cursor.Value = -1;   // a fresh level starts un-cursored; the first ↓ lands on its first row
            _epoch.Value = _epoch.Peek() + 1;
        }

        void GoBack()
        {
            if (_nav is not { } nav || !nav.Pop()) return;
            _forward = false;
            _cursor.Value = -1;
            _epoch.Value = _epoch.Peek() + 1;
        }

        /// <summary>↑/↓ rove (wrapping) · Enter activates · → drills into a folder · ←/Backspace goes back. Escape is NOT
        /// handled: the popup's light dismiss owns it, and a second close path could drift from click-away's.</summary>
        void OnKey(KeyEventArgs e)
        {
            int count = _children.Count;
            int cursor = _cursor.Peek();
            switch (e.KeyCode)
            {
                case Keys.Down:
                    e.Handled = true;
                    if (count > 0) _cursor.Value = cursor < 0 ? 0 : (cursor + 1) % count;
                    return;
                case Keys.Up:
                    e.Handled = true;
                    if (count > 0) _cursor.Value = cursor < 0 ? count - 1 : (cursor - 1 + count) % count;
                    return;
                case Keys.Enter:
                    e.Handled = true;
                    if ((uint)cursor < (uint)count) Activate(_children[cursor]);
                    return;
                case Keys.Right:
                    e.Handled = true;
                    if ((uint)cursor < (uint)count && _children[cursor] is { Kind: SidebarEntryKind.Folder } f)
                        Drill(f.FolderId, f.Name);
                    return;
                case Keys.Left:
                case Keys.Back:
                    // Swallowed even at the root: a back gesture falling through a focus-trapped popup would navigate
                    // the app out from under a flyout the user is still reading.
                    e.Handled = true;
                    GoBack();
                    return;
            }
        }
    }
}
