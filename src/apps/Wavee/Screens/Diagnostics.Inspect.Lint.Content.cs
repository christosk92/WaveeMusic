// ── Screens/Diagnostics.Inspect.Lint.Content.cs — the report-only content rules: text.placeholder, key.hygiene ───────────────────
// wrong words on screen (a missing string, a missing argument, NaN), and keys that break every selector an agent or a rule writes
//
// Role: CORE (pure: reads a prepared `InspectLint.Snapshot`). Level `todo` (reported, never counted in the summary). Spec: PLAN 5.3
// (text.placeholder, key.hygiene); design-qa G6; the owner rule "never put a tier, form or column count in a node key".
//
// WHAT LOC RENDERS. `Loc.Get` shows a missing key as `[the.key]` and `Loc.Format` a missing argument as `{name}` (engine
// Localization.cs `Get`/`Format`), on purpose loud: either on screen is a bug, whatever the page.

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class InspectLint
    {
        // ── text.placeholder ────────────────────────────────────────────────────────────────────────────────────────

        static bool KeyChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';

        /// <summary>A missing loc key as `Loc.Get` renders it: <c>[a.b]</c>, word characters and at least one dot inside the brackets.</summary>
        public static bool TryMissingKey(string text, out string token)
        {
            token = "";
            for (int open = text.IndexOf('['); open >= 0; open = text.IndexOf('[', open + 1))
            {
                int close = text.IndexOf(']', open + 1);
                if (close < 0) return false;
                ReadOnlySpan<char> inner = text.AsSpan(open + 1, close - open - 1);
                if (inner.Length < 3 || inner.IndexOf('.') <= 0 || inner[^1] == '.') continue;
                bool ok = true;
                foreach (char c in inner)
                    if (!KeyChar(c)) { ok = false; break; }
                if (!ok) continue;
                token = text[open..(close + 1)];
                return true;
            }
            return false;
        }

        /// <summary>A missing argument as `Loc.Format` renders it: <c>{name}</c>, an identifier inside the braces.</summary>
        public static bool TryMissingArg(string text, out string token)
        {
            token = "";
            for (int open = text.IndexOf('{'); open >= 0; open = text.IndexOf('{', open + 1))
            {
                int close = text.IndexOf('}', open + 1);
                if (close < 0) return false;
                ReadOnlySpan<char> inner = text.AsSpan(open + 1, close - open - 1);
                if (inner.Length == 0 || !(char.IsAsciiLetter(inner[0]) || inner[0] == '_')) continue;
                bool ok = true;
                foreach (char c in inner)
                    if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) { ok = false; break; }
                if (!ok) continue;
                token = text[open..(close + 1)];
                return true;
            }
            return false;
        }

        static readonly string[] s_junkWords = ["NaN", "Infinity", "-Infinity", "null", "undefined"];

        /// <summary>A value that leaked as words: NaN, Infinity, null, undefined, as a whole word.</summary>
        public static bool TryJunkWord(string text, out string token)
        {
            token = "";
            foreach (string word in s_junkWords)
            {
                for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
                {
                    bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                    int end = at + word.Length;
                    bool endOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
                    if (!startOk || !endOk) continue;
                    token = word;
                    return true;
                }
            }
            return false;
        }

        /// <summary><c>text.placeholder</c>: in-scope words that are not content. A missing loc key (<c>[a.b.c]</c>) or a missing argument
        /// (<c>{name}</c>) is an error; NaN, Infinity, null or undefined as a word is a warning. Grouped by (kind, family pattern).</summary>
        static int PlaceholderRule(Snapshot s, List<Finding> into)
        {
            int looked = 0;
            var groups = new Dictionary<string, (Severity Severity, string Kind, string Token, List<Node> Nodes)>(StringComparer.Ordinal);
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Text is not { } t || t.Glyph || t.String.Length == 0) continue;
                looked++;
                string kind, token;
                Severity sev;
                if (TryMissingKey(t.String, out token)) { kind = "missing-key"; sev = Severity.Error; }
                else if (TryMissingArg(t.String, out token)) { kind = "missing-arg"; sev = Severity.Error; }
                else if (TryJunkWord(t.String, out token)) { kind = "value-" + token.TrimStart('-').ToLowerInvariant(); sev = Severity.Warn; }
                else continue;
                string key = kind + "|" + FamilyPattern(s, n);
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = (sev, kind, token, []);
                g.Nodes.Add(n);
            }
            foreach (var (key, g) in groups)
            {
                var (refs, rects) = Members(g.Nodes);
                string pattern = key[(key.IndexOf('|') + 1)..];
                string what = g.Kind switch
                {
                    "missing-key" => $"a missing loc key {g.Token}",
                    "missing-arg" => $"a missing argument {g.Token}",
                    _ => $"a leaked value '{g.Token}'",
                };
                into.Add(new Finding(Rules.TextPlaceholder, g.Severity,
                    $"{what} is on screen in {pattern}: {Q(g.Nodes[0].Text!.String)}{(g.Nodes.Count > 1 ? $" and {g.Nodes.Count - 1} more" : "")}", refs)
                {
                    Measured = [M("count", g.Nodes.Count)],
                    Pattern = pattern,
                    Cause = g.Kind,
                    Count = g.Nodes.Count,
                    Rects = rects,
                });
            }
            return looked;
        }

        // ── key.hygiene ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A key as text a person can read: control characters as <c>\xNN</c>.</summary>
        public static string Printable(string key)
        {
            bool clean = true;
            foreach (char c in key)
                if (char.IsControl(c)) { clean = false; break; }
            if (clean) return key;
            var b = new StringBuilder(key.Length + 8);
            foreach (char c in key)
                if (char.IsControl(c)) b.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                else b.Append(c);
            return b.ToString();
        }

        static readonly string[] s_tierWords = ["compact", "small", "standard", "huge", "huge4k"];

        /// <summary>A key part that names a tier, a form or a column count (the owner rule): a tier name, <c>form</c>, or a count like <c>3col</c>,
        /// <c>cols4</c>, <c>2up</c>.</summary>
        public static bool TryTierWord(string key, out string word)
        {
            word = "";
            int start = 0;
            for (int i = 0; i <= key.Length; i++)
            {
                if (i < key.Length && char.IsAsciiLetterOrDigit(key[i])) continue;
                if (i > start)
                {
                    string part = key[start..i].ToLowerInvariant();
                    if (Array.IndexOf(s_tierWords, part) >= 0 || part == "form" || IsCountWord(part)) { word = key[start..i]; return true; }
                }
                start = i + 1;
            }
            return false;
        }

        static bool IsCountWord(string part)
        {
            int digits = 0;
            while (digits < part.Length && char.IsAsciiDigit(part[digits])) digits++;
            if (digits > 0) return part[digits..] is "col" or "cols" or "column" or "columns" or "up";
            foreach (string head in (ReadOnlySpan<string>)["cols", "columns", "col"])
                if (part.StartsWith(head, StringComparison.Ordinal) && part.Length > head.Length && part[head.Length..].All(char.IsAsciiDigit)) return true;
            return false;
        }

        /// <summary>A run id: <c>#</c> and three or more digits (<c>more#855313</c>): it changes every run, so no selector can name it.</summary>
        public static bool HasRunId(string key)
        {
            for (int at = key.IndexOf('#'); at >= 0; at = key.IndexOf('#', at + 1))
            {
                int digits = 0;
                while (at + 1 + digits < key.Length && char.IsAsciiDigit(key[at + 1 + digits])) digits++;
                if (digits >= 3) return true;
            }
            return false;
        }

        /// <summary>The instance of a repeated family other than <paramref name="family"/> nearest above <paramref name="n"/> (-1: none).</summary>
        static int OtherInstanceAbove(Snapshot s, Node n, string family)
        {
            for (int i = n.Parent; i >= 0; i = s.Nodes[i].Parent)
            {
                Node m = s.Nodes[i];
                if (m.Instance == i && !string.Equals(m.Family, family, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>An ancestor of <paramref name="n"/> carries the same key.</summary>
        static bool NestedInSameKey(Snapshot s, Node n, string key)
        {
            for (int i = n.Parent; i >= 0; i = s.Nodes[i].Parent)
                if (string.Equals(s.Nodes[i].Key, key, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary><c>key.hygiene</c> (design-qa G6): in-scope keys that make a selector ambiguous or unstable, one finding per (class, key family):
        /// control characters; a trailing <c>:</c>; a run id (<c>#</c> + digits); a tier, form or column-count word (the owner rule); and the same
        /// key on two or more nodes (not counting a copy nested in another) OUTSIDE a repeated family: neither the family's own instances (rows keyed
        /// alike) nor cells of which every copy but one sits in a different row (told apart by `root=<row key>`) count; the rest only `nth` tells apart.</summary>
        static int KeyHygieneRule(Snapshot s, List<Finding> into)
        {
            int looked = 0;
            var groups = new Dictionary<string, (string Class, string Detail, List<Node> Nodes, HashSet<string> Keys)>(StringComparer.Ordinal);
            var byKey = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
            void Add(string cls, string detail, Node n)
            {
                string key = cls + "|" + PatternOfKey(n.Key!);
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = (cls, detail, [], new HashSet<string>(StringComparer.Ordinal));
                g.Nodes.Add(n);
                g.Keys.Add(Printable(n.Key!));
            }
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Key is not { Length: > 0 } key) continue;
                looked++;
                bool control = false;
                foreach (char c in key)
                    if (char.IsControl(c)) { control = true; break; }
                if (control) Add("control", "a control character", n);
                if (key.EndsWith(':')) Add("trailing-colon", "a trailing ':'", n);
                if (HasRunId(key)) Add("run-id", "a run id", n);
                if (TryTierWord(key, out string word)) Add("tier-word", "the tier, form or count word '" + word + "'", n);
                if (!byKey.TryGetValue(key, out List<Node>? copies)) byKey[key] = copies = [];
                copies.Add(n);
            }
            foreach (var (key, all) in byKey)
            {
                if (all.Count < 2) continue;
                // A copy nested in another copy repeats its anchor's key (a component's key and its root's): the outer one is what a selector finds.
                var copies = all.FindAll(n => !NestedInSameKey(s, n, key));
                if (copies.Count < 2) continue;
                if (copies.TrueForAll(static n => n.Instance == n.Index)) continue;   // a repeated family: its rows share the key by design
                string family = FamilyOfKey(key);
                var rows = new HashSet<int>();
                int loose = 0;
                foreach (Node n in copies)
                {
                    int row = OtherInstanceAbove(s, n, family);
                    if (row < 0 || !rows.Add(row)) loose++;
                }
                if (loose <= 1) continue;   // each copy but one is a cell of its own row: `root=<row key>` tells them apart
                foreach (Node n in copies) Add("duplicate", copies.Count.ToString(CultureInfo.InvariantCulture) + " nodes share it", n);
            }
            foreach (var (gkey, g) in groups)
            {
                var (refs, rects) = Members(g.Nodes);
                string pattern = gkey[(gkey.IndexOf('|') + 1)..];
                string keys = string.Join(", ", g.Keys.Take(4).Select(static k => "'" + k + "'")) + (g.Keys.Count > 4 ? ", ..." : "");
                string message = g.Class == "duplicate"
                    ? $"{keys}: the same key is on {g.Nodes.Count} on-screen nodes that no row tells apart (a selector needs nth)"
                    : $"{keys}: {(g.Keys.Count == 1 ? "the key carries" : "these keys carry")} {g.Detail}";
                into.Add(new Finding(Rules.KeyHygiene, Severity.Warn, message, refs.Select(Printable).ToList())
                {
                    Measured = [M("count", g.Nodes.Count), M("keys", g.Keys.Count)],
                    Pattern = pattern,
                    Cause = g.Class,
                    Count = g.Nodes.Count,
                    Rects = rects,
                });
            }
            return looked;
        }
    }
}
