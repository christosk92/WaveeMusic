// ── Themes/Classic/Structure/Views/Nav/WinUiNavRow.cs — Classic's navigation rows (themes plan 6.1, T5a) ──────────────────
//
// From `Shell/Sidebar.UI.Slot.cs` (`PaneSlot`): the pane's bound row slot, ONE Component per ItemsView slot, and the whole
// plan-row vocabulary behind it (section headers, route rows, entity and folder rows, rail tiles, grid strips, the empty hint,
// the tree's closing gutter, the drop band, the selection pill and the drop cues). The core pane keeps planning, publishing and
// the per-row epochs; this is what one row looks like. It reaches the pane only through `INavRowHost`.
//
// The row primitives it builds from are Classic's (`WinUiEntityRow`, `WinUiSelectionPill`, `WinUiSkeletons`: `WinUiNavEntityRow.cs`,
// `WinUiNavRowParts.cs`, T5f). What stays in `Shell/Sidebar.UI.Rows.cs` is shared with the pages: `Sidebar.Cover`, `Sidebar.Chevron`, the
// pane text, loc and glyph tables. T5b gave the structure the heads: the section header, the separator, the "+" and the quiet count are
// `WinUiNavHead.cs`, the filter box is `WinUiNavFilter.cs`; T5f the grid tile (`WinUiNavGridCell.cs`). The menus' rows and the drop
// specs are the host's (core decides what a menu holds and where a drop lands).
//
// Numbers are `Rules\WinUiNavGeometry.cs` (the Classic ladder) -- WVTH003 allows no literal look here.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>ONE bound slot of the pane's plan list. <c>ItemsView.CreateBound</c> builds each slot once and recycles it
/// by writing <c>scope.Index</c>; Render reads that signal plus THIS row's epoch (<see cref="INavRowHost.SubscribeRowEpoch"/>,
/// bumped by a publish / selection sweep / play-state sweep only for the rows that changed), so a heterogeneous plan
/// re-skins exactly the realized rows it touches — never the list.
///
/// <para>NO HOOKS anywhere in this class: every builder is a plain method, so hook order is identical across every
/// recycle. The chevrons, the pill, the "+" and the grid-tile <c>Controls.Surface</c>s are hook-owning CHILDREN, and each
/// row kind has its own recycle pool (<c>ContentType</c> = row kind), so a header slot never rebinds into a
/// chevron-less shape. A surface is PROPS-DRIVEN (<c>Embed.Comp(props, factory)</c> re-pushes the new <c>CardData</c>
/// on every rebind), which is what makes it legal under a recycling slot: no constructor argument freezes at mount
/// (the adapter is WinUiNavGridCell.cs).</para>
///
/// <para>Rows never read playback or the route signal themselves: the pane reads both ONCE on behalf of every row
/// (<see cref="INavRowHost.RowPlayState"/>, <see cref="INavRowHost.SelectedRoutePeek"/>).</para></summary>
internal sealed class WinUiNavRow : Component
{
    readonly INavRowHost _o;
    readonly RowScope _scope;

    // Live-state probes handed to hook-owning children and bound props. They capture only THIS slot (never a section
    // id, an index or a height): child ctor args and bindings freeze at MOUNT while the slot recycles every scroll.
    // Cached so a re-render allocates no new delegate for them.
    Func<bool>? _folderOpen;
    Func<int>? _folderIdentity;
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

    readonly WinUiNavMetrics _m;

    public WinUiNavRow(INavRowHost owner, RowScope scope, WinUiNavMetrics metrics) { _o = owner; _scope = scope; _m = metrics; }

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
            SidebarRowKind.SectionHeader => HeaderRow(section, index, sel),
            // Only a Divider SECTION plans this row; ordinary section joins are whitespace, never implicit rules.
            SidebarRowKind.Divider => WinUiNavHead.Separator(),
            SidebarRowKind.IconRow => RouteRow(section, row.Key, sel, index),
            SidebarRowKind.EntityRow => (uint)row.EntryIndex < (uint)_o.Plan.Entries.Count
                ? EntryRow(section, _o.Plan.Entries[row.EntryIndex], in row, sel, index) : Nothing,
            SidebarRowKind.FolderHeader => FolderRow(section, in row, index, sel),
            SidebarRowKind.Empty => EmptyRow(),
            SidebarRowKind.Skeleton => WinUiSkeletons.Row(OneLine),
            SidebarRowKind.TreeEnd => TreeEndRow(in row, index),
            SidebarRowKind.DropBand => DropBandRow(section, index),
            _ => Nothing,
        };

        // In-place reorder: the Reorderable owns the row's drag source, keyboard lift and position track — which is why
        // a banded row carries no Drag payload, no Animate, no OnRename/OnMove of its own.
        if (ReorderBand(in row, index) is { } pair)
            content = pair.Ro.Item(index - pair.Start, FillSlot(content), key: row.Key, transition: _o.RowPlacement);
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

    /// <summary>The one row shape WinUI draws (the board has no two-line or cover-32 row), whatever the layout document says of a section.</summary>
    const SidebarRowShape OneLine = SidebarRowShape.EntityOneLine;

    /// <summary>FILL THE SLOT. <c>Reorderable.Item</c>'s wrapper is a flex ROW, so a row without Grow arranges at its
    /// measured content width and its hover/selected plate drew narrower than its neighbours'. A BoxEl content (an
    /// entity row, the ZStack indicator) takes the fill here; a tooltip-wrapped ComponentEl already carries it through
    /// <c>ToolTip.Wrap(grow: 1f)</c> (the reconciler mirrors a component anchor's grow), so it is never applied twice.
    /// MinWidth 0 keeps a long title eliding instead of pushing the row past the pane.</summary>
    static Element FillSlot(Element content)
        => content.Is(out BoxEl box) ? box with { Grow = 1f, Shrink = 1f, MinWidth = 0f } : content;

    (Reorderable Ro, int Start)? ReorderBand(in SidebarRow row, int index)
    {
        if (row.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.IconRow or SidebarRowKind.FolderHeader)) return null;
        if (!_o.TryBandOf(index, out var band)) return null;
        return (_o.ReorderFor(band.SectionId), band.Start);
    }

    // ── section chrome ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 40-px section head (NavigationViewItemHeader, the board's `Shell-Nav`): the title on the bottom line, the "+" on Playlists (the
    /// pane's one create flow, <see cref="INavRowHost.CreatePlaylist"/>). No chevron and no ⋯ button: a section never collapses here, and its options
    /// are the header's right-click menu (<see cref="INavRowHost.HeaderMenu"/>). An open Playlists filter adds its box under the head. The head is the
    /// pill's section anchor (the one-pill rule's third arm), so the pill is drawn here too.</summary>
    Element HeaderRow(SidebarSection section, int index, string sel)
    {
        string id = section.Id;
        var owner = _o;

        // Every delegate reads LIVE pane state: a header slot recycles across sections.
        Element? create = null;
        if (section.Kind == SidebarSectionKind.Playlists)
            create = new BoxEl
            {
                Shrink = 0f, Margin = new Edges4(0f, 0f, 0f, -_m.HeaderSink),
                Children =
                [
                    Embed.Comp(() => new WinUiNavCreateButton(
                        owner.CreatePlaylist, menu: owner.CreateMenu, drop: owner.HeaderCreateDropSpec(),
                        dropActive: () => owner.HeaderCreateDropActive.Value,
                        box: _m.HeaderButton, glyph: WinUiNavGeometry.HeaderPlusGlyph)) with { Key = "tree-create" },
                ],
            };

        BoxEl head = WinUiNavHead.Header(WinUiNavText.TitleOf(section), create);
        if (_o.MenuOverlay is { } svc && _o.HeaderMenu(id) is { } menu) head = head.WithContextMenu(svc, menu);
        Element header = head;
        if (_o.FilterOpenFor(id)) header = new BoxEl { Direction = 1, Children = [header, FilterBox()] };

        // The header's own pill: centred on the 40-px title band (a header has no margin, so no RowMarginY).
        _pillState = new SidebarPillState(_o.PillRouteOf(index, sel), _o.RowSelectsRoute(index, sel), 0f,
            (WinUiNavGeometry.HeaderHeight - WinUiNavGeometry.PillH) * 0.5f);
        Func<SidebarPillState> probe = _pillProbe ??= PillState;
        return ZStack(header, Embed.Comp(() => new WinUiSelectionPill(owner, probe)));
    }

    /// <summary>The transient Playlists filter (design V.11), under an open filter's header: the pane's query, focus ticket and close
    /// verb are <see cref="INavRowHost"/>'s, the field is <see cref="WinUiNavFilterBox"/>.</summary>
    Element FilterBox() => Embed.Comp(() => new WinUiNavFilterBox(_o)) with { Key = "pane-filter" };

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
        return _o.DisclosureOpen(folderId, folder: true, fallback: _o.Model.IsFolderExpanded(folderId));
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

    /// <summary>The rootlist rows (<see cref="NavDropRules.IsRootlistSection"/>): the Playlists section, and Your Library while
    /// it is the Playlists · Custom order list.</summary>
    bool RootlistSection(SidebarSection section) => NavDropRules.IsRootlistSection(section.Kind, _o.TreeSortedNonCustom);

    // ── item / entity rows ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A projected entry: a playlist / album / artist / show / track / app route the projection knows, or a pin.</summary>
    Element EntryRow(SidebarSection section, in SidebarLibraryEntry entry, in SidebarRow row, string sel, int index)
    {
        bool named = entry.Name.Length > 0;
        bool compact = _o.CompactPlan;
        bool track = entry.IsTrack;
        string? route = entry.RouteKey;
        bool routeEntry = entry.Kind == SidebarEntryKind.AppRoute;
        // A route pin, Liked Songs and a folder wear a 16 glyph; every other entity a 16 thumbnail (the board's two leading forms).
        bool glyphRow = WinUiNavArt.IsGlyph(in entry);
        // The label, its pin state and whether it is a stand-in are the model's (NavRowFacts.LabelOf): a PIN is never a blank
        // row (design D9), an authoritative miss keeps the last-known name dimmed with the warning mark (design Q9), and a
        // non-pin row keeps the identity gate (Trap 5) on its uri fallback.
        var labelFacts = _o.Model.LabelOf(in entry);
        bool unavailable = labelFacts.PinState == SidebarPinState.Unavailable;
        // S9: an offline pin keeps its last title and cover, quieted, and wears the offline mark where the Unpin button sat.
        bool offline = labelFacts.PinState == SidebarPinState.Offline;
        string label = labelFacts.Label;
        bool placeholderTitle = labelFacts.PlaceholderTitle;
        // Resolved pane-side by the SAME rule the selection sweep uses, so the row that draws the plate and the row
        // whose epoch got bumped can never disagree.
        bool selected = _o.RowSelectsRoute(index, sel);
        bool reordering = _o.TryBandOf(index, out _);
        var (playing, animated) = _o.RowPlayState(index);
        var shape = OneLine;
        float height = _m.RowHeight;

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
        DragPayload? resource = treeRow && entry.Id != SidebarCatalogue.LikedRoute ? _o.PayloadOf(in snapshot, rootlistItem: true) : null;
        DragPayload? drag = null;
        if (!reordering && !track)   // a track is never a pin source (enforced by the kind, not per surface)
            drag = treeRow ? _o.TreeDragPayload(in snapshot) : _o.PayloadOf(in snapshot, rootlistItem);
        // A NON-editable playlist row takes the resource spec too, so it refuses a track deposit WITH a reason; every
        // other kind keeps the pin spec and stays transparent while a drag merely crosses it.
        bool playlistRow = snapshot.Kind == SidebarEntryKind.Playlist;
        DropTargetSpec? drop = playlistRow || resource is not null
            ? _o.ResourceDropSpec(row.SectionId, PinSlot(row.SectionId, index),
                playlistRow && snapshot.CanEdit ? snapshot.Uri : null, snapshot.Name, resource, index,
                isPlaylistRow: playlistRow, rootFacts: TreeRowFacts(index, in snapshot))
            : PinSpec(section, row.SectionId, index);

        var spec = new WinUiRowSpec
        {
            Key = row.Key,
            Label = label,
            Selected = selected,
            Enabled = named || track || routeEntry,
            Depth = IndentOf(in row),
            Tile = compact,
            Shape = shape,
            Height = height,   // UNIFORM per section: a band's slot pitch and the extent table both assume one height
            ArtSize = WinUiNavGeometry.ArtOf(shape),
            Ink = placeholderTitle || unavailable || offline ? Tok.TextTertiary : null,
            Leading = glyphRow ? null : Quieted(WinUiNavArt.Thumb(in snapshot), offline),
            Glyph = glyphRow ? WinUiNavArt.GlyphOf(_o, in snapshot) : null,
            // The warning mark is the unavailable pin's trailing mark, the offline mark the offline pin's, so both show in
            // every row shape.
            Trailing = unavailable ? Icon(Icons.Warning, WinUiNavGeometry.WarningMark, Tok.TextTertiary)
                : offline ? OfflineMark()
                : TrailingBadge(section, in snapshot),
            Playing = playing,
            PlayingAnimated = animated,
            Track = track,
            OnClick = click,
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
            OnRename = rename,
            OnMove = move,
            Drag = drag,
            DropTarget = drop,
        };
        if (treeRow && rootlistItem) ApplyTreeSelection(ref spec, snapshot.Id, click);
        float trailing = unavailable ? WinUiNavGeometry.WarningMark
            : offline ? WinUiNavGeometry.OfflineMarkBox
            : spec.Trailing is not null ? WinUiNavGeometry.CountSlot
            : 0f;
        spec.LabelTooltip = LabelOverflows(label, row.Depth, WinUiNavGeometry.TrailingWidth(playing, false, trailing),
            WinUiNavGeometry.LabelX(shape, glyph: glyphRow));
        Element built = WinUiEntityRow.Create(in spec);
        if (track) built = WinUiEntityRow.WithPlayTrackHint(built);
        // The pill stays at the row's own left edge at every depth, never the drop caret's gutter. An offline pin always
        // carries its reason as the tooltip (with the name when the name is cut).
        string tip = offline ? OfflineTip(label, spec.LabelTooltip || compact) : label;
        return Indicator(Tipped(built, spec.LabelTooltip || offline, tip), selected, IndentOf(in row), height, AnchorRoute(selected, route, sel));
    }

    /// <summary>The offline pin's mark (S9): the cloud-off glyph, 12 in a 24 box, tertiary, in the Unpin button's place.</summary>
    static Element OfflineMark() => new BoxEl
    {
        Width = WinUiNavGeometry.OfflineMarkBox, Height = WinUiNavGeometry.OfflineMarkBox, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [Icon(WinUiNavGeometry.OfflineGlyph, WinUiNavGeometry.OfflineMarkGlyph, Tok.TextTertiary)],
    };

    /// <summary>An offline pin keeps its cover, at <see cref="WinUiNavGeometry.OfflineArtOpacity"/>.</summary>
    static Element? Quieted(Element? cover, bool offline)
        => !offline || cover is null ? cover
            : new BoxEl { Shrink = 0f, Opacity = WinUiNavGeometry.OfflineArtOpacity, Children = [cover] };

    /// <summary>"Not available offline", led by the name when the row cuts it.</summary>
    static string OfflineTip(string label, bool labelCut)
    {
        string reason = Loc.Get("sidebar.pin.offline");
        return labelCut ? label + " · " + reason : reason;
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
        bool expanded = _o.Model.IsFolderExpanded(folderId);
        float height = _m.RowHeight;

        // A synced folder pin the rootlist no longer carries renders visible-but-disabled, never vanishes.
        if (entry.Missing) return MissingFolderRow(section, in entry, in row, height);

        var snapshot = entry;
        var shape = OneLine;
        string label = entry.Name.Length > 0 ? entry.Name : NavRowFacts.ShortUri(entry.Id);
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
        var resource = _o.PayloadOf(in snapshot, rootlistItem);
        // Spring-load: hold a drag over a CLOSED folder and it opens. Re-checked inside the callback — the gesture
        // outlives any single render, and ActivateFolder is a TOGGLE.
        Action springLoad = () =>
        {
            if (!_o.Model.IsFolderExpanded(folderId)) _o.ActivateFolder(folderId, snapshot.Name, index);
        };
        DropTargetSpec? drop = rootlistItem
            ? _o.ResourceDropSpec(row.SectionId, -1, null, null, resource, index, springLoad,
                rootFacts: TreeRowFacts(index, in snapshot))
            : PinSpec(section, row.SectionId, index, springLoad);

        var spec = new WinUiRowSpec
        {
            Key = row.Key,
            Label = label,
            Depth = IndentOf(in row),
            Shape = shape,
            Height = height,
            MarginY = _m.MarginY,
            Glyph = Icons.Folder,
            // NavigationViewItem: the down chevron at rest, turned up while the folder is open (the board `Shell-Nav`).
            DisclosureChevron = Sidebar.Chevron.Section(_folderOpen ??= FolderOpenLive, WinUiNavGeometry.ChevronGlyph, identity: _folderIdentity ??= FolderIdentity),
            Trailing = FolderTrailing(in snapshot, folderId, rootlistItem),
            Tile = compact,
            OnRealized = compact ? TileRealize : null,
            OnClick = activate,
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
            OnRename = rename,
            OnMove = move,
            Drag = reordering ? null : rootlistItem ? _o.TreeDragPayload(in snapshot) : resource,
            DropTarget = drop,
        };
        if (rootlistItem) ApplyTreeSelection(ref spec, folderId.Length > 0 ? snapshot.Id : "", activate);
        // The chevron column replaces the trailing pad; the folder's own "+" is laid out before it (S8).
        float folderSlot = spec.Trailing is not null ? _m.FolderPlus : 0f;
        spec.LabelTooltip = LabelOverflows(label, row.Depth, WinUiNavGeometry.FolderTrail
            + WinUiNavGeometry.TrailingWidth(false, false, folderSlot), WinUiNavGeometry.LabelX(shape, glyph: true));
        // A folder is a pill anchor when it is the deepest visible ancestor of the route (rule 2 of §P1.2). Both drop
        // cues stay: the bottom band of an expanded header IS the "first child" slot, and the whole outdent gesture
        // happens on folder rows.
        return Indicator(Tipped(WinUiEntityRow.Create(in spec), spec.LabelTooltip, label), _o.RowSelectsRoute(index, sel),
            IndentOf(in row), height, _o.PillRouteOf(index, sel));
    }

    /// <summary>The dimmed retention row for a folder pin the rootlist lost: no click, no disclosure, no drag/drop, and a
    /// menu reduced to the one honest verb (Unpin) — the reason rides the tooltip and, with subtitles, the second line.</summary>
    Element MissingFolderRow(SidebarSection section, in SidebarLibraryEntry entry, in SidebarRow row, float height)
    {
        string reason = Loc.Get(Strings.Sidebar.Pin.FolderMissing);
        var menu = _o.MissingFolderMenu(in entry);
        var spec = new WinUiRowSpec
        {
            Key = row.Key,
            Label = entry.Name.Length > 0 ? entry.Name : Loc.Get(Strings.Sidebar.V3.Kind.Folder),
            Enabled = false,
            Depth = IndentOf(in row),
            Tile = _o.CompactPlan,
            Shape = OneLine,
            Height = height,
            MarginY = _m.MarginY,
            Glyph = Icons.Folder,
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
        };
        return Tipped(WinUiEntityRow.Create(in spec), true, reason);
    }

    /// <summary>Does the one-line label truncate in this row? The pane's CURRENT width (peeked: a tooltip decision, not a
    /// subscription) minus the row's own ladder (its label x: <see cref="WinUiNavGeometry.LabelX"/>), through the one estimate
    /// <see cref="SidebarLabelFit"/> owns.</summary>
    bool LabelOverflows(string label, int depth, float trailing, float labelX)
        => SidebarLabelFit.Overflows(label, WinUiNavGeometry.LabelWidth(_o.ExpandedWidth.Peek(), depth, trailing, labelX));

    /// <summary>A row whose full label would truncate wears it as a tooltip. The slot owns the wrap, so the tooltip is the
    /// row's wrapper and a sibling of the drop cues, never a child of the row. A compact TILE always wears its label (the
    /// 40-DIP tile shows no text) and never grows: it is 40 wide already.</summary>
    Element Tipped(Element built, bool tooltip, string label) => Sidebar.RowTip.Wrap(built, _o.CompactPlan, tooltip, label);

    /// <summary>The depth a row is drawn at in THIS pane: a compact tile is never indented, so its pill sits at PillX(0).</summary>
    int IndentOf(in SidebarRow row) => _o.CompactPlan ? 0 : row.Depth;

    /// <summary>The compact tile's realize handler, cached so a render allocates no delegate for it.</summary>
    Action<NodeHandle> TileRealize => _tileRealize ??= h => _tileNode = h;

    /// <summary>The flyout's anchor: the tile's live node. Cached like <see cref="TileRealize"/>.</summary>
    Func<NodeHandle> TileAnchor => _tileAnchor ??= () => _tileNode;

    /// <summary>A route row (an app route the layout carries: a Collections page, a Home or Settings glyph row). Label and
    /// glyph come from the route table so a route follows the UI culture; an unknown key in a hand-edited document
    /// degrades rather than crashing.</summary>
    Element RouteRow(SidebarSection section, string key, string sel, int index)
    {
        var dest = _o.DestOf(key);
        bool selected = _o.RowSelectsRoute(index, sel);
        float height = _m.RowHeight;
        string title = dest.Title;
        // A durable application destination is a pin drag source unless a Reorderable already owns the drag;
        // SidebarPinId centrally excludes editor/tooling routes.
        DragPayload? drag = !_o.TryBandOf(index, out _) && SidebarPinId.FromRoute(key) is not null
            ? _o.PayloadOfRoute(key, title)
            : null;
        var menu = _o.RouteMenu(section, key, index);

        var spec = new WinUiRowSpec
        {
            Key = key,
            Label = title,
            Selected = selected,
            Depth = 0,
            Shape = OneLine,
            Height = height,
            MarginY = _m.MarginY,
            Glyph = dest.Glyph,
            Tile = _o.CompactPlan,
            OnClick = () => _o.Navigate(key, null),
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
            Drag = drag,
            DropTarget = PinSpec(section, section.Id, index),
        };
        spec.LabelTooltip = LabelOverflows(title, 0, 0f, WinUiNavGeometry.LabelX(OneLine, glyph: true));
        // The pill registers under the LIVE route when this row is an adopted anchor (an album page lights Albums: NavRowKit.AdoptiveRoute).
        return Indicator(Tipped(WinUiEntityRow.Create(in spec), spec.LabelTooltip, title), selected, 0, height, AnchorRoute(selected, key, sel));
    }

    /// <summary>The empty Pinned band while a pinnable drag is live (design V.3): 36 tall in the 4,2 margin, a dashed
    /// 1-px AccentDefault ring, r4, "Drop here to pin"; the drop pins at position 0.</summary>
    Element DropBandRow(SidebarSection section, int index) => new BoxEl
    {
        Key = "pin-drop-band", Height = _m.RowHeight, Shrink = 0f,
        Margin = new Edges4(0f, _m.MarginY, 0f, _m.MarginY),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        BorderWidth = 1f, BorderColor = Tok.AccentDefault, BorderDashOn = WinUiNavGeometry.DropBandDashOn, BorderDashOff = WinUiNavGeometry.DropBandDashOff,
        DropTarget = _o.ResourceDropSpec(section.Id, 0, null, null, rootPlanIndex: index),
        Children = [Caption(Loc.Get("sidebar.pin.dropHere")) with { Color = Tok.AccentDefault }],
    };

    // ── degraded rows ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The quiet hint under an empty Playlists section: 40 DIP tall, 11px tertiary, in its 2,2 margin. Its copy
    /// names the header "+" that fixes it, and swaps to the query copy while a search is live. The planner plans no Empty
    /// row in Your Library (the head owns that state, §P3.5).</summary>
    Element EmptyRow()
    {
        string text = _o.SearchText.Length > 0 ? LibraryEmptyText() : Loc.Get(Strings.Sidebar.Empty.Playlists);
        return new BoxEl
        {
            Height = WinUiNavGeometry.EmptyHintHeight, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, WinUiNavGeometry.RowMarginY, 0f, WinUiNavGeometry.RowMarginY),
            Padding = new Edges4(WinUiNavGeometry.HeaderTextX, 0f, WinUiNavGeometry.TrailingPad, 0f),
            Children =
            [
                R.Type.MicroMeta(text) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    string LibraryEmptyText()
    {
        string query = _o.SearchText;
        return query.Length > 0
            ? Loc.Format(Sidebar.PaneLoc.SearchEmpty, ("query", query))
            : Loc.Get(Sidebar.PaneLoc.LibraryEmpty);
    }

    // ── multi-select + the folder "+" ────────────────────────────────────────────────────────────────────────────

    /// <summary>Turn a rootlist row into a MULTI-SELECTABLE one (W17). <c>OnActivate</c> REPLACES <c>OnClick</c>
    /// because Ctrl/Shift are the gesture; <paramref name="plain"/> is the row's ordinary verb, which a plain click still
    /// runs after clearing the selection. The check lane reads the live selection through BOUND thunks (a selection
    /// change re-skins it with no re-render); the quiet plate is a value the pane's epoch bump re-renders.</summary>
    void ApplyTreeSelection(ref WinUiRowSpec spec, string entryId, Action? plain)
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

    /// <summary>A folder row's trailing slot: the quiet count (the Playlists section), then the folder's own "+"
    /// (20 box, glyph 12) — the count is the fact, the "+" the verb. The "+" is keyed inside a row keyed by the entry,
    /// so it remounts when this slot recycles onto another folder, which is what makes capturing the folder id in its
    /// factory safe.</summary>
    Element? FolderTrailing(in SidebarLibraryEntry entry, string folderId, bool rootlistItem)
    {
        // No count: the "N items" subtitle already says it, and the chevron column is the folder's trailing edge.
        if (!rootlistItem || folderId.Length == 0 || !_o.HasActions) return null;

        var owner = _o;
        string name = entry.Name;
        var active = _folderPlusDrop;
        Func<float> reveal = _folderPlusReveal ??= FolderPlusOpacity;
        Element plus = Embed.Comp(() => new WinUiNavCreateButton(
            () => owner.NewPlaylistInFolder(folderId),
            menu: () => owner.FolderCreateMenu(folderId),
            drop: owner.FolderCreateDropSpec(folderId, name, active),
            dropActive: () => active.Value,
            revealOpacity: reveal,
            box: _m.FolderPlus, glyph: WinUiNavGeometry.FolderPlusGlyph)) with { Key = "folder-create" };
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
    Element? TrailingBadge(SidebarSection section, in SidebarLibraryEntry entry)
    {
        if (section.Kind == SidebarSectionKind.NewReleases && _o.Model.AgeBadge(entry.SortStamp) is { Length: > 0 } age)
            return R.Type.MicroMeta(age) with { Color = Tok.TextTertiary, MaxLines = 1 };
        return null;
    }

    /// <summary>The route a row's pill registers under: its own, or the LIVE route when the row is lit for a route it does not carry (an adopted anchor,
    /// NavRowKit.AdoptiveRoute: an album page lights Albums), so the pill's bound read lights it.</summary>
    static string? AnchorRoute(bool selected, string? own, string live)
        => selected && live.Length > 0 && !string.Equals(own, live, StringComparison.Ordinal) ? live : own;

    /// <summary>Attach the item-owned selection pill and both drop cues. Shape-stable for recycling: a slot never
    /// inherits an animated transform from the route it represented one window earlier. The ZStack order is load-bearing
    /// — plate UNDER the row (text never tinted), pill over it, insertion line on top.</summary>
    Element Indicator(Element row, bool selected, int depth, float height, string? route)
    {
        if (!float.IsFinite(height) || height <= 0f) height = _m.RowHeight;
        _pillState = new SidebarPillState(
            Route: route,
            Selected: selected,
            Indent: WinUiNavGeometry.PillX(depth),
            Top: _m.MarginY + (height - WinUiNavGeometry.PillH) * 0.5f);
        var owner = _o;
        Func<SidebarPillState> probe = _pillProbe ??= PillState;
        return ZStack(DropPlate(), row, Embed.Comp(() => new WinUiSelectionPill(owner, probe)), InsertionLine());
    }

    /// <summary>THE "INTO" PLATE (W16): mounted once per row, ALWAYS, as the ZStack's first child — accent@0.18 with a
    /// 1-DIP accent border, radius 4, auto-sized to the row's rect, out of hit-testing so hover and the drop reach the
    /// row. It left the row itself because bindings wire at MOUNT only: a thunk built there captured the row's state as
    /// values and a same-keyed re-render kept the stale one. Every thunk reads the LIVE slot index + row epoch.</summary>
    Element DropPlate() => new BoxEl
    {
        Key = "drop-plate",
        // The plate covers the row, not the row's vertical margin.
        Margin = new Edges4(0f, _m.MarginY, 0f, _m.MarginY),
        Corners = CornerRadius4.All(WinUiNavGeometry.DropPlateRadius),
        BorderWidth = WinUiNavGeometry.DropPlateBorderWidth,
        Fill = Prop.Of(_plateFill ??= CuePlateFill),
        BorderColor = Prop.Of(_plateBorder ??= CuePlateBorder),
        HitTestVisible = false,
    };

    ColorF CuePlateFill()
    {
        int i = _scope.Index.Value;
        _ = _o.SubscribeRowEpoch(i);
        return SidebarDropCue.DrawsPlate(_o.DropSlotFor(i).Kind) ? Tok.AccentDefault with { A = WinUiNavGeometry.DropPlateAlpha } : ColorF.Transparent;
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
        Height = WinUiNavGeometry.DropLineThickness,
        Width = Prop.Of(_lineWidth ??= CueLineWidth),
        Corners = CornerRadius4.All(WinUiNavGeometry.DropLineCorner),
        Fill = Tok.AccentDefault,
        Opacity = Prop.Of(_lineOpacity ??= CueLineOpacity),
        Transform = Prop.Of(_lineTransform ??= CueLineTransform),
        HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Width = WinUiNavGeometry.DropDotSize, Height = WinUiNavGeometry.DropDotSize,
                Corners = CornerRadius4.All(WinUiNavGeometry.DropDotSize * 0.5f),
                Margin = new Edges4(0f, (WinUiNavGeometry.DropLineThickness - WinUiNavGeometry.DropDotSize) * 0.5f, 0f, 0f),
                Fill = Tok.AccentDefault,
                HitTestVisible = false,
            },
        ],
    };

    float CueLineWidth()
    {
        int i = _scope.Index.Value;
        _ = _o.SubscribeRowEpoch(i);
        return WinUiNavGeometry.DropLineWidth(_o.ContentWidth, _o.DropSlotFor(i).Depth);
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
        return Affine2D.Translation(WinUiNavGeometry.TreeContentX(slot.Depth),
                                    WinUiNavGeometry.DropLineY(slot.Kind, _o.RowExtentOf(i)));
    }

    /// <summary>The row's STRUCTURAL drop facts, straight off the published plan (<see cref="NavDropRules.TreeRowFacts"/>). The
    /// payload-dependent half — self, ancestor, whether the centre takes this payload, the live rootlist-loaded gate — is
    /// folded in at hover.</summary>
    SidebarRowFacts TreeRowFacts(int index, in SidebarLibraryEntry entry)
        => NavDropRules.TreeRowFacts(_o.Plan.Rows, _o.Plan.Entries, index, in entry,
                                     entry.IsFolder && _o.Model.IsFolderExpanded(entry.FolderId), _o.TreeSortedNonCustom);

    /// <summary>The tree's closing gutter: 24 DIP of nothing that owns ONE slot — "top level, at the end" (the chip says
    /// "Move to end"). It exists because that slot had no target and the old create row accepted rootlist payloads,
    /// turning a drag past the end into a duplicated playlist. The destination is synthetic (resolved against the last
    /// top-level entry at commit), and its caret reads the LIVE slot index like every other row's.</summary>
    Element TreeEndRow(in SidebarRow row, int index)
    {
        var facts = NavDropRules.TreeEndFacts(_o.TreeSortedNonCustom);
        var target = new DragPayload(DragKind.Route, row.SectionId, "", "", RootlistItem: true);
        var drop = _o.ResourceDropSpec(row.SectionId, -1, null, null, target, index, rootFacts: facts);
        return ZStack(
            new BoxEl
            {
                Key = "tree-end",
                Height = WinUiNavGeometry.TreeEndHeight,
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
        bool rowSelects = _o.RowSelectsRoute(index, live);
        // An adopted anchor (an album page lights Albums) is lit for a route it does not carry: its pill reads the LIVE route, derived here rather than
        // from the render's snapshot, which the bound read can outrun on the epoch that lights it.
        return (_pillState with { Route = AnchorRoute(rowSelects, _pillState.Route, live) }).For(live, rowSelects);
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
