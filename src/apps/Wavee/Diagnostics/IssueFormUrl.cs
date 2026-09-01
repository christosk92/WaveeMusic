using System;
using System.Collections.Generic;
using System.Text;

namespace Wavee;

/// <summary>Builds the prefilled GitHub issue-form / Discussions URL for one report — the small fields only; the
/// full redacted report never rides the URL (it goes to the clipboard and a saved file instead). GitHub rejects a
/// URL over roughly 8&#160;KB, so <see cref="Build"/> trims the least-essential long fields (in
/// <see cref="ReportChannel.TruncationOrder"/>) until the result fits <see cref="Budget"/>.</summary>
static class IssueFormUrl
{
    /// <summary>Comfortably under GitHub's ~8&#160;KB URL limit, leaving headroom for the browser and any proxy in
    /// between.</summary>
    public const int Budget = 7000;

    /// <summary>Assembles the URL for <paramref name="kind"/>. <paramref name="fields"/> is the channel's field ids
    /// (in any order) mapped to the reporter's already-typed answers; a dropdown id with a value that doesn't match
    /// one of its known options throws <see cref="ArgumentException"/> rather than silently sending GitHub a value
    /// it will just drop. Identity fields (<c>version</c>, <c>install-source</c>, <c>architecture</c>,
    /// <c>windows-version</c>) are taken from <paramref name="id"/> and never truncated.</summary>
    public static string Build(ReportKind kind, ReportIdentity id, string title, IReadOnlyList<KeyValuePair<string, string>> fields, IReadOnlyList<string> labels)
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

    /// <summary>The head plus <c>&amp;&lt;id&gt;=&lt;value&gt;</c> for every id in <see cref="ReportChannel.FieldIds"/>
    /// that has a non-empty value and is not one of the four identity ids already folded into <paramref name="head"/>.</summary>
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

    /// <summary>Dropdown fields must send GitHub one of its known option strings exactly — a mismatch (wrong case,
    /// trailing whitespace, a typo) is silently dropped by the form rather than rejected, so this catches it here.
    /// Non-dropdown ids, and an empty value for an optional dropdown, are never validated.</summary>
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
