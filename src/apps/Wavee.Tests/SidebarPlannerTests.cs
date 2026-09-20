// ── Wavee.Tests/SidebarPlannerTests.cs — the 0.2.9 sidebar planner suite, ported to 0.3 ────────────────────────────
//
// Ported 2026-09-13 from `_old/Wavee.Tests/{SidebarRowPlannerTests, SidebarRailPlannerTests, SidebarRowGeometryTests,
// SidebarRowExtentsTests, SidebarRowDiffTests, SidebarEditPlanTests, SidebarPaneInvariantTests}.cs` (0.2.9's
// `Wavee.Core.Sidebar`). The types kept their 0.2.9 names and now live directly in namespace `Wavee`
// (`src/apps/Wavee/Shell/Sidebar.cs` + `Sidebar.Doc.cs`, 6.2k + 3.4k lines) — CORE, engine-free, pure. Every fact
// below is pinned against that production code, read section by section before this file was written.
//
// TWO 0.3 DELTAS every fixture in this file codes against:
//   1. `SidebarLibraryEntry.Cover` is now a `StringId` (was nullable) — a fixture that used to pass `Cover: null`
//      passes `Cover: default` (== `StringId.Empty`). `MosaicTiles` stays `IReadOnlyList<StringId>?`, still `null`.
//   2. The pane-width ladder (`NavPaneMinW`/`NavPaneMaxW`/`CompactRailW`, the tier ladder) moved off
//      `ShellResponsiveLayout` onto `SidebarPaneBounds`, declared right beside `Sidebar.cs`'s planner.
//
// These are pure-value tests: no `Entities` handle is ever constructed, so none of these classes boots a scope or
// joins `EntitiesCollection` (D17) — `SidebarLibraryEntry`, `SidebarRowPlan` and friends are POCOs/POD structs that
// never touch the entity graph.

using System;
using System.Collections.Generic;
using Wavee;
using Xunit;

namespace Wavee.Tests;

