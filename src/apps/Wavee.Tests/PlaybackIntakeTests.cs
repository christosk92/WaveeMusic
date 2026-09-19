// ── Wavee.Tests/PlaybackIntakeTests.cs — the pure rules of the Connect intake and the queue layout (gap batch B3c) ──────
//
// No scope, no live queue: spans and values in, answers out. The arrival order between the Connect mailbox and the host's
// own intakes (IntakeQueue, HoldsMailbox), a controller's rows spliced behind the deck (SpliceQueue), the user queue a new
// context keeps and where it lands (KeptQueue, Layout), the station a radio seed plays (StationUri), and the video gid the
// PutState snapshot carries. Gap batch R4-1 adds the superseding load (G-244), update_context's reshuffle (G-245), the page
// route (G-242), the attribution window (G-246) and the snapshot's wire facts (G-240, G-036).

using Wavee;
using Xunit;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

public class PlaybackIntakeTests
{
    static QueueEdge Row(QueueBucket bucket, ulong id, QueueProvider provider = QueueProvider.Context)
        => new(id, (byte)provider, (byte)bucket);

    static byte[] Buckets(ReadOnlySpan<QueueEdge> rows)
    {
        var b = new byte[rows.Length];
        for (int i = 0; i < rows.Length; i++) b[i] = rows[i].Bucket;
        return b;
    }

    static byte B(QueueBucket bucket) => (byte)bucket;

    // ── arrival order: the mailbox and the host's intakes ───────────────────────────────────────────────────────────

    static Playback.Intake LoadIntake(ClusterBuffer buffer) => new() { Kind = Playback.IntakeKind.Load, Buffer = buffer };
    static Playback.Intake QueueIntake(ClusterBuffer buffer) => new() { Kind = Playback.IntakeKind.Queue, Buffer = buffer };

    [Fact]
    public void A_verb_that_arrived_after_a_play_waits_for_the_play()
    {
        // "play, then pause": the play arrived with nothing waiting in the mailbox, so it is due before the pause item.
        var released = new List<ClusterBuffer>();
        var intakes = new Playback.IntakeQueue(released.Add);
        ClusterBuffer buffer = ClusterBuffer.Rent();

        intakes.Arrive(LoadIntake(buffer), pending: 0);

        Assert.True(intakes.TryTake(mailboxEmpty: false, out Playback.Intake taken));
        Assert.Equal(Playback.IntakeKind.Load, taken.Kind);
        Assert.Same(buffer, taken.Buffer);
        Assert.Empty(released);
    }

    [Fact]
    public void A_verb_that_arrived_before_a_play_goes_first()
    {
        var intakes = new Playback.IntakeQueue(static _ => { });
        intakes.Arrive(LoadIntake(ClusterBuffer.Rent()), pending: 2);

        Assert.False(intakes.TryTake(mailboxEmpty: false, out _));
        intakes.Taken();
        Assert.False(intakes.TryTake(mailboxEmpty: false, out _));
        intakes.Taken();
        Assert.True(intakes.TryTake(mailboxEmpty: false, out _));
    }

    [Fact]
    public void An_empty_mailbox_releases_an_intake_whatever_it_still_counted()
    {
        // The mailbox dropped or cleared an item the drain never took: the intake must not wait for it forever.
        var intakes = new Playback.IntakeQueue(static _ => { });
        intakes.Arrive(QueueIntake(ClusterBuffer.Rent()), pending: 5);

        Assert.False(intakes.TryTake(mailboxEmpty: false, out _));
        Assert.True(intakes.TryTake(mailboxEmpty: true, out Playback.Intake taken));
        Assert.Equal(Playback.IntakeKind.Queue, taken.Kind);
        Assert.Equal(0, intakes.Count);
    }

    [Fact]
    public void Every_play_reaches_the_reducer_for_its_command_receipt_in_arrival_order()
    {
        var released = new List<ClusterBuffer>();
        var intakes = new Playback.IntakeQueue(released.Add);
        ClusterBuffer first = ClusterBuffer.Rent(), rows = ClusterBuffer.Rent(), second = ClusterBuffer.Rent();

        intakes.Arrive(LoadIntake(first), pending: 0);
        intakes.Arrive(QueueIntake(rows), pending: 0);
        intakes.Arrive(LoadIntake(second), pending: 1);                     // one mailbox item came between

        Assert.Empty(released);
        Assert.Equal(3, intakes.Count);
        Assert.True(intakes.TryTake(mailboxEmpty: false, out Playback.Intake initial));
        Assert.Same(first, initial.Buffer);
        Assert.True(intakes.TryTake(mailboxEmpty: false, out Playback.Intake a));
        Assert.Same(rows, a.Buffer);
        Assert.False(intakes.TryTake(mailboxEmpty: false, out _));          // the item between goes before the newer play
        intakes.Taken();
        Assert.True(intakes.TryTake(mailboxEmpty: false, out Playback.Intake b));
        Assert.Same(second, b.Buffer);
    }

