using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Forces an aggressive managed GC pass and gives DirectComposition a chance to
/// release retained GPU resources back to the OS.
///
/// <para>
/// Normally calling <see cref="GC.Collect()"/> manually is an antipattern, but
/// this helper is used at deliberate "the user can't see us" moments where a
/// brief stutter is acceptable in exchange for the working set actually shrinking:
/// closing a tab, minimizing the window, the window losing focus for a long time.
/// </para>
///
/// <para>
/// The two-pass collection is intentional. The first compacting collect releases
/// managed memory and queues the finalizers that <c>CompositionObject</c> uses to
/// drop retained DirectX resources; the second collect after
/// <see cref="GC.WaitForPendingFinalizers"/> reclaims the now-unreachable wrappers
/// the finalizers freed up.
/// </para>
/// </summary>
public static class MemoryReleaseHelper
{
    /// <summary>
    /// Runs the GC dance and logs before/after managed + working set sizes.
    /// Safe to call from any thread.
    /// </summary>
    public static void ReleaseWorkingSet(ILogger? logger = null, string reason = "")
    {
        long beforeManaged = GC.GetTotalMemory(false);
        long beforeWorkingSet = SafeWorkingSet();

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long afterManaged = GC.GetTotalMemory(false);
        long afterWorkingSet = SafeWorkingSet();

        logger?.LogInformation(
            "MemoryRelease ({Reason}): managed {BeforeMb:F1} → {AfterMb:F1} MB ({DeltaMb:+0.0;-0.0;0} MB), " +
            "working set {BeforeWsMb:F1} → {AfterWsMb:F1} MB ({DeltaWsMb:+0.0;-0.0;0} MB)",
            string.IsNullOrEmpty(reason) ? "manual" : reason,
            beforeManaged / 1048576.0, afterManaged / 1048576.0,
            (afterManaged - beforeManaged) / 1048576.0,
            beforeWorkingSet / 1048576.0, afterWorkingSet / 1048576.0,
            (afterWorkingSet - beforeWorkingSet) / 1048576.0);
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