#region ROW PLANNER — SidebarRowPlannerTests
// The Curated pane's render contract (§C1.7 / §C8.5). Everything the renderer does is downstream of this plan, so the
// row SEQUENCE — not just the row count — is pinned per section kind, per degraded state, and at 10 000 entries.
public sealed class SidebarRowPlannerTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string uri, string name,
        string creator = "", long visited = 0, long sortStamp = 1, int sourceOrder = 0, int depth = 0,
        SidebarPlaylistFlavor flavor = SidebarPlaylistFlavor.None, bool pinned = false)
        => new(Id: id, Kind: kind, Uri: uri, Name: name, Creator: creator, Cover: default, MosaicTiles: null,
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

    /// <summary>Task G: the shortcuts/top-bar band (SidebarIds.TopBarSection — never collapsible chrome) emits NO
    /// SectionHeader row, whatever title it carries — unlike an ordinary section with the same shape (StaticLinks +
    /// TitleLocKey), which still gets one. Its item rows come first with nothing ahead of them, so the pane's first
    /// SectionHeader row (the quick layout menu host) lands on the NEXT real section instead.</summary>
    [Fact]
    public void TopBarBand_EmitsNoSectionHeader_AndItsRowsComeFirst()
    {
        var input = FullInput();
        var topBar = new SidebarSectionSpec(SidebarIds.TopBarSection, SidebarSectionKind.StaticLinks,
            Title: null, TitleLocKey: SidebarShortcutsSection.TitleLocKey, Hidden: false, Collapsed: false,
            Display: SidebarDisplayOptions.Links, Items: [Route("i1", "home"), Route("i2", "search")]);
        var next = Sec("real", SidebarSectionKind.Pinned);

        var plan = SidebarRowPlanner.Build(Doc(topBar, next), input);

        Assert.Equal(new[]
        {
            SidebarRowKind.IconRow, SidebarRowKind.IconRow,                              // the band, header-less
            SidebarRowKind.SectionHeader, SidebarRowKind.EntityRow, SidebarRowKind.EntityRow,  // the real section
        }, KindsOf(plan));
        Assert.Equal("real", plan.Rows[2].SectionId);   // the FIRST SectionHeader row now names the real section
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
        for (int i = 0; i < N; i++) lib[i] = Playlist(i.ToString(), "Mix " + i, order: i) with { LastPlayedMs = N - i };   // Recents = PLAYED: entry 0 is the newest play

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

        // Pinned (2) / Jump back in via Played as a two-column media strip / shortcuts (5, Audiobooks joined
        // beside Podcasts) / tree (folder + 2 leaves + create), separated by the three authored quiet dividers.
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

    // ── "once pinned, never also in the normal list" (every design, pane and rail) ──────────────────────────────────────

    static bool ContainsEntry(SidebarRowPlan plan, string sectionId, string entryId)
    {
        foreach (var r in plan.Rows)
            if (r.SectionId == sectionId && r.EntryIndex >= 0 &&
                string.Equals(plan.Entries[r.EntryIndex].Id, entryId, StringComparison.Ordinal))
                return true;
        return false;
    }

    [Fact]
    public void PinnedEntry_IsExcludedFromPlaylistTreeAndEntityList_WhenTheDocumentHasAPinnedSection()
    {
        var pinned = Playlist("1", "Alpha mix", order: 0);
        var other = Playlist("2", "Beta mix", order: 1);
        var input = new SidebarProjectionInput
        {
            Library = new[] { pinned, other },
            PlaylistTree = new[] { pinned, other },
            Pins = new[] { pinned },
            PinnedIds = new HashSet<string>(StringComparer.Ordinal) { pinned.Id },
        };

        var doc = Doc(
            Sec("p", SidebarSectionKind.Pinned),
            Sec("e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default),
            Sec("t", SidebarSectionKind.PlaylistTree));

        var plan = SidebarRowPlanner.Build(doc, input);

        Assert.True(ContainsEntry(plan, "p", pinned.Id));
        Assert.False(ContainsEntry(plan, "e", pinned.Id));
        Assert.False(ContainsEntry(plan, "t", pinned.Id));
        // The un-pinned entry is unaffected in either place.
        Assert.True(ContainsEntry(plan, "e", other.Id));
        Assert.True(ContainsEntry(plan, "t", other.Id));
    }

    [Fact]
    public void PinnedEntry_StillShowsInTheList_WhenTheDocumentHasNoPinnedSection()
    {
        // A Curated user who removed the Pinned section, or V3 while searching/drilled where the band is absent — the
        // item must not simply vanish from the sidebar.
        var pinned = Playlist("1", "Alpha mix", order: 0);
        var input = new SidebarProjectionInput
        {
            Library = new[] { pinned },
            PinnedIds = new HashSet<string>(StringComparer.Ordinal) { pinned.Id },
        };

        var plan = SidebarRowPlanner.Build(
            Doc(Sec("e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default)), input);

        Assert.Single(plan.Entries);
        Assert.Equal(pinned.Id, plan.Entries[0].Id);
    }

    [Fact]
    public void PinnedFolder_HidesItsWholeSubtree_InTheSourceOrderTreeWalker()
    {
        var folder = Folder("f1", "Chill", depth: 0, order: 0);
        var child = Playlist("a", "Inner A", order: 1, depth: 1);
        var top = Playlist("b", "Top level", order: 2, depth: 0);
        var input = new SidebarProjectionInput
        {
            PlaylistTree = new[] { folder, child, top },
            Pins = new[] { folder },
            PinnedIds = new HashSet<string>(StringComparer.Ordinal) { folder.Id },
            ExpandedFolders = new HashSet<string>(StringComparer.Ordinal) { "f1" },
        };
        var doc = Doc(Sec("p", SidebarSectionKind.Pinned), Sec("t", SidebarSectionKind.PlaylistTree));

        var plan = SidebarRowPlanner.Build(doc, input);

        Assert.DoesNotContain(plan.Rows, r => r.SectionId == "t" && r.Kind == SidebarRowKind.FolderHeader);
        Assert.False(ContainsEntry(plan, "t", child.Id));
        Assert.True(ContainsEntry(plan, "t", top.Id));
        // The folder pin itself still draws — the subtree is reachable through it.
        Assert.True(ContainsEntry(plan, "p", folder.Id));
    }

    [Fact]
    public void PinnedFolder_HidesItsWholeSubtree_InTheFlatSearchWalker()
    {
        var folder = Folder("f1", "Chill", depth: 0, order: 0);
        var child = Playlist("alpha-inner", "Alpha inner", order: 1, depth: 1);
        var top = Playlist("alpha-top", "Alpha top", order: 2, depth: 0);
        var input = new SidebarProjectionInput
        {
            PlaylistTree = new[] { folder, child, top },
            Pins = new[] { folder },
            PinnedIds = new HashSet<string>(StringComparer.Ordinal) { folder.Id },
            Search = "Alpha",
        };
        var doc = Doc(Sec("p", SidebarSectionKind.Pinned), Sec("t", SidebarSectionKind.PlaylistTree));

        var plan = SidebarRowPlanner.Build(doc, input);

        // "Alpha inner" matches the search but sits inside the pinned folder's subtree — hidden regardless. "Alpha top"
        // is outside it and still shows.
        Assert.False(ContainsEntry(plan, "t", child.Id));
        Assert.True(ContainsEntry(plan, "t", top.Id));
    }

    [Fact]
    public void PinnedRoute_HidesTheMatchingShortcutItem()
    {
        var input = new SidebarProjectionInput
        {
            PinnedIds = new HashSet<string>(StringComparer.Ordinal) { "liked" },
        };
        var doc = Doc(
            Sec("p", SidebarSectionKind.Pinned),
            Sec("c", SidebarSectionKind.CollectionShortcuts, items: [Route("i1", "liked"), Route("i2", "albums")]));

        var plan = SidebarRowPlanner.Build(doc, input);

        Assert.DoesNotContain(plan.Rows, r => r.SectionId == "c" && r.Key == "liked");
        Assert.Contains(plan.Rows, r => r.SectionId == "c" && r.Key == "albums");
    }

    [Fact]
    public void JumpBackIn_StillListsAPinnedPlaylist()
    {
        // Feeds (JumpBackIn/NewReleases/Concerts/…) are recency feeds, not the library list — PlanPinned's exclusion
        // rule never reaches them.
        var pinned = Playlist("1", "Alpha mix", visited: 500, order: 0);
        var input = new SidebarProjectionInput
        {
            Visited = new[] { pinned },
            Pins = new[] { pinned },
            PinnedIds = new HashSet<string>(StringComparer.Ordinal) { pinned.Id },
        };
        var doc = Doc(Sec("p", SidebarSectionKind.Pinned), Sec("j", SidebarSectionKind.JumpBackIn));

        var plan = SidebarRowPlanner.Build(doc, input);

        Assert.True(ContainsEntry(plan, "p", pinned.Id));
        Assert.True(ContainsEntry(plan, "j", pinned.Id));
    }

    [Fact]
    public void Rail_MirrorsThePane_PinnedEntriesExcludedFromTreeEntityListAndShortcuts()
    {
        var pinned = Playlist("1", "Alpha mix", order: 0);
        var other = Playlist("2", "Beta mix", order: 1);
        var input = new SidebarProjectionInput
        {
            Library = new[] { pinned, other },
            PlaylistTree = new[] { pinned, other },
            Pins = new[] { pinned },
            PinnedIds = new HashSet<string>(StringComparer.Ordinal) { pinned.Id },
        };
        var doc = Doc(
            Sec("p", SidebarSectionKind.Pinned),
            Sec("e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default),
            Sec("t", SidebarSectionKind.PlaylistTree));

        var rail = SidebarRowPlanner.BuildRail(doc, input);

        Assert.True(ContainsEntry(rail, "p", pinned.Id));
        Assert.False(ContainsEntry(rail, "e", pinned.Id));
        Assert.False(ContainsEntry(rail, "t", pinned.Id));
        Assert.True(ContainsEntry(rail, "e", other.Id));
        Assert.True(ContainsEntry(rail, "t", other.Id));

        // A pinned route's shortcut tile is likewise skipped in the rail.
        var liked = new SidebarProjectionInput { PinnedIds = new HashSet<string>(StringComparer.Ordinal) { "liked" } };
        var shortcutDoc = Doc(
            Sec("p", SidebarSectionKind.Pinned),
            Sec("c", SidebarSectionKind.CollectionShortcuts, items: [Route("i1", "liked"), Route("i2", "albums")]));
        var shortcutRail = SidebarRowPlanner.BuildRail(shortcutDoc, liked);
        Assert.DoesNotContain(shortcutRail.Rows, r => r.SectionId == "c" && r.Key == "liked");
        Assert.Contains(shortcutRail.Rows, r => r.SectionId == "c" && r.Key == "albums");
    }
}
#endregion

#region RAIL PLANNER — SidebarRailPlannerTests
// The mode-specific 56-DIP rail (§C5.2 / §C8.5). The rail is DERIVED, never authored: it is exactly the ShowInRail
// sections, reduced to tiles, capped, with headings collapsed into compact rules. A rail that silently disagrees with the
// expanded pane is the bug this class exists to prevent.
public sealed class SidebarRailPlannerTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string uri, string name,
        string creator = "", int sourceOrder = 0, int depth = 0)
        => new(Id: id, Kind: kind, Uri: uri, Name: name, Creator: creator, Cover: default, MosaicTiles: null,
            ChildCount: 0, AddedAtMs: 0, SortStamp: 100 + sourceOrder, LastVisitedTicksUtc: 1000 - sourceOrder,
            SourceOrder: sourceOrder, Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None);

    static SidebarLibraryEntry Playlist(string slug, string name, int order = 0, int depth = 0)
        => Entry("pl:spotify:playlist:" + slug, SidebarEntryKind.Playlist, "spotify:playlist:" + slug, name,
            "Owner", order, depth);

    static SidebarLibraryEntry Folder(string id, string name, int depth = 0, int order = 0)
        => Entry("folder:" + id, SidebarEntryKind.Folder, "", name, "", order, depth);

    static SidebarSectionSpec Sec(string id, SidebarSectionKind kind, SidebarDisplayOptions? display = null,
        IReadOnlyList<SidebarItemSpec>? items = null, SidebarEntityQuery? query = null,
        IReadOnlyList<SidebarSectionSpec>? children = null, bool hidden = false)
        => new(id, kind, null, "sidebar.section.header", hidden, false, display, items, query, children);

    static SidebarCustomLayout Doc(params SidebarSectionSpec[] sections)
        => new(SidebarTemplates.Curated, sections);

    static SidebarItemSpec Route(string id, string key) => new(id, SidebarItemTarget.Route, key);

    static SidebarItemSpec Entity(string id, string uri, SidebarEntityKind kind = SidebarEntityKind.Playlist)
        => new(id, SidebarItemTarget.Entity, uri, kind);

    static SidebarRowKind[] KindsOf(SidebarRowPlan plan)
    {
        var k = new SidebarRowKind[plan.Rows.Count];
        for (int i = 0; i < k.Length; i++) k[i] = plan.Rows[i].Kind;
        return k;
    }

    static int TileCount(SidebarRowPlan plan)
    {
        int n = 0;
        for (int i = 0; i < plan.Rows.Count; i++) if (plan.Rows[i].Kind != SidebarRowKind.Divider) n++;
        return n;
    }

    static SidebarLibraryEntry[] Playlists(int n, string prefix = "p")
    {
        var a = new SidebarLibraryEntry[n];
        for (int i = 0; i < n; i++) a[i] = Playlist(prefix + i, "Mix " + i, i);
        return a;
    }

    // ── ShowInRail is the whole contract ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyShowInRailSections_Contribute()
    {
        var input = new SidebarProjectionInput { Pins = Playlists(3, "pin"), Library = Playlists(4) };

        var plan = SidebarRowPlanner.BuildRail(Doc(
            Sec("p", SidebarSectionKind.Pinned, SidebarDisplayOptions.Entities with { ShowInRail = false }),
            Sec("e", SidebarSectionKind.EntityList, SidebarDisplayOptions.Entities with { ShowInRail = true },
                query: SidebarEntityQuery.Default)), input);

        Assert.Equal(4, TileCount(plan));
        foreach (var row in plan.Rows) Assert.Equal("e", row.SectionId);
    }

    [Fact]
    public void HiddenSection_ContributesNothing()
    {
        var input = new SidebarProjectionInput { Pins = Playlists(3, "pin") };
        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("p", SidebarSectionKind.Pinned, hidden: true)), input);
        Assert.Empty(plan.Rows);
    }

    [Fact]
    public void EmptyRail_WhenNoSectionShowsInRail()
    {
        var off = SidebarDisplayOptions.Entities with { ShowInRail = false };
        var input = new SidebarProjectionInput { Pins = Playlists(3, "pin"), Library = Playlists(4) };

        var plan = SidebarRowPlanner.BuildRail(Doc(
            Sec("p", SidebarSectionKind.Pinned, off),
            Sec("d", SidebarSectionKind.Divider, off),
            Sec("e", SidebarSectionKind.EntityList, off, query: SidebarEntityQuery.Default)), input);

        // A legal state: the rail then renders only the quick-menu tile, which is chrome, not a planned row.
        Assert.Empty(plan.Rows);
        Assert.Empty(plan.Entries);
    }

    // ── chrome ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HeaderAndDivider_BecomeCompactDividers_AndCollapse()
    {
        var input = new SidebarProjectionInput { Pins = Playlists(1, "pin") };

        var plan = SidebarRowPlanner.BuildRail(Doc(
            Sec("d0", SidebarSectionKind.Divider),
            Sec("h0", SidebarSectionKind.Header),
            Sec("p1", SidebarSectionKind.Pinned),
            Sec("h1", SidebarSectionKind.Header),
            Sec("d1", SidebarSectionKind.Divider),
            Sec("p2", SidebarSectionKind.Pinned),
            Sec("d2", SidebarSectionKind.Divider)), input);

        // Leading run dropped, the middle Header+Divider run collapses to ONE rule, the trailing divider is dropped.
        Assert.Equal(new[] { SidebarRowKind.EntityRow, SidebarRowKind.Divider, SidebarRowKind.EntityRow },
            KindsOf(plan));
    }

    [Fact]
    public void ADividerWithShowInRailOff_DrawsNoRule()
    {
        var input = new SidebarProjectionInput { Pins = Playlists(1, "pin") };
        var plan = SidebarRowPlanner.BuildRail(Doc(
            Sec("p1", SidebarSectionKind.Pinned),
            Sec("d", SidebarSectionKind.Divider, SidebarDisplayOptions.Entities with { ShowInRail = false }),
            Sec("p2", SidebarSectionKind.Pinned)), input);

        Assert.Equal(new[] { SidebarRowKind.EntityRow, SidebarRowKind.EntityRow }, KindsOf(plan));
    }

    // ── caps ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PinnedCapsAtEight_EntityListCapsAtTwenty_TotalCapsAtForty()
    {
        var pinnedOnly = SidebarRowPlanner.BuildRail(Doc(Sec("p", SidebarSectionKind.Pinned)),
            new SidebarProjectionInput { Pins = Playlists(20, "pin") });
        Assert.Equal(SidebarRowPlanner.RailPinnedCap, TileCount(pinnedOnly));

        var listOnly = SidebarRowPlanner.BuildRail(
            Doc(Sec("e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default)),
            new SidebarProjectionInput { Library = Playlists(50) });
        Assert.Equal(SidebarRowPlanner.RailEntityListCap, TileCount(listOnly));

        // 8 + 20 + 20 would be 48 tiles; the rail stops at 40.
        var everything = SidebarRowPlanner.BuildRail(Doc(
            Sec("p", SidebarSectionKind.Pinned),
            Sec("e1", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default),
            Sec("e2", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default),
            Sec("e3", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default)),
            new SidebarProjectionInput { Pins = Playlists(20, "pin"), Library = Playlists(50) });

        Assert.Equal(SidebarRowPlanner.RailTileCap, TileCount(everything));
        // Every tile still points at a real entry — the cap must not leave orphaned entries behind.
        foreach (var row in everything.Rows)
            if (row.Kind != SidebarRowKind.Divider && row.EntryIndex >= 0)
                Assert.InRange(row.EntryIndex, 0, everything.Entries.Count - 1);
    }

    [Fact]
    public void JumpBackInCapsAtFour_AndMaxItemsTightensTheCapFurther()
    {
        var input = new SidebarProjectionInput { Visited = Playlists(10, "v") };

        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("j", SidebarSectionKind.JumpBackIn)), input);
        Assert.Equal(SidebarRowPlanner.RailJumpBackInCap, TileCount(plan));

        var tighter = SidebarRowPlanner.BuildRail(Doc(Sec("j", SidebarSectionKind.JumpBackIn,
            SidebarDisplayOptions.Entities with { MaxItems = 2 })), input);
        Assert.Equal(2, TileCount(tighter));
    }

    // ── pinned folders (#102 follow-up) ─────────────────────────────────────────────────────────────────────────────
    //
    // A pinned FOLDER must tile the same way a PlaylistTree folder does (`PlaylistTree_ContributesArtTilesForLeaves-
    // AndFolderTilesForFolders` below): a FolderHeader row, never an EntityRow. `RailFrom` used to hand every pinned
    // entry an EntityRow unconditionally, which (a) resolved its art through the mosaic/cover lookup — a folder's
    // Cover is empty but its MosaicTiles carries a child's cover, so the tile drew that CHILD PLAYLIST's artwork
    // instead of a folder glyph — and (b) read `entry.RouteKey`, which is `null` for a folder, so the rail's click
    // handler stayed null too. One wrong SidebarRowKind, two visible defects.

    [Fact]
    public void PinnedFolder_ContributesAFolderHeaderTile_NotAnEntityRow()
    {
        var input = new SidebarProjectionInput
        {
            Pins = new[] { Playlist("keep", "Kept Mix"), Folder("nf", "New Folder", order: 1) },
        };

        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("p", SidebarSectionKind.Pinned)), input);

        Assert.Equal(new[] { SidebarRowKind.EntityRow, SidebarRowKind.FolderHeader }, KindsOf(plan));
        Assert.Equal(new[] { "pl:spotify:playlist:keep", "folder:nf" }, KeysOf(plan));
        // The folder tile still aliases the SAME entry the expanded pane's FolderHeader row reads (name/FolderId/
        // ChildCount) — the rail's `Tile()` folder arm needs exactly that, not a second projection.
        var folderRow = plan.Rows[1];
        Assert.Equal(SidebarEntryKind.Folder, plan.Entries[folderRow.EntryIndex].Kind);
    }

    [Fact]
    public void PinnedFolder_CountsAgainstTheSameEightTileCap_AsAnyOtherPin()
    {
        var pins = new List<SidebarLibraryEntry>(Playlists(7, "pin")) { Folder("nf", "New Folder", order: 7) };
        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("p", SidebarSectionKind.Pinned)),
            new SidebarProjectionInput { Pins = pins });

        Assert.Equal(SidebarRowPlanner.RailPinnedCap, TileCount(plan));
        Assert.Equal(SidebarRowKind.FolderHeader, plan.Rows[^1].Kind);   // the 8th pin, a folder, still gets its tile
    }

    // ── per-kind contributions ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CollectionShortcutsAndLinks_ContributeOneTilePerVisibleItem()
    {
        var hidden = new SidebarItemSpec("i3", SidebarItemTarget.Route, "history", Hidden: true);
        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("c", SidebarSectionKind.CollectionShortcuts,
            items: [Route("i1", "liked"), Route("i2", "albums"), hidden])), new SidebarProjectionInput());

        Assert.Equal(new[] { SidebarRowKind.IconRow, SidebarRowKind.IconRow }, KindsOf(plan));
        Assert.Equal("liked", plan.Rows[0].Key);
        Assert.Equal("albums", plan.Rows[1].Key);
    }

    [Fact]
    public void PlaceholderItems_AreSkipped()
    {
        var pl = Playlist("known", "Known");
        var input = new SidebarProjectionInput
        {
            ByUri = new Dictionary<string, SidebarLibraryEntry>(StringComparer.Ordinal)
            {
                ["spotify:playlist:known"] = pl,
            },
        };

        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("s", SidebarSectionKind.StaticLinks, items:
        [
            Route("i0", "home"),
            Entity("i1", "spotify:playlist:known"),
            Entity("i2", "spotify:playlist:vanished"),                                    // unresolved -> no tile
            new SidebarItemSpec("i3", SidebarItemTarget.Track, "spotify:track:1", SidebarEntityKind.Track),
        ])), input);

        // A route glyph tile + the one resolvable entity. No Placeholder row ever reaches the rail, and a track has no
        // tile (a text-less rail cannot label it).
        Assert.Equal(new[] { SidebarRowKind.IconRow, SidebarRowKind.EntityRow }, KindsOf(plan));
        Assert.DoesNotContain(SidebarRowKind.Placeholder, KindsOf(plan));
        Assert.Single(plan.Entries);
        Assert.Equal(pl.Id, plan.Entries[0].Id);
    }

    [Fact]
    public void CustomGroupChildren_AreFlattened()
    {
        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("g", SidebarSectionKind.CustomGroup,
            items: [Route("i1", "home")],
            children:
            [
                Sec("c1", SidebarSectionKind.CollectionShortcuts, items: [Route("i2", "liked"), Route("i3", "albums")]),
                Sec("c2", SidebarSectionKind.StaticLinks, SidebarDisplayOptions.Shortcuts with { ShowInRail = false },
                    items: [Route("i4", "settings")]),
                Sec("c3", SidebarSectionKind.Divider),
            ])), new SidebarProjectionInput());

        // The group's own item, then c1's two — flattened into one tile run. c2 opted out; a nested divider is noise.
        Assert.Equal(new[] { SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow }, KindsOf(plan));
        Assert.Equal(new[] { "home", "liked", "albums" }, KeysOf(plan));
    }

    /// <summary>The rail is TOP LEVEL ONLY. A 56-DIP strip has no indent lane and no disclosure, so a nested tile was
    /// indistinguishable from a top-level one; a folder's contents are reached through its tile's side flyout
    /// (<c>SidebarRailFolderFlyout</c>) instead, which is why nothing is lost by dropping them from the strip.</summary>
    [Fact]
    public void PlaylistTree_ContributesArtTilesForLeavesAndFolderTilesForFolders()
    {
        var input = new SidebarProjectionInput
        {
            PlaylistTree = new[]
            {
                Folder("f1", "Chill", 0, 0),
                Playlist("a", "Inner", 1, 1),
                Playlist("b", "Top", 2, 0),
            },
        };
        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("t", SidebarSectionKind.PlaylistTree)), input);

        Assert.Equal(new[] { SidebarRowKind.FolderHeader, SidebarRowKind.EntityRow }, KindsOf(plan));
        Assert.Equal(new[] { "folder:f1", "pl:spotify:playlist:b" }, KeysOf(plan));
    }

    [Fact]
    public void PlaylistTree_RailNeverTilesANestedEntry_AtAnyDepth()
    {
        var input = new SidebarProjectionInput
        {
            PlaylistTree =
            [
                Playlist("top", "Top", 0),
                Folder("g", "Chill", 0, 1),
                Playlist("b", "Nested", 2, 1),
                Folder("k", "Deep", 1, 3),
                Playlist("f", "Deeper", 4, 2),
                Playlist("tail", "Tail", 5),
            ],
        };

        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("t", SidebarSectionKind.PlaylistTree)), input);

        Assert.Equal(new[] { "pl:spotify:playlist:top", "folder:g", "pl:spotify:playlist:tail" }, KeysOf(plan));
        // Every entry the plan aliases is top level too — a filtered tile must not leak an orphan entry either.
        Assert.All(plan.Entries, e => Assert.Equal(0, e.Depth));
    }

    [Fact]
    public void PlaylistTree_QuerySortsSiblingTilesAndPrunesEmptyFolders()
    {
        var input = new SidebarProjectionInput
        {
            PlaylistTree =
            [
                Playlist("z-root", "Zulu root", 0),
                Folder("keep", "Keep", 0, 1),
                Playlist("z-child", "Zulu child", 2, 1),
                Playlist("a-child", "Alpha child", 3, 1),
                Folder("empty", "Empty", 0, 4),
                Playlist("hidden", "Hidden", 5, 1),
                Playlist("a-root", "Alpha root", 6),
            ],
        };
        var query = SidebarEntityQuery.PlaylistsAlphabetical with
        {
            ExcludeUris = ["spotify:playlist:hidden"],
        };

        var plan = SidebarRowPlanner.BuildRail(
            Doc(Sec("t", SidebarSectionKind.PlaylistTree, query: query)), input);

        // Top level only: the folder survives (its descendants still match, so it is not pruned) but its children are
        // reached through the folder flyout, not through tiles of their own.
        Assert.Equal(new[] { "pl:spotify:playlist:a-root", "folder:keep", "pl:spotify:playlist:z-root" },
            KeysOf(plan));
        Assert.DoesNotContain(plan.Rows, row => row.Key == "folder:empty");
    }

    [Fact]
    public void PlaylistTree_GridRailFlattensAndSortsAllLeaves()
    {
        var display = SidebarDisplayOptions.Entities with { Presentation = SidebarPresentation.Grid };
        var input = new SidebarProjectionInput
        {
            PlaylistTree =
            [
                Folder("f", "Folder"),
                Playlist("c", "Charlie", 1, 1),
                Playlist("a", "Alpha", 2, 1),
                Playlist("b", "Bravo", 3),
            ],
        };

        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("t", SidebarSectionKind.PlaylistTree,
            display, query: SidebarEntityQuery.PlaylistsAlphabetical)), input);

        Assert.Equal(new[]
        {
            "pl:spotify:playlist:a", "pl:spotify:playlist:b", "pl:spotify:playlist:c",
        }, KeysOf(plan));
        Assert.All(plan.Rows, row => Assert.Equal(SidebarRowKind.EntityRow, row.Kind));
    }

    [Fact]
    public void EntityEmbed_ContributesItsCover_AndNothingWhenUnresolved()
    {
        var al = Entry("album:spotify:album:9", SidebarEntryKind.Album, "spotify:album:9", "Ceremony", "Artist");
        var input = new SidebarProjectionInput
        {
            ByUri = new Dictionary<string, SidebarLibraryEntry>(StringComparer.Ordinal)
            {
                ["spotify:album:9"] = al,
            },
        };

        var resolved = SidebarRowPlanner.BuildRail(Doc(Sec("s", SidebarSectionKind.EntityEmbed,
            items: [Entity("i1", "spotify:album:9", SidebarEntityKind.Album)])), input);
        Assert.Equal(new[] { SidebarRowKind.EntityRow }, KindsOf(resolved));
        Assert.Equal("spotify:album:9", resolved.Rows[0].Key);

        var missing = SidebarRowPlanner.BuildRail(Doc(Sec("s", SidebarSectionKind.EntityEmbed,
            items: [Entity("i1", "spotify:album:gone", SidebarEntityKind.Album)])), input);
        Assert.Empty(missing.Rows);
    }

    [Fact]
    public void Concerts_ContributeOneGlyphTile()
    {
        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("c", SidebarSectionKind.Concerts)),
            new SidebarProjectionInput());
        Assert.Equal(new[] { SidebarRowKind.IconRow }, KindsOf(plan));
        Assert.Equal("c", plan.Rows[0].Key);
    }

    [Fact]
    public void NewReleases_NeverContributesATile()
    {
        var input = new SidebarProjectionInput { NewReleases = Playlists(4, "n") };

        // Even with ShowInRail explicitly ON in a hand-edited document: a releases FEED has no meaningful single tile.
        var plan = SidebarRowPlanner.BuildRail(Doc(Sec("n", SidebarSectionKind.NewReleases,
            SidebarDisplayOptions.Entities with { ShowInRail = true })), input);
        Assert.Empty(plan.Rows);
    }

    [Fact]
    public void UnknownSectionKind_ContributesNothing()
    {
        var plan = SidebarRowPlanner.BuildRail(
            Doc(new SidebarSectionSpec("s", (SidebarSectionKind)200)), new SidebarProjectionInput());
        Assert.Empty(plan.Rows);
    }

    // ── the shipped default ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CuratedTemplate_RailComposition()
    {
        var pl1 = Playlist("1", "Alpha", 0);
        var al1 = Entry("album:spotify:album:9", SidebarEntryKind.Album, "spotify:album:9", "Ceremony", "Artist", 1);

        var input = new SidebarProjectionInput
        {
            Pins = new[] { pl1, al1 },
            Played = new[] { al1 },
            PlaylistTree = new[] { Folder("f1", "Chill"), Playlist("2", "Inner", 1, 1), Playlist("3", "Top", 2) },
        };

        var plan = SidebarRowPlanner.BuildRail(SidebarTemplates.Build(SidebarTemplates.Curated), input);

        // 2 pin tiles · rule · 5 shortcut glyphs (Audiobooks, A2 plan §3.6, joined beside Podcasts) · rule · folder +
        // its ONE top-level sibling ("Inner" sits inside the folder, and the rail is top level only). Jump back in
        // ships ShowInRail:false, and its two flanking dividers collapse into the single quiet rule before the
        // shortcuts.
        Assert.Equal(new[]
        {
            SidebarRowKind.EntityRow, SidebarRowKind.EntityRow,
            SidebarRowKind.Divider,
            SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow,
            SidebarRowKind.IconRow,
            SidebarRowKind.Divider,
            SidebarRowKind.FolderHeader, SidebarRowKind.EntityRow,
        }, KindsOf(plan));

        Assert.Equal(new[] { "liked", "albums", "artists", "podcasts", "audiobooks" },
            new[] { plan.Rows[3].Key, plan.Rows[4].Key, plan.Rows[5].Key, plan.Rows[6].Key, plan.Rows[7].Key });
    }

    [Fact]
    public void Rail_IsDeterministic_AndReusesBuffers()
    {
        var doc = SidebarTemplates.Build(SidebarTemplates.Curated);
        var input = new SidebarProjectionInput
        {
            Pins = Playlists(3, "pin"),
            PlaylistTree = Playlists(5),
            Revision = 11,
        };
        var buffers = new SidebarPlanBuffers();

        var a = SidebarRowPlanner.BuildRail(doc, input, buffers);
        var rowsA = new List<SidebarRow>(a.Rows);
        Assert.Equal(11, a.Revision);

        var b = SidebarRowPlanner.BuildRail(doc, input, buffers);
        Assert.Equal(rowsA, b.Rows);
    }

    static string[] KeysOf(SidebarRowPlan plan)
    {
        var k = new string[plan.Rows.Count];
        for (int i = 0; i < k.Length; i++) k[i] = plan.Rows[i].Key;
        return k;
    }
}
#endregion

