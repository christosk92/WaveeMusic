using System.Diagnostics;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>
/// The ONE app-side motion clock: sample per-frame animation against the frame's own PRESENT time, never
/// <c>Environment.TickCount64</c>. TickCount64 only advances in ~15.6 ms system-timer quanta, so at the 120 Hz+ a
/// modern panel produces frames at (or whenever the GPU has headroom to spare), its per-frame delta alternates
/// 0 / 15.6 ms — which steps or freezes anything driven off it instead of gliding (see LyricsMediaClock's doc for
/// the karaoke-wipe bug this caused). Reference engines agree: Flutter hands every <c>Ticker</c> the frame's vsync
/// timestamp (<c>SchedulerBinding.currentFrameTimeStamp</c>); Chromium animates on <c>BeginFrameArgs.frame_time</c>.
///
/// <see cref="NowQpc"/> is <c>FrameClock.PresentQpc</c> — the predicted vblank the frame CURRENTLY being produced
/// will land on, set by the host at the top of every <c>RunFrame</c> — falling back to a live QPC read only before
/// the first frame / when the host has not published one. Every wall-clock stamp two per-frame writers compare
/// against each other must come from here, or the comparison silently spans two different epochs.
/// </summary>
static class FrameTime
{
    /// <summary>The current frame's present time, in <see cref="Stopwatch.GetTimestamp"/> (QPC) units.</summary>
    public static long NowQpc => FrameClock.PresentQpc > 0 ? FrameClock.PresentQpc : Stopwatch.GetTimestamp();

    /// <summary><see cref="NowQpc"/> converted to milliseconds. Comparable with any other FrameTime-derived stamp;
    /// NOT with <c>Environment.TickCount64</c> — the two are different epochs entirely.</summary>
    public static long NowMs => (long)(NowQpc * (1000.0 / Stopwatch.Frequency));
}
