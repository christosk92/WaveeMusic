using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

// The five sidebar sort comparators (F.7.7), driven against the REAL SidebarSort (source-included, engine-free).
// The properties that matter here are the ones a list view exposes as flicker or as a lie:
//   * every comparator is a TOTAL order (List<T>.Sort is unstable),
//   * never-visited entries sink as a BLOCK under Recents and never float above a visited one when reversed,
//   * empty creators stay last under Creator in both directions,
//   * Custom appends unknown ids stably and ignores `desc`.
public class SidebarSortTests
{
    static SidebarLibraryEntry Pl(string id, string name, string creator = "Owner",
                                  long sortStamp = 0, long visited = 0, int order = 0) =>
        new("pl:spotify:playlist:" + id, SidebarEntryKind.Playlist, "spotify:playlist:" + id, name, creator,
            null, null, ChildCount: 0, AddedAtMs: 0, SortStamp: sortStamp, LastVisitedTicksUtc: visited,
            SourceOrder: order, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

    static SidebarLibraryEntry Artist(string id, string name, int order = 0) =>
        new("artist:spotify:artist:" + id, SidebarEntryKind.Artist, "spotify:artist:" + id, name, "",
            null, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: order, Depth: 0, Circular: true, Flavor: SidebarPlaylistFlavor.None);

    static string[] Names(List<SidebarLibraryEntry> l)
    {
        var a = new string[l.Count];
        for (int i = 0; i < l.Count; i++) a[i] = l[i].Name;
        return a;
    }

    static List<SidebarLibraryEntry> Sorted(List<SidebarLibraryEntry> list, SidebarV3Sort sort, bool desc = false,
                                           IReadOnlyList<string>? custom = null)
    {
        SidebarSort.Apply(list, sort, desc, custom);
        return list;
    }

    [Fact]
    public void Recents_OrdersByLastVisitedDescending()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("a", "Alpha", visited: 100),
            Pl("b", "Bravo", visited: 300),
            Pl("c", "Charlie", visited: 200),
        };
        Assert.Equal(new[] { "Bravo", "Charlie", "Alpha" }, Names(Sorted(list, SidebarV3Sort.Recents)));
    }

    [Fact]
    public void Recents_NeverVisitedSinkAsABlock_OrderedBySortStampThenName()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("n1", "NeverOld", sortStamp: 10),
            Pl("v1", "Visited", visited: 5),
            Pl("n2", "NeverNew", sortStamp: 99),
        };
        Assert.Equal(new[] { "Visited", "NeverNew", "NeverOld" }, Names(Sorted(list, SidebarV3Sort.Recents)));
    }

    [Fact]
    public void Recents_Descending_ReversesEachBlockIndependently()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("v1", "V1", visited: 100),
            Pl("v2", "V2", visited: 200),
            Pl("n1", "N1", sortStamp: 10),
            Pl("n2", "N2", sortStamp: 20),
        };
        // Visited block reverses (oldest first) but STAYS ahead of the never-visited block, which also reverses.
        Assert.Equal(new[] { "V1", "V2", "N1", "N2" }, Names(Sorted(list, SidebarV3Sort.Recents, desc: true)));
    }

    [Fact]
    public void RecentlyAdded_FallsBackToSourceOrder_WhenStampsTie()
    {
        // The honest AddedAt gap: playlists share a first-run stamp, so rootlist order (SourceOrder asc) decides.
        var list = new List<SidebarLibraryEntry>
        {
            Pl("c", "Charlie", sortStamp: 500, order: 2),
            Pl("a", "Alpha", sortStamp: 500, order: 0),
            Pl("b", "Bravo", sortStamp: 500, order: 1),
            Pl("z", "Zulu", sortStamp: 900, order: 9),
        };
        Assert.Equal(new[] { "Zulu", "Alpha", "Bravo", "Charlie" }, Names(Sorted(list, SidebarV3Sort.RecentlyAdded)));
    }

    [Fact]
    public void RecentlyAdded_Descending_Reverses()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("a", "Alpha", sortStamp: 100),
            Pl("b", "Bravo", sortStamp: 200),
        };
        Assert.Equal(new[] { "Alpha", "Bravo" }, Names(Sorted(list, SidebarV3Sort.RecentlyAdded, desc: true)));
    }

    [Fact]
    public void Alphabetical_IsCaseInsensitive_AndDoesNotStripArticles()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("t", "The Beatles"),
            Pl("a", "alpha"),
            Pl("z", "Zebra"),
            Pl("b", "Bravo"),
        };
        // "The Beatles" sorts under T, exactly like Spotify — no article stripping.
        Assert.Equal(new[] { "alpha", "Bravo", "The Beatles", "Zebra" }, Names(Sorted(list, SidebarV3Sort.Alphabetical)));
    }

    [Fact]
    public void Alphabetical_IsATotalOrder_ForIdenticalNamesAndCreators()
    {
        var a = Pl("aaa", "Same", "Same");
        var b = Pl("bbb", "Same", "Same");
        Assert.True(SidebarSort.Alphabetical(in a, in b, desc: false) < 0);
        Assert.True(SidebarSort.Alphabetical(in b, in a, desc: false) > 0);
        Assert.Equal(0, SidebarSort.Alphabetical(in a, in a, desc: false));
    }

    [Fact]
    public void Sorting_TwiceIsIdempotent_UnderTheUnstableListSort()
    {
        var list = new List<SidebarLibraryEntry>();
        for (int i = 0; i < 40; i++) list.Add(Pl("id" + i, "Same name", "Same creator", sortStamp: 7, order: 0));
        var first = Names(Sorted(list, SidebarV3Sort.RecentlyAdded));
        var second = Names(Sorted(list, SidebarV3Sort.RecentlyAdded));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Creator_SortsByCreatorThenName_WithEmptyCreatorsLastInBothDirections()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("b", "Beta", "Zoe"),
            Artist("x", "An Artist"),                 // no creator
            Pl("a", "Alpha", "Adam"),
            Pl("c", "Gamma", "Adam"),
        };
        Assert.Equal(new[] { "Alpha", "Gamma", "Beta", "An Artist" },
                     Names(Sorted(list, SidebarV3Sort.Creator)));

        Assert.Equal(new[] { "Beta", "Gamma", "Alpha", "An Artist" },
                     Names(Sorted(list, SidebarV3Sort.Creator, desc: true)));
    }

    [Fact]
    public void Custom_UsesStoredOrder_ThenAppendsUnknownIdsBySourceOrder()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("new2", "New2", order: 5),
            Pl("known2", "Known2", order: 9),
            Pl("new1", "New1", order: 1),
            Pl("known1", "Known1", order: 8),
        };
        var order = new[] { "pl:spotify:playlist:known1", "pl:spotify:playlist:known2" };
        Assert.Equal(new[] { "Known1", "Known2", "New1", "New2" },
                     Names(Sorted(list, SidebarV3Sort.Custom, desc: false, custom: order)));
    }

    [Fact]
    public void Custom_AppendedIdsStayPutAcrossTwoBuilds_AndDescIsIgnored()
    {
        var order = new[] { "pl:spotify:playlist:k" };
        var list = new List<SidebarLibraryEntry>
        {
            Pl("u2", "U2", order: 2),
            Pl("k", "K", order: 7),
            Pl("u1", "U1", order: 1),
        };
        var asc = Names(Sorted(list, SidebarV3Sort.Custom, desc: false, custom: order));
        var desc = Names(Sorted(list, SidebarV3Sort.Custom, desc: true, custom: order));
        Assert.Equal(new[] { "K", "U1", "U2" }, asc);
        Assert.Equal(asc, desc);                                   // desc is not applied to a user-authored order
    }

    [Fact]
    public void Custom_WithNoStoredOrder_IsPureSourceOrder()
    {
        var list = new List<SidebarLibraryEntry> { Pl("b", "B", order: 2), Pl("a", "A", order: 1) };
        Assert.Equal(new[] { "A", "B" }, Names(Sorted(list, SidebarV3Sort.Custom, custom: null)));
    }

    [Fact]
    public void Custom_IgnoresDuplicateIdsInTheStoredOrder()
    {
        var rank = SidebarSort.BuildRanks(new[] { "x", "y", "x" });
        Assert.Equal(0, rank["x"]);                                // first occurrence wins; no re-rank, no throw
        Assert.Equal(1, rank["y"]);
    }

    [Fact]
    public void Effective_FallsBackToAlphabetical_WhenCustomIsPickedOutsideThePlaylistsFilter()
    {
        Assert.Equal(SidebarV3Sort.Custom, SidebarSort.Effective(SidebarV3Sort.Custom, SidebarV3Filter.Playlists));
        Assert.Equal(SidebarV3Sort.Alphabetical, SidebarSort.Effective(SidebarV3Sort.Custom, SidebarV3Filter.All));
        Assert.Equal(SidebarV3Sort.Alphabetical, SidebarSort.Effective(SidebarV3Sort.Custom, SidebarV3Filter.Albums));
        Assert.Equal(SidebarV3Sort.Recents, SidebarSort.Effective(SidebarV3Sort.Recents, SidebarV3Filter.Albums));
        Assert.False(SidebarSort.SupportsDirection(SidebarV3Sort.Custom));
        Assert.True(SidebarSort.SupportsDirection(SidebarV3Sort.Recents));
    }

    [Fact]
    public void EveryComparator_IsAntisymmetric_OverAMixedList()
    {
        var entries = new[]
        {
            Pl("a", "Alpha", "Zoe", sortStamp: 5, visited: 100, order: 3),
            Pl("b", "Bravo", "", sortStamp: 5, visited: 0, order: 1),
            Artist("c", "Charlie", order: 2),
            Pl("d", "alpha", "Adam", sortStamp: 50, visited: 100, order: 0),
        };
        var sorts = new[] { SidebarV3Sort.Recents, SidebarV3Sort.RecentlyAdded, SidebarV3Sort.Alphabetical, SidebarV3Sort.Creator };
        foreach (var s in sorts)
            foreach (bool desc in new[] { false, true })
            {
                var cmp = SidebarSort.For(s, desc);
                for (int i = 0; i < entries.Length; i++)
                    for (int j = 0; j < entries.Length; j++)
                    {
                        int ab = cmp(entries[i], entries[j]);
                        int ba = cmp(entries[j], entries[i]);
                        if (i == j) Assert.Equal(0, ab);
                        else Assert.True(ab != 0 && Math.Sign(ab) == -Math.Sign(ba),
                                         $"{s}/{desc} is not a total order at ({i},{j})");
                    }
            }
    }
}
