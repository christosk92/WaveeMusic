// ── Shell/Sidebar.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the pane and its two layouts (Classic / Library), the collapsed rail, folder flyout, tree drag cues, multi-select,
// the "Move to folder…" picker
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 7500 lines (this file + its named partials — see below)
// Spec: ch 26 §9.4 · ch 25 (the pane) · ch 26 W21/W22 (what the pipeline and a bound action draw)
//
// THE ONE RENDERER. Classic and Library are a document + a `PaneConfig` over ONE `PaneView` (ch 25 §0.1); the renderer
// never branches on the layout. This file holds the mount points, the layout host (a layout switch is a Key remount),
// the Classic mode shell and the config/metrics vocabulary, and the pane's core: plan → publish → per-row epochs, the
// route and now-playing sweeps, the NavigationView pill transaction, disclosure choreography, reorder bands, the tree
// multi-selection and drag peek.
//
// NAMED PARTIALS (the J1 split — each carries its own header): `Sidebar.UI.Drop.cs` (every drop spec + the rootlist
// slot commit), `Sidebar.UI.Menus.cs` (row menus, the quick layout menu, "Move to folder…"), `Sidebar.UI.Slot.cs` (the
// bound row slot), `Sidebar.UI.Rows.cs` (row primitives), `Sidebar.UI.Flyout.cs` (the compact section and folder
// flyouts), `Sidebar.UI.Footer.cs` (the pane footer), `Sidebar.UI.Library.cs` (Library's mode, session and head).

