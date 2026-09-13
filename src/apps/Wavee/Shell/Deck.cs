// ── Shell/Deck.cs ──────────────────────────────────────────────────────────────────────────────────────────────────
// the 13 model classes ported verbatim + a pure Fold + the catalog
//
// Role: CORE
// Owner: K
// Wave: 4
// Budget: 1500 lines
// Spec: ch 23 §9 (1,450-1,600)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE DECK'S PHYSICS. Twelve player presets, six models, one contract: a model folds a `Deck.Input` and returns a
// `Deck.Frame`. It never sees an `Element`, a `Signal`, a bridge or a clock — which is exactly why every one of the
// thirteen classes below is unit-testable without a GPU, and why `Wavee.Tests` can drive the entire tonearm
// choreography at a 33 ms step and assert against the constants rather than against a screenshot.
//
// THE THREE-LAYER SEAM, whole:
//   1. `Deck.cs` (here)      physics. `System`-only. Twelve presets → six models (`Deck.Models.Create`).
//   2. `Deck.UI.cs`          the host, the 30 Hz clock, the signal slab, art, gesture, the record family's face.
//   3. `Deck.Faces.cs`       the other eight faces.
// The clock diff-writes one `Frame` into a FIXED slab of signals per tick inside ONE batch, value-gated at a
// perceptual quantum; the faces bind that slab ONCE at mount and are never re-rendered by the 30 Hz path. Adding a
// deck therefore adds no signal, no bind and no wake.
//
// WHAT `Fold` IS FOR (ch 23 §8). 0.2.9's `DeckClock.Fold` read `PlaybackBridge` and `FrameTime` directly, so the one
// function that guarantees "mounted mid-song" and "ticked mid-song" see IDENTICAL inputs was the one function no test
// could reach. Here it is split: `Deck.Fold(in TransportFacts, ref PositionInterpolator, …)` is pure and lives here;
// `Deck.UI.cs`'s three-line shell reads the signals and hands the facts in. The boundary / seek / synthetic-jump logic
// is then testable.
//
// CLOCK DISCIPLINE (Christos's rule, `animations-sample-frame-time`). `Input.NowMs` is the FRAME's present time
// (`FrameTime.NowQpc` → ms), never `Environment.TickCount64`, whose 15.6 ms granularity steps a 30 Hz integrator. No
// clock is read in this file at all: every timer is relative to the `NowMs` the caller stamps.
//
// Rules: allocation-free ticks (P8) — the only arrays a model owns are allocated once in its constructor; no LINQ, no
// closures on the tick path, no async, no boxing (P9). Ported VERBATIM from `Features/Player/Deck/Model/**`; the
// decisions are not re-derived, only the boundary rule's inputs change from uris to entity SLOTS (ch 23 §8).

namespace Wavee;

public static partial class Deck
{
    // ── 1. the two value types every model is a function of ─────────────────────────────────────────────────────────

    /// <summary>The medium-agnostic name of what the deck is doing — what the diagnostics page and a face's caption
    /// read. A model maps its OWN phase enum onto this; nothing outside a model switches on a model-private phase.</summary>
    public enum PhaseName : byte
    {
        Idle, Cueing, NeedleDown, Playing, Pausing, Paused, SpinningUp, Seeking, NextTrack, ChangingRecord,
        RunOut, LockedGroove, AutoReturn, Buffering, Error, Stopped, Unavailable, Winding,
    }

    /// <summary>Which record-family face a <see cref="RecordModel"/> is driving. The physics are shared (one tonearm
    /// machine); the variant only changes geometry and which parts a face draws — which is why it lives beside the
    /// frame rather than inside the machine.</summary>
    public enum RecordVariant : byte { Record, Turntable, Zune, Picture }

    /// <summary>What a deck model hands back for ONE tick. Deliberately a flat POD of scalars rather than a per-deck
    /// shape: the faces bind a FIXED slab of float signals and the clock diff-writes this into them, so adding a deck
    /// never adds a signal, a bind or a write to the 30 Hz path.</summary>
    /// <param name="Frac">Progress 0..1 — every deck carries it somewhere (bar, sled, thumb, pack radius).</param>
    /// <param name="Angle0">Record: platter degrees · Tape: LEFT reel · CD: disc · VU: LEFT needle.</param>
    /// <param name="Angle1">Record: ARM degrees · Tape: RIGHT reel · VU: RIGHT needle.</param>
    /// <param name="Lift">Record: 0 = stylus down .. 1 = lifted (the buffering bob overshoots above 1).</param>
    /// <param name="Slide">Record: 0 = in the sleeve .. 1 = on the platter · Cassette: eject · CD: tray.</param>
    /// <param name="Aux0">Per-model spare: tape LEFT pack scale, CD sled offset, …</param>
    /// <param name="Aux1">Per-model spare: tape RIGHT pack scale, …</param>
    /// <param name="CoverGen">Monotonic: bumps at the exact instant the artwork should CHANGE (mid-sleeve,
    /// mid-eject).</param>
    /// <param name="Thump">A ONE-TICK edge (needle-drop): the host fires a one-shot, it is never a state.</param>
    public readonly record struct Frame(
        float Frac, float Angle0, float Angle1,
        float Lift,
        float Slide,
        float Aux0, float Aux1,
        int CoverGen, bool Thump, PhaseName Phase);

    /// <summary>The contract every deck's physics satisfies. Engine-free by construction: a model never sees an
    /// element, a signal or a bridge — it folds an <see cref="Input"/> and returns a <see cref="Frame"/>.</summary>
    public interface IModel
    {
        /// <summary>Advance by <paramref name="dtSec"/> and return this tick's frame. MUST be allocation-free.</summary>
        Frame Tick(in Input input, float dtSec);

        /// <summary>Analyser band levels 0..1 (spectrum decks); empty for every other deck.</summary>
        ReadOnlySpan<float> Bands { get; }

        /// <summary>Analyser peak caps 0..1, paired with <see cref="Bands"/>; empty for every other deck.</summary>
        ReadOnlySpan<float> Peaks { get; }

        /// <summary>Nothing is moving and nothing is scheduled — the ticker may stop. A model that lies here either
        /// burns a 30 Hz timer forever (false when settled) or freezes mid-animation (true when not).</summary>
        bool IsSettled { get; }
    }

    /// <summary>WHY a deck's medium changed track, as an EDGE (set for exactly one fold, then cleared by the clock).
    /// The record family needs all four combinations because the physical gesture differs: a same-album advance
    /// re-cues the arm on the SAME disc, a new album pulls the record and swaps the sleeve, and a USER skip is quicker
    /// (300 ms lift) than a natural run-through (450 ms).
    /// <para><see cref="RepeatOne"/> is the one non-track-change edge: the uri did not move, but the medium did — the
    /// arm re-cues the same disc.</para></summary>
    public enum Boundary : byte { None, NaturalSameAlbum, NaturalNewAlbum, SkipSameAlbum, SkipNewAlbum, RepeatOne }

    /// <summary>The transport phase as the DECK sees it. <see cref="Ended"/> is "not playing with the playhead inside
    /// the last 1.5 s"; <see cref="Transitioning"/> is reserved for a host that can report it. Named
    /// <c>TransportPhase</c> rather than <c>Phase</c> so it can never be read as <see cref="Playback.Phase"/>, which
    /// is a different (reducer-side) vocabulary.</summary>
    public enum TransportPhase : byte
    {
        Idle, Resolving, Buffering, Playing, Pausing, Paused, Seeking, Transitioning, Recovering, Ended, Failed,
    }

    /// <summary>ONE fold of the transport for ONE tick. Pure data, engine-free, so every deck model is a unit-testable
    /// value function of it. Built in exactly one place — <see cref="Fold"/> — which is also what seeds a freshly
    /// mounted deck, so "mounted mid-song" and "ticked mid-song" see the identical shape.</summary>
    /// <param name="NowMs">A monotonic clock: the FRAME's present time, never the 15.6 ms-granular tick count. Every
    /// timer in every model is relative to it.</param>
    /// <param name="Advancing">Audio is actually coming out (playing ∧ not buffering).</param>
    /// <param name="Buffering">Buffering, OR an intent that is still resolving.</param>
    /// <param name="QueueEnded">Phase == Ended with no standing intent — the run-out / locked-groove trigger.</param>
    /// <param name="SeekTargetMs">A committed seek awaiting its acknowledgement, or a synthesized remote jump.</param>
    /// <param name="ScrubTargetMs">Pointer-owned position: non-null means a drag is LIVE right now.</param>
    /// <param name="PositionMs">INTERPOLATED between the transport's ~1 Hz anchors — never the raw reported value.</param>
    /// <param name="ReducedMotion">A VALUE, read every fold: models collapse their phase durations, they never change
    /// shape. Never an early return — that would shift hook order when the OS flag flips.</param>
    public readonly record struct Input(
        long NowMs, bool HasTrack, TransportPhase Phase, bool PlayWhenReady,
        bool Advancing,
        bool Buffering,
        bool Error, bool QueueEnded,
        Boundary Boundary,
        long? SeekTargetMs,
        long? ScrubTargetMs,
        long PositionMs,
        long DurationMs, bool RepeatOne, float Rpm, bool ReducedMotion)
    {
        /// <summary>Progress through the track, 0..1. An unknown duration reads 0 — never a fiction: the arm parks at
        /// the lead-in rather than guessing.</summary>
        public float Frac => DurationMs > 0 ? Clamp01(PositionMs / (float)DurationMs) : 0f;

        /// <summary>The same mapping for an arbitrary position (a seek target, a scrub target).</summary>
        public float FracOf(long ms) => DurationMs > 0 ? Clamp01(ms / (float)DurationMs) : 0f;

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }

    // ── 2. DeckEase — the four CSS cubic-béziers, evaluated exactly ─────────────────────────────────────────────────

    /// <summary>The deck family's easing curves — the mockup's four CSS <c>cubic-bezier</c>s, evaluated exactly.
    /// Engine-free on purpose: the tonearm machine is unit-tested against real curve values, so it cannot depend on
    /// the engine's easing table (a rendering concern, on the other side of the model boundary).</summary>
    public static class Ease
    {
        /// <summary>The swing: <c>cubic-bezier(.4,0,.2,1)</c> — Fluent's standard accelerate-decelerate.</summary>
        public static float Std(float t) => CubicBezier(0.4f, 0f, 0.2f, 1f, t);

        /// <summary>The cue lever going UP: <c>cubic-bezier(.2,0,0,1)</c> — leaves instantly, arrives softly.</summary>
        public static float LiftUp(float t) => CubicBezier(0.2f, 0f, 0f, 1f, t);

