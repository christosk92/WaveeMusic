// ── Wavee.Tests/SidebarPlannerTests.cs — the sidebar planner suite on the P3 layout model ─────────────────────────────
//
// The planner reads ONE document (`SidebarLayoutDoc`: the resolved sections of a layout, its density and Your Library's
// options) and ONE projection (`SidebarProjectionInput`), and emits ONE flat row list for the expanded pane and the
// compact rail alike (`SidebarRowPlanner`, `Shell/Sidebar.Planner.cs`). Every fact below builds its document through the
// real overlay rules (`SidebarLayoutRules.Resolve`), so a default, an op and a plan are pinned together, never a
// hand-built section list.
//
// The kept classes test types that survive in shape: the pure plan geometry (`SidebarRowGeometry`), the analytic row
// extents (`SidebarRowExtents`), the row diff (`SidebarRowDiff`) and the pane invariant (`SidebarPaneInvariant`).

using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Shared fixtures: pure entries, documents built through the overlay rules, and row-kind readers.</summary>
static class PlanFixture
{
    public static SidebarLibraryEntry Playlist(string id, int depth = 0, string folder = "")
        => new(id, SidebarEntryKind.Playlist, id.Replace("pl:", ""), "P " + id, "", default, null, 3, 0, 0, 0, 0, depth, false,
               SidebarPlaylistFlavor.None) { FolderId = folder, FolderName = "", FirstArtistName = "", CountKnown = true };

    public static SidebarLibraryEntry Folder(string folderId, int depth = 0)
        => new("folder:" + folderId, SidebarEntryKind.Folder, "", "F " + folderId, "", default, null, 1, 0, 0, 0, 0, depth, false,
               SidebarPlaylistFlavor.None) { FolderId = folderId, FolderName = "", FirstArtistName = "", CountKnown = true };

    public static SidebarLibraryEntry Album(string id)
        => new("album:" + id, SidebarEntryKind.Album, id, "A " + id, "", default, null, 10, 0, 0, 0, 0, 0, false,
               SidebarPlaylistFlavor.None) { FolderId = "", FolderName = "", FirstArtistName = "" };

    public static SidebarLayoutDoc Doc(SidebarLayoutId layout, Func<SidebarLayoutState, SidebarLayoutState>? edit = null,
                                       SidebarDensity density = SidebarDensity.Default)
        => SidebarLayoutRules.Resolve((edit ?? (s => s))(SidebarLayoutState.Default), layout, density);

    public static SidebarLayoutState Op(SidebarLayoutState s, SidebarOp op) => SidebarLayoutRules.Apply(s, op, false).State;

    /// <summary>Several ops applied in order, as one document edit.</summary>
    public static Func<SidebarLayoutState, SidebarLayoutState> Ops(params SidebarOp[] ops)
        => s => { foreach (var op in ops) s = Op(s, op); return s; };

    public static SidebarRowKind[] Kinds(in SidebarRowPlan p) => p.Rows.Select(r => r.Kind).ToArray();

    public static string[] Keys(in SidebarRowPlan p, SidebarRowKind kind)
        => p.Rows.Where(r => r.Kind == kind).Select(r => r.Key).ToArray();

    public static string[] AllKeys(in SidebarRowPlan p) => p.Rows.Select(r => r.Key).ToArray();
}

#region ROW PLANNER — SidebarRowPlannerTests
// The planner's row SEQUENCE is pinned per section kind, per degraded state and per pane mode (§P3.15 facts 1–27).
public sealed class SidebarRowPlannerTests
{
    static SidebarLayoutDoc Classic(Func<SidebarLayoutState, SidebarLayoutState>? edit = null)
        => PlanFixture.Doc(SidebarLayoutId.Classic, edit);

    static SidebarLayoutDoc Library(Func<SidebarLayoutState, SidebarLayoutState>? edit = null)
        => PlanFixture.Doc(SidebarLayoutId.Library, edit);

    static SidebarRowPlan Plan(SidebarLayoutDoc doc, SidebarProjectionInput input, SidebarPlanOptions? options = null)
        => SidebarRowPlanner.Build(doc, in input, options ?? new SidebarPlanOptions(Mode: SidebarPaneMode.Expanded));

    /// <summary>A projection with one playlist in the rootlist, so the Playlists section has its tree.</summary>
    static SidebarProjectionInput OneInTree() => new(PlaylistTree: [PlanFixture.Playlist("pl:a")]);

    // ── Classic ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_NoPins_HomeSepCollectionsSepPlaylists()
    {
        // Fact 1. The empty Pinned section is not rendered at all: no header, no separator of its own.
        var plan = Plan(Classic(), OneInTree());
        Assert.Equal(new[]
        {
            SidebarRowKind.IconRow,                                                   // home
            SidebarRowKind.Divider, SidebarRowKind.SectionHeader,                     // collections
            SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow,
            SidebarRowKind.Divider, SidebarRowKind.SectionHeader,                     // playlists
            SidebarRowKind.EntityRow, SidebarRowKind.TreeEnd,
        }, PlanFixture.Kinds(plan));
        Assert.DoesNotContain(plan.Rows, r => r.Key == "pinned");
    }

    [Fact]
    public void Classic_Pins_UnderTheirHeader_DedupedFromTheTree()
    {
        // Fact 2. pl:a is drawn in Pinned, and its tree row is skipped while the dedupe applies.
        var pin = PlanFixture.Playlist("pl:a");
        var input = new SidebarProjectionInput(Pins: [pin], PlaylistTree: [pin, PlanFixture.Playlist("pl:b")],
                                               PinnedIds: new HashSet<string> { "pl:a" });
        var plan = Plan(Classic(), input);
        Assert.Single(plan.Rows, r => r.Key == "pl:a");
        Assert.Equal("pinned", plan.Rows.Single(r => r.Key == "pl:a").SectionId);
        Assert.Equal("playlists", plan.Rows.Single(r => r.Key == "pl:b").SectionId);
        Assert.Contains(plan.Rows, r => r.Kind == SidebarRowKind.SectionHeader && r.Key == "pinned");
    }

    [Fact]
    public void Classic_PinnedCollapsed_Expanded_NoDedupe()
    {
        // Fact 3. Expanded pane, collapsed Pinned: the header stays, and the pin returns to its home section.
        var pin = PlanFixture.Playlist("pl:a");
        var input = new SidebarProjectionInput(Pins: [pin], PlaylistTree: [pin, PlanFixture.Playlist("pl:b")]);
        var plan = Plan(Classic(PlanFixture.Ops(new SetSectionCollapsed(SidebarLayoutId.Classic, "pinned", true))), input);
        Assert.Contains(plan.Rows, r => r.Kind == SidebarRowKind.SectionHeader && r.Key == "pinned");
        Assert.Equal("playlists", plan.Rows.Single(r => r.Key == "pl:a").SectionId);
    }

    [Fact]
    public void Classic_PinnedCollapsed_Compact_OneTile_Deduped()
    {
        // Fact 4. Compact rail: a collapsed Pinned is one tile, and its pins are not planned anywhere else.
        var pin = PlanFixture.Playlist("pl:a");
        var input = new SidebarProjectionInput(Pins: [pin], PlaylistTree: [pin, PlanFixture.Playlist("pl:b")],
                                               PinnedIds: new HashSet<string>(StringComparer.Ordinal) { "pl:a" });
        var plan = Plan(Classic(PlanFixture.Ops(new SetSectionCollapsed(SidebarLayoutId.Classic, "pinned", true))), input,
                        new SidebarPlanOptions(Mode: SidebarPaneMode.Compact));
        Assert.Contains(plan.Rows, r => r.Kind == SidebarRowKind.SectionTile && r.Key == "pinned" && r.EntryIndex == -1);
        Assert.DoesNotContain(plan.Rows, r => r.Key == "pl:a");
        Assert.Single(plan.Rows, r => r.Key == "pl:b");
    }

    [Fact]
    public void Classic_CollapsedSection_HeaderOnly()
    {
        // Fact 5. A collapsed Collections is its header alone: no glyph rows of its own.
        var plan = Plan(Classic(PlanFixture.Ops(new SetSectionCollapsed(SidebarLayoutId.Classic, "collections", true))), OneInTree());
        Assert.Equal(new[]
        {
            SidebarRowKind.IconRow,
            SidebarRowKind.Divider, SidebarRowKind.SectionHeader,                     // collections, collapsed
            SidebarRowKind.Divider, SidebarRowKind.SectionHeader,                     // playlists
            SidebarRowKind.EntityRow, SidebarRowKind.TreeEnd,
        }, PlanFixture.Kinds(plan));
    }

    [Fact]
    public void Classic_CollapsedEmptySection_NotRendered()
    {
        // Fact 5, the empty half: a collapsed Pinned with no pins draws nothing, not even its header.
        var plan = Plan(Classic(PlanFixture.Ops(new SetSectionCollapsed(SidebarLayoutId.Classic, "pinned", true))), OneInTree());
        Assert.DoesNotContain(plan.Rows, r => r.Key == "pinned");
    }

    [Fact]
    public void Classic_EmptyRecent_NotRendered_NoDoubleSeparator()
    {
        // Fact 6. Recent is shown but has nothing: its header and separator are dropped together, and the tree stays last.
        var plan = Plan(Classic(PlanFixture.Ops(new SetSectionShown(SidebarLayoutId.Classic, "recent", true))), OneInTree());
        Assert.DoesNotContain(plan.Rows, r => r.Key == "recent");
        Assert.Equal(2, plan.Rows.Count(r => r.Kind == SidebarRowKind.Divider));
        Assert.Equal(SidebarRowKind.TreeEnd, plan.Rows[^1].Kind);
    }

    [Fact]
    public void Classic_Recent_RespectsLimit()
    {
        // Fact 7. Twelve plays, the default Recent limit of 5: five rows, in the order the feed gave them.
        var played = Enumerable.Range(0, 12).Select(i => PlanFixture.Playlist("pl:r" + i)).ToArray();
        var plan = Plan(Classic(PlanFixture.Ops(new SetSectionShown(SidebarLayoutId.Classic, "recent", true))),
                        new SidebarProjectionInput(Played: played));
        Assert.Equal(new[] { "pl:r0", "pl:r1", "pl:r2", "pl:r3", "pl:r4" }, PlanFixture.Keys(plan, SidebarRowKind.EntityRow));
    }

    [Fact]
    public void Classic_HiddenSection_NotRendered()
    {
        // Fact 8. A hidden Collections contributes no header, no divider and no glyph rows.
        var plan = Plan(Classic(PlanFixture.Ops(new SetSectionShown(SidebarLayoutId.Classic, "collections", false))), OneInTree());
        Assert.DoesNotContain(plan.Rows, r => r.SectionId == "collections");
        Assert.DoesNotContain(plan.Rows, r => r.Key == "liked");
        Assert.Equal(1, plan.Rows.Count(r => r.Kind == SidebarRowKind.Divider));
    }

