// ── Wavee.Tests/ShellDeckTests.cs — the session document's deck section (gap batch B3b, G-078) ───────────────────────
//
// The pure half of `Shell/Shell.Host.Deck.cs`: `Playback.RestorePoint` ↔ `SessionDeckDto`. No window, no file, no
// reducer — the wiring that calls these (the launch restore, `PersistDeck`, the exit capture) is a composition line each.
//
//   A DOCUMENT IS NOT TRUSTED. A garbled or hand-edited session.json must restore NOTHING rather than a row: a uri no
//   provider owns, a catalog uri that is not a gid, and an identity that is not playable all come back empty.
//
//   EMPTY CLEARS. A foreign owner's row and a signed-out account are not this device's session; their empty point is the
//   instruction to drop the section, not to keep the last one.

using Wavee;
using Xunit;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

public class ShellDeckTests
{
    static readonly EntityId Track = EntityId.ForGid(EntityKind.Track, (UInt128)0x1234_5678_9ABC_DEF0UL);
    static readonly EntityId Episode = EntityId.ForGid(EntityKind.Episode, (UInt128)0x0BAD_F00DUL);
    static readonly EntityId Album = EntityId.ForGid(EntityKind.Album, (UInt128)0x0FED_CBA9_8765_4321UL);

    [Fact]
    public void A_deck_round_trips_through_the_session_section()
    {
        var point = new Playback.RestorePoint(Track, Album, CursorIndex: 4, PositionMs: 61_000, DurationMs: 200_000,
            Shuffle: true, Repeat: RepeatMode.Context);

        Shell.SessionDeckDto? dto = Shell.SessionDeck.ToDto(in point);

        Assert.NotNull(dto);
        Assert.Equal(Track.Text, dto!.Track);
        Assert.Equal(Album.Text, dto.Context);
        Assert.Equal(point, Shell.SessionDeck.FromDto(dto));
    }

    [Fact]
    public void An_episode_with_no_context_round_trips_without_one()
    {
        var point = new Playback.RestorePoint(Episode, default, CursorIndex: -1, PositionMs: 5_000, DurationMs: 0,
            Shuffle: false, Repeat: RepeatMode.Off);

        Shell.SessionDeckDto? dto = Shell.SessionDeck.ToDto(in point);

        Assert.Null(dto!.Context);
        Assert.Equal(point, Shell.SessionDeck.FromDto(dto));
    }

