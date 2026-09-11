using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>Which tape transport a <see cref="TapeModel"/> drives — the cassette's two small hubs or the reel-to-reel's
/// two big open flanges. Same physics (constant LINEAR tape speed → angular speed falls as the take-up pack grows),
/// different radii/rates per the mockup's geometry table.</summary>
public enum TapeKind : byte { Cassette, Reel }

/// <summary>Engine-free physics for the Cassette and Reel-to-reel decks (Part 3, "Other decks"). One instance per
/// mounted deck; <see cref="Tick"/> is allocation-free and pure aside from its own mutable state.</summary>
public sealed class TapeModel : IDeckModel
{
    enum EjectPhase : byte { None, Out, In }

    const float EjectStageMs = 500f;
    const float WindWindowMs = 900f;
    const long BigJumpMs = 2_500;

    readonly TapeKind _kind;
    SpinIntegrator _left, _right;

    long _windUntilMs;
    long _prevPosMs;
    long? _prevSeekTarget;
    bool _initialized;

    EjectPhase _eject = EjectPhase.None;
    float _ejectElapsedMs;
    int _coverGen;
    bool _settled = true;

    public TapeModel(TapeKind kind)
    {
        _kind = kind;
        float tauUp = kind == TapeKind.Cassette ? 0.25f : 0.35f;
        float tauDown = kind == TapeKind.Cassette ? 0.30f : 0.40f;
        _left = new SpinIntegrator(tauUp, tauDown);
        _right = new SpinIntegrator(tauUp, tauDown);
    }

    public ReadOnlySpan<float> Bands => ReadOnlySpan<float>.Empty;
    public ReadOnlySpan<float> Peaks => ReadOnlySpan<float>.Empty;
    public bool IsSettled => _settled;

    public DeckFrame Tick(in DeckInput input, float dtSec)
    {
        float p = input.Frac;
        var (rL, rR) = PackRadii(_kind, p);
        float rMax = _kind == TapeKind.Cassette ? 26f : 92f;

        bool playing = input.Advancing || (input.PlayWhenReady && !input.Buffering);

        // A committed seek (SeekTargetMs going null → non-null) or a big remote jump between ticks both mean "the tape
        // needs to physically catch up" — 900 ms of fast-wind before settling back to real-time speed.
        bool seekCommitted = _initialized && input.SeekTargetMs is not null && _prevSeekTarget is null;
        bool bigJump = _initialized && Math.Abs(input.PositionMs - _prevPosMs) > BigJumpMs;
        if (seekCommitted || bigJump) _windUntilMs = input.NowMs + (long)WindWindowMs;
        float wind = input.NowMs < _windUntilMs ? (_kind == TapeKind.Cassette ? 4f : 5f) : 1f;

        _left.Step(Omega(_kind, rL, wind, playing), dtSec);
        _right.Step(Omega(_kind, rR, wind, playing), dtSec);

        float slide = 1f;
        if (input.Boundary is DeckBoundary.NaturalNewAlbum or DeckBoundary.SkipNewAlbum && _eject == EjectPhase.None)
        {
            _eject = EjectPhase.Out;
            _ejectElapsedMs = 0f;
        }
        if (_eject != EjectPhase.None)
        {
            _ejectElapsedMs += dtSec * 1000f;
            if (_eject == EjectPhase.Out)
            {
                float t = Clamp01(_ejectElapsedMs / EjectStageMs);
                slide = 1f - t;
                if (_ejectElapsedMs >= EjectStageMs)
                {
                    _coverGen++;
                    _eject = EjectPhase.In;
                    _ejectElapsedMs -= EjectStageMs;
                }
            }
            if (_eject == EjectPhase.In)
            {
                float t = Clamp01(_ejectElapsedMs / EjectStageMs);
                slide = t;
                if (_ejectElapsedMs >= EjectStageMs)
                {
                    _eject = EjectPhase.None;
                    slide = 1f;
                }
            }
        }

        _prevSeekTarget = input.SeekTargetMs;
        _prevPosMs = input.PositionMs;
        _initialized = true;

        var phase = wind > 1f ? DeckPhaseName.Winding : playing ? DeckPhaseName.Playing : DeckPhaseName.Paused;
        _settled = _left.AtRest && _right.AtRest && _eject == EjectPhase.None;

        return new DeckFrame(p, _left.Angle, _right.Angle, 0f, slide, rL / rMax, rR / rMax, _coverGen, false, phase);
    }

    /// Pure geometry: the left (supply) and right (take-up) pack radii, in % of S, for a given play fraction.
    public static (float rL, float rR) PackRadii(TapeKind kind, float p) => kind == TapeKind.Cassette
        ? (26f - 13f * p, 13f + 13f * p)
        : (92f - 48f * p, 44f + 48f * p);

    /// Pure physics: constant linear tape speed (scaled by `wind`) turned into an angular speed at radius `r`.
    /// Negative because both hubs spin the same visual direction while the geometry angle convention is CCW-positive.
    public static float Omega(TapeKind kind, float r, float wind, bool playing)
    {
        if (!playing) return 0f;
        float baseDegPerSec = wind * (kind == TapeKind.Cassette ? 900f : 6000f);
        return -baseDegPerSec / r;
    }

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
