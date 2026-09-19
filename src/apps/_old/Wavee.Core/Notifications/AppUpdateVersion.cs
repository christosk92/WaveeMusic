using System;

namespace Wavee.Core;

/// <summary>
/// The engine-free version arithmetic behind <see cref="IAppUpdateService"/>: is the feed's version newer than ours,
/// and did this launch follow an update? Pure string/int math with no I/O, so it is unit-tested directly rather than
/// through the HTTP-shaped service that uses it.
/// </summary>
public static class AppUpdateVersion
{
    /// <summary>
    /// True when <paramref name="remote"/> is strictly newer than <paramref name="current"/>.
    /// <para>
    /// Both sides are normalized first: a leading <c>v</c>, SemVer build metadata (<c>+…</c>) and a pre-release suffix
    /// (<c>-dev</c>, <c>-rc.1</c>, …) are stripped, then up to four dot-separated numeric parts are compared with
    /// missing parts treated as 0 — so <c>0.1.2</c> and <c>0.1.2.0</c> are the same version, and <c>0.1.2.1</c> beats
    /// both. Anything that does not parse that way on EITHER side returns false: an unstamped local build (<c>dev</c>)
    /// or a malformed feed must never produce an "update available" prompt.
    /// </para>
    /// </summary>
    public static bool IsNewer(string? remote, string? current)
    {
        if (!TryParse(remote, out var r)) return false;
        if (!TryParse(current, out var c)) return false;
        for (int i = 0; i < 4; i++)
        {
            if (r[i] > c[i]) return true;
            if (r[i] < c[i]) return false;
        }
        return false;
    }

    /// <summary>
    /// The startup "you were updated" rule: true when the previous run recorded a DIFFERENT version than the one now
    /// running. A first-ever launch (<paramref name="lastRunVersion"/> empty) is NOT an update — a fresh install must
    /// not greet the user with an update notice.
    /// </summary>
    public static bool IsFirstRunAfterUpdate(string? lastRunVersion, string? currentVersion)
    {
        if (string.IsNullOrEmpty(lastRunVersion)) return false;
        if (string.IsNullOrEmpty(currentVersion)) return false;
        return !string.Equals(lastRunVersion, currentVersion, StringComparison.Ordinal);
    }

    /// <summary>The release-notes tag for a version: the first three numeric parts (<c>0.1.1.42</c> → <c>0.1.1</c>).
    /// Falls back to the normalized input when it does not parse.</summary>
    public static string ReleaseTagVersion(string? version)
    {
        string norm = Normalize(version);
        if (!TryParse(version, out var v)) return norm;
        return v[0] + "." + v[1] + "." + v[2];
    }

    static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        string s = version.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return s;
    }

    static bool TryParse(string? version, out int[] parts)
    {
        parts = new int[4];
        string s = Normalize(version);
        if (s.Length == 0) return false;

        int index = 0;
        int start = 0;
        while (start <= s.Length)
        {
            int dot = s.IndexOf('.', start);
            int end = dot < 0 ? s.Length : dot;
            if (end == start) return false;                       // empty segment ("1..2", "1.")
            if (index >= 4) return false;                          // more than four parts is not a version we know
            if (!int.TryParse(s.AsSpan(start, end - start), System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out int value))
                return false;                                      // non-numeric segment ("dev", "1.x")
            parts[index++] = value;
            if (dot < 0) break;
            start = dot + 1;
        }
        return index > 0;
    }
}
