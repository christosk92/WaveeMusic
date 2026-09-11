using System;
using System.Diagnostics;

namespace Wavee;

/// <summary>
/// Maps QPC time onto a continuous media-position estimate (ms) — the ONE clock the lyrics surface's karaoke wipe,
/// follow scroll and depth-of-field ramp read for "now". Engine-free (System + System.Diagnostics only) so it is
/// unit-testable without a scene, a bridge or a GPU (see <c>LyricsMediaClockTests</c>).
///
/// <para><b>Why this exists.</b> The authoritative signal — <c>PlaybackBridge.PositionMs</c> — is itself a coarse
/// ~1 Hz IPC snapshot. The old <c>LyricsView.OnFrame</c> extrapolated it against <c>Environment.TickCount64</c>, but
/// TickCount64 only advances in ~15.6 ms SYSTEM-TIMER quanta: at the 120+ Hz a modern panel (or a GPU doing 3 ms of
/// work) actually produces frames at, its per-frame delta alternates 0 / 15.6 ms. Every snapshot disagreement was
/// then measured against that coarse "now" and folded in as a STEP (up to 125 ms in one frame), and a backward step
/// froze the wipe under the monotonic guard — "the karaoke wipe steps even with blur at 0". Reference engines agree
/// motion must sample the FRAME's own timestamp, never a wall clock: Flutter hands every <c>Ticker</c> the frame's
/// vsync stamp (<c>SchedulerBinding.currentFrameTimeStamp</c>), Chromium animates on <c>BeginFrameArgs.frame_time</c>.
/// This clock is fed <c>PlaybackBridge.LastPositionSample</c> — a (position, the QPC instant that position was true)
/// pair — and queried at <see cref="FrameTime.NowQpc"/>, so both sides of every comparison share one clock.</para>
///
/// <para><b>The model.</b> A straight line in (QPC, media-ms) space: <c>anchorMs</c> at <c>anchorQpc</c>, advancing
/// at <c>rate</c> (nominally 1.0). <see cref="OnSample"/> is the only writer of that line:
///   • The FIRST sample ever, a paused→PLAYING edge (a resume), or a disagreement bigger than
///     <see cref="SnapThresholdMs"/> (a seek / a Spotify Connect transfer / a track change) all SNAP — the line is
///     torn up and redrawn through the new sample at rate 1. A snap is a real discontinuity; there is nothing to
///     smooth.
///   • Anything smaller is ordinary IPC jitter or accumulated drift. The line is re-anchored at the SAMPLE's own
///     timestamp evaluated against the CURRENT mapping (continuity — the value does not move), and the rate is
///     nudged by up to <see cref="MaxSlew"/> so the remaining error closes over about <see cref="ConvergeMs"/> — the
///     error shrinks by ~20% every 200 ms sample, never as a step the eye can catch.
///   • WHILE PAUSED every sample pins the line flat (<c>rate = 0</c>): <see cref="At"/> then returns exactly that
///     position no matter how far <c>targetQpc</c> drifts, so a scrub while paused follows immediately and an idle
///     pause accumulates no drift to correct on resume.</para>
/// </summary>
sealed class LyricsMediaClock
{
    /// <summary>A sample this far (ms) from the mapping's own prediction is not jitter or an ordinary drift
    /// correction — it is a seek, a Connect device transfer, or a track change. Snap instead of slewing.</summary>
    public const long SnapThresholdMs = 250;

    /// <summary>The largest speed-up/slow-down an ordinary correction may apply (5%) — small enough that closing
    /// even the largest un-snapped disagreement (just under <see cref="SnapThresholdMs"/>) is never perceptible as
    /// the lyrics visibly "running fast".</summary>
    public const double MaxSlew = 0.05;

    /// <summary>The time constant an ordinary disagreement closes over: <c>rate = 1 + clamp(error / ConvergeMs, ±MaxSlew)</c>,
    /// so a fresh 200 ms-apart sample sees ~20% of the remaining error gone (error·(1 − 200/ConvergeMs) once the
    /// slew is unclamped) — a 40 ms bias is under 3 ms a little past 3 s, and keeps shrinking.</summary>
    public const double ConvergeMs = 1000.0;

