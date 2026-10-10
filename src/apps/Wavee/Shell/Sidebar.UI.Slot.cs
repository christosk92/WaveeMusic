// ── Shell/Sidebar.UI.Slot.cs ───────────────────────────────────────────────────────────────────────────────────────
// the pane's bound row slot: ONE Component per ItemsView slot, and the whole plan-row vocabulary behind it
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 1400 lines
// Spec: ch 25 §0, §1.2, §2 W1-W4 W7 W8 W16-W19, §3, §6, §9, §10; ch 26 W21, W22
// NAMED PARTIAL of Sidebar.UI.cs (J1): PaneSlot — the bound slot and its row builders (0.2.9 Pane/SidebarPaneSlot.cs)

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>ONE bound slot of the pane's plan list. <c>ItemsView.CreateBound</c> builds each slot once and recycles it
    /// by writing <c>scope.Index</c>; Render reads that signal plus THIS row's epoch (<see cref="PaneView.SubscribeRowEpoch"/>,
    /// bumped by a publish / selection sweep / play-state sweep only for the rows that changed), so a heterogeneous plan
    /// re-skins exactly the realized rows it touches — never the list.
    ///
    /// <para>NO HOOKS anywhere in this class: every builder is a plain method, so hook order is identical across every
    /// recycle. The chevrons, the pill, the "+" and the grid-tile <c>Controls.Surface</c>s are hook-owning CHILDREN, and each
    /// row kind has its own recycle pool (<c>ContentType</c> = row kind), so a header slot never rebinds into a
    /// chevron-less shape. A surface is PROPS-DRIVEN (<c>Embed.Comp(props, factory)</c> re-pushes the new <c>CardData</c>
    /// on every rebind), which is what makes it legal under a recycling slot: no constructor argument freezes at mount
    /// (the adapters are Sidebar.Cards.cs).</para>
    ///
    /// <para>Rows never read playback or the route signal themselves: the pane reads both ONCE on behalf of every row
    /// (<see cref="PaneView.RowPlayState"/>, <see cref="PaneView.SelectedRoutePeek"/>).</para></summary>
    internal sealed class PaneSlot : Component
    {
        readonly PaneView _o;
        readonly RowScope _scope;

        // Live-state probes handed to hook-owning children and bound props. They capture only THIS slot (never a section
        // id, an index or a height): child ctor args and bindings freeze at MOUNT while the slot recycles every scroll.
        // Cached so a re-render allocates no new delegate for them.
        Func<bool>? _headerOpen, _folderOpen;
        Func<int>? _sectionIdentity, _folderIdentity;
        Func<SidebarPillState>? _pillProbe;
        Func<float>? _folderPlusReveal, _treeEndWidth, _lineWidth, _lineOpacity;
        Func<ColorF>? _plateFill, _plateBorder;
        Func<Affine2D>? _lineTransform;
        SidebarPillState _pillState;
        Action<KeyEventArgs>? _slotKey;

        /// <summary>Is this slot's folder "+" the armed drop destination? One flag per SLOT (a slot draws at most one folder
        /// row); the pane's folder-create drop spec clears it on leave and on drop.</summary>
        readonly Signal<bool> _folderPlusDrop = new(false);

        /// <summary>The compact tile's own node: its flyout anchors on it. One per slot (a slot draws one tile at a time).</summary>
        NodeHandle _tileNode = NodeHandle.Null;
        Action<NodeHandle>? _tileRealize;
        Func<NodeHandle>? _tileAnchor;

        public PaneSlot(PaneView owner, RowScope scope) { _o = owner; _scope = scope; }

        public override Element Render()
        {
            int index = _scope.Index.Value;          // a recycle writes this → exactly this row re-renders
            _ = _o.SubscribeRowEpoch(index);         // THIS row's epoch only
            // PEEKED, never subscribed: the pane's selection sweep bumps the epoch of the two rows a navigation concerns.
            string sel = _o.SelectedRoutePeek;

            var rows = _o.Plan.Rows;
            // The count signal lands one layout effect after the plan: render nothing rather than clamp onto a foreign row.
            if ((uint)index >= (uint)rows.Count) return Nothing;
            var row = rows[index];
            var section = _o.SectionOf(row.SectionId);
            if (section is null) return Nothing;

            Element content = row.Kind switch
            {
                SidebarRowKind.SectionHeader => HeaderRow(section, in row, index, sel),
                // Only a Divider SECTION plans this row; ordinary section joins are whitespace, never implicit rules.
                SidebarRowKind.Divider => SectionHeader.Separator(),
                SidebarRowKind.IconRow => RouteRow(section, row.Key, sel, index),
                SidebarRowKind.EntityRow => (uint)row.EntryIndex < (uint)_o.Plan.Entries.Count
                    ? EntryRow(section, _o.Plan.Entries[row.EntryIndex], in row, sel, index) : Nothing,
                SidebarRowKind.FolderHeader => FolderRow(section, in row, index, sel),
                SidebarRowKind.GridStrip => GridStripRow(section, in row, sel),
                SidebarRowKind.Empty => EmptyRow(section),
                SidebarRowKind.Skeleton => Skeletons.Row(index, section.Shape,
                    heightOverride: SidebarRowGeometry.HeightOf(section.Shape), artOverride: SidebarRowGeometry.ArtOf(section.Shape)),
                SidebarRowKind.TreeEnd => TreeEndRow(in row, index),
                SidebarRowKind.SectionTile => SectionTileRow(section, in row, index, sel),
                SidebarRowKind.DropBand => DropBandRow(section, index),
                _ => Nothing,
            };

            // In-place reorder: the Reorderable owns the row's drag source, keyboard lift and position track — which is why
            // a banded row carries no Drag payload, no Animate, no OnRename/OnMove of its own.
            if (ReorderBand(in row, index) is { } pair)
                content = pair.Ro.Item(index - pair.Start, FillSlot(content), key: row.Key, transition: PaneView.Placement);
            return new BoxEl { Direction = 1, OnKeyDown = _slotKey ??= SlotKey, OnFocusChanged = _scope.OnFocusChanged, Children = [content] };
        }

        /// <summary>The roving stop is this slot's ROOT (ItemsView focuses it): Enter / Space reach the list's invoke through it.</summary>
        void SlotKey(KeyEventArgs e)
        {
            if (e.Handled || e.IsRepeat || (e.Mods & KeyModifiers.Alt) != 0) return;
            if (e.KeyCode == Keys.Enter) { _scope.OnInteraction(ItemContainerTrigger.EnterKey, e.Mods); e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { _scope.OnInteraction(ItemContainerTrigger.SpaceKey, e.Mods); e.Handled = true; }
        }

        static Element Nothing => new BoxEl { Height = 0f, Shrink = 0f };

        /// <summary>FILL THE SLOT. <c>Reorderable.Item</c>'s wrapper is a flex ROW, so a row without Grow arranges at its
        /// measured content width and its hover/selected plate drew narrower than its neighbours'. A BoxEl content (an
        /// entity row, the ZStack indicator) takes the fill here; a tooltip-wrapped ComponentEl already carries it through
        /// <c>ToolTip.Wrap(grow: 1f)</c> (the reconciler mirrors a component anchor's grow), so it is never applied twice.
        /// MinWidth 0 keeps a long title eliding instead of pushing the row past the pane.</summary>
        static Element FillSlot(Element content)
            => content is BoxEl box ? box with { Grow = 1f, Shrink = 1f, MinWidth = 0f } : content;

        (Reorderable Ro, int Start)? ReorderBand(in SidebarRow row, int index)
        {
            if (row.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.IconRow or SidebarRowKind.FolderHeader)) return null;
            if (!_o.TryBandOf(index, out var band)) return null;
            return (_o.ReorderFor(band.SectionId), band.Start);
        }

        // ── section chrome ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The 40-px section header (P1.4.6): the title, then the "+" (Playlists always carries it: the pane's one
        /// create flow, <see cref="PaneView.CreatePlaylist"/>), the ⋯ (<see cref="PaneView.HeaderMenu"/>, revealed on hover,
        /// permanent after a touch) and the chevron, which always toggles the section's collapse. An open Classic filter
        /// replaces the header's tail with its box. The header is the pill's section anchor (the one-pill rule's third
        /// arm), so the pill is drawn here too.</summary>
        Element HeaderRow(SidebarSection section, in SidebarRow row, int index, string sel)
        {
            string id = section.Id;
            var owner = _o;

            // Every delegate reads LIVE pane state: a header slot recycles across sections.
            Element? create = null, more = null;
            if (section.Kind == SidebarSectionKind.Playlists)
                create = Embed.Comp(() => new CreateButton(
                    owner.CreatePlaylist, menu: owner.CreateMenu, drop: owner.HeaderCreateDropSpec(),
                    dropActive: () => owner.HeaderCreateDropActive.Value,
                    box: SidebarRowGeometry.HeaderButton, glyph: SidebarRowGeometry.PlusGlyph)) with { Key = "tree-create" };
            if (_o.MenuOverlay is { } svc && _o.HeaderMenu(id) is { } menu)
                more = ToolTip.Wrap(SectionHeader.InlineButton(Icons.More, null, reveal: !_o.TouchLast)
                    .WithContextMenu(svc, menu) with { ClickRequestsContext = true }, Loc.Get(PaneLoc.SectionOptions));

            // ONE rotating glyph, never a swap; a recycle onto another section seeds its angle instead of spinning.
            Action<bool> toggle = open => owner.ToggleSection(id, !open);
            Element chevron = new BoxEl
            {
                Width = SidebarRowGeometry.HeaderButton, Height = SidebarRowGeometry.HeaderButton, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Chevron.Section(_headerOpen ??= HeaderOpenLive, identity: _sectionIdentity ??= SectionIdentity)],
            };

            Element header = SectionHeader.Header(PaneText.TitleOf(section), !section.Collapsed, toggle, create, more, chevron);
            if (_o.FilterOpenFor(id)) header = new BoxEl { Direction = 1, Children = [header, _o.FilterBox()] };

            // The header's own pill: centred on the 40-px title band (a header has no margin, so no RowMarginY).
            _pillState = new SidebarPillState(_o.PillRouteOf(index, sel), _o.RowSelectsRoute(index, sel), 0f,
                (SidebarRowGeometry.HeaderHeight - SidebarRowGeometry.PillH) * 0.5f);
            Func<SidebarPillState> probe = _pillProbe ??= PillState;
            return ZStack(header, Embed.Comp(() => new SelectionPill(owner, probe)));
        }

        /// <summary>The header chevron's live open state. Captures only the SLOT: it re-reads the plan row at the slot's
        /// current index, and its epoch read is what re-renders the chevron on a toggle.</summary>
        bool HeaderOpenLive()
        {
            int index = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(index);
            var rows = _o.Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return true;
            var section = _o.SectionOf(rows[index].SectionId);
            return section is null || _o.DisclosureOpen(section.Id, folder: false, fallback: !section.Collapsed);
        }

        /// <summary>The folder disclosure chevron's live expansion state — same recycle-safe shape.</summary>
        bool FolderOpenLive()
        {
            int index = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(index);
            var plan = _o.Plan;
            if ((uint)index >= (uint)plan.Rows.Count) return true;
            int entryIndex = plan.Rows[index].EntryIndex;
            if ((uint)entryIndex >= (uint)plan.Entries.Count) return true;
            string folderId = plan.Entries[entryIndex].FolderId;
            return _o.DisclosureOpen(folderId, folder: true, fallback: Sidebar.IsFolderExpanded(folderId));
        }

        /// <summary>The chevrons' recycle identity: the section (or folder) the slot's CURRENT row stands for. When it changes
        /// the chevron seeds its resting angle rather than animating a toggle nobody made.</summary>
        int SectionIdentity()
        {
            int index = _scope.Index.Value;
            var rows = _o.Plan.Rows;
            return (uint)index < (uint)rows.Count ? StringComparer.Ordinal.GetHashCode(rows[index].SectionId) : 0;
        }

        int FolderIdentity()
        {
            int index = _scope.Index.Value;
            var plan = _o.Plan;
            if ((uint)index >= (uint)plan.Rows.Count) return 0;
            int entryIndex = plan.Rows[index].EntryIndex;
            return (uint)entryIndex < (uint)plan.Entries.Count
                ? StringComparer.Ordinal.GetHashCode(plan.Entries[entryIndex].FolderId)
                : 0;
        }

        /// <summary>The rootlist rows: the Playlists section, and Your Library while it is the Playlists · Custom order list —
        /// the rootlist itself (§P3.12). A sorted Library is a view of it, so its rows only drop INTO folders.</summary>
        bool RootlistSection(SidebarSection section)
            => section.Kind == SidebarSectionKind.Playlists || (section.Kind == SidebarSectionKind.Library && !_o.TreeSortedNonCustom);

        // ── item / entity rows ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A projected entry: a playlist / album / artist / show / track / app route the projection knows, or a pin.</summary>
        Element EntryRow(SidebarSection section, in SidebarLibraryEntry entry, in SidebarRow row, string sel, int index)
        {
            bool named = entry.Name.Length > 0;
            bool compact = _o.CompactPlan;
            bool track = entry.IsTrack;
            string? route = entry.RouteKey;
            bool routeEntry = entry.Kind == SidebarEntryKind.AppRoute;
            // A route pin wears its destination's glyph; the Library's Liked row keeps its heart tile (Cover.ForEntry).
            bool routeGlyph = routeEntry && entry.Id != SidebarCatalogue.LikedRoute;
            // A PIN is never a blank row (design D9): while nothing has named it, the kind's noun shows in TextTertiary
            // ("Playlist" is honest where a uri fragment reads as data). An authoritative miss keeps the last-known name,
            // dimmed, with the warning mark (design Q9). A non-pin row keeps the identity gate (Trap 5) on its uri fallback.
            bool pinned = entry.IsPinned;
            var pinState = pinned
                ? SidebarPinStateRules.Of(entry.IdentityKnown || named, entry.Missing, Spotify.Current.IsOnline)
                : SidebarPinState.Resolved;
            bool unavailable = pinState == SidebarPinState.Unavailable;
            string label = named ? entry.Name
                : routeEntry ? Shell.Dest(Shell.Parse(entry.Id)).Title
                : pinned ? (SidebarPinStateRules.FallbackTitleKey(entry.Kind) is { } noun ? Loc.Get(noun) : PaneText.ShortUri(entry.Uri))
                : SidebarProjection.ShouldShowUriFallbackTitle(entry.IsPinned, entry.IdentityKnown)
                    ? PaneText.ShortUri(entry.Uri)
                    : "";
            bool placeholderTitle = pinned && !named && !routeEntry;
            // Resolved pane-side by the SAME rule the selection sweep uses, so the row that draws the plate and the row
            // whose epoch got bumped can never disagree.
            bool selected = _o.RowSelectsRoute(index, sel);
            bool reordering = _o.TryBandOf(index, out _);
            var (playing, animated) = _o.RowPlayState(index);
            var shape = section.Shape;
            // A Text section (Classic, Show covers off) is text-only: no cover and no route glyph, so a route pin is text too.
            bool textOnly = shape == SidebarRowShape.Text;
            float height = SidebarRowGeometry.HeightOf(shape);

            var snapshot = entry;   // an `in` parameter cannot be captured — copy the record struct for the closures
            Action? click = null;
            if (track) click = () => _o.Play(snapshot.Uri, asTrack: true);
            else if (route is { Length: > 0 } navRoute) click = () => _o.Navigate(navRoute, snapshot.Name, in snapshot);

            var menu = _o.EntryMenu(section, index, in snapshot, row.Key, unavailable);
            // F2: only a row that can really be renamed takes it (and so becomes a focus stop); never a banded row.
            Action? rename = reordering ? null : _o.RenameAction(in snapshot);

            bool treeRow = RootlistSection(section) && !reordering;
            // Rootlist membership is a fact about the ENTITY, not the section: a pinned or recent playlist row is the same
            // rootlist member as its tree row, and is file-able from either.
            bool rootlistItem = !reordering && snapshot.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder;
            // Alt+↑/↓ — TREE rows only: a pinned row is a rootlist member but not a rootlist ORDERING slot.
            Action<int>? move = treeRow && rootlistItem ? _o.TreeMoveAction(in snapshot) : null;
            // `resource` is THIS row's own identity (what a drop files relative to, the "Move into {name}" name, the self
            // check). The DRAG payload is the pane's: a row inside the multi-selection lifts the whole selection.
            // The Liked row is a fixed home, never a rootlist slot (§P5.6): a filing dragged over it is a pin-path refusal.
            DragPayload? resource = treeRow && entry.Id != SidebarCatalogue.LikedRoute ? PaneView.PayloadOf(in snapshot, rootlistItem: true) : null;
            DragPayload? drag = null;
            if (!reordering && !track)   // a track is never a pin source (enforced by the kind, not per surface)
                drag = treeRow ? _o.TreeDragPayload(in snapshot) : PaneView.PayloadOf(in snapshot, rootlistItem);
            // A NON-editable playlist row takes the resource spec too, so it refuses a track deposit WITH a reason; every
            // other kind keeps the pin spec and stays transparent while a drag merely crosses it.
            bool playlistRow = snapshot.Kind == SidebarEntryKind.Playlist;
            DropTargetSpec? drop = playlistRow || resource is not null
                ? _o.ResourceDropSpec(row.SectionId, PinSlot(row.SectionId, index),
                    playlistRow && snapshot.CanEdit ? snapshot.Uri : null, snapshot.Name, resource, index,
                    isPlaylistRow: playlistRow, rootFacts: TreeRowFacts(index, in snapshot))
                : PinSpec(section, row.SectionId, index);

            bool markPinned = SidebarPinRules.ShowsPinMark(_o.Config.Layout, section.Kind, row.Depth, snapshot.IsPinned, track);
            var spec = new RowSpec
            {
                Key = row.Key,
                Label = label,
                // The shape already decides the second line (a one-line shape never draws one), so the text is only built for a two-line row.
                // An unavailable pin says so on its second line (design Q9).
                Subtitle = shape == SidebarRowShape.EntityTwoLine
                    ? unavailable ? Loc.Get("sidebar.pin.unavailable") : PaneText.SubtitleOf(in snapshot)
                    : null,
                Selected = selected,
                Enabled = named || track || routeEntry,
                Depth = IndentOf(in row),
                Tile = compact,
                Shape = shape,
                Height = height,   // UNIFORM per section: a band's slot pitch and the extent table both assume one height
                ArtSize = SidebarRowGeometry.ArtOf(shape),
                Ink = placeholderTitle || unavailable ? Tok.TextTertiary : null,
                Leading = routeGlyph || textOnly ? null : Cover.ForEntry(in snapshot, SidebarRowGeometry.ArtOf(shape)),
                Glyph = routeGlyph && !textOnly ? Shell.Dest(Shell.Parse(entry.Id)).Glyph : null,
                // The warning mark is the unavailable pin's trailing mark, so it shows in every row shape.
                Trailing = unavailable ? Icon(Icons.Warning, 12f, Tok.TextTertiary) : TrailingBadge(section, in snapshot),
                Pinned = markPinned,   // #85
                OnUnpin = UnpinOf(markPinned, in snapshot),
                Playing = playing,
                PlayingAnimated = animated,
                Track = track,
                Overflow = menu is not null,
                OnClick = click,
                MenuOverlay = _o.MenuOverlay,
                Menu = menu,
                OnRename = rename,
                OnMove = move,
                Drag = drag,
                DropTarget = drop,
            };
            if (treeRow && rootlistItem) ApplyTreeSelection(ref spec, snapshot.Id, click);
            spec.LabelTooltip = LabelOverflows(label, row.Depth, RowTrailing(in spec, menu is not null, playing), LabelStartOf(in spec));
            Element built = EntityRow.Create(in spec);
            if (track) built = EntityRow.WithPlayTrackHint(built);
            // The pill stays in the row's own indent (32 per depth level), never the drop caret's gutter.
            return Indicator(Tipped(built, spec.LabelTooltip, label), selected, IndentOf(in row), height, route);
        }

        /// <summary>A rootlist FOLDER: entity-row geometry, the folder mark, and the disclosure chevron in the TRAILING
        /// cluster (W7). A folder never navigates; by default it toggles its expansion, and a mode may reroute that gesture
        /// through <c>Config.ActivateFolder</c> (V3's narrow drill-in) — click and menu verb take the same path.</summary>
        Element FolderRow(SidebarSection section, in SidebarRow row, int index, string sel)
        {
            var entries = _o.Plan.Entries;
            if ((uint)row.EntryIndex >= (uint)entries.Count) return Nothing;
            var entry = entries[row.EntryIndex];
            string folderId = entry.FolderId;
            bool expanded = Sidebar.IsFolderExpanded(folderId);
            float height = SidebarRowGeometry.HeightOf(section.Shape);

            // A synced folder pin the rootlist no longer carries renders visible-but-disabled, never vanishes.
            if (entry.Missing) return MissingFolderRow(section, in entry, in row, height);

            var snapshot = entry;
            var shape = section.Shape;
            float art = SidebarRowGeometry.ArtOf(shape);
            string label = entry.Name.Length > 0 ? entry.Name : PaneText.ShortUri(entry.Id);
            bool compact = _o.CompactPlan;
            if (compact) _o.SetTileAnchor(index, TileAnchor);   // Enter on the focused tile opens the flyout from here
            string sectionId = row.SectionId;

            // A compact folder tile opens its children as a flyout (a second click closes it); the rail never toggles.
            Action activate = () =>
            {
                if (compact) _o.OpenFolderFlyout(sectionId, in snapshot, TileAnchor);
                else _o.ActivateFolder(folderId, snapshot.Name, index);
            };
            var menu = _o.FolderMenu(section, index, in snapshot, activate, expanded, row.Key);
            bool reordering = _o.TryBandOf(index, out _);
            Action? rename = reordering ? null : _o.RenameAction(in snapshot);
            // Only a rootlist folder is a rootlist member; a folder elsewhere is a plain pin row.
            bool rootlistItem = RootlistSection(section) && !reordering;
            // Alt+↑/↓ moves the folder's whole subtree among its siblings, exactly as it moves a playlist.
            Action<int>? move = rootlistItem ? _o.TreeMoveAction(in snapshot) : null;
            var resource = PaneView.PayloadOf(in snapshot, rootlistItem);
            // Spring-load: hold a drag over a CLOSED folder and it opens. Re-checked inside the callback — the gesture
            // outlives any single render, and ActivateFolder is a TOGGLE.
            Action springLoad = () =>
            {
                if (!Sidebar.IsFolderExpanded(folderId)) _o.ActivateFolder(folderId, snapshot.Name, index);
            };
            DropTargetSpec? drop = rootlistItem
                ? _o.ResourceDropSpec(row.SectionId, -1, null, null, resource, index, springLoad,
                    rootFacts: TreeRowFacts(index, in snapshot))
                : PinSpec(section, row.SectionId, index, springLoad);

            bool folderPinned = SidebarPinRules.ShowsPinMark(_o.Config.Layout, section.Kind, row.Depth, false, false);
            var spec = new RowSpec
            {
                Key = row.Key,
                Label = label,
                // Trap 5: routed through the same PURE decision every other kind's subtitle uses
                // (`PaneText.SubtitleOf`), which gates a folder's "N items" on `entry.CountKnown` — a Pending
                // unlisted folder pin (the rootlist hasn't answered this session) shows its title alone, never a
                // confident "0 items".
                Subtitle = shape == SidebarRowShape.EntityTwoLine ? PaneText.SubtitleOf(in entry) : null,
                Depth = IndentOf(in row),
                Shape = shape,
                Height = height,
                ArtSize = art,
                // Text shape: the 16-DIP folder mark in the icon column (cover shapes keep the folder cover).
                Leading = shape == SidebarRowShape.Text ? null : Cover.Folder(art, expanded),
                Glyph = shape == SidebarRowShape.Text ? (expanded ? Icons.FolderOpen : Icons.Folder) : null,
                // A pinned folder shows the mark LEFT of its chevron (Library's depth-0 pins; §P5.6). Its IsPinned is not a
                // rootlist fact, so the folder's own state never enters the rule.
                Pinned = folderPinned,
                OnUnpin = UnpinOf(folderPinned, in snapshot),
                DisclosureChevron = Chevron.Disclosure(_folderOpen ??= FolderOpenLive, identity: _folderIdentity ??= FolderIdentity),
                Trailing = FolderTrailing(section, in snapshot, folderId, rootlistItem),
                Tile = compact,
                OnRealized = compact ? TileRealize : null,
                OnClick = activate,
                Overflow = menu is not null,
                MenuOverlay = _o.MenuOverlay,
                Menu = menu,
                OnRename = rename,
                OnMove = move,
                Drag = reordering ? null : rootlistItem ? _o.TreeDragPayload(in snapshot) : resource,
                DropTarget = drop,
            };
            if (rootlistItem) ApplyTreeSelection(ref spec, folderId.Length > 0 ? snapshot.Id : "", activate);
            spec.LabelTooltip = LabelOverflows(label, row.Depth, FolderTrail + SidebarRowGeometry.OverflowReserve(menu is not null, chevron: true), LabelStartOf(in spec));
            // A folder is a pill anchor when it is the deepest visible ancestor of the route (rule 2 of §P1.2). Both drop
            // cues stay: the bottom band of an expanded header IS the "first child" slot, and the whole outdent gesture
            // happens on folder rows.
            return Indicator(Tipped(EntityRow.Create(in spec), spec.LabelTooltip, label), _o.RowSelectsRoute(index, sel),
                IndentOf(in row), height, _o.PillRouteOf(index, sel));
        }

        /// <summary>The dimmed retention row for a folder pin the rootlist lost: no click, no disclosure, no drag/drop, and a
        /// menu reduced to the one honest verb (Unpin) — the reason rides the tooltip and, with subtitles, the second line.</summary>
        Element MissingFolderRow(SidebarSection section, in SidebarLibraryEntry entry, in SidebarRow row, float height)
        {
            string reason = Loc.Get(Strings.Sidebar.Pin.FolderMissing);
            float art = SidebarRowGeometry.ArtOf(section.Shape);
            var menu = _o.MissingFolderMenu(in entry);
            var spec = new RowSpec
            {
                Key = row.Key,
                Label = entry.Name.Length > 0 ? entry.Name : Loc.Get(Strings.Sidebar.V3.Kind.Folder),
                Subtitle = section.Shape == SidebarRowShape.EntityTwoLine ? reason : null,
                Enabled = false,
                Depth = IndentOf(in row),
                Tile = _o.CompactPlan,
                Shape = section.Shape,
                Height = height,
                ArtSize = art,
                Leading = section.Shape == SidebarRowShape.Text ? null : Cover.Folder(art, expanded: false),
                Glyph = section.Shape == SidebarRowShape.Text ? Icons.Folder : null,
                Pinned = SidebarPinRules.ShowsPinMark(_o.Config.Layout, section.Kind, row.Depth, false, false),
                Overflow = menu is not null,
                MenuOverlay = _o.MenuOverlay,
                Menu = menu,
            };
            return Tipped(EntityRow.Create(in spec), true, reason);
        }

        /// <summary>The trailing width a row's label gives up to its count (the quiet badge), and a folder's 40-px chevron
        /// column (which replaces the 14-px trailing pad). Both feed <see cref="LabelOverflows"/>: the estimate has to know
        /// what the row keeps for itself.</summary>
        const float CountTrail = 28f, FolderTrail = SidebarRowGeometry.ChevronColumn - SidebarRowGeometry.TrailingPad;

        /// <summary>The trailing width a non-chevron row's label gives up: the pin mark (12, or the 24 Unpin button) plus the
        /// count slot, which the hover "…" shares (<see cref="SidebarRowGeometry.CountSlotWidth"/>), so rest and hover agree.</summary>
        static float RowTrailing(in RowSpec spec, bool menu, bool playing)
            => SidebarRowGeometry.PinWidth(spec.Pinned, spec.OnUnpin is not null)
               + SidebarRowGeometry.CountSlotWidth(menu, spec.Trailing is not null, spec.Pinned || playing, CountTrail - SidebarRowGeometry.TrailingGap);

        /// <summary>The pin mark's action (J1): "Unpin" when the row is a pin the rule offers an Unpin for, else null (the bare
        /// mark). The same rule and the same ring-recorded verb as the row's context menu.</summary>
        static Action? UnpinOf(bool pinned, in SidebarLibraryEntry entry)
        {
            if (!pinned) return null;
            string? id = SidebarPinId.FromEntry(in entry);
            if (PinRowRule.Decide(true, id, id is not null && Sidebar.IsPinned(id)) != PinRowKind.Unpin) return null;
            string pinId = id!, name = entry.Name;
            return () => PaneView.UnpinWithToast(pinId, name);
        }

        /// <summary>Does the one-line label truncate in this row? The pane's CURRENT width (peeked: a tooltip decision, not a
        /// subscription) minus the row's own ladder, through the one estimate <see cref="SidebarLabelFit"/> owns.</summary>
        static bool LabelOverflows(string label, int depth, float trailing, float labelStart)
            => SidebarLabelFit.Overflows(label, SidebarLabelFit.LabelWidth(Sidebar.Width.Peek(), depth, trailing, labelStart));

        /// <summary>Where the row's label starts (slot space, before the depth indent): the same three cases
        /// <see cref="EntityRow.Create"/> lays out. A Text-shape row with no glyph and no leading visual has no icon column (label
        /// at the header's x); one with a glyph and no leading visual is a folder (mark at the header's x, 8 gap, label at pane
        /// 40); every other row has the 40-px icon column and its 4 gap.</summary>
        static float LabelStartOf(in RowSpec spec)
        {
            bool text = spec.Shape == SidebarRowShape.Text && spec.Leading is null;
            return SidebarRowGeometry.LabelStartOf(iconColumn: !text || spec.Glyph is not null, textGlyph: text && spec.Glyph is { Length: > 0 });
        }

        /// <summary>A row whose full label would truncate wears it as a tooltip. The slot owns the wrap, so the tooltip is the
        /// row's wrapper and a sibling of the drop cues, never a child of the row. A compact TILE always wears its label (the
        /// 40-DIP tile shows no text) and never grows: it is 40 wide already.</summary>
        Element Tipped(Element built, bool tooltip, string label)
            => _o.CompactPlan ? ToolTip.Wrap(built, label)
                : tooltip ? ToolTip.Wrap(built, label, grow: 1f)
                : built;

        /// <summary>The depth a row is drawn at in THIS pane: a compact tile is never indented, so its pill sits at PillX(0).</summary>
        int IndentOf(in SidebarRow row) => _o.CompactPlan ? 0 : row.Depth;

        /// <summary>The compact tile's realize handler, cached so a render allocates no delegate for it.</summary>
        Action<NodeHandle> TileRealize => _tileRealize ??= h => _tileNode = h;

        /// <summary>The flyout's anchor: the tile's live node. Cached like <see cref="TileRealize"/>.</summary>
        Func<NodeHandle> TileAnchor => _tileAnchor ??= () => _tileNode;

        /// <summary>A collapsed section in the compact rail (design V.9): one 40×36 glyph tile whose click opens the section's rows
        /// as a flyout. Takes the pill when the selected route lives inside (pill rule 3).</summary>
        Element SectionTileRow(SidebarSection section, in SidebarRow row, int index, string sel)
        {
            string id = section.Id;
            var owner = _o;
            owner.SetTileAnchor(index, TileAnchor);   // Enter on the focused tile opens the flyout from here
            var spec = new RowSpec
            {
                Key = row.Key, Label = PaneText.TitleOf(section), Shape = SidebarRowShape.Glyph, Tile = true,
                Glyph = PaneIcon.SectionGlyph(section.Kind), Selected = _o.RowSelectsRoute(index, sel),
                OnRealized = TileRealize,
                OnClick = () => owner.OpenSectionFlyout(id, TileAnchor),
            };
            var built = ToolTip.Wrap(EntityRow.Create(in spec), spec.Label);
            return Indicator(built, spec.Selected, 0, SidebarRowGeometry.RowHeight, _o.PillRouteOf(index, sel));
        }

        /// <summary>A route row (an app route the layout carries: a Collections page, a Home or Settings glyph row). Label and
        /// glyph come from the route table so a route follows the UI culture; an unknown key in a hand-edited document
        /// degrades rather than crashing.</summary>
        Element RouteRow(SidebarSection section, string key, string sel, int index)
        {
            var dest = Shell.Dest(Shell.Parse(key));
            bool selected = _o.RowSelectsRoute(index, sel);
            float height = SidebarRowGeometry.HeightOf(section.Shape);
            string title = dest.Title;
            // A durable application destination is a pin drag source unless a Reorderable already owns the drag;
            // SidebarPinId centrally excludes editor/tooling routes.
            DragPayload? drag = !_o.TryBandOf(index, out _) && SidebarPinId.FromRoute(key) is not null
                ? PaneView.PayloadOfRoute(key, title)
                : null;
            var menu = _o.RouteMenu(section, key, index);

            var spec = new RowSpec
            {
                Key = key,
                Label = title,
                Selected = selected,
                Depth = 0,
                Shape = section.Shape,
                Height = height,
                // A Text section's route pin stays text-only (no glyph); only a folder carries a mark in Text shape.
                Glyph = section.Shape == SidebarRowShape.Text ? null : dest.Glyph,
                Trailing = CountBadge(section, key),
                Tile = _o.CompactPlan,
                OnClick = () => _o.Navigate(key, null),
                Overflow = menu is not null,
                MenuOverlay = _o.MenuOverlay,
                Menu = menu,
                Drag = drag,
                DropTarget = PinSpec(section, section.Id, index),
            };
            spec.LabelTooltip = LabelOverflows(title, 0, RowTrailing(in spec, menu is not null, playing: false), LabelStartOf(in spec));
            return Indicator(Tipped(EntityRow.Create(in spec), spec.LabelTooltip, title), selected, 0, height, key);
        }

        /// <summary>The empty Pinned band while a pinnable drag is live (design V.3): 36 tall in the 4,2 margin, a dashed
        /// 1-px AccentDefault ring, r4, "Drop here to pin"; the drop pins at position 0.</summary>
        Element DropBandRow(SidebarSection section, int index) => new BoxEl
        {
            Key = "pin-drop-band", Height = SidebarRowGeometry.RowHeight, Shrink = 0f,
            Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            BorderWidth = 1f, BorderColor = Tok.AccentDefault, BorderDashOn = 4f, BorderDashOff = 2f,
            DropTarget = _o.ResourceDropSpec(section.Id, 0, null, null, rootPlanIndex: index),
            Children = [Caption(Loc.Get("sidebar.pin.dropHere")) with { Color = Tok.AccentDefault }],
        };

        // ── grid strips ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One row of a Grid-presentation section: <c>ItemCount</c> cells from <c>Entries[EntryIndex..]</c>.
        /// <para>THE COLUMN COUNT IS THE PLANNER'S. Re-deriving it from width here disagreed with how the planner already
        /// sliced the entries (a ragged grid whose row rhythm changed with the pane). Since the 180-DIP floor (#84) the planned
        /// count can starve cells below the 40-DIP cell floor, so this only WRAPS fewer cells per visual line
        /// (<c>SidebarRowGeometry.GridFallbackColumns</c>): the strip grows taller, its cells never narrower. The pane width
        /// is SUBSCRIBED so the cell edge re-flows with the seam. A cell's cover is its edge less the shared grid card's 2 × 8
        /// plate padding (<see cref="SidebarCardRules.TileCover"/>), so the chosen column count is honoured down to the floor.</para></summary>
        Element GridStripRow(SidebarSection section, in SidebarRow row, string sel)
        {
            float pane = _o.ExpandedWidth.Value;
            int cols = Math.Clamp(_o.GridColumns, 2, 4);
            const float gap = Spacing.S;
            float avail = pane - PaneMetrics.PaneInsetH;
            int fitCols = SidebarRowGeometry.GridFallbackColumns(cols, avail, gap, Cover.S40);
            float edge = MathF.Min(PaneMetrics.GridCellMax, MathF.Max(Cover.S40, (avail - gap * (fitCols - 1)) / fitCols));

            var entries = _o.Plan.Entries;
            int start = row.EntryIndex;
            int count = row.ItemCount;
            if (start < 0 || count <= 0 || start >= entries.Count) return Nothing;
            if (start + count > entries.Count) count = entries.Count - start;

            var cells = new Element[count];
            for (int i = 0; i < count; i++) cells[i] = GridCell(section, entries[start + i], edge, sel);
            return new BoxEl
            {
                Direction = 0, Wrap = true, Gap = gap,
                Padding = new Edges4(0f, 0f, 0f, Spacing.S),
                Children = cells,
            };
        }

        /// <summary>One cell of a grid strip: the shared media surface at the tile shape, in a column of the strip's derived
        /// width (a component anchor mirrors its rendered child's size rather than carrying flex props, so the width lands on
        /// this thin wrapper). Selection, the title gate (Trap 5), the activation, the play, the menu, the drag and the drop spec
        /// are <see cref="SidebarCards.Tile"/>'s — a grid cell is a different renderer, not a different rule.</summary>
        Element GridCell(SidebarSection section, SidebarLibraryEntry entry, float edge, string sel)
            => new BoxEl
            {
                Direction = 1, Width = edge, Shrink = 0f,
                Children = [Controls.Surface(SidebarCards.Tile(_o, section, in entry, edge, sel), global::Wavee.Shape.SidebarTile)],
            };

        // ── degraded rows ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The quiet hint under an empty Playlists section: 40 DIP tall, 11px tertiary, in its 2,2 margin. Its copy
        /// names the header "+" that fixes it, and swaps to the query copy while a search is live. The planner plans no Empty
        /// row in Your Library (the head owns that state, §P3.5).</summary>
        Element EmptyRow(SidebarSection section)
        {
            string text = _o.SearchText.Length > 0 ? LibraryEmptyText() : Loc.Get(Strings.Sidebar.Empty.Playlists);
            return new BoxEl
            {
                Height = PaneMetrics.EmptyHintHeight, AlignItems = FlexAlign.Center,
                Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY),
                Padding = new Edges4(SidebarRowGeometry.HeaderTextX, 0f, SidebarRowGeometry.TrailingPad, 0f),
                Children =
                [
                    global::Wavee.Design.Type.MicroMeta(text) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            };
        }

        string LibraryEmptyText()
        {
            string query = _o.SearchText;
            return query.Length > 0
                ? Loc.Format(PaneLoc.SearchEmpty, ("query", query))
                : Loc.Get(PaneLoc.LibraryEmpty);
        }

        // ── multi-select + the folder "+" ────────────────────────────────────────────────────────────────────────────

        /// <summary>Turn a rootlist row into a MULTI-SELECTABLE one (W17). <c>OnActivate</c> REPLACES <c>OnClick</c>
        /// because Ctrl/Shift are the gesture; <paramref name="plain"/> is the row's ordinary verb, which a plain click still
        /// runs after clearing the selection. The check lane reads the live selection through BOUND thunks (a selection
        /// change re-skins it with no re-render); the quiet plate is a value the pane's epoch bump re-renders.</summary>
        void ApplyTreeSelection(ref RowSpec spec, string entryId, Action? plain)
        {
            if (entryId.Length == 0) return;
            var owner = _o;
            spec.OnClick = null;
            spec.OnActivate = mods => owner.ActivateTreeRow(entryId, mods, plain);
            spec.OnEscape = owner.ClearTreeSelection;
            // PEEKED: read inside a press/key handler, where a subscription has no scope to belong to.
            spec.ChecksVisible = () => owner.ChecksVisible.Peek();
            spec.MultiSelected = owner.TreeSelection.Contains(entryId);
            spec.CheckLane = SelectorVisualsBound.BoundCheckLane(
                visible: () => owner.ChecksVisible.Value,
                isChecked: () =>
                {
                    _ = owner.SelectionVersion.Value;   // the bind's subscription — an epoch bump cannot reach a bound read
                    return owner.TreeSelection.Contains(entryId);
                },
                interact: (_, _) => owner.ToggleTreeSelection(entryId),
                // The row owns its own left inset (the 32-px depth ladder), so the lane adds none.
                leftMargin: 0f);
        }

        /// <summary>A folder row's trailing slot: the quiet count (the Playlists section), then the folder's own "+"
        /// (24 box, glyph 16) — the count is the fact, the "+" the verb. The "+" is keyed inside a row keyed by the entry,
        /// so it remounts when this slot recycles onto another folder, which is what makes capturing the folder id in its
        /// factory safe.</summary>
        Element? FolderTrailing(SidebarSection section, in SidebarLibraryEntry entry, string folderId, bool rootlistItem)
        {
            // No count: the "N items" subtitle already says it, and the chevron column is the folder's trailing edge.
            if (!rootlistItem || folderId.Length == 0 || _o.Acts is null) return null;

            var owner = _o;
            string name = entry.Name;
            var active = _folderPlusDrop;
            Func<float> reveal = _folderPlusReveal ??= FolderPlusOpacity;
            Element plus = Embed.Comp(() => new CreateButton(
                () => owner.NewPlaylistInFolder(folderId),
                menu: () => owner.FolderCreateMenu(folderId),
                drop: owner.FolderCreateDropSpec(folderId, name, active),
                dropActive: () => active.Value,
                revealOpacity: reveal,
                box: SidebarRowGeometry.RowButton, glyph: SidebarRowGeometry.PlusGlyph)) with { Key = "folder-create" };
            return plus;
        }

        /// <summary>The folder "+"'s base opacity, BOUND. Mouse hover is the engine's own reveal cascade, but hover flags
        /// freeze while a drag is live — so this lights the "+" while a rootlist drag is over the "+" itself OR over the row
        /// (whose spec publishes the slot), exactly when "drop this INTO a new folder here" is the useful sentence.
        /// Reads <c>_scope.Index.Value</c>, never a captured index.</summary>
        float FolderPlusOpacity()
        {
            int i = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(i);
            return _folderPlusDrop.Value || _o.DropSlotFor(i).PlanIndex == i ? 1f : 0f;
        }

        // ── shared row plumbing ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A FEED row's trailing slot: a new release's age badge (a playlist's count is its subtitle)
        /// ("3d"). The equalizer is the row primitive's own slot; route counts are <see cref="CountBadge"/>'s.</summary>
        static Element? TrailingBadge(SidebarSection section, in SidebarLibraryEntry entry)
        {
            if (section.Kind == SidebarSectionKind.NewReleases && PaneText.AgeBadge(entry.SortStamp) is { Length: > 0 } age)
                return global::Wavee.Design.Type.MicroMeta(age) with { Color = Tok.TextTertiary, MaxLines = 1 };
            return null;
        }

        /// <summary>The library-shortcut count through the ONE quiet badge (never an accent pill). Albums / Artists / Liked /
        /// Podcasts read the signed-in account's library edges — the server total while a relation is still paging, so the
        /// number does not climb — and an UNKNOWN relation is the 20×12 pending plate. Local files carries no count.
        /// <para>The relation's publish signal is read INSIDE <see cref="Counts.Live"/>'s own component, behind a value gate
        /// (W3-A2) — never here. This slot used to subscribe to it directly, and the library relations publish on exactly
        /// the routes that were slow: an artist page asking "is this album saved / this artist followed" and a playlist
        /// page asking "is this track liked" bump <c>SavedAlbums</c> / <c>FollowedArtists</c> / <c>Liked</c> on every
        /// membership answer, so the two shortcut rows rebuilt their whole row (spec, menu, drop spec, indicator — ~67 KB
        /// each) per publication while the number they show did not change (the <c>PaneSlot×2</c> census line).</para></summary>
        static Element? CountBadge(SidebarSection section, string routeKey)
        {
            if (section.Kind != SidebarSectionKind.Collections || ShortcutCount.KindOf(routeKey) is not { } kind) return null;
            return Counts.Live(kind);
        }

        /// <summary>Attach the item-owned selection pill and both drop cues. Shape-stable for recycling: a slot never
        /// inherits an animated transform from the route it represented one window earlier. The ZStack order is load-bearing
        /// — plate UNDER the row (text never tinted), pill over it, insertion line on top.</summary>
        Element Indicator(Element row, bool selected, int depth, float height, string? route)
        {
            if (!float.IsFinite(height) || height <= 0f) height = SidebarRowGeometry.HeightOf(SidebarRowShape.EntityTwoLine);
            _pillState = new SidebarPillState(
                Route: route,
                Selected: selected,
                Indent: SidebarRowGeometry.PillX(depth),
                Top: SidebarRowGeometry.PillTop(height));
            var owner = _o;
            Func<SidebarPillState> probe = _pillProbe ??= PillState;
            // Tree guides sit between the row and the pill: under the pill (the pill is the only "you are here"), over the
            // row's plate. The compact rail has no tree, so it mounts none.
            return _o.CompactPlan
                ? ZStack(DropPlate(), row, Embed.Comp(() => new SelectionPill(owner, probe)), InsertionLine())
                : ZStack(DropPlate(), row, TreeGuides(depth), Embed.Comp(() => new SelectionPill(owner, probe)), InsertionLine());
        }

        /// <summary>THE TREE GUIDES: one 1-px <see cref="Tok.StrokeDividerDefault"/> line per ancestor level of a child row,
        /// at slot x <see cref="SidebarRowGeometry.TreeGuideX"/> (pane 24 + 32·d), spanning the whole slot (margins
        /// included) so the lines of consecutive rows join. A zero-layout overlay: not hit-testable and not in flex sizing,
        /// built per row render (a recycled slot gets its own depth's lines), so no slot height or virtual offset moves.</summary>
        static Element TreeGuides(int depth)
        {
            int levels = SidebarRowGeometry.ClampDepth(depth);
            var kids = new Element[levels * 2];
            float cursor = 0f;
            for (int d = 0; d < levels; d++)
            {
                float x = SidebarRowGeometry.TreeGuideX(d);
                kids[d * 2] = new BoxEl { Width = x - cursor, Shrink = 0f };
                kids[d * 2 + 1] = new BoxEl { Width = TreeGuideWidth, Shrink = 0f, Fill = Tok.StrokeDividerDefault };
                cursor = x + TreeGuideWidth;
            }
            return new BoxEl
            {
                Key = "tree-guides",
                Direction = 0, AlignItems = FlexAlign.Stretch,
                HitTestVisible = false,
                Children = kids,
            };
        }

        const float TreeGuideWidth = 1f;

        /// <summary>THE "INTO" PLATE (W16): mounted once per row, ALWAYS, as the ZStack's first child — accent@0.18 with a
        /// 1-DIP accent border, radius 4, auto-sized to the row's rect, out of hit-testing so hover and the drop reach the
        /// row. It left the row itself because bindings wire at MOUNT only: a thunk built there captured the row's state as
        /// values and a same-keyed re-render kept the stale one. Every thunk reads the LIVE slot index + row epoch.</summary>
        Element DropPlate() => new BoxEl
        {
            Key = "drop-plate",
            // The plate covers the row, not the row's 2-px vertical margin.
            Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY),
            Corners = CornerRadius4.All(4f),
            BorderWidth = 1f,
            Fill = Prop.Of(_plateFill ??= CuePlateFill),
            BorderColor = Prop.Of(_plateBorder ??= CuePlateBorder),
            HitTestVisible = false,
        };

        ColorF CuePlateFill()
        {
            int i = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(i);
            return SidebarDropCue.DrawsPlate(_o.DropSlotFor(i).Kind) ? Tok.AccentDefault with { A = 0.18f } : ColorF.Transparent;
        }

        ColorF CuePlateBorder()
        {
            int i = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(i);
            return SidebarDropCue.DrawsPlate(_o.DropSlotFor(i).Kind) ? Tok.AccentDefault : ColorF.Transparent;
        }

        /// <summary>THE INSERTION LINE (W16): a 2-DIP accent caret, radius 1, with a 6-DIP terminal dot at its left cap (what
        /// makes a hairline read as a caret, not a divider). Mounted once per row, ALWAYS — conditional mounting would need a
        /// re-render per pointer move. Its key is constant, so a recycled slot UPDATES it and keeps the thunks it mounted
        /// with: THE LIVE INDEX IS THE WHOLE FIX (a captured index or height lit two carets after an auto-scrolled drag).
        /// The caret starts at <c>TreeContentX(depth)</c> — where that depth's content starts drawing — not at IndentFor.</summary>
        Element InsertionLine() => new BoxEl
        {
            Key = "drop-line",
            Height = SidebarDropCue.LineThickness,
            Width = Prop.Of(_lineWidth ??= CueLineWidth),
            Corners = CornerRadius4.All(SidebarDropCue.LineCorner),
            Fill = Tok.AccentDefault,
            Opacity = Prop.Of(_lineOpacity ??= CueLineOpacity),
            Transform = Prop.Of(_lineTransform ??= CueLineTransform),
            HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Width = SidebarDropCue.DotSize, Height = SidebarDropCue.DotSize,
                    Corners = CornerRadius4.All(SidebarDropCue.DotSize * 0.5f),
                    Margin = new Edges4(0f, (SidebarDropCue.LineThickness - SidebarDropCue.DotSize) * 0.5f, 0f, 0f),
                    Fill = Tok.AccentDefault,
                    HitTestVisible = false,
                },
            ],
        };

        float CueLineWidth()
        {
            int i = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(i);
            return SidebarDropCue.LineWidth(_o.ContentWidth, _o.DropSlotFor(i).Depth);
        }

        float CueLineOpacity()
        {
            int i = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(i);
            return SidebarDropCue.DrawsLine(_o.DropSlotFor(i).Kind) ? 1f : 0f;
        }

        Affine2D CueLineTransform()
        {
            int i = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(i);
            var slot = _o.DropSlotFor(i);
            return Affine2D.Translation(SidebarRowGeometry.TreeContentX(slot.Depth),
                                        SidebarDropCue.LineY(slot.Kind, _o.RowExtentOf(i)));
        }

        /// <summary>The row's STRUCTURAL drop facts, straight off the published plan. <c>NextVisibleDepth</c> is the whole
        /// depth-ambiguity story (a slot is ambiguous exactly when it is shallower than this row). The payload-dependent half
        /// — self, ancestor, whether the centre takes this payload, the live rootlist-loaded gate — is folded in at hover.</summary>
        SidebarRowFacts TreeRowFacts(int index, in SidebarLibraryEntry entry)
        {
            var rows = _o.Plan.Rows;
            var entries = _o.Plan.Entries;
            int nextDepth = 0;
            bool hasChild = false;
            if ((uint)index < (uint)rows.Count && index + 1 < rows.Count)
            {
                var next = rows[index + 1];
                if (next.Kind is SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader
                    && string.Equals(next.SectionId, rows[index].SectionId, StringComparison.Ordinal)
                    && (uint)next.EntryIndex < (uint)entries.Count)
                {
                    nextDepth = entries[next.EntryIndex].Depth;
                    hasChild = nextDepth > entry.Depth;
                }
            }
            return new SidebarRowFacts(
                IsFolder: entry.IsFolder,
                FolderExpanded: entry.IsFolder && Sidebar.IsFolderExpanded(entry.FolderId),
                FolderHasChildren: hasChild,
                Depth: entry.Depth,
                NextVisibleDepth: nextDepth,
                CenterAccepts: entry.IsFolder,
                SourceIsSelf: false,
                SortedNonCustom: _o.TreeSortedNonCustom,
                RootlistLoaded: true);
        }

        /// <summary>The tree's closing gutter: 24 DIP of nothing that owns ONE slot — "top level, at the end" (the chip says
        /// "Move to end"). It exists because that slot had no target and the old create row accepted rootlist payloads,
        /// turning a drag past the end into a duplicated playlist. The destination is synthetic (resolved against the last
        /// top-level entry at commit), and its caret reads the LIVE slot index like every other row's.</summary>
        Element TreeEndRow(in SidebarRow row, int index)
        {
            var facts = new SidebarRowFacts(
                IsFolder: false, FolderExpanded: false, FolderHasChildren: false,
                Depth: 0, NextVisibleDepth: 0, CenterAccepts: false, SourceIsSelf: false,
                SortedNonCustom: _o.TreeSortedNonCustom, RootlistLoaded: true) { IsListEnd = true };
            var target = new DragPayload(DragKind.Route, row.SectionId, "", "", RootlistItem: true);
            var drop = _o.ResourceDropSpec(row.SectionId, -1, null, null, target, index, rootFacts: facts);
            return ZStack(
                new BoxEl
                {
                    Key = "tree-end",
                    Height = SidebarRowGeometry.TreeEndHeight,
                    Width = Prop.Of(_treeEndWidth ??= TreeEndWidth),
                    DropTarget = drop,
                },
                InsertionLine());
        }

        float TreeEndWidth() => _o.ContentWidth;

        /// <summary>THE PILL'S ONE LIVE READ. The pill's opacity is BOUND to this (never a mount-time literal), re-derived by
        /// the row epoch on every edge that can change it. The route is PEEKED (subscribing would put every realized pill
        /// back on the route fanout). The verdict is the pane's row resolver AND-ed with the route this pill was drawn for;
        /// playback is deliberately absent — the playing row draws |||, never the pill.</summary>
        SidebarPillState PillState()
        {
            int index = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(index);
            string live = _o.SelectedRoutePeek;
            return _pillState.For(live, _o.RowSelectsRoute(index, live));
        }

        /// <summary>A PINNED row is also a drop target, so dragging an entity onto the band pins it AT that position. Scoped to
        /// the band (never the pane) — a pane-wide accept would pin the moment a row was nudged.</summary>
        DropTargetSpec? PinSpec(SidebarSection section, string sectionId, int index, Action? onSpringLoad = null)
        {
            if (section.Kind != SidebarSectionKind.Pinned) return null;
            int slot = PinSlot(sectionId, index);
            return slot >= 0
                ? _o.ResourceDropSpec(sectionId, slot, null, null, rootPlanIndex: index, onSpringLoad: onSpringLoad)
                : null;
        }

        int PinSlot(string sectionId, int index)
            => _o.TryBandOf(index, out var band) && string.Equals(band.SectionId, sectionId, StringComparison.Ordinal)
                ? index - band.Start
                : -1;
    }
}
