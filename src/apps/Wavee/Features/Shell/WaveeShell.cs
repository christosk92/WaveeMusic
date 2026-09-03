using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

// The Wavee shell root — the WaveeMusic 4-row chrome (tab strip + caption · toolbar · sidebar + content card; player bar
// deferred). Owns the route as SIGNALS so the toolbar/sidebar/tabs all react, plus the open-tab list. Builds the frame
// ONCE and never remounts it for theming: a theme/palette switch bumps Tok.Epoch and the host's RethemeAll re-renders
// every component and re-fires every bound fill IN PLACE (there is no theme-keyed OverlayHost remount) — which is why
// chrome fills must be Prop.Of binds or render-time token reads, never values frozen into ctor args at mount. Each
// chrome piece re-renders itself from the signals it reads. Mounted by WaveeApp inside Services + PlaybackBridge.
sealed class WaveeShell : Component
{
    readonly Signal<Route> _route = new(new Route("home"));
    readonly Signal<NavTransitionKind> _navMotion = new(NavTransitionKind.Forward);
    readonly Signal<bool> _canBack = new(false);
    readonly Signal<bool> _canForward = new(false);
    const int MaxBackStack = 200;   // bound the in-memory back/forward stacks over a long session (the persisted HistoryStore keeps its own 500-entry cap)
    readonly List<Route> _history = new();
    readonly List<Route> _forwardHistory = new();
    readonly HistoryStore _historyStore = new();
    readonly SessionSnapshotStore _session = new();
    readonly NavPreviewStore _navPreview = new();   // click→detail handoff: the card stashes its known cover/title/artist
    readonly HomeSectionPreviewStore _homeSectionPreview = new(); // Home source section → seeded drill page
    readonly Wavee.Features.Browse.BrowseDirectoryStore _browseDirectory = new(); // Browse's two loads, surviving KeepAlive eviction (T11)
    readonly Wavee.Features.Browse.BrowsePageStore _browsePages = new(); // Browse CATEGORY pages, keyed by pageUri (G1)
    // Page-scoped shell MATERIAL: a page writes its art colour (flat tint) or Home's three washes here while active; the
    // shell paints it as the one layer between the window's base layer (live Mica) and the chrome column. Null tint +
    // null wash ⇒ the bare base. Owner-gated writes (ShellMaterialState) make A→B navigation race-free. Provided at the root via
    // ShellMaterial.Slot; rendered by ShellMaterialLayer.
    readonly Signal<ShellMaterialState> _shellMaterial = new(default);

    // Page-scoped shell MASTHEAD: route-keyed publications (no owner tokens). The band is the one renderer, mounted
    // once in ContentHost above the KeepAlive boundary. Registry decides which families show; the store holds dynamics.
    readonly ShellMastheadStore _shellMasthead = new();
    readonly NavOriginStore _navOrigins = new();

    // Right-rail (lyrics / queue / now-playing panels) UI state — created here, provided via ShellUi.Slot, and
    // toggled from the player bar. The rail reserves inline width when it fits; otherwise it floats over the content.
    readonly ShellUi _shellUi = new();

    // Whether the CURRENT (or next) transition of PlaybackBridge.VideoSurface into resolved Fullscreen was reached by
    // an explicit user action rather than an automatic reappearance — written by the rail↔video coupling effect
    // below, read once at mount by VideoFullscreenSurface (see that type's UserInitiated doc comment for why the
    // surface itself cannot tell the difference).
    readonly Signal<bool> _videoFullscreenUserOpen = new(true);

    // The signals-first action system's ambient service bag (Actions/ActionServices.cs): ONE reference-stable instance
    // provided at the root next to NavCtx; fields are refreshed each render, Overlay is bound inside the OverlayHost
    // subtree (ActionServicesOverlayBinder). Context, not ctor args — the component-props-freeze contract.
    readonly ActionServices _actions = new();

    // ── The shell-wide "drop a file to play it" target (P4) ──────────────────────────────────────────────────────────
    // Allocated ONCE (the PlaylistInlineEdit precedent: a per-render spec would churn the scene's drop-target column) and
    // hung on the full-bleed `tinted` layer, which is an ancestor of the whole chrome column. The engine hands a drop to
    // the DEEPEST accepting target under the pointer (DragDropContext.FindTarget), so the per-row .mp4 attach targets in
    // DetailTracks still win for a drop that lands on a track row; only drops on the rest of the window reach this.
    readonly Signal<bool> _fileDropOver = new(false);
    DropTargetSpec? _fileDrop;

    // The tab list + which tab is active (TabWorkspace, pure). `_selectedTab` is the strip's SelectedIndex cell — a
    // PROJECTION of _tabs.ActiveIndex re-asserted after every op (Apply), never the place a decision is read from: the
    // engine strip writes that cell before it raises OnSelectionChanged, so reading it back to ask "did the tab change"
    // always answered no. `_savedPins` is the PinnedRevision last written to settings (SyncPins).
    readonly TabWorkspace _tabs = new();
    int _savedPins;
    readonly Signal<int> _tabsVersion = new(0);
    readonly Signal<int> _selectedTab = new(0);

    // ── the MERGED chrome row (one 48-DIP TitleBar: nav + tabs · window-centred search · identity) ────────────────────
    // The row is priority-collapsed by a pure ladder (MergedChromeLayout) held here as a band-gated signal: it is
    // recomputed on every viewport move but only PUBLISHED when a stage flips, exactly like the two-row toolbar's old
    // ToolbarLayout. Everything the bar's islands read comes from these signals, never from a frozen ctor arg.
    readonly Signal<MergedChromeLayout> _chromeLayout = new(MergedChromeLayout.FromWidth(0f, 1));
    readonly Signal<int> _searchFocusRequest = new(0);      // Ctrl+F → omnibar caret (Ctrl+K opens the command palette)
    readonly Signal<bool> _paletteOpen = new(false);        // Ctrl+K command palette (WaveeCommandPalette)
    readonly Signal<bool> _searchFocused = new(false);
    readonly Signal<bool> _searchFlyoutOpen = new(false);
    readonly Signal<float> _tabNaturalExtent = new(ShellResponsiveLayout.ChromeTabMinW);
    MergedChromeRow? _chrome;
    TabStrip? _strip;

    readonly Signal<string> _searchText = new("");
    // Sidebar state. Collapsed and (once pinned by a drag) width are seeded by SidebarPreferences' own constructor, from
    // the ACTIVE design's keys, so the FIRST layout already uses the saved values — no startup animation; written back on
    // change via SaveSidebar, which delegates to that service.
    readonly IAppSettings _settings;
    readonly Signal<bool> _drawerExpanded = new(false);
    Signal<bool>? _narrowShellState;
    Signal<bool>? _narrowDrawerState;
    Signal<bool>? _presentedCompactState;
    // The sidebar's PANE STATE now belongs to SidebarPreferences (one owner for all three designs, per-design remembered
    // state, and the same instance the Settings page + customizer write). These two fields are ALIASES of that service's
    // signals, kept under their historical names so every binding/read below is unchanged — the shell binds them, it no
    // longer owns them. SwitchDesign writes a new VALUE into the same signal instances, never new signals, so every bound
    // prop stays live across a design switch.
    readonly SidebarPreferences _sidebar;
    readonly Signal<bool> _sidebarCompact;                      // == _sidebar.Collapsed (the user's collapse preference)
    readonly Signal<float> _sidebarWidth;                       // == _sidebar.Width (expanded width, drag-resizable, persisted)
    readonly Signal<bool> _sidebarDragging = new(false);       // ON during a seam drag → snaps all layout transitions (1:1 resize)
    // The width-pin latch lives on the service now (per DESIGN — pinning V3's width must not freeze Classic's ladder), so
    // the effect below reads _sidebar.WidthUserSet instead of a shell field.
    bool _navPaneTierSeeded;                                   // false until the first effect run with a real viewport width
    SidebarDesign _tierDesign;                                 // which design the tier ladder is currently seeded against
    readonly Signal<float> _sidebarFade = new(1f);             // content-opacity cue as a resize nears the collapse detent
    readonly Signal<float> _rightRailFade = new(1f);           // same cue on the right-rail left-seam splitter
    Action<float>? _requestTheme;                              // ambient ThemeControl.Request: live animated re-theme (captured in Render)