using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    // ══ 1. MOUNT POINTS ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The docked sidebar: the active layout's pane (one layer: the rail is the same list planned compact). Reads
    /// its own state — <see cref="Layout"/>, <see cref="Width"/>, <see cref="Mode"/>, <see cref="DragPeek"/>,
    /// <c>Shell.Current</c> — so the shell mounts it with no arguments and binds the COLUMN width itself
    /// (<c>Compact ∧ ¬DragPeek ? 48 : Width</c>, the 200/100 ms FluentPane Reveal, the IsolateLayout firewall).</summary>
    // MOUNT POINT (stage B contract)
    public static Element Pane() => Embed.Comp(static () => new PaneHost(inDrawer: false));

    /// <summary>The narrow-shell drawer's mount (viewport ≤ 720): the SAME host, always expanded, no rail layer, its
    /// own scroll key — a second independent mount, never a shared instance (ch 25 W20).</summary>
    // MOUNT POINT (stage B contract)
    public static Element DrawerPane() => Embed.Comp(static () => new PaneHost(inDrawer: true));

    // ══ 2. THE LAYOUT HOST + THE BINDER PUMP ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Bumped once, when the projection binder first builds. The pane folds it into its plan key so a pane
    /// that rendered before the first projection re-plans the moment one exists (the binder is a plain service, so its
    /// first publish is otherwise invisible to a render that saw <c>Revision == 0</c>).</summary>
    static readonly Signal<int> s_binderEpoch = new(0);

    /// <summary>The ONE mount seam: re-renders only on a layout switch and mounts that layout under its own Key, so a
    /// switch REMOUNTS (fresh hooks, section and scroll state) with a 150 ms fade.</summary>
    internal sealed class PaneHost : Component
    {
        readonly bool _inDrawer;

        public PaneHost(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            // Constructed here (no signal write) so the pane's first render already has a driver; STARTED in the pump,
            // which is an effect, because a rebuild publishes the entries cell.
            EnsureBinder();
            UseSignalEffect(PumpBinder);
            // The account edge (§P3.8): idempotent — a market, locale or tier switch keeps the account and returns at once.
            UseSignalEffect(static () =>
            {
                _ = Entities.ScopeEpoch.Value;
                if (Entities.Current is { } scope) EnsureAccount(scope.Key);
            });
            // The one-shot migration toast (§P3.10): after the first pane mounts, never before.
            UseEffect(static () => { ShowPendingMigrationToast(); return null; }, DepKey.Empty);

            // The ONLY signal this body reads: a width, filter or collapse change never re-renders the host.
            var layout = Layout.Value;
            Element mode = layout == SidebarLayoutId.Library
                ? Embed.Comp(() => new LibraryMode(_inDrawer))
                : Embed.Comp(() => new ClassicMode(_inDrawer));
            return new BoxEl
            {
                Grow = 1f, Direction = 1,
                // Key is mandatory: without it two mode components that share a type would reuse hooks across a
                // switch. A quick fade only — the shell's width transition owns spatial motion.
                Children = [mode with
                {
                    Key = layout == SidebarLayoutId.Library ? "sidebar.library" : "sidebar.classic",
                    Enter = new EnterExit(Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlFast,
                }],
            };
        }
    }

    /// <summary>Set by the migration when a customized section or item was carried over or dropped; shown once, after the
    /// first pane mounts.</summary>
    internal static SidebarMigrationResult? PendingMigrationToast;

    static void ShowPendingMigrationToast()
    {
        if (PendingMigrationToast is not { } r) return;
        PendingMigrationToast = null;
        string layout = Loc.Get(r.Layout == SidebarLayoutId.Library ? "sidebar.layoutName.library" : "sidebar.layoutName.classic");
        string text = r.Dropped.Count == 0
            ? Loc.Format("sidebar.migration.toast", ("layout", layout))
            : Loc.Format("sidebar.migration.toastDropped", ("layout", layout), ("names", DroppedNames(r.Dropped)));
        Notify.Say(text, InfoBarSeverity.Informational, actionLabel: Loc.Get("sidebar.menu.edit"), onAction: EnterEdit,
                   dedupeKey: "sidebar.migration", durationMs: 8000f);
    }

    internal static string DroppedNames(IReadOnlyList<string> keys)
    {
        var parts = new string[keys.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = Loc.Get(keys[i]);
        return string.Join(", ", parts);
    }

    /// <summary>The projection binder, built once per process. The binder is a plain service whose <c>Sync()</c> is
    /// idempotent (one trigger fold), so both pane mounts may pump it.</summary>
    static SidebarProjectionBinder EnsureBinder() => Binder ??= new SidebarProjectionBinder();

    /// <summary>The binder's subscription, as ONE signal effect: every table/edge the projection reads, the two shell logs,
    /// the layout and library signals, and every preference that reshapes the pass. The binder itself only Peeks (it is not
    /// a computation), so this is what makes a hydrated playlist title, a rootlist push, a navigation or a filter change
    /// reach the pane. It SYNCS, never invalidates: the binder's gate folds the row versions of exactly the rows the
    /// sidebar shows, so a wake from a table change elsewhere in the app costs one fold, not a three-pass rebuild
    /// (G-180, sidebar decision D8). It reads <see cref="Entities.ScopeEpoch"/> first, because the welcome effect
    /// switches scope on every login and this effect must re-point at the new scope's signals, not keep holding the
    /// retired set's (G-179).</summary>
    static void PumpBinder()
    {
        var binder = EnsureBinder();
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a Switch re-points every table signal read below
        if (Entities.Current is { } scope)
        {
            var edges = scope.Edges;
            _ = edges.Rootlist.Changed.Value;
            _ = edges.SavedAlbums.Changed.Value;
            _ = edges.FollowedArtists.Changed.Value;
            _ = edges.SavedShows.Changed.Value;
            _ = edges.AlbumArtists.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Users.Changed.Value;
            _ = scope.Albums.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Shows.Changed.Value;
        }
        // The recency feeds and the Recents sort (G-172), the new-releases feed.
        _ = Shell.History.Store.Version.Value;
        _ = Shell.PlayLog.Version.Value;
        _ = Notify.Items.Value;
        _ = Notify.ReleasesState.Value;
        _ = PinsVersion.Value;
        _ = LayoutVersion.Value;
        _ = Layout.Value;
        _ = FolderVersion.Value;
        _ = LibraryFilter.Value;
        _ = LibrarySearch.Value;

        bool first = binder.Revision == 0;
        if (first) binder.Start();
        else binder.Sync();
        if (first) s_binderEpoch.Value = s_binderEpoch.Peek() + 1;
    }

    // ══ 3. THE CLASSIC MODE SHELL ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Classic: the catalogue's sectioned list, one scroller (design P.1). Its document is the live
    /// <see cref="Doc"/> — the user's layout overlay resolved by <see cref="SidebarLayoutRules.Resolve"/>.</summary>
    internal sealed class ClassicMode : Component
    {
        readonly bool _inDrawer;

        public ClassicMode(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            var config = UseMemo(() => new PaneConfig
            {
                Layout = SidebarLayoutId.Classic,
                ScrollKeyPrefix = "sidebar.classic",
                Document = static () => { _ = LayoutVersion.Value; return Doc; },
                OnCreatePlaylist = PaneView.CreatePlaylistFlow,
            }, DepKey.Empty);
            return Embed.Comp(() => new PaneView(config, _inDrawer));
        }
    }

    // ══ 4. THE MODE SEAM, THE REORDER COMMIT, THE METRICS ══════════════════════════════════════════════════════════════

    /// <summary>THE ONLY MODE SEAM. Built ONCE per mode mount and frozen into the pane, so EVERY member is a delegate or a
    /// flag — a value member would pin frame 1 forever. <c>Document</c>/<c>Input</c>/<c>ModeEpoch</c>/<c>Options</c> are
    /// invoked inside the pane's render, which is what subscribes the pane to the signals they read. The renderer never
    /// branches on <see cref="Layout"/>.</summary>
    internal sealed record PaneConfig
    {
        public required SidebarLayoutId Layout { get; init; }
        public required string ScrollKeyPrefix { get; init; }
        /// <summary>The live document — invoked in the pane's render (its read of LayoutVersion subscribes the pane).</summary>
        public required Func<SidebarLayoutDoc> Document { get; init; }
        /// <summary>The mode's transform of the binder's planner input (Library: the shaped list + the tree regroup).</summary>
        public Func<SidebarProjectionInput, SidebarProjectionInput>? Input { get; init; }
        /// <summary>Mode-owned state folded into the plan key (Library: filter, columns, drill).</summary>
        public Func<int>? ModeEpoch { get; init; }
        /// <summary>The planner options the mode decides (Library: the chip and the grid columns).</summary>
        public Func<SidebarPlanOptions, SidebarPlanOptions>? Options { get; init; }
        public Func<Element?>? Head { get; init; }
        public Action<string, string>? ActivateFolder { get; init; }
        public Func<bool>? DisclosesFoldersInline { get; init; }
        /// <summary>A live probe: a non-custom sort refuses positional drops with "clear sorting to reorder".</summary>
        public Func<bool>? TreeSortedNonCustom { get; init; }
        public Action? SortedListRefusalAction { get; init; }
        /// <summary>(kind, from, requested) ⇒ reachable slot. Allocation-free: it runs on the displacement path.</summary>
        public Func<SidebarSectionKind, int, int, int>? ClampReorderSlot { get; init; }
        public Action<PaneReorder>? CommitReorder { get; init; }
        public Action? OnCreatePlaylist { get; init; }
        /// <summary>Arrow navigation ran off an END of the list (−1 above the first row, +1 below the last). Return true when
        /// the mode took focus (Library: Up from the first row lands on the page dropdown, design V.11).</summary>
        public Func<int, bool>? OnEdgeNavigate { get; init; }
        /// <summary>Whether a LIST row carries the pill now (the pane calls it on change only): the Library head's dropdown
        /// carries the pill only when no row does (pill rule 1 beats rule 3).</summary>
        public Action<bool>? PillOnRowChanged { get; init; }
        /// <summary>Ctrl+F with the pane focused, when set (Library: open the toolbar's search box); otherwise Classic's
        /// transient Playlists filter (P1.5).</summary>
        public Action? OpenSearch { get; init; }
    }

    /// <summary>One committed same-list reorder in BAND-SLOT space: the renderer knows the geometry, only the mode knows
    /// where the order lives.</summary>
    internal readonly record struct PaneReorder(SidebarSection Section, int FromSlot, int ToSlot, int SlotCount,
                                                Func<int, string> KeyAt);

    /// <summary>The shared commit: Pinned through the pin store (mapped by pin id — a band position can drift from it),
    /// RECORDED in the undo ring (design C.5 / Q14; never toasted).</summary>
    internal static void DefaultReorderCommit(in PaneReorder r)
    {
        if (r.FromSlot == r.ToSlot || r.Section.Kind != SidebarSectionKind.Pinned) return;
        int pf = Pins.IndexOf(r.KeyAt(r.FromSlot));
        int pt = Pins.IndexOf(r.KeyAt(r.ToSlot));
        if (pf < 0 || pt < 0) MovePinRecorded(r.FromSlot, r.ToSlot);
        else MovePinRecorded(pf, pt);
    }

    /// <summary>One contiguous run of reorderable plan rows owned by one section, at that section's ONE row height.</summary>
    internal readonly record struct PaneBand(string SectionId, int Start, int Count, float Extent)
    {
        public bool Contains(int planIndex) => Count > 0 && planIndex >= Start && planIndex < Start + Count;
    }

    /// <summary>The pane's one inset (the list's 4-px WinUI item margin + the 3-px content-grid top) and the shape of
    /// each section's rows. Every band above or below the list reproduces <see cref="PanePad"/>'s horizontal 4.</summary>
    internal static class PaneMetrics
    {
        public static readonly Edges4 PanePad = new(SidebarRowGeometry.PaneEdge, SidebarRowGeometry.PaneTopInset, SidebarRowGeometry.PaneEdge, 0f);
        public const float PaneInsetH = SidebarRowGeometry.PaneEdge * 2f;
        /// <summary>A band above the list whose content starts at the header text's x (pane 14).</summary>
        public static readonly Edges4 HeadBandInset = new(SidebarRowGeometry.PaneEdge + SidebarRowGeometry.HeaderTextX, 0f,
                                                          SidebarRowGeometry.PaneEdge + SidebarRowGeometry.TrailingPad, 0f);
        public const float EmptyHintHeight = SidebarRowGeometry.EmptyHintHeight;
        /// <summary>Grid cells stay media-card sized at the 460-DIP maximum.</summary>
        public const float GridCellMax = 160f;
    }

    // ══ 5. THE PANE ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>THE ONE SIDEBAR PANE RENDERER (0.2.9 <c>SidebarPane</c>).
    ///
    /// <para>HOW A FRAME FLOWS. (1) Render subscribes the document/projection/pin/folder/search/mode/options epochs and
    /// re-plans in a <c>UseMemo</c> keyed on their fold, into A/B buffers (a plan ALIASES its buffers). (2) The plan is
    /// published to the bound slots as a PLAIN FIELD from a LAYOUT effect, and only the rows the diff found changed get
    /// their epoch bumped. (3) The row count rides <c>CountSignal</c>, written in the same effect — and it is the ONLY
    /// publish signal this render reads (W3-A2): the plan version is the slots', the effects' and the ItemsView's edge.
    /// A republish that changed some rows' content re-skins those rows and
    /// nothing else; the pane itself renders once per RE-PLAN (an input the binder's content gates let through), never a
    /// second time for the publish it caused.</para>
    ///
    /// <para>Route and playback are read ONCE here on behalf of every row (two signal effects that bump only the rows
    /// that flipped) — replacing them with per-row reads restores the Slot×52 + Pill×44 storm per navigation.</para></summary>
    internal sealed partial class PaneView : Component
    {
        // ── frozen-at-mount wiring ──
        internal readonly PaneConfig Config;
        internal readonly bool InDrawer;

        /// <summary>The pane's measured OPEN width — the shared width preference.</summary>
        internal Signal<float> ExpandedWidth => Width;

        readonly SidebarPlanBuffers _paneBuffersA = new(), _paneBuffersB = new();
        bool _presentedUsesA;
        bool _planPublished;
        int _nextPlanEpoch;

        /// <summary>The pane-OWNED search head text (session-only; two panes must not filter each other).</summary>
        readonly Signal<string> _search = new("");
        string _effectiveSearch = "";

        /// <summary>The Classic filter box's section (null = closed). Its query is the pane's search signal.</summary>
        string? _filterSectionId;
        /// <summary>Bumped to focus the filter box: opening it and a second Ctrl+F both move focus into it.</summary>
        readonly Signal<int> _filterFocus = new(0);
        Action<KeyEventArgs>? _paneKey;

        readonly Signal<int> _rowCount = new(0);
        readonly Signal<int> _planVersion = new(0);
        readonly Signal<int> _dispVersion = new(0);
        readonly Signal<int> _disclosureVersion = new(0);

        // ── mount-stable subtrees (W3-A2): built once, handed back by reference on every later render ──
        // Element is immutable and the reconciler compares a ComponentEl by type + key, so reusing the SAME element
        // across renders is exactly the reference short-circuit ToolTipSlots and the props seam rely on. Each of these
        // reads only fields that never change after mount (the layout, the count signal, the controller, `this`).
        Element? _paddedList;
        Element? _dragPeekWatcher;
        /// <summary>The pane's bound width — one Prop for the pane's life, never a fresh closure per render.</summary>
        readonly Prop<float> _expandedWidth = Prop.Of(static () => Sidebar.Width.Value);

        // ── per-row epochs (GROW-ONLY: a slot may address an index a frame after the plan shrank) ──
        Signal<int>[] _rowEpochs = Array.Empty<Signal<int>>();
        byte[] _rowPlay = Array.Empty<byte>();                 // bit 0 current, bit 1 actively playing
        readonly List<int> _rowPlaySet = new();
        readonly List<int> _rowSelSet = new(), _rowSelNext = new(), _rowSelFlip = new();
        Func<int, string?>? _rowRouteOf;

        // ── disclosure ──
        readonly SidebarDisclosures _disclosures = new();
        /// <summary>The sections a header click toggled through <see cref="ToggleSection"/>, recorded by its commit lambda. A
        /// collapse's disclosure entry is already gone when its commit's publish runs (the band settles right after the
        /// commit), so the publish learns "this flip was choreographed" from here. Consumed by every collapse-only publish.</summary>
        readonly HashSet<string> _choreographed = new(StringComparer.Ordinal);
        /// <summary>The last keyed publish's per-row motion (new-plan index to FLIP start / fade), valid only for the
        /// displacement bump that carries it (<see cref="_dedupeSeedsVer"/>) so a later bump never replays it.</summary>
        Dictionary<int, SidebarDedupeMotion.Seed> _dedupeSeeds = new();
        int _dedupeSeedsVer = -1;
        Func<int, (float dx, float dy)?>? _dedupeFlip;
        Func<int, (float from, float delayMs)?>? _dedupeFade;
        Func<string, ItemDisclosureRange?>? _resolveDisclosure;
        Action<Action>? _post;
        Action? _flushPrefsCommit;
        Action<ItemDisclosureDiagnostic>? _disclosureLog;

        readonly ItemsViewController _listController = new();
        /// <summary>The list's edge adapter: one cached delegate, so the options record never allocates a closure per render.</summary>
        Action<int>? _edgeNav;
        bool _pillOnRow;

        /// <summary>THE ONE published drop slot — written once per hover, read by the row's line, its plate and the commit.</summary>
        readonly Signal<SidebarDropSlot> _dropSlot = new(SidebarDropSlot.None);

        // ── the playlist tree's multi-selection (per pane instance) ──
        internal readonly SidebarTreeSelection TreeSelection = new();
        internal readonly Signal<int> SelectionVersion = new(0);
        internal readonly Signal<bool> ChecksVisible = new(false);
        readonly List<string> _treeVisibleOrder = new();

        // ── edit mode (§P4.5) ──
        /// <summary>Edit mode's pane (the edit bar and the Outline), mount-stable: built once and swapped in for the head, list
        /// and footer while <see cref="Sidebar.Editing"/> is set.</summary>
        Element? _editPane;
        bool _wasEditing;
        /// <summary>The keyboard-current row's key, captured on the edit edge while the list is still mounted.</summary>
        string? _editReturnKey;
        bool _restoreAfterEdit;
        readonly HashSet<string> _selBefore = new(StringComparer.Ordinal);

        /// <summary>DRAG PEEK — a transient expansion of a collapsed pane for one drag (never a write to Collapsed).</summary>
        readonly Signal<bool> _dragPeek = new(false);
        /// <summary>The rail tile armed as a drop destination (bound read — the rail subtree is memoized).</summary>
        readonly Signal<string?> _railDropUri = new(null);

        readonly Dictionary<string, Reorderable> _reorder = new(StringComparer.Ordinal);
        readonly List<PaneBand> _bands = new();
        readonly HashSet<string> _pinnedSubtrees = new(StringComparer.Ordinal);
        readonly Dictionary<string, byte> _pinnedDepths = new(StringComparer.Ordinal);
        readonly Dictionary<string, SidebarSection> _sections = new(StringComparer.Ordinal);

        /// <summary>The Pinned band's drop cue is armed while a pinnable drag is live (design V.3): <see cref="Render"/> builds
        /// the plan options with it, and the drag watcher writes it on every drag edge.</summary>
        readonly Signal<bool> _pinDropArmed = new(false);

        /// <summary>The context-menu SHIELD's key — the node MUST stay childless (see Render).</summary>
        internal const string ContextShieldKey = "sidebar:context-shield";

        // ── published to the bound slots (PLAIN FIELDS; seeded with real empty plans, never default) ──
        internal SidebarRowPlan Plan = new(Array.Empty<SidebarRow>(), Array.Empty<SidebarLibraryEntry>(), 0);
        /// <summary>The compact (rail) flag THE PUBLISHED PLAN WAS BUILT WITH — read by the slot (a tile, not a row).</summary>
        internal bool CompactPlan;
        internal SidebarLayoutDoc Doc = SidebarLayoutDoc.Empty;
        internal IOverlayService MenuOverlay = Overlay.Service.Default;
        internal ActionServices? Acts;
        internal Actions.Registry? Registry;

        /// <summary>Each rail tile's anchor by plan index, registered by its slot (grow-only: allocation-free after warm-up),
        /// so Enter on a tile opens the same flyout its click opens.</summary>
        Func<NodeHandle>?[] _tileAnchors = Array.Empty<Func<NodeHandle>?>();
        Func<ContextMenuModel?>? _paneMenuFn;

        internal void SetTileAnchor(int index, Func<NodeHandle> anchor)
        {
            if ((uint)index >= (uint)_tileAnchors.Length)
                Array.Resize(ref _tileAnchors, Math.Max(32, Math.Max(index + 1, _tileAnchors.Length * 2)));
            _tileAnchors[index] = anchor;
        }

        Func<NodeHandle>? TileAnchorAt(int index) => (uint)index < (uint)_tileAnchors.Length ? _tileAnchors[index] : null;

        Func<int, (float dx, float dy)>? _displacement;

        sealed record PlanStage(SidebarLayoutDoc Document, SidebarRowPlan Pane, string EffectiveSearch, bool UsesA,
                                int Epoch, bool Compact, int GridColumns);

        /// <summary>The grid column count the PUBLISHED plan's strips were cut with (GridStripRow wraps by it).</summary>
        internal int GridColumns { get; private set; } = 2;

        /// <summary>THE MID-DRAG FREEZE: a re-projection arriving during a rootlist filing is parked, not published.</summary>
        readonly SidebarStageHold<PlanStage> _deferredStage = new();
        /// <summary>One-shot: this gesture's OWN commit is not a foreign projection and must publish through the freeze.</summary>
        bool _publishThroughFreeze;

        // ── the pill target (design V.5): ONE pill per pane ──
        SidebarPillTarget _pillTarget = SidebarPillTarget.None;
        string _pillRoute = "";
        readonly List<string> _ancestorScratch = new(4);
        InputHooks? _hooks;

        // ── selection travel (the NavigationView pill transaction) ──
        string _selRoute = "";
        string _prevSelRoute = "";
        int _selEpoch;
        NodeHandle _selectionFlightFrom, _selectionFlightTo;
        readonly Dictionary<string, NodeHandle> _selectionPills = new(StringComparer.Ordinal);
        readonly Dictionary<int, string> _selectionRouteByNode = new();
        readonly Dictionary<int, SidebarPillLane> _selectionLaneByNode = new();
        Shell.Route _routeCache = Shell.Route.None;
        string _routeKeyCache = "";

        /// <summary>STATEFUL (estimate-then-correct + scroll anchoring), so created once per pane. Seeded ANALYTICALLY
        /// from the plan (<see cref="SidebarRowExtents"/>) so a folder expansion lands at its final geometry before
        /// anything realizes.</summary>
        readonly RepeatLayout _rowLayout;

        /// <summary>Reordering rides <c>MotionTok.ItemPlacement</c>.</summary>
        static readonly LayoutTransition RowPlacement = new(TransitionChannels.Position, MotionTok.ItemPlacement.ToDynamics());

        // ── rootlist marker stream (derived from the published tree, cached per projection revision) ──
        readonly List<RootlistEntry> _markers = new(256);
        int _markerRevision = -1;

        internal PaneView(PaneConfig config, bool inDrawer)
        {
            Config = config;
            InDrawer = inDrawer;
            _rowLayout = RepeatLayout.Extents(RowExtentSeed, estimatedExtent: SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine));
        }

        public override Element Render()
        {
            _post = UsePost();
            MenuOverlay = UseContext(Overlay.Service) ?? Overlay.Service.Default;
            _hooks = UseContext(InputHooks.Current);
            Acts = ActionServicesOrNull();
            Registry = Actions.Registry.Current ?? Acts?.Extensions;

            // The mode's live document — invoked HERE so the signals it reads subscribe this pane.
            var sourceDoc = Config.Document();
            // The drawer always renders expanded; a live drag peek presents expanded too (it flips twice per drag). Compact is
            // what the FRAME presents (the pane mode the shell resolved), never the window band alone.
            bool compact = !InDrawer && Sidebar.Mode.Value == SidebarPaneMode.Compact && !_dragPeek.Value;
            string search = _search.Value;
            var options = OptionsFor(compact, _pinDropArmed.Value);

            var stage = UseMemo(() => BuildStage(sourceDoc, search, in options), PlanDep(search, compact, in options));
            if (!_planPublished) PublishStage(stage, notify: false);
            int disclosureUiVersion = _disclosureVersion.Value;
            UseLayoutEffect(() => TryPublishStage(stage), DepKey.From(HashCode.Combine(stage.Epoch, disclosureUiVersion)));
            // AFTER the publish: the travel direction needs the plan the rows are about to render from. This read also
            // subscribes the pane to the route, so a navigation re-renders it without re-planning.
            TrackSelection(SelectedRoute);
            UseEffect(InstallBenchHook, DepKey.Empty);
            UseSignalEffect(RefreshPlayState);
            UseSignalEffect(RefreshSelection);
            UseLayoutEffect(RunSelectionTransaction, _selEpoch);
            // THE COUNT, never the plan version (W3-A2). This render decides empty pane vs list off the published plan, which
            // is a function of the COUNT. The 0.3 first cut read `_planVersion` here, so every publish (each one a re-plan the
            // binder's content gates had let through: a cover landing on a saved album, a playlist learning its track count)
            // rendered this pane a SECOND time, and that render's rail memo rebuilt ~26 tooltip-wrapped tiles — the
            // `PaneView×1 a=324K` + `ToolTip×33` lines of the scroll census. The seed lands in PublishStage's synchronous
            // first publish, above, before this read, so the first render already sees the real count.
            int rows = _rowCount.Value;
            ConfigureReorder();

            // EDIT MODE (§P4.5). The list is unmounted while editing, so the keyboard-current row is captured by KEY on the
            // false → true edge (the old list is still mounted here) and restored by key after the true → false edge. An
            // account switch leaves focus where it is (ExitEditCore(false) clears EditExitRestoresFocus).
            bool editing = Sidebar.Editing.Value;
            if (editing && !_wasEditing)
            {
                int current = _listController.CurrentItemIndex;
                var planRows = Plan.Rows;
                _editReturnKey = (uint)current < (uint)planRows.Count ? planRows[current].Key : null;
            }
            else if (!editing && _wasEditing)
                _restoreAfterEdit = Sidebar.EditExitRestoresFocus;
            _wasEditing = editing;
            UseLayoutEffect(() =>
            {
                if (!_restoreAfterEdit) return;
                _restoreAfterEdit = false;
                string? key = _editReturnKey;
                _editReturnKey = null;
                _post?.Invoke(() =>
                {
                    int at = key is null ? -1 : IndexOfRowKey(key);
                    if (at >= 0) _listController.FocusItem(at);   // current + into view + keyboard focus
                    else if (Plan.Rows.Count > 0) _listController.FocusItem(0);   // the row is gone: the first row
                });
            }, DepKey.From(editing ? 1 : 0));

            // ONE layer: the rail is the SAME list planned compact (design V.9), clipped to the frame's presented column. The
            // layer is measured at the OPEN width even while presented compact, so text never reflows through the rail width.
            var layer = new BoxEl
            {
                Key = "pane-layer", Direction = 1, Grow = 1f, Shrink = 0f, ClipToBounds = true,
                Width = _expandedWidth,
                // The docked rail is inert behind a pinned overlay (design C.3); the drawer IS that overlay, so it stays live.
                HitTestVisible = InDrawer || !SidebarPaneModeRules.OverlayPinned(Sidebar.Band.Value, editing),
                // DRAG PEEK: a spring-load waypoint over the whole rail (never a destination).
                DropTarget = compact ? RailPeekDropSpec() : null,
                Children = PaneChildren(rows, compact, editing),
            };

            var children = new List<Element>(2) { layer };
            // Owns the ONE UseDragState() subscription that ends a peek (2 flips per drag, not the drag epoch).
            if (!InDrawer) children.Add(_dragPeekWatcher ??= Embed.Comp(() => new DragPeekWatcher(this)) with { Key = "drag-peek" });

            var root = new BoxEl
            {
                // No fill, no corners: the sidebar is flush frame chrome over Mica (ch 25 §4).
                Grow = 1f, Direction = 1, ZStack = true, ClipToBounds = true,
                OnKeyDown = _paneKey ??= OnPaneKey,
                Children = [.. children],
            };

            // THE CONTEXT-MENU SHIELD. A context flyout makes an element a hit target (ContextBit), so hanging the menu
            // off the root made every dead spot resolve the WHOLE sidebar as the press/hover owner. The menu goes on a
            // ZStack SHELL plus a CHILDLESS full-bleed shield beneath the content: content wins wherever it hits, the
            // shield takes the rest, and a cascade from a childless node reaches nothing.
            if (!Controls.IsNullOverlay(MenuOverlay))
            {
                var svc = MenuOverlay;
                Func<ContextMenuModel?> menu = _paneMenuFn ??= PaneMenu;
                root = new BoxEl
                {
                    Grow = 1f, Direction = 1, ZStack = true, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl { Key = ContextShieldKey }.WithContextMenu(svc, menu),
                        root with { Grow = 0f, Shrink = 0f },
                    ],
                }.WithContextMenu(svc, menu);
            }
            return root;
        }

        // ── the pane layer's children ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The rail's empty body: the rail has no room for the empty hint, so an empty rail is just its head.</summary>
        static readonly BoxEl s_railSpacer = new() { Key = "empty", Grow = 1f };

        /// <summary>The layer's children, top to bottom: the mode head and the body (the padded list or the empty hint). There
        /// is no footer: Settings lives in the profile menu and the palette, the pane menu on the pane's right-click.</summary>
        Element[] PaneChildren(int rows, bool compact, bool editing)
        {
            // Edit mode (expanded only) swaps the head and the list for the Outline; the list is unmounted and its state stays
            // with its element (§P4.5).
            if (editing && !compact) return [_editPane ??= Embed.Comp(() => new EditPane(this)) with { Key = "edit-pane" }];
            Element? modeHead = Config.Head?.Invoke();

            // The list is built ONCE: every option it carries is a mount-stable field or delegate, and the count rides
            // its CountSignal — so a re-render hands the reconciler the same element and it writes nothing.
            // The rail has no room for the empty hint: an empty rail is just its head.
            Element body = rows == 0 ? (compact ? s_railSpacer : EmptyPane()) : (_paddedList ??= PaddedList());
            return modeHead is { } mh ? [mh, body] : [body];
        }

        /// <summary>The plan row whose key is <paramref name="key"/>, or −1.</summary>
        int IndexOfRowKey(string key)
        {
            var rows = Plan.Rows;
            for (int i = 0; i < rows.Count; i++) if (string.Equals(rows[i].Key, key, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>THE PANE'S ONE INSET: PaneMetrics.PanePad (4,3,4,0) around the virtualized list, and nowhere else.</summary>
        Element PaddedList() => new BoxEl
        {
            Key = "plan-pad", Direction = 1, Grow = 1f, Padding = PaneMetrics.PanePad,
            Children = [PlanList()],
        };

        Element PlanList() => ItemsView.CreateBound(
            Plan.Rows.Count,
            scope => Embed.Comp(() => new PaneSlot(this, scope)),
            _rowLayout,
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None,
                Selector = SelectorVisual.None,
                Grow = 1f,
                CountSignal = _rowCount,
                Controller = _listController,
                // One recycle pool per row kind — a header slot never rebinds into an entity row's shape.
                ContentType = ContentTypeOf,
                Scroll = new ScrollOptions
                {
                    AutoEdgeFade = true,
                    ScrollKey = InDrawer ? Config.ScrollKeyPrefix + ".drawer" : Config.ScrollKeyPrefix,
                },
                Reorder = new ReorderOptions
                {
                    ItemDisplacement = _displacement ??= Displacement,
                    DisplacementVersion = _dispVersion,
                },
                // The key-matched publish's motion (a section toggle's or a pin move's displaced and de-duped rows) rides the
                // same displacement bump. Stable delegates: ListOptions freeze at mount.
                Entrance = new EntranceOptions
                {
                    ItemFlipFrom = _dedupeFlip ??= DedupeFlipFrom,
                    ItemFadeFrom = _dedupeFade ??= DedupeFadeFrom,
                },
                Disclosure = new DisclosureOptions
                {
                    Version = _planVersion,
                    ResolveRange = _resolveDisclosure ??= ResolveDisclosureRange,
                    Diagnostic = _disclosureLog ??= LogDisclosure,
                },
                IsItemEnabled = IsRowFocusStop,
                ItemText = RowTypeAheadText,
                IsItemInvokedEnabled = true,
                OnInvoked = InvokeRow,
                KeepAlive = KeepFilterRow,
                OnEdgeNavigate = Config.OnEdgeNavigate is { } edge ? (_edgeNav ??= d => edge(d)) : null,
            }) with { Key = "plan" };

        /// <summary>Give list row <paramref name="index"/> keyboard focus (current + into view) — the Library head's Down
        /// from the dropdown, and the Edit-mode exit's focus return (§P4.5).</summary>
        internal void FocusListItem(int index) => _listController.FocusItem(index);

        // ── the roving stops, typeahead and Enter (P1) ──

        /// <summary>A header, glyph row, entity row or folder is a roving stop; separators, hints and drop bands are not.</summary>
        bool IsRowFocusStop(int index)
        {
            var rows = Plan.Rows;
            return (uint)index < (uint)rows.Count && SidebarTypeAheadRules.IsFocusStop(rows[index].Kind);
        }

        string RowTypeAheadText(int index)
        {
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return "";
            var row = rows[index];
            return SidebarTypeAheadRules.TextOf(row.Kind, RowLabelOf(in row));
        }

        /// <summary>The label typeahead reads for a row: a header's title, an entry's name, a glyph row's destination title.</summary>
        string RowLabelOf(in SidebarRow row)
        {
            if (row.Kind is SidebarRowKind.SectionHeader or SidebarRowKind.SectionTile)
                return SectionOf(row.SectionId) is { } header ? PaneText.TitleOf(header) : "";
            if ((uint)row.EntryIndex < (uint)Plan.Entries.Count) return Plan.Entries[row.EntryIndex].Name;
            return row.Kind == SidebarRowKind.IconRow ? Shell.Dest(Shell.Parse(row.Key)).Title : "";
        }

        /// <summary>Enter (and a double tap) on the roving stop: a header toggles, a folder toggles, a row runs its click; a
        /// rail tile (a section, or a compact folder) opens its flyout, anchored where its click would anchor it.</summary>
        void InvokeRow(int index)
        {
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return;
            var row = rows[index];
            switch (row.Kind)
            {
                case SidebarRowKind.SectionHeader when SectionOf(row.SectionId) is { } s:
                    ToggleSection(s.Id, !s.Collapsed);
                    break;
                case SidebarRowKind.SectionTile when TileAnchorAt(index) is { } tile:
                    OpenSectionFlyout(row.SectionId, tile);
                    break;
                case SidebarRowKind.FolderHeader when CompactPlan && (uint)row.EntryIndex < (uint)Plan.Entries.Count
                                                      && !Plan.Entries[row.EntryIndex].Missing && TileAnchorAt(index) is { } ftile:
                    OpenFolderFlyout(row.SectionId, Plan.Entries[row.EntryIndex], ftile);
                    break;
                case SidebarRowKind.FolderHeader when (uint)row.EntryIndex < (uint)Plan.Entries.Count:
                    var folder = Plan.Entries[row.EntryIndex];
                    if (folder.Missing) return;   // drawn disabled: no click, no disclosure
                    ActivateFolder(folder.FolderId, folder.Name, index);
                    break;
                default:
                    if ((uint)row.EntryIndex < (uint)Plan.Entries.Count)
                    {
                        var e = Plan.Entries[row.EntryIndex];
                        // The slot draws an unnamed, non-track, non-route entry disabled (EntryRow's Enabled rule): inert here too.
                        if (e.Name.Length == 0 && !e.IsTrack && e.Kind != SidebarEntryKind.AppRoute) return;
                    }
                    if (RowRouteOf(index) is { Length: > 0 } route)
                        Navigate(route, (uint)row.EntryIndex < (uint)Plan.Entries.Count ? Plan.Entries[row.EntryIndex].Name : null);
                    else if ((uint)row.EntryIndex < (uint)Plan.Entries.Count && Plan.Entries[row.EntryIndex].IsTrack)
                        Play(Plan.Entries[row.EntryIndex].Uri, asTrack: true);
                    break;
            }
        }

        /// <summary>The Classic filter box's header row stays realized while it is open (its editor keeps caret, focus and IME
        /// composition through a scroll).</summary>
        bool KeepFilterRow(int index)
        {
            var rows = Plan.Rows;
            return _filterSectionId is { } id && (uint)index < (uint)rows.Count
                   && rows[index].Kind == SidebarRowKind.SectionHeader && string.Equals(rows[index].SectionId, id, StringComparison.Ordinal);
        }

        // ── the Classic filter box (design V.11) ──

        internal bool FilterOpenFor(string sectionId) => string.Equals(_filterSectionId, sectionId, StringComparison.Ordinal);

        /// <summary>The first visible top-level section of <paramref name="kind"/>, or null.</summary>
        string? FirstVisibleSectionId(SidebarSectionKind kind)
            => Doc.Find(kind) is { Hidden: false } section ? section.Id : null;

        /// <summary>Ctrl+F on the pane opens the filter under the Playlists header; a second press re-focuses it.</summary>
        void OnPaneKey(KeyEventArgs e)
        {
            if (e.Handled) return;
            // Ctrl+Z / Ctrl+Y with focus IN the sidebar (this handler only sees keys routed through the pane): the ring's
            // undo/redo, while editing or while a sidebar toast is open (Q6). A focused TextBox consumed its own Ctrl+Z
            // first; FocusedIsTextEditor covers its empty-stack fall-through (TextEditCore.cs:349-352).
            if (e.Mods == KeyModifiers.Ctrl && (e.KeyCode == Keys.Z || e.KeyCode == Keys.Y))
            {
                if (SidebarUndoRing.KeyAllowed(Sidebar.Editing.Peek(), Sidebar.SidebarToastOpen, Shell.FocusedIsTextEditor()))
                {
                    if (e.KeyCode == Keys.Z) Sidebar.Undo(); else Sidebar.Redo();
                    e.Handled = true;
                }
                return;
            }
            if (e.KeyCode != Keys.F || e.Mods != KeyModifiers.Ctrl) return;
            if (Config.OpenSearch is { } open) { open(); e.Handled = true; return; }
            string? tree = FirstVisibleSectionId(SidebarSectionKind.Playlists);
            if (tree is null) return;
            if (!HasHeaderRow(tree)) return;   // a title-less section has nowhere to host the box
            _filterSectionId = tree;
            _filterFocus.Value = _filterFocus.Peek() + 1;
            RepublishNow();
            e.Handled = true;
        }

        bool HasHeaderRow(string sectionId)
        {
            var rows = Plan.Rows;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Kind == SidebarRowKind.SectionHeader && string.Equals(rows[i].SectionId, sectionId, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>Closes the filter box and clears its query.</summary>
        internal void CloseFilter()
        {
            if (_filterSectionId is null) return;
            _filterSectionId = null;
            _search.Value = "";
            RepublishNow();
        }

        /// <summary>The transient filter (design V.11): a 32-px field under the Playlists header, focused on open; Esc, or
        /// leaving it empty on blur, closes it and clears the query.</summary>
        internal Element FilterBox() => Embed.Comp(() => new PaneFilterBox(this)) with { Key = "pane-filter" };

        /// <summary>The filter field: the pane's own search signal in a plain editor, focused when it opens and again on a
        /// re-press of Ctrl+F (the ticket moves).</summary>
        sealed class PaneFilterBox(PaneView owner) : Component
        {
            public override Element Render()
            {
                var hooks = UseContext(InputHooks.Current);
                var post = UsePost();
                var root = UseRef<NodeHandle>(default);
                // A fresh box takes focus on its first realization; a later Ctrl+F moves focus through the ticket.
                var pending = UseRef(true);
                int ticket = owner._filterFocus.Value;
                void Focus(NodeHandle h) => post(() => hooks.FocusNode?.Invoke(hooks.FirstFocusableIn?.Invoke(h) ?? h, true));
                UseLayoutEffect(() =>
                {
                    if (!root.Value.IsNull) Focus(root.Value);
                }, DepKey.From(ticket));

                var parts = UseMemo(() =>
                {
                    var pr = new TemplateParts();
                    pr[EditableText.PartRoot] = b => b with
                    {
                        Fill = ColorF.Transparent, HoverFill = ColorF.Transparent,
                        OnRealized = h =>
                        {
                            root.Value = h;
                            if (!pending.Value) return;
                            pending.Value = false;
                            Focus(h);
                        },
                    };
                    return pr;
                }, DepKey.Empty);

                return new BoxEl
                {
                    Direction = 1, Height = 32f,
                    Margin = new Edges4(SidebarRowGeometry.HeaderTextX, 0f, SidebarRowGeometry.TrailingPad, 4f),
                    Children =
                    [
                        Embed.Comp(() => new EditableText
                        {
                            Text = owner._search, Placeholder = Loc.Get(Strings.Sidebar.V3.SearchPlaceholder),
                            Width = float.NaN, Height = 32f, FontSize = 13f, Parts = parts,
                            ShowDeleteButton = true,
                            PreviewKeyDown = e =>
                            {
                                if (e.KeyCode != Keys.Escape) return false;
                                owner.CloseFilter();
                                return true;
                            },
                            OnFocusChanged = gained =>
                            {
                                if (!gained && owner._search.Peek().Length == 0) owner.CloseFilter();
                            },
                        }),
                    ],
                };
            }
        }

        // ── the section header's ⋯ menu ──

        /// <summary>A section header's ⋯ menu (design C.4): the header model — limits, Show, Collapse/Expand, Move, Hide section —
        /// mapped by the one mapper. The lock reasons come from <see cref="SidebarMenus.LockingNames"/>. The kinds with no header
        /// menu are ruled out from the catalogue; the model itself is built only when the menu opens.</summary>
        internal Func<ContextMenuModel?>? HeaderMenu(string sectionId)
        {
            if (!SidebarCatalogue.TryKindOf(sectionId, out var kind) || kind is SidebarSectionKind.Library or SidebarSectionKind.Home or SidebarSectionKind.Settings) return null;
            return () =>
            {
                SidebarMenus.Overlay = MenuOverlay;
                var rows = SidebarMenuModel.Header(Sidebar.Layout.Peek(), Sidebar.State, sectionId, SidebarMenus.LockingNames());
                return rows.Count == 0 ? null : new ContextMenuModel(SidebarMenus.Map(rows, sectionId, this));
            };
        }

        /// <summary>The authored-empty pane (ch 25 W8): it names the state only. There is no customizer to offer a fix from.</summary>
        Element EmptyPane()
        {
            var kids = new List<Element>(2)
            {
                Icon(Icons.SplitView, 24f, Tok.TextTertiary),
                new TextEl(Loc.Get("sidebar.paneEmpty"))
                {
                    Size = 14f, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2,
                },
            };
            return new BoxEl
            {
                Key = "empty", Direction = 1, Grow = 1f, Gap = Spacing.S,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(16f, 24f, 16f, 24f),
                Children = [.. kids],
            };
        }

        int ContentTypeOf(int index)
        {
            var rows = Plan.Rows;
            return (uint)index < (uint)rows.Count ? (int)rows[index].Kind : 0;
        }

        // ── planning ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Everything outside the document the plan depends on, folded into one key. Reading them here is ALSO
        /// the subscription that re-plans this pane.</summary>
        DepKey PlanDep(string search, bool compact, in SidebarPlanOptions options)
        {
            int layoutVer = LayoutVersion.Value;
            int entriesVer = Binder is { } b ? b.Entries.Version.Value : 0;
            // Everything else the planner reads from the binder — pins band, recency, feeds, a contributed section's rows —
            // moves THIS edge, not the entries cell (G-172/G-173).
            int inputVer = Binder is { } bi ? bi.InputVersion.Value : 0;
            int pinsVer = PinsVersion.Value;
            int folderVer = FolderVersion.Value;
            int binderEpoch = s_binderEpoch.Value;
            int revision = Binder?.Revision ?? 0;
            int mode = Config.ModeEpoch?.Invoke() ?? 0;
            // The planner options the mode decided: Library's chip, grid columns and folder shape. The drop band's arming
            // is its own fold (PinDrop), so a drag that starts or ends names that cause instead of Mode.
            int pinDrop = options.PinDropArmed ? 1 : 0;
            int optionsFold = HashCode.Combine((int)options.Filter, options.GridColumns, options.FoldersInline, options.Drilled);
            // The re-plan census (Sidebar.Census.cs): which of these inputs moved since the LAST BUILD. Accumulated here
            // (every render folds the key) and claimed by BuildStage, which runs only when the key actually moved.
            var moved = Shell.SidebarReplanCause.None;
            if (layoutVer != _depLayout) moved |= Shell.SidebarReplanCause.Layout;
            if (entriesVer != _depEntries) moved |= Shell.SidebarReplanCause.Entries;
            if (inputVer != _depInput) moved |= Shell.SidebarReplanCause.Input;
            if (pinsVer != _depPins) moved |= Shell.SidebarReplanCause.Pins;
            if (folderVer != _depFolder) moved |= Shell.SidebarReplanCause.Folder;
            if (binderEpoch != _depBinder) moved |= Shell.SidebarReplanCause.Binder;
            if (revision != _depRevision) moved |= Shell.SidebarReplanCause.Revision;
            if (mode != _depMode) moved |= Shell.SidebarReplanCause.Mode;
            if (optionsFold != _depOptions) moved |= Shell.SidebarReplanCause.Mode;
            if (pinDrop != _depPinDrop) moved |= Shell.SidebarReplanCause.PinDrop;
            if (!string.Equals(search, _depSearch, StringComparison.Ordinal)) moved |= Shell.SidebarReplanCause.Search;
            _depLayout = layoutVer; _depEntries = entriesVer; _depInput = inputVer; _depPins = pinsVer; _depFolder = folderVer;
            _depBinder = binderEpoch; _depRevision = revision; _depMode = mode; _depOptions = optionsFold; _depPinDrop = pinDrop;
            _depSearch = search;
            _pendingReplanCauses |= moved;
            return DepKey.Combine(DepKey.From(layoutVer, entriesVer, pinsVer, folderVer),
                DepKey.Combine(DepKey.From(revision, mode, optionsFold, binderEpoch),
                    DepKey.Combine(DepKey.Combine(DepKey.From(inputVer), DepKey.From(compact ? 1 : 0, pinDrop)), search)));
        }

        // The last PlanDep inputs + the causes accumulated since the last build (Sidebar.Census.cs).
        int _depLayout = int.MinValue, _depEntries = int.MinValue, _depInput = int.MinValue, _depPins = int.MinValue,
            _depFolder = int.MinValue, _depBinder = int.MinValue, _depRevision = int.MinValue, _depMode = int.MinValue,
            _depOptions = int.MinValue, _depPinDrop = int.MinValue;
        string? _depSearch;
        Shell.SidebarReplanCause _pendingReplanCauses;

        /// <summary>The options this pane plans with: the frame's presentation and the drop band's arming first, then the
        /// mode's decision (<see cref="PaneConfig.Options"/>). The caller passes the two signal reads it subscribes to.</summary>
        SidebarPlanOptions OptionsFor(bool compact, bool pinDropArmed)
        {
            var options = new SidebarPlanOptions(
                Mode: compact ? SidebarPaneMode.Compact : SidebarPaneMode.Expanded,
                PinDropArmed: pinDropArmed);
            return Config.Options is { } shape ? shape(options) : options;
        }

        PlanStage BuildStage(SidebarLayoutDoc document, string search, in SidebarPlanOptions options)
        {
            Shell.SidebarReplanCensus.NoteBuild(_pendingReplanCauses);
            _pendingReplanCauses = Shell.SidebarReplanCause.None;
            var input = Input(search);
            bool useA = !_planPublished || !_presentedUsesA;
            var paneBuffers = useA ? _paneBuffersA : _paneBuffersB;
            var pane = Sidebar.Plan(document, in input, in options, paneBuffers);
            return new PlanStage(document, pane, SidebarSearch.Normalize(input.Search), useA, ++_nextPlanEpoch,
                options.Mode == SidebarPaneMode.Compact, options.GridColumns);
        }

        /// <summary>A collapsed section's rows for its flyout: the ONE planner over a one-section document with that section
        /// expanded and presented as a List (the flyout draws one entry per row, so a Grid's strips would drop all but their
        /// first entry), against the pane's own input.</summary>
        internal SidebarRowPlan PlanSectionExpanded(string sectionId, SidebarPlanBuffers buffers)
        {
            var input = Input(_search.Peek());
            return SidebarRowPlanner.BuildSection(Doc, sectionId, in input, buffers);
        }

        void TryPublishStage(PlanStage stage)
        {
            // A collapse's rows stay in the DOCUMENT until its commit, so a publish mid-collapse still carries them; each band
            // re-finds its rows by key, so a publish never has to wait for a disclosure to settle.
            if (_planPublished && stage.UsesA == _presentedUsesA && stage.Compact == CompactPlan
                && ReferenceEquals(stage.Pane.Rows, Plan.Rows)) return;
            // THE MID-DRAG FREEZE — exempt while any disclosure is in flight (spring-loading a folder exists to reveal its
            // children) and for this gesture's own commit. Track drags are not frozen: they aim at identity, not position.
            if (_publishThroughFreeze) _publishThroughFreeze = false;
            else if (_disclosures.Count == 0 && _deferredStage.TryHold(Drag.LiveRootlistDrag(), stage)) return;
            PublishStage(stage, notify: true);
        }

        /// <summary>Publish what the freeze parked, exactly once (the drag watcher's layout effect, on session end).</summary>
        internal void FlushDeferredStage()
        {
            _publishThroughFreeze = false;
            if (_deferredStage.TryFlush(out var stage) && stage is not null) PublishStage(stage, notify: true);
        }

        internal void DiscardDeferredStage() => _deferredStage.Discard();

        void PublishStage(PlanStage stage, bool notify)
        {
            _deferredStage.Discard();
            // Captured BEFORE the swap — the A/B buffers keep the outgoing rows alive for the diff.
            var oldRows = Plan.Rows;
            var oldEntries = Plan.Entries;
            var oldDoc = Doc;
            // A new document, a new effective query or a presentation flip (expanded ⇄ rail: the same rows, re-planned)
            // changes what rows draw without necessarily changing the row record, so those bump wholesale. A document that
            // only COLLAPSED or EXPANDED a section is not one of them: every section/folder toggle mints such a document, and
            // bumping wholesale re-rendered every realized row on each click (~15 KB a row, ~0.5 MB a toggle in the frame
            // bench). Its rows still re-seed their extents (by key, for a choreographed toggle - see below; anchored and
            // by index for any other), like any re-shaped plan; only the rows the diff finds changed, and the headers
            // whose state flipped, re-render.
            bool collapseOnly = _planPublished && !ReferenceEquals(stage.Document, oldDoc) && stage.Document.SameExceptCollapsed(oldDoc);
            bool wholesale = !_planPublished
                             || (!ReferenceEquals(stage.Document, oldDoc) && !collapseOnly)
                             || !string.Equals(stage.EffectiveSearch, _effectiveSearch, StringComparison.Ordinal)
                             || stage.Compact != CompactPlan;
            // THE KEY-MATCHED PATH (S2d, S3, J2). A section toggle that ran through the disclosure channel, and a pin or
            // unpin (pins arrive through the binder, not the document, so those publishes are neither wholesale nor
            // collapse-only), edit rows OUTSIDE the reveal band too (the planner's pin dedupe). They splice the extent table
            // by KEY (every survivor keeps its measured extent, the scroll offset holds) and seed the displaced rows' glide,
            // instead of re-seeding the table index by index. Any other collapse-only publish (a header-menu or remote
            // collapse) keeps the anchored Reseed with no motion. A folder disclosure in flight stands the path down: its
            // band range comes from the entries, which the seeds do not read.
            bool flips = collapseOnly && !wholesale && SidebarDedupeMotion.FlippedSectionsAll(oldDoc, stage.Document, _choreographed);
            bool pinMove = !wholesale && !collapseOnly && SidebarDedupeMotion.PinnedKeysChanged(oldRows, stage.Pane.Rows);
            if (collapseOnly && _choreographed.Count > 0)
                foreach (var consumed in SidebarDedupeMotion.FlippedSections(oldDoc, stage.Document)) _choreographed.Remove(consumed);   // only the ids this publish flipped
            MeasuredStackVirtualLayout? keyed = null;
            float[]? oldExtents = null;
            if ((flips || pinMove) && !_disclosures.AnyFolder()
                && _rowLayout.CustomLayout is MeasuredStackVirtualLayout measured && measured.ItemCount == oldRows.Count)
            {
                keyed = measured;
                oldExtents = new float[oldRows.Count];
                for (int i = 0; i < oldExtents.Length; i++) oldExtents[i] = measured.ItemRect(i, 0f).H;   // a toggle, not a frame
            }

            Doc = stage.Document;
            Plan = stage.Pane;
            CompactPlan = stage.Compact;
            GridColumns = stage.GridColumns;
            _effectiveSearch = stage.EffectiveSearch;
            _presentedUsesA = stage.UsesA;
            _planPublished = true;
            RebuildIndex(Plan);
            ResolvePillTarget(SelectedRoutePeek);
            ConfigureReorder();
            EnsureRowSlots(Plan.Rows.Count);
            if (keyed is not null && oldExtents is not null)
            {
                var olds = oldExtents;
                // The old band ranges come from the PRE-swap rows (oldRows), never from Plan.Rows. ItemsViewController's
                // SpliceDisclosures then sees the layout's count already equal and does not splice a second time.
                _dedupeSeeds = SidebarDedupeMotion.ForPublish(oldDoc, Doc, oldRows, Plan.Rows, _disclosures.SectionIds(),
                    i => (uint)i < (uint)olds.Length ? olds[i] : float.NaN, RowExtentSeed, out var splices);
                foreach (var splice in splices) keyed.Splice(splice.At, splice.Removed, splice.Inserted);
                if (keyed.ItemCount != Plan.Rows.Count) { _dedupeSeeds.Clear(); ReseedRowExtents(); }
            }
            else
            {
                _dedupeSeeds.Clear();
                if (wholesale || collapseOnly) ReseedRowExtents();
            }
            if (!notify)
            {
                // The FIRST publish runs synchronously inside the pane's render, before anything has read the count
                // signal: seeding it here is a forward write (no subscriber yet), and Render's read right after it sees
                // the real count on frame one.
                _rowCount.Value = Plan.Rows.Count;
                return;
            }

            Shell.SidebarReplanCensus.NotePublish(wholesale);
            void PublishSignals()
            {
                _rowCount.Value = Plan.Rows.Count;
                _planVersion.Value = _planVersion.Peek() + 1;
                // The entrance edge: the list plays the seeds once, on the displacement bump that carries them.
                if (_dedupeSeeds.Count > 0)
                {
                    _dedupeSeedsVer = _dispVersion.Peek() + 1;
                    _dispVersion.Value = _dedupeSeedsVer;
                }
                if (wholesale) BumpAllRowEpochs();
                else
                {
                    BumpChangedRowEpochs(oldRows, oldEntries);
                    if (collapseOnly) BumpFlippedHeaders(oldDoc);
                }
            }
            if (Context.Runtime is { } runtime) runtime.Batch(PublishSignals);
            else PublishSignals();
        }

        /// <summary>The planner input. Its lists ALIAS the binder's buffers (valid until the next rebuild).</summary>
        SidebarProjectionInput Input(string search)
        {
            var binder = Binder;
            var input = binder?.CurrentInput ?? default;
            if (binder is null || binder.Revision == 0)
            {
                // No projection yet: every dynamic source is honestly PENDING — skeletons, never an empty library.
                input = input with { LibraryState = SidebarSourceState.Pending, TreeState = SidebarSourceState.Pending };
            }
            input = input with { LikedTitle = Loc.Get("nav.likedSongs") };
            if (Config.Input is { } shape) input = shape(input);
            if (search.Length > 0) input = input with { Search = search };
            return input;
        }

        /// <summary>Rebuilt with every plan: the section map, the tree order + prune, the Pinned reorder band.</summary>
        void RebuildIndex(SidebarRowPlan plan)
        {
            _sections.Clear();
            var sections = Doc.Sections;
            for (int i = 0; i < sections.Count; i++) _sections[sections[i].Id] = sections[i];

            _bands.Clear();
            RebuildTreeSelectionOrder(plan);
            if (TreeSelection.Prune(_treeVisibleOrder))
            {
                if (ChecksVisible.Peek() != TreeSelection.CheckLaneVisible)
                    ChecksVisible.Value = TreeSelection.CheckLaneVisible;
                SelectionVersion.Value = SelectionVersion.Peek() + 1;
            }
            _pinnedSubtrees.Clear();
            _pinnedDepths.Clear();
            var rows = plan.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (SectionOf(row.SectionId)?.Kind != SidebarSectionKind.Pinned) continue;
                if (row.Kind == SidebarRowKind.SectionHeader)
                {
                    _pinnedDepths[row.SectionId] = row.Depth;
                    continue;
                }
                // A pinned band showing folder descendants is not contiguous in slot space: disarm it.
                if (row.EntryIndex >= 0 && _pinnedDepths.TryGetValue(row.SectionId, out byte sectionDepth)
                    && row.Depth > sectionDepth)
                    _pinnedSubtrees.Add(row.SectionId);
            }

            string? bandId = null;
            int bandStart = 0;
            float bandExtent = SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                bool item = IsReorderableRow(row);
                if (item && bandId is not null && string.Equals(bandId, row.SectionId, StringComparison.Ordinal)) continue;
                if (bandId is not null)
                {
                    _bands.Add(new PaneBand(bandId, bandStart, i - bandStart, bandExtent));
                    bandId = null;
                }
                if (!item) continue;
                // Only Pinned reorders in place: Your Library's list reorders as a rootlist move, never a local overlay.
                var section = SectionOf(row.SectionId);
                if (section is null || section.Kind != SidebarSectionKind.Pinned || _pinnedSubtrees.Contains(row.SectionId))
                    continue;
                bandId = row.SectionId;
                bandStart = i;
                bandExtent = SidebarRowGeometry.PitchOf(section.Shape);
            }
            if (bandId is not null) _bands.Add(new PaneBand(bandId, bandStart, rows.Count - bandStart, bandExtent));
        }

        static bool IsReorderableRow(in SidebarRow row) => row.Kind
            is SidebarRowKind.EntityRow or SidebarRowKind.IconRow or SidebarRowKind.FolderHeader;

        // ── disclosure choreography ────────────────────────────────────────────────────────────────────────────────
        // Every section/folder opens and closes on the engine's reveal band (fluent-gpu smooth-reveal plan §10): the
        // presented height springs under MotionTok.Reveal while the rows below ride it, any number at once, and a click on
        // a key already in flight REVERSES it from where it stands. Expand: the rows enter the document and the plan first
        // (published on the click frame), then the band reveals them. Collapse: the rows stay until the band rests, then
        // the preference write commits. Reduced motion is the engine's (a value at the seed) — never branched on here.

        internal bool DisclosureOpen(string id, bool folder, bool fallback) => _disclosures.IsOpen(id, folder, fallback);

        bool TrySectionBodyRange(string sectionId, out ItemDisclosureRange range)
        {
            if (!SidebarRowGeometry.TrySectionBodyRange(Plan.Rows, sectionId, out int first, out int count))
            {
                range = default;
                return false;
            }
            range = new ItemDisclosureRange("section:" + sectionId, first, count);
            return true;
        }

        bool TryFolderDescendantRange(string folderId, out ItemDisclosureRange range)
        {
            if (!SidebarRowGeometry.TryFolderDescendantRange(Plan.Rows, Plan.Entries, folderId, out int first, out int count))
            {
                range = default;
                return false;
            }
            range = new ItemDisclosureRange("folder:" + folderId, first, count);
            return true;
        }

        bool TryRange(string id, bool folder, out ItemDisclosureRange range)
            => folder ? TryFolderDescendantRange(id, out range) : TrySectionBodyRange(id, out range);

        /// <summary>The band's rows in the PUBLISHED plan, by key — null once a committed collapse removed them.</summary>
        ItemDisclosureRange? ResolveDisclosureRange(string key)
        {
            if (key.StartsWith("section:", StringComparison.Ordinal))
                return TrySectionBodyRange(key.Substring("section:".Length), out var s) ? s : null;
            if (key.StartsWith("folder:", StringComparison.Ordinal))
                return TryFolderDescendantRange(key.Substring("folder:".Length), out var f) ? f : null;
            return null;
        }

        void StartDisclosure(string key, string id, bool folder, bool open, Action commit)
        {
            var toggle = _disclosures.Start(key, id, folder, open);
            if (toggle == SidebarDisclosures.Toggle.Ignore) return;
            if (toggle == SidebarDisclosures.Toggle.Reverse)
            {
                // Mid-flight reversal. Reopening a closing band: its collapse never committed (the document still holds the
                // rows), so it springs back open with no commit. Closing an opening band: the open already committed, so
                // the close carries the commit like any collapse.
                if (TryRange(id, folder, out var live))
                    _listController.BeginDisclosure(live, open ? ItemDisclosureDirection.Expand : ItemDisclosureDirection.Collapse,
                        collapseCommit: open ? null : WithPrefsCommit(commit), settled: () => DisclosureSettled(key));
                else
                {
                    if (!open) { commit(); SchedulePrefsCommit(); }
                    DisclosureSettled(key);
                }
            }
            else if (open)
            {
                // PUBLISH ON THE CLICK FRAME (input phase ⇒ a forward write), through any drag freeze: the band arms against
                // the inserted rows before the first expanded paint — the chevron and the rows move together.
                commit();
                SchedulePrefsCommit();
                _publishThroughFreeze = true;
                RepublishNow();
                _publishThroughFreeze = false;   // an unchanged stage returns before consuming it; never let it leak to the next drag
                if (TryRange(id, folder, out var opened))
                    _listController.BeginDisclosure(opened, ItemDisclosureDirection.Expand, settled: () => DisclosureSettled(key));
                else DisclosureSettled(key);   // nothing to disclose (an empty section)
            }
            else if (TryRange(id, folder, out var closing))
                _listController.BeginDisclosure(closing, ItemDisclosureDirection.Collapse,
                    collapseCommit: WithPrefsCommit(commit), settled: () => DisclosureSettled(key));
            else
            {
                commit();
                SchedulePrefsCommit();
                DisclosureSettled(key);
            }
            _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
            BumpDisclosureEpochs(id, folder, TryRange(id, folder, out var bumped) ? bumped : null);
        }

        void DisclosureSettled(string key)
        {
            if (!_disclosures.TryGet(key, out var entry)) return;
            _disclosures.Settled(key);
            _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
            BumpDisclosureEpochs(entry.Id, entry.Folder, TryRange(entry.Id, entry.Folder, out var band) ? band : null);
        }

        /// <summary>A disclosure edge re-skins the header (its chevron) and the disclosed band — nothing else.</summary>
        void BumpDisclosureEpochs(string id, bool folder, ItemDisclosureRange? band)
        {
            int header = folder
                ? SidebarRowGeometry.FolderHeaderIndexOf(Plan.Rows, Plan.Entries, id)
                : SectionHeaderIndexOf(Plan, id);
            if (header >= 0) BumpRowEpoch(header);
            if (band is not { Count: > 0 } b) return;
            int end = Math.Min(b.FirstIndex + b.Count, Plan.Rows.Count);
            for (int i = Math.Max(0, b.FirstIndex); i < end; i++) BumpRowEpoch(i);
        }

        static int SectionHeaderIndexOf(SidebarRowPlan plan, string sectionId)
        {
            var rows = plan.Rows;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Kind == SidebarRowKind.SectionHeader
                    && string.Equals(rows[i].SectionId, sectionId, StringComparison.Ordinal)) return i;
            return -1;
        }

        void RepublishNow()
        {
            if (!_planPublished) return;
            TryPublishStage(BuildStage(Config.Document(), _search.Peek(), OptionsFor(CompactPlan, _pinDropArmed.Peek())));
        }

        Action WithPrefsCommit(Action commit) => () => { commit(); SchedulePrefsCommit(); };

        /// <summary>Drain the coalesced document write on the NEXT frame — never inside the click that must plan,
        /// publish, realize and arm.</summary>
        void SchedulePrefsCommit()
        {
            if (_post is { } post) post(_flushPrefsCommit ??= FlushPendingCommit);
            else FlushPendingCommit();
        }

        /// <summary>Always-on disclosure lifecycle log (0.2.9's env-gated trace, re-homed per CLAUDE.md).</summary>
        static void LogDisclosure(ItemDisclosureDiagnostic d)
        {
            Log.Info("sidebar", "disclosure." + d.Kind + " key=" + d.Range.Key + " dir=" + d.Direction
                                + " first=" + d.Range.FirstIndex + " count=" + d.Range.Count);
        }

        // ── the per-row reads ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Subscribe the CALLING computation to ONE row's epoch. Load-bearing: a bound slot is a frozen child.
        /// Out of range falls back to the plan version, which the publish that grows the array always bumps.</summary>
        internal int SubscribeRowEpoch(int index)
        {
            var epochs = _rowEpochs;
            return (uint)index < (uint)epochs.Length ? epochs[index].Value : _planVersion.Value;
        }

        internal (bool Playing, bool Animated) RowPlayState(int index)
        {
            var play = _rowPlay;
            byte packed = (uint)index < (uint)play.Length ? play[index] : (byte)0;
            return ((packed & 1) != 0, (packed & 2) != 0);
        }

        void EnsureRowSlots(int count)
        {
            if (count <= _rowEpochs.Length) return;
            int cap = Math.Max(32, _rowEpochs.Length);
            while (cap < count) cap *= 2;
            var epochs = new Signal<int>[cap];
            Array.Copy(_rowEpochs, epochs, _rowEpochs.Length);
            for (int i = _rowEpochs.Length; i < cap; i++) epochs[i] = new Signal<int>(0);
            var play = new byte[cap];
            Array.Copy(_rowPlay, play, _rowPlay.Length);
            _rowEpochs = epochs;
            _rowPlay = play;
        }

        void BumpRowEpoch(int index)
        {
            var epochs = _rowEpochs;
            if ((uint)index < (uint)epochs.Length) epochs[index].Value = epochs[index].Peek() + 1;
        }

        /// <summary>The headers of the sections whose Collapsed flag differs from <paramref name="oldDoc"/>: their chevron
        /// reads the flag, and a header row's record does not carry it (a collapse from Edit mode or a reset has no
        /// disclosure to re-skin it).</summary>
        void BumpFlippedHeaders(SidebarLayoutDoc oldDoc)
        {
            var sections = Doc.Sections;
            for (int i = 0; i < sections.Count && i < oldDoc.Sections.Count; i++)
            {
                if (sections[i].Collapsed == oldDoc.Sections[i].Collapsed) continue;
                int header = SectionHeaderIndexOf(Plan, sections[i].Id);
                if (header >= 0) BumpRowEpoch(header);
            }
        }

        void BumpAllRowEpochs()
        {
            var epochs = _rowEpochs;
            for (int i = 0; i < epochs.Length; i++) epochs[i].Value = epochs[i].Peek() + 1;
        }

        /// <summary>Bump exactly the rows whose record or ENTRY differs from the presented plan's — through
        /// <see cref="PlanDiff"/>, whose entry compare is by VALUE (see its remarks for the mosaic-tiles trap).</summary>
        void BumpChangedRowEpochs(IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries)
        {
            var rows = Plan.Rows;
            var entries = Plan.Entries;
            for (int i = 0; i < rows.Count; i++)
                if (PlanDiff.RowChanged(oldRows, oldEntries, rows, entries, i)) BumpRowEpoch(i);
        }

        /// <summary>The pane's ONE read of playback on behalf of every row: the coarse active-context gate first (an idle
        /// app never joins the identity fan-out), then a packed byte per row, bumping only the rows that flipped.</summary>
        void RefreshPlayState()
        {
            _ = _planVersion.Value;   // a republish re-plans which index holds which entity
            var seam = Controls.NowPlaying;
            bool active = seam is not null && seam.HasActiveContext.Value;
            bool playing = active && seam!.IsPlaying.Value;

            var play = _rowPlay;
            for (int i = 0; i < _rowPlaySet.Count; i++)
            {
                int idx = _rowPlaySet[i];
                if ((uint)idx >= (uint)play.Length || play[idx] == 0) continue;
                play[idx] = 0;
                BumpRowEpoch(idx);
            }
            _rowPlaySet.Clear();
            if (!active) return;

            byte packed = playing ? (byte)3 : (byte)1;
            var rows = Plan.Rows;
            int n = Math.Min(rows.Count, play.Length);
            for (int i = 0; i < n; i++)
            {
                string uri = RowPlayUri(i);
                if (uri.Length == 0 || !seam!.RelatesTo(uri)) continue;
                _rowPlaySet.Add(i);
                if (play[i] == packed) continue;
                play[i] = packed;
                BumpRowEpoch(i);
            }
        }

        /// <summary>The uri row <paramref name="index"/> would play, resolved EXACTLY as the slot resolves it.</summary>
        string RowPlayUri(int index)
        {
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return "";
            var row = rows[index];
            var entries = Plan.Entries;
            bool resolved = row.EntryIndex >= 0 && row.EntryIndex < entries.Count;
            return row.Kind == SidebarRowKind.EntityRow && resolved ? entries[row.EntryIndex].Uri : "";
        }

        string RouteKeyOf(in Shell.Route route)
        {
            if (!route.Equals(_routeCache))
            {
                _routeCache = route;
                _routeKeyCache = Shell.NameOf(route);
            }
            return _routeKeyCache;
        }

        /// <summary>The last pointer was a finger: header ⋯ buttons stay visible (design V.6).</summary>
        internal bool TouchLast => _hooks?.LastPointerWasTouch?.Invoke() ?? false;

        /// <summary>Recompute the one pill target for <paramref name="route"/> over the PUBLISHED plan.</summary>
        void ResolvePillTarget(string route)
        {
            SidebarPillRules.AncestorFolders(Binder?.CurrentInput.PlaylistTree, route, _ancestorScratch);
            _pillTarget = SidebarPillRules.Resolve(Plan.Rows, Plan.Entries, _rowRouteOf ??= RowRouteOf, route, _ancestorScratch,
                OwningSection(route));
            bool onRow = _pillTarget.PlanIndex >= 0;
            if (onRow != _pillOnRow)
            {
                _pillOnRow = onRow;
                Config.PillOnRowChanged?.Invoke(onRow);
            }
        }

        /// <summary>The route a pill drawn on row <paramref name="index"/> registers under: the live route when the row is
        /// the pill's ancestor / header anchor, the row's own route otherwise.</summary>
        internal string? PillRouteOf(int index, string liveRoute)
            => index == _pillTarget.PlanIndex && _pillTarget.Anchor != SidebarPillAnchor.Row ? liveRoute : RowRouteOf(index);

        /// <summary>The route plan row <paramref name="index"/> navigates to, by the rule the selection sweep uses: a projected
        /// entry's route, else a hand-placed route item's key. Null when the row is not a navigation target.</summary>
        internal string? RowRouteOf(int index)
        {
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return null;
            var row = rows[index];
            return SidebarRowResolve.RouteOf(in row, Plan.Entries);
        }

        /// <summary>The first visible top-level section that would hold <paramref name="route"/> if it were expanded: Pinned for
        /// a pinned route, Collections for one of its pages, Playlists for a playlist or folder in the tree, and Your Library
        /// for an entity route under the Library layout. Null otherwise.</summary>
        string? OwningSection(string route)
        {
            if (route.Length == 0) return null;
            var doc = Doc;
            if (doc.Find(SidebarSectionKind.Pinned) is { Hidden: false } pinned && IsPinned(SidebarPinId.FromRoute(route))) return pinned.Id;
            if (doc.Find(SidebarSectionKind.Collections) is { Hidden: false } collections && ItemsHold(collections.Items, route))
                return collections.Id;
            if (doc.Find(SidebarSectionKind.Playlists) is { Hidden: false } playlists
                && Binder?.CurrentInput.PlaylistTree is { } tree && EntriesHold(tree, route)) return playlists.Id;
            if (doc.Layout == SidebarLayoutId.Library && doc.Find(SidebarSectionKind.Library) is { Hidden: false } library
                && Binder?.CurrentInput.Library is { } list && EntriesHold(list, route)) return library.Id;
            return null;
        }

        static bool ItemsHold(IReadOnlyList<string> items, string route)
        {
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i], route, StringComparison.Ordinal)) return true;
            return false;
        }

        static bool EntriesHold(IReadOnlyList<SidebarLibraryEntry> entries, string route)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (string.Equals(entry.Id, route, StringComparison.Ordinal) || SidebarRowResolve.EntrySelects(in entry, route)) return true;
            }
            return false;
        }

        /// <summary>The live selected route key. SUBSCRIBES the caller — only the pane's own render uses it.</summary>
        internal string SelectedRoute => RouteKeyOf(Shell.Current.Value);

        /// <summary>The selected route key WITHOUT subscribing (pane rows re-render on their own epoch).</summary>
        internal string SelectedRoutePeek => RouteKeyOf(Shell.Current.Peek());

        /// <summary>Does row <paramref name="index"/> draw SELECTED for <paramref name="route"/>? The ONE rule the sweep
        /// also uses (<see cref="SidebarRowResolve.SelectsRoute"/>).</summary>
        internal bool RowSelectsRoute(int index, string route)
        {
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return false;
            var row = rows[index];
            return SidebarRowResolve.SelectsRoute(in row, Plan.Entries, route)
                   || (index == _pillTarget.PlanIndex && _pillTarget.Anchor != SidebarPillAnchor.Row);
        }

        /// <summary>The ONE route sweep: bump the symmetric difference of the selected rows — the row that lost the
        /// pill and the row that gained it, nothing else.</summary>
        void RefreshSelection()
        {
            string route = SelectedRoute;
            _ = _planVersion.Value;
            ResolvePillTarget(route);
            var next = _rowSelNext;
            next.Clear();
            SidebarRowResolve.Sweep(Plan.Rows, Plan.Entries, route, next);
            // The one pill's row joins the selected set even when the sweep does not draw it selected (an ancestor or header anchor).
            if (_pillTarget.PlanIndex >= 0 && !next.Contains(_pillTarget.PlanIndex)) next.Add(_pillTarget.PlanIndex);
            var prev = _rowSelSet;
            var flipped = _rowSelFlip;
            flipped.Clear();
            SidebarRowResolve.Flipped(prev, next, flipped);
            // A route change that keeps the pill on the same non-row anchor (pl:a → pl:b in one collapsed folder) must still
            // re-render that anchor: its slot registers the pill under the live route.
            if (_pillTarget.PlanIndex >= 0 && _pillTarget.Anchor != SidebarPillAnchor.Row
                && !string.Equals(route, _pillRoute, StringComparison.Ordinal) && !flipped.Contains(_pillTarget.PlanIndex))
                flipped.Add(_pillTarget.PlanIndex);
            _pillRoute = route;
            for (int i = 0; i < flipped.Count; i++) BumpRowEpoch(flipped[i]);
            prev.Clear();
            for (int i = 0; i < next.Count; i++) prev.Add(next[i]);
        }

        // ── selection travel (NavigationView's paired 600 ms indicator flight) ─────────────────────────────────────

        void TrackSelection(string route)
        {
            if (string.Equals(route, _selRoute, StringComparison.Ordinal)) return;
            _prevSelRoute = _selRoute;
            _selRoute = route;
            _selEpoch++;
        }

        /// <summary>Bind a realized indicator node to the route it draws for; the reverse index makes it CHECKABLE after
        /// a recycle (a registration is removed only when the outgoing route still points back at THIS node).</summary>
        internal void RegisterSelectionPill(string route, NodeHandle node, SidebarPillLane lane)
        {
            var scene = Context.Scene;
            if (route.Length == 0 || scene is null || node.IsNull || !scene.IsLive(node)) return;
            int index = (int)node.Raw.Index;
            if (_selectionRouteByNode.TryGetValue(index, out var previous)
                && !string.Equals(previous, route, StringComparison.Ordinal)
                && _selectionPills.TryGetValue(previous, out var owned) && owned == node)
                _selectionPills.Remove(previous);
            _selectionRouteByNode[index] = route;
            _selectionLaneByNode[index] = lane;
            _selectionPills[route] = node;
        }

        SidebarPillLane LaneOf(NodeHandle node)
            => _selectionLaneByNode.TryGetValue((int)node.Raw.Index, out var lane) ? lane : SidebarPillLane.List;

        /// <summary>Would the pill's own bound read light this node? The transaction may only assert "visible" for a
        /// node this answers true for (#22/#23).</summary>
        bool PillDrawsSelected(NodeHandle node)
            => !node.IsNull
               && _selectionRouteByNode.TryGetValue((int)node.Raw.Index, out var route)
               && SidebarPillState.Lit(route, SelectedRoutePeek);

        void RunSelectionTransaction()
        {
            var scene = Context.Scene;
            var anim = Context.Anim;
            if (scene is null || anim is null) return;
            // Force-complete an interrupted pair; the visibility each half settles at is DERIVED, never a literal.
            if (!_selectionFlightFrom.IsNull && scene.IsLive(_selectionFlightFrom))
                NavigationSelectionMotion.SnapVertical(anim, _selectionFlightFrom, visible: PillDrawsSelected(_selectionFlightFrom));
            if (!_selectionFlightTo.IsNull && scene.IsLive(_selectionFlightTo))
                NavigationSelectionMotion.SnapVertical(anim, _selectionFlightTo, visible: PillDrawsSelected(_selectionFlightTo));
            _selectionFlightFrom = _selectionFlightTo = NodeHandle.Null;

            if (!TrySelectionPill(_selRoute, scene, out var incoming)) return;
            if (!TrySelectionPill(_prevSelRoute, scene, out var outgoing))
            {
                NavigationSelectionMotion.SnapVertical(anim, incoming, visible: true);
                return;
            }
            var from = scene.AbsoluteRect(outgoing);
            var to = scene.AbsoluteRect(incoming);
            float travel = to.Y - from.Y;
            if (!float.IsFinite(travel) || MathF.Abs(travel) <= 0.5f || outgoing == incoming)
            {
                NavigationSelectionMotion.SnapVertical(anim, outgoing, visible: false);
                NavigationSelectionMotion.SnapVertical(anim, incoming, visible: true);
                return;
            }
            // Same container and depth ⇒ the continuous worm; anything else — a depth change, or the pill moving between the
            // head, the list and the footer (design V.5) — scales out and in place, pivoting at the facing edges.
            bool sameLane = SidebarPillMotionRules.Slides(LaneOf(outgoing), LaneOf(incoming), to.X - from.X);
            NavigationSelectionMotion.StartVertical(anim, outgoing, 0f, travel, SelectionPill.PillH, outgoing: true, sameDepth: sameLane);
            NavigationSelectionMotion.StartVertical(anim, incoming, -travel, 0f, SelectionPill.PillH, outgoing: false, sameDepth: sameLane);
            _selectionFlightFrom = outgoing;
            _selectionFlightTo = incoming;
        }

        /// <summary>The realized indicator for a route — liveness is not enough: the reverse index must agree the node
        /// still draws this route (a recycled node is perfectly live).</summary>
        bool TrySelectionPill(string route, SceneStore scene, out NodeHandle node)
        {
            if (route.Length > 0 && _selectionPills.TryGetValue(route, out node)
                && !node.IsNull && scene.IsLive(node)
                && _selectionRouteByNode.TryGetValue((int)node.Raw.Index, out var bound)
                && string.Equals(bound, route, StringComparison.Ordinal)) return true;
            if (route.Length > 0) _selectionPills.Remove(route);
            node = NodeHandle.Null;
            return false;
        }

        // ── extents ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The analytic seed extent (NaN = measure). Called only when the host seeds/resizes — never per frame.</summary>
        float RowExtentSeed(int index)
        {
            if (!_planPublished) return SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine);
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine);
            return SidebarRowExtents.HeightOf(rows, index, SectionOf(rows[index].SectionId));
        }

        void ReseedRowExtents()
        {
            if (_rowLayout.CustomLayout is MeasuredStackVirtualLayout measured) measured.Reseed(Plan.Rows.Count);
        }

        /// <summary>The row's LIVE laid-out height (bound-safe: one peek + one rect read), 44 on any degenerate answer.</summary>
        internal float RowExtentOf(int index)
        {
            if ((uint)index >= (uint)Plan.Rows.Count) return SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine);
            float cross = MathF.Max(1f, Sidebar.Width.Peek() - PaneMetrics.PaneInsetH);
            var layout = _rowLayout.CustomLayout;
            if (layout is null) return SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine);
            float extent = layout.ItemRect(index, cross).H;
            return float.IsFinite(extent) && extent > 0f ? extent : SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine);
        }

        /// <summary>The normalized query that built the published plan (pane head, or V3's mode-global query).</summary>
        internal string SearchText => _effectiveSearch;

        internal SidebarSection? SectionOf(string sectionId) => _sections.TryGetValue(sectionId, out var s) ? s : null;

        internal bool TryBandOf(int planIndex, out PaneBand band)
        {
            for (int i = 0; i < _bands.Count; i++)
                if (_bands[i].Contains(planIndex)) { band = _bands[i]; return true; }
            band = default;
            return false;
        }

        /// <summary>One Reorderable per in-place section, kept for the component's life. LiveProject off (a mid-drag
        /// projection would swap content under the lifted node), no insertion line (mixed pitches), and a Stationary
        /// lift at opacity 0 — the vacated slot IS the gap, the chip is the ONE moving visual.</summary>
        internal Reorderable ReorderFor(string sectionId)
        {
            if (_reorder.TryGetValue(sectionId, out var ro)) return ro;
            ro = new Reorderable(Drag.Resource)
            {
                LiveProject = false,
                ShowInsertionLine = false,
                DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = 0f },
            };
            _reorder[sectionId] = ro;
            return ro;
        }

        internal static LayoutTransition Placement => RowPlacement;

        // ── reorder wiring ─────────────────────────────────────────────────────────────────────────────────────────

        void ConfigureReorder()
        {
            for (int i = 0; i < _bands.Count; i++)
            {
                var band = _bands[i];
                if (SectionOf(band.SectionId) is null) continue;
                var ro = ReorderFor(band.SectionId);
                ro.Scene = Context.Scene;
                ro.RequestRender = BumpDisplacement;
                ro.ItemCount = band.Count;
                ro.ItemExtent = band.Extent;
                ro.Spacing = 0f;
                string id = band.SectionId;
                ro.ItemOf = slot => PayloadAt(id, slot);
                ro.OnReorder = (from, to) => CommitReorder(id, from, to);
                ro.OnCrossCommit = (payload, _, _, _, slot) => AcceptForeign(id, payload, slot);
            }
        }

        void BumpDisplacement()
        {
            _dispVersion.Value = _dispVersion.Peek() + 1;
            Context.RequestRerender();
        }

        /// <summary>A displaced row's FLIP start (the key-matched publish's seeds), only on the bump that carries them.</summary>
        (float dx, float dy)? DedupeFlipFrom(int planIndex)
            => _dedupeSeedsVer == _dispVersion.Peek() && _dedupeSeeds.TryGetValue(planIndex, out var seed) && seed.Dy != 0f
                ? (0f, seed.Dy) : null;

        /// <summary>A row the publish inserted outside the reveal band eases in from transparent.</summary>
        (float from, float delayMs)? DedupeFadeFrom(int planIndex)
            => _dedupeSeedsVer == _dispVersion.Peek() && _dedupeSeeds.TryGetValue(planIndex, out var seed) && seed.Fade
                ? (0f, 0f) : null;

        /// <summary>The ItemsView displacement channel in plan-row space. Stable delegate (ListOptions freeze at mount).</summary>
        (float dx, float dy) Displacement(int planIndex)
        {
            if (!TryBandOf(planIndex, out var band)) return (0f, 0f);
            var ro = ReorderFor(band.SectionId);
            if (!ro.IsLifted) return (0f, 0f);
            int slot = planIndex - band.Start;
            int from = ro.LiftedIndex;
            int shown = ro.TargetIndex;
            int reachable = ClampReorderSlot(band.SectionId, from, shown);
            if (reachable == shown) return (0f, ro.OffsetFor(slot));
            return (0f, SidebarReorderClamp.Offset(slot, from, reachable, band.Extent));
        }

        /// <summary>The ONE clamp chokepoint for both the gap and the commit, so the gap never opens where the drop
        /// will not land.</summary>
        int ClampReorderSlot(string sectionId, int from, int to)
        {
            if (Config.ClampReorderSlot is not { } clamp || from < 0 || to < 0) return to;
            return SectionOf(sectionId) is { } section ? clamp(section.Kind, from, to) : to;
        }

        object? PayloadAt(string sectionId, int slot)
        {
            var band = BandFor(sectionId);
            if (band.Count == 0) return null;
            var rows = Plan.Rows;
            int index = band.Start + slot;
            if ((uint)index >= (uint)rows.Count) return null;
            var row = rows[index];
            if (row.EntryIndex >= 0 && row.EntryIndex < Plan.Entries.Count)
            {
                var e = Plan.Entries[row.EntryIndex];
                return PayloadOf(in e, e.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder);
            }
            // A hand-placed route row: its key is its identity and its pin id.
            return PayloadOfRoute(row.Key, Shell.Dest(Shell.Parse(row.Key)).Title);
        }

        PaneBand BandFor(string sectionId)
        {
            for (int i = 0; i < _bands.Count; i++)
                if (string.Equals(_bands[i].SectionId, sectionId, StringComparison.Ordinal)) return _bands[i];
            return default;
        }

        void CommitReorder(string sectionId, int from, int to)
        {
            var section = SectionOf(sectionId);
            if (section is null) return;
            to = ClampReorderSlot(sectionId, from, to);
            if (from == to) return;
            _publishThroughFreeze = true;
            var band = BandFor(sectionId);
            var ctx = new PaneReorder(section, from, to, band.Count, slot => KeyAt(sectionId, slot));
            if (Config.CommitReorder is { } commit) commit(ctx);
            else DefaultReorderCommit(in ctx);
        }

        string KeyAt(string sectionId, int slot)
        {
            var band = BandFor(sectionId);
            if (band.Count == 0) return "";
            var rows = Plan.Rows;
            int index = band.Start + slot;
            return (uint)index < (uint)rows.Count ? rows[index].Key : "";
        }

        /// <summary>A FOREIGN entity dropped onto a reorderable band: only Pinned accepts one.</summary>
        void AcceptForeign(string sectionId, object? payload, int slot)
        {
            if (SectionOf(sectionId) is not { Kind: SidebarSectionKind.Pinned }) return;
            AcceptPinDrop(payload, slot);
        }

        /// <summary>Drop-to-pin (the band or the empty drop zone). An already-pinned payload is a MOVE, never a duplicate.</summary>
        internal void AcceptPinDrop(object? payload, int slot)
        {
            if (Drag.Unwrap(payload) is not { CanPin: true } p) return;
            string? pinId = SidebarPinId.Canonical(p.Id) ?? SidebarPinId.Canonical(p.Uri);
            if (pinId is null) return;
            int at = Pins.IndexOf(pinId);
            if (at >= 0)
            {
                MovePinRecorded(at, slot > at ? slot - 1 : slot);   // remove-then-insert shifts later indices down by one
                return;
            }
            PinWithToast(pinId, SidebarPinId.KindOf(pinId), SidebarPinId.UriOf(pinId), p.Name, slot);
        }

        // ── the tree multi-selection ───────────────────────────────────────────────────────────────────────────────

        internal IReadOnlyList<string> TreeVisibleOrder => _treeVisibleOrder;

        void RebuildTreeSelectionOrder(SidebarRowPlan plan)
        {
            _treeVisibleOrder.Clear();
            var rows = plan.Rows;
            var entries = plan.Entries;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader)) continue;
                if (SectionOf(row.SectionId)?.Kind is not (SidebarSectionKind.Playlists or SidebarSectionKind.Library)) continue;
                if ((uint)row.EntryIndex >= (uint)entries.Count) continue;
                var entry = entries[row.EntryIndex];
                if (entry.Kind is not (SidebarEntryKind.Playlist or SidebarEntryKind.Folder) || entry.Id.Length == 0) continue;
                _treeVisibleOrder.Add(entry.Id);
            }
        }

        /// <summary>THE ONE selection write: bump the UNION of before/after rows (a row's drag payload is "the whole
        /// selection when I am in it"), or every tree row when the lane's visibility flipped (a shape change).</summary>
        void MutateSelection(Func<bool> mutate)
        {
            _selBefore.Clear();
            foreach (string id in TreeSelection.Ids) _selBefore.Add(id);
            bool laneBefore = TreeSelection.CheckLaneVisible;
            if (!mutate()) return;
            bool laneAfter = TreeSelection.CheckLaneVisible;
            if (laneBefore != laneAfter) BumpTreeRowEpochs(null);
            else
            {
                foreach (string id in _selBefore) BumpTreeRowEpochs(id);
                foreach (string id in TreeSelection.Ids)
                    if (!_selBefore.Contains(id)) BumpTreeRowEpochs(id);
            }
            _selBefore.Clear();
            if (ChecksVisible.Peek() != laneAfter) ChecksVisible.Value = laneAfter;
            SelectionVersion.Value = SelectionVersion.Peek() + 1;
        }

        void BumpTreeRowEpochs(string? entryId)
        {
            var rows = Plan.Rows;
            var entries = Plan.Entries;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader)) continue;
                if ((uint)row.EntryIndex >= (uint)entries.Count) continue;
                if (entryId is null || string.Equals(entries[row.EntryIndex].Id, entryId, StringComparison.Ordinal))
                {
                    BumpRowEpoch(i);
                    if (entryId is not null) return;
                }
            }
        }

        /// <summary>A plain click clears the selection and runs the row's verb (WinUI Extended); Ctrl/Shift select.</summary>
        internal void ActivateTreeRow(string entryId, KeyModifiers mods, Action? plain)
        {
            bool ctrl = (mods & KeyModifiers.Ctrl) != 0;
            bool shift = (mods & KeyModifiers.Shift) != 0;
            if (!ctrl && !shift)
            {
                MutateSelection(TreeSelection.Clear);
                plain?.Invoke();
                return;
            }
            MutateSelection(() => TreeSelection.Interact(entryId, ctrl, shift, _treeVisibleOrder));
        }

        internal void ToggleTreeSelection(string entryId) => MutateSelection(() => TreeSelection.Toggle(entryId));

        internal void ClearTreeSelection() => MutateSelection(TreeSelection.Clear);

        /// <summary>The row menu's Select: turn the lane on AND put this row in it.</summary>
        internal void BeginTreeCheckMode(string entryId) => MutateSelection(() =>
        {
            bool changed = TreeSelection.SetCheckMode(true);
            return TreeSelection.Toggle(entryId) || changed;
        });

        internal IReadOnlyList<string> OrderedTreeSelection() => TreeSelection.Ordered(_treeVisibleOrder);

        /// <summary>Dragging a row IN the selection lifts the whole (normalised) selection; a row outside it lifts only
        /// itself — silently substituting five items for the one under the cursor moves things nobody aimed at.</summary>
        internal DragPayload TreeDragPayload(in SidebarLibraryEntry entry)
        {
            if (TreeSelection.Count >= 2 && TreeSelection.Contains(entry.Id))
            {
                var ordered = RootlistSelection.Normalize(RootlistTree, TreeSelection.Ids);
                if (ordered.Count >= 2)
                {
                    var refs = new RootRef[ordered.Count];
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        var r = RootlistTreeNav.RefOf(ordered[i]);
                        refs[i] = new RootRef(r.Key, r.IsFolder);
                    }
                    var first = ordered[0];
                    return PayloadOf(in first, rootlistItem: true) with { RootlistItems = refs };
                }
            }
            return PayloadOf(in entry, rootlistItem: true);
        }

        // ── drag peek ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Dwell before a collapsed pane peeks open — shorter than a folder's 500 ms: it only reveals labels.</summary>
        internal const float DragPeekMs = 250f;

        internal void SetDragPeek(bool on)
        {
            if (_dragPeek.Peek() != on) _dragPeek.Value = on;
            // …and the shell, which CLIPS the column: without the mirror the peeked rows lay out expanded inside the 48-DIP rail.
            if (DragPeek.Peek() != on) DragPeek.Value = on;
        }

        /// <summary>Disarm every row's cue on SESSION END (a drop outside the pane never reaches a row's OnLeave).</summary>
        internal void ClearDropSlot()
        {
            if (_dropSlot.Peek().PlanIndex >= 0) _dropSlot.Value = SidebarDropSlot.None;
            if (_railDropUri.Peek() is not null) _railDropUri.Value = null;
        }

        /// <summary>Owns the ONE UseDragState() subscription that ends a peek, disarms the cue and flushes the freeze —
        /// on SESSION END (drop, cancel and Escape alike), in a layout effect keyed on the active EDGE.</summary>
        sealed class DragPeekWatcher(PaneView owner) : Component
        {
            public override Element Render()
            {
                bool active = UseDragState().Active;
                // The Pinned band's cue follows the drag: armed while a pinnable drag is live, so it appears and leaves with it.
                UseLayoutEffect(() => { owner._pinDropArmed.SetIfChanged(Drag.LivePinnable()); }, active ? 1 : 0);
                UseLayoutEffect(() =>
                {
                    if (active) return;
                    owner.SetDragPeek(false);
                    owner.ClearDropSlot();
                    owner.FlushDeferredStage();
                }, active ? 1 : 0);
                UseEffect(() => (Action?)(() => owner.DiscardDeferredStage()), DepKey.Empty);
                return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
            }
        }

        // ── commands the rows raise ────────────────────────────────────────────────────────────────────────────────

        Action<string, bool>? _benchToggle;

        /// <summary>The frame bench's section handle (Diagnostics.BenchHooks): this pane's, while it is mounted.</summary>
        Action? InstallBenchHook()
        {
            var toggle = _benchToggle ??= ToggleSection;
            Diagnostics.BenchHooks.ToggleSidebarSection = toggle;
            return () => { if (ReferenceEquals(Diagnostics.BenchHooks.ToggleSidebarSection, toggle)) Diagnostics.BenchHooks.ToggleSidebarSection = null; };
        }

        /// <summary>Collapse/expand through the disclosure channel, which choreographs it and then commits the layout's one
        /// command. A New releases section that opens is seen (its badge clears).</summary>
        internal void ToggleSection(string sectionId, bool collapsed)
        {
            // A collapse writes the persisted bit only at rest, so the header's `open` is stale mid-flight: a click on a
            // section in flight flips ITS direction (a reverse), whatever the persisted state says.
            if (_disclosures.TryGet("section:" + sectionId, out var live)) collapsed = live.Open;
            StartDisclosure("section:" + sectionId, sectionId, folder: false, open: !collapsed, () =>
            {
                _choreographed.Add(sectionId);   // the publish this commit causes takes the key-matched path
                Sidebar.Dispatch(new SetSectionCollapsed(Config.Layout, sectionId, collapsed));
                if (!collapsed && sectionId == SidebarCatalogue.IdOf(SidebarSectionKind.NewReleases)) Sidebar.MarkNewReleasesSeen();
            });
        }

        /// <summary>A header menu's Collapse or Expand: the same choreography as a click, toward the menu's own direction. The
        /// menu's label comes from the persisted state, which a collapse writes only at rest, so a pick that names the way
        /// the section's disclosure is already heading is a no-op (it must not reverse that disclosure).</summary>
        internal void SetSectionDirection(string sectionId, bool collapsed)
        {
            if (_disclosures.TryGet("section:" + sectionId, out var live) && live.Open == !collapsed) return;
            ToggleSection(sectionId, collapsed);
        }

        /// <summary>A folder activation through the mode seam, structurally animated only when it discloses inline.</summary>
        internal void ActivateFolder(string folderId, string name, int planIndex)
        {
            if (folderId.Length == 0) return;
            Action commit = Config.ActivateFolder is { } activate
                ? () => activate(folderId, name)
                : () => ToggleFolder(folderId);
            if (!(Config.DisclosesFoldersInline?.Invoke() ?? true)) { commit(); return; }
            _ = planIndex;
            // A folder in flight reverses from the disclosure's own direction: its saved state moves only at rest.
            bool open = _disclosures.TryGet("folder:" + folderId, out var live) ? !live.Open : !IsFolderExpanded(folderId);
            StartDisclosure("folder:" + folderId, folderId, folder: true, open, commit);
        }

        /// <summary>The folder menu's Expand verb from a compact tile's flyout or a row menu: show the full pane (the overlay
        /// in Narrow and Tiny), then disclose the folder there.</summary>
        internal void ExpandFolderInPane(string folderId)
        {
            if (folderId.Length == 0) return;
            CloseFlyout();
            OpenPane();
            SetFolderExpanded(folderId, true);
        }

        // ── navigation, playback, payloads ─────────────────────────────────────────────────────────────────────────

        internal void Navigate(string routeKey, string? arg)
        {
            if (routeKey.Length == 0) return;
            Shell.GoTo(Shell.Parse(routeKey, arg));
        }

        /// <summary>0.2.9 stashed a detail preview here so the header painted on frame one; in 0.3 the handle IS the
        /// shared row the destination reads, so the outcome is by construction (ch 26 §7.3 SidebarNavPreview).</summary>
        internal void Navigate(string routeKey, string? arg, in SidebarLibraryEntry entry) => Navigate(routeKey, arg);

        internal void PlayTrack(string uri) => Play(uri, asTrack: true);

        /// <summary>A track row plays instead of navigating; a card's play button plays the entity as a CONTEXT.</summary>
        internal void Play(string uri, bool asTrack)
        {
            if (uri.Length == 0) return;
            var target = EntityUri.Parse(uri);
            if (!target.IsValid) return;
            if (Acts?.Play is { } play) play(target);
            else Shell.OnPlayContext?.Invoke(target);
        }

        /// <summary>The mode's create verb, else the ONE shared flow (Library V3 supplies none and calls this directly
        /// from its own header/rail "+").</summary>
        internal void CreatePlaylist() => (Config.OnCreatePlaylist ?? CreatePlaylistFlow)();

        /// <summary>Is the header "+" the armed drop destination (one per pane — one PlaylistTree header on screen)?</summary>
        internal readonly Signal<bool> HeaderCreateDropActive = new(false);

        /// <summary>The ONE create-playlist verb every "+" shares (numbered name, optimistic row, navigate) — the
        /// library seam's. An absent seam is never a half-made playlist and never a silent click either: it says why
        /// (G-170).</summary>
        internal static void CreatePlaylistFlow()
        {
            if (LibraryWrites?.CreatePlaylist is { } create) create(null, true);
            else RefuseWrite("create playlist");
        }

        /// <summary>The "never a silent no-op" arm of every library write the pane issues (a "+" click, a create drop,
        /// a folder verb): a log line for us, the refusal's sentence for the user. Static — a click verb has no pane
        /// instance to hand (Classic's rail "+" is a static factory).</summary>
        internal static void RefuseWrite(string verb)
        {
            Log.Warn("sidebar", "library write refused: no seam for " + verb);
            if (RefusalSentence(SidebarDropRefusal.WritesUnavailable) is { Length: > 0 } sentence)
                Notify.Say(sentence, InfoBarSeverity.Informational, dedupeKey: "sidebar.writes-unavailable");
        }

        /// <summary>A projection entry as the drag payload every destination reads. Tracks resolve through the library
        /// seam, lazily, after a compatible drop (the payload factory is cold by contract).</summary>
        internal static DragPayload PayloadOf(in SidebarLibraryEntry entry, bool rootlistItem)
        {
            var kind = entry.Kind switch
            {
                SidebarEntryKind.Playlist => DragKind.Playlist,
                SidebarEntryKind.Folder => DragKind.Folder,
                SidebarEntryKind.Album => DragKind.Album,
                SidebarEntryKind.Artist => DragKind.Artist,
                SidebarEntryKind.Show => DragKind.Show,
                SidebarEntryKind.Track => DragKind.Track,
                _ => DragKind.Route,
            };
            string uri = entry.Kind == SidebarEntryKind.AppRoute ? SidebarPinId.UriOf(entry.Id) : entry.Uri;
            Func<CancellationToken, Task<Track[]>>? resolver = null;
            if (uri.Length > 0 && kind is DragKind.Playlist or DragKind.Album or DragKind.Show or DragKind.Track
                && LibraryWrites?.ResolveTracks is { } resolve)
                resolver = ct => resolve(uri, ct);
            // A FOLDER travels as its wire uri (`spotify:folder:<hex>`), one of the two spellings `Drag.FolderKey`
            // reduces to the group id — so its RootRefs, the self check and the marker stream all address the same hex.
            // The entry id ("folder:<hex>") is neither spelling; `SidebarPinId.Canonical` maps the uri back for a pin drop.
            string id = kind == DragKind.Folder && entry.FolderId.Length > 0 ? EntityUri.FolderPrefix + entry.FolderId : entry.Id;
            return new DragPayload(kind, id, uri, entry.Name,
                TrackResolver: resolver,
                RootlistItem: rootlistItem && kind is DragKind.Playlist or DragKind.Folder,
                ArtUrl: entry.Cover.IsEmpty ? null : Controls.ArtUrl(entry.Cover));
        }

        /// <summary>A hand-placed ROUTE row as a pin payload, or null when the route is not a durable destination.</summary>
        internal static DragPayload? PayloadOfRoute(string routeKey, string title)
        {
            if (SidebarPinId.FromRoute(routeKey) is not { } pinId) return null;
            string uri = SidebarPinId.UriOf(pinId);
            var kind = uri.Length > 0 ? Drag.KindOfUri(uri) : DragKind.Route;
            return new DragPayload(kind, pinId, uri, title);
        }

        // ── the facts the drop/menu halves read ────────────────────────────────────────────────────────────────────

        /// <summary>A row's content width (bound-safe signal read).</summary>
        internal float ContentWidth => MathF.Max(0f, Sidebar.Width.Value - PaneMetrics.PaneInsetH);

        internal bool TreeSortedNonCustom => Config.TreeSortedNonCustom?.Invoke() ?? false;

        /// <summary>"Not known to be loaded" never presents as "is": a filing against an empty tree lands nowhere.</summary>
        internal bool RootlistLoaded
            => Binder?.CurrentInput is { TreeState: SidebarSourceState.Ready, PlaylistTree.Count: > 0 };

        /// <summary>The FULL flattened tree the projection publishes — structure is decided here, never on the plan.</summary>
        internal IReadOnlyList<SidebarLibraryEntry>? RootlistTree => Binder?.CurrentInput.PlaylistTree;

        /// <summary>THE marker stream every legality question is asked against, derived from the published tree
        /// (<see cref="RootlistMarkerStream"/>) and cached per projection revision.</summary>
        internal IReadOnlyList<RootlistEntry>? RootlistMarkers
        {
            get
            {
                var binder = Binder;
                var tree = binder?.CurrentInput.PlaylistTree;
                if (binder is null || tree is null || tree.Count == 0) return null;
                if (_markerRevision != binder.Revision)
                {
                    RootlistMarkerStream.Build(tree, _markers);
                    _markerRevision = binder.Revision;
                }
                return _markers;
            }
        }

        /// <summary>The slot this row publishes (bound-safe: one signal read, two int compares).</summary>
        internal SidebarDropSlot DropSlotFor(int planIndex)
        {
            var cue = _dropSlot.Value;
            return planIndex >= 0 && cue.PlanIndex == planIndex ? cue : SidebarDropSlot.None;
        }

        /// <summary>Is this rail tile the armed drop destination (bound-safe)?</summary>
        internal bool IsRailDropActive(string key) => string.Equals(_railDropUri.Value, key, StringComparison.Ordinal);
    }

    /// <summary>THE PANE'S PUBLISH DIFF (W3-A2): which realized rows a republish must re-skin, and whether a plan moved at
    /// all. The shape rule is <see cref="SidebarRowDiff"/>'s — a row is new at its slot, its record differs, or the entry
    /// it addresses differs — with ONE correction: the entry compare is <see cref="SidebarEntriesShadow.SameEntry"/>,
    /// whose <c>MosaicTiles</c> leg is BY VALUE. The projection materialises a folder's (and a cover-less playlist's)
    /// tile list fresh on every rebuild, so the record's compiler equality — a REFERENCE compare on that one member — read
    /// every folder row and every mosaic row as changed on every re-plan that followed a rebuild: two slots, a disclosure
    /// chevron and a selection pill re-rendered per publication with nothing about them moved (the <c>PaneSlot×2</c> /
    /// <c>Chevron×1</c> / <c>SelectionPill×1</c> lines of the scroll census). Public so the rule is pinned by facts
    /// (<c>SidebarWiringTests</c>); allocation-free.</summary>
    public static class PlanDiff
    {
        /// <summary>Does row <paramref name="index"/> of the new plan render differently from the same slot of the old?</summary>
        public static bool RowChanged(
            IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
            IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries,
            int index)
        {
            if ((uint)index >= (uint)newRows.Count) return false;
            if (index >= oldRows.Count) return true;              // the row is new at this slot
            var row = newRows[index];
            if (!row.Equals(oldRows[index])) return true;
            int entry = row.EntryIndex;
            if (entry < 0) return false;                          // a header / divider / skeleton carries no entry
            bool inOld = entry < oldEntries.Count;
            bool inNew = entry < newEntries.Count;
            if (inOld != inNew) return true;
            return inNew && !SidebarEntriesShadow.SameEntry(oldEntries[entry], newEntries[entry]);
        }

        /// <summary>The ORDERED EDIT SCRIPT from <paramref name="oldRows"/> to <paramref name="newRows"/>, matched on
        /// (SectionId, Key): a two-pointer walk with set lookups. A row only in the old list is removed, a row only in the
        /// new list is inserted, and a key that CHANGES section is a remove plus an insert (it is a different row). The
        /// splices ascend, and each <c>At</c> is in the index space AFTER the earlier splices, so applying them in order to
        /// a layout's extent table (<c>ISplicingVirtualLayout.Splice</c>) turns the old table into the new count with every
        /// surviving row keeping its measured extent. A pair of rows present in both lists but out of order (a reorder)
        /// is also a remove plus an insert, so the script is always count-correct.</summary>
        public static List<(int At, int Removed, int Inserted)> Splices(IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarRow> newRows)
        {
            ArgumentNullException.ThrowIfNull(oldRows);
            ArgumentNullException.ThrowIfNull(newRows);
            var result = new List<(int At, int Removed, int Inserted)>(4);
            var inOld = new HashSet<(string, string)>(oldRows.Count);
            var inNew = new HashSet<(string, string)>(newRows.Count);
            for (int i = 0; i < oldRows.Count; i++) inOld.Add((oldRows[i].SectionId, oldRows[i].Key));
            for (int j = 0; j < newRows.Count; j++) inNew.Add((newRows[j].SectionId, newRows[j].Key));

            int a = 0, b = 0;      // the walk's cursors into the old and the new list
            int removed = 0, inserted = 0, editAt = 0;
            void Flush()
            {
                if (removed > 0 || inserted > 0) result.Add((editAt, removed, inserted));
                removed = inserted = 0;
            }
            while (a < oldRows.Count || b < newRows.Count)
            {
                if (a < oldRows.Count && b < newRows.Count
                    && string.Equals(oldRows[a].Key, newRows[b].Key, StringComparison.Ordinal)
                    && string.Equals(oldRows[a].SectionId, newRows[b].SectionId, StringComparison.Ordinal))
                {
                    Flush();
                    a++; b++;
                    continue;
                }
                if (removed == 0 && inserted == 0) editAt = b;   // b IS the index in the after-earlier-splices space
                var oldKey = a < oldRows.Count ? (oldRows[a].SectionId, oldRows[a].Key) : default;
                var newKey = b < newRows.Count ? (newRows[b].SectionId, newRows[b].Key) : default;
                bool oldGone = a < oldRows.Count && !inNew.Contains(oldKey);
                bool newFresh = b < newRows.Count && !inOld.Contains(newKey);
                if (a >= oldRows.Count) { inserted++; b++; }
                else if (b >= newRows.Count) { removed++; a++; }
                else if (oldGone) { removed++; a++; }
                else if (newFresh) { inserted++; b++; }
                else
                {
                    // Both exist on the other side but not at this position (a reorder): drop both from the lookups so
                    // their later partners read as plain edits instead of cascading mismatches.
                    inNew.Remove(newKey); inOld.Remove(oldKey);
                    removed++; a++; inserted++; b++;
                }
            }
            Flush();
            return result;
        }

        /// <summary>Did the plan move at all — a different row count, or any slot <see cref="RowChanged"/>? The rail's
        /// version gate: a re-plan that reproduced the same rail rows over the same entries bumps nothing.</summary>
        public static bool Changed(
            IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
            IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries)
        {
            if (oldRows.Count != newRows.Count) return true;
            for (int i = 0; i < newRows.Count; i++)
                if (RowChanged(oldRows, oldEntries, newRows, newEntries, i)) return true;
            return false;
        }
    }
}
