using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>
/// The record family's model: Record, Turntable, Zune and Picture disc all spin the same platter and run the same
/// <see cref="TonearmMachine"/>. Only the FACE differs — the Zune draws no arm at all, and runs the machine anyway so
/// that its platter brakes, spins up and rides the run-out exactly like its siblings.
///
/// <para>Engine-free and allocation-free: one <see cref="TonearmState"/> value, one <see cref="SpinIntegrator"/>, and
/// a <see cref="DeckFrame"/> struct out per tick.</para>
/// </summary>
public sealed class RecordModel : IDeckModel
{
    /// <summary>SL-1200 spin-up: 3·τ ≈ the quoted 0.7 s to 33⅓.</summary>
    public const float PlatterTauUpSec = 0.23f;
    /// <summary>…and a 1.6 s electronic brake on the way down.</summary>
    public const float PlatterTauDownSec = 0.53f;
    /// <summary>A 33⇄45 change is a pitch SLEW, not a start: both time constants tighten for the duration.</summary>
    public const float SpeedRampTauSec = 0.17f;
    /// <summary>How long the tightened time constants stay in force after a speed change.</summary>
    public const long SpeedRampMs = 600;

    readonly RecordVariant _variant;
    TonearmState _state;
    SpinIntegrator _platter;
    DeckPhaseName _phase;
    float _rpm;
    long _rampUntilMs;

    /// <summary>
    /// Seeded from the CURRENT transport (<see cref="TonearmMachine.Seed"/>): mounting the deck mid-song must not
    /// replay the cueing sequence.
    /// </summary>
    public RecordModel(in DeckInput seed, RecordVariant variant)
    {
        _variant = variant;
        _state = TonearmMachine.Seed(in seed);
        _platter = new SpinIntegrator(PlatterTauUpSec, PlatterTauDownSec);
        _rpm = seed.Rpm;
        _phase = TonearmMachine.NameOf(in _state);
        // The platter is already at speed if the record was already playing — no spin-up on mount.
        if (_state.PlatterOn && !seed.ReducedMotion) _platter.Omega = seed.Rpm * 6f;
    }

    /// <summary>Which face is drawing this model. The machine is identical for all four.</summary>
    public RecordVariant Variant => _variant;

    /// <summary>The tonearm's state, for the diagnostics page and the tests.</summary>
    public TonearmState State => _state;

    /// <summary>Platter angle in degrees, 0..360.</summary>
    public float PlatterAngleDeg => _platter.Angle;

    /// <summary>Platter angular velocity in degrees per second.</summary>
    public float PlatterOmegaDegPerSec => _platter.Omega;

    /// <summary>True while the 33⇄45 pitch slew is in force.</summary>
    public bool IsSpeedRamping => _rampUntilMs != 0;

    /// <summary>The last phase this model produced.</summary>
    public DeckPhaseName Phase => _phase;

    public DeckFrame Tick(in DeckInput input, float dtSec)
    {
        ApplySpeedRamp(in input);
        _state = TonearmMachine.Step(_state, in input);
        var f = TonearmMachine.Sample(in _state, in input);
        _platter.Step(f.PlatterTargetDegPerSec, dtSec);
        _phase = f.Name;
        return new DeckFrame(input.Frac, _platter.Angle, f.ArmDeg, f.Lift, f.Slide, 0f, 0f, _state.CoverGen, f.Thump, f.Name);
    }

    /// <summary>Nothing is moving: the ticker may stop until the transport says otherwise.</summary>
    public bool IsSettled
        => (_state.Phase is TonearmPhase.Idle or TonearmPhase.Paused or TonearmPhase.Stopped or TonearmPhase.Unavailable)
           && _platter.AtRest;

    /// <summary>The record family has no analyser.</summary>
    public ReadOnlySpan<float> Bands => ReadOnlySpan<float>.Empty;

    /// <inheritdoc cref="Bands"/>
    public ReadOnlySpan<float> Peaks => ReadOnlySpan<float>.Empty;

    void ApplySpeedRamp(in DeckInput input)
    {
        if (input.Rpm != _rpm)
        {
            _rpm = input.Rpm;
            _rampUntilMs = Math.Max(1L, input.NowMs + SpeedRampMs);
            _platter.TauUp = _platter.TauDown = SpeedRampTauSec;
        }
        else if (_rampUntilMs != 0 && input.NowMs >= _rampUntilMs)
        {
            _rampUntilMs = 0;
            _platter.TauUp = PlatterTauUpSec;
            _platter.TauDown = PlatterTauDownSec;
        }
    }
}
