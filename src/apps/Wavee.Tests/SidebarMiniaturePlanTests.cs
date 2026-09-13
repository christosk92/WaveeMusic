using Wavee;
using Xunit;

namespace Wavee.Tests;

// The template confirmation's miniature as a plan (ch 26 W11 + parity 17, 83-85): what the SIDEBAR will render, with
// deterministic sample indices so the dialog never flickers between opens.
public class SidebarMiniaturePlanTests
{
    static List<SidebarMiniatureRow> Plan(SidebarCustomLayout layout)
    {
        var rows = new List<SidebarMiniatureRow>();
        SidebarMiniaturePlan.Build(layout, rows);
        return rows;
    }

    [Fact]
    public void ABlankTemplateIsOneCentredRowNotAnEmptyPane()
    {
        var rows = Plan(SidebarTemplates.Build(SidebarTemplates.Blank));
        Assert.Single(rows);
        Assert.Equal(SidebarMiniatureRowKind.Blank, rows[0].Kind);
    }

    [Fact]
    public void TheCuratedTemplatePlansItsSectionsInOrder()
    {
        var kinds = Plan(SidebarTemplates.Build(SidebarTemplates.Curated)).ConvertAll(r => r.Kind);
        Assert.Equal(new[]
        {
            SidebarMiniatureRowKind.Title, SidebarMiniatureRowKind.PinnedPlaylist, SidebarMiniatureRowKind.PinnedArtist,
            SidebarMiniatureRowKind.Divider,
            SidebarMiniatureRowKind.Title, SidebarMiniatureRowKind.GridPair,                // Jump back in is a grid
            SidebarMiniatureRowKind.Divider,
            SidebarMiniatureRowKind.Title, SidebarMiniatureRowKind.Shortcut, SidebarMiniatureRowKind.Shortcut,
            SidebarMiniatureRowKind.Shortcut,                                                // at most three
            SidebarMiniatureRowKind.Divider,
            SidebarMiniatureRowKind.Title, SidebarMiniatureRowKind.TreeFolder, SidebarMiniatureRowKind.TreePlaylist,
        }, kinds);
    }

    [Fact]
    public void HiddenSectionsAreSkippedAndGroupChildrenInlined()
    {
        var layout = new SidebarCustomLayout("custom",
        [
            new SidebarSectionSpec("sec_h", SidebarSectionKind.NewReleases, Hidden: true),
            new SidebarSectionSpec("sec_g", SidebarSectionKind.CustomGroup, Children:
            [
                new SidebarSectionSpec("sec_c", SidebarSectionKind.Concerts),
            ]),
        ]);
        var rows = Plan(layout);
        Assert.DoesNotContain(rows, r => r.Section?.Id == "sec_h");
        // group: title + its own sample row (index 1 + 6), then the child: title + sample (child index 0 + 6)
        Assert.Equal(new[] { "sec_g", "sec_g", "sec_c", "sec_c" }, rows.ConvertAll(r => r.Section!.Id));
        Assert.Equal(1 + SidebarMiniaturePlan.SampleOffset, rows[1].Index);
        Assert.Equal(0 + SidebarMiniaturePlan.SampleOffset, rows[3].Index);
    }

    [Fact]
    public void TheSampleIndicesAreDeterministic()
    {
        var a = Plan(SidebarTemplates.Build(SidebarTemplates.Curated));
        var b = Plan(SidebarTemplates.Build(SidebarTemplates.Curated));
        Assert.Equal(a.ConvertAll(r => (r.Kind, r.Index)), b.ConvertAll(r => (r.Kind, r.Index)));
        Assert.Equal(SidebarMiniaturePlan.PinnedPlaylistSample, a[1].Index);
        Assert.Equal(SidebarMiniaturePlan.PinnedArtistSample, a[2].Index);
    }
}
