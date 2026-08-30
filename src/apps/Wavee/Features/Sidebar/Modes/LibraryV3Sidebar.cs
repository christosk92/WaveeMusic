using System;
using System.Collections.Generic;
using FluentGpu.Controls;   // Route
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>
/// Mode B — Library V3: Spotify's unified "Your Library" interaction model in Wavee's Fluent language. Mounted only by
/// <c>SidebarHost</c>, under the <c>"sidebar.v3"</c> key; the ctor shape is shared with every mode component so the host's
/// switch stays uniform.
///
/// <para>R3.0.3 — V3 IS NOW A DOCUMENT PLUS CHROME. It used to ship its own pane container (a private index map, list, row
/// and rail — four files, a second row vocabulary, a second height ladder), which is exactly why four left insets, two
/// badge styles and two selection mechanisms existed in one app. All of that is retired: the content is an EPHEMERAL
/// document (<see cref="LibraryV3Document"/>) rendered by the ONE <see cref="SidebarPane"/>, and this file is the seam
/// between V3's state and that renderer:</para>
/// <list type="number">
/// <item><b>Document</b> — the synthesized layout for the live filter/qualifier/sort/direction/view/search/drill state.</item>
/// <item><b>Input</b> — the planner input, shaped so the pane's rows ARE the published V3 projection: the pin band and the
/// library as two windows over one list, re-grouped into tree order by <see cref="LibraryV3View"/>. No filter, sort or
/// search logic is re-implemented here; <c>SidebarBinderPipeline</c> stays the one owner.</item>
/// <item><b>ModeEpoch</b> — the fold of that state, so a chip tap re-plans the pane and re-skins the realized rows.</item>
/// <item><b>Head</b> — <see cref="LibraryV3Chrome"/>: the header band, toolbar, chips, breadcrumb and empty/error states,
/// unchanged from the landed surface.</item>
/// <item><b>IsReorderableSection / CommitReorder / ActivateFolder</b> — the three behaviours the shared renderer cannot
/// guess: V3's LOCAL custom order (§3.2.9) and its narrow drill-in navigation (Revision 2).</item>
/// </list>
///
/// <para>The two always-mounted layers (expanded measured at the persisted open width + the 56-DIP rail), selection, the
/// context menus, drop-to-pin, the skeletons and every metric now come from the shared pane — so V3 cannot drift from
/// Classic or Curated by construction.</para>
/// </summary>
sealed class LibraryV3Sidebar : Component
{
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;
    readonly Signal<bool> _compact;
    readonly Signal<float> _expandedWidth;
    readonly bool _inDrawer;

    /// <summary>The pin band handed to the planner: a WINDOW over the published projection's leading pins, never a copy.</summary>
    readonly LibraryV3Window _pins = new();

    /// <summary>The materialized custom order a reorder commit writes. Reused — a commit happens at human rate, but the
    /// list is the whole visible order (F.7.10) and a 10k library must not allocate it twice.</summary>
    readonly List<string> _orderScratch = new(64);

    SidebarPreferences? _prefs;
    LibraryV3Session? _session;

    // The document cache: `Document` is invoked on EVERY pane render, and a rebuilt-per-render document would allocate a
    // handful of records per frame the pane paints. Keyed on the state it was built from (a record struct — value equality)
    // AND on the shortcut band, which is part of the document since Phase 1: a band edit bumps LayoutVersion and re-plans,
    // and without the band in the key the re-plan would run against the STALE cached document. Compared by reference —
    // every band edit rebuilds the list in the reducer, so a reference match proves the content matches.
    LibraryV3DocState _docState;
    IReadOnlyList<SidebarItemSpec>? _docTopBar;
    SidebarCustomLayout? _docLayout;

    // The view (order) cache. The pane shapes its input TWICE per plan (the expanded plan and the rail plan), so without
    // this the re-grouping pass would run twice per state change.
    long _viewEpoch = long.MinValue;

    public LibraryV3Sidebar(Signal<Route> route, Action<string, string?> go, Signal<bool> compact,
                            Signal<float> expandedWidth, bool inDrawer = false)
    {
        _route = route; _go = go; _compact = compact; _expandedWidth = expandedWidth; _inDrawer = inDrawer;
    }

