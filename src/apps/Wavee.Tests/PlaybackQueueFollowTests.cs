// ── Wavee.Tests/PlaybackQueueFollowTests.cs — the queue under the deck (gap batch B3c) ────────────────────────────────
//
// What the playback host does with the live queue between reducer drains, as the reducer and `Queue` see it: the follow
// that puts the buckets back under the cursor (and why the next-row arm must run after it), the repeat-context wrap row,
// the gate that keeps a foreign list untouched, the uid book every queue row's server uid is kept in, and the text-form
// context a station plays under. The queue is the live one, so every fact boots a fake scope and joins the entities
// collection (the uid book mints through `Queue.MintItemIds`, whose counter the queue suite also reads).

using Wavee;
using Xunit;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaybackQueueFollowTests
{
    static readonly ulong Phone = Playback.DeviceHash("phone");

    static QueueEdge Row(QueueBucket bucket, ulong id, QueueProvider provider = QueueProvider.Context)
        => new(id, (byte)provider, (byte)bucket);

    static EntityRef Track(int n)
    {
        var id = EntityId.ForGid(EntityKind.Track, (UInt128)(ulong)(0xB300 + n));
        return new EntityRef(EntityKind.Track, Entities.Current.Tracks.Slot(id));
    }

    /// <summary>The live queue <paramref name="refs"/>/<paramref name="rows"/>, the deck playing row
    /// <paramref name="cursor"/>.</summary>
    static Playback.State DeckAt(int cursor, EntityRef[] refs, QueueEdge[] rows, RepeatMode repeat = RepeatMode.Off)
    {
        Queue.Replace(refs, rows);
        var s = Playback.State.Initial;
        s.Us = Playback.DeviceHash("wavee-device");
        s.Current = refs[cursor];
        s.CurrentId = refs[cursor].Id;
        s.Cursor = Queue.CursorOf(cursor);
        s.Phase = Playback.Phase.Playing;
        s.DurationMs = 180_000;
        s.LoadEpoch = s.Epoch;
        s.Repeat = repeat;
        Playback.Ownership.Claim(ref s.Own, Playback.ClaimCause.UserPlay, 1, 0, 0, acknowledged: false);
        return s;
    }

    // ── the repeat-context wrap (Queue.WrapIndex) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Repeat_context_wraps_to_the_first_context_row_and_not_to_the_deck_of_a_followed_queue()
    {
        // Followed: every row before the deck is history. The old wrap — the first non-history row — was the deck itself.
        TestScope.Fresh();
        EntityRef[] refs = [Track(0), Track(1), Track(2)];
        var s = DeckAt(2, refs, [Row(QueueBucket.History, 1), Row(QueueBucket.History, 2), Row(QueueBucket.NowPlaying, 3)],
            RepeatMode.Context);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Next(), ref fx);

        Assert.True(fx.Load);
        Assert.Equal(refs[0], fx.LoadRow);
        Assert.Equal(0, s.Cursor.Index);
    }

    [Fact]
    public void A_wrap_passes_over_a_queued_row_that_was_already_played()
    {
        TestScope.Fresh();
        EntityRef[] refs = [Track(0), Track(1), Track(2)];
        var s = DeckAt(2, refs,
            [Row(QueueBucket.History, 1, QueueProvider.Queue), Row(QueueBucket.History, 2), Row(QueueBucket.NowPlaying, 3)],
            RepeatMode.Context);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Next(), ref fx);

        Assert.Equal(refs[1], fx.LoadRow);
        Assert.Equal(1, s.Cursor.Index);
    }

    [Fact]
    public void The_endgame_under_repeat_context_prepares_the_wrap_row()
    {
        TestScope.Fresh();
        EntityRef[] refs = [Track(0), Track(1), Track(2)];
        var s = DeckAt(2, refs, [Row(QueueBucket.History, 1), Row(QueueBucket.History, 2), Row(QueueBucket.NowPlaying, 3)],
            RepeatMode.Context);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);

        Assert.True(fx.PrepareNext);
        Assert.Equal(refs[0], fx.NextRow);
        Assert.False(fx.Autoplay);                                        // a repeating context never runs out
    }

    // ── the follow after a drain ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_previous_clicks_in_one_drain_arm_the_history_row_once_the_queue_is_followed()
    {
        // The host's order: the ring's Steps, then Queue.Follow, then the watch's QueueChanged. Before the follow the
        // stale buckets make the walk skip the history row the deck now sits in front of.
        TestScope.Fresh();
        EntityRef[] refs = [Track(0), Track(1), Track(2), Track(3)];
        var s = DeckAt(2, refs,
            [Row(QueueBucket.History, 1), Row(QueueBucket.History, 2), Row(QueueBucket.NowPlaying, 3), Row(QueueBucket.NextUp, 4)]);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Prev(), ref fx);
        Playback.Step(ref s, Playback.Input.Prev(), ref fx);
        Assert.Equal(0, s.Cursor.Index);
        Assert.Equal(refs[2], fx.PrefetchRow);                             // stale: row 1 still reads as history

        Assert.True(Playback.FollowsQueue(in s, Queue.RefAt(s.Cursor.Index)));
        Assert.True(Queue.Follow(s.Cursor));
        Playback.Step(ref s, Playback.Input.QueueChanged(), ref fx);

        Assert.True(fx.Prefetch);
        Assert.Equal(refs[1], fx.PrefetchRow);
        Assert.Equal(refs[1].Id, s.NextId);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        Assert.Equal(refs[1], fx.LoadRow);
        Assert.Equal(1, s.Cursor.Index);
    }

    [Fact]
    public void After_an_advance_the_follow_moves_the_buckets_and_a_second_follow_writes_nothing()
    {
        TestScope.Fresh();
        EntityRef[] refs = [Track(0), Track(1), Track(2)];
        var s = DeckAt(0, refs, [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2), Row(QueueBucket.NextUp, 3)]);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);

        Assert.True(Queue.Follow(s.Cursor));
        uint version = Queue.Version;
        Assert.Equal((byte)QueueBucket.History, Queue.Rows[0].Bucket);
        Assert.Equal((byte)QueueBucket.NowPlaying, Queue.Rows[1].Bucket);
        Assert.True(Queue.UpNext(out int start, out int length));
        Assert.Equal(2, start);                                            // "Next up" is the row after the new deck
        Assert.Equal(1, length);

        Assert.False(Queue.Follow(s.Cursor));
        Assert.Equal(version, Queue.Version);
    }

    [Fact]
    public void The_queue_follows_only_a_local_deck_whose_row_is_at_the_cursor()
    {
        TestScope.Fresh();
        EntityRef[] refs = [Track(0), Track(1)];
        var s = DeckAt(0, refs, [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2)]);

        Assert.True(Playback.FollowsQueue(in s, Queue.RefAt(0)));
        Assert.False(Playback.FollowsQueue(in s, Queue.RefAt(1)));          // the queue was replaced under the deck

        var none = s;
        none.Cursor = QueueCursor.None;
        Assert.False(Playback.FollowsQueue(in none, Queue.RefAt(0)));

        var fx = new Playback.Effects();
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, Phone, 1_000);
        var remote = default(Playback.RemoteState);
        Playback.Step(ref s, Playback.Input.Cluster(in frame, in remote), ref fx);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        Assert.False(Playback.FollowsQueue(in s, Queue.RefAt(0)));          // a foreign list is never re-bucketed
    }

    // ── the uid book (G-075) ────────────────────────────────────────────────────────────────────────────────────────

    static string Formatted(Playback.UidBook book, ulong itemId)
    {
        Span<char> text = stackalloc char[Playback.UidBook.MaxChars];
        return new string(text[..book.Format(itemId, text)]);
    }

    [Fact]
    public void Every_captured_uid_shape_round_trips_through_its_item_id()
    {
        var book = new Playback.UidBook();

        ulong autoplay = book.ItemIdOf("0ab15c9f39e1de3b"u8);                  // 16 hex: packs, nothing booked
        Assert.Equal(Playback.QueueUid.ItemIdOf("0ab15c9f39e1de3b"u8), autoplay);
        Assert.Equal(0, book.Count);
        Assert.Equal("0ab15c9f39e1de3b", Formatted(book, autoplay));

        ulong context = book.ItemIdOf("5bd5aabfe4434940c96f"u8);               // 20 hex: 80 bits, booked
        ulong queued = book.ItemIdOf("q2"u8);                                  // a user-queue row's short uid
        Assert.NotEqual(0UL, context);
        Assert.NotEqual(0UL, queued);
        Assert.NotEqual(context, queued);
        Assert.Equal("5bd5aabfe4434940c96f", Formatted(book, context));
        Assert.Equal("q2", Formatted(book, queued));

        Assert.Equal(context, book.ItemIdOf("5bd5aabfe4434940c96f"u8));        // a re-sent queue keeps its identities
        Assert.Equal(2, book.Count);
    }

    [Fact]
    public void No_uid_and_an_oversized_one_store_nothing()
    {
        var book = new Playback.UidBook();
        Assert.Equal(0UL, book.ItemIdOf(default));
        Assert.Equal(0UL, book.ItemIdOf(new byte[Playback.UidBook.MaxChars + 1]));
        Assert.Equal("", Formatted(book, 0));
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void Retain_forgets_the_uids_no_live_row_carries()
    {
        var book = new Playback.UidBook();
        ulong kept = book.ItemIdOf("q2"u8);
        ulong gone = book.ItemIdOf("q3"u8);

        book.Retain([Row(QueueBucket.UserQueue, kept, QueueProvider.Queue)]);

        Assert.Equal(1, book.Count);
        Assert.Equal("q2", Formatted(book, kept));
        Assert.NotEqual("q3", Formatted(book, gone));
        Assert.NotEqual(gone, book.ItemIdOf("q3"u8));                          // booked afresh, never the stale id
    }

    // ── the station context (radio through the one context path) ───────────────────────────────────────────────────

    [Fact]
    public void A_station_uri_is_a_text_form_spotify_context_that_autoplay_never_continues()
    {
        TestScope.Fresh();
        const string station = "spotify:station:track:7idegBIikag5rTZP4WZihP";

        EntityId id = EntityId.Parse(station.AsSpan());

        Assert.False(id.IsEmpty);                                             // State.Context can carry it
        Assert.False(id.IsPlayable);                                          // resolved, never played as a row
        Assert.Equal(EntityProvider.Spotify, id.Provider);
        Assert.Equal(station, id.Text);                                       // and the PUT body writes it back
        Assert.False(Playback.RemotePlan.AutoplayContinues(System.Text.Encoding.UTF8.GetBytes(station)));
    }
}