    // (The rail layout-defer lock that used to live here is gone — see the note in ShellUi. The projected path commits
    // the reserved width in one frame, so there is nothing to debounce, and deferring the breakpoints only guaranteed a
    // window where the wide column set was rendered into the narrow pane.)
    // Interactive grip drag owns geometry and therefore suppresses projection globally while the pointer is down. Rail
    // and collapse toggles must NOT use this gate: doing so cancels their own Reveal/FLIP tracks. Those commits use the
    // scoped SuppressDescendantTransitions contract on the projected shell containers instead.
    void SyncDragSuppression()
        => Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, _sidebarDragging.Peek());

    // Projected motion (see docs/plans/…prancy-otter). WAVEE_RAIL_BASELINE=1 is the A/B escape hatch: it selects the OLD
    // SizeMode.Reflow width-per-tick tracks (real layout every tick — the slow 16–45 ms path) so the rail probe can
    // measure the pre-fix baseline from the SAME build. Default (unset) = the projected Reveal path below.
    static readonly bool s_railBaseline = Diag.EnvFlag("WAVEE_RAIL_BASELINE");
    // WinUI SplitView's compact-inline pane spline (generic.xaml, ClosedCompactLeft <-> OpenInlineLeft). Wavee gives
    // the retained motion 300 ms rather than WinUI's 200 ms because the heavier media surface can otherwise consume
    // most of the authored duration in its commit frames and visually read as a snap.
    static readonly EasingSpec SplitViewPaneEase = EasingSpec.CubicBezier(0f, 0.35f, 0.15f, 1f);
    const float SplitViewPaneDurationMs = 300f;
    // Stable-frame anchor key for the content card's FLIP (see the row's MorphId + the card's RelativeTo). Not a Hero
    // participant key — the row never unmounts, so it never matches a mounting node and never triggers a connected fly.
    const string ContentRowMorphId = "shell.content-row";
    /// <summary>The one main-page silhouette. Route content may paint through it, but may not author another shape.</summary>
    internal static readonly CornerRadius4 ContentPaneCorners = new(Radii.Card, 0f, 0f, 0f);

    /// <summary>HOW A LEFT+TOP-ONLY BORDER IS DRAWN. The engine's <c>BorderWidth</c> is a single uniform SDF ring
    /// (<c>SceneRecorder.EmitBorderRing</c>) — there is no per-side thickness, and two 1-DIP strips cannot follow the
    /// 8-DIP top-left arc (they leave a visible notch exactly at the corner). So the bordered box is made ONE DIP LARGER
    /// on its right and bottom edges with this negative margin and parked inside a <c>ClipToBounds</c> parent of the real
    /// geometry: the ring is inset (it spans [0,bw] INSIDE the bounds), so the right and bottom strokes land entirely in
    /// the clipped-away DIP while the left and top strokes — and the full rounded corner between them — survive intact.
    /// One node, no notch, and the arc is the renderer's own.</summary>
    static readonly Edges4 StrokeOverhang = new(0f, 0f, -1f, -1f);

    /// <summary>The content region's stock separating stroke: 1px <c>Tok.StrokeCardDefault</c> on LEFT + TOP only, over
    /// the given silhouette. Paint-only and STATIC final geometry — it must not ride the content card's FLIP, or the
    /// region's edge would slide away from the sidebar it separates from.</summary>
    static Element ContentRegionStroke(CornerRadius4 corners) => new BoxEl
    {
        ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Margin = StrokeOverhang,
                BorderWidth = 1f,
                BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
                Corners = corners,
            },
        ],
    };

    // The sidebar collapse (56↔expanded) AND the content card's FLIP share ONE transition, so the pane's animating edge
    // and the card's left edge ease on identical dynamics (edge coherence). Reveal lays the subtree out at its FINAL size
    // immediately and eases only a clip window + a translate (compositor-only) — NO per-tick boundary relayout /
    // DirectWrite text re-shape (what made Reflow slow). Snapped 1:1 during a grip drag through the suppression arbiter
    // (ApplyProjections → SnapStructuralToLayout).
    static readonly LayoutTransition SidebarPaneAnim = s_railBaseline
        ? new(TransitionChannels.Size, TransitionDynamics.Tween(Motion.ControlFast, Easing.SmoothOut), SizeMode.Reflow)
        : new(TransitionChannels.Size | TransitionChannels.Position,
            TransitionDynamics.Tween(SplitViewPaneDurationMs, SplitViewPaneEase), SizeMode.Reveal,
            ExitDynamics: TransitionDynamics.Tween(SplitViewPaneDurationMs, SplitViewPaneEase),
            SuppressDescendantTransitions: true);

    // The content card FLIPs (Position|Size Reveal, SAME dynamics as the pane) so it absorbs the reserved-width shift when
    // the pane / rail spacer commit a new width. In the Reflow baseline the card carried NO transition (it re-tiled via
    // real layout every tick), so this is null there.
    static readonly LayoutTransition? ContentCardAnim = s_railBaseline ? null : new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(SplitViewPaneDurationMs, SplitViewPaneEase),
        SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(SplitViewPaneDurationMs, SplitViewPaneEase),
        SuppressDescendantTransitions: true);

    // The right-rail open/close. Projected: a Reveal slide (FLIP TranslateX + presented-width) on the rail OVERLAY, so the
    // panel slide-reveals under its own clip; the reservation spacer snaps its width 0↔RailWidth at commit (NO transition)
    // and the content card's FLIP absorbs the reserved shift. Baseline: BOTH the spacer and the overlay animated REAL
    // width via a critically-damped Reflow spring (the old double width track). Spring damping 1.0 ⇒ no overshoot.
    static readonly LayoutTransition? RailOverlayAnim = s_railBaseline
        ? new(TransitionChannels.Size, TransitionDynamics.Spring(0.22f, 1f), SizeMode.Reflow)
        : null;

    // The reservation spacer: animated (Reflow) in the baseline, SNAP (null) in the projected path.
    static readonly LayoutTransition? RailSpacerAnim = s_railBaseline
        ? new(TransitionChannels.Size, TransitionDynamics.Spring(0.22f, 1f), SizeMode.Reflow)
        : (LayoutTransition?)null;

    // The shell receives its persisted settings through the IAppSettings interface (provided by the composition root,
    // Services). It never sees the concrete store — no "ForUnpackaged"/registry/publisher detail leaks in here.
    static string HistoryFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "WaveeMusic", "history.json");
    static string SessionFilePath() => SessionSnapshotStore.DefaultPath();

    // Stress-probe nav seam (WAVEE_NAV_PROBE only): lets the WaveeNavProbe drive REAL navigation/theme/tab churn through
    // the same signals the chrome writes — no synthetic input, no reaching into private state. Inert in normal runs.
    internal static Action<string, string?>? ProbeNav;
    internal static Action<int>? ProbeRail;   // open the right rail in a given RailMode (screenshot probes)
    internal static Action<bool>? ProbeRailOpen;   // open/close the right rail (WAVEE_RAIL_PROBE perf probe)
    internal static Action? ProbeBack, ProbeForward, ProbeTheme;
    internal static Action<string>? ProbeOpenTab;
    internal static Action<string, string?, bool>? ProbeCardNav;   // replicate a Home-card click: (key, arg, doMorph=Hero fly)
    internal static Action<Wavee.Core.Album>? ProbeOpenAlbum;      // replicate a related-album card click: DetailNav.OpenAlbum (stash preview + nav)
    internal static Action<bool>? ProbeSidebarCompact;
    internal static Action? ProbeSidebarDragBegin, ProbeSidebarDragEnd;
    internal static Action<float>? ProbeSidebarDragWidth;
    internal static Action<int>? ProbeSidebarMode;   // switch the active sidebar DESIGN (WAVEE_SIDEBAR_MODE_SHOT)
    internal static Action<int>? ProbeSidebarDesign;     // alias of ProbeSidebarMode, named for the design shots (WAVEE_SIDEBAR_MODE_SHOT)
    internal static Action<int>? ProbeSidebarV3View;     // Library-V3 presentation: a SidebarV3View ordinal (WAVEE_SIDEBAR_V3_SHOT)
    internal static Action<int>? ProbeSidebarV3Filter;   // Library-V3 kind filter: a SidebarV3Filter ordinal (WAVEE_SIDEBAR_V3_SHOT)
    internal static Action<bool>? ProbeSidebarDrawer;    // open/close the real narrow overlay drawer
    internal static Func<SidebarPaneFrameSnapshot>? ProbeSidebarPaneFrame; // settled rendered-width invariant probe
    NodeHandle _sidebarPaneNode;
    // The shell's CONTENT region (chrome rows above, docked player bar below) — the scope of the drag-drop spotlight
    // scrim. Captured at realize + re-published whenever the region is re-arranged.
    NodeHandle _contentRegionNode;
    SceneStore? _contentScene;
    InputHooks? _inputHooks;

    public WaveeShell(IAppSettings settings, SidebarPreferences sidebar)
    {
        _settings = settings;
        RestorePinnedWorkspace();
        _session.Init(SessionFilePath());
        RestoreSessionNav();
        _sidebar = sidebar;
        // The service already seeded the ACTIVE design's pane triple in its own constructor (a dragged width verbatim, an
        // undragged one from that design's tier ladder at the pre-measure fallback), so the FIRST layout is already correct
        // and the viewport effect in Render commits the real tier without a visible step.
        _sidebarCompact = sidebar.Collapsed;
        _sidebarWidth = sidebar.Width;
        _tierDesign = sidebar.Design.Peek();
        _shellUi.RailWidth.Value = ShellResponsiveLayout.ClampRailWidth(settings.Get(WaveeSettings.ShellRailWidth));
        _shellUi.DockedVideoHeight.Value = ShellResponsiveLayout.ClampDockedVideoHeight(
            settings.Get(WaveeSettings.ShellDockedVideoHeight), _shellUi.RailWidth.Peek());
        if (_session.ShellSection is { } shell
            && shell.RailMode >= (int)RailMode.Lyrics && shell.RailMode <= (int)RailMode.Video)
        {
            _shellUi.RailOpen.Value = shell.RailOpen;
            _shellUi.Mode.Value = (RailMode)shell.RailMode;
        }

        // Inert probe (screenshot / UI iteration only): open the right rail to the Lyrics panel at startup.
        if (Diag.EnvFlag("WAVEE_LYRICS_OPEN") || Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE") || Diag.EnvFlag("WAVEE_IMMERSIVE_OPEN")) { _shellUi.RailOpen.Value = true; _shellUi.Mode.Value = RailMode.Lyrics; }
        // Inert probe (screenshot / UI iteration only): open the IMMERSIVE STAGE at startup. Deliberately a startup flag
        // rather than a WaveeNavProbe run-loop takeover, so the stage is captured through the app's own supported
        // `--screenshot` path — which quiesces the render thread before CaptureBgra (FluentApp.RunCore). A probe that
        // drives CaptureBgra itself races the render thread and dies on `capture.cmd.Reset failed: 0x80004005`.
        // Usage: WAVEE_IMMERSIVE_OPEN=1 ... -- --fake --width W --height H --screenshot out.png
        if (Diag.EnvFlag("WAVEE_IMMERSIVE_OPEN")) _shellUi.ImmersiveLyrics.Value = true;
        if (Diag.EnvFlag("WAVEE_NOWPLAYING_OPEN")) { _shellUi.RailOpen.Value = true; _shellUi.Mode.Value = RailMode.Details; }

        // WAVEE_STARTUP_BENCH belongs in this list even though it drives no navigation: the bench's "session restored"
        // mark is the first frame on which ProbeNav is non-null, so without the flag here that timing reads n/a forever.
        if (Diag.EnvFlag("WAVEE_NAV_PROBE") || Diag.EnvFlag("WAVEE_RESIZE_PROBE") || Diag.EnvFlag("WAVEE_CONN_STRESS") || Diag.EnvFlag("WAVEE_TRACKLIST_SHOT") || Diag.EnvFlag("WAVEE_HERO_SHOT") || Diag.EnvFlag("WAVEE_SHELF_SHOT") || Diag.EnvFlag("WAVEE_RAIL_SHOT") || Diag.EnvFlag("WAVEE_HOME_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_RAIL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_PROBE") || Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE") || Diag.EnvFlag("WAVEE_MEM_SOAK") || Diag.EnvFlag("WAVEE_PERF_BENCH") || Diag.EnvFlag("WAVEE_STARTUP_BENCH") || Diag.EnvFlag("WAVEE_SIDEBAR_MODE_SHOT") || Diag.EnvFlag("WAVEE_SIDEBAR_V3_SHOT") || Diag.EnvFlag("WAVEE_SIDEBAR_VISUAL_SHOT"))
        {
            ProbeNav = GoNav; ProbeBack = Back; ProbeForward = Forward; ProbeTheme = ToggleTheme; ProbeOpenTab = OpenNewTab;
            ProbeRail = m => { _shellUi.RailOpen.Value = true; _shellUi.Mode.Value = (RailMode)m; };
            ProbeRailOpen = open => { _shellUi.RailOpen.Value = open; };
            // Exactly the Home-card path: stash a preview (→ DetailShell mounts the PREVIEW path, not the skeleton path the
            // sidebar nav hits) + fire the Hero-fly morph, then navigate — so the probe can reproduce the card-click transition.
            ProbeCardNav = (key, arg, doMorph) =>
            {
                if (!Diag.EnvFlag("WAVEE_PB_NOPREVIEW") && key.StartsWith("pl:", System.StringComparison.Ordinal))
                    _navPreview.Set(key, DetailPreview.FromPlaylist(new Wavee.Core.PlaylistSummary(key.Substring(3), arg ?? "Playlist", "", 0, null)));
                GoNav(key, arg);
            };
            // The EXACT related-album-card path (DetailTrailing → h.OpenAlbum → DetailNav.OpenAlbum): stash the card's
            // partial model + fire the fly, then nav. Lets the probe measure album→album on the post-fix (in-place) path.
            ProbeOpenAlbum = a => DetailNav.OpenAlbum(_navPreview, GoNav, a);
            ProbeSidebarCompact = compact =>
            {
                _sidebarCompact.Value = compact;
                _sidebarFade.Value = 1f;
                SaveSidebar();
            };
            ProbeSidebarDragBegin = () =>
            {
                _sidebarDragging.Value = true;
                SyncDragSuppression();
            };
            ProbeSidebarDragWidth = width =>
            {
                _sidebarCompact.Value = false;
                _sidebarWidth.Value = Math.Clamp(width, ShellResponsiveLayout.NavPaneMinW, ShellResponsiveLayout.NavPaneMaxW);
                _sidebarFade.Value = 1f;
            };
            ProbeSidebarDragEnd = () =>
            {
                _sidebarFade.Value = 1f;
                SaveSidebar(widthUserSet: true);   // the probe replicates a real drag commit, including the width-pin edge
                _sidebarDragging.Value = false;
                SyncDragSuppression();
            };
            // Drive a real design switch through the real service (snapshot/restore + remount), not by poking a signal:
            // the mode-shot probe must exercise the same path the Settings picker does.
            ProbeSidebarMode = mode => _sidebar.SwitchDesign(SidebarDesignInfo.FromInt(mode));
            ProbeSidebarDesign = ProbeSidebarMode;   // one hook, two names: the design shots read better against SidebarDesign
            // The Library-V3 view state goes through the same PERSISTING setters the sort/view flyout and the filter chips
            // call — not a bare signal write — so a shot reflects what the mode actually renders after a real user change.
            ProbeSidebarV3View = view => _sidebar.SetV3View(view);
            ProbeSidebarV3Filter = filter => _sidebar.SetV3Filter(filter);
            ProbeSidebarDrawer = open => _narrowDrawerState?.SetIfChanged(open);
            ProbeSidebarPaneFrame = ReadSidebarPaneFrame;
        }

        _historyStore.Init(HistoryFilePath());
        _historyStore.LoadFromDisk();

        _historyStore.Add(_route.Peek());   // record this session's first visit
    }

    void RestorePinnedWorkspace()
    {
        var snapshot = WorkspaceTabsPersistence.Decode(_settings.Get(WaveeSettings.WorkspacePinnedTabs));
        var active = _tabs.RestorePinned(snapshot, NavRouteNormalizer.Apply);
        _selectedTab.Value = _tabs.ActiveIndex;
        _route.Value = active;
        SyncOmnibarToRoute(active);
        SeedTabExtent();
        _savedPins = _tabs.PinnedRevision;   // what was just decoded IS the saved state
    }

    /// <summary>Cold-start nav restore: AFTER settings + pinned tabs. Fail-soft — a bad snapshot never breaks boot.
    /// Playback restore is a separate later consumer of the same document.</summary>
    void RestoreSessionNav()
    {
        try
        {
            var snap = _session.Load();
            var back = new List<SessionRouteDto>();
            var fwd = new List<SessionRouteDto>();
            if (!SessionSnapshotStore.TryApplyNav(snap?.Nav, back, fwd, out var active, out int tabId))
                return;
            _history.Clear();
            _forwardHistory.Clear();
            for (int h = 0; h < back.Count; h++)
            {
                var r = NavRouteNormalizer.Apply(back[h].Name, back[h].Arg);
                _history.Add(r);
                _navOrigins.Restore(r.Name, r.Arg, OriginOf(back[h]));
            }
            for (int h = 0; h < fwd.Count; h++)
            {
                var r = NavRouteNormalizer.Apply(fwd[h].Name, fwd[h].Arg);
                _forwardHistory.Add(r);
                _navOrigins.Restore(r.Name, r.Arg, OriginOf(fwd[h]));
            }
            var activeRoute = NavRouteNormalizer.Apply(active.Name, active.Arg);
            _navOrigins.Restore(activeRoute.Name, activeRoute.Arg, OriginOf(active));
            _route.Value = activeRoute;
            _canBack.Value = _history.Count > 0;
            _canForward.Value = _forwardHistory.Count > 0;
            SyncOmnibarToRoute(_route.Peek());
            // Re-select the session's tab when it survived (a pin) and put the restored route on the active tab in
            // place — without SavePinnedWorkspace: restore must not rewrite pins.
            _tabs.TrySelect(tabId);
            _tabs.SetActiveRoute(activeRoute);
            _selectedTab.Value = _tabs.ActiveIndex;
            _savedPins = _tabs.PinnedRevision;
            SeedTabExtent();
        }
        catch
        {
            _history.Clear();
            _forwardHistory.Clear();
            _canBack.Value = false;
            _canForward.Value = false;
            _route.Value = new Route("home");
        }
    }

    void SavePinnedWorkspace()
    {
        var snapshot = _tabs.PinnedSnapshot();
        _settings.Set(WaveeSettings.WorkspacePinnedTabs,
            WorkspaceTabsPersistence.Encode(snapshot.Tabs, snapshot.LastSelected));
        _savedPins = _tabs.PinnedRevision;
    }

    /// <summary>Persist the pins exactly when the workspace says they changed — one settings write per real change,
    /// none for a switch between session-only tabs.</summary>
    void SyncPins()
    {
        if (_tabs.PinnedRevision != _savedPins) SavePinnedWorkspace();
    }

    /// <summary>Publish the content region's absolute rect as the engine's drop-spotlight scrim scope. Idempotent and
    /// cheap (one scalar write); called from realize + every re-arrange of the region.</summary>
    void PublishScrimClip()
    {
        if (_contentScene is not { } sc || _contentRegionNode.IsNull || !sc.IsLive(_contentRegionNode)) return;
        RectF r = sc.AbsoluteRect(_contentRegionNode);
        sc.SpotlightScrimClip = r.IsEmpty ? null : r;
    }

    SidebarPaneFrameSnapshot ReadSidebarPaneFrame()
    {
        bool presented = _presentedCompactState?.Peek() ?? _sidebarCompact.Peek();
        float rendered = 0f;
        if (!_sidebarPaneNode.IsNull && _inputHooks?.GetNodeRect is { } rectOf)
            rendered = rectOf(_sidebarPaneNode).W;
        return new SidebarPaneFrameSnapshot(
            _sidebar.Design.Peek(),
            _sidebarCompact.Peek(),
            presented,
            _sidebarWidth.Peek(),
            rendered,
            presented ? 0f : 1f,
            presented ? 1f : 0f,
            ExpandedHitTestVisible: !presented,
            RailHitTestVisible: presented);
    }

    // widthUserSet is the DRAG-COMMIT edge only: it pins the width as a preference so the responsive tier ladder stops
    // writing it. The collapse toggle also persists through here and must leave the flag alone — collapsing the pane is
    // not a width choice, and pinning on it would freeze every user at whatever tier they happened to collapse from.
    // Both arms now DELEGATE to SidebarPreferences, which owns the per-design keys and the clamp; the shell no longer
    // touches the (legacy, migration-only) global sidebar.width / sidebar.collapsed keys.
    void SaveSidebar(bool widthUserSet = false)
    {
        if (widthUserSet) _sidebar.CommitWidthDrag(_sidebarWidth.Peek());
        else _sidebar.SetCollapsed(_sidebarCompact.Peek());
    }

    void CommitSidebarDrag() => SaveSidebar(widthUserSet: true);
    void CommitRailDrag()
    {
        float w = ShellResponsiveLayout.ClampRailWidth(_shellUi.RailWidth.Peek());
        _shellUi.RailWidth.Value = w;
        _settings.Set(WaveeSettings.ShellRailWidth, w);
    }

    void ToggleSidebar()
    {
        if (_narrowShellState?.Peek() == true)
        {
            if (_narrowDrawerState is { } drawer) drawer.Value = !drawer.Peek();
            return;
        }

        bool compact = !_sidebarCompact.Peek();
        _sidebarCompact.Value = compact;
        _presentedCompactState?.SetIfChanged(compact);
    }

    void CloseNarrowDrawer() => _narrowDrawerState?.SetIfChanged(false);

    public override Element Render()
    {
        _inputHooks = UseContext(InputHooks.Current);
        _contentScene = Context.Scene;   // captured at render: PublishScrimClip runs from layout, outside any render context
        _requestTheme = UseContext(ThemeControl.Request);   // host's live re-theme trigger (animated in-place; no remount)
        // Float the auto-mounted toast lane ABOVE the fixed bottom player bar (idempotent static write — the
        // ToastHost-registration idiom): reserve the player-bar height on the docked (bottom) edge.
        Toast.EdgeInset = WaveeSize.PlayerBarH;
        // The shell's content lives in the OverlayHost ZStack, which deliberately lets its child OVERFLOW (a tall popup must
        // not be clipped to the window). For the page CONTENT that means a tall page (a Detail rail is ~600px and does not
        // scroll) sizes the whole column to its content (~827px) and overflows the 760px window — shoving the fixed player
        // bar off the bottom (the "player bar disappears / slides" glitch). Pin the column's height to the LIVE viewport (a
        // BOUND prop, not a stale literal — it re-fires on resize) so the column is exactly window-tall and its Shrink=1 /
        // MinHeight=0 content region yields instead of overflowing. UI-thread signal; the binding re-lays-out on resize.
        var vpSig = UseContextSignal(Viewport.Size);
        var narrowShell = UseSignal(ShellResponsiveLayout.NarrowFor(
            vpSig.Peek().Width, current: false, initialized: false));
        var narrowDrawer = UseSignal(false);
        var presentedCompact = UseSignal(narrowShell.Peek() || _sidebarCompact.Peek());
        _narrowShellState = narrowShell;
        _narrowDrawerState = narrowDrawer;
        _presentedCompactState = presentedCompact;

        // Width is hot while the OS resize loop is active, but these structural signals only write when a hysteretic
        // band flips. Interactive window sizing suppresses layout projection, so the rail tracks the pointer 1:1.
        UseSignalEffect(() =>
        {
            bool current = narrowShell.Peek();
            bool next = ShellResponsiveLayout.NarrowFor(vpSig.Value.Width, current, initialized: true);
            if (next == current) return;
            narrowShell.Value = next;
            if (!next) narrowDrawer.SetIfChanged(false);
        });
        // The nav-pane's responsive DEFAULT width (stock OpenPaneLength-per-window-class). Same band discipline as the
        // narrow-shell effect above: it reads the hot viewport width but only writes when a hysteretic tier flips, and it
        // is never debounced (a deferred structural width is a frame rendered at the wrong tier — ShellUi's note).
        // Three writers must never fight over the pane width, so this one yields to both of the others:
        //   • a pinned width — SidebarPreferences.WidthUserSet (per design), latched by the drag commit, is a permanent opt-out;
        //   • a live drag — Peek-gated (no subscription): OnMove owns the width 1:1 while the pointer is down. A drag ends
        //     with SaveSidebar(widthUserSet: true), so after any committed drag this effect is inert anyway.
        // vpSig AND Design are read FIRST and unconditionally: an early return before either read would drop that
        // subscription and the tier would never update again. Subscribing to Design is what makes a design switch RE-SEED
        // the ladder against the incoming design's tier set (Classic 240/280/320 · V3 300/340/380 · Curated 280/320/360)
        // instead of holding the outgoing design's tier. The pane's SidebarPaneAnim animates whatever this commits, for free.
        UseSignalEffect(() =>
        {
            float w = vpSig.Value.Width;
            var design = _sidebar.Design.Value;
            if (design != _tierDesign) { _tierDesign = design; _navPaneTierSeeded = false; }
            _sidebar.SetViewportWidth(w);   // the service needs the live viewport to tier-seed an incoming design
            if (_sidebar.WidthUserSet || _sidebarDragging.Peek()) return;
            float next = ShellResponsiveLayout.NavPaneDefaultFor(w, _sidebarWidth.Peek(), _navPaneTierSeeded, _sidebar.Tiers);
            if (w > 0f) _navPaneTierSeeded = true;
            _sidebar.SetResponsiveWidth(next);
        });
        UseSignalEffect(() =>
            presentedCompact.SetIfChanged(narrowShell.Value || _sidebarCompact.Value));
        bool compact = _sidebarCompact.Value;    // subscribe → re-persist on a collapse/expand toggle (infrequent)
        bool dragging = _sidebarDragging.Value;  // subscribe → snap all layout transitions while resizing the sidebar
        // Persist the collapse toggle here; the grip's drag-end (OnReleased → CommitSidebarDrag) persists the width AND
        // pins it as a user choice. This path must stay on the flag-free overload. The initial values are seeded at field
        // construction (below) so the first layout is already correct. Defensive: storage failures never touch the UI.
        UseEffect(() => SaveSidebar(), compact);
        // Keep an owner-safe suppression edge in the reactive lifecycle as cleanup insurance. This snaps geometry only;
        // the user's reduced-motion preference and non-layout feedback remain untouched.
        UseEffect(() => SyncDragSuppression(), dragging);

        // Session chrome, not a durable preference: reopening the app should restore the right rail exactly as it was,
        // including the Video mode shown beside the artist page in the user's repro. The store equality-gates the mount
        // run after it adopted the snapshot, so only real open/close or mode changes schedule a save.
        UseSignalEffect(() =>
            _session.UpdateShell(_shellUi.RailOpen.Value, (int)_shellUi.Mode.Value));

        // Rail viewport-fit + layout-defer (off-render, auto-tracking effects — the render body stays subscription-free
        // so the shell isn't re-run on every resize pixel; only the rail band / pages re-solve from the signals below).
        var post = UsePost();

        // Refresh the (reference-stable) ActionServices bag — plain field writes on the same instance, so the
        // Ctx.Provide below never churns its consumers. Overlay is bound by ActionServicesOverlayBinder (inside the
        // OverlayHost subtree, where the REAL service lives).
        _actions.Playback = UseContext(PlaybackBridge.Slot);
        _actions.Library = UseContext(LibraryBridge.Slot);
        _actions.Svc = UseContext(Services.Slot);
        _actions.Store = UseContext(LibraryStore.Slot);
        // Jump List was built at PlaybackBridge.Activate (window exists) — attach the shell's history + the warm
        // library so "Jump back in" can print real names instead of "Playlist" / a raw URI.
        _actions.Playback?.JumpList?.AttachHistory(_historyStore);
        _actions.Playback?.JumpList?.AttachLibrary(_actions.Store);
        _actions.Clipboard = UseContext(InputHooks.Current).Clipboard;
        _actions.Go = GoNav;
        _actions.Post = post;
        // The update toast's "What's new" action navigates through the shell; the bridge has no nav of its own.
        if (_actions.Svc?.Notifications is { } notificationCenter) notificationCenter.Navigate = GoNav;
        _actions.VideoOverrides = _actions.Svc?.VideoOverrides;
        _actions.Sidebar = _sidebar;   // the pin store behind Pin/Unpin to sidebar (reference-stable → never churns consumers)
        _actions.CurrentRoute ??= () => _route.Peek().Name;   // the ActiveRoute target-mode resolver (Peek: invoke-time read, not a render dep)
        _actions.CurrentDestination ??= CurrentSidebarDestination;
        if (_actions.Extensions is null)
        {
            // The extension registry, built ONCE per shell: the first-party "wavee" actions + the nine sidebar data
            // sources land in one enumerable catalog (the customizer's action/section pickers read it in order).
            var registry = WaveeExtensionRegistry.Build(_actions);
            _actions.Svc?.RegisterSidebarSources(registry);
            _actions.Extensions = registry;
        }

        // Deep-link drain: Pending is a monotonic ticket (same shape as _searchFocusRequest). Read .Value so this
        // auto-tracked effect re-runs; the first tick also drains verbs posted before the shell mounted.
        UseEffect(() =>
        {
            _ = DeepLinkChannel.Pending.Value;
            DrainDeepLinks();
        });
        // Track-boundary announce: CurrentTrack only writes on a real change (not per-frame). Compose the string on
        // that edge; skip when no AT client is listening so we don't allocate a spoken line nobody hears.
        UseEffect(() =>
        {
            if (_actions.Playback is not { } playback) return;
            var track = playback.CurrentTrack.Value;
            if (track is null || !Announcer.IsAvailable) return;
            string title = track.Title;
            if (string.IsNullOrEmpty(title) || title == track.Uri) return;
            string artist = track.Artists.Count > 0 ? track.Artists[0].Name : "";
            Announcer.SayThrottled(artist.Length == 0 ? title : title + ", " + artist);
        });
        // Best-effort session flush on shell unmount. Process-exit Flush lives in Program.cs.
        UseEffect(() => (Action?)(() => _session.Flush()), DepKey.Empty);
        // The crash notice used to live here (a sticky toast reading PendingCrashReport off settings). It moved to
        // Program.cs (RunMarker + CrashPromptPolicy.Decide, latched into CrashPromptPolicy.ThisLaunch before the app
        // loop even starts) and ReportChrome (which consumes that latch and opens the modal report prompt, or the
        // opted-out toast) — see the ReportChrome mount below.
        // Mouse side buttons / keyboard Back-Forward keys arrive as an OS COMMAND (WM_APPCOMMAND), not as a click at a
        // position, so they cannot be an Accelerator box like Alt+Left/Right — they come in through the PAL seam and land
        // on the same Back()/Forward() the chord boxes use. Subscribed once for the shell's lifetime.
        UseEffect(() =>
        {
            Action<int> onNav = which => { if (which == 0) Back(); else Forward(); };
            FluentApp.AppNavigationCommand += onNav;
            return () => FluentApp.AppNavigationCommand -= onNav;
        }, DepKey.Empty);
        // Ctrl+mouse-wheel zoom (the browser gesture). The dispatcher invokes InputHooks.ZoomWheel for a Ctrl+wheel
        // only AFTER element-level OnPointerWheel handlers declined it and BEFORE the viewport scrolls, and the Win32
        // backend synthesizes pinch-zoom from Ctrl + hi-res/touchpad scroll before events ever reach the dispatcher —
        // so this hook only sees detented mouse wheels, exactly the population that wants discrete ladder steps.
        // Installed once for the shell's lifetime (the AppNavigationCommand shape above); cleared on unmount only if
        // still ours (the drawer's KeyPreview restore rule), since ZoomWheel is a single slot, not an event.
        UseEffect(() =>
        {
            var hooks = _inputHooks;
            Func<float, bool> zoomWheel = notch => { ZoomStep(notch > 0f ? +1 : -1); return true; };
            if (hooks is not null) hooks.ZoomWheel = zoomWheel;
            return () => { if (hooks is not null && ReferenceEquals(hooks.ZoomWheel, zoomWheel)) hooks.ZoomWheel = null; };
        }, DepKey.Empty);
        // The row indicator / "Videos only" filter read the association plane + the curation through a process-wide
        // probe rather than context, because they run per ROW (a context read or a signal subscription per row is not
        // affordable there). Both halves of the has-video answer are attached here, and nothing else answers it.
        VideoPresence.Attach(_actions.VideoOverrides, _actions.Svc?.RealStore);
        // The two override toasts' "Manage" button + the Settings roster deep-link: bump the request counter (the
        // PlaybackRuntimeBanner precedent — Settings has no route-arg tab deep-link) and navigate.
        if (_actions.Playback is { } pb && pb.OpenVideoOverrideManager is null)
            pb.OpenVideoOverrideManager = _ =>
            {
                pb.OpenVideoOverrides.Value = pb.OpenVideoOverrides.Peek() + 1;
                GoNav("settings", null);
            };
        // Maintain ShellUi.RailFits from the live viewport/sidebar/rail widths. The rail no longer auto-closes on a
        // fits-flip — it switches between inline (spacer reserves width) and floating (overlay only). Peek-guarded so
        // this never re-triggers.
        UseSignalEffect(() =>
        {
            float vpW = vpSig.Value.Width;
            float sbW = presentedCompact.Value ? ShellResponsiveLayout.CompactRailW : _sidebarWidth.Value;
            bool fits = ShellUi.CanFitRail(vpW, sbW, _shellUi.RailWidth.Value);
            _shellUi.RailFits.SetIfChanged(fits);

            // ── the video host-capability report (docked-video plan §3.4) ────────────────────────────────────────────
            // An INPUT to VideoUpgradeGate.AvailabilityFor's intersection, never chrome state — this effect only
            // REPORTS what the host can do right now. Docked rides the SAME rail-fit test above (a docked card needs
            // exactly the room the rail needs); Floating has no host precondition; Detached/Fullscreen come from the
            // live InputHooks, because this is the shell's one seam onto them (Services.cs's ctor runs before AppHost
            // wires ANY hook, so its own seed stays permissive rather than duplicating this test). A null
            // CanOpenDetachedWindow means the platform never wired the preflight, not that it refuses — treat that as
            // available so a platform without one keeps working exactly as it did before capability existed.
            if (_actions.Playback is { } capBridge)
            {
                bool detached = _inputHooks?.CanOpenDetachedWindow?.Invoke() ?? true;
                bool fullscreen = _inputHooks?.WindowSetFullscreen is not null;
                // DOCKED has TWO independent suppliers, OR-ed (DockedVideoHosting.DockedHostAvailable): the RAIL can
                // host a docked card when the rail fits — the `fits` test just above, unchanged — and a module WATCH
                // PAGE can always host one, because its stage is full-width page content that needs no rail at all.
                // Before the OR, narrowing the window demoted a watch page's in-page video to the floating mini
                // player: an overlay to dismiss, over the very page whose purpose was to show that video.
                bool pageStage = DockedVideoHosting.PageStageHosts(
                    _shellUi.ActiveStagePlayable.Value, capBridge.CurrentTrack.Value?.Uri);   // subscribe → nav + track
                var cap = (DockedVideoHosting.DockedHostAvailable(fits, pageStage) ? PlacementSet.Docked : PlacementSet.None)
                        | PlacementSet.Floating
                        | (detached ? PlacementSet.Detached : PlacementSet.None)
                        | (fullscreen ? PlacementSet.Fullscreen : PlacementSet.None);
                capBridge.HostPlacementCapability.SetIfChanged(cap);
            }
        });

        // ── §3.5 / B1, B6, B7, B13-B14 — the rail ↔ docked-video coupling, and the fullscreen focus guard ────────────
        // ONE effect over the two independent drivers (ShellUi.RailOpen, PlaybackBridge.VideoSurface) — never in
        // render. Put here (not at the scattered rail open/close write sites in RightRail/PlayerBar) because reacting
        // to the RESULT of those writes is exactly what a signals-first coupling is for: nothing else needs to know
        // this rule exists.
        var railOpenWas = UseRef(_shellUi.RailOpen.Peek());
        var videoStateWas = UseRef(_actions.Playback?.VideoSurface.Peek() ?? PlacementState.Initial(PlacementPolicy.Video));
        // WHICH docked host owned the surface on the previous run. Kept as an edge like the two above rather than
        // re-derived, because the rules that need it fire AFTER the video has already left the dock — at which point
        // the derivation unconditionally reads Rail (nothing is docked, so the rail is the resting owner) and would
        // answer a question about a body that was never on screen.
        var hostWasRef = UseRef(DockedVideoHost.Rail);
        UseSignalEffect(() =>
        {
            if (_actions.Playback is not { } pb) return;
            bool railOpen = _shellUi.RailOpen.Value;
            var state = pb.VideoSurface.Value;   // subscribe

            bool wasOpen = railOpenWas.Value;
            var prev = videoStateWas.Value;
            railOpenWas.Value = railOpen;
            videoStateWas.Value = state;

            // The ONE derived host (DockedVideoHosting.HostFor): the rail's card, or a module watch page's in-page
            // stage. Never claimed and never stored — a page that is navigating away is exit-frozen in the same
            // reconcile pass as the route change, and a parked keep-alive page is skipped outright, so neither can
            // re-render to hand a claim back. Every rule below that used to assume "docked ⇒ the rail" now takes it.
            var host = DockedVideoHosting.HostFor(
                PlacementCore.Resolve(state), _shellUi.ActiveStagePlayable.Value, pb.CurrentTrack.Value?.Uri);
            var hostBefore = hostWasRef.Value;
            hostWasRef.Value = host;

            // B1 — docking was just requested (Requested transitioned INTO Docked, an explicit ShowVideoAt): open the
            // rail into Video mode, but only when it was closed — ModeOnDock's own null leaves an already-open rail
            // showing whatever it was showing.
            if (state.Requested == SurfacePlacement.Docked && prev.Requested != SurfacePlacement.Docked
                && RailVideoCoupling.ModeOnDock(railOpen, _shellUi.Mode.Peek(), host) is { } dockMode)
            {
                _shellUi.Mode.Value = dockMode;
                _shellUi.RailOpen.Value = true;
            }

            // B6 — the user closed the rail while a video was docked: demote to Floating so playback continues.
            // Preferred stays untouched (Demote's whole point), so re-opening the rail re-docks (B7).
            if (!railOpen && wasOpen)
            {
                var demoteTo = RailVideoCoupling.OnRailClosed(state, host);
                if (demoteTo != SurfacePlacement.None) pb.DemoteVideoTo(demoteTo);
            }

            // B7 — the rail was re-opened: only re-dock when Preferred says the rail is what took it away, never when
            // the user deliberately chose the mini player (that write sets Preferred = Floating).
            if (railOpen && !wasOpen && RailVideoCoupling.ReDockOnRailOpen(state))
                pb.ShowVideoAt(SurfacePlacement.Docked);

            // B13/B14 — the EXPLICIT intent left the dock (turned off, or moved to another placement) while the rail
            // was showing the video-first body: that body has nothing left to show, so close the rail. Keyed on
            // Requested (not Resolve), so an AVAILABILITY-only drop — B10, this track simply has no video — leaves
            // the rail open per the empty-state precedent instead of thrashing it closed every track boundary.
            bool videoLeftDock = prev.Requested == SurfacePlacement.Docked && state.Requested != SurfacePlacement.Docked;
            if (RailVideoCoupling.CloseRailOnVideoLeft(_shellUi.Mode.Peek(), videoLeftDock, hostBefore))
                _shellUi.RailOpen.Value = false;

            // ── §5.4 — user-initiated vs automatic fullscreen entry (the focus-steal guard) ──────────────────────────
            // VideoFullscreenSurface reads _videoFullscreenUserOpen ONCE at its own mount to decide whether to take
            // focus. Computed HERE, not there, because that surface remounts fresh every time Flow.Show toggles it
            // (no memory of its own history) — only the shell, which never unmounts, can tell "Requested just BECAME
            // Fullscreen" (an explicit ShowVideoAt(Fullscreen)/F11) apart from "Requested was ALREADY Fullscreen and
            // Resolve is only NOW catching up because availability came back" (the exact analogue of B12's "never
            // re-opened by a track change", for Fullscreen: a video-less interstitial track drops ALL availability —
            // including Fullscreen's — to None, and the next video track can re-open it with Requested never having
            // moved). Written exactly once per mount-transition (never left stale for the next one) so a later
            // automatic remount always reads the correct, freshly-computed answer.
            bool resolvedFullscreenNow = PlacementCore.Resolve(state) == SurfacePlacement.Fullscreen;
            bool resolvedFullscreenWas = PlacementCore.Resolve(prev) == SurfacePlacement.Fullscreen;
            if (resolvedFullscreenNow && !resolvedFullscreenWas)
                _videoFullscreenUserOpen.Value =
                    state.Requested == SurfacePlacement.Fullscreen && prev.Requested != SurfacePlacement.Fullscreen;
        });

        // ── merged chrome pressure projection ─────────────────────────────────────────────────────────────────────────
        // The strip reports its real natural content extent. That measurement decides only whether search is a full
        // field or the caption-adjacent icon; every tab remains mounted in the horizontal scroller.
        UseSignalEffect(() =>
        {
            float w = vpSig.Value.Width;
            _ = _tabsVersion.Value;
            float extent = _tabNaturalExtent.Value;
            var old = _chromeLayout.Peek();
            var next = MergedChromeLayout.Resolve(w, extent, old);
            if (old.SearchMode != next.SearchMode)
            {
                if (old.SearchMode == MergedSearchMode.Field && _searchFocused.Peek())
                    _searchFocusRequest.Value = _searchFocusRequest.Peek() + 1;
                else if (old.SearchMode == MergedSearchMode.Icon && _searchFlyoutOpen.Peek())
                    _searchFocusRequest.Value = _searchFocusRequest.Peek() + 1;
            }
            _chromeLayout.SetIfChanged(next);
        });
        // The row's island builders. Constructed once (it owns the measured-extent signals behind the centring guard);
        // its three ambient services are PLAIN FIELDS refreshed per render — a slot builder runs inside the BAR's render
        // and can never call UseContext itself, so the shell resolves them here.
        var chrome = _chrome ??= new MergedChromeRow(
            _canBack, _canForward, GoNav, Back, Forward,
            _searchText, ToggleTheme, _history, _forwardHistory,
            _chromeLayout, _searchFocusRequest, _searchFocused, _searchFlyoutOpen,
            TabStripHost, TabStripItemsVersion);
        chrome.Bridge = _actions.Playback;
        chrome.Ui = _shellUi;
        chrome.Acts = _actions;

        // FULLSCREEN VIDEO unmounts the shell's chrome. Not opacity-0, not off-screen: every layer left above a
        // full-screen video costs GPU (it defeats the composition fast path the video hole relies on), and the docked
        // player bar specifically is what STACKED a second transport under the video's own. Read as a Func (a
        // Flow.Show predicate) rather than in this render body, so the enter/exit edge mounts and unmounts the two
        // chrome bands WITHOUT re-rendering the whole shell — the same discipline the immersive-lyrics layer uses.
        // The predicate reads PlaybackBridge.VideoFullscreenActive, the ONE derived value (resolved placement ==
        // Fullscreen); there is deliberately no second "is fullscreen" flag anywhere in the shell.
        var pbChrome = _actions.Playback;
        bool ShellChromeMounted() => pbChrome is null || !pbChrome.VideoFullscreenActive.Value;

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f, Height = Prop.Of(() => vpSig.Value.Height),   // window-tall → content yields, never overflows the player bar
            OnKeyDown = OnShellKey,   // Space = play/pause after focused routing (editors/buttons consume first)
            Children =
            [
                // Zero-size, renders nothing: owns the ambient-cadence policy's two subscriptions (window activation +
                // the debounced power poll). It lives here because the shell is the one always-mounted host in the tree.
                Embed.Comp(() => new AmbientPowerPolicy.Watcher()),
                // Shell-wide chords. InputHooks.KeyPreview is modifier-BLIND (Func<int,bool>), so the shell verbs
                // ride the engine's KeyAccelerator seam instead — the dispatcher matches key+mods against any live,
                // visible, enabled node AFTER focused routing declines it (the WinUI ProcessKeyboardAccelerators
                // order). Zero-size + hit-test-free ⇒ pure keyboard surface, and it composes with the narrow drawer's
                // own Escape KeyPreview rather than fighting it for the single preview slot.
                // Bare Space is NOT an accelerator (OnKey only runs FindAccelerator for Ctrl/Alt or F1–F12) — it
                // bubbles here via OnKeyDown after editors/clickables have had first refusal.
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = NewTabChord, OnClick = () => OpenNewTab("home"),
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = FocusSearchChord, OnClick = OpenPalette,
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = FindChord, OnClick = FocusFind,
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = BackChord, OnClick = Back,
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = ForwardChord, OnClick = Forward,
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = VideoFullscreenChord, OnClick = ToggleVideoFullscreen,
                },
                // App zoom (Ctrl+± / Ctrl+0, top row and numpad — the chord statics explain why there are eight).
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = ZoomInChord, OnClick = () => ZoomStep(+1),
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = ZoomInShiftChord, OnClick = () => ZoomStep(+1),
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = ZoomInNumpadChord, OnClick = () => ZoomStep(+1),
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = ZoomOutChord, OnClick = () => ZoomStep(-1),
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = ZoomOutShiftChord, OnClick = () => ZoomStep(-1),
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = ZoomOutNumpadChord, OnClick = () => ZoomStep(-1),
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = ZoomResetChord, OnClick = () => ZoomStep(0),
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = ZoomResetNumpadChord, OnClick = () => ZoomStep(0),
                },
                // THE chrome row. One 48-DIP TitleBar in merged mode: the tabs island carries Wavee's nav cluster and
                // the text-first strip, the flexible centre column carries the window-centred omnibar, and the trailing
                // island carries identity. ContentVersion is mandatory here — see MergedChromeRow.ContentVersion.
                // UNMOUNTED under fullscreen video (ShellChromeMounted): the OS window is borderless on its monitor,
                // so a custom caption strip over the video would be chrome for a frame that no longer exists.
                Flow.Show(ShellChromeMounted, Embed.Comp(() =>
                {
                    var bar = new TitleBar
                    {
                        IconGlyph = "", ShowBackButton = false, ShowCaptionButtons = true,
                        // The HAMBURGER is the bar's own pane-toggle built-in, not a child of the tabs island. Two
                        // reasons, both from the first feel pass: it lands in the fixed lead column (centred where the
                        // 56-DIP compact rail's icons are, which a child of the island cannot reach — the island starts
                        // after a 16-DIP lead and clips anything shifted left of it), and it is reported as its OWN
                        // Client region, so the header pad between it and the tabs island stays real window-drag band.
                        ShowPaneToggle = true, OnPaneToggle = ToggleSidebar,
                        Parts = ChromeParts,               // the four-DIP nudge that lines it up with the rail's icons
                        ShowRailBaseline = false,          // this chrome has no rail; the seam below is the app's own
                        Tabs = chrome.Tabs,
                        TabsVersion = TitleBarTabsVersion,
                        TabsElasticLane = true,            // tabs absorb the overrun; the omnibar keeps its allocated width
                        Trailing = chrome.Trailing,
                        CaptionLeading = chrome.CaptionLeading,
                        // The row's own version covers its layout/auth terms; the shell folds in the ROUTE and the pin
                        // store's version so the trailing island's direct Pin/Unpin button (MergedChromeRow.PinButton,
                        // resolved from ActionServices.CurrentDestination at render) follows a navigation and a
                        // pin/unpin instead of showing the previous page's state until something else re-rendered.
                        ContentVersion = () => HashCode.Combine(chrome.ContentVersion(), _route.Value.Name, _route.Value.Arg,
                                                                _sidebar.PinsVersion.Value),
                    };
                    // Hand the island the bar's LIVE centre-column measurement (the merged-mode mirror of ContentAvail)
                    // so an expanding field can clamp itself without the bar re-rendering.
                    bar.CenterContent = _ => chrome.Center(bar.CenterAvail);
                    return bar;
                })),
                // NO chrome↔content seam hairline here. Stock Win11 (WinUI NavigationView + the WinUI-Gallery shell) draws
                // no bar-wide divider under the title bar: the separation IS the content region's own left+top stroke
                // (see ContentRegionStroke below), which starts exactly where the page starts. A full-width hairline
                // stacked a SECOND rule against that stroke over the page, ran straight across the sidebar band (which
                // owns no such edge in stock), and put the tab underline 6 DIP above a line it never belonged to.
                Ui.ZStack(
                    // The sidebar + content row. The sidebar PANE (SidebarPane) is the row's DIRECT child, so ITS width
                    // is what the row distributes — the content column re-solves and tiles against it gap-free. The width
                    // is signal-bound + drag-resizable (the grip overlay below); the pane animates the collapse toggle.
                    // No Fill on the row: the PLATE is painted once per band (the sidebar pane and the content-side
                    // backing below each paint it), so a row-level slab would sit UNDER those and double-tint them —
                    // the exact hazard the coincident content-pane double-composite used to have. Real-layout tiling
                    // keeps the seam gap-free with nothing behind.
                    new BoxEl
                    {
                        // ClipToBounds (Task B5): a settle-frame safety net so a page's content can never paint past the
                        // content card into the fixed rail band during the rail reveal. The card + page wrappers already
                        // clip; this bounds the row itself while the flex-shrink chain re-solves.
                        // MorphId (stable-frame anchor for the content card's FLIP): the row never moves on a sidebar
                        // toggle, so the content card FLIPs its slide RELATIVE to this frame (Element.RelativeTo below)
                        // instead of its own parent (which absorbs the reserved-width shift → a zero delta → the snap).
                        MorphId = ContentRowMorphId,
                        Direction = 0, Grow = 1f, ClipToBounds = true,
                        Children =
                        [
                            // The sidebar pane — a LITERAL row child (NOT a component): an Embed.Comp root mirrors its Grow
                        // onto the host node and grows HORIZONTALLY in the row (gap), whereas a literal child cross-
                        // stretches to full row height for free. Width is bound (compact rail / draggable expanded); the
                        // Reflow animates the collapse toggle but is null mid-drag so the pane tracks the cursor 1:1.
                        new BoxEl
                        {
                            // NO fill: the sidebar band is a paint-site OMISSION over the window's BASE LAYER (live
                            // Mica), exactly like the merged chrome row and the player dock. One uninterrupted base
                            // under all chrome is what kills the title-bar-vs-toolbar seam the translucent plate
                            // produced — and the reason the content region needs a stroke rather than a fill step to
                            // separate itself from this band.
                            Direction = 1, Shrink = 0f, ClipToBounds = true,
                            OnRealized = h => _sidebarPaneNode = h,
                            // A live DRAG PEEK presents the pane expanded, so this column must widen with it — it is
                            // ClipToBounds, and a 56-DIP column around expanded rows renders them as a strip of art +
                            // tree connectors with every label cut off (SidebarPreferences.DragPeek carries the why).
                            Width = Prop.Of(() => presentedCompact.Value && !_sidebar.DragPeek.Value
                                ? ShellResponsiveLayout.CompactRailW : _sidebarWidth.Value),
                            // SidebarPaneAnim eases the COLLAPSE toggle (56↔expanded) as a clip+translate reveal — the pane
                            // is ClipToBounds so the reveal scissors its content. During a drag the suppression arbiter
                            // snaps every layout transition (this pane AND the sidebar sections) to the laid-out width 1:1.
                            Animate = SidebarPaneAnim,
                            Children =
                            [
                                // Content fades (compositor-only) toward the collapse detent; the chrome fill stays solid.
                                // Column wrapper so the mode component's Grow=1f fills our HEIGHT (its ScrollView needs a
                                // definite one). SidebarHost is the ONE mount seam: it re-renders on a design switch and
                                // remounts the selected mode under a design-derived Key.
                                new BoxEl
                                {
                                    Direction = 1, Grow = 1f,
                                    Opacity = Prop.Of(() => _sidebarFade.Value),
                                    // LAYOUT FIREWALL for the whole sidebar. Every realized row wraps its content in a
                                    // ToolTip, so a sidebar publish marks dozens of layout-dirty nodes that each escape
                                    // to a full-tree relayout from the scene root — the dominant escape source in the
                                    // app. This box is the right level: it is INSIDE the pane, which owns the chrome
                                    // fill and the animated collapse reveal (a boundary must never sit on a box whose
                                    // own size animates), and it fills that pane exactly, so the clip Boundary() implies
                                    // is redundant with the pane's existing ClipToBounds and changes nothing. Its width
                                    // cross-stretches from the pane and its height is Grow=1 in the pane's column, so
                                    // neither axis can be content-sized by a descendant. Sidebar flyouts, context menus
                                    // and drag ghosts are not in this subtree (overlay host / hoisted bands).
                                    IsolateLayout = true, ClipToBounds = true,
                                    Children = [ Embed.Comp(() => new SidebarHost(_route, GoNav, presentedCompact, _sidebarWidth)) ],
                                },
                            ],
                        },
                            // Content side: the STOCK Win11 content region — ONE rectangle, flush on all four sides, filled
                            // with the LayerFillColorDefault rung and PAIRED with a 1px stroke on LEFT+TOP only, corner
                            // 8,0,0,0, and NO shadow. That triple (flush · one corner · stroke, no shadow) is what WinUI's
                            // own NavigationView content grid and the WinUI-Gallery shell paint over bare Mica; the
                            // separation comes from the stroke, never from a gutter or an elevation.
                            new BoxEl
                            {
                                // MinHeight=0 at every flex level of the content chain (see the card below) so a tall page
                                // can shrink/clip instead of overflowing the column and covering the docked player bar.
                                // Shrink=1 + MinWidth=0 are the HORIZONTAL analogue: FlexShrink defaults to 0 (Yoga-style),
                                // so without them this Grow=1 content region floors at its page's intrinsic min-width and
                                // CANNOT yield when the row (sidebar + content + right rail) overruns a narrow window — the
                                // fixed-width rail (Shrink=0) is then shoved off the right window edge and the Lyrics panel
                                // is clipped by a per-page amount (wide pages push it further ⇒ the "rail changes size / gets
                                // cut off depending on the page" instability). With them the content page is the ONE region
                                // that gives (it clips/scrolls), so the rail keeps its full RailWidth on every page.
                                Direction = 1, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
                                Children =
                                [
                                    // Static final-geometry underlay — the CONTENT LAYER of the Windows 11 Mica model
                                    // (learn.microsoft.com system-backdrops: Base Layer = bare Mica chrome, Content
                                    // Layer = a translucent smoke OVER the base). This is the stock WinUI
                                    // LayerFillColorDefault rung (WaveeColors.FileArea: #4C3A3A3A dark / #80FFFFFF
                                    // light) — it LIGHTENS the content region over live Mica so the page always reads
                                    // one step ABOVE the base, never darker (an opaque #282828 pane inverted the model
                                    // on light-tinted wallpapers: the page read as a black slab inside brighter
                                    // chrome). It is the only FILL painted in this region: the rounded top-left
                                    // cut-away is a paint-site omission that shows the base. Do not add a
                                    // sidebar-coloured seam strip here: it reads through the page as a full-height rail
                                    // and squares off the corner.
                                    //
                                    // NO SHADOW (stock). A drop shadow cast under a ~30%-alpha fill bleeds THROUGH it
                                    // and muddies the top and left of the page; zero stock Win11 shells elevate the
                                    // content layer. The region's separation is ContentRegionStroke below.
                                    // NO MARGIN either — the permanent trailing Spacing.S gutter is gone; the rail's
                                    // 8-DIP breathing room is now the bound gap box below, which exists only while the
                                    // rail is inline. A closed rail leaves the page flush to the window edge.
                                    new BoxEl
                                    {
                                        Grow = 1f,
                                        Fill = Prop.Of(() => WaveeColors.FileArea),
                                        Corners = ContentPaneCorners,
                                    },
                                    new BoxEl
                                    {
                                        // MinHeight=0 (the flex `min-height:0` override): this card CLIPS its content, so it
                                        // must be allowed to shrink BELOW the page's natural min-height. Without it, a tall
                                        // page (a Detail RAIL is ~600px and does not scroll) forces the content region past
                                        // the column height and PUSHES THE FIXED PLAYER BAR off the bottom for a frame on
                                        // navigation — the "player bar animates away then back" glitch. With it the card
                                        // shrinks to the available space and clips/scrolls, so the player bar stays docked.
                                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                                        // Flush on ALL FOUR sides (stock): against the navigation pane, the chrome row,
                                        // the player dock and — when it is open — the rail's own reservation gap.
                                        // NO fill: the static underlay above owns the pane surface. This card and that
                                        // underlay are coincident at rest (both Grow=1, both flush, same corner), so a
                                        // fill here composited the translucent FileArea TWICE — the pane
                                        // read a rung too light (#333333 instead of #303030 dark) and mid-slide the
                                        // moving card banded against the static surface. The card keeps the corner,
                                        // the clip and the FLIP; the paint and the stroke are both static siblings now.
                                        // (Regression tell: a dark pane sampling #333333 means this fill came back.)
                                        Fill = ColorF.Transparent,
                                        // NO border here either. The region's stroke is a STATIC final-geometry overlay
                                        // (ContentRegionStroke below) so the edge does not slide with the FLIPping card,
                                        // and so it can be LEFT+TOP only — a uniform ring on this node drew a stroke
                                        // along the player-dock seam and the rail seam that stock has no line on.
                                        // Stock NavigationViewContentGridCornerRadius = 8,0,0,0: only the corner facing
                                        // the nav pane rounds.
                                        Corners = ContentPaneCorners,
                                        ClipToBounds = true,
                                        // Layout firewall (#5): this card is Grow=1 (its size is the shell's content region,
                                        // parent-determined) and clips — so a re-render deep inside a page re-solves only this
                                        // subtree (RunSubtree) instead of a full-tree layout from the root on every nav.
                                        IsolateLayout = true,
                                        // Projected motion: the card carries a Position|Size Reveal so that when the sidebar
                                        // pane commits a new width, the card FLIP-translates from its OLD left edge to the new
                                        // one AND presented-size-reveals its width from old→new (CaptureProjections/ApplyProjections),
                                        // sliding the content sheet in lock-step with the pane's revealing edge. Size is required
                                        // alongside Position: Grow=1 makes the card's final width layout-driven, so a Position-only
                                        // FLIP would snap the width to final on frame 1 while only the left edge eased (the visible
                                        // "card width snaps" tear). Same dynamics as the pane (edge coherence); mirrors SidebarPaneAnim.
                                        // RelativeTo (CRITICAL): FLIP against the stable ROW frame, NOT the card's layout parent.
                                        // The card fills the content region, and that region absorbs the ENTIRE reserved-width
                                        // shift — so the card's PARENT-relative rect never changes (zero delta) and the default
                                        // FLIP would produce no slide: the sheet SNAPS to its final X while only the (occluded)
                                        // pane reveal eases behind it. Anchoring the FLIP to the row (which does not move on a
                                        // toggle) restores the real delta, so the card slides while the static L1/L2/L3 underlays
                                        // stay put to fill the trailing gap (the WinUI ContentTransform.TranslateX choreography).
                                        // Grow=1 ⇒ the final rect is layout-driven. Null in the Reflow baseline (real-layout tile).
                                        Animate = ContentCardAnim,
                                        RelativeTo = s_railBaseline ? null : ContentRowMorphId,
                                        Children = [ Embed.Comp(() => new ContentHost(_route, _navMotion, ActiveTabId, _settings)) ],
                                    },
                                    // The region's ONE separating treatment, topmost so a page can never paint over it:
                                    // a 1px Tok.StrokeCardDefault on LEFT + TOP only, following the 8-DIP top-left arc.
                                    ContentRegionStroke(ContentPaneCorners),
                                ],
                            },
                            // The rail's breathing room, and the ONLY gap in the row. It is part of the rail's
                            // RESERVATION, not a permanent trailing gutter on the page: with the rail closed the page
                            // runs flush to the window edge (stock), and opening the rail widens this from 0 to 8 in the
                            // same commit as the spacer below (both snap; the content card's FLIP absorbs the shift).
                            new BoxEl
                            {
                                Shrink = 0f, HitTestVisible = false,
                                Width = Prop.Of(() => _shellUi.RailOpen.Value && _shellUi.RailFits.Value ? Spacing.S : 0f),
                            },
                            // Right rail RESERVATION spacer — the WaveeMusic-style lyrics / now-playing band. A literal row
                            // child (Shrink=0, bound width) so the content card re-tiles against it. Projected motion: this
                            // width flips 0<->RailWidth at commit with NO Animate — it SNAPS to the reserved extent, and the
                            // content card's FLIP (SidebarReveal) absorbs the shift while the rail overlay slide-reveals into
                            // the reserved band. (Animating both the spacer AND the overlay was the old double width track.)
                            //
                            // Docked band coats collapse to ONE (content-pane twin): the underlay child below paints the
                            // single FileArea coat for the reserved band, and RightRail's own surface goes Transparent
                            // while docked and draws on it — instead of Toolbar + FileArea + RightRail's FileArea, three
                            // coats become one. Floating keeps FileArea over FloatingChrome (B2). The band's one stroke
                            // rides RightRail's own topmost edge, not this underlay.
                            new BoxEl
                            {
                                // NO fill on the spacer itself: the rail's rounded top-left wedge (and the 8-DIP gap
                                // before it) read against the window's BASE LAYER — live Mica — like every other
                                // paint-site omission in the chrome. The band's own rung is painted by its child below.
                                Shrink = 0f,
                                Width = Prop.Of(() => _shellUi.RailOpen.Value && _shellUi.RailFits.Value ? _shellUi.RailWidth.Value : 0f),
                                Animate = RailSpacerAnim,   // null (snap) in the projected path; Reflow spring in the baseline
                                Children =
                                [
                                    // Static final-geometry underlay — the rail-side twin of the content card's underlay
                                    // above, and the ONE coat of the docked band. The reserved width SNAPS at commit
                                    // while RightRail translates its panel in over 300ms; without this, that band is
                                    // bare Mica for the whole slide and the open reads as "the content jumps away from a
                                    // dark hole, then a panel arrives". Painting the rail's own surface here (same Fill +
                                    // same top-left card corner) means the band IS the rail from frame 0 and only the
                                    // panel's CONTENT is seen to arrive — which is exactly why RightRail paints
                                    // Transparent while docked: two coats of the same smoke is one rung too dark.
                                    // Paint-only: never a hit target, no opacity track (a fading full-height rail surface
                                    // is what produced the old "ghost rail").
                                    // The same Mica CONTENT-LAYER rung as the content card's underlay above — the rail
                                    // band and the page must be the SAME rung, or the seam between them reappears the
                                    // moment one composites over Mica and the other over flat paint.
                                    // NO stroke here: the band's single left+top hairline rides RightRail's own topmost
                                    // `edge` (an underlay stroke would double-draw against it). A stroke-less band for
                                    // the 300ms slide-in is fine — the edge lands with the panel.
                                    new BoxEl
                                    {
                                        Grow = 1f, HitTestPassThrough = true, ClipToBounds = true, ZStack = true,
                                        Children =
                                        [
                                            new BoxEl
                                            {
                                                Margin = StrokeOverhang,
                                                Fill = Prop.Of(() => WaveeColors.FileArea),
                                                Corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f),
                                            },
                                        ],
                                    },
                                ],
                            },
                        ],
                    },
                    // Resize-grip overlay: a narrow strip translated to the pane↔content seam. The overlay's own hit
                    // bounds are only the grip column, so sidebar wheel/hover routing and its scrollbar thumb still hit
                    // the sidebar ScrollView instead of a non-scrollable overlay branch.
                    new BoxEl
                    {
                        Width = Prop.Of(() => narrowShell.Value ? 0f : Splitter.StripW), Direction = 1, ClipToBounds = true,
                        Transform = Prop.Of(() => Affine2D.Translation(presentedCompact.Value
                            ? ShellResponsiveLayout.CompactRailW : _sidebarWidth.Value, 0f)),
                        Children =
                        [
                            // The strip is entirely on the content side of the seam to avoid covering the sidebar's
                            // 12-DIP scrollbar lane; Splitter's root Grow=1 fills this definite-height column.
                            // Issue #84 — NavPaneMinW dropped 240 → 180. Left at their engine defaults, FadeStart would
                            // fall to the new Min (180, no resist band left at all) and ForcePush would default to
                            // FadeDistance (44), collapsing at 136 — the pane would stop resisting well before the
                            // floor and only snap to the rail deep past it. Pinning FadeStart at the OLD floor (240)
                            // keeps the same dim-and-resist feel through 240 → 180 that shipped before this issue;
                            // ForcePush = 64 moves the collapse point to 240 − 64 = 176, just below the new 180 floor
                            // (the old geometry collapsed 44 DIP below its 240 floor — this keeps a matching feel
                            // scaled to the shorter 60-DIP resist span); ReExpand = 190 keeps the same ~14-DIP
                            // hysteresis band above the collapse point the old 196/210 pair had.
                            Splitter.Create(_sidebarWidth, CommitSidebarDrag, new()
                            {
                                Min = ShellResponsiveLayout.NavPaneMinW,
                                Max = ShellResponsiveLayout.NavPaneMaxW,
                                ShowIndicator = false,
                                CompactWidth = ShellResponsiveLayout.CompactRailW,
                                FadeStart = 240f,
                                FadeDistance = 64f,
                                ForcePush = 64f,
                                ReExpand = 190f,
                            }, collapsed: _sidebarCompact, fade: _sidebarFade, dragging: _sidebarDragging),
                        ],
                    },
                    new BoxEl
                    {
                        Width = Prop.Of(() => _shellUi.RailOpen.Value ? Splitter.StripW : 0f),
                        Direction = 1, ClipToBounds = true,
                        Transform = Prop.Of(() => Affine2D.Translation(
                            vpSig.Value.Width - _shellUi.RailWidth.Value - Splitter.StripW, 0f)),
                        Children =
                        [
                            Splitter.Create(_shellUi.RailWidth, CommitRailDrag, new()
                            {
                                Min = ShellResponsiveLayout.RailMinW,
                                Max = ShellResponsiveLayout.RailMaxW,
                                Polarity = SplitterPolarity.Leading,
                                ShowIndicator = false,
                                InvertCollapsed = true,
                            }, collapsed: _shellUi.RailOpen, fade: _rightRailFade),
                        ],
                    },
                    new BoxEl
                    {
                        Grow = 1f, Direction = 0, Justify = FlexJustify.End, HitTestPassThrough = true,
                        Children =
                        [
                            new BoxEl
                            {
                                // The rail overlay hosts RightRail. Projected motion: its width flips 0<->RailWidth as the
                                // single commit; RailReveal (Position|Size Reveal) slide-reveals it (FLIP TranslateX +
                                // presented-width) under its own ClipToBounds instead of animating REAL layout width per
                                // tick. Floating mode (!RailFits) has no spacer, so this overlays the content without
                                // resizing it — the reveal is purely the panel sliding in.
                                Direction = 1, Shrink = 0f, ClipToBounds = true, ZStack = true,
                                // Projected path: the overlay keeps its final width and RightRail translates its retained
                                // subtree through this clip. The baseline retains the old animated 0↔width layout path.
                                Width = Prop.Of(() => s_railBaseline
                                    ? (_shellUi.RailOpen.Value ? _shellUi.RailWidth.Value : 0f)
                                    : _shellUi.RailWidth.Value),
                                Animate = RailOverlayAnim,
                                HitTestPassThrough = true,
                                Children =
                                [
                                    // Backing band for the FLOATING overlay only — FloatingChrome (the shell GROUND), not
                                    // FloatingPane: RightRail paints the content surface on top, and pane-then-surface was
                                    // a double-coat that made the floating rail one rung darker than docked. Docked stays
                                    // transparent so the rail's rounded TL wedge shows the ground behind it.
                                    new BoxEl
                                    {
                                        // Paint-only closed-rail backing: never become the deepest hit in this retained
                                        // overlay. The interactive RightRail subtree above owns input while open.
                                        Grow = 1f, HitTestPassThrough = true,
                                        Fill = Prop.Of(() => _shellUi.RailOpen.Value && !_shellUi.RailFits.Value
                                            ? WaveeColors.FloatingChrome : ColorF.Transparent),
                                    },
                                    new BoxEl
                                    {
                                        // The panel stays mounted at its final width so close can translate it out without
                                        // relayout. When RightRail marks its root non-hit-testable, this wrapper must also
                                        // yield or the invisible retained 340-DIP strip covers the page scrollbar.
                                        Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, HitTestPassThrough = true,
                                        Corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f),
                                        // LAYOUT FIREWALL for the rail panel (lyrics, queue, friends): a re-render in
                                        // there must not re-solve the whole window. This INNER host is the right level —
                                        // the overlay above owns the reveal and the floating backing band — and on the
                                        // projected (default) path that overlay holds a CONSTANT RailWidth while
                                        // RightRail translates its retained subtree, so this box's width is stable
                                        // rather than animating. It already clips, so Boundary() adds only IsolateLayout.
                                        IsolateLayout = true,
                                        Opacity = Prop.Of(() => _rightRailFade.Value),
                                        Children = [ Embed.Comp(() => new RightRail()) ],
                                    },
                                ],
                            },
                        ],
                    },
                    // In narrow mode the 56-DIP rail remains inline. Hamburger opens this separately-retained full pane
                    // over the page, so the saved desktop expanded/collapsed preference is never overwritten.
                    Embed.Comp(() => new ShellNarrowDrawer(
                        narrowShell, narrowDrawer, vpSig, _sidebarWidth, _drawerExpanded, _route, GoNav))
                // Bounded fill: Grow=1 takes the free space, Shrink=1 makes this the ONE region that yields when the
                // window is shorter than the column's natural height. MinHeight=0 (the flex `min-height:0` override on the
                // SHRINKING element itself — the engine otherwise floors a flex item at its CONTENT's natural min) is what
                // actually lets it yield below a tall page (a Detail rail is ~600px and does not scroll); without it the
                // region overflows the column and shoves the fixed PlayerBar ~67px off the bottom for a frame on nav (the
                // "player bar disappears then slides back" glitch). The merged chrome row (one TitleBar + its 1px seam)
                // and the PlayerBar host keep the default Shrink=0, so the player bar stays a fixed 72px slot docked at the
                // window bottom and only the middle gives — its bounded height then lets the sidebar ScrollView scroll.
                //
                // ClipToBounds: this region's OWN box is clamped to the dock every frame (Shrink=1 yields → the player bar
                // never moves), but it is a ZStack and a ZStack deliberately lets children OVERFLOW (a popup must escape the
                // window). So while a page's content settles, the content-sized child chain can extend past this box down
                // into the docked player-bar band, where the translucent bar reveals it. Clip the region to its own box so
                // its bottom edge IS the player bar's top — content can never paint into the reserved dock slot. (The engine
                // RunSubtree fix keeps the IsolateLayout card's own box flush at rest; this clip covers the settle window
                // and is correct composition regardless. The Hero fly draws in a separate top band; popups live in the
                // OUTER OverlayHost ZStack — neither is affected.)
                ) with
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
                    // Scope the drag-drop spotlight scrim to THIS region: the title bar and the docked player bar stay
                    // fully lit while the page dims behind a drag (the bar keeps showing what is playing, and the
                    // caption buttons keep reading as live chrome). Re-published on every re-arrange, so a resize or a
                    // chrome-height change can never leave a stale rect behind.
                    OnRealized = h => { _contentRegionNode = h; PublishScrimClip(); },
                    OnBoundsChanged = _ => PublishScrimClip(),
                },
                // UNMOUNTED under fullscreen video (ShellChromeMounted) — the video surface's own auto-hiding transport
                // is the ONLY transport for the duration. This is the double-bar fix's shell half: the surface stopped
                // reserving a WaveeSize.PlayerBarH pass-through band, and the bar itself leaves the tree.
                Flow.Show(ShellChromeMounted, Embed.Comp(() => new PlayerBar())),
            ],
        };

        _fileDrop ??= new DropTargetSpec(
            [DropKinds.Files],
            OnEnter: _ => _fileDropOver.Value = true,
            OnLeave: _ => _fileDropOver.Value = false,
            OnDrop: s =>
            {
                _fileDropOver.Value = false;
                if (s.Payload is FileDropData { Count: > 0 } files) LocalFileActions.PlayDropped(_actions, files.Paths);
            });
        // The shell BACKDROP — the material stack the whole authenticated shell sits on, bottom-up:
        //   1. LIVE MICA — the DWM window material itself (Program.cs asks for it: CustomFrame, always base Mica —
        //      the Mica Alt row is gone, Workstream B). This box paints NOTHING, so the backdrop reads straight
        //      through every paint-site omission in the chrome (the title-bar drag bands, the sidebar band, the
        //      player dock).
        //   2. MATERIAL — ShellMaterialLayer, driven by the page-published signal: a flat tint (detail/artist pages
        //      publish their art colour at A=0.14) or Home's three clipped radial washes. A LOW-ALPHA scrim between
        //      Mica and the chrome, so the window material carries the album/artist hue instead of replacing it.
        //   3. the chrome column itself.
        // A ZStack, not a fill + one child: the material has to sit BETWEEN the backdrop and the column. The column
        // keeps its Grow=1 + viewport-bound height, so it fills the stack exactly as it filled the old parent.
        //
        // NOTE — the WINDOWS 11 MICA LAYERING (learn.microsoft.com system-backdrops + style/mica, explicit product
        // decision after two rejected alternatives). BASE LAYER: this root paints NOTHING — the merged row / sidebar /
        // player bands are paint-site omissions over live Mica, and a page's low-alpha tint composites over the
        // material. CONTENT LAYER: the content pane and rail band paint the stock WinUI LayerFillColorDefault rung
        // (WaveeColors.FileArea), a translucent smoke that keeps the page one step ABOVE the base on any wallpaper —
        // an opaque pane (both the deterministic #282828 and the "Files hybrid") inverted the model on light-tinted
        // wallpapers and read as a black slab. The merged single-row chrome is what makes bare Mica safe: no plates
        // remain, so the two-material seam the deterministic ground was invented for (D19) cannot recur.
        // ShellGround/#EDEDED and ContentSurface survive as the no-Mica fallbacks and floating-surface flatten bases.
        var tinted = new BoxEl
        {
            Grow = 1f, ZStack = true,
            Fill = ColorF.Transparent,
            DropTarget = _fileDrop,
            Children = [Embed.Comp(() => new ShellMaterialLayer(_shellMaterial, vpSig)), column],
        };

        // Transient toasts are now the engine's auto-mounted Toast host (a top-Z lane inside OverlayHost, InfoBar-chromed,
        // HostTimerQueue-driven with hover-pause). The bespoke Wavee ToastHost + its bottom-centre-above-the-bar positioner
        // were deleted in G6b; the engine host docks bottom-right (Toast.Placement) 24px from the window edge.
        // The local-playback setup banner FLOATS over the content (top-centre, just below the toolbar) instead of
        // inserting into the chrome column — a persistent offer must never reflow the page. Same pass-through positioner
        // pattern as the toast layer; the wrapper adds the overlay elevation the InfoBar itself doesn't carry. Sits
        // BELOW the toast layer so transient toasts stack above it.
        var runtimeBannerLayer = new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.Center,
            Padding = new Edges4(0f, 48f + 8f, 0f, 0f),   // clear the ONE merged chrome row (48) + a breathing gap
            Children =
            [
                new BoxEl { MaxWidth = 560f, Children = [ Embed.Comp(() => new PlaybackRuntimeChrome(_settings)) ] },
            ],
        };
        // The zero-size binder leaf lives INSIDE the OverlayHost subtree so it can capture the real overlay service
        // into the stable ActionServices bag (invoke-time dialogs: confirm / rename / add-to-playlist picker).
        // The in-window picture-in-picture video surface: a top-Z, pass-through floating layer (draggable + resizable),
        // visible only when the derived placement is the in-window PiP (VideoActive × VideoPlacement.InWindowPip). Reads
        // the resolved PopOutVideoSource + the backend-owned player (PlaybackBridge.VideoPlayer) from the bridge (provided at
        // the app root) and PRESENTS that player via PopOutVideoStage — no surface ever builds one, which is why moving
        // between the PiP and the pop-out does not restart the video. Sits above the content/banner layers so it floats over
        // the page + player bar; engine popups (in the
        // outer OverlayHost ZStack) still stack above it. VideoPlacementHost is the sibling CONTROLLER leaf (renders
        // empty) that owns the detached pop-out window's lifecycle off the same derived placement state.
        // The shell drop cue: a pass-through pill that fades in while a file drag is over the window (BOUND opacity, so
        // showing/hiding it is compositor-only — the shell is never re-rendered by a drag hover).
        var fileDropLayer = new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Opacity = Prop.Of(() => _fileDropOver.Value ? 1f : 0f),
            Children =
            [
                new BoxEl
                {
                    Padding = new Edges4(18f, 10f, 18f, 10f), Corners = CornerRadius4.All(Radii.Control),
                    Fill = Tok.FillSolidBase, BorderColor = Tok.AccentDefault, BorderWidth = 1f, Shadow = Elevation.Dialog,
                    Children = [new TextEl(Loc.Get(Strings.LocalFile.DropHint)) { Size = 14f, Color = Tok.TextPrimary }],
                },
            ],
        };
        // The IMMERSIVE LYRICS surface — a full-bleed layer directly above the chrome column (so it covers the content
        // card, the sidebar and the rail) and below every layer after it here, which is where the persistent banner,
        // the drop cue and the engine's auto-mounted toast / teaching-tip lane live. Flow.Show, not a `.Value` read in
        // this render body: the boundary mounts/unmounts the surface reactively without re-rendering the whole shell,
        // and its anchor is hit-test-transparent (MirrorParticipation), so the surface's own pass-through caption and
        // player-bar bands still reach the chrome underneath. Enter/Exit terminals come from the surface itself (they
        // read the reduced-motion VALUE at access time).
        var immersiveLyricsLayer = Flow.Show(
            () => _shellUi.ImmersiveLyrics.Value,
            new BoxEl
            {
                // Direction = 1 is LOAD-BEARING, not style. A BoxEl defaults to a ROW, and in a row the single child's
                // WIDTH is the main axis — where an oversized measure survives arrange (FlexShrink defaults to 0). As a
                // COLUMN the child's width is the CROSS axis, which AlignItems.Stretch hard-clamps to the slot, so a
                // layer can never be wider than the window whatever it measured. The banner and file-drop layers below
                // already have this shape; the immersive layer was the one row among them, and the one that overflowed.
                // Shrink/MinWidth/MinHeight say the same thing on the other axis.
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                HitTestPassThrough = true,
                Enter = ImmersiveLyricsSurface.EnterTerminal,
                Exit = ImmersiveLyricsSurface.ExitTerminal,
                Children = [Embed.Comp(() => new ImmersiveLyricsSurface())],
            });
        // The FULLSCREEN VIDEO surface (docked-video plan §5.4) — same full-bleed-layer pattern as the immersive
        // lyrics stage just above, mounted where the OTHER video placements (InWindowVideoPip / VideoPlacementHost)
        // already sit, below. Enter/Exit terminals come from the surface itself; UNLIKE the lyrics stage's, they carry
        // NO opacity component — a cross-fade would wash the video hole out (see that surface's own remarks).
        var videoFullscreenLayer = Flow.Show(
            // The SAME derived value the chrome unmount reads (ShellChromeMounted) — one signal, so the surface
            // mounting and the chrome leaving are the same edge and cannot disagree by a frame.
            () => _actions.Playback?.VideoFullscreenActive.Value == true,
            new BoxEl
            {
                // Same load-bearing Direction = 1 as the lyrics stage above — see its comment for why a row would let
                // an oversized measure survive arrange.
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                HitTestPassThrough = true,
                Enter = Wavee.Features.Video.VideoFullscreenSurface.EnterTerminal,
                Exit = Wavee.Features.Video.VideoFullscreenSurface.ExitTerminal,
                Children = [Embed.Comp(() => new Wavee.Features.Video.VideoFullscreenSurface { UserInitiated = _videoFullscreenUserOpen })],
            });
        var shellWithOverlays = Ui.ZStack(tinted, immersiveLyricsLayer, runtimeBannerLayer, fileDropLayer,
            WaveeCommandPalette.Overlay(_paletteOpen, GoNav, _actions, _settings, ToggleTheme),
            Embed.Comp(() => new ActionServicesOverlayBinder(_actions)),
            // Zero-size chrome INSIDE the OverlayHost subtree (so UseContext(Overlay.Service) resolves the real service —
            // the same reason ActionServicesOverlayBinder lives here): opens the one-time design chooser once per install,
            // after the first painted frame.
            Embed.Comp(() => new SidebarOnboardingChrome(_settings)),
            // Zero-size chrome, same reason as SidebarOnboardingChrome just above: owns both post-auth ways the
            // setup wizard can come up (a manual "Run setup again" re-run, or continuing a first-run wizard that
            // was still pending when auth completed). See Features/Setup/SetupChrome.cs.
            Embed.Comp(() => new SetupChrome(_settings)),
            // Zero-size chrome, same reason as the two above (the real Overlay.Service resolves only inside the
            // OverlayHost subtree): opens the in-app report dialog on request (About / Diagnostics / deep link) and,
            // once per launch, the crash-reopen prompt Program.cs latched into CrashPromptPolicy.ThisLaunch. MUST
            // mount before AfterUpdateChrome below — CrashNoticeThisLaunch has to be set before AfterUpdateChrome's
            // own effect decides whether to show the What's-new plate this launch.
            Embed.Comp(() => new ReportChrome(_settings)),
            // Zero-size chrome, same reason as the two above (the real Overlay.Service resolves only inside the
            // OverlayHost subtree): raises the after-update "What's new" plate once, on the first launch that follows
            // an update. See Features/ReleaseNotes/AfterUpdateDialog.cs for the three deferrals.
            Embed.Comp(() => new AfterUpdateChrome(_settings)),
            // The sidebar projection binder's pump — zero-size, always-mounted, BELOW the HistoryStore provide so the
            // visited feed resolves (see SidebarProjectionBinder remarks). Nothing rebuilds the projection without it.
            _actions.Svc?.SidebarBinder.MountPoint() ?? new BoxEl { HitTestVisible = false, Shrink = 0f },
            Embed.Comp(() => new Wavee.Features.Video.InWindowVideoPip { Settings = _settings }),
            Embed.Comp(() => new Wavee.Features.Video.VideoPlacementHost { Settings = _settings }),
            videoFullscreenLayer,
            // The setup wizard's shell-side scrim (work package I) — after every full-bleed content layer above (so
            // it covers PiP too, per videoFullscreenLayer's own remarks). This whole ZStack becomes OverlayHost's
            // Child (OverlayHost.Create(shellWithOverlays) below), and the host paints its own popups — including
            // the wizard's plate itself — ON TOP of that Child, so living inside it (however late) still keeps this
            // scrim under the wizard.
            Embed.Comp(() => new SetupCoverScrim()),
            DragPreviewLayer.Of(WaveeResourceDrag.Preview)) with { Grow = 1f };

        return Ctx.Provide(ShellUi.Slot, _shellUi,
               Ctx.Provide(ShellMaterial.Slot, _shellMaterial,
               Ctx.Provide(ShellMasthead.Slot, _shellMasthead,
               Ctx.Provide(HistoryStore.OriginSlot, _navOrigins,
               Ctx.Provide(HistoryStore.BackCtx, (Action)Back,
               Ctx.Provide(HistoryStore.NavCtx, (Action<string, string?>)GoNav,
               Ctx.Provide(HistoryStore.GoWithOrigin, (Action<string, string?, NavOrigin?>)GoNav,
               Ctx.Provide(HistoryStore.Slot, _historyStore,
               Ctx.Provide(NavPreviewStore.Slot, _navPreview,
               Ctx.Provide(HomeSectionPreviewStore.Slot, _homeSectionPreview,
               Ctx.Provide(Wavee.Features.Browse.BrowseDirectoryStore.Slot, _browseDirectory,
               Ctx.Provide(Wavee.Features.Browse.BrowsePageStore.Slot, _browsePages,
               Ctx.Provide(SearchQuery.Slot, _searchText,
               Ctx.Provide(ActionServices.Slot, _actions,
               Ctx.Provide(WaveeExtensionRegistry.Slot, _actions.Extensions,
               OverlayHost.Create(shellWithOverlays))))))))))))))));
    }

    Element TabStripHost()
    {
        if (_strip is { } s) s.IsAddTabButtonVisible = _chromeLayout.Peek().ShowNewTab;
        return Embed.Comp(BuildTabStrip);
    }

    TabStrip BuildTabStrip()
    {
        var strip = new TabStrip
        {
            // TEXT-FIRST (Zune) tabs: no plate, no flare, no separators, no rail — weight + opacity carry selection and
            // one strip-owned sliding 2-DIP accent underline marks it. That is what lets the tab strip share a 48-DIP
            // row with the search and the identity cluster; SelectedFill/TabWidth are Chrome-grammar and ignored here.
            Appearance = TabStripAppearance.Text,
            OverflowMode = TabStripOverflowMode.Scroll,
            TextFontSize = 13f,
            // The "+" is back, but HOVER-ONLY: at rest it paints nothing (Ctrl+T stays the power affordance), and it
            // cross-fades in whenever the pointer is over the strip. Its 32-DIP slot is RESERVED at every moment
            // regardless — the strip hugs, and TitleBar reports that hug wholesale as one TitleBarHit.Client region, so
            // a mount-on-hover would move the reported rect on every pointer entry and would have to join
            // MergedChromeRow.ContentVersion. A permanently reserved slot inside an island that is already entirely
            // client-hit-tested keeps the region rect (and this fold) completely still.
            IsAddTabButtonVisible = _chromeLayout.Peek().ShowNewTab,
            AddButtonVisibility = TabStripAddButtonVisibility.OnStripPointerOver,
            OnAddTabButtonClick = () => { OpenNewTab("home"); return null; },
            IndicatorFill = Prop.Of(() => Tok.AccentDefault),
            MinTabWidth = ShellResponsiveLayout.ChromeTabMinW,
            MaxTabWidth = ShellResponsiveLayout.ChromeTabMaxW,
            ItemsSource = BuildTabItems,
            ItemsVersion = TabStripItemsVersion,
            // CONTRACT: the strip treats SelectedIndex as its own state cell and WRITES it before it raises
            // OnSelectionChanged (the engine's controlled-prop idiom — TabView/ItemsViewPresets rely on it). So the
            // cell is never evidence of whether a click changed tabs: ActivateTab asks TabWorkspace (its ActiveId),
            // and Apply re-asserts the cell from the model afterwards. Reading `_selectedTab.Peek() == i` as an
            // early-out here is exactly the bug that left the content on the previous tab's page.
            SelectedIndex = _selectedTab,
            OnSelectionChanged = ActivateTab,
            OnTabCloseRequested = CloseTab,
            ScrollMetricsChanged = OnTabStripMetrics,
        };
        // Embed.Comp runs its factory once and the reconciler mounts THAT instance, so the first one built is the live
        // one (and a defensive extra call still leaves this pointing at the mounted instance).
        _strip ??= strip;
        return strip;
    }

    int TitleBarTabsVersion()
    {
        int version = _tabsVersion.Value;
        int selected = _selectedTab.Value;
        return unchecked(version * 397 ^ selected);
    }

    int TabStripItemsVersion()
    {
        int version = _tabsVersion.Value;
        bool add = _chromeLayout.Value.ShowNewTab;
        return unchecked(version * 397 ^ (add ? 1 : 0));
    }

    void OnTabStripMetrics(TabStripScrollMetrics metrics)
    {
        float extent = MathF.Max(ShellResponsiveLayout.ChromeTabMinW,
            MathF.Round(metrics.ContentExtent / ShellResponsiveLayout.ChromeWidthQuantumW)
            * ShellResponsiveLayout.ChromeWidthQuantumW);
        _tabNaturalExtent.SetIfChanged(extent);
    }

    void SeedTabExtent()
    {
        var tabs = _tabs.Tabs;
        int pinned = 0;
        for (int i = 0; i < tabs.Count; i++) if (tabs[i].Pinned) pinned++;
        float estimate = MergedChromeLayout.EstimatedTabExtent(tabs.Count, pinned);
        if (estimate > _tabNaturalExtent.Peek()) _tabNaturalExtent.Value = estimate;
    }

    void TabsChanged()
    {
        // The strip will publish its exact post-layout extent. Seed upward immediately so adding/unpinning tabs can
        // collapse search in the same event turn instead of briefly squeezing the new tab behind a stale measurement.
        SeedTabExtent();
        _tabsVersion.Value = _tabsVersion.Peek() + 1;
    }

    IReadOnlyList<TabViewItem> BuildTabItems()
    {
        var tabs = _tabs.Tabs;
        var items = new TabViewItem[tabs.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var tab = tabs[i];
            var (label, glyph) = ShellNav.Dest(tab.Route);
            var destination = DestinationOf(tab);
            int id = tab.Id;
            items[i] = new TabViewItem
            {
                Key = "tab#" + id,
                Header = label,
                Icon = glyph,
                IsClosable = !tab.Pinned && tabs.Count > 1,
                IsPinned = tab.Pinned,
                ContextMenu = () => TabMenu(id),
                // A tab is CLICK-PRIMARY: switching tabs is the constant intent, dragging one out the rare one. At the
                // base 4px box a click landed while the mouse is still travelling promotes to a drag and its click is
                // suppressed — the tab silently fails to select. WinUI widens the mouse box ×2 on list items for exactly
                // this reason (LISTVIEWBASEITEM_MOUSE_DRAG_THRESHOLD_MULTIPLIER).
                Drag = destination is { } d
                    ? Drag.Source(WaveeDragKinds.Resource,
                        () => WaveeResourceDragPayload.FromDestination(d, _actions),
                        thresholdMultiplier: Drag.ClickPrimaryThresholdMultiplier)
                    : null,
                // Spring-load NAVIGATION on every tab. Holding a drag over a tab switches to it, so the destination the
                // user actually wants can be reached mid-gesture instead of forcing them to cancel, navigate and start
                // over. On a tab that is NOT a deposit destination, SpringLoadOnly is what keeps that honest: the tab
                // never accepts a drop and is never published as a refusal either, so merely travelling ACROSS the strip
                // on the way to the sidebar leaves the chip silent rather than flashing a not-allowed glyph at each tab.
                //
                // A tab standing for an EDITABLE PLAYLIST is also a real destination — dropping tracks on it appends
                // them to that playlist without ever leaving the page you are on, the cross-tab deposit. The two
                // coexist by construction: FindTarget resolves the spring host BEFORE acceptance, so the same spec both
                // opens the tab on a dwell and takes the drop on a release.
                DropTarget = TabDropTarget(destination, id),
            };
        }
        return items;
    }

    /// <summary>One tab's drop target: always the spring-load waypoint, plus a track deposit when the tab stands for a
    /// playlist this user can write to (see <see cref="TabDropRules"/> for the decision and why the same-playlist cases
    /// are REFUSED rather than left to no-op inside the deposit).</summary>
    DropTargetSpec TabDropTarget(SidebarDestination? destination, int tabId)
    {
        var acts = _actions;
        if (destination is { Kind: SidebarEntryKind.Playlist } d && WaveeRootlist.CanEditPlaylist(acts, d.Uri))
        {
            string uri = d.Uri, name = d.Name;
            return Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
                accepts: p => TabDropRules.AcceptsDeposit(uri, targetEditable: true, p.CanCopyTracks,
                                                          p.SourcePlaylistUri, p.Uri),
                caption: _ => Strings.Drag.AddTo(name),
                // insertionIndex null = APPEND: a tab has no slot to speak of, exactly like the page-body target.
                // DepositTracksAsync owns the toast and the error path.
                onDrop: (p, _) => WaveeResourceDrop.DepositTracks(acts, uri, name, p, insertionIndex: null),
                // The deposited feel: the lifted visual snaps home rather than gliding into a list it never joined.
                settleOnDrop: false,
                springLoadMs: WaveeResourceDrag.SpringLoadMs,
                onSpringLoad: (_, _) => ActivateTabById(tabId));
        }
        return Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
            springLoadMs: WaveeResourceDrag.SpringLoadMs,
            onSpringLoad: (_, _) => ActivateTabById(tabId),
            springLoadOnly: true);
    }

    SidebarDestination? CurrentSidebarDestination()
    {
        var route = _route.Peek();
        var (title, _) = string.Equals(route.Name, "search", StringComparison.Ordinal)
            ? ShellNav.Dest("search")
            : ShellNav.Dest(route);
        return SidebarDestination.FromRoute(route.Name, route.Arg, title);
    }

    static SidebarDestination? DestinationOf(WorkspaceTab tab)
    {
        var route = tab.Route;
        string title = string.Equals(route.Name, "search", StringComparison.Ordinal)
            ? ShellNav.Dest("search").Title
            : ShellNav.Dest(route).Title;
        return SidebarDestination.FromRoute(route.Name, route.Arg, title);
    }

    ContextMenuModel? TabMenu(int tabId)
    {
        int index = _tabs.IndexOf(tabId);
        if (index < 0) return null;
        var tab = _tabs.Tabs[index];
        var rows = new List<MenuFlyoutItem>(9)
        {
            new MenuFlyoutItem(
                Loc.Get(tab.Pinned ? Strings.Shell.UnpinTab : Strings.Shell.PinTab),
                ActionIcons.Resolve(tab.Pinned ? ActionIcons.Unpin : ActionIcons.Pin),
                true, () => SetTabPinned(tabId, !tab.Pinned)),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseTab), Icons.Cancel,
                _tabs.Count > 1, () => CloseTabById(tabId)),
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseOtherTabs), default,
                HasOtherUnpinned(tabId), () => CloseOtherTabs(tabId)),
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseTabsRight), default,
                HasUnpinnedToRight(index), () => CloseTabsToRight(tabId)),
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseAllUnpinned), default,
                HasAnyUnpinned(), CloseAllUnpinnedTabs),
        };
        if (DestinationOf(tab) is { } destination
            && PinActions.RowForDestination(_actions, in destination) is { } pagePin)
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(pagePin);
        }
        return new ContextMenuModel(rows, new ContextMenuHeader(null, ShellNav.Dest(tab.Route).Title));
    }

    bool HasAnyUnpinned()
    {
        var tabs = _tabs.Tabs;
        for (int i = 0; i < tabs.Count; i++) if (!tabs[i].Pinned) return true;
        return false;
    }

    bool HasOtherUnpinned(int tabId)
    {
        var tabs = _tabs.Tabs;
        for (int i = 0; i < tabs.Count; i++) if (tabs[i].Id != tabId && !tabs[i].Pinned) return true;
        return false;
    }

    bool HasUnpinnedToRight(int index)
    {
        var tabs = _tabs.Tabs;
        for (int i = index + 1; i < tabs.Count; i++) if (!tabs[i].Pinned) return true;
        return false;
    }

    /// <summary>The active tab's stable id, SUBSCRIBING (ContentHost keys its keep-alive slot on it): the workspace
    /// is plain state, so the two signals every tab op bumps stand in as its change notification.</summary>
    int ActiveTabId()
    {
        _ = _tabsVersion.Value;
        _ = _selectedTab.Value;
        return _tabs.ActiveId;
    }

    /// <summary>The active tab's stable id without subscribing (event-handler / persist path).</summary>
    int PeekActiveTabId() => _tabs.ActiveId;

    /// <summary>Zero-alloc persist: passes the live stacks; the store copies into reused buffers and debounces 2 s.</summary>
    void CaptureNav()
        => _session.UpdateNav(_route.Peek(), _history, _forwardHistory, PeekActiveTabId(), _navOrigins);

    static NavOrigin? OriginOf(in SessionRouteDto d)
        => d.OriginLabel is { Length: > 0 } && d.OriginName is { Length: > 0 }
            ? new NavOrigin(d.OriginLabel, d.OriginName, d.OriginArg)
            : null;

    void DrainDeepLinks()
    {
        while (DeepLinkChannel.TryDequeue(out DeepLinkVerb verb))
        {
            try { ApplyDeepLink(verb); }
            catch { /* a bad verb must not break the shell */ }
        }
    }

    void ApplyDeepLink(DeepLinkVerb verb)
    {
        switch (verb.Kind)
        {
            case DeepLinkKind.Open:
                GoDeepLinkOpen(verb.Route, verb.Arg);
                break;
            case DeepLinkKind.Play:
            case DeepLinkKind.Resume:
            case DeepLinkKind.Pause:
                HandleDeepLinkPlayback(verb);
                break;
        }
    }

    /// <summary>Compose the shell's opaque nav key. Entity verbs arrive as <c>route=album&amp;arg=spotify:album:…</c>
    /// and become <c>album:spotify:album:…</c>; a already-full key is passed through.
    ///
    /// <para>A deep link is UNTRUSTED input — it arrives from the OS, a browser, another app — so the composed key is
    /// checked against <see cref="ShellRoutes"/> before it can become a tab. Without that, a typo'd or hostile verb
    /// opened a real tab on the not-found page and wrote it into the persisted history log.</para></summary>
    void GoDeepLinkOpen(string route, string arg)
    {
        // wavee://open?route=report&arg=bug|feature|crash|question|idea — a report is a dialog, never a tab/history
        // entry, so it is handled and returns before the entity-route / ShellRoutes.IsKnown machinery below.
        if (route.Equals("report", StringComparison.OrdinalIgnoreCase))
        {
            ReportRequests.Open(ReportChannels.TryParseKind(arg, out var k) ? k : ReportKind.Bug);
            return;
        }
        string key = route;
        string? a = arg.Length == 0 ? null : arg;
        if (a is not null && route.IndexOf(':') < 0
            && (route is "album" or "pl" or "artist" or "show" or "prerelease" or "module"))
        {
            key = route + ":" + a;
            a = null;   // URI lives in the key; Arg is the display name, which a deep link does not carry
        }
        if (!ShellRoutes.IsKnown(key))
        {
            WaveeLog.Instance.Warn("nav", "deeplink.route.unknown: " + key);
            return;
        }
        // The developer surfaces are not part of the app's public verb map: they are reachable from Settings once the
        // user has turned developer mode on, and a link from outside the app must not be what turns them on.
        if ((DeveloperMode.IsDeveloperRoute(key) || key == PlaybackRuntimeDiagnosticsPage.Route)
            && !DeveloperMode.Enabled.Peek())
        {
            WaveeLog.Instance.Warn("nav", "deeplink.route.developerOnly: " + key);
            return;
        }
        GoNav(key, a);
    }

    /// <summary>Play/resume from a <c>wavee://</c> verb. PlaybackBridge.Player is the shell's playback seam.</summary>
    void HandleDeepLinkPlayback(DeepLinkVerb verb)
    {
        var playback = _actions.Playback;
        if (playback is null) return;
        switch (verb.Kind)
        {
            case DeepLinkKind.Play:
                if (verb.Link.Length > 0) PlayLink.PlayDirect(_actions, verb.Link);   // the module path, same as Play ▸ Link…
                else _ = playback.Player.PlayAsync(verb.Context);
                break;
            case DeepLinkKind.Resume:
                _ = playback.Player.ResumeAsync();
                break;
            case DeepLinkKind.Pause:
                _ = playback.Player.PauseAsync();
                break;
        }
    }

    // ── navigation (the single source of truth the chrome reads) ─────────────────────────────────
    void Go(string key, string? arg, NavTransitionKind motion = NavTransitionKind.Forward, NavOrigin? origin = null)
    {
        var normalized = NavRouteNormalizer.Apply(key, arg);
        key = normalized.Name;
        arg = normalized.Arg;
        CloseNarrowDrawer();
        _history.Add(_route.Peek());
        if (_history.Count > MaxBackStack) _history.RemoveAt(0);   // bound the in-memory back-stack
        _forwardHistory.Clear();
        _canForward.Value = false;
        _navMotion.Value = motion;
        _navOrigins.Write(key, arg, origin);   // every Go writes; null overwrites — latest arrival wins
        _route.Value = new Route(key, arg);
        _canBack.Value = _history.Count > 0;
        _historyStore.Add(_route.Peek());
        RecordRecentSurface(_route.Peek());
        SyncActiveTab(_route.Peek());
        SyncOmnibarToRoute(_route.Peek());
        CaptureNav();
    }

    /// <summary>Spotify chrome: the omnibar is the search page's query. Leaving Search (Home, a result, another tab)
    /// clears it back to the placeholder; arriving on Search restores the route Arg. Live typing is untouched because
    /// this only runs on navigation, not on keystrokes.</summary>
    void SyncOmnibarToRoute(Route r)
    {
        string next = r.Name == "search" ? (r.Arg ?? "") : "";
        if (_searchText.Peek() != next) _searchText.Value = next;
    }

    // Addendum A5 — the `recent_surfaces` pin reason. Hooked to FORWARD navigation only: Back/Forward re-visit a surface
    // through history, which is not a new "open" and must not churn the 50-slot LRU (the surface is already in it).
    // Runs on the UI thread, so nothing here may touch SQLite: RecordRecentSurface updates the in-memory pin mirror
    // synchronously (the write gate has to see the new pin on the very next upsert) and lanes the row + the entity flush
    // onto CachedStore's background writer. Null RealStore (fake/offline backend) ⇒ no-op.
    void RecordRecentSurface(Route r)
    {
        if (!Wavee.Backend.Persistence.RecentSurfaceRoute.TryClassify(r.Name, out var uri, out var kind)) return;
        if (_actions.Svc?.RealStore is Wavee.Backend.Persistence.CachedStore store) store.RecordRecentSurface(uri, (int)kind);
    }

    void Back()
    {
        if (_history.Count == 0) return;
        CloseNarrowDrawer();
        _forwardHistory.Add(_route.Peek());
        if (_forwardHistory.Count > MaxBackStack) _forwardHistory.RemoveAt(0);
        _canForward.Value = true;
        _navMotion.Value = NavTransitionKind.Back;
        _route.Value = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _canBack.Value = _history.Count > 0;
        _historyStore.Add(_route.Peek());
        SyncActiveTab(_route.Peek());
        SyncOmnibarToRoute(_route.Peek());
        CaptureNav();
    }

    void Forward()
    {
        if (_forwardHistory.Count == 0) return;
        CloseNarrowDrawer();
        _history.Add(_route.Peek());
        _canBack.Value = true;
        _navMotion.Value = NavTransitionKind.Forward;
        _route.Value = _forwardHistory[^1];
        _forwardHistory.RemoveAt(_forwardHistory.Count - 1);
        _canForward.Value = _forwardHistory.Count > 0;
        _historyStore.Add(_route.Peek());
        SyncActiveTab(_route.Peek());
        SyncOmnibarToRoute(_route.Peek());
        CaptureNav();
    }

    void Home() => Go("home", null);

    // History always opens in its own tab (global view — same as browser convention).
    void GoNav(string key, string? arg) => GoNav(key, arg, null);

    void GoNav(string key, string? arg, NavOrigin? origin)
    {
        if (key == "history") OpenNewTab(key);
        else Go(key, arg, NavTransitionKind.Forward, origin);
    }

    /// <summary>Every navigation lands on the ACTIVE tab (browser semantics: the tab follows the page).</summary>
    void SyncActiveTab(Route r)
    {
        _tabs.SetActiveRoute(r);
        TabsChanged();
        SyncPins();
    }

    /// <summary>Tab SWITCH: show the tab's page as it was. Deliberately not a <see cref="Go"/> — no back-stack push
    /// (the tab is not a new place; Back must still leave the page, not undo the switch), no origin write (the
    /// arrival's crumb stands — a Go would erase "Home › Browse › X" back to the IA answer), no HistoryStore /
    /// recent-surface record (nothing was newly opened). Neutral motion: a switch is neither forward nor back.</summary>
    void Restore(Route r)
    {
        CloseNarrowDrawer();
        _navMotion.Value = NavTransitionKind.Neutral;
        _route.Value = r;
        SyncOmnibarToRoute(r);
        CaptureNav();
    }

    /// <summary>The one place a workspace op meets the shell: re-assert the strip's selection cell from the model
    /// (the strip may have pre-written it, the model may have moved the tab), persist pins if they changed, then
    /// follow the intent — <see cref="Restore"/> for a switch, <see cref="Go"/> for a fresh tab's first route.</summary>
    void Apply(TabNavResult result)
    {
        if (_selectedTab.Peek() != result.ActiveIndex) _selectedTab.Value = result.ActiveIndex;
        TabsChanged();
        SyncPins();
        switch (result.Intent)
        {
            case TabNavIntent.Restore when result.Route is { } r:
                Restore(r);
                break;
            case TabNavIntent.Push when result.Route is { } r:
                Go(r.Name, r.Arg, NavTransitionKind.Neutral);
                break;
        }
    }

    /// <summary>Select tab <paramref name="i"/> AND show its page — the pair every tab activation needs (writing the
    /// selection signal alone moves the highlight without swapping content, which is what makes a spring-load
    /// useless). No selection guard here: see the contract on the strip's SelectedIndex.</summary>
    void ActivateTab(int i) => Apply(_tabs.Activate(i));

    void ActivateTabById(int id) => Apply(_tabs.ActivateById(id));

    void OpenNewTab(string key) => Apply(_tabs.Open(new Route(key)));

    /// <summary>Closing the active tab restores its neighbour; closing a background tab changes nothing on screen.</summary>
    void CloseTab(int i) => Apply(_tabs.Close(i));

    void CloseTabById(int id) => Apply(_tabs.Close(_tabs.IndexOf(id)));

    void SetTabPinned(int id, bool pinned) => Apply(_tabs.SetPinned(id, pinned));

    void CloseOtherTabs(int keepId)
        => Apply(_tabs.CloseWhere(tab => tab.Id != keepId && !tab.Pinned));

    void CloseTabsToRight(int tabId)
    {
        int index = _tabs.IndexOf(tabId);
        if (index < 0) return;
        var tabs = _tabs.Tabs;
        var rightIds = new HashSet<int>();
        for (int i = index + 1; i < tabs.Count; i++) if (!tabs[i].Pinned) rightIds.Add(tabs[i].Id);
        Apply(_tabs.CloseWhere(tab => rightIds.Contains(tab.Id)));
    }

    void CloseAllUnpinnedTabs() => Apply(_tabs.CloseWhere(static tab => !tab.Pinned));

    /// <summary>Template overrides for the merged chrome row. The bar lays its pane toggle out at x=4 (root padding 2 +
    /// the button's own margin 2) and it is 40 wide, so its centre lands at 24 — while the 56-DIP compact rail centres
    /// the sidebar's icon column at 28. Nudge the painted slot four DIP right and take the four back on the trailing
    /// side, so the hamburger lines up with the icons directly beneath it and NOTHING after it moves (the idiom the
    /// two-row toolbar used on the same button, restored now that the button lives in the bar's own lead column).
    /// <para>Margin is not an owned property of <c>PartPaneToggle</c> (the control owns OnClick/Role/OnRealized/Children
    /// and re-applies exactly those after the part function), so overriding it here is inside the template contract.
    /// Built ONCE: mutating a TemplateParts bumps its Epoch and invalidates the engine's apply-once prototype cache.</para></summary>
    static readonly TemplateParts ChromeParts = BuildChromeParts();

    static TemplateParts BuildChromeParts()
    {
        var parts = new TemplateParts();
        parts[TitleBar.PartPaneToggle] = b => b with { Margin = new Edges4(6f, 2f, -2f, 2f) };
        return parts;
    }

    // Shell-wide keyboard chords (see the accelerator hosts in the column). Ctrl+T opens a new Home tab; Ctrl+K opens
    // the command palette (subsumes the old omnibar focus). Ctrl+F focuses the omnibar. Alt+Left/Right = Back/Forward.
    // Space = play/pause via OnShellKey (not an accelerator — InputDispatcher only matches Ctrl/Alt or F-keys).
    // Mouse XButton1/2 are NOT delivered by the engine (InputDispatcher handles buttons 0/1/2 only) — no wiring.
    static readonly KeyAccelerator NewTabChord = new(Keys.T, KeyModifiers.Ctrl);
    static readonly KeyAccelerator FocusSearchChord = new(Keys.K, KeyModifiers.Ctrl);
    static readonly KeyAccelerator FindChord = new(Keys.F, KeyModifiers.Ctrl);
    static readonly KeyAccelerator BackChord = new(Keys.Left, KeyModifiers.Alt);
    static readonly KeyAccelerator ForwardChord = new(Keys.Right, KeyModifiers.Alt);
    // F11 — bare, no modifier: F-keys ARE accelerator-eligible with no modifier (InputDispatcher.FindAccelerator
    // matches Ctrl/Alt OR an F-key), unlike bare Space/Escape. No existing shell chord claims F11 (checked: NewTab is
    // Ctrl+T, palette Ctrl+K, find Ctrl+F, back/forward Alt+Left/Right — none is an F-key), so this is a fresh binding.
    static readonly KeyAccelerator VideoFullscreenChord = new(Keys.F11, KeyModifiers.None);
    // App zoom — the browser chord set, spelled out per VK because a KeyAccelerator matches EXACT modifiers: the
    // top-row '=' key (OemPlus) types '+' only WITH Shift on many layouts, so Ctrl+Shift+= must be its own chord or
    // the natural way users type "Ctrl and plus" silently does nothing; and the numpad's + / − / 0 are DISTINCT VKs
    // (Add / Subtract / NumPad0), never aliases of the top-row keys, so each needs its own binding too. Eight chords,
    // three verbs — the same spread Chromium registers for its zoom accelerators.
    static readonly KeyAccelerator ZoomInChord = new(Keys.OemPlus, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomInShiftChord = new(Keys.OemPlus, KeyModifiers.Ctrl | KeyModifiers.Shift);
    static readonly KeyAccelerator ZoomInNumpadChord = new(Keys.Add, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomOutChord = new(Keys.OemMinus, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomOutShiftChord = new(Keys.OemMinus, KeyModifiers.Ctrl | KeyModifiers.Shift);
    static readonly KeyAccelerator ZoomOutNumpadChord = new(Keys.Subtract, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomResetChord = new(Keys.D0, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomResetNumpadChord = new(Keys.NumPad0, KeyModifiers.Ctrl);

    void OpenPalette() => _paletteOpen.Value = !_paletteOpen.Peek();

    // F11 toggles video fullscreen ONLY while a video is active (docked-video plan §5.4/B16-B18) — with no video
    // playing there is nothing to fill the screen with, so the chord is a no-op rather than opening an empty stage.
    //
    // NO DOUBLE-FIRE with MediaPlayerElement's own F11. The dispatcher runs FOCUSED routing first and only reaches
    // FindAccelerator when nothing set Handled (InputDispatcher.OnKey — the WinUI ProcessKeyboardAccelerators order).
    // With focus inside the player, the element consumes F11 and routes it to its FullscreenRequested delegate, which
    // every Wavee surface wires to this same placement model; with focus anywhere else, this accelerator runs. Exactly
    // one of the two fires, always — and both end at ShowVideoAt(Fullscreen)/ExitVideoFullscreen. The same holds for
    // `F` and a double-click on the video, which the element handles and delegates identically.
    void ToggleVideoFullscreen()
    {
        if (_actions.Playback is not { } pb) return;
        if (pb.VideoPlacementNow() == SurfacePlacement.Fullscreen) { pb.ExitVideoFullscreen(); return; }
        if (pb.VideoActive()) pb.ShowVideoAt(SurfacePlacement.Fullscreen);
    }

    void FocusFind()
    {
        // DetailTracks owns a private _searchExpanded with no shell-reachable focus ticket. Ctrl+F therefore focuses
        // the omnibar (MergedChromeRow still honors FirstFocusableIn — Wave 1). In-page filter is a follow-up.
        _searchFocusRequest.Value = _searchFocusRequest.Peek() + 1;
    }

    void OnShellKey(KeyEventArgs e)
    {
        if (e.Handled || e.Mods != KeyModifiers.None) return;
        switch (e.KeyCode)
        {
            case Keys.Escape: OnShellEscape(e); return;
            case Keys.Space: OnShellSpace(e); return;
        }
    }

    // THE IMMERSIVE STAGE'S (AND THE FULLSCREEN VIDEO SURFACE'S) SECOND WAY OUT, and why it lives here rather than on
    // either surface.
    //
    // Both surfaces have their own Escape handler, but Escape routes to the FOCUSED node and bubbles up ITS ancestors
    // (InputDispatcher.OnKey) — and both deliberately leave the caption strip and the docked player bar LIVE, so one
    // click on either moves focus outside the surface's subtree and the surface's own handler is never reached. This
    // column is an ancestor of the title bar, the content region AND the player bar, so it catches every one of those.
    //
    // NOT an accelerator: the dispatcher only matches KeyAccelerator for Ctrl/Alt or F-keys, so bare Escape cannot be
    // one (same reason Space is handled here). NOT InputHooks.KeyPreview either: that is a SINGLE slot which
    // OverlayHost.Render re-assigns unconditionally on every one of its renders, so any chain installed at mount is
    // destroyed the first time a context menu opens.
    //
    // Precedence comes for free from the routing order — everything that should beat us already has:
    //   • an in-flight item drag cancels first (OnKey stage 1);
    //   • OverlayHost.PreviewKey runs PRE-focus and swallows Escape for every menu / flyout / device picker /
    //     ContentDialog that is dismissible, so those close instead of either surface;
    //   • every deeper focused Escape owner (the palette's list, in-page search, SemanticZoom, Reorderable…) gets
    //     first refusal by bubbling, and we bail on e.Handled above.
    // The one competitor that is NOT ordered for us is the command palette, which is a SIBLING ZStack layer rather
    // than an overlay entry — hence the explicit guard.
    //
    // Video fullscreen is checked AFTER immersive lyrics — docked-video plan §5.4 — so an Escape while BOTH happen to
    // be up (not reachable today; immersive lyrics has no video-fullscreen entry point) closes the lyrics stage first.
    void OnShellEscape(KeyEventArgs e)
    {
        if (_paletteOpen.Peek()) return;   // the palette owns Escape while it is up (sibling layer, not an overlay)
        if (_shellUi.ImmersiveLyrics.Peek())
        {
            _shellUi.ImmersiveLyrics.Value = false;
            // Handled ALSO stops OnKey's unhandled-Escape arm from clearing focus, which would leave the keyboard
            // with nowhere to route the next key.
            e.Handled = true;
            return;
        }
        if (_actions.Playback is { } pb && pb.VideoPlacementNow() == SurfacePlacement.Fullscreen)
        {
            pb.ExitVideoFullscreen();
            e.Handled = true;
        }
    }

    void OnShellSpace(KeyEventArgs e)
    {
        if (FocusedIsTextEditor()) return;
        if (_actions.Playback is not { } pb) return;
        PlayerBarContent.TogglePlayPause(pb);
        e.Handled = true;
    }

    bool FocusedIsTextEditor()
    {
        var focused = _inputHooks?.GetFocus?.Invoke() ?? default;
        if (focused.IsNull || _contentScene is null || !_contentScene.IsLive(focused)) return false;
        ref var ix = ref _contentScene.Interaction(focused);
        return ix.Role == AutomationRole.Text
               || (ix.HandlerMask & InteractionInfo.CharBit) != 0;
    }

    void ToggleTheme()
    {
        var next = Theme.Dark ? ThemeKind.Light : ThemeKind.Dark;
        Tok.Use(WaveeTheme.ResolvePalette(), next);
        _settings.Set(WaveeSettings.ThemeMode, next == ThemeKind.Dark ? 2 : 1);
        _requestTheme?.Invoke(250f);
    }

    // App zoom, one ladder rung per invocation (0 = reset to 100%). STATIC and internal on purpose: the chord boxes,
    // the Ctrl+wheel hook AND the command palette (WaveeCommands.Invoke — a static table dispatch with no shell
    // instance in hand) all land on this one verb. FluentApp.SetZoom clamps and drops no-change writes itself;
    // persistence is deliberately NOT here — WaveeApp's debounced zoom-save timer picks the value up, so a held-down
    // chord or a wheel spin never writes the registry once per rung.
    internal static void ZoomStep(int dir)
        => FluentApp.SetZoom(dir == 0 ? ZoomLadder.Default
            : dir > 0 ? ZoomLadder.In(FluentApp.Zoom)
            : ZoomLadder.Out(FluentApp.Zoom));
}

/// <summary>Narrow-shell light-dismissible pane. It stays mounted while closed so its sidebar state and scene nodes are
/// retained; open/close is compositor-only (scrim opacity + pane translation).</summary>
sealed class ShellNarrowDrawer : Component
{
    readonly IReadSignal<bool> _narrow;
    readonly Signal<bool> _open;
    readonly IReadSignal<Size2> _viewport;
    readonly Signal<float> _expandedWidth;
    readonly Signal<bool> _drawerCompact;
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;

    public ShellNarrowDrawer(IReadSignal<bool> narrow, Signal<bool> open, IReadSignal<Size2> viewport,
        Signal<float> expandedWidth, Signal<bool> drawerCompact, Signal<Route> route, Action<string, string?> go)
    {
        _narrow = narrow; _open = open; _viewport = viewport; _expandedWidth = expandedWidth;
        _drawerCompact = drawerCompact; _route = route; _go = go;
    }

    public override Element Render()
    {
        bool open = _narrow.Value && _open.Value;
        var hooks = UseContext(InputHooks.Current);
        var savedPreview = UseRef<Func<int, bool>?>(null);
        var escPreview = UseRef<Func<int, bool>?>(null);
        var escInstalled = UseRef(false);

        escPreview.Value ??= key =>
        {
            if ((key == Keys.Escape || key == Keys.GamepadB) && _open.Peek())
            {
                _open.Value = false;
                return true;
            }
            return savedPreview.Value?.Invoke(key) ?? false;
        };
        UseEffect(() =>
        {
            if (open && !escInstalled.Value)
            {
                escInstalled.Value = true;
                savedPreview.Value = hooks.KeyPreview;
                hooks.KeyPreview = escPreview.Value;
            }
            else if (!open && escInstalled.Value)
            {
                escInstalled.Value = false;
                if (ReferenceEquals(hooks.KeyPreview, escPreview.Value)) hooks.KeyPreview = savedPreview.Value;
                savedPreview.Value = null;
            }
        }, open);

        return new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestVisible = open,
            Children =
            [
                Embed.Comp(() => new ShellNarrowDrawerScrim(_open)),
                new BoxEl
                {
                    Grow = 1f, Direction = 0, Justify = FlexJustify.Start, HitTestPassThrough = true,
                    Children =
                    [
                        Embed.Comp(() => new ShellNarrowDrawerPane(
                            _open, _viewport, _expandedWidth, _drawerCompact, _route, _go)),
                    ],
                },
            ],
        };
    }
}

sealed class ShellNarrowDrawerScrim : Component
{
    readonly Signal<bool> _open;
    public ShellNarrowDrawerScrim(Signal<bool> open) => _open = open;

    public override Element Render()
    {
        bool open = _open.Value;
        var mounted = UseRef(false);
        float ms = Motion.ReducedMotion ? 0f : WaveeMotion.Fast;
        float target = ShellResponsiveLayout.DrawerRestingOpacity(open);
        UseTransition(AnimChannel.Opacity, mounted.Value ? 1f - target : target, target,
            ms, Easing.Linear, open);
        mounted.Value = true;
        return new BoxEl
        {
            Grow = 1f, Fill = ColorF.FromRgba(0, 0, 0, 0x33), Opacity = target,
            HitTestVisible = open, OnClick = () => _open.Value = false,
        };
    }
}

/// <summary>The shell's half of the setup wizard's scrim (work package I — see <see cref="SetupSession.Covering"/>
/// and <c>SetupLayout.CoverFor</c>). The engine's own popup scrim only ever paints for the BARE pre-auth mount
/// (<c>SetupDialog.Open</c> sets <c>PopupOptions.ScrimVisual = bare</c>); post-auth the shell paints this instead,
/// because only the shell knows whether the current setup page wants an ordinary dim (<see cref="SetupCover.Dim"/>)
/// or a lifted, live-preview look (<see cref="SetupCover.Live"/> — Appearance/Sidebar, where the wizard's own
/// promise is "this window IS the preview"). No ctor args — always mounted, reading <see cref="SetupSession.Covering"/>
/// itself instead of taking a signal in, since (unlike <see cref="ShellNarrowDrawerScrim"/>'s per-shell drawer state)
/// this one static signal is shared by every mount — modelled on <see cref="ShellNarrowDrawerScrim"/> down to the
/// cross-fade idiom.</summary>
sealed class SetupCoverScrim : Component
{
    public override Element Render()
    {
        bool dim = SetupSession.Covering.Value == SetupCover.Dim;
        var mounted = UseRef(false);
        float ms = Motion.ReducedMotion ? 0f : WaveeMotion.Standard;
        float target = dim ? 1f : 0f;
        UseTransition(AnimChannel.Opacity, mounted.Value ? 1f - target : target, target,
            ms, Easing.Linear, dim);
        mounted.Value = true;
        return new BoxEl
        {
            Grow = 1f, Fill = Prop.Of(() => Tok.FillSmoke), Opacity = target,
            HitTestVisible = false,
        };
    }
}

sealed class ShellNarrowDrawerPane : Component
{
    readonly Signal<bool> _open;
    readonly IReadSignal<Size2> _viewport;
    readonly Signal<float> _expandedWidth;
    readonly Signal<bool> _drawerCompact;
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;

    public ShellNarrowDrawerPane(Signal<bool> open, IReadSignal<Size2> viewport, Signal<float> expandedWidth,
        Signal<bool> drawerCompact, Signal<Route> route, Action<string, string?> go)
    {
        _open = open; _viewport = viewport; _expandedWidth = expandedWidth; _drawerCompact = drawerCompact;
        _route = route; _go = go;
    }

    public override Element Render()
    {
        bool open = _open.Value;
        var mounted = UseRef(false);
        float width = ShellResponsiveLayout.DrawerWidth(_viewport.Peek().Width, _expandedWidth.Peek());
        float ms = Motion.ReducedMotion ? 0f : 300f;
        float target = ShellResponsiveLayout.DrawerRestingTranslateX(open, width);
        UseTransition(AnimChannel.TranslateX, mounted.Value ? (open ? -width : 0f) : target, target,
            ms, Easing.SmoothOut, open);
        mounted.Value = true;

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Grow = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
            // Keep the resting position coupled to the live width. A closed drawer can grow while the viewport is
            // resized inside the narrow band; retaining the old static translation would expose the newly-added strip.
            // The transition channel owns the in-flight value, then this binding supplies the exact settled geometry.
            Transform = Prop.Of(() => Affine2D.Translation(
                ShellResponsiveLayout.DrawerRestingTranslateX(
                    _open.Value,
                    ShellResponsiveLayout.DrawerWidth(_viewport.Value.Width, _expandedWidth.Value)),
                0f)),
            Width = Prop.Of(() => ShellResponsiveLayout.DrawerWidth(
                _viewport.Value.Width, _expandedWidth.Value)),
            // A stock OVERLAY PANE, not a dialog: in-app ACRYLIC + flyout elevation, exactly what WinUI's own
            // NavigationView paints for its minimal-mode pane (NavigationViewDefaultPaneBackground =
            // AcrylicInAppFillColorDefault, Shadow = Flyout — see NavigationView.cs "Overlay pane"). The old
            // dead-opaque FloatingChrome plate + Elevation.Dialog (blur 64) read as a modal slab dropped on the
            // window; the drawer is a transient light-dismiss surface and has to say so. Fill stays transparent —
            // the acrylic layer IS the surface (the OverlayHost popup-chrome idiom), and its own FallbackColor
            // covers the no-blur path, so this never degrades to a hole.
            Fill = ColorF.Transparent,
            Acrylic = Tok.AcrylicFlyout,
            BorderWidth = 1f, BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
            Corners = new CornerRadius4(0f, Radii.Card, Radii.Card, 0f),
            Shadow = Elevation.Flyout, HitTestVisible = open,
            // A SECOND, independent SidebarHost mount (its own hooks / scroll / mode-component instance) sharing the same
            // width signal and the same SidebarPreferences — one mode, one state, two mounts.
            Children = [Embed.Comp(() => new SidebarHost(_route, _go, _drawerCompact, _expandedWidth, inDrawer: true))],
        };
    }
}
