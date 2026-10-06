// ── Shell/Visualizer.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// Slab (+ the poster slab), SeriesSource, InstanceSource, Clock (per frame: leases, demands, the palette fade, moments, the
// scope pull, the whole-song Timeline series), FaceSpec, the Face switch, FaceFrame, the drifting blobs and the stage's
// backdrop Field
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 760 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §2.10, §3.1, §4.9; viz-app-plan §3.2-§3.7, §6
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE DECK PATTERN, ON THE STAGE. ONE `Clock` component ticks once per FRAME while it runs (`Controls.FrameTicker`, the
// PACEABLE twin — the GPU governor may pace a visualizer), pulls the engine's bands (`Playback.Audio.CopySpectrum`) and,
// when a mounted face reads it, the time-domain window (`CopyWaveform`), folds them through the CORE `Visualizer.Model`
// and writes the `Slab` inside ONE `Runtime.Batch` — FloatSignals for scalars, `SeriesSource.Version` bumps for the
// series. Every face is built ONCE and binds `Transform`/`Opacity`/`Fill` props over the slab (compositor-only writes,
// zero re-render). A preview is the SAME face at a smaller size bound to the SAME slab — or, as a frozen POSTER, to the
// stage's poster slab, which never ticks: a poster costs nodes, never writes.
//
// THE PALETTE. SurfaceCore derives the cover's palette (`StageCtx.BasePalette`); the clock owns what faces see: the
// slab's `A/B/C/Deep` cross-fade from the colours CAPTURED at a change (600 ms on a track change, 900 ms / 1.4 s calm on
// a moment), `FadeFrom/FadeTo/FadeMix` carry the same fade to gradient nodes that bind `GradientTo/GradientMix`, and on
// landing `PaletteEpoch` bumps and `StageCtx.Palette` (the value a face builds with) is republished. A face gradient on
// `MomentMix` follows MOMENTS only: a track fade rests it at 0 at its start (solid fills cross-fade on a track, the
// gradient lands with the republished palette). Nothing here decides: the tick's inputs and every constant come from
// `Visualizer` (CORE) and `Stage` (CORE).

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Visualizer
{
    // ══ 1. THE SLAB ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A reusable sample buffer + a version the bound <c>SeriesEl.Samples</c> thunk reads (the engine's
    /// SeriesSamples contract: equal by array identity, count and version).</summary>
    public sealed class SeriesSource(int capacity)
    {
        public readonly float[] Buffer = new float[capacity];
        public readonly Signal<uint> Version = new(0u);
        public int Count = capacity;
        /// <summary>The bound view — reading <see cref="Version"/>.Value is what subscribes the bind.</summary>
        public SeriesSamples Current => new(Buffer, Count, Version.Value);
        public void Publish() => Version.Value = Version.Peek() + 1u;
    }

    /// <summary>A reusable sprite buffer + a version the bound <c>SpriteFieldEl.Instances</c> thunk reads (equal by array
    /// identity, count and version — the SeriesSource contract for instanced sprites). A face sim writes
    /// <see cref="Buffer"/>[0..<see cref="Count"/>) and calls <see cref="Publish"/> once per tick.</summary>
    public sealed class InstanceSource(int capacity)
    {
        public readonly Sprite[] Buffer = new Sprite[capacity];
        public int Count;
        public readonly Signal<uint> Version = new(0u);
        /// <summary>The bound view — reading <see cref="Version"/>.Value is what subscribes the bind.</summary>
        public SpriteInstances Current => new(Buffer, Count, Version.Value);
        public void Publish() => Version.Value = Version.Peek() + 1u;
    }

    /// <summary>Every signal a face can bind. One per stage (the gallery's live previews share it) plus one POSTER slab that
    /// never ticks (<see cref="CreatePoster"/>). Inside this class <c>Bands</c> / <c>Source</c> / <c>Scope</c> are the
    /// MEMBERS; the CORE classes are spelled <c>Visualizer.Bands</c> etc.</summary>
    public sealed class Slab
    {
        static readonly Palette s_seed = Palette.From(ColorF.FromRgba(255, 158, 196), null, dark: true);

        public readonly FloatSignal[] Bands = Make(Visualizer.Bands.Count, 0f), Peaks = Make(Visualizer.Bands.Count, 0f);
        public readonly FloatSignal Low = new(0f), Mid = new(0f), High = new(0f), Level = new(0f), Kick = new(0f), BeatPhase = new(0f);
        /// <summary>The stage backdrop Field's opacity (Stage.UI.cs Backdrop).</summary>
        public readonly FloatSignal BaseFieldOp = new(Stage.Tone.BaseFieldA);
        public readonly Signal<int> BeatIndex = new(0);
        public readonly Signal<Source> Source = new(Visualizer.Source.Breath);
        /// <summary>The accent cross-fade's live value (Stage.Tone.CrossFadeMs) — the hairline, the ring strokes, the glow tint.</summary>
        public readonly Signal<ColorF> Accent = new(ColorF.FromRgba(255, 158, 196));

        // ── the palette (cross-faded by the clock) ──
        /// <summary>The live A/B/C/Deep — every solid fill binds these (<c>Fill = Prop.Bind(slab.A)</c>).</summary>
        public readonly Signal<ColorF> A = new(s_seed.A), B = new(s_seed.B), C = new(s_seed.C), Deep = new(s_seed.Deep);
        /// <summary>Bumps when a palette fade LANDS (gradient nodes keyed by it remount on the new colours, never per tick).</summary>
        public readonly Signal<int> PaletteEpoch = new(0);
        /// <summary>0 → 1 across a MOMENT's fade, 0 at rest — and back to 0 when a track fade starts, so it never freezes
        /// part-way (a gradient node: Gradient = the palette it was built with, GradientTo = its <c>Rotated()</c>,
        /// GradientMix = this; the epoch remount lands it).</summary>
        public readonly FloatSignal MomentMix = new(0f);
        /// <summary>Any fade (a track change or a moment): Gradient = <see cref="FadeFrom"/>, GradientTo = <see cref="FadeTo"/>,
        /// GradientMix = <see cref="FadeMix"/> (0 → 1; 1 at rest, so <see cref="FadeTo"/> shows). From/To change once per
        /// fade START, in the same batch that resets the mix.</summary>
        public readonly FloatSignal FadeMix = new(1f);
        public readonly Signal<Palette> FadeFrom = new(s_seed), FadeTo = new(s_seed);

        // ── musical time ──
        /// <summary>The bar under the playhead · the bar index of the last downbeat CROSSED while playing (a seek never
        /// counts — the trigger for on-the-downbeat changes) · 0..1 through the bar.</summary>
        public readonly Signal<int> Bar = new(0), Downbeat = new(0);
        public readonly FloatSignal BarPhase = new(0f);
        /// <summary>Position / duration (the interpolated playhead), 1/4096 steps.</summary>
        public readonly FloatSignal Progress = new(0f);
        /// <summary>The spectral flux, followed (0 without a live spectrum) · the onset flash (jumps to an onset's strength,
        /// decays 0.85 per 30 Hz tick; 0 under Calm) · the 4-bar energy ahead of the playhead.</summary>
        public readonly FloatSignal Flux = new(0f), Onset = new(0f), Energy = new(0f);
        /// <summary>0 quiet · 1 normal · 2 loud (Visualizer.Sections).</summary>
        public readonly Signal<byte> Section = new(Sections.Normal);
        /// <summary>Ring's ripple ring buffer: each slot's 0..1 age, 1 = none.</summary>
        public readonly FloatSignal[] Ripples = Make(Beat.Ripples, 1f);

        // ── series ──
        /// <summary>The scope line: 512 samples 0..1 (0.5 = silence) for a Cartesian SeriesEl; the polar ring: 256 radius
        /// fractions (SeriesShape.Polar). Filled only while a mounted face reads them (<see cref="ScopeReaders"/>);
        /// <see cref="ScopeLive"/> says the engine's tap (true) or the spectral synthesis (false) drew them.</summary>
        public readonly SeriesSource Scope = new(Oscilloscope.Points), ScopeRadial = new(Oscilloscope.RadialPoints);
        public readonly Signal<bool> ScopeLive = new(false);
        int _scopeReaders;
        /// <summary>How many MOUNTED faces read <see cref="Scope"/> / <see cref="ScopeRadial"/> off this slab
        /// (<see cref="UseScopeReader"/>). A plain count, never a signal: the clock Peeks it per tick (no re-lease, no render).</summary>
        public int ScopeReaders => _scopeReaders;
        internal void AddScopeReader() => _scopeReaders++;
        internal void RemoveScopeReader() => _scopeReaders = Math.Max(0, _scopeReaders - 1);
        public readonly SeriesSource AuroraLow = new(Aurora.Points), AuroraMid = new(Aurora.Points), AuroraHigh = new(Aurora.Points);
        /// <summary>The WHOLE song's kind-237 waveform as 360 buckets — static per track (empty ⇒ all zeros).</summary>
        public readonly SeriesSource TimelineLow = new(Timeline.Points), TimelineMid = new(Timeline.Points), TimelineHigh = new(Timeline.Points);

        /// <summary>Diagnostics (read by Screens/Diagnostics.UI.cs's card).</summary>
        public float LastFftMs; public long LastAlignFrames, LastSequence; public Tier LastTier;

        public Slab()
        {
            Oscilloscope.Flat(Scope.Buffer, ScopeRadial.Buffer);
            Aurora.Fill(AuroraLow.Buffer, AuroraMid.Buffer, AuroraHigh.Buffer, 0f, 0f, 0f, 0f, 0f, 0f);
        }

        /// <summary>The gallery's frozen slab: a pleasant, palette-coloured REST pose (a real spread shape, a ripple, a
        /// still scope line) that nothing ever ticks — a poster tile binds it and costs zero writes. The clock recolours it
        /// when a palette fade lands (<see cref="SetPalette"/>) and copies the track's Timeline series into it.</summary>
        public static Slab CreatePoster()
        {
            var s = new Slab();
            Span<float> shape = stackalloc float[Visualizer.Bands.Count];
            Visualizer.Bands.Spread(0.62f, 0.46f, 0.34f, 7, shape);
            for (int i = 0; i < shape.Length; i++) { s.Bands[i].Value = shape[i]; s.Peaks[i].Value = MathF.Min(1f, shape[i] + 0.06f); }
            s.Low.Value = 0.55f; s.Mid.Value = 0.42f; s.High.Value = 0.30f; s.Level.Value = 0.45f; s.Kick.Value = 0.2f; s.BeatPhase.Value = 0.3f;
            s.BarPhase.Value = 0.3f; s.Progress.Value = 0.38f; s.Energy.Value = 0.45f; s.Flux.Value = 0.2f;
            s.Ripples[0].Value = 0.35f; s.Ripples[1].Value = 0.7f;
            s.Source.Value = Visualizer.Source.Live;
            Oscilloscope.Synthesize(shape, 1.3f, s.Scope.Buffer, s.ScopeRadial.Buffer);
            Aurora.Fill(s.AuroraLow.Buffer, s.AuroraMid.Buffer, s.AuroraHigh.Buffer, 0.55f, 0.42f, 0.30f, 0.8f, 1.9f, 3.2f);
            return s;
        }

        /// <summary>Snap every palette channel to <paramref name="p"/> and bump the epoch (the poster slab's recolour).</summary>
        public void SetPalette(in Palette p)
        {
            SetSlabColour(A, p.A); SetSlabColour(B, p.B); SetSlabColour(C, p.C); SetSlabColour(Deep, p.Deep); SetSlabColour(Accent, p.Accent);
            if (!FadeFrom.Peek().Equals(p)) FadeFrom.Value = p;
            if (!FadeTo.Peek().Equals(p)) FadeTo.Value = p;
            if (FadeMix.Peek() != 1f) FadeMix.Value = 1f;
            PaletteEpoch.Value = PaletteEpoch.Peek() + 1;
        }

        static FloatSignal[] Make(int n, float seed)
        {
            var a = new FloatSignal[n];
            for (int i = 0; i < n; i++) a[i] = new FloatSignal(seed);
            return a;
        }
    }

    static void SetSlabColour(Signal<ColorF> s, ColorF v) { if (!v.Equals(s.Peek())) s.Value = v; }

    /// <summary>Count the calling component as a reader of <paramref name="slab"/>'s scope series while
    /// <paramref name="reads"/> holds (and it stays mounted): the clock fills them only while one exists
    /// (<see cref="Demand.PullsScope"/>). The MOUNT SITES call it (the stage's FaceHost for a Scope/Flow face, a LIVE
    /// gallery tile of those kinds); a face builder never does, it is not a component and would count twice. Call it
    /// unconditionally, in stable hook order (<c>reads</c> is the effect's dependency).</summary>
    public static void UseScopeReader(RenderContext rc, Slab slab, bool reads, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
        => rc.UseEffect(() =>
        {
            if (!reads) return null;
            slab.AddScopeReader();
            return slab.RemoveScopeReader;
        }, DepKey.From(reads), __hf, __hl);

    // ══ 2. THE CLOCK ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Mounted ONCE by Stage.UI.cs's SurfaceCore (zero-size), inside its <c>Ctx.Provide(Stage.StageContext, …)</c>.
    /// Holds the lease the VISIBLE consumers need, demands the precomputed edges for the current track, owns the palette
    /// fade and the moments, and folds one <see cref="Model.Tick"/> per tick into the slab. Every preference it needs
    /// arrives as a SIGNAL on the context (SurfaceCore's one epoch effect writes them — O8): the tick Peeks, never reads
    /// the registry.</summary>
    public sealed class Clock : Component
    {
        readonly Model _model = new();
        readonly float[] _db = new float[Visualizer.Bands.Count];
        readonly float[] _wave = new float[AudioEffects.WaveformSamples];
        readonly float[] _scopeLine = new float[Oscilloscope.Points], _scopeRadial = new float[Oscilloscope.RadialPoints];
        readonly float[] _tlLow = new float[Timeline.Points], _tlMid = new float[Timeline.Points], _tlHigh = new float[Timeline.Points];
        readonly Action _tick, _tickCore, _write, _onPosition, _demandEdges, _onTimeline, _onBasePalette;
        readonly Func<Action?> _lease;
        readonly Signal<bool> _settled = new(true);   // the deck's mirror (Deck.UI.cs:233, :378-379): the gate reads a SIGNAL, never _model.IsSettled
        Stage.StageCtx? _ctx;
        InputHooks? _hooks;                           // captured in Render (V-U42), read by the lease effect
        Deck.PositionInterpolator _pos;
        bool _anchored, _advancing, _run;
        long _lastTickMs;
        int _lastReportMs = int.MinValue;
        Frame _pending;
        bool _haveLive;
        // the accent cross-fade (the hairline, the brand chip, the strokes)
        ColorF _accentTarget, _fadeFrom;
        long _fadeStartMs = -1;                       // −1 = seeded, nothing to fade
        // the palette: the cover's base, the live (rotated) target, the one fade, the moments schedule
        Palette _base, _live;
        Palette.Fade _fade;
        bool _paletteSeeded, _fadeSignalsPending;
        Moments.Schedule _moments;
        // the scope: filled only while a mounted face reads it (Slab.ScopeReaders, peeked per tick), the synthesis at most
        // at the spectrum's publish rate; the Aurora ribbons only while something can show them
        bool _scopeFresh, _scopeLive, _onsetPrimed, _ribbons;
        long _lastSequence, _lastOnsetSequence, _timelineKey = -1, _lastSynthMs;
        uint _timelineScope;
        float _scopeAgc, _scopeT;

        public Clock()
        {
            _tickCore = TickCore;
            _tick = () => Reactive.Untrack(_tickCore);
            _write = WriteCore;
            _onPosition = OnPosition;
            _demandEdges = DemandEdges;
            _onTimeline = OnTimeline;
            _onBasePalette = OnBasePalette;
            _lease = Lease;
        }

        public override Element Render()
        {
            var ctx = UseContext(Stage.StageContext)!;
            _ctx = ctx;
            _hooks = UseContext(InputHooks.Current);
            bool playing = Playback.IsPlaying.Value;
            bool occluded = _hooks.WindowOccluded?.Value ?? false;
            bool run = Demand.Ticks(Shell.Ui.ImmersiveLyrics.Value, playing, _settled.Value, occluded, Design.Reduced);
            _run = run;
            UseEffect(_lease);
            UseEffect(_onPosition);
            UseEffect(_demandEdges);
            UseEffect(_onTimeline);
            // the accent TARGET: seed the slab on the first run; afterwards capture the fade's start colour and time (V-U17)
            UseSignalEffect(() =>
            {
                var target = Stage.AccentSignal.Value;
                if (target.Equals(_accentTarget)) return;
                _accentTarget = target;
                if (_fadeStartMs < 0 || !_run) { ctx.Slab.Accent.Value = target; _fadeStartMs = 0; return; }   // mount, or the clock is not running: snap
                _fadeFrom = ctx.Slab.Accent.Peek();
                _fadeStartMs = Design.FrameTime.NowMs;
            });
            // the palette TARGET (the cover's base palette): snap on mount / while stopped, else a CrossFadeMs fade
            UseSignalEffect(_onBasePalette);
            // the clock stopped mid-fade (settled / occluded / reduced): land both fades on their targets
            UseEffect(() =>
            {
                if (run) return;
                if (!ctx.Slab.Accent.Peek().Equals(_accentTarget)) ctx.Slab.Accent.Value = _accentTarget;
                _fadeStartMs = 0;
                if (_fade.Active || _fadeSignalsPending) LandPalette();
            }, DepKey.From(run));
            // PER FRAME while it runs: the ticker is mounted only then, so a settled / occluded / paused stage idles.
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Children = run ? [Embed.Comp(() => new VisualizerFrames(_tick))] : [] };
        }

        /// <summary>The clock's per-frame tick (<see cref="Controls.FrameTicker"/>, the paceable twin), named for the
        /// <c>[wake]</c> census.</summary>
        sealed class VisualizerFrames(Action tick) : Controls.FrameTicker(tick, paceable: true);

        /// <summary>The ONE lease: the tier the VISIBLE consumers need — the selected face in Visualizer mode raised to the
        /// shown gallery's preview tier, the base Field's breath otherwise (V-U55) — under the stage/playing/owner/supported/
        /// occluded/reduced gates (Visualizer.Demand.For). "Shown" is <c>StageCtx.GalleryShown</c>: open AND the layout has
        /// room (the gallery's own mount gate minus the idle chrome, which would re-lease on every idle flip). Spectrum
        /// implies level (the engine's AcquireSpectrum does that); Scope is the spectrum lease plus the per-tick waveform
        /// pull, which the tick gates on a mounted reader (Demand.PullsScope).</summary>
        Action? Lease()
        {
            var ctx = _ctx!;
            bool viz = ctx.Mode.Value == Stage.Mode.Visualizer, gallery = ctx.GalleryShown.Value;
            var kind = ctx.Kind.Value;
            // the face's needs inside Card; a layout's strip spectrum (or the level the backdrop field breathes with when it has none)
            var needs = Stage.LayoutRules.NeedsOf(ctx.Look.Value.Eff, ctx.SpectrumPref.Value, kind);
            var tier = Demand.For(needs, viz, gallery, Shell.Ui.ImmersiveLyrics.Value, Playback.IsPlaying.Value,
                                  Playback.OwnerSignal.Value == Playback.Owner.Us, Playback.Audio.Supported.Value,
                                  _hooks?.WindowOccluded?.Value ?? false, Design.Reduced);
            ctx.Slab.LastTier = tier;
            Stage.Diagnostics.NoteLease(tier);
            if (tier == Tier.None) return null;
            IDisposable lease = tier >= Tier.Spectrum ? Playback.Audio.AcquireSpectrum() : Playback.Audio.AcquireLevels();
            return lease.Dispose;
        }

        /// <summary>Re-anchor the playhead on every host report AND on every advancing edge (the deck's PositionInterpolator
        /// discipline, Deck.cs:269-276: without the edge a resume would extrapolate across the whole paused gap until the
        /// next ~1 Hz report). While the clock is stopped the report also lands the progress (a paused seek moves it).</summary>
        void OnPosition()
        {
            int pos = Playback.PositionMs.Value;
            bool playing = Playback.IsPlaying.Value;
            bool loading = Playback.PhaseSignal.Value == Playback.Phase.Loading;
            bool buffering = Playback.Buffering.Value || loading;
            _ = Playback.Current.Value;
            bool advancing = playing && !buffering;
            if (pos != _lastReportMs || !_anchored || advancing != _advancing)
            {
                _pos.Anchor(Design.FrameTime.NowMs, pos);
                _anchored = true;
                _lastReportMs = pos;
            }
            _advancing = advancing;
            if (!_run && _ctx is { } ctx)
            {
                int dur = Playback.DurationMs.Peek();
                Set(ctx.Slab.Progress, dur > 0 ? Q(Math.Clamp(pos / (float)dur, 0f, 1f), 1f / 4096f) : 0f);
            }
        }

        /// <summary>The precomputed sources for the playing track: the banded waveform, the beat grid, the audio group.</summary>
        void DemandEdges()
        {
            var cur = Playback.Current.Value;
            _ = Entities.ScopeEpoch.Value;
            if (cur.Kind != EntityKind.Track || cur.IsNone) return;
            var t = new Track(cur.Slot);
            if (!t.IsValid) return;
            Entities.EnsureEdge(FetchEdge.TrackWaveform, t.Slot);
            Entities.EnsureEdge(FetchEdge.TrackBeats, t.Slot);
            if (!t.Knows(TrackFields.Audio)) Entities.Ensure(t, TrackFields.Audio);
        }

        /// <summary>The whole-song Timeline series, ONCE per (scope, track, payload write): resampled when the waveform edge
        /// lands, into the live slab and the poster slab — an effect, not the tick, so a paused stage shows the song too. The
        /// key carries the parent's edge VERSION (bumped by every write), so a same-length replacement re-reads too.</summary>
        void OnTimeline()
        {
            uint scope = Entities.ScopeEpoch.Value;           // FIRST: a scope switch re-points the table read below
            var cur = Playback.Current.Value;
            var edges = Entities.Current.Edges;
            _ = edges.TrackWaveform.Changed.Value;
            ReadOnlySpan<WaveSample> bands = default;
            long key = 0L;
            if (cur.Kind == EntityKind.Track && !cur.IsNone && edges.TrackWaveform.State(cur.Slot) == EdgeState.Complete)
            {
                bands = edges.TrackWaveform.Payload(cur.Slot);
                key = ((long)cur.Slot << 32) | edges.TrackWaveform.Version(cur.Slot);
            }
            if ((key == _timelineKey && scope == _timelineScope) || _ctx is not { } ctx) return;
            _timelineKey = key; _timelineScope = scope;
            Timeline.Resample(bands, _tlLow, _tlMid, _tlHigh);
            foreach (var slab in new[] { ctx.Slab, ctx.PosterSlab })
            {
                Copy(_tlLow, slab.TimelineLow); Copy(_tlMid, slab.TimelineMid); Copy(_tlHigh, slab.TimelineHigh);
            }
        }

        /// <summary>The cover's palette moved (a track, a late grading, a theme flip): snap on mount or while stopped,
        /// else fade from the CAPTURED colours over <see cref="Stage.Tone.CrossFadeMs"/>; the rotation resets.</summary>
        void OnBasePalette()
        {
            var ctx = _ctx!;
            var b = ctx.BasePalette.Value;
            if (_paletteSeeded && b.Equals(_base)) return;
            _base = b;
            _moments.Reset();
            if (!_paletteSeeded || !_run) { _paletteSeeded = true; SnapPalette(b); return; }
            BeginFade(b, Stage.Tone.CrossFadeMs, moment: false);
        }

        void SnapPalette(in Palette p)
        {
            var ctx = _ctx!;
            _fade.Land();
            _fadeSignalsPending = false;
            _live = p;
            var s = ctx.Slab;
            SetSlabColour(s.A, p.A); SetSlabColour(s.B, p.B); SetSlabColour(s.C, p.C); SetSlabColour(s.Deep, p.Deep);
            if (!s.FadeFrom.Peek().Equals(p)) s.FadeFrom.Value = p;
            if (!s.FadeTo.Peek().Equals(p)) s.FadeTo.Value = p;
            Set(s.FadeMix, 1f); Set(s.MomentMix, 0f);
            s.PaletteEpoch.Value = s.PaletteEpoch.Peek() + 1;
            ctx.Palette.SetIfChanged(p);
            ctx.PosterSlab.SetPalette(p);
        }

        /// <summary>Start a fade toward <paramref name="target"/> from what is ON SCREEN now (mid-fade included). The
        /// gradient channels' From/To are written by the next tick's batch, together with the mix reset.</summary>
        void BeginFade(in Palette target, float durationMs, bool moment)
        {
            var s = _ctx!.Slab;
            var from = new Palette(_live.Accent, s.A.Peek(), s.B.Peek(), s.C.Peek(), s.Deep.Peek(), _live.Dark);
            _fade.Begin(from, target, Design.FrameTime.NowMs, durationMs, moment);
            _live = target;
            _fadeSignalsPending = true;
        }

        /// <summary>The fade landed (or the clock stopped mid-fade): exact target colours, the mixes at rest, the epoch,
        /// the live palette republished (gradient faces remount on it) and the poster recoloured.</summary>
        void LandPalette()
        {
            var ctx = _ctx!;
            var s = ctx.Slab;
            _fade.Land();
            SetSlabColour(s.A, _live.A); SetSlabColour(s.B, _live.B); SetSlabColour(s.C, _live.C); SetSlabColour(s.Deep, _live.Deep);
            if (_fadeSignalsPending || !s.FadeTo.Peek().Equals(_live)) { s.FadeTo.Value = _live; _fadeSignalsPending = false; }
            Set(s.FadeMix, 1f); Set(s.MomentMix, 0f);
            s.PaletteEpoch.Value = s.PaletteEpoch.Peek() + 1;
            ctx.Palette.SetIfChanged(_live);
            ctx.PosterSlab.SetPalette(_live);
        }

        void TickCore()
        {
            var ctx = _ctx!;
            var slab = ctx.Slab;
            long now = Design.FrameTime.NowMs;
            if (!_anchored) { _pos.Anchor(now, Playback.PositionMs.Peek()); _anchored = true; }
            float dt = DeltaSec(_lastTickMs, now);
            _lastTickMs = now;

            int n = Playback.Audio.CopySpectrum(_db, out SpectrumInfo info);
            _haveLive = n == Visualizer.Bands.Count && info.Live && info.Sequence != 0;
            bool fresh = info.Sequence != _lastSequence;                 // a new publish since the last tick (dedupe: one frame, one read)
            _lastSequence = info.Sequence;
            slab.LastFftMs = info.FftMs; slab.LastAlignFrames = info.AlignFrames; slab.LastSequence = info.Sequence;
            // an onset is a NEW OnsetSequence; the first one seen after a (re)lease is history, not an onset
            bool onset = false;
            if (!_haveLive) _onsetPrimed = false;
            else if (!_onsetPrimed) { _onsetPrimed = true; _lastOnsetSequence = info.OnsetSequence; }
            else if (info.OnsetSequence != _lastOnsetSequence) { onset = true; _lastOnsetSequence = info.OnsetSequence; }
            // the level: the analysed window's PRE-gain RMS under a spectrum lease, the (post-gain) level tap under a level lease
            bool haveLevel = !_haveLive && slab.LastTier >= Tier.Level;
            float rms = _haveLive ? info.WindowRms : Playback.Audio.Levels.Peek().Rms;

            var cur = Playback.Current.Peek();
            long dur = Playback.DurationMs.Peek();
            long pos = _pos.Estimate(now, _advancing, null, null, dur);
            ReadOnlySpan<WaveSample> bands = default; ReadOnlySpan<uint> beats = default; ushort tempo = 0;
            bool haveBands = false, haveBeats = false;
            if (cur.Kind == EntityKind.Track && !cur.IsNone)
            {
                var edges = Entities.Current.Edges;
                if (edges.TrackWaveform.State(cur.Slot) == EdgeState.Complete) { bands = edges.TrackWaveform.Payload(cur.Slot); haveBands = bands.Length > 0; }
                if (edges.TrackBeats.State(cur.Slot) == EdgeState.Complete) { beats = edges.TrackBeats.Payload(cur.Slot); haveBeats = beats.Length > 0; }
                var t = new Track(cur.Slot);
                if (t.IsValid && t.Knows(TrackFields.Audio)) tempo = t.Tempo;
            }
            // every preference is a PEEK of a context signal (O8) — never Prefs.* on the tick
            bool playing = Playback.IsPlaying.Peek(), calm = ctx.Calm.Peek(), viz = ctx.Mode.Peek() == Stage.Mode.Visualizer;
            float sensitivity = ctx.Sensitivity.Peek();
            // the Aurora ribbon arrays: only while an Aurora stage face or the shown gallery could put them on screen
            bool underFace = viz && Stage.LayoutRules.ShowsFace(ctx.Look.Peek().Eff);   // a layout has no face: its backdrop field breathes as it does in Lyrics mode
            _ribbons = underFace && (Catalog.Successor(ctx.Kind.Peek()) == Kind.Aurora || ctx.GalleryShown.Peek());
            var input = new Input(playing, calm, sensitivity, _haveLive, info.Muted, rms, pos, dur, haveBands, haveBeats, tempo,
                                  underFace, HaveLevel: haveLevel, Flux: info.Flux, Onset: onset, OnsetStrength: info.OnsetStrength, Ribbons: _ribbons);
            _pending = _model.Tick(in input, _db, bands, beats, dt);

            // a moment: on the 8th bar's downbeat (or a face's force), rotate the live palette and fade to it
            if (_paletteSeeded && _moments.Step(_pending.Bar, _pending.DownbeatEdge, ctx.Moments.Peek(), Moments.ForcedSequence))
                BeginFade(_live.Rotated(), Moments.FadeMsFor(calm), moment: true);

            // the scope, while a mounted face reads it: the engine's window on a fresh publish, else (no tap: Connect,
            // --fake) the spectral synthesis — on a fresh publish too, else at most once per Oscilloscope.SynthesizeMs
            _scopeFresh = false;
            if (Demand.PullsScope(slab.LastTier, viz, slab.ScopeReaders))
            {
                if (_haveLive)
                {
                    WaveformInfo wi = default;
                    int w = fresh ? Playback.Audio.CopyWaveform(_wave, out wi) : 0;
                    if (w > 0 && wi.Live)
                    {
                        float gain = Visualizer.Bands.Gain(Visualizer.Bands.ClampSensitivity(sensitivity), calm);
                        Oscilloscope.Fill(_wave.AsSpan(0, w), _scopeLine, _scopeRadial, ref _scopeAgc, gain, dt * Visualizer.Bands.RefHz);
                        _scopeFresh = true; _scopeLive = true;
                    }
                }
                else
                {
                    if (playing) _scopeT += dt;
                    if (fresh || now - _lastSynthMs >= Oscilloscope.SynthesizeMs)
                    {
                        _lastSynthMs = now;
                        Oscilloscope.Synthesize(_model.Level, _scopeT, _scopeLine, _scopeRadial);
                        _scopeFresh = true; _scopeLive = false;
                    }
                }
            }
            if (Context.Runtime is { } rt) rt.Batch(_write); else WriteCore();
        }

        /// <summary>ONE batch → ONE frame request, however many signals moved. Peek-compare before every write.</summary>
        void WriteCore()
        {
            var ctx = _ctx!;
            var s = ctx.Slab; var f = _pending; var m = _model;
            for (int i = 0; i < Visualizer.Bands.Count; i++) { Set(s.Bands[i], Q(m.Level[i], 1f / 128f)); Set(s.Peaks[i], Q(m.Peak[i], 1f / 128f)); }
            Set(s.Low, Q(f.Low, 1f / 256f)); Set(s.Mid, Q(f.Mid, 1f / 256f)); Set(s.High, Q(f.High, 1f / 256f)); Set(s.Level, Q(f.Level, 1f / 256f));
            Set(s.Kick, Q(f.Kick, 1f / 256f)); Set(s.BeatPhase, Q(f.BeatPhase, 1f / 256f)); Set(s.BaseFieldOp, Q(f.BaseFieldOp, 1f / 256f));
            Set(s.BarPhase, Q(f.BarPhase, 1f / 256f)); Set(s.Progress, Q(f.Progress, 1f / 4096f));
            Set(s.Flux, Q(f.Flux, 1f / 256f)); Set(s.Onset, Q(f.Onset, 1f / 256f)); Set(s.Energy, Q(f.Energy, 1f / 256f));
            for (int r = 0; r < Beat.Ripples; r++) Set(s.Ripples[r], Q(m.RippleAge[r], 1f / 256f));
            if (s.BeatIndex.Peek() != f.BeatIndex) s.BeatIndex.Value = f.BeatIndex;
            if (s.Bar.Peek() != f.Bar) s.Bar.Value = f.Bar;
            if (s.Downbeat.Peek() != f.Downbeat) s.Downbeat.Value = f.Downbeat;
            if (s.Section.Peek() != f.Section) s.Section.Value = f.Section;
            if (s.Source.Peek() != f.Source) { s.Source.Value = f.Source; Stage.Diagnostics.NoteSource(f.Source); }
            if (_ribbons) { Copy(m.AuroraLow, s.AuroraLow); Copy(m.AuroraMid, s.AuroraMid); Copy(m.AuroraHigh, s.AuroraHigh); }
            if (_scopeFresh)
            {
                Copy(_scopeLine, s.Scope); Copy(_scopeRadial, s.ScopeRadial);
                if (s.ScopeLive.Peek() != _scopeLive) s.ScopeLive.Value = _scopeLive;
            }
            // the accent cross-fade rides the same tick (a bound Fill snaps, §1.8): LINEAR from the captured start over
            // elapsed / Stage.Tone.CrossFadeMs, landing exactly on the target (V-U17)
            if (_fadeStartMs > 0)
            {
                float p = Stage.Tone.Progress(_lastTickMs - _fadeStartMs);
                var next = p >= 1f ? _accentTarget : ColorF.Lerp(_fadeFrom, _accentTarget, p);
                if (!next.Equals(s.Accent.Peek())) s.Accent.Value = next;
                if (p >= 1f) _fadeStartMs = 0;
            }
            // the palette fade: the gradient channels' endpoints once at the start (with the mix reset), then the lerp. A
            // TRACK fade also rests MomentMix at 0 in this batch: the tick drives it only through a moment's fade, so a
            // track change landing mid-moment would freeze it part-way (gradients follow moments only; solid fills carry
            // the track's cross-fade and the gradient lands with the republished palette)
            if (_fadeSignalsPending)
            {
                s.FadeFrom.Value = _fade.From; s.FadeTo.Value = _fade.To; Set(s.FadeMix, 0f);
                if (!_fade.Moment) Set(s.MomentMix, 0f);
                _fadeSignalsPending = false;
            }
            if (_fade.Active)
            {
                float p = _fade.Progress(_lastTickMs);
                if (p >= 1f) LandPalette();
                else
                {
                    var at = Palette.Lerp(_fade.From, _fade.To, p);
                    SetSlabColour(s.A, at.A); SetSlabColour(s.B, at.B); SetSlabColour(s.C, at.C); SetSlabColour(s.Deep, at.Deep);
                    Set(s.FadeMix, Q(p, 1f / 256f));
                    if (_fade.Moment) Set(s.MomentMix, Q(p, 1f / 256f));
                }
            }
            bool settled = m.IsSettled && !_fade.Active && _fadeStartMs <= 0;
            if (_settled.Peek() != settled) _settled.Value = settled;   // the gate's mirror (Deck.UI.cs:378-379)
        }

        static void Set(FloatSignal s, float v) { if (v != s.Peek()) s.Value = v; }
        static float Q(float v, float q) => MathF.Round(v / q) * q;
        /// <summary>Bit-identical samples publish NOTHING: most ticks refill the same values (a paused Aurora, a scope over
        /// silence), and a version bump re-records every bound SeriesEl for no pixel.</summary>
        static void Copy(float[] src, SeriesSource dst)
        {
            if (dst.Count == src.Length && src.AsSpan().SequenceEqual(dst.Buffer.AsSpan(0, src.Length))) return;
            src.AsSpan().CopyTo(dst.Buffer); dst.Count = src.Length; dst.Publish();
        }
    }

    // ══ 3. THE FACES ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The box a face fills and how it is shown. <see cref="Preview"/>: a gallery tile (fewer parts, no blur, no
    /// per-frame sim if avoidable); <see cref="Poster"/>: a FROZEN tile bound to the poster slab — mount no sim, no ticker.
    /// The SAFE rect (stage coordinates, <c>Stage.Layout.FaceSafe</c>) is where a stage face centres its subject: clear of
    /// the now-playing card, the caption and the transport; 0/0/0/0 (a preview) means the whole box.</summary>
    public readonly record struct FaceSpec(float W, float H, bool Preview, string? CoverUrl,
        bool Poster = false, float SafeLeft = 0f, float SafeTop = 0f, float SafeRight = 0f, float SafeBottom = 0f)
    {
        public float SafeX1 => SafeRight > SafeLeft ? MathF.Min(SafeRight, W) : W;
        public float SafeY1 => SafeBottom > SafeTop ? MathF.Min(SafeBottom, H) : H;
        public float SafeW => MathF.Max(1f, SafeX1 - SafeLeft);
        public float SafeH => MathF.Max(1f, SafeY1 - SafeTop);
        public float SafeCx => SafeLeft + SafeW * 0.5f;
        public float SafeCy => SafeTop + SafeH * 0.5f;
    }

    /// <summary>Build a face ONCE for a slab; everything that moves is a bound prop over it. The stage remounts per (kind,
    /// width) on a keyed CHILD box; the gallery mounts the shown faces at preview scale. A legacy kind draws its successor
    /// (the stored int was already coerced; this is the belt to that brace).</summary>
    public static Element Face(Kind kind, Slab slab, in Palette pal, in FaceSpec spec) => Catalog.Successor(kind) switch
    {
        Kind.Verse => global::Wavee.Verse.Face(slab, in pal, in spec),
        Kind.Bars => BarsFace(slab, in pal, in spec),
        Kind.Ring => RingFace(slab, in pal, in spec),
        Kind.Orbit => OrbitFace(slab, in pal, in spec),
        Kind.Aurora => AuroraFace(slab, in pal, in spec),
        Kind.Timeline => TimelineFace(slab, in pal, in spec),
        Kind.Classic => ClassicFace(slab, in pal, in spec),
        Kind.Warp => WarpFace(slab, in pal, in spec),
        Kind.Tunnel => TunnelFace(slab, in pal, in spec),
        Kind.Ambience => AmbienceFace(slab, in pal, in spec),
        Kind.Kaleido => KaleidoFace(slab, in pal, in spec),
        Kind.Scope => ScopeFace(slab, in pal, in spec),
        Kind.Drift => DriftFace(slab, in pal, in spec),
        Kind.Type => TypeFace(slab, in pal, in spec),
        Kind.Mosaic => MosaicFace(slab, in pal, in spec),
        Kind.Spotlight => SpotlightFace(slab, in pal, in spec),
        Kind.Magneto => MagnetoFace(slab, in pal, in spec),
        Kind.Flow => FlowFace(slab, in pal, in spec),
        _ => BloomFace(slab, in pal, in spec),
    };

    static float Min(in FaceSpec s) => MathF.Min(s.W, s.H);
    static float Max(in FaceSpec s) => MathF.Max(s.W, s.H);

    // ── the frame every face shares ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The clipped box a face fills; <c>Canvas.Create</c> takes the children list directly (IReadOnlyList&lt;CanvasChild&gt;,
    /// Canvas.cs:16) — no wrapper record (V-U2). Named FaceFrame: <see cref="Frame"/> is the model's output record.
    /// A face animates every frame while the content under and over it (backdrop art, scrim, caption, panels) stays still,
    /// so it is a RepaintBoundary: its motion re-rasters only its own slice, never the tiles of the stage around it.</summary>
    static Element FaceFrame(in FaceSpec spec, List<CanvasChild> kids) => new BoxEl
    {
        Width = spec.W, Height = spec.H, ClipToBounds = true, HitTestVisible = false, RepaintBoundary = true,
        Children = [Canvas.Create(spec.W, spec.H, kids)],
    };

    // ── drifting blobs: a radial disc on 28–36 s ping-pong keyframe loops, zero ticks ─────────────────────────────────

    /// <summary>The keyframe drift a blob rides (translate + scale on its own root — the hooks' HostNode contract, which is
    /// why each blob is a component). Under reduced motion the loop is replaced by REST keys (the engine does not snap
    /// looping keyframes — §1.8, V-E10).</summary>
    abstract class DriftingDisc : Component
    {
        static readonly Keyframe[] s_restTranslate = [new Keyframe(0f, 0f), new Keyframe(1f, 0f)];
        static readonly Keyframe[] s_restScale = [new Keyframe(0f, 1f), new Keyframe(1f, 1f)];

        protected void Drift(int index, float w, float h)
        {
            var (dx, dy, sc) = Field.Drift[index % Field.Blobs];
            float ms = Field.PeriodSec[index % Field.Blobs] * 1000f;
            bool reduced = Design.Reduced;
            var key = DepKey.From(index, (int)w, (int)h, reduced ? 1 : 0);   // the 4-int form — there is no 3-int DepKey (V-U3)
            UseKeyframes(AnimChannel.TranslateX, reduced ? s_restTranslate : [new Keyframe(0f, 0f), new Keyframe(0.5f, dx * w, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.TranslateY, reduced ? s_restTranslate : [new Keyframe(0f, 0f), new Keyframe(0.5f, dy * h, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.ScaleX, reduced ? s_restScale : [new Keyframe(0f, 1f), new Keyframe(0.5f, sc, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.ScaleY, reduced ? s_restScale : [new Keyframe(0f, 1f), new Keyframe(0.5f, sc, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)], ms, loop: !reduced, key);
            Context.UseAmbientPause(AmbientMotion.Pose, key, loops: !reduced);
        }

        /// <summary>The disc's radial gradient: the tint, its 45 % shoulder, transparent at the rim.</summary>
        protected static GradientSpec Disc(ColorF tint, float alpha)
            => new(GradientShape.Radial, 0f, [new GradientStop(0f, tint with { A = alpha }), new GradientStop(0.55f, tint with { A = alpha * 0.45f }), new GradientStop(1f, tint with { A = 0f })]);
    }

    /// <summary>One blob of a fixed tint (a face's own blob; RE-PUSHED props recolour the mounted disc, the loop keeps running).</summary>
    sealed class Blob : DriftingDisc
    {
        public sealed record Props(int Index, float Diameter, ColorF Tint, float W, float H);
        public override Element Render()
        {
            var p = UseProps<Props>();
            Drift(p.Index, p.W, p.H);
            return new BoxEl
            {
                Width = p.Diameter, Height = p.Diameter, Corners = Radii.Circle(p.Diameter), HitTestVisible = false,
                Gradient = Disc(p.Tint, p.Tint.A),
            };
        }
    }

    /// <summary>One blob of the stage's backdrop Field, tinted by palette ROLE and cross-faded with the palette: its
    /// gradient runs from the fade's start colours to its target (<c>GradientTo</c>), mixed by the slab's bound
    /// <c>FadeMix</c> — the component re-renders once per fade START, never per tick.</summary>
    sealed class BackdropBlob : DriftingDisc
    {
        public sealed record Props(int Index, float Diameter, float W, float H, Slab Slab);
        public override Element Render()
        {
            var p = UseProps<Props>();
            var start = p.Slab.FadeFrom.Value;
            var end = p.Slab.FadeTo.Value;
            Drift(p.Index, p.W, p.H);
            return new BoxEl
            {
                Width = p.Diameter, Height = p.Diameter, Corners = Radii.Circle(p.Diameter), HitTestVisible = false,
                Gradient = Disc(TintOf(in start, p.Index), AlphaOf(p.Index)), GradientTo = Disc(TintOf(in end, p.Index), AlphaOf(p.Index)),
                GradientMix = (Prop<float>)p.Slab.FadeMix,
            };
        }

        /// <summary>The four blobs' roles: the deep tone top-left, then B, A and C — the cover's own colours, no invented hue.</summary>
        static ColorF TintOf(in Palette pal, int i) => (i & 3) switch { 0 => pal.Deep, 1 => pal.B, 2 => pal.A, _ => pal.C };
        /// <summary>The paint colours are brighter than the deep tone: a backdrop, not a face, so they sit a step back.</summary>
        static float AlphaOf(int i) => (i & 3) == 0 ? 1f : 0.82f;
    }

    /// <summary>The stage's always-on backdrop layer (Stage.UI.cs Backdrop): four drifting blobs in the palette's roles,
    /// cross-fading with it, at <paramref name="opacity"/> (the slab's BaseFieldOp). The four blobs drift every frame under
    /// the whole stage: a RepaintBoundary keeps that drift in this slice instead of re-rastering every stage tile (art,
    /// scrim, bars, caption) above and below it. They are pure radial gradients, so the slice rasters at a quarter of the
    /// window scale and is upsampled — the same look for 1/16 of the raster work, and no window-sized tiles held.</summary>
    public static Element BackdropField(Slab slab, float w, float h, Prop<float> opacity, bool boundary = true)
    {
        float m = MathF.Max(w, h), d = 1.1f * m;
        var kids = new List<CanvasChild>(Field.Blobs);
        (float X, float Y)[] corners = [(-0.55f, -0.62f), (1f - 0.55f, -0.62f), (-0.55f, 1f - 0.66f), (1f - 0.58f, 1f - 0.64f)];
        for (int i = 0; i < Field.Blobs; i++)
            kids.Add(new CanvasChild(corners[i].X * m, corners[i].Y * m, Embed.Comp(new BackdropBlob.Props(i, d, w, h, slab), static () => new BackdropBlob()) with { Key = "blob:" + i }));
        return new BoxEl
        {
            // boundary: false when the caller already draws it inside a quarter-scale boundary of its own (the stage's
            // Backdrop rasters floor, art, Field and scrim together): a nested boundary would only add a layer.
            Width = w, Height = h, ClipToBounds = true, HitTestVisible = false, RepaintBoundary = boundary, RasterScale = boundary ? 0.25f : 1f,
            Opacity = opacity,
            Children = [Canvas.Create(w, h, kids)],
        };
    }
}

/// <summary>THE AMBIENT GATE. A looping drift (the stage Field's blobs, a face's blobs and clouds, the hero photo's Ken Burns
/// pan, Verse's cloud words, Spotlight's name) is decoration that follows the music: it moves only while something PLAYS.
/// Paused, its rows are PAUSED in place (<see cref="AnimEngine.SetPaused"/>: the pixel on screen and the loop's phase both
/// stand still, and the render thread asks for no frames, so a paused stage presents nothing); Play resumes each row from
/// exactly where it stood. Minimized / occluded is the engine's own compositor pause; reduced motion is each caller's REST keys.</summary>
internal static class AmbientMotion
{
    /// <summary>The four pose channels a drift rides (translate + scale).</summary>
    public static readonly AnimChannel[] Pose = [AnimChannel.TranslateX, AnimChannel.TranslateY, AnimChannel.ScaleX, AnimChannel.ScaleY];
    /// <summary>The two translate channels a pan-only drift rides.</summary>
    public static readonly AnimChannel[] Translate = [AnimChannel.TranslateX, AnimChannel.TranslateY];

    /// <summary>Pause this component's host-node rows on <paramref name="channels"/> while playback is paused. Call it AFTER
    /// the component's <c>UseKeyframes</c> (layout effects run in declaration order) with the SAME seed key: a re-seed clears
    /// a pause, so the gate re-applies whenever the seed moves. <paramref name="loops"/> false (rest keys): nothing to pause,
    /// and no subscription. Reads <c>Playback.IsPlaying</c>, so the caller re-renders on a play/pause edge.</summary>
    public static void UseAmbientPause(this RenderContext ctx, AnimChannel[] channels, DepKey seed, bool loops,
        [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        bool paused = loops && !Playback.IsPlaying.Value;
        ctx.UseLayoutEffect(() =>
        {
            if (ctx.Anim is not { } anim || ctx.HostNode.IsNull) return;
            foreach (var channel in channels) anim.SetPaused(ctx.HostNode, channel, paused);
        }, DepKey.From(seed.GetHashCode(), paused ? 1 : 0), __hf, __hl);
    }
}