    [Fact]
    public void An_empty_point_clears_the_section()
        => Assert.Null(Shell.SessionDeck.ToDto(default(Playback.RestorePoint)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a uri")]
    [InlineData("spotify:track:not-a-gid")]
    public void A_section_naming_no_track_restores_nothing(string? track)
        => Assert.True(Shell.SessionDeck.FromDto(new Shell.SessionDeckDto { Track = track, PositionMs = 1_000 }).IsEmpty);

    [Fact]
    public void A_section_naming_something_that_does_not_play_restores_nothing()
    {
        Assert.True(Shell.SessionDeck.FromDto(new Shell.SessionDeckDto { Track = Album.Text }).IsEmpty);
        Assert.True(Shell.SessionDeck.FromDto(null).IsEmpty);
    }

    [Fact]
    public void A_bad_context_drops_the_context_and_keeps_the_track()
    {
        var point = Shell.SessionDeck.FromDto(new Shell.SessionDeckDto { Track = Track.Text, Context = "garbage" });

        Assert.Equal(Track, point.Track);
        Assert.True(point.Context.IsEmpty);
    }

    [Fact]
    public void A_position_past_the_duration_an_unknown_repeat_and_a_negative_cursor_are_pulled_into_range()
    {
        var point = Shell.SessionDeck.FromDto(new Shell.SessionDeckDto
        {
            Track = Track.Text, PositionMs = 300_000, DurationMs = 200_000, Repeat = 9, CursorIndex = -7,
        });

        Assert.Equal(200_000, point.PositionMs);
        Assert.Equal(RepeatMode.Off, point.Repeat);
        Assert.Equal(-1, point.CursorIndex);
    }

    [Fact]
    public void The_write_gate_sees_a_moved_position_and_ignores_a_repeat_of_the_same_deck()
    {
        var point = new Playback.RestorePoint(Track, Album, 2, 10_000, 200_000, false, RepeatMode.Off);
        Shell.SessionDeckDto? first = Shell.SessionDeck.ToDto(in point);
        Shell.SessionDeckDto? same = Shell.SessionDeck.ToDto(in point);
        Shell.SessionDeckDto? moved = Shell.SessionDeck.ToDto(point with { PositionMs = 11_000 });

        Assert.True(Shell.SessionDeck.Same(first, same));
        Assert.False(Shell.SessionDeck.Same(first, moved));
        Assert.False(Shell.SessionDeck.Same(first, null));
        Assert.True(Shell.SessionDeck.Same(null, null));
    }

    // ── the queue rows ride with the deck (2026-09-16: "when I restart the app the queue is also gone") ────────────
    //
    // The document carries the queue's rows in reading order, windowed around the deck (`SelectRows`), and the section's
    // cursor indexes THOSE rows. A restore lays them back before any context seed is decided.

    static EntityId TrackN(int n) => EntityId.ForGid(EntityKind.Track, (UInt128)(0x1000UL + (ulong)n));

    static QueueEdge Edge(QueueBucket bucket, QueueProvider provider = QueueProvider.Context, ulong item = 0)
        => new(item, (byte)provider, (byte)bucket);

    /// <summary>A queue of <paramref name="history"/> history rows, the deck, <paramref name="user"/> user-queue rows and
    /// <paramref name="next"/> next-up autoplay rows, every row a distinct track and a distinct item id.</summary>
    static (EntityId[] ids, QueueEdge[] rows, int deck) Laid(int history, int user, int next)
    {
        int n = history + 1 + user + next;
        var ids = new EntityId[n];
        var rows = new QueueEdge[n];
        for (int i = 0; i < n; i++)
        {
            ids[i] = TrackN(i);
            QueueBucket bucket = i < history ? QueueBucket.History
                : i == history ? QueueBucket.NowPlaying
                : i < history + 1 + user ? QueueBucket.UserQueue
                : QueueBucket.NextUp;
            QueueProvider provider = bucket == QueueBucket.UserQueue ? QueueProvider.Queue
                : bucket == QueueBucket.NextUp ? QueueProvider.Autoplay : QueueProvider.Context;
            rows[i] = Edge(bucket, provider, item: 100UL + (ulong)i);
        }
        return (ids, rows, history);
    }

    static Playback.RestorePoint PointOn(EntityId track, int cursor)
        => new(track, Album, cursor, 10_000, 200_000, false, RepeatMode.Off);

    [Fact]
    public void The_rows_are_written_in_queue_order_and_the_cursor_indexes_them()
    {
        var (ids, rows, deck) = Laid(history: 2, user: 1, next: 2);
        var point = PointOn(ids[deck], deck);

        Shell.SessionDeckDto? dto = Shell.SessionDeck.ToDto(in point, ids, rows, deck);

        Assert.NotNull(dto?.Rows);
        Assert.Equal(6, dto!.Rows!.Length);
        Assert.Equal(2, dto.CursorIndex);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(ids[i].Text, dto.Rows[i].Uri);
            Assert.Equal(rows[i].Bucket, dto.Rows[i].Bucket);
            Assert.Equal(rows[i].Provider, dto.Rows[i].Provider);
            Assert.Equal(rows[i].ItemId, dto.Rows[i].ItemId);
        }
        Assert.Equal((int)QueueBucket.NowPlaying, dto.Rows[2].Bucket);
        Assert.Equal((int)QueueProvider.Autoplay, dto.Rows[5].Provider);
    }

    [Fact]
    public void Twelve_history_rows_before_the_deck_keep_ten()
    {
        var (ids, rows, deck) = Laid(history: 12, user: 0, next: 3);
        var point = PointOn(ids[deck], deck);

        Shell.SessionDeckDto? dto = Shell.SessionDeck.ToDto(in point, ids, rows, deck);

        Assert.Equal(Shell.SessionDeck.HistoryKeep + 1 + 3, dto!.Rows!.Length);
        Assert.Equal(ids[2].Text, dto.Rows[0].Uri);
        Assert.Equal(Shell.SessionDeck.HistoryKeep, dto.CursorIndex);
        Assert.Equal(ids[deck].Text, dto.Rows[dto.CursorIndex].Uri);
    }

    [Fact]
    public void The_rows_are_capped_and_the_deck_always_makes_the_window()
    {
        var (ids, rows, deck) = Laid(history: 0, user: 0, next: 250);
        var point = PointOn(ids[deck], deck);

        Shell.SessionDeckDto? dto = Shell.SessionDeck.ToDto(in point, ids, rows, deck);

        Assert.Equal(Shell.SessionDeck.MaxRows, dto!.Rows!.Length);
        Assert.Equal(0, dto.CursorIndex);
        Assert.Equal(ids[Shell.SessionDeck.MaxRows - 1].Text, dto.Rows[^1].Uri);
    }

    [Fact]
    public void A_row_whose_id_does_not_play_is_skipped_and_the_cursor_moves_with_the_rest()
    {
        var (ids, rows, deck) = Laid(history: 1, user: 0, next: 1);
        ids[0] = Album;                                     // a history row that is not a track
        var point = PointOn(ids[deck], deck);

        Shell.SessionDeckDto? dto = Shell.SessionDeck.ToDto(in point, ids, rows, deck);

        Assert.Equal(2, dto!.Rows!.Length);
        Assert.Equal(0, dto.CursorIndex);
        Assert.Equal(ids[deck].Text, dto.Rows[0].Uri);
    }

    [Fact]
    public void A_deck_with_no_queue_writes_no_rows_and_an_empty_point_still_clears()
    {
        var point = PointOn(Track, 3);
        Assert.Null(Shell.SessionDeck.ToDto(in point)!.Rows);
        Assert.Null(Shell.SessionDeck.ToDto(in point, default, default, -1)!.Rows);
        Assert.Null(Shell.SessionDeck.ToDto(default(Playback.RestorePoint), default, default, -1));
    }

    [Fact]
    public void The_rows_round_trip_with_bucket_provider_and_item_id()
    {
        var (ids, rows, deck) = Laid(history: 1, user: 2, next: 2);
        var point = PointOn(ids[deck], deck);
        Shell.SessionDeckDto? dto = Shell.SessionDeck.ToDto(in point, ids, rows, deck);

        Playback.RestorePoint back = Shell.SessionDeck.FromDto(dto);

        Assert.True(back.HasRows);
        Assert.Equal(6, back.Rows!.Length);
        Assert.Equal(1, back.CursorIndex);
        Assert.Equal(ids[deck], back.Track);
        Assert.Equal(Album, back.Context);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(ids[i], back.Rows[i].Id);
            Assert.Equal(rows[i], back.Rows[i].Edge);
        }
    }

