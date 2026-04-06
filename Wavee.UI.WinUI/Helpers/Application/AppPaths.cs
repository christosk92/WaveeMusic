using System;
using System.IO;

namespace Wavee.UI.WinUI.Helpers.Application;

public static class AppPaths
{
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wavee");

    public static string LogsDirectory { get; } = Path.Combine(AppDataDirectory, "logs");

    public static string RollingLogFilePath { get; } = Path.Combine(LogsDirectory, "wavee-.log");

    public static string CrashLogPath { get; } = Path.Combine(AppDataDirectory, "crash.log");
}