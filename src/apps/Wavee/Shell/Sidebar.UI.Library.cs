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
        readonly bool _inDrawer;   // mount configuration: a drawer and a docked pane are separate mounts

        public LibraryMode(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            bool inDrawer = _inDrawer;
            var s = UseMemo(() => new LibrarySession(inDrawer), DepKey.Empty);

            // MEMOS, not raw width reads: a seam drag writes Width every frame; the column count / folder mode flip rarely.
            var vp = UseContextSignal(Viewport.Size);
            s.Lib.Columns = UseComputed(() => SidebarLibraryHeadRules.GridColumns(NavLibrary.PresentedWidth(inDrawer, vp.Value.Width)));
            // The drawer always drills; a docked pane drills below 240 px (design P.2) and shows the inline tree from 240 up.
            bool narrow = UseComputed(() => inDrawer || NavLibrary.PresentedWidth(false, vp.Value.Width) < SidebarLibraryMetrics.DrillInWidth).Value;
            // Inline disclosure and a drill stack answer the same question, so only one may be live. Signal writes ⇒ effect.
            UseLayoutEffect(() => s.Lib.SetNarrow(narrow), DepKey.From(narrow));

            // The binder has no pump of its own: the debounced search text and the folder edits resync here.
            var search = UseDebouncedValue(LibrarySearch, NavLibrary.SearchDebounceMs);
            UseEffect(() =>
            {
                _ = search.Value;
                _ = FolderVersion.Value;
                s.Lib.Resync();
            });

            var config = UseMemo(() => new PaneConfig
            {
                Layout = SidebarLayoutId.Library,
                ScrollKeyPrefix = "sidebar.library",
                Document = static () => Model.ReadLayout(),
                Input = s.Lib.ShapeInput,
                ModeEpoch = s.Lib.ReadModeEpoch,
                Options = s.Lib.ShapeOptions,
                Head = s.Head,
                TreeSortedNonCustom = s.Lib.TreeSortedNonCustom,
                SortedListRefusalAction = s.Lib.ShowCustomOrder,
                ClampReorderSlot = s.Lib.ClampReorderSlot,
                CommitReorder = s.CommitReorder,
                ActivateFolder = s.Lib.ActivateFolder,
                DisclosesFoldersInline = s.Lib.DisclosesFoldersInline,
                OnEdgeNavigate = s.OnListEdge,
                PillOnRowChanged = s.NotePillOnRow,
                OpenSearch = s.Lib.OpenSearch,
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

    /// <summary>The Library mount's VIEW session: the pane reference, the head's focus targets and the pill flag, around the
    /// model's <see cref="NavLibrary"/> (the drill stack, the shaping state, the empty-state facts, the verbs). A reference-stable
    /// frozen prop; everything mutable inside is a signal or read at call time. Never persisted.</summary>
    internal sealed class LibrarySession
    {
        public LibrarySession(bool inDrawer)
        {
            InDrawer = inDrawer;
            Lib = Model.OpenLibrary(inDrawer);
            CreateMenuFn = () => PaneRef?.CreateMenu();
            HeaderDropActiveFn = () => PaneRef is { } p && p.HeaderCreateDropActive.Value;
        }

        /// <summary>The model's session for this mount.</summary>
        public readonly NavLibrary Lib;
        public readonly bool InDrawer;
        public PaneView? PaneRef;                                  // set by the mount factory, before the pane renders
        public readonly Func<ContextMenuModel?> CreateMenuFn;
        public readonly Func<bool> HeaderDropActiveFn;

        /// <summary>A list row carries the pill now (fed by the pane's PillOnRowChanged). Session-scoped; the head reads it.</summary>
        public readonly Signal<bool> RowPill = new(false);

        public void NotePillOnRow(bool on) => RowPill.SetIfChanged(on);

        // ── the head's focus targets (written by the head's OnRealized; the ring and the list's edge read them) ──
        internal NodeHandle HomeNode, DropdownNode, SearchButtonNode;
        internal InputHooks? Hooks;
        internal bool HasPages;
        /// <summary>Set by the search box's Esc-close: the toolbar's search button takes focus when it remounts.</summary>
        internal bool FocusSearchButtonOnMount;

        // ── PaneConfig: reorder. The pin move is the pane's (PaneReorder is a plan-space value); the rules are the model's. ──

        public void CommitReorder(PaneReorder r)
        {
            DefaultReorderCommit(in r);
            Lib.Resync();
        }

        // ── the head's ring and the list's edge (design V.11) ──────────────────────────────────────────────────────

        public Element Head() => Embed.Comp(() => new LibraryHead(this)) with { Key = "library-head" };

        public bool FocusHead(SidebarLibraryHeadRules.HeadStop stop)
        {
            var node = stop == SidebarLibraryHeadRules.HeadStop.Home || !HasPages ? HomeNode : DropdownNode;
            if (node.IsNull || Hooks?.FocusNode is not { } focus) return false;
            focus(node, true);
            return true;
        }

        /// <summary>The list's Up from its first row lands on the dropdown (Home when the title is not a menu).</summary>
        public bool OnListEdge(int direction) => direction < 0 && FocusHead(SidebarLibraryHeadRules.HeadStop.Dropdown);
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
            int drillVersion = s.Lib.DrillVersion.Value;
            bool searching = SidebarLibraryMetrics.HasQuery(LibrarySearch.Value);
            // Self-correcting state 1: a drilled-into folder that vanished POPS rather than pointing at nothing.
            bool missing = s.Lib.View.DrillTargetMissing;
            UseLayoutEffect(() => { if (missing) s.Lib.Back(); }, DepKey.From(missing ? 1 : 0, drillVersion));
            // Self-correcting state 2: a search FLATTENS the tree, so there is no folder to be inside of.
            UseLayoutEffect(() => { if (searching) s.Lib.ResetDrill(); }, DepKey.From(searching));
            // The one-shot failure diagnostic: a failed library with no rows (a list-only count is enough here).
            bool failedEmpty = s.Lib.FailedEmpty;
            UseLayoutEffect(() => { if (failedEmpty) Log.Warn("sidebar", "library.failed rows=0", s.Lib.Cell.Error); },
                DepKey.From(failedEmpty));

            if (!s.InDrawer && Sidebar.Mode.Value == SidebarPaneMode.Compact && !Sidebar.DragPeek.Value) return CompactHead();

            // The facts subscribe the head to the layout, the binder's input edge and the cell's version (the model's).
            var facts = s.Lib.ReadFacts();
            var lib = facts.Options;
            var filter = facts.Filter;
            string route = Shell.NameOf(Shell.Current.Value);
            var counts = Model.Counts;
            SidebarLibraryHeadRules.Pages(lib.HiddenKinds, in counts, static r => Shell.Dest(Shell.Parse(r)).Glyph, _pages);
            s.HasPages = SidebarLibraryHeadRules.DropdownIsMenu(_pages.Count);
            string? page = SidebarLibraryHeadRules.PageOf(route, lib.HiddenKinds);

            // THE ONE OWNER of Your Library's empty state (the planner plans no Empty row in Library, §P3.5) is the model's.
            var load = facts.Load;
            int rows = facts.Rows;
            string? shapedSearch = facts.ShapedSearch;
            var empty = facts.Empty;

            var kids = new List<Element>(9)
            {
                HomeRow(route),
                SectionHeader.Separator(),
                DropdownRow(page),
                Embed.Comp(() => new LibraryChips(s)) with { Key = "library-chips" },
                Embed.Comp(() => new LibraryToolbar(s)) with { Key = "library-toolbar" },
            };
            if (s.Lib.DrillActive) kids.Add(Breadcrumb());
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
                OnClick = () => s.Lib.Navigate("home", null), Focusable = true,
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
            var items = SidebarMenus.MapPages(SidebarMenuModel.Pages(_pages, page), _pages, r => s.Lib.Navigate(r, null));
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
                    Tile("lib-home", home.Title, home.Glyph, string.Equals(route, "home", StringComparison.Ordinal), () => s.Lib.Navigate("home", null)),
                    Tile("lib-search", Loc.Get("sidebar.library.search"), Icons.Search, false, () => { s.Lib.OpenSearch(); Sidebar.OpenPane(); }),
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
                    OnClick = s.Lib.Back,
                    Children = [Icon(Icons.Back, 12f, Tok.TextSecondary)],
                }.Interactive(Interaction.Subtle), s.Lib.ParentName),
                new TextEl(s.Lib.CurrentFolderName)
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
                SidebarLibraryEmpty.Failed => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Page, onAction: s.Lib.Retry),
                SidebarLibraryEmpty.Search => Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact,
                    // a pasted paragraph cannot blow out 240 DIP
                    title: Strings.Sidebar.V3.Empty.Search(query.Length > 24 ? string.Concat(query.AsSpan(0, 24), "…") : query),
                    subtitle: Loc.Get(Strings.Sidebar.V3.Empty.SearchSub),
                    actionLabel: Loc.Get(Strings.Sidebar.V3.ClearSearch),
                    onAction: s.Lib.ClearSearch),
                SidebarLibraryEmpty.Filter => Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact,
                    title: Strings.Sidebar.V3.Empty.Filter(Loc.Get(SidebarLibraryHeadRules.ChipKey(filter))),
                    subtitle: "",
                    actionLabel: Loc.Get(Strings.Sidebar.V3.ClearFilter),
                    onAction: s.Lib.ClearFilter),
                _ => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                    title: Loc.Get(Strings.Sidebar.V3.Empty.Library),
                    subtitle: Loc.Get(Strings.Sidebar.V3.Empty.LibrarySub),
                    actionLabel: Loc.Get(Strings.Sidebar.CreatePlaylistTooltip),
                    onAction: s.Lib.CreatePlaylist),
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
                    OnClick = s.Lib.Retry,
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

        void Commit(SidebarLibraryFilter chip) => s.Lib.ToggleChip(chip);   // a chip change leaves any drilled folder

        /// <summary>The app's one filter chip (<see cref="Controls.Chip"/>, the stock toggle: 32/r4, checked = accent); a click
        /// on the active chip clears it, which the lit chip's tooltip says. The root keeps the rail's roving tab stop, but
        /// <see cref="ToggleButton"/> re-asserts its own role, so the <c>RadioButton</c> set here is overridden and assistive
        /// tech announces a toggle button (with its checked state): the semantics stay the toggle's, on purpose. The tooltip
        /// wrapper is always mounted (text null while unlit), so lighting a chip never remounts it.</summary>
        Element Chip(NodeHandle[] nodes, int index, SidebarLibraryFilter chip, bool on, bool focusable)
        {
            var toggle = Controls.Chip(Loc.Get(SidebarLibraryHeadRules.ChipKey(chip)), on, true, () => Commit(chip), new TemplateParts
            {
                [ToggleButton.PartRoot] = b => b with
                {
                    Shrink = 0f, Role = AutomationRole.RadioButton, Focusable = focusable,
                    OnRealized = h => nodes[index] = h,
                },
            });
            return ToolTip.Wrap(toggle, (Prop<string?>)(on ? Loc.Get(Strings.Sidebar.V3.ClearFilter) : null)) with { Key = "chip:" + (int)chip };
        }

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
                return SidebarLibraryHeadRules.ShapeOf(NavLibrary.PresentedWidth(inDrawer, vp.Value.Width),
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
                        kids.Add(ToolTip.Wrap(IconButton.Create(Icons.Search, s.Lib.OpenSearch, parts: searchParts, size: ControlSize.Small)
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
                        kids.Add(Embed.Comp(() => new CreateButton(s.Lib.CreatePlaylist, menu: s.CreateMenuFn,
                            drop: s.PaneRef?.HeaderCreateDropSpec(), dropActive: s.HeaderDropActiveFn,
                            box: SidebarLibraryHeadRules.ToolbarControl, glyph: 14f)) with { Key = "lib-create" });
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
                Padding = new Edges4(SidebarLibraryHeadRules.BandInsetX - SidebarRowGeometry.PaneEdge, 4f, 0f, 4f),
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
            => Open(anchor, SidebarMenus.Map(NavMenuModel.Menu(NavMenuTarget.Sort(filter))));

        void OpenOptions(NodeHandle anchor, SidebarLibraryFilter filter)
            => Open(anchor, SidebarMenus.Map(NavMenuModel.Menu(NavMenuTarget.LibraryOptions(filter))));

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
                        // The Esc rule is the model's; closing latches the toolbar's ⌕ to take focus when it remounts.
                        s.Lib.Escape(() => s.FocusSearchButtonOnMount = true);
                        return true;
                    },
                    OnFocusChanged = gained =>
                    {
                        if (!gained) s.Lib.SearchBlurred();
                    },
                }) with { Key = "search:field" },
            };
            // The editor hides its own ✕ while empty, so an empty box offers a trailing close.
            if (empty)
                kids.Add(IconButton.Create(Icons.Cancel, s.Lib.CloseSearch, size: ControlSize.Small) with { Key = "search:close" });

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
