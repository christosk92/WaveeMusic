using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>The sort keys the library pickers offer (LibrarySortView rows 0..4). The int codes are PERSISTED
/// (LibraryStateKeys.Sort / AlbumSort) — never renumber.</summary>
public enum LibraryNavSort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, ReleaseDate = 4 }

/// <summary>What an order needs from a row. The navigator's NavItem and the discography's Album both project to this,
/// so one comparator set serves the artists, albums and podcasts panes and the discography column.</summary>
public readonly record struct LibraryNavFacts(string Uri, string Title, string Subtitle, int Year, string? CoverUrl);

/// <summary>The pure ordering behind every library list. Rows arrive in SOURCE order — the saved set newest-added-first
/// (StoreLibrarySource.JoinSet) for the panes, the API's release order for a discography — and that order is the
/// tie-break of last resort, so the result is a total order: the same set in the same source order always yields the
/// same sequence, which is what lets the page key its (frozen-template) ItemsView on <see cref="OrderKey"/> without
/// remounting on a same-set republish (#E).</summary>
public static class LibraryNavOrder
{
    static readonly StringComparer Name = StringComparer.OrdinalIgnoreCase;

    /// <summary>The permutation (indices into <paramref name="rows"/>) for <paramref name="sort"/>.
    /// <list type="bullet">
    /// <item><b>Recents</b> — played rows newest-first, then the never-played block in source order. The block split is
    /// applied BEFORE <paramref name="desc"/> (the SidebarSort.Recents rule): a never-played row can never float above a
    /// played one; <paramref name="desc"/> reverses inside each block.</item>
    /// <item><b>RecentlyAdded</b> — the source order IS added-desc; <paramref name="desc"/> reverses it.</item>
    /// <item><b>Alphabetical</b> / <b>Creator</b> — case-insensitive title / subtitle, then title, then uri.</item>
    /// <item><b>ReleaseDate</b> — year desc; unknown (0) years sink as a block.</item>
    /// </list></summary>
    public static int[] Order(LibraryNavFacts[] rows, LibraryNavSort sort, bool desc, IReadOnlyDictionary<string, long> lastPlayed)
    {
        var idx = new int[rows.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        if (rows.Length < 2) return idx;
        int sign = desc ? -1 : 1;
        Comparison<int> cmp = sort switch
        {
            LibraryNavSort.Recents => (a, b) =>
            {
                long pa = StampOf(lastPlayed, rows[a].Uri), pb = StampOf(lastPlayed, rows[b].Uri);
                bool ha = pa > 0, hb = pb > 0;
                if (ha != hb) return ha ? -1 : 1;                       // block split — direction-proof
                int c = ha ? pb.CompareTo(pa) : a.CompareTo(b);         // newest play first · never played: source order
                return sign * (c != 0 ? c : ByTitle(rows, a, b));
            },
            LibraryNavSort.Alphabetical => (a, b) => sign * ByTitle(rows, a, b),
            LibraryNavSort.Creator => (a, b) =>
            {
                int c = Name.Compare(rows[a].Subtitle, rows[b].Subtitle);
                return sign * (c != 0 ? c : ByTitle(rows, a, b));
            },
            LibraryNavSort.ReleaseDate => (a, b) =>
            {
                bool ya = rows[a].Year > 0, yb = rows[b].Year > 0;
                if (ya != yb) return ya ? -1 : 1;                       // unknown years sink as a block
                int c = rows[b].Year.CompareTo(rows[a].Year);
                return sign * (c != 0 ? c : ByTitle(rows, a, b));
            },
            _ => (a, b) => sign * a.CompareTo(b),                       // RecentlyAdded
        };
        Array.Sort(idx, cmp);   // unstable sort, total comparator (index is the last tie-break) → deterministic
        return idx;
    }

    static long StampOf(IReadOnlyDictionary<string, long> map, string uri) => map.TryGetValue(uri, out var ms) ? ms : 0;

    static int ByTitle(LibraryNavFacts[] rows, int a, int b)
    {
        int c = Name.Compare(rows[a].Title, rows[b].Title);
        if (c == 0) c = string.CompareOrdinal(rows[a].Uri, rows[b].Uri);
        return c != 0 ? c : a.CompareTo(b);
    }

    /// <summary>Identity of the SEQUENCE (uris in order) — the remount key part that says "the frozen template's
    /// index→row mapping is stale". FNV-1a, not string.GetHashCode: stable across runs so a test can pin it.</summary>
    public static string OrderKey(LibraryNavFacts[] rows)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < rows.Length; i++) h = Fnv(h, rows[i].Uri);
        return rows.Length + ":" + h.ToString("x16");
    }

    /// <summary>Identity of what the rows DISPLAY (uri, title, subtitle, cover) — the remount key part that says "a
    /// fact landed after mount". Selection is deliberately not an input: selecting must never remount.</summary>
    public static string FactsKey(LibraryNavFacts[] rows)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            h = Fnv(Fnv(Fnv(Fnv(h, r.Uri), r.Title), r.Subtitle), r.CoverUrl ?? "");
        }
        return h.ToString("x16");
    }

    static ulong Fnv(ulong h, string s)
    {
        for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 1099511628211UL; }
        h ^= 0x1F; h *= 1099511628211UL;   // field separator so ("ab","c") ≠ ("a","bc")
        return h;
    }
}
