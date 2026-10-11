// ── Screens/ReportComposer.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the report dialog's COMPOSER and its file I/O: gather the live app state on the UI thread, compose and redact the big
// text off it (crash-report file, past-session log or this session's ring), assemble the bundle for the current answers,
// and write it (Save As, the copy beside the logs)
//
// Role: CORE (engine-free: files, the log ring, `Platform` and the entity tables; no element, component or overlay type)
// Spec: docs/themes/plan/technical-plan.md §5.1 C15 (X38)
//
// Moved out of `Feedback.UI.cs` section 6 unchanged, plus the I/O the dialog did inline: `BuildBundle`, `TryWriteFile`
// (the Save As write) and `SaveBesideLogs` (the GitHub door's copy). The dialog keeps the pickers, the clipboard, the
// browser door and the toasts.

using FluentGpu.WindowsApi.Packaging;

namespace Wavee;

public static partial class Feedback
{
    /// <summary>The UI-thread half of a compose: everything that reads live app state.</summary>
    public sealed record ComposeInput(ReportKind Kind, ReportPrefill? Prefill, RedactionRules Rules,
        ReportIdentity Identity, string DiagnosticsRaw);

    /// <summary>The fully-assembled, ALREADY-REDACTED material the dialog builds the bundle and URL from. <see cref="Rules"/>
    /// rides along so each keystroke's answers are redacted with the SAME rules the compose used.</summary>
    public sealed record ComposedReport(ReportIdentity Identity, string Diagnostics, string[] LogLines, string? CrashHead,
        string? CrashReportPath, string LogSource, string CrashSummary, string CrashTime, RedactionRules Rules);

    /// <summary>The ONE place redaction runs over the big text (once, on a pool thread). <see cref="Compose"/> touches files,
    /// <see cref="Log.Snapshot"/> and the <see cref="PastSessionLog"/> seam only — no engine call.</summary>
    public static class ReportComposer
    {
        /// <summary>UI thread: the rules (OS account + machine, and — when known — the signed-in account's id and display
        /// name plus every Connect device the roster holds), the identity, and the raw diagnostics text.</summary>
        public static ComposeInput Gather(ReportKind kind, ReportPrefill? prefill)
        {
            string diagnostics = "";
            try { diagnostics = DiagnosticsText?.Invoke() ?? ""; }
            catch (Exception ex) { Log.Warn("report", "diagnostics text failed", ex); }
            return new ComposeInput(kind, prefill, Rules(), Identity(), diagnostics);
        }

        static RedactionRules Rules()
        {
            string? account = null, displayName = null;
            string[]? devices = null;
            try
            {
                if (Platform.Scope.Account is { Length: > 0 } a) account = a;
                var me = User.Me;
                if (me.IsValid && me.Knows(UserFields.Identity)) displayName = Entities.Strings.Resolve(me.NameId);
                var rows = Playback.Devices.Rows;
                if (rows.Length > 0)
                {
                    devices = new string[rows.Length];
                    for (int i = 0; i < rows.Length; i++) devices[i] = rows[i].Name ?? "";
                }
            }
            catch (Exception ex) { Log.Warn("report", "redaction rules incomplete", ex); }
            return new RedactionRules(Environment.UserName, Environment.MachineName, account, displayName, devices);
        }

        static ReportIdentity Identity()
        {
            var v = Platform.Version;
            return ReportIdentity.From(v.SemVer, v.Codename, v.Quad, v.Commit, v.Channel, v.IsStore, v.IsDev,
                PackageIdentity.IsPackaged, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                Environment.OSVersion.Version.Build);
        }

