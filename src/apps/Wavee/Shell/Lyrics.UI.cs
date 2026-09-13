// ── Shell/Lyrics.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// the view, the row, the frame driver, ticker/stepper, the NPV peek — and the lyrics pane inside the stage (A12);
// Lyrics.Stage.UI.cs is dropped
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 3500 lines
// Spec: ch 22 §9, restated under A12 (2,600 + the dropped Lyrics.Stage.UI.cs's 1,400, less the ~500 already inside Stage.UI.cs's 1,150)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE READING SURFACE, and nothing that decides. One component (`ViewCore`) renders BOTH surfaces — the 340-DIP rail
// panel (`View()`) and the immersive stage's reading column (`StagePane()`); the only differences are `large` (type,
// gutter, band, σ ladder) and the INK (`Ink`: theme rungs on the rail, the stage's own ladder on media). Every number
// and every decision it draws with is `Lyrics.cs`'s — the ladders, the clocks, the emphasis word, the wipe, the
// cascade, the motion-demand gate, the upgrade authority and §12b's geometry — so this file lays out and drives nodes.
//
// THE FIVE THINGS THIS FILE IS SHAPED BY (ch 22 §0, §9):
//   1. Zero re-render per frame. The per-frame lane (`OnFrame`) writes SCENE COLUMNS (σ, transform, glyph wipe) and a
//      handful of value-gated signals; a row re-renders only when ITS OWN packed emphasis changes.
//   2. Lyrics wake only for the words (2026-09-12). The frame stepper is a frame-clock SUBSCRIBER, and its subscription
//      IS the request for panel-rate frames — so it is mounted only while `MotionDemand` says a lane is moving, and a
//      quiescent surface re-arms from a one-shot timeout at the next media instant instead.
//   3. Motion samples the frame clock. The media position is the playback host's (position, stamp) sample mapped onto
//      `Design.FrameTime.NowQpc` through `MediaClock` + `SampleClock`; `Environment.TickCount64` appears nowhere.
//   4. Two indices. `_activeLine` (lead-shifted: emphasis + follow) and `_voiceLine` (true time: wipe + glow); neither
//      drives the other.
//   5. A line hand-off is N springs on the LINES: the viewport LATCHES and every line carries a compensating translate
//      that decays to 0 (`Cascade`), marked TransformDirty only.
//
// Props freeze at mount (component-props-contract.md): `ViewCore`'s (large, onMedia, visible) are genuine mount config —
// each call site mounts one instance per surface for the app's life. Every LIVE value a row reads is a signal.

using System.Diagnostics;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using FluentGpu.Text;
using FrameTime = Wavee.Design.FrameTime;

namespace Wavee;

public static partial class Lyrics
{
    // ══ 0. MOUNT POINTS ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The immersive stage's reading column: capped at 700, left-anchored, its gutter spelled as TRAILING
    /// padding (the leading air is the stage band's region gap) and the pivot band's height reserved at the bottom.</summary>
    const float StageColumnMaxW = 700f, StageColumnGutter = 48f, StagePivotBandH = 72f;

    /// <summary>The rail body ticks only while the rail is open ON LYRICS and the stage is not covering it — one ticker
    /// ever runs (ch 22 parity 58).</summary>
    static readonly Func<bool> s_railVisible = static () =>
        Shell.Ui.RailOpen.Value && Shell.Ui.Mode.Value == Shell.RailMode.Lyrics && !Shell.Ui.ImmersiveLyrics.Value;

    /// <summary>The stage column ticks only while the stage is up AND its lyrics pane is the one showing (the queue pane
    /// parks it — both panes stay mounted).</summary>
    static readonly Func<bool> s_stageVisible = static () =>
        Shell.Ui.ImmersiveLyrics.Value && Stage.Pane.Current.Value == Stage.Pane.Lyrics;

    // MOUNT POINT (stage B contract)
    /// <summary>The rail's lyrics body (`RailMode.Lyrics`): the reading surface at rail metrics on theme ink. The header
    /// (title · 🌐 · &lt;/&gt; · ⛶ · ✕) is the rail frame's.</summary>
    public static Element View()
        => Embed.Comp(static () => new ViewCore(large: false, onMedia: false, visible: s_railVisible)) with { Key = "lyrics:rail" };

    // MOUNT POINT (stage B contract)
    /// <summary>The lyrics pane's BODY inside the stage's pane frame (A12: the frame, the cross-fade and the pivot are
    /// `Stage.UI.cs`'s). The SAME view at stage metrics on the stage's ink.</summary>
    public static Element StagePane() => new BoxEl
    {
        Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
        // LEFT-ANCHORED, never centred: centring converted an over-wide pane into text pushed off-screen.
        Justify = FlexJustify.Start, AlignItems = FlexAlign.Stretch,
        Padding = new Edges4(0f, 0f, StageColumnGutter, StagePivotBandH),
        Children =
        [
            new BoxEl
            {
                // MEASURED, never predicted: the column grows into whatever the pane gives it, capped by MaxWidth.
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, MaxWidth = StageColumnMaxW,
                Children = [Embed.Comp(static () => new ViewCore(large: true, onMedia: true, visible: s_stageVisible)) with { Key = "lyrics:stage" }],
            },
        ],
    };

    // MOUNT POINT (stage B contract)
    /// <summary>The now-playing panel's two-row lyrics reel. Absent — a bare zero-size box, no spine, no gap — unless
    /// the playing track has a timed document.</summary>
    public static Element NpvPeek() => Embed.Comp(static () => new PeekHost());

    // ══ 1. THE INK SEAM ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>WHICH ink a view paints with — the one seam between the two surfaces that mount the same view. A MODE,
    /// not a captured colour: every rung resolves its token at the point of consumption, so a live theme flip re-reads
    /// correctly on either surface (a <c>ColorF</c> frozen into a component would not).</summary>
    public readonly record struct Ink(bool OnMedia)
    {
        /// <summary>A sung glyph, the active line, a lit interlude dot.</summary>
        public ColorF Primary => OnMedia ? Design.StageInk.Ink : Tok.TextPrimary;
        /// <summary>An inactive line-synced line and the secondary layer.</summary>
        public ColorF Secondary => OnMedia ? Design.StageInk.InkSecondary : Tok.TextSecondary;
        /// <summary>Meta captions that are not lyric text.</summary>
        public ColorF Tertiary => OnMedia ? Design.StageInk.InkTertiary : Tok.TextTertiary;

        /// <summary>The one plate the reading surface owns (the resync pill, the video note).</summary>
        public ColorF Plate => OnMedia ? Design.StageInk.ScrimRest : Tok.FillSolidBase with { A = 0.92f };
        public ColorF PlateHover => OnMedia ? Design.StageInk.ScrimHover : Tok.FillSubtleSecondary;
        public ColorF PlatePressed => OnMedia ? Design.StageInk.ScrimPressed : Tok.FillSubtleTertiary;
        public ColorF PlateStroke => OnMedia ? Design.StageInk.Stroke : Tok.StrokeCardDefault;

        /// <summary>The resync ring: the accent on a theme plate, plain ink on media.</summary>
        public ColorF RingFill => OnMedia ? Design.StageInk.Ink : Tok.AccentDefault;
        public ColorF RingTrack => OnMedia ? Design.StageInk.Ink with { A = 0.30f } : Tok.StrokeControlDefault with { A = 0.55f };

        /// <summary>The loading bars.</summary>
        public ColorF Skeleton => OnMedia ? Design.StageInk.SkeletonBar : Tok.FillSubtleSecondary;

        /// <summary>The held-note BLOOM: on a dark stage the ink (a halo adds luminance), on a light stage the VEIL —
        /// blurred near-black under near-black subtracts and reads as a smudge.</summary>
        public ColorF Bloom => OnMedia ? (Design.StageInk.IsDark ? Design.StageInk.Ink : Design.StageInk.Veil) : Tok.TextPrimary;
        /// <summary>…and how much of it survives: half on the light stage.</summary>
        public float BloomScale => OnMedia && !Design.StageInk.IsDark ? 0.5f : 1f;
    }

    // ══ 2. THE VIEW ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The follow scroll's four modes.</summary>
    internal enum FollowMode : byte { Following, DetachedActive, DetachedIdle, Resyncing }

    enum FollowArm : byte { Unavailable, AtTarget, Armed }

    enum FollowIntent : byte { Normal, Resync }

    /// <summary>The lyrics reading surface.</summary>
    internal sealed class ViewCore : Component
    {
        // ── advance-probe seam (`--lyrics-advance-probe`, Screens/Diagnostics.Probe.cs, Wave 6) ──────────────────────
        // `internal` accessors, not switches: the probe drives the media clock SYNCHRONOUSLY so a line advance and the
        // frame that records it are the same frame, and the autonomous stepper stays unmounted under it.
        internal static bool ProbeSyncMode = false;   // set by the probe entry (Wave 6)
        internal static ViewCore? ProbeActive;
        internal void ProbeStep(long nowMs) => OnFrame(forceVisual: true, probeNowMs: nowMs);
        internal void ProbeForceSnapped() => _scrollSnapped = true;
        internal NodeHandle ProbeViewport => _viewportNode;
        internal int ProbeActiveLine => _activeLine.Peek();
        internal int ProbeLineCount => _doc?.Lines.Count ?? 0;
        internal long ProbeLineStartMs(int i) => _doc is { } d && (uint)i < (uint)d.Lines.Count ? d.Lines[i].StartMs : 0L;
        internal NodeHandle ProbeDofNode(int i) => (uint)i < (uint)_dofNodes.Length ? _dofNodes[i] : default;
        internal float ProbeCascadeComp(int i) => (uint)i < (uint)_casComp.Length ? _casComp[i] : 0f;
        internal float ProbeCascadeDelayMs(int i) => (uint)i < (uint)_casDelay.Length ? _casDelay[i] : 0f;
        internal bool ProbeCascadeActive => _cascadePending;
        internal bool ProbeScrollSnapped => _scrollSnapped;

        // ── mount config (frozen at mount — correct: one instance per surface for the app's life) ────────────────────
        internal readonly bool Large;
        internal readonly Ink InkMode;
        readonly Func<bool> _visible;
        internal readonly RowMetrics Metrics;
        readonly float _band;

        internal ViewCore(bool large, bool onMedia, Func<bool> visible)
        {
            Large = large;
            InkMode = new Ink(onMedia);
            _visible = visible;
            Metrics = Surface.Timed(large);
            _band = Surface.FocalBand(large);
            _wakeTick = WakeTick;
            _dotAlpha = new FloatSignal[Interlude.DotCount];
            for (int k = 0; k < _dotAlpha.Length; k++) _dotAlpha[k] = new FloatSignal(0f);
        }

        // ── the media clock ──────────────────────────────────────────────────────────────────────────────────────────
        readonly MediaClock _clock = new();
        long _lastSampleStamp = long.MinValue;   // the host sample (PosQpc ms stamp) already fed to the clock
        int _lastSamplePos = int.MinValue;
        long _lastClockLogMs;                    // FrameTime.NowMs of the last `lyrics.clock` line (0 = not baselined)

        // ── emphasis, voice, now ─────────────────────────────────────────────────────────────────────────────────────
        readonly Signal<int> _activeLine = new(-1);   // emphasis + follow target (lead-shifted)
        readonly Signal<int> _voiceLine = new(-1);    // the line being sung on TRUE time (wipe + glow)
        internal readonly FloatSignal NowMs = new(0f);
        /// <summary>One VALUE-GATED signal per line: a row re-renders solely on ITS OWN bucket/past/reserve change. A
        /// shared memo would fan every boundary out to the whole realized document.</summary>
        Signal<int>[] _lineEmphasis = [];
        readonly Signal<int> _emphasisFallback = new(Emphasis.Fallback);   // a resize-gap guard, never a state

        // ── the follow ───────────────────────────────────────────────────────────────────────────────────────────────
        internal readonly Signal<FollowMode> Follow_ = new(FollowMode.Following);
        readonly FloatSignal _resyncProgress = new(1f);
        long _resyncDeadlineMs;
        bool _scrollSnapped;
        float? _followProgrammaticTargetY;   // the last POSTED resync destination (the kernel's chase target is not readable)