    [Fact]
    public void Classic_EmptyTree_HeaderAndHint()
    {
        // Fact 9. A ready, empty rootlist: the Playlists header and its quiet hint, nothing else.
        var plan = Plan(Classic(), new SidebarProjectionInput(PlaylistTree: Array.Empty<SidebarLibraryEntry>()));
        Assert.Equal(SidebarRowKind.SectionHeader, plan.Rows[^2].Kind);
        Assert.Equal("playlists", plan.Rows[^2].Key);
        Assert.Equal(SidebarRowKind.Empty, plan.Rows[^1].Kind);
    }

    [Fact]
    public void Classic_PendingTree_Skeletons()
    {
        // Fact 9, the pending half: three skeleton rows while the rootlist is still arriving.
        var plan = Plan(Classic(), new SidebarProjectionInput(PlaylistTree: null, TreeState: SidebarSourceState.Pending));
        Assert.Equal(3, plan.Rows.Count(r => r.Kind == SidebarRowKind.Skeleton));
    }

    [Fact]
    public void Classic_CollapsedFolder_SkipsChildren()
    {
        // Fact 10. The folder is collapsed in the shared expansion set: its child is not planned.
        var tree = new[] { PlanFixture.Folder("f"), PlanFixture.Playlist("pl:a", depth: 1, folder: "f"), PlanFixture.Playlist("pl:b") };
        var plan = Plan(Classic(), new SidebarProjectionInput(PlaylistTree: tree, ExpandedFolders: new HashSet<string>()));
        Assert.Equal(SidebarRowKind.FolderHeader, plan.Rows.Single(r => r.Key == "folder:f").Kind);
        Assert.DoesNotContain(plan.Rows, r => r.Key == "pl:a");
        Assert.Single(plan.Rows, r => r.Key == "pl:b");
    }

    [Fact]
    public void Classic_ExpandedFolder_ChildrenAtTheirDepth()
    {
        // Fact 10, the expanded half: the child sits one level in.
        var tree = new[] { PlanFixture.Folder("f"), PlanFixture.Playlist("pl:a", depth: 1, folder: "f"), PlanFixture.Playlist("pl:b") };
        var plan = Plan(Classic(), new SidebarProjectionInput(PlaylistTree: tree, ExpandedFolders: new HashSet<string> { "f" }));
        Assert.Equal(1, plan.Rows.Single(r => r.Key == "pl:a").Depth);
        Assert.Equal(0, plan.Rows.Single(r => r.Key == "pl:b").Depth);
    }

    [Fact]
    public void Classic_Collections_OrderAndHidden_AndPinnedRouteDeduped()
    {
        // Fact 11. Artists is hidden, audiobooks moves to the front, and a pinned Albums page is drawn in Pinned only.
        var albumsPin = SidebarLibraryEntry.ForRoute("albums", "Albums");
        var edit = PlanFixture.Ops(
            new SetItemShown(SidebarLayoutId.Classic, "collections", "artists", false),
            new MoveItem(SidebarLayoutId.Classic, "collections", "audiobooks", 0));
        var input = new SidebarProjectionInput(Pins: [albumsPin],
                                               PinnedIds: new HashSet<string> { SidebarPinId.FromRoute("albums")! });
        var plan = Plan(Classic(edit), input);
        Assert.Equal(new[] { "audiobooks", "liked", "podcasts" }, plan.Rows
            .Where(r => r.Kind == SidebarRowKind.IconRow && r.SectionId == "collections").Select(r => r.Key).ToArray());
        Assert.Equal("pinned", plan.Rows.Single(r => r.Key == "albums").SectionId);
    }

