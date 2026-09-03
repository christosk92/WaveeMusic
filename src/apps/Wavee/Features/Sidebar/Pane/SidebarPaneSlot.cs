using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Sidebar;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// R3.0.1 — ONE bound slot of the pane's plan list, and the whole 13-kind row vocabulary behind it (extracted from
// `Modes/Curated/CuratedRowSlot.cs`; Classic, Curated and Library V3 all render through this).
//
// WHY A COMPONENT PER SLOT. `ItemsView.CreateBound` builds each slot ONCE and recycles it by writing the slot's index
// signal. A Component whose Render reads `scope.Index.Value` therefore re-renders exactly on a recycle — which is how a
// heterogeneous plan (13 row kinds) rides a signals-first list at all. It additionally subscribes THIS row's epoch
// (`SidebarPane.SubscribeRowEpoch` — bumped by a publish for the document / projection / pin / folder / MODE edges the
// diff found changed) and the live ROUTE, so a customizer edit, a library refresh, a pin mutation, a section toggle or a
// navigation re-skins only the realized window — never the list.
//
// SELECTION lives inside the item container, matching WinUI NavigationViewItem: the shared row ramp plus a permanently
// mounted 3×16 SelectionIndicator. The previous and next realized rows run the exact paired NavigationView timeline.
//
// NO HOOKS BELOW Render's prologue. Every builder is a plain method: hook order must be identical across every recycle.
// (The animated chevrons ARE hook-owning components, but they are CHILDREN — `Embed.Comp` — and each row kind has its own
// recycle pool via `ContentType`, so a header slot never rebinds into a shape without a chevron.)
sealed class SidebarPaneSlot : Component
{
    readonly SidebarPane _o;
    readonly RowScope _scope;

    // Cached live-state probes handed to the chevron components. They capture only THIS slot (never a section id), because
    // a child component's ctor args FREEZE at mount and the slot recycles onto a different section every scroll.
    Func<bool>? _headerOpen;
    Func<bool>? _folderOpen;
    Func<bool>? _cardOpen;
    Func<SidebarPillState>? _pillProbe;
    SidebarPillState _pillState;

    public SidebarPaneSlot(SidebarPane owner, RowScope scope) { _o = owner; _scope = scope; }

    public override Element Render()
    {
        int index = _scope.Index.Value;        // a recycle writes this → exactly this row re-renders
        _ = _o.SubscribeRowEpoch(index);      // THIS row's epoch only (see SidebarPane.SubscribeRowEpoch)
        // Pane selection is the live ROUTE, never a list index — PEEKED, not subscribed: the pane's RefreshSelection
        // sweep bumps this row's epoch (read above) when it gains or loses the pill, so a navigation re-renders the two
        // rows it concerns instead of every realized row. Subscribing here put the whole window on the route fanout.
        string sel = _o.SelectedRoutePeek;

        var plan = _o.Plan;
        var rows = plan.Rows;
        // A slot can transiently address an index the newest plan no longer has (the count signal lands in a layout
        // effect, one hop after the plan). Render nothing rather than clamp onto a foreign row.
        if ((uint)index >= (uint)rows.Count) return Blank;
        var row = rows[index];
        var section = _o.SectionOf(row.SectionId);
        if (section is null) return Blank;

        Element content = row.Kind switch
        {
            SidebarRowKind.SectionHeader => Header(section, row, index, rows),
            SidebarRowKind.HeaderLabel => Banded(SidebarSectionHeader.Label(SidebarPaneText.TitleOf(section)), index, rows),
            // Only a Divider SECTION produces this row. Ordinary section joins are whitespace, never implicit rules.
            SidebarRowKind.Divider => SidebarSectionHeader.ExplicitDivider(),
            SidebarRowKind.IconRow or SidebarRowKind.EntityRow or SidebarRowKind.Placeholder
                => ItemOrEntity(section, row, sel, index),
            SidebarRowKind.FolderHeader => FolderRow(section, row, sel, index),
            SidebarRowKind.GridStrip => GridStrip(section, row, sel),
            SidebarRowKind.Empty => EmptyRow(section),
            SidebarRowKind.Skeleton => SidebarSkeletons.Row(index, section.Opts.Density, section.Opts.Subtitles,
                heightOverride: SidebarPaneMetrics.RowHeight(section),
                artOverride: SidebarPaneMetrics.ArtSize(section)),
            SidebarRowKind.TreeEnd => TreeEndRow(section, row, index),
            SidebarRowKind.EntityCard => Card(section, row, sel, index),
            SidebarRowKind.PromptRow => Prompt(section),
            // PHASE 2 / Decision B — the customize canvas. Only `SidebarRowPlanner.BuildEdit` emits this kind, so the
            // arm is unreachable in every ordinary pane, and the row lands in its OWN recycle pool (`ContentType` is the
            // row kind) rather than rebinding a header slot into a card's shape.
            SidebarRowKind.SectionCard => SidebarPaneEditCard.Build(_o, section, index,
                SidebarChevron.Section(_cardOpen ??= CardOpenLive)),
            _ => Blank,
        };

        // PHASE 2 — the SECTION-CARD band. A separate branch, deliberately ahead of and disjoint from the item-band wrap
        // below: `ReorderBand` never claims a `SectionCard` (its kind guard lists only the four item kinds), the two
        // bands carry different DRAG KINDS, and one wrap site answering both questions is how a card ends up moving an
        // item's slot. The `Grow/Shrink/MinWidth` treatment is the same and for the same reason — see the note below.
        if (row.Kind == SidebarRowKind.SectionCard && _o.TryEditSectionBand(index, out var cardBand))
            content = _o.SectionReorder.Item(index - cardBand.Start,
                content is BoxEl card ? card with { Grow = 1f, Shrink = 1f, MinWidth = 0f } : content,
                key: row.SectionId, transition: SidebarPane.Placement);

        // In-place reorder (§C5.1). The Reorderable owns the row's drag source, its keyboard lift and its position
        // track — which is exactly why the row itself carries no Drag payload and no Animate when wrapped.
        //
        // FILL THE SLOT. `Reorderable.Item` wraps its content in a BoxEl that leaves `Direction` at its default 0 =
        // ROW, so the row sits on that wrapper's MAIN axis and — with no Grow — arranges at its own MEASURED CONTENT
        // WIDTH under the pane's width cap. Every unwrapped row fills (the bound slot's component anchor is a COLUMN
        // whose cross axis stretches by default), so a REORDERABLE band's rows drew visibly narrower plates than their
        // neighbours: hovering a short "Hans Zimmer" painted a stub of a fill while a long, ellipsised title next to it
        // painted full width — which reads as "hover and selected are different widths" even though Fill/HoverFill/
        // PressedFill all sit on the SAME BoxEl (SidebarEntityRow.cs:301-341) and cannot differ by state. It affects
        // Pinned / StaticLinks / CustomGroup in ALL THREE designs plus V3's PlaylistTree under Custom sort
        // (SidebarPane.IsReorderableSection + LibraryV3Sidebar.IsSectionReorderable).
        //
        // HISTORICAL PRECEDENT, not a live path: this is the SAME defect the customizer's section OUTLINE had already
        // fixed app-side once (the deleted `Curated/SidebarOutlineView.cs`, "FILL THE COLUMN (round-3 defect 1)"), and
        // that the deleted top-bar strip (`Curated/SidebarTopBarCard.cs`) sidestepped with an explicit Width. Phase 3
        // deleted both surfaces, so the pane's two wrap sites above are now the ONLY live call sites and this file is
        // the pattern's owner. MinWidth 0 keeps a long title ELIDING rather than pushing the row past the pane, exactly
        // as the outline's fix did.
        //
        // Phase 1 materialises the shortcut band as a StaticLinks "Shortcuts" section, which lands in a reorderable
        // band — those rows would render narrow without this.
        //
        // `content` is a BoxEl on MOST wrapped kinds (SidebarEntityRow.Create returns BoxEl; Indicator returns
        // Ui.ZStack, also a BoxEl) — but NOT on the tooltip-wrapped ones (a track row, a missing-entity row, an
        // unavailable action row), where it is a ComponentEl. Those carry their own fill through
        // `ToolTip.Wrap(grow: 1f)`, and the reconciler mirrors a component anchor's FlexGrow from its rendered child
        // (Reconciler.MirrorParticipation), so the grow still reaches this wrapper. The type test is therefore a real
        // branch, not just cast insurance: each shape owns its fill, and neither applies it twice.
        if (ReorderBand(row, index) is { } pair)
            content = pair.Ro.Item(index - pair.Start,
                content is BoxEl fill ? fill with { Grow = 1f, Shrink = 1f, MinWidth = 0f } : content,
                key: row.Key, transition: SidebarPane.Placement);
        return content;
    }

    static Element Blank => new BoxEl { Height = 0f, Shrink = 0f };