#region ROW GEOMETRY — SidebarRowGeometryTests
// SidebarRowGeometry is the engine-free half of the sidebar's row ladder. Two things are under test:
//
//   1. HEIGHT PARITY between the two documents that render the SAME "Your Library" section — Classic's locked built-in
//      document and the Wavee Curated seed template. Both must reach 40 (Task C: Cozy without a subtitle a glyph row
//      never paints) through the ONE ladder. This is the regression the user's screenshots showed (Classic's rows
//      visibly roomier than Curated's), and the reason it is worth a test is that the two section lists are authored
//      in different assemblies by different code paths, so nothing else stops them drifting. NOTE what this canNOT
//      catch: a document already PERSISTED to sidebar-layout.json carries its own density and is never retro-fitted
//      by a template edit — templates seed documents, they do not update them.
//
//   2. The pure plan geometry the selection cue needs: cumulative content-space Y, route→index lookup, and the travel
//      direction (whose 0 case — "unknowable" — is a real answer the indicator depends on, not an error).
public sealed class SidebarRowGeometryTests
{
    // ── 0. THE TREE-CONTENT ORIGIN (the caret's x) ────────────────────────────────────────────────
    //
    // A tree row is NOT laid out on `IndentFor(depth)`. A tree row pads once at IndentFor(0) and then spends real
    // cells — the 3-DIP selection gutter and one 12-DIP connector cell per level — before the art, with no reserved
    // disclosure cell any more (the folder's chevron lives in the row's TRAILING cluster, so a tree row's content
    // starts exactly where a depth-0 standard-leading row's does).

