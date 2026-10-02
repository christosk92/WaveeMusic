// ── Entities/Waveform.cs ───────────────────────────────────────────────────────────────────────────────────────────
// WaveformBands (the kind-237 payload contract + its pure readers), BeatGrid (the audio-analysis grid + the tempo fallback)
//
// Role: CORE
// Plan: docs/plans/wavee/fullscreen-flagship-implementation.md §4.5
//
// ONE payload, two readers. Kind 237 lands as N `WaveSample` triples (low/mid/high, 0..255) — the `EdgeTable<WaveSample>`
// payload of Edges.TrackWaveform, so Count == Total == N (O6) — at most MaxSamples per track (longer answers are
// max-pooled onto it), indexed by the track's DURATION rather than a fixed hop (V-D17). The track drawer's 220 columns
// and the stage's Horizon both derive from it HERE, at read time — no lossy reduction at decode. `public` because
// Wavee.Tests is a ProjectReference with no InternalsVisibleTo. No allocation after warm-up: every reader takes the
// caller's span.

namespace Wavee;

/// <summary>One kind-237 sample: the three band envelopes (0..255) at one instant. 3 bytes, unmanaged — the
/// <c>EdgeTable&lt;WaveSample&gt;</c> payload (the <c>FormatEdge</c> payload-only precedent, Edges.cs:716/:847).</summary>
public readonly record struct WaveSample(byte Low, byte Mid, byte High);

public static class WaveformBands
{
    /// <summary>The wire's nominal hop (<c>ThreeBandWaveforms.hop_ms</c> = 20 on every observed answer). INFORMATIONAL — readers
    /// index by duration (<see cref="IndexAt"/>), never by this, because long answers are decimated onto <see cref="MaxSamples"/>.</summary>
    public const int NominalHopMs = 20;
    /// <summary>The most samples kept per track: 8 B/sample in the edge arena (3 payload + 4 targets + 1 pending) ⇒ 32 KB. A
    /// 4-minute answer (≈ 12,000 hops) is max-pooled 3:1 (≈ 59 ms per sample — finer than Horizon's 66 ms per point).</summary>
    public const int MaxSamples = 4096;
    /// <summary>The drawer's column count (the decoder's old fixed reduction width, now applied at read time).</summary>
    public const int Columns = 220;

    /// <summary>Band fold for sample <paramref name="i"/> of <paramref name="n"/>: the MAX of <paramref name="band"/> over its share
    /// (max-pooling when decimating; one sample — nearest neighbour — when the band is shorter than n). 0 for an empty band.</summary>
    public static byte Fold(ReadOnlySpan<byte> band, int i, int n)
    {
        if (band.IsEmpty || n <= 0) return 0;
        int from = (int)((long)i * band.Length / n);
        int to = Math.Max(from + 1, (int)((long)(i + 1) * band.Length / n));
        byte m = 0;
        for (int k = from; k < to && k < band.Length; k++) if (band[k] > m) m = band[k];
        return m;
    }

    /// <summary>The sample under <paramref name="positionMs"/> of a <paramref name="durationMs"/>-long track (clamped) — index by
    /// DURATION, so decimated and full-rate answers read alike. 0 when either is unknown.</summary>
    public static int IndexAt(long positionMs, long durationMs, int n)
        => n <= 0 || durationMs <= 0 ? 0 : (int)Math.Clamp(positionMs * n / durationMs, 0L, n - 1L);

    /// <summary>The drawer's reduction: per column the MAX of each band over the column's share, SUMMED, normalised so the
    /// loudest column is 1.0 — the decoder's old arithmetic run at read time over the KEPT samples. On a decimated track a
    /// column is within one quantisation step of the old decode (the kept samples are themselves per-band maxima), so this
    /// is an equivalent, not a bit-identical, reproduction. False for silence.</summary>
    public static bool ToColumns(ReadOnlySpan<WaveSample> samples, Span<float> into)
    {
        into.Clear();
        int cols = into.Length, n = samples.Length, peak = 0;
        if (cols == 0 || n == 0) return false;
        Span<int> sums = cols <= 1024 ? stackalloc int[cols] : new int[cols];
        for (int c = 0; c < cols; c++)
        {
            int from = (int)((long)c * n / cols);
            int to = Math.Max(from + 1, Math.Min(n, (int)((long)(c + 1) * n / cols)));
            int l = 0, m = 0, h = 0;
            for (int i = from; i < to; i++)
            {
                var s = samples[i];
                if (s.Low > l) l = s.Low;
                if (s.Mid > m) m = s.Mid;
                if (s.High > h) h = s.High;
            }
            int sum = l + m + h;
            sums[c] = sum;
            if (sum > peak) peak = sum;
        }
        if (peak <= 0) return false;
        for (int c = 0; c < cols; c++) into[c] = sums[c] / (float)peak;
        return true;
    }

