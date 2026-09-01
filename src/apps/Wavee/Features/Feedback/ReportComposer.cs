using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Wavee;

/// <summary>What the caller already knows before <see cref="ReportComposer.Compose"/> runs: which past session's log
/// to quote, which crash-report file to read, and a starting title for the dialog's title box. All three are
/// optional — most entry points (About's "Report a problem…", the deep link) pass null and let
/// <see cref="ReportComposer.Compose"/> fall back to this session's own log.</summary>
/// <param name="PastSession">A specific past session to quote (the Diagnostics overflow's "Report this session…"
/// on a selected past session). Null means "not specified" — <see cref="ReportComposer.Compose"/> still falls back
/// to a past session on its own for a WER-dump/unclean-exit crash prompt.</param>
/// <param name="CrashReportPath">A specific <c>crash-report-*.txt</c> to read (the Crash reports card's
/// "Report…" row, or a crash prompt's own managed report). Takes priority over <see cref="CrashPromptDecision.ReportPath"/>.</param>
/// <param name="Title">A starting value for the dialog's title box, or null to leave it empty.</param>
sealed record ReportPrefill(WaveeLogSessions.Info? PastSession = null, string? CrashReportPath = null, string? Title = null);

/// <summary>The fully-assembled, ALREADY-REDACTED material <see cref="ReportDialog"/> needs to build the bundle and
/// the URL: identity, the redacted "Copy diagnostics" text, redacted log lines (from the crash report's tail, a past
/// session, or this session — whichever <see cref="ReportComposer.Compose"/> picked), the redacted crash-report head
/// (crash channel only) and a one-line summary for the crash <c>InfoBar</c>. <see cref="Rules"/> rides along so the
/// dialog can redact each keystroke's answers with the SAME rules <see cref="ReportComposer.Compose"/> used, rather
/// than re-deriving them.</summary>
sealed record ComposedReport(ReportIdentity Identity, string Diagnostics, string[] LogLines, string? CrashHead,
    string? CrashReportPath, string LogSource, string CrashSummary, RedactionRules Rules);

/// <summary>Builds a <see cref="ComposedReport"/> off the UI thread: this is the ONE place redaction actually runs
/// (once, on a threadpool thread — <see cref="ReportDialog"/> only re-redacts the small per-keystroke answers against
/// the same <see cref="RedactionRules"/> this method already resolved). Reads files, <see cref="WaveeLog"/>'s ring and
/// <see cref="WaveeLogSessions"/> only — no engine call, so it is safe inside a <c>Task.Run</c>.</summary>
static class ReportComposer
{
    /// <summary>The literal <see cref="CrashReport"/> writes right before the exception's <c>ToString()</c> — the
    /// anchor <see cref="ExceptionSummary"/> uses to pull the crash <c>InfoBar</c>'s one-line summary out of the
    /// (already assembled) report head.</summary>
    const string ExceptionMarker = "\nException\n---------\n";

    /// <summary>The redaction rules for THIS reporter: the OS account + machine names (never Spotify-specific), plus —
    /// when a session is live — the signed-in Spotify user's id/display name and the names of any Connect devices the
    /// roster currently knows about. A null or logged-out <paramref name="svc"/> still redacts the OS-level names.</summary>
    public static RedactionRules Rules(Services? svc)
    {
        var user = svc?.Session.CurrentUser;
        IReadOnlyList<string>? deviceNames = null;
        if (svc?.Devices.Devices is { Count: > 0 } devices)
        {
            var names = new string[devices.Count];
            for (int i = 0; i < devices.Count; i++) names[i] = devices[i].Name;
            deviceNames = names;
        }
        return new RedactionRules(Environment.UserName, Environment.MachineName, user?.Id, user?.DisplayName, deviceNames);
    }

    public static ReportIdentity Identity() => ReportIdentity.From(AppVersion.Info,
        FluentGpu.WindowsApi.Packaging.PackageIdentity.IsPackaged,
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        Environment.OSVersion.Version.Build);

    /// <summary>Assembles a <see cref="ComposedReport"/> for <paramref name="kind"/>. THREAD-POOL SAFE: no engine
    /// call, only file I/O, <see cref="WaveeLog.Instance"/>'s ring snapshot, <see cref="WaveeLogSessions"/> and
    /// ~60 KB of <see cref="ReportRedactor"/> regex — the caller (<c>ReportDialog</c>) runs this inside
    /// <c>Task.Run</c> and posts the result back to the UI thread.
    ///
    /// <para>Log-line source, in priority order: (1) <paramref name="prefill"/>'s or <paramref name="crash"/>'s crash
    /// report file, when one exists on disk — its tail IS the log excerpt; (2) a specific past session
    /// (<paramref name="prefill"/>) or the previous session on disk (a WER-dump/unclean-exit crash prompt has no
    /// report file to read from); (3) this session's own ring, last <see cref="ReportBundle.ManualLogLines"/> entries.</para></summary>
    public static ComposedReport Compose(ReportKind kind, ReportPrefill? prefill, CrashPromptDecision crash, Services? svc)
    {
        var rules = Rules(svc);
        var id = Identity();
        string diagnostics = ReportRedactor.Redact(SettingsPage.DiagInfoText(svc), rules);

        string? reportPath = prefill?.CrashReportPath is { Length: > 0 } p ? p : crash.ReportPath;
        if (reportPath is { Length: > 0 } && TryReadFile(reportPath, out string fileText))
        {
            var (head, tail) = ReportBundle.SplitCrashReport(fileText);
            string redactedHead = ReportRedactor.Redact(head, rules);
            string[] logLines = new string[tail.Length];
            for (int i = 0; i < tail.Length; i++) logLines[i] = ReportRedactor.Redact(tail[i], rules);
            string summary = ReportRedactor.Redact(ExceptionSummary(head), rules);
            return new ComposedReport(id, diagnostics, logLines, redactedHead, reportPath, "crash report", summary, rules);
        }

        WaveeLogSessions.Info? past = prefill?.PastSession;
        if (past is null && crash.Source is CrashSource.WerDump or CrashSource.UncleanExit)
        {
            var candidates = WaveeLogSessions.ListPastSessions(WaveeLog.Instance.BasePath, Environment.ProcessId);
            past = candidates.Count > 0 ? candidates[0] : null;
        }
        if (past is not null)
        {
            int cap = kind == ReportKind.Crash ? ReportBundle.CrashLogLines : ReportBundle.ManualLogLines;
            string[] logLines = RedactTail(WaveeLogSessions.ReadSessionRawLines(past), cap, rules);
            string? head = crash.Source == CrashSource.WerDump && crash.DumpPath is { Length: > 0 } dump
                ? ReportRedactor.Redact("Windows Error Reporting dump: " + dump, rules)
                : null;
            return new ComposedReport(id, diagnostics, logLines, head, null, "previous session", "", rules);
        }

        var snapshot = WaveeLog.Instance.Snapshot();
        int manualCap = ReportBundle.ManualLogLines;
        int take = Math.Min(snapshot.Length, manualCap);
        var thisSession = new string[take];
        int start = snapshot.Length - take;
        for (int i = 0; i < take; i++)
        {
            var e = snapshot[start + i];
            thisSession[i] = ReportRedactor.Redact(
                "seq=" + e.Sequence.ToString(CultureInfo.InvariantCulture) + " " + e.Format(), rules);
        }
        return new ComposedReport(id, diagnostics, thisSession, null, null, "this session", "", rules);
    }

    /// <summary>The newest <paramref name="cap"/> of <paramref name="lines"/>, each redacted.</summary>
    static string[] RedactTail(List<string> lines, int cap, RedactionRules rules)
    {
        int take = Math.Min(lines.Count, cap);
        var result = new string[take];
        int start = lines.Count - take;
        for (int i = 0; i < take; i++) result[i] = ReportRedactor.Redact(lines[start + i], rules);
        return result;
    }

    /// <summary>The exception's first line — <c>ex.ToString()</c>'s first line, straight out of the head
    /// <see cref="CrashReport"/> already wrote — for the crash <c>InfoBar</c>'s one-line summary. Empty when the head
    /// has no <c>Exception</c> section (an older-format report, or a hand-edited one).</summary>
    static string ExceptionSummary(string head)
    {
        int idx = head.IndexOf(ExceptionMarker, StringComparison.Ordinal);
        if (idx < 0) return "";
        int start = idx + ExceptionMarker.Length;
        if (start >= head.Length) return "";
        int end = head.IndexOf('\n', start);
        string line = end < 0 ? head[start..] : head[start..end];
        return line.TrimEnd('\r');
    }

    static bool TryReadFile(string path, out string text)
    {
        try
        {
            if (!File.Exists(path)) { text = ""; return false; }
            text = File.ReadAllText(path);
            return true;
        }
        catch { text = ""; return false; }
    }
}
