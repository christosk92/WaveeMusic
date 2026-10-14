using System;
using System.Linq;
using Wavee;
using Wavee.Themes.Classic;
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
        => Assert.True(SidebarLabelFit.Overflows("A very long playlist name that cannot fit", SidebarGeometry.LabelWidth(240, 0, 0)));

    [Fact]
    public void Overflows_AShortTitleWithTrailingContent_DoesNot()
        => Assert.False(SidebarLabelFit.Overflows("Jazz", SidebarGeometry.LabelWidth(320, 0, 28)));
}
