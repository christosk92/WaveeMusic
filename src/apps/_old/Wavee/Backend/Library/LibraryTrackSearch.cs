using System;
using System.Collections.Generic;
using Wavee.Backend.Persistence;
using Wavee.Core;

namespace Wavee.Backend.Library;

// ── the offline track search (the search page's fallback when there is no live session) ──────────────────────────────
// `IStore.QueryTracks` scans the RESIDENT tracks only, which stopped being the whole library the moment the hot tier
// became a bounded cache over cold. This rebases it the same way LibrarySearchIndex is rebased (Addendum A4):
//
//   1. the resident scan runs FIRST and unchanged — same matcher, same order, so nothing regresses for a warm library;
//   2. if it did not already fill the limit, the COLD candidate rows (liked ∪ every adopted playlist's membership, thin
//      columns only) are matched in C# and the survivors are hydrated one PK read at a time through the store's cold
//      fallback — bounded by the limit, and it keeps the result type a full `Track` record.
//
// Matching stays `OrdinalIgnoreCase` substring on the title or ANY artist name — identical to InMemoryStore.MatchesText.
// No SQL `LIKE` pre-filter: `NOCASE` folds ASCII only and would drop non-ASCII matches (see LibraryCandidates.cs).
public static class LibraryTrackSearch
{
    public static IReadOnlyList<Track> Search(IStore store, string? text, int limit = 200)
    {
        var q = (text ?? "").Trim();
        if (limit <= 0) return Array.Empty<Track>();
        var resident = store.QueryTracks(q, TrackSort.None, limit);
        // An empty query has no corpus semantics (it means "the first N resident tracks") — leave it alone.
        if (q.Length == 0 || resident.Count >= limit || store is not ILibraryCandidateStore cold) return resident;

        IReadOnlyList<ColdThinRow> rows;
        try { rows = cold.LoadLibraryCandidates(ColdCandidateScope.LibraryTracks).Tracks; }
        catch (Exception) { return resident; }   // a cold read failure degrades to the resident result, never to an error
        if (rows.Count == 0) return resident;

        var list = new List<Track>(limit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < resident.Count; i++) { list.Add(resident[i]); seen.Add(resident[i].Uri); }
        for (int i = 0; i < rows.Count && list.Count < limit; i++)
        {
            var r = rows[i];
            if (r.Uri.Length == 0 || seen.Contains(r.Uri) || !Matches(r, q)) continue;
            if (store.GetTrack(r.Uri) is not { } track) continue;   // one PK read + promote — bounded by `limit`
            seen.Add(r.Uri);
            list.Add(track);
        }
        return list;
    }

    // Title, or any single artist name. `subtitle` is the ", "-joined artist list, so it is split back apart rather than
    // matched whole — otherwise a query straddling the separator ("beatles, the") would be a false positive that the
    // resident matcher (which tests each Artists[i].Name) never produces.
    static bool Matches(in ColdThinRow row, string q)
    {
        if (row.Title is { Length: > 0 } title && title.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (row.Subtitle is not { Length: > 0 } names) return false;
        int start = 0;
        while (true)
        {
            int sep = names.IndexOf(", ", start, StringComparison.Ordinal);
            int end = sep < 0 ? names.Length : sep;
            if (names.AsSpan(start, end - start).Contains(q.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
            if (sep < 0) return false;
            start = sep + 2;
        }
    }
}
