using System;

namespace Wavee.UI.WinUI.Services;

public static class AppFeatureFlags
{
    public const string LocalFilesEnvironmentVariable = "WAVEE_ENABLE_LOCAL_FILES";
    public const string VerboseUiDiagnosticsEnvironmentVariable = "WAVEE_VERBOSE_UI_DIAGNOSTICS";

#if DEBUG
    public const bool DiagnosticsEnabled = true;
#else
    public const bool DiagnosticsEnabled = false;
#endif

    // Verbose per-projection UI breadcrumbs (e.g. TrackDataGrid's "[track-grid]"
    // Debug.WriteLine). Default OFF even in DEBUG: under the VS debugger every
    // Debug.WriteLine is a synchronous Output-window write on the UI thread, so
    // these fire on each grid reprojection / filter keystroke and inflate
    // perceived stalls. Opt in via WAVEE_VERBOSE_UI_DIAGNOSTICS when needed.
    private static readonly Lazy<bool> VerboseUiDiagnosticsValue = new(() =>
    {
        var value = Environment.GetEnvironmentVariable(VerboseUiDiagnosticsEnvironmentVariable);
        return TryParseFlag(value, out var enabled) && enabled;
    });

    public static bool VerboseUiDiagnostics => VerboseUiDiagnosticsValue.Value;

    public const string PerfDiagnosticsEnvironmentVariable = "WAVEE_PERF_DIAGNOSTICS";

    // Continuous performance instrumentation: UiHealthMonitor's 16 ms UI-thread
    // sampler + its per-frame CompositionTarget.Rendering hook, the memory-budget
    // poller, and the nav/GC profilers (UiOperationProfiler, NavigationDiagnostics
    // — which back the [gc]/[stall]/UI-op log lines). ON in DEBUG; OFF in Release
    // by default because the per-frame sampling + GC.CollectionCount/GetTotalMemory
    // every tick is real, continuous UI-thread/CPU overhead. Opt in on a Release
    // build with WAVEE_PERF_DIAGNOSTICS=1 for field troubleshooting.
    private static readonly Lazy<bool> PerfDiagnosticsValue = new(() =>
    {
        // An explicit env override wins in ANY configuration, so you can force the
        // instrumentation OFF in a Debug run (WAVEE_PERF_DIAGNOSTICS=0) to measure
        // its CPU impact, or ON in a Release build (=1) for field troubleshooting.
        var value = Environment.GetEnvironmentVariable(PerfDiagnosticsEnvironmentVariable);
        if (TryParseFlag(value, out var enabled))
            return enabled;

        // Default: on in DEBUG, off in Release.
        return DiagnosticsEnabled;
    });

    public static bool PerfDiagnosticsEnabled => PerfDiagnosticsValue.Value;

#if WAVEE_ENABLE_LOCAL_FILES
    private const bool LocalFilesBuildDefault = true;
#else
    private const bool LocalFilesBuildDefault = false;
#endif

    private static readonly Lazy<bool> LocalFilesEnabledValue = new(() =>
    {
        var value = Environment.GetEnvironmentVariable(LocalFilesEnvironmentVariable);
        return TryParseFlag(value, out var enabled) ? enabled : LocalFilesBuildDefault;
    });

    public static bool LocalFilesEnabled => LocalFilesEnabledValue.Value;

    private static bool TryParseFlag(string? value, out bool enabled)
    {
        enabled = false;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "y":
            case "on":
            case "enabled":
                enabled = true;
                return true;

            case "0":
            case "false":
            case "no":
            case "n":
            case "off":
            case "disabled":
                enabled = false;
                return true;

            default:
                return false;
        }
    }
}
