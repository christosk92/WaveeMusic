using Wavee;
using Xunit;

namespace Wavee.Tests;

// The customizer's navigation-level picks: the Hidden sections walk (nothing vanishes into an invisible elsewhere),
// the palette's append subject (ch 26 W6) and the item picker's Library-tab filter (W13).
public class SidebarCustomizerPicksTests
{
    static SidebarSectionSpec S(string id, SidebarSectionKind kind, bool hidden = false,
                                IReadOnlyList<SidebarSectionSpec>? children = null)
        => new(id, kind, Hidden: hidden, Children: children);

    [Fact]
    public void HiddenSectionsWalkTopLevelAndChildrenInDocumentOrder()
    {
        var sections = new[]
        {
            S("sec_a", SidebarSectionKind.Pinned, hidden: true),
            S("sec_b", SidebarSectionKind.CustomGroup, children: [S("sec_c", SidebarSectionKind.StaticLinks, hidden: true)]),
            S("sec_d", (SidebarSectionKind)99, hidden: true),   // a kind this build does not understand is still listed
            S("sec_e", SidebarSectionKind.Divider),
        };
        var into = new List<SidebarSectionSpec>();
        SidebarCustomizerPicks.HiddenSections(sections, into);
        Assert.Equal(new[] { "sec_a", "sec_c", "sec_d" }, into.ConvertAll(s => s.Id));
    }

    [Fact]
    public void TheOptionsSubjectWinsOverTheExpandedCard()
    {
        var layout = new SidebarCustomLayout("curated",
            [S("sec_links", SidebarSectionKind.StaticLinks), S("sec_other", SidebarSectionKind.StaticLinks)]);
        Assert.Equal("sec_links", SidebarCustomizerPicks.AppendTarget(layout, "sec_links", "sec_other")?.Id);
        Assert.Equal("sec_other", SidebarCustomizerPicks.AppendTarget(layout, null, "sec_other")?.Id);
        Assert.Null(SidebarCustomizerPicks.AppendTarget(layout, null, null));
    }

    [Fact]
    public void OnlyARealStaticLinksSectionReceivesAnAppend()
    {
        var layout = new SidebarCustomLayout("curated",
            [S("sec_tree", SidebarSectionKind.PlaylistTree), S("sec_links", SidebarSectionKind.StaticLinks)]);
        Assert.Null(SidebarCustomizerPicks.AppendTarget(layout, "sec_tree", null));
        Assert.Null(SidebarCustomizerPicks.AppendTarget(layout, SidebarIds.TopBarSection, null));
        Assert.Null(SidebarCustomizerPicks.AppendTarget(layout, "sec_gone", null));
    }

    [Fact]
    public void TheLibraryTabNeverOffersRoutesTracksOrFolders()
    {
        Assert.True(SidebarCustomizerPicks.OffersEntry(SidebarEntryKind.Playlist, null));
        Assert.False(SidebarCustomizerPicks.OffersEntry(SidebarEntryKind.Folder, null));
        Assert.False(SidebarCustomizerPicks.OffersEntry(SidebarEntryKind.Track, null));
        Assert.False(SidebarCustomizerPicks.OffersEntry(SidebarEntryKind.AppRoute, null));
        Assert.True(SidebarCustomizerPicks.OffersEntry(SidebarEntryKind.Artist, SidebarEntryKind.Artist));
        Assert.False(SidebarCustomizerPicks.OffersEntry(SidebarEntryKind.Album, SidebarEntryKind.Artist));
        Assert.Equal(SidebarEntityKind.Show, SidebarCustomizerPicks.EntityKindOf(SidebarEntryKind.Show));
        Assert.Equal(SidebarEntityKind.None, SidebarCustomizerPicks.EntityKindOf(SidebarEntryKind.AppRoute));
    }
}
