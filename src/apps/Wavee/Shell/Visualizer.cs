// ── Shell/Visualizer.cs ────────────────────────────────────────────────────────────────────────────────────────────
// Visualizer.Kind/Need/Tier/Source, Catalog, Demand, Bands, the eight face models (Field, Halo, Horizon, Matrix, Aurora,
// Spectrum, Pulse, Tape) and the ONE alloc-free Model.Tick the clock folds every 30 Hz tick through
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 520 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §2.5-§2.10, §4.8
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT A FACE IS MADE OF. The engine publishes 48 log bands in dB (SpectrumAnalyzer) ~60×/s; the UI PULLS them at
// 30 Hz into the clock's 48-float dB buffer and this file turns them into every scalar a face binds: `Bands.Unit` (dB → 0..1 with the
// sensitivity and the calm gain), `Bands.Follow` (the prototype's 0.55/0.10 attack/release), `Bands.Peaks` (hold 10
// ticks, fall 0.018), the low/mid/high averages, the Halo/Matrix/Spectrum index maps, the Aurora ribbons and the
// Horizon window written into preallocated series buffers, the Pulse beat phase (TrackBeats → tempo grid), the Tape's
// area-conserving reels, the Field's breath. Every constant is the prototype's (Flagship.dc.html:472-523, 568-588;
// Visualizers.dc.html:235-320) and is named here ONCE.
//
// NOTHING IS FAKED. Without a live spectrum the band arrays DECAY to the floor (Halo/Matrix/Spectrum rest); the
// Horizon reads the precomputed kind-237 bands; Pulse reads the beat grid or the tempo grid; Field and Tape breathe
// on the precomputed level when there is one and rest otherwise. `Source` says which, every tick, for the gallery
// caption and the diagnostics card.
//
// ORDER OF THE BAND PIPELINE (§2.8, V-U32): dB → `Unit` (RAW, floor/ceiling window) → AGC (a slow running maximum of
// the frame's max, so a quiet master and a loud one fill the same range) → the USER's gain (sensitivity × calm) →
// follower → peak hold. The user's gain is applied to an already-normalised frame; applying it before the AGC would let
// the AGC cancel the slider.
//
// Rules: `System`-only; the model owns its arrays (allocated once in the constructor) and `Tick` allocates nothing
// (P8); no LINQ, no closures, no async, no boxing (P9). `public` because Wavee.Tests is a ProjectReference.

namespace Wavee;

public static partial class Visualizer
{
    // ── 1. the vocabulary ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The eight faces. PERSISTED ints (Platform.Keys.StageVisualizer) — append only.</summary>
    public enum Kind : byte { Field = 0, Halo = 1, Horizon = 2, Matrix = 3, Aurora = 4, Spectrum = 5, Pulse = 6, Tape = 7 }

    /// <summary>What a face needs to move.</summary>
    public enum Need : byte { None = 0, Breath = 1, Spectrum = 2, Precomputed = 3, Beats = 4 }

    /// <summary>The engine lease a face holds while live (§2.5).</summary>
    public enum Tier : byte { None = 0, Level = 1, Spectrum = 2 }

    /// <summary>Where this tick's motion came from (the gallery caption, the diagnostics card).</summary>
    public enum Source : byte { Breath = 0, Live = 1, Precomputed = 2, TempoGrid = 3 }

    public static class Catalog
    {
        public const int Count = 8;
        /// <summary>A stored/hand-edited int → a real kind; anything else is Horizon (the board's default).</summary>
        public static int Coerce(int stored) => (uint)stored < (uint)Count ? stored : (int)Kind.Horizon;
        public static Need NeedsOf(Kind k) => k switch
        {
            Kind.Field or Kind.Tape => Need.Breath,
            Kind.Halo or Kind.Matrix or Kind.Aurora or Kind.Spectrum => Need.Spectrum,
            Kind.Horizon => Need.Precomputed,
            Kind.Pulse => Need.Beats,
            _ => Need.None,
        };
        /// <summary>Horizon and Aurora draw through SeriesEl; everything else is bound BoxEls.</summary>
        public static bool UsesSeries(Kind k) => k is Kind.Horizon or Kind.Aurora;
    }

