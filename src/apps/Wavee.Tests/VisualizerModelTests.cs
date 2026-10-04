// ── Wavee.Tests/VisualizerModelTests.cs — the visualizer's pure core (Shell/Visualizer.cs) ─────────────────────────────
//
// Pure: `Visualizer.Bands / Beat / Sections / Aurora / Timeline / Oscilloscope` and the ONE `Model.Tick` take the caller's
// spans and touch no engine, no signal and no table, so nothing here needs a scope. The catalog, the demand and the
// migration are VisualizerCatalogTests; the palette VisualizerPaletteTests; the moments VisualizerMomentsTests. The clock,
// the slab and the faces that bind them are Visualizer.UI.cs (covered by the `--fake` and real-account walks, not a unit
// test). Plan: docs/plans/wavee/fullscreen-flagship-implementation.md §4.8, §5.2 (V-U2, V-U32, V-U55); viz-app-plan §3.3-§3.4.

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
        float rms = 0f, long pos = 0, long dur = 180_000, bool bands = false, bool beats = false, ushort tempo = 0, bool viz = true,
        bool level = false, float flux = 0f, bool onset = false, float strength = 0f)
        => new(playing, calm, sensitivity, live, muted, rms, pos, dur, bands, beats, tempo, viz, level, flux, onset, strength);

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

    static int ArgMax(ReadOnlySpan<float> v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++) if (v[i] > v[best]) best = i;
        return best;
    }

    /// <summary>A 120 BPM grid from 0 ms (500 ms a beat); with <paramref name="barsFrom"/> ≥ 0 every 4th beat from it is a downbeat.</summary>
    static uint[] Grid(int beats, int barsFrom = -1)
    {
        var g = new uint[beats];
        for (int i = 0; i < beats; i++)
            g[i] = (uint)(i * 500) | (barsFrom >= 0 && i >= barsFrom && (i - barsFrom) % 4 == 0 ? BeatGrid.DownbeatBit : 0u);
        return g;
    }

    static int LiveRipples(Visualizer.Model m)
    {
        int n = 0;
        foreach (float a in m.RippleAge) if (a < 1f) n++;
        return n;
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

        float g = 0f;
        for (int i = 0; i < 400; i++) g = Visualizer.Bands.Agc(ref agc, 0f);
        Assert.Equal(Visualizer.Bands.AgcMaxGain, g);

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

    // ── the precomputed spread and the tempo pulse ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Spread_is_zero_without_data_deterministic_and_lands_each_energy_on_its_envelope()
    {
        var a = new float[Visualizer.Bands.Count];
        Visualizer.Bands.Spread(0f, 0f, 0f, 17, a);
        Assert.All(a, v => Assert.Equal(0f, v));                         // silence stays silent (the ripple scales with energy)

        var b = new float[Visualizer.Bands.Count];
        var c = new float[Visualizer.Bands.Count];
        Visualizer.Bands.Spread(0.5f, 0.4f, 0.3f, 17, b);
        Visualizer.Bands.Spread(0.5f, 0.4f, 0.3f, 17, c);
        Assert.Equal(b, c);                                              // nothing random, nothing timed
        Visualizer.Bands.Spread(0.5f, 0.4f, 0.3f, 18, c);
        Assert.NotEqual(b, c);                                           // the ripple moves with the beat (the seed)
        Assert.All(b, v => Assert.InRange(v, 0f, 1f));

        Visualizer.Bands.Spread(0.8f, 0f, 0f, 0, a);
        Assert.InRange(ArgMax(a), 2, 6);                                 // the low envelope is centred on band 4
        Visualizer.Bands.Spread(0f, 0.8f, 0f, 0, a);
        Assert.InRange(ArgMax(a), 15, 21);                               // the mid on 18
        Visualizer.Bands.Spread(0f, 0f, 0.8f, 0, a);
        Assert.InRange(ArgMax(a), 35, 41);                               // the high on 38

        // the ripple is at most SpreadRipple × the loudest energy
        for (int seed = 0; seed < 32; seed++)
        {
            Visualizer.Bands.Spread(0.5f, 0f, 0f, seed, a);
            Assert.InRange(a[47], 0f, Visualizer.Bands.SpreadRipple * 0.5f + 1e-4f);
        }
        for (int i = 0; i < 48; i++) Assert.InRange(Visualizer.Bands.Hash(i, 3), 0f, 0.99999f);
    }

    [Fact]
    public void Pulse_is_a_bass_weighted_hump_on_the_beat()
    {
        var on = new float[Visualizer.Bands.Count];
        var late = new float[Visualizer.Bands.Count];
        Visualizer.Bands.Pulse(0f, on);
        Visualizer.Bands.Pulse(0.8f, late);
        Assert.True(on[4] > on[38]);                                     // bass-weighted
        Assert.True(on[4] > late[4]);                                    // high on the beat, gone by its end
        Assert.All(on, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void Sample_interpolates_between_neighbouring_waveform_samples()
    {
        WaveSample[] two = [new(0, 0, 0), new(255, 255, 255)];
        Assert.True(Visualizer.Bands.Sample(two, 500, 1_000, out float l, out float m, out float h));
        Assert.Equal(0.5f, l, 3);                                        // half way between the two centres (250 and 750 ms)
        Assert.Equal(0.5f, m, 3);
        Assert.Equal(0.5f, h, 3);
        Visualizer.Bands.Sample(two, 100, 1_000, out l, out _, out _);
        Assert.Equal(0f, l);                                             // before the first centre: clamped to it
        Visualizer.Bands.Sample(two, 990, 1_000, out l, out _, out _);
        Assert.Equal(1f, l);                                             // after the last centre: clamped to it
        Assert.False(Visualizer.Bands.Sample(ReadOnlySpan<WaveSample>.Empty, 0, 1_000, out _, out _, out _));
        Assert.False(Visualizer.Bands.Sample(two, 0, 0, out _, out _, out _));
    }

    // ── the fold: bands ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Model_applies_sensitivity_after_the_agc()
    {
        float[] one = Converged(-50f, 1f), more = Converged(-50f, 1.5f);
        for (int i = 0; i < one.Length; i++)
        {
            Assert.True(one[i] > 0.3f);
            Assert.Equal(1.5f, more[i] / one[i], 3);
        }

        float[] full = Converged(-20f, 1f), half = Converged(-20f, 0.5f);
        for (int i = 0; i < full.Length; i++)
        {
            Assert.InRange(full[i], 0.99f, 1f);
            Assert.Equal(0.5f, half[i] / full[i], 3);
        }

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

        var f = Run(m, Inp(live: true, muted: true, rms: 0.1f, level: true), live, NoBands, NoBeats, 80);
        Assert.True(Visualizer.Bands.Settled(m.Level));                  // muted: no live frame AND no level tap
        Assert.Equal(Visualizer.Source.Breath, f.Source);

        var short47 = new float[Visualizer.Bands.Count - 1];
        Array.Fill(short47, -10f);
        var m2 = new Visualizer.Model();
        f = Run(m2, Inp(live: true, rms: 0.1f), short47, NoBands, NoBeats, 30);
        Assert.Equal(Visualizer.Source.Breath, f.Source);
        Assert.True(Visualizer.Bands.Settled(m2.Level));
    }

    [Fact]
    public void Model_level_lease_reads_the_rms_and_spreads_it_over_the_bands()
    {
        // a level lease (no spectrum): the level is the tap's RMS ×3.5, and the bands are its spread — not a dead floor
        var m = new Visualizer.Model();
        var f = Run(m, Inp(level: true, rms: 0.1f), NoDb, NoBands, NoBeats, 30);
        Assert.Equal(Visualizer.Source.Live, f.Source);
        Assert.Equal(0.35f, f.Level, 4);
        Assert.Equal(0.35f, f.Low, 4);                                   // without a waveform the three energies are the level
        Assert.True(m.Level[4] > 0.2f && m.Level[38] > 0.2f);

        // with the song's waveform the level is SHAPED by it (bass-heavy here) and still scaled to the live level
        var wave = new WaveSample[100];
        Array.Fill(wave, new WaveSample(200, 60, 20));
        var shaped = Run(new Visualizer.Model(), Inp(level: true, rms: 0.1f, bands: true, pos: 50_000, dur: 100_000), NoDb, wave, NoBeats, 30);
        Assert.True(shaped.Low > shaped.Mid && shaped.Mid > shaped.High);
        Assert.Equal(0.35f, shaped.Level, 4);

        // the tap is ignored while a live spectrum is there (the window RMS is), and silent when paused
        Assert.Equal(Visualizer.Source.Breath, Run(new Visualizer.Model(), Inp(playing: false, level: true, rms: 0.1f), NoDb, NoBands, NoBeats, 1).Source);
    }

    [Fact]
    public void Model_spreads_the_precomputed_waveform_when_there_is_no_live_frame()
    {
        var samples = new WaveSample[100];
        Array.Fill(samples, new WaveSample(200, 100, 50));
        var m = new Visualizer.Model();
        var f = Run(m, Inp(bands: true, pos: 90_000), NoDb, samples, NoBeats, 30);

        Assert.Equal(Visualizer.Source.Precomputed, f.Source);
        Assert.Equal(200f / 255f, f.Low, 4);                             // the three energies are the song's own bytes
        Assert.Equal(100f / 255f, f.Mid, 4);
        Assert.Equal(50f / 255f, f.High, 4);
        Assert.Equal((200f + 100f + 50f) / 3f / 255f, f.Level, 4);
        Assert.True(m.Level[4] > m.Level[44]);                           // and the 48 bands are their spread: bass-heavy here
        Assert.True(m.Level[44] > 0f);                                   // never a dead floor on Connect
    }

    [Fact]
    public void Model_source_ladder()
    {
        var live = Flat(-30f);
        var wave = new WaveSample[50];
        Array.Fill(wave, new WaveSample(100, 100, 100));

        Assert.Equal(Visualizer.Source.Live, Run(new Visualizer.Model(), Inp(live: true, rms: 0.1f), live, NoBands, NoBeats, 1).Source);
        Assert.Equal(Visualizer.Source.Live, Run(new Visualizer.Model(), Inp(level: true, rms: 0.1f), NoDb, NoBands, NoBeats, 1).Source);
        Assert.Equal(Visualizer.Source.Precomputed, Run(new Visualizer.Model(), Inp(bands: true), NoDb, wave, NoBeats, 1).Source);
        Assert.Equal(Visualizer.Source.TempoGrid, Run(new Visualizer.Model(), Inp(tempo: 1200), NoDb, NoBands, NoBeats, 1).Source);
        Assert.Equal(Visualizer.Source.Breath, Run(new Visualizer.Model(), Inp(), NoDb, NoBands, NoBeats, 1).Source);

        // live beats everything; the bands beat the tempo (the tempo only ever fills in for nothing)
        Assert.Equal(Visualizer.Source.Live, Run(new Visualizer.Model(), Inp(live: true, rms: 0.1f, bands: true, tempo: 1200), live, wave, NoBeats, 1).Source);
        Assert.Equal(Visualizer.Source.Precomputed, Run(new Visualizer.Model(), Inp(bands: true, tempo: 1200), NoDb, wave, NoBeats, 1).Source);

        // paused: a live frame is not live (it is stale by definition), and the bands do not play
        Assert.Equal(Visualizer.Source.Breath, Run(new Visualizer.Model(), Inp(playing: false, live: true, bands: true), live, wave, NoBeats, 1).Source);
    }

    [Fact]
    public void Model_tempo_pulse_moves_the_bands_without_any_data()
    {
        var m = new Visualizer.Model();
        Run(m, Inp(tempo: 1200, pos: 0), NoDb, NoBands, NoBeats, 1);
        Assert.True(m.Level[4] > 0.2f);                                  // a hump on the beat
        Assert.True(m.Level[4] > m.Level[40]);                           // bass-weighted
    }

    // ── the fold: beats and bars ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Model_beats_from_the_grid_then_the_tempo()
    {
        uint[] grid = Grid(40);                                          // 120 BPM from 0 ms, no bar marks

        var f = Run(new Visualizer.Model(), Inp(pos: 250, beats: true), NoDb, NoBands, grid, 1);
        Assert.Equal(0, f.BeatIndex);
        Assert.Equal(0.5f, f.BeatPhase, 5);
        Assert.Equal(MathF.Exp(-3f), f.Kick, 5);                         // Kick(phase) = e^(-6 phase)

        f = Run(new Visualizer.Model(), Inp(pos: 1_750, beats: true), NoDb, NoBands, grid, 1);
        Assert.Equal(3, f.BeatIndex);
        Assert.Equal(0.5f, f.BeatPhase, 5);
        Assert.Equal(Visualizer.Source.Precomputed, f.Source);           // a grid with no bands is still Spotify's data, not idle

        uint[] late = new uint[40];
        for (int i = 0; i < late.Length; i++) late[i] = (uint)(400 + i * 500);
        f = Run(new Visualizer.Model(), Inp(pos: 100, beats: true), NoDb, NoBands, late, 1);
        Assert.Equal(0, f.BeatIndex);
        Assert.Equal(1f, f.BeatPhase);
        Assert.True(f.Kick < 0.01f);                                     // before the first beat: a spent kick, not a full one

        f = Run(new Visualizer.Model(), Inp(pos: 250, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(0, f.BeatIndex);
        Assert.Equal(0.5f, f.BeatPhase, 5);
        Assert.Equal(MathF.Exp(-3f), f.Kick, 5);
        Assert.Equal(Visualizer.Source.TempoGrid, f.Source);

        f = Run(new Visualizer.Model(), Inp(pos: 1_750, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(3, f.BeatIndex);

        f = Run(new Visualizer.Model(), Inp(pos: 750, beats: true, tempo: 600), NoDb, NoBands, grid, 1);
        Assert.Equal(1, f.BeatIndex);                                    // the grid beats the tempo
        Assert.Equal(0.5f, f.BeatPhase, 5);

        f = Run(new Visualizer.Model(), Inp(pos: 250, beats: true, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(0.5f, f.BeatPhase, 5);                              // an empty grid falls through to the tempo

        f = Run(new Visualizer.Model(), Inp(pos: 250, beats: true), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(0, f.BeatIndex);                                    // neither: no beat, no kick, never a negative index
        Assert.Equal(0f, f.BeatPhase);
        Assert.Equal(0f, f.Kick);
        Assert.False(f.HasBars);

        f = Run(new Visualizer.Model(), Inp(playing: false, pos: 250, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(0.5f, f.BeatPhase, 5);                              // paused: the phase still reads, nothing kicks
        Assert.Equal(0f, f.Kick);
    }

    [Fact]
    public void Model_kick_attacks_instantly_and_releases_smoothly()
    {
        var m = new Visualizer.Model();
        var f = Run(m, Inp(pos: 0, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(1f, f.Kick, 5);

        f = Run(m, Inp(pos: 250, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.True(f.Kick < 1f);
        Assert.True(f.Kick > MathF.Exp(-3f));
    }

    [Fact]
    public void Model_bars_come_from_the_grids_downbeats()
    {
        uint[] grid = Grid(64, barsFrom: 0);                             // a downbeat every 4th beat from beat 0
        var f = Run(new Visualizer.Model(), Inp(pos: 2_250, beats: true), NoDb, NoBands, grid, 1);
        Assert.True(f.HasBars);
        Assert.Equal(1, f.Bar);                                          // beat 4 opens bar 1
        Assert.Equal(0.125f, f.BarPhase, 4);                             // half a beat into a 4-beat bar

        f = Run(new Visualizer.Model(), Inp(pos: 7_900, beats: true), NoDb, NoBands, grid, 1);
        Assert.Equal(3, f.Bar);                                          // beat 15 (7.5 s) is the last beat of bar 3

        // a pickup: the first bar starts at beat 1 — before it, bar 0
        uint[] pickup = Grid(64, barsFrom: 1);
        f = Run(new Visualizer.Model(), Inp(pos: 250, beats: true), NoDb, NoBands, pickup, 1);
        Assert.Equal(0, f.Bar);
        f = Run(new Visualizer.Model(), Inp(pos: 2_750, beats: true), NoDb, NoBands, pickup, 1);
        Assert.Equal(1, f.Bar);                                          // beat 5 opens the second marked bar

        // a backward seek recounts (an incremental counter must never keep counting past a rewind)
        var m = new Visualizer.Model();
        Run(m, Inp(pos: 20_000, beats: true), NoDb, NoBands, grid, 1);
        f = Run(m, Inp(pos: 2_250, beats: true), NoDb, NoBands, grid, 1);
        Assert.Equal(1, f.Bar);
    }

    [Fact]
    public void Model_bars_fall_back_to_every_fourth_beat()
    {
        var f = Run(new Visualizer.Model(), Inp(pos: 2_250, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        Assert.True(f.HasBars);
        Assert.Equal(1, f.Bar);
        Assert.Equal(0.125f, f.BarPhase, 4);

        f = Run(new Visualizer.Model(), Inp(pos: 2_250, beats: true), NoDb, NoBands, Grid(64), 1);
        Assert.Equal(1, f.Bar);                                          // a grid without bar marks counts 4 beats a bar too
        Assert.Equal((1, 0.375f), Visualizer.Beat.BarOfTempo(5, 0.5f));
        Assert.Equal((0, 0f), Visualizer.Beat.BarOfTempo(-3, 0f));        // never a negative bar
    }

    [Fact]
    public void Model_downbeat_edges_fire_once_when_crossed_and_never_on_a_seek()
    {
        var m = new Visualizer.Model();
        int edges = 0;
        Visualizer.Frame f = default;
        for (long pos = 1_800; pos <= 2_400; pos += 33)
        {
            f = m.Tick(Inp(pos: pos, tempo: 1200), NoDb, NoBands, NoBeats, Dt);
            if (f.DownbeatEdge) { edges++; Assert.Equal(1, f.Bar); }
        }
        Assert.Equal(1, edges);                                          // bar 0 → 1 at 2 000 ms, once
        Assert.Equal(1, f.Downbeat);

        f = m.Tick(Inp(pos: 20_000, tempo: 1200), NoDb, NoBands, NoBeats, Dt);
        Assert.False(f.DownbeatEdge);                                    // a seek moves the bar …
        Assert.Equal(10, f.Bar);
        Assert.Equal(1, f.Downbeat);                                     // … never the downbeat

        m.Tick(Inp(pos: 21_900, tempo: 1200), NoDb, NoBands, NoBeats, Dt);
        f = m.Tick(Inp(pos: 22_010, tempo: 1200), NoDb, NoBands, NoBeats, Dt);
        Assert.True(f.DownbeatEdge);                                     // playing on from there crosses bar 11
        Assert.Equal(11, f.Downbeat);

        // paused: no edges
        var p = new Visualizer.Model();
        p.Tick(Inp(playing: false, pos: 1_990, tempo: 1200), NoDb, NoBands, NoBeats, Dt);
        Assert.False(p.Tick(Inp(playing: false, pos: 2_010, tempo: 1200), NoDb, NoBands, NoBeats, Dt).DownbeatEdge);
    }

    // ── the fold: progress, ripples, flux, onsets, energy ───────────────────────────────────────────────────────────

    [Fact]
    public void Model_progress_is_position_over_duration_clamped()
    {
        Assert.Equal(0.25f, Run(new Visualizer.Model(), Inp(pos: 45_000, dur: 180_000), NoDb, NoBands, NoBeats, 1).Progress, 5);
        Assert.Equal(0f, Run(new Visualizer.Model(), Inp(pos: 45_000, dur: 0), NoDb, NoBands, NoBeats, 1).Progress);
        Assert.Equal(1f, Run(new Visualizer.Model(), Inp(pos: 200_000, dur: 180_000), NoDb, NoBands, NoBeats, 1).Progress);
    }

    [Fact]
    public void Model_ripples_spawn_once_per_beat_and_age_out()
    {
        var m = new Visualizer.Model();
        m.Tick(Inp(pos: 400, tempo: 1200), NoDb, NoBands, NoBeats, Dt);   // the first beat seen only seeds
        Assert.Equal(0, LiveRipples(m));
        m.Tick(Inp(pos: 510, tempo: 1200), NoDb, NoBands, NoBeats, Dt);   // a new beat: one ripple
        Assert.Equal(1, LiveRipples(m));
        for (long pos = 540; pos < 1_000; pos += 90) m.Tick(Inp(pos: pos, tempo: 1200), NoDb, NoBands, NoBeats, Dt);
        Assert.Equal(1, LiveRipples(m));                                 // the same beat: still one
        m.Tick(Inp(pos: 1_010, tempo: 1200), NoDb, NoBands, NoBeats, Dt); // the next beat: two
        Assert.Equal(2, LiveRipples(m));

        // a ripple lives 1.1 s; with nothing playing they all age out and only then does the model settle
        for (int i = 0; i < 20; i++) m.Tick(Inp(playing: false), NoDb, NoBands, NoBeats, Dt);
        Assert.True(LiveRipples(m) > 0);
        Assert.False(m.IsSettled);
        for (int i = 0; i < 40; i++) m.Tick(Inp(playing: false), NoDb, NoBands, NoBeats, Dt);
        Assert.Equal(0, LiveRipples(m));
        Assert.Equal(Visualizer.Beat.Ripples, m.RippleAge.Length);
    }

    [Fact]
    public void Model_ripples_follow_onsets_under_a_live_spectrum_and_halve_under_calm()
    {
        var live = Flat(-30f);
        var m = new Visualizer.Model();
        m.Tick(Inp(live: true, rms: 0.1f, tempo: 1200, pos: 400), live, NoBands, NoBeats, Dt);
        m.Tick(Inp(live: true, rms: 0.1f, tempo: 1200, pos: 510), live, NoBands, NoBeats, Dt);
        Assert.Equal(0, LiveRipples(m));                                 // live: the beat does not spawn, an onset does
        m.Tick(Inp(live: true, rms: 0.1f, onset: true, strength: 0.7f), live, NoBands, NoBeats, Dt);
        Assert.Equal(1, LiveRipples(m));
        m.Tick(Inp(live: true, rms: 0.1f, onset: true, strength: 0.7f), live, NoBands, NoBeats, Dt);
        Assert.Equal(1, LiveRipples(m));                                 // 33 ms later: inside the 0.15 s gap

        // calm: every other beat
        var c = new Visualizer.Model();
        c.Tick(Inp(calm: true, pos: 400, tempo: 1200), NoDb, NoBands, NoBeats, Dt);
        for (long pos = 510; pos < 1_000; pos += 90) c.Tick(Inp(calm: true, pos: pos, tempo: 1200), NoDb, NoBands, NoBeats, Dt);
        Assert.Equal(0, LiveRipples(c));
        c.Tick(Inp(calm: true, pos: 1_010, tempo: 1200), NoDb, NoBands, NoBeats, Dt);
        Assert.Equal(1, LiveRipples(c));
    }

    [Fact]
    public void Model_flux_is_followed_and_the_onset_flash_decays()
    {
        var live = Flat(-30f);
        var m = new Visualizer.Model();
        var f = m.Tick(Inp(live: true, rms: 0.1f, flux: 6f), live, NoBands, NoBeats, Dt);
        Assert.Equal(Visualizer.Beat.FluxAttack, f.Flux, 4);             // 6 dB reads as a full flux; one tick of attack
        f = m.Tick(Inp(live: true, rms: 0.1f, flux: 0f), live, NoBands, NoBeats, Dt);
        Assert.Equal(0.6f * (1f - Visualizer.Beat.FluxRelease), f.Flux, 4);
        f = Run(new Visualizer.Model(), Inp(flux: 6f), NoDb, NoBands, NoBeats, 3);
        Assert.Equal(0f, f.Flux);                                        // no live spectrum: no flux

        f = m.Tick(Inp(live: true, rms: 0.1f, onset: true, strength: 0.8f), live, NoBands, NoBeats, Dt);
        Assert.Equal(0.8f, f.Onset, 4);                                  // jumps to the onset's strength
        f = m.Tick(Inp(live: true, rms: 0.1f), live, NoBands, NoBeats, Dt);
        Assert.Equal(0.8f * Visualizer.Beat.OnsetDecay, f.Onset, 4);     // decays 0.85 per reference tick

        var calm = new Visualizer.Model();
        f = calm.Tick(Inp(calm: true, live: true, rms: 0.1f, onset: true, strength: 0.8f), live, NoBands, NoBeats, Dt);
        Assert.Equal(0f, f.Onset);                                       // calm: no onset flashes
    }

    [Fact]
    public void Model_energy_looks_ahead_and_the_section_follows_it()
    {
        // 100 s: the first half quiet, the second loud — the track's mean sits between
        var wave = new WaveSample[100];
        for (int i = 0; i < wave.Length; i++) wave[i] = i < 50 ? new WaveSample(40, 40, 40) : new WaveSample(220, 220, 220);
        var m = new Visualizer.Model();
        var f = Run(m, Inp(bands: true, pos: 10_000, dur: 100_000), NoDb, wave, NoBeats, 1);
        Assert.Equal(40f / 255f, f.Energy, 3);
        Assert.Equal(Visualizer.Sections.Quiet, f.Section);
        f = Run(m, Inp(bands: true, pos: 45_000, dur: 100_000), NoDb, wave, NoBeats, 1);
        Assert.True(f.Energy > 40f / 255f + 0.05f);                      // the 8 s window already reaches the loud half
        f = Run(m, Inp(bands: true, pos: 70_000, dur: 100_000), NoDb, wave, NoBeats, 1);
        Assert.Equal(Visualizer.Sections.Loud, f.Section);

        // no waveform: the section stays normal
        Assert.Equal(Visualizer.Sections.Normal, Run(new Visualizer.Model(), Inp(tempo: 1200), NoDb, NoBands, NoBeats, 5).Section);
    }

    [Fact]
    public void Sections_are_hysteretic()
    {
        byte s = Visualizer.Sections.Normal;
        s = Visualizer.Sections.Next(s, 1.10f);
        Assert.Equal(Visualizer.Sections.Normal, s);                     // under LoudEnter: still normal
        s = Visualizer.Sections.Next(s, 1.20f);
        Assert.Equal(Visualizer.Sections.Loud, s);
        s = Visualizer.Sections.Next(s, 1.08f);
        Assert.Equal(Visualizer.Sections.Loud, s);                       // above LoudLeave: held
        s = Visualizer.Sections.Next(s, 1.00f);
        Assert.Equal(Visualizer.Sections.Normal, s);
        s = Visualizer.Sections.Next(s, 0.70f);
        Assert.Equal(Visualizer.Sections.Quiet, s);
        s = Visualizer.Sections.Next(s, 0.80f);
        Assert.Equal(Visualizer.Sections.Quiet, s);                      // under QuietLeave: held
        s = Visualizer.Sections.Next(s, 2.0f);
        Assert.Equal(Visualizer.Sections.Loud, s);                       // a jump crosses straight through
        Assert.Equal(Visualizer.Sections.Normal, Visualizer.Sections.Next(Visualizer.Sections.Loud, float.NaN));
    }

    [Fact]
    public void Model_calm_cuts_the_gain_and_the_kick()
    {
        float[] normal = Converged(-50f, 1f), calm = Converged(-50f, 1f, calm: true);
        for (int i = 0; i < normal.Length; i++) Assert.Equal(Visualizer.Bands.CalmGain, calm[i] / normal[i], 3);

        var n = Run(new Visualizer.Model(), Inp(pos: 0, tempo: 1200), NoDb, NoBands, NoBeats, 1);
        var c = Run(new Visualizer.Model(), Inp(pos: 0, tempo: 1200, calm: true), NoDb, NoBands, NoBeats, 1);
        Assert.Equal(1f, n.Kick, 5);
        Assert.Equal(Visualizer.Beat.CalmKick, c.Kick, 5);
    }

    [Fact]
    public void Model_frame_carries_the_faces_scalars()
    {
        var m = new Visualizer.Model();
        var f = Run(m, Inp(live: true, rms: 0.1f, viz: true), Flat(-6f), NoBands, NoBeats, 40);
        Assert.Equal(1f, f.Low, 3);
        Assert.Equal(1f, f.Mid, 3);
        Assert.Equal(1f, f.High, 3);
        Assert.Equal(Visualizer.Field.BaseOpacity(f.Low, true), f.BaseFieldOp);

        var plain = Run(new Visualizer.Model(), Inp(live: true, rms: 0.1f, viz: false), Flat(-6f), NoBands, NoBeats, 40);
        Assert.Equal(Visualizer.Field.BaseOpacity(plain.Low, false), plain.BaseFieldOp);
        Assert.True(plain.BaseFieldOp < f.BaseFieldOp);                  // the base Field is near-full under a face, breathing under a pane
    }

    // ── series: Aurora, Timeline, the scope ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Model_aurora_drifts_while_playing_and_freezes_when_paused()
    {
        var m = new Visualizer.Model();
        Run(m, Inp(), NoDb, NoBands, NoBeats, 1);
        float[] first = (float[])m.AuroraLow.Clone();
        Run(m, Inp(), NoDb, NoBands, NoBeats, 1);
        Assert.NotEqual(first, m.AuroraLow);                             // near-still in silence, never stopped while playing

        Run(m, Inp(playing: false), NoDb, NoBands, NoBeats, 1);
        float[] paused = (float[])m.AuroraLow.Clone();
        Run(m, Inp(playing: false), NoDb, NoBands, NoBeats, 3);
        Assert.Equal(paused, m.AuroraLow);                               // paused: the rest pose holds
        Assert.All(m.AuroraLow, v => Assert.InRange(v, 0f, 1f));
        Assert.Equal(256, Visualizer.Aurora.Points);
    }

    [Fact]
    public void Aurora_louder_bands_swing_further()
    {
        var quiet = new float[Visualizer.Aurora.Points]; var loud = new float[Visualizer.Aurora.Points];
        var mid = new float[Visualizer.Aurora.Points]; var high = new float[Visualizer.Aurora.Points];
        Visualizer.Aurora.Fill(quiet, mid, high, 0f, 0f, 0f, 1f, 1f, 1f);
        Visualizer.Aurora.Fill(loud, mid, high, 1f, 1f, 1f, 1f, 1f, 1f);
        Assert.True(Swing(loud) > Swing(quiet));
        Assert.All(loud, v => Assert.InRange(v, 0f, 1f));
        Assert.Equal(0.05f, Visualizer.Aurora.Amplitude(0f), 5);
        Assert.Equal(0.27f, Visualizer.Aurora.Amplitude(1f), 5);

        static float Swing(float[] v)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (float x in v) { if (x < lo) lo = x; if (x > hi) hi = x; }
            return hi - lo;
        }
    }

    [Fact]
    public void Timeline_resamples_the_whole_song_and_keeps_its_transients()
    {
        var low = new float[Visualizer.Timeline.Points]; var mid = new float[Visualizer.Timeline.Points]; var high = new float[Visualizer.Timeline.Points];

        // a 4 096-sample song with ONE loud sample: max-pooling keeps it (nearest or mean sampling would lose it)
        var song = new WaveSample[WaveformBands.MaxSamples];
        song[2_048] = new WaveSample(255, 255, 255);
        Assert.True(Visualizer.Timeline.Resample(song, low, mid, high));
        Assert.Equal(1f, low[ArgMax(low)]);
        Assert.InRange(ArgMax(low), 178, 182);                           // half way through the song
        Assert.Equal(0.75f, high[ArgMax(high)], 4);                      // the Horizon weights: high·0.75

        // a short payload is interpolated: monotone between its samples, exact at its ends
        WaveSample[] ramp = [new(0, 0, 0), new(255, 255, 255)];
        Visualizer.Timeline.Resample(ramp, low, mid, high);
        for (int i = 1; i < low.Length; i++) Assert.True(low[i] >= low[i - 1]);
        Assert.Equal(0f, low[0]);
        Assert.Equal(1f, low[^1]);

        // empty ⇒ false and a flat rule, and a stale buffer is cleared
        Array.Fill(low, 5f);
        Assert.False(Visualizer.Timeline.Resample(ReadOnlySpan<WaveSample>.Empty, low, mid, high));
        Assert.All(low, v => Assert.Equal(0f, v));
        Assert.Equal(360, Visualizer.Timeline.Points);
    }

    [Fact]
    public void Oscilloscope_locks_to_a_rising_zero_crossing_and_closes_the_polar_loop()
    {
        var wave = new float[1024];
        for (int i = 0; i < wave.Length; i++) wave[i] = 0.4f * MathF.Sin((i - 37) * MathF.Tau / 128f);   // a rising crossing at 37
        var line = new float[Visualizer.Oscilloscope.Points];
        var radial = new float[Visualizer.Oscilloscope.RadialPoints];
        float agc = 0f;
        Visualizer.Oscilloscope.Fill(wave, line, radial, ref agc, 1f, 1f);
        Assert.Equal(0.5f, line[0], 2);                                  // the window starts on the crossing …
        Assert.True(line[3] > 0.5f);                                     // … going up
        Assert.All(line, v => Assert.InRange(v, 0f, 1f));
        Assert.True(MaxOf(line) > 0.85f);                                // a quiet master is normalised up (AGC)
        Assert.Equal(radial[0], radial[^1], 1);                          // the polar ring meets itself
        Assert.All(radial, v => Assert.InRange(v, Visualizer.Oscilloscope.RadialBase - Visualizer.Oscilloscope.RadialSwing - 1e-4f,
                                                  Visualizer.Oscilloscope.RadialBase + Visualizer.Oscilloscope.RadialSwing + 1e-4f));

        // the spectral stand-in: silent bands draw the rest line, and it is deterministic in time
        var bands = new float[Visualizer.Bands.Count];
        Visualizer.Oscilloscope.Synthesize(bands, 3f, line, radial);
        Assert.All(line, v => Assert.Equal(0.5f, v, 5));
        Assert.All(radial, v => Assert.Equal(Visualizer.Oscilloscope.RadialBase, v, 5));
        Array.Fill(bands, 0.6f);
        var again = new float[line.Length];
        Visualizer.Oscilloscope.Synthesize(bands, 3f, line, radial);
        Visualizer.Oscilloscope.Synthesize(bands, 3f, again, radial);
        Assert.Equal(line, again);
        Assert.Equal(0.5f, line[0], 4);                                  // windowed: the Cartesian line rests at both ends
        Assert.Equal(0.5f, line[^1], 4);

        static float MaxOf(float[] v) { float m = 0f; foreach (float x in v) if (x > m) m = x; return m; }
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
        var grid = Grid(400, barsFrom: 0);

        Visualizer.Frame sink = default;
        void Pass(int count, bool live)
        {
            for (int i = 0; i < count; i++)
            {
                var input = Inp(live: live, level: !live, rms: 0.1f, pos: 1_000 + i * 33, bands: true, beats: true, tempo: 1200, flux: 2f, onset: (i & 15) == 0, strength: 0.5f);
                sink = m.Tick(in input, db, wave, grid, Dt);
            }
        }
        Pass(200, live: true); Pass(200, live: false);                   // warm both ladders

        long before = GC.GetAllocatedBytesForCurrentThread();
        Pass(1_000, live: true);
        Pass(1_000, live: false);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
        Assert.True(sink.Level >= 0f);                                   // the frames are consumed, so nothing is elided
    }

    [Fact]
    public void TickHz_matches_Design_Cadence()
    {
        Assert.Equal(Design.Cadence.ClockHz, Visualizer.TickHz);
        Assert.Equal(1000f / 60f, Visualizer.TickMs, 4);
    }

    [Fact]
    public void Model_is_rate_independent_two_60Hz_ticks_land_where_one_30Hz_tick_does()
    {
        var at30 = new Visualizer.Model();
        var at60 = new Visualizer.Model();
        var on = Inp(live: true, rms: 0.1f);
        float[] loud = Flat(-20f);
        Run(at30, on, loud, NoBands, NoBeats, 6);
        for (int i = 0; i < 12; i++) at60.Tick(in on, loud, NoBands, NoBeats, 1f / 60f);
        var off = Inp(playing: false);
        Run(at30, off, NoDb, NoBands, NoBeats, 15);                      // 10 held ticks, then 5 of fall
        for (int i = 0; i < 30; i++) at60.Tick(in off, NoDb, NoBands, NoBeats, 1f / 60f);

        for (int i = 0; i < Visualizer.Bands.Count; i++)
        {
            Assert.Equal(at30.Level[i], at60.Level[i], 4);
            Assert.Equal(at30.Peak[i], at60.Peak[i], 4);
        }
        Assert.True(at30.Peak[0] < at30.Level[0] + 0.8f && at30.Peak[0] > at30.Level[0], "the scenario must end mid-fall, above the band");
    }

    [Fact]
    public void PerTicks_is_exact_at_one_reference_tick()
    {
        Assert.Equal(Visualizer.Bands.Attack, Visualizer.Bands.PerTicks(Visualizer.Bands.Attack, 1f));
        float dt = Dt, ticks = dt * Visualizer.Bands.RefHz;
        Assert.Equal(1f, ticks);
        float k = Visualizer.Bands.PerTicks(Visualizer.Bands.Release, 0.5f), v = 1f;
        v -= v * k; v -= v * k;
        Assert.Equal(1f - Visualizer.Bands.Release, v, 5);
    }

    [Fact]
    public void DeltaSec_is_nominal_on_the_first_tick_and_clamped_after()
    {
        Assert.Equal(Visualizer.TickMs / 1000f, Visualizer.DeltaSec(0L, 5_000L), 6);
        Assert.Equal(0.033f, Visualizer.DeltaSec(10_000L, 10_033L), 5);
        Assert.Equal(Visualizer.DtMinSec, Visualizer.DeltaSec(10_000L, 10_000L));
        Assert.Equal(Visualizer.DtMinSec, Visualizer.DeltaSec(10_000L, 9_000L));
        Assert.Equal(Visualizer.DtMaxSec, Visualizer.DeltaSec(10_000L, 20_000L));
    }

    [Fact]
    public void Field_blobs_and_opacities()
    {
        Assert.Equal(Visualizer.Field.Blobs, Visualizer.Field.PeriodSec.Length);
        Assert.Equal(Visualizer.Field.Blobs, Visualizer.Field.Drift.Length);
        Assert.All(Visualizer.Field.PeriodSec, p => Assert.InRange(p, 28f, 36f));   // 28-36 s loops (§2.1)

        Assert.Equal(Stage.Tone.BaseFieldVisualizerA, Visualizer.Field.BaseOpacity(0.7f, true));        // under a face: near-full whatever the breath
        Assert.Equal(Stage.Tone.BaseFieldA, Visualizer.Field.BaseOpacity(0f, false));
        Assert.Equal(Stage.Tone.BaseFieldA + 0.5f * Stage.Tone.BaseFieldBreathA, Visualizer.Field.BaseOpacity(0.5f, false), 5);
    }
}
