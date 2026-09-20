// ── Entities/Daylist.cs — daylist rollover, countdown and edition rules ──────────────────────────────────────────────
//
// Role: CORE
// Owner: P
// Wave: 5
// Spec: daylist rollover / countdown / edition — pure, engine-free, no allocation
//
// Engine-free rule classes only: no timers, no signals, no I/O. Callers own the clock and the retry state; these
// classes just answer what it means.

namespace Wavee;

/// <summary>When a daylist's window has ended, Spotify does not republish the next edition instantly — it lands a
/// little late, and sometimes it is late enough that a first check still finds the stale one. So rollover is not a
/// single deadline: it is a grace period followed by a short, BOUNDED retry ladder (four tries, capped growth), after
/// which the caller gives up and waits for the next natural trigger (activation, resume, a user pull) instead of
/// polling forever.</summary>
public static class DaylistRollover
{
    public const long GraceMs = 30_000;               // Spotify publishes the next edition a little after the stated instant
    public static readonly long[] RetryMs = [30_000, 60_000, 120_000, 300_000];
    public const int MaxAttempts = 4;
    public const long MinRefireMs = 60_000;            // activation/resume may reset the ladder only this long after the last fire

    public static readonly PlaylistFields Groups =
        PlaylistFields.Identity | PlaylistFields.Format | PlaylistFields.Daylist | PlaylistFields.TrackCount | PlaylistFields.Accent;

    public enum Verdict : byte { Idle, Arm, Fire, Exhausted }

    /// <summary>windowEnd&lt;=0 means no daylist to roll: Idle. attempts&gt;=MaxAttempts means the ladder ran out: Exhausted.
    /// Otherwise the due instant is the window's end plus the grace plus the sum of the ladder steps already spent;
    /// Fire once now has reached it, Arm (with the same fireAt) while waiting.</summary>
    public static Verdict Decide(int windowEndUnixS, int attempts, long nowUnixMs, out long fireAtUnixMs)
    {
        if (windowEndUnixS <= 0)
        {
            fireAtUnixMs = 0;
            return Verdict.Idle;
        }
        if (attempts >= MaxAttempts)
        {
            fireAtUnixMs = 0;
            return Verdict.Exhausted;
        }
        long due = (long)windowEndUnixS * 1000 + GraceMs;
        for (int i = 0; i < attempts; i++) due += RetryMs[i];
        fireAtUnixMs = due;
        return nowUnixMs >= due ? Verdict.Fire : Verdict.Arm;
    }

    /// <summary>The timer delay for an Arm verdict: never under a second (avoid a busy timer) and never past
    /// int.MaxValue-1 (a real timer field's ceiling).</summary>
    public static int DelayMs(long fireAtUnixMs, long nowUnixMs)
    {
        long delta = fireAtUnixMs - nowUnixMs;
        if (delta < 1000) return 1000;
        if (delta > int.MaxValue - 1) return int.MaxValue - 1;
        return (int)delta;
    }

    /// <summary>Did the answer move the window forward? A later end means a genuinely new edition landed.</summary>
    public static bool Advanced(int heldWindowEndS, int answeredWindowEndS) => answeredWindowEndS > heldWindowEndS;

    /// <summary>May activation/resume restart the ladder from attempt 0? Only once the held window has actually
    /// ended AND enough time has passed since the last fire — otherwise a rapid resume/activation cycle would spin
    /// the ladder faster than the retry steps intend.</summary>
    public static bool ResetsOnActivation(int windowEndUnixS, long lastFireUnixMs, long nowUnixMs)
        => windowEndUnixS > 0 && nowUnixMs >= (long)windowEndUnixS * 1000 && nowUnixMs - lastFireUnixMs >= MinRefireMs;
}

/// <summary>The daylist countdown reads a monotonic frame clock, not the wall clock, so it survives a clock change
/// mid-session — but a frame clock STALLS across sleep/suspend while the wall clock does not, so it must be
/// periodically re-anchored to the wall clock or a laptop's countdown would run for real seconds of wall time
/// far past what the frame clock says. Re-anchoring is on a tick cadence AND a wall-gap trigger, whichever fires
/// first, so a stall is caught even between scheduled anchors.</summary>
public static class DaylistCountdown
{
    public const int ReanchorEveryTicks = 15;
    public const long ReanchorGapMs = 2_500;

    public enum Phase : byte { Idle, Counting, Rolling }

    /// <summary>The current unix-ms instant, derived from the last wall anchor plus how far the frame clock has
    /// moved since that anchor.</summary>
    public static long Now(long unixAnchorMs, long frameAnchorMs, long frameNowMs)
        => unixAnchorMs + (frameNowMs - frameAnchorMs);

    /// <summary>Time to re-anchor: every 15th tick, or sooner if the frame clock's own delta since the last tick
    /// exceeds the gap threshold (a sleep/suspend signature).</summary>
    public static bool NeedsReanchor(int tick, long frameDeltaSinceLastTickMs)
        => tick % ReanchorEveryTicks == 0 || frameDeltaSinceLastTickMs > ReanchorGapMs;

    /// <summary>Never negative: the countdown floors at zero once the window has passed.</summary>
    public static long RemainingMs(long expiresAtMs, long nowUnixMs) => Math.Max(0, expiresAtMs - nowUnixMs);

    /// <summary>No window (<c>&lt;=0</c>) is Idle; not yet due is Counting; due or past (including the exact
    /// instant) is Rolling — the boundary favours acting over waiting.</summary>
    public static Phase PhaseOf(long expiresAtMs, long nowUnixMs)
    {
        if (expiresAtMs <= 0) return Phase.Idle;
        return expiresAtMs > nowUnixMs ? Phase.Counting : Phase.Rolling;
    }
}

/// <summary>Which of two daylist windows should win when both are in hand. A held window of 0 means the app just
/// relaunched and hasn't decoded the daylist's own edition yet — it is unknown, not "no edition", so it must yield
/// to ANY real window rather than compete on the numbers. Once both are known, a strictly later window is a newer
/// edition; equal or older never outranks what is already held.</summary>
public static class DaylistEdition
{
    public static bool Outranks(bool incomingKnowsWindow, int incomingWindowEndS, int heldWindowEndS)
        => incomingKnowsWindow && incomingWindowEndS > 0 && incomingWindowEndS > heldWindowEndS;
}
