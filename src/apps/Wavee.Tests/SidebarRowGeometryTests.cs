using System.Collections.Generic;
using System.Reflection;
using Wavee;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

// ── SidebarGeometry (Classic): the one row ladder, pinned to the WinUI NavigationView numbers it was drawn from ──────────────
//
// Pure numbers. A retune of any of these is a visible change to every sidebar row, so each one is pinned here rather
// than inferred from a rendered screenshot.

public sealed class SidebarRowGeometryTests
{
    [Fact]
    public void Pitches_AreWinUi()
    {
        Assert.Equal(36f, SidebarGeometry.HeightOf(SidebarRowShape.Glyph));
        Assert.Equal(40f, SidebarGeometry.PitchOf(SidebarRowShape.Glyph));
        Assert.Equal(40f, SidebarGeometry.HeightOf(SidebarRowShape.EntityTwoLine));
        Assert.Equal(44f, SidebarGeometry.PitchOf(SidebarRowShape.EntityTwoLine));
        Assert.Equal(40f, SidebarGeometry.PitchOf(SidebarRowShape.EntityOneLine));
    }

    [Fact]
    public void Art_Is16_32_24()
    {
        Assert.Equal(16f, SidebarGeometry.ArtOf(SidebarRowShape.Glyph));
        Assert.Equal(32f, SidebarGeometry.ArtOf(SidebarRowShape.EntityTwoLine));
        Assert.Equal(24f, SidebarGeometry.ArtOf(SidebarRowShape.EntityOneLine));
    }

    [Fact]
    public void TheSectionTitle_StartsOnTheCovers_AndTheCoverClearsThePill()
    {
        // The section title starts on the covers' left edge, and the cover clears the 3-px pill by 7.
        Assert.Equal(SidebarGeometry.LeadInset, SidebarGeometry.HeaderTextX);
        Assert.Equal(7f, SidebarGeometry.LeadInset - SidebarGeometry.PillX(0) - SidebarGeometry.PillW);
    }

    [Theory]
    [InlineData(0, 0f)] [InlineData(1, 31f)] [InlineData(3, 93f)] [InlineData(7, 93f)] [InlineData(-1, 0f)]
    public void Indent_Is31PerLevel_CappedAt3(int depth, float expected) => Assert.Equal(expected, SidebarGeometry.IndentFor(depth));

    [Fact]
    public void Pill_StartsAtTheIndent_CentredBelowTheMargin()
    {
        Assert.Equal(31f, SidebarGeometry.PillX(1));
        Assert.Equal(12f, SidebarGeometry.PillTop(36f));     // 2 + (36 − 16) / 2
        Assert.Equal(14f, SidebarGeometry.PillTop(40f));
    }

    [Fact]
    public void Caret_StartsWhereThePillDoes() => Assert.Equal(SidebarGeometry.PillX(2), SidebarGeometry.TreeContentX(2));

    [Fact]
    public void Subtitle_OnlyInTheTwoLineShape()
    {
        Assert.True(SidebarGeometry.SubtitleVisible(SidebarRowShape.EntityTwoLine, "48 songs"));
        Assert.False(SidebarGeometry.SubtitleVisible(SidebarRowShape.EntityOneLine, "48 songs"));
        Assert.False(SidebarGeometry.SubtitleVisible(SidebarRowShape.EntityTwoLine, ""));
    }

    [Fact]
    public void Chrome_Header40_Separator8()
    {
        Assert.Equal(40f, SidebarGeometry.HeaderHeight);
        Assert.Equal(8f, SidebarGeometry.SeparatorHeight);
        Assert.Equal(48f, SidebarGeometry.RailWidth);
    }
}

// ── What core asks the structure (T5f: there is no core copy of the ladder any more) ─────────────────────────────────────
//
// Core's pane reads the ladder only through the kit (`NavRowKit`) and the rail's width through the policy. Classic answers from
// `SidebarGeometry`; these facts pin the answers, so a retune cannot leave core's seed extents, the list's inset or the drop
// resolver's depth ladder disagreeing with the rows.
public sealed class SidebarGeometryKitTests
{
    [Fact]
    public void TheKitAnswersFromTheLadder()
    {
        var kit = ClassicNavRows.Kit;
        Assert.Equal(new Wavee.Theming.Contracts.NavTreeLadder(SidebarGeometry.TreeContentX(0), SidebarGeometry.IndentStep), kit.TreeLadder);
        Assert.Equal(SidebarGeometry.ListInset, kit.ListInset);
        Assert.Equal(SidebarGeometry.PillH, kit.PillHeight);
        foreach (var shape in new[] { SidebarRowShape.Glyph, SidebarRowShape.EntityTwoLine, SidebarRowShape.EntityOneLine })
            Assert.Equal(SidebarGeometry.PitchOf(shape), kit.PitchOf(shape));
    }

    [Fact]
    public void TheListInset_IsTheItemMarginAndTheTopInset()
    {
        Assert.Equal(new FluentGpu.Foundation.Edges4(4f, 3f, 4f, 0f), SidebarGeometry.ListInset);
        Assert.Equal(8f, SidebarGeometry.PaneInsetH);
    }