    [Fact]
    public void The_intake_queue_is_bounded_and_lets_its_oldest_go()
    {
        var released = new List<ClusterBuffer>();
        var intakes = new Playback.IntakeQueue(released.Add);
        var buffers = new ClusterBuffer[Playback.IntakeQueue.Depth + 1];
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = ClusterBuffer.Rent();
            intakes.Arrive(QueueIntake(buffers[i]), pending: 0);
        }

        Assert.Equal(Playback.IntakeQueue.Depth, intakes.Count);
        Assert.Equal(new[] { buffers[0] }, released);
        Assert.True(intakes.TryTake(mailboxEmpty: false, out Playback.Intake oldest));
        Assert.Same(buffers[1], oldest.Buffer);

        intakes.Clear();
        Assert.Equal(0, intakes.Count);
        Assert.Equal(buffers.Length - 1, released.Count);                   // everything but the one taken went back
    }

    [Theory]
    [InlineData(0L, 0L, 0L, 3_000L, false)]                                  // no inbound resolve in flight
    [InlineData(4L, 4L, 1_000L, 3_000L, true)]                               // the load's resolve is still out
    [InlineData(4L, 5L, 1_000L, 3_000L, false)]                              // a newer context load superseded it
    [InlineData(4L, 4L, 3_000L, 3_000L, false)]                              // the hold ran out
    public void An_inbound_resolve_holds_the_mailbox_until_it_answers_is_superseded_or_times_out(
        long holdSeq, long contextSeq, long nowMs, long untilMs, bool holds)
        => Assert.Equal(holds, Playback.HoldsMailbox(holdSeq, contextSeq, nowMs, untilMs));

    // ── a controller's rows behind the deck (set_queue / update_context) ────────────────────────────────────────────

    [Fact]
    public void A_controllers_rows_replace_what_follows_the_deck_user_queue_first_and_history_stays()
    {
        int[] live = [1, 2, 3, 4, 5];
        QueueEdge[] liveRows =
        [
            Row(QueueBucket.History, 1), Row(QueueBucket.History, 2), Row(QueueBucket.NowPlaying, 3),
            Row(QueueBucket.UserQueue, 4, QueueProvider.Queue), Row(QueueBucket.NextUp, 5),
        ];
        int[] next = [10, 11, 12, 0, 13];
        QueueEdge[] nextRows =
        [
            Row(QueueBucket.NextUp, 20), Row(QueueBucket.NextUp, 21, QueueProvider.Queue),
            Row(QueueBucket.NextUp, 22, QueueProvider.Autoplay), Row(QueueBucket.NextUp, 23),
            Row(QueueBucket.NextUp, 24, QueueProvider.Queue),
        ];
        var packed = new int[16];
        var rows = new QueueEdge[16];

        int n = Playback.SpliceQueue(live, liveRows, 2, next, nextRows, packed, rows);

        Assert.Equal(7, n);
        Assert.Equal(new[] { 1, 2, 3, 11, 13, 10, 12 }, packed[..n]);
        Assert.Equal(new[] { B(QueueBucket.History), B(QueueBucket.History), B(QueueBucket.NowPlaying), B(QueueBucket.UserQueue),
            B(QueueBucket.UserQueue), B(QueueBucket.NextUp), B(QueueBucket.NextUp) }, Buckets(rows.AsSpan(0, n)));
        Assert.Equal(21UL, rows[3].ItemId);
        Assert.Equal((byte)QueueProvider.Autoplay, rows[6].Provider);
        Assert.True(Queue.IsOrdered(rows.AsSpan(0, n)));
    }

    [Fact]
    public void A_splice_rebuckets_the_kept_rows_and_refuses_a_deck_that_is_not_a_row()
    {
        int[] live = [1, 2];
        QueueEdge[] liveRows = [Row(QueueBucket.NextUp, 1), Row(QueueBucket.NowPlaying, 2)];
        var packed = new int[4];
        var rows = new QueueEdge[4];

        Assert.Equal(2, Playback.SpliceQueue(live, liveRows, 1, [], [], packed, rows));
        Assert.Equal(B(QueueBucket.History), rows[0].Bucket);

        Assert.Equal(-1, Playback.SpliceQueue(live, liveRows, 2, [], [], packed, rows));
        Assert.Equal(-1, Playback.SpliceQueue(live, liveRows, -1, [], [], packed, rows));
        Assert.Equal(2, Playback.SpliceQueue(live, liveRows, 1, [7, 8, 9], [Row(QueueBucket.NextUp, 7), Row(QueueBucket.NextUp, 8),
            Row(QueueBucket.NextUp, 9)], packed.AsSpan(0, 2), rows.AsSpan(0, 2)));   // nothing past the output
    }

    // ── the user queue a new context keeps (0.2.9 keepUserQueue) ────────────────────────────────────────────────────

    [Fact]
    public void A_new_context_keeps_the_queued_rows_still_waiting_after_the_deck_in_their_order()
    {
        int[] packed = [10, 11, 12, 13, 14, 15, 16];
        QueueEdge[] rows =
        [
            Row(QueueBucket.History, 1, QueueProvider.Queue),      // a queued row already played
            Row(QueueBucket.History, 2),
            Row(QueueBucket.NowPlaying, 3, QueueProvider.Queue),   // the deck is playing a queued row
            Row(QueueBucket.UserQueue, 4, QueueProvider.Queue),
            Row(QueueBucket.UserQueue, 5, QueueProvider.Queue),
            Row(QueueBucket.NextUp, 6),
            Row(QueueBucket.NextUp, 7, QueueProvider.Autoplay),
        ];
        var keptPacked = new int[8];
        var keptRows = new QueueEdge[8];

        int n = Playback.RemotePlan.KeptQueue(packed, rows, keptPacked, keptRows);

        Assert.Equal(2, n);
        Assert.Equal(new[] { 13, 14 }, keptPacked[..n]);
        Assert.Equal(4UL, keptRows[0].ItemId);
        Assert.Equal(5UL, keptRows[1].ItemId);
        Assert.Equal(B(QueueBucket.UserQueue), keptRows[0].Bucket);
        Assert.Equal((byte)QueueProvider.Queue, keptRows[1].Provider);
        Assert.Equal(1, Playback.RemotePlan.KeptQueue(packed, rows, keptPacked.AsSpan(0, 1), keptRows));   // capped
    }

    [Fact]
    public void With_no_deck_row_the_rows_in_the_user_queue_bucket_are_kept()
    {
        int[] packed = [20, 21];
        QueueEdge[] rows = [Row(QueueBucket.UserQueue, 8), Row(QueueBucket.NextUp, 9)];
        var keptPacked = new int[2];
        var keptRows = new QueueEdge[2];

        Assert.Equal(1, Playback.RemotePlan.KeptQueue(packed, rows, keptPacked, keptRows));
        Assert.Equal(20, keptPacked[0]);
        Assert.Equal(0, Playback.RemotePlan.KeptQueue([], [], keptPacked, keptRows));
    }

    // ── the autoplay tail a same-context play keeps ─────────────────────────────────────────────────────────────────

    // History, the deck, one queued row, two context rows, three autoplay rows; a played autoplay row in history.
    static readonly int[] s_livePacked = [10, 11, 12, 13, 14, 15, 16, 17, 18];
    static readonly QueueEdge[] s_liveRows =
    [
        Row(QueueBucket.History, 1, QueueProvider.Autoplay),
        Row(QueueBucket.History, 2),
        Row(QueueBucket.NowPlaying, 3),
        Row(QueueBucket.UserQueue, 4, QueueProvider.Queue),
        Row(QueueBucket.NextUp, 5),
        Row(QueueBucket.NextUp, 6),
        Row(QueueBucket.NextUp, 7, QueueProvider.Autoplay),
        Row(QueueBucket.NextUp, 8, QueueProvider.Autoplay),
        Row(QueueBucket.NextUp, 9, QueueProvider.Autoplay),
    ];

    [Fact]
    public void The_same_context_keeps_the_autoplay_rows_still_waiting_in_their_order()
    {
        var packed = new int[9];
        var rows = new QueueEdge[9];

        int n = Playback.RemotePlan.KeptAutoplay(s_livePacked, s_liveRows, sameContext: true, packed, rows);

        Assert.Equal(3, n);
        Assert.Equal(new[] { 16, 17, 18 }, packed[..n]);
        Assert.Equal(new ulong[] { 7, 8, 9 }, new[] { rows[0].ItemId, rows[1].ItemId, rows[2].ItemId });
        Assert.All(rows[..n], r => Assert.Equal((byte)QueueProvider.Autoplay, r.Provider));
        Assert.All(rows[..n], r => Assert.Equal(B(QueueBucket.NextUp), r.Bucket));
        Assert.Equal(2, Playback.RemotePlan.KeptAutoplay(s_livePacked, s_liveRows, sameContext: true, packed.AsSpan(0, 2), rows));   // capped
    }

    [Fact]
    public void A_different_context_drops_the_autoplay_rows_and_still_keeps_the_user_queue()
    {
        var packed = new int[9];
        var rows = new QueueEdge[9];

        Assert.Equal(0, Playback.RemotePlan.KeptAutoplay(s_livePacked, s_liveRows, sameContext: false, packed, rows));
        Assert.Equal(1, Playback.RemotePlan.KeptQueue(s_livePacked, s_liveRows, packed, rows));
        Assert.Equal(13, packed[0]);
        Assert.Equal(0, Playback.RemotePlan.KeptAutoplay([], [], sameContext: true, packed, rows));
    }

    [Fact]
    public void A_same_context_layout_lands_the_kept_queue_after_the_deck_and_the_autoplay_tail_after_the_context_run()
    {
        int[] context = [1, 2, 3, 4];
        var keptPacked = new int[9];
        var keptRows = new QueueEdge[9];
        var autoPacked = new int[9];
        var autoRows = new QueueEdge[9];
        int kept = Playback.RemotePlan.KeptQueue(s_livePacked, s_liveRows, keptPacked, keptRows);
        int auto = Playback.RemotePlan.KeptAutoplay(s_livePacked, s_liveRows, sameContext: true, autoPacked, autoRows);
        var packed = new int[20];
        var rows = new QueueEdge[20];

        int n = Playback.RemotePlan.Layout(context, 1, keptPacked.AsSpan(0, kept), keptRows.AsSpan(0, kept),
            autoPacked.AsSpan(0, auto), autoRows.AsSpan(0, auto), packed, rows, out int deck);

        Assert.Equal(8, n);
        Assert.Equal(1, deck);
        Assert.Equal(new[] { 1, 2, 13, 3, 4, 16, 17, 18 }, packed[..n]);
        Assert.Equal(new[] { B(QueueBucket.History), B(QueueBucket.NowPlaying), B(QueueBucket.UserQueue), B(QueueBucket.NextUp),
            B(QueueBucket.NextUp), B(QueueBucket.NextUp), B(QueueBucket.NextUp), B(QueueBucket.NextUp) }, Buckets(rows.AsSpan(0, n)));
        Assert.Equal((byte)QueueProvider.Context, rows[4].Provider);
        Assert.Equal((byte)QueueProvider.Autoplay, rows[5].Provider);
        Assert.True(Queue.IsOrdered(rows.AsSpan(0, n)));

        // a different context: the same layout without the tail
        Assert.Equal(5, Playback.RemotePlan.Layout(context, 1, keptPacked.AsSpan(0, kept), keptRows.AsSpan(0, kept), [], [], packed, rows, out deck));
        Assert.Equal(new[] { 1, 2, 13, 3, 4 }, packed[..5]);
    }

    [Fact]
    public void The_same_context_is_the_same_id_and_never_an_empty_one()
    {
        var album = EntityId.Parse("spotify:album:2noRn2Aes5aoNVsU6iWThc".AsSpan());
        var other = EntityId.Parse("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M".AsSpan());
        Assert.True(Playback.RemotePlan.SameContext(album, album));
        Assert.False(Playback.RemotePlan.SameContext(album, other));
        Assert.False(Playback.RemotePlan.SameContext(default, default));
        Assert.False(Playback.RemotePlan.SameContext(default, album));
    }

    [Fact]
    public void A_held_context_lays_out_history_the_deck_the_kept_queue_then_next_up()
    {
        int[] context = [1, 2, 3, 4, 5, 6];
        int[] kept = [90, 91];
        QueueEdge[] keptRows = [Row(QueueBucket.UserQueue, 40, QueueProvider.Queue), Row(QueueBucket.UserQueue, 41, QueueProvider.Queue)];
        var packed = new int[20];
        var rows = new QueueEdge[20];

        int n = Playback.RemotePlan.Layout(context, 3, kept, keptRows, [], [], packed, rows, out int deck);

        Assert.Equal(8, n);
        Assert.Equal(3, deck);
        Assert.Equal(new[] { 1, 2, 3, 4, 90, 91, 5, 6 }, packed[..n]);
        Assert.Equal(new[] { B(QueueBucket.History), B(QueueBucket.History), B(QueueBucket.History), B(QueueBucket.NowPlaying),
            B(QueueBucket.UserQueue), B(QueueBucket.UserQueue), B(QueueBucket.NextUp), B(QueueBucket.NextUp) }, Buckets(rows.AsSpan(0, n)));
        Assert.Equal(40UL, rows[4].ItemId);
        Assert.Equal((byte)QueueProvider.Context, rows[0].Provider);
        Assert.True(Queue.IsOrdered(rows.AsSpan(0, n)));
    }

    [Fact]
    public void A_layout_caps_history_at_fifty_and_the_run_at_its_output()
    {
        var context = new int[100];
        for (int i = 0; i < context.Length; i++) context[i] = i + 1;
        var packed = new int[60];
        var rows = new QueueEdge[60];

        int n = Playback.RemotePlan.Layout(context, 80, [], [], [], [], packed, rows, out int deck);

        Assert.Equal(60, n);
        Assert.Equal(Playback.RemotePlan.HistoryCap, deck);
        Assert.Equal(31, packed[0]);
        Assert.Equal(81, packed[deck]);
        Assert.Equal(90, packed[59]);
    }

    [Fact]
    public void A_layout_skips_unrepresentable_rows_and_refuses_an_unrepresentable_start()
    {
        var packed = new int[4];
        var rows = new QueueEdge[4];

        Assert.Equal(2, Playback.RemotePlan.Layout([1, 0, 3], 2, [], [], [], [], packed, rows, out int deck));
        Assert.Equal(1, deck);
        Assert.Equal(new[] { 1, 3 }, packed[..2]);

        Assert.Equal(0, Playback.RemotePlan.Layout([0, 2], 0, [], [], [], [], packed, rows, out deck));
        Assert.Equal(-1, deck);
        Assert.Equal(0, Playback.RemotePlan.Layout([1, 2], 5, [], [], [], [], packed, rows, out deck));
        Assert.Equal(-1, deck);
    }

    // ── radio ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Song_radio_and_artist_radio_play_the_seeds_station()
    {
        Assert.True(EntityId.TryParseGid("spotify:track:7idegBIikag5rTZP4WZihP"u8, out EntityId track));
        Assert.True(EntityId.TryParseGid("spotify:artist:4Z8W4fKeB5YxbusRsdQVPb"u8, out EntityId artist));

        Assert.Equal("spotify:station:track:7idegBIikag5rTZP4WZihP", Playback.RemotePlan.StationUri(track));
        Assert.Equal("spotify:station:artist:4Z8W4fKeB5YxbusRsdQVPb", Playback.RemotePlan.StationUri(artist));
    }

    [Fact]
    public void A_seed_with_no_spotify_station_has_none()
    {
        Assert.Equal("", Playback.RemotePlan.StationUri(EntityId.ForGid(EntityKind.Episode, (UInt128)0x1234UL)));
        Assert.Equal("", Playback.RemotePlan.StationUri(EntityId.ForGid(EntityKind.Album, (UInt128)0x1234UL)));
        Assert.Equal("", Playback.RemotePlan.StationUri(default));
    }

    // ── "Start radio" the 0.2.9 way: seed → inspiredby-mix playlist, parked behind the deck (G-251) ────────────────

    [Fact]
    public void The_radio_seed_is_the_seeds_own_uri_and_only_a_track_or_an_artist_has_one()
    {
        Assert.True(EntityId.TryParseGid("spotify:track:7idegBIikag5rTZP4WZihP"u8, out EntityId track));
        Assert.True(EntityId.TryParseGid("spotify:artist:4Z8W4fKeB5YxbusRsdQVPb"u8, out EntityId artist));

        Assert.Equal("spotify:track:7idegBIikag5rTZP4WZihP", Playback.RemotePlan.RadioSeedUri(track));
        Assert.Equal("spotify:artist:4Z8W4fKeB5YxbusRsdQVPb", Playback.RemotePlan.RadioSeedUri(artist));
        Assert.Equal("", Playback.RemotePlan.RadioSeedUri(EntityId.ForGid(EntityKind.Episode, (UInt128)0x1234UL)));
        Assert.Equal("", Playback.RemotePlan.RadioSeedUri(EntityId.ForGid(EntityKind.Playlist, (UInt128)0x1234UL)));
        Assert.Equal("", Playback.RemotePlan.RadioSeedUri(default));
    }

    [Fact]
    public void A_parked_radio_keeps_history_and_the_deck_lands_the_kept_queue_next_and_drops_the_deck_from_the_radio()
    {
        // The live queue: two played rows, the deck (target 3), a queued row and the old context's continuation.
        int[] live = [1, 2, 3, 4, 5, 6];
        QueueEdge[] liveRows =
        [
            Row(QueueBucket.History, 11), Row(QueueBucket.History, 12), Row(QueueBucket.NowPlaying, 13),
            Row(QueueBucket.UserQueue, 14, QueueProvider.Queue), Row(QueueBucket.NextUp, 15), Row(QueueBucket.NextUp, 16),
        ];
        int[] kept = [4];
        QueueEdge[] keptRows = [Row(QueueBucket.UserQueue, 14, QueueProvider.Queue)];
        // Song radio leads with its seed — the recording on the deck (target 3) — then three others.
        int[] radio = [3, 7, 8, 9];
        QueueEdge[] radioRows = [Row(QueueBucket.NextUp, 21), Row(QueueBucket.NextUp, 22), Row(QueueBucket.NextUp, 23), Row(QueueBucket.NextUp, 24)];
        var packed = new int[20];
        var rows = new QueueEdge[20];

        int n = Playback.RemotePlan.RadioAfterCurrent(live, liveRows, 2, kept, keptRows, radio, radioRows, packed, rows, out int dropped);

        Assert.Equal(7, n);
        Assert.Equal(1, dropped);
        Assert.Equal(new[] { 1, 2, 3, 4, 7, 8, 9 }, packed[..n]);                 // the old continuation (5, 6) is gone
        Assert.Equal(new[] { B(QueueBucket.History), B(QueueBucket.History), B(QueueBucket.NowPlaying), B(QueueBucket.UserQueue),
            B(QueueBucket.NextUp), B(QueueBucket.NextUp), B(QueueBucket.NextUp) }, Buckets(rows.AsSpan(0, n)));
        Assert.Equal(13UL, rows[2].ItemId);                                          // the deck row is the SAME row
        Assert.Equal(14UL, rows[3].ItemId);
        Assert.Equal(22UL, rows[4].ItemId);                                          // radio[1] is first: the seed was dropped
        Assert.Equal((byte)QueueProvider.Context, rows[4].Provider);
        Assert.True(Queue.IsOrdered(rows.AsSpan(0, n)));
    }

    [Fact]
    public void A_radio_that_does_not_carry_the_deck_row_keeps_every_row()
    {
        int[] live = [3];
        QueueEdge[] liveRows = [Row(QueueBucket.NowPlaying, 13)];
        int[] radio = [7, 8, 9];
        QueueEdge[] radioRows = [Row(QueueBucket.NextUp, 21), Row(QueueBucket.NextUp, 22), Row(QueueBucket.NextUp, 23)];
        var packed = new int[8];
        var rows = new QueueEdge[8];

        int n = Playback.RemotePlan.RadioAfterCurrent(live, liveRows, 0, [], [], radio, radioRows, packed, rows, out int dropped);

        Assert.Equal(4, n);
        Assert.Equal(0, dropped);
        Assert.Equal(new[] { 3, 7, 8, 9 }, packed[..n]);
        Assert.Equal(B(QueueBucket.NowPlaying), rows[0].Bucket);
        Assert.Equal(B(QueueBucket.NextUp), rows[1].Bucket);
    }

    [Fact]
    public void A_radio_layout_refuses_a_deck_that_is_not_a_live_row_and_skips_unrepresentable_rows()
    {
        int[] live = [3, 0];
        QueueEdge[] liveRows = [Row(QueueBucket.NowPlaying, 13), Row(QueueBucket.NextUp, 14)];
        var packed = new int[6];
        var rows = new QueueEdge[6];

        Assert.Equal(-1, Playback.RemotePlan.RadioAfterCurrent(live, liveRows, 2, [], [], [7], [Row(QueueBucket.NextUp, 21)], packed, rows, out _));
        Assert.Equal(-1, Playback.RemotePlan.RadioAfterCurrent(live, liveRows, -1, [], [], [7], [Row(QueueBucket.NextUp, 21)], packed, rows, out _));
        Assert.Equal(-1, Playback.RemotePlan.RadioAfterCurrent(live, liveRows, 1, [], [], [7], [Row(QueueBucket.NextUp, 21)], packed, rows, out _));  // target 0

        int n = Playback.RemotePlan.RadioAfterCurrent(live, liveRows, 0, [], [], [0, 7], [Row(QueueBucket.NextUp, 21), Row(QueueBucket.NextUp, 22)],
            packed, rows, out int dropped);
        Assert.Equal(2, n);
        Assert.Equal(0, dropped);                                                    // an unrepresentable row is skipped, not "the deck"
        Assert.Equal(new[] { 3, 7 }, packed[..n]);

        Assert.Equal(1, Playback.RemotePlan.RadioAfterCurrent(live, liveRows, 0, [], [], [7, 8], [Row(QueueBucket.NextUp, 21), Row(QueueBucket.NextUp, 22)],
            packed.AsSpan(0, 1), rows.AsSpan(0, 1), out _));                         // nothing past the output
    }

    [Fact]
    public void A_radio_parks_only_behind_a_live_local_deck_row()
    {
        var s = Playback.State.Initial;
        s.Us = Playback.DeviceHash("wavee-device");
        Assert.False(Playback.RemotePlan.RadioParks(in s, deckIsLiveRow: false));   // idle: nothing to park behind

        s.CurrentId = EntityId.ForGid(EntityKind.Track, (UInt128)0x51UL);
        s.Phase = Playback.Phase.Playing;
        Assert.True(Playback.RemotePlan.RadioParks(in s, deckIsLiveRow: true));
        Assert.False(Playback.RemotePlan.RadioParks(in s, deckIsLiveRow: false));   // the queue no longer holds the deck row

        s.Phase = Playback.Phase.Paused;
        Assert.True(Playback.RemotePlan.RadioParks(in s, deckIsLiveRow: true));     // paused still finishes first
        s.Phase = Playback.Phase.Ended;
        Assert.False(Playback.RemotePlan.RadioParks(in s, deckIsLiveRow: true));    // an ended queue plays the radio now
        s.Phase = Playback.Phase.Playing;
        s.Parked = true;
        Assert.False(Playback.RemotePlan.RadioParks(in s, deckIsLiveRow: true));    // a restored, never-resumed deck too

        s.Parked = false;
        var phone = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, Playback.DeviceHash("phone"), 2_000);
        Playback.Ownership.Fold(ref s.Own, in phone, s.Us);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        Assert.False(Playback.RemotePlan.RadioParks(in s, deckIsLiveRow: true));    // a foreign owner: the one context path
    }

    // ── the PutState video offer ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_snapshot_carries_the_rows_video_gid_only_with_its_offer_and_only_while_active()
    {
        const string gid = "0123456789abcdef0123456789abcdef";
        var identity = new Playback.DeviceIdentity("wavee-device", "Wavee", "cid", "Win32", "1.2.3", "3.2.6");
        var s = Playback.State.Initial;
        s.Us = Playback.DeviceHash("wavee-device");
        s.CurrentId = EntityId.ForGid(EntityKind.Track, (UInt128)0x77UL);
        s.Phase = Playback.Phase.Playing;
        s.Repeat = RepeatMode.Off;
        Playback.Ownership.Claim(ref s.Own, Playback.ClaimCause.UserPlay, 1, 0, 0, acknowledged: false);

        var offer = Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 0,
            hasVideo: true, videoGid: gid);
        Assert.Equal(gid, offer.VideoGid);
        Assert.Equal(gid, offer.WithMessageId(9).VideoGid);

        Assert.Equal("", Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 0,
            hasVideo: false, videoGid: gid).VideoGid);

        var idle = s;
        idle.Own = Playback.OwnerState.Initial;
        Assert.Equal("", Playback.Snapshot.Of(in idle, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 0,
            hasVideo: true, videoGid: gid).VideoGid);
    }

    // ── R4-1: a newer load supersedes one still resolving (G-244) ───────────────────────────────────────────────────

    [Fact]
    public void The_intake_queue_says_whether_a_load_is_waiting_behind_a_queue_body()
    {
        var intakes = new Playback.IntakeQueue(static _ => { });
        Assert.False(intakes.HasLoad);

        intakes.Arrive(QueueIntake(ClusterBuffer.Rent()), pending: 0);
        Assert.False(intakes.HasLoad);

        intakes.Arrive(LoadIntake(ClusterBuffer.Rent()), pending: 3);   // still waiting behind three mailbox items
        Assert.True(intakes.HasLoad);

        intakes.Clear();
        Assert.False(intakes.HasLoad);
    }

    [Theory]
    [InlineData(4L, 4L, 1_000L, 3_000L, true, true)]    // a newer load is waiting while the older one resolves: superseded
    [InlineData(4L, 4L, 1_000L, 3_000L, false, false)]  // nothing newer: the hold stands
    [InlineData(0L, 0L, 1_000L, 3_000L, true, false)]   // no hold to supersede: the load runs in arrival order anyway
    [InlineData(4L, 5L, 1_000L, 3_000L, true, false)]   // already superseded by a local play
    [InlineData(4L, 4L, 3_000L, 3_000L, true, false)]   // the hold already ran out
    public void A_newer_inbound_load_supersedes_the_hold_of_one_still_resolving(long holdSeq, long contextSeq, long nowMs,
        long untilMs, bool loadWaiting, bool supersedes)
        => Assert.Equal(supersedes, Playback.SupersedesHold(holdSeq, contextSeq, nowMs, untilMs, loadWaiting));

    // ── R4-1: update_context reshuffles, set_queue keeps the controller's order (G-245) ─────────────────────────────

    [Theory]
    [InlineData(Spotify.Decode.RemoteCmd.UpdateContext, true, true)]
    [InlineData(Spotify.Decode.RemoteCmd.UpdateContext, false, false)]
    [InlineData(Spotify.Decode.RemoteCmd.SetQueue, true, false)]
    [InlineData(Spotify.Decode.RemoteCmd.SetQueue, false, false)]
    public void Update_context_is_shuffled_again_while_shuffle_is_on_and_set_queue_never(Spotify.Decode.RemoteCmd kind,
        bool shuffling, bool reshuffles)
        => Assert.Equal(reshuffles, Playback.ReshufflesAfter(kind, shuffling));

    // ── R4-1: context paging (G-242) ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hm://context-resolve/v1/page/abc?offset=100", "/context-resolve/v1/page/abc?offset=100")]
    [InlineData("https://spclient.wg.spotify.com/context-resolve/v1/page/abc", "/context-resolve/v1/page/abc")]
    [InlineData("/context-resolve/v1/page/abc", "/context-resolve/v1/page/abc")]
    [InlineData("context-resolve/v1/page/abc", "/context-resolve/v1/page/abc")]
    [InlineData("https://spclient.wg.spotify.com", "")]
    [InlineData("", "")]
    public void A_next_page_url_is_asked_of_spclient_by_its_path(string url, string path)
        => Assert.Equal(path, Playback.RemotePlan.PageRoute(url));

    // ── R4-1: the PUT's attribution ages (G-246) ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(912u, 1_000L, 1_000L, 912u)]
    [InlineData(912u, 1_000L, 10_999L, 912u)]
    [InlineData(912u, 1_000L, 11_000L, 0u)]             // 10 s on, the PUT answers some other change
    [InlineData(0u, 1_000L, 1_000L, 0u)]
    public void A_controller_command_owns_the_puts_of_the_next_ten_seconds_only(uint id, long stampedAt, long now, uint expected)
        => Assert.Equal(expected, Playback.CommandAttribution.MessageId(id, stampedAt, now));

    [Fact]
    public void A_snapshot_captured_eleven_seconds_after_the_command_names_no_command()
    {
        var identity = new Playback.DeviceIdentity("wavee-device", "Wavee", "cid", "Win32", "1.2.3", "3.2.6");
        var s = Playback.State.Initial;
        s.LastCommandMessageId = 77;
        s.LastCommandAtMs = 5_000;

        Assert.Equal(77u, Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 9_000).LastCommandMessageId);
        Assert.Equal(0u, Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 16_000).LastCommandMessageId);
    }

    // ── R4-1: the snapshot's wire facts (G-240, G-036) ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_loading_deck_is_captured_as_the_engaged_transport_buffering()
    {
        var identity = new Playback.DeviceIdentity("wavee-device", "Wavee", "cid", "Win32", "1.2.3", "3.2.6");
        var s = Playback.State.Initial;
        s.CurrentId = EntityId.ForGid(EntityKind.Track, (UInt128)0x77UL);
        s.Phase = Playback.Phase.Loading;
        Playback.Ownership.Claim(ref s.Own, Playback.ClaimCause.UserPlay, 1, 0, 0, acknowledged: false);

        var snapshot = Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 0);

        Assert.True(snapshot.IsBuffering);
        Assert.False(snapshot.IsPlaying);
        Assert.False(snapshot.IsPaused);
    }

    [Fact]
    public void The_sign_outs_put_is_inactive_with_our_volume_and_no_player_half_even_while_playback_is_ours()
    {
        var identity = new Playback.DeviceIdentity("wavee-device", "Wavee", "cid", "Win32", "1.2.3", "3.2.6");
        var s = Playback.State.Initial;
        s.CurrentId = EntityId.ForGid(EntityKind.Track, (UInt128)0x77UL);
        s.Phase = Playback.Phase.Playing;
        s.Volume = 0.5f;
        Playback.Ownership.Claim(ref s.Own, Playback.ClaimCause.UserPlay, 1, 0, 0, acknowledged: false);

        var retiring = Playback.Snapshot.Retiring(in s, in identity, 1_700_000_000_000);

        Assert.False(retiring.IsActive);
        Assert.Equal(Playback.PublishReason.BecameInactive, retiring.Reason);
        Assert.False(retiring.HasTrack);
        Assert.Equal(Playback.Input.WireVolume(0.5f), retiring.Volume);
    }
}
