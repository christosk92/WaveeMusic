// ── Wavee.Tests/RefillTests.cs — the refill rule (Playback.Refill.cs) ─────────────────────────────────────────────────
//
// Pure: a QueueEdge[] and a cursor in, a RefillKind out. No scope, no reducer, no clock. The reducer's `CheckRefill`
// only applies the verdict, and `PlaybackStepTests` pins that side.

using Wavee;
using Xunit;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

public class RefillTests
{
    static QueueEdge Row(QueueBucket bucket, QueueProvider provider = QueueProvider.Context, ulong id = 1)
        => new(id, (byte)provider, (byte)bucket);

    /// <summary>A context run of <paramref name="rows"/> rows with the deck at <paramref name="cursor"/>: history before it,
    /// next-up after it.</summary>
    static QueueEdge[] Context(int rows, int cursor)
    {
        var edges = new QueueEdge[rows];
        for (int i = 0; i < rows; i++)
            edges[i] = Row(i < cursor ? QueueBucket.History : i == cursor ? QueueBucket.NowPlaying : QueueBucket.NextUp, id: (ulong)(i + 1));
        return edges;
    }

    /// <summary>The deck row followed by <paramref name="autoplay"/> autoplay rows.</summary>
    static QueueEdge[] AutoplayRun(int autoplay)
    {
        var edges = new QueueEdge[autoplay + 1];
        edges[0] = Row(QueueBucket.NowPlaying);
        for (int i = 1; i <= autoplay; i++) edges[i] = Row(QueueBucket.NextUp, QueueProvider.Autoplay, (ulong)(i + 1));
        return edges;
    }

    static Playback.RefillKind Decide(QueueEdge[] rows, int cursor, Playback.AutoplayPhase phase = Playback.AutoplayPhase.None,
        bool pagesKnown = true, bool morePages = false, bool autoplayPages = false, RepeatMode repeat = RepeatMode.Off)
        => Playback.Refill.Decide(rows, cursor, phase, pagesKnown, morePages, autoplayPages, repeat);

    [Fact]
    public void A_two_row_context_with_no_page_asks_autoplay_at_once()
    {
        Assert.Equal(Playback.RefillKind.AskAutoplay, Decide(Context(2, 0), 0));
    }

    [Fact]
    public void A_hundred_row_page_is_asked_for_its_next_page_at_seventy_five_not_seventy_four()
    {
        Assert.Equal(Playback.RefillKind.None, Decide(Context(100, 74), 74, morePages: true));
        Assert.Equal(Playback.RefillKind.PageContext, Decide(Context(100, 75), 75, morePages: true));
        Assert.Equal(Playback.RefillKind.None, Decide(Context(100, 0), 0, morePages: true));
    }

    [Fact]
    public void Three_rows_ahead_is_nearly_consumed_whatever_the_run_length()
    {
        Assert.True(Playback.Refill.NearlyConsumed(4, 3));
        Assert.True(Playback.Refill.NearlyConsumed(1000, 3));
        Assert.False(Playback.Refill.NearlyConsumed(16, 4));
        Assert.False(Playback.Refill.NearlyConsumed(0, 0));
    }

    [Fact]
    public void User_queued_rows_do_not_count_for_either_run()
    {
        // Two context rows, then thirty rows the user queued: still a two-row context, one ahead.
        var rows = new QueueEdge[32];
        rows[0] = Row(QueueBucket.NowPlaying);
        for (int i = 1; i <= 30; i++) rows[i] = Row(QueueBucket.UserQueue, QueueProvider.Queue, (ulong)(i + 1));
        rows[31] = Row(QueueBucket.NextUp, id: 99);

        Playback.Refill.Run(rows, 0, QueueProvider.Context, out int total, out int ahead);
        Assert.Equal(2, total);
        Assert.Equal(1, ahead);
        Assert.Equal(Playback.RefillKind.PageContext, Decide(rows, 0, morePages: true));
        Assert.Equal(Playback.RefillKind.AskAutoplay, Decide(rows, 0));
    }

    [Fact]
    public void A_consumed_autoplay_run_pages_when_a_page_is_held_and_re_asks_otherwise()
    {
        var rows = AutoplayRun(8);
        Assert.Equal(Playback.RefillKind.None, Decide(rows, 0));                              // eight ahead
        Assert.Equal(Playback.RefillKind.None, Decide(rows, 4, autoplayPages: true));         // four ahead
        Assert.Equal(Playback.RefillKind.PageAutoplay, Decide(rows, 5, autoplayPages: true)); // three ahead
        Assert.Equal(Playback.RefillKind.AskAutoplay, Decide(rows, 5));
    }

    [Fact]
    public void Nothing_is_asked_before_the_host_has_said_whether_the_context_pages()
    {
        Assert.Equal(Playback.RefillKind.None, Decide(Context(2, 0), 0, pagesKnown: false));
        Assert.Equal(Playback.RefillKind.None, Decide(Context(100, 90), 90, pagesKnown: false, morePages: true));
    }

    [Fact]
    public void Repeat_track_never_asks()
    {
        Assert.Equal(Playback.RefillKind.None, Decide(Context(2, 0), 0, repeat: RepeatMode.Track));
        Assert.Equal(Playback.RefillKind.None, Decide(Context(100, 90), 90, morePages: true, repeat: RepeatMode.Track));
        Assert.Equal(Playback.RefillKind.AskAutoplay, Decide(Context(2, 0), 0, repeat: RepeatMode.Context));
    }

    [Theory]
    [InlineData(Playback.AutoplayPhase.Requested)]
    [InlineData(Playback.AutoplayPhase.Waiting)]
    [InlineData(Playback.AutoplayPhase.Deferred)]
    [InlineData(Playback.AutoplayPhase.Exhausted)]
    public void An_ask_that_is_out_or_answered_for_good_blocks_another(Playback.AutoplayPhase phase)
    {
        Assert.Equal(Playback.RefillKind.None, Decide(Context(2, 0), 0, phase));
        Assert.Equal(Playback.RefillKind.None, Decide(Context(100, 90), 90, phase, morePages: true));
        Assert.Equal(Playback.RefillKind.None, Decide(AutoplayRun(8), 6, phase, autoplayPages: true));
    }

    [Fact]
    public void History_rows_count_as_consumed()
    {
        var rows = Context(8, 6);                                     // six played, the deck row, one ahead
        Playback.Refill.Run(rows, 6, QueueProvider.Context, out int total, out int ahead);
        Assert.Equal(8, total);
        Assert.Equal(1, ahead);
        Assert.Equal(Playback.RefillKind.PageContext, Decide(rows, 6, morePages: true));
    }

    [Fact]
    public void A_cursor_off_the_queue_asks_nothing()
    {
        Assert.Equal(Playback.RefillKind.None, Decide(Context(2, 0), -1));
        Assert.Equal(Playback.RefillKind.None, Decide(Context(2, 0), 2));
        Assert.Equal(Playback.RefillKind.None, Decide([], 0));
    }
}
