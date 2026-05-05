using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Defers explicit, blocking memory-release passes while navigation is likely
/// parsing XAML, realising containers, loading images, or crossfading content.
/// Pure ref-counted boolean — the previous <c>GC.TryStartNoGCRegion</c> attempt
/// turned out to do more harm than good (allocation-budget exhaustion forced a
/// catch-up Gen2 right at navigation completion, the late-nav hiccup). The
/// runtime team's own guidance is unambiguous: <em>"For any normal kind of
/// applications, YOU DON'T NEED TO DO THIS. You are likely to make your
/// application run slower or blow up memory."</em> So we don't.
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

    private sealed class TimerState
    {
        public TimerState(string reason) => Reason = reason;
        public string Reason { get; }
        public Timer? Timer { get; set; }
    }
}
