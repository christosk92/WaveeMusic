using System;
using System.Collections.Generic;
using System.Globalization;

namespace Wavee;

// The five sidebar sort comparators (F.7.7). Engine-free (System only), source-included by src/apps/Wavee.Tests, so
// SidebarSortTests drives the REAL comparators.
//
// EVERY comparator ends in an ordinal Id compare, so each one is a TOTAL order. That is not politeness: List<T>.Sort is
// unstable, so two entries with an equal key would otherwise reshuffle between rebuilds — visible as row flicker under
// the FLIP transitions.

public static class SidebarSort
{
    static StringComparer? s_name;

    /// <summary>Localized name/creator collation, built ONCE per UI culture (<c>CurrentUICulture</c> is launch-scoped in
    /// this app — <c>WaveeSettings.UiCulture</c> is applied before the first mount) and cached. Case-insensitive.
    /// NO article stripping: "The Beatles" sorts under T, exactly like Spotify.</summary>
    public static StringComparer NameComparer =>
        s_name ??= StringComparer.Create(CultureInfo.CurrentUICulture, ignoreCase: true);

    /// <summary>Test/culture-switch hook: drop the cached collator so the next access rebuilds it for the current culture.</summary>
    public static void ResetCollator() => s_name = null;

    // Cached delegates for the four closure-free sorts × both directions — a memo rebuild then allocates nothing at all
    // (only Custom, which needs the rank map, allocates).
    static readonly Comparison<SidebarLibraryEntry> s_recentsAsc = static (a, b) => Recents(in a, in b, desc: false);
    static readonly Comparison<SidebarLibraryEntry> s_recentsDesc = static (a, b) => Recents(in a, in b, desc: true);
    static readonly Comparison<SidebarLibraryEntry> s_addedAsc = static (a, b) => RecentlyAdded(in a, in b, desc: false);
    static readonly Comparison<SidebarLibraryEntry> s_addedDesc = static (a, b) => RecentlyAdded(in a, in b, desc: true);
    static readonly Comparison<SidebarLibraryEntry> s_alphaAsc = static (a, b) => Alphabetical(in a, in b, desc: false);
    static readonly Comparison<SidebarLibraryEntry> s_alphaDesc = static (a, b) => Alphabetical(in a, in b, desc: true);
    static readonly Comparison<SidebarLibraryEntry> s_creatorAsc = static (a, b) => Creator(in a, in b, desc: false);
    static readonly Comparison<SidebarLibraryEntry> s_creatorDesc = static (a, b) => Creator(in a, in b, desc: true);

    /// <summary>The comparator for a (sort, direction) pair. <paramref name="customOrder"/> is only read for
    /// <see cref="SidebarV3Sort.Custom"/>; a null/empty order there degrades to pure <c>SourceOrder</c> (the stable-append
    /// rule with an empty curated block, F.7.10).</summary>
    public static Comparison<SidebarLibraryEntry> For(SidebarV3Sort sort, bool desc,
                                                     IReadOnlyList<string>? customOrder = null)
    {
        switch (sort)
        {
            case SidebarV3Sort.RecentlyAdded: return desc ? s_addedDesc : s_addedAsc;
            case SidebarV3Sort.Alphabetical: return desc ? s_alphaDesc : s_alphaAsc;
            case SidebarV3Sort.Creator: return desc ? s_creatorDesc : s_creatorAsc;
            case SidebarV3Sort.Custom:
            {
                // Rank map: O(1) lookups instead of an IndexOf per comparison. `desc` is deliberately IGNORED for Custom
                // (the user's order has no meaningful inverse; the direction affordance is hidden).
                var rank = BuildRanks(customOrder);
                return (a, b) => Custom(in a, in b, rank);
            }
            default: return desc ? s_recentsDesc : s_recentsAsc;
        }
    }

    /// <summary>Sort in place. Kept beside the comparators so no caller forgets that the list must already hold exactly
    /// the entries it wants sorted (filters run BEFORE the sort; pins are partitioned AFTER it — F.7.9).</summary>
    public static void Apply(List<SidebarLibraryEntry> list, SidebarV3Sort sort, bool desc,
                             IReadOnlyList<string>? customOrder = null)
    {
        if (list.Count > 1) list.Sort(For(sort, desc, customOrder));
    }

