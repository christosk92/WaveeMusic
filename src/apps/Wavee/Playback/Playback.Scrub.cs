// ── Playback/Playback.Scrub.cs ───────────────────────────────────────────────────────────────────────────────────────
// The scrub gesture as a value, and the keyboard scrub ladder: the two pieces of scrubbing the reducer and the seek bar
// decide with — and nothing that plays a sample.
//
// Role: CORE
// Wave: playback smoothness WP-3a (D4; V-PA6, V-PA9, V-PA22) — docs/plans/wavee/playback-smoothness-implementation.md §4.10
//
// `System` only: no engine type, no signal, no clock — the caller hands in `now` (the frame clock, ms), which is also what
// makes both types a unit fact. Both are plain structs whose methods are a few integer/double operations on their own
// fields: no allocation on a tick, no LINQ, no closures, no boxing (P8/P9). `public` because Wavee.Tests is a
// ProjectReference.
//
//   • `ScrubModel`          the pointer gesture (Idle → Pressed → Scrubbing). It lives in the reducer's `State` (`State.Scrub`);
//                           the reducer's ScrubBegin/ScrubMove/ScrubEnd/ScrubCancel arms call Down/Move/Up/Cancel and turn the
//                           returned `Effects` into `fx.Scrub*` (§4.9). The audio side (`Playback.Audio.Scrub.cs`) never sees it.
//   • `KeyboardScrubLadder` the Left/Right hold ladder. It lives in the seek bar component (and the stage routes the same keys
//                           to it); there is no key-up event, so the commit comes from the bar's 50 ms tick.

namespace Wavee;

/// <summary>The scrub gesture as a value (D4): Idle → Pressed → Scrubbing; Up and Cancel return to Idle at once (V-PA6: the optimistic PosMs and
/// the seek generation cover the commit — no Committing state, no landing hand-shake). Moves are coalesced to ≤ 20 per second for the audio side;
/// the velocity is audio-ms per wall-ms (dimensionless), passed through unchanged (V-PA9). Parking is the grain source's own business.</summary>
public struct ScrubModel
{
    public enum State : byte { Idle, Pressed, Scrubbing }

    /// <summary>The minimum gap between two <see cref="Effects.Move"/>s handed to the audio side: 50 ms = 20 per second.</summary>
    public const int CoalesceMs = 50;

    public State Current;
    /// <summary>Whether this gesture owns a grain voice on the pump. The reducer's ScrubBegin arm sets it (to the value of <c>fx.ScrubBegin</c>) and its
    /// ScrubEnd arm reads it AFTER <see cref="Up"/> to choose "release the held voice" over "one ordinary seek" — so <see cref="Up"/> leaves it alone;
    /// only <see cref="Cancel"/> clears it.</summary>
    public bool Audible;
    /// <summary>The latest pointer position (every <see cref="Move"/>, coalesced or not) and the frame-clock stamps of the latest move and the latest
    /// one handed to the audio side.</summary>
    public long PositionMs, LastMoveAtMs, LastSentAtMs;
    /// <summary>Audio-ms per wall-ms, smoothed over every move (not only the sent ones). Negative while dragging backwards. Dimensionless and never clamped
    /// here — the grain source owns its own rate limit.</summary>
    public double Velocity;

    /// <summary>What one call asks the reducer to do. <c>PositionMs</c> is the position the effect refers to; <c>Velocity</c> is only set on a
    /// <c>Move</c>. <c>default</c> means "nothing" (inert call, or a move swallowed by the coalescing).</summary>
    public readonly record struct Effects(bool Begin, bool Move, bool Commit, bool Cancel, long PositionMs, double Velocity);

    /// <summary>Pointer down at <paramref name="ms"/>: Idle/any → Pressed, velocity 0. Always begins (a second Down without an Up/Cancel restarts the gesture).</summary>
    public Effects Down(long ms, long now) { Current = State.Pressed; PositionMs = ms; LastMoveAtMs = LastSentAtMs = now; Velocity = 0; return new(Begin: true, false, false, false, ms, 0); }

    /// <summary>Pointer move to <paramref name="ms"/>: inert while Idle; otherwise Pressed → Scrubbing, the velocity folds in, and at most one move per
    /// <see cref="CoalesceMs"/> is returned (<see cref="Effects.Move"/>). The final position is never lost to the coalescing: <see cref="Up"/> carries it.</summary>
    public Effects Move(long ms, long now)
    {
        if (Current == State.Idle) return default;
        double dt = Math.Max(1, now - LastMoveAtMs);
        Velocity = 0.5 * Velocity + 0.5 * ((ms - PositionMs) / dt);
        PositionMs = ms; LastMoveAtMs = now; Current = State.Scrubbing;
        if (now - LastSentAtMs < CoalesceMs) return default;
        LastSentAtMs = now;
        return new(false, Move: true, false, false, ms, Velocity);
    }

