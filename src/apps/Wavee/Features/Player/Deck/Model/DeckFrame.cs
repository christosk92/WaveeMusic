using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>
/// What a deck model hands back for ONE tick. Deliberately a flat POD of scalars rather than a per-deck shape: the
/// faces bind <c>DeckSignals</c>' fixed set of <c>FloatSignal</c>s and <c>DeckClock</c> diff-writes this into them,
/// so adding a deck never adds a signal, a bind or a write to the 30 Hz path.
/// </summary>
/// <param name="Frac">Progress 0..1 — every deck carries it somewhere (bar, sled, thumb, pack radius).</param>
/// <param name="Angle0">Record: platter degrees · Tape: LEFT reel · CD: disc · VU: LEFT needle.</param>
/// <param name="Angle1">Record: ARM degrees · Tape: RIGHT reel · VU: RIGHT needle.</param>
/// <param name="Lift">Record: 0 = stylus down .. 1 = lifted (the buffering bob overshoots above 1).</param>
/// <param name="Slide">Record: 0 = in the sleeve .. 1 = on the platter · Cassette: eject · CD: tray.</param>
/// <param name="Aux0">Per-model spare: tape LEFT pack scale, CD sled offset, …</param>
/// <param name="Aux1">Per-model spare: tape RIGHT pack scale, …</param>
/// <param name="CoverGen">Monotonic: bumps at the exact instant the artwork should CHANGE (mid-sleeve, mid-eject).</param>
/// <param name="Thump">A ONE-TICK edge (needle-drop): the host fires a one-shot, it is never a state.</param>
public readonly record struct DeckFrame(
    float Frac, float Angle0, float Angle1,
    float Lift,
    float Slide,
    float Aux0, float Aux1,
    int CoverGen, bool Thump, DeckPhaseName Phase);

/// <summary>The medium-agnostic name of what the deck is doing — what the diagnostics page and a face's caption read.
/// A model maps its OWN phase enum onto this; nothing outside a model switches on a model-private phase.</summary>
public enum DeckPhaseName : byte
{
    Idle, Cueing, NeedleDown, Playing, Pausing, Paused, SpinningUp, Seeking, NextTrack, ChangingRecord,
    RunOut, LockedGroove, AutoReturn, Buffering, Error, Stopped, Unavailable, Winding,
}

/// <summary>Which record-family face a <c>RecordModel</c> is driving. The physics are shared (one tonearm machine);
/// the variant only changes geometry and which parts a face draws — which is why it lives beside the frame rather
/// than inside the machine.</summary>
public enum RecordVariant : byte { Record, Turntable, Zune, Picture }

/// <summary>
/// The contract every deck's physics satisfies. Engine-free by construction: a model never sees an
/// <c>Element</c>, a signal or a bridge — it folds a <see cref="DeckInput"/> and returns a <see cref="DeckFrame"/>.
/// </summary>
public interface IDeckModel
{
    /// <summary>Advance by <paramref name="dtSec"/> and return this tick's frame. MUST be allocation-free.</summary>
    DeckFrame Tick(in DeckInput input, float dtSec);

    /// <summary>Analyser band levels 0..1 (spectrum decks); empty for every other deck.</summary>
    ReadOnlySpan<float> Bands { get; }

    /// <summary>Analyser peak caps 0..1, paired with <see cref="Bands"/>; empty for every other deck.</summary>
    ReadOnlySpan<float> Peaks { get; }

    /// <summary>Nothing is moving and nothing is scheduled — the ticker may stop. A model that lies here either burns
    /// a 30 Hz timer forever (false when settled) or freezes mid-animation (true when not).</summary>
    bool IsSettled { get; }
}

/// <summary>
/// The deck family's easing curves — the mockup's four CSS <c>cubic-bezier</c>s, evaluated exactly. Engine-free on
/// purpose: the tonearm machine is unit-tested against real curve values, so it cannot depend on the engine's
/// <c>Easing</c> table (which is a rendering concern and lives on the other side of the model boundary).
/// </summary>
public static class DeckEase
{
    /// <summary>The swing: <c>cubic-bezier(.4,0,.2,1)</c> — Fluent's standard accelerate-decelerate.</summary>
    public static float Std(float t) => CubicBezier(0.4f, 0f, 0.2f, 1f, t);

    /// <summary>The cue lever going UP: <c>cubic-bezier(.2,0,0,1)</c> — leaves instantly, arrives softly.</summary>
    public static float LiftUp(float t) => CubicBezier(0.2f, 0f, 0f, 1f, t);

    /// <summary>The silicone-damped DESCENT: <c>cubic-bezier(.2,.6,.3,1)</c> — most of the drop up front, then creep.</summary>
    public static float Damped(float t) => CubicBezier(0.2f, 0.6f, 0.3f, 1f, t);

    /// <summary>The record leaving its sleeve: <c>cubic-bezier(.2,.7,.2,1)</c>.</summary>
    public static float SlideOut(float t) => CubicBezier(0.2f, 0.7f, 0.2f, 1f, t);

    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>
    /// Evaluate the CSS cubic-bezier <c>(0,0) (x1,y1) (x2,y2) (1,1)</c> at abscissa <paramref name="x"/>.
    /// <para>A cubic Bézier is parametric, so <c>y</c> is NOT a closed form of <c>x</c>: solve <c>X(t) = x</c> for
    /// <c>t</c> first (6 Newton steps from <c>t = x</c>, which converges for every curve whose control abscissae are
    /// in [0,1] — they all are here), then evaluate <c>Y(t)</c>. A flat tangent stalls Newton, so the fallback is
    /// bisection; both are cheap and allocation-free.</para>
    /// </summary>
    public static float CubicBezier(float x1, float y1, float x2, float y2, float x)
    {
        if (!(x > 0f)) return 0f;    // also catches NaN
        if (x >= 1f) return 1f;
        if (x1 == y1 && x2 == y2) return x;   // the identity curve — no solve needed
        return Sample(y1, y2, SolveT(x1, x2, x));
    }

    // B(t) for one axis with endpoints pinned at 0 and 1: ((a·t + b)·t + c)·t.
    static float Sample(float p1, float p2, float t)
    {
        float c = 3f * p1, b = 3f * (p2 - p1) - c, a = 1f - c - b;
        return ((a * t + b) * t + c) * t;
    }

    static float Slope(float p1, float p2, float t)
    {
        float c = 3f * p1, b = 3f * (p2 - p1) - c, a = 1f - c - b;
        return (3f * a * t + 2f * b) * t + c;
    }

    static float SolveT(float x1, float x2, float x)
    {
        const float Eps = 1e-6f;
        float t = x;
        for (int i = 0; i < 6; i++)
        {
            float err = Sample(x1, x2, t) - x;
            if (MathF.Abs(err) < Eps) return t;
            float d = Slope(x1, x2, t);
            if (MathF.Abs(d) < 1e-5f) break;      // flat tangent — Newton cannot make progress
            t -= err / d;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        }
        float lo = 0f, hi = 1f;
        t = x;
        for (int i = 0; i < 32; i++)
        {
            float v = Sample(x1, x2, t);
            if (MathF.Abs(v - x) < Eps) break;
            if (v < x) lo = t; else hi = t;
            t = (lo + hi) * 0.5f;
        }
        return t;
    }
}
