using System;
using System.Collections.Generic;
using System.Text;

namespace Wavee.Core.ReleaseNotes;

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
