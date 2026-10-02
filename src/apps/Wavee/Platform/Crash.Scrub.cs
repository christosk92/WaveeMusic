// ── Platform/Crash.Scrub.cs ────────────────────────────────────────────────────────────────────────────────────────
// The crash & diagnostics pipeline's STRUCTURAL SCRUBBER (WP-C, half 1): turns one captured bundle (summary + report
// text + log tail) into the text a stranger's server — or the in-app consent prompt — is allowed to see.
// Engine-free: no D3D, no window, no process; every rule here is a fact in `Wavee.Tests/CrashScrubTests.cs`.
//
// Role: CORE
// Owner: WP-C
// Wave: crash-diagnostics (post-0.3; independent of the migration waves — nothing here renders)
// Budget: n/a (new file)
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.4, §F ("C · Scrub + upload"), §I
//
// WHY STRUCTURAL, NOT REGEX-ONLY. `Feedback.ReportRedactor.Redact` (Screens/Feedback.cs:584-706) already catches every
// GENERIC secret shape (paths, emails, bearer tokens, IPs, device ids) and every INJECTED literal (the signed-in
// account, the machine name, Connect device names) — this file builds on it (step 5 below runs it last, unmodified)
// but adds what a regex alone cannot: the app log's own `key=value` grammar. A regex that doesn't know a line is
// `nav.route arg=<uri-escaped-id>` cannot tell a track id (safe, and explicitly wanted in the consent copy per B.4
// item 6) from an account id in the SAME shape a field over in the `auth` category. So the log tail is TOKENIZED —
// prefix fields, the level letter, the bracketed category, the event id, then every remaining `key=value` pair — and
// each field's VALUE is allowed through only by an explicit allowlist (by key name, by being purely numeric, or by a
// `…Ms`/`…Bytes`/`frames…` key suffix/prefix); everything else about a line (its prose) passes through untouched to
// step 5's regex pass, which is where a stray path/email/secret embedded in prose still gets caught.
//
// ORDER MATTERS (idempotence: `Scrub(Scrub(x)) == Scrub(x)`, tested directly). Each step below is idempotent on its
// own output, and running them in this order never re-exposes what an earlier step hid:
//   1. tail lines: drop below Info, drop `auth`/`wire`/credential-marker lines whole, field-allowlist every other line
//      (the report text's `module=` and `args=` lines get their own narrower rewrites, §B.4 items 3-4)
//   2. absolute Windows paths (report + tail) → `<path>`, except the three install-tree prefixes
//   3. `Feedback.ReportRedactor.Redact` (report + tail + `Summary.ExceptionMessage` + `Summary.LastRoute`) — the
//      generic backstop, run LAST so nothing it already turned into a placeholder can be re-matched by an earlier,
//      narrower rule (a `<path>` token is not a drive-letter path; a `[line dropped: credential]` line contains none
//      of the credential markers it was dropped for).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Wavee.Feedback;

namespace Wavee;

public static partial class Crash
{
    /// <summary>One bundle after the scrubber has run — what leaves this machine, and what the consent prompt shows
    /// before it does. <see cref="Summary"/> here has its two free-text fields (<c>ExceptionMessage</c>,
    /// <c>LastRoute</c>) already redacted; every other field was already scrubber-safe when WP-B wrote it (§B.2).</summary>
    public sealed record ScrubbedBundle(Summary Summary, string ReportTxt, string TailTxt);

    /// <summary>Structural, engine-free: turns one raw bundle into the text a stranger's server (or the local consent
    /// prompt) is allowed to see. Never throws — a malformed line degrades to "pass it through to the regex pass"
    /// rather than losing the rest of the tail.</summary>
    public static partial class Scrubber
    {
        // ── 0. the injected literals (mirrors Feedback.ReportComposer.Rules — that method is private to Feedback.UI.cs,
        // so this is the SAME gather, not a call to it; both read the identical live seams) ──────────────────────────

        /// <summary>UI thread or a pool thread reading already-published state (never blocks, never awaits): the OS
        /// account + machine name, and — when known — the signed-in account's id/display name and every Connect
        /// device the roster currently holds. Any failure (no session yet, roster not attached) degrades to
        /// <see cref="RedactionRules.None"/> — the built-in patterns alone still redact a great deal.</summary>
        public static RedactionRules RulesNow()
        {
            try
            {
                string? account = null, displayName = null;
                string[]? devices = null;
                if (Platform.Scope.Account is { Length: > 0 } a) account = a;
                var me = User.Me;
                if (me.IsValid && me.Knows(UserFields.Identity)) displayName = Entities.Strings.Resolve(me.NameId);
                var rows = Playback.Devices.Rows;
                if (rows.Length > 0)
                {
                    devices = new string[rows.Length];
                    for (int i = 0; i < rows.Length; i++) devices[i] = rows[i].Name ?? "";
                }
                return new RedactionRules(Environment.UserName, Environment.MachineName, account, displayName, devices);
            }
            catch (Exception)
            {
                return RedactionRules.None;
            }
        }

