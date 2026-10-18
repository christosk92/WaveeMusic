// ── Wavee.Tests/QueueHistoryTests.cs — "played before this" (contract 1.46) ───────────
// Pure: the history projection takes the play log's entries and a row resolver as arguments; the interested set is a string blob.

using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class QueueHistoryProjectionTests
{
    static Shell.PlayEntry Play(string uri, long at, string context = "") =>
        new(EntityUri.Parse(uri), context.Length == 0 ? default : EntityUri.Parse(context), at, null);

    static EntityRef Resolve(string uri) => uri.Contains("episode", StringComparison.Ordinal)
        ? new EntityRef(EntityKind.Episode, 7)
        : uri.Contains("track", StringComparison.Ordinal) ? new EntityRef(EntityKind.Track, 1 + uri.GetHashCode() % 1000 & 0x3FF | 1) : default;

    [Fact]
    public void Newest_first_and_the_decks_own_start_is_skipped()
    {
        var log = new[] { Play("spotify:track:aaaaaaaaaaaaaaaaaaaaa1", 1), Play("spotify:track:aaaaaaaaaaaaaaaaaaaaa2", 2), Play("spotify:track:aaaaaaaaaaaaaaaaaaaaa3", 3) };
        var now = Resolve("spotify:track:aaaaaaaaaaaaaaaaaaaaa3");
        var rows = Shell.QueueModel.History(log, now, Resolve);
        Assert.Equal(2, rows.Length);
        Assert.Equal("spotify:track:aaaaaaaaaaaaaaaaaaaaa2", rows[0].Uri);
        Assert.Equal(2L, rows[0].PlayedAtMs);
        Assert.Equal("spotify:track:aaaaaaaaaaaaaaaaaaaaa1", rows[1].Uri);
    }

    [Fact]
    public void A_track_appears_once_with_its_latest_play_and_the_context_travels()
    {
        var log = new[]
        {
            Play("spotify:track:aaaaaaaaaaaaaaaaaaaaa1", 1, "spotify:album:bbbbbbbbbbbbbbbbbbbbb1"),
            Play("spotify:track:aaaaaaaaaaaaaaaaaaaaa2", 2),
            Play("spotify:track:aaaaaaaaaaaaaaaaaaaaa1", 3, "spotify:playlist:ccccccccccccccccccccc1"),
        };
        var rows = Shell.QueueModel.History(log, default, Resolve);
        Assert.Equal(2, rows.Length);
        Assert.Equal(3L, rows[0].PlayedAtMs);
        Assert.Equal("spotify:playlist:ccccccccccccccccccccc1", rows[0].Context);
        Assert.Equal("", rows[1].Context);
    }

    [Fact]
    public void The_cap_and_non_playable_rows_bound_the_list()
    {
        var log = new List<Shell.PlayEntry>();
        for (int i = 0; i < 80; i++) log.Add(Play("spotify:track:" + i.ToString("D22"), i + 1));
        log.Add(Play("spotify:artist:ddddddddddddddddddddd1", 100));   // not a track: left out
        var rows = Shell.QueueModel.History(log, default, Resolve);
        Assert.Equal(Shell.QueueModel.HistoryCap, rows.Length);
        Assert.Equal(80L, rows[0].PlayedAtMs);
        Assert.Empty(Shell.QueueModel.History([], default, Resolve));
    }
}