    [Fact]
    public void A_garbled_row_is_dropped_and_the_cursor_is_re_based_past_it()
    {
        var dto = new Shell.SessionDeckDto
        {
            Track = Track.Text, CursorIndex = 2,
            Rows =
            [
                new Shell.SessionQueueRowDto { Uri = TrackN(1).Text, Bucket = (int)QueueBucket.History },
                new Shell.SessionQueueRowDto { Uri = "spotify:track:not-a-gid", Bucket = (int)QueueBucket.History },
                new Shell.SessionQueueRowDto { Uri = Track.Text, Bucket = (int)QueueBucket.NowPlaying },
                new Shell.SessionQueueRowDto { Uri = Album.Text, Bucket = (int)QueueBucket.NextUp },
                new Shell.SessionQueueRowDto { Uri = TrackN(2).Text, Bucket = 9 },
                new Shell.SessionQueueRowDto { Uri = TrackN(3).Text, Bucket = (int)QueueBucket.NextUp, Provider = 7, ItemId = 5 },
            ],
        };

        Playback.RestorePoint point = Shell.SessionDeck.FromDto(dto);

        Assert.Equal(3, point.Rows!.Length);
        Assert.Equal(1, point.CursorIndex);
        Assert.Equal(Track, point.Rows[1].Id);
        Assert.Equal((byte)QueueProvider.Context, point.Rows[2].Edge.Provider);   // an unknown provider reads as context
        Assert.Equal(5UL, point.Rows[2].Edge.ItemId);
    }

    [Fact]
    public void A_cursor_whose_own_row_is_garbled_comes_back_as_none()
    {
        var dto = new Shell.SessionDeckDto
        {
            Track = Track.Text, CursorIndex = 0,
            Rows = [new Shell.SessionQueueRowDto { Uri = "garbage", Bucket = 0 }, new Shell.SessionQueueRowDto { Uri = TrackN(1).Text, Bucket = 2 }],
        };

        Assert.Equal(-1, Shell.SessionDeck.FromDto(dto).CursorIndex);
        Assert.Null(Shell.SessionDeck.FromDto(new Shell.SessionDeckDto { Track = Track.Text, CursorIndex = 0, Rows = [] }).Rows);
    }

