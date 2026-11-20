using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wavee.ReleaseTool;

/// <summary>One change file of <c>changes/unreleased/</c> (the format is <c>changes/README.md</c>): the front matter, the
/// body (the CHANGELOG line) and everything wrong with it. A file is read as far as it goes, so one run names every
/// mistake instead of the first.</summary>
sealed class ChangeFile
{
    /// <summary>Relative to the repo root, forward slashes: <c>changes/unreleased/226-heart.md</c>.</summary>
    public string RelativePath { get; init; } = "";
    public string FullPath { get; init; } = "";
    /// <summary>added | changed | fixed | removed as written; null when the key is missing.</summary>
    public string? Kind { get; set; }
    /// <summary>The issues the change closes, in the order written, without repeats.</summary>
    public List<int> Issues { get; } = [];
    /// <summary>A community contributor's GitHub login, or null for the owner's own change.</summary>
    public string? By { get; set; }
    /// <summary>none | additive | breaking as written (none when the key is missing).</summary>
    public string Contract { get; set; } = "none";
    public string ContractNote { get; set; } = "";
    /// <summary>Everything after the front matter, trimmed.</summary>
    public string Body { get; set; } = "";
    public List<string> Problems { get; } = [];
}

/// <summary>Reads and checks the change files, and the <c>changes check</c> command.</summary>
static partial class ChangeFiles
{
    /// <summary>Where the change files wait for the next release, relative to the repo root.</summary>
    public const string Folder = "changes/unreleased";

    /// <summary>The CHANGELOG subsections a change file can land in, in the order the release section lists them.</summary>
    public static readonly string[] Kinds = ["added", "changed", "removed", "fixed"];

    static readonly string[] Contracts = ["none", "additive", "breaking"];
    static readonly string[] Keys = ["kind", "issues", "by", "contract", "contract-note"];
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex LoginRegex();

    /// <summary>0 for none, 1 for additive, 2 for breaking: the highest decides the release's contract bump.</summary>
    public static int ContractRank(string contract) => contract switch { "breaking" => 2, "additive" => 1, _ => 0 };

    /// <summary>Every <c>*.md</c> in <c>changes/unreleased/</c>, in name order, each with its problems. <c>.gitkeep</c> and
    /// other files are skipped. <paramref name="problems"/> gets what is wrong with the folder itself.</summary>
    public static List<ChangeFile> ReadAll(string root, List<string> problems)
    {
        var files = new List<ChangeFile>();
        string dir = Path.Combine(root, "changes", "unreleased");
        if (!Directory.Exists(dir))
        {
            problems.Add(Folder + "/ does not exist");
            return files;
        }

        var paths = new List<string>();
        foreach (var p in Directory.GetFiles(dir))
            if (Path.GetExtension(p).Equals(".md", StringComparison.OrdinalIgnoreCase)) paths.Add(p);
        paths.Sort(static (a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));

        foreach (var full in paths)
        {
            string rel = Folder + "/" + Path.GetFileName(full);
            string text;
            try
            {
                text = StrictUtf8.GetString(File.ReadAllBytes(full));
            }
            catch (DecoderFallbackException)
            {
                var bad = new ChangeFile { RelativePath = rel, FullPath = full };
                bad.Problems.Add("not UTF-8 text");
                files.Add(bad);
                continue;
            }
            files.Add(Parse(rel, full, text));
        }
        return files;
    }

