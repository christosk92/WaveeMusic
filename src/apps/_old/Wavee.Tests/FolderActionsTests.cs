using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// SIBLING RESOLUTION — the rule behind Move up / Move down / Alt+↑ / Alt+↓ (D12).
///
/// <para>Reordering the rootlist used to be a DRAG and nothing else: <c>NavExtras</c> built Move up/down only for
/// reorder bands and pins, and a keyboard-only user could not move a playlist at all. The verbs address the SIBLING RUN
/// — the entries sharing a parent folder — because "the rows at my depth" would fuse two different folders' children
/// into one list and walk an item out of its folder sideways.</para>
/// </summary>
public class FolderActionsTests
{
    static RootlistSiblingRun Run(string id) => RootlistTreeNav.Siblings(SidebarTreeFixture.Tree(), id);

    // ── top level ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TopLevelRun_CountsFoldersAsSiblings_NotTheirContents()
    {
        // a · [Chill] · d · [Trailing] — four siblings, even though seven rows sit between the first and the last.
        var run = Run(SidebarTreeFixture.Pl("a"));
        Assert.Equal(0, run.Position);
        Assert.Equal(4, run.Count);
        Assert.False(run.CanMoveUp);                                  // the run's first item
        Assert.True(run.CanMoveDown);
        // The next sibling is the FOLDER, addressed by its group id — which is what makes Move down step OVER it
        // (RootlistOps resolves After against the folder's whole span) rather than into it.
        Assert.Equal(new RootlistItemRef("g", IsFolder: true), run.Next);
    }

    [Fact]
    public void LastTopLevelEntry_HasNoMoveDown()
    {
        var run = Run(SidebarTreeFixture.Fo("h"));
        Assert.Equal(3, run.Position);
        Assert.Equal(4, run.Count);
        Assert.True(run.CanMoveUp);
        Assert.False(run.CanMoveDown);
        Assert.Equal(new RootlistItemRef(SidebarTreeFixture.PlaylistUriPrefix + "d", IsFolder: false), run.Previous);
    }

    // ── nested ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NestedRun_IsScopedToItsFolder_NotToItsDepth()
    {
        // b's siblings are Chill's children (b · c · [Deep]) — NOT "everything at depth 1", which would also sweep in
        // Trailing's child e and let a Move down walk b into a different folder.
        var run = Run(SidebarTreeFixture.Pl("b"));
        Assert.Equal(0, run.Position);
        Assert.Equal(3, run.Count);
        Assert.Equal(new RootlistItemRef(SidebarTreeFixture.PlaylistUriPrefix + "c", IsFolder: false), run.Next);

        var middle = Run(SidebarTreeFixture.Pl("c"));
        Assert.Equal(1, middle.Position);
        Assert.True(middle.CanMoveUp);
        Assert.True(middle.CanMoveDown);
        Assert.Equal(new RootlistItemRef(SidebarTreeFixture.PlaylistUriPrefix + "b", IsFolder: false), middle.Previous);
        Assert.Equal(new RootlistItemRef("k", IsFolder: true), middle.Next);
    }

    [Fact]
    public void AnOnlyChild_HasNeitherVerb()
    {
        var run = Run(SidebarTreeFixture.Pl("f"));      // Deep's only child
        Assert.Equal(0, run.Position);
        Assert.Equal(1, run.Count);
        Assert.False(run.CanMoveUp);
        Assert.False(run.CanMoveDown);
    }

    [Fact]
    public void ARowTheTreeDoesNotShow_HasNoRunAtAll()
    {
        // Absent, never "position 0 of 0": a verb built from a run that does not exist would move the wrong row.
        Assert.True(Run("pl:spotify:playlist:ghost").IsEmpty);
        Assert.True(RootlistTreeNav.Siblings(null, SidebarTreeFixture.Pl("a")).IsEmpty);
        Assert.True(RootlistTreeNav.Siblings(SidebarTreeFixture.Tree(), "").IsEmpty);
    }

    // ── the layout the menu draws from it ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEndsOfTheRunHideTheVerb_TheyDoNotDisableIt()
    {
        var first = SidebarTreeNavLayout.Decide(Run(SidebarTreeFixture.Pl("a")), hasDestinations: true);
        Assert.False(first.MoveUp);
        Assert.True(first.MoveDown);

        var last = SidebarTreeNavLayout.Decide(Run(SidebarTreeFixture.Fo("h")), hasDestinations: true);
        Assert.True(last.MoveUp);
        Assert.False(last.MoveDown);

        // "Move to folder…" survives both ends — an item that cannot move inside its run can still be filed elsewhere —
        // but never opens an empty picker.
        Assert.True(last.MoveToFolder);
        Assert.False(SidebarTreeNavLayout.Decide(Run(SidebarTreeFixture.Pl("a")), hasDestinations: false).MoveToFolder);
        Assert.True(SidebarTreeNavLayout.Decide(RootlistSiblingRun.None, hasDestinations: false).IsEmpty);
    }
}
