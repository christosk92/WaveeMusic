using System;
using System.Runtime;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Two jobs while navigation is in flight:
/// <list type="number">
/// <item>Suppress per-nav working-set trims. Callers that request a trim
/// during a nav window (via <see cref="TryDeferRelease"/>) are deferred —
/// and then dropped when the window closes. <see cref="MemoryBudgetService"/>
/// stays the single authority for "actually trim working set" decisions
/// (10 s timer + OS pressure events). The previous behaviour was to fire
/// the deferred trim 250 ms post-nav, which called
/// <c>SetProcessWorkingSetSize(-1, -1)</c> and forced the OS to evict pages
/// the very next nav had to hard-fault back in — a self-inflicted stutter
/// on rapid back-and-forth navigation.</item>
/// <item>Suppress implicit Gen2 collections by flipping
/// <see cref="GCSettings.LatencyMode"/> to <see cref="GCLatencyMode.SustainedLowLatency"/>
/// for the duration of the window. Without this, the runtime can fire a full
/// blocking compact <em>during</em> a navigation if the allocation budget happens
/// to tip over — exactly the "stall then BOOM" symptom captured in nav-health
/// report nav #34 (143 ms refresh, gen0/1/2 all +1, managedΔ=-7.7 MB).</item>
/// </list>
///
/// <para>
/// SustainedLowLatency only suppresses <strong>Gen2</strong>. Gen0/Gen1 still
/// collect normally — those are sub-millisecond and don't show up as stalls.
/// The 4-second window cap (callers pass duration) bounds heap growth: once the
/// window closes the runtime can perform any deferred Gen2 it judges necessary.
/// </para>
///
/// <para>
/// We do <em>not</em> use <c>GC.TryStartNoGCRegion</c>. An earlier attempt with
/// it caused allocation-budget exhaustion to force a catch-up Gen2 right at
/// navigation completion (the late-nav hiccup). LatencyMode is the documented,
/// well-behaved mechanism for this.
/// </para>
/// </summary>
public static class NavigationGcCoordinator
{
    private static readonly object Gate = new();
    private static int _activeWindows;
    private static int _deferredReleaseCount;
    private static string? _deferredReason;
    private static ILogger? _deferredLogger;

    // The latency mode that was in effect before any critical window opened.
    // Saved on the 0 → 1 transition, restored on the N → 0 transition. Using a
    // nullable so we can tell "no window has opened yet" apart from "window
    // open and we saved the mode".
    private static GCLatencyMode? _priorLatencyMode;

    public static bool IsNavigationCritical
    {
        get
        {
            lock (Gate)
                return _activeWindows > 0;
        }
    }

    public static void BeginCriticalWindow(TimeSpan duration, string reason)
    {
        if (duration <= TimeSpan.Zero)
            return;

        lock (Gate)
        {
            // 0 → 1: stash the prior latency mode and switch the runtime into
            // SustainedLowLatency for the duration of the window. Subsequent
            // BeginCriticalWindow calls (multiple concurrent nav reasons) just
            // bump the ref count.
            if (_activeWindows == 0)
            {
                try
                {
                    _priorLatencyMode = GCSettings.LatencyMode;
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                }
                catch
                {
                    // GCSettings.LatencyMode setter is well-behaved on .NET 10
                    // but treat it as best-effort — diagnostics over correctness.
                    _priorLatencyMode = null;
                }
            }
            _activeWindows++;
        }

        Timer? timer = null;
        var timerState = new TimerState(reason);
        timer = new Timer(
            static state =>
            {
                var timerState = (TimerState)state!;
                timerState.Timer?.Dispose();
                EndCriticalWindow(timerState.Reason);
            },
            timerState,
            duration,
            Timeout.InfiniteTimeSpan);
        timerState.Timer = timer;
    }

    public static bool TryDeferRelease(ILogger? logger, string reason)
    {
        lock (Gate)
        {
            if (_activeWindows <= 0)
                return false;

            _deferredReleaseCount++;
            _deferredReason = string.IsNullOrWhiteSpace(_deferredReason)
                ? reason
                : $"{_deferredReason},{reason}";
            _deferredLogger ??= logger;

            logger?.LogDebug(
                "MemoryRelease ({Reason}) deferred during navigation window (active={ActiveWindows})",
                string.IsNullOrWhiteSpace(reason) ? "manual" : reason,
                _activeWindows);

            return true;
        }
    }

    private static void EndCriticalWindow(string reason)
    {
        lock (Gate)
        {
            if (_activeWindows > 0)
                _activeWindows--;

            if (_activeWindows > 0)
                return;

            // N → 0: restore the latency mode the runtime had before we flipped it.
            // Any deferred Gen2 the runtime chose not to fire during the window can
            // happen now — that's exactly the desired ordering (after the user is
            // past the click, not during it).
            if (_priorLatencyMode.HasValue)
            {
                try
                {
                    GCSettings.LatencyMode = _priorLatencyMode.Value;
                }
                catch
                {
                    // best-effort restore; if it fails we stay on SustainedLowLatency
                    // until the next BeginCriticalWindow re-attempts the round trip.
                }
                _priorLatencyMode = null;
            }

            // Drain deferred-release state without executing the trim. Per-nav
            // working-set trimming is now owned exclusively by MemoryBudgetService;
            // firing it here forced page-out that the next nav had to fault back in.
            if (_deferredReleaseCount > 0)
            {
                _deferredLogger?.LogDebug(
                    "MemoryRelease deferrals dropped post-nav (count={Count}, reason={Reason}). " +
                    "Working-set trim now owned exclusively by MemoryBudgetService.",
                    _deferredReleaseCount, _deferredReason ?? reason);
                _deferredReleaseCount = 0;
                _deferredReason = null;
                _deferredLogger = null;
            }
        }
    }

    private sealed class TimerState
    {
        public TimerState(string reason) => Reason = reason;
        public string Reason { get; }
        public Timer? Timer { get; set; }
    }
}