    [Fact]
    public void Classic_Compact_NoHeaders_TopLevelOnly_NoGutter()
    {
        // Fact 12. The rail: no section headers, a folder's children behind its flyout, and no tree gutter.
        var tree = new[] { PlanFixture.Folder("f"), PlanFixture.Playlist("pl:a", depth: 1, folder: "f"), PlanFixture.Playlist("pl:b") };
        var plan = Plan(Classic(), new SidebarProjectionInput(PlaylistTree: tree, ExpandedFolders: new HashSet<string> { "f" }),
                        new SidebarPlanOptions(Mode: SidebarPaneMode.Compact));
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.SectionHeader);
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.TreeEnd);
        Assert.DoesNotContain(plan.Rows, r => r.Depth > 0);
        Assert.DoesNotContain(plan.Rows, r => r.Key == "pl:a");
    }

    [Fact]
    public void Classic_CollapsedSection_IsOneTile_KeyedById_InCompact()
    {
        // The compact arm of a collapsed titled section: exactly one tile, keyed by the section id.
        var plan = Plan(Classic(PlanFixture.Ops(new SetSectionCollapsed(SidebarLayoutId.Classic, "collections", true))),
                        new SidebarProjectionInput(), new SidebarPlanOptions(Mode: SidebarPaneMode.Compact));
        Assert.Contains(plan.Rows, r => r.Kind == SidebarRowKind.SectionTile && r.Key == "collections" && r.EntryIndex == -1);
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.SectionHeader);
    }

    [Fact]
    public void Classic_DropArmed_EmptyPinned_HeaderAndDropBand()
    {
        // Fact 13. An empty Pinned while a pinnable drag is live: its header and the dashed drop band.
        var plan = Plan(Classic(), new SidebarProjectionInput(), new SidebarPlanOptions(PinDropArmed: true));
        var band = Assert.Single(plan.Rows, r => r.Kind == SidebarRowKind.DropBand);
        Assert.Equal("pinned", band.SectionId);
        Assert.Contains(plan.Rows, r => r.Kind == SidebarRowKind.SectionHeader && r.Key == "pinned");
    }

    [Fact]
    public void Classic_DropArmed_HiddenPinned_NoBand()
    {
        // Fact 13, hidden: a hidden Pinned plans nothing, band included.
        var plan = Plan(Classic(PlanFixture.Ops(new SetSectionShown(SidebarLayoutId.Classic, "pinned", false))),
                        new SidebarProjectionInput(), new SidebarPlanOptions(PinDropArmed: true));
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.DropBand);
    }

    [Fact]
    public void Classic_DropArmed_Compact_NoBand()
    {
        // Fact 13, compact: the rail has no room for a drop band.
        var plan = Plan(Classic(), new SidebarProjectionInput(),
                        new SidebarPlanOptions(Mode: SidebarPaneMode.Compact, PinDropArmed: true));
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.DropBand);
    }

    [Fact]
    public void Classic_Separators_NeverFirst_NeverDouble()
    {
        // Fact 14. Every optional section shown, some of them empty: a separator is never first, never doubled, never last.
        var edit = PlanFixture.Ops(
            new SetSectionShown(SidebarLayoutId.Classic, "recent", true),
            new SetSectionShown(SidebarLayoutId.Classic, "newReleases", true));
        var input = new SidebarProjectionInput(PlaylistTree: [PlanFixture.Playlist("pl:a")],
                                               Played: Array.Empty<SidebarLibraryEntry>(), NewReleases: null);
        var kinds = PlanFixture.Kinds(Plan(Classic(edit), input));
        Assert.NotEqual(SidebarRowKind.Divider, kinds[0]);
        Assert.NotEqual(SidebarRowKind.Divider, kinds[^1]);
        for (int i = 1; i < kinds.Length; i++)
            Assert.False(kinds[i] == SidebarRowKind.Divider && kinds[i - 1] == SidebarRowKind.Divider);
    }

    [Fact]
    public void Classic_Search_FlattensPlaylists_NoGutter()
    {
        // Fact 15. A Playlists search lists the matching playlists in rootlist order, with no folder chrome and no gutter.
        var tree = new[] { PlanFixture.Folder("f"), PlanFixture.Playlist("pl:a", depth: 1, folder: "f"), PlanFixture.Playlist("pl:b") };
        var plan = Plan(Classic(), new SidebarProjectionInput(PlaylistTree: tree, Search: "pl:b"));
        Assert.Equal(new[] { "pl:b" }, PlanFixture.Keys(plan, SidebarRowKind.EntityRow));
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.FolderHeader);
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.TreeEnd);
    }

    [Fact]
    public void Classic_SettingsNeverPlanned()
    {
        // Fact 16. Settings is the footer, not a planned section.
        var plan = Plan(Classic(), OneInTree());
        Assert.DoesNotContain(plan.Rows, r => r.Key == "settings");
    }

    [Fact]
    public void LikedAppearsOncePerLayout_Classic()
    {
        // Fact 26. Classic: Liked is one glyph row in Collections, never an entity row.
        var plan = Plan(Classic(), OneInTree());
        Assert.Single(plan.Rows, r => r.Kind == SidebarRowKind.IconRow && r.Key == "liked");
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.EntityRow && r.Key == "liked");
    }

    // ── Library (design P.2a) ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Library_PinsFirst_NoHeader_ThenLiked_ThenList()
    {
        // Fact 17. Pins first with no header, Liked Songs next, then the shaped list.
        var pin = PlanFixture.Playlist("pl:p") with { IsPinned = true };
        var album = PlanFixture.Album("x");
        var input = new SidebarProjectionInput(Library: [album], Pins: [pin], LikedTitle: "Liked Songs");
        var plan = Plan(Library(), input);
        Assert.Equal(new[] { "pl:p", "liked", album.Id }, PlanFixture.AllKeys(plan));
        Assert.All(plan.Rows, r => Assert.Equal(SidebarRowKind.EntityRow, r.Kind));
    }

    [Fact]
    public void Library_AlbumsChip_HidesLikedAndRoutePins()
    {
        // Fact 18. Under the Albums chip: no Liked row, no playlist pin, no route pin, only the albums.
        var album = PlanFixture.Album("x");
        var input = new SidebarProjectionInput(
            Library: [album],
            Pins: [PlanFixture.Playlist("pl:p") with { IsPinned = true }, SidebarLibraryEntry.ForRoute("search", "Search")],
            LikedTitle: "Liked Songs");
        var plan = Plan(Library(), input, new SidebarPlanOptions(Filter: SidebarLibraryFilter.Albums));
        Assert.Equal(new[] { album.Id }, PlanFixture.AllKeys(plan));
    }

    [Fact]
    public void Library_ShowLikedOff_NoLikedRow()
    {
        // Fact 19. "Show Liked Songs" off: the fixed row is gone and the list is all that remains.
        var album = PlanFixture.Album("x");
        var plan = Plan(Library(PlanFixture.Ops(new SetShowLiked(false))),
                        new SidebarProjectionInput(Library: [album], LikedTitle: "Liked Songs"));
        Assert.Equal(new[] { album.Id }, PlanFixture.AllKeys(plan));
    }

    [Fact]
    public void Library_HiddenKind_PinGone()
    {
        // Fact 20. Albums hidden in Your Library: an album pin is not drawn, and the list follows the hidden set too.
        var albumPin = PlanFixture.Album("x") with { IsPinned = true };
        var plan = Plan(Library(PlanFixture.Ops(new SetItemShown(SidebarLayoutId.Library, "library", "albums", false))),
                        new SidebarProjectionInput(Library: [PlanFixture.Playlist("pl:p")], Pins: [albumPin], LikedTitle: "Liked Songs"));
        Assert.DoesNotContain(plan.Rows, r => r.Key == albumPin.Id);
        Assert.Equal(new[] { "liked", "pl:p" }, PlanFixture.AllKeys(plan));
    }

    [Fact]
    public void Library_Grid_Strips()
    {
        // Fact 21. Seven albums in three columns: strips of 3, 3 and 1.
        var albums = Enumerable.Range(0, 7).Select(i => PlanFixture.Album("a" + i)).ToArray();
        var grid = Library(PlanFixture.Ops(new SetLibraryView(SidebarLibraryView.Grid)));
        var plan = Plan(grid, new SidebarProjectionInput(Library: albums), new SidebarPlanOptions(GridColumns: 3));
        Assert.Equal(new[] { 3, 3, 1 }, plan.Rows.Where(r => r.Kind == SidebarRowKind.GridStrip).Select(r => r.ItemCount).ToArray());
    }

    [Fact]
    public void Library_EmptyList_PlansNoEmptyRow()
    {
        // Fact 22. An empty list under the Albums chip plans no Empty row, with or without Liked and pins: the head owns it.
        var empty = new SidebarProjectionInput(Library: Array.Empty<SidebarLibraryEntry>(), LikedTitle: "Liked Songs");
        var withPins = empty with { Pins = [PlanFixture.Playlist("pl:p") with { IsPinned = true }] };

        Assert.DoesNotContain(Plan(Library(), empty, new SidebarPlanOptions(Filter: SidebarLibraryFilter.Albums)).Rows,
                              r => r.Kind == SidebarRowKind.Empty);
        Assert.DoesNotContain(Plan(Library(), empty).Rows, r => r.Kind == SidebarRowKind.Empty);
        Assert.DoesNotContain(Plan(Library(), withPins).Rows, r => r.Kind == SidebarRowKind.Empty);
    }

    static SidebarLayoutDoc GridDoc() => PlanFixture.Doc(SidebarLayoutId.Library,
        s => PlanFixture.Op(s, new SetLibraryView(SidebarLibraryView.Grid)));

    [Fact]
    public void Library_GridPending_NoRows()
    {
        // Pending with an EMPTY published list: no placeholder strips, no skeletons (the list view would show three).
        var input = new SidebarProjectionInput(Library: [], LibraryState: SidebarSourceState.Pending);
        var plan = Plan(GridDoc(), input, new SidebarPlanOptions(GridColumns: 3));
        Assert.DoesNotContain(plan.Rows, r => r.Kind is SidebarRowKind.GridStrip or SidebarRowKind.Skeleton);
    }

    [Fact]
    public void Library_GridPending_WithRows_KeepsStrips()
    {
        // One edge still pending (LibraryState is the worst of three) but rows already published: they stay (§3.2.10).
        var list = new[] { PlanFixture.Album("a"), PlanFixture.Album("b"), PlanFixture.Album("c"), PlanFixture.Album("d") };
        var input = new SidebarProjectionInput(Library: list, LibraryState: SidebarSourceState.Pending);
        var plan = Plan(GridDoc(), input, new SidebarPlanOptions(GridColumns: 3));
        Assert.Equal(2, plan.Rows.Count(r => r.Kind == SidebarRowKind.GridStrip));   // 3 + 1
    }

    [Fact]
    public void Library_PinMark_DepthZeroPinsOnly()
    {
        Assert.True(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Library, SidebarSectionKind.Pinned, 0, false, false));
        // A pinned folder's expanded child (planned in the pinned section at depth 1) is not a pin.
        Assert.False(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Library, SidebarSectionKind.Pinned, 1, false, false));
        // …unless it is itself pinned (the existing #85 rule).
        Assert.True(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Library, SidebarSectionKind.Pinned, 1, true, false));
        // Classic's pins sit under their header and keep today's rule; a track never shows the mark.
        Assert.False(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Classic, SidebarSectionKind.Pinned, 0, false, false));
        Assert.False(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Library, SidebarSectionKind.Library, 0, true, true));
    }

    [Fact]
    public void Library_PinnedFolder_ExpandsInline()
    {
        // Fact 22a. Pin folder f (tree: folder f → pl:a, pl:b), expanded in the wide pane: its children follow it, one level in.
        var folder = PlanFixture.Folder("f") with { IsPinned = true };
        var tree = new[] { PlanFixture.Folder("f"), PlanFixture.Playlist("pl:a", depth: 1, folder: "f"),
                           PlanFixture.Playlist("pl:b", depth: 1, folder: "f") };
        var input = new SidebarProjectionInput(Library: Array.Empty<SidebarLibraryEntry>(), PlaylistTree: tree, Pins: [folder],
                                               ExpandedFolders: new HashSet<string> { "f" });
        var plan = Plan(Library(), input);
        Assert.Equal(SidebarRowKind.FolderHeader, plan.Rows[0].Kind);
        Assert.Equal("pl:a", plan.Rows[1].Key);
        Assert.Equal(1, plan.Rows[1].Depth);
        Assert.Equal("pl:b", plan.Rows[2].Key);
    }

    [Fact]
    public void Library_PinnedFolder_NotInlineWhenDrilled()
    {
        // Fact 22a, narrow pane: the folder click drills instead, so nothing expands inline.
        var folder = PlanFixture.Folder("f") with { IsPinned = true };
        var tree = new[] { PlanFixture.Folder("f"), PlanFixture.Playlist("pl:a", depth: 1, folder: "f") };
        var input = new SidebarProjectionInput(Library: Array.Empty<SidebarLibraryEntry>(), PlaylistTree: tree, Pins: [folder],
                                               ExpandedFolders: new HashSet<string> { "f" });
        var plan = Plan(Library(), input, new SidebarPlanOptions(FoldersInline: false));
        Assert.DoesNotContain(plan.Rows, r => r.Key == "pl:a");   // the narrow pane drills (ActivateFolder) instead
    }

    [Fact]
    public void Library_Drilled_NoPins_NoLiked()
    {
        // Fact 22b. Drilled into folder f: the level is f's children only, no pin rows, no Liked row, no band even mid-drag.
        var pin = PlanFixture.Playlist("pl:p") with { IsPinned = true };
        var level = new[] { PlanFixture.Playlist("pl:a", depth: 0, folder: "f") };
        var input = new SidebarProjectionInput(Library: level, Pins: [pin], LikedTitle: "Liked Songs");
        var plan = Plan(Library(), input, new SidebarPlanOptions(FoldersInline: false, Drilled: true, PinDropArmed: true));
        Assert.Single(plan.Rows);
        Assert.Equal("pl:a", plan.Rows[0].Key);
        Assert.DoesNotContain(plan.Rows, r => r.Key == "pl:p" || r.Key == SidebarCatalogue.LikedRoute
                                              || r.Kind == SidebarRowKind.DropBand);
    }

    [Fact]
    public void Library_Drilled_PinnedChild_Once()
    {
        // Fact 22b. pl:a is pinned AND a child of f: drilled, the shaped level keeps it and the pin block is not planned,
        // so it shows exactly once.
        var pinA = PlanFixture.Playlist("pl:a", depth: 0, folder: "f") with { IsPinned = true };
        var input = new SidebarProjectionInput(Library: [pinA], Pins: [pinA]);
        var plan = Plan(Library(), input, new SidebarPlanOptions(FoldersInline: false, Drilled: true));
        Assert.Single(plan.Rows, r => r.Key == "pl:a");
    }

    [Fact]
    public void Library_CustomOrderTree_EndsWithGutter()
    {
        // Fact 23. Custom order IS the rootlist: its end gutter takes "move to the end" drops.
        var doc = Library(PlanFixture.Ops(new SetLibrarySort(SidebarLibrarySort.CustomOrder, false)));
        var input = new SidebarProjectionInput(Library: [PlanFixture.Playlist("pl:a"), PlanFixture.Playlist("pl:b")], LibraryIsTree: true);
        var plan = Plan(doc, input);
        Assert.Equal(SidebarRowKind.TreeEnd, plan.Rows[^1].Kind);
    }

    [Fact]
    public void Library_RecentsSort_NoGutter()
    {
        // Fact 23, the other half: a recents-sorted list has no gutter.
        var input = new SidebarProjectionInput(Library: [PlanFixture.Playlist("pl:a"), PlanFixture.Playlist("pl:b")], LibraryIsTree: true);
        var plan = Plan(Library(), input);
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.TreeEnd);
    }

    [Fact]
    public void Library_Compact_TopLevelOnly()
    {
        // Fact 24. The rail tiles the top level: a nested playlist is not a tile, and the Liked tile stays.
        var input = new SidebarProjectionInput(Library: [PlanFixture.Playlist("pl:a", depth: 0), PlanFixture.Playlist("pl:b", depth: 1)],
                                               LikedTitle: "Liked Songs");
        var plan = Plan(Library(), input, new SidebarPlanOptions(Mode: SidebarPaneMode.Compact));
        Assert.Equal(new[] { "liked", "pl:a" }, PlanFixture.AllKeys(plan));
    }

    [Fact]
    public void LikedAppearsOncePerLayout_Library()
    {
        // Fact 26. Library: Liked is one entity row, never a glyph row.
        var plan = Plan(Library(), new SidebarProjectionInput(LikedTitle: "Liked Songs"));
        Assert.Single(plan.Rows, r => r.Kind == SidebarRowKind.EntityRow && r.Key == "liked");
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.IconRow && r.Key == "liked");
    }

    // ── shared ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildSection_ReturnsTheSectionsRows_TopLevel()
    {
        // Fact 25. A section's flyout is its top level: a folder header, its child not drawn, and no headers or gutters.
        var tree = new[] { PlanFixture.Folder("f"), PlanFixture.Playlist("pl:a", depth: 1, folder: "f"), PlanFixture.Playlist("pl:b") };
        var plan = SidebarRowPlanner.BuildSection(Classic(), "playlists", new SidebarProjectionInput(PlaylistTree: tree));
        Assert.Equal(new[] { SidebarRowKind.FolderHeader, SidebarRowKind.EntityRow }, PlanFixture.Kinds(plan));
        Assert.DoesNotContain(plan.Rows, r => r.Depth > 0);
    }
}
#endregion