        // ── 1. the whole-bundle scrub ────────────────────────────────────────────────────────────────────────────────

        public static ScrubbedBundle Scrub(Summary s, string reportTxt, IEnumerable<string> tailLines, RedactionRules rules)
        {
            string report = ScrubReportText(reportTxt ?? "");
            string tail = ScrubTail(tailLines);

            report = ScrubPaths(report);
            tail = ScrubPaths(tail);

            report = ReportRedactor.Redact(report, rules);
            tail = ReportRedactor.Redact(tail, rules);

            // FaultModule (a native crash's faulting module base name, #165) is free text read out of another process's
            // module list: already normalized to [a-z0-9._-] by the handler, but it still goes through the same redactor
            // as every other string a stranger's server will see (a module named after the account is not impossible).
            var scrubbedSummary = s with
            {
                ExceptionMessage = ReportRedactor.Redact(s.ExceptionMessage ?? "", rules),
                LastRoute = ReportRedactor.Redact(s.LastRoute ?? "", rules),
                FaultModule = ReportRedactor.Redact(s.FaultModule ?? "", rules),
            };
            return new ScrubbedBundle(scrubbedSummary, report, tail);
        }

        /// <summary>What the consent prompt / recovery dialog shows before a send: a two-line header, the report text,
        /// then the tail under its own labelled rule, truncated with an ellipsis rather than a hard cut mid-token.</summary>
        public static string Preview(ScrubbedBundle b, int maxChars = 60_000)
        {
            var s = b.Summary;
            int tailLines = CountLines(b.TailTxt);
            var sb = new StringBuilder(b.ReportTxt.Length + b.TailTxt.Length + 256);
            sb.Append("Wavee ").Append(s.Version).Append(" (").Append(s.Quad).Append(") · ")
              .Append(s.Commit).Append(" · ").Append(s.Arch).Append(" · ").Append(s.OsBuild).Append('\n');
            sb.Append("report ").Append(s.ReportId).Append(" · install ").Append(s.InstallId).Append(" · ")
              .Append(s.Gpu).Append(" · ").Append(s.Packaged ? "packaged" : "unpackaged").Append(" · ").Append(s.Locale).Append('\n');
            sb.Append('\n').Append(b.ReportTxt);
            sb.Append("\n\n--- log-tail.txt (").Append(tailLines.ToString(CultureInfo.InvariantCulture))
              .Append(" lines, personal details removed) ---\n").Append(b.TailTxt);
            string full = sb.ToString();
            if (maxChars <= 0) return "";
            return full.Length <= maxChars ? full : string.Concat(full.AsSpan(0, maxChars - 1), "…");
        }

        static int CountLines(string tail)
        {
            if (tail.Length == 0) return 0;
            int n = 1;
            foreach (char c in tail) if (c == '\n') n++;
            if (tail[^1] == '\n') n--;
            return n;
        }

        // ── 2. the report text: the module line and the argv whitelist (§B.4 items 3-4) ────────────────────────────────

        static string ScrubReportText(string report)
        {
            string s = ModuleLine().Replace(report, "module=<install>\\Wavee.exe");
            return ArgsLine().Replace(s, static m => "args=" + FilterArgvLine(m.Groups[1].Value));
        }