    [Theory]
    [InlineData(0, 13f)]     // 4 padding + 3 gutter + 6 gap — identical to StandardLeading at depth 0
    [InlineData(1, 25f)]
    [InlineData(2, 37f)]
    [InlineData(3, 49f)]
    [InlineData(4, 61f)]
    [InlineData(9, 61f)]     // past MaxIndentDepth the ladder stops marching right, exactly like IndentFor
    [InlineData(-3, 13f)]
    public void TreeContentX_IsTheSumOfTheRowsOwnLeadingCells(int depth, float expected)
    {
        Assert.Equal(expected, SidebarRowGeometry.TreeContentX(depth), 3);
        // …and it IS a sum of the named constants, not a literal that happens to match.
        int clamped = Math.Clamp(depth, 0, SidebarRowGeometry.MaxIndentDepth);
        Assert.Equal(SidebarRowGeometry.IndentFor(0) + SidebarRowGeometry.LeadingLaneWidth
                     + clamped * SidebarRowGeometry.TreeGuideStep,
                     SidebarRowGeometry.TreeContentX(depth), 3);
    }

    [Fact]
    public void TreeContentX_MarchesOneWholeConnectorCellPerLevel()
    {
        // The step the depth pick reads backwards. If these two ever differ, an outdent lands on the wrong level.
        for (int d = 0; d < SidebarRowGeometry.MaxIndentDepth; d++)
            Assert.Equal(SidebarRowGeometry.TreeGuideStep,
                         SidebarRowGeometry.TreeContentX(d + 1) - SidebarRowGeometry.TreeContentX(d), 3);
        Assert.Equal(SidebarRowGeometry.IndentStep, SidebarRowGeometry.TreeGuideStep);
    }

    // ── 0b. THE ONE LEADING LANE (the art/glyph column's x, and the label that follows it) ──────────────────────────────
    //
    // One art column, one label x, for every row SHAPE (art / bare glyph / tree) at a given density, and for the fixed
    // chrome bands mounted above the list too.

    [Fact]
    public void ArtX_AtDepthZero_Is21_TheContentLanePlusTheLeadingLane()
    {
        Assert.Equal(21f, SidebarRowGeometry.ArtX(0));
        Assert.Equal(SidebarRowGeometry.ContentLane + SidebarRowGeometry.LeadingLaneWidth, SidebarRowGeometry.ArtX(0));
    }

    [Theory]
    [InlineData(SidebarDensity.Compact, 20f)]
    [InlineData(SidebarDensity.Cozy, 32f)]
    [InlineData(SidebarDensity.Comfortable, 40f)]
    public void ArtFor_IsTheThreeCanonicalArtSizes(SidebarDensity density, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.ArtFor(density));

    [Theory]
    [InlineData(SidebarDensity.Compact, 47f)]     // 21 + 20 + 6
    [InlineData(SidebarDensity.Cozy, 59f)]        // 21 + 32 + 6
    [InlineData(SidebarDensity.Comfortable, 67f)] // 21 + 40 + 6
    public void GlyphRowAndArtRow_AtOneDensity_ShareOneLabelX(SidebarDensity density, float expectedLabelX)
    {
        // Both the bare-glyph arm (the icon centres inside the leading column) and the art arm build an
        // ArtFor(density)-wide leading column, with the SAME LeadingGap before the text either way. One formula, one
        // label x, whether the row shows a glyph (Home, Liked) or cover art (a playlist) at the same density.
        float labelX = SidebarRowGeometry.ArtX(0) + SidebarRowGeometry.ArtFor(density) + SidebarRowGeometry.LeadingGap;
        Assert.Equal(expectedLabelX, labelX);
    }

    // ── 1. the height ladder ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarDensity.Compact, false, 32f)]
    [InlineData(SidebarDensity.Compact, true, 32f)]     // Compact suppresses subtitles outright — no second line, no growth
    [InlineData(SidebarDensity.Cozy, false, 40f)]
    [InlineData(SidebarDensity.Cozy, true, 44f)]        // = Classic's entity row (a glyph/shortcut row is Cozy+NO subtitle — 40, Task C)
    [InlineData(SidebarDensity.Comfortable, false, 44f)]// also 44 — but a 40-DIP art column, no longer used for Shortcuts/Links
    [InlineData(SidebarDensity.Comfortable, true, 48f)]
    public void HeightFor_IsTheThreeCanonicalHeightsPlusComfortable(SidebarDensity d, bool sub, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.HeightFor(d, sub));

    [Fact]
    public void ClassicHeight_IsTheCozyWithSubtitleHeight()
        => Assert.Equal(SidebarRowGeometry.ClassicHeight, SidebarRowGeometry.HeightFor(SidebarDensity.Cozy, true));

    [Theory]
    [InlineData(-1, 4f)]
    [InlineData(0, 4f)]
    [InlineData(1, 16f)]
    [InlineData(4, 52f)]
    [InlineData(9, 52f)]   // clamped at four levels
    public void IndentFor_IsFourPlusTwelvePerLevelClampedAtFour(int depth, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.IndentFor(depth));

    // ── 2. Classic ⇄ Curated shortcut-row parity (the reported defect) ────────────────────────────────────────────────

    static SidebarSectionSpec Shortcuts(SidebarCustomLayout layout)
    {
        foreach (var s in layout.Sections)
            if (s.Kind == SidebarSectionKind.CollectionShortcuts) return s;
        throw new InvalidOperationException("no CollectionShortcuts section");
    }

    [Fact]
    public void ClassicAndCuratedTemplate_ShortcutRowsAreTheSameHeight()
    {
        var classic = Shortcuts(SidebarBuiltInDocuments.Classic(true, true, true));
        var curated = Shortcuts(SidebarTemplates.Build(SidebarTemplates.Curated));

        Assert.Equal(SidebarRowGeometry.HeightFor(classic.Opts), SidebarRowGeometry.HeightFor(curated.Opts));
        // …and the number itself, so a future "let's make Curated cozier" edit fails HERE instead of in a screenshot.
        Assert.Equal(40f, SidebarRowGeometry.HeightFor(curated.Opts));
    }

    [Fact]
    public void ClassicAndCuratedTemplate_ShortcutRowsShareTheWholeGeometryInput()
    {
        var classic = Shortcuts(SidebarBuiltInDocuments.Classic(true, true, true)).Opts;
        var curated = Shortcuts(SidebarTemplates.Build(SidebarTemplates.Curated)).Opts;

        // Height is Density × Subtitles; the art/glyph shape is Artwork. All three must match or the rows differ in a way
        // the height assertion alone would miss (a 44-DIP row with 40-DIP artwork is not a 44-DIP glyph row).
        Assert.Equal(classic.Density, curated.Density);
        Assert.Equal(classic.Subtitles, curated.Subtitles);
        Assert.Equal(classic.Artwork, curated.Artwork);
    }

    [Fact]
    public void ClassicInspiredTemplate_AlsoMatchesClassicsShortcutHeight()
    {
        // The "Classic-inspired" template exists to reproduce Classic inside an EDITABLE document; if it drifts, a user
        // who picks it gets rows that are not the Classic rows it is named after.
        var classic = Shortcuts(SidebarBuiltInDocuments.Classic(true, true, true));
        var inspired = Shortcuts(SidebarTemplates.Build(SidebarTemplates.ClassicInspired));
        Assert.Equal(SidebarRowGeometry.HeightFor(classic.Opts), SidebarRowGeometry.HeightFor(inspired.Opts));
    }

    // ── 3. pure plan geometry ────────────────────────────────────────────────────────────────────────────────────────

    static readonly float[] MixedExtents = [30f, 44f, 44f, 16f, 30f, 48f, 48f];   // header · 2 rows · divider · header · 2 rows

    static Func<int, float> Extents(float[] e) => i => (uint)i < (uint)e.Length ? e[i] : 0f;

    [Fact]
    public void ContentYOf_IsThePrefixSumOfEveryEarlierRow()
    {
        var extentOf = Extents(MixedExtents);
        int n = MixedExtents.Length;
        Assert.Equal(0f, SidebarRowGeometry.ContentYOf(0, n, extentOf));
        Assert.Equal(30f, SidebarRowGeometry.ContentYOf(1, n, extentOf));
        Assert.Equal(74f, SidebarRowGeometry.ContentYOf(2, n, extentOf));
        Assert.Equal(118f, SidebarRowGeometry.ContentYOf(3, n, extentOf));
        Assert.Equal(134f, SidebarRowGeometry.ContentYOf(4, n, extentOf));
        Assert.Equal(164f, SidebarRowGeometry.ContentYOf(5, n, extentOf));
        Assert.Equal(212f, SidebarRowGeometry.ContentYOf(6, n, extentOf));
    }

    [Fact]
    public void ContentYOf_ClampsBothEnds()
    {
        var extentOf = Extents(MixedExtents);
        int n = MixedExtents.Length;
        float total = 260f;   // the whole MixedExtents sum
        Assert.Equal(0f, SidebarRowGeometry.ContentYOf(-5, n, extentOf));
        Assert.Equal(total, SidebarRowGeometry.ContentYOf(n, n, extentOf));
        Assert.Equal(total, SidebarRowGeometry.ContentYOf(n + 99, n, extentOf));
    }

    [Fact]
    public void ContentYOf_SkipsDegenerateExtents()
    {
        // A zero-height row (the pane's Blank) and a NaN (an unmeasured slot) must contribute nothing rather than
        // poisoning every later offset — one NaN would otherwise make the whole column NaN.
        float[] e = [44f, 0f, float.NaN, 44f];
        Assert.Equal(88f, SidebarRowGeometry.ContentYOf(4, e.Length, Extents(e)));
    }

    [Fact]
    public void IndexOfRoute_FindsTheFirstMatchAndIgnoresNonTargets()
    {
        string?[] routes = [null, "albums", "", "liked", "albums"];
        Assert.Equal(1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], "albums"));
        Assert.Equal(3, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], "liked"));
        Assert.Equal(-1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], "podcasts"));
        Assert.Equal(-1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], ""));
        Assert.Equal(-1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], null));
        // Ordinal, never culture- or case-insensitive: route keys are identifiers.
        Assert.Equal(-1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], "Albums"));
    }

    [Theory]
    [InlineData(1, 5, 1)]     // moved down the plan
    [InlineData(5, 1, -1)]    // moved up
    [InlineData(3, 3, 0)]     // same row — nothing travelled
    [InlineData(-1, 4, 0)]    // arriving from off-plan (deep link / collapsed section): direction is unknowable
    [InlineData(4, -1, 0)]    // leaving to off-plan
    [InlineData(-1, -1, 0)]
    public void DirectionOf_IsSignedOnlyWhenBothRowsAreOnThePlan(int from, int to, int expected)
        => Assert.Equal(expected, SidebarRowGeometry.DirectionOf(from, to));

    // ── grid strip fallback (issue #84) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GridFallbackColumns_HonoursThePlannerAtAWidePane()
    {
        // 240 available, 3 planner columns, 8-DIP gap: edge = (240 - 16) / 3 = 74.67 — comfortably above the 40 floor.
        Assert.Equal(3, SidebarRowGeometry.GridFallbackColumns(3, 240f, 8f, 40f));
    }

    [Fact]
    public void GridFallbackColumns_DropsToFewerColumnsRatherThanShrinkingCellsBelowTheFloor()
    {
        // The narrow-pane case: at the 180-DIP floor (164 DIP available after the 16-DIP pane inset), 4 planner
        // columns would give (164 - 24) / 4 = 35 — under the 40-DIP floor — so the strip falls back to 3:
        // (164 - 16) / 3 ≈ 49.3, which clears it.
        Assert.Equal(3, SidebarRowGeometry.GridFallbackColumns(4, 164f, 8f, 40f));
        // A 2-column section never needed the fallback in the first place — the planner's own ceiling already fits.
        Assert.Equal(2, SidebarRowGeometry.GridFallbackColumns(2, 164f, 8f, 40f));
    }

    [Fact]
    public void GridFallbackColumns_NeverExceedsThePlannersColumnCount()
    {
        // A very wide pane must not invent MORE columns than the planner asked for — the planner is the ceiling.
        Assert.Equal(4, SidebarRowGeometry.GridFallbackColumns(4, 2000f, 8f, 40f));
    }

    [Fact]
    public void GridFallbackColumns_FloorsAtOneColumn()
    {
        // Even one column's edge can go under the floor at an extreme width — the fallback never returns less than 1
        // (the row degrades to the narrowest possible strip rather than throwing or returning zero).
        Assert.Equal(1, SidebarRowGeometry.GridFallbackColumns(4, 10f, 8f, 40f));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-3)]
    public void GridFallbackColumns_TreatsANonPositivePlannedCountAsOne(int planned)
        => Assert.Equal(1, SidebarRowGeometry.GridFallbackColumns(planned, 2000f, 8f, 40f));

    // ── pin glyph (issue #85, H1) ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false, true)]    // a pinned, non-track entry draws the glyph
    [InlineData(false, false, false)]  // not pinned: nothing to show
    [InlineData(true, true, false)]    // a track is never pinnable (locked decision) even if IsPinned is somehow set
    [InlineData(false, true, false)]
    public void ShowsPinGlyph_IsPinnedAndNeverATrack(bool isPinned, bool isTrack, bool expected)
        => Assert.Equal(expected, SidebarRowGeometry.ShowsPinGlyph(isPinned, isTrack));
}
#endregion