    readonly double _qpcFrequency;

    bool _hasSample;
    bool _playing;
    double _anchorMs;
    long _anchorQpc;
    double _rate = 1.0;

    // The cheap monotonic guard (see At): the rate is always >= 1-MaxSlew (0.95) > 0 between snaps, so this only
    // ever matters right after one — Snap() resets it so a legitimate backward seek is never clamped away.
    long _lastReturnedMs = long.MinValue;
    long _lastTargetQpc;   // the latest frame target At() was evaluated at since the last (re)anchor — the slew pivot

    // Diagnostics — read-and-reset once per log interval (LyricsView logs "lyrics.clock" every 30 s of wall time
    // while a timed document is playing). Plain counters, no allocation.
    long _frames;
    long _zeroAdvanceFrames;
    long _maxStepMs;
    double _cumulativeSlewMs;
    long _snaps;
    long _lastPlayingMs = long.MinValue;   // previous frame's returned ms WHILE PLAYING; breaks across a pause

    /// <param name="qpcFrequency">QPC ticks per second. Defaults to <see cref="Stopwatch.Frequency"/> (0 or negative
    /// also resolves to it) — a test injects a convenient value (e.g. 1000, so QPC reads directly as milliseconds).</param>
    public LyricsMediaClock(long qpcFrequency = 0) => _qpcFrequency = qpcFrequency > 0 ? qpcFrequency : Stopwatch.Frequency;

    /// <summary>The play state the mapping was last fed — a caller whose play state flips WITHOUT a fresh sample feeds
    /// the edge itself (see <c>LyricsView.OnFrame</c>) so a resume does not sit pinned until the next ~200 ms tick.</summary>
    public bool Playing => _playing;

    /// <summary>Feed an authoritative (position, the QPC instant it was true) sample. Returns <c>true</c> when the
    /// position JUMPED (the first sample ever, or a &gt;<see cref="SnapThresholdMs"/> disagreement — a seek, a transfer,
    /// a track change) rather than converging smoothly — callers that care about a discontinuity (the follow scroll,
    /// the handoff cascade) key their "treat this like a fresh landing" recovery off that return value. A
    /// paused→playing resume always rebases the mapping but reports <c>false</c> unless the position also jumped.</summary>
    public bool OnSample(long positionMs, long sampleQpc, bool playing)
    {
        bool resuming = playing && !_playing;
        _playing = playing;

        if (!playing)
        {
            // Paused: PIN via rate 0 — the SAME formula in At() then returns anchorMs regardless of how far
            // targetQpc drifts, so a scrub (a fresh sample at a different position, still paused) is reflected
            // immediately and idling paused accumulates no error to correct on resume.
            _anchorMs = positionMs;
            _anchorQpc = sampleQpc;
            _rate = 0.0;
            _lastReturnedMs = long.MinValue;
            _lastTargetQpc = 0;
            bool firstSample = !_hasSample;
            _hasSample = true;
            if (firstSample) _snaps++;
            return firstSample;
        }

        if (!_hasSample)
        {
            Snap(positionMs, sampleQpc);
            return true;
        }

        // While paused the mapping is pinned (rate 0), so on a resume `predicted` is the paused position and the error
        // is exactly "did the position move while paused" (a paused scrub already re-pinned it, so normally ~0).
        double predicted = At_Internal(sampleQpc);
        double error = positionMs - predicted;
        bool jumped = Math.Abs(error) > SnapThresholdMs;
        if (jumped || resuming)
        {
            // A resume always REBASES (the pinned rate-0 line cannot be slewed back to rate 1), but it only reports a
            // snap when the position genuinely jumped — a plain pause/resume is not a seek, and the caller's seek
            // recovery (instant re-latch, zeroing an in-flight handoff cascade) must not fire for it.
            Snap(positionMs, sampleQpc);
            return jumped;
        }

        // Continuity: the ERROR is measured at the sample's own instant (above), but the line is pivoted where the
        // viewer last SAW it — the latest frame target — not at the sample instant. A sample is always older than the
        // frame on screen (host tick → UI post → a frame produced ~2 vblanks ahead), and re-anchoring at that older
        // instant with a new rate would retroactively shift the value already displayed: a small step at every sample.
        // Pivoting at max(last frame, sample) keeps what was shown exactly where it was and bends only the slope ahead.
        long pivot = Math.Max(_lastTargetQpc, sampleQpc);
        _anchorMs = At_Internal(pivot);
        _anchorQpc = pivot;
        _rate = 1.0 + Math.Clamp(error / ConvergeMs, -MaxSlew, MaxSlew);
        _cumulativeSlewMs += Math.Abs(error);
        return false;
    }

