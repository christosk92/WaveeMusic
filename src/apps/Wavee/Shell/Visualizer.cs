// ── Shell/Visualizer.cs ────────────────────────────────────────────────────────────────────────────────────────────
// Visualizer.Kind/Need/Tier/Source/Group/Pace/Lod, Catalog (the shown faces, the legacy migration), Demand, Bands (+ the
// precomputed spread and the tempo pulse), Beat, Aurora, Timeline, Oscilloscope, Sections, Field (the backdrop) and the ONE
// alloc-free Model.Tick the clock folds every tick through
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 760 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §2.5-§2.10, §4.8; viz-app-plan §3.1-§3.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT A FACE IS MADE OF. The engine publishes 48 log bands in dB (SpectrumAnalyzer) ~60×/s; the UI PULLS them at every
// frame into the clock's 48-float dB buffer and this file turns them into every scalar a face binds: `Bands.Unit` (dB → 0..1
// with the sensitivity and the calm gain), `Bands.Follow` (the prototype's 0.55/0.10 attack/release), `Bands.Peaks` (hold
// 10 ticks, fall 0.018), the low/mid/high averages, the beat and the bar (grid → tempo), the progress, the 4-bar energy and
// its section, the ripple ring buffer, the flux follower and the onset flash, the Aurora ribbons, the whole-song Timeline
// series and the scope line. Every constant is named here ONCE.
//
// NOTHING IS FAKED, NOTHING IS DEAD. The band ladder is: live FFT → the level tap (a live level spread over the bands) →
// the song's own kind-237 waveform at the playhead, spread over the 48 bands by three fixed envelopes → the beat grid or
// the tempo as a bass-weighted hump on each beat → rest (the release toward the floor). `Source` says which, every tick,
// for the gallery caption and the diagnostics card.
//
// ORDER OF THE BAND PIPELINE (§2.8, V-U32): dB → `Unit` (RAW, floor/ceiling window) → AGC (a slow running maximum of
// the frame's max, so a quiet master and a loud one fill the same range) → the USER's gain (sensitivity × calm) →
// follower → peak hold. The user's gain is applied to an already-normalised frame; applying it before the AGC would let
// the AGC cancel the slider.
//
// THE FACES are in Visualizer.Fluent/Classics/Zune/ITunes.UI.cs and Verse.UI.cs; the eight first-generation faces (Field,
// Halo, Horizon, Matrix, Spectrum, Pulse, Tape) are gone and their persisted ints migrate (`Catalog.Coerce`).
//
// Rules: `System`-only; the model owns its arrays (allocated once in the constructor) and `Tick` allocates nothing
// (P8); no LINQ, no closures, no async, no boxing (P9). `public` because Wavee.Tests is a ProjectReference.

namespace Wavee;

public static partial class Visualizer
{
    // ── 1. the vocabulary ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The faces. PERSISTED ints (Platform.Keys.StageVisualizer) — append only. 0-7 except Aurora are the first
    /// generation: their builders are gone and a stored value migrates to its successor (<see cref="Catalog.Coerce"/>).</summary>
    public enum Kind : byte
    {
        Field = 0, Halo = 1, Horizon = 2, Matrix = 3, Aurora = 4, Spectrum = 5, Pulse = 6, Tape = 7,
        Verse = 8, Bloom = 9, Bars = 10, Ring = 11, Orbit = 12, Timeline = 13, Classic = 14, Warp = 15, Tunnel = 16,
        Ambience = 17, Kaleido = 18, Scope = 19, Drift = 20, Type = 21, Mosaic = 22, Spotlight = 23, Magneto = 24, Flow = 25,
    }

    /// <summary>What a face needs to move (flags: a face may need several).</summary>
    [Flags]
    public enum Need : byte
    {
        None = 0, Level = 1, Spectrum = 2, Beats = 4, Precomputed = 8, Scope = 16, Lyrics = 32, Covers = 64, Gallery = 128,
    }

    /// <summary>The engine lease a face holds while live (§2.5). Scope implies spectrum as spectrum implies level: the
    /// engine's time-domain tap rides the spectrum lease (<c>CopyWaveform</c>), so <see cref="Scope"/> is a spectrum lease
    /// plus the per-tick waveform pull.</summary>
    public enum Tier : byte { None = 0, Level = 1, Spectrum = 2, Scope = 3 }

    /// <summary>Where this tick's motion came from (the gallery caption, the diagnostics card).</summary>
    public enum Source : byte { Breath = 0, Live = 1, Precomputed = 2, TempoGrid = 3 }

    /// <summary>The gallery's sections, in order.</summary>
    public enum Group : byte { Lyrics = 0, Fluent = 1, Classics = 2, Zune = 3, ITunes = 4 }

    /// <summary>How much a face moves — the word under its gallery name.</summary>
    public enum Pace : byte { Calm = 0, Lively = 1 }

    /// <summary>A face's level of detail: the stage, a live gallery preview, or a frozen poster (bound to a slab that
    /// never ticks — zero writes).</summary>
    public enum Lod : byte { Stage = 0, Preview = 1, Poster = 2 }

    public static class Catalog
    {
        public const int Count = 26;
        /// <summary>The first-run face and the answer for any int this build cannot draw.</summary>
        public const Kind Default = Kind.Bloom;

        static readonly Kind[] s_shown =
        [
            Kind.Verse,
            Kind.Bloom, Kind.Bars, Kind.Ring, Kind.Orbit, Kind.Aurora, Kind.Timeline,
            Kind.Classic, Kind.Warp, Kind.Tunnel, Kind.Ambience, Kind.Kaleido, Kind.Scope, Kind.Drift,
            Kind.Type, Kind.Mosaic, Kind.Spotlight,
            Kind.Magneto, Kind.Flow,
        ];
        static readonly Group[] s_groups = [Group.Lyrics, Group.Fluent, Group.Classics, Group.Zune, Group.ITunes];

        /// <summary>The shipped faces in gallery order (grouped: Lyrics | Fluent | Classics | Zune | iTunes).</summary>
        public static ReadOnlySpan<Kind> Shown => s_shown;
        public static int ShownCount => s_shown.Length;
        public static ReadOnlySpan<Group> Groups => s_groups;

        /// <summary>The first generation (0-7) minus Aurora, which was redone in place and is still shown.</summary>
        public static bool IsLegacy(Kind k) => k is Kind.Field or Kind.Halo or Kind.Horizon or Kind.Matrix or Kind.Spectrum or Kind.Pulse or Kind.Tape;
        public static bool IsShown(Kind k) => (uint)k < Count && !IsLegacy(k);

        /// <summary>A legacy face's successor (Field→Bloom, Halo→Ring, Horizon→Timeline, Matrix→Classic, Spectrum→Bars,
        /// Pulse→Ring, Tape→Timeline — Tape's reels were the song's progress); a shown face is itself.</summary>
        public static Kind Successor(Kind k) => k switch
        {
            Kind.Field => Kind.Bloom,
            Kind.Halo or Kind.Pulse => Kind.Ring,
            Kind.Horizon or Kind.Tape => Kind.Timeline,
            Kind.Matrix => Kind.Classic,
            Kind.Spectrum => Kind.Bars,
            _ => k,
        };

