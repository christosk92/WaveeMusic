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
    /// recycle. The chevrons, the pill, the "+", the drop zone and the hero / grid-tile <c>Controls.Surface</c>s are
    /// hook-owning CHILDREN, and each row kind has its own recycle pool (<c>ContentType</c> = row kind), so a header slot
    /// never rebinds into a chevron-less shape. A surface is PROPS-DRIVEN (<c>Embed.Comp(props, factory)</c> re-pushes the
    /// new <c>CardData</c> on every rebind), which is what makes it legal under a recycling slot: no constructor argument
    /// freezes at mount (the adapters are Sidebar.Cards.cs).</para>
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
        Func<bool>? _headerOpen, _folderOpen, _cardOpen;
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
                SidebarRowKind.HeaderLabel => SectionHeader.Header(PaneText.TitleOf(section), true, null, null, null, null),
                // Only a Divider SECTION plans this row; ordinary section joins are whitespace, never implicit rules.
                SidebarRowKind.Divider => SectionHeader.Separator(),
                SidebarRowKind.IconRow or SidebarRowKind.EntityRow or SidebarRowKind.Placeholder
                    => ItemOrEntity(section, in row, sel, index),
                SidebarRowKind.FolderHeader => FolderRow(section, in row, index, sel),
                SidebarRowKind.GridStrip => GridStripRow(section, in row, sel),
                SidebarRowKind.Empty => EmptyRow(section),
                SidebarRowKind.Skeleton => Skeletons.Row(index, PaneMetrics.ShapeOf(section),
                    heightOverride: PaneMetrics.RowHeight(section), artOverride: PaneMetrics.ArtSize(section)),
                SidebarRowKind.TreeEnd => TreeEndRow(in row, index),
                SidebarRowKind.EntityCard => HeroCard(section, in row, sel, index),
                SidebarRowKind.PromptRow => PromptCard(section),
                // The customize canvas: only the edit planner emits this kind, into its own recycle pool.
                SidebarRowKind.SectionCard => EditCard.Build(_o, section, index,
                    Chevron.Section(_cardOpen ??= CardOpenLive, identity: _sectionIdentity ??= SectionIdentity)),
                _ => Nothing,
            };

            // The SECTION-CARD band. Deliberately ahead of and disjoint from the item band below: the two carry different
            // drag kinds, and one wrap site answering both is how a card ends up moving an item's slot.
            if (row.Kind == SidebarRowKind.SectionCard && _o.TryEditSectionBand(index, out var cardBand))
                content = _o.SectionReorder.Item(index - cardBand.Start, FillSlot(content), key: row.SectionId,
                    transition: PaneView.Placement);

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
            if (row.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.IconRow or SidebarRowKind.Placeholder
                                 or SidebarRowKind.FolderHeader)) return null;
            if (!_o.TryBandOf(index, out var band)) return null;
            return (_o.ReorderFor(band.SectionId), band.Start);
        }

        // ── section chrome ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The 40-px section header (P1.4.6): the title, then the "+" (a PlaylistTree's create affordance, gated on a
        /// CONFIG flag, never on the design, because V3's own chrome carries its own "+"), the ⋯ (<see cref="PaneView.HeaderMenu"/>,
        /// revealed on hover, permanent after a touch) and the chevron. An editable EntityList with <c>InlineControls</c>
        /// hangs its chip strip under the header; an open Classic filter replaces the header's tail with its box. The header
        /// is the pill's section anchor (the one-pill rule's third arm), so the pill is drawn here too.</summary>
        Element HeaderRow(SidebarSectionSpec section, in SidebarRow row, int index, string sel)
        {
            string id = section.Id;
            var owner = _o;

            // Every delegate reads LIVE pane state: a header slot recycles across sections.
            Element? create = null, more = null;
            if (section.Kind == SidebarSectionKind.PlaylistTree && _o.Config.HeaderCreate && _o.Config.OnCreatePlaylist is not null)
                create = Embed.Comp(() => new CreateButton(
                    owner.CreatePlaylist, menu: owner.CreateMenu, drop: owner.HeaderCreateDropSpec(),
                    dropActive: () => owner.HeaderCreateDropActive.Value,
                    box: SidebarRowGeometry.HeaderButton, glyph: 12f)) with { Key = "tree-create" };
            if (_o.MenuOverlay is { } svc && _o.HeaderMenu(id) is { } menu)
                more = ToolTip.Wrap(SectionHeader.InlineButton(Icons.More, null, reveal: !_o.TouchLast)
                    .WithContextMenu(svc, menu) with { ClickRequestsContext = true }, Loc.Get(PaneLoc.SectionOptions));

            // Explicit locals, never a ternary against null: a lambda has no natural type in that position.
            Action<bool>? toggle = null;
            Element? chevron = null;
            // The materialised Shortcuts band is NOT collapsible in any mode: it is projected from the document's TopBar,
            // has no persisted Collapsed bit, and SetSectionCollapsed("topbar") is an UnknownSection rejection — a chevron
            // there would be an affordance that silently does nothing.
            if (_o.Config.SetSectionCollapsed is not null && !SidebarIds.IsTopBar(id))
            {
                toggle = open => owner.ToggleSection(id, !open);
                // ONE rotating glyph, never a swap; a recycle onto another section seeds its angle instead of spinning.
                chevron = new BoxEl
                {
                    Width = SidebarRowGeometry.HeaderButton, Height = SidebarRowGeometry.HeaderButton, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Chevron.Section(_headerOpen ??= HeaderOpenLive, identity: _sectionIdentity ??= SectionIdentity)],
                };
            }

            Element header = SectionHeader.Header(PaneText.TitleOf(section), !section.Collapsed, toggle, create, more, chevron);
            if (_o.FilterOpenFor(id)) header = new BoxEl { Direction = 1, Children = [header, _o.FilterBox()] };
            else if (!_o.Config.ReadOnly && section.Kind == SidebarSectionKind.EntityList && section.Opts.InlineControls && !section.Collapsed)
                header = new BoxEl { Direction = 1, Gap = 4f, Children = [header, InlineControls.Chips(_o, section)] };

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

        /// <summary>The EDIT CARD's chevron — same recycle-safe shape. It asks about the session the PUBLISHED plan was built
        /// from, so the mark can never claim "open" over a plan with no body rows under this card.</summary>
        bool CardOpenLive()
        {
            int index = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(index);
            var rows = _o.Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return false;
            var section = _o.SectionOf(rows[index].SectionId);
            return section is not null && _o.EditShowsBody(section);
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

        // ── item / entity rows ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The three row kinds that carry EITHER a projected entry or a hand-placed item, resolved in ONE place:
        /// an ACTION item is an action row whatever kind the planner chose (a bound shortcut arrives as Placeholder and must
        /// never render as a missing entity) → a projected entry (a Pinned override item still supplies alias/icon) → a
        /// hand-placed TRACK plays → a ROUTE item is a glyph row → anything else is the dimmed missing-entity retention
        /// row, never dropped.</summary>
        Element ItemOrEntity(SidebarSectionSpec section, in SidebarRow row, string sel, int index)
        {
            var item = PaneText.ItemOf(section, row.Key);
            if (item is { Target: SidebarItemTarget.Action }) return ActionRow(section, item, index);

            var entries = _o.Plan.Entries;
            if ((uint)row.EntryIndex < (uint)entries.Count)
            {
                var entry = entries[row.EntryIndex];
                // A Pinned override may be keyed by uri instead of pin id (the planner accepts both).
                item ??= PaneText.ItemOf(section, entry.Uri);
                return EntryRow(section, in entry, in row, sel, item, index);
            }
            if (item is { Target: SidebarItemTarget.Track }) return TrackItemRow(section, item, index);
            if (item is { Target: SidebarItemTarget.Route }) return RouteRow(section, item, sel, index);
            return MissingRow(section, item, in row);
        }

        /// <summary>A projected entry: a playlist / album / artist / show / track / app route the projection knows.</summary>
        Element EntryRow(SidebarSectionSpec section, in SidebarLibraryEntry entry, in SidebarRow row, string sel,
                         SidebarItemSpec? item, int index)
        {
            bool named = entry.Name.Length > 0;
            bool track = entry.IsTrack;
            string? route = entry.RouteKey;
            // A route pin persisted with an empty Name has no entity to resolve one: the route table is its title source.
            bool routePin = !named && entry.Kind == SidebarEntryKind.AppRoute && route is { Length: > 0 };
            // Trap 5: a PIN with no cached Name and no Identity yet (the store's disk leg answers
            // `Knows(Identity)` asynchronously, after this row's first synchronous render) must not fall back to
            // the raw uri fragment either — that reads as real data when it is a guess. Pending shows nothing
            // rather than "3fMbdgg4jU18AjLCKBhRSm". A resolved entity that is genuinely nameless (rare) still gets
            // the honest short-uri fallback. The gate covers EVERY row, pinned or not (a library row is minted
            // from a uri-only collection answer and resolves later, so it needs the gate just as much), and every
            // renderer that paints a title now goes through it - GridCell and both rail paths included.
            string label = item?.LabelOverride is { Length: > 0 } alias ? alias
                : named ? entry.Name
                : routePin ? Shell.Dest(Shell.Parse(route!)).Title
                : SidebarProjection.ShouldShowUriFallbackTitle(entry.IsPinned, entry.IdentityKnown)
                    ? PaneText.ShortUri(entry.Uri)
                    : "";
            // Resolved pane-side by the SAME rule the selection sweep uses, so the row that draws the plate and the row
            // whose epoch got bumped can never disagree.
            bool selected = _o.RowSelectsRoute(index, sel);
            bool reordering = _o.TryBandOf(index, out _);
            var (playing, animated) = _o.RowPlayState(index);
            var shape = PaneMetrics.ShapeOf(section);
            float height = PaneMetrics.RowHeight(section);

            var snapshot = entry;   // an `in` parameter cannot be captured — copy the record struct for the closures
            Action? click = null;
            if (track) click = () => _o.Play(snapshot.Uri, asTrack: true);
            else if (route is { Length: > 0 } navRoute) click = () => _o.Navigate(navRoute, snapshot.Name, in snapshot);

            var menu = _o.EntryMenu(section, index, in snapshot, item, row.Key);
            // F2: only a row that can really be renamed takes it (and so becomes a focus stop); never a banded row.
            Action? rename = reordering ? null : _o.RenameAction(in snapshot);

            bool treeRow = section.Kind == SidebarSectionKind.PlaylistTree && !reordering;
            // Rootlist membership is a fact about the ENTITY, not the section: a pinned or recent playlist row is the same
            // rootlist member as its tree row, and is file-able from either.
            bool rootlistItem = !reordering && snapshot.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder;
            // Alt+↑/↓ — TREE rows only: a pinned row is a rootlist member but not a rootlist ORDERING slot.
            Action<int>? move = treeRow && rootlistItem ? _o.TreeMoveAction(in snapshot) : null;
            // `resource` is THIS row's own identity (what a drop files relative to, the "Move into {name}" name, the self
            // check). The DRAG payload is the pane's: a row inside the multi-selection lifts the whole selection.
            DragPayload? resource = treeRow ? PaneView.PayloadOf(in snapshot, rootlistItem: true) : null;
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

            var spec = new RowSpec
            {
                Key = row.Key,
                Label = label,
                // The shape already decides the second line (a one-line shape never draws one), so the text is only built for a two-line row.
                Subtitle = shape == SidebarRowShape.EntityTwoLine ? PaneText.SubtitleOf(in snapshot) : null,
                Selected = selected,
                Enabled = named || track || routePin,
                Depth = row.Depth,
                Shape = shape,
                Height = height,   // UNIFORM per section: a band's slot pitch and the extent table both assume one height
                ArtSize = PaneMetrics.ArtSize(section),
                Leading = LeadingArt(section, in snapshot, item),
                Glyph = section.Opts.Artwork ? null : PaneText.Glyph(item, PaneText.EntryGlyph(snapshot.Kind)),
                Trailing = TrailingBadge(section, in snapshot),
                Pinned = SidebarRowGeometry.ShowsPinGlyph(snapshot.IsPinned, track),   // #85
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
            spec.LabelTooltip = LabelOverflows(label, row.Depth, (spec.Trailing is null ? 0f : CountTrail)
                + EntityRow.OverflowReserve(menu is not null, spec.Trailing is not null || spec.Pinned || playing));
            Element built = EntityRow.Create(in spec);
            if (track) built = EntityRow.WithPlayTrackHint(built);
            // The pill stays in the row's own indent (31 per depth level), never the drop caret's gutter.
            return Indicator(Tipped(built, spec.LabelTooltip, label), selected, row.Depth, height, route);
        }

        /// <summary>A rootlist FOLDER: entity-row geometry, the folder mark, and the disclosure chevron in the TRAILING
        /// cluster (W7). A folder never navigates; by default it toggles its expansion, and a mode may reroute that gesture
        /// through <c>Config.ActivateFolder</c> (V3's narrow drill-in) — click and menu verb take the same path.</summary>
        Element FolderRow(SidebarSectionSpec section, in SidebarRow row, int index, string sel)
        {
            var entries = _o.Plan.Entries;
            if ((uint)row.EntryIndex >= (uint)entries.Count) return Nothing;
            var entry = entries[row.EntryIndex];
            string folderId = entry.FolderId;
            bool expanded = Sidebar.IsFolderExpanded(folderId);
            float height = PaneMetrics.RowHeight(section);

            // A synced folder pin the rootlist no longer carries renders visible-but-disabled, never vanishes.
            if (entry.Missing) return MissingFolderRow(section, in entry, in row, height);

            var snapshot = entry;
            var shape = PaneMetrics.ShapeOf(section);
            float art = PaneMetrics.ArtSize(section);
            string label = entry.Name.Length > 0 ? entry.Name : PaneText.ShortUri(entry.Id);

            Action activate = () => _o.ActivateFolder(folderId, snapshot.Name, index);
            var menu = _o.FolderMenu(section, index, in snapshot, activate, expanded, row.Key);
            bool reordering = _o.TryBandOf(index, out _);
            Action? rename = reordering ? null : _o.RenameAction(in snapshot);
            // Only a PlaylistTree folder is a rootlist member; a folder elsewhere is a plain pin row.
            bool rootlistItem = section.Kind == SidebarSectionKind.PlaylistTree && !reordering;
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

            var spec = new RowSpec
            {
                Key = row.Key,
                Label = label,
                // Trap 5: routed through the same PURE decision every other kind's subtitle uses
                // (`PaneText.SubtitleOf`), which gates a folder's "N items" on `entry.CountKnown` — a Pending
                // unlisted folder pin (the rootlist hasn't answered this session) shows its title alone, never a
                // confident "0 items".
                Subtitle = shape == SidebarRowShape.EntityTwoLine ? PaneText.SubtitleOf(in entry) : null,
                Depth = row.Depth,
                Shape = shape,
                Height = height,
                ArtSize = art,
                Leading = section.Opts.Artwork ? Cover.Folder(art, expanded) : null,
                Glyph = section.Opts.Artwork ? null : expanded ? Icons.FolderOpen : Icons.Folder,
                DisclosureChevron = Chevron.Disclosure(_folderOpen ??= FolderOpenLive, identity: _folderIdentity ??= FolderIdentity),
                Trailing = FolderTrailing(section, in snapshot, folderId, rootlistItem),
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
            spec.LabelTooltip = LabelOverflows(label, row.Depth, FolderTrail + EntityRow.OverflowReserve(menu is not null, trailing: true));
            // A folder is a pill anchor when it is the deepest visible ancestor of the route (rule 2 of §P1.2). Both drop
            // cues stay: the bottom band of an expanded header IS the "first child" slot, and the whole outdent gesture
            // happens on folder rows.
            return Indicator(Tipped(EntityRow.Create(in spec), spec.LabelTooltip, label), _o.RowSelectsRoute(index, sel),
                row.Depth, height, _o.PillRouteOf(index, sel));
        }

        /// <summary>The dimmed retention row for a folder pin the rootlist lost: no click, no disclosure, no drag/drop, and a
        /// menu reduced to the one honest verb (Unpin) — the reason rides the tooltip and, with subtitles, the second line.</summary>
        Element MissingFolderRow(SidebarSectionSpec section, in SidebarLibraryEntry entry, in SidebarRow row, float height)
        {
            string reason = Loc.Get(Strings.Sidebar.Pin.FolderMissing);
            float art = PaneMetrics.ArtSize(section);
            var menu = _o.MissingFolderMenu(in entry);
            var spec = new RowSpec
            {
                Key = row.Key,
                Label = entry.Name.Length > 0 ? entry.Name : Loc.Get(Strings.Sidebar.V3.Kind.Folder),
                Subtitle = section.Opts.Subtitles ? reason : null,
                Enabled = false,
                Depth = row.Depth,
                Shape = PaneMetrics.ShapeOf(section),
                Height = height,
                ArtSize = art,
                Leading = section.Opts.Artwork ? Cover.Folder(art, expanded: false) : null,
                Glyph = section.Opts.Artwork ? null : Icons.Folder,
                Overflow = menu is not null,
                MenuOverlay = _o.MenuOverlay,
                Menu = menu,
            };
            return ToolTip.Wrap(EntityRow.Create(in spec), reason, grow: 1f);
        }

        /// <summary>The trailing width a row's label gives up to its count (the quiet badge), and a folder's 40-px chevron
        /// column (which replaces the 14-px trailing pad). Both feed <see cref="LabelOverflows"/>: the estimate has to know
        /// what the row keeps for itself.</summary>
        const float CountTrail = 28f, FolderTrail = SidebarRowGeometry.ChevronColumn - SidebarRowGeometry.TrailingPad;

        /// <summary>Does the one-line label truncate in this row? The pane's CURRENT width (peeked: a tooltip decision, not a
        /// subscription) minus the row's own ladder, through the one estimate <see cref="SidebarLabelFit"/> owns.</summary>
        static bool LabelOverflows(string label, int depth, float trailing)
            => SidebarLabelFit.Overflows(label, SidebarLabelFit.LabelWidth(Sidebar.Width.Peek(), depth, trailing));

        /// <summary>A row whose full label would truncate wears it as a tooltip. The slot owns the wrap, so the tooltip is the
        /// row's wrapper and a sibling of the drop cues, never a child of the row.</summary>
        static Element Tipped(Element built, bool tooltip, string label)
            => tooltip ? ToolTip.Wrap(built, label, grow: 1f) : built;

        /// <summary>A hand-picked app route (CollectionShortcuts / StaticLinks). Label + glyph come from the route table so a
        /// pinned "Liked Songs" follows the UI culture; an unknown key in a hand-edited document degrades rather than
        /// crashing.</summary>
        Element RouteRow(SidebarSectionSpec section, SidebarItemSpec item, string sel, int index)
        {
            string key = item.Key;
            var dest = Shell.Dest(Shell.Parse(key));
            bool selected = _o.RowSelectsRoute(index, sel);
            float height = PaneMetrics.RowHeight(section);
            string title = item.LabelOverride is { Length: > 0 } routeAlias ? routeAlias : dest.Title;
            // A durable application destination is a pin drag source unless a Reorderable already owns the drag;
            // SidebarPinId centrally excludes editor/tooling routes.
            DragPayload? drag = !_o.TryBandOf(index, out _) && SidebarPinId.FromRoute(key) is not null
                ? PaneView.PayloadOfRoute(key, title)
                : null;
            var menu = _o.RouteMenu(section, item, index);

            var spec = new RowSpec
            {
                Key = key,
                Label = title,
                Selected = selected,
                Depth = 0,
                Shape = PaneMetrics.ShapeOf(section),
                Height = height,
                Glyph = RowGlyphs.For(item, dest.Glyph),
                Trailing = CountBadge(section, key),
                OnClick = () => _o.Navigate(key, null),
                Overflow = menu is not null,
                MenuOverlay = _o.MenuOverlay,
                Menu = menu,
                Drag = drag,
                DropTarget = PinSpec(section, section.Id, index),
            };
            spec.LabelTooltip = LabelOverflows(title, 0, (spec.Trailing is null ? 0f : CountTrail)
                + EntityRow.OverflowReserve(menu is not null, spec.Trailing is not null));
            return Indicator(Tipped(EntityRow.Create(in spec), spec.LabelTooltip, title), selected, 0, height, key);
        }

        /// <summary>A hand-placed TRACK: click PLAYS, never navigates, and the hover/focus play glyph makes that legible before
        /// the click. Never a pin source. Its menu is layout-only (null when there is nothing to move or remove).</summary>
        Element TrackItemRow(SidebarSectionSpec section, SidebarItemSpec item, int index)
        {
            string uri = item.Key;
            var (playing, animated) = _o.RowPlayState(index);
            float art = PaneMetrics.ArtSize(section);
            string label = item.LabelOverride is { Length: > 0 } trackAlias ? trackAlias
                : item.FallbackTitle is { Length: > 0 } trackCached ? trackCached
                : PaneText.ShortUri(uri);
            var menu = _o.LayoutOnlyMenu(section, item, index, uri);

            var spec = new RowSpec
            {
                Key = uri,
                Label = label,
                Shape = PaneMetrics.ShapeOf(section),
                Height = PaneMetrics.RowHeight(section),
                ArtSize = art,
                Leading = section.Opts.Artwork ? Cover.ArtUrl(item.FallbackImageUrl, uri, art) : null,
                Glyph = section.Opts.Artwork ? null : RowGlyphs.For(item, Icons.MusicNote),
                Playing = playing,
                PlayingAnimated = animated,
                Track = true,
                Overflow = menu is not null,
                OnClick = () => _o.Play(uri, asTrack: true),
                MenuOverlay = _o.MenuOverlay,
                Menu = menu,
            };
            spec.LabelTooltip = LabelOverflows(label, 0, 0f);
            return Tipped(EntityRow.WithPlayTrackHint(EntityRow.Create(in spec)), spec.LabelTooltip, label);
        }

        /// <summary>An ACTION shortcut, resolved ONLY through the pane's registry seam (W22). An unavailable target renders
        /// VISIBLE-BUT-DISABLED with the reason as its tooltip — a vanishing row makes the user's own sidebar look broken.
        /// <c>grow: 1f</c> is load-bearing: the tooltip wrapper is a flex ROW, so without it the disabled arm shrank to its
        /// label while the enabled arm filled the pane — one row kind at two widths.</summary>
        Element ActionRow(SidebarSectionSpec section, SidebarItemSpec item, int index)
        {
            var (label, icon, enabled, reason, click) = _o.ResolveActionRow(item);
            if (string.IsNullOrEmpty(label)) label = Loc.Get(PaneLoc.ExtensionManage);
            var menu = _o.LayoutOnlyMenu(section, item, index, item.Key);

            var spec = new RowSpec
            {
                Key = item.Id,
                Label = label,
                Selected = false,
                Enabled = enabled,
                Shape = PaneMetrics.ShapeOf(section),
                Height = PaneMetrics.RowHeight(section),
                // Art-wide leading column + the row's own leading gap: the label lines up with its siblings'.
                Leading = PaneIcon.Leading(item.IconOverride, icon, enabled, PaneMetrics.ArtSize(section)),
                Overflow = menu is not null,
                OnClick = enabled ? click : null,
                MenuOverlay = _o.MenuOverlay,
                Menu = menu,
            };
            Element built = EntityRow.Create(in spec);
            return reason is { Length: > 0 } why ? ToolTip.Wrap(built, why, grow: 1f) : built;
        }

        /// <summary>The missing-entity retention row: dimmed, from the item's last-known title/art, with a menu of exactly one
        /// verb (remove it) — or none under a locked document. NEVER auto-removed. Always tooltip-wrapped, so it takes
        /// <c>grow: 1f</c> or it would be the one row in its section that never fills.</summary>
        Element MissingRow(SidebarSectionSpec section, SidebarItemSpec? item, in SidebarRow row)
        {
            float art = PaneMetrics.ArtSize(section);
            string missing = Loc.Get(PaneLoc.MissingEntity);
            string label = item?.LabelOverride is { Length: > 0 } missingAlias ? missingAlias
                : item?.FallbackTitle is { Length: > 0 } missingCached ? missingCached
                : PaneText.ShortUri(row.Key);

            var spec = new RowSpec
            {
                Key = row.Key,
                Label = label,
                Subtitle = section.Opts.Subtitles ? missing : null,
                Enabled = false,
                Depth = row.Depth,
                Shape = PaneMetrics.ShapeOf(section),
                Height = PaneMetrics.RowHeight(section),
                ArtSize = art,
                Leading = section.Opts.Artwork ? Cover.ArtUrl(item?.FallbackImageUrl, row.Key, art) : null,
                Glyph = section.Opts.Artwork ? null : PaneText.Glyph(item, Icons.MusicNote),
                MenuOverlay = _o.MenuOverlay,
                Menu = _o.MissingItemMenu(section.Id, item),
            };
            return ToolTip.Wrap(EntityRow.Create(in spec), missing, grow: 1f);
        }

        // ── the hero card ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The <c>EntityEmbed</c> spotlight card — the shared media surface at the hero shape
        /// (<see cref="SidebarCardRules.HeroShape"/>): cover left (circular for artists), title + subtitle, and — with
        /// <c>Display.PlayButton</c> — the surface's own play affordance, which plays the entity AS A CONTEXT and turns into the
        /// now-playing pill while it is the playing context. Clicking anywhere else navigates; hover, press, the hand cursor,
        /// the focus ring, the drag and the "…" are the surface's, the menu and the drop spec the pane's
        /// (<see cref="SidebarCards.Hero"/>). An unresolved entity is still a card: 0.55 opacity and disabled, from the item's
        /// cached title/art — the adapter withholds its click, menu, play, drag and drop.</summary>
        Element HeroCard(SidebarSectionSpec section, in SidebarRow row, string sel, int index)
        {
            var (data, resolved) = SidebarCards.Hero(_o, section, in row, sel, index);
            return new BoxEl
            {
                Direction = 1,
                // The pane owns the horizontal inset; the card contributes only its vertical breathing room.
                Margin = new Edges4(0f, 2f, 0f, 2f),
                Opacity = resolved ? 1f : 0.55f,
                IsEnabled = resolved,
                Children = [Controls.Surface(data, SidebarCardRules.HeroShape(PaneMetrics.CardHeight(section)))],
            };
        }

        // ── grid strips ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One row of a Grid-presentation section: <c>ItemCount</c> cells from <c>Entries[EntryIndex..]</c>.
        /// <para>THE COLUMN COUNT IS THE PLANNER'S. Re-deriving it from width here disagreed with how the planner already
        /// sliced the entries (a ragged grid whose row rhythm changed with the pane). Since the 180-DIP floor (#84) the planned
        /// count can starve cells below the 40-DIP cell floor, so this only WRAPS fewer cells per visual line
        /// (<c>SidebarRowGeometry.GridFallbackColumns</c>): the strip grows taller, its cells never narrower. The pane width
        /// is SUBSCRIBED so the cell edge re-flows with the seam. A cell's cover is its edge less the shared grid card's 2 × 8
        /// plate padding (<see cref="SidebarCardRules.TileCover"/>), so the chosen column count is honoured down to the floor.</para></summary>
        Element GridStripRow(SidebarSectionSpec section, in SidebarRow row, string sel)
        {
            float pane = _o.ExpandedWidth.Value;
            int cols = Math.Clamp(section.Opts.GridColumns, 2, 4);
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
        Element GridCell(SidebarSectionSpec section, SidebarLibraryEntry entry, float edge, string sel)
            => new BoxEl
            {
                Direction = 1, Width = edge, Shrink = 0f,
                Children = [Controls.Surface(SidebarCards.Tile(_o, section, in entry, edge, sel), global::Wavee.Shape.SidebarTile)],
            };

        // ── degraded + affordance rows ───────────────────────────────────────────────────────────────────────────────

        /// <summary>A section that resolved to ZERO rows — FOUR arms. Pinned runs FIRST and unconditionally: its empty state IS
        /// the drop target, and an authored HideBody must never delete the one surface that teaches drop-to-pin. HideBody ⇒ a
        /// literal blank (the header stays; V3's own actionable state is then the only empty message on screen). ActionCard ⇒
        /// a disabled row at the section's own row height. Otherwise the quiet 40-DIP 11px tertiary hint in its 4,2 margin, per-kind copy; a
        /// PlaylistTree names the header "+" that fixes it, and swaps to the query copy while a search is live.</summary>
        Element EmptyRow(SidebarSectionSpec section)
        {
            if (section.Kind == SidebarSectionKind.Pinned)
            {
                var owner = _o;
                return Embed.Comp(() => new PinDropZone(owner.AcceptPinDrop));
            }

            var behavior = SidebarSectionKinds.EmptyBehaviorFor(section.Kind, section.Opts.EmptyBehavior);
            if (behavior == SidebarEmptyBehavior.HideBody) return Nothing;

            string text = section.Kind switch
            {
                SidebarSectionKind.PlaylistTree => _o.SearchText.Length > 0
                    ? LibraryEmptyText()
                    : Loc.Get(Strings.Sidebar.Empty.Playlists),
                SidebarSectionKind.EntityList => LibraryEmptyText(),
                _ => PaneText.EmptyText(section.Kind),
            };

            if (behavior == SidebarEmptyBehavior.ActionCard)
                return EntityRow.Create(new RowSpec
                {
                    Key = section.Id + ":empty",
                    Label = text,
                    Enabled = false,
                    Shape = PaneMetrics.ShapeOf(section),
                    Height = PaneMetrics.RowHeight(section),
                    Glyph = section.Kind == SidebarSectionKind.Concerts ? Icons.Calendar : Icons.Grid,
                });

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

        /// <summary>The ACTIONABLE degraded state (W21: the planner tests it FIRST, so a Pending source that needs a prompt
        /// never shows a skeleton). Concerts with no location points at the hub; an unresolvable contribution offers "Manage
        /// extension" (the customizer) with the binder's reason on a second line, which is what makes it 56 instead of 48.</summary>
        Element PromptCard(SidebarSectionSpec section)
        {
            bool concerts = section.Kind == SidebarSectionKind.Concerts;
            string label = Loc.Get(concerts ? PaneLoc.ConcertsPrompt : PaneLoc.ExtensionManage);
            string? reason = concerts ? null : ExtensionReason(section.Id);
            // Explicit locals, never a ternary against null: a method group has no natural type in that position.
            Action? click = null;
            if (concerts) click = () => _o.Navigate("concerts", null);
            else if (_o.Config.OnCustomize is not null) click = _o.OpenCustomizer;

            var titleText = new TextEl(label)
            {
                Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            Element[] lines = reason is { Length: > 0 } why
                ? [titleText, global::Wavee.Design.Type.MicroMeta(why) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                : [titleText];

            return new BoxEl
            {
                Key = section.Id,
                Direction = 0,
                Height = SidebarRowGeometry.PromptHeight(reason is { Length: > 0 }),
                AlignItems = FlexAlign.Center,
                Gap = Spacing.S,
                // Vertical only — the pane owns the horizontal inset.
                Margin = new Edges4(0f, Spacing.XXS, 0f, Spacing.XXS),
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                Corners = Radii.CardAll,
                Shadow = Elevation.Card,
                Role = click is null ? AutomationRole.None : AutomationRole.Button,
                Cursor = click is null ? CursorId.Arrow : CursorId.Hand,
                Focusable = click is not null,
                OnClick = click,
                Children =
                [
                    Cover.Glyph(concerts ? Icons.Calendar : Icons.Settings, Cover.S28),
                    new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.XXS, Children = lines },
                    Icon(Icons.ChevronRight, 10f, Tok.TextTertiary),
                ],
            }.Interactive(Interaction.Card);
        }

        /// <summary>Why a contributed section cannot render, as prose. The binder's availability verdict is the authority —
        /// this never inspects an extension id (the forward-compat guardrail).</summary>
        static string? ExtensionReason(string sectionId)
            => (Sidebar.Binder?.AvailabilityOf(sectionId) ?? SidebarContributionAvailability.Missing) switch
            {
                SidebarContributionAvailability.Live or SidebarContributionAvailability.Cached => null,
                SidebarContributionAvailability.Missing => Loc.Get(PaneLoc.ExtensionMissing),
                _ => Loc.Get(PaneLoc.ExtensionNotNow),
            };

        // ── multi-select + the folder "+" ────────────────────────────────────────────────────────────────────────────

        /// <summary>Turn a PlaylistTree row into a MULTI-SELECTABLE one (W17). <c>OnActivate</c> REPLACES <c>OnClick</c>
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
                // The row owns its own left inset (the 31-px depth ladder), so the lane adds none.
                leftMargin: 0f);
        }

        /// <summary>A folder row's trailing slot: the quiet count (when the tree asks for counts), then the folder's own "+"
        /// (20 box, glyph 12) — the count is the fact, the "+" the verb. The "+" is keyed inside a row keyed by the entry,
        /// so it remounts when this slot recycles onto another folder, which is what makes capturing the folder id in its
        /// factory safe.</summary>
        Element? FolderTrailing(SidebarSectionSpec section, in SidebarLibraryEntry entry, string folderId, bool rootlistItem)
        {
            Element? badge = section.Kind == SidebarSectionKind.PlaylistTree && section.Opts.CountBadges
                ? Counts.Badge(entry.ChildCount)
                : null;
            if (!rootlistItem || folderId.Length == 0 || _o.Acts is null) return badge;

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
                box: 20f, glyph: 12f)) with { Key = "folder-create" };
            if (badge is null) return plus;
            return new BoxEl
            {
                Direction = 0, Gap = 2f, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children = [badge, plus],
            };
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

        static Element? LeadingArt(SidebarSectionSpec section, in SidebarLibraryEntry entry, SidebarItemSpec? item)
        {
            if (!section.Opts.Artwork) return null;
            float size = PaneMetrics.ArtSize(section);
            // An authored icon override beats the artwork slot: it is the user's explicit choice for this row.
            if (item?.IconOverride is { Length: > 0 } name)
                return Cover.Glyph(RowGlyphs.Glyph(name, Icons.MusicNote), size,
                    entry.Circular || entry.Kind == SidebarEntryKind.Artist);
            // A concert is projected as a route stamped with its event date, and wants that date, not a glyph tile.
            if (section.Kind == SidebarSectionKind.Concerts && entry.SortStamp > 0)
                return PaneText.DateBlock(entry.SortStamp, size);
            return Cover.ForEntry(in entry, size);
        }

        /// <summary>A FEED row's trailing slot: a tree playlist's quiet count (CountBadges only) or a new release's age badge
        /// ("3d"). The equalizer is the row primitive's own slot; route counts are <see cref="CountBadge"/>'s.</summary>
        static Element? TrailingBadge(SidebarSectionSpec section, in SidebarLibraryEntry entry)
        {
            if (section.Kind == SidebarSectionKind.PlaylistTree && section.Opts.CountBadges && entry.Kind == SidebarEntryKind.Playlist)
                return Counts.Badge(entry.ChildCount);
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
        static Element? CountBadge(SidebarSectionSpec section, string routeKey)
        {
            if (!section.Opts.CountBadges || ShortcutCount.KindOf(routeKey) is not { } kind) return null;
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
            return ZStack(DropPlate(), row, Embed.Comp(() => new SelectionPill(owner, probe)), InsertionLine());
        }

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
        DropTargetSpec? PinSpec(SidebarSectionSpec section, string sectionId, int index, Action? onSpringLoad = null)
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
