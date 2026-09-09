using System.Collections.Concurrent;
using Xunit;

namespace Wavee.Tests;

public sealed class LatestProjectionTests
{
    [Fact]
    public async Task WorkIsSerializedAndSupersededResultsCannotPublish()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var posts = new BlockingCollection<Action>();
        var seen = new ConcurrentQueue<int>();
        var delivered = new List<int>();
        using var worker = new LatestProjection<int, int>(input =>
        {
            seen.Enqueue(input);
            if (input == 1) { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            return input;
        }, posts.Add, delivered.Add, error => throw error);
        worker.Submit(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Submit(2);
        worker.Submit(3);
        release.Set();
        Assert.True(posts.TryTake(out var deliver, TimeSpan.FromSeconds(5)));
        deliver();
        Assert.Equal([1, 3], seen.ToArray());
        Assert.Equal([3], delivered);
    }

    [Fact]
    public void AlreadyQueuedDeliveryIsInvalidatedByNewInputAndDispose()
    {
        using var posts = new BlockingCollection<Action>();
        var delivered = new List<int>();
        var worker = new LatestProjection<int, int>(input => input, posts.Add, delivered.Add, error => throw error);
        worker.Submit(1);
        Assert.True(posts.TryTake(out var old, TimeSpan.FromSeconds(5)));
        worker.Submit(2);
        old();
        Assert.Empty(delivered);
        Assert.True(posts.TryTake(out var current, TimeSpan.FromSeconds(5)));
        worker.Dispose();
        current();
        Assert.Empty(delivered);
    }
}