        /// <summary>The silicone-damped DESCENT: <c>cubic-bezier(.2,.6,.3,1)</c> — most of the drop up front, then a
        /// creep.</summary>
        public static float Damped(float t) => CubicBezier(0.2f, 0.6f, 0.3f, 1f, t);

        /// <summary>The record leaving its sleeve: <c>cubic-bezier(.2,.7,.2,1)</c>.</summary>
        public static float SlideOut(float t) => CubicBezier(0.2f, 0.7f, 0.2f, 1f, t);

        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        /// <summary>Evaluate the CSS cubic-bezier <c>(0,0) (x1,y1) (x2,y2) (1,1)</c> at abscissa <paramref name="x"/>.
        /// <para>A cubic Bézier is parametric, so <c>y</c> is NOT a closed form of <c>x</c>: solve <c>X(t) = x</c> for
        /// <c>t</c> first (6 Newton steps from <c>t = x</c>, which converges for every curve whose control abscissae
        /// are in [0,1] — they all are here), then evaluate <c>Y(t)</c>. A flat tangent stalls Newton, so the fallback
        /// is bisection; both are cheap and allocation-free.</para></summary>
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

    // ── 3. DeckBoundaryRules — what just happened to the medium ─────────────────────────────────────────────────────

    /// <summary>The ONE answer to "what just happened to the medium?", as a pure function of the previous and current
    /// transport facts. None of the four cases is observable from a screenshot, which is why every record-family
    /// gesture hangs off a tested rule rather than an inline comparison.
    ///
    /// <para><b>0.3 change (ch 23 §8): slots, not uris.</b> The comparisons are entity SLOTS now. A slot of
    /// <see cref="UnknownSlot"/> or below is "we have nothing to compare" — never a synthesized skip, or mounting the
    /// deck while nothing played would fake one — and an unknown ALBUM slot reads as "not the same album", never as
    /// same. Every other branch is identical.</para></summary>
    public static class BoundaryRules
    {
        /// <summary>Slot 0 is every table's permanent "none" row, and a negative slot is an invalid handle. Either
        /// means UNKNOWN here.</summary>
        public const int UnknownSlot = 0;

        /// <summary>How close to the end counts as "the track ran out" rather than "the user skipped".</summary>
        public const long NaturalEndWindowMs = 1_500;

        /// <summary>How near the start the new position must be for a same-track change to read as a repeat-one
        /// rewind.</summary>
        public const long RepeatRewindMs = 2_000;

        /// <summary>Classify the edge. The <c>prev*</c> values are the LAST fold's; the unprefixed ones are the fold
        /// being built.
        /// <para>Ordering matters: the same-track arm runs FIRST (a repeat-one rewind is not a track change), and an
        /// unknown slot on either side is inert.</para></summary>
        public static Boundary Classify(int prevTrackSlot, int prevAlbumSlot, long prevPosMs, long prevDurMs,
                                        TransportPhase prevPhase,
                                        int trackSlot, int albumSlot, long posMs, bool repeatOne)
        {
            if (prevTrackSlot == trackSlot)
                return repeatOne && prevDurMs > 0 && prevPosMs >= prevDurMs - NaturalEndWindowMs && posMs < RepeatRewindMs
                    ? Boundary.RepeatOne
                    : Boundary.None;
            if (prevTrackSlot <= UnknownSlot || trackSlot <= UnknownSlot) return Boundary.None;
            bool natural = prevPhase == TransportPhase.Transitioning
                        || (prevDurMs > 0 && prevPosMs >= prevDurMs - NaturalEndWindowMs);
            bool sameAlbum = albumSlot > UnknownSlot && prevAlbumSlot == albumSlot;
            return (natural, sameAlbum) switch
            {
                (true, true) => Boundary.NaturalSameAlbum,
                (true, false) => Boundary.NaturalNewAlbum,
                (false, true) => Boundary.SkipSameAlbum,
                _ => Boundary.SkipNewAlbum,
            };
        }
    }

    // ── 4. PositionInterpolator — the smooth playhead ───────────────────────────────────────────────────────────────

    /// <summary>The smooth playhead, minus the DVR arm (a deck draws a TRACK's medium; a live broadcast has no groove
    /// to be a fraction of).
    /// <para>The transport reports position at ~1 Hz. A deck writing an arm angle 30 times a second off that raw
    /// number would step once a second and stand still in between, so each tick extrapolates from the last anchor's
    /// wall clock instead. Anchoring is a separate call because the anchor edge is a SIGNAL change, while the estimate
    /// is wanted on every tick.</para></summary>
    public struct PositionInterpolator
    {
        long _anchorWallMs, _anchorPosMs;

        /// <summary>Re-anchor: at wall time <paramref name="wallMs"/> the transport reported
        /// <paramref name="positionMs"/>. Call on every reported-position change AND on a play/pause edge — otherwise
        /// a resume extrapolates across the whole paused gap for one frame.</summary>
        public void Anchor(long wallMs, long positionMs)
        {
            _anchorWallMs = wallMs;
            _anchorPosMs = positionMs;
        }

        /// <summary>The position to draw at <paramref name="nowMs"/>.
        /// <para>A committed <paramref name="seekTargetMs"/> WINS outright: the user asked for that position and the
        /// medium must be there now, not after the acknowledgement lands. <paramref name="upperBoundMs"/> is the
        /// host's "I have not submitted past here" bound, which stops the extrapolation running ahead of real audio at
        /// the end of a track.</para>
        /// <para>An UNANCHORED interpolator extrapolates machine uptime — the trap the tests pin — so the shell
        /// anchors before its first tick, belt and braces.</para></summary>
        public readonly long Estimate(long nowMs, bool advancing, long? seekTargetMs, long? upperBoundMs, long durationMs)
        {
            if (seekTargetMs is { } t) return Clamp(t, durationMs);
            long est = advancing ? _anchorPosMs + (nowMs - _anchorWallMs) : _anchorPosMs;
            if (upperBoundMs is { } ub && est > ub) est = ub;
            return Clamp(est, durationMs);
        }

        /// <summary>The last anchored position, unextrapolated (what a paused deck rests at).</summary>
        public readonly long AnchorPositionMs => _anchorPosMs;

        static long Clamp(long v, long dur) => dur > 0 ? Math.Clamp(v, 0, dur) : Math.Max(0, v);
    }

    // ── 5. SpinIntegrator — the one reason a platter feels like a motor ─────────────────────────────────────────────

    /// <summary>A first-order angular-velocity lag with an integrated angle — the ONE reason a deck's platter, hub or
    /// disc feels like a motor rather than a CSS keyframe. Every rotating deck part goes through this.
    /// <para>Physics: <c>ω' = (target − ω)·(1 − e^(−dt/τ))</c>, then <c>angle += ω·dt</c>, wrapped into [0, 360).
    /// Separate rise/fall constants because a direct-drive turntable does NOT stop the way it starts — the SL-1200
    /// reaches 33⅓ in ~0.7 s (3τ at τ↑ = 0.23 s) and its electronic brake takes ~1.6 s (3τ at τ↓ = 0.53 s).</para>
    /// <para>Degrees per second is <c>rpm · 6</c> (33⅓ → 200 °/s, 45 → 270 °/s).</para></summary>
    public struct SpinIntegrator
    {
        /// <summary>Below this (°/s) a spin-down is called finished and snapped to a hard zero, so a settled deck's
        /// angle stops changing and the ticker can stop. Without the snap the exponential never reaches 0 and the
        /// deck writes a new (sub-pixel) angle forever.</summary>
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
        /// part that turns the other way (a cassette's right hub against its left) without needing a second
        /// target.</summary>
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

    // ── 6. the tonearm: phases, state, frame ────────────────────────────────────────────────────────────────────────

    /// <summary>Where the tonearm is in its choreography. This is the RECORD family's transport story told in the
    /// medium's own vocabulary — a turntable does not "pause", it lifts the cue lever and brakes the platter — and
    /// every one of these is reachable from the transition table in <see cref="TonearmMachine.Step"/>.</summary>
    public enum TonearmPhase : byte
    {
        /// <summary>Nothing on the platter: the record is in its sleeve and the arm sits on its rest.</summary>
        Idle,
        /// <summary>The record slides out of the sleeve onto the platter.</summary>
        SlideOut,
        /// <summary>The beat of stillness with the platter already turning, before the arm moves.</summary>
        Cue,
        /// <summary>Arm swings from its rest to the lead-in groove.</summary>
        SwingToLead,
        /// <summary>Silicone-damped cue-lever descent onto the groove.</summary>
        Lower,
        /// <summary>Riding the groove: the arm angle IS the position.</summary>
        Tracking,
        /// <summary>Cue lever up. <see cref="TonearmState.AfterLift"/> says what happens at the top.</summary>
        Lift,
        /// <summary>Lifted, platter braked.</summary>
        Paused,
        /// <summary>Platter coming back up to speed under a lifted arm.</summary>
        SpinUp,
        /// <summary>Lifted arm travelling to a seek target.</summary>
        SeekSwing,
        /// <summary>The headshell is under the pointer; the arm follows the drag.</summary>
        Dragging,
        /// <summary>Lifted arm travelling back to the lead-in for the next track on the SAME record.</summary>
        RecueSwing,
        /// <summary>Lifted arm travelling back to its rest. <see cref="TonearmState.AfterRest"/> says why.</summary>
        SwingToRest,
        /// <summary>The record slides back into its sleeve.</summary>
        SleeveIn,
        /// <summary>The sleeve art crossfades to the new album (<see cref="TonearmState.CoverGen"/> ticks here).</summary>
        CoverSwap,
        /// <summary>Riding the run-out groove after the queue ended.</summary>
        RunOut,
        /// <summary>Parked in the locked groove, waiting for the auto-return to trip.</summary>
        LockedGroove,
        /// <summary>Lifted and bobbing over the groove while the stream buffers.</summary>
        Hover,
        /// <summary>Auto-returned: record still on the platter, arm on its rest, platter braked.</summary>
        Stopped,
        /// <summary>Playback failed; the arm retreated and the platter stopped.</summary>
        Unavailable,
    }

