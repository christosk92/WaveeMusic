namespace Wavee.UI.Diagnostics;

/// <summary>
/// Master switch for the high-volume, per-item developer traces (home response
/// parser, sidebar reorder, home-scroll layout, and the card preview /
/// coordinator / canvas surfaces).
///
/// <para>
/// Those traces are already <c>Debug.WriteLine</c> / <c>[Conditional("DEBUG")]</c>
/// — stripped entirely from Release. The problem is Debug builds run under the
/// VS debugger: every <c>Debug.WriteLine</c> is pumped through the debug-output
/// channel synchronously and is slow enough that hundreds of per-item lines at
/// startup / on each nav dominate a profiling capture (and bury the real log).
/// Gating them behind this flag — off by default — keeps a Debug profiling run
/// representative of real cost. Flip it to <c>true</c> (e.g. from the Debug
/// page or the watch window) only when you actually need the per-item trace.
/// </para>
/// </summary>
public static class UiTrace
{
    /// <summary>When false (default), the per-item Debug traces are suppressed.</summary>
    public static bool Verbose;

    /// <summary>
    /// Verbose-gated, Debug-only trace line. <c>[Conditional("DEBUG")]</c> so the
    /// call (and its interpolated argument) is stripped from Release exactly like
    /// <c>Debug.WriteLine</c>; in Debug builds it emits only when <see cref="Verbose"/>
    /// is on. Use for the high-volume per-item home-parser / sidebar-reorder traces.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Line(string message)
    {
        if (Verbose)
            System.Diagnostics.Debug.WriteLine(message);
    }
}