    /// <summary>Parses one change file. The front matter is <c>key: value</c> lines between two <c>---</c> lines, with
    /// <c>#</c> comments; a value may be quoted; <c>issues</c> is <c>[n, m]</c> or <c>[]</c>.</summary>
    public static ChangeFile Parse(string relativePath, string fullPath, string text)
    {
        var f = new ChangeFile { RelativePath = relativePath, FullPath = fullPath };
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        int i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        if (i == lines.Length || lines[i].Trim() != "---")
        {
            f.Problems.Add("no front matter: the file must start with a '---' line");
            return f;
        }
        int open = i;
        int close = -1;
        for (i = open + 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") { close = i; break; }
        if (close < 0)
        {
            f.Problems.Add("the front matter has no closing '---' line");
            return f;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int n = open + 1; n < close; n++)
        {
            string line = StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            string where = "line " + (n + 1).ToString(CultureInfo.InvariantCulture);
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                f.Problems.Add($"{where}: '{line}' is not 'key: value'");
                continue;
            }
            string key = line[..colon].Trim();
            if (!TryUnquote(line[(colon + 1)..].Trim(), out string value))
            {
                f.Problems.Add($"{where}: the value of '{key}' opens a quote it never closes");
                continue;
            }
            if (Array.IndexOf(Keys, key) < 0)
            {
                f.Problems.Add($"{where}: unknown key '{key}' (known: {string.Join(", ", Keys)})");
                continue;
            }
            if (!seen.Add(key))
            {
                f.Problems.Add($"{where}: '{key}' is given twice");
                continue;
            }
            switch (key)
            {
                case "kind": f.Kind = value; break;
                case "issues": ParseIssues(value, f, where); break;
                case "by": f.By = value.Length == 0 ? null : value; break;
                case "contract": f.Contract = value.Length == 0 ? "none" : value; break;
                case "contract-note": f.ContractNote = value; break;
            }
        }

        var body = new StringBuilder();
        for (int n = close + 1; n < lines.Length; n++) body.Append(lines[n]).Append('\n');
        f.Body = body.ToString().Trim();
        Check(f);
        if (f.Body.Length == 0)
            f.Problems.Add("the body is empty: it is the CHANGELOG line (a bold lead phrase, then plain detail)");
        return f;
    }

    /// <summary>The rules on the values, after the front matter has been read.</summary>
    static void Check(ChangeFile f)
    {
        if (f.Kind is null or { Length: 0 })
        {
            f.Kind = null;
            f.Problems.Add("kind is missing (added, changed, fixed or removed)");
        }
        else if (Array.IndexOf(Kinds, f.Kind) < 0)
            f.Problems.Add($"kind '{f.Kind}' is not added, changed, fixed or removed");

        if (f.By is { } by)
        {
            if (by.StartsWith('@'))
                f.Problems.Add($"by '{by}': write the login without the '@'");
            else if (!LoginRegex().IsMatch(by))
                f.Problems.Add($"by '{by}' is not a GitHub login (letters, digits and '-', at most 39, not starting with '-')");
        }

        if (Array.IndexOf(Contracts, f.Contract) < 0)
            f.Problems.Add($"contract '{f.Contract}' is not none, additive or breaking");
        else if (f.Contract != "none" && f.ContractNote.Trim().Length == 0)
            f.Problems.Add($"contract is {f.Contract} but contract-note is empty (one line: what was added, removed or changed)");
    }

