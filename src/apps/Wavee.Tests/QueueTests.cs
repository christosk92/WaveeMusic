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

    // ── the playable walk (a dead row is never advanced onto) ───────────────────────────────────────────────────────

    static bool[] AllLive(int n)
    {
        var flags = new bool[n];
        Array.Fill(flags, true);
        return flags;
    }

    [Fact]
    public void The_playable_walk_is_the_plain_walk_when_every_row_is_live()
    {
        var rows = Session();
        var live = new Queue.PlayableFlags(AllLive(rows.Length));
        Assert.Equal(Queue.NextIndex(rows, 2), Queue.NextPlayable(rows, live, 2, forward: true, wrap: false));
        Assert.Equal(Queue.PrevIndex(rows, 2), Queue.NextPlayable(rows, live, 2, forward: false, wrap: false));
        Assert.Equal(Queue.NextIndex(rows, -1), Queue.NextPlayable(rows, live, -1, forward: true, wrap: false));
    }

    [Fact]
    public void The_playable_walk_steps_past_dead_rows_in_both_directions()
    {
        var rows = Session();
        var flags = AllLive(rows.Length);
        flags[3] = false;                                                // both user-queued rows are dead…
        flags[4] = false;
        flags[1] = false;                                                // …and so is the most recent history row
        var f = new Queue.PlayableFlags(flags);

        Assert.Equal(5, Queue.NextPlayable(rows, f, 2, forward: true, wrap: false));
        Assert.Equal(0, Queue.NextPlayable(rows, f, 2, forward: false, wrap: false));
    }

    [Fact]
    public void A_run_of_dead_rows_to_the_end_answers_none_and_the_walk_is_bounded()
    {
        var rows = Session();
        var flags = AllLive(rows.Length);
        for (int i = 3; i < rows.Length; i++) flags[i] = false;
        Assert.Equal(-1, Queue.NextPlayable(rows, new Queue.PlayableFlags(flags), 2, forward: true, wrap: false));

        // Every row dead: no answer in either direction, wrap or not — and it returns, which is the bound.
        var dead = new Queue.PlayableFlags(new bool[rows.Length]);
        Assert.Equal(-1, Queue.NextPlayable(rows, dead, 2, forward: true, wrap: true));
        Assert.Equal(-1, Queue.NextPlayable(rows, dead, 2, forward: true, wrap: false));
        Assert.Equal(-1, Queue.NextPlayable(rows, dead, 2, forward: false, wrap: false));
        Assert.Equal(-1, Queue.NextPlayable(default, dead, -1, forward: true, wrap: true));   // an empty list
    }

    [Fact]
    public void The_wrap_lands_on_the_first_live_context_row_and_may_replay_the_deck()
    {
        var rows = Session();
        var flags = AllLive(rows.Length);
        for (int i = 3; i < rows.Length; i++) flags[i] = false;

        // Off the end under repeat-context: the first row the CONTEXT provided (history row 0, `WrapIndex`).
        Assert.Equal(0, Queue.NextPlayable(rows, new Queue.PlayableFlags(flags), 2, forward: true, wrap: true));

        // With the history dead too the only live row is the one on the deck: it replays rather than ending.
        flags[0] = false;
        flags[1] = false;
        Assert.Equal(2, Queue.NextPlayable(rows, new Queue.PlayableFlags(flags), 2, forward: true, wrap: true));
    }

    [Fact]
    public void Flags_past_the_array_are_not_playable()
    {
        var f = new Queue.PlayableFlags([true]);
        Assert.True(f.IsPlayable(0));
        Assert.False(f.IsPlayable(1));
        Assert.False(f.IsPlayable(-1));
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

        Queue.Enqueue(episode, Queue.CursorOf(0), itemId: 42);

        Assert.Equal(2, Queue.Count);
        Assert.Equal(episode, Queue.RefAt(1));
        Assert.Equal((byte)QueueBucket.UserQueue, Queue.Rows[1].Bucket);
        Assert.Equal((byte)QueueProvider.Queue, Queue.Rows[1].Provider);
        Assert.Equal(42UL, Queue.Rows[1].ItemId);
        // A local queue write is authoritative: nothing would ever settle a pending bit, so none is set.
        Assert.Equal((byte)EdgePending.None, Queue.Pending[1]);

        // A ref the queue cannot represent is dropped, not aliased onto row 0.
        Queue.Enqueue(default, Queue.CursorOf(0));
        Assert.Equal(2, Queue.Count);
    }

    // ── enqueue against the deck (B3's finding: the bucket rule lands behind an advanced cursor) ────────────────────

    [Fact]
    public void After_an_advance_the_enqueue_index_follows_the_cursor_not_the_original_now_playing_row()
    {
        // The reducer moved the cursor to row 2 without re-bucketing: row 0 still SAYS NowPlaying. The bucket rule answers
        // 1 — behind the cursor, where the row would never play and would shift the deck's own index.
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2), Row(QueueBucket.NextUp, 3), Row(QueueBucket.NextUp, 4)];
        Assert.Equal(1, Queue.EnqueueIndex(rows));
        Assert.Equal(3, Queue.EnqueueIndex(rows, cursorIndex: 2));

        // Queued rows still waiting right after the cursor stay ahead of the new one; no deck falls back to the buckets.
        QueueEdge[] queued = [Row(QueueBucket.History, 1), Row(QueueBucket.NowPlaying, 2), Row(QueueBucket.UserQueue, 3, QueueProvider.Queue), Row(QueueBucket.NextUp, 4)];
        Assert.Equal(3, Queue.EnqueueIndex(queued, cursorIndex: 1));
        Assert.Equal(queued.Length, Queue.EnqueueIndex(queued, cursorIndex: 3));
        Assert.Equal(3, Queue.EnqueueIndex(queued, cursorIndex: -1));
    }

    [Fact]
    public void Enqueue_after_an_advance_lands_after_the_deck_and_never_shifts_it()
    {
        TestScope.Fresh();
        EntityRef[] refs = [new(EntityKind.Track, 1), new(EntityKind.Track, 2), new(EntityKind.Track, 3), new(EntityKind.Track, 4)];
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2), Row(QueueBucket.NextUp, 3), Row(QueueBucket.NextUp, 4)];
        Queue.Replace(refs, rows);
        var cursor = Queue.CursorOf(0);
        Assert.True(Queue.TryAdvance(ref cursor, out _));
        Assert.True(Queue.TryAdvance(ref cursor, out _));                       // on row 2, buckets untouched

        EntityRef added = new(EntityKind.Track, 9);
        Queue.Enqueue(added, cursor);

        Assert.Equal(5, Queue.Count);
        Assert.Equal(refs[2], Queue.RefAt(cursor.Index));                        // the deck's row did not move
        Assert.Equal(added, Queue.RefAt(3));
        Assert.True(Queue.TryPeek(in cursor, out EntityRef next));
        Assert.Equal(added, next);                                              // …and it is what plays next
        Assert.True(Queue.IsOrdered(Queue.Rows));                                // the list was followed first
        Assert.Equal((byte)QueueBucket.NowPlaying, Queue.Rows[2].Bucket);
    }

    [Fact]
    public void The_same_recording_queued_twice_is_two_rows()
    {
        TestScope.Fresh();
        EntityRef track = new(EntityKind.Track, 5);
        EntityRef[] refs = [track];
        QueueEdge[] rows = [Row(QueueBucket.NowPlaying, 1)];
        Queue.Replace(refs, rows);

        Queue.Enqueue(track, Queue.CursorOf(0));
        Queue.Enqueue(track, Queue.CursorOf(0));

        Assert.Equal(3, Queue.Count);
        Assert.Equal((byte)QueueBucket.NowPlaying, Queue.Rows[0].Bucket);        // the deck's row was not re-bucketed in place
        Assert.NotEqual(0UL, Queue.Rows[1].ItemId);
        Assert.NotEqual(Queue.Rows[1].ItemId, Queue.Rows[2].ItemId);
    }

    // ── the --fake seed's default queue (ch 31 §3.1) ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_fake_seed_lands_the_default_queue_one_plus_three_plus_eight()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(1_788_000_000);

        var rows = Queue.Rows;
        Assert.Equal(Entities.QueueSeedRows, rows.Length);
        Assert.True(Queue.IsOrdered(rows));
        Assert.Equal(EdgeState.Complete, Queue.State);
        Assert.True(Queue.Range(QueueBucket.NowPlaying, out _, out int nowPlaying));
        Assert.Equal(1, nowPlaying);

        Span<int> index = stackalloc int[rows.Length];
        Queue.Split(rows, Queue.Divider(rows, -1), index, out int user, out int next, out int autoplay);
        Assert.Equal((3, 5, 3), (user, next, autoplay));
        for (int i = 0; i < rows.Length; i++) Assert.Equal(EntityKind.Track, Queue.RefAt(i).Kind);
        ulong first = rows[0].ItemId;

        // Deterministic ids (ch 31 §7.2 rule A): a second seed stamps the same ones.
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(1_788_000_000);
        Assert.Equal(Entities.QueueSeedFirstItemId, first);
        Assert.Equal(first, Queue.Rows[0].ItemId);
    }
}
