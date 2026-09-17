// ── Wavee.Tests/PlaylistReorderRulesTests.cs — the same-list reorder gate, the index conventions, the keyed anchor ────
//
// Wave 4.5's gate for `Track.ReorderRules` (Entities/Track.Rules.cs), ported VERBATIM from 0.2.9's
// PlaylistReorderRulesTests (241 lines), with MoveRowsConventionTests' PURE assertions folded in (the section at the
// bottom). A drop names an insertion position through the DISPLAYED row order and the commit maps it back to a
// membership index, so the gate must refuse whenever that map is not the identity — a re-sort, a text query, any filter.
//
// Two deliberate omissions, both stated:
//   · `RowsAreKeyed` is `Drag.RowsAreKeyed` (Platform/Drag.cs) in 0.3 and DragTests pins it. Its EMPTY-set answer
//     changed on purpose (an empty set is keyed there, which keeps a foreign copy legal), so 0.2.9's "nothing to move is
//     not a keyed move" line does not port.
//   · MoveRowsConventionTests' server/local APPLIERS (`PlaylistMutationSource.BuildKeyedMove`, `UserPlaylistSource`) are
//     the playlist mutation seam's, not a rule here; what the rules own of that convention — the PRE-move index passes
//     through uncorrected, the end is the list length, moving up is unaffected — is pinned below.
//
// Pure: item ids are bare StringIds (only emptiness matters to the anchor), so no scope and no interner.

using FluentGpu.Foundation;
using Xunit;
using Rules = Wavee.Track.ReorderRules;

namespace Wavee.Tests;

public class PlaylistReorderRulesTests
{
    [Fact]
    public void NaturalUnfilteredOrderAllowsTheMove()
    {
        Assert.True(Rules.AllowsSameListMove(true, "", Track.FilterState.Default));
    }

    [Fact]
    public void ASortRefusesTheMove()
    {
        Assert.False(Rules.AllowsSameListMove(false, "", Track.FilterState.Default));
    }

    [Fact]
    public void ATextQueryRefusesTheMove()
    {
        Assert.False(Rules.AllowsSameListMove(true, "daft", Track.FilterState.Default));
    }

    /// <summary>0.2.9 ran this as a MemberData theory; a loop keeps every filter and every assertion without asking the
    /// runner to serialize a record struct.</summary>
    [Fact]
    public void AnyActiveFilterRefusesTheMove()
    {
        Track.FilterState[] activeFilters =
        [
            Track.FilterState.Default with { Flags = Track.FilterFlags.LikedOnly },
            Track.FilterState.Default with { Flags = Track.FilterFlags.PlayableOnly },
            Track.FilterState.Default with { ExplicitMode = Track.TraitMode.Hide },
            Track.FilterState.Default with { VideoMode = Track.TraitMode.Only },
            Track.FilterState.Default with { Duration = Track.DurationRange.UnderThreeMinutes },
            Track.FilterState.Default with { Added = Track.AddedRange.LastSevenDays },
            Track.FilterState.Default with { Origin = Track.OriginFilter.Local },
            Track.FilterState.Default with { Tempo = Track.TempoBand.From120To139 },
            Track.FilterState.Default with { Camelot = 16 },          // "8B"
            Track.FilterState.Default with { Tag = "K-Pop" },
        ];

        foreach (var filter in activeFilters)
        {
            Assert.False(filter.IsDefault);
            Assert.False(Rules.AllowsSameListMove(true, "", filter));
        }
    }

    // ── display<->original mapping: the drag payload carries ORIGINAL membership indices, while the framework's
    // virtual-removal math counts DISPLAY positions. Getting this backwards hides the wrong rows and mis-sizes the gap.

    [Fact]
    public void DisplayRowOf_IsTheIdentityInNaturalOrder()
    {
        int[] view = [0, 1, 2, 3, 4];
        Assert.Equal(0, Rules.DisplayRowOf(0, view));
        Assert.Equal(3, Rules.DisplayRowOf(3, view));
        Assert.Equal(4, Rules.DisplayRowOf(4, view));
    }