    /// <summary>The tonearm's whole state as one value. Pure data — <see cref="TonearmMachine.Step"/> maps
    /// (state, input) → state and <see cref="TonearmMachine.Sample"/> maps (state, input) → a drawable frame, so the
    /// machine can be driven from a test at any cadence without a clock, a signal or a window.</summary>
    /// <param name="Phase">Which leg of the choreography.</param>
    /// <param name="SinceMs">Wall clock at which <paramref name="Phase"/> was entered.</param>
    /// <param name="PhaseMs">How long this leg lasts; 0 = it has no deadline (Tracking, Paused, Hover, …).</param>
    /// <param name="FromDeg">Arm angle the leg starts at.</param>
    /// <param name="ToDeg">Arm angle the leg ends at (and, while Dragging, where the headshell currently is).</param>
    /// <param name="LiftFrom">Lift the leg starts at — a lift interrupted halfway resumes from where it was.</param>
    /// <param name="PlatterOn">Is the platter driven?</param>
    /// <param name="RecordOut">Is the record on the platter (as opposed to in its sleeve)?</param>
    /// <param name="CoverGen">Bumped once per sleeve/label artwork swap; the face crossfades on the edge.</param>
    /// <param name="AfterLift">Which leg a <see cref="TonearmPhase.Lift"/> hands over to at the top.</param>
    /// <param name="AfterRest">Which leg a <see cref="TonearmPhase.SwingToRest"/> hands over to at the rest.</param>
    /// <param name="SwingMs">Duration stashed for the swing that FOLLOWS a lift (recue / change / auto-return /
    /// error).</param>
    /// <param name="ResumeDown">Does the arm come back down after a seek swing, or stay up (seek while paused)?</param>
    /// <param name="ThumpAtMs">Wall clock of the stylus drop; the face fires its dust puff on that one frame.</param>
    /// <param name="PlatterOffAtMs">Deferred brake: 0 = none, else the wall clock at which the platter stops.</param>
    public readonly record struct TonearmState(
        TonearmPhase Phase, long SinceMs, float PhaseMs, float FromDeg, float ToDeg, float LiftFrom,
        bool PlatterOn, bool RecordOut, int CoverGen,
        TonearmPhase AfterLift, TonearmPhase AfterRest, float SwingMs,
        bool ResumeDown, long ThumpAtMs, long PlatterOffAtMs)
    {
        /// <summary>Cold start: empty platter, arm on its rest, nothing turning.</summary>
        public static TonearmState Initial => new(TonearmPhase.Idle, 0, 0, TonearmMachine.RestDeg, TonearmMachine.RestDeg,
            1f, false, false, 0, TonearmPhase.Idle, TonearmPhase.Idle, 0, false, 0, 0);
    }

    /// <summary>One drawable tonearm frame. <c>Lift</c> is 0 (in the groove) .. 1 (cue lever up), and above 1 for the
    /// buffering bob. <c>Slide</c> is 0 (in the sleeve) .. 1 (on the platter).</summary>
    public readonly record struct TonearmFrame(
        float ArmDeg, float Lift, float PlatterTargetDegPerSec, float Slide, bool Thump, PhaseName Name);

    /// <summary>The SL-1200 in a value type: 0.7 s spin-up, a silicone-damped cue-lever descent, a 1.8 s locked groove
    /// and an auto-return. Engine-free by construction — the face binds what <see cref="Sample"/> hands back and never
    /// asks the machine a question.
    ///
    /// <para><b>The priority order in <see cref="Step"/> is load-bearing.</b> Ten rules in a fixed order — record
    /// leaving, error, boundary, queue end, live drag, committed seek, buffering, pause/resume, play-from-rest,
    /// current-leg timer. Re-ordering any two changes behaviour in ways no screenshot shows: a pause arriving in the
    /// same tick as a track boundary, a seek during a buffer stall, an error during a record change.</para>
    ///
    /// <para><b>The deferred brake is applied FIRST</b>, before every rule — including the rules that return the state
    /// untouched. A record change brakes 700 ms into the return swing, an error brakes WITH the swing, the auto-return
    /// brakes 500 ms after the lift. "Stop the platter when the swing ends" is a different, worse deck.</para></summary>
    public static class TonearmMachine
    {
        /// <summary>Arm on its rest.</summary>
        public const float RestDeg = -34f;
        /// <summary>Arm over the lead-in groove (position 0).</summary>
        public const float LeadInDeg = -20f;
        /// <summary>Lead-in → run-out sweep.</summary>
        public const float SpanDeg = 17f;
        /// <summary>Arm over the run-out groove (position 1).</summary>
        public const float RunOutDeg = LeadInDeg + SpanDeg;

        public const float SlideMs = 600, CueHoldMs = 300, SwingLeadMs = 1200, LowerMs = 900, LiftMs = 450, SpinUpMs = 700,
            SeekLiftMs = 400, SeekSwingBaseMs = 300, SeekSwingPerSpanMs = 600, SeekLowerMs = 700, RecueSwingMs = 900,
            SkipLiftMs = 300, ChangeSwingMs = 1200, ChangePlatterOffMs = 700, SleeveInMs = 600, CoverSwapMs = 350,
            RunOutBaseMs = 400, RunOutPerSpanMs = 800, LockedGroove33Ms = 1800, LockedGroove45Ms = 1333, AutoLiftMs = 500,
            AutoSwingMs = 1400, AutoPlatterOffMs = 500, ErrorSwingMs = 1200, BobPeriodMs = 1800, BufferLiftMs = 400,
            ReducedMs = 150, ThumpMs = 120, BufferLeadInFrac = 0.02f;

        /// <summary>Above this the machine treats the record as a 45 (shorter locked groove).</summary>
        public const float Rpm45Threshold = 40f;
        /// <summary>A seek shorter than this many degrees is below one rendered quantum — the arm does not move.</summary>
        public const float SeekDeadZoneDeg = 0.15f;

        /// <summary>Where the arm sits for a normalized position.</summary>
        public static float AngleOf(float frac) => LeadInDeg + SpanDeg * Ease.Clamp01(frac);

        /// <summary>Reduced motion collapses every leg to a snap.</summary>
        static float Dur(float ms, in Input i) => i.ReducedMotion ? MathF.Min(ms, ReducedMs) : ms;

        /// <summary>A swing costs a fixed setup plus a per-span travel time, so a long seek reads as a long
        /// journey.</summary>
        static float SwingMsFor(float from, float to, float baseMs, float perSpanMs, in Input i)
            => Dur(baseMs + perSpanMs * MathF.Abs(to - from) / SpanDeg, i);

        /// <summary>Mounted mid-song (Cover → Player, or a rail reopened): the record was playing all along, so there
        /// is no cueing sequence to watch — the machine starts where the transport already is.</summary>
        public static TonearmState Seed(in Input i)
        {
            if (!i.HasTrack) return TonearmState.Initial;
            float a = AngleOf(i.Frac);
            if (i.Error) return TonearmState.Initial with { Phase = TonearmPhase.Unavailable, RecordOut = true, SinceMs = i.NowMs };
            if (i.Advancing || (i.PlayWhenReady && !i.Buffering))
                return TonearmState.Initial with { Phase = TonearmPhase.Tracking, SinceMs = i.NowMs, FromDeg = a, ToDeg = a, LiftFrom = 0f, PlatterOn = true, RecordOut = true };
            if (i.Buffering)
                return TonearmState.Initial with { Phase = TonearmPhase.Hover, SinceMs = i.NowMs, FromDeg = a, ToDeg = a, PlatterOn = true, RecordOut = true };
            // The queue already ran out before we mounted: the deck has auto-returned, it is not merely paused.
            if (i.QueueEnded) return TonearmState.Initial with { Phase = TonearmPhase.Stopped, SinceMs = i.NowMs, RecordOut = true };
            return TonearmState.Initial with { Phase = TonearmPhase.Paused, SinceMs = i.NowMs, FromDeg = a, ToDeg = a, RecordOut = true };
        }

        /// <summary>One fold. See the type doc for the priority order and the deferred brake.
        /// <para>Idempotent within a tick: calling this twice with the SAME input returns the same state both times,
        /// so a caller that folds a boundary edge eagerly and then ticks on the same clock value cannot double-count
        /// it.</para></summary>
        public static TonearmState Step(TonearmState s, in Input i)
        {
            long now = i.NowMs;

            // Deferred brake and the one-shot stylus drop are applied FIRST, so every rule below — including the ones
            // that return `s` untouched — carries them.
            if (s.PlatterOffAtMs != 0 && now >= s.PlatterOffAtMs) s = s with { PlatterOn = false, PlatterOffAtMs = 0 };
            if (s.ThumpAtMs != 0 && now > s.ThumpAtMs) s = s with { ThumpAtMs = 0 };

            var cur = Sample(in s, in i);
            float armNow = cur.ArmDeg, liftNow = MathF.Min(cur.Lift, 1f);

            // 1. the track went away → back into the sleeve from any pose
            if (!i.HasTrack)
            {
                if (s.Phase is TonearmPhase.Idle or TonearmPhase.SleeveIn) return Timers(s, in i);
                // Already retreating towards the sleeve: let it finish rather than restarting the lift.
                if ((s.Phase is TonearmPhase.Lift or TonearmPhase.SwingToRest) && s.AfterRest == TonearmPhase.SleeveIn) return Timers(s, in i);
                return s.RecordOut
                    ? LiftThen(s, in i, LiftMs, TonearmPhase.SwingToRest, TonearmPhase.SleeveIn, armNow, liftNow, null, ChangeSwingMs)
                    : Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false };
            }

            // 2. error → lift · rest · brake (once); the platter stops WITH the swing, not at the end of it
            if (i.Error && s.Phase is not (TonearmPhase.Unavailable or TonearmPhase.Lift or TonearmPhase.SwingToRest))
                return LiftThen(s, in i, LiftMs, TonearmPhase.SwingToRest, TonearmPhase.Unavailable, armNow, liftNow, 0f, ErrorSwingMs);
            if (s.Phase == TonearmPhase.Unavailable)
                // The record never left the platter, so recovery is a plain cue — no sleeve, no slide-out.
                return !i.Error && i.PlayWhenReady
                    ? Enter(s, TonearmPhase.Cue, now, Dur(CueHoldMs, i), RestDeg, RestDeg, 1f) with { PlatterOn = true }
                    : s;