    // ── 2. demand ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static class Demand
    {
        /// <summary>The lease tier for what is VISIBLE (§2.5): outside Visualizer mode only the base Field's breath shows, so the
        /// level tier is enough whatever face is selected (V-U55). Alt-tab is deliberately NOT an input; occlusion and
        /// reduced motion are.</summary>
        public static Tier For(Kind kind, bool visualizerMode, bool stageUp, bool playing, bool ownerUs, bool audioSupported, bool occluded, bool reduced)
        {
            if (!stageUp || !playing || !ownerUs || !audioSupported || occluded || reduced) return Tier.None;
            if (!visualizerMode) return Tier.Level;
            return Catalog.NeedsOf(kind) switch { Need.Spectrum => Tier.Spectrum, Need.Breath => Tier.Level, _ => Tier.None };
        }

        /// <summary>Does the 30 Hz clock run? While the stage is up and not occluded/reduced, and either playing or
        /// something is still settling (a release tail, the reels coasting).</summary>
        public static bool Ticks(bool stageUp, bool playing, bool settled, bool occluded, bool reduced)
            => stageUp && !occluded && !reduced && (playing || !settled);
    }

    // ── 3. bands ───────────────────────────────────────────────────────────────────────────────────────────────────

    public static class Bands
    {
        public const int Count = 48;                       // = SpectrumAnalyzer.DefaultBandCount
        public const float FloorDb = -60f, CeilingDb = -6f;
        public const float MinSensitivity = 0.3f, MaxSensitivity = 1.5f, CalmGain = 0.55f;
        public const float Attack = 0.55f, CalmAttack = 0.25f, Release = 0.10f;
        public const int PeakHoldTicks = 10;
        public const float PeakFall = 0.018f;
        /// <summary>Auto-gain: a slow running maximum of the frame max; the frame is normalised by max(agc, AgcFloor).</summary>
        public const float AgcDecay = 0.995f, AgcFloor = 0.40f, AgcMaxGain = 2f;

        public static float ClampSensitivity(float v) => float.IsFinite(v) ? Math.Clamp(v, MinSensitivity, MaxSensitivity) : 1f;

        /// <summary>dB → 0..1 through the floor/ceiling window. RAW: the AGC and the user gain are applied AFTER (V-U32).</summary>
        public static float Unit(float db) => MathF.Min(1f, MathF.Max(0f, (db - FloorDb) / (CeilingDb - FloorDb)));

        /// <summary>The user's gain on an AGC-normalised value: sensitivity (0.3–1.5) × the calm reduction.</summary>
        public static float Gain(float sensitivity, bool calm) => sensitivity * (calm ? CalmGain : 1f);

        /// <summary>One AGC step: returns the gain to apply to this frame's unit values.</summary>
        public static float Agc(ref float agc, float frameMax)
        {
            agc = MathF.Max(frameMax, agc * AgcDecay);
            return MathF.Min(AgcMaxGain, 1f / MathF.Max(agc, AgcFloor));
        }

        /// <summary>The prototype's follower: fast up (0.55; 0.25 calm), slow down (0.10).</summary>
        public static void Follow(ReadOnlySpan<float> target, Span<float> level, bool calm)
        {
            float atk = calm ? CalmAttack : Attack;
            int n = Math.Min(target.Length, level.Length);
            for (int i = 0; i < n; i++)
            {
                float v = level[i], t = target[i];
                level[i] = v + (t - v) * (t > v ? atk : Release);
            }
        }

        /// <summary>Release every band toward 0 (no live frame this tick).</summary>
        public static void Decay(Span<float> level)
        {
            for (int i = 0; i < level.Length; i++) level[i] -= level[i] * Release;
        }

