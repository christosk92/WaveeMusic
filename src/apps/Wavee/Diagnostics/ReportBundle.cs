using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wavee;

/// <summary>Assembles the full redacted report text — the thing that goes on the clipboard and is saved beside the
/// logs as <c>wavee-report-&lt;stamp&gt;.txt</c>. Every input is assumed ALREADY redacted by the caller
/// (<c>ReportComposer</c> does that once, off the UI thread); this class only lays the pieces out and keeps the
/// whole thing under <see cref="MaxBytes"/> by dropping the oldest kept log lines first.</summary>
static class ReportBundle
{
    /// <summary>The hard cap on a saved/clipboard report — comfortably pasteable into a GitHub textarea.</summary>
    public const int MaxBytes = 60 * 1024;

    /// <summary>How many log lines a crash report carries (the previous session's or this one's tail).</summary>
    public const int CrashLogLines = 300;

    /// <summary>How many log lines a manually-filed report carries (this session's tail).</summary>
    public const int ManualLogLines = 200;

    /// <summary>How much of the bundle the in-dialog preview shows before it just says how much more there is.</summary>
    public const int PreviewChars = 12_000;

    public static string FileName(DateTimeOffset stamp) =>
        "wavee-report-" + stamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";

    /// <summary>Lays out one report: a header line naming the kind and the timestamp, a build-stamp line, an
    /// install/arch/os line, then each non-empty answer, then (when present) the redacted crash-report head, then
    /// (when <paramref name="includeLogs"/>) the diagnostics block and a fenced log excerpt. All inputs are ALREADY
    /// redacted by the caller — this method only arranges them and trims the log excerpt (oldest lines first) to
    /// keep the whole bundle at or under <see cref="MaxBytes"/>.</summary>
    public static string Build(ReportKind kind, ReportIdentity id, IReadOnlyList<(string Label, string Text)> answers,
        string diagnostics, string? crashHead, IReadOnlyList<string> logLines, string logSource, bool includeLogs, DateTimeOffset now)
    {
        var sb = new StringBuilder(8 * 1024);
        sb.Append("Wavee report · ").Append(KindName(kind)).Append(" · ")
          .Append(now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("version=").Append(id.VersionLine)
          .Append(" quad=").Append(id.Quad)
          .Append(" commit=").Append(id.Commit)
          .Append(" channel=").Append(id.Channel).Append('\n');
        sb.Append("install=").Append(id.InstallSource)
          .Append(" arch=").Append(id.Architecture)
          .Append(" os=").Append(id.WindowsVersion).Append('\n');

        foreach (var (label, text) in answers)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            sb.Append('\n').Append(label).Append(":\n").Append(text.TrimEnd()).Append('\n');
        }

        if (!string.IsNullOrEmpty(crashHead))
        {
            sb.Append("\n--- Crash report (redacted) ---\n");
            sb.Append(CapCrashHead(crashHead));
            sb.Append('\n');
        }

        if (includeLogs)
        {
            if (!string.IsNullOrEmpty(diagnostics))
                sb.Append("\n--- Diagnostics ---\n").Append(diagnostics.TrimEnd()).Append('\n');

            if (logLines.Count > 0)
            {
                sb.Append("\n--- Log excerpt (").Append(logSource).Append(", last ").Append(logLines.Count)
                  .Append(" lines, redacted) ---\n```text\n");
                AppendLogExcerpt(sb, logLines);
                sb.Append("\n```\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>Appends as many of the NEWEST <paramref name="logLines"/> as fit the remaining byte budget
    /// (<see cref="MaxBytes"/> minus everything already in <paramref name="sb"/> minus the closing fence), oldest
    /// lines dropped first. When any line is dropped, the first fenced line becomes
    /// <c>"[truncated: N older lines dropped to stay under 60 KB]"</c>.</summary>
    static void AppendLogExcerpt(StringBuilder sb, IReadOnlyList<string> logLines)
    {
        const string closeFence = "\n```\n";
        int prefixBytes = Encoding.UTF8.GetByteCount(sb.ToString());
        int budget = Math.Max(0, MaxBytes - prefixBytes - Encoding.UTF8.GetByteCount(closeFence));

        int n = logLines.Count;
        long used = 0;
        int keep = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            long lineBytes = Encoding.UTF8.GetByteCount(logLines[i]) + (keep > 0 ? 1 : 0);   // +1 joining '\n'
            if (used + lineBytes > budget) break;
            used += lineBytes;
            keep++;
        }
        int dropped = n - keep;

        string? notice = null;
        if (dropped > 0)
        {
            notice = "[truncated: " + dropped.ToString(CultureInfo.InvariantCulture) + " older lines dropped to stay under 60 KB]";
            long noticeBytes = Encoding.UTF8.GetByteCount(notice) + (keep > 0 ? 1 : 0);
            while (used + noticeBytes > budget && keep > 0)
            {
                int removedIndex = n - keep;
                long removedBytes = Encoding.UTF8.GetByteCount(logLines[removedIndex]) + (keep > 1 ? 1 : 0);
                used -= removedBytes;
                keep--;
                dropped++;
                notice = "[truncated: " + dropped.ToString(CultureInfo.InvariantCulture) + " older lines dropped to stay under 60 KB]";
                noticeBytes = Encoding.UTF8.GetByteCount(notice) + (keep > 0 ? 1 : 0);
            }
        }

        bool first = true;
        if (notice is not null) { sb.Append(notice); first = false; }
        for (int i = n - keep; i < n; i++)
        {
            if (!first) sb.Append('\n');
            sb.Append(logLines[i]);
            first = false;
        }
    }

    /// <summary>Caps the crash-report head at <see cref="MaxBytes"/>/2 (bytes, not chars — the header can carry
    /// non-ASCII paths before redaction removes most of them), appending a truncation note when it had to cut.</summary>
    static string CapCrashHead(string crashHead)
    {
        int maxBytes = MaxBytes / 2;
        if (Encoding.UTF8.GetByteCount(crashHead) <= maxBytes) return crashHead;

        const string notice = "\n[truncated: crash report head cut to fit the report budget]";
        int budget = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(notice));
        int chars = SafeTruncateChars(crashHead, budget);
        return crashHead[..chars] + notice;
    }

    /// <summary>The largest prefix of <paramref name="s"/> whose UTF-8 encoding is at most
    /// <paramref name="byteBudget"/> bytes, never splitting a surrogate pair mid-code-point.</summary>
    static int SafeTruncateChars(string s, int byteBudget)
    {
        if (byteBudget <= 0) return 0;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Encoding.UTF8.GetByteCount(s.AsSpan(0, mid)) <= byteBudget) lo = mid; else hi = mid - 1;
        }
        // Never split a UTF-16 surrogate pair.
        if (lo > 0 && lo < s.Length && char.IsHighSurrogate(s[lo - 1]) && char.IsLowSurrogate(s[lo])) lo--;
        return lo;
    }

    /// <summary>Splits a <c>crash-report-*.txt</c> file (see <see cref="CrashReport.Write"/>) at its
    /// <c>"wavee.log tail"</c> section header. <c>Head</c> is the banner, build-stamp header, exception and frame
    /// RVAs verbatim; <c>Tail</c> is the last <see cref="CrashLogLines"/> lines of the log tail that followed it.
    /// A file with no such section (an older-format or hand-edited report) returns the whole text as <c>Head</c>
    /// and an empty <c>Tail</c>.</summary>
    public static (string Head, string[] Tail) SplitCrashReport(string fileText)
    {
        const string marker = "wavee.log tail";
        int idx = fileText.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return (fileText.TrimEnd(), Array.Empty<string>());

        string head = fileText[..idx].TrimEnd();

        // Skip the rest of the title line, then the "----" underline line that follows it.
        int afterTitle = fileText.IndexOf('\n', idx);
        if (afterTitle < 0) return (head, Array.Empty<string>());
        int afterDashes = fileText.IndexOf('\n', afterTitle + 1);
        if (afterDashes < 0) return (head, Array.Empty<string>());

        string tailText = fileText[(afterDashes + 1)..];
        string[] rawLines = tailText.Split('\n');
        int lineCount = rawLines.Length;
        if (lineCount > 0 && rawLines[^1].Length == 0) lineCount--;   // the file's own trailing newline

        int take = Math.Min(CrashLogLines, lineCount);
        var tail = new string[take];
        int skip = lineCount - take;
        for (int i = 0; i < take; i++)
            tail[i] = rawLines[skip + i].TrimEnd('\r');
        return (head, tail);
    }

    public static string Preview(string bundle) =>
        bundle.Length <= PreviewChars ? bundle
            : bundle[..PreviewChars] + "\n… (" + ((bundle.Length - PreviewChars) / 1024) + " KB more in the copied report)";

    static string KindName(ReportKind kind) => kind switch
    {
        ReportKind.Crash => "Crash",
        ReportKind.Bug => "Bug",
        ReportKind.Feature => "Feature",
        ReportKind.Question => "Question",
        _ => "Idea",
    };
}
