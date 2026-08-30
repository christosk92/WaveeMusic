using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// THE ANALYTIC ROW LADDER (SidebarRowExtents). The pane feeds these numbers to the virtualizing host as its per-row
// extent SEED, so they are what the sidebar's content extent, scroll anchor and drop placement are computed from before
// anything realizes. A drift between this ladder and what SidebarPaneSlot actually builds is exactly the class of bug
// that made a folder expansion shuffle the rows around it, so every term is pinned here against the renderer's own
// constants rather than against a copied literal.
public sealed class SidebarRowExtentsTests
{
    static SidebarSectionSpec Sec(string id, SidebarSectionKind kind, SidebarDisplayOptions? display = null,
                                  bool collapsed = false, IReadOnlyList<SidebarItemSpec>? items = null)
        => new(id, kind, null, "sidebar.section.header", false, collapsed, display, items, null, null);

    static SidebarRow Row(SidebarRowKind kind, string sectionId = "s", byte depth = 0, int entry = -1)
        => new(kind, sectionId, depth, entry, 0, sectionId);

    static float H(IReadOnlyList<SidebarRow> rows, int i, SidebarSectionSpec section, bool editable = false)
        => SidebarRowExtents.HeightOf(rows, i, section, editable);

    [Fact]
    public void ItemRows_AreTheSectionsOneUniformHeight()
    {
        // The ladder is per SECTION, never per row (iron rule 4): Cozy+subtitles = 44 = Classic's row.
        var cozy = Sec("s", SidebarSectionKind.EntityList,
            SidebarDisplayOptions.Default with { Density = SidebarDensity.Cozy, Subtitles = true });
        var compact = Sec("s", SidebarSectionKind.EntityList,
            SidebarDisplayOptions.Default with { Density = SidebarDensity.Compact, Subtitles = true });
        var comfy = Sec("s", SidebarSectionKind.EntityList,
            SidebarDisplayOptions.Default with { Density = SidebarDensity.Comfortable, Subtitles = true });

        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.EntityRow), Row(SidebarRowKind.IconRow), Row(SidebarRowKind.Placeholder),
            Row(SidebarRowKind.FolderHeader), Row(SidebarRowKind.Skeleton),
        };
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(44f, H(rows, i, cozy));
            Assert.Equal(32f, H(rows, i, compact));
            Assert.Equal(48f, H(rows, i, comfy));
        }
        Assert.Equal(SidebarRowGeometry.ClassicHeight, H(rows, 0, cozy));
    }

    [Fact]
    public void ChromeRows_MatchTheRenderersOwnConstants()
    {
        var section = Sec("s", SidebarSectionKind.PlaylistTree);
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.Divider), Row(SidebarRowKind.TreeEnd), Row(SidebarRowKind.SectionCard),
            Row(SidebarRowKind.EntityCard), Row(SidebarRowKind.GridStrip),
        };
        Assert.Equal(SidebarRowGeometry.DividerHeight, H(rows, 0, section));
        Assert.Equal(SidebarRowGeometry.TreeEndHeight, H(rows, 1, section));
        Assert.Equal(SidebarRowGeometry.ClassicHeight, H(rows, 2, section));
        Assert.Equal(SidebarRowGeometry.CardHeightFor(section.Opts.Density), H(rows, 3, section));
        // A GridStrip's cells wrap artwork + text at font metrics this layer cannot see: NOT analytic, by contract.
        Assert.True(float.IsNaN(H(rows, 4, section)));
        // A row whose section is gone renders nothing, so it occupies nothing (never the 44-DIP estimate).
        Assert.Equal(0f, SidebarRowExtents.HeightOf(rows, 0, null, editable: false));
    }

    [Fact]
    public void EmptyRow_FollowsTheSectionsEmptyBehavior()
    {
        var rows = new List<SidebarRow> { Row(SidebarRowKind.Empty) };
        // Pinned's empty state IS its drop zone, and it is unconditional (R3.1.5).
        Assert.Equal(SidebarRowGeometry.PinDropZoneRestHeight,
            H(rows, 0, Sec("p", SidebarSectionKind.Pinned)));
        // A quiet feed hint is the 32-DIP band, not a full row.
        Assert.Equal(SidebarRowGeometry.EmptyHintHeight,
            H(rows, 0, Sec("s", SidebarSectionKind.EntityList,
                SidebarDisplayOptions.Default with { EmptyBehavior = SidebarEmptyBehavior.CompactHint })));
        // HideBody draws nothing at all.
        Assert.Equal(0f,
            H(rows, 0, Sec("s", SidebarSectionKind.EntityList,
                SidebarDisplayOptions.Default with { EmptyBehavior = SidebarEmptyBehavior.HideBody })));
    }

    [Fact]
    public void BandedRhythm_IsEightExceptFirstRowAndAfterADividerOrHeading()
    {
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.SectionHeader, "a"),     // 0 - the pane's first row
            Row(SidebarRowKind.EntityRow, "a"),
            Row(SidebarRowKind.SectionHeader, "b"),     // 2 - after a row: full air
            Row(SidebarRowKind.Divider, "d"),
            Row(SidebarRowKind.SectionHeader, "c"),     // 4 - after a divider: none
            Row(SidebarRowKind.HeaderLabel, "e"),
            Row(SidebarRowKind.SectionHeader, "f"),     // 6 - after a bare heading: none
        };
        Assert.Equal(0f, SidebarRowExtents.BandTop(rows, 0));
        Assert.Equal(SidebarRowGeometry.SectionGap, SidebarRowExtents.BandTop(rows, 2));
        Assert.Equal(0f, SidebarRowExtents.BandTop(rows, 4));
        Assert.Equal(0f, SidebarRowExtents.BandTop(rows, 6));

        var section = Sec("a", SidebarSectionKind.EntityList);
        float bare = SidebarRowGeometry.HeaderHeight + SidebarRowGeometry.HeaderBodyGap;
        Assert.Equal(bare, H(rows, 0, section));
        Assert.Equal(bare + SidebarRowGeometry.SectionGap, H(rows, 2, section));
        Assert.Equal(bare, H(rows, 4, section));
        Assert.Equal(bare + SidebarRowGeometry.SectionGap, H(rows, 5, section));   // the heading itself follows a header
    }

    [Fact]
    public void InlineChipStrip_OnlyOnAnEditableOpenEntityListThatAsksForIt()
    {
        var rows = new List<SidebarRow> { Row(SidebarRowKind.SectionHeader) };
        float bare = SidebarRowGeometry.HeaderHeight + SidebarRowGeometry.HeaderBodyGap;
        float withChips = bare + SidebarRowGeometry.ChipStripGap + SidebarRowGeometry.ChipStripHeight;
        var chips = SidebarDisplayOptions.Default with { InlineControls = true };

        Assert.Equal(withChips, H(rows, 0, Sec("s", SidebarSectionKind.EntityList, chips), editable: true));
        // A READ-ONLY pane (Classic, Library V3) never renders them...
        Assert.Equal(bare, H(rows, 0, Sec("s", SidebarSectionKind.EntityList, chips), editable: false));
        // ...nor does a collapsed section, nor a section of another kind.
        Assert.Equal(bare, H(rows, 0, Sec("s", SidebarSectionKind.EntityList, chips, collapsed: true), editable: true));
        Assert.Equal(bare, H(rows, 0, Sec("s", SidebarSectionKind.PlaylistTree, chips), editable: true));
    }

    [Fact]
    public void PrefixSumOfExtents_IsExactlyContentYOf()
    {
        var section = Sec("s", SidebarSectionKind.PlaylistTree,
            SidebarDisplayOptions.Default with { Density = SidebarDensity.Cozy, Subtitles = true });
        var rows = new List<SidebarRow>
        {
            Row(SidebarRowKind.SectionHeader),
            Row(SidebarRowKind.FolderHeader),
            Row(SidebarRowKind.EntityRow),
            Row(SidebarRowKind.EntityRow),
            Row(SidebarRowKind.TreeEnd),
        };
        float ExtentOf(int i) => H(rows, i, section);

        // The pane's rows are contiguous inside ONE virtualized list, so the prefix sum IS the row's content-space Y -
        // which is what drop placement and bring-into-view resolve against.
        float running = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(running, SidebarRowGeometry.ContentYOf(i, rows.Count, ExtentOf), 3);
            running += ExtentOf(i);
        }
        // 30 header (first row, no air) + 44 folder + 44 + 44 + 24 tree end. The trailing 44-DIP CREATE row is gone:
        // the affordance is the section header's "+", which is chrome inside the header band's own extent.
        Assert.Equal(30f + 44f + 44f + 44f + 24f, running, 3);
        Assert.Equal(running, SidebarRowGeometry.ContentYOf(rows.Count, rows.Count, ExtentOf), 3);
    }
}
