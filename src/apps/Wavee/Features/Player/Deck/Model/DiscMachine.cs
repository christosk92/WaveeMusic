using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>Engine-free physics for the CD/MiniDisc deck (Part 3, "Other decks"): a constant-linear-velocity spindle
/// (angular speed falls as the virtual read head moves outward) plus the sled travel and the tray-eject sequence on a
/// new album.</summary>
public sealed class DiscModel : IDeckModel
{
    enum EjectPhase : byte { None, Out, In }

    const float EjectStageMs = 500f;

    SpinIntegrator _disc = new(0.4f, 0.6f);
    EjectPhase _eject = EjectPhase.None;
    float _ejectElapsedMs;
    int _coverGen;
    bool _settled = true;

    public ReadOnlySpan<float> Bands => ReadOnlySpan<float>.Empty;
    public ReadOnlySpan<float> Peaks => ReadOnlySpan<float>.Empty;
    public bool IsSettled => _settled;

    public DeckFrame Tick(in DeckInput input, float dtSec)
    {
        float p = input.Frac;
        bool playing = input.Advancing || (input.PlayWhenReady && !input.Buffering);

        _disc.Step(ClvTargetDegPerSec(p, playing), dtSec);

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

        float aux0 = (20f + 26f * p) / 100f;   // sled travel, fraction of the disc diameter D
        var phase = input.Buffering ? DeckPhaseName.Buffering : playing ? DeckPhaseName.Playing : DeckPhaseName.Paused;
        _settled = _disc.AtRest && _eject == EjectPhase.None;

        return new DeckFrame(p, _disc.Angle, 0f, 0f, slide, aux0, 0f, _coverGen, false, phase);
    }

    /// Pure physics: CLV spindle target — fastest at the inner edge (p=0), slowest at the outer rim (p=1).
    public static float ClvTargetDegPerSec(float p, bool playing) => playing ? 3000f - 1800f * p : 0f;

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
