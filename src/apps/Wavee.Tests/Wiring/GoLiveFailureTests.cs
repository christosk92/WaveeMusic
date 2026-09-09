using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Wiring;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public sealed class GoLiveFailureTests
{
    [Fact]
    public async Task FailureIsVisibleWhilePartialSessionTeardownIsStillBlocked()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Progress();
        var error = new InvalidOperationException("Catalog initialization failed");
        Exception? received = null;
        var cleanup = GoLiveFailure.ReportAndRollbackAsync(error, progress, "Could not initialize", true,
            CancellationToken.None, async failure => { received = failure; await release.Task; });
        try
        {
            Assert.False(cleanup.IsCompleted);
            Assert.Same(error, received);
            Assert.Equal(LoginPhase.Failed, Assert.Single(progress.States).Phase);
            Assert.Equal("Could not initialize", progress.States[0].Error);
        }
        finally { release.TrySetResult(); }
        await cleanup;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CancelledOrQuietSiblingStillCleansUpWithoutReplacingTheVisibleLogin(bool cancelled, bool quiet)
    {
        var progress = new Progress();
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        bool cleaned = false;
        await GoLiveFailure.ReportAndRollbackAsync(new InvalidOperationException(), progress, "Failure", !quiet,
            cancellation.Token, _ => { cleaned = true; return Task.CompletedTask; });
        Assert.True(cleaned);
        Assert.Empty(progress.States);
    }

    sealed class Progress : ILoginProgress
    {
        public List<LoginSnapshot> States { get; } = [];
        public void Report(LoginSnapshot snapshot) => States.Add(snapshot);
    }
}
