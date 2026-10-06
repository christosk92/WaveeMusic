// ── Shell/Stage.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// StageCtx (the stage's signals), SurfaceCore (fullscreen, focus scope, idle, accent, the track-key signal), Backdrop, Hero
// (the morphing cover), PaneHost (Lyrics · Up next · Artist under KeepAlive), the lyric caption (previous · active with the
// karaoke wipe and bloom · next, the break dots), ChromeHost → TopBar, TransportCard, GalleryHost, the hairline, the
// TeachingTip, Open/Close
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 1600 lines (the three-line lyric caption grew it past the plan's 1250)
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §3, §4.10
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FULLSCREEN STAGE. `Stage.View()` is what the shell mounts while `Shell.Ui.ImmersiveLyrics` is up (Shell.UI.cs:500).
// Mount = enter: the window goes borderless-fullscreen (the Video.UI.cs:1213-1218 shape), the shell collapses its chrome and
// body (FrameRules.ChromeMounted → bound Visible on the chrome row, the content region and the dock), a focus scope takes the keys. Unmount = exit.
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
//
// Mounted from other files: `Lyrics.StagePane(accent)` (the reading column), `Stage.QueuePaneBody` (Queue.UI.cs:65),
// `Stage.NowPlayingMenu` (Actions.UI.cs:958), `Shell.SeekBar(onScrubbing:)`/`Shell.TimeText` (the bar's own seek — never a fork),
// `Shell.PlayerKey`/`Shell.BarKeyStep` (the bar's key map and its keyboard seek ladder).

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Design.StageInk;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Stage
{
    // ══ 0. MOUNT POINT + SEAMS ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The fullscreen stage. Mount it while <c>Shell.Ui.ImmersiveLyrics</c> is true (Shell.UI.cs:500).</summary>
    public static Element View() => Embed.Comp(static () => new SurfaceCore());

    public static EnterExit EnterTerminal => Design.Reduced
        ? new EnterExit(Opacity: 0f, Active: true) : new EnterExit(Sx: 1.03f, Sy: 1.03f, Opacity: 0f, Active: true);
    public static EnterExit ExitTerminal => Design.Reduced
        ? new EnterExit(Opacity: 0f, Active: true) : new EnterExit(Sx: 1.02f, Sy: 1.02f, Opacity: 0f, Active: true);

    /// <summary>The Up-next rows (Queue.UI.cs:65 installs it). Null renders the empty sentence.</summary>
    public static Func<Element>? QueuePaneBody { get; set; }
    /// <summary>The now-playing context menu (Actions.UI.cs:958 installs it); asked at OPEN time.</summary>
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
        public readonly Signal<Layout> Layout = new(Stage.Layout.Seed(1920f, 1080f));
        public readonly Signal<Mode> Mode = new(Stage.Mode.Lyrics);
        public readonly Signal<Visualizer.Kind> Kind = new(Visualizer.Catalog.Default);
        public readonly Signal<bool> GalleryOpen = new(true), LyricsOverlay = new(true), Calm = new(false), Moments = new(true);
        /// <summary>The gallery is open AND the layout has room for it (<c>Layout.ShowGallery</c>) — the pane's mount gate
        /// minus the mode; the visualizer clock's lease and ribbon gate read it (SurfaceCore writes it). The idle chrome is
        /// NOT part of it: an open gallery stays until it is closed, because the face is laid out beside it
        /// (<c>Layout.FaceRight</c>) and hiding the pane with the chrome left the face clipped beside an empty column.</summary>
        public readonly Signal<bool> GalleryShown = new(true);
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
        public Action<Mode> SetMode = static _ => { };
        public Action ToggleGallery = static () => { }, Exit = static () => { }, Activity = static () => { };
        /// <summary>The idle machine's holds: pointer over a control · a menu/tip/flyout open (counted) · a seek scrub.</summary>
        public Action<bool> OverControls = static _ => { }, MenuOpen = static _ => { }, Scrubbing = static _ => { };
        /// <summary>The playing track's row from the key — <c>Value</c> subscribes the caller to the KEY (one write per slot/version change).</summary>
        public Track RowValue() { int slot = (int)(TrackKey.Value >> 32); return slot > 0 ? new Track(slot) : default; }
        public Track RowPeek() { int slot = (int)(TrackKey.Peek() >> 32); return slot > 0 ? new Track(slot) : default; }
    }
    public static readonly Context<StageCtx?> StageContext = new(null);

    /// <summary>Enter from any entry point. <paramref name="begin"/> is the caller's <c>UseContext(SharedTransition.Begin)</c>
    /// (null when headless): the bar art's flight is captured BEFORE the mount so the hero receives it. Nothing playing opens
    /// an EMPTY stage, exactly as the rail ⛶ does today (V-U53).</summary>
    public static void Open(Action<string>? begin, string cause)
    {
        if (!Entry.CanEnter(Video.State.Resolved == Video.SurfacePlacement.Fullscreen)) return;
        if (Shell.Ui.ImmersiveLyrics.Peek()) return;
        begin?.Invoke(Entry.MorphKey);
        Shell.Ui.ImmersiveLyrics.Value = true;
        Diagnostics.NoteEnter(cause);
    }

    /// <summary>Exit. The explicit <c>Begin</c> is REQUIRED: reverse capture is not implemented (ConnectedAnimation.cs:238-241),
    /// so the hero is captured here and the re-mounted bar art receives the flight.</summary>
    public static void Close(Action<string>? begin, string cause)
    {
        if (!Shell.Ui.ImmersiveLyrics.Peek()) return;
        begin?.Invoke(Entry.MorphKey);
        Shell.Ui.ImmersiveLyrics.Value = false;
        Diagnostics.NoteExit(cause);
    }

    static ColorF Shade(float a) => Ink.Veil with { A = a };
    const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>A full-bleed, pass-through ZStack LAYER — every Flow.Show child and every layer component's ROOT is one. The
    /// boundary above it is a transparent anchor, a default flex COLUMN that mirrors AlignSelf/JustifySelf/size but never
    /// Margin (Reconciler.MirrorParticipation): inside it AlignSelf would act on the HORIZONTAL cross axis (the transport
    /// card parked at the top-right, the top bar shrunk to its content, the caption pushed right) and a root without Grow is
    /// only as tall as its content (the hero's titles slot collapsed to 0 and took the artist line with it). Stretch fills
    /// the anchor's width, Grow + Basis 0 its height; the PLACED box is this layer's direct child, where vertical = AlignSelf,
    /// horizontal = JustifySelf and the Margin is the offset (FlexLayout.ArrangeZStack). Decorative layers turn hit-testing off.</summary>
    static readonly BoxEl Layer = new()
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
        ZStack = true, HitTestPassThrough = true,
    };
    static readonly FormatCache<int> s_percent = FormatCache.Create<int>();

    // ══ 1. THE SURFACE ══════════════════════════════════════════════════════════════════════════════════════════════

    sealed class SurfaceCore : Component
    {
        readonly StageCtx _ctx = new();
        readonly FloatSignal _frac = new(0f);
        readonly Action _onWake;
        PlayerChromeVisibility? _idle;        // created in Render with the host timer clock (MediaPlayerElement.cs:729-730) — never with 0 (V-U10)
        TimerHandle _wake;
        double _armedDueMs = double.PositiveInfinity;
        int _menusOpen;                       // the device flyout, the "…" menu and the TeachingTip each count one (V-U22)
        InputHooks? _hooks;
        Action<string>? _begin;
        NodeHandle _root;
        bool _cursorHidden, _paletteSeeded;
        OverlayHandle? _tip, _options;

        public SurfaceCore()
        {
            _onWake = () => { _armedDueMs = double.PositiveInfinity; Sync(); };
            _ctx.SetMode = m => { Prefs.Stage.SetMode((int)m); Diagnostics.NoteMode(m); };
            _ctx.ToggleGallery = () =>
            {
                // The gallery exists only in the Card layout: from a layout (or from another mode while a layout is stored) G goes
                // back to Card AND opens it — ToggleGallery alone would CLOSE a gallery whose stored flag is still true.
                bool layoutStored = _ctx.Look.Peek().IsLayout || (_ctx.Mode.Peek() != Mode.Visualizer && _ctx.LayoutPref.Peek() != VizLayout.Card);
                if (layoutStored)
                {
                    Prefs.Stage.SetLayout((int)VizLayout.Card);
                    if (_ctx.Mode.Peek() != Mode.Visualizer) Prefs.Stage.SetMode((int)Mode.Visualizer);
                    Prefs.Stage.SetGalleryOpen(true);
                    return;
                }
                var (mode, open) = ModeRules.ToggleGallery(_ctx.Mode.Peek(), _ctx.GalleryOpen.Peek());
                if (mode != _ctx.Mode.Peek()) Prefs.Stage.SetMode((int)mode);
                Prefs.Stage.SetGalleryOpen(open);
            };
            _ctx.Exit = () => Close(_begin, "exit-button");
            _ctx.Activity = () => { _idle?.Activity(ChromeActivity.Pointer, Now()); Sync(); };
            _ctx.OverControls = over => { _idle?.SetPointerOverControls(over, Now()); Sync(); };
            _ctx.MenuOpen = open => { _menusOpen = Math.Max(0, _menusOpen + (open ? 1 : -1)); _idle?.SetMenuOpen(_menusOpen > 0, Now()); Sync(); };
            _ctx.Scrubbing = on => { _idle?.SetScrubbing(on, Now()); Sync(); };
        }

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            _hooks = hooks;
            var overlay = UseContext(Overlay.Service);
            _begin = UseContext(SharedTransition.Begin);
            var vp = UseContextSignal(Viewport.Size);
            var ctx = _ctx;
            // the options popover: anchored under the sliders button (the TeachingTip's anchor too), light-dismiss, holding the chrome while open
            ctx.OpenOptions = () =>
            {
                if (_options is { IsOpen: true } open) { open.Close(); return; }
                var opened = overlay.Open(() => ctx.GalleryButton.Peek(), () => OptionsContent(ctx, () => _options?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
                ctx.MenuOpen(true);
                ctx.OptionsOpen.Value = true;
                opened.ClosedAction = () => { _options = null; ctx.OptionsOpen.Value = false; ctx.MenuOpen(false); };
                _options = opened;
            };

            // ── the ONE layout signal, equality-gated (Signal<T>.SetIfChanged) ──
            UseSignalEffect(() => { var size = vp.Value; ctx.Layout.SetIfChanged(Layout.Resolve(size.Width, size.Height, ctx.Layout.Peek())); });
            // ── the gallery's "shown" gate, narrowed to one bool (a resize re-runs this, the clock's lease only on a flip): open, room
            //    for it, and a layout that has a gallery (only Card does) ──
            UseSignalEffect(() => ctx.GalleryShown.SetIfChanged(ctx.GalleryOpen.Value && ctx.Layout.Value.ShowGallery && LayoutRules.GalleryAllowed(ctx.Look.Value.Eff)));

            // ── EVERY preference, once per Prefs.Stage epoch, into the context's signals (O8) — the only Prefs.Stage reads on the stage ──
            UseSignalEffect(() =>
            {
                var m = (Mode)Prefs.Stage.Mode();
                ctx.Mode.SetIfChanged(m);
                Diagnostics.NoteMode(m);                          // gated: logs only on a real change, keeps the card's mode truthful
                ctx.Kind.SetIfChanged((Visualizer.Kind)Prefs.Stage.Visualizer());
                ctx.GalleryOpen.SetIfChanged(Prefs.Stage.GalleryOpen());
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

            // ── the layout axis, derived (each effect reads only what the previous wrote): the effective layout, whether the caption
            //    line shows, the ONE Look the geometry takes, the pane region's pane, and the lyrics view's visibility gate ──
            UseSignalEffect(() => ctx.Effective.SetIfChanged(LayoutRules.Effective(ctx.LayoutPref.Value, ctx.Layout.Value.Aspect, ctx.HeroFact.Value)));
            UseSignalEffect(() => ctx.CaptionOn.SetIfChanged(LayoutRules.ShowsCaption(ctx.Mode.Value, ctx.Effective.Value, ctx.LyricsOverlay.Value,
                ctx.HasTimedLyrics.Value, ctx.Layout.Value.ShowPane, ctx.Kind.Value)));
            UseSignalEffect(() =>
            {
                var look = LayoutRules.LookOf(ctx.Mode.Value, ctx.Effective.Value, ctx.SpectrumPref.Value, ctx.HeroLyricsPref.Value, ctx.CaptionOn.Value);
                ctx.Look.SetIfChanged(look);
                Diagnostics.NoteLayout(look.Eff);
            });
            UseSignalEffect(() =>
            {
                var look = ctx.Look.Value;
                ctx.Pane.SetIfChanged(LayoutRules.Pane(look.Mode, look.Eff, look.HeroLyrics));
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

            // ── REAL fullscreen with prior-state restore (Video.UI.cs:1213-1218) ──
            var priorFs = UseRef(false);
            UseLayoutEffect(() =>
            {
                priorFs.Value = hooks.IsWindowFullscreen?.Invoke() ?? false;
                if (!priorFs.Value) hooks.WindowSetFullscreen?.Invoke(true);
                return () => { if (!priorFs.Value) hooks.WindowSetFullscreen?.Invoke(false); };
            }, DepKey.Empty);

            // ── a focus scope at the root; focus returns to whoever opened the stage (Video.UI.cs:1221-1236) ──
            var priorFocus = UseRef<NodeHandle>(default);
            UseLayoutEffect(() =>
            {
                if (Context.HostNode.IsNull) return null;
                var root = Context.HostNode;
                priorFocus.Value = hooks.GetFocus?.Invoke() ?? default;
                hooks.PushFocusScope?.Invoke(root);
                hooks.FocusNode?.Invoke(root, false);
                return () =>
                {
                    hooks.PopFocusScope?.Invoke(root);
                    var back = priorFocus.Value;
                    priorFocus.Value = default;
                    if (!back.IsNull) hooks.RestoreFocus?.Invoke(back);
                };
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
            }, DepKey.Empty);

            // ── idle: the engine machine on the HOST TIMER clock, one timer re-armed from NextWakeMs (MediaPlayerElement.cs:390-416, :726-730) ──
            _wake = UseTimeout(_onWake, 1_000f, DepKey.Empty);                           // arms once at mount — harmless (Sync re-arms)
            var idle = _idle ??= new PlayerChromeVisibility(PlayerChromeTiming.Default, _wake.NowMs);
            bool playing = Playback.IsPlaying.Value;
            UseEffect(() => { idle.SetPlayback(playing ? ChromePlayback.Playing : ChromePlayback.Paused, Now()); idle.SetCursorMayHide(true, Now()); Sync(); }, DepKey.From(playing));

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
            var fade = new EnterExit(Opacity: 0f, Active: true);
            return Ctx.Provide(StageContext, ctx, new BoxEl
            {
                Width = L.W, Height = L.H, ZStack = true, ClipToBounds = true, Focusable = true,   // explicit size (V-U49); the floor is the Backdrop's
                OnRealized = h => _root = h,
                OnHoverMove = p => { idle.PointerMoved(p.X, p.Y, Now()); Sync(); },
                OnPointerExit = () => { idle.PointerLeft(Now()); Sync(); },
                OnPointerWheel = _ => { idle.Activity(ChromeActivity.Pointer, Now()); Sync(); },   // wheel = activity (V-U22)
                OnKeyDown = OnKey,
                // Never leave focus NULL while the stage is up: an unhandled Escape clears it and disarms the key map (Video.UI.cs:1255-1259).
                OnFocusChanged = got =>
                {
                    if (got || _root.IsNull) return;
                    if ((hooks.GetFocus?.Invoke() ?? default).IsNull) hooks.FocusNode?.Invoke(_root, false);
                },
                Children =
                [
                    Embed.Comp(static () => new Backdrop()) with { Key = "stage:backdrop" },
                    // the Artist layout's photo + scrims (the backdrop is hidden under an opaque header, see Backdrop)
                    Flow.Show(() => ctx.Mode.Value == Mode.Visualizer && ctx.Look.Value.Eff == VizLayout.Artist, Layer with
                    {
                        Key = "stage:hero", HitTestVisible = false, HitTestPassThrough = false,
                        Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                        Children = [Embed.Comp(static () => new HeroLayer())],
                    }),
                    // the face: the Show's child carries its own Enter/Exit (the bare-ComponentEl rule, §1.8 — V-U31); FaceHost keys a CHILD per kind
                    Flow.Show(() => ctx.Mode.Value == Mode.Visualizer && LayoutRules.ShowsFace(ctx.Look.Value.Eff), new BoxEl
                    {
                        Key = "stage:face", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Enter = new EnterExit(Sx: 0.97f, Sy: 0.97f, Opacity: 0f, Active: true), Exit = fade, Transition = MotionTok.StandardEnter,
                        Children = [Embed.Comp(static () => new FaceHost())],
                    }),
                    // bottom smoke under the face: BOTTOM (AlignSelf) across the width (JustifySelf) — V-U9
                    new BoxEl
                    {
                        Key = "stage:smoke", Height = Tone.SmokeH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(1f, Shade(Tone.SmokeA))),
                        Visible = Prop.Of(() => ctx.Mode.Value == Mode.Visualizer && LayoutRules.ShowsFace(ctx.Look.Value.Eff)),
                    },
                    // the layouts' spectrum: a strip / ring in its own small RepaintBoundary (Card draws none — the face is the picture)
                    Embed.Comp(static () => new SpectrumLayer()) with { Key = "stage:spectrum" },
                    Embed.Comp(static () => new NowPlayingCard()) with { Key = "stage:npc" },
                    Embed.Comp(static () => new Hero()) with { Key = "stage:identity" },
                    Flow.Show(() => ctx.Mode.Value == Mode.Visualizer && ctx.Look.Value.Eff == VizLayout.Artist && !ctx.Look.Value.HeroLyrics, Layer with
                    {
                        Key = "stage:artistname", HitTestVisible = false, HitTestPassThrough = false,
                        Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                        Children = [Embed.Comp(static () => new ArtistNameLayer())],
                    }),
                    Embed.Comp(static () => new PaneHost()) with { Key = "stage:pane" },
                    // the lyric caption: a full-bleed LAYER (the Show's child carries the fade); CaptionHost places its block from the
                    // allocator's caption geometry (Layout.CaptionX/W/Bottom) — centred in the face's region, above the transport
                    Flow.Show(() => ctx.CaptionOn.Value, Layer with
                    {
                        // The caption animates every frame (its clock is per-frame) over the still scrim and art: a
                        // RepaintBoundary keeps that motion in its own slice (tiles over the text only) instead of re-
                        // rastering the full-window root tiles under it on every frame.
                        Key = "stage:caption", HitTestVisible = false, HitTestPassThrough = false, RepaintBoundary = true,
                        Enter = new EnterExit(Dy: 12f, Opacity: 0f, Active: true), Exit = fade, Transition = MotionTok.ControlNormal,
                        Children = [Embed.Comp(static () => new CaptionHost())],
                    }),
                    Embed.Comp(static () => new ChromeHost()) with { Key = "stage:chrome" },
                    // the hairline while the chrome is hidden: BOTTOM edge (AlignSelf), full width (JustifySelf) as a direct child of
                    // the Show's layer — directly under the Show anchor its AlignSelf End read horizontally and left it 0 wide (V-U49)
                    Flow.Show(() => !ctx.Chrome.Value, Layer with
                    {
                        Key = "stage:hairline", HitTestVisible = false, HitTestPassThrough = false,
                        Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal,
                        Children =
                        [
                            new BoxEl
                            {
                                Height = Layout.HairlineH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 0, HitTestVisible = false,
                                Children = [new BoxEl { Grow = 1f, Height = Layout.HairlineH, Fill = Prop.Bind(ctx.Slab.Accent), TransformOriginX = 0f, TransformOriginY = 0.5f,
                                                        Transform = Prop.Of(() => Affine2D.Scale(_frac.Value, 1f)) }],
                            },
                        ],
                    }),
                    Embed.Comp(static () => new LyricFacts()) with { Key = "stage:facts" },
                    // artist-header lookups only while the Artist layout is the stored choice in Visualizer mode (Card never pays for them)
                    Flow.Show(() => ctx.Mode.Value == Mode.Visualizer && ctx.LayoutPref.Value == VizLayout.Artist, Embed.Comp(static () => new HeroFacts()) with { Key = "stage:herofacts" }),
                    Embed.Comp(static () => new Visualizer.Clock()) with { Key = "stage:clock" },
                ],
            });
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
            ColorF accentBase = Ink.Accent(url);
            ctx.Accent.SetIfChanged(AccentSet.From(accentBase));
            var palette = Visualizer.Palette.From(accentBase, Design.SchemeFor(url), Ink.IsDark);
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

        /// <summary>Esc / F11 close; [ / ] step the face (the layout inside a Large art / Centered / Artist layout), G the gallery (back in Card); everything else is the player bar's own key map (Shell.PlayerBar.UI.cs:645-659 via
        /// Shell.PlayerKey, Shell.PlayerBar.cs:448-459) so Space / ← → / ↑ ↓ work in fullscreen (O9). Media keys need nothing:
        /// they arrive through SMTC regardless of focus (Playback.Os.cs:309-323). Any key is idle activity.</summary>
        void OnKey(KeyEventArgs e)
        {
            _idle?.Activity(ChromeActivity.Keyboard, Now());
            Sync();
            if (e.Handled) return;
            if (e.Mods == KeyModifiers.None && e.KeyCode == Keys.Escape) { e.Handled = true; Close(_begin, "escape"); return; }
            if (e.Mods == KeyModifiers.None && e.KeyCode == Keys.F11) { e.Handled = true; Close(_begin, "f11"); return; }
            // [ / ] the previous / next face (Shift: the group) in Visualizer mode; G the gallery (from another mode it
            // switches to Visualizer first — ModeRules.ToggleGallery)
            int step = e.Ctrl || e.Alt || _ctx.Mode.Peek() != Mode.Visualizer ? 0 : ModeRules.FaceStep(e.KeyCode);
            if (step != 0)
            {
                e.Handled = true;
                if (_ctx.Look.Peek().IsLayout)
                {
                    // inside a layout the keys walk the three layouts (the STORED one — an Artist without a header draws Large art but
                    // steps on from Artist); Card is entered and left through the options popover or G
                    Prefs.Stage.SetLayout((int)LayoutRules.Step(_ctx.LayoutPref.Peek(), step));
                    return;
                }
                var kind = _ctx.Kind.Peek();
                var next = e.Shift ? Visualizer.Catalog.StepGroup(kind, step) : Visualizer.Catalog.Step(kind, step);
                Prefs.Stage.SetVisualizer((int)next);
                Diagnostics.NotePick(next);
                return;
            }
            if (e.Mods == KeyModifiers.None && e.KeyCode == Keys.G && !e.IsRepeat) { e.Handled = true; _ctx.ToggleGallery(); return; }
            var intent = Shell.PlayerKey(e.KeyCode, true, e.Handled, e.Ctrl || e.Alt, e.Shift);
            if (intent == Shell.PlayerKeyIntent.None) return;
            e.Handled = true;
            switch (intent)
            {
                // The bar's keyboard scrub ladder (5 s → 15 s → 30 s while held, Shift = 1 s): the stage's own seek rail mounts its commit tick.
                case Shell.PlayerKeyIntent.SeekBack: Shell.BarKeyStep(-1, fine: false); break;
                case Shell.PlayerKeyIntent.SeekForward: Shell.BarKeyStep(1, fine: false); break;
                case Shell.PlayerKeyIntent.SeekBackFine: Shell.BarKeyStep(-1, fine: true); break;
                case Shell.PlayerKeyIntent.SeekForwardFine: Shell.BarKeyStep(1, fine: true); break;
                case Shell.PlayerKeyIntent.VolumeDown: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() - .05f, 0f, 1f)); break;
                case Shell.PlayerKeyIntent.VolumeUp: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() + .05f, 0f, 1f)); break;
                case Shell.PlayerKeyIntent.Toggle: if (!e.IsRepeat) Shell.TogglePlayPause("stage.space"); break;
            }
        }

        /// <summary>MediaPlayerElement.Sync (:390-416), verbatim in shape: tick (the bool is IGNORED — V-U10), publish the chrome
        /// value-gated, apply the cursor, re-arm the one timer at NextWakeMs (+∞ = nothing pending, no timer — V-U48).</summary>
        void Sync()
        {
            if (_idle is not { } vis) return;
            double now = _wake.NowMs;
            vis.Tick(now);
            _ctx.Chrome.SetIfChanged(vis.ChromeVisible);
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
    }

    // ══ 1b. THE LAYERS (each a component on the context's SIGNALS — SurfaceCore's tree is static, V-U50) ═════════════

    /// <summary>Backdrop: the baked cover (old layer kept under the new), the base Field (the palette's roles, cross-fading
    /// with it), the scrim (deeper under Verse — Tone.ScrimFor).</summary>
    sealed class Backdrop : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var mode = ctx.Mode.Value;
            var kind = ctx.Kind.Value;
            var eff = ctx.Look.Value.Eff;
            var track = ctx.RowValue();
            string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            return Layer with   // full height from Grow, not from whichever child happens to declare L.H
            {
                // ONE quarter-scale repaint boundary for the whole backdrop: the floor, the cover under an 80-DIP baked blur, the
                // Field's radial blobs and the flat scrim are all soft or flat, so they raster together into one small surface
                // and composite as ONE upsampled quad. Separately they were three window-sized layers composited every frame
                // (the Field drifts under all of them), and the bars' acrylic re-blurred all three.
                ClipToBounds = true, HitTestVisible = false, HitTestPassThrough = false, Fill = Ink.Floor,
                RepaintBoundary = true, RasterScale = 0.25f,
                // under an opaque header photo the quarter-scale blobs would raster for nothing: hide the whole backdrop (Artist layout, Visualizer mode, photo known)
                Visible = Prop.Of(() => !(ctx.Mode.Value == Mode.Visualizer && ctx.Look.Value.Eff == VizLayout.Artist && ctx.HeroFact.Value == HeroState.Header)),
                Children =
                [
                    Embed.Comp(new BackdropArt.Props(url), static () => new BackdropArt()),
                    Visualizer.BackdropField(ctx.Slab, L.W, L.H, Prop.Bind(ctx.Slab.BaseFieldOp), boundary: false) with { Key = "stage:field" },
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Fill = Shade(LayoutRules.ScrimFor(mode, eff, kind)), BrushTransitionMs = Tone.CrossFadeMs,   // a static fill cross-fades; a bound one would snap
                    },
                ],
            };
        }
    }

    /// <summary>The morphing hero + its title column (one node each, Layout FLIP). The wrapper is PASS-THROUGH, not
    /// hit-invisible: the MetaLink inside must stay clickable (V-U8).</summary>
    sealed class Hero : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var overlay = UseContext(Overlay.Service);
            var L = ctx.Layout.Value;
            var mode = ctx.Mode.Value;
            var set = ctx.Accent.Value;
            var track = ctx.RowValue();
            var k = ctx.Look.Value;                       // what is drawn: Card (the thumb) or a layout's cover + titles
            string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            float size = L.CoverSize(in k);
            float corners = CoverCorners(in k);
            int decode = size > 400f ? 1024 : 512;    // Spotify's largest cover is 640: the big covers decode at 1024, the rest at 512
            var flip = new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Spring(0.45f, 0.90f), SizeMode.ScaleCorrect);
            var move = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(0.45f, 0.90f));
            var (ts, tl) = L.TitleFont(in k);
            var meta = L.MetaFont(in k);
            int titleLines = L.TitleMaxLines(in k);       // two under the hero when the block clears the transport, one in the card
            bool center = L.TitlesCentered(in k);
            float titleW = L.TitleW(in k);
            int heroArtist = k.Eff == VizLayout.Artist && !k.HeroLyrics ? ctx.HeroArtist.Value : 0;
            string uri = track.IsValid ? track.Uri.Text : "";
            string title = track.IsValid ? track.Title : "";
            string shown = Transport.UsesTitle(title, uri) ? title : Loc.Get(Strings.Player.NothingPlaying);
            var titleKids = new List<Element>(3)
            {
                new TextEl(shown)
                {
                    Size = ts, LineHeight = tl, Weight = k.Eff is VizLayout.LargeArt or VizLayout.Centered ? (ushort)700 : (ushort)600, FontFamily = DisplayFace, Color = Ink.Ink,
                    Wrap = titleLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = titleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    MaxWidth = center ? titleW : float.NaN,
                },
                Embed.Comp(new MetaLink.Props(meta.Size, meta.Line, set.Text, center, heroArtist), static () => new MetaLink()),
            };
            if (L.ShowChipsFor(in k)) titleKids.Add(Embed.Comp(static () => new Chips()) with { Key = "stage:chips" });
            // FULL HEIGHT (Layer: Grow + Basis 0). Content-sized, this ZStack was only as tall as the cover, so the titles'
            // slot (H − TitleY) arranged to 0 and the MetaLink — the one Shrink = 1 child of that column — shrank to nothing.
            return Layer with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:cover", Width = size, Height = size, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(L.CoverX(in k), L.CoverY(in k), 0f, 0f), Corners = CornerRadius4.All(corners), Shadow = Elevation.Dialog, ClipToBounds = true,
                        Layout = flip, HitTestVisible = false,
                        Children = [Controls.Artwork(url.Length > 0 ? url : null, size, size, corners, morphKey: Entry.MorphKey, decodePx: decode)],
                    },
                    // The placed column fills its slot down to the stage's foot (a Start-aligned auto-height ZStack child does), so it
                    // is PASS-THROUGH; the identity block itself is the content-height inner column — title · meta · chips FLOW,
                    // so a two-line title pushes the meta and chips down instead of overlapping them.
                    new BoxEl
                    {
                        Key = "stage:titles", Direction = 1, Width = titleW, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(L.TitleX(in k), L.TitleY(in k), 0f, 0f), Layout = move, HitTestPassThrough = true,
                        Children =
                        [
                            new BoxEl { Direction = 1, Gap = L.TitleGapFor(in k), MinWidth = 0f, AlignItems = center ? FlexAlign.Center : FlexAlign.Stretch, Children = titleKids.ToArray() }
                                .WithContextMenu(overlay,                                        // right-click the identity = the "…" menu, as the old stage had
                                    () => { var m = NowPlayingMenu?.Invoke(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) ctx.MenuOpen(true); return m; },
                                    new ContextMenuOptions { OnClosed = _ => ctx.MenuOpen(false) }),
                        ],
                    },
                ],
            };
        }
    }

    /// <summary>The cover's corner radius per look: the board's 14 (Large art), 18 (Centered), 8 / 6 (the Artist hero's identity row); Card keeps
    /// the Mode's (the thumb's control radius, the hero's card radius).</summary>
    static float CoverCorners(in Look k) => k.Eff switch
    {
        VizLayout.LargeArt => Radii.Card,
        VizLayout.Centered => 18f,
        VizLayout.Artist => k.HeroLyrics ? 6f : 8f,
        _ => k.Mode == Mode.Visualizer ? Radii.Control : Radii.Card,
    };

    /// <summary>The format chip, "Synced lyrics", and (outside Visualizer mode) BPM from the Audio group. Its own component so
    /// the format / device / owner signals re-render THIS row only (V-U50).</summary>
    sealed class Chips : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var mode = ctx.Mode.Value;
            var track = ctx.RowValue();
            bool hasTimed = ctx.HasTimedLyrics.Value;
            var kids = new List<Element>(4);
            var fmt = Playback.StreamFormat.Value;
            _ = Playback.Devices.Changed.Value;                   // the roster is part of the remote verdict
            bool remote = Shell.DevicePicker.IsRemote(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, Playback.Devices.Rows);
            if (Transport.ShowsQualityBadge(!fmt.IsEmpty, remote)) kids.Add(Chip(Icons.Equalizer, Entities.Strings.Resolve(fmt)));
            if (hasTimed) kids.Add(Chip(Icons.Document, Loc.Get(Strings.Stage.SyncedLyrics)));
            if (mode != Mode.Visualizer && track.IsValid && track.Knows(TrackFields.Audio) && track.Tempo > 0)
                kids.Add(Chip(null, Strings.Stage.Bpm(((track.Tempo + 5) / 10).ToString(System.Globalization.CultureInfo.InvariantCulture))));   // a formatted key: the generated method already formats (V-D15)
            return new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Margin = new Edges4(0f, Spacing.S, 0f, 0f), HitTestVisible = false, Children = kids.ToArray() };
        }

        static BoxEl Chip(string? glyph, string text) => new()
        {
            Height = 24f, Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Shrink = 0f,
            Corners = Radii.ControlAll, Fill = Ink.GlassRest, BorderWidth = 1f, BorderColor = Ink.Stroke,
            Children = glyph is null
                ? [new TextEl(text) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary }]
                : [new TextEl(glyph) { Size = 12f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary }, new TextEl(text) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary }],
        };
    }

    /// <summary>The acrylic now-playing card behind the thumb (Visualizer mode): TOP-LEFT (AlignSelf Start · JustifySelf Start).</summary>
    sealed class NowPlayingCard : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, HitTestVisible = false,   // a ZStack, so the card's Align/Justify mean something
                Children =
                [
                    Flow.Show(() => ctx.Mode.Value == Mode.Visualizer && ctx.Layout.Value.ShowChips && LayoutRules.ShowsNowPlayingCard(ctx.Look.Value.Eff), new BoxEl
                    {
                        Width = Layout.NowPlayingCardW, Height = Layout.NowPlayingCardH, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(Layout.NowPlayingCardX, Layout.NowPlayingCardY, 0f, 0f), Corners = Radii.CardAll,
                        Acrylic = Ink.Card, Shadow = Elevation.Card, HitTestVisible = false,
                        Enter = new EnterExit(Dy: -8f, Opacity: 0f, Active: true, DelayMs: 150f), Exit = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
                    }),
                ],
            };
        }
    }

    /// <summary>The pane region: KeepAlive keeps the lyrics view's state across mode switches. TOP-LEFT at (PaneX, PaneTop).</summary>
    sealed class PaneHost : Component
    {
        static readonly LayoutTransition s_move = new(TransitionChannels.Position, TransitionDynamics.Spring(0.45f, 0.90f));
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var k = ctx.Look.Value;
            var slab = ctx.Slab;
            var pane = L.PaneRect(in k);
            return new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = pane.W, Height = pane.H, ClipToBounds = true,
                Margin = new Edges4(pane.X, pane.Y, 0f, 0f), Layout = s_move,   // the pane slides to its new rect with the cover (Lyrics ⇄ Large art)
                Visible = Prop.Of(() => ctx.Layout.Value.ShowPane && ctx.Pane.Value != PaneKind.None),
                Children =
                [
                    // keyed by the PANE, not the mode: Lyrics mode and Large art share "pane:1", so ONE lyrics view (its scroll, follow
                    // and measured rows) serves both and a switch between them re-keys nothing
                    Flow.KeepAlive(() => ctx.Pane.Value, static p => "pane:" + (int)p, p => p switch
                    {
                        PaneKind.Lyrics => PaneFrame(Lyrics.StagePane(slab.Accent)),
                        PaneKind.Queue => PaneFrame(Embed.Comp(static () => new QueuePane())),
                        PaneKind.Artist => PaneFrame(Embed.Comp(static () => new ArtistStage())),
                        _ => new BoxEl { HitTestVisible = false },
                    }),
                ],
            };
        }

        static Element PaneFrame(Element body) => new BoxEl
        {
            Grow = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
            Animate = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(500f, Easing.FluentDecelerate),
                Enter: new EnterExit(Dy: 40f, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
            Children = [body],
        };
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

    // ══ 2. THE BACKDROP ART (the crisp-flash fix) ═══════════════════════════════════════════════════════════════════

    /// <summary>The blurred cover is baked ONCE per track (BakedBlurSpec) and the PREVIOUS cover stays mounted under
    /// the new one, so a track change fades blur-to-blur and never shows a crisp placeholder.</summary>
    sealed class BackdropArt : Component
    {
        public sealed record Props(string Url);
        string _shown = "", _previous = "";
        public override Element Render()
        {
            var p = UseProps<Props>();
            if (p.Url != _shown) { _previous = _shown; _shown = p.Url; }
            var kids = new List<Element>(2);
            if (_previous.Length > 0) kids.Add(Cover(_previous) with { Key = "bd:" + _previous });
            if (_shown.Length > 0) kids.Add(Cover(_shown) with { Key = "bd:" + _shown });
            return Layer with { HitTestVisible = false, HitTestPassThrough = false, Children = kids.ToArray() };   // full height (Grow), so the stretched art fills the stage
        }
        static ImageEl Cover(string url) => Ui.Image(url, ImageFit.Cover, aspect: float.NaN, decodePx: 512f, corners: 0f, placeholder: Ink.ArtStandIn(url),
                                                      transition: ImageTransition.Fade(Tone.CrossFadeMs)) with
        {
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, BakedBlur = new BakedBlurSpec(80f, 0.5f),
        };
    }

    // ══ 3. THE FACE HOST ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The chosen face, remounted per (kind, 8-DIP width bucket) on a KEYED CHILD (a Key on a component's root is inert in a
    /// single-child slot — V-U11; the width is in the key so opening or closing the gallery re-centres the subject under the
    /// same 0.55 s FluentDecelerate instead of jumping), inset on the right while the gallery is open. The face is told its
    /// SAFE rect (Layout.FaceSafe: clear of the now-playing card, the caption when it shows, the transport). Reads the
    /// context's signals only (O8); the live palette re-renders it once per landed fade, never per tick.</summary>
    sealed class FaceHost : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var kind = ctx.Kind.Value;
            var pal = ctx.Palette.Value;
            bool galleryOpen = ctx.GalleryShown.Value;                  // open AND room AND the Card layout: the inset follows what is on screen
            float w = L.W - L.FaceRight(galleryOpen);
            bool caption = ctx.CaptionOn.Value;                         // in Card + Visualizer mode exactly ModeRules.ShowsCaption(...)
            var safe = L.FaceSafe(galleryOpen, caption);
            var track = ctx.RowValue();
            string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            // the stage face is a scope reader while its kind draws the slab's scope series (Scope, Flow)
            Visualizer.UseScopeReader(Context, ctx.Slab, Visualizer.Catalog.UsesScope(kind));
            return new BoxEl
            {
                Width = w, Height = L.H, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        // the width in the key is quantised to 8 DIP: the gallery's open/close still remounts (re-centres)
                        // the face, a window drag no longer rebuilds it (and its sims) on every pixel
                        Key = "viz:" + (int)kind + ":" + ((int)w >> 3), Width = w, Height = L.H,
                        Animate = new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Tween(550f, Easing.FluentDecelerate),
                            Enter: new EnterExit(Sx: 0.97f, Sy: 0.97f, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true)),
                        Children = [Visualizer.Face(kind, ctx.Slab, in pal, new Visualizer.FaceSpec(w, L.H, Preview: false, CoverUrl: url.Length > 0 ? url : null,
                            SafeLeft: safe.X, SafeTop: safe.Y, SafeRight: safe.Right, SafeBottom: safe.Bottom))],
                    },
                ],
            };
        }
    }

    // ══ 4. THE LYRIC CAPTION (over the face · Compact Lyrics mode's lyric line) ═════════════════════════════════════

    /// <summary>The lyric over the visualizer, presented as three lines: the PREVIOUS line above (small, ≈35 %), the ACTIVE line
    /// at the hero-title scale (40–56 DIP by height) and the NEXT line below (small, ≈45 %), centred in the face's region at the
    /// allocator's caption geometry (<c>Layout.CaptionX/W/Bottom</c> — the lower third, above the transport and hairline) over a
    /// soft elliptical veil, never a plate.
    /// <list type="bullet">
    /// <item>The active line carries the lyrics view's karaoke WIPE (<c>Wipe.ComputeSplit</c> + <c>LeadFrac</c>, its DIP feather
    /// over the measured run, its lift — LineRow's recipe) and a blurred accent BLOOM layer that follows the same split; a
    /// line-synced line has no wipe and fades in whole. No other line is blurred.</item>
    /// <item>A real instrumental break (and a long intro) shows the lyrics view's three breathing dots in the active slot
    /// (<c>Interlude.Progress/Pulse/DotAlpha/BreathScale</c>), until the next line resolves (<c>Stage.Caption.View</c>).</item>
    /// <item>Every slot is KEYED by its role + line index (+ the document's generation), so a hand-off plays the Exit (up and
    /// out) on the outgoing line and the Enter (up from 16 DIP below) on the incoming one — StandardEnter, 300 ms; reduced
    /// motion keeps only the cross-fade.</item>
    /// <item>While playing, the RENDER THREAD poses the active line's wipe (<c>AnimChannel.GlyphWipeSplit</c> keyframes,
    /// <c>Lyrics.Wipe.SplitKeyframes</c> — the exact split function, seeded once per line and again when a position report
    /// moves the clock), the lyrics view's channel; the UI wakes once per view EDGE (<c>Caption.NextEdgeMs</c>) and per
    /// report, not per frame. A break's dots still tick per frame (scene columns only); paused, a 4 Hz interval writes the
    /// split itself. A render happens only when the packed view (line + break edge) changes.</item>
    /// </list></summary>
    sealed class CaptionHost : Component
    {
        const int Unresolved = int.MinValue;
        /// <summary>The packed view (<c>Stage.Caption.Pack</c>) — the ONE signal the render subscribes to; the tick writes it on change.</summary>
        readonly Signal<int> _view = new(Unresolved);
        readonly FloatSignal[] _dotAlpha = new FloatSignal[Lyrics.Interlude.DotCount];
        readonly Action _tick;
        readonly Action<NodeHandle> _onMain, _onGlow, _onDots;
        Deck.PositionInterpolator _pos;
        bool _anchored, _advancing;
        int _lastReport = int.MinValue;
        Lyrics.Doc? _doc;
        int _docGen;                          // bumps per document, so a new track's line 5 is a NEW keyed slot (its wipe nodes re-report)
        // the active line's two wipe nodes (main + bloom), the line they were built for, its measured run and the wrap width
        NodeHandle _mainNode, _glowNode, _dotsNode;
        int _wipeLine = -1;
        float _runLen = float.NaN, _wrapW = float.NaN;
        // The render-thread wipe rows (CaptionWipeRows).
        readonly CaptionWipeRows _rows = new();
        // The one-shot wake at the next view edge (Caption.EdgeWake, published to CaptionWake), the timer's callback, and
        // whether the report effect has had its mount run.
        readonly Signal<Lyrics.MotionWake> _edgeWake = new(new Lyrics.MotionWake(0, -1f));
        Caption.EdgeWake _edge;
        int _edgeSeq;
        readonly Action _onEdge;
        bool _reportSubscribed;

        public CaptionHost()
        {
            for (int k = 0; k < _dotAlpha.Length; k++) _dotAlpha[k] = new FloatSignal(0f);
            _tick = () => Reactive.Untrack(Tick);
            _onEdge = () => { _edge.Fire(); _tick(); };
            _onMain = h => _mainNode = h;
            _onGlow = h => _glowNode = h;
            _onDots = h => _dotsNode = h;
        }

        static EnterExit SlotEnter => new(Dy: Design.Reduced ? 0f : Tone.CaptionRiseDip, Opacity: 0f, Active: true);
        static EnterExit SlotExit => new(Dy: Design.Reduced ? 0f : -Tone.CaptionRiseDip, Opacity: 0f, Active: true);

        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var k = ctx.Look.Value;
            bool galleryShown = ctx.GalleryShown.Value;
            var set = ctx.Accent.Value;
            _ = Lyrics.Store.Changed.Value;
            var track = ctx.RowValue();
            var doc = track.IsValid ? Lyrics.Store.Doc(track) : null;
            if (doc is not null && (!Lyrics.IsTimed(doc) || doc.Lines.Count == 0)) doc = null;
            if (!ReferenceEquals(doc, _doc)) { _doc = doc; _docGen++; _wipeLine = -1; _runLen = float.NaN; }
            bool playing = Playback.IsPlaying.Value;
            UseSignalEffect(() =>
            {
                int pos = Playback.PositionMs.Value;
                bool advancing = Playback.IsPlaying.Value && !Playback.Buffering.Value;
                // re-anchor on every report AND on a play/pause edge (Deck.PositionInterpolator.Anchor): a resume must not
                // extrapolate across the paused gap for a tick
                if (pos != _lastReport || advancing != _advancing || !_anchored) { _pos.Anchor(Design.FrameTime.NowMs, pos); _anchored = true; _lastReport = pos; }
                _advancing = advancing;
                // a report (or a play/pause edge) re-times the render-thread wipe and the edge wake; never on the mount run,
                // which runs inside this render
                if (!_reportSubscribed) { _reportSubscribed = true; return; }
                _tick();
            });
            // A 4 Hz interval paused (a seek still lands). Playing, the render thread poses the wipe and the UI wakes at the next
            // view edge (CaptionWake); only a break's breathing dots - or a host whose render thread does not own the
            // compositor - tick per frame (the ticker child below is mounted only then).
            bool renderOwned = Context.Anim is { RenderOwnsCompositor: true };
            UseInterval(_tick, 250f, enabled: _doc is not null && !playing);

            // The render resolves the view itself (so a new document never shows the old one's indices for a tick) and
            // SUBSCRIBES to the tick's signal for the next change.
            int view = _view.Value;
            if (_doc is { } d0 && _anchored) view = ComputeView(d0, Now(), out _, out _);
            IReadOnlyList<Lyrics.Line> lines = _doc?.Lines ?? Array.Empty<Lyrics.Line>();
            int count = view == Unresolved ? 0 : lines.Count;
            int anchor = Caption.AnchorOf(view);
            bool dots = count > 0 && Caption.DotsOf(view);
            bool perFrame = _doc is not null && playing && (dots || !renderOwned);
            Element[] ticker = perFrame ? [Embed.Comp(() => new CaptionFrames(_tick))]
                : _doc is not null && playing ? [Embed.Comp(() => new CaptionWake(this))] : [];
            var (prevI, centreI, nextI) = count > 0 ? Caption.Slots(anchor, dots, L.CaptionContextFor(in k), count) : (-1, -1, -1);

            float capW = L.CaptionWFor(in k, galleryShown), capX = L.CaptionXFor(in k, galleryShown), capBottom = L.CaptionBottomFor(in k);
            var (aSize, aLine) = L.CaptionFont;
            var (cSize, cLine) = L.CaptionContextFont;
            // the wipe nodes belong to ONE line: a new centre re-keys the slot, whose fresh text nodes report through OnRealized
            if (centreI != _wipeLine) { _wipeLine = centreI; _mainNode = default; _glowNode = default; _runLen = float.NaN; }
            if (capW != _wrapW) { _wrapW = capW; _runLen = float.NaN; }
            if (!dots) _dotsNode = default;
            // after the commit that realized this view's nodes: seed (or hand back) the wipe and re-arm the edge wake
            UseEffect(_tick, DepKey.From(HashCode.Combine(view, centreI, _docGen, playing, capW)));

            string gen = _docGen.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var kids = new List<Element>(3);
            if (prevI >= 0 && lines[prevI].Text.Length > 0)
                kids.Add(ContextLine(lines[prevI].Text, "cap:p:" + gen + ":" + prevI, cSize, cLine, Tone.CaptionPrevA, capW));
            if (dots) kids.Add(DotsSlot("cap:d:" + gen + ":" + anchor, aLine));
            else if (centreI >= 0 && lines[centreI].Text.Length > 0)
                kids.Add(ActiveSlot(lines[centreI], "cap:a:" + gen + ":" + centreI, aSize, aLine, cSize, cLine, set.Text, capW, _anchored ? Now() : 0L));
            if (nextI >= 0 && lines[nextI].Text.Length > 0)
                kids.Add(ContextLine(lines[nextI].Text, "cap:n:" + gen + ":" + nextI, cSize, cLine, Tone.CaptionNextA, capW));

            return Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    // the veil: a wide ELLIPSE behind the block at its tallest, feathered to nothing (radial) — legibility over a
                    // bright face without a plate; BOTTOM-anchored around the block (vertical = AlignSelf, horizontal = JustifySelf)
                    new BoxEl
                    {
                        Width = capW + 2f * Layout.CaptionVeilPadX, Height = L.CaptionBlockMaxHFor(in k) + 2f * Layout.CaptionVeilPadY,
                        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                        Margin = new Edges4(capX - Layout.CaptionVeilPadX, 0f, 0f, MathF.Max(0f, capBottom - Layout.CaptionVeilPadY)),
                        Gradient = RadialGradient(new GradientStop(0f, Shade(Tone.CaptionVeilA)), new GradientStop(0.55f, Shade(Tone.CaptionVeilA * 0.55f)), new GradientStop(1f, Shade(0f))),
                    },
                    // the block: previous · active (or the dots) · next, centred in its region, bottom-anchored above the transport
                    new BoxEl
                    {
                        Width = capW, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Margin = new Edges4(capX, 0f, 0f, capBottom),
                        Direction = 1, AlignItems = FlexAlign.Center, Gap = Layout.CaptionLineGap, HitTestVisible = false,
                        Children = kids.ToArray(),
                    },
                    .. ticker,
                ],
            };
        }

        /// <summary>The caption's per-frame tick (<see cref="Controls.FrameTicker"/>), named for the <c>[wake]</c> census.</summary>
        sealed class CaptionFrames(Action tick) : Controls.FrameTicker(tick);

        /// <summary>A context line (previous / next): small, one line, the stage ink at a low alpha — never blurred.</summary>
        static Element ContextLine(string text, string key, float size, float line, float alpha, float maxW) => new BoxEl
        {
            Key = key, Direction = 1, AlignItems = FlexAlign.Center, MaxWidth = maxW, HitTestVisible = false,
            Enter = SlotEnter, Exit = SlotExit, Transition = MotionTok.StandardEnter,
            Children = [new TextEl(text) { Size = size, LineHeight = line, Weight = 600, FontFamily = DisplayFace, Color = Ink.Ink with { A = alpha }, Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
        };

        /// <summary>The active line: a ZStack of [the accent bloom (blurred, low alpha), the sung/unsung text], both carrying the
        /// SAME wipe for a word-synced line (the halo follows the voice, LineRow's recipe); a line-synced line is plain ink and
        /// fades in whole with its slot. The translation / romanization, when chosen, sits under it at the context size.</summary>
        Element ActiveSlot(Lyrics.Line line, string key, float size, float lineH, float subSize, float subLine, ColorF bloom, float maxW, long now)
        {
            ColorF sung = Ink.Ink, unsung = Ink.Ink with { A = Tone.CaptionUnsungA };
            TextEl main = LineText(line.Text, size, lineH, sung), glow = LineText(line.Text, size, lineH, bloom);
            if (line.IsWordByWord && line.Syllables.Count > 0)
            {
                float split = SplitAt(line, now);
                float soft = Lyrics.Wipe.SoftnessOfLine(float.IsNaN(_runLen) ? MathF.Max(1f, maxW) : _runLen, large: true);
                float lift = Lyrics.Wipe.LiftFor(large: true, Design.Reduced);
                float run = float.IsNaN(_runLen) ? 0f : _runLen;   // the run the render thread steps the split along
                main = main with { Wipe = new GlyphWipe(sung, unsung, split, soft, lift) { Run = run }, OnRealized = _onMain };
                glow = glow with { Wipe = new GlyphWipe(bloom, bloom with { A = 0f }, split, soft, lift) { Run = run }, OnRealized = _onGlow };
            }
            var stack = new BoxEl
            {
                ZStack = true, HitTestVisible = false,
                Children =
                [
                    new BoxEl { Direction = 1, Blur = Tone.CaptionBloomSigma, Opacity = Tone.CaptionBloomA, HitTestVisible = false, Children = [glow] },
                    main,
                ],
            };
            string? secondary = Prefs.Lyrics.SecondaryLine() switch { Prefs.Lyrics.Translation => line.Translation, Prefs.Lyrics.Romanization => line.Romanization, _ => null };
            return new BoxEl
            {
                Key = key, Direction = 1, AlignItems = FlexAlign.Center, Gap = 4f, MaxWidth = maxW, HitTestVisible = false,
                Enter = SlotEnter, Exit = SlotExit, Transition = MotionTok.StandardEnter,
                Children = secondary is { Length: > 0 }
                    ? [stack, new TextEl(secondary) { Size = subSize, LineHeight = subLine, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
                    : [stack],
            };
        }

        static TextEl LineText(string text, float size, float line, ColorF color) => new(text)
        {
            Size = size, LineHeight = line, Weight = 600, FontFamily = DisplayFace, Color = color,
            Wrap = TextWrap.Wrap, MaxLines = Layout.CaptionActiveMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

        /// <summary>The break's three breathing dots in the active slot (the lyrics view's dots: size, gap, ink); one active
        /// line tall so the block keeps its rhythm. The inner row is the breath owner (no transform of its own).</summary>
        Element DotsSlot(string key, float height)
        {
            float d = Lyrics.Interlude.DotSize(large: true);
            return new BoxEl
            {
                Key = key, Height = height, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                Enter = SlotEnter, Exit = SlotExit, Transition = MotionTok.StandardEnter,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Shrink = 0f, Gap = Lyrics.Interlude.DotGap(large: true), AlignItems = FlexAlign.Center, OnRealized = _onDots,
                        Children = [Dot(0, d), Dot(1, d), Dot(2, d)],
                    },
                ],
            };

            Element Dot(int k, float size) => new BoxEl
            {
                Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(size * 0.5f), Fill = Ink.Ink, Opacity = (Prop<float>)_dotAlpha[k],
            };
        }

        long Now() => _pos.Estimate(Design.FrameTime.NowMs, _advancing, null, null, Playback.DurationMs.Peek());

        /// <summary>The packed view at <paramref name="now"/> (<see cref="Caption.ViewAt"/>).</summary>
        static int ComputeView(Lyrics.Doc d, long now, out long gapStart, out long gapEnd) => Caption.ViewAt(d, now, out gapStart, out gapEnd);

        static float SplitAt(Lyrics.Line line, long now) => CaptionWipeRows.SplitAt(line, now);

        void Tick()
        {
            if (_doc is not { } d || !_anchored) { _rows.Hold(Context.Anim, Context.Scene); ArmEdge(long.MaxValue, 0L); return; }
            long now = Now();
            int view = ComputeView(d, now, out long gapStart, out long gapEnd);
            if (view != _view.Peek()) _view.Value = view;
            // playing: the next instant the view can change is the one wake owed (the dots tick per frame and need none)
            ArmEdge(_advancing && !Caption.DotsOf(view) ? Caption.NextEdgeMs(d, now) : long.MaxValue, now);
            if (Context.Scene is not { } scene) return;
            if (Caption.DotsOf(view)) { _rows.Hold(Context.Anim, scene); DriveDots(scene, now, gapStart, gapEnd); return; }
            int anchor = Caption.AnchorOf(view);
            // the nodes belong to the line the last render built; a line-synced line has no wipe (it faded in whole)
            if (anchor < 0 || anchor != _wipeLine || (uint)anchor >= (uint)d.Lines.Count) { _rows.Hold(Context.Anim, scene); return; }
            var line = d.Lines[anchor];
            if (!line.IsWordByWord || line.Syllables.Count == 0) { _rows.Hold(Context.Anim, scene); return; }
            var main = _mainNode;
            if (!main.IsNull && scene.IsLive(main) && float.IsNaN(_runLen)) _runLen = MeasureRun(scene, main, line.Text);
            if (_advancing && Context.Anim is { RenderOwnsCompositor: true } anim
                && _rows.Seed(anim, scene, line, anchor, main, _glowNode, now, Design.FrameTime.NowMs, _runLen)) return;
            _rows.Cancel(Context.Anim);   // the UI owns the split (paused, buffering, a host whose render thread does not own the compositor)
            CaptionWipeRows.DriveUi(scene, main, _glowNode, line, now, _runLen);
        }

        /// <summary>Arm (or clear) the one-shot wake at media instant <paramref name="atMs"/> (<see cref="long.MaxValue"/> =
        /// none) through <see cref="Caption.EdgeWake"/>, in the frame clock's one domain.</summary>
        void ArmEdge(long atMs, long now)
        {
            if (_edge.Arm(atMs, now, Design.FrameTime.NowMs, out float delay))
                _edgeWake.Value = new Lyrics.MotionWake(++_edgeSeq, delay);
        }

        /// <summary>The caption's one-shot wake at the next view edge while the render thread poses the wipe: a re-arm restarts
        /// it, a cleared one cancels it. Named for the <c>[wake]</c> census.</summary>
        sealed class CaptionWake(CaptionHost owner) : Component
        {
            public override Element Render()
            {
                var wake = owner._edgeWake.Value;
                var timer = UseTimeout(owner._onEdge, MathF.Max(wake.DelayMs, 1f), DepKey.From(wake.Seq));
                if (wake.DelayMs < 0f) timer.Cancel();
                return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
            }
        }

        /// <summary>The active line's reading-order run length at the wrap width it was laid out at (one seam query per line,
        /// stack-allocated — the lyrics view's MeasureRunLength over the node's OWN text style).</summary>
        float MeasureRun(SceneStore scene, NodeHandle node, string text)
        {
            float wrap = _wrapW > 1f ? _wrapW : scene.Bounds(node).W;
            if (text.Length == 0 || !(wrap > 1f) || FluentGpu.Text.TextSeam.Default is not { } fonts) return MathF.Max(1f, wrap);
            var style = scene.Layout(node).TextStyle;
            Span<RectF> fragments = stackalloc RectF[8];
            int n = fonts.GetRangeRects(text, in style, wrap, 0, text.Length, fragments);
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += fragments[i].W;
            return sum > 1f ? sum : MathF.Max(1f, wrap);
        }

        /// <summary>The dots' fill + breath on the media clock (the lyrics view's DriveInterludeDots): three alpha signals bound
        /// to the dots, one transform on their row — both write-gated.</summary>
        void DriveDots(SceneStore scene, long now, long gapStart, long gapEnd)
        {
            float progress = Lyrics.Interlude.Progress(now, gapStart, gapEnd);
            float pulse = Lyrics.Interlude.Pulse(now, gapStart, Design.Reduced);
            var alpha = _dotAlpha;
            for (int k = 0; k < alpha.Length; k++)
            {
                float a = Lyrics.Interlude.DotAlpha(k, progress, pulse);
                if (MathF.Abs(alpha[k].Peek() - a) >= Lyrics.Wipe.DotAlphaEps) alpha[k].Value = a;
            }
            var h = _dotsNode;
            if (h.IsNull || !scene.IsLive(h)) return;
            ref NodePaint paint = ref scene.Paint(h);
            float scale = Lyrics.Interlude.BreathScale(pulse);
            if (MathF.Abs(paint.LocalTransform.M11 - scale) < Lyrics.Interlude.ScaleEps) return;
            paint.LocalTransform = scale >= 1f ? Affine2D.Identity : new Affine2D(scale, 0f, 0f, scale, 0f, 0f);
            scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }

    /// <summary>The caption's karaoke wipe on its active line's two text nodes (the main glyphs and the bloom). Playing, the
    /// RENDER THREAD poses it: <see cref="Seed"/> lays the exact split function from now to where it settles
    /// (<see cref="Lyrics.Wipe.SplitKeyframes"/>) on <c>AnimChannel.GlyphWipeSplit</c>, once per line and again when a report
    /// moves the clock. A line handing off is <see cref="Hold">held</see> where it stands, its on-screen split written back
    /// as the authored one; paused, <see cref="Cancel"/> drops the rows and <see cref="DriveUi"/> writes the split on the same
    /// tick. Every split, the UI's and the render thread's, steps in whole DIPs of the measured reading-order run
    /// (<see cref="GlyphWipe.Run"/>), so a wrapped line moves exactly as the UI wrote it.</summary>
    public sealed class CaptionWipeRows
    {
        int _line = -1;
        NodeHandle _main, _glow;
        long _clock;

        /// <summary>The line the rows were seeded for; -1 = none (the UI writes the split, or nothing is wiping).</summary>
        public int Line => _line;

        /// <summary>LineRow's split: the char-weighted sung fraction on TRUE time, nudged by the wipe's own lead mid-line.</summary>
        public static float SplitAt(Lyrics.Line line, long now)
        {
            float split = Lyrics.Wipe.ComputeSplit(line, now);
            return split > 0f && split < 1f ? Math.Clamp(split + Lyrics.Wipe.LeadFrac, 0f, 1f) : split;
        }

        /// <summary>The split the UI writes at <paramref name="now"/>: stepped in whole DIPs of the run (as the render thread
        /// steps a <see cref="GlyphWipe.Run"/> of the same length).</summary>
        public static float UiSplit(Lyrics.Line line, long now, float runLen) => GlyphWipe.Quantize(SplitAt(line, now), Run(runLen));

        static float Run(float runLen) => runLen > 1f ? runLen : 0f;

        /// <summary>Seed the rows on <paramref name="main"/> (and <paramref name="glow"/> when it carries a wipe) for line
        /// <paramref name="anchor"/>; <paramref name="clockNowMs"/> is the frame clock <paramref name="now"/> was estimated
        /// on (a report that moves position - clock re-seeds). False = the line's nodes are not realized yet.</summary>
        public bool Seed(AnimEngine anim, SceneStore scene, Lyrics.Line line, int anchor, NodeHandle main, NodeHandle glow,
            long now, long clockNowMs, float runLen)
        {
            if (main.IsNull || !scene.IsLive(main) || !scene.TryGetGlyphWipe(main, out _)) return false;
            bool halo = !glow.IsNull && scene.IsLive(glow) && scene.TryGetGlyphWipe(glow, out _);
            long clock = now - clockNowMs;
            if (_line == anchor && _main == main && _glow == (halo ? glow : default) && Math.Abs(clock - _clock) <= 1L) return true;
            if (_line >= 0 && (_line != anchor || _main != main)) Hold(anim, scene);
            float softness = Lyrics.Wipe.SoftnessOfLine(runLen > 1f ? runLen : MathF.Max(1f, scene.Bounds(main).W), large: true);
            var keys = Lyrics.Wipe.SplitKeyframes(line, now, lead: true, out long endMs);
            _line = anchor; _main = main; _glow = halo ? glow : default; _clock = clock;
            if (keys is null)
            {
                // settled: the split is 1 - written once, as the UI path would
                anim.Cancel(main, AnimChannel.GlyphWipeSplit);
                if (halo) anim.Cancel(glow, AnimChannel.GlyphWipeSplit);
                WriteSplit(scene, main, 1f, softness, runLen);
                if (halo) WriteSplit(scene, glow, 1f, softness, runLen);
                return true;
            }
            float durationMs = endMs - now;
            // the rows start from the value at `now`: written as the authored split too, so a row not posed yet shows the same
            WriteSplit(scene, main, keys[0].Value, softness, runLen);
            anim.Keyframes(main, AnimChannel.GlyphWipeSplit, keys, durationMs);
            if (halo)
            {
                WriteSplit(scene, glow, keys[0].Value, softness, runLen);
                anim.Keyframes(glow, AnimChannel.GlyphWipeSplit, keys, durationMs);   // the bloom tracks the main layer exactly
            }
            return true;
        }

        /// <summary>Stop the rows where they STAND (<see cref="AnimEngine.SetHeld"/>): an outgoing line (it exits keyed) or a
        /// break keeps the split on screen, as the UI path simply stopped writing it. The on-screen split is written back as
        /// the authored one too, so a row that is ever lost (an overlay-row overflow, a later cancel) leaves the line where it
        /// stands instead of snapping it back to its seed.</summary>
        public void Hold(AnimEngine? anim, SceneStore? scene)
        {
            if (_line < 0) return;
            if (anim is not null)
            {
                HoldOne(anim, scene, _main);
                HoldOne(anim, scene, _glow);
            }
            _line = -1;
            _main = _glow = default;
        }

        static void HoldOne(AnimEngine anim, SceneStore? scene, NodeHandle node)
        {
            if (node.IsNull) return;
            if (scene is not null && scene.IsLive(node) && scene.TryGetGlyphWipe(node, out var w)
                && anim.TryGetTrackValue(node, AnimChannel.GlyphWipeSplit, out float value))
            {
                float shown = w.QuantizeSplit(value, scene.Bounds(node).W);
                if (shown != w.Split)
                {
                    scene.SetGlyphWipe(node, w with { Split = shown });
                    scene.Mark(node, NodeFlags.PaintDirty);
                }
            }
            anim.SetHeld(node, AnimChannel.GlyphWipeSplit, true);
        }

        /// <summary>Hand the split back to the UI on the SAME line (paused, buffering): the rows go, and the caller writes the
        /// split from the clock on this same tick (<see cref="DriveUi"/>), so a cancelled row's pose never stays on screen.</summary>
        public void Cancel(AnimEngine? anim)
        {
            if (_line < 0) return;
            if (anim is not null)
            {
                if (!_main.IsNull) anim.Cancel(_main, AnimChannel.GlyphWipeSplit);
                if (!_glow.IsNull) anim.Cancel(_glow, AnimChannel.GlyphWipeSplit);
            }
            _line = -1;
            _main = _glow = default;
        }

        /// <summary>The UI's own write of the wipe — the lyrics view's write path in shape: the split stepped along the run,
        /// the per-line DIP feather, the settled/eps write-stops (a tick that moves nothing writes nothing).</summary>
        public static void DriveUi(SceneStore scene, NodeHandle main, NodeHandle glow, Lyrics.Line line, long now, float runLen)
        {
            if (main.IsNull || !scene.IsLive(main) || !scene.TryGetGlyphWipe(main, out var mw)) return;
            float split = UiSplit(line, now, runLen);
            float softness = Lyrics.Wipe.SoftnessOfLine(runLen > 1f ? runLen : MathF.Max(1f, scene.Bounds(main).W), large: true);
            if (WipeMoved(split, softness, in mw))
            {
                scene.SetGlyphWipe(main, mw with { Split = split, Softness = softness, Run = Run(runLen) });
                scene.Mark(main, NodeFlags.PaintDirty);
            }
            if (glow.IsNull || !scene.IsLive(glow) || !scene.TryGetGlyphWipe(glow, out var gw) || !WipeMoved(split, softness, in gw)) return;
            scene.SetGlyphWipe(glow, gw with { Split = split, Softness = softness, Run = Run(runLen) });   // the bloom tracks the main layer exactly
            scene.Mark(glow, NodeFlags.PaintDirty);
        }

        static bool WipeMoved(float split, float softness, in GlyphWipe w)
            => !Lyrics.Wipe.SplitSettled(split, w.Split)
               && (MathF.Abs(split - w.Split) > Lyrics.Wipe.SplitEps || MathF.Abs(softness - w.Softness) > Lyrics.Wipe.SoftnessEps);

        /// <summary>The authored split (and its feather and run), written only when it differs.</summary>
        static void WriteSplit(SceneStore scene, NodeHandle node, float split, float softness, float runLen)
        {
            if (node.IsNull || !scene.IsLive(node) || !scene.TryGetGlyphWipe(node, out var w)) return;
            float run = Run(runLen);
            if (w.Split == split && w.Run == run && MathF.Abs(softness - w.Softness) <= Lyrics.Wipe.SoftnessEps) return;
            scene.SetGlyphWipe(node, w with { Split = split, Softness = softness, Run = run });
            scene.Mark(node, NodeFlags.PaintDirty);
        }
    }

    // ══ 5. THE CHROME (top bar · transport card · gallery) ══════════════════════════════════════════════════════════

    /// <summary>Always mounted; each chrome piece sits under its OWN Flow.Show whose child is a BoxEl carrying the Enter/Exit
    /// (a bare ComponentEl root never plays its exit, Reconciler.cs:4106-4137 — V-U31) and the ZStack placement
    /// (vertical = AlignSelf, horizontal = JustifySelf — V-U9). The wrapper is PASS-THROUGH so empty chrome area yields to the
    /// stage root's pointer handlers while the bars stay clickable (V-U8).</summary>
    sealed class ChromeHost : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            bool sheet = L.Aspect == Aspect.Portrait;
            // Every piece: Flow.Show → a full-bleed LAYER carrying the Enter/Exit → the PLACED box as the layer's direct ZStack
            // child. Placed directly under the Show anchor (a flex column, see Layer), the bar's AlignSelf Start / the card's End
            // read as HORIZONTAL alignment: the bar shrank to its content (brand over "Lyrics", exit cut to "Ex…") and the card
            // sat at the top-right overflowing the window; and this host, content-height, put "bottom" near the top.
            return Layer with
            {
                Children =
                [
                    // TOP bar: top edge (AlignSelf Start), full width (JustifySelf Stretch)
                    Flow.Show(() => ctx.Chrome.Value, Layer with
                    {
                        Key = "chrome:top",
                        Enter = new EnterExit(Dy: -8f, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: -8f, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                        Children =
                        [
                            new BoxEl
                            {
                                Height = Layout.TopBarH, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, Direction = 0,
                                Children = [Embed.Comp(static () => new TopBar())],
                            },
                        ],
                    }),
                    // TRANSPORT card: bottom edge (AlignSelf End — auto height ⇒ the card's own 112/96), full width at Pad, or right of
                    // the art on Compact (Layout.TransportLeft — V-U23); the Margin is the ZStack offset, so the gutters hold.
                    Flow.Show(() => ctx.Chrome.Value, Layer with
                    {
                        Key = "chrome:transport",
                        Enter = new EnterExit(Dy: 8f, Opacity: 0f, Active: true), Exit = new EnterExit(Dy: 8f, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                        Children =
                        [
                            new BoxEl
                            {
                                AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 0,
                                Margin = new Edges4(L.TransportLeft, 0f, Layout.Pad, Layout.Pad),
                                Children = [Embed.Comp(static () => new TransportCard())],
                            },
                        ],
                    }),
                    // GALLERY pane: a right-docked column (top-aligned, JustifySelf End) or Portrait's bottom sheet (AlignSelf End, full width)
                    // NOT gated on the idle chrome: the face beside it is inset for it while it is open (GalleryShown)
                    Flow.Show(() => ctx.Mode.Value == Mode.Visualizer && ctx.GalleryShown.Value, Layer with
                    {
                        Key = "chrome:gallery",
                        Enter = new EnterExit(Dx: sheet ? 0f : 32f, Dy: sheet ? 32f : 0f, Opacity: 0f, Active: true), Exit = new EnterExit(Dx: sheet ? 0f : 32f, Dy: sheet ? 32f : 0f, Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0,
                                AlignSelf = sheet ? FlexAlign.End : FlexAlign.Start, JustifySelf = sheet ? FlexAlign.Stretch : FlexAlign.End,
                                Margin = sheet ? new Edges4(0f, 0f, 0f, L.TransportH + 2f * Layout.Pad) : new Edges4(0f, Layout.GalleryTop, Layout.GalleryRight, Layout.GalleryBottom),
                                Children = [Embed.Comp(static () => new GalleryHost())],
                            },
                        ],
                    }),
                ],
            };
        }
    }

    /// <summary>48 DIP: brand · the SelectorBar (pill tinted through TemplateParts) · gallery toggle · exit. Fills its wrapper (Grow).</summary>
    sealed class TopBar : Component
    {
        readonly Signal<int> _index = new(0);
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var set = ctx.Accent.Value;
            UseSignalEffect(() => _index.SetIfChanged((int)ctx.Mode.Value));
            var parts = UseMemo(() => { var p = new TemplateParts(); p.Set<BoxEl>(SelectorBar.PartPill, b => b with { Fill = set.Fill }); return p; }, DepKey.From(set.GetHashCode()));
            string[] items = L.IconOnlySelector ? ["", "", "", ""] : [Loc.Get(Strings.Stage.Mode.Lyrics), Loc.Get(Strings.Stage.Mode.Visualizer), Loc.Get(Strings.Stage.Mode.Queue), Loc.Get(Strings.Stage.Mode.Artist)];
            string?[] icons = [Icons.Document, Icons.Equalizer, Icons.Queue, Icons.Contact];
            var style = new SelectorBarStyle { RestColor = Ink.InkSecondary, SelectedColor = Ink.Ink, HoverColor = Ink.Ink, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, SelectedWeight = 600, ItemHeight = 36f, ItemGap = 4f };
            bool optionsOn = ctx.OptionsOpen.Value;
            return new BoxEl
            {
                Grow = 1f, Height = Layout.TopBarH, Direction = 0, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.L, 0f, Spacing.S, 0f), Gap = Spacing.S,
                Gradient = GradientDown(new GradientStop(0f, Shade(0.42f)), new GradientStop(1f, Shade(0f))),
                OnHoverMove = _ => ctx.OverControls(true), OnPointerExit = () => ctx.OverControls(false),
                Children =
                [
                    new BoxEl
                    {
                        // the brand never paints over the selector: clipped, and the subtitle is the part that gives way
                        Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, ClipToBounds = true,
                        Children =
                        [
                            new BoxEl { Width = 18f, Height = 18f, Corners = CornerRadius4.All(4.5f), Fill = Prop.Bind(ctx.Slab.Accent), Shrink = 0f },
                            new TextEl("Wavee") { Size = 12f, LineHeight = 16f, Weight = 600, Color = Ink.Ink, Shrink = 0f },
                            new TextEl(Loc.Get(Strings.Stage.Brand)) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                        ],
                    },
                    SelectorBar.Create(items, _index, onChange: i => ctx.SetMode((Mode)Math.Clamp(i, 0, ModeRules.Count - 1)), parts: parts, icons: icons, style: style),
                    new BoxEl
                    {
                        Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Layout.BarButtonGap,
                        Children =
                        [
                            ToolTip.Wrap(BarGlyph(Icons.Equalizer, ctx.OpenOptions, lit: optionsOn, onRealized: h => ctx.GalleryButton.SetIfChanged(h)), Loc.Get(Strings.Stage.Options.Button)),   // the TeachingTip's anchor SIGNAL (V-U16)
                            // Labelled only where the allocator says the column holds it (never "Ex…"); else the glyph with the label as its tooltip.
                            L.ExitLabelShown
                                ? Button.Create(Loc.Get(Strings.Stage.Exit), ctx.Exit, ButtonAppearance.Standard, ControlSize.Small, glyph: Icons.BackToWindow) with { Shrink = 0f }
                                : ToolTip.Wrap(BarGlyph(Icons.BackToWindow, ctx.Exit, lit: false, onRealized: null), Loc.Get(Strings.Stage.Exit)),
                        ],
                    },
                ],
            };
        }

        /// <summary>A 40×36 glass glyph button of the top bar (the gallery toggle, the icon-only exit).</summary>
        static BoxEl BarGlyph(string glyph, Action onClick, bool lit, Action<NodeHandle>? onRealized) => new()
        {
            Width = Layout.BarButtonW, Height = 36f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Fill = lit ? Ink.GlassHover : Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = onClick, OnRealized = onRealized,
            Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Ink.Ink }],
        };
    }

    /// <summary>The acrylic transport card: seek row (the bar's own seek + times) and the three clusters. Fills its wrapper (Grow);
    /// reads the playing row through the context's key (V-U34), never Tracks.Changed.</summary>
    sealed class TransportCard : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var set = ctx.Accent.Value;
            var overlay = UseContext(Overlay.Service);
            var track = ctx.RowValue();
            bool playing = Playback.IsPlaying.Value;
            bool loading = Playback.PhaseSignal.Value == Playback.Phase.Loading;
            var tf = Shell.TransportFacts();
            bool primary = Transport.PrimaryEnabled(track.IsValid, loading);
            var lib = Controls.Library;
            string uri = track.IsValid ? track.Uri.Text : "";
            string title = track.IsValid ? track.Title : "";
            bool saved = lib is not null && uri.Length > 0 && lib.IsSaved(uri);
            Action? like = lib is { } seam && uri.Length > 0 ? () => seam.ToggleSaved(uri, title) : null;
            var repeat = Playback.Repeat.Value;
            EntityId contextId = Playback.ContextUri.Value;
            Queue.ContextWire wire = Playback.ContextLabel.Value;
            string? source = Queue.ContextName(contextId, in wire);
            bool hasSource = source is { Length: > 0 };
            bool compact = L.Aspect == Aspect.Compact;

            var right = new List<Element>(5) { Heart(saved, like, set.Fill), Embed.Comp(static () => new MuteGlyph()) };
            if (L.VolumeFits) right.Add(Embed.Comp(new VolumeRow.Props(set, Layout.VolumeSliderW), static () => new VolumeRow()));   // only where the cluster still fits the card
            right.Add(Embed.Comp(static () => new DeviceButton()));
            right.Add(new BoxEl
            {
                Width = 40f, Height = 40f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, ClickRequestsContext = true,
                Children = [new TextEl(Icons.More) { Size = 16f, FontFamily = Theme.IconFont, Color = Ink.Ink }],
            }.WithContextMenu(overlay,
                () => { var m = NowPlayingMenu?.Invoke(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) ctx.MenuOpen(true); return m; },  // the "…" menu holds the chrome (V-U22); an empty menu never opens, so never counts
                new ContextMenuOptions { OnClosed = _ => ctx.MenuOpen(false) }));                                       // ContextMenu.cs:43

            return new BoxEl
            {
                Grow = 1f, Height = L.TransportH, Direction = 1, Padding = new Edges4(20f, 10f, 20f, 0f), Corners = Radii.CardAll,
                Acrylic = Ink.Card, Shadow = Elevation.Card, ClipToBounds = true,
                OnHoverMove = _ => ctx.OverControls(true), OnPointerExit = () => ctx.OverControls(false),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                        // the bar's own seek; its scrub edge holds the chrome through the new onScrubbing seam (§4.12 — a PlayerChromeFeed is inert without a MediaPlayerElement owner)
                        Children = [Shell.TimeText(remaining: false, ink: Ink.InkSecondary), new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [Shell.SeekBar(onScrubbing: ctx.Scrubbing)] }, Shell.TimeText(remaining: true, ink: Ink.InkSecondary)],
                    },
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Height = MathF.Min(58f, L.TransportH - 46f),   // 10 pad + 32 seek hit-box + this ≤ the card (Compact is 96)
                        Children =
                        [
                            new BoxEl
                            {
                                Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1,
                                Children =
                                [
                                    // a formatted key: the generated method formats; Loc.Get would render "[…]" (V-D15). No source ⇒ no label (never "Playing from search" for nothing).
                                    new TextEl(hasSource ? Strings.Stage.PlayingFrom(Queue.ContextKindLabel(contextId)) : "") { Size = 12f, LineHeight = 16f, Color = Ink.InkTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                                    new TextEl(source ?? "") { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                                ],
                            },
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f,
                                Children =
                                [
                                    ToolTip.Wrap(Satellite(Icons.Shuffle, Shell.ToggleShuffle, tf.CanTransport, Playback.Shuffle.Value, set.Fill), Loc.Get(Strings.Player.Shuffle)),
                                    ToolTip.Wrap(Glyph(Icons.Previous, static () => Playback.Previous(), 40f, 17f, tf.PrevEnabled), Loc.Get(Strings.Player.Previous)),
                                    Play(playing, primary, compact ? 52f : 56f, set),
                                    ToolTip.Wrap(Glyph(Icons.Next, static () => Playback.Next(), 40f, 17f, tf.NextEnabled), Loc.Get(Strings.Player.Next)),
                                    ToolTip.Wrap(Satellite(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll, Shell.CycleRepeat, tf.CanTransport, repeat != RepeatMode.Off, set.Fill), Loc.Get(Strings.Player.Repeat)),
                                ],
                            },
                            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = 6f, Children = right.ToArray() },
                        ],
                    },
                ],
            };
        }
    }

    /// <summary>The 56-DIP filled play disc: a hand-built circular BoxEl on the surface's AccentSet (Button.Create has no circular
    /// variant, so <c>ButtonPalette.ForAccent</c> is not used — V-U46).</summary>
    static Element Play(bool playing, bool enabled, float box, AccentSet set) => new BoxEl
    {
        Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(box),
        Fill = enabled ? set.Fill : Ink.ScrimRest, HoverFill = enabled ? set.FillSecondary : Ink.ScrimRest, PressedFill = enabled ? set.FillTertiary : Ink.ScrimRest,
        BrushTransitionMs = Design.Motion.Faster, Shadow = enabled ? Elevation.Card : (ShadowSpec?)null,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
        OnClick = static () => Shell.TogglePlayPause("stage.button"), Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(playing ? Icons.Pause : Icons.Play) { Size = 22f, FontFamily = Theme.IconFont, Color = enabled ? set.Ink : Ink.InkTertiary }],
    };

    /// <summary>A plateless 40-DIP glyph button (glass on hover).</summary>
    static BoxEl Glyph(string glyph, Action? onClick, float box, float glyphSize, bool enabled = true, Action<NodeHandle>? onRealized = null) => new()
    {
        Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = Ink.GlassRest, HoverFill = enabled ? Ink.GlassHover : Ink.GlassRest, PressedFill = enabled ? Ink.GlassPressed : Ink.GlassRest, BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = enabled ? onClick : null,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null, OnRealized = onRealized,
        Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Theme.IconFont, Color = enabled ? Ink.InkSecondary : Ink.InkTertiary, HoverColor = enabled ? Ink.Ink : Ink.InkTertiary }],
    };

    /// <summary>Shuffle / repeat: accent-filled while latched (the prototype's <c>.ab.chk</c>).</summary>
    static BoxEl Satellite(string glyph, Action onClick, bool enabled, bool latched, ColorF accent) => new()
    {
        Width = 40f, Height = 40f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = latched && enabled ? accent : Ink.GlassRest, HoverFill = latched && enabled ? accent with { A = 0.9f } : enabled ? Ink.GlassHover : Ink.GlassRest,
        PressedFill = enabled ? Ink.GlassPressed : Ink.GlassRest, BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = !enabled ? Ink.InkTertiary : latched ? ColorContrast.PickContrast(accent) : Ink.InkSecondary, HoverColor = !enabled ? Ink.InkTertiary : latched ? ColorContrast.PickContrast(accent) : Ink.Ink }],
    };

    static BoxEl Heart(bool saved, Action? onLike, ColorF accent) => new()
    {
        Width = 40f, Height = 40f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = Ink.GlassRest, HoverFill = onLike is null ? Ink.GlassRest : Ink.GlassHover, PressedFill = onLike is null ? Ink.GlassRest : Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = onLike is not null, AllowFocusOnInteraction = false, OnClick = onLike, Cursor = onLike is null ? (CursorId?)null : CursorId.Hand, BlocksDragArm = true,
        Children = [new BoxEl { Key = saved ? "sh:on" : "sh:off", Children = [new TextEl(saved ? Icons.HeartFill : Icons.Heart) { Size = 18f, FontFamily = Theme.IconFont, Color = saved ? accent : Ink.InkSecondary, HoverColor = saved ? accent : Ink.Ink }] }],
    };

    sealed class MuteGlyph : Component
    {
        public override Element Render()
        {
            bool muted = Playback.Muted.Value;
            return ToolTip.Wrap(Glyph(muted ? Icons.Mute : Icons.Volume, static () => Playback.ToggleMute(), 40f, 17f), Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
        }
    }

    /// <summary>The stock Slider in the surface accent at a fixed length (re-pushed props: a new accent re-skins in place).</summary>
    sealed class VolumeRow : Component
    {
        public sealed record Props(AccentSet Set, float Length);
        static readonly Slider.SliderOptions Options = new()
        {
            ThumbToolTipValueConverter = static v => s_percent.Get(Math.Clamp((int)MathF.Round(v * 100f), 0, 100), static p => p + "%"),   // cached: the tooltip re-reads per drag frame
        };
        readonly FloatSignal _level = new(Playback.Volume.Peek());
        public override Element Render()
        {
            var p = UseProps<Props>();
            UseSignalEffect(() => _level.SetIfChanged(Playback.Volume.Value));
            var style = Slider.DefaultStyle with
            {
                RailFill = Ink.Ink with { A = 0.545f }, RailFillDisabled = Ink.Ink with { A = 0.235f },
                ValueFill = p.Set.Fill, ValueFillPointerOver = p.Set.FillSecondary, ValueFillPressed = p.Set.FillTertiary, ValueFillDisabled = p.Set.Fill with { A = 0.3f },
                ThumbRing = Tok.FillControlSolid, ThumbFill = p.Set.Fill, ThumbFillPointerOver = p.Set.FillSecondary, ThumbFillPressed = p.Set.FillTertiary, ThumbFillDisabled = p.Set.Fill with { A = 0.3f },
                ThumbBorder = GradientSpec.Solid(Ink.Stroke),
            };
            return Slider.Create(_level, static v => Playback.SetVolume(v), Options, length: p.Length, thickness: 32f, style: style);
        }
    }

    /// <summary>"This PC ▾" — the two-section device picker over the SAME items the player bar builds (Shell.DevicePickerMenuItems).</summary>
    sealed class DeviceButton : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            _ = Playback.Devices.Changed.Value;
            var rows = Playback.Devices.Rows;
            int slot = Shell.DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, rows);
            string name = slot >= 0 ? rows[slot].Name ?? "" : Loc.Get(Strings.Player.SystemDefault);
            string glyph = slot >= 0 ? Icons.Devices : Icons.ThisPc;
            if (slot < 0)
            {
                string? selected = Playback.Audio.SelectedOutputId.Value;
                foreach (var d in Playback.Audio.Devices.Value)
                    if (selected is { Length: > 0 } && string.Equals(d.Id, selected, StringComparison.OrdinalIgnoreCase)) { name = d.Name; break; }
            }
            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                Playback.PickerOpened();
                var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(DeviceItems(), () => handle.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedRight, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
                ctx.MenuOpen(true);                                                          // the flyout holds the chrome (V-U22)
                opened.ClosedAction = () => { handle.Value = null; ctx.MenuOpen(false); };
                handle.Value = opened;
            }
            return new BoxEl
            {
                Height = 32f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Padding = new Edges4(11f, 0f, 10f, 0f), Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster, BorderWidth = 1f, BorderColor = Ink.Stroke,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h, MaxWidth = 220f,
                Shrink = 1f, MinWidth = Layout.DeviceMinW,   // the name gives way before the cluster leaves the card (Layout.TransportRightMinW)
                Children =
                [
                    new TextEl(glyph) { Size = 14f, FontFamily = Theme.IconFont, Color = Ink.Ink },
                    new TextEl(name) { Size = 14f, LineHeight = 20f, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                    new TextEl(Icons.ChevronDown) { Size = 12f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary },
                ],
            };
        }
        static List<MenuFlyoutItem> DeviceItems()
            => Shell.DevicePickerMenuItems(Playback.OwnerSignal.Peek(), Playback.ActiveDeviceSlot.Peek(), Playback.Devices.Rows,
                Playback.Audio.Devices.Peek(), Playback.Audio.SelectedOutputId.Peek(), Playback.Audio.Supported.Peek(),
                Playback.Audio.PlayingOpened, Shell.PickerAskedQuality());
    }

    /// <summary>"Artist · Album" (the previous stage's MetaLink with re-pushed type + colour). Subscribed to ITS row through the
    /// context's key (V-U34) and to Albums.Changed for the album title. Navigation CLOSES the stage first (V-U29).</summary>
    sealed class MetaLink : Component
    {
        /// <summary><paramref name="Center"/> centres the line (a TextEl has no text-align); <paramref name="HeroArtist"/> = the artist SLOT the
        /// Artist hero shows as its big name — the line then lists the OTHER billed artists ("with Cheat Codes · Sex"), 0 = every artist.</summary>
        public sealed record Props(float Size, float Line, ColorF Accent, bool Center = false, int HeroArtist = 0);
        readonly Action _go;
        Action<string>? _begin;
        public MetaLink() => _go = Go;
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            _begin = UseContext(SharedTransition.Begin);
            var p = UseProps<Props>();
            var hover = UseSignal(false);
            _ = Entities.ScopeEpoch.Value;                    // FIRST: a scope switch re-points the table reads below
            var track = ctx.RowValue();
            _ = Entities.Current.Albums.Changed.Value;
            _ = Entities.Current.Artists.Changed.Value;       // the names land after the credits
            _ = Entities.Current.Edges.TrackArtists.Changed.Value;
            string album = track.IsValid && track.Album.IsValid ? track.Album.Title : "";
            string line;
            if (p.HeroArtist > 0 && track.IsValid)
            {
                // the big name is the artist whose header shows: this line carries the OTHERS (never the same name twice)
                string others = OtherArtistNames(track, p.HeroArtist);
                string with = others.Length > 0 ? Strings.Stage.WithArtists(others) : "";
                line = with.Length > 0 && album.Length > 0 ? with + " · " + album : with.Length > 0 ? with : album;
            }
            else
            {
                string artists = track.IsValid ? ArtistNames(track) : "";
                line = artists.Length > 0 && album.Length > 0 ? artists + " · " + album : artists.Length > 0 ? artists : album;
            }
            if (line.Length == 0) return new BoxEl { Height = 0f, HitTestVisible = false };
            bool enabled = Shell.LinkFor(track, Shell.LinkSlot.Artist).Kind != Shell.RouteKind.NotFound;
            bool lit = enabled && hover.Value;
            return new BoxEl
            {
                // Shrink 0: this box sits in the identity COLUMN, where Shrink is VERTICAL — Shrink 1 let a short slot squeeze the
                // artist line to zero height under ClipToBounds. The width is the column's (cross-axis stretch); the TEXT shrinks.
                MinWidth = 0f, Shrink = 0f, Direction = 0, ClipToBounds = true, Cursor = enabled ? CursorId.Hand : (CursorId?)null, OnClick = enabled ? _go : null,
                Justify = p.Center ? FlexJustify.Center : FlexJustify.Start,
                OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null, OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = enabled, AllowFocusOnInteraction = false,
                Children = [new TextEl(line) { Size = p.Size, LineHeight = p.Line, Color = lit ? p.Accent : Ink.InkSecondary, Underline = lit, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
            };
        }

        /// <summary>The billed artists but <paramref name="exceptSlot"/>, ", " joined (the Artist hero's "with …" line).</summary>
        static string OtherArtistNames(Track track, int exceptSlot)
        {
            var slots = track.ArtistSlots;
            string joined = "";
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == exceptSlot) continue;
                string name = new Artist(slots[i]).Name;
                if (name.Length == 0) continue;
                joined = joined.Length == 0 ? name : joined + ", " + name;
            }
            return joined;
        }

        /// <summary>The billed artists, ", " joined, from the track's credits (Edges.TrackArtists — the player bar's source,
        /// Track.UI.cs MetadataLine: never the flat line first); the flat <c>ArtistLineId</c> only when no credit has a name yet.</summary>
        static string ArtistNames(Track track)
        {
            var slots = track.ArtistSlots;
            string joined = "";
            for (int i = 0; i < slots.Length; i++)
            {
                string name = new Artist(slots[i]).Name;
                if (name.Length == 0) continue;
                joined = joined.Length == 0 ? name : joined + ", " + name;
            }
            return joined.Length > 0 ? joined : Entities.Strings.Resolve(track.ArtistLineId);
        }
        void Go()
        {
            var r = Playback.Current.Peek();
            if (r.Kind != EntityKind.Track) return;
            var route = Shell.LinkFor(new Track(r.Slot), Shell.LinkSlot.Artist);
            if (route.Kind == Shell.RouteKind.NotFound) return;
            Close(_begin, "navigate");          // leave the stage BEFORE navigating (V-U29): the shell body is collapsed under it
            Shell.GoTo(route);
        }
    }

    // ══ 6. THE GALLERY MOUNT ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The gallery pane's body (placement + Enter/Exit live on ChromeHost's wrapper). Re-renders on the layout signal;
    /// the pane's controls take the context's own preference signals (O8).</summary>
    sealed class GalleryHost : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            return new BoxEl
            {
                OnHoverMove = _ => ctx.OverControls(true), OnPointerExit = () => ctx.OverControls(false),
                Children = [Visualizer.Gallery(ctx, L, close: static () => Prefs.Stage.SetGalleryOpen(false))],
            };
        }
    }

    // ══ 7. UP NEXT · ARTIST ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"Up next" + the Autoplay ToggleSwitch, then the queue's own rows (QueuePaneBody).</summary>
    sealed class QueuePane : Component
    {
        readonly Signal<bool> _autoplay = new(true);   // seeded once, mirrored from the setting by the effect — never a new signal per render
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var set = ctx.Accent.Value;
            UseSignalEffect(() => { _ = Platform.SettingsChanged.Value; _autoplay.SetIfChanged(Platform.Settings.Get(Platform.Keys.AutoplayEnabled)); });
            EntityId contextId = Playback.ContextUri.Value;
            Queue.ContextWire wire = Playback.ContextLabel.Value;
            UseEffect(static () => Queue.EnsureContext(Queue.ContextTarget(Playback.ContextUri.Peek(), Playback.ContextLabel.Peek())),
                      DepKey.From(contextId.GetHashCode() ^ (wire.Referrer.GetHashCode() * 31)));
            // the stage's own ink for the OFF state too: the stock Off* ramp is the app THEME's, and the stage's ground is not (V-U30)
            var toggle = ToggleSwitch.DefaultStyle with
            {
                OnFill = set.Fill, OnHover = set.FillSecondary, OnPressed = set.FillTertiary, OnKnob = set.Ink,
                OffFill = Ink.GlassRest, OffHover = Ink.GlassHover, OffPressed = Ink.GlassPressed, OffBorder = Ink.InkSecondary, OffKnob = Ink.InkSecondary,
                Foreground = Ink.Ink, MinWidth = 40f,
            };
            Element? rows = QueuePaneBody?.Invoke();
            var layout = ctx.Layout.Value;
            float s = layout.QueueScale;
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true, MaxWidth = layout.QueueListMaxW,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Margin = new Edges4(0f, 0f, 0f, Spacing.M),
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Stage.UpNext)) { Size = MathF.Round(28f * s), LineHeight = MathF.Round(36f * s), Weight = 600, FontFamily = DisplayFace, Color = Ink.Ink },
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                                Children = [new TextEl(Loc.Get(Strings.Player.Autoplay)) { Size = MathF.Round(14f * s), LineHeight = MathF.Round(20f * s), Color = Ink.Ink },
                                            ToggleSwitch.Create(_autoplay, static on => Platform.Settings.Set(Platform.Keys.AutoplayEnabled, on), style: toggle)],
                            },
                        ],
                    },
                    new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stagequeue",
                        Content = rows ?? new BoxEl { Padding = new Edges4(0f, Spacing.XXL, 0f, 0f), Children = [new TextEl(Loc.Get(Strings.Player.QueueEmpty)) { Size = 14f, LineHeight = 20f, Color = Ink.InkTertiary }] },
                    },
                ],
            };
        }
    }

}
