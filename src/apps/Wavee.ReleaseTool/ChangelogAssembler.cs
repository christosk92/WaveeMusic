using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using static Wavee.ReleaseNotes;

namespace Wavee.ReleaseTool;

/// <summary>
/// <c>changelog assemble</c>: folds <c>changes/unreleased/*.md</c> into <c>CHANGELOG.md</c> as the version's section,
/// raises the theme contract version from their <c>contract:</c> fields (with a section in
/// <c>docs/themes/contract-history.md</c>), adds each new community login to <c>CONTRIBUTORS.md</c> and deletes the
/// files it assembled. The release runner's <c>changelog</c> step; the owner curates the section afterwards.
/// </summary>
/// <remarks>Everything is worked out before the first write, so a refusal (exit 2) or a missing file (exit 1) changes
/// nothing. Every file keeps its line endings and its BOM (or lack of one).</remarks>
static partial class ChangelogAssembler
{
    /// <summary>The CHANGELOG's wrap column; a continuation line is indented two spaces, as the existing bullets are.</summary>
    const int WrapColumn = 120;

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemverRegex();

    [GeneratedRegex(@"^(?<head>\s*public const int (?<name>Major|Minor) = )(?<n>\d+)(?<tail>;.*)$")]
    private static partial Regex ConstantRegex();

    [GeneratedRegex(@"^\|\s*\[@(?<login>[A-Za-z0-9-]+)\]")]
    private static partial Regex ContributorRowRegex();

    [GeneratedRegex(@"^\d{1,9}[.)]$")]
    private static partial Regex OrderedMarkerRegex();

