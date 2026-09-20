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
//   View()   the mount point — reads the rail width and the chosen preset and keys ONE host on "deck:<slug>@<side>".
//   Host     the layout firewall (Width = Height = side, ClipToBounds, IsolateLayout, 8-DIP corners, 320 ms entrance)
//            holding exactly two children: the face (built once per render of the host, all binds) and the Clock.
//   Clock    a 0×0 component that owns the only timer. It samples `Playback.PositionMs` against the FRAME clock
//            (`Design.FrameTime`), folds the transport into a `Deck.Input`, ticks the model and diff-writes one frame
//            into the `Slab` inside ONE batch, value-gated at a perceptual quantum. `ClockRules.ShouldTick` decides
//            whether the timer runs at all: a paused, settled, ended, reduced-motion or rail-closed deck runs none and
//            requests no frame; a transport CHANGE is then the clock.
//   Slab     the fixed set of signals every face binds at build and the clock writes.
//   Gesture  the one scrub gesture per host (headshell, click wheel) — `GripRules` decides, `Playback.SeekTo` commits.
//   the record family (Record / Turntable / Zune / Picture): one face, four skins, over one tonearm machine.
//
// Stage-2 fixes owed by ch 23 §6.4, delivered here: (#1) the headshell wires OnDragCanceled → Gesture.Cancel; (#4) a
// MODEL option (VU needles, Winamp analyser) rebuilds the mounted model in place (`Models.ModelOptionSlug`); (#6)
// both grips refuse over an unknown duration (`GripRules.Enabled`). §6.4(5) (the scope that never settled) is the
// model's own `IsSettled`, in `Deck.cs`.
//
// Rules: faces are FUNCTIONS, built once per host render; everything that moves is a bound Transform/Opacity/Text over
// the slab; no allocation on the tick path; the live track is read INSIDE thunks and leaf components, never in a face
// body (that would rebuild the face on every track change).

