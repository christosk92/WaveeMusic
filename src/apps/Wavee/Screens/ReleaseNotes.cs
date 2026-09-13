// ── Screens/ReleaseNotes.cs — CORE (owner R, wave 6; plan §2 · the file's own budget is 1,000) ─────────────────────
//
// THE PARSERS, LINKS AND VALIDATION. A DERIVED split of ch 28 §9.5's 1,850-line `ReleaseNotes.cs` budget: the other
// half — the document + index records, and the named partial ch 28 §9.3.6 asks for — is `Screens/ReleaseNotes.Model.cs`.
//
// Spec: ch 28 §9.5 / §9.3.6.
//
// THESE TWO FILES (this one and `ReleaseNotes.Model.cs`) ARE THE EXACT CHERRY-PICKS `Wavee.ReleaseTool.csproj`
// compiles (plan §3.3) — a third partial added to this concern is a build break in that project, not a behavior
// change here.
//
// `AppUpdateToasts` is NOT in this file (A8) — it belongs to `Platform/Notify.cs`.
//
// Ported verbatim from the ten remaining files under `src/apps/_old/Wavee.Core/ReleaseNotes/` (namespace
// `Wavee.Core.ReleaseNotes`, now nested under `Wavee.ReleaseNotes`): `ChangelogParser.cs`, `MarkdownLite.cs`,
// `ReleaseCommits.cs`, `ReleaseNotesRange.cs`, `ReleaseNotesValidation.cs`, `HighlightVisibility.cs`,
// `HighlightCardMetrics.cs`, `HighlightViewerLayout.cs`, `IssueStateBudget.cs`, `IssueStateCache.cs`. Only the
// namespace, the nesting and the usings moved — bodies, comments and doc comments are unchanged.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wavee;

public static partial class ReleaseNotes
{
    // ── ChangelogParser.cs ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One <c>## [version]</c> block of CHANGELOG.md. <see cref="Date"/> is null when the heading carried none and
    /// the literal string <c>"unreleased"</c> while the release has not been dated by the release script yet.</summary>
    public sealed record ChangelogRelease(string Version, string? Date, ReleaseSection[] Sections);

    /// <summary>Keep a Changelog 1.1 (+ our own <c>Known limitations</c> section) → the same
    /// <see cref="ReleaseSection"/>/<see cref="ReleaseItem"/> shapes the JSON document uses, so the release tool can fold a
    /// parsed CHANGELOG straight into <c>whatsnew.json</c>.
    /// <para>Pure and forgiving: unknown headings and stray prose are skipped, never fatal. Item text keeps its
    /// markdown-lite (see <see cref="MarkdownLite"/>) — this parser only takes the block structure apart.</para></summary>
    public static partial class ChangelogParser
    {
        /// <summary>The repository issue/PR references default to when a bullet writes them bare (<c>#412</c>).</summary>
        public const string WaveeRepo = "christosk92/WaveeMusic";

        [GeneratedRegex(@"^## \[(?<v>[^\]]+)\](?:\s*[-–]\s*(?<d>\d{4}-\d{2}-\d{2}|unreleased))?\s*$")]
        private static partial Regex HeadingRegex();

        [GeneratedRegex(@"^### (?<k>Added|Changed|Deprecated|Removed|Fixed|Security|Known limitations)\s*$")]
        private static partial Regex SectionRegex();

        [GeneratedRegex(@"^- (?<text>.+?)(?:\s\((?<refs>(?:[#!]\d+(?:,\s*)?)+)\))?\s*$")]
        private static partial Regex BulletRegex();

        /// <summary>Every dated (or explicitly unreleased) block in the file, in document order.</summary>
        public static IReadOnlyList<ChangelogRelease> Parse(string markdown) => Parse(markdown, WaveeRepo);

        /// <summary>As <see cref="Parse(string)"/>, with the repository that bare <c>#n</c>/<c>!n</c> references belong to.</summary>
        public static IReadOnlyList<ChangelogRelease> Parse(string markdown, string defaultRepo)
        {
            var releases = new List<ChangelogRelease>(8);
            if (string.IsNullOrEmpty(markdown)) return releases;

            string version = "";
            string? date = null;
            bool inRelease = false;
            var sections = new List<ReleaseSection>(8);

            string? kind = null;
            var items = new List<ReleaseItem>(16);
            var bullet = new List<string>(4);          // the current bullet's line fragments, joined by a space

            void FlushBullet()
            {
                if (bullet.Count == 0) return;
                string line = string.Join(' ', bullet);
                bullet.Clear();
                if (kind is not string k) return;
                var m = BulletRegex().Match(line);
                if (!m.Success) return;
                items.Add(BuildItem(k, items.Count, m, defaultRepo));
            }

            void FlushSection()
            {
                FlushBullet();
                if (kind is string k && items.Count > 0) sections.Add(new ReleaseSection { Kind = k, Items = items.ToArray() });
                items.Clear();
                kind = null;
            }

            void FlushRelease()
            {
                FlushSection();
                if (inRelease) releases.Add(new ChangelogRelease(version, date, sections.ToArray()));
                sections.Clear();
                inRelease = false;
            }

            foreach (var raw in SplitLines(markdown))
            {
                string line = raw.TrimEnd();

                var heading = HeadingRegex().Match(line);
                if (heading.Success)
                {
                    FlushRelease();
                    version = heading.Groups["v"].Value;
                    date = heading.Groups["d"].Success ? heading.Groups["d"].Value : null;
                    inRelease = true;
                    continue;
                }

                if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
                {
                    FlushRelease();                                  // some other H1/H2 ends the block
                    continue;
                }

                if (!inRelease) continue;

                var section = SectionRegex().Match(line);
                if (section.Success)
                {
                    FlushSection();
                    kind = KindOf(section.Groups["k"].Value);
                    continue;
                }

                if (line.StartsWith("### ", StringComparison.Ordinal)) { FlushSection(); continue; }   // unknown heading

                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    FlushBullet();
                    bullet.Add("- " + line[2..].TrimStart());
                    continue;
                }

                if (bullet.Count > 0)
                {
                    if (line.Length == 0) { FlushBullet(); continue; }
                    if (char.IsWhiteSpace(raw[0])) { bullet.Add(line.Trim()); continue; }   // indented continuation
                    FlushBullet();
                }
            }

            FlushRelease();
            return releases;
        }

        /// <summary>The block for one version, or null.</summary>
        public static ChangelogRelease? Find(string markdown, string version) => Find(markdown, version, WaveeRepo);

        /// <summary>The block for one version, or null.</summary>
        public static ChangelogRelease? Find(string markdown, string version, string defaultRepo)
        {
            foreach (var r in Parse(markdown, defaultRepo))
                if (string.Equals(r.Version, version, StringComparison.Ordinal)) return r;
            return null;
        }

        static ReleaseItem BuildItem(string kind, int index, Match m, string defaultRepo)
        {
            var issues = new List<ReleaseIssue>(2);
            var prs = new List<ReleasePr>(2);
            if (m.Groups["refs"].Success)
            {
                foreach (var token in m.Groups["refs"].Value.Split(','))
                {
                    string t = token.Trim();
                    if (t.Length < 2) continue;
                    if (!int.TryParse(t.AsSpan(1), System.Globalization.NumberStyles.None,
                                      System.Globalization.CultureInfo.InvariantCulture, out int n)) continue;
                    if (t[0] == '#') issues.Add(new ReleaseIssue { Repo = defaultRepo, Number = n });
                    else if (t[0] == '!') prs.Add(new ReleasePr { Repo = defaultRepo, Number = n });
                }
            }

            string text = m.Groups["text"].Value.Trim();
            string? scope = TakeScope(ref text);

            return new ReleaseItem
            {
                Id = kind + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Scope = scope,
                Text = text,
                Issues = issues.ToArray(),
                Prs = prs.ToArray(),
            };
        }

        /// <summary>Splits an optional leading scope label off a bullet: <c>**Player:** …</c>, <c>**Player**: …</c> or
        /// <c>Player: …</c>. A scope is 1-16 characters of letters and spaces starting with a capital — anything longer, or
        /// carrying punctuation, is ordinary prose and stays in the text (which is why a bullet that merely opens with bold
        /// emphasis, <c>**Developer mode** — …</c>, keeps its markdown intact).</summary>
        static string? TakeScope(ref string text)
        {
            if (TryScope(text, "**", ":**", out string? a, out int lenA)) { text = text[lenA..].TrimStart(); return a; }
            if (TryScope(text, "**", "**:", out string? b, out int lenB)) { text = text[lenB..].TrimStart(); return b; }
            if (TryScope(text, "", ":", out string? c, out int lenC)) { text = text[lenC..].TrimStart(); return c; }
            return null;
        }

