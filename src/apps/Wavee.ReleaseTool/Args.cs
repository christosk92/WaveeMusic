using System;
using System.Collections.Generic;

namespace Wavee.ReleaseTool;

/// <summary>
/// A hand-rolled <c>--key value</c> / <c>--flag</c> parser. Deliberately no package: this tool has exactly one
/// dependency (Wavee.Core) and the release script must be able to `dotnet run` it on a clean machine.
/// </summary>
sealed class Args
{
    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    Args(string command) => Command = command;

    /// <summary>The verb — <c>validate</c> or <c>render</c>. Empty when none was given.</summary>
    public string Command { get; }

    /// <summary>Unknown keys, reported so a typo is a usage error instead of a silent default.</summary>
    public List<string> Unknown { get; } = [];

    /// <summary>
    /// Parses <c>&lt;command&gt; [--key value] [--flag]</c>. A key whose next token starts with <c>--</c>
    /// (or that ends the line) is treated as a flag. Never throws.
    /// </summary>
    public static Args Parse(string[] argv)
    {
        string command = argv.Length > 0 && !argv[0].StartsWith("--", StringComparison.Ordinal) ? argv[0] : "";
        var a = new Args(command);
        for (int i = command.Length > 0 ? 1 : 0; i < argv.Length; i++)
        {
            string tok = argv[i];
            if (!tok.StartsWith("--", StringComparison.Ordinal)) { a.Unknown.Add(tok); continue; }
            string key = tok[2..];
            int eq = key.IndexOf('=');
            if (eq > 0) { a._values[key[..eq]] = key[(eq + 1)..]; continue; }
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                a._values[key] = argv[++i];
            else
                a._flags.Add(key);
        }
        return a;
    }

    /// <summary>The value for <paramref name="key"/>, or null when absent/empty.</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    /// <summary>The value for <paramref name="key"/>, or <paramref name="fallback"/>.</summary>
    public string GetOr(string key, string fallback) => Get(key) ?? fallback;

    /// <summary>True when <c>--key</c> was present with no value.</summary>
    public bool Flag(string key) => _flags.Contains(key);

    /// <summary>Records a missing required key into <paramref name="missing"/> and returns "" so parsing continues.</summary>
    public string Require(string key, List<string> missing)
    {
        string? v = Get(key);
        if (v is null) { missing.Add("--" + key); return ""; }
        return v;
    }

    /// <summary>Keys this tool understands; anything else is a usage error.</summary>
    public IReadOnlyList<string> UnknownKeys(params string[] known)
    {
        var bad = new List<string>();
        foreach (var k in _values.Keys)
            if (Array.FindIndex(known, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)) < 0) bad.Add("--" + k);
        foreach (var k in _flags)
            if (Array.FindIndex(known, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)) < 0) bad.Add("--" + k);
        return bad;
    }
}
