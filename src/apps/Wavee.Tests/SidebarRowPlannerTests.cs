using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// The Curated pane's render contract (§C1.7 / §C8.5). Everything the renderer does is downstream of this plan, so the
// row SEQUENCE — not just the row count — is pinned per section kind, per degraded state, and at 10 000 entries.
public sealed class SidebarRowPlannerTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string uri, string name,
        string creator = "", long visited = 0, long sortStamp = 1, int sourceOrder = 0, int depth = 0,
        SidebarPlaylistFlavor flavor = SidebarPlaylistFlavor.None, bool pinned = false)
        => new(Id: id, Kind: kind, Uri: uri, Name: name, Creator: creator, Cover: null, MosaicTiles: null,
            ChildCount: 0, AddedAtMs: 0, SortStamp: sortStamp, LastVisitedTicksUtc: visited,
            SourceOrder: sourceOrder, Depth: depth, Circular: false, Flavor: flavor) { IsPinned = pinned };

    static SidebarLibraryEntry Playlist(string slug, string name, long visited = 0, int order = 0, int depth = 0,
        SidebarPlaylistFlavor flavor = SidebarPlaylistFlavor.None, bool pinned = false)
        => Entry("pl:spotify:playlist:" + slug, SidebarEntryKind.Playlist, "spotify:playlist:" + slug, name,
            creator: "Owner", visited: visited, sortStamp: 100 + order, sourceOrder: order, depth: depth,
            flavor: flavor, pinned: pinned);

    static SidebarLibraryEntry Album(string slug, string name, int order = 0)
        => Entry("album:spotify:album:" + slug, SidebarEntryKind.Album, "spotify:album:" + slug, name,
            creator: "Artist", sortStamp: 200 + order, sourceOrder: order);

    static SidebarLibraryEntry Folder(string id, string name, int depth = 0, int order = 0)
        => Entry("folder:" + id, SidebarEntryKind.Folder, "", name, sourceOrder: order, depth: depth)
            with { FolderId = id, FolderName = name };

    static SidebarSectionSpec Sec(string id, SidebarSectionKind kind, SidebarDisplayOptions? display = null,
        IReadOnlyList<SidebarItemSpec>? items = null, SidebarEntityQuery? query = null,
        IReadOnlyList<SidebarSectionSpec>? children = null, bool hidden = false, bool collapsed = false,
        string? titleLocKey = "sidebar.section.header")
        => new(id, kind, null, titleLocKey, hidden, collapsed, display, items, query, children);

    static SidebarCustomLayout Doc(params SidebarSectionSpec[] sections)
        => new(SidebarTemplates.Curated, sections);

    static SidebarItemSpec Route(string id, string key) => new(id, SidebarItemTarget.Route, key);

    static SidebarItemSpec Entity(string id, string uri, SidebarEntityKind kind = SidebarEntityKind.Playlist,
        string? fallbackTitle = null)
        => new(id, SidebarItemTarget.Entity, uri, kind, FallbackTitle: fallbackTitle);

    static SidebarRowKind[] KindsOf(SidebarRowPlan plan)
    {
        var k = new SidebarRowKind[plan.Rows.Count];
        for (int i = 0; i < k.Length; i++) k[i] = plan.Rows[i].Kind;
        return k;
    }

    /// <summary>A projection with every source populated, so a per-kind expectation never fails for want of data.</summary>
    static SidebarProjectionInput FullInput()
    {
        var pl1 = Playlist("1", "Alpha mix", visited: 500, order: 0, flavor: SidebarPlaylistFlavor.ByYou);
        var pl2 = Playlist("2", "Beta mix", visited: 400, order: 1, flavor: SidebarPlaylistFlavor.BySpotify);
        var al1 = Album("9", "Ceremony", order: 2);

        var byUri = new Dictionary<string, SidebarLibraryEntry>(StringComparer.Ordinal)
        {
            ["spotify:playlist:1"] = pl1,
            ["spotify:playlist:2"] = pl2,
            ["spotify:album:9"] = al1,
        };

        return new SidebarProjectionInput
        {
            Library = new[] { pl1, pl2, al1 },
            PlaylistTree = new[] { Folder("f1", "Chill", 0, 0), Playlist("3", "Inside folder", order: 1, depth: 1), pl1 },
            Pins = new[] { pl2, al1 },
            Visited = new[] { pl1, al1 },
            Played = new[] { al1 },
            NewReleases = new[] { Album("n1", "New one"), Album("n2", "New two") },
            Concerts = new[] { Entry("concert:1", SidebarEntryKind.Album, "spotify:concert:1", "Gig", "Venue") },
            ByUri = byUri,
            Revision = 7,
        };
    }

    // ── per-kind row sequences ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachSectionKind_EmitsExpectedRowSequence()
    {
        var input = FullInput();

        Check(Sec("s", SidebarSectionKind.Pinned),
            [SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow, SidebarRowKind.EntityRow]);

        Check(Sec("s", SidebarSectionKind.JumpBackIn),
            [SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow, SidebarRowKind.EntityRow]);

        Check(Sec("s", SidebarSectionKind.CollectionShortcuts,
                items: [Route("i1", "liked"), Route("i2", "albums")]),
            [SidebarRowKind.SectionHeader, SidebarRowKind.IconRow, SidebarRowKind.IconRow]);

        // TreeEnd is the tree's closing gutter — the "top level, at the end" drop slot — and it is now the section's
        // LAST row outright. The create ROW that used to follow it (and squat that very slot, duplicating a dragged
        // playlist into a new one) is deleted: the affordance is the section HEADER's "+".
        Check(Sec("s", SidebarSectionKind.PlaylistTree),
            [SidebarRowKind.SectionHeader, SidebarRowKind.FolderHeader, SidebarRowKind.EntityRow,
             SidebarRowKind.EntityRow, SidebarRowKind.TreeEnd]);

        Check(Sec("s", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default),
            [SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow, SidebarRowKind.EntityRow,
             SidebarRowKind.EntityRow]);

        Check(Sec("s", SidebarSectionKind.StaticLinks, items: [Route("i1", "home"), Route("i2", "search")]),
            [SidebarRowKind.SectionHeader, SidebarRowKind.IconRow, SidebarRowKind.IconRow]);

        Check(Sec("s", SidebarSectionKind.CustomGroup, items: [Route("i1", "home")],
                children: [Sec("c", SidebarSectionKind.Header)]),
            [SidebarRowKind.SectionHeader, SidebarRowKind.IconRow, SidebarRowKind.HeaderLabel]);

        Check(Sec("s", SidebarSectionKind.Header), [SidebarRowKind.HeaderLabel]);

        // A lone divider is both leading AND trailing — it draws nothing.
        Check(Sec("s", SidebarSectionKind.Divider, titleLocKey: null), []);

        Check(Sec("s", SidebarSectionKind.EntityEmbed, items: [Entity("i1", "spotify:album:9",
                SidebarEntityKind.Album)]),
            [SidebarRowKind.SectionHeader, SidebarRowKind.EntityCard]);

        Check(Sec("s", SidebarSectionKind.NewReleases),
            [SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow, SidebarRowKind.EntityRow]);

        Check(Sec("s", SidebarSectionKind.Concerts),
            [SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow]);

        void Check(SidebarSectionSpec spec, SidebarRowKind[] expected)
        {
            var plan = SidebarRowPlanner.Build(Doc(spec), input);
            Assert.Equal(expected, KindsOf(plan));
            foreach (var row in plan.Rows)
            {
                Assert.False(string.IsNullOrEmpty(row.Key));
                Assert.False(string.IsNullOrEmpty(row.SectionId));
                if (row.EntryIndex >= 0) Assert.InRange(row.EntryIndex, 0, plan.Entries.Count - 1);
            }
        }
    }

    [Fact]
    public void UnknownSectionKind_EmitsNothing()
    {
        var plan = SidebarRowPlanner.Build(
            Doc(new SidebarSectionSpec("s", (SidebarSectionKind)200, Title: "future")), FullInput());
        Assert.Empty(plan.Rows);
    }

    [Fact]
    public void HiddenSection_EmitsNothing()
    {
        var input = FullInput();
        var plan = SidebarRowPlanner.Build(Doc(
            Sec("a", SidebarSectionKind.Pinned, hidden: true),
            Sec("b", SidebarSectionKind.CollectionShortcuts, items: [Route("i1", "liked")])), input);

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.IconRow}, KindsOf(plan));
        foreach (var r in plan.Rows) Assert.Equal("b", r.SectionId);
    }

    [Fact]
    public void CollapsedSection_EmitsHeaderOnly()
    {
        var plan = SidebarRowPlanner.Build(Doc(Sec("a", SidebarSectionKind.Pinned, collapsed: true)), FullInput());
        Assert.Equal(new[] {SidebarRowKind.SectionHeader}, KindsOf(plan));
        Assert.Empty(plan.Entries);
    }

    [Fact]
    public void Divider_LeadingAndTrailing_AreDropped_AndConsecutiveCollapse()
    {
        var input = FullInput();
        var plan = SidebarRowPlanner.Build(Doc(
            Sec("d0", SidebarSectionKind.Divider, titleLocKey: null),
            Sec("d1", SidebarSectionKind.Divider, titleLocKey: null),
            Sec("h1", SidebarSectionKind.Header),
            Sec("d2", SidebarSectionKind.Divider, titleLocKey: null),
            Sec("d3", SidebarSectionKind.Divider, titleLocKey: null),
            Sec("h2", SidebarSectionKind.Header),
            Sec("d4", SidebarSectionKind.Divider, titleLocKey: null)), input);

        Assert.Equal(new[] {SidebarRowKind.HeaderLabel, SidebarRowKind.Divider, SidebarRowKind.HeaderLabel},
            KindsOf(plan));
        // The surviving divider is the LAST of the collapsed run (the one closest to the content it separates).
        Assert.Equal("d3", plan.Rows[1].SectionId);
    }

    [Fact]
    public void HiddenSectionBetweenDividers_DoesNotStrandARule()
    {
        var plan = SidebarRowPlanner.Build(Doc(
            Sec("h1", SidebarSectionKind.Header),
            Sec("d1", SidebarSectionKind.Divider, titleLocKey: null),
            Sec("gone", SidebarSectionKind.Pinned, hidden: true)), FullInput());

        Assert.Equal(new[] {SidebarRowKind.HeaderLabel}, KindsOf(plan));
    }

    [Fact]
    public void SectionBodyRange_StopsAtDividerAndHeaderlessRootSibling()
    {
        var plan = SidebarRowPlanner.Build(Doc(
            Sec("library", SidebarSectionKind.StaticLinks,
                items: [Route("a", "liked"), Route("b", "albums")]),
            Sec("rule", SidebarSectionKind.Divider, titleLocKey: null),
            Sec("api", SidebarSectionKind.StaticLinks, items: [Route("console", "api")], titleLocKey: null)),
            FullInput());

        Assert.True(SidebarRowGeometry.TrySectionBodyRange(plan.Rows, "library", out int first, out int count));
        Assert.Equal(1, first);
        Assert.Equal(2, count);
        Assert.Equal(SidebarRowKind.Divider, plan.Rows[first + count].Kind);
        Assert.Equal("api", plan.Rows[first + count + 1].SectionId);
    }

    [Fact]
    public void SectionBodyRange_KeepsNestedGroupRowsAndTheirDividerAtNestedDepth()
    {
        var group = Sec("group", SidebarSectionKind.CustomGroup,
            items: [Route("own", "home")],
            children:
            [
                Sec("child-a", SidebarSectionKind.Header),
                Sec("child-rule", SidebarSectionKind.Divider, titleLocKey: null),
                Sec("child-b", SidebarSectionKind.Header),
            ]);
        var plan = SidebarRowPlanner.Build(Doc(group,
            Sec("api", SidebarSectionKind.StaticLinks, items: [Route("console", "api")], titleLocKey: null)),
            FullInput());

        Assert.True(SidebarRowGeometry.TrySectionBodyRange(plan.Rows, "group", out int first, out int count));
        Assert.Equal(1, first);
        Assert.Equal(4, count);
        Assert.Equal((byte)1, plan.Rows[first + 2].Depth);
        Assert.Equal(SidebarRowKind.Divider, plan.Rows[first + 2].Kind);
        Assert.Equal("api", plan.Rows[first + count].SectionId);
        Assert.Equal((byte)0, plan.Rows[first + count].Depth);
    }

    // ── grids, truncation, degraded states ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GridSection_ChunksIntoStrips()
    {
        var pins = new SidebarLibraryEntry[7];
        for (int i = 0; i < pins.Length; i++) pins[i] = Playlist("g" + i, "Grid " + i, order: i);

        var input = new SidebarProjectionInput { Pins = pins };
        var plan = SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.Pinned,
            SidebarDisplayOptions.Entities with { Presentation = SidebarPresentation.Grid, GridColumns = 3 })), input);

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.GridStrip, SidebarRowKind.GridStrip,
            SidebarRowKind.GridStrip}, KindsOf(plan));
        Assert.Equal(7, plan.Entries.Count);
        Assert.Equal((0, 3), (plan.Rows[1].EntryIndex, plan.Rows[1].ItemCount));
        Assert.Equal((3, 3), (plan.Rows[2].EntryIndex, plan.Rows[2].ItemCount));
        Assert.Equal((6, 1), (plan.Rows[3].EntryIndex, plan.Rows[3].ItemCount));
    }

    [Fact]
    public void GridColumns_AreClampedAtPlanTime()
    {
        var pins = new SidebarLibraryEntry[4];
        for (int i = 0; i < pins.Length; i++) pins[i] = Playlist("g" + i, "Grid " + i, order: i);
        var input = new SidebarProjectionInput { Pins = pins };

        // A hand-edited document with 99 columns still plans a legal grid.
        var plan = SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.Pinned,
            SidebarDisplayOptions.Entities with { Presentation = SidebarPresentation.Grid, GridColumns = 99 })), input);
        Assert.Equal(4, plan.Rows[1].ItemCount);
        Assert.Equal(2, plan.Rows.Count);
    }

    [Fact]
    public void MaxItems_TruncatesWithoutTouchingTheDocument()
    {
        var lib = new SidebarLibraryEntry[10];
        for (int i = 0; i < lib.Length; i++) lib[i] = Playlist("m" + i, "Mix " + i, visited: 100 - i, order: i);
        var input = new SidebarProjectionInput { Library = lib, Pins = lib };

        var doc = Doc(Sec("s", SidebarSectionKind.EntityList,
            SidebarDisplayOptions.Entities with { MaxItems = 3 }, query: SidebarEntityQuery.Default));

        var plan = SidebarRowPlanner.Build(doc, input);
        Assert.Equal(4, plan.Rows.Count);                     // header + 3
        Assert.Equal(3, plan.Entries.Count);
        Assert.Equal(3, doc.Sections[0].Opts.MaxItems);        // the document is untouched

        // Pinned honours MaxItems too.
        var pinnedPlan = SidebarRowPlanner.Build(Doc(Sec("p", SidebarSectionKind.Pinned,
            SidebarDisplayOptions.Entities with { MaxItems = 2 })), input);
        Assert.Equal(3, pinnedPlan.Rows.Count);
    }

    [Fact]
    public void PendingSource_EmitsSkeletonRows_ReadySourceDoesNot()
    {
        var pending = new SidebarProjectionInput
        {
            LibraryState = SidebarSourceState.Pending,
            TreeState = SidebarSourceState.Pending,
            RecentsState = SidebarSourceState.Pending,
            NewReleasesState = SidebarSourceState.Pending,
            ConcertsState = SidebarSourceState.Pending,
        };

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Skeleton, SidebarRowKind.Skeleton,
            SidebarRowKind.Skeleton}, KindsOf(SidebarRowPlanner.Build(
                Doc(Sec("s", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default)), pending)));

        // A pending tree plans SKELETONS and nothing else. Its create affordance is the header's "+", which is chrome
        // the renderer draws inside the header row — so it survives a pending source without costing a planned row.
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Skeleton, SidebarRowKind.Skeleton,
            SidebarRowKind.Skeleton}, KindsOf(SidebarRowPlanner.Build(
                Doc(Sec("s", SidebarSectionKind.PlaylistTree)), pending)));

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Skeleton, SidebarRowKind.Skeleton,
            SidebarRowKind.Skeleton}, KindsOf(SidebarRowPlanner.Build(
                Doc(Sec("s", SidebarSectionKind.JumpBackIn)), pending)));

        // Once the source is ready — even ready-and-empty — a skeleton is a lie.
        var ready = new SidebarProjectionInput();
        foreach (var kind in new[] { SidebarSectionKind.EntityList, SidebarSectionKind.JumpBackIn,
            SidebarSectionKind.NewReleases })
        {
            var plan = SidebarRowPlanner.Build(Doc(Sec("s", kind, query: SidebarEntityQuery.Default)), ready);
            Assert.DoesNotContain(SidebarRowKind.Skeleton, KindsOf(plan));
        }

        // A pending source that already has (stale) data renders the data, not a skeleton.
        var stale = FullInput() with { LibraryState = SidebarSourceState.Pending };
        Assert.DoesNotContain(SidebarRowKind.Skeleton, KindsOf(SidebarRowPlanner.Build(
            Doc(Sec("s", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default)), stale)));
    }

    [Fact]
    public void EmptySection_EmitsEmptyRow()
    {
        var empty = new SidebarProjectionInput();
        foreach (var kind in new[] { SidebarSectionKind.JumpBackIn, SidebarSectionKind.EntityList,
            SidebarSectionKind.NewReleases, SidebarSectionKind.Concerts, SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.StaticLinks, SidebarSectionKind.CustomGroup, SidebarSectionKind.EntityEmbed })
        {
            var plan = SidebarRowPlanner.Build(Doc(Sec("s", kind, query: SidebarEntityQuery.Default)), empty);
            Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Empty}, KindsOf(plan));
            Assert.Equal("s", plan.Rows[1].Key);
        }

        // An EMPTY tree is the kind's ordinary Empty hint and nothing else — the hint itself names the header "+".
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Empty},
            KindsOf(SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.PlaylistTree)), empty)));
    }

    [Fact]
    public void EmptyPinned_EmitsDropZoneRow()
    {
        var plan = SidebarRowPlanner.Build(Doc(Sec("p", SidebarSectionKind.Pinned)), new SidebarProjectionInput());
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Empty}, KindsOf(plan));
        Assert.Equal("p", plan.Rows[1].SectionId);
        Assert.Equal(-1, plan.Rows[1].EntryIndex);
    }

    [Fact]
    public void HiddenPinOverride_RemovesThatPinFromThePlan()
    {
        var input = FullInput();
        var hide = new SidebarItemSpec("i1", SidebarItemTarget.Entity, "spotify:playlist:2",
            SidebarEntityKind.Playlist, Hidden: true);

        var plan = SidebarRowPlanner.Build(Doc(Sec("p", SidebarSectionKind.Pinned, items: [hide])), input);
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow}, KindsOf(plan));
        Assert.Equal("album:spotify:album:9", plan.Entries[0].Id);
    }

    [Fact]
    public void ExpandedPinnedFolder_EmitsItsVisibleSubtreeAtRelativeDepth()
    {
        var root = Folder("f1", "Pinned folder", depth: 2, order: 0) with { IsPinned = true };
        var nested = Folder("f2", "Collapsed child folder", depth: 3, order: 2);
        var input = new SidebarProjectionInput
        {
            Pins = [root, Album("after", "Independent pin")],
            PlaylistTree =
            [
                root with { IsPinned = false },
                Playlist("a", "Direct child", order: 1, depth: 3),
                nested,
                Playlist("b", "Hidden grandchild", order: 3, depth: 4),
                Playlist("outside", "Outside subtree", order: 4, depth: 2),
            ],
            ExpandedFolders = new HashSet<string>(StringComparer.Ordinal) { "f1" },
        };

        var plan = SidebarRowPlanner.Build(Doc(Sec("p", SidebarSectionKind.Pinned)), input);

        Assert.Equal(new[]
        {
            SidebarRowKind.SectionHeader,
            SidebarRowKind.FolderHeader,
            SidebarRowKind.EntityRow,
            SidebarRowKind.FolderHeader,
            SidebarRowKind.EntityRow,
        }, KindsOf(plan));
        Assert.Equal(new[] { "Pinned folder", "Direct child", "Collapsed child folder", "Independent pin" },
            NamesOf(plan));
        Assert.Equal(new byte[] { 0, 0, 1, 1, 0 }, DepthsOf(plan));
    }

    // ── the playlist tree ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlaylistTree_EmitsDepthAwareFolderHeaders_AndTheClosingGutterLast()
    {
        var tree = new[]
        {
            Folder("f1", "Chill", depth: 0, order: 0),
            Playlist("a", "Inner A", order: 1, depth: 1),
            Folder("f2", "Deeper", depth: 1, order: 2),
            Playlist("b", "Inner B", order: 3, depth: 2),
            Playlist("c", "Top level", order: 4, depth: 0),
        };
        var plan = SidebarRowPlanner.Build(Doc(Sec("t", SidebarSectionKind.PlaylistTree)),
            new SidebarProjectionInput { PlaylistTree = tree });

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.FolderHeader, SidebarRowKind.EntityRow,
            SidebarRowKind.FolderHeader, SidebarRowKind.EntityRow, SidebarRowKind.EntityRow,
            SidebarRowKind.TreeEnd}, KindsOf(plan));

        Assert.Equal(new byte[] { 0, 0, 1, 1, 2, 0, 0 }, DepthsOf(plan));
        // The closing gutter is the section's LAST row — it owns the "top level, at the end" drop slot, and there is no
        // longer a create row underneath it to accept a rootlist payload that was aimed here.
        Assert.Equal(SidebarRowKind.TreeEnd, plan.Rows[^1].Kind);
        Assert.Equal(-1, plan.Rows[^1].EntryIndex);
        Assert.Equal("folder:f1", plan.Rows[1].Key);
    }

    [Fact]
    public void CollapsedFolder_HidesItsWholeSubtree()
    {
        var tree = new[]
        {
            Folder("f1", "Chill", depth: 0, order: 0),
            Playlist("a", "Inner A", order: 1, depth: 1),
            Folder("f2", "Deeper", depth: 1, order: 2),
            Playlist("b", "Inner B", order: 3, depth: 2),
            Playlist("c", "Top level", order: 4, depth: 0),
        };

        // Only f2 is expanded -> f1 is collapsed, so everything under it disappears (f2 included).
        var input = new SidebarProjectionInput
        {
            PlaylistTree = tree,
            ExpandedFolders = new HashSet<string>(StringComparer.Ordinal) { "f2" },
        };
        var plan = SidebarRowPlanner.Build(Doc(Sec("t", SidebarSectionKind.PlaylistTree)), input);

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.FolderHeader, SidebarRowKind.EntityRow,
            SidebarRowKind.TreeEnd}, KindsOf(plan));
        Assert.Equal("folder:f1", plan.Rows[1].Key);
        Assert.Equal("pl:spotify:playlist:c", plan.Rows[2].Key);
    }

    [Fact]
    public void PlaylistTree_NullQueryPreservesExactSourceOrder()
    {
        var input = new SidebarProjectionInput
        {
            PlaylistTree =
            [
                Playlist("z", "Zulu root", order: 0),
                Folder("f", "Folder", depth: 0, order: 1),
                Playlist("b", "Bravo child", order: 2, depth: 1),
                Playlist("a", "Alpha child", order: 3, depth: 1),
                Playlist("a-root", "Alpha root", order: 4),
            ],
        };

        var plan = SidebarRowPlanner.Build(Doc(Sec("t", SidebarSectionKind.PlaylistTree)), input);
        Assert.Equal(new[] { "Zulu root", "Folder", "Bravo child", "Alpha child", "Alpha root" },
            NamesOf(plan));
    }

    [Fact]
    public void PlaylistTree_QuerySortsLeafSlotsWithinEachParentAndKeepsFoldersStructural()
    {
        var input = new SidebarProjectionInput
        {
            PlaylistTree =
            [
                Playlist("z-root", "Zulu root", order: 0),
                Folder("f", "Folder F", depth: 0, order: 1),
                Playlist("z-child", "Zulu child", order: 2, depth: 1),
                Playlist("a-child", "Alpha child", order: 3, depth: 1),
                Folder("g", "Folder G", depth: 0, order: 4),
                Playlist("m-child", "Middle child", order: 5, depth: 1),
                Playlist("a-root", "Alpha root", order: 6),
            ],
        };
        var query = new SidebarEntityQuery(SidebarEntityKinds.Playlists,
            SidebarSortMode.Alphabetical, Descending: false);

        var plan = SidebarRowPlanner.Build(
            Doc(Sec("t", SidebarSectionKind.PlaylistTree, query: query)), input);

        Assert.Equal(new[]
        {
            "Alpha root", "Folder F", "Alpha child", "Zulu child",
            "Folder G", "Middle child", "Zulu root",
        }, NamesOf(plan));
    }

    [Fact]
    public void PlaylistTree_QueryFiltersQualifiersAndPrunesEmptyFolders()
    {
        var input = new SidebarProjectionInput
        {
            PlaylistTree =
            [
                Folder("you", "Your folder", order: 0),
                Playlist("mine", "Mine", order: 1, depth: 1, flavor: SidebarPlaylistFlavor.ByYou),
                Folder("spotify", "Spotify folder", order: 2),
                Playlist("made", "Made for you", order: 3, depth: 1,
                    flavor: SidebarPlaylistFlavor.BySpotify),
            ],
        };
        var query = new SidebarEntityQuery(SidebarEntityKinds.Playlists,
            SidebarSortMode.CustomOrder, Qualifier: SidebarPlaylistQualifier.ByYou,
            IncludeUris: ["spotify:playlist:mine", "spotify:playlist:made"],
            ExcludeUris: ["spotify:playlist:made"]);

        var plan = SidebarRowPlanner.Build(
            Doc(Sec("t", SidebarSectionKind.PlaylistTree, query: query)), input);
        Assert.Equal(new[] { "Your folder", "Mine" }, NamesOf(plan));
        Assert.DoesNotContain(plan.Rows, row => row.Key == "folder:spotify");
    }

    [Fact]
    public void PlaylistTree_GridFlattensAllDescendantsAndUsesConfiguredColumns()
    {
        var display = SidebarDisplayOptions.Default with
        {
            Presentation = SidebarPresentation.Grid,
            GridColumns = 2,
        };
        var input = new SidebarProjectionInput
        {
            PlaylistTree =
            [
                Folder("f", "Folder", order: 0),
                Playlist("c", "Charlie", order: 1, depth: 1),
                Playlist("a", "Alpha", order: 2, depth: 1),
                Playlist("b", "Bravo", order: 3),
            ],
            ExpandedFolders = new HashSet<string>(StringComparer.Ordinal),
        };

        var source = SidebarRowPlanner.Build(
            Doc(Sec("t", SidebarSectionKind.PlaylistTree, display: display)), input);
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, NamesOf(source));
        Assert.Equal(new[]
        {
            SidebarRowKind.SectionHeader, SidebarRowKind.GridStrip,
            SidebarRowKind.GridStrip, SidebarRowKind.TreeEnd,
        }, KindsOf(source));
        Assert.Equal(2, source.Rows[1].ItemCount);
        Assert.Equal(1, source.Rows[2].ItemCount);

        var sorted = SidebarRowPlanner.Build(Doc(Sec("t", SidebarSectionKind.PlaylistTree,
            display: display with { GridColumns = 3 }, query: SidebarEntityQuery.PlaylistsAlphabetical)), input);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, NamesOf(sorted));
        Assert.Equal(3, sorted.Rows[1].ItemCount);
        Assert.DoesNotContain(sorted.Rows, row => row.Kind == SidebarRowKind.FolderHeader);
    }

    static byte[] DepthsOf(SidebarRowPlan plan)
    {
        var d = new byte[plan.Rows.Count];
        for (int i = 0; i < d.Length; i++) d[i] = plan.Rows[i].Depth;
        return d;
    }

    static string[] NamesOf(SidebarRowPlan plan)
    {
        var names = new string[plan.Entries.Count];
        for (int i = 0; i < names.Length; i++) names[i] = plan.Entries[i].Name;
        return names;
    }

    // ── search ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Search_FiltersEntityListAndPlaylistTree_ButNotShortcuts()
    {
        var input = FullInput() with { Search = "Alpha" };

        var list = SidebarRowPlanner.Build(
            Doc(Sec("e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default)), input);
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow}, KindsOf(list));
        Assert.Equal("Alpha mix", list.Entries[0].Name);

        // The tree flattens while searching: matching leaves only, no folder chrome.
        var tree = SidebarRowPlanner.Build(Doc(Sec("t", SidebarSectionKind.PlaylistTree)), input);
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow, SidebarRowKind.TreeEnd},
            KindsOf(tree));
        Assert.Equal(0, tree.Rows[1].Depth);

        // Shortcuts and links are app destinations, not library rows — search never filters them.
        var shortcuts = SidebarRowPlanner.Build(Doc(Sec("c", SidebarSectionKind.CollectionShortcuts,
            items: [Route("i1", "liked"), Route("i2", "albums")])), input);
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.IconRow, SidebarRowKind.IconRow},
            KindsOf(shortcuts));
    }

    [Fact]
    public void BlankSearch_IsNotASearch()
    {
        var input = FullInput() with { Search = "   " };
        var plan = SidebarRowPlanner.Build(
            Doc(Sec("e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default)), input);
        Assert.Equal(3, plan.Entries.Count);
    }

    // ── query filtering + sorting ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EntityList_FiltersByKindAndQualifier()
    {
        var input = FullInput();

        var albums = SidebarRowPlanner.Build(Doc(Sec("e", SidebarSectionKind.EntityList,
            query: new SidebarEntityQuery(SidebarEntityKinds.Albums))), input);
        Assert.Single(albums.Entries);
        Assert.Equal(SidebarEntryKind.Album, albums.Entries[0].Kind);

        var byYou = SidebarRowPlanner.Build(Doc(Sec("e", SidebarSectionKind.EntityList,
            query: new SidebarEntityQuery(SidebarEntityKinds.Playlists, SidebarSortMode.Alphabetical,
                Descending: false, Qualifier: SidebarPlaylistQualifier.ByYou))), input);
        Assert.Single(byYou.Entries);
        Assert.Equal("Alpha mix", byYou.Entries[0].Name);
    }

    [Theory]
    [InlineData(SidebarSortMode.Recents)]
    [InlineData(SidebarSortMode.RecentlyAdded)]
    [InlineData(SidebarSortMode.Alphabetical)]
    [InlineData(SidebarSortMode.Creator)]
    [InlineData(SidebarSortMode.CustomOrder)]
    public void Pins_SortBeforeRest_InEveryEntityListSort(SidebarSortMode sort)
    {
        // "Zzz" sorts last alphabetically, is the oldest by every stamp, and is last in source order — it may ONLY be
        // first because it is pinned.
        var pinned = Playlist("z", "Zzz last", visited: 1, order: 99);
        var others = new[]
        {
            Playlist("a", "Aaa", visited: 900, order: 0),
            Playlist("b", "Bbb", visited: 800, order: 1),
        };
        var lib = new[] { others[0], others[1], pinned };

        var input = new SidebarProjectionInput
        {
            Library = lib,
            PinnedIds = new HashSet<string>(StringComparer.Ordinal) { pinned.Id },
        };

        var plan = SidebarRowPlanner.Build(Doc(Sec("e", SidebarSectionKind.EntityList,
            query: new SidebarEntityQuery(SidebarEntityKinds.Playlists, sort))), input);

        Assert.Equal(3, plan.Entries.Count);
        Assert.Equal(pinned.Id, plan.Entries[0].Id);
    }

    [Fact]
    public void PinnedFlag_OnTheEntryIsHonouredWhenNoExplicitSetIsGiven()
    {
        var pinned = Playlist("z", "Zzz last", visited: 1, order: 99, pinned: true);
        var input = new SidebarProjectionInput
        {
            Library = new[] { Playlist("a", "Aaa", visited: 900), pinned },
        };
        var plan = SidebarRowPlanner.Build(Doc(Sec("e", SidebarSectionKind.EntityList,
            query: SidebarEntityQuery.Default)), input);
        Assert.Equal(pinned.Id, plan.Entries[0].Id);
    }

    [Fact]
    public void Alphabetical_HonoursTheDescendingFlag()
    {
        var input = new SidebarProjectionInput
        {
            Library = new[] { Playlist("b", "Bravo", order: 0), Playlist("a", "Alpha", order: 1) },
        };

        var asc = SidebarRowPlanner.Build(Doc(Sec("e", SidebarSectionKind.EntityList,
            query: SidebarEntityQuery.PlaylistsAlphabetical)), input);
        Assert.Equal("Alpha", asc.Entries[0].Name);

        var desc = SidebarRowPlanner.Build(Doc(Sec("e", SidebarSectionKind.EntityList,
            query: new SidebarEntityQuery(SidebarEntityKinds.Playlists, SidebarSortMode.Alphabetical,
                Descending: true))), input);
        Assert.Equal("Bravo", desc.Entries[0].Name);
    }

    // ── the extended catalog ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EntityEmbed_EmitsACard_AndAMissingEntityStillCards()
    {
        var input = FullInput();

        var resolved = SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.EntityEmbed,
            items: [Entity("i1", "spotify:album:9", SidebarEntityKind.Album)])), input);
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.EntityCard}, KindsOf(resolved));
        Assert.Equal(0, resolved.Rows[1].EntryIndex);
        Assert.Equal("spotify:album:9", resolved.Rows[1].Key);
        Assert.Single(resolved.Entries);

        // The entity vanished (unfollowed elsewhere / offline cold cache): STILL a card, dimmed, from the fallback.
        var missing = SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.EntityEmbed,
            items: [Entity("i1", "spotify:album:gone", SidebarEntityKind.Album, fallbackTitle: "Old favourite")])),
            input);
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.EntityCard}, KindsOf(missing));
        Assert.Equal(-1, missing.Rows[1].EntryIndex);
        Assert.Equal("spotify:album:gone", missing.Rows[1].Key);
        Assert.Empty(missing.Entries);

        // A hidden or absent target is an Empty row (the "pick something to spotlight" state), never a crash.
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Empty},
            KindsOf(SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.EntityEmbed)), input)));
    }

    [Fact]
    public void MissingEntityItem_EmitsPlaceholder_AndIsNeverDropped()
    {
        var input = FullInput();
        var plan = SidebarRowPlanner.Build(Doc(Sec("g", SidebarSectionKind.CustomGroup, items:
        [
            Entity("i1", "spotify:playlist:1"),
            Entity("i2", "spotify:playlist:vanished", fallbackTitle: "Old mix"),
        ])), input);

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow, SidebarRowKind.Placeholder},
            KindsOf(plan));
        Assert.Equal("spotify:playlist:vanished", plan.Rows[2].Key);
        Assert.Equal(-1, plan.Rows[2].EntryIndex);
    }

    [Fact]
    public void TrackItem_EmitsARowKeyedByItsUri()
    {
        var plan = SidebarRowPlanner.Build(Doc(Sec("g", SidebarSectionKind.CustomGroup, items:
            [new SidebarItemSpec("i1", SidebarItemTarget.Track, "spotify:track:7", SidebarEntityKind.Track)])),
            FullInput());

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow}, KindsOf(plan));
        Assert.Equal("spotify:track:7", plan.Rows[1].Key);
        Assert.Equal(-1, plan.Rows[1].EntryIndex);
    }

    [Fact]
    public void JumpBackIn_SwitchesSourceWithItsRecentsOption()
    {
        var input = FullInput();

        var visited = SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.JumpBackIn)), input);
        Assert.Equal(2, visited.Entries.Count);
        Assert.Equal("pl:spotify:playlist:1", visited.Entries[0].Id);

        var played = SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.JumpBackIn,
            SidebarDisplayOptions.Entities with { Recents = SidebarRecentsSource.Played })), input);
        Assert.Single(played.Entries);
        Assert.Equal("album:spotify:album:9", played.Entries[0].Id);
    }

    [Fact]
    public void Concerts_LocationUnset_EmitsAPromptRow()
    {
        var input = FullInput() with { ConcertsLocationUnset = true };
        var plan = SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.Concerts)), input);

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.PromptRow}, KindsOf(plan));
        Assert.Equal("s", plan.Rows[1].Key);

        // No events (location known) is an Empty row; pending is a skeleton.
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Empty}, KindsOf(SidebarRowPlanner.Build(
            Doc(Sec("s", SidebarSectionKind.Concerts)), new SidebarProjectionInput())));
        Assert.Contains(SidebarRowKind.Skeleton, KindsOf(SidebarRowPlanner.Build(
            Doc(Sec("s", SidebarSectionKind.Concerts)),
            new SidebarProjectionInput { ConcertsState = SidebarSourceState.Pending })));
    }

    [Fact]
    public void NewReleases_EmptyEmitsEmptyRow_PendingEmitsSkeletons()
    {
        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Empty}, KindsOf(SidebarRowPlanner.Build(
            Doc(Sec("s", SidebarSectionKind.NewReleases)), new SidebarProjectionInput())));

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.Skeleton, SidebarRowKind.Skeleton,
            SidebarRowKind.Skeleton}, KindsOf(SidebarRowPlanner.Build(Doc(Sec("s", SidebarSectionKind.NewReleases)),
                new SidebarProjectionInput { NewReleasesState = SidebarSourceState.Pending })));
    }

    [Fact]
    public void CustomGroup_ItemsAndChildrenSitOneLevelIn()
    {
        var plan = SidebarRowPlanner.Build(Doc(Sec("g", SidebarSectionKind.CustomGroup,
            items: [Route("i1", "home")],
            children: [Sec("c", SidebarSectionKind.CollectionShortcuts, items: [Route("i2", "liked")])])),
            FullInput());

        Assert.Equal(new[] {SidebarRowKind.SectionHeader, SidebarRowKind.IconRow, SidebarRowKind.SectionHeader,
            SidebarRowKind.IconRow}, KindsOf(plan));
        Assert.Equal(new byte[] { 0, 1, 1, 1 }, DepthsOf(plan));
    }

    // ── determinism + the 10k budget ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_IsDeterministic()
    {
        var doc = SidebarTemplates.Build(SidebarTemplates.Curated);
        var input = FullInput();

        var a = SidebarRowPlanner.Build(doc, input);
        var rowsA = new List<SidebarRow>(a.Rows);
        var idsA = new List<string>();
        foreach (var e in a.Entries) idsA.Add(e.Id);

        var b = SidebarRowPlanner.Build(doc, input);
        Assert.Equal(rowsA, b.Rows);
        var idsB = new List<string>();
        foreach (var e in b.Entries) idsB.Add(e.Id);
        Assert.Equal(idsA, idsB);
        Assert.Equal(input.Revision, b.Revision);
    }

    [Fact]
    public void TenThousandEntries_PlansUnderRowCap_AndInBudget()
    {
        const int N = 10_000;
        var lib = new SidebarLibraryEntry[N];
        for (int i = 0; i < N; i++) lib[i] = Playlist(i.ToString(), "Mix " + i, visited: N - i, order: i);

        var input = new SidebarProjectionInput { Library = lib };
        var doc = Doc(Sec("e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default));
        var buffers = new SidebarPlanBuffers();

        var warm = SidebarRowPlanner.Build(doc, input, buffers);
        Assert.Equal(1 + N, warm.Rows.Count);                     // header + every entry, in ONE flat list
        Assert.Equal(N, warm.Entries.Count);
        Assert.True(N <= SidebarRowPlanner.DynamicSectionRowCap);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var plan = SidebarRowPlanner.Build(doc, input, buffers);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1 + N, plan.Rows.Count);
        // A warm re-plan reuses the caller's buffers, so it allocates no rows, no entries and NO STRINGS. The bound is a
        // generous smoke bound (§C8.5), not an engine zero-alloc gate.
        Assert.True(delta < 512 * 1024, "warm re-plan allocated " + delta + " bytes");

        // Every row's key is an EXISTING string — a concatenation would show up as an allocation above.
        Assert.Equal(lib[0].Id, plan.Rows[1].Key);
    }

    [Fact]
    public void SectionRowCaps_AreDeclaredAndOrdered()
    {
        Assert.Equal(40, SidebarRowPlanner.RailTileCap);
        Assert.Equal(5000, SidebarRowPlanner.SectionRowCap);
        Assert.True(SidebarRowPlanner.DynamicSectionRowCap >= 10_000);
    }

    [Fact]
    public void Build_WithoutBuffers_StillWorks()
    {
        var plan = SidebarRowPlanner.Build(SidebarTemplates.Build(SidebarTemplates.Curated), FullInput());
        Assert.NotEmpty(plan.Rows);
        Assert.Equal(7, plan.Revision);
    }

    [Fact]
    public void CuratedTemplate_PlansItsWholeIA()
    {
        var plan = SidebarRowPlanner.Build(SidebarTemplates.Build(SidebarTemplates.Curated), FullInput());

        // Pinned (2) / Jump back in via Played as a two-column media strip / shortcuts (5) /
        // tree (folder + 2 leaves + create), separated by the three authored quiet dividers.
        Assert.Equal(new[] {
            SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow, SidebarRowKind.EntityRow,
            SidebarRowKind.Divider,
            SidebarRowKind.SectionHeader, SidebarRowKind.GridStrip,
            SidebarRowKind.Divider,
            SidebarRowKind.SectionHeader, SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow,
            SidebarRowKind.IconRow, SidebarRowKind.IconRow,
            SidebarRowKind.Divider,
            SidebarRowKind.SectionHeader, SidebarRowKind.FolderHeader, SidebarRowKind.EntityRow,
            SidebarRowKind.EntityRow, SidebarRowKind.TreeEnd,
        }, KindsOf(plan));
    }

    // ── the FOLDER DISCLOSURE contract (the expand/collapse flicker) ──────────────────────────────────────────────────

    static readonly SidebarLibraryEntry[] DisclosureTree =
    [
        Folder("f1", "Chill", depth: 0, order: 0),
        Playlist("a", "Inner A", order: 1, depth: 1),
        Playlist("b", "Inner B", order: 2, depth: 1),
        Playlist("c", "Top level", order: 3, depth: 0),
    ];

    static SidebarRowPlan TreePlan(params string[] expanded)
        => SidebarRowPlanner.Build(Doc(Sec("t", SidebarSectionKind.PlaylistTree)),
            new SidebarProjectionInput
            {
                PlaylistTree = DisclosureTree,
                ExpandedFolders = new HashSet<string>(expanded, StringComparer.Ordinal),
            });

    [Fact]
    public void FolderDescendantRange_ResolvesOnTheFirstPlanAfterTheToggle()
    {
        // The whole disclosure choreography hangs off this: the pane must be able to arm the opening band from the
        // FIRST plan built after the toggle, or the expansion needs a second publish (and a second frame) to start.
        var collapsed = TreePlan();
        Assert.False(SidebarRowGeometry.TryFolderDescendantRange(
            collapsed.Rows, collapsed.Entries, "f1", out _, out _));

        var expanded = TreePlan("f1");
        Assert.True(SidebarRowGeometry.TryFolderDescendantRange(
            expanded.Rows, expanded.Entries, "f1", out int first, out int count));
        // header(0) folder(1) [a(2) b(3)] c(4) treeEnd(5) create(6)
        Assert.Equal(1, SidebarRowGeometry.FolderHeaderIndexOf(expanded.Rows, expanded.Entries, "f1"));
        Assert.Equal(2, first);
        Assert.Equal(2, count);
        Assert.Equal(SidebarRowKind.EntityRow, expanded.Rows[first].Kind);
        Assert.Equal(SidebarRowKind.EntityRow, expanded.Rows[first + count - 1].Kind);
    }

    [Fact]
    public void FolderToggle_ReplansTheTailWithoutReKeyingIt()
    {
        var collapsed = TreePlan();
        var expanded = TreePlan("f1");
        Assert.True(SidebarRowGeometry.TryFolderDescendantRange(
            expanded.Rows, expanded.Entries, "f1", out int first, out int count));

        // The rows below the folder are RE-PLANNED, never re-keyed: every tail key survives the toggle, merely shifted
        // down by the inserted band. That is what lets the reconciler move them instead of remounting them.
        for (int i = first; i < collapsed.Rows.Count; i++)
            Assert.Equal(collapsed.Rows[i].Key, expanded.Rows[i + count].Key);
        Assert.Equal(collapsed.Rows.Count + count, expanded.Rows.Count);

        // The folder HEADER's record is byte-identical across the toggle — its chevron state is not in the plan at all.
        // That is precisely why a disclosure edge must bump the header's row epoch explicitly (SidebarPane
        // .BumpDisclosureEpochs) instead of leaning on the plan diff, which cannot see it.
        Assert.Equal(collapsed.Rows[1], expanded.Rows[1]);
        var changed = new bool[expanded.Rows.Count];
        SidebarRowDiff.Diff(collapsed.Rows, collapsed.Entries, expanded.Rows, expanded.Entries, changed);
        Assert.False(changed[0]);          // the section header is untouched
        Assert.False(changed[1]);          // …and so is the folder header
        // Nothing ABOVE the insertion point changed; the insertion point and everything after it did (an index-keyed
        // diff cannot express "the tail merely shifted" — the epoch bump the pane does instead is scoped to the band).
        for (int i = first; i < expanded.Rows.Count; i++) Assert.True(changed[i]);
    }

    [Fact]
    public void FolderToggle_IsSymmetric()
    {
        var collapsed = TreePlan();
        var reCollapsed = TreePlan();
        Assert.Equal(KindsOf(collapsed), KindsOf(reCollapsed));
        for (int i = 0; i < collapsed.Rows.Count; i++)
            Assert.Equal(collapsed.Rows[i], reCollapsed.Rows[i]);
    }
}
