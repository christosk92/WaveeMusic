// ── Shell/Sidebar.UI.Library.cs ────────────────────────────────────────────────────────────────────────────────────
// Your Library (design P.2a): the mode's PaneConfig, the session (shaping, drill, reorder rules) and the fixed head —
// Home, the "Your Library ▾" page dropdown, the chips and the toolbar
//
// Role: UI · Spec: sidebar-rework-implementation.md §P5.5 · design P.2, P.2a, V.5, V.9, V.11

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>The Library layout's mount: one <see cref="PaneView"/> with the head above the list.</summary>
    internal sealed class LibraryMode : Component
    {
        /// <summary>0.2.9's binder-pump search debounce (<c>SidebarBinderPump.SearchDebounceMs</c>).</summary>
        const float SearchDebounceMs = 90f;

        readonly bool _inDrawer;   // mount configuration: a drawer and a docked pane are separate mounts

        public LibraryMode(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            bool inDrawer = _inDrawer;
            var s = UseMemo(() => new LibrarySession(inDrawer), DepKey.Empty);

            // MEMOS, not raw width reads: a seam drag writes Width every frame; the column count / folder mode flip rarely.
            var vp = UseContextSignal(Viewport.Size);
            s.Columns = UseComputed(() => SidebarLibraryHeadRules.GridColumns(LibrarySession.PresentedWidth(inDrawer, vp.Value.Width)));
            // The drawer always drills; a docked pane drills below 240 px (design P.2) and shows the inline tree from 240 up.
            bool narrow = UseComputed(() => inDrawer || LibrarySession.PresentedWidth(false, vp.Value.Width) < SidebarLibraryMetrics.DrillInWidth).Value;
            // Inline disclosure and a drill stack answer the same question, so only one may be live. Signal writes ⇒ effect.
            UseLayoutEffect(() =>
            {
                s.NarrowFolders.SetIfChanged(narrow);
                if (!narrow) s.ResetDrill();
            }, DepKey.From(narrow));

            // The binder has no pump of its own: the debounced search text and the folder edits resync here.
            var search = UseDebouncedValue(LibrarySearch, SearchDebounceMs);
            UseEffect(() =>
            {
                _ = search.Value;
                _ = FolderVersion.Value;
                LibrarySession.Resync();
            });

            var config = UseMemo(() => new PaneConfig
            {
                Layout = SidebarLayoutId.Library,
                ScrollKeyPrefix = "sidebar.library",
                Document = static () => { _ = LayoutVersion.Value; return Doc; },
                Input = s.ShapeInput,
                ModeEpoch = s.ReadModeEpoch,
                Options = s.ShapeOptions,
                Head = s.Head,
                TreeSortedNonCustom = s.TreeSortedNonCustom,
                SortedListRefusalAction = s.SwitchToCustomSortForReorder,
                ClampReorderSlot = s.ClampReorderSlot,
                CommitReorder = s.CommitReorder,
                ActivateFolder = s.ActivateFolder,
                DisclosesFoldersInline = s.DisclosesFoldersInline,
                OnEdgeNavigate = s.OnListEdge,
                PillOnRowChanged = s.NotePillOnRow,
                OpenSearch = s.OpenSearch,
                // Null: the toolbar's "+" calls PaneView.CreatePlaylist() — the pane's ONE create flow.
                OnCreatePlaylist = null,
            }, DepKey.Empty);

            // The factory runs once at mount: the session holds the pane so the head's "+" reuses the pane's own
            // HeaderCreateDropSpec / CreateMenu / CreatePlaylist and the ring can focus the list.
            return Embed.Comp(() =>
            {
                var pane = new PaneView(config, inDrawer);
                s.PaneRef = pane;
                return pane;
            });
        }
    }

    /// <summary>The Library session: the shaping state the head and the pane share, the drill stack and the reorder rules.
    /// A reference-stable frozen prop; everything mutable inside is a signal or read at call time. Never persisted.</summary>
    internal sealed class LibrarySession
    {
        /// <summary>The published cell: the binder publishes its OWN <c>Entries</c>; <c>Sidebar.Entries</c> is the headless fallback.</summary>
        public static SidebarEntries Cell => Binder?.Entries ?? Entries;

        /// <summary>Rebuild the projection now if any trigger moved (one struct compare when nothing did).</summary>
        public static void Resync() => Binder?.Sync();

        /// <summary>The width the library is presented at: the drawer's overlay width, or the docked pane's presented width.
        /// The stored preference is not it: a narrow viewport clamps the pane below it.</summary>
        public static float PresentedWidth(bool inDrawer, float vpW) => inDrawer
            ? SidebarPaneModeRules.OverlayWidth(Sidebar.Width.Value, vpW)
            : Sidebar.PresentedWidth.Value;

        public LibrarySession(bool inDrawer)
        {
            InDrawer = inDrawer;
            CreateMenuFn = () => PaneRef?.CreateMenu();
            HeaderDropActiveFn = () => PaneRef is { } p && p.HeaderCreateDropActive.Value;
        }

        public readonly bool InDrawer;
        public PaneView? PaneRef;                                  // set by the mount factory, before the pane renders
        public IReadSignal<int>? Columns;                          // the derived grid column count (a memo)
        public readonly Signal<bool> NarrowFolders = new(false);   // below DrillInWidth (or in the drawer) folders DRILL
        public readonly Signal<int> DrillVersion = new(0);         // bumped by every push/pop/reset: one re-plan, no remount
        public readonly SidebarLibraryShaper View = new();         // the built order, shared by the input shaper and the head
        public readonly Func<ContextMenuModel?> CreateMenuFn;
        public readonly Func<bool> HeaderDropActiveFn;

        /// <summary>A list row carries the pill now (fed by the pane's PillOnRowChanged). Session-scoped; the head reads it.</summary>
        public readonly Signal<bool> RowPill = new(false);

        public void NotePillOnRow(bool on) => RowPill.SetIfChanged(on);

        readonly List<string> _folderIds = new(4), _folderNames = new(4);
        long _viewEpoch = long.MinValue;                        // the pane shapes its input twice per plan (list + rail)

        // ── the head's focus targets (written by the head's OnRealized; the ring and the list's edge read them) ──
        internal NodeHandle HomeNode, DropdownNode, SearchButtonNode;
        internal InputHooks? Hooks;
        internal bool HasPages;
        /// <summary>Set by the search box's Esc-close: the toolbar's search button takes focus when it remounts.</summary>
        internal bool FocusSearchButtonOnMount;

        // ── the view state ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>THE Library view state in one value. Reading it SUBSCRIBES the caller (document, mode epoch, head) to every
        /// signal the document is a function of, so none can render a state the others disagree with. Allocation-free.</summary>
        public SidebarLibraryState ReadState()
        {
            _ = LayoutVersion.Value;
            var lib = Doc.Library;
            var filter = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, lib.HiddenKinds);
            _ = DrillVersion.Value;          // a push/pop re-slices the view
            return new SidebarLibraryState(filter, lib.Sort, lib.Descending, lib.View, Columns?.Value ?? 2,
                SidebarLibraryMetrics.HasQuery(LibrarySearch.Value), DrillActive ? CurrentFolderId : null,
                Pins.Count > 0);
        }

        // ── PaneConfig: epoch, head, input, options ──────────────────────────────────────────────────────────────

        public int ReadModeEpoch() => ReadState().GetHashCode();

        /// <summary>KEYED and type-stable: the pane rebuilds this element every render and the instance behind it must
        /// survive (its hooks own the search focus latch and the self-correcting effects).</summary>
        public Element Head() => Embed.Comp(() => new LibraryHead(this)) with { Key = "library-head" };

        /// <summary>The published projection (filtered, searched, sorted, matching pins FIRST) re-grouped into tree order or
        /// sliced to one folder level. While Pinned is shown the planner draws the pins from <c>input.Pins</c> (§P3.5), so the
        /// list skips the published pin head; with Pinned hidden the pins stay in their natural place.</summary>
        public SidebarProjectionInput ShapeInput(SidebarProjectionInput input)
        {
            var state = ReadState();
            var cell = Cell;
            var published = cell.Current;
            int pinCount = Math.Clamp(cell.PinCount, 0, published.Count);
            int skip = Doc.Find(SidebarSectionKind.Pinned) is { Hidden: false } && !state.Drilled ? pinCount : 0;
            bool group = state.FoldersApply;
            int revision = Binder?.Revision ?? 0;

            // The view's own re-bucket gate: `cell.Version` covers every input `View.Build` reads from the projection, `skip`
            // covers the pin band, `group`/`drill` cover the view state. `revision` still goes INTO `View.Build` below, as
            // `EnsureParentMap`'s own cheap internal memo key for the folder→parent walk over the tree slice.
            long epoch = ViewEpoch(cell.Version.Peek(), skip, group, state.DrillFolderId);
            if (epoch != _viewEpoch)
            {
                _viewEpoch = epoch;
                View.Build(published, skip, input.PlaylistTree, revision, state.DrillFolderId, group);
            }
            // ExpandedFolders is KEPT: the shaped list is already depth-stamped (the planner never re-filters it), and
            // PlanLibrary reads the set for one thing only — an expanded PINNED folder's children (§P3.5).
            return input with { Library = View.Rows, LibraryIsTree = group };
        }

        public SidebarPlanOptions ShapeOptions(SidebarPlanOptions o)
            => o with
            {
                Filter = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, Doc.Library.HiddenKinds),
                GridColumns = Columns?.Value ?? 2,
                FoldersInline = DisclosesFoldersInline() && !DrillActive,   // §P3.5: a pinned folder opens in place
                Drilled = DrillActive,                                       // §P3.5: a folder level has no pins / Liked
            };

        static long ViewEpoch(int entriesVersion, int skip, bool group, string? drill)
        {
            unchecked
            {
                long h = entriesVersion;
                h = h * 1099511628211L + skip;
                h = h * 1099511628211L + (group ? 1 : 0);
                h = h * 1099511628211L + (drill is { Length: > 0 } d ? StringComparer.Ordinal.GetHashCode(d) : 0);
                return h;
            }
        }

        // ── PaneConfig: reorder. Pins reorder through the pin store; the library reorders only as a ROOTLIST move under
        //    Playlists · Custom order (the pane's resource drop → WaveeResourceDrop.MoveRootlist), never a local overlay. ──

        bool CanReorderCustom()
            => LibraryFilter.Peek() == SidebarLibraryFilter.Playlists && Doc.Library.Sort == SidebarLibrarySort.CustomOrder
               && LibrarySearch.Peek().Length == 0 && Doc.Library.View == SidebarLibraryView.List && !DrillActive;

        /// <summary>D10 — the tree shows a SORTED view, so positional drops refuse with "clear sorting to reorder" while Into
        /// stays legal.</summary>
        public bool TreeSortedNonCustom() => !CanReorderCustom();

        /// <summary>No local overlay to clamp: a rootlist move is the server's.</summary>
        public int ClampReorderSlot(SidebarSectionKind kind, int from, int to) => to;

        public void CommitReorder(PaneReorder r)
        {
            DefaultReorderCommit(in r);
            Resync();
        }

        /// <summary>H3 (#85) — the refusal toast's action: Playlists + Custom order, the one state a positional reorder is legal.</summary>
        public void SwitchToCustomSortForReorder()
        {
            SetLibraryFilter(SidebarLibraryFilter.Playlists);
            Dispatch(new SetLibrarySort(SidebarLibrarySort.CustomOrder, false));
            Resync();
        }

        // ── drill-in ─────────────────────────────────────────────────────────────────────────────────────────────
        // A STACK of ids AND names: nesting is genuine, and a folder that vanishes mid-session must still label sanely.

        public bool DrillActive => _folderIds.Count > 0;
        public string CurrentFolderId => _folderIds.Count == 0 ? "" : _folderIds[^1];
        public string CurrentFolderName => _folderNames.Count == 0 ? Loc.Get(Strings.Sidebar.V3.Title) : _folderNames[^1];
        /// <summary>The level BACK returns to — the breadcrumb's accessible name.</summary>
        public string ParentName => _folderNames.Count <= 1 ? Loc.Get(Strings.Sidebar.V3.Title) : _folderNames[^2];

        /// <summary>Enter a folder level. It also EXPANDS the folder: the projection omits a collapsed folder's children,
        /// so the level would be empty — and widening back to inline leaves you standing inside it.</summary>
        public void PushFolder(string? folderId, string? name)
        {
            if (string.IsNullOrEmpty(folderId)) return;
            SetFolderExpanded(folderId, true);
            _folderIds.Add(folderId);
            _folderNames.Add(name is { Length: > 0 } ? name : Loc.Get(Strings.Sidebar.V3.Kind.Folder));
            BumpDrill();
            Resync();
        }

        public void PopFolder()
        {
            if (_folderIds.Count == 0) return;
            _folderIds.RemoveAt(_folderIds.Count - 1);
            _folderNames.RemoveAt(_folderNames.Count - 1);
            BumpDrill();
        }

        /// <summary>Leave drill-in entirely (pane widened, a query landed, the kind filter changed).</summary>
        public void ResetDrill()
        {
            if (_folderIds.Count == 0) return;
            _folderIds.Clear();
            _folderNames.Clear();
            BumpDrill();
        }

        void BumpDrill() => DrillVersion.Value = DrillVersion.Peek() + 1;

        /// <summary>WHAT A FOLDER ROW DOES — the one decision point, so the renderer never learns about drill levels:
        /// a GRID cannot disclose, so switch to the list and open it; a NARROW pane drills; a WIDE pane toggles inline.</summary>
        public void ActivateFolder(string folderId, string name)
        {
            if (folderId.Length == 0) return;
            if (Doc.Library.View == SidebarLibraryView.Grid)
            {
                SetLibraryView(SidebarLibraryView.List);
                SetFolderExpanded(folderId, true);
            }
            else if (NarrowFolders.Peek()) PushFolder(folderId, name);
            else ToggleFolder(folderId);
            Resync();
        }

        /// <summary>Only a wide LIST discloses in place; grid switches view and narrow drills, so neither seeds an
        /// inline expand/collapse animation.</summary>
        public bool DisclosesFoldersInline() => !NarrowFolders.Peek() && Doc.Library.View == SidebarLibraryView.List;

        // ── the head's ring and the list's edge (design V.11) ──────────────────────────────────────────────────────

        public bool FocusHead(SidebarLibraryHeadRules.HeadStop stop)
        {
            var node = stop == SidebarLibraryHeadRules.HeadStop.Home || !HasPages ? HomeNode : DropdownNode;
            if (node.IsNull || Hooks?.FocusNode is not { } focus) return false;
            focus(node, true);
            return true;
        }

        /// <summary>The list's Up from its first row lands on the dropdown (Home when the title is not a menu).</summary>
        public bool OnListEdge(int direction) => direction < 0 && FocusHead(SidebarLibraryHeadRules.HeadStop.Dropdown);

        // ── shared verbs ─────────────────────────────────────────────────────────────────────────────────────────

        public void Navigate(string key, string? arg)
        {
            if (PaneRef is { } pane) pane.Navigate(key, arg);
            else Shell.GoTo(Shell.Parse(key, arg));
        }

        public void CreatePlaylist() => PaneRef?.CreatePlaylist();

        public void OpenSearch() => LibrarySearchOpen.SetIfChanged(true);

        /// <summary>Close AND clear: a closed box showing leftover text next time would read as a bug.</summary>
        public void CloseSearch()
        {
            LibrarySearchOpen.SetIfChanged(false);
            LibrarySearch.SetIfChanged("");
            Resync();
        }

        /// <summary>The retry banner / error state: invalidate + sync re-runs the contributing reads.</summary>
        public void Retry()
        {
            if (Binder is not { } binder) return;
            binder.Invalidate();
            binder.Sync();
        }
    }

    /// <summary>Your Library's fixed head (design P.2a). Reads every changing input from signals in its own render.</summary>
    internal sealed class LibraryHead(LibrarySession s) : Component
    {
        /// <summary>The breadcrumb's fly (the folder navigation is <c>MotionTok.ConnectedFly</c>, spring 0.45 / 1.0).</summary>
        static readonly LayoutTransition BreadcrumbFly = new(
            TransitionChannels.Position | TransitionChannels.Opacity, MotionTok.ConnectedFly.ToDynamics(),
            Enter: new EnterExit(Dx: 12f, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dx: 12f, Opacity: 0f, Active: true));

        readonly List<SidebarLibraryPage> _pages = new(4);
        OverlayHandle? _pagesMenu;

        public override Element Render()
        {
            // Every hook runs BEFORE the compact early return: the rail ⇄ pane switch must not change the hook order.
            var hooks = UseContext(InputHooks.Current);
            s.Hooks = hooks;
            int drillVersion = s.DrillVersion.Value;
            bool searching = SidebarLibraryMetrics.HasQuery(LibrarySearch.Value);
            // Self-correcting state 1: a drilled-into folder that vanished POPS rather than pointing at nothing.
            bool missing = s.View.DrillTargetMissing;
            UseLayoutEffect(() => { if (missing) s.PopFolder(); }, DepKey.From(missing ? 1 : 0, drillVersion));
            // Self-correcting state 2: a search FLATTENS the tree, so there is no folder to be inside of.
            UseLayoutEffect(() => { if (searching) s.ResetDrill(); }, DepKey.From(searching));
            // The one-shot failure diagnostic: a failed library with no rows (a list-only count is enough here).
            var cell = LibrarySession.Cell;
            bool failedEmpty = cell.State == LoadState.Failed && s.View.Count == 0;
            UseLayoutEffect(() => { if (failedEmpty) Log.Warn("sidebar", "library.failed rows=0", LibrarySession.Cell.Error); },
                DepKey.From(failedEmpty));

            if (!s.InDrawer && Sidebar.Mode.Value == SidebarPaneMode.Compact && !Sidebar.DragPeek.Value) return CompactHead();

            _ = LayoutVersion.Value;
            // The kind counts AND the query the list was shaped with publish through the binder's input edge (§P5.2).
            _ = Binder?.InputVersion.Value;
            // The PUBLISHED rows: the list count, the load state and the pending flag below are plain properties of the cell,
            // so the head subscribes to the cell's version here. Without this read a search with no results never shows its
            // band, and a cleared search keeps a stale one.
            _ = cell.Version.Value;
            var lib = Doc.Library;
            var filter = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, lib.HiddenKinds);
            string route = Shell.NameOf(Shell.Current.Value);
            var counts = Binder?.Counts ?? default;
            SidebarLibraryHeadRules.Pages(lib.HiddenKinds, in counts, static r => Shell.Dest(Shell.Parse(r)).Glyph, _pages);
            s.HasPages = SidebarLibraryHeadRules.DropdownIsMenu(_pages.Count);
            string? page = SidebarLibraryHeadRules.PageOf(route, lib.HiddenKinds);

            var load = cell.State;
            bool anyPending = cell.AnyContributingKindPending;
            // THE ONE OWNER of Your Library's empty state (the planner plans no Empty row in Library, §P3.5): the scroller's
            // row count exactly as PlanLibrary builds it — the pins ShowsPinInLibrary keeps, the Liked row, the list — over
            // the query the LIST was shaped with (the binder's, never the live box text, which runs ahead by the debounce).
            var input = Binder?.CurrentInput ?? default;
            string? shapedSearch = SidebarSearch.Normalize(input.Search) is { Length: > 0 } q ? q : null;
            bool pinnedShown = Doc.Find(SidebarSectionKind.Pinned) is { Hidden: false };
            int rows = SidebarLibraryEmptyRules.ScrollerRows(input.Pins, pinnedShown, lib, filter, shapedSearch,
                                                             Loc.Get("nav.likedSongs"), s.View.Count, s.DrillActive);
            var empty = SidebarLibraryEmptyRules.Of(rows, load, anyPending, filter, shapedSearch is not null);

            var kids = new List<Element>(9)
            {
                HomeRow(route),
                SectionHeader.Separator(),
                DropdownRow(page),
                Embed.Comp(() => new LibraryChips(s)) with { Key = "library-chips" },
                Embed.Comp(() => new LibraryToolbar(s)) with { Key = "library-toolbar" },
            };
            if (s.DrillActive) kids.Add(Breadcrumb());
            kids.Add(new BoxEl
            {
                Key = "library-rule", Height = SidebarLibraryHeadRules.RuleHeight, Shrink = 0f,
                Margin = new Edges4(-SidebarRowGeometry.PaneEdge, 0f, -SidebarRowGeometry.PaneEdge, 0f),
                Fill = Tok.StrokeDividerDefault,
            });
            // §3.2.10 (kept): loaded content is NEVER blanked. A failure WITH rows is a one-line banner; only a failure with
            // nothing to show takes the pane. A pending library shows the pane's skeletons, never an empty state.
            if (load == LoadState.Failed && rows > 0) kids.Add(ErrorBanner());
            else if (empty != SidebarLibraryEmpty.None) kids.Add(EmptyBand(empty, filter, shapedSearch ?? ""));

            return new BoxEl
            {
                Key = "library-head", Direction = 1, Shrink = 0f,
                Padding = new Edges4(SidebarRowGeometry.PaneEdge, SidebarRowGeometry.PaneTopInset, SidebarRowGeometry.PaneEdge, 0f),
                Children = [.. kids],
            };
        }

        Element HomeRow(string route)
        {
            var home = Shell.Dest(Shell.Parse("home"));
            var row = EntityRow.Create(new RowSpec
            {
                Key = "lib-home", Label = home.Title, Shape = SidebarRowShape.Glyph, Glyph = home.Glyph,
                Selected = string.Equals(route, "home", StringComparison.Ordinal),
                OnClick = () => s.Navigate("home", null), Focusable = true,
                OnRealized = h => s.HomeNode = h,
                MenuOverlay = s.PaneRef?.MenuOverlay,
                // The Home section exists in the Library catalogue; its row menu is the route's (P3's RouteMenu returns the
                // menu factory itself). SidebarMenuModel.Item(Home, …) adds nothing section-scoped.
                Menu = s.PaneRef is { } p && Doc.Find(SidebarSectionKind.Home) is { } homeSection ? p.RouteMenu(homeSection, "home", -1) : null,
            });
            return new BoxEl
            {
                Key = "lib-home-ring", ZStack = true, Shrink = 0f,
                OnKeyDown = e => OnRingKey(SidebarLibraryHeadRules.HeadStop.Home, e),
                Children = s.PaneRef is { } pane
                    ? [row, Embed.Comp(() => new SelectionPill(pane, () => HomePill(pane), SidebarPillLane.Head)) with { Key = "lib-home-pill" }]
                    : [row],
            };
        }

        static SidebarPillState HomePill(PaneView pane)
        {
            string live = pane.SelectedRoute;                                   // a signal read: the pill re-renders
            return new SidebarPillState("home", SidebarPillState.Lit("home", live), 0f,
                SidebarRowGeometry.PillTop(SidebarRowGeometry.RowHeight));
        }

        /// <summary>The page dropdown's pill: lit while the route is one of the pages AND no list row carries it.</summary>
        SidebarPillState DropdownPill(PaneView pane)
        {
            string live = pane.SelectedRoute;
            string? pg = SidebarLibraryHeadRules.PageOf(live, Doc.Library.HiddenKinds);
            bool lit = pg is not null && SidebarLibraryHeadRules.DropdownCarriesPill(live, Doc.Library.HiddenKinds, s.RowPill.Value);
            return new SidebarPillState(lit ? pg : null, lit, 0f,
                (SidebarLibraryHeadRules.DropdownRowHeight - SidebarRowGeometry.PillH) * 0.5f);
        }

        Element DropdownRow(string? page)
        {
            // The accessible name reads the whole "Your Library · Albums". BoxEl has no AutomationName, so the tooltip is
            // the accessible name, as for the icon-only toolbar cells; the bubble repeats the visible label on hover.
            string title = page is null
                ? Loc.Get("sidebar.library.title")
                : Loc.Format("sidebar.library.titleOnPage", ("page", Loc.Get(PageTitleKey(page))));
            // "Your Library" in 600 TextPrimary; on a page the "· Albums" suffix in 400 TextSecondary (the preview's head),
            // and only the suffix ellipsises in a narrow pane.
            var head = new TextEl(Loc.Get("sidebar.library.title"))
            {
                Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Shrink = 0f,
            };
            Element[] words = page is null
                ? [head]
                : [head, new TextEl(Loc.Format("sidebar.library.pageSuffix", ("page", Loc.Get(PageTitleKey(page)))))
                  {
                      Size = 14f, Weight = 400, Color = Tok.TextSecondary, MaxLines = 1, Shrink = 1f, MinWidth = 0f,
                      Trim = TextTrim.CharacterEllipsis,
                  }];
            // No page to offer (every kind hidden through Filters): plain text — no chevron, no button role, no empty menu.
            Element title0 = !s.HasPages
                ? new BoxEl
                {
                    Key = "library-title", Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
                    Height = SidebarLibraryHeadRules.ToolbarControl,
                    Margin = new Edges4(SidebarLibraryHeadRules.TitleX - SidebarRowGeometry.PaneEdge, 4f, 0f, 4f),
                    Children = [.. words],
                }
                : ToolTip.Wrap(new BoxEl
                {
                    Key = "library-dropdown", Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
                    Height = SidebarLibraryHeadRules.ToolbarControl, Shrink = 1f, MinWidth = 0f,
                    Margin = new Edges4(SidebarLibraryHeadRules.TitleX - SidebarRowGeometry.PaneEdge - SidebarLibraryHeadRules.TitleHitInset, 4f, 0f, 4f),
                    Padding = new Edges4(SidebarLibraryHeadRules.TitleHitInset, 0f, SidebarLibraryHeadRules.TitleHitInset, 0f),
                    Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnRealized = h => s.DropdownNode = h,
                    OnClick = () => OpenPages(page),
                    OnKeyDown = e => OnRingKey(SidebarLibraryHeadRules.HeadStop.Dropdown, e),
                    Children = [.. words, Icon(Icons.ChevronDown, SidebarLibraryHeadRules.TitleChevron, Tok.TextSecondary)],
                }.Interactive(new InteractionRecipe
                {
                    Fill = new StateBrush(Tok.FillSubtleTransparent, Tok.FillSubtleSecondary, Tok.FillSubtleTertiary, Tok.FillSubtleTransparent),
                    BrushMs = 83f,
                }), title);
            return new BoxEl
            {
                Key = "library-dropdown-row", ZStack = true, Height = SidebarLibraryHeadRules.DropdownRowHeight, Shrink = 0f,
                // The pill is ALWAYS mounted (lit or dark, like a row's): the pane's selection transaction needs the outgoing
                // node to animate it out when the route leaves the page (§P5.5 "Cross-container pill motion").
                Children = s.PaneRef is { } pane
                    ? [Embed.Comp(() => new SelectionPill(pane, () => DropdownPill(pane), SidebarPillLane.Head)) with { Key = "lib-dropdown-pill" }, title0]
                    : [title0],
            };
        }

        static string PageTitleKey(string page) => page switch
        {
            "albums" => "nav.albums",
            "artists" => "nav.artists",
            "podcasts" => "nav.podcasts",
            _ => "nav.audiobooks",
        };

        void OpenPages(string? page)
        {
            if (s.PaneRef?.MenuOverlay is not { } svc || _pages.Count == 0) return;
            if (_pagesMenu is { IsOpen: true } open) { open.Close(); return; }
            var items = SidebarMenus.MapPages(SidebarMenuModel.Pages(_pages, page), _pages, r => s.Navigate(r, null));
            _pagesMenu = svc.Open(
                () => s.DropdownNode,
                () => MenuFlyout.Create(items, () => _pagesMenu?.Close(), minWidth: 220f),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            _pagesMenu.ClosedAction = () => _pagesMenu = null;
        }

        /// <summary>The head's ring (design V.11): Up/Down between Home, the dropdown and the list's first row; Enter/Space on
        /// the dropdown opens the page menu.</summary>
        void OnRingKey(SidebarLibraryHeadRules.HeadStop stop, KeyEventArgs e)
        {
            if (e.Handled) return;
            if (stop == SidebarLibraryHeadRules.HeadStop.Dropdown && e.KeyCode is Keys.Enter or Keys.Space)
            {
                OpenPages(SidebarLibraryHeadRules.PageOf(Shell.NameOf(Shell.Current.Peek()), Doc.Library.HiddenKinds));
                e.Handled = true;
                return;
            }
            int dir = e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.Up ? -1 : 0;
            if (dir == 0) return;
            var from = stop == SidebarLibraryHeadRules.HeadStop.Dropdown && !s.HasPages ? SidebarLibraryHeadRules.HeadStop.Dropdown : stop;
            var next = SidebarLibraryHeadRules.Next(from, dir, s.PaneRef?.Plan.Rows.Count ?? 0);
            if (!s.HasPages && next == SidebarLibraryHeadRules.HeadStop.Dropdown && dir > 0)
                next = (s.PaneRef?.Plan.Rows.Count ?? 0) > 0 ? SidebarLibraryHeadRules.HeadStop.List : stop;   // skip the plain title
            if (next == SidebarLibraryHeadRules.HeadStop.List) s.PaneRef?.FocusListItem(0);
            else if (next != stop) s.FocusHead(next);
            e.Handled = true;
        }

        /// <summary>The compact rail's head (P2.7's, moved here): Home, the search tile and the separator. The rail has no
        /// dropdown, chips or toolbar; the search tile opens the overlay (or expands the docked pane) with the box open.</summary>
        Element CompactHead()
        {
            string route = Shell.NameOf(Shell.Current.Value);
            var home = Shell.Dest(Shell.Parse("home"));
            Element Tile(string key, string label, string glyph, bool selected, Action click)
                => ToolTip.Wrap(EntityRow.Create(new RowSpec
                {
                    Key = key, Label = label, Shape = SidebarRowShape.Glyph, Tile = true, Glyph = glyph,
                    Focusable = true, Selected = selected, OnClick = click,
                }), label);
            return new BoxEl
            {
                Key = "lib-compact-head", Direction = 1, Shrink = 0f,
                Padding = new Edges4(SidebarRowGeometry.PaneEdge, SidebarRowGeometry.PaneTopInset, SidebarRowGeometry.PaneEdge, 0f),
                Children =
                [
                    Tile("lib-home", home.Title, home.Glyph, string.Equals(route, "home", StringComparison.Ordinal), () => s.Navigate("home", null)),
                    Tile("lib-search", Loc.Get("sidebar.library.search"), Icons.Search, false, () => { s.OpenSearch(); Sidebar.OpenPane(); }),
                    SectionHeader.Separator(),
                ],
            };
        }

        /// <summary>Back + the current level's name; the BACK target's label is the button's accessible name.</summary>
        Element Breadcrumb() => new BoxEl
        {
            Key = "library-breadcrumb",
            Direction = 0, Height = SidebarLibraryMetrics.BreadcrumbHeight, AlignItems = FlexAlign.Center, Gap = 4f,
            // Optical lane: the 24-DIP box starts 6 before the lane so its 12-DIP GLYPH sits on it.
            Padding = new Edges4(SidebarLibraryHeadRules.TitleX - SidebarRowGeometry.PaneEdge - 6f, 0f, SidebarRowGeometry.TrailingPad, 0f),
            Animate = BreadcrumbFly,
            Children =
            [
                ToolTip.Wrap(new BoxEl
                {
                    Width = 24f, Height = 24f, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = s.PopFolder,
                    Children = [Icon(Icons.Back, 12f, Tok.TextSecondary)],
                }.Interactive(Interaction.Subtle), s.ParentName),
                new TextEl(s.CurrentFolderName)
                {
                    Size = 12f, Weight = 600, Color = Tok.TextSecondary,
                    Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };

        /// <summary>The scroller has nothing to show (decided by <see cref="SidebarLibraryEmptyRules.Of"/> over the planned
        /// rows). The ONLY empty state Your Library draws: the planner plans no Empty row in this layout.</summary>
        Element EmptyBand(SidebarLibraryEmpty empty, SidebarLibraryFilter filter, string query)
        {
            Element body = empty switch
            {
                // The FULL error state (page scale), never the compact arm and never the banner.
                SidebarLibraryEmpty.Failed => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Page, onAction: s.Retry),
                SidebarLibraryEmpty.Search => Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact,
                    // a pasted paragraph cannot blow out 240 DIP
                    title: Strings.Sidebar.V3.Empty.Search(query.Length > 24 ? string.Concat(query.AsSpan(0, 24), "…") : query),
                    subtitle: Loc.Get(Strings.Sidebar.V3.Empty.SearchSub),
                    actionLabel: Loc.Get(Strings.Sidebar.V3.ClearSearch),
                    onAction: static () => { LibrarySearch.SetIfChanged(""); LibrarySession.Resync(); }),
                SidebarLibraryEmpty.Filter => Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact,
                    title: Strings.Sidebar.V3.Empty.Filter(Loc.Get(SidebarLibraryHeadRules.ChipKey(filter))),
                    subtitle: "",
                    actionLabel: Loc.Get(Strings.Sidebar.V3.ClearFilter),
                    onAction: static () => { SetLibraryFilter(SidebarLibraryFilter.None); LibrarySession.Resync(); }),
                _ => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                    title: Loc.Get(Strings.Sidebar.V3.Empty.Library),
                    subtitle: Loc.Get(Strings.Sidebar.V3.Empty.LibrarySub),
                    actionLabel: Loc.Get(Strings.Sidebar.CreatePlaylistTooltip),
                    onAction: s.CreatePlaylist),
            };
            // Shrink 0, no Grow: the state reads as the content it replaces, above the (now empty) scroll surface.
            return new BoxEl { Key = "library-empty", Direction = 1, Shrink = 0f, Children = [body] };
        }

        /// <summary>The one-line retry banner (rows present). CARD family: the plate's edge takes the pane edge.</summary>
        Element ErrorBanner() => new BoxEl
        {
            Key = "library-error-banner",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Shrink = 0f,
            Padding = new Edges4(8f, 6f, 8f, 6f),
            Margin = new Edges4(0f, 4f, 0f, 4f),
            Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
            Children =
            [
                Icon(Icons.StatusWarning, 12f, Tok.TextSecondary),
                new TextEl(Loc.Get(Strings.Common.ErrorTitle))
                {
                    Size = 12f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
                new BoxEl
                {
                    Padding = new Edges4(6f, 2f, 6f, 2f), Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                    HoverFill = Tok.FillSubtleTertiary,
                    OnClick = s.Retry,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Common.Retry)) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary },
                    ],
                },
            ],
        };
    }

    /// <summary>Your Library's chip rail (design P.2a): one tab stop that roves by index; the active chip clears on a second
    /// click. Only the session comes in; every changing input is read here.</summary>
    internal sealed class LibraryChips(LibrarySession s) : Component
    {
        readonly List<SidebarLibraryFilter> _chips = new(5);

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var focused = UseSignal(-1);                                       // roved chip index (one tab stop)
            var nodes = UseMemo(static () => new NodeHandle[5], DepKey.Empty);
            var scroll = UseMemo(static () => new ScrollHandle(), DepKey.Empty);

            _ = LayoutVersion.Value;
            var hidden = Doc.Library.HiddenKinds;
            var active = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, hidden);
            SidebarLibraryHeadRules.Chips(hidden, _chips);                     // rebuilt here: a hidden kind loses its chip
            int roved = focused.Value is var f && f >= 0 && f < _chips.Count ? f : Math.Max(0, _chips.IndexOf(active));

            // HOME the rail on a chip change: the active chip leads it.
            UseLayoutEffect(() => scroll.ScrollTo(0.0), DepKey.From((int)active));

            var chips = new Element[_chips.Count];
            for (int i = 0; i < _chips.Count; i++) chips[i] = Chip(nodes, i, _chips[i], _chips[i] == active, i == roved);

            return ScrollView(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = SidebarLibraryHeadRules.ChipGap,
                Padding = new Edges4(SidebarLibraryHeadRules.BandInsetX - SidebarRowGeometry.PaneEdge, 4f,
                                     SidebarLibraryHeadRules.BandInsetX - SidebarRowGeometry.PaneEdge, 4f),
                OnKeyDown = e => Rove(e, hooks, nodes, focused, roved),
                Children = chips,
            }, horizontal: true) with
            {
                Grow = 0f, Height = SidebarLibraryHeadRules.ChipRowHeight, AutoEdgeFade = true, SuppressScrollBar = true,
                ScrollKey = "sidebar.library.chips", Handle = scroll,
            };
        }

        void Commit(SidebarLibraryFilter chip)
        {
            SetLibraryFilter(SidebarLibraryHeadRules.Toggle(LibraryFilter.Peek(), chip));
            s.ResetDrill();      // a chip change leaves any drilled folder
            LibrarySession.Resync();
        }

        /// <summary>The app's one filter chip (<see cref="Controls.Chip"/>, the stock toggle: 32/r4, checked = accent); a click
        /// on the active chip clears it. The root keeps the rail's roving tab stop and radio role.</summary>
        Element Chip(NodeHandle[] nodes, int index, SidebarLibraryFilter chip, bool on, bool focusable)
            => Controls.Chip(Loc.Get(SidebarLibraryHeadRules.ChipKey(chip)), on, true, () => Commit(chip), new TemplateParts
            {
                [ToggleButton.PartRoot] = b => b with
                {
                    Shrink = 0f, Role = AutomationRole.RadioButton, Focusable = focusable,
                    OnRealized = h => nodes[index] = h,
                },
            }) with { Key = "chip:" + (int)chip };

        /// <summary>One tab stop, roving by index (←/→/Home/End); Space/Enter commits; selection does not follow focus.</summary>
        void Rove(KeyEventArgs e, InputHooks hooks, NodeHandle[] nodes, Signal<int> focused, int cur)
        {
            if (e.Handled || _chips.Count == 0) return;
            int n = _chips.Count, next;
            switch (e.KeyCode)
            {
                case Keys.Left: next = cur == 0 ? n - 1 : cur - 1; break;
                case Keys.Right: next = cur == n - 1 ? 0 : cur + 1; break;
                case Keys.Home: next = 0; break;
                case Keys.End: next = n - 1; break;
                case Keys.Space:
                case Keys.Enter:
                    Commit(_chips[cur]);
                    e.Handled = true;
                    return;
                default:
                    return;
            }
            focused.Value = next;
            if (!nodes[next].IsNull) (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(nodes[next]);
            e.Handled = true;
        }
    }

    /// <summary>Your Library's toolbar: the search tile (or the open search box), the sort, the view toggles, "+" and "⋯".
    /// Its controls are built by walking the head's Tab order, so the order on screen is the order the rules pin.</summary>
    internal sealed class LibraryToolbar(LibrarySession s) : Component
    {
        OverlayHandle? _menu;
        readonly List<SidebarLibraryHeadRules.HeadTabStop> _stops = new(11);   // reused every render (no per-render list)

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            bool searching = LibrarySearchOpen.Value;
            _ = LayoutVersion.Value;
            var lib = Doc.Library;
            var filter = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, lib.HiddenKinds);
            var sort = SidebarLibraryHeadRules.Effective(lib.Sort, filter);
            var sortAnchor = UseRef<NodeHandle>(default);
            var moreAnchor = UseRef<NodeHandle>(default);
            // Anchors survive the controls' re-assertion of their roots only through a Parts modifier (memoised once).
            var sortParts = UseMemo(() => AnchorParts(Button.PartRoot, sortAnchor), DepKey.Empty);
            var sortIconParts = UseMemo(() => AnchorParts(IconButton.PartRoot, sortAnchor), DepKey.Empty);
            var moreParts = UseMemo(() => AnchorParts(IconButton.PartRoot, moreAnchor), DepKey.Empty);
            var searchParts = UseMemo(() =>
            {
                var m = new TemplateParts();
                m[IconButton.PartRoot] = b => b with
                {
                    OnRealized = h =>
                    {
                        s.SearchButtonNode = h;
                        if (!s.FocusSearchButtonOnMount) return;
                        s.FocusSearchButtonOnMount = false;
                        post(() => hooks.FocusNode?.Invoke(h, true));   // back to the magnifier after Esc closed the box
                    },
                };
                return m;
            }, DepKey.Empty);

            string sortLabel = Loc.Get(SidebarLibraryHeadRules.ToolbarSortKey(sort));
            // The shape flips rarely; a seam drag writes Width every frame, so it is a MEMO (static: it reads only signals).
            var vp = UseContextSignal(Viewport.Size);
            bool inDrawer = s.InDrawer;
            var shape = UseComputed(() =>
            {
                _ = LayoutVersion.Value;
                var l = Doc.Library;
                var f = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, l.HiddenKinds);
                return SidebarLibraryHeadRules.ShapeOf(LibrarySession.PresentedWidth(inDrawer, vp.Value.Width),
                    Loc.Get(SidebarLibraryHeadRules.ToolbarSortKey(SidebarLibraryHeadRules.Effective(l.Sort, f))));
            }).Value;
            // Icon-only while searching (the box takes the row) and whenever the labelled row would not fit (§P5.1 ShapeOf).
            Element sortButton = searching || shape != SidebarLibraryHeadRules.ToolbarShape.Full
                ? ToolTip.Wrap(IconButton.Create(Icons.Sort, () => OpenSort(sortAnchor.Value, filter), parts: sortIconParts,
                                                 size: ControlSize.Small) with { Key = "lib-sort-compact" }, sortLabel)
                : Button.Create(sortLabel, () => OpenSort(sortAnchor.Value, filter), ButtonAppearance.Subtle, ControlSize.Small,
                                glyph: Icons.Sort, parts: sortParts) with { Key = "lib-sort" };

            // The controls are built by walking the head's Tab order (§P5.1 TabOrder, Q11), so the Tab order IS the screen
            // order. Below 240 the view toggles leave the order (⋯ › View keeps the choice): ⋯ is never the control that gets
            // clipped.
            SidebarLibraryHeadRules.TabOrder(shape, searching, _stops);
            var kids = new List<Element>(8);
            for (int i = 0; i < _stops.Count; i++)
                switch (_stops[i])
                {
                    case SidebarLibraryHeadRules.HeadTabStop.SearchBox:
                        kids.Add(Embed.Comp(() => new LibrarySearchBox(s)) with { Key = "lib-search-box" });
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.Search:
                        kids.Add(ToolTip.Wrap(IconButton.Create(Icons.Search, s.OpenSearch, parts: searchParts, size: ControlSize.Small)
                                                  with { Key = "lib-search" }, Loc.Get("sidebar.library.search")));
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.Sort:
                        kids.Add(sortButton);
                        if (!searching) kids.Add(new BoxEl { Key = "lib-spacer", Grow = 1f });   // not a stop
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.ListView:
                        kids.Add(ViewToggle(SidebarLibraryView.List, lib.View, Icons.ViewList, "sidebar.view.list"));
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.GridView:
                        kids.Add(ViewToggle(SidebarLibraryView.Grid, lib.View, Icons.ViewGrid, "sidebar.view.grid"));
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.Create:
                        kids.Add(Embed.Comp(() => new CreateButton(s.CreatePlaylist, menu: s.CreateMenuFn,
                            drop: s.PaneRef?.HeaderCreateDropSpec(), dropActive: s.HeaderDropActiveFn,
                            box: SidebarLibraryHeadRules.ToolbarControl, glyph: SidebarRowGeometry.PlusGlyph)) with { Key = "lib-create" });
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.More:
                        kids.Add(ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenOptions(moreAnchor.Value, filter), parts: moreParts,
                                                                size: ControlSize.Small) with { Key = "lib-more" },
                                              Loc.Get("sidebar.library.options")));
                        break;
                    // Home, Dropdown and Chips are LibraryHead's rows above, List is the pane's list below: not toolbar controls.
                }
            return new BoxEl
            {
                Key = "library-toolbar", Direction = 0, Height = SidebarLibraryHeadRules.ToolbarHeight, Shrink = 0f,
                AlignItems = FlexAlign.Center, Gap = SidebarLibraryHeadRules.ToolbarGap,
                Padding = new Edges4(SidebarLibraryHeadRules.ToolbarInsetLeft - SidebarRowGeometry.PaneEdge, 4f,
                                     SidebarLibraryHeadRules.ToolbarInsetRight - SidebarRowGeometry.PaneEdge, 4f),
                Children = [.. kids],
            };
        }

        static TemplateParts AnchorParts(string part, Ref<NodeHandle> anchor)
        {
            var m = new TemplateParts();
            m[part] = b => b with { OnRealized = h => anchor.Value = h };
            return m;
        }

        /// <summary>The view toggle's checked-state cell, at toolbar size: 32×32 (IconButton has no checked state). NEUTRAL,
        /// never accent (design V.0/V.4 keep the accent for the pill, the InfoBadge, the insertion line and the drop ring):
        /// on = FillSubtleSecondary + TextPrimary, hover FillSubtleTertiary; off = transparent + TextSecondary, hover
        /// FillSubtleSecondary. An icon-only cell's accessible name is its tooltip.</summary>
        static Element ViewToggle(SidebarLibraryView v, SidebarLibraryView current, string glyph, string key)
        {
            bool on = current == v;
            var cell = new BoxEl
            {
                Key = "lib-view-" + (v == SidebarLibraryView.Grid ? "grid" : "list"),
                Width = SidebarLibraryHeadRules.ToolbarControl, Height = SidebarLibraryHeadRules.ToolbarControl, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                Fill = on ? Tok.FillSubtleSecondary : ColorF.Transparent,
                HoverFill = on ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
                BrushTransitionMs = global::Wavee.Design.Motion.Fast,
                Role = AutomationRole.RadioButton, Cursor = CursorId.Hand, Focusable = true,
                OnClick = () => SetLibraryView(v),
                Children = [Icon(glyph, 16f, on ? Tok.TextPrimary : Tok.TextSecondary)],
            };
            return ToolTip.Wrap(cell, Loc.Get(key));
        }

        void OpenSort(NodeHandle anchor, SidebarLibraryFilter filter)
            => Open(anchor, SidebarMenus.Map(SidebarMenuModel.Sort(State, filter)));

        void OpenOptions(NodeHandle anchor, SidebarLibraryFilter filter)
            => Open(anchor, SidebarMenus.Map(SidebarMenuModel.LibraryOptions(State, filter)));

        /// <summary>The open/close-handle pattern of the header overflow, moved here.</summary>
        void Open(NodeHandle anchor, IReadOnlyList<MenuFlyoutItem> items)
        {
            if (s.PaneRef?.MenuOverlay is not { } svc || items.Count == 0) return;
            if (_menu is { IsOpen: true } open) { open.Close(); return; }
            SidebarMenus.Overlay = svc;
            _menu = svc.Open(() => anchor, () => MenuFlyout.Create(items, () => _menu?.Close(), minWidth: 220f),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            _menu.ClosedAction = () => _menu = null;
        }
    }

    /// <summary>The search box that replaces the toolbar's search tile while <c>LibrarySearchOpen</c>: a glyph, the field and
    /// a close. It takes focus on mount (the user opened it: the tile, the rail's tile, Ctrl+F).</summary>
    internal sealed class LibrarySearchBox(LibrarySession s) : Component
    {
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            bool empty = LibrarySearch.Value.Length == 0;
            var parts = UseMemo(() =>
            {
                var pr = new TemplateParts();
                pr[EditableText.PartRoot] = b => b with
                {
                    Fill = ColorF.Transparent, HoverFill = ColorF.Transparent,
                    // The box mounts only because the user opened it: it always takes focus — after commit, through
                    // FirstFocusableIn (the node is not laid out in OnRealized).
                    OnRealized = h => post(() => hooks.FocusNode?.Invoke(hooks.FirstFocusableIn?.Invoke(h) ?? h, true)),
                };
                pr[EditableText.PartLane] = b => b with { Padding = new Edges4(SidebarLibraryHeadRules.ToolbarControl, 0f, 4f, 0f) };
                return pr;
            }, DepKey.Empty);

            var kids = new List<Element>(3)
            {
                new BoxEl
                {
                    Key = "search:glyph", Width = SidebarLibraryHeadRules.ToolbarControl, Height = SidebarLibraryHeadRules.ToolbarControl,
                    Shrink = 0f, HitTestVisible = false, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start,
                    Children = [Icon(Icons.Search, 16f, Tok.TextSecondary)],
                },
                Embed.Comp(() => new EditableText
                {
                    Text = LibrarySearch, Placeholder = Loc.Get("sidebar.library.searchPlaceholder"),
                    Width = float.NaN, Height = SidebarLibraryHeadRules.ToolbarControl, FontSize = 14f, Chromeless = true,
                    Parts = parts, ShowDeleteButton = true,   // the WinUI inline ✕ — clears, keeps focus
                    // BEFORE the editor's own revert-then-blur; true pre-empts it.
                    PreviewKeyDown = e =>
                    {
                        if (e.KeyCode != Keys.Escape) return false;
                        if (SidebarLibraryHeadRules.OnEscape(LibrarySearch.Peek()) == SidebarLibraryHeadRules.SearchEscape.Clear)
                        {
                            LibrarySearch.SetIfChanged("");
                            LibrarySession.Resync();
                            return true;
                        }
                        s.FocusSearchButtonOnMount = true;   // the toolbar's ⌕ takes focus when it remounts
                        s.CloseSearch();
                        return true;
                    },
                    OnFocusChanged = gained =>
                    {
                        if (!gained && SidebarLibraryHeadRules.ClosesOnBlur(LibrarySearch.Peek())) s.CloseSearch();
                    },
                }) with { Key = "search:field" },
            };
            // The editor hides its own ✕ while empty, so an empty box offers a trailing close.
            if (empty)
                kids.Add(IconButton.Create(Icons.Cancel, s.CloseSearch, size: ControlSize.Small) with { Key = "search:close" });

            return new BoxEl
            {
                Key = "lib-search", ZStack = false, Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Height = SidebarLibraryHeadRules.ToolbarControl, AlignItems = FlexAlign.Center, Corners = Radii.ControlAll,
                Fill = Tok.FillSubtleSecondary,
                Children = [.. kids],
            };
        }
    }
}