    [Fact]
    public void DisplayRowOf_InvertsAReorderedOrFilteredView()
    {
        // A sorted/filtered view: display 0 shows original 4, display 1 shows original 0, display 2 shows original 2.
        int[] view = [4, 0, 2];
        Assert.Equal(1, Rules.DisplayRowOf(0, view));
        Assert.Equal(2, Rules.DisplayRowOf(2, view));
        Assert.Equal(0, Rules.DisplayRowOf(4, view));
    }

    [Fact]
    public void DisplayRowOf_ReportsMinusOneForARowThatIsNotDisplayed()
    {
        int[] view = [4, 0, 2];
        Assert.Equal(-1, Rules.DisplayRowOf(1, view));
        Assert.Equal(-1, Rules.DisplayRowOf(9, view));
        Assert.Equal(-1, Rules.DisplayRowOf(0, Array.Empty<int>()));
    }

    // ── Alt+Up / Alt+Down block move ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BlockMove_InheritsTheDragGateAndAddsTheWriteGate()
    {
        Assert.True(Rules.AllowsBlockMove(true, true, "", Track.FilterState.Default));
        // A read-only playlist has nothing to reorder no matter how clean its display order is.
        Assert.False(Rules.AllowsBlockMove(false, true, "", Track.FilterState.Default));
        // …and every ambiguity that refuses the DRAG refuses the keystroke identically.
        Assert.False(Rules.AllowsBlockMove(true, false, "", Track.FilterState.Default));
        Assert.False(Rules.AllowsBlockMove(true, true, "daft", Track.FilterState.Default));
        Assert.False(Rules.AllowsBlockMove(true, true, "",
            Track.FilterState.Default with { ExplicitMode = Track.TraitMode.Only }));
    }

    [Fact]
    public void BlockMoveTarget_UsesThePreMoveInsertionConvention()
    {
        // [A,B,C,D]: moving B (1) DOWN one means "insert before the row currently at index 3" — the jumped row is still
        // counted at that moment, which is why the answer is max + 2 and not max + 1.
        Assert.Equal(3, Rules.BlockMoveTarget([1], 4, +1));
        // Moving C (2) UP one is simply "insert before B".
        Assert.Equal(1, Rules.BlockMoveTarget([2], 4, -1));
        // A contiguous block of two behaves the same way about its own extremes.
        Assert.Equal(4, Rules.BlockMoveTarget([1, 2], 5, +1));
        Assert.Equal(0, Rules.BlockMoveTarget([1, 2], 5, -1));
    }

    [Fact]
    public void BlockMoveTarget_RefusesAtTheBoundaries()
    {
        Assert.Equal(-1, Rules.BlockMoveTarget([0], 4, -1));
        Assert.Equal(-1, Rules.BlockMoveTarget([3], 4, +1));
        Assert.Equal(-1, Rules.BlockMoveTarget([2, 3], 4, +1));
        Assert.Equal(-1, Rules.BlockMoveTarget([0, 1], 4, -1));
    }

    [Fact]
    public void BlockMoveTarget_RefusesAGappedSelectionRatherThanInventingAMove()
    {
        // "One row up" has no single meaning for {B, D}: any answer would also close the gap between them.
        Assert.Equal(-1, Rules.BlockMoveTarget([1, 3], 5, +1));
        Assert.Equal(-1, Rules.BlockMoveTarget([1, 3], 5, -1));
        // Unordered input is still a contiguous run and is accepted; duplicates are not a run at all.
        Assert.Equal(4, Rules.BlockMoveTarget([2, 1], 5, +1));
        Assert.Equal(-1, Rules.BlockMoveTarget([1, 1], 5, +1));
    }