        /// <summary>A stored/hand-edited int → a SHOWN kind: a shown int is itself, a legacy int its successor, anything
        /// else <see cref="Default"/>.</summary>
        public static int Coerce(int stored) => (uint)stored < (uint)Count ? (int)Successor((Kind)stored) : (int)Default;

        public static Group GroupOf(Kind k) => k switch
        {
            Kind.Verse => Group.Lyrics,
            Kind.Classic or Kind.Warp or Kind.Tunnel or Kind.Ambience or Kind.Kaleido or Kind.Scope or Kind.Drift => Group.Classics,
            Kind.Type or Kind.Mosaic or Kind.Spotlight => Group.Zune,
            Kind.Magneto or Kind.Flow => Group.ITunes,
            _ => Group.Fluent,
        };

        public static Pace PaceOf(Kind k) => k switch
        {
            Kind.Bars or Kind.Ring or Kind.Classic or Kind.Warp or Kind.Tunnel or Kind.Kaleido or Kind.Scope or Kind.Drift
                or Kind.Mosaic or Kind.Magneto => Pace.Lively,
            _ => Pace.Calm,
        };

        /// <summary>What each face reads (viz-app-plan §2.20). A legacy kind answers for its successor.</summary>
        public static Need NeedsOf(Kind k) => Successor(k) switch
        {
            Kind.Verse => Need.Spectrum | Need.Lyrics,
            Kind.Bloom => Need.Level,
            Kind.Ring or Kind.Warp or Kind.Tunnel or Kind.Drift or Kind.Magneto => Need.Spectrum | Need.Beats,
            Kind.Timeline => Need.Precomputed | Need.Beats | Need.Level,
            Kind.Scope or Kind.Flow => Need.Spectrum | Need.Scope,
            Kind.Type => Need.Level | Need.Beats,
            Kind.Mosaic => Need.Spectrum | Need.Beats | Need.Covers,
            Kind.Spotlight => Need.Beats | Need.Gallery,
            _ => Need.Spectrum,                       // Bars, Orbit, Aurora, Classic, Ambience, Kaleido
        };

        public static bool Has(Need needs, Need flag) => (needs & flag) != 0;

        /// <summary>The lease a set of needs holds: scope ⊃ spectrum ⊃ level; beats/precomputed/covers/gallery need none.</summary>
        public static Tier TierOf(Need needs)
            => Has(needs, Need.Scope) ? Tier.Scope : Has(needs, Need.Spectrum) ? Tier.Spectrum : Has(needs, Need.Level) ? Tier.Level : Tier.None;

        /// <summary>The tier the OPEN gallery's previews need: the max over the shown faces, capped at Spectrum (the scope
        /// pull is a per-tick cost, never a lease of its own; a Scope preview reads the same spectrum lease).</summary>
        public static readonly Tier PreviewTier = ComputePreviewTier();

        static Tier ComputePreviewTier()
        {
            var t = Tier.None;
            foreach (var k in s_shown)
            {
                var own = TierOf(NeedsOf(k));
                if (own > Tier.Spectrum) own = Tier.Spectrum;
                if (own > t) t = own;
            }
            return t;
        }

        public static bool UsesScope(Kind k) => Has(NeedsOf(k), Need.Scope);
        /// <summary>The faces that draw through SeriesEl (the rest are bound BoxEls / sprites / images / text).</summary>
        public static bool UsesSeries(Kind k) => Successor(k) is Kind.Aurora or Kind.Timeline or Kind.Scope or Kind.Flow;
        /// <summary>Can the lyric caption sit over this face? Not over Verse (the lyrics ARE the face), not over the faces
        /// that carry their own text (Type, Mosaic, Spotlight) and not over the ones whose drawing sits where the caption
        /// would (Timeline, Scope, Classic, Flow) — the prototype's rule.</summary>
        public static bool CaptionFriendly(Kind k)
            => Successor(k) is not (Kind.Verse or Kind.Timeline or Kind.Scope or Kind.Classic or Kind.Type or Kind.Mosaic or Kind.Spotlight or Kind.Flow);
        /// <summary>A preview that is a physics sim or a feedback trail: a poster unless it is the one being looked at.</summary>
        public static bool Physics(Kind k) => k is Kind.Warp or Kind.Magneto or Kind.Drift or Kind.Flow;

        /// <summary>The index in <see cref="Shown"/>; −1 for a legacy kind.</summary>
        public static int IndexOf(Kind k)
        {
            for (int i = 0; i < s_shown.Length; i++) if (s_shown[i] == k) return i;
            return -1;
        }

        /// <summary>The shown face <paramref name="delta"/> steps away, wrapping (the stage's [ and ] keys).</summary>
        public static Kind Step(Kind k, int delta)
        {
            int n = s_shown.Length, i = IndexOf(Successor(k));
            if (i < 0) i = IndexOf(Default);
            return s_shown[((i + delta) % n + n) % n];
        }

        /// <summary>The FIRST face of the group <paramref name="delta"/> groups away, wrapping (Shift + [ / ]).</summary>
        public static Kind StepGroup(Kind k, int delta)
        {
            int n = s_groups.Length, g = (int)GroupOf(Successor(k));
            return FirstOf(s_groups[((g + delta) % n + n) % n]);
        }

        public static Kind FirstOf(Group g)
        {
            foreach (var k in s_shown) if (GroupOf(k) == g) return k;
            return Default;
        }

        public static int CountIn(Group g)
        {
            int c = 0;
            foreach (var k in s_shown) if (GroupOf(k) == g) c++;
            return c;
        }

        /// <summary>The live-preview budget: at most this many gallery tiles bind the ticking slab at once.</summary>
        public const int MaxLivePreviews = 6;
        /// <summary>How far either side of the selected tile (in gallery order) previews stay live.</summary>
        public const int LiveRadius = 2;

        /// <summary>A gallery tile's detail (viz-app-plan §4.1): the selected tile and the one under the pointer (or the
        /// keyboard cursor) are live; the selected tile's neighbours are live unless their preview is a physics sim;
        /// everything else is a frozen poster. The GPU tier is not an input (every tier gets the same gallery). At most <see cref="LiveRadius"/>·2 + 1 + 1 =
        /// <see cref="MaxLivePreviews"/> tiles are live. Indices are into <see cref="Shown"/>; −1 = none.</summary>
        public static Lod LodFor(int index, int selected, int focus)
        {
            if (index == selected || index == focus) return Lod.Preview;
            if ((uint)index >= (uint)s_shown.Length || Physics(s_shown[index])) return Lod.Poster;
            return selected >= 0 && Math.Abs(index - selected) <= LiveRadius ? Lod.Preview : Lod.Poster;
        }
    }