            // 3. boundary edges (the caller sets these for exactly one fold)
            switch (i.Boundary)
            {
                case Boundary.NaturalSameAlbum or Boundary.SkipSameAlbum or Boundary.RepeatOne:
                    if (LiftStaged(in s, now, TonearmPhase.RecueSwing, TonearmPhase.Idle)) return s;
                    // Cold deck (in the sleeve, or braked after an auto-return): just cue it again.
                    if (!s.RecordOut || !s.PlatterOn) return CueFrom(s, in i);
                    // Same record, next track: lift, swing back to the lead-in, drop. The platter NEVER stops.
                    return LiftThen(s, in i, i.Boundary == Boundary.SkipSameAlbum ? SkipLiftMs : LiftMs,
                        TonearmPhase.RecueSwing, TonearmPhase.Idle, armNow, liftNow, null, RecueSwingMs);

                case Boundary.NaturalNewAlbum or Boundary.SkipNewAlbum:
                    if (LiftStaged(in s, now, TonearmPhase.SwingToRest, TonearmPhase.SleeveIn)) return s;
                    // Nothing on the platter: the only visible change is the artwork. CoverGen is the one thing in
                    // this machine a repeated fold could double-count, so it is the one thing guarded against it.
                    if (!s.RecordOut)
                        return s.Phase == TonearmPhase.CoverSwap && s.SinceMs == now
                            ? s
                            : Enter(s, TonearmPhase.CoverSwap, now, Dur(CoverSwapMs, i), RestDeg, RestDeg, 1f) with { CoverGen = s.CoverGen + 1 };
                    // A different record: lift, return, sleeve it, swap the cover, slide the new one out. The brake
                    // trips 700 ms into the return swing — the platter is still coasting while the arm travels.
                    return LiftThen(s, in i, i.Boundary == Boundary.SkipNewAlbum ? SkipLiftMs : LiftMs,
                        TonearmPhase.SwingToRest, TonearmPhase.SleeveIn, armNow, liftNow, ChangePlatterOffMs, ChangeSwingMs);
            }

            // 4. queue end → ride the run-out
            if (i.QueueEnded && s.Phase is TonearmPhase.Tracking or TonearmPhase.Lower)
                return Enter(s, TonearmPhase.RunOut, now, SwingMsFor(armNow, RunOutDeg, RunOutBaseMs, RunOutPerSpanMs, in i), armNow, RunOutDeg, 0f);

            // 5. headshell drag
            if (i.ScrubTargetMs is not null && s.RecordOut && s.Phase is not (TonearmPhase.Dragging or TonearmPhase.Lift))
                return LiftThen(s, in i, SkipLiftMs, TonearmPhase.Dragging, TonearmPhase.Idle, armNow, liftNow, null, 0);
            if (s.Phase == TonearmPhase.Dragging)
            {
                if (i.ScrubTargetMs is null)
                {
                    // Released: swing from where the headshell WAS (ToDeg tracks the drag below — `armNow` has already
                    // fallen back to the transport position now that the scrub target is gone) to the committed target.
                    float from = s.ToDeg, to = AngleOf(i.FracOf(i.SeekTargetMs ?? i.PositionMs));
                    return Enter(s, TonearmPhase.SeekSwing, now, SwingMsFor(from, to, SeekSwingBaseMs, SeekSwingPerSpanMs, in i), from, to, 1f)
                        with { ResumeDown = i.PlayWhenReady, PlatterOn = i.PlayWhenReady || s.PlatterOn };
                }
                return s.ToDeg == armNow ? s : s with { ToDeg = armNow };   // remember where the headshell is
            }

            // 6. committed seek
            if (i.SeekTargetMs is { } target && s.Phase is TonearmPhase.Tracking or TonearmPhase.Paused)
            {
                float to = AngleOf(i.FracOf(target));
                if (MathF.Abs(to - armNow) < SeekDeadZoneDeg) return s;     // below a rendered quantum: do not twitch
                var st = s with { ToDeg = to, ResumeDown = i.PlayWhenReady };
                return s.Phase == TonearmPhase.Paused
                    // Already up: swing straight across and stay up.
                    ? Enter(st, TonearmPhase.SeekSwing, now, SwingMsFor(armNow, to, SeekSwingBaseMs, SeekSwingPerSpanMs, in i), armNow, to, 1f)
                    : LiftThen(st, in i, SeekLiftMs, TonearmPhase.SeekSwing, TonearmPhase.Idle, armNow, liftNow, null, 0);
            }

            // 7. buffering → hover (the platter keeps turning; only the stylus leaves the groove)
            if (i.Buffering && s.Phase is TonearmPhase.Tracking or TonearmPhase.Lower or TonearmPhase.SpinUp)
            {
                float to = i.Frac < BufferLeadInFrac ? LeadInDeg : armNow;   // a lead-in stall waits over the lead-in
                return LiftThen(s with { ToDeg = to }, in i, BufferLiftMs, TonearmPhase.Hover, TonearmPhase.Idle, armNow, liftNow, null, 0)
                    with { PlatterOn = true };
            }
            if (s.Phase == TonearmPhase.Hover)
            {
                if (!i.PlayWhenReady) return Enter(s, TonearmPhase.Paused, now, 0, armNow, armNow, 1f) with { PlatterOn = false };
                // Buffered is not enough — the hover clears when audio is actually ADVANCING again.
                if (!i.Buffering && i.Advancing) { float a = AngleOf(i.Frac); return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), a, a, 1f); }
                return s;
            }

            // 8. pause / resume
            if (s.Phase == TonearmPhase.Tracking && !i.PlayWhenReady && !i.QueueEnded)
                return LiftThen(s, in i, LiftMs, TonearmPhase.Paused, TonearmPhase.Idle, armNow, liftNow, null, 0);
            if (s.Phase == TonearmPhase.Paused && i.PlayWhenReady && !i.Buffering)
                return Enter(s, TonearmPhase.SpinUp, now, Dur(SpinUpMs, i), armNow, armNow, 1f) with { PlatterOn = true };

            // 9. idle / stopped → play
            if ((s.Phase is TonearmPhase.Idle or TonearmPhase.Stopped) && i.PlayWhenReady) return CueFrom(s, in i);

            // 10. the current leg's timer
            return Timers(s, in i);
        }

        /// <summary>Is the reaction this rule is about to stage ALREADY staged, on this very tick? The clock folds a
        /// boundary edge eagerly and then ticks, and both calls can land on the same <c>NowMs</c>; matching on the
        /// lift's DESTINATION (rather than merely on "a lift") keeps a pause-lift from swallowing a boundary.</summary>
        static bool LiftStaged(in TonearmState s, long now, TonearmPhase afterLift, TonearmPhase afterRest)
            => s.Phase == TonearmPhase.Lift && s.SinceMs == now && s.AfterLift == afterLift && s.AfterRest == afterRest;

        static TonearmState Timers(TonearmState s, in Input i)
            => s.PhaseMs > 0 && i.NowMs - s.SinceMs >= s.PhaseMs ? Advance(s, in i) : s;

