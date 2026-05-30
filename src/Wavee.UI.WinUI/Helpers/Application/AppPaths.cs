using System;
using System.IO;

namespace Wavee.UI.WinUI.Helpers.Application;

public static class AppPaths
{
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wavee");

    /// <summary>
    /// Local (non-roaming) data root: <c>%LOCALAPPDATA%\Wavee</c>. The out-of-process
    /// AudioHost and the native-dependency provisioner write here. Under MSIX this and
    /// <see cref="AppDataDirectory"/> redirect to different package-container subfolders,
    /// so a diagnostics bundle must gather from both.
    /// </summary>
    public static string LocalAppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee");

    /// <summary><c>%LOCALAPPDATA%\Wavee\Logs</c> — the AudioHost's Serilog output (audiohost-*.log).</summary>
    public static string AudioHostLogsDirectory { get; } = Path.Combine(LocalAppDataDirectory, "Logs");

    /// <summary><c>%LOCALAPPDATA%\Wavee\NativeDeps</c> — native-dep cache + *.failure.json markers.</summary>
    public static string NativeDepsDirectory { get; } = Path.Combine(LocalAppDataDirectory, "NativeDeps");

    public static string LogsDirectory { get; } = Path.Combine(AppDataDirectory, "logs");

    public static string RollingLogFilePath { get; } = Path.Combine(LogsDirectory, "wavee-.log");

    public static string DrmRollingLogFilePath { get; } = Path.Combine(LogsDirectory, "drm-.log");

    public static string CrashLogPath { get; } = Path.Combine(AppDataDirectory, "crash.log");

    public static string PendingCrashReportPath { get; } = Path.Combine(AppDataDirectory, "pending-crash.json");

    public static string DiagnosticsDirectory { get; } = Path.Combine(AppDataDirectory, "diag");

    public static string PhiSilicaDiagnosticsDirectory { get; } = Path.Combine(DiagnosticsDirectory, "phi-silica");
}
