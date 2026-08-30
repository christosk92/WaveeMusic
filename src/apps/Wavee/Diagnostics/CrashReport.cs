using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Wavee;

/// <summary>
/// The one-file crash artifact: header (version / pid / framework / OS), the exception, and the tail of the app log.
/// Written from the app-loop catch and the process-wide unhandled handler; the path is stashed in
/// <c>WaveeSettings.PendingCrashReport</c> so the next launch can offer it.
/// </summary>
static class CrashReport
{
    /// <summary>How many reports survive a <see cref="Prune"/>. Enough to see a pattern across a few days of a
    /// recurring crash, small enough that a crash loop cannot quietly fill the log folder.</summary>
    public const int DefaultKeep = 10;

    /// <summary>The reports folder — the SAME lowercase <c>logs</c> directory Program.cs configures the app log into, so
    /// "open the report folder" lands on the logs the report quotes rather than a sibling folder Windows created for
    /// a differently-cased path on a case-sensitive volume.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "logs");

    public static string Write(Exception ex, string? logPath)
    {
        string dir = DefaultDirectory;
        Directory.CreateDirectory(dir);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"crash-report-{stamp}.txt");

        var sb = new StringBuilder(32 * 1024);
        sb.AppendLine("Wavee crash report");
        sb.AppendLine("=================");
        // version FIRST after the banner: the only question that is always asked of a crash report is "which build?",
        // and a report pasted into an issue is usually truncated after the first few lines.
        sb.AppendLine("version=" + AppVersion());
        sb.AppendLine("timeLocal=" + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("framework=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        sb.AppendLine("os=" + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        sb.AppendLine();
        sb.AppendLine("Exception");
        sb.AppendLine("---------");
        sb.AppendLine(ex.ToString());

        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
        {
            sb.AppendLine();
            sb.AppendLine("wavee.log tail");
            sb.AppendLine("--------------");
            foreach (var line in TailLines(logPath, maxLines: 600))
                sb.AppendLine(line);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Prune(dir);
        return path;
    }

    /// <summary>Delete the oldest <c>crash-report-*.txt</c> beyond the newest <paramref name="keep"/>. Ordering is by
    /// FILE NAME, not mtime: the name carries a sortable <c>yyyyMMdd-HHmmss</c> stamp, which stays correct across a copy
    /// or a restore that resets timestamps. Best-effort — a locked or vanished file never fails a crash write.</summary>
    public static void Prune(string dir, int keep = DefaultKeep)
    {
        if (keep < 0) keep = 0;
        string[] files;
        try
        {
            if (!Directory.Exists(dir)) return;
            files = Directory.GetFiles(dir, "crash-report-*.txt", SearchOption.TopDirectoryOnly);
        }
        catch { return; }
        if (files.Length <= keep) return;

        Array.Sort(files, static (a, b) =>
            string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));   // newest name first
        for (int i = keep; i < files.Length; i++)
        {
            try { File.Delete(files[i]); }
            catch { }
        }
    }

    static string AppVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(CrashReport).Assembly;
            if (asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } v)
                return v;
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    static IEnumerable<string> TailLines(string path, int maxLines)
    {
        var q = new Queue<string>(Math.Max(16, maxLines));
        foreach (var line in File.ReadLines(path))
        {
            if (q.Count == maxLines) q.Dequeue();
            q.Enqueue(line);
        }
        return q;
    }
}
