// ── Wavee.Tests/QueueSessionTests.cs — the queue verbs against the LIVE queue ─────────────────────────────────────────
//
// The session writes in `Entities/Queue.Rules.cs` that the rail panel, the stage pane, a drop and the menu verbs call:
// each follows the cursor first, lands one structural write, and never moves the row the deck is on. They need a live
// `Entities.Current`, so the class joins the entities collection and boots a fake scope (offline).

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class QueueSessionTests
{
    static QueueEdge Row(QueueBucket bucket, ulong id, QueueProvider provider = QueueProvider.Context)
        => new(id, (byte)provider, (byte)bucket);

    static EntityRef T(int slot) => new(EntityKind.Track, slot);

    /// <summary>A context of <paramref name="count"/> rows (slots 1..count, ids 1..count), the deck on row 0.</summary>
    static void Context(int count)
    {
        TestScope.Fresh();
        var refs = new EntityRef[count];
        var rows = new QueueEdge[count];
        for (int i = 0; i < count; i++)
        {
            refs[i] = T(i + 1);
            rows[i] = Row(i == 0 ? QueueBucket.NowPlaying : QueueBucket.NextUp, (ulong)(i + 1));
        }
        Queue.Replace(refs, rows);
    }

    static string Slots()
    {
        var parts = new string[Queue.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = Queue.RefAt(i).Slot.ToString();
        return string.Join(",", parts);
    }

    [Fact]
    public void Play_next_goes_to_the_head_of_the_queue_and_add_to_queue_after_it_both_after_the_deck()
    {
        Context(5);
        var cursor = Queue.CursorOf(0);
        Assert.True(Queue.TryAdvance(ref cursor, out _));                        // the deck moved to row 1

        Assert.Equal(1, Queue.AddToQueue([T(20)], cursor));
        Assert.Equal(2, Queue.AddToQueue([T(21), T(22)], cursor));
        Assert.Equal(1, Queue.PlayNext([T(30)], cursor));

        Assert.Equal("1,2,30,20,21,22,3,4,5", Slots());
        Assert.Equal(T(2), Queue.RefAt(cursor.Index));                           // the deck never moved
        Assert.True(Queue.IsOrdered(Queue.Rows));
        Assert.Equal((byte)QueueProvider.Queue, Queue.Rows[2].Provider);
    }

    [Fact]
    public void The_user_queue_insert_honours_the_drop_slot_and_skips_unrepresentable_refs()
    {
        Context(3);
        var cursor = Queue.CursorOf(0);
        Queue.AddToQueue([T(10), T(11)], cursor);
        Assert.Equal(2, Queue.InsertUser([T(40), default, T(41)], cursor, userIndex: 1));
        Assert.Equal("1,10,40,41,11,2,3", Slots());
    }

    [Fact]
    public void Remove_takes_out_an_upcoming_row_and_refuses_the_deck_and_history()
    {
        Context(4);
        var cursor = Queue.CursorOf(1);                                         // the reducer is on row 1
        Assert.False(Queue.RemoveUpcoming(0, cursor));                          // behind the deck
        Assert.False(Queue.RemoveUpcoming(1, cursor));                          // the deck itself
        Assert.True(Queue.RemoveItem(3, cursor));                               // by item id
        Assert.Equal("1,2,4", Slots());
        Assert.False(Queue.RemoveItem(99, cursor));
    }

    [Fact]
    public void Clear_drops_only_the_rows_still_waiting_in_the_user_queue()
    {
        Context(3);
        var cursor = Queue.CursorOf(0);
        Queue.AddToQueue([T(10), T(11)], cursor);
        Assert.Equal(2, Queue.ClearUserQueue(cursor));
        Assert.Equal("1,2,3", Slots());
        Assert.Equal(0, Queue.ClearUserQueue(cursor));
    }

    [Fact]
    public void A_section_move_is_remove_then_insert_inside_that_section_only()
    {
        Context(4);                                                             // deck 1, next up 2,3,4
        var cursor = Queue.CursorOf(0);
        Queue.AddToQueue([T(10), T(11)], cursor);
        Assert.True(Queue.MoveInSection(QueueSection.NextUp, 0, 2, cursor));
        Assert.Equal("1,10,11,3,4,2", Slots());
        Assert.True(Queue.MoveInSection(QueueSection.Queue, 1, 0, cursor));
        Assert.Equal("1,11,10,3,4,2", Slots());
        Assert.False(Queue.MoveInSection(QueueSection.Autoplay, 0, 1, cursor)); // an empty section moves nothing
    }

    [Fact]
    public void A_skip_to_a_next_up_row_keeps_the_queue_and_answers_the_cursor_to_play()
    {
        Context(5);
        var current = Queue.CursorOf(0);
        Queue.AddToQueue([T(10)], current);                                     // 1 · q10 · 2 3 4 5
        int target = Queue.IndexOfItem(4);                                      // slot 4, in next up
        Assert.True(Queue.SkipTo(target, current, out var cursor));
        Assert.Equal(T(4), Queue.RefAt(cursor));
        Assert.True(Queue.TryPeek(in cursor, out EntityRef next));
        Assert.Equal(T(10), next);                                              // the queued row still plays first
        Assert.Equal((byte)QueueBucket.History, Queue.Rows[0].Bucket);
        Assert.False(Queue.SkipTo(0, cursor, out _));                           // history is not a skip target
    }

    [Fact]
    public void Follow_rewrites_only_when_the_buckets_are_behind_the_deck()
    {
        Context(3);
        var cursor = Queue.CursorOf(0);
        uint before = Queue.Version;
        Assert.False(Queue.Follow(cursor));                                     // already followed: no write
        Assert.Equal(before, Queue.Version);

        Assert.True(Queue.TryAdvance(ref cursor, out _));
        Assert.True(Queue.Follow(cursor));
        Assert.NotEqual(before, Queue.Version);
        Assert.Equal((byte)QueueBucket.History, Queue.Rows[0].Bucket);
        Assert.Equal((byte)QueueBucket.NowPlaying, Queue.Rows[1].Bucket);
        Assert.False(Queue.Follow(QueueCursor.None));
    }

    [Fact]
    public void Minted_item_ids_are_never_zero_and_never_repeat()
    {
        ulong a = Queue.MintItemIds(3), b = Queue.MintItemIds(1);
        Assert.NotEqual(0UL, a);
        Assert.True(b >= a + 3);
    }
}
