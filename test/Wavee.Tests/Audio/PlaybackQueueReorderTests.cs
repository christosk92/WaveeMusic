using System.Linq;
using FluentAssertions;
using Wavee.Audio.Queue;
using Xunit;

namespace Wavee.Tests.Audio;

/// <summary>
/// Tests for <see cref="PlaybackQueue.ReorderWithinBucket"/> — the drag-reorder
/// mutation. Covers each bucket, the shuffle-aware context-tail path, the
/// played/current-track safety bound, the no-op guard, and UID preservation.
/// </summary>
public class PlaybackQueueReorderTests
{
    private static QueueTrack Track(string id) => new($"spotify:track:{id}");

    [Fact]
    public void UserQueue_MovesItem()
    {
        var q = new PlaybackQueue();
        // PlayNext head-inserts → user queue ends up [c, b, a].
        q.PlayNext(Track("a"));
        q.PlayNext(Track("b"));
        q.PlayNext(Track("c"));

        q.ReorderWithinBucket(QueueReorderTarget.UserQueue, 0, 2).Should().BeTrue();

        q.GetSnapshot().UserQueueTracks.Select(t => t.Uri)
            .Should().Equal("spotify:track:b", "spotify:track:a", "spotify:track:c");
    }

    [Fact]
    public void UserQueue_PreservesUidsOnMove()
    {
        var q = new PlaybackQueue();
        q.PlayNext(Track("a"));
        q.PlayNext(Track("b"));
        var beforeUids = q.GetSnapshot().UserQueueTracks.Select(t => t.Uid).ToList();

        q.ReorderWithinBucket(QueueReorderTarget.UserQueue, 0, 1).Should().BeTrue();

        // Same UIDs survive — only the order changed.
        q.GetSnapshot().UserQueueTracks.Select(t => t.Uid)
            .Should().BeEquivalentTo(beforeUids);
    }

    [Fact]
    public void NoOp_WhenSourceEqualsDestination()
    {
        var q = new PlaybackQueue();
        q.PlayNext(Track("a"));
        q.PlayNext(Track("b"));

        q.ReorderWithinBucket(QueueReorderTarget.UserQueue, 1, 1).Should().BeFalse();
    }

    [Fact]
    public void ContextUpcoming_MovesWithinTail_ShuffleOff()
    {
        var q = new PlaybackQueue();
        q.SetTracks(Enumerable.Range(0, 6).Select(i => Track($"t{i}")), startIndex: 0);
        // current = t0; upcoming tail = t1..t5 (tail positions 0..4).

        q.ReorderWithinBucket(QueueReorderTarget.ContextUpcoming, 0, 2).Should().BeTrue();

        // t1 (tail pos 0) moved to tail pos 2.
        q.GetNextTracks().Select(t => t.Uri)
            .Should().Equal("spotify:track:t2", "spotify:track:t3", "spotify:track:t1",
                            "spotify:track:t4", "spotify:track:t5");
    }

    [Fact]
    public void ContextUpcoming_MovesVisibleRow_ShuffleOn()
    {
        var q = new PlaybackQueue();
        q.SetTracks(Enumerable.Range(0, 6).Select(i => Track($"t{i}")), startIndex: 0);
        q.SetShuffle(true);

        var before = q.GetNextTracks().Select(t => t.Uri).ToList();
        before.Should().HaveCount(5);

        q.ReorderWithinBucket(QueueReorderTarget.ContextUpcoming, 0, 2).Should().BeTrue();

        // The row at tail pos 0 moved to pos 2; the others keep relative order.
        q.GetNextTracks().Select(t => t.Uri)
            .Should().Equal(before[1], before[2], before[0], before[3], before[4]);
    }

    [Fact]
    public void ContextUpcoming_RejectsOutOfRange()
    {
        var q = new PlaybackQueue();
        q.SetTracks(Enumerable.Range(0, 4).Select(i => Track($"t{i}")), startIndex: 0);

        // tail only has positions 0..2 (t1..t3); 99 is out of range.
        q.ReorderWithinBucket(QueueReorderTarget.ContextUpcoming, 0, 99).Should().BeFalse();
    }

    [Fact]
    public void RejectsMoveOnEmptyBucket()
    {
        var q = new PlaybackQueue();
        q.ReorderWithinBucket(QueueReorderTarget.UserQueue, 0, 1).Should().BeFalse();
        q.ReorderWithinBucket(QueueReorderTarget.PostContextQueue, 0, 1).Should().BeFalse();
    }
}