        /// <summary>Peak caps: hold 10 ticks at the band's level, then fall 0.018 per tick, never below the band.</summary>
        public static void Peaks(ReadOnlySpan<float> level, Span<float> peak, Span<int> hold)
        {
            int n = Math.Min(level.Length, Math.Min(peak.Length, hold.Length));
            for (int i = 0; i < n; i++)
            {
                if (level[i] >= peak[i]) { peak[i] = level[i]; hold[i] = PeakHoldTicks; }
                else if (hold[i] > 0) hold[i]--;
                else peak[i] = MathF.Max(level[i], peak[i] - PeakFall);
            }
        }

        public static float Average(ReadOnlySpan<float> v, int from, int to)
        {
            to = Math.Min(to, v.Length);
            if (to <= from) return 0f;
            float s = 0f;
            for (int i = from; i < to; i++) s += v[i];
            return s / (to - from);
        }

        public static bool Settled(ReadOnlySpan<float> level, float eps = 0.005f)
        {
            for (int i = 0; i < level.Length; i++) if (level[i] > eps) return false;
            return true;
        }
    }

    // ── 4. the faces' arithmetic (every constant from the prototype) ───────────────────────────────────────────────

    public static class Halo
    {
        public const int Bars = 72;
        /// <summary>Bar j reads band round(m/35 · 47 · 0.85), m mirrored about the top (Flagship.dc.html:572).</summary>
        public static int BandOf(int j) { int m = j < 36 ? j : 71 - j; return (int)MathF.Round(m / 35f * (Bands.Count - 1) * 0.85f); }
        public static float Scale(float level) => 0.1f + 0.9f * level;
        public static float AngleDeg(int j) => j * 360f / Bars;
        public static float GlowOpacity(float low) => 0.16f + low * 0.4f;
        /// <summary>The bar colour mixes c2→c1 by |sin(πj/72)| (HALO_C).</summary>
        public static float MixOf(int j) => MathF.Abs(MathF.Sin(j / (float)Bars * MathF.PI));
    }

    /// <summary>Budget (V-U35, §2.10): 3 nodes per column (dim track, lit bar, peak dot) + 12 static row carvers = 32·3 + 12 = 108
    /// (the gallery preview: 16 columns + 12 carvers = 60). The lit bar scales by <see cref="LitRows"/>/<see cref="Rows"/>; the
    /// peak dot sits <see cref="PeakRow"/> rows up.</summary>
    public static class Matrix
    {
        public const int Columns = 32, Rows = 12;
        public static int BandOf(int c) => (int)MathF.Floor(c / 31f * (Bands.Count - 1));
        public static int LitRows(float level) => Math.Clamp((int)MathF.Round(level * Rows), 0, Rows);
        public static int PeakRow(float peak) => Math.Clamp((int)MathF.Ceiling(peak * Rows), 1, Rows);
    }

    public static class Spectrum
    {
        public const int Bars = Bands.Count;
        public const float FloorScale = 0.02f;
        public static float Scale(float level) => MathF.Max(FloorScale, level);
    }

    public static class Aurora
    {
        public const int Points = 65;
        /// <summary>One ribbon as 0..1 heights (the prototype's SVG y, 300 tall: height = (300 − y)/300).</summary>
        public static void Ribbon(Span<float> into, float ampFrac, float f, float speed, float phase, float baseFrac, float t)
        {
            for (int k = 0; k < into.Length; k++)
            {
                float u = k / (float)(into.Length - 1) * MathF.Tau;
                float wave = MathF.Sin(u * f + t * speed + phase) * 0.6f + MathF.Sin(u * f * 2.3f - t * speed * 0.7f + phase * 2f) * 0.4f;
                into[k] = Math.Clamp(1f - baseFrac + ampFrac * wave, 0f, 1f);
            }
        }
        public static void Fill(Span<float> low, Span<float> mid, Span<float> high, float l, float m, float h, float t)
        {
            Ribbon(low, (30f + 120f * l) / 300f, 1.2f, 0.6f, 0f, 210f / 300f, t);
            Ribbon(mid, (20f + 90f * m) / 300f, 1.8f, 0.9f, 1.7f, 180f / 300f, t);
            Ribbon(high, (12f + 70f * h) / 300f, 2.6f, 1.3f, 3.1f, 160f / 300f, t);
        }
    }

