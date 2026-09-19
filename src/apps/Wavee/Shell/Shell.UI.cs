// ── Shell/Shell.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// window frame, merged chrome row, tab strip, drill trail, narrow drawer, back/forward flyout, not-found page,
// network chrome, file-drop target + cue (LocalFileActions), the notification panel and its rows (A9)
//
// Role: UI
// Owner: I
// Wave: 4
// Budget: 2600 lines
// Spec: ch 18 §9 (frame UI 3,250, less +Shell.Masthead.UI.cs's 950) + ch 19 §9.4 (300 of its Shell.UI.cs +2,000 share — the panel and its rows; the other 1,700 is +Shell.Overlays.UI.cs)
//
// THE FRAME, BOTTOM-UP (ch 18 §1-§6). Everything under `OverlayHost` is one ZStack:
//
//   tinted[ MaterialLayer · column ]  ·  Stage.View  ·  Overlays  ·  file-drop cue  ·  CommandPalette
//         ·  Video.PipLayer  ·  Video.FullscreenLayer  ·  drag preview
//
//   column = 14 zero-size chord boxes · [chrome row] · content region · [player dock]
//            └ both bracketed bands UNMOUNT under full-screen video (FrameRules.ChromeMounted)
//   content region = ZStack[ row(sidebar column · content card · rail gap · rail reservation) · sidebar seam ·
//                            rail seam · rail overlay(Rail.Frame) · narrow drawer ]
//
// THE RULE THIS FILE KEEPS: it lays out and binds, it never decides. Every geometry/precedence/gating answer below is a
// call into `Shell.FrameRules`, `Shell.Chrome`, `TabWorkspace`, `Notify` or an owner's rule table — each pinned by a
// test. The frame root renders ONCE for the process: every live value reaches it through a bound prop, a `Flow.Show`
// predicate or an effect, so a resize, a navigation or a theme flip never re-runs this render body.

