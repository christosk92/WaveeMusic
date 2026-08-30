using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// The mode-specific 56-DIP rail (§C5.2 / §C8.5). The rail is DERIVED, never authored: it is exactly the ShowInRail
// sections, reduced to tiles, capped, with headings collapsed into compact rules. A rail that silently disagrees with the
// expanded pane is the bug this class exists to prevent.
public sealed class SidebarRailPlannerTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string uri, string name,
        string creator = "", int sourceOrder = 0, int depth = 0)
        => new(Id: id, Kind: kind, Uri: uri, Name: name, Creator: creator, Cover: null, MosaicTiles: null,
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

        // 2 pin tiles · rule · 5 shortcut glyphs · rule · folder + its ONE top-level sibling ("Inner" sits inside the
        // folder, and the rail is top level only). Jump back in ships ShowInRail:false, and its two flanking dividers
        // collapse into the single quiet rule before the shortcuts.
        Assert.Equal(new[]
        {
            SidebarRowKind.EntityRow, SidebarRowKind.EntityRow,
            SidebarRowKind.Divider,
            SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow,
            SidebarRowKind.IconRow,
            SidebarRowKind.Divider,
            SidebarRowKind.FolderHeader, SidebarRowKind.EntityRow,
        }, KindsOf(plan));

        Assert.Equal(new[] { "liked", "albums", "artists", "podcasts", "local" },
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
