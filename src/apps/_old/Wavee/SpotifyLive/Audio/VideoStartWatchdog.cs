using System;

namespace Wavee.SpotifyLive.Audio;

/// <summary>
/// The video-media host's per-load START WATCHDOG: a POD, allocation-free decision unit that turns "the source reported
/// loaded but never reached a playing/advancing state" into exactly ONE fault, so the layers above can recover instead of
/// sitting in a paused-at-0:00 zombie state forever.
/// <para>It exists because a video can wedge in a state NOTHING reports: when a superseded predecessor's process-global
/// native Stop lands on the successor session, that session settles on <c>PlaybackState.Idle</c> — no error signal, no
/// terminal state, no position ticks — and both engine-side watchdogs are structurally unable to fire (the session one
/// requires a published Opening/Buffering; the native one requires native state ≤ 1). See <see cref="VideoLoadPump{T}"/>
/// for the wedge mechanism.</para>
/// <para>Contract: ARM per load, DISARM on the first real progress and on teardown, NEVER fire for a deliberately paused
/// session ("loaded, user paused before the first frame" is not a fault — the budget is re-based while play intent is
/// false, so a resume gets a fresh full budget). Cheap enough to piggyback the host's existing 200 ms ticker: one
/// <c>TickCount64</c> compare and a few branches, zero allocation.</para>
/// </summary>
public struct VideoStartWatchdog
{
    /// <summary>The default budget. Deliberately generous — a cold DRM start is a CDM spin-up + a license round-trip + a
    /// first-segment fetch, and a false fault would be far worse than a slow one.</summary>
    public const int DefaultTimeoutMs = 10_000;

    long _armedAtTicks;
    bool _armed;          // explicit, so tick 0 is a legal arm instant (the struct default is disarmed)
    bool _fired;
    readonly int _timeoutMs;

    /// <summary>Create a watchdog with an explicit budget (tests use a short one).</summary>
    public VideoStartWatchdog(int timeoutMs = DefaultTimeoutMs)
    {
        _timeoutMs = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
        _armedAtTicks = 0;
        _armed = false;
        _fired = false;
    }

    /// <summary>The budget this watchdog fires after.</summary>
    public readonly int TimeoutMs => _timeoutMs > 0 ? _timeoutMs : DefaultTimeoutMs;

    /// <summary>True while a load is being watched and has not yet faulted.</summary>
    public readonly bool IsArmed => _armed && !_fired;

    /// <summary>Arm for a freshly-loaded source (called once per successful <c>LoadVideo</c> build).</summary>
    public void Arm(long nowTicks)
    {
        _armedAtTicks = nowTicks;
        _armed = true;
        _fired = false;
    }

    /// <summary>Disarm — teardown, stop, dispose, or a load that never got off the ground.</summary>
    public void Disarm()
    {
        _armedAtTicks = 0;
        _armed = false;
        _fired = false;
    }

    /// <summary>The per-tick decision. Returns TRUE exactly once, on the tick the budget is exceeded with no progress and
    /// with play intent still asserted; the caller then raises its fault.</summary>
    /// <param name="nowTicks"><see cref="Environment.TickCount64"/>.</param>
    /// <param name="playIntent">Does the user/controller want this playing RIGHT NOW? False (a deliberate pause) re-bases
    /// the budget instead of counting toward a fault.</param>
    /// <param name="progressed">Has the session demonstrably started/advanced (Playing, a positive position, Ended) or
    /// already reported an error? Either way there is nothing left to watch — disarm permanently.</param>
    public bool ShouldFault(long nowTicks, bool playIntent, bool progressed)
    {
        if (!_armed || _fired) return false;
        if (progressed) { Disarm(); return false; }
        if (!playIntent) { _armedAtTicks = nowTicks; return false; }
        if (nowTicks - _armedAtTicks <= TimeoutMs) return false;
        _fired = true;
        return true;
    }
}