    /// <summary>The three bands at a position, 0..1 each; false for an empty payload.</summary>
    public static bool At(ReadOnlySpan<WaveSample> samples, long positionMs, long durationMs, out float low, out float mid, out float high)
    {
        low = mid = high = 0f;
        if (samples.IsEmpty) return false;
        var s = samples[IndexAt(positionMs, durationMs, samples.Length)];
        low = s.Low / 255f; mid = s.Mid / 255f; high = s.High / 255f;
        return true;
    }

    /// <summary>A combined 0..1 level at a position — the Connect / <c>--fake</c> stand-in for the live RMS.</summary>
    public static float LevelAt(ReadOnlySpan<WaveSample> samples, long positionMs, long durationMs)
        => At(samples, positionMs, durationMs, out float l, out float m, out float h) ? (l + m + h) / 3f : 0f;
}

/// <summary>The audio-analysis beat grid (Edges.TrackBeats: one <c>uint</c> per beat — start ms in bits 0..30, bit 31 =
/// a bar's downbeat) and the kind-222 tempo fallback. Pure; the stage's Pulse and the Horizon ticks read it.</summary>
public static class BeatGrid
{
    public const int MaxBeats = 8192, MaxBars = 2048;
    public const uint DownbeatBit = 0x8000_0000u, MsMask = 0x7FFF_FFFFu;
    public const int DownbeatToleranceMs = 40;

    public static uint StartMs(uint packed) => packed & MsMask;
    public static bool IsDownbeat(uint packed) => (packed & DownbeatBit) != 0;

    /// <summary>Fold the bars in: the beat nearest each bar start (within the tolerance) is flagged a downbeat. Both
    /// spans are in wire order (ascending); linear, no allocation.</summary>
    public static void MarkDownbeats(Span<uint> beats, ReadOnlySpan<uint> bars)
    {
        if (beats.IsEmpty) return;
        int j = 0;
        foreach (uint bar in bars)
        {
            long t = bar & MsMask;
            while (j + 1 < beats.Length && (beats[j + 1] & MsMask) <= t) j++;
            int best = j;
            if (j + 1 < beats.Length && Math.Abs((long)(beats[j + 1] & MsMask) - t) < Math.Abs((long)(beats[j] & MsMask) - t)) best = j + 1;
            if (Math.Abs((long)(beats[best] & MsMask) - t) <= DownbeatToleranceMs) beats[best] |= DownbeatBit;
        }
    }

    /// <summary>The beat under <paramref name="positionMs"/>: its index (−1 before the first beat), the 0..1 phase inside it
    /// and the local period. False for an empty grid. Binary search; no allocation.</summary>
    public static bool Phase(ReadOnlySpan<uint> beats, long positionMs, out int index, out float phase, out float periodMs)
    {
        index = -1; phase = 0f; periodMs = 0f;
        if (beats.IsEmpty) return false;
        int lo = 0, hi = beats.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if ((beats[mid] & MsMask) <= positionMs) { index = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (index < 0) { periodMs = beats.Length > 1 ? (beats[1] & MsMask) - (beats[0] & MsMask) : 500f; return true; }
        long cur = beats[index] & MsMask;
        long next = index + 1 < beats.Length ? (beats[index + 1] & MsMask) : cur + (index > 0 ? cur - (beats[index - 1] & MsMask) : 500);
        periodMs = Math.Max(1f, next - cur);
        phase = Math.Clamp((positionMs - cur) / periodMs, 0f, 1f);
        return true;
    }

    /// <summary>The tempo-grid fallback (kind 222, <c>Track.Tempo</c> ×10): a constant period phase-locked to position
    /// (beat 0 at 0 ms). False when the tempo is unknown. Double arithmetic: a long modulo of a truncated float period
    /// drifts a beat every few minutes (V-D18).</summary>
    public static bool TempoPhase(ushort tempoX10, long positionMs, out float phase, out float periodMs)
    {
        if (tempoX10 == 0) { phase = 0f; periodMs = 0f; return false; }
        periodMs = 600_000f / tempoX10;
        double p = positionMs / (double)periodMs;
        phase = (float)(p - Math.Floor(p));
        return true;
    }
}
