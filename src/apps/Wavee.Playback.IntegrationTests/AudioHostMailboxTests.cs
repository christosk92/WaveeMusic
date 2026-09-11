using System.Collections.Concurrent;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Playback.IntegrationTests;

public sealed class AudioHostMailboxTests
{
    [Fact]
    public async Task BlockedOperationYieldsOwnerAndContinuationReturnsToOwner()
    {
        var errors = new ConcurrentQueue<Exception>();
        await using var mailbox = new AudioHostMailbox(errors.Enqueue);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        SynchronizationContext? before = null, after = null;
        Task blocked = mailbox.InvokeAsync(async () =>
        {
            before = SynchronizationContext.Current;
            order.Add(1);
            entered.SetResult();
            await release.Task;
            after = SynchronizationContext.Current;
            order.Add(3);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await mailbox.InvokeAsync(() => { order.Add(2); return Task.CompletedTask; })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blocked.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(mailbox, before);
        Assert.Same(mailbox, after);
        Assert.Equal(new[] { 1, 2, 3 }, order);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task OperationFailureDoesNotBreakLaterCommands()
    {
        var errors = new ConcurrentQueue<Exception>();
        await using var mailbox = new AudioHostMailbox(errors.Enqueue);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mailbox.InvokeAsync(
            () => Task.FromException(new InvalidOperationException("fixture failure"))));
        int applied = 0;
        await mailbox.InvokeAsync(() => { applied++; return Task.CompletedTask; });
        Assert.Equal(1, applied);
        Assert.Empty(errors);
    }
}
