namespace Wavee.Core;

/// <summary>The artist "Popular" chart's pure merge + the shared caps. The chart's three-step FETCH pipeline (overview
/// seed → SpClient <c>artist-top-tracks-extensions</c> → kind-185 counts) now lives in ONE place, the artist ladder
/// (<c>Backend/Hydration/ArtistHydration.cs</c>) — the service interface + its switchable/null halves were deleted with
/// the rest of the legacy hydration paths (hydration-facade-plan.md §1.6). What stays here is deliberately pure: the
/// ordering/play-count contract is unit-testable without an HTTP seam, and the UI sizes its pager off the same
/// constants the ladder writes.</summary>
public static class ArtistPopularTracks
{
    /// <summary>What <c>queryArtistOverview</c> returns. A stored <c>TopTracks</c> longer than this IS an extended list —
    /// that count, not a second timestamp column, is the "already extended" gate.</summary>
    public const int OverviewSeedCap = 10;

    /// <summary>The extended ceiling (Spotify serves ~50). Bounds the metadata batch, the merged list, and therefore the
    /// uris the artist_overview document persists.</summary>
    public const int ExtendedCap = 50;

    /// <summary>Fold the extension list onto the seed. The SEED KEEPS ITS ORDER AND ITS PLAY COUNTS at the head (the
    /// overview is the authoritative count for the head; the extension carries uris only — its counts arrive in step
    /// three, see <see cref="WithPlayCounts"/>), then extension-only tracks append in extension order. Duplicates collapse
    /// by uri, the seed instance always winning. An empty extension returns the seed unchanged — a failed or empty step
    /// two must never reorder a painted chart.</summary>
    public static IReadOnlyList<Track> Merge(IReadOnlyList<Track>? seed, IReadOnlyList<Track>? extension)
    {
        var head = seed ?? Array.Empty<Track>();
        if (extension is not { Count: > 0 })
            return head.Count > ExtendedCap ? Slice(head) : head;

        var merged = new List<Track>(Math.Min(ExtendedCap, head.Count + extension.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < head.Count && merged.Count < ExtendedCap; i++)
        {
            var t = head[i];
            if (t.Uri.Length == 0 || !seen.Add(t.Uri)) continue;
            merged.Add(t);
        }
        for (int i = 0; i < extension.Count && merged.Count < ExtendedCap; i++)
        {
            var t = extension[i];
            if (t.Uri.Length == 0 || !seen.Add(t.Uri)) continue;
            merged.Add(t);
        }
        return merged;
    }

    /// <summary>Step three: hand the rows that have NO count (<c>PlayCount &lt;= 0</c>) the kind-185 count keyed by their
    /// uri. A row that already carries a positive count is never touched (the overview head stays authoritative, and a
    /// stale-but-known count beats a fresh miss), and a count that is not positive is never applied. Returns the SAME
    /// instance when nothing changes — the caller keys "write the artist" on reference inequality.</summary>
    public static IReadOnlyList<Track> WithPlayCounts(IReadOnlyList<Track> chart, IReadOnlyDictionary<string, long> counts)
    {
        if (chart.Count == 0 || counts.Count == 0) return chart;
        List<Track>? result = null;
        for (int i = 0; i < chart.Count; i++)
        {
            var t = chart[i];
            if (t.PlayCount > 0 || t.Uri.Length == 0 || !counts.TryGetValue(t.Uri, out long plays) || plays <= 0)
            {
                result?.Add(t);
                continue;
            }
            if (result is null)
            {
                result = new List<Track>(chart.Count);
                for (int j = 0; j < i; j++) result.Add(chart[j]);
            }
            result.Add(t with { PlayCount = plays });
        }
        return result ?? chart;
    }

    /// <summary>The uris of the rows that still have no play count — what step three asks kind 185 for.</summary>
    public static List<string> UrisWithoutPlayCount(IReadOnlyList<Track> chart)
    {
        var need = new List<string>();
        for (int i = 0; i < chart.Count; i++)
            if (chart[i].PlayCount <= 0 && chart[i].Uri.Length > 0) need.Add(chart[i].Uri);
        return need;
    }

    /// <summary>How many of <paramref name="merged"/> came from beyond the seed (the structured-log "appended" field).</summary>
    public static int AppendedCount(IReadOnlyList<Track>? seed, IReadOnlyList<Track> merged)
        => Math.Max(0, merged.Count - (seed?.Count ?? 0));

    static IReadOnlyList<Track> Slice(IReadOnlyList<Track> tracks)
    {
        var list = new List<Track>(ExtendedCap);
        for (int i = 0; i < ExtendedCap; i++) list.Add(tracks[i]);
        return list;
    }
}
