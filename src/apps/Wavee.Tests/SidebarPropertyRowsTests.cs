using Wavee;
using Xunit;

namespace Wavee.Tests;

// Which rows the re-hosted property surface draws, in which group (ch 26 W15). The per-kind option table itself is
// SidebarSectionKinds.AllowsDisplayField's; these facts pin the ORDER, the GROUPING and the exclusions layered on it.
public class SidebarPropertyRowsTests
{
    static SidebarSectionSpec Section(SidebarSectionKind kind, SidebarDisplayOptions? display = null, int items = 0)
    {
        var list = new List<SidebarItemSpec>();
        for (int i = 0; i < items; i++)
            list.Add(new SidebarItemSpec("itm_" + i.ToString("x8"), SidebarItemTarget.Route, "home"));
        return new SidebarSectionSpec("sec_00000001", kind, Display: display, Items: list);
    }

    [Fact]
    public void AnEntityListGroupsItsRowsInPanelOrder()
    {
        var appearance = new List<SidebarDisplayField>();
        var behavior = new List<SidebarDisplayField>();
        SidebarPropertyRows.DisplayFields(Section(SidebarSectionKind.EntityList), appearance, behavior);

        Assert.Equal(
            new[] { SidebarDisplayField.Density, SidebarDisplayField.Presentation, SidebarDisplayField.Artwork,
                    SidebarDisplayField.Subtitles, SidebarDisplayField.CountBadges, SidebarDisplayField.InlineControls },
            appearance);
        Assert.Equal(
            new[] { SidebarDisplayField.MaxItems, SidebarDisplayField.EmptyBehavior, SidebarDisplayField.ShowInRail },
            behavior);
    }

    [Fact]
    public void GridColumnsAppearOnlyWhileTheSectionIsAGrid()
    {
        var appearance = new List<SidebarDisplayField>();
        var behavior = new List<SidebarDisplayField>();
        SidebarPropertyRows.DisplayFields(
            Section(SidebarSectionKind.EntityList, SidebarDisplayOptions.Entities with { Presentation = SidebarPresentation.Grid }),
            appearance, behavior);
        Assert.Equal(2, appearance.IndexOf(SidebarDisplayField.GridColumns));
    }

    [Fact]
    public void TheDeadStartCollapsedRowIsNeverOffered()
    {
        var appearance = new List<SidebarDisplayField>();
        var behavior = new List<SidebarDisplayField>();
        foreach (SidebarSectionKind kind in Enum.GetValues<SidebarSectionKind>())
        {
            SidebarPropertyRows.DisplayFields(Section(kind), appearance, behavior);
            Assert.DoesNotContain(SidebarDisplayField.CollapsedByDefault, appearance);
            Assert.DoesNotContain(SidebarDisplayField.CollapsedByDefault, behavior);
        }
    }

    /// <summary>0.2.9's option table allows ShowInRail on a Divider, so its popover carries a Behavior group with that
    /// one switch — and no rename row and no collapse row.</summary>
    [Fact]
    public void ADividerOffersOnlyTheRailSwitchBesideHidden()
    {
        var appearance = new List<SidebarDisplayField>();
        var behavior = new List<SidebarDisplayField>();
        SidebarPropertyRows.DisplayFields(Section(SidebarSectionKind.Divider), appearance, behavior);
        Assert.Empty(appearance);
        Assert.Equal(new[] { SidebarDisplayField.ShowInRail }, behavior);
        Assert.False(SidebarPropertyRows.ShowsTitleRow(SidebarSectionKind.Divider));
        Assert.False(SidebarPropertyRows.ShowsCollapseRow(SidebarSectionKind.Divider));
        Assert.False(SidebarPropertyRows.ShowsCollapseRow(SidebarSectionKind.Header));
        Assert.True(SidebarPropertyRows.ShowsCollapseRow(SidebarSectionKind.Pinned));
    }

    [Fact]
    public void PinnedHasNoAddPathAndAnEmptyHintAtZero()
    {
        var empty = SidebarPropertyRows.Items(SidebarSectionKind.Pinned, 0);
        Assert.True(empty.EmptyHint);
        Assert.False(empty.Buttons);
        var some = SidebarPropertyRows.Items(SidebarSectionKind.Pinned, 3);
        Assert.False(some.EmptyHint);
        Assert.False(some.Buttons);
    }

    [Fact]
    public void BothButtonsDisableAtTheCapButASpotlightRetargetsForever()
    {
        var full = SidebarPropertyRows.Items(SidebarSectionKind.StaticLinks, SidebarLayoutReducer.MaxItemsPerSection);
        Assert.True(full.Buttons);
        Assert.False(full.AddEnabled);
        Assert.True(full.ActionShortcut);
        Assert.False(full.ActionEnabled);

        var embed = SidebarPropertyRows.Items(SidebarSectionKind.EntityEmbed, 1);
        Assert.True(embed.AddEnabled);
        Assert.False(embed.ActionShortcut);
    }

    [Fact]
    public void CustomOrderAndReverseFollowTheQuery()
    {
        Assert.True(SidebarPropertyRows.CustomOrderAllowed(SidebarSectionKind.PlaylistTree, SidebarEntityQuery.Default));
        Assert.True(SidebarPropertyRows.CustomOrderAllowed(SidebarSectionKind.EntityList,
            new SidebarEntityQuery(SidebarEntityKinds.Playlists)));
        Assert.False(SidebarPropertyRows.CustomOrderAllowed(SidebarSectionKind.EntityList, SidebarEntityQuery.Default));

        Assert.False(SidebarPropertyRows.DescendingEnabled(SidebarEntityQuery.PlaylistTreeSourceOrder));
        Assert.True(SidebarPropertyRows.DescendingEnabled(SidebarEntityQuery.Default));
    }
}
