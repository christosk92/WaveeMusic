using System;
using System.IO;
using System.Text.Json;
using Wavee.UI.WinUI.Helpers.Application;

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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

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

            File.WriteAllText(AppPaths.PendingCrashReportPath, JsonSerializer.Serialize(report, JsonOptions));
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
            return JsonSerializer.Deserialize<CrashRecoveryReport>(stream, JsonOptions);
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
