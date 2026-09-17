// ── Screens/Feedback.cs ────────────────────────────────────────────────────────────────────────────────────────────
// ReportKind / ReportChannel / ReportChannels (the routing table + the verbatim issue-form dropdowns), the request
// payload the entry points carry (ReportPrefill), ReportIdentity, ReportKindIndex, ReportBundle, ReportForm
// (answers / URL fields / labels — ported out of 0.2.9's ReportDialogCard so they are testable), IssueFormUrl,
// RedactionRules / ReportRedactor
//
// Role: CORE
// Owner: R
// Wave: 6
// Budget: 650 lines
// Spec: ch 28 §9.5 (+ §0 items 12-14, §8's Feedback rows)
//
// Engine-free by construction: System + BCL only, no FluentGpu type, no loc lookup (the one place a label is needed,
// ReportForm, takes the RESOLVED strings as a record). Everything here is pinned by Wavee.Tests/FeedbackTests.cs.
// CrashPromptPolicy / CrashPromptDecision, CrashReportFiles, RunMarker and the crash-report writer are owner S's
// (Platform/Platform.Settings.cs, Screens/Diagnostics*.cs); the report chrome consumes S's latch, nothing is re-declared.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wavee;

public static partial class Feedback
{
    // ══ 1. KINDS AND CHANNELS ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The five channels an in-app report can land in: two issue forms that cover crashes and bugs, a
    /// feature-proposal issue form, and two GitHub Discussions categories for softer, non-actionable input.</summary>
    public enum ReportKind : byte { Crash, Bug, Feature, Question, Idea }

    /// <summary>Everything <see cref="IssueFormUrl"/> and <see cref="ReportBundle"/> need about one channel: where it
    /// lives on GitHub, which field ids it prefills (in URL order), which of those give up their content first when
    /// the URL must be trimmed, and which box the toast tells the reporter to paste the clipboard report into.</summary>
    public sealed record ReportChannel(ReportKind Kind, string Path, string TitlePrefix, string? Template, string? Category,
        string[] FieldIds, string[] TruncationOrder, string PasteBox);

    /// <summary>The routing table. The dropdown option strings are copied VERBATIM from the issue-form YAML
    /// (<c>.github/ISSUE_TEMPLATE/*.yml</c>): GitHub silently drops a prefilled value that doesn't match one exactly,
    /// so they are never localized (ch 28 §0.12) and <c>ReportChannelsTests</c> pins them against the YAML.</summary>
    public static class ReportChannels
    {
        public const string Repo = "https://github.com/christosk92/WaveeMusic";

        /// <summary>The dropdown's "no answer" slug — <see cref="ReportForm"/> compares against it to decide whether to
        /// emit an <c>Area:</c> line and an <c>area:</c> label.</summary>
        public const string NotSure = "Not sure";

        public static readonly string[] InstallSources =
            ["Microsoft Store", "Sideloaded (.appinstaller or .msix from GitHub)", "Built from source"];

        public static readonly string[] Architectures = ["x64", "ARM64", NotSure];

        public static readonly string[] When =
            ["On launch", "During playback", "When switching video on or off", "When navigating pages", "After an update", "Randomly", "Other"];

        public static readonly string[] Reproduces = ["Every time", "Sometimes", "Once so far"];

        public static readonly string[] Areas =
        [
            "playback", "video", "lyrics", "player", "connect", "library", "playlists", "search", "home", "browse",
            "concerts", "detail-pages", "sidebar", "shell", "auth", "setup", "updates", "store", "release-tooling",
            "diagnostics", "modules", "i18n", "engine", NotSure,
        ];

        public static readonly ReportChannel Crash = new(ReportKind.Crash, "/issues/new", "[Crash]: ", "crash_report.yml", null,
            ["version", "install-source", "architecture", "windows-version", "when", "reproduces", "what-were-you-doing"],
            ["what-were-you-doing"], "Crash report");

        public static readonly ReportChannel Bug = new(ReportKind.Bug, "/issues/new", "[Bug]: ", "bug_report.yml", null,
            ["version", "install-source", "architecture", "windows-version", "what-happened", "steps-to-reproduce", "expected-behaviour"],
            ["expected-behaviour", "steps-to-reproduce", "what-happened"], "Relevant log lines");

        public static readonly ReportChannel Feature = new(ReportKind.Feature, "/issues/new", "[Feature]: ", "feature_request.yml", null,
            ["problem", "proposal", "area", "alternatives"], ["alternatives", "proposal", "problem"], "Proposal");

        public static readonly ReportChannel Question = new(ReportKind.Question, "/discussions/new", "", null, "q-a", ["body"], ["body"], "Body");

