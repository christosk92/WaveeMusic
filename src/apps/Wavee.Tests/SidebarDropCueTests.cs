using System;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE CUE INVARIANT — <b>line ⟺ Before/After/EndOfList, plate ⟺ Into, never both</b>.
///
/// <para>This is the whole of D1. Every rootlist drop used to be a whole-row target whose meaning was a hidden vertical
/// hit test, and the row drew the same accent plate for all three outcomes — so the surface physically could not say
/// whether a drop would insert above, insert below, or deposit inside. One plate meant three things, and the user's
/// verdict was "counter-intuitive". The plate now means exactly one thing, and an ordering draws a caret instead.</para>
/// </summary>
public class SidebarDropCueTests
{
    static SidebarDropSlot Slot(SidebarDropKind kind, int depth = 0)
        => new(4, kind, depth, SidebarDropRefusal.None);

    [Theory]
    [InlineData(SidebarDropKind.Before)]
    [InlineData(SidebarDropKind.After)]
    [InlineData(SidebarDropKind.EndOfList)]
    public void OrderingKinds_DrawTheLineAndNeverThePlate(SidebarDropKind kind)
    {
        Assert.True(SidebarDropCue.DrawsLine(kind));
        Assert.False(SidebarDropCue.DrawsPlate(kind));
        var slot = Slot(kind);
        Assert.True(slot.DrawsLine);
        Assert.False(slot.DrawsPlate);
        Assert.True(slot.IsArmed);
    }

    [Fact]
    public void Into_DrawsThePlateAndNeverTheLine()
    {
        Assert.True(SidebarDropCue.DrawsPlate(SidebarDropKind.Into));
        Assert.False(SidebarDropCue.DrawsLine(SidebarDropKind.Into));
        var slot = Slot(SidebarDropKind.Into);
        Assert.True(slot.DrawsPlate);
        Assert.False(slot.DrawsLine);
        Assert.True(slot.IsArmed);
    }

    [Fact]
    public void NoKindEverDrawsBoth_AndNoneDrawsNeither()
    {
        foreach (SidebarDropKind kind in Enum.GetValues<SidebarDropKind>())
        {
            bool line = SidebarDropCue.DrawsLine(kind);
            bool plate = SidebarDropCue.DrawsPlate(kind);
            Assert.False(line && plate);
            // ...and exactly one cue for every ARMED kind: an armed slot that drew nothing would be a target the user
            // cannot see, which is the other half of the same defect.
            Assert.Equal(kind != SidebarDropKind.None, line || plate);
        }
    }

    [Theory]
    [InlineData(SidebarDropRefusal.Self)]
    [InlineData(SidebarDropRefusal.IntoItself)]
    [InlineData(SidebarDropRefusal.IntoDescendant)]
    [InlineData(SidebarDropRefusal.NoOp)]
    [InlineData(SidebarDropRefusal.SortedList)]
    [InlineData(SidebarDropRefusal.NotLoaded)]
    [InlineData(SidebarDropRefusal.Unavailable)]
    public void ARefusedSlotDrawsNeither(SidebarDropRefusal refusal)
    {
        // The resolver's structural guarantee: a refusal always carries Kind = None, so the cue rule stays total and the
        // surface never promises a drop it will refuse.
        var slot = new SidebarDropSlot(4, SidebarDropKind.None, 2, refusal);
        Assert.False(slot.DrawsLine);
        Assert.False(slot.DrawsPlate);
        Assert.False(slot.IsArmed);
    }

    [Fact]
    public void RefusalsFromTheResolverAlwaysCarryKindNone()
    {
        var facts = new SidebarRowFacts(
            IsFolder: true, FolderExpanded: false, FolderHasChildren: true, Depth: 1, NextVisibleDepth: 0,
            CenterAccepts: true, SourceIsSelf: true, SortedNonCustom: true,
            RootlistLoaded: true);
        foreach (float t in new[] { 0f, 0.2f, 0.5f, 0.8f, 1f })
        {
            var slot = RootlistSlotResolver.Resolve(4, t, 100f, 44f, in facts, SidebarDropSlot.None);
            Assert.NotEqual(SidebarDropRefusal.None, slot.Refusal);
            Assert.False(slot.DrawsLine || slot.DrawsPlate);
        }
    }

    // ── the caret's geometry ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LineY_IsTheTopEdgeForBefore_AndTheBottomEdgeForAfter()
    {
        Assert.Equal(0f, SidebarDropCue.LineY(SidebarDropKind.Before, 44f));
        Assert.Equal(44f - SidebarDropCue.LineThickness, SidebarDropCue.LineY(SidebarDropKind.After, 44f));
        // The tree's end marker IS the slot, so its caret sits at the top of that row.
        Assert.Equal(0f, SidebarDropCue.LineY(SidebarDropKind.EndOfList, SidebarRowGeometry.TreeEndHeight));
        // A degenerate row never produces a negative offset.
        Assert.Equal(0f, SidebarDropCue.LineY(SidebarDropKind.After, 1f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void LineWidth_IsTheContentLaneMinusTheDepthIndent(int depth)
    {
        const float content = 300f;
        // THE TREE-CONTENT ORIGIN, not the outer indent ladder: the caret starts where the row at that depth starts
        // DRAWING (gutter + connector cells + the disclosure cell), which is the whole of the F2 fix.
        float expected = content - SidebarRowGeometry.TreeContentX(depth) - SidebarRowGeometry.RowInsetRight;
        Assert.Equal(expected, SidebarDropCue.LineWidth(content, depth), 3);
        // A deeper caret is strictly shorter — that IS the depth cue.
        if (depth > 0)
            Assert.True(SidebarDropCue.LineWidth(content, depth) < SidebarDropCue.LineWidth(content, depth - 1));
    }

    [Fact]
    public void LineWidth_NeverGoesNegative()
    {
        Assert.Equal(0f, SidebarDropCue.LineWidth(0f, 0));
        Assert.Equal(0f, SidebarDropCue.LineWidth(4f, 3));
    }
}
