using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>One frame's worth of lyrics MOTION state, as the surface's own lanes report it at the end of a step.
/// Pure data — no scene, no signals, no engine types — so the demand decision below is unit-testable.</summary>
/// <param name="Playing">Whether the media clock is advancing. Every MEDIA-clock lane (the karaoke wipe, the glow
/// envelope, the interlude dots' fill/breath) is frozen while paused, so a paused surface demands nothing from them.</param>
/// <param name="VoiceActive">A line is being sung right now (<c>voiceLine >= 0</c>, i.e. the clock sits between the
/// line's start and its sung-out point): its wipe split and glow envelope both move every frame.</param>
/// <param name="DotsActive">The interlude dots are up — their fill and breath are both driven off the media clock.</param>
/// <param name="GlowFadeActive">An OUTGOING halo cross-fade is still in flight (wall-clock, ~240 ms).</param>
/// <param name="DofRampPending">The σ ramp has not reached its target (the pass self-quiesces).</param>
/// <param name="CascadePending">At least one line's compensating translate is still in flight or still staggered.</param>
/// <param name="FollowUnsettled">The follow has not landed: a first-landing jump is owed on a line that has actually
/// resolved, or a reserved-band edge is waiting for the arrange that measures it.</param>
/// <param name="Following">The follow mode is <c>Following</c>. Detached/resyncing is a wall-clock countdown plus a
/// programmatic settle, so it demands frames whatever the transport is doing.</param>
/// <param name="NowMs">The media clock, in ms.</param>
/// <param name="NextEventMs">The next media instant at which this surface's own resolution changes — the upcoming
/// line's lead-shifted handoff, else its start (<see cref="LyricsMotionDemand.None"/> when nothing is left).</param>
readonly record struct LyricsMotionLanes(
    bool Playing,
    bool VoiceActive,
    bool DotsActive,
    bool GlowFadeActive,
    bool DofRampPending,
    bool CascadePending,
    bool FollowUnsettled,
    bool Following,
    long NowMs,
    long NextEventMs);

/// <summary>The answer: whether the per-frame stepper must be mounted, and — when it must not — the media instant the
/// surface has to be woken at instead. <see cref="WakeAtMs"/> is <see cref="LyricsMotionDemand.None"/> when no future
/// instant can start motion (an outro, an untimed tail, a paused surface).</summary>
readonly record struct LyricsMotionDecision(bool NeedsTicks, long WakeAtMs);

/// <summary>The re-arm the view publishes to its ticker: a one-shot timeout <paramref name="DelayMs"/> from now, tagged
/// with a <paramref name="Seq"/> so that arming the SAME delay twice still restarts the timer (the hook re-arms on a
/// dep change, and two consecutive quiescent steps can legitimately ask for the same wait). A negative delay means
/// "nothing to wake for" — the ticker cancels whatever is pending.</summary>
readonly record struct LyricsMotionWake(int Seq, float DelayMs);

/// <summary>Does the lyrics surface have MOTION IN FLIGHT — i.e. is there anything for the next <c>OnFrame</c> to
/// advance? Pure and engine-free by construction so the whole gate can be unit-tested without a scene or a GPU.
///
/// <para>WHY IT EXISTS. <c>LyricsFrameStepper</c> is a <c>FrameClock.Tick</c> subscriber, and its subscription is also
/// the REQUEST for panel-rate frames (<c>WakeReasons.FrameClockPoller</c>, latency-sensitive, so the ambient cap never
/// paces it). Mounting it on <c>playing</c> alone therefore held the whole UI thread at panel rate for the entire
/// length of a track — measured on ARM64 with the panel open through an instrumental intro: 4146 frames in 30 s, 4085
/// of them record-only, to render 61. "Playing" is not motion; motion is a lane that is actually moving.</para>
///
/// <para>THE CONTRACT. Ticks are demanded while any lane is live, and the MEDIA-clock lanes only count while the clock
/// is advancing, so a paused surface keeps exactly the pre-existing behaviour (ticks only for an in-flight cascade or a
/// detached/resyncing follow). When no lane is live the surface goes quiescent and re-arms from a one-shot timeout at
/// <see cref="ArmLeadMs"/> before the next event on the MEDIA clock — the upcoming line's lead-shifted handoff, or its
/// start. Nothing is lost across the gap: the wipe split, the glow envelope and the dots' fill/breath are pure
/// functions of the media clock (never accumulated), so the first frame after a re-mount lands on the same value it
/// would have had if the stepper had never left. The two INTEGRATORS (the σ ramp and the handoff cascade) are the
/// exception, which is why they are both in the lane set: neither can go quiescent mid-flight, and the caller re-seeds
/// their dt stamps on a re-arm rather than differencing across the gap.</para></summary>
static class LyricsMotionDemand
{
    /// <summary>"No future instant can start motion." Also the caller's "not computed" sentinel.</summary>
    public const long None = long.MaxValue;

