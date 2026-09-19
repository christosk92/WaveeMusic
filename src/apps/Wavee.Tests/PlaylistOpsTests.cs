// ── Wavee.Tests/PlaylistOpsTests.cs — the playlist4 op replayer (plan §3.3; §3.10's matrix) ──────────────────────────
//
// In-code lists named by letters (a row is `spotify:track:<x>` with item_id `<x>`; a rootlist entry is a bare uri). Each
// case says where its rule comes from:
//   CAPTURE — the op shape AND its outcome are what a 2026-09 capture showed (Fixtures/playlist-ops/README.md);
//   RULE    — rule-derived: no capture has shown it; the replayer implements the plan's reading and refuses on doubt.
// The same shapes replayed over the REAL scrubbed baselines are PlaylistDiffDecodeTests.

using Wavee;
using Xunit;

namespace Wavee.Tests;

// Imported INSIDE the namespace on purpose: the namespace-level `Wavee.Place` (Entities/Concert.cs) would otherwise
// win over the imported `PlaylistOps.Place`.
using static Wavee.PlaylistOps;

public class PlaylistOpsTests
{
    static WireItem T(string id) => new("spotify:track:" + id, ItemId: id);
    static WireItem Entry(string uri) => new(uri);
    static List<WireItem> ListOf(params string[] ids) => ids.Select(T).ToList();
    static string[] Ids(List<WireItem> list) => list.Select(r => r.ItemId ?? r.Uri).ToArray();

    static bool Apply(List<WireItem> list, Op[] ops, WireItem[] items, out Tally tally, out Misfit misfit,
        ListAttributeChange[]? lists = null)
        => TryApply<WireItem, WireItemAccess>(list, ops, items, lists ?? Array.Empty<ListAttributeChange>(), default,
            out _, out tally, out misfit);

    static bool Apply(List<WireItem> list, params Op[] ops) => Apply(list, ops, [], out _, out _);

    // ── MOV ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_forward_mov_lands_before_the_ORIGINAL_index_the_reorders_pair()
    {
        // reorders.saz: desktop asked "move M right after A" → echo MOV{0,1,2}; then "move Q right after A" → MOV{3,1,1}.
        // The second echo only makes sense if A sat at 0 and M at 1 after the first: to=2 is "before original index 2".
        var list = ListOf("M", "A", "x", "Q", "y");
        Assert.True(Apply(list, Op.Move(0, 1, 2)));
        Assert.Equal(new[] { "A", "M", "x", "Q", "y" }, Ids(list));
        Assert.True(Apply(list, Op.Move(3, 1, 1)));
        Assert.Equal(new[] { "A", "Q", "M", "x", "y" }, Ids(list));
    }

    [Fact]
    public void Capture_backward_block_mov_5_8_0_brings_the_eight_rows_to_the_head_in_order()
    {
        // reorders.saz r33: the keyed request moved eight rows add_first; the echo was ONE MOV{5,8,0}.
        var list = ListOf("p", "q", "r", "s", "t", "a", "b", "c", "d", "e", "f", "g", "h");
        Assert.True(Apply(list, Op.Move(5, 8, 0)));
        Assert.Equal(new[] { "a", "b", "c", "d", "e", "f", "g", "h", "p", "q", "r", "s", "t" }, Ids(list));
    }

    [Fact]
    public void Capture_forward_block_mov_0_3_5_in_six_rows_lands_at_two()
    {
        // more.saz 11→14: MOV{0,3,5} in a 6-row list. A FINAL-index reading would put a 3-row block at 5 (max 3): out of
        // range. Pre-removal: before original index 5 ⇒ 5 − 3 = 2.
        var list = ListOf("b", "a", "c", "e", "f", "g");
        Assert.True(Apply(list, Op.Move(0, 3, 5)));
        Assert.Equal(new[] { "e", "f", "b", "a", "c", "g" }, Ids(list));
    }

    [Fact]
    public void Capture_three_sequential_single_row_movs_agree_with_the_keyed_request_they_echo()
    {
        // reorders.saz r34: "move g, X, Z right after d" was echoed as MOV{6,1,4} · MOV{8,1,5} · MOV{10,1,6} — each
        // against the state the previous left.
        var positional = ListOf("a", "b", "c", "d", "e", "f", "g", "h", "X", "M", "Z", "p", "q");
        var keyed = positional.ToList();
        Assert.True(Apply(positional, Op.Move(6, 1, 4), Op.Move(8, 1, 5), Op.Move(10, 1, 6)));
        Assert.Equal(new[] { "a", "b", "c", "d", "g", "X", "Z", "e", "f", "h", "M", "p", "q" }, Ids(positional));

        WireItem[] items = [T("g"), T("X"), T("Z"), T("d")];
        Assert.True(Apply(keyed, [Op.MoveKeyed(0, 3, Place.AfterItem, anchor: 3)], items, out var tally, out _));
        Assert.Equal(Ids(positional), Ids(keyed));
        Assert.Equal(3, tally.Moved);
    }