    public static class Horizon
    {
        public const int Points = 181;
        public const float SpanMs = 12_000f;
        /// <summary>The SeriesEl amplitude the prototype used (M·0.92).</summary>
        public const float Amplitude = 0.92f;
        /// <summary>The 12 s window about the playhead, three mirrored series: Σ·0.55, (mid+high)·0.6, high·0.75. Indexed by
        /// the track's duration (<see cref="WaveformBands.IndexAt"/>); outside [0, duration) the series is 0.</summary>
        public static void Fill(ReadOnlySpan<WaveSample> samples, long positionMs, long durationMs, Span<float> low, Span<float> mid, Span<float> high)
        {
            if (samples.IsEmpty || durationMs <= 0) { low.Clear(); mid.Clear(); high.Clear(); return; }
            int n = samples.Length;
            for (int k = 0; k < Points; k++)
            {
                float t = positionMs - SpanMs * 0.5f + SpanMs * k / (Points - 1);
                if (t < 0f || t >= durationMs) { low[k] = mid[k] = high[k] = 0f; continue; }
                var s = samples[WaveformBands.IndexAt((long)t, durationMs, n)];
                float a = s.Low / 255f, b = s.Mid / 255f, c = s.High / 255f;
                low[k] = MathF.Min(1f, (a + b + c) * 0.55f);
                mid[k] = MathF.Min(1f, (b + c) * 0.6f);
                high[k] = MathF.Min(1f, c * 0.75f);
            }
        }
    }

    public static class Pulse
    {
        public const int Rings = 4;
        public static float Kick(float phase) => MathF.Exp(-6f * phase);
        /// <summary>Ring r's 0..1 progress: four rings a beat apart, each four beats long (Flagship.dc.html:291, 587).</summary>
        public static float RingProgress(int beatIndex, float phase, int ring) => (((beatIndex + ring) & 3) + phase) / Rings;
        public static float RingOpacity(float progress) => 0.6f * (1f - progress);
        public static float RingScale(float progress) => 1f + 1.3f * progress;
        public static float CoverScale(float kick) => 1f + kick * 0.025f;
        public static float GlowOpacity(float low) => 0.14f + low * 0.3f;
    }

    public static class Tape
    {
        public const float R0 = 34f, R1 = 128f, Flange = 140f, HubR = 30f;
        /// <summary>Tape speed in prototype units; the hub turns Speed/radius rad/s.</summary>
        public const float Speed = 70f, SpeedEase = 0.08f;
        public const int MeterCells = 20;
        /// <summary>Area-conserving packs: r² trades linearly with progress.</summary>
        public static (float Left, float Right) Radii(float progress)
        {
            float p = Math.Clamp(progress, 0f, 1f);
            float a = R0 * R0, b = R1 * R1 - R0 * R0;
            return (MathF.Sqrt(a + b * (1f - p)), MathF.Sqrt(a + b * p));
        }
        public static float AngleStepDeg(float speed, float radius, float dtSec) => speed / MathF.Max(radius, 1f) * 57.29578f * dtSec;
        public static int Lit(float level) => Math.Clamp((int)MathF.Round(level * MeterCells * 1.6f), 0, MeterCells);
        /// <summary>0 blue, 1 green, 2 orange (cells 0-11, 12-16, 17-19).</summary>
        public static int Zone(int cell) => cell < 12 ? 0 : cell < 17 ? 1 : 2;
    }

