// ── Wavee.Tests/SidebarProjectionTests.cs — sort, search, the edge-backed projection, the publish gate, the binder ──
//
// Ported from the 0.2.9 suite (src/apps/_old/Wavee.Tests: SidebarSortTests.cs, SidebarProjectionTests.cs,
// SidebarChurnTests.cs, SidebarProjectionBinderTests.cs) against the 0.3 production code in
// src/apps/Wavee/Shell/Sidebar.cs (SidebarProjection / SidebarSort / SidebarSearch / SidebarBinderPipeline /
// SidebarEntriesShadow / SidebarLibraryEntry / SidebarEntryKind(s) / SidebarEntryKindMask / SidebarPlaylistFlavor)
// and Sidebar.Host.cs (SidebarProjectionBinder / SidebarEntries / SidebarFirstSeen / SidebarRecency).
//
// THE BIG 0.3 CHANGE, carried through every fact in the PROJECTION region below: 0.2.9's projection COPIED records
// out of a `LibraryStore`; 0.3's projection IS a read over `User.Me`'s edges (Rootlist/SavedAlbums/FollowedArtists/
// SavedShows) joined to Playlist/Album/Artist/Show handles. Every fixture that used to build a `PlaylistNode`/
// `PlaylistSummary`/`Album`/`Artist`/`Show` DTO tree now stages real rows through `Staging` (exactly like
// EdgesStagingTests.cs / AlbumTests.cs / ArtistTests.cs / ShowTests.cs / PlaylistTests.cs) and writes the edges
// through `User.Replace` / `User.ReplaceRootlist` (the same "replace a whole relation from a provider answer" entry
// point `Entities/User.cs` documents) — never through a mocked store, because there is no store any more.
//
// Regions, in file order, each self-contained:
//   SORT              SidebarSort — every comparator is a TOTAL order; ports ~1:1, PURE.
//   SEARCH            SidebarSearch — diacritics-insensitive match; PURE.
//   ENTRY KINDS       SidebarEntryKinds — filter/query → kind-mask mapping; PURE.
//   PINS-FIRST        SidebarProjection.PinsFirst — the pins-first partition; PURE (operates on plain lists).
//   FIRST-SEEN        SidebarFirstSeen (Sidebar.Host.cs) — the bounded first-observation map; PURE.
//   RECENCY           SidebarRecency (Sidebar.Host.cs) — the navigation-recency identity lookup; PURE.
//   PROJECTION        SidebarProjection.Build over real staged edges — [Collection(EntitiesCollection.Name)].
//   SHADOW            SidebarEntriesShadow — the publish/churn gate; PURE.
//   BINDER TRIGGERS   SidebarBinderTriggers — the rebuild gate's trigger fold; PURE.
//   BINDER SHAPE      SidebarBinderPipeline.Project/Shape — filter → sort → pins-first; PURE.
//   UNLISTED PIN      SidebarBinderPipeline.ResolveUnlistedPin — the offline-display-cache overlay; PURE.
//   CONTRIBUTION      SidebarBinderPipeline.Resolve/ResolveExtensions + SidebarDataSourceTable; PURE.
//
// DROPPED (see the porting agent's handoff for the full accounting):
//   - The pin-id vocabulary (SidebarPinId.FromUri/KindOf/RouteOf/FromRoute/UriOf/FolderIdOf/FromEntry, PinId_*): a
//     dedicated SidebarPinTests.cs already exists in this project and is the declared port target for SidebarPinId —
//     a second copy here would fork the source of truth over the same production code rather than adding a real
//     fact. (Its own header names real gaps in its own coverage — KindOf/RouteOf/UriOf/FolderIdOf/the FromUri success
//     paths are not yet driven anywhere; that belongs in SidebarPinTests.cs, not here.)
//   - SidebarChurnTests.cs's F3a (ClassicDocumentCache) is already covered by SidebarDesignTests.cs's "REGION 1"
//     (its header says so explicitly). F3c (the selection sweep, SidebarRowResolve) and F3d (the accent pill,
//     SidebarPillState) belong to Sidebar.cs's DIFF region — never named in this task's authoritative type list, and
//     not exercised by anything in this project yet; they are a different port, not weakened here.
//   - SidebarProjectionBinderTests.cs's row-planner facts (SidebarRowPlanner.Build/BuildRail for Extension sections,
//     the EntityQuery include/exclude-uri facts): SidebarRowPlanner belongs to Sidebar.cs's PLAN region, never named
//     in this task's authoritative type list either.
//   - SidebarProjectionBinder's integration facts live in SidebarWiringTests.cs now that the seams exist:
//     `Sidebar.Store` is lazy with `Sidebar.UseStore(...)` (no real profile touched) and the binder takes its two
//     shell logs through `ISidebarRecencyLogs`. The rebuild gate, the recency wiring and InputVersion are ported
//     there; StateOf/AvailabilityOf over a live host still are not.
//   - The Build-integration half of the FirstSeen facts (rebuild stability / prune-and-persist through
//     `SidebarProjection.Build`) is not ported yet. It became reachable once an undated edge (AddedAt == 0) stopped
//     converting to the epoch; `An_undated_playlist_falls_back_to_its_first_seen_stamp` pins that fallback. The pure
//     FirstSeen mechanism itself (Stamp/Peek/PruneTo/CopyTo/Load/Frozen) ports in full in the FIRST-SEEN region.
//
// House style: xunit.v3; test names are sentences; `Nullable` on; no warnings, no unused locals/usings.

