using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

public class TrailingCoalescerTests
{
    sealed class Harness
    {
        public long Now;
        public readonly List<int> Runs = new();
        TaskCompletionSource? _delayGate;
        public readonly TrailingCoalescer Coalescer;

        public Harness(int windowMs = 400)
        {
            Coalescer = new TrailingCoalescer(windowMs, () => Now, DelayAsync);
        }

        Task DelayAsync(int _, CancellationToken ct)
        {
            _delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _delayGate.Task.WaitAsync(ct);
        }

        public void Post(int id) => Coalescer.Post(() => Runs.Add(id));

        public Task CompleteDelayAsync() => _delayGate?.TrySetResult() is true ? Task.CompletedTask : Task.CompletedTask;
    }

    [Fact]
    public void FirstPost_RunsSynchronously()
    {
        var h = new Harness();
        h.Post(1);
        Assert.Equal([1], h.Runs);
    }

    [Fact]
    public async Task RapidPosts_LeadOnce_TrailWithLatest()
    {
        var h = new Harness();
        h.Now = 1000;
        h.Post(1);
        h.Now = 1010;
        h.Post(2);
        h.Now = 1020;
        h.Post(3);

        Assert.Equal([1], h.Runs);
        await h.CompleteDelayAsync();
        await Task.Delay(20);
        Assert.Equal([1, 3], h.Runs);
    }

    [Fact]
    public async Task PostAfterQuietWindow_LeadsAgain()
    {
        var h = new Harness();
        h.Now = 0;
        h.Post(1);
        await h.CompleteDelayAsync();
        await Task.Delay(20);

        h.Now = 500;
        h.Post(2);
        Assert.Equal([1, 2], h.Runs);
    }

    [Fact]
    public async Task LongBurst_BoundedExecutionsPerWindow()
    {
        var h = new Harness(100);
        h.Now = 0;
        for (int i = 0; i < 20; i++)
        {
            h.Now = i * 10;
            h.Post(i);
        }

        int leading = h.Runs.Count;
        Assert.Equal(1, leading);

        await h.CompleteDelayAsync();
        await Task.Delay(20);
        Assert.Equal(2, h.Runs.Count);
        Assert.Equal(19, h.Runs[^1]);
    }

    [Fact]
    public async Task Dispose_CancelsPendingTrailing()
    {
        var h = new Harness();
        h.Now = 1000;
        h.Post(1);
        h.Now = 1010;
        h.Post(2);
        h.Coalescer.Dispose();
        await h.CompleteDelayAsync();
        await Task.Delay(20);
        Assert.Equal([1], h.Runs);
    }
}