    public static int Run(Args a)
    {
        var bad = a.UnknownKeys("root", "version", "dry-run", "json");
        if (bad.Count > 0) { Console.Error.WriteLine("error: unknown option(s): " + string.Join(", ", bad)); Program.PrintUsage(); return Validator.ExitUsage; }
        if (a.Positional is not ["assemble"])
        {
            Console.Error.WriteLine("error: 'changelog' takes one verb: changelog assemble --root <repo> --version <M.m.p> [--dry-run] [--json]");
            Program.PrintUsage();
            return Validator.ExitUsage;
        }
        if (!ChangeFiles.TryRoot(a, out string root)) return Validator.ExitUsage;
        if (a.Get("version") is not { } version)
        {
            Console.Error.WriteLine("error: missing required argument: --version <M.m.p>");
            Program.PrintUsage();
            return Validator.ExitUsage;
        }
        if (!SemverRegex().IsMatch(version))
        {
            Console.Error.WriteLine($"error: --version '{version}' is not a semver (M.m.p)");
            return Validator.ExitUsage;
        }
        bool dryRun = a.Flag("dry-run");
        bool json = a.Flag("json");

        // ── the change files ────────────────────────────────────────────────────────────────────────────────
        var folderProblems = new List<string>();
        var files = ChangeFiles.ReadAll(root, folderProblems);
        var problems = new List<string>(folderProblems);
        foreach (var f in files)
            foreach (var p in f.Problems) problems.Add(f.RelativePath + ": " + p);
        if (problems.Count > 0)
        {
            foreach (var p in problems) Console.Error.WriteLine("error: " + p);
            Console.Error.WriteLine($"error: {problems.Count} problem(s) in the change files; nothing was assembled");
            return Validator.ExitInvalid;
        }
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"error: no change files in {ChangeFiles.Folder}/: nothing to assemble");
            return Validator.ExitInvalid;
        }

        // ── CHANGELOG.md ────────────────────────────────────────────────────────────────────────────────────
        string changelogPath = Path.Combine(root, "CHANGELOG.md");
        if (!TextFile.TryRead(changelogPath, out var changelog)) return Validator.ExitUsage;
        string heading = "## [" + version + "]";
        int firstRelease = -1;
        for (int i = 0; i < changelog.Lines.Count; i++)
        {
            string line = changelog.Lines[i];
            if (line.StartsWith(heading, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"error: CHANGELOG.md already has a '{heading}' section (line {i + 1}); nothing was assembled");
                return Validator.ExitInvalid;
            }
            if (firstRelease < 0 && line.StartsWith("## [", StringComparison.Ordinal)) firstRelease = i;
        }

        var section = new List<string> { heading + " - unreleased", "" };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kind in ChangeFiles.Kinds)
        {
            counts[kind] = 0;
            foreach (var f in files)
            {
                if (f.Kind != kind) continue;
                if (counts[kind]++ == 0) section.AddRange(["### " + ReleaseNotesValidation.SectionTitle(kind), ""]);
                section.AddRange(Wrap(BulletText(f), "- ", "  "));
            }
            if (counts[kind] > 0) section.Add("");
        }
        string before = firstRelease >= 0 ? changelog.Lines[firstRelease] : "";
        InsertBlock(changelog, firstRelease, section);

        // ── the theme contract ──────────────────────────────────────────────────────────────────────────────
        string contractPath = Path.Combine(root, "src", "apps", "Wavee", "Theming", "Contracts", "ContractVersion.cs");
        if (!TextFile.TryRead(contractPath, out var contract)) return Validator.ExitUsage;
        int major = -1, minor = -1, majorLine = -1, minorLine = -1;
        for (int i = 0; i < contract.Lines.Count; i++)
        {
            var m = ConstantRegex().Match(contract.Lines[i]);
            if (!m.Success) continue;
            int n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (m.Groups["name"].Value == "Major") { major = n; majorLine = i; }
            else { minor = n; minorLine = i; }
        }
        if (majorLine < 0 || minorLine < 0)
        {
            Console.Error.WriteLine("error: " + contractPath + " has no 'public const int Major = N;' / 'public const int Minor = N;'");
            return Validator.ExitUsage;
        }

        int rank = 0;
        foreach (var f in files) rank = Math.Max(rank, ChangeFiles.ContractRank(f.Contract));
        string bump = rank switch { 2 => "breaking", 1 => "additive", _ => "none" };
        int toMajor = rank == 2 ? major + 1 : major;
        int toMinor = rank == 2 ? 0 : rank == 1 ? minor + 1 : minor;
        string from = major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture);
        string to = toMajor.ToString(CultureInfo.InvariantCulture) + "." + toMinor.ToString(CultureInfo.InvariantCulture);

        string historyPath = Path.Combine(root, "docs", "themes", "contract-history.md");
        TextFile? history = null;
        var historySection = new List<string>();
        if (rank > 0)
        {
            SetConstant(contract, majorLine, toMajor);
            SetConstant(contract, minorLine, toMinor);
            if (!TextFile.TryRead(historyPath, out var h)) return Validator.ExitUsage;
            history = h;
            historySection.AddRange(["## " + to + " (Wavee " + version + ")", ""]);
            foreach (var f in files)
            {
                if (f.Contract == "none") continue;
                string note = Collapse(f.ContractNote);
                if (f.Issues.Count > 0) note += " (" + IssueRefs(f.Issues) + ")";
                historySection.AddRange(Wrap(note, "- ", "  "));
            }
            historySection.Add("");
            int firstSection = history.Lines.FindIndex(static l => l.StartsWith("## ", StringComparison.Ordinal));
            InsertBlock(history, firstSection, historySection);
        }

        // ── CONTRIBUTORS.md ─────────────────────────────────────────────────────────────────────────────────
        var community = new List<string>();
        foreach (var f in files)
            if (f.By is { } by && !community.Exists(x => x.Equals(by, StringComparison.OrdinalIgnoreCase))) community.Add(by);

        string contributorsPath = Path.Combine(root, "CONTRIBUTORS.md");
        TextFile? contributors = null;
        var newRows = new List<string>();
        var known = new List<string>();
        if (community.Count > 0)
        {
            if (!TextFile.TryRead(contributorsPath, out var c)) return Validator.ExitUsage;
            contributors = c;
            if (!AddContributors(contributors, files, community, version, newRows, known))
            {
                Console.Error.WriteLine("error: CONTRIBUTORS.md has no '## Code' table (| Who | What |)");
                return Validator.ExitUsage;
            }
        }

        var issues = new SortedSet<int>();
        foreach (var f in files) issues.UnionWith(f.Issues);

        // ── write, or say what would be written ─────────────────────────────────────────────────────────────
        if (!dryRun)
        {
            changelog.Write();
            if (rank > 0)
            {
                contract.Write();
                history!.Write();
            }
            if (newRows.Count > 0) contributors!.Write();
            foreach (var f in files) File.Delete(f.FullPath);
        }

        if (json)
        {
            Console.Out.WriteLine(ChangeFiles.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("version", version);
                var paths = new List<string>(files.Count);
                foreach (var f in files) paths.Add(f.RelativePath);
                ChangeFiles.WriteStrings(w, "files", paths);
                w.WriteStartObject("sections");
                foreach (var kind in ChangeFiles.Kinds) w.WriteNumber(kind, counts[kind]);
                w.WriteEndObject();
                w.WriteStartObject("contract");
                w.WriteString("from", from);
                w.WriteString("to", to);
                w.WriteString("bump", bump);
                w.WriteEndObject();
                ChangeFiles.WriteStrings(w, "community", community);
                w.WriteStartArray("issues");
                foreach (var n in issues) w.WriteNumberValue(n);
                w.WriteEndArray();
                w.WriteEndObject();
            }));
            return Validator.ExitOk;
        }

        var o = Console.Out;
        o.WriteLine(dryRun
            ? $"changelog assemble (dry run, nothing written): Wavee {version}, {files.Count} change file(s)"
            : $"ok: assembled {files.Count} change file(s) into CHANGELOG.md [{version}]");
        o.WriteLine();
        o.WriteLine(before.Length > 0 ? $"CHANGELOG.md, above '{before}':" : "CHANGELOG.md, at the end:");
        foreach (var l in section) o.WriteLine("    " + l);
        if (rank > 0)
        {
            o.WriteLine($"theme contract {from} -> {to} ({bump}): ContractVersion.cs Major = {toMajor}, Minor = {toMinor}; docs/themes/contract-history.md gains:");
            foreach (var l in historySection) o.WriteLine("    " + l);
        }
        else
        {
            o.WriteLine($"theme contract stays {from} (no change file changes it)");
        }
        foreach (var login in known) o.WriteLine($"CONTRIBUTORS.md already credits @{login} in its Code table");
        foreach (var row in newRows) o.WriteLine("CONTRIBUTORS.md, Code table: " + row);
        o.WriteLine(dryRun ? "would delete:" : "deleted:");
        foreach (var f in files) o.WriteLine("    " + f.RelativePath);
        return Validator.ExitOk;
    }

    /// <summary>The bullet's text before wrapping: the body on one line, the community credit, then the <c>(#a, #b)</c>
    /// group last, where <see cref="ChangelogParser"/> (and so the release's issue cross-check) reads it.</summary>
    static string BulletText(ChangeFile f)
    {
        string text = BodyLine(f.Body);
        if (f.By is { } by) text += " A community contribution from @" + by + ", thank you.";
        if (f.Issues.Count > 0) text += " (" + IssueRefs(f.Issues) + ")";
        return text;
    }

    static string IssueRefs(List<int> issues)
    {
        var parts = new string[issues.Count];
        for (int i = 0; i < issues.Count; i++) parts[i] = "#" + issues[i].ToString(CultureInfo.InvariantCulture);
        return string.Join(", ", parts);
    }

    /// <summary>All white space, line breaks included, as single spaces.</summary>
    static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A change file's body on one line. A body written as a bullet loses its marker: the section adds its own.</summary>
    static string BodyLine(string body)
    {
        string text = Collapse(body);
        return text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal) ? text[2..] : text;
    }

    /// <summary>Greedy wrap at <see cref="WrapColumn"/>. A word longer than the line keeps a line of its own. A continuation
    /// line never starts with a word Markdown would read as a list item, a quote, a heading or an underline: the word
    /// before it moves down with it, or, when that word is alone on its line, the line runs long instead.</summary>
    static List<string> Wrap(string text, string first, string rest)
    {
        var lines = new List<string>();
        var cur = new List<string>();
        string prefix = first;
        int len = 0;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur.Count == 0) { cur.Add(word); len = prefix.Length + word.Length; continue; }
            bool risky = StartsBlock(word);
            if (len + 1 + word.Length <= WrapColumn || (risky && cur.Count == 1)) { cur.Add(word); len += 1 + word.Length; continue; }
            var carry = new List<string>(2);
            if (risky) { carry.Add(cur[^1]); cur.RemoveAt(cur.Count - 1); }
            lines.Add(prefix + string.Join(' ', cur));
            prefix = rest;
            carry.Add(word);
            cur = carry;
            len = prefix.Length + string.Join(' ', cur).Length;
        }
        if (cur.Count > 0) lines.Add(prefix + string.Join(' ', cur));
        return lines;
    }

    static bool StartsBlock(string word)
        => word is "*" or "+" || word[0] == '>' || OrderedMarkerRegex().IsMatch(word)
           || word.Trim('#').Length == 0 || word.Trim('=').Length == 0 || word.Trim('-').Length == 0;

    /// <summary>Inserts <paramref name="block"/> (which ends with its own blank line) before line <paramref name="at"/>,
    /// or at the end of the file when <paramref name="at"/> is negative.</summary>
    static void InsertBlock(TextFile file, int at, List<string> block)
    {
        if (at >= 0)
        {
            file.Lines.InsertRange(at, block);
            return;
        }
        // At the end: keep the file's final newline (its last "line" is then empty) and one blank line before the block.
        int end = file.Lines.Count > 0 && file.Lines[^1].Length == 0 ? file.Lines.Count - 1 : file.Lines.Count;
        var lead = new List<string>();
        if (end > 0 && file.Lines[end - 1].Length > 0) lead.Add("");
        lead.AddRange(block);
        if (end == file.Lines.Count - 1) lead.RemoveAt(lead.Count - 1);   // the file's own final newline ends the block
        file.Lines.InsertRange(end, lead);
    }

    static void SetConstant(TextFile file, int line, int value)
    {
        var m = ConstantRegex().Match(file.Lines[line]);
        file.Lines[line] = m.Groups["head"].Value + value.ToString(CultureInfo.InvariantCulture) + m.Groups["tail"].Value;
    }

    /// <summary>Adds a row to the <c>## Code</c> table for each login it does not credit yet, kept in the table's order
    /// (by login, case-insensitive). False when the file has no such table.</summary>
    static bool AddContributors(TextFile file, List<ChangeFile> files, List<string> logins, string version,
                                List<string> newRows, List<string> known)
    {
        var lines = file.Lines;
        int code = lines.FindIndex(static l => l.TrimEnd() == "## Code");
        if (code < 0) return false;
        int header = -1;
        for (int i = code + 1; i < lines.Count && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
            if (lines[i].StartsWith('|')) { header = i; break; }
        if (header < 0 || header + 1 >= lines.Count || !lines[header + 1].StartsWith("|-", StringComparison.Ordinal)) return false;
        int firstRow = header + 2;
        int end = firstRow;
        while (end < lines.Count && lines[end].StartsWith('|')) end++;

        foreach (var login in logins)
        {
            bool credited = false;
            for (int i = firstRow; i < end && !credited; i++)
                credited = ContributorRowRegex().Match(lines[i]) is { Success: true } m
                           && m.Groups["login"].Value.Equals(login, StringComparison.OrdinalIgnoreCase);
            if (credited) { known.Add(login); continue; }

            var leads = new List<string>();
            var issues = new List<int>();
            foreach (var f in files)
            {
                if (!login.Equals(f.By, StringComparison.OrdinalIgnoreCase)) continue;
                leads.Add(LeadPhrase(f.Body));
                foreach (var n in f.Issues) if (!issues.Contains(n)) issues.Add(n);
            }
            var row = new StringBuilder();
            row.Append("| [@").Append(login).Append("](https://github.com/").Append(login).Append(") | ")
               .Append(string.Join("; ", leads).Replace("|", "\\|", StringComparison.Ordinal));
            if (issues.Count > 0)
            {
                row.Append(" (");
                for (int i = 0; i < issues.Count; i++)
                {
                    string n = issues[i].ToString(CultureInfo.InvariantCulture);
                    if (i > 0) row.Append(", ");
                    row.Append("[#").Append(n).Append("](https://github.com/").Append(ChangelogParser.WaveeRepo)
                       .Append("/issues/").Append(n).Append(')');
                }
                row.Append(')');
            }
            row.Append(", in Wavee ").Append(version).Append(" |");

            int at = firstRow;
            while (at < end && ContributorRowRegex().Match(lines[at]) is { Success: true } m
                   && string.Compare(m.Groups["login"].Value, login, StringComparison.OrdinalIgnoreCase) < 0) at++;
            lines.Insert(at, row.ToString());
            end++;
            newRows.Add(row.ToString());
        }
        return true;
    }

    /// <summary>The body's bold lead phrase without its <c>**</c> and its closing period; a body with no bold lead gives
    /// its first sentence.</summary>
    static string LeadPhrase(string body)
    {
        string text = BodyLine(body);
        string lead;
        int close;
        if (text.StartsWith("**", StringComparison.Ordinal) && (close = text.IndexOf("**", 2, StringComparison.Ordinal)) > 2)
            lead = text[2..close];
        else
        {
            int stop = text.IndexOf(". ", StringComparison.Ordinal);
            lead = stop > 0 ? text[..stop] : text;
        }
        return lead.Trim().TrimEnd('.').TrimEnd();
    }

    /// <summary>A text file as lines, with the line ending and the BOM it was read with, so a rewrite changes only the
    /// lines that were edited. A file with mixed endings keeps them: it is split on its dominant ending only.</summary>
    sealed class TextFile
    {
        static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public required string Path { get; init; }
        public required bool Bom { get; init; }
        public required string NewLine { get; init; }
        public required List<string> Lines { get; init; }

        /// <summary>Reads <paramref name="path"/>; reports a missing or non-UTF-8 file itself.</summary>
        public static bool TryRead(string path, out TextFile file)
        {
            file = null!;
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("error: not found: " + path);
                return false;
            }
            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text;
            try
            {
                text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            }
            catch (DecoderFallbackException)
            {
                Console.Error.WriteLine("error: not UTF-8 text: " + path);
                return false;
            }
            string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            file = new TextFile { Path = path, Bom = bom, NewLine = nl, Lines = [.. text.Split(nl)] };
            return true;
        }

        public void Write()
        {
            byte[] body = StrictUtf8.GetBytes(string.Join(NewLine, Lines));
            using var fs = File.Create(Path);
            if (Bom) fs.Write([0xEF, 0xBB, 0xBF]);
            fs.Write(body);
        }
    }
}