    void Snap(long positionMs, long qpc)
    {
        _anchorMs = positionMs;
        _anchorQpc = qpc;
        _rate = 1.0;
        _hasSample = true;
        _lastReturnedMs = long.MinValue;
        _lastTargetQpc = 0;
        _snaps++;
    }

    /// <summary>Hard-seed the mapping to an exact position, bypassing the snap/slew decision entirely — for sites
    /// that already know they are re-anchoring (a fresh document load, a deliberate lyric click, the sync-advance
    /// probe). Equivalent to feeding a "first sample" (an unconditional anchor=sample, rate=1/0 snap) regardless of
    /// history.</summary>
    public void Reset(long positionMs, long qpc, bool playing)
    {
        _hasSample = false;
        OnSample(positionMs, qpc, playing);
    }

    double At_Internal(long targetQpc) => _anchorMs + (targetQpc - _anchorQpc) * 1000.0 / _qpcFrequency * _rate;

    /// <summary>The media position (ms) at <paramref name="targetQpc"/> — call once per frame with
    /// <see cref="FrameTime.NowQpc"/>, never <c>Environment.TickCount64</c>. Zero-allocation.</summary>
    public long At(long targetQpc)
    {
        _frames++;
        if (targetQpc > _lastTargetQpc) _lastTargetQpc = targetQpc;
        long ms = (long)At_Internal(targetQpc);

        if (_playing)
        {
            // Never goes backward while playing between snaps — rate >= 1-MaxSlew (0.95) > 0 already guarantees
            // that mathematically; this is the cheap belt-and-braces the old `_lastDisplay` guard was, engaging
            // only if integer truncation ever lands a hair below the previous frame's rounded value.
            if (_lastReturnedMs != long.MinValue && ms < _lastReturnedMs) ms = _lastReturnedMs;
            _lastReturnedMs = ms;

            if (_lastPlayingMs != long.MinValue)
            {
                long step = ms - _lastPlayingMs;
                if (step == 0) _zeroAdvanceFrames++;
                long absStep = step < 0 ? -step : step;
                if (absStep > _maxStepMs) _maxStepMs = absStep;
            }
            _lastPlayingMs = ms;
        }
        else
        {
            _lastPlayingMs = long.MinValue;   // break the step-size chain across a pause
        }

        return ms;
    }

    /// <summary>Read the accumulated diagnostics since the last read and zero them — a periodic log line's source,
    /// never a per-frame one.</summary>
    public LyricsClockDiagnostics ReadAndResetDiagnostics()
    {
        var snapshot = new LyricsClockDiagnostics(_frames, _zeroAdvanceFrames, _maxStepMs, _cumulativeSlewMs, _snaps);
        _frames = 0;
        _zeroAdvanceFrames = 0;
        _maxStepMs = 0;
        _cumulativeSlewMs = 0;
        _snaps = 0;
        return snapshot;
    }
}

/// <summary>One <see cref="LyricsMediaClock"/> reporting interval's worth of counters (see
/// <see cref="LyricsMediaClock.ReadAndResetDiagnostics"/>) — the "lyrics.clock" log line's payload.</summary>
readonly record struct LyricsClockDiagnostics(long Frames, long ZeroAdvanceFrames, long MaxStepMs, double SlewMs, long Snaps);
