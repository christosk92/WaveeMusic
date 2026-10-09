using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── SidebarRowGeometry: the one row ladder, pinned to the WinUI NavigationView numbers it was drawn from ──────────────
//
// Pure numbers. A retune of any of these is a visible change to every sidebar row, so each one is pinned here rather
// than inferred from a rendered screenshot.

public sealed class SidebarRowGeometryTests
{
    [Fact]
    public void Pitches_AreWinUi()
    {
        Assert.Equal(36f, SidebarRowGeometry.HeightOf(SidebarRowShape.Glyph));
        Assert.Equal(40f, SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph));
        Assert.Equal(40f, SidebarRowGeometry.HeightOf(SidebarRowShape.EntityTwoLine));
        Assert.Equal(44f, SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine));
        Assert.Equal(40f, SidebarRowGeometry.PitchOf(SidebarRowShape.EntityOneLine));
    }

    [Fact]
    public void Art_Is16_32_24()
    {
        Assert.Equal(16f, SidebarRowGeometry.ArtOf(SidebarRowShape.Glyph));
        Assert.Equal(32f, SidebarRowGeometry.ArtOf(SidebarRowShape.EntityTwoLine));
        Assert.Equal(24f, SidebarRowGeometry.ArtOf(SidebarRowShape.EntityOneLine));
    }

    [Fact]
    public void Rulers_IconCentre24_Label48_Trailing18_Chevron44()
    {
        Assert.Equal(24f, SidebarRowGeometry.IconCentreX);
        Assert.Equal(48f, SidebarRowGeometry.LabelX);
        Assert.Equal(302f, SidebarRowGeometry.TrailingRight(320f));
        Assert.Equal(276f, SidebarRowGeometry.ChevronLeft(320f));
    }

    [Theory]
    [InlineData(0, 0f)] [InlineData(1, 32f)] [InlineData(3, 96f)] [InlineData(7, 96f)] [InlineData(-1, 0f)]
    public void Indent_Is32PerLevel_CappedAt3(int depth, float expected) => Assert.Equal(expected, SidebarRowGeometry.IndentFor(depth));

    [Fact]
    public void Pill_StartsAtTheIndent_CentredBelowTheMargin()
    {
        Assert.Equal(32f, SidebarRowGeometry.PillX(1));
        Assert.Equal(12f, SidebarRowGeometry.PillTop(36f));     // 2 + (36 − 16) / 2
        Assert.Equal(14f, SidebarRowGeometry.PillTop(40f));
    }

    [Fact]
    public void Chrome_TopInset4_SeparatorLine4_HeaderButton28()
    {
        Assert.Equal(4f, SidebarRowGeometry.PaneTopInset);
        Assert.Equal(4f, SidebarRowGeometry.SeparatorLineTop);
        Assert.Equal(28f, SidebarRowGeometry.HeaderButton);
        Assert.Equal(SidebarLibraryHeadRules.ToolbarIconButton, SidebarRowGeometry.HeaderButton);
        Assert.Equal(16f, SidebarRowGeometry.HeaderGlyph);
        Assert.Equal(16f, SidebarRowGeometry.PlusGlyph);
        Assert.Equal(24f, SidebarRowGeometry.RowButton);
        Assert.True(SidebarRowGeometry.RowButton <= 28f);   // fits inside Classic's 28-px text row
        Assert.Equal(16f, SidebarRowGeometry.HeaderTextPaneX);
        Assert.Equal(296f, 320f - SidebarRowGeometry.PaneEdge - SidebarRowGeometry.HeaderTrailingPad - SidebarRowGeometry.HeaderButton / 2f);
    }

    [Fact]
    public void Caret_StartsWhereThePillDoes() => Assert.Equal(SidebarRowGeometry.PillX(2), SidebarRowGeometry.TreeContentX(2));

    [Fact]
    public void Subtitle_OnlyInTheTwoLineShape()
    {
        Assert.True(SidebarRowGeometry.SubtitleVisible(SidebarRowShape.EntityTwoLine, "48 songs"));
        Assert.False(SidebarRowGeometry.SubtitleVisible(SidebarRowShape.EntityOneLine, "48 songs"));
        Assert.False(SidebarRowGeometry.SubtitleVisible(SidebarRowShape.EntityTwoLine, ""));
    }

    [Fact]
    public void Chrome_Header40_Separator8()
    {
        Assert.Equal(40f, SidebarRowGeometry.HeaderHeight);
        Assert.Equal(8f, SidebarRowGeometry.SeparatorHeight);
        Assert.Equal(48f, SidebarRowGeometry.RailWidth);
    }
}