    [Fact]
    public void BlockMoveTarget_RefusesNonsenseInputs()
    {
        Assert.Equal(-1, Rules.BlockMoveTarget([], 4, +1));
        Assert.Equal(-1, Rules.BlockMoveTarget([0], 0, +1));
        Assert.Equal(-1, Rules.BlockMoveTarget([0], 4, 0));    // only ±1 is a "block move"
        Assert.Equal(-1, Rules.BlockMoveTarget([0], 4, +2));
        Assert.Equal(-1, Rules.BlockMoveTarget([9], 4, -1));   // out of range
    }

    // ── The drop caption's semantic claim ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VerbFor_DistinguishesAMoveFromACopyAndFromAnUnknownCount()
    {
        // Same playlist + membership rows behind it: the rows LEAVE their slots — a different edit than a copy.
        Assert.Equal(Track.DropVerb.MoveRows, Rules.VerbFor(true, 3, 3));
        // A foreign drop with a track snapshot knows exactly how many it will add.
        Assert.Equal(Track.DropVerb.AddTracks, Rules.VerbFor(false, 0, 12));
        // A container still behind a cold resolver does NOT — so it must caption without a number rather than say "1".
        Assert.Equal(Track.DropVerb.AddContainer, Rules.VerbFor(false, 0, 0));
        // "Same list" with no rows to move is not a move at all; nothing truthful is left to say.
        Assert.Equal(Track.DropVerb.None, Rules.VerbFor(true, 0, 5));
    }

    // ── the KEYED-reorder gate ─────────────────────────────────────────────────────────────────────────────────────
    // The wire reorder is ONE item-keyed MOV: every moved row is named by its membership item_id and the landing position
    // by ONE anchor row's item_id. No positional fallback, so both halves are answered BEFORE the gesture commits.

    /// <summary>Membership item ids, in ORIGINAL order ("" = an id that has not landed yet).</summary>
    static StringId[] Rows(params string[] ids)
    {
        var rows = new StringId[ids.Length];
        for (int i = 0; i < ids.Length; i++) rows[i] = ids[i].Length == 0 ? default : new StringId(100 + i);
        return rows;
    }

    /// <summary>The moved rows, by ORIGINAL index, each carrying its (landed) item id.</summary>
    static RowRef[] Moved(params int[] originalIndices)
    {
        var rows = new RowRef[originalIndices.Length];
        for (int i = 0; i < originalIndices.Length; i++) rows[i] = new RowRef(new StringId(500 + originalIndices[i]), originalIndices[i]);
        return rows;
    }

    [Fact]
    public void AnchorRowIsKeyed_TheTwoEndsNeedNoAnchorAtAll()
    {
        // add_first / add_last name no row, so an unkeyed neighbour at either end is irrelevant.
        var tracks = Rows("", "b", "c", "");
        Assert.True(Rules.AnchorRowIsKeyedAt(tracks, Moved(2), 0));
        Assert.True(Rules.AnchorRowIsKeyedAt(tracks, Moved(0), tracks.Length));
    }

    [Fact]
    public void AnchorRowIsKeyed_TakesThePredecessorInTheMiddle()
    {
        var tracks = Rows("a", "b", "c", "d");
        Assert.True(Rules.AnchorRowIsKeyedAt(tracks, Moved(3), 2));    // lands after "b"
        // …and refuses when that predecessor is the row whose id has not landed yet.
        Assert.False(Rules.AnchorRowIsKeyedAt(Rows("a", "", "c", "d"), Moved(3), 2));
    }

    [Fact]
    public void AnchorRowIsKeyed_WalksBackOverTheRowsThatAreThemselvesMoving()
    {
        // A GAPPED selection lands as one contiguous run, so the anchor is the nearest UNSELECTED row above the slot:
        // rows 1 and 2 are moving, so the anchor for slot 3 is row 0 — not row 2.
        var tracks = Rows("a", "b", "c", "d");
        Assert.True(Rules.AnchorRowIsKeyedAt(tracks, Moved(1, 2), 3));
        // The verdict follows THAT row: an unkeyed row 0 refuses even though the skipped rows are keyed.
        Assert.False(Rules.AnchorRowIsKeyedAt(Rows("", "b", "c", "d"), Moved(1, 2), 3));
        // Everything above the slot is moving → nothing is left to anchor to, which is add_first.
        Assert.True(Rules.AnchorRowIsKeyedAt(Rows("", "b", "c"), Moved(0, 1), 2));
    }