#region ROW EXTENTS — SidebarRowExtentsTests
// THE ANALYTIC ROW LADDER (SidebarRowExtents). The pane feeds these numbers to the virtualizing host as its per-row
// extent SEED, so they are what the sidebar's content extent, scroll anchor and drop placement are computed from before
// anything realizes. A drift between this ladder and what the renderer actually builds is exactly the class of bug that
// made a folder expansion shuffle the rows around it, so every term is pinned here against the renderer's own constants
// rather than against a copied literal.
public sealed class SidebarRowExtentsTests
{
    static SidebarSectionSpec Sec(string id, SidebarSectionKind kind, SidebarDisplayOptions? display = null,
                                  bool collapsed = false, IReadOnlyList<SidebarItemSpec>? items = null)
        => new(id, kind, null, "sidebar.section.header", false, collapsed, display, items, null, null);

    static SidebarRow Row(SidebarRowKind kind, string sectionId = "s", byte depth = 0, int entry = -1)
        => new(kind, sectionId, depth, entry, 0, sectionId);

    static float H(IReadOnlyList<SidebarRow> rows, int i, SidebarSectionSpec section, bool editable = false)
        => SidebarRowExtents.HeightOf(rows, i, section, editable);

    [Fact]
    public void ItemRows_AreTheSectionsOneUniformHeight()
    {
        // The ladder is per SECTION, never per row (iron rule 4): Cozy+subtitles = 44 = Classic's row.
        var cozy = Sec("s", SidebarSectionKind.EntityList,
            SidebarDisplayOptions.Default with { Density = SidebarDensity.Cozy, Subtitles = true });
        var compact = Sec("s", SidebarSectionKind.EntityList,
            SidebarDisplayOptions.Default with { Density = SidebarDensity.Compact, Subtitles = true });
        var comfy = Sec("s", SidebarSectionKind.EntityList,
            SidebarDisplayOptions.Default with { Density = SidebarDensity.Comfortable, Subtitles = true });

        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.EntityRow), Row(SidebarRowKind.IconRow), Row(SidebarRowKind.Placeholder),
            Row(SidebarRowKind.FolderHeader), Row(SidebarRowKind.Skeleton),
        };
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(44f, H(rows, i, cozy));
            Assert.Equal(32f, H(rows, i, compact));
            Assert.Equal(48f, H(rows, i, comfy));
        }
        Assert.Equal(SidebarRowGeometry.ClassicHeight, H(rows, 0, cozy));
    }

    [Fact]
    public void ShortcutSection_IsCozyWithoutSubtitle_40()
    {
        // Task C: SidebarDisplayOptions.Shortcuts/Links declare Subtitles:false — neither CollectionShortcuts nor
        // StaticLinks ever paints a subtitle line, so the honest row is Cozy-without-subtitle (40), not the 44 the
        // preset used to claim by spelling Subtitles:true for a line it never drew.
        var shortcuts = Sec("s", SidebarSectionKind.CollectionShortcuts, SidebarDisplayOptions.Shortcuts);
        var links = Sec("s", SidebarSectionKind.StaticLinks, SidebarDisplayOptions.Links);
        var rows = new List<SidebarRow> { Row(SidebarRowKind.IconRow) };

        Assert.Equal(40f, H(rows, 0, shortcuts));
        Assert.Equal(40f, H(rows, 0, links));
    }

    [Fact]
    public void ChromeRows_MatchTheRenderersOwnConstants()
    {
        var section = Sec("s", SidebarSectionKind.PlaylistTree);
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.Divider), Row(SidebarRowKind.TreeEnd), Row(SidebarRowKind.SectionCard),
            Row(SidebarRowKind.EntityCard), Row(SidebarRowKind.GridStrip),
        };
        Assert.Equal(SidebarRowGeometry.DividerHeight, H(rows, 0, section));
        Assert.Equal(SidebarRowGeometry.TreeEndHeight, H(rows, 1, section));
        Assert.Equal(SidebarRowGeometry.ClassicHeight, H(rows, 2, section));
        Assert.Equal(SidebarRowGeometry.CardHeightFor(section.Opts.Density), H(rows, 3, section));
        // A GridStrip's cells wrap artwork + text at font metrics this layer cannot see: NOT analytic, by contract.
        Assert.True(float.IsNaN(H(rows, 4, section)));
        // A row whose section is gone renders nothing, so it occupies nothing (never the 44-DIP estimate).
        Assert.Equal(0f, SidebarRowExtents.HeightOf(rows, 0, null, editable: false));
    }

    [Fact]
    public void EmptyRow_FollowsTheSectionsEmptyBehavior()
    {
        var rows = new List<SidebarRow> { Row(SidebarRowKind.Empty) };
        // Pinned's empty state IS its drop zone, and it is unconditional (R3.1.5).
        Assert.Equal(SidebarRowGeometry.PinDropZoneRestHeight,
            H(rows, 0, Sec("p", SidebarSectionKind.Pinned)));
        // A quiet feed hint is the 32-DIP band, not a full row.
        Assert.Equal(SidebarRowGeometry.EmptyHintHeight,
            H(rows, 0, Sec("s", SidebarSectionKind.EntityList,
                SidebarDisplayOptions.Default with { EmptyBehavior = SidebarEmptyBehavior.CompactHint })));
        // HideBody draws nothing at all.
        Assert.Equal(0f,
            H(rows, 0, Sec("s", SidebarSectionKind.EntityList,
                SidebarDisplayOptions.Default with { EmptyBehavior = SidebarEmptyBehavior.HideBody })));
    }

    [Fact]
    public void BandedRhythm_IsEightExceptFirstRowAndAfterADividerOrHeading()
    {
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.SectionHeader, "a"),     // 0 - the pane's first row
            Row(SidebarRowKind.EntityRow, "a"),
            Row(SidebarRowKind.SectionHeader, "b"),     // 2 - after a row: full air
            Row(SidebarRowKind.Divider, "d"),
            Row(SidebarRowKind.SectionHeader, "c"),     // 4 - after a divider: none
            Row(SidebarRowKind.HeaderLabel, "e"),
            Row(SidebarRowKind.SectionHeader, "f"),     // 6 - after a bare heading: none
        };
        Assert.Equal(0f, SidebarRowExtents.BandTop(rows, 0));
        Assert.Equal(SidebarRowGeometry.SectionGap, SidebarRowExtents.BandTop(rows, 2));
        Assert.Equal(0f, SidebarRowExtents.BandTop(rows, 4));
        Assert.Equal(0f, SidebarRowExtents.BandTop(rows, 6));

        var section = Sec("a", SidebarSectionKind.EntityList);
        float bare = SidebarRowGeometry.HeaderHeight + SidebarRowGeometry.HeaderBodyGap;
        Assert.Equal(bare, H(rows, 0, section));
        Assert.Equal(bare + SidebarRowGeometry.SectionGap, H(rows, 2, section));
        Assert.Equal(bare, H(rows, 4, section));
        Assert.Equal(bare + SidebarRowGeometry.SectionGap, H(rows, 5, section));   // the heading itself follows a header
    }

    [Fact]
    public void InlineChipStrip_OnlyOnAnEditableOpenEntityListThatAsksForIt()
    {
        var rows = new List<SidebarRow> { Row(SidebarRowKind.SectionHeader) };
        float bare = SidebarRowGeometry.HeaderHeight + SidebarRowGeometry.HeaderBodyGap;
        float withChips = bare + SidebarRowGeometry.ChipStripGap + SidebarRowGeometry.ChipStripHeight;
        var chips = SidebarDisplayOptions.Default with { InlineControls = true };

        Assert.Equal(withChips, H(rows, 0, Sec("s", SidebarSectionKind.EntityList, chips), editable: true));
        // A READ-ONLY pane (Classic, Library V3) never renders them...
        Assert.Equal(bare, H(rows, 0, Sec("s", SidebarSectionKind.EntityList, chips), editable: false));
        // ...nor does a collapsed section, nor a section of another kind.
        Assert.Equal(bare, H(rows, 0, Sec("s", SidebarSectionKind.EntityList, chips, collapsed: true), editable: true));
        Assert.Equal(bare, H(rows, 0, Sec("s", SidebarSectionKind.PlaylistTree, chips), editable: true));
    }

    [Fact]
    public void PrefixSumOfExtents_IsExactlyContentYOf()
    {
        var section = Sec("s", SidebarSectionKind.PlaylistTree,
            SidebarDisplayOptions.Default with { Density = SidebarDensity.Cozy, Subtitles = true });
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.SectionHeader),
            Row(SidebarRowKind.FolderHeader),
            Row(SidebarRowKind.EntityRow),
            Row(SidebarRowKind.EntityRow),
            Row(SidebarRowKind.TreeEnd),
        };
        float ExtentOf(int i) => H(rows, i, section);

        // The pane's rows are contiguous inside ONE virtualized list, so the prefix sum IS the row's content-space Y -
        // which is what drop placement and bring-into-view resolve against.
        float running = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(running, SidebarRowGeometry.ContentYOf(i, rows.Count, ExtentOf), 3);
            running += ExtentOf(i);
        }
        // 30 header (first row, no air) + 44 folder + 44 + 44 + 24 tree end. The trailing 44-DIP CREATE row is gone:
        // the affordance is the section header's "+", which is chrome inside the header band's own extent.
        Assert.Equal(30f + 44f + 44f + 44f + 24f, running, 3);
        Assert.Equal(running, SidebarRowGeometry.ContentYOf(rows.Count, rows.Count, ExtentOf), 3);
    }
}
#endregion

#region ROW DIFF — SidebarRowDiffTests
// SidebarRowDiff decides which of the pane's per-row epochs a publish bumps, i.e. which realized rows re-render. Two
// directions matter, and they fail differently:
//   - too EAGER is only a perf regression (a pane-wide version would bump everything, every publish);
//   - too LAZY is a correctness bug — a realized row keeps drawing the previous plan's content. The entry cases below
//     are that risk in concrete form: a row addresses its entry by INDEX, so a library refresh can leave every row
//     record byte-identical while the entry behind it gained a name, a cover or a child count.
public sealed class SidebarRowDiffTests
{
    static SidebarRow Row(string key, int entryIndex = -1, string section = "s",
                          SidebarRowKind kind = SidebarRowKind.EntityRow)
        => new(kind, section, 0, entryIndex, 0, key);

    static SidebarLibraryEntry Entry(string id, string name = "n", string uri = "spotify:playlist:x")
        => new(id, SidebarEntryKind.Playlist, uri, name, "", default, null, 0, 0, 0, 0, 0, 0, false,
               SidebarPlaylistFlavor.None);