using System.Globalization;
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
using static FluentGpu.Dsl.Ui;
using FgMotion = FluentGpu.Dsl.Motion;

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
        SetPage(RouteKind.SidebarCustomize, static (in Route _) => Sidebar.CustomizerPage());
        if (NotificationsLauncher is null) NotificationsLauncher = OpenNotificationPanel;

        Design.Install();   // G-196: Tok.Use(Tok.NeutralPalette, _) rather than the engine's own default ramp
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

    /// <summary>The frame's context captures, read by the static key handlers and verbs (never a render input).</summary>
    static InputHooks? s_hooks;
    static SceneStore? s_scene;
    static Action<float>? s_requestTheme;
    static NodeHandle s_contentRegion;

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
        Ui.RailWidth.Value = ClampRailWidth(Platform.Settings.Get(Platform.Keys.ShellRailWidth));
        Ui.DockedVideoHeight.Value = ClampDockedVideoHeight(Platform.Settings.Get(Platform.Keys.ShellDockedVideoHeight), Ui.RailWidth.Peek());
        if (Session.ShellSection is { } shell && shell.RailMode >= (int)RailMode.Lyrics && shell.RailMode <= (int)RailMode.Video)
        {
            Ui.RailOpen.Value = shell.RailOpen;
            Ui.Mode.Value = (RailMode)shell.RailMode;
        }
    }

    // ══ 2. THE FRAME ROOT ═════════════════════════════════════════════════════════════════════════════════════════

    // The sidebar collapse AND the content card's FLIP share ONE transition, so the pane's animating edge and the card's
    // left edge ease on identical dynamics. Reveal lays out at the FINAL size and eases a clip + translate only; a grip
    // drag snaps both 1:1 through the suppression arbiter. WinUI SplitView's pane spline at 300 ms (200 ms read as a snap
    // behind the heavier media surface).
    static readonly EasingSpec PaneEase = EasingSpec.CubicBezier(0f, 0.35f, 0.15f, 1f);
    const float PaneMs = 300f;
    const string ContentRowMorphId = "shell.content-row";

    static readonly LayoutTransition SidebarPaneAnim = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(PaneMs, PaneEase), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(PaneMs, PaneEase), SuppressDescendantTransitions: true);

    static readonly LayoutTransition ContentCardAnim = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(PaneMs, PaneEase), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(PaneMs, PaneEase), SuppressDescendantTransitions: true);

    /// <summary>A LEFT+TOP-only stroke: the engine's border is one uniform SDF ring, so the stroked box is one DIP larger on
    /// its right and bottom and parked in a clip of the real geometry — the right/bottom strokes land in the clipped DIP
    /// and the rounded top-left arc survives whole.</summary>
    static readonly Edges4 StrokeOverhang = new(0f, 0f, -1f, -1f);
    static readonly CornerRadius4 RailBandCorners = new(Radii.Card, 0f, 0f, 0f);

    static readonly Splitter.SplitterOptions SidebarSeamOptions = new()
    {
        Min = Layout.NavPaneMinW, Max = Layout.NavPaneMaxW, ShowIndicator = false, CompactWidth = Layout.CompactRailW,
        // Issue #84: the resist band starts at the OLD 240 floor so the feel through 240 → 180 is unchanged; collapse at
        // 240 − 64 = 176 (just under the 180 floor), re-expand at 190.
        FadeStart = 240f, FadeDistance = 64f, ForcePush = 64f, ReExpand = 190f,
    };

    static readonly Splitter.SplitterOptions RailSeamOptions = new()
    {
        Min = RailMinW, Max = RailMaxW, Polarity = SplitterPolarity.Leading, ShowIndicator = false, InvertCollapsed = true,
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
        /// <summary>A module watch page's stage would host what is playing — split from the resize-rate effect so the
        /// playable-uri reads run at navigation/track rate, not per resize pixel.</summary>
        readonly Signal<bool> _pageStageHosts = new(false);

        bool _narrowSeeded, _tierSeeded;
        SidebarDesign _tierDesign = Sidebar.Design.Peek();
        float _lastAutoZoom;
        bool _autoZoomSeeded;
        Video.PlacementState _videoWas = Video.State.Surface.Peek();
        bool _railWas = Ui.RailOpen.Peek();
        Video.DockedHost _hostWas = Video.DockedHost.Rail;

        public override Element Render()
        {
            s_hooks = UseContext(InputHooks.Current);
            s_scene = Context.Scene;
            s_requestTheme = UseContext(ThemeControl.Request);
            var post = UsePost();
            // Float the engine's toast lane above the docked player bar (the idempotent registration idiom).
            Toast.EdgeInset = Design.Size.PlayerBarH;
            var vp = UseContextSignal(Viewport.Size);
            var zoom = UseContextSignal(Viewport.Zoom);

            // The narrow band's FIRST value is known only here; seed it before the column mounts so a narrow launch
            // lays out narrow on frame one (no subscriber reads these yet).
            if (!_narrowSeeded)
            {
                _narrowSeeded = true;
                bool narrow = Layout.NarrowFor(vp.Peek().Width, current: false, initialized: false);
                Ui.NarrowShell.SetIfChanged(narrow);
                Ui.SidebarPresentedCompact.SetIfChanged(FrameRules.PresentedCompact(narrow, Sidebar.Collapsed.Peek()));
            }

            // ── the responsive bands (hot width, band-gated writes; never debounced — a deferred structural width is
            //    a frame rendered at the wrong tier) ──
            UseSignalEffect(() =>
            {
                bool current = Ui.NarrowShell.Peek();
                bool next = Layout.NarrowFor(vp.Value.Width, current, initialized: true);
                if (next == current) return;
                Ui.NarrowShell.Value = next;
                if (!next) Ui.DrawerOpen.SetIfChanged(false);
            });
            UseSignalEffect(static () =>
                Ui.SidebarPresentedCompact.SetIfChanged(FrameRules.PresentedCompact(Ui.NarrowShell.Value, Sidebar.Collapsed.Value)));
            // The nav pane's responsive DEFAULT width. Viewport AND design are read first and unconditionally (an early
            // return before either drops the subscription). Yields to a pinned width (the service no-ops) and to a live
            // drag (peek-gated — the grip owns the width 1:1 while the pointer is down).
            UseSignalEffect(() =>
            {
                float w = vp.Value.Width;
                var design = Sidebar.Design.Value;
                if (design != _tierDesign) { _tierDesign = design; _tierSeeded = false; }
                Sidebar.SetViewportWidth(w);
                if (Sidebar.WidthUserSet || _sidebarDragging.Peek()) return;
                float next = Layout.NavPaneDefaultFor(w, Sidebar.Width.Peek(), _tierSeeded, Sidebar.Tiers);
                if (w > 0f) _tierSeeded = true;
                Sidebar.SetResponsiveWidth(next);
            });
            // The grip's detent writes Sidebar.Collapsed directly; persist it (posted: the service re-writes the cell).
            UseSignalEffect(() =>
            {
                bool collapsed = Sidebar.Collapsed.Value;
                post(collapsed ? s_persistCollapsed : s_persistExpanded);
            });
            UseSignalEffect(() => FgMotion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, _sidebarDragging.Value));
            // Session chrome, not a preference: reopening restores the rail exactly as it was.
            UseSignalEffect(static () => Session.CaptureShell(Ui.RailOpen.Value, (int)Ui.Mode.Value));

            // ── large-display auto-zoom (ch 18 W24): resize-SETTLED base extent → the pure control loop ──
            var baseDip = UseDebouncedValue(() =>
            {
                var size = vp.Value;
                float z = zoom.Value;
                return new Size2(size.Width * z, size.Height * z);
            }, FrameRules.ZoomAutoDebounceMs);
            UseSignalEffect(() =>
            {
                var b = baseDip.Value;
                float live = zoom.Value;
                var mode = (ZoomAutoMode)Math.Clamp(Platform.Settings.Get(Platform.Keys.ZoomMode), 0, 2);
                var d = FrameRules.AutoZoom(mode, b.Width, b.Height, live, _lastAutoZoom, _autoZoomSeeded);
                _lastAutoZoom = d.LastAuto;
                _autoZoomSeeded = d.Seeded;
                if (d.Kind == FrameRules.ZoomStepKind.Apply) post(ZoomApplier(d.Zoom));
                else if (d.Kind == FrameRules.ZoomStepKind.PinManual) Platform.Settings.Set(Platform.Keys.ZoomMode, (int)ZoomAutoMode.Manual);
            });
            // Zoom persistence: the settled value only, so a held chord or a wheel spin writes once.
            var zoomSettled = UseDebouncedValue(() => zoom.Value, FrameRules.ZoomAutoDebounceMs);
            UseSignalEffect(() =>
            {
                float z = zoomSettled.Value;
                if (MathF.Abs(Platform.Settings.Get(Platform.Keys.ZoomLevel) - z) > FrameRules.ZoomEpsilon)
                    Platform.Settings.Set(Platform.Keys.ZoomLevel, z);
            });

            // ── the process-lifetime subscriptions (stage-2 wiring, owner-I report) ──
            UseEffect(() =>
            {
                var hooks = s_hooks;
                Action<int> onNav = static which => { if (which == 0) GoBack(); else GoForward(); };
                Func<float, bool> onWheel = static notch => { ZoomStep(notch > 0f ? +1 : -1); return true; };
                Action<string> onRedirect = _ => post(s_drainActivation);
                Action onColors = OnSystemColorsChanged;
                FluentApp.AppNavigationCommand += onNav;
                FluentApp.ActivationRedirected += onRedirect;
                FluentApp.SystemColorsChanged += onColors;
                if (hooks is not null) hooks.ZoomWheel = onWheel;
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
                };
            }, DepKey.Empty);

            // ── the published derived facts ──
            UseSignalEffect(static () =>
                Auth.SetIfChanged(FoldAuth(Spotify.Status.Value, Spotify.Fault.Value, Platform.Args.Fake || Platform.HasStoredCredential())));   // --fake presents its seeded account as signed in (G-065)
            UseSignalEffect(() => _pageStageHosts.SetIfChanged(
                Video.DockedHosting.PageStageHosts(Ui.ActiveStagePlayable.Value, Playback.CurrentId.Value.Text)));
            UseSignalEffect(() =>
            {
                float sidebarW = Ui.SidebarPresentedCompact.Value ? Layout.CompactRailW : Sidebar.Width.Value;
                Ui.RailFits.SetIfChanged(Ui.CanFitRail(vp.Value.Width, sidebarW, Ui.RailWidth.Value));
            });
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
                    ? Rail.VideoCoupling.ModeOnDock(railOpen, Ui.Mode.Peek(), host) : null;
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
                float seeded = Chrome.SeedTabExtent(s_tabExtent.Peek(), Tabs.Count, Tabs.PinnedCount);
                s_tabExtent.SetIfChanged(seeded);
            });
            UseSignalEffect(() =>
            {
                float w = vp.Value.Width;
                _ = TabsVersion.Value;
                float extent = s_tabExtent.Value;
                var old = ChromeLayout.Peek();
                var next = Chrome.Resolve(w, extent, old);
                if (FrameRules.ReissueSearchFocus(old.SearchMode, next.SearchMode, s_searchFocused.Peek(), s_searchFlyoutOpen.Peek()))
                    SearchFocusRequest.Value = SearchFocusRequest.Peek() + 1;
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

            var column = new BoxEl
            {
                // Window-tall, bound: the content region yields instead of shoving the docked bar off the bottom.
                Direction = 1, Grow = 1f, Height = Prop.Of(() => vp.Value.Height),
                OnKeyDown = OnShellKey,
                Children =
                [
                    Chord(NewTabChord, static () => OpenTab(new Route(RouteKind.Home))),
                    Chord(PaletteChord, static () => PaletteOpen.Value = !PaletteOpen.Peek()),
                    Chord(FindChord, static () => SearchFocusRequest.Value = SearchFocusRequest.Peek() + 1),
                    Chord(BackChord, GoBack),
                    Chord(ForwardChord, GoForward),
                    Chord(FullscreenChord, ToggleVideoFullscreen),
                    Chord(ZoomInChord, static () => ZoomStep(+1)),
                    Chord(ZoomInShiftChord, static () => ZoomStep(+1)),
                    Chord(ZoomInPadChord, static () => ZoomStep(+1)),
                    Chord(ZoomOutChord, static () => ZoomStep(-1)),
                    Chord(ZoomOutShiftChord, static () => ZoomStep(-1)),
                    Chord(ZoomOutPadChord, static () => ZoomStep(-1)),
                    Chord(ZoomResetChord, static () => ZoomStep(0)),
                    Chord(ZoomResetPadChord, static () => ZoomStep(0)),
                    // FULL-SCREEN VIDEO UNMOUNTS THE CHROME: every layer left above the video costs GPU, and the docked bar
                    // is what stacked a second transport under the video's own. A predicate, so the edge never re-renders
                    // the frame.
                    Flow.Show(s_chromeMounted, ChromeRow()),
                    ContentRegion(vp),
                    Flow.Show(s_chromeMounted, PlayerBarDock()),
                ],
            };

            var tinted = new BoxEl
            {
                Grow = 1f, ZStack = true, Fill = ColorF.Transparent,
                // The shell-wide "drop a file to play it" target. The engine hands a drop to the DEEPEST accepting target,
                // so a row's own file target still wins; only drops on the rest of the window land here.
                DropTarget = _fileDrop,
                Children = [MaterialLayer(), column],
            };

            var stack = ZStack(
                tinted,
                // The immersive stage covers the content, the sidebar and the rail, and sits below the banner, the drop cue
                // and the toast lane. A predicate, not a render read; Direction 1 so an oversized measure is cross-clamped.
                Flow.Show(static () => Ui.ImmersiveLyrics.Value, new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, HitTestPassThrough = true,
                    Enter = Stage.EnterTerminal, Exit = Stage.ExitTerminal,
                    Children = [Stage.View()],
                }),
                Overlays(),
                FileDropLayer(),
                CommandPalette(),
                Video.PipLayer(),
                Video.FullscreenLayer(),
                Embed.Comp(static () => new CoverScrim()),
                DragPreviewLayer.Of(Drag.Preview),
                Diagnostics.FpsOverlay()) with { Grow = 1f };

            return Ctx.Provide(ShellMaterial.Slot, MaterialState, OverlayHost.Create(stack));
        }

        Element ContentRegion(IReadSignal<Size2> vp) => ZStack(
            new BoxEl
            {
                // The row never moves on a toggle: the content card FLIPs relative to THIS frame.
                MorphId = ContentRowMorphId, Direction = 0, Grow = 1f, ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        // THE W12 TRAP lives in FrameRules.SidebarPaneWidth: a drag peek presents the pane expanded, so this
                        // clipped column widens with it.
                        Direction = 1, Shrink = 0f, ClipToBounds = true,
                        Width = Prop.Of(static () => FrameRules.SidebarPaneWidth(Ui.SidebarPresentedCompact.Value,
                            Sidebar.DragPeek.Value, Sidebar.Width.Value)),
                        Animate = SidebarPaneAnim,
                        Children =
                        [
                            // The layout firewall for the whole sidebar, INSIDE the animating pane.
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, IsolateLayout = true, ClipToBounds = true,
                                Opacity = Prop.Of(() => _sidebarFade.Value),
                                Children = [Sidebar.Pane()],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        // The stock Win11 content region: flush, one corner, a left+top stroke, no shadow, no gutter.
                        Direction = 1, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
                        Children =
                        [
                            new BoxEl { Grow = 1f, Fill = Prop.Of(static () => Design.Colors.FileArea), Corners = Design.Size.ContentPaneCorners },
                            new BoxEl
                            {
                                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                                Fill = ColorF.Transparent, Corners = Design.Size.ContentPaneCorners, ClipToBounds = true,
                                IsolateLayout = true, Animate = ContentCardAnim, RelativeTo = ContentRowMorphId,
                                Children = [Embed.Comp(static () => new ContentHost())],
                            },
                            ContentRegionStroke(),
                        ],
                    },
                    // The rail's 8-DIP breathing room exists only while the rail is inline.
                    new BoxEl
                    {
                        Shrink = 0f, HitTestVisible = false,
                        Width = Prop.Of(static () => FrameRules.RailGapWidth(Ui.RailOpen.Value, Ui.RailFits.Value)),
                    },
                    // The rail RESERVATION: snaps 0 ↔ width at commit; its underlay is the docked band's ONE coat.
                    new BoxEl
                    {
                        Shrink = 0f,
                        Width = Prop.Of(static () => FrameRules.RailReservedWidth(Ui.RailOpen.Value, Ui.RailFits.Value, Ui.RailWidth.Value)),
                        Children =
                        [
                            new BoxEl
                            {
                                Grow = 1f, HitTestPassThrough = true, ClipToBounds = true, ZStack = true,
                                Children = [new BoxEl { Margin = StrokeOverhang, Fill = Prop.Of(static () => Design.Colors.FileArea), Corners = RailBandCorners }],
                            },
                        ],
                    },
                ],
            },
            // The sidebar seam: a strip translated to the pane edge, entirely on the content side of it.
            new BoxEl
            {
                Direction = 1, ClipToBounds = true,
                Width = Prop.Of(static () => FrameRules.SidebarSeamWidth(Ui.NarrowShell.Value)),
                Transform = Prop.Of(static () => Affine2D.Translation(
                    FrameRules.SidebarSeamX(Ui.SidebarPresentedCompact.Value, Sidebar.Width.Value), 0f)),
                Children =
                [
                    Splitter.Create(Sidebar.Width, static () => Sidebar.CommitWidthDrag(Sidebar.Width.Peek()), SidebarSeamOptions,
                        collapsed: Sidebar.Collapsed, fade: _sidebarFade, dragging: _sidebarDragging),
                ],
            },
            // The rail seam: exists only while the rail is open.
            new BoxEl
            {
                Direction = 1, ClipToBounds = true,
                Width = Prop.Of(static () => FrameRules.RailSeamWidth(Ui.RailOpen.Value)),
                Transform = Prop.Of(() => Affine2D.Translation(FrameRules.RailSeamX(vp.Value.Width, Ui.RailWidth.Value), 0f)),
                Children = [Splitter.Create(Ui.RailWidth, CommitRailDrag, RailSeamOptions, collapsed: Ui.RailOpen, fade: _railFade)],
            },
            // The rail overlay: final width always; Rail.Frame owns the translate, the coats and the bodies.
            new BoxEl
            {
                Grow = 1f, Direction = 0, Justify = FlexJustify.End, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Shrink = 0f, ClipToBounds = true, ZStack = true, HitTestPassThrough = true,
                        Width = Prop.Of(static () => Ui.RailWidth.Value),
                        Children =
                        [
                            // The FLOATING rail's backing band (the shell ground) — paint-only, never the deepest hit.
                            new BoxEl
                            {
                                Grow = 1f, HitTestPassThrough = true,
                                Fill = Prop.Of(static () => FrameRules.RailFloats(Ui.RailOpen.Value, Ui.RailFits.Value)
                                    ? Design.Colors.FloatingChrome : ColorF.Transparent),
                            },
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, HitTestPassThrough = true,
                                Corners = RailBandCorners, IsolateLayout = true,
                                Opacity = Prop.Of(() => _railFade.Value),
                                Children = [Rail.Frame()],
                            },
                        ],
                    },
                ],
            },
            Embed.Comp(static () => new NarrowDrawer())) with
        {
            // The ONE region that yields when the window is shorter than the column; clipped so a settling page never
            // paints into the dock slot. The drag spotlight scrim is scoped to it (chrome and dock stay lit).
            Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
            OnRealized = static h => { s_contentRegion = h; PublishScrimClip(); },
            OnBoundsChanged = static _ => PublishScrimClip(),
        };
    }

    static readonly Func<bool> s_chromeMounted = static () =>
        FrameRules.ChromeMounted(Video.PlacementCore.Resolve(Video.State.Surface.Value));

    static readonly Action s_persistCollapsed = static () => Sidebar.SetCollapsed(true);
    static readonly Action s_persistExpanded = static () => Sidebar.SetCollapsed(false);
    static readonly Action s_drainActivation = DrainActivation;

    static Action ZoomApplier(float zoom) => () => FluentApp.SetZoom(zoom);

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

    static Element ContentRegionStroke() => new BoxEl
    {
        ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Margin = StrokeOverhang, BorderWidth = 1f, BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
                Corners = Design.Size.ContentPaneCorners,
            },
        ],
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

    /// <summary>Follow the OS theme live while the preference is "System" (mode 0); the accent follows always.</summary>
    static void OnSystemColorsChanged()
    {
        if (FluentApp.SystemAccentRamp() is { } ramp) Tok.SetAccent(in ramp);
        if (Platform.Settings.Get(Platform.Keys.ThemeMode) != 0) return;
        var kind = FluentApp.SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark;
        if (kind == Tok.Theme) return;
        if (SeedPalette is { } seed) seed(kind);
        else Tok.Use(kind);
        s_requestTheme?.Invoke(Design.Motion.Standard);
    }

    static void ToggleSidebar()
    {
        // Narrow: the hamburger opens the drawer and never writes the desktop collapse preference.
        if (Ui.NarrowShell.Peek()) { Ui.DrawerOpen.Value = !Ui.DrawerOpen.Peek(); return; }
        Sidebar.SetCollapsed(!Sidebar.Collapsed.Peek());
    }

    static void CommitRailDrag()
    {
        float w = ClampRailWidth(Ui.RailWidth.Peek());
        Ui.RailWidth.Value = w;
        Platform.Settings.Set(Platform.Keys.ShellRailWidth, w);
    }

    /// <summary>F11 — toggles video fullscreen only while a video is active. With focus inside a player the element
    /// consumes F11 first (focused routing precedes accelerators), so exactly one of the two fires.</summary>
    static void ToggleVideoFullscreen()
    {
        switch (FrameRules.F11(Video.State.Resolved == Video.SurfacePlacement.Fullscreen, Video.State.IsActive))
        {
            case FrameRules.FullscreenToggle.Enter: Video.State.EnterFullscreen(); break;
            case FrameRules.FullscreenToggle.Exit: Video.State.ExitFullscreen(); break;
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
                    Ui.ImmersiveLyrics.Value = false;
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
            TogglePlayPause();
            e.Handled = true;
        }
    }

    static bool FocusedIsTextEditor()
    {
        var focused = s_hooks?.GetFocus?.Invoke() ?? default;
        if (focused.IsNull || s_scene is not { } scene || !scene.IsLive(focused)) return false;
        ref var ix = ref scene.Interaction(focused);
        return ix.Role == AutomationRole.Text || (ix.HandlerMask & InteractionInfo.CharBit) != 0;
    }

    static void PublishScrimClip()
    {
        if (s_scene is not { } scene || s_contentRegion.IsNull || !scene.IsLive(s_contentRegion)) return;
        RectF r = scene.AbsoluteRect(s_contentRegion);
        scene.SpotlightScrimClip = r.IsEmpty ? null : r;
    }

    /// <summary>The centred drop pill — bound opacity, so a drag hover never re-renders the shell.</summary>
    static Element FileDropLayer() => new BoxEl
    {
        Grow = 1f, HitTestPassThrough = true, Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
        Opacity = Prop.Of(static () => FileDropOver.Value ? 1f : 0f),
        Children =
        [
            new BoxEl
            {
                HitTestVisible = false,
                Padding = new Edges4(18f, 10f, 18f, 10f), Corners = CornerRadius4.All(Radii.Control),
                Fill = Prop.Of(static () => Tok.FillSolidBase), BorderColor = Prop.Of(static () => Tok.AccentDefault),
                BorderWidth = 1f, Shadow = Elevation.Dialog,
                Children = [new TextEl(Loc.Get(Strings.LocalFile.DropHint)) { Size = 14f, Color = Tok.TextPrimary }],
            },
        ],
    };

    /// <summary>The setup wizard's cover scrim (ch 28 parity item 22): the plate opens with the engine's own overlay
    /// scrim OFF (a shell is always behind it in 0.3 — <see cref="Setup.Covering"/>'s doc comment), so this box is the
    /// only dim. Tok.FillSmoke, 250 ms linear, 0 under reduced motion; nothing ticks while <see cref="Setup.Covering"/>
    /// holds still, and the box is hit-test transparent whenever it is not dimming.</summary>
    sealed class CoverScrim : Component
    {
        bool _mounted;

        public override Element Render()
        {
            bool dim = Setup.Covering.Value == Setup.Cover.Dim;
            float ms = Design.Reduced ? 0f : 250f;
            float target = dim ? 1f : 0f;
            UseTransition(AnimChannel.Opacity, _mounted ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(dim));
            _mounted = true;
            return new BoxEl { Grow = 1f, Fill = Tok.FillSmoke, Opacity = target, HitTestVisible = dim };
        }
    }

    // ══ 3. THE CONTENT HOST ═══════════════════════════════════════════════════════════════════════════════════════

    static readonly KeepAliveOptions s_keepAlive = new(
        // The live page plus a two-deep back stack; parked pages release their image pins.
        MaxEntries: KeepAliveSlots,
        TransitionFor: PageTransition,
        SuppressLayoutTransitionsOnActivation: true);

    static readonly HashSet<string> s_warnedUnknown = new(StringComparer.Ordinal);

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
        var motion = Motion.Peek();
        bool videoSafe = oldToken is Route prev ? NeedsVideoSafe(prev, next) : next.Kind == RouteKind.Module;
        if (videoSafe) return RecipeForVideoSafe(motion);
        return oldToken is Route from ? RecipeFor(from, next, motion) : RecipeFor(motion);
    }

    static Element PageBody(Route route)
    {
        var factory = PageFor(route);
        switch (FrameRules.BodyFor(route, factory is not null, Platform.Settings.Get(Platform.Keys.DeveloperMode)))
        {
            case FrameRules.BodyKind.Page:
                return PageBox("page:" + NameOf(route), factory!(route));
            case FrameRules.BodyKind.Empty:
                return PageBox("page-empty:" + NameOf(route), null);
            default:
                return NotFoundPage(route);
        }
    }

    static BoxEl PageBox(string key, Element? page) => new()
    {
        Key = key, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = page is null ? [] : [page],
    };

    /// <summary>Nothing claims the route (a retired key, a stale restored tab, a developer route with developer mode off).
    /// NOT "coming soon": say so, keep the destination's own glyph, give the one action that always works.</summary>
    static Element NotFoundPage(in Route route)
    {
        var (_, glyph) = Dest(route);
        string key = NameOf(route) + "|" + (ArgOf(route) ?? "");
        if (s_warnedUnknown.Add(key)) Log.Warn("nav", "route.unknown: " + key);   // once per key per process
        return new BoxEl
        {
            Key = "page-notfound:" + key,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Gap = Spacing.M,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                Icon(glyph, 40f, Tok.TextTertiary),
                Design.Type.PageHero(Loc.Get(Strings.Nav.PageNotFound)),
                Button.Standard(Loc.Get(Strings.Nav.GoHome), static () => GoTo(new Route(RouteKind.Home))),
            ],
        };
    }

    /// <summary>The page-scale offline strip (ch 29 W9 C / W10) floated over the kept content while a stored credential
    /// exists but the session is down. The chrome's accent Reconnect is the loud layer; this one is caution-tinted.</summary>
    static Element OfflineLane() => new BoxEl
    {
        Grow = 1f, Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
        Padding = new Edges4(16f, 12f, 16f, 0f),
        Children =
        [
            new BoxEl
            {
                MaxWidth = 720f, Shrink = 0f, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Control),
                Fill = Prop.Of(static () => Tok.FillSolidBase), Shadow = Elevation.Flyout,
                Children = [Controls.OfflineStrip(onRetry: static () => Spotify.Login())],
            },
        ],
    };

    // ══ 4. THE MERGED CHROME ROW ══════════════════════════════════════════════════════════════════════════════════
    //
    // ONE 48-DIP TitleBar in merged mode: the tabs island (nav cluster + text-first strip) · the window-centred omnibar
    // (`+Shell.Masthead.UI.cs`) · the trailing identity island. The island builders run INSIDE the bar's render — no
    // hooks here; hook-owned behaviour lives in child components. `ContentVersion` is the bar's memo key and folds every
    // term an island reads.

    /// <summary>The island-button footprint: 40×44, the bar's own nav metric (a 32-DIP button would leave strips that are
    /// neither draggable nor clickable). A property: the default style captures theme brushes.</summary>
    static IconButton.Style ChromeButtonStyle => IconButton.DefaultStyle with { Size = 40f, Height = 44f };

    /// <summary>2 DIP either side — what the bar gives its own pane toggle.</summary>
    static readonly Edges4 ChromeButtonMargin = new(2f, 0f, 2f, 0f);

    /// <summary>The strip's natural content extent (quantised), fed back into the chrome allocator.</summary>
    static readonly Signal<float> s_tabExtent = new(Layout.ChromeTabMinW);

    static TabStrip? s_strip;

    /// <summary>Nudge the bar's hamburger four DIP right so it centres over the 56-DIP compact rail's icons (Margin is
    /// not an owned property of the part). Built once: mutating a TemplateParts invalidates its prototype cache.</summary>
    static readonly TemplateParts s_chromeParts = BuildChromeParts();

    static TemplateParts BuildChromeParts()
    {
        var parts = new TemplateParts();
        parts[TitleBar.PartPaneToggle] = static b => b with { Margin = new Edges4(6f, 2f, -2f, 2f) };
        return parts;
    }

    static readonly LayoutTransition TabLaneMotion = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(FgMotion.ControlFast, Easing.FluentPopOpen));

    static Element ChromeRow() => Embed.Comp(static () =>
    {
        var bar = new TitleBar
        {
            IconGlyph = "", ShowBackButton = false, ShowCaptionButtons = true,
            ShowPaneToggle = true, OnPaneToggle = ToggleSidebar, Parts = s_chromeParts,
            ShowRailBaseline = false,          // the seam below is the content region's own stroke
            Tabs = TabsIsland, TabsVersion = TitleBarTabsVersion,
            TabsElasticLane = true,            // tabs absorb the overrun; the omnibar keeps its allocation
            Trailing = TrailingIsland, CaptionLeading = CaptionLeadingIsland,
            ContentVersion = ChromeContentVersion,
        };
        bar.CenterContent = _ => CenterIsland(bar.CenterAvail);
        return bar;
    });

    static int ChromeContentVersion()
    {
        var l = ChromeLayout.Value;
        int flags = (l.ShowName ? 1 : 0) | (l.ShowActions ? 2 : 0) | (l.ShowForward ? 4 : 0)
                  | (l.SearchMode == MergedSearchMode.Icon ? 8 : 0) | (l.ShowBack ? 16 : 0)
                  | (l.ShowNewTab ? 32 : 0) | (l.ShowTrailing ? 64 : 0);
        var r = Current.Value;
        // The route and the pin store's version: the trailing Pin/Unpin follows a navigation and a pin change.
        return HashCode.Combine(flags, (int)l.SearchWidth, (int)l.LeadClusterW, TitleBarTabsVersion(), (int)Auth.Value,
            HashCode.Combine(r.Kind, r.Subject, r.Arg), Sidebar.PinsVersion.Value);
    }

    // Both fold Shown: the strip's labels come from the staged route, so it must rebuild when that route LANDS (the
    // exit leg after the commit), not only when the workspace or the selection changes.
    static int TitleBarTabsVersion() => HashCode.Combine(TabsVersion.Value, SelectedTab.Value, Shown.Value);

    static int TabStripItemsVersion() => HashCode.Combine(TabsVersion.Value, ChromeLayout.Value.ShowNewTab, Shown.Value);

    /// <summary>The tabs island takes a RESERVED, quantised width (issue #88): hugging the strip shoved the centred search
    /// by half of every title swing.</summary>
    static Element TabsIsland()
    {
        var l = ChromeLayout.Value;
        var kids = new List<Element>(3);
        if (l.ShowBack)
        {
            kids.Add(Embed.Comp(static () => new NavHistoryButton(forward: false)) with { Key = "chrome-back" });
        }
        if (l.ShowForward)
        {
            kids.Add(Embed.Comp(static () => new NavHistoryButton(forward: true)) with { Key = "chrome-forward" });
        }
        if (s_strip is { } strip) strip.IsAddTabButtonVisible = l.ShowNewTab;
        kids.Add(Embed.Comp(BuildTabStrip) with { Key = "chrome-tab-strip" });
        return new BoxEl
        {
            Key = "chrome-tabs-lane", Animate = TabLaneMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Height = TitleBar.ExpandedHeight,
            Width = l.TabIslandWidth, Shrink = 0f, ClipToBounds = true, Children = kids.ToArray(),
        };
    }

    static TabStrip BuildTabStrip()
    {
        var strip = new TabStrip
        {
            // TEXT-FIRST tabs: weight + opacity carry selection, one sliding 2-DIP accent underline marks it.
            Appearance = TabStripAppearance.Text,
            OverflowMode = TabStripOverflowMode.Scroll,
            TextFontSize = 13f,
            // The "+" is hover-only, but its 32-DIP slot is reserved at all times so the reported client rect never moves.
            IsAddTabButtonVisible = ChromeLayout.Peek().ShowNewTab,
            AddButtonVisibility = TabStripAddButtonVisibility.OnStripPointerOver,
            OnAddTabButtonClick = static () => { OpenTab(new Route(RouteKind.Home)); return null; },
            IndicatorFill = Prop.Of(static () => Tok.AccentDefault),
            MinTabWidth = Layout.ChromeTabMinW,
            MaxTabWidth = Layout.ChromeTabMaxW,
            ItemsSource = BuildTabItems,
            ItemsVersion = TabStripItemsVersion,
            // CONTRACT: the strip WRITES this cell before raising OnSelectionChanged, so it is never evidence of a change —
            // ActivateTab asks the workspace, and the host re-asserts the cell from the model afterwards.
            SelectedIndex = SelectedTab,
            OnSelectionChanged = ActivateTab,
            OnTabCloseRequested = CloseTab,
            ScrollMetricsChanged = static m => s_tabExtent.SetIfChanged(Chrome.TabExtentFromMetrics(m.ContentExtent)),
        };
        s_strip = strip;   // the factory runs once per mount, so the newest instance IS the mounted one
        return strip;
    }

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
                Drag = pinId is { } p ? Drag.Source(() => TabPayload(p, pinTitle), clickPrimary: true) : null,
                DropTarget = TabDropTarget(route, id, destTitle),
            };
        }
        return items;
    }

    /// <summary>A tab dragged out is its destination as a pin payload (tracks resolve lazily, after a compatible drop).</summary>
    static DragPayload TabPayload(string pinId, string title)
    {
        string uri = SidebarPinId.UriOf(pinId);
        var kind = uri.Length > 0 ? Drag.KindOfUri(uri) : DragKind.Route;
        Func<CancellationToken, Task<Track[]>>? resolver = null;
        if (uri.Length > 0 && (kind is DragKind.Playlist or DragKind.Album or DragKind.Show)
            && Sidebar.LibraryWrites?.ResolveTracks is { } resolve)
            resolver = ct => resolve(uri, ct);
        return new DragPayload(kind, pinId, uri, title, TrackResolver: resolver);
    }

    /// <summary>Every tab is a spring-load waypoint (a dwell activates it); a tab standing for an EDITABLE playlist is
    /// also a deposit destination (the cross-tab append). SpringLoadOnly keeps a non-destination silent rather than a
    /// flashing refusal as a drag crosses the strip.</summary>
    static DropTargetSpec TabDropTarget(in Route route, int tabId, string name)
    {
        if (route.Kind == RouteKind.Playlist && route.Subject.IsValid
            && Entities.Current.Playlists.TryGetSlot(route.Subject.Text.AsSpan(), out int slot) && new Playlist(slot).Editable)
        {
            string uri = route.Subject.Text;
            return Drop.Target<DragPayload>(Drag.Resource,
                accepts: p => Drag.TabAcceptsDeposit(uri, targetEditable: true, p.CanCopyTracks, p.SourcePlaylistUri, p.Uri),
                onDrop: (p, _) => Sidebar.LibraryWrites?.DepositTracks?.Invoke(uri, name, p),
                caption: _ => Drag.AddTo(name),
                settleOnDrop: false,   // the deposited feel: the lifted visual snaps home
                springLoadMs: Drag.SpringLoadMs,
                onSpringLoad: (_, _) => ActivateTabById(tabId));
        }
        return Drop.Target<DragPayload>(Drag.Resource,
            springLoadMs: Drag.SpringLoadMs,
            onSpringLoad: (_, _) => ActivateTabById(tabId),
            springLoadOnly: true);
    }

    static ContextMenuModel? TabMenu(int tabId)
    {
        int index = Tabs.IndexOf(tabId);
        if (index < 0) return null;
        var tab = Tabs.Tabs[index];
        bool pinned = tab.Pinned;
        var rows = new List<MenuFlyoutItem>(9)
        {
            new MenuFlyoutItem(Loc.Get(pinned ? Strings.Shell.UnpinTab : Strings.Shell.PinTab),
                ActionIcons.Resolve(pinned ? ActionIcons.Unpin : ActionIcons.Pin), true, () => SetTabPinned(tabId, !pinned)),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseTab), Icons.Cancel, Tabs.Count > 1, () => CloseTabById(tabId)),
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseOtherTabs), default, Tabs.HasOtherUnpinned(tabId), () => CloseOtherTabs(tabId)),
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseTabsRight), default, Tabs.HasUnpinnedToRight(index), () => CloseTabsToRight(tabId)),
            new MenuFlyoutItem(Loc.Get(Strings.Shell.CloseAllUnpinned), default, Tabs.HasAnyUnpinned(), CloseAllUnpinnedTabs),
        };
        if (PinRow(tab.Route) is { } pinRow)
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(pinRow);
        }
        return new ContextMenuModel(rows, new ContextMenuHeader(null, Dest(tab.Route).Title));
    }

    /// <summary>The absolute Pin/Unpin row for a destination, or null when it is not pinnable (<see cref="PinRowRule"/>).</summary>
    static MenuFlyoutItem? PinRow(in Route route)
    {
        string? pinId = FrameRules.PinIdFor(route);
        var kind = PinRowRule.Decide(hasStore: true, pinId, Sidebar.IsPinned(pinId));
        if (kind == PinRowKind.None || pinId is not { } id) return null;
        string title = route.Kind == RouteKind.Search ? Loc.Get(Strings.Nav.Search) : Dest(route).Title;
        return Actions.Menu.Pin(kind == PinRowKind.Unpin, () => PinDestination(id, title), () => UnpinDestination(id));
    }

    static void PinDestination(string pinId, string title)
    {
        var pin = new SidebarPin(pinId, SidebarPinId.KindOf(pinId), SidebarPinId.UriOf(pinId), title,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!Sidebar.Pin(pin)) return;   // already pinned: a silent no-op, never a claim it did something
        Notify.Say(title.Length > 0 ? Strings.Sidebar.PinnedToast(title) : Loc.Get(Strings.Sidebar.Pin.Pinned),
            InfoBarSeverity.Success, Loc.Get(Strings.Sidebar.Pin.Undo), () => Sidebar.Unpin(pinId));
    }

    static void UnpinDestination(string pinId)
    {
        int at = Sidebar.Pins.IndexOf(pinId);
        if (at < 0) return;
        var removed = Sidebar.Pins[at];
        if (Sidebar.Unpin(pinId) < 0) return;
        Notify.Say(Loc.Get(Strings.Sidebar.Pin.Unpinned), InfoBarSeverity.Informational,
            Loc.Get(Strings.Sidebar.Pin.Undo), () => Sidebar.InsertPin(removed, at));
    }

    static Element ChromeButton(string glyph, Action onClick, string tooltip, string key)
        => ToolTip.Wrap(IconButton.Create(glyph, onClick, ChromeButtonStyle) with { Key = key, Margin = ChromeButtonMargin }, tooltip);

    static Element CaptionLeadingIsland()
    {
        var l = ChromeLayout.Value;
        bool dark = Theme.Dark;
        var theme = ToolTip.Wrap(
            IconButton.Create(dark ? Icons.Sun : Icons.Moon, static () => ToggleTheme(s_requestTheme), ChromeButtonStyle)
                with { Key = "chrome-theme-toggle", Margin = ChromeButtonMargin },
            Loc.Get(dark ? Strings.Shell.LightTheme : Strings.Shell.DarkTheme));
        return new BoxEl
        {
            Direction = 0, Height = TitleBar.ExpandedHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children = l.SearchMode == MergedSearchMode.Icon ? [theme, SearchFlyoutButton()] : [theme],
        };
    }

    static Element TrailingIsland()
    {
        var l = ChromeLayout.Value;
        if (!l.ShowTrailing) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        var kids = new List<Element>(5) { AuthChip() };
        // The ONE "actions in row" stage: bell, friends, pin and settings enter together. Below it they fold into the
        // profile menu (pin simply drops — the tab menu still offers it).
        if (l.ActionsInRow)
        {
            kids.Add(Embed.Comp(static () => new BellButton()) with { Key = "chrome-bell" });
            kids.Add(ChromeButton(Icons.Friends, static () => Ui.Toggle(RailMode.Friends), Loc.Get(Strings.Shell.Friends), "chrome-friends"));
            kids.Add(PinButton());
            kids.Add(ChromeButton(Icons.Settings, static () => GoTo(new Route(RouteKind.Settings)), Loc.Get(Strings.Auth.Settings), "chrome-settings"));
        }
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Height = TitleBar.ExpandedHeight,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The identity form from the AUTH FOLD, never raw session status: a silent resume behind the cache-first
    /// shell is Connecting, and an actionable "Sign in" there would race it (ch 18 W17).</summary>
    static Element AuthChip() => FrameRules.ChipFor(Auth.Value) switch
    {
        FrameRules.ChipForm.Profile => ProfileChip() with { Key = "chrome-profile" },
        FrameRules.ChipForm.Connecting => new BoxEl
        {
            Key = "chrome-connecting", Height = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
            Children = [Caption(Loc.Get(Strings.Shell.Connecting)).Secondary()],
        },
        // Offline = a credential is on disk but the resume failed: the verb is "try again", not "sign in".
        var form => Button.Accent(Loc.Get(form == FrameRules.ChipForm.Reconnect ? Strings.Shell.Reconnect : Strings.Shell.SignIn),
            static () => Spotify.Login()) with { Key = "chrome-sign-in" },
    };

    /// <summary>The direct Pin/Unpin for the destination on screen. Its 44 DIP are budgeted unconditionally (issue #88), so
    /// an unpinnable destination gets an invisible placeholder of the same width — the tree stays honest to the budget.</summary>
    static Element PinButton()
    {
        if (PinRow(Current.Value) is not { Invoke: { } invoke } row)
            return new BoxEl
            {
                Key = "chrome-pin-placeholder", Width = Layout.ChromeNavButtonW, Height = TitleBar.ExpandedHeight,
                Shrink = 0f, HitTestVisible = false, Opacity = 0f,
            };
        return ChromeButton(row.Icon.Glyph ?? Icons.Pin, invoke, row.Label, "chrome-pin");
    }

    /// <summary>Back or Forward: the primary on click, the history flyout on right-click / touch-hold.</summary>
    sealed class NavHistoryButton(bool forward) : Component
    {
        public override Element Render()
        {
            bool canDo = (forward ? CanForward : CanBack).Value;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);

            void OpenHistory(ContextRequestEventArgs _)
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var stack = forward ? s_nav.Forward : s_nav.Back;   // read at OPEN, never at mount
                var (rows, hasMore) = FrameRules.HistoryMenu(stack.Count);
                if (rows == 0) return;                              // an empty stack opens nothing at all
                var items = new MenuFlyoutItem[rows + (hasMore ? 2 : 0)];
                for (int i = 0; i < rows; i++)
                {
                    var r = stack[FrameRules.HistoryMenuIndex(stack.Count, i)];
                    var (title, glyph) = Dest(r);
                    items[i] = new MenuFlyoutItem(title, glyph, true, () => GoTo(r));
                }
                if (hasMore)
                {
                    items[rows] = MenuFlyoutItem.Separator;
                    items[rows + 1] = new MenuFlyoutItem(Loc.Get(Strings.Nav.ViewAllHistory), Icons.Clock, true,
                        static () => GoTo(new Route(RouteKind.History)));
                }
                var h = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value = h;
                h.ClosedAction = () => handle.Value = null;
            }

            return IconButton.Create(forward ? Icons.Forward : Icons.Back, forward ? GoForward : (Action)GoBack,
                    ChromeButtonStyle, isEnabled: canDo)
                with { Margin = ChromeButtonMargin, OnRealized = h => anchor.Value = h, OnContextRequested = OpenHistory };
        }
    }

    /// <summary>The trailing island's bell: the unread pill over the button's own box (never its footprint).</summary>
    sealed class BellButton : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            int unread = Notify.Unread.Value;

            void Toggle() => OpenNotificationPanel(overlay, () => anchor.Value, handle);

            var style = ChromeButtonStyle;
            var button = IconButton.Create(Icons.Bell, Toggle, style) with { OnRealized = h => anchor.Value = h };
            float w = style.Size, hgt = style.Height ?? style.Size;
            BoxEl content = unread <= 0 ? button : new BoxEl
            {
                ZStack = true, Width = w, Height = hgt, Shrink = 0f,
                Children =
                [
                    button,
                    new BoxEl
                    {
                        Width = w, Height = hgt, Direction = 1, Justify = FlexJustify.Start, HitTestVisible = false,
                        Children = [new BoxEl { Direction = 0, Justify = FlexJustify.End, Children = [InfoBadge.Count(unread)] }],
                    },
                ],
            };
            return ToolTip.Wrap(content with { Margin = ChromeButtonMargin }, Loc.Get(Strings.Notifications.Title));
        }
    }

    // ══ 5. THE NARROW DRAWER ═══════════════════════════════════════════════════════════════════════════════════════
    //
    // In the narrow band the 56-DIP rail stays inline and the hamburger opens a separately-retained full pane OVER the
    // page, so the saved desktop preference is never overwritten. Always mounted; open/close is compositor-only.

    sealed class NarrowDrawer : Component
    {
        Func<int, bool>? _savedPreview;
        Func<int, bool>? _escapePreview;
        bool _installed;

        public override Element Render()
        {
            bool narrow = Ui.NarrowShell.Value;
            bool open = narrow && Ui.DrawerOpen.Value;
            var hooks = UseContext(InputHooks.Current);
            _escapePreview ??= key =>
            {
                if ((key == Keys.Escape || key == Keys.GamepadB) && Ui.DrawerOpen.Peek())
                {
                    Ui.DrawerOpen.Value = false;
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
            // Any navigation closes the drawer (a row click, a chord, a deep link).
            UseSignalEffect(static () =>
            {
                _ = Current.Value;
                Ui.DrawerOpen.SetIfChanged(false);
            });

            // BUG E1 (perf): on a desktop-width window the drawer is unreachable, so mount NOTHING — no scrim, no
            // second `Sidebar.DrawerPane()` (a whole second PaneView planning the sidebar on every invalidation and
            // a second `PumpBinder` registration), no drawer PaneHost at all. Gated on NARROWNESS ALONE, never on
            // `Ui.DrawerOpen` (see `NarrowDrawerMount.ShouldMount`): the pane must stay mounted for the ENTIRE time
            // the shell is narrow, closed included, or the first open after entering the narrow band would have
            // nothing already-mounted for `DrawerPane`'s open/close slide (`UseTransition`) to animate from — it
            // would just pop in. `PaneHost`'s `UseSignalEffect(PumpBinder)` is a `SignalEffectCell`
            // (`IDisposableCell`), so mounting/unmounting `DrawerPane` across the breakpoint cleanly
            // registers/unregisters the binder pump instead of leaking a second one (verified in the engine's
            // `RenderContext.RunAllCleanups`).
            // Peek, not Value: ShouldMount ignores drawerOpen entirely (see its own doc), so reading it here would
            // only add a needless subscription — `open` above already reads it live for the cases that DO care.
            if (!NarrowDrawerMount.ShouldMount(narrow, Ui.DrawerOpen.Peek()))
                return new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

            return new BoxEl
            {
                Grow = 1f, ZStack = true, HitTestVisible = open,
                Children =
                [
                    Embed.Comp(static () => new DrawerScrim()),
                    new BoxEl
                    {
                        Grow = 1f, Direction = 0, Justify = FlexJustify.Start, HitTestPassThrough = true,
                        Children = [Embed.Comp(static () => new DrawerPane())],
                    },
                ],
            };
        }
    }

    /// <summary>BUG E1's pure mount decision (`docs/plans/wavee/wavee-0.3-bug-handoff-2026-09-15.md` §7,
    /// `NarrowDrawer.Render`): whether the narrow drawer's heavy subtree (the scrim plus a second full
    /// `Sidebar.DrawerPane()`) should be mounted at all. Deliberately takes <paramref name="drawerOpen"/> and
    /// ignores it — the decision is narrowness alone. A version of this that also required <paramref
    /// name="drawerOpen"/> would unmount the pane the instant it closes on a narrow window, which would then pop
    /// in with no animation the next time it opens (the reveal/close slide needs the pane already mounted).
    /// Engine-free and pure so it is unit-testable without mounting a component
    /// (<c>ShellNarrowDrawerTests</c>).</summary>
    public static class NarrowDrawerMount
    {
        public static bool ShouldMount(bool narrowShell, bool drawerOpen) => narrowShell;
    }

    sealed class DrawerScrim : Component
    {
        bool _mounted;

        public override Element Render()
        {
            bool open = Ui.DrawerOpen.Value;
            float ms = Design.Reduced ? 0f : Design.Motion.Fast;
            float target = Layout.DrawerRestingOpacity(open);
            UseTransition(AnimChannel.Opacity, _mounted ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(open));
            _mounted = true;
            return new BoxEl
            {
                Grow = 1f, Fill = ColorF.FromRgba(0, 0, 0, 0x33), Opacity = target,
                HitTestVisible = open, OnClick = static () => Ui.DrawerOpen.Value = false,
            };
        }
    }

    sealed class DrawerPane : Component
    {
        bool _mounted;

        public override Element Render()
        {
            bool open = Ui.DrawerOpen.Value;
            var vp = UseContextSignal(Viewport.Size);
            float width = Layout.DrawerWidth(vp.Peek().Width, Sidebar.Width.Peek());
            float ms = Design.Reduced ? 0f : PaneMs;
            float target = Layout.DrawerRestingTranslateX(open, width);
            UseTransition(AnimChannel.TranslateX, _mounted ? (open ? -width : 0f) : target, target, ms, Easing.SmoothOut, DepKey.From(open));
            _mounted = true;

            return new BoxEl
            {
                Direction = 1, Shrink = 0f, Grow = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
                // The resting position stays coupled to the live width: a closed drawer can grow while resizing inside the
                // band, and a stale static translation would expose the added strip.
                Transform = Prop.Of(() => Affine2D.Translation(
                    Layout.DrawerRestingTranslateX(Ui.DrawerOpen.Value, Layout.DrawerWidth(vp.Value.Width, Sidebar.Width.Value)), 0f)),
                Width = Prop.Of(() => Layout.DrawerWidth(vp.Value.Width, Sidebar.Width.Value)),
                // A stock OVERLAY PANE (NavigationView minimal mode): in-app acrylic + flyout elevation, not a dialog slab.
                Fill = ColorF.Transparent, Acrylic = Tok.AcrylicFlyout,
                BorderWidth = 1f, BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
                Corners = new CornerRadius4(0f, Radii.Card, Radii.Card, 0f),
                Shadow = Elevation.Flyout, HitTestVisible = open,
                Children = [Sidebar.DrawerPane()],
            };
        }
    }

    // ══ 6. THE NOTIFICATION PANEL (ch 19 W14-W17, A9) ══════════════════════════════════════════════════════════════

    static Action? s_closeNotificationPanel;
    static string? s_runningVersion;

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
        Action close = h.Close;
        s_closeNotificationPanel = close;
        h.ClosedAction = () =>
        {
            handle.Value = null;
            if (ReferenceEquals(s_closeNotificationPanel, close)) s_closeNotificationPanel = null;
        };
        Notify.PanelOpened(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>A click that navigates away dismisses the panel (a floating panel over a new page reads as stuck).</summary>
    static void CloseNotificationPanel() => s_closeNotificationPanel?.Invoke();

    sealed class NotificationPanelView : Component
    {
        public override Element Render()
        {
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
                if (Notify.PassesFilter(n, filter)) rows.Add(NotificationRow(n, now, expanded, expandedId));
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
            PanelLink(Loc.Get(Strings.Notifications.MarkAllRead), static () =>
            {
                Notify.MarkAllRead(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Notify.ClearLive();
            }),
        };
        if (Notify.ShowsClear(filter) && Notify.ClearActivity is { } clear)
            kids.Add(PanelLink(Loc.Get(Strings.Notifications.Clear), clear));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Padding = new Edges4(14f, 12f, 8f, 8f),
            Children = kids.ToArray(),
        };
    }

    static Element FilterPills(NotifyFilter current) => new BoxEl
    {
        Direction = 0, Gap = 6f, Padding = new Edges4(12f, 2f, 12f, 8f),
        Children =
        [
            FilterPill(Strings.Notifications.Filter.All, NotifyFilter.All, current),
            FilterPill(Strings.Notifications.Filter.Updates, NotifyFilter.Updates, current),
            FilterPill(Strings.Notifications.Filter.Spotify, NotifyFilter.Spotify, current),
            FilterPill(Strings.Notifications.Filter.New, NotifyFilter.New, current),
            FilterPill(Strings.Notifications.Filter.Activity, NotifyFilter.Activity, current),
        ],
    };

    static Element FilterPill(string labelKey, NotifyFilter pill, NotifyFilter current)
    {
        bool on = pill == current;
        return new BoxEl
        {
            Shrink = 0f, MinHeight = 26f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(11f, 3f, 11f, 3f), Corners = CornerRadius4.All(13f),
            Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary,
            HoverFill = on ? Tok.AccentSecondary : Design.Colors.RowHover,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => Notify.Filter.Value = pill,
            Children = [new TextEl(Loc.Get(labelKey)) { Size = 12f, Weight = 600, Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary }],
        };
    }

    static Element NotificationRow(Notification n, long now, Signal<string> expanded, string expandedId) => n.Category switch
    {
        NotifyCategory.AppUpdate => NotificationCard("ntf:" + n.Id, UpdateRow(n.Update ?? AppUpdateSnapshot.Idle), n.IsUnread, null),
        NotifyCategory.Social => NotificationCard("ntf:" + n.Id, SocialRow(n, now), n.IsUnread, () => OpenSocial(n)),
        NotifyCategory.NewRelease => NotificationCard("ntf:" + n.Id, ReleaseRow(n), n.IsUnread, () => OpenRelease(n)),
        _ => ActivityCard(n, now, expanded, expandedId),
    };

    /// <summary>The generic row frame: 56 min, r8, the hover rung, the unread dot, and the enter/exit/slide choreography.</summary>
    static Element NotificationCard(string key, Element content, bool unread, Action? onClick) => new BoxEl
    {
        Key = key,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, MinHeight = 56f,
        Padding = new Edges4(10f, 8f, 10f, 8f), Corners = CornerRadius4.All(8f),
        HoverFill = onClick is not null ? Design.Colors.RowHover : ColorF.Transparent,
        PressedFill = onClick is not null ? Design.Colors.RowPressed : ColorF.Transparent,
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
                    new TextEl(s.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "%")
                        { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
                ],
            };
        }
        else if (arm.Actions.Length > 0)
        {
            var buttons = new Element[arm.Actions.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                var verb = arm.Actions[i];
                buttons[i] = PanelPill(UpdateVerbLabel(verb), () => RunUpdateRowVerb(verb, s), accent: i == 0);
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
                        new TextEl(title) { Size = 13.5f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
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

    /// <summary>What's new navigates (the running build's notes once it landed, else the target's) and, on a completed
    /// update, acknowledges it; every other verb is the updater's.</summary>
    static void RunUpdateRowVerb(UpdateRowAction verb, AppUpdateSnapshot s)
    {
        if (verb != UpdateRowAction.WhatsNew)
        {
            Notify.UpdateCommand?.Invoke(verb, s);
            return;
        }
        GoTo(Parse("whatsnew", Notify.NotesVersion(s, RunningVersion())));
        CloseNotificationPanel();
        if (s.State == AppUpdateState.Completed) Notify.UpdateCommand?.Invoke(UpdateRowAction.Dismiss, s);
    }

    static string RunningVersion()
        => s_runningVersion ??= Notify.AppUpdateVersion.ReleaseTagVersion(typeof(Shell).Assembly.GetName().Version?.ToString());

    static Element SocialRow(in Notification n, long now)
    {
        var text = new List<Element>(2)
        {
            new TextEl(SpotifyUpdates.CleanTitle(n.Title)) { Size = 13f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2 },
        };
        if (n.TimestampMs > 0 && n.TimestampMs != NotifyRows.UpdatePin)
            text.Add(new TextEl(RelativeTime(now - n.TimestampMs)) { Size = 11f, Weight = 600, Color = Tok.TextTertiary });
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

    /// <summary>An in-app route when the target resolves to a destination, else the web page (a profile, a webview).</summary>
    static void OpenSocial(Notification n)
    {
        Notify.MarkRead(n.Id);
        Notify.Dismiss(n.Id);   // G-090: acting on the in-app row also pulls its live OS banner, if one is still up
        if (n.ActionType == SocialActionType.Navigate && n.ActionUri is { Length: > 0 } uri
            && !uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && For(EntityUri.Parse(uri)) is { IsNone: false } route)
        {
            GoTo(route);
            CloseNotificationPanel();
            return;
        }
        OpenWebFor(n.ActionUri);
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
                    new TextEl(n.Title) { Size = 13.5f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(n.Creator ?? "") { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
            PanelTypePill(Loc.Get(Notify.ReleaseTypeKey(n.ReleaseKind, n.AlbumType))),
        ],
    };

    /// <summary>An album opens its page; an episode has no detail route yet, so it opens the web player.</summary>
    static void OpenRelease(Notification n)
    {
        Notify.MarkRead(n.Id);
        Notify.Dismiss(n.Id);   // G-090
        if (n.ReleaseKind == NewReleaseKind.Album && For(n.Subject, n.Title) is { IsNone: false } route)
        {
            GoTo(route);
            CloseNotificationPanel();
            return;
        }
        OpenWebFor(n.Subject.IsValid ? n.Subject.Text : null);
    }

    static void OpenWebFor(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        string web = uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? uri : Actions.WebLinkOf(EntityUri.Parse(uri));
        if (web.Length == 0) return;
        Actions.Services.OpenExternal?.Invoke(web);
        CloseNotificationPanel();
    }

    /// <summary>A local library mutation: summary + age + Undo (when the journal offers it); a click expands the detail
    /// (swapping the card's KEY, so the expansion is a keyed remount the slide choreography carries).</summary>
    static Element ActivityCard(Notification n, long now, Signal<string> expanded, string expandedId)
    {
        bool isExpanded = expandedId == n.Id;
        var right = new List<Element>(2)
        {
            new TextEl(RelativeTime(now - n.TimestampMs)) { Size = 11f, Weight = 600, Color = Tok.TextTertiary, Shrink = 0f },
        };
        if (Notify.UndoActivity is { } undo)
            right.Add(PanelPill(Loc.Get(Strings.Notifications.Undo), () => _ = UndoActivityAsync(undo, n), accent: false));

        var card = new BoxEl
        {
            Key = "ntf:act:" + n.Id,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, MinHeight = 56f,
            Padding = new Edges4(10f, 8f, 10f, 8f), Corners = CornerRadius4.All(8f),
            HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
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
                        new TextEl(n.Title) { Size = 13f, Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Color = Tok.TextPrimary },
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

    static async Task UndoActivityAsync(Func<Notification, Task<bool>> undo, Notification n)
    {
        bool ok;
        try { ok = await undo(n).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("notify", "activity.undo failed", ex); ok = false; }
        // The toast is a UI-thread write; the journal's await may have resumed anywhere.
        if (!ok) Notify.ToUi(static () => Notify.Say(Loc.Get(Strings.Notifications.UndoFailed), InfoBarSeverity.Warning));
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
                        : PanelPill(Loc.Get(Strings.Notifications.Activity.Detail.Open), () => { GoTo(route); CloseNotificationPanel(); }, accent: false),
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
        Children = [new TextEl(message) { Size = 13f, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 300f }],
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
        Children = [Design.Type.Eyebrow(label) with { Color = Tok.TextTertiary }],
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
        HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = onClick,
        Children = [new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary }],
    };
}
