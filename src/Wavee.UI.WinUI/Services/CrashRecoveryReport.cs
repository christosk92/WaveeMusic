using System;
using System.IO;
using System.Text.Json;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Json;

namespace Wavee.UI.WinUI.Services;

public sealed record CrashRecoveryReport(
    DateTimeOffset TimestampUtc,
    string Source,
    string ExceptionType,
    string Message,
    string StackTrace,
    string InnerException)
{
    public string TimestampDisplay => TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");

    public string FullText
    {
        get
        {
            var inner = string.IsNullOrWhiteSpace(InnerException)
                ? string.Empty
                : $"{Environment.NewLine}Inner:{Environment.NewLine}{InnerException}";
            return $"[{TimestampUtc:O}] [{Source}] {ExceptionType}: {Message}{Environment.NewLine}{StackTrace}{inner}";
        }
    }
}

public static class CrashRecoveryReportStore
{
    public static void WritePending(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);

            var report = new CrashRecoveryReport(
                DateTimeOffset.UtcNow,
                PiiRedactor.Redact(source),
                ex.GetType().Name,
                PiiRedactor.Redact(ex.Message),
                PiiRedactor.Redact(ex.StackTrace ?? string.Empty),
                ex.InnerException is null ? string.Empty : PiiRedactor.Redact(ex.InnerException.ToString()));

            var json = JsonSerializer.Serialize(report, WaveeUiWinUiJsonContext.Default.CrashRecoveryReport);
            File.WriteAllText(AppPaths.PendingCrashReportPath, json);
        }
        catch
        {
            // Best effort only. The regular crash.log append still carries the report.
        }
    }

    public static CrashRecoveryReport? TryReadPending()
    {
        try
        {
            if (!File.Exists(AppPaths.PendingCrashReportPath))
                return null;

            using var stream = new FileStream(
                AppPaths.PendingCrashReportPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, WaveeUiWinUiJsonContext.Default.CrashRecoveryReport);
        }
        catch
        {
            return TryReadCrashLogTail();
        }
    }

    public static void ClearPending()
    {
        try
        {
            if (File.Exists(AppPaths.PendingCrashReportPath))
                File.Delete(AppPaths.PendingCrashReportPath);
        }
        catch
        {
            // A locked marker should not block app recovery.
        }
    }

    private static CrashRecoveryReport? TryReadCrashLogTail()
    {
        try
        {
            if (!File.Exists(AppPaths.CrashLogPath))
                return null;

            var text = File.ReadAllText(AppPaths.CrashLogPath);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            const int maxChars = 8000;
            var tail = text.Length <= maxChars ? text : text[^maxChars..];
            return new CrashRecoveryReport(
                DateTimeOffset.UtcNow,
                "CrashLogTail",
                "Unknown",
                "Wavee recorded a crash, but the pending crash marker could not be parsed.",
                tail.Trim(),
                string.Empty);
        }
        catch
        {
            return null;
        }
    }
}