    static readonly IReadOnlyList<SidebarLibraryEntry> NoEntries = Array.Empty<SidebarLibraryEntry>();

    [Fact]
    public void IdenticalPlans_ChangeNothing()
    {
        var rows = new[] { Row("a"), Row("b"), Row("c") };
        var next = new[] { Row("a"), Row("b"), Row("c") };
        Span<bool> changed = new bool[3];
        SidebarRowDiff.Diff(rows, NoEntries, next, NoEntries, changed);
        Assert.Equal(new[] { false, false, false }, changed.ToArray());
    }

    [Fact]
    public void OnlyTheRowsWhoseRecordMoved_AreMarked()
    {
        var rows = new[] { Row("a"), Row("b"), Row("c") };
        var next = new[] { Row("a"), Row("B!"), Row("c") };
        Span<bool> changed = new bool[3];
        SidebarRowDiff.Diff(rows, NoEntries, next, NoEntries, changed);
        Assert.Equal(new[] { false, true, false }, changed.ToArray());
    }

    [Fact]
    public void RowsBeyondTheOldPlan_AreAlwaysMarked()
    {
        var rows = new[] { Row("a") };
        var next = new[] { Row("a"), Row("b") };
        Span<bool> changed = new bool[2];
        SidebarRowDiff.Diff(rows, NoEntries, next, NoEntries, changed);
        Assert.Equal(new[] { false, true }, changed.ToArray());
    }

    [Fact]
    public void AShrunkPlan_OnlyReportsTheRowsItStillHas()
    {
        var rows = new[] { Row("a"), Row("b"), Row("c") };
        var next = new[] { Row("a") };
        Span<bool> changed = new bool[3];
        changed[1] = changed[2] = false;
        SidebarRowDiff.Diff(rows, NoEntries, next, NoEntries, changed);
        Assert.False(changed[0]);
        // Indices past the new plan are left alone — the pane never bumps an epoch no slot can legally address.
        Assert.False(changed[1]);
        Assert.False(changed[2]);
    }

    // The regression this type exists for: same row records, different entity behind one of them.
    [Fact]
    public void AChangedEntry_MarksTheRowThatAddressesIt_EvenWithAnIdenticalRowRecord()
    {
        var rows = new[] { Row("a", entryIndex: 0), Row("b", entryIndex: 1) };
        var oldEntries = new[] { Entry("p1", "Old name"), Entry("p2") };
        var newEntries = new[] { Entry("p1", "New name"), Entry("p2") };
        Span<bool> changed = new bool[2];
        SidebarRowDiff.Diff(rows, oldEntries, rows, newEntries, changed);
        Assert.Equal(new[] { true, false }, changed.ToArray());
    }

    [Fact]
    public void AnEntryIndexThatBecomesOutOfRange_MarksTheRow()
    {
        var rows = new[] { Row("a", entryIndex: 1) };
        var oldEntries = new[] { Entry("p1"), Entry("p2") };
        var newEntries = new[] { Entry("p1") };
        Span<bool> changed = new bool[1];
        SidebarRowDiff.Diff(rows, oldEntries, rows, newEntries, changed);
        Assert.True(changed[0]);
    }

    [Fact]
    public void EntrylessRows_IgnoreTheEntryListEntirely()
    {
        // A header/divider/skeleton carries EntryIndex -1: nothing behind it can go stale, so a wholesale entry-list
        // churn must not re-render it.
        var rows = new[] { Row("h", entryIndex: -1, kind: SidebarRowKind.SectionHeader) };
        Span<bool> changed = new bool[1];
        SidebarRowDiff.Diff(rows, new[] { Entry("p1") }, rows, new[] { Entry("p9", "different") }, changed);
        Assert.False(changed[0]);
    }

    [Fact]
    public void RowChanged_IsFalseOutsideTheNewPlan()
        => Assert.False(SidebarRowDiff.RowChanged(
            new[] { Row("a") }, NoEntries, new[] { Row("a") }, NoEntries, index: 5));
}
#endregion