        /// <summary>Pool thread. Log source, in priority order: (1) the prefill's crash-report file (the Reports
        /// list's "View"/crash prompt's "Report on GitHub instead" always carry one; a bare <c>ReportKind.Crash</c>
        /// with none is only the deep-link/probe corner) — its tail IS the excerpt; (2) a specific past session; (3)
        /// this session's ring, last <see cref="ReportBundle.ManualLogLines"/> entries.</summary>
        public static ComposedReport Compose(ComposeInput input)
        {
            var rules = input.Rules;
            var id = input.Identity;
            string diagnostics = ReportRedactor.Redact(input.DiagnosticsRaw, rules);

            string? reportPath = input.Prefill?.CrashReportPath;
            if (reportPath is { Length: > 0 } && TryReadFile(reportPath, out string fileText, out string crashTime))
            {
                var (head, tail) = ReportBundle.SplitCrashReport(fileText);
                string[] lines = new string[tail.Length];
                for (int i = 0; i < tail.Length; i++) lines[i] = ReportRedactor.Redact(tail[i], rules);
                string summary = ReportRedactor.Redact(ReportBundle.ExceptionSummary(head), rules);
                return new ComposedReport(id, diagnostics, lines, ReportRedactor.Redact(head, rules), reportPath,
                    "crash report", summary, crashTime, rules);
            }

            string? pastId = input.Prefill?.PastSessionId;
            if (pastId is not null && PastSessionLog?.Invoke(pastId) is { } raw)
            {
                int cap = input.Kind == ReportKind.Crash ? ReportBundle.CrashLogLines : ReportBundle.ManualLogLines;
                int take = Math.Min(raw.Count, cap), start = raw.Count - take;
                string[] lines = new string[take];
                for (int i = 0; i < take; i++) lines[i] = ReportRedactor.Redact(raw[start + i], rules);
                return new ComposedReport(id, diagnostics, lines, null, null, "previous session", "", "", rules);
            }

            var snapshot = Log.Snapshot();
            int count = Math.Min(snapshot.Length, ReportBundle.ManualLogLines), from = snapshot.Length - count;
            var thisSession = new string[count];
            for (int i = 0; i < count; i++)
            {
                var e = snapshot[from + i];
                thisSession[i] = ReportRedactor.Redact("seq=" + e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + e.Format(), rules);
            }
            return new ComposedReport(id, diagnostics, thisSession, null, null, "this session", "", "", rules);
        }

        /// <summary>The file's text plus its last-write time ("t") for the crash InfoBar — read here, off the UI thread,
        /// rather than per render.</summary>
        static bool TryReadFile(string path, out string text, out string time)
        {
            text = "";
            time = "";
            try
            {
                if (!File.Exists(path)) return false;
                text = File.ReadAllText(path);
                time = File.GetLastWriteTime(path).ToString("t");
                return true;
            }
            catch { return false; }
        }

        // ── the report I/O (the dialog's Save / Copy / GitHub doors call these) ─────────────────────────────────────────

        /// <summary>The report text for the current answers: the identity, the redacted answers, diagnostics, the crash head and
        /// the log excerpt, assembled by <see cref="ReportBundle.Build"/>.</summary>
        public static string BuildBundle(ReportKind kind, ComposedReport c, ReportLabels labels, in ReportDraft draft, bool includeLogs, DateTimeOffset now)
            => ReportBundle.Build(kind, c.Identity, ReportForm.Answers(kind, labels, c.Rules, draft), c.Diagnostics,
                c.CrashHead, c.LogLines, c.LogSource, includeLogs, now);

        /// <summary>Write the report to a path the user picked. Null on success; the exception otherwise (the caller toasts it).</summary>
        public static Exception? TryWriteFile(string path, string text)
        {
            try
            {
                File.WriteAllText(path, text);
                return null;
            }
            catch (Exception ex) { return ex; }
        }

        /// <summary>The <c>wavee-report-&lt;stamp&gt;.txt</c> beside the logs that rides the GitHub door (the full report never
        /// rides the URL). A failure is logged, not thrown: the clipboard copy and the browser still happen.</summary>
        public static void SaveBesideLogs(string bundle, DateTimeOffset now)
        {
            try
            {
                Directory.CreateDirectory(Platform.LogFolder);
                File.WriteAllText(Path.Combine(Platform.LogFolder, ReportBundle.FileName(now)), bundle);
            }
            catch (Exception ex) { Log.Warn("report", "save bundle failed", ex); }
        }
    }
}
