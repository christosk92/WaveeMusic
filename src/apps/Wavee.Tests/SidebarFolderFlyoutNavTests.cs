using System;
using System.Collections.Generic;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// The PURE half of the collapsed rail's folder flyout: which rows a level shows, and what push/pop/back mean.
//
// The flyout itself is engine-bound (a Component in Features\Sidebar\Pane\), so it is deliberately NOT reachable here —
// exactly the split `SidebarNavBandModel` / `RootlistTreeNav` already use. What IS reachable is every rule the component
// renders, which is the whole point of factoring the drill-in out of it: the concert date flyout could keep its model in
// one `Signal<int>` because it has two levels and a fixed root; a folder chain is unbounded and grew edge cases.
public sealed class SidebarFolderFlyoutNavTests
{
    // ── direct children ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shared fixture's tree: a · [Chill g]{b, c, [Deep k]{f}} · d · [Trailing h]{e}.</summary>
    [Fact]
    public void Children_AreDirectOnly_AndKeepRootlistOrder()
    {
        var tree = SidebarTreeFixture.Tree();
        var into = new List<SidebarLibraryEntry>();

        Assert.Equal(3, SidebarFolderTree.Children(tree, "g", into));
        Assert.Equal(new[] { SidebarTreeFixture.Pl("b"), SidebarTreeFixture.Pl("c"), SidebarTreeFixture.Fo("k") },
            Ids(into));
    }

    /// <summary>A folder's own <c>FolderId</c> is ITSELF, so a lookup written against that field makes every folder its
    /// own child. Containment is <c>ParentFolderId</c> for both kinds — this is the guard on that.</summary>
    [Fact]
    public void Children_NeverContainTheFolderItself()
    {
        var into = new List<SidebarLibraryEntry>();
        SidebarFolderTree.Children(SidebarTreeFixture.Tree(), "g", into);

        Assert.DoesNotContain(into, e => e.Id == SidebarTreeFixture.Fo("g"));
    }

    [Fact]
    public void Children_OfANestedFolder_AreReachable()
    {
        var into = new List<SidebarLibraryEntry>();

        Assert.Equal(1, SidebarFolderTree.Children(SidebarTreeFixture.Tree(), "k", into));
        Assert.Equal(SidebarTreeFixture.Pl("f"), into[0].Id);
    }

    [Fact]
    public void Children_OfAnEmptyOrUnknownFolder_AreNone()
    {
        var tree = SidebarTreeFixture.Tree();
        var into = new List<SidebarLibraryEntry> { SidebarTreeFixture.Playlist("stale", 0) };

        // Empty is NOT "the whole tree": the buffer is cleared, and an unknown id yields nothing rather than everything.
        Assert.Equal(0, SidebarFolderTree.Children(tree, "no-such-folder", into));
        Assert.Empty(into);
        Assert.Equal(0, SidebarFolderTree.Children(tree, "", into));
        Assert.Equal(0, SidebarFolderTree.Children(null, "g", into));
    }

    [Fact]
    public void TryFolder_ResolvesByGroupId_NotByEntryId()
    {
        var tree = SidebarTreeFixture.Tree();

        Assert.True(SidebarFolderTree.TryFolder(tree, "k", out var deep));
        Assert.Equal("Deep", deep.Name);
        Assert.Equal("g", deep.ParentFolderId);
        // The ENTRY id ("folder:k") is not the group id — passing it must miss rather than half-match.
        Assert.False(SidebarFolderTree.TryFolder(tree, SidebarTreeFixture.Fo("k"), out _));
    }

    // ── the drill-in stack ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Root_HasNoBack()
    {
        var nav = new SidebarFolderFlyoutNav("g", "Chill");

        Assert.Equal(1, nav.Depth);
        Assert.False(nav.CanGoBack);
        Assert.Equal("g", nav.Current.FolderId);
        Assert.Equal("Chill", nav.Current.Name);
        // Parent clamps to the root, so a caller can name "back to X" without indexing the stack.
        Assert.Equal("g", nav.Parent.FolderId);
        Assert.False(nav.Pop());
        Assert.Equal(1, nav.Depth);
    }

    [Fact]
    public void Push_DrillsIn_AndBackReturnsToTheParentLevel()
    {
        var nav = new SidebarFolderFlyoutNav("g", "Chill");

        Assert.True(nav.Push("k", "Deep"));
        Assert.Equal(2, nav.Depth);
        Assert.True(nav.CanGoBack);
        Assert.Equal("Deep", nav.Current.Name);
        Assert.Equal("Chill", nav.Parent.Name);

        Assert.True(nav.Pop());
        Assert.Equal(1, nav.Depth);
        Assert.False(nav.CanGoBack);
        Assert.Equal("g", nav.Current.FolderId);
    }

    [Fact]
    public void Push_RefusesAnEmptyId_AndAFolderAlreadyOnTheStack()
    {
        var nav = new SidebarFolderFlyoutNav("g", "Chill");
        nav.Push("k", "Deep");

        Assert.False(nav.Push("", "nothing"));
        // A rootlist cycle cannot exist, but a stale projection mid-move can briefly describe one; an unbounded push
        // would grow the stack until Back became useless.
        Assert.False(nav.Push("g", "Chill"));
        Assert.False(nav.Push("k", "Deep again"));
        Assert.Equal(2, nav.Depth);
    }