#region EDIT PLAN — SidebarEditPlanTests
/// <summary>
/// PHASE 2 — the pure rules an edit session implies (<see cref="SidebarEditPlan"/>), plus the two band-slot →
/// command translations the canvas commits through.
///
/// <para>"Customize" is a MODE OVER THE LIVE PANE, not a page that redraws the sidebar, so every decision the canvas
/// makes — which sections reveal their rows, whether section drag is armed, what a card's count says, and how a
/// dropped card or a dropped palette chip becomes ONE undoable command — lives in the engine-free half and is driven
/// here against production code rather than only by the eye.</para>
///
/// <para><b>The index trap this suite exists for.</b> Two index spaces meet in the translations: BAND SLOTS
/// enumerate the <c>SectionCard</c> rows of the PLAN (which is built over the RENDER-PATH document — the one
/// carrying the materialised Shortcuts head at index 0), while <c>MoveSection.NewIndex</c>/<c>AddSection.Index</c>
/// index the PERSISTED document, which does not contain that head at all. Every row array below is therefore built
/// over the render document while the command is asserted against the persisted one, so an off-by-one would fail
/// here rather than silently file a section one slot too high.</para>
/// </summary>
public class SidebarEditPlanTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSectionSpec Sec(string id, SidebarSectionKind kind, bool hidden = false,
        IReadOnlyList<SidebarItemSpec>? items = null, IReadOnlyList<SidebarSectionSpec>? children = null)
        => new(id, kind, Title: null, TitleLocKey: null, Hidden: hidden, Collapsed: false, Display: null,
               Items: items, Query: null, Children: children, Extension: null);

    static SidebarItemSpec Route(string key, string id, bool hidden = false)
        => new(id, SidebarItemTarget.Route, key, Hidden: hidden);

    static SidebarCustomLayout Doc(params SidebarSectionSpec[] sections)
        => new(SidebarTemplates.Curated, sections);

    /// <summary>The render document a pane in edit mode actually plans: the persisted document with the materialised
    /// Shortcuts head prepended, exactly as a Curated pane builds it.</summary>
    static SidebarCustomLayout Rendered(SidebarCustomLayout persisted)
        => SidebarShortcutsSection.Prepend(persisted, SidebarCustomLayout.DefaultTopBar);

    /// <summary>The card rows <c>SidebarRowPlanner.BuildEdit</c> emits for a document: ONE <c>SectionCard</c> per
    /// top-level section this build understands, in document order, with the card's honest count. The shape is copied
    /// from BuildEdit deliberately — the rules under test consume rows, not a planner.</summary>
    static List<SidebarRow> Cards(SidebarCustomLayout document)
    {
        var rows = new List<SidebarRow>();
        foreach (var s in document.Sections)
        {
            if (!SidebarSectionKinds.IsKnown(s.Kind)) continue;
            rows.Add(new SidebarRow(SidebarRowKind.SectionCard, s.Id, 0, -1, SidebarEditPlan.CardCount(s), s.Id));
        }
        return rows;
    }

    // ── ShowsBody ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Exactly ONE section reveals its real rows under its card. One at a time on purpose: a 60-row expanded
    /// sidebar turns section dragging into a scroll-fight, and a card-only plan has the uniform pitch
    /// <c>Reorderable</c> wants.</summary>
    [Fact]
    public void ShowsBody_RevealsOnlyTheExpandedSection()
    {
        var a = Sec("sec_a", SidebarSectionKind.StaticLinks);
        var b = Sec("sec_b", SidebarSectionKind.EntityList);
        var edit = new SidebarEditState(ExpandedSection: "sec_a");

        Assert.True(SidebarEditPlan.ShowsBody(in edit, a));
        Assert.False(SidebarEditPlan.ShowsBody(in edit, b));

        // No session state at all ⇒ every section is a card.
        var none = new SidebarEditState();
        Assert.False(SidebarEditPlan.ShowsBody(in none, a));
        Assert.False(SidebarEditPlan.ShowsBody(in none, b));

        // A blank id is "nothing expanded", not "the section whose id is empty".
        var blank = new SidebarEditState(ExpandedSection: "");
        Assert.False(SidebarEditPlan.ShowsBody(in blank, a));
    }

    /// <summary>"Show section contents" reveals EVERY visible section's body at once, for item-level work.</summary>
    [Fact]
    public void ShowsBody_ShowContentsRevealsEveryVisibleSection()
    {
        var edit = new SidebarEditState(ShowContents: true);
        Assert.True(SidebarEditPlan.ShowsBody(in edit, Sec("sec_a", SidebarSectionKind.StaticLinks)));
        Assert.True(SidebarEditPlan.ShowsBody(in edit, Sec("sec_b", SidebarSectionKind.EntityList)));
        Assert.True(SidebarEditPlan.ShowsBody(in edit, Sec("sec_c", SidebarSectionKind.Pinned)));
    }

    /// <summary>A HIDDEN section NEVER reveals a body — not even while it is the expanded one, and not under
    /// ShowContents. Its rows are not in the user's live sidebar, so drawing them would be the editor lying about the
    /// artifact it edits. The CARD still exists (dimmed, eye-off): nothing vanishes into an invisible elsewhere, which
    /// is what the planner's separate "hidden still gets a card" rule guarantees.</summary>
    [Fact]
    public void ShowsBody_AHiddenSectionNeverRevealsOne()
    {
        var hidden = Sec("sec_h", SidebarSectionKind.StaticLinks, hidden: true);

        foreach (var edit in new[]
                 {
                     new SidebarEditState(ExpandedSection: "sec_h"),
                     new SidebarEditState(ShowContents: true),
                     new SidebarEditState(ExpandedSection: "sec_h", ShowContents: true),
                 })
            Assert.False(SidebarEditPlan.ShowsBody(in edit, hidden));
    }

    /// <summary>A Divider and a Header are pure chrome — the planner has no body arm for either — so their cards carry
    /// no disclosure mark rather than offering a chevron that opens onto nothing.</summary>
    [Theory]
    [InlineData(SidebarSectionKind.Divider)]
    [InlineData(SidebarSectionKind.Header)]
    public void ShowsBody_ChromeKindsHaveNoBodyToShow(SidebarSectionKind kind)
    {
        Assert.False(SidebarEditPlan.HasBody(kind));

        var section = Sec("sec_x", kind);
        var expanded = new SidebarEditState(ExpandedSection: "sec_x");
        var all = new SidebarEditState(ShowContents: true);
        Assert.False(SidebarEditPlan.ShowsBody(in expanded, section));
        Assert.False(SidebarEditPlan.ShowsBody(in all, section));
    }

    [Fact]
    public void HasBody_IsTrueForEveryOtherKnownKind()
    {
        foreach (SidebarSectionKind kind in Enum.GetValues<SidebarSectionKind>())
        {
            if (kind is SidebarSectionKind.Divider or SidebarSectionKind.Header) continue;
            Assert.True(SidebarEditPlan.HasBody(kind), kind + " lost its body arm");
        }
    }

    // ── SectionsReorderable ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Section drag is armed only while EVERY section is a card. A <c>Reorderable</c> band is one CONTIGUOUS
    /// run at ONE uniform pitch; the moment a section expands, its body rows split the card run in two and the slot math
    /// would address body rows as if they were cards — the same guard the pane already applies to a Pinned band whose
    /// folder is expanded. Explicit Move up / Move down stay available from every card's "…" menu, so a section can
    /// always be reordered: drag is one of several ways, never the only way.</summary>
    [Fact]
    public void SectionsReorderable_IsDisarmedByAnyRevealedBody()
    {
        var idle = new SidebarEditState();
        Assert.True(SidebarEditPlan.SectionsReorderable(in idle));

        var expanded = new SidebarEditState(ExpandedSection: "sec_a");
        Assert.False(SidebarEditPlan.SectionsReorderable(in expanded));

        var contents = new SidebarEditState(ShowContents: true);
        Assert.False(SidebarEditPlan.SectionsReorderable(in contents));

        var both = new SidebarEditState(ExpandedSection: "sec_a", ShowContents: true);
        Assert.False(SidebarEditPlan.SectionsReorderable(in both));

        // An OPEN OPTIONS POPOVER changes no rows, so it must not disarm the band either.
        var options = new SidebarEditState(OptionsSection: "sec_a");
        Assert.True(SidebarEditPlan.SectionsReorderable(in options));

        // A blank expanded id is "nothing expanded" here too — the two rules must not disagree about that.
        var blank = new SidebarEditState(ExpandedSection: "");
        Assert.True(SidebarEditPlan.SectionsReorderable(in blank));
    }

    // ── Fold ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The session folded into the pane's plan <c>DepKey</c>. "No session" must be 0 and a LIVE session must
    /// never be 0, or entering edit mode on a document that happened to fold to 0 would not re-plan at all.</summary>
    [Fact]
    public void Fold_SeparatesNoSessionFromEverySession()
    {
        SidebarEditState? none = null;
        Assert.Equal(0, SidebarEditPlan.Fold(none));

        foreach (var live in new SidebarEditState?[]
                 {
                     new SidebarEditState(),
                     new SidebarEditState(ShowContents: true),
                     new SidebarEditState(ExpandedSection: "sec_a"),
                     new SidebarEditState(ExpandedSection: "sec_a", ShowContents: true),
                     new SidebarEditState(OptionsSection: "sec_a"),
                 })
            Assert.NotEqual(0, SidebarEditPlan.Fold(live));
    }

    [Fact]
    public void Fold_ChangesWithTheExpandedSectionAndTheContentsSwitch()
    {
        SidebarEditState? idle = new SidebarEditState();
        SidebarEditState? a = new SidebarEditState(ExpandedSection: "sec_a");
        SidebarEditState? b = new SidebarEditState(ExpandedSection: "sec_b");
        SidebarEditState? contents = new SidebarEditState(ShowContents: true);

        Assert.NotEqual(SidebarEditPlan.Fold(idle), SidebarEditPlan.Fold(a));
        Assert.NotEqual(SidebarEditPlan.Fold(a), SidebarEditPlan.Fold(b));      // a DIFFERENT section, not just "one"
        Assert.NotEqual(SidebarEditPlan.Fold(idle), SidebarEditPlan.Fold(contents));

        // Deterministic: the same session folds the same way, or the pane would re-plan on every frame.
        Assert.Equal(SidebarEditPlan.Fold(a), SidebarEditPlan.Fold(new SidebarEditState(ExpandedSection: "sec_a")));
    }

    /// <summary>OptionsSection is EXCLUDED on purpose: opening a popover changes nothing about the planned rows, and
    /// folding it in would re-plan the whole pane on every open.</summary>
    [Fact]
    public void Fold_IgnoresTheOpenOptionsPopover()
    {
        SidebarEditState? closed = new SidebarEditState(ExpandedSection: "sec_a");
        SidebarEditState? open = new SidebarEditState(ExpandedSection: "sec_a", OptionsSection: "sec_b");
        Assert.Equal(SidebarEditPlan.Fold(closed), SidebarEditPlan.Fold(open));
    }

    // ── CardCount ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A card counts what the DOCUMENT holds, never "how many rows would this section plan" — the latter is
    /// only knowable by planning the body, and planning a 10 000-entry EntityList once per card per re-plan to print a
    /// number would be a real cost for a decoration.</summary>
    [Fact]
    public void CardCount_CountsAGroupsChildren()
    {
        var group = Sec("sec_g", SidebarSectionKind.CustomGroup,
            children: [Sec("sec_c1", SidebarSectionKind.StaticLinks), Sec("sec_c2", SidebarSectionKind.Divider)]);
        Assert.Equal(2, SidebarEditPlan.CardCount(group));
        Assert.Equal(0, SidebarEditPlan.CardCount(Sec("sec_empty", SidebarSectionKind.CustomGroup)));
    }

    /// <summary>An authored item list counts its VISIBLE items — a hidden item draws no row, so counting it would make
    /// the card disagree with the sidebar beside it.</summary>
    [Fact]
    public void CardCount_CountsVisibleAuthoredItemsOnly()
    {
        var links = Sec("sec_l", SidebarSectionKind.StaticLinks,
            items: [Route("home", "itm_1"), Route("search", "itm_2", hidden: true), Route("liked", "itm_3")]);
        Assert.Equal(2, SidebarEditPlan.CardCount(links));
        Assert.Equal(0, SidebarEditPlan.CardCount(Sec("sec_l2", SidebarSectionKind.StaticLinks)));

        // The materialised Shortcuts head is an ordinary StaticLinks section, so its card counts its shortcuts.
        Assert.Equal(SidebarCustomLayout.DefaultTopBar.Count,
            SidebarEditPlan.CardCount(SidebarShortcutsSection.From(SidebarCustomLayout.DefaultTopBar)));
    }

    /// <summary>A PROJECTED section shows nothing rather than a number it would have to guess. Pinned is the subtle one:
    /// its "items" are display OVERRIDES for pins made elsewhere, not the pin list — counting them would print "0" over
    /// a band showing twelve pins.</summary>
    [Fact]
    public void CardCount_IsMinusOneForEveryProjectedSection()
    {
        Assert.Equal(-1, SidebarEditPlan.CardCount(Sec("sec_p", SidebarSectionKind.Pinned)));
        Assert.Equal(-1, SidebarEditPlan.CardCount(Sec("sec_p2", SidebarSectionKind.Pinned,
            items: [Route("home", "itm_1")])));

        foreach (var kind in new[]
                 {
                     SidebarSectionKind.PlaylistTree, SidebarSectionKind.EntityList, SidebarSectionKind.JumpBackIn,
                     SidebarSectionKind.NewReleases, SidebarSectionKind.Concerts, SidebarSectionKind.Extension,
                     SidebarSectionKind.Divider, SidebarSectionKind.Header,
                 })
            Assert.Equal(-1, SidebarEditPlan.CardCount(Sec("sec_x", kind)));
    }

    // ── IsPinnedCard ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Shortcuts head is not in <c>Sections</c>, so MoveSection / SetSectionHidden / DuplicateSection /
    /// RemoveSection addressed at it are all UnknownSection rejections. Its card therefore carries no grip, no eye and
    /// no "…": an affordance that silently rejects is strictly worse than one that is not offered.</summary>
    [Fact]
    public void IsPinnedCard_IsExactlyTheSentinel()
    {
        Assert.True(SidebarEditPlan.IsPinnedCard(SidebarIds.TopBarSection));
        Assert.False(SidebarEditPlan.IsPinnedCard("sec_a"));
        Assert.False(SidebarEditPlan.IsPinnedCard(null));
        Assert.False(SidebarEditPlan.IsPinnedCard(""));
    }

    // ── SectionIdAt ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SectionIdAt_ReadsOnlyCardRowsInsideTheBand()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.StaticLinks), Sec("sec_b", SidebarSectionKind.EntityList));
        var rows = Cards(Rendered(persisted));                 // [topbar, sec_a, sec_b]

        Assert.Equal("sec_a", SidebarEditPlan.SectionIdAt(rows, 1, 2, 0));
        Assert.Equal("sec_b", SidebarEditPlan.SectionIdAt(rows, 1, 2, 1));
        Assert.Equal("", SidebarEditPlan.SectionIdAt(rows, 1, 2, 2));        // past the band
        Assert.Equal("", SidebarEditPlan.SectionIdAt(rows, 1, 2, -1));
        Assert.Equal("", SidebarEditPlan.SectionIdAt(null, 0, 2, 0));
        Assert.Equal("", SidebarEditPlan.SectionIdAt(rows, 9, 2, 0));        // past the plan

        // A non-card row inside the band's span is NOT a card, so it resolves to "" rather than to its section id.
        rows[1] = new SidebarRow(SidebarRowKind.IconRow, "sec_a", 0, -1, 0, "home");
        Assert.Equal("", SidebarEditPlan.SectionIdAt(rows, 1, 2, 0));
    }

    // ── ToMoveSection ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Dropping past the last card is APPEND, which in the post-removal index space is the persisted tail. The
    /// row array is built over the RENDER document (Shortcuts head at plan index 0) while the answer indexes the
    /// PERSISTED one — so a stray +1 fails here.</summary>
    [Fact]
    public void ToMoveSection_PastTheEndIsThePostRemovalTail()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList),
                            Sec("sec_c", SidebarSectionKind.StaticLinks), Sec("sec_d", SidebarSectionKind.Divider));
        var rows = Cards(Rendered(persisted));
        Assert.Equal(5, rows.Count);                                   // the head plus four cards
        Assert.Equal(SidebarIds.TopBarSection, rows[0].SectionId);

        var move = Assert.IsType<MoveSection>(
            SidebarEditPlan.ToMoveSection(persisted, rows, bandStart: 1, bandCount: 4, from: 0, to: 3));
        Assert.Equal("sec_a", move.SectionId);
        Assert.Null(move.NewParentId);
        Assert.Equal(3, move.NewIndex);                                // NOT 4: the head is not in `Sections`

        var result = SidebarLayoutReducer.Apply(persisted, move);
        Assert.True(result.Changed);
        Assert.Equal(new[] { "sec_b", "sec_c", "sec_d", "sec_a" }, IdsOf(result.Layout));
    }

    [Fact]
    public void ToMoveSection_AboveALaterNeighbourLandsInThatNeighboursPlace()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList),
                            Sec("sec_c", SidebarSectionKind.StaticLinks), Sec("sec_d", SidebarSectionKind.Divider));
        var rows = Cards(Rendered(persisted));

        // sec_a dropped at band slot 1 — i.e. between sec_b and sec_c.
        var move = Assert.IsType<MoveSection>(
            SidebarEditPlan.ToMoveSection(persisted, rows, 1, 4, from: 0, to: 1));
        Assert.Equal("sec_a", move.SectionId);
        Assert.Equal(1, move.NewIndex);
        Assert.Equal(new[] { "sec_b", "sec_a", "sec_c", "sec_d" },
                     IdsOf(SidebarLayoutReducer.Apply(persisted, move).Layout));
    }

    [Fact]
    public void ToMoveSection_AboveAnEarlierNeighbourLandsAboveIt()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList),
                            Sec("sec_c", SidebarSectionKind.StaticLinks), Sec("sec_d", SidebarSectionKind.Divider));
        var rows = Cards(Rendered(persisted));

        var move = Assert.IsType<MoveSection>(
            SidebarEditPlan.ToMoveSection(persisted, rows, 1, 4, from: 3, to: 1));
        Assert.Equal("sec_d", move.SectionId);
        Assert.Equal(1, move.NewIndex);
        Assert.Equal(new[] { "sec_a", "sec_d", "sec_b", "sec_c" },
                     IdsOf(SidebarLayoutReducer.Apply(persisted, move).Layout));
    }

    /// <summary>THE GAP. An unknown (future) section kind plans no card, exactly as it renders no rows — so the band's
    /// slots and the document's indexes diverge. Bridging through the NEIGHBOUR the drop landed above is the only
    /// translation that stays exact when a card is missing from the middle of the run, and the unknown section must come
    /// out of the move exactly where it went in (the round-trip-untouched policy).</summary>
    [Fact]
    public void ToMoveSection_BridgesTheGapAnUnknownKindLeavesInTheBand()
    {
        var persisted = Doc(
            Sec("sec_a", SidebarSectionKind.StaticLinks),
            new SidebarSectionSpec("sec_future", (SidebarSectionKind)200, Title: "From the future"),
            Sec("sec_b", SidebarSectionKind.EntityList),
            Sec("sec_c", SidebarSectionKind.Divider));

        var rows = Cards(Rendered(persisted));
        Assert.Equal(4, rows.Count);                                   // head + THREE cards: sec_future plans none
        Assert.Equal(new[] { SidebarIds.TopBarSection, "sec_a", "sec_b", "sec_c" },
                     new[] { rows[0].SectionId, rows[1].SectionId, rows[2].SectionId, rows[3].SectionId });

        // sec_a dropped at band slot 1 — visually between sec_b and sec_c. Bridged through sec_c (document index 3).
        var move = Assert.IsType<MoveSection>(
            SidebarEditPlan.ToMoveSection(persisted, rows, 1, 3, from: 0, to: 1));
        Assert.Equal("sec_a", move.SectionId);
        Assert.Equal(2, move.NewIndex);

        var after = SidebarLayoutReducer.Apply(persisted, move);
        Assert.True(after.Changed);
        Assert.Equal(new[] { "sec_future", "sec_b", "sec_a", "sec_c" }, IdsOf(after.Layout));
    }

    /// <summary>The sentinel is not in <c>Sections</c>, so a drag of the Shortcuts head has no honest command — the
    /// canvas does not offer the grip, and the translation refuses it a second time.</summary>
    [Fact]
    public void ToMoveSection_RefusesTheShortcutsHead()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));
        var rows = Cards(Rendered(persisted));

        // A band that (wrongly) covered the head: slot 0 IS the sentinel.
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, bandStart: 0, bandCount: 3, from: 0, to: 2));
    }

    [Fact]
    public void ToMoveSection_RefusesEveryDegenerateSlot()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));
        var rows = Cards(Rendered(persisted));

        Assert.Null(SidebarEditPlan.ToMoveSection(null, rows, 1, 2, 0, 1));
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, null, 1, 2, 0, 1));
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 1, 1));      // from == to is silence
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 1, 0, 1));      // a one-card band cannot reorder
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, -1, 1));
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 0, 2));      // past the band
    }

    /// <summary>A card whose section the persisted document does not contain (a stale plan, a hand-edited document)
    /// produces nothing rather than a command aimed at a section that is not there.</summary>
    [Fact]
    public void ToMoveSection_RefusesASectionThePersistedDocumentDoesNotHold()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));
        var rows = Cards(Rendered(persisted));
        rows[1] = new SidebarRow(SidebarRowKind.SectionCard, "sec_stale", 0, -1, -1, "sec_stale");

        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 0, 1));
    }

    /// <summary>A card is always a TOP-LEVEL section. A group CHILD reaching the band would mean the canvas cards
    /// nested sections too, and a top-level <c>NewIndex</c> computed for one would file it somewhere the cue never
    /// pointed — so it is refused rather than guessed.</summary>
    [Fact]
    public void ToMoveSection_RefusesAGroupChild()
    {
        var persisted = Doc(
            Sec("sec_g", SidebarSectionKind.CustomGroup, children: [Sec("sec_c1", SidebarSectionKind.StaticLinks)]),
            Sec("sec_b", SidebarSectionKind.EntityList));

        var rows = new List<SidebarRow>
        {
            new(SidebarRowKind.SectionCard, SidebarIds.TopBarSection, 0, -1, 1, SidebarIds.TopBarSection),
            new(SidebarRowKind.SectionCard, "sec_c1", 0, -1, 0, "sec_c1"),      // a CHILD, wrongly carded
            new(SidebarRowKind.SectionCard, "sec_b", 0, -1, -1, "sec_b"),
        };

        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 0, 1));   // moving a child
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 1, 0));   // landing above a child
    }

    // ── ToAddSection ─────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSectionDropPayload Payload(SidebarSectionKind kind = SidebarSectionKind.StaticLinks,
        SidebarItemSpec? item = null)
        => new(kind, "Label", item);

    /// <summary>The drop convention is "insert BEFORE the card you aimed at" — the same neighbour bridging
    /// <see cref="SidebarEditPlan.ToMoveSection"/> uses, and for the same reason.</summary>
    [Fact]
    public void ToAddSection_InsertsBeforeTheCardUnderThePointer()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList),
                            Sec("sec_c", SidebarSectionKind.Divider));
        var item = Route("home", "itm_home");

        var add = Assert.IsType<AddSection>(
            SidebarEditPlan.ToAddSection(persisted, "sec_b", Payload(item: item)));
        Assert.Equal(SidebarSectionKind.StaticLinks, add.Kind);
        Assert.Equal(1, add.Index);                                    // NOT 2: the render document's head is not here
        Assert.Null(add.ParentId);
        Assert.Same(item, add.Item);

        var result = SidebarLayoutReducer.Apply(persisted, add);
        Assert.True(result.Changed);
        Assert.Equal(SidebarSectionKind.StaticLinks, result.Layout.Sections[1].Kind);
        Assert.Equal("sec_b", result.Layout.Sections[2].Id);
        Assert.Equal("home", result.Layout.Sections[1].ItemList[0].Key);   // pre-seeded: never an empty Links section
    }

    /// <summary>The pinned Shortcuts head is not in <c>Sections</c>, so a drop on it resolves to index 0 — "above
    /// everything the reducer can address", which is exactly where the cue pointed.</summary>
    [Fact]
    public void ToAddSection_ADropOnTheShortcutsHeadIsIndexZero()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));

        var add = Assert.IsType<AddSection>(
            SidebarEditPlan.ToAddSection(persisted, SidebarIds.TopBarSection, Payload()));
        Assert.Equal(0, add.Index);

        var result = SidebarLayoutReducer.Apply(persisted, add);
        Assert.Equal(SidebarSectionKind.StaticLinks, result.Layout.Sections[0].Kind);
        Assert.Equal("sec_a", result.Layout.Sections[1].Id);
    }

    /// <summary>No card under the pointer ⇒ APPEND, which is also what a plain palette CLICK does — so drag is never
    /// the only way to add a section.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToAddSection_WithNoCardUnderThePointerAppends(string? beforeId)
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));

        var add = Assert.IsType<AddSection>(SidebarEditPlan.ToAddSection(persisted, beforeId, Payload()));
        Assert.Equal(persisted.Sections.Count, add.Index);
        Assert.Equal("sec_b", SidebarLayoutReducer.Apply(persisted, add).Layout.Sections[1].Id);
    }

    [Fact]
    public void ToAddSection_RefusesAChildCardAndAnUnknownKind()
    {
        var persisted = Doc(
            Sec("sec_g", SidebarSectionKind.CustomGroup, children: [Sec("sec_c1", SidebarSectionKind.StaticLinks)]),
            Sec("sec_b", SidebarSectionKind.EntityList));

        // A child card is not a top-level slot: refusing beats silently filing the section where the cue never pointed.
        Assert.Null(SidebarEditPlan.ToAddSection(persisted, "sec_c1", Payload()));

        // A section the document does not hold at all is the same refusal.
        Assert.Null(SidebarEditPlan.ToAddSection(persisted, "sec_stale", Payload()));

        // A payload this build cannot add (a future kind) never becomes a command the reducer would only reject.
        Assert.Null(SidebarEditPlan.ToAddSection(persisted, "sec_b", Payload((SidebarSectionKind)200)));

        Assert.Null(SidebarEditPlan.ToAddSection(null, "sec_b", Payload()));
        Assert.Null(SidebarEditPlan.ToAddSection(persisted, "sec_b", null));
    }

    /// <summary>The drag KIND has ONE owner. A drag kind typed twice is a drop that silently accepts nothing — the dnd
    /// rule for cross-list work — which is why the pane and the companion page both read this const.</summary>
    [Fact]
    public void SectionDragKind_IsOneNamedConstant()
        => Assert.Equal("wavee.sidebar.section", SidebarEditPlan.SectionDragKind);

    static string[] IdsOf(SidebarCustomLayout layout)
    {
        var ids = new string[layout.Sections.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = layout.Sections[i].Id;
        return ids;
    }
}
#endregion

