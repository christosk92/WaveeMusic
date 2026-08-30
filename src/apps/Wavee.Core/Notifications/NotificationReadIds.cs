using System;
using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>The PER-ITEM half of the notification center's read state, as a set codec over one persisted string.
///
/// <para>The remote feeds (gander + what's-new) have no server mark-read endpoint, so read state is local. Until now it
/// was a single WATERMARK per feed ("everything at or before this instant is seen"), advanced by Mark all read — which
/// is all a panel that marks everything on open ever needed. A surface that marks ONE row seen (Home's timeline) needs
/// the other half, and it must be the SAME store, not a second one: whatever marks a row read here is what both the
/// panel and the bell badge read back.</para>
///
/// <para>Bounded by construction: the set is trimmed to <see cref="Cap"/> ids, oldest-first, and it is CLEARED whenever
/// the watermark advances (a mark-all subsumes every individual id), so it cannot grow without limit.</para>
///
/// <para>Scans in place, no split and no allocation on the read path — <see cref="Contains"/> runs once per item per
/// rebuild. Follows <c>WaveeTipsCore</c>'s codec shape deliberately; it is not shared with it because a tip id is a
/// dotted ASCII constant this app authored, while these are opaque SERVER ids and need the separator guard + the cap.</para>
/// </summary>
public static class NotificationReadIds
{
    /// <summary>The set separator. An id containing it is refused by <see cref="Add"/> rather than corrupting the set.</summary>
    public const char Separator = '\n';

    /// <summary>How many individually-marked ids survive. Well past a session's worth of clicks on a 20-item feed.</summary>
    public const int Cap = 200;

    /// <summary>True when <paramref name="id"/> has been individually marked read.</summary>
    public static bool Contains(string? set, string? id)
    {
        if (string.IsNullOrEmpty(set) || string.IsNullOrEmpty(id)) return false;
        int i = 0;
        while (i <= set.Length)
        {
            int end = set.IndexOf(Separator, i);
            if (end < 0) end = set.Length;
            if (end - i == id.Length && string.CompareOrdinal(set, i, id, 0, id.Length) == 0) return true;
            i = end + 1;
        }
        return false;
    }

    /// <summary>The set with <paramref name="id"/> added — idempotent (an already-present id returns the input
    /// unchanged, so re-marking never grows the string), append-ordered, and trimmed to <see cref="Cap"/> from the
    /// OLDEST end. An empty id, or one containing the separator, is refused.</summary>
    public static string Add(string? set, string? id)
    {
        if (id is not { Length: > 0 } || id.IndexOf(Separator) >= 0) return set ?? "";
        if (Contains(set, id)) return set!;
        string next = string.IsNullOrEmpty(set) ? id : set + Separator + id;
        return Trim(next);
    }

    /// <summary>The ids in stored order, empty segments dropped. For tests and diagnostics — the hot path is
    /// <see cref="Contains"/>.</summary>
    public static List<string> Parse(string? set)
    {
        var ids = new List<string>();
        if (string.IsNullOrEmpty(set)) return ids;
        int i = 0;
        while (i <= set.Length)
        {
            int end = set.IndexOf(Separator, i);
            if (end < 0) end = set.Length;
            if (end > i) ids.Add(set.Substring(i, end - i));
            i = end + 1;
        }
        return ids;
    }

    /// <summary>Drop the oldest ids until at most <see cref="Cap"/> remain. Returns the input when it already fits.</summary>
    public static string Trim(string? set)
    {
        if (string.IsNullOrEmpty(set)) return "";
        int count = 1;
        for (int i = 0; i < set.Length; i++) if (set[i] == Separator) count++;
        if (count <= Cap) return set;

        int drop = count - Cap;
        int cut = 0;
        for (int i = 0; i < drop; i++)
        {
            int end = set.IndexOf(Separator, cut);
            if (end < 0) return "";
            cut = end + 1;
        }
        return set[cut..];
    }
}