    // ── 2. demand ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static class Demand
    {
        /// <summary>The lease tier for what is VISIBLE (§2.5): outside Visualizer mode only the base Field's breath shows, so the
        /// level tier is enough whatever face is selected (V-U55); in Visualizer mode the face's own tier, raised to the open
        /// gallery's preview tier (<see cref="Catalog.PreviewTier"/>) so a Bars preview is not dead under a Bloom stage.
        /// Alt-tab is deliberately NOT an input; occlusion and reduced motion are.</summary>
        public static Tier For(Kind kind, bool visualizerMode, bool galleryShown, bool stageUp, bool playing, bool ownerUs, bool audioSupported, bool occluded, bool reduced)
            => For(Catalog.NeedsOf(kind), visualizerMode, galleryShown, stageUp, playing, ownerUs, audioSupported, occluded, reduced);

        /// <summary>The same decision for an explicit set of needs — what the visualizer LAYOUTS hold (<c>Stage.LayoutRules.NeedsOf</c>:
        /// the strip's spectrum, or the level the backdrop field breathes with), where there is no face to name.</summary>
        public static Tier For(Need needs, bool visualizerMode, bool galleryShown, bool stageUp, bool playing, bool ownerUs, bool audioSupported, bool occluded, bool reduced)
        {
            if (!stageUp || !playing || !ownerUs || !audioSupported || occluded || reduced) return Tier.None;
            if (!visualizerMode) return Tier.Level;
            var face = Catalog.TierOf(needs);
            return galleryShown && Catalog.PreviewTier > face ? Catalog.PreviewTier : face;
        }

        /// <summary>Does the clock fill the scope series this tick? Under a spectrum-or-better lease, in Visualizer mode, while
        /// at least one MOUNTED face reads them (<paramref name="readers"/>: the live slab's <c>ScopeReaders</c> — the stage
        /// Scope/Flow face and the LIVE gallery tiles of those kinds count themselves; an open gallery whose Scope tile is a
        /// poster is no reason).</summary>
        public static bool PullsScope(Tier tier, bool visualizerMode, int readers)
            => tier >= Tier.Spectrum && visualizerMode && readers > 0;

        /// <summary>Does the clock run? While the stage is up and not occluded/reduced, and either playing or
        /// something is still settling (a release tail, a ripple fading out).</summary>
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
        /// <summary>The level tap's RMS → a 0..1 level (the prototype's ×3.5).</summary>
        public const float RmsGain = 3.5f;

        /// <summary>The clock rate every per-tick constant in this file was authored at (the prototype's 30 Hz). The model
        /// steps by <c>dtSec · RefHz</c> REFERENCE ticks, so the look is the same at any clock rate (<see cref="TickHz"/>).</summary>
        public const float RefHz = 30f;

        /// <summary>A per-reference-tick fraction <paramref name="k"/> over <paramref name="ticks"/> reference ticks:
        /// <c>1 − (1 − k)^ticks</c>. Exactly <paramref name="k"/> at one tick, so a 30 Hz step is bit-identical.</summary>
        public static float PerTicks(float k, float ticks) => ticks == 1f ? k : 1f - MathF.Pow(1f - k, ticks);

        public static float ClampSensitivity(float v) => float.IsFinite(v) ? Math.Clamp(v, MinSensitivity, MaxSensitivity) : 1f;

        /// <summary>dB → 0..1 through the floor/ceiling window. RAW: the AGC and the user gain are applied AFTER (V-U32).</summary>
        public static float Unit(float db) => MathF.Min(1f, MathF.Max(0f, (db - FloorDb) / (CeilingDb - FloorDb)));

        /// <summary>The user's gain on an AGC-normalised value: sensitivity (0.3–1.5) × the calm reduction.</summary>
        public static float Gain(float sensitivity, bool calm) => sensitivity * (calm ? CalmGain : 1f);

        /// <summary>One AGC step: returns the gain to apply to this frame's unit values.</summary>
        public static float Agc(ref float agc, float frameMax) => Agc(ref agc, frameMax, 1f);

        /// <summary><see cref="Agc(ref float, float)"/> over <paramref name="ticks"/> reference ticks.</summary>
        public static float Agc(ref float agc, float frameMax, float ticks)
        {
            agc = MathF.Max(frameMax, agc * (ticks == 1f ? AgcDecay : MathF.Pow(AgcDecay, ticks)));
            return MathF.Min(AgcMaxGain, 1f / MathF.Max(agc, AgcFloor));
        }

        /// <summary>The prototype's follower: fast up (0.55; 0.25 calm), slow down (0.10).</summary>
        public static void Follow(ReadOnlySpan<float> target, Span<float> level, bool calm) => Follow(target, level, calm, 1f);

        /// <summary><see cref="Follow(ReadOnlySpan{float}, Span{float}, bool)"/> over <paramref name="ticks"/> reference ticks.</summary>
        public static void Follow(ReadOnlySpan<float> target, Span<float> level, bool calm, float ticks)
        {
            float atk = PerTicks(calm ? CalmAttack : Attack, ticks), rel = PerTicks(Release, ticks);
            int n = Math.Min(target.Length, level.Length);
            for (int i = 0; i < n; i++)
            {
                float v = level[i], t = target[i];
                level[i] = v + (t - v) * (t > v ? atk : rel);
            }
        }

        /// <summary>Release every band toward 0 (no source this tick).</summary>
        public static void Decay(Span<float> level) => Decay(level, 1f);

        /// <summary><see cref="Decay(Span{float})"/> over <paramref name="ticks"/> reference ticks.</summary>
        public static void Decay(Span<float> level, float ticks)
        {
            float rel = PerTicks(Release, ticks);
            for (int i = 0; i < level.Length; i++) level[i] -= level[i] * rel;
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

        /// <summary><see cref="Peaks(ReadOnlySpan{float}, Span{float}, Span{int})"/> over <paramref name="ticks"/> reference
        /// ticks, the hold counted in (fractional) reference ticks — the same 10-tick hold and 0.018-per-tick fall in time.</summary>
        public static void Peaks(ReadOnlySpan<float> level, Span<float> peak, Span<float> hold, float ticks)
        {
            int n = Math.Min(level.Length, Math.Min(peak.Length, hold.Length));
            for (int i = 0; i < n; i++)
            {
                if (level[i] >= peak[i]) { peak[i] = level[i]; hold[i] = PeakHoldTicks; }
                else if (hold[i] > 0f) hold[i] -= ticks;
                else peak[i] = MathF.Max(level[i], peak[i] - PeakFall * ticks);
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

        // ── the precomputed spread and the tempo pulse (§3.3): the song's own data, never a timer or a random ──

        /// <summary>The three envelopes' centres and widths (bands): low 4/6, mid 18/10, high 38/10.</summary>
        public const float LowCentre = 4f, LowWidth = 6f, MidCentre = 18f, MidWidth = 10f, HighCentre = 38f, HighWidth = 10f;
        /// <summary>The per-band ripple's depth, scaled by the loudest energy (so silence stays silent).</summary>
        public const float SpreadRipple = 0.06f;

        static readonly float[] s_envLow = Envelope(LowCentre, LowWidth), s_envMid = Envelope(MidCentre, MidWidth), s_envHigh = Envelope(HighCentre, HighWidth);

        static float[] Envelope(float centre, float width)
        {
            var e = new float[Count];
            for (int i = 0; i < Count; i++) { float d = (i - centre) / width; e[i] = MathF.Exp(-0.5f * d * d); }
            return e;
        }

        /// <summary>A deterministic 0..1 hash of (band, seed) — the spread's ripple; the same seed always draws the same bars.</summary>
        public static float Hash(int i, int seed)
        {
            uint h = unchecked((uint)i * 374_761_393u + (uint)seed * 668_265_263u);
            h = unchecked((h ^ (h >> 13)) * 1_274_126_177u);
            return (h ^ (h >> 16)) / 4_294_967_296f;
        }

        /// <summary>Three energies (the kind-237 bytes at the playhead, or a live level) spread over the 48 bands by three
        /// fixed Gaussian envelopes, plus a deterministic per-band ripple <c>0.06·hash(i, seed)</c> scaled by the loudest
        /// energy so the bars are not one smooth hump. Zero in, zero out.</summary>
        public static void Spread(float low, float mid, float high, int seed, Span<float> into)
        {
            float peak = MathF.Max(low, MathF.Max(mid, high));
            int n = Math.Min(Count, into.Length);
            for (int i = 0; i < n; i++)
            {
                float v = low * s_envLow[i] + mid * s_envMid[i] + high * s_envHigh[i] + SpreadRipple * peak * Hash(i, seed);
                into[i] = MathF.Min(1f, MathF.Max(0f, v));
            }
        }

        /// <summary>The tempo stand-in: a bass-weighted hump on each beat, <c>Beat.Kick(phase)</c> high — the grid or the
        /// tempo IS the song's data, the shape is the only thing drawn.</summary>
        public static void Pulse(float phase, Span<float> into)
        {
            float k = Beat.Kick(phase);
            int n = Math.Min(Count, into.Length);
            for (int i = 0; i < n; i++) into[i] = MathF.Min(1f, k * (0.70f * s_envLow[i] + 0.30f * s_envMid[i] + 0.12f * s_envHigh[i]));
        }

        /// <summary>The kind-237 bands at a position, LINEARLY interpolated between the two neighbouring samples (sample i
        /// is centred at (i + ½)·dur/n), each 0..1. False for an empty payload or an unknown duration.</summary>
        public static bool Sample(ReadOnlySpan<WaveSample> samples, long positionMs, long durationMs, out float low, out float mid, out float high)
        {
            low = mid = high = 0f;
            int n = samples.Length;
            if (n == 0 || durationMs <= 0) return false;
            double f = positionMs * (double)n / durationMs - 0.5;
            int i0 = (int)Math.Clamp(Math.Floor(f), 0.0, n - 1.0), i1 = Math.Min(i0 + 1, n - 1);
            float t = (float)Math.Clamp(f - i0, 0.0, 1.0);
            WaveSample a = samples[i0], b = samples[i1];
            low = (a.Low + (b.Low - a.Low) * t) / 255f;
            mid = (a.Mid + (b.Mid - a.Mid) * t) / 255f;
            high = (a.High + (b.High - a.High) * t) / 255f;
            return true;
        }
    }

    // ── 4. the shared arithmetic the slab carries ──────────────────────────────────────────────────────────────────

    public static class Beat
    {
        /// <summary>The kick envelope over a beat: e^(−6·phase).</summary>
        public static float Kick(float phase) => MathF.Exp(-6f * phase);
        /// <summary>A tempo grid (or a grid without bar marks) counts every 4th beat as a downbeat.</summary>
        public const int BeatsPerBar = 4;
        /// <summary>The calm kick (×0.3) and the kick follower's release per reference tick.</summary>
        public const float CalmKick = 0.3f, KickRelease = 0.35f;
        /// <summary>A ripple's life (Ring's rings, §2.4) and the shortest gap between two spawns.</summary>
        public const float RippleLifeSec = 1.1f, RippleMinGapSec = 0.15f;
        public const int Ripples = 4;
        /// <summary>The flux follower (0.6 up / 0.2 down per reference tick), the dB that reads as a full flux, and the onset
        /// flash's per-reference-tick decay.</summary>
        public const float FluxAttack = 0.6f, FluxRelease = 0.2f, FluxFullDb = 6f, OnsetDecay = 0.85f;
        /// <summary>The 4-bar energy window when there is no beat to measure bars in.</summary>
        public const float EnergyWindowMs = 8_000f;
        /// <summary>At most this many kind-237 samples are averaged for the energy window (strided).</summary>
        public const int EnergyMaxSamples = 64;

        /// <summary>The bar under a grid beat: the downbeats at or before <paramref name="beatIndex"/> (bit 31 of the grid,
        /// <see cref="BeatGrid.IsDownbeat"/>), counted by the caller; a grid without bar marks counts every 4th beat.</summary>
        public static (int Bar, float Phase) BarOfTempo(int beatIndex, float beatPhase)
        {
            int b = Math.Max(0, beatIndex);
            return (b / BeatsPerBar, (b % BeatsPerBar + beatPhase) / BeatsPerBar);
        }
    }

    /// <summary>The 0 quiet · 1 normal · 2 loud section over the 4-bar energy relative to the track's mean, hysteretic so a
    /// passage on the line does not flicker.</summary>
    public static class Sections
    {
        public const byte Quiet = 0, Normal = 1, Loud = 2;
        public const float LoudEnter = 1.15f, LoudLeave = 1.05f, QuietEnter = 0.75f, QuietLeave = 0.85f;

        public static byte Next(byte current, float ratio)
        {
            if (!float.IsFinite(ratio)) return Normal;
            return current switch
            {
                Loud => ratio < LoudLeave ? (ratio < QuietEnter ? Quiet : Normal) : Loud,
                Quiet => ratio > QuietLeave ? (ratio > LoudEnter ? Loud : Normal) : Quiet,
                _ => ratio > LoudEnter ? Loud : ratio < QuietEnter ? Quiet : Normal,
            };
        }
    }

    public static class Aurora
    {
        public const int Points = 256;
        /// <summary>Each ribbon's base drift speed (rad/s at full energy, scaled by 0.15 + e — so silence is near-still).</summary>
        public const float LowSpeed = 0.6f, MidSpeed = 0.9f, HighSpeed = 1.3f, StillFloor = 0.15f;

        /// <summary>Ribbon amplitude from its energy: 0.05 at rest, 0.27 at full.</summary>
        public static float Amplitude(float e) => 0.05f + 0.22f * e;

        /// <summary>One ribbon as 0..1 heights over the box, a two-sine wave about <paramref name="baseFrac"/>.</summary>
        public static void Ribbon(Span<float> into, float ampFrac, float f, float phase, float offset, float baseFrac)
        {
            for (int k = 0; k < into.Length; k++)
            {
                float u = k / (float)(into.Length - 1) * MathF.Tau;
                float wave = MathF.Sin(u * f + phase + offset) * 0.6f + MathF.Sin(u * f * 2.3f - phase * 0.7f + offset * 2f) * 0.4f;
                into[k] = Math.Clamp(baseFrac + ampFrac * wave, 0f, 1f);
            }
        }

        /// <summary>The three ribbons at their phases and energies.</summary>
        public static void Fill(Span<float> low, Span<float> mid, Span<float> high, float l, float m, float h, float pl, float pm, float ph)
        {
            Ribbon(low, Amplitude(l), 1.2f, pl, 0f, 0.30f);
            Ribbon(mid, Amplitude(m), 1.8f, pm, 1.7f, 0.40f);
            Ribbon(high, Amplitude(h), 2.6f, ph, 3.1f, 0.47f);
        }
    }

    /// <summary>The WHOLE song's kind-237 waveform as <see cref="Points"/> buckets (Timeline, §2.7): each bucket takes the
    /// MAX of each band over its share (a transient never falls between two samples), a shorter payload is linearly
    /// interpolated; the three series carry the Horizon weights (Σ·0.55, (mid+high)·0.6, high·0.75).</summary>
    public static class Timeline
    {
        public const int Points = 360;

        /// <summary>Resample into the three spans (their common length). False — and all zeros — for an empty payload.</summary>
        public static bool Resample(ReadOnlySpan<WaveSample> samples, Span<float> low, Span<float> mid, Span<float> high)
        {
            int cols = Math.Min(low.Length, Math.Min(mid.Length, high.Length)), n = samples.Length;
            low.Clear(); mid.Clear(); high.Clear();
            if (cols == 0 || n == 0) return false;
            for (int c = 0; c < cols; c++)
            {
                float l, m, h;
                if (n >= cols)
                {
                    int from = (int)((long)c * n / cols), to = Math.Max(from + 1, Math.Min(n, (int)((long)(c + 1) * n / cols)));
                    int bl = 0, bm = 0, bh = 0;
                    for (int i = from; i < to; i++)
                    {
                        var s = samples[i];
                        if (s.Low > bl) bl = s.Low;
                        if (s.Mid > bm) bm = s.Mid;
                        if (s.High > bh) bh = s.High;
                    }
                    l = bl / 255f; m = bm / 255f; h = bh / 255f;
                }
                else
                {
                    float f = cols == 1 ? 0f : c * (n - 1f) / (cols - 1f);
                    int i0 = (int)f, i1 = Math.Min(i0 + 1, n - 1);
                    float t = f - i0;
                    WaveSample a = samples[i0], b = samples[i1];
                    l = (a.Low + (b.Low - a.Low) * t) / 255f; m = (a.Mid + (b.Mid - a.Mid) * t) / 255f; h = (a.High + (b.High - a.High) * t) / 255f;
                }
                low[c] = MathF.Min(1f, (l + m + h) * 0.55f);
                mid[c] = MathF.Min(1f, (m + h) * 0.6f);
                high[c] = MathF.Min(1f, h * 0.75f);
            }
            return true;
        }
    }

    /// <summary>The scope line (§2.13): the engine's latency-aligned time-domain window (mono, −1..1) → <see cref="Points"/>
    /// samples 0..1 (0.5 = silence) for a Cartesian SeriesEl and <see cref="RadialPoints"/> radius fractions for a polar one.
    /// Phase-locked to the first rising zero crossing (Winamp's trigger) so a steady tone holds still; a slow running peak
    /// normalises a quiet master. Without a tap: the prototype's spectral synthesis over the 48 bands — an honest
    /// "scope from the spectrum", never a random wave.</summary>
    public static class Oscilloscope
    {
        public const int Points = 512, RadialPoints = 256;
        /// <summary>The window drawn (samples) and how far into the tap the trigger may be searched.</summary>
        public const int Window = 768, TriggerSearch = 256;
        public const float AgcDecay = 0.995f, AgcFloor = 0.05f, MaxGain = 6f, Headroom = 0.9f;
        /// <summary>The polar ring's rest radius and swing (fractions of the half-box), and the seam blend length.</summary>
        public const float RadialBase = 0.62f, RadialSwing = 0.30f;
        public const int SeamBlend = 24;
        /// <summary>The spectral stand-in redraws no faster than the engine publishes a spectrum (~60/s): a 144 Hz frame
        /// clock would otherwise run the 12-sine synthesis over 768 samples more than twice per new picture.</summary>
        public const float SynthesizeMs = 1000f / 60f;

        /// <summary>Fill both lines from a tap window. <paramref name="gain"/> is the user's gain (sensitivity × calm).</summary>
        public static void Fill(ReadOnlySpan<float> wave, Span<float> line, Span<float> radial, ref float agc, float gain, float ticks)
        {
            int n = wave.Length;
            if (n < 4) { Flat(line, radial); return; }
            int start = 0, last = Math.Min(TriggerSearch, n - 2);
            for (int i = 1; i <= last; i++) if (wave[i - 1] < 0f && wave[i] >= 0f) { start = i; break; }
            int window = Math.Min(Window, n - start);
            float peak = 0f;
            for (int i = start; i < start + window; i++) { float a = MathF.Abs(wave[i]); if (a > peak) peak = a; }
            agc = MathF.Max(peak, agc * (ticks == 1f ? AgcDecay : MathF.Pow(AgcDecay, ticks)));
            float g = MathF.Min(MaxGain, Headroom / MathF.Max(agc, AgcFloor)) * gain;
            Resample(wave.Slice(start, window), line, g, 0.5f, 0.5f);
            Resample(wave.Slice(start, window), radial, g, RadialBase, RadialSwing);
            CloseSeam(radial);
        }

        /// <summary>The spectral stand-in: twelve sines weighted by every 4th band (the prototype's synthesis), windowed so
        /// the Cartesian line rests at both ends; the polar ring uses whole harmonics so it closes on itself.</summary>
        public static void Synthesize(ReadOnlySpan<float> bands, float tSec, Span<float> line, Span<float> radial)
        {
            if (bands.Length < Bands.Count) { Flat(line, radial); return; }
            for (int k = 0; k < line.Length; k++)
            {
                float u = k / (float)(line.Length - 1), y = 0f;
                for (int b = 0; b < 12; b++) y += bands[b * 4] * MathF.Sin(u * (3f + b * 4.3f) * MathF.PI + tSec * (2f + b) + b) / (1f + b * 0.35f);
                y *= MathF.Sin(MathF.PI * u) * 0.55f;
                line[k] = 0.5f + 0.5f * Math.Clamp(y, -1f, 1f);
            }
            for (int k = 0; k < radial.Length; k++)
            {
                float u = k / (float)radial.Length * MathF.Tau, y = 0f;
                for (int b = 0; b < 12; b++) y += bands[b * 4] * MathF.Sin(u * (2 + b) + tSec * (1f + b * 0.5f) + b) / (1f + b * 0.35f);
                radial[k] = RadialBase + RadialSwing * Math.Clamp(y * 0.45f, -1f, 1f);
            }
        }

        /// <summary>The rest line: silence (0.5) and the polar ring at its rest radius.</summary>
        public static void Flat(Span<float> line, Span<float> radial)
        {
            line.Fill(0.5f);
            radial.Fill(RadialBase);
        }

        static void Resample(ReadOnlySpan<float> src, Span<float> dst, float gain, float centre, float swing)
        {
            int n = src.Length, m = dst.Length;
            for (int k = 0; k < m; k++)
            {
                float f = m == 1 ? 0f : k * (n - 1f) / (m - 1f);
                int i0 = (int)f, i1 = Math.Min(i0 + 1, n - 1);
                float s = src[i0] + (src[i1] - src[i0]) * (f - i0);
                dst[k] = centre + swing * Math.Clamp(s * gain, -1f, 1f);
            }
        }

        /// <summary>A polar loop must meet itself: the last <see cref="SeamBlend"/> samples ease toward the first one.</summary>
        static void CloseSeam(Span<float> radial)
        {
            int n = radial.Length, b = Math.Min(SeamBlend, n / 4);
            if (b <= 0) return;
            float first = radial[0];
            for (int k = n - b; k < n; k++)
            {
                float w = (k - (n - b) + 1f) / (b + 1f);
                radial[k] += (first - radial[k]) * w;
            }
        }
    }

    /// <summary>The stage's always-on backdrop (Stage.UI.cs Backdrop): four blobs on 28-36 s keyframe loops.</summary>
    public static class Field
    {
        public const int Blobs = 4;
        /// <summary>Keyframe periods per blob (s) and the drift targets as fractions of W/H plus the end scale.</summary>
        public static readonly float[] PeriodSec = [30f, 36f, 33f, 28f];
        public static readonly (float Dx, float Dy, float Scale)[] Drift = [(0.16f, 0.12f, 1.15f), (-0.14f, 0.14f, 0.9f), (0.18f, -0.12f, 1.1f), (-0.15f, -0.13f, 1.2f)];
        public static float BaseOpacity(float low, bool visualizer) => visualizer ? Stage.Tone.BaseFieldVisualizerA : Stage.Tone.BaseFieldA + low * Stage.Tone.BaseFieldBreathA;
    }

    // ── 5. the fold ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What one tick reads. The band/beat spans travel beside it (a readonly record struct cannot hold a span).
    /// <paramref name="HaveLevel"/>: a level-tap RMS is valid this tick (a level lease, no spectrum); <paramref name="Flux"/>:
    /// the last publish's spectral flux (dB); <paramref name="Onset"/>: the engine reported a NEW onset since the last tick
    /// (the clock compares <c>SpectrumInfo.OnsetSequence</c>) with <paramref name="OnsetStrength"/> 0..1;
    /// <paramref name="Ribbons"/>: refill the Aurora ribbon arrays this tick (the clock passes Visualizer mode with an Aurora
    /// stage or the gallery shown) — false skips the 3 × 256-sample fill, the phases still drift.</summary>
    public readonly record struct Input(
        bool Playing, bool Calm, float Sensitivity, bool HaveLive, bool Muted, float Rms,
        long PositionMs, long DurationMs, bool HaveBands, bool HaveBeats, ushort TempoX10, bool Visualizer,
        bool HaveLevel = false, float Flux = 0f, bool Onset = false, float OnsetStrength = 0f, bool Ribbons = true);

    /// <summary>Every scalar the slab writes after a tick. <see cref="Bar"/> is the bar under the playhead (0 before the
    /// first downbeat); <see cref="Downbeat"/> is the bar index of the last downbeat CROSSED while playing (a seek moves
    /// the bar, never the downbeat — a forward seek of exactly one bar included), and <see cref="DownbeatEdge"/> is true
    /// on the tick it was crossed.</summary>
    public readonly record struct Frame(
        float Low, float Mid, float High, float Level, float Kick, int BeatIndex, float BeatPhase, float BaseFieldOp,
        int Bar, float BarPhase, int Downbeat, bool DownbeatEdge, bool HasBars,
        float Progress, float Flux, float Onset, float Energy, byte Section, Source Source);

    /// <summary>THE model: owns every preallocated array; <see cref="Tick"/> allocates nothing.</summary>
    public sealed class Model
    {
        public readonly float[] Target = new float[Bands.Count], Level = new float[Bands.Count], Peak = new float[Bands.Count];
        readonly float[] _hold = new float[Bands.Count];   // in REFERENCE ticks (Bands.RefHz), fractional
        public readonly float[] AuroraLow = new float[Aurora.Points], AuroraMid = new float[Aurora.Points], AuroraHigh = new float[Aurora.Points];
        /// <summary>Ring's ripple ring buffer: each slot's 0..1 age, 1 = none.</summary>
        public readonly float[] RippleAge = [1f, 1f, 1f, 1f];
        float _agc = Bands.AgcFloor, _kick, _flux, _onset, _energy, _sinceRipple = NoRipple;
        float _auroraL, _auroraM, _auroraH;
        int _lastRippleBeat = int.MinValue, _rippleCount;
        // the bar counter over a grid: incremental (a forward play adds the beats it crossed; a backward seek recounts)
        int _gridLength = -1; uint _gridFirst, _gridLast; bool _gridHasBars;
        int _countedTo = -1, _downbeats, _lastDownbeatBeat = -1;
        int _lastBar, _downbeat; bool _barPrimed; long _lastPositionMs;
        // the track's mean kind-237 level (the section's reference), recomputed when the payload changes
        int _meanLength = -1; long _meanDuration; float _trackMean;
        byte _section = Sections.Normal;
        public bool IsSettled { get; private set; } = true;
        const float NoRipple = 1e6f;

        /// <summary><paramref name="liveDb"/>: the 48 dB bands when <see cref="Input.HaveLive"/> (else ignored);
        /// <paramref name="bands"/>: the kind-237 samples or empty; <paramref name="beats"/>: the TrackBeats payload or empty.
        /// <see cref="Input.Rms"/> is the PRE-gain window RMS under a spectrum lease (SpectrumInfo.WindowRms), the level tap's
        /// RMS under a level lease.</summary>
        public Frame Tick(in Input input, ReadOnlySpan<float> liveDb, ReadOnlySpan<WaveSample> bands, ReadOnlySpan<uint> beats, float dtSec)
        {
            float ticks = dtSec * Bands.RefHz;   // every per-tick constant below was authored at 30 Hz
            bool live = input.HaveLive && !input.Muted && liveDb.Length >= Bands.Count;
            bool levelTap = !live && input.HaveLevel && !input.Muted && float.IsFinite(input.Rms);
            float userGain = Bands.Gain(Bands.ClampSensitivity(input.Sensitivity), input.Calm);
            bool playing = input.Playing;

            // 1. the beat: the grid, else the tempo, else nothing (first: the spread's ripple and the pulse read it)
            int beatIndex = 0; float phase = 0f, periodMs = 0f; bool hasBeat, fromGrid = false;
            if (input.HaveBeats && BeatGrid.Phase(beats, input.PositionMs, out beatIndex, out phase, out periodMs))
            {
                hasBeat = fromGrid = true;
                if (beatIndex < 0) { beatIndex = 0; phase = 1f; }                // before the first beat: a spent kick, not a full one
            }
            else if (BeatGrid.TempoPhase(input.TempoX10, input.PositionMs, out phase, out periodMs)) { hasBeat = true; beatIndex = periodMs > 0f ? (int)(input.PositionMs / periodMs) : 0; }
            else { hasBeat = false; beatIndex = 0; phase = 0f; }                // Phase leaves -1 behind on an empty grid

            // 2. the bands and the three energies: live → level tap → kind-237 → the beat → rest
            float low, mid, high, level;
            Source source;
            if (live && playing)
            {
                float frameMax = 0f;
                for (int i = 0; i < Bands.Count; i++) { float u = Bands.Unit(liveDb[i]); Target[i] = u; if (u > frameMax) frameMax = u; }
                float gain = Bands.Agc(ref _agc, frameMax, ticks) * userGain;          // AGC first, then sensitivity/calm (V-U32)
                for (int i = 0; i < Bands.Count; i++) Target[i] = MathF.Min(1f, Target[i] * gain);
                Bands.Follow(Target, Level, input.Calm, ticks);
                low = Bands.Average(Level, 0, 8); mid = Bands.Average(Level, 8, 28); high = Bands.Average(Level, 28, 48);
                level = MathF.Min(1f, MathF.Max(0f, input.Rms * Bands.RmsGain));
                source = Source.Live;
            }
            else if (levelTap && playing)
            {
                // the live level, shaped by the song's own bands when they are known (else an even spread)
                level = MathF.Min(1f, MathF.Max(0f, input.Rms * Bands.RmsGain));
                if (input.HaveBands && Bands.Sample(bands, input.PositionMs, input.DurationMs, out float bl, out float bm, out float bh))
                {
                    float shape = MathF.Min(2f, level / MathF.Max(0.05f, (bl + bm + bh) / 3f));
                    low = MathF.Min(1f, bl * shape * userGain); mid = MathF.Min(1f, bm * shape * userGain); high = MathF.Min(1f, bh * shape * userGain);
                }
                else low = mid = high = MathF.Min(1f, level * userGain);
                Bands.Spread(low, mid, high, beatIndex, Target);
                Bands.Follow(Target, Level, input.Calm, ticks);
                source = Source.Live;
            }
            else if (input.HaveBands && playing && Bands.Sample(bands, input.PositionMs, input.DurationMs, out float pl, out float pm, out float ph))
            {
                low = MathF.Min(1f, pl * userGain); mid = MathF.Min(1f, pm * userGain); high = MathF.Min(1f, ph * userGain);
                level = (pl + pm + ph) / 3f;
                Bands.Spread(low, mid, high, beatIndex, Target);
                Bands.Follow(Target, Level, input.Calm, ticks);
                source = Source.Precomputed;
            }
            else if (hasBeat && playing)
            {
                Bands.Pulse(phase, Target);
                for (int i = 0; i < Bands.Count; i++) Target[i] = MathF.Min(1f, Target[i] * userGain);
                Bands.Follow(Target, Level, input.Calm, ticks);
                low = Bands.Average(Level, 0, 8); mid = Bands.Average(Level, 8, 28); high = Bands.Average(Level, 28, 48);
                level = (low + mid + high) / 3f;
                source = fromGrid ? Source.Precomputed : Source.TempoGrid;    // Spotify's beat grid IS precomputed data
            }
            else
            {
                Bands.Decay(Level, ticks);                                    // paused, or nothing at all: rest
                low = mid = high = level = 0f;
                source = Source.Breath;
            }
            Bands.Peaks(Level, Peak, _hold, ticks);

            // 3. the kick: instant attack, eased release; calm ×0.3
            float kickTarget = hasBeat && playing ? Beat.Kick(phase) * (input.Calm ? Beat.CalmKick : 1f) : 0f;
            _kick = kickTarget > _kick ? kickTarget : _kick + (kickTarget - _kick) * Bands.PerTicks(Beat.KickRelease, ticks);

            // 4. the bar: the grid's downbeats, else every 4th beat
            int bar = 0; float barPhase = 0f; bool hasBars = hasBeat;
            if (fromGrid) (bar, barPhase) = BarOfGrid(beats, beatIndex, phase);
            else if (hasBeat) (bar, barPhase) = Beat.BarOfTempo(beatIndex, phase);
            // an edge is the NEXT bar reached by playing on: a forward seek of exactly one bar moves the playhead a whole bar
            // in one tick, continuous playback at most a beat (or one clamped tick, whichever is longer)
            long stepMs = input.PositionMs - _lastPositionMs;
            bool continuous = stepMs >= 0L && stepMs <= MathF.Max(periodMs, DtMaxSec * 1000f);
            bool edge = hasBars && playing && _barPrimed && continuous && bar == _lastBar + 1;
            if (edge || !_barPrimed) _downbeat = bar;
            _lastBar = bar; _barPrimed = hasBars; _lastPositionMs = input.PositionMs;

            // 5. ripples: an onset under a live spectrum, else each beat (calm: every other one); ≥ 0.15 s apart
            float lifeStep = dtSec / Beat.RippleLifeSec;
            for (int r = 0; r < Beat.Ripples; r++) RippleAge[r] = MathF.Min(1f, RippleAge[r] + lifeStep);
            _sinceRipple = MathF.Min(NoRipple, _sinceRipple + dtSec);
            bool trigger = false;
            if (playing && live) trigger = input.Onset;
            else if (playing && hasBeat && beatIndex != _lastRippleBeat) { trigger = _lastRippleBeat != int.MinValue && phase < 0.5f; }
            if (hasBeat) _lastRippleBeat = beatIndex;
            if (trigger && _sinceRipple >= Beat.RippleMinGapSec)
            {
                _rippleCount++;
                if (!input.Calm || (_rippleCount & 1) == 0) Spawn();
                _sinceRipple = 0f;
            }

            // 6. progress, flux, the onset flash
            float progress = input.DurationMs > 0 ? Math.Clamp(input.PositionMs / (float)input.DurationMs, 0f, 1f) : 0f;
            float fluxTarget = live && playing && float.IsFinite(input.Flux) ? Math.Clamp(input.Flux / Beat.FluxFullDb, 0f, 1f) : 0f;
            _flux += (fluxTarget - _flux) * Bands.PerTicks(fluxTarget > _flux ? Beat.FluxAttack : Beat.FluxRelease, ticks);
            _onset *= ticks == 1f ? Beat.OnsetDecay : MathF.Pow(Beat.OnsetDecay, ticks);
            if (input.Onset && live && playing && !input.Calm) _onset = MathF.Max(_onset, Math.Clamp(input.OnsetStrength, 0f, 1f));
            if (_onset < 0.002f) _onset = 0f;

            // 7. the 4-bar energy ahead of the playhead (kind-237) and its section; without a waveform a slow level follower
            if (input.HaveBands && bands.Length > 0 && input.DurationMs > 0)
            {
                if (bands.Length != _meanLength || input.DurationMs != _meanDuration) { _meanLength = bands.Length; _meanDuration = input.DurationMs; _trackMean = MeanLevel(bands); }
                float windowMs = hasBars && periodMs > 0f ? periodMs * Beat.BeatsPerBar * 4f : Beat.EnergyWindowMs;
                _energy = WindowLevel(bands, input.PositionMs, input.PositionMs + (long)windowMs, input.DurationMs);
                _section = Sections.Next(_section, _energy / MathF.Max(0.02f, _trackMean));
            }
            else
            {
                _energy += (level - _energy) * Bands.PerTicks(0.02f, ticks);
                _section = Sections.Normal;
            }

            // 8. the Aurora ribbons: phases drift with the energy (near-still in silence, frozen when paused); the arrays are
            //    refilled only while something can show them (Input.Ribbons)
            if (playing)
            {
                _auroraL = (_auroraL + dtSec * Aurora.LowSpeed * (Aurora.StillFloor + low)) % (MathF.Tau * 100f);
                _auroraM = (_auroraM + dtSec * Aurora.MidSpeed * (Aurora.StillFloor + mid)) % (MathF.Tau * 100f);
                _auroraH = (_auroraH + dtSec * Aurora.HighSpeed * (Aurora.StillFloor + high)) % (MathF.Tau * 100f);
            }
            if (input.Ribbons) Aurora.Fill(AuroraLow, AuroraMid, AuroraHigh, low, mid, high, _auroraL, _auroraM, _auroraH);

            IsSettled = !playing && Bands.Settled(Level) && _kick < 0.002f && _flux < 0.002f && _onset == 0f && RipplesSpent();
            return new Frame(low, mid, high, level, _kick, beatIndex, phase, Field.BaseOpacity(low, input.Visualizer),
                bar, barPhase, _downbeat, edge, hasBars, progress, _flux, _onset, _energy, _section, source);
        }

        void Spawn()
        {
            int oldest = 0;
            for (int r = 1; r < Beat.Ripples; r++) if (RippleAge[r] > RippleAge[oldest]) oldest = r;
            RippleAge[oldest] = 0f;
        }

        bool RipplesSpent()
        {
            for (int r = 0; r < Beat.Ripples; r++) if (RippleAge[r] < 1f) return false;
            return true;
        }

        /// <summary>The bar under a grid beat from the grid's downbeat marks (counted incrementally), or every 4th beat for a
        /// grid without marks. The phase is the beat's place in its bar over the bar's real beat count.</summary>
        (int Bar, float Phase) BarOfGrid(ReadOnlySpan<uint> beats, int beatIndex, float phase)
        {
            if (beats.Length != _gridLength || beats[0] != _gridFirst || beats[^1] != _gridLast)
            {
                _gridLength = beats.Length; _gridFirst = beats[0]; _gridLast = beats[^1];
                _gridHasBars = false;
                for (int i = 0; i < beats.Length; i++) if (BeatGrid.IsDownbeat(beats[i])) { _gridHasBars = true; break; }
                _countedTo = -1; _downbeats = 0; _lastDownbeatBeat = -1;
            }
            if (!_gridHasBars) return Beat.BarOfTempo(beatIndex, phase);
            int b = Math.Clamp(beatIndex, 0, beats.Length - 1);
            if (b < _countedTo) { _countedTo = -1; _downbeats = 0; _lastDownbeatBeat = -1; }   // a backward seek recounts
            for (int j = _countedTo + 1; j <= b; j++) if (BeatGrid.IsDownbeat(beats[j])) { _downbeats++; _lastDownbeatBeat = j; }
            _countedTo = Math.Max(_countedTo, b);
            if (_lastDownbeatBeat < 0) return (0, Math.Clamp((b + phase) / Beat.BeatsPerBar, 0f, 1f));   // a pickup before the first bar
            int next = -1;
            for (int j = _lastDownbeatBeat + 1; j < beats.Length && j <= _lastDownbeatBeat + 16; j++) if (BeatGrid.IsDownbeat(beats[j])) { next = j; break; }
            int span = next > _lastDownbeatBeat ? next - _lastDownbeatBeat : Beat.BeatsPerBar;
            return (_downbeats - 1, Math.Clamp((b - _lastDownbeatBeat + phase) / span, 0f, 1f));
        }

        static float MeanLevel(ReadOnlySpan<WaveSample> bands)
        {
            long sum = 0;
            for (int i = 0; i < bands.Length; i++) sum += bands[i].Low + bands[i].Mid + bands[i].High;
            return bands.Length == 0 ? 0f : sum / (bands.Length * 3f * 255f);
        }

        /// <summary>The mean kind-237 level over [from, to) (strided to ≤ <see cref="Beat.EnergyMaxSamples"/> samples).</summary>
        static float WindowLevel(ReadOnlySpan<WaveSample> bands, long fromMs, long toMs, long durationMs)
        {
            int n = bands.Length;
            int a = WaveformBands.IndexAt(fromMs, durationMs, n), b = WaveformBands.IndexAt(Math.Min(toMs, durationMs - 1), durationMs, n);
            if (b < a) b = a;
            int stride = Math.Max(1, (b - a + 1) / Beat.EnergyMaxSamples), count = 0;
            float sum = 0f;
            for (int i = a; i <= b; i += stride) { var s = bands[i]; sum += s.Low + s.Mid + s.High; count++; }
            return count == 0 ? 0f : sum / (count * 3f * 255f);
        }
    }

    /// <summary>The NOMINAL step — Design.Cadence.ClockHz, restated so this file stays System-only; a test pins the two. The
    /// clock ticks once per produced frame (Controls.FrameTicker); this is only the first tick's dt (no previous tick to diff).
    /// The model is rate-independent (<see cref="Bands.RefHz"/>).</summary>
    public const int TickHz = 60;
    public const float TickMs = 1000f / TickHz;
    public const float DtMinSec = 0.001f, DtMaxSec = 0.080f;
    public static float DeltaSec(long lastTickMs, long nowMs)
        => lastTickMs == 0L ? TickMs / 1000f : Math.Clamp((nowMs - lastTickMs) / 1000f, DtMinSec, DtMaxSec);
}
