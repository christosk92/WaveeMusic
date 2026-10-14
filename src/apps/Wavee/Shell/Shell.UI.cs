// ── Shell/Shell.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// window frame (T4a: the host of `Shell.Frame`), the core side of the merged chrome row (T4b: the picture is `Shell.TitleBar`), drill trail,
// narrow drawer (host), not-found page, network chrome, file-drop target + cue (LocalFileActions), the notification panel and its rows (A9)
//
// Role: UI
// Owner: I
// Wave: 4
// Budget: 2600 lines
// Spec: ch 18 §9 (frame UI 3,250, less +Shell.Masthead.UI.cs's 950) + ch 19 §9.4 (300 of its Shell.UI.cs +2,000 share — the panel and its rows; the other 1,700 is +Shell.Overlays.UI.cs)
//
// THE FRAME, BOTTOM-UP (ch 18 §1-§6). Everything under `OverlayHost` is one ZStack:
//
//   tinted[ MaterialLayer · column ]  ·  Stage.Root  ·  Video.PipLayer  ·  Video.FullscreenLayer  ·  Overlays
//         ·  file-drop cue  ·  CommandPalette  ·  drag preview
//
//   The video layers sit BELOW the banner, toast and drop-cue layers: the video is a hole in the UI swapchain (a DestOut
//   erase over a DComp visual beneath it), so everything drawn BEFORE it is erased where it overlaps and everything
//   drawn AFTER it covers the picture. Those layers are pass-through, so the mini player still takes its drag.
//
//   column = 14 zero-size chord boxes · [chrome row] · content region · [player dock]
//            └ the bracketed bands and the content region COLLAPSE (bound Visible, still mounted) under full-screen video
//              OR the fullscreen stage (FrameModel.ChromeMounted): out of layout, paint and hit-test, subtree inactive
//   content region = ZStack[ row(sidebar column · content card · rail gap · rail reservation) · sidebar seam ·
//                            rail overlay(Shell.Panel) · rail seam · narrow drawer ]
//
// T4a (themes plan 6.1): the frame's PICTURE moved to the structure. `FrameRoot` below is the core host (hooks, effects, state
// machines; [CoreHost]) and mounts `Shell.Frame`; the column, the layers and the content region are
// `Themes\Classic\Structure\Views\Frame\ClassicFrame.cs`. The content region's four states (a page, an empty body, the not-found
// page, the offline lane) are decided here (`FrameRules.ContentBodyFor` / `ShowsOfflineStrip`) and drawn by `Shell.ContentState`
// (`ClassicContentState.cs`); the keep-alive boundary and its page transition stay in `ContentHost`.
//
// T4c: the frame's small views are the structure's too. The material, the file-drop cue, the command palette, the setup cover
// scrim, the drag chip and the frame-rate overlay are child views the frame mounts from their props (`ShellFrameProps`); the
// narrow drawer (`NarrowDrawer` below) is a [CoreHost] that keeps the Escape key-preview chain and mounts `Shell.NarrowDrawer`; the
// back/forward history menu is `Shell.HistoryMenu`, mounted by `NavHistoryButton`. The pictures are
// `Themes\Classic\Structure\Views\{Drawer,FrameBits,Material,Palette}`; the decisions (the file-drop target, `Setup.Covering`, the drag
// chip resolver, the FPS rule) are the props this file builds.
//
// THE RULE THIS FILE KEEPS: it lays out and binds, it never decides. Every geometry/precedence/gating answer below is a
// call into `Shell.FrameRules`, `Shell.Chrome`, `TabWorkspace`, `Notify` or an owner's rule table — each pinned by a
// test. The frame root renders ONCE for the process: every live value reaches it through a bound prop, a `Flow.Show`
// predicate or an effect, so a resize, a navigation or a theme flip never re-runs this render body.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using FgMotion = FluentGpu.Dsl.Motion;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Shell
{
    // ══ 1. THE MOUNT POINTS AND THE SEAMS ══════════════════════════════════════════════════════════════════════════

    /// <summary>Install the UI half: the frame as the root, the Wave-4 pages, the panel launcher. Called ONCE by the
    /// composition root before <see cref="Run"/>.</summary>
    // MOUNT POINT (stage B contract)
    public static void InstallUi()
    {
        RootFactory = static () => Frame();
        SetPage(RouteKind.History, static (in Route _) => HistoryPage());
        if (NotificationsLauncher is null) NotificationsLauncher = OpenNotificationPanel;

        Actions.InstallUi();   // G-195: Stage.NowPlayingMenu

        // G-193: a rich-text anchor's entity uri → the app's route key, through the SAME table `GoTo` uses.
        Controls.RouteForUri = static uri =>
        {
            var route = For(EntityUri.Parse(uri));
            return route.Kind == RouteKind.NotFound ? null : NameOf(route);
        };

        // G-027: the shell's own three ActionServices verbs. Play/PlayNext/AddToQueue/StartRadio are the queue owner's;
        // IsSaved/SetSaved are User.cs's; every entity file's AppActions.Register call fills the context-menu table —
        // none of those seams live in this file, so they are left null here rather than guessed at.
        Actions.Services.Go = static route => GoTo(route);
        Actions.Services.CurrentRoute = static () => Current.Peek();
        Actions.Services.CurrentDestination = static () =>
        {
            var route = Current.Peek();
            return SidebarPinId.FromRoute(NameOf(route)) is not null ? route : null;   // a destination is what the sidebar can pin
        };
        // The extension registry itself — the customizer's action picker and every BOUND row resolve through it.
        // Idempotent: a second InstallUi (a login-gate re-run) replaces `Registry.Current` with a freshly-built table
        // rather than double-registering into a live one (`Registry.Build`'s own contract).
        Actions.Services.Extensions = Actions.Registry.Build(Actions.Services);
    }

    /// <summary>The window frame. Mounted once by <see cref="RootFactory"/>.</summary>
    // MOUNT POINT (stage B contract)
    public static Element Frame()
    {
        SeedFrameState();
        return Embed.Comp(static () => new FrameRoot());
    }

    /// <summary>The notification panel's body. Opened through <see cref="OpenNotificationPanel"/> (the bell, or the
    /// profile menu's row when the ladder folds the bell).</summary>
    // MOUNT POINT (stage B contract)
    public static Element NotificationPanel() => Embed.Comp(static () => new NotificationPanelView());

    /// <summary>Navigation-frame attribution (0.2.9 <c>NavigationFrameWatch.NoteRoute</c>, which has no 0.3 home yet):
    /// the content host reports every route it activates here. Null = nobody is attributing frames.</summary>
    public static Action<Route>? RouteNoted;

    // The frame's context captures and node handles are ROOT-LIFETIME state (RootScope.cs): each belongs to the tree that
    // mounted it, so a root remount starts them empty and nothing in the new tree can point at a node of the frozen one.
    static readonly RootState<InputHooks?> s_hooksState = new("Shell.Hooks");
    static readonly RootState<SceneStore?> s_sceneState = new("Shell.Scene");
    static readonly RootState<NodeHandle> s_contentRegionState = new("Shell.ContentRegion");
    static readonly RootState<NodeHandle> s_sidebarColumnState = new("Shell.SidebarColumn");

    /// <summary>The frame's context captures, read by the static key handlers and verbs (never a render input).</summary>
    static InputHooks? s_hooks { get => s_hooksState.Value; set => s_hooksState.Value = value; }
    static SceneStore? s_scene { get => s_sceneState.Value; set => s_sceneState.Value = value; }
    static NodeHandle s_contentRegion { get => s_contentRegionState.Value; set => s_contentRegionState.Value = value; }
    /// <summary>The sidebar column's node, read only by the pane-invariant probe (never a render input).</summary>
    static NodeHandle s_sidebarColumn { get => s_sidebarColumnState.Value; set => s_sidebarColumnState.Value = value; }

    /// <summary>The shell MATERIAL cell the page tints publish into (provided through <see cref="ShellMaterial.Slot"/>)
    /// and the neutral owner the content host claims it with for every route that has no colour of its own.</summary>
    internal static readonly Signal<ShellMaterialState> MaterialState = new(default);
    static readonly object s_neutralMaterialOwner = new();

    static bool s_frameSeeded;

    /// <summary>The persisted chrome state the frame's first layout must already have (no startup animation). Runs once,
    /// before the root component exists, so no subscriber sees the writes.</summary>
    static void SeedFrameState()
    {
        if (s_frameSeeded) return;
        s_frameSeeded = true;
        PanelHost.Shared.SeedSizes();
        if (Session.ShellSection is { } shell) PanelHost.Shared.RestoreSession(shell.RailOpen, shell.RailMode);
    }

    // ══ 2. THE FRAME ROOT ═════════════════════════════════════════════════════════════════════════════════════════

    // CLAMP-ONLY: no `collapsed` argument, so the engine detent is off and the raw cell reaches the rail floor. The mode,
    // the approach band and the fade are all SidebarResizeRules (read off Sidebar.Seam).
    static Splitter.SplitterOptions SidebarSeamOptionsFor(Theming.NavPanePolicy nav) => new()
    {
        Min = nav.RailWidth, Max = nav.MaxWidth, ShowIndicator = false,
    };

    // The indicator is the drag GUIDE (F243): the rail seam writes Ui.RailDragWidth, so nothing else moves until release;
    // BuildRailSeamParts shows the 2-DIP thumb only while a drag is in flight (never on hover).
    static Splitter.SplitterOptions RailSeamOptionsFor(Theming.PanelPolicy panel) => new()
    {
        Min = panel.MinWidth, Max = panel.MaxWidth, Polarity = SplitterPolarity.Leading, ShowIndicator = true, InvertCollapsed = true,
    };

    // The shell chords. KeyPreview is modifier-blind, so every chord rides the dispatcher's accelerator seam (matched after
    // focused routing declines it). Zoom is spelled per VK: Ctrl+Shift+= types "+" on many layouts and the keypad keys are
    // distinct VKs — eight chords, three verbs.
    static readonly KeyAccelerator NewTabChord = new(Keys.T, KeyModifiers.Ctrl);
    static readonly KeyAccelerator PaletteChord = new(Keys.K, KeyModifiers.Ctrl);
    static readonly KeyAccelerator FindChord = new(Keys.F, KeyModifiers.Ctrl);
    static readonly KeyAccelerator BackChord = new(Keys.Left, KeyModifiers.Alt);
    static readonly KeyAccelerator ForwardChord = new(Keys.Right, KeyModifiers.Alt);
    static readonly KeyAccelerator FullscreenChord = new(Keys.F11, KeyModifiers.None);
    static readonly KeyAccelerator ZoomInChord = new(Keys.OemPlus, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomInShiftChord = new(Keys.OemPlus, KeyModifiers.Ctrl | KeyModifiers.Shift);
    static readonly KeyAccelerator ZoomInPadChord = new(Keys.Add, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomOutChord = new(Keys.OemMinus, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomOutShiftChord = new(Keys.OemMinus, KeyModifiers.Ctrl | KeyModifiers.Shift);
    static readonly KeyAccelerator ZoomOutPadChord = new(Keys.Subtract, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomResetChord = new(Keys.D0, KeyModifiers.Ctrl);
    static readonly KeyAccelerator ZoomResetPadChord = new(Keys.NumPad0, KeyModifiers.Ctrl);

    static BoxEl Chord(KeyAccelerator chord, Action onFire)
        => new() { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false, Accelerator = chord, OnClick = onFire };

    /// <summary>The fourteen chord boxes the frame's column mounts first, in this order. Behaviour the shell owns (what a chord
    /// DOES is <see cref="FrameVerbs"/>), built once; the structure's frame places them (<c>ShellFrameProps.Chords</c>).</summary>
    static readonly IReadOnlyList<Element> s_chords =
    [
        Chord(NewTabChord, static () => FrameVerbs.Shared.NewTab()),
        Chord(PaletteChord, static () => FrameVerbs.Shared.TogglePalette()),
        Chord(FindChord, static () => FrameVerbs.Shared.FocusSearch()),
        Chord(BackChord, static () => FrameVerbs.Shared.GoBack()),
        Chord(ForwardChord, static () => FrameVerbs.Shared.GoForward()),
        Chord(FullscreenChord, static () => FrameVerbs.Shared.ToggleFullscreen()),
        Chord(ZoomInChord, static () => FrameVerbs.Shared.Zoom(+1)),
        Chord(ZoomInShiftChord, static () => FrameVerbs.Shared.Zoom(+1)),
        Chord(ZoomInPadChord, static () => FrameVerbs.Shared.Zoom(+1)),
        Chord(ZoomOutChord, static () => FrameVerbs.Shared.Zoom(-1)),
        Chord(ZoomOutShiftChord, static () => FrameVerbs.Shared.Zoom(-1)),
        Chord(ZoomOutPadChord, static () => FrameVerbs.Shared.Zoom(-1)),
        Chord(ZoomResetChord, static () => FrameVerbs.Shared.Zoom(0)),
        Chord(ZoomResetPadChord, static () => FrameVerbs.Shared.Zoom(0)),
    ];

    /// <summary>The window's core host (T4a): every hook, effect and state machine of the frame, and ONE props record for the
    /// structure's <c>Shell.Frame</c> view. It builds no element and reads no token: the picture is the theme's.</summary>
    [CoreHost]
    sealed class FrameRoot : Component
    {
        readonly DropTargetSpec _fileDrop = new([DropKinds.Files],
            OnEnter: static _ => FileDropOver.Value = true,
            OnLeave: static _ => FileDropOver.Value = false,
            OnDrop: static s =>
            {
                FileDropOver.Value = false;
                if (s.Payload is FileDropData { Count: > 0 } files) OnFilesDropped?.Invoke(files.Paths);
            });

        readonly Signal<float> _sidebarFade = new(1f);
        readonly Signal<float> _railFade = new(1f);
        readonly Signal<bool> _sidebarDragging = new(false);
        readonly Signal<bool> _railDragging = new(false);
        /// <summary>A module watch page's stage would host what is playing — split from the resize-rate effect so the
        /// playable-uri reads run at navigation/track rate, not per resize pixel.</summary>
        readonly Signal<bool> _pageStageHosts = new(false);

        bool _bandSeeded;
        /// <summary>The window and content tiers of this root (T3b): created with the first render on the ACTIVE structure's
        /// ladders, so a theme switch (a new root) re-resolves them with the new structure's.</summary>
        WindowTiers.Binding? _tiers;
        TemplateParts? _seamParts;
        TemplateParts? _railSeamParts;
        readonly ZoomService.AutoLoop _autoZoom = new();
        Video.PlacementState _videoWas = Video.State.Surface.Peek();
        bool _railWas = Ui.RailOpen.Peek();
        Video.DockedHost _hostWas = Video.DockedHost.Rail;

        /// <summary>This root's state scope (<see cref="RootScope"/>): adopted at the first render, ended by the unmount cleanup of
        /// the process-lifetime effect below. A theme switch unmounts this component and mounts a new one.</summary>
        RootScope? _scope;

        public override Element Render()
        {
            if (_scope is null)
            {
                _scope = RootScope.Begin();
                // The omnibar model is process state; the facts its mounted forms hold go with this root.
                _scope.OnDispose(static () => s_omnibar.ResetMountedFacts(SearchFocusRequest.Peek()));
            }
            s_hooks = UseContext(InputHooks.Current);
            s_scene = Context.Scene;
            Appearance.BindTransition(UseContext(ThemeControl.Request));
            var post = UsePost();
            var overlay = UseContext(Overlay.Service);
            _seamParts ??= BuildSeamParts(overlay);
            _railSeamParts ??= BuildRailSeamParts(_railDragging);
            // The structure's policies, read tracked: a theme switch or a policy-moving option re-renders the frame with the new seams.
            var policies = ShellPolicy.Tracked;
            // Float the engine's toast lane above the docked player bar (the idempotent registration idiom).
            Toast.EdgeInset = policies.PlayerBar.Height;
            var vp = UseContextSignal(Viewport.Size);
            var zoom = UseContextSignal(Viewport.Zoom);

            // THE WINDOW TIERS (T3b, plan 3.2-3.3): resolved here from the layout viewport through the active structure's
            // ladders. A first mount is final in its own frame; a crossing after mount is committed by the host on the first
            // frame that is not resized (the resized frame keeps the old tier), so the band below changes on that frame and
            // the pane, the content card and the player bar FLIP instead of snapping.
            var tiers = _tiers ??= WindowTiers.ForActiveStructure(vp);
            tiers.Begin(Context);

            // The window band's FIRST value is known only here; seed it before the column mounts so a small launch lays out as
            // a rail (or no pane) on frame one (no subscriber reads it yet).
            if (!_bandSeeded)
            {
                _bandSeeded = true;
                var size0 = vp.Peek();
                var pane0 = FrameModel.Seed(new WindowFacts(size0.Width, size0.Height), tiers.WindowNow,
                    Sidebar.UserCollapsed.Peek(), Sidebar.Editing.Peek(), Sidebar.Width.Peek(), policies.NavPane);
                Sidebar.Band.SetIfChanged(pane0.Band);
                Sidebar.Mode.SetIfChanged(pane0.Mode);
                Sidebar.PresentedWidth.SetIfChanged(pane0.PresentedWidth);
            }
            // The content tier is estimated from the sidebar the seed above just settled, then the facts are published.
            tiers.Complete(Context);

            // THE WINDOW BAND (design V.1): the window tier read as a band — the ONLY way the window width reaches the sidebar.
            UseSignalEffect(() =>
            {
                var current = Sidebar.Band.Peek();
                // The band is the window tier (another structure's ladder re-bands the window in place, with its hysteresis).
                var next = FrameModel.BandOf(tiers.Window);
                if (next != current) Sidebar.Band.Value = next;
                // Tracked, and also when the band did not move: a policy whose band stopped having an overlay closes it.
                if (FrameModel.ClosesOverlay(next, ShellPolicy.Tracked.NavPane)) Sidebar.OverlayOpen.SetIfChanged(false);
            });
            // The title bar's pane toggle is disabled while the sidebar is being edited (a plain signal mirrored here).
            UseSignalEffect(static () => s_paneToggleEnabled.SetIfChanged(
                SidebarPaneModeRules.CanTogglePane(Sidebar.Band.Value, Sidebar.Editing.Value, ShellPolicy.Tracked.NavPane)));
            // THE ONE PRESENTATION EFFECT: ① the mode, ② the presented width. A live drag presents Track(seam). It never
            // writes Sidebar.Width or UserCollapsed (those are the preference, persisted at commit).
            UseSignalEffect(() =>
            {
                var size = vp.Value;
                var band = Sidebar.Band.Value;
                bool editing = Sidebar.Editing.Value;
                bool collapsed = Sidebar.UserCollapsed.Value;
                float expandedWidth = Sidebar.Width.Value;
                // The seam is read only while a drag is being presented (a conditional subscription, as always).
                var nav = ShellPolicy.Tracked.NavPane;
                bool dragging = FrameModel.PresentsDrag(_sidebarDragging.Value, band, nav);
                float seam = dragging ? Sidebar.Seam.Value : 0f;
                var pane = FrameModel.Present(new WindowFacts(size.Width, size.Height), band, collapsed, editing, expandedWidth, dragging, seam, nav);
                if (dragging)
                {
                    Sidebar.Mode.SetIfChanged(pane.Mode);
                    Sidebar.PresentedWidth.SetIfChanged(pane.PresentedWidth);
                    _sidebarFade.SetIfChanged(pane.Fade);
                    return;
                }
                _sidebarFade.SetIfChanged(pane.Fade);
                Sidebar.Mode.SetIfChanged(pane.Mode);
                Sidebar.PresentedWidth.SetIfChanged(pane.PresentedWidth);
            });
            UseSignalEffect(() => FgMotion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, _sidebarDragging.Value));
            // F243: a rail drag ends (the splitter clears `dragging` AFTER its commit). A COMMITTED drag already made the
            // preview equal; a CANCELLED one never commits, so the preview falls back to the committed width here.
            UseSignalEffect(() =>
            {
                if (_railDragging.Value) return;
                float committed = Ui.RailWidth.Peek();
                if (Ui.RailDragWidth.Peek() != committed) Ui.RailDragWidth.Value = committed;
            });
            // Session chrome, not a preference: reopening restores the rail exactly as it was.
            UseSignalEffect(static () => Session.CaptureShell(Ui.RailOpen.Value, (int)Ui.Mode.Value));

            // ── large-display auto-zoom (ch 18 W24; plan 3.2, C1b): the window's base extent → the pure control loop ──
            // A SETTLED size (a maximize, restore, Snap, a drag release, a DPI or monitor change) steps HERE, synchronously: this
            // effect runs in the layout phase of the resized frame that carries the size, so the new zoom is written there
            // (the zoom write gives one more resized frame) and the old zoom is never on screen for 500 ms. Inside the OS modal
            // size-move loop nothing is rescaled (the drag's end is the settled size). The base is the viewport times the zoom,
            // zoom-invariant, so our own write re-fires this effect with the same base: Unchanged. The zoom is Peeked (the host
            // writes the viewport and the zoom in the same paint, before any effect runs); a zoom someone else moved is the
            // trailing effect's business below.
            var modal = UseContextSignal(Viewport.InModalLoop);
            UseSignalEffect(() =>
            {
                var size = vp.Value;
                bool inModal = modal.Value;
                float z = zoom.Peek();
                float baseW = size.Width * z, baseH = size.Height * z;
                ZoomService.NoteWindow(baseW);
                if (_autoZoom.Arrive(Environment.TickCount64, baseW, baseH, inModal) == ZoomService.SizeArrival.Settled)
                    _autoZoom.Step(ZoomService.Mode, baseW, baseH, z, static run => run());
            });
            // A STREAM of sizes (a second one within DebounceMs of the previous, e.g. a scripted resize animation) waits here for
            // the quiet; an isolated size was already settled above and finds nothing left to do. The debounce is only the
            // quiet timer: the step uses the loop's LATEST base, never the debounced copy, which a settled step above may have
            // outrun (a stale base would undo the zoom just written). The zoom is tracked, as it always was: a Ctrl+-, the wheel
            // or the palette moving it in Auto re-runs this at once, and the loop pins Manual instead of undoing the pick.
            var quiet = UseDebouncedValue(() =>
            {
                var size = vp.Value;
                float z = zoom.Value;
                return new Size2(size.Width * z, size.Height * z);
            }, ZoomService.DebounceMs);
            UseSignalEffect(() =>
            {
                _ = quiet.Value;
                float z = zoom.Value;
                if (_autoZoom.TryGetLatest(out float baseW, out float baseH))
                    _autoZoom.Step(ZoomService.Mode, baseW, baseH, z, post);
            });
            // Zoom persistence: the settled value only, so a held chord or a wheel spin writes once.
            var zoomSettled = UseDebouncedValue(() => zoom.Value, ZoomService.DebounceMs);
            UseSignalEffect(() => ZoomService.PersistSettled(zoomSettled.Value));

            // ── the process-lifetime subscriptions (stage-2 wiring, owner-I report) ──
            var rootScope = _scope;
            UseEffect(() =>
            {
                var hooks = s_hooks;
                Action<int> onNav = static which => { if (which == 0) GoBack(); else GoForward(); };
                Func<float, bool> onWheel = static notch => { ZoomStep(notch > 0f ? +1 : -1); return true; };
                Action<string> onRedirect = _ => post(s_drainActivation);
                Action onColors = Appearance.OnSystemColorsChanged;
                FluentApp.AppNavigationCommand += onNav;
                FluentApp.ActivationRedirected += onRedirect;
                FluentApp.SystemColorsChanged += onColors;
                if (hooks is not null) hooks.ZoomWheel = onWheel;
                Diagnostics.SidebarPaneFrame = s_paneFrame;
                // The toast activation → deep-link hop: a toast reaches exactly the destinations a link can.
                Notify.HostInstall(post, static raw => ApplyDeepLink(raw));
                DrainActivation();   // a payload parked before the window existed
                // G-010: the window is guaranteed to exist by the time the frame's mount effect runs (this component
                // IS the window's content), so activation no longer waits on the FIRST reducer effect to happen to
                // land after it. `Activate` itself is idempotent (`s_active` gate) and self-heals a hwnd of 0.
                Playback.Os.Activate(FluentApp.WindowHandle, Playback.Snap());
                return () =>
                {
                    FluentApp.AppNavigationCommand -= onNav;
                    FluentApp.ActivationRedirected -= onRedirect;
                    FluentApp.SystemColorsChanged -= onColors;
                    if (hooks is not null && ReferenceEquals(hooks.ZoomWheel, onWheel)) hooks.ZoomWheel = null;
                    if (ReferenceEquals(Diagnostics.SidebarPaneFrame, s_paneFrame)) Diagnostics.SidebarPaneFrame = null;
                    RootScope.End(rootScope);   // LAST: the root-lifetime state (handles, overlays, pivots) goes with this tree
                };
            }, DepKey.Empty);

            // ── the published derived facts ──
            UseSignalEffect(static () =>
                Auth.SetIfChanged(FoldAuth(Spotify.Status.Value, Spotify.Fault.Value, Platform.Args.Fake || Platform.HasStoredCredential())));   // --fake presents its seeded account as signed in (G-065)
            UseSignalEffect(() => _pageStageHosts.SetIfChanged(
                Video.DockedHosting.PageStageHosts(Ui.ActiveStagePlayable.Value, Playback.CurrentId.Value.Text)));
            UseSignalEffect(() =>
            {
                Ui.RailFits.SetIfChanged(Ui.CanFitRail(vp.Value.Width, Sidebar.PresentedWidth.Value, Ui.RailWidth.Value,
                    ShellPolicy.Tracked.Panel.InlineMin));
            });
            // A policy change (a theme switch) moves the panel's bounds: the committed width follows them. The stored preference is
            // left alone, so the next launch loads it again through the new bounds.
            UseSignalEffect(() => PanelHost.Shared.FitWidthTo(ShellPolicy.Tracked.Panel));
            // The video host-capability REPORT (an input to the availability intersection, never chrome state). Reads the
            // published fit, so it runs when a fact flips rather than per resize pixel.
            UseSignalEffect(() =>
            {
                bool fits = Ui.RailFits.Value;
                bool pageStage = _pageStageHosts.Value;
                var hooks = s_hooks;
                var cap = (Video.DockedHosting.DockedHostAvailable(fits, pageStage) ? Video.PlacementSet.Docked : Video.PlacementSet.None)
                        | Video.PlacementSet.Floating
                        | ((hooks?.CanOpenDetachedWindow?.Invoke() ?? true) ? Video.PlacementSet.Detached : Video.PlacementSet.None)
                        | (hooks?.WindowSetFullscreen is not null ? Video.PlacementSet.Fullscreen : Video.PlacementSet.None);
                Video.State.HostCapability.SetIfChanged(cap);
            });

            // ── the rail ↔ docked-video coupling (B1, B6, B7, B13): ONE effect over the two drivers, reacting to the RESULT
            //    of whatever wrote them. The writes are posted: both drivers are read here. ──
            UseSignalEffect(() =>
            {
                bool railOpen = Ui.RailOpen.Value;
                var state = Video.State.Surface.Value;
                var host = Video.DockedHosting.HostFor(Video.PlacementCore.Resolve(state), Ui.ActiveStagePlayable.Value,
                    Playback.CurrentId.Value.Text);
                bool wasOpen = _railWas;
                var prev = _videoWas;
                var hostBefore = _hostWas;
                _railWas = railOpen;
                _videoWas = state;
                _hostWas = host;

                RailMode? dockMode = state.Requested == Video.SurfacePlacement.Docked && prev.Requested != Video.SurfacePlacement.Docked
                    ? Rail.VideoCoupling.ModeOnDock(railOpen, host) : null;
                var demoteTo = !railOpen && wasOpen ? Rail.VideoCoupling.OnRailClosed(state, host) : Video.SurfacePlacement.None;
                bool reDock = railOpen && !wasOpen && Rail.VideoCoupling.ReDockOnRailOpen(state);
                bool leftDock = prev.Requested == Video.SurfacePlacement.Docked && state.Requested != Video.SurfacePlacement.Docked;
                bool closeRail = Rail.VideoCoupling.CloseRailOnVideoLeft(Ui.Mode.Peek(), leftDock, hostBefore);
                if (dockMode is not null || demoteTo != Video.SurfacePlacement.None || reDock || closeRail)
                    post(CouplingWrites(dockMode, demoteTo, reDock, closeRail));
            });

            // ── the merged chrome row's allocation: recomputed per pixel, published only when a stage flips ──
            UseSignalEffect(static () =>
            {
                _ = TabsVersion.Value;
                float seeded = Chrome.SeedTabExtent(s_tabExtent.Peek(), Tabs.Count, Tabs.PinnedCount, ShellPolicy.Tracked.Chrome.Budget);
                s_tabExtent.SetIfChanged(seeded);
            });
            UseSignalEffect(() =>
            {
                float w = vp.Value.Width;
                _ = TabsVersion.Value;
                float extent = s_tabExtent.Value;
                var old = ChromeLayout.Peek();
                // The identity chip's FORM is a budget input, not a decoration: "Connecting" and "Reconnect" are 58-68
                // DIP wider than the avatar, well past the 16-DIP gutter cushion (#88). Reading it here also subscribes
                // this effect to the auth fold, so a sign-in or a resume RE-ALLOCATES the row instead of overflowing it.
                var next = Chrome.Resolve(w, extent, old, FrameRules.ChipFor(Auth.Value), ShellPolicy.Tracked.Chrome.Budget);
                if (FrameRules.ReissueSearchFocus(old.SearchMode, next.SearchMode, s_omnibar.SearchFocused.Peek(), s_omnibar.FlyoutOpen.Peek()))
                    s_omnibar.Focus();
                ChromeLayout.SetIfChanged(next);
            });

            // ── the material mirror: the page channel → the model's TintOwner (the chrome's settled colour fact) ──
            UseSignalEffect(static () =>
            {
                var m = MaterialState.Value;
                bool neutral = m.Owner is null || ReferenceEquals(m.Owner, s_neutralMaterialOwner) || (m.Tint is null && m.Wash is null);
                Material.SetIfChanged(neutral
                    ? TintOwner.Neutral
                    : new TintOwner(TintClaimantOf(Current.Peek()), m.Tint is { } t ? PackArgb(t) : 0u, true));
            });

            // ── the track-boundary announce (only when an AT client listens) ──
            UseSignalEffect(static () =>
            {
                var cur = Playback.Current.Value;
                if (cur.IsNone || cur.Kind != EntityKind.Track || !Announcer.IsAvailable) return;
                var track = new Track(cur.Slot);
                string title = track.Title;
                if (title.Length == 0) return;
                string artist = Entities.Strings.Resolve(track.ArtistLineId);
                Announcer.SayThrottled(artist.Length == 0 ? title : title + ", " + artist);
            });

            // THE PICTURE IS THE STRUCTURE'S (T4a). Everything above wired the window's hooks, effects and state machines; the
            // column, the layers, the content region and its seams are the `Shell.Frame` template's. The root still renders ONCE
            // for the process: the props freeze at mount, and every live value reaches the template through a signal or a Func.
            var frame = ViewSlots.View(ViewKeys.ShellFrame, FrameProps(vp, policies));

            // HomeLayout (F29): one app-wide document signal, provided at the shell root beside the material cell so
            // HomeScreen and CustomizeScreen both read it and a customize edit is visible on Home the same frame.
            return Ctx.Provide(HomeLayout.Slot, HomeLayout.Instance,
                Ctx.Provide(ShellMaterial.Slot, MaterialState, OverlayHost.Create(frame)));
        }

        /// <summary>The props of the structure's frame: the live values as read-only signals and Funcs, the behaviour this host wired
        /// (chords, key handler, file-drop target, realized-node captures, the two seams' splitters) and the element builders of the
        /// surfaces that have no key of their own yet. Nothing here is presentation.</summary>
        ShellFrameProps FrameProps(IReadSignal<Size2> vp, Theming.ThemePolicies policies) => new()
        {
            Viewport = vp,
            SidebarFade = _sidebarFade,
            RailFade = _railFade,
            SidebarPresentedWidth = Sidebar.PresentedWidth,
            RailWidth = Ui.RailWidth,
            ChromeMounted = s_chromeMounted,
            ImmersiveOpen = static () => Ui.ImmersiveLyrics.Value,
            SidebarPaneDocked = static () => Sidebar.Mode.Value != SidebarPaneMode.Minimal,
            RailFloats = static () => FrameRules.RailFloats(Ui.RailOpen.Value, Ui.RailFits.Value),
            // THE W12 TRAP lives in FrameRules.SidebarPaneWidth: a drag peek presents the pane expanded, so the clipped column widens with it.
            SidebarColumnWidth = static () => FrameRules.SidebarPaneWidth(Sidebar.DragPeek.Value, Sidebar.Width.Value, Sidebar.PresentedWidth.Value),
            SidebarSeamWidth = static () => FrameRules.SidebarSeamWidth(SidebarPaneModeRules.SeamVisible(Sidebar.Band.Value)),
            RailGapWidth = static () => FrameRules.RailGapWidth(Ui.RailOpen.Value, Ui.RailFits.Value),
            RailReservedWidth = static () => FrameRules.RailReservedWidth(Ui.RailOpen.Value, Ui.RailFits.Value, Ui.RailWidth.Value),
            RailSeamWidth = static () => FrameRules.RailSeamWidth(Ui.RailOpen.Value),
            RailSeamX = () => FrameRules.RailSeamX(vp.Value.Width, Ui.RailDragWidth.Value),
            Chords = s_chords,
            OnKeyDown = OnShellKey,
            FileDrop = _fileDrop,
            OnSidebarColumnRealized = static h => s_sidebarColumn = h,
            OnContentRegionRealized = static h => { s_contentRegion = h; PublishScrimClip(); },
            OnContentRegionBounds = static _ => PublishScrimClip(),
            TitleBar = TitleBarProps(),
            PlayerBar = PlayerBarHost.Props,
            Material = new MaterialProps { State = MaterialState },
            ContentHost = static () => Embed.Comp(static () => new ContentHost()),
            NavPane = Sidebar.DockedPaneProps,
            SidebarSeam = () => Splitter.Create(Sidebar.Seam, () => Sidebar.CommitSeam(), SidebarSeamOptionsFor(policies.NavPane),
                fade: _sidebarFade, dragging: _sidebarDragging, parts: _seamParts),
            Panel = RailWiring.Panel,
            RailSeam = () => Splitter.Create(Ui.RailDragWidth, PanelHost.CommitWidth, RailSeamOptionsFor(policies.Panel), collapsed: Ui.RailOpen,
                fade: _railFade, dragging: _railDragging, parts: _railSeamParts),
            NarrowDrawer = static () => Embed.Comp(static () => new NarrowDrawer()),
            StageRoot = Stage.RootProps,
            VideoPipLayer = static () => Video.PipLayer(),
            VideoFullscreenLayer = static () => Video.FullscreenLayer(),
            Overlays = static () => Overlays(),
            FileDropCue = new FileDropProps { Over = FileDropOver },
            CommandPalette = s_paletteProps,
            CoverScrim = s_coverScrimProps,
            DragChip = s_dragChipProps,
            Fps = s_fpsProps,
        };
    }

    static readonly Func<bool> s_chromeMounted = static () => FrameFacts.ChromeMounted();

    static readonly Action s_drainActivation = DrainActivation;

    static Action CouplingWrites(RailMode? dockMode, Video.SurfacePlacement demoteTo, bool reDock, bool closeRail) => () =>
    {
        if (dockMode is { } mode)
        {
            Ui.Mode.Value = mode;
            Ui.RailOpen.Value = true;
        }
        if (demoteTo != Video.SurfacePlacement.None) Video.State.Demote(demoteTo);
        if (reDock) Video.State.OpenAt(Video.SurfacePlacement.Docked);
        if (closeRail) Ui.RailOpen.Value = false;
    };

    static uint PackArgb(ColorF c)
    {
        static uint B(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return (B(c.A) << 24) | (B(c.R) << 16) | (B(c.G) << 8) | B(c.B);
    }

    // ── 2.1 the frame's verbs ───────────────────────────────────────────────────────────────────────────────────────

    static void DrainActivation()
    {
        string raw = TakePendingActivation();
        if (raw.Length > 0) ApplyDeepLink(raw);
    }

    // -- 2.2 the sidebar seam: keyboard, double-click, context menu ---------------------------------------------------

    /// <summary>Parts for the seam root: a focusable slider (left/right nudge, Shift x5, Home/End, Enter/Space toggles), a
    /// double-click that toggles the pane, and the layout context menu. Splitter re-asserts only its mechanics (size,
    /// cursor, drag handlers), so these survive.</summary>
    static TemplateParts BuildSeamParts(IOverlayService overlay)
    {
        var parts = new TemplateParts();
        parts[Splitter.PartRoot] = b => (b with
        {
            Focusable = true, TabStop = true, AllowFocusOnInteraction = false, Role = AutomationRole.Slider,
            OnKeyDown = OnSeamKey,
            OnPointerPressed = static e => { if (e.ClickCount == 2) Sidebar.TogglePane(); },
        }).WithContextMenu(overlay, () => SeamPaneMenu(overlay));
        return parts;
    }

    /// <summary>The seam's right-click is the sidebar's pane menu (§P4.6): the rows the footer ⋯ opens, on the same
    /// overlay host. Built here rather than through <c>PaneView.PaneMenu</c>, which is per-pane and not reachable from the
    /// seam's static parts.</summary>
    static ContextMenuModel? SeamPaneMenu(IOverlayService overlay)
    {
        Sidebar.SidebarMenus.Overlay = overlay;
        return new ContextMenuModel(Sidebar.SidebarMenus.Map(SidebarMenuModel.Pane(Sidebar.Layout.Peek(), Sidebar.State, Sidebar.Density.Peek(),
            Sidebar.Editing.Peek(), Sidebar.SidebarMenus.LockingNames())));
    }

    /// <summary>The rail seam's guide: the splitter's 2-DIP thumb, bound to the drag so it shows while the pointer is down
    /// and never on hover (the rail itself does not move until release).</summary>
    static TemplateParts BuildRailSeamParts(Signal<bool> dragging)
    {
        var parts = new TemplateParts();
        parts[Splitter.PartIndicator] = b => b with
        {
            HoverOpacity = float.NaN, PressedOpacity = float.NaN,
            Opacity = Prop.Of(() => dragging.Value ? 1f : 0f),
        };
        return parts;
    }

    static void OnSeamKey(KeyEventArgs e)
    {
        if (e.Handled) return;
        switch (e.KeyCode)
        {
            case Keys.Left: Sidebar.StepSeam(-1, e.Shift); break;
            case Keys.Right: Sidebar.StepSeam(+1, e.Shift); break;
            case Keys.Home: Sidebar.SetExpandedWidth(ShellPolicy.Now.NavPane.MinWidth); break;
            case Keys.End: Sidebar.SetExpandedWidth(ShellPolicy.Now.NavPane.MaxWidth); break;
            case Keys.Enter or Keys.Space: Sidebar.TogglePane(); break;
            default: return;
        }
        e.Handled = true;
        Announcer.Say(Sidebar.UserCollapsed.Peek()
            ? Loc.Get("sidebar.seam.announceCollapsed")
            : Loc.Format("sidebar.seam.announceWidth", ("width", (int)Sidebar.Width.Peek())));
    }

    /// <summary>F11 — closes the fullscreen stage if it is up; otherwise exits / enters video fullscreen; otherwise opens
    /// the stage (an empty stage is allowed; <c>Stage.Open</c> itself refuses over fullscreen video). With focus inside a
    /// player the element consumes F11 first (focused routing precedes accelerators), so exactly one of them fires.</summary>
    internal static void ToggleVideoFullscreen()
    {
        switch (FrameRules.F11(Video.State.Resolved == Video.SurfacePlacement.Fullscreen, Video.State.IsActive, Ui.ImmersiveLyrics.Peek()))
        {
            case FrameRules.FullscreenToggle.Enter: Video.State.EnterFullscreen(); break;
            case FrameRules.FullscreenToggle.Exit: Video.State.ExitFullscreen(); break;
            case FrameRules.FullscreenToggle.CloseStage: Stage.Close(null, "f11-shell"); break;   // the stage root normally takes F11 first; this is the unfocused fallback (no flight) — V-U43
            case FrameRules.FullscreenToggle.OpenStage: Stage.Open(null, "f11"); break;           // no shared-element flight from a key press; an empty stage is allowed
        }
    }

    /// <summary>Bare Escape and Space bubble here after every focused owner declined them. NOT accelerators (the
    /// dispatcher only matches Ctrl/Alt/F-keys) and NOT KeyPreview (a single slot the overlay host re-assigns).</summary>
    static void OnShellKey(KeyEventArgs e)
    {
        if (e.Handled || e.Mods != KeyModifiers.None) return;
        if (e.KeyCode == Keys.Escape)
        {
            switch (FrameRules.Escape(false, PaletteOpen.Peek(), Ui.ImmersiveLyrics.Peek(),
                        Video.State.Resolved == Video.SurfacePlacement.Fullscreen))
            {
                case FrameRules.EscapeAction.CloseImmersiveLyrics:
                    Stage.Close(null, "escape-shell");   // the stage root normally takes Esc first; this is the unfocused fallback
                    e.Handled = true;   // also stops the unhandled-Escape arm from clearing focus
                    break;
                case FrameRules.EscapeAction.ExitVideoFullscreen:
                    Video.State.ExitFullscreen();
                    e.Handled = true;
                    break;
            }
            return;
        }
        if (e.KeyCode == Keys.Space && FrameRules.SpaceTogglesPlayback(false, FocusedIsTextEditor()))
        {
            PlayerVerbs.Default.TogglePlay("space");
            e.Handled = true;
        }
    }

    /// <summary>Is a text editor focused? The sidebar's own Ctrl+Z/Ctrl+Y handler asks it (§P4.6); there is no frame chord.</summary>
    internal static bool FocusedIsTextEditor()
    {
        var focused = s_hooks?.GetFocus?.Invoke() ?? default;
        if (focused.IsNull || s_scene is not { } scene || !scene.IsLive(focused)) return false;
        ref var ix = ref scene.Interaction(focused);
        return ix.Role == AutomationRole.Text || (ix.HandlerMask & InteractionInfo.CharBit) != 0;
    }

    /// <summary>The pane-invariant probe's observation (<see cref="Diagnostics.SidebarPaneFrame"/>): the presentation facts
    /// as decided, plus the column's laid-out width. The band, the mode and the overlay are the sidebar's own signals.</summary>
    static readonly Func<SidebarPaneFrameSnapshot> s_paneFrame = SidebarPaneFrame;

    static SidebarPaneFrameSnapshot SidebarPaneFrame()
    {
        float rendered = s_scene is { } scene && !s_sidebarColumn.IsNull && scene.IsLive(s_sidebarColumn)
            ? scene.AbsoluteRect(s_sidebarColumn).W : Sidebar.PresentedWidth.Peek();
        return new SidebarPaneFrameSnapshot(Sidebar.Layout.Peek(), Sidebar.Mode.Peek(), Sidebar.Band.Peek(),
            Sidebar.UserCollapsed.Peek(), Sidebar.OverlayOpen.Peek(), Sidebar.Width.Peek(), Sidebar.PresentedWidth.Peek(), rendered);
    }

    static void PublishScrimClip()
    {
        if (s_scene is not { } scene || s_contentRegion.IsNull || !scene.IsLive(s_contentRegion)) return;
        RectF r = scene.AbsoluteRect(s_contentRegion);
        scene.SpotlightScrimClip = r.IsEmpty ? null : r;
    }

    // ── the small frame views' props (T4c) ──────────────────────────────────────────────────────────────────────────────
    //
    // The file-drop cue (`Shell.FileDrop`, bound to `FileDropOver`), the setup wizard's cover scrim (`Shell.CoverScrim`), the drag
    // chip layer (`Shell.DragChip`) and the frame-rate overlay (`Shell.Fps`) are the structure's pictures
    // (`Themes\Classic\Structure\Views\FrameBits\ClassicFrameBits.cs`); these are the decisions they bind to. Cached statics, so
    // an equal record skips the template.

    /// <summary>Does the setup wizard dim the shell? (<see cref="Setup.Covering"/>'s doc comment: the plate opens with the engine's own
    /// overlay scrim OFF, a shell is always behind it in 0.3, so the cover scrim is the only dim.)</summary>
    static readonly CoverScrimProps s_coverScrimProps = new() { Dim = static () => Setup.Covering.Value == Setup.Cover.Dim };

    /// <summary>What the dragged payload is called and shows: the app's chip resolver (<c>Drag.Preview</c>).</summary>
    static readonly DragChipProps s_dragChipProps = new() { Preview = static state => Drag.Preview(state) };

    /// <summary>The FPS HUD's rule: developer mode AND the overlay toggle, never in presentation mode.</summary>
    static readonly FpsProps s_fpsProps = new() { Shown = static () => Diagnostics.FpsOverlayOn() };

    // ══ 3. THE CONTENT HOST ═══════════════════════════════════════════════════════════════════════════════════════

    static readonly KeepAliveOptions s_keepAlive = new(
        // The live page plus a two-deep back stack; parked pages release their image pins.
        MaxEntries: KeepAliveSlots,
        TransitionFor: PageTransition,
        SuppressLayoutTransitionsOnActivation: true);


    /// <summary>The route whose page is ON SCREEN, as opposed to <see cref="Current"/>, the route just committed: the two
    /// differ for the exit leg of a page swap (<see cref="Design.Nav.ExitDurationMs"/>), while the old page is still
    /// leaving. Written by <see cref="ContentHost"/> once that leg has run; the tab strip labels the active tab from it
    /// (<see cref="TabLabelStaging"/>) so the label lands WITH the page instead of a frame ahead of it.</summary>
    public static readonly Signal<Route> Shown = new(Route.None);

    static readonly Action s_landShown = static () => Shown.Value = Current.Peek();

    /// <summary>The keep-alive page-swap boundary, the masthead band overlaid on it, and the offline lane. Pages receive
    /// route VALUES, never the route signal — the boundary is the only route subscriber.</summary>
    sealed class ContentHost : Component
    {
        public override Element Render()
        {
            // A floating surface at its default anchor reserves bottom space. The wrapper is UNCONDITIONAL: toggling it in
            // and out of the tree would remount the keep-alive boundary and cold-restart every cached page.
            float reserve = Video.FloatingSurfaceReserve.Value;

            // The staged label route (see Shown): armed ONCE from mount so boot's route lands, then RE-ARMED by the effect
            // below for every commit — the effect subscribes to the route, this render body never does. The lag is the
            // exit leg exactly; the instant cut and reduced motion run no exit leg, so there it lands on the next tick.
            var land = UseTimeout(s_landShown, TabLabelStaging.DelayMs(instantCut: false, reducedMotion: false));
            UseSignalEffect(() =>
            {
                _ = Current.Value;
                land.RestartIn(TabLabelStaging.DelayMs(PageMotionStyle() == Design.PageMotionStyle.None, FgMotion.ReducedMotion));
            });

            // The NEUTRAL half of the material hand-over: claimed for exactly the routes no page tints (disjoint sets,
            // so the two effects need no order).
            UseSignalEffect(static () =>
            {
                var r = Current.Value;
                if (!ClaimsMaterial(r))
                    ShellMaterial.Publish(MaterialState, s_neutralMaterialOwner, isClaim: true, definite: true, tint: null, wash: null);
            });
            // Frame attribution + the CLEARING half of the stage-playable claim (value-gated: an idle nav writes nothing).
            UseSignalEffect(static () =>
            {
                var r = Current.Value;
                RouteNoted?.Invoke(r);
                if (FrameRules.ClearsStagePlayable(r, Ui.ActiveStagePlayable.Peek())) Ui.ActiveStagePlayable.Value = "";
            });

            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, ZStack = true,
                Padding = new Edges4(0f, 0f, 0f, reserve),
                Children =
                [
                    // Clips exiting pages; FILLS the card (the masthead overlays it and never steals column height).
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                        Children = [Flow.KeepAlive(static () => Current.Value, static r => SlotKey(r), static r => PageBody(r), s_keepAlive)],
                    },
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, HitTestPassThrough = true,
                        Children = [Masthead()],
                    },
                    Flow.Show(static () => FrameRules.ShowsOfflineStrip(Auth.Value), OfflineLane()),
                ],
            };
        }
    }

    /// <summary>Enter AND Exit (the two pages overlap for the swap). Direction by PEEK: the nav verbs write it before the
    /// route, and an untracked read keeps a motion-only write from re-running the boundary. A swap touching a module page
    /// takes the translate-only pair — an opacity recipe washes out (or erases) a composited video hole.</summary>
    static LayoutTransition? PageTransition(object oldToken, object newToken)
    {
        if (newToken is not Route next) return null;
        // Home→Home, same tab (remediation §3.10/W0): SlotKey already ignores Arg, so this is a facet switch inside
        // the SAME mounted HomeScreen, never a page swap — the facet content root's own keyed Exit/Enter (F13) plays
        // the swap. A page transition here would double up on top of it (and, worse, replay a page entrance every
        // time the facet pivot is clicked).
        if (oldToken is Route prevHome && prevHome.Kind == RouteKind.Home && next.Kind == RouteKind.Home && prevHome.Tab == next.Tab)
            return null;
        var motion = Motion.Peek();
        bool videoSafe = oldToken is Route prev ? NeedsVideoSafe(prev, next) : next.Kind == RouteKind.Module;
        if (videoSafe) return RecipeForVideoSafe(motion);
        return oldToken is Route from ? RecipeFor(from, next, motion) : RecipeFor(motion);
    }

    /// <summary>The body for a route: the content region's state (<see cref="FrameRules.ContentBodyFor"/>) as the props of the
    /// structure's <c>Shell.ContentState</c> view, which draws it (T4a). The core decides which state and builds the page; the
    /// body box, the not-found page and the lane are the theme's. The scroll scope is provided around the slot, so the page below
    /// reads it as it always did.</summary>
    static Element PageBody(Route route)
    {
        var factory = PageFor(route);
        switch (FrameRules.ContentBodyFor(route, factory is not null, FrameFacts.DeveloperNow))
        {
            case ContentStateKind.Page:
                return Ctx.Provide(PageScrollScope, ScrollScopeOf(route.Tab),
                    ViewSlots.View(ViewKeys.ShellContentState,
                        ContentStateProps.ForPage(FrameRules.ContentKey(ContentStateKind.Page, NameOf(route), null), factory!(route))));
            case ContentStateKind.EmptyBody:
                return ViewSlots.View(ViewKeys.ShellContentState,
                    ContentStateProps.ForEmptyBody(FrameRules.ContentKey(ContentStateKind.EmptyBody, NameOf(route), null)));
            default:
                return NotFoundPage(route);
        }
    }

    static readonly Action s_goHome = static () => GoTo(new Route(RouteKind.Home));

    /// <summary>Nothing claims the route (a retired key, a stale restored tab, a developer route with developer mode off).
    /// NOT "coming soon": the theme says so, keeps the destination's own glyph and gives the one action that always works. The
    /// core notes the unknown route (once per key per process) and hands over the glyph and the action.</summary>
    static Element NotFoundPage(in Route route)
    {
        var (_, glyph) = Dest(route);
        string name = NameOf(route);
        string? arg = ArgOf(route);
        FrameModel.NoteUnknownRoute(name + "|" + (arg ?? ""));   // once per key per process; the host logs it
        return ViewSlots.View(ViewKeys.ShellContentState,
            ContentStateProps.ForNotFound(FrameRules.ContentKey(ContentStateKind.NotFound, name, arg), glyph, s_goHome));
    }

    /// <summary>The page-scale offline strip (ch 29 W9 C / W10) floated over the kept content while a stored credential
    /// exists but the session is down. The chrome's accent Reconnect is the loud layer; the lane the theme draws is
    /// caution-tinted. Core hands over the strip's builder (its own surface, <c>Controls.OfflineStrip</c>).</summary>
    static Element OfflineLane() => ViewSlots.View(ViewKeys.ShellContentState, s_offlineLane);

    static readonly ContentStateProps s_offlineLane = ContentStateProps.ForOfflineLane(
        static () => Controls.OfflineStrip(onRetry: static () => Spotify.Login()));

    // ══ 4. THE MERGED CHROME ROW (the core side) ═════════════════════════════════════════════════════════════════
    //
    // T4b (themes plan 6.1): the row's PICTURE is the structure's `Shell.TitleBar` (`Themes\Classic\Structure\Views\TitleBar\
    // ClassicTitleBar.cs`): ONE 48-DIP TitleBar in merged mode, the tabs island (nav cluster + text-first strip) · the
    // window-centred omnibar (`Shell.Search`, `Views\Search\ClassicSearch.cs`) · the trailing identity island. What stays here is
    // what the picture reads, and `+Shell.TitleBar.cs` builds its props from it: the strip's measured extent, the pane toggle's
    // enabled cell and tooltip, the version stamps, and the tab items with their menus, drops and pin rows. The allocation is
    // `Shell.Chrome` (`Shell.Chrome.cs`). The island builders run INSIDE the bar's render — no hooks there; hook-owned
    // behaviour lives in child components. `ContentVersion` is the bar's memo key and folds every term an island reads.

    /// <summary>The strip's natural content extent (quantised), fed back into the chrome allocator.</summary>
    static readonly RootState<Signal<float>> s_tabExtentState = new("Shell.TabExtent", static () => new Signal<float>(Layout.ChromeTabMinW));
    /// <summary>The tab strip's measured content extent (root-lifetime: the new root's strip measures itself again).</summary>
    static Signal<float> s_tabExtent => s_tabExtentState.Value;

    /// <summary>The title bar's pane toggle reads this for its enabled state (see <see cref="PaneToggleTip"/>). A plain
    /// signal mirrored by the frame component, so the bar's live binding needs no runtime at construction.</summary>
    static readonly Signal<bool> s_paneToggleEnabled = new(true);

    static string PaneToggleTip()
        => Sidebar.Editing.Value ? Loc.Get("sidebar.pane.finishEditing")
         : Sidebar.Mode.Value == SidebarPaneMode.Expanded || Sidebar.OverlayOpen.Value ? Loc.Get("sidebar.pane.collapse")
         : Loc.Get("sidebar.pane.expand");

    static int ChromeContentVersion()
    {
        var l = ChromeLayout.Value;
        int flags = (l.ShowName ? 1 : 0) | (l.ShowActions ? 2 : 0) | (l.ShowForward ? 4 : 0)
                  | (l.SearchMode == MergedSearchMode.Icon ? 8 : 0) | (l.ShowBack ? 16 : 0)
                  | (l.ShowNewTab ? 32 : 0) | (l.ShowTrailing ? 64 : 0);
        var r = Current.Value;
        // The route and the pin store's version: the trailing Pin/Unpin follows a navigation and a pin change.
        // l.Chip is the form the trailing island actually BUILDS from (the view's AuthChip); Auth.Value alone is not enough:
        // the allocator effect publishes the new Chip after Auth flips, so the bar can render once with the stale chip
        // and the version must move again when ChromeLayout lands, or TitleBar's render memo returns the stale tree.
        return HashCode.Combine(flags, (int)l.SearchWidth, (int)l.LeadClusterW, TitleBarTabsVersion(), (int)Auth.Value,
            HashCode.Combine(r.Kind, r.Subject, r.Arg), Sidebar.PinsVersion.Value, (int)l.Chip);
    }

    // Both fold Shown: the strip's labels come from the staged route, so it must rebuild when that route LANDS (the
    // exit leg after the commit), not only when the workspace or the selection changes.
    static int TitleBarTabsVersion() => HashCode.Combine(TabsVersion.Value, SelectedTab.Value, Shown.Value);

    static int TabStripItemsVersion() => HashCode.Combine(TabsVersion.Value, ChromeLayout.Value.ShowNewTab, Shown.Value);

    static IReadOnlyList<TabViewItem> BuildTabItems()
    {
        var tabs = Tabs.Tabs;
        var shown = Shown.Peek();   // the version folds it; the items source itself is untracked
        var items = new TabViewItem[tabs.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var tab = tabs[i];
            var route = tab.Route;
            // The header and icon name the page ON SCREEN (the staged route lags the commit by the exit leg); the pin
            // id, the drag payload's title and the drop target describe the tab's DESTINATION and keep its own route.
            var (destTitle, destGlyph) = Dest(route);
            var staged = TabLabelStaging.LabelRoute(route, shown);
            var (label, glyph) = staged == route ? (destTitle, destGlyph) : Dest(staged);
            int id = tab.Id;
            string? pinId = FrameRules.PinIdFor(route);
            string pinTitle = route.Kind == RouteKind.Search ? Loc.Get(Strings.Nav.Search) : destTitle;
            items[i] = new TabViewItem
            {
                Key = "tab#" + id.ToString(CultureInfo.InvariantCulture),
                Header = label,
                Icon = glyph,
                IsClosable = TabWorkspace.IsClosable(tab, tabs.Count),
                IsPinned = tab.Pinned,
                ContextMenu = () => TabMenu(id),
                // CLICK-PRIMARY: switching tabs is the constant intent, so the mouse drag box is widened.
                Drag = pinId is { } p ? Drag.Source(() => WorkspaceVerbs.Shared.TabPayload(p, pinTitle), clickPrimary: true) : null,
                DropTarget = TabDropTarget(route, id, destTitle),
            };
        }
        return items;
    }

    /// <summary>Every tab is a spring-load waypoint (a dwell activates it); a tab standing for an EDITABLE playlist is
    /// also a deposit destination (the cross-tab append). SpringLoadOnly keeps a non-destination silent rather than a
    /// flashing refusal as a drag crosses the strip. What a tab accepts and what a drop does are
    /// <see cref="IChromeVerbs"/>'s; this only shapes the engine's drop spec.</summary>
    static DropTargetSpec TabDropTarget(in Route route, int tabId, string name)
    {
        var chrome = WorkspaceVerbs.Shared;
        if (chrome.DropSiteFor(route, tabId, name) is { } site)
        {
            return Drop.Target<DragPayload>(Drag.Resource,
                accepts: p => chrome.TabAccepts(p, site),
                onDrop: (p, _) => chrome.TabDrop(p, site),
                caption: _ => Drag.AddTo(name),
                settleOnDrop: false,   // the deposited feel: the lifted visual snaps home
                springLoadMs: Drag.SpringLoadMs,
                onSpringLoad: (_, _) => chrome.ActivateTabById(tabId));
        }
        return Drop.Target<DragPayload>(Drag.Resource,
            springLoadMs: Drag.SpringLoadMs,
            onSpringLoad: (_, _) => chrome.ActivateTabById(tabId),
            springLoadOnly: true);
    }

    static ContextMenuModel? TabMenu(int tabId)
    {
        int index = Tabs.IndexOf(tabId);
        if (index < 0) return null;
        var tab = Tabs.Tabs[index];
        bool pinned = tab.Pinned;
        var chrome = WorkspaceVerbs.Shared;
        var rows = new List<MenuFlyoutItem>(9)
        {
            new MenuFlyoutItem(Loc.Get(pinned ? Strings.Shell.UnpinTab : Strings.Shell.PinTab),
                ActionIcons.Resolve(pinned ? ActionIcons.Unpin : ActionIcons.Pin), true, () => chrome.SetTabPinned(tabId, !pinned)),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseTab), Icons.Cancel, Tabs.Count > 1, () => chrome.CloseTabById(tabId)),
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseOtherTabs), default, Tabs.HasOtherUnpinned(tabId), () => chrome.CloseOtherTabs(tabId)),
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseTabsRight), default, Tabs.HasUnpinnedToRight(index), () => chrome.CloseTabsToRight(tabId)),
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseAllUnpinned), default, Tabs.HasAnyUnpinned(), chrome.CloseAllUnpinnedTabs),
        };
        if (PinRow(tab.Route) is { } pinRow)
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(pinRow);
        }
        return new ContextMenuModel(rows, new ContextMenuHeader(null, Dest(tab.Route).Title));
    }

    /// <summary>The absolute Pin/Unpin row for a destination, or null when it is not pinnable (<see cref="PinVerbs.TargetFor"/>).</summary>
    static MenuFlyoutItem? PinRow(in Route route)
    {
        if (PinVerbs.TargetFor(route) is not { } target) return null;
        return Actions.Menu.Pin(target.Kind == PinRowKind.Unpin, () => PinVerbs.Pin(target.PinId, target.Title), () => PinVerbs.Unpin(target.PinId));
    }

    // ══ 5. THE OVERLAY PANE ════════════════════════════════════════════════════════════════════════════════════════════
    //
    // In the Narrow and Tiny bands the sidebar is an OVERLAY over the page: `Sidebar.Band` decides whether there is one and
    // `Sidebar.OverlayOpen` is the session's open flag. The band's forced mode and the user's collapse never reach it, so
    // the saved desktop preference is never overwritten. Always mounted while the window is narrow; open/close is
    // compositor-only.

    /// <summary>The drawer's core host (T4c): the band's overlay and pinned answers, the single <c>KeyPreview</c> slot it chains while
    /// open (bare Escape closes the overlay; while editing, Escape belongs to the pane first), the leaf-navigation close and the mount
    /// decision. It builds no element: the scrim, the pane and their open/close motion are the structure's <c>Shell.NarrowDrawer</c>
    /// (<c>ClassicDrawer.cs</c>).</summary>
    [CoreHost]
    sealed class NarrowDrawer : Component
    {
        /// <summary>The live reads and verbs the scrim and the pane bind to: cached, equal across renders.</summary>
        static readonly NarrowDrawerParts s_drawerParts = new()
        {
            IsOpen = static () => Sidebar.OverlayOpen.Value || SidebarPaneModeRules.OverlayPinned(Sidebar.Band.Value, Sidebar.Editing.Value),
            IsPinned = static () => SidebarPaneModeRules.OverlayPinned(Sidebar.Band.Value, Sidebar.Editing.Value),
            Width = static viewportW => SidebarPaneModeRules.OverlayWidth(Sidebar.Width.Value, viewportW),
            WidthNow = static viewportW => SidebarPaneModeRules.OverlayWidth(Sidebar.Width.Peek(), viewportW),
            RestingOpacity = static open => Layout.DrawerRestingOpacity(open),
            RestingTranslateX = static (open, width) => Layout.DrawerRestingTranslateX(open, width),
            Close = static () => Sidebar.OverlayOpen.Value = false,
            PaneBody = static () => Sidebar.DrawerPane(),
        };

        Func<int, bool>? _savedPreview;
        Func<int, bool>? _escapePreview;
        bool _installed;

        public override Element Render()
        {
            var band = Sidebar.Band.Value;
            bool overlay = SidebarPaneModeRules.HasOverlay(band);
            bool pinned = SidebarPaneModeRules.OverlayPinned(band, Sidebar.Editing.Value);
            bool open = overlay && (Sidebar.OverlayOpen.Value || pinned);
            var hooks = UseContext(InputHooks.Current);
            _escapePreview ??= key =>
            {
                // While editing, Esc belongs to the pane (it exits Edit first); a second Esc then closes the overlay.
                if ((key == Keys.Escape || key == Keys.GamepadB) && Sidebar.OverlayOpen.Peek() && !Sidebar.Editing.Peek())
                {
                    Sidebar.OverlayOpen.Value = false;
                    return true;
                }
                return _savedPreview?.Invoke(key) ?? false;
            };
            // Chain the single KeyPreview slot while open; restore it only if it is still ours.
            UseEffect(() =>
            {
                if (open && !_installed)
                {
                    _installed = true;
                    _savedPreview = hooks.KeyPreview;
                    hooks.KeyPreview = _escapePreview;
                }
                else if (!open && _installed)
                {
                    _installed = false;
                    if (ReferenceEquals(hooks.KeyPreview, _escapePreview)) hooks.KeyPreview = _savedPreview;
                    _savedPreview = null;
                }
            }, DepKey.From(open));
            // A leaf navigation closes the overlay (NavigationView.cpp:4840-4846) — never while editing.
            UseSignalEffect(static () =>
            {
                _ = Current.Value;
                if (SidebarPaneModeRules.LeafInvokeClosesOverlay(Sidebar.Editing.Peek())) Sidebar.OverlayOpen.SetIfChanged(false);
            });

            // BUG E1 (perf): the mount decision stays here (`NarrowDrawerMount.ShouldMount`, the band ALONE, never `Sidebar.OverlayOpen`); the theme
            // mounts nothing but a zero-size placeholder when it is false (no scrim, no second `Sidebar.DrawerPane()`, no overlay PaneHost) and
            // keeps the pane mounted, closed included, for the ENTIRE time the shell is narrow, so the first open has something to slide from.
            // `PaneHost`'s `UseSignalEffect(PumpBinder)` is a `SignalEffectCell` (`IDisposableCell`), so mounting/unmounting across the breakpoint
            // cleanly registers/unregisters the binder pump (verified in the engine's `RenderContext.RunAllCleanups`).
            // Peek, not Value: ShouldMount ignores the open flag entirely (see its own doc).
            return ViewSlots.View(ViewKeys.ShellNarrowDrawer, new NarrowDrawerProps
            {
                Mounted = NarrowDrawerMount.ShouldMount(overlay, Sidebar.OverlayOpen.Peek()),
                Open = open,
                Parts = s_drawerParts,
            });
        }
    }

    /// <summary>BUG E1's pure mount decision (`docs/plans/wavee/wavee-0.3-bug-handoff-2026-09-15.md` §7,
    /// `NarrowDrawer.Render`): whether the overlay's heavy subtree (the scrim plus a second full `Sidebar.DrawerPane()`)
    /// should be mounted at all. Deliberately takes <paramref name="overlayOpen"/> and ignores it — the decision is the
    /// band alone. A version of this that also required <paramref name="overlayOpen"/> would unmount the pane the instant
    /// it closes on a narrow window, which would then pop in with no animation the next time it opens (the reveal/close
    /// slide needs the pane already mounted). Engine-free and pure so it is unit-testable without mounting a component
    /// (<c>ShellNarrowDrawerTests</c>).</summary>
    public static class NarrowDrawerMount
    {
        public static bool ShouldMount(bool hasOverlay, bool overlayOpen) => hasOverlay;
    }

    // ══ 6. THE NOTIFICATION PANEL (ch 19 W14-W17, A9) ══════════════════════════════════════════════════════════════

    static INotificationVerbs PanelVerbs => NotificationVerbs.Shared;

    /// <summary>THE one open path for the panel, shared by the bell and the profile menu's row (installed into
    /// <see cref="NotificationsLauncher"/>): same placement and chrome, and the open is the panel's DEMAND — it refetches
    /// the stale feeds and advances the watermarks every time. A second press closes it.</summary>
    public static void OpenNotificationPanel(IOverlayService overlay, Func<NodeHandle> anchor, Ref<OverlayHandle?> handle)
    {
        if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
        var h = overlay.Open(anchor, static () => NotificationPanel(), FlyoutPlacement.BottomEdgeAlignedRight,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            {
                ConstrainToRootBounds = false,
            });
        handle.Value = h;
        OverlayService.Track(h);
        Action close = h.Close;
        NotificationVerbs.PanelCloser = close;
        h.ClosedAction = () =>
        {
            handle.Value = null;
            if (ReferenceEquals(NotificationVerbs.PanelCloser, close)) NotificationVerbs.PanelCloser = null;
        };
        Notify.PanelOpened(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    sealed class NotificationPanelView : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);   // the release rows' album menu host
            var expanded = UseSignal("");
            var tick = UseSignal(0);
            // Relative times advance while open; a frame-clock interval that auto-pauses when parked.
            UseInterval(() => tick.Value = tick.Peek() + 1, Notify.RelativeTickMs);
            UseSignalEffect(static () =>
                Notify.UpdateProgress.Value = Math.Clamp(Notify.Update.Value.ProgressPercent / 100f, 0f, 1f));

            var feed = Notify.Items.Value;
            var filter = Notify.Filter.Value;
            var social = Notify.SocialState.Value;
            var releases = Notify.ReleasesState.Value;
            _ = tick.Value;
            string expandedId = expanded.Value;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var rows = new List<Element>(feed.Items.Count);
            for (int i = 0; i < feed.Items.Count; i++)
            {
                var n = feed.Items[i];
                if (Notify.PassesFilter(n, filter)) rows.Add(NotificationRow(n, now, expanded, expandedId, overlay));
            }

            Element body = rows.Count > 0
                ? new ScrollEl
                {
                    MaxHeight = Notify.FeedMaxHeight, ContentSized = true, AutoEdgeFade = true, ScrollKey = "notifications",
                    Content = new BoxEl
                    {
                        Direction = 1, Width = Notify.PanelWidth, Padding = new Edges4(6f, 4f, 6f, 8f), Gap = 2f,
                        Children = rows.ToArray(),
                    },
                }
                : PanelEmpty(Loc.Get(Notify.EmptyMessageKey(filter, social, releases)));

            return new BoxEl
            {
                Direction = 1, Width = Notify.PanelWidth, MinWidth = Notify.PanelWidth, MaxHeight = Notify.PanelMaxHeight,
                Children =
                [
                    PanelHeader(filter),
                    FilterPills(filter),
                    Embed.Comp(static () => new PendingSyncRow()) with { Key = "nc-pending-sync" },
                    body,
                ],
            };
        }
    }

    /// <summary>"n playlist changes still syncing" — the app-wide answer to "did that actually happen?". Its own component
    /// so the outbox draining never re-renders the feed; nothing at 0, which is nearly always.</summary>
    sealed class PendingSyncRow : Component
    {
        public override Element Render()
        {
            int pending = Notify.PendingEdits.Value;
            if (pending <= 0) return new BoxEl { Height = 0f, HitTestVisible = false };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Padding = new Edges4(14f, 4f, 14f, 8f),
                Children =
                [
                    Icon(Icons.Refresh, 12f, Tok.TextTertiary),
                    new TextEl(Strings.Notifications.PendingSync(pending)) { Size = 12f, Color = Tok.TextSecondary, Grow = 1f },
                ],
            };
        }
    }

    static Element PanelHeader(NotifyFilter filter)
    {
        var kids = new List<Element>(3)
        {
            new TextEl(Loc.Get(Strings.Notifications.Title)) { Size = 15f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f },
            // G-090: Mark all read must also clear the OS banners it acknowledges — otherwise every live toast this
            // session raised keeps sitting in the Action Center after the in-app centre says everything is read.
            PanelLink(Loc.Get(Strings.Notifications.MarkAllRead), static () => NotificationVerbs.Shared.MarkAllRead()),
        };
        if (Notify.ShowsClear(filter) && Notify.ClearActivity is { } clear)
            kids.Add(PanelLink(Loc.Get(Strings.Notifications.Clear), clear));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Padding = new Edges4(14f, 12f, 8f, 8f),
            Children = kids.ToArray(),
        };
    }

    /// <summary>The filter chips on the shared one-line rail (it scrolls with an edge fade if the flyout is too narrow).</summary>
    static Element FilterPills(NotifyFilter current) => new BoxEl
    {
        Padding = new Edges4(12f, 2f, 12f, 0f),
        Children =
        [
            Controls.ChipRail(
            [
                FilterPill(Strings.Notifications.Filter.All, NotifyFilter.All, current),
                FilterPill(Strings.Notifications.Filter.Updates, NotifyFilter.Updates, current),
                FilterPill(Strings.Notifications.Filter.Spotify, NotifyFilter.Spotify, current),
                FilterPill(Strings.Notifications.Filter.New, NotifyFilter.New, current),
                FilterPill(Strings.Notifications.Filter.Activity, NotifyFilter.Activity, current),
            ], "notify.filters"),
        ],
    };

    /// <summary>The app's one filter chip (<see cref="Controls.Chip"/>); exclusive, so a click on the active one is a no-op.</summary>
    static Element FilterPill(string labelKey, NotifyFilter pill, NotifyFilter current)
        => Controls.Chip(Loc.Get(labelKey), pill == current, true, () => Notify.Filter.Value = pill) with { Key = "notify-filter:" + (int)pill };

    static Element NotificationRow(Notification n, long now, Signal<string> expanded, string expandedId, IOverlayService? overlay) => n.Category switch
    {
        NotifyCategory.AppUpdate => NotificationCard("ntf:" + n.Id, UpdateRow(n.Update ?? AppUpdateSnapshot.Idle), n.IsUnread, null),
        NotifyCategory.Social => NotificationCard("ntf:" + n.Id, SocialRow(n, now), n.IsUnread, () => PanelVerbs.OpenSocial(n)),
        NotifyCategory.NewRelease => ReleaseCard(n, overlay),
        _ => ActivityCard(n, now, expanded, expandedId),
    };

    /// <summary>A new-release row: click opens it; an ALBUM release also answers a right-click with the one album menu
    /// (the cards' own — Play next, Add to queue, Save, Go to artist…). An episode release has no container menu.</summary>
    static Element ReleaseCard(Notification n, IOverlayService? overlay)
    {
        var card = NotificationCard("ntf:" + n.Id, ReleaseRow(n), n.IsUnread, () => PanelVerbs.OpenRelease(n));
        if (n.ReleaseKind != NewReleaseKind.Album || !n.Subject.IsValid || overlay is null || Controls.IsNullOverlay(overlay)) return card;
        var target = ActionTarget.ForAlbum(n.Subject, n.Title);
        string? art = n.ImageUrl, creator = n.Creator;
        return card.WithContextMenu(overlay, () => Menus.Container(in target, art, creator));
    }

    /// <summary>The generic row frame: 56 min, r8, the hover rung, the unread dot, and the enter/exit/slide choreography.</summary>
    static BoxEl NotificationCard(string key, Element content, bool unread, Action? onClick) => new BoxEl
    {
        Key = key,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, MinHeight = 56f,
        Padding = new Edges4(10f, 8f, 10f, 8f), Corners = CornerRadius4.All(8f),
        HoverFill = onClick is not null ? R.Layer.RowHover : ColorF.Transparent,
        PressedFill = onClick is not null ? R.Layer.RowPressed : ColorF.Transparent,
        Role = onClick is not null ? AutomationRole.Button : AutomationRole.None,
        Cursor = onClick is not null ? CursorId.Hand : CursorId.Arrow,
        Focusable = onClick is not null,
        OnClick = onClick,
        Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
        Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
        Layout = LayoutTransition.Slide,
        Children = [content, UnreadDot(unread)],
    };

    static Element UnreadDot(bool unread) => unread
        ? new BoxEl { Width = 8f, Height = 8f, Shrink = 0f, Corners = CornerRadius4.All(4f), Fill = Tok.AccentDefault }
        : new BoxEl { Width = 8f, Shrink = 0f };

    /// <summary>ONE row per update state, from the WHOLE published snapshot; the sentence under the title is the one
    /// Settings ▸ About prints. While a download runs the row shows progress instead of buttons.</summary>
    static Element UpdateRow(AppUpdateSnapshot s)
    {
        var arm = Notify.UpdateRow(s.State);
        var (glyph, tint) = arm.Glyph switch
        {
            UpdateRowGlyph.Download => (Icons.Download, Tok.AccentDefault),
            UpdateRowGlyph.Refresh => (Icons.Refresh, Tok.AccentDefault),
            UpdateRowGlyph.Success => (Icons.StatusSuccess, Tok.SystemFillSuccess),
            UpdateRowGlyph.Error => (Icons.StatusError, Tok.SystemFillCritical),
            _ => (Icons.StatusInfo, Tok.TextSecondary),
        };
        Element trailing;
        if (arm.ShowsProgress)
        {
            trailing = new BoxEl
            {
                Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Margin = new Edges4(0f, 6f, 0f, 0f),
                Children =
                [
                    ProgressBar.Create(Notify.UpdateProgress, 200f),
                    R.Type.MicroMeta(s.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "%") with
                        { Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
                ],
            };
        }
        else if (arm.Actions.Length > 0)
        {
            var buttons = new Element[arm.Actions.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                var verb = arm.Actions[i];
                buttons[i] = PanelPill(UpdateVerbLabel(verb), () => PanelVerbs.RunUpdateRow(verb, s), accent: i == 0);
            }
            trailing = new BoxEl { Direction = 0, Gap = 6f, Wrap = true, Margin = new Edges4(0f, 4f, 0f, 0f), Children = buttons };
        }
        else trailing = new BoxEl();

        string title = arm.TitleLocKey.Length > 0 ? Loc.Get(arm.TitleLocKey) : "";
        string body = Notify.AppUpdateToasts.StateSentence(s);
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = 10f, AlignItems = FlexAlign.Start,
            Children =
            [
                GlyphChip(glyph, tint),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 3f, MinWidth = 0f,
                    Children =
                    [
                        R.Type.DenseTitle(title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        body.Length > 0 ? new TextEl(body) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3 } : new BoxEl(),
                        trailing,
                    ],
                },
            ],
        };
    }

    static string UpdateVerbLabel(UpdateRowAction verb) => Loc.Get(verb switch
    {
        UpdateRowAction.UpdateNow => Strings.Update.Action.UpdateNow,
        UpdateRowAction.WhatsNew => Strings.Update.Action.WhatsNew,
        UpdateRowAction.Later => Strings.Update.Action.Later,
        UpdateRowAction.Retry => Strings.Update.Action.Retry,
        UpdateRowAction.OpenReleasePage => Strings.Update.Action.OpenReleasePage,
        _ => Strings.Update.Action.Dismiss,
    });

    static Element SocialRow(in Notification n, long now)
    {
        var text = new List<Element>(2)
        {
            R.Type.DenseMeta(SpotifyUpdates.CleanTitle(n.Title)) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2 },
        };
        if (n.TimestampMs > 0 && n.TimestampMs != NotifyRows.UpdatePin)
            text.Add(R.Type.MicroMeta(RelativeTime(now - n.TimestampMs)) with { Weight = 600, Color = Tok.TextTertiary });
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, Gap = 10f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), ClipToBounds = true,
                    Children = [Controls.Artwork(n.ImageUrl, 40f, 40f, 20f)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f, Children = text.ToArray() },
            ],
        };
    }

    static Element ReleaseRow(in Notification n) => new BoxEl
    {
        Direction = 0, Grow = 1f, Basis = 0f, Gap = 10f, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Width = 44f, Height = 44f, Shrink = 0f, Corners = CornerRadius4.All(5f), ClipToBounds = true,
                Children = [Controls.Artwork(n.ImageUrl, 44f, 44f, 5f)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f, MinWidth = 0f,
                Children =
                [
                    R.Type.DenseTitle(n.Title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(n.Creator ?? "") { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
            PanelTypePill(Loc.Get(Notify.ReleaseTypeKey(n.ReleaseKind, n.AlbumType))),
        ],
    };

    /// <summary>A local library mutation: summary + age + Undo (when the journal offers it); a click expands the detail
    /// (swapping the card's KEY, so the expansion is a keyed remount the slide choreography carries).</summary>
    static Element ActivityCard(Notification n, long now, Signal<string> expanded, string expandedId)
    {
        bool isExpanded = expandedId == n.Id;
        var right = new List<Element>(2)
        {
            R.Type.MicroMeta(RelativeTime(now - n.TimestampMs)) with { Weight = 600, Color = Tok.TextTertiary, Shrink = 0f },
        };
        if (Notify.UndoActivity is { } undo)
            right.Add(PanelPill(Loc.Get(Strings.Notifications.Undo), () => _ = PanelVerbs.UndoActivityAsync(undo, n), accent: false));

        var card = new BoxEl
        {
            Key = "ntf:act:" + n.Id,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, MinHeight = 56f,
            Padding = new Edges4(10f, 8f, 10f, 8f), Corners = CornerRadius4.All(8f),
            HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => { Notify.MarkRead(n.Id); expanded.Value = isExpanded ? "" : n.Id; },
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Basis = 0f, Gap = 10f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        GlyphChip(Icons.StatusInfo, Tok.TextSecondary),
                        R.Type.DenseMeta(n.Title) with { Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Color = Tok.TextPrimary },
                        new BoxEl { Direction = 1, Shrink = 0f, AlignItems = FlexAlign.End, Gap = 4f, Children = right.ToArray() },
                    ],
                },
                UnreadDot(n.IsUnread),
            ],
        };
        if (!isExpanded) return card;
        return new BoxEl
        {
            Key = "ntf:actwrap:" + n.Id, Direction = 1, Gap = 2f,
            Children = [card, ActivityDetail(n)],
        };
    }

    /// <summary>The target line (always present, so expanding is never a visual no-op) with an Open jump when the
    /// subject routes, then the journal's detail sentence.</summary>
    static Element ActivityDetail(in Notification n)
    {
        var subject = n.Subject;
        var route = subject.IsValid ? For(subject) : Route.None;
        string target = subject.IsValid ? (route.IsNone ? subject.Text : Dest(route).Title) : "";
        var kids = new List<Element>(2);
        if (target.Length > 0)
            kids.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                Children =
                [
                    new TextEl(target) { Size = 12f, Weight = 600, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap },
                    route.IsNone
                        ? new BoxEl()
                        : PanelPill(Loc.Get(Strings.Notifications.Activity.Detail.Open), () => PanelVerbs.OpenRoute(route), accent: false),
                ],
            });
        if (n.Body.Length > 0)
            kids.Add(new TextEl(n.Body) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
        return new BoxEl { Direction = 1, Gap = 3f, Margin = new Edges4(46f, 0f, 12f, 6f), Children = kids.ToArray() };
    }

    static string RelativeTime(long ageMs)
    {
        var (unit, count) = Notify.RelativeAge(ageMs);
        return unit switch
        {
            RelativeUnit.Now => Loc.Get(Strings.Friends.Now),
            RelativeUnit.Minutes => Strings.Friends.MinAgo(count),
            RelativeUnit.Hours => Strings.Friends.HrAgo(count),
            _ => Strings.Friends.DAgo(count),
        };
    }

    static Element PanelEmpty(string message) => new BoxEl
    {
        Direction = 1, Width = Notify.PanelWidth, MinHeight = 120f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = Edges4.All(24f),
        Children = [R.Type.DenseTitle(message) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 300f }],
    };

    static Element GlyphChip(string glyph, ColorF tint) => new BoxEl
    {
        Width = 36f, Height = 36f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(18f), Fill = Tok.FillSubtleSecondary,
        Children = [Icon(glyph, 16f, tint)],
    };

    static Element PanelTypePill(string label) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(9f, 2f, 9f, 2f), Corners = CornerRadius4.All(10f), Fill = Tok.FillSubtleSecondary,
        Children = [R.Type.Eyebrow(label) with { Color = Tok.TextTertiary }],
    };

    static Element PanelPill(string label, Action onClick, bool accent) => new BoxEl
    {
        Shrink = 0f, MinHeight = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(12f, 4f, 12f, 4f), Corners = CornerRadius4.All(14f),
        Fill = accent ? Tok.AccentDefault : Tok.FillControlDefault,
        HoverFill = accent ? Tok.AccentSecondary : Tok.FillControlSecondary,
        PressedFill = accent ? Tok.AccentTertiary : Tok.FillControlTertiary,
        BorderWidth = accent ? 0f : 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
        OnClick = onClick,
        Children = [new TextEl(label) { Size = 12f, Weight = 600, Color = accent ? Tok.TextOnAccentPrimary : Tok.TextPrimary }],
    };

    static Element PanelLink(string label, Action onClick) => new BoxEl
    {
        Shrink = 0f, MinHeight = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(8f, 3f, 8f, 3f), Corners = CornerRadius4.All(6f),
        HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = onClick,
        Children = [new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary }],
    };
}