#region PANE INVARIANT — SidebarPaneInvariantTests
// The settled-frame terminal-state validator: the docked pane's rendered facts (which layer is opaque, which is
// hit-test-visible, is the width in range) either agree with each other or the fault flags say exactly how they do
// not. `SidebarPaneBounds` (Sidebar.cs) is 0.3's owner of the width ladder that 0.2.9 kept on `ShellResponsiveLayout`.
public class SidebarPaneInvariantTests
{
    static SidebarPaneFrameSnapshot Expanded(float preferred = 320f, float rendered = 320f) => new(
        SidebarDesign.Curated,
        UserCollapsed: false,
        PresentedCompact: false,
        PreferredExpandedWidth: preferred,
        RenderedPaneWidth: rendered,
        ExpandedOpacity: 1f,
        RailOpacity: 0f,
        ExpandedHitTestVisible: true,
        RailHitTestVisible: false);

    static SidebarPaneFrameSnapshot Compact(float rendered = SidebarPaneBounds.CompactRailW) => new(
        SidebarDesign.LibraryV3,
        UserCollapsed: true,
        PresentedCompact: true,
        PreferredExpandedWidth: 340f,
        RenderedPaneWidth: rendered,
        ExpandedOpacity: 0f,
        RailOpacity: 1f,
        ExpandedHitTestVisible: false,
        RailHitTestVisible: true);

    [Fact]
    public void ExpandedTerminalState_IsValid()
    {
        var state = Expanded();
        Assert.Equal(SidebarPaneInvariantFault.None, SidebarPaneInvariant.Inspect(in state));
    }

    [Fact]
    public void CompactTerminalState_IsValid()
    {
        var state = Compact();
        Assert.True(SidebarPaneInvariant.IsValid(in state));
    }

    [Fact]
    public void ReportedTwentyFourDipSliver_IsRejected()
    {
        var state = Expanded(rendered: 24f);
        var fault = SidebarPaneInvariant.Inspect(in state);
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.ExpandedWidthOutOfRange));
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Theory]
    [InlineData(55.49f, false)]
    [InlineData(55.5f, true)]
    [InlineData(56.5f, true)]
    [InlineData(56.51f, false)]
    public void CompactWidth_UsesHalfDipTolerance(float rendered, bool valid)
    {
        var state = Compact(rendered);
        Assert.Equal(valid, SidebarPaneInvariant.IsValid(in state));
    }

    [Fact]
    public void WrongLayerOwnership_IsRejected()
    {
        var state = Expanded() with
        {
            ExpandedOpacity = 0f,
            RailOpacity = 1f,
            ExpandedHitTestVisible = false,
            RailHitTestVisible = true,
        };
        var fault = SidebarPaneInvariant.Inspect(in state);
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.LayerOpacityMismatch));
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.HitTestOwnerMismatch));
    }

    [Fact]
    public void ExpandedWidthMustMatchTheRememberedPreference()
    {
        var state = Expanded(preferred: 360f, rendered: 320f);
        Assert.True(SidebarPaneInvariant.Inspect(in state)
            .HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Fact]
    public void NonFiniteGeometry_IsRejectedWithoutFurtherClassification()
    {
        var state = Expanded() with { RenderedPaneWidth = float.NaN };
        Assert.Equal(SidebarPaneInvariantFault.NonFiniteValue, SidebarPaneInvariant.Inspect(in state));
    }

    // ── THE ONE CONTENT LANE ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The lane is DERIVED, never typed twice: the pane edge plus a depth-0 row's own indent. If someone
    /// re-tunes <c>IndentFor</c> or the pane pad, the lane moves with them instead of silently disagreeing.</summary>
    [Fact]
    public void ContentLane_IsThePaneEdgePlusTheDepthZeroRowIndent()
    {
        Assert.Equal(SidebarRowGeometry.ContentLane, SidebarRowGeometry.PaneEdge + SidebarRowGeometry.IndentFor(0));
        Assert.Equal(SidebarRowGeometry.PaneEdge + SidebarRowGeometry.RowInsetRight, SidebarRowGeometry.ContentLaneEnd);
        // The landed numbers the screenshots were measured against, pinned so a "harmless" retune is a visible diff.
        Assert.Equal(12f, SidebarRowGeometry.ContentLane);
        Assert.Equal(16f, SidebarRowGeometry.ContentLaneEnd);
    }

    /// <summary>A NESTED row indents from the lane, so a depth-1 child sits exactly one 12-DIP level inside it.</summary>
    [Fact]
    public void NestedRowsIndentFromTheLane()
    {
        Assert.Equal(SidebarRowGeometry.ContentLane + 12f,
                     SidebarRowGeometry.PaneEdge + SidebarRowGeometry.IndentFor(1));
        Assert.Equal(SidebarRowGeometry.IndentFor(4), SidebarRowGeometry.IndentFor(9));   // clamped at 4 levels
    }
}
#endregion