        // ── the halo cross-fade ──────────────────────────────────────────────────────────────────────────────────────
        FloatSignal[] _glowAlpha = [];
        int _glowInLine = -1;
        int _glowOutLine = -1; long _glowOutStartMs; float _glowOutFrom;

        // ── the wipe feather ─────────────────────────────────────────────────────────────────────────────────────────
        float[] _lineRunLen = [];         // per-line reading-order run length (NaN = unmeasured)
        float _runLenWrapW = float.NaN;   // the wrap width every cached entry was measured at

        // ── scene handles (reported ONCE, at realization) ────────────────────────────────────────────────────────────
        NodeHandle _viewportNode = NodeHandle.Null;
        NodeHandle[] _lineNodes = [];
        NodeHandle[] _glowNodes = [];
        NodeHandle[] _dofNodes = [];

        // ── the directional σ model ──────────────────────────────────────────────────────────────────────────────────
        float[] _dofCurrent = [];        // the σ each line is being DRIVEN to (NaN = never driven ⇒ adopt)
        bool _dofRampPending = true;
        long _dofRampMs;                 // FrameTime.NowMs of the last ramp pass (0 = none)

        // ── the staggered hand-off cascade (Cascade.Arm/Step own the math; these are its caller-owned spans) ─────────
        float[] _casComp = [], _casVel = [], _casDelay = [], _casRate = [];
        byte[] _casWrite = [];
        bool _cascadePending;
        readonly Signal<bool> _cascadeRunning = new(false);   // the ticker-facing mirror, written twice per hand-off
        long _casQpc;                                          // FrameTime.NowQpc of the last cascade pass (0 = none)

        // ── preferences, republished for the rows ────────────────────────────────────────────────────────────────────
        /// <summary>The secondary-line mode. A SIGNAL the rows read: a ctor int would never reach a mounted row.</summary>
        internal readonly Signal<int> Secondary = new(Prefs.None);
        int _strength = BlurPolicy.StrongGpuDefault;   // the RESOLVED 0..100 blur strength
        float _dofScale = 1f;
        /// <summary>The strength multiplier for the one RENDER-time consumer (the line-synced ACTIVE row's halo), which
        /// reads it only inside its isActive branch so a slider move re-renders exactly that row.</summary>
        internal readonly FloatSignal HaloScale = new(1f);

        // ── the document ─────────────────────────────────────────────────────────────────────────────────────────────
        Doc? _doc;
        Doc? _pendingUpgrade;
        Loadable<Doc?>? _docLoadable;
        string _trackId = "";
        /// <summary>Bumped on every NON-same-shape swap and folded into each row's key, so a genuine document change
        /// remounts the rows (they froze the old line and signals at mount).</summary>
        int _docEpoch;
        MeasuredLayout? _layout;

        // ── the interlude ────────────────────────────────────────────────────────────────────────────────────────────
        int _interludeReserveLine = -1;
        int _reserveRelatchFrames;
        readonly Signal<bool> _dotsShown = new(false);
        readonly FloatSignal[] _dotAlpha;
        NodeHandle _dotsRowNode = NodeHandle.Null;

        // ── motion demand ────────────────────────────────────────────────────────────────────────────────────────────
        readonly Signal<bool> _motionLive = new(true);   // starts live: the first step decides for itself
        readonly Signal<MotionWake> _motionWake = new(new MotionWake(0, -1f));
        readonly Signal<bool> _motionRecheck = new(false);
        int _motionWakeSeq;
        long _motionWakeAtMs = long.MinValue;
        internal readonly Action _wakeTick;

        // ── the developer surface (gated by `diag.developerMode`, never by an env var — plan §9.6 Q1) ─────────────────
        internal readonly Signal<bool> DebugOpen = new(false);

        string RowKey(int index) => "ll" + _docEpoch + ":" + index;

        internal FollowMode FollowModeValue => Follow_.Value;
        internal bool CascadeRunningValue => _cascadeRunning.Value;
        internal bool MotionLiveValue => _motionLive.Value;
        internal MotionWake MotionWakeValue => _motionWake.Value;
        internal bool MotionRecheckValue => _motionRecheck.Value;
        internal string TrackId => _trackId;

        void WakeTick() { if (!ProbeSyncMode) OnFrame(); }

        public override Element Render()
        {
            bool open = _visible();
            UseEffect(() => { if (!open) ResetFollowState(Context.Scene); }, DepKey.From(open));

            // ── preferences: ONE read per view under the lyrics epoch, republished to the rows ──────────────────────
            // Writing `Secondary`/`HaloScale` here is the 0.2.9 shape on purpose: this render does not READ either, and
            // the rows that do must re-render IN THIS flush so the re-arranged extents are what the next step latches on.
            int secondary = Prefs.SecondaryLine();
            Secondary.Value = secondary;
            int strength = Prefs.BlurStrength(GpuProfile.IsWeak);
            _strength = strength;
            float newScale = BlurPolicy.Scale(strength);
            if (newScale != _dofScale)
            {
                _dofScale = newScale;
                _dofRampPending = true;
                // A slider move is a user edit, not a hand-off: NaN makes the ramp ADOPT every target in one pass, so a
                // decrease never stops part-way on a paused, tickerless surface.
                Array.Fill(_dofCurrent, float.NaN);
            }
            HaloScale.Value = newScale;
            // Re-arm the ramp IMMEDIATELY on a strength change — it does not ride the ticker, so it lands paused too.
            UseEffect(() => { if (Context.Scene is { } scene) DriveDofRamp(scene, FrameTime.NowMs); }, DepKey.From(strength));
            // A mode flip changes EVERY row's height: re-arrange, then hard re-latch from the fresh geometry.
            UseEffect(() =>
            {
                if (Context.Scene is { } sc && !_viewportNode.IsNull && sc.IsLive(_viewportNode))
                    sc.Mark(_viewportNode, NodeFlags.LayoutDirty | NodeFlags.VirtualRangeDirty);
                ResetScrollSnap();
            }, DepKey.From(secondary));

            // ── what is playing ─────────────────────────────────────────────────────────────────────────────────────
            var current = Playback.Current.Value;                  // value-gated: changes on a track change only
            bool anything = !Playback.CurrentId.Value.IsEmpty;
            string trackId = current.Kind == EntityKind.Track && !current.IsNone ? Store.IdOf(new Track(current.Slot)) : "";

            if (!anything)
            {
                ClearDocument();
                _trackId = "";
                return Message(Loc.Get(Strings.Player.NothingPlaying));
            }
            if (trackId.Length == 0)
            {
                ClearDocument();
                _trackId = "";
                return Message(Loc.Get(Strings.Lyrics.NoLyrics));
            }
            if (!string.Equals(_trackId, trackId, StringComparison.Ordinal)) ClearDocument();
            _trackId = trackId;

            // The document lives under a track-keyed boundary, so chrome re-renders reconcile it in place instead of
            // rebuilding the virtual list and remounting every line.
            Element body = Embed.Comp(() => new DocHost(this, trackId)) with { Key = "lyrics-doc:" + trackId };
            Element? ticker = open ? Embed.Comp(() => new Ticker(this)) with { Key = "lyrics-ticker:" + trackId } : null;
            Element dots = InterludeDots();
            Element resync = ResyncOverlay();
            Element banner = VideoNote();
            Element debug = Embed.Comp(() => new DebugLayer(this)) with { Key = "lyrics-debug:" + trackId };

            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true,
                Children = ticker is null
                    ? [body, dots, resync, banner, debug]
                    : [body, ticker, dots, resync, banner, debug],
            };
        }

