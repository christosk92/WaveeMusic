using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>Engine-free "analyser" for the Winamp/WMP-style decks (Part 3, "Other decks"): band levels synthesized
/// from the RMS/peak tap (or a flat fallback when there is no tap) rather than a real FFT, plus a peak-hold/fall
/// overlay. Scope mode instead fills <see cref="Bands"/> with a fake waveform sample. Arrays are preallocated once in
/// the constructor so <see cref="Tick"/> never allocates.</summary>
public sealed class LevelModel : IDeckModel
{
    const float PeakHoldMs = 400f;
    const float PeakFallPerSec = 1.2f;
    const float AttackCoeff = 0.5f;
    const float ReleaseCoeff = 0.12f;
    const float Floor = 0.02f;
    const float SettleEps = 0.005f;

    readonly int _bandCount;
    readonly bool _scope;
    readonly Func<(float rms, float peak)?>? _levels;
    readonly float _seed;
    readonly float[] _bands;
    readonly float[] _peaks;
    readonly float[] _peakHoldMs;

    float _t;
    bool _settled;

    public LevelModel(int bands, bool scope, Func<(float rms, float peak)?> levels, float? seed = null)
    {
        _bandCount = bands;
        _scope = scope;
        _levels = levels;
        _seed = seed ?? (float)(new Random().NextDouble() * 1000.0);
        _bands = new float[bands];
        _peaks = new float[bands];
        _peakHoldMs = new float[bands];
        for (int i = 0; i < bands; i++) _bands[i] = Floor;
    }

    public ReadOnlySpan<float> Bands => _bands;
    public ReadOnlySpan<float> Peaks => _peaks;
    public bool IsSettled => _settled;

    public DeckFrame Tick(in DeckInput input, float dtSec)
    {
        _t += dtSec;
        bool playing = input.Advancing || (input.PlayWhenReady && !input.Buffering);

        var sample = _levels?.Invoke();
        float env, rmsN;
        if (sample is { } s)
        {
            env = Clamp01(s.peak * 2.2f);
            rmsN = Clamp01(s.rms * 3.5f);
        }
        else
        {
            env = playing ? 0.8f : 0f;
            rmsN = env;
        }

        if (_scope)
        {
            for (int i = 0; i < _bandCount; i++)
            {
                float v = 0.5f + 0.38f * MathF.Sin(i * 0.5f + _t * 9f) * MathF.Sin(i * 0.08f + _t * 2f) * env;
                _bands[i] = v;
                UpdatePeak(i, v, dtSec);
            }
        }
        else
        {
            for (int i = 0; i < _bandCount; i++)
            {
                float target = playing
                    ? MathF.Max(Floor, env * (0.35f + 0.45f * MathF.Sin(_t * (3f + 0.37f * i) + _seed + i) * MathF.Sin(1.3f * _t + 0.8f * i)) + rmsN * Hash(i, _t) * 0.15f)
                    : Floor;
                float coeff = target > _bands[i] ? AttackCoeff : ReleaseCoeff;
                _bands[i] += (target - _bands[i]) * coeff;
                // The exponential release never reaches the floor exactly; snap the last half-percent so a silent
                // deck really settles (and the ticker can stop) instead of asymptoting forever.
                if (!playing && _bands[i] < Floor + SettleEps) _bands[i] = Floor;
                UpdatePeak(i, _bands[i], dtSec);
            }
        }

        bool settled = !playing;
        if (settled)
        {
            for (int i = 0; i < _bandCount; i++)
            {
                if (_bands[i] > Floor) { settled = false; break; }
            }
        }
        _settled = settled;

        var phase = playing ? DeckPhaseName.Playing : DeckPhaseName.Paused;
        return new DeckFrame(input.Frac, 0f, 0f, 0f, 1f, 0f, 0f, 0, false, phase);
    }

    void UpdatePeak(int i, float band, float dtSec)
    {
        if (band >= _peaks[i])
        {
            _peaks[i] = band;
            _peakHoldMs[i] = PeakHoldMs;
        }
        else if (_peakHoldMs[i] > 0f)
        {
            _peakHoldMs[i] -= dtSec * 1000f;
        }
        else
        {
            _peaks[i] = MathF.Max(band, _peaks[i] - PeakFallPerSec * dtSec);
        }
    }

    /// A cheap deterministic pseudo-random value in 0..1 from a band index and a coarse (1/30 s) time bucket.
    static float Hash(int i, float t)
    {
        unchecked
        {
            int n = i * 374_761_393 + (int)MathF.Floor(t * 30f) * 668_265_263;
            n = (n ^ (n >> 13)) * 1_274_126_177;
            n ^= n >> 16;
            return (n & 0x7fffffff) / 2_147_483_648f;
        }
    }

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
