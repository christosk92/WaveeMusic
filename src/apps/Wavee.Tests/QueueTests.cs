// ── Wavee.Tests/QueueTests.cs — bucket ranges, the cursor walk, the enqueue position ─────────────────────────────
//
// Wave 1's gate for Entities/Queue.cs (plan §5). Most rules under test are pure functions over a
// `ReadOnlySpan<QueueEdge>`, which is why they need no session, no scope and no engine loop (D17) — and why the
// reducer can reason about a queue it has been handed rather than only about the live one.
//
// The CROSS-KIND facts at the bottom are the exception, and they are the ones added on 2026-09-12 with the packed
// identity (docs/plans/wavee/wavee-0.3-entity-identity-memory.md §3.1 requirement 4): a queue mixes TRACKS and
// EPISODES, a CSR target is a bare `int`, and track slot 5 and episode slot 5 are the SAME int — so 0.2.9 made an
// episode ride as a Podcast-flagged track row and 0.3's edge table would have confused the two for one. `Queue.Pack`
// puts the kind in the target's top 8 bits, which makes the whole `EdgeTable` key on the full identity for free. Those
// facts need a live `Entities.Current`, so the class joins the entities collection and boots a fake scope
// (`Store.Boot` is a no-op until a path is set, so this stays offline).

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class QueueTests
{
    static QueueEdge Row(QueueBucket bucket, ulong id = 0, QueueProvider provider = QueueProvider.Context)
        => new(id, (byte)provider, (byte)bucket);

    /// <summary>A realistic session: two played rows, the one on the deck, two user-queued, three from the context.</summary>
    static QueueEdge[] Session() =>
    [
        Row(QueueBucket.History, 1),
        Row(QueueBucket.History, 2),
        Row(QueueBucket.NowPlaying, 3),
        Row(QueueBucket.UserQueue, 4, QueueProvider.Queue),
        Row(QueueBucket.UserQueue, 5, QueueProvider.Queue),
        Row(QueueBucket.NextUp, 6),
        Row(QueueBucket.NextUp, 7),
        Row(QueueBucket.NextUp, 8, QueueProvider.Autoplay),
    ];

    // ── the storage order ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reading_order_is_history_then_now_playing_then_user_queue_then_next_up()
    {
        Assert.True(Queue.Rank(QueueBucket.History) < Queue.Rank(QueueBucket.NowPlaying));
        Assert.True(Queue.Rank(QueueBucket.NowPlaying) < Queue.Rank(QueueBucket.UserQueue));
        Assert.True(Queue.Rank(QueueBucket.UserQueue) < Queue.Rank(QueueBucket.NextUp));
        Assert.True(Queue.IsOrdered(Session()));
    }

    [Fact]
    public void Wire_order_is_not_reading_order_and_the_invariant_catches_it()
    {
        // The enum's declaration order (NowPlaying first) is 0.2.9's and is NOT the storage order; a decoder that
        // emitted rows in it would break every range and the cursor walk.
        QueueEdge[] wireOrder = [Row(QueueBucket.NowPlaying), Row(QueueBucket.UserQueue), Row(QueueBucket.History)];
        Assert.False(Queue.IsOrdered(wireOrder));
    }

    // ── bucket ranges ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Each_bucket_is_a_contiguous_slice()
    {
        var rows = Session();
        Assert.True(Queue.Range(rows, QueueBucket.History, out int start, out int length));
        Assert.Equal(0, start);
        Assert.Equal(2, length);

        Assert.True(Queue.Range(rows, QueueBucket.NowPlaying, out start, out length));
        Assert.Equal(2, start);
        Assert.Equal(1, length);

        Assert.True(Queue.Range(rows, QueueBucket.UserQueue, out start, out length));
        Assert.Equal(3, start);
        Assert.Equal(2, length);

        Assert.True(Queue.Range(rows, QueueBucket.NextUp, out start, out length));
        Assert.Equal(5, start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void A_bucket_with_no_rows_contributes_nothing_header_included()
    {
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying), Row(QueueBucket.NextUp)];
        Assert.False(Queue.Range(rows, QueueBucket.UserQueue, out int start, out int length));
        Assert.Equal(0, length);
        Assert.Equal(0, start);
    }

    [Fact]
    public void Up_next_is_the_user_queue_and_the_continuation_as_one_slice()
    {
        Assert.True(Queue.UpNext(Session(), out int start, out int length));
        Assert.Equal(3, start);
        Assert.Equal(5, length);
    }

    [Fact]
    public void Up_next_still_answers_when_only_one_of_the_two_buckets_has_rows()
    {
        QueueEdge[] onlyContext = [Row(QueueBucket.NowPlaying), Row(QueueBucket.NextUp), Row(QueueBucket.NextUp)];
        Assert.True(Queue.UpNext(onlyContext, out int start, out int length));
        Assert.Equal(1, start);
        Assert.Equal(2, length);

        QueueEdge[] nothing = [Row(QueueBucket.History), Row(QueueBucket.NowPlaying)];
        Assert.False(Queue.UpNext(nothing, out _, out int none));
        Assert.Equal(0, none);
    }

    // ── the cursor walk (§4.7) ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Advancing_never_re_enters_history()
    {
        var rows = Session();
        Assert.Equal(2, Queue.NextIndex(rows, -1));        // from nowhere: the now-playing row, not history
        Assert.Equal(3, Queue.NextIndex(rows, 2));
        Assert.Equal(4, Queue.NextIndex(rows, 3));
    }

    [Fact]
    public void The_end_of_the_queue_is_a_refusal_and_not_a_wrap()
    {
        var rows = Session();
        Assert.Equal(-1, Queue.NextIndex(rows, rows.Length - 1));
        Assert.Equal(-1, Queue.NextIndex([], -1));
    }

    [Fact]
    public void Ten_advances_over_a_twelve_row_queue_land_on_the_last_row()
    {
        // Plan §4.15's shape: ten Next clicks are ten cursor moves and one Load effect. Here: ten moves.
        QueueEdge[] rows = new QueueEdge[12];
        rows[0] = Row(QueueBucket.NowPlaying);
        for (int i = 1; i < rows.Length; i++) rows[i] = Row(QueueBucket.NextUp);

        int index = 0;
        for (int i = 0; i < 10; i++) index = Queue.NextIndex(rows, index);
        Assert.Equal(10, index);
    }

    [Fact]
    public void Previous_walks_back_into_history_because_that_is_what_history_is_for()
    {
        var rows = Session();
        Assert.Equal(1, Queue.PrevIndex(rows, 2));
        Assert.Equal(0, Queue.PrevIndex(rows, 1));
        Assert.Equal(-1, Queue.PrevIndex(rows, 0));
        Assert.Equal(rows.Length - 1, Queue.PrevIndex(rows, -1));      // from nowhere: the last row
    }

    [Fact]
    public void The_default_cursor_is_the_head_and_none_is_a_different_state()
    {
        Assert.False(default(QueueCursor).IsNone);
        Assert.Equal(0, default(QueueCursor).Index);
        Assert.True(QueueCursor.None.IsNone);
    }

    // ── identity and insertion ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_row_is_found_by_the_servers_item_id_and_never_by_its_index()
    {
        var rows = Session();
        Assert.Equal(5, Queue.IndexOfItem(rows, 6));
        Assert.Equal(-1, Queue.IndexOfItem(rows, 99));
        Assert.Equal(-1, Queue.IndexOfItem(rows, 0));                  // 0 is the "no id" sentinel, not row zero
    }

    [Fact]
    public void Add_to_queue_appends_after_the_last_user_queued_row()
    {
        // Queueing three tracks must play them in the order they were clicked, which prepending would reverse.
        Assert.Equal(5, Queue.EnqueueIndex(Session()));
    }

    [Fact]
    public void With_no_user_queue_it_lands_straight_after_the_row_that_is_playing()
    {
        QueueEdge[] rows = [Row(QueueBucket.History), Row(QueueBucket.NowPlaying), Row(QueueBucket.NextUp)];
        Assert.Equal(2, Queue.EnqueueIndex(rows));
    }

    [Fact]
    public void With_nothing_playing_it_lands_at_the_end()
    {
        QueueEdge[] rows = [Row(QueueBucket.History), Row(QueueBucket.History)];
        Assert.Equal(2, Queue.EnqueueIndex(rows));
        Assert.Equal(0, Queue.EnqueueIndex([]));
    }

    // ── the cross-kind target (defect 5) ─────────────────────────────────────────────────────────

    [Fact]
    public void A_row_pointer_round_trips_through_the_packed_target()
    {
        foreach (EntityKind kind in new[] { EntityKind.Track, EntityKind.Episode, EntityKind.Album, EntityKind.Concert })
            foreach (int slot in new[] { 1, 2, 4095, 4096, Queue.MaxSlot })
            {
                var row = new EntityRef(kind, slot);
                int packed = Queue.Pack(row);
                Assert.True(packed > 0);
                Assert.Equal(row, Queue.Unpack(packed));
            }
    }

    [Fact]
    public void The_same_slot_number_in_two_tables_is_two_different_targets()
    {
        // THE defect, in one assertion: an episode and a track can genuinely both be row 5 of their own table, and a
        // bare slot cannot tell them apart — so `Contains`, `IndexOf`, `Insert` and `Settle`, all keyed on the target,
        // would have treated them as one row.
        Assert.NotEqual(Queue.Pack(new EntityRef(EntityKind.Track, 5)),
                        Queue.Pack(new EntityRef(EntityKind.Episode, 5)));
    }

    [Fact]
    public void Pack_refuses_what_it_cannot_represent_rather_than_aliasing()
    {
        // 0 is "none" in every table and arena (P3), and it is what a cleared CSR hole already reads as — so refusing
        // by answering 0 costs nothing, where truncating a slot would silently point at another entity.
        Assert.Equal(0, Queue.Pack(default));                                    // kindless
        Assert.Equal(0, Queue.Pack(new EntityRef(EntityKind.Track, 0)));         // slot 0 is "none"
        Assert.Equal(0, Queue.Pack(new EntityRef(EntityKind.Track, Queue.MaxSlot + 1)));
        Assert.True(Queue.Unpack(0).IsNone);
        Assert.True(Queue.Unpack(-1).IsNone);
    }

    [Fact]
    public void Packing_a_batch_writes_one_target_per_row_and_allocates_nothing()
    {
        EntityRef[] refs = [new(EntityKind.Track, 5), new(EntityKind.Episode, 5)];
        Span<int> dst = stackalloc int[4];
        Assert.Equal(2, Queue.Pack(refs, dst));
        Assert.Equal(refs[0], Queue.Unpack(dst[0]));
        Assert.Equal(refs[1], Queue.Unpack(dst[1]));
        Assert.Equal(1, Queue.Pack(refs, dst[..1]));                             // the shorter of the two spans wins
    }

    // ── the live queue ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_live_queue_hands_back_rows_that_know_their_table()
    {
        TestScope.Fresh();
        EntityRef[] refs = [new(EntityKind.Track, 5), new(EntityKind.Episode, 5), new(EntityKind.Track, 9)];
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2), Row(QueueBucket.NextUp, 3)];
        Queue.Replace(refs, rows);

        Assert.Equal(3, Queue.Count);
        Assert.Equal(EdgeState.Complete, Queue.State);
        Assert.Equal(refs[0], Queue.RefAt(0));
        Assert.Equal(refs[1], Queue.RefAt(1));
        Assert.NotEqual(Queue.RefAt(0), Queue.RefAt(1));
        Assert.True(Queue.RefAt(99).IsNone);                                     // past the end is "nothing", not a throw

        // …and the cursor walk carries the kind with it, which is what the deck needs to know what to load.
        var cursor = Queue.CursorOf(0);
        Assert.True(Queue.TryAdvance(ref cursor, out EntityRef next));
        Assert.Equal(refs[1], next);
        Assert.Equal(1, cursor.Index);
        Assert.Equal(refs[1], Queue.Current(in cursor));
        Assert.Equal(5, Queue.SlotAt(in cursor));
    }

    [Fact]
    public void Settling_a_removal_takes_out_the_row_the_kind_names_and_not_its_twin()
    {
        // With a bare slot as the target this was the bug: settling the episode's removal would have found the TRACK
        // at index 0 first (same slot number) and dropped the wrong row.
        TestScope.Fresh();
        EntityRef track = new(EntityKind.Track, 5), episode = new(EntityKind.Episode, 5);
        EntityRef[] refs = [track, episode];
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2)];
        Queue.Replace(refs, rows);

        Assert.True(Queue.MarkRemoveAt(1));
        Assert.Equal((byte)EdgePending.Remove, Queue.Pending[1]);
        Assert.Equal((byte)EdgePending.None, Queue.Pending[0]);

        Assert.True(Queue.Settle(episode, ok: true));
        Assert.Equal(1, Queue.Count);
        Assert.Equal(track, Queue.RefAt(0));
    }

    [Fact]
    public void Enqueue_splices_a_row_of_any_kind_after_the_one_that_is_playing()
    {
        TestScope.Fresh();
        EntityRef track = new(EntityKind.Track, 5), episode = new(EntityKind.Episode, 5);
        EntityRef[] refs = [track];
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1)];
        Queue.Replace(refs, rows);

        Queue.Enqueue(episode, itemId: 42);

        Assert.Equal(2, Queue.Count);
        Assert.Equal(episode, Queue.RefAt(1));
        Assert.Equal((byte)QueueBucket.UserQueue, Queue.Rows[1].Bucket);
        Assert.Equal((byte)QueueProvider.Queue, Queue.Rows[1].Provider);
        Assert.Equal(42UL, Queue.Rows[1].ItemId);
        Assert.Equal((byte)EdgePending.Add, Queue.Pending[1]);

        // A ref the queue cannot represent is dropped, not aliased onto row 0.
        Queue.Enqueue(default);
        Assert.Equal(2, Queue.Count);
    }
}
