using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Starts a short opportunistic no-GC region and defers explicit, blocking
/// memory-release passes while navigation is likely to be parsing XAML,
/// realizing containers, loading images, or crossfading content.
/// </summary>
public static class NavigationGcCoordinator
{
    private const long NoGcTotalBudgetBytes = 192L * 1024 * 1024;
    private const long NoGcLohBudgetBytes = 48L * 1024 * 1024;

    private static readonly object Gate = new();
    private static int _activeWindows;
    private static int _deferredReleaseCount;
    private static string? _deferredReason;
    private static ILogger? _deferredLogger;
    private static bool _noGcRegionActive;

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
            if (_activeWindows == 0)
                TryStartNoGcRegion();

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
        int pendingCount = 0;
        string? pendingReason = null;
        ILogger? pendingLogger = null;
        var shouldRunDeferredRelease = false;

        lock (Gate)
        {
            if (_activeWindows > 0)
                _activeWindows--;

            if (_activeWindows > 0)
                return;

            EndNoGcRegion();

            if (_deferredReleaseCount == 0)
                return;

            pendingCount = _deferredReleaseCount;
            pendingReason = _deferredReason;
            pendingLogger = _deferredLogger;
            _deferredReleaseCount = 0;
            _deferredReason = null;
            _deferredLogger = null;
            shouldRunDeferredRelease = true;
        }

        if (!shouldRunDeferredRelease)
            return;

        _ = ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(250);
            MemoryReleaseHelper.ReleaseWorkingSetNow(
                pendingLogger,
                $"deferred-after-navigation:{pendingReason ?? reason}:{pendingCount}");
        });
    }

    private static void TryStartNoGcRegion()
    {
        if (_noGcRegionActive)
            return;

        try
        {
            _noGcRegionActive = GC.TryStartNoGCRegion(
                NoGcTotalBudgetBytes,
                NoGcLohBudgetBytes,
                disallowFullBlockingGC: true);
        }
        catch
        {
            _noGcRegionActive = false;
        }
    }

    private static void EndNoGcRegion()
    {
        if (!_noGcRegionActive)
            return;

        try
        {
            GC.EndNoGCRegion();
        }
        catch
        {
            // If the allocation budget was exceeded, the runtime already ended
            // the region by doing a GC. There is nothing useful to do here.
        }
        finally
        {
            _noGcRegionActive = false;
        }
    }

    private sealed class TimerState
    {
        public TimerState(string reason) => Reason = reason;
        public string Reason { get; }
        public Timer? Timer { get; set; }
    }
}
