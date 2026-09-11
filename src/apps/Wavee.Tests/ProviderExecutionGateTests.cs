using Wavee.Backend.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class ProviderExecutionGateTests
{
    [Fact]
    public async Task ReadyWakesAllMatchingWaiters()
    {
        var gate = new ProviderExecutionGate(7, ProviderExecutionState.Initializing);
        var first = gate.WaitAsync(7, default).AsTask();
        var second = gate.WaitAsync(7, default).AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        gate.Set(7, ProviderExecutionState.Ready);
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SupersessionAndOfflineCompleteWithoutAdmittingAnOldEpoch()
    {
        var gate = new ProviderExecutionGate(7, ProviderExecutionState.Initializing);
        var old = gate.WaitAsync(7, default).AsTask();
        gate.Set(8, ProviderExecutionState.Initializing);
        Assert.False(await old.WaitAsync(TimeSpan.FromSeconds(5)));
        gate.Set(7, ProviderExecutionState.Ready);
        var current = gate.WaitAsync(8, default).AsTask();
        Assert.False(current.IsCompleted);
        gate.Set(8, ProviderExecutionState.Offline);
        Assert.False(await current.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelTheGate()
    {
        var gate = new ProviderExecutionGate(1, ProviderExecutionState.Initializing);
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(1, cancellation.Token).AsTask();
        var retained = gate.WaitAsync(1, default).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.Set(1, ProviderExecutionState.Ready);
        Assert.True(await retained.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