    /// <summary>Pointer up at <paramref name="ms"/>: a live gesture commits ONCE and returns to Idle at once (a click — Down then Up — commits too); Idle is inert.</summary>
    public Effects Up(long ms, long now) { if (Current == State.Idle) return default; Current = State.Idle; PositionMs = ms; return new(false, false, Commit: true, false, ms, 0); }

    /// <summary>Capture lost / Escape / unmount: a live gesture returns to Idle and reports <see cref="Effects.Cancel"/> at the last position; Idle is inert. <see cref="Audible"/>
    /// is cleared either way.</summary>
    public Effects Cancel() { bool live = Current != State.Idle; Current = State.Idle; Audible = false; return live ? new(false, false, false, Cancel: true, PositionMs, 0) : default; }
}

/// <summary>Keyboard scrubbing (D4) without a key-up (V-PA22): a press or OS repeat steps the VISUAL target up the ladder — 5 s for the first
/// 0.5 s held, then 15 s, then 30 s; Shift = 1 s fine — at most once per <see cref="MinStepIntervalMs"/> (an OS repeat runs at ~30 Hz, far too fast to step
/// per repeat); the target commits from <see cref="Tick"/> once no repeat has arrived for GraceMs.
/// PURE; lives in the bar component (replaces PlayerSeekAccumulator, Shell.PlayerBar.cs:425-446).</summary>
public struct KeyboardScrubLadder
{
    public const int FineMs = 1_000, StepMs = 5_000, MidMs = 15_000, FastMs = 30_000, MidAfterMs = 500, FastAfterMs = 1_500, GraceMs = 250, MinStepIntervalMs = 150;

    /// <summary>When the current ladder began (its first press), when the latest press arrived (accepted or not — it keeps the hold and the grace alive) and
    /// when the latest ACCEPTED step happened — frame-clock ms.</summary>
    public long HeldSinceMs, LastKeyMs, LastStepMs;
    /// <summary>The visual target (ms) the presses have accumulated; what <see cref="Tick"/> commits.</summary>
    public int TargetMs;
    /// <summary>True from the first press until <see cref="Tick"/> commits it.</summary>
    public bool Active;

    /// <summary>One press or OS repeat: <paramref name="direction"/> is −1 (back) or +1 (forward), <paramref name="fine"/> is Shift. A press after
    /// more than <see cref="GraceMs"/> of silence (or none yet) starts a fresh ladder from <paramref name="fromMs"/> — and a fresh ladder's first press always
    /// steps; otherwise the ladder continues from its own target, and a press sooner than <see cref="MinStepIntervalMs"/> after the last ACCEPTED step only
    /// refreshes <see cref="LastKeyMs"/> (the target does not move). Returns the visual target, clamped to [<paramref name="minMs"/>, <paramref name="durationMs"/>]:
    /// <paramref name="minMs"/> is the live DVR window's start (negative counts as 0), and a duration at or below the minimum pins the target to the minimum.</summary>
    public int Step(int direction, bool fine, int fromMs, int durationMs, long nowMs, int minMs = 0)
    {
        if (!Active || nowMs - LastKeyMs > GraceMs) { Active = true; HeldSinceMs = nowMs; TargetMs = fromMs; }
        else if (nowMs - LastStepMs < MinStepIntervalMs) { LastKeyMs = nowMs; return TargetMs; }
        LastKeyMs = LastStepMs = nowMs;
        long held = nowMs - HeldSinceMs;
        int step = fine ? FineMs : held >= FastAfterMs ? FastMs : held >= MidAfterMs ? MidMs : StepMs;
        long lo = Math.Max(0, minMs), hi = Math.Max(lo, durationMs);
        TargetMs = (int)Math.Clamp(TargetMs + (long)direction * step, lo, hi);
        return TargetMs;
    }

    /// <summary>The bar's 50 ms tick: the committed target once the key has been idle for GraceMs, else −1.</summary>
    public int Tick(long nowMs) { if (!Active || nowMs - LastKeyMs < GraceMs) return -1; Active = false; return TargetMs; }
}
