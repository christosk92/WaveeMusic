using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.Core;

namespace Wavee;

/// <summary>One row-level change between two membership snapshots. An add has <see cref="OldIndex"/> null; a remove has
/// <see cref="NewIndex"/> null; a move (or shift-displaced survivor) carries both.</summary>
public readonly record struct RowChange(string Key, int? OldIndex, int? NewIndex);

/// <summary>The keyed row diff between two tracklist snapshots (§4.6). <see cref="Moves"/> lists EVERY survivor whose
/// index changed — including rows merely displaced by an insert/remove above them (the FLIP pass needs those deltas; the
/// choreographer animates only the viewport-intersecting ones). Classification counts only STRUCTURAL changes
/// (adds+removes): a single insert at the top of a 1k-row playlist displaces 999 survivors but is one small edit, not a
/// reset. <see cref="IsReset"/> flags a curated re-cut (most content replaced) → the whole-list crossfade treatment.</summary>
public sealed record MembershipDelta(
    IReadOnlyList<RowChange> Adds,
    IReadOnlyList<RowChange> Removes,
    IReadOnlyList<RowChange> Moves,
    double RetainedFraction,
    bool IsReset)
{
    public bool IsEmpty => Adds.Count == 0 && Removes.Count == 0 && Moves.Count == 0;
}

/// <summary>Pure O(n) keyed diff over the read-model tracklist. Identity = <c>Track.ContextUid</c> (the playlist4 per-row
/// ItemId, stamped by JoinMembership — stable across reorders); rows without one fall back to <c>uri#occurrence</c>, so a
/// playlist with duplicate uris still diffs row-accurately. Works identically whether the new list came from an in-place
/// ops apply, a /diff, or a full refetch — the choreography never depends on which network path produced it.</summary>
public static class  MembershipDiff
{
    // A curated re-cut (editorial mixes): most of the old content gone, or a bulk edit too large to narrate row-by-row.
    const double ResetRetainedBelow = 0.5;
    const int ResetStructuralAbove = 40;

    public static MembershipDelta Diff(IReadOnlyList<Track> old, IReadOnlyList<Track> next)
    {
        var oldIndex = new Dictionary<string, int>(old.Count, StringComparer.Ordinal);
        var occ = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < old.Count; i++) oldIndex[KeyOf(old[i], occ)] = i;

        var adds = new List<RowChange>();
        var moves = new List<RowChange>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        occ.Clear();
        for (int i = 0; i < next.Count; i++)
        {
            var key = KeyOf(next[i], occ);
            if (oldIndex.TryGetValue(key, out int was) && consumed.Add(key))
            {
                if (was != i) moves.Add(new RowChange(key, was, i));
            }
            else adds.Add(new RowChange(key, null, i));
        }

        var removes = new List<RowChange>();
        foreach (var kv in oldIndex)
            if (!consumed.Contains(kv.Key)) removes.Add(new RowChange(kv.Key, kv.Value, null));
        removes.Sort((a, b) => a.OldIndex!.Value.CompareTo(b.OldIndex!.Value));   // dictionary order isn't positional

        int retained = consumed.Count;
        double retainedFraction = old.Count == 0 ? 1.0 : (double)retained / old.Count;
        int structural = adds.Count + removes.Count;
        // first fill (old empty) is a load, not an edit — never a reset, and the choreographer skips it anyway.
        bool isReset = old.Count > 0 && (retainedFraction < ResetRetainedBelow || structural > ResetStructuralAbove);

        return new MembershipDelta(adds, removes, moves, retainedFraction, isReset);
    }

    /// <summary>The per-row identity keys for one snapshot (same derivation the diff uses) — the choreographer's anchor
    /// scan and survivor maps.</summary>
    public static string[] Keys(IReadOnlyList<Track> list)
    {
        var occ = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new string[list.Count];
        for (int i = 0; i < list.Count; i++) keys[i] = KeyOf(list[i], occ);
        return keys;
    }

    // ContextUid (the stable playlist4 ItemId) when present; else uri + per-uri occurrence (duplicate uris stay distinct).
    static string KeyOf(Track t, Dictionary<string, int> occ)
    {
        if (t.ContextUid is { Length: > 0 } uid) return uid;
        int n = occ.TryGetValue(t.Uri, out var c) ? c + 1 : 0;
        occ[t.Uri] = n;
        return t.Uri + "#" + n;
    }

    /// <summary>The identity of ONE row, for per-row UI STATE that must not be shared between two rows carrying the same
    /// track (a playlist may legitimately hold a song twice, and a track uri then names two different rows). Same rule as
    /// the diff above, reachable without a whole snapshot: the <c>ContextUid</c> where the read model has one — the
    /// stable playlist4 ItemId that <c>StoreLibrarySource</c> stamps onto every membership-joined copy, and the same
    /// identity the reorder machinery moves rows by — else the uri qualified by the row's DISPLAY POSITION, which is the
    /// only thing distinguishing duplicates when no membership row exists behind them.
    /// <para>Position is the FALLBACK and never the primary. A sort or a filter reorders the list, so a purely
    /// index-keyed expansion would open a different song than the one that was clicked; with the uid leading, a real
    /// playlist keeps its drawer attached to the row across any re-sort, and only a uid-less list (an album, a chart —
    /// where duplicate uris do not occur) closes its drawer when the order changes.</para></summary>
    public static string RowKey(Track t, int displayIndex)
        => t.ContextUid is { Length: > 0 } uid ? uid
            : t.Uri.Length == 0 ? "" : t.Uri + "#@" + displayIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>Does <paramref name="key"/> (a <see cref="RowKey"/>) name THIS row? Compared in place rather than by
    /// building the key and throwing it away: the callers are bound re-skin closures that re-evaluate on every slot
    /// recycle, i.e. per row per scroll step.</summary>
    public static bool RowKeyMatches(string? key, Track t, int displayIndex)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (t.ContextUid is { Length: > 0 } uid) return string.Equals(key, uid, StringComparison.Ordinal);
        if (t.Uri.Length == 0) return false;
        var span = key.AsSpan();
        if (!span.StartsWith(t.Uri, StringComparison.Ordinal)) return false;
        var tail = span[t.Uri.Length..];
        return tail.Length > 2 && tail[0] == '#' && tail[1] == '@'
            && int.TryParse(tail[2..], NumberStyles.None, CultureInfo.InvariantCulture, out int i)
            && i == displayIndex;
    }
}
