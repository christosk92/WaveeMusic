using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Wavee;

/// <summary>Pure listing of <c>crash-report-*.txt</c> files, factored out of <see cref="CrashReport"/> so the
/// Diagnostics "Crash reports" card can enumerate them without touching the writer/pruner. Ordering matches
/// <see cref="CrashReport.Prune"/> exactly — newest FILE NAME first, not mtime — so the card and the pruner never
/// disagree about which reports are "the newest".</summary>
static class CrashReportFiles
{
    const string Prefix = "crash-report-";
    const string Suffix = ".txt";
    const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>The <paramref name="max"/> newest crash reports in <paramref name="dir"/>, newest name first. A
    /// missing directory or an I/O failure (locked folder, vanished mid-enumeration) is an empty list, never a
    /// throw — this backs a settings-page card, not a diagnostic that must be trusted to fail loudly.</summary>
    public static (string Path, DateTime Stamp)[] List(string dir, int max = CrashReport.DefaultKeep)
    {
        if (max <= 0) return [];
        string[] files;
        try
        {
            if (!Directory.Exists(dir)) return [];
            files = Directory.GetFiles(dir, Prefix + "*" + Suffix, SearchOption.TopDirectoryOnly);
        }
        catch { return []; }

        Array.Sort(files, static (a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));

        var result = new List<(string Path, DateTime Stamp)>(Math.Min(max, files.Length));
        foreach (var file in files)
        {
            if (result.Count == max) break;
            if (TryStamp(Path.GetFileName(file), out var stamp))
                result.Add((file, stamp));
        }
        return result.ToArray();
    }

    /// <summary>Parses the <c>yyyyMMdd-HHmmss</c> stamp out of a <c>crash-report-&lt;stamp&gt;.txt</c> file name.
    /// A name of any other shape (a stray file someone dropped into the folder) fails to parse rather than being
    /// guessed at.</summary>
    public static bool TryStamp(string fileName, out DateTime stamp)
    {
        stamp = default;
        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal) || !fileName.EndsWith(Suffix, StringComparison.Ordinal))
            return false;
        var span = fileName.AsSpan(Prefix.Length, fileName.Length - Prefix.Length - Suffix.Length);
        return DateTime.TryParseExact(span, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp);
    }
}