    public static class Field
    {
        public const int Blobs = 4;
        /// <summary>Keyframe periods per blob (s) and the drift targets as fractions of W/H plus the end scale.</summary>
        public static readonly float[] PeriodSec = [30f, 36f, 33f, 28f];
        public static readonly (float Dx, float Dy, float Scale)[] Drift = [(0.16f, 0.12f, 1.15f), (-0.14f, 0.14f, 0.9f), (0.18f, -0.12f, 1.1f), (-0.15f, -0.13f, 1.2f)];
        public static float FaceOpacity(float low) => 0.75f + low * 0.25f;
        public static float BaseOpacity(float low, bool visualizer) => visualizer ? Stage.Tone.BaseFieldVisualizerA : Stage.Tone.BaseFieldA + low * Stage.Tone.BaseFieldBreathA;
    }

    // ── 5. the fold ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What one tick reads. The band/beat spans travel beside it (a readonly record struct cannot hold a span).</summary>
    public readonly record struct Input(
        bool Playing, bool Calm, float Sensitivity, bool HaveLive, bool Muted, float Rms,
        long PositionMs, long DurationMs, bool HaveBands, bool HaveBeats, ushort TempoX10, bool Visualizer);

    /// <summary>Every scalar the slab writes after a tick.</summary>
    public readonly record struct Frame(
        float Low, float Mid, float High, float Level, float Kick, int BeatIndex, float BeatPhase,
        float FaceFieldOp, float BaseFieldOp, float GlowOp, float BeatScale,
        float ReelL, float ReelR, float AngleL, float AngleR, int MeterLit, Source Source);

    /// <summary>THE model: owns every preallocated array; <see cref="Tick"/> allocates nothing.</summary>
    public sealed class Model
    {
        public readonly float[] Target = new float[Bands.Count], Level = new float[Bands.Count], Peak = new float[Bands.Count];
        readonly int[] _hold = new int[Bands.Count];
        public readonly float[] HorizonLow = new float[Horizon.Points], HorizonMid = new float[Horizon.Points], HorizonHigh = new float[Horizon.Points];
        public readonly float[] AuroraLow = new float[Aurora.Points], AuroraMid = new float[Aurora.Points], AuroraHigh = new float[Aurora.Points];
        float _t, _agc = Bands.AgcFloor, _speed, _angleL, _angleR, _kick;
        public bool IsSettled { get; private set; } = true;