using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using Google.Protobuf;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── SORT — every comparator ends in an ordinal Id compare (List<T>.Sort is unstable) ───────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarSortFacts
{
    static SidebarLibraryEntry Pl(string id, string name, string creator = "Owner",
                                  long sortStamp = 0, long visited = 0, int order = 0, long played = 0) =>
        new("pl:spotify:playlist:" + id, SidebarEntryKind.Playlist, "spotify:playlist:" + id, name, creator,
            default, null, ChildCount: 0, AddedAtMs: 0, SortStamp: sortStamp, LastVisitedTicksUtc: visited,
            SourceOrder: order, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { LastPlayedMs = played };

    static SidebarLibraryEntry Artist(string id, string name, int order = 0) =>
        new("artist:spotify:artist:" + id, SidebarEntryKind.Artist, "spotify:artist:" + id, name, "",
            default, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
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
    public void Recents_OrdersByLastPlayedDescending()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("a", "Alpha", played: 100),
            Pl("b", "Bravo", played: 300),
            Pl("c", "Charlie", played: 200),
        };
        Assert.Equal(new[] { "Bravo", "Charlie", "Alpha" }, Names(Sorted(list, SidebarV3Sort.Recents)));
    }

    [Fact]
    public void Recents_NeverPlayedSinkAsABlock_OrderedBySortStampThenName()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("n1", "NeverOld", sortStamp: 10),
            Pl("v1", "Played", played: 5),
            Pl("n2", "NeverNew", sortStamp: 99),
        };
        Assert.Equal(new[] { "Played", "NeverNew", "NeverOld" }, Names(Sorted(list, SidebarV3Sort.Recents)));
    }

    [Fact]
    public void Recents_Descending_ReversesEachBlockIndependently()
    {
        var list = new List<SidebarLibraryEntry>
        {
            Pl("v1", "V1", played: 100),
            Pl("v2", "V2", played: 200),
            Pl("n1", "N1", sortStamp: 10),
            Pl("n2", "N2", sortStamp: 20),
        };
        Assert.Equal(new[] { "V1", "V2", "N1", "N2" }, Names(Sorted(list, SidebarV3Sort.Recents, desc: true)));
    }

    /// <summary>Opening a row (a click ⇒ a navigation ⇒ LastVisitedTicksUtc moves) must NOT reorder "Recents" — only
    /// playing something does. The comparator does not read LastVisitedTicksUtc at all.</summary>
    [Fact]
    public void Recents_AVisitDoesNotReorder()
    {
        var a = Pl("a", "Alpha", played: 200, visited: 10);
        var b = Pl("b", "Bravo", played: 100, visited: 20);
        var list = new List<SidebarLibraryEntry> { a, b };
        Assert.Equal(new[] { "Alpha", "Bravo" }, Names(Sorted(list, SidebarV3Sort.Recents)));

        var bVisitedAgain = b with { LastVisitedTicksUtc = 999_999 };
        var list2 = new List<SidebarLibraryEntry> { a, bVisitedAgain };
        Assert.Equal(new[] { "Alpha", "Bravo" }, Names(Sorted(list2, SidebarV3Sort.Recents)));
    }

    [Fact]
    public void RecentlyAdded_FallsBackToSourceOrder_WhenStampsTie()
    {
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
            Artist("x", "An Artist"),
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
        Assert.Equal(asc, desc);
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
        Assert.Equal(0, rank["x"]);
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

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── SEARCH — diacritics-insensitive, allocation-free, library-only ──────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarSearchFacts
{
    static SidebarLibraryEntry Entry(string name, string creator) =>
        new("pl:spotify:playlist:x", SidebarEntryKind.Playlist, "spotify:playlist:x", name, creator,
            default, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

    [Fact]
    public void Matches_IsCaseAndDiacriticsInsensitive()
    {
        var e = Entry("Café Crème", "Björk");
        Assert.True(SidebarSearch.Matches(in e, "cafe"));
        Assert.True(SidebarSearch.Matches(in e, "CRÈME"));
        Assert.True(SidebarSearch.Matches(in e, "creme"));
        Assert.True(SidebarSearch.Matches(in e, ""));               // an empty query matches everything
        Assert.False(SidebarSearch.Matches(in e, "zzz"));
    }

    [Fact]
    public void Matches_MatchesCreatorOnlyForTwoOrMoreCharacters()
    {
        var e = Entry("Zzz", "Bjork");
        Assert.False(SidebarSearch.Matches(in e, "b"));              // one letter must not match every creator
        Assert.True(SidebarSearch.Matches(in e, "bj"));
    }

    [Fact]
    public void Normalize_TrimsAndNeverReturnsNull()
    {
        Assert.Equal("cafe", SidebarSearch.Normalize("  cafe \n"));
        Assert.Equal("", SidebarSearch.Normalize(null));
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── ENTRY KINDS + qualifier matching — the filter/query → kind-mask mapping (PURE) ──────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarEntryKindsFacts
{
    [Fact]
    public void From_MapsEachV3FilterToItsKindMask()
    {
        Assert.Equal(SidebarEntryKindMask.PlaylistTree, SidebarEntryKinds.From(SidebarV3Filter.Playlists));
        Assert.Equal(SidebarEntryKindMask.Show, SidebarEntryKinds.From(SidebarV3Filter.Podcasts));
        Assert.Equal(SidebarEntryKindMask.Album, SidebarEntryKinds.From(SidebarV3Filter.Albums));
        Assert.Equal(SidebarEntryKindMask.Artist, SidebarEntryKinds.From(SidebarV3Filter.Artists));
        Assert.Equal(SidebarEntryKindMask.All, SidebarEntryKinds.From(SidebarV3Filter.All));
    }

    [Fact]
    public void From_MapsCoreEntityKindsToProjectionKinds()
    {
        Assert.Equal(SidebarEntryKindMask.PlaylistTree | SidebarEntryKindMask.Album,
                     SidebarEntryKinds.From(SidebarEntityKinds.Playlists | SidebarEntityKinds.Albums));
    }

    [Fact]
    public void Has_TestsOneKindAgainstAMask()
    {
        Assert.True(SidebarEntryKinds.Has(SidebarEntryKindMask.PlaylistTree, SidebarEntryKind.Folder));
        Assert.False(SidebarEntryKinds.Has(SidebarEntryKindMask.Album, SidebarEntryKind.Playlist));
    }

    [Fact]
    public void QualifiersAvailable_NeedsTwoDistinctKnownFlavors()
    {
        byte onlyByYou = (byte)(1 << (int)SidebarPlaylistFlavor.ByYou);
        Assert.False(SidebarProjection.QualifiersAvailable(onlyByYou));

        byte byYouAndSpotify = (byte)((1 << (int)SidebarPlaylistFlavor.ByYou) | (1 << (int)SidebarPlaylistFlavor.BySpotify));
        Assert.True(SidebarProjection.QualifiersAvailable(byYouAndSpotify));

        // The "None" (unknown) bit never counts toward the two, however many rows set it.
        byte byYouAndUnknown = (byte)((1 << (int)SidebarPlaylistFlavor.ByYou) | (1 << (int)SidebarPlaylistFlavor.None));
        Assert.False(SidebarProjection.QualifiersAvailable(byYouAndUnknown));
    }

    [Fact]
    public void MatchesQualifier_TreatsAnyAsEverything()
    {
        var e = new SidebarLibraryEntry("pl:x", SidebarEntryKind.Playlist, "spotify:playlist:x", "X", "Me",
            default, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.ByYou);

        Assert.True(e.MatchesQualifier(SidebarV3Qualifier.Any));
        Assert.True(e.MatchesQualifier(SidebarV3Qualifier.ByYou));
        Assert.False(e.MatchesQualifier(SidebarV3Qualifier.BySpotify));
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── PINS-FIRST — the stable partition every sort mode applies AFTER sorting (PURE) ──────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarProjectionPinsFirstFacts
{
    static SidebarLibraryEntry Pl(string id, string name) =>
        new("pl:spotify:playlist:" + id, SidebarEntryKind.Playlist, "spotify:playlist:" + id, name, "",
            default, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

    static SidebarPin Pin(string id, string name) => new(id, SidebarEntryKind.Playlist, "", name, 0);

    static string[] Names(List<SidebarLibraryEntry> l)
    {
        var a = new string[l.Count];
        for (int i = 0; i < l.Count; i++) a[i] = l[i].Name;
        return a;
    }

    [Fact]
    public void PinsLead_InPinOrder_RegardlessOfTheSortOrder()
    {
        var rows = new List<SidebarLibraryEntry> { Pl("a", "Alpha"), Pl("b", "Bravo"), Pl("c", "Charlie") };
        SidebarSort.Apply(rows, SidebarV3Sort.Alphabetical, desc: false);

        var pins = new[] { Pin("pl:spotify:playlist:c", "Charlie"), Pin("pl:spotify:playlist:a", "Alpha") };
        int band = SidebarProjection.PinsFirst(rows, pins);

        Assert.Equal(2, band);
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, Names(rows));   // PIN order, not sort order
        Assert.True(rows[0].IsPinned);
        Assert.True(rows[1].IsPinned);
        Assert.False(rows[2].IsPinned);
    }

    [Fact]
    public void APinOutsideTheCurrentFilter_IsSimplyAbsent()
    {
        var rows = new List<SidebarLibraryEntry> { Pl("a", "Alpha") };
        var pins = new[] { Pin("album:spotify:album:zz", "A pinned album"), Pin("pl:spotify:playlist:a", "Alpha") };
        int band = SidebarProjection.PinsFirst(rows, pins);
        Assert.Equal(1, band);
        Assert.Equal(new[] { "Alpha" }, Names(rows));
    }

    [Fact]
    public void NoPins_IsANoOp()
    {
        var rows = new List<SidebarLibraryEntry> { Pl("a", "Alpha"), Pl("b", "Bravo") };
        Assert.Equal(0, SidebarProjection.PinsFirst(rows, Array.Empty<SidebarPin>()));
        Assert.Equal(new[] { "Alpha", "Bravo" }, Names(rows));
        Assert.False(rows[0].IsPinned);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── FIRST-SEEN — the bounded local "date added" proxy for playlists (PURE, Sidebar.Host.cs) ─────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarFirstSeenFacts
{
    [Fact]
    public void Stamp_RecordsOnlyTheFirstObservation_PeekNeverRecords()
    {
        var seen = new SidebarFirstSeen(() => 100L);
        Assert.Equal(0L, seen.Peek("a"));
        Assert.Equal(0, seen.Count);
        Assert.Equal(100L, seen.Stamp("a"));
        Assert.Equal(100L, seen.Stamp("a"));      // idempotent: the SAME stamp comes back
        Assert.Equal(1, seen.NewStamps);
        Assert.Equal(1, seen.Count);
    }

    [Fact]
    public void PrunesIdsThatLeftTheLibrary_AndSurvivesARoundTrip()
    {
        var seen = new SidebarFirstSeen(() => 42L);
        seen.Stamp("pl:a");
        seen.Stamp("pl:b");
        Assert.Equal(2, seen.Count);
        Assert.Equal(1, seen.PruneTo(new[] { "pl:a" }));
        Assert.Equal(42L, seen.Peek("pl:a"));
        Assert.Equal(0L, seen.Peek("pl:b"));

        var snapshot = new List<KeyValuePair<string, long>>();
        seen.CopyTo(snapshot);
        var reloaded = new SidebarFirstSeen(() => 999L);
        reloaded.Load(snapshot);
        Assert.Equal(42L, reloaded.Peek("pl:a"));
        Assert.Equal(0, reloaded.NewStamps);
        Assert.Equal(42L, reloaded.Stamp("pl:a"));   // a known id never re-stamps
        Assert.Equal(0, reloaded.NewStamps);
    }

    [Fact]
    public void Frozen_NeverRecords_ButStillActsJustSeen()
    {
        int before = SidebarFirstSeen.Frozen.Count;
        long stamp = SidebarFirstSeen.Frozen.Stamp("x");
        Assert.True(stamp > 0);
        Assert.Equal(before, SidebarFirstSeen.Frozen.Count);
        Assert.Equal(0, SidebarFirstSeen.Frozen.NewStamps);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── RECENCY — navigation recency, an identity lookup by route key (PURE, Sidebar.Host.cs) ───────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarRecencyFacts
{
    [Fact]
    public void Build_IsAnIdentityLookupOnRouteKey_NewestVisitWins()
    {
        var visits = new List<SidebarVisit>
        {
            new("pl:spotify:playlist:p1", 100),
            new("album:spotify:album:a1", 150),
            new("pl:spotify:playlist:p1", 300),      // a later visit to the same route
        };
        var recency = SidebarRecency.Build(visits);
        Assert.Equal(300L, recency.LastVisitedTicks("pl:spotify:playlist:p1"));
        Assert.Equal(150L, recency.LastVisitedTicks("album:spotify:album:a1"));
        Assert.Equal(0L, recency.LastVisitedTicks("show:spotify:show:s1"));
    }

    [Fact]
    public void Build_WorksOverAnyRowShape_ViaTheAccessorOverload()
    {
        var rows = new[] { ("home", 10L), ("pl:x", 20L), ("pl:x", 40L) };
        var recency = SidebarRecency.Build(rows, static r => r.Item1, static r => r.Item2);
        Assert.Equal(40L, recency.LastVisitedTicks("pl:x"));
        Assert.Equal(10L, recency.LastVisitedTicks("home"));
        Assert.Same(SidebarRecency.Empty, SidebarRecency.Build(Array.Empty<SidebarVisit>()));
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── PROJECTION — SidebarProjection.Build over REAL staged edges (User.Me's Rootlist/SavedAlbums/FollowedArtists/
//    SavedShows), the 0.3 replacement for the 0.2.9 LibraryStore fixture ───────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[Collection(EntitiesCollection.Name)]
public class SidebarProjectionEdgeFacts
{
    // ── fixture plumbing: TestScope.Fresh() boots a fake in-memory scope (TestScope.cs); Staging + CommitAndPublish
    // land real rows exactly like EdgesStagingTests.cs / AlbumTests.cs / ArtistTests.cs / ShowTests.cs; User.Replace /
    // User.ReplaceRootlist then write the edges directly, the same public entry point a collection sync uses
    // (Entities/User.cs: "Replace a whole relation from a provider answer").

    static User Me(string uri = "spotify:user:me")
    {
        var me = Entities.User(EntityUri.Parse(uri));
        Entities.Current.MeSlot = me.Slot;
        return me;
    }

    static void StageUser(Staging s, string uri, string name)
    {
        ref var row = ref s.Users.Add();
        row.Id = s.Text(uri);
        row.Name = s.Text(name);
        row.Known = (uint)UserFields.Identity;
        row.Authority = Authority.Full;
    }

    static Playlist StagePlaylist(Staging s, string uri, string title, string? ownerUri, int trackCount,
        PlaylistCaps caps, string? image = null)
    {
        ref var row = ref s.Playlists.Add();
        row.Id = s.Text(uri);
        row.Title = s.Text(title);
        if (ownerUri is not null) row.OwnerUri = s.Text(ownerUri);
        row.TrackCount = trackCount;
        if (image is not null) row.Image = s.Text(image);
        row.Caps = (byte)caps;
        row.Known = (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities);
        row.Authority = Authority.Full;
        return Entities.Playlist(EntityUri.Parse(uri));
    }

    static Album StageAlbum(Staging s, string uri, string title, int trackCount, string? image, params string[] artistNames)
    {
        ref var row = ref s.Albums.Add();
        row.Id = s.Text(uri);
        row.Title = s.Text(title);
        if (image is not null) row.Image = s.Text(image);
        row.TrackCount = trackCount;
        row.Known = (uint)AlbumFields.Identity;
        row.Authority = Authority.Full;

        if (artistNames.Length > 0)
        {
            var run = s.Run(Relation.AlbumArtists);
            for (int i = 0; i < artistNames.Length; i++)
            {
                string artistUri = uri + ":artist:" + i;
                ref var ar = ref s.Artists.Add();
                ar.Id = s.Text(artistUri);
                ar.Name = s.Text(artistNames[i]);
                ar.Known = (uint)ArtistFields.Identity;
                ar.Authority = Authority.Full;
                run.Add(s.Text(artistUri));
            }
            StagedId albumId = s.Text(uri);
            run.End(in albumId);
        }
        return Entities.Album(EntityUri.Parse(uri));
    }

    static Artist StageArtist(Staging s, string uri, string name)
    {
        ref var row = ref s.Artists.Add();
        row.Id = s.Text(uri);
        row.Name = s.Text(name);
        row.Known = (uint)ArtistFields.Identity;
        row.Authority = Authority.Full;
        return Entities.Artist(EntityUri.Parse(uri));
    }

    static Show StageShow(Staging s, string uri, string title, string publisher)
    {
        ref var row = ref s.Shows.Add();
        row.Id = s.Text(uri);
        row.Title = s.Text(title);
        row.Publisher = s.Text(publisher);
        row.Known = (uint)ShowFields.Identity;
        row.Authority = Authority.Full;
        return Entities.Show(EntityUri.Parse(uri));
    }

    /// <summary>A track row carrying just what <c>Playlist.MosaicTiles</c> (Entities/Playlist.UI.cs) reads: its own
    /// image and its album's slot (the dedupe key — two tracks off one album are one tile, never two). No album row is
    /// staged; <c>AlbumUri</c> alone resolves a slot at commit (Entities/Track.cs:517), which is all the dedupe needs.</summary>
    static Track StageTrack(Staging s, string uri, string albumUri, string? image)
    {
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text(uri);
        row.AlbumUri = s.Text(albumUri);
        if (image is not null) row.Image = s.Text(image);
        row.Known = (uint)TrackFields.Identity;
        row.Authority = Authority.Full;
        return Entities.Track(EntityUri.Parse(uri));
    }

    static void SetSavedAlbums(User me, params (Album Album, int AddedAt)[] saves)
    {
        var targets = new int[saves.Length];
        var payload = new LibraryEdge[saves.Length];
        for (int i = 0; i < saves.Length; i++) { targets[i] = saves[i].Album.Slot; payload[i] = new LibraryEdge(saves[i].AddedAt, 0); }
        me.Replace(LibraryEdgeKind.SavedAlbums, targets, payload, EdgeState.Complete, targets.Length);
    }

    static void SetFollowedArtists(User me, params Artist[] artists)
    {
        var targets = new int[artists.Length];
        var payload = new LibraryEdge[artists.Length];
        for (int i = 0; i < artists.Length; i++) targets[i] = artists[i].Slot;
        me.Replace(LibraryEdgeKind.FollowedArtists, targets, payload, EdgeState.Complete, targets.Length);
    }

    static void SetSavedShows(User me, params Show[] shows)
    {
        var targets = new int[shows.Length];
        var payload = new LibraryEdge[shows.Length];
        for (int i = 0; i < shows.Length; i++) targets[i] = shows[i].Slot;
        me.Replace(LibraryEdgeKind.SavedShows, targets, payload, EdgeState.Complete, targets.Length);
    }

    // Rootlist row builders. Folder identity is the rootlist GROUP id the wire carries (decision D10,
    // `RootlistEdge.FolderId`): the row's FolderId is the bare hex, its Id is "folder:<hex>", its children's
    // ParentFolderId is the bare hex — stable across a rename AND a move. The edges are built through `with` rather
    // than the positional constructor, so these builders do not depend on where the column sits in the record.
    static (int Target, RootlistEdge Edge) Item(Playlist p, byte depth, int addedAt = 0) =>
        (p.Slot, default(RootlistEdge) with { Depth = depth, Kind = (byte)RootlistKind.Item, AddedAt = addedAt });
    static (int Target, RootlistEdge Edge) FolderStart(ushort position, byte depth, string name, string? hex = null) =>
        (Table.None, default(RootlistEdge) with
        {
            Position = position, Depth = depth, Kind = (byte)RootlistKind.FolderStart,
            FolderName = Entities.Strings.Intern(name),
            FolderId = Entities.Strings.Intern(hex ?? HexOf(position)),
        });
    static (int Target, RootlistEdge Edge) FolderEnd(byte depth) =>
        (Table.None, default(RootlistEdge) with { Depth = depth, Kind = (byte)RootlistKind.FolderEnd });

    /// <summary>A deterministic 16-hex group id per fixture position ("0000000000000001" …).</summary>
    static string HexOf(int position) => position.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    static void SetRootlist(User me, params (int Target, RootlistEdge Edge)[] rows)
    {
        var targets = new int[rows.Length];
        var payload = new RootlistEdge[rows.Length];
        for (int i = 0; i < rows.Length; i++) { targets[i] = rows[i].Target; payload[i] = rows[i].Edge; }
        me.ReplaceRootlist(targets, payload);
    }

    static (List<SidebarLibraryEntry> Rows, SidebarProjectionResult Result) Build(
        User me, SidebarEntryKindMask kinds, bool flatten = true, Func<string, bool>? expanded = null,
        IReadOnlyDictionary<string, long>? lastPlayed = null, SidebarFirstSeen? firstSeen = null,
        SidebarRecency? recency = null, bool ensureIdentity = false)
    {
        var into = new List<SidebarLibraryEntry>();
        var r = SidebarProjection.Build(into, in me, kinds, firstSeen ?? new SidebarFirstSeen(() => 1_000_000L),
            recency, includeFolderChildren: flatten, isFolderExpanded: expanded, lastPlayed: lastPlayed,
            ensureIdentity: ensureIdentity);
        return (into, r);
    }

    /// <summary>A playlist row staged with NO field group known yet — <c>StagePlaylist</c> always marks Identity, so
    /// Bug H's "has this row's identity landed" facts need a row that genuinely has not answered.</summary>
    static Playlist StageColdPlaylist(Staging s, string uri)
    {
        ref var row = ref s.Playlists.Add();
        row.Id = s.Text(uri);
        return Entities.Playlist(EntityUri.Parse(uri));
    }

    static string[] Names(IReadOnlyList<SidebarLibraryEntry> l)
    {
        var a = new string[l.Count];
        for (int i = 0; i < l.Count; i++) a[i] = l[i].Name;
        return a;
    }

    // ── per-kind field derivation ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Playlist_DerivesIdOwnerAndCount_AndItsOwnCoverAlwaysWinsOverAMosaic()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        StageUser(s, "spotify:user:christos", "Christos");
        var p = StagePlaylist(s, "spotify:playlist:p1", "Focus", "spotify:user:christos", 42,
            PlaylistCaps.IsOwner | PlaylistCaps.CanView, image: "spotify:image:cover1");
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree);
        var e = Assert.Single(rows);
        Assert.Equal("pl:spotify:playlist:p1", e.Id);
        Assert.Equal(SidebarEntryKind.Playlist, e.Kind);
        Assert.Equal("spotify:playlist:p1", e.Uri);
        Assert.Equal("Focus", e.Name);
        Assert.Equal("Christos", e.Creator);
        Assert.Equal("Christos", e.OwnerName);
        Assert.Equal(42, e.ChildCount);
        Assert.Equal(42, e.TrackCount);
        Assert.False(e.Circular);
        Assert.True(e.IsPlayable);
        Assert.True(e.IsOwner);
        Assert.Equal("pl:spotify:playlist:p1", e.RouteKey);
        Assert.Equal("spotify:image:cover1", Entities.Strings.Resolve(e.Cover));
        // G-059 (fixed): a playlist with its OWN cover never pays for the track walk — MosaicTiles stays null and
        // Cover.Art (Sidebar.UI.Rows.cs) draws e.Cover instead. See the CoverlessPlaylist_* facts below for the
        // cover-less arm.
        Assert.Null(e.MosaicTiles);
    }

    // ── G-059: a cover-less playlist's own 2×2 mosaic ────────────────────────────────────────────────────────────────

    [Fact]
    public void CoverlessPlaylist_MosaicIsTheFirstFourDistinctTrackCovers_InMembershipOrder()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:mix", "Roadtrip", null, 0, PlaylistCaps.None);   // no image
        var t1 = StageTrack(s, "spotify:track:t1", "spotify:album:a1", "spotify:image:cover1");
        var t2 = StageTrack(s, "spotify:track:t2", "spotify:album:a2", "spotify:image:cover2");
        // Two tracks off the SAME album: one tile, not two (the detail page's own dedupe, Playlist.UI.cs:104).
        var t2b = StageTrack(s, "spotify:track:t2b", "spotify:album:a2", "spotify:image:cover2b");
        var t3 = StageTrack(s, "spotify:track:t3", "spotify:album:a3", "spotify:image:cover3");
        var t4 = StageTrack(s, "spotify:track:t4", "spotify:album:a4", "spotify:image:cover4");
        var t5 = StageTrack(s, "spotify:track:t5", "spotify:album:a5", "spotify:image:cover5");   // a 5th — never reached
        TestScope.CommitAndPublish(s);
        p.ApplyMembership([t1.Slot, t2.Slot, t2b.Slot, t3.Slot, t4.Slot, t5.Slot], default);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree);
        var e = Assert.Single(rows);
        Assert.True(e.Cover.IsEmpty);
        Assert.NotNull(e.MosaicTiles);
        var tiles = e.MosaicTiles!;
        Assert.Equal(4, tiles.Count);
        var urls = new string[tiles.Count];
        for (int i = 0; i < tiles.Count; i++) urls[i] = Entities.Strings.Resolve(tiles[i]);
        Assert.Equal(
            new[] { "spotify:image:cover1", "spotify:image:cover2", "spotify:image:cover3", "spotify:image:cover4" },
            urls);
    }

    [Fact]
    public void CoverlessPlaylist_WithFewerThanFourCoveredTracks_KeepsThePartialMosaic()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:duo", "Duo", null, 0, PlaylistCaps.None);
        var t1 = StageTrack(s, "spotify:track:d1", "spotify:album:d1", "spotify:image:duo1");
        var t2 = StageTrack(s, "spotify:track:d2", "spotify:album:d2", "spotify:image:duo2");
        TestScope.CommitAndPublish(s);
        p.ApplyMembership([t1.Slot, t2.Slot], default);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree);
        var row = Assert.Single(rows);
        Assert.NotNull(row.MosaicTiles);
        var tiles = row.MosaicTiles!;
        // Cover.Art (Sidebar.UI.Rows.cs) takes tile 0 as a single cover below 4 — same partial shape the folder
        // mosaic already produced, so this row lands on the exact same rendering branch.
        Assert.Equal(2, tiles.Count);
        Assert.Equal("spotify:image:duo1", Entities.Strings.Resolve(tiles[0]));
    }

    [Fact]
    public void CoverlessPlaylist_WithTracksButNoCoversYet_MosaicStaysNull()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:blank", "Blank", null, 0, PlaylistCaps.None);
        var t1 = StageTrack(s, "spotify:track:n1", "spotify:album:n1", image: null);
        var t2 = StageTrack(s, "spotify:track:n2", "spotify:album:n2", image: null);
        TestScope.CommitAndPublish(s);
        p.ApplyMembership([t1.Slot, t2.Slot], default);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree);
        // Fewer than one cover known ⇒ null, so the row falls back to the neutral placeholder tile rather than an
        // empty mosaic array.
        Assert.Null(Assert.Single(rows).MosaicTiles);
    }

    [Fact]
    public void CoverlessPlaylist_TracksNotLoadedYet_MosaicStaysNullUntilTheyLand()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:pending", "Pending", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);   // the PlaylistTracks edge is untouched: EdgeState.Unknown

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (before, _) = Build(me, SidebarEntryKindMask.PlaylistTree);
        // Not "no cover known" — "not asked yet": a premature null-tiles walk must not run, or it would settle on
        // an empty mosaic before the tracks edge has even been fetched.
        Assert.Null(Assert.Single(before).MosaicTiles);

        var s2 = Staging.Rent();
        var t1 = StageTrack(s2, "spotify:track:late1", "spotify:album:late1", "spotify:image:late1");
        var t2 = StageTrack(s2, "spotify:track:late2", "spotify:album:late2", "spotify:image:late2");
        TestScope.CommitAndPublish(s2);
        p.ApplyMembership([t1.Slot, t2.Slot], default);

        var (after, _) = Build(me, SidebarEntryKindMask.PlaylistTree);
        Assert.NotNull(Assert.Single(after).MosaicTiles);
    }

    [Fact]
    public void Album_JoinsUpToThreeArtists_AndKeepsTheFirstOne()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        StageAlbum(s, "spotify:album:a1", "Discovery", trackCount: 14, image: null, "Daft Punk");
        StageAlbum(s, "spotify:album:a2", "Split", trackCount: 9, image: null, "A", "B", "C", "D");
        TestScope.CommitAndPublish(s);

        var me = Me();
        var al1 = Entities.Album(EntityUri.Parse("spotify:album:a1"));
        var al2 = Entities.Album(EntityUri.Parse("spotify:album:a2"));
        SetSavedAlbums(me, (al2, 100), (al1, 200));   // newest (by our own order) first, as the edge already is

        var (rows, _) = Build(me, SidebarEntryKindMask.Album);
        Assert.Equal(2, rows.Count);
        var one = rows.Find(r => r.Name == "Discovery");
        Assert.Equal("album:spotify:album:a1", one.Id);
        Assert.Equal("Daft Punk", one.Creator);
        Assert.Equal("Daft Punk", one.FirstArtistName);
        Assert.Equal(14, one.TrackCount);

        var many = rows.Find(r => r.Name == "Split");
        Assert.Equal("A, B, C…", many.Creator);
        Assert.Equal("A", many.FirstArtistName);
    }

    [Fact]
    public void Artist_IsCircular_AndHasNoCreator()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        StageArtist(s, "spotify:artist:x", "Radiohead");
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetFollowedArtists(me, Entities.Artist(EntityUri.Parse("spotify:artist:x")));

        var (rows, _) = Build(me, SidebarEntryKindMask.Artist);
        var e = Assert.Single(rows);
        Assert.Equal("artist:spotify:artist:x", e.Id);
        Assert.True(e.Circular);
        Assert.Equal("", e.Creator);
        Assert.Equal(0, e.ChildCount);
        Assert.False(e.IsPlayable);
    }

    [Fact]
    public void Show_ProjectsPublisherAsCreatorAndSubtitle()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        StageShow(s, "spotify:show:s1", "The Daily", "The New York Times");
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetSavedShows(me, Entities.Show(EntityUri.Parse("spotify:show:s1")));

        var (rows, _) = Build(me, SidebarEntryKindMask.Show);
        var e = Assert.Single(rows);
        Assert.Equal("show:spotify:show:s1", e.Id);
        Assert.Equal("The New York Times", e.Creator);
        Assert.Equal("The New York Times", e.Publisher);
        Assert.Equal("", e.OwnerName);
        Assert.True(e.IsPlayable);
    }

    [Fact]
    public void KindMask_SelectsExactlyTheRequestedFamilies()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:p", "P", null, 0, PlaylistCaps.None);
        StageAlbum(s, "spotify:album:a", "A", 0, null, "Artist");
        StageArtist(s, "spotify:artist:r", "R");
        StageShow(s, "spotify:show:sh", "S", "Pub");
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));
        SetSavedAlbums(me, (Entities.Album(EntityUri.Parse("spotify:album:a")), 0));
        SetFollowedArtists(me, Entities.Artist(EntityUri.Parse("spotify:artist:r")));
        SetSavedShows(me, Entities.Show(EntityUri.Parse("spotify:show:sh")));

        var (all, _) = Build(me, SidebarEntryKindMask.All);
        Assert.Equal(4, all.Count);

        var (onlyShows, _) = Build(me, SidebarEntryKindMask.Show);
        Assert.Equal(SidebarEntryKind.Show, Assert.Single(onlyShows).Kind);
    }

    // ── folder recursion ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlattenedTree_StampsDepthFolderIdAndChildCountAndMosaic()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var top = StagePlaylist(s, "spotify:playlist:top", "Top", null, 0, PlaylistCaps.None);
        var in1 = StagePlaylist(s, "spotify:playlist:in1", "Inner one", null, 0, PlaylistCaps.None, image: "spotify:image:i1");
        var in2 = StagePlaylist(s, "spotify:playlist:in2", "Inner two", null, 0, PlaylistCaps.None, image: "spotify:image:i2");
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me,
            Item(top, depth: 0),
            FolderStart(position: 1, depth: 0, name: "Cafe & chill"),
            Item(in1, depth: 1),
            Item(in2, depth: 1),
            FolderEnd(depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree, flatten: true);
        Assert.Equal(new[] { "Top", "Cafe & chill", "Inner one", "Inner two" }, Names(rows));

        Assert.Equal(0, rows[0].Depth);
        Assert.Equal("", rows[0].FolderId);

        var cafe = rows[1];
        Assert.True(cafe.IsFolder);
        Assert.Equal("folder:" + HexOf(1), cafe.Id);             // the pin id: ForFolder(group hex)
        Assert.Equal(HexOf(1), cafe.FolderId);                   // the BARE group hex, 0.2.9's convention
        Assert.Equal("", cafe.ParentFolderId);                   // top level
        Assert.Equal(2, cafe.ChildCount);                       // two DIRECT playlist children
        Assert.Null(cafe.RouteKey);
        Assert.Equal("", cafe.Uri);
        Assert.NotNull(cafe.MosaicTiles);
        Assert.Equal(2, cafe.MosaicTiles!.Count);                // the folder mosaic (unlike a playlist's) still ports

        Assert.Equal(1, rows[2].Depth);
        Assert.Equal(HexOf(1), rows[2].FolderId);
        Assert.Equal("Cafe & chill", rows[2].FolderName);
        Assert.Equal(HexOf(1), rows[2].ParentFolderId);
        Assert.Equal("Cafe & chill", rows[2].ParentFolderName);

        for (int i = 0; i < rows.Count; i++) Assert.Equal(i, rows[i].SourceOrder);   // rootlist order, folders included
    }

    /// <summary>A sub-folder counts as a DIRECT child of its enclosing folder, exactly like a playlist, as 0.2.9
    /// counted it (one playlist + one sub-folder inside a folder is ChildCount == 2).</summary>
    [Fact]
    public void NestedFolder_CountsTowardItsParentsChildCount()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var a = StagePlaylist(s, "spotify:playlist:a", "A", null, 0, PlaylistCaps.None);
        var deep = StagePlaylist(s, "spotify:playlist:deep", "Deep", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me,
            FolderStart(position: 1, depth: 0, name: "Outer"),
            Item(a, depth: 1),
            FolderStart(position: 2, depth: 1, name: "Inner"),
            Item(deep, depth: 2),
            FolderEnd(depth: 1),
            FolderEnd(depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree, flatten: true);
        var outer = rows.Find(r => r.Id == "folder:" + HexOf(1));
        var inner = rows.Find(r => r.Id == "folder:" + HexOf(2));
        Assert.Equal(2, outer.ChildCount);     // "A" and the "Inner" sub-folder, both direct children
        Assert.Equal(1, inner.ChildCount);     // "Deep"
        Assert.Equal(HexOf(1), inner.ParentFolderId);   // a sub-folder's parent is the enclosing folder's bare hex
    }

    /// <summary>D10: a folder's identity is its GROUP id, so moving it — or putting a new folder in front of it — keeps
    /// its id, its expansion key and its pin; the old position-derived id moved with every reorder.</summary>
    [Fact]
    public void A_moved_folder_keeps_its_group_id()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var a = StagePlaylist(s, "spotify:playlist:a", "A", null, 0, PlaylistCaps.None);
        var b = StagePlaylist(s, "spotify:playlist:b", "B", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me,
            FolderStart(position: 0, depth: 0, name: "Mixes", hex: "6a1f2c"),
            Item(a, depth: 1),
            FolderEnd(depth: 0),
            Item(b, depth: 0));
        var (before, _) = Build(me, SidebarEntryKindMask.PlaylistTree, flatten: true);

        SetRootlist(me,
            Item(b, depth: 0),
            FolderStart(position: 1, depth: 0, name: "Mixes renamed", hex: "6a1f2c"),
            Item(a, depth: 1),
            FolderEnd(depth: 0));
        var (after, _) = Build(me, SidebarEntryKindMask.PlaylistTree, flatten: true);

        var was = before.Find(r => r.IsFolder);
        var now = after.Find(r => r.IsFolder);
        Assert.Equal("folder:6a1f2c", was.Id);
        Assert.Equal(was.Id, now.Id);
        Assert.Equal("6a1f2c", now.FolderId);
        Assert.Equal("6a1f2c", after.Find(r => r.Name == "A").ParentFolderId);
        // The expansion predicate is asked with the bare hex — the key a 0.2.9 `v3.expandedFolders` array holds.
        var (expanded, _) = Build(me, SidebarEntryKindMask.PlaylistTree, flatten: false, expanded: id => id == "6a1f2c");
        Assert.Contains(expanded, r => r.Name == "A");
    }

    /// <summary>A FolderStart that arrived without a group id still gets a row key unique within the tree, and one no
    /// real hex id, pin or synced uri can collide with.</summary>
    [Fact]
    public void A_folder_without_a_group_id_is_keyed_by_its_position_under_a_non_hex_prefix()
    {
        TestScope.Fresh();
        var edge = default(RootlistEdge) with { Position = 7, Kind = (byte)RootlistKind.FolderStart };
        Assert.Equal("~7", SidebarProjection.FolderIdOf(in edge));
        Assert.True(EntityUri.FolderIdOf("spotify:folder:~7").IsEmpty);
    }

    [Fact]
    public void CollapsedFolder_IsOpaque_AndAnExpandedOneRevealsItsChildren()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var top = StagePlaylist(s, "spotify:playlist:top", "Top", null, 0, PlaylistCaps.None);
        var in1 = StagePlaylist(s, "spotify:playlist:in1", "Inner one", null, 0, PlaylistCaps.None);
        var in2 = StagePlaylist(s, "spotify:playlist:in2", "Inner two", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me,
            Item(top, depth: 0),
            FolderStart(position: 1, depth: 0, name: "Cafe & chill"),
            Item(in1, depth: 1),
            Item(in2, depth: 1),
            FolderEnd(depth: 0));

        var (collapsed, _) = Build(me, SidebarEntryKindMask.PlaylistTree, flatten: false, expanded: null);
        Assert.Equal(new[] { "Top", "Cafe & chill" }, Names(collapsed));

        var (expanded, _) = Build(me, SidebarEntryKindMask.PlaylistTree, flatten: false,
            expanded: id => id == HexOf(1));
        Assert.Equal(new[] { "Top", "Cafe & chill", "Inner one", "Inner two" }, Names(expanded));
    }

    [Fact]
    public void PlaylistsOnlyMask_HidesFoldersButKeepsEveryLeaf()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var top = StagePlaylist(s, "spotify:playlist:top", "Top", null, 0, PlaylistCaps.None);
        var in1 = StagePlaylist(s, "spotify:playlist:in1", "Inner one", null, 0, PlaylistCaps.None);
        var in2 = StagePlaylist(s, "spotify:playlist:in2", "Inner two", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me,
            Item(top, depth: 0),
            FolderStart(position: 1, depth: 0, name: "Cafe & chill"),
            Item(in1, depth: 1),
            Item(in2, depth: 1),
            FolderEnd(depth: 0));

        // Dropping the Folder bit must never drop the playlists inside a folder.
        var (rows, _) = Build(me, SidebarEntryKindMask.Playlist, flatten: false);
        Assert.Equal(new[] { "Top", "Inner one", "Inner two" }, Names(rows));
    }

    // ── flavor (playlist provenance for the qualifier chips) ────────────────────────────────────────────────────────

    [Fact]
    public void FlavorOf_PartitionsByYou_BySpotify_Mixed_AndNone()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        StageUser(s, "spotify:user:spotify_editorial", "Spotify");
        StageUser(s, "spotify:user:zoe", "Zoe");
        var mine = StagePlaylist(s, "spotify:playlist:mine", "Mine", null, 0, PlaylistCaps.IsOwner | PlaylistCaps.CanView);
        var editorial = StagePlaylist(s, "spotify:playlist:ed", "Ed", "spotify:user:spotify_editorial", 0, PlaylistCaps.CanView);
        var collab = StagePlaylist(s, "spotify:playlist:collab", "Collab", "spotify:user:zoe", 0,
            PlaylistCaps.CanView | PlaylistCaps.CanEditItems);
        var someoneElses = StagePlaylist(s, "spotify:playlist:se", "SE", "spotify:user:zoe", 0, PlaylistCaps.CanView);
        var unknown = StagePlaylist(s, "spotify:playlist:unk", "Unknown", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        Assert.Equal(SidebarPlaylistFlavor.ByYou, SidebarProjection.FlavorOf(in mine));
        Assert.Equal(SidebarPlaylistFlavor.BySpotify, SidebarProjection.FlavorOf(in editorial));
        Assert.Equal(SidebarPlaylistFlavor.Mixed, SidebarProjection.FlavorOf(in collab));      // collaborative
        Assert.Equal(SidebarPlaylistFlavor.Mixed, SidebarProjection.FlavorOf(in someoneElses)); // someone else's, view-only
        Assert.Equal(SidebarPlaylistFlavor.None, SidebarProjection.FlavorOf(in unknown));       // the data does not say
    }

    // ── LastPlayedMs — the "Recents" sort key, stamped by uri, never by route ───────────────────────────────────────

    [Fact]
    public void Build_StampsLastPlayedMs_FromTheMapByUri_AndZeroWhenAbsent()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var played = StagePlaylist(s, "spotify:playlist:played", "Played", null, 0, PlaylistCaps.None);
        var never = StagePlaylist(s, "spotify:playlist:never", "Never", null, 0, PlaylistCaps.None);
        StageAlbum(s, "spotify:album:a1", "PlayedAlbum", 0, null, "X");
        StageArtist(s, "spotify:artist:r1", "PlayedArtist");
        StageShow(s, "spotify:show:s1", "PlayedShow", "Pub");
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me, Item(played, depth: 0), Item(never, depth: 0));
        SetSavedAlbums(me, (Entities.Album(EntityUri.Parse("spotify:album:a1")), 0));
        SetFollowedArtists(me, Entities.Artist(EntityUri.Parse("spotify:artist:r1")));
        SetSavedShows(me, Entities.Show(EntityUri.Parse("spotify:show:s1")));

        var lastPlayed = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["spotify:playlist:played"] = 555L,
            ["spotify:album:a1"] = 777L,
            ["spotify:artist:r1"] = 888L,
            ["spotify:show:s1"] = 999L,
        };

        var (rows, _) = Build(me, SidebarEntryKindMask.All, lastPlayed: lastPlayed);
        var byName = new Dictionary<string, SidebarLibraryEntry>(StringComparer.Ordinal);
        foreach (var e in rows) byName[e.Name] = e;

        Assert.Equal(555L, byName["Played"].LastPlayedMs);
        Assert.Equal(0L, byName["Never"].LastPlayedMs);
        Assert.Equal(777L, byName["PlayedAlbum"].LastPlayedMs);
        Assert.Equal(888L, byName["PlayedArtist"].LastPlayedMs);
        Assert.Equal(999L, byName["PlayedShow"].LastPlayedMs);
    }

    [Fact]
    public void Build_WithNoLastPlayedMap_EveryRowStaysZero()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:p1", "P1", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree, lastPlayed: null);
        Assert.Equal(0L, Assert.Single(rows).LastPlayedMs);
    }

    [Fact]
    public void Build_ReusesTheCallersList_AndReportsItsCount()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:p1", "P1", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var into = new List<SidebarLibraryEntry> { SidebarLibraryEntry.ForRoute("stale", "Stale") };
        var r = SidebarProjection.Build(into, in me, SidebarEntryKindMask.All,
            new SidebarFirstSeen(() => 1_000_000L), null, includeFolderChildren: true);
        Assert.Equal(into.Count, r.Count);
        Assert.DoesNotContain("Stale", Names(into));
    }

    // ── SortStamp — an edge's AddedAt of 0 means "never dated": AddedAtMs stays 0 and the sort stamp falls back to
    //    the local first-seen stamp; any other value is unix seconds, scaled to ms.

    [Fact]
    public void SortStamp_ForASavedAlbum_ReflectsTheEdgesAddedAt_InUnixSeconds()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        StageAlbum(s, "spotify:album:a1", "A", 0, null, "X");
        TestScope.CommitAndPublish(s);

        var me = Me();
        var al = Entities.Album(EntityUri.Parse("spotify:album:a1"));
        SetSavedAlbums(me, (al, 12_345));

        var (rows, _) = Build(me, SidebarEntryKindMask.Album);
        var e = Assert.Single(rows);
        Assert.Equal(12_345L * 1000L, e.AddedAtMs);
        Assert.Equal(e.AddedAtMs, e.SortStamp);
    }

    [Fact]
    public void An_undated_playlist_falls_back_to_its_first_seen_stamp()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:p1", "P1", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0, addedAt: 0));    // 0 = "never dated"

        var (rows, r) = Build(me, SidebarEntryKindMask.PlaylistTree);
        Assert.Equal(0L, rows[0].AddedAtMs);
        Assert.Equal(1_000_000L, rows[0].SortStamp);       // the Build helper's first-seen clock
        Assert.Equal(1, r.NewFirstSeenStamps);
    }

    // ── Bug H: IdentityKnown + the `ensureIdentity` gate ────────────────────────────────────────────────────────────
    //
    // Pinned/library rows painted "0 songs" and a grey tile until the user opened the playlist's own page, because
    // WalkRootlist read TrackCount/ImageId with no Entities.Ensure. These facts drive the REAL fix over REAL staged
    // rows: `IdentityKnown` on the emitted entry (never inferred from TrackCount == 0 — a genuinely empty playlist
    // must still say "0 songs"), and `ensureIdentity` — the ONLY caller-controlled bit that may fire the ask, so a
    // structural full/tree walk (`includeFolderChildren:true`) never warms a whole rootlist's identity by accident.
    // `Table.Touched` is the observable proof a fetch was actually queued (`Entities.Ensure` stamps it before
    // planning the batch) — the same technique EdgeDoorTests/FetchTests use, without needing a registered provider:
    // `Fetch.Pump()` silently skips undispatchable batches (Fetch.cs's `s_providers[...] is null` guard).

    [Fact]
    public void Playlist_WithLandedIdentity_IsStampedIdentityKnown()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:known", "Known", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);
        Assert.True(Assert.Single(rows).IdentityKnown);
    }

    [Fact]
    public void Playlist_WithUnknownIdentity_IsNotStampedIdentityKnown()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StageColdPlaylist(s, "spotify:playlist:cold");
        TestScope.CommitAndPublish(s);
        Assert.False(p.Knows(PlaylistFields.Identity));

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);
        Assert.False(Assert.Single(rows).IdentityKnown);
    }

    // OBSERVABLE: `Asked`, not `Touched`. `Entities.Ensure` sets `Touched[slot] = Entities.Now` (Entities.cs:1913)
    // and so does `Alloc` (:1036) — and `Now` does not advance inside a test, so a before/after compare on it is
    // ALWAYS equal: the positive facts could never pass and the negative ones passed vacuously. `Fetch.cs:426`
    // (`table.Asked[slot] |= groups`) is what a plan actually marks, and it starts at 0 on a cold row.
    [Fact]
    public void EnsureIdentity_True_AsksForAVisiblePlaylistsIdentity_WhenItHasNotLandedYet()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StageColdPlaylist(s, "spotify:playlist:cold");
        TestScope.CommitAndPublish(s);
        uint before = Entities.Current.Playlists.Asked[p.Slot];

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);

        Assert.NotEqual(before, Entities.Current.Playlists.Asked[p.Slot]);
    }

    /// <summary>The structural full/tree passes (`Sidebar.Host.Rebuild`'s `build.All`/`build.Tree`) call `Build` with
    /// `ensureIdentity` left at its default false — this is the fact that guards against ever flipping that default,
    /// which would ask for a whole hundreds-deep rootlist's identity on every rebuild.</summary>
    [Fact]
    public void EnsureIdentity_DefaultsToFalse_AndAsksForNothing()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StageColdPlaylist(s, "spotify:playlist:cold");
        TestScope.CommitAndPublish(s);
        uint before = Entities.Current.Playlists.Asked[p.Slot];

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        Build(me, SidebarEntryKindMask.PlaylistTree);   // ensureIdentity not passed

        Assert.Equal(before, Entities.Current.Playlists.Asked[p.Slot]);
    }

    /// <summary>A row hidden inside a COLLAPSED folder must never be asked for, even when `ensureIdentity` is true —
    /// the one thing that keeps a large rootlist cheap. `flatten: false` + no `expanded` predicate reproduces the
    /// real "published buffer" pass over a folder nobody opened.</summary>
    [Fact]
    public void EnsureIdentity_NeverAsksForARowInsideACollapsedFolder()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var inner = StageColdPlaylist(s, "spotify:playlist:hidden");
        TestScope.CommitAndPublish(s);
        uint before = Entities.Current.Playlists.Asked[inner.Slot];

        var me = Me();
        SetRootlist(me,
            FolderStart(position: 1, depth: 0, name: "Closed"),
            Item(inner, depth: 1),
            FolderEnd(depth: 0));

        // flatten:false + expanded:null ⇒ the folder is collapsed, exactly like the real buffer build when nobody
        // opened it.
        Build(me, SidebarEntryKindMask.PlaylistTree, flatten: false, expanded: null, ensureIdentity: true);

        Assert.Equal(before, Entities.Current.Playlists.Asked[inner.Slot]);
    }

    [Fact]
    public void EnsureIdentity_AsksForARowInsideAnExpandedFolder()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var inner = StageColdPlaylist(s, "spotify:playlist:open-child");
        TestScope.CommitAndPublish(s);
        uint before = Entities.Current.Playlists.Asked[inner.Slot];

        var me = Me();
        SetRootlist(me,
            FolderStart(position: 1, depth: 0, name: "Open", hex: "aa"),
            Item(inner, depth: 1),
            FolderEnd(depth: 0));

        Build(me, SidebarEntryKindMask.PlaylistTree, flatten: false, expanded: id => id == "aa", ensureIdentity: true);

        Assert.NotEqual(before, Entities.Current.Playlists.Asked[inner.Slot]);
    }

    /// <summary>Bug H's membership half: a cover-less row's PlaylistTracks edge is ensured too, but only while it is
    /// genuinely unknown — a fully-loaded membership that simply found no covers must never re-ask.</summary>
    [Fact]
    public void EnsureIdentity_AsksForACoverlessRowsMembership_OnlyWhileItIsUnknown()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:coverless", "Coverless", null, 0, PlaylistCaps.None);   // no image
        TestScope.CommitAndPublish(s);
        Assert.Equal(EdgeState.Unknown, p.MembershipState);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p.Slot, 0));
        Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);
        // `WasAsked` (the planner's own dedupe ledger) is what actually flips on a plan — `State` stays Unknown
        // until a real answer lands, which no test here waits for (no provider is registered).
        Assert.True(Entities.Current.Edges.PlaylistTracks.WasAsked(p.Slot, 0));
    }

    /// <summary>D2 (issue #4): the deleted bug A1 `ShouldEnsureCount` used to force exactly this ask — a COVERED row
    /// with an unknown count got its full `PlaylistTracks` edge read just to learn a number (the ~30 full
    /// `GET /playlist/v2/playlist/{id}` reads proven at boot). `StagePlaylist` never marks `PlaylistFields.TrackCount`
    /// known (it stages Identity+Capabilities only, exactly the shape a real ListMetadataV2 answer leaves) — the
    /// count now simply stays unknown, and the row shows none; it is never a reason to ask anything.</summary>
    [Fact]
    public void CoveredRowWithAnUnknownCount_IsNeverAskedForItsTracks()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:covered", "Covered", null, 0, PlaylistCaps.None,
            image: "spotify:image:cover");
        TestScope.CommitAndPublish(s);
        Assert.False(p.Knows(PlaylistFields.TrackCount));

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);
        Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p.Slot, 0));
        Assert.False(Assert.Single(rows).CountKnown);   // no ask ⇒ no count ⇒ no subtitle, never a confident zero
    }

    /// <summary>The positive control: a covered row whose count IS already known is never asked either — same
    /// conclusion (nothing left to warm), reached from the other starting state.</summary>
    [Fact]
    public void EnsureIdentity_NeverAsksAFullyKnownCoveredRows_Tracks()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Playlists.Add();
        row.Id = s.Text("spotify:playlist:covered-counted");
        row.Title = s.Text("Covered Counted");
        row.Image = s.Text("spotify:image:cover");
        row.TrackCount = 7;
        row.Known = (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities | PlaylistFields.TrackCount);
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);
        var p = Entities.Playlist(EntityUri.Parse("spotify:playlist:covered-counted"));
        Assert.True(p.Knows(PlaylistFields.TrackCount));

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);
        Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p.Slot, 0));
    }

    // ── the mosaic warm: the leading member tracks' album + image, once membership has landed ────────────────────
    //
    // `Decode.PlaylistRevision` lands a membership as BARE uri-only track slots (Item(): field 1 = the uri, nothing
    // else), so a cover-less playlist's tiles have nothing to read until each leading track's own row answers. The
    // rail used to leave that to the playlist PAGE (`DemandRows`' TrackFields.Row ask) — an empty tile until the page
    // had been opened once, every launch. These facts pin the projection's own bounded ask.

    /// <summary>A cover-less visible row whose membership HAS landed asks the first <see cref="SidebarProjection.MosaicTrackPrefix"/>
    /// member tracks for <see cref="SidebarProjection.MosaicTrackFields"/> — and nothing past that prefix. The
    /// membership edge itself is NOT re-asked: it already answered.</summary>
    [Fact]
    public void EnsureIdentity_WarmsTheLeadingMemberTracksAlbumAndImage_OnceACoverlessRowsMembershipLanded()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:bare", "Bare", null, 0, PlaylistCaps.None);   // no image
        TestScope.CommitAndPublish(s);
        var members = BareTracks("spotify:track:bare", SidebarProjection.MosaicTrackPrefix + 2);
        p.ApplyMembership(members, default);
        Assert.Equal(EdgeState.Complete, p.MembershipState);

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));
        Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);

        var tracks = Entities.Current.Tracks;
        uint want = (uint)SidebarProjection.MosaicTrackFields;
        for (int i = 0; i < members.Length; i++)
        {
            bool inPrefix = i < SidebarProjection.MosaicTrackPrefix;
            Assert.Equal(inPrefix ? want : 0u, tracks.Asked[members[i]] & want);
        }
        Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p.Slot, 0));
    }

    /// <summary>The two gates the warm shares with every other projection ask: a row with its own cover never pays for
    /// the track walk, and a structural (non-visible) pass asks for nothing.</summary>
    [Fact]
    public void EnsureIdentity_NeverWarmsMosaicTracks_ForACoveredRow_OrFromAStructuralPass()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var covered = StagePlaylist(s, "spotify:playlist:covered-bare", "Covered", null, 0, PlaylistCaps.None,
            image: "spotify:image:cover");
        var coverless = StagePlaylist(s, "spotify:playlist:coverless-bare", "Coverless", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);
        var coveredMembers = BareTracks("spotify:track:cb", 3);
        var coverlessMembers = BareTracks("spotify:track:clb", 3);
        covered.ApplyMembership(coveredMembers, default);
        coverless.ApplyMembership(coverlessMembers, default);

        var me = Me();
        SetRootlist(me, Item(covered, depth: 0), Item(coverless, depth: 0));
        var tracks = Entities.Current.Tracks;
        uint want = (uint)SidebarProjection.MosaicTrackFields;

        Build(me, SidebarEntryKindMask.PlaylistTree);   // ensureIdentity defaults to false: the structural passes
        foreach (int t in coverlessMembers) Assert.Equal(0u, tracks.Asked[t] & want);

        Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);
        foreach (int t in coveredMembers) Assert.Equal(0u, tracks.Asked[t] & want);
        foreach (int t in coverlessMembers) Assert.Equal(want, tracks.Asked[t] & want);
    }

    /// <summary>A full mosaic (four distinct covers already in hand) asks for nothing more — the bare tracks behind
    /// it stay unasked — and, short of four, only the rows that do not yet know album+image are asked (a track that
    /// answered, from disk or the wire, is never re-asked).</summary>
    [Fact]
    public void EnsureIdentity_MosaicWarm_SkipsKnownTracks_AndStopsOnceFourTilesAreInHand()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var full = StagePlaylist(s, "spotify:playlist:full-mosaic", "Full", null, 0, PlaylistCaps.None);
        var half = StagePlaylist(s, "spotify:playlist:half-mosaic", "Half", null, 0, PlaylistCaps.None);
        var fullKnown = new int[4];
        for (int i = 0; i < 4; i++)
            fullKnown[i] = StageTrack(s, "spotify:track:fk" + i, "spotify:album:fk" + i, "spotify:image:fk" + i).Slot;
        int halfKnown = StageTrack(s, "spotify:track:hk", "spotify:album:hk", "spotify:image:hk").Slot;
        TestScope.CommitAndPublish(s);
        var fullBare = BareTracks("spotify:track:fb", 2);
        var halfBare = BareTracks("spotify:track:hb", 2);
        full.ApplyMembership([.. fullKnown, .. fullBare], default);
        half.ApplyMembership([halfKnown, .. halfBare], default);

        var me = Me();
        SetRootlist(me, Item(full, depth: 0), Item(half, depth: 0));
        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);
        Assert.Equal(4, rows[0].MosaicTiles!.Count);
        Assert.Single(rows[1].MosaicTiles!);

        var tracks = Entities.Current.Tracks;
        uint want = (uint)SidebarProjection.MosaicTrackFields;
        foreach (int t in fullBare) Assert.Equal(0u, tracks.Asked[t] & want);      // four tiles: settled
        Assert.Equal(0u, tracks.Asked[halfKnown] & want);                          // already knows both
        foreach (int t in halfBare) Assert.Equal(want, tracks.Asked[t] & want);    // the ones the tiles still need
    }

    /// <summary>Bare uri-only track slots — the exact shape a `PlaylistRevision` membership answer leaves behind: a
    /// slot allocated for the uri, no field group known, nothing asked.</summary>
    static int[] BareTracks(string uriPrefix, int count)
    {
        var slots = new int[count];
        for (int i = 0; i < count; i++)
        {
            var t = Entities.Track(EntityUri.Parse(uriPrefix + i));
            Assert.False(t.Knows(SidebarProjection.MosaicTrackFields));
            slots[i] = t.Slot;
        }
        return slots;
    }

    /// <summary>THE USER'S ACTUAL STATE (bug A1): `StagePlaylist` marks Identity+Capabilities known — exactly what a
    /// ListMetadataV2 answer leaves — but never `PlaylistFields.TrackCount`, because that route carries no length.
    /// The emitted row must carry `IdentityKnown: true` and `CountKnown: false`, never inferring the second from the
    /// first.</summary>
    [Fact]
    public void Playlist_WithLandedIdentityButNoLength_IsNotStampedCountKnown()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:thin", "Thin", null, 43, PlaylistCaps.None,
            image: "spotify:image:cover");
        TestScope.CommitAndPublish(s);
        Assert.True(p.Knows(PlaylistFields.Identity));
        Assert.False(p.Knows(PlaylistFields.TrackCount));

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree);
        var e = Assert.Single(rows);
        Assert.True(e.IdentityKnown);
        Assert.False(e.CountKnown);
    }

    /// <summary>D2: the one free upgrade over the fact above — once membership is resident for some OTHER reason (a
    /// cover-less row's own mosaic ask), its <see cref="Playlist.MembershipTotal"/> IS the real count, so the row
    /// shows it without a second ask. This is bug A1's own proven shape (Identity landed off a thin route that never
    /// carried a length) recovered for free instead of masked as "unknown".</summary>
    [Fact]
    public void ACoverlessRowsLandedMembership_FillsTheCount_WhenTheLengthFieldNeverDid()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p = StagePlaylist(s, "spotify:playlist:thin-coverless", "Thin", null, 0, PlaylistCaps.None);   // no image
        var t1 = StageTrack(s, "spotify:track:tc1", "spotify:album:tc1", "spotify:image:tc1");
        var t2 = StageTrack(s, "spotify:track:tc2", "spotify:album:tc2", "spotify:image:tc2");
        var t3 = StageTrack(s, "spotify:track:tc3", "spotify:album:tc3", "spotify:image:tc3");
        TestScope.CommitAndPublish(s);
        p.ApplyMembership([t1.Slot, t2.Slot, t3.Slot], default);
        Assert.False(p.Knows(PlaylistFields.TrackCount));       // the bit never landed
        Assert.Equal(3, p.MembershipTotal);                     // but the resident edge already knows the real count

        var me = Me();
        SetRootlist(me, Item(p, depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree);
        var e = Assert.Single(rows);
        Assert.True(e.CountKnown);
        Assert.Equal(3, e.TrackCount);
    }

    /// <summary>The real decoders, not the `StagePlaylist` shortcut: a PlaylistRead-shaped answer
    /// (`Spotify.Decode.PlaylistRevision`, the SelectedListContent shape a real `contents` fetch decodes) marks the
    /// count known because it carries the wire's `length` field; a ListMetadataV2-shaped answer (ext kind 205,
    /// `Spotify.Decode.ListMetadataV2`) never does, because that proto has no length field at all
    /// (Protos/list_metadata_v2.proto). Staged through the same Staging/TestScope pipeline `DecodeTests.cs` uses.</summary>
    [Fact]
    public void ARealPlaylistReadAnswer_MarksTheCountKnown_AListMetadataV2AnswerNever_Does()
    {
        TestScope.Fresh();
        string uri = "spotify:playlist:full-read";

        var full = new Wavee.Protocol.Playlist.SelectedListContent
        {
            Length = 43,
            Attributes = new Wavee.Protocol.Playlist.ListAttributes { Name = "Full Read" },
            Contents = new Wavee.Protocol.Playlist.ListItems { Pos = 0, Truncated = false },
        }.ToByteArray();
        var s1 = Staging.Rent();
        Spotify.Decode.PlaylistRevision(full, System.Text.Encoding.UTF8.GetBytes(uri), s1);
        TestScope.CommitAndPublish(s1);

        var p = Entities.Playlist(EntityUri.Parse(uri));
        Assert.True(p.Knows(PlaylistFields.TrackCount));
        Assert.Equal(43, p.TrackCount);

        TestScope.Fresh();
        string uri2 = "spotify:playlist:thin-meta";
        var meta = new Wavee.Protocol.ExtendedMetadata.ListMetadataV2 { Name = "Thin Meta" };
        var s2 = Staging.Rent();
        Spotify.Decode.ListMetadataV2(meta.ToByteArray(), System.Text.Encoding.UTF8.GetBytes(uri2), s2);
        TestScope.CommitAndPublish(s2);

        var p2 = Entities.Playlist(EntityUri.Parse(uri2));
        Assert.True(p2.Knows(PlaylistFields.Identity));
        Assert.False(p2.Knows(PlaylistFields.TrackCount));
    }

    /// <summary>THE REGRESSION (library.db evidence, 2026-09-15): "Eurodance Mix" — a real 50-track, Spotify-made
    /// mix with cover art — persisted with `track_count=0` and the `PlaylistFields.TrackCount` bit SET. The culprit:
    /// its PAGE re-asked the same playlist through the revision-gated `/diff` route
    /// (`Spotify.Api.Library.ReadList`), which — when the delta cannot be expressed against the held base —
    /// answers with `changes_require_resync: true` AND a `contents` block anyway; `PlaylistOps.DecodeDiff` (Spotify.Playlist.Ops.cs)
    /// reads that block exactly like a full read's, so `Decode.PlaylistRevision` saw a REAL `length: 0` field and,
    /// before this fix, treated any `sawLength` as trustworthy — overwriting the correct 50 the sidebar's earlier
    /// FULL read had already landed. Reproduces the exact shape: a trustworthy 50 lands first (the sidebar's ask),
    /// then a resync-flagged, zero-length "page re-ask" answer for the SAME playlist must not move it.</summary>
    [Fact]
    public void AResyncFlaggedDiffAnswer_NeverOverwritesAnEstablishedCount_TheEurodanceShape()
    {
        TestScope.Fresh();
        string uri = "spotify:playlist:eurodance";

        var trustworthy = new Wavee.Protocol.Playlist.SelectedListContent
        {
            Length = 50,
            Attributes = new Wavee.Protocol.Playlist.ListAttributes { Name = "Eurodance Mix" },
            Contents = new Wavee.Protocol.Playlist.ListItems { Pos = 0, Truncated = false },
        }.ToByteArray();
        var s1 = Staging.Rent();
        Spotify.Decode.PlaylistRevision(trustworthy, System.Text.Encoding.UTF8.GetBytes(uri), s1);
        TestScope.CommitAndPublish(s1);

        var p = Entities.Playlist(EntityUri.Parse(uri));
        Assert.True(p.Knows(PlaylistFields.TrackCount));
        Assert.Equal(50, p.TrackCount);

        // The page's own re-ask: revision-gated, resync-flagged, `length: 0` — the shape `ReadList` hands to
        // `Decode.PlaylistRevision` unchanged when `PlaylistOps.DecodeDiff` reads a `Contents` answer off a diff response.
        var resyncFlagged = new Wavee.Protocol.Playlist.SelectedListContent
        {
            Length = 0,
            ChangesRequireResync = true,
            Contents = new Wavee.Protocol.Playlist.ListItems { Pos = 0, Truncated = false },
        }.ToByteArray();
        var s2 = Staging.Rent();
        Spotify.Decode.PlaylistRevision(resyncFlagged, System.Text.Encoding.UTF8.GetBytes(uri), s2);
        TestScope.CommitAndPublish(s2);

        Assert.Equal(50, p.TrackCount);            // never zeroed
        Assert.True(p.Knows(PlaylistFields.TrackCount));   // the bit the first answer set is never cleared either
    }

    /// <summary>The same resync-flagged shape reaching a COLD row (no prior trustworthy answer): the count must
    /// stay genuinely unknown, never a confident zero.</summary>
    [Fact]
    public void AResyncFlaggedDiffAnswer_OnAColdRow_LeavesTheCountUnknown()
    {
        TestScope.Fresh();
        string uri = "spotify:playlist:cold-resync";

        var resyncFlagged = new Wavee.Protocol.Playlist.SelectedListContent
        {
            Length = 0,
            ChangesRequireResync = true,
            Contents = new Wavee.Protocol.Playlist.ListItems { Pos = 0, Truncated = false },
        }.ToByteArray();
        var s = Staging.Rent();
        Spotify.Decode.PlaylistRevision(resyncFlagged, System.Text.Encoding.UTF8.GetBytes(uri), s);
        TestScope.CommitAndPublish(s);

        var p = Entities.Playlist(EntityUri.Parse(uri));
        Assert.False(p.Knows(PlaylistFields.TrackCount));
        Assert.Equal(0, p.TrackCount);
    }

    /// <summary>E3/Bug A1: the visible-row ask is BATCHED — every cold row in one walk gets its `Asked` bit flipped
    /// from the SAME `Build` call, off one span-form `Entities.Ensure`, not one `Fetch.Plan` per row.</summary>
    [Fact]
    public void EnsureIdentity_BatchesTheAskAcrossMultipleColdRows_InOneWalk()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p1 = StageColdPlaylist(s, "spotify:playlist:cold1");
        var p2 = StageColdPlaylist(s, "spotify:playlist:cold2");
        var p3 = StageColdPlaylist(s, "spotify:playlist:cold3");
        TestScope.CommitAndPublish(s);
        uint b1 = Entities.Current.Playlists.Asked[p1.Slot];
        uint b2 = Entities.Current.Playlists.Asked[p2.Slot];
        uint b3 = Entities.Current.Playlists.Asked[p3.Slot];

        var me = Me();
        SetRootlist(me, Item(p1, depth: 0), Item(p2, depth: 0), Item(p3, depth: 0));

        Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);

        Assert.NotEqual(b1, Entities.Current.Playlists.Asked[p1.Slot]);
        Assert.NotEqual(b2, Entities.Current.Playlists.Asked[p2.Slot]);
        Assert.NotEqual(b3, Entities.Current.Playlists.Asked[p3.Slot]);
    }

    /// <summary>D2 (issue #4): the OLD count-driven half of this batching claim (deleted bug A1 `ShouldEnsureCount`)
    /// used to ask every un-counted COVERED row's `PlaylistTracks` edge from the same `Build` call — a boot-time
    /// storm across the whole rootlist. Covered rows with covers of their own never ask for anything any more,
    /// batched or not.</summary>
    [Fact]
    public void EnsureIdentity_NeverAsksMultipleUncountedCoveredRows_InOneWalk()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p1 = StagePlaylist(s, "spotify:playlist:u1", "U1", null, 0, PlaylistCaps.None, image: "spotify:image:1");
        var p2 = StagePlaylist(s, "spotify:playlist:u2", "U2", null, 0, PlaylistCaps.None, image: "spotify:image:2");
        TestScope.CommitAndPublish(s);
        Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p1.Slot, 0));
        Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p2.Slot, 0));

        var me = Me();
        SetRootlist(me, Item(p1, depth: 0), Item(p2, depth: 0));

        Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);

        Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p1.Slot, 0));
        Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p2.Slot, 0));
    }

    /// <summary>The membership ask that DOES remain (a cover-less row's own mosaic, `ShouldEnsureMembership`) keeps
    /// the collected-then-asked-once shape across MULTIPLE rows in one walk — never one `EnsureEdge` per row — and
    /// goes out at `FetchPriority.Prefetch`, never `Visible`: a tile is not a page, and must never compete with the
    /// pane's own visible asks or reproduce the boot-time storm the deleted count ask caused.</summary>
    [Fact]
    public void EnsureIdentity_BatchesTheMembershipAskAcrossMultipleCoverlessRows_AtPrefetchPriority()
    {
        TestScope.Fresh();
        Fetch.Reset();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        try
        {
            var s = Staging.Rent();
            var p1 = StagePlaylist(s, "spotify:playlist:u1", "U1", null, 0, PlaylistCaps.None);   // no image
            var p2 = StagePlaylist(s, "spotify:playlist:u2", "U2", null, 0, PlaylistCaps.None);   // no image
            TestScope.CommitAndPublish(s);
            Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p1.Slot, 0));
            Assert.False(Entities.Current.Edges.PlaylistTracks.WasAsked(p2.Slot, 0));

            var me = Me();
            SetRootlist(me, Item(p1, depth: 0), Item(p2, depth: 0));

            Build(me, SidebarEntryKindMask.PlaylistTree, ensureIdentity: true);
            Fetch.Drain();                                      // the host's tick (wave D4): the walk's asks leave here

            Assert.True(Entities.Current.Edges.PlaylistTracks.WasAsked(p1.Slot, 0));
            Assert.True(Entities.Current.Edges.PlaylistTracks.WasAsked(p2.Slot, 0));
            var batch = Assert.Single(provider.Seen);           // ONE Start() call, not one per row
            Assert.Equal(2, batch.Count);
            Assert.Equal(FetchPriority.Prefetch, batch.Priority);
        }
        finally { Fetch.Reset(); }
    }

    /// <summary>Bug A3: a folder whose `spotify:end-group:` marker never arrives (a truncated answer, or the page
    /// ceiling was hit mid-folder) must not show a confident "0 items" — it gets the count of what the walk actually
    /// saw before the stream ran out, drained at the end of the walk exactly as a real FolderEnd would have patched
    /// it.</summary>
    [Fact]
    public void AnUnclosedFolder_GetsItsWalkedChildCount_NeverAConfidentZero()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p1 = StagePlaylist(s, "spotify:playlist:c1", "C1", null, 0, PlaylistCaps.None);
        var p2 = StagePlaylist(s, "spotify:playlist:c2", "C2", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        // No FolderEnd — the wire stream stops mid-folder.
        SetRootlist(me,
            FolderStart(position: 1, depth: 0, name: "Truncated"),
            Item(p1, depth: 1),
            Item(p2, depth: 1));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree);
        var folder = Assert.Single(rows, r => r.Kind == SidebarEntryKind.Folder);
        Assert.Equal(2, folder.ChildCount);
        // The drained count is the honest count of what this answer carried — not a placeholder — so it is KNOWN,
        // the same as a folder closed by a real FolderEnd (see the fact below).
        Assert.True(folder.CountKnown);
    }

    /// <summary>Trap 5 follow-up: every folder WalkRootlist actually closes (a real `spotify:end-group:` marker)
    /// carries a real, known count by the time its row is emitted — never left at the construction-time placeholder
    /// `CountKnown: false`.</summary>
    [Fact]
    public void AClosedFolder_HasItsChildCountKnown()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var p1 = StagePlaylist(s, "spotify:playlist:cf1", "CF1", null, 0, PlaylistCaps.None);
        TestScope.CommitAndPublish(s);

        var me = Me();
        SetRootlist(me,
            FolderStart(position: 1, depth: 0, name: "Closed", hex: "bb"),
            Item(p1, depth: 1),
            FolderEnd(depth: 0));

        var (rows, _) = Build(me, SidebarEntryKindMask.PlaylistTree, flatten: false, expanded: id => id == "bb");
        var folder = Assert.Single(rows, r => r.Kind == SidebarEntryKind.Folder);
        Assert.Equal(1, folder.ChildCount);
        Assert.True(folder.CountKnown);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── ENSURE PREDICATES — the pure half of Bug H's "ask only for visible rows" rule ────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarProjectionEnsurePredicateFacts
{
    [Fact]
    public void ShouldEnsureIdentity_OnlyWhenTheCallerSaysSo_AndIdentityIsStillUnknown()
    {
        Assert.True(SidebarProjection.ShouldEnsureIdentity(ensureIdentity: true, identityKnown: false));
        Assert.False(SidebarProjection.ShouldEnsureIdentity(ensureIdentity: true, identityKnown: true));
        Assert.False(SidebarProjection.ShouldEnsureIdentity(ensureIdentity: false, identityKnown: false));
        Assert.False(SidebarProjection.ShouldEnsureIdentity(ensureIdentity: false, identityKnown: true));
    }

    [Fact]
    public void ShouldEnsureMembership_OnlyForACoverlessRowWithUnknownMembership()
    {
        Assert.True(SidebarProjection.ShouldEnsureMembership(ensureIdentity: true, hasCover: false, EdgeState.Unknown));
        Assert.False(SidebarProjection.ShouldEnsureMembership(ensureIdentity: true, hasCover: true, EdgeState.Unknown));
        Assert.False(SidebarProjection.ShouldEnsureMembership(ensureIdentity: true, hasCover: false, EdgeState.Complete));
        Assert.False(SidebarProjection.ShouldEnsureMembership(ensureIdentity: false, hasCover: false, EdgeState.Unknown));
    }

    /// <summary>The mosaic warm's gate is the complement of <see cref="SidebarProjection.ShouldEnsureMembership"/>
    /// in time: only once membership has landed (Partial or Complete — there are no member slots before), only for a
    /// visible cover-less row, and only while the mosaic is short of four tiles.</summary>
    [Fact]
    public void ShouldWarmMosaicTracks_OnlyForAVisibleCoverlessRow_WhoseMembershipLanded_AndMosaicIsShort()
    {
        Assert.True(SidebarProjection.ShouldWarmMosaicTracks(ensureIdentity: true, hasCover: false, EdgeState.Complete, tileCount: 0));
        Assert.True(SidebarProjection.ShouldWarmMosaicTracks(ensureIdentity: true, hasCover: false, EdgeState.Partial, tileCount: 3));
        Assert.False(SidebarProjection.ShouldWarmMosaicTracks(ensureIdentity: true, hasCover: false, EdgeState.Complete, tileCount: 4));
        Assert.False(SidebarProjection.ShouldWarmMosaicTracks(ensureIdentity: true, hasCover: false, EdgeState.Unknown, tileCount: 0));
        Assert.False(SidebarProjection.ShouldWarmMosaicTracks(ensureIdentity: true, hasCover: true, EdgeState.Complete, tileCount: 0));
        Assert.False(SidebarProjection.ShouldWarmMosaicTracks(ensureIdentity: false, hasCover: false, EdgeState.Complete, tileCount: 0));
    }

    /// <summary>Trap 5: rootlist Unknown (never answered this session — a cold launch, every time, since the
    /// rootlist is network-only) means PENDING for an unresolved folder pin, never the confident Missing verdict.
    /// Complete+absent is the only combination that earns Missing; found always wins regardless of rootlist state.</summary>
    [Fact]
    public void ResolveFolderPinState_PendingWhileUnknown_MissingOnlyOnceAnsweredAndAbsent()
    {
        Assert.Equal(SidebarPinFolderState.Pending,
            SidebarProjection.ResolveFolderPinState(EdgeState.Unknown, foundInProjection: false));
        Assert.Equal(SidebarPinFolderState.Missing,
            SidebarProjection.ResolveFolderPinState(EdgeState.Complete, foundInProjection: false));
        Assert.Equal(SidebarPinFolderState.Normal,
            SidebarProjection.ResolveFolderPinState(EdgeState.Complete, foundInProjection: true));
        // Found always wins, even while the rootlist relation itself is mid-page (Partial) or (in principle) Unknown —
        // "we already have it" is never overridden by "we don't otherwise know yet".
        Assert.Equal(SidebarPinFolderState.Normal,
            SidebarProjection.ResolveFolderPinState(EdgeState.Unknown, foundInProjection: true));
        Assert.Equal(SidebarPinFolderState.Missing,
            SidebarProjection.ResolveFolderPinState(EdgeState.Partial, foundInProjection: false));
    }

    /// <summary>Trap 5, the pin-band TITLE half: a PIN (any resolvable kind — album/artist/show, not just folder)
    /// whose Identity has not landed must not show the raw id/uri fallback as its title. Scoped to pins alone —
    /// a non-pinned row's fallback is untouched by this predicate regardless of `identityKnown`.</summary>
    [Fact]
    public void ShouldShowUriFallbackTitle_NeverForAPendingPin_AlwaysForANonPinnedRow()
    {
        Assert.False(SidebarProjection.ShouldShowUriFallbackTitle(isPinned: true, identityKnown: false));   // pending
        Assert.True(SidebarProjection.ShouldShowUriFallbackTitle(isPinned: true, identityKnown: true));     // known → normal
        Assert.True(SidebarProjection.ShouldShowUriFallbackTitle(isPinned: false, identityKnown: false));   // not a pin at all
        Assert.True(SidebarProjection.ShouldShowUriFallbackTitle(isPinned: false, identityKnown: true));
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── SUBTITLE — Bug A1: SidebarPaneText.SubtitleOf never paints "0 songs" for an unknown COUNT (PURE) ─────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// These facts construct the user's ACTUAL reported state: a playlist whose Identity landed off the cheap
// ListMetadataV2 route (title, cover — `IdentityKnown: true`) but whose TrackCount was never real, because that
// route carries no length at all (Spotify.Decode.cs's ListMetadataV2, ext kind 205). The deleted
// `KnownIdentity_WithZeroTracks_StillSaysZeroSongs` enshrined exactly this bug: its premise, "identity known ⇒
// count known", is false — see the handoff's trap 3.
public class SidebarPaneTextSubtitleFacts
{
    static SidebarLibraryEntry Playlist(int trackCount, bool identityKnown, bool countKnown) =>
        new("pl:spotify:playlist:x", SidebarEntryKind.Playlist, "spotify:playlist:x", "X", "Owner",
            default, null, ChildCount: trackCount, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { IdentityKnown = identityKnown, CountKnown = countKnown };

    [Fact]
    public void UnknownIdentityAndCount_YieldsNoSubtitle_NeverZeroSongs()
    {
        var e = Playlist(trackCount: 0, identityKnown: false, countKnown: false);
        Assert.Null(Sidebar.PaneText.SubtitleOf(in e));
    }

    /// <summary>THE USER'S ACTUAL STATE (bug A1): a row that HAS a cover and DOES show a title — Identity landed
    /// off ListMetadataV2 — but whose count never did, because that route carries no length. Before the fix, the
    /// old `IdentityKnown`-gated `SubtitleOf` read this exact state as "known, zero" and painted a confident
    /// "0 songs" on a 43-track playlist. The subtitle must be OMITTED, never a zero.</summary>
    [Fact]
    public void IdentityKnown_ButCountNotKnown_YieldsNoSubtitle_NeverZeroSongs()
    {
        var e = Playlist(trackCount: 0, identityKnown: true, countKnown: false);
        Assert.Null(Sidebar.PaneText.SubtitleOf(in e));
    }

    /// <summary>The genuinely-empty case: the COUNT has landed (off a real PlaylistRead answer, bug A1's
    /// `PlaylistFields.TrackCount` bit), and the playlist really does have zero tracks. This is a real state and
    /// must still say "0 songs" — the whole reason `CountKnown` exists instead of inferring unknown-ness from
    /// `TrackCount == 0`.</summary>
    [Fact]
    public void CountKnown_WithZeroTracks_StillSaysZeroSongs()
    {
        var e = Playlist(trackCount: 0, identityKnown: true, countKnown: true);
        Assert.Equal(Strings.Sidebar.SongCount(0), Sidebar.PaneText.SubtitleOf(in e));
    }

    [Fact]
    public void CountKnown_WithARealCount_SaysTheRealCount()
    {
        var e = Playlist(trackCount: 42, identityKnown: true, countKnown: true);
        Assert.Equal(Strings.Sidebar.SongCount(42), Sidebar.PaneText.SubtitleOf(in e));
    }

    /// <summary>A row whose count is unknown but that HAPPENS to carry a nonzero TrackCount (e.g. a stale value
    /// left over from a previous session) still shows nothing — the gate is the bit, never the number.</summary>
    [Fact]
    public void UnknownCount_IsNeverInferredFromANonZeroTrackCount()
    {
        var e = Playlist(trackCount: 50, identityKnown: true, countKnown: false);
        Assert.Null(Sidebar.PaneText.SubtitleOf(in e));
    }

    // ── FOLDER — Trap 5: SidebarPane's FolderRow (Sidebar.UI.Slot.cs) now renders its subtitle through THIS SAME
    //    pure decision (`Subtitle = section.Opts.Subtitles ? PaneText.SubtitleOf(in entry) : null`), so driving the
    //    folder branch here — not by reading Sidebar.UI.Slot.cs's source — pins the real, live rendering decision.

    static SidebarLibraryEntry Folder(int childCount, bool countKnown) =>
        new("folder:abc", SidebarEntryKind.Folder, "", "My Folder", "",
            default, null, ChildCount: childCount, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { CountKnown = countKnown };

    /// <summary>The pending case (bug A follow-up, trap 5): a folder whose count is not known — a Pending unlisted
    /// pin, before the rootlist has answered this session — shows no subtitle, never a confident "0 items".</summary>
    [Fact]
    public void UnknownFolderCount_YieldsNoSubtitle_NeverZeroItems()
    {
        var e = Folder(childCount: 0, countKnown: false);
        Assert.Null(Sidebar.PaneText.SubtitleOf(in e));
    }

    [Fact]
    public void KnownFolderCount_WithZeroItems_StillSaysZeroItems()
    {
        var e = Folder(childCount: 0, countKnown: true);
        Assert.Equal(Strings.Sidebar.V3.ItemCount(0), Sidebar.PaneText.SubtitleOf(in e));
    }

    [Fact]
    public void KnownFolderCount_WithARealCount_SaysTheRealCount()
    {
        var e = Folder(childCount: 4, countKnown: true);
        Assert.Equal(Strings.Sidebar.V3.ItemCount(4), Sidebar.PaneText.SubtitleOf(in e));
    }

    /// <summary>End-to-end, through the real production seam: a Pending unlisted folder pin
    /// (`SidebarBinderPipeline.ResolveUnlistedPin`, rootlist state Unknown) produces an entry whose subtitle —
    /// via the SAME `PaneText.SubtitleOf` call `FolderRow` makes — is omitted, not "0 items".</summary>
    [Fact]
    public void APendingUnlistedFolderPin_RendersNoSubtitle()
    {
        var pin = new SidebarPin("folder:36405e1711f88d9c", SidebarEntryKind.Folder, "", "F", AddedAtMs: 1000);
        var entry = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated: null, EdgeState.Unknown);

        Assert.False(entry.Missing);
        Assert.Null(Sidebar.PaneText.SubtitleOf(in entry));
    }

    /// <summary>The Missing case still carries no subtitle either (its row never reaches this decision in
    /// production — `FolderRow` returns `MissingFolderRow` first — but the data itself must not lie if it ever did).</summary>
    [Fact]
    public void AMissingUnlistedFolderPin_AlsoCarriesNoKnownCount()
    {
        var pin = new SidebarPin("folder:36405e1711f88d9c", SidebarEntryKind.Folder, "", "F", AddedAtMs: 1000);
        var entry = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated: null, EdgeState.Complete);

        Assert.True(entry.Missing);
        Assert.Null(Sidebar.PaneText.SubtitleOf(in entry));
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── SHADOW — a rebuild that changed nothing must not bump the published version (PURE) ──────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarEntriesShadowFacts
{
    static SidebarEntriesMeta Meta(int state = 0, Exception? error = null, bool pending = false,
                                   bool qualifiers = false, int pinCount = 0)
        => new(state, error, pending, qualifiers, pinCount);

    static SidebarLibraryEntry Playlist(string id, string name = "n", int childCount = 0,
                                        IReadOnlyList<StringId>? mosaic = null)
        => new(id, SidebarEntryKind.Playlist, "spotify:playlist:" + id, name, "", default, mosaic, childCount,
               0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None);

    [Fact]
    public void FirstPublish_AlwaysCounts_AsAChange()
    {
        var shadow = new SidebarEntriesShadow();
        Assert.True(shadow.Publish(Array.Empty<SidebarLibraryEntry>(), Meta()));
        Assert.False(shadow.Publish(Array.Empty<SidebarLibraryEntry>(), Meta()));   // an empty library must not storm
    }

    [Fact]
    public void AnIdenticalRepublish_IsNotAChange()
    {
        var shadow = new SidebarEntriesShadow();
        var rows = new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b"), Playlist("c") };
        Assert.True(shadow.Publish(rows, Meta(pinCount: 1)));

        var again = new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b"), Playlist("c") };
        Assert.False(shadow.Publish(again, Meta(pinCount: 1)));
        Assert.False(shadow.Publish(again, Meta(pinCount: 1)));
    }

    [Fact]
    public void AnyRowDelta_IsAChange()
    {
        var shadow = new SidebarEntriesShadow();
        var rows = new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") };
        Assert.True(shadow.Publish(rows, Meta()));

        Assert.True(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a") }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("b"), Playlist("a") }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("b"), Playlist("a", name: "renamed"),
        }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("b"), Playlist("a", name: "renamed", childCount: 12),
        }, Meta()));
        Assert.False(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("b"), Playlist("a", name: "renamed", childCount: 12),
        }, Meta()));
    }

    [Fact]
    public void AnyMetaDelta_IsAChange_EvenWithIdenticalRows()
    {
        var shadow = new SidebarEntriesShadow();
        var rows = new List<SidebarLibraryEntry> { Playlist("a") };
        Assert.True(shadow.Publish(rows, Meta()));

        Assert.True(shadow.Publish(rows, Meta(state: 1)));
        Assert.True(shadow.Publish(rows, Meta(state: 1, pending: true)));
        Assert.True(shadow.Publish(rows, Meta(state: 1, pending: true, qualifiers: true)));
        Assert.True(shadow.Publish(rows, Meta(state: 1, pending: true, qualifiers: true, pinCount: 3)));
        Assert.False(shadow.Publish(rows, Meta(state: 1, pending: true, qualifiers: true, pinCount: 3)));

        var boom = new InvalidOperationException("boom");
        Assert.True(shadow.Publish(rows, Meta(state: 2, error: boom, pending: true, qualifiers: true, pinCount: 3)));
        Assert.False(shadow.Publish(rows, Meta(state: 2, error: boom, pending: true, qualifiers: true, pinCount: 3)));
        Assert.True(shadow.Publish(rows, Meta(state: 2, error: new InvalidOperationException("boom"),
                                              pending: true, qualifiers: true, pinCount: 3)));
    }

    [Fact]
    public void MosaicTiles_CompareByValue_SoAFolderyLibraryStillSettles()
    {
        var shadow = new SidebarEntriesShadow();
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: new List<StringId> { new(1), new(2) }),
        }, Meta()));
        Assert.False(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: new List<StringId> { new(1), new(2) }),   // equal by value, different instance
        }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: new List<StringId> { new(1), new(3) }),   // a real cover change
        }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: null),                                    // and losing the mosaic entirely
        }, Meta()));
    }

    [Fact]
    public void ThePublishedShadow_MirrorsTheLastAcceptedRebuild()
    {
        var shadow = new SidebarEntriesShadow();
        shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta());
        Assert.Equal(2, shadow.Published.Count);
        Assert.Equal("a", shadow.Published[0].Id);

        shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta());
        Assert.Equal(2, shadow.Published.Count);
        Assert.False(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta()));
    }

    static SidebarLibraryEntry Folder(string id, string name = "n")
        => new(id, SidebarEntryKind.Folder, "", name, "", default, null, 0, 0, 0, 0, 0, 0, false,
              SidebarPlaylistFlavor.None);

    /// <summary>The binder keeps TWO shadows: one over the PUBLISHED (folder-collapse-filtered) rows and one over the
    /// FULL flattened projection the planner actually reads. A hydration that only touches a child living inside a
    /// collapsed folder must trip the FULL shadow even though the published one — folder-collapsed, so the child
    /// never appears in it at all — sees no difference. Mechanical over two independent SidebarEntriesShadow
    /// instances; no binder required.</summary>
    [Fact]
    public void HiddenFolderChildHydration_ChangesTheFullProjection_NotThePublishedRows()
    {
        var folder = Folder("f1");
        var childBefore = Playlist("c1", name: "spotify:playlist:c1", childCount: 0);
        var childAfter = Playlist("c1", name: "Road Trip", childCount: 42);

        var published = new List<SidebarLibraryEntry> { folder };            // collapsed → child omitted
        var full = new List<SidebarLibraryEntry> { folder, childBefore };

        var publishedShadow = new SidebarEntriesShadow();
        var fullShadow = new SidebarEntriesShadow();
        Assert.True(publishedShadow.Publish(published, Meta()));
        Assert.True(fullShadow.Publish(full, default));

        var publishedAgain = new List<SidebarLibraryEntry> { folder };
        Assert.False(publishedShadow.Publish(publishedAgain, Meta()));

        var fullAgain = new List<SidebarLibraryEntry> { folder, childAfter };
        Assert.True(fullShadow.Publish(fullAgain, default));
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── BINDER TRIGGERS — the rebuild gate's one comparable fold (PURE) ──────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarBinderTriggersFacts
{
    [Fact]
    public void IdenticalTriggers_CompareEqual_SoARedundantSyncDoesNoWork()
    {
        var a = new SidebarBinderTriggers(LibraryEpoch: 11, PinsVersion: 2, PlayLogRevision: 3);
        var b = new SidebarBinderTriggers(LibraryEpoch: 11, PinsVersion: 2, PlayLogRevision: 3);
        Assert.Equal(a, b);
        Assert.Equal(a.Fold(), b.Fold());
    }

    [Fact]
    public void APinMutation_TriggersARebuild()
    {
        var before = new SidebarBinderTriggers(PinsVersion: 4);
        var after = before with { PinsVersion = 5 };
        Assert.NotEqual(before, after);
        Assert.NotEqual(before.Fold(), after.Fold());
    }

    [Fact]
    public void APlayLogAppend_TriggersARebuild()
    {
        var before = new SidebarBinderTriggers(PlayLogRevision: 17);
        var after = before with { PlayLogRevision = 18 };
        Assert.NotEqual(before, after);
        Assert.NotEqual(before.Fold(), after.Fold());
    }

    [Fact]
    public void AFilterSortOrDesignChange_TriggersARebuild()
    {
        int all = SidebarBinderTriggers.PackV3((int)SidebarDesign.LibraryV3, (int)SidebarV3Filter.All,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Recents, descending: true);
        int playlists = SidebarBinderTriggers.PackV3((int)SidebarDesign.LibraryV3, (int)SidebarV3Filter.Playlists,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Recents, descending: true);
        int alphabetical = SidebarBinderTriggers.PackV3((int)SidebarDesign.LibraryV3, (int)SidebarV3Filter.All,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Alphabetical, descending: true);
        int ascending = SidebarBinderTriggers.PackV3((int)SidebarDesign.LibraryV3, (int)SidebarV3Filter.All,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Recents, descending: false);
        int curated = SidebarBinderTriggers.PackV3((int)SidebarDesign.Curated, (int)SidebarV3Filter.All,
            (int)SidebarV3Qualifier.Any, (int)SidebarV3Sort.Recents, descending: true);

        Assert.Equal(5, new HashSet<int> { all, playlists, alphabetical, ascending, curated }.Count);
    }

    [Fact]
    public void ASearchKeystroke_TriggersARebuild()
    {
        var before = new SidebarBinderTriggers(SearchHash: "caf".GetHashCode(StringComparison.Ordinal));
        var after = new SidebarBinderTriggers(SearchHash: "café".GetHashCode(StringComparison.Ordinal));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ASourceNotification_TriggersARebuild()
    {
        var before = new SidebarBinderTriggers(SourceEpoch: 1);
        Assert.NotEqual(before, before with { SourceEpoch = 2 });
    }

    [Fact]
    public void AQueueOrNowPlayingChange_TriggersARebuild()
    {
        var before = new SidebarBinderTriggers(PlaybackEpoch: 4L << 20);
        Assert.NotEqual(before, before with { PlaybackEpoch = 5L << 20 });
        Assert.NotEqual(before.Fold(), (before with { PlaybackEpoch = (4L << 20) ^ 99 }).Fold());
    }

    /// <summary>G-180: the row-version lane and the feed-table lane are real lanes — both halves of their 64 bits reach
    /// the fold, so a hydration of a sidebar row (or of a demanded feed's table) is a rebuild.</summary>
    [Fact]
    public void ALibraryRowOrFeedTableChange_TriggersARebuild()
    {
        var before = new SidebarBinderTriggers(LibraryRows: 0x1234_5678_0000_0001L, FeedTables: 7L);
        Assert.NotEqual(before.Fold(), (before with { LibraryRows = 0x1234_5678_0000_0002L }).Fold());
        Assert.NotEqual(before.Fold(), (before with { LibraryRows = 0x1234_5679_0000_0001L }).Fold());
        Assert.NotEqual(before.Fold(), (before with { FeedTables = 8L }).Fold());
        Assert.NotEqual(before.Fold(), (before with { FeedTables = 7L | (1L << 40) }).Fold());
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── BINDER SHAPE — SidebarBinderPipeline.Project/Shape: filter → qualifier → search → sort → pins-first (PURE) ─────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarBinderPipelineShapeFacts
{
    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string uri, string name,
        string creator = "", long sortStamp = 1, int order = 0, int depth = 0,
        SidebarPlaylistFlavor flavor = SidebarPlaylistFlavor.None)
        => new(id, kind, uri, name, creator, default, null, ChildCount: 0, AddedAtMs: 0, SortStamp: sortStamp,
               LastVisitedTicksUtc: 0, SourceOrder: order, Depth: depth, Circular: false, Flavor: flavor);

    static SidebarLibraryEntry Playlist(string slug, string name, int order = 0,
        SidebarPlaylistFlavor flavor = SidebarPlaylistFlavor.None)
        => Entry("pl:spotify:playlist:" + slug, SidebarEntryKind.Playlist,
                 "spotify:playlist:" + slug, name, "Owner", 100 + order, order, flavor: flavor);

    static SidebarLibraryEntry Album(string slug, string name, int order = 0)
        => Entry("album:spotify:album:" + slug, SidebarEntryKind.Album,
                 "spotify:album:" + slug, name, "Artist", 200 + order, order);

    static SidebarLibraryEntry Folder(string id, string name)
        => Entry("folder:" + id, SidebarEntryKind.Folder, "", name);

    static SidebarPin Pin(string id) => new(id, SidebarEntryKind.Playlist, "", "cached", 0);

    static readonly IReadOnlyList<SidebarLibraryEntry> Library =
    [
        Playlist("1", "Alpha", 0, SidebarPlaylistFlavor.ByYou),
        Playlist("2", "Beta", 1, SidebarPlaylistFlavor.BySpotify),
        Album("9", "Ceremony", 2),
        Folder("f1", "Chill"),
    ];

    static (List<SidebarLibraryEntry> Rows, SidebarEntriesShape Shape) Project(
        SidebarV3Filter filter = SidebarV3Filter.All,
        SidebarV3Qualifier qualifier = SidebarV3Qualifier.Any,
        SidebarV3Sort sort = SidebarV3Sort.Recents,
        bool desc = true,
        string? search = null,
        bool qualifiersAvailable = false,
        IReadOnlyList<SidebarPin>? pins = null,
        IReadOnlyList<string>? customOrder = null,
        IReadOnlyList<SidebarLibraryEntry>? library = null)
    {
        var into = new List<SidebarLibraryEntry>();
        var scratch = new List<SidebarLibraryEntry>();
        var query = new SidebarV3Query(filter, qualifier, sort, desc, search, qualifiersAvailable);
        var shape = SidebarBinderPipeline.Project(library ?? Library, into, scratch, in query, pins, customOrder);
        return (into, shape);
    }

    [Fact]
    public void TheFilter_SelectsTheContributingKinds()
    {
        Assert.Equal(4, Project().Shape.Count);
        Assert.Equal(1, Project(SidebarV3Filter.Albums).Shape.Count);
        Assert.Equal(0, Project(SidebarV3Filter.Artists).Shape.Count);
        Assert.Equal(3, Project(SidebarV3Filter.Playlists).Shape.Count);   // playlists INCLUDE their folders
    }

    [Fact]
    public void Search_MatchesNameAndFlattensFoldersAway()
    {
        var (rows, shape) = Project(search: "alpha");
        Assert.Equal(1, shape.Count);
        Assert.Equal("Alpha", rows[0].Name);
        Assert.Equal(0, Project(search: "chill").Shape.Count);   // a folder is a container, not a result
    }

    [Fact]
    public void Search_IsCaseAndDiacriticsInsensitive_AndTrims()
    {
        Assert.Equal(1, Project(search: "  BETA ").Shape.Count);
    }

    [Fact]
    public void AStaleQualifier_CannotHideTheList_WhenTheChipsAreUnavailable()
    {
        Assert.Equal(3, Project(SidebarV3Filter.Playlists, SidebarV3Qualifier.BySpotify).Shape.Count);
        Assert.Equal(1, Project(SidebarV3Filter.Playlists, SidebarV3Qualifier.BySpotify,
                                qualifiersAvailable: true).Shape.Count);
    }

    [Fact]
    public void Pins_LeadInPinOrder_AndPinCountIsTheBandLength()
    {
        var pins = new[] { Pin("album:spotify:album:9"), Pin("pl:spotify:playlist:2") };
        var (rows, shape) = Project(sort: SidebarV3Sort.Alphabetical, desc: false, pins: pins);

        Assert.Equal(2, shape.PinCount);
        Assert.Equal("album:spotify:album:9", rows[0].Id);
        Assert.Equal("pl:spotify:playlist:2", rows[1].Id);
        Assert.True(rows[0].IsPinned);
        Assert.True(rows[1].IsPinned);
        Assert.False(rows[2].IsPinned);
        Assert.Equal(4, shape.Count);
    }

    [Fact]
    public void APinTheFilterExcludes_DoesNotAppear()
    {
        var pins = new[] { Pin("album:spotify:album:9") };
        var (rows, shape) = Project(SidebarV3Filter.Playlists, pins: pins);
        Assert.Equal(0, shape.PinCount);
        for (int i = 0; i < rows.Count; i++) Assert.NotEqual("album:spotify:album:9", rows[i].Id);
    }

    [Fact]
    public void CustomSort_OutsideThePlaylistsFilter_FallsBackToAlphabetical()
    {
        var order = new[] { "album:spotify:album:9" };
        var (rows, _) = Project(SidebarV3Filter.All, sort: SidebarV3Sort.Custom, customOrder: order);
        Assert.NotEqual("album:spotify:album:9", rows[0].Id);
    }

    [Fact]
    public void CustomSort_UnderThePlaylistsFilter_HonoursTheLocalOrder()
    {
        var order = new[] { "pl:spotify:playlist:2" };
        var (rows, _) = Project(SidebarV3Filter.Playlists, sort: SidebarV3Sort.Custom, customOrder: order);
        Assert.Equal("pl:spotify:playlist:2", rows[0].Id);
    }

    [Fact]
    public void Shape_OperatesInPlace_SoTheBinderNeedsNoCopy()
    {
        var list = new List<SidebarLibraryEntry> { Playlist("1", "Alpha"), Playlist("2", "Beta") };
        var scratch = new List<SidebarLibraryEntry>();
        var query = new SidebarV3Query(SidebarV3Filter.Playlists, Search: "beta");
        var shape = SidebarBinderPipeline.Shape(list, scratch, in query);
        Assert.Equal(1, shape.Count);
        Assert.Single(list);
        Assert.Equal("Beta", list[0].Name);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── UNLISTED PIN — an editorial/Spotify-owned entity the user never saved (PURE) ─────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[Collection(EntitiesCollection.Name)]
public class SidebarBinderPipelineUnlistedPinFacts
{
    [Fact]
    public void NoHydrationYet_StillRendersTheOfflineDisplayCache()
    {
        var pin = new SidebarPin("pl:spotify:playlist:korea", SidebarEntryKind.Playlist, "spotify:playlist:korea",
            "Top Songs - South Korea", AddedAtMs: 1000);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated: null);

        Assert.True(row.IsPinned);
        Assert.Equal(pin.Id, row.Id);
        Assert.Equal(SidebarEntryKind.Playlist, row.Kind);
        Assert.Equal("Top Songs - South Korea", row.Name);
        Assert.True(row.Cover.IsEmpty);
        Assert.Equal(0, row.TrackCount);
    }

    /// <summary>THE USER'S ACTUAL STATE (trap 5 follow-up): a pin the ylpin bridge minted with an empty cached
    /// Name (the doc comment on `ResolveUnlistedPin` already names this: "the ylpin bridge mints every non-route
    /// pin with Name = ''"), for a kind that has no hydration yet. The row must carry `IdentityKnown: false` — the
    /// signal `ShouldShowUriFallbackTitle` reads to keep the renderer from falling back to a raw id/uri fragment —
    /// never true just because the row exists.</summary>
    [Fact]
    public void AnUnnamedArtistPin_WithNoHydrationYet_IsNotStampedIdentityKnown()
    {
        var pin = new SidebarPin("artist:spotify:artist:3fMbdgg4jU18AjLCKBhRSm", SidebarEntryKind.Artist,
            "spotify:artist:3fMbdgg4jU18AjLCKBhRSm", "", AddedAtMs: 1000);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated: null);

        Assert.True(row.IsPinned);
        Assert.Equal("", row.Name);
        Assert.False(row.IdentityKnown);
        Assert.False(SidebarProjection.ShouldShowUriFallbackTitle(row.IsPinned, row.IdentityKnown));
    }

    /// <summary>The positive control: once `ResolveLivePin` hydrates the SAME pin (Identity landed), the merged row
    /// carries `IdentityKnown: true` and the renderer's uri fallback gate opens (though `Name` is real by then, so
    /// the fallback is moot in practice — this pins the SIGNAL, not just the visible name).</summary>
    [Fact]
    public void AnUnnamedArtistPin_OnceHydrated_IsStampedIdentityKnown()
    {
        var pin = new SidebarPin("artist:spotify:artist:3fMbdgg4jU18AjLCKBhRSm", SidebarEntryKind.Artist,
            "spotify:artist:3fMbdgg4jU18AjLCKBhRSm", "", AddedAtMs: 1000);
        var hydrated = new SidebarLibraryEntry("", SidebarEntryKind.Artist, "", "Real Name", "", default, null,
            ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: true, Flavor: SidebarPlaylistFlavor.None);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated);

        Assert.True(row.IdentityKnown);
        Assert.True(SidebarProjection.ShouldShowUriFallbackTitle(row.IsPinned, row.IdentityKnown));
    }

    [Fact]
    public void OnceHydrated_ProjectsRealArtAndARealTrackCount()
    {
        var pin = new SidebarPin("pl:spotify:playlist:korea", SidebarEntryKind.Playlist, "spotify:playlist:korea",
            "Top Songs - South Korea", AddedAtMs: 1000);
        var cover = Entities.Strings.Intern("spotify:image:korea-cover");
        var hydrated = new SidebarLibraryEntry("", SidebarEntryKind.Playlist, "", "", "Spotify", cover, null,
            ChildCount: 50, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated);

        Assert.True(row.IsPinned);
        Assert.Equal(pin.Id, row.Id);
        Assert.Equal("Top Songs - South Korea", row.Name);          // the pin's own cache still names the row
        Assert.False(row.Cover.IsEmpty);
        Assert.Equal("spotify:image:korea-cover", Entities.Strings.Resolve(row.Cover));
        Assert.Equal(50, row.TrackCount);
        Assert.Equal("Spotify", row.Creator);
    }

    [Fact]
    public void TheHydrationOverlay_NeverBlanksAFieldItDidNotResolve()
    {
        var pin = new SidebarPin("show:spotify:show:1", SidebarEntryKind.Show, "spotify:show:1", "cached", AddedAtMs: 0);
        var hydrated = new SidebarLibraryEntry("", SidebarEntryKind.Show, "", "", "Acme Media", default, null,
            ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated);

        Assert.True(row.Cover.IsEmpty);              // the overlay had none — the (already-empty) base value survives
        Assert.Equal("Acme Media", row.Creator);      // …but the field the overlay DID carry wins
    }

    [Fact]
    public void AFolderPin_IsMissing_AndCarriesItsGroupId()
    {
        var pin = new SidebarPin("folder:36405e1711f88d9c", SidebarEntryKind.Folder, "", "F", AddedAtMs: 1000);
        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated: null);

        Assert.True(row.IsPinned);
        Assert.True(row.Missing);
        Assert.Equal("36405e1711f88d9c", row.FolderId);
    }

    /// <summary>Trap 5 follow-up: the rootlist is network-only, never persisted, so EVERY cold launch starts
    /// <see cref="EdgeState.Unknown"/> — a pinned folder not yet found in the (not-yet-run) walk must render as
    /// PENDING, never the confident "Not in your library on this device yet" text. The default (no
    /// <c>rootlistState</c> passed) is what the OLD tests exercise and must stay Missing — this fact is the one
    /// that pins the NEW, explicit-Unknown behaviour.</summary>
    [Fact]
    public void AFolderPin_WhileTheRootlistHasNeverAnswered_IsPendingNotMissing()
    {
        var pin = new SidebarPin("folder:36405e1711f88d9c", SidebarEntryKind.Folder, "", "F", AddedAtMs: 1000);
        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated: null, EdgeState.Unknown);

        Assert.True(row.IsPinned);
        Assert.False(row.Missing);      // pending, not a confident negative
        Assert.False(row.CountKnown);   // and no "0 items" either — the count is genuinely unknown too
        Assert.Equal("36405e1711f88d9c", row.FolderId);
    }

    /// <summary>The positive control: once the rootlist HAS answered (Complete, or any non-Unknown state — a
    /// Partial answer already says enough to trust an absence) and still does not carry the folder, Missing is the
    /// honest, correct verdict.</summary>
    [Fact]
    public void AFolderPin_OnceTheRootlistHasAnswered_IsMissing()
    {
        var pin = new SidebarPin("folder:36405e1711f88d9c", SidebarEntryKind.Folder, "", "F", AddedAtMs: 1000);
        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated: null, EdgeState.Complete);

        Assert.True(row.Missing);
        Assert.False(row.CountKnown);
    }

    [Fact]
    public void APlaylistPin_IsNotMissing()
    {
        var pin = new SidebarPin("pl:spotify:playlist:korea", SidebarEntryKind.Playlist, "spotify:playlist:korea",
            "Top Songs - South Korea", AddedAtMs: 1000);
        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated: null);
        Assert.False(row.Missing);
    }

    [Fact]
    public void OnceHydrated_TheEntityNamesAPinTheServerMintedNameless()
    {
        // The ylpin bridge mints every non-route pin with Name = "" — the entity is what names the row.
        var pin = new SidebarPin("pl:spotify:playlist:korea", SidebarEntryKind.Playlist, "spotify:playlist:korea",
            "", AddedAtMs: 1000);
        var hydrated = new SidebarLibraryEntry("", SidebarEntryKind.Playlist, "", "Top Songs - South Korea", "Spotify",
            default, null, ChildCount: 50, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated);

        Assert.Equal("Top Songs - South Korea", row.Name);
    }

    [Fact]
    public void TheHydrationOverlay_NeverBlanksACachedName()
    {
        var pin = new SidebarPin("pl:spotify:playlist:korea", SidebarEntryKind.Playlist, "spotify:playlist:korea",
            "cached", AddedAtMs: 1000);
        var hydrated = new SidebarLibraryEntry("", SidebarEntryKind.Playlist, "", "", "Spotify",
            default, null, ChildCount: 50, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated);

        Assert.Equal("cached", row.Name);
    }

    [Fact]
    public void OnceHydrated_TheFirstArtistNameOverlays()
    {
        var pin = new SidebarPin("album:spotify:album:1", SidebarEntryKind.Album, "spotify:album:1", "Cupid",
            AddedAtMs: 1000);
        var hydrated = new SidebarLibraryEntry("", SidebarEntryKind.Album, "", "Cupid", "", default, null,
            ChildCount: 10, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FirstArtistName = "roti." };

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated);

        Assert.Equal("roti.", row.FirstArtistName);
    }

    /// <summary>G-059: an unlisted pin's offline display cache never carries a mosaic (only its cover url, if any),
    /// so a cover-less pinned playlist stayed a blank tile until the overlay actually forwarded ResolveLivePin's
    /// fresh read — the field this fact would have caught missing from the `with` in ResolveUnlistedPin.</summary>
    [Fact]
    public void OnceHydrated_TheMosaicOverlays()
    {
        var pin = new SidebarPin("pl:spotify:playlist:korea", SidebarEntryKind.Playlist, "spotify:playlist:korea",
            "Top Songs - South Korea", AddedAtMs: 1000);
        var tiles = new List<StringId> { Entities.Strings.Intern("spotify:image:a"), Entities.Strings.Intern("spotify:image:b") };
        var hydrated = new SidebarLibraryEntry("", SidebarEntryKind.Playlist, "", "", "Spotify", default, tiles,
            ChildCount: 50, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydrated);

        Assert.Same(tiles, row.MosaicTiles);
    }

    /// <summary>Bug A1: `CountKnown` is NOT hardcoded true the way `IdentityKnown` correctly is — `ResolveLivePin`
    /// stamps the real <see cref="PlaylistFields.TrackCount"/> bit, and this overlay must forward it rather than
    /// assume "hydrated ⇒ counted" the way the old code assumed "hydrated ⇒ identity known ⇒ counted".</summary>
    [Fact]
    public void OnceHydrated_ForwardsTheRealCountKnownBit_NeverHardcodedTrue()
    {
        var pin = new SidebarPin("pl:spotify:playlist:korea", SidebarEntryKind.Playlist, "spotify:playlist:korea",
            "Top Songs - South Korea", AddedAtMs: 1000);
        var hydratedButUncounted = new SidebarLibraryEntry("", SidebarEntryKind.Playlist, "", "", "Spotify",
            default, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { IdentityKnown = true, CountKnown = false };

        var row = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, hydratedButUncounted);

        Assert.True(row.IdentityKnown);
        Assert.False(row.CountKnown);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── CONTRIBUTION RESOLUTION — Extension sections resolved against a data-source table (PURE) ────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class SidebarBinderPipelineContributionFacts
{
    static SidebarLibraryEntry Playlist(string slug, string name) =>
        new("pl:spotify:playlist:" + slug, SidebarEntryKind.Playlist, "spotify:playlist:" + slug, name, "Owner",
            default, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 1, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

    static SidebarLibraryEntry Album(string slug, string name) =>
        new("album:spotify:album:" + slug, SidebarEntryKind.Album, "spotify:album:" + slug, name, "Artist",
            default, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 1, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

    static SidebarSectionSpec ExtSection(string id, string contribution, int schemaVersion = 1, int maxItems = 0)
        => new(id, SidebarSectionKind.Extension, null, null)
        {
            Extension = new SidebarExtensionRef(SidebarContributions.WaveeExtensionId, contribution, schemaVersion, default),
            Display = maxItems > 0 ? SidebarDisplayOptions.Default with { MaxItems = maxItems } : null,
        };

    static SidebarCustomLayout Doc(params SidebarSectionSpec[] sections) => new(SidebarTemplates.Curated, sections);

    /// <summary>A configurable contributed source — the shape a sandboxed extension arrives in.</summary>
    sealed class StubSource : SidebarDataSourceBase
    {
        readonly List<SidebarLibraryEntry> _rows = new();
        public bool PartialThenThrow;
        public int SchemaVersion = 1;

        public StubSource(string id) : base(id) { }

        public override SidebarConfigSchema ConfigSchema => new(SchemaVersion, Array.Empty<SidebarConfigField>());

        public StubSource With(params SidebarLibraryEntry[] rows)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            return this;
        }

        public void Publish(SidebarSourceState state, bool prompt = false) => SetHealth(state, null, prompt);

        public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
        {
            if (PartialThenThrow)
            {
                into.Add(Playlist("partial", "Partial"));
                throw new InvalidOperationException("boom after a partial fill");
            }
            int max = request.MaxItems > 0 ? request.MaxItems : _rows.Count;
            int n = _rows.Count < max ? _rows.Count : max;
            for (int i = 0; i < n; i++) into.Add(_rows[i]);
            return n;
        }
    }

    static SidebarSectionSlice Resolve(SidebarSectionSpec section, ISidebarContributionHost? host,
        List<SidebarLibraryEntry> pool, SidebarContributionCache? cache = null)
        => SidebarBinderPipeline.Resolve(section, host, pool, cache);

    [Fact]
    public void AnUnregisteredContribution_ResolvesToMissing()
    {
        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "charts"), new SidebarDataSourceTable(), pool);
        Assert.Equal(SidebarContributionAvailability.Missing, slice.Availability);
        Assert.Equal(0, slice.Count);
        Assert.Empty(pool);
    }

    [Fact]
    public void ASectionWithNoExtensionRef_ResolvesToMissing()
    {
        var pool = new List<SidebarLibraryEntry>();
        var bare = new SidebarSectionSpec("sec_bare", SidebarSectionKind.Extension, null, null);
        Assert.Equal(SidebarContributionAvailability.Missing, Resolve(bare, new SidebarDataSourceTable(), pool).Availability);
    }

    [Fact]
    public void ADisabledContribution_ResolvesToDisabled_AndKeepsTheSection()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha")));
        table.SetEnabled(SidebarContributions.Library, false);

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library"), table, pool);
        Assert.Equal(SidebarContributionAvailability.Disabled, slice.Availability);
        Assert.Empty(pool);
    }

    [Fact]
    public void ANewerConfigSchema_ResolvesToIncompatible_AndChangesNothing()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha")));

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library", schemaVersion: 2), table, pool);
        Assert.Equal(SidebarContributionAvailability.Incompatible, slice.Availability);
        Assert.Empty(pool);
    }

    [Fact]
    public void ALiveSource_FillsAWindowIntoTheSharedPool()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha"), Playlist("2", "Beta")));

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library"), table, pool);

        Assert.Equal(SidebarContributionAvailability.Live, slice.Availability);
        Assert.Equal(0, slice.Start);
        Assert.Equal(2, slice.Count);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void TheSectionsMaxItems_ReachesTheSourceAsTheRequestBound()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library)
            .With(Playlist("1", "Alpha"), Playlist("2", "Beta"), Playlist("3", "Gamma")));

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library", maxItems: 2), table, pool);
        Assert.Equal(2, slice.Count);
    }

    [Fact]
    public void EveryExtensionSection_GetsADisjointWindowOverOnePool()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha")));
        table.Add(new StubSource(SidebarContributions.Queue).With(Album("9", "Ceremony"), Album("8", "Other")));

        var pool = new List<SidebarLibraryEntry>();
        var slices = new SidebarExtensionSlices();
        SidebarBinderPipeline.ResolveExtensions(
            Doc(ExtSection("sec_1", "library"), ExtSection("sec_2", "queue")), table, pool, slices);

        Assert.True(slices.TryGet("sec_1", out var one));
        Assert.True(slices.TryGet("sec_2", out var two));
        Assert.Equal((0, 1), (one.Start, one.Count));
        Assert.Equal((1, 2), (two.Start, two.Count));
        Assert.Equal(3, pool.Count);
    }

    [Fact]
    public void ExtensionSections_NestedInACustomGroup_AreResolvedToo()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Queue).With(Album("9", "Ceremony")));

        var group = new SidebarSectionSpec("sec_group", SidebarSectionKind.CustomGroup, null, null)
        {
            Children = new[] { ExtSection("sec_child", "queue") },
        };
        var pool = new List<SidebarLibraryEntry>();
        var slices = new SidebarExtensionSlices();
        SidebarBinderPipeline.ResolveExtensions(Doc(group), table, pool, slices);

        Assert.True(slices.TryGet("sec_child", out var slice));
        Assert.Equal(1, slice.Count);
    }

    [Fact]
    public void AThrowingSource_LeaksNoPartialRows_AndReportsError()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new StubSource(SidebarContributions.Library) { PartialThenThrow = true });

        var pool = new List<SidebarLibraryEntry> { Album("9", "Pre-existing") };
        var slice = Resolve(ExtSection("sec_1", "library"), table, pool);

        Assert.Equal(SidebarSourceState.Error, slice.State);
        Assert.Equal(0, slice.Count);
        Assert.Single(pool);
        Assert.Equal("album:spotify:album:9", pool[0].Id);
    }

    [Fact]
    public void AFailedSource_ReplaysItsLastGoodSnapshot_AsCached()
    {
        var table = new SidebarDataSourceTable();
        var source = new StubSource(SidebarContributions.Library).With(Playlist("1", "Alpha"), Playlist("2", "Beta"));
        table.Add(source);
        var cache = new SidebarContributionCache();
        var section = ExtSection("sec_1", "library");

        var pool = new List<SidebarLibraryEntry>();
        var live = Resolve(section, table, pool, cache);
        Assert.Equal(SidebarContributionAvailability.Live, live.Availability);
        Assert.True(cache.Has(SidebarContributions.Library));

        source.With();
        source.Publish(SidebarSourceState.Error);
        pool.Clear();
        var stale = Resolve(section, table, pool, cache);

        Assert.Equal(SidebarContributionAvailability.Cached, stale.Availability);
        Assert.Equal(SidebarSourceState.Ready, stale.State);
        Assert.Equal(2, stale.Count);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void AFailingSourceWithNoSnapshot_IsAnErrorSlice_NotACachedOne()
    {
        var table = new SidebarDataSourceTable();
        var source = new StubSource(SidebarContributions.Library);
        source.Publish(SidebarSourceState.Error);
        table.Add(source);

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "library"), table, pool, new SidebarContributionCache());
        Assert.Equal(SidebarContributionAvailability.Live, slice.Availability);
        Assert.Equal(SidebarSourceState.Error, slice.State);
        Assert.Equal(0, slice.Count);
    }

    [Fact]
    public void AnActionableDegradedState_TravelsToTheSlice()
    {
        var table = new SidebarDataSourceTable();
        var concerts = new StubSource(SidebarContributions.Concerts);
        concerts.Publish(SidebarSourceState.Ready, prompt: true);
        table.Add(concerts);

        var pool = new List<SidebarLibraryEntry>();
        var slice = Resolve(ExtSection("sec_1", "concerts"), table, pool);
        Assert.True(slice.NeedsPrompt);
        Assert.Equal(SidebarSourceState.Ready, slice.State);
        Assert.Equal(0, slice.Count);
    }

    [Fact]
    public void TheSliceTable_ReportsAvailabilityForTheSurfacesBadge()
    {
        var slices = new SidebarExtensionSlices();
        slices.Set("sec_1", new SidebarSectionSlice(0, 0, SidebarSourceState.Error, SidebarContributionAvailability.Disabled));
        Assert.Equal(SidebarContributionAvailability.Disabled, slices.AvailabilityOf("sec_1"));
        Assert.Equal(SidebarContributionAvailability.Missing, slices.AvailabilityOf("sec_unknown"));
        slices.Clear();
        Assert.Equal(0, slices.Count);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── FINGERPRINT (G-059) — the rebuild gate's content lane (SidebarLibraryFingerprint, Sidebar.cs) must wake a
//    cover-less playlist's row when its MOSAIC-feeding edge lands, not just when the playlist's own row changes ─────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[Collection(EntitiesCollection.Name)]
public class SidebarLibraryFingerprintPlaylistTracksTests
{
    [Fact]
    public void ACoverlessPlaylists_TracksLandingOnTheEdgeDirectly_MovesTheFingerprint()
    {
        TestScope.Fresh();
        var p = SidebarWiringStage.StagePlaylist("spotify:playlist:coverless", "No Cover");
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, p);

        long before = SidebarLibraryFingerprint.Of(in me, default);

        // The REAL fetch path (Spotify.Api.Playlist.cs) writes the tracks edge DIRECTLY — it never calls
        // Playlist.ApplyMembership/ApplyPage, so it never bumps the playlist row's own Table.Version. Only folding
        // PlaylistTracks.Version(slot) in for a cover-less row (SidebarLibraryFingerprint.PlaylistRow) catches this;
        // before the fix, `before` and the post-landing fold were EQUAL and the row never re-rendered its mosaic.
        Entities.Current.Edges.PlaylistTracks.Replace(
            p.Slot, ReadOnlySpan<int>.Empty, ReadOnlySpan<PlaylistTrackEdge>.Empty, EdgeState.Complete, 0);

        Assert.NotEqual(before, SidebarLibraryFingerprint.Of(in me, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACoveredPlaylists_EdgeLandingMovesFingerprintOnlyWhenCountIsUnknown(bool countKnown)
    {
        // Covered rows still derive their count from membership until an authoritative count lands.
        // Once both cover and count are known, membership does not affect this row's presentation.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Playlists.Add();
        row.Id = s.Text("spotify:playlist:covered");
        row.Title = s.Text("Has A Cover");
        row.Image = s.Text("spotify:image:cover");
        row.Caps = (byte)PlaylistCaps.CanView;
        row.Known = (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities | (countKnown ? PlaylistFields.TrackCount : 0));
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);
        var p = Entities.Playlist(EntityUri.Parse("spotify:playlist:covered"));
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, p);

        long before = SidebarLibraryFingerprint.Of(in me, default);
        Entities.Current.Edges.PlaylistTracks.Replace(
            p.Slot, ReadOnlySpan<int>.Empty, ReadOnlySpan<PlaylistTrackEdge>.Empty, EdgeState.Complete, 0);

        long after = SidebarLibraryFingerprint.Of(in me, default);
        if (countKnown) Assert.Equal(before, after);
        else Assert.NotEqual(before, after);
    }

    /// <summary>Bug A2: the mosaic needs each member TRACK's own <c>AlbumSlot</c>/<c>ImageId</c>, which can land in a
    /// LATER drain than the membership edge itself — the batched <c>TrackFields.Identity</c> ensure over bare
    /// uri-only slots (WalkRootlist §5). The edge's own Version does not move for that (it already landed); only
    /// folding the member TRACK ROWS' own versions into `PlaylistRow` catches a track's identity arriving after its
    /// uri did.</summary>
    [Fact]
    public void ACoverlessPlaylists_AMemberTracksIdentityLandingLater_MovesTheFingerprint()
    {
        TestScope.Fresh();
        var p = SidebarWiringStage.StagePlaylist("spotify:playlist:coverless2", "No Cover 2");
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, p);

        // A bare, uri-only member track slot — exactly what the membership edge alone creates (the item decoder,
        // Spotify.Decode.cs's PlaylistRevision, stages only the target uri: no title, no album, no image).
        var track = Entities.Track(EntityUri.Parse("spotify:track:bare"));
        Assert.False(track.Knows(TrackFields.Identity));
        Entities.Current.Edges.PlaylistTracks.Replace(
            p.Slot, new[] { track.Slot }, new PlaylistTrackEdge[1], EdgeState.Complete, 1);

        long afterMembership = SidebarLibraryFingerprint.Of(in me, default);

        // The track's OWN Identity lands later; the edge does not move for this — only the track row's Version does.
        Entities.Current.Tracks.Bump(track.Slot, (uint)TrackFields.Identity);

        Assert.NotEqual(afterMembership, SidebarLibraryFingerprint.Of(in me, default));
    }
}
