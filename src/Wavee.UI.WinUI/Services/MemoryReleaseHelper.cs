using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Diagnostic surface for "release working set" requests. The actual native
/// <c>SetProcessWorkingSetSize(-1, -1)</c> call has been removed — see
/// <see cref="TrimWorkingSet"/> for why. The class is preserved because the
/// before/after working-set snapshot + <c>[memrel]</c> diagnostic events
/// remain useful for correlating UI stalls with whatever the rest of the
/// system thinks counts as memory pressure.
///
/// <para>
/// History: this helper previously did
/// <c>GC.Collect(2, blocking: true, compacting: true)</c> before the trim
/// (60+ forced Gen2 compacts per session, "stall then BOOM" UI freezes; the
/// GC.Collect call was removed in an earlier pass). The trim alone seemed
/// harmless because it doesn't free live memory — but in release-mode
/// diagnostics we observed it forced the OS to page out 770 MB of unmanaged
/// composition / image surface memory the next navigation immediately
/// touched, producing 7000–10000 hard page faults per click and 100–300 ms
/// UI thread stalls during fault recovery. The trim is now a no-op too;
/// Windows manages the working set itself.
/// </para>
/// </summary>
public static class MemoryReleaseHelper
{
    /// <summary>
    /// Records a "release working set" diagnostic event. Safe to call from any
    /// thread. <see cref="TrimWorkingSet"/> itself is a no-op; the before/after
    /// working-set snapshot still gets logged and forwarded to
    /// NavigationDiagnostics so [memrel] correlation surfaces continue to work.
    /// We still defer during navigation critical windows for diagnostic
    /// consistency with how the trim used to behave.
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
        // ~0 ms (TrimWorkingSet is a no-op), wsBefore == wsAfter. If a future
        // [stall] line shows a [memrel] with durMs > 50 or a non-zero ws
        // delta, that means someone re-added a native trim somewhere.
        Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.RecordMemoryRelease(
            string.IsNullOrEmpty(reason) ? "manual" : reason,
            threadId, sw.Elapsed.TotalMilliseconds,
            gen2Before, gen2After,
            beforeWorkingSet, afterWorkingSet,
            beforeManaged, afterManaged);
    }

    /// <summary>
    /// No-op. Previously called <c>SetProcessWorkingSetSize(_selfProcess.Handle, -1, -1)</c>
    /// which forced the OS to evict the process's resident pages — including
    /// ~770 MB of unmanaged composition / image surface memory the very next
    /// navigation touched, producing 7000–10000 hard page faults per click and
    /// 100–300 ms UI thread stalls during fault recovery. Modern PCs have
    /// 16–32 GB of RAM; an 800 MB working set is a non-event for the OS and
    /// it can manage paging on its own when memory is actually tight. We
    /// intentionally don't tell it to trim early.
    /// </summary>
    public static void TrimWorkingSet(ILogger? logger = null, string reason = "")
    {
        // Intentionally empty. The before/after working-set snapshot in
        // ReleaseWorkingSetNow + the [memrel] diagnostic event still record the
        // call, so it remains visible whether anyone's still trying to trim.
        logger?.LogDebug(
            "MemoryRelease ({Reason}): working-set trim is a no-op (see MemoryReleaseHelper docs)",
            string.IsNullOrEmpty(reason) ? "manual" : reason);
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
}