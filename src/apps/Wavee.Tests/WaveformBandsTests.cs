// ── Wavee.Tests/WaveformBandsTests.cs — the kind-237 payload contract and the beat grid (Entities/Waveform.cs) ─────────
//
// Pure: `WaveformBands` and `BeatGrid` take the caller's spans and touch no table, so nothing here needs a scope. The
// decode and commit halves are DecodeTests (`A_waveform_lands_as_WaveSample_triples_capped_at_MaxSamples`); the fake seed's
// half is EntitiesFakeAlbumTests. Plan: docs/plans/wavee/fullscreen-flagship-implementation.md §4.5.1, §5.2.

using System.Runtime.CompilerServices;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class WaveformBandsTests
{
    // ── Fold ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fold_is_the_max_over_the_samples_share_and_nearest_neighbour_when_upsampling()
    {
        // a 12-long band onto n = 4: each sample is the MAX of its three
        byte[] twelve = [1, 5, 2, 9, 3, 3, 0, 0, 7, 4, 4, 6];
        Assert.Equal(5, WaveformBands.Fold(twelve, 0, 4));
        Assert.Equal(9, WaveformBands.Fold(twelve, 1, 4));
        Assert.Equal(7, WaveformBands.Fold(twelve, 2, 4));
        Assert.Equal(6, WaveformBands.Fold(twelve, 3, 4));

        // a 3-long band onto n = 9: one sample per share, nearest neighbour — [b0,b0,b0,b1,b1,b1,b2,b2,b2]
        byte[] three = [10, 20, 30];
        byte[] expected = [10, 10, 10, 20, 20, 20, 30, 30, 30];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], WaveformBands.Fold(three, i, 9));

        // an empty band (a body that omitted it) is 0, and so is a degenerate axis
        Assert.Equal(0, WaveformBands.Fold(ReadOnlySpan<byte>.Empty, 0, 4));
        Assert.Equal(0, WaveformBands.Fold(twelve, 0, 0));
    }

    [Fact]
    public void Fold_of_an_equal_length_band_is_the_identity()
    {
        byte[] band = [3, 1, 4, 1, 5, 9, 2, 6];
        for (int i = 0; i < band.Length; i++) Assert.Equal(band[i], WaveformBands.Fold(band, i, band.Length));
    }

    // ── IndexAt ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IndexAt_indexes_by_duration_and_clamps()
    {
        Assert.Equal(2048, WaveformBands.IndexAt(90_000, 180_000, 4096));
        Assert.Equal(0, WaveformBands.IndexAt(-5, 180_000, 4096));
        Assert.Equal(4095, WaveformBands.IndexAt(999_999, 180_000, 4096));
        Assert.Equal(0, WaveformBands.IndexAt(90_000, 0, 4096));          // an unknown duration
        Assert.Equal(0, WaveformBands.IndexAt(90_000, -1, 4096));
        Assert.Equal(0, WaveformBands.IndexAt(90_000, 180_000, 0));       // an empty payload
    }

    [Fact]
    public void IndexAt_reads_a_decimated_and_a_full_rate_answer_at_the_same_instant()
    {
        // 10 s into a 3-minute track: the spike sits at hop 500 of the 9,000-hop answer and at sample 227 of the same
        // answer max-pooled onto 4,096 — the same moment, because the index is DURATION-relative (V-D17).
        Assert.Equal(500, WaveformBands.IndexAt(10_000, 180_000, 9_000));
        Assert.Equal(227, WaveformBands.IndexAt(10_000, 180_000, WaveformBands.MaxSamples));
    }

    // ── ToColumns ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToColumns_is_the_decoders_old_reduction_at_read_time()
    {
        // equivalent, not bit-identical (V-D16): the kept samples are already per-band maxima
        var samples = new WaveSample[1_000];
        samples[0] = new WaveSample(10, 0, 0);
        samples[^1] = new WaveSample(200, 200, 200);
        var columns = new float[WaveformBands.Columns];

        Assert.True(WaveformBands.ToColumns(samples, columns));
        Assert.Equal(1f, columns[^1]);                                    // the loudest column is the ceiling, in the LAST column
        Assert.Equal(10f / 600f, columns[0], 5);                          // the first sample (10,0,0): 10 of the 600 peak
        Assert.Equal(0f, columns[100]);
    }

    [Fact]
    public void ToColumns_sums_the_per_band_maxima_of_each_columns_share()
    {
        // two samples share each of the 2 columns; the SUM is of the three band MAXIMA, not the max of the sums
        WaveSample[] samples = [new(100, 0, 0), new(0, 100, 0), new(0, 0, 50), new(0, 0, 0)];
        var columns = new float[2];

        Assert.True(WaveformBands.ToColumns(samples, columns));
        Assert.Equal(1f, columns[0]);                                     // 100 + 100 + 0 = 200, the peak
        Assert.Equal(0.25f, columns[1], 5);                               // 0 + 0 + 50 = 50 of 200
    }

    [Fact]
    public void ToColumns_stretches_a_payload_shorter_than_the_columns_and_clears_the_buffer()
    {
        var columns = new float[WaveformBands.Columns];
        Array.Fill(columns, 7f);                                          // a stale buffer must not leak through
        Assert.True(WaveformBands.ToColumns(new[] { new WaveSample(100, 100, 100) }, columns));
        Assert.All(columns, c => Assert.Equal(1f, c));
    }

    [Fact]
    public void ToColumns_is_false_for_silence_and_for_nothing()
    {
        var columns = new float[WaveformBands.Columns];
        Array.Fill(columns, 7f);
        Assert.False(WaveformBands.ToColumns(new WaveSample[500], columns));
        Assert.All(columns, c => Assert.Equal(0f, c));

        Assert.False(WaveformBands.ToColumns(ReadOnlySpan<WaveSample>.Empty, columns));
        Assert.False(WaveformBands.ToColumns(new WaveSample[4], Span<float>.Empty));
    }

    [Fact]
    public void A_WaveSample_is_three_bytes_because_the_edge_arena_casts_staged_bytes_to_it()
        => Assert.Equal(3, Unsafe.SizeOf<WaveSample>());

    // ── At / LevelAt ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void At_and_LevelAt_read_the_sample_under_the_playhead()
    {
        WaveSample[] samples = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(51, 102, 153)];

        // a 4 s track over four samples: one per second
        Assert.True(WaveformBands.At(samples, 0, 4_000, out float low, out float mid, out float high));
        Assert.Equal((1f, 0f, 0f), (low, mid, high));
        Assert.True(WaveformBands.At(samples, 1_500, 4_000, out low, out mid, out high));
        Assert.Equal((0f, 1f, 0f), (low, mid, high));
        Assert.True(WaveformBands.At(samples, 2_999, 4_000, out low, out mid, out high));
        Assert.Equal((0f, 0f, 1f), (low, mid, high));
        Assert.True(WaveformBands.At(samples, 3_500, 4_000, out low, out mid, out high));
        Assert.Equal(0.2f, low, 5);
        Assert.Equal(0.4f, mid, 5);
        Assert.Equal(0.6f, high, 5);
        Assert.Equal(0.4f, WaveformBands.LevelAt(samples, 3_500, 4_000), 5);

        // clamped at both ends
        Assert.True(WaveformBands.At(samples, 99_000, 4_000, out low, out _, out _));
        Assert.Equal(0.2f, low, 5);
        Assert.True(WaveformBands.At(samples, -1, 4_000, out low, out _, out _));
        Assert.Equal(1f, low);

        // nothing to read: false, zeros
        Assert.False(WaveformBands.At(ReadOnlySpan<WaveSample>.Empty, 1_000, 4_000, out low, out mid, out high));
        Assert.Equal((0f, 0f, 0f), (low, mid, high));
        Assert.Equal(0f, WaveformBands.LevelAt(ReadOnlySpan<WaveSample>.Empty, 1_000, 4_000));
    }

    // ── BeatGrid ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BeatGrid_phase_binary_search_and_downbeats()
    {
        uint[] beats = [500, 1_000, 1_500, 2_000, 2_500];

        // before the first beat: index -1, the first period
        Assert.True(BeatGrid.Phase(beats, 100, out int index, out float phase, out float period));
        Assert.Equal(-1, index);
        Assert.Equal(0f, phase);
        Assert.Equal(500f, period);

        // between beats: the phase is inside (0, 1)
        Assert.True(BeatGrid.Phase(beats, 1_250, out index, out phase, out period));
        Assert.Equal(1, index);
        Assert.Equal(0.5f, phase, 5);
        Assert.Equal(500f, period);

        // on a beat: phase 0 of THAT beat
        Assert.True(BeatGrid.Phase(beats, 1_500, out index, out phase, out period));
        Assert.Equal(2, index);
        Assert.Equal(0f, phase);

        // past the last beat the last period carries on
        Assert.True(BeatGrid.Phase(beats, 2_750, out index, out phase, out period));
        Assert.Equal(4, index);
        Assert.Equal(0.5f, phase, 5);
        Assert.Equal(500f, period);

        // an empty grid answers false (Pulse then takes the tempo)
        Assert.False(BeatGrid.Phase(ReadOnlySpan<uint>.Empty, 1_000, out index, out phase, out period));
        Assert.Equal(-1, index);

        // MarkDownbeats flags the nearest beat within 40 ms of a bar start and nothing beyond; the phase search ignores the bit
        uint[] bars = [1_020, 2_100];
        BeatGrid.MarkDownbeats(beats, bars);
        var marks = new bool[beats.Length];
        for (int i = 0; i < beats.Length; i++) marks[i] = BeatGrid.IsDownbeat(beats[i]);
        Assert.Equal(new[] { false, true, false, false, false }, marks);   // 2,100 is 100 ms from 2,000: not a downbeat
        Assert.Equal(1_000u, BeatGrid.StartMs(beats[1]));
        Assert.True(BeatGrid.Phase(beats, 1_250, out index, out phase, out period));
        Assert.Equal(1, index);
        Assert.Equal(0.5f, phase, 5);

        // a bar a little BEFORE its beat flags that beat, not the one behind it
        uint[] other = [1_000, 1_500];
        BeatGrid.MarkDownbeats(other, [1_480u]);
        Assert.False(BeatGrid.IsDownbeat(other[0]));
        Assert.True(BeatGrid.IsDownbeat(other[1]));

        // nothing to fold into
        BeatGrid.MarkDownbeats(Span<uint>.Empty, bars);
    }

    [Fact]
    public void TempoPhase_is_phase_locked_to_position_and_does_not_drift()
    {
        Assert.False(BeatGrid.TempoPhase(0, 1_000, out float phase, out float period));
        Assert.Equal(0f, phase);
        Assert.Equal(0f, period);

        // 120 BPM (tempo x10 = 1200): a 500 ms period, beat 0 at 0 ms
        Assert.True(BeatGrid.TempoPhase(1_200, 250, out phase, out period));
        Assert.Equal(500f, period);
        Assert.Equal(0.5f, phase, 5);

        // ... and the same phase 25 hours in: a float modulo would have drifted a beat by now (V-D18)
        Assert.True(BeatGrid.TempoPhase(1_200, 90_000_250, out phase, out period));
        Assert.Equal(0.5f, phase, 5);
    }
}