    [Fact]
    public void AnchorRowIsKeyed_ReadsDisplaySlotsThroughTheViewMap()
    {
        // The drop hands a DISPLAY slot; the anchor lives in MEMBERSHIP. In natural order the map is the identity…
        int[] natural = [0, 1, 2, 3];
        Assert.False(Rules.AnchorRowIsKeyed(natural, Rows("a", "", "c", "d"), Moved(3), 2));
        // …and the two edges resolve to first/end without consulting a row at all.
        Assert.Equal(0, Rules.OriginalInsertionIndex(natural, 4, 0));
        Assert.Equal(4, Rules.OriginalInsertionIndex(natural, 4, 4));
        Assert.Equal(2, Rules.OriginalInsertionIndex(natural, 4, 2));
        // An empty view still names the END of membership for any slot past it, and the head otherwise.
        Assert.Equal(0, Rules.OriginalInsertionIndex(Array.Empty<int>(), 0, 0));
    }

    // ── the toIndex convention (folded from 0.2.9's MoveRowsConventionTests) ────────────────────────────────────────
    // Rows [1,2] moved to index 5 of a 10-row list: 5 is read BEFORE the rows are lifted ("insert before the row
    // currently at index 5"), and the op discounts the lifted rows above the target itself. A caller that subtracted
    // them first would move the block two rows too far up — so the deposit path hands OriginalInsertionIndex through
    // UNMODIFIED, and the anchor it names is the row currently above that slot.

    static readonly int[] TenNatural = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    [Fact]
    public void TheDropIndexIsThePreMoveIndexAndIsNotCorrectedForTheLiftedRows()
    {
        var lifted = Moved(1, 2);

        // Display slot 5 names membership index 5 — NOT 3 (5 minus the two rows lifted above it).
        Assert.Equal(5, Rules.OriginalInsertionIndex(TenNatural, 10, 5));
        // The anchor of that pre-move index is row 4, the unlifted row currently above slot 5 …
        var ids = Rows("0", "1", "2", "3", "", "5", "6", "7", "8", "9");
        Assert.False(Rules.AnchorRowIsKeyed(TenNatural, ids, lifted, 5));
        // … and it is keyed whenever row 4 is, whatever the lifted rows carry.
        Assert.True(Rules.AnchorRowIsKeyed(TenNatural, Rows("0", "", "", "3", "4", "5", "6", "7", "8", "9"), lifted, 5));
        // The pre-corrected index (5 - 2 = 3) would name a DIFFERENT anchor (row 0, walking back over the lifted rows):
        // the two readings are not interchangeable, which is exactly why nothing may pre-correct.
        Assert.True(Rules.AnchorRowIsKeyedAt(ids, lifted, 3));
    }

    [Fact]
    public void MovingUpIsUnaffectedByTheConvention()
    {
        // Nothing is lifted above the target, so the pre- and post-removal readings coincide: slot 0 is the head.
        Assert.Equal(0, Rules.OriginalInsertionIndex(TenNatural, 10, 0));
        Assert.True(Rules.AnchorRowIsKeyedAt(Rows("", "1", "2", "3", "4", "", "", "7", "8", "9"), Moved(5, 6), 0));
        Assert.Equal(4, Rules.BlockMoveTarget([5, 6], 10, -1));
    }

    [Fact]
    public void AppendUsesTheListLength()
    {
        // The end of the display is the end of MEMBERSHIP — add_last, which names no anchor row at all.
        Assert.Equal(10, Rules.OriginalInsertionIndex(TenNatural, 10, 10));
        Assert.True(Rules.AnchorRowIsKeyedAt(Rows("0", "1", "2", "3", "4", "5", "6", "7", "8", ""), Moved(1, 2), 10));
    }
}