    /// <summary>The sort that is actually applied for a filter: <see cref="SidebarV3Sort.Custom"/> exists only under the
    /// Playlists filter (locked decision 10), and under any other filter it falls back to Alphabetical FOR DISPLAY while
    /// the persisted preference is left untouched (F.7.10).</summary>
    public static SidebarV3Sort Effective(SidebarV3Sort sort, SidebarV3Filter filter) =>
        sort == SidebarV3Sort.Custom && filter != SidebarV3Filter.Playlists ? SidebarV3Sort.Alphabetical : sort;

    /// <summary>True when the direction affordance should be shown at all (Custom has no inverse).</summary>
    public static bool SupportsDirection(SidebarV3Sort sort) => sort != SidebarV3Sort.Custom;

    public static Dictionary<string, int> BuildRanks(IReadOnlyList<string>? order)
    {
        var rank = new Dictionary<string, int>(order?.Count ?? 0, StringComparer.Ordinal);
        if (order is null) return rank;
        for (int i = 0; i < order.Count; i++)
        {
            var id = order[i];
            if (!string.IsNullOrEmpty(id)) rank.TryAdd(id, i);        // first occurrence wins; a duplicate id never re-ranks
        }
        return rank;
    }

    // ── the comparators ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Recents: last-VISITED descending. Never-visited entries sink to the bottom AS A BLOCK, ordered by
    /// SortStamp desc then Name. The visited/never-visited partition is applied before (and is never affected by)
    /// <paramref name="desc"/>, so a never-visited item can never float above a visited one; `desc` reverses the two
    /// blocks independently.</summary>
    public static int Recents(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
    {
        bool av = a.LastVisitedTicksUtc > 0, bv = b.LastVisitedTicksUtc > 0;
        if (av != bv) return av ? -1 : 1;

        int c = av
            ? b.LastVisitedTicksUtc.CompareTo(a.LastVisitedTicksUtc)
            : b.SortStamp.CompareTo(a.SortStamp);
        if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return desc ? -c : c;
    }

    /// <summary>Recently added: the resolved SortStamp descending, then rootlist/source order ascending, then Name.
    /// HONEST LIMIT: playlists have no server add-date, so their stamp is the local first-observation proxy
    /// (<see cref="SidebarFirstSeen"/>) — on a first run every playlist ties and SourceOrder (Spotify's own newest-first
    /// rootlist order) decides.</summary>
    public static int RecentlyAdded(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
    {
        int c = b.SortStamp.CompareTo(a.SortStamp);
        if (c == 0) c = a.SourceOrder.CompareTo(b.SourceOrder);
        if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return desc ? -c : c;
    }

    /// <summary>Alphabetical by Name (localized, case-insensitive), then Creator, then Id.</summary>
    public static int Alphabetical(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
    {
        int c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = NameComparer.Compare(a.Creator, b.Creator);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return desc ? -c : c;
    }

    /// <summary>By Creator (localized), with EMPTY creators last ALWAYS — the empty-creator partition is not reversed by
    /// <paramref name="desc"/> (an artist row, which has no creator, must not lead the list just because the direction
    /// flipped). Then Name, then Id.</summary>
    public static int Creator(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
    {
        bool ac = a.Creator.Length > 0, bc = b.Creator.Length > 0;
        if (ac != bc) return ac ? -1 : 1;

        int c = ac ? NameComparer.Compare(a.Creator, b.Creator) : 0;
        if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return desc ? -c : c;
    }

    /// <summary>The local custom overlay: ids the user actually ordered come first in their stored order; every id absent
    /// from the order APPENDS after all known ids in SourceOrder ascending (the stable-append rule, F.7.10 — newly
    /// followed playlists land where Spotify itself would put them). `desc` is never applied.</summary>
    public static int Custom(in SidebarLibraryEntry a, in SidebarLibraryEntry b, Dictionary<string, int> rank)
    {
        bool ak = rank.TryGetValue(a.Id, out int ra);
        bool bk = rank.TryGetValue(b.Id, out int rb);
        if (ak != bk) return ak ? -1 : 1;                       // known block leads the appended block

        int c = ak ? ra.CompareTo(rb) : a.SourceOrder.CompareTo(b.SourceOrder);
        if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return c;
    }
}
