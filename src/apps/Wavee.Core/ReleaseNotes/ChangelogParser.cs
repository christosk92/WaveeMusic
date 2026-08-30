using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Wavee.Core.ReleaseNotes;

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
