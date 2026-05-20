using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Coordinates explicit memory-release requests while navigation is in flight.
///
/// Calls to <see cref="TryDeferRelease"/> during a navigation window are
/// deferred and then dropped when the window closes. <see cref="MemoryBudgetService"/>
/// remains the single authority for cache eviction decisions. The old behavior
/// of trimming immediately after navigation forced the OS to evict pages that
/// the next navigation faulted back in, causing self-inflicted stutter.
///
/// This class used to flip GC latency mode to SustainedLowLatency for every
/// navigation. In practice that bunched Gen2 work into catch-up pauses after
/// rapid navigation, and under memory pressure Gen2 still occurred inside the
/// critical window. The runtime now keeps its normal interactive policy; this
/// coordinator only gates explicit release/trim requests.
/// </summary>
public static class NavigationGcCoordinator
{
    private static readonly object Gate = new();
    private static int _activeWindows;
    private static int _deferredReleaseCount;
    private static string? _deferredReason;
    private static ILogger? _deferredLogger;

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

            if (_deferredReleaseCount > 0)
            {
                _deferredLogger?.LogDebug(
                    "MemoryRelease deferrals dropped post-nav (count={Count}, reason={Reason}). " +
                    "Working-set trim now owned exclusively by MemoryBudgetService.",
                    _deferredReleaseCount,
                    _deferredReason ?? reason);
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
