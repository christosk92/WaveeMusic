using System.Collections.Generic;
using Wavee.Backend.Playlists;
using Xunit;

namespace Wavee.Tests;

// PlaylistResyncQueue's lifecycle (§2.2.1): Marked -> Revalidating -> resolved / Failed, with attempts counted and a
// Changed callback so the UI can subscribe. An injected clock keeps SinceUtcMs assertions exact.
public class PlaylistResyncQueueTests
{
    static PlaylistResyncQueue Queue(long now) => new(() => now);

    [Fact]
    public void Mark_Twice_KeepsTheOriginalStamp()
    {
        var q = Queue(1000);
        q.Mark("spotify:playlist:p");
        var first = q.Get("spotify:playlist:p");
        Assert.Equal(PlaylistResyncQueue.Phase.Marked, first.Phase);
        Assert.Equal(1000, first.SinceUtcMs);

        // A second Mark while still Marked must not reset the "how long has this been unresolved" clock.
        q.Mark("spotify:playlist:p");
        var second = q.Get("spotify:playlist:p");
        Assert.Equal(1000, second.SinceUtcMs);
        Assert.Equal(0, second.Attempts);
    }

    [Fact]
    public void TakeAll_ReturnsOnlyMarked_AndMovesThemToRevalidating()
    {
        var q = Queue(1000);
        q.Mark("spotify:playlist:a");
        q.Mark("spotify:playlist:b");

        var taken = q.TakeAll();
        Assert.Equal(2, taken.Count);
        Assert.Contains("spotify:playlist:a", taken);
        Assert.Contains("spotify:playlist:b", taken);
        Assert.Equal(PlaylistResyncQueue.Phase.Revalidating, q.Get("spotify:playlist:a").Phase);
        Assert.Equal(PlaylistResyncQueue.Phase.Revalidating, q.Get("spotify:playlist:b").Phase);

        // A second TakeAll is empty — nothing is Marked any more.
        Assert.Empty(q.TakeAll());
    }

    [Fact]
    public void Fail_IncrementsAttempts_AndPhasesFailed()
    {
        var q = Queue(1000);
        q.Mark("spotify:playlist:p");
        q.TakeAll();   // -> Revalidating

        int n1 = q.Fail("spotify:playlist:p");
        Assert.Equal(1, n1);
        Assert.Equal(PlaylistResyncQueue.Phase.Failed, q.Get("spotify:playlist:p").Phase);

        // Fail on an untracked uri is a no-op that reports 0 attempts.
        Assert.Equal(0, q.Fail("spotify:playlist:unknown"));
    }

    [Fact]
    public void TryBeginRetry_OnlyFromFailed()
    {
        var q = Queue(1000);
        q.Mark("spotify:playlist:p");

        Assert.False(q.TryBeginRetry("spotify:playlist:p"));   // still Marked, not Failed — no-op
        q.TakeAll();                                            // -> Revalidating
        Assert.False(q.TryBeginRetry("spotify:playlist:p"));   // Revalidating, not Failed — no-op
        q.Fail("spotify:playlist:p");                           // -> Failed

        Assert.True(q.TryBeginRetry("spotify:playlist:p"));
        Assert.Equal(PlaylistResyncQueue.Phase.Revalidating, q.Get("spotify:playlist:p").Phase);
    }

    [Fact]
    public void Resolve_Removes_AndFiresChanged()
    {
        var q = Queue(1000);
        var changed = new List<string>();
        q.Changed = uri => changed.Add(uri);

        q.Mark("spotify:playlist:p");
        changed.Clear();
        q.Resolve("spotify:playlist:p");

        Assert.Equal(PlaylistResyncQueue.Phase.None, q.Get("spotify:playlist:p").Phase);
        Assert.Equal(new[] { "spotify:playlist:p" }, changed);

        // Resolving an untracked uri is a no-op — no Changed fire.
        changed.Clear();
        q.Resolve("spotify:playlist:unknown");
        Assert.Empty(changed);
    }

    [Fact]
    public void Get_OnUnknownUri_IsNone()
    {
        var q = Queue(1000);
        Assert.Equal(PlaylistResyncQueue.Entry.None, q.Get("spotify:playlist:nope"));
    }
}
