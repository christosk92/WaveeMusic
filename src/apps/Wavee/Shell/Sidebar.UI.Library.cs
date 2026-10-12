// ── Shell/Sidebar.UI.Library.cs ────────────────────────────────────────────────────────────────────────────────────
// Your Library (design P.2a): the mode's PaneConfig and the session (shaping, drill, reorder rules, the head's focus ring)
//
// Role: UI · Spec: sidebar-rework-implementation.md §P5.5 · design P.2, P.2a, V.5, V.9, V.11
// T5b: the fixed head -- Home, the "Your Library ▾" page dropdown, the chips, the toolbar, the breadcrumb, the banners -- is the
// structure's (`Shell.NavLibraryHead`, Classic's is `Themes/Classic/Structure/Views/Nav/ClassicNavLibraryHead.cs`). The session is
// its host (`INavLibraryHeadHost`, Theming/Contracts/NavLibraryHead.cs): the head draws, the session keeps what decides.

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
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>The Library layout's mount: one <see cref="PaneView"/> with the head above the list.</summary>
    internal sealed class LibraryMode : Component
    {
        readonly bool _inDrawer;   // mount configuration: a drawer and a docked pane are separate mounts
        readonly NavRowKit _kit;   // the structure's list (planner, diff, extents, freeze, row slot)

        public LibraryMode(bool inDrawer, NavRowKit kit) { _inDrawer = inDrawer; _kit = kit; }

        public override Element Render()
        {
            bool inDrawer = _inDrawer;
            var kit = _kit;
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
                var pane = new PaneView(config, inDrawer, kit);
                s.PaneRef = pane;
                return pane;
            });
        }
    }

    /// <summary>The Library mount's VIEW session: the pane reference, the head's focus targets and the pill flag, around the
    /// model's <see cref="NavLibrary"/> (the drill stack, the shaping state, the empty-state facts, the verbs). A reference-stable
    /// frozen prop; everything mutable inside is a signal or read at call time. Never persisted. The structure's head reads it
    /// through <see cref="INavLibraryHeadHost"/> (the explicit members at the end).</summary>
    internal sealed class LibrarySession : INavLibraryHeadHost
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

        /// <summary>The head's props: ONE record per mount (equal across the pane's re-renders, so the slot runs its template once).</summary>
        NavLibraryHeadProps? _headProps;

        /// <summary>The head above the list: the structure's <c>Shell.NavLibraryHead</c> over this session.</summary>
        public Element Head() => ViewSlots.View(ViewKeys.ShellNavLibraryHead, _headProps ??= new NavLibraryHeadProps(this)) with { Key = "library-head" };

        public bool FocusHead(SidebarLibraryHeadRules.HeadStop stop)
        {
            var node = stop == SidebarLibraryHeadRules.HeadStop.Home || !HasPages ? HomeNode : DropdownNode;
            if (node.IsNull || Hooks?.FocusNode is not { } focus) return false;
            focus(node, true);
            return true;
        }

        /// <summary>The list's Up from its first row lands on the dropdown (Home when the title is not a menu).</summary>
        public bool OnListEdge(int direction) => direction < 0 && FocusHead(SidebarLibraryHeadRules.HeadStop.Dropdown);

        // ── INavLibraryHeadHost: what the structure's head may ask of this session ──
        // Each line forwards a member the core already had; nothing here decides or draws.

        NavLibrary INavLibraryHeadHost.Lib => Lib;
        NavModel INavLibraryHeadHost.Model => Model;
        bool INavLibraryHeadHost.InDrawer => InDrawer;

        SidebarLayoutDoc INavLibraryHeadHost.Doc => Doc;
        IReadSignal<int> INavLibraryHeadHost.LayoutVersion => LayoutVersion;
        Signal<string> INavLibraryHeadHost.Search => LibrarySearch;
        Signal<bool> INavLibraryHeadHost.SearchOpen => LibrarySearchOpen;
        Signal<SidebarLibraryFilter> INavLibraryHeadHost.Filter => LibraryFilter;
        bool INavLibraryHeadHost.RailPresented => !InDrawer && Mode.Value == SidebarPaneMode.Compact && !DragPeek.Value;
        string INavLibraryHeadHost.Route => Shell.NameOf(Shell.Current.Value);
        string INavLibraryHeadHost.RoutePeek => Shell.NameOf(Shell.Current.Peek());
        (string Title, string Glyph) INavLibraryHeadHost.DestOf(string routeKey) => Shell.Dest(Shell.Parse(routeKey));
        string INavLibraryHeadHost.GlyphOf(string routeKey) => Shell.Dest(Shell.Parse(routeKey)).Glyph;
        void INavLibraryHeadHost.OpenPane() => Sidebar.OpenPane();

        INavRowHost? INavLibraryHeadHost.PaneRef => PaneRef;
        string INavLibraryHeadHost.PaneSelectedRoute => PaneRef?.SelectedRoute ?? "";
        void INavLibraryHeadHost.FocusListItem(int index) => PaneRef?.FocusListItem(index);
        Func<ContextMenuModel?> INavLibraryHeadHost.CreateMenuFn => CreateMenuFn;
        Func<bool> INavLibraryHeadHost.HeaderDropActiveFn => HeaderDropActiveFn;

        IReadOnlyList<MenuFlyoutItem> INavLibraryHeadHost.SortMenu(SidebarLibraryFilter filter)
            => SidebarMenus.Map(NavMenuModel.Menu(NavMenuTarget.Sort(filter)));
        IReadOnlyList<MenuFlyoutItem> INavLibraryHeadHost.OptionsMenu(SidebarLibraryFilter filter)
            => SidebarMenus.Map(NavMenuModel.Menu(NavMenuTarget.LibraryOptions(filter)));
        IReadOnlyList<MenuFlyoutItem> INavLibraryHeadHost.PagesMenu(IReadOnlyList<SidebarLibraryPage> pages, string? page)
            => SidebarMenus.MapPages(SidebarMenuModel.Pages(pages, page), pages, r => Lib.Navigate(r, null));
        void INavLibraryHeadHost.BindMenuOverlay(IOverlayService overlay) => SidebarMenus.Overlay = overlay;

        Signal<bool> INavLibraryHeadHost.RowPill => RowPill;
        InputHooks? INavLibraryHeadHost.Hooks { get => Hooks; set => Hooks = value; }
        NodeHandle INavLibraryHeadHost.HomeNode { get => HomeNode; set => HomeNode = value; }
        NodeHandle INavLibraryHeadHost.DropdownNode { get => DropdownNode; set => DropdownNode = value; }
        NodeHandle INavLibraryHeadHost.SearchButtonNode { get => SearchButtonNode; set => SearchButtonNode = value; }
        bool INavLibraryHeadHost.HasPages { get => HasPages; set => HasPages = value; }
        bool INavLibraryHeadHost.FocusSearchButtonOnMount { get => FocusSearchButtonOnMount; set => FocusSearchButtonOnMount = value; }
    }
}
