// ── Shell/Visualizer.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// Slab, SeriesSource, Palette, Clock (30 Hz, leases, demands), the eight faces (stage + preview scale), Gallery
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 1100 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §2.10, §3.1, §4.9
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE DECK PATTERN, ON THE STAGE. ONE `Clock` component ticks at 30 Hz (`UseInterval`, auto-paused when parked), pulls
// the engine's bands (`Playback.Audio.CopySpectrum`), folds them through the CORE `Visualizer.Model` and writes the
// `Slab` inside ONE `Runtime.Batch` — FloatSignals for scalars, `SeriesSource.Version` bumps for the six series. Every
// face is built ONCE and binds `Transform`/`Opacity`/`Fill` props over the slab (compositor-only writes, zero
// re-render); Horizon and Aurora bind `SeriesEl.Samples` over a `SeriesSource`. A preview is the SAME face at a smaller
// size with fewer parts, bound to the SAME slab — eight previews cost nodes, never analysis. Nothing here decides: the
// tick's inputs and every constant come from `Visualizer` (CORE) and `Stage` (CORE).

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Media;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Design.StageInk;

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

    /// <summary>Every signal a face can bind. One per stage; the gallery previews share it.</summary>
    public sealed class Slab
    {
        public readonly FloatSignal[] Bands = Make(Visualizer.Bands.Count), Peaks = Make(Visualizer.Bands.Count);
        public readonly FloatSignal Low = new(0f), Mid = new(0f), High = new(0f), Level = new(0f), Kick = new(0f), BeatPhase = new(0f);
        public readonly FloatSignal FaceFieldOp = new(0.75f), BaseFieldOp = new(Stage.Tone.BaseFieldA), GlowOp = new(0.16f), BeatScale = new(1f);
        public readonly FloatSignal ReelL = new(Visualizer.Tape.R1), ReelR = new(Visualizer.Tape.R0), AngleL = new(0f), AngleR = new(0f);
        public readonly FloatSignal[] Rings = Make(Pulse.Rings);
        public readonly Signal<int> BeatIndex = new(0), MeterLit = new(0);
        public readonly Signal<Source> Source = new(Visualizer.Source.Breath);
        public readonly SeriesSource HorizonLow = new(Horizon.Points), HorizonMid = new(Horizon.Points), HorizonHigh = new(Horizon.Points);
        public readonly SeriesSource AuroraLow = new(Aurora.Points), AuroraMid = new(Aurora.Points), AuroraHigh = new(Aurora.Points);
        /// <summary>The accent cross-fade's live value (Stage.Tone.CrossFadeMs) — the hairline, the ring strokes, the glow tint.</summary>
        public readonly Signal<ColorF> Accent = new(ColorF.FromRgba(255, 158, 196));
        /// <summary>Diagnostics (read by Screens/Diagnostics.UI.cs's card).</summary>
        public float LastFftMs; public long LastAlignFrames, LastSequence; public Tier LastTier;

        static FloatSignal[] Make(int n)
        {
            var a = new FloatSignal[n];
            for (int i = 0; i < n; i++) a[i] = new FloatSignal(0f);
            return a;
        }
    }

    /// <summary>The colours a face paints with, derived once per (cover, Tok.Epoch) in Stage.UI.cs and published on
    /// <see cref="Stage.StageCtx.Palette"/>: the accent, a complementary partner, and the four blob tints from the cover's
    /// scheme (<c>Wavee.Scheme</c>, Entities/Palette.cs:67-72 — ARGB uints, converted by <c>Design.Palette.ToColor</c>, Design.cs:677).
    /// The third blob is the ACCENT, not the scheme's <c>TextBrightAccent</c>: that role is pure white on every dark grading
    /// (Entities/Palette.cs:55-63, User.Cover.cs:879) and a white blob would wash the whole Field.</summary>
    public readonly record struct Palette(ColorF Accent, ColorF C1, ColorF C2, ColorF F1, ColorF F2, ColorF F3, ColorF F4)
    {
        public static Palette From(ColorF accent, Scheme? scheme)
        {
            var (h, s, v) = accent.ToHsv();
            ColorF c2 = ColorF.FromHsv((h + 150f) % 360f, MathF.Min(1f, s * 0.9f + 0.1f), MathF.Min(1f, v * 0.95f + 0.05f));
            if (scheme is { IsEmpty: false } sc)
                return new Palette(accent, accent, c2, Design.Palette.ToColor(sc.BackgroundBase), Design.Palette.ToColor(sc.BackgroundTintedBase),
                                   accent, Design.Palette.ToColor(sc.TextSubdued));
            ColorF dim = ColorF.FromHsv(h, s * 0.6f, v * 0.35f), deep = ColorF.FromHsv((h + 40f) % 360f, s * 0.7f, v * 0.25f);
            return new Palette(accent, accent, c2, dim, accent, c2, deep);
        }
    }

    // ══ 2. THE CLOCK ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Mounted ONCE by Stage.UI.cs's SurfaceCore (zero-size), inside its <c>Ctx.Provide(Stage.StageContext, …)</c>.
    /// Holds the lease the VISIBLE consumer needs, demands the precomputed edges for the current track, and folds one
    /// <see cref="Model.Tick"/> per 30 Hz tick into the slab. Every preference it needs arrives as a SIGNAL on the context
    /// (SurfaceCore's one epoch effect writes them — O8): the tick Peeks, never reads the registry.</summary>
    public sealed class Clock : Component
    {
        readonly Model _model = new();
        readonly float[] _db = new float[Bands.Count];
        readonly Action _tick, _tickCore, _write, _onPosition, _demandEdges;
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
        ColorF _accentTarget, _fadeFrom;
        long _fadeStartMs = -1;                       // −1 = seeded, nothing to fade

        public Clock()
        {
            _tickCore = TickCore;
            _tick = () => Reactive.Untrack(_tickCore);
            _write = WriteCore;
            _onPosition = OnPosition;
            _demandEdges = DemandEdges;
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
            UseInterval(_tick, TickMs, run);
            UseEffect(_lease);
            UseEffect(_onPosition);
            UseEffect(_demandEdges);
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
            // the clock stopped mid-fade (settled / occluded / reduced): land on the target
            UseEffect(() => { if (!run && !ctx.Slab.Accent.Peek().Equals(_accentTarget)) ctx.Slab.Accent.Value = _accentTarget; }, DepKey.From(run));
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        /// <summary>The ONE lease: the tier the VISIBLE consumer needs — the selected face in Visualizer mode, the base Field's
        /// breath otherwise (V-U55) — under the stage/playing/owner/supported/occluded/reduced gates (Visualizer.Demand.For).
        /// Spectrum implies level (the engine's AcquireSpectrum does that).</summary>
        Action? Lease()
        {
            var ctx = _ctx!;
            var tier = Demand.For(ctx.Kind.Value, ctx.Mode.Value == Stage.Mode.Visualizer, Shell.Ui.ImmersiveLyrics.Value, Playback.IsPlaying.Value,
                                  Playback.OwnerSignal.Value == Playback.Owner.Us, Playback.Audio.Supported.Value,
                                  _hooks?.WindowOccluded?.Value ?? false, Design.Reduced);
            ctx.Slab.LastTier = tier;
            Stage.Diagnostics.NoteLease(tier);
            if (tier == Tier.None) return null;
            IDisposable lease = tier == Tier.Spectrum ? Playback.Audio.AcquireSpectrum() : Playback.Audio.AcquireLevels();
            return lease.Dispose;
        }

        /// <summary>Re-anchor the playhead on every host report AND on every advancing edge (the deck's PositionInterpolator
        /// discipline, Deck.cs:269-276: without the edge a resume would extrapolate across the whole paused gap until the
        /// next ~1 Hz report).</summary>
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

        void TickCore()
        {
            var ctx = _ctx!;
            var slab = ctx.Slab;
            long now = Design.FrameTime.NowMs;
            if (!_anchored) { _pos.Anchor(now, Playback.PositionMs.Peek()); _anchored = true; }
            float dt = DeltaSec(_lastTickMs, now);
            _lastTickMs = now;

            int n = Playback.Audio.CopySpectrum(_db, out SpectrumInfo info);
            _haveLive = n == Bands.Count && info.Live && info.Sequence != 0;
            slab.LastFftMs = info.FftMs; slab.LastAlignFrames = info.AlignFrames; slab.LastSequence = info.Sequence;
            // the level: the analysed window's PRE-gain RMS under a spectrum lease, the (post-gain) level tap otherwise
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
            var input = new Input(Playback.IsPlaying.Peek(), ctx.Calm.Peek(), ctx.Sensitivity.Peek(), _haveLive, info.Muted, rms,
                                  pos, dur, haveBands, haveBeats, tempo, ctx.Mode.Peek() == Stage.Mode.Visualizer);
            _pending = _model.Tick(in input, _db, bands, beats, dt);
            if (Context.Runtime is { } rt) rt.Batch(_write); else WriteCore();
        }

        /// <summary>ONE batch → ONE frame request, however many signals moved. Peek-compare before every write.</summary>
        void WriteCore()
        {
            var s = _ctx!.Slab; var f = _pending; var m = _model;
            for (int i = 0; i < Bands.Count; i++) { Set(s.Bands[i], Q(m.Level[i], 1f / 128f)); Set(s.Peaks[i], Q(m.Peak[i], 1f / 128f)); }
            Set(s.Low, Q(f.Low, 1f / 256f)); Set(s.Mid, Q(f.Mid, 1f / 256f)); Set(s.High, Q(f.High, 1f / 256f)); Set(s.Level, Q(f.Level, 1f / 256f));
            Set(s.Kick, Q(f.Kick, 1f / 256f)); Set(s.BeatPhase, Q(f.BeatPhase, 1f / 256f));
            Set(s.FaceFieldOp, Q(f.FaceFieldOp, 1f / 256f)); Set(s.BaseFieldOp, Q(f.BaseFieldOp, 1f / 256f));
            Set(s.GlowOp, Q(f.GlowOp, 1f / 256f)); Set(s.BeatScale, Q(f.BeatScale, 1f / 4096f));
            Set(s.ReelL, Q(f.ReelL, 0.25f)); Set(s.ReelR, Q(f.ReelR, 0.25f)); Set(s.AngleL, Q(f.AngleL, 0.5f)); Set(s.AngleR, Q(f.AngleR, 0.5f));
            for (int r = 0; r < Pulse.Rings; r++) Set(s.Rings[r], Q(Pulse.RingProgress(f.BeatIndex, f.BeatPhase, r), 1f / 256f));
            if (s.BeatIndex.Peek() != f.BeatIndex) s.BeatIndex.Value = f.BeatIndex;
            if (s.MeterLit.Peek() != f.MeterLit) s.MeterLit.Value = f.MeterLit;
            if (s.Source.Peek() != f.Source) { s.Source.Value = f.Source; Stage.Diagnostics.NoteSource(f.Source); }
            Copy(m.HorizonLow, s.HorizonLow); Copy(m.HorizonMid, s.HorizonMid); Copy(m.HorizonHigh, s.HorizonHigh);
            Copy(m.AuroraLow, s.AuroraLow); Copy(m.AuroraMid, s.AuroraMid); Copy(m.AuroraHigh, s.AuroraHigh);
            // the accent cross-fade rides the same tick (a bound Fill snaps, §1.8): LINEAR from the captured start over
            // elapsed / Stage.Tone.CrossFadeMs, landing exactly on the target (V-U17)
            if (_fadeStartMs > 0)
            {
                float p = Stage.Tone.Progress(_lastTickMs - _fadeStartMs);
                var next = p >= 1f ? _accentTarget : ColorF.Lerp(_fadeFrom, _accentTarget, p);
                if (!next.Equals(s.Accent.Peek())) s.Accent.Value = next;
                if (p >= 1f) _fadeStartMs = 0;
            }
            bool settled = m.IsSettled;
            if (_settled.Peek() != settled) _settled.Value = settled;   // the gate's mirror (Deck.UI.cs:378-379)
        }

        static void Set(FloatSignal s, float v) { if (v != s.Peek()) s.Value = v; }
        static float Q(float v, float q) => MathF.Round(v / q) * q;
        static void Copy(float[] src, SeriesSource dst) { src.AsSpan().CopyTo(dst.Buffer); dst.Count = src.Length; dst.Publish(); }
    }

    // ══ 3. THE FACES ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The box a face fills and whether it is a gallery preview (fewer parts, no cover).</summary>
    public readonly record struct FaceSpec(float W, float H, bool Preview, string? CoverUrl);

    /// <summary>Build a face ONCE for a slab; everything that moves is a bound prop over it. The stage remounts per kind
    /// (a keyed CHILD box, <c>Key = "viz:" + kind</c>), the gallery mounts eight at preview scale.</summary>
    public static Element Face(Kind kind, Slab slab, in Palette pal, in FaceSpec spec) => kind switch
    {
        Kind.Field => FieldFace(slab, pal, spec),
        Kind.Halo => HaloFace(slab, pal, spec),
        Kind.Horizon => HorizonFace(slab, pal, spec),
        Kind.Matrix => MatrixFace(slab, pal, spec),
        Kind.Aurora => AuroraFace(slab, pal, spec),
        Kind.Spectrum => SpectrumFace(slab, pal, spec),
        Kind.Pulse => PulseFace(slab, pal, spec),
        _ => TapeFace(slab, pal, spec),
    };

    static float Min(in FaceSpec s) => MathF.Min(s.W, s.H);
    static float Max(in FaceSpec s) => MathF.Max(s.W, s.H);

    // ── Field: four blobs on 28–36 s keyframes, breathing opacity, ONE bound channel ──────────────────────────────────

    /// <summary>Also the stage's always-on backdrop layer (Stage.UI.cs Backdrop): <paramref name="opacity"/> is the slab's
    /// Face/BaseFieldOp as a Prop.</summary>
    public static Element FieldFace(Slab slab, in Palette pal, in FaceSpec spec, Prop<float>? opacity = null)
    {
        float d = 1.1f * Max(in spec);
        var kids = new List<CanvasChild>(Field.Blobs);
        ColorF[] tints = [pal.F1, pal.F2, pal.F3, pal.F4];
        (float X, float Y)[] corners = [(-0.55f, -0.62f), (1f - 0.55f, -0.62f), (-0.55f, 1f - 0.66f), (1f - 0.58f, 1f - 0.64f)];
        for (int i = 0; i < Field.Blobs; i++)
        {
            float x = corners[i].X * Max(in spec), y = corners[i].Y * Max(in spec);
            // RE-PUSHED props (component-props contract): a new palette recolours the mounted blob; the keyframes keep running.
            kids.Add(new CanvasChild(x, y, Embed.Comp(new Blob.Props(i, d, tints[i], spec.W, spec.H), static () => new Blob()) with { Key = "blob:" + i }));
        }
        return new BoxEl
        {
            Width = spec.W, Height = spec.H, ClipToBounds = true, HitTestVisible = false,
            Opacity = opacity ?? (Prop<float>)slab.FaceFieldOp,
            Children = [Canvas.Create(spec.W, spec.H, kids)],
        };
    }

    /// <summary>One blob: a radial-gradient disc on looping ping-pong keyframes (translate + scale) — zero ticks. The
    /// keyframes target this component's own root (the hooks' HostNode contract), which is why each blob is a component.
    /// Under reduced motion the loop is replaced by REST keys (the engine does not snap looping keyframes — §1.8, V-E10).</summary>
    sealed class Blob : Component
    {
        public sealed record Props(int Index, float Diameter, ColorF Tint, float W, float H);
        static readonly Keyframe[] s_restTranslate = [new Keyframe(0f, 0f), new Keyframe(1f, 0f)];
        static readonly Keyframe[] s_restScale = [new Keyframe(0f, 1f), new Keyframe(1f, 1f)];
        public override Element Render()
        {
            var p = UseProps<Props>();
            var (dx, dy, sc) = Field.Drift[p.Index];
            float ms = Field.PeriodSec[p.Index] * 1000f;
            bool reduced = Design.Reduced;
            var key = DepKey.From(p.Index, (int)p.W, (int)p.H, reduced ? 1 : 0);   // the 4-int form — there is no 3-int DepKey (V-U3)
            UseKeyframes(AnimChannel.TranslateX, reduced ? s_restTranslate : [new Keyframe(0f, 0f), new Keyframe(0.5f, dx * p.W, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.TranslateY, reduced ? s_restTranslate : [new Keyframe(0f, 0f), new Keyframe(0.5f, dy * p.H, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.ScaleX, reduced ? s_restScale : [new Keyframe(0f, 1f), new Keyframe(0.5f, sc, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.ScaleY, reduced ? s_restScale : [new Keyframe(0f, 1f), new Keyframe(0.5f, sc, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)], ms, loop: !reduced, key);
            return new BoxEl
            {
                Width = p.Diameter, Height = p.Diameter, Corners = Radii.Circle(p.Diameter), HitTestVisible = false,
                Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, p.Tint), new GradientStop(0.55f, p.Tint with { A = 0.45f }), new GradientStop(1f, p.Tint with { A = 0f })]),
            };
        }
    }

    // ── Halo: 72 (36 preview) bars around the cover, the glow on the bass, the cover on the kick ───────────────────────

    static Element HaloFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float m = Min(in spec);
        float cx = spec.W * 0.5f, cy = spec.H * 0.46f;
        float r = 0.17f * m, barW = MathF.Max(1f, 0.0076f * m), barH = 0.11f * m, cover = 0.22f * m;
        int bars = spec.Preview ? Halo.Bars / 2 : Halo.Bars;
        var kids = new List<CanvasChild>(bars + 2)
        {
            new CanvasChild(cx - 0.45f * m, cy - 0.45f * m, new BoxEl
            {
                Width = 0.9f * m, Height = 0.9f * m, Corners = Radii.Circle(0.9f * m), HitTestVisible = false,
                Opacity = slab.GlowOp,
                Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, pal.C1), new GradientStop(1f, pal.C1 with { A = 0f })]),
            }),
        };
        for (int j = 0; j < bars; j++)
        {
            int src = spec.Preview ? j * 2 : j;
            var band = slab.Bands[Halo.BandOf(src)];
            ColorF c = ColorF.Lerp(pal.C2, pal.C1, Halo.MixOf(src));
            // wrapper: bottom at the centre, rotated about it; inner bar: the top barH of the wrapper, scaled about its own bottom
            kids.Add(new CanvasChild(cx - barW * 0.5f, cy - (r + barH), new BoxEl
            {
                Width = barW, Height = r + barH, Direction = 1, HitTestVisible = false,
                Rotation = Halo.AngleDeg(src), TransformOriginX = 0.5f, TransformOriginY = 1f,
                Children =
                [
                    new BoxEl
                    {
                        Width = barW, Height = barH, Shrink = 0f, Corners = Radii.Circle(barW), Fill = c,
                        TransformOriginX = 0.5f, TransformOriginY = 1f,
                        Transform = Prop.Of(() => Affine2D.Scale(1f, Halo.Scale(band.Value))),
                    },
                ],
            }));
        }
        if (!spec.Preview)
            kids.Add(new CanvasChild(cx - cover * 0.5f, cy - cover * 0.5f, new BoxEl
            {
                Width = cover, Height = cover, Corners = CornerRadius4.All(0.064f * m), ClipToBounds = true, Shadow = Elevation.Dialog,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Transform = Prop.Of(() => Affine2D.Scale(slab.BeatScale.Value, slab.BeatScale.Value)),
                Children = [Controls.Artwork(spec.CoverUrl, cover, cover, 0.064f * m, decodePx: 512)],
            }));
        return FaceFrame(spec, kids);
    }

    // ── Horizon: three mirrored SeriesEls over the precomputed bands, a past veil, the playhead ───────────────────────

    static Element HorizonFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float top = 0.28f * spec.H, h = 0.44f * spec.H;
        var lo = slab.HorizonLow; var mi = slab.HorizonMid; var hi = slab.HorizonHigh;
        return FaceFrame(spec,
        [
            new CanvasChild(0f, top, new BoxEl
            {
                Width = spec.W, Height = h, ZStack = true, HitTestVisible = false,
                Children =
                [
                    // Mirrored amplitude is measured against HALF the height (§4.3.6): 0.92 ⇒ a sample of 1.0 reaches 92 % of the way to the edge
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Mirrored, Amplitude = Horizon.Amplitude, Color = pal.C2, Samples = Prop.Of(() => lo.Current) },
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Mirrored, Amplitude = Horizon.Amplitude, Color = pal.C1, Samples = Prop.Of(() => mi.Current) },
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Mirrored, Amplitude = Horizon.Amplitude, Color = ColorF.Lerp(pal.C1, ColorF.FromRgba(255, 255, 255), 0.75f), Samples = Prop.Of(() => hi.Current) },
                    // the past veil: the LEFT half (horizontal = JustifySelf), full height (vertical = AlignSelf) — V-U9
                    new BoxEl { Width = spec.W * 0.5f, JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Stretch, Gradient = GradientRight(new GradientStop(0f, ColorF.FromRgba(14, 12, 14, 204)), new GradientStop(1f, ColorF.FromRgba(14, 12, 14, 77))) },
                ],
            }),
            new CanvasChild(spec.W * 0.5f - 1f, top - 0.03f * spec.H, new BoxEl { Width = 2f, Height = h + 0.06f * spec.H, Corners = Radii.Circle(2f), Fill = ColorF.FromRgba(255, 255, 255), HitTestVisible = false }),
        ]);
    }

    // ── Matrix: 32 (16 preview) columns × 12 rows — THREE nodes per column (dim track, lit bar, peak dot) + the static row
    //    carvers that cut every bar into "dots" (V-U35: a dot per cell was ~1,700 nodes; this is 107: 96 + 11 carvers) ───

    static Element MatrixFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        int cols = spec.Preview ? Matrix.Columns / 2 : Matrix.Columns;
        float left = 0.14f * spec.W, top = 0.20f * spec.H, w = spec.W - 2f * left, h = spec.H - top - 0.28f * spec.H;
        float cw = w / cols, rh = h / Matrix.Rows, bar = MathF.Max(2f, cw * 0.44f), carve = MathF.Max(1f, rh * 0.42f);
        var kids = new List<CanvasChild>(cols * 3 + Matrix.Rows);
        for (int c = 0; c < cols; c++)
        {
            int src = spec.Preview ? c * 2 : c;
            var band = slab.Bands[Matrix.BandOf(src)]; var peak = slab.Peaks[Matrix.BandOf(src)];
            float x = left + c * cw + (cw - bar) * 0.5f;
            // the dim track: the whole column at 16 %
            kids.Add(new CanvasChild(x, top, new BoxEl { Width = bar, Height = h, Corners = Radii.Circle(bar), Fill = ColorF.FromRgba(255, 255, 255, 41), HitTestVisible = false }));
            // the lit bar: scaled from the BOTTOM to LitRows/Rows — quantised to whole rows so it steps like a dot column
            kids.Add(new CanvasChild(x, top, new BoxEl
            {
                Width = bar, Height = h, Corners = Radii.Circle(bar), Fill = ColorF.FromRgba(255, 255, 255), HitTestVisible = false,
                TransformOriginX = 0.5f, TransformOriginY = 1f,
                Transform = Prop.Of(() => Affine2D.Scale(1f, Matrix.LitRows(band.Value) / (float)Matrix.Rows)),
            }));
            // the peak dot: one accent cell, translated to its row. The visible cell of row k sits carve/2 below the row's top
            // (the carvers straddle the row boundaries), so the dot is offset by carve/2 to land exactly on the lit cells.
            kids.Add(new CanvasChild(x, top, new BoxEl
            {
                Width = bar, Height = rh - carve, Corners = Radii.Circle(bar), Fill = Prop.Bind(slab.Accent), HitTestVisible = false,
                Transform = Prop.Of(() => Affine2D.Translation(0f, (Matrix.Rows - Matrix.PeakRow(peak.Value)) * rh + carve * 0.5f)),
            }));
        }
        // the row carvers: static strips of the stage floor between rows, ABOVE the columns — one per interior row boundary
        for (int rr = 1; rr < Matrix.Rows; rr++)
            kids.Add(new CanvasChild(left - bar, top + rr * rh - carve * 0.5f, new BoxEl { Width = w + 2f * bar, Height = carve, Fill = Ink.Floor, HitTestVisible = false }));
        return FaceFrame(spec, kids);
    }

    // ── Aurora: three baseline SeriesEls, screen-ish by alpha ──────────────────────────────────────────────────────────

    static Element AuroraFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float h = 0.62f * spec.H;
        var lo = slab.AuroraLow; var mi = slab.AuroraMid; var hi = slab.AuroraHigh;
        return FaceFrame(spec,
        [
            new CanvasChild(0f, spec.H - h, new BoxEl
            {
                Width = spec.W, Height = h, ZStack = true, HitTestVisible = false,
                Children =
                [
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Baseline, Color = pal.C2 with { A = 0.80f }, Samples = Prop.Of(() => lo.Current) },
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Baseline, Color = pal.C1 with { A = 0.60f }, Samples = Prop.Of(() => mi.Current) },
                    new SeriesEl { Width = spec.W, Height = h, Shape = SeriesShape.Baseline, Color = pal.Accent with { A = 0.50f }, Samples = Prop.Of(() => hi.Current) },
                ],
            }),
        ]);
    }

    // ── Spectrum: 48 (24 preview) bars + caps + a dimmer reflection ───────────────────────────────────────────────────

    static Element SpectrumFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        int bars = spec.Preview ? Spectrum.Bars / 2 : Spectrum.Bars;
        float left = 0.09f * spec.W, w = spec.W - 2f * left, gap = 0.0045f * spec.W, bw = (w - gap * (bars - 1)) / bars;
        float h = 0.42f * spec.H, bottom = 0.30f * spec.H, top = spec.H - bottom - h;
        var grad = new GradientSpec(GradientShape.Linear, 90f, [new GradientStop(0f, ColorF.FromRgba(255, 255, 255)), new GradientStop(0.3f, pal.C1), new GradientStop(1f, pal.C2)]);
        var kids = new List<CanvasChild>(bars * 3);
        for (int i = 0; i < bars; i++)
        {
            int src = spec.Preview ? i * 2 : i;
            var band = slab.Bands[src]; var peak = slab.Peaks[src];
            float x = left + i * (bw + gap);
            kids.Add(new CanvasChild(x, top, new BoxEl { Width = bw, Height = h, Gradient = grad, Corners = new CornerRadius4(bw * 0.35f, bw * 0.35f, bw * 0.1f, bw * 0.1f), TransformOriginX = 0.5f, TransformOriginY = 1f, HitTestVisible = false,
                                                        Transform = Prop.Of(() => Affine2D.Scale(1f, Spectrum.Scale(band.Value))) }));
            kids.Add(new CanvasChild(x, top, new BoxEl { Width = bw, Height = MathF.Max(2f, 0.005f * spec.H), Corners = Radii.Circle(2f), Fill = ColorF.FromRgba(255, 255, 255), HitTestVisible = false,
                                                        Transform = Prop.Of(() => Affine2D.Translation(0f, h * (1f - peak.Value) - 2f)) }));
            if (!spec.Preview)
                kids.Add(new CanvasChild(x, top + h + 0.006f * spec.H, new BoxEl { Width = bw, Height = h, Gradient = grad, Opacity = 0.22f, TransformOriginX = 0.5f, TransformOriginY = 0f, HitTestVisible = false,
                                                                                 Transform = Prop.Of(() => Affine2D.Scale(1f, 0.45f * Spectrum.Scale(band.Value))) }));
        }
        return FaceFrame(spec, kids);
    }

    // ── Pulse: four accent rings a beat apart, the glow, the cover on the kick ────────────────────────────────────────

    static Element PulseFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float m = Min(in spec), cx = spec.W * 0.5f, cy = spec.H * 0.46f, cover = 0.26f * m, ring = MathF.Max(1f, 0.003f * m);
        var kids = new List<CanvasChild>(Pulse.Rings + 2)
        {
            new CanvasChild(cx - 0.45f * m, cy - 0.45f * m, new BoxEl
            {
                Width = 0.9f * m, Height = 0.9f * m, Corners = Radii.Circle(0.9f * m), HitTestVisible = false, Opacity = Prop.Of(() => Pulse.GlowOpacity(slab.Low.Value)),
                Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, pal.C1), new GradientStop(1f, pal.C1 with { A = 0f })]),
            }),
        };
        for (int r = 0; r < Pulse.Rings; r++)
        {
            var p = slab.Rings[r];
            kids.Add(new CanvasChild(cx - cover * 0.5f, cy - cover * 0.5f, new BoxEl
            {
                Width = cover, Height = cover, Corners = CornerRadius4.All(0.077f * m), BorderWidth = ring, BorderColor = Prop.Bind(slab.Accent), HitTestVisible = false,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Opacity = Prop.Of(() => Pulse.RingOpacity(p.Value)),
                Transform = Prop.Of(() => Affine2D.Scale(Pulse.RingScale(p.Value), Pulse.RingScale(p.Value))),
            }));
        }
        if (!spec.Preview)
            kids.Add(new CanvasChild(cx - cover * 0.5f, cy - cover * 0.5f, new BoxEl
            {
                Width = cover, Height = cover, Corners = CornerRadius4.All(0.077f * m), ClipToBounds = true, Shadow = Elevation.Dialog,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f, Transform = Prop.Of(() => Affine2D.Scale(slab.BeatScale.Value, slab.BeatScale.Value)),
                Children = [Controls.Artwork(spec.CoverUrl, cover, cover, 0.077f * m, decodePx: 512)],
            }));
        return FaceFrame(spec, kids);
    }

    // ── Tape: two area-conserving packs, two hubs, the tape, a 20-cell level meter ───────────────────────────────────────

    static Element TapeFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        // the prototype's 960×540 board, scaled to fit (xMidYMid meet)
        float k = MathF.Min(spec.W / 960f, spec.H / 540f), ox = (spec.W - 960f * k) * 0.5f, oy = (spec.H - 540f * k) * 0.5f;
        float flange = Tape.Flange * k, hub = Tape.HubR * k, pack = Tape.R1 * k;
        var kids = new List<CanvasChild>(8 + Tape.MeterCells)
        {
            Reel(ox + 300f * k, oy + 250f * k, flange, pack, hub, slab.ReelL, slab.AngleL, pal.Accent),
            Reel(ox + 660f * k, oy + 250f * k, flange, pack, hub, slab.ReelR, slab.AngleR, pal.C2),
            new CanvasChild(ox + 268f * k, oy + 250f * k, new PolylineStrokeEl
            {
                Width = 424f * k, Height = 184f * k, P0 = new Point2(0f, 95f * k), P1 = new Point2(202f * k, 180f * k), P2 = new Point2(222f * k, 180f * k), P3 = new Point2(424f * k, 95f * k),
                PointCount = 4, Color = ColorF.FromRgba(0x7a, 0x66, 0x56), Thickness = 3f * k, RoundCaps = false,
            }),
            new CanvasChild(ox + 468f * k, oy + 424f * k, new BoxEl { Width = 24f * k, Height = 12f * k, Corners = CornerRadius4.All(2f * k), Fill = ColorF.FromRgba(255, 255, 255), HitTestVisible = false }),
        };
        if (!spec.Preview)
        {
            float mLeft = 0.14f * spec.W, mW = spec.W - 2f * mLeft, mGap = 0.005f * spec.W, cell = (mW - mGap * (Tape.MeterCells - 1)) / Tape.MeterCells, mH = MathF.Max(3f, 0.022f * spec.H);
            ColorF[] zone = [pal.C2, pal.Accent, pal.C1];
            for (int i = 0; i < Tape.MeterCells; i++)
            {
                int cellIndex = i; ColorF on = zone[Tape.Zone(i)];
                kids.Add(new CanvasChild(mLeft + i * (cell + mGap), spec.H - 0.25f * spec.H, new BoxEl
                {
                    Width = cell, Height = mH, Corners = Radii.Circle(2f), HitTestVisible = false,
                    Fill = Prop.Of(() => slab.MeterLit.Value > cellIndex ? on : ColorF.FromRgba(255, 255, 255, 26)),
                }));
            }
        }
        return FaceFrame(spec, kids);
    }

    /// <summary>One reel: flange ring, the pack (radius bound as a scale of its max), the hub with three spokes (bound rotation).</summary>
    static CanvasChild Reel(float cx, float cy, float flange, float pack, float hub, FloatSignal radius, FloatSignal angleDeg, ColorF accent)
    {
        float scaleK = 1f / Tape.R1;
        return new CanvasChild(cx - flange, cy - flange, new BoxEl
        {
            Width = 2f * flange, Height = 2f * flange, ZStack = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Children =
            [
                new BoxEl { Width = 2f * flange, Height = 2f * flange, Corners = Radii.Circle(2f * flange), BorderWidth = 2f, BorderColor = ColorF.FromRgba(255, 255, 255, 217) },
                new BoxEl
                {
                    Width = 2f * pack, Height = 2f * pack, Corners = Radii.Circle(2f * pack), Fill = ColorF.FromRgba(0x2b, 0x26, 0x21), BorderWidth = 1f, BorderColor = ColorF.FromRgba(255, 255, 255, 31),
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => Affine2D.Scale(radius.Value * scaleK, radius.Value * scaleK)),
                },
                new BoxEl
                {
                    Width = 2f * hub, Height = 2f * hub, ZStack = true, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => Affine2D.Rotation(angleDeg.Value * (MathF.PI / 180f))),
                    Children =
                    [
                        new BoxEl { Width = 2f * hub, Height = 2f * hub, Corners = Radii.Circle(2f * hub), Fill = ColorF.FromRgba(0x11, 0x11, 0x11), BorderWidth = 3f, BorderColor = accent },
                        Spoke(hub, accent, 0f), Spoke(hub, accent, 120f), Spoke(hub, accent, 240f),
                    ],
                },
            ],
        });
    }

    /// <summary>A spoke: horizontally centred (JustifySelf) at the TOP of the hub box (AlignSelf), rotated about the hub centre — V-U9.
    /// The spoke is 0.7·hub tall with a 0.3·hub top margin, so its BOTTOM edge is the hub centre; the transform origin is
    /// the box's own bottom (Y = 1) — margins are not part of the box the origin is normalised against.</summary>
    static BoxEl Spoke(float hub, ColorF c, float deg) => new()
    {
        Width = 5f, Height = hub * 0.7f, Corners = Radii.Circle(5f), Fill = c, JustifySelf = FlexAlign.Center, AlignSelf = FlexAlign.Start,
        Rotation = deg, TransformOriginX = 0.5f, TransformOriginY = 1f,   // rotate about the hub centre
        Margin = new Edges4(0f, hub * 0.3f, 0f, 0f),
    };

    // ── the frame every face shares ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The clipped box a face fills; <c>Canvas.Create</c> takes the children list directly (IReadOnlyList&lt;CanvasChild&gt;,
    /// Canvas.cs:16) — no wrapper record (V-U2). Named FaceFrame: <see cref="Frame"/> is the model's output record.</summary>
    static Element FaceFrame(in FaceSpec spec, List<CanvasChild> kids) => new BoxEl
    {
        Width = spec.W, Height = spec.H, ClipToBounds = true, HitTestVisible = false,
        Children = [Canvas.Create(spec.W, spec.H, kids)],
    };

    // ══ 4. THE GALLERY ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The acrylic pane: title row, then a SCROLLER holding the 2-column gallery of live previews and the four
    /// settings rows (the grid + settings run to ≈ 856 DIP at 1080p against a 856-DIP pane and overflow at 900p — V-U21).
    /// Mounted by Stage.UI.cs's GalleryHost. Every preference is a SIGNAL on the context (O8): the tiles' selection border
    /// and check disc bind <c>ctx.Kind</c>; the slider and toggle controls take the context's own signals (seeded ONCE by
    /// SurfaceCore's epoch effect and written by the controls while dragging — a write round-trips through Prefs.Stage →
    /// Epoch → the same effect, which <c>SetIfChanged</c>s the clamped value back). <c>ItemsView.Create</c> freezes its
    /// template at mount (ItemsView.cs:611-617), so the grid is KEYED by the palette + accent so a cover change remounts
    /// the eight tiles with the new colours (V-U20).</summary>
    public static Element Gallery(Stage.StageCtx ctx, Stage.Layout layout, Action close)
    {
        var slab = ctx.Slab;
        var p = ctx.Palette.Value;
        var accent = ctx.Accent.Value;
        var kindSig = ctx.Kind;
        // the tile's own height: padding 6 + preview + gap 6 + the two caption lines (20 + 16) + padding 8 (the grid cell is fixed)
        float tileW = (layout.GalleryW - 40f - 10f) * 0.5f, previewH = 78f, tileH = previewH + 56f;
        // frozen at mount BY DESIGN (the grid is keyed by palette + accent, so a new accent remounts the tiles): plain colour
        // locals, so the border thunk reads only the kind signal inside it
        ColorF pickBorder = accent.Fill, restBorder = ColorF.FromRgba(255, 255, 255, 15);
        Element tile(int i)
        {
            var kind = (Kind)i;
            return new BoxEl
            {
                Direction = 1, Gap = 6f, Padding = new Edges4(6f, 6f, 6f, 8f), Corners = CornerRadius4.All(6f), Fill = ColorF.FromRgba(255, 255, 255, 10),
                HoverFill = ColorF.FromRgba(255, 255, 255, 19), BrushTransitionMs = Design.Motion.Fast, Cursor = CursorId.Hand,
                BorderWidth = 2f, BorderColor = Prop.Of(() => kindSig.Value == kind ? pickBorder : restBorder),
                Children =
                [
                    new BoxEl
                    {
                        Width = tileW - 12f, Height = previewH, Corners = Radii.ControlAll, ClipToBounds = true, ZStack = true,
                        Gradient = new GradientSpec(GradientShape.Linear, 135f, [new GradientStop(0f, p.F1), new GradientStop(1f, p.F4)]),
                        Children =
                        [
                            new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = ColorF.FromRgba(14, 12, 14, 115) },
                            Face(kind, slab, in p, new FaceSpec(tileW - 12f, previewH, Preview: true, CoverUrl: null)),
                            // the check disc: TOP (AlignSelf) RIGHT (JustifySelf) — V-U9
                            new BoxEl
                            {
                                Width = 20f, Height = 20f, Corners = Radii.Circle(20f), Fill = accent.Fill, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 8f, 8f, 0f),
                                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Visible = Prop.Of(() => kindSig.Value == kind),
                                Children = [new TextEl(Icons.Check) { Size = 12f, FontFamily = Theme.IconFont, Color = accent.Ink }],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 1, Padding = new Edges4(4f, 0f, 4f, 0f),
                        Children =
                        [
                            new TextEl(Loc.Get(NameKey(kind))) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink },
                            new TextEl(Prop.Of(() => SubtitleOf(kind, kindSig.Value, slab.Source.Value))) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            };
        }
        var sliderStyle = Slider.DefaultStyle with { ValueFill = accent.Fill, ValueFillPointerOver = accent.FillSecondary, ValueFillPressed = accent.FillTertiary, ThumbFill = accent.Fill, ThumbFillPointerOver = accent.FillSecondary, ThumbFillPressed = accent.FillTertiary };   // rail + thumb ring stay the theme's (the stage follows the app theme)
        var toggleStyle = ToggleSwitch.DefaultStyle with { OnFill = accent.Fill, OnHover = accent.FillSecondary, OnPressed = accent.FillTertiary, OnKnob = accent.Ink, MinWidth = 40f, OffBorder = ColorF.FromRgba(255, 255, 255, 153), OffKnob = ColorF.FromRgba(255, 255, 255, 204) };
        return new BoxEl
        {
            Width = layout.GalleryW, Height = layout.GalleryH, Direction = 1, Gap = 10f, Padding = new Edges4(20f, 14f, 20f, 20f), Corners = Radii.CardAll,
            Acrylic = AcrylicSpec.InAppBase, Shadow = Elevation.Flyout, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Shrink = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Stage.GalleryTitle)) { Size = 20f, LineHeight = 28f, Weight = 600, FontFamily = "Segoe UI Variable Display", Color = Ink.Ink },
                        ToolTip.Wrap(IconButton.Create(Icons.Cancel, close, size: ControlSize.Small), Loc.Get(Strings.Stage.ClosePanel)),
                    ],
                },
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stagegallery",
                    Content = new BoxEl
                    {
                        Direction = 1, Gap = 10f,
                        Children =
                        [
                            ItemsView.Create(Catalog.Count, tile, RepeatLayout.Grid(2, tileH, 10f), new ListOptions
                            {
                                SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None, IsItemInvokedEnabled = true,
                                OnInvoked = static i => { Prefs.Stage.SetVisualizer(i); Stage.Diagnostics.NotePick((Kind)i); },
                                KeyOf = static i => "viz:" + i, Grow = 0f,
                            }) with { Key = "gallery:grid:" + HashCode.Combine(p, accent.Fill) },   // a new palette REMOUNTS the frozen template
                            new TextEl(Loc.Get(Strings.Stage.SettingsHeader)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink, Margin = new Edges4(0f, 6f, 0f, 0f) },
                            SettingRow(Icons.Audio, Loc.Get(Strings.Stage.Sensitivity),
                                Slider.Create(ctx.Sensitivity, static v => Prefs.Stage.SetSensitivity(v), SensitivityOptions, length: 150f, thickness: 24f, style: sliderStyle)),
                            SettingRow(Icons.Document, Loc.Get(Strings.Stage.LyricsOverlay),
                                ToggleSwitch.Create(ctx.LyricsOverlay, static on => Prefs.Stage.SetLyricsOverlay(on), style: toggleStyle)),
                            SettingRow(Icons.RefineSparkle, Loc.Get(Strings.Stage.ReduceMotion),
                                ToggleSwitch.Create(ctx.Calm, static on => Prefs.Stage.SetCalm(on), style: toggleStyle)),
                            SettingRow(Icons.Clock, Loc.Get(Strings.Stage.SyncOffset),
                                Slider.Create(ctx.SyncOffsetMs, static v => { int ms = (int)MathF.Round(v / 10f) * 10; Prefs.Stage.SetSyncOffsetMs(ms); Playback.Audio.SetSpectrumOffsetMs(ms); }, OffsetOptions, length: 150f, thickness: 24f, style: sliderStyle)),
                        ],
                    },
                },
            ],
        };
    }

    static readonly Slider.SliderOptions SensitivityOptions = new() { Min = Bands.MinSensitivity, Max = Bands.MaxSensitivity, Step = 0.05f, IsThumbToolTipEnabled = true, ThumbToolTipValueConverter = static v => ((int)MathF.Round(v * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" };
    static readonly Slider.SliderOptions OffsetOptions = new() { Min = -500f, Max = 500f, Step = 10f, IsThumbToolTipEnabled = true, ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms" };

    static Element SettingRow(string glyph, string label, Element control) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Gap = Spacing.M, MinHeight = 48f,
        Padding = new Edges4(16f, 0f, 12f, 0f), Corners = Radii.ControlAll, Fill = ColorF.FromRgba(255, 255, 255, 13), BorderWidth = 1f, BorderColor = ColorF.FromRgba(255, 255, 255, 15),
        Children =
        [
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary }, new TextEl(label) { Size = 14f, LineHeight = 20f, Color = Ink.Ink }] },
            control,
        ],
    };

    public static string NameKey(Kind k) => k switch
    {
        Kind.Field => Strings.Stage.Viz.Field, Kind.Halo => Strings.Stage.Viz.Halo, Kind.Horizon => Strings.Stage.Viz.Horizon, Kind.Matrix => Strings.Stage.Viz.Matrix,
        Kind.Aurora => Strings.Stage.Viz.Aurora, Kind.Spectrum => Strings.Stage.Viz.Spectrum, Kind.Pulse => Strings.Stage.Viz.Pulse, _ => Strings.Stage.Viz.Tape,
    };
    static string SubKey(Kind k) => k switch
    {
        Kind.Field => Strings.Stage.VizSub.Field, Kind.Halo => Strings.Stage.VizSub.Halo, Kind.Horizon => Strings.Stage.VizSub.Horizon, Kind.Matrix => Strings.Stage.VizSub.Matrix,
        Kind.Aurora => Strings.Stage.VizSub.Aurora, Kind.Spectrum => Strings.Stage.VizSub.Spectrum, Kind.Pulse => Strings.Stage.VizSub.Pulse, _ => Strings.Stage.VizSub.Tape,
    };
    /// <summary>The tile's second line: the face's blurb, or — while it is the active kind falling back — where its motion comes from.
    /// <paramref name="current"/> is the context's kind SIGNAL value (never a registry read — O8).</summary>
    static string SubtitleOf(Kind k, Kind current, Source live)
    {
        if (current != k || live == Source.Live) return Loc.Get(SubKey(k));
        return Loc.Get(live switch { Source.Precomputed => Strings.Stage.Source.Precomputed, Source.TempoGrid => Strings.Stage.Source.Tempo, _ => Strings.Stage.Source.Breath });
    }
}
