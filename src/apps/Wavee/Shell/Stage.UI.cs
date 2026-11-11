// ── Shell/Stage.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// StageCtx (the stage's signals), SurfaceCore (fullscreen, focus scope, idle, accent, the track-key signal), PaneHost (which of Lyrics · Up next ·
// Artist is shown, under KeepAlive; the region and each pane's frame are `Stage.Pane`), the layer builders the structure's root places, the TeachingTip, Open/Close. The frame's PICTURE (the layer
// stack, the chrome's placement, the top bar, the transport card, the gallery, the options popover, the caption) is the structure's since
// T7a1: `Stage.Root` and its siblings; so are the layout layers since T7a2 (backdrop, hero photo, face, scrim, spectrum, card, identity,
// artist name, the Artist pane: `ViewKeys.Stage*`; their props and facts are `Stage.Layers.cs` and `Stage.Artist.cs`).
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 1600 lines (the three-line lyric caption grew it past the plan's 1250)
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §3, §4.10
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FULLSCREEN STAGE. The frame mounts `Stage.Root` (`StageRootProps.Mount` → `Stage.Mount`) while `Shell.Ui.ImmersiveLyrics` is up
// (ClassicFrame, inside the immersive layer's Flow.Show). Mount = enter: the window goes borderless-fullscreen (the fullscreen
// video layer's shape), the shell collapses its chrome and body (FrameModel.ChromeMounted → bound Visible on the chrome row, the content region and
// the dock), a focus scope takes the keys. Unmount = exit.
//
//   • ONE context instance of SIGNALS (`StageCtx`) for the stage's life; every layer is a component on the signals it reads,
//     so SurfaceCore's tree is static and a track / preference / layout change re-renders only what reads it.
//   • ONE layout signal (`Stage.Layout`), written only on a real change; every DIP comes from it.
//   • ONE cover node (`stage:cover`) morphs between the hero and the thumb with a Layout FLIP; the title column follows.
//   • The accent is PER SURFACE: `AccentSet.From(cover accent)` into control Styles; big fills bind `slab.Accent`, which the
//     clock cross-fades (a bound channel snaps — §1.8). `Tok.SetAccent` is never called.
//   • Idle is the engine's PlayerChromeVisibility on the host timer clock, one timer re-armed from NextWakeMs; chrome UNMOUNTS
//     through Flow.Show whose child BoxEls carry the Enter/Exit (never alpha 0); the cursor hides through SetCursorOverride;
//     a hairline progress bar shows while hidden.
//   • ZStack placement: vertical = AlignSelf, horizontal = JustifySelf (FlexLayout.cs:1279-1286). Full-bleed wrappers over
//     interactive content are HitTestPassThrough, never HitTestVisible = false (which prunes the subtree).
//   • …but ONLY on a DIRECT ZStack child. A Flow.Show boundary and a component are transparent ANCHORS: default flex COLUMNS
//     that mirror the child's AlignSelf/JustifySelf/size but never its Margin (Reconciler.MirrorParticipation). Inside one,
//     a child's AlignSelf is the HORIZONTAL cross axis and a root without Grow is only as tall as its content. So a Show's
//     child and a layer component's root are a full-bleed `Layer` (Stretch + Grow), and the placed box sits one level down.
//   • Preferences are read ONCE per epoch into the context's signals; no tick, bind thunk or child render touches Prefs.Stage.
//   • Nothing here decides: modes, aspect classes, demand, visuals are `Stage` (CORE) and `Visualizer` (CORE).
//   • THE SURFACE IS CORE, THE ROOT'S TREE IS THE STRUCTURE'S (T7a1). SurfaceCore keeps every hook, effect and machine; each render it
//     hands the structure's `Draw` a `StageRootView` (size, the wiring it put on the root box, the layers' predicates, the chrome
//     views' props, the builders of the layers T7a2/T7b still own) and mounts what comes back under the context provider.
//
// Mounted from other files: `Lyrics.StagePane(accent)` (the reading column), `Queue.StagePane()` (the Up next column: Queue.UI.cs, the `Queue.StagePane` view),
// `Stage.NowPlayingMenu` (installed by `Actions.InstallUi`), `Shell.SeekBar(onScrubbing:)`/`Shell.TimeText` (the bar's own seek hosts, which
// mount `Player.SeekRail` / `Player.TimeLabel` — never a fork),
// `Shell.PlayerKey`/`Shell.ScrubController.KeyStep` (the bar's key map and its keyboard seek ladder).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

using Wavee.Theming.Contracts;

using Ink = Wavee.Theming.Roles.StageInk;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Stage
{
    // ══ 0. MOUNT POINT + SEAMS ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The fullscreen stage's core surface, drawing its root with <paramref name="draw"/> (the structure's <c>Stage.Root</c>
    /// template passes its own static method). Mount it while <c>Shell.Ui.ImmersiveLyrics</c> is true; the frame does, through the key.</summary>
    public static Element Mount(Func<StageRootView, Element> draw) => Embed.Comp(new SurfaceArgs(draw), static () => new SurfaceCore());

    /// <summary>The props of <see cref="ViewKeys.StageRoot"/>, shared by the frame's props: one cached object.</summary>
    public static readonly StageRootProps RootProps = new() { Mount = Mount };

    /// <summary>What the surface is mounted with: the structure's drawing of the root.</summary>
    sealed record SurfaceArgs(Func<StageRootView, Element> Draw);

    public static EnterExit EnterTerminal => Design.Reduced
        ? new EnterExit(Opacity: 0f, Active: true) : new EnterExit(Sx: 1.03f, Sy: 1.03f, Opacity: 0f, Active: true);
    public static EnterExit ExitTerminal => Design.Reduced
        ? new EnterExit(Opacity: 0f, Active: true) : new EnterExit(Sx: 1.02f, Sy: 1.02f, Opacity: 0f, Active: true);

    /// <summary>The now-playing context menu (<c>Actions.InstallUi</c> installs it); asked at OPEN time.</summary>
    public static Func<ContextMenuModel?>? NowPlayingMenu { get; set; }

    /// <summary>The accent TARGET for the playing cover. The clock eases <c>Slab.Accent</c> toward it (Stage.Tone.CrossFadeMs).</summary>
    public static readonly Signal<ColorF> AccentSignal = new(ColorF.FromRgba(0xff, 0x9e, 0xc4));

    /// <summary>The stage's pane region currently shows the LYRICS pane (Lyrics mode, Large art, the Artist hero with lyrics): what
    /// the lyrics view's visibility gate reads (<c>Lyrics.s_stageVisible</c>) instead of the stored mode, so one instance serves every
    /// arrangement. SurfaceCore writes it and clears it when the stage closes.</summary>
    public static readonly Signal<bool> LyricsPaneShown = new(false);

    /// <summary>What every stage child reads — ambient, many consumers ⇒ context (component-props-contract §3). ONE instance for
    /// the stage's life (SurfaceCore's field), holding SIGNALS: a consumer re-renders on the signals it reads, never on
    /// SurfaceCore's own renders (V-U50). Every preference is mirrored here by SurfaceCore's one epoch effect (O8); the
    /// playing track is ONE key signal (slot ≪ 32 | row version — the §2 narrowing, V-U34). The actions are set once in
    /// SurfaceCore's constructor.</summary>
    public sealed class StageCtx
    {
        /// <summary>The stage's layout: resolved from the window's size and the tiers the stage is drawn in by a MEMO SurfaceCore binds on its first
        /// render (<see cref="BindLayout"/>), so a view that reads it beside the tier reads the layout OF that tier. An effect wrote it after the
        /// views had already rendered against a new tier, and the plate drew one render of a form for the old tier's layout (audit B2z r3: the W640
        /// crossing rebuilt the parts that form drops).</summary>
        public IReadSignal<Layout> Layout => _layout;
        IReadSignal<Layout> _layout = new Signal<Layout>(Stage.Layout.Seed(1920f, 1080f));

        /// <summary>SurfaceCore's resolved layout (its first render, before any view reads it).</summary>
        internal void BindLayout(IReadSignal<Layout> source) => _layout = source;
        /// <summary>What the stage shows: the stored mode, or <see cref="Stage.Mode.Video"/> while the picture is offered and chosen.</summary>
        public readonly Signal<Mode> Mode = new(Stage.Mode.Lyrics);
        /// <summary>The stage offers its Video mode now (<see cref="Picture.Offered"/>: the structure draws it and a music video is on in the
        /// main window): the top bar's Video tab is there. SurfaceCore writes it.</summary>
        public readonly Signal<bool> VideoOffered = new(false);
        /// <summary>Video mode's stored arrangement (<c>Prefs.Stage.Theatre</c> / <c>.TheatrePane</c>, mirrored by the epoch effect): Theatre is
        /// wanted, and the panel it shows beside the picture (Lyrics or Up next).</summary>
        public readonly Signal<bool> TheatreWanted = new(false);
        public readonly Signal<PaneKind> TheatrePane = new(PaneKind.Queue);
        /// <summary>DERIVED by SurfaceCore: the stage shows the picture in THEATRE now (Video mode, Theatre wanted, and the layout has room for
        /// the panel, <c>Layout.TheatreFits</c>): the picture sits in the left region and <see cref="Pane"/> is the panel beside it.</summary>
        public readonly Signal<bool> Theatre = new(false);
        /// <summary>DERIVED by SurfaceCore: Video mode holds over an AUDIO-ONLY track (video stays on for the main window, this track has none): the
        /// frame stays and the picture's place shows the cover and the lyrics (<see cref="Picture"/>'s audio face). The Video tab is disabled.</summary>
        public readonly Signal<bool> AudioFace = new(false);
        /// <summary>DERIVED by SurfaceCore: the audio face shows the lyrics (everywhere but beside Theatre's own Lyrics panel, which already does):
        /// the gate the face's reading column ticks on.</summary>
        public readonly Signal<bool> AudioLyrics = new(false);
        /// <summary>The top bar's "Theatre mode" / "Fill the screen": Cinema ⇄ Theatre (stored).</summary>
        public Action ToggleTheatre = static () => { };
        /// <summary>THE END CARD (Video mode; <c>Playback.EndHold.cs</c>): a music video ended and the next track waits for the countdown. SurfaceCore's
        /// one countdown timer writes these: the card is up, the whole seconds left (5..1, ticking once a second) and the frame-clock stamp the
        /// countdown started at (<see cref="EndRemainingMs"/> reads what is left now, for a progress line that starts where the countdown stands).
        /// While it is up Cinema's shelf lists the tracks AFTER the card's ("Then").</summary>
        public readonly Signal<bool> EndCard = new(false);
        public readonly Signal<int> EndSeconds = new(0);
        public readonly Signal<long> EndAtMs = new(0L);
        /// <summary>What is left of the countdown now, in ms (0 when none); peeked, for the card's render.</summary>
        public Func<int> EndRemainingMs = static () => 0;
        /// <summary>The countdown's whole length in ms (the card's progress line spans it).</summary>
        public int EndTotalMs => Playback.EndHoldMs;
        /// <summary>The card's "Play now" (the next track now) and "Cancel" (no advance: playback rests at the ended video's end, in Video mode).</summary>
        public Action EndPlayNow = static () => { }, EndCancel = static () => { };
        public readonly Signal<Visualizer.Kind> Kind = new(Visualizer.Catalog.Default);
        public readonly Signal<bool> GalleryOpen = new(true), LyricsOverlay = new(true), Calm = new(false), Moments = new(true);
        /// <summary>The gallery is open AND the layout has room for it (<c>Layout.ShowGallery</c>) — the pane's mount gate
        /// minus the mode; the visualizer clock's lease and ribbon gate read it (SurfaceCore writes it). The idle chrome is
        /// NOT part of it: an open gallery stays until it is closed, because the face is laid out beside it
        /// (<c>Layout.FaceRight</c>) and hiding the pane with the chrome left the face clipped beside an empty column.</summary>
        public readonly Signal<bool> GalleryShown = new(true);
        /// <summary>The gallery was asked for during this stage's life (the face chip, G, the options footer): a tier whose gallery opens only
        /// on request (<c>StageTierMetrics.GalleryOnDemand</c>, C-Stage-Compact) shows it from then on (<c>Layout.GalleryOpenFor</c>).</summary>
        public readonly Signal<bool> GalleryAsked = new(false);
        /// <summary>The Up next column beside the lyrics is open (<c>Prefs.Stage.UpNextOpen</c>, mirrored by the epoch effect); closed, its drawer
        /// slides off the stage's right edge leaving the tab that opens it again (<see cref="UpNextDrawer"/>). <see cref="ToggleUpNext"/> flips it.</summary>
        public readonly Signal<bool> UpNextOpen = new(true);
        /// <summary>DERIVED by SurfaceCore: the bars under the cover show (<c>Layout.ArtBarsShown</c>, Lyrics mode on a Huge 4K stage), narrowed
        /// to one bool so the clock's lease flips only when they come or go.</summary>
        public readonly Signal<bool> ArtBars = new(false);
        // ── the visualizer LAYOUT axis (Stage.Layouts.cs): the stored preferences, mirrored by the one epoch effect ──
        /// <summary>The STORED layout and spectrum choice, the Artist hero's "lyrics over the image" and "slow pan and zoom" switches
        /// and its dimming (0–100) — never read from Prefs on a hot path (O8).</summary>
        public readonly Signal<VizLayout> LayoutPref = new(Stage.VizLayout.Card);
        public readonly Signal<SpectrumStyle> SpectrumPref = new(SpectrumStyle.Bars);
        public readonly Signal<bool> HeroLyricsPref = new(true), HeroMotion = new(true);
        public readonly Signal<int> HeroDim = new(HeroRules.DefaultDim);
        /// <summary>What the playing artist's header looks like (HeroFacts writes it) and the artist SLOT whose header shows (0 = none).</summary>
        public readonly Signal<HeroState> HeroFact = new(HeroState.Pending);
        public readonly Signal<int> HeroArtist = new(0);
        /// <summary>The NEXT queued track's first artist's header url once known (HeroFacts writes it; "" = none) — decoded hidden under the hero near the end.</summary>
        public readonly Signal<string> NextHeroUrl = new("");
        /// <summary>DERIVED by SurfaceCore: the effective layout (<see cref="LayoutRules.Effective"/>), whether the lyric caption line
        /// shows, the one <see cref="Stage.Look"/> every layout geometry call takes, and the pane the pane region shows.</summary>
        public readonly Signal<VizLayout> Effective = new(Stage.VizLayout.Card);
        public readonly Signal<bool> CaptionOn = new(false);
        public readonly Signal<Look> Look = new(Stage.Look.Plain(Stage.Mode.Lyrics));
        public readonly Signal<PaneKind> Pane = new(PaneKind.Lyrics);
        public readonly FloatSignal Sensitivity = new(1f), SyncOffsetMs = new(0f);
        /// <summary>The chrome is mounted (the idle machine's output) · the playing track has timed lyrics (LyricFacts writes it).</summary>
        public readonly Signal<bool> Chrome = new(true), HasTimedLyrics = new(false);
        public readonly Signal<AccentSet> Accent = new(AccentSet.From(ColorF.FromRgba(0xff, 0x9e, 0xc4)));
        /// <summary>The cover's palette for the active arm (SurfaceCore derives it) — the clock's fade TARGET.</summary>
        public readonly Signal<Visualizer.Palette> BasePalette = new(Visualizer.Palette.From(ColorF.FromRgba(0xff, 0x9e, 0xc4), null, Ink.IsDark));
        /// <summary>The LIVE palette a face builds with: the base, rotated by the moments; the clock republishes it when a
        /// fade LANDS (so a face re-renders once per change, never per tick — solid fills bind the slab instead).</summary>
        public readonly Signal<Visualizer.Palette> Palette = new(Visualizer.Palette.From(ColorF.FromRgba(0xff, 0x9e, 0xc4), null, Ink.IsDark));
        /// <summary>(slot ≪ 32) | the row's Version for the playing TRACK; 0 = nothing playing / not a track.</summary>
        public readonly Signal<long> TrackKey = new(0L);
        /// <summary>The gallery toggle's realized node — the TeachingTip's anchor (V-U16).</summary>
        public readonly Signal<NodeHandle> GalleryButton = new(default);
        public readonly Visualizer.Slab Slab = new();
        /// <summary>The gallery's frozen slab: poster tiles bind it, nothing ticks it (the clock recolours it on a landing).</summary>
        public readonly Visualizer.Slab PosterSlab = Visualizer.Slab.CreatePoster();
        /// <summary>The options popover is open (the top bar's sliders button lights while it is) and the action that toggles it.</summary>
        public readonly Signal<bool> OptionsOpen = new(false);
        public Action OpenOptions = static () => { };
        /// <summary>The lyrics words of the stage's bar (Z-Lyrics: "translate", "resync"): the second-line cycle (none, translation, romanization), whether the
        /// playing document carries a second layer and one is on (both read signals, so the caller subscribes), and the lyrics view's resync (re-attach the follow).</summary>
        public Action ToggleSecondLine = static () => { }, ResyncLyrics = static () => { };
        public Func<bool> HasSecondLine = static () => false, SecondLineOn = static () => false;
        /// <summary>Open the gallery (never closes it) — the options footer's switch; G is <see cref="ToggleGallery"/>.</summary>
        public Action OpenGallery = static () => { };
        public Action<Mode> SetMode = static _ => { };
        public Action ToggleGallery = static () => { }, Exit = static () => { }, Activity = static () => { };
        /// <summary>Video mode's "Switch to audio": the video goes off (the placement menu's "Turn off video", one verb) and the stage lands on
        /// the cover and lyrics.</summary>
        public Action SwitchToAudio = static () => { };
        /// <summary>Collapse or open the Up next column (persisted: the stage reopens as it was left).</summary>
        public Action ToggleUpNext = static () => { };
        /// <summary>The idle machine's holds: pointer over a control · a menu/tip/flyout open (counted) · a seek scrub.</summary>
        public Action<bool> OverControls = static _ => { }, MenuOpen = static _ => { }, Scrubbing = static _ => { };
        /// <summary>The playing track's row from the key — <c>Value</c> subscribes the caller to the KEY (one write per slot/version change).</summary>
        public Track RowValue() { int slot = (int)(TrackKey.Value >> 32); return slot > 0 ? new Track(slot) : default; }
    }
    public static readonly Context<StageCtx?> StageContext = new(null);

    /// <summary>Enter from any entry point. <paramref name="begin"/> is the caller's <c>UseContext(SharedTransition.Begin)</c>
    /// (null when headless): the bar art's flight is captured BEFORE the mount so the hero receives it. Nothing playing opens
    /// an EMPTY stage, exactly as the rail ⛶ does today (V-U53). With a music video on, a structure that draws the Video mode opens
    /// on the picture (<see cref="Picture"/>).</summary>
    public static void Open(Action<string>? begin, string cause)
    {
        if (Shell.Ui.ImmersiveLyrics.Peek()) return;
        // ONE full screen: a video-only full screen that is up gives way to the stage (the picture goes back to its home, which the stage's
        // Video mode then takes over where the structure draws it)
        if (Video.State.Resolved == Video.SurfacePlacement.Fullscreen) Video.State.ExitFullscreen();
        begin?.Invoke(Entry.MorphKey);
        // the stage OPENS on the picture whenever it is offered (SurfaceCore's first mode): say so in the same flush as the open, before the
        // stage mounts, so the docked picture is never hidden for the frames between (the "covered by the stage" rule) and its glide to the
        // stage leaves from where it is on screen
        Picture.NoteMode(Picture.OfferedNow());
        Shell.Ui.ImmersiveLyrics.Value = true;
        Diagnostics.NoteEnter(cause);
    }

    /// <summary>Exit. The explicit <c>Begin</c> is REQUIRED: reverse capture is not implemented (ConnectedAnimation.cs:238-241),
    /// so the hero is captured here and the re-mounted bar art receives the flight.</summary>
    public static void Close(Action<string>? begin, string cause)
    {
        if (!Shell.Ui.ImmersiveLyrics.Peek()) return;
        begin?.Invoke(Entry.MorphKey);
        // the window first, outside the flush that unmounts the stage: the page under it then lays out once, at the size the window now has
        // (the teardown's ReleaseWindow used to restore it inside that flush, after the page had laid out at the full-screen size: two
        // page layouts and a 22-24 ms flush on every exit)
        ImmersiveService.Shared.ExitWindowNow(ImmersiveKind.Stage);
        Shell.Ui.ImmersiveLyrics.Value = false;
        Diagnostics.NoteExit(cause);
    }

    const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>A full-bleed, pass-through ZStack LAYER — every Flow.Show child and every layer component's ROOT is one. The
    /// boundary above it is a transparent anchor, a default flex COLUMN that mirrors AlignSelf/JustifySelf/size but never
    /// Margin (Reconciler.MirrorParticipation): inside it AlignSelf would act on the HORIZONTAL cross axis (the transport
    /// card parked at the top-right, the top bar shrunk to its content, the caption pushed right) and a root without Grow is
    /// only as tall as its content (the hero's titles slot collapsed to 0 and took the artist line with it). Stretch fills
    /// the anchor's width, Grow + Basis 0 its height; the PLACED box is this layer's direct child, where vertical = AlignSelf,
    /// horizontal = JustifySelf and the Margin is the offset (FlexLayout.ArrangeZStack). Decorative layers turn hit-testing off.</summary>
    internal static readonly BoxEl Layer = new()
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
        ZStack = true, HitTestPassThrough = true,
    };

    // ══ 1. THE SURFACE ══════════════════════════════════════════════════════════════════════════════════════════════

    sealed class SurfaceCore : Component
    {
        readonly StageCtx _ctx = new();
        Layout? _resolved;   // the stage layout last resolved (the classifier's hysteresis)
        readonly FloatSignal _frac = new(0f);
        readonly Action _onWake;
        // The window's own pointer edges (InputHooks): the pointer left the window (another monitor, the desktop, the OS leave's park) or the
        // window blurred. Both are THE leave (Leave), built once so a subscription allocates nothing per event.
        readonly Action _onWindowLeave, _onWindowBlur;
        // THE END CARD's one timer (Playback.EndHold.cs): armed from the hold's frame-clock stamp for the next whole second, so a re-render or a
        // remount re-arms it for what is left; it fires the release when the countdown is over
        readonly Action _onCountdown;
        TimerHandle _countdown;
        // The idle machine + the counted menu hold (the device flyout, the "…" menu, the options popover and the TeachingTip each count
        // one, V-U22) belong to the IMMERSIVE MODE, not to this component: a remount adopts the same object. The machine itself is
        // started in Render with the host timer clock (MediaPlayerElement.cs:729-730) — never with 0 (V-U10).
        readonly ChromeIdle _idle = ImmersiveService.Shared.IdleOf(ImmersiveKind.Stage);
        readonly IStageVerbs _verbs;
        // Video mode: the picture was chosen (the stage OPENS on it whenever it is offered; the Video tab chooses it, another tab leaves it), and
        // the stored mode the stage shows otherwise (the preference, mirrored by the epoch effect). Mode.Video is never stored.
        readonly Signal<bool> _pictureWanted = new(Picture.OfferedNow());
        readonly Signal<Mode> _stored = new(Mode.Lyrics);
        // The root box's wiring and the structure's view of the surface: built ONCE (the delegates close over this component's fields);
        // each render only re-sizes the view. The structure places all of it and decides none of it.
        readonly StageRootView _view;
        IImmersiveHost? _immersiveHost;
        Action? _openOptions;                 // the options popover's toggle, rebuilt each Render (it closes over the overlay)
        TimerHandle _wake;
        double _armedDueMs = double.PositiveInfinity;
        InputHooks? _hooks;
        Action<string>? _begin;
        NodeHandle _root;
        bool _cursorHidden, _paletteSeeded;
        bool? _loggedVisible;   // the last chrome visibility the `[stage.chrome]` line reported
        bool _movedOverRegion;   // a move has been classified over what counts (PointerOverRegion's answer without a window position)
        Point2 _lastPointer = new(float.NaN, float.NaN);   // the window position the last classified move came from (a repeat is no move)
        ChromeHold _loggedHolds;   // the holds the last `[stage.chrome] hold` lines reported (edges only)
        string _playbackFact = "playing";   // the deck's phase and fault behind the Stopped hold, for its line
        OverlayHandle? _tip, _options;

        public SurfaceCore()
        {
            _onWake = () => { _armedDueMs = double.PositiveInfinity; Sync(); };
            _onWindowLeave = () => Leave("window");
            // a blur leaves too when the pointer was already out (the engine's window edge fires only for a pointer that was in): the machine
            // hears one more leave, which only (re)schedules the leave debounce, so the chrome is free to hide whatever came before
            _onWindowBlur = () => Leave("blur");
            _onCountdown = () => Countdown(Playback.EndHoldNow.Peek());
            // The stage's verbs (StageModel.cs): the mode, the ONE gallery rule, the face step, exit, activity, options.
            _verbs = new StageVerbs(StagePrefsPort.Shared, () => StageModel.StateOf(_ctx), new StageHost(this));
            _ctx.SetMode = mode =>
            {
                bool offered = _ctx.VideoOffered.Peek();
                // the frame is up: the picture is offered, or Video mode holds over an audio-only track (the audio face)
                bool frame = offered || _ctx.Mode.Peek() == Mode.Video;
                // the audio face in Cinema stands lit on Lyrics (it shows them): pressing it again keeps the frame as it is
                if (mode == Mode.Lyrics && _ctx.AudioFace.Peek() && !_ctx.Theatre.Peek()) { _verbs.Activity(); return; }
                if (mode == Mode.Video)
                {
                    // the Video tab: the picture, filling the stage (Cinema)
                    if (offered)
                    {
                        _pictureWanted.Value = true;
                        if (_ctx.TheatreWanted.Peek()) Prefs.Stage.SetTheatre(false, (int)_ctx.TheatrePane.Peek());
                    }
                    _verbs.Activity();
                    return;
                }
                // the Lyrics and Up next tabs with a music video on: the picture stays, the panel slides in beside it (Theatre), where the
                // layout has room for it; Visualizer and Artist leave the picture as before
                if (frame && TheatreRules.OpensTheatre(mode) && _ctx.Layout.Peek().TheatreFits)
                {
                    _pictureWanted.Value = true;
                    Prefs.Stage.SetTheatre(true, (int)TheatreRules.PaneOf(mode));
                    _verbs.Activity();
                    return;
                }
                _pictureWanted.Value = false;
                _verbs.SetMode(mode);
            };
            _ctx.ToggleTheatre = () =>
            {
                Prefs.Stage.SetTheatre(!_ctx.Theatre.Peek(), (int)_ctx.TheatrePane.Peek());
                _verbs.Activity();
            };
            _ctx.SwitchToAudio = () =>
            {
                _pictureWanted.Value = false;
                Video.State.TurnOff();
                _verbs.SetMode(Mode.Lyrics);
            };
            // the verbs decide on the gallery as it is SHOWN (StageModel.StateOf), then the ask is noted: the order matters on a tier whose
            // gallery opens only on request, where the first press must open a sheet nobody has seen yet
            _ctx.ToggleGallery = () => { _verbs.ToggleGallery(); _ctx.GalleryAsked.Value = true; };
            _ctx.OpenGallery = () => { _verbs.OpenGallery(); _ctx.GalleryAsked.Value = true; };
            _ctx.Exit = _verbs.Exit;
            _ctx.Activity = _verbs.Activity;
            _ctx.ToggleUpNext = () => { Prefs.Stage.SetUpNextOpen(!_ctx.UpNextOpen.Peek()); _verbs.Activity(); };
            _ctx.OpenOptions = _verbs.OpenOptions;
            _ctx.ToggleSecondLine = static () => LyricsVerbs.Global.ToggleSecondLine();
            _ctx.ResyncLyrics = static () => Lyrics.ViewCore.StageResync?.Invoke();
            _ctx.HasSecondLine = static () => Lyrics.Prefs.Available.Value != 0;
            _ctx.SecondLineOn = static () => Lyrics.Prefs.SecondaryLine() != 0;
            _ctx.EndRemainingMs = static () => Playback.EndHoldNow.Peek().RemainingMs(Playback.FrameNowMs());
            // the card's buttons leave with the card: focus goes back to the stage's root first (its key map: Space, Esc, F11), never to nothing
            _ctx.EndPlayNow = () =>
            {
                FocusRoot();
                var hold = Playback.EndHoldNow.Peek();
                if (hold.Phase == Playback.EndHold.Counting) Playback.ReleaseEndHold(hold.AtMs);
            };
            _ctx.EndCancel = () => { FocusRoot(); Playback.CancelEndHold(); };
            // in Theatre only a control over the picture holds the chrome: a top-bar button or the panel's is outside it (PointerOverRegion)
            _ctx.OverControls = over => { _idle.SetPointerOverControls(over && PointerOverRegion(), Now()); Sync(); };
            _ctx.MenuOpen = open => { _idle.MenuOpen(open, Now()); Sync(); };
            _ctx.Scrubbing = on => { _idle.SetScrubbing(on, Now()); Sync(); };
            var ctx = _ctx;
            _view = new StageRootView
            {
                Stage = ctx, Width = 0f, Height = 0f,
                OnRealized = h => _root = h,
                // a move ANYWHERE within the stage (the theme wires it as OnPointerMoveWithin, so a move over a lyric line or the picture's ground
                // counts too), in window DIP: the stage's root and the chrome over the picture (the video layer, another tree) report one point
                // In THEATRE only the picture counts (RegionOf): a move over it is activity; the first move off it (onto the panel, the floor, the
                // top bar) is a LEAVE, so the chrome goes after the leave debounce and the cursor never hides off the picture.
                OnHoverMove = p => { PointerAt(_hooks?.GetPointerPosition?.Invoke() ?? p); Sync(); },
                // a hover handed from one tree to the other (the stage ⇄ the chrome over the picture) or taken by an overlay is not a leave: the
                // pointer holds drop, nothing is scheduled. A hover exit never DETECTS a window leave: the window's own edges do (_onWindowLeave,
                // _onWindowBlur, which fire after this exit), so an exit whose pointer is not at a real point over the stage does nothing here
                OnPointerExit = () =>
                {
                    if (!PointerHandedOver()) return;
                    _idle.PointerCovered(Now());
                    Log.Info(Diagnostics.Category, $"[stage.chrome] exit as=covered src=exit ptr={PointerText()}");
                    Sync();
                },
                OnPointerWheel = _ => { if (PointerOverRegion()) { _idle.Activity(ChromeActivity.Pointer, Now()); Sync(); } },   // wheel = activity (V-U22), over the picture alone in Theatre
                OnKeyDown = OnKey,
                // Never leave focus NULL while the stage is up: an unhandled Escape clears it and disarms the key map (the fullscreen video layer guards the same way).
                OnFocusChanged = got =>
                {
                    if (got || _root.IsNull) return;
                    if ((_hooks?.GetFocus?.Invoke() ?? default).IsNull) _hooks?.FocusNode?.Invoke(_root, false);
                },
                // artist photo + scrims (the backdrop is hidden under an opaque header, see Backdrop)
                ShowHeroLayer = () => ctx.Mode.Value == Mode.Visualizer && ctx.Look.Value.Eff == VizLayout.Artist,
                ShowFace = () => ctx.Mode.Value == Mode.Visualizer && LayoutRules.ShowsFace(ctx.Look.Value.Eff),
                ShowSmoke = () => ctx.Mode.Value == Mode.Visualizer && ctx.Look.Value.Eff == VizLayout.Card,
                ShowArtistName = () => ctx.Mode.Value == Mode.Visualizer && ctx.Look.Value.Eff == VizLayout.Artist && !ctx.Look.Value.HeroLyrics,
                ShowCaption = () => ctx.CaptionOn.Value,
                ChromeVisible = () => ctx.Chrome.Value,
                // NOT gated on the idle chrome: the face beside it is inset for it while it is open (GalleryShown)
                ShowGallery = () => ctx.Mode.Value == Mode.Visualizer && ctx.GalleryShown.Value,
                // artist-header lookups only while the Artist layout is the stored choice in Visualizer mode (Card never pays for them)
                ShowHeroFacts = () => ctx.Mode.Value == Mode.Visualizer && ctx.LayoutPref.Value == VizLayout.Artist,
                HairlineFraction = () => _frac.Value,
                ShowPicture = () => ctx.Mode.Value == Mode.Video,
                PictureRealized = Picture.Realized,
                ShowTheatre = () => ctx.Theatre.Value,
                ShowAudioFace = () => ctx.AudioFace.Value,
                ShowEndCard = () => ctx.EndCard.Value,
                // Theatre's about block (the Artist pane's own facts and demand; its Artist tab opens in the stage)
                About = () => AboutFacts(ctx),
                DemandAbout = Demand,
                OpenArtist = () => ctx.SetMode(Mode.Artist),
                TopBar = new StageTopBarProps { Stage = ctx },
                Transport = TransportProps(ctx),
                Gallery = new StageGalleryProps { Stage = ctx, Verbs = StageSettingsVerbs.Shared },
                Backdrop = BackdropProps(ctx),
                Hero = HeroProps(ctx),
                Face = new StageLayerProps { Stage = ctx },
                FaceScrim = FaceScrimProps(ctx),
                Spectrum = new StageLayerProps { Stage = ctx },
                NowPlayingCard = NowPlayingCardProps(ctx),
                Identity = IdentityProps(ctx),
                ArtistName = ArtistNameProps(ctx),
                Layers = s_layers,
            };
        }

        /// <summary>The surface, as the stage's verbs reach it.</summary>
        sealed class StageHost(SurfaceCore owner) : IStageHost
        {
            public void Close(string cause) => Stage.Close(owner._begin, cause);
            public void OpenOptions() => owner._openOptions?.Invoke();
            public void Activity() { owner._idle.Activity(ChromeActivity.Pointer, owner.Now()); owner.Sync(); }
            public void GoTo(Shell.Route route) => Shell.GoTo(route);
            public void NoteMode(Mode mode) => Diagnostics.NoteMode(mode);
            public void NotePick(Visualizer.Kind face) => Diagnostics.NotePick(face);
        }

        /// <summary>The stage is still wanted (the shell mounts it while this is up): a teardown that finds it true is a REMOUNT.</summary>
        static bool StageWanted() => Shell.Ui.ImmersiveLyrics.Peek();

        public override Element Render()
        {
            var draw = UseProps<SurfaceArgs>().Draw;
            var hooks = UseContext(InputHooks.Current);
            _hooks = hooks;
            var overlay = UseContext(Overlay.Service);
            _begin = UseContext(SharedTransition.Begin);
            var vp = UseContextSignal(Viewport.Size);
            var facts = Theming.TierContext.DrawnIn(UseContext(Theming.ThemeFactsContext.Signal));   // the tiers this stage is drawn in
            var ctx = _ctx;
            // the options popover: anchored under the sliders button (the TeachingTip's anchor too), light-dismiss, holding the chrome while open
            _openOptions = () =>
            {
                if (_options is { IsOpen: true } open) { open.Close(); return; }
                var opened = overlay.Open(() => ctx.GalleryButton.Peek(), () => OptionsContent(ctx, () => _options?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
                ctx.MenuOpen(true);
                ctx.OptionsOpen.Value = true;
                opened.ClosedAction = () => { _options = null; ctx.OptionsOpen.Value = false; ctx.MenuOpen(false); };
                _options = opened;
            };

            // ── the ONE layout, a memo (equality-gated, pulled glitch-free by whoever reads it) ──
            // The classifier's edges are the stage policy's, read tracked so another structure re-classifies the open stage in place; the
            // budget's metrics are the window tier's (the stage fills the window), read tracked so a tier crossing re-allocates it. The
            // classifier's hysteresis reads the last layout resolved.
            ctx.BindLayout(UseComputed<Layout>(() =>
            {
                var size = vp.Value;
                var stage = ShellPolicy.Tracked.Stage;
                var metrics = stage.MetricsFor(facts.Value.Tiers);
                var next = Layout.Resolve(size.Width, size.Height, _resolved, stage.Aspect, metrics);
                _resolved = next;
                return next;
            }));
            // ── the gallery's "shown" gate, narrowed to one bool (a resize re-runs this, the clock's lease only on a flip): open, room
            //    for it, and a layout that has a gallery (only Card does) ──
            UseSignalEffect(() => ctx.ArtBars.SetIfChanged(ctx.Layout.Value.ArtBarsShown(ctx.Mode.Value)));
            UseSignalEffect(() =>
            {
                var layout = ctx.Layout.Value;
                ctx.GalleryShown.SetIfChanged(layout.GalleryOpenFor(ctx.GalleryOpen.Value, ctx.GalleryAsked.Value) && layout.ShowGallery && LayoutRules.GalleryAllowed(ctx.Look.Value.Eff));
            });

            // ── EVERY preference, once per Prefs.Stage epoch, into the context's signals (O8) — the only Prefs.Stage reads on the stage ──
            UseSignalEffect(() =>
            {
                _stored.SetIfChanged((Mode)Prefs.Stage.Mode());
                ctx.Kind.SetIfChanged((Visualizer.Kind)Prefs.Stage.Visualizer());
                ctx.GalleryOpen.SetIfChanged(Prefs.Stage.GalleryOpen());
                ctx.UpNextOpen.SetIfChanged(Prefs.Stage.UpNextOpen());
                ctx.TheatreWanted.SetIfChanged(Prefs.Stage.Theatre());
                ctx.TheatrePane.SetIfChanged((PaneKind)Prefs.Stage.TheatrePane());
                ctx.LyricsOverlay.SetIfChanged(Prefs.Stage.LyricsOverlay());
                ctx.Calm.SetIfChanged(Prefs.Stage.Calm());
                ctx.Moments.SetIfChanged(Prefs.Stage.Moments());
                ctx.Sensitivity.SetIfChanged(Prefs.Stage.Sensitivity());
                ctx.SyncOffsetMs.SetIfChanged(Prefs.Stage.SyncOffsetMs());
                ctx.LayoutPref.SetIfChanged((VizLayout)Prefs.Stage.Layout());
                ctx.SpectrumPref.SetIfChanged((SpectrumStyle)Prefs.Stage.Spectrum());
                ctx.HeroLyricsPref.SetIfChanged(Prefs.Stage.HeroLyrics());
                ctx.HeroMotion.SetIfChanged(Prefs.Stage.HeroMotion());
                ctx.HeroDim.SetIfChanged(Prefs.Stage.HeroDim());
            });

            // ── the mode: the picture while it is offered and chosen (Video mode, never stored), else the stored mode; the picture's
            //    showing state goes to Stage.Picture (the presenter follows the stage's reservation while it holds) ──
            UseSignalEffect(() => ctx.VideoOffered.SetIfChanged(Picture.Offered()));
            UseSignalEffect(() =>
            {
                // the picture is chosen (the stage opened on it, or the Video tab) and it is offered now, or video stays on over an audio-only
                // track (the audio face: the frame holds until the next track brings the picture back, or the video is turned off)
                var m = _pictureWanted.Value && (ctx.VideoOffered.Value || Picture.VideoOn()) ? Mode.Video : _stored.Value;
                ctx.Mode.SetIfChanged(m);
                Diagnostics.NoteMode(m);                          // gated: logs only on a real change, keeps the card's mode truthful
                Picture.NoteMode(m == Mode.Video);
            });
            // Theatre as SHOWN: Video mode, wanted, and the window tier's layout has room for the panel (a crossing into a tier without one
            // draws Cinema and keeps the choice for the way back)
            UseSignalEffect(() => ctx.Theatre.SetIfChanged(ctx.Mode.Value == Mode.Video && ctx.TheatreWanted.Value && ctx.Layout.Value.TheatreFits));
            // the audio face: Video mode without a picture now; it shows the lyrics but beside Theatre's own Lyrics panel
            UseSignalEffect(() => ctx.AudioFace.SetIfChanged(ctx.Mode.Value == Mode.Video && !ctx.VideoOffered.Value));
            UseSignalEffect(() => ctx.AudioLyrics.SetIfChanged(ctx.AudioFace.Value && !(ctx.Theatre.Value && ctx.TheatrePane.Value == PaneKind.Lyrics)));
            // ── THE END CARD (Playback.EndHold.cs): the stage offers the hold while it shows the picture with a video face (a structure that draws
            //    Video mode, which is the one that draws the card); its one countdown timer follows the hold. Closing the stage withdraws the
            //    offer, which cancels a running countdown (the unmount below). ──
            _countdown = UseTimeout(_onCountdown, Playback.EndHoldMs, DepKey.Empty);   // armed once at mount: a fire with no hold counting does nothing
            UseSignalEffect(() => Playback.OfferEndHold(Picture.Supported && ctx.Mode.Value == Mode.Video && !ctx.AudioFace.Value));
            UseSignalEffect(() => Countdown(Playback.EndHoldNow.Value));
            // the stage's view, for the chrome Video mode draws over the picture (in the video layer, outside this subtree)
            var view = _view;
            UseLayoutEffect(() =>
            {
                Picture.Publish(view);
                return () => Picture.Withdraw(view);
            }, DepKey.Empty);

            // ── the layout axis, derived (each effect reads only what the previous wrote): the effective layout, whether the caption
            //    line shows, the ONE Look the geometry takes, the pane region's pane, and the lyrics view's visibility gate ──
            UseSignalEffect(() =>
            {
                var layout = ctx.Layout.Value;   // the window tier's allowed layouts ride on it (P6 WK), so a tier crossing re-decides
                ctx.Effective.SetIfChanged(LayoutRules.Effective(ctx.LayoutPref.Value, layout.Aspect, ctx.HeroFact.Value, layout.AllowedLayouts));
            });
            // A row-identity stage too short for the Lyrics caption keeps its art, title and controls and drops the caption (Layout.CaptionFits).
            UseSignalEffect(() => ctx.CaptionOn.SetIfChanged(LayoutRules.ShowsCaption(ctx.Mode.Value, ctx.Effective.Value, ctx.LyricsOverlay.Value,
                ctx.HasTimedLyrics.Value, ctx.Layout.Value.ShowPane, ctx.Kind.Value) && ctx.Layout.Value.CaptionFits(ctx.Mode.Value)));
            UseSignalEffect(() =>
            {
                var look = LayoutRules.LookOf(ctx.Mode.Value, ctx.Effective.Value, ctx.SpectrumPref.Value, ctx.HeroLyricsPref.Value, ctx.CaptionOn.Value);
                ctx.Look.SetIfChanged(look);
                Diagnostics.NoteLayout(look.Eff);
            });
            UseSignalEffect(() =>
            {
                var look = ctx.Look.Value;
                // Video mode has a pane only in Theatre: the panel beside the picture
                ctx.Pane.SetIfChanged(look.Mode == Mode.Video
                    ? ctx.Theatre.Value ? ctx.TheatrePane.Value : PaneKind.None
                    : LayoutRules.Pane(look.Mode, look.Eff, look.HeroLyrics));
            });
            UseSignalEffect(() => LyricsPaneShown.SetIfChanged(ctx.Pane.Value == PaneKind.Lyrics));

            // ── the playing ROW, narrowed (§2 perf fix): ONE subscription to Tracks.Changed, ONE write when the slot or its
            //    Version moved; every consumer reads ctx.TrackKey and re-renders only then (V-U12: no hook inside a factory) ──
            UseSignalEffect(() =>
            {
                _ = Entities.ScopeEpoch.Value;                    // FIRST (the rail's rule, Rail.UI.cs NowPlayingBody): a scope switch re-points the table read below
                var r = Playback.Current.Value;
                _ = Entities.Current.Tracks.Changed.Value;
                long key = 0L;
                if (r.Kind == EntityKind.Track && !r.IsNone)
                {
                    var t = Entities.Current.Tracks;
                    uint version = (uint)r.Slot < (uint)t.Count ? t.Version[r.Slot] : 0u;
                    key = ((long)r.Slot << 32) | version;
                }
                ctx.TrackKey.SetIfChanged(key);
            });

            // ── the accent set + palette + the cross-fade TARGET, derived EAGERLY (the effect body runs now, before any child
            //    mounts, so the clock's first read and every control's first render see the cover's accent and never the stale
            //    one); re-derived when the track key or the cover's late grading moves. A theme flip (RethemeAll re-renders this
            //    component; Tok.Epoch is a plain int) re-derives through the keyed effect — four ints, no 3-int DepKey (V-U3). ──
            UseSignalEffect(PublishAccent);
            int themeEpoch = Tok.Epoch;
            UseEffect(PublishAccent, DepKey.From(themeEpoch, 0, 0, 0));

            // ── REAL fullscreen with prior-state restore, and a focus scope at the root with focus returned to whoever opened the
            //    stage: both are the IMMERSIVE SERVICE's (ImmersiveService.cs), keyed by the mode — a remount of this view while the
            //    stage is still wanted leaves the window and the scope as they are ──
            var immersive = ImmersiveService.Shared;
            var immersiveHost = _immersiveHost ??= new HooksImmersiveHost(hooks);
            UseLayoutEffect(() =>
            {
                immersive.EnterWindow(ImmersiveKind.Stage, immersiveHost);
                return () => { immersive.ReleaseWindow(ImmersiveKind.Stage, immersiveHost, StageWanted); };
            }, DepKey.Empty);

            // The scope is this component's rendered child (the context provider's node, above the structure's root box); FOCUS goes to the root
            // box, which carries the key map (OnKey). Keys route from the focused node UP its ancestors, so focus on the provider node above the
            // box never reached the box's handler: Esc and F11 fell through as unhandled (Esc then cleared focus) and the stage stayed open.
            UseLayoutEffect(() =>
            {
                if (Context.HostNode.IsNull) return null;
                immersive.PushFocus(ImmersiveKind.Stage, immersiveHost, Context.HostNode, focusRoot: _root.IsNull);
                if (!_root.IsNull) immersiveHost.FocusNode(_root, false);
                return () => { immersive.ReleaseFocus(ImmersiveKind.Stage, immersiveHost, StageWanted); };
            }, DepKey.Empty);

            // ── unmount: release the cursor override, close the tip ──
            UseEffect(() => () =>
            {
                if (_cursorHidden) { hooks.SetCursorOverride?.Invoke(this, null); _cursorHidden = false; }
                _tip?.Close();
                _tip = null;
                _options?.Close();
                _options = null;
                LyricsPaneShown.Value = false;                    // the lyrics view's gate must not outlive the stage (it ticks only while shown)
                if (!StageWanted()) Playback.OfferEndHold(false); // the stage closed: a running countdown stops (a remount keeps it)
            }, DepKey.Empty);

            // ── the window's pointer edges: a leave of the window and a blur are THE leave (sources `window` and `blur`). A re-entry needs no
            //    handler: the leave forgot the pointer's point, so the next real move over the stage is the machine's ENTER (a reveal) ──
            UseEffect(() =>
            {
                hooks.PointerLeftWindowObserved += _onWindowLeave;
                hooks.WindowBlurObserved += _onWindowBlur;
                return () =>
                {
                    hooks.PointerLeftWindowObserved -= _onWindowLeave;
                    hooks.WindowBlurObserved -= _onWindowBlur;
                };
            }, DepKey.Empty);

            // ── idle: the engine machine on the HOST TIMER clock, one timer re-armed from NextWakeMs (MediaPlayerElement.cs:390-416, :726-730) ──
            _wake = UseTimeout(_onWake, 1_000f, DepKey.Empty);                           // arms once at mount — harmless (Sync re-arms)
            var idle = _idle;
            idle.Start(_wake.NowMs);
            // a pause, the end or a failure pins the chrome; a load (a video joining, a skip) does not (ChromeIdle.PlaybackOf)
            var chromePlayback = ChromeIdle.PlaybackOf(Playback.PhaseSignal.Value, Playback.Error.Value);
            string playbackFact = Playback.PhaseSignal.Peek() + (Playback.Error.Peek() != Playback.Fault.None ? "/" + Playback.Error.Peek() : "");
            UseEffect(() => { _playbackFact = playbackFact; idle.SetPlayback(chromePlayback, Now()); Sync(); }, DepKey.From((int)chromePlayback));

            // ── the hairline fraction from the coarse report (1 Hz is plenty for a 3-DIP line) ──
            UseSignalEffect(() => { int d = Playback.DurationMs.Value, p = Playback.PositionMs.Value; _frac.SetIfChanged(d > 0 ? Math.Clamp(p / (float)d, 0f, 1f) : 0f); });

            // ── the TeachingTip, once: anchored to the gallery toggle's realized node (a SIGNAL, V-U16); it holds the chrome while open ──
            UseSignalEffect(() =>
            {
                var anchor = ctx.GalleryButton.Value;
                if (ctx.Mode.Value != Mode.Visualizer || anchor.IsNull || _tip is not null || Prefs.Stage.TipSeen()) return;
                ctx.MenuOpen(true);
                _tip = TeachingTip.Show(overlay, () => ctx.GalleryButton.Peek(), tip =>
                {
                    tip.Title = Loc.Get(Strings.Stage.Tip.Title);
                    tip.Body = Loc.Get(Strings.Stage.Tip.Body);
                    tip.ActionButtonContent = Loc.Get(Strings.Stage.Tip.GotIt);
                    tip.ActionButtonIsAccent = true;
                    tip.ActionButtonClick = () => _tip?.Close();
                    tip.PreferredPlacement = TeachingTip.PlacementMode.Bottom;   // nested enum (TeachingTip.cs:48, V-U5)
                    tip.Closed = _ => { Prefs.Stage.SetTipSeen(); _tip = null; ctx.MenuOpen(false); };
                });
            });

            var L = ctx.Layout.Value;
            // THE PICTURE IS THE STRUCTURE'S: the root box (size, wiring), the layer stack and the chrome's placement are `draw`'s.
            return Ctx.Provide(StageContext, ctx, draw(_view with { Width = L.W, Height = L.H }));   // explicit size (V-U49); the floor is the Backdrop's
        }

        /// <summary>Focus the stage's root box (the key map's home), as the stage does when it opens.</summary>
        void FocusRoot()
        {
            if (!_root.IsNull) _hooks?.FocusNode?.Invoke(_root, false);
        }

        /// <summary>The end card's countdown, from the hold as it stands (<paramref name="hold"/>): the card's signals, and the ONE timer armed for
        /// the next whole second; when nothing is left, the release (the card stays up until the advance lands). Reads no signal.</summary>
        void Countdown(Playback.EndHoldFacts hold)
        {
            var ctx = _ctx;
            if (hold.Phase != Playback.EndHold.Counting)
            {
                _countdown.Cancel();
                ctx.EndCard.SetIfChanged(false);
                ctx.EndSeconds.SetIfChanged(0);
                return;
            }
            int left = hold.RemainingMs(Playback.FrameNowMs());
            if (left <= 0) { Playback.ReleaseEndHold(hold.AtMs); return; }
            int seconds = (left + 999) / 1000;
            ctx.EndAtMs.SetIfChanged(hold.AtMs);
            ctx.EndSeconds.SetIfChanged(seconds);
            ctx.EndCard.SetIfChanged(true);
            _countdown.RestartIn(left - (seconds - 1) * 1000);
        }

        /// <summary>The accent derivation (one definition, two triggers). Reads the track KEY (not the table), the cover's late
        /// grading and the theme; writes the outputs (the accent set, the base palette, the accent target) equality-gated, so
        /// the second trigger on the same inputs is a no-op.</summary>
        void PublishAccent()
        {
            var ctx = _ctx;
            var track = ctx.RowValue();
            string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            if (url.Length > 0) _ = global::Wavee.Palette.Watch(url).Value;
            ColorF accentBase = Ink.Accent(url);   // the system accent with "Now playing colours" off (a tracked read: a flip re-runs this)
            ctx.Accent.SetIfChanged(AccentSet.From(accentBase));
            var palette = Visualizer.Palette.From(accentBase, Ink.NowPlayingColors ? Design.SchemeFor(url) : null, Ink.IsDark);
            ctx.BasePalette.SetIfChanged(palette);
            if (!_paletteSeeded)
            {
                // the FIRST derivation seeds what the faces and the backdrop read, before any child mounts; from here on the
                // clock owns the live palette (it fades to every later base and rotates it on the moments)
                _paletteSeeded = true;
                ctx.Palette.Value = palette;
                ctx.Slab.SetPalette(palette);
                ctx.PosterSlab.SetPalette(palette);
            }
            AccentSignal.SetIfChanged(accentBase);
        }

        /// <summary>F11 closes; Esc steps Cinema → Theatre, else closes (<see cref="TheatreRules.EscapeStepsToTheatre"/>); [ / ] step the face (the layout inside a Large art / Centered / Artist layout), G the gallery (back in Card); everything else is the player bar's own key map (Shell.PlayerBar.UI.cs:645-659 via
        /// Shell.PlayerKey, Shell.PlayerBar.cs:448-459) so Space / ← → / ↑ ↓ work in fullscreen (O9). Media keys need nothing:
        /// they arrive through SMTC regardless of focus (Playback.Os.cs:309-323). Any key is idle activity.</summary>
        void OnKey(KeyEventArgs e)
        {
            _idle.Activity(ChromeActivity.Keyboard, Now());
            Sync();
            if (e.Handled) return;
            if (e.Mods == KeyModifiers.None && e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                // Cinema → Theatre first (the toggle's own verb: its motion, its stored choice), then out (TheatreRules.EscapeStepsToTheatre)
                if (TheatreRules.EscapeStepsToTheatre(_ctx.Mode.Peek(), _ctx.Theatre.Peek(), _ctx.AudioFace.Peek(), _ctx.Layout.Peek().TheatreFits)) { _ctx.ToggleTheatre(); return; }
                Close(_begin, "escape");
                return;
            }
            if (e.Mods == KeyModifiers.None && e.KeyCode == Keys.F11) { e.Handled = true; Close(_begin, "f11"); return; }
            // [ / ] the previous / next face (Shift: the group) in Visualizer mode; G the gallery (from another mode it
            // switches to Visualizer first — ModeRules.ToggleGallery)
            int step = StageModel.FaceStepOf(e.KeyCode, e.Ctrl, e.Alt, _ctx.Mode.Peek());
            if (step != 0)
            {
                e.Handled = true;
                // inside a layout the keys walk the layouts (Artist only: Large art / Centered step the FACE behind the cover; the STORED one — an Artist without a header draws Large art but
                // steps on from Artist); Card is entered and left through the options popover or G
                _verbs.StepFace(step, e.Shift);
                return;
            }
            // G is the visualizer's: Video mode has no gallery (the picture is the stage)
            if (e.Mods == KeyModifiers.None && e.KeyCode == Keys.G && !e.IsRepeat && _ctx.Mode.Peek() != Mode.Video) { e.Handled = true; _ctx.ToggleGallery(); return; }
            var intent = Shell.PlayerKey(e.KeyCode, true, e.Handled, e.Ctrl || e.Alt, e.Shift);
            if (intent == Shell.PlayerKeyIntent.None) return;
            e.Handled = true;
            switch (intent)
            {
                // The bar's keyboard scrub ladder (5 s → 15 s → 30 s while held, Shift = 1 s): the stage's own seek rail mounts its commit tick.
                case Shell.PlayerKeyIntent.SeekBack: Shell.ScrubController.KeyStep(-1, fine: false); break;
                case Shell.PlayerKeyIntent.SeekForward: Shell.ScrubController.KeyStep(1, fine: false); break;
                case Shell.PlayerKeyIntent.SeekBackFine: Shell.ScrubController.KeyStep(-1, fine: true); break;
                case Shell.PlayerKeyIntent.SeekForwardFine: Shell.ScrubController.KeyStep(1, fine: true); break;
                case Shell.PlayerKeyIntent.VolumeDown: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() - .05f, 0f, 1f)); break;
                case Shell.PlayerKeyIntent.VolumeUp: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() + .05f, 0f, 1f)); break;
                case Shell.PlayerKeyIntent.Toggle: if (!e.IsRepeat) PlayerVerbs.Default.TogglePlay("stage.space"); break;
            }
        }

        /// <summary>MediaPlayerElement.Sync (:390-416), verbatim in shape: tick (the bool is IGNORED — V-U10), publish the chrome
        /// value-gated, apply the cursor, re-arm the one timer at NextWakeMs (+∞ = nothing pending, no timer — V-U48).</summary>
        void Sync()
        {
            var vis = _idle;
            if (!vis.Started) return;
            double now = _wake.NowMs;
            vis.Tick(now);
            _ctx.Chrome.SetIfChanged(vis.ChromeVisible);
            if (RegionOf() is null) { vis.Region = "stage"; vis.PointerOutsideRegion = false; } else vis.Region = "picture";
            // one line per visibility edge, naming the cause and what holds it (a chrome that sticks says why in the owner's log)
            if (vis.ChromeVisible != _loggedVisible)
            {
                _loggedVisible = vis.ChromeVisible;
                Log.Info(Diagnostics.Category, $"[stage.chrome] {(vis.ChromeVisible ? "show" : "hide")} cause={vis.LastCause} holds={ChromeHoldsText(vis.Holds)} menus={vis.MenuHolds} region={vis.Region} ptr={PointerText()} inside={(!vis.PointerOutsideRegion && PointerOnStage() ? "yes" : "no")}");
            }
            LogHoldEdges(vis);
            if (vis.CursorHidden != _cursorHidden)
            {
                _cursorHidden = vis.CursorHidden;
                if (_cursorHidden) _hooks?.SetCursorOverride?.Invoke(this, CursorId.Hidden);
                else _hooks?.SetCursorOverride?.Invoke(this, null);
            }
            double due = vis.NextWakeMs;
            if (double.IsPositiveInfinity(due)) { _armedDueMs = double.PositiveInfinity; return; }
            if (due < _armedDueMs - 1.0)
            {
                _armedDueMs = due;
                _wake.RestartIn((float)Math.Max(0.0, due - now));
            }
        }

        double Now() => _wake.NowMs;

        /// <summary>What counts as the stage for the pointer, in window DIP: the picture's rect in Theatre (the reservation the presenter follows, as
        /// laid out now, so the rule moves with it), else null (the whole stage). The audio face has no picture to guard: the whole stage.</summary>
        RectF? RegionOf()
        {
            if (_ctx.Mode.Peek() != Mode.Video || !_ctx.Theatre.Peek() || _ctx.AudioFace.Peek()) return null;
            var node = Picture.Reservation;
            if (node.IsNull || _hooks?.GetNodeRect is not { } rectOf) return null;
            return rectOf(node);
        }

        /// <summary>Where a pointer that is over the picture in Theatre has NOT yet left its controls (window DIP): the picture's rect <paramref name="picture"/>
        /// widened by the stage's side gutter and carried down to the stage's foot, over what belongs to the picture under it (the title and the about
        /// block, or the bar's own overshoot). The control bar is the picture's foot, so a hand that runs along it and overshoots by a few DIP has not
        /// left (the owner's log: hide / show six times in seconds, leaves at y 746-781 under a picture ending at 746, back 183-865 ms later). The
        /// ENTER stays the picture's rect itself.</summary>
        RectF LeaveRegionOf(in RectF picture)
        {
            var L = _ctx.Layout.Peek();
            float gutter = L.TransportInset;
            float x0 = MathF.Max(0f, picture.X - gutter), x1 = MathF.Min(L.W, picture.X + picture.W + gutter);
            return new RectF(x0, picture.Y, MathF.Max(0f, x1 - x0), MathF.Max(picture.H, L.H - picture.Y));
        }

        /// <summary>A pointer move at <paramref name="w"/> (window DIP). Over the region (the picture in Theatre, else the stage) it is the machine's move.
        /// Outside it but still in <see cref="LeaveRegionOf"/> while the pointer was over the picture it is neither (no activity, no leave): the idle
        /// timing runs on. The first move past that is the leave. A hover the engine replays with the pointer at rest (the picture moved from under it:
        /// Theatre's pane toggle shrinks it away) is no move: it decides nothing, and the next real move is judged against the new geometry.</summary>
        void PointerAt(Point2 w)
        {
            if (w.X == _lastPointer.X && w.Y == _lastPointer.Y) return;
            _lastPointer = w;
            var region = RegionOf();
            _idle.Region = region is null ? "stage" : "picture";
            if (region is not { } r || Inside(r, w))
            {
                _movedOverRegion = true;
                _idle.PointerOutsideRegion = false;
                _idle.PointerMoved(w.X, w.Y, Now());
                return;
            }
            if (_idle.PointerOutsideRegion) return;
            if (Inside(LeaveRegionOf(r), w)) return;   // the overshoot under (or beside) the picture: not left yet
            _idle.PointerOutsideRegion = true;
            Leave("region");
        }

        /// <summary>THE one leave: the pointer is no longer over what counts. <paramref name="source"/>: <c>region</c> (off the picture in Theatre,
        /// still inside the window), <c>window</c> (the engine's window edge: out of the window, onto another monitor, the OS leave's park, or a
        /// release outside after a held drag) or <c>blur</c> (the window lost activation; on a blur with the pointer in, the <c>window</c> line comes
        /// first). The machine drops the pointer holds, shows the cursor (Sync releases the override) and hides the chrome after its leave debounce;
        /// the counted Menu hold, a scrub and a stop still pin it. The last move's own "over the picture" answer is forgotten, so nothing decides
        /// "still inside" from it until a real move comes back (that move is the machine's ENTER).</summary>
        internal void Leave(string source)
        {
            _movedOverRegion = false;
            _lastPointer = new(float.NaN, float.NaN);   // a move back at the same spot is a real enter
            _idle.PointerLeft(Now());
            Log.Info(Diagnostics.Category, $"[stage.chrome] exit as=left src={source} ptr={PointerText()}");
            Sync();
        }

        /// <summary>The pointer is over what counts (the picture in Theatre, else the stage), for a hold or a wheel (a leave is decided by
        /// <see cref="PointerAt"/> from a real position, never here). Without a window position the last move's own answer stands (the move that
        /// carried the hover was classified by <see cref="PointerAt"/>); before any move, nothing is over the picture.</summary>
        bool PointerOverRegion()
            => RegionOf() is not { } r
            || (_hooks?.GetPointerPosition?.Invoke() is { } p
                ? Inside(r, p) || !_idle.PointerOutsideRegion && _movedOverRegion && Inside(LeaveRegionOf(r), p)
                : _idle.Region == "picture" && !_idle.PointerOutsideRegion && _movedOverRegion);

        /// <summary>The pointer's window position for the `[stage.chrome]` lines ("x,y" or "none"). Edges only: it formats.</summary>
        string PointerText() => _hooks?.GetPointerPosition?.Invoke() is { } p ? $"{p.X:0},{p.Y:0}" : "none";

        /// <summary>One `[stage.chrome] hold` line per edge of the holds that can pin the chrome (OverControls, Scrubbing, Menu, Stopped), so a chrome
        /// that sticks names what pins it. Called from <see cref="Sync"/>; it writes only when a hold flipped.</summary>
        void LogHoldEdges(ChromeIdle vis)
        {
            const ChromeHold watched = ChromeHold.OverControls | ChromeHold.Scrubbing | ChromeHold.Menu | ChromeHold.Stopped;
            var holds = vis.Holds;
            var changed = (holds ^ _loggedHolds) & watched;
            _loggedHolds = holds;
            if (changed == ChromeHold.None) return;
            foreach (var h in s_loggedHolds)
                if ((changed & h) != 0)
                    Log.Info(Diagnostics.Category, $"[stage.chrome] hold {h}={((holds & h) != 0 ? 1 : 0)}{(h == ChromeHold.Stopped ? " phase=" + _playbackFact : "")} ptr={PointerText()} holds={ChromeHoldsText(holds)} menus={vis.MenuHolds}");
        }

        static readonly ChromeHold[] s_loggedHolds = [ChromeHold.OverControls, ChromeHold.Scrubbing, ChromeHold.Menu, ChromeHold.Stopped];

        static bool Inside(in RectF r, Point2 p) => p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.W && p.Y < r.Y + r.H;

        /// <summary>A hover exit is an in-window HANDOVER (an overlay or the other tree took the hover): the engine still has the pointer in this
        /// window and its position is a real point over the stage (window DIP; the stage fills the window). A missing position (a blur) or one
        /// outside the stage (an off-window move, the OS leave's park) is never read as "still inside": the window's edges decide those.</summary>
        bool PointerHandedOver() => _hooks?.IsPointerInWindow?.Invoke() == true && PointerOnStage();

        /// <summary>The pointer's window position is a real point over the stage (window DIP; the stage fills the window). A missing position
        /// (a blur) or one outside (an off-window move, the OS leave's park at -10000) is not.</summary>
        bool PointerOnStage()
        {
            if (_hooks?.GetPointerPosition?.Invoke() is not { } p) return false;
            var L = _ctx.Layout.Peek();
            return p.X >= 0f && p.Y >= 0f && p.X < L.W && p.Y < L.H;
        }
    }

    /// <summary>The idle chrome's holds as "none" or "Stopped|OverControls": the `[stage.chrome]` line (an edge, never a frame) and the
    /// inspector's `status`.</summary>
    internal static string ChromeHoldsText(ChromeHold h) => h == ChromeHold.None ? "none" : h.ToString().Replace(", ", "|");

    // ══ 1b. THE CORE LAYERS (each a component on the context's SIGNALS — SurfaceCore's tree is static, V-U50) ═════════
    // The backdrop, hero photo, face, scrim, spectrum, card, identity and artist name are the structure's since T7a2 (`Shell\Stage.Layers.cs`
    // builds their props); what stays here are the pane region's host (which pane, the KeepAlive; it mounts `Stage.Pane`, the lyrics view, T7b, and the `Queue.StagePane`
    // view, T7d) and the zero-size machinery.

    /// <summary>The pane region's core host: WHICH pane is shown and the KeepAlive that keeps each pane's state (the lyrics view's scroll,
    /// follow and measured rows) across mode switches, and the Artist pane's props cache. The region (its rect at (PaneX, PaneTop), its move
    /// with the cover, its clip, when it shows) and the frame each pane enters in are the structure's (<c>Stage.Pane</c>,
    /// <c>ClassicStagePane</c>).
    /// <para>THEATRE (<paramref name="theatre"/>): Video mode's panel beside the picture is a second host of its own, mounted only while Theatre
    /// shows: it keys on the STORED Theatre panel (Lyrics or Up next, never None, so a panel sliding out keeps its body until it is gone), the
    /// lyrics at the panel's type (<c>Layout.TheatreLyricsTypeScale</c>) and the Up next list in its narrow column form with the videos' thumbnails.</para></summary>
    [CoreHost]
    sealed class PaneHost(bool theatre = false) : Component
    {
        StageArtistPaneProps? _artist;   // the Artist pane's props: built once per pane host (the facts hold the bio length cache)
        StagePaneProps? _props;          // built once per stage context: the template re-renders on the context's signals it reads

        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            if (_props is null || !ReferenceEquals(_props.Stage, ctx))
                _props = new StagePaneProps { Stage = ctx, Panes = (frame, empty) => Panes(ctx, frame, empty), Theatre = theatre };
            return ViewSlots.View(ViewKeys.StagePane, _props);
        }

        // keyed by the PANE, not the mode: Lyrics mode and Large art share "pane:1", so ONE lyrics view (its scroll, follow
        // and measured rows) serves both and a switch between them re-keys nothing
        Element Panes(StageCtx ctx, Func<Element, Element> frame, Func<Element> empty)
            => theatre
            ? Flow.KeepAlive(() => ctx.TheatrePane.Value, static p => "theatre:" + (int)p, p => p switch
            {
                PaneKind.Lyrics => frame(Lyrics.StagePane(ctx.Slab.Accent, theatre: true)),
                _ => frame(Queue.StagePane(aside: true, video: true)),
            })
            : Flow.KeepAlive(() => ctx.Pane.Value, static p => "pane:" + (int)p, p => p switch
            {
                PaneKind.Lyrics => frame(Lyrics.StagePane(ctx.Slab.Accent)),
                PaneKind.Queue => frame(Queue.StagePane()),
                PaneKind.Artist => frame(ViewSlots.View(ViewKeys.StageArtistPane, _artist ??= ArtistPaneProps(ctx))),
                _ => empty(),
            });
    }

    /// <summary>Writes <c>ctx.HasTimedLyrics</c> and keeps the playing track's lyrics ensured — the ONE reader of
    /// <c>Lyrics.Store.Changed</c> on the stage (V-U50: the surface itself never subscribes to it).</summary>
    sealed class LyricFacts : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            _ = Lyrics.Store.Changed.Value;
            var track = ctx.RowValue();
            bool hasTimed = track.IsValid && Lyrics.Store.Doc(track) is { IsSynced: true, Lines.Count: > 0 };
            UseEffect(() => ctx.HasTimedLyrics.SetIfChanged(hasTimed), DepKey.From(hasTimed));
            UseEffect(() => { if (track.IsValid) Lyrics.Store.Ensure(track); }, DepKey.From(track.Slot));
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

}
