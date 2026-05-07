using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Asks Windows to trim pages the process can release from its working set.
///
/// <para>
/// Previously this helper also performed an explicit
/// <c>GC.Collect(2, blocking: true, compacting: true)</c> before the trim, on
/// the theory that compacting first let Windows reclaim more pages. In practice
/// that produced 60+ forced Gen2 compacts per session (one per nav critical
/// window, one per memory-budget tick, one per refocus, one at startup) — each
/// a stop-the-world pause of 100–500 ms that froze the UI thread. Captured in
/// nav-health reports as the canonical "stall then BOOM" signature.
/// </para>
///
/// <para>
/// DATAS GC tunes itself; manual <c>GC.Collect</c> calls fight that and produce
/// the pathological Gen0 ≈ Gen1 ≈ Gen2 ratio. The trim alone is enough — it
/// returns unused committed pages to the OS without pausing the runtime, which
/// is what Task Manager's Memory column reflects anyway. If the runtime decides
/// a Gen2 is genuinely warranted, it will fire one — concurrently and far more
/// briefly than a forced compacting collect.
/// </para>
/// </summary>
public static class MemoryReleaseHelper
{
    /// <summary>
    /// Trims the working set and logs before/after working-set size. Safe to
    /// call from any thread; defers if a navigation critical window is open
    /// (working-set trim still triggers minor page faults on the next access,
    /// so we wait until the user is past the click).
    /// </summary>
    public static void ReleaseWorkingSet(ILogger? logger = null, string reason = "")
    {
        if (NavigationGcCoordinator.TryDeferRelease(logger, reason))
            return;

        ReleaseWorkingSetNow(logger, reason);
    }

    internal static void ReleaseWorkingSetNow(ILogger? logger = null, string reason = "")
    {
        long beforeManaged = GC.GetTotalMemory(false);
        long beforeWorkingSet = SafeWorkingSet();
        int gen2Before = GC.CollectionCount(2);
        int threadId = Environment.CurrentManagedThreadId;
        var sw = Stopwatch.StartNew();

        TrimWorkingSet(logger, reason);

        sw.Stop();
        long afterManaged = GC.GetTotalMemory(false);
        long afterWorkingSet = SafeWorkingSet();
        int gen2After = GC.CollectionCount(2);

        logger?.LogInformation(
            "MemoryRelease ({Reason}): trim {BeforeWsMb:F1} → {AfterWsMb:F1} MB ({DeltaWsMb:+0.0;-0.0;0} MB) in {DurMs:F1} ms",
            string.IsNullOrEmpty(reason) ? "manual" : reason,
            beforeWorkingSet / 1048576.0, afterWorkingSet / 1048576.0,
            (afterWorkingSet - beforeWorkingSet) / 1048576.0,
            sw.Elapsed.TotalMilliseconds);

        // Still recorded into NavigationDiagnostics: durations should now be
        // ~1 ms (just the SetProcessWorkingSetSize syscall). If a future [stall]
        // line shows a [memrel] with durMs > 50, that's a signal something is
        // wrong with the trim path.
        Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.RecordMemoryRelease(
            string.IsNullOrEmpty(reason) ? "manual" : reason,
            threadId, sw.Elapsed.TotalMilliseconds,
            gen2Before, gen2After,
            beforeWorkingSet, afterWorkingSet,
            beforeManaged, afterManaged);
    }

    /// <summary>
    /// Asks Windows to trim pages the process can release from its working set.
    /// This does not free live memory; it just returns unused committed pages to
    /// the OS sooner, which is what Task Manager's Memory column reflects.
    /// </summary>
    public static void TrimWorkingSet(ILogger? logger = null, string reason = "")
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (!SetProcessWorkingSetSize(_selfProcess.Handle, -1, -1))
            {
                logger?.LogDebug(
                    "MemoryRelease ({Reason}): working-set trim failed win32={Error}",
                    string.IsNullOrEmpty(reason) ? "manual" : reason,
                    Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "MemoryRelease ({Reason}): working-set trim failed", reason);
        }
    }

    // Cached single Process handle for the current process. Process is finalizable;
    // creating + disposing one per call (which several diagnostics paths used to do)
    // adds finalizer-queue pressure that shows up as GC.RunFinalizers CPU. Process
    // for the *current* process is safe to cache for the app lifetime.
    private static readonly Process _selfProcess = Process.GetCurrentProcess();

    private static long SafeWorkingSet()
    {
        try
        {
            // Refresh() is required — WorkingSet64 is cached on the Process instance
            // until Refresh re-queries the OS for the current process metrics.
            _selfProcess.Refresh();
            return _selfProcess.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr hProcess,
        nint dwMinimumWorkingSetSize,
        nint dwMaximumWorkingSetSize);
}