    [Fact]
    public void Push_CarriesTheNameItWasGiven_NotALiveLookup()
    {
        // The level name is captured at push time on purpose: the folder can be renamed or deleted while the flyout is
        // open, and the back header must still name the level the user actually came through.
        var nav = new SidebarFolderFlyoutNav("g", "Chill");
        nav.Push("k", "Deep");

        Assert.Equal("Deep", nav.Current.Name);
        Assert.Equal("Chill", nav.Parent.Name);
    }

    [Fact]
    public void PageKey_ChangesOnEveryMove_SoTheSlideCannotBeSkipped()
    {
        var nav = new SidebarFolderFlyoutNav("g", "Chill");
        string root = nav.PageKey;

        nav.Push("k", "Deep");
        string deep = nav.PageKey;
        Assert.NotEqual(root, deep);

        nav.Pop();
        Assert.Equal(root, nav.PageKey);

        // A DIFFERENT folder at the same depth is a different page — two levels that happened to share a depth must not
        // reconcile as one and swallow the transition.
        nav.Push("h", "Trailing");
        Assert.NotEqual(deep, nav.PageKey);
    }

    [Fact]
    public void DeepChain_PopsOneLevelAtATime()
    {
        var nav = new SidebarFolderFlyoutNav("a", "A");
        Assert.True(nav.Push("b", "B"));
        Assert.True(nav.Push("c", "C"));
        Assert.Equal(3, nav.Depth);

        Assert.True(nav.Pop());
        Assert.Equal("b", nav.Current.FolderId);
        Assert.True(nav.Pop());
        Assert.Equal("a", nav.Current.FolderId);
        Assert.False(nav.Pop());
    }

    // ── ONE containment definition ────────────────────────────────────────────────────────────
    //
    // The flyout LISTED rows from the `ParentFolderId` scan and printed "N items" from the projection's own
    // `ChildCount` — two definitions of "what is in this folder", and a folder full of playlists that read "0 items"
    // (F1, screenshot #14). Both now come from one place. These pin the equivalence on a REAL projection of a REAL
    // marker stream, so neither side can drift without this failing.

    /// <summary>A nested fixture built the way the app builds it: markers → <c>RootlistTreeBuilder</c> → the full
    /// flattened <c>SidebarProjection</c>, which is what <c>SidebarProjectionInput.PlaylistTree</c> is.</summary>
    static List<SidebarLibraryEntry> Projected()
    {
        var markers = Wavee.Backend.Playlists.RootlistTreeBuilder.EntriesFromUris(new[]
        {
            "spotify:playlist:a",
            "spotify:start-group:g:Chill",
            "spotify:playlist:b",
            "spotify:playlist:c",
            "spotify:start-group:k:Deep",
            "spotify:playlist:f",
            "spotify:end-group:k",
            "spotify:end-group:g",
            "spotify:playlist:d",
            "spotify:start-group:empty:Empty",
            "spotify:end-group:empty",
        });
        var nodes = Wavee.Backend.Playlists.RootlistTreeBuilder.Build(
            markers, uri => new Wavee.Core.PlaylistSummary(uri, uri, "", 0, null));
        var into = new List<SidebarLibraryEntry>(16);
        SidebarProjection.Build(into, SidebarEntryKindMask.PlaylistTree, nodes,
                                Array.Empty<Wavee.Core.Album>(), Array.Empty<Wavee.Core.Artist>(),
                                Array.Empty<Wavee.Core.Show>(), null, null, null, includeFolderChildren: true);
        return into;
    }

    [Fact]
    public void ChildCount_IsTheCountOfExactlyTheRowsTheFlyoutLists()
    {
        var tree = Projected();
        var rows = new List<SidebarLibraryEntry>();
        foreach (var f in tree)
        {
            if (f.Kind != SidebarEntryKind.Folder) continue;
            int listed = SidebarFolderTree.Children(tree, f.FolderId, rows);
            // the number the flyout renders ≡ the rows it renders ≡ the projection's own Items count
            Assert.Equal(listed, SidebarFolderTree.ChildCount(tree, f.FolderId));
            Assert.Equal(listed, f.ChildCount);
        }
    }

    [Fact]
    public void ZeroItems_IsImpossibleWhileTheFolderHasAny()
    {
        var tree = Projected();
        var rows = new List<SidebarLibraryEntry>();
        foreach (string folder in new[] { "g", "k" })
        {
            Assert.NotEqual(0, SidebarFolderTree.Children(tree, folder, rows));
            Assert.NotEqual(0, SidebarFolderTree.ChildCount(tree, folder));
        }
        // …and an EMPTY folder still reads zero on both channels — the count is not "unknown", it is none.
        Assert.Equal(0, SidebarFolderTree.Children(tree, "empty", rows));
        Assert.Equal(0, SidebarFolderTree.ChildCount(tree, "empty"));
    }

    static string[] Ids(IReadOnlyList<SidebarLibraryEntry> rows)
    {
        var ids = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++) ids[i] = rows[i].Id;
        return ids;
    }
}
