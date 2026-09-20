// ── Wavee.Tests/QueueOrderTests.cs — the queue's order rules and the forward-looking split ────────────────────────────
//
// `Entities/Queue.Rules.cs`: the optimistic move (remove + insert, never a swap — 0.2.9 QueueOrderTests' equivalence),
// the cursor FOLLOW that puts buckets back after the reducer moved the deck, the SKIP a row click makes, and the section
// split every queue surface renders from. Pure over spans — no scope, no engine loop. The live session writes built on
// these are QueueSessionTests.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class QueueOrderTests
{
    static QueueEdge Row(QueueBucket bucket, ulong id, QueueProvider provider = QueueProvider.Context)
        => new(id, (byte)provider, (byte)bucket);

    static QueueEdge User(ulong id) => Row(QueueBucket.UserQueue, id, QueueProvider.Queue);

    static string Ids(ReadOnlySpan<QueueEdge> rows)
    {
        var parts = new string[rows.Length];
        for (int i = 0; i < rows.Length; i++) parts[i] = rows[i].ItemId.ToString();
        return string.Join(",", parts);
    }

    static string Buckets(ReadOnlySpan<QueueEdge> rows)
    {
        var parts = new char[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            parts[i] = (QueueBucket)rows[i].Bucket switch
            {
                QueueBucket.History => 'H', QueueBucket.NowPlaying => 'N', QueueBucket.UserQueue => 'Q', _ => 'U',
            };
        return new string(parts);
    }

    static int[] TargetsFor(QueueEdge[] rows)
    {
        var t = new int[rows.Length];
        for (int i = 0; i < rows.Length; i++) t[i] = (int)rows[i].ItemId * 10;
        return t;
    }

    // ── move: remove + insert, not swap ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_multi_slot_move_shifts_every_row_between()
    {
        QueueEdge[] rows = [User(1), User(2), User(3), User(4), User(5)];
        int[] all = [0, 1, 2, 3, 4];

        var down = (QueueEdge[])rows.Clone();
        Assert.True(QueueOrder.Move(TargetsFor(down), down, all, 0, 2));
        Assert.Equal("2,3,1,4,5", Ids(down));

        var up = (QueueEdge[])rows.Clone();
        Assert.True(QueueOrder.Move(TargetsFor(up), up, all, 4, 1));
        Assert.Equal("1,5,2,3,4", Ids(up));
    }

    [Fact]
    public void An_adjacent_move_is_the_old_swap()
    {
        for (int i = 0; i < 4; i++)
            foreach (int delta in new[] { -1, 1 })
            {
                int to = i + delta;
                if ((uint)to >= 4) continue;
                QueueEdge[] rows = [User(1), User(2), User(3), User(4)];
                var swapped = (QueueEdge[])rows.Clone();
                (swapped[i], swapped[to]) = (swapped[to], swapped[i]);
                QueueOrder.Move(TargetsFor(rows), rows, new[] { 0, 1, 2, 3 }, i, to);
                Assert.Equal(Ids(swapped), Ids(rows));
            }
    }

    [Fact]
    public void A_move_rewrites_only_its_own_sections_positions_and_moves_the_target_with_the_payload()
    {
        // Interleaved on purpose: a section is a SUBSEQUENCE, so the other rows never shift.
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 90), User(1), Row(QueueBucket.NextUp, 91), User(2), User(3), Row(QueueBucket.NextUp, 92, QueueProvider.Autoplay)];
        int[] targets = TargetsFor(rows);
        Assert.True(QueueOrder.Move(targets, rows, new[] { 1, 3, 4 }, 2, 0));
        Assert.Equal("90,3,91,1,2,92", Ids(rows));
        for (int i = 0; i < rows.Length; i++) Assert.Equal((int)rows[i].ItemId * 10, targets[i]);
    }

    [Fact]
    public void Move_clamps_the_target_and_a_no_op_reports_false()
    {
        QueueEdge[] rows = [User(1), User(2), User(3)];
        int[] all = [0, 1, 2];
        Assert.False(QueueOrder.Move(TargetsFor(rows), rows, all, 1, 1));
        Assert.False(QueueOrder.Move(TargetsFor(rows), rows, all, 7, 0));
        Assert.False(QueueOrder.Move(TargetsFor(rows), rows, ReadOnlySpan<int>.Empty, 0, 1));
        Assert.True(QueueOrder.Move(TargetsFor(rows), rows, all, 0, 99));
        Assert.Equal("2,3,1", Ids(rows));
    }

    // ── follow ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Follow_marks_history_behind_the_cursor_and_the_deck_on_it()
    {
        // Two Next clicks the reducer made without touching buckets: the deck is on row 2.
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1), User(2), Row(QueueBucket.NextUp, 3), Row(QueueBucket.NextUp, 4)];
        Assert.True(QueueOrder.Follow(TargetsFor(rows), rows, 2));
        Assert.Equal("HHNU", Buckets(rows));
        Assert.Equal("1,2,3,4", Ids(rows));                                    // nothing moved
        Assert.True(Queue.IsOrdered(rows));
        Assert.False(QueueOrder.Follow(TargetsFor(rows), rows, 2));            // idempotent
    }

    [Fact]
    public void After_a_retreat_the_old_deck_row_goes_behind_the_users_queue_and_the_cursor_index_stays()
    {
        // Previous from a context row with a queued row waiting after it: the old deck row is the context's again, and
        // the user's queue still plays first — so the queued row slides in front of it.
        QueueEdge[] rows = [Row(QueueBucket.History, 1), Row(QueueBucket.NowPlaying, 2), User(3), Row(QueueBucket.NextUp, 4)];
        int[] targets = TargetsFor(rows);
        Assert.True(QueueOrder.Follow(targets, rows, 0));
        Assert.Equal("1,3,2,4", Ids(rows));
        Assert.Equal("NQUU", Buckets(rows));
        Assert.True(Queue.IsOrdered(rows));
        for (int i = 0; i < rows.Length; i++) Assert.Equal((int)rows[i].ItemId * 10, targets[i]);
    }

    [Fact]
    public void Follow_refuses_a_cursor_that_names_no_row()
    {
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1)];
        Assert.False(QueueOrder.Follow(TargetsFor(rows), rows, -1));
        Assert.False(QueueOrder.Follow(TargetsFor(rows), rows, 5));
        Assert.Equal("N", Buckets(rows));
    }

    // ── skip ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_click_on_a_queued_row_consumes_the_queued_rows_before_it()
    {
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1), User(2), User(3), User(4), Row(QueueBucket.NextUp, 5)];
        Assert.Equal(3, QueueOrder.Skip(TargetsFor(rows), rows, 3));
        Assert.Equal("1,2,3,4,5", Ids(rows));
        Assert.Equal("HHHNU", Buckets(rows));
    }

    [Fact]
    public void A_click_on_a_next_up_row_keeps_the_users_queue_behind_it()
    {
        // 0.2.9 SkipToUpcomingIndex: the skipped continuation rows leave the upcoming list; the queue is NOT consumed.
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1), User(2), User(3), Row(QueueBucket.NextUp, 4), Row(QueueBucket.NextUp, 5), Row(QueueBucket.NextUp, 6)];
        int[] targets = TargetsFor(rows);
        int at = QueueOrder.Skip(targets, rows, 4);
        Assert.Equal(2, at);
        Assert.Equal("1,4,5,2,3,6", Ids(rows));
        Assert.Equal("HHNQQU", Buckets(rows));
        Assert.True(Queue.IsOrdered(rows));
        for (int i = 0; i < rows.Length; i++) Assert.Equal((int)rows[i].ItemId * 10, targets[i]);
    }

    [Fact]
    public void A_click_on_history_or_the_deck_is_not_a_skip()
    {
        QueueEdge[] rows = [Row(QueueBucket.History, 1), Row(QueueBucket.NowPlaying, 2), Row(QueueBucket.NextUp, 3)];
        Assert.Equal(-1, QueueOrder.Skip(TargetsFor(rows), rows, 0));
        Assert.Equal(-1, QueueOrder.Skip(TargetsFor(rows), rows, 1));
        Assert.Equal(-1, QueueOrder.Skip(TargetsFor(rows), rows, 9));
    }

    // ── the forward-looking split (ch 21 §0 #5, §7) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_divider_is_the_local_cursor_else_the_now_playing_row()
    {
        QueueEdge[] rows = [Row(QueueBucket.History, 1), Row(QueueBucket.NowPlaying, 2), Row(QueueBucket.NextUp, 3)];
        Assert.Equal(2, Queue.Divider(rows, 2));
        Assert.Equal(1, Queue.Divider(rows, -1));                              // a remote mirror / nothing played yet
        Assert.Equal(1, Queue.Divider(rows, 9));
        QueueEdge[] headless = [Row(QueueBucket.UserQueue, 1, QueueProvider.Queue)];
        Assert.Equal(-1, Queue.Divider(headless, -1));
    }

    [Fact]
    public void The_split_is_by_provenance_after_the_divider_with_nothing_behind_it()
    {
        // The reducer is on row 3 (unfollowed): rows 0-3 are gone from the list whatever their buckets still say.
        QueueEdge[] rows =
        [
            Row(QueueBucket.NowPlaying, 1), User(2), Row(QueueBucket.NextUp, 3), Row(QueueBucket.NextUp, 4),
            User(5), Row(QueueBucket.NextUp, 6), Row(QueueBucket.NextUp, 7, QueueProvider.Autoplay), Row(QueueBucket.NextUp, 8, QueueProvider.Autoplay),
        ];
        Span<int> index = stackalloc int[rows.Length];
        int n = Queue.Split(rows, 3, index, out int user, out int next, out int autoplay);
        Assert.Equal((4, 1, 1, 2), (n, user, next, autoplay));
        Assert.Equal(4, index[0]);
        Assert.Equal(5, index[1]);
        Assert.Equal(6, index[2]);
        Assert.Equal(7, index[3]);

        Assert.Equal(1, Queue.PositionInSection(rows, 3, 7, out int autoCount));
        Assert.Equal(2, autoCount);
        Assert.Equal(-1, Queue.PositionInSection(rows, 3, 1, out _));           // behind the deck: not upcoming
    }

    [Fact]
    public void Without_a_divider_only_the_upcoming_buckets_count()
    {
        QueueEdge[] rows = [User(1), Row(QueueBucket.NextUp, 2)];
        Span<int> index = stackalloc int[2];
        Queue.Split(rows, -1, index, out int user, out int next, out int autoplay);
        Assert.Equal((1, 1, 0), (user, next, autoplay));
        Assert.True(Queue.IsUpcoming(rows, -1, 0));
        Assert.False(Queue.IsUpcoming(rows, 1, 0));
    }

    [Fact]
    public void A_repeat_context_wrap_lands_on_the_contexts_head_not_a_consumed_queue_row()
    {
        // Followed at the end of the run: everything played is history, and NextIndex(-1) would answer the deck itself.
        QueueEdge[] rows = [Row(QueueBucket.History, 1, QueueProvider.Queue), Row(QueueBucket.History, 2), Row(QueueBucket.History, 3), Row(QueueBucket.NowPlaying, 4)];
        Assert.Equal(3, Queue.NextIndex(rows, -1));
        Assert.Equal(1, Queue.WrapIndex(rows));
    }
}
