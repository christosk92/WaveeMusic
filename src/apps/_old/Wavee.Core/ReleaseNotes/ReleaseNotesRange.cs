using System;
using System.Collections.Generic;

namespace Wavee.Core.ReleaseNotes;

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

    // ── version keys: four numeric parts + the pre-release ordinal ───────────────────────────────────────────────────
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
