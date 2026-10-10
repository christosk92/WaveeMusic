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
    public void Text_Is36_Pitch40_NoArt()
    {
        Assert.Equal(36f, SidebarRowGeometry.HeightOf(SidebarRowShape.Text));
        Assert.Equal(40f, SidebarRowGeometry.PitchOf(SidebarRowShape.Text));
        Assert.Equal(SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph), SidebarRowGeometry.PitchOf(SidebarRowShape.Text));   // Collections' pitch
        Assert.Equal(0f, SidebarRowGeometry.ArtOf(SidebarRowShape.Text));
        Assert.Equal(16f, SidebarRowGeometry.PaneEdge + SidebarRowGeometry.TextLabelX);   // the label sits at the header's x
        Assert.True(SidebarRowGeometry.RowButton < SidebarRowGeometry.TextRowHeight);     // a folder's + and the Unpin button fit inside the row
    }

    [Fact]
    public void TextFolder_GlyphAtPane16_Gap8_LabelAtPane40()
    {
        Assert.Equal(8f, SidebarRowGeometry.TextGlyphGap);
        Assert.Equal(SidebarRowGeometry.TextLabelX + SidebarRowGeometry.GlyphSize, SidebarRowGeometry.TextGlyphColumn);
        Assert.Equal(16f, SidebarRowGeometry.PaneEdge + SidebarRowGeometry.TextLabelX);              // the mark's left edge
        Assert.Equal(40f, SidebarRowGeometry.TextGlyphLabelPaneX(0));                                // the label
        Assert.Equal(72f, SidebarRowGeometry.TextGlyphLabelPaneX(1));
        Assert.Equal(36f, SidebarRowGeometry.TextGlyphLabelX);                                       // slot space
        // The mark's centre stays on the tree guide column (slot x 20), so the guides still join it.
        Assert.Equal(SidebarRowGeometry.TextLabelX + SidebarRowGeometry.GlyphSize / 2f, SidebarRowGeometry.TreeGuideX(0));
    }

    [Theory]
    [InlineData(true, false, 44f)]     // icon column 40 + gap 4
    [InlineData(false, false, 12f)]    // glyph-less text row: the header's x
    [InlineData(false, true, 36f)]     // text folder: mark 12..28, gap 8
    public void LabelStart_IsTheRowsOwnLadder(bool iconColumn, bool textGlyph, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.LabelStartOf(iconColumn, textGlyph));

    [Fact]
    public void LabelWidth_FollowsTheLabelStart()
    {
        float icon = SidebarLabelFit.LabelWidth(320f, 0, 0f);
        float folder = SidebarLabelFit.LabelWidth(320f, 0, 0f, SidebarRowGeometry.LabelStartOf(true, textGlyph: true));
        Assert.Equal(icon + 8f, folder);   // the text folder's label starts 8 DIP earlier than the icon-column row's
        Assert.Equal(icon, SidebarLabelFit.LabelWidth(320f, 0, 0f, 44f));
    }

    // ── the count / "…" shared slot, the pin button ──

    [Theory]
    [InlineData(true, true, 26f)]      // a folder keeps a separate reserve for the "…"
    [InlineData(true, false, 0f)]      // a count / plain row has none: the "…" replaces the count in place
    [InlineData(false, true, 0f)]      // no menu, no "…"
    [InlineData(false, false, 0f)]
    public void OverflowReserve_OnlyAChevronRowKeepsOne(bool menu, bool chevron, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.OverflowReserve(menu, chevron));

    [Theory]
    [InlineData(true, true, false, 22f, 32f)]    // count + menu: 6 gap + the 26 "…" box (the count fits inside it)
    [InlineData(true, true, false, 40f, 46f)]    // a wide count grows the slot leftwards, never the "…"
    [InlineData(false, true, false, 22f, 28f)]   // count, no menu: 6 gap + the count
    [InlineData(true, false, true, 22f, 32f)]    // pin / eq and a menu but no count: an empty slot reserves the "…"
    [InlineData(true, false, false, 22f, 0f)]    // nothing to keep clear of: the title may run under the "…"
    [InlineData(false, false, true, 22f, 0f)]
    public void CountSlotWidth_CountAndOverflowShareOneSlot(bool menu, bool count, bool lead, float countWidth, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.CountSlotWidth(menu, count, lead, countWidth));

    [Fact]
    public void CountSlot_HoverNeverChangesTheLabelWidth()
    {
        // Rest and hover are the same layout: the slot is as wide with the "…" as the larger of the count and the "…".
        float withMenu = SidebarRowGeometry.CountSlotWidth(menu: true, count: true, lead: false, countWidth: 22f);
        Assert.True(withMenu >= SidebarRowGeometry.TrailingGap + 26f);
        Assert.Equal(withMenu, SidebarRowGeometry.CountSlotWidth(menu: true, count: true, lead: false, countWidth: 12f));   // narrow counts do not shrink it
    }

    [Fact]
    public void PinWidth_UnpinButtonIs12WiderThanTheMark()
    {
        Assert.Equal(0f, SidebarRowGeometry.PinWidth(pinned: false, button: true));
        Assert.Equal(18f, SidebarRowGeometry.PinWidth(pinned: true, button: false));   // 6 gap + the 12 mark
        Assert.Equal(30f, SidebarRowGeometry.PinWidth(pinned: true, button: true));    // 6 gap + the 24 button
        Assert.Equal(12f, SidebarRowGeometry.PinWidth(true, true) - SidebarRowGeometry.PinWidth(true, false));
        Assert.Equal(24f, SidebarRowGeometry.RowButton);
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

    [Fact]
    public void TreeGuides_SitOnTheFolderMarkColumn_32PerLevel()
    {
        Assert.Equal(SidebarRowGeometry.IconCentreX, SidebarRowGeometry.TreeGuidePaneX(0));   // 24
        Assert.Equal(56f, SidebarRowGeometry.TreeGuidePaneX(1));
        Assert.Equal(88f, SidebarRowGeometry.TreeGuidePaneX(2));
        Assert.Equal(20f, SidebarRowGeometry.TreeGuideX(0));                                  // slot space
    }

    [Fact]
    public void ChildrenStartUnderTheFoldersName()
    {
        // Text shape: a folder mark at 16 with its label at 40; a depth-1 child's glyph-less label sits at 48 (the 32 indent
        // from the header's x), 8 past the folder's label, as an indented child should.
        Assert.Equal(SidebarRowGeometry.LabelX, SidebarRowGeometry.TextLabelPaneX(1));
        Assert.Equal(SidebarRowGeometry.TextGlyphLabelPaneX(0) + SidebarRowGeometry.TextGlyphGap, SidebarRowGeometry.TextLabelPaneX(1));
        Assert.Equal(48f, SidebarRowGeometry.LabelPaneX(0));
        Assert.Equal(SidebarRowGeometry.HeaderTextPaneX, SidebarRowGeometry.TextLabelPaneX(0));   // 16
        // Cover shapes: a depth-1 child's art centre is at 56 and its label at 80.
        Assert.Equal(56f, SidebarRowGeometry.IconCentreX + SidebarRowGeometry.IndentFor(1));
        Assert.Equal(80f, SidebarRowGeometry.LabelPaneX(1));
    }
}
