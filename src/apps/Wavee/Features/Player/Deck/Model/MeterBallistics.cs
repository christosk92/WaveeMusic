using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>Engine-free physics for the Hi-fi VU deck (Part 3, "Other decks"): dB-to-needle-angle mapping plus a
/// first-order ballistic lag (slow for VU, fast for PPM). When the level tap has no data but the deck is playing, a
/// small constant plus a slow breathing sine keeps the needles alive instead of pinned at the floor.</summary>
public sealed class MeterModel : IDeckModel
{
    const float RestDeg = -45f;
    const float BreatheOmega = 0.6f;    // rad/s — the "slow sin" for the no-tap-but-playing case
    const float RightOffsetOmega = 2.1f;  // rad/s — L→R stereo derivation

    readonly bool _ppm;
    readonly Func<(float rms, float peak)?>? _levels;

    float _t;
    float _angleL = RestDeg, _angleR = RestDeg;
    bool _playing;

    public MeterModel(bool ppm, Func<(float rms, float peak)?> levels)
    {
        _ppm = ppm;
        _levels = levels;
    }

    public ReadOnlySpan<float> Bands => ReadOnlySpan<float>.Empty;
    public ReadOnlySpan<float> Peaks => ReadOnlySpan<float>.Empty;
    public bool IsSettled => !_playing && MathF.Abs(_angleL - RestDeg) < 0.25f && MathF.Abs(_angleR - RestDeg) < 0.25f;

    public DeckFrame Tick(in DeckInput input, float dtSec)
    {
        _t += dtSec;
        _playing = input.Advancing || (input.PlayWhenReady && !input.Buffering);

        float targetL, targetR;
        if (!_playing)
        {
            targetL = RestDeg;
            targetR = RestDeg;
        }
        else
        {
            var sample = _levels?.Invoke();
            float dbL = sample is { } s
                ? RmsToDb(s.rms)
                : RmsToDb(0.12f) + 3f * MathF.Sin(BreatheOmega * _t);
            float dbR = dbL + 1.5f * MathF.Sin(RightOffsetOmega * _t);
            targetL = DbToDeg(dbL);
            targetR = DbToDeg(dbR);
        }

        float tau = _ppm ? 0.05f : 0.3f;
        float k = 1f - MathF.Exp(-dtSec / tau);
        _angleL += (targetL - _angleL) * k;
        _angleR += (targetR - _angleR) * k;

        var phase = _playing ? DeckPhaseName.Playing : DeckPhaseName.Paused;
        return new DeckFrame(input.Frac, _angleL, _angleR, 0f, 1f, 0f, 0f, 0, false, phase);
    }

    public static float DbToDeg(float db) => Math.Clamp(-45f + (db + 20f) / 23f * 90f, -48f, 48f);
    public static float RmsToDb(float rms) => 20f * MathF.Log10(MathF.Max(rms, 1e-4f)) + 18f;
}
