using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>
/// A first-order angular-velocity lag with an integrated angle — the ONE reason a deck's platter, hub or disc feels
/// like a motor rather than a CSS keyframe. Every rotating deck part goes through this.
/// <para>Physics: <c>ω' = (target − ω)·(1 − e^(−dt/τ))</c>, then <c>angle += ω·dt</c>, wrapped into [0, 360).
/// Separate rise/fall constants because a direct-drive turntable does NOT stop the way it starts — the SL-1200
/// reaches 33⅓ in ~0.7 s (3τ at τ↑ = 0.23 s) and its electronic brake takes ~1.6 s (3τ at τ↓ = 0.53 s).</para>
/// <para>Degrees per second is <c>rpm · 6</c> (33⅓ → 200 °/s, 45 → 270 °/s).</para>
/// </summary>
public struct SpinIntegrator
{
    /// <summary>Below this (°/s) a spin-down is called finished and snapped to a hard zero, so a settled deck's
    /// angle stops changing and the ticker can stop. Without the snap the exponential never reaches 0 and the deck
    /// writes a new (sub-pixel) angle forever.</summary>
    public const float RestEpsilonDegPerSec = 0.05f;

    /// <summary>Current angular velocity, °/s (signed).</summary>
    public float Omega;

    /// <summary>Current angle in degrees, always in [0, 360).</summary>
    public float Angle;

    /// <summary>Time constant while speeding UP (|target| &gt; |ω|), seconds.</summary>
    public float TauUp;

    /// <summary>Time constant while slowing DOWN, seconds.</summary>
    public float TauDown;

    public SpinIntegrator(float tauUp, float tauDown)
    {
        TauUp = tauUp;
        TauDown = tauDown;
    }

    /// <summary>Advance one tick toward <paramref name="targetDegPerSec"/>. <paramref name="dir"/> is +1/−1 for a
    /// part that turns the other way (a cassette's right hub against its left) without needing a second target.</summary>
    public void Step(float targetDegPerSec, float dt, int dir = 1)
    {
        float tau = MathF.Abs(targetDegPerSec) > MathF.Abs(Omega) ? TauUp : TauDown;
        // tau <= 0 (or an unset default-constructed integrator) means "no lag": land on the target this tick.
        float k = tau > 0f ? 1f - MathF.Exp(-dt / tau) : 1f;
        Omega += (targetDegPerSec - Omega) * k;
        if (targetDegPerSec == 0f && MathF.Abs(Omega) < RestEpsilonDegPerSec) Omega = 0f;
        Angle = (Angle + Omega * dt * dir) % 360f;
        if (Angle < 0f) Angle += 360f;
    }

    /// <summary>The part has stopped turning (an exact zero — see <see cref="RestEpsilonDegPerSec"/>).</summary>
    public readonly bool AtRest => Omega == 0f;
}
