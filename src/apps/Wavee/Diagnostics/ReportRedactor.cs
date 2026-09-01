using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Wavee;

/// <summary>Names <see cref="ReportRedactor.Redact"/> scrubs beyond its built-in patterns — everything the app
/// itself knows about this install and is not already generic enough to catch on its own (a Windows user name, the
/// machine name, the signed-in Spotify account, Spotify Connect device names on the LAN). All optional: a report
/// composed before login, or from a machine where none of this is known, passes <see cref="None"/>.</summary>
public sealed record RedactionRules(
    string? UserName,
    string? MachineName,
    string? SpotifyUserId,
    string? DisplayName,
    IReadOnlyList<string>? DeviceNames)
{
    /// <summary>No injected literals — only the built-in path/secret/network patterns fire.</summary>
    public static readonly RedactionRules None = new(null, null, null, null, null);
}

/// <summary>Whole-text scrubber for anything the app is about to hand to a stranger on GitHub: a crash report body,
/// a diagnostics dump, or a slice of <c>wavee.log</c>. <see cref="Redact"/> is ordered and idempotent — every
/// pattern's replacement is a placeholder token (<c>&lt;user&gt;</c>, <c>&lt;ip&gt;</c>, …) that none of the other
/// patterns can match, so running it twice is a no-op. AOT-safe: every pattern is a <see cref="GeneratedRegex"/>
/// source-generated matcher (precedent: <c>Wavee.Core.ReleaseNotes.ChangelogParser</c>), never
/// <see cref="Regex"/>'s reflection-based constructor.
/// <para>
/// This class is source-included into <c>Wavee.Tests</c> (see <c>Wavee.Tests.csproj</c>), so it — like the rest of
/// <c>Diagnostics/</c> — may reference only <c>System.*</c>.
/// </para></summary>
public static partial class ReportRedactor
{
    /// <summary>Windows user-profile paths: <c>C:\Users\bob\...</c> or <c>c:/users/Bob/...</c>. Keeps the drive
    /// letter and the "Users" segment (useful shape for triage) and only blanks the account name.</summary>
    [GeneratedRegex(@"(?i)([A-Z]:[\\/]+Users[\\/]+)([^\\/\s""'|<>]+)")]
    private static partial Regex ProfilePath();

    /// <summary>The <c>%USERPROFILE%</c> environment token, cmd- or PowerShell-spelled — collapses to one opaque
    /// placeholder rather than trying to preserve a path shape that was never expanded to begin with.</summary>
    [GeneratedRegex(@"(?i)%USERPROFILE%|\$env:USERPROFILE")]
    private static partial Regex ProfileVar();

    /// <summary>macOS/Linux-style home paths (<c>/Users/bob/Library/...</c>), for the rare report authored from a
    /// log captured on another platform or pasted in from elsewhere.</summary>
    [GeneratedRegex(@"(/Users/)([^/\s""'|<>]+)")]
    private static partial Regex MacProfile();

    /// <summary>A UNC host name (<c>\\NAS01\share\x</c> → <c>\\&lt;host&gt;\share\x</c>). The share and the rest of
    /// the path stay — only the machine identity is scrubbed. The <c>(?&lt;![\w:])</c> guard keeps this off a
    /// literal already preceded by a word character or colon (avoids double-matching inside a larger token).</summary>
    [GeneratedRegex(@"(?<![\w:])\\\\([^\\\s""'|<>]+)(\\)")]
    private static partial Regex UncHost();

    /// <summary>A Spotify account URI (<c>spotify:user:abc123</c>). Deliberately narrow to the <c>user</c> URI kind
    /// — <c>spotify:track:...</c>, <c>spotify:album:...</c> etc. are content identifiers, not identity, and must
    /// survive untouched so a report can still say what was playing.</summary>
    [GeneratedRegex(@"(spotify:user:)([^\s:""'|<>]+)")]
    private static partial Regex SpotifyUser();