#region ROW GEOMETRY — SidebarPlanGeometryTests
// SidebarRowGeometry is the engine-free half of the sidebar's row ladder. The plan helpers under test are the pure geometry
// the selection cue and the drop caret need: the tree's content origin, the row ladder's heights, the cumulative content-space
// Y, the route → index lookup, the travel direction, the grid fallback and the pin glyph.
public sealed class SidebarPlanGeometryTests
{
    // ── 0. THE TREE-CONTENT ORIGIN (the caret's x) ────────────────────────────────────────────────────────────────────
    //
    // A tree row's content origin is `TreeContentX(depth)` = `IndentFor(depth)`: one 31-DIP level per folder, no gutter
    // and no connector cells. The folder's chevron lives in the row's TRAILING cluster.

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(1, 31f)]
    [InlineData(2, 62f)]
    [InlineData(3, 93f)]
    [InlineData(4, 93f)]     // past MaxIndentDepth the ladder stops marching right, exactly like IndentFor
    [InlineData(-3, 0f)]
    public void TreeContentX_IsThirtyOnePerLevelFromTheRowOrigin(int depth, float expected)
    {
        Assert.Equal(expected, SidebarRowGeometry.TreeContentX(depth), 3);
        // …and it IS a sum of the named constants, not a literal that happens to match.
        int clamped = Math.Clamp(depth, 0, SidebarRowGeometry.MaxIndentDepth);
        Assert.Equal(SidebarRowGeometry.IndentFor(0) + clamped * SidebarRowGeometry.IndentStep,
                     SidebarRowGeometry.TreeContentX(depth), 3);
    }

    [Fact]
    public void TreeContentX_MarchesOneIndentStepPerLevel()
    {
        // The step the depth pick reads backwards. If these two ever differ, an outdent lands on the wrong level.
        for (int d = 0; d < SidebarRowGeometry.MaxIndentDepth; d++)
            Assert.Equal(SidebarRowGeometry.IndentStep,
                         SidebarRowGeometry.TreeContentX(d + 1) - SidebarRowGeometry.TreeContentX(d), 3);
    }

    // ── 1. the row ladder (WinUI NavigationView) ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarRowShape.Glyph, 36f, 40f)]
    [InlineData(SidebarRowShape.EntityOneLine, 36f, 40f)]
    [InlineData(SidebarRowShape.EntityTwoLine, 40f, 44f)]
    public void HeightAndPitch_AreTheThreeWinUiShapes(SidebarRowShape shape, float height, float pitch)
    {
        Assert.Equal(height, SidebarRowGeometry.HeightOf(shape));
        Assert.Equal(pitch, SidebarRowGeometry.PitchOf(shape));
    }

    [Theory]
    [InlineData(-1, 0f)]
    [InlineData(0, 0f)]
    [InlineData(1, 31f)]
    [InlineData(4, 93f)]
    [InlineData(9, 93f)]   // clamped at three levels
    public void IndentFor_IsThirtyOnePerLevelClampedAtThree(int depth, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.IndentFor(depth));

    // ── 2. pure plan geometry ────────────────────────────────────────────────────────────────────────────────────────

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
        // A zero-height row and a NaN (an unmeasured slot) must contribute nothing rather than poisoning every later offset.
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
        // The narrow-pane case: at the 180-DIP floor (164 DIP available after the 16-DIP pane inset), 4 planner columns would
        // give (164 - 24) / 4 = 35 — under the 40-DIP floor — so the strip falls back to 3.
        Assert.Equal(3, SidebarRowGeometry.GridFallbackColumns(4, 164f, 8f, 40f));
        // A 2-column section never needed the fallback in the first place.
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
        // Even one column's edge can go under the floor at an extreme width — the fallback never returns less than 1.
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
// THE ANALYTIC ROW LADDER (SidebarRowExtents). The pane feeds these numbers to the virtualizing host as its per-row extent
// SEED, so they are what the content extent, scroll anchor and drop placement are computed from before anything realizes.
public sealed class SidebarRowExtentsTests
{
    static SidebarRow R(SidebarRowKind kind, string key, int entryIndex = -1)
        => new(kind, "s", 0, entryIndex, 0, key);

