// ── Wavee.Tests/VisualizerModelTests.cs — the visualizer's pure core (Shell/Visualizer.cs) ─────────────────────────────
//
// Pure: `Visualizer.Catalog / Demand / Bands`, the eight faces' arithmetic and the ONE `Model.Tick` take the caller's
// spans and touch no engine, no signal and no table, so nothing here needs a scope. The 30 Hz clock, the slab and the faces
// that bind them are Visualizer.UI.cs (covered by the `--fake` and real-account walks, not a unit test). Plan:
// docs/plans/wavee/fullscreen-flagship-implementation.md §4.8, §5.2 (V-U2, V-U32, V-U35, V-U55).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class VisualizerModelTests
{
    const float Dt = 1f / 30f;

    static ReadOnlySpan<float> NoDb => default;
    static ReadOnlySpan<WaveSample> NoBands => default;
    static ReadOnlySpan<uint> NoBeats => default;

    static Visualizer.Input Inp(bool playing = true, bool calm = false, float sensitivity = 1f, bool live = false, bool muted = false,
        float rms = 0f, long pos = 0, long dur = 180_000, bool bands = false, bool beats = false, ushort tempo = 0, bool viz = true)
        => new(playing, calm, sensitivity, live, muted, rms, pos, dur, bands, beats, tempo, viz);

    static float[] Flat(float db)
    {
        var a = new float[Visualizer.Bands.Count];
        Array.Fill(a, db);
        return a;
    }

    /// <summary>Tick <paramref name="ticks"/> times at the nominal cadence; the last frame.</summary>
    static Visualizer.Frame Run(Visualizer.Model m, in Visualizer.Input input, ReadOnlySpan<float> db, ReadOnlySpan<WaveSample> bands, ReadOnlySpan<uint> beats, int ticks)
    {
        Visualizer.Frame f = default;
        for (int i = 0; i < ticks; i++) f = m.Tick(in input, db, bands, beats, Dt);
        return f;
    }

    /// <summary>The band levels a flat live frame converges to (80 ticks is far past the follower's settling).</summary>
    static float[] Converged(float db, float sensitivity, bool calm = false)
    {
        var m = new Visualizer.Model();
        Run(m, Inp(live: true, rms: 0.1f, sensitivity: sensitivity, calm: calm), Flat(db), NoBands, NoBeats, 80);
        return m.Level;
    }

    static int ArgMax(float[] v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++) if (v[i] > v[best]) best = i;
        return best;
    }

    static WaveSample[] Spiked(int n, int spikeAt)
    {
        var s = new WaveSample[n];
        s[spikeAt] = new WaveSample(255, 255, 255);
        return s;
    }

    // ── catalog ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Visualizer.Kind.Field, Visualizer.Need.Breath, false)]
    [InlineData(Visualizer.Kind.Halo, Visualizer.Need.Spectrum, false)]
    [InlineData(Visualizer.Kind.Horizon, Visualizer.Need.Precomputed, true)]
    [InlineData(Visualizer.Kind.Matrix, Visualizer.Need.Spectrum, false)]
    [InlineData(Visualizer.Kind.Aurora, Visualizer.Need.Spectrum, true)]
    [InlineData(Visualizer.Kind.Spectrum, Visualizer.Need.Spectrum, false)]
    [InlineData(Visualizer.Kind.Pulse, Visualizer.Need.Beats, false)]
    [InlineData(Visualizer.Kind.Tape, Visualizer.Need.Breath, false)]
    public void Catalog_needs_and_series(Visualizer.Kind kind, Visualizer.Need need, bool series)
    {
        Assert.Equal(need, Visualizer.Catalog.NeedsOf(kind));
        Assert.Equal(series, Visualizer.Catalog.UsesSeries(kind));      // Horizon and Aurora only
    }

    [Fact]
    public void Catalog_coerces_stored_ints_and_pins_the_persisted_values()
    {
        Assert.Equal(Visualizer.Catalog.Count, Enum.GetValues<Visualizer.Kind>().Length);

        // the persisted ints (Platform.Keys.StageVisualizer) are append-only
        Visualizer.Kind[] order = [Visualizer.Kind.Field, Visualizer.Kind.Halo, Visualizer.Kind.Horizon, Visualizer.Kind.Matrix,
            Visualizer.Kind.Aurora, Visualizer.Kind.Spectrum, Visualizer.Kind.Pulse, Visualizer.Kind.Tape];
        Assert.Equal(Visualizer.Catalog.Count, order.Length);
        for (int i = 0; i < order.Length; i++)
        {
            Assert.Equal(i, (int)order[i]);
            Assert.Equal(i, Visualizer.Catalog.Coerce(i));
        }

        // anything else is the board's default
        int horizon = (int)Visualizer.Kind.Horizon;
        Assert.Equal(horizon, Visualizer.Catalog.Coerce(99));
        Assert.Equal(horizon, Visualizer.Catalog.Coerce(Visualizer.Catalog.Count));
        Assert.Equal(horizon, Visualizer.Catalog.Coerce(-1));
        Assert.Equal(horizon, Visualizer.Catalog.Coerce(int.MinValue));
        Assert.Equal(horizon, Visualizer.Catalog.Coerce(int.MaxValue));
    }

    // ── demand ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static Visualizer.Tier TierFor(Visualizer.Kind k, bool viz = true, bool stageUp = true, bool playing = true, bool owner = true,
        bool supported = true, bool occluded = false, bool reduced = false)
        => Visualizer.Demand.For(k, viz, stageUp, playing, owner, supported, occluded, reduced);

    [Fact]
    public void Demand_every_closed_gate_holds_no_lease()
    {
        foreach (var k in Enum.GetValues<Visualizer.Kind>())
            foreach (bool viz in new[] { true, false })
            {
                Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, stageUp: false));
                Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, playing: false));
                Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, owner: false));
                Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, supported: false));
                Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, occluded: true));
                Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, reduced: true));
            }
    }

    [Theory]
    [InlineData(Visualizer.Kind.Field, Visualizer.Tier.Level)]
    [InlineData(Visualizer.Kind.Halo, Visualizer.Tier.Spectrum)]
    [InlineData(Visualizer.Kind.Horizon, Visualizer.Tier.None)]
    [InlineData(Visualizer.Kind.Matrix, Visualizer.Tier.Spectrum)]
    [InlineData(Visualizer.Kind.Aurora, Visualizer.Tier.Spectrum)]
    [InlineData(Visualizer.Kind.Spectrum, Visualizer.Tier.Spectrum)]
    [InlineData(Visualizer.Kind.Pulse, Visualizer.Tier.None)]
    [InlineData(Visualizer.Kind.Tape, Visualizer.Tier.Level)]
    public void Demand_tiers_follow_the_face_in_visualizer_mode_and_are_level_outside_it(Visualizer.Kind kind, Visualizer.Tier expected)
    {
        Assert.Equal(expected, TierFor(kind));
        // outside Visualizer mode only the base Field's breath is visible, whatever face is selected (V-U55)
        Assert.Equal(Visualizer.Tier.Level, TierFor(kind, viz: false));
    }

    [Fact]
    public void Ticks_run_while_settling_even_when_paused_and_stop_when_occluded_or_reduced()
    {
        Assert.True(Visualizer.Demand.Ticks(stageUp: true, playing: true, settled: true, occluded: false, reduced: false));
        Assert.True(Visualizer.Demand.Ticks(stageUp: true, playing: false, settled: false, occluded: false, reduced: false));   // a release tail
        Assert.False(Visualizer.Demand.Ticks(stageUp: true, playing: false, settled: true, occluded: false, reduced: false));
        Assert.False(Visualizer.Demand.Ticks(stageUp: false, playing: true, settled: false, occluded: false, reduced: false));
        Assert.False(Visualizer.Demand.Ticks(stageUp: true, playing: true, settled: false, occluded: true, reduced: false));
        Assert.False(Visualizer.Demand.Ticks(stageUp: true, playing: true, settled: false, occluded: false, reduced: true));
    }

    // ── bands ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bands_unit_is_the_raw_floor_to_ceiling_window()
    {
        Assert.Equal(0f, Visualizer.Bands.Unit(-60f));
        Assert.Equal(1f, Visualizer.Bands.Unit(-6f));
        Assert.Equal(0.5f, Visualizer.Bands.Unit(-33f), 5);
        Assert.Equal(0f, Visualizer.Bands.Unit(-120f));                  // below the floor
        Assert.Equal(1f, Visualizer.Bands.Unit(6f));                     // above the ceiling
    }

    [Fact]
    public void Bands_gain_and_sensitivity_clamp()
    {
        Assert.Equal(0.55f, Visualizer.Bands.Gain(1f, calm: true));
        Assert.Equal(1.5f, Visualizer.Bands.Gain(1.5f, calm: false));
        Assert.Equal(1f, Visualizer.Bands.Gain(1f, calm: false));

        Assert.Equal(Visualizer.Bands.MaxSensitivity, Visualizer.Bands.ClampSensitivity(9f));
        Assert.Equal(Visualizer.Bands.MinSensitivity, Visualizer.Bands.ClampSensitivity(0f));
        Assert.Equal(Visualizer.Bands.MinSensitivity, Visualizer.Bands.ClampSensitivity(-1f));
        Assert.Equal(1.2f, Visualizer.Bands.ClampSensitivity(1.2f));
        Assert.Equal(1f, Visualizer.Bands.ClampSensitivity(float.NaN));            // a hand-edited value is a sane one
        Assert.Equal(1f, Visualizer.Bands.ClampSensitivity(float.PositiveInfinity));
    }

    [Fact]
    public void Bands_follow_moves_fast_up_and_slow_down()
    {
        float[] level = [0f, 1f];
        Visualizer.Bands.Follow(new[] { 1f, 0f }, level, calm: false);
        Assert.Equal(0.55f, level[0], 5);                                // attack 0.55
        Assert.Equal(0.9f, level[1], 5);                                 // release 0.10

        level = [0f, 1f];
        Visualizer.Bands.Follow(new[] { 1f, 0f }, level, calm: true);
        Assert.Equal(0.25f, level[0], 5);                                // the calm attack
        Assert.Equal(0.9f, level[1], 5);                                 // the release does not change

        // a shorter target never reads or writes past either span
        float[] longer = [0.5f, 0.5f, 0.5f];
        Visualizer.Bands.Follow(new[] { 1f }, longer, calm: false);
        Assert.Equal(0.5f, longer[1]);
        Assert.Equal(0.5f, longer[2]);
    }

    [Fact]
    public void Bands_decay_releases_toward_the_floor()
    {
        float[] level = [1f, 0.5f, 0f];
        Visualizer.Bands.Decay(level);
        Assert.Equal(0.9f, level[0], 5);
        Assert.Equal(0.45f, level[1], 5);
        Assert.Equal(0f, level[2]);
    }

    [Fact]
    public void Bands_peaks_hold_ten_ticks_then_fall_and_never_sit_below_the_band()
    {
        float[] level = [0.8f];
        float[] peak = [0.5f];
        int[] hold = [0];

        Visualizer.Bands.Peaks(level, peak, hold);                       // the band rose past the cap: the cap follows and holds
        Assert.Equal(0.8f, peak[0]);
        Assert.Equal(Visualizer.Bands.PeakHoldTicks, hold[0]);

        level[0] = 0.2f;
        for (int i = 0; i < Visualizer.Bands.PeakHoldTicks; i++)
        {
            Visualizer.Bands.Peaks(level, peak, hold);
            Assert.Equal(0.8f, peak[0]);                                 // held
        }
        Assert.Equal(0, hold[0]);
        Visualizer.Bands.Peaks(level, peak, hold);                       // the eleventh tick: the fall begins
        Assert.Equal(0.8f - Visualizer.Bands.PeakFall, peak[0], 5);

        level[0] = 0.79f; peak[0] = 0.8f; hold[0] = 0;                   // the fall stops at the band
        Visualizer.Bands.Peaks(level, peak, hold);
        Assert.Equal(0.79f, peak[0]);
    }

    [Fact]
    public void Bands_agc_is_capped_floored_and_releases_slowly()
    {
        float agc = Visualizer.Bands.AgcFloor;
        Assert.Equal(Visualizer.Bands.AgcMaxGain, Visualizer.Bands.Agc(ref agc, 0.1f));    // a quiet master: the cap
        Assert.Equal(1f, Visualizer.Bands.Agc(ref agc, 1f));                               // a full-scale frame needs no gain

        // the running maximum decays 0.995 per step: after a long silence the gain is back at its cap
        float g = 0f;
        for (int i = 0; i < 400; i++) g = Visualizer.Bands.Agc(ref agc, 0f);
        Assert.Equal(Visualizer.Bands.AgcMaxGain, g);

        // for any frame in [0, 1] the gain stays within [1, 2]
        for (float frameMax = 0f; frameMax <= 1f; frameMax += 0.1f)
        {
            g = Visualizer.Bands.Agc(ref agc, frameMax);
            Assert.InRange(g, 1f, Visualizer.Bands.AgcMaxGain);
        }
    }

    [Fact]
    public void Bands_average_and_settled()
    {
        float[] v = [0.1f, 0.2f, 0.3f, 0.4f];
        Assert.Equal(0.25f, Visualizer.Bands.Average(v, 1, 3), 5);
        Assert.Equal(0.35f, Visualizer.Bands.Average(v, 2, 100), 5);     // `to` is clamped to the span
        Assert.Equal(0f, Visualizer.Bands.Average(v, 3, 3));
        Assert.Equal(0f, Visualizer.Bands.Average(v, 4, 2));

        float[] quiet = [0.004f, 0f, 0.005f];
        Assert.True(Visualizer.Bands.Settled(quiet));
        quiet[1] = 0.006f;
        Assert.False(Visualizer.Bands.Settled(quiet));
    }

    // ── the fold: bands ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Model_applies_sensitivity_after_the_agc()
    {
        // 1) a quiet frame (every band -50 dB, unit 0.185): the AGC is at its 2x cap, so the user's gain scales the
        //    normalised frame by exactly 1.5 — nothing is clamped yet
        float[] one = Converged(-50f, 1f), more = Converged(-50f, 1.5f);
        for (int i = 0; i < one.Length; i++)
        {
            Assert.True(one[i] > 0.3f);
            Assert.Equal(1.5f, more[i] / one[i], 3);
        }

        // 2) a loud frame (-20 dB, unit 0.741): the AGC normalises it to the ceiling, and a user gain of 0.5 then takes
        //    EXACTLY half. Were the gain applied BEFORE the AGC, the AGC would cancel it (V-U32).
        float[] full = Converged(-20f, 1f), half = Converged(-20f, 0.5f);
        for (int i = 0; i < full.Length; i++)
        {
            Assert.InRange(full[i], 0.99f, 1f);
            Assert.Equal(0.5f, half[i] / full[i], 3);
        }

        // 3) the user's gain never pushes a band past the ceiling
        float[] clamped = Converged(-40f, 1.5f);
        for (int i = 0; i < clamped.Length; i++) Assert.InRange(clamped[i], 0.999f, 1f);
    }

    [Fact]
    public void Model_live_bands_move_and_release_to_the_floor()
    {
        var ramp = new float[Visualizer.Bands.Count];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = -60f + 54f * i / (ramp.Length - 1);

        var m = new Visualizer.Model();
        Assert.True(m.IsSettled);                                        // a fresh model has nothing to draw
        var f = Run(m, Inp(live: true, rms: 0.2f), ramp, NoBands, NoBeats, 20);
        Assert.Equal(Visualizer.Source.Live, f.Source);
        Assert.False(m.IsSettled);                                       // playing is never settled
        Assert.Equal(0f, m.Level[0]);
        Assert.True(m.Level[24] > m.Level[0]);
        Assert.True(m.Level[47] > m.Level[24]);
        Assert.True(m.Level[47] > 0.95f);
        Assert.True(m.Peak[47] >= m.Level[47]);                          // the cap rides at or above the band

        // paused with no frame: every band releases to the floor, then the whole model settles (the reels coast to a stop last)
        Run(m, Inp(playing: false), NoDb, NoBands, NoBeats, 60);
        Assert.True(Visualizer.Bands.Settled(m.Level));
        Run(m, Inp(playing: false), NoDb, NoBands, NoBeats, 200);
        Assert.True(m.IsSettled);
    }

    [Fact]
    public void Model_treats_muted_and_short_frames_as_no_frame()
    {
        var live = Flat(-30f);
        var m = new Visualizer.Model();
        Run(m, Inp(live: true, rms: 0.1f), live, NoBands, NoBeats, 30);
        Assert.True(m.Level[10] > 0.5f);

        // muted: the engine still publishes, but the UI releases the bands through the normal follower
        var f = Run(m, Inp(live: true, muted: true, rms: 0.1f), live, NoBands, NoBeats, 80);
        Assert.True(Visualizer.Bands.Settled(m.Level));
        Assert.Equal(Visualizer.Source.Breath, f.Source);

        // a frame shorter than the band count is not a live frame
        var short47 = new float[Visualizer.Bands.Count - 1];
        Array.Fill(short47, -10f);
        var m2 = new Visualizer.Model();
        f = Run(m2, Inp(live: true, rms: 0.1f), short47, NoBands, NoBeats, 30);
        Assert.Equal(Visualizer.Source.Breath, f.Source);
        Assert.True(Visualizer.Bands.Settled(m2.Level));
    }

    [Fact]
    public void Model_never_invents_bands_without_a_live_frame()
    {
        var samples = new WaveSample[100];
        Array.Fill(samples, new WaveSample(200, 100, 50));
        var m = new Visualizer.Model();
        var f = Run(m, Inp(bands: true, pos: 90_000), NoDb, samples, NoBeats, 30);

        Assert.All(m.Level, v => Assert.True(v <= 0.005f));              // the per-band arrays stay at the floor (Halo/Matrix/Spectrum rest)
        Assert.Equal(Visualizer.Source.Precomputed, f.Source);
        Assert.Equal(200f / 255f, f.Low, 4);                             // while the three energies come from the precomputed bands
        Assert.Equal(100f / 255f, f.Mid, 4);
        Assert.Equal(50f / 255f, f.High, 4);
        Assert.Equal((200f + 100f + 50f) / 3f / 255f, f.Level, 4);
    }

    [Fact]
    public void Model_source_ladder()
    {
        var live = Flat(-30f);
        var wave = new WaveSample[50];
        Array.Fill(wave, new WaveSample(100, 100, 100));

        Assert.Equal(Visualizer.Source.Live, Run(new Visualizer.Model(), Inp(live: true, rms: 0.1f), live, NoBands, NoBeats, 1).Source);
        Assert.Equal(Visualizer.Source.Precomputed, Run(new Visualizer.Model(), Inp(bands: true), NoDb, wave, NoBeats, 1).Source);
        Assert.Equal(Visualizer.Source.TempoGrid, Run(new Visualizer.Model(), Inp(tempo: 1200), NoDb, NoBands, NoBeats, 1).Source);
        Assert.Equal(Visualizer.Source.Breath, Run(new Visualizer.Model(), Inp(), NoDb, NoBands, NoBeats, 1).Source);

        // live beats everything; the bands beat the tempo (the tempo only ever fills in for nothing)
        Assert.Equal(Visualizer.Source.Live, Run(new Visualizer.Model(), Inp(live: true, rms: 0.1f, bands: true, tempo: 1200), live, wave, NoBeats, 1).Source);
        Assert.Equal(Visualizer.Source.Precomputed, Run(new Visualizer.Model(), Inp(bands: true, tempo: 1200), NoDb, wave, NoBeats, 1).Source);

        // paused: a live frame is not live (it is stale by definition), and the bands do not play
        Assert.Equal(Visualizer.Source.Breath, Run(new Visualizer.Model(), Inp(playing: false, live: true, bands: true), live, wave, NoBeats, 1).Source);
    }

    // ── the fold: beats ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Model_beats_from_the_grid_then_the_tempo()
    {
        uint[] grid = new uint[40];
        for (int i = 0; i < grid.Length; i++) grid[i] = (uint)(i * 500);          // 120 BPM from 0 ms

        var f = Run(new Visualizer.Model(), Inp(pos: 250, beats: true), NoDb, NoBands, grid, 1);
        Assert.Equal(0, f.BeatIndex);
        Assert.Equal(0.5f, f.BeatPhase, 5);
        Assert.Equal(MathF.Exp(-3f), f.Kick, 5);                         // Kick(phase) = e^(-6 phase)
        Assert.Equal(Visualizer.Pulse.CoverScale(f.Kick), f.BeatScale);

        f = Run(new Visualizer.Model(), Inp(pos: 1_750, beats: true), NoDb, NoBands, grid, 1);
        Assert.Equal(3, f.BeatIndex);
        Assert.Equal(0.5f, f.BeatPhase, 5);
        Assert.Equal(Visualizer.Source.Precomputed, f.Source);           // a grid with no bands is still Spotify's data, not idle

        // before the grid's first beat the kick is spent, not full
        uint[] late = new uint[40];
        for (int i = 0; i < late.Length; i++) late[i] = (uint)(400 + i * 500);
        f = Run(new Visualizer.Model(), Inp(pos: 100, beats: true), NoDb, NoBands, late, 1);
        Assert.Equal(0, f.BeatIndex);
        Assert.Equal(1f, f.BeatPhase);
        Assert.True(f.Kick < 0.01f);

        // a tempo of 120 BPM (x10 = 1200) with no grid lands on the same phase, and says where it came from
        f = Run(new Visualizer.Model(), Inp(pos: 250, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(0, f.BeatIndex);
        Assert.Equal(0.5f, f.BeatPhase, 5);
        Assert.Equal(MathF.Exp(-3f), f.Kick, 5);
        Assert.Equal(Visualizer.Source.TempoGrid, f.Source);

        f = Run(new Visualizer.Model(), Inp(pos: 1_750, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(3, f.BeatIndex);

        // the grid beats the tempo: a 60 BPM tempo would read phase 0.75 here
        f = Run(new Visualizer.Model(), Inp(pos: 750, beats: true, tempo: 600), NoDb, NoBands, grid, 1);
        Assert.Equal(1, f.BeatIndex);
        Assert.Equal(0.5f, f.BeatPhase, 5);

        // an empty grid falls through to the tempo
        f = Run(new Visualizer.Model(), Inp(pos: 250, beats: true, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(0.5f, f.BeatPhase, 5);

        // neither: no beat, no kick, and never a negative index
        f = Run(new Visualizer.Model(), Inp(pos: 250, beats: true), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(0, f.BeatIndex);
        Assert.Equal(0f, f.BeatPhase);
        Assert.Equal(0f, f.Kick);
        Assert.Equal(1f, f.BeatScale);

        // paused: the phase is still reported (the rings hold their pose) but nothing kicks
        f = Run(new Visualizer.Model(), Inp(playing: false, pos: 250, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(0.5f, f.BeatPhase, 5);
        Assert.Equal(0f, f.Kick);
    }

    [Fact]
    public void Model_kick_attacks_instantly_and_releases_smoothly()
    {
        var m = new Visualizer.Model();
        var f = Run(m, Inp(pos: 0, tempo: 1200), NoDb, NoBands, NoBeats, 1);   // phase 0: the kick is at its peak
        Assert.Equal(1f, f.Kick, 5);

        f = Run(m, Inp(pos: 250, tempo: 1200), NoDb, NoBands, NoBeats, 1);     // the target fell to e^-3: the follower eases down
        Assert.True(f.Kick < 1f);
        Assert.True(f.Kick > MathF.Exp(-3f));
    }

    [Fact]
    public void Pulse_rings_are_a_beat_apart_and_each_four_beats_long()
    {
        foreach (float phase in new[] { 0f, 0.25f, 0.5f, 0.99f })
            for (int beat = 0; beat < 8; beat++)
            {
                float sum = 0f;
                for (int r = 0; r < Visualizer.Pulse.Rings; r++)
                {
                    float p = Visualizer.Pulse.RingProgress(beat, phase, r);
                    Assert.InRange(p, 0f, 0.9999f);
                    sum += p;
                    // a beat apart: ring r now is where ring r+1 was one beat ago
                    if (r < Visualizer.Pulse.Rings - 1)
                        Assert.Equal(p, Visualizer.Pulse.RingProgress(beat - 1, phase, r + 1), 5);
                }
                Assert.Equal((0f + 1f + 2f + 3f + Visualizer.Pulse.Rings * phase) / Visualizer.Pulse.Rings, sum, 4);
            }

        Assert.Equal(0.6f, Visualizer.Pulse.RingOpacity(0f));
        Assert.Equal(0f, Visualizer.Pulse.RingOpacity(1f));
        Assert.Equal(1f, Visualizer.Pulse.RingScale(0f));
        Assert.Equal(2.3f, Visualizer.Pulse.RingScale(1f), 5);
        Assert.Equal(1f, Visualizer.Pulse.Kick(0f));
        Assert.True(Visualizer.Pulse.Kick(0.5f) < Visualizer.Pulse.Kick(0.25f));
        Assert.Equal(1.025f, Visualizer.Pulse.CoverScale(1f), 5);
        Assert.Equal(1f, Visualizer.Pulse.CoverScale(0f));
        Assert.Equal(0.14f, Visualizer.Pulse.GlowOpacity(0f), 5);
    }

    // ── the fold: series ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Model_horizon_window_is_12_seconds_about_the_playhead()
    {
        // a 3-minute track; one spike at 10 s. The window is indexed by DURATION, so the 9,000-hop answer (spike at index 500)
        // and the same answer max-pooled onto 4,096 (spike at index 227) draw the same picture (V-D17).
        foreach (var (n, spike) in new[] { (9_000, 500), (WaveformBands.MaxSamples, 227) })
        {
            var samples = Spiked(n, spike);
            var m = new Visualizer.Model();

            Run(m, Inp(pos: 10_000, dur: 180_000, bands: true), NoDb, samples, NoBeats, 1);
            Assert.Equal(90, ArgMax(m.HorizonLow));                      // the playhead is the window's centre
            Assert.Equal(1f, m.HorizonLow[90]);
            Assert.Equal(0f, m.HorizonLow[89]);
            Assert.Equal(0f, m.HorizonLow[91]);
            Assert.Equal(1f, m.HorizonMid[90]);
            Assert.Equal(0.75f, m.HorizonHigh[90]);                      // high·0.75

            Run(m, Inp(pos: 4_000, dur: 180_000, bands: true), NoDb, samples, NoBeats, 1);
            Assert.Equal(180, ArgMax(m.HorizonLow));                     // 6 s ahead of the playhead: the right edge

            Run(m, Inp(pos: 16_000, dur: 180_000, bands: true), NoDb, samples, NoBeats, 1);
            Assert.Equal(0, ArgMax(m.HorizonLow));                       // 6 s behind: the left edge
            Assert.Equal(1f, m.HorizonLow[0]);
        }
    }

    [Fact]
    public void Horizon_is_zero_outside_the_track_and_flat_without_a_payload()
    {
        var samples = new WaveSample[1_000];
        Array.Fill(samples, new WaveSample(100, 100, 100));
        var m = new Visualizer.Model();

        Run(m, Inp(pos: 0, dur: 180_000, bands: true), NoDb, samples, NoBeats, 1);          // before the start: the left half is 0
        Assert.Equal(0f, m.HorizonLow[89]);
        Assert.True(m.HorizonLow[90] > 0f);
        Assert.True(m.HorizonLow[180] > 0f);

        Run(m, Inp(pos: 180_000, dur: 180_000, bands: true), NoDb, samples, NoBeats, 1);    // at the end: the right half is 0
        Assert.True(m.HorizonLow[89] > 0f);
        Assert.Equal(0f, m.HorizonLow[90]);
        Assert.Equal(0f, m.HorizonLow[180]);

        // no payload (an empty edge) or an unknown duration ⇒ a flat hairline, and a stale buffer is cleared
        var low = new float[Visualizer.Horizon.Points]; var mid = new float[Visualizer.Horizon.Points]; var high = new float[Visualizer.Horizon.Points];
        Array.Fill(low, 5f); Array.Fill(mid, 5f); Array.Fill(high, 5f);
        Visualizer.Horizon.Fill(ReadOnlySpan<WaveSample>.Empty, 1_000, 180_000, low, mid, high);
        Assert.All(low, v => Assert.Equal(0f, v));
        Assert.All(mid, v => Assert.Equal(0f, v));
        Assert.All(high, v => Assert.Equal(0f, v));

        Array.Fill(low, 5f);
        Visualizer.Horizon.Fill(samples, 1_000, 0, low, mid, high);
        Assert.All(low, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Horizon_series_weights_are_the_prototypes()
    {
        // (0.2, 0.4, 0.6): Σ·0.55 = 0.66, (mid+high)·0.6 = 0.6, high·0.75 = 0.45; a single sample covers the whole track
        WaveSample[] one = [new(51, 102, 153)];
        var low = new float[Visualizer.Horizon.Points]; var mid = new float[Visualizer.Horizon.Points]; var high = new float[Visualizer.Horizon.Points];
        Visualizer.Horizon.Fill(one, 90_000, 180_000, low, mid, high);
        Assert.Equal(0.66f, low[90], 4);
        Assert.Equal(0.6f, mid[90], 4);
        Assert.Equal(0.45f, high[90], 4);

        Assert.Equal(181, Visualizer.Horizon.Points);
        Assert.Equal(12_000f, Visualizer.Horizon.SpanMs);
        Assert.Equal(0.92f, Visualizer.Horizon.Amplitude);

        // a full-scale sample saturates the sums
        WaveSample[] loud = [new(255, 255, 255)];
        Visualizer.Horizon.Fill(loud, 90_000, 180_000, low, mid, high);
        Assert.Equal(1f, low[90]);
        Assert.Equal(1f, mid[90]);
        Assert.Equal(0.75f, high[90]);
    }

    [Fact]
    public void Model_aurora_drifts_even_at_rest()
    {
        var m = new Visualizer.Model();
        Run(m, Inp(playing: false), NoDb, NoBands, NoBeats, 1);
        float[] first = (float[])m.AuroraLow.Clone();
        Run(m, Inp(playing: false), NoDb, NoBands, NoBeats, 1);
        Assert.NotEqual(first, m.AuroraLow);                             // the t terms keep moving with nothing playing
        Assert.All(m.AuroraLow, v => Assert.InRange(v, 0f, 1f));
        Assert.All(m.AuroraMid, v => Assert.InRange(v, 0f, 1f));
        Assert.All(m.AuroraHigh, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void Aurora_louder_bands_swing_further()
    {
        var quiet = new float[Visualizer.Aurora.Points]; var loud = new float[Visualizer.Aurora.Points];
        var mid = new float[Visualizer.Aurora.Points]; var high = new float[Visualizer.Aurora.Points];
        Visualizer.Aurora.Fill(quiet, mid, high, 0f, 0f, 0f, 1f);
        Visualizer.Aurora.Fill(loud, mid, high, 1f, 1f, 1f, 1f);
        Assert.True(Swing(loud) > Swing(quiet));
        Assert.All(loud, v => Assert.InRange(v, 0f, 1f));

        static float Swing(float[] v)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (float x in v) { if (x < lo) lo = x; if (x > hi) hi = x; }
            return hi - lo;
        }
    }

    // ── the fold: tape, field, calm ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tape_reels_conserve_area()
    {
        var (l0, r0) = Visualizer.Tape.Radii(0f);
        Assert.Equal(Visualizer.Tape.R1, l0, 3);                         // a full left pack, an empty right one
        Assert.Equal(Visualizer.Tape.R0, r0, 3);
        var (l1, r1) = Visualizer.Tape.Radii(1f);
        Assert.Equal(Visualizer.Tape.R0, l1, 3);
        Assert.Equal(Visualizer.Tape.R1, r1, 3);

        float area = l0 * l0 + r0 * r0;                                  // r² trades linearly: the SUM of areas is constant
        for (float p = 0f; p <= 1f; p += 0.1f)
        {
            var (l, r) = Visualizer.Tape.Radii(p);
            Assert.Equal(area, l * l + r * r, 1);
        }

        Assert.Equal(Visualizer.Tape.Radii(0f), Visualizer.Tape.Radii(-3f));      // progress is clamped
        Assert.Equal(Visualizer.Tape.Radii(1f), Visualizer.Tape.Radii(7f));

        // the hub turns Speed/radius rad/s: the small reel is faster
        Assert.Equal(70f / 35f * 57.29578f / 30f, Visualizer.Tape.AngleStepDeg(70f, 35f, Dt), 3);
        Assert.True(Visualizer.Tape.AngleStepDeg(70f, 34f, Dt) > Visualizer.Tape.AngleStepDeg(70f, 128f, Dt));
        Assert.Equal(0f, Visualizer.Tape.AngleStepDeg(0f, 34f, Dt));
    }

    [Fact]
    public void Tape_meter_and_zones()
    {
        Assert.Equal(0, Visualizer.Tape.Lit(0f));
        Assert.Equal(16, Visualizer.Tape.Lit(0.5f));
        Assert.Equal(Visualizer.Tape.MeterCells, Visualizer.Tape.Lit(1f));
        Assert.Equal(Visualizer.Tape.MeterCells, Visualizer.Tape.Lit(5f));      // clamped
        Assert.Equal(0, Visualizer.Tape.Lit(-1f));

        for (int c = 0; c < Visualizer.Tape.MeterCells; c++)
        {
            int zone = Visualizer.Tape.Zone(c);
            Assert.InRange(zone, 0, 2);
            if (c > 0) Assert.True(zone >= Visualizer.Tape.Zone(c - 1));        // blue, then green, then orange
        }
        Assert.Equal(0, Visualizer.Tape.Zone(11));
        Assert.Equal(1, Visualizer.Tape.Zone(12));
        Assert.Equal(1, Visualizer.Tape.Zone(16));
        Assert.Equal(2, Visualizer.Tape.Zone(17));
        Assert.Equal(2, Visualizer.Tape.Zone(19));
    }

    [Fact]
    public void Model_tape_reels_turn_and_coast_to_exactly_zero()
    {
        var m = new Visualizer.Model();
        var playing = Run(m, Inp(pos: 0, dur: 180_000), NoDb, NoBands, NoBeats, 30);
        Assert.Equal(Visualizer.Tape.R1, playing.ReelL, 3);              // progress 0: a full left pack
        Assert.Equal(Visualizer.Tape.R0, playing.ReelR, 3);
        Assert.True(playing.AngleL > 0f);
        Assert.True(playing.AngleR > playing.AngleL);                    // the small reel turns faster
        Assert.False(m.IsSettled);

        // paused: the speed eases to exactly 0, and only then is the model settled
        Visualizer.Frame f = default;
        for (int i = 0; i < 400 && !m.IsSettled; i++) f = m.Tick(Inp(playing: false, pos: 0), NoDb, NoBands, NoBeats, Dt);
        Assert.True(m.IsSettled);

        var next = m.Tick(Inp(playing: false, pos: 0), NoDb, NoBands, NoBeats, Dt);
        Assert.Equal(f.AngleL, next.AngleL);                             // the hubs have stopped
        Assert.Equal(f.AngleR, next.AngleR);

        // mid-track the packs have traded evenly
        var mid = Run(new Visualizer.Model(), Inp(pos: 90_000, dur: 180_000), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(mid.ReelL, mid.ReelR, 3);

        // a live frame lights the meter from the pre-gain RMS: 0.1 → level 0.35 → 11 of 20 cells
        var lit = Run(new Visualizer.Model(), Inp(live: true, rms: 0.1f), Flat(-30f), NoBands, NoBeats, 1);
        Assert.Equal(0.35f, lit.Level, 4);
        Assert.Equal(11, lit.MeterLit);
    }

    [Fact]
    public void Model_calm_cuts_the_gain_and_the_kick()
    {
        // the gain: the calm user gain is 0.55 of the normal one (the AGC is at its cap on a quiet frame, so nothing clamps)
        float[] normal = Converged(-50f, 1f), calm = Converged(-50f, 1f, calm: true);
        for (int i = 0; i < normal.Length; i++) Assert.Equal(Visualizer.Bands.CalmGain, calm[i] / normal[i], 3);

        // the kick: x0.3, and the cover pulses less
        var n = Run(new Visualizer.Model(), Inp(pos: 0, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        var c = Run(new Visualizer.Model(), Inp(pos: 0, tempo: 1200, calm: true), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(1f, n.Kick, 5);
        Assert.Equal(0.3f, c.Kick, 5);
        Assert.True(c.BeatScale < n.BeatScale);
    }

    [Fact]
    public void Model_frame_carries_the_faces_scalars()
    {
        // live: low / mid / high are the band averages over 0-8, 8-28 and 28-48; the glow and face opacity follow `low`
        var m = new Visualizer.Model();
        var f = Run(m, Inp(live: true, rms: 0.1f, viz: true), Flat(-6f), NoBands, NoBeats, 40);
        Assert.Equal(1f, f.Low, 3);
        Assert.Equal(1f, f.Mid, 3);
        Assert.Equal(1f, f.High, 3);
        Assert.Equal(Visualizer.Field.FaceOpacity(f.Low), f.FaceFieldOp);
        Assert.Equal(Visualizer.Halo.GlowOpacity(f.Low), f.GlowOp);
        Assert.Equal(Visualizer.Field.BaseOpacity(f.Low, true), f.BaseFieldOp);

        var plain = Run(new Visualizer.Model(), Inp(live: true, rms: 0.1f, viz: false), Flat(-6f), NoBands, NoBeats, 40);
        Assert.Equal(Visualizer.Field.BaseOpacity(plain.Low, false), plain.BaseFieldOp);
        Assert.True(plain.BaseFieldOp < f.BaseFieldOp);                  // the base Field is near-full under a face, breathing under a pane
    }

    // ── allocation and cadence ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Model_tick_allocates_nothing()
    {
        var m = new Visualizer.Model();
        var db = new float[Visualizer.Bands.Count];
        for (int i = 0; i < db.Length; i++) db[i] = -50f + i * 0.5f;
        var wave = new WaveSample[WaveformBands.MaxSamples];
        for (int i = 0; i < wave.Length; i++) wave[i] = new WaveSample((byte)(i & 255), (byte)((i * 3) & 255), (byte)((i * 7) & 255));
        var grid = new uint[400];
        for (int i = 0; i < grid.Length; i++) grid[i] = (uint)(i * 500);

        var input = Inp(live: true, rms: 0.1f, pos: 1_000, bands: true, beats: true, tempo: 1200);
        Visualizer.Frame sink = default;
        for (int i = 0; i < 200; i++)                                    // warm
        {
            input = input with { PositionMs = 1_000 + i * 33 };
            sink = m.Tick(in input, db, wave, grid, Dt);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            input = input with { PositionMs = 1_000 + i * 33 };
            sink = m.Tick(in input, db, wave, grid, Dt);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
        Assert.True(sink.Level >= 0f);                                   // the frames are consumed, so nothing is elided
    }

    [Fact]
    public void TickHz_matches_Design_Cadence()
    {
        Assert.Equal(Design.Cadence.PluggedLoopHz, Visualizer.TickHz);
        Assert.Equal(1000f / 30f, Visualizer.TickMs, 4);
    }

    [Fact]
    public void DeltaSec_is_nominal_on_the_first_tick_and_clamped_after()
    {
        Assert.Equal(Visualizer.TickMs / 1000f, Visualizer.DeltaSec(0L, 5_000L), 6);        // no previous tick: the nominal step
        Assert.Equal(0.033f, Visualizer.DeltaSec(10_000L, 10_033L), 5);
        Assert.Equal(Visualizer.DtMinSec, Visualizer.DeltaSec(10_000L, 10_000L));           // a zero or negative step is floored
        Assert.Equal(Visualizer.DtMinSec, Visualizer.DeltaSec(10_000L, 9_000L));
        Assert.Equal(Visualizer.DtMaxSec, Visualizer.DeltaSec(10_000L, 20_000L));           // a hitch is capped, not integrated
    }

    // ── the index maps ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Halo_bars_mirror_about_the_top_and_stay_in_range()
    {
        for (int j = 0; j < Visualizer.Halo.Bars; j++)
        {
            Assert.InRange(Visualizer.Halo.BandOf(j), 0, Visualizer.Bands.Count - 1);
            Assert.Equal(Visualizer.Halo.BandOf(j), Visualizer.Halo.BandOf(Visualizer.Halo.Bars - 1 - j));   // mirrored about the top
            Assert.InRange(Visualizer.Halo.MixOf(j), 0f, 1f);
            Assert.InRange(Visualizer.Halo.AngleDeg(j), 0f, 360f);
        }
        Assert.Equal(0, Visualizer.Halo.BandOf(0));
        Assert.Equal(40, Visualizer.Halo.BandOf(35));                    // round(35/35 · 47 · 0.85)
        Assert.Equal(0f, Visualizer.Halo.AngleDeg(0));
        Assert.Equal(180f, Visualizer.Halo.AngleDeg(36));
        Assert.Equal(0.1f, Visualizer.Halo.Scale(0f), 5);                // ring bars rest at 0.1 scale
        Assert.Equal(1f, Visualizer.Halo.Scale(1f), 5);
        Assert.Equal(0.16f, Visualizer.Halo.GlowOpacity(0f), 5);
    }

    [Fact]
    public void Matrix_columns_rows_and_peak_dots()
    {
        Assert.Equal(32, Visualizer.Matrix.Columns);
        Assert.Equal(12, Visualizer.Matrix.Rows);
        int prev = -1;
        for (int c = 0; c < Visualizer.Matrix.Columns; c++)
        {
            int band = Visualizer.Matrix.BandOf(c);
            Assert.InRange(band, 0, Visualizer.Bands.Count - 1);
            Assert.True(band >= prev);                                   // monotone across the columns
            prev = band;
        }
        Assert.Equal(0, Visualizer.Matrix.BandOf(0));
        Assert.Equal(Visualizer.Bands.Count - 1, Visualizer.Matrix.BandOf(Visualizer.Matrix.Columns - 1));

        Assert.Equal(0, Visualizer.Matrix.LitRows(0f));
        Assert.Equal(6, Visualizer.Matrix.LitRows(0.5f));
        Assert.Equal(Visualizer.Matrix.Rows, Visualizer.Matrix.LitRows(1f));
        Assert.Equal(Visualizer.Matrix.Rows, Visualizer.Matrix.LitRows(3f));     // clamped
        Assert.Equal(0, Visualizer.Matrix.LitRows(-1f));

        Assert.Equal(1, Visualizer.Matrix.PeakRow(0f));                  // a dim peak dot still sits at row 0 (never off the grid)
        Assert.Equal(6, Visualizer.Matrix.PeakRow(0.5f));
        Assert.Equal(7, Visualizer.Matrix.PeakRow(0.51f));
        Assert.Equal(Visualizer.Matrix.Rows, Visualizer.Matrix.PeakRow(1f));
        Assert.Equal(Visualizer.Matrix.Rows, Visualizer.Matrix.PeakRow(4f));
    }

    [Fact]
    public void Spectrum_bars_have_a_floor()
    {
        Assert.Equal(Visualizer.Bands.Count, Visualizer.Spectrum.Bars);
        Assert.Equal(0.02f, Visualizer.Spectrum.Scale(0f));
        Assert.Equal(0.02f, Visualizer.Spectrum.Scale(-1f));
        Assert.Equal(0.5f, Visualizer.Spectrum.Scale(0.5f));
        Assert.Equal(1f, Visualizer.Spectrum.Scale(1f));
    }

    [Fact]
    public void Field_blobs_and_opacities()
    {
        Assert.Equal(Visualizer.Field.Blobs, Visualizer.Field.PeriodSec.Length);
        Assert.Equal(Visualizer.Field.Blobs, Visualizer.Field.Drift.Length);
        Assert.All(Visualizer.Field.PeriodSec, p => Assert.InRange(p, 28f, 36f));   // 28-36 s loops (§2.1)

        Assert.Equal(0.75f, Visualizer.Field.FaceOpacity(0f));
        Assert.Equal(1f, Visualizer.Field.FaceOpacity(1f));
        Assert.Equal(Stage.Tone.BaseFieldVisualizerA, Visualizer.Field.BaseOpacity(0.7f, true));        // under a face: near-full whatever the breath
        Assert.Equal(Stage.Tone.BaseFieldA, Visualizer.Field.BaseOpacity(0f, false));
        Assert.Equal(Stage.Tone.BaseFieldA + 0.5f * Stage.Tone.BaseFieldBreathA, Visualizer.Field.BaseOpacity(0.5f, false), 5);
    }
}