        public static readonly ReportChannel Idea = new(ReportKind.Idea, "/discussions/new", "", null, "ideas", ["body"], ["body"], "Body");

        public static ReportChannel For(ReportKind kind) => kind switch
        {
            ReportKind.Crash => Crash,
            ReportKind.Bug => Bug,
            ReportKind.Feature => Feature,
            ReportKind.Question => Question,
            _ => Idea,
        };

        /// <summary>Parses a deep-link argument (<c>wavee://open?route=report&amp;arg=bug|feature|crash|question|idea</c>)
        /// case-insensitively. An unrecognized or missing argument is not an error — the caller falls back to
        /// <see cref="ReportKind.Bug"/> — so this only reports whether the parse succeeded.</summary>
        public static bool TryParseKind(string? arg, out ReportKind kind)
        {
            if (!string.IsNullOrEmpty(arg))
            {
                if (string.Equals(arg, "crash", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Crash; return true; }
                if (string.Equals(arg, "bug", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Bug; return true; }
                if (string.Equals(arg, "feature", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Feature; return true; }
                if (string.Equals(arg, "question", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Question; return true; }
                if (string.Equals(arg, "idea", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Idea; return true; }
            }
            kind = ReportKind.Bug;
            return false;
        }
    }

    // ══ 2. THE REQUEST PAYLOAD ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What a caller already knows before the composer runs. All optional — About's "Report a problem…" and
    /// the deep link pass null and the composer quotes this session's own log.</summary>
    /// <param name="PastSessionId">A specific past session to quote (the Logs panel's "Report this session…") — the
    /// <c>WaveeLogSessions.Info.SessionId</c> owner S's <c>Feedback.PastSessionLog</c> seam resolves.</param>
    /// <param name="CrashReportPath">A specific <c>crash-report-*.txt</c> to read (the Crash reports card's "Report…"
    /// row, or the crash toast's action). Takes priority over the crash prompt decision's <c>ReportPath</c>.</param>
    /// <param name="Title">A starting value for the title box.</param>
    public sealed record ReportPrefill(string? PastSessionId = null, string? CrashReportPath = null, string? Title = null);

    // ══ 3. IDENTITY ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The identity fields every channel prefills — computed once per report so <see cref="IssueFormUrl"/> and
    /// <see cref="ReportBundle"/> never derive them differently. Quad/Commit/Channel ride along for the bundle header.</summary>
    /// <param name="VersionLine">e.g. <c>"0.2.5 Breaker (0.2.5.6) · 7e209e37"</c>.</param>
    /// <param name="InstallSource">One of <see cref="ReportChannels.InstallSources"/>.</param>
    /// <param name="Architecture">One of <see cref="ReportChannels.Architectures"/>.</param>
    /// <param name="WindowsVersion">e.g. <c>"Windows 11 (build 26100)"</c>.</param>
    public sealed record ReportIdentity(string VersionLine, string InstallSource, string Architecture, string WindowsVersion,
        string Quad, string Commit, string Channel)
    {
        /// <summary>Builds the identity from the build stamp (<c>WaveeVersionInfo</c>'s SemVer / Codename / Quad / Commit /
        /// Channel / IsStore / IsDev) plus the OS facts. Every input is a parameter so this stays testable without a
        /// packaged/OS environment; the composer feeds it <c>Platform.Version</c>.</summary>
        public static ReportIdentity From(string semVer, string codename, string quad, string commit, string channel,
            bool isStore, bool isDev, bool isPackaged, string osArch, int osBuild)
        {
            var sb = new StringBuilder(64);
            sb.Append(semVer);
            if (codename.Length > 0) sb.Append(' ').Append(codename);
            if (quad.Length > 0) sb.Append(" (").Append(quad).Append(')');
            if (commit.Length > 0) sb.Append(" · ").Append(commit);

            string install = isStore ? ReportChannels.InstallSources[0]
                           : isPackaged && !isDev ? ReportChannels.InstallSources[1]
                           : ReportChannels.InstallSources[2];

            string arch = osArch switch { "X64" => "x64", "Arm64" => "ARM64", _ => ReportChannels.NotSure };

            string win = (osBuild >= 22000 ? "Windows 11" : "Windows 10") + " (build " + osBuild.ToString(CultureInfo.InvariantCulture) + ")";

            return new ReportIdentity(sb.ToString(), install, arch, win, quad, commit, channel);
        }

        /// <summary>Best-effort architecture label (<c>labels=</c> only applies for reporters with triage rights). Empty
        /// for "Not sure" — there is no such label.</summary>
        public string ArchLabel => Architecture switch { "x64" => "arch: x64", "ARM64" => "arch: arm64", _ => "" };

        /// <summary>Best-effort install-source label. Empty for "Built from source" — the repo has no such label.</summary>
        public string InstallLabel => InstallSource == ReportChannels.InstallSources[0] ? "install: store"
                                    : InstallSource == ReportChannels.InstallSources[1] ? "install: sideload"
                                    : "";
    }

    // ══ 4. THE SEGMENTED INDEX ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The report-kind ↔ <c>Segmented</c>-index round trip. <see cref="ReportKind.Crash"/> is never a segment:
    /// it is a fixed kind the dialog forces from a crash prompt/prefill.</summary>
    public static class ReportKindIndex
    {
        /// <summary>Display order — the order the dialog lists kindBug / kindFeature / kindQuestion / kindIdea.</summary>
        public static readonly ReportKind[] Segments = [ReportKind.Bug, ReportKind.Feature, ReportKind.Question, ReportKind.Idea];

        /// <summary>The index for <paramref name="kind"/>; 0 (Bug) for Crash or anything not a segment.</summary>
        public static int IndexOf(ReportKind kind)
        {
            int i = Array.IndexOf(Segments, kind);
            return i < 0 ? 0 : i;
        }

        /// <summary>The kind at an index; out of range clamps to Bug, so the round trip is stable.</summary>
        public static ReportKind KindAt(int index) => index >= 0 && index < Segments.Length ? Segments[index] : ReportKind.Bug;
    }

    // ══ 5. THE BUNDLE ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Assembles the full redacted report — the clipboard text and the saved <c>wavee-report-&lt;stamp&gt;.txt</c>.
    /// Every input is ALREADY redacted (the composer does that once, off the UI thread); this class only lays the pieces
    /// out and keeps the whole under <see cref="MaxBytes"/> by dropping the oldest log lines first.</summary>
    public static class ReportBundle
    {
        /// <summary>The hard cap on a saved/clipboard report — comfortably pasteable into a GitHub textarea.</summary>
        public const int MaxBytes = 60 * 1024;
        /// <summary>Log lines a crash report carries.</summary>
        public const int CrashLogLines = 300;
        /// <summary>Log lines a manually-filed report carries.</summary>
        public const int ManualLogLines = 200;
        /// <summary>How much the in-dialog preview shows before it says how much more there is (ch 28 §0.13).</summary>
        public const int PreviewChars = 12_000;

        /// <summary>The literal the crash-report writer puts right before the exception's <c>ToString()</c>.</summary>
        const string ExceptionMarker = "\nException\n---------\n";

        public static string FileName(DateTimeOffset stamp) =>
            "wavee-report-" + stamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";

        /// <summary>A header line (kind + timestamp), a build-stamp line, an install/arch/os line, each non-empty answer,
        /// the redacted crash-report head when present, then — when <paramref name="includeLogs"/> — the diagnostics
        /// block and a fenced log excerpt trimmed oldest-first to keep the bundle at or under <see cref="MaxBytes"/>.</summary>
        public static string Build(ReportKind kind, ReportIdentity id, IReadOnlyList<(string Label, string Text)> answers,
            string diagnostics, string? crashHead, IReadOnlyList<string> logLines, string logSource, bool includeLogs, DateTimeOffset now)
        {
            var sb = new StringBuilder(8 * 1024);
            sb.Append("Wavee report · ").Append(KindName(kind)).Append(" · ")
              .Append(now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("version=").Append(id.VersionLine).Append(" quad=").Append(id.Quad)
              .Append(" commit=").Append(id.Commit).Append(" channel=").Append(id.Channel).Append('\n');
            sb.Append("install=").Append(id.InstallSource).Append(" arch=").Append(id.Architecture)
              .Append(" os=").Append(id.WindowsVersion).Append('\n');

            foreach (var (label, text) in answers)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                sb.Append('\n').Append(label).Append(":\n").Append(text.TrimEnd()).Append('\n');
            }

            if (!string.IsNullOrEmpty(crashHead))
                sb.Append("\n--- Crash report (redacted) ---\n").Append(CapCrashHead(crashHead)).Append('\n');

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

        /// <summary>Appends as many of the NEWEST lines as fit the remaining byte budget; when any are dropped the first
        /// fenced line becomes <c>"[truncated: N older lines dropped to stay under 60 KB]"</c>.</summary>
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
                notice = TruncatedNotice(dropped);
                long noticeBytes = Encoding.UTF8.GetByteCount(notice) + (keep > 0 ? 1 : 0);
                while (used + noticeBytes > budget && keep > 0)
                {
                    int removedIndex = n - keep;
                    used -= Encoding.UTF8.GetByteCount(logLines[removedIndex]) + (keep > 1 ? 1 : 0);
                    keep--;
                    dropped++;
                    notice = TruncatedNotice(dropped);
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

        static string TruncatedNotice(int dropped) =>
            "[truncated: " + dropped.ToString(CultureInfo.InvariantCulture) + " older lines dropped to stay under 60 KB]";

        /// <summary>Caps the crash head at <see cref="MaxBytes"/>/2 BYTES (it can carry non-ASCII paths), with a note.</summary>
        static string CapCrashHead(string crashHead)
        {
            int maxBytes = MaxBytes / 2;
            if (Encoding.UTF8.GetByteCount(crashHead) <= maxBytes) return crashHead;
            const string notice = "\n[truncated: crash report head cut to fit the report budget]";
            int chars = SafeTruncateChars(crashHead, Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(notice)));
            return crashHead[..chars] + notice;
        }

        /// <summary>The largest prefix whose UTF-8 encoding fits <paramref name="byteBudget"/>, never splitting a surrogate pair.</summary>
        static int SafeTruncateChars(string s, int byteBudget)
        {
            if (byteBudget <= 0) return 0;
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Encoding.UTF8.GetByteCount(s.AsSpan(0, mid)) <= byteBudget) lo = mid; else hi = mid - 1;
            }
            if (lo > 0 && lo < s.Length && char.IsHighSurrogate(s[lo - 1]) && char.IsLowSurrogate(s[lo])) lo--;
            return lo;
        }

        /// <summary>Splits a <c>crash-report-*.txt</c> at its <c>"wavee.log tail"</c> section: <c>Head</c> is the banner,
        /// build stamp, exception and frame RVAs verbatim; <c>Tail</c> the last <see cref="CrashLogLines"/> lines after it.
        /// A file with no such section returns the whole text as the head and an empty tail.</summary>
        public static (string Head, string[] Tail) SplitCrashReport(string fileText)
        {
            const string marker = "wavee.log tail";
            int idx = fileText.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return (fileText.TrimEnd(), Array.Empty<string>());

            string head = fileText[..idx].TrimEnd();
            int afterTitle = fileText.IndexOf('\n', idx);                 // the rest of the title line…
            if (afterTitle < 0) return (head, Array.Empty<string>());
            int afterDashes = fileText.IndexOf('\n', afterTitle + 1);     // …then the "----" underline
            if (afterDashes < 0) return (head, Array.Empty<string>());

            string[] rawLines = fileText[(afterDashes + 1)..].Split('\n');
            int lineCount = rawLines.Length;
            if (lineCount > 0 && rawLines[^1].Length == 0) lineCount--;   // the file's own trailing newline

            int take = Math.Min(CrashLogLines, lineCount);
            var tail = new string[take];
            int skip = lineCount - take;
            for (int i = 0; i < take; i++) tail[i] = rawLines[skip + i].TrimEnd('\r');
            return (head, tail);
        }

        /// <summary>The exception's first line out of a report head, for the crash InfoBar's summary. Empty when the head
        /// has no <c>Exception</c> section (an older-format or hand-edited report).</summary>
        public static string ExceptionSummary(string head)
        {
            int idx = head.IndexOf(ExceptionMarker, StringComparison.Ordinal);
            if (idx < 0) return "";
            int start = idx + ExceptionMarker.Length;
            if (start >= head.Length) return "";
            int end = head.IndexOf('\n', start);
            return (end < 0 ? head[start..] : head[start..end]).TrimEnd('\r');
        }

        /// <summary>The preview cut. The tail is an ENGLISH literal by design (ch 28 §0.12b) and a preview that silently
        /// stopped mid-log would read as a truncation bug — keep both the cap and the tail.</summary>
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

    // ══ 6. THE FORM (answers, URL fields, labels — 0.2.9 ReportDialogCard's pure half) ═════════════════════════════

    /// <summary>The form's field labels, already localized — the one input from the loc table this CORE takes.</summary>
    public sealed record ReportLabels(string Title, string When, string Reproduces, string WhatWereYouDoing, string WhatHappened,
        string Steps, string Expected, string Area, string Problem, string Proposal, string Alternatives, string Body);

    /// <summary>What the reporter has typed/picked so far: the title, the kind's three free-text fields in field order
    /// (Crash: doing · Bug: happened/steps/expected · Feature: problem/proposal/alternatives · Question/Idea: body) and
    /// the three dropdown indices.</summary>
    public readonly record struct ReportDraft(string Title, string F1, string F2, string F3, int When, int Reproduces, int Area);

    public static class ReportForm
    {
        /// <summary>Crash mode is forced by a crash kind, a crash prompt, or a prefill naming a crash-report file — the
        /// listed file's "Report…", the relaunch prompt and <c>arg=crash</c> all land on the same fixed-kind form.</summary>
        public static ReportKind EffectiveKind(ReportKind kind, string? crashReportPath, bool crashPrompt)
            => kind == ReportKind.Crash || crashPrompt || crashReportPath is { Length: > 0 } ? ReportKind.Crash : kind;

        /// <summary>Include logs by default for Crash and Bug only.</summary>
        public static bool IncludesLogsByDefault(ReportKind kind) => kind is ReportKind.Crash or ReportKind.Bug;

        static int Clamp(int i, int len) => i < 0 || i >= len ? 0 : i;
        public static string WhenAt(int i) => ReportChannels.When[Clamp(i, ReportChannels.When.Length)];
        public static string ReproducesAt(int i) => ReportChannels.Reproduces[Clamp(i, ReportChannels.Reproduces.Length)];
        public static string AreaAt(int i) => ReportChannels.Areas[Clamp(i, ReportChannels.Areas.Length)];

        /// <summary>The bundle's answer section. Only the small free-text answers are redacted here — per keystroke,
        /// against the SAME rules the composer resolved.</summary>
        public static List<(string Label, string Text)> Answers(ReportKind kind, ReportLabels l, RedactionRules rules, in ReportDraft d)
        {
            var list = new List<(string, string)>(6);
            string t = d.Title.Trim();
            if (t.Length > 0) list.Add((l.Title, t));
            switch (kind)
            {
                case ReportKind.Crash:
                    list.Add((l.When, WhenAt(d.When)));
                    list.Add((l.Reproduces, ReproducesAt(d.Reproduces)));
                    list.Add((l.WhatWereYouDoing, ReportRedactor.Redact(d.F1, rules)));
                    break;
                case ReportKind.Bug:
                    list.Add((l.WhatHappened, ReportRedactor.Redact(d.F1, rules)));
                    list.Add((l.Steps, ReportRedactor.Redact(d.F2, rules)));
                    list.Add((l.Expected, ReportRedactor.Redact(d.F3, rules)));
                    list.Add((l.Area, AreaAt(d.Area)));
                    break;
                case ReportKind.Feature:
                    list.Add((l.Problem, ReportRedactor.Redact(d.F1, rules)));
                    list.Add((l.Proposal, ReportRedactor.Redact(d.F2, rules)));
                    list.Add((l.Area, AreaAt(d.Area)));
                    list.Add((l.Alternatives, ReportRedactor.Redact(d.F3, rules)));
                    break;
                default:
                    list.Add((l.Body, ReportRedactor.Redact(d.F1, rules)));
                    break;
            }
            return list;
        }

        /// <summary>The URL's per-channel fields. GitHub's issue UI prefills inputs and textareas from the URL but NEVER
        /// dropdowns (verified 2026-09-02), so every dropdown answer is ALSO written as the first line of the neighbouring
        /// text field; the dropdown params stay in the URL (harmless, and they light up if GitHub ever supports them).</summary>
        public static List<KeyValuePair<string, string>> UrlFields(ReportKind kind, ReportLabels l, ReportIdentity id,
            RedactionRules rules, in ReportDraft d)
        {
            var fields = new List<KeyValuePair<string, string>>(4);
            switch (kind)
            {
                case ReportKind.Crash:
                {
                    string when = WhenAt(d.When), repro = ReproducesAt(d.Reproduces);
                    fields.Add(new("when", when));
                    fields.Add(new("reproduces", repro));
                    fields.Add(new("what-were-you-doing", l.When + ": " + when + " \u00b7 " + l.Reproduces + ": " + repro
                        + "\n\n" + ReportRedactor.Redact(d.F1, rules)));
                    break;
                }
                case ReportKind.Bug:
                    fields.Add(new("what-happened", AreaLine(l.Area, d.Area) + ReportRedactor.Redact(d.F1, rules)));
                    fields.Add(new("steps-to-reproduce", ReportRedactor.Redact(d.F2, rules)));
                    fields.Add(new("expected-behaviour", ReportRedactor.Redact(d.F3, rules)));
                    break;
                case ReportKind.Feature:
                    fields.Add(new("problem", AreaLine(l.Area, d.Area) + ReportRedactor.Redact(d.F1, rules)));
                    fields.Add(new("proposal", ReportRedactor.Redact(d.F2, rules)));
                    fields.Add(new("area", AreaAt(d.Area)));
                    fields.Add(new("alternatives", ReportRedactor.Redact(d.F3, rules)));
                    break;
                default:
                    fields.Add(new("body", ReportRedactor.Redact(d.F1, rules)
                        + "\n\n" + id.VersionLine + " · " + id.Architecture + " · " + id.WindowsVersion));
                    break;
            }
            return fields;
        }

        /// <summary>"Area: playback" + a blank line when an area was picked; empty for "Not sure".</summary>
        public static string AreaLine(string areaLabel, int area)
        {
            string slug = AreaAt(area);
            return slug.Length == 0 || slug == ReportChannels.NotSure ? "" : areaLabel + ": " + slug + "\n\n";
        }

        /// <summary>Best-effort labels: arch + install for the issue channels, plus <c>area: &lt;slug&gt;</c> on a Bug with
        /// a picked area. Discussions get none.</summary>
        public static List<string> Labels(ReportKind kind, ReportIdentity id, int area)
        {
            var labels = new List<string>(3);
            if (kind is ReportKind.Question or ReportKind.Idea) return labels;
            if (id.ArchLabel.Length > 0) labels.Add(id.ArchLabel);
            if (id.InstallLabel.Length > 0) labels.Add(id.InstallLabel);
            if (kind == ReportKind.Bug && AreaAt(area) is { Length: > 0 } slug && slug != ReportChannels.NotSure) labels.Add("area: " + slug);
            return labels;
        }

        /// <summary>The URL title: the typed title, else the crash summary, else the crash InfoBar's title.</summary>
        public static string UrlTitle(string title, string crashSummary, string crashTitle)
        {
            string t = title.Trim();
            return t.Length > 0 ? t : crashSummary.Length > 0 ? crashSummary : crashTitle;
        }
    }

    // ══ 7. THE URL ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The prefilled issue-form / Discussions URL — the small fields only; the full report never rides the URL.
    /// GitHub rejects a URL over ~8 KB, so <see cref="Build"/> trims the least-essential long fields
    /// (<see cref="ReportChannel.TruncationOrder"/>) until the result fits <see cref="Budget"/>.</summary>
    public static class IssueFormUrl
    {
        /// <summary>Comfortably under GitHub's ~8 KB limit, with headroom for the browser and any proxy.</summary>
        public const int Budget = 7000;

        /// <summary><paramref name="fields"/> maps field ids to already-typed answers; a dropdown id whose value is not one
        /// of its options THROWS rather than silently sending GitHub a value it drops. The identity fields come from
        /// <paramref name="id"/> and are never truncated.</summary>
        public static string Build(ReportKind kind, ReportIdentity id, string title, IReadOnlyList<KeyValuePair<string, string>> fields,
            IReadOnlyList<string> labels)
        {
            var ch = ReportChannels.For(kind);
            var head = new StringBuilder(256).Append(ReportChannels.Repo).Append(ch.Path).Append('?');
            if (ch.Template is { } t) head.Append("template=").Append(t).Append('&');
            if (ch.Category is { } c) head.Append("category=").Append(c).Append('&');
            head.Append("title=").Append(E(ch.TitlePrefix + title.Trim()));

            if (ch.Template is not null)
                head.Append("&version=").Append(E(id.VersionLine))
                    .Append("&install-source=").Append(E(id.InstallSource))
                    .Append("&architecture=").Append(E(id.Architecture))
                    .Append("&windows-version=").Append(E(id.WindowsVersion));

            if (labels.Count > 0) head.Append("&labels=").Append(E(string.Join(",", labels)));

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in fields)
            {
                Validate(kv.Key, kv.Value);
                values[kv.Key] = kv.Value;
            }

            for (int pass = 0; pass <= ch.TruncationOrder.Length; pass++)
            {
                string url = Assemble(head, ch, values);
                if (url.Length <= Budget) return url;
                if (pass == ch.TruncationOrder.Length) return url[..Budget];

                string f = ch.TruncationOrder[pass];
                if (!values.TryGetValue(f, out var v) || v.Length == 0) continue;
                // %XX ≈ 3 chars per source char worst case — an overestimate keeps this converging in one pass per field.
                int cut = Math.Max(0, v.Length - (url.Length - Budget) / 3 - 2);
                values[f] = cut == 0 ? "" : v[..cut] + "…";
            }
            return Assemble(head, ch, values);
        }

        /// <summary>The head plus <c>&amp;id=value</c> for every non-identity field id with a non-empty value, in channel order.</summary>
        static string Assemble(StringBuilder head, ReportChannel ch, Dictionary<string, string> values)
        {
            var sb = new StringBuilder(head.Length + 512).Append(head);
            foreach (var fieldId in ch.FieldIds)
            {
                if (fieldId is "version" or "install-source" or "architecture" or "windows-version") continue;
                if (values.TryGetValue(fieldId, out var v) && v.Length > 0)
                    sb.Append('&').Append(fieldId).Append('=').Append(E(v));
            }
            return sb.ToString();
        }

        /// <summary>A dropdown must receive one of its option strings exactly (case, whitespace); an empty optional value
        /// and a non-dropdown id are never validated.</summary>
        static void Validate(string id, string value)
        {
            string[]? options = id switch
            {
                "install-source" => ReportChannels.InstallSources,
                "architecture" => ReportChannels.Architectures,
                "when" => ReportChannels.When,
                "reproduces" => ReportChannels.Reproduces,
                "area" => ReportChannels.Areas,
                _ => null,
            };
            if (options is null || value.Length == 0) return;
            if (Array.IndexOf(options, value) < 0)
                throw new ArgumentException($"'{value}' is not a valid option for field '{id}'.", nameof(value));
        }

        static string E(string s) => Uri.EscapeDataString(s);
    }

    // ══ 8. REDACTION ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Names scrubbed beyond the built-in patterns — what the app knows about this install that is not generic
    /// enough to catch on its own (the OS account, the machine, the signed-in Spotify account, Connect device names).
    /// All optional: a report composed before login passes <see cref="None"/>.</summary>
    public sealed record RedactionRules(string? UserName, string? MachineName, string? SpotifyUserId, string? DisplayName,
        IReadOnlyList<string>? DeviceNames)
    {
        /// <summary>No injected literals — only the built-in path/secret/network patterns fire.</summary>
        public static readonly RedactionRules None = new(null, null, null, null, null);
    }

    /// <summary>Whole-text scrubber for anything about to be handed to a stranger on GitHub. Ordered and idempotent:
    /// every replacement is a placeholder token none of the other patterns can match, so running it twice is a no-op.
    /// AOT-safe: every pattern is a source-generated <see cref="GeneratedRegexAttribute"/> matcher.</summary>
    public static partial class ReportRedactor
    {
        /// <summary><c>C:\Users\bob\...</c> / <c>c:/users/Bob/...</c> — keeps the drive and "Users", blanks the account.</summary>
        [GeneratedRegex(@"(?i)([A-Z]:[\\/]+Users[\\/]+)([^\\/\s""'|<>]+)")]
        private static partial Regex ProfilePath();

        /// <summary><c>%USERPROFILE%</c> / <c>$env:USERPROFILE</c> — one opaque placeholder.</summary>
        [GeneratedRegex(@"(?i)%USERPROFILE%|\$env:USERPROFILE")]
        private static partial Regex ProfileVar();

        /// <summary>macOS/Linux-style home paths, for a log captured or pasted from elsewhere.</summary>
        [GeneratedRegex(@"(/Users/)([^/\s""'|<>]+)")]
        private static partial Regex MacProfile();

        /// <summary>A UNC host (<c>\\NAS01\share</c> → <c>\\&lt;host&gt;\share</c>); the guard keeps it off a larger token.</summary>
        [GeneratedRegex(@"(?<![\w:])\\\\([^\\\s""'|<>]+)(\\)")]
        private static partial Regex UncHost();

        /// <summary><c>spotify:user:…</c> only — track/album URIs are content, not identity, and must survive.</summary>
        [GeneratedRegex(@"(spotify:user:)([^\s:""'|<>]+)")]
        private static partial Regex SpotifyUser();

        [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
        private static partial Regex Email();

        /// <summary><c>Authorization: Bearer …</c> — keeps the word, redacts the token.</summary>
        [GeneratedRegex(@"(?i)\b(bearer)\s+[A-Za-z0-9\-._~+/]+=*")]
        private static partial Regex Bearer();

        /// <summary>A secret-named <c>key=value</c> / <c>key: value</c>. Needs the <c>=</c>/<c>:</c> right after the key word, so
        /// prose ("the key of C") never fires; a <c>Bearer</c> value is left to <see cref="Bearer"/>.</summary>
        [GeneratedRegex(@"(?i)\b(access_?token|refresh_?token|id_?token|token|api_?key|key|auth|authorization|password|pwd|secret|cookie|set-cookie|session_?id)\s*[=:]\s*(""?)(?!bearer\b)([^\s&""|,;<>]+)")]
        private static partial Regex KeyValueSecret();

        /// <summary>Account facts that identify in aggregate (country, tier, catalogue) — same shape and discipline.</summary>
        [GeneratedRegex(@"(?i)\b(country|product|product_?tier|tier|catalogue)\s*[=:]\s*(""?)([^\s""|,;<>]+)")]
        private static partial Regex CountryProduct();

        /// <summary>IPv4 — but not a version quad: <c>version=</c>/<c>quad=</c>/<c>build=</c>/<c>from</c>/<c>to</c> before it, or
        /// the number directly inside parentheses (the <c>"0.2.5 Breaker (0.2.5.6)"</c> stamp), suppress the match.</summary>
        [GeneratedRegex(@"(?<!(?:version|quad|build|from|to)\s*[=:( ]\s*)(?<!\()(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])")]
        private static partial Regex IPv4();

        /// <summary>IPv6, compressed or not. The uncompressed branch needs a hex LETTER so a timestamp or a
        /// <c>seq:tid</c> field never matches; the compressed branch keys off the literal <c>::</c>.</summary>
        [GeneratedRegex(@"(?i)\b(?=(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b)(?=[0-9:]*[a-f])(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b|\b(?:[0-9a-f]{1,4}:){1,6}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,5})?(?:%[0-9a-zA-Z]+)?\b|(?<![\w:])::1\b")]
        private static partial Regex IPv6();

        [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
        private static partial Regex Mac();

        /// <summary><c>deviceId=…</c> / <c>device_id: …</c> followed by 16+ hex characters.</summary>
        [GeneratedRegex(@"(?i)\b(device_?id)\s*[=:]\s*([0-9a-f]{16,})")]
        private static partial Regex DeviceId();

        /// <summary>Every pattern in a fixed order, then the injected literals. <c>Redact(Redact(x), r) == Redact(x, r)</c>.</summary>
        public static string Redact(string text, RedactionRules rules)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string s = ProfilePath().Replace(text, "$1<user>");
            s = ProfileVar().Replace(s, "<user-profile>");
            s = MacProfile().Replace(s, "$1<user>");
            s = UncHost().Replace(s, @"\\<host>$2");

            s = Literal(s, rules.UserName, "<user>");
            s = Literal(s, rules.MachineName, "<machine>");
            s = Literal(s, rules.SpotifyUserId, "<spotify-user>");
            s = Literal(s, rules.DisplayName, "<display-name>");
            if (rules.DeviceNames is { } names)
                foreach (var n in names) s = Literal(s, n, "<device>");

            s = SpotifyUser().Replace(s, "$1<id>");
            s = Email().Replace(s, "<email>");
            s = Bearer().Replace(s, "$1 <token>");
            s = KeyValueSecret().Replace(s, "$1=$2<redacted>");
            s = CountryProduct().Replace(s, "$1=$2<redacted>");
            s = IPv4().Replace(s, "<ip>");
            s = IPv6().Replace(s, "<ip6>");
            s = Mac().Replace(s, "<mac>");
            return DeviceId().Replace(s, "$1=<device-id>");
        }

        /// <summary>A literal name, replaced WORD-WISE and case-insensitively. Substring matching turned reports into
        /// nonsense the moment a name was a common token (a Connect device called "Wavee" redacted every "Wavee"; a display
        /// name that prefixes a GitHub handle rewrote the feed URL), so a hit needs a non-alphanumeric (or the edge) on
        /// both sides; under 3 characters is skipped (a short account name would erase prose), and the app's own name is
        /// never a secret. A manual scan, not a runtime Regex: every pattern in this class is source-generated.
        /// <para>The search cursor and the copy cursor are SEPARATE: 0.2.9 advanced one cursor past an unbounded hit
        /// before any builder existed, and the text before the first bounded hit silently vanished.</para></summary>
        static string Literal(string s, string? value, string placeholder)
        {
            if (value is not { Length: >= 3 } v || string.Equals(v, "Wavee", StringComparison.OrdinalIgnoreCase)) return s;
            int search = 0, copied = 0;
            StringBuilder? sb = null;
            while (true)
            {
                int i = s.IndexOf(v, search, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                int end = i + v.Length;
                if ((i == 0 || !char.IsLetterOrDigit(s[i - 1])) && (end == s.Length || !char.IsLetterOrDigit(s[end])))
                {
                    sb ??= new StringBuilder(s.Length);
                    sb.Append(s, copied, i - copied).Append(placeholder);
                    copied = end;
                }
                search = end;
            }
            if (sb is null) return s;
            sb.Append(s, copied, s.Length - copied);
            return sb.ToString();
        }
    }
}
