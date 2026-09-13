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
    /// recycle. The chevrons, the pill, the "+" and the drop zone are hook-owning CHILDREN, and each row kind has its own
    /// recycle pool (<c>ContentType</c> = row kind), so a header slot never rebinds into a chevron-less shape.</para>
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
                SidebarRowKind.SectionHeader => HeaderRow(section, in row, index, rows),
                SidebarRowKind.HeaderLabel => Banded(SectionHeader.Label(PaneText.TitleOf(section)), index, rows),
                // Only a Divider SECTION plans this row; ordinary section joins are whitespace, never implicit rules.
                SidebarRowKind.Divider => SectionHeader.ExplicitDivider(),
                SidebarRowKind.IconRow or SidebarRowKind.EntityRow or SidebarRowKind.Placeholder
                    => ItemOrEntity(section, in row, sel, index),
                SidebarRowKind.FolderHeader => FolderRow(section, in row, index),
                SidebarRowKind.GridStrip => GridStripRow(section, in row, sel),
                SidebarRowKind.Empty => EmptyRow(section),
                SidebarRowKind.Skeleton => Skeletons.Row(index, section.Opts.Density, section.Opts.Subtitles,
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
            return content;
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

        /// <summary>The 28-DIP section header inside its rhythm band. The trailing slot can carry, in order: an editable
        /// EntityList's sort/view trigger, a PlaylistTree's "+" (its ONLY create affordance — gated on a CONFIG flag, never
        /// on the design, because V3's own chrome carries its own "+"), and — on the FIRST header only — the quick layout
        /// menu, which must never be crowded out. An editable EntityList with <c>InlineControls</c> hangs its chip strip
        /// under the header INSIDE the band (header chrome, never a virtualized row).</summary>
        Element HeaderRow(SidebarSectionSpec section, in SidebarRow row, int index, IReadOnlyList<SidebarRow> rows)
        {
            string id = section.Id;
            bool editable = !_o.Config.ReadOnly;
            var owner = _o;

            Element? sort = null, create = null, layout = null;
            if (editable && section.Kind == SidebarSectionKind.EntityList)
                sort = InlineControls.SortTrigger(_o, section);
            // Every delegate reads LIVE pane state: a header slot recycles across sections.
            if (section.Kind == SidebarSectionKind.PlaylistTree && _o.Config.HeaderCreate && _o.Config.OnCreatePlaylist is not null)
                create = Embed.Comp(() => new CreateButton(
                    owner.CreatePlaylist,
                    menu: owner.CreateMenu,
                    drop: owner.HeaderCreateDropSpec(),
                    dropActive: () => owner.HeaderCreateDropActive.Value)) with { Key = "tree-create" };
            if (string.Equals(_o.MenuHostSectionId, row.SectionId, StringComparison.Ordinal))
                layout = LayoutMenu.Button(24f);

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
                chevron = Chevron.Section(_headerOpen ??= HeaderOpenLive, identity: _sectionIdentity ??= SectionIdentity);
            }

            Element header = SectionHeader.Header(PaneText.TitleOf(section), !section.Collapsed, toggle,
                Cluster(sort, create, layout), chevron);
            if (!editable || section.Kind != SidebarSectionKind.EntityList || !section.Opts.InlineControls || section.Collapsed)
                return Banded(header, index, rows);
            return Banded(new BoxEl
            {
                Direction = 1, Gap = 4f,
                Children = [header, InlineControls.Chips(_o, section)],
            }, index, rows);
        }

        static Element? Cluster(Element? a, Element? b, Element? c)
        {
            int n = (a is null ? 0 : 1) + (b is null ? 0 : 1) + (c is null ? 0 : 1);
            if (n == 0) return null;
            if (n == 1) return a ?? b ?? c;
            var kids = new Element[n];
            int k = 0;
            if (a is not null) kids[k++] = a;
            if (b is not null) kids[k++] = b;
            if (c is not null) kids[k++] = c;
            return new BoxEl { Direction = 0, Gap = 2f, AlignItems = FlexAlign.Center, Children = kids };
        }

        /// <summary>SECTION RHYTHM: a header band carries 8 DIP of air above and 2 below. It is PADDING on a wrapper, never a
        /// margin on the header, so the variable-extent layout's measured height stays honest and scroll anchoring cannot
        /// drift. The air above is suppressed THREE ways — the pane's first row, directly after a Divider, and directly
        /// after a bare HeaderLabel — because the last two already supply the gap.</summary>
        static Element Banded(Element header, int index, IReadOnlyList<SidebarRow> rows)
        {
            float top = PaneMetrics.SectionGap;
            if (index <= 0) top = 0f;
            else if (rows[index - 1].Kind is SidebarRowKind.Divider or SidebarRowKind.HeaderLabel) top = 0f;
            return new BoxEl
            {
                Direction = 1, Shrink = 0f,
                Padding = new Edges4(0f, top, 0f, PaneMetrics.HeaderBodyGap),
                Children = [header],
            };
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
            // An unresolved ENTITY (a real name is coming) stays dimmed from its uri instead — honest, never blank.
            bool routePin = !named && entry.Kind == SidebarEntryKind.AppRoute && route is { Length: > 0 };
            string label = item?.LabelOverride is { Length: > 0 } alias ? alias
                : named ? entry.Name
                : routePin ? Shell.Dest(Shell.Parse(route!)).Title
                : PaneText.ShortUri(entry.Uri);
            // Resolved pane-side by the SAME rule the selection sweep uses, so the row that draws the plate and the row
            // whose epoch got bumped can never disagree.
            bool selected = _o.RowSelectsRoute(index, sel);
            bool reordering = _o.TryBandOf(index, out _);
            var (playing, animated) = _o.RowPlayState(index);
            float height = PaneMetrics.RowHeight(section);
            // TreeLeading == StandardLeading at depth 0, so the only reason to take the tree path is the connector guides a
            // deeper row draws — and depth only exists where a folder does. A folder-free tree renders flush.
            bool treeNode = section.Kind == SidebarSectionKind.PlaylistTree && _o.SectionHasFolder(row.SectionId);
            int treeDepth = treeNode ? entry.Depth : 0;
            int baseDepth = treeNode ? Math.Max(0, row.Depth - treeDepth) : row.Depth;

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
                Subtitle = section.Opts.Subtitles ? PaneText.SubtitleOf(in snapshot) : null,
                Selected = selected,
                Enabled = named || track || routePin,
                Depth = baseDepth,
                TreeNode = treeNode,
                TreeDepth = treeDepth,
                TreeContinuationMask = treeNode ? TreeMaskOf(row.SectionId, index, treeDepth) : (byte)0,
                Density = section.Opts.Density,
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
            Element built = EntityRow.Create(in spec);
            if (track) built = EntityRow.WithPlayTrackHint(built);
            // Tree connectors own their depth lanes; the selection pill stays in the row's base gutter.
            return Indicator(built, selected, baseDepth, height, route);
        }

        /// <summary>A rootlist FOLDER: entity-row geometry, the folder mark, and the disclosure chevron in the TRAILING
        /// cluster (W7). A folder never navigates; by default it toggles its expansion, and a mode may reroute that gesture
        /// through <c>Config.ActivateFolder</c> (V3's narrow drill-in) — click and menu verb take the same path.</summary>
        Element FolderRow(SidebarSectionSpec section, in SidebarRow row, int index)
        {
            var entries = _o.Plan.Entries;
            if ((uint)row.EntryIndex >= (uint)entries.Count) return Nothing;
            var entry = entries[row.EntryIndex];
            string folderId = entry.FolderId;
            bool expanded = Sidebar.IsFolderExpanded(folderId);
            float height = PaneMetrics.RowHeight(section);

            // A synced folder pin the rootlist no longer carries renders visible-but-disabled, never vanishes.
            if (entry.Missing) return MissingFolderRow(section, in entry, in row, height);

            // W7: the same tree test EntryRow uses; TreeDepth is the row's OWN tree depth, so a pinned folder sits flush
            // with its pinned siblings instead of marching right by its rootlist nesting.
            bool treeNode = section.Kind == SidebarSectionKind.PlaylistTree;
            int treeDepth = treeNode ? entry.Depth : 0;
            int baseDepth = treeNode ? Math.Max(0, row.Depth - treeDepth) : row.Depth;
            var snapshot = entry;
            float art = PaneMetrics.ArtSize(section);

            Action activate = () => _o.ActivateFolder(folderId, snapshot.Name, index);
            var menu = _o.FolderMenu(section, index, in snapshot, activate, expanded, row.Key);
            bool reordering = _o.TryBandOf(index, out _);
            Action? rename = reordering ? null : _o.RenameAction(in snapshot);
            bool rootlistItem = treeNode && !reordering;
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
                Label = entry.Name.Length > 0 ? entry.Name : PaneText.ShortUri(entry.Id),
                Subtitle = section.Opts.Subtitles ? Strings.Sidebar.V3.ItemCount(entry.ChildCount) : null,
                Depth = baseDepth,
                TreeNode = treeNode,
                TreeDepth = treeDepth,
                TreeContinuationMask = treeNode ? TreeMaskOf(row.SectionId, index, treeDepth) : (byte)0,
                Density = section.Opts.Density,
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
            // No pill (a folder has no route), but both drop cues: the bottom band of an expanded header IS the "first
            // child" slot, and the whole outdent gesture happens on folder rows.
            return DropCueOverlay(EntityRow.Create(in spec));
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
                Density = section.Opts.Density,
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

        /// <summary>Which connector columns continue below a realized tree row. The plan is preorder, so the first later tree
        /// entry at or above a level decides it: equal ⇒ a sibling continues; lower ⇒ the branch ended. Renderer-side
        /// because it is visual chrome, not document/query semantics.</summary>
        byte TreeMaskOf(string sectionId, int index, int depth)
        {
            int levels = Math.Clamp(depth, 0, 4);
            if (levels == 0) return 0;
            int unresolved = (1 << levels) - 1;
            int mask = 0;
            var plan = _o.Plan;
            var rows = plan.Rows;
            var entries = plan.Entries;
            for (int i = index + 1; i < rows.Count && unresolved != 0; i++)
            {
                var next = rows[i];
                if (!string.Equals(next.SectionId, sectionId, StringComparison.Ordinal)) break;
                if (next.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader)) break;
                if ((uint)next.EntryIndex >= (uint)entries.Count) break;
                int nextDepth = entries[next.EntryIndex].Depth;
                for (int level = 1; level <= levels; level++)
                {
                    int bit = 1 << (level - 1);
                    if ((unresolved & bit) == 0 || nextDepth > level) continue;
                    if (nextDepth == level) mask |= bit;
                    unresolved &= ~bit;
                }
            }
            return (byte)mask;
        }

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
                Density = section.Opts.Density,
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
            return Indicator(EntityRow.Create(in spec), selected, 0, height, key);
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
                Density = section.Opts.Density,
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
            return EntityRow.WithPlayTrackHint(EntityRow.Create(in spec));
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
                Density = section.Opts.Density,
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
                Density = section.Opts.Density,
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

        /// <summary>The <c>EntityEmbed</c> spotlight card: cover left (circular for artists), title + subtitle, and — with
        /// <c>Display.PlayButton</c> — a circular play button revealed on hover that plays the entity AS A CONTEXT. Clicking
        /// anywhere else navigates. An unresolved entity is still a card: 0.55 opacity, disabled, no click, no menu, from the
        /// item's cached title/art, play hidden.</summary>
        Element HeroCard(SidebarSectionSpec section, in SidebarRow row, string sel, int index)
        {
            var item = PaneText.ItemOf(section, row.Key);
            var entries = _o.Plan.Entries;
            bool resolved = (uint)row.EntryIndex < (uint)entries.Count;
            var entry = resolved ? entries[row.EntryIndex] : default;
            float height = PaneMetrics.CardHeight(section);
            float cover = PaneMetrics.CardCover(section);

            string title = item?.LabelOverride is { Length: > 0 } cardAlias ? cardAlias
                : resolved && entry.Name.Length > 0 ? entry.Name
                : item?.FallbackTitle is { Length: > 0 } cardCached ? cardCached
                : PaneText.ShortUri(row.Key);
            string? subtitle = resolved ? PaneText.SubtitleOf(in entry) : Loc.Get(PaneLoc.MissingEntity);
            bool circular = resolved
                ? entry.Circular || entry.Kind == SidebarEntryKind.Artist
                : item?.EntityKind == SidebarEntityKind.Artist;
            // ForEntry for a resolved card: it owes the entry's kind dispatch (folder tile, route glyph, Liked's cover).
            Element art = resolved
                ? Cover.ForEntry(in entry, cover)
                : Cover.ArtUrl(item?.FallbackImageUrl, row.Key, cover, circular);

            string uri = resolved ? entry.Uri : "";
            bool track = resolved && entry.IsTrack;
            string? route = resolved ? entry.RouteKey : SidebarPinId.FromUri(uri);
            bool selected = _o.RowSelectsRoute(index, sel);
            var (playing, animated) = _o.RowPlayState(index);
            bool canPlay = resolved && section.Opts.PlayButton && uri.Length > 0 && entry.IsPlayable;
            var snapshot = entry;

            Action? activate = null;
            if (track) activate = () => _o.Play(uri, asTrack: true);
            else if (route is { Length: > 0 } cardRoute) activate = () => _o.Navigate(cardRoute, title, in snapshot);

            var titleText = new TextEl(title)
            {
                Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            Element[] lines = subtitle is { Length: > 0 } sub
                ? [titleText, new TextEl(sub) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                : [titleText];
            var text = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 2f, Children = lines };
            Element[] children = canPlay
                ? [art, text, PlayButton(uri, playing && animated, section.Opts.Density)]
                : [art, text];

            var card = new BoxEl
            {
                Key = row.Key,
                Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = 12f,
                Padding = new Edges4(8f, 0f, 8f, 0f),
                Corners = CornerRadius4.All(Radii.Card),
                Fill = selected ? global::Wavee.Design.Colors.SelectedRest : Tok.FillCardSecondary,
                HoverFill = selected ? global::Wavee.Design.Colors.SelectedHover : Tok.FillSubtleSecondary,
                PressedFill = selected ? global::Wavee.Design.Colors.SelectedPressed : Tok.FillSubtleTertiary,
                BorderWidth = selected ? 2f : 1f,
                BorderColor = selected ? Tok.AccentDefault : Tok.StrokeCardDefault,
                // The pane owns the horizontal inset; the card contributes only its vertical breathing room.
                Margin = new Edges4(0f, 2f, 0f, 2f),
                Opacity = resolved ? 1f : 0.55f,
                IsEnabled = resolved,
                Cursor = activate is null ? CursorId.Arrow : CursorId.Hand,
                OnClick = resolved ? activate : null,
                Children = children,
            };
            if (resolved && _o.EntryMenu(section, index, in snapshot, item, row.Key) is { } menu)
                card = card.WithContextMenu(_o.MenuOverlay, menu);
            return card;
        }

        /// <summary>The card's circular play button: 28 at Compact, 32 otherwise; revealed on hover, and PINNED at opacity 1
        /// with the Pause glyph while this card is the playing context. On-accent ink is the token, never a literal white.</summary>
        Element PlayButton(string uri, bool playingNow, SidebarDensity density)
        {
            float box = density == SidebarDensity.Compact ? 28f : 32f;
            var owner = _o;
            return new BoxEl
            {
                Opacity = playingNow ? 1f : 0f, HoverOpacity = 1f, Shrink = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Width = box, Height = box, Shrink = 0f,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = Radii.Circle(box),
                        Fill = Tok.AccentDefault,
                        Role = AutomationRole.Button, Cursor = CursorId.Hand,
                        OnClick = () => owner.Play(uri, asTrack: false),
                        Children = [Icon(playingNow ? Icons.Pause : Icons.Play, 14f, Tok.TextOnAccentPrimary)],
                    }.Interactive(Interaction.Subtle),
                ],
            };
        }

        // ── grid strips ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One row of a Grid-presentation section: <c>ItemCount</c> cells from <c>Entries[EntryIndex..]</c>.
        /// <para>THE COLUMN COUNT IS THE PLANNER'S. Re-deriving it from width here disagreed with how the planner already
        /// sliced the entries (a ragged grid whose row rhythm changed with the pane). Since the 180-DIP floor (#84) the planned
        /// count can starve cells below the 40-DIP art floor, so this only WRAPS fewer cells per visual line
        /// (<c>SidebarRowGeometry.GridFallbackColumns</c>): the strip grows taller, its cells never narrower. The pane width
        /// is SUBSCRIBED so the cell edge re-flows with the seam.</para></summary>
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

        Element GridCell(SidebarSectionSpec section, SidebarLibraryEntry entry, float edge, string sel)
        {
            // A cell is not a plan row (one strip draws several), so it asks the resolver about the ENTRY — the same
            // predicate the row-level sweep ORs across the strip's range.
            bool selected = SidebarRowResolve.EntrySelects(in entry, sel);
            string label = entry.Name.Length > 0 ? entry.Name : PaneText.ShortUri(entry.Uri);
            float artEdge = MathF.Max(Cover.S40, edge - Spacing.S);
            string? route = entry.RouteKey;

            var labelText = new TextEl(label)
            {
                Size = 12f, Weight = (ushort)(selected ? 600 : 400), Color = selected ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            // ForEntry, never the raw cover factory: an app-route entry keeps its glyph tile and Liked its dynamic cover.
            Element cover = Cover.ForEntry(in entry, artEdge);
            Element[] kids = section.Opts.Subtitles && PaneText.SubtitleOf(in entry) is { Length: > 0 } sub
                ? [cover, labelText, new TextEl(sub) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                : [cover, labelText];

            Action? click = null;
            if (entry.IsTrack) click = () => _o.Play(entry.Uri, asTrack: true);
            else if (route is { Length: > 0 } cellRoute) click = () => _o.Navigate(cellRoute, entry.Name, in entry);

            var cell = new BoxEl
            {
                Key = entry.Id,
                Direction = 1, Width = edge, Shrink = 0f, Gap = Spacing.XS,
                Padding = Edges4.All(Spacing.XS),
                Corners = Radii.CardAll,
                Shadow = Elevation.Card,
                Cursor = click is null ? CursorId.Arrow : CursorId.Hand,
                OnClick = click,
                Children = kids,
            }.Interactive(Interaction.Card);
            // AFTER Interactive: the recipe rewrites the border wholesale.
            cell = cell with
            {
                BorderColor = selected ? Tok.AccentDefault : Tok.StrokeCardDefault,
                BorderWidth = selected ? 2f : 1f,
            };
            if (_o.GridCellMenu(in entry) is { } menu) cell = cell.WithContextMenu(_o.MenuOverlay, menu);
            return cell;
        }

        // ── degraded + affordance rows ───────────────────────────────────────────────────────────────────────────────

        /// <summary>A section that resolved to ZERO rows — FOUR arms. Pinned runs FIRST and unconditionally: its empty state IS
        /// the drop target, and an authored HideBody must never delete the one surface that teaches drop-to-pin. HideBody ⇒ a
        /// literal blank (the header stays; V3's own actionable state is then the only empty message on screen). ActionCard ⇒
        /// a disabled row at the section's own row height. Otherwise the quiet 32-DIP 11px tertiary hint, per-kind copy; a
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
                    Density = section.Opts.Density,
                    Height = PaneMetrics.RowHeight(section),
                    Glyph = section.Kind == SidebarSectionKind.Concerts ? Icons.Calendar : Icons.Grid,
                });

            return new BoxEl
            {
                Height = PaneMetrics.EmptyHintHeight, AlignItems = FlexAlign.Center,
                Padding = PaneMetrics.RowInset,
                Children =
                [
                    new TextEl(text) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
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
                ? [titleText, new TextEl(why) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
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
                // The row owns its own left inset (depth ladder + selection gutter), so the lane adds none.
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
                return new TextEl(age) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1 };
            return null;
        }

        /// <summary>The library-shortcut count through the ONE quiet badge (never an accent pill). Albums / Artists / Liked /
        /// Podcasts read the signed-in account's library edges — the server total while a relation is still paging, so the
        /// number does not climb — and an UNKNOWN relation is the 20×12 pending plate. Local files carries no count. Reading
        /// the relation's publish signal is what re-renders this row when the count moves.</summary>
        static Element? CountBadge(SidebarSectionSpec section, string routeKey)
        {
            if (!section.Opts.CountBadges) return null;
            LibraryEdgeKind kind;
            switch (routeKey)
            {
                case "albums": kind = LibraryEdgeKind.SavedAlbums; break;
                case "artists": kind = LibraryEdgeKind.FollowedArtists; break;
                case "liked": kind = LibraryEdgeKind.Liked; break;
                case "podcasts": kind = LibraryEdgeKind.SavedShows; break;
                default: return null;
            }
            var relation = User.Relation(kind);
            _ = relation.Changed.Value;
            var me = User.Me;
            return me.State(kind) == EdgeState.Unknown ? Counts.Badge(null) : Counts.Badge(relation.Total(me.Slot));
        }

        /// <summary>Attach the item-owned selection pill and both drop cues. Shape-stable for recycling: a slot never
        /// inherits an animated transform from the route it represented one window earlier. The ZStack order is load-bearing
        /// — plate UNDER the row (text never tinted), pill over it, insertion line on top.</summary>
        Element Indicator(Element row, bool selected, int depth, float height, string? route)
        {
            if (!float.IsFinite(height) || height <= 0f) height = SidebarRowGeometry.ClassicHeight;
            _pillState = new SidebarPillState(
                Route: route,
                Selected: selected,
                Indent: SidebarRowGeometry.IndentFor(depth),
                Top: MathF.Max(0f, (height - SelectionPill.PillH) * 0.5f));
            var owner = _o;
            Func<SidebarPillState> probe = _pillProbe ??= PillState;
            return ZStack(DropPlate(), row, Embed.Comp(() => new SelectionPill(owner, probe)), InsertionLine());
        }

        /// <summary>A row with no selection pill that still owns both drop cues (a folder header).</summary>
        Element DropCueOverlay(Element row) => ZStack(DropPlate(), row, InsertionLine());

        /// <summary>THE "INTO" PLATE (W16): mounted once per row, ALWAYS, as the ZStack's first child — accent@0.18 with a
        /// 1-DIP accent border, radius 4, auto-sized to the row's rect, out of hit-testing so hover and the drop reach the
        /// row. It left the row itself because bindings wire at MOUNT only: a thunk built there captured the row's state as
        /// values and a same-keyed re-render kept the stale one. Every thunk reads the LIVE slot index + row epoch.</summary>
        Element DropPlate() => new BoxEl
        {
            Key = "drop-plate",
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