    /// <summary>How far AHEAD of the next event the surface wakes — and how close an event has to be before the
    /// decision simply stays live rather than arming a timeout for it. Two 60 Hz frames: enough that the stepper is
    /// already mounted and stepping when the syllable lands, small enough that an instrumental gap is still spent
    /// asleep. It also makes the re-arm self-terminating — a timeout that fires a hair early finds the event inside
    /// the lead and goes live instead of arming again.</summary>
    public const long ArmLeadMs = 32L;

    /// <summary>The re-check period for the states that have NO media-clock deadline at all: no document yet, an
    /// untimed one, or sync suppressed by a video. The condition that ends them is not a clock instant, so the ticker
    /// polls it on a slow TIMER (which wakes nothing that produces frames) instead of subscribing the frame clock.</summary>
    public const float UnresolvedRecheckMs = 250f;

    /// <summary>The lane test on its own: is something moving RIGHT NOW (deadline not consulted)?</summary>
    public static bool LanesMoving(in LyricsMotionLanes l)
        => l.CascadePending
        || !l.Following
        || (l.Playing && (l.VoiceActive || l.DotsActive || l.GlowFadeActive || l.DofRampPending || l.FollowUnsettled));

    /// <summary>The whole decision: ticks now, or the instant to be woken at.</summary>
    public static LyricsMotionDecision Evaluate(in LyricsMotionLanes l)
    {
        if (LanesMoving(in l)) return new LyricsMotionDecision(true, None);
        // Paused: the media clock is not advancing, so no future media instant arrives on its own. The surface is woken
        // by the transport instead (a resume, a scrub — the ticker's PositionMs effect).
        if (!l.Playing || l.NextEventMs >= None) return new LyricsMotionDecision(false, None);
        // Already inside the lead window ⇒ stay live rather than arm a timeout that would fire immediately.
        if (l.NextEventMs - l.NowMs <= ArmLeadMs) return new LyricsMotionDecision(true, None);
        return new LyricsMotionDecision(false, l.NextEventMs - ArmLeadMs);
    }

    /// <summary>The next media instant at which the surface's own resolution changes, searched from the line the view
    /// has already resolved to. Two candidates per line and they are checked in time order: the lead-shifted HANDOFF
    /// (<c>start - leadMs</c>), at which the active index moves and the emphasis/latch/cascade fire, and the line's
    /// START, at which it becomes the voice line and its wipe and glow begin. A quiescent surface is by construction
    /// past the current line's sung-out point (otherwise the voice lane would be live), so no earlier line can carry an
    /// event still in the future — which is what makes starting the scan at <paramref name="fromLine"/> exact and
    /// keeps it to a couple of iterations.</summary>
    public static long NextEventMs(IReadOnlyList<LyricLine>? lines, int fromLine, long nowMs, long leadMs)
    {
        if (lines is null) return None;
        for (int i = Math.Max(0, fromLine); i < lines.Count; i++)
        {
            long start = lines[i].StartMs;
            if (start - leadMs > nowMs) return start - leadMs;
            if (start > nowMs) return start;
        }
        return None;
    }
}
