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
//   tinted[ MaterialLayer · column ]  ·  Stage.View  ·  Video.PipLayer  ·  Video.FullscreenLayer  ·  Overlays
//         ·  file-drop cue  ·  CommandPalette  ·  drag preview
//
//   The video layers sit BELOW the banner, toast and drop-cue layers: the video is a hole in the UI swapchain (a DestOut
//   erase over a DComp visual beneath it), so everything drawn BEFORE it is erased where it overlaps and everything
//   drawn AFTER it covers the picture. Those layers are pass-through, so the mini player still takes its drag.
//
//   column = 14 zero-size chord boxes · [chrome row] · content region · [player dock]
//            └ the bracketed bands and the content region COLLAPSE (bound Visible, still mounted) under full-screen video
//              OR the fullscreen stage (FrameRules.ChromeMounted): out of layout, paint and hit-test, subtree inactive
//   content region = ZStack[ row(anchor · sidebar column · page column[Zune band, card stack] · rail gap · rail reservation) ·
//                            sidebar seam · rail overlay(top spacer, Rail.Frame) · rail seam · narrow drawer ]
//   (the Zune band lives in the page column and stops at the inline rail's left edge; the inline rail runs from the title bar's
//   bottom to the dock, and only the narrow overlay starts under the band)
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
using Wavee.HomeUi;
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
    /// <summary>The sidebar column's node, read only by the pane-invariant probe (never a render input).</summary>
    static NodeHandle s_sidebarColumn;

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
        Ui.RailDragWidth.Value = Ui.RailWidth.Peek();
        Ui.DockedVideoHeight.Value = ClampDockedVideoHeight(Platform.Settings.Get(Platform.Keys.ShellDockedVideoHeight), Ui.RailWidth.Peek());
        if (Session.ShellSection is { } shell && shell.RailMode >= (int)RailMode.Lyrics && shell.RailMode <= (int)RailMode.Video)
        {
            Ui.RailOpen.Value = shell.RailOpen;
            Ui.Mode.Value = (RailMode)shell.RailMode;
        }
    }

    /// <summary>The page column's (and so the content card's and the Zune band's) width for a viewport: the viewport less the
    /// sidebar column, the rail's inline gap and the rail's inline reservation. Reads the signals, so a caller inside an effect
    /// or a computed re-runs on a resize, a pane change or a rail toggle. The page gutter and the band's pin visibility decide
    /// from THIS.</summary>
    internal static float PageColumnWidth(float viewportW) => FrameRules.CardWidth(viewportW,
        FrameRules.SidebarPaneWidth(Sidebar.DragPeek.Value, Sidebar.Width.Value, Sidebar.PresentedWidth.Value),
        FrameRules.RailGapWidth(Ui.RailOpen.Value, Ui.RailFits.Value),
        FrameRules.RailReservedWidth(Ui.RailOpen.Value, Ui.RailFits.Value, Ui.RailWidth.Value));

    // ══ 2. THE FRAME ROOT ═════════════════════════════════════════════════════════════════════════════════════════

    // The sidebar collapse AND the content card's FLIP share ONE transition, so the pane's animating edge and the card's
    // left edge ease on identical dynamics. Reveal lays out at the FINAL size and eases a clip + translate only; a grip
    // drag snaps both 1:1 through the suppression arbiter. WinUI SplitView's pane spline at 300 ms (200 ms read as a snap
    // behind the heavier media surface).
    static readonly EasingSpec PaneEase = FrameRules.CardMotionEase;
    const float PaneMs = FrameRules.CardMotionMs;
    const string FrameColumnMorphId = "shell.frame-column";
    /// <summary>The card's X anchor (see <see cref="ContentCardAnchor"/>): fixed in x at the row's left edge, and as far below the
    /// row's top as the Zune band is tall, so it moves in y exactly as the card stack does.</summary>
    const string ContentCardAnchorId = "shell.content-card-anchor";

    /// <summary>The pane opens in 200 ms and closes in 100 ms on WinUI's SplitView spline (MotionTok.PaneOpen/PaneClose).</summary>
    static readonly LayoutTransition SidebarPaneAnim = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(MotionTok.PaneOpen.DurationMs, Easing.FluentPane), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(MotionTok.PaneClose.DurationMs, Easing.FluentPane), SuppressDescendantTransitions: true);

    static readonly LayoutTransition ContentCardAnim = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(PaneMs, PaneEase), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(PaneMs, PaneEase), SuppressDescendantTransitions: true);

    /// <summary>The whole content region's answer to a chrome-edge change that moves its top: a Position FLIP relative to the
    /// frame column, which never moves, plus a Height RELAYOUT. The region lays out once at its final top and height, the FLIP
    /// eases the top back over <see cref="PaneMs"/> / <see cref="PaneEase"/> and the Relayout re-solves the region's subtree at
    /// the interpolated height each tick, so the region's BOTTOM edge stays on the dock the whole time (a Reveal would leave an
    /// empty strip above the dock when the region shrinks, and overlap the dock when it grows). Height only, so the width
    /// stays with the card's own X FLIP (<see cref="ContentCardAnim"/>). The Zune band no longer sits above the region (it
    /// lives in the page column, where <see cref="PageColumnCardAnim"/> does this job for it), so this stays for the chrome-edge
    /// cases only. It is a one-off: the chrome mounting is latched out by <c>_chromeEdge</c> and a window resize never
    /// captures.</summary>
    static readonly LayoutTransition ContentRegionAnim = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(PaneMs, PaneEase), SizeMode.Relayout,
        ExitDynamics: TransitionDynamics.Tween(PaneMs, PaneEase), Axes: SizeAxes.Height);

    /// <summary><see cref="ContentRegionAnim"/>'s idiom one level down, for the page column's card stack: the band above it
    /// opens or closes (84, 52 or 0 DIP), so the card's top travels with the band's revealed bottom edge (a Position FLIP, which
    /// is PARENT-relative on purpose: the stack's x inside the page column never changes, so this FLIP is Y only and the
    /// ground and stroke keep snapping in X) while a Height Relayout keeps its bottom on the dock. The X FLIP of a pane or rail
    /// toggle belongs to the card inside it (<see cref="ContentCardAnim"/>, anchored at <see cref="ContentCardAnchorId"/> so
    /// that it does not see this Y move a second time). Height only: the width stays with the card's own Reveal.</summary>
    static readonly LayoutTransition PageColumnCardAnim = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(PaneMs, PaneEase), SizeMode.Relayout,
        ExitDynamics: TransitionDynamics.Tween(PaneMs, PaneEase), Axes: SizeAxes.Height);

    /// <summary>The Zune band's size change: on a nav-style switch or a row-2 change its height, on a rail toggle its WIDTH (it
    /// is the page column's first child, so it narrows with the card). A Size RELAYOUT on the card's own tween
    /// (<see cref="PaneMs"/> / <see cref="PaneEase"/>), so on a rail toggle the pins slide with the band's right edge instead of
    /// snapping. <see cref="PageColumnCardAnim"/> eases the card stack below it in the same tween, so the band's bottom edge
    /// and the card's top travel together and the card's bottom edge stays on the dock. The row-2 fade is an opacity
    /// transition on a mounted node, so the descendant suppression does not cull it.</summary>
    public static readonly LayoutTransition ZuneBandAnim = new(TransitionChannels.Size,
        TransitionDynamics.Tween(PaneMs, PaneEase), SizeMode.Relayout,
        ExitDynamics: TransitionDynamics.Tween(PaneMs, PaneEase), SuppressDescendantTransitions: true);

    /// <summary>A LEFT+TOP-only stroke: the engine's border is one uniform SDF ring, so the stroked box is one DIP larger on
    /// its right and bottom and parked in a clip of the real geometry — the right/bottom strokes land in the clipped DIP
    /// and the rounded top-left arc survives whole.</summary>
    static readonly Edges4 StrokeOverhang = new(0f, FrameRules.StrokeOverhangTop, -1f, -1f);
    static readonly CornerRadius4 RailBandCorners = new(Radii.Card, 0f, 0f, 0f);

    // CLAMP-ONLY: no `collapsed` argument, so the engine detent is off and the raw cell reaches the rail floor. The mode,
    // the approach band and the fade are all SidebarResizeRules (read off Sidebar.Seam).
    static readonly Splitter.SplitterOptions SidebarSeamOptions = new()
    {
        Min = SidebarRowGeometry.RailWidth, Max = SidebarResizeRules.ExpandedMaxW, ShowIndicator = false,
    };

    // The indicator is the drag GUIDE (F243): the rail seam writes Ui.RailDragWidth, so nothing else moves until release;
    // BuildRailSeamParts shows the 2-DIP thumb only while a drag is in flight (never on hover).
    static readonly Splitter.SplitterOptions RailSeamOptions = new()
    {
        Min = RailMinW, Max = RailMaxW, Polarity = SplitterPolarity.Leading, ShowIndicator = true, InvertCollapsed = true,
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
        readonly Signal<bool> _railDragging = new(false);
        /// <summary>True for <see cref="FrameRules.ChromeEdgeSnapMs"/> after the chrome mounts or unmounts: the content region
        /// then SNAPS instead of FLIPping (see <see cref="ContentRegionAnim"/>).</summary>
        readonly Signal<bool> _chromeEdge = new(false);
        /// <summary>A module watch page's stage would host what is playing — split from the resize-rate effect so the
        /// playable-uri reads run at navigation/track rate, not per resize pixel.</summary>
        readonly Signal<bool> _pageStageHosts = new(false);

        bool _bandSeeded;
        bool _chromeWas = s_chromeMounted();
        TemplateParts? _seamParts;
        TemplateParts? _railSeamParts;
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
            var overlay = UseContext(Overlay.Service);
            _seamParts ??= BuildSeamParts(overlay);
            _railSeamParts ??= BuildRailSeamParts(_railDragging);
            // Float the engine's toast lane above the docked player bar (the idempotent registration idiom).
            Toast.EdgeInset = Design.Size.PlayerBarH;
            var vp = UseContextSignal(Viewport.Size);
            var zoom = UseContextSignal(Viewport.Zoom);

            // The window band's FIRST value is known only here; seed it before the column mounts so a small launch lays out as
            // a rail (or no pane) on frame one (no subscriber reads it yet).
            if (!_bandSeeded)
            {
                _bandSeeded = true;
                float w0 = vp.Peek().Width;
                var band0 = SidebarPaneModeRules.BandOf(w0, SidebarWindowBand.Wide);
                Sidebar.Band.SetIfChanged(band0);
                var mode0 = SidebarPaneModeRules.Resolve(band0, Sidebar.UserCollapsed.Peek(), Sidebar.Editing.Peek(),
                    paneHidden: Sidebar.NavStyle.Peek() == ShellNavStyle.Zune);
                Sidebar.Mode.SetIfChanged(mode0);
                Sidebar.PresentedWidth.SetIfChanged(SidebarPaneModeRules.PresentedWidth(mode0, Sidebar.Width.Peek(), w0));
                // The heads present the launch style from frame one: a Zune launch is hoisted without a Reflow.
                Ui.PresentedNavStyle.SetIfChanged(Sidebar.NavStyle.Peek());
                // The band's row 2 is right on frame one: Home opens at 84, never animating from 52.
                var boot = Current.Peek();
                Ui.PresentedSubRow.SetIfChanged(ZuneNavRules.SubRowOf(in boot));
                // The page gutter is right on frame one: a narrow launch must not mount at 36 and step a frame later.
                Ui.PageGutter.SetIfChanged(PageGeometry.GutterFor(FrameRules.CardWidth(w0, Sidebar.PresentedWidth.Peek(),
                    FrameRules.RailGapWidth(Ui.RailOpen.Peek(), Ui.RailFits.Peek()),
                    FrameRules.RailReservedWidth(Ui.RailOpen.Peek(), Ui.RailFits.Peek(), Ui.RailWidth.Peek()))));
            }

            // THE WINDOW BAND (design V.1): hysteretic, derived — the ONLY way the window width reaches the sidebar.
            UseSignalEffect(() =>
            {
                var current = Sidebar.Band.Peek();
                var next = SidebarPaneModeRules.BandOf(vp.Value.Width, current);
                if (next == current) return;
                Sidebar.Band.Value = next;
                if (!SidebarPaneModeRules.HasOverlay(next)) Sidebar.OverlayOpen.SetIfChanged(false);
            });
            // The title bar's pane toggle is disabled while the sidebar is being edited (a plain signal mirrored here). Under
            // Zune it is not disabled but ABSENT: ChromeContentVersion hides it through TitleBar.ShowPaneToggle.
            UseSignalEffect(static () => s_paneToggleEnabled.SetIfChanged(!Sidebar.Editing.Value));
            // THE ONE PRESENTATION EFFECT: ① the mode, ② the presented width. A live drag presents Track(seam). It never
            // writes Sidebar.Width or UserCollapsed (those are the preference, persisted at commit).
            UseSignalEffect(() =>
            {
                float vpW = vp.Value.Width;
                var band = Sidebar.Band.Value;
                bool editing = Sidebar.Editing.Value;
                var state = new SidebarResizeRules.State(Sidebar.UserCollapsed.Value, Sidebar.Width.Value);
                bool hidden = Sidebar.NavStyle.Value == ShellNavStyle.Zune;
                if (_sidebarDragging.Value && SidebarPaneModeRules.SeamVisible(band) && !hidden)
                {
                    var live = SidebarResizeRules.Track(Sidebar.Seam.Value, in state, editing);
                    Sidebar.Mode.SetIfChanged(live.Collapsed ? SidebarPaneMode.Compact : SidebarPaneMode.Expanded);
                    Sidebar.PresentedWidth.SetIfChanged(live.PresentedWidth);
                    _sidebarFade.SetIfChanged(live.Fade);
                    return;
                }
                _sidebarFade.SetIfChanged(1f);
                var mode = SidebarPaneModeRules.Resolve(band, state.UserCollapsed, editing, paneHidden: hidden);
                Sidebar.Mode.SetIfChanged(mode);
                Sidebar.PresentedWidth.SetIfChanged(SidebarPaneModeRules.PresentedWidth(mode, state.ExpandedWidth, vpW));
            });
            // THE PAGE GUTTER: one effect, reading exactly what the content card's width reads (the same signals, the same
            // FrameRules), so the gutter steps in the SAME flush as the card and its FLIP covers the step. Not
            // OnBoundsChanged: that fires after layout and would re-lay the page a frame late. Hysteretic against its own
            // last value (PageGeometry.GutterFor), so a resize near 600 / 880 cannot flicker.
            UseSignalEffect(() =>
            {
                float cardW = PageColumnWidth(vp.Value.Width);
                Ui.PageGutter.SetIfChanged(PageGeometry.GutterFor(cardW, Ui.PageGutter.Peek()));
            });
            // The chrome's mount edge (full-screen video, immersive lyrics) is NOT a nav-style switch: the collapsed region is
            // arranged 0x0 at the cursor, so on the way back ContentRegionAnim would see a Y/H change and fly the whole card
            // in from the window top. The edge latches the same layout-transition suppression a seam drag uses, for the
            // commit that carries it (the latch is written in the flush, before the layout and ApplyProjections).
            var chromeEdgeEnd = UseTimeout(() => _chromeEdge.Value = false, FrameRules.ChromeEdgeSnapMs);
            UseSignalEffect(() =>
            {
                bool mounted = s_chromeMounted();
                if (mounted == _chromeWas) return;
                _chromeWas = mounted;
                _chromeEdge.Value = true;
                chromeEdgeEnd.RestartIn(FrameRules.ChromeEdgeSnapMs);
            });
            UseSignalEffect(() => FgMotion.SetLayoutTransitionsSuppressed(
                MotionSuppressionSource.AppResize, _sidebarDragging.Value || _chromeEdge.Value));
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
                };
            }, DepKey.Empty);

            // ── the published derived facts ──
            UseSignalEffect(static () =>
                Auth.SetIfChanged(FoldAuth(Spotify.Status.Value, Spotify.Fault.Value, Platform.Args.Fake || Platform.HasStoredCredential())));   // --fake presents its seeded account as signed in (G-065)
            UseSignalEffect(() => _pageStageHosts.SetIfChanged(
                Video.DockedHosting.PageStageHosts(Ui.ActiveStagePlayable.Value, Playback.CurrentId.Value.Text)));
            UseSignalEffect(() =>
            {
                Ui.RailFits.SetIfChanged(Ui.CanFitRail(vp.Value.Width, Sidebar.PresentedWidth.Value, Ui.RailWidth.Value));
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
                // The identity chip's FORM is a budget input, not a decoration: "Connecting" and "Reconnect" are 58-68
                // DIP wider than the avatar, well past the 16-DIP gutter cushion (#88). Reading it here also subscribes
                // this effect to the auth fold, so a sign-in or a resume RE-ALLOCATES the row instead of overflowing it.
                // Zune: no Forward in the budget (it is not drawn) and a compact, right-aligned search at its minimum.
                bool zune = Sidebar.NavStyle.Value == ShellNavStyle.Zune;
                var next = Chrome.Resolve(w, extent, old, FrameRules.ChipFor(Auth.Value), zune: zune, compactSearch: zune);
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
                Direction = 1, Grow = 1f, Height = Prop.Of(() => vp.Value.Height), MorphId = FrameColumnMorphId,
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
                    Embed.Comp(static () => new NavStylePresenter()) with { Key = "shell:navstyle-presenter" },
                    Embed.Comp(static () => new SubRowPresenter()) with { Key = "shell:subrow-presenter" },
                    Embed.Comp(static () => new BleedPresenter()) with { Key = "shell:bleed-presenter" },
                    Embed.Comp(static () => new CardPoseTracker()) with { Key = "shell:card-pose-tracker" },
                    // FULL-SCREEN VIDEO COLLAPSES THE CHROME, it does not unmount it: every layer left under the video
                    // costs GPU, and the docked bar is what stacked a second transport under the video's own, but a
                    // structural Flow.Show rebuilt the tab row and the whole player bar (seek rail, marquee, art, device
                    // buttons) on every exit. A bound Visible takes each out of layout, paint, hit-test and focus and
                    // pauses its subtree (UseInterval, UseIsActive), and the edge never re-renders the frame.
                    ChromeRow() with { Visible = Prop.Of(s_chromeMounted) },
                    ContentRegion(vp),
                    PlayerBarDock() with { Visible = Prop.Of(s_chromeMounted) },
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
                // The video layers come BEFORE the in-stack overlays: the hole erases whatever is drawn under it, so the
                // runtime banner, the toasts and the drop pill must be painted after it to stay over a floating or
                // fullscreen picture (every one of them is pass-through, so the mini player's drag still lands).
                Video.PipLayer(),
                Video.FullscreenLayer(),
                Overlays(),
                FileDropLayer(),
                CommandPalette(),
                Embed.Comp(static () => new CoverScrim()),
                DragPreviewLayer.Of(Drag.Preview),
                Diagnostics.FpsOverlay()) with { Grow = 1f };

            // HomeLayout (F29): one app-wide document signal, provided at the shell root beside the material cell so
            // HomeScreen and CustomizeScreen both read it and a customize edit is visible on Home the same frame.
            return Ctx.Provide(HomeLayout.Slot, HomeLayout.Instance,
                Ctx.Provide(ShellMaterial.Slot, MaterialState, OverlayHost.Create(stack)));
        }

        Element ContentRegion(IReadSignal<Size2> vp) => ZStack(
            new BoxEl
            {
                Direction = 0, Grow = 1f, ClipToBounds = true,
                Children =
                [
                    // Zero-size: the content card's X anchor (see ContentCardAnchor). First, so it sits at the row's left edge.
                    Embed.Comp(static () => new ContentCardAnchor()),
                    new BoxEl
                    {
                        // THE W12 TRAP lives in FrameRules.SidebarPaneWidth: a drag peek presents the pane expanded, so this
                        // clipped column widens with it.
                        Direction = 1, Shrink = 0f, ClipToBounds = true,
                        OnRealized = static h => s_sidebarColumn = h,
                        Width = Prop.Of(static () => FrameRules.SidebarPaneWidth(
                            Sidebar.DragPeek.Value, Sidebar.Width.Value, Sidebar.PresentedWidth.Value)),
                        Animate = SidebarPaneAnim,
                        Children =
                        [
                            // The layout firewall for the whole sidebar, INSIDE the animating pane. Minimal presents the
                            // column at 0 DIP and the drawer owns the sidebar, so the docked pane unmounts: a mounted one
                            // would keep planning the full list and leave its rows reachable by Tab inside the clipped column.
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, IsolateLayout = true, ClipToBounds = true,
                                Opacity = Prop.Of(() => _sidebarFade.Value),
                                Children = [Flow.Show(static () => Sidebar.Mode.Value != SidebarPaneMode.Minimal, Sidebar.Pane())],
                            },
                        ],
                    },
                    // The PAGE COLUMN: [the Zune band, the card stack]. The band is 0 tall outside Zune, so Classic/Library
                    // lay out exactly as before; under Zune the inline rail beside it runs from the title bar to the dock.
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                        Children =
                        [
                            Sidebar.ZuneBand() with { Visible = Prop.Of(s_chromeMounted) },
                            new BoxEl
                            {
                                // The stock Win11 content region: always flush (no left gap, ever); the one corner and the left+top stroke only while a pane is docked; no shadow.
                                Direction = 1, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
                                Animate = PageColumnCardAnim,
                                Children =
                                [
                                    Embed.Comp(static () => new CardGround()),
                                    new BoxEl
                                    {
                                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                                        Fill = ColorF.Transparent, Corners = Prop.Of(s_contentCorners), ClipToBounds = true,
                                        IsolateLayout = true, Animate = ContentCardAnim, RelativeTo = ContentCardAnchorId,
                                        OnRealized = static h => { s_contentCard = h; PublishCardRect(); },
                                        OnBoundsChanged = static _ => { PublishCardRect(); PublishScrimClip(); },
                                        Children = [Embed.Comp(static () => new ContentHost())],
                                    },
                                    Embed.Comp(static () => new CardStroke()),
                                ],
                            },
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
                Width = Prop.Of(static () => FrameRules.SidebarSeamWidth(SidebarPaneModeRules.SeamVisible(Sidebar.Band.Value)
                    && Sidebar.NavStyle.Value != ShellNavStyle.Zune)),
                Transform = Prop.Of(static () => Affine2D.Translation(Sidebar.PresentedWidth.Value, 0f)),
                Children =
                [
                    Splitter.Create(Sidebar.Seam, () => Sidebar.CommitSeam(), SidebarSeamOptions,
                        fade: _sidebarFade, dragging: _sidebarDragging, parts: _seamParts),
                ],
            },
            // The rail overlay: final width always; Rail.Frame owns the translate, the coats and the bodies.
            new BoxEl
            {
                Grow = 1f, Direction = 0, Justify = FlexJustify.End, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Shrink = 0f, ClipToBounds = true, HitTestPassThrough = true,
                        Width = Prop.Of(static () => Ui.RailWidth.Value),
                        Children =
                        [
                            // Inline, the rail starts at the title bar's bottom; the narrow overlay still starts under the Zune band.
                            Embed.Comp(static () => new RailOverlayTopSpacer()),
                            new BoxEl
                            {
                                // The card stack's idiom (PageColumnCardAnim): the body's top eases with the band's bottom edge as a
                                // parent-relative Y FLIP while a Height Relayout keeps its bottom on the dock. The spacer above is plain.
                                Grow = 1f, MinHeight = 0f, ZStack = true, HitTestPassThrough = true, Animate = PageColumnCardAnim,
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
                ],
            },
            // The rail seam: exists only while the rail is open. It paints AFTER (above) the rail overlay: the rail no longer
            // moves during a drag, so a narrowing drag carries the guide into the rail's rect, and it must stay visible over
            // the rail's content. At rest the strip lies wholly left of the rail, so hit-testing is unchanged.
            new BoxEl
            {
                Direction = 1, ClipToBounds = true,
                Width = Prop.Of(static () => FrameRules.RailSeamWidth(Ui.RailOpen.Value)),
                Transform = Prop.Of(() => Affine2D.Translation(FrameRules.RailSeamX(vp.Value.Width, Ui.RailDragWidth.Value), 0f)),
                Children =
                [
                    Splitter.Create(Ui.RailDragWidth, CommitRailDrag, RailSeamOptions, collapsed: Ui.RailOpen, fade: _railFade,
                        dragging: _railDragging, parts: _railSeamParts),
                ],
            },
            Embed.Comp(static () => new NarrowDrawer())) with
        {
            // The ONE region that yields when the window is shorter than the column; clipped so a settling page never
            // paints into the dock slot. The drag spotlight scrim is scoped to it, below the Zune band (chrome and dock stay lit).
            // The bound Visible COLLAPSES the whole shell body while the fullscreen stage OR full-screen video is up: out of
            // layout, paint and hit-test, and its subtree goes inactive (UseInterval pauses, UseActivation fires its edges as on a KeepAlive
            // tab switch — every consumer reviewed benign, V-U37). It sits on THIS node, which carries no MorphId: the
            // card's anchor (ContentCardAnchorId) is a separate node with an unbound Visible (the BindContract rule guards the
            // tagged node itself).
            Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
            Animate = ContentRegionAnim, RelativeTo = FrameColumnMorphId,
            Visible = Prop.Of(s_chromeMounted),
            OnRealized = static h => { s_contentRegion = h; PublishScrimClip(); },
            OnBoundsChanged = static _ => PublishScrimClip(),
        };
    }

    /// <summary>The content card's corners, bound to the pane mode: one thunk for the ground, the clip, the stroke and the
    /// cover-tone plane, so they change in the same commit.</summary>
    internal static readonly Func<CornerRadius4> s_contentCorners =
        static () => FrameRules.ContentCorners(FrameRules.PaneDocked(Sidebar.Mode.Value));

    static readonly Func<bool> s_chromeMounted = static () =>
        FrameRules.ChromeMounted(Video.PlacementCore.Resolve(Video.State.Surface.Value), Ui.ImmersiveLyrics.Value);

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

    // ══ 2.2 THE CARD'S GROUND AND STROKE (and the artist bleed's cut-out) ══════════════════════════════════════════════

    /// <summary>EXPERIMENTAL (artist bleed): how much of the card's top fill is gone at this moment, 0..1. The strip over the
    /// photo's extent and the stroke over it are both drawn at <c>1 - this</c>. 0 with no backdrop, so the ground is today's.</summary>
    static float BleedCut()
    {
        if (Ui.BleedBackdrop.Value is not { } b) return 0f;
        return Ui.BleedPresence.Value * ArtistBleed.HeroVisible(b.ScrollY.Value, b.CollapseDistance);
    }

    static readonly Func<float> s_bleedKeep = static () => 1f - BleedCut();

    /// <summary>The corners of the layers that sit over the photo (the ground and the stroke), WITH a backdrop: today's thunk with the
    /// top-left radius faded by <see cref="ArtistBleed.CornerFor"/>, so the notch the rounded corner would cut out of the photo
    /// fades with the hero region and returns as the hero scrolls away. Paint-rate (BleedCut reads the scroll and the presence).</summary>
    static readonly Func<CornerRadius4> s_bleedCorners = static () =>
    {
        var c = s_contentCorners();
        return new CornerRadius4(ArtistBleed.CornerFor(c.TopLeft, BleedCut()), c.TopRight, c.BottomRight, c.BottomLeft);
    };

    /// <summary>The photo's extent below the card top while a backdrop is published (or fading out), else 0. Read at navigation
    /// rate by <see cref="CardGround"/> and <see cref="CardStroke"/>, which re-render when the publication changes, never per
    /// scroll.</summary>
    static float BleedExtent() => Ui.BleedBackdrop.Value?.PhotoHeight ?? 0f;

    /// <summary>The content card's fill. With no backdrop it is the plain rounded fill, no clip and no extra nodes. With one it is
    /// a clip exactly the photo's extent tall over the plain fill below, and inside the clip the translucent fill is split at ONE
    /// line, the hero's presented bottom (<see cref="ArtistBleed.RiserTop"/>, a paint-only translation): a STRIP above it that
    /// fades out as the photo bleeds through (<see cref="BleedCut"/>), a RISER from it down that is always solid, and a short
    /// FADE above the line that is the photo's own bottom feather turned into fill (so the photo ends in the card's fill, not in
    /// a hard edge). The strip and the riser TILE the clip, so the translucent fill is never drawn twice. The card rect never
    /// changes; with no backdrop nothing here is translated or faded.</summary>
    sealed class CardGround : Component
    {
        public override Element Render()
        {
            float scale = UseContext(Viewport.Scale);   // the line is snapped to a device pixel (unconditional: hook order)
            float p = BleedExtent();
            if (p <= 0f)
                return new BoxEl { Grow = 1f, Fill = Prop.Of(static () => Design.Colors.FileArea), Corners = Prop.Of(s_contentCorners) };
            float band = ArtistHeroLayout.PhotoFadeBandFor(p);
            float Line() => Ui.BleedBackdrop.Value is { } b
                ? ArtistBleed.SnapToPixel(ArtistBleed.RiserTop(b.ScrollY.Value, b.HeroHeight, b.Floor, p), scale)
                : p;
            return new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true, HitTestVisible = false,
                Corners = Prop.Of(s_bleedCorners),
                Children =
                [
                    new BoxEl
                    {
                        Height = p, Shrink = 0f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                        Children =
                        [
                            new BoxEl
                            {
                                Height = p, AlignSelf = FlexAlign.Start, Fill = Prop.Of(static () => Design.Colors.FileArea),
                                Opacity = Prop.Of(s_bleedKeep),
                                Transform = Prop.Of(() => Affine2D.Translation(0f, ArtistBleed.StripShift(Line(), p))),
                            },
                            new BoxEl
                            {
                                Height = band, AlignSelf = FlexAlign.Start, Fill = Prop.Of(static () => Design.Colors.FileArea),
                                EdgeFade = new EdgeFadeSpec(EdgeMask.Top, band),
                                Opacity = Prop.Of(static () => BleedCut()),
                                Transform = Prop.Of(() => Affine2D.Translation(0f, Line() - band)),
                            },
                            new BoxEl
                            {
                                Height = p, AlignSelf = FlexAlign.Start, Fill = Prop.Of(static () => Design.Colors.FileArea),
                                Transform = Prop.Of(() => Affine2D.Translation(0f, Line())),
                            },
                        ],
                    },
                    new BoxEl { Grow = 1f, MinHeight = 0f, Fill = Prop.Of(static () => Design.Colors.FileArea) },
                ],
            };
        }
    }

    /// <summary>The card's left+top stroke. Without a backdrop it is the single plain ring. With one it is split by CLIPPING,
    /// never redrawn: a top clip (the photo's extent, faded with the strip) and a lower clip holding the same ring with its
    /// geometry still starting at the card top, so the union is exactly the plain ring and the left edge below the hero stays.
    /// THE NOTCH (option 1): the ring over the photo (a) and the ground use <see cref="s_bleedCorners"/>, whose top-left radius fades
    /// with the hero region (<see cref="ArtistBleed.CornerFor"/>), so the photo meets the window edge with no rounded notch and the
    /// stroke fades with it; the part below the hero (b) keeps today's corners, and the backdrop is not extended behind the pane.</summary>
    sealed class CardStroke : Component
    {
        static readonly Func<Affine2D> s_leftShift = static () => Affine2D.Translation(
            FrameRules.StrokeLeftShift(FrameRules.PaneDocked(Sidebar.Mode.Value)), 0f);

        /// <summary>The plain ring (one DIP of extra right overhang so the box can shift left: undocked, the page bleeds to the
        /// window edge and the LEFT stroke leaves the clip, the top stroke stays), with an overridable top margin.</summary>
        static BoxEl Ring(float top, bool bleeding = false) => new()
        {
            Margin = new Edges4(0f, top, -2f * FrameRules.StrokeW, -FrameRules.StrokeW), BorderWidth = FrameRules.StrokeW,
            BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
            Corners = Prop.Of(bleeding ? s_bleedCorners : s_contentCorners),
            Transform = Prop.Of(s_leftShift),
        };

        public override Element Render()
        {
            float p = BleedExtent();
            if (p <= 0f)
                return new BoxEl { ZStack = true, ClipToBounds = true, HitTestVisible = false, Children = [Ring(FrameRules.StrokeOverhangTop)] };
            return new BoxEl
            {
                ZStack = true, ClipToBounds = true, HitTestVisible = false,
                Children =
                [
                    // (a) over the photo: a clip exactly the photo's extent tall holding today's ring (card-tall: it overhangs the
                    //     clip's bottom by the card's own height), faded with the strip.
                    new BoxEl
                    {
                        Height = p, AlignSelf = FlexAlign.Start, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                        Opacity = Prop.Of(s_bleedKeep),
                        Children =
                        [
                            Ring(FrameRules.StrokeOverhangTop, bleeding: true) with
                            {
                                Margin = new Edges4(0f, FrameRules.StrokeOverhangTop, -2f * FrameRules.StrokeW, 0f),
                                AlignSelf = FlexAlign.Start,
                                Height = Prop.Of(static () => Ui.CardRect.Value.H + FrameRules.StrokeW),
                            },
                        ],
                    },
                    // (b) below the photo: the ring's geometry still starts at the card top (negative top margin), so only the part
                    //     below the hero shows.
                    new BoxEl
                    {
                        Margin = new Edges4(0f, p, 0f, 0f), ZStack = true, ClipToBounds = true, HitTestVisible = false,
                        Children = [Ring(FrameRules.StrokeOverhangTop - p)],
                    },
                ],
            };
        }
    }

    /// <summary>The content card's X anchor: a zero-size node in the content row, at the row's left edge (x never changes on a
    /// pane or rail toggle) and <see cref="ZuneNavRules.BandHeight"/> below the row's top (so it moves in y exactly as the card
    /// stack does when the band opens or closes). The card's FLIP is measured relative to it, so a toggle gives the card its X
    /// FLIP (pane, rail) and no Y FLIP: the Y move of the band is the card stack's own (<see cref="PageColumnCardAnim"/>), and
    /// two nested row-relative FLIPs would compose into double the distance. It reads the same two signals as the band, so both
    /// re-render in the same commit and the anchor never leads or trails the stack by a layout pass.</summary>
    sealed class ContentCardAnchor : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 0f, Shrink = 0f, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, HitTestVisible = false,
                    Height = ZuneNavRules.BandHeight(Sidebar.NavStyle.Value, Ui.PresentedSubRow.Value),
                },
                new BoxEl { MorphId = ContentCardAnchorId, Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false },
            ],
        };
    }

    /// <summary>The narrow rail overlay's top spacer: the band's height while the rail floats (it starts under the band), zero
    /// while it is inline (it starts at the title bar's bottom). A plain height, read from the same two signals as the band (so
    /// it steps in the band's commit) with NO transition of its own: the overlay body below it carries
    /// <see cref="PageColumnCardAnim"/>, so a navigation between a route with a row 2 and one without, or a nav-style switch,
    /// eases the overlay's top with the band's bottom edge without a Reflow (a live Reflow shoves every other node's position
    /// FLIP in the tree).</summary>
    sealed class RailOverlayTopSpacer : Component
    {
        public override Element Render() => new BoxEl
        {
            Shrink = 0f, HitTestVisible = false,
            Height = FrameRules.RailOverlayTop(Ui.RailFits.Value,
                ZuneNavRules.BandHeight(Sidebar.NavStyle.Value, Ui.PresentedSubRow.Value)),
        };
    }

    /// <summary>EXPERIMENTAL (artist bleed): the content card host's node, for its laid-out window rect.</summary>
    static NodeHandle s_contentCard;

    /// <summary>Publishes the card's LAID-OUT rect in window coordinates (the sum of the layout bounds up the parent chain, never
    /// the FLIP's in-flight pose), value-gated. The material layer derives the backdrop's final span from it. With a backdrop
    /// showing, a change also arms <see cref="CardPoseTracker"/>; without one the presented pose is simply the final rect.</summary>
    static void PublishCardRect()
    {
        if (s_scene is not { } scene || s_contentCard.IsNull || !scene.IsLive(s_contentCard)) return;
        float x = 0f, y = 0f;
        for (var n = s_contentCard; !n.IsNull; n = scene.Parent(n))
        {
            ref readonly var b = ref scene.Bounds(n);
            x += b.X;
            y += b.Y;
        }
        var own = scene.Bounds(s_contentCard);
        var rect = new RectF(x, y, own.W, own.H);
        bool moved = Ui.CardRect.SetIfChanged(rect);
        // Without a backdrop nobody draws the pose: keep it equal to the final rect, so a backdrop that appears later starts in
        // place. With one the tracker owns it (a FLIP starts at the OLD pose, which is exactly what stays until it samples).
        if (Ui.BleedBackdrop.Peek() is null) Ui.CardPose.SetIfChanged(rect);
        else if (moved) Ui.CardSettle.Value++;
    }

    /// <summary>The card's PRESENTED left/top (the layout transitions' in-flight translation included) into <see cref="Ui.CardPose"/>.</summary>
    static void SampleCardPose()
    {
        if (s_scene is not { } scene || s_contentCard.IsNull || !scene.IsLive(s_contentCard)) return;
        Ui.CardPose.SetIfChanged(scene.AbsoluteRect(s_contentCard));
    }

    /// <summary>EXPERIMENTAL (artist bleed): feeds <see cref="Ui.CardPose"/> for the length of the card's layout transition
    /// (<see cref="Ui.CardSettle"/> re-arms it on every change of the final rect), then lands on the settled pose. The ticker is
    /// mounted only inside that window, so an idle window pays nothing. Renders an empty, zero-size box.
    /// <para>TWO SOURCES. Under the Async host, sampling <c>AbsoluteRect</c> reads the PREVIOUS frame's layout, so the photo would
    /// trail the card. A pane, rail or nav-style toggle, and a change of the band's row 2 (<see cref="Ui.PresentedSubRow"/>, which
    /// eases the band's height under a navigation), run a known tween (the card's FLIP: <see cref="PaneMs"/> /
    /// <see cref="PaneEase"/>), so a settle ARMED by a live toggle plays that tween analytically
    /// (<see cref="ArtistBleed.PoseAt"/>) and the photo follows the card in the same frame. Everything else (a window resize, the
    /// sidebar drag peek, the rail drag commit, a snap) has no known curve and samples the real pose per tick. Every run lands on
    /// the sample.</para>
    /// <para>THE ARM is recorded in Render, not in an effect: the toggle's own commit re-renders this tracker (it reads the
    /// toggles) BEFORE the layout pass that bumps <see cref="Ui.CardSettle"/> and re-renders it again, so the arm is always
    /// recorded first. It reads the LIVE nav style, not <c>PresentedNavStyle</c>: the card's nav-style FLIP runs in the NavStyle
    /// commit, while PresentedNavStyle flips <c>HoistSettleMs</c> later. A settle consumes the arm.</para></summary>
    sealed class CardPoseTracker : Component
    {
        readonly Signal<bool> _running = new(false);
        readonly Action _tick;
        readonly Action _stop;
        bool _seen;
        SidebarPaneMode _mode;
        bool _railOpen;
        ShellNavStyle _navStyle;
        ZuneSubRow _subRow;
        SidebarWindowBand _band;
        long _bandT;               // when the window band last crossed (0: never)
        long _armT;               // when a live toggle last changed (0: none, or consumed by a settle)
        int _lastSettle;
        bool _tween;               // the current run plays the analytic pose
        RectF _from, _to;
        long _t0;

        public CardPoseTracker()
        {
            _tick = Tick;
            _stop = () => { SampleCardPose(); _running.Value = false; };
        }

        void Tick()
        {
            // Layout-transition suppression (a scroll starts it) cancels the in-flight FLIP and snaps the card without a rect
            // change, so the analytic pose would run on alone: fall through to the real pose.
            if (_tween && FgMotion.LayoutTransitionsSuppressed) _tween = false;
            if (_tween) Ui.CardPose.SetIfChanged(TweenPose());
            else SampleCardPose();
        }

        RectF TweenPose() => ArtistBleed.PoseAt(_from, _to, Design.FrameTime.NowMs - _t0, PaneMs, ArtistBleed.PaneEase);

        public override Element Render()
        {
            int settle = Ui.CardSettle.Value;       // layout-change rate, never per frame
            // The live toggles, all navigation rate. The first render only takes the snapshot (a toggle that predates the tracker
            // armed nothing).
            var mode = Sidebar.Mode.Value;
            bool railOpen = Ui.RailOpen.Value;
            var navStyle = Sidebar.NavStyle.Value;
            var subRow = Ui.PresentedSubRow.Value;
            var band = Sidebar.Band.Value;
            long now = Design.FrameTime.NowMs;
            // A window-band cross (a resize) also flips the mode, and the resize cancels the card's FLIP; the mode can trail the band
            // by a render, so the cross shadows arming for the arm window. A drag (or the chrome-edge latch) holds the layout
            // suppression, so the card snaps 1:1 and the sample is right. Neither is a toggle.
            if (_seen && band != _band) _bandT = now;
            bool toggled = _seen && (mode != _mode || railOpen != _railOpen || navStyle != _navStyle || subRow != _subRow);
            if (toggled && ArtistBleed.ArmsPose(FgMotion.LayoutTransitionsSuppressed, _bandT > 0 && now - _bandT <= ArtistBleed.ToggleArmWindowMs))
                _armT = now;
            (_seen, _mode, _railOpen, _navStyle, _subRow, _band) = (true, mode, railOpen, navStyle, subRow, band);
            if (settle != _lastSettle)
            {
                _lastSettle = settle;
                var rect = Ui.CardRect.Peek();
                bool armed = !Design.Reduced && ArtistBleed.TweensPose(_armT > 0, now - _armT);
                _armT = 0;                          // consumed: a later settle (a resize) samples
                if (armed)
                {
                    _tween = true;
                    _from = Ui.CardPose.Peek();
                    _to = rect;
                    _t0 = now;
                }
                // The nav-style FLIP's height relayout re-arranges the card (H only) on every frame of the tween, one settle each:
                // those keep the running tween and only move its target height.
                else if (_tween && now - _t0 < PaneMs && ArtistBleed.ContinuesTween(_to, rect)) _to = rect;
                else _tween = false;
            }
            bool running = _running.Value;
            // The ticker mounts on the render AFTER this effect: write the run's first analytic pose here, so the photo does not
            // trail the card's first (front-loaded) frames.
            UseEffect(() =>
            {
                if (settle <= 0) return;
                if (_tween) Ui.CardPose.SetIfChanged(TweenPose());
                _running.Value = true;
            }, DepKey.From(settle));
            UseTimeout(_stop, PaneMs + 120f, DepKey.From(settle));
            return new BoxEl
            {
                Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                Children = running ? [Embed.Comp(() => new Controls.FrameTicker(_tick))] : [],
            };
        }
    }

    /// <summary>EXPERIMENTAL (artist bleed): follows "a backdrop is published" into <see cref="Ui.BleedPresence"/> with a tween of
    /// <c>Design.Motion.Standard</c> (a snap under reduced motion), then runs <see cref="Ui.BleedHandover"/> over a second
    /// Standard so the page's own photo and veil ease out over the shell's identical ones, and retains the last backdrop in
    /// <see cref="Ui.BleedBackdrop"/> until the fade-out ends so the card ground keeps its geometry meanwhile. The per-frame
    /// ticker is mounted only while the tween runs. Renders an empty, zero-size box.</summary>
    sealed class BleedPresenter : Component
    {
        readonly Signal<bool> _running = new(false);
        readonly Action _step;
        float _from, _target;
        long _t0;
        string? _lastKey;

        public BleedPresenter() => _step = Step;

        public override Element Render()
        {
            var backdrop = MaterialState.Value.Backdrop;   // navigation rate
            float target = backdrop is null ? 0f : 1f;
            bool running = _running.Value;
            UseEffect(() =>
            {
                if (backdrop is not null && !ReferenceEquals(Ui.BleedBackdrop.Peek(), backdrop))
                {
                    if (Ui.BleedBackdrop.Peek() is null) SampleCardPose();   // the pose was parked at the final rect; start from the presented one
                    Ui.BleedBackdrop.Value = backdrop;
                }
                // A different artist published while the bleed is already fully present restarts the hand-over (the new page's own
                // photo draws again until the shell's new one is in), without touching the presence.
                bool republished = backdrop is not null && !string.Equals(_lastKey, backdrop.Key, StringComparison.Ordinal);
                _lastKey = backdrop?.Key;
                if (target == _target && !republished) return;
                _from = Ui.BleedPresence.Peek();
                _target = target;
                _t0 = Design.FrameTime.NowMs;
                Ui.BleedHandover.Value = 0f;
                if (Design.Reduced) Land();
                else _running.Value = true;
            }, DepKey.From(HashCode.Combine(target, backdrop)));
            return new BoxEl
            {
                Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                Children = running ? [Embed.Comp(() => new Controls.FrameTicker(_step))] : [],
            };
        }

        void Step()
        {
            float elapsed = Design.FrameTime.NowMs - _t0;
            float t = Math.Clamp(elapsed / Design.Motion.Standard, 0f, 1f);
            Ui.BleedPresence.Value = _from + (_target - _from) * Easings.Ease(Easing.FluentStandard, t);
            if (_target == 0f)
            {
                if (t >= 1f) Land();
                return;
            }
            // The hand-over starts only once the shell's layers are fully present.
            float h = Math.Clamp((elapsed - Design.Motion.Standard) / Design.Motion.Standard, 0f, 1f);
            Ui.BleedHandover.Value = Easings.Ease(Easing.FluentStandard, h);
            if (h >= 1f) Land();
        }

        void Land()
        {
            Ui.BleedPresence.Value = _target;
            Ui.BleedHandover.Value = _target;
            if (_target == 0f) Ui.BleedBackdrop.Value = null;
            _running.Value = false;
        }
    }

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
            Sidebar.Editing.Peek(), Sidebar.SidebarMenus.LockingNames(), Sidebar.ClassicCovers.Peek(),
            zune: Sidebar.NavStyle.Peek() == ShellNavStyle.Zune, zunePins: Sidebar.ZunePins.Peek())));
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
            case Keys.Home: Sidebar.SetExpandedWidth(SidebarResizeRules.ExpandedMinW); break;
            case Keys.End: Sidebar.SetExpandedWidth(SidebarResizeRules.ExpandedMaxW); break;
            case Keys.Enter or Keys.Space: Sidebar.TogglePane(); break;
            default: return;
        }
        e.Handled = true;
        Announcer.Say(Sidebar.UserCollapsed.Peek()
            ? Loc.Get("sidebar.seam.announceCollapsed")
            : Loc.Format("sidebar.seam.announceWidth", ("width", (int)Sidebar.Width.Peek())));
    }

    /// <summary>F243: the drag's release. The seam wrote only <c>Ui.RailDragWidth</c>; the rail's layout width and
    /// the persisted setting change here, once, so the page reflows and the PlayReady stream resizes once per drag.</summary>
    static void CommitRailDrag()
    {
        if (FrameRules.RailDragCommit(Ui.RailDragWidth.Peek(), Ui.RailWidth.Peek()) is not { } w) return;
        Ui.RailWidth.Value = w;
        Ui.RailDragWidth.Value = w;
        Platform.Settings.Set(Platform.Keys.ShellRailWidth, w);
    }

    /// <summary>F11 — closes the fullscreen stage if it is up; otherwise exits / enters video fullscreen; otherwise opens
    /// the stage (an empty stage is allowed; <c>Stage.Open</c> itself refuses over fullscreen video). With focus inside a
    /// player the element consumes F11 first (focused routing precedes accelerators), so exactly one of them fires.</summary>
    static void ToggleVideoFullscreen()
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
            TogglePlayPause("space");
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
            Sidebar.UserCollapsed.Peek(), Sidebar.OverlayOpen.Peek(), Sidebar.Width.Peek(), Sidebar.PresentedWidth.Peek(), rendered,
            PaneHidden: Sidebar.NavStyle.Peek() == ShellNavStyle.Zune);
    }

    static void PublishScrimClip()
    {
        if (s_scene is not { } scene || s_contentRegion.IsNull || !scene.IsLive(s_contentRegion)) return;
        RectF r = scene.AbsoluteRect(s_contentRegion);
        // The region's page column holds the Zune band, and chrome stays lit: the scrim starts at the card stack's (laid-out) top.
        // The clip is ONE rect across the region's width, so under Zune the inline rail's top (its header, tabs and gear, level
        // with the band) stays lit while the rest of the rail dims: the rail's header is chrome too.
        float top = MathF.Max(r.Y, Ui.CardRect.Peek().Y);
        if (top > r.Y && top < r.Y + r.H) r = new RectF(r.X, top, r.W, r.H - (top - r.Y));
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

    // ══ 2b. THE NAV-STYLE PRESENTER ═══════════════════════════════════════════════════════════════════════════════════
    //
    // A nav-style switch is TWO commits by construction. Commit 1 (Sidebar.NavStyle) moves only the frame: the pane, the
    // Zune band and the gutter, and the content card FLIPs once. Commit 2, FrameRules.HoistSettleMs later
    // (Ui.PresentedNavStyle), changes only what is INSIDE the card: the page heads' heights (which Reflow) and the Zune
    // band's row-2 words. The card's rect does not change there, so the engine's descendant suppression (the card is a
    // suppression root whenever its rect changes) cannot snap the heads' Reflow.

    /// <summary>Follows <see cref="Sidebar.NavStyle"/> into <see cref="Ui.PresentedNavStyle"/> once the frame has settled. The
    /// only writer besides the boot seed. Renders an empty, zero-size box; a double switch re-arms the timer, so only the
    /// last style presents.</summary>
    sealed class NavStylePresenter : Component
    {
        readonly Action _present = static () => Ui.PresentedNavStyle.SetIfChanged(Sidebar.NavStyle.Peek());

        public override Element Render()
        {
            var s = Sidebar.NavStyle.Value;
            // The frame-clock idiom (Album.Page.cs, Artist.UI.cs): UseTimeout arms from mount and re-arms when its deps
            // change. The mount-time fire is a no-op because the value is already seeded.
            UseTimeout(_present, FrameRules.HoistSettleMs, DepKey.From((int)s));
            return new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };
        }
    }

    /// <summary>Follows the route (and the nav style) into <see cref="Ui.PresentedSubRow"/>, the band's row-2 kind. On a route
    /// change it POSTS the write (<c>UsePost</c> runs after the commit in which the incoming page mounted), so the band's height
    /// change and the card's move never share a commit with the page's own mount, its entrance or its head's Reflow. On a style
    /// change it writes at once. The boot value is seeded by <c>FrameRoot</c>. Renders an empty, zero-size box.</summary>
    sealed class SubRowPresenter : Component
    {
        public override Element Render()
        {
            var post = UsePost();
            // A style change writes at once (a signal write belongs in an effect, never in a render).
            UseSignalEffect(static () =>
            {
                _ = Sidebar.NavStyle.Value;
                var now = Current.Peek();
                Ui.PresentedSubRow.SetIfChanged(ZuneNavRules.SubRowOf(in now));
            });
            var route = Current.Value;
            if (ZuneNavRules.SubRowOf(in route) != Ui.PresentedSubRow.Peek())
            {
                // Resolve the row again at fire time: a second navigation before the post lands presents the LAST route's row.
                post(static () =>
                {
                    var now = Current.Peek();
                    Ui.PresentedSubRow.SetIfChanged(ZuneNavRules.SubRowOf(in now));
                });
            }
            return new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };
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
        // Home→Home, same tab (remediation §3.10/W0): SlotKey already ignores Arg, so this is a facet switch inside
        // the SAME mounted HomeScreen, never a page swap — the facet content root's own keyed Exit/Enter (F13) plays
        // the swap. A page transition here would double up on top of it (and, worse, replay a page entrance every
        // time the facet pivot is clicked).
        if (oldToken is Route prevHome && prevHome.Kind == RouteKind.Home && next.Kind == RouteKind.Home && prevHome.Tab == next.Tab)
            return null;
        // Facet→facet of one artist's discography or one user's list, same tab: SlotKey and PageKey both ignore the facet, so
        // this is the SAME mounted page re-binding in place (the views pill slides, the body re-skeletons). A page
        // entrance here would replay the whole page for a pill click.
        if (oldToken is Route prevFacet && FrameRules.IsFacetPage(next.Kind) && prevFacet.Kind == next.Kind && prevFacet.Tab == next.Tab
            && string.Equals(FrameRules.PageKeyOf(prevFacet), FrameRules.PageKeyOf(next), StringComparison.Ordinal))
            return null;
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
                return Ctx.Provide(PageScrollScope, ScrollScopeOf(route.Tab), PageBox("page:" + FrameRules.PageKeyOf(route), factory!(route)));
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
    /// NOT "coming soon": say so under the shared page head (Title, 120) and give the one action that always works.</summary>
    static Element NotFoundPage(in Route route)
    {
        string key = NameOf(route) + "|" + (ArgOf(route) ?? "");
        if (s_warnedUnknown.Add(key)) Log.Warn("nav", "route.unknown: " + key);   // once per key per process
        float g = Ui.PageGutter.Value;
        return new BoxEl
        {
            Key = "page-notfound:" + key,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
            Children =
            [
                PageHead.Create(new PageHeadSpec(Loc.Get(Strings.Nav.PageNotFound)) { Gutter = g, Key = "notfound:head" }),
                new BoxEl
                {
                    Direction = 1, Padding = new Edges4(g, 0f, g, 0f), AlignItems = FlexAlign.Start,
                    Children = [Button.Standard(Loc.Get(Strings.Nav.GoHome), static () => GoTo(new Route(RouteKind.Home)))],
                },
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
    static IconButton.Style ChromeButtonStyle => Ui.ChromeArmsOnMedia.Value
        // CHROME INK (artist bleed): the rest glyph is the bound ink (ChromeGlyphParts); the static hover and pressed arms go light
        // while the mix is past one half. A once-per-crossing read, folded into ChromeContentVersion so the bar's memo follows it.
        ? IconButton.DefaultStyle with
        {
            Size = 40f, Height = 44f,
            HoverFill = Design.OnMedia.GlassHover, PressedFill = Design.OnMedia.GlassPressed,
            HoverForeground = Design.OnMedia.Ink, PressedForeground = Design.OnMedia.InkSecondary,
        }
        : IconButton.DefaultStyle with { Size = 40f, Height = 44f };

    /// <summary>The chrome buttons' rest glyph ink: a paint-rate bind of <see cref="Ui.ChromeInkPrimary"/> (the style's own rest
    /// colour, so a mix of 0 is exactly today's) while a backdrop shows; with none the part leaves the glyph's static colour
    /// alone. One instance: the modifier is static, and re-runs wherever the button is built.</summary>
    static readonly TemplateParts ChromeGlyphParts = MakeChromeGlyphParts();

    static TemplateParts MakeChromeGlyphParts()
    {
        var p = new TemplateParts();
        p.Set<TextEl>(IconButton.PartGlyph, static g => Ui.ChromeOnMedia ? g with { Color = Prop.Of(Ui.ChromeInkPrimary) } : g);   // ink: see Ui.ChromeInkMix
        return p;
    }

    /// <summary>2 DIP either side — what the bar gives its own pane toggle.</summary>
    static readonly Edges4 ChromeButtonMargin = new(2f, 0f, 2f, 0f);

    /// <summary>The strip's natural content extent (quantised), fed back into the chrome allocator.</summary>
    static readonly Signal<float> s_tabExtent = new(Layout.ChromeTabMinW);

    static TabStrip? s_strip;

    /// <summary>The mounted title bar, so <see cref="ChromeContentVersion"/> can set its public <c>ShowPaneToggle</c> before the
    /// bar reads it.</summary>
    static TitleBar? s_titleBar;

    /// <summary>The chrome islands' fade: the wordmark, the tab strip and the compact search cross-fade in place. The chrome
    /// row is a SIBLING of the content card, not a descendant, so the card's descendant suppression never touches these.</summary>
    static readonly MotionTokenDef s_chromeFade =
        MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    /// <summary>The title bar's pane toggle reads this for its enabled state (see <see cref="PaneToggleTip"/>). A plain
    /// signal mirrored by the frame component, so the bar's live binding needs no runtime at construction.</summary>
    static readonly Signal<bool> s_paneToggleEnabled = new(true);

    static string PaneToggleTip()
        => Sidebar.Editing.Value ? Loc.Get("sidebar.pane.finishEditing")
         : Sidebar.Mode.Value == SidebarPaneMode.Expanded || Sidebar.OverlayOpen.Value ? Loc.Get("sidebar.pane.collapse")
         : Loc.Get("sidebar.pane.expand");

    static readonly LayoutTransition TabLaneMotion = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(FgMotion.ControlFast, Easing.FluentPopOpen));

    static Element ChromeRow() => Embed.Comp(static () =>
    {
        var bar = new TitleBar
        {
            IconGlyph = "", ShowBackButton = false, ShowCaptionButtons = true,
            ShowPaneToggle = Sidebar.NavStyle.Peek() != ShellNavStyle.Zune, OnPaneToggle = Sidebar.TogglePane,
            PaneToggleEnabledSignal = s_paneToggleEnabled, PaneToggleToolTip = PaneToggleTip,
            ShowRailBaseline = false,          // the seam below is the content region's own stroke
            CaptionInk = Ui.ChromeInkPrimary,  // ink: see Ui.ChromeInkMix (the caption glyphs ride the same mix)
            Tabs = TabsIsland, TabsVersion = TitleBarTabsVersion,
            TabsElasticLane = true,            // tabs absorb the overrun; the omnibar keeps its allocation
            Trailing = TrailingIsland, CaptionLeading = CaptionLeadingIsland,
            ContentVersion = ChromeContentVersion,
        };
        bar.CenterContent = _ => CenterIsland(bar.CenterAvail);
        s_titleBar = bar;
        return bar;
    });

    static int ChromeContentVersion()
    {
        var l = ChromeLayout.Value;
        // Zune presents no pane, so its toggle is ABSENT (no button, no client region), not disabled. TitleBar.Render calls this
        // before it reads ShowPaneToggle for its memo key and its region deps, so setting the field HERE lands in the same
        // render that re-pushes the drag regions (no ordering dependency on a signal effect), and bit 128 below makes the
        // bar re-render and re-push when the style flips.
        bool zune = Sidebar.NavStyle.Value == ShellNavStyle.Zune;
        if (s_titleBar is { } bar) bar.ShowPaneToggle = !zune;
        int flags = (l.ShowName ? 1 : 0) | (l.ShowActions ? 2 : 0) | (l.ShowForward ? 4 : 0)
                  | (l.SearchMode == MergedSearchMode.Icon ? 8 : 0) | (l.ShowBack ? 16 : 0)
                  | (l.ShowNewTab ? 32 : 0) | (l.ShowTrailing ? 64 : 0) | (zune ? 128 : 0)
                  | (Ui.ChromeOnMedia ? 256 : 0)           // the rest ink is bound (ChromeGlyphParts, the wordmark, the tab labels)
                  | (Ui.ChromeArmsOnMedia.Value ? 512 : 0); // the static hover/pressed arms (ChromeButtonStyle)
        var r = Current.Value;
        // The route and the pin store's version: the trailing Pin/Unpin follows a navigation and a pin change.
        // l.Chip is the form the trailing island actually BUILDS from (AuthChip(l.Chip)); Auth.Value alone is not enough:
        // the allocator effect publishes the new Chip after Auth flips, so the bar can render once with the stale chip
        // and the version must move again when ChromeLayout lands, or TitleBar's render memo returns the stale tree.
        // s_searchSettle: the pill's width eases on focus, and the regions must be pushed once it has SETTLED (F5).
        return HashCode.Combine(flags, (int)l.SearchWidth, (int)l.LeadClusterW, TitleBarTabsVersion(), (int)Auth.Value,
            HashCode.Combine(r.Kind, r.Subject, r.Arg), Sidebar.PinsVersion.Value, HashCode.Combine((int)l.Chip, s_searchSettle.Value));
    }

    // Both fold Shown: the strip's labels come from the staged route, so it must rebuild when that route LANDS (the
    // exit leg after the commit), not only when the workspace or the selection changes.
    // The nav style and the tab count ride along: the lane swaps the strip for the wordmark at one tab under Zune, and a
    // hidden pane toggle shifts the lane, so the bar must re-render and re-push its regions on either.
    static int TitleBarTabsVersion() => HashCode.Combine(TabsVersion.Value, SelectedTab.Value, Shown.Value,
        (int)Sidebar.NavStyle.Value, Tabs.Count);

    // The ink flags ride along: the labels' part modifier (ChromeTabParts) reads them while the strip renders.
    static int TabStripItemsVersion() => HashCode.Combine(TabsVersion.Value, ChromeLayout.Value.ShowNewTab, Shown.Value,
        (Ui.ChromeOnMedia ? 1 : 0) | (Ui.ChromeArmsOnMedia.Value ? 2 : 0));

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
        // One tab under Zune: the wordmark stands in for the strip. The lane keeps its key, its width and TabLaneMotion, so the
        // 1 to 2+ swap is a cross-fade of the two arms inside a lane whose width change (if any) slides; the exiting arm leaves
        // layout as an orphan layer, so the entering one is never pushed aside.
        if (FrameRules.ShowsWordmark(Sidebar.NavStyle.Value, Tabs.Count))
            kids.Add(Design.Type.Wordmark(Loc.Get(Strings.Shell.Wordmark)) with
            {
                // ink: see Ui.ChromeInkMix
                Key = "chrome-wordmark", Color = Ui.ChromePrimary, Margin = new Edges4(Spacing.S, 0f, 0f, 0f),
                Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_chromeFade,
            });
        else
            kids.Add(Embed.Comp(BuildTabStrip) with
            {
                Key = "chrome-tab-strip", Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_chromeFade,
            });
        return new BoxEl
        {
            Key = "chrome-tabs-lane", Animate = TabLaneMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Height = TitleBar.ExpandedHeight,
            Width = l.TabIslandWidth, Shrink = 0f, ClipToBounds = true, Children = kids.ToArray(),
        };
    }

    /// <summary>The tab labels' ink: while a backdrop shows, the rest colour is a paint-rate bind (selected = primary, the others
    /// secondary) and the static hover and pressed arms go light once the mix is past one half (a navigation-rate read of the tab
    /// strip's own render). The selected label is the one with weight; the text-first strip gives only it any.</summary>
    static readonly TemplateParts ChromeTabParts = MakeChromeTabParts();

    static TemplateParts MakeChromeTabParts()
    {
        var p = new TemplateParts();
        p.Set<TextEl>(TabStrip.PartTabLabel, static t =>
        {
            if (!Ui.ChromeOnMedia) return t;
            bool selected = t.Weight > 0;   // the TabStrip's weight rule: only the selected label carries any (TabStrip.cs, Text appearance)
            bool arms = Ui.ChromeArmsOnMedia.Value;
            // ink: see Ui.ChromeInkMix
            return t with
            {
                Color = Prop.Of(selected ? Ui.ChromeInkPrimary : Ui.ChromeInkSecondary),
                HoverColor = arms && t.HoverColor.A > 0f ? Design.OnMedia.Ink : t.HoverColor,
                PressedColor = arms && t.PressedColor.A > 0f ? (selected ? Design.OnMedia.Ink : Design.OnMedia.InkTertiary) : t.PressedColor,
            };
        });
        return p;
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
            Parts = ChromeTabParts,
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
        var kind = PinRowRule.Decide(hasStore: Sidebar.AccountKey.Length > 0, pinId, Sidebar.IsPinned(pinId));
        if (kind == PinRowKind.None || pinId is not { } id) return null;
        string title = route.Kind == RouteKind.Search ? Loc.Get(Strings.Nav.Search) : Dest(route).Title;
        return Actions.Menu.Pin(kind == PinRowKind.Unpin, () => PinDestination(id, title), () => UnpinDestination(id));
    }

    static void PinDestination(string pinId, string title)
    {
        var pin = new SidebarPin(pinId, SidebarPinId.KindOf(pinId), SidebarPinId.UriOf(pinId), title,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Sidebar.PinRecorded(pin, title.Length > 0 ? title : Sidebar.SidebarMenus.PinName(pinId));
    }

    static void UnpinDestination(string pinId) => Sidebar.UnpinRecorded(pinId, Sidebar.SidebarMenus.PinName(pinId));

    static Element ChromeButton(string glyph, Action onClick, string tooltip, string key)
        => ToolTip.Wrap(IconButton.Create(glyph, onClick, ChromeButtonStyle, parts: ChromeGlyphParts) with { Key = key, Margin = ChromeButtonMargin }, tooltip);

    static Element CaptionLeadingIsland()
    {
        // No theme toggle here any more: the profile menu's Theme row (Actions.ProfileRules.Rows) carries it in every nav style.
        var l = ChromeLayout.Value;
        return new BoxEl
        {
            Direction = 0, Height = TitleBar.ExpandedHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children = l.SearchMode == MergedSearchMode.Icon ? [SearchFlyoutButton()] : [],
        };
    }

    static Element TrailingIsland()
    {
        var l = ChromeLayout.Value;
        if (!l.ShowTrailing) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        var kids = new List<Element>(6);
        // Zune: the compact search sits right-aligned just before the identity chip (the centre column is empty there).
        if (Sidebar.NavStyle.Value == ShellNavStyle.Zune && l.SearchMode == MergedSearchMode.Field) kids.Add(ZuneSearchHost());
        kids.Add(AuthChip(l.Chip));
        // The ONE "actions in row" stage: bell, friends and pin enter together. Below it they fold into the profile menu
        // (pin simply drops — the tab menu still offers it).
        if (l.ActionsInRow)
        {
            kids.Add(Embed.Comp(static () => new BellButton()) with { Key = "chrome-bell" });
            kids.Add(ChromeButton(Icons.Friends, static () => Ui.Toggle(RailMode.Friends), Loc.Get(Strings.Shell.Friends), "chrome-friends"));
            kids.Add(PinButton());
        }
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Height = TitleBar.ExpandedHeight,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The identity form from the AUTH FOLD, never raw session status: a silent resume behind the cache-first
    /// shell is Connecting, and an actionable "Sign in" there would race it (ch 18 W17).
    /// <para>The form comes from the RESOLVED row (<c>Chrome.Chip</c>), not from <c>Auth</c> directly, for the same
    /// reason <see cref="PinButton"/> reserves its 44 DIP unconditionally: the element tree must never be a form the
    /// budget did not price. The allocator reads the same <see cref="FrameRules.ChipFor"/> fold, so the two can only
    /// ever agree. (#88)</para></summary>
    static Element AuthChip(FrameRules.ChipForm chip) => chip switch
    {
        FrameRules.ChipForm.Profile => ProfileChip() with { Key = "chrome-profile" },
        FrameRules.ChipForm.Connecting => new BoxEl
        {
            Key = "chrome-connecting", Height = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
            // ink: see Ui.ChromeInkMix
            Children = [Ui.ChromeOnMedia ? Caption(Loc.Get(Strings.Shell.Connecting)) with { Color = Prop.Of(Ui.ChromeInkSecondary) } : Caption(Loc.Get(Strings.Shell.Connecting)).Secondary()],
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
                    ChromeButtonStyle, isEnabled: canDo, parts: ChromeGlyphParts)
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
            // presentation mode (the Store shot): the account's unread count is account data, so no badge
            int unread = Diagnostics.StoreShot.Presenting.Value ? 0 : Notify.Unread.Value;

            void Toggle() => OpenNotificationPanel(overlay, () => anchor.Value, handle);

            var style = ChromeButtonStyle;
            var button = IconButton.Create(Icons.Bell, Toggle, style, parts: ChromeGlyphParts) with { OnRealized = h => anchor.Value = h };
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

    // ══ 5. THE OVERLAY PANE ════════════════════════════════════════════════════════════════════════════════════════════
    //
    // In the Narrow and Tiny bands the sidebar is an OVERLAY over the page: `Sidebar.Band` decides whether there is one and
    // `Sidebar.OverlayOpen` is the session's open flag. The band's forced mode and the user's collapse never reach it, so
    // the saved desktop preference is never overwritten. Always mounted while the window is narrow; open/close is
    // compositor-only.

    sealed class NarrowDrawer : Component
    {
        Func<int, bool>? _savedPreview;
        Func<int, bool>? _escapePreview;
        bool _installed;

        public override Element Render()
        {
            var band = Sidebar.Band.Value;
            bool overlay = SidebarPaneModeRules.HasOverlay(band) && Sidebar.NavStyle.Value != ShellNavStyle.Zune;
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

            // BUG E1 (perf): on a desktop-width window the overlay is unreachable, so mount NOTHING — no scrim, no second
            // `Sidebar.DrawerPane()` (a whole second PaneView planning the sidebar on every invalidation and a second
            // `PumpBinder` registration), no overlay PaneHost at all. Gated on the band ALONE, never on `Sidebar.OverlayOpen`
            // (see `NarrowDrawerMount.ShouldMount`): the pane must stay mounted for the ENTIRE time the shell is narrow, closed
            // included, or the first open after entering the narrow band would have nothing already-mounted for the pane's
            // open/close slide (`UseTransition`) to animate from. `PaneHost`'s `UseSignalEffect(PumpBinder)` is a
            // `SignalEffectCell` (`IDisposableCell`), so mounting/unmounting across the breakpoint cleanly registers/unregisters
            // the binder pump (verified in the engine's `RenderContext.RunAllCleanups`).
            // Peek, not Value: ShouldMount ignores the open flag entirely (see its own doc).
            if (!NarrowDrawerMount.ShouldMount(overlay, Sidebar.OverlayOpen.Peek()))
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

    sealed class DrawerScrim : Component
    {
        bool _mounted;

        public override Element Render()
        {
            // Pinned (editing in a forced band) keeps the pane open behind a non-dismissing 0.2 scrim that lets the page
            // behind it stay usable (Q16): no hit test, no click-to-close.
            bool pinned = SidebarPaneModeRules.OverlayPinned(Sidebar.Band.Value, Sidebar.Editing.Value);
            bool open = Sidebar.OverlayOpen.Value || pinned;
            float ms = Design.Reduced ? 0f : Design.Motion.Fast;
            float target = Layout.DrawerRestingOpacity(open);
            UseTransition(AnimChannel.Opacity, _mounted ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(open));
            _mounted = true;
            return new BoxEl
            {
                Grow = 1f,
                Fill = pinned ? new ColorF(0f, 0f, 0f, 0.2f) : ColorF.FromRgba(0, 0, 0, 0x33),
                Opacity = pinned ? 1f : target,
                HitTestVisible = open && !pinned, OnClick = pinned ? null : static () => Sidebar.OverlayOpen.Value = false,
            };
        }
    }

    sealed class DrawerPane : Component
    {
        bool _mounted;

        public override Element Render()
        {
            bool open = Sidebar.OverlayOpen.Value || SidebarPaneModeRules.OverlayPinned(Sidebar.Band.Value, Sidebar.Editing.Value);
            var vp = UseContextSignal(Viewport.Size);
            float width = SidebarPaneModeRules.OverlayWidth(Sidebar.Width.Peek(), vp.Peek().Width);
            float ms = Design.Reduced ? 0f : (open ? MotionTok.PaneOpen.DurationMs : MotionTok.PaneClose.DurationMs);
            float target = Layout.DrawerRestingTranslateX(open, width);
            UseTransition(AnimChannel.TranslateX, _mounted ? (open ? -width : 0f) : target, target, ms, Easing.FluentPane, DepKey.From(open));
            _mounted = true;

            return new BoxEl
            {
                Direction = 1, Shrink = 0f, Grow = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
                // The resting position stays coupled to the live width: a closed pane can grow while resizing inside the
                // band, and a stale static translation would expose the added strip.
                Transform = Prop.Of(() => Affine2D.Translation(Layout.DrawerRestingTranslateX(
                    Sidebar.OverlayOpen.Value || SidebarPaneModeRules.OverlayPinned(Sidebar.Band.Value, Sidebar.Editing.Value),
                    SidebarPaneModeRules.OverlayWidth(Sidebar.Width.Value, vp.Value.Width)), 0f)),
                Width = Prop.Of(() => SidebarPaneModeRules.OverlayWidth(Sidebar.Width.Value, vp.Value.Width)),
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

    static Element NotificationRow(Notification n, long now, Signal<string> expanded, string expandedId, IOverlayService? overlay) => n.Category switch
    {
        NotifyCategory.AppUpdate => NotificationCard("ntf:" + n.Id, UpdateRow(n.Update ?? AppUpdateSnapshot.Idle), n.IsUnread, null),
        NotifyCategory.Social => NotificationCard("ntf:" + n.Id, SocialRow(n, now), n.IsUnread, () => OpenSocial(n)),
        NotifyCategory.NewRelease => ReleaseCard(n, overlay),
        _ => ActivityCard(n, now, expanded, expandedId),
    };

    /// <summary>A new-release row: click opens it; an ALBUM release also answers a right-click with the one album menu
    /// (the cards' own — Play next, Add to queue, Save, Go to artist…). An episode release has no container menu.</summary>
    static Element ReleaseCard(Notification n, IOverlayService? overlay)
    {
        var card = NotificationCard("ntf:" + n.Id, ReleaseRow(n), n.IsUnread, () => OpenRelease(n));
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
                    global::Wavee.Design.Type.MicroMeta(s.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "%") with
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
                        global::Wavee.Design.Type.DenseTitle(title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
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
            global::Wavee.Design.Type.DenseMeta(SpotifyUpdates.CleanTitle(n.Title)) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2 },
        };
        if (n.TimestampMs > 0 && n.TimestampMs != NotifyRows.UpdatePin)
            text.Add(global::Wavee.Design.Type.MicroMeta(RelativeTime(now - n.TimestampMs)) with { Weight = 600, Color = Tok.TextTertiary });
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
                    global::Wavee.Design.Type.DenseTitle(n.Title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
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
            global::Wavee.Design.Type.MicroMeta(RelativeTime(now - n.TimestampMs)) with { Weight = 600, Color = Tok.TextTertiary, Shrink = 0f },
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
                        global::Wavee.Design.Type.DenseMeta(n.Title) with { Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Color = Tok.TextPrimary },
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
        Children = [global::Wavee.Design.Type.DenseTitle(message) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 300f }],
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