        /// <summary>The whitelist §B.4 item 3 names, applied to a space-separated argv string: <c>--fake</c>,
        /// <c>--relaunched-after-update</c> pass through bare; <c>--profile</c> keeps the flag but drops the profile
        /// name that follows it; <c>--crash-probe</c> keeps the flag AND its mode argument. Every other token —
        /// including argv[0]'s own path (already `&lt;path&gt;`-eligible anyway) and every other flag
        /// (<c>--crash-handler</c>, <c>--relaunch-after</c>, <c>--recovery</c>, …) — is dropped outright: nothing about
        /// how THIS launch was invoked leaves the machine except the four shapes a report legitimately needs.</summary>
        public static string FilterArgvLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string[] tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>(tokens.Length);
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                if (t is "--fake" or "--relaunched-after-update") { kept.Add(t); continue; }
                if (t == "--profile")
                {
                    kept.Add(t);
                    if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal)) i++;
                    continue;
                }
                if (t == "--crash-probe")
                {
                    kept.Add(t);
                    if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal)) { kept.Add(tokens[i + 1]); i++; }
                    continue;
                }
                // not whitelisted: dropped silently (no placeholder — an argv line is a list, not a fixed field set).
            }
            return string.Join(' ', kept);
        }

        /// <summary>The module line's PATH, not just its first word: an MSIX install's real path is
        /// <c>C:\Program Files\WindowsApps\…\Wavee.exe</c> — it contains a space, so stopping at <c>\S+</c> would leave
        /// everything after "Program" untouched. The line is always written as <c>module=&lt;path&gt; base=…</c>
        /// (<c>Screens/Diagnostics.Host.cs</c>'s <c>MainModuleLine</c> always appends <c>base=</c>, even as
        /// <c>base=unknown</c>), so a lazy match up to <c> base=</c> — or end of line if that never comes — is the
        /// path, spaces and all.</summary>
        [GeneratedRegex(@"(?m)^module=.*?(?= base=|$)")]
        private static partial Regex ModuleLine();

        [GeneratedRegex(@"(?m)^args=(.*)$")]
        private static partial Regex ArgsLine();

        // ── 3. absolute Windows paths → <path>, everywhere except the three install-tree prefixes (§B.4 item 2) ───────

        static readonly string[] PathExemptions = { @"\WindowsApps\", @"\Program Files", @"\Windows\" };

        static string ScrubPaths(string text)
        {
            string s = DrivePath().Replace(text, static m => IsExemptPath(m.Value) ? m.Value : "<path>");
            return UncPath().Replace(s, static m => IsExemptPath(m.Value) ? m.Value : "<path>");
        }

        static bool IsExemptPath(string path)
        {
            foreach (string exempt in PathExemptions)
                if (path.Contains(exempt, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [GeneratedRegex(@"[A-Za-z]:\\[^\s""'|<>]+")]
        private static partial Regex DrivePath();

        [GeneratedRegex(@"\\\\[^\s""'|<>]+")]
        private static partial Regex UncPath();

        // ── 4. the log tail: parsed, not grepped (§B.4 item 1) ──────────────────────────────────────────────────────

        /// <summary>Every field NAME the value survives under, regardless of value shape. "ts"/"level"/"cat"/"event"
        /// in the plan's prose are this file's <c>t</c>/the level letter/the bracketed category/the event id — none of
        /// those are literal <c>key=value</c> tokens (they are positional), so they are never redacted in the first
        /// place; <c>seq</c>/<c>tid</c>/<c>t</c>/<c>pid</c> are the file-line prefix's own structural counters (never
        /// personal), and <c>op</c>/<c>elapsed</c> are the operation-id/duration pair <see cref="WaveeLogEntry.Format"/>
        /// always writes right after the event id.</summary>
        static readonly HashSet<string> AllowlistedKeys = new(StringComparer.Ordinal)
        {
            "route", "arg", "navId", "state", "code", "status", "reason", "mode", "kind", "source",
            "seq", "tid", "t", "pid", "op", "elapsed",
        };

        /// <summary>The <c>startup</c> event's own fields that are dropped even though nothing in
        /// <see cref="AllowlistedKeys"/> would otherwise catch them turning up there — a name that outlives the
        /// allowlist's exact key set if one is ever widened.</summary>
        static readonly HashSet<string> StartupForceDropped = new(StringComparer.Ordinal) { "account", "credential", "sid" };

        static readonly string[] CredentialMarkers = { "Authorization", "Cookie", "client-token", "set-cookie" };

        static string ScrubTail(IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            foreach (string raw in lines)
            {
                string? scrubbed = ScrubTailLine(raw);
                if (scrubbed is null) continue;
                sb.Append(scrubbed).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>One tail line → the scrubbed line, or null when the WHOLE line is dropped (below Info). A
        /// credential-bearing line survives as a fixed placeholder rather than disappearing silently — a report with a
        /// hole nobody can see is worse than one that says so.</summary>
        public static string? ScrubTailLine(string rawLine)
        {
            if (string.IsNullOrEmpty(rawLine)) return null;
            if (ContainsCredentialMarker(rawLine)) return "[line dropped: credential]";

            var tokens = Tokenize(rawLine);
            int i = 0;
            if (i < tokens.Count && tokens[i].StartsWith("seq=", StringComparison.Ordinal)) i++;
            if (i < tokens.Count && tokens[i].StartsWith("tid=", StringComparison.Ordinal)) i++;
            if (i < tokens.Count && tokens[i].StartsWith("t=", StringComparison.Ordinal)) i++;
            if (i < tokens.Count && tokens[i].StartsWith("sid=", StringComparison.Ordinal)) i++;
            if (i < tokens.Count && tokens[i].StartsWith("pid=", StringComparison.Ordinal)) i++;

            WaveeLogLevel? level = i < tokens.Count ? ParseLevelToken(tokens[i]) : null;
            if (level is not null) i++;
            if (level is WaveeLogLevel.Trace or WaveeLogLevel.Debug) return null;

            string category = "";
            if (i < tokens.Count && tokens[i].Length >= 2 && tokens[i][0] == '[' && tokens[i][^1] == ']')
            { category = tokens[i][1..^1]; i++; }
            if (category.Equals("auth", StringComparison.OrdinalIgnoreCase) || category.Equals("wire", StringComparison.OrdinalIgnoreCase))
                return "[line dropped: credential]";

            string eventId = "";
            if (i < tokens.Count && tokens[i] != "-" && !tokens[i].Contains('='))
                eventId = tokens[i];
            bool startup = eventId == "startup";

            var sb = new StringBuilder(rawLine.Length);
            for (int k = 0; k < tokens.Count; k++)
            {
                if (sb.Length > 0) sb.Append(' ');
                string tok = tokens[k];
                int eq = tok.IndexOf('=');
                if (eq > 0)
                {
                    string key = tok[..eq];
                    string value = tok[(eq + 1)..];
                    bool forceDrop = startup && StartupForceDropped.Contains(key);
                    bool keep = !forceDrop && KeepValue(key, value);
                    sb.Append(keep ? tok : key + "=<dropped>");
                }
                else sb.Append(tok);
            }
            return sb.ToString();
        }

        static bool ContainsCredentialMarker(string line)
        {
            foreach (string marker in CredentialMarkers)
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static WaveeLogLevel? ParseLevelToken(string token) => token switch
        {
            "T" => WaveeLogLevel.Trace, "D" => WaveeLogLevel.Debug, "I" => WaveeLogLevel.Info,
            "W" => WaveeLogLevel.Warning, "E" => WaveeLogLevel.Error, "C" => WaveeLogLevel.Critical,
            _ => null,
        };

        static bool KeepValue(string key, string rawValue)
        {
            if (AllowlistedKeys.Contains(key)) return true;
            if (IsPurelyNumeric(StripQuotes(rawValue))) return true;
            if (key.EndsWith("Ms", StringComparison.Ordinal)) return true;
            if (key.EndsWith("Bytes", StringComparison.Ordinal)) return true;
            if (key.StartsWith("frames", StringComparison.Ordinal)) return true;
            return false;
        }

        static bool IsPurelyNumeric(string v)
        {
            if (v.Length == 0) return false;
            int i = v[0] == '-' ? 1 : 0;
            if (i >= v.Length) return false;
            bool sawDigit = false, sawDot = false;
            for (; i < v.Length; i++)
            {
                char c = v[i];
                if (c is >= '0' and <= '9') { sawDigit = true; continue; }
                if (c == '.' && !sawDot) { sawDot = true; continue; }
                return false;
            }
            return sawDigit;
        }

        static string StripQuotes(string v)
        {
            if (v.Length < 2 || v[0] != '"' || v[^1] != '"') return v;
            string inner = v[1..^1];
            return inner.IndexOf('\\') < 0 ? inner : inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        /// <summary>Whitespace-splits <paramref name="line"/> while keeping a <c>key="quoted value"</c> token whole —
        /// <see cref="WaveeLogField.AppendTo"/>'s own quoting (backslash-escaped <c>"</c>/<c>\</c>) is the only quoting
        /// a line can contain, so this is its exact inverse, not a general shell-lexer.</summary>
        static List<string> Tokenize(string line)
        {
            var tokens = new List<string>();
            int i = 0, n = line.Length;
            while (i < n)
            {
                while (i < n && line[i] == ' ') i++;
                if (i >= n) break;
                var sb = new StringBuilder();
                bool sawEquals = false;
                while (i < n && line[i] != ' ')
                {
                    if (!sawEquals && line[i] == '=')
                    {
                        sb.Append(line[i]); i++; sawEquals = true;
                        if (i < n && line[i] == '"')
                        {
                            sb.Append(line[i]); i++;
                            while (i < n)
                            {
                                char c = line[i];
                                if (c == '\\' && i + 1 < n && (line[i + 1] == '"' || line[i + 1] == '\\'))
                                { sb.Append(c).Append(line[i + 1]); i += 2; continue; }
                                sb.Append(c); i++;
                                if (c == '"') break;
                            }
                        }
                        continue;
                    }
                    sb.Append(line[i]); i++;
                }
                tokens.Add(sb.ToString());
            }
            return tokens;
        }
    }
}
