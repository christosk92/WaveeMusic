using System;

namespace Wavee;

/// <summary>
/// The newest-wins latch behind <see cref="SystemMediaControlsBridge.OnPositionChanged"/>: it collapses a BURST of
/// position ticks into exactly ONE <c>UpdateTimeline</c> push.
/// <para>
/// <b>Why.</b> Every <c>UpdateTimeline</c> is a WinRT activation plus property puts plus a CROSS-PROCESS COM RPC to the
/// shell (~1 ms). One per second is free; one per queued tick is not. The bridge is fed from <c>PlaybackBridge</c>'s
/// UI-thread posts, so any frame that drains a BACKLOG of position ticks (each carrying a different whole second, so the
/// steady-state per-second dedupe never fires) used to pay that RPC once per queued tick, synchronously, on the UI
/// thread. This turns N ticks in one drain into N field writes plus one deferred flush.
/// </para>
/// <para>
/// <b>Shape.</b> <see cref="Push"/> latches the newest position and returns true only for the FIRST tick of a burst —
/// that is the caller's cue to schedule one flush (the bridge posts it, so it runs at the top of the next frame, after
/// the whole burst has been latched). <see cref="TryTake"/> consumes the latch and decides whether the OS actually needs
/// telling; it carries the whole-second dedupe that used to live in the bridge's <c>_lastTimelineSec</c> field, so the
/// steady ~1 Hz cadence the OS expects is unchanged.
/// </para>
/// <para>
/// <b>Cost.</b> A POD struct with four fields — no allocation on the per-tick path, and none on the flush path either.
/// Deliberately dependency-free (System only, no FluentGpu, no WinRT) so the coalescing rule is unit-testable without a
/// media session: <c>Wavee.Tests</c> source-includes this file.
/// </para>
/// </summary>
internal struct SmtcTimelineCoalescer
{
    private long _pendingMs;   // newest position latched since the last flush
    private long _lastSec;     // last whole second actually pushed to the OS (the steady-state dedupe)
    private bool _hasLast;     // has _lastSec ever been written? (default(struct) must not dedupe against second 0)
    private bool _flushQueued; // a flush is scheduled and not yet consumed

    /// <summary>True while a flush has been scheduled and not yet consumed by <see cref="TryTake"/>.</summary>
    internal readonly bool FlushQueued => _flushQueued;

    /// <summary>Record a position tick. NEWEST WINS — an unconsumed pending value is simply overwritten, so a burst of N
    /// ticks costs N field writes and no OS call. Returns true IFF the caller must SCHEDULE a flush (no flush is
    /// outstanding); every subsequent tick of the same burst returns false, which is what makes the burst cost exactly
    /// one <c>UpdateTimeline</c>.</summary>
    internal bool Push(long positionMs)
    {
        _pendingMs = positionMs;
        if (_flushQueued) return false;
        _flushQueued = true;
        return true;
    }

    /// <summary>Consume the latch — ALWAYS clears the scheduled-flush bit, so a caller that bails out (disposed bridge,
    /// no session) cannot wedge the latch armed forever. Returns true, with the clamped position, IFF the OS timeline
    /// actually has to be pushed: a non-positive duration and a whole-second value equal to the last pushed one are both
    /// dropped here.</summary>
    internal bool TryTake(long durationMs, out long positionMs)
    {
        _flushQueued = false;
        positionMs = 0;
        if (durationMs <= 0) return false;
        long pos = Math.Clamp(_pendingMs, 0, durationMs);
        long sec = pos / 1000;
        if (_hasLast && sec == _lastSec) return false;
        _hasLast = true;
        _lastSec = sec;
        positionMs = pos;
        return true;
    }
}
