using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE TREE ROW'S MENU EXTRAS (D12). <c>SidebarPaneSlot.NavExtras</c> used to build Move up / Move down only for
/// reorder bands and pins, so the one verb a rootlist row offered was "Move out of {parent}" — one level, one
/// direction. A right-click on a playlist could not reorder it at all, and neither could a keyboard.
///
/// <para>The DECISION — which verbs, at which position in the sibling run — is pure, and is driven directly here.</para>
/// </summary>
public class SidebarNavExtrasTests
{
    static SidebarTreeNavLayout Layout(string id)
    {
        var tree = SidebarTreeFixture.Tree();
        return SidebarTreeNavLayout.Decide(RootlistTreeNav.Siblings(tree, id),
                                           RootlistTreeNav.HasDestinations(tree, SidebarTreeFixture.Markers(), id));
    }

    // ── the decision ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMiddleTreeRow_OffersAllThreeVerbs()
    {
        // c sits between b and [Deep] inside Chill: both orderings are real, and there are folders to file it into.
        var mid = Layout(SidebarTreeFixture.Pl("c"));
        Assert.True(mid.MoveUp);
        Assert.True(mid.MoveDown);
        Assert.True(mid.MoveToFolder);
        Assert.False(mid.IsEmpty);
    }

    [Fact]
    public void TheEndsOfTheSiblingRunDropTheVerbTheyCannotHonour()
    {
        var first = Layout(SidebarTreeFixture.Pl("a"));       // first at top level
        Assert.False(first.MoveUp);
        Assert.True(first.MoveDown);

        var last = Layout(SidebarTreeFixture.Fo("h"));        // last at top level
        Assert.True(last.MoveUp);
        Assert.False(last.MoveDown);

        var only = Layout(SidebarTreeFixture.Pl("f"));        // Deep's only child
        Assert.False(only.MoveUp);
        Assert.False(only.MoveDown);
        Assert.True(only.MoveToFolder);                       // it can still leave the folder it is alone in
    }

    [Fact]
    public void AFolderRowGetsTheSameVerbsAsAPlaylistRow()
    {
        // ONE renderer, so Classic / Library V3 / Curated share this; and a folder is an ordinary sibling in its run.
        var folder = Layout(SidebarTreeFixture.Fo("k"));      // Deep, last among Chill's three children
        Assert.True(folder.MoveUp);
        Assert.False(folder.MoveDown);
        Assert.True(folder.MoveToFolder);
    }

    [Fact]
    public void ARowWithNoRunAndNowhereToGo_YieldsNoExtrasAtAll()
    {
        Assert.True(SidebarTreeNavLayout.Decide(RootlistSiblingRun.None, hasDestinations: false).IsEmpty);
    }
}
