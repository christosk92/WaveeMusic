using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The drop gap's geometry for the SHAPE Wavee's playlist page actually declares: ONE bound list carrying the track
/// rows AND, appended after them, the "Recommended songs" header plus its cards (DetailTracks: `listTotal = visible + 1
/// + recCount`), with <c>InsertionOptions.Range</c> bounding insertion to the track rows alone.
///
/// <para>The defect this pins: <c>InsertionPlan.DisplacementFor</c> returned a flat 0 for every item past the
/// insertable range, so dragging a "Recommended songs" card into the list at the very BOTTOM slot opened a gap that no
/// row moved for — and the in-gap preview, drawn at the gap's leading edge (which at a bottom slot IS the section's own
/// top), painted straight over the "Recommended songs" header.</para>
///
/// <para>The rule is NET growth, not "trailing rows never move": a same-list MOVE has Σremoval == N, so the content
/// height is invariant and the section correctly stays put (the original A12 case); a cross-list COPY genuinely grows
/// the list by the gap, so the section must make room.</para>
/// </summary>
public class PlaylistInsertionGeometryTests
{
    // The page shape: 2 persistent prefix items (the vertical hero + its chrome), 40 track rows, then the appended
    // "Recommended songs" header (item 42) and three rec cards (43..45).
    const int Prefix = 2, Tracks = 40, RecHeader = Prefix + Tracks;
    const float Row = 56f;

    static InsertionPlan At(int slot, int dragged, bool sameList)
        => SortableMath.Plan(Prefix, Tracks, slot, dragged, Row, sameList, SortableMath.DefaultPreviewCap);

    [Fact]
    public void CopyAtTheBottomSlot_PushesTheRecommendedSectionDownByTheGap()
    {
        var plan = At(slot: Tracks, dragged: 1, sameList: false);   // "insert after the last track"
        Assert.Equal(Row, plan.GapExtent, 3);

        // No track row moves — the gap is entirely below them...
        Assert.Equal(0f, plan.DisplacementFor(Prefix + Tracks - 1, default), 3);
        // ...so the appended section is the ONLY thing that can make room for it, and every one of its rows moves by
        // the same amount (header and cards stay glued together).
        Assert.Equal(Row, plan.DisplacementFor(RecHeader, default), 3);
        Assert.Equal(Row, plan.DisplacementFor(RecHeader + 1, default), 3);
        Assert.Equal(Row, plan.DisplacementFor(RecHeader + 3, default), 3);

        // The preview/line sit exactly where the section's header used to be — which is precisely why the section had
        // to move: leading extent (the measured prefix) + 40 rows.
        Assert.Equal(Prefix * Row + Tracks * Row, plan.PreviewOffset(Prefix * Row, default), 3);
    }

    [Fact]
    public void CopyOfManyTracks_MovesTheSectionByTheCappedGap_NotTheRawCount()
    {
        var plan = At(slot: 10, dragged: 500, sameList: false);
        Assert.Equal(SortableMath.DefaultPreviewCap * Row, plan.GapExtent, 3);
        // A track row below the slot and the appended section agree — one gap, one displacement.
        Assert.Equal(plan.DisplacementFor(Prefix + 10, default), plan.DisplacementFor(RecHeader, default), 3);
        Assert.Equal(SortableMath.DefaultPreviewCap * Row, plan.DisplacementFor(RecHeader, default), 3);
    }

    [Fact]
    public void SameListMove_LeavesTheRecommendedSectionExactlyWhereItIs()
    {
        // Two rows lifted out of the middle and dropped near the end: the gap is 2 rows, the two sources hide, and the
        // content height is invariant — so the appended section must NOT move (A12 as originally written).
        var plan = At(slot: 30, dragged: 2, sameList: true);
        System.Span<int> sources = stackalloc int[] { Prefix + 5, Prefix + 9 };

        Assert.Equal(2 * Row, plan.GapExtent, 3);
        Assert.Equal(0f, plan.DisplacementFor(RecHeader, sources), 3);
        Assert.Equal(0f, plan.DisplacementFor(RecHeader + 2, sources), 3);
        // ...and it agrees with the last insertable row, which is the whole invariant.
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
        // A brand-new playlist: no track rows at all, the "Recommended songs" section is the only thing under the drop.
        var plan = SortableMath.Plan(Prefix, 0, 0, 2, Row, sameList: false, SortableMath.DefaultPreviewCap);
        Assert.True(plan.IsActive);
        Assert.Equal(2 * Row, plan.GapExtent, 3);
        Assert.Equal(2 * Row, plan.DisplacementFor(Prefix, default), 3);   // the section rides it
        Assert.Equal(0f, plan.DisplacementFor(Prefix - 1, default), 3);    // the prefix does not
    }
}
