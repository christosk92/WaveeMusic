// ── Wavee.Tests/PlaylistInsertionGeometryTests.cs — WP-5.O stream A ─────────────────────────────────────────────────
// 0.2.9's PlaylistInsertionGeometryTests, verbatim: the gap geometry lives in the engine (`SortableMath`), and this pins
// it for the SHAPE the playlist page declares — the track rows plus the ONE appended "Recommended songs" section.

using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

/// <summary>The drop gap's geometry for ONE bound list carrying the track rows AND, appended after them, the recommendations
/// header plus its cards, with insertion bounded to the track rows. The rule is NET growth: a same-list MOVE keeps the
/// content height invariant (the section stays put); a cross-list COPY grows the list by the gap (the section makes room).</summary>
public class PlaylistInsertionGeometryTests
{
    const int Prefix = 2, Tracks = 40, RecHeader = Prefix + Tracks;
    const float Row = 56f;

    static InsertionPlan At(int slot, int dragged, bool sameList)
        => SortableMath.Plan(Prefix, Tracks, slot, dragged, Row, sameList, SortableMath.DefaultPreviewCap);

    [Fact]
    public void CopyAtTheBottomSlot_PushesTheRecommendedSectionDownByTheGap()
    {
        var plan = At(slot: Tracks, dragged: 1, sameList: false);
        Assert.Equal(Row, plan.GapExtent, 3);
        Assert.Equal(0f, plan.DisplacementFor(Prefix + Tracks - 1, default), 3);
        Assert.Equal(Row, plan.DisplacementFor(RecHeader, default), 3);
        Assert.Equal(Row, plan.DisplacementFor(RecHeader + 1, default), 3);
        Assert.Equal(Row, plan.DisplacementFor(RecHeader + 3, default), 3);
        Assert.Equal(Prefix * Row + Tracks * Row, plan.PreviewOffset(Prefix * Row, default), 3);
    }

    [Fact]
    public void CopyOfManyTracks_MovesTheSectionByTheCappedGap_NotTheRawCount()
    {
        var plan = At(slot: 10, dragged: 500, sameList: false);
        Assert.Equal(SortableMath.DefaultPreviewCap * Row, plan.GapExtent, 3);
        Assert.Equal(plan.DisplacementFor(Prefix + 10, default), plan.DisplacementFor(RecHeader, default), 3);
        Assert.Equal(SortableMath.DefaultPreviewCap * Row, plan.DisplacementFor(RecHeader, default), 3);
    }

    [Fact]
    public void SameListMove_LeavesTheRecommendedSectionExactlyWhereItIs()
    {
        var plan = At(slot: 30, dragged: 2, sameList: true);
        System.Span<int> sources = stackalloc int[] { Prefix + 5, Prefix + 9 };

        Assert.Equal(2 * Row, plan.GapExtent, 3);
        Assert.Equal(0f, plan.DisplacementFor(RecHeader, sources), 3);
        Assert.Equal(0f, plan.DisplacementFor(RecHeader + 2, sources), 3);
        Assert.Equal(plan.DisplacementFor(Prefix + Tracks - 1, sources), plan.DisplacementFor(RecHeader, sources), 3);
    }

    [Fact]
    public void SameListMoveToTheVeryBottom_StillLeavesTheSectionPut()
    {
        var plan = At(slot: Tracks, dragged: 1, sameList: true);
        System.Span<int> sources = stackalloc int[] { Prefix + 0 };
        Assert.Equal(0f, plan.DisplacementFor(RecHeader, sources), 3);
    }

    [Fact]
    public void TheStickyPrefixNeverMoves()
    {
        var copy = At(slot: 0, dragged: 2, sameList: false);
        Assert.Equal(0f, copy.DisplacementFor(0, default), 3);
        Assert.Equal(0f, copy.DisplacementFor(1, default), 3);
        Assert.Equal(2 * Row, copy.DisplacementFor(Prefix, default), 3);
    }

    [Fact]
    public void AnEmptyPlaylistStillOpensAGapTheSectionMakesRoomFor()
    {
        var plan = SortableMath.Plan(Prefix, 0, 0, 2, Row, sameList: false, SortableMath.DefaultPreviewCap);
        Assert.True(plan.IsActive);
        Assert.Equal(2 * Row, plan.GapExtent, 3);
        Assert.Equal(2 * Row, plan.DisplacementFor(Prefix, default), 3);
        Assert.Equal(0f, plan.DisplacementFor(Prefix - 1, default), 3);
    }

    /// <summary>The playlist page's preview cap is the engine's, never a local literal (ch 06 §8 PlaylistInsertionPreview.Cap).</summary>
    [Fact]
    public void ThePreviewCardCap_IsTheEnginesCap() => Assert.Equal(SortableMath.DefaultPreviewCap, Wavee.Drag.PreviewCap);
}
