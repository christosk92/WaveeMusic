using System.Collections.Generic;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── SidebarPillRules: exactly one selection pill, on the most specific place that still shows the route ──────────────

public sealed class SidebarPillRulesTests
{
    static SidebarRow Row(SidebarRowKind kind, string key, string section = "s", int depth = 0, int entry = -1)
        => new(kind, section, (byte)depth, entry, 0, key);

    static SidebarLibraryEntry Folder(string id, int depth, int order = 0)
        => new(Id: "folder:" + id, Kind: SidebarEntryKind.Folder, Uri: "", Name: id, Creator: "", Cover: default,
            MosaicTiles: null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: order,
            Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None) { FolderId = id, FolderName = id };

    static SidebarLibraryEntry Playlist(string id, int depth, int order = 0)
        => new(Id: "pl:" + id, Kind: SidebarEntryKind.Playlist, Uri: "spotify:playlist:" + id, Name: id, Creator: "",
            Cover: default, MosaicTiles: null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: order, Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None);

    /// <summary>The pane's row → route projection: an entity row addresses its route by its key, nothing else does.</summary>
    static System.Func<int, string?> RoutesOf(IReadOnlyList<SidebarRow> rows)
        => i => rows[i].Kind == SidebarRowKind.EntityRow ? rows[i].Key : null;

    static readonly IReadOnlyList<string> NoAncestors = new List<string>();

    [Fact]
    public void VisibleRow_TakesThePill()
    {
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.SectionHeader, "s"),
            Row(SidebarRowKind.EntityRow, "pl:a", entry: 0),
        };
        var entries = new List<SidebarLibraryEntry> { Playlist("a", 0) };

        var pill = SidebarPillRules.Resolve(rows, entries, RoutesOf(rows), "pl:a", NoAncestors, "s");

        Assert.Equal(new SidebarPillTarget(1, SidebarPillAnchor.Row), pill);
    }

    [Fact]
    public void VisibleRow_BeatsAnAncestorFolderAndTheSectionHeader()
    {
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.SectionHeader, "s"),
            Row(SidebarRowKind.FolderHeader, "f1", entry: 0),
            Row(SidebarRowKind.EntityRow, "pl:x", depth: 1, entry: 1),
        };
        var entries = new List<SidebarLibraryEntry> { Folder("f1", 0), Playlist("x", 1) };
        var ancestors = new List<string> { "f1" };

        var pill = SidebarPillRules.Resolve(rows, entries, RoutesOf(rows), "pl:x", ancestors, "s");

        Assert.Equal(new SidebarPillTarget(2, SidebarPillAnchor.Row), pill);
    }

    [Fact]
    public void CollapsedFolder_PillOnTheDeepestVisibleAncestor()
    {
        // f1 ⊃ f2 ⊃ pl:x, with f2 collapsed: the plan shows only the two folder rows.
        var tree = new List<SidebarLibraryEntry> { Folder("f1", 0), Folder("f2", 1), Playlist("x", 2) };
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.FolderHeader, "f1", entry: 0),
            Row(SidebarRowKind.FolderHeader, "f2", entry: 1),
        };
        var ancestors = new List<string>();
        SidebarPillRules.AncestorFolders(tree, "pl:x", ancestors);

        var pill = SidebarPillRules.Resolve(rows, tree, RoutesOf(rows), "pl:x", ancestors, "s");

        Assert.Equal(new SidebarPillTarget(1, SidebarPillAnchor.AncestorFolder), pill);
    }

    [Fact]
    public void CollapsedSection_PillOnItsHeader()
    {
        var rows = new List<SidebarRow> { Row(SidebarRowKind.SectionHeader, "s1", section: "s1") };

        var pill = SidebarPillRules.Resolve(rows, new List<SidebarLibraryEntry>(), RoutesOf(rows), "pl:gone",
            NoAncestors, "s1");

        Assert.Equal(new SidebarPillTarget(0, SidebarPillAnchor.SectionHeader), pill);
    }

    [Fact]
    public void HiddenSection_NoPill()
    {
        var rows = new List<SidebarRow> { Row(SidebarRowKind.SectionHeader, "other", section: "other") };

        var pill = SidebarPillRules.Resolve(rows, new List<SidebarLibraryEntry>(), RoutesOf(rows), "pl:gone",
            NoAncestors, null);

        Assert.Equal(SidebarPillTarget.None, pill);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyRoute_NoPill(string? route)
    {
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.SectionHeader, "s"),
            Row(SidebarRowKind.EntityRow, "", entry: 0),
        };

        var pill = SidebarPillRules.Resolve(rows, new List<SidebarLibraryEntry>(), RoutesOf(rows), route, NoAncestors, "s");

        Assert.Equal(SidebarPillTarget.None, pill);
    }

    [Fact]
    public void AncestorFolders_DeepestFirst_SkipsSiblings()
    {
        // f1 ⊃ { pl:y, f2 ⊃ pl:x }: pl:y is a sibling of f2, so it is never an ancestor of pl:x.
        var tree = new List<SidebarLibraryEntry> { Folder("f1", 0), Playlist("y", 1), Folder("f2", 1), Playlist("x", 2) };
        var into = new List<string> { "stale" };

        SidebarPillRules.AncestorFolders(tree, "pl:x", into);

        Assert.Equal(new[] { "f2", "f1" }, into);
    }

    [Fact]
    public void AncestorFolders_OfATopLevelRoute_IsEmpty()
    {
        var tree = new List<SidebarLibraryEntry> { Folder("f1", 0), Playlist("a", 0) };
        var into = new List<string>();

        SidebarPillRules.AncestorFolders(tree, "pl:a", into);

        Assert.Empty(into);
    }

    [Fact]
    public void Pill_SlidesOnlyWithinOneContainerAtOneDepth()
    {
        Assert.True(SidebarPillMotionRules.Slides(SidebarPillLane.List, SidebarPillLane.List, 0f));
        Assert.False(SidebarPillMotionRules.Slides(SidebarPillLane.List, SidebarPillLane.List, 31f));     // a depth change scales
        Assert.False(SidebarPillMotionRules.Slides(SidebarPillLane.Head, SidebarPillLane.List, 0f));      // head ↔ list: no slide
        Assert.False(SidebarPillMotionRules.Slides(SidebarPillLane.List, SidebarPillLane.Footer, 0f));    // list ↔ footer: no slide
        Assert.True(SidebarPillMotionRules.Slides(SidebarPillLane.Head, SidebarPillLane.Head, 0f));       // Home ↔ dropdown worms
    }
}