        /// <summary>The current leg's deadline passed: hand over to the next one. Public so tests can pin the chain.</summary>
        public static TonearmState Advance(TonearmState s, in Input i)
        {
            long now = i.NowMs;
            switch (s.Phase)
            {
                case TonearmPhase.SlideOut: return Enter(s, TonearmPhase.Cue, now, Dur(CueHoldMs, i), RestDeg, RestDeg, 1f) with { RecordOut = true, PlatterOn = true };
                case TonearmPhase.Cue: return Enter(s, TonearmPhase.SwingToLead, now, Dur(SwingLeadMs, i), RestDeg, LeadInDeg, 1f);
                case TonearmPhase.SwingToLead: return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), LeadInDeg, LeadInDeg, 1f);
                case TonearmPhase.RecueSwing: return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), LeadInDeg, LeadInDeg, 1f);
                case TonearmPhase.SpinUp: return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), s.ToDeg, s.ToDeg, 1f);
                case TonearmPhase.SeekSwing:
                    return s.ResumeDown
                        ? Enter(s, TonearmPhase.Lower, now, Dur(SeekLowerMs, i), s.ToDeg, s.ToDeg, 1f)
                        : Enter(s, TonearmPhase.Paused, now, 0, s.ToDeg, s.ToDeg, 1f);
                case TonearmPhase.Lower: return Enter(s, TonearmPhase.Tracking, now, 0, s.ToDeg, s.ToDeg, 0f) with { ThumpAtMs = now };

                case TonearmPhase.Lift:
                    return s.AfterLift switch
                    {
                        TonearmPhase.SeekSwing => Enter(s, TonearmPhase.SeekSwing, now, SwingMsFor(s.FromDeg, s.ToDeg, SeekSwingBaseMs, SeekSwingPerSpanMs, in i), s.FromDeg, s.ToDeg, 1f),
                        TonearmPhase.RecueSwing => Enter(s, TonearmPhase.RecueSwing, now, s.SwingMs, s.FromDeg, LeadInDeg, 1f),
                        TonearmPhase.SwingToRest => Enter(s, TonearmPhase.SwingToRest, now, s.SwingMs, s.FromDeg, RestDeg, 1f),
                        TonearmPhase.Hover => Enter(s, TonearmPhase.Hover, now, 0, s.ToDeg, s.ToDeg, 1f),
                        TonearmPhase.Dragging => Enter(s, TonearmPhase.Dragging, now, 0, s.FromDeg, s.FromDeg, 1f),
                        _ => Enter(s, TonearmPhase.Paused, now, 0, s.FromDeg, s.FromDeg, 1f) with { PlatterOn = false },
                    };

                case TonearmPhase.SwingToRest:
                    return s.AfterRest switch
                    {
                        TonearmPhase.SleeveIn => Enter(s, TonearmPhase.SleeveIn, now, Dur(SleeveInMs, i), RestDeg, RestDeg, 1f) with { PlatterOn = false },
                        TonearmPhase.Unavailable => Enter(s, TonearmPhase.Unavailable, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false },
                        TonearmPhase.Stopped => Enter(s, TonearmPhase.Stopped, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false },
                        _ => Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false, RecordOut = false },
                    };

                case TonearmPhase.SleeveIn:
                    return i.HasTrack
                        ? Enter(s, TonearmPhase.CoverSwap, now, Dur(CoverSwapMs, i), RestDeg, RestDeg, 1f) with { RecordOut = false, CoverGen = s.CoverGen + 1 }
                        : Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f) with { RecordOut = false, PlatterOn = false };

                case TonearmPhase.CoverSwap:
                    return i.PlayWhenReady && !i.Error
                        ? Enter(s, TonearmPhase.SlideOut, now, Dur(SlideMs, i), RestDeg, RestDeg, 1f)
                        : Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f);

                case TonearmPhase.RunOut:
                    return Enter(s, TonearmPhase.LockedGroove, now,
                        Dur(i.Rpm > Rpm45Threshold ? LockedGroove45Ms : LockedGroove33Ms, i), RunOutDeg, RunOutDeg, 0f);

                // Auto-return: lift off the locked groove, swing home, brake 500 ms into the swing, park.
                case TonearmPhase.LockedGroove:
                    return Enter(s, TonearmPhase.Lift, now, Dur(AutoLiftMs, i), RunOutDeg, RestDeg, 0f) with
                    {
                        AfterLift = TonearmPhase.SwingToRest,
                        AfterRest = TonearmPhase.Stopped,
                        SwingMs = Dur(AutoSwingMs, i),
                        PlatterOffAtMs = Math.Max(1L, now + (long)(Dur(AutoLiftMs, i) + Dur(AutoPlatterOffMs, i))),
                    };

                default: return s;
            }
        }

        static TonearmState Enter(TonearmState s, TonearmPhase p, long now, float ms, float from, float to, float liftFrom)
            => s with { Phase = p, SinceMs = now, PhaseMs = ms, FromDeg = from, ToDeg = to, LiftFrom = liftFrom };

        /// <summary>Lift (<paramref name="liftMs"/>, RESUMING from wherever the arm already is — a lift interrupted
        /// halfway does not snap to 0 first) and then hand over to <paramref name="afterLift"/>.
        /// <paramref name="platterOffAfterSwingMs"/> schedules the brake relative to the FOLLOWING swing's START —
        /// <c>0</c> is "with the swing", <c>null</c> is "never". A same-album recue passes <c>null</c>: if the platter
        /// brakes on a next-track inside an album, the deck is wrong.</summary>
        static TonearmState LiftThen(TonearmState s, in Input i, float liftMs, TonearmPhase afterLift, TonearmPhase afterRest,
                                     float armNow, float liftNow, float? platterOffAfterSwingMs, float swingMs)
        {
            float lift = Dur(liftMs, i);
            long off = platterOffAfterSwingMs is { } d ? Math.Max(1L, i.NowMs + (long)(lift + Dur(d, i))) : 0L;
            return Enter(s, TonearmPhase.Lift, i.NowMs, lift, armNow, afterLift == TonearmPhase.SwingToRest ? RestDeg : s.ToDeg, liftNow) with
            { AfterLift = afterLift, AfterRest = afterRest, SwingMs = Dur(swingMs, i), PlatterOffAtMs = off };
        }

        /// <summary>Start playing: a record already on the platter only needs a cue; one in its sleeve slides out
        /// first.</summary>
        static TonearmState CueFrom(TonearmState s, in Input i) => s.RecordOut
            ? Enter(s, TonearmPhase.Cue, i.NowMs, Dur(CueHoldMs, i), RestDeg, RestDeg, 1f) with { PlatterOn = true }
            : Enter(s, TonearmPhase.SlideOut, i.NowMs, Dur(SlideMs, i), RestDeg, RestDeg, 1f);

        /// <summary>The drawable frame for a state at a moment. Pure: no clock, no state mutation.</summary>
        public static TonearmFrame Sample(in TonearmState s, in Input i)
        {
            long elapsed = i.NowMs - s.SinceMs; if (elapsed < 0) elapsed = 0;
            float t = s.PhaseMs > 0 ? Ease.Clamp01(elapsed / s.PhaseMs) : 1f;
            float arm, lift, slide = s.RecordOut ? 1f : 0f;
            switch (s.Phase)
            {
                case TonearmPhase.Tracking: arm = AngleOf(i.Frac); lift = 0f; break;
                case TonearmPhase.Dragging: arm = AngleOf(i.FracOf(i.ScrubTargetMs ?? i.PositionMs)); lift = 1f; break;
                case TonearmPhase.RunOut: arm = s.FromDeg + (s.ToDeg - s.FromDeg) * t; lift = 0f; break;   // linear: a groove, not a swing
                case TonearmPhase.LockedGroove: arm = RunOutDeg; lift = 0f; break;
                case TonearmPhase.SwingToLead or TonearmPhase.SeekSwing or TonearmPhase.RecueSwing or TonearmPhase.SwingToRest:
                    arm = s.FromDeg + (s.ToDeg - s.FromDeg) * Ease.Std(t); lift = 1f; break;
                case TonearmPhase.Lift: arm = s.FromDeg; lift = s.LiftFrom + (1f - s.LiftFrom) * Ease.LiftUp(t); break;
                case TonearmPhase.Lower: arm = s.ToDeg; lift = 1f - Ease.Damped(t); break;
                case TonearmPhase.Hover:
                    arm = s.ToDeg;
                    lift = i.ReducedMotion ? 1f : 1f + 0.4f * (0.5f - 0.5f * MathF.Cos(2f * MathF.PI * (elapsed % (long)BobPeriodMs) / BobPeriodMs));
                    break;
                case TonearmPhase.SlideOut: arm = RestDeg; lift = 1f; slide = t; break;
                case TonearmPhase.SleeveIn: arm = RestDeg; lift = 1f; slide = 1f - t; break;
                default: arm = s.Phase is TonearmPhase.Paused or TonearmPhase.SpinUp ? s.ToDeg : RestDeg; lift = 1f; break;
            }
            float platter = s.PlatterOn && !i.ReducedMotion ? i.Rpm * 6f : 0f;
            bool thump = s.ThumpAtMs != 0 && i.NowMs - s.ThumpAtMs < ThumpMs && !i.ReducedMotion;
            return new TonearmFrame(arm, lift, platter, slide, thump, NameOf(in s));
        }

        /// <summary>The phase's name in the shared deck vocabulary, ignoring what it is on its way to.</summary>
        public static PhaseName NameOf(TonearmPhase p) => p switch
        {
            TonearmPhase.SlideOut or TonearmPhase.Cue or TonearmPhase.SwingToLead => PhaseName.Cueing,
            TonearmPhase.Lower => PhaseName.NeedleDown,
            TonearmPhase.Tracking => PhaseName.Playing,
            TonearmPhase.Lift => PhaseName.Pausing,
            TonearmPhase.Paused => PhaseName.Paused,
            TonearmPhase.SpinUp => PhaseName.SpinningUp,
            TonearmPhase.SeekSwing or TonearmPhase.Dragging => PhaseName.Seeking,
            TonearmPhase.RecueSwing => PhaseName.NextTrack,
            TonearmPhase.SwingToRest or TonearmPhase.SleeveIn or TonearmPhase.CoverSwap => PhaseName.ChangingRecord,
            TonearmPhase.RunOut => PhaseName.RunOut,
            TonearmPhase.LockedGroove => PhaseName.LockedGroove,
            TonearmPhase.Hover => PhaseName.Buffering,
            TonearmPhase.Stopped => PhaseName.Stopped,
            TonearmPhase.Unavailable => PhaseName.Unavailable,
            _ => PhaseName.Idle,
        };

        /// <summary>The phase's name told with its INTENT: a lift is only "pausing" when it is not the first beat of a
        /// recue, a seek, a record change, an error retreat or the auto-return. Both retreats share
        /// <see cref="TonearmPhase.Lift"/> and <see cref="TonearmPhase.SwingToRest"/>, so the DESTINATION is what
        /// distinguishes them.</summary>
        public static PhaseName NameOf(in TonearmState s) => s.Phase switch
        {
            TonearmPhase.Lift => s.AfterRest switch
            {
                TonearmPhase.Stopped => PhaseName.AutoReturn,
                TonearmPhase.SleeveIn => PhaseName.ChangingRecord,
                TonearmPhase.Unavailable => PhaseName.Error,
                _ => s.AfterLift switch
                {
                    TonearmPhase.RecueSwing => PhaseName.NextTrack,
                    TonearmPhase.SeekSwing or TonearmPhase.Dragging => PhaseName.Seeking,
                    TonearmPhase.Hover => PhaseName.Buffering,
                    _ => PhaseName.Pausing,
                },
            },
            TonearmPhase.SwingToRest => s.AfterRest switch
            {
                TonearmPhase.Stopped => PhaseName.AutoReturn,
                TonearmPhase.Unavailable => PhaseName.Error,
                _ => PhaseName.ChangingRecord,
            },
            _ => NameOf(s.Phase),
        };
    }

    /// <summary>The headshell drag's arithmetic: deck-space point → normalized position, and arm-local point → deck
    /// space. Pure geometry, shared by the record family's faces and unit-tested without a pointer.</summary>
    public static class TonearmGeometry
    {
        /// <summary>Inner edge of the label as a fraction of the record's radius — the groove band starts here.</summary>
        public const float LabelRadiusFrac = 0.34f;
        /// <summary>Width of the groove band as a fraction of the radius (label edge → rim).</summary>
        public const float GrooveBandFrac = 1f - LabelRadiusFrac;

        /// <summary>Where a deck-space point falls in the track: the RIM is position 0 (the lead-in) and the LABEL
        /// edge is position 1 (the run-out), because that is the direction a stylus actually travels.</summary>
        public static float FracFromDeckPoint(float x, float y, float platterCx, float platterCy, float recordD)
        {
            float dx = x - platterCx, dy = y - platterCy;
            float d = MathF.Sqrt(dx * dx + dy * dy) / (recordD * 0.5f);
            return Ease.Clamp01(1f - (d - LabelRadiusFrac) / GrooveBandFrac);
        }

        /// <summary>Map a point inside the arm's own box to deck space, honouring the arm's rotation about its pivot
        /// (the face's transform origin at (.5, .08)).</summary>
        public static (float X, float Y) ArmLocalToDeck(float lx, float ly, float armDeg, float armX, float armY, float armW, float armH)
        {
            float px = armW * 0.5f, py = armH * 0.08f;
            float r = armDeg * (MathF.PI / 180f), c = MathF.Cos(r), s = MathF.Sin(r);
            float dx = lx - px, dy = ly - py;
            return (armX + px + c * dx - s * dy, armY + py + s * dx + c * dy);
        }
    }

    // ── 7. RecordModel — the record family's physics ────────────────────────────────────────────────────────────────

    /// <summary>The record family's model: Record, Turntable, Zune and Picture disc all spin the same platter and run
    /// the same <see cref="TonearmMachine"/>. Only the FACE differs — the Zune draws no arm at all, and runs the
    /// machine anyway so that its platter brakes, spins up and rides the run-out exactly like its siblings.
    ///
    /// <para>Engine-free and allocation-free: one <see cref="TonearmState"/> value, one <see cref="SpinIntegrator"/>,
    /// and a <see cref="Frame"/> struct out per tick.</para></summary>
    public sealed class RecordModel : IModel
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
        PhaseName _phase;
        float _rpm;
        long _rampUntilMs;

        /// <summary>Seeded from the CURRENT transport (<see cref="TonearmMachine.Seed"/>): mounting the deck mid-song
        /// must not replay the cueing sequence.</summary>
        public RecordModel(in Input seed, RecordVariant variant)
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
        public PhaseName Phase => _phase;

        public Frame Tick(in Input input, float dtSec)
        {
            ApplySpeedRamp(in input);
            _state = TonearmMachine.Step(_state, in input);
            var f = TonearmMachine.Sample(in _state, in input);
            _platter.Step(f.PlatterTargetDegPerSec, dtSec);
            _phase = f.Name;
            return new Frame(input.Frac, _platter.Angle, f.ArmDeg, f.Lift, f.Slide, 0f, 0f, _state.CoverGen, f.Thump, f.Name);
        }

        /// <summary>Nothing is moving: the ticker may stop until the transport says otherwise.</summary>
        public bool IsSettled
            => (_state.Phase is TonearmPhase.Idle or TonearmPhase.Paused or TonearmPhase.Stopped or TonearmPhase.Unavailable)
               && _platter.AtRest;

        /// <summary>The record family has no analyser.</summary>
        public ReadOnlySpan<float> Bands => ReadOnlySpan<float>.Empty;

        /// <inheritdoc cref="Bands"/>
        public ReadOnlySpan<float> Peaks => ReadOnlySpan<float>.Empty;

        void ApplySpeedRamp(in Input input)
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

    // ── 8. TapeModel — cassette and reel-to-reel ────────────────────────────────────────────────────────────────────

    /// <summary>Which tape transport a <see cref="TapeModel"/> drives — the cassette's two small hubs or the
    /// reel-to-reel's two big open flanges. Same physics (constant LINEAR tape speed → angular speed falls as the
    /// take-up pack grows), different radii/rates per the geometry table.</summary>
    public enum TapeKind : byte { Cassette, Reel }

    /// <summary>Engine-free physics for the Cassette and Reel-to-reel decks. One instance per mounted deck;
    /// <see cref="Tick"/> is allocation-free and pure aside from its own mutable state.</summary>
    public sealed class TapeModel : IModel
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

        public Frame Tick(in Input input, float dtSec)
        {
            float p = input.Frac;
            var (rL, rR) = PackRadii(_kind, p);
            float rMax = _kind == TapeKind.Cassette ? 26f : 92f;

            bool playing = input.Advancing || (input.PlayWhenReady && !input.Buffering);

            // A committed seek (SeekTargetMs going null → non-null) or a big remote jump between ticks both mean "the
            // tape needs to physically catch up" — 900 ms of fast-wind before settling back to real-time speed.
            bool seekCommitted = _initialized && input.SeekTargetMs is not null && _prevSeekTarget is null;
            bool bigJump = _initialized && Math.Abs(input.PositionMs - _prevPosMs) > BigJumpMs;
            if (seekCommitted || bigJump) _windUntilMs = input.NowMs + (long)WindWindowMs;
            float wind = input.NowMs < _windUntilMs ? (_kind == TapeKind.Cassette ? 4f : 5f) : 1f;

            _left.Step(Omega(_kind, rL, wind, playing), dtSec);
            _right.Step(Omega(_kind, rR, wind, playing), dtSec);

            float slide = StepEject(in input, dtSec);

            _prevSeekTarget = input.SeekTargetMs;
            _prevPosMs = input.PositionMs;
            _initialized = true;

            var phase = wind > 1f ? PhaseName.Winding : playing ? PhaseName.Playing : PhaseName.Paused;
            _settled = _left.AtRest && _right.AtRest && _eject == EjectPhase.None;

            return new Frame(p, _left.Angle, _right.Angle, 0f, slide, rL / rMax, rR / rMax, _coverGen, false, phase);
        }

        float StepEject(in Input input, float dtSec)
        {
            float slide = 1f;
            if (input.Boundary is Boundary.NaturalNewAlbum or Boundary.SkipNewAlbum && _eject == EjectPhase.None)
            {
                _eject = EjectPhase.Out;
                _ejectElapsedMs = 0f;
            }
            if (_eject == EjectPhase.None) return slide;

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
            return slide;
        }

        /// <summary>Pure geometry: the left (supply) and right (take-up) pack radii, in % of the shell, for a given
        /// play fraction.</summary>
        public static (float rL, float rR) PackRadii(TapeKind kind, float p) => kind == TapeKind.Cassette
            ? (26f - 13f * p, 13f + 13f * p)
            : (92f - 48f * p, 44f + 48f * p);

        /// <summary>Pure physics: constant linear tape speed (scaled by <paramref name="wind"/>) turned into an
        /// angular speed at radius <paramref name="r"/>. Negative because both hubs spin the same visual direction
        /// while the geometry angle convention is CCW-positive.</summary>
        public static float Omega(TapeKind kind, float r, float wind, bool playing)
        {
            if (!playing) return 0f;
            float baseDegPerSec = wind * (kind == TapeKind.Cassette ? 900f : 6000f);
            return -baseDegPerSec / r;
        }

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }

    // ── 9. DiscModel — CD and MiniDisc ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Engine-free physics for the CD/MiniDisc deck: a constant-linear-velocity spindle (angular speed falls
    /// as the virtual read head moves outward) plus the sled travel and the tray-eject sequence on a new album.</summary>
    public sealed class DiscModel : IModel
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

        public Frame Tick(in Input input, float dtSec)
        {
            float p = input.Frac;
            bool playing = input.Advancing || (input.PlayWhenReady && !input.Buffering);

            _disc.Step(ClvTargetDegPerSec(p, playing), dtSec);

            float slide = 1f;
            if (input.Boundary is Boundary.NaturalNewAlbum or Boundary.SkipNewAlbum && _eject == EjectPhase.None)
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

            float aux0 = (20f + 26f * p) / 100f;   // sled travel, fraction of the disc diameter
            var phase = input.Buffering ? PhaseName.Buffering : playing ? PhaseName.Playing : PhaseName.Paused;
            _settled = _disc.AtRest && _eject == EjectPhase.None;

            return new Frame(p, _disc.Angle, 0f, 0f, slide, aux0, 0f, _coverGen, false, phase);
        }

        /// <summary>Pure physics: CLV spindle target — fastest at the inner edge (p=0), slowest at the outer rim
        /// (p=1).</summary>
        public static float ClvTargetDegPerSec(float p, bool playing) => playing ? 3000f - 1800f * p : 0f;

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }

    // ── 10. MeterModel — the hi-fi VU needles ──────────────────────────────────────────────────────────────────────

    /// <summary>Engine-free physics for the Hi-fi VU deck: dB-to-needle-angle mapping plus a first-order ballistic lag
    /// (slow for VU, fast for PPM). When the level tap has NO data but the deck is playing, a small constant plus a
    /// slow breathing sine keeps the needles alive instead of pinned at the floor — absence is a real answer, not an
    /// error.</summary>
    public sealed class MeterModel : IModel
    {
        const float RestDeg = -45f;
        const float BreatheOmega = 0.6f;      // rad/s — the "slow sin" for the no-tap-but-playing case
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

        public Frame Tick(in Input input, float dtSec)
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

            var phase = _playing ? PhaseName.Playing : PhaseName.Paused;
            return new Frame(input.Frac, _angleL, _angleR, 0f, 1f, 0f, 0f, 0, false, phase);
        }

        public static float DbToDeg(float db) => Math.Clamp(-45f + (db + 20f) / 23f * 90f, -48f, 48f);
        public static float RmsToDb(float rms) => 20f * MathF.Log10(MathF.Max(rms, 1e-4f)) + 18f;
    }

    // ── 11. LevelModel — the Winamp / WMP analyser ──────────────────────────────────────────────────────────────────

    /// <summary>Engine-free "analyser" for the Winamp/WMP-style decks: band levels synthesized from the RMS/peak tap
    /// (or a flat fallback when there is no tap) rather than a real FFT, plus a peak-hold/fall overlay. Scope mode
    /// instead fills <see cref="Bands"/> with a fake waveform sample. Arrays are preallocated once in the constructor
    /// so <see cref="Tick"/> never allocates.</summary>
    public sealed class LevelModel : IModel
    {
        const float PeakHoldMs = 400f;
        const float PeakFallPerSec = 1.2f;
        const float AttackCoeff = 0.5f;
        const float ReleaseCoeff = 0.12f;
        const float FloorLevel = 0.02f;
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
            _seed = seed ?? (float)(Random.Shared.NextDouble() * 1000.0);
            _bands = new float[bands];
            _peaks = new float[bands];
            _peakHoldMs = new float[bands];
            for (int i = 0; i < bands; i++) _bands[i] = FloorLevel;
        }

        public ReadOnlySpan<float> Bands => _bands;
        public ReadOnlySpan<float> Peaks => _peaks;
        public bool IsSettled => _settled;

        public Frame Tick(in Input input, float dtSec)
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
                        ? MathF.Max(FloorLevel, env * (0.35f + 0.45f * MathF.Sin(_t * (3f + 0.37f * i) + _seed + i) * MathF.Sin(1.3f * _t + 0.8f * i)) + rmsN * Hash(i, _t) * 0.15f)
                        : FloorLevel;
                    float coeff = target > _bands[i] ? AttackCoeff : ReleaseCoeff;
                    _bands[i] += (target - _bands[i]) * coeff;
                    // The exponential release never reaches the floor exactly; snap the last half-percent so a silent
                    // deck really settles (and the ticker can stop) instead of asymptoting forever.
                    if (!playing && _bands[i] < FloorLevel + SettleEps) _bands[i] = FloorLevel;
                    UpdatePeak(i, _bands[i], dtSec);
                }
            }

            // SCOPE mode settles on the same rule as spectrum mode: the ticker must be able to stop when a scope deck
            // is paused (ch 23 §8 — the case 0.2.9's own test file did not cover, and the defect it shipped).
            bool settled = !playing;
            if (settled)
            {
                for (int i = 0; i < _bandCount; i++)
                {
                    if (_scope ? MathF.Abs(_bands[i] - 0.5f) > SettleEps : _bands[i] > FloorLevel) { settled = false; break; }
                }
            }
            _settled = settled;

            var phase = playing ? PhaseName.Playing : PhaseName.Paused;
            return new Frame(input.Frac, 0f, 0f, 0f, 1f, 0f, 0f, 0, false, phase);
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

        /// <summary>A cheap deterministic pseudo-random value in 0..1 from a band index and a coarse (1/30 s) time
        /// bucket.</summary>
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

    // ── 12. ProgressModel — the two faces whose only moving part is progress ────────────────────────────────────────

    /// <summary>The deck model for faces whose ONLY moving part is progress — the iPod Classic (a click-wheel screen:
    /// bar, thumb, clocks) and Canvas drift (whose motion is slab-driven keyframes on the cover, not a per-tick fold).
    /// <para>It exists so those two faces are not special-cased in the clock: they get the same ticker, the same
    /// value-gated writes and the same phase name as every other deck, for the cost of one struct copy per tick.
    /// <see cref="IsSettled"/> is always true, so the ticker runs while (and only while) audio is playing.</para></summary>
    public sealed class ProgressModel : IModel
    {
        int _coverGen;

        public Frame Tick(in Input input, float dtSec)
        {
            // The cover swap is the one edge these faces DO care about: Canvas keys its whole drift frame on CoverGen
            // and cross-fades, and the iPod's LCD art changes with it. Only a NEW ALBUM counts — a same-album advance
            // keeps the artwork, so bumping there would flash the cover on every track of a record.
            if (input.Boundary is Boundary.NaturalNewAlbum or Boundary.SkipNewAlbum) _coverGen++;
            return new Frame(input.Frac, 0f, 0f, 0f, 1f, 0f, 0f, _coverGen, false, PhaseOf(in input));
        }

        public ReadOnlySpan<float> Bands => default;
        public ReadOnlySpan<float> Peaks => default;

        /// <summary>Always settled: nothing here has inertia, so the ticker's own play/pause gate is the whole
        /// story.</summary>
        public bool IsSettled => true;

        static PhaseName PhaseOf(in Input i)
        {
            if (i.Error) return PhaseName.Error;
            if (!i.HasTrack) return PhaseName.Idle;
            if (i.Buffering) return PhaseName.Buffering;
            if (i.QueueEnded) return PhaseName.Stopped;
            if (i.ScrubTargetMs is not null || i.SeekTargetMs is not null) return PhaseName.Seeking;
            return i.Advancing || i.PlayWhenReady ? PhaseName.Playing : PhaseName.Paused;
        }
    }

    // ── 13. DriftPath — the Canvas deck's Ken Burns table ───────────────────────────────────────────────────────────

    /// <summary>Pure keyframe data for the Canvas-drift deck's slow pan/zoom. No model and no tick — the face drives
    /// an engine keyframe loop directly off this table; it exists purely so the timing/shape is pinned in one
    /// engine-free place instead of copied into face code.</summary>
    public static class DriftPath
    {
        public const float SlowSeconds = 40f, FastSeconds = 16f;

        /// <summary>(offset 0..1 through the loop, tx, ty, scale) — tx/ty are fractions of the frame size, MIRRORED so
        /// the loop (0 → 1) has no seam when played back-and-forth.</summary>
        public static readonly (float Offset, float Tx, float Ty, float Scale)[] Keys =
        [
            (0f, 0f, 0f, 1f),
            (0.5f, -0.05f, 0.03f, 1.06f),
            (1f, 0.04f, -0.04f, 1.02f),
        ];
    }

    // ── 14. the pure Fold (ch 23 §8) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The transport facts ONE fold needs, as plain values the shell reads off the playback signals. Separate
    /// from <see cref="Input"/> because <see cref="Input"/> carries DERIVED terms (the interpolated position, the
    /// phase, the boundary edge) that <see cref="Fold"/> computes — so a test can drive the derivation rather than
    /// restate it.</summary>
    /// <param name="ReportedPositionMs">The transport's own ~1 Hz number — never what a deck draws.</param>
    /// <param name="SyntheticSeekMs">A remote jump the shell synthesized (a Connect transfer moved the playhead with
    /// no local seek). Folded in beside a real committed seek.</param>
    public readonly record struct TransportFacts(
        bool HasTrack, bool IsPlaying, bool Buffering, bool Error,
        long DurationMs, long ReportedPositionMs,
        long? SeekTargetMs, long? ScrubTargetMs, long? SyntheticSeekMs,
        bool RepeatOne, float Rpm, bool ReducedMotion);

    /// <summary>The playhead this close to the end while NOT playing is the track having run out — the queue end the
    /// tonearm rides into its locked groove on. The same window <see cref="BoundaryRules.NaturalEndWindowMs"/> uses.</summary>
    public const long EndedWindowMs = 1_500;

    /// <summary>Derive the deck's transport phase from what the host actually publishes. Pure: the caller hands in
    /// values it already read, so this is usable from a render and from a ticker alike.</summary>
    public static TransportPhase PhaseOf(bool playing, bool buffering, bool error, long positionMs, long durationMs)
    {
        if (error) return TransportPhase.Failed;
        if (buffering) return TransportPhase.Buffering;
        if (playing) return TransportPhase.Playing;
        if (durationMs > 0 && positionMs >= durationMs - EndedWindowMs) return TransportPhase.Ended;
        return TransportPhase.Paused;
    }

    /// <summary>THE fold: transport facts → <see cref="Input"/>. Shared with <see cref="Seed"/> so a deck mounted
    /// mid-song sees byte-identical inputs to one that has been ticking — which is what lets
    /// <see cref="TonearmMachine.Seed"/> answer "the record was already playing" instead of replaying a cue sequence.
    ///
    /// <para>Pure: it reads no clock and no signal. The shell stamps <paramref name="nowMs"/> off the FRAME clock and
    /// hands the facts in; `Deck.UI.cs`'s three-line reader is the only thing that peeks a signal (and it must PEEK —
    /// this runs from a timer, never from a render, so it must subscribe nothing).</para></summary>
    public static Input Fold(in TransportFacts f, ref PositionInterpolator pos, long nowMs, Boundary boundary)
    {
        bool advancing = f.IsPlaying && !f.Buffering;
        long? seek = f.SeekTargetMs ?? f.SyntheticSeekMs;
        long position = pos.Estimate(nowMs, advancing, seek, null, f.DurationMs);
        var phase = PhaseOf(f.IsPlaying, f.Buffering, f.Error, position, f.DurationMs);
        return new Input(
            nowMs,
            f.HasTrack,
            phase,
            f.IsPlaying,
            advancing,
            f.Buffering,
            f.Error,
            phase == TransportPhase.Ended,
            boundary,
            seek,
            f.ScrubTargetMs,
            position,
            f.DurationMs,
            f.RepeatOne,
            f.Rpm,
            f.ReducedMotion);
    }

    /// <summary>The input a deck model is CONSTRUCTED from: the transport exactly as it is right now, with a fresh
    /// interpolator anchored to the reported position.</summary>
    public static Input Seed(in TransportFacts f, long nowMs)
    {
        var interp = default(PositionInterpolator);
        interp.Anchor(nowMs, f.ReportedPositionMs);
        return Fold(in f, ref interp, nowMs, Boundary.None);
    }

    // ── 15. the catalog: preset id → physics ────────────────────────────────────────────────────────────────────────

    /// <summary>Preset id → the deck's PHYSICS. The ONE place a preset becomes a model, so the host holds no per-deck
    /// knowledge at all and the twelve decks can be built (and unit-tested) independently. The mirror of
    /// `Deck.UI.cs`'s face dispatch: physics on one side, geometry on the other, and nothing in between knows both.
    ///
    /// <para><b>Why the seed.</b> The record family is the only one with a mount-time DECISION to make: a deck mounted
    /// mid-song must show a record that has been playing all along, not replay a three-second cue sequence. It
    /// therefore takes the transport as it stands; every other model starts from its own rest pose and converges
    /// within a tick or two, so they take only their options.</para></summary>
    public static class Models
    {
        /// <summary>Winamp's spectrum window is 19 bars; its oscilloscope option fills the SAME band array with a
        /// waveform sample instead, so the face binds one set of signals either way.</summary>
        public const int WinampBands = 19;
        /// <summary>WMP's bar visualizer.</summary>
        public const int WmpBands = 24;

        /// <summary>Build the model for a preset.</summary>
        /// <param name="preset">The catalog row (`Rail.PlayerCatalog`).</param>
        /// <remarks>The preset's options are read through `Rail.PlayerPrefs` (clamped on read, under the one epoch).</remarks>
        /// <param name="levels">The audio level tap as a PULL, not a subscription: levels are published on the audio
        /// control thread, so a deck must peek them from its own ticker — a reactive edge there would schedule UI work
        /// from that thread. Null (Connect playback, a remote device, `--fake`) is a real answer the synths handle by
        /// falling back to a level-free envelope.</param>
        /// <param name="seed">The transport as it stands, for the record family's mount-time decision.</param>
        public static IModel Create(in Rail.PlayerCatalog.Preset preset,
                                    Func<(float rms, float peak)?> levels, in Input seed)
            => preset.Id switch
            {
                Rail.PlayerCatalog.Record => new RecordModel(in seed, RecordVariant.Record),
                Rail.PlayerCatalog.Turntable => new RecordModel(in seed, RecordVariant.Turntable),
                Rail.PlayerCatalog.Zune => new RecordModel(in seed, RecordVariant.Zune),
                Rail.PlayerCatalog.Picture => new RecordModel(in seed, RecordVariant.Picture),
                Rail.PlayerCatalog.Cassette => new TapeModel(TapeKind.Cassette),
                Rail.PlayerCatalog.Reel => new TapeModel(TapeKind.Reel),
                Rail.PlayerCatalog.Cd => new DiscModel(),
                Rail.PlayerCatalog.Vu => new MeterModel(Rail.PlayerPrefs.ChoiceSlug(in preset, "ballistics") == "ppm", levels),
                Rail.PlayerCatalog.Winamp => new LevelModel(WinampBands, Rail.PlayerPrefs.ChoiceSlug(in preset, "vis") == "scope", levels),
                Rail.PlayerCatalog.Wmp => new LevelModel(WmpBands, false, levels),
                // iPod and Canvas: progress is the only thing that moves per tick (Canvas's drift is slab keyframes).
                _ => new ProgressModel(),
            };

        /// <summary>Which record-family variant a preset drives, for the face dispatch. Non-record presets answer
        /// <see cref="RecordVariant.Record"/>; the caller only asks when it already knows the family.</summary>
        public static RecordVariant VariantOf(int presetId) => presetId switch
        {
            Rail.PlayerCatalog.Turntable => RecordVariant.Turntable,
            Rail.PlayerCatalog.Zune => RecordVariant.Zune,
            Rail.PlayerCatalog.Picture => RecordVariant.Picture,
            _ => RecordVariant.Record,
        };

        /// <summary>Is this preset one of the four the record family draws? The tonearm machine runs for all four,
        /// including the Zune, which draws no arm — that is what makes its platter brake, spin up and ride the run-out
        /// exactly like its siblings.</summary>
        public static bool IsRecordFamily(int presetId) => presetId
            is Rail.PlayerCatalog.Record or Rail.PlayerCatalog.Turntable
            or Rail.PlayerCatalog.Zune or Rail.PlayerCatalog.Picture;

        /// <summary>The ONE option of a preset that is read at MODEL construction rather than by the face — VU ›
        /// needles (<c>ballistics</c>: the τ) and Winamp › analyser (<c>vis</c>: spectrum vs scope synth); null for
        /// every other preset. ch 23 §6.4(4): 0.2.9 built the model once, so flipping either restyled the face and
        /// kept the old physics until a remount. The host compares this option's current slug with the one its model
        /// was built from and rebuilds the model IN PLACE when they differ — no remount, no entrance fade.</summary>
        public static string? ModelOptionSlug(int presetId) => presetId switch
        {
            Rail.PlayerCatalog.Vu => "ballistics",
            Rail.PlayerCatalog.Winamp => "vis",
            _ => null,
        };
    }

    // ── 16. the shell's decisions, pure (stage 2: the clock's gates, the grips, the lettering latch) ────────────────
    //
    // Everything `Deck.UI.cs` DECIDES rather than draws. 0.2.9 kept these inline in `DeckClock`, `DeckGesture`,
    // `IpodWheel` and the cover leaves, so the three interaction defects ch 23 §6.4 lists (#1 the headshell has no
    // cancel, #4 the model options never reach a mounted model, #6 the headshell seeks to 0 over an unknown duration)
    // were untestable. The shell now calls these and nothing else.

    /// <summary>The deck clock's gates and quanta. <see cref="ShouldTick"/> is the idle-GPU rule for this surface: a
    /// paused, settled, ended, reduced-motion or rail-closed deck runs NO timer, so it requests no frame.</summary>
    public static class ClockRules
    {
        /// <summary>The self-paced tick — mirrors <c>Design.Cadence.PluggedLoopHz</c>.</summary>
        public const float TickMs = 1000f / Design.Cadence.PluggedLoopHz;

        /// <summary>A reported position further than this from the interpolation's expectation, with no seek of our
        /// own pending, IS a seek somebody else made (a Connect device, a media key, a lock-screen scrub).</summary>
        public const long RemoteJumpMs = 2_500;

        /// <summary>How close a report must land to a seek target before the deck stops forcing the target.</summary>
        public const long SeekSettleMs = 500;

        /// <summary>The longest a committed seek may pin the playhead before the deck stops trusting it.</summary>
        public const long SeekLatchMs = 2_000;

        /// <summary>The dt clamp: a resume-from-sleep tick must not integrate ten minutes into a platter.</summary>
        public const float DtMinSec = 0.001f, DtMaxSec = 0.040f;

        public const float Rpm33 = 33.333f, Rpm45 = 45f;

        /// <summary>The record family's disc diameter as a fraction of the deck — the widest rotating part, so the
        /// angle quantum it yields is the finest any face needs.</summary>
        public const float DiscFrac = 0.70f;

        /// <summary>The square edge's grid. The side is part of the host's remount key, so a per-DIP side would remount
        /// four times as often during a splitter drag (ch 21 §9.1(6)).</summary>
        public const float SideQuantum = 4f;

        /// <summary>The run gate (ch 23 §0(6)). <paramref name="playWhenReady"/> covers a load that is still resolving;
        /// <paramref name="settled"/> is the model's own <see cref="IModel.IsSettled"/>. When this is false the ticker
        /// is OFF and a transport CHANGE is the clock.</summary>
        public static bool ShouldTick(bool reducedMotion, bool railOpen, bool playing, bool playWhenReady, bool buffering, bool settled)
            => !reducedMotion && railOpen && (playing || playWhenReady || buffering || !settled);

        /// <summary>May a face's LOOPING motion run (the Winamp marquee, the Canvas drift)? Only while audio is meant to
        /// be coming out, the rail is open, the window is active and motion is not reduced — a paused deck owns no
        /// looping animation row, so it wakes no frame.</summary>
        public static bool LoopsMayRun(bool reducedMotion, bool railOpen, bool active, bool playing)
            => !reducedMotion && railOpen && active && playing;

        /// <summary>The integration step for one tick, clamped (a first tick uses the nominal step).</summary>
        public static float DeltaSec(long lastTickMs, long nowMs)
            => lastTickMs == 0L ? TickMs / 1000f : Math.Clamp((nowMs - lastTickMs) / 1000f, DtMinSec, DtMaxSec);

        /// <summary>A report nobody here asked for moved the playhead: the clock synthesizes a seek target so the medium
        /// sees an EDGE (lift, swing, lower) instead of a groove that teleported. Never on the first report of a
        /// track (<paramref name="lastDurationMs"/> 0) and never while a seek of our own is pending.</summary>
        public static bool IsRemoteJump(long reportedMs, long expectedMs, long lastDurationMs, bool seekPending)
            => !seekPending && lastDurationMs > 0 && Math.Abs(reportedMs - expectedMs) > RemoteJumpMs;

        /// <summary>Has the transport's report caught up with a seek target?</summary>
        public static bool SeekLanded(long reportedMs, long targetMs) => Math.Abs(reportedMs - targetMs) < SeekSettleMs;

        /// <summary>Release a committed seek once the report lands, or once the latch window has passed since the
        /// commit. A zero <paramref name="committedAtMs"/> (no stamp) only releases on landing — 0.2.9 compared against
        /// an unset stamp and released every latch on its first tick.</summary>
        public static bool ReleaseSeekLatch(long reportedMs, long committedMs, long nowMs, long committedAtMs)
            => SeekLanded(reportedMs, committedMs) || (committedAtMs != 0L && nowMs - committedAtMs > SeekLatchMs);

        /// <summary>One rim pixel of rotation: a disc's rim travels π·d per turn, so 360/(π·d) degrees is the smallest
        /// angle that moves a pixel (0.505° at side 324).</summary>
        public static float AngleQuantumDeg(float side) => 360f / MathF.Max(1f, MathF.PI * side * DiscFrac);

        /// <summary>Round to a perceptual quantum; a write that lands on the same step writes nothing.</summary>
        public static float Quantize(float v, float q) => q > 0f ? MathF.Round(v / q) * q : v;

        /// <summary>The platter speed for the <c>rpm</c> option's slug.</summary>
        public static float RpmFor(string? rpmSlug) => rpmSlug == "45" ? Rpm45 : Rpm33;

        /// <summary>The deck's square edge for a rail width: the content width (<c>rail − 2·inset</c>) on the 4-DIP
        /// grid, never below one quantum.</summary>
        public static float SideFor(float railWidth, float inset)
            => MathF.Max(SideQuantum, MathF.Round((railWidth - 2f * inset) / SideQuantum) * SideQuantum);

        /// <summary>A cross-kind row identity: a track slot and an episode slot are the same <c>int</c>, so the kind
        /// rides in the high byte. 0 (and any non-positive slot) is "nothing".</summary>
        public static int RowKey(byte kind, int slot) => slot <= 0 || kind == 0 ? 0 : (kind << 24) | (slot & 0x00FF_FFFF);
    }

    /// <summary>The two grips' rules — the record's headshell and the iPod's click wheel share one gesture, and this is
    /// what that gesture asks.</summary>
    public static class GripRules
    {
        /// <summary>One full turn of the click wheel moves the playhead a QUARTER of the track.</summary>
        public const float WheelGear = 0.25f;

        /// <summary>A grip is live only over a playable, unfailed, seekable item whose duration is KNOWN. ch 23
        /// §6.4(6): 0.2.9's headshell checked <c>CanSeek</c> and not the duration, so a release over an unknown
        /// duration committed a seek to 0. Both grips now refuse.</summary>
        public static bool Enabled(bool hasPlayable, bool failed, bool canSeek, long durationMs)
            => hasPlayable && !failed && canSeek && durationMs > 0;

        /// <summary>A drag is abandoned when the item or the device changes under the finger (committing it would seek
        /// the WRONG song).</summary>
        public static bool StillCurrent(bool active, bool enabled, bool sameItem, bool sameDevice)
            => active && enabled && sameItem && sameDevice;

        /// <summary>A deck draws a TRACK's medium: the clamp is 0..duration (an unknown duration clamps at zero only).</summary>
        public static long Clamp(long ms, long durationMs) => durationMs > 0 ? Math.Clamp(ms, 0, durationMs) : Math.Max(0, ms);

        /// <summary>A groove fraction → milliseconds; 0 over an unknown duration.</summary>
        public static long MsAt(float frac, long durationMs) => durationMs > 0 ? (long)(Ease.Clamp01(frac) * durationMs) : 0L;

        /// <summary>Unwrap an angular delta across the ±π seam — without it a wheel crossing 9 o'clock jumps a quarter
        /// of the track backwards.</summary>
        public static float Unwrap(float deltaRad)
            => deltaRad > MathF.PI ? deltaRad - MathF.Tau : deltaRad < -MathF.PI ? deltaRad + MathF.Tau : deltaRad;

        /// <summary>The wheel's geared advance for one (already unwrapped) angular delta.</summary>
        public static float WheelAdvance(float frac, float deltaRad) => Ease.Clamp01(frac + deltaRad / MathF.Tau * WheelGear);

        /// <summary>Does releasing the wheel commit? A tap that never moved cancels: it must not fire a seek to where the
        /// playhead already is.</summary>
        public static bool ReleaseCommits(bool moved, long durationMs) => moved && durationMs > 0;
    }

    /// <summary>What a cover/lettering leaf SHOWS. The artwork and the label change when the MECHANISM says so (a
    /// <see cref="Frame.CoverGen"/> bump), a same-album advance re-letters in place (the medium did not change), and —
    /// ch 23 §7, the one place 0.3 deliberately differs from 0.2.9's blank label — a new item's text is adopted only
    /// once it is KNOWN, so an unfetched row never letters a blank sleeve.</summary>
    public static class Lettering
    {
        /// <param name="currentRow">The playing item (<see cref="ClockRules.RowKey"/>), 0 for none.</param>
        /// <param name="currentAlbum">Its album (or show) row key, 0 when unknown.</param>
        /// <param name="currentKnown">Does the playing item carry the fields this leaf paints?</param>
        /// <param name="shownRow">The item the leaf shows now, 0 before its first latch.</param>
        /// <param name="shownAlbum">That item's album row key.</param>
        /// <param name="generationMoved">Has <see cref="Frame.CoverGen"/> moved since the leaf last latched?</param>
        /// <returns>True when the leaf should show <paramref name="currentRow"/>.</returns>
        public static bool Adopt(int currentRow, int currentAlbum, bool currentKnown, int shownRow, int shownAlbum,
                                 bool generationMoved)
        {
            if (currentRow == 0) return false;                        // nothing playing: keep what is shown
            if (currentRow == shownRow || shownRow == 0) return true;  // the same item (its text may have landed), or the first latch
            if (!currentKnown) return false;                          // never swap a lettered leaf for a blank one
            return generationMoved || (currentAlbum != 0 && currentAlbum == shownAlbum);
        }
    }
}
