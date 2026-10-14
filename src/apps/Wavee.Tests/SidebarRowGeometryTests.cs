using System.Collections.Generic;
using System.Reflection;
using Wavee;
using Wavee.Themes.Classic;
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

// ── The transitional mirror: core's `SidebarRowGeometry` equals Classic's `SidebarGeometry` (T5a) ────────────────────────
//
// The ladder is Classic's (`Themes/Classic/Structure/Rules/SidebarGeometry.cs`). Core keeps a copy of exactly the members the
// sub-surfaces that are still core read (the heads, the flyouts, the Edit outline, the drop rules, the resize rules, the shared
// row primitives), until T5b-f move them. A copy that drifts is a sidebar whose head and rows disagree, so every shared constant
// and every shared function is pinned equal here; the day the last reader moves, the copy and this class go.
public sealed class SidebarGeometryParityTests
{
    [Fact]
    public void Every_constant_core_keeps_is_the_value_classic_declares()
    {
        var mismatches = new List<string>();
        foreach (var core in typeof(SidebarRowGeometry).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!core.IsLiteral) continue;
            var classic = typeof(SidebarGeometry).GetField(core.Name, BindingFlags.Public | BindingFlags.Static);
            if (classic is null || !classic.IsLiteral) { mismatches.Add(core.Name + ": missing in Classic"); continue; }
            if (!Equals(classic.GetRawConstantValue(), core.GetRawConstantValue()))
                mismatches.Add(core.Name + ": Classic " + classic.GetRawConstantValue() + ", core " + core.GetRawConstantValue());
        }
        Assert.Empty(mismatches);
    }

    [Theory]
    [InlineData(SidebarRowShape.Glyph)]
    [InlineData(SidebarRowShape.EntityTwoLine)]
    [InlineData(SidebarRowShape.EntityOneLine)]
    public void The_shape_functions_agree(SidebarRowShape shape)
    {
        Assert.Equal(SidebarGeometry.HeightOf(shape), SidebarRowGeometry.HeightOf(shape));
        Assert.Equal(SidebarGeometry.PitchOf(shape), SidebarRowGeometry.PitchOf(shape));
        Assert.Equal(SidebarGeometry.ArtOf(shape), SidebarRowGeometry.ArtOf(shape));
        Assert.Equal(SidebarGeometry.SubtitleVisible(shape, "48 songs"), SidebarRowGeometry.SubtitleVisible(shape, "48 songs"));
        Assert.Equal(SidebarGeometry.SubtitleVisible(shape, ""), SidebarRowGeometry.SubtitleVisible(shape, ""));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(9)]
    public void The_depth_functions_agree(int depth)
    {
        Assert.Equal(SidebarGeometry.ClampDepth(depth), SidebarRowGeometry.ClampDepth(depth));
        Assert.Equal(SidebarGeometry.IndentFor(depth), SidebarRowGeometry.IndentFor(depth));
        Assert.Equal(SidebarGeometry.TreeContentX(depth), SidebarRowGeometry.TreeContentX(depth));
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
