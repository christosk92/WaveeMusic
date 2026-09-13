namespace Wavee.Core;

/// <summary>What a grouped recents page says about WHEN each entity was last played, in the shape PlayLogStore.MergeRecency
/// takes. A Single row is its own uri; a Group row is its context (the header uri) plus every collapsed member — the
/// members are the only complete account of which plays the card stands for (RecentsList.Group). Max-merge downstream
/// makes duplicates and out-of-order rows harmless.</summary>
public static class RecentsRecency
{
    /// <summary>Every uri a snapshot names, stamped with when it was played. Cross-device plays only exist here — the
    /// local play-log ring never sees a play made on another device — so this is the one path that lets "Recents" in
    /// the library panes reflect what you listened to on your phone.</summary>
    public static List<KeyValuePair<string, long>> Stamps(IReadOnlyList<RecentsRow> rows)
    {
        var into = new List<KeyValuePair<string, long>>(rows.Count * 2);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.PlayedAtMs <= 0) continue;
            if (r.Uri.Length > 0) into.Add(new(r.Uri, r.PlayedAtMs));
            if (r.ContextUri is { Length: > 0 } ctx && ctx != r.Uri) into.Add(new(ctx, r.PlayedAtMs));
            if (r.Members is { Count: > 0 } members)
                for (int m = 0; m < members.Count; m++)
                    if (members[m].Uri.Length > 0 && members[m].PlayedAtMs > 0) into.Add(new(members[m].Uri, members[m].PlayedAtMs));
        }
        return into;
    }

    /// <summary>After identity hydration: the album + billed artists of every TRACK uri in <paramref name="uris"/>,
    /// stamped with that track's play time (looked up from the rows/members). <paramref name="resolve"/> is the
    /// store read (IStore.GetTrack); a track the store cannot name contributes nothing — a recents row only carries a
    /// track uri, never its album/artists, so this is the one place those facts get attached, and only once the store
    /// actually has the track resident (never a network call of its own).</summary>
    public static List<KeyValuePair<string, long>> TrackStamps(IReadOnlyList<RecentsRow> rows, IReadOnlyList<string> uris,
                                                                Func<string, Track?> resolve)
    {
        var playedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (RecentsList.EntityKindOf(r.Uri) == RecentsEntityKind.Track) Newest(playedAt, r.Uri, r.PlayedAtMs);
            if (r.Members is { } members)
                for (int m = 0; m < members.Count; m++) Newest(playedAt, members[m].Uri, members[m].PlayedAtMs);
        }
        var into = new List<KeyValuePair<string, long>>();
        for (int i = 0; i < uris.Count; i++)
        {
            if (!playedAt.TryGetValue(uris[i], out long at) || resolve(uris[i]) is not { } t) continue;
            if (t.Album.Uri.Length > 0) into.Add(new(t.Album.Uri, at));
            for (int a = 0; a < t.Artists.Count; a++) into.Add(new(t.Artists[a].Uri, at));
        }
        return into;
    }

    // Last-write-wins is wrong here (wire order is not chronological across a header + its members) — this is a
    // max, same as PlayRecency.Stamp downstream, so a re-scan of the same rows is always idempotent.
    static void Newest(Dictionary<string, long> map, string uri, long at)
    {
        if (uri.Length == 0 || at <= 0) return;
        if (!map.TryGetValue(uri, out long cur) || at > cur) map[uri] = at;
    }
}