    static void ParseIssues(string value, ChangeFile f, string where)
    {
        if (value.Length == 0) return;   // `issues:` with nothing after it reads as no issues
        if (!value.StartsWith('[') || !value.EndsWith(']'))
        {
            f.Problems.Add($"{where}: issues must be a list, [225, 226] or [] (got '{value}')");
            return;
        }
        string inner = value[1..^1].Trim();
        if (inner.Length == 0) return;
        foreach (var part in inner.Split(','))
        {
            string t = part.Trim();
            if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0)
            {
                if (!f.Issues.Contains(n)) f.Issues.Add(n);
            }
            else if (t.StartsWith('#'))
                f.Problems.Add($"{where}: issue '{t}': write the number without the '#'");
            else
                f.Problems.Add($"{where}: issue '{t}' is not a positive issue number");
        }
    }

    /// <summary>Cuts a <c>#</c> comment off a line: a <c>#</c> outside quotes, at the start or after white space.</summary>
    static string StripComment(string line)
    {
        char quote = '\0';
        for (int j = 0; j < line.Length; j++)
        {
            char c = line[j];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"') { j++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '#' && (j == 0 || char.IsWhiteSpace(line[j - 1]))) return line[..j];
        }
        return line;
    }

    /// <summary>A double-quoted value takes <c>\"</c> and <c>\\</c>; a single-quoted one takes <c>''</c>. False when a quote
    /// opens and never closes.</summary>
    static bool TryUnquote(string raw, out string value)
    {
        value = raw;
        if (raw.Length == 0 || raw[0] is not ('"' or '\'')) return true;
        char q = raw[0];
        if (raw.Length < 2 || raw[^1] != q) return false;
        string inner = raw[1..^1];
        value = q == '"'
            ? inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
            : inner.Replace("''", "'", StringComparison.Ordinal);
        return true;
    }

    /// <summary><c>changes check --root &lt;repo&gt; [--json]</c>: exit 0 when every change file is valid (none is valid
    /// too), 2 when anything is wrong, 1 on a usage error.</summary>
    public static int Run(Args a)
    {
        var bad = a.UnknownKeys("root", "json");
        if (bad.Count > 0) { Console.Error.WriteLine("error: unknown option(s): " + string.Join(", ", bad)); Program.PrintUsage(); return Validator.ExitUsage; }
        if (a.Positional is not ["check"])
        {
            Console.Error.WriteLine("error: 'changes' takes one verb: changes check --root <repo> [--json]");
            Program.PrintUsage();
            return Validator.ExitUsage;
        }
        if (!TryRoot(a, out string root)) return Validator.ExitUsage;

        var folderProblems = new List<string>();
        var files = ReadAll(root, folderProblems);
        int count = folderProblems.Count;
        int badFiles = 0;
        foreach (var f in files)
        {
            count += f.Problems.Count;
            if (f.Problems.Count > 0) badFiles++;
        }

        if (a.Flag("json"))
        {
            Console.Out.WriteLine(Json(w =>
            {
                w.WriteStartObject();
                w.WriteBoolean("ok", count == 0);
                w.WriteStartArray("files");
                foreach (var f in files)
                {
                    w.WriteStartObject();
                    w.WriteString("path", f.RelativePath);
                    w.WriteString("kind", f.Kind);
                    w.WriteStartArray("issues");
                    foreach (var n in f.Issues) w.WriteNumberValue(n);
                    w.WriteEndArray();
                    w.WriteString("by", f.By);
                    w.WriteString("contract", f.Contract);
                    w.WriteString("contractNote", f.ContractNote);
                    WriteStrings(w, "problems", f.Problems);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                WriteStrings(w, "problems", folderProblems);
                w.WriteEndObject();
            }));
        }
        else
        {
            foreach (var p in folderProblems) Console.Error.WriteLine("error: " + p);
            foreach (var f in files)
                foreach (var p in f.Problems) Console.Error.WriteLine("error: " + f.RelativePath + ": " + p);
            if (count > 0)
                Console.Error.WriteLine($"error: {count} problem(s) in {badFiles} of {files.Count} change file(s) in {Folder}/");
            else if (files.Count == 0)
                Console.WriteLine($"ok: no change files in {Folder}/");
            else
                Console.WriteLine($"ok: {files.Count} change file(s) in {Folder}/, all valid");
        }
        return count == 0 ? Validator.ExitOk : Validator.ExitInvalid;
    }

    /// <summary><c>--root</c>, required, an existing folder; reports the usage error itself.</summary>
    public static bool TryRoot(Args a, out string root)
    {
        root = "";
        if (a.Get("root") is not { } r)
        {
            Console.Error.WriteLine("error: missing required argument: --root <repo>");
            Program.PrintUsage();
            return false;
        }
        root = Path.GetFullPath(r);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine("error: --root not found: " + root);
            return false;
        }
        return true;
    }

    /// <summary>One line of compact JSON. A character outside ASCII (it can only sit inside a string) is written as a
    /// <c>\uXXXX</c> escape, so the runner reads the same text whatever the console's code page; apostrophes stay
    /// readable.</summary>
    public static string Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(w);
        string text = Encoding.UTF8.GetString(ms.ToArray());
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c < 0x7F) sb.Append(c);
            else sb.Append(@"\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }
}