    /// <summary>Any email address.</summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    /// <summary>An <c>Authorization: Bearer ...</c> token. Keeps the word "Bearer" so the shape of the header is
    /// still legible in a report; only the opaque token value is redacted.</summary>
    [GeneratedRegex(@"(?i)\b(bearer)\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex Bearer();

    /// <summary>Any <c>key=value</c> or <c>key: value</c> pair whose key names a secret or credential. Requires an
    /// <c>=</c> or <c>:</c> immediately (whitespace aside) after the key word, so prose that merely uses the word —
    /// "the key of C major", "ask the user" — never fires. A value that is the word <c>Bearer</c> (an
    /// <c>Authorization: Bearer …</c> header, already handled by <see cref="Bearer"/>) is left to that rule.</summary>
    [GeneratedRegex(@"(?i)\b(access_?token|refresh_?token|id_?token|token|api_?key|key|auth|authorization|password|pwd|secret|cookie|set-cookie|session_?id)\s*[=:]\s*(""?)(?!bearer\b)([^\s&""|,;<>]+)")]
    private static partial Regex KeyValueSecret();

    /// <summary>Account facts that are not secrets but are still account-identifying in aggregate (country, Premium
    /// tier, catalogue). Same <c>key=value</c>/<c>key: value</c> shape and same word-boundary discipline as
    /// <see cref="KeyValueSecret"/>.</summary>
    [GeneratedRegex(@"(?i)\b(country|product|product_?tier|tier|catalogue)\s*[=:]\s*(""?)([^\s""|,;<>]+)")]
    private static partial Regex CountryProduct();

    /// <summary>An IPv4 address — but not a version quad. <c>version=</c>/<c>quad=</c>/<c>build=</c>/<c>from</c>/
    /// <c>to</c> immediately before the number, or the number sitting directly inside parentheses (the
    /// "<c>0.2.5 Breaker (0.2.5.6)</c>" release-stamp shape), both suppress the match — .NET's regex engine allows
    /// variable-length lookbehind, so the keyword guard is exact rather than an approximation.</summary>
    [GeneratedRegex(@"(?<!(?:version|quad|build|from|to)\s*[=:( ]\s*)(?<!\()(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])")]
    private static partial Regex IPv4();

    /// <summary>An IPv6 address, compressed (<c>2001:db8::ff00:42:8329</c>, <c>fe80::1%12</c> with a zone id) or not.
    /// The uncompressed branch requires at least one hex letter (<c>a</c>-<c>f</c>) somewhere in the run so an
    /// all-digit colon-separated string — a timestamp (<c>12:34:56.789</c>) or a <c>seq:tid</c>-shaped log field —
    /// never matches; the compressed branch instead keys off the literal <c>::</c>, which neither of those ever
    /// contains, so no letter requirement is needed there. <c>::1</c> (loopback, no leading group) is its own
    /// branch.</summary>
    [GeneratedRegex(@"(?i)\b(?=(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b)(?=[0-9:]*[a-f])(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b|\b(?:[0-9a-f]{1,4}:){1,6}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,5})?(?:%[0-9a-zA-Z]+)?\b|(?<![\w:])::1\b")]
    private static partial Regex IPv6();

    /// <summary>A MAC address, colon- or hyphen-delimited (<c>AA-BB-CC-DD-EE-FF</c>).</summary>
    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex Mac();

    /// <summary>A Spotify Connect / hardware device id: <c>deviceId=...</c> or <c>device_id: ...</c> followed by 16+
    /// hex characters.</summary>
    [GeneratedRegex(@"(?i)\b(device_?id)\s*[=:]\s*([0-9a-f]{16,})")]
    private static partial Regex DeviceId();

    /// <summary>Runs every pattern over <paramref name="text"/> in a fixed order, then the caller-supplied
    /// <paramref name="rules"/> literals. Every replacement is a placeholder token none of the other patterns can
    /// themselves match, so <c>Redact(Redact(x), rules) == Redact(x, rules)</c> — calling this twice on the same
    /// text (e.g. once on a cached body, again after appending fresh keystrokes) never double-redacts.</summary>
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

    /// <summary>Ordinal, case-insensitive literal replace. Skipped under 3 characters — a two- or one-letter user
    /// name (not unusual for a short Windows account name) would otherwise erase that string wherever it appears in
    /// prose, not just as an identity.</summary>
    static string Literal(string s, string? value, string placeholder)
        => value is { Length: >= 3 } v ? s.Replace(v, placeholder, StringComparison.OrdinalIgnoreCase) : s;
}