        // ── 2.1 the overlays ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The note a VIDEO raises while it suppresses timed sync, so the missing highlight reads as a
        /// deliberate state. The full-bleed pass-through positioner stays OUTSIDE <c>Flow.Show</c>.</summary>
        Element VideoNote() => new BoxEl
        {
            Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.M, Surface.VideoNoteTopPad(Large), Spacing.M, 0f),
            Children =
            [
                Flow.Show(
                    static () => SyncGate.SyncSuppressed(Playback.VideoActive.Value),
                    new BoxEl
                    {
                        Shrink = 0f, MinWidth = 0f,
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                        Corners = CornerRadius4.All(Radii.Control),
                        Fill = InkMode.Plate,
                        HitTestVisible = false,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Player.LyricsSyncUnavailableDuringVideo))
                            {
                                Size = 12f, Weight = 600, MinWidth = 0f, Color = InkMode.Tertiary,
                                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            },
                        ],
                    }),
            ],
        };

        /// <summary>The "Resync" pill with its 4 s countdown ring, up while the user owns the scroll.</summary>
        Element ResyncOverlay() => new BoxEl
        {
            Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
            Padding = new Edges4(0f, 0f, 0f, Surface.ResyncInset(Large)),
            Children =
            [
                Flow.Show(
                    () => Follow_.Value is FollowMode.DetachedActive or FollowMode.DetachedIdle,
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                        Padding = new Edges4(11f, 7f, 13f, 7f), Corners = CornerRadius4.All(16f),
                        Fill = InkMode.Plate, BorderWidth = 1f, BorderColor = InkMode.PlateStroke,
                        HoverFill = InkMode.PlateHover, PressedFill = InkMode.PlatePressed,
                        // PRESS only — the pill never grows on hover (ch 22 audit 1, parity 75).
                        PressScale = Design.Motion.ScaleSubtle.Press, Cursor = CursorId.Hand,
                        OnClick = () => BeginResync(Context.Scene),
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                        Enter = new EnterExit(Dy: 4f, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Dy: 4f, Opacity: 0f, Active: true),
                        Layout = LayoutTransition.Fade,
                        Children =
                        [
                            ProgressRing.Create(_resyncProgress, size: 18f, foreground: InkMode.RingFill, track: InkMode.RingTrack),
                            new TextEl(Loc.Get(Strings.Player.ResyncLyrics)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = InkMode.Primary },
                        ],
                    }),
            ],
        };

        /// <summary>The instrumental break's three breathing dots: an overlay, not a row (a pseudo-row would put a
        /// conditional off-by-one into every index mapping). Two empty Grow spacers split the panel at the band and the
        /// row's bottom margin buys the rest — exact at every viewport size. Decorative: <c>HitTestVisible = false</c> on
        /// the whole subtree, so the lyrics stay scrollable and clickable straight through it.</summary>
        Element InterludeDots()
        {
            float d = Interlude.DotSize(Large);
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, HitTestVisible = false,
                Direction = 1, AlignItems = FlexAlign.Start,
                Children =
                [
                    new BoxEl { Grow = _band, MinHeight = 0f },
                    Flow.Show(() => _dotsShown.Value, new BoxEl
                    {
                        // Enter/exit owner. The breath lives on the CHILD (one transform owner per node).
                        Direction = 0, Shrink = 0f,
                        Margin = new Edges4(Metrics.SidePad, 0f, 0f, Interlude.AnchorMargin(Large, _band)),
                        Enter = new EnterExit(Sx: 0.72f, Sy: 0.72f, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Sx: 0.72f, Sy: 0.72f, Opacity: 0f, Active: true),
                        Layout = LayoutTransition.Fade,
                        Children =
                        [
                            new BoxEl
                            {
                                // Breath owner: declares no transform and owns no animation channel, so the frame lane's
                                // direct LocalTransform write is the only thing that touches it.
                                Direction = 0, Shrink = 0f, Gap = Interlude.DotGap(Large), AlignItems = FlexAlign.Center,
                                OnRealized = h => _dotsRowNode = h,
                                Children = [Dot(0, d), Dot(1, d), Dot(2, d)],
                            },
                        ],
                    }),
                    new BoxEl { Grow = 1f - _band, MinHeight = 0f },
                ],
            };

            Element Dot(int k, float size) => new BoxEl
            {
                Width = size, Height = size, Shrink = 0f,
                Corners = CornerRadius4.All(size * 0.5f),
                Fill = InkMode.Primary,
                Opacity = (Prop<float>)_dotAlpha[k],
            };
        }

        // ── 2.2 the document host ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>The track-keyed document boundary. It demands the document ONCE (on its mount — a track change) and
        /// mirrors the store into a <see cref="Loadable{T}"/> the skeleton region reads, applying the UPGRADE rule on the
        /// way: a richer document that lands mid-line is HELD until the next hand-off.</summary>
        sealed class DocHost(ViewCore owner, string trackId) : Component
        {
            readonly Loadable<Doc?> _loadable = Loadable<Doc?>.Pending(null);

            public override Element Render()
            {
                UseLayoutEffect(() => Store.Ensure(trackId), DepKey.Empty);
                UseSignalEffect(() =>
                {
                    _ = Store.Changed.Value;   // subscribe → every publication re-syncs
                    owner.SyncFromStore(trackId, _loadable);
                });

                bool large = owner.Large;
                return Skel.Region<Doc?>(
                    _loadable,
                    shimmerSource: () => owner.Shimmer(),
                    content: d => d is { Lines.Count: > 0 } ready ? owner.LyricsContent(ready) : owner.Message(Loc.Get(Strings.Lyrics.NoLyrics)),
                    reveal: SkelReveal.FadeOnly,
                    onFailed: () => owner.Message(Loc.Get(Strings.Lyrics.NoLyrics)),
                    isEmpty: static d => d is not { Lines.Count: > 0 },
                    onEmpty: () => owner.Message(Loc.Get(Strings.Lyrics.NoLyrics)),
                    // The derived-shimmer style is INERT under a custom shimmerSource — the visible gap is Shimmer()'s own.
                    style: new SkeletonStyle(owner.InkMode.Skeleton, RowGap: Surface.ShimmerGap(large), BarRadius: Surface.ShimmerRadius, TextRatio: 0.86f),
                    smoothResize: false);
            }
        }

        /// <summary>Mirror one store publication into the region's loadable. First arrival: prepare, then flip Ready. A
        /// replacement: the authority gate, then apply now or hold for the next hand-off.</summary>
        internal void SyncFromStore(string trackId, Loadable<Doc?> loadable)
        {
            if (!string.Equals(trackId, _trackId, StringComparison.Ordinal)) return;   // a stale host on its way out
            _docLoadable = loadable;
            bool answered = Store.Answered(trackId);
            var doc = Store.Doc(trackId);
            if (!answered)
            {
                // A re-fetch dropped the document: back to the shimmer, and nothing from the old one survives.
                if (loadable.State.Peek() != (byte)LoadState.Pending) { ClearDocumentState(); loadable.SetPending(null); }
                return;
            }
            if (loadable.State.Peek() != (byte)LoadState.Ready)
            {
                if (doc is { Lines.Count: > 0 }) PrepareDocument(doc, Playback.PositionMs.Peek());
                else PublishSecondaryAvailability(null);
                loadable.SetReady(doc);
                return;
            }
            if (doc is null || ReferenceEquals(doc, loadable.Value.Peek())) return;
            ReceiveUpgrade(doc);
        }

        void ReceiveUpgrade(Doc upgrade)
        {
            if (upgrade.Lines.Count == 0) return;
            var current = _docLoadable?.Value.Peek() ?? _doc;
            if (current is not null && !Authority.IsRicher(upgrade, current)) return;
            if (Authority.ApplyImmediately(Playback.IsPlaying.Peek(), _doc, _activeLine.Peek()))
                ApplyUpgrade(upgrade, Playback.PositionMs.Peek());
            else
                _pendingUpgrade = upgrade;
        }

        void ApplyUpgrade(Doc upgrade, long posMs)
        {
            _pendingUpgrade = null;
            PrepareDocument(upgrade, posMs);
            _docLoadable?.SetReady(upgrade);
        }

        // ── 2.3 preparing and clearing a document ───────────────────────────────────────────────────────────────────

        void PrepareDocument(Doc doc, long posMs)
        {
            if (ReferenceEquals(_doc, doc)) return;
            // The cascade and the dots retire BEFORE the arrays are replaced — a surviving row would otherwise keep a
            // stale compensation with no handle left to clear it through.
            ZeroCascade(Context.Scene);
            HideInterludeDots();

            var previous = _doc;
            // THE upgrade question, asked once: everything a row FREEZES must be identical for the rows to survive.
            bool sameShape = previous is not null && RowShape.SameRows(previous, doc);
            if (!sameShape) { _layout = null; _docEpoch++; }
            _doc = doc;
            PublishSecondaryAvailability(doc);
            int n = doc.Lines.Count;
            if (!sameShape)
            {
                _lineNodes = new NodeHandle[n];
                _glowNodes = new NodeHandle[n];
                _dofNodes = new NodeHandle[n];
                _glowAlpha = new FloatSignal[n];
                for (int i = 0; i < n; i++) _glowAlpha[i] = new FloatSignal(0f);
                _lineRunLen = new float[n];
                Array.Fill(_lineRunLen, float.NaN);
                _runLenWrapW = float.NaN;
                _dofCurrent = new float[n];
                Array.Fill(_dofCurrent, float.NaN);
                _casComp = new float[n]; _casVel = new float[n]; _casDelay = new float[n]; _casRate = new float[n];
                _casWrite = new byte[n];
            }
            else
            {
                // KEEP by identity the handles, the row-held signals, the run lengths and σ continuity; reset the
                // per-document motion in place, and the halo VALUES (the signal objects survive).
                Array.Clear(_casComp); Array.Clear(_casVel); Array.Clear(_casDelay); Array.Clear(_casRate); Array.Clear(_casWrite);
                for (int i = 0; i < _glowAlpha.Length; i++) _glowAlpha[i].Value = 0f;
            }
            _dofRampPending = true;
            _dofRampMs = 0L;
            _cascadePending = false;
            _casQpc = 0L;

            // SEEDED at the document's TRUE opening state (through the same interlude advance the frame lane uses), so
            // the PushEmphasis below is a silent no-op instead of fanning the whole document out on every load.
            bool timed = IsTimed(doc);
            int seedActive = timed ? AdvancePastInterlude(doc, ResolveLine(doc.Lines, posMs), posMs, out _, out _) : -1;
            if (!sameShape)
            {
                _interludeReserveLine = -1;
                _lineEmphasis = new Signal<int>[n];
                for (int i = 0; i < n; i++) _lineEmphasis[i] = new Signal<int>(Emphasis.Pack(i, seedActive, -1));
            }
            _glowInLine = -1; _glowOutLine = -1;
            _activeLine.Value = seedActive;
            if (!timed) _voiceLine.Value = -1;
            PushEmphasis();
            NowMs.Value = posMs;
            _scrollSnapped = false;
            RebaseClock(posMs);
            WakeMotion();
        }

        /// <summary>The header toggle's capability. Gated on a TIMED document: an unsynced block has no rows to render a
        /// second line in, so offering the toggle there would be a control that visibly does nothing (parity 69).</summary>
        static void PublishSecondaryAvailability(Doc? doc)
            => Prefs.Available.Value = doc is not null && IsTimed(doc) ? doc.SecondaryAvailable : 0;

        void PushEmphasis()
        {
            var em = _lineEmphasis;
            int active = _activeLine.Peek();
            int reserve = _interludeReserveLine;
            for (int i = 0; i < em.Length; i++) em[i].Value = Emphasis.Pack(i, active, reserve);
        }

        void ClearDocument()
        {
            // The capability retires BEFORE the early-out, so the toggle never lingers over the next track (parity 70).
            PublishSecondaryAvailability(null);
            ClearDocumentState();
        }

        void ClearDocumentState()
        {
            ResetFollowState(Context.Scene);
            _layout = null;
            _viewportNode = NodeHandle.Null;
            _pendingUpgrade = null;
            if (_doc is null && _lineNodes.Length == 0) return;
            _doc = null;
            _lineNodes = []; _glowNodes = []; _dofNodes = [];
            _glowAlpha = [];
            _lineEmphasis = [];
            _lineRunLen = [];
            _runLenWrapW = float.NaN;
            _dofCurrent = [];
            _dofRampPending = true;
            _dofRampMs = 0L;
            _casComp = []; _casVel = []; _casDelay = []; _casRate = []; _casWrite = [];
            _cascadePending = false;
            _cascadeRunning.Value = false;
            _casQpc = 0L;
            _glowInLine = -1; _glowOutLine = -1;
            _activeLine.Value = -1;
            _voiceLine.Value = -1;
            HideInterludeDots();
            _interludeReserveLine = -1;
            _reserveRelatchFrames = 0;
            NowMs.Value = 0f;
            _scrollSnapped = false;
            // A clear resets the clock too: a port that keeps it across a track change re-treats the next document's
            // first sample as a >250 ms disagreement.
            _clock.Reset(0L, FrameTime.NowQpc, false);
            _lastSampleStamp = long.MinValue;
            _lastSamplePos = int.MinValue;
            WakeMotion();
        }

        /// <summary>Seed the media clock from an authoritative position, and absorb the host's CURRENT sample so the next
        /// step does not re-treat our own re-anchor as a disagreement.</summary>
        void RebaseClock(long positionMs)
        {
            var snap = Playback.Snap();
            _clock.Reset(positionMs, FrameTime.NowQpc, Playback.IsPlaying.Peek());
            _lastSampleStamp = snap.PosQpc;
            _lastSamplePos = snap.PosMs;
        }

        // ── 2.4 the content ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Is timed sync suppressed right now? Read with <c>.Value</c> so the CONTENT re-renders the moment a
        /// video starts or stops; the frame lane peeks the same state.</summary>
        static bool SyncSuppressedNow() => SyncGate.SyncSuppressed(Playback.VideoActive.Value);

        internal Element LyricsContent(Doc doc)
        {
            // A suppressed timed document renders exactly like an untimed one — no active line to be far from.
            if (!IsTimed(doc) || SyncSuppressedNow()) return UnsyncedContent(doc);

            var lines = doc.Lines;
            float estimate = Metrics.Estimate;
            if (_layout is null || MathF.Abs(_layout.Band - _band) > 0.001f || MathF.Abs(_layout.Estimate - estimate) > 0.5f)
                _layout = new MeasuredLayout(estimate, _band);
            var layout = _layout;
            return Virtual.Custom(
                lines.Count,
                layout,
                i =>
                {
                    int idx = i;
                    var line = lines[idx];
                    var emphasis = (uint)idx < (uint)_lineEmphasis.Length ? _lineEmphasis[idx] : _emphasisFallback;
                    var glow = (uint)idx < (uint)_glowAlpha.Length ? _glowAlpha[idx] : null;
                    return Embed.Comp(() => new LineRow(this, idx, line, emphasis, glow)) with { Key = RowKey(idx) };
                },
                keyOf: RowKey,
                // Realize the WHOLE document: every row must be measured before follow geometry is trusted.
                overscan: Math.Min(lines.Count, Surface.OverscanCap)) with
            {
                Grow = 1f,
                MinHeight = 0f,
                RealizeOverscanImmediately = true,
                AutoEdgeFade = true,
                SuppressScrollBar = true,
                OnScrollGeometryChanged = (
                    static g => g.UserScrollActive ? 1L : 0L,
                    g => OnScrollActivity(g.UserScrollActive, FrameTime.NowMs)),
                OnRealized = h => _viewportNode = h,
            };
        }

        /// <summary>An untimed (or suppressed) document: a reading block at reading type, left-aligned, wrapped, never
        /// ellipsised — no highlight, no follow, no wipe, no blur ladder.</summary>
        Element UnsyncedContent(Doc doc)
        {
            var m = Surface.Unsynced(Large);
            var ink = InkMode.Primary with { A = Surface.UnsyncedAlpha };   // an ABSOLUTE alpha, as 0.2.9 set it
            var rows = new Element[doc.Lines.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new BoxEl
                {
                    Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Stretch,
                    Padding = new Edges4(m.SidePad, m.RowPad, m.SidePad, m.RowPad),
                    Children =
                    [
                        new TextEl(doc.Lines[i].Text)
                        {
                            Size = m.FontSize, Weight = 700, Wrap = TextWrap.Wrap, LineHeight = m.LineHeight,
                            Color = ink, MaxLines = 0, Trim = TextTrim.None,
                        },
                    ],
                };
            }
            float pad = Surface.UnsyncedBlockPad(Large);
            return new ScrollEl
            {
                Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, SuppressScrollBar = true,
                ScrollKey = "lyrics:unsynced:" + doc.TrackId,
                Content = new BoxEl { Direction = 1, Padding = new Edges4(0f, pad, 0f, pad), Children = rows },
            };
        }

        /// <summary>The loading bars, at the exact side pad the first lines will take. They animate nothing themselves:
        /// the breath is the engine's skeleton pulse on the region's first child.</summary>
        internal Element Shimmer()
        {
            var rows = new Element[Surface.ShimmerRows];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new BoxEl
                {
                    Width = Surface.ShimmerBarW(Large, i), Height = Surface.ShimmerRowH(Large),
                    Corners = CornerRadius4.All(Surface.ShimmerRadius),
                    Fill = InkMode.Skeleton,
                    AlignSelf = FlexAlign.Start,
                };
            }
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, Direction = 1, Gap = Surface.ShimmerGap(Large),
                Padding = new Edges4(Metrics.SidePad, Surface.ShimmerPadTop(Large), Metrics.SidePad, 0f),
                Children = rows,
            };
        }

        /// <summary>A centred one-line state ("No lyrics available", "Nothing playing").</summary>
        internal Element Message(string text) => new BoxEl
        {
            Grow = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(Spacing.XXL, 0f, Spacing.XXL, 0f),
            Children = [new TextEl(text) { Size = 14f, LineHeight = 20f, Color = InkMode.Secondary, Wrap = TextWrap.Wrap }],
        };

        // ── 2.5 node reports ────────────────────────────────────────────────────────────────────────────────────────

        internal void ReportLineNode(int index, NodeHandle h)
        {
            if ((uint)index < (uint)_lineNodes.Length) _lineNodes[index] = h;
        }

        internal void ReportGlowNode(int index, NodeHandle h)
        {
            if ((uint)index < (uint)_glowNodes.Length) _glowNodes[index] = h;
        }

        internal void ReportDofNode(int index, NodeHandle h)
        {
            if ((uint)index < (uint)_dofNodes.Length) _dofNodes[index] = h;
            _dofRampPending = true;   // a freshly realized row may still be NaN in the σ model
            // REALIZE-MID-CASCADE: seed the compensation at realization, never one tick later (a one-frame pop).
            float c = (uint)index < (uint)_casComp.Length ? _casComp[index] : 0f;
            if (c != 0f && Context.Scene is { } scene) WriteCascade(scene, index, c, landed: false);
        }

        // ── 2.6 the wipe feather (DIP → the engine's reading-order fraction) ────────────────────────────────────────

        float CurrentWrapWidth()
        {
            var scene = Context.Scene;
            var viewport = _viewportNode;
            if (scene is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport)) return float.NaN;
            float w = scene.ScrollRef(viewport).ViewportW - 2f * Metrics.SidePad;
            return w > 1f ? w : float.NaN;
        }

        /// <summary>The per-line softness fraction (<see cref="Wipe.SoftnessOfLine"/> over the measured run length).</summary>
        internal float SoftnessOfLine(int index) => Wipe.SoftnessOfLine(RunLengthOf(index, CurrentWrapWidth()), Large);

        /// <summary>Lazily measured reading-order run length, cached per line per width epoch (a ≥0.5 DIP wrap move
        /// retires every entry). Per-frame callers only ever read the array.</summary>
        float RunLengthOf(int index, float wrapWidth)
        {
            var runLen = _lineRunLen;
            if (float.IsNaN(wrapWidth))
            {
                float last = _runLenWrapW;
                return last > 1f ? last : Metrics.FontSize * 12f;
            }
            if (float.IsNaN(_runLenWrapW) || MathF.Abs(_runLenWrapW - wrapWidth) >= 0.5f)
            {
                _runLenWrapW = wrapWidth;
                Array.Fill(runLen, float.NaN);
            }
            if ((uint)index >= (uint)runLen.Length) return wrapWidth;
            float cached = runLen[index];
            if (!float.IsNaN(cached)) return cached;
            float measured = MeasureRunLength(index, wrapWidth);
            runLen[index] = measured;
            return measured;
        }

        /// <summary>One seam query per line per epoch. The style MUST mirror <see cref="LineRow"/>'s LineText exactly —
        /// the feather is a fraction of the extent measured here.</summary>
        float MeasureRunLength(int index, float wrapWidth)
        {
            var doc = _doc;
            if (doc is null || (uint)index >= (uint)doc.Lines.Count) return wrapWidth;
            string text = doc.Lines[index].Text;
            if (text.Length == 0 || TextSeam.Default is not { } fonts) return wrapWidth;
            var style = new TextStyle(default, Metrics.FontSize, 700, TextWrap.Wrap, TextTrim.None, 0,
                CharSpacing: 0f, LineHeight: Metrics.LineHeight);
            Span<RectF> fragments = stackalloc RectF[8];
            int n = fonts.GetRangeRects(text, in style, wrapWidth, 0, text.Length, fragments);
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += fragments[i].W;
            if (n == fragments.Length) sum = MathF.Max(sum, n * wrapWidth);
            return sum > 1f ? sum : wrapWidth;
        }

        // ── 2.7 the directional σ ramp ──────────────────────────────────────────────────────────────────────────────

        static bool SuppressesDof(FollowMode mode) => mode != FollowMode.Following;

        /// <summary>The σ the ELEMENT declares for line <paramref name="index"/>: the ramp's LIVE value, so a re-render
        /// mid-ramp re-asserts the in-flight σ instead of stomping the node back to the rest target.</summary>
        internal float DofDeclaredFor(int index)
        {
            float cur = (uint)index < (uint)_dofCurrent.Length ? _dofCurrent[index] : float.NaN;
            if (!float.IsNaN(cur)) return cur;
            return DofRamp.Target(index, _activeLine.Peek(), Large, _dofScale, SuppressesDof(Follow_.Peek()));
        }

        void ApplyDofSuppression(SceneStore? scene)
        {
            _dofRampPending = true;
            WakeMotion();   // an integrator needs frames, and this edge can arrive from a scroll callback
            if (scene is not null) DriveDofRamp(scene, FrameTime.NowMs);
        }

        void DriveDofRamp(SceneStore scene, long nowMs)
        {
            var cur = _dofCurrent;
            float dt = _dofRampMs == 0L ? DofRamp.SeedDtMs : Math.Clamp(nowMs - _dofRampMs, 0f, DofRamp.DtMaxMs);
            _dofRampMs = nowMs;
            if (!_dofRampPending || cur.Length == 0) return;

            // Strength 0 ⇒ the effect is OFF: snap every σ to 0 in ONE pass and quiesce — not the 65 ms decrease.
            if (!BlurPolicy.Enabled(_strength))
            {
                for (int i = 0; i < cur.Length; i++)
                {
                    cur[i] = 0f;
                    var h0 = (uint)i < (uint)_dofNodes.Length ? _dofNodes[i] : NodeHandle.Null;
                    if (h0.IsNull || !scene.IsLive(h0)) continue;
                    ref NodePaint p0 = ref scene.Paint(h0);
                    if (p0.BlurSigma <= DofRamp.LandWriteEps) continue;
                    p0.BlurSigma = 0f;
                    scene.Mark(h0, NodeFlags.PaintDirty);
                }
                _dofRampPending = false;
                return;
            }

            bool suppress = SuppressesDof(Follow_.Peek());
            int active = _activeLine.Peek();
            bool moving = false;
            for (int i = 0; i < cur.Length; i++)
            {
                float target = DofRamp.Target(i, active, Large, _dofScale, suppress);
                float c = DofRamp.Step(cur[i], target, dt, out bool landed);
                if (!landed) moving = true;
                cur[i] = c;
                var h = (uint)i < (uint)_dofNodes.Length ? _dofNodes[i] : NodeHandle.Null;
                if (h.IsNull || !scene.IsLive(h)) continue;
                ref NodePaint p = ref scene.Paint(h);
                if (!DofRamp.ShouldWrite(p.BlurSigma, c, landed)) continue;
                p.BlurSigma = c;
                scene.Mark(h, NodeFlags.PaintDirty);
            }
            _dofRampPending = moving;
        }

        // ── 2.8 the staggered hand-off cascade ──────────────────────────────────────────────────────────────────────

        void ArmCascade(SceneStore scene, float delta, int newActive)
        {
            if (_casComp.Length == 0 || delta == 0f) return;
            // Reduced motion read ONCE as a value: the cascade still runs (the lines must end where the latch put them),
            // with every stagger at 0 — one rigid, critically damped translate.
            bool pending = Cascade.Arm(_casComp, _casVel, _casDelay, _casRate, _casWrite, delta, newActive, Design.Reduced);
            FlushCascadeWrites(scene);
            SetCascadePending(pending);
        }

        void DriveCascade(SceneStore scene)
        {
            long qpc = FrameTime.NowQpc;
            if (_casQpc == qpc) return;   // a second step inside one frame has nothing to integrate
            float dtMs = _casQpc == 0L
                ? DofRamp.SeedDtMs
                : Math.Clamp((float)((qpc - _casQpc) * 1000.0 / Stopwatch.Frequency), 0f, Cascade.DtMaxMs);
            _casQpc = qpc;
            if (!_cascadePending || _casComp.Length == 0) return;
            bool moving = Cascade.Step(_casComp, _casVel, _casDelay, _casRate, _casWrite, dtMs);
            FlushCascadeWrites(scene);
            SetCascadePending(moving);
        }

        void FlushCascadeWrites(SceneStore scene)
        {
            var write = _casWrite;
            var comp = _casComp;
            for (int i = 0; i < write.Length; i++)
            {
                byte flag = write[i];
                if (flag == Cascade.WriteNone) continue;
                WriteCascade(scene, i, comp[i], flag == Cascade.WriteLanded);
            }
        }

        /// <summary>The ONE place a line's compensating translate reaches the scene — on <c>dofContent</c>, which declares
        /// no transform and owns no animation channel, marked TransformDirty ONLY.</summary>
        void WriteCascade(SceneStore scene, int index, float comp, bool landed)
        {
            var h = (uint)index < (uint)_dofNodes.Length ? _dofNodes[index] : NodeHandle.Null;
            if (h.IsNull || !scene.IsLive(h)) return;
            ref NodePaint p = ref scene.Paint(h);
            float currentDy = p.LocalTransform.Dy;
            if (landed ? MathF.Abs(currentDy - comp) < 0.0005f : !Cascade.ShouldWrite(Cascade.WriteMoving, comp, currentDy)) return;
            p.LocalTransform = comp == 0f ? Affine2D.Identity : Affine2D.Translation(0f, comp);
            scene.Mark(h, NodeFlags.TransformDirty);
        }

        void SetCascadePending(bool pending)
        {
            _cascadePending = pending;
            _cascadeRunning.Value = pending;
        }

        void ZeroCascade(SceneStore? scene)
        {
            var comp = _casComp;
            SetCascadePending(false);
            for (int i = 0; i < comp.Length; i++)
            {
                if (comp[i] != 0f && scene is not null) WriteCascade(scene, i, 0f, landed: true);
                comp[i] = 0f; _casVel[i] = 0f; _casDelay[i] = 0f;
            }
        }

        // ── 2.9 motion demand ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Force the surface back to per-frame motion from OUTSIDE a step (a document landing, a snap reset, a
        /// tap-seek, a σ suppression edge) — work the demand test would only discover on a frame no longer produced.</summary>
        void WakeMotion()
        {
            ClearMotionWake();
            _motionLive.Value = true;
        }

        void ArmMotionWake(long wakeAtMs, long nowMs)
        {
            if (wakeAtMs >= MotionDemand.None) { ClearMotionWake(); return; }
            if (wakeAtMs == _motionWakeAtMs) return;
            _motionWakeAtMs = wakeAtMs;
            _motionWake.Value = new MotionWake(++_motionWakeSeq, MathF.Max(1f, wakeAtMs - nowMs));
        }

        void ClearMotionWake()
        {
            if (_motionWakeAtMs == long.MinValue) return;
            _motionWakeAtMs = long.MinValue;
            _motionWake.Value = new MotionWake(++_motionWakeSeq, -1f);
        }

        /// <summary>No document, an untimed one, or sync suppressed by a video: no media deadline to re-arm from, so the
        /// ticker re-checks on a slow TIMER (which asks the host for no frames) while playback runs.</summary>
        void QuiesceUnresolved()
        {
            _motionLive.Value = false;
            ClearMotionWake();
            _motionRecheck.Value = Playback.IsPlaying.Peek();
        }

        // ── 2.10 the follow ─────────────────────────────────────────────────────────────────────────────────────────

        void SetFollowMode(FollowMode next, SceneStore? scene)
        {
            var previous = Follow_.Peek();
            if (previous == next) return;
            bool wasSuppressed = SuppressesDof(previous);
            bool nowSuppressed = SuppressesDof(next);
            Follow_.Value = next;
            // Leaving Following retires the cascade (it would fight the user's scroll / the resync glide), the dots and
            // their reserved band — at the detach EDGE, so the exits animate.
            if (next != FollowMode.Following) { ZeroCascade(scene); HideInterludeDots(); SetInterludeReserve(-1); }
            if (wasSuppressed != nowSuppressed) ApplyDofSuppression(scene);
        }

        void ResetFollowState(SceneStore? scene)
        {
            _resyncDeadlineMs = 0L;
            _resyncProgress.Value = 1f;
            ZeroCascade(scene);
            SetFollowMode(FollowMode.Following, scene);
        }

        void OnScrollActivity(bool userScrollActive, long nowMs)
        {
            if (userScrollActive)
            {
                _resyncDeadlineMs = 0L;
                _resyncProgress.Value = 1f;
                ZeroCascade(Context.Scene);
                SetFollowMode(FollowMode.DetachedActive, Context.Scene);
                return;
            }
            if (Follow_.Peek() != FollowMode.DetachedActive) return;
            _resyncDeadlineMs = nowMs + Follow.ResyncIdleMs;
            _resyncProgress.Value = 1f;
            SetFollowMode(FollowMode.DetachedIdle, Context.Scene);
        }

        void TickFollowState(SceneStore scene, long nowMs)
        {
            var mode = Follow_.Peek();
            if (mode == FollowMode.DetachedIdle)
            {
                _resyncProgress.SetIfChanged(Follow.ResyncProgress(_resyncDeadlineMs, nowMs));
                if (Follow.ResyncDue(_resyncDeadlineMs, nowMs)) { BeginResync(scene); return; }
            }
            if (mode == FollowMode.Resyncing) DriveResync(scene);
        }

        void BeginResync(SceneStore? scene)
        {
            _resyncDeadlineMs = 0L;
            _resyncProgress.Value = 1f;
            SetFollowMode(FollowMode.Resyncing, scene);
            if (scene is not null) DriveResync(scene);
        }

        void DriveResync(SceneStore scene)
        {
            int active = _activeLine.Peek();
            if (active < 0 || ScrollActiveIntoView(scene, active, FollowIntent.Resync) == FollowArm.AtTarget)
            {
                _resyncDeadlineMs = 0L;
                _resyncProgress.Value = 1f;
                SetFollowMode(FollowMode.Following, scene);   // the ladder returns only once the glide has landed
            }
        }

        /// <summary>A lyric row was clicked: back to live, commit the seek, re-anchor the clock, and HARD-latch.</summary>
        internal void SeekToLine(int index)
        {
            var doc = _doc;
            if (doc is null || (uint)index >= (uint)doc.Lines.Count) return;
            ResetFollowState(Context.Scene);
            long ms = doc.Lines[index].StartMs;
            Playback.SeekTo((int)Math.Clamp(ms, 0L, int.MaxValue));   // a line tap is a commit, never a scrub preview
            RebaseClock(ms);
            _scrollSnapped = false;
            ZeroCascade(Context.Scene);
            WakeMotion();
        }

        internal void ResetScrollSnap()
        {
            _scrollSnapped = false;
            ZeroCascade(Context.Scene);
            WakeMotion();
            ProbeActive = this;
        }

        FollowArm ScrollActiveIntoView(SceneStore scene, int active, FollowIntent intent)
        {
            var viewport = _viewportNode;
            var layout = _layout;
            if (layout is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport))
                return FollowArm.Unavailable;
            ref ScrollState sc = ref scene.ScrollRef(viewport);
            if (sc.ViewportH <= 0.5f || sc.ContentH <= 0.5f) return FollowArm.Unavailable;

            if (intent == FollowIntent.Normal)
            {
                if (Follow_.Peek() != FollowMode.Following) return FollowArm.Unavailable;
                if (sc.UserScrollActive) { OnScrollActivity(true, FrameTime.NowMs); return FollowArm.Unavailable; }
            }

            // This target runs outside layout: feed the newest published viewport first.
            layout.SetViewport(sc.ViewportH, sc.ViewportW);
            RectF item = layout.ItemRect(active, sc.ViewportW);
            float target = Follow.Target(item.Y, item.H, ReserveOf(active), sc.ViewportH, sc.ContentH, _band);

            if (!_scrollSnapped && intent == FollowIntent.Normal)
            {
                // FIRST LANDING for this document / seek: a hard jump with the cascade at rest (nothing was seen to
                // travel from).
                _scrollSnapped = true;
                LatchViewport(viewport, target);
                return FollowArm.AtTarget;
            }
            if (intent == FollowIntent.Resync) _scrollSnapped = true;

            if (intent == FollowIntent.Normal)
            {
                // The hand-off: ONE instant latch + the per-line compensating cascade.
                float delta = target - sc.OffsetY;
                if (MathF.Abs(delta) <= Follow.LatchEpsDip) return FollowArm.AtTarget;
                LatchViewport(viewport, target);
                ArmCascade(scene, delta, active);
                return FollowArm.AtTarget;
            }

            // Resync: the kernel's Driven chase flies the viewport back, velocity-continuous on a re-target.
            bool alreadyProgrammatic = sc.Activity == ScrollActivity.Driven
                                       && (sc.ActivityFlags & ScrollActivityFlags.Programmatic) != 0;
            if (!alreadyProgrammatic) _followProgrammaticTargetY = null;
            if (alreadyProgrammatic && _followProgrammaticTargetY is { } lastTarget && MathF.Abs(lastTarget - target) <= Follow.LatchEpsDip)
                return FollowArm.Armed;
            if (!alreadyProgrammatic && MathF.Abs(sc.OffsetY - target) <= Follow.LatchEpsDip)
                return FollowArm.AtTarget;
            _followProgrammaticTargetY = target;
            // ζ=1 half-life branch (zeta/omega 0): monotone, zero overshoot. Wakes a frame, never re-renders.
            ScrollIntoView.ScrollTo(Context, viewport, target, halflifeMs: Follow.ResyncHalfLifeMs, zeta: 0f, omega: 0f,
                settleVel: Follow.ResyncSettleVel);
            return FollowArm.Armed;
        }

        /// <summary>The INSTANT latch. Wakes a frame WITHOUT re-rendering — a re-render here rebuilds the virtual list and
        /// re-seeds every line's springs (the "all lines flash active" bug).</summary>
        void LatchViewport(NodeHandle viewport, float target)
        {
            _followProgrammaticTargetY = null;
            ScrollIntoView.ScrollTo(Context, viewport, target, animate: false);
        }

        // ── 2.11 the interlude lane ─────────────────────────────────────────────────────────────────────────────────

        float ReserveOf(int index) => index >= 0 && index == _interludeReserveLine ? Interlude.ReserveDip(Large) : 0f;

        /// <summary>Arm/release the anchor's reserved band through the SAME per-line emphasis signal (bit 3), so exactly
        /// the one or two affected rows re-render.</summary>
        void SetInterludeReserve(int line)
        {
            if (_interludeReserveLine == line) return;
            int prev = _interludeReserveLine;
            _interludeReserveLine = line;
            var em = _lineEmphasis;
            int active = _activeLine.Peek();
            if ((uint)prev < (uint)em.Length) em[prev].Value = Emphasis.Pack(prev, active, line);
            if ((uint)line < (uint)em.Length) em[line].Value = Emphasis.Pack(line, active, line);
            // The height reaches the extent table on the NEXT arrange: re-arrange, and keep the follow live for a few
            // frames so an ordinary latch + cascade absorbs it as motion.
            if (Context.Scene is { } sc && !_viewportNode.IsNull && sc.IsLive(_viewportNode))
                sc.Mark(_viewportNode, NodeFlags.LayoutDirty | NodeFlags.VirtualRangeDirty);
            _reserveRelatchFrames = Follow.ReserveRelatchFrames;
        }

        void HideInterludeDots()
        {
            _dotsShown.Value = false;
            _dotsRowNode = NodeHandle.Null;
        }

        void DriveInterludeDots(SceneStore scene, long nowMs, long gapStart, long gapEnd)
        {
            float progress = Interlude.Progress(nowMs, gapStart, gapEnd);
            float pulse = Interlude.Pulse(nowMs, gapStart, Design.Reduced);
            var alpha = _dotAlpha;
            for (int k = 0; k < alpha.Length; k++)
            {
                float a = Interlude.DotAlpha(k, progress, pulse);
                if (MathF.Abs(alpha[k].Peek() - a) >= Wipe.DotAlphaEps) alpha[k].Value = a;
            }

            float extraLift = 0f;
            int anchor = _activeLine.Peek();
            if (_layout is { } lay && anchor >= 0 && !_viewportNode.IsNull && scene.IsLive(_viewportNode) && scene.HasScroll(_viewportNode))
            {
                ref ScrollState sc = ref scene.ScrollRef(_viewportNode);
                float rowH = lay.ItemRect(anchor, sc.ViewportW).H - ReserveOf(anchor);
                extraLift = Interlude.ExtraLift(rowH, Large);
            }

            var h = _dotsRowNode;
            if (h.IsNull || !scene.IsLive(h)) return;
            ref NodePaint paint = ref scene.Paint(h);
            float scale = Interlude.BreathScale(pulse);
            float dy = -extraLift;
            // Both channels live in ONE matrix, so the gate compares both.
            if (MathF.Abs(paint.LocalTransform.M11 - scale) < Interlude.ScaleEps && MathF.Abs(paint.LocalTransform.Dy - dy) < Interlude.LiftEps) return;
            paint.LocalTransform = scale >= 1f && dy == 0f ? Affine2D.Identity : new Affine2D(scale, 0f, 0f, scale, 0f, dy);
            scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }

        // ── 2.12 THE FRAME DRIVER ───────────────────────────────────────────────────────────────────────────────────
        // clock → active/voice → interlude → follow → dots → emphasis → σ ramp → cascade → glow → demand → wipe

        internal void OnFrame(bool forceVisual = false, long probeNowMs = long.MinValue)
        {
            var doc = _doc;
            if (doc is null || doc.Lines.Count == 0 || !IsTimed(doc)) { QuiesceUnresolved(); return; }
            // A video is a different edit of the song: suppress sync at the ONE driver, so the whole timed apparatus
            // stops together. Peek — this is a frame callback; the content's own read subscribes.
            if (SyncGate.SyncSuppressed(Playback.VideoActive.Peek()))
            {
                if (_activeLine.Peek() != -1) _activeLine.Value = -1;
                if (_voiceLine.Peek() != -1) _voiceLine.Value = -1;
                ResetFollowState(Context.Scene);
                QuiesceUnresolved();
                return;
            }

            // RE-ARM SEED: after a quiescent gap the integrators take their first-step seed instead of spending the gap.
            if (!_motionLive.Peek() && !ProbeSyncMode) { _dofRampMs = 0L; _casQpc = 0L; }

            long wallMs = FrameTime.NowMs;
            long auth = Playback.PositionMs.Peek();
            bool playing = Playback.IsPlaying.Peek();
            long nowMs;
            if (probeNowMs != long.MinValue)
            {
                nowMs = probeNowMs; auth = probeNowMs; playing = true;
                _clock.Reset(probeNowMs, FrameTime.NowQpc, true);
            }
            else
            {
                // The host's authoritative (position, stamp) sample, fed only when it changes and mapped onto QPC through
                // its AGE; then queried at THIS frame's present time.
                var snap = Playback.Snap();
                if (snap.PosQpc != _lastSampleStamp || snap.PosMs != _lastSamplePos)
                {
                    _lastSampleStamp = snap.PosQpc;
                    _lastSamplePos = snap.PosMs;
                    long sampleQpc = SampleClock.SampleQpc(snap.PosQpc, Playback.FrameNowMs(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
                    if (_clock.OnSample(snap.PosMs, sampleQpc, playing)) OnClockJump();
                }
                else if (playing != _clock.Playing)
                {
                    // The play state flipped with no fresh sample: feed the EDGE at the authoritative position NOW.
                    if (_clock.OnSample(auth, Stopwatch.GetTimestamp(), playing)) OnClockJump();
                }
                nowMs = playing ? _clock.At(FrameTime.NowQpc) : auth;
            }

            // Always-on (never an env switch): one line per 30 s of wall time while a timed document plays.
            if (playing)
            {
                if (_lastClockLogMs == 0L) _lastClockLogMs = wallMs;
                else if (wallMs - _lastClockLogMs >= 30_000)
                {
                    _lastClockLogMs = wallMs;
                    var diag = _clock.ReadAndResetDiagnostics();
                    Log.Event(WaveeLogLevel.Info, Lyrics.Diag.Category, "lyrics.clock",
                        "media clock: " + diag.Frames + " frames, " + diag.Snaps + " snap(s)",
                        fields:
                        [
                            WaveeLogField.Of("frames", diag.Frames),
                            WaveeLogField.Of("zeroAdvanceFrames", diag.ZeroAdvanceFrames),
                            WaveeLogField.Of("maxStepMs", diag.MaxStepMs),
                            WaveeLogField.Of("slewMs", diag.SlewMs),
                            WaveeLogField.Of("snaps", diag.Snaps),
                        ]);
                }
            }

            // Eye leads voice: emphasis + follow on the LEAD-shifted clock, wipe + glow on TRUE time.
            int active = ResolveLine(doc.Lines, nowMs + LeadMs);
            int voiceLine = ResolveLine(doc.Lines, nowMs);
            // A line stops being the voice at its own SUNG-OUT point, not when the next one starts.
            if (voiceLine >= 0 && nowMs >= SungOutMs(doc, voiceLine)) voiceLine = -1;
            // A real break RETIRES the finished line: applied BEFORE `activeChanged`, so the onset is a normal hand-off.
            active = AdvancePastInterlude(doc, active, nowMs, out long gapStart, out long gapEnd);
            bool activeChanged = active != _activeLine.Peek();
            if (activeChanged) _activeLine.Value = active;
            int prevVoiceLine = _voiceLine.Peek();
            bool voiceChanged = voiceLine != prevVoiceLine;
            if (voiceChanged) _voiceLine.Value = voiceLine;
            NowMs.Value = nowMs;
            // The HELD upgrade lands inside the hand-off, and the step returns (the rows are about to be rebuilt).
            if (activeChanged && _pendingUpgrade is { } upgrade)
            {
                ApplyUpgrade(upgrade, nowMs);
                return;
            }

            // A main-content scroll defers the GLOW only; the readable wipe and the core lane are never deferred.
            bool deferHeavy = Context.PeekMainScrollBusy?.Invoke() == true;
            bool runGlow = !deferHeavy || activeChanged || voiceChanged || forceVisual;

            var scene = Context.Scene;
            if (scene is null) { QuiesceUnresolved(); return; }
            TickFollowState(scene, wallMs);

            // ── core lane ──
            bool following = Follow_.Peek() == FollowMode.Following;
            bool dotsUp = Interlude.DotsUp(following, gapStart, gapEnd, nowMs);
            // The band's edges, release-then-arm, before the follow runs (an arm and its first latch share a frame).
            if (activeChanged && _interludeReserveLine >= 0 && _interludeReserveLine != active) SetInterludeReserve(-1);
            if (dotsUp && _interludeReserveLine != active) SetInterludeReserve(active);
            bool reserveRelatch = _reserveRelatchFrames > 0;
            if (reserveRelatch) _reserveRelatchFrames--;
            if (active >= 0 && (uint)active < (uint)doc.Lines.Count && (!_scrollSnapped || activeChanged || reserveRelatch || forceVisual))
                ScrollActiveIntoView(scene, active, FollowIntent.Normal);
            if (dotsUp)
            {
                _dotsShown.Value = true;
                DriveInterludeDots(scene, nowMs, gapStart, gapEnd);
            }
            else if (_dotsShown.Peek()) HideInterludeDots();
            if (activeChanged) { PushEmphasis(); _dofRampPending = true; }
            DriveDofRamp(scene, wallMs);   // after PushEmphasis: the re-rendering rows read a σ model at the new line
            DriveCascade(scene);           // after the follow: an arm and its first integration step share one frame

            // ── visual lane ──
            if (runGlow)
            {
                if (voiceChanged) BeginGlowFades(scene, prevVoiceLine, voiceLine, wallMs);
                DriveGlowFades(scene, wallMs);
                if ((uint)voiceLine < (uint)doc.Lines.Count)
                {
                    var line = doc.Lines[voiceLine];
                    SetGlowAlpha(voiceLine, Glow.VoiceAlpha(line, line.StartMs, SungOutMs(doc, voiceLine), nowMs));
                    if (_glowInLine == voiceLine) _glowInLine = -1;
                }
            }

            // ── motion demand: this step's OUTCOME, published after every lane ran ──
            var demand = MotionDemand.Evaluate(new MotionLanes(
                Playing: playing,
                VoiceActive: voiceLine >= 0,
                DotsActive: dotsUp,
                GlowFadeActive: _glowOutLine >= 0,
                DofRampPending: _dofRampPending,
                CascadePending: _cascadePending,
                // An owed first landing counts only while there is a line to land on (an intro is not motion).
                FollowUnsettled: (!_scrollSnapped && active >= 0) || _reserveRelatchFrames > 0,
                Following: Follow_.Peek() == FollowMode.Following,
                NowMs: nowMs,
                NextEventMs: MotionDemand.NextEventMs(doc.Lines, active, nowMs, LeadMs)));
            _motionRecheck.Value = false;
            _motionLive.Value = demand.NeedsTicks;
            if (demand.NeedsTicks) ClearMotionWake();
            else ArmMotionWake(demand.WakeAtMs, nowMs);

            // ── the karaoke wipe on the VOICE line (line-synced rows carry no wipe, so this no-ops for them) ──
            if ((uint)voiceLine >= (uint)_lineNodes.Length) return;
            var mainNode = _lineNodes[voiceLine];
            var glowNode = (uint)voiceLine < (uint)_glowNodes.Length ? _glowNodes[voiceLine] : NodeHandle.Null;
            if (mainNode.IsNull || !scene.IsLive(mainNode) || !scene.TryGetGlyphWipe(mainNode, out var mw)) return;

            float split = Wipe.ComputeSplit(doc.Lines[voiceLine], nowMs);
            if (split > 0f && split < 1f) split = Math.Clamp(split + Wipe.LeadFrac, 0f, 1f);
            // Pixel-quantise the boundary (skip-submit elides sub-pixel ticks); the settled endpoints stay EXACT.
            float runW = scene.AbsoluteRect(mainNode).W;
            if (runW > 1f && split > 0f && split < 1f) split = MathF.Round(split * runW) / runW;
            // A seed resolved before the viewport had geometry self-heals into the same write.
            float softness = SoftnessOfLine(voiceLine);

            if (!Wipe.SplitSettled(split, mw.Split)
                && (MathF.Abs(split - mw.Split) > Wipe.SplitEps || MathF.Abs(softness - mw.Softness) > Wipe.SoftnessEps))
            {
                scene.SetGlyphWipe(mainNode, mw with { Split = split, Softness = softness });
                scene.Mark(mainNode, NodeFlags.PaintDirty);
            }

            if (runGlow && !glowNode.IsNull && scene.IsLive(glowNode) && scene.TryGetGlyphWipe(glowNode, out var gw))
            {
                // The halo tracks the main layer's geometry EXACTLY, with the same settled write-stop.
                bool glowDirty = !Wipe.SplitSettled(split, gw.Split)
                    && (MathF.Abs(split - gw.Split) > Wipe.SplitEps || MathF.Abs(softness - gw.Softness) > Wipe.SoftnessEps);
                if (glowDirty) scene.SetGlyphWipe(glowNode, gw with { Split = split, Softness = softness });
                // NEVER NEST: while the row's own DoF σ is up, the halo does not paint (a nested blur layer is pin-ineligible).
                float sigma = Wipe.GlowSigma(DofDeclaredFor(voiceLine), GlowAlphaOf(voiceLine), Large, _dofScale);
                ref var gp = ref scene.Paint(glowNode);
                if (MathF.Abs(gp.BlurSigma - sigma) > 0.01f) { gp.BlurSigma = sigma; glowDirty = true; }
                if (glowDirty) scene.Mark(glowNode, NodeFlags.PaintDirty);
            }
        }

        /// <summary>A snap (first sample, a real seek / transfer / track change): the next follow is an instant latch,
        /// and any in-flight cascade was measured against geometry the jump invalidates.</summary>
        void OnClockJump()
        {
            _scrollSnapped = false;
            ZeroCascade(Context.Scene);
        }

        // ── 2.13 the halo cross-fade ────────────────────────────────────────────────────────────────────────────────

        /// <summary>Arm the voice hand-off. Each fade starts from the LIVE alpha; a THIRD in-flight out-fade is finished
        /// instantly — at most <see cref="Wipe.MaxGlowFades"/> halos ever animate.</summary>
        void BeginGlowFades(SceneStore scene, int prev, int next, long nowMs)
        {
            if (_glowOutLine >= 0 && _glowOutLine != prev && _glowOutLine != next) FinishGlowOut(scene, _glowOutLine);
            _glowOutLine = prev;
            _glowOutStartMs = nowMs;
            _glowOutFrom = GlowAlphaOf(prev);
            _glowInLine = next;
        }

        void DriveGlowFades(SceneStore scene, long nowMs)
        {
            if (_glowOutLine < 0) return;
            float a = Glow.FadeOut(_glowOutFrom, nowMs - _glowOutStartMs);
            if (a <= 0f) { FinishGlowOut(scene, _glowOutLine); _glowOutLine = -1; }
            else SetGlowAlpha(_glowOutLine, a);
        }

        internal float GlowAlphaOf(int line) => (uint)line < (uint)_glowAlpha.Length ? _glowAlpha[line].Peek() : 0f;

        void SetGlowAlpha(int line, float a)
        {
            if ((uint)line < (uint)_glowAlpha.Length) _glowAlpha[line].Value = a;
        }

        void FinishGlowOut(SceneStore scene, int line)
        {
            SetGlowAlpha(line, 0f);
            // A word-by-word halo σ is paint-driven: return it to rest once invisible so the layer costs nothing.
            if (_doc is { } d && (uint)line < (uint)d.Lines.Count && d.Lines[line].IsWordByWord && (uint)line < (uint)_glowNodes.Length)
            {
                var g = _glowNodes[line];
                if (g.IsNull || !scene.IsLive(g)) return;
                ref var gp = ref scene.Paint(g);
                bool dirty = false;
                if (gp.BlurSigma != 0f) { gp.BlurSigma = 0f; dirty = true; }
                if (gp.BlurCachePolicy != BlurCachePolicy.Normal) { gp.BlurCachePolicy = BlurCachePolicy.Normal; dirty = true; }
                if (dirty) scene.Mark(g, NodeFlags.PaintDirty);
            }
        }
    }

    // ══ 3. THE MEASURED LAYOUT ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The lyrics list's variable-height layout: every row is CONTENT-FIT (a one-line lyric short, a two-line
    /// lyric tall — no dead space, nothing clipped), over the engine's estimate-then-correct seam, plus a top/bottom
    /// focal pad so the FIRST and LAST lines can still reach the band. Stateful and allocation-free.</summary>
    internal sealed class MeasuredLayout : IMeasuredVirtualLayout, IViewportVirtualLayout
    {
        public readonly float Estimate;
        public readonly float Band;
        float _viewport;
        ExtentTable? _table;

        public MeasuredLayout(float estimate, float band)
        {
            Estimate = MathF.Max(1f, estimate);
            Band = Math.Clamp(band, 0.05f, 0.95f);
        }

        ExtentTable Ensure(int n)
        {
            if (_table is null) _table = new ExtentTable(n, Estimate);
            else if (_table.Count != n) _table.Reset(n, Estimate);
            return _table;
        }

        // The band centres a row's MIDDLE; Estimate·0.5 is the half-height correction (the active row is always measured).
        float TopPad => _viewport <= 0f ? 0f : MathF.Max(0f, _viewport * Band - Estimate * 0.5f);
        float BottomPad => _viewport <= 0f ? 0f : MathF.Max(0f, _viewport * (1f - Band) - Estimate * 0.5f);

        public void SetViewport(float mainExtent, float crossSize) => _viewport = MathF.Max(0f, mainExtent);

        public float ContentExtent(int itemCount, float crossSize)
            => itemCount <= 0 ? 0f : TopPad + (float)Ensure(itemCount).Total + BottomPad;

        public void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan, out int first, out int last)
        {
            if (itemCount <= 0) { first = last = 0; return; }
            var t = Ensure(itemCount);
            float o = scrollOffset - TopPad;
            first = Math.Max(0, t.IndexAt(MathF.Max(0f, o)) - overscan);
            last = Math.Min(itemCount, t.IndexAt(MathF.Max(0f, o + viewportExtent)) + 1 + overscan);
            if (last < first) last = first;
        }

        public RectF ItemRect(int index, float crossSize)
        {
            float pos = _table?.OffsetOf(index) ?? index * Estimate;
            float ext = _table?.ExtentAt(index) ?? Estimate;
            return new RectF(0f, TopPad + pos, crossSize, ext);
        }

        public void SetMeasured(int index, float mainExtent, float crossSize) => _table?.SetExtent(index, mainExtent);
        public float OffsetOf(int index, float crossSize) => TopPad + (_table?.OffsetOf(index) ?? index * Estimate);
        public int IndexAt(float offset, float crossSize) => _table?.IndexAt(MathF.Max(0f, offset - TopPad)) ?? 0;
    }

    // ══ 4. THE ROW ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One lyric line. Frozen at mount: the owner, its index, its <see cref="Line"/>, its emphasis signal and its
    /// halo signal — a genuine document change re-KEYS the row. Live values only through signals: its OWN packed
    /// emphasis (<c>.Value</c>), the owner's shared <c>NowMs</c> (peeked), <c>Secondary</c> (<c>.Value</c>),
    /// <c>HaloScale</c> (<c>.Value</c>, inside the active branch only) and the follow mode (peeked).</summary>
    sealed class LineRow(ViewCore owner, int index, Line line, Signal<int> emphasis, FloatSignal? glowFade) : Component
    {
        Prop<float> GlowOpacity()
        {
            if (glowFade is not { } s) return 0f;
            float k = owner.InkMode.BloomScale;
            return k >= 1f ? (Prop<float>)s : Prop.Of(() => s.Value * k);
        }

        public override Element Render()
        {
            int e = emphasis.Value;                          // THIS line's packed word only
            int dist = Emphasis.DistOf(e);
            bool isActive = dist == 0;
            bool past = (e & Emphasis.PastBit) != 0;
            bool large = owner.Large;
            var ink = owner.InkMode;
            var m = owner.Metrics;
            float reserve = Emphasis.HasReserve(e) ? Interlude.ReserveDip(large) : 0f;

            // Distance is carried by OPACITY + DoF; scale is a flat 0.98, left-anchored. Reduced motion is a VALUE
            // folded into the spring key, so the first render after an OS flip retargets.
            bool reduce = Design.Reduced;
            float scale = Surface.ScaleFor(isActive, reduce);
            float opacity = Emphasis.OpacityOf(e);
            float blur = owner.Follow_.Peek() == FollowMode.Following ? owner.DofDeclaredFor(index) : 0f;

            var key = DepKey.From(dist, (isActive ? 2 : 0) | (past ? 4 : 0) | (reduce ? 8 : 0));
            // Critically damped at AMLL's stiffness/mass; the brightness hand-off is symmetric (one response).
            UseSpring(AnimChannel.Opacity, opacity, SpringParams.FromResponse(0.18f, 1.0f), key);
            var scaleSpring = new SpringParams(100f, 2f * MathF.Sqrt(100f * 2f), 2f);
            UseSpring(AnimChannel.ScaleX, scale, scaleSpring, key);
            UseSpring(AnimChannel.ScaleY, scale, scaleSpring, key);

            Element textEl;
            float lift = Wipe.LiftFor(large, reduce);
            if (line.IsWordByWord && line.Syllables.Count > 0)
            {
                // ALWAYS a two-child ZStack [glow, main], in every state: a line leaving the voice slot is an in-place
                // update, never a child-type swap (a re-shape + blur-cache miss = the lines-above-active flicker).
                float split = Wipe.ComputeSplit(line, (long)owner.NowMs.Peek());
                if (split > 0f && split < 1f) split = Math.Clamp(split + Wipe.LeadFrac, 0f, 1f);
                float softness = owner.SoftnessOfLine(index);
                ColorF sung = ink.Primary;
                // Sung glyphs full-bright; unsung the same ink at the ABSOLUTE unsung alpha, sitting `lift` DIP low.
                var mainWipe = new GlyphWipe(Before: sung, After: sung with { A = Lyrics.Wipe.UnsungAlpha },
                    Split: split, Softness: softness, Lift: lift);
                Element main = LineText(line.Text, sung) with
                {
                    Wipe = mainWipe,
                    OnRealized = h => owner.ReportLineNode(index, h),
                };
                // The bloom glyphs mount only NEAR the focus (still dim + blurred, so the swap never pops on the focal
                // row); same split, feather and lift as the main layer, so the halo never floats out from under it.
                bool near = dist <= Surface.NearDistance;
                var bloom = ink.Bloom;
                var glowWipe = new GlyphWipe(Before: bloom, After: bloom with { A = 0f }, Split: split, Softness: softness, Lift: lift);
                Element glowText = (near ? LineText(line.Text, bloom) with { Wipe = glowWipe } : LineText("", bloom))
                    with { OnRealized = h => owner.ReportGlowNode(index, h) };
                Element glow = new BoxEl { Opacity = GlowOpacity(), HitTestVisible = false, Children = [glowText] };
                textEl = new BoxEl { ZStack = true, Children = [glow, main] };
            }
            else
            {
                // Line-level lyrics: the same persistent ZStack. σ on the ACTIVE row only — at dist ≥ 1 the parent already
                // carries a DoF σ and a nested blur layer is pin-ineligible.
                bool near = dist <= Surface.NearDistance;
                Element glow = new BoxEl
                {
                    Blur = isActive ? Surface.LineSyncedHaloSigma(large) * owner.HaloScale.Value : 0f,
                    BlurCachePolicy = BlurCachePolicy.HoldIfCached,
                    Opacity = GlowOpacity(),
                    HitTestVisible = false,
                    Children = [LineText(near ? line.Text : "", ink.Bloom with { A = Surface.LineSyncedHaloTextAlpha })],
                };
                Element main = LineText(line.Text, isActive ? ink.Primary : ink.Secondary) with
                {
                    BrushTransitionMs = Design.Motion.Fast,   // the Secondary↔Primary flip never snaps in one frame
                    OnRealized = h => owner.ReportLineNode(index, h),
                };
                textEl = new BoxEl { ZStack = true, Children = [glow, main] };
            }

            // The secondary layer, read from the shared mode signal (never a frozen int).
            string? secondaryText = owner.Secondary.Value switch
            {
                Prefs.Translation => NonEmpty(line.Translation),
                Prefs.Romanization => NonEmpty(line.Romanization),
                _ => null,
            };

            // Own DoF on a persistent INNER wrapper, separate from the scale/opacity track owner — the σ ramp and the
            // cascade write THIS node. The secondary line sits INSIDE it, so it blurs, fades and travels with its lyric.
            Element dofContent = new BoxEl
            {
                Direction = 1,
                Blur = blur,
                BlurCachePolicy = BlurCachePolicy.HoldIfCached,
                OnRealized = h => owner.ReportDofNode(index, h),
                Children = secondaryText is null ? [textEl] : [textEl, SecondaryText(secondaryText)],
            };

            return new BoxEl
            {
                Direction = 1,
                // The springs' REST targets: at settle the slab and the element agree, so a later emphasis render cannot
                // snap the row or flash a newly realized one.
                ScaleX = scale,
                ScaleY = scale,
                Opacity = opacity,
                Shrink = 0f,
                // PAD, not margin: the measured seam reads the border box.
                Padding = new Edges4(m.SidePad, m.RowPad + reserve, m.SidePad, m.RowPad),
                Justify = FlexJustify.Center,
                AlignItems = FlexAlign.Stretch,
                TransformOriginX = 0f,
                TransformOriginY = 0.5f,
                Cursor = CursorId.Hand,
                OnClick = () => owner.SeekToLine(index),
                Role = AutomationRole.Button,
                Focusable = true,
                AllowFocusOnInteraction = false,
                Children = [dofContent],
            };

            // Wrapped, unbounded, never trimmed on BOTH surfaces. `ViewCore.MeasureRunLength` rebuilds this style — the
            // two change together.
            TextEl LineText(string text, ColorF color) => new(text)
            {
                Size = m.FontSize, Weight = 700, Wrap = TextWrap.Wrap, LineHeight = m.LineHeight,
                Color = color, MaxLines = 0, Trim = TextTrim.None,
            };

            // No wipe, no glow, no lift (a translation is not sung); everything else inherited from dofContent.
            TextEl SecondaryText(string text) => new(text)
            {
                Size = m.FontSize * Surface.SecondaryFontRatio, Weight = 600, Wrap = TextWrap.Wrap,
                LineHeight = m.LineHeight * Surface.SecondaryFontRatio,
                Color = ink.Secondary, MaxLines = 0, Trim = TextTrim.None,
                Margin = new Edges4(0f, Surface.SecondaryGapDip, 0f, 0f),
            };
        }

        static string? NonEmpty(string? s) => s is { Length: > 0 } ? s : null;
    }

    // ══ 5. THE TICKER AND THE STEPPER ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The surface's clock host (keyed per track, mounted only while the surface is visible). It wakes the
    /// step on a transport or position edge, mounts the per-frame <see cref="Stepper"/> ONLY while a lane is moving, and
    /// otherwise re-arms from a one-shot timeout at the next media instant — "lyrics wake only for the words".</summary>
    sealed class Ticker(ViewCore owner) : Component
    {
        public override Element Render()
        {
            // Once per mount (a track change): the next step owes a first landing.
            UseEffect(() => owner.ResetScrollSnap(), DepKey.Empty);

            _ = Playback.IsPlaying.Value;                  // re-evaluate the gates on a transport edge
            var follow = owner.FollowModeValue;            // isolated subscriptions: never re-render the view or a row
            bool cascading = owner.CascadeRunningValue;
            bool motionLive = owner.MotionLiveValue;

            // The play-start edge is one immediate step, and a position publication in EITHER state carries a scrub or a
            // seek to a surface whose stepper is unmounted mid-gap.
            UseSignalEffect(() =>
            {
                bool playing = Playback.IsPlaying.Value;
                _ = Playback.PositionMs.Value;
                if (ViewCore.ProbeSyncMode && playing) return;
                owner.OnFrame(forceVisual: !playing);
            });

            // Mounting CONDITIONALLY (not gating inside) is what lets the loop idle: an unmounted stepper is not a
            // frame-clock subscriber, so it contributes no wake reason at all. The two edges beside `motionLive` arm work
            // from OUTSIDE a step: a cascade that must finish across a pause, and the detached/resync countdown.
            bool needsTicks = motionLive || cascading || follow != FollowMode.Following;
            Element? stepper = needsTicks && !ViewCore.ProbeSyncMode ? Embed.Comp(() => new Stepper(owner)) : null;

            // The RE-ARM: one shot at the next event minus MotionDemand.ArmLeadMs. The Seq restarts it.
            var wake = owner.MotionWakeValue;
            var timer = UseTimeout(owner._wakeTick, MathF.Max(wake.DelayMs, 1f), DepKey.From(wake.Seq));
            if (needsTicks || wake.DelayMs < 0f) timer.Cancel();

            // …and the slow re-check for the states with no media deadline (no document, untimed, video-suppressed).
            UseInterval(owner._wakeTick, MotionDemand.UnresolvedRecheckMs, enabled: owner.MotionRecheckValue && !needsTicks);

            return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f, Children = stepper is null ? [] : [stepper] };
        }
    }

    /// <summary>The per-frame step: a frame-clock subscriber running ONE step per produced frame. Its subscription is the
    /// request for those frames; unmounting it lets the loop idle. It never re-renders its owner.</summary>
    sealed class Stepper(ViewCore owner) : Component
    {
        public override Element Render()
        {
            var tick = UseContextSignal(FrameClock.Tick);
            UseSignalEffect(() =>
            {
                _ = tick.Value;
                owner.OnFrame();
            });
            return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
        }
    }

    // ══ 6. THE DEVELOPER SURFACE (the "lyrics debug" pill + overlay — KEPT, gated by Developer mode) ═══════════════════

    /// <summary>The lyrics-search debug pill and its overlay: a strict subset of the inspector (identity block + per-source
    /// rows), re-homed on the persisted <c>diag.developerMode</c> setting instead of the deleted env var (plan §9.6 Q1).
    /// It keeps THEME ink on purpose — it is a plate, not the reading surface.</summary>
    sealed class DebugLayer(ViewCore owner) : Component
    {
        public override Element Render()
        {
            _ = Platform.SettingsChanged.Value;   // subscribe → a Developer-mode flip mounts/unmounts it live
            bool dev = Platform.Settings.Get(Platform.Keys.DeveloperMode);
            if (!dev) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
            if (owner.DebugOpen.Value) return Overlay(owner.TrackId);
            // ink-scan: off (the developer plate keeps theme ink)
            return new BoxEl
            {
                // A full-bleed PASS-THROUGH positioner: only the pill is hittable, the lyrics stay scrollable under it.
                Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
                Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                Padding = new Edges4(0f, 0f, 12f, 12f),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
                        Fill = Tok.FillSolidBase with { A = 0.90f }, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                        Cursor = CursorId.Hand, OnClick = () => owner.DebugOpen.Value = true,
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                        Children = [new TextEl(Loc.Get(Strings.Lyrics.Debug.Pill)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary }],
                    },
                ],
            };
        }

        Element Overlay(string trackId)
        {
            var report = Diag.ForTrack(trackId);
            var rows = new List<Element>
            {
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Lyrics.Debug.Title)) { Size = 18f, LineHeight = 24f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f },
                        new BoxEl
                        {
                            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
                            Fill = Tok.FillSubtleSecondary, Cursor = CursorId.Hand, OnClick = () => owner.DebugOpen.Value = false,
                            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                            Children = [new TextEl(Loc.Get(Strings.Lyrics.Debug.Close)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary }],
                        },
                    ],
                },
            };

            if (report is null)
            {
                rows.Add(new TextEl(Loc.Get(Strings.Lyrics.Debug.NoReport))
                { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
            }
            else
            {
                rows.Add(new TextEl(report.Summary) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.AccentTextPrimary, Wrap = TextWrap.Wrap });
                string artist = report.Artist.Length > 0 ? report.Artist : Loc.Get(Strings.Lyrics.Debug.NoArtist);
                rows.Add(new TextEl("“" + report.Title + "” — " + artist)
                { Size = 12f, LineHeight = 16f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap });
                rows.Add(new TextEl((report.Album.Length > 0 ? report.Album : "—") + "   ·   " + report.DurationMs / 1000 + "s   ·   ISRC " + (report.Isrc ?? "—"))
                { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
                rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault });
                foreach (var t in report.Sources) rows.Add(SourceRow(t));
                if (report.Sources.Count == 0)
                    rows.Add(new TextEl(Loc.Get(Strings.Lyrics.Debug.NoSources)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary });
            }

            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, Direction = 1, Fill = Tok.FillSolidBase with { A = 0.97f },
                Children =
                [
                    new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f,
                        Content = new BoxEl { Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.L), Children = rows.ToArray() },
                    },
                ],
            };
        }

        static Element SourceRow(SourceTrace t)
        {
            ColorF dot = t.Outcome switch
            {
                Outcome.Hit => new ColorF(0.30f, 0.78f, 0.45f, 1f),
                Outcome.Timeout => new ColorF(0.92f, 0.70f, 0.25f, 1f),
                Outcome.Error => new ColorF(0.90f, 0.35f, 0.38f, 1f),
                Outcome.Skipped => new ColorF(0.40f, 0.42f, 0.50f, 1f),
                _ => new ColorF(0.55f, 0.57f, 0.62f, 1f),
            };
            string outcome = t.Outcome switch
            {
                Outcome.Hit => "HIT", Outcome.Miss => "MISS", Outcome.Timeout => "TIMEOUT",
                Outcome.Error => "ERROR", Outcome.Skipped => "SKIPPED", _ => "PENDING",
            };
            var lines = new List<Element>(3)
            {
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Children =
                    [
                        new BoxEl { Width = 8f, Height = 8f, Corners = Radii.Circle(8f), Fill = dot },
                        new TextEl(t.SourceId) { Size = 14f, LineHeight = 20f, Weight = 600, Color = t.Winner ? Tok.AccentTextPrimary : Tok.TextPrimary },
                        new TextEl(outcome + " · " + t.ElapsedMs + "ms" + (t.Winner ? "  " + Loc.Get(Strings.Lyrics.Debug.Winner) : ""))
                        { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary, Grow = 1f },
                    ],
                },
            };
            if (t.Detail.Length > 0)
                lines.Add(new TextEl(t.Detail) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
            if (t.Outcome == Outcome.Hit && t.RerankReason.Length > 0)
                lines.Add(new TextEl("rerank " + t.Score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "  ·  " + t.RerankReason)
                { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
            return new BoxEl { Direction = 1, Gap = Spacing.XS, Children = lines.ToArray() };
        }
        // ink-scan: on
    }

    // ══ 7. THE NPV LYRICS PEEK ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The peek's per-track key: the reel remounts per track (<c>"npv-lyrics:" + id</c>), so its pair and its
    /// seed never carry across a change.</summary>
    sealed class PeekHost : Component
    {
        public override Element Render()
        {
            var current = Playback.Current.Value;
            string trackId = current.Kind == EntityKind.Track && !current.IsNone ? Store.IdOf(new Track(current.Slot)) : "";
            if (trackId.Length == 0) return new BoxEl();
            return Embed.Comp(() => new PeekReel(trackId)) with { Key = "npv-lyrics:" + trackId };
        }
    }

    /// <summary>The two-row reel: the sung line over a faded peek at the next, on a 3-DIP accent spine. It plans NO
    /// fetch of its own — it reads the ONE store document — and ticks on its own 100 ms interval, which runs only while
    /// the reel shows: stopping the clock IS the suppression.</summary>
    sealed class PeekReel(string trackId) : Component
    {
        const float RowH = 56f, SpineW = 3f, PeekOpacity = 0.38f, TickMs = 100f;
        static readonly EnterExit FlipIn = new(Dy: RowH, Opacity: 0f, Active: true);
        static readonly EnterExit FlipOut = new(Dy: -RowH, Opacity: 0f, Active: true);

        readonly Signal<(int Active, int Peek)> _pair = new((int.MinValue, int.MinValue));
        Doc? _doc;
        Action? _tick;

        public override Element Render()
        {
            var tick = _tick ??= Tick;   // cached: the interval never re-arms on a re-render
            UseLayoutEffect(() => Store.Ensure(trackId), DepKey.Empty);
            _ = Store.Changed.Value;
            var doc = Store.Doc(trackId);
            // A video is a different edit: the peek becomes the note, and its clock stops.
            bool syncOff = SyncGate.SyncSuppressed(Playback.VideoActive.Value);
            bool show = PeekClock.ShouldShow(doc);
            _doc = show && !syncOff ? doc : null;
            UseInterval(tick, TickMs, enabled: show && !syncOff);
            UseLayoutEffect(() => { if (_doc is not null) Tick(); }, DepKey.From(show));

            var pair = _pair.Value;
            // ORDER matters: the `!show` early-out runs BEFORE the video branch — over an unsynced or absent document a
            // video shows NOTHING (the note is a state of a synced document, never a stand-in for a missing one).
            if (!show) return new BoxEl();
            if (syncOff) return UnsyncedNote();
            if (pair.Active == int.MinValue)
            {
                // Seed synchronously, so the reel never shows an empty pair for a tick.
                var seed = PeekClock.ActiveAndPeek(doc, PositionNow());
                pair = (seed.Active, seed.Peek);
            }

            var lines = doc!.Lines;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Stretch, Gap = Spacing.S,
                Height = RowH * 2f,
                Cursor = CursorId.Hand,
                OnClick = static () => Shell.Ui.Toggle(Shell.RailMode.Lyrics),
                Role = AutomationRole.Button,
                Focusable = true,
                Children =
                [
                    new BoxEl { Width = SpineW, Shrink = 0f, Corners = CornerRadius4.All(1.5f), Fill = Tok.AccentDefault },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Height = RowH * 2f, ClipToBounds = true,
                        Children = [Slot(pair.Active, lines, faded: false), Slot(pair.Peek, lines, faded: true)],
                    },
                ],
            };
        }

        /// <summary>One 56-DIP row: the spine at tertiary, half height, and the sync note — still opening the rail.</summary>
        static Element UnsyncedNote() => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Height = RowH,
            Cursor = CursorId.Hand,
            OnClick = static () => Shell.Ui.Toggle(Shell.RailMode.Lyrics),
            Role = AutomationRole.Button,
            Focusable = true,
            Children =
            [
                new BoxEl { Width = SpineW, Shrink = 0f, Height = RowH * 0.5f, Corners = CornerRadius4.All(1.5f), Fill = Tok.TextTertiary },
                new TextEl(Loc.Get(Strings.Player.LyricsSyncUnavailableDuringVideo))
                {
                    Size = 12f, Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };

        /// <summary>The playback position at the host's frame clock: the last authoritative sample extrapolated (the
        /// reducer's own <c>State.Position</c>), so the reel does not wait on the ~1 Hz position publication.</summary>
        static long PositionNow() => Playback.Snap().Position(Playback.FrameNowMs());

        void Tick()
        {
            var doc = _doc;
            if (doc is null) { _pair.SetIfChanged((-1, -1)); return; }
            var next = PeekClock.ActiveAndPeek(doc, PositionNow());
            _pair.SetIfChanged((next.Active, next.Peek));
        }

        static Element Slot(int index, IReadOnlyList<Line> lines, bool faded)
        {
            Element inner = (uint)index < (uint)lines.Count
                ? new BoxEl
                {
                    Key = "l:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Height = RowH, MinWidth = 0f, Grow = 1f, Basis = 0f,
                    Direction = 1, Justify = FlexJustify.Center,
                    Enter = FlipIn, Exit = FlipOut,
                    Transition = MotionTok.ControlFast,
                    Children =
                    [
                        Design.Type.NpvLyric(lines[index].Text) with
                        {
                            Color = Tok.TextPrimary, MaxLines = 2, MinWidth = 0f,
                            Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                }
                : new BoxEl { Height = RowH };
            return new BoxEl
            {
                Height = RowH, ClipToBounds = true, MinWidth = 0f,
                Opacity = faded ? PeekOpacity : 1f,
                Children = [inner],
            };
        }
    }
}
