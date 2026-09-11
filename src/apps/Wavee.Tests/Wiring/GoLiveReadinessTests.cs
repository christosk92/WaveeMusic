using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Wiring;
using Xunit;

namespace Wavee.Tests;

public sealed class GoLiveReadinessTests
{
    [Fact]
    public async Task CancellingLoginReleasesItsChannelWithoutCancellingSharedInitialization()
    {
        var initialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var attempt = new CancellationTokenSource();
        bool abandoned = false;
        var wait = GoLiveReadiness.WaitAsync(initialization.Task, attempt.Token, () => abandoned = true);
        Assert.False(wait.IsCompleted);
        attempt.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.True(abandoned);
        Assert.False(initialization.Task.IsCompleted);
        initialization.SetResult();
        await initialization.Task;
    }

    [Fact]
    public async Task FailedInitializationReleasesItsChannelAndPreservesTheFailure()
    {
        var failure = new InvalidOperationException("Storage migration failed");
        bool abandoned = false;
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => GoLiveReadiness.WaitAsync(
            Task.FromException(failure), CancellationToken.None, () => abandoned = true));
        Assert.Same(failure, observed);
        Assert.True(abandoned);
    }

    [Fact]
    public async Task ReadyStorageKeepsTheChannelForTheLiveStack()
    {
        bool abandoned = false;
        await GoLiveReadiness.WaitAsync(Task.CompletedTask, CancellationToken.None, () => abandoned = true);
        Assert.False(abandoned);
    }
}
