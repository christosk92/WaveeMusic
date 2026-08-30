using System;
using System.Linq;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Phase B: the pure QueueCore widening (head-insert / batch / replace-up-next / move / remove). Deterministic, no I/O.
public class QueueCorePhaseBTests
{
    static QueuedTrack T(string id) =>
        new(new Track(id, "spotify:track:" + id, id, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null), "uid" + id);

    static string[] UserQueue(QueueCore q) =>
        q.Snapshot().Where(e => e.Bucket == QueueBucket.UserQueue).Select(e => e.Track.Id).ToArray();

    static string[] NextUp(QueueCore q) =>
        q.Snapshot().Where(e => e.Bucket == QueueBucket.NextUp).Select(e => e.Track.Id).ToArray();

    [Fact]
    public void EnqueueNext_InsertsAheadOfUserQueue()
    {
        var q = new QueueCore();
        q.SetContext("ctx", new[] { T("a"), T("b") }, 0);
        q.EnqueueUser(T("u1"));
        q.EnqueueNext(T("u0"));   // head-insert → before u1
        Assert.Equal(new[] { "u0", "u1" }, UserQueue(q));
    }

    [Fact]
    public void EnqueueRange_AppendsAllInOrder()
    {
        var q = new QueueCore();
        q.SetContext("ctx", new[] { T("a") }, 0);
        q.EnqueueRange(new[] { T("u0"), T("u1"), T("u2") });
        Assert.Equal(new[] { "u0", "u1", "u2" }, UserQueue(q));
    }

    [Fact]
    public void ReplaceNextUp_ReplacesTheUserQueue()
    {
        var q = new QueueCore();
        q.SetContext("ctx", new[] { T("a") }, 0);
        q.EnqueueUser(T("old"));
        q.ReplaceNextUp(new[] { T("n1"), T("n2") });
        Assert.Equal(new[] { "n1", "n2" }, UserQueue(q));
    }

    [Fact]
    public void Remove_UserQueueItem_ByQId()
    {
        var q = new QueueCore();
        q.SetContext("ctx", new[] { T("a") }, 0);
        q.EnqueueRange(new[] { T("u0"), T("u1"), T("u2") });
        Assert.True(q.Remove("q1"));            // remove u1
        Assert.Equal(new[] { "u0", "u2" }, UserQueue(q));
    }

    [Fact]
    public void Remove_UpcomingContextTrack_ByCId()
    {
        var q = new QueueCore();
        q.SetContext("ctx", new[] { T("a"), T("b"), T("c") }, 0);   // current a; next-up c1=b, c2=c
        Assert.True(q.Remove("c1"));            // remove b
        Assert.Equal(new[] { "c" }, NextUp(q));
    }

    [Fact]
    public void Move_ReordersWithinUserQueue()
    {
        var q = new QueueCore();
        q.SetContext("ctx", new[] { T("a") }, 0);
        q.EnqueueRange(new[] { T("u0"), T("u1"), T("u2") });
        Assert.True(q.Move("q0", 2));           // u0 → end
        Assert.Equal(new[] { "u1", "u2", "u0" }, UserQueue(q));
    }

    [Fact]
    public void Remove_UnknownEntry_ReturnsFalse()
    {
        var q = new QueueCore();
        q.SetContext("ctx", new[] { T("a") }, 0);
        Assert.False(q.Remove("q9"));
        Assert.False(q.Move("nope", 0));
    }
}