    [Fact]
    public void Rule_a_destination_strictly_inside_the_moved_block_is_refused()
    {
        var list = ListOf("a", "b", "c", "d", "e", "f");
        Assert.False(Apply(list, [Op.Move(1, 3, 2)], [], out _, out var misfit));
        Assert.Equal(Refusal.OpShape, misfit.Why);
        Assert.Equal(new[] { "a", "b", "c", "d", "e", "f" }, Ids(list));
    }

    [Theory]
    [InlineData(1)]   // before its own first row
    [InlineData(3)]   // before the row right after it
    public void Rule_a_block_moved_to_where_it_already_is_stays(int to)
    {
        var list = ListOf("a", "b", "c", "d");
        Assert.True(Apply(list, Op.Move(1, 2, to)));
        Assert.Equal(new[] { "a", "b", "c", "d" }, Ids(list));
    }

    [Fact]
    public void Rule_to_equal_to_the_count_moves_the_block_to_the_end()
    {
        var list = ListOf("a", "b", "c", "d");
        Assert.True(Apply(list, Op.Move(0, 2, 4)));
        Assert.Equal(new[] { "c", "d", "a", "b" }, Ids(list));
    }

    [Theory]
    [InlineData(3, 2, 0)]   // the block runs past the end
    [InlineData(0, 1, 5)]   // to past the end
    [InlineData(-1, 1, 0)]
    public void A_mov_outside_the_list_is_refused(int from, int length, int to)
    {
        var list = ListOf("a", "b", "c", "d");
        Assert.False(Apply(list, [Op.Move(from, length, to)], [], out _, out var misfit));
        Assert.Equal(Refusal.IndexOutOfRange, misfit.Why);
        Assert.Equal(new[] { "a", "b", "c", "d" }, Ids(list));
    }