        static bool TryScope(string text, string open, string close, out string? scope, out int consumed)
        {
            scope = null; consumed = 0;
            if (!text.StartsWith(open, StringComparison.Ordinal)) return false;

            int i = open.Length;
            if (i >= text.Length || !char.IsAsciiLetterUpper(text[i])) return false;

            int j = i;
            while (j < text.Length && j - i < 16 && (char.IsAsciiLetter(text[j]) || text[j] == ' ')) j++;
            if (j == i) return false;
            if (!text.AsSpan(j).StartsWith(close, StringComparison.Ordinal)) return false;

            int after = j + close.Length;
            if (after >= text.Length || text[after] != ' ') return false;    // "Scope:" must actually label something

            scope = text[i..j].Trim();
            consumed = after;
            return scope.Length > 0;
        }

        static string KindOf(string heading) => heading switch
        {
            "Added" => "added",
            "Changed" => "changed",
            "Deprecated" => "deprecated",
            "Removed" => "removed",
            "Fixed" => "fixed",
            "Security" => "security",
            "Known limitations" => "known",
            _ => "changed",
        };

        static IEnumerable<string> SplitLines(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                yield return text[start..end];
                start = i + 1;
            }
            if (start < text.Length) yield return text[start..].TrimEnd('\r');
        }
    }

    // ── MarkdownLite.cs ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What one inline run of release-notes text is.</summary>
    public enum InlineKind { Text, Bold, Code, Link, Issue, Pr, Mention, Url }

    /// <summary>One inline run. <see cref="Text"/> is always what the UI DISPLAYS; the actionable half lives in
    /// <see cref="Target"/> (link/url href, or a mention's bare login) and <see cref="Number"/> + <see cref="Repo"/>
    /// (issue/PR references — <see cref="Repo"/> is null when the reference was bare, e.g. <c>#123</c>).</summary>
    public readonly record struct InlineToken(InlineKind Kind, string Text, string? Target = null, int Number = 0, string? Repo = null, bool Bold = false);

    /// <summary>The inline markdown subset release notes are allowed to use. Pure, and deliberately tiny: there is no block
    /// structure here (the sections come from <see cref="ChangelogParser"/> or the JSON), no headings, no images, no HTML.
    /// <para>Never throws. Unbalanced or unknown syntax falls through as literal text, which is the only sane behaviour for
    /// content that is authored by hand and rendered on a page the user cannot fix.</para></summary>
    public static class MarkdownLite
    {
        /// <summary><c>**bold**</c>, <c>*em*</c> (rendered as weight), <c>`code`</c>, <c>[text](url)</c>, bare http(s) URLs,
        /// <c>#123</c>, <c>owner/repo#123</c>, <c>!123</c>, <c>@handle</c>, and backslash escapes.</summary>
        public static InlineToken[] Tokenize(string s)
        {
            if (string.IsNullOrEmpty(s)) return [];

            var outp = new List<InlineToken>(8);
            var buf = new StringBuilder(s.Length);

            void Flush()
            {
                if (buf.Length > 0) { outp.Add(new InlineToken(InlineKind.Text, buf.ToString())); buf.Clear(); }
            }

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                if (c == '\\' && i + 1 < s.Length) { buf.Append(s[++i]); continue; }

                if (c == '`')
                {
                    int e = s.IndexOf('`', i + 1);
                    if (e > i) { Flush(); outp.Add(new InlineToken(InlineKind.Code, s[(i + 1)..e])); i = e; continue; }
                }

                if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int e = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (e > i) { Flush(); AddBold(outp, s[(i + 2)..e]); i = e + 1; continue; }
                }

                if (c == '*')
                {
                    int e = s.IndexOf('*', i + 1);
                    if (e > i + 1) { Flush(); AddBold(outp, s[(i + 1)..e]); i = e; continue; }
                }

                if (c == '[')
                {
                    int close = s.IndexOf("](", i, StringComparison.Ordinal);
                    int end = close > 0 ? s.IndexOf(')', close) : -1;
                    if (end > close)
                    {
                        Flush();
                        outp.Add(new InlineToken(InlineKind.Link, s[(i + 1)..close], s[(close + 2)..end]));
                        i = end;
                        continue;
                    }
                }

                if ((c == '#' || c == '!') && TryRef(s, i, buf, out int n, out int len, out int backLen))
                {
                    // owner/repo#123: the repo prefix was already buffered as ordinary text — take it back off the tail so
                    // it renders as part of the reference chip and not as stray text in front of it.
                    string? repo = null;
                    if (backLen > 0)
                    {
                        repo = s.Substring(i - backLen, backLen);
                        buf.Length -= backLen;
                    }
                    Flush();
                    outp.Add(new InlineToken(c == '#' ? InlineKind.Issue : InlineKind.Pr,
                                             (repo ?? "") + s.Substring(i, len), null, n, repo));
                    i += len - 1;
                    continue;
                }

                if (c == '@' && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && TryHandle(s, i, out string h))
                {
                    Flush();
                    outp.Add(new InlineToken(InlineKind.Mention, "@" + h, h));
                    i += h.Length;
                    continue;
                }

                if (c == 'h' && (s.AsSpan(i).StartsWith("https://", StringComparison.Ordinal) ||
                                 s.AsSpan(i).StartsWith("http://", StringComparison.Ordinal)))
                {
                    int e = i;
                    while (e < s.Length && !char.IsWhiteSpace(s[e]) && s[e] != ')') e++;
                    while (e - 1 > i && (s[e - 1] == '.' || s[e - 1] == ',')) e--;   // trailing sentence punctuation is not the URL
                    Flush();
                    string url = s[i..e];
                    outp.Add(new InlineToken(InlineKind.Url, url, url));
                    i = e - 1;
                    continue;
                }

                buf.Append(c);
            }

            Flush();
            return outp.ToArray();
        }

        /// <summary><c>#123</c> / <c>!123</c>, optionally prefixed by <c>owner/repo</c> immediately before the marker.
        /// A bare marker only counts at the start of the text or after whitespace, <c>(</c> or <c>,</c> — so "Wow!" and
        /// "C#" stay text. <paramref name="backLen"/> is how many characters BEFORE <paramref name="i"/> the repo prefix
        /// occupies (0 when there is none); those characters are already in <paramref name="buffered"/>.</summary>
        /// <summary>A bold run may itself carry `code`, links or refs ("**dismissed with `Esc`,**" is the shipping
        /// changelog's own shape): tokenize the inside and mark every run bold instead of emitting it verbatim.</summary>
        static void AddBold(List<InlineToken> outp, string inner)
        {
            foreach (var t in Tokenize(inner))
                outp.Add(t.Kind == InlineKind.Text ? new InlineToken(InlineKind.Bold, t.Text) : t with { Bold = true });
        }

        static bool TryRef(string s, int i, StringBuilder buffered, out int number, out int len, out int backLen)
        {
            number = 0; len = 0; backLen = 0;

            int j = i + 1;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            int digits = j - (i + 1);
            if (digits is 0 or > 9) return false;                                  // no digits, or too many to be an issue
            if (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) return false;   // "#1abc" is not a reference
            if (!int.TryParse(s.AsSpan(i + 1, digits), System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out number)) return false;
            len = 1 + digits;

            // owner/repo immediately before the marker?
            int start = i;
            while (start > 0 && IsRepoChar(s[start - 1])) start--;
            int prefix = i - start;
            if (prefix > 0 && IsRepoSlug(s.AsSpan(start, prefix)) && prefix <= buffered.Length && EndsWith(buffered, s, start, prefix))
            {
                backLen = prefix;
                return true;
            }

            return i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t' || s[i - 1] == '(' || s[i - 1] == ',';
        }

        static bool IsRepoChar(char c)
            => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '/';

        /// <summary><c>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+</c> — exactly one slash, neither side empty.</summary>
        static bool IsRepoSlug(ReadOnlySpan<char> s)
        {
            int slash = -1;
            for (int k = 0; k < s.Length; k++)
            {
                if (s[k] == '/')
                {
                    if (slash >= 0) return false;
                    slash = k;
                }
            }
            return slash > 0 && slash < s.Length - 1;
        }

        /// <summary>Guards the take-back: the prefix must actually be the tail of the pending text run (it is not when a
        /// token boundary — a code span, bold, an escape — fell inside it).</summary>
        static bool EndsWith(StringBuilder buffered, string s, int start, int count)
        {
            int b = buffered.Length - count;
            for (int k = 0; k < count; k++)
                if (buffered[b + k] != s[start + k]) return false;
            return true;
        }

        /// <summary><c>@handle</c> — GitHub logins are 1-39 of <c>[A-Za-z0-9-]</c>. Returns the login WITHOUT the '@'.</summary>
        static bool TryHandle(string s, int i, out string handle)
        {
            handle = "";
            int j = i + 1;
            while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '-')) j++;
            int n = j - (i + 1);
            if (n is 0 or > 39) return false;
            handle = s.Substring(i + 1, n);
            return true;
        }
    }

    // ── ReleaseCommits.cs ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cross-checks the CHANGELOG entry's <c>(#n)</c> refs against the commits actually shipped in
    /// <c>&lt;prevTag&gt;..HEAD</c> (git closing keywords are the source of truth for "this fixed an issue"; the
    /// CHANGELOG is the source of truth for "this shipped in this release"), and wires the two together for
    /// rendering (<see cref="ReleaseNotesValidation.RenderBody"/>).
    /// </summary>
    public static class ReleaseCommits
    {
        /// <summary><c>commits.json</c> (the shape <c>ConvertFrom-GitLogRecords</c> / <c>Write-ReleaseCommitsJson</c>
        /// write) → <see cref="ReleaseCommit"/>[]. Repairs null members, derives <see cref="ReleaseCommit.Short"/>
        /// from <see cref="ReleaseCommit.Sha"/> when empty, and drops null array elements.</summary>
        /// <exception cref="JsonException">The document is not a JSON array, or a commit has no <c>sha</c>.</exception>
        public static ReleaseCommit[] Parse(ReadOnlySpan<byte> json)
        {
            var commits = JsonSerializer.Deserialize(json, ReleaseNotesJsonContext.Default.ReleaseCommitArray)
                ?? throw new JsonException("commits.json is not an array");

            var list = new List<ReleaseCommit>(commits.Length);
            foreach (var c in commits)
            {
                if (c is null) continue;
                if (string.IsNullOrEmpty(c.Sha)) throw new JsonException("a commit in commits.json has no sha");
                c.Short = string.IsNullOrEmpty(c.Short) ? c.Sha[..Math.Min(7, c.Sha.Length)] : c.Short;
                c.Subject ??= "";
                c.Issues ??= [];
                c.Prs ??= [];
                list.Add(c);
            }
            return list.ToArray();
        }

        /// <summary>
        /// ALWAYS attaches <paramref name="commits"/> to every section item whose <c>#n</c>/<c>!n</c> refs intersect
        /// the commit's issues∪prs (dedup by sha), fills <see cref="ReleaseNotesDocument.UnlinkedCommits"/> (commits
        /// matching no item, in input order), and returns the mismatches between the CHANGELOG entry's refs and the
        /// commits' closing keywords — one line per issue number, ascending:
        /// <code>
        /// issue #{n} is fixed by {short} "{subject}" but the CHANGELOG [{doc.Version}] entry does not cite it
        /// CHANGELOG cites #{n} but no commit in {range} carries "Fixes #{n}"
        /// </code>
        /// Only closing-keyword issues (<see cref="ReleaseCommit.Issues"/>) can be "missing in the changelog"; a
        /// CHANGELOG <c>#n</c> is satisfied by a commit whose <see cref="ReleaseCommit.Issues"/> OR
        /// <see cref="ReleaseCommit.Prs"/> contain <c>n</c>. Issue refs of another repo
        /// (<see cref="ReleaseIssue.Repo"/> set and different from <paramref name="repo"/>, case-insensitive) are
        /// ignored. Calls <see cref="ReleaseNotesDocument.Normalize"/> first. Errors are returned even when attaching
        /// (so <c>--allow-unlinked</c> can ship what it has).
        /// </summary>
        public static IReadOnlyList<string> Link(
            ReleaseNotesDocument doc, IReadOnlyList<ReleaseCommit> commits, string repo, string range)
        {
            ArgumentNullException.ThrowIfNull(doc);
            ArgumentNullException.ThrowIfNull(commits);
            doc.Normalize();

            // Every item's own ref numbers (issues, repo-filtered, ∪ PRs), and which items own a given ref number.
            var itemsByRef = new Dictionary<int, List<ReleaseItem>>();
            var changelogIssues = new SortedSet<int>();
            foreach (var section in doc.Sections)
            {
                foreach (var item in section.Items)
                {
                    var refs = new HashSet<int>();
                    foreach (var i in item.Issues)
                    {
                        if (i.Repo.Length > 0 && !string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase))
                            continue;
                        refs.Add(i.Number);
                        changelogIssues.Add(i.Number);
                    }
                    foreach (var p in item.Prs) refs.Add(p.Number);

                    foreach (var n in refs)
                    {
                        if (!itemsByRef.TryGetValue(n, out var list)) itemsByRef[n] = list = [];
                        list.Add(item);
                    }
                }
            }

            // Attach: for each commit, find every item whose refs intersect this commit's issues∪prs.
            var itemCommits = new Dictionary<ReleaseItem, (HashSet<string> Shas, List<ReleaseCommit> Commits)>();
            var unlinked = new List<ReleaseCommit>();
            var refSatisfied = new HashSet<int>();
            var fixedBy = new Dictionary<int, List<ReleaseCommit>>();

            foreach (var c in commits)
            {
                var commitRefs = new HashSet<int>();
                foreach (var n in c.Issues)
                {
                    commitRefs.Add(n);
                    refSatisfied.Add(n);
                    if (!fixedBy.TryGetValue(n, out var fixers)) fixedBy[n] = fixers = [];
                    fixers.Add(c);
                }
                foreach (var n in c.Prs)
                {
                    commitRefs.Add(n);
                    refSatisfied.Add(n);
                }

                var touched = new HashSet<ReleaseItem>();
                foreach (var n in commitRefs)
                    if (itemsByRef.TryGetValue(n, out var list))
                        foreach (var item in list) touched.Add(item);

                foreach (var item in touched)
                {
                    if (!itemCommits.TryGetValue(item, out var bucket))
                        itemCommits[item] = bucket = ([], []);
                    if (bucket.Shas.Add(c.Sha)) bucket.Commits.Add(c);
                }
                if (touched.Count == 0) unlinked.Add(c);
            }

            foreach (var section in doc.Sections)
                foreach (var item in section.Items)
                    item.Commits = itemCommits.TryGetValue(item, out var bucket) ? [.. bucket.Commits] : [];
            doc.UnlinkedCommits = [.. unlinked];

            var mismatches = new List<(int Issue, string Message)>();
            foreach (var (n, fixers) in fixedBy)
            {
                if (changelogIssues.Contains(n)) continue;
                var first = fixers[0];
                mismatches.Add((n,
                    $"issue #{n} is fixed by {first.Short} \"{first.Subject}\" but the CHANGELOG [{doc.Version}] entry does not cite it"));
            }
            foreach (var n in changelogIssues)
            {
                if (refSatisfied.Contains(n)) continue;
                mismatches.Add((n, $"CHANGELOG cites #{n} but no commit in {range} carries \"Fixes #{n}\""));
            }
            mismatches.Sort((a, b) => a.Issue.CompareTo(b.Issue));

            var errors = new List<string>(mismatches.Count);
            foreach (var m in mismatches) errors.Add(m.Message);
            return errors;
        }
    }

    // ── ReleaseNotesRange.cs ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Since you last looked": which released versions the user has not seen yet.
    /// <para>Pure. The range is half-open at the bottom and closed at the top — <c>(lastSeen, current]</c> — so the version
    /// that is running is always in the stack and the one already read never is. Newest first, because that is the order the
    /// page stacks them in.</para></summary>
    public static class ReleaseNotesRange
    {
        /// <param name="lastSeenSemver">What the user last read: a semver ("0.2.1") or an MSIX quad ("0.2.1.17"); empty or
        /// unparsable means "nothing" and yields just the current release.</param>
        /// <param name="currentSemver">The release being shown: a semver or a quad; it is looked up in the index and, when
        /// it is not there, nothing is returned (an index that does not know the release cannot describe a range to it).</param>
        /// <param name="index">The rolling index. Null/empty yields nothing.</param>
        /// <param name="channel">"beta" lets pre-release entries into the stack; anything else filters them out.</param>
        public static ReleaseNotesIndexEntry[] Between(string lastSeenSemver, string currentSemver, ReleaseNotesIndex index, string channel)
        {
            if (index is null) return [];
            var releases = index.Releases;
            if (releases is not { Length: > 0 }) return [];
            if (index.Find(currentSemver) is not { } current) return [];

            bool beta = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase);
            var currentKey = Key(current.Version);

            if (string.IsNullOrWhiteSpace(lastSeenSemver) || !TryKey(lastSeenSemver, out var lastKey))
                return [current];

            var picked = new List<ReleaseNotesIndexEntry>(4);
            foreach (var e in releases)
            {
                if (e is null) continue;
                if (ReferenceEquals(e, current)) continue;
                if (IsPrerelease(e) && !beta) continue;
                if (!TryKey(e.Version, out var key)) continue;
                if (Compare(key, lastKey) <= 0) continue;            // already seen
                if (Compare(key, currentKey) >= 0) continue;         // at or beyond the release being shown
                picked.Add(e);
            }

            picked.Add(current);
            picked.Sort(static (a, b) => Compare(Key(b.Version), Key(a.Version)));
            return picked.ToArray();
        }

        static bool IsPrerelease(ReleaseNotesIndexEntry e)
            => string.Equals(e.Channel, "beta", StringComparison.OrdinalIgnoreCase) || e.Version.IndexOf('-') > 0;

        // ── version keys: four numeric parts + the pre-release ordinal ───────────────────────────────────────────────
        // A release sorts AFTER every pre-release of the same core (0.4.0-beta.2 < 0.4.0), which is why the release's beta
        // ordinal is int.MaxValue. Missing numeric parts are 0, so "0.2.1" and "0.2.1.0" are the same version.

        readonly record struct VersionKey(int A, int B, int C, int D, int Beta);

        static VersionKey Key(string version) => TryKey(version, out var k) ? k : default;

        static bool TryKey(string version, out VersionKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(version)) return false;

            string s = version.Trim();
            if (s[0] is 'v' or 'V') s = s[1..];
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];

            int beta = int.MaxValue;
            int dash = s.IndexOf('-');
            if (dash >= 0)
            {
                var pre = s.AsSpan(dash + 1);
                beta = 0;
                if (pre.StartsWith("beta.", StringComparison.Ordinal) &&
                    int.TryParse(pre[5..], System.Globalization.NumberStyles.None,
                                 System.Globalization.CultureInfo.InvariantCulture, out int n)) beta = n;
                s = s[..dash];
            }

            Span<int> parts = stackalloc int[4];
            int index = 0, start = 0;
            while (true)
            {
                int dot = s.IndexOf('.', start);
                int end = dot < 0 ? s.Length : dot;
                if (end == start || index >= 4) return false;
                if (!int.TryParse(s.AsSpan(start, end - start), System.Globalization.NumberStyles.None,
                                  System.Globalization.CultureInfo.InvariantCulture, out int value)) return false;
                parts[index++] = value;
                if (dot < 0) break;
                start = dot + 1;
            }
            if (index == 0) return false;

            key = new VersionKey(parts[0], parts[1], parts[2], parts[3], beta);
            return true;
        }

        static int Compare(VersionKey x, VersionKey y)
        {
            int c = x.A.CompareTo(y.A); if (c != 0) return c;
            c = x.B.CompareTo(y.B); if (c != 0) return c;
            c = x.C.CompareTo(y.C); if (c != 0) return c;
            c = x.D.CompareTo(y.D); if (c != 0) return c;
            return x.Beta.CompareTo(y.Beta);
        }
    }

    // ── ReleaseNotesValidation.cs ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pure, engine-free half of the release-notes pipeline: media budget rules, the deep-link rule, the
    /// index merge, the GitHub release body, and the store-listing text.
    /// </summary>
    /// <remarks>
    /// This lives in <c>Wavee.Core</c> (not in the release tool) for one reason: it is the part worth unit-testing.
    /// <c>Wavee.ReleaseTool</c>'s <c>Validator</c> is a thin shell that reads files, calls the GitHub API and calls
    /// into here. Nothing in here touches the network, the clock, or the engine — only the file system, and only for
    /// sizes/hashes/copies of media the caller names.
    /// </remarks>
    public static class ReleaseNotesValidation
    {
        /// <summary>Per-file cap for a still (webp/png/jpg).</summary>
        public const long MaxStillBytes = 150_000;

        /// <summary>Per-file cap for motion (mp4, or a webp declared <c>kind: "video"</c>).</summary>
        public const long MaxMotionBytes = 600_000;

        /// <summary>Cap on every media file of one release added together.</summary>
        public const long MaxTotalBytes = 1_500_000;

        /// <summary>How many releases <c>whatsnew-index.json</c> carries (newest first).</summary>
        public const int MaxIndexEntries = 12;

        /// <summary>Hard cap on <c>store-listing.txt</c>.</summary>
        public const int StoreListingMaxChars = 1500;

        /// <summary>Every highlight deep link must be an in-app route, not an arbitrary URI.</summary>
        public const string DeepLinkPrefix = "wavee://open?route=";

        /// <summary>The tag a release is published under; also the folder its assets hang off.</summary>
        public const string TagPrefix = "wavee-v";

        static readonly string[] AllowedExtensions = [".webp", ".png", ".jpg", ".jpeg", ".mp4"];

        /// <summary>One media file referenced by the document, and whether it is billed against the motion budget.</summary>
        public readonly record struct ReleaseMediaEntry(string Src, bool Motion);

        /// <summary>`.mp4`, or anything a highlight declares as <c>kind: "video"</c> (an animated webp).</summary>
        public static bool IsMotionMedia(string? kind, string src)
            => string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase)
               || src.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

        /// <summary>The per-file budget an entry is measured against.</summary>
        public static long MaxBytesFor(bool motion) => motion ? MaxMotionBytes : MaxStillBytes;

        /// <summary>
        /// Every media source the document references, in document order, de-duplicated by path. A poster is always
        /// billed as a still even when its highlight is a video.
        /// </summary>
        public static IReadOnlyList<ReleaseMediaEntry> MediaEntries(ReleaseNotesDocument doc)
        {
            // The document is hand-authored JSON: an explicit "highlights": null (or a null element inside the array) is
            // legal on the wire and would NRE the enumeration below. Normalize is the ONE owner of that repair and is
            // idempotent, so calling it at each public entry point costs nothing on an already-clean document.
            ArgumentNullException.ThrowIfNull(doc);
            doc.Normalize();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<ReleaseMediaEntry>();
            foreach (var h in doc.Highlights)
            {
                if (h.Media is not { } m) continue;
                if (!string.IsNullOrWhiteSpace(m.Src) && seen.Add(m.Src))
                    list.Add(new ReleaseMediaEntry(m.Src, IsMotionMedia(m.Kind, m.Src)));
                if (!string.IsNullOrWhiteSpace(m.Poster) && seen.Add(m.Poster!))
                    list.Add(new ReleaseMediaEntry(m.Poster!, false));
            }
            return list;
        }

        /// <summary>The one folder a media reference may name. <see cref="CopyMedia"/> FLATTENS every reference into
        /// <c>&lt;out&gt;/media/&lt;basename&gt;</c>, and the app resolves a reference verbatim under the notes root
        /// (<c>ReleaseNotesStore.MediaPath</c> → <c>&lt;embeddedRoot&gt;/media/&lt;basename&gt;</c>), so those two only
        /// agree when the document says <c>media/&lt;basename&gt;</c> and nothing else.</summary>
        public const string MediaFolder = "media";

        /// <summary>Is <paramref name="src"/> exactly <c>media/&lt;basename&gt;</c> — the one shape that survives the
        /// publish? A bare basename, a second folder, a nested path or a backslash separator all fail.</summary>
        public static bool IsPublishableMediaPath(string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return false;
            int slash = src.IndexOf('/');
            if (slash <= 0 || slash == src.Length - 1) return false;               // no folder, or nothing after it
            if (!src.AsSpan(0, slash).SequenceEqual(MediaFolder)) return false;    // some other folder
            var name = src.AsSpan(slash + 1);
            return name.IndexOf('/') < 0 && name.IndexOf('\\') < 0;               // exactly one level deep
        }

        /// <summary>
        /// The media rules: every reference is exactly <c>media/&lt;basename&gt;</c> and resolves inside
        /// <paramref name="notesDir"/>, no GIF, only webp/png/jpg/mp4, ≤150 KB per still, ≤600 KB per motion file,
        /// ≤1.5 MB in total, and no two distinct sources sharing a basename (release assets are flat, so a shared
        /// basename would collide).
        /// </summary>
        /// <returns>Human-readable errors; empty when the document passes.</returns>
        public static IReadOnlyList<string> ValidateMedia(ReleaseNotesDocument doc, string notesDir)
        {
            var errors = new List<string>();
            var basenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;

            foreach (var (src, motion) in MediaEntries(doc))
            {
                if (!IsSafeRelativePath(src))
                {
                    errors.Add($"media path must be relative and stay inside the notes folder: {src}");
                    continue;
                }

                // Not a `continue`: a document that puts its files in the wrong folder should still hear about the size,
                // type and basename problems in the same run rather than one rule per fix-and-rerun cycle.
                if (!IsPublishableMediaPath(src))
                    errors.Add($"media must be referenced as '{MediaFolder}/{Path.GetFileName(src)}', not '{src}' — "
                        + $"publishing FLATTENS every reference into <release>/{MediaFolder}/<basename> and the app resolves "
                        + $"it back as <notes root>/{MediaFolder}/<basename>, so any other folder (or a bare file name) is a "
                        + "poster that validates here and then renders as an empty band on the user's machine");

                string ext = Path.GetExtension(src);
                if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"GIF is not allowed (use webp or mp4): {src}");
                    continue;
                }
                if (Array.FindIndex(AllowedExtensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)) < 0)
                {
                    errors.Add($"unsupported media type '{ext}' (allowed: {string.Join(", ", AllowedExtensions)}): {src}");
                    continue;
                }

                string full = Path.Combine(notesDir, src);
                if (!File.Exists(full))
                {
                    errors.Add($"missing media file: {src}");
                    continue;
                }

                string basename = Path.GetFileName(src);
                if (basenames.TryGetValue(basename, out string? other))
                    errors.Add($"duplicate media basename '{basename}': {other} and {src}");
                else
                    basenames[basename] = src;

                long len = new FileInfo(full).Length;
                total += len;
                long cap = MaxBytesFor(motion);
                if (len > cap)
                    errors.Add($"media too large: {src} is {len} bytes, cap is {cap} ({(motion ? "motion" : "still")})");
            }

            if (total > MaxTotalBytes)
                errors.Add($"media total {total} bytes exceeds the {MaxTotalBytes} byte budget");
            return errors;
        }

        /// <summary>Highlight deep links must open an in-app route: <c>wavee://open?route=…</c>.</summary>
        public static IReadOnlyList<string> ValidateDeepLinks(ReleaseNotesDocument doc)
        {
            ArgumentNullException.ThrowIfNull(doc);
            doc.Normalize();
            var errors = new List<string>();
            foreach (var h in doc.Highlights)
            {
                if (h.DeepLink is not { Length: > 0 } dl) continue;
                if (!dl.StartsWith(DeepLinkPrefix, StringComparison.Ordinal))
                    errors.Add($"deep link must start with '{DeepLinkPrefix}': {dl}");
                else if (dl.Length == DeepLinkPrefix.Length)
                    errors.Add($"deep link names no route: {dl}");
            }
            return errors;
        }

        /// <summary>Size + SHA-256 for every media file, in document order. Missing files are skipped (validation reports them).</summary>
        public static ReleaseMedia[] MediaHashes(ReleaseNotesDocument doc, string notesDir)
        {
            var entries = MediaEntries(doc);
            var list = new List<ReleaseMedia>(entries.Count);
            foreach (var (src, _) in entries)
            {
                if (!IsSafeRelativePath(src)) continue;
                string full = Path.Combine(notesDir, src);
                if (!File.Exists(full)) continue;
                using var fs = File.OpenRead(full);
                byte[] hash = SHA256.HashData(fs);
                list.Add(new ReleaseMedia { Src = src, Bytes = new FileInfo(full).Length, Sha256 = Convert.ToHexStringLower(hash) });
            }
            return list.ToArray();
        }

        /// <summary>Copies every referenced media file into <c>&lt;outDir&gt;/media/</c> (flat, by basename).</summary>
        /// <returns>The number of files copied.</returns>
        public static int CopyMedia(ReleaseNotesDocument doc, string notesDir, string outDir)
        {
            int copied = 0;
            foreach (var (src, _) in MediaEntries(doc))
            {
                if (!IsSafeRelativePath(src)) continue;
                string full = Path.Combine(notesDir, src);
                if (!File.Exists(full)) continue;
                string dest = Path.Combine(outDir, "media", Path.GetFileName(src));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(full, dest, overwrite: true);
                copied++;
            }
            return copied;
        }

        /// <summary>
        /// This release prepended to the previous index, newest first, capped at <see cref="MaxIndexEntries"/>.
        /// An existing entry for the same version is replaced, not duplicated.
        /// </summary>
        public static ReleaseNotesIndex MergeIndex(ReleaseNotesIndex? previous, ReleaseNotesDocument doc)
        {
            var head = new ReleaseNotesIndexEntry
            {
                Version = doc.Version,
                PackageVersion = doc.PackageVersion,
                Name = doc.Name,
                Date = doc.Date,
                Channel = doc.Channel,
                Issues = IssueNumbers(doc),
            };
            var list = new List<ReleaseNotesIndexEntry>(MaxIndexEntries) { head };
            if (previous?.Releases is { } prev)
            {
                foreach (var e in prev)
                {
                    if (list.Count >= MaxIndexEntries) break;
                    if (string.Equals(e.Version, doc.Version, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(e);
                }
            }
            return new ReleaseNotesIndex
            {
                Schema = 1,
                Product = string.IsNullOrWhiteSpace(doc.Product) ? "wavee" : doc.Product,
                Releases = list.ToArray(),
            };
        }

        /// <summary>Section heading for a Keep-a-Changelog kind (<c>known</c> → "Known limitations").</summary>
        public static string SectionTitle(string kind) => kind switch
        {
            "added" => "Added",
            "changed" => "Changed",
            "deprecated" => "Deprecated",
            "removed" => "Removed",
            "fixed" => "Fixed",
            "security" => "Security",
            "known" => "Known limitations",
            _ => kind.Length == 0 ? "Other" : char.ToUpperInvariant(kind[0]) + kind[1..],
        };

        /// <summary>Badge word for a highlight kind (<c>new|improved|rebuilt</c>).</summary>
        public static string HighlightKindLabel(string kind) => kind switch
        {
            "new" => "New",
            "improved" => "Improved",
            "rebuilt" => "Rebuilt",
            _ => kind.Length == 0 ? "New" : char.ToUpperInvariant(kind[0]) + kind[1..],
        };

        /// <summary>The public URL of a media file once it is a release asset of this version's tag.</summary>
        public static string MediaAssetUrl(string repo, string version, string src)
            => $"https://github.com/{repo}/releases/download/{TagPrefix}{version}/{Path.GetFileName(src)}";

        /// <summary>
        /// The GitHub release body: title, tagline, notices as GitHub alerts, highlights (with media linked to this
        /// release's assets), the changelog sections with <c>#n</c> autolinks, a footer, and — when
        /// <paramref name="generatedNotes"/> is supplied (<c>POST /releases/generate-notes</c>) — a folded appendix.
        /// </summary>
        public static string RenderBody(ReleaseNotesDocument doc, string repo, string? generatedNotes)
        {
            ArgumentNullException.ThrowIfNull(doc);
            doc.Normalize();
            var sb = new StringBuilder(4096);
            sb.Append("# Wavee ").Append(doc.Version);
            if (doc.Name.Length > 0) sb.Append(" — ").Append(doc.Name);
            sb.Append("\n\n");

            if (doc.Tagline.Length > 0) sb.Append(doc.Tagline).Append("\n\n");

            foreach (var n in doc.Notices)
            {
                if (n.Text.Length == 0) continue;
                string alert = n.Kind switch { "breaking" => "WARNING", "warning" => "WARNING", _ => "NOTE" };
                sb.Append("> [!").Append(alert).Append("]\n> ");
                if (n.Kind == "breaking") sb.Append("**Breaking:** ");
                sb.Append(n.Text.Replace("\n", "\n> ", StringComparison.Ordinal)).Append("\n\n");
            }

            if (doc.Highlights.Length > 0)
            {
                sb.Append("## Highlights\n\n");
                foreach (var h in doc.Highlights)
                {
                    sb.Append("### ").Append(h.Title);
                    if (h.Kind.Length > 0) sb.Append(" · ").Append(HighlightKindLabel(h.Kind));
                    sb.Append("\n\n");
                    if (h.Body.Length > 0) sb.Append(h.Body).Append("\n\n");
                    if (h.Media is { } m)
                    {
                        string alt = m.Alt.Length > 0 ? m.Alt : h.Title;
                        if (m.Poster is { Length: > 0 } poster)
                            sb.Append("[![").Append(alt).Append("](").Append(MediaAssetUrl(repo, doc.Version, poster))
                              .Append(")](").Append(MediaAssetUrl(repo, doc.Version, m.Src)).Append(")\n\n");
                        else if (IsMotionMedia(m.Kind, m.Src))
                            sb.Append('[').Append(alt).Append("](").Append(MediaAssetUrl(repo, doc.Version, m.Src)).Append(")\n\n");
                        else
                            sb.Append("![").Append(alt).Append("](").Append(MediaAssetUrl(repo, doc.Version, m.Src)).Append(")\n\n");
                    }
                    if (h.DeepLink is { Length: > 0 } dl) sb.Append('`').Append(dl).Append("`\n\n");
                }
            }

            foreach (var s in doc.Sections)
            {
                if (s.Items.Length == 0) continue;
                sb.Append("## ").Append(SectionTitle(s.Kind)).Append("\n\n");
                foreach (var item in s.Items)
                {
                    sb.Append("- ");
                    if (item.Scope is { Length: > 0 } scope) sb.Append(scope).Append(": ");
                    sb.Append(item.Text);
                    bool first = true;
                    foreach (var i in item.Issues)
                    {
                        sb.Append(first ? " (" : ", ");
                        first = false;
                        sb.Append('[').Append('#').Append(i.Number).Append("](https://github.com/")
                          .Append(i.Repo.Length > 0 ? i.Repo : repo).Append("/issues/").Append(i.Number).Append(')');
                    }
                    foreach (var p in item.Prs)
                    {
                        sb.Append(first ? " (" : ", ");
                        first = false;
                        sb.Append('[').Append('#').Append(p.Number).Append("](https://github.com/")
                          .Append(p.Repo.Length > 0 ? p.Repo : repo).Append("/pull/").Append(p.Number).Append(')');
                    }
                    if (!first) sb.Append(')');
                    sb.Append('\n');
                }
                sb.Append('\n');
            }

            AppendResolvedIssues(sb, doc, repo);
            AppendOtherChanges(sb, doc, repo);

            sb.Append("---\n\n");
            var facts = new List<string>(5);
            if (doc.Date.Length > 0) facts.Add("Released " + doc.Date);
            if (doc.PackageVersion.Length > 0) facts.Add("build " + doc.PackageVersion);
            if (doc.Channel.Length > 0) facts.Add(doc.Channel);
            if (doc.Arch.Length > 0) facts.Add(string.Join(", ", doc.Arch));
            if (doc.MinOs.Length > 0) facts.Add("requires Windows " + doc.MinOs);
            sb.Append(string.Join(" · ", facts)).Append("\n\n");
            if (doc.Links.Changelog.Length > 0)
                sb.Append("Full changelog: ").Append(doc.Links.Changelog).Append('\n');
            if (doc.Links.Compare.Length > 0)
                sb.Append("Compare: ").Append(doc.Links.Compare).Append('\n');
            if (doc.Links.Changelog.Length > 0 || doc.Links.Compare.Length > 0) sb.Append('\n');

            if (!string.IsNullOrWhiteSpace(generatedNotes))
            {
                sb.Append("<details><summary>Commits &amp; contributors</summary>\n\n")
                  .Append(generatedNotes.Trim()).Append("\n\n</details>\n");
            }

            return sb.ToString();
        }

        /// <summary>The <c>## Resolved issues</c> block: one line per distinct issue number (ascending) the document
        /// cites, its title, the commits that fix it and any PR that shipped it. Omitted entirely when no section item
        /// carries a commit (<see cref="ReleaseItem.Commits"/>).</summary>
        static void AppendResolvedIssues(StringBuilder sb, ReleaseNotesDocument doc, string repo)
        {
            bool anyCommits = false;
            foreach (var s in doc.Sections)
            {
                foreach (var item in s.Items)
                {
                    if (item.Commits.Length == 0) continue;
                    anyCommits = true;
                    break;
                }
                if (anyCommits) break;
            }
            if (!anyCommits) return;

            sb.Append("## Resolved issues\n\n");
            foreach (var n in IssueNumbers(doc))
            {
                ReleaseIssue? issue = null;
                var commits = new List<ReleaseCommit>();
                var seenSha = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in doc.Sections)
                {
                    foreach (var item in s.Items)
                    {
                        bool cites = false;
                        foreach (var i in item.Issues)
                        {
                            if (i.Number != n) continue;
                            cites = true;
                            issue ??= i;
                        }
                        if (!cites) continue;
                        // An item can cite several issues; only the commits that name THIS one (closing keyword or
                        // squash suffix) belong on its line.
                        foreach (var c in item.Commits)
                            if ((Array.IndexOf(c.Issues, n) >= 0 || Array.IndexOf(c.Prs, n) >= 0) && seenSha.Add(c.Sha))
                                commits.Add(c);
                    }
                }

                sb.Append("- [#").Append(n).Append("](https://github.com/").Append(repo)
                  .Append(issue is { IsPullRequest: true } ? "/pull/" : "/issues/").Append(n).Append(')');
                if (issue is { Title.Length: > 0 }) sb.Append(' ').Append(EscapeMarkdown(issue.Title));
                sb.Append(" — ");
                if (commits.Count == 0)
                {
                    sb.Append("no linked commit");
                }
                else
                {
                    for (int i = 0; i < commits.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append('[').Append(commits[i].Short).Append("](https://github.com/").Append(repo)
                          .Append("/commit/").Append(commits[i].Sha).Append(')');
                    }
                    var prs = new SortedSet<int>();
                    foreach (var c in commits)
                        foreach (var p in c.Prs)
                            if (p != n) prs.Add(p);
                    if (prs.Count > 0)
                    {
                        sb.Append(" (PR ");
                        bool first = true;
                        foreach (var p in prs)
                        {
                            if (!first) sb.Append(", ");
                            first = false;
                            sb.Append('[').Append('#').Append(p).Append("](https://github.com/").Append(repo)
                              .Append("/pull/").Append(p).Append(')');
                        }
                        sb.Append(')');
                    }
                }
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        /// <summary>The folded <c>&lt;details&gt;Other changes&lt;/details&gt;</c> appendix — every commit in range
        /// that cites no section item (<see cref="ReleaseNotesDocument.UnlinkedCommits"/>). Omitted when empty.</summary>
        static void AppendOtherChanges(StringBuilder sb, ReleaseNotesDocument doc, string repo)
        {
            if (doc.UnlinkedCommits.Length == 0) return;
            sb.Append("<details><summary>Other changes</summary>\n\n");
            foreach (var c in doc.UnlinkedCommits)
                sb.Append("- [").Append(c.Short).Append("](https://github.com/").Append(repo).Append("/commit/")
                  .Append(c.Sha).Append(") ").Append(EscapeMarkdown(c.Subject)).Append('\n');
            sb.Append("\n</details>\n\n");
        }

        /// <summary>Backslash-escapes GitHub Flavored Markdown's special characters (<c>\ * _ ` [ ] &lt; &gt; ~ |</c>)
        /// so a commit subject or issue title cannot break the release body's formatting. Deliberately leaves
        /// <c>#</c> alone — GitHub autolinks <c>#52</c> in a subject to the issue/PR, and escaping it would break
        /// that.</summary>
        public static string EscapeMarkdown(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length + 8);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': case '*': case '_': case '`': case '[': case ']':
                    case '<': case '>': case '~': case '|':
                        sb.Append('\\');
                        break;
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>Distinct issue numbers (&gt; 0) cited by any section item, ascending.</summary>
        public static int[] IssueNumbers(ReleaseNotesDocument doc)
        {
            ArgumentNullException.ThrowIfNull(doc);
            var set = new SortedSet<int>();
            foreach (var s in doc.Sections)
                foreach (var item in s.Items)
                    foreach (var i in item.Issues)
                        if (i.Number > 0) set.Add(i.Number);
            var result = new int[set.Count];
            set.CopyTo(result);
            return result;
        }

        /// <summary>
        /// The store blurb: tagline plus highlight titles, never longer than <see cref="StoreListingMaxChars"/>.
        /// Trailing bullets are dropped before any text is cut; only a tagline that is itself over the cap gets
        /// hard-truncated (on a word boundary, with an ellipsis).
        /// </summary>
        public static string RenderStoreListing(ReleaseNotesDocument doc)
        {
            ArgumentNullException.ThrowIfNull(doc);
            doc.Normalize();
            string head = doc.Tagline.Trim();
            string title = doc.Name.Length > 0
                ? $"New in Wavee {doc.Version} “{doc.Name}”:"
                : $"New in Wavee {doc.Version}:";

            var bullets = new List<string>();
            foreach (var h in doc.Highlights)
                if (h.Title.Length > 0)
                    bullets.Add("- " + h.Title.Trim());

            while (true)
            {
                var sb = new StringBuilder(StoreListingMaxChars);
                if (head.Length > 0) sb.Append(head).Append("\n\n");
                if (bullets.Count > 0)
                {
                    sb.Append(title).Append('\n');
                    for (int i = 0; i < bullets.Count; i++) sb.Append(bullets[i]).Append('\n');
                }
                string text = sb.ToString().TrimEnd();
                if (text.Length <= StoreListingMaxChars) return text;
                if (bullets.Count > 0) { bullets.RemoveAt(bullets.Count - 1); continue; }
                return Ellipsize(text, StoreListingMaxChars);
            }
        }

        static string Ellipsize(string text, int max)
        {
            if (text.Length <= max) return text;
            int cut = max - 1;
            int space = text.LastIndexOf(' ', Math.Min(cut, text.Length - 1));
            if (space > max / 2) cut = space;
            return text[..cut].TrimEnd() + "…";
        }

        static bool IsSafeRelativePath(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) return false;
            if (Path.IsPathRooted(src)) return false;
            if (src.Contains(':', StringComparison.Ordinal)) return false;
            foreach (var seg in src.Split('/', '\\'))
                if (seg == "..") return false;
            return true;
        }

        /// <summary>UTC stamp in the form the schema uses for <c>generatedAt</c>.</summary>
        public static string Stamp(DateTimeOffset when)
            => when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    // ── HighlightVisibility.cs ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Which highlights a given install actually shows. One kind is channel-aware: a <c>"store"</c> highlight
    /// ("Wavee is now on the Microsoft Store", with its get-it button) is an announcement FOR the feed channels — a
    /// Store-installed build is already there, and showing it a card whose button opens the listing it came from reads
    /// as a bug. Every other kind renders everywhere; an unknown kind stays visible (forward-compat: an old build
    /// title-cases it into a generic pill rather than dropping the card).
    /// <para>Pure by design — the page, the after-update dialog and the tests all ask the same class, so "hidden on the
    /// Store" cannot drift between the two surfaces.</para></summary>
    public static class HighlightVisibility
    {
        /// <summary>The channel-aware highlight kind (compared case-insensitively, like every kind on the wire).</summary>
        public const string StoreKind = "store";

        /// <summary>Is this the Store-announcement kind — the one that gets the special card treatment (accent chrome +
        /// the "Get it from the Microsoft Store" button) instead of the generic kind pill?</summary>
        public static bool IsStore(ReleaseHighlight? highlight)
            => highlight is not null && string.Equals(highlight.Kind, StoreKind, StringComparison.OrdinalIgnoreCase);

        /// <summary>Does this highlight render on this install? Everything renders everywhere, except a
        /// <see cref="StoreKind"/> highlight on a Store-channel install.</summary>
        public static bool IsVisible(ReleaseHighlight? highlight, bool isStoreInstall)
            => highlight is not null && !(isStoreInstall && IsStore(highlight));

        /// <summary>The first <paramref name="max"/> VISIBLE highlights, in document order. The cap counts what actually
        /// renders: a hidden store card frees its slot for the next highlight rather than shipping a two-card strip with
        /// an invisible third.</summary>
        public static List<ReleaseHighlight> SelectVisible(IReadOnlyList<ReleaseHighlight?>? highlights,
                                                           bool isStoreInstall, int max)
        {
            var visible = new List<ReleaseHighlight>(max > 0 ? Math.Min(max, highlights?.Count ?? 0) : 0);
            if (highlights is null || max <= 0) return visible;
            foreach (var h in highlights)
            {
                if (visible.Count >= max) break;
                if (IsVisible(h, isStoreInstall)) visible.Add(h!);
            }
            return visible;
        }
    }

    // ── HighlightCardMetrics.cs ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The highlight card's fixed geometry and the after-update dialog's height budget, as PURE arithmetic.
    /// The card and the dialog RENDER from these constants and the tests hold the 620 DIP plate cap against them, so a
    /// padding someone nudges fails a test instead of a screenshot.</summary>
    public static class HighlightCardMetrics
    {
        /// <summary>Release images are authored 1200×675; the band derives its height from the card's width rather than
        /// pinning a pixel height a wide card would letterbox and a narrow one would crop.</summary>
        public const float PosterAspect = 16f / 9f;

        /// <summary>The page card's width cap — with one highlight a full-row card reads as a banner, not a card.</summary>
        public const float CardMaxW = 420f;

        /// <summary>The dialog's lone-card cap, REPLACING the old 236 DIP height cap: 356 wide gives a 200 DIP band at
        /// the authored proportion. A height cap would have had to crop.</summary>
        public const float CompactCardMaxW = 356f;

        public const float TitleSize = 13.5f;
        /// <summary>Explicit, so the title block's height is arithmetic (18 or 36), not a font-metric guess.</summary>
        public const float TitleLineHeight = 18f;
        /// <summary>Two: "Report a problem from inside Wavee" wraps at 216 wide and a one-line ellipsis cuts the verb off.
        /// Three is 18 DIP the body needs more.</summary>
        public const int TitleMaxLines = 2;

        public const float BodySize = 12.5f;
        /// <summary>The font-natural box for 12.5 px is ~16.6; an INTEGER line height is what makes the overflow test
        /// exact — natural heights are 17·n, never within half a pixel of the threshold.</summary>
        public const float BodyLineHeight = 17f;
        /// <summary>Four: ~128 characters at 216 wide (one whole sentence of every 0.2.6 body), ~260 at 420. Three cuts
        /// the Logs body inside its first clause; five is 17 DIP the row does not need once the viewer exists.</summary>
        public const int BodyLines = 4;
        public const float BodySlotHeight = BodyLines * BodyLineHeight;      // 68

        /// <summary>One line of fade, not two — two dissolved lines out of four reads as a rendering fault.</summary>
        public const float FadeHeight = 24f;
        /// <summary>Natural heights are 51 / 68 / 85, so "exactly four lines" is decided correctly.</summary>
        public const float OverflowThreshold = BodySlotHeight + 0.5f;        // 68.5

        public const float LabelHeight = 16f;
        public const float StoreButtonHeight = 32f;
        public const float StoreButtonGap = 4f;
        public const float PadL = 12f, PadT = 10f, PadR = 12f, PadB = 12f;
        /// <summary>The column gap between title, slot and label.</summary>
        public const float TitleBodyGap = 4f;
        /// <summary>The store card's hit region stops 4 DIP under the slot; its button is a sibling footer, so a card
        /// never nests a button inside a button.</summary>
        public const float HitRegionStoreBottomPad = 4f;

        public const float PlateWidth = 720f;
        public const float PlatePadX = 26f;
        public const float PlateMaxHeight = 620f;
        public const float CardGap = 10f;
        public const float RowPadTop = 6f, RowPadBottom = 14f;
        public const float HeroPadTop = 22f, HeroPillRow = 22f, HeroGap = 8f,
                           HeroWelcomeLine = 35f, HeroTaglineLine = 19f, HeroPadBottom = 18f;
        /// <summary>14 + 32 buttons + 14 + the 1 px top border.</summary>
        public const float FooterHeight = 62f;

        public static bool Overflows(float naturalHeight) => naturalHeight > OverflowThreshold;

        /// <summary>10 + title + 4 + 68 + tail + 12 → 132 / 150 regular (one- / two-line title), 152 / 170 store.</summary>
        public static float TextBlockHeight(int titleLines, bool store)
        {
            float title = Math.Clamp(titleLines, 1, TitleMaxLines) * TitleLineHeight;
            float tail = store
                ? HitRegionStoreBottomPad + StoreButtonGap + StoreButtonHeight   // 4 + 4 + 32
                : TitleBodyGap + LabelHeight;                                    // 4 + 16
            return PadT + title + TitleBodyGap + BodySlotHeight + tail + PadB;
        }

        /// <summary>668 inner: three-up 216, two-up 329, lone min(668, 356) = 356.</summary>
        public static float DialogCardWidth(int cardCount)
        {
            int n = Math.Clamp(cardCount, 1, 3);
            float inner = PlateWidth - 2f * PlatePadX;
            return MathF.Min((inner - (n - 1) * CardGap) / n, CompactCardMaxW);
        }

        /// <summary>The 16:9 band: 216 → 122, 329 → 185, 356 → 200, 420 → 236.</summary>
        public static float BandHeight(float cardWidth)
            => MathF.Round(cardWidth / PosterAspect, MidpointRounding.AwayFromZero);

        public static float CardHeight(float cardWidth, int titleLines, bool store)
            => BandHeight(cardWidth) + TextBlockHeight(titleLines, store);

        /// <summary>22 + 22 + 8 + 35 + 8 + 19·lines + 18 → 132 for one tagline line, 151 for two.</summary>
        public static float HeroHeight(int taglineLines)
            => HeroPadTop + HeroPillRow + HeroGap + HeroWelcomeLine + HeroGap
               + Math.Max(1, taglineLines) * HeroTaglineLine + HeroPadBottom;

        /// <summary>The plate's height for a row of <paramref name="cardCount"/> cards, worst case (two-line titles).</summary>
        public static float DialogHeight(int cardCount, bool store, int taglineLines)
            => HeroHeight(taglineLines) + RowPadTop
               + CardHeight(DialogCardWidth(cardCount), TitleMaxLines, store)
               + RowPadBottom + FooterHeight;
    }

    // ── HighlightViewerLayout.cs ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The viewer's navigation verbs, app-neutral: the view maps FluentGpu's <c>Keys.*</c> ints onto these so
    /// this file stays engine-free and unit-testable.</summary>
    public enum HighlightNavKey : byte { Previous, Next, First, Last }

    /// <summary>Which way a slide travels. None = the index did not change (clamped at an end, or a single item).</summary>
    public enum HighlightSlideDirection : byte { None, Forward, Back }

    public readonly record struct HighlightStep(int Index, HighlightSlideDirection Direction);

    /// <summary>The viewer plate's geometry (design §B.1) and its stepping rule (§B.2), pure.</summary>
    public static class HighlightViewerLayout
    {
        /// <summary>A 1200 px poster shown at 960 is a 0.8× downsample — sharp, never upsampled; wider plates make the
        /// text measure absurd.</summary>
        public const float PlateMaxWidth = 960f;
        /// <summary>Below this the image is unreadable (180 tall); the text column scrolls instead.</summary>
        public const float PlateMinWidth = 320f;
        /// <summary>48 DIP of veil each side, so the plate reads as a plate and not as a page.</summary>
        public const float ScrimInsetX = 96f;
        /// <summary>Pager (36) + text block (≤ 260) + 64 vertical margin the image must leave below itself.</summary>
        public const float ReservedBelowImage = 360f;
        public const float PlateMarginY = 64f;
        public const float PosterAspect = 16f / 9f;
        public const float ChromeCircle = 36f;
        public const float ChromeInset = 12f;

        /// <summary>W = min(max(320, min(960, vpW − 96, (vpH − 360)·16⁄9)), vpW − 96). The floor never pushes the plate
        /// past the window edge. 1440×900 → 960; 1100×700 → 604; 900×600 → 427; 500×420 → 320; a 320-wide window → 224.</summary>
        public static float PlateWidth(float vpW, float vpH)
        {
            float byWidth = vpW - ScrimInsetX;
            float byHeight = (vpH - ReservedBelowImage) * PosterAspect;
            float w = MathF.Min(PlateMaxWidth, MathF.Min(byWidth, byHeight));
            w = MathF.Max(PlateMinWidth, w);
            w = MathF.Min(w, byWidth);
            return MathF.Round(w);
        }

        /// <summary>The 16:9 band height, from the plate's OWN width — always, poster or not. L4 (issue #89): a
        /// no-poster slide used to get a flat 120 DIP band regardless of plate width; the prototype keeps the same
        /// <c>w·9/16</c> rule either way (a tinted band, not a smaller one), which is also just this formula with
        /// <paramref name="hasPoster"/> dropped — kept as a parameter for the call-site's own poster-presence branch on
        /// the band's FILL, not its height.</summary>
        public static float ImageHeight(float plateWidth, bool hasPoster) => MathF.Round(plateWidth / PosterAspect);

        public static float PlateMaxHeight(float vpH) => vpH - PlateMarginY;

        /// <summary>Clamped, no wrap (the WinUI FlipView rule): a Right press on the last slide does nothing. A silent
        /// jump back to the first feels like a bug, and the dots already say where you are.</summary>
        public static HighlightStep Step(int current, int count, HighlightNavKey key)
        {
            if (count <= 1) return new(0, HighlightSlideDirection.None);
            int last = count - 1;
            current = Math.Clamp(current, 0, last);
            int target = key switch
            {
                HighlightNavKey.Previous => current - 1,
                HighlightNavKey.Next => current + 1,
                HighlightNavKey.First => 0,
                _ => last,
            };
            return StepTo(current, Math.Clamp(target, 0, last), count);
        }

        /// <summary>A direct jump (a pip click): the direction is the sign of the move.</summary>
        public static HighlightStep StepTo(int current, int target, int count)
        {
            if (count <= 1) return new(0, HighlightSlideDirection.None);
            int last = count - 1;
            current = Math.Clamp(current, 0, last);
            target = Math.Clamp(target, 0, last);
            var dir = target > current ? HighlightSlideDirection.Forward
                    : target < current ? HighlightSlideDirection.Back
                    : HighlightSlideDirection.None;
            return new(dir == HighlightSlideDirection.None ? current : target, dir);
        }
    }

    // ── IssueStateBudget.cs ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The rate-limit policy for refreshing issue chips. Wavee ships no GitHub token, so the REST budget is 60
    /// requests per hour per IP — a What's-new page with forty references would burn it on one opening.
    /// <para>Pure: it decides WHICH keys to fetch and WHEN to give up; the HTTP half lives in the app's store.</para></summary>
    public sealed class IssueStateBudget
    {
        public const long OneDayMs = 24L * 60L * 60L * 1000L;

        /// <param name="maxPerOpen">How many issue fetches one opening of the page may spend.</param>
        /// <param name="ttlMs">How long a cached state stays good; inside it, an issue is never re-fetched.</param>
        public IssueStateBudget(int maxPerOpen = 20, long ttlMs = OneDayMs)
        {
            MaxPerOpen = maxPerOpen < 0 ? 0 : maxPerOpen;
            TtlMs = ttlMs < 0 ? 0 : ttlMs;
        }

        public int MaxPerOpen { get; }
        public long TtlMs { get; }

        /// <summary>The keys worth fetching now: input order, duplicates collapsed, anything still fresh in
        /// <paramref name="cache"/> dropped, and the result capped at <see cref="MaxPerOpen"/>. Empty is a valid plan (and
        /// the common one on a second visit) — the page then renders the snapshot states with its "as of" footer.</summary>
        public string[] Plan(IEnumerable<string> keys, IssueStateCache cache, long nowMs)
        {
            if (keys is null || MaxPerOpen == 0) return [];

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var plan = new List<string>(Math.Min(MaxPerOpen, 16));
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (!seen.Add(key)) continue;
                if (cache is not null && cache.IsFresh(key, nowMs, TtlMs)) continue;
                plan.Add(key);
                if (plan.Count >= MaxPerOpen) break;
            }
            return plan.ToArray();
        }

        /// <summary>Stop the whole refresh, not just this request: GitHub answered 403 (rate limited or UA-rejected), or the
        /// response says the remaining quota is zero. Anything else — including a 404 for a deleted issue — is a per-issue
        /// problem the caller skips past.</summary>
        public bool ShouldStop(int statusCode, string? rateLimitRemainingHeader)
        {
            if (statusCode == 403 || statusCode == 429) return true;
            if (string.IsNullOrWhiteSpace(rateLimitRemainingHeader)) return false;
            return int.TryParse(rateLimitRemainingHeader.Trim(), System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out int remaining) && remaining <= 0;
        }
    }

    // ── IssueStateCache.cs ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One issue's live GitHub state, as last fetched.</summary>
    public sealed class IssueState
    {
        /// <summary>open | closed.</summary>
        public string State { get; set; } = "open";
        /// <summary>completed | reopened | not_planned | duplicate | null.</summary>
        public string? StateReason { get; set; }
        public string Title { get; set; } = "";
        /// <summary>Unix-ms of the fetch that produced this entry — the TTL is measured from here.</summary>
        public long FetchedAtMs { get; set; }
    }

    /// <summary>The persisted "issue chips" cache: <c>"{repo}#{number}"</c> → its last known state.
    /// <para>Wavee ships no GitHub token, so the REST budget is 60 requests/hour per IP; this cache plus
    /// <see cref="IssueStateBudget"/> is what keeps a What's-new page open all day from burning it. Serialized with
    /// <see cref="ReleaseNotesJsonContext"/> — dictionary keys are NOT camel-cased, so the repo slug survives verbatim.</para></summary>
    public sealed class IssueStateCache
    {
        public Dictionary<string, IssueState> Entries { get; set; } = new(StringComparer.Ordinal);

        /// <summary>The canonical cache key for an issue or PR reference.</summary>
        public static string Key(string repo, int number)
            => repo + "#" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The live state for a document's issue reference, or null when nothing has been fetched for it.</summary>
        public IssueState? Lookup(ReleaseIssue issue)
            => issue is null ? null : Lookup(Key(issue.Repo, issue.Number));

        /// <summary>The live state for a raw <c>"{repo}#{number}"</c> key, or null.</summary>
        public IssueState? Lookup(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var entries = Entries;
            if (entries is null) return null;
            return entries.TryGetValue(key, out var state) ? state : null;
        }

        /// <summary>True when the key has an entry younger than <paramref name="ttlMs"/> — i.e. re-fetching it would be
        /// waste. An entry stamped in the future (clock skew) counts as fresh rather than triggering a refetch storm.</summary>
        public bool IsFresh(string key, long nowMs, long ttlMs)
            => Lookup(key) is { } e && nowMs - e.FetchedAtMs < ttlMs;

        /// <summary>Records (or replaces) one fetched state.</summary>
        public void Set(string key, IssueState state)
        {
            if (string.IsNullOrEmpty(key) || state is null) return;
            Entries ??= new Dictionary<string, IssueState>(StringComparer.Ordinal);
            Entries[key] = state;
        }
    }
}