    [Fact]
    public void A_document_without_rows_restores_as_before()
    {
        var point = new Playback.RestorePoint(Track, Album, 4, 61_000, 200_000, true, RepeatMode.Context);

        Playback.RestorePoint back = Shell.SessionDeck.FromDto(Shell.SessionDeck.ToDto(in point));

        Assert.Null(back.Rows);
        Assert.False(back.HasRows);
        Assert.Equal(point, back);
    }

    [Fact]
    public void The_write_gate_sees_a_changed_row_and_a_reorder_and_ignores_the_same_rows()
    {
        var (ids, rows, deck) = Laid(history: 0, user: 1, next: 2);
        var point = PointOn(ids[deck], deck);
        Shell.SessionDeckDto? first = Shell.SessionDeck.ToDto(in point, ids, rows, deck);
        Shell.SessionDeckDto? same = Shell.SessionDeck.ToDto(in point, ids, rows, deck);

        var swapped = (EntityId[])ids.Clone();
        (swapped[2], swapped[3]) = (swapped[3], swapped[2]);
        Shell.SessionDeckDto? reordered = Shell.SessionDeck.ToDto(in point, swapped, rows, deck);

        var changed = (QueueEdge[])rows.Clone();
        changed[3] = changed[3] with { ItemId = 999 };
        Shell.SessionDeckDto? edited = Shell.SessionDeck.ToDto(in point, ids, changed, deck);

        Assert.True(Shell.SessionDeck.Same(first, same));
        Assert.False(Shell.SessionDeck.Same(first, reordered));
        Assert.False(Shell.SessionDeck.Same(first, edited));
        Assert.False(Shell.SessionDeck.Same(first, Shell.SessionDeck.ToDto(in point)));
        Assert.False(Shell.SessionDeck.Same(first, Shell.SessionDeck.ToDto(in point, ids.AsSpan(0, 3), rows.AsSpan(0, 3), deck)));
    }

    [Theory]
    [InlineData(0, 0, 0, 200, 0, 0)]      // nothing → nothing
    [InlineData(5, 2, -1, 200, 0, 5)]     // no deck → from the head
    [InlineData(5, 2, 2, 200, 0, 5)]      // 2 history rows, all kept
    [InlineData(20, 12, 12, 200, 2, 18)]  // 12 history rows → 10 kept
    [InlineData(300, 0, 0, 200, 0, 200)]  // the cap
    [InlineData(300, 12, 12, 200, 2, 200)]// the cap counts the kept history
    [InlineData(5, 2, 9, 200, 0, 5)]      // a deck past the rows → from the head
    [InlineData(5, 2, 2, 0, 0, 0)]        // a zero cap writes nothing
    public void The_window_rule(int total, int history, int deck, int cap, int first, int count)
    {
        var rows = new QueueEdge[total];
        for (int i = 0; i < total; i++)
            rows[i] = Edge(i < history ? QueueBucket.History : i == history ? QueueBucket.NowPlaying : QueueBucket.NextUp);

        Shell.SessionDeck.SelectRows(rows, deck, cap, out int f, out int c);

        Assert.Equal(first, f);
        Assert.Equal(count, c);
    }

    [Fact]
    public void The_deck_is_the_now_playing_row_that_names_the_track_then_the_cursor()
    {
        var (ids, rows, deck) = Laid(history: 2, user: 0, next: 2);

        Assert.Equal(deck, Shell.SessionDeck.DeckIndex(ids, rows, ids[deck], deck));
        Assert.Equal(deck, Shell.SessionDeck.DeckIndex(ids, rows, ids[deck], cursor: -1));
        Assert.Equal(deck, Shell.SessionDeck.DeckIndex(ids, rows, ids[deck], cursor: 4));     // a stale cursor loses to the row
        Assert.Equal(4, Shell.SessionDeck.DeckIndex(ids, rows, ids[4], cursor: 4));          // a track on next-up: the cursor names it
        Assert.Equal(-1, Shell.SessionDeck.DeckIndex(ids, rows, Track, cursor: 1));          // nothing names the track
    }
}