    [Fact]
    public void Extents_Header40_Separator8_Entity44_Glyph40_DropBand40()
    {
        // Fact 27. An entity section (two-line rows): the header, the separator, an entity row, a glyph row and the drop band.
        var section = new SidebarSection(SidebarSectionKind.Playlists, false, false, 0, SidebarRowShape.EntityTwoLine,
                                         Array.Empty<string>());
        var rows = new[]
        {
            R(SidebarRowKind.SectionHeader, "playlists"),
            R(SidebarRowKind.Divider, "d"),
            R(SidebarRowKind.EntityRow, "pl:a", entryIndex: 0),
            R(SidebarRowKind.IconRow, "liked"),
            R(SidebarRowKind.DropBand, "pinned"),
        };
        Assert.Equal(40f, SidebarRowExtents.HeightOf(rows, 0, section));
        Assert.Equal(8f, SidebarRowExtents.HeightOf(rows, 1, section));
        Assert.Equal(44f, SidebarRowExtents.HeightOf(rows, 2, section));
        Assert.Equal(40f, SidebarRowExtents.HeightOf(rows, 3, section));
        Assert.Equal(40f, SidebarRowExtents.HeightOf(rows, 4, section));
    }
}
#endregion

#region ROW DIFF — SidebarRowDiffTests
// The per-row change marks the pane uses to decide which slots re-realize. A row is changed when its record moved, OR when
// the entry it addresses changed under an identical record (the regression this type exists for).
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