    (Reorderable Ro, int Start)? ReorderBand(in SidebarRow row, int index)
    {
        if (row.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.IconRow or SidebarRowKind.Placeholder
                             or SidebarRowKind.FolderHeader)) return null;
        if (!_o.TryBandOf(index, out var band)) return null;
        return (_o.ReorderFor(band.SectionId), band.Start);
    }

    // ── section chrome ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 28-DIP section header inside its R3.1.3 rhythm band. Click toggles the section through the MODE's own
    /// collapse owner (<c>SidebarPane.ToggleSection</c> → the undoable <c>SetSectionCollapsed</c> command for Curated, the
    /// persisted per-section flag for Classic). The FIRST header additionally hosts the quick sidebar-layout menu (§C6.4,
    /// Classic's placement); an EDITABLE <c>EntityList</c> header carries its sort/view trigger and — with
    /// <c>Display.InlineControls</c> — its filter chip strip directly under the header band (§C1.8.6).</summary>
    Element Header(SidebarSectionSpec section, in SidebarRow row, int index, IReadOnlyList<SidebarRow> rows)
    {
        bool open = !section.Collapsed;
        string id = section.Id;
        // The trailing slot can carry BOTH affordances: an editable EntityList always gets its sort/view trigger, and the
        // pane's first header always keeps the layout menu (that entry point must never be crowded out — §C6.4).
        var affordances = new List<Element>(2);
        bool editable = !_o.Config.ReadOnly;
        if (editable && section.Kind == SidebarSectionKind.EntityList)
            affordances.Add(SidebarPaneInlineControls.SortTrigger(_o, section));
        // THE PLAYLIST TREE'S CREATE AFFORDANCE, and the ONLY one it has: click opens [New playlist · New folder],
        // and dropping playlists on it makes a folder out of them. It replaced the deleted `CreateAction` ROW, which
        // cost the section's rhythm at the bottom of a 10k-playlist tree and also squatted the "top level, at the end"
        // drop slot that belongs to the tree's own closing gutter (D3).
        //
        // Gated on a CONFIG FLAG, never on `Design` (rule 1): Library V3's own chrome already carries a "+".
        // Every delegate reads LIVE pane state because a header slot recycles across sections.
        if (section.Kind == SidebarSectionKind.PlaylistTree && _o.Config.HeaderCreate
            && _o.Config.OnCreatePlaylist is not null)
            affordances.Add(Embed.Comp(() => new SidebarCreateButton(
                _o.CreatePlaylist,
                menu: _o.CreateMenu,
                drop: _o.HeaderCreateDropSpec(),
                dropActive: () => _o.HeaderCreateDropActive.Value)) with { Key = "tree-create" });
        if (string.Equals(_o.MenuHostSectionId, row.SectionId, StringComparison.Ordinal) && _o.Prefs is { } prefs)
            affordances.Add(SidebarLayoutMenu.Button(prefs, _o.Navigate, box: 24f));
        Element? action = affordances.Count switch
        {
            0 => null,
            1 => affordances[0],
            _ => new BoxEl { Direction = 0, Gap = 2f, AlignItems = FlexAlign.Center, Children = [.. affordances] },
        };

        // Explicit locals rather than ternaries against null (the note SidebarSectionHeader carries): a lambda leaning on
        // target typing inside a conditional is exactly the shape that breaks when the target moves into an initializer.
        Action<bool>? toggle = null;
        Element? chevron = null;
        // PHASE 1 — the materialised Shortcuts section is NOT collapsible, in every mode. Not a design branch (this
        // reads a SECTION ID, the same way MenuHostSectionId above does): the band is projected from
        // `SidebarCustomLayout.TopBar`, which is not in `Sections`, so it has no persisted `Collapsed` bit and no
        // section-scoped command that could write one — `SetSectionCollapsed("topbar")` is an `UnknownSection`
        // rejection. Offering the chevron anyway would be a visible affordance that silently does nothing, which is
        // strictly worse than not offering it.
        if (_o.Config.SetSectionCollapsed is not null && !SidebarIds.IsTopBar(id))
        {
            toggle = o => _o.ToggleSection(id, !o);
            // R3.1.7a — ONE glyph whose Rotation animates, never a glyph swap.
            chevron = SidebarChevron.Section(_headerOpen ??= HeaderOpenLive);
        }

        Element header = SidebarSectionHeader.Header(SidebarPaneText.TitleOf(section), open, toggle, action,
            chevron: chevron);

        if (!editable || section.Kind != SidebarSectionKind.EntityList || !section.Opts.InlineControls
            || section.Collapsed)
            return Banded(header, index, rows);
        return Banded(new BoxEl
        {
            Direction = 1, Gap = 4f,
            Children = [header, SidebarPaneInlineControls.Chips(_o, section)],
        }, index, rows);
    }

    /// <summary>R3.1.3 — SECTION RHYTHM. The planner emits contiguous rows (zero gap), so five sections used to read as one
    /// undifferentiated column. A header band therefore carries 8 DIP of air above it and 2 DIP below (matching
    /// <c>SidebarSectionHeader.Section</c>'s own internal gap).
    ///
    /// <para>It is PADDING on a wrapper, not a Margin on the header: padding is unambiguously part of the slot's MEASURED
    /// height, so <c>RepeatLayout.VariableList</c>'s extent stays honest and scroll anchoring cannot drift.</para>
    ///
    /// <para>The air is suppressed for the pane's FIRST row (nothing to separate from) and directly after a DIVIDER or a
    /// bare HEADING row, both of which already supply the gap — doubling it there would out-space Classic's landed
    /// rule + lead-in.</para></summary>
    static Element Banded(Element header, int index, IReadOnlyList<SidebarRow> rows)
    {
        float top = SidebarPaneMetrics.SectionGap;
        if (index <= 0) top = 0f;
        else
        {
            var prev = rows[index - 1].Kind;
            if (prev is SidebarRowKind.Divider or SidebarRowKind.HeaderLabel) top = 0f;
        }
        return new BoxEl
        {
            Direction = 1, Shrink = 0f,
            Padding = new Edges4(0f, top, 0f, SidebarPaneMetrics.HeaderBodyGap),
            Children = [header],
        };
    }

    /// <summary>The header chevron's live open state. Captures only the SLOT, so it survives every recycle: it re-reads the
    /// plan row at the slot's current index and the section behind it, and its <c>SubscribeRowEpoch</c> read is what
    /// re-renders the chevron on a toggle.</summary>
    bool HeaderOpenLive()
    {
        int index = _scope.Index.Value;
        _ = _o.SubscribeRowEpoch(index);
        var rows = _o.Plan.Rows;
        if ((uint)index >= (uint)rows.Count) return true;
        var section = _o.SectionOf(rows[index].SectionId);
        return section is null || _o.DisclosureOpen(section.Id, folder: false, fallback: !section.Collapsed);
    }

    /// <summary>The EDIT CARD's chevron state — same recycle-safe shape as <see cref="HeaderOpenLive"/>: it captures the
    /// SLOT and never a section id, because a chevron is a hook-owning child component whose ctor args freeze at mount
    /// while this slot recycles onto a different section on every scroll.
    /// <para>It asks the pane about the session THE PUBLISHED PLAN WAS BUILT FROM, never the live signal, so the mark can
    /// never claim "open" over a plan that has no body rows under this card.</para></summary>
    bool CardOpenLive()
    {
        int index = _scope.Index.Value;
        _ = _o.SubscribeRowEpoch(index);
        var rows = _o.Plan.Rows;
        if ((uint)index >= (uint)rows.Count) return false;
        var section = _o.SectionOf(rows[index].SectionId);
        return section is not null && _o.EditShowsBody(section);
    }

    /// <summary>The folder chevron's live expansion state — same recycle-safe shape as <see cref="HeaderOpenLive"/>.</summary>
    bool FolderOpenLive()
    {
        int index = _scope.Index.Value;
        _ = _o.SubscribeRowEpoch(index);
        var rows = _o.Plan.Rows;
        if ((uint)index >= (uint)rows.Count) return true;
        var row = rows[index];
        var entries = _o.Plan.Entries;
        if (row.EntryIndex < 0 || row.EntryIndex >= entries.Count) return true;
        string folderId = entries[row.EntryIndex].FolderId;
        bool open = _o.Prefs?.IsFolderExpanded(folderId) ?? true;
        return _o.DisclosureOpen(folderId, folder: true, fallback: open);
    }

    // ── item / entity rows ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The three row kinds that can carry EITHER a projected entry or a hand-placed item spec, resolved in ONE
    /// place so the planner's join rules are honoured exactly once:
    /// <list type="bullet">
    /// <item>an ACTION item is an action row whatever kind the planner chose for it (the planner has no action row kind,
    /// so a bound shortcut arrives as <c>Placeholder</c> — it must never render as a missing entity);</item>
    /// <item><c>EntryIndex &gt;= 0</c> ⇒ the projected entry (a Pinned override item, when present, still supplies its
    /// alias/icon);</item>
    /// <item>a TRACK item is a play row built from its own spec (a hand-placed track is not in the library projection);</item>
    /// <item>a ROUTE item is a glyph row;</item>
    /// <item>anything else is the dimmed missing-entity retention row — never dropped (§C1.4).</item>
    /// </list></summary>
    Element ItemOrEntity(SidebarSectionSpec section, in SidebarRow row, string sel, int index)
    {
        var item = SidebarPaneText.ItemOf(section, row.Key);
        if (item is { Target: SidebarItemTarget.Action }) return ActionRow(section, item, index);

        var entries = _o.Plan.Entries;
        if (row.EntryIndex >= 0 && row.EntryIndex < entries.Count)
        {
            var entry = entries[row.EntryIndex];
            // A Pinned OVERRIDE may be keyed by uri instead of pin id (both are legal — the planner's own hidden-override
            // test accepts either), so a projected row looks for its side-table entry both ways.
            item ??= SidebarPaneText.ItemOf(section, entry.Uri);
            return EntryRow(section, in entry, row, sel, item, index);
        }
        if (item is { Target: SidebarItemTarget.Track }) return TrackItemRow(section, item, index);
        if (item is { Target: SidebarItemTarget.Route }) return RouteRow(section, item, sel, index);
        return MissingRow(section, item, row);
    }

    /// <summary>A projected entry: a playlist / album / artist / show / folder / track / app route the projection knows.</summary>
    Element EntryRow(SidebarSectionSpec section, in SidebarLibraryEntry entry, in SidebarRow row, string sel,
                     SidebarItemSpec? item, int index)
    {
        bool named = entry.Name.Length > 0;
        // "A row whose entry Name.Length == 0 renders dimmed from the uri" — the entity exists but has not resolved a
        // display name yet, which is honest as a dimmed row and never as a blank one.
        string label = item?.LabelOverride is { Length: > 0 } alias ? alias
            : named ? entry.Name
            : SidebarPaneText.ShortUri(entry.Uri);
        bool track = entry.IsTrack;
        string? route = entry.RouteKey;
        // Through the pane, which resolves it with SidebarRowResolve — the SAME rule its selection sweep uses, so the
        // row that draws the pill and the row whose epoch got bumped can never disagree.
        bool selected = _o.RowSelectsRoute(index, sel);
        bool reordering = _o.TryBandOf(index, out _);
        // Resolved pane-side by ONE signal effect over the playback bridge (SidebarPane.RefreshPlayState), so this row
        // never joins the hot Identity fanout: a change to it bumped this row's epoch, which is what re-rendered us.
        var (playing, animated) = _o.RowPlayState(index);
        float height = SidebarPaneMetrics.RowHeight(section);
        // TreeLeading is IDENTICAL to StandardLeading at depth 0 (W7 removed the reserved disclosure cell it used to
        // pay for on every row), so the only visible reason to take the tree path at all is the CONNECTOR GUIDES a
        // deeper row draws — and depth only exists where a folder does. A folder-free PlaylistTree section (V3's
        // common case) therefore renders its rows through StandardLeading, flush with a StaticLinks row like Liked
        // Songs above it, exactly as it would through TreeLeading anyway.
        bool treeNode = section.Kind == SidebarSectionKind.PlaylistTree && _o.SectionHasFolder(row.SectionId);
        int treeDepth = treeNode ? entry.Depth : 0;
        int baseDepth = treeNode ? Math.Max(0, row.Depth - treeDepth) : row.Depth;

        var snapshot = entry;   // an `in` parameter cannot be captured — copy the record struct for the lazy closures
        string rowKey = row.Key;
        // Explicit locals, not inline conditionals: a lambda has no natural type, so a ternary against null would lean on
        // target typing inside an object initializer (the note SidebarSectionHeader already carries).
        Action? click = null;
        if (track) click = () => _o.Play(snapshot.Uri, asTrack: true);
        else if (route is { Length: > 0 } r) click = () => _o.Navigate(r, snapshot.Name);

        Func<ContextMenuModel?>? menu = null;
        Action? rename = null;
        if (_o.Acts is { } acts)
        {
            menu = () => Menus.SidebarEntry(acts, in snapshot, extras: NavExtras(section, index, item, rowKey));
            // F2 — the keyboard path to the row menu's Rename. Only a row that can actually be renamed takes it (and
            // therefore becomes a focus stop); a Reorderable-wrapped row never does, because that wrapper owns the
            // focus stop and the key handler and two of each per row is a documented stomp.
            if (!reordering) rename = Menus.SidebarRenameAction(acts, in snapshot);
        }

        // A Reorderable installs its OWN drag source and position track; a second one is a documented stomp. A TRACK is
        // never a pin drag source at all (locked decision 4 is enforced by the KIND, not per surface).
        bool treeRow = section.Kind == SidebarSectionKind.PlaylistTree && !reordering;
        // Rootlist membership is a fact about the ENTITY, not about which section happens to be showing it: a PINNED or
        // recently-played playlist row is the very same rootlist member as its tree row. Flagging it there too is what
        // lets that row be filed into a folder; keying the flag off the section (as this used to) meant the identical
        // playlist was file-able from one list and inert from another.
        bool rootlistItem = !reordering && snapshot.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder;
        // Alt+↑/↓ — the KEYBOARD half of D12, the same sibling move the row menu's Move up / Move down offer. TREE rows
        // only: a pinned or recently-played row is a rootlist MEMBER but not a rootlist ordering slot, so an Alt+arrow
        // there would silently reorder a list the user is not looking at. Never on a Reorderable-wrapped row (treeRow
        // already excludes those) — that wrapper owns the focus stop and the key handler.
        Action<int>? move = treeRow && rootlistItem && _o.Acts is { } moveActs
            ? d => FolderActions.Move(moveActs, snapshot.Id, d)
            : null;
        // The DESTINATION half stays tree-only: a pinned row is a rootlist member but not a rootlist ORDERING slot.
        // `resource` is THIS ROW's own identity (what a drop is filed relative to; the "Move into {name}" name; the
        // Self check) — always the single entry, never the selection. The DRAG payload is the PANE's: dragging a row
        // that is IN the multi-selection lifts the whole selection, dragging one outside it lifts just that row (the
        // detail-page rule). Conflating the two made a selected row's destination identity the FIRST selected entry.
        WaveeResourceDragPayload? resource = treeRow
            ? WaveeResourceDragPayload.FromEntry(snapshot, _o.Acts?.Svc, rootlistItem: true)
            : null;
        WaveeResourceDragPayload? drag = null;
        if (!reordering && !track)
            drag = treeRow ? _o.TreeDragPayload(in snapshot)
                           : WaveeResourceDragPayload.FromEntry(snapshot, _o.Acts?.Svc, rootlistItem);
        // A NON-editable playlist row now takes the resource spec too. It still refuses a track deposit — but it does so
        // with a REASON ("you can't edit this playlist") instead of the bare not-allowed glyph it used to show, which is
        // the difference between "no, and here's why" and "this feature is broken". Every other row kind keeps the pin
        // spec and, through the resource spec's `transparent` arm, stays silent while a drag merely crosses it.
        bool playlistRow = snapshot.Kind == SidebarEntryKind.Playlist;
        DropTargetSpec? drop = playlistRow || resource is not null
            ? _o.ResourceDropSpec(row.SectionId, PinSlot(row.SectionId, index),
                playlistRow && snapshot.CanEdit ? snapshot.Uri : null,
                snapshot.Name, resource, index, isPlaylistRow: playlistRow,
                // The row's STRUCTURAL facts. The payload-dependent half (self / ancestor / whether the centre takes
                // this payload's tracks) is folded in at hover, where the payload first exists.
                rootFacts: TreeRowFacts(index, in snapshot))
            : PinSpec(section, row.SectionId, index);

        var spec = new SidebarRowSpec
        {
            Key = row.Key,
            Label = label,
            Subtitle = section.Opts.Subtitles ? SidebarPaneText.SubtitleOf(in snapshot) : null,
            Selected = selected,
            Enabled = named || track,
            Depth = baseDepth,
            TreeNode = treeNode,
            TreeDepth = treeDepth,
            TreeContinuationMask = treeNode ? TreeMaskOf(row.SectionId, index, treeDepth) : (byte)0,
            Density = section.Opts.Density,
            // UNIFORM per section: a Reorderable's slot pitch and the section's own rhythm both assume one height.
            Height = height,
            ArtSize = SidebarPaneMetrics.ArtSize(section),
            Leading = LeadingArt(section, in snapshot, item),
            Glyph = section.Opts.Artwork ? null : SidebarPaneText.Glyph(item, SidebarPaneText.EntryGlyph(snapshot.Kind)),
            Trailing = TrailingBadge(section, in snapshot),
            // H1 (#85) — SidebarProjection.PinsFirst stamps IsPinned on every entry it floats, in every sort mode,
            // but nothing rendered it. ShowsPinGlyph is the pure (and therefore unit-tested) half of this decision.
            Pinned = SidebarRowGeometry.ShowsPinGlyph(snapshot.IsPinned, track),
            Playing = playing,
            PlayingAnimated = animated,
            Track = track,
            Overflow = _o.Acts is not null && _o.MenuOverlay is not null,
            OnClick = click,
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
            OnRename = rename,
            OnMove = move,
            Drag = drag,
            DropTarget = drop,
        };
        // MULTI-SELECT (tree rows outside a reorder band only). Ctrl/Shift reach the row through `OnActivate`, which
        // REPLACES `OnClick`; the check lane and the plate read the pane's live selection.
        if (treeRow && rootlistItem) ApplyTreeSelection(ref spec, snapshot.Id, click);
        Element built = SidebarEntityRow.Create(spec);
        if (track) built = SidebarEntityRow.WithPlayTrackHint(built);
        // Tree connectors own their depth lanes; the selection indicator stays in the row's base gutter.
        return Indicator(built, selected, baseDepth, height, route);
    }

    /// <summary>A rootlist FOLDER: the entity-row geometry with a disclosure chevron and the folder mark. A folder never
    /// navigates to a ROUTE (it has none) — by default it toggles its expansion, which lives in <c>SidebarPreferences</c>
    /// (shared by every design and both pane mounts). A mode may reroute that gesture through
    /// <c>SidebarPaneConfig.ActivateFolder</c> (V3's narrow drill-in level); the click and the menu verb take the same path,
    /// so they cannot disagree.</summary>
    Element FolderRow(SidebarSectionSpec section, in SidebarRow row, string sel, int index)
    {
        var entries = _o.Plan.Entries;
        if (row.EntryIndex < 0 || row.EntryIndex >= entries.Count) return Blank;
        var entry = entries[row.EntryIndex];
        string folderId = entry.FolderId;
        bool expanded = _o.Prefs?.IsFolderExpanded(folderId) ?? true;
        float height = SidebarPaneMetrics.RowHeight(section);
        // W7: the SAME test EntryRow uses — a folder outside a PlaylistTree section (a pinned or recently-played
        // folder shortcut) is not a tree row at all, so it must not out-indent its non-tree siblings. TreeDepth is the
        // row's RELATIVE depth (its own tree depth), never the rootlist depth, so a PINNED folder sits flush with its
        // pinned siblings instead of marching right by its rootlist nesting.
        bool treeNode = section.Kind == SidebarSectionKind.PlaylistTree;
        int treeDepth = treeNode ? entry.Depth : 0;
        int baseDepth = treeNode ? Math.Max(0, row.Depth - treeDepth) : row.Depth;
        var snapshot = entry;
        _ = sel;   // a folder is never the selected ROUTE (it has none)

        // Explicit locals, never a ternary against null: a lambda has no natural type in that position.
        Action activate = () => _o.ActivateFolder(folderId, snapshot.Name, index);
        string rowKey = row.Key;

        Func<ContextMenuModel?>? menu = null;
        if (_o.Acts is { } acts)
            menu = () => Menus.SidebarEntry(acts, in snapshot, activate, expanded,
                extras: NavExtras(section, index, SidebarPaneText.ItemOf(section, rowKey), rowKey));

        bool reordering = _o.TryBandOf(index, out _);
        // F2 — the same Rename verb the folder menu carries, reached from the keyboard (see EntityRow).
        Action? rename = _o.Acts is { } renameActs && !reordering ? Menus.SidebarRenameAction(renameActs, in snapshot) : null;
        bool rootlistItem = treeNode && !reordering;
        // Alt+↑/↓ moves a FOLDER among its siblings exactly as it moves a playlist — the whole subtree travels with it,
        // because the move addresses the folder's own rootlist span (see EntityRow for the rest of the reasoning).
        Action<int>? move = rootlistItem && _o.Acts is { } moveActs
            ? d => FolderActions.Move(moveActs, snapshot.Id, d)
            : null;
        var resource = WaveeResourceDragPayload.FromEntry(snapshot, _o.Acts?.Svc, rootlistItem);
        // Spring-load: hold a drag over a CLOSED folder and it opens, so its children become reachable mid-gesture.
        // Re-checked inside the callback rather than trusted from `expanded` — the row is re-rendered on every folder
        // version bump, but the gesture that fires this outlives any single render, and ActivateFolder is a TOGGLE.
        Action springLoad = () =>
        {
            if (_o.Prefs is { } p && !p.IsFolderExpanded(folderId)) _o.ActivateFolder(folderId, snapshot.Name, index);
        };
        DropTargetSpec? drop = rootlistItem
            ? _o.ResourceDropSpec(row.SectionId, -1, null, null, resource, index, springLoad,
                                  rootFacts: TreeRowFacts(index, in snapshot))
            : PinSpec(section, row.SectionId, index, springLoad);

        var spec = new SidebarRowSpec
        {
            Key = row.Key,
            Label = entry.Name.Length > 0 ? entry.Name : SidebarPaneText.ShortUri(entry.Id),
            Subtitle = section.Opts.Subtitles ? Strings.Sidebar.V3.ItemCount(entry.ChildCount) : null,
            Depth = baseDepth,
            TreeNode = treeNode,
            TreeDepth = treeDepth,
            TreeContinuationMask = treeNode ? TreeMaskOf(row.SectionId, index, treeDepth) : (byte)0,
            Density = section.Opts.Density,
            Height = height,
            ArtSize = SidebarPaneMetrics.ArtSize(section),
            Leading = section.Opts.Artwork
                ? SidebarCover.Folder(SidebarPaneMetrics.ArtSize(section), expanded)
                : null,
            Glyph = section.Opts.Artwork ? null : (expanded ? Icons.FolderOpen : Icons.Folder),
            // R3.1.7a — the folder disclosure rotates too (ChevronRight → 90°), so headers and folders share one motion.
            // W7: moved to the TRAILING cluster (SidebarEntityRow.Create) — the folder mosaic tile stays the leading
            // visual (Spotify's folder rows have no leading chevron either), and this is now the first trailing element.
            DisclosureChevron = SidebarChevron.Disclosure(_folderOpen ??= FolderOpenLive),
            Trailing = FolderTrailing(section, in snapshot, folderId, rootlistItem),
            OnClick = activate,
            Overflow = _o.Acts is not null && _o.MenuOverlay is not null,
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
            OnRename = rename,
            OnMove = move,
            Drag = reordering ? null : (rootlistItem ? _o.TreeDragPayload(in snapshot) : resource),
            DropTarget = drop,
        };
        if (rootlistItem) ApplyTreeSelection(ref spec, folderId.Length > 0 ? snapshot.Id : "", activate);
        // A folder row carries no selection pill (it has no route), but it still needs both drop cues: the bottom band
        // of an expanded header IS the "first child" slot, and the whole D2 outdent gesture happens on folder rows.
        return DropCueOverlay(SidebarEntityRow.Create(spec));
    }

    /// <summary>Which connector columns continue below a realized tree row. The plan is preorder, so the first later
    /// tree entry whose source depth is at or above a level decides that level: equal means a sibling continues; lower
    /// means the branch ended. This stays renderer-side because it is visual chrome, not document/query semantics.</summary>
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

    /// <summary>A hand-picked app route (CollectionShortcuts / StaticLinks). Label + glyph come from <c>ShellNav</c> so a
    /// pinned "Liked Songs" follows the UI culture instead of freezing whatever culture authored it; an unknown route key
    /// in a hand-edited document degrades to the library mark rather than crashing (§C1.8.7).</summary>
    Element RouteRow(SidebarSectionSpec section, SidebarItemSpec item, string sel, int index)
    {
        var dest = ShellNav.Dest(item.Key);
        bool selected = _o.RowSelectsRoute(index, sel);   // one owner: SidebarRowResolve (see EntryRow)
        float height = SidebarPaneMetrics.RowHeight(section);
        string key = item.Key;
        string title = item.LabelOverride is { Length: > 0 } alias ? alias : dest.Title;

        // A route row is a pin drag source when it is a durable application destination and a Reorderable is not
        // already the drag owner. SidebarPinId centrally excludes editor/tooling routes.
        WaveeResourceDragPayload? drag = null;
        if (!_o.TryBandOf(index, out _) && SidebarPinId.FromRoute(key) is not null)
        {
            var destination = SidebarDestination.FromRoute(key, null, title);
            if (destination is { } d) drag = WaveeResourceDragPayload.FromDestination(d, _o.Acts);
        }
        var drop = PinSpec(section, section.Id, index);

        var spec = new SidebarRowSpec
        {
            Key = key,
            Label = title,
            Selected = selected,
            Depth = 0,
            Density = section.Opts.Density,
            Height = height,
            Glyph = SidebarIcons.For(item, dest.Glyph),
            Trailing = CountBadge(section, key),
            OnClick = () => _o.Navigate(key, null),
            Overflow = _o.Acts is not null && _o.MenuOverlay is not null,
            MenuOverlay = _o.MenuOverlay,
            Menu = RouteMenu(section, item, index),
            Drag = drag,
            DropTarget = drop,
        };
        return Indicator(SidebarEntityRow.Create(spec), selected, 0, height, key);
    }

    Func<ContextMenuModel?>? RouteMenu(SidebarSectionSpec section, SidebarItemSpec item, int index)
    {
        if (_o.Acts is not { } acts) return null;
        string key = item.Key;
        var dest = ShellNav.Dest(key);
        var entry = SidebarLibraryEntry.ForRoute(key, dest.Title);
        var snapshot = item;
        return () => Menus.SidebarEntry(acts, in entry, extras: NavExtras(section, index, snapshot, key));
    }

    /// <summary>A hand-placed TRACK (§C1.8.3): click PLAYS, it never navigates, and a hover/focus play glyph replaces the
    /// chevron affordance so the behaviour is legible before the click. Tracks are never pin sources.</summary>
    Element TrackItemRow(SidebarSectionSpec section, SidebarItemSpec item, int index)
    {
        string uri = item.Key;
        var (playing, animated) = _o.RowPlayState(index);
        float height = SidebarPaneMetrics.RowHeight(section);
        string label = item.LabelOverride is { Length: > 0 } alias ? alias
            : item.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(uri);

        var spec = new SidebarRowSpec
        {
            Key = uri,
            Label = label,
            Density = section.Opts.Density,
            Height = height,
            ArtSize = SidebarPaneMetrics.ArtSize(section),
            Leading = section.Opts.Artwork
                ? SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, uri, SidebarPaneMetrics.ArtSize(section))
                : null,
            Glyph = section.Opts.Artwork ? null : SidebarIcons.For(item, Icons.MusicNote),
            Playing = playing,
            PlayingAnimated = animated,
            Track = true,
            Overflow = _o.MenuOverlay is not null && LayoutOnlyMenu(section, item, index, uri) is not null,
            OnClick = () => _o.Play(uri, asTrack: true),
            MenuOverlay = _o.MenuOverlay,
            Menu = LayoutOnlyMenu(section, item, index, uri),
        };
        return SidebarEntityRow.WithPlayTrackHint(SidebarEntityRow.Create(spec));
    }

    /// <summary>An ACTION shortcut (<c>SidebarItemTarget.Action</c>). Resolved ONLY through the extension registry — no new
    /// UI looks up <c>AppActions.All</c> (the M3 forward-compat guardrail). An unavailable target renders
    /// VISIBLE-BUT-DISABLED with the reason as its tooltip; it never vanishes, because a vanishing row makes the user's
    /// own sidebar look broken.</summary>
    Element ActionRow(SidebarSectionSpec section, SidebarItemSpec item, int index)
    {
        var binding = item.Action;
        var reg = _o.Registry;
        var acts = _o.Acts;
        float height = SidebarPaneMetrics.RowHeight(section);

        string label = item.LabelOverride ?? "";
        var icon = default(IconRef);
        bool enabled = false;
        // The default reason covers the two host-shaped cases (no registry / no action bag yet): the row still renders,
        // disabled, saying so — never silently absent.
        string? reason = Loc.Get(SidebarPaneLoc.ExtensionNotNow);
        Action? click = null;

        if (binding is null)
        {
            // An Action item with no binding at all: a half-authored or hand-edited document.
            reason = Loc.Get(SidebarPaneLoc.ExtensionMissing);
        }
        else
        {
            var bound = binding;   // non-nullable local: Execute takes it by `in`
            if (reg is not null && reg.TryGetAction(bound, out var descriptor))
            {
                label = Pick(label, descriptor.Label());
                icon = descriptor.Icon();
            }
            if (reg is not null && acts is { } services)
            {
                var resolution = reg.Resolve(services, bound);
                enabled = resolution.Available;
                reason = resolution.ReasonLocKey is { } key ? Loc.Get(key) : null;
                var registry = reg;
                if (enabled) click = () => registry.Execute(services, in bound);
            }
        }
        if (label.Length == 0) label = Loc.Get(SidebarPaneLoc.ExtensionManage);

        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = label,
            Selected = false,
            Enabled = enabled,
            Density = section.Opts.Density,
            Height = height,
            // Art-wide leading column + the row's own LeadingGap (W7): an action row's label lines up with its siblings'.
            Leading = SidebarPaneIcon.Leading(item.IconOverride, icon, enabled, SidebarPaneMetrics.ArtSize(section)),
            Overflow = _o.MenuOverlay is not null && LayoutOnlyMenu(section, item, index, item.Key) is not null,
            OnClick = click,
            MenuOverlay = _o.MenuOverlay,
            Menu = LayoutOnlyMenu(section, item, index, item.Key),
        };
        Element row = SidebarEntityRow.Create(spec);
        // grow: 1f — the tooltip wrapper is a flex ROW, so without it the DISABLED arm of this row (the only arm that
        // gets a tooltip) shrank to its own label while the enabled arm, returned bare, filled the pane. One row kind
        // rendering at two different widths depending on availability is the narrowest possible version of that bug.
        return reason is { Length: > 0 } r ? ToolTip.Wrap(row, r, grow: 1f) : row;
    }

    static string Pick(string authored, string fallback) => authored.Length > 0 ? authored : fallback;

    /// <summary>The missing-entity retention row (§C1.4): dimmed, from the item's last-known title/art, with a menu that
    /// offers exactly the one honest verb — remove it. NEVER auto-removed. Under a LOCKED document there is no verb at all
    /// (the row is not the user's to edit here), so it degrades to the tooltip alone.</summary>
    Element MissingRow(SidebarSectionSpec section, SidebarItemSpec? item, in SidebarRow row)
    {
        float height = SidebarPaneMetrics.RowHeight(section);
        string label = item?.LabelOverride is { Length: > 0 } alias ? alias
            : item?.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(row.Key);
        string sectionId = section.Id;

        // The context menu of a missing entity is EXACTLY one verb: remove it. Everything else would be a promise about an
        // entity we cannot reach (§C5.1's "context menu limited to Remove").
        Func<ContextMenuModel?>? menu = null;
        if (!_o.Config.ReadOnly && item is { Id.Length: > 0 } present)
        {
            string itemId = present.Id;
            menu = () => new ContextMenuModel(
            [
                // Through SidebarItemCommands: a missing-entity row inside the materialised Shortcuts section is
                // addressed at the SENTINEL id, where `RemoveItem` would be an UnknownSection rejection and the row
                // would sit there refusing to go away. One owner for that choice (Phase 1).
                new MenuFlyoutItem(Loc.Get(SidebarPaneLoc.RemoveItem), ActionIcons.Resolve(ActionIcons.Remove), true,
                    () => _o.Dispatch(SidebarItemCommands.Remove(sectionId, itemId))),
            ]);
        }

        var spec = new SidebarRowSpec
        {
            Key = row.Key,
            Label = label,
            Subtitle = section.Opts.Subtitles ? Loc.Get(SidebarPaneLoc.MissingEntity) : null,
            Enabled = false,
            Depth = row.Depth,
            Density = section.Opts.Density,
            Height = height,
            ArtSize = SidebarPaneMetrics.ArtSize(section),
            Leading = section.Opts.Artwork
                ? SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, row.Key, SidebarPaneMetrics.ArtSize(section))
                : null,
            Glyph = section.Opts.Artwork ? null : SidebarPaneText.Glyph(item, Icons.MusicNote),
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
        };
        // grow: 1f — see WithPlayTrackHint. A retention row is ALWAYS tooltip-wrapped, so without it the missing-entity
        // row was the one row in a section that never filled: dimmed AND narrow, which reads as broken rather than as
        // "this entity is unavailable".
        return ToolTip.Wrap(SidebarEntityRow.Create(spec), Loc.Get(SidebarPaneLoc.MissingEntity), grow: 1f);
    }

    // ── the hero card (§C1.8.2) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>EntityEmbed</c> spotlight card: cover left (circular for artists), title + subtitle, and — when
    /// <c>Display.PlayButton</c> — a circular play button revealed on hover/focus that plays the entity AS A CONTEXT
    /// through the same player verb the detail-page CTA uses. Clicking anywhere else navigates. A missing entity is still
    /// a card: dimmed, from the item's cached title/art, with the play affordance hidden.</summary>
    Element Card(SidebarSectionSpec section, in SidebarRow row, string sel, int index)
    {
        var item = SidebarPaneText.ItemOf(section, row.Key);
        var entries = _o.Plan.Entries;
        bool resolved = row.EntryIndex >= 0 && row.EntryIndex < entries.Count;
        var entry = resolved ? entries[row.EntryIndex] : default;
        float height = SidebarPaneMetrics.CardHeight(section);
        float cover = SidebarPaneMetrics.CardCover(section);

        string title = item?.LabelOverride is { Length: > 0 } alias ? alias
            : resolved && entry.Name.Length > 0 ? entry.Name
            : item?.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(row.Key);
        string? subtitle = resolved ? SidebarPaneText.SubtitleOf(in entry) : Loc.Get(SidebarPaneLoc.MissingEntity);
        bool circular = resolved
            ? entry.Circular || entry.Kind == SidebarEntryKind.Artist
            : item?.EntityKind == SidebarEntityKind.Artist;

        Element art = resolved
            // ForEntry for the same reason GridCell takes it: a spotlight card embeds an ENTRY, so it owes that
            // entry's kind dispatch — a folder tile, a route glyph, and the liked collection's own dynamic cover.
            // The unresolved arm keeps the raw factory: there is no entry to dispatch on yet.
            ? SidebarCover.ForEntry(in entry, cover)
            : SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, row.Key, cover, circular);

        string uri = resolved ? entry.Uri : "";
        bool track = resolved && entry.IsTrack;
        string? route = resolved ? entry.RouteKey : SidebarPinId.FromUri(uri);
        bool selected = _o.RowSelectsRoute(index, sel);   // one owner: SidebarRowResolve (see EntryRow)
        var (playing, animated) = _o.RowPlayState(index);
        bool canPlay = resolved && section.Opts.PlayButton && uri.Length > 0 && entry.IsPlayable;
        var snapshot = entry;
        string rowKey = row.Key;

        Action? activate = null;
        if (track) activate = () => _o.Play(uri, asTrack: true);
        else if (route is { Length: > 0 } r) activate = () => _o.Navigate(r, title);

        var lines = new List<Element>(2)
        {
            new TextEl(title)
            {
                Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (subtitle is { Length: > 0 } s)
            lines.Add(new TextEl(s)
            {
                Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        var text = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 2f, Children = [.. lines],
        };

        var children = new List<Element>(3) { art, text };
        if (canPlay) children.Add(PlayButton(uri, playing && animated, section));

        var card = new BoxEl
        {
            Key = row.Key,
            Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = 12f,
            Padding = new Edges4(8f, 0f, 8f, 0f),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = selected ? WaveeColors.SelectedRest : Tok.FillCardSecondary,
            HoverFill = selected ? WaveeColors.SelectedHover : Tok.FillSubtleSecondary,
            PressedFill = selected ? WaveeColors.SelectedPressed : Tok.FillSubtleTertiary,
            BorderWidth = selected ? 2f : 1f,
            BorderColor = selected ? Tok.AccentDefault : Tok.StrokeCardDefault,
            // R3.1.2: the pane owns the horizontal inset, so the card contributes only its vertical breathing room. Its
            // own 8-DIP left/right margin used to DOUBLE the pane's, which is why a card never lined up with the rows.
            Margin = new Edges4(0f, 2f, 0f, 2f),
            Opacity = resolved ? 1f : 0.55f,
            IsEnabled = resolved,
            Cursor = activate is null ? CursorId.Arrow : CursorId.Hand,
            OnClick = resolved ? activate : null,
            Children = [.. children],
        };
        if (_o.Acts is { } acts && _o.MenuOverlay is { } svc && resolved)
            card = card.WithContextMenu(svc, () => Menus.SidebarEntry(acts, in snapshot,
                extras: NavExtras(section, index, item, rowKey)));
        return card;
    }

    /// <summary>The card's hover/focus-revealed circular play button. The glyph over the accent plate is the on-accent
    /// token (never a literal white — the accent plate is light in the light theme).</summary>
    Element PlayButton(string uri, bool playingNow, SidebarSectionSpec section)
    {
        float box = section.Opts.Density == SidebarDensity.Compact ? 28f : 32f;
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
                    OnClick = () => _o.Play(uri, asTrack: false),
                    Children = [Icon(playingNow ? Icons.Pause : Icons.Play, 14f, Tok.TextOnAccentPrimary)],
                }.Interactive(Interaction.Subtle),
            ],
        };
    }

    // ── grid strips (§C5.1) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One row of a Grid-presentation section: <c>ItemCount</c> tiles from <c>Entries[EntryIndex..]</c>.
    ///
    /// <para>R3.1.2 — the cell edge derives from the REAL pane inset (<c>SidebarPaneMetrics.PaneInsetH</c>) rather than an
    /// assumed <c>Spacing.L</c> that nothing applied, and the strip adds no horizontal padding of its own.</para>
    ///
    /// <para>THE COLUMN COUNT IS THE PLANNER'S, NOT THIS METHOD'S. A width-reactive override here (there used to be a
    /// "≤240 DIP falls back to two columns") silently disagreed with <c>SidebarRowPlanner.EmitProjected</c>, which had
    /// already sliced the entries into GridColumns-wide strips: a 4-item section at 3 columns plans strips of 3 and 1, and
    /// re-wrapping the first at 2 columns made one strip two lines tall and the next one — a ragged grid whose ROW RHYTHM
    /// changed with the pane width. That held while the pane clamped at
    /// <c>ShellResponsiveLayout.NavPaneMinW</c> = 240, where even 4 columns still yielded 50-DIP cells.
    ///
    /// <para>Issue #84 lowered the floor to 180, where 4 (even 2) planner columns would shrink cells below the 40-DIP
    /// floor. Re-deriving the column count here is still wrong for the reason above, so instead this WRAPS fewer of the
    /// planner's cells per visual row: once <c>cols</c> planner columns can no longer each get ≥40 DIP at the current
    /// pane width, only the first <c>fitCols</c> cells of THIS strip render on one line and the remainder wrap onto
    /// their own line(s) at the SAME 40-DIP floor — the strip gets taller, never its cells narrower. Every strip in a
    /// section still wraps identically because they all read the same live pane width, so the row rhythm stays uniform
    /// within a pane width even though it now varies with the pane's own live size.</para></summary>
    Element GridStrip(SidebarSectionSpec section, in SidebarRow row, string sel)
    {
        float pane = _o.ExpandedWidth.Value;       // subscribe → the CELL EDGE re-flows with the pane (the count does not)
        int cols = Math.Clamp(section.Opts.GridColumns, 2, 4);
        const float gap = Spacing.S;
        float avail = pane - SidebarPaneMetrics.PaneInsetH;
        // The planner's own column count, clamped DOWN only far enough to keep every cell at/above the 40-DIP floor —
        // never re-derived from width (see the note above), only capped by it. Pure arithmetic lives in the
        // engine-free SidebarRowGeometry so it is unit-testable without a source-text test.
        int fitCols = SidebarRowGeometry.GridFallbackColumns(cols, avail, gap, SidebarCover.S40);
        float edge = MathF.Min(SidebarPaneMetrics.GridCellMax,
            MathF.Max(SidebarCover.S40, (avail - gap * (fitCols - 1)) / fitCols));

        var entries = _o.Plan.Entries;
        int start = row.EntryIndex;
        int count = row.ItemCount;
        if (start < 0 || count <= 0 || start >= entries.Count) return Blank;
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
        string? route = entry.RouteKey;
        // A grid CELL is not a plan row (one strip row draws several), so it asks the resolver about the ENTRY — the
        // same predicate the row-level sweep ORs across the strip's range.
        bool selected = SidebarRowResolve.EntrySelects(in entry, sel);
        string label = entry.Name.Length > 0 ? entry.Name : SidebarPaneText.ShortUri(entry.Uri);
        var snapshot = entry;
        float artEdge = MathF.Max(SidebarCover.S40, edge - Spacing.S);

        var kids = new List<Element>(3)
        {
            // ForEntry, not Art: a grid cell is an art slot like every other one, and calling the raw cover factory
            // skipped the KIND dispatch — so an app-route entry (Liked Songs, Albums, Podcasts) lost its glyph tile
            // and painted a bare seeded tint, and Liked lost its dynamic cover with it.
            SidebarCover.ForEntry(in entry, artEdge),
            new TextEl(label)
            {
                Size = 12f, Weight = (ushort)(selected ? 600 : 400), Color = selected ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (section.Opts.Subtitles && SidebarPaneText.SubtitleOf(in entry) is { Length: > 0 } sub)
            kids.Add(new TextEl(sub) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        Action? click = null;
        if (entry.IsTrack) click = () => _o.Play(snapshot.Uri, asTrack: true);
        else if (route is { Length: > 0 } r) click = () => _o.Navigate(r, snapshot.Name);

        var cell = new BoxEl
        {
            Key = entry.Id,
            Direction = 1, Width = edge, Shrink = 0f, Gap = Spacing.XS,
            Padding = Edges4.All(Spacing.XS),
            Corners = Radii.CardAll,
            Shadow = Elevation.Card,
            Cursor = click is null ? CursorId.Arrow : CursorId.Hand,
            OnClick = click,
            Children = [.. kids],
        }.Interactive(Interaction.Card);
        cell = cell with
        {
            BorderColor = selected ? Tok.AccentDefault : Tok.StrokeCardDefault,
            BorderWidth = selected ? 2f : 1f,
        };
        if (_o.Acts is { } acts && _o.MenuOverlay is { } svc)
            cell = cell.WithContextMenu(svc, () => Menus.SidebarEntry(acts, in snapshot));
        return cell;
    }

    // ── degraded + affordance rows ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A section that resolved to ZERO rows.
    ///
    /// <para>R3.1.5 BUG FIX — the PINNED branch runs FIRST. It used to sit after the <c>HideBody</c> test, so an authored
    /// (or future default) <c>HideBody</c> on a Pinned section silently deleted the drop zone: the one surface that teaches
    /// drop-to-pin, gone, with no way to get it back. Pinned's empty state IS the drop target and is therefore
    /// unconditional (which is also why a track drag — never carrying the sidebar entity kind — cannot pin anything
    /// anywhere).</para>
    ///
    /// <para>R3.1.6 — a dynamic feed's empty state is a QUIET 32-DIP 11f tertiary hint with per-kind copy, not a 40-DIP
    /// 12f line borrowing <c>nav.history.empty.nothingHere</c>. HideBody deliberately keeps the section header: it remains
    /// a discoverable/configurable authored section, while its live body consumes no empty billboard.</para></summary>
    Element EmptyRow(SidebarSectionSpec section)
    {
        if (section.Kind == SidebarSectionKind.Pinned)
            return Embed.Comp(() => new SidebarPinDropZone(_o.AcceptPinDrop));

        var behavior = SidebarSectionKinds.EmptyBehaviorFor(section.Kind, section.Opts.EmptyBehavior);
        if (behavior == SidebarEmptyBehavior.HideBody)
            return Blank;

        string text = section.Kind switch
        {
            // A tree with no playlists says so AND names the affordance that fixes it — the header "+". It used to
            // borrow the library's copy because the create ROW underneath was the answer; that row is gone.
            SidebarSectionKind.PlaylistTree => _o.SearchText.Length > 0
                ? LibraryEmptyText()
                : Loc.Get(Strings.Sidebar.Empty.Playlists),
            SidebarSectionKind.EntityList => LibraryEmptyText(),
            _ => SidebarPaneText.EmptyText(section.Kind),
        };

        if (behavior == SidebarEmptyBehavior.ActionCard)
            return SidebarEntityRow.Create(new SidebarRowSpec
            {
                Key = section.Id + ":empty",
                Label = text,
                Enabled = false,
                Density = section.Opts.Density,
                Height = SidebarPaneMetrics.RowHeight(section),
                Glyph = section.Kind == SidebarSectionKind.Concerts ? Icons.Calendar : Icons.Grid,
            });

        return new BoxEl
        {
            Height = SidebarPaneMetrics.EmptyHintHeight, AlignItems = FlexAlign.Center,
            Padding = SidebarPaneMetrics.RowInset,
            Children =
            [
                new TextEl(text)
                {
                    Size = 11f, Color = Tok.TextTertiary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    string LibraryEmptyText()
    {
        string query = _o.SearchText;
        return query.Length > 0
            ? Loc.Format(SidebarPaneLoc.SearchEmpty, ("query", query))
            : Loc.Get(SidebarPaneLoc.LibraryEmpty);
    }

    /// <summary>The ACTIONABLE degraded state (§C1.8.5 / the extension failure matrix). Concerts with no location points at
    /// the hub where a location is set; an unresolvable contribution offers "Manage extension", which in M2 means the
    /// customizer — the one surface where the section can be reconfigured or removed.</summary>
    Element Prompt(SidebarSectionSpec section)
    {
        bool concerts = section.Kind == SidebarSectionKind.Concerts;
        string label = concerts ? Loc.Get(SidebarPaneLoc.ConcertsPrompt) : Loc.Get(SidebarPaneLoc.ExtensionManage);
        string? reason = concerts ? null : ExtensionReason(section.Id);
        // Explicit locals, never a ternary against null: a method group has no natural type in that position.
        Action? click = null;
        if (concerts) click = () => _o.Navigate(ConcertRoutes.Hub, null);
        else if (_o.Config.OnCustomize is not null) click = _o.OpenCustomizer;

        Element[] lines = reason is { Length: > 0 }
            ?
            [
                new TextEl(label)
                {
                    Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
                new TextEl(reason)
                {
                    Size = 11f, Color = Tok.TextTertiary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ]
            :
            [
                new TextEl(label)
                {
                    Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ];

        return new BoxEl
        {
            Key = section.Id,
            Direction = 0,
            Height = SidebarRowGeometry.PromptHeight(reason is { Length: > 0 }),
            AlignItems = FlexAlign.Center,
            Gap = Spacing.S,
            // R3.1.2: vertical only — the pane owns the horizontal inset (see the card).
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
                SidebarCover.Glyph(concerts ? Icons.Calendar : Icons.Settings, SidebarCover.S28),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    Gap = Spacing.XXS, Children = lines,
                },
                Icon(Icons.ChevronRight, 10f, Tok.TextTertiary),
            ],
        }.Interactive(Interaction.Card);
    }

    /// <summary>Why a contributed section cannot render, as prose. The binder's availability verdict is the authority —
    /// this never inspects an extension id (the M3 guardrail).</summary>
    string? ExtensionReason(string sectionId)
    {
        var availability = _o.Prefs?.Binder?.AvailabilityOf(sectionId) ?? SidebarContributionAvailability.Missing;
        return availability switch
        {
            SidebarContributionAvailability.Live or SidebarContributionAvailability.Cached => null,
            SidebarContributionAvailability.Missing => Loc.Get(SidebarPaneLoc.ExtensionMissing),
            _ => Loc.Get(SidebarPaneLoc.ExtensionNotNow),
        };
    }

    // ── multi-select + the folder "+" ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is the folder row's "+" the armed drop destination? One flag per SLOT: a slot draws at most one folder
    /// row at a time, and the flag is cleared on leave and on drop.</summary>
    readonly Signal<bool> _folderPlusDrop = new(false);

    /// <summary>Turn a PlaylistTree row into a MULTI-SELECTABLE one.
    ///
    /// <para><c>OnActivate</c> REPLACES <c>OnClick</c> because Ctrl/Shift are the gesture and <c>OnClick</c> throws the
    /// modifiers away; <paramref name="plain"/> is the row's ordinary verb (navigate, or toggle the folder), which a
    /// plain click still runs after clearing the selection. The lane and the plate read the pane's LIVE selection —
    /// the lane through BOUND thunks (a selection change re-skins it with no re-render), the plate as a value the
    /// pane's own epoch bump re-renders.</para></summary>
    void ApplyTreeSelection(ref SidebarRowSpec spec, string entryId, Action? plain)
    {
        if (entryId.Length == 0) return;
        var owner = _o;
        spec.OnClick = null;
        spec.OnActivate = mods => owner.ActivateTreeRow(entryId, mods, plain);
        spec.OnEscape = owner.ClearTreeSelection;
        // PEEKED: this is read inside a press/key handler, where a subscription would have no scope to belong to.
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
            // The row owns its own left inset (the depth ladder + the 3-DIP selection gutter), so the lane adds none.
            leftMargin: 0f);
    }

    /// <summary>A folder row's trailing slot: the quiet count badge (when the section asks for one) and the folder's
    /// own "+". Both, in that order, in a 2-DIP <c>HStack</c> — the count is the fact, the "+" is the verb.</summary>
    Element? FolderTrailing(SidebarSectionSpec section, in SidebarLibraryEntry entry, string folderId, bool rootlistItem)
    {
        Element? badge = section.Kind == SidebarSectionKind.PlaylistTree && section.Opts.CountBadges
            ? SidebarCounts.Badge(entry.ChildCount)
            : null;
        if (!rootlistItem || folderId.Length == 0 || _o.Acts is not { } acts) return badge;
        string name = entry.Name;
        // Keyed inside a row whose own Key is the entry key, so the Comp remounts when this slot recycles onto another
        // folder — which is what makes capturing `folderId`/`name` in the factory safe (props freeze at mount).
        var plus = Embed.Comp(() => new SidebarCreateButton(
            () => FolderActions.NewPlaylistIn(acts, folderId),
            menu: () => FolderCreateMenu(acts, folderId),
            drop: FolderCreateDropSpec(acts, folderId, name),
            dropActive: () => _folderPlusDrop.Value,
            revealOpacity: FolderPlusOpacity,
            box: 20f, glyph: 12f)) with { Key = "folder-create" };
        if (badge is null) return plus;
        return new BoxEl
        {
            Direction = 0, Gap = 2f, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children = [badge, plus],
        };
    }

    /// <summary>The folder "+"'s base opacity, BOUND. Mouse hover is the engine's own reveal cascade
    /// (<c>HoverOpacity</c> on the button, lit by ROW hover with no app hover tracking) — but hover flags are NOT
    /// updated while a drag is live, so this is what shows the affordance mid-gesture: it lights while a rootlist drag
    /// is over the "+" itself, and while the drag is over the ROW (whose own spec publishes the slot), which is exactly
    /// when "you could drop this INTO a new folder here" is the useful sentence.
    /// <para>Reads <c>_scope.Index.Value</c>, never a captured index — the recycled-binding rule.</para></summary>
    float FolderPlusOpacity()
    {
        int i = _scope.Index.Value;
        _ = _o.SubscribeRowEpoch(i);
        return _folderPlusDrop.Value || _o.DropSlotFor(i).PlanIndex == i ? 1f : 0f;
    }

    /// <summary>[New playlist in this folder · New folder inside] — the two verbs the folder's context menu already
    /// carries, reached from the row itself. Built at OPEN time, like every other menu here.</summary>
    ContextMenuModel? FolderCreateMenu(ActionServices acts, string folderId)
    {
        if (acts.Library is null) return null;
        return new ContextMenuModel(new List<MenuFlyoutItem>(2)
        {
            new(Loc.Get(Strings.Sidebar.NewPlaylistHere), ActionIcons.Resolve(ActionIcons.Add), true,
                () => FolderActions.NewPlaylistIn(acts, folderId)),
            new(Loc.Get(Strings.Sidebar.NewFolderInside), ActionIcons.Resolve(ActionIcons.Folder),
                acts.Overlay is not null, () => FolderActions.NewFolder(acts, folderId)),
        });
    }

    /// <summary>Drop playlists on a folder's "+" ⇒ a new SUB-FOLDER inside it, holding them. Rootlist payloads only:
    /// a track set aimed at a folder has nothing to make there, and it is TRANSPARENT rather than refused because it
    /// is merely crossing on its way to a playlist row (the "no" split the row spec documents).</summary>
    DropTargetSpec FolderCreateDropSpec(ActionServices acts, string folderId, string folderName)
    {
        static bool Filing(WaveeResourceDragPayload p)
            => p.RootlistItem && p.Kind is WaveeResourceKind.Playlist or WaveeResourceKind.Folder;

        return Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
            accepts: static p => Filing(p),
            transparent: static p => !Filing(p),
            caption: _ => Strings.Drag.NewFolderInside(folderName),
            onEnter: (_, _) => _folderPlusDrop.Value = true,
            onOver: (_, _) => _folderPlusDrop.Value = true,
            onLeave: _ => _folderPlusDrop.Value = false,
            onDrop: (p, _) =>
            {
                _folderPlusDrop.Value = false;
                FolderActions.NewFolderWith(acts, folderId, WaveeResourceDrop.RootRefs(p));
            },
            visualPolicy: DropTargetVisualPolicy.Spotlight,
            // Rule 6 / D15 — an organisation drag must not dim the app it is happening inside.
            spotlightWhen: static s => WaveeResourceDrag.Unwrap(s.Payload) is not { RootlistItem: true });
    }

    // ── shared row plumbing ──────────────────────────────────────────────────────────────────────────────────────────

    Element? LeadingArt(SidebarSectionSpec section, in SidebarLibraryEntry entry, SidebarItemSpec? item)
    {
        if (!section.Opts.Artwork) return null;
        float size = SidebarPaneMetrics.ArtSize(section);
        // An authored icon override beats the artwork slot: it is the user's explicit choice for this row.
        if (item?.IconOverride is { Length: > 0 } name)
            return SidebarCover.Glyph(SidebarIcons.Glyph(name, Icons.MusicNote), size,
                entry.Circular || entry.Kind == SidebarEntryKind.Artist);
        // A CONCERT is projected as a route whose stamp is the event date; §C1.8.5 wants that date, not a glyph tile.
        if (section.Kind == SidebarSectionKind.Concerts && entry.SortStamp > 0)
            return SidebarPaneText.DateBlock(entry.SortStamp, size);
        return SidebarCover.ForEntry(in entry, size);
    }

    /// <summary>The trailing slot for a FEED row: §C1.8.4's age badge on a new release ("3d"). Everything else has none —
    /// the count badge belongs to route rows and the now-playing equalizer is the row primitive's own slot.</summary>
    static Element? TrailingBadge(SidebarSectionSpec section, in SidebarLibraryEntry entry)
    {
        if (section.Kind == SidebarSectionKind.PlaylistTree && section.Opts.CountBadges &&
            entry.Kind == SidebarEntryKind.Playlist)
            return SidebarCounts.Badge(entry.ChildCount);
        if (section.Kind == SidebarSectionKind.NewReleases &&
            SidebarPaneText.AgeBadge(entry.SortStamp) is { Length: > 0 } age)
            return new TextEl(age) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1 };
        return null;
    }

    /// <summary>R3.1.4 — the library-shortcut count, through the ONE quiet badge (<see cref="SidebarCounts"/>). Classic's
    /// accent <c>InfoBadge.Count</c> pill is gone: this is the only count renderer left in the sidebar.</summary>
    Element? CountBadge(SidebarSectionSpec section, string routeKey)
    {
        if (!section.Opts.CountBadges) return null;
        int index = routeKey switch { "albums" => 0, "artists" => 1, "liked" => 2, "podcasts" => 3, _ => -1 };
        if (index < 0) return null;
        var store = _o.Store;
        if (store is null) return null;
        var stats = store.Stats;
        if ((LoadState)stats.State.Value != LoadState.Ready || stats.Value.Value is not { } s)
            return SidebarCounts.Badge(null);
        int count = index switch { 0 => s.Albums, 1 => s.Artists, 2 => s.LikedSongs, _ => s.Podcasts };
        return SidebarCounts.Badge(count);
    }

    /// <summary>Attach the item-owned SelectionIndicator. Keeping it shape-stable is load-bearing for recycling: a slot
    /// never inherits an animated transform from the route it represented one window earlier.</summary>
    Element Indicator(Element row, bool selected, int depth, float height, string? route)
    {
        if (!float.IsFinite(height) || height <= 0f) height = SidebarRowMetrics.ClassicHeight;
        _pillState = new SidebarPillState(
            Route: route,
            Selected: selected,
            Indent: SidebarRowMetrics.IndentFor(depth),
            Top: MathF.Max(0f, (height - SidebarSelectionPill.PillH) * 0.5f));
        return ZStack(DropPlate(), row, Embed.Comp(() => new SidebarSelectionPill(_o, _pillProbe ??= PillState)),
                      InsertionLine());
    }

    /// <summary>A row that has no selection pill but still owns the two drop cues (a folder header).</summary>
    Element DropCueOverlay(Element row) => ZStack(DropPlate(), row, InsertionLine());

    /// <summary>
    /// THE "INTO" PLATE. Mounted once per row, ALWAYS, as the ZStack's FIRST child — under the row, so text, artwork
    /// and the <c>|||</c> glyph are never tinted and the row's own hover/selected fills still composite over it. The
    /// pixels are what the row's <c>Fill</c> used to draw: accent at 0.18 α with a 1-DIP accent border, corner 4 to
    /// match the row. <c>HitTestVisible = false</c> keeps the whole subtree out of <c>HitAny</c>, so hover and the drop
    /// itself still reach the row underneath.
    ///
    /// <para><b>Why it left the row.</b> <c>SidebarEntityRow.Create</c> bound <c>Fill</c>/<c>BorderColor</c> as thunks
    /// that folded the cue with <c>enabled &amp;&amp; selected</c> — captured as VALUES at the render that built them.
    /// The reconciler registers a node's bindings at MOUNT and never again (Update rewrites props, not bindings), so a
    /// same-keyed re-render — which is exactly what <c>RefreshSelection</c>'s epoch bump produces — left the OLD thunk
    /// in place: the resting route plate stayed on the previous route until the row scrolled out and remounted. The
    /// plate is now bound off the LIVE published slot and the row's fills are static values again, re-asserted on
    /// every reconcile (and back on the 83 ms <c>BrushTransition</c> cross-fade for free).</para>
    ///
    /// <para>Every thunk reads <c>_scope.Index.Value</c> — never a captured index — for the same reason
    /// <see cref="InsertionLine"/> does. Pinned by <c>SidebarPaneInvariantTests</c>.</para>
    /// </summary>
    Element DropPlate() => new BoxEl
    {
        // No explicit size: an auto-sized ZStack child fills its slot (FlexLayout.ArrangeZStack), so the plate is the
        // row's own rect at every density without a second height ladder to keep in sync.
        Key = "drop-plate",
        Corners = CornerRadius4.All(4f),
        BorderWidth = 1f,
        Fill = Prop.Of(() =>
        {
            int i = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(i);
            return SidebarDropCue.DrawsPlate(_o.DropSlotFor(i).Kind)
                ? Tok.AccentDefault with { A = 0.18f } : ColorF.Transparent;
        }),
        BorderColor = Prop.Of(() =>
        {
            int i = _scope.Index.Value;
            _ = _o.SubscribeRowEpoch(i);
            return SidebarDropCue.DrawsPlate(_o.DropSlotFor(i).Kind) ? Tok.AccentDefault : ColorF.Transparent;
        }),
        HitTestVisible = false,
    };

    /// <summary>
    /// THE INSERTION LINE. Mounted once per row, ALWAYS — never conditionally — and every prop bound off the LIVE slot
    /// index (<c>SidebarSelectionPill</c>/<see cref="PillState"/>'s discipline). Conditional mounting would need a
    /// re-render per pointer move, which is precisely what a cue that must track the pointer cannot afford.
    ///
    /// <para><b>THE LIVE INDEX IS THE WHOLE FIX.</b> The line's key is the constant <c>"drop-line"</c>, so when an
    /// ItemsView slot recycles onto another plan row the reconciler pairs it by <c>(Key, type)</c> and UPDATES it —
    /// and Update never re-registers a node's bindings, which are wired at MOUNT only. A thunk that captured the plan
    /// <c>index</c> (or the row <c>height</c>) therefore kept answering for the row this slot was FIRST mounted with:
    /// after an auto-scrolled drag two carets were lit at once — the armed row's and a recycled slot's ghost — and the
    /// slot that had inherited index 0's binding could never draw "before the first row". Reading
    /// <c>_scope.Index.Value</c> subscribes the binding to the recycle write, and the row epoch beside it to a
    /// same-index re-plan. Pinned by <c>SidebarPaneInvariantTests</c>.</para>
    ///
    /// <para><b>Why a line at all.</b> Before/Inside/After were three outcomes behind one pixel-identical accent plate
    /// (D1), so the surface could not say which of them a drop meant. The plate now means exactly ONE thing — Into, a
    /// deposit — and the line means exactly one thing — an ORDERING, at the depth it is drawn at. The terminal dot at
    /// the left cap is what makes a 2-DIP hairline read as an insertion caret rather than as a divider.</para>
    /// </summary>
    Element InsertionLine() => new BoxEl
        {
            Key = "drop-line",
            Height = SidebarDropCue.LineThickness,
            Width = Prop.Of(() =>
            {
                int i = _scope.Index.Value;
                _ = _o.SubscribeRowEpoch(i);
                return SidebarDropCue.LineWidth(_o.ContentWidth, _o.DropSlotFor(i).Depth);
            }),
            Corners = CornerRadius4.All(SidebarDropCue.LineCorner),
            Fill = Tok.AccentDefault,
            Opacity = Prop.Of(() =>
            {
                int i = _scope.Index.Value;
                _ = _o.SubscribeRowEpoch(i);
                return SidebarDropCue.DrawsLine(_o.DropSlotFor(i).Kind) ? 1f : 0f;
            }),
            Transform = Prop.Of(() =>
            {
                int i = _scope.Index.Value;
                _ = _o.SubscribeRowEpoch(i);
                var slot = _o.DropSlotFor(i);
                // THE ONE TREE-CONTENT ORIGIN. The caret starts where the row at that depth starts DRAWING —
                // gutter + connector cells (no reserved disclosure cell any more; W7 moved the folder chevron to the
                // TRAILING cluster) — not at `IndentFor(depth)`, which is the row's outer padding ladder and paints
                // the line roughly one whole level to the left of what it means (F2).
                return Affine2D.Translation(SidebarRowGeometry.TreeContentX(slot.Depth),
                                            SidebarDropCue.LineY(slot.Kind, _o.RowExtentOf(i)));
            }),
            HitTestVisible = false,
            Children =
            [
                // The terminal dot sits at the caret's left cap, centred on the hairline.
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

    /// <summary>The row's structural drop facts, straight off the published plan. Everything the resolver needs that the
    /// PAYLOAD does not decide.
    /// <para><c>NextVisibleDepth</c> is the whole depth-ambiguity story: it is the depth of the next visible tree row, or
    /// 0 when this is the last one, and a slot is ambiguous exactly when it is SHALLOWER than this row — i.e. "after the
    /// last visible child of a (possibly nested) folder", the one gesture D2 got silently wrong.</para></summary>
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
            FolderExpanded: entry.IsFolder && (_o.Prefs?.IsFolderExpanded(entry.FolderId) ?? true),
            FolderHasChildren: hasChild,
            Depth: entry.Depth,
            NextVisibleDepth: nextDepth,
            // Completed at hover: only the payload knows whether this row's centre has anything to take.
            CenterAccepts: entry.IsFolder,
            SourceIsSelf: false,
            SortedNonCustom: _o.TreeSortedNonCustom,
            RootlistLoaded: true);
    }

    /// <summary>The tree's closing gutter: 24 DIP of nothing that owns ONE slot — "top level, at the end". It exists
    /// because that slot had no target at all and the create row below it happily accepted rootlist payloads, turning a
    /// drag past the end of the list into a duplicated playlist (D3).</summary>
    Element TreeEndRow(SidebarSectionSpec section, in SidebarRow row, int index)
    {
        var facts = new SidebarRowFacts(
            IsFolder: false, FolderExpanded: false, FolderHasChildren: false,
            Depth: 0, NextVisibleDepth: 0, CenterAccepts: false,
            SourceIsSelf: false,
            SortedNonCustom: _o.TreeSortedNonCustom, RootlistLoaded: true) { IsListEnd = true };
        // A synthetic destination: the slot is expressed against the last TOP-LEVEL entry at commit time, so the row
        // itself stands for no entity and carries no name of its own.
        var target = new WaveeResourceDragPayload(WaveeResourceKind.Route, row.SectionId, "", "", RootlistItem: true);
        var drop = _o.ResourceDropSpec(row.SectionId, -1, null, null, target, index, rootFacts: facts);
        var owner = _o;
        // The line reads the LIVE slot index like every other row's — a plan republish that shifts the gutter's index
        // under this same-keyed slot would otherwise leave its caret answering for the row it first mounted with.
        return ZStack(
            new BoxEl
            {
                Key = "tree-end",
                Height = SidebarRowGeometry.TreeEndHeight,
                Width = Prop.Of(() => owner.ContentWidth),
                DropTarget = drop,
            },
            InsertionLine());
    }

    /// <summary>THE PILL'S ONE LIVE READ. The indicator's opacity is BOUND to this (never a mount-time literal), so it is
    /// re-derived by the row's own epoch on every edge that can change it — a navigation, a republish, a recycle — with
    /// no re-render and no dependence on whether the pane's motion transaction ran.
    ///
    /// <para><c>Index</c> is read as a signal (a recycle re-runs the bind), the row epoch is what
    /// <c>RefreshSelection</c> bumps on a route edge, and the route itself is PEEKED — subscribing here would only
    /// re-add every realized pill to the route fanout the epoch mechanism exists to avoid.</para>
    ///
    /// <para>The verdict is the pane's (<c>SidebarRowResolve.SelectsRoute</c> — the same owner the selection sweep and
    /// the row's own plate use, so the pill can never disagree with the skin under it), AND-ed with the route this pill
    /// was drawn for. Playback is deliberately absent: <c>RowSelectsRoute</c> is route-only, so the now-playing row
    /// draws <c>|||</c> and never the pill.</para></summary>
    SidebarPillState PillState()
    {
        int index = _scope.Index.Value;
        _ = _o.SubscribeRowEpoch(index);
        string selectedRoute = _o.SelectedRoutePeek;
        return _pillState.For(selectedRoute, _o.RowSelectsRoute(index, selectedRoute));
    }

    /// <summary>A PINNED row is also a drop target, so dragging a playlist/album/artist onto the pinned band pins it AT
    /// that position. Deliberately scoped to the band (never the whole pane): a pane-wide accept would pin an entity the
    /// moment a row was nudged, which is the accidental-pin hazard Classic avoided by scoping too.</summary>
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
            ? index - band.Start : -1;

    /// <summary>A layout-only menu (action shortcuts, hand-placed tracks): no entity verbs, just the navbar extras.
    /// Null when this row has nothing to move or remove, so a right-click opens nothing rather than an empty flyout.</summary>
    Func<ContextMenuModel?>? LayoutOnlyMenu(SidebarSectionSpec section, SidebarItemSpec item, int index, string key)
    {
        if (NavExtras(section, index, item, key).IsEmpty) return null;
        return () => Menus.LayoutOnly(NavExtras(section, index, item, key));
    }

    /// <summary>Move up / Move down / Move to folder… / Remove for this row, or null when none apply. Built at menu-open
    /// time from the live plan index (the slot recycles) so a pin mutation between render and right-click cannot offer a
    /// dead move.
    ///
    /// <para>Pinned rows do not get a Remove extra: Unpin already lives in the pin-state slot of the entity menu, and
    /// duplicating it as a trailing destructive would show the same verb twice. Authored items (StaticLinks /
    /// CustomGroup / Shortcuts) get Remove through <see cref="SidebarItemCommands"/>, which is also how a missing-entity
    /// row already drops itself.</para>
    ///
    /// <para>A ROOTLIST TREE row gets the same two labels over a different list (<see cref="TreeMoves"/>) plus
    /// "Move to folder…". Reordering the rootlist used to be a drag and nothing else (D12); these are the menu half of
    /// the fix, and <c>SidebarRowSpec.OnMove</c> (Alt+↑/↓) is the keyboard half. "Move out of {parent}" is NOT added
    /// here — it already lives in the entity menu itself, on every surface that shows the row, and a second copy in the
    /// extras would show the verb twice on a tree row.</para>
    ///
    /// <para>The result is SPLIT (<see cref="SidebarMenuExtras"/>): the positional verbs go into <c>Organize</c>, which
    /// the playlist and folder arms fold into their <b>Organize ▸</b> submenu beside Pin and "Move out of {parent}";
    /// Remove goes into <c>Trailing</c> and stays at the bottom. Appending all of them flat is what used to render
    /// "Move up" below "Invite collaborators".</para></summary>
    SidebarMenuExtras NavExtras(SidebarSectionSpec section, int planIndex, SidebarItemSpec? item, string key)
    {
        int at = -1, count = 0;
        if (_o.TryBandOf(planIndex, out var band) && string.Equals(band.SectionId, section.Id, StringComparison.Ordinal))
        {
            at = planIndex - band.Start;
            count = band.Count;
        }
        else if (section.Kind == SidebarSectionKind.Pinned && _o.Prefs is { } prefs)
        {
            // The reorder band disarms while a pinned folder is expanded (slot math would move the wrong pin). Explicit
            // Move up/down still address the pin store, matching the section-card menu's P6 rule.
            string id = SidebarPinId.Canonical(key) ?? key;
            at = prefs.Pins.IndexOf(id);
            count = prefs.Pins.Count;
        }

        bool removable = !_o.Config.ReadOnly
            && item is { Id.Length: > 0 }
            && section.Kind != SidebarSectionKind.Pinned
            && SidebarSectionKinds.AcceptsItems(section.Kind);

        var layout = SidebarNavLayout.Decide(at, count, removable);
        var (tree, entryId) = TreeMoves(section, planIndex);
        if (layout.IsEmpty && tree.IsEmpty) return default;

        var rows = new List<MenuFlyoutItem>(4);
        string sectionId = section.Id;
        if (layout.MoveUp)
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveUp),
                new IconRef { Glyph = Icons.ChevronUp, Font = Theme.IconFont }, true,
                () => _o.MoveRowByKey(sectionId, key, -1)));
        if (layout.MoveDown)
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveDown),
                new IconRef { Glyph = Icons.ChevronDown, Font = Theme.IconFont }, true,
                () => _o.MoveRowByKey(sectionId, key, 1)));
        // Right-clicking a row that is INSIDE a multi-selection addresses the SELECTION, not the row: the positional
        // verbs are meaningless for N rows at once (there is no single "up") and would silently move only the one under
        // the cursor, so they are replaced by the one verb a set can honour. Alt+↑/↓ stays single-row, deliberately —
        // it is a nudge, not a batch.
        bool batch = entryId.Length > 0 && _o.TreeSelection.Count >= 2 && _o.TreeSelection.Contains(entryId);
        if (_o.Acts is { } treeActs && !batch)
        {
            // The ROOTLIST verbs. Same two labels as the layout ones above, a different list underneath: these move the
            // real rootlist through `FolderActions`, not a document's item order. They are mutually exclusive with the
            // band arm by construction (a Reorderable-wrapped row is excluded in TreeMoves), so the menu never shows
            // "Move up" twice.
            if (tree.MoveUp)
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveUp),
                    new IconRef { Glyph = Icons.ChevronUp, Font = Theme.IconFont }, true,
                    () => FolderActions.MoveUp(treeActs, entryId)));
            if (tree.MoveDown)
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveDown),
                    new IconRef { Glyph = Icons.ChevronDown, Font = Theme.IconFont }, true,
                    () => FolderActions.MoveDown(treeActs, entryId)));
            if (tree.MoveToFolder)
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveToFolder),
                    ActionIcons.Resolve(ActionIcons.Folder), true,
                    () => FolderActions.MoveTo(treeActs, entryId)));
        }
        else if (_o.Acts is { } batchActs && batch)
        {
            int n = _o.TreeSelection.Count;
            rows.Add(new MenuFlyoutItem(Strings.Menu.MoveManyToFolder(n),
                ActionIcons.Resolve(ActionIcons.Folder), true,
                // The picker takes N ids and excludes every selected subtree through the SAME batch legality check the
                // drag cue asks — one destination rule, so it can never offer a folder a drop would refuse.
                () => RootlistFolderPicker.Open(batchActs, _o.OrderedTreeSelection())));
        }
        // SELECT — the pointer-only way into check mode, and the only one: there is no chord a user discovers, and a
        // permanently visible checkbox lane would cost every tree row 24 DIP for a gesture most sessions never use.
        // Exit is Escape, or clearing the last item.
        if (entryId.Length > 0 && _o.Acts is not null && !_o.TreeSelection.CheckLaneVisible)
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.Select),
                new IconRef { Glyph = Icons.Check, Font = Theme.IconFont }, true,
                () => _o.BeginTreeCheckMode(entryId)));
        // Remove is the ONE trailing verb: it deletes the row from the user's document, so it belongs at the bottom
        // with the destructive block, not inside Organize ▸ next to the moves.
        List<MenuFlyoutItem>? trailing = null;
        if (layout.Remove)
        {
            string itemId = item!.Id;
            trailing = [new MenuFlyoutItem(Loc.Get(SidebarPaneLoc.ItemRemove),
                ActionIcons.Resolve(ActionIcons.Remove), true,
                () => _o.Dispatch(SidebarItemCommands.Remove(sectionId, itemId)))];
        }
        return new SidebarMenuExtras(rows.Count > 0 ? rows : null, trailing);
    }

    /// <summary>Which ROOTLIST move verbs this plan row offers, and the projection entry id they address.
    ///
    /// <para>Decided against the published tree (<c>SidebarProjectionInput.PlaylistTree</c>), not against the plan: the
    /// plan is expansion-filtered, and "my previous sibling" must be the real one even when the row above it on screen
    /// belongs to a collapsed folder. Positions come from the SIBLING RUN — the entries sharing this one's parent — so
    /// the verb disappears at the run's ends instead of moving the row into a neighbouring folder.</para>
    ///
    /// <para>Empty for anything that is not a rootlist playlist/folder, and for a row inside a REORDER BAND: a
    /// <c>Reorderable</c> owns that row's ordering (and supplies its own Move up/down through
    /// <see cref="SidebarNavLayout"/>), so offering the rootlist verbs there would put two different orderings behind
    /// one label.</para></summary>
    (SidebarTreeNavLayout Layout, string EntryId) TreeMoves(SidebarSectionSpec section, int planIndex)
    {
        if (section.Kind != SidebarSectionKind.PlaylistTree || _o.Acts is null) return (default, "");
        if (_o.TryBandOf(planIndex, out _)) return (default, "");
        var rows = _o.Plan.Rows;
        var entries = _o.Plan.Entries;
        if ((uint)planIndex >= (uint)rows.Count) return (default, "");
        var row = rows[planIndex];
        if (row.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader)) return (default, "");
        if ((uint)row.EntryIndex >= (uint)entries.Count) return (default, "");
        var entry = entries[row.EntryIndex];
        if (entry.Kind is not (SidebarEntryKind.Playlist or SidebarEntryKind.Folder) || entry.Id.Length == 0)
            return (default, "");

        var tree = _o.Prefs?.Binder?.CurrentInput.PlaylistTree;
        // STRUCTURE from the tree, LEGALITY from the store's marker stream — the same split the drop cue uses, so
        // "Move to folder…" can never offer a destination a drag would refuse (or hide one it would accept).
        var run = RootlistTreeNav.Siblings(tree, entry.Id);
        return (SidebarTreeNavLayout.Decide(in run, RootlistTreeNav.HasDestinations(tree, _o.RootlistMarkers, entry.Id)),
                entry.Id);
    }

}