        /// <summary><paramref name="liveDb"/>: the 48 dB bands when <see cref="Input.HaveLive"/> (else ignored);
        /// <paramref name="bands"/>: the kind-237 samples or empty; <paramref name="beats"/>: the TrackBeats payload or empty.
        /// <see cref="Input.Rms"/> is the PRE-gain window RMS under a spectrum lease (SpectrumInfo.WindowRms), the level tap's
        /// RMS under a level lease.</summary>
        public Frame Tick(in Input input, ReadOnlySpan<float> liveDb, ReadOnlySpan<WaveSample> bands, ReadOnlySpan<uint> beats, float dtSec)
        {
            _t += dtSec;
            bool live = input.HaveLive && !input.Muted && liveDb.Length >= Bands.Count;
            float userGain = Bands.Gain(Bands.ClampSensitivity(input.Sensitivity), input.Calm);

            // 1. bands: live → unit → AGC → the user's gain → follow; otherwise release toward the floor (nothing is synthesised)
            if (live && input.Playing)
            {
                float frameMax = 0f;
                for (int i = 0; i < Bands.Count; i++) { float u = Bands.Unit(liveDb[i]); Target[i] = u; if (u > frameMax) frameMax = u; }
                float gain = Bands.Agc(ref _agc, frameMax) * userGain;          // AGC first, then sensitivity/calm (V-U32)
                for (int i = 0; i < Bands.Count; i++) Target[i] = MathF.Min(1f, Target[i] * gain);
                Bands.Follow(Target, Level, input.Calm);
            }
            else Bands.Decay(Level);
            Bands.Peaks(Level, Peak, _hold);

            // 2. the three energies and the level: live from the bands, else the precomputed waveform at the playhead
            float low, mid, high, level;
            Source source;
            if (live && input.Playing)
            {
                low = Bands.Average(Level, 0, 8); mid = Bands.Average(Level, 8, 28); high = Bands.Average(Level, 28, 48);
                level = MathF.Min(1f, MathF.Max(0f, input.Rms * 3.5f));
                source = Source.Live;
            }
            else if (input.HaveBands && input.Playing && WaveformBands.At(bands, input.PositionMs, input.DurationMs, out float bl, out float bm, out float bh))
            {
                low = MathF.Min(1f, bl * userGain); mid = MathF.Min(1f, bm * userGain); high = MathF.Min(1f, bh * userGain);
                level = (bl + bm + bh) / 3f;
                source = Source.Precomputed;
            }
            else { low = mid = high = level = 0f; source = Source.Breath; }

            // 3. the beat: the grid, else the tempo, else nothing
            int beatIndex = 0; float phase = 0f; bool hasBeat;
            if (input.HaveBeats && BeatGrid.Phase(beats, input.PositionMs, out beatIndex, out phase, out _))
            {
                hasBeat = true;
                if (beatIndex < 0) { beatIndex = 0; phase = 1f; }                // before the first beat: a spent kick, not a full one
                if (source == Source.Breath) source = Source.Precomputed;         // Spotify's beat grid IS precomputed data
            }
            else if (BeatGrid.TempoPhase(input.TempoX10, input.PositionMs, out phase, out float periodMs)) { hasBeat = true; beatIndex = periodMs > 0f ? (int)(input.PositionMs / periodMs) : 0; if (source == Source.Breath) source = Source.TempoGrid; }
            else { hasBeat = false; beatIndex = 0; }                             // Phase leaves -1 behind on an empty grid
            float kickTarget = hasBeat && input.Playing ? Pulse.Kick(phase) * (input.Calm ? 0.3f : 1f) : 0f;
            _kick = kickTarget > _kick ? kickTarget : _kick + (kickTarget - _kick) * 0.35f;

            // 4. series: Horizon from the payload (empty ⇒ flat), Aurora always drifting
            Horizon.Fill(bands, input.PositionMs, input.DurationMs, HorizonLow, HorizonMid, HorizonHigh);
            Aurora.Fill(AuroraLow, AuroraMid, AuroraHigh, low, mid, high, _t);

            // 5. tape: eased speed, area-conserving radii, hubs turning at Speed/radius
            _speed += ((input.Playing ? Tape.Speed : 0f) - _speed) * Tape.SpeedEase;
            if (_speed < 0.05f) _speed = 0f;
            float progress = input.DurationMs > 0 ? input.PositionMs / (float)input.DurationMs : 0f;
            var (rL, rR) = Tape.Radii(progress);
            _angleL = (_angleL + Tape.AngleStepDeg(_speed, rL, dtSec)) % 360f;
            _angleR = (_angleR + Tape.AngleStepDeg(_speed, rR, dtSec)) % 360f;

            IsSettled = !input.Playing && Bands.Settled(Level) && _kick < 0.002f && _speed == 0f;
            return new Frame(low, mid, high, level, _kick, beatIndex, phase,
                Field.FaceOpacity(low), Field.BaseOpacity(low, input.Visualizer), Halo.GlowOpacity(low), Pulse.CoverScale(_kick),
                rL, rR, _angleL, _angleR, Tape.Lit(level), source);
        }
    }

    /// <summary>The clock's cadence — Design.Cadence.PluggedLoopHz (30), restated so this file stays System-only; a test pins the two.</summary>
    public const int TickHz = 30;
    public const float TickMs = 1000f / TickHz;
    public const float DtMinSec = 0.001f, DtMaxSec = 0.080f;
    public static float DeltaSec(long lastTickMs, long nowMs)
        => lastTickMs == 0L ? TickMs / 1000f : Math.Clamp((nowMs - lastTickMs) / 1000f, DtMinSec, DtMaxSec);
}