#region PANE INVARIANT — SidebarPaneInvariantTests
// The settled pane observation (§P2.9): the presentation as decided, plus the column's laid-out width. `Layout` names the
// layout the pane shows (Classic or Your Library).
public class SidebarPaneInvariantTests
{
    static SidebarPaneFrameSnapshot Expanded(float preferred = 320f, float presented = 320f, float rendered = 320f) => new(
        Layout: SidebarLayoutId.Classic,
        Mode: SidebarPaneMode.Expanded,
        Band: SidebarWindowBand.Wide,
        UserCollapsed: false,
        OverlayOpen: false,
        PreferredExpandedWidth: preferred,
        PresentedWidth: presented,
        RenderedPaneWidth: rendered);

    static SidebarPaneFrameSnapshot Compact(float rendered = 48f, SidebarWindowBand band = SidebarWindowBand.Wide) => new(
        Layout: SidebarLayoutId.Library,
        Mode: SidebarPaneMode.Compact,
        Band: band,
        UserCollapsed: true,
        OverlayOpen: false,
        PreferredExpandedWidth: 340f,
        PresentedWidth: rendered,
        RenderedPaneWidth: rendered);

    static SidebarPaneFrameSnapshot Minimal(float rendered = 0f) => new(
        Layout: SidebarLayoutId.Classic,
        Mode: SidebarPaneMode.Minimal,
        Band: SidebarWindowBand.Tiny,
        UserCollapsed: false,
        OverlayOpen: false,
        PreferredExpandedWidth: 320f,
        PresentedWidth: 0f,
        RenderedPaneWidth: rendered);

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
        Assert.Equal(SidebarPaneInvariantFault.None, SidebarPaneInvariant.Inspect(in state));
    }

    [Fact]
    public void MinimalTerminalState_IsValidOnlyWhenEmpty()
    {
        var empty = Minimal(rendered: 0f);
        Assert.Equal(SidebarPaneInvariantFault.None, SidebarPaneInvariant.Inspect(in empty));

        var sliver = Minimal(rendered: 12f);
        Assert.True(SidebarPaneInvariant.Inspect(in sliver).HasFlag(SidebarPaneInvariantFault.MinimalNotEmpty));
    }

    [Fact]
    public void A_rail_at_fifty_six_is_not_the_forty_eight_dip_strip()
    {
        var state = Compact(rendered: 56f);
        Assert.True(SidebarPaneInvariant.Inspect(in state).HasFlag(SidebarPaneInvariantFault.RailWidthMismatch));
    }

    [Fact]
    public void ReportedTwentyFourDipSliver_IsRejected()
    {
        // Presented AND rendered at 24: below the expanded floor.
        var sliver = Expanded(presented: 24f, rendered: 24f);
        Assert.True(SidebarPaneInvariant.Inspect(in sliver).HasFlag(SidebarPaneInvariantFault.ExpandedWidthOutOfRange));

        // Presented 320 but the column laid out at 24: the rendered width disagrees with the presented one.
        var mismatch = Expanded(rendered: 24f);
        Assert.True(SidebarPaneInvariant.Inspect(in mismatch).HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Theory]
    [InlineData(47.49f, false)]
    [InlineData(47.5f, true)]
    [InlineData(48.5f, true)]
    [InlineData(48.51f, false)]
    public void RailWidth_UsesHalfDipTolerance(float rendered, bool valid)
    {
        var state = Compact(rendered);
        Assert.Equal(valid, SidebarPaneInvariant.Inspect(in state) == SidebarPaneInvariantFault.None);
    }

    [Fact]
    public void WindowBandAndModeMustAgree()
    {
        // A Narrow window is a forced rail: an expanded pane there is a settle that never happened.
        var inline = Expanded() with { Band = SidebarWindowBand.Narrow };
        Assert.True(SidebarPaneInvariant.Inspect(in inline).HasFlag(SidebarPaneInvariantFault.ModeBandMismatch));

        // A Tiny window is the drawer: a rail there is the same mismatch.
        var rail = Compact(band: SidebarWindowBand.Tiny);
        Assert.True(SidebarPaneInvariant.Inspect(in rail).HasFlag(SidebarPaneInvariantFault.ModeBandMismatch));

        // The forced rail in Narrow is valid.
        var forced = Compact(band: SidebarWindowBand.Narrow);
        Assert.Equal(SidebarPaneInvariantFault.None, SidebarPaneInvariant.Inspect(in forced));
    }

    [Fact]
    public void RenderedWidthMustMatchThePresentedWidth()
    {
        var state = Expanded(preferred: 320f, presented: 320f, rendered: 300f);
        Assert.True(SidebarPaneInvariant.Inspect(in state)
            .HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Fact]
    public void PresentedMayYieldBelowTheStoredPreference()
    {
        // The window yield: preferred 300, presented (and rendered) 276 — valid, the preference is not rewritten.
        var state = Expanded(preferred: 300f, presented: 276f, rendered: 276f);
        Assert.Equal(SidebarPaneInvariantFault.None, SidebarPaneInvariant.Inspect(in state));
    }

    [Fact]
    public void PresentedMayNotExceedPreferred()
    {
        var state = Expanded(preferred: 280f, presented: 320f, rendered: 320f);
        Assert.True(SidebarPaneInvariant.Inspect(in state).HasFlag(SidebarPaneInvariantFault.PresentedExceedsPreferred));
    }

    [Fact]
    public void NonFiniteGeometry_IsRejectedWithoutFurtherClassification()
    {
        var state = Expanded() with { RenderedPaneWidth = float.NaN };
        Assert.Equal(SidebarPaneInvariantFault.NonFiniteValue, SidebarPaneInvariant.Inspect(in state));
    }

    // ── THE ONE CONTENT LANE ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A NESTED row indents from the row origin, so a depth-1 child sits exactly one 31-DIP level inside it,
    /// and the ladder stops at three levels.</summary>
    [Fact]
    public void NestedRowsIndentOneStepPerLevel()
    {
        Assert.Equal(SidebarRowGeometry.IndentStep, SidebarRowGeometry.IndentFor(1));
        Assert.Equal(SidebarRowGeometry.IndentFor(3), SidebarRowGeometry.IndentFor(9));   // clamped at 3 levels
    }
}
#endregion