    [Fact]
    public void TheRailWidth_IsThePolicys() => Assert.Equal(SidebarGeometry.RailWidth, ClassicPolicies.NavPane.RailWidth);

    [Fact]
    public void GridColumns_Clamped()
    {
        Assert.Equal(2, SidebarGeometry.GridColumns(240f));
        Assert.Equal(2, SidebarGeometry.GridColumns(300f));
        Assert.Equal(3, SidebarGeometry.GridColumns(420f));
        Assert.Equal(4, SidebarGeometry.GridColumns(560f));
        Assert.Equal(4, SidebarGeometry.GridColumns(900f));
        Assert.Equal(2, SidebarGeometry.GridColumns(float.NaN));
    }

    [Fact]
    public void LabelWidth_IsThePaneLessTheRowsLadder()
    {
        // 320 - 8 (list insets) - 0 (indent) - 42 (icon column) - 8 (label gap) - 14 (trailing pad) - 28 (trailing).
        Assert.Equal(220f, SidebarGeometry.LabelWidth(320f, 0, 28f));
        Assert.Equal(220f - 31f, SidebarGeometry.LabelWidth(320f, 1, 28f));
    }

    [Fact]
    public void TrailingWidth_CountsEveryPartTheRowLaysOut()
    {
        // S8: the cluster is a 6 lead margin, its parts and a 6 gap between them; nothing laid out takes nothing.
        Assert.Equal(0f, SidebarGeometry.TrailingWidth(false, false, 0f));
        Assert.Equal(6f + 26f, SidebarGeometry.TrailingWidth(false, false, SidebarGeometry.CountSlot));        // a count slot
        Assert.Equal(6f + 12f, SidebarGeometry.TrailingWidth(false, true, 0f));                                // the pin mark alone
        Assert.Equal(6f + 12f + 6f + 26f, SidebarGeometry.TrailingWidth(false, true, SidebarGeometry.CountSlot)); // pin + count
        Assert.Equal(6f + 12f + 6f + 12f + 6f + 24f,
            SidebarGeometry.TrailingWidth(true, true, SidebarGeometry.OfflineMarkBox));                        // eq + pin + a mark
    }

    [Fact]
    public void A_pinned_row_with_a_count_cuts_its_label_earlier_than_the_count_alone_did()
    {
        // S8: the overflow estimate includes the pin mark beside the count, so a name that fits beside a count alone can still
        // be cut once the pin mark stands next to it — and then it wears its tooltip.
        string name = new('x', 30);   // 30 × 7 = 210 DIP estimated
        float countOnly = SidebarGeometry.LabelWidth(320f, 0, SidebarGeometry.TrailingWidth(false, false, SidebarGeometry.CountSlot));
        float pinAndCount = SidebarGeometry.LabelWidth(320f, 0, SidebarGeometry.TrailingWidth(false, true, SidebarGeometry.CountSlot));
        Assert.False(SidebarLabelFit.Overflows(name, countOnly));
        Assert.True(SidebarLabelFit.Overflows(name, pinAndCount));
    }

    [Fact]
    public void The_offline_pin_mark_is_the_boards_24_box_and_its_cover_is_quieted()
    {
        // Board `C-Sidebar` (state offline): the 24 mark in the pin's place, 12 glyph, the cover at 0.45.
        Assert.Equal(24f, SidebarGeometry.OfflineMarkBox);
        Assert.Equal(12f, SidebarGeometry.OfflineMarkGlyph);
        Assert.Equal(0.45f, SidebarGeometry.OfflineArtOpacity);
        Assert.Equal("", SidebarGeometry.OfflineGlyph);
    }

    // ── the insertion caret's geometry (moved from SidebarDropCueTests with the pictures) ──

    [Fact]
    public void DropLineY_IsTheTopEdgeForBefore_AndTheBottomEdgeForAfter()
    {
        Assert.Equal(0f, SidebarGeometry.DropLineY(SidebarDropKind.Before, 44f));
        Assert.Equal(44f - SidebarGeometry.DropLineThickness, SidebarGeometry.DropLineY(SidebarDropKind.After, 44f));
        Assert.Equal(0f, SidebarGeometry.DropLineY(SidebarDropKind.EndOfList, SidebarGeometry.TreeEndHeight));
        // A degenerate row never produces a negative offset.
        Assert.Equal(0f, SidebarGeometry.DropLineY(SidebarDropKind.After, 1f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DropLineWidth_IsTheContentWidthMinusTheDepthIndent(int depth)
    {
        const float content = 300f;
        float expected = content - 31f * depth;
        Assert.Equal(expected, SidebarGeometry.DropLineWidth(content, depth), 3);
        // A deeper caret is strictly shorter: that IS the depth cue.
        if (depth > 0)
            Assert.True(SidebarGeometry.DropLineWidth(content, depth) < SidebarGeometry.DropLineWidth(content, depth - 1));
    }

    [Fact]
    public void DropLineWidth_NeverGoesNegative()
    {
        Assert.Equal(0f, SidebarGeometry.DropLineWidth(0f, 0));
        Assert.Equal(0f, SidebarGeometry.DropLineWidth(4f, 3));
    }
}
