// ── Shell/Deck.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// host, clock, signals, art, gesture, dispatch, the record family
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 1500 lines
// Spec: ch 23 §9 (1,450-1,600)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE SHELL OF THE DECK. `Deck.cs` is the physics; this file is everything that touches the engine around it:
//
//   Face()   the mount point — one style's deck at the side the now-playing stage (`Deck.Stage.cs`) gives it, keyed "deck:<slug>".
//   Host     the layout firewall (Width = Height = side, ClipToBounds, IsolateLayout, 8-DIP corners, 320 ms entrance)
//            holding exactly two children: the face (built once per render of the host, all binds) and the Clock.
//   Clock    a 0×0 component that owns the only timer. It samples `Playback.PositionMs` against the FRAME clock
//            (`FrameTime`), folds the transport into a `Deck.Input`, ticks the model and diff-writes one frame
//            into the `Slab` inside ONE batch, value-gated at a perceptual quantum. `ClockRules.ShouldTick` decides
//            whether the timer runs at all: a paused, settled, ended, reduced-motion or rail-closed deck runs none and
//            requests no frame; a transport CHANGE is then the clock.
//   Slab     the fixed set of signals every face binds at build and the clock writes.
//   Gesture  the one scrub gesture per host (headshell, click wheel) — `GripRules` decides, `Playback.SeekTo` commits.
//   the record family (Vinyl's turntable, Metro's poster): one face, two skins, over one tonearm machine.
//
// Stage-2 fixes owed by ch 23 §6.4, delivered here: (#1) the headshell wires OnDragCanceled → Gesture.Cancel; (#6)
// both grips refuse over an unknown duration (`GripRules.Enabled`). §6.4(5) (the paused analyser that never settled) is
// the model's own `IsSettled`, in `Deck.cs`. (#4, a model option reaching a mounted model, went with the options: the
// 2026-11-08 styles have none.)
//
// THE ANALYSER DECKS READ THE REAL FFT (docs/plans/wavee/fullscreen-flagship-implementation.md §4.13). Skin and Visualizer hold
// a SPECTRUM lease and read `SpectrumFold` — the rail's own fold of the engine's 48 bands — so nothing synthesises a
// band any more; the VU holds the level lease and rests without a tap.
//
// Rules: faces are FUNCTIONS, built once per host render; everything that moves is a bound Transform/Opacity/Text over
// the slab; no allocation on the tick path; the live track is read INSIDE thunks and leaf components, never in a face
// body (that would rebuild the face on every track change).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Deck
{
    // ══ 1. THE MOUNT POINT ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The deck's square edge at the default rail width (340 − 2·8 = 324).</summary>
    public static float DefaultSide => Shell.RailDefaultW - 2f * Spacing.S;

    /// <summary>One style's deck: a <c>side × side</c> square keyed <c>"deck:&lt;slug&gt;"</c> (another style is another deck, so the picker's
    /// carousel holds one per style it shows). A re-pushed side redraws it in place (its model, clock and gesture keep running). The caller
    /// (the now-playing stage, <see cref="Stage"/>) owns the "nothing is playing" arm and the side; only a deck style comes here
    /// (<see cref="Rail.PlayerCatalog.IsDeck"/>): the cover and the canvas are the stage's own.</summary>
    internal static Element Face(int styleId, float side)
    {
        var style = Rail.PlayerCatalog.ById(styleId);
        return Embed.Comp(new HostSide(side), () => new Host { Style = style }) with { Key = "deck:" + style.Slug };
    }

    // ══ 2. THE SLAB ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ONE channel between the physics and the pixels: a fixed slab the faces bind at build and the clock
    /// diff-writes once per tick. The per-deck meaning of each float is documented on <see cref="Frame"/>. Resting values
    /// are the record family's "nothing has happened yet" pose.</summary>
    public sealed class Slab
    {
        /// <summary>24 covers the widest deck (the visualizer); the skin binds the first 19.</summary>
        public const int BandCount = 24;

        public readonly FloatSignal Frac = new(0f), Angle0 = new(0f), Angle1 = new(TonearmMachine.RestDeg),
                                    Lift = new(1f), Slide = new(1f), Aux0 = new(0f), Aux1 = new(0f);
        public readonly FloatSignal[] Bands = Make(BandCount), Peaks = Make(BandCount);

        /// <summary>Bumps at the instant the artwork should change (mid-sleeve, mid-eject) — NOT on a track change.</summary>
        public readonly Signal<int> CoverGen = new(0);
        public readonly Signal<PhaseName> Phase = new(PhaseName.Idle);

        /// <summary>A committed seek awaiting its report (the grip writes it, the clock releases it).</summary>
        public readonly Signal<long?> SeekTargetMs = new(null);
        /// <summary>Pointer-owned position: non-null MEANS a drag is live right now.</summary>
        public readonly Signal<long?> ScrubTargetMs = new(null);
        /// <summary>May a face's LOOPING motion run (<see cref="ClockRules.LoopsMayRun"/>)? The skin's marquee gates on it, so a paused
        /// deck owns no looping animation row.</summary>
        public readonly Signal<bool> LoopsRun = new(false);

        /// <summary>The frame-clock stamp of the last commit (0 = none) — the latch window's origin.</summary>
        public long SeekCommittedAtMs;

        static FloatSignal[] Make(int n)
        {
            var a = new FloatSignal[n];
            for (int i = 0; i < n; i++) a[i] = new FloatSignal(0f);
            return a;
        }
    }

    // ══ 3. THE HOST ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The deck's edge, re-pushed by the stage on a column quantum (the deck keeps its node, its model and its clock).</summary>
    sealed record HostSide(float Side);

    /// <summary>The square that IS a deck. Re-renders on the prefs epoch (a finish pick restyles the face IN PLACE) and
    /// never on the transport. The model is created from the CURRENT transport (<see cref="Seed"/>), so a deck mounted
    /// mid-song shows a record that was already playing.</summary>
    sealed class Host : Component
    {
        /// <summary>320 ms opacity entrance — KEPT under reduced motion (a cross-fade between two hero contents).</summary>
        static readonly LayoutTransition Entrance = new(TransitionChannels.Opacity,
            TransitionDynamics.Tween(320f, Easing.SmoothOut), Enter: new EnterExit(Opacity: 0f, Active: true));

        /// <summary>The level tap as a PULL (never a subscription: the tap is written off the UI thread). Null — Connect,
        /// a remote device, <c>--fake</c>, no local audio — is a real answer: the VU's needles rest.</summary>
        static readonly Func<(float rms, float peak)?> LevelTap = static () =>
        {
            if (!Playback.Audio.Supported.Peek() || Playback.OwnerSignal.Peek() != Playback.Owner.Us) return null;
            var f = Playback.Audio.Levels.Peek();
            return (f.Rms, f.Peak);
        };

        /// <summary>The analyser decks read the rail's OWN fold of the engine bands (<see cref="SpectrumFold.Pull"/>,
        /// called from the deck clock's tick) — the real FFT, independent of whether the stage is open; nothing synthesises
        /// bands any more.</summary>
        static readonly SpectrumPull SpectrumTap = static () => SpectrumFold.Levels;

        /// <summary>Which deck — frozen; the stage's key remounts on another style.</summary>
        public required Rail.PlayerCatalog.Style Style;
        /// <summary>The square edge in DIP: the stage's re-pushed <see cref="HostSide"/>, read on every render (the gesture reads it live).</summary>
        public float Side = DefaultSide;

        public readonly Slab Sig = new();
        public readonly Gesture Grip;
        /// <summary>Raised on the needle-drop edge; the record face's puff subscribes.</summary>
        public Action? ThumpRequested;
        /// <summary>The live model, built once from the transport as it stands.</summary>
        public IModel Model = null!;

        bool _built;

        public Host() => Grip = new Gesture(Sig);

        public float SideDip => Side > 0f ? Side : DefaultSide;
        /// <summary>The hi-fi's VU reads the post-gain level tap (an <c>AcquireLevels</c> lease).</summary>
        public bool UsesLevels => Style.Id is Rail.PlayerCatalog.Hifi;
        /// <summary>The skin and the visualizer read the engine's 48-band FFT through <see cref="SpectrumFold"/> (an <c>AcquireSpectrum</c>
        /// lease, which implies the level tap).</summary>
        public bool UsesSpectrum => Style.Id is Rail.PlayerCatalog.Skin or Rail.PlayerCatalog.Visualizer;

        /// <summary>Re-read per tick: the record family's speed follows the playing release (a single or an EP at 45, an album at 33⅓), and
        /// the release lands after the track does.</summary>
        public float Rpm() => Models.IsRecordFamily(Style.Id) ? ClockRules.RpmFor(PlayingSingle()) : ClockRules.Rpm33;

        /// <summary>Is the playing track's release a single or an EP? An unknown release reads as an album (33⅓). Peeked: no subscription.</summary>
        static bool PlayingSingle()
        {
            var t = Shell.NowPlayingFace.TrackPeek();
            if (!t.IsValid) return false;
            var album = t.Album;
            return album.IsValid && album.Knows(AlbumFields.Release) && album.Kind is AlbumKind.Single or AlbumKind.EP;
        }

        public override Element Render()
        {
            _ = Rail.PlayerPrefs.Epoch.Value;
            Side = UseProps<HostSide>().Side;
            float side = SideDip;
            if (!_built)
            {
                var facts = ReadFacts(Sig, Rpm(), null);
                Model = Models.Create(Style.Id, LevelTap, Seed(in facts, FrameTime.NowMs), SpectrumTap);
                _built = true;
            }

            var host = this;
            return new BoxEl
            {
                Width = side, Height = side, Shrink = 0f,
                ClipToBounds = true, IsolateLayout = true,
                Corners = CornerRadius4.All(Radii.Card),
                Animate = Entrance,
                Children =
                [
                    // THE LOOK SLOT. A face is absolute geometry built from its finish, so a finish that changes a part (the picture disc's
                    // printed cover, Pocket's body) would SNAP if the face were rebuilt in place. The face sits under a key naming its look
                    // instead: a finish pick (one write, one key change) cross-fades the old face out and the new one in over the same square.
                    // The model, the clock and the gesture live on this host and never remount.
                    new BoxEl
                    {
                        Width = side, Height = side, Shrink = 0f, ZStack = true,
                        Children =
                        [
                            new BoxEl
                            {
                                Key = LookKey(Style), Width = side, Height = side, Shrink = 0f, ZStack = true,
                                Enter = LookFade, Exit = LookFade, Transition = MotionTok.ControlFast,
                                Children = [BuildFace(Style.Id, side, Sig, host)],
                            },
                        ],
                    },
                    Embed.Comp(() => new Clock { Host = host }) with { Key = "deck-clock" },
                ],
            };
        }

        static readonly EnterExit LookFade = new(Opacity: 0f, Active: true);

        /// <summary>The face's look as a key: the style and its finish (the only choice that changes a picture). Built on an epoch render, never
        /// per frame.</summary>
        static string LookKey(in Rail.PlayerCatalog.Style style)
            => style.HasFinish ? "look:" + style.Slug + "." + style.Finishes[Rail.PlayerPrefs.Finish(style.Id)].Slug : "look:" + style.Slug;
    }

    /// <summary>Style id → the deck's PIXELS (the mirror of <see cref="Models.Create"/>). An id with no face draws a blank square, never a crash.
    /// Phase 1 of the 2026-11-08 redesign maps each style onto the deck face it replaces; phase 3 redraws them.</summary>
    static Element BuildFace(int styleId, float side, Slab sig, Host host) => styleId switch
    {
        Rail.PlayerCatalog.Vinyl => RecordFace.Build(side, sig, host, RecordVariant.Turntable, Rail.PlayerPrefs.Finish(Rail.PlayerCatalog.Vinyl)),
        Rail.PlayerCatalog.Metro => RecordFace.Build(side, sig, host, RecordVariant.Zune, Rail.PlayerCatalog.VinylBlack),
        Rail.PlayerCatalog.Cassette => CassetteFace.Build(side, sig),
        Rail.PlayerCatalog.Reel => ReelFace.Build(side, sig),
        Rail.PlayerCatalog.Cd => CdFace.Build(side, sig),
        Rail.PlayerCatalog.Pocket => IpodFace.Build(side, sig, host, Rail.PlayerPrefs.Finish(Rail.PlayerCatalog.Pocket)),
        Rail.PlayerCatalog.Skin => WinampFace.Build(side, sig),
        Rail.PlayerCatalog.Hifi => VuFace.Build(side, sig),
        Rail.PlayerCatalog.Visualizer => WmpFace.Build(side, sig),
        _ => new BoxEl { Width = side, Height = side },
    };

    // ══ 4. THE CLOCK ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The transport as the deck folds it — Peek-only (a timer and a render may both call it).
    /// <c>Loading</c> counts as buffering with a play intent: the record hovers over the lead-in rather than cueing
    /// into silence.</summary>
    static TransportFacts ReadFacts(Slab sig, float rpm, long? syntheticSeekMs)
    {
        var phase = Playback.PhaseSignal.Peek();
        bool loading = phase == Playback.Phase.Loading;
        return new TransportFacts(
            HasTrack: !Playback.CurrentId.Peek().IsEmpty,
            IsPlaying: Playback.IsPlaying.Peek() || loading,
            Buffering: Playback.Buffering.Peek() || loading,
            Error: Playback.Error.Peek() != Playback.Fault.None,
            DurationMs: Playback.DurationMs.Peek(),
            ReportedPositionMs: DeckPositionMs(phase, Playback.PositionMs.Peek(), Playback.DurationMs.Peek()),
            SeekTargetMs: sig.SeekTargetMs.Peek(),
            ScrubTargetMs: sig.ScrubTargetMs.Peek(),
            SyntheticSeekMs: syntheticSeekMs,
            RepeatOne: Playback.Repeat.Peek() == Spotify.Decode.RepeatMode.Track,
            Rpm: rpm,
            ReducedMotion: Design.Reduced);
    }

    /// <summary>The position the MEDIUM is at. When the queue runs out the transport parks its playhead at the row's start (so a play press
    /// replays it, <c>Playback.EndOfQueue</c>), but the record played to its end: the deck reads the end, which is what lets the fold see
    /// the queue end (<see cref="PhaseOf"/>) and the arm ride the run-out, sit in the locked groove and return — before, the parked 0 read as
    /// a pause and then as a remote seek, and the arm hovered back over the lead-in instead.</summary>
    static long DeckPositionMs(Playback.Phase phase, long reportedMs, long durationMs)
        => phase == Playback.Phase.Ended && durationMs > 0 ? durationMs : reportedMs;

    /// <summary>The ONE clock in the hero slot, rendering a 0×0 box (plus its per-frame ticker while it runs). It is its
    /// own component because the ticker's mount belongs to the component that owns it, and hanging it off the host would
    /// make every enable/disable edge rebuild the face.</summary>
    sealed class Clock : Component
    {
        public required Host Host;

        readonly Signal<bool> _settled = new(true);
        readonly Action _tick, _tickCore, _write, _transport, _loops, _readGain;
        readonly Func<Action?> _lease;
        IReadSignal<bool>? _active;

        PositionInterpolator _pos;
        bool _anchored, _wasPlaying, _wasAdvancing;
        long _lastTickMs, _prevPos, _prevDur;
        int _lastReportMs = int.MinValue, _row, _album;
        TransportPhase _prevPhase;
        Boundary _pendingBoundary;
        long? _syntheticSeek;
        Frame _pending;
        float _quantum = 0.5f;
        /// <summary>The user's band gain (<c>Visualizer.Bands.Gain(Prefs.Stage.Sensitivity(), calm: false)</c>), cached ONCE per
        /// prefs epoch by <see cref="ReadGain"/> — the tick never reads the prefs registry (O8, V-U18).</summary>
        float _gain = 1f;

        public Clock()
        {
            _tickCore = TickCore;
            _tick = () => Reactive.Untrack(_tickCore);   // a fold must subscribe nothing, whoever calls it
            _write = WriteCore;
            _transport = OnTransport;
            _loops = PublishLoops;
            _lease = LeaseLevels;
            _readGain = ReadGain;
        }

        public override Element Render()
        {
            // Only the run gate's inputs are read at render scope, so this re-renders on play/pause/buffer/settle/rail
            // edges and never on a position report.
            bool playing = Playback.IsPlaying.Value;
            bool loading = Playback.PhaseSignal.Value == Playback.Phase.Loading;
            bool buffering = Playback.Buffering.Value || loading;
            bool settled = _settled.Value;
            bool railOpen = Shell.Ui.RailOpen.Value;
            bool run = ClockRules.ShouldTick(Design.Reduced, railOpen, playing, playing || loading, buffering, settled);
            _active = UseIsActive();
            UseEffect(_transport);
            UseEffect(_loops);
            UseEffect(_lease);
            UseSignalEffect(_readGain);                    // re-runs on a Prefs.Stage epoch bump — never on a tick
            // PER FRAME while it runs: the ticker is mounted only then (and pauses itself while parked), so a paused,
            // settled, ended, reduced-motion or rail-closed deck requests no frame.
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Children = run ? [Embed.Comp(() => new DeckFrames(_tick))] : [] };
        }

        /// <summary>The clock's per-frame tick (<see cref="Controls.FrameTicker"/>), named for the <c>[wake]</c> census.</summary>
        sealed class DeckFrames(Action tick) : Controls.FrameTicker(tick);

        /// <summary>The ONE prefs read of the analyser path: sensitivity folded into the gain, cached per epoch (O8). Only an
        /// analyser deck reads it, so the others subscribe to nothing.</summary>
        void ReadGain()
        {
            if (Host.UsesSpectrum) _gain = Visualizer.Bands.Gain(Prefs.Stage.Sensitivity(), false);
        }

        /// <summary>The transport effect (auto-tracked, no re-render): the timestamped position sample, the boundary
        /// edge, the remote-jump synthesis, and — while the timer is off — the state-change clock.</summary>
        void OnTransport()
        {
            var cur = Playback.Current.Value;
            var transport = Playback.PhaseSignal.Value;
            int pos = (int)DeckPositionMs(transport, Playback.PositionMs.Value, Playback.DurationMs.Value);
            bool playing = Playback.IsPlaying.Value;
            bool loading = transport == Playback.Phase.Loading;
            bool buffering = Playback.Buffering.Value || loading;
            bool repeatOne = Playback.Repeat.Value == Spotify.Decode.RepeatMode.Track;
            _ = Playback.Error.Value;
            var sig = Host.Sig;
            bool seekPending = sig.SeekTargetMs.Value is not null;
            _ = sig.ScrubTargetMs.Value;                   // a drag starting on a still deck must wake it
            bool railOpen = Shell.Ui.RailOpen.Value;
            bool settled = _settled.Value;
            bool active = _active?.Value ?? true;
            long now = FrameTime.NowMs;

            RowsOf(cur, out int row, out int album);
            bool fold = false, rowMoved = row != _row;
            var edge = BoundaryRules.Classify(_row, _album, _prevPos, _prevDur, _prevPhase, row, album, pos, repeatOne);
            if (edge != Boundary.None) { _pendingBoundary = edge; fold = true; }   // a skip must not wait for a tick
            _row = row;
            _album = album;

            if (pos != _lastReportMs || playing != _wasPlaying || rowMoved || !_anchored)
            {
                // What the interpolation expected at this instant; a report far from it that nobody here asked for IS a
                // seek (a Connect device, a media key) — synthesize the edge so the arm lifts instead of teleporting.
                long expected = _pos.Estimate(now, _wasAdvancing, null, null, _prevDur);
                if (_anchored && !rowMoved && pos != _lastReportMs
                    && ClockRules.IsRemoteJump(pos, expected, _prevDur, seekPending || _syntheticSeek is not null))
                {
                    _syntheticSeek = pos;
                    fold = true;
                }
                _pos.Anchor(now, pos);
                _anchored = true;
                _lastReportMs = pos;
                _wasPlaying = playing;
                _wasAdvancing = playing && !buffering;
            }

            // The first run folds at once, so a deck mounted mid-song never paints the slab's resting pose for a tick.
            bool run = ClockRules.ShouldTick(Design.Reduced, railOpen, playing, playing || loading, buffering, settled);
            if (fold || !run || !active || _lastTickMs == 0L) _tick();
        }

        void TickCore()
        {
            var h = Host;
            var sig = h.Sig;
            long now = FrameTime.NowMs;
            if (!_anchored) { _pos.Anchor(now, DeckPositionMs(Playback.PhaseSignal.Peek(), Playback.PositionMs.Peek(), Playback.DurationMs.Peek())); _anchored = true; }
            float dt = ClockRules.DeltaSec(_lastTickMs, now);
            _lastTickMs = now;

            var facts = ReadFacts(sig, h.Rpm(), _syntheticSeek);
            var input = Fold(in facts, ref _pos, now, _pendingBoundary);
            _pendingBoundary = Boundary.None;                       // an EDGE: exactly one fold sees it
            if (_syntheticSeek is { } synth && ClockRules.SeekLanded(facts.ReportedPositionMs, synth)) _syntheticSeek = null;
            if (sig.SeekTargetMs.Peek() is { } committed
                && ClockRules.ReleaseSeekLatch(facts.ReportedPositionMs, committed, now, sig.SeekCommittedAtMs))
            {
                sig.SeekTargetMs.Value = null;
                sig.SeekCommittedAtMs = 0;
            }
            _prevPos = input.PositionMs;
            _prevDur = input.DurationMs;
            _prevPhase = input.Phase;

            // The analyser decks fold the engine's published bands into the rail's shared levels BEFORE the model reads them.
            if (h.UsesSpectrum) SpectrumFold.Pull(_gain);
            _pending = h.Model.Tick(in input, dt);
            _quantum = ClockRules.AngleQuantumDeg(h.SideDip);
            // ONE batch → ONE frame request, however many bands moved.
            if (Context.Runtime is { } rt) rt.Batch(_write); else WriteCore();
            if (_pending.Thump) h.ThumpRequested?.Invoke();
        }

        /// <summary>Every write is value-gated at a PERCEPTUAL quantum (a rim pixel, 1/1024 of the bar, 1/64 of a band):
        /// a tick that moves nothing writes nothing, and the frame is elided.</summary>
        void WriteCore()
        {
            var h = Host;
            var o = h.Sig;
            var f = _pending;
            float aq = _quantum;
            Set(o.Frac, ClockRules.Quantize(f.Frac, 1f / 1024f));
            Set(o.Angle0, ClockRules.Quantize(f.Angle0, aq));
            Set(o.Angle1, ClockRules.Quantize(f.Angle1, aq * 0.5f));
            Set(o.Lift, ClockRules.Quantize(f.Lift, 0.01f));
            Set(o.Slide, ClockRules.Quantize(f.Slide, 0.005f));
            Set(o.Aux0, ClockRules.Quantize(f.Aux0, 0.005f));
            Set(o.Aux1, ClockRules.Quantize(f.Aux1, 0.005f));
            var bands = h.Model.Bands;
            for (int i = 0; i < bands.Length && i < o.Bands.Length; i++) Set(o.Bands[i], ClockRules.Quantize(bands[i], 1f / 64f));
            var peaks = h.Model.Peaks;
            for (int i = 0; i < peaks.Length && i < o.Peaks.Length; i++) Set(o.Peaks[i], ClockRules.Quantize(peaks[i], 1f / 64f));
            if (o.CoverGen.Peek() != f.CoverGen) o.CoverGen.Value = f.CoverGen;
            if (o.Phase.Peek() != f.Phase) o.Phase.Value = f.Phase;
            bool settled = h.Model.IsSettled;
            if (_settled.Peek() != settled) _settled.Value = settled;
        }

        static void Set(FloatSignal s, float v)
        {
            if (v != s.Peek()) s.Value = v;
        }

        void PublishLoops()
        {
            bool active = _active?.Value ?? true;
            Host.Sig.LoopsRun.Value = ClockRules.LoopsMayRun(Design.Reduced, Shell.Ui.RailOpen.Value, active, Playback.IsPlaying.Value);
        }

        /// <summary>The audio taps do no work until somebody holds a lease — only a visible, moving analyser deck does: the VU
        /// holds the level tier, Winamp and WMP the spectrum tier (which implies the level tap).</summary>
        Action? LeaseLevels()
        {
            bool needs = (Host.UsesLevels || Host.UsesSpectrum) && (_active?.Value ?? true) && !Design.Reduced && Shell.Ui.RailOpen.Value
                         && (Playback.IsPlaying.Value || Playback.Buffering.Value || !_settled.Value)
                         && Playback.Audio.Supported.Value && Playback.OwnerSignal.Value == Playback.Owner.Us;
            if (!needs) return null;
            var lease = Host.UsesSpectrum ? Playback.Audio.AcquireSpectrum() : Playback.Audio.AcquireLevels();
            return lease.Dispose;
        }
    }

    // ══ 5. THE GESTURE ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The seek bar's scrub, lifted so a deck's grip can drive it. The contract is the slab's two signals:
    /// a drag writes <see cref="Slab.ScrubTargetMs"/>, a release writes <see cref="Slab.SeekTargetMs"/> and posts ONE
    /// <see cref="Playback.SeekTo"/>. No audible preview: the medium follows the finger, the audio moves on release.
    /// One per host, so two grips on one face cannot fight.</summary>
    public sealed class Gesture
    {
        readonly Slab _sig;
        EntityRef _item;
        int _device = -1;
        bool _active;

        public Gesture(Slab sig) => _sig = sig;

        public bool Active => _active;

        public void Begin(long ms)
        {
            if (!Enabled()) return;
            _item = Playback.Current.Peek();
            _device = Playback.ActiveDeviceSlot.Peek();
            _active = true;
            _sig.ScrubTargetMs.Value = GripRules.Clamp(ms, Playback.DurationMs.Peek());
        }

        public void Move(long ms)
        {
            if (!IsCurrent()) return;
            _sig.ScrubTargetMs.Value = GripRules.Clamp(ms, Playback.DurationMs.Peek());
        }

        public void Commit(long ms)
        {
            if (!IsCurrent()) { Release(); return; }
            long target = GripRules.Clamp(ms, Playback.DurationMs.Peek());
            _sig.SeekTargetMs.Value = target;      // the deck keeps the medium on the target until the report lands
            _sig.SeekCommittedAtMs = FrameTime.NowMs;
            Playback.SeekTo((int)target);
            Release();
        }

        /// <summary>Aborted (a lost capture, the item changing under the finger): nothing audible happened, so the scrub
        /// position simply goes away.</summary>
        public void Cancel() => Release();

        void Release()
        {
            _active = false;
            _item = default;
            _device = -1;
            _sig.ScrubTargetMs.Value = null;
        }

        static bool Enabled() => GripRules.Enabled(!Playback.CurrentId.Peek().IsEmpty,
            Playback.Error.Peek() != Playback.Fault.None, Playback.CanSeek.Peek(), Playback.DurationMs.Peek());

        bool IsCurrent() => GripRules.StillCurrent(_active, Enabled(), Playback.Current.Peek() == _item,
            Playback.ActiveDeviceSlot.Peek() == _device);
    }

    // ══ 6. ART, ROWS AND THE LATCH ═══════════════════════════════════════════════════════════════════════════════════

    const float Deg2Rad = MathF.PI / 180f;

    // Stable absolute paths (a stable string is a stable image-cache key). UI thread only.
    static readonly Dictionary<string, string> s_assetPaths = new(StringComparer.Ordinal);

    static ColorF Hex(uint rgb, float a = 1f) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);
    static ColorF White(float a) => new(1f, 1f, 1f, a);
    static ColorF Black(float a) => new(0f, 0f, 0f, a);
    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>A bundled ALPHA texture (a groove ring, a reel flange, the Winamp title bar) painted in an INK: the texture's alpha is the
    /// shape, <paramref name="ink"/>'s colour fills it (the image overlay composites atop the texture's own pixels, so its transparent parts
    /// stay transparent) and <paramref name="ink"/>'s alpha is the strength. The placeholder is TRANSPARENT: a missing or undecoded texture
    /// shows the fallback under it, never a grey plate.</summary>
    static Element Texture(string assetFile, float w, float h, CornerRadius4 corners, ColorF ink)
        => Strength(new ImageEl
        {
            Source = AssetPath(assetFile), Width = w, Height = h, Fit = ImageFit.Cover, Corners = corners,
            ColorOverlay = new ColorF(ink.R, ink.G, ink.B, 1f), Placeholder = ColorF.Transparent,
        }, w, h, ink.A);

    /// <summary>A bundled texture in its OWN colours (the CD rainbow) at <paramref name="strength"/>.</summary>
    static Element OwnTexture(string assetFile, float w, float h, CornerRadius4 corners, float strength = 1f)
        => Strength(new ImageEl
        {
            Source = AssetPath(assetFile), Width = w, Height = h, Fit = ImageFit.Cover, Corners = corners, Placeholder = ColorF.Transparent,
        }, w, h, strength);

    /// <summary>The texture at a strength: a plain box opacity (multiplied into the draw, no layer: these sit on nodes that turn per frame).</summary>
    static Element Strength(ImageEl image, float w, float h, float strength)
        => strength >= 1f ? image : new BoxEl { Width = w, Height = h, Shrink = 0f, Opacity = strength, HitTestVisible = false, Children = [image] };

    static string AssetPath(string assetFile)
    {
        if (s_assetPaths.TryGetValue(assetFile, out string? p)) return p;
        p = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "deck", assetFile);
        s_assetPaths[assetFile] = p;
        return p;
    }

    static Element Circle(float d, Prop<ColorF> fill) => new BoxEl { Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), Fill = fill };

    /// <summary>A hollow SDF ring — it composites over what is under it.</summary>
    static Element Ring(float d, float w, ColorF c) => new BoxEl
    {
        Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), BorderWidth = w, BorderColor = c,
    };

    /// <summary>Absolute placement WITHOUT a clip — a disc's shadow must escape its own box.</summary>
    static Element Offset(float x, float y, Element child) => new BoxEl { OffsetX = x, OffsetY = y, Children = [child] };

    /// <summary>A cover at a layout size that also drives the decode (64 / 128 / 256 / 512).</summary>
    static Element Cover(string? url, float size, CornerRadius4 corners, Prop<ColorF> placeholder) => new ImageEl
    {
        Source = url ?? "", Width = size, Height = size, Fit = ImageFit.Cover, DecodePx = DecodeFor(size),
        Corners = corners, Placeholder = placeholder,
    };

    static float DecodeFor(float size)
    {
        float px = size * 1.5f;
        return px <= 64f ? 64f : px <= 128f ? 128f : px <= 256f ? 256f : 512f;
    }

    /// <summary>The hero's cover WASH: the card ground lerped toward the cover's lifted accent (the theme accent before a
    /// grading lands or with no cover) — every cover slot's placeholder, so an undecoded slot is a tint, never a hole. With "Now playing colours"
    /// off (CT9 U3) the theme accent stands in for the cover's.</summary>
    static ColorF Wash(string? url)
    {
        ColorF accent = url is { Length: > 0 } && StageInk.NowPlayingColors && Design.SchemeFor(url) is { } s
            ? ColorScience.Lift(ColorScience.Accent(s))
            : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, R.Wash.CoverMix);
    }

    /// <summary>The wash as a bound channel: the grading lands after the leaf is built and repaints one fill.</summary>
    static Prop<ColorF> WatchedWash(string? url) => Prop.Of(() =>
    {
        if (url is { Length: > 0 }) _ = Wavee.Palette.Watch(url).Value;
        return Wash(url);
    });

    /// <summary>The position a clock or a bar SHOWS: a live drag first, then a committed seek, then the report.</summary>
    static long PlayheadMs(Slab sig) => sig.ScrubTargetMs.Value ?? sig.SeekTargetMs.Value ?? Playback.PositionMs.Value;

    /// <summary>The row identity and its album (a show for an episode) as <see cref="ClockRules.RowKey"/>s.</summary>
    static void RowsOf(EntityRef r, out int row, out int album)
    {
        row = r.IsNone ? 0 : ClockRules.RowKey((byte)r.Kind, r.Slot);
        album = 0;
        if (r.Kind == EntityKind.Track && new Track(r.Slot) is { IsValid: true } t && t.AlbumSlot > 0)
            album = ClockRules.RowKey((byte)EntityKind.Album, t.AlbumSlot);
        else if (r.Kind == EntityKind.Episode && new Episode(r.Slot) is { IsValid: true } e && e.ShowSlot > 0)
            album = ClockRules.RowKey((byte)EntityKind.Show, e.ShowSlot);
    }

    static bool KnowsText(EntityRef r) => r.Kind switch
    {
        EntityKind.Track => new Track(r.Slot) is { IsValid: true } t && t.Knows(TrackFields.Title | TrackFields.Artists),
        EntityKind.Episode => new Episode(r.Slot) is { IsValid: true } e && e.Knows(EpisodeFields.Title | EpisodeFields.Show),
        _ => false,
    };

    static bool KnowsImage(EntityRef r) => r.Kind switch
    {
        EntityKind.Track => new Track(r.Slot) is { IsValid: true } t && t.Knows(TrackFields.Image),
        EntityKind.Episode => new Episode(r.Slot) is { IsValid: true } e && e.Knows(EpisodeFields.Image),
        _ => false,
    };

    /// <summary>While a row lacks fields a reader paints, subscribe its table so the landing re-runs the reader.</summary>
    static void WatchRow(EntityRef r)
    {
        if (!r.IsNone && Entities.TableFor(r.Kind) is { } table) _ = table.Changed.Value;
    }

    static string TitleOf(EntityRef r) => r.Kind switch
    {
        EntityKind.Track when new Track(r.Slot).IsValid => new Track(r.Slot).Title,
        EntityKind.Episode when new Episode(r.Slot).IsValid => new Episode(r.Slot).Title,
        _ => "",
    };

    /// <summary>The joined artist line (a show's title for an episode).</summary>
    static string ArtistsOf(EntityRef r) => r.Kind switch
    {
        EntityKind.Track when new Track(r.Slot).IsValid => Entities.Strings.Resolve(new Track(r.Slot).ArtistLineId),
        EntityKind.Episode when new Episode(r.Slot) is { IsValid: true } e && e.ShowSlot > 0 => e.Show.Title,
        _ => "",
    };

    /// <summary>The FIRST artist only — the VU strip and the Winamp title never show the joined list.</summary>
    static string FirstArtistOf(EntityRef r)
    {
        if (r.Kind != EntityKind.Track || !new Track(r.Slot).IsValid) return ArtistsOf(r);
        var slots = new Track(r.Slot).ArtistSlots;
        return slots.Length > 0 ? new Artist(slots[0]).Name : "";
    }

    static string AlbumNameOf(EntityRef r) => r.Kind switch
    {
        EntityKind.Track when new Track(r.Slot) is { IsValid: true } t && t.AlbumSlot > 0 => t.Album.Title,
        EntityKind.Episode when new Episode(r.Slot) is { IsValid: true } e && e.ShowSlot > 0 => e.Show.Title,
        _ => "",
    };

    static string? CoverUrlOf(EntityRef r) => r.Kind switch
    {
        EntityKind.Track when new Track(r.Slot) is { IsValid: true } t && t.Knows(TrackFields.Image) => Controls.ArtUrl(t.ImageId),
        EntityKind.Episode when new Episode(r.Slot) is { IsValid: true } e && e.Knows(EpisodeFields.Image) => Controls.ArtUrl(e.ImageId),
        _ => null,
    };

    /// <summary>What a cover or lettering leaf SHOWS (<see cref="Lettering.Adopt"/>): it re-renders on the mechanism's
    /// generation and on the playing item, adopts per the rule, and — while its row lacks the fields it paints, or a newer
    /// row is waiting for them — subscribes the table so the landing re-letters it. A mutable struct held as a field of
    /// its component and only ever updated in place.</summary>
    struct Latch
    {
        int _row, _album, _gen;

        /// <summary>The row the leaf shows.</summary>
        public EntityRef Shown;
        /// <summary>The generation the leaf last latched on — an inner <c>Key</c> on it plays the 300 ms cross-fade.</summary>
        public readonly int Gen => _gen;

        /// <param name="followTrack">The iPod's screen changes with the SONG, not with the album: every known item adopts.</param>
        public void Update(Slab sig, bool image, bool followTrack = false)
        {
            int gen = sig.CoverGen.Value;
            var cur = Playback.Current.Value;
            RowsOf(cur, out int row, out int album);
            bool known = image ? KnowsImage(cur) : KnowsText(cur);
            if (Lettering.Adopt(row, album, known, _row, _album, followTrack || gen != _gen))
            {
                Shown = cur;
                _row = row;
                _album = album;
                _gen = gen;
            }
            if (row != _row) WatchRow(cur);
            if (!(image ? KnowsImage(Shown) : KnowsText(Shown))) WatchRow(Shown);
        }
    }

    static readonly FormatCache<int> s_elapsed = FormatCache.Create<int>();
    static readonly FormatCache<int> s_remaining = FormatCache.Create<int>();
    static readonly FormatCache<int> s_elapsedPadded = FormatCache.Create<int>();
    static readonly FormatCache<int> s_remainingPadded = FormatCache.Create<int>();

    /// <summary>"m:ss" / "-m:ss" (iPod, Zune) or "mm:ss" / "-mm:ss" (VU, Winamp), one cached string per whole second.</summary>
    static string ElapsedText(Slab sig, bool padded)
    {
        int s = (int)Math.Max(0L, PlayheadMs(sig) / 1000L);
        return padded ? s_elapsedPadded.Get(s, static v => Mmss(v, true)) : s_elapsed.Get(s, static v => Mmss(v, false));
    }

    static string RemainingText(Slab sig, bool padded)
    {
        int s = (int)(Math.Max(0L, Playback.DurationMs.Value - PlayheadMs(sig)) / 1000L);
        return padded ? s_remainingPadded.Get(s, static v => "-" + Mmss(v, true)) : s_remaining.Get(s, static v => "-" + Mmss(v, false));
    }

    static string Mmss(int total, bool padded)
        => (padded ? (total / 60).ToString("D2", System.Globalization.CultureInfo.InvariantCulture)
                   : (total / 60).ToString(System.Globalization.CultureInfo.InvariantCulture))
           + ":" + (total % 60).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>300 ms opacity entrance on a cover leaf's generation-keyed box: the remount IS the cross-fade (KEPT under
    /// reduced motion).</summary>
    static readonly LayoutTransition CoverFade = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(300f, Easing.SmoothOut), Enter: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The cover, wherever a face shows one: a stable component (keyed by the caller) whose INNER box is keyed
    /// on the latched generation, so the art changes when the mechanism says so and cross-fades as it does.</summary>
    sealed record CoverArtProps(Slab Sig, float Size, bool Round);

    sealed class CoverArt : Component
    {
        Slab Sig = null!;
        float Size;
        bool Round;
        Latch _latch;

        public override Element Render()
        {
            (Sig, Size, Round) = UseProps<CoverArtProps>();
            _latch.Update(Sig, image: true);
            string? url = CoverUrlOf(_latch.Shown);
            float size = Size;
            var corners = Round ? Radii.Circle(size) : default;
            return new BoxEl
            {
                Key = FormatCache.Int(_latch.Gen), Width = size, Height = size, Shrink = 0f, ZStack = true,
                Corners = corners, Animate = CoverFade,
                Children = [Cover(url, size, corners, WatchedWash(url))],
            };
        }
    }

    /// <summary>The size is RE-PUSHED (<see cref="CoverArtProps"/>): a 12" → 7" flip or a rail quantum redraws the label at its new size and keeps
    /// its node (the art's latch and its cross-fade generation with it).</summary>
    static Element CoverLeaf(Slab sig, float size, bool round, string key)
        => Embed.Comp(new CoverArtProps(sig, size, round), static () => new CoverArt()) with { Key = key };

    // ══ 7. THE RECORD FAMILY ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Two skins over one tonearm machine. Vinyl's turntable: a graphite plinth, a platter with a machined rim under a slipmat,
    /// the record in one of four finishes (black, smoke, ruby, or the picture disc printing the cover) and a straight arm with an offset
    /// headshell whose stylus rides the groove. Metro's type-and-bar poster draws no arm. Absolute layout in fractions of <c>side</c>; the
    /// turntable's record and arm come from <see cref="TonearmGeometry"/>, the table the machine reads too, so the stylus the face draws
    /// sits on the groove the machine says it plays. The palettes are literal (a deck is a physical object, the same in light and dark).</summary>
    static class RecordFace
    {
        const uint ArmInk = 0xF2F4F7, ArmDarkInk = 0x5D6470, ArmMidInk = 0xC3C8D1, HeadInk = 0x2B2E34, HeadEdgeInk = 0x7A808A;
        /// <summary>The board's graphite plinth (lit from the top left), the platter's machined rim, the felt slipmat, the bearing housing.</summary>
        const uint PlinthTop = 0x2E2E33, PlinthBottom = 0x18181A, RimLight = 0xC4C7CD, RimDark = 0x6E7076, RimEnd = 0x9A9DA3,
            Slipmat = 0x1C1C1F, HousingTop = 0x2A2B30, HousingBottom = 0x1A1B1E, PostInk = 0x2C2D32, LampInk = 0xFF5A36;
        const uint VinylBlack = 0x15171C, RubyBase = 0x7A1A2A, SpindleInk = 0xE6E9EF;
        const uint ZuneGrey = 0x9AA1AD, MetroAccent = 0xF0568C;
        /// <summary>The grooves' ink strength per finish.</summary>
        const float GrooveBlack = 0.16f, GrooveSmoke = 0.30f, GrooveRuby = 0.20f;

        /// <summary>The plinth's inset inside the deck's square (the board's 14 and 22 of 328) and its corner.</summary>
        const float PlinthX = 0.043f, PlinthY = 0.067f, PlinthCorner = 0.043f;
        /// <summary>The platter's diameter (its machined rim shows just outside the record) and the slipmat's.</summary>
        const float PlatterFrac = 0.72f, MatFrac = 0.695f;
        /// <summary>The arm, in fractions of the side: its box's width, the pivot's depth inside the box (the counterweight sits above
        /// it), the tube's width, the headshell's width and length, and the headshell's offset (clockwise from the arm's effective line,
        /// so the cartridge sits near tangent to the groove mid-side).</summary>
        const float ArmBoxW = 0.12f, ArmPivotY = 0.085f, TubeW = 0.017f, HeadW = 0.046f, HeadLen = 0.078f, HeadOffsetDeg = 20f;
        /// <summary>The track gaps pressed into the black, smoke and ruby records: smooth bands where the groove widens between songs.</summary>
        const float GapA = 0.80f, GapB = 0.62f;

        /// <param name="finish">Vinyl's finish index (<see cref="Rail.PlayerCatalog.VinylBlack"/> …); Metro passes black.</param>
        public static Element Build(float side, Slab sig, Host host, RecordVariant variant, int finish)
        {
            float s = side > 0f ? side : DefaultSide;
            var kids = new List<CanvasChild>(12);
            if (variant == RecordVariant.Zune)
            {
                kids.Add(new CanvasChild(0.08f * s, 0.22f * s, Platter(0.62f * s, finish, variant, sig, host)));
                var accent = Hex(MetroAccent);
                float typeW = 0.34f * s;
                kids.Add(new CanvasChild(0.62f * s, 0.08f * s,
                    // The side is re-pushed (a column quantum redraws in place).
                    Embed.Comp(new ZuneTypeProps(sig, s, typeW, accent), static () => new ZuneType()) with { Key = "zune-type" }));
                float barW = 0.86f * s;
                var frac = sig.Frac;
                kids.Add(new CanvasChild(0.08f * s, 0.88f * s, new BoxEl
                {
                    Width = barW, Height = 3f, Shrink = 0f, ZStack = true, Fill = Hex(0x333333),
                    Children =
                    [
                        // SCALED, never resized: a width write at tick rate would be a layout write.
                        new BoxEl
                        {
                            Width = barW, Height = 3f, Shrink = 0f, Fill = accent,
                            TransformOriginX = 0f, TransformOriginY = 0.5f,
                            Transform = Prop.Of(() => Affine2D.Scale(frac.Value, 1f)),
                        },
                    ],
                }));
                return Canvas.Create(s, s, kids) with { Fill = Hex(0x000000) };
            }

            float rd = TonearmGeometry.RecordDFrac * s, cx = TonearmGeometry.CenterX * s, cy = TonearmGeometry.CenterY * s;
            Plinth(kids, s, cx, cy, sig);
            kids.Add(new CanvasChild(cx - rd * 0.5f, cy - rd * 0.5f, Platter(rd, finish, variant, sig, host)));
            float armW = ArmBoxW * s, pivotY = ArmPivotY * s;
            float armH = pivotY + TonearmGeometry.ArmLengthFrac * s + 0.03f * s;
            float armX = TonearmGeometry.PivotX * s - armW * 0.5f, armY = TonearmGeometry.PivotY * s - pivotY;
            kids.Add(new CanvasChild(armX, armY, Tonearm(s, rd, cx, cy, armW, armH, armX, armY, sig, host)));
            // No ground of its own: the plinth stands on the theme's panel.
            return Canvas.Create(s, s, kids);
        }

        /// <summary>Everything on the turntable that does not move: the plinth, the platter's rim and slipmat, the arm's bearing housing
        /// and rest, and the start/stop button, whose lamp is lit while the motor drives the platter.</summary>
        static void Plinth(List<CanvasChild> kids, float s, float cx, float cy, Slab sig)
        {
            float pw = s - 2f * PlinthX * s, ph = s - 2f * PlinthY * s;
            kids.Add(new CanvasChild(PlinthX * s, PlinthY * s, new BoxEl
            {
                Width = pw, Height = ph, Shrink = 0f, Corners = CornerRadius4.All(PlinthCorner * s),
                Gradient = Ui.LinearGradient(70f, new GradientStop(0f, Hex(PlinthTop)), new GradientStop(1f, Hex(PlinthBottom))),
                BorderWidth = 1f, BorderColor = White(0.07f),
                Shadow = new ShadowSpec(0.05f * s, 0.022f * s, 0f, Black(0.45f)),
            }));

            float pd = PlatterFrac * s, md = MatFrac * s;
            kids.Add(new CanvasChild(cx - pd * 0.5f, cy - pd * 0.5f, new BoxEl
            {
                Width = pd, Height = pd, Shrink = 0f, Corners = Radii.Circle(pd),
                Gradient = Ui.LinearGradient(60f, new GradientStop(0f, Hex(RimLight)), new GradientStop(0.55f, Hex(RimDark)), new GradientStop(1f, Hex(RimEnd))),
                Shadow = new ShadowSpec(0.03f * s, 0.01f * s, 0f, Black(0.5f)),
            }));
            kids.Add(new CanvasChild(cx - md * 0.5f, cy - md * 0.5f, Circle(md, Hex(Slipmat))));

            float px = TonearmGeometry.PivotX * s, py = TonearmGeometry.PivotY * s, baseD = 0.15f * s;
            kids.Add(new CanvasChild(px - baseD * 0.5f, py - baseD * 0.5f, new BoxEl
            {
                Width = baseD, Height = baseD, Shrink = 0f, Corners = Radii.Circle(baseD),
                Gradient = Ui.RadialGradient(new GradientStop(0f, Hex(HousingTop)), new GradientStop(1f, Hex(HousingBottom))),
                BorderWidth = 1f, BorderColor = White(0.10f),
                Shadow = new ShadowSpec(0.02f * s, 0.006f * s, 0f, Black(0.4f)),
            }));

            // The rest: a post under the tube, part-way down the arm at the rest angle.
            float rest = TonearmMachine.RestDeg * Deg2Rad, along = 0.60f * TonearmGeometry.ArmLengthFrac * s;
            float rx = px - along * MathF.Sin(rest), ry = py + along * MathF.Cos(rest), postW = 0.042f * s, postH = 0.03f * s;
            kids.Add(new CanvasChild(rx - postW * 0.5f, ry - postH * 0.5f, new BoxEl
            {
                Width = postW, Height = postH, Shrink = 0f, Corners = CornerRadius4.All(0.008f * s),
                Fill = Hex(PostInk), BorderWidth = 1f, BorderColor = White(0.12f),
                Shadow = new ShadowSpec(0.012f * s, 0.004f * s, 0f, Black(0.4f)),
            }));

            float btnW = 0.122f * s, btnH = 0.037f * s, lamp = 0.013f * s;
            var phase = sig.Phase;
            kids.Add(new CanvasChild(0.104f * s, 0.86f * s, new BoxEl
            {
                Width = btnW, Height = btnH, Shrink = 0f, ZStack = true, Corners = CornerRadius4.All(0.009f * s), Fill = Hex(0x0E0E10),
                BorderWidth = 1f, BorderColor = White(0.06f),
                Children =
                [
                    new BoxEl
                    {
                        Width = lamp, Height = lamp, Shrink = 0f, Corners = Radii.Circle(lamp), Fill = Hex(LampInk),
                        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 0f, 0.4f * btnH, 0f),
                        Shadow = new ShadowSpec(0.02f * s, 0f, 0f, Hex(LampInk, 0.9f)),
                        Opacity = Prop.Of(() => MotorOn(phase.Value) ? 1f : 0.22f),
                    },
                ],
            }));
        }

        /// <summary>Is the motor driving the platter in this phase (the start/stop lamp)?</summary>
        static bool MotorOn(PhaseName p) => p is PhaseName.Cueing or PhaseName.NeedleDown or PhaseName.Playing or PhaseName.SpinningUp
            or PhaseName.Seeking or PhaseName.NextTrack or PhaseName.RunOut or PhaseName.LockedGroove or PhaseName.Buffering;

        /// <summary>The record leaving its sleeve: one wrapper translating and scaling the whole disc, plus the puff.</summary>
        static Element Platter(float d, int finish, RecordVariant variant, Slab sig, Host host)
        {
            var slide = sig.Slide;
            float dd = d, thump = 0.08f * d;
            return new BoxEl
            {
                Width = d, Height = d, Shrink = 0f, ZStack = true,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                // A third of a diameter back into the sleeve, growing the last 4 % as it clears it.
                Transform = Prop.Of(() =>
                {
                    float e = Ease.SlideOut(slide.Value);
                    float k = 0.96f + 0.04f * e;
                    return Affine2D.Translation(-0.34f * dd * (1f - e), 0f).Multiply(Affine2D.Scale(k, k));
                }),
                Opacity = Prop.Of(() => Clamp01(slide.Value * 1.6f)),
                Children =
                [
                    variant == RecordVariant.Zune ? Disc(d, finish, variant, sig) : Record(d, finish, sig),
                    Offset((d - thump) * 0.5f, (d - thump) * 0.5f,
                        Embed.Comp(new RecordThumpProps(host, thump), static () => new RecordThump()) with { Key = "thump" }),
                ],
            };
        }

        /// <summary>Vinyl's record. ONE rotating node carries everything pressed or printed on it (the grooves, the smooth rim, the track
        /// gaps, the dead wax with its etched mark, the label, the spindle hole), so the platter costs a single transform write per tick.
        /// Over it, NOT turning, the groove sheen: a record's reflection stays with the room's light while the vinyl turns under it, and it
        /// is the label and the etched mark that show the turn.</summary>
        static Element Record(float d, int finish, Slab sig)
        {
            bool picture = finish == Rail.PlayerCatalog.VinylPicture;
            var angle = sig.Angle0;
            ColorF vinyl = finish switch
            {
                // Translucent, not Acrylic: this node rotates every frame and a backdrop layer would re-blur every frame.
                Rail.PlayerCatalog.VinylSmoke => new ColorF(40f / 255f, 44f / 255f, 54f / 255f, 0.55f),
                Rail.PlayerCatalog.VinylRuby => Hex(RubyBase),
                _ => Hex(VinylBlack),
            };
            var layers = new List<Element>(12);
            if (picture)
            {
                layers.Add(CoverLeaf(sig, d, round: true, key: "picture-art"));
                layers.Add(new BoxEl { Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), Fill = Black(0.10f), BorderWidth = 1f, BorderColor = White(0.06f) });
            }
            else
            {
                layers.Add(new BoxEl
                {
                    Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), Fill = vinyl, BorderWidth = 1f, BorderColor = White(0.08f),
                    Shadow = new ShadowSpec(0.04f * d, 0.012f * d, 0f, Black(0.5f)),
                });
                // The grooves in an ink over the finish (the ring's alpha is the shape): a faint light on the dark vinyl.
                layers.Add(Texture("grooves-1024.png", d, d, Radii.Circle(d), finish switch
                {
                    Rail.PlayerCatalog.VinylSmoke => White(GrooveSmoke),
                    Rail.PlayerCatalog.VinylRuby => White(GrooveRuby),
                    _ => White(GrooveBlack),
                }));
                // Smooth vinyl where there is no groove: the rim outside the lead-in, two gaps between songs, the dead wax inside the
                // run-out groove (its edge catches the light).
                layers.Add(Ring(d, 0.5f * (1f - TonearmGeometry.LeadInRadiusFrac) * d, vinyl));
                layers.Add(Centered(Ring(GapA * d, 0.007f * d, vinyl)));
                layers.Add(Centered(Ring(GapB * d, 0.007f * d, vinyl)));
                float wax = TonearmGeometry.RunOutRadiusFrac * d;
                layers.Add(Centered(Circle(wax, vinyl)));
                layers.Add(Centered(Ring(wax, 1f, White(0.16f))));
                // The etched matrix mark in the dead wax: the one mark on the vinyl itself that shows it turning.
                float mark = 0.05f * d;
                layers.Add(Offset(0.5f * d + 0.185f * d - mark * 0.5f, 0.5f * d - 0.5f, new BoxEl { Width = mark, Height = 1f, Shrink = 0f, Fill = White(0.22f), Rotation = 90f, TransformOriginX = 0.5f, TransformOriginY = 0.5f }));
                // A faint off-centre glint that turns with the record: the grooves are never perfectly even.
                layers.Add(new BoxEl
                {
                    Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d),
                    Gradient = Ui.RadialGradient(new Point2(0.30f, 0.25f), new Point2(0.9f, 0.9f),
                        new GradientStop(0f, White(0.05f)), new GradientStop(0.55f, ColorF.Transparent), new GradientStop(1f, White(0.03f))),
                });
                // The paper label, printed with the cover; its edge is the paper's.
                float label = TonearmGeometry.LabelRadiusFrac * d;
                layers.Add(Centered(CoverLeaf(sig, label, round: true, key: "label-art")));
                layers.Add(Centered(Ring(label, 1f, Black(0.45f))));
            }

            // The spindle hole (the spindle itself stands in it).
            float hole = 0.035f * d;
            layers.Add(Centered(new BoxEl
            {
                Width = hole, Height = hole, Shrink = 0f, Corners = Radii.Circle(hole),
                Gradient = Ui.RadialGradient(new Point2(0.35f, 0.3f), new Point2(0.9f, 0.9f), new GradientStop(0f, White(1f)), new GradientStop(1f, Hex(0x8E9197))),
                BorderWidth = 1f, BorderColor = Black(0.5f),
            }));

            return new BoxEl
            {
                Width = d, Height = d, Shrink = 0f, ZStack = true,
                Children =
                [
                    new BoxEl
                    {
                        Width = d, Height = d, Shrink = 0f, ZStack = true, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                        Transform = Prop.Of(() => Affine2D.Rotation(angle.Value * Deg2Rad)),
                        Children = layers.ToArray(),
                    },
                    // The sheen: a soft band of light across the grooves, fixed to the room.
                    new BoxEl
                    {
                        Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), HitTestVisible = false,
                        Gradient = Ui.LinearGradient(40f, new GradientStop(0.26f, White(0f)), new GradientStop(0.47f, White(picture ? 0.08f : 0.11f)),
                            new GradientStop(0.53f, White(picture ? 0.08f : 0.11f)), new GradientStop(0.74f, White(0f))),
                    },
                ],
            };
        }

        static Element Centered(Element child) => new BoxEl { AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Children = [child] };

        /// <summary>Metro's record (the poster's disc): the ONE rotating node, everything printed on it its child.</summary>
        static Element Disc(float d, int finish, RecordVariant variant, Slab sig)
        {
            bool zune = variant == RecordVariant.Zune;
            bool picture = !zune && finish == Rail.PlayerCatalog.VinylPicture;
            var angle = sig.Angle0;
            var layers = new List<Element>(8);

            if (picture)
            {
                layers.Add(CoverLeaf(sig, d, round: true, key: "picture-art"));
                layers.Add(new BoxEl
                {
                    Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), Fill = Black(0.10f),
                    BorderWidth = 1f, BorderColor = White(0.06f), Shadow = new ShadowSpec(34f, 14f, 0f, Black(0.45f)),
                });
            }
            else
            {
                layers.Add(new BoxEl
                {
                    Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), BorderWidth = 1f, BorderColor = White(0.06f),
                    Shadow = new ShadowSpec(34f, 14f, 0f, Black(0.45f)),
                    Fill = finish switch
                    {
                        // Translucent, not Acrylic: this node rotates 30×/s and a backdrop layer would re-blur every frame.
                        Rail.PlayerCatalog.VinylSmoke => new ColorF(40f / 255f, 44f / 255f, 54f / 255f, 0.55f),
                        Rail.PlayerCatalog.VinylRuby => Hex(RubyBase),
                        _ => Hex(VinylBlack),
                    },
                });
                // The grooves in an ink over the finish (the ring's alpha is the shape): a faint light on the dark vinyl.
                layers.Add(Texture("grooves-1024.png", d, d, Radii.Circle(d), finish switch
                {
                    Rail.PlayerCatalog.VinylSmoke => White(GrooveSmoke),
                    Rail.PlayerCatalog.VinylRuby => White(GrooveRuby),
                    _ => White(GrooveBlack),
                }));
            }

            // The sheen turns WITH the record — what sells it as a solid object rather than a spinning picture.
            layers.Add(new BoxEl
            {
                Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d),
                Gradient = Ui.RadialGradient(new Point2(0.30f, 0.25f), new Point2(0.9f, 0.9f),
                    new GradientStop(0f, White(0.10f)), new GradientStop(0.55f, ColorF.Transparent), new GradientStop(1f, White(0.06f))),
            });

            if (!picture)
            {
                float label = TonearmGeometry.LabelRadiusFrac * d, lx = (d - label) * 0.5f;
                layers.Add(Offset(lx, lx, CoverLeaf(sig, label, round: true, key: "label-art")));
                layers.Add(Offset(lx, lx, Ring(label, zune ? 3f : 2f, zune ? White(0.9f) : Black(0.6f))));
            }

            // The spindle.
            float hole = 0.05f * d, hx = (d - hole) * 0.5f;
            layers.Add(Offset(hx, hx, new BoxEl
            {
                Width = hole, Height = hole, Shrink = 0f, Corners = Radii.Circle(hole),
                Fill = picture ? White(1f) : Hex(SpindleInk), BorderWidth = 1f, BorderColor = Black(0.5f),
            }));

            return new BoxEl
            {
                Width = d, Height = d, Shrink = 0f, ZStack = true, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Transform = Prop.Of(() => Affine2D.Rotation(angle.Value * Deg2Rad)),
                Children = layers.ToArray(),
            };
        }

        /// <summary>The headshell's grip (the inspector drags it by this key).</summary>
        public const string HeadshellKey = "deck-headshell";

        /// <summary>Where the headshell was last dragged to, so the release commits it. Allocated once, at build.</summary>
        sealed class ArmGrip { public long LastMs; }

        /// <summary>Vinyl's tonearm (Metro draws none): a straight tube from the bearing to an offset headshell, the counterweight behind
        /// the bearing. The box turns about the pivot by the machine's arm angle, which puts the stylus — the headshell's tip, on the
        /// arm's effective line at <see cref="TonearmGeometry.ArmLengthFrac"/> — on the groove. The cue lever lifts the arm a hair up
        /// and nearer (its shadow on the record stays behind and softens).</summary>
        static Element Tonearm(float s, float rd, float cx, float cy, float armW, float armH, float armX, float armY, Slab sig, Host host)
        {
            var angle1 = sig.Angle1;
            var lift = sig.Lift;
            var arm = Hex(ArmInk);
            var armDark = Hex(ArmDarkInk);
            float pX = armW * 0.5f, pY = ArmPivotY * s, stylusY = pY + TonearmGeometry.ArmLengthFrac * s;
            float headLen = HeadLen * s, headW = HeadW * s, tubeW = TubeW * s;

            // The headshell turns HeadOffsetDeg clockwise about the stylus, so its back (where the tube meets it) sits right of the
            // effective line; the tube runs from the pivot to that joint.
            float off = HeadOffsetDeg * Deg2Rad;
            float jx = pX + headLen * MathF.Sin(off), jy = stylusY - headLen * MathF.Cos(off);
            float tubeLen = MathF.Sqrt((jx - pX) * (jx - pX) + (jy - pY) * (jy - pY));
            float tubeDeg = -MathF.Atan2(jx - pX, jy - pY) / Deg2Rad;
            var tubeGradient = Ui.LinearGradient(0f, new GradientStop(0f, armDark), new GradientStop(0.4f, arm),
                new GradientStop(0.6f, Hex(ArmMidInk)), new GradientStop(1f, armDark));

            Element Tube(Prop<ColorF> fill, GradientSpec? gradient) => Offset(pX - tubeW * 0.5f, pY, new BoxEl
            {
                Width = tubeW, Height = tubeLen, Shrink = 0f, Corners = CornerRadius4.All(tubeW * 0.5f), Fill = fill, Gradient = gradient,
                TransformOriginX = 0.5f, TransformOriginY = 0f, Rotation = tubeDeg,
            });
            Element Shell(Prop<ColorF> fill, bool parts) => Offset(pX - headW * 0.5f, stylusY - headLen, new BoxEl
            {
                Width = headW, Height = headLen, Shrink = 0f, ZStack = true, Corners = CornerRadius4.All(0.006f * s), Fill = fill,
                BorderWidth = parts ? 1f : 0f, BorderColor = Hex(HeadEdgeInk),
                TransformOriginX = 0.5f, TransformOriginY = 1f, Rotation = HeadOffsetDeg,
                Children = parts
                    ?
                    [
                        // The cartridge under the shell's nose, and its stylus guard: a thread of red at the very tip.
                        new BoxEl
                        {
                            Width = 0.62f * headW, Height = 0.42f * headLen, Shrink = 0f, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Center,
                            Margin = new Edges4(0f, 0f, 0f, 0.05f * headLen), Corners = CornerRadius4.All(0.004f * s), Fill = Hex(0x141518),
                        },
                        new BoxEl
                        {
                            Width = 0.40f * headW, Height = MathF.Max(1f, 0.05f * headLen), Shrink = 0f, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Center,
                            Fill = Hex(0xD8342C),
                        },
                        // The finger lift.
                        new BoxEl
                        {
                            Width = 0.42f * headW, Height = 0.06f * headLen, Shrink = 0f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End,
                            Margin = new Edges4(0f, 0.22f * headLen, -0.36f * headW, 0f), Fill = Hex(ArmDarkInk), Corners = CornerRadius4.All(0.004f * s),
                        },
                    ]
                    : [],
            });

            // The drag: a point on the headshell → deck space through the arm's own rotation → a groove fraction → ms.
            float hitW = 0.14f * s, hitH = 0.16f * s, hitX = jx - hitW * 0.5f, hitY = stylusY - headLen - 0.03f * s;
            var grip = new ArmGrip();
            var gesture = host.Grip;
            long MsAt(Point2 p)
            {
                var (dx, dy) = TonearmGeometry.ArmLocalToDeck(p.X + hitX, p.Y + hitY, angle1.Peek(), armX, armY, pX, pY);
                return GripRules.MsAt(TonearmGeometry.FracFromDeckPoint(dx, dy, cx, cy, rd), Playback.DurationMs.Peek());
            }

            float cwW = 0.05f * s, cwH = 0.052f * s, stub = 0.033f * s, pivotD = 0.075f * s, capD = 0.03f * s;
            float originY = pY / armH;
            var lifted = new BoxEl
            {
                Width = armW, Height = armH, Shrink = 0f, ZStack = true, TransformOriginX = 0.5f, TransformOriginY = originY,
                // The cue lever: a hair up and a hair larger. The buffering bob overshoots 1, clamped at 1.4.
                Transform = Prop.Of(() =>
                {
                    float l = MathF.Min(lift.Value, 1.4f);
                    float k = 1f + 0.03f * l;
                    return Affine2D.Translation(0f, -0.016f * s * l).Multiply(Affine2D.Scale(k, k));
                }),
                Children =
                [
                    // The counterweight on its stub behind the bearing.
                    Offset(pX - tubeW * 0.6f, pY - stub, new BoxEl { Width = tubeW * 1.2f, Height = stub, Shrink = 0f, Gradient = tubeGradient }),
                    Offset(pX - cwW * 0.5f, pY - stub - cwH + 0.004f * s, new BoxEl
                    {
                        Width = cwW, Height = cwH, Shrink = 0f, Corners = CornerRadius4.All(0.008f * s),
                        Gradient = Ui.LinearGradient(0f, new GradientStop(0f, Hex(0x4A4D54)), new GradientStop(0.35f, Hex(0xB9BCC3)),
                            new GradientStop(0.6f, Hex(0x8A8D94)), new GradientStop(1f, Hex(0x3A3C42))),
                        BorderWidth = 1f, BorderColor = Black(0.35f),
                    }),
                    Tube(ColorF.Transparent, tubeGradient),
                    Shell(Hex(HeadInk), parts: true),
                    // The bearing: a polished dome with a dark cap.
                    Offset(pX - pivotD * 0.5f, pY - pivotD * 0.5f, new BoxEl
                    {
                        Width = pivotD, Height = pivotD, Shrink = 0f, Corners = Radii.Circle(pivotD),
                        Gradient = Ui.RadialGradient(new Point2(0.35f, 0.3f), new Point2(0.9f, 0.9f),
                            new GradientStop(0f, White(1f)), new GradientStop(0.3f, Hex(0xD6D8DC)), new GradientStop(1f, Hex(0x8E9197))),
                        Shadow = new ShadowSpec(0.02f * s, 0.008f * s, 0f, Black(0.45f)),
                    }),
                    Offset(pX - capD * 0.5f, pY - capD * 0.5f, Circle(capD, Hex(0x2A2B30))),
                    // The grip: transparent and larger than the headshell. OnDragCanceled is wired (ch 23 §6.4(1)): a lost capture
                    // without a release must not leave the arm parked in Dragging.
                    Offset(hitX, hitY, new BoxEl
                    {
                        Key = HeadshellKey, Width = hitW, Height = hitH, Shrink = 0f, Fill = ColorF.Transparent, Cursor = CursorId.Hand,
                        OnPointerDown = p => { long ms = MsAt(p); grip.LastMs = ms; gesture.Begin(ms); },
                        OnDrag = p => { long ms = MsAt(p); grip.LastMs = ms; gesture.Move(ms); },
                        OnPointerReleased = _ => gesture.Commit(grip.LastMs),
                        OnDragCanceled = gesture.Cancel,
                    }),
                ],
            };

            // The arm's shadow on the record: it turns with the arm but does not lift; lifting moves it away and softens it.
            var shadow = new BoxEl
            {
                Width = armW, Height = armH, Shrink = 0f, ZStack = true, HitTestVisible = false,
                Transform = Prop.Of(() =>
                {
                    float l = Clamp01(lift.Value);
                    return Affine2D.Translation((0.005f + 0.02f * l) * s, (0.007f + 0.026f * l) * s);
                }),
                Opacity = Prop.Of(() => 0.6f - 0.25f * Clamp01(lift.Value)),
                Children = [Tube(Black(0.55f), null), Shell(Black(0.55f), parts: false)],
            };

            return new BoxEl
            {
                Width = armW, Height = armH, Shrink = 0f, ZStack = true, TransformOriginX = 0.5f, TransformOriginY = originY,
                Transform = Prop.Of(() => Affine2D.Rotation(angle1.Value * Deg2Rad)),
                Children = [shadow, lifted],
            };
        }

        /// <summary>The needle drop: a dust ring that expands and fades ONCE per thump, driven straight from the
        /// animation scheduler — three one-shot tracks, zero re-renders.</summary>
        sealed record RecordThumpProps(Host Host, float Size);

        sealed class RecordThump : Component
        {
            Host Host = null!;
            float Size;

            static readonly Keyframe[] FadeOut = [new(0f, 0.9f, Easing.Linear), new(1f, 0f, Easing.SmoothOut)];
            static readonly Keyframe[] Expand = [new(0f, 0.2f, Easing.SmoothOut), new(1f, 1.6f, Easing.SmoothOut)];
            const float PuffMs = 500f;

            NodeHandle _node;

            public override Element Render()
            {
                (Host, Size) = UseProps<RecordThumpProps>();
                var host = Host;
                float size = Size;
                // A constant dep: a re-render must not re-subscribe (ch 23 §6.4(3)).
                UseLayoutEffect(() =>
                {
                    var anim = Context.Anim;
                    var scene = Context.Scene;
                    if (anim is null || scene is null) return null;
                    void OnThump()
                    {
                        var node = _node;
                        if (node.IsNull || !scene.IsLive(node)) return;
                        anim.Keyframes(node, AnimChannel.Opacity, FadeOut, PuffMs);
                        anim.Keyframes(node, AnimChannel.ScaleX, Expand, PuffMs);
                        anim.Keyframes(node, AnimChannel.ScaleY, Expand, PuffMs);
                    }
                    host.ThumpRequested += OnThump;
                    return () => host.ThumpRequested -= OnThump;
                }, DepKey.From(0));

                return new BoxEl
                {
                    Width = size, Height = size, Shrink = 0f, Corners = Radii.Circle(size),
                    BorderWidth = MathF.Max(1f, size * 0.08f), BorderColor = White(0.7f),
                    Opacity = 0f, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    OnRealized = h => _node = h,
                };
            }
        }

        /// <summary>The Zune's poster: a lowercase title whose last third carries the accent, one grey line of artist and
        /// clock. The lettering follows the latch (the artwork's instant, and never a blank for an unknown row); the clock
        /// is a bound text in a FIXED slot, so its relayout cannot move the line beside it.</summary>
        sealed record ZuneTypeProps(Slab Sig, float Side, float BlockW, ColorF Accent);

        sealed class ZuneType : Component
        {
            Slab Sig = null!;
            float Side;
            float BlockW;
            ColorF Accent;
            Latch _latch;

            const string Display = "Segoe UI Variable Display";
            const float LineFloor = 11f;

            public override Element Render()
            {
                (Sig, Side, BlockW, Accent) = UseProps<ZuneTypeProps>();
                _latch.Update(Sig, image: false);
                float s = Side;
                string title = TitleOf(_latch.Shown).ToLowerInvariant();
                string artist = ArtistsOf(_latch.Shown);
                // The grey line never drops under the 11-DIP type floor (a Zune hero's deck is 296 wide: 0.036 of it read 10.7).
                float titleSize = 0.11f * s, lineSize = MathF.Max(LineFloor, 0.036f * s);
                // Two runs rather than one span: this boundary only moves when the title does. < 3 chars: no accent run.
                int cut = title.Length >= 3 ? title.Length - title.Length / 3 : title.Length;
                var sig = Sig;
                var grey = Hex(ZuneGrey);

                return new BoxEl
                {
                    Width = BlockW, Direction = 1, Gap = 0.02f * s, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0,
                            Children =
                            [
                                new TextEl(title[..cut]) { Size = titleSize, Weight = 300, FontFamily = Display, Color = White(1f), MaxLines = 1, Trim = TextTrim.Clip },
                                new TextEl(title[cut..]) { Size = titleSize, Weight = 300, FontFamily = Display, Color = Accent, MaxLines = 1, Trim = TextTrim.Clip },
                            ],
                        },
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center,
                            Children =
                            [
                                // Shrink (G-256): the artist gives way, never the clock's fixed slot — a long name used to
                                // push the elapsed time past the block's clip.
                                new TextEl(artist.Length > 0 ? artist + " · " : "") { Size = lineSize, Color = grey, MaxLines = 1, Trim = TextTrim.Clip, Shrink = 1f },
                                new BoxEl
                                {
                                    Width = 0.10f * s, Shrink = 0f,
                                    Children = [new TextEl(Prop.Of(() => ElapsedText(sig, padded: false))) { Size = lineSize, Color = grey, MaxLines = 1 }],
                                },
                            ],
                        },
                    ],
                };
            }
        }
    }
}
