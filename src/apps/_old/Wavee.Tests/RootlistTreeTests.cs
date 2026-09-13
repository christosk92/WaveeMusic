using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Flat rootlist marker stream (playlist uris + start/end-group markers) → the sidebar PlaylistNode tree.
// Folders are RECURSIVE (locked decision 12): an end-group pushes a folder NODE into its parent instead of flattening its
// children up one level. These assertions pin the nested shape, the malformed-marker behaviour, and — critically — the
// flat-consumer guarantee that made the recursion change safe to land.
public class RootlistTreeBuilderTests
{
    static PlaylistSummary Resolve(string uri) => new(uri, "Name-" + uri.Split(':')[^1], "Owner", 0, null);

    static ColdRootlistEntry Cold(int pos, int kind, string uri, string? group = null, int depth = 0)
        => new(pos, kind, uri, group, depth);

    static string Start(string id, string name) => "spotify:start-group:" + id + ":" + name;
    static string End(string id) => "spotify:end-group:" + id;
    static string Pl(string id) => "spotify:playlist:" + id;

    static PlaylistFolder Folder(PlaylistNode n) => Assert.IsType<PlaylistFolder>(n);
    static PlaylistLeaf Leaf(PlaylistNode n) => Assert.IsType<PlaylistLeaf>(n);

