using System;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── SidebarTypeAheadRules: which rows take focus, and what typeahead reads from them ───────────────────────────────

public sealed class SidebarTypeAheadRulesTests
{
    [Fact]
    public void FocusStops_AreExactlyTheNavigableRows()
    {
        var stops = Enum.GetValues<SidebarRowKind>().Where(SidebarTypeAheadRules.IsFocusStop).ToArray();

        Assert.Equal(
            new[] { SidebarRowKind.SectionHeader, SidebarRowKind.IconRow, SidebarRowKind.EntityRow,
                    SidebarRowKind.FolderHeader, SidebarRowKind.SectionTile },
            stops);
    }

    [Fact]
    public void TextOf_NonFocusRow_IsEmpty()
        => Assert.Equal("", SidebarTypeAheadRules.TextOf(SidebarRowKind.Divider, "x"));

    [Fact]
    public void TextOf_FocusRow_IsItsLabel()
        => Assert.Equal("Running", SidebarTypeAheadRules.TextOf(SidebarRowKind.EntityRow, "Running"));

    [Fact]
    public void Overflows_ALongPlaylistNameInANarrowColumn()
        => Assert.True(SidebarLabelFit.Overflows("A very long playlist name that cannot fit", SidebarLabelFit.LabelWidth(240, 0, 0)));

    [Fact]
    public void Overflows_AShortTitleWithTrailingContent_DoesNot()
        => Assert.False(SidebarLabelFit.Overflows("Jazz", SidebarLabelFit.LabelWidth(320, 0, 28)));

    [Fact]
    public void LabelWidth_TextOnlyRow_ChargesNoIconColumn()
    {
        Assert.Equal(
            SidebarLabelFit.LabelWidth(240, 0, 0) + SidebarRowGeometry.IconColumn + SidebarRowGeometry.LabelGap - SidebarRowGeometry.TextLabelX,
            SidebarLabelFit.LabelWidth(240, 0, 0, iconColumn: false));
        Assert.Equal(SidebarLabelFit.LabelWidth(240, 1, 28), SidebarLabelFit.LabelWidth(240, 1, 28, iconColumn: true));
    }
}