    // ── REM ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_three_sequential_rems_each_index_the_state_the_previous_left()
    {
        // reorders.saz r32: REM{2,1} · REM{4,2} · REM{10,1}, each carrying the rows it removes.
        var list = ListOf("a", "b", "K1", "c", "d", "K2", "K3", "e", "f", "g", "h", "i", "j", "K4", "k");
        WireItem[] items = [T("K1"), T("K2"), T("K3"), T("K4")];
        Op[] ops = [Op.Remove(2, 1, 0), Op.Remove(4, 2, 1), Op.Remove(10, 1, 3)];
        Assert.True(Apply(list, ops, items, out var tally, out _));
        Assert.Equal(new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k" }, Ids(list));
        Assert.Equal(4, tally.Removed);
    }

    [Fact]
    public void The_order_of_a_batch_is_its_meaning_the_same_rems_reversed_do_not_fit()
    {
        var list = ListOf("a", "b", "K1", "c", "d", "K2", "K3", "e", "f", "g", "h", "i", "j", "K4", "k");
        WireItem[] items = [T("K1"), T("K2"), T("K3"), T("K4")];
        Op[] reversed = [Op.Remove(10, 1, 3), Op.Remove(4, 2, 1), Op.Remove(2, 1, 0)];
        Assert.False(Apply(list, reversed, items, out _, out var misfit));
        Assert.Equal(0, misfit.OpIndex);
        Assert.Equal(Refusal.IdentityMismatch, misfit.Why);
        Assert.Equal(15, list.Count);
    }

    [Fact]
    public void Capture_an_index_rem_whose_carried_row_is_not_there_refuses_and_leaves_the_list_untouched()
    {
        var list = ListOf("a", "b", "c");
        Assert.False(Apply(list, [Op.Remove(1, 1, 0)], [T("x")], out _, out var misfit));
        Assert.Equal(Refusal.IdentityMismatch, misfit.Why);
        Assert.Equal(Kind.Rem, misfit.Kind);
        Assert.Equal(new[] { "a", "b", "c" }, Ids(list));
    }

    [Fact]
    public void Identity_is_the_item_id_when_both_rows_carry_one_so_a_repeated_uri_is_not_enough()
    {
        // One track twice in a playlist: two rows, one uri, two item_ids. The REM names the SECOND copy's id.
        var list = new List<WireItem> { new("spotify:track:dup", ItemId: "01"), new("spotify:track:dup", ItemId: "02") };
        Assert.False(Apply(list, [Op.Remove(0, 1, 0)], [new WireItem("spotify:track:dup", ItemId: "02")], out _, out var misfit));
        Assert.Equal(Refusal.IdentityMismatch, misfit.Why);
        Assert.True(Apply(list, [Op.Remove(1, 1, 0)], [new WireItem("spotify:track:dup", ItemId: "02")], out _, out _));
        Assert.Equal(new[] { "01" }, Ids(list));
    }

    [Fact]
    public void Identity_falls_back_to_the_uri_on_rootlist_rows_which_have_no_item_id()
    {
        var list = new List<WireItem> { Entry("spotify:playlist:A"), Entry("spotify:playlist:B") };
        Assert.False(Apply(list, [Op.Remove(0, 1, 0)], [Entry("spotify:playlist:B")], out _, out _));
        Assert.True(Apply(list, [Op.Remove(0, 1, 0)], [Entry("spotify:playlist:A")], out _, out _));
        Assert.Equal(new[] { "spotify:playlist:B" }, Ids(list));
    }

    [Fact]
    public void Rule_an_index_rem_that_does_not_carry_every_row_it_removes_is_not_replayed()
    {
        // 0.2.x removed blind when the counts disagreed; every captured REM carries its rows, so fewer is unverifiable.
        var list = ListOf("a", "b", "c");
        var op = new Op(Kind.Rem, From: 0, Length: 2, ItemsStart: 0, ItemsCount: 1);
        Assert.False(Apply(list, [op], [T("a")], out _, out var misfit));
        Assert.Equal(Refusal.OpShape, misfit.Why);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void An_index_rem_past_the_end_is_refused()
    {
        var list = ListOf("a", "b");
        Assert.False(Apply(list, [Op.Remove(1, 2, 0)], [T("b"), T("c")], out _, out var misfit));
        Assert.Equal(Refusal.IndexOutOfRange, misfit.Why);
    }

    [Fact]
    public void Capture_keyed_rems_remove_by_item_id_wherever_the_rows_are()
    {
        // reorders.saz request 452: three keyed REMs in one delta.
        var list = ListOf("a", "K1", "b", "K2", "K3", "c", "K4");
        WireItem[] items = [T("K1"), T("K2"), T("K3"), T("K4")];
        Assert.True(Apply(list, [Op.RemoveKeyed(0, 1), Op.RemoveKeyed(1, 2), Op.RemoveKeyed(3, 1)], items, out var tally, out _));
        Assert.Equal(new[] { "a", "b", "c" }, Ids(list));
        Assert.Equal(4, tally.Removed);
    }

    [Fact]
    public void A_keyed_rem_of_a_row_that_is_not_there_is_refused()
    {
        var list = ListOf("a", "b");
        Assert.False(Apply(list, [Op.RemoveKeyed(0, 2)], [T("a"), T("zz")], out _, out var misfit));
        Assert.Equal(Refusal.KeyAbsent, misfit.Why);
        Assert.Equal(new[] { "a", "b" }, Ids(list));
    }

    // ── ADD ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_sequential_adds_step_through_the_list_they_build()
    {
        // morediffs.saz 6→11: ADD{3} · ADD{4} · ADD{5}, one item each.
        var list = ListOf("b", "a", "c");
        WireItem[] items = [T("e"), T("f"), T("g")];
        Assert.True(Apply(list, [Op.AddAt(3, 0, 1), Op.AddAt(4, 1, 1), Op.AddAt(5, 2, 1)], items, out var tally, out _));
        Assert.Equal(new[] { "b", "a", "c", "e", "f", "g" }, Ids(list));
        Assert.Equal(3, tally.Added);
    }

    [Fact]
    public void Capture_a_rootlist_folder_create_is_one_add_of_two_markers_and_a_mov_puts_a_playlist_between_them()
    {
        // more.saz 135→137: ADD{0, [start-group, end-group]} then MOV{2,1,1}.
        var list = new List<WireItem> { Entry("spotify:playlist:P"), Entry("spotify:playlist:Q") };
        WireItem[] items = [Entry("spotify:start-group:f0:Folder+A"), Entry("spotify:end-group:f0")];
        Assert.True(Apply(list, [Op.AddAt(0, 0, 2), Op.Move(2, 1, 1)], items, out var tally, out _));
        Assert.Equal(new[] { "spotify:start-group:f0:Folder+A", "spotify:playlist:P", "spotify:end-group:f0", "spotify:playlist:Q" }, Ids(list));
        Assert.True(tally.Reconciles(2, list.Count));
    }

    [Fact]
    public void Capture_a_keyed_add_lands_right_after_its_anchor()
    {
        // reorders.saz request 511: ADD{items, add_after_item}; the echo was ADD{from_index = anchor + 1}.
        var list = ListOf("a", "b", "c");
        Assert.True(Apply(list, [Op.AddAt(Place.AfterItem, 0, 1, anchor: 1)], [T("n"), T("b")], out _, out _));
        Assert.Equal(new[] { "a", "b", "n", "c" }, Ids(list));
    }

    [Fact]
    public void Rule_add_first_and_add_last_land_at_the_ends()
    {
        var list = ListOf("a", "b");
        Assert.True(Apply(list, [Op.AddAt(Place.First, 0, 1), Op.AddAt(Place.Last, 1, 1)], [T("f"), T("l")], out _, out _));
        Assert.Equal(new[] { "f", "a", "b", "l" }, Ids(list));
    }

    [Fact]
    public void An_add_of_an_item_id_the_list_already_holds_is_refused()
    {
        // item_ids are unique per list: the list is not the one the op was computed against (0.2.x skipped it — right for
        // its optimistic echo, wrong for a replay).
        var list = ListOf("a", "b");
        Assert.False(Apply(list, [Op.AddAt(2, 0, 1)], [T("a")], out _, out var misfit));
        Assert.Equal(Refusal.DuplicateItemId, misfit.Why);
        Assert.Equal(new[] { "a", "b" }, Ids(list));
    }

    [Fact]
    public void An_add_after_an_anchor_that_is_not_there_is_refused()
    {
        var list = ListOf("a");
        Assert.False(Apply(list, [Op.AddAt(Place.AfterItem, 0, 1, anchor: 1)], [T("n"), T("zz")], out _, out var misfit));
        Assert.Equal(Refusal.AnchorAbsent, misfit.Why);
    }

    [Fact]
    public void An_add_may_append_but_not_land_past_the_end()
    {
        var list = ListOf("a");
        Assert.False(Apply(list, [Op.AddAt(2, 0, 1)], [T("n")], out _, out var misfit));
        Assert.Equal(Refusal.IndexOutOfRange, misfit.Why);
        Assert.True(Apply(list, [Op.AddAt(1, 0, 1)], [T("n")], out _, out _));
        Assert.Equal(new[] { "a", "n" }, Ids(list));
    }

    // ── keyed MOV ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_a_keyed_add_first_mov_lifts_the_rows_and_lands_them_in_op_order()
    {
        // reorders.saz request 458 (add_first) — the op's order is the landing order, not the list's.
        var list = ListOf("a", "b", "c", "d");
        Assert.True(Apply(list, [Op.MoveKeyed(0, 2, Place.First)], [T("d"), T("b")], out _, out _));
        Assert.Equal(new[] { "d", "b", "a", "c" }, Ids(list));
    }

    [Fact]
    public void Rule_a_keyed_add_last_mov_lands_at_the_end()
    {
        var list = ListOf("a", "b", "c");
        Assert.True(Apply(list, [Op.MoveKeyed(0, 1, Place.Last)], [T("a")], out _, out _));
        Assert.Equal(new[] { "b", "c", "a" }, Ids(list));
    }

    [Fact]
    public void A_keyed_mov_whose_anchor_is_one_of_the_moved_rows_is_refused()
    {
        var list = ListOf("a", "b", "c");
        Assert.False(Apply(list, [Op.MoveKeyed(0, 2, Place.AfterItem, anchor: 1)], [T("a"), T("b")], out _, out var misfit));
        Assert.Equal(Refusal.AnchorAbsent, misfit.Why);
        Assert.Equal(new[] { "a", "b", "c" }, Ids(list));
    }

    [Fact]
    public void A_keyed_mov_of_a_row_that_is_not_there_is_refused()
    {
        var list = ListOf("a", "b");
        Assert.False(Apply(list, [Op.MoveKeyed(0, 1, Place.First)], [T("zz")], out _, out var misfit));
        Assert.Equal(Refusal.KeyAbsent, misfit.Why);
    }

    [Fact]
    public void A_keyed_mov_naming_one_row_twice_needs_two_rows()
    {
        var list = ListOf("a", "b");
        Assert.False(Apply(list, [Op.MoveKeyed(0, 2, Place.Last)], [T("a"), T("a")], out _, out var misfit));
        Assert.Equal(Refusal.KeyAbsent, misfit.Why);
    }

    // ── UPDATE_LIST_ATTRIBUTES ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_a_header_op_rides_with_row_ops_and_comes_back_as_attrs()
    {
        // more.saz 11→14: MOV{0,3,5} · REM{0,2} · UPDATE_LIST_ATTRIBUTES{name, description} in ONE flat diff.
        var list = ListOf("b", "a", "c", "e", "f", "g");
        Op[] ops = [Op.Move(0, 3, 5), Op.Remove(0, 2, 0), Op.UpdateList(0)];
        ListAttributeChange[] lists = [new("New name", "A description")];
        Assert.True(TryApply<WireItem, WireItemAccess>(list, ops, [T("e"), T("f")], lists, default, out var attrs, out var tally, out _));
        Assert.Equal(new[] { "b", "a", "c", "g" }, Ids(list));
        Assert.Equal("New name", attrs.Name);
        Assert.Equal("A description", attrs.Description);
        Assert.Equal(ListAttrs.None, attrs.Unset);
        Assert.Equal(new Tally(Added: 0, Removed: 2, Moved: 3, ItemUpdates: 0, ListUpdates: 1), tally);
    }

    [Fact]
    public void An_unset_attribute_is_a_bit_never_an_empty_string_and_a_later_op_wins()
    {
        var list = ListOf("a");
        ListAttributeChange[] lists = [new(Name: "x", Description: "old"), new(Unset: ListAttrs.Description), new(Name: "y")];
        Assert.True(TryApply<WireItem, WireItemAccess>(list, [Op.UpdateList(0), Op.UpdateList(1), Op.UpdateList(2)], [], lists,
            default, out var attrs, out _, out _));
        Assert.Equal("y", attrs.Name);
        Assert.Null(attrs.Description);
        Assert.Equal(ListAttrs.Description, attrs.Unset);
        Assert.False(attrs.IsEmpty);
    }

    // ── UPDATE_ITEM_ATTRIBUTES (RULE: never captured) ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule_an_item_attribute_op_patches_the_row_at_its_index()
    {
        var list = new List<WireItem> { T("a"), new("spotify:track:b", ItemId: "b", AddedBy: "user1", Timestamp: 5, Present: ItemAttrs.AddedBy | ItemAttrs.Timestamp) };
        WireItem values = new("", AddedBy: "user2", Present: ItemAttrs.AddedBy);
        Assert.True(Apply(list, [Op.UpdateItem(1, 0, ItemAttrs.AddedBy, ItemAttrs.Timestamp)], [values], out var tally, out _));
        Assert.Equal("user2", list[1].AddedBy);
        Assert.Equal(0L, list[1].Timestamp);
        Assert.Equal(ItemAttrs.AddedBy, list[1].Present);
        Assert.Equal("b", list[1].ItemId);
        Assert.Equal(1, tally.ItemUpdates);
    }

    [Fact]
    public void Rule_an_item_attribute_op_whose_old_state_is_not_the_rows_is_refused()
    {
        var list = new List<WireItem> { new("spotify:track:a", ItemId: "a", AddedBy: "user1", Present: ItemAttrs.AddedBy) };
        var op = new Op(Kind.UpdateItemAttributes, From: 0, ItemsStart: 0, ItemsCount: 2, Set: ItemAttrs.AddedBy, OldSet: ItemAttrs.AddedBy);
        WireItem[] items = [new("", AddedBy: "user2", Present: ItemAttrs.AddedBy), new("", AddedBy: "user9", Present: ItemAttrs.AddedBy)];
        Assert.False(Apply(list, [op], items, out _, out var misfit));
        Assert.Equal(Refusal.IdentityMismatch, misfit.Why);
        Assert.Equal("user1", list[0].AddedBy);
    }

    [Fact]
    public void An_item_attribute_op_outside_the_list_is_refused()
    {
        var list = ListOf("a");
        Assert.False(Apply(list, [Op.UpdateItem(1, 0, ItemAttrs.Public)], [new WireItem("", Public: true, Present: ItemAttrs.Public)], out _, out var misfit));
        Assert.Equal(Refusal.IndexOutOfRange, misfit.Why);
    }

    // ── the batch as a whole ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_misfit_mid_batch_leaves_the_list_exactly_as_it_was()
    {
        var list = ListOf("a", "b", "c");
        Op[] ops = [Op.Move(0, 1, 3), Op.AddAt(0, 0, 1), Op.Remove(1, 1, 1)];
        Assert.False(Apply(list, ops, [T("n"), T("zz")], out var tally, out var misfit));
        Assert.Equal(2, misfit.OpIndex);
        Assert.Equal(Refusal.IdentityMismatch, misfit.Why);
        Assert.Equal(new[] { "a", "b", "c" }, Ids(list));
        Assert.Equal(default(Tally), tally);
    }

    [Fact]
    public void The_tally_is_what_was_applied_and_the_stored_total_must_reconcile_with_it()
    {
        var list = ListOf("a", "b", "c", "d");
        Op[] ops = [Op.Move(0, 1, 2), Op.Remove(3, 1, 0), Op.AddAt(3, 1, 1), Op.AddAt(4, 2, 1), Op.AddAt(5, 3, 1)];
        Assert.True(Apply(list, ops, [T("d"), T("e"), T("f"), T("g")], out var tally, out _));
        Assert.Equal(3, tally.Added);
        Assert.Equal(1, tally.Removed);
        Assert.True(tally.Reconciles(4, list.Count));
        Assert.False(tally.Reconciles(4, list.Count + 1));      // a stored total that drifted is a full read
    }

    [Theory]
    [InlineData(4, 6, 3, 1, true)]
    [InlineData(38, 40, 2, 0, true)]
    [InlineData(4, 4, 0, 0, true)]
    [InlineData(4, 5, 3, 1, false)]
    [InlineData(-1, 0, 1, 0, false)]
    public void Reconciles_is_baseline_plus_adds_minus_removes(int baseline, int replayed, int added, int removed, bool expected)
        => Assert.Equal(expected, Reconciles(baseline, replayed, added, removed));

    [Fact]
    public void An_op_whose_rows_lie_outside_the_item_span_is_malformed()
    {
        var list = ListOf("a");
        Assert.False(Apply(list, [Op.AddAt(0, 1, 1)], [T("n")], out _, out var misfit));
        Assert.Equal(Refusal.Malformed, misfit.Why);
    }

    [Fact]
    public void An_empty_batch_applies_and_changes_nothing()
    {
        var list = ListOf("a", "b");
        Assert.True(Apply(list, [], [], out var tally, out _));
        Assert.Equal(default(Tally), tally);
        Assert.Equal(new[] { "a", "b" }, Ids(list));
    }

    // ── the caller's own row type ────────────────────────────────────────────────────────────────────────────────────

    readonly record struct Row(string Uri, string? Id, int Slot);

    /// <summary>A caller whose row holds no item attributes: every patch is refused.</summary>
    readonly struct RowAccess : IRowAccess<Row>
    {
        public bool SameUri(in Row a, in Row b) => a.Uri == b.Uri;
        public bool HasItemId(in Row row) => row.Id is not null;
        public bool SameItemId(in Row a, in Row b) => a.Id == b.Id;
        public bool Holds(in Row row, in Row values, ItemAttrs set, ItemAttrs unset) => set == ItemAttrs.None && unset == ItemAttrs.None;
        public bool TryPatch(ref Row row, in Row values, ItemAttrs set, ItemAttrs unset) => false;
    }

    [Fact]
    public void The_replay_runs_over_the_callers_row_type_through_its_accessor()
    {
        var list = new List<Row> { new("u:a", "a", 1), new("u:b", "b", 2), new("u:c", "c", 3) };
        Row[] items = [new("u:c", "c", 0), new("u:n", "n", 9)];
        Assert.True(TryApply<Row, RowAccess>(list, [Op.Move(0, 1, 3), Op.Remove(1, 1, 0), Op.AddAt(0, 1, 1)], items, [], default,
            out _, out var tally, out _));
        Assert.Equal(new[] { 9, 2, 1 }, list.Select(r => r.Slot).ToArray());
        Assert.True(tally.Reconciles(3, list.Count));

        Assert.False(TryApply<Row, RowAccess>(list, [Op.UpdateItem(0, 0, ItemAttrs.AddedBy)], [new Row("", null, 0)], [], default,
            out _, out _, out var misfit));
        Assert.Equal(Refusal.PatchRefused, misfit.Why);
    }
}