using System.Collections.Generic;
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
    public const float DefaultSide = Shell.RailDefaultW - 2f * Spacing.S;

    /// <summary>The now-playing player deck for the rail's hero slot: a <c>side × side</c> square that fills the rail's
    /// content width. It reads its own inputs — <see cref="Shell.Ui.RailWidth"/> (quantised to the 4-DIP grid) and the
    /// chosen preset (<see cref="Rail.PlayerPrefs"/>) — and keys one host on <c>"deck:&lt;slug&gt;@&lt;side&gt;"</c>, so a
    /// preset change or a rail resize past a quantum is a remount and nothing else is. The caller owns the hero slot's
    /// "nothing is playing" arm (ch 21: the slot mounts no deck without a current item).</summary>
    // MOUNT POINT (stage B contract)
    public static Element View() => Embed.Comp(static () => new ViewHost());

    sealed class ViewHost : Component
    {
        public override Element Render()
        {
            // A memo, so a splitter drag re-renders this only when the quantised side actually changes.
            var side = UseComputed(static () => ClockRules.SideFor(Shell.Ui.RailWidth.Value, Spacing.S));
            float s = side.Value;
            var preset = Rail.PlayerPrefs.CurrentPreset();   // subscribes the prefs epoch
            return Embed.Comp(() => new Host { Preset = preset, Side = s })
                with { Key = "deck:" + preset.Slug + "@" + FormatCache.Int((int)s) };
        }
    }

    // ══ 2. THE SLAB ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ONE channel between the physics and the pixels: a fixed slab the faces bind at build and the clock
    /// diff-writes once per tick. The per-deck meaning of each float is documented on <see cref="Frame"/>. Resting values
    /// are the record family's "nothing has happened yet" pose.</summary>
    public sealed class Slab
    {
        /// <summary>24 covers the widest deck (WMP); Winamp binds the first 19.</summary>
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
        /// <summary>May a face's LOOPING motion run (<see cref="ClockRules.LoopsMayRun"/>)? The Winamp marquee and the
        /// Canvas drift gate on it, so a paused deck owns no looping animation row.</summary>
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

    /// <summary>The square that IS a deck. Re-renders on the prefs epoch (an option flip restyles the face IN PLACE) and
    /// never on the transport. The model is created from the CURRENT transport (<see cref="Seed"/>), so a deck mounted
    /// mid-song shows a record that was already playing.</summary>
    sealed class Host : Component
    {
        /// <summary>320 ms opacity entrance — KEPT under reduced motion (a cross-fade between two hero contents).</summary>
        static readonly LayoutTransition Entrance = new(TransitionChannels.Opacity,
            TransitionDynamics.Tween(320f, Easing.SmoothOut), Enter: new EnterExit(Opacity: 0f, Active: true));

        /// <summary>The level tap as a PULL (never a subscription: the tap is written off the UI thread). Null — Connect,
        /// a remote device, <c>--fake</c>, no local audio — is a real answer the synths handle.</summary>
        static readonly Func<(float rms, float peak)?> LevelTap = static () =>
        {
            if (!Playback.Audio.Supported.Peek() || Playback.OwnerSignal.Peek() != Playback.Owner.Us) return null;
            var f = Playback.Audio.Levels.Peek();
            return (f.Rms, f.Peak);
        };

        /// <summary>Which deck — frozen; the view's key remounts on a preset change.</summary>
        public required Rail.PlayerCatalog.Preset Preset;
        /// <summary>The square edge in DIP — frozen; the view's key remounts on a quantum change.</summary>
        public float Side = DefaultSide;

        public readonly Slab Sig = new();
        public readonly Gesture Grip;
        /// <summary>Raised on the needle-drop edge; the record face's puff subscribes.</summary>
        public Action? ThumpRequested;
        /// <summary>The live model. Rebuilt IN PLACE when the preset's model option flips (ch 23 §6.4(4)).</summary>
        public IModel Model = null!;

        string? _modelOption;
        bool _built;

        public Host() => Grip = new Gesture(Sig);

        public float SideDip => Side > 0f ? Side : DefaultSide;
        public bool UsesLevels => Preset.Id is Rail.PlayerCatalog.Vu or Rail.PlayerCatalog.Winamp or Rail.PlayerCatalog.Wmp;

        /// <summary>Re-read per tick: a 33 → 45 flip must reach a model that was built once.</summary>
        public float Rpm() => Models.IsRecordFamily(Preset.Id)
            ? ClockRules.RpmFor(Rail.PlayerPrefs.ChoiceSlug(in Preset, "rpm"))
            : ClockRules.Rpm33;

        public override Element Render()
        {
            _ = Rail.PlayerPrefs.Epoch.Value;
            float side = SideDip;
            string? optionSlug = Models.ModelOptionSlug(Preset.Id);
            string? option = optionSlug is null ? null : Rail.PlayerPrefs.ChoiceSlug(in Preset, optionSlug);
            if (!_built || !string.Equals(option, _modelOption, StringComparison.Ordinal))
            {
                var facts = ReadFacts(Sig, Rpm(), null);
                Model = Models.Create(in Preset, LevelTap, Seed(in facts, Design.FrameTime.NowMs));
                _modelOption = option;
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
                    BuildFace(in Preset, side, Sig, host),
                    Embed.Comp(() => new Clock { Host = host }) with { Key = "deck-clock" },
                ],
            };
        }
    }

    /// <summary>Preset id → the deck's PIXELS (the mirror of <see cref="Models.Create"/>). An id with no face draws a
    /// blank square, never a crash.</summary>
    static Element BuildFace(in Rail.PlayerCatalog.Preset preset, float side, Slab sig, Host host) => preset.Id switch
    {
        Rail.PlayerCatalog.Record => RecordFace.Build(in preset, side, sig, host, RecordVariant.Record),
        Rail.PlayerCatalog.Turntable => RecordFace.Build(in preset, side, sig, host, RecordVariant.Turntable),
        Rail.PlayerCatalog.Zune => RecordFace.Build(in preset, side, sig, host, RecordVariant.Zune),
        Rail.PlayerCatalog.Picture => RecordFace.Build(in preset, side, sig, host, RecordVariant.Picture),
        Rail.PlayerCatalog.Cassette => CassetteFace.Build(in preset, side, sig),
        Rail.PlayerCatalog.Reel => ReelFace.Build(in preset, side, sig),
        Rail.PlayerCatalog.Cd => CdFace.Build(in preset, side, sig),
        Rail.PlayerCatalog.Ipod => IpodFace.Build(in preset, side, sig, host),
        Rail.PlayerCatalog.Winamp => WinampFace.Build(in preset, side, sig),
        Rail.PlayerCatalog.Vu => VuFace.Build(in preset, side, sig),
        Rail.PlayerCatalog.Wmp => WmpFace.Build(in preset, side, sig),
        Rail.PlayerCatalog.Canvas => CanvasFace.Build(in preset, side, sig),
        _ => new BoxEl { Width = side, Height = side },
    };

    // ══ 4. THE CLOCK ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The transport as the deck folds it — Peek-only (a timer and a render may both call it).
    /// <c>Loading</c> counts as buffering with a play intent: the record hovers over the lead-in rather than cueing
    /// into silence.</summary>
    static TransportFacts ReadFacts(Slab sig, float rpm, long? syntheticSeekMs)
    {
        bool loading = Playback.PhaseSignal.Peek() == Playback.Phase.Loading;
        return new TransportFacts(
            HasTrack: !Playback.CurrentId.Peek().IsEmpty,
            IsPlaying: Playback.IsPlaying.Peek() || loading,
            Buffering: Playback.Buffering.Peek() || loading,
            Error: Playback.Error.Peek() != Playback.Fault.None,
            DurationMs: Playback.DurationMs.Peek(),
            ReportedPositionMs: Playback.PositionMs.Peek(),
            SeekTargetMs: sig.SeekTargetMs.Peek(),
            ScrubTargetMs: sig.ScrubTargetMs.Peek(),
            SyntheticSeekMs: syntheticSeekMs,
            RepeatOne: Playback.Repeat.Peek() == Spotify.Decode.RepeatMode.Track,
            Rpm: rpm,
            ReducedMotion: Design.Reduced);
    }

    /// <summary>The ONE timer in the hero slot, rendering a 0×0 box. It is its own component because
    /// <c>UseInterval</c> belongs to the component that owns it, and hanging it off the host would make every
    /// enable/disable edge rebuild the face.</summary>
    sealed class Clock : Component
    {
        public required Host Host;

        readonly Signal<bool> _settled = new(true);
        readonly Action _tick, _tickCore, _write, _transport, _loops;
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

        public Clock()
        {
            _tickCore = TickCore;
            _tick = () => Reactive.Untrack(_tickCore);   // a fold must subscribe nothing, whoever calls it
            _write = WriteCore;
            _transport = OnTransport;
            _loops = PublishLoops;
            _lease = LeaseLevels;
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
            UseInterval(_tick, ClockRules.TickMs, run);
            _active = UseIsActive();
            UseEffect(_transport);
            UseEffect(_loops);
            UseEffect(_lease);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        /// <summary>The transport effect (auto-tracked, no re-render): the timestamped position sample, the boundary
        /// edge, the remote-jump synthesis, and — while the timer is off — the state-change clock.</summary>
        void OnTransport()
        {
            var cur = Playback.Current.Value;
            int pos = Playback.PositionMs.Value;
            bool playing = Playback.IsPlaying.Value;
            bool loading = Playback.PhaseSignal.Value == Playback.Phase.Loading;
            bool buffering = Playback.Buffering.Value || loading;
            bool repeatOne = Playback.Repeat.Value == Spotify.Decode.RepeatMode.Track;
            _ = Playback.Error.Value;
            _ = Playback.DurationMs.Value;
            _ = Rail.PlayerPrefs.Epoch.Value;              // a 33→45 flip / a model rebuild must re-fold a still deck
            var sig = Host.Sig;
            bool seekPending = sig.SeekTargetMs.Value is not null;
            _ = sig.ScrubTargetMs.Value;                   // a drag starting on a still deck must wake it
            bool railOpen = Shell.Ui.RailOpen.Value;
            bool settled = _settled.Value;
            bool active = _active?.Value ?? true;
            long now = Design.FrameTime.NowMs;

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
            long now = Design.FrameTime.NowMs;
            if (!_anchored) { _pos.Anchor(now, Playback.PositionMs.Peek()); _anchored = true; }
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

        /// <summary>The level tap does no work until somebody holds a lease — only a visible, moving level deck does.</summary>
        Action? LeaseLevels()
        {
            bool needs = Host.UsesLevels && (_active?.Value ?? true) && !Design.Reduced && Shell.Ui.RailOpen.Value
                         && (Playback.IsPlaying.Value || Playback.Buffering.Value || !_settled.Value)
                         && Playback.Audio.Supported.Value && Playback.OwnerSignal.Value == Playback.Owner.Us;
            if (!needs) return null;
            var lease = Playback.Audio.AcquireLevels();
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
            _sig.SeekCommittedAtMs = Design.FrameTime.NowMs;
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

    /// <summary>A bundled deck texture (grooves, wood grain, CD rainbow, marble, reel flange, Winamp title bar), tinted.
    /// The placeholder is TRANSPARENT: a missing or undecoded texture shows the fallback under it, never a grey plate.</summary>
    static Element Texture(string assetFile, float w, float h, CornerRadius4 corners, ColorF tint) => new ImageEl
    {
        Source = AssetPath(assetFile), Width = w, Height = h, Fit = ImageFit.Cover, Corners = corners,
        ColorOverlay = tint, Placeholder = ColorF.Transparent,
    };

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
    /// grading lands or with no cover) — every cover slot's placeholder, so an undecoded slot is a tint, never a hole.</summary>
    static ColorF Wash(string? url)
    {
        ColorF accent = url is { Length: > 0 } && Design.SchemeFor(url) is { } s
            ? Design.Palette.Lift(Design.Palette.Accent(s))
            : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, Tok.Theme == ThemeKind.Dark ? 0.18f : 0.10f);
    }

    /// <summary>The wash as a bound channel: the grading lands after the leaf is built and repaints one fill.</summary>
    static Prop<ColorF> WatchedWash(string? url) => Prop.Of(() =>
    {
        if (url is { Length: > 0 }) _ = Wavee.Palette.Watch(url).Value;
        return Wash(url);
    });

    /// <summary>The LIVE cover accent of whatever is playing (read inside a thunk: a track change or a late grading
    /// repaints the bound fill and never rebuilds the face).</summary>
    static ColorF LiveCoverAccent(ColorF fallback)
    {
        var cur = Playback.Current.Value;
        string? url = CoverUrlOf(cur);
        if (url is null) { WatchRow(cur); return fallback; }
        _ = Wavee.Palette.Watch(url).Value;
        return Design.SchemeFor(url) is { } s ? Design.Palette.Accent(s) : fallback;
    }

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
    sealed class CoverArt : Component
    {
        public required Slab Sig;
        public required float Size;
        public required bool Round;
        Latch _latch;

        public override Element Render()
        {
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

    /// <summary><c>Size</c> is a FROZEN factory field, so the key carries it: a 12" → 7" flip rebuilds the face in place
    /// and must remount the label at its new size rather than keep the mount-time one.</summary>
    static Element CoverLeaf(Slab sig, float size, bool round, string key)
        => Embed.Comp(() => new CoverArt { Sig = sig, Size = size, Round = round }) with { Key = key + "@" + FormatCache.Int((int)size) };

    // ══ 7. THE RECORD FAMILY ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Four skins over one geometry: the bare record, the wood-plinth turntable, the Zune's type-and-bar poster
    /// and the picture disc. Absolute layout in fractions of <c>side</c>; the palettes are literal (a deck is a physical
    /// object); exactly the deck ground behind a 7" hole and the album-colour vinyl follow the theme/cover.</summary>
    static class RecordFace
    {
        const uint ArmInk = 0xCFD4DD, ArmDarkInk = 0x9AA1AD, WoodTop = 0x6B4A2E, WoodBottom = 0x4A301B;
        const uint VinylBlack = 0x15171C, SplatterBase = 0xF5F1EA, MarbleBase = 0x6B4FA8, SpindleInk = 0xE6E9EF;
        const uint AlbumFallback = 0x1C4F9A, ZuneGrey = 0x9AA1AD;

        public static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig, Host host, RecordVariant variant)
        {
            // `preset` is an `in` parameter and cannot be captured: every option becomes a plain local first. The
            // variant flags guard every read an option row does not carry (ch 23 §6.4(7)).
            bool zune = variant == RecordVariant.Zune, turntable = variant == RecordVariant.Turntable;
            bool picture = variant == RecordVariant.Picture;
            string finish = zune || picture ? "black" : Rail.PlayerPrefs.ChoiceSlug(in preset, "finish");
            bool small = !zune && Rail.PlayerPrefs.ChoiceSlug(in preset, "size") == "7";
            bool showSleeve = !zune && Rail.PlayerPrefs.ChoiceSlug(in preset, "sleeve") != "off";
            string accentSlug = zune ? Rail.PlayerPrefs.ChoiceSlug(in preset, "accent") : "pink";

            float s = side > 0f ? side : DefaultSide;
            Prop<ColorF> deckBg = zune ? Hex(0x000000) : turntable ? Hex(WoodBottom) : Prop.Of(static () => Tok.FillCardSecondary);

            float d = zune ? 0.62f * s : small ? 0.52f * s : 0.70f * s;
            float px = zune ? 0.08f * s : small ? 0.33f * s : 0.24f * s;
            float py = zune ? 0.22f * s : small ? 0.25f * s : 0.16f * s;
            if (!zune && !showSleeve) px = 0.15f * s;   // nothing to slide out of: the record sits where the sleeve was

            var kids = new List<CanvasChild>(12);
            if (turntable) Plinth(kids, s);

            if (showSleeve)
            {
                float sleeve = 0.64f * s;
                kids.Add(new CanvasChild(0.06f * s, 0.12f * s, new BoxEl
                {
                    Width = sleeve, Height = sleeve, Shrink = 0f, ZStack = true, ClipToBounds = true,
                    Corners = CornerRadius4.All(4f), Rotation = turntable ? -4f : -2.5f,
                    Shadow = new ShadowSpec(30f, 10f, 0f, Black(0.25f)),
                    Fill = Black(0.35f),                     // a sleeve with no art is still a sleeve
                    Children = [CoverLeaf(sig, sleeve, round: false, key: "sleeve-art")],
                }));
            }

            kids.Add(new CanvasChild(px, py, Platter(d, finish, variant, sig, host, deckBg, small)));

            if (!zune)
            {
                float armW = 0.22f * s, armH = 0.92f * s, armX = 0.75f * s, armY = 0.02f * s;
                kids.Add(new CanvasChild(armX, armY, Tonearm(d, px, py, armW, armH, armX, armY, turntable, sig, host)));
                kids.Add(new CanvasChild(0.845f * s, 0.06f * s, new BoxEl
                {
                    Width = 0.03f * s, Height = 0.09f * s, Shrink = 0f, Corners = CornerRadius4.All(3f),
                    Fill = turntable ? Hex(0xC9CED8) : Hex(ArmDarkInk, 0.6f),
                }));
            }
            else
            {
                var accent = ZuneAccent(accentSlug);
                float typeW = 0.34f * s;
                kids.Add(new CanvasChild(0.62f * s, 0.08f * s,
                    // The accent is a FROZEN factory field, so an option flip must remount: the key carries it.
                    Embed.Comp(() => new ZuneType { Sig = sig, Side = s, BlockW = typeW, Accent = accent })
                        with { Key = "zune-type:" + accentSlug }));
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
            }

            return Canvas.Create(s, s, kids) with { Fill = deckBg, Corners = zune ? default(CornerRadius4) : CornerRadius4.All(Radii.Card) };
        }

        static void Plinth(List<CanvasChild> kids, float s)
        {
            // The grain PNG is alpha: tinted white, with the stain OVER it so the grain reads through.
            kids.Add(new CanvasChild(0f, 0f, Texture("wood-grain-1024.png", s, s, CornerRadius4.All(Radii.Card), White(1f))));
            kids.Add(new CanvasChild(0f, 0f, new BoxEl
            {
                Width = s, Height = s, Shrink = 0f, Corners = CornerRadius4.All(Radii.Card),
                Gradient = Ui.GradientDown(new GradientStop(0f, Hex(WoodTop, 0.88f)), new GradientStop(1f, Hex(WoodBottom, 0.96f))),
            }));
            float mat = 0.77f * s, matX = 0.205f * s, matY = 0.125f * s;
            kids.Add(new CanvasChild(matX, matY, new BoxEl
            {
                Width = mat, Height = mat, Shrink = 0f, Corners = Radii.Circle(mat),
                Gradient = Ui.RadialGradient(new GradientStop(0f, Hex(0x3A3D44)), new GradientStop(1f, Hex(0x2B2E34))),
            }));
            kids.Add(new CanvasChild(matX, matY, Ring(mat, 4f, Hex(0x1F2126))));
            kids.Add(new CanvasChild(matX + 8f, matY + 8f, Ring(mat - 16f, 2f, Hex(0x6D7078))));

            // The strobe window: a lit pitch lamp (it does not blink).
            float strobeW = 0.16f * s, strobeH = 0.05f * s, dot = strobeH * 0.5f;
            kids.Add(new CanvasChild(0.08f * s, 0.86f * s, Canvas.Create(strobeW, strobeH,
            [
                new CanvasChild(0f, 0f, new BoxEl
                {
                    Width = strobeW, Height = strobeH, Shrink = 0f, Corners = CornerRadius4.All(2f), Fill = Hex(0x1A1C20),
                }),
                new CanvasChild(strobeW - dot * 2f, (strobeH - dot) * 0.5f, new BoxEl
                {
                    Width = dot, Height = dot, Shrink = 0f, Corners = Radii.Circle(dot), Fill = Hex(0xFF5A2A),
                    Shadow = new ShadowSpec(10f, 0f, 0f, Hex(0xFF5A2A, 0.9f)),
                }),
            ])));
        }

        /// <summary>The record leaving its sleeve: one wrapper translating and scaling the whole disc, plus the puff.</summary>
        static Element Platter(float d, string finish, RecordVariant variant, Slab sig, Host host, Prop<ColorF> deckBg, bool small)
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
                    Disc(d, finish, variant, sig, deckBg, small),
                    Offset((d - thump) * 0.5f, (d - thump) * 0.5f,
                        Embed.Comp(() => new RecordThump { Host = host, Size = thump }) with { Key = "thump@" + FormatCache.Int((int)thump) }),
                ],
            };
        }

        /// <summary>The ONE rotating node: everything printed on the record is its child, so the platter costs a single
        /// transform write per tick however many layers the finish has.</summary>
        static Element Disc(float d, string finish, RecordVariant variant, Slab sig, Prop<ColorF> deckBg, bool small)
        {
            bool picture = variant == RecordVariant.Picture, zune = variant == RecordVariant.Zune;
            var angle = sig.Angle0;
            var layers = new List<Element>(14);

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
                        "album" => AlbumVinyl,
                        // Translucent, not Acrylic: this node rotates 30×/s and a backdrop layer would re-blur every frame.
                        "clear" => new ColorF(40f / 255f, 44f / 255f, 54f / 255f, 0.55f),
                        "splatter" => Hex(SplatterBase),
                        "marble" => Hex(MarbleBase),
                        _ => Hex(VinylBlack),
                    },
                });
                if (finish == "splatter")
                {
                    float dot = 0.04f * d;
                    AddDot(layers, d, dot, 0.30f, 0.25f, 0xFFD166);
                    AddDot(layers, d, dot, 0.70f, 0.40f, 0xFFD166);
                    AddDot(layers, d, dot, 0.60f, 0.78f, 0xEF476F);
                    AddDot(layers, d, dot, 0.24f, 0.66f, 0xEF476F);
                    AddDot(layers, d, dot, 0.82f, 0.70f, 0x06D6A0);
                    AddDot(layers, d, dot, 0.45f, 0.12f, 0x06D6A0);
                }
                if (finish == "marble") layers.Add(Texture("marble-1024.png", d, d, Radii.Circle(d), White(0.9f)));
                layers.Add(Texture("grooves-1024.png", d, d, Radii.Circle(d), finish switch
                {
                    "clear" => White(0.12f),
                    "album" => White(0.08f),
                    "splatter" => Black(0.08f),
                    "marble" => Black(0.18f),
                    _ => White(0.035f),
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

            // The spindle — or, on a 7", the big hole the deck itself shows through.
            float hole = small ? 0.20f * d : 0.05f * d, hx = (d - hole) * 0.5f;
            layers.Add(Offset(hx, hx, small
                ? new BoxEl { Width = hole, Height = hole, Shrink = 0f, Corners = Radii.Circle(hole), Fill = deckBg }
                : new BoxEl
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

        static void AddDot(List<Element> into, float d, float dot, float fx, float fy, uint rgb)
            => into.Add(Offset(fx * d - dot * 0.5f, fy * d - dot * 0.5f, new BoxEl
            {
                Width = dot, Height = dot, Shrink = 0f, Corners = Radii.Circle(dot),
                Gradient = Ui.RadialGradient(new GradientStop(0f, Hex(rgb)), new GradientStop(1f, Hex(rgb, 0.75f))),
            }));

        /// <summary>The cover's accent, deepened ×0.55 (dyed vinyl, not paint) — bound, resolved inside the thunk.</summary>
        static readonly Prop<ColorF> AlbumVinyl = Prop.Of(static () =>
        {
            var c = LiveCoverAccent(Hex(AlbumFallback));
            return new ColorF(c.R * 0.55f, c.G * 0.55f, c.B * 0.55f, 1f);
        });

        /// <summary>Where the headshell was last dragged to, so the release commits it. Allocated once, at build.</summary>
        sealed class ArmGrip { public long LastMs; }

        static Element Tonearm(float d, float platterX, float platterY, float armW, float armH, float armX, float armY,
                               bool turntable, Slab sig, Host host)
        {
            var angle1 = sig.Angle1;
            var lift = sig.Lift;
            var arm = turntable ? Hex(0xF2F4F7) : Hex(ArmInk);
            var armDark = turntable ? Hex(0x5D6470) : Hex(ArmDarkInk);
            float tubeW = turntable ? 0.07f * armW : 0.09f * armW, tubeH = 0.62f * armH, tubeX = 0.455f * armW, tubeY = 0.09f * armH;
            var tubeGradient = turntable
                ? Ui.LinearGradient(0f, new GradientStop(0f, Hex(0x5D6470)), new GradientStop(0.4f, Hex(0xF2F4F7)),
                    new GradientStop(0.6f, Hex(0xC3C8D1)), new GradientStop(1f, Hex(0x5D6470)))
                : Ui.LinearGradient(0f, new GradientStop(0f, armDark), new GradientStop(0.5f, arm), new GradientStop(1f, armDark));
            float pivotD = 0.44f * armW, headW = 0.22f * armW, headH = 0.12f * armH;
            float hitW = 0.44f * armW, hitH = 0.20f * armH, hitX = 0.28f * armW, hitY = 0.64f * armH;

            // The drag: a point on the headshell → deck space through the arm's own rotation → a groove fraction → ms.
            var grip = new ArmGrip();
            var gesture = host.Grip;
            float cx = platterX + d * 0.5f, cy = platterY + d * 0.5f;
            long MsAt(Point2 p)
            {
                var (dx, dy) = TonearmGeometry.ArmLocalToDeck(p.X + hitX, p.Y + hitY, angle1.Peek(), armX, armY, armW, armH);
                return GripRules.MsAt(TonearmGeometry.FracFromDeckPoint(dx, dy, cx, cy, d), Playback.DurationMs.Peek());
            }

            var lifted = new BoxEl
            {
                Width = armW, Height = armH, Shrink = 0f, ZStack = true, TransformOriginX = 0.5f, TransformOriginY = 0.08f,
                // The cue lever: a hair up and a hair larger. The buffering bob overshoots 1, clamped at 1.4.
                Transform = Prop.Of(() =>
                {
                    float l = MathF.Min(lift.Value, 1.4f);
                    float k = 1f + 0.015f * l;
                    return Affine2D.Translation(0f, -0.015f * armH * l).Multiply(Affine2D.Scale(k, k));
                }),
                Children =
                [
                    // A shadow-only twin (ShadowSpec is not bindable): the shadow's strength is this node's opacity.
                    Offset(tubeX, tubeY, new BoxEl
                    {
                        Width = tubeW, Height = tubeH, Shrink = 0f, Corners = CornerRadius4.All(4f), Fill = ColorF.Transparent,
                        Shadow = new ShadowSpec(10f, 10f, 0f, Black(0.45f)), Opacity = Prop.Of(() => 0.45f * Clamp01(lift.Value)),
                    }),
                    Offset(tubeX, tubeY, new BoxEl
                    {
                        Width = tubeW, Height = tubeH, Shrink = 0f, Corners = CornerRadius4.All(4f), Gradient = tubeGradient,
                    }),
                    Offset(armW * 0.5f - pivotD * 0.5f, armH * 0.08f - pivotD * 0.5f, new BoxEl
                    {
                        Width = pivotD, Height = pivotD, Shrink = 0f, Corners = Radii.Circle(pivotD),
                        Gradient = Ui.RadialGradient(new GradientStop(0f, arm), new GradientStop(1f, armDark)),
                        Shadow = new ShadowSpec(8f, 3f, 0f, Black(0.35f)),
                    }),
                    Offset(0.39f * armW, 0.69f * armH, new BoxEl
                    {
                        Width = headW, Height = headH, Shrink = 0f, ZStack = true, Corners = CornerRadius4.All(3f),
                        Rotation = -18f, Fill = turntable ? Hex(0x2B2E34) : arm,
                        BorderWidth = turntable ? 1f : 0f, BorderColor = turntable ? Hex(0x7A808A) : ColorF.Transparent,
                        Children = [Offset((headW - 2f) * 0.5f, headH - 8f, new BoxEl { Width = 2f, Height = 8f, Shrink = 0f, Fill = Hex(0xDD3333) })],
                    }),
                    // The grip: transparent and larger than the cartridge. OnDragCanceled is wired (ch 23 §6.4(1)): a lost
                    // capture without a release must not leave the arm parked in Dragging.
                    Offset(hitX, hitY, new BoxEl
                    {
                        Width = hitW, Height = hitH, Shrink = 0f, Fill = ColorF.Transparent, Cursor = CursorId.Hand,
                        OnPointerDown = p => { long ms = MsAt(p); grip.LastMs = ms; gesture.Begin(ms); },
                        OnDrag = p => { long ms = MsAt(p); grip.LastMs = ms; gesture.Move(ms); },
                        OnPointerReleased = _ => gesture.Commit(grip.LastMs),
                        OnDragCanceled = gesture.Cancel,
                    }),
                ],
            };

            return new BoxEl
            {
                Width = armW, Height = armH, Shrink = 0f, ZStack = true, TransformOriginX = 0.5f, TransformOriginY = 0.08f,
                Transform = Prop.Of(() => Affine2D.Rotation(angle1.Value * Deg2Rad)),
                Children = [lifted],
            };
        }

        static ColorF ZuneAccent(string slug) => slug switch
        {
            "orange" => Hex(0xFF7A1A),
            "green" => Hex(0x8CBF26),
            "blue" => Hex(0x1BA1E2),
            _ => Hex(0xF0568C),
        };

        /// <summary>The needle drop: a dust ring that expands and fades ONCE per thump, driven straight from the
        /// animation scheduler — three one-shot tracks, zero re-renders.</summary>
        sealed class RecordThump : Component
        {
            public required Host Host;
            public required float Size;

            static readonly Keyframe[] FadeOut = [new(0f, 0.9f, Easing.Linear), new(1f, 0f, Easing.SmoothOut)];
            static readonly Keyframe[] Expand = [new(0f, 0.2f, Easing.SmoothOut), new(1f, 1.6f, Easing.SmoothOut)];
            const float PuffMs = 500f;

            NodeHandle _node;

            public override Element Render()
            {
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
        sealed class ZuneType : Component
        {
            public required Slab Sig;
            public required float Side;
            public required float BlockW;
            public required ColorF Accent;
            Latch _latch;

            const string Display = "Segoe UI Variable Display";

            public override Element Render()
            {
                _latch.Update(Sig, image: false);
                float s = Side;
                string title = TitleOf(_latch.Shown).ToLowerInvariant();
                string artist = ArtistsOf(_latch.Shown);
                float titleSize = 0.11f * s, lineSize = 0.036f * s;
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