    [Fact]
    public void TopLevelPlaylists_BecomeLeaves()
    {
        var entries = new[]
        {
            Cold(0, 0, Pl("p1")),
            Cold(1, 0, Pl("p2")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        Assert.Equal(2, tree.Count);
        Assert.Equal(Pl("p1"), Leaf(tree[0]).Playlist.Uri);
        Assert.Equal(Pl("p2"), Leaf(tree[1]).Playlist.Uri);
    }

    [Fact]
    public void Folder_GroupsItsPlaylists_AndLeavesFollow()
    {
        var entries = new[]
        {
            Cold(0, 1, Start("g1", "My%20Folder"), "My Folder"),
            Cold(1, 0, Pl("p1"), depth: 1),
            Cold(2, 0, Pl("p2"), depth: 1),
            Cold(3, 2, End("g1")),
            Cold(4, 0, Pl("p3")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        Assert.Equal(2, tree.Count);
        var folder = Folder(tree[0]);
        Assert.Equal("My Folder", folder.Name);
        Assert.Equal("g1", folder.Id);
        Assert.Equal(2, folder.Items.Count);
        Assert.Equal(Pl("p1"), Leaf(folder.Items[0]).Playlist.Uri);
        Assert.Equal(Pl("p2"), Leaf(folder.Items[1]).Playlist.Uri);
        Assert.Equal(Pl("p3"), Leaf(tree[1]).Playlist.Uri);
    }

    // Desktop encodes a folder name with SPACE AS `+` (captured a164 "New+Folder", b037 "named+folder+update", b128
    // "root+folder+updated+name"); a literal + travels as %2B. The label must show spaces, never the pluses.
    [Theory]
    [InlineData("New+Folder", "New Folder")]
    [InlineData("root+folder+updated+name", "root folder updated name")]
    [InlineData("A%2BB+C", "A+B C")]
    [InlineData("caf%C3%A9+mix", "café mix")]
    [InlineData("has%3Acolon+too", "has:colon too")]
    public void FolderName_DecodesPlusAsSpace_AndPercentEscapes(string wire, string expected)
    {
        var entries = RootlistTreeBuilder.EntriesFromUris(new[] { Start("g1", wire), End("g1") });
        Assert.Equal(expected, entries[0].GroupName);
    }

    [Fact]
    public void NestedFolder_IsAFolderInsideAFolder()
    {
        // The pre-recursion builder flattened the inner group's playlists into the outer folder and dropped the inner
        // folder entirely. It must now survive as a real child node, in position.
        var entries = new[]
        {
            Cold(0, 1, Start("outer", "Outer"), "Outer"),
            Cold(1, 0, Pl("a")),
            Cold(2, 1, Start("inner", "Inner"), "Inner"),
            Cold(3, 0, Pl("b")),
            Cold(4, 2, End("inner")),
            Cold(5, 0, Pl("c")),
            Cold(6, 2, End("outer")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);

        var outer = Folder(Assert.Single(tree));
        Assert.Equal("Outer", outer.Name);
        Assert.Equal(3, outer.Items.Count);                                  // a · [Inner] · c — order preserved
        Assert.Equal(Pl("a"), Leaf(outer.Items[0]).Playlist.Uri);
        var inner = Folder(outer.Items[1]);
        Assert.Equal("Inner", inner.Name);
        Assert.Equal(Pl("b"), Leaf(Assert.Single(inner.Items)).Playlist.Uri);
        Assert.Equal(Pl("c"), Leaf(outer.Items[2]).Playlist.Uri);
    }

    [Fact]
    public void FolderContainingOnlyAFolder_IsNotCollapsed()
    {
        var entries = new[]
        {
            Cold(0, 1, Start("outer", "Outer"), "Outer"),
            Cold(1, 1, Start("inner", "Inner"), "Inner"),
            Cold(2, 0, Pl("b")),
            Cold(3, 2, End("inner")),
            Cold(4, 2, End("outer")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        var outer = Folder(Assert.Single(tree));
        var inner = Folder(Assert.Single(outer.Items));
        Assert.Equal("Inner", inner.Name);
        Assert.Single(inner.Items);
    }

    [Fact]
    public void ThreeLevelNesting_PreservesDepth()
    {
        var entries = new[]
        {
            Cold(0, 1, Start("l1", "L1"), "L1"),
            Cold(1, 1, Start("l2", "L2"), "L2"),
            Cold(2, 1, Start("l3", "L3"), "L3"),
            Cold(3, 0, Pl("deep")),
            Cold(4, 2, End("l3")),
            Cold(5, 2, End("l2")),
            Cold(6, 2, End("l1")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);

        var l1 = Folder(Assert.Single(tree));
        var l2 = Folder(Assert.Single(l1.Items));
        var l3 = Folder(Assert.Single(l2.Items));
        Assert.Equal("L1", l1.Name);
        Assert.Equal("L2", l2.Name);
        Assert.Equal("L3", l3.Name);
        Assert.Equal(Pl("deep"), Leaf(Assert.Single(l3.Items)).Playlist.Uri);
    }

    [Fact]
    public void EndGroupWithoutStart_IsIgnored()
    {
        var entries = new[]
        {
            Cold(0, 2, End("ghost")),
            Cold(1, 0, Pl("p1")),
            Cold(2, 2, End("ghost2")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        Assert.Equal(Pl("p1"), Leaf(Assert.Single(tree)).Playlist.Uri);       // no folder invented, nothing thrown
    }

    [Fact]
    public void StartGroupWithoutEnd_StillFlushes()
    {
        var entries = new[]
        {
            Cold(0, 1, Start("g1", "F"), "F"),
            Cold(1, 0, Pl("p1"), depth: 1),
            // missing end-group → must still surface the folder with its child
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        Assert.Single(Folder(Assert.Single(tree)).Items);
    }

    [Fact]
    public void UnbalancedNestedFolders_FlushInnermostFirst_IntoTheirParent()
    {
        // Two opens, no closes: the flush must rebuild the nesting (inner INSIDE outer), not dump two sibling folders at
        // the top level.
        var entries = new[]
        {
            Cold(0, 1, Start("outer", "Outer"), "Outer"),
            Cold(1, 0, Pl("a")),
            Cold(2, 1, Start("inner", "Inner"), "Inner"),
            Cold(3, 0, Pl("b")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);

        var outer = Folder(Assert.Single(tree));
        Assert.Equal(2, outer.Items.Count);
        Assert.Equal(Pl("a"), Leaf(outer.Items[0]).Playlist.Uri);
        var inner = Folder(outer.Items[1]);
        Assert.Equal("Inner", inner.Name);
        Assert.Equal(Pl("b"), Leaf(Assert.Single(inner.Items)).Playlist.Uri);
    }

    [Fact]
    public void InterleavedMarkers_DoNotThrow()
    {
        // A crossed / mismatched stream (an end whose id is not the innermost open group, a stray end at the end): the
        // builder closes the innermost group regardless and never throws.
        var entries = new[]
        {
            Cold(0, 1, Start("g1", "One"), "One"),
            Cold(1, 1, Start("g2", "Two"), "Two"),
            Cold(2, 2, End("g1")),          // closes g2 (innermost) — ids are not matched
            Cold(3, 0, Pl("p1")),
            Cold(4, 2, End("g2")),          // closes g1
            Cold(5, 2, End("g1")),          // stray
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        var one = Folder(Assert.Single(tree));
        Assert.Equal("One", one.Name);
        Assert.Equal(2, one.Items.Count);                                    // [Two] · p1
        Assert.Equal("Two", Folder(one.Items[0]).Name);
        Assert.Equal(Pl("p1"), Leaf(one.Items[1]).Playlist.Uri);
    }

    [Fact]
    public void DuplicateGroupIds_AreKeptDistinctByPosition()
    {
        var entries = new[]
        {
            Cold(0, 1, Start("dup", "First"), "First"),
            Cold(1, 0, Pl("a")),
            Cold(2, 2, End("dup")),
            Cold(3, 1, Start("dup", "Second"), "Second"),
            Cold(4, 0, Pl("b")),
            Cold(5, 2, End("dup")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        Assert.Equal(2, tree.Count);
        Assert.Equal("First", Folder(tree[0]).Name);
        Assert.Equal("Second", Folder(tree[1]).Name);
        Assert.Equal(Pl("a"), Leaf(Assert.Single(Folder(tree[0]).Items)).Playlist.Uri);
        Assert.Equal(Pl("b"), Leaf(Assert.Single(Folder(tree[1]).Items)).Playlist.Uri);
    }

    [Fact]
    public void DepthFieldDisagreesWithMarkers_MarkersWin()
    {
        // Every row claims depth 7 (or 0) — a malformed depth column must not shape the tree. Nesting comes from the
        // start/end markers alone.
        var entries = new[]
        {
            Cold(0, 1, Start("outer", "Outer"), "Outer", depth: 7),
            Cold(1, 1, Start("inner", "Inner"), "Inner", depth: 0),
            Cold(2, 0, Pl("b"), depth: 7),
            Cold(3, 2, End("inner"), depth: 7),
            Cold(4, 2, End("outer"), depth: 0),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        var outer = Folder(Assert.Single(tree));
        var inner = Folder(Assert.Single(outer.Items));
        Assert.Equal(Pl("b"), Leaf(Assert.Single(inner.Items)).Playlist.Uri);
    }

    [Fact]
    public void EmptyFolder_SurvivesAsAnEmptyFolderNode()
    {
        var entries = new[]
        {
            Cold(0, 1, Start("empty", "Empty"), "Empty"),
            Cold(1, 2, End("empty")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        var folder = Folder(Assert.Single(tree));
        Assert.Equal("Empty", folder.Name);
        Assert.Empty(folder.Items);
    }

    [Fact]
    public void NonPlaylistUriInsideGroup_IsSkipped()
    {
        var entries = new[]
        {
            Cold(0, 1, Start("g1", "F"), "F"),
            Cold(1, 0, "spotify:album:al1"),
            Cold(2, 0, Pl("p1")),
            Cold(3, 0, "spotify:show:sh1"),
            Cold(4, 2, End("g1")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        var folder = Folder(Assert.Single(tree));
        Assert.Equal(Pl("p1"), Leaf(Assert.Single(folder.Items)).Playlist.Uri);
    }

    [Fact]
    public void LiveRootlistEntries_BuildTheSameTreeAsColdRows()
    {
        // StoreLibrarySource reads the LIVE RootlistEntry rows; the overload must not fork the parse.
        var cold = new[]
        {
            Cold(0, 1, Start("g1", "F"), "F"),
            Cold(1, 0, Pl("p1"), depth: 1),
            Cold(2, 2, End("g1")),
            Cold(3, 0, Pl("p2")),
        };
        var live = new[]
        {
            new RootlistEntry(0, 1, Start("g1", "F"), "F", 0),
            new RootlistEntry(1, 0, Pl("p1"), null, 1),
            new RootlistEntry(2, 2, End("g1"), null, 0),
            new RootlistEntry(3, 0, Pl("p2"), null, 0),
        };
        var a = RootlistTreeBuilder.Build(cold, Resolve);
        var b = RootlistTreeBuilder.Build(live, Resolve);
        Assert.Equal(SidebarTree.Flatten(a).Count, SidebarTree.Flatten(b).Count);
        Assert.Equal(Folder(a[0]).Name, Folder(b[0]).Name);
        Assert.Equal(Leaf(a[1]).Playlist.Uri, Leaf(b[1]).Playlist.Uri);
    }

    // ── P3: the marker rows carry their ADD timestamp ────────────────────────────────────────────────────────────────
    // A folder RENAME has to resend the marker's ORIGINAL create timestamp (golden b037), so the value has to survive
    // the one parse every rootlist path shares. Positional against the uris; a row without one stays at 0 ("not
    // captured"), which is the state the rename path bootstraps rather than papers over.
    [Fact]
    public void EntriesFromUris_CarryTheAddTimestamps_PositionallyAndSafely()
    {
        string[] uris = [Start("g1", "F"), Pl("p1"), End("g1"), Pl("p2")];
        var entries = RootlistTreeBuilder.EntriesFromUris(uris, new long[] { 11, 22, 33 });   // deliberately short

        Assert.Equal(11L, entries[0].AddedAtMs);
        Assert.Equal(22L, entries[1].AddedAtMs);
        Assert.Equal(33L, entries[2].AddedAtMs);
        Assert.Equal(0L, entries[3].AddedAtMs);                       // beyond the supplied stamps → "not captured"
        Assert.Equal(0L, RootlistTreeBuilder.EntriesFromUris(uris)[0].AddedAtMs);   // the 1-arg overload stays at 0

        // the rest of the parse is unchanged by the extra column
        Assert.Equal([1, 0, 2, 0], System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(entries, e => e.Kind)));
        Assert.Equal([0, 1, 0, 0], System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(entries, e => e.Depth)));
        Assert.Equal("F", entries[0].GroupName);
    }


    // ── the migration guard ───────────────────────────────────────────────────────────────────────────────────────────
    // PlaylistFolder.Items changing from IReadOnlyList<PlaylistSummary> to IReadOnlyList<PlaylistNode> is a BREAKING
    // change for every flat consumer (Menus.AddToPlaylistItem, the Classic playlist list, LibraryStore.Playlists).
    // SidebarTree.Flatten is the bridge that keeps them correct; this is the test that says so.
    [Fact]
    public void FlatConsumers_StillSeeEveryPlaylist()
    {
        var entries = new[]
        {
            Cold(0, 0, Pl("top1")),
            Cold(1, 1, Start("outer", "Outer"), "Outer"),
            Cold(2, 0, Pl("in1")),
            Cold(3, 1, Start("inner", "Inner"), "Inner"),
            Cold(4, 0, Pl("deep1")),
            Cold(5, 1, Start("deepest", "Deepest"), "Deepest"),
            Cold(6, 0, Pl("deep2")),
            Cold(7, 2, End("deepest")),
            Cold(8, 2, End("inner")),
            Cold(9, 0, Pl("in2")),
            Cold(10, 2, End("outer")),
            Cold(11, 0, Pl("top2")),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);

        var flat = SidebarTree.Flatten(tree);
        Assert.Equal(
            new[] { Pl("top1"), Pl("in1"), Pl("deep1"), Pl("deep2"), Pl("in2"), Pl("top2") },
            Uris(flat));
        Assert.Equal(6, SidebarTree.CountLeaves(tree));

        // The append-into overload is the allocation-free shape the hot consumers use; same result, caller-owned buffer.
        var buffer = new List<PlaylistSummary>();
        SidebarTree.Flatten(tree, buffer);
        Assert.Equal(Uris(flat), Uris(buffer));

        // And a folder is still findable by its rootlist group id at any depth.
        Assert.Equal("Deepest", Assert.IsType<PlaylistFolder>(SidebarTree.FindFolder(tree, "deepest")).Name);
        Assert.Null(SidebarTree.FindFolder(tree, "nope"));
    }

    [Fact]
    public void FromFlat_RoundTripsAFolderlessSource()
    {
        var flat = new[] { Resolve(Pl("a")), Resolve(Pl("b")) };
        var tree = SidebarTree.FromFlat(flat);
        Assert.Equal(2, tree.Count);
        Assert.Equal(Uris(flat), Uris(SidebarTree.Flatten(tree)));
    }

    static string[] Uris(IReadOnlyList<PlaylistSummary> list)
    {
        var a = new string[list.Count];
        for (int i = 0; i < list.Count; i++) a[i] = list[i].Uri;
        return a;
    }
}