    public override Element Render()
    {
        _prefs = UseContext(SidebarPreferences.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var store = UseContext(LibraryStore.Slot);

        // Mount-once, reference-stable — a legal frozen prop for the chrome components below. Its mutable service fields are
        // refreshed here each render (the landed WaveeSidebar._acts pattern); its signals are the ephemeral session state.
        var session = UseMemo(() => new LibraryV3Session(_route, _go, _expandedWidth, _inDrawer), DepKey.Empty);
        _session = session;
        session.Prefs = _prefs;
        session.Library = lib;
        session.Acts = UseContext(ActionServices.Slot);

        // The projection's INPUTS. The binder reads LibraryStore's cells but does not warm them, and V3 shows every kind in
        // one list, so all five warmers plus the tree and the added-at side-channel are armed here. Each Ensure is
        // idempotent (a latched bool), so this is one call per process, not per render.
        if (store is { } s)
        {
            s.EnsurePlaylistTree();
            s.EnsurePlaylists();
            s.EnsureAlbums();
            s.EnsureArtists();
            s.EnsureShows();
            s.EnsureAddedAt();
            s.EnsureStats();
        }

        // MEMOS, not raw width reads: a seam drag writes the width every frame, and a memo's equality cut-off means the
        // document epoch moves only when the derived INTEGER (or boolean) does.
        var columns = UseComputed(ComputeColumns);
        session.Columns = columns;
        var narrow = UseComputed(ComputeNarrow);
        bool drillCapable = narrow.Value;

        // Publish the folder mode (read at click time by ActivateFolder) and drop any drill level once the pane is wide
        // enough to disclose inline — inline disclosure and a drill stack are two answers to the same question, so only one
        // may ever be live. In an EFFECT: both are signal writes.
        UseLayoutEffect(() =>
        {
            session.NarrowFolders.SetIfChanged(drillCapable);
            if (!drillCapable) session.ResetDrill();
        }, DepKey.From(drillCapable ? 1 : 0));

        // Built ONCE and frozen into the pane (the component-props contract). Every member is a delegate or a flag, so it
        // reads live state at the pane's render time.
        var config = UseMemo(() => new SidebarPaneConfig
        {
            Design = SidebarDesign.LibraryV3,
            ScrollKeyPrefix = "sidebar.v3",
            Document = BuildDocument,
            Input = ShapeInput,
            ModeEpoch = ReadModeEpoch,
            // V3's OWN sections are all title-less (§3.2.7), so they plan no header row and there is nothing to collapse.
            // Phase 1's Shortcuts section is titled and therefore does plan one — deliberately, it is the app's
            // navigation and needs a name — but it stays non-collapsible here for the same reason as everywhere else:
            // the band is materialised from `TopBar` and has no persisted Collapsed bit to write.
            SetSectionCollapsed = null,
            // The document is ephemeral and the CHROME owns every piece of its state: no inline section controls, no
            // document commands (a Dispatch here would edit the CURATED document), no customize CTA.
            ReadOnly = true,
            // V3 has its own library-only search in the toolbar (§3.2.5), writing the mode-global V3Search the projection
            // binder folds in — the pane's pinned search head would be a second, competing query.
            SearchHead = false,
            Head = ChromeHead,
            // PHASE 1 — the shortcut band is no longer a config delegate at all: it is the FIRST SECTION of the document
            // BuildDocument returns (Decision A). The renderer therefore needs no `NavBand`/`RailHead` seam, the 56-DIP
            // rail form rides the planner's existing `ShowInRail`, and V3 finally has a navigation section of its own.
            // §3.2.3 keeps the design switch in V3's own overflow menu (it embeds SidebarLayoutMenu.Rows as a sub-menu), so
            // the pane must not hang a second layout button off a header. The RAIL keeps its copy: a collapsed pane has no
            // overflow menu to reach.
            ShowLayoutMenu = false,
            RailLayoutMenu = true,
            RailFooter = BuildRailFooter,
            IsReorderableSection = IsSectionReorderable,
            TreeSortedNonCustom = TreeSortedNonCustom,
            ClampReorderSlot = ClampReorderSlot,
            CommitReorder = CommitPaneReorder,
            ActivateFolder = session.ActivateFolder,
            DisclosesFoldersInline = session.DisclosesFoldersInline,
            OnCreatePlaylist = session.CreatePlaylist,
        }, DepKey.Empty);

        return Embed.Comp(() => new SidebarPane(config, _route, _go, _compact, _expandedWidth, _inDrawer));
    }

    // ── the document ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The live document. Invoked inside the PANE's render, so every signal <see cref="LibraryV3Session.ReadState"/>
    /// reads subscribes the pane — which is what makes a chip tap re-plan it.</summary>
    SidebarCustomLayout BuildDocument()
    {
        if (_session is not { } session) return SidebarCustomLayout.Empty;
        var state = session.ReadState();
        // The shortcut band lives on the CURATED document (one global list), so V3 reads it from the preference service
        // rather than from its own synthesized layout. The read is unconditional and comes first: the pane already
        // subscribes LayoutVersion through PlanDep, and short-circuiting behind the cache would only hide a stale band.
        var topBar = _prefs?.TopBar;
        if (_docLayout is { } cached && _docState.Equals(state) && ReferenceEquals(_docTopBar, topBar)) return cached;
        _docState = state;
        _docTopBar = topBar;
        _docLayout = LibraryV3Document.Build(in state, topBar);
        return _docLayout;
    }

    Element? ChromeHead()
    {
        if (_session is not { } session) return null;
        // KEYED and type-stable: the pane rebuilds this element on every render, and the component instance behind it must
        // survive (its hooks own the search field's focus latch and the qualifier auto-correct effect).
        return Embed.Comp(() => new LibraryV3Chrome(session)) with { Key = "v3-chrome" };
    }

    /// <summary>Everything OUTSIDE the projection that changes what the plan or a realized row draws, folded into one int:
    /// the whole V3 view state. The pane reads it in its plan <c>DepKey</c> AND in every row's epoch, so a filter/sort/view
    /// change re-plans and re-skins the realized window without the list rebuilding.</summary>
    int ReadModeEpoch() => _session is { } session ? session.ReadState().GetHashCode() : 0;

    // ── the planner input ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shape the binder's planner input so the pane renders V3's OWN index space.
    ///
    /// <para>The published projection (<c>SidebarPreferences.Entries</c>) is already the V3 list: filtered by the lens,
    /// qualifier-compacted, searched, sorted (custom order included) and pins-first. This hands the pane exactly that, as
    /// two windows over one list — the leading pin band to the <c>Pinned</c> section, the remainder (re-grouped into tree
    /// order, or sliced to one folder level) to the library section. Nothing is re-filtered and nothing is re-sorted, which
    /// is what keeps ONE implementation of V3's rules in the app.</para>
    ///
    /// <para><c>ExpandedFolders = null</c> means "nothing is collapsed" to the planner, and that is correct rather than
    /// lax: a collapsed folder's children are already ABSENT from the published projection (the binder projects with
    /// <c>isFolderExpanded</c>), so a second expansion test would only be able to hide rows the projection deliberately
    /// included.</para>
    /// </summary>
    SidebarProjectionInput ShapeInput(SidebarProjectionInput input)
    {
        if (_session is not { } session || _prefs is not { } prefs) return input;

        var state = session.ReadState();
        var entries = prefs.Entries;
        var published = entries.Current;
        int pinCount = entries.PinCount;
        if (pinCount < 0) pinCount = 0;
        if (pinCount > published.Count) pinCount = published.Count;

        int skip = state.PinsBandVisible ? pinCount : 0;
        bool group = LibraryV3Document.FoldersApply(in state);
        int revision = prefs.Binder?.Revision ?? 0;

        long epoch = ViewEpoch(entries.Version.Peek(), revision, skip, group, state.DrillFolderId);
        if (epoch != _viewEpoch)
        {
            _viewEpoch = epoch;
            session.View.Build(published, skip, input.PlaylistTree, revision, state.DrillFolderId, group);
        }

        _pins.Set(published, 0, skip);
        // V3's header carries the create "+" (§3.2.3) and its config leaves `HeaderCreate` false, so the section header
        // does not add a second one. The empty-library CTA lives in the chrome's empty state.
        input = input with { Pins = _pins, ExpandedFolders = null };
        return group
            ? (input with { PlaylistTree = session.View.Rows })
            : (input with { Library = session.View.Rows });
    }

    static long ViewEpoch(int entriesVersion, int revision, int skip, bool group, string? drill)
    {
        unchecked
        {
            long h = entriesVersion;
            h = h * 1099511628211L + revision;
            h = h * 1099511628211L + skip;
            h = h * 1099511628211L + (group ? 1 : 0);
            h = h * 1099511628211L + (drill is { Length: > 0 } d ? StringComparer.Ordinal.GetHashCode(d) : 0);
            return h;
        }
    }

    // ── reorder (§3.2.9's LOCAL custom order) ────────────────────────────────────────────────────────────────────────

    /// <summary>Which sections reorder in place. The shared PIN BAND always does (that order is the pin store's, which every
    /// design writes); the library reorders ONLY under the exact conditions locked decision 10 allows a local overlay —
    /// which is also why an EntityList (a grid view, a flat lens, a search) never does.</summary>
    bool IsSectionReorderable(SidebarSectionKind kind)
    {
        if (kind == SidebarSectionKind.Pinned) return true;
        if (kind != SidebarSectionKind.PlaylistTree) return false;
        return CanReorderCustom();
    }

    /// <summary>Playlists lens ∧ Custom sort (<c>SidebarPreferences.CanReorderV3</c>) ∧ empty search ∧ a list view ∧ no
    /// drill level. Peeked, never subscribed: the mode epoch already carries every one of these.</summary>
    bool CanReorderCustom()
    {
        if (_prefs is not { } prefs || _session is not { } session) return false;
        if (!prefs.CanReorderV3) return false;
        if (session.DrillActive) return false;
        if (LibraryV3Metrics.HasQuery(prefs.V3Search.Peek())) return false;
        return LibraryV3Metrics.IsList(LibraryV3Metrics.NormalizeView(prefs.V3View.Peek()));
    }

    /// <summary>D10 — is the playlist tree showing a NON-CUSTOM order right now? The exact complement of
    /// <see cref="CanReorderCustom"/>: V3's rows are in the rootlist's own order ONLY under the Playlists lens with the
    /// Custom sort, no search, a list view and no drill level. Under anything else the rows are a SORTED view, a
    /// positional insert cannot be shown where it would land, and the resolver refuses Before/After/EndOfList with the
    /// existing "clear sorting to reorder" sentence — while Into a folder or a playlist stays legal, because a deposit
    /// needs no position at all.
    /// <para>Classic and Curated never supply this probe (null ⇒ false): they always render rootlist order.</para>
    /// <para>Read from inside a live drag's hover, so it stays the same peeked, allocation-free test as its
    /// complement.</para></summary>
    bool TreeSortedNonCustom() => !CanReorderCustom();

    /// <summary>D11 — the sibling-run clamp, applied DURING the gesture. Only the library band clamps: the shared PIN
    /// band is one flat list with no folders in it, and a section that is not reorderable never lifts at all.</summary>
    int ClampReorderSlot(SidebarSectionKind kind, int from, int to)
    {
        if (kind != SidebarSectionKind.PlaylistTree || _session is not { } session) return to;
        return CanReorderCustom() ? session.View.ClampToSiblingRun(from, to) : to;
    }

    /// <summary>Commit a same-band reorder.
    ///
    /// <para>The PIN band goes through the shared commit (the pin store). The library band writes V3's local overlay and
    /// NOTHING else — never <c>LibraryBridge</c>, never a playlist mutation source, never Spotify's rootlist. Explicit
    /// resource edge-drops own durable rootlist organization outside this local sorted-view gesture. The whole visible
    /// order is materialized at that moment (F.7.10), which folds in every stably-appended id
    /// and drops ids the projection no longer has.</para></summary>
    void CommitPaneReorder(SidebarPaneReorder r)
    {
        if (_prefs is not { } prefs) return;
        if (r.Section.Kind == SidebarSectionKind.Pinned)
        {
            SidebarPaneReorderCommit.Default(prefs, in r);
            return;
        }
        if (_session is not { } session || r.FromSlot == r.ToSlot || !CanReorderCustom()) return;

        var view = session.View;
        // The band's slots are the library section's contiguous rows, which ARE this view's rows in order. Verify before
        // writing: a mismatch means the plan and the view disagreed (a projection publish mid-gesture), and a materialized
        // order taken from the wrong list would persist a shuffled library.
        if (r.SlotCount != view.Count) return;
        if (!string.Equals(r.KeyAt(r.FromSlot), view.KeyAt(r.FromSlot), StringComparison.Ordinal)) return;
        // §3.2.9's FOLDER BOUNDARY CLAMP: a local overlay that moved items between folders would misrepresent a tree it
        // cannot write, so a drop aimed across a boundary simply does not commit.
        if (!view.SameParent(r.FromSlot, r.ToSlot)) return;

        view.MaterializeOrder(_orderScratch, r.FromSlot, r.ToSlot);
        prefs.SetV3CustomOrder(_orderScratch);
    }

    // ── the 56-DIP rail's own affordances (§3.2.13) ──────────────────────────────────────────────────────────────────

    /// <summary>The rail's two V3 affordances, appended after the plan's tiles (the pane adds the separating rule): the
    /// "Your Library" tile, which EXPANDS the pane rather than navigating (a 56-DIP strip cannot host a library), and the
    /// create-playlist button. The tiles themselves — pins first, then the current filtered library — come from the DOCUMENT
    /// (every section's <c>ShowInRail</c>), so the rail honours the active filter exactly as the expanded pane does.</summary>
    Element? BuildRailFooter()
    {
        if (_session is not { } session) return null;
        return new BoxEl
        {
            Key = "v3-rail-footer",
            Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children =
            [
                SidebarRailItem.Icon("v3-rail-expand", Icons.List, false, session.Expand,
                                     Loc.Get(Strings.Sidebar.V3.Expand)),
                Embed.Comp(() => new SidebarCreateButton(
                    session.CreatePlaylist, menu: CreateMenu, box: SidebarRailItem.Box, glyph: 16f)),
            ],
        };
    }

    /// <summary>The rail "+"'s flyout — the same two verbs the header's "+" carries, so a collapsed V3 pane can create
    /// a folder too. Built at open time; null falls back to the plain create-playlist click.</summary>
    ContextMenuModel? CreateMenu()
    {
        if (_session is not { Acts: { Library: not null } acts } session) return null;
        return new ContextMenuModel(new List<MenuFlyoutItem>(2)
        {
            new(Loc.Get(Strings.Detail.NewPlaylist), ActionIcons.Resolve(ActionIcons.Add), true, session.CreatePlaylist),
            new(Loc.Get(Strings.Sidebar.CreateFolder), ActionIcons.Resolve(ActionIcons.Folder),
                acts.Overlay is not null, () => FolderActions.NewFolder(acts, null)),
        });
    }

    // ── derived geometry ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The grid column count, DERIVED from the pane width (§3.2.8: the sidebar's cell size is derived, never
    /// chosen). Constant for the list views, so a seam drag cannot churn the document epoch there.</summary>
    int ComputeColumns()
    {
        int view = LibraryV3Metrics.NormalizeView(_prefs?.V3View.Value ?? (int)SidebarV3View.List);
        if (!LibraryV3Metrics.IsGrid(view)) return LibraryV3Document.ClampColumns(0);
        // The pane owns ONE inset (SidebarPaneMetrics.PanePad), and its grid strip derives the cell edge from exactly that,
        // so the column count has to be computed against the same available width.
        float cross = _expandedWidth.Value - SidebarPaneMetrics.PaneInsetH;
        return LibraryV3Document.ClampColumns(LibraryV3Metrics.Columns(view, cross));
    }

    /// <summary>Revision 2's threshold: the overlay drawer and any pane below <see cref="LibraryV3Metrics.DrillInWidth"/>
    /// NAVIGATE into folders; wider panes disclose inline.</summary>
    bool ComputeNarrow() => _inDrawer || _expandedWidth.Value < LibraryV3Metrics.DrillInWidth;
}
