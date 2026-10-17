// ── Platform/FrameTime.cs ──────────────────────────────────────────────────────────────────────────────────────────
// THE app-side motion clock (ported from 0.2.9's `Features/Player/FrameTime.cs`).
//
// Role: CORE (a platform clock, not a design token: the models, the per-frame drivers and the core hosts read it, and a
// `[CoreHost]` may — WVTH008 fences `Design.*`, which holds the tokens; the clock lived there until the leftovers lane P3 moved it out
// so the album countdown and the playlist daylist hosts could be `[CoreHost]`)

using FluentGpu.Hooks;

namespace Wavee;

/// <summary>THE app-side motion clock: sample per-frame animation against the frame's own PRESENT time, never
/// <c>Environment.TickCount64</c>. TickCount64 only advances in ~15.6 ms system-timer quanta, so at the 120 Hz+ a
/// modern panel produces frames at, its per-frame delta alternates 0 / 15.6 ms — which steps or freezes anything
/// driven off it instead of gliding. Reference engines agree: Flutter hands every ticker the frame's vsync
/// timestamp; Chromium animates on the begin-frame time.
///
/// <para><see cref="NowQpc"/> is the engine's present clock — the predicted vblank the frame CURRENTLY being
/// produced will land on — falling back to a live QPC read only before the first frame. Every wall-clock stamp two
/// per-frame writers compare against each other must come from here, or the comparison silently spans two different
/// epochs. <b>There are no exceptions to this rule in this layer.</b></para></summary>
public static class FrameTime
{
    /// <summary>The current frame's present time, in QPC units.</summary>
    public static long NowQpc => FrameClock.PresentQpc > 0
        ? FrameClock.PresentQpc
        : System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary><see cref="NowQpc"/> in milliseconds. Comparable with any other frame-time-derived stamp; NOT with
    /// <c>Environment.TickCount64</c> — the two are different epochs entirely.</summary>
    public static long NowMs => (long)(NowQpc * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
}
