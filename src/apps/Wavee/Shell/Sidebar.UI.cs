// ── Shell/Sidebar.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the pane and the three designs (Classic / Library V3 / Wavee Curated), the collapsed rail, folder flyout, tree
// drag cues, multi-select, the "Move to folder…" picker, the section options popover
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 7500 lines (this file + its named partials — see below)
// Spec: ch 26 §9.4 · ch 25 (the pane) · ch 26 W21/W22 (what the pipeline and a bound action draw)
//
// THE ONE RENDERER. Classic, Library V3 and Curated are a document + a `PaneConfig` over ONE `PaneView` (ch 25 §0.1);
// the renderer never branches on the design. This file holds the mount points, the design host (a design switch is a
// Key remount), the Classic and Curated mode shells, the config/metrics vocabulary and the pane's core: plan →
// publish → per-row epochs, the route and now-playing sweeps, the NavigationView pill transaction, disclosure
// choreography, reorder bands, the tree multi-selection, drag peek and the canvas commands.
//
// NAMED PARTIALS (the J1 split — each carries its own header): `Sidebar.UI.Drop.cs` (every drop spec + the rootlist
// slot commit), `Sidebar.UI.Menus.cs` (row menus, the quick layout menu, "Move to folder…", the options popover),
// `Sidebar.UI.Slot.cs` (the bound row slot), `Sidebar.UI.Rows.cs` (row primitives), `Sidebar.UI.Rail.cs` (the 56-DIP
// rail + folder flyout), `Sidebar.UI.LibraryV3.cs` (Library V3's mode, session and fixed chrome).
//
// INSIDE `Sidebar`, `Design` is the design SIGNAL (Sidebar.Host.cs) — every token read is `global::Wavee.Design.*`.

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

    /// <summary>The docked sidebar: the active design's pane (expanded layer + 56-DIP rail, both always mounted). Reads
    /// its own state — <see cref="Design"/>, <see cref="Width"/>, <see cref="Collapsed"/>, <see cref="DragPeek"/>,
    /// <c>Shell.Current</c> — so the shell mounts it with no arguments and binds the COLUMN width itself
    /// (<c>Collapsed ∧ ¬DragPeek ? 56 : Width</c>, the 300 ms Reveal, the IsolateLayout firewall).</summary>
    // MOUNT POINT (stage B contract)
    public static Element Pane() => Embed.Comp(static () => new PaneHost(inDrawer: false));

    /// <summary>The narrow-shell drawer's mount (viewport ≤ 720): the SAME host, always expanded, no rail layer, its
    /// own scroll key — a second independent mount, never a shared instance (ch 25 W20).</summary>
    // MOUNT POINT (stage B contract)
    public static Element DrawerPane() => Embed.Comp(static () => new PaneHost(inDrawer: true));

    // ══ 2. THE DESIGN HOST + THE BINDER PUMP ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Bumped once, when the projection binder first builds. The pane folds it into its plan key so a pane
    /// that rendered before the first projection re-plans the moment one exists (the binder is a plain service, so its
    /// first publish is otherwise invisible to a render that saw <c>Revision == 0</c>).</summary>
    static readonly Signal<int> s_binderEpoch = new(0);

    /// <summary>The ONE mount seam (0.2.9 <c>SidebarHost</c>): re-renders only on a design switch and mounts that design
    /// under <see cref="MountKey"/>, so a switch REMOUNTS (fresh hooks, section and scroll state) with a 150 ms fade.</summary>
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

            // The ONLY signal this body reads: a width, filter or collapse change never re-renders the host.
            var design = Sidebar.Design.Value;
            Element mode = design switch
            {
                SidebarDesign.LibraryV3 => Embed.Comp(() => new LibraryV3Mode(_inDrawer)),
                SidebarDesign.Curated => Embed.Comp(() => new CuratedMode(_inDrawer)),
                _ => Embed.Comp(() => new ClassicMode(_inDrawer)),
            };
            return new BoxEl
            {
                Grow = 1f, Direction = 1,
                // Key is mandatory: without it two mode components that share a type would reuse hooks across a
                // switch. A quick fade only — the shell's width transition owns spatial motion.
                Children = [mode with
                {
                    Key = MountKey(design),
                    Enter = new EnterExit(Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlFast,
                }],
            };
        }
    }

    /// <summary>The projection binder, built once per process. 0.2.9 mounted a component pump at the app root; in 0.3
    /// the binder is a plain service whose <c>Sync()</c> is idempotent (one trigger fold), so both pane mounts may pump
    /// it. The two feed seams are read HERE, once — which is why their owners install them before the first mount.</summary>
    static SidebarProjectionBinder EnsureBinder()
    {
        if (Binder is { } existing) return existing;
        var binder = new SidebarProjectionBinder();
        var table = WaveeBuiltInDataSources.RegisterAll(binder, NewReleasesFetch, ConcertsFetch);
        binder.UseHost(new WaveeBuiltInDataSources.ContributionHost(table), table);
        WaveeBuiltInDataSources.Attach(table, s_sourcePost, binder.OnSourceChanged);
        Binder = binder;
        return binder;
    }

    /// <summary>The marshaller handed to the async sources: an INDIRECTION through <see cref="ToUi"/>, so an
    /// <see cref="Activate"/> that lands after the binder exists (a shell remount) still reaches every source.</summary>
    static readonly Action<Action> s_sourcePost = static a => ToUi(a);

    /// <summary>The binder's subscription, as ONE signal effect: every table/edge the projection or a feed reads, the
    /// two shell logs, playback, and every preference that reshapes the pass. The binder itself only Peeks (it is not a
    /// computation), so this is what makes a hydrated playlist title, a rootlist push, a navigation, a play or a V3 filter
    /// change reach the pane. It SYNCS, never invalidates: the binder's gate folds the row versions of exactly the rows
    /// the sidebar shows, so a wake from a table change elsewhere in the app costs one fold, not a three-pass rebuild
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
            // The feeds (G-173): queue and now playing, their track/episode rows, top tracks, concerts.
            _ = edges.Queue.Changed.Value;
            _ = edges.TrackArtists.Changed.Value;
            _ = edges.ArtistPopular.Changed.Value;
            _ = edges.FeedSection.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Episodes.Changed.Value;
            _ = scope.Concerts.Changed.Value;
        }
        _ = Playback.Current.Value;
        // The recency feeds and the Recents sort (G-172).
        _ = Shell.History.Store.Version.Value;
        _ = Shell.PlayLog.Version.Value;
        _ = PinsVersion.Value;
        _ = LayoutVersion.Value;
        _ = FolderVersion.Value;
        _ = V3OrderVersion.Value;
        _ = Sidebar.Design.Value;
        _ = V3Filter.Value;
        _ = V3Qualifier.Value;
        _ = V3Sort.Value;
        _ = V3Desc.Value;
        _ = V3Search.Value;

        bool first = binder.Revision == 0;
        if (first) binder.Start();
        else binder.Sync();
        if (first) s_binderEpoch.Value = s_binderEpoch.Peek() + 1;
    }

    // ══ 3. THE CLASSIC AND CURATED MODE SHELLS ═════════════════════════════════════════════════════════════════════════

    /// <summary>Classic: a LOCKED built-in document rebuilt from three persisted section flags, read-only, with the rail
    /// "+" appended by the pane (a rail plan is tiles-from-sections and cannot express authored chrome).</summary>
    internal sealed class ClassicMode : Component
    {
        readonly bool _inDrawer;
        // A freshly-minted document per render defeats the pane's wholesale reference test (every publish would re-skin
        // the whole realized window), so the build is cached on the flags + band reference.
        readonly ClassicDocumentCache _docCache = new();

        public ClassicMode(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            var config = UseMemo(() => new PaneConfig
            {
                Design = SidebarDesign.Classic,
                ScrollKeyPrefix = "sidebar.classic",
                Document = BuildDocument,
                ModeEpoch = SectionEpoch,
                SetSectionCollapsed = SetSection,
                ReadOnly = true,
                SearchHead = false,
                OnCreatePlaylist = PaneView.CreatePlaylistFlow,
                HeaderCreate = true,
                RailFooter = static () => Embed.Comp(static () => new CreateButton(
                    PaneView.CreatePlaylistFlow, menu: PaneView.CreateRootMenu, box: Rail.Box, glyph: 16f)),
            }, DepKey.Empty);
            return Embed.Comp(() => new PaneView(config, _inDrawer));
        }

        /// <summary>The three flag reads are UNCONDITIONAL and first — they are the pane's subscription (this runs inside
        /// the pane's render); only the build is cached. The dev-tools section is gone with the API console (Q7).</summary>
        SidebarCustomLayout BuildDocument()
        {
            bool pinnedOpen = ClassicPinnedOpen.Value;
            bool libraryOpen = ClassicLibraryOpen.Value;
            bool playlistsOpen = ClassicPlaylistsOpen.Value;
            _ = LayoutVersion.Value;   // the shortcut band lives on the Curated document
            return _docCache.Get(pinnedOpen, libraryOpen, playlistsOpen, devTools: false, topBar: TopBar);
        }

        static int SectionEpoch()
            => ClassicDocumentCache.FlagsOf(ClassicPinnedOpen.Value, ClassicLibraryOpen.Value, ClassicPlaylistsOpen.Value);

        static void SetSection(string sectionId, bool collapsed)
        {
            if (SidebarBuiltInDocuments.ClassicSectionOf(sectionId) is { } section) SetClassicSection(section, !collapsed);
        }
    }

    /// <summary>Wavee Curated: the persisted, EDITABLE document with the shortcut band materialised as its first
    /// section, the library-only search head, and the ONE canvas seam — armed exactly while the customize route is the
    /// active destination (the route cannot go stale; a flag set by a KeepAlive page would).</summary>
    internal sealed class CuratedMode : Component
    {
        readonly bool _inDrawer;
        SidebarCustomLayout? _sourceDoc;
        SidebarCustomLayout? _renderDoc;

        public CuratedMode(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            var config = UseMemo(() => new PaneConfig
            {
                Design = SidebarDesign.Curated,
                ScrollKeyPrefix = "sidebar.curated",
                Document = BuildDocument,
                SetSectionCollapsed = static (id, collapsed) => Dispatch(new SetSectionCollapsed(id, collapsed)),
                ReadOnly = false,
                SearchHead = true,
                Edit = ReadEditSession,
                OnCustomize = OpenCustomizerRoute,
                OnCreatePlaylist = PaneView.CreatePlaylistFlow,
                HeaderCreate = true,
            }, DepKey.Empty);
            return Embed.Comp(() => new PaneView(config, _inDrawer));
        }

        /// <summary>The persisted document with the band prepended, cached on the document REFERENCE (the reducer's
        /// no-change arm returns the input, so a reference match proves nothing moved).</summary>
        SidebarCustomLayout BuildDocument()
        {
            _ = LayoutVersion.Value;
            var source = Layout;
            if (_renderDoc is { } cached && ReferenceEquals(_sourceDoc, source)) return cached;
            _sourceDoc = source;
            _renderDoc = SidebarShortcutsSection.Prepend(source, source.EffectiveTopBar);
            return _renderDoc;
        }

        /// <summary>The live edit session, or null when the customizer is not the active destination.</summary>
        static SidebarEditState? ReadEditSession()
            => Shell.Current.Value.Kind == Shell.RouteKind.SidebarCustomize ? Edit.Read() : null;
    }

    /// <summary>"Customize sidebar…": switch to Curated (a no-op when already there) THEN navigate — the silent
    /// force-switch the customizer's design segmented makes visible (ch 26 §0.11).</summary>
    internal static void OpenCustomizerRoute()
    {
        SwitchDesign(SidebarDesign.Curated);
        Shell.GoTo(new Shell.Route(Shell.RouteKind.SidebarCustomize));
    }

    // ══ 4. THE MODE SEAM, THE REORDER COMMIT, THE METRICS ══════════════════════════════════════════════════════════════

    /// <summary>THE ONLY MODE SEAM (0.2.9 <c>SidebarPaneConfig</c>, member for member). Built ONCE per mode mount and
    /// frozen into the pane, so EVERY member is a delegate or a flag — a value member would pin frame 1 forever.
    /// <c>Document</c>/<c>Input</c>/<c>ModeEpoch</c>/<c>Edit</c> are invoked inside the pane's render, which is what
    /// subscribes the pane to the signals they read. The renderer never branches on <see cref="Design"/>.</summary>
    internal sealed record PaneConfig
    {
        public required SidebarDesign Design { get; init; }
        public required string ScrollKeyPrefix { get; init; }
        public required Func<SidebarCustomLayout> Document { get; init; }
        /// <summary>The mode's transform of the binder's planner input (V3 folds its filter/sort/search/drill).</summary>
        public Func<SidebarProjectionInput, SidebarProjectionInput>? Input { get; init; }
        /// <summary>Mode-owned state folded into the plan key AND the per-row epoch (Classic's flags, V3's view).</summary>
        public Func<int>? ModeEpoch { get; init; }
        /// <summary>Where a header click's collapse lives (Curated: the undoable command; Classic: its flags).</summary>
        public Action<string, bool>? SetSectionCollapsed { get; init; }
        /// <summary>Not the user's document here: no inline controls, no Remove verb, no empty-pane CTA.</summary>
        public bool ReadOnly { get; init; }
        /// <summary>A "+" in every PlaylistTree header (flyout + drop-to-create). A flag, never a design branch.</summary>
        public bool HeaderCreate { get; init; }
        /// <summary>The library-only search head (only while the document holds a visible EntityList).</summary>
        public bool SearchHead { get; init; }
        /// <summary>Mode chrome above the scroll surface (V3's nav band → header → toolbar → chips → rule).</summary>
        public Func<Element?>? Head { get; init; }
        /// <summary>Chrome tiles prepended to the rail (V3 only — its nav band left the document).</summary>
        public Func<Element?>? RailHead { get; init; }
        /// <summary>The canvas seam: non-null state ⇒ the pane renders as the customize canvas.</summary>
        public Func<SidebarEditState?>? Edit { get; init; }
        public bool ShowLayoutMenu { get; init; } = true;
        public bool RailLayoutMenu { get; init; } = true;
        public Func<Element?>? RailFooter { get; init; }
        /// <summary>What activating a folder does (null ⇒ toggle the shared expansion). V3's narrow drill uses it.</summary>
        public Action<string, string>? ActivateFolder { get; init; }
        public Func<bool>? DisclosesFoldersInline { get; init; }
        public Func<SidebarSectionKind, bool>? IsReorderableSection { get; init; }
        /// <summary>A live probe: a non-custom sort refuses positional drops with "clear sorting to reorder".</summary>
        public Func<bool>? TreeSortedNonCustom { get; init; }
        public Action? SortedListRefusalAction { get; init; }
        /// <summary>(kind, from, requested) ⇒ reachable slot. Allocation-free: it runs on the displacement path.</summary>
        public Func<SidebarSectionKind, int, int, int>? ClampReorderSlot { get; init; }
        public Action<PaneReorder>? CommitReorder { get; init; }
        public Action? OnCustomize { get; init; }
        public Action? OnCreatePlaylist { get; init; }
    }

    /// <summary>One committed same-list reorder in BAND-SLOT space: the renderer knows the geometry, only the mode knows
    /// where the order lives.</summary>
    internal readonly record struct PaneReorder(SidebarSectionSpec Section, int FromSlot, int ToSlot, int SlotCount,
                                                Func<int, string> KeyAt);

    /// <summary>The built-in commit Classic and Curated share: Pinned through the shared pin store (mapped through pin
    /// IDS — hidden overrides shift the band against the store), every other kind through the undoable item command.</summary>
    internal static void DefaultReorderCommit(in PaneReorder r)
    {
        if (r.FromSlot == r.ToSlot) return;
        if (r.Section.Kind == SidebarSectionKind.Pinned)
        {
            int pf = Pins.IndexOf(r.KeyAt(r.FromSlot));
            int pt = Pins.IndexOf(r.KeyAt(r.ToSlot));
            if (pf < 0 || pt < 0) { MovePin(r.FromSlot, r.ToSlot); return; }
            MovePin(pf, pt);
            return;
        }
        int itemFrom = ItemIndexAt(r.Section, r.FromSlot);
        int itemTo = ItemIndexAt(r.Section, r.ToSlot);
        if (itemFrom < 0 || itemTo < 0) return;
        // SidebarItemCommands picks MoveTopBarItem for the materialised Shortcuts band (not in `Sections`).
        Dispatch(SidebarItemCommands.Move(r.Section.Id, itemFrom, itemTo));
    }

    /// <summary>Band position → item-list index: the planner skips hidden items in order.</summary>
    static int ItemIndexAt(SidebarSectionSpec section, int slot)
    {
        var items = section.ItemList;
        int seen = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Hidden) continue;
            if (seen == slot) return i;
            seen++;
        }
        return items.Count == 0 ? -1 : items.Count - 1;
    }

    /// <summary>One contiguous run of reorderable plan rows owned by one section, at that section's ONE row height.</summary>
    internal readonly record struct PaneBand(string SectionId, int Start, int Count, float Extent)
    {
        public bool Contains(int planIndex) => Count > 0 && planIndex >= Start && planIndex < Start + Count;
    }

    /// <summary>ONE inset system and ONE height ladder (0.2.9 <c>SidebarPaneMetrics</c>). The pane pads (8,8,8,12) ONCE
    /// around the virtualized list; every band inside sits at the row inset; a chrome band ABOVE the list lands on the
    /// content lane by itself.</summary>
    internal static class PaneMetrics
    {
        public static readonly Edges4 PanePad = new(SidebarRowGeometry.PaneEdge, 8f, SidebarRowGeometry.PaneEdge, 12f);
        public const float PaneInsetH = SidebarRowGeometry.PaneEdge * 2f;
        public const float ContentLane = SidebarRowGeometry.ContentLane;
        public const float ContentLaneEnd = SidebarRowGeometry.ContentLaneEnd;
        public static readonly Edges4 RowInset = new(SidebarRowGeometry.RowInsetLeft, 0f, SidebarRowGeometry.RowInsetRight, 0f);
        public static readonly Edges4 BandInset = new(ContentLane, 0f, ContentLaneEnd, 0f);
        /// <summary>21 — where every row's art column starts; the V3 header glyph, closed search and first chip sit here.</summary>
        public const float LeadInset = ContentLane + SidebarRowGeometry.LeadingLaneWidth;
        public static readonly Edges4 LeadBandInset = new(LeadInset, 0f, ContentLaneEnd, 0f);
        public const float SectionGap = SidebarRowGeometry.SectionGap;
        public const float HeaderBodyGap = SidebarRowGeometry.HeaderBodyGap;
        /// <summary>THE one edit-card height: the card band is a Reorderable with one pitch.</summary>
        public const float EditCardHeight = SidebarRowGeometry.ClassicHeight;
        public const float EmptyHintHeight = SidebarRowGeometry.EmptyHintHeight;
        /// <summary>Grid cells stay media-card sized at the 460-DIP maximum.</summary>
        public const float GridCellMax = 160f;

        /// <summary>A section's UNIFORM row height — from its subtitle INTENT, never from whether a row has one.</summary>
        public static float RowHeight(SidebarSectionSpec section)
            => SidebarRowGeometry.HeightFor(section.Opts.Density, section.Opts.Subtitles);

        public static float ArtSize(SidebarSectionSpec section) => SidebarRowGeometry.ArtFor(section.Opts.Density);
        public static float CardHeight(SidebarSectionSpec section) => SidebarRowGeometry.CardHeightFor(section.Opts.Density);
        public static float CardCover(SidebarSectionSpec section) => CardHeight(section) - 16f;
    }

    // ══ 5. THE PANE ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>THE ONE SIDEBAR PANE RENDERER (0.2.9 <c>SidebarPane</c>).
    ///
    /// <para>HOW A FRAME FLOWS. (1) Render subscribes the document/projection/pin/folder/search/mode/edit epochs and
    /// re-plans in a <c>UseMemo</c> keyed on their fold, into A/B buffers (a plan ALIASES its buffers). (2) The plan is
    /// published to the bound slots as a PLAIN FIELD from a LAYOUT effect, and only the rows the diff found changed get
    /// their epoch bumped. (3) The row count rides <c>CountSignal</c>, written in the same effect — and it is the ONLY
    /// publish signal this render reads (W3-A2): the plan version is the slots', the effects' and the ItemsView's edge,
    /// the rail version is <see cref="PaneView.RailHost"/>'s. A republish that changed some rows' content re-skins those rows and
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
        readonly SidebarPlanBuffers _railBuffersA = new(), _railBuffersB = new();
        bool _presentedUsesA;
        bool _planPublished;
        int _nextPlanEpoch;

        /// <summary>The pane-OWNED search head text (session-only; two panes must not filter each other).</summary>
        readonly Signal<string> _search = new("");
        string _effectiveSearch = "";

        readonly Signal<int> _rowCount = new(0);
        readonly Signal<int> _planVersion = new(0);
        /// <summary>Bumped by a publish ONLY when the RAIL plan's rows (or the entries they address) differ from the
        /// presented rail's, or the publish was wholesale — <see cref="RailHost"/>'s one re-render edge (W3-A2). A
        /// re-plan that hydrated a row in the expanded list, or toggled a folder past the rail's tile cap, moves
        /// <see cref="_planVersion"/> and leaves this alone.</summary>
        readonly Signal<int> _railVersion = new(0);
        readonly Signal<int> _dispVersion = new(0);
        readonly Signal<int> _disclosureVersion = new(0);

        // ── mount-stable subtrees (W3-A2): built once, handed back by reference on every later render ──
        // Element is immutable and the reconciler compares a ComponentEl by type + key, so reusing the SAME element
        // across renders is exactly the reference short-circuit ToolTipSlots and the props seam rely on. Each of these
        // reads only fields that never change after mount (the layout, the count signal, the controller, `this`).
        Element? _paddedList;
        Element? _searchHead;
        Element? _dragPeekWatcher;
        Element? _railHost;
        /// <summary>The expanded layer's bound width — one Prop for the pane's life, never a fresh closure per render.</summary>
        readonly Prop<float> _expandedWidth = Prop.Of(static () => Sidebar.Width.Value);

        // ── per-row epochs (GROW-ONLY: a slot may address an index a frame after the plan shrank) ──
        Signal<int>[] _rowEpochs = Array.Empty<Signal<int>>();
        byte[] _rowPlay = Array.Empty<byte>();                 // bit 0 current, bit 1 actively playing
        readonly List<int> _rowPlaySet = new();
        readonly List<int> _rowSelSet = new(), _rowSelNext = new(), _rowSelFlip = new();
        Func<string, SidebarSectionSpec?>? _sectionOf;

        // ── disclosure ──
        string? _activeDisclosureKey;
        string? _activeDisclosureId;
        bool _activeDisclosureIsFolder;
        bool _activeDisclosureOpen;
        string? _pendingExpandSection;
        string? _pendingExpandFolder;
        ItemDisclosureRange? _activeDisclosureBand;
        Action<Action>? _post;
        Action? _flushPrefsCommit;
        Action? _queuedDisclosure;
        Action<ItemDisclosureDiagnostic>? _disclosureLog;

        readonly ItemsViewController _listController = new();

        /// <summary>THE ONE published drop slot — written once per hover, read by the row's line, its plate and the commit.</summary>
        readonly Signal<SidebarDropSlot> _dropSlot = new(SidebarDropSlot.None);

        // ── the playlist tree's multi-selection (per pane instance) ──
        internal readonly SidebarTreeSelection TreeSelection = new();
        internal readonly Signal<int> SelectionVersion = new(0);
        internal readonly Signal<bool> ChecksVisible = new(false);
        readonly List<string> _treeVisibleOrder = new();
        readonly HashSet<string> _selBefore = new(StringComparer.Ordinal);

        /// <summary>DRAG PEEK — a transient expansion of a collapsed pane for one drag (never a write to Collapsed).</summary>
        readonly Signal<bool> _dragPeek = new(false);
        /// <summary>The rail tile armed as a drop destination (bound read — the rail subtree is memoized).</summary>
        readonly Signal<string?> _railDropUri = new(null);

        readonly Dictionary<string, Reorderable> _reorder = new(StringComparer.Ordinal);
        readonly List<PaneBand> _bands = new();
        PaneBand _sectionBand;
        Reorderable? _sectionReorder;
        readonly HashSet<string> _pinnedSubtrees = new(StringComparer.Ordinal);
        readonly Dictionary<string, byte> _pinnedDepths = new(StringComparer.Ordinal);
        readonly Dictionary<string, SidebarSectionSpec> _sections = new(StringComparer.Ordinal);

        /// <summary>Playlist-tree tiles the rail may draw before the rest of the document gets its turn.</summary>
        const int RailTreeTiles = 20;
        /// <summary>The context-menu SHIELD's key — the node MUST stay childless (see Render).</summary>
        internal const string ContextShieldKey = "sidebar:context-shield";

        // ── published to the bound slots (PLAIN FIELDS; seeded with real empty plans, never default) ──
        internal SidebarRowPlan Plan = new(Array.Empty<SidebarRow>(), Array.Empty<SidebarLibraryEntry>(), 0);
        internal SidebarRowPlan RailPlan = new(Array.Empty<SidebarRow>(), Array.Empty<SidebarLibraryEntry>(), 0);
        internal SidebarCustomLayout Doc = SidebarCustomLayout.Empty;
        /// <summary>The edit session THE PUBLISHED PLAN WAS BUILT FROM (in lockstep with <see cref="Plan"/>).</summary>
        internal SidebarEditState? PublishedEdit;
        internal IOverlayService MenuOverlay = Overlay.Service.Default;
        internal ActionServices? Acts;
        internal Actions.Registry? Registry;
        internal string? MenuHostSectionId;

        Func<int, (float dx, float dy)>? _displacement;

        sealed record PlanStage(SidebarCustomLayout Document, SidebarRowPlan Pane, SidebarRowPlan Rail,
                                string EffectiveSearch, bool UsesA, int Epoch, SidebarEditState? Edit);

        /// <summary>THE MID-DRAG FREEZE: a re-projection arriving during a rootlist filing is parked, not published.</summary>
        readonly SidebarStageHold<PlanStage> _deferredStage = new();
        /// <summary>One-shot: this gesture's OWN commit is not a foreign projection and must publish through the freeze.</summary>
        bool _publishThroughFreeze;

        // ── selection travel (the NavigationView pill transaction) ──
        string _selRoute = "";
        string _prevSelRoute = "";
        int _selEpoch;
        NodeHandle _selectionFlightFrom, _selectionFlightTo;
        readonly Dictionary<string, NodeHandle> _selectionPills = new(StringComparer.Ordinal);
        readonly Dictionary<int, string> _selectionRouteByNode = new();
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
            _rowLayout = RepeatLayout.Extents(RowExtentSeed, estimatedExtent: SidebarRowGeometry.ClassicHeight);
        }

        public override Element Render()
        {
            _post = UsePost();
            MenuOverlay = UseContext(Overlay.Service) ?? Overlay.Service.Default;
            Acts = ActionServicesOrNull();
            Registry = Actions.Registry.Current ?? Acts?.Extensions;

            // The mode's live document — invoked HERE so the signals it reads subscribe this pane.
            var sourceDoc = Config.Document();
            // The drawer always renders expanded; a live drag peek presents expanded too (it flips twice per drag). Compact is
            // what the FRAME presents (collapsed, or the narrow band's 56-DIP column), never `Collapsed` alone.
            bool compact = !InDrawer && Shell.Ui.SidebarPresentedCompact.Value && !_dragPeek.Value;
            string search = _search.Value;
            // Invoked UNCONDITIONALLY: edit mode must not change the hook sequence (branch on the value, never the hooks).
            var edit = Config.Edit?.Invoke();

            var stage = UseMemo(() => BuildStage(sourceDoc, search, edit), PlanDep(search, in edit));
            if (!_planPublished) PublishStage(stage, notify: false);
            int disclosureUiVersion = _disclosureVersion.Value;
            UseLayoutEffect(() => TryPublishStage(stage), DepKey.From(HashCode.Combine(stage.Epoch, disclosureUiVersion)));
            // AFTER the publish: the travel direction needs the plan the rows are about to render from. This read also
            // subscribes the pane to the route, so a navigation re-renders it without re-planning.
            TrackSelection(SelectedRoute);
            UseSignalEffect(RefreshPlayState);
            UseSignalEffect(RefreshSelection);
            UseLayoutEffect(RunSelectionTransaction, _selEpoch);
            // THE COUNT, never the plan version (W3-A2). This render decides two things off the published plan — empty
            // pane vs list, and whether a pending expansion has its rows yet — and both are functions of the COUNT. The
            // 0.3 first cut read `_planVersion` here, so every publish (each one a re-plan the binder's content gates had
            // let through: a cover landing on a saved album, a playlist learning its track count) rendered this pane a
            // SECOND time, and that render's rail memo rebuilt ~26 tooltip-wrapped tiles — the `PaneView×1 a=324K` +
            // `ToolTip×33` lines of the scroll census. The seed lands in PublishStage's synchronous first publish, above,
            // before this read, so the first render already sees the real count.
            int rows = _rowCount.Value;
            UseLayoutEffect(() =>
            {
                if (_activeDisclosureKey is { } active && _activeDisclosureOpen
                    && (_pendingExpandSection is not null || _pendingExpandFolder is not null)
                    && PendingExpandRange() is null)
                {
                    DisclosureSettled(active);
                    return;
                }
                if (_activeDisclosureKey is not null || _queuedDisclosure is not { } queued) return;
                _queuedDisclosure = null;
                queued();
            }, DepKey.From(HashCode.Combine(disclosureUiVersion, rows)));
            ConfigureReorder();

            Element expanded = new BoxEl
            {
                Key = "expanded-layer", Direction = 1, Grow = 1f, Shrink = 0f,
                // Measured at the OPEN width even while presented compact: text never reflows through 56 DIP.
                Width = _expandedWidth, ClipToBounds = true,
                Opacity = compact ? 0f : 1f, HitTestVisible = !compact,
                Children = ExpandedChildren(rows),
            };

            var children = new List<Element>(3) { expanded };
            if (!InDrawer)
            {
                // THE RAIL IS ITS OWN COMPONENT (W3-A2, RailHost): it re-renders on ITS edges — the rail version, the
                // route, the culture, the binder's first publish — so a pane render never rebuilds its ~26 tooltip-wrapped
                // tiles. The embed is held by reference: a propless ComponentEl the reconciler reuses without a render.
                children.Add(new BoxEl
                {
                    Key = "compact-layer", Direction = 1, Grow = 1f, Shrink = 0f, Width = SidebarPaneBounds.CompactRailW,
                    Opacity = compact ? 1f : 0f, HitTestVisible = compact,
                    // DRAG PEEK: a pure spring-load waypoint over the whole rail (never a destination, never a refusal).
                    DropTarget = RailPeekDropSpec(),
                    Children = [_railHost ??= Embed.Comp(() => new RailHost(this))],
                });
                // Owns the ONE UseDragState() subscription that ends a peek (2 flips per drag, not the drag epoch).
                children.Add(_dragPeekWatcher ??= Embed.Comp(() => new DragPeekWatcher(this)) with { Key = "drag-peek" });
            }

            var root = new BoxEl
            {
                // No fill, no corners: the sidebar is flush frame chrome over Mica (ch 25 §4).
                Grow = 1f, Direction = 1, ZStack = true, ClipToBounds = true,
                Children = [.. children],
            };

            // THE CONTEXT-MENU SHIELD. A context flyout makes an element a hit target (ContextBit), so hanging the menu
            // off the root made every dead spot resolve the WHOLE sidebar as the press/hover owner. The menu goes on a
            // ZStack SHELL plus a CHILDLESS full-bleed shield beneath the content: content wins wherever it hits, the
            // shield takes the rest, and a cascade from a childless node reaches nothing.
            if (!Controls.IsNullOverlay(MenuOverlay))
            {
                var svc = MenuOverlay;
                Func<ContextMenuModel?> menu = LayoutMenu.Model;
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

        // ── the expanded body ──────────────────────────────────────────────────────────────────────────────────────

        Element[] ExpandedChildren(int rows)
        {
            Element? modeHead = Config.Head?.Invoke();
            // No search head on the canvas: at most one body is on screen there, so it would visibly filter nothing.
            Element? searchHead = Config.SearchHead && PublishedEdit is null && HasEntityList(Doc)
                ? (_searchHead ??= Embed.Comp(() => new SearchHead(_search, Sidebar.Width)) with { Key = "head" })
                : null;

            // The list is built ONCE: every option it carries is a mount-stable field or delegate, and the count rides
            // its CountSignal — so a re-render hands the reconciler the same element and it writes nothing.
            Element body = rows == 0 ? EmptyPane() : (_paddedList ??= PaddedList());
            int n = 1 + (modeHead is null ? 0 : 1) + (searchHead is null ? 0 : 1);
            var kids = new Element[n];
            int k = 0;
            if (modeHead is { } mh) kids[k++] = mh;
            if (searchHead is { } sh) kids[k++] = sh;
            kids[k] = body;
            return kids;
        }

        /// <summary>THE PANE'S ONE INSET: (8,8,8,12) around the virtualized list, and nowhere else.</summary>
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
                Overscan = 2,
                CacheExtentPx = 240f,
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
                Disclosure = new DisclosureOptions
                {
                    Version = _planVersion,
                    PendingExpand = PendingExpandRange,
                    OnExpandStarted = OnExpandStarted,
                    OnExpandSettled = OnExpandSettled,
                    Diagnostic = _disclosureLog ??= LogDisclosure,
                },
            }) with { Key = "plan" };

        /// <summary>The authored-empty pane (ch 25 W8): it names the state and offers the one fix; a LOCKED document has
        /// no customizer, so the CTA is ABSENT rather than dead.</summary>
        Element EmptyPane()
        {
            var kids = new List<Element>(4)
            {
                Icon(Icons.SplitView, 24f, Tok.TextTertiary),
                new TextEl(Loc.Get("sidebar.customizer.empty"))
                {
                    Size = 14f, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2,
                },
                new TextEl(Loc.Get("sidebar.customizer.emptySub"))
                {
                    Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3,
                },
            };
            if (Config.OnCustomize is { } customize)
                kids.Add(new BoxEl
                {
                    Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Padding = new Edges4(12f, 0f, 12f, 0f), Corners = Radii.ControlAll,
                    Fill = Tok.AccentDefault, Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                    OnClick = customize,
                    Children =
                    [
                        global::Wavee.Design.Type.DenseTitle(Loc.Get("sidebar.layout.customize")) with
                        {
                            Color = Tok.TextOnAccentPrimary, MaxLines = 1,
                        },
                    ],
                }.Interactive(Interaction.Subtle));

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
        DepKey PlanDep(string search, in SidebarEditState? edit)
        {
            int layoutVer = LayoutVersion.Value;
            int entriesVer = Binder is { } b ? b.Entries.Version.Value : 0;
            // Everything else the planner reads from the binder — pins band, recency, queue / now playing, feeds, a
            // contributed section's rows — moves THIS edge, not the entries cell (G-172/G-173).
            int inputVer = Binder is { } bi ? bi.InputVersion.Value : 0;
            int pinsVer = PinsVersion.Value;
            int folderVer = FolderVersion.Value;
            int binderEpoch = s_binderEpoch.Value;
            int revision = Binder?.Revision ?? 0;
            int mode = Config.ModeEpoch?.Invoke() ?? 0;
            int editFold = SidebarEditPlan.Fold(in edit);
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
            if (editFold != _depEdit) moved |= Shell.SidebarReplanCause.Edit;
            if (!string.Equals(search, _depSearch, StringComparison.Ordinal)) moved |= Shell.SidebarReplanCause.Search;
            _depLayout = layoutVer; _depEntries = entriesVer; _depInput = inputVer; _depPins = pinsVer; _depFolder = folderVer;
            _depBinder = binderEpoch; _depRevision = revision; _depMode = mode; _depEdit = editFold; _depSearch = search;
            _pendingReplanCauses |= moved;
            return DepKey.Combine(DepKey.From(layoutVer, entriesVer, pinsVer, folderVer),
                DepKey.Combine(DepKey.From(revision, mode, editFold, binderEpoch),
                    DepKey.Combine(DepKey.From(inputVer), search)));
        }

        // The last PlanDep inputs + the causes accumulated since the last build (Sidebar.Census.cs).
        int _depLayout = int.MinValue, _depEntries = int.MinValue, _depInput = int.MinValue, _depPins = int.MinValue,
            _depFolder = int.MinValue, _depBinder = int.MinValue, _depRevision = int.MinValue, _depMode = int.MinValue,
            _depEdit = int.MinValue;
        string? _depSearch;
        Shell.SidebarReplanCause _pendingReplanCauses;

        PlanStage BuildStage(SidebarCustomLayout document, string search, SidebarEditState? edit)
        {
            Shell.SidebarReplanCensus.NoteBuild(_pendingReplanCauses);
            _pendingReplanCauses = Shell.SidebarReplanCause.None;
            var input = Input(search);
            bool useA = !_planPublished || !_presentedUsesA;
            var paneBuffers = useA ? _paneBuffersA : _paneBuffersB;
            var railBuffers = useA ? _railBuffersA : _railBuffersB;
            // The canvas is the EDIT PROJECTION of the same document into the same flat list — never a second renderer.
            var pane = edit is { } session
                ? PlanEdit(document, in input, in session, paneBuffers)
                : Sidebar.Plan(document, in input, paneBuffers);
            // The rail is never the canvas: a collapsed pane mid-edit shows the user's actual rail.
            var rail = PlanRail(document, in input, railBuffers);
            return new PlanStage(document, pane, rail, SidebarSearch.Normalize(input.Search), useA, ++_nextPlanEpoch, edit);
        }

        void TryPublishStage(PlanStage stage)
        {
            // A collapse keeps the expanded model presented until it reaches zero; a prepared expansion must publish
            // first so ItemsView can arm the opening band.
            bool preparedExpansion = _activeDisclosureOpen
                && (_pendingExpandSection is not null || _pendingExpandFolder is not null);
            if (_activeDisclosureKey is not null && !preparedExpansion) return;
            if (_planPublished && stage.UsesA == _presentedUsesA && ReferenceEquals(stage.Pane.Rows, Plan.Rows)) return;
            // THE MID-DRAG FREEZE — exempt for a disclosure in flight (spring-loading a folder exists to reveal its
            // children) and for this gesture's own commit. Track drags are not frozen: they aim at identity, not position.
            if (_publishThroughFreeze) _publishThroughFreeze = false;
            else if (_activeDisclosureKey is null && _deferredStage.TryHold(Drag.LiveRootlistDrag(), stage)) return;
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
            var oldRail = RailPlan;
            // A new document, a new effective query or an edit-session edge changes what rows draw without necessarily
            // changing the row record, so those bump wholesale.
            bool wholesale = !_planPublished
                             || !ReferenceEquals(stage.Document, Doc)
                             || !string.Equals(stage.EffectiveSearch, _effectiveSearch, StringComparison.Ordinal)
                             || !Nullable.Equals(stage.Edit, PublishedEdit);
            // The rail draws from its OWN plan (and the document, for its sections), so it moves on a wholesale publish
            // or when its rows/entries differ — a re-plan that only touched the expanded list leaves it alone (W3-A2).
            bool railChanged = wholesale
                               || PlanDiff.Changed(oldRail.Rows, oldRail.Entries, stage.Rail.Rows, stage.Rail.Entries);

            PublishedEdit = stage.Edit;
            Doc = stage.Document;
            Plan = stage.Pane;
            RailPlan = stage.Rail;
            _effectiveSearch = stage.EffectiveSearch;
            _presentedUsesA = stage.UsesA;
            _planPublished = true;
            RebuildIndex(Plan);
            ConfigureReorder();
            EnsureRowSlots(Plan.Rows.Count);
            if (wholesale) ReseedRowExtents();
            if (!notify)
            {
                // The FIRST publish runs synchronously inside the pane's render, before anything has read the count
                // signal: seeding it here is a forward write (no subscriber yet), and Render's read right after it sees
                // the real count on frame one.
                _rowCount.Value = Plan.Rows.Count;
                return;
            }

            Shell.SidebarReplanCensus.NotePublish(railChanged, wholesale);
            void PublishSignals()
            {
                _rowCount.Value = Plan.Rows.Count;
                _planVersion.Value = _planVersion.Peek() + 1;
                if (railChanged) _railVersion.Value = _railVersion.Peek() + 1;
                if (wholesale) BumpAllRowEpochs();
                else BumpChangedRowEpochs(oldRows, oldEntries);
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
                input = input with
                {
                    LibraryState = SidebarSourceState.Pending,
                    TreeState = SidebarSourceState.Pending,
                    RecentsState = SidebarSourceState.Pending,
                    NewReleasesState = SidebarSourceState.Pending,
                    ConcertsState = SidebarSourceState.Pending,
                };
            }
            // A 200-playlist rootlist must not eat the whole 40-tile rail budget.
            input = input with { RailTreeCap = RailTreeTiles };
            if (Config.Input is { } shape) input = shape(input);
            if (search.Length > 0) input = input with { Search = search };
            return input;
        }

        /// <summary>Rebuilt with every plan: the section map, the menu host, the tree order + prune, the reorder bands.</summary>
        void RebuildIndex(SidebarRowPlan plan)
        {
            _sections.Clear();
            var sections = Doc.Sections;
            for (int i = 0; i < sections.Count; i++)
            {
                _sections[sections[i].Id] = sections[i];
                var kids = sections[i].ChildList;
                for (int j = 0; j < kids.Count; j++) _sections[kids[j].Id] = kids[j];
            }

            MenuHostSectionId = null;
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
            RebuildSectionBand(plan);
            var rows = plan.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (SectionOf(row.SectionId)?.Kind != SidebarSectionKind.Pinned) continue;
                if (row.Kind is SidebarRowKind.SectionHeader or SidebarRowKind.SectionCard)
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
            float bandExtent = SidebarRowGeometry.ClassicHeight;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (Config.ShowLayoutMenu && MenuHostSectionId is null && row.Kind == SidebarRowKind.SectionHeader)
                    MenuHostSectionId = row.SectionId;

                bool item = IsReorderableRow(row);
                if (item && bandId is not null && string.Equals(bandId, row.SectionId, StringComparison.Ordinal)) continue;
                if (bandId is not null)
                {
                    _bands.Add(new PaneBand(bandId, bandStart, i - bandStart, bandExtent));
                    bandId = null;
                }
                if (!item) continue;
                var section = SectionOf(row.SectionId);
                if (section is null || !IsReorderableSection(section.Kind) || _pinnedSubtrees.Contains(row.SectionId))
                    continue;
                bandId = row.SectionId;
                bandStart = i;
                bandExtent = PaneMetrics.RowHeight(section);
            }
            if (bandId is not null) _bands.Add(new PaneBand(bandId, bandStart, rows.Count - bandStart, bandExtent));
        }

        /// <summary>The section-card drag band: armed only on the canvas while every card is a card, skipping the pinned
        /// Shortcuts head (it is not in <c>Sections</c>), verified contiguous, and never for fewer than two cards.</summary>
        void RebuildSectionBand(SidebarRowPlan plan)
        {
            _sectionBand = default;
            if (PublishedEdit is not { } edit || !SidebarEditPlan.SectionsReorderable(in edit)) return;
            var rows = plan.Rows;
            int start = -1, count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind != SidebarRowKind.SectionCard) { if (start >= 0) break; continue; }
                if (SidebarEditPlan.IsPinnedCard(rows[i].SectionId)) { if (start >= 0) break; continue; }
                if (start < 0) start = i;
                count++;
            }
            if (start < 0 || count < 2) return;
            _sectionBand = new PaneBand(SidebarEditPlan.SectionDragKind, start, count, PaneMetrics.EditCardHeight);
        }

        internal bool TryEditSectionBand(int planIndex, out PaneBand band)
        {
            band = _sectionBand;
            return band.Contains(planIndex);
        }

        /// <summary>The section-card Reorderable. Its OWN drag kind; LiveProject off and no insertion line (a recycling
        /// list with no <c>List(...)</c> wrapper); the ghost lift stays because the chip resolver answers only
        /// <see cref="Drag.Resource"/>.</summary>
        internal Reorderable SectionReorder => _sectionReorder ??= new Reorderable(SidebarEditPlan.SectionDragKind)
        {
            LiveProject = false,
            ShowInsertionLine = false,
            AnnounceAssertive = true,
        };

        static bool IsReorderableRow(in SidebarRow row) => row.Kind
            is SidebarRowKind.EntityRow or SidebarRowKind.IconRow or SidebarRowKind.Placeholder
            or SidebarRowKind.FolderHeader;

        bool IsReorderableSection(SidebarSectionKind kind)
            => Config.IsReorderableSection is { } test
                ? test(kind)
                : kind is SidebarSectionKind.Pinned or SidebarSectionKind.StaticLinks or SidebarSectionKind.CustomGroup;

        static bool HasEntityList(SidebarCustomLayout doc)
        {
            var sections = doc.Sections;
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].Kind == SidebarSectionKind.EntityList && !sections[i].Hidden) return true;
                var kids = sections[i].ChildList;
                for (int j = 0; j < kids.Count; j++)
                    if (kids[j].Kind == SidebarSectionKind.EntityList && !kids[j].Hidden) return true;
            }
            return false;
        }

        // ── disclosure choreography ────────────────────────────────────────────────────────────────────────────────
        // Expand: the inserted band fades/rises while survivors FLIP; collapse: departing rows stay alive while
        // survivors glide up, and the preference write is deferred to the settle. Reduced motion is the engine's
        // (seeded under named tokens) — never branched on here.

        ItemDisclosureRange? PendingExpandRange()
        {
            if (_pendingExpandSection is { } section && TrySectionBodyRange(section, out var sectionRange)) return sectionRange;
            if (_pendingExpandFolder is { } folder && TryFolderDescendantRange(folder, out var folderRange)) return folderRange;
            return null;
        }

        void OnExpandStarted(ItemDisclosureRange range)
        {
            if (!string.Equals(_activeDisclosureKey, range.Key, StringComparison.Ordinal)) return;
            _activeDisclosureBand = range;
            _pendingExpandSection = null;
            _pendingExpandFolder = null;
        }

        void OnExpandSettled(ItemDisclosureRange range) => DisclosureSettled(range.Key);

        internal bool DisclosureOpen(string id, bool folder, bool fallback)
            => _activeDisclosureKey is not null
               && _activeDisclosureIsFolder == folder
               && string.Equals(_activeDisclosureId, id, StringComparison.Ordinal)
                ? _activeDisclosureOpen
                : fallback;

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

        void StartDisclosure(string key, string id, bool folder, bool open, Action commit)
        {
            if (_activeDisclosureKey is not null && !string.Equals(_activeDisclosureKey, key, StringComparison.Ordinal))
            {
                _queuedDisclosure = () => StartDisclosure(key, id, folder, open, commit);
                if (_pendingExpandSection is not null || _pendingExpandFolder is not null)
                {
                    string completed = _activeDisclosureKey;
                    _pendingExpandSection = null;
                    _pendingExpandFolder = null;
                    DisclosureSettled(completed);
                }
                else _listController.CompleteDisclosure();
                return;
            }
            if (_activeDisclosureKey is not null && _activeDisclosureOpen == open) return;

            _activeDisclosureKey = key;
            _activeDisclosureId = id;
            _activeDisclosureIsFolder = folder;
            _activeDisclosureOpen = open;

            ItemDisclosureRange range;
            bool hasRange = folder ? TryFolderDescendantRange(id, out range) : TrySectionBodyRange(id, out range);
            if (open && !hasRange)
            {
                if (folder) _pendingExpandFolder = id; else _pendingExpandSection = id;
                commit();
                SchedulePrefsCommit();
                // PUBLISH ON THE CLICK FRAME (input phase ⇒ a forward write): otherwise the chevron rotates two frames
                // before any row moves.
                RepublishNow();
                _activeDisclosureBand = PendingExpandRange();
                _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
                BumpDisclosureEpochs(id, folder, _activeDisclosureBand);
                return;
            }
            if (!hasRange)
            {
                commit();
                SchedulePrefsCommit();
                DisclosureSettled(key);
                return;
            }

            _pendingExpandSection = null;
            _pendingExpandFolder = null;
            _activeDisclosureBand = range;
            _listController.BeginDisclosure(range,
                open ? ItemDisclosureDirection.Expand : ItemDisclosureDirection.Collapse,
                collapseCommit: open ? null : WithPrefsCommit(commit),
                settled: () => DisclosureSettled(key));
            _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
            BumpDisclosureEpochs(id, folder, range);
        }

        void DisclosureSettled(string key)
        {
            if (!string.Equals(_activeDisclosureKey, key, StringComparison.Ordinal)) return;
            string? id = _activeDisclosureId;
            bool folder = _activeDisclosureIsFolder;
            var band = _activeDisclosureBand;
            _activeDisclosureKey = null;
            _activeDisclosureId = null;
            _activeDisclosureBand = null;
            _pendingExpandSection = null;
            _pendingExpandFolder = null;
            _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
            if (id is not null) BumpDisclosureEpochs(id, folder, band);
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
            TryPublishStage(BuildStage(Config.Document(), _search.Peek(), Config.Edit?.Invoke()));
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
            if (d.Kind is ItemDisclosureDiagnosticKind.Progress) return;   // per-frame: too chatty for an always-on log
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
            switch (row.Kind)
            {
                case SidebarRowKind.EntityCard:
                    return resolved ? entries[row.EntryIndex].Uri : "";
                case SidebarRowKind.IconRow:
                case SidebarRowKind.EntityRow:
                case SidebarRowKind.Placeholder:
                {
                    if (resolved) return entries[row.EntryIndex].Uri;
                    var section = SectionOf(row.SectionId);
                    if (section is null) return "";
                    var item = SidebarRowResolve.ItemOf(section, row.Key);
                    return item is { Target: SidebarItemTarget.Track } ? item.Key : "";
                }
                default:
                    return "";
            }
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

        /// <summary>The live selected route key. SUBSCRIBES the caller — only the pane's own render and the
        /// <see cref="RailHost"/> (whose tiles bake the selected state in as a value) use it.</summary>
        internal string SelectedRoute => RouteKeyOf(Shell.Current.Value);

        /// <summary>The selected route key WITHOUT subscribing (rows re-render on their epoch; the rail memo keys on it).</summary>
        internal string SelectedRoutePeek => RouteKeyOf(Shell.Current.Peek());

        /// <summary>Does row <paramref name="index"/> draw SELECTED for <paramref name="route"/>? The ONE rule the sweep
        /// also uses (<see cref="SidebarRowResolve.SelectsRoute"/>).</summary>
        internal bool RowSelectsRoute(int index, string route)
        {
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return false;
            var row = rows[index];
            return SidebarRowResolve.SelectsRoute(in row, Plan.Entries, SectionOf(row.SectionId), route);
        }

        /// <summary>The ONE route sweep: bump the symmetric difference of the selected rows — the row that lost the
        /// pill and the row that gained it, nothing else.</summary>
        void RefreshSelection()
        {
            string route = SelectedRoute;
            _ = _planVersion.Value;
            var next = _rowSelNext;
            next.Clear();
            SidebarRowResolve.Sweep(Plan.Rows, Plan.Entries, _sectionOf ??= SectionOf, route, next);
            var prev = _rowSelSet;
            var flipped = _rowSelFlip;
            flipped.Clear();
            SidebarRowResolve.Flipped(prev, next, flipped);
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
        internal void RegisterSelectionPill(string route, NodeHandle node)
        {
            var scene = Context.Scene;
            if (route.Length == 0 || scene is null || node.IsNull || !scene.IsLive(node)) return;
            int index = (int)node.Raw.Index;
            if (_selectionRouteByNode.TryGetValue(index, out var previous)
                && !string.Equals(previous, route, StringComparison.Ordinal)
                && _selectionPills.TryGetValue(previous, out var owned) && owned == node)
                _selectionPills.Remove(previous);
            _selectionRouteByNode[index] = route;
            _selectionPills[route] = node;
        }

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
            // Same lane ⇒ the continuous worm; a real depth change scales (linear, no opacity leg).
            bool sameLane = MathF.Abs(to.X - from.X) < 0.5f;
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
            if (!_planPublished) return SidebarRowGeometry.ClassicHeight;
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return SidebarRowGeometry.ClassicHeight;
            return SidebarRowExtents.HeightOf(rows, index, SectionOf(rows[index].SectionId), !Config.ReadOnly);
        }

        void ReseedRowExtents()
        {
            if (_rowLayout.CustomLayout is MeasuredStackVirtualLayout measured) measured.Reseed(Plan.Rows.Count);
        }

        /// <summary>The row's LIVE laid-out height (bound-safe: one peek + one rect read), 44 on any degenerate answer.</summary>
        internal float RowExtentOf(int index)
        {
            if ((uint)index >= (uint)Plan.Rows.Count) return SidebarRowGeometry.ClassicHeight;
            float cross = MathF.Max(1f, Sidebar.Width.Peek() - PaneMetrics.PaneInsetH);
            var layout = _rowLayout.CustomLayout;
            if (layout is null) return SidebarRowGeometry.ClassicHeight;
            float extent = layout.ItemRect(index, cross).H;
            return float.IsFinite(extent) && extent > 0f ? extent : SidebarRowGeometry.ClassicHeight;
        }

        /// <summary>The normalized query that built the published plan (pane head, or V3's mode-global query).</summary>
        internal string SearchText => _effectiveSearch;

        internal SidebarSectionSpec? SectionOf(string sectionId) => _sections.TryGetValue(sectionId, out var s) ? s : null;

        readonly Dictionary<string, (int Revision, bool HasFolder)> _sectionHasFolder = new(StringComparer.Ordinal);

        /// <summary>Does this section plan a folder row (memoized per plan revision, never scanned per realized row)?</summary>
        internal bool SectionHasFolder(string sectionId)
        {
            if (_sectionHasFolder.TryGetValue(sectionId, out var cached) && cached.Revision == Plan.Revision)
                return cached.HasFolder;
            var rows = Plan.Rows;
            bool has = false;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Kind == SidebarRowKind.FolderHeader
                    && string.Equals(rows[i].SectionId, sectionId, StringComparison.Ordinal)) { has = true; break; }
            _sectionHasFolder[sectionId] = (Plan.Revision, has);
            return has;
        }

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
            ConfigureSectionReorder();
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

        void ConfigureSectionReorder()
        {
            if (_sectionReorder is null && _sectionBand.Count == 0) return;
            var ro = SectionReorder;
            ro.Scene = Context.Scene;
            ro.RequestRender = BumpDisplacement;
            ro.ItemCount = _sectionBand.Count;
            ro.ItemExtent = PaneMetrics.EditCardHeight;
            ro.Spacing = 0f;
            ro.ItemOf = null;
            ro.OnReorder = CommitSectionMove;
            // The keyboard lift's only feedback (Space · arrows · Space · Esc) — composed on the EDGE, never per frame.
            ro.AnnounceText = SectionAnnounce;
        }

        string? SectionAnnounce(ReorderAnnounce a)
        {
            string name = SectionTitleAtCardSlot(a.Index);
            if (name.Length == 0) return null;
            string where = Loc.Format("sidebar.pin.position", ("index", a.Slot + 1), ("count", a.Count));
            return a.Kind switch
            {
                ReorderAnnounceKind.Grab => Loc.Format("sidebar.customizer.reorderGrabbed", ("name", name), ("position", where)),
                ReorderAnnounceKind.Move => Loc.Format("sidebar.customizer.reorderMoved", ("name", name), ("position", where)),
                ReorderAnnounceKind.Drop => Loc.Format("sidebar.customizer.reorderDropped", ("name", name), ("position", where)),
                _ => Loc.Format("sidebar.customizer.reorderCancelled", ("name", name)),
            };
        }

        string SectionTitleAtCardSlot(int slot)
        {
            string id = SidebarEditPlan.SectionIdAt(Plan.Rows, _sectionBand.Start, _sectionBand.Count, slot);
            var section = id.Length == 0 ? null : SectionOf(id);
            return section is null ? "" : PaneText.TitleOf(section);
        }

        /// <summary>A section-card drag as the undoable MoveSection, translated against the PERSISTED document (the
        /// render document carries the materialised Shortcuts section at index 0).</summary>
        void CommitSectionMove(int from, int to)
        {
            if (Config.ReadOnly) return;
            var command = SidebarEditPlan.ToMoveSection(Layout, Plan.Rows, _sectionBand.Start, _sectionBand.Count, from, to);
            if (command is not null) DispatchCanvas(command);
        }

        /// <summary>The ItemsView displacement channel in plan-row space. Stable delegate (ListOptions freeze at mount).</summary>
        (float dx, float dy) Displacement(int planIndex)
        {
            if (_sectionBand.Contains(planIndex) && _sectionReorder is { IsLifted: true } sro)
                return (0f, sro.OffsetFor(planIndex - _sectionBand.Start));
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

        /// <summary>Menu Move up / Move down: the band's own commit when armed; the pin store when a Pinned band is
        /// disarmed (an expanded folder) — drag is one way, never the only way.</summary>
        internal void MoveRowByKey(string sectionId, string key, int delta)
        {
            if (delta == 0 || key.Length == 0) return;
            var section = SectionOf(sectionId);
            if (section is null) return;
            var band = BandFor(sectionId);
            if (band.Count > 0)
            {
                int from = -1;
                for (int s = 0; s < band.Count; s++)
                    if (string.Equals(KeyAt(sectionId, s), key, StringComparison.Ordinal)) { from = s; break; }
                if (from < 0) return;
                int to = from + delta;
                if ((uint)to >= (uint)band.Count) return;
                CommitReorder(sectionId, from, to);
                return;
            }
            if (section.Kind != SidebarSectionKind.Pinned) return;
            string id = SidebarPinId.Canonical(key) ?? key;
            int pinFrom = Pins.IndexOf(id);
            int pinTo = pinFrom + delta;
            if (pinFrom < 0 || (uint)pinTo >= (uint)Pins.Count) return;
            MovePin(pinFrom, pinTo);
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
                MovePin(at, slot > at ? slot - 1 : slot);   // remove-then-insert shifts later indices down by one
                return;
            }
            PinWithToast(pinId, SidebarPinId.KindOf(pinId), SidebarPinId.UriOf(pinId), p.Name);
            int now = Pins.IndexOf(pinId);
            if (now > slot && slot >= 0) MovePin(now, slot);
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
                if (SectionOf(row.SectionId)?.Kind != SidebarSectionKind.PlaylistTree) continue;
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
            // …and the shell, which CLIPS the column: without the mirror the peeked rows lay out expanded inside 56 DIP.
            if (DragPeek.Peek() != on) DragPeek.Value = on;
        }

        /// <summary>Disarm every row's cue on SESSION END (a drop outside the pane never reaches a row's OnLeave).</summary>
        internal void ClearDropSlot()
        {
            if (_dropSlot.Peek().PlanIndex >= 0) _dropSlot.Value = SidebarDropSlot.None;
            if (_railDropUri.Peek() is not null) _railDropUri.Value = null;
        }

        /// <summary>THE 56-DIP RAIL AS ITS OWN COMPONENT (W3-A2). The 0.3 first cut memoized <c>Rail.Build</c> INSIDE the
        /// pane's render on (plan version, theme, culture, binder, rail head, route) — so every publish, each a re-plan the
        /// binder's content gates had let through, re-rendered the pane (it read <c>_planVersion</c>) and the memo rebuilt
        /// ~26 tooltip-wrapped tiles: <c>ToolTipSlots</c> compares its target by REFERENCE, so every rebuilt tile re-pushed
        /// and re-rendered its ToolTip — the <c>ToolTip×33</c> line of the scroll census, while the sidebar itself never
        /// scrolled. This host subscribes to <see cref="_railVersion"/> instead, which <see cref="PublishStage"/> bumps only
        /// when the RAIL plan's rows or their entries differ (or the publish was wholesale); plus the route (a tile bakes
        /// <c>selected</c> in as a value — a bind would freeze on the reused node, ch 25 §9), the culture, the rail head's
        /// document epoch and the binder's first publish (the pending-skeleton edge). A retheme re-renders the tree
        /// engine-side, so <c>Tok.Epoch</c> is read for parity with the old key only. The pane hands the rail its plan as a
        /// plain field, exactly as it hands the slots theirs.</summary>
        sealed class RailHost(PaneView owner) : Component
        {
            public override Element Render()
            {
                _ = owner._railVersion.Value;
                _ = Localization.CultureEpoch.Value;
                _ = s_binderEpoch.Value;
                _ = Tok.Epoch;
                if (owner.Config.RailHead is not null) _ = LayoutVersion.Value;
                _ = owner.SelectedRoute;   // subscribes THIS host, not the pane: a navigation re-skins the selected tile
                return ScrollView(Rail.Build(owner, owner.RailPlan)) with { Grow = 1f, AutoEdgeFade = true, SuppressScrollBar = true };
            }
        }

        /// <summary>Owns the ONE UseDragState() subscription that ends a peek, disarms the cue and flushes the freeze —
        /// on SESSION END (drop, cancel and Escape alike), in a layout effect keyed on the active EDGE.</summary>
        sealed class DragPeekWatcher(PaneView owner) : Component
        {
            public override Element Render()
            {
                bool active = UseDragState().Active;
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

        /// <summary>A document command; a no-op under a LOCKED document (the belt to the rows' braces).</summary>
        internal void Dispatch(SidebarCommand command)
        {
            if (Config.ReadOnly) return;
            Sidebar.Dispatch(command);
        }

        /// <summary>Collapse/expand through the MODE's owner, choreographed by the disclosure channel.</summary>
        internal void ToggleSection(string sectionId, bool collapsed)
        {
            if (Config.SetSectionCollapsed is not { } apply) return;
            StartDisclosure("section:" + sectionId, sectionId, folder: false, open: !collapsed, () => apply(sectionId, collapsed));
        }

        // ── the customize canvas ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>The host the options popover hands the customizer's control set — the session itself.</summary>
        internal ISidebarEditHost EditHost => Edit;

        /// <summary>SESSION state, never SetSectionCollapsed: opening a card to look inside must not rewrite the real
        /// sidebar's collapse state or fill the undo ring.</summary>
        internal void ToggleEditExpanded(string sectionId) => Edit.ToggleExpanded(sectionId);

        /// <summary>Reads the session the PUBLISHED plan was built from, so a chevron never claims "open" over no body.</summary>
        internal bool EditShowsBody(SidebarSectionSpec section)
            => PublishedEdit is { } edit && SidebarEditPlan.ShowsBody(in edit, section);

        internal void SetSectionHidden(string sectionId, bool hidden)
        {
            if (Config.ReadOnly || SidebarEditPlan.IsPinnedCard(sectionId)) return;
            DispatchCanvas(new SetSectionHidden(sectionId, hidden));
        }

        /// <summary>The card menu's Move up/down: addresses the document, so it works while the drag band is disarmed.</summary>
        internal void MoveSectionBy(string sectionId, int delta)
        {
            if (Config.ReadOnly || delta == 0 || SidebarEditPlan.IsPinnedCard(sectionId)) return;
            var layout = Layout;
            var at = layout.Locate(sectionId);
            if (at.Index < 0) return;
            int siblings = at.Parent is null ? layout.Sections.Count : at.Parent.ChildList.Count;
            int next = at.Index + delta;
            if (next < 0 || next >= siblings) return;
            DispatchCanvas(new MoveSection(sectionId, at.Parent?.Id, next));
        }

        internal void RemoveEditSection(string sectionId)
        {
            if (Config.ReadOnly || SidebarEditPlan.IsPinnedCard(sectionId)) return;
            if (DispatchCanvas(new RemoveSection(sectionId)) != SidebarRejectReason.None) return;
            if (string.Equals(Edit.Expanded.Peek(), sectionId, StringComparison.Ordinal)) Edit.Expanded.Value = null;
        }

        /// <summary>Is there room for one more section? Runs per frame per target during a palette drag — a compare only.</summary>
        internal bool CanAcceptPaletteDrop => !Config.ReadOnly && Layout.SectionCount < SidebarLayoutReducer.MaxSections;

        /// <summary>The per-CARD accept: room for a section AND a card a new section can land above (a child card inside a
        /// group is not a top-level slot). Allocation-free, per frame.</summary>
        internal bool CanAcceptPaletteDropBefore(string sectionId)
            => CanAcceptPaletteDrop && SidebarEditPlan.CanAddBefore(Layout, sectionId);

        /// <summary>Why this card refuses a palette chip — the caption the drag chip shows beside its not-allowed glyph, so
        /// the refusal is said while the user is still aiming (a loc lookup, never a composed string).</summary>
        internal string PaletteRefusalKey(string sectionId)
        {
            var layout = Layout;
            if (Config.ReadOnly || layout.SectionCount >= SidebarLayoutReducer.MaxSections) return PaneLoc.EditDropFull;
            return layout.Locate(sectionId).Index < 0
                ? SidebarRejectText.LocKey(SidebarRejectReason.UnknownSection)!
                : SidebarRejectText.LocKey(SidebarRejectReason.NestingTooDeep)!;
        }

        internal void AddSectionFromPalette(string beforeSectionId, SidebarSectionDropPayload payload)
        {
            if (Config.ReadOnly) return;
            // The accept already refused the cases the caption explains; a command that still cannot be built (a kind this
            // build does not know) is said, not swallowed.
            if (SidebarEditPlan.ToAddSection(Layout, beforeSectionId, payload) is { } command) DispatchCanvas(command);
            else SayCanvasRejection(SidebarRejectReason.UnknownSection);
        }

        internal void DuplicateEditSection(string sectionId, string copyTitle)
        {
            if (Config.ReadOnly || SidebarEditPlan.IsPinnedCard(sectionId)) return;
            DispatchCanvas(new DuplicateSection(sectionId, copyTitle));
        }

        /// <summary>A canvas command — a card drag, a card-menu verb, a palette drop — through the edit session, so the
        /// options popover's controls see the verdict (<c>RejectEpoch</c> / <c>LastReject</c>), and, because the canvas has
        /// no inline reject strip of its own, a rejection is SAID (G-184: these went straight to <c>Sidebar.Dispatch</c>
        /// and a refused one was silent).</summary>
        SidebarRejectReason DispatchCanvas(SidebarCommand command)
        {
            var reason = Edit.Apply(command);
            SayCanvasRejection(reason);
            return reason;
        }

        /// <summary>The rejection sentence as a toast. A NoChange is silence, not a rejection (the gesture that produced it
        /// landed where it started).</summary>
        static void SayCanvasRejection(SidebarRejectReason reason)
        {
            if (reason is SidebarRejectReason.None or SidebarRejectReason.NoChange) return;
            if (SidebarRejectText.LocKey(reason) is { } key)
                Notify.Say(Loc.Get(key), InfoBarSeverity.Informational, dedupeKey: "sidebar.canvas.reject");
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
            StartDisclosure("folder:" + folderId, folderId, folder: true, open: !IsFolderExpanded(folderId), commit);
        }

        // ── the collapsed rail's folder flyout ─────────────────────────────────────────────────────────────────────

        /// <summary>Realized rail tiles by key: a flyout anchors to a NODE and the memoized rail has no hooks.</summary>
        readonly Dictionary<string, NodeHandle> _railNodes = new(StringComparer.Ordinal);

        internal void RegisterRailNode(string key, NodeHandle node) => _railNodes[key] = node;

        NodeHandle RailNode(string key) => _railNodes.TryGetValue(key, out var h) ? h : NodeHandle.Null;

        OverlayHandle? _railFolderFlyout;

        /// <summary>Toggle the 300-DIP side flyout for a folder tile, anchored to the tile's right edge (the only side a
        /// 56-DIP strip against the window edge has room on). A second click closes it.</summary>
        internal void OpenRailFolderFlyout(string sectionId, in SidebarLibraryEntry folder)
        {
            if (Controls.IsNullOverlay(MenuOverlay)) return;
            string folderId = folder.FolderId;
            if (folderId.Length == 0) return;
            if (_railFolderFlyout is { IsOpen: true } open) { open.Close(); return; }
            string key = RailTileKey(in folder);
            string name = folder.Name;
            _railFolderFlyout = MenuOverlay.Open(
                () => RailNode(key),
                () => Embed.Comp(() => new RailFolderFlyout
                {
                    Owner = this,
                    SectionId = sectionId,
                    RootFolderId = folderId,
                    RootFolderName = name,
                    Close = CloseRailFolderFlyout,
                }),
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                {
                    ConstrainToRootBounds = true,
                });
            _railFolderFlyout.ClosedAction = () => _railFolderFlyout = null;
        }

        internal void CloseRailFolderFlyout() => _railFolderFlyout?.Close();

        /// <summary>The rail registry key — the SAME string the tile is keyed with.</summary>
        internal static string RailTileKey(in SidebarLibraryEntry entry) => "rail:" + entry.Id;

        /// <summary>The folder menu's Expand verb from a rail tile or flyout row: open the pane and disclose it there.</summary>
        internal void ExpandFolderInPane(string folderId)
        {
            if (folderId.Length == 0) return;
            CloseRailFolderFlyout();
            SetCollapsed(false);
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

        internal void OpenCustomizer() => Config.OnCustomize?.Invoke();

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
