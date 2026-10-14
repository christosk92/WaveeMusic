// ── Shell/VisualizerClock.cs ───────────────────────────────────────────────────────────────────────────────────────
// the visualizer's CLOCK as a core model (themes plan C4, X13): the lease on the audio taps, the demand for the precomputed
// edges, the playhead, the palette fade and the moments, the scope pull, and the one batched write per tick into the slab
//
// Role: CORE
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4.5
//
// MOVED from the `Visualizer.Clock` component in `Visualizer.UI.cs`, whose fields and methods were never component state: the
// component keeps the hooks (the effects, the ticker mount) and calls this. Every preference the tick needs is a PEEK of a
// context signal (O8); the faces stay content renderers over the slab. Faces and the gallery are unchanged.

using System.Runtime.CompilerServices;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Visualizer
{
    /// <summary>THE CLOCK'S MODEL (themes plan C4, X13). Mounted ONCE by Stage.UI.cs's SurfaceCore (zero-size), inside its <c>Ctx.Provide(Stage.StageContext, …)</c>.
    /// Holds the lease the VISIBLE consumers need, demands the precomputed edges for the current track, owns the palette
    /// fade and the moments, and folds one <see cref="Model.Tick"/> per tick into the slab. Every preference it needs
    /// arrives as a SIGNAL on the context (SurfaceCore's one epoch effect writes them — O8): the tick Peeks, never reads
    /// the registry.</summary>
    public sealed class ClockCore
    {
        readonly Model _model = new();
        readonly float[] _db = new float[Visualizer.Bands.Count];
        readonly float[] _wave = new float[AudioEffects.WaveformSamples];
        readonly float[] _scopeLine = new float[Oscilloscope.Points], _scopeRadial = new float[Oscilloscope.RadialPoints];
        readonly float[] _tlLow = new float[Timeline.Points], _tlMid = new float[Timeline.Points], _tlHigh = new float[Timeline.Points];
        internal readonly Action _tick, _tickCore, _write, _onPosition, _demandEdges, _onTimeline, _onBasePalette;
        internal readonly Func<Action?> _lease;
        internal readonly Signal<bool> _settled = new(true);   // the deck's mirror (Deck.UI.cs's `_settled`): the gate reads a SIGNAL, never _model.IsSettled
        Stage.StageCtx? _ctx;
        InputHooks? _hooks;                           // captured in Render (V-U42), read by the lease effect
        Action<Action>? _batch;                       // the runtime's Batch, bound by the component (null headless: the write runs inline)
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

        public ClockCore()
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

        /// <summary>Bind the stage context, the input hooks (the occluded gate the lease reads) and the runtime's batcher. Called by
        /// the component's Render every time (cheap: three field writes).</summary>
        internal void Bind(Stage.StageCtx ctx, InputHooks hooks, Action<Action>? batch)
        {
            _ctx = ctx;
            _hooks = hooks;
            _batch = batch;
        }

        /// <summary>Is the per-frame tick running (the ticker is mounted)? Set by the component's Render.</summary>
        internal bool Run { set => _run = value; }

        /// <summary>The accent TARGET moved: seed the slab on the first run; afterwards capture the fade's start colour and time (V-U17).</summary>
        internal void OnAccentTarget(ColorF target)
        {
            var ctx = _ctx!;
            if (target.Equals(_accentTarget)) return;
            _accentTarget = target;
            if (_fadeStartMs < 0 || !_run) { ctx.Slab.Accent.Value = target; _fadeStartMs = 0; return; }   // mount, or the clock is not running: snap
            _fadeFrom = ctx.Slab.Accent.Peek();
            _fadeStartMs = FrameTime.NowMs;
        }

        /// <summary>The clock stopped mid-fade (settled / occluded / reduced): land both fades on their targets.</summary>
        internal void OnStopped()
        {
            var ctx = _ctx!;
            if (!ctx.Slab.Accent.Peek().Equals(_accentTarget)) ctx.Slab.Accent.Value = _accentTarget;
            _fadeStartMs = 0;
            if (_fade.Active || _fadeSignalsPending) LandPalette();
        }

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
                _pos.Anchor(FrameTime.NowMs, pos);
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
            _fade.Begin(from, target, FrameTime.NowMs, durationMs, moment);
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
            long now = FrameTime.NowMs;
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
            if (_batch is { } batch) batch(_write); else WriteCore();
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
            if (_settled.Peek() != settled) _settled.Value = settled;   // the gate's mirror (Deck.UI.cs's `_settled`)
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

}
