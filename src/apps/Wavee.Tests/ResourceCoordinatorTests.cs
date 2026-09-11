using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class ResourceCoordinatorTests
{
    [Fact]
    public async Task InitializingProtocolDoesNotAdmitNetworkOrBlockLocalWork()
    {
        await using var fixture = new CatalogFixture();
        var execution = new ProviderExecutionGate(fixture.Repository.Epoch, ProviderExecutionState.Initializing);
        var remote = new TestProvider((requests, _) => Task.FromResult(Present(requests)));
        var local = new TestProvider((requests, _) => Task.FromResult(Present(requests)), "local", network: false);
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [remote, local], fixture.Clock, execution);
        var networkKey = fixture.Key();
        var localKey = networkKey with { Scope = fixture.Scope with { Provider = "local" } };
        var read = coordinator.EnsureAsync([networkKey, localKey]);
        await UntilAsync(() => local.Calls == 1);
        Assert.False(read.IsCompleted);
        Assert.Equal(0, remote.Calls);
        Assert.Equal(ResourceActivity.Idle, fixture.Repository.Peek(networkKey).Activity);
        execution.Set(fixture.Repository.Epoch, ProviderExecutionState.Ready);
        Assert.All(await read.WaitAsync(TimeSpan.FromSeconds(5)), result => Assert.Equal(ResourceEnsureStatus.Ready, result.Status));
        Assert.Equal(1, remote.Calls);
    }

    [Fact]
    public async Task StartupCancellationNeverConsumesAnAttempt()
    {
        await using var fixture = new CatalogFixture();
        var execution = new ProviderExecutionGate(fixture.Repository.Epoch, ProviderExecutionState.Initializing);
        var provider = new TestProvider((requests, _) => Task.FromResult(Present(requests)));
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock, execution);
        using var cancellation = new CancellationTokenSource();
        var read = coordinator.EnsureAsync([fixture.Key()], ct: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        execution.Set(fixture.Repository.Epoch, ProviderExecutionState.Ready);
        Assert.Equal(ResourceEnsureStatus.Ready, Assert.Single(await coordinator.EnsureAsync([fixture.Key()])).Status);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task DisjointKeyEnsure_DoesNotHoldAdmissionAcrossThePersistenceRead()
    {
        // Finding #11: `_admission` now covers only the in-memory `_jobs` decide-and-admit step, not the
        // persistence reads around it. A caller stuck on a slow cold read must leave the permit free the whole
        // time, and a disjoint-key caller must be able to start (and issue its own read) without waiting for it.
        await using var fixture = new CatalogFixture();
        var keyA = fixture.Key("a");
        var keyB = fixture.Key("b");
        var readEntered = NewSignal();
        var readGate = NewSignal();
        fixture.Persistence.BeforeRead = async keys =>
        {
            if (!keys.Contains(keyA)) return;
            readEntered.TrySetResult();
            await readGate.Task;
        };
        var provider = new TestProvider((requests, _) => Task.FromResult(Present(requests)));
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);

        var first = coordinator.EnsureAsync([keyA]);
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var admission = (SemaphoreSlim)typeof(ResourceCoordinator)
            .GetField("_admission", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(coordinator)!;
        Assert.Equal(1, admission.CurrentCount);

        // Must not block trying to acquire a permit A is holding — because A no longer holds one here.
        var second = coordinator.EnsureAsync([keyB]);

        readGate.SetResult();
        Assert.Equal(ResourceEnsureStatus.Ready, Assert.Single(await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(ResourceEnsureStatus.Ready, Assert.Single(await second.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task OverlappingWaiters_ShareTransportAndCancellationOnlyDetachesOne()
    {
        await using var fixture = new CatalogFixture();
        var entered = NewSignal();
        var release = NewSignal();
        var provider = new TestProvider(async (requests, _) =>
        { entered.TrySetResult(); await release.Task; return Present(requests); });
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        using var caller = new CancellationTokenSource();
        var first = coordinator.EnsureAsync([fixture.Key()], ct: caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.EnsureAsync([fixture.Key()]);
        await fixture.Commits.FlushAsync();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.Equal(ResourceEnsureStatus.Ready, Assert.Single(await second.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Force_SupersedesRunningRequestAndRejectsItsLateValue()
    {
        await using var fixture = new CatalogFixture();
        var entered = NewSignal();
        var release = NewSignal();
        int call = 0;
        var provider = new TestProvider(async (requests, _) =>
        {
            int number = Interlocked.Increment(ref call);
            if (number == 1) { entered.TrySetResult(); await release.Task; }
            return Present(requests, number);
        });
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var first = coordinator.EnsureAsync([fixture.Key()]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forced = await coordinator.EnsureAsync([fixture.Key()], force: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ResourceEnsureStatus.Ready, Assert.Single(forced).Status);
        Assert.Equal(ResourceEnsureStatus.Superseded, Assert.Single(await first).Status);
        release.SetResult();
        await UntilAsync(() => provider.Active == 0);
        await fixture.Commits.FlushAsync();
        Assert.Equal(2, Assert.IsType<PlayCountValue>(fixture.Repository.Peek(fixture.Key()).Value).Count);
    }

    [Fact]
    public async Task MoreThanOneBatch_UsesExactlyFourWorkersAndAtMost300UrisPerRequest()
    {
        await using var fixture = new CatalogFixture();
        var release = NewSignal();
        var provider = new TestProvider(async (requests, _) =>
        {
            Assert.InRange(requests.Select(request => request.Key.Subject).Distinct().Count(), 1, 300);
            await release.Task;
            return Present(requests);
        });
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        // 5 batches (300*4 + 1) so the 4 workers are all busy at once with a 5th batch still queued.
        var keys = Enumerable.Range(0, 1201).Select(i => fixture.Key("track" + i)).ToArray();
        var ensure = coordinator.EnsureAsync(keys);
        await UntilAsync(() => provider.Active == 4);
        Assert.Equal(4, provider.MaximumActive);
        release.SetResult();
        var results = await ensure.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, result => Assert.Equal(ResourceEnsureStatus.Ready, result.Status));
        Assert.Equal(5, provider.Calls);
        Assert.Equal(4, provider.MaximumActive);
    }

    [Fact]
    public async Task OneBatchGroup_WithDifferentSubjects_ArrivesInASingleFetchCall()
    {
        await using var fixture = new CatalogFixture();
        var release = NewSignal();
        var receivedSource = new TaskCompletionSource<IReadOnlyList<ResourceRequest>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TestProvider(async (requests, _) =>
        { receivedSource.TrySetResult(requests); await release.Task; return Present(requests); }, batchGroup: _ => "shared");
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var keys = new[] { fixture.Key("a"), fixture.Key("b"), fixture.Key("c"), fixture.Key("d") };
        var ensure = coordinator.EnsureAsync(keys);
        var received = await receivedSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, received.Select(request => request.Key.Subject).Distinct().Count());
        Assert.Equal(1, provider.Calls);
        release.SetResult();
        var results = await ensure.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal(ResourceEnsureStatus.Ready, result.Status));
    }

    [Fact]
    public async Task FourDistinctBatchGroups_RunConcurrentlyAcrossAllFourWorkers()
    {
        await using var fixture = new CatalogFixture();
        var release = NewSignal();
        var provider = new TestProvider(async (requests, _) => { await release.Task; return Present(requests); },
            batchGroup: key => key.Subject);
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var keys = new[] { fixture.Key("a"), fixture.Key("b"), fixture.Key("c"), fixture.Key("d") };
        var ensure = coordinator.EnsureAsync(keys);
        await UntilAsync(() => coordinator.Running == 4);
        Assert.Equal(4, provider.Calls);
        release.SetResult();
        var results = await ensure.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal(ResourceEnsureStatus.Ready, result.Status));
    }

    [Fact]
    public async Task TransientRetry_IsFiniteAndARepeatedEnsureDoesNotRestartExhaustedWork()
    {
        await using var fixture = new CatalogFixture();
        var provider = new TestProvider((requests, _) => Task.FromResult<IReadOnlyList<ResourceResponse>>(
            requests.Select(request => new ResourceResponse(request,
                ResourceFetchResult.Failed(new(ResourceErrorKind.Transport, "503", 503)))).ToArray()));
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var key = fixture.Key();
        var ensure = coordinator.EnsureAsync([key]);
        foreach (int seconds in new[] { 2, 10, 30 })
        {
            await UntilAsync(() => fixture.Repository.Peek(key).Activity == ResourceActivity.Backoff);
            fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
            // Wait until the next scheduled deadline or terminal result, not a real retry delay.
            var expectedCalls = seconds == 2 ? 2 : seconds == 10 ? 3 : 4;
            await UntilAsync(() => provider.Calls == expectedCalls);
        }
        Assert.Equal(ResourceEnsureStatus.Failed, Assert.Single(await ensure.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(ResourceEnsureStatus.Failed, Assert.Single(await coordinator.EnsureAsync([key])).Status);
        Assert.Equal(4, provider.Calls);
    }

    [Fact]
    public async Task RateLimit_IsNotRetriedOutsideHttpMiddleware()
    {
        await using var fixture = new CatalogFixture();
        var provider = new TestProvider((requests, _) => Task.FromResult<IReadOnlyList<ResourceResponse>>(
            requests.Select(request => new ResourceResponse(request,
                ResourceFetchResult.Failed(new(ResourceErrorKind.Transport, "429", 429), TimeSpan.FromSeconds(1)))).ToArray()));
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        Assert.Equal(ResourceEnsureStatus.Failed, Assert.Single(await coordinator.EnsureAsync([fixture.Key()])
            .WaitAsync(TimeSpan.FromSeconds(5))).Status);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task CancelledLastDemand_DoesNotScheduleAnotherRetry()
    {
        await using var fixture = new CatalogFixture();
        var provider = new TestProvider((requests, _) => Task.FromResult<IReadOnlyList<ResourceResponse>>(
            requests.Select(request => new ResourceResponse(request,
                ResourceFetchResult.Failed(new(ResourceErrorKind.Transport, "timeout")))).ToArray()));
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        using var demand = new CancellationTokenSource();
        var ensure = coordinator.EnsureAsync([fixture.Key()], ct: demand.Token);
        await UntilAsync(() => fixture.Repository.Peek(fixture.Key()).Activity == ResourceActivity.Backoff);
        demand.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ensure);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, coordinator.Pending);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task StaleGenerationSupersede_ClearsTheEntrysActivityBackToIdle()
    {
        await using var fixture = new CatalogFixture();
        var release = NewSignal();
        // Distinct batch groups per subject: each of the four "busy" keys occupies its own worker, so `key`'s
        // own job is admitted (Activity → Queued) but never picked up while all four stay blocked.
        var provider = new TestProvider(async (requests, _) => { await release.Task; return Present(requests); },
            batchGroup: k => k.Subject);
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var busy = coordinator.EnsureAsync(Enumerable.Range(0, 4).Select(i => fixture.Key("busy" + i)).ToArray());
        await UntilAsync(() => coordinator.Running == 4);
        var key = fixture.Key();
        var ensure = coordinator.EnsureAsync([key]);
        await UntilAsync(() => fixture.Repository.Peek(key).Activity == ResourceActivity.Queued);
        // A sibling caller captures the SAME key directly against the repository — bypassing the coordinator
        // entirely, exactly like an unrelated write racing it — which bumps its RequestId without touching
        // Activity. TakeBatchLocked's stale-generation check (Part 5 item 3e) will complete the coordinator's
        // own job as Superseded once a worker frees up; nothing else in the repository ever resets its activity.
        await fixture.Repository.CaptureRequestAsync(key, ResourcePriority.Visible);
        release.SetResult();
        Assert.Equal(ResourceEnsureStatus.Superseded, Assert.Single(await ensure.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await UntilAsync(() => fixture.Repository.Peek(key).Activity == ResourceActivity.Idle);
        await busy.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OfflineLocalSource_UsesSameCoordinatorWithoutNetworkAccess()
    {
        await using var fixture = new CatalogFixture();
        await fixture.Repository.SetSessionAsync(fixture.Scope, fixture.Scope.ProviderAccount, online: false);
        var provider = new TestProvider((requests, _) => Task.FromResult(Present(requests)), "local", network: false);
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var local = fixture.Key() with { Scope = fixture.Scope with { Provider = "local" } };
        Assert.Equal(ResourceEnsureStatus.Ready, Assert.Single(await coordinator.EnsureAsync([local])).Status);
        Assert.Equal(1, provider.Calls);
    }

    static IReadOnlyList<ResourceResponse> Present(IReadOnlyList<ResourceRequest> requests, long count = 0)
        => requests.Select(request => new ResourceResponse(request,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new PlayCountValue(count))))).ToArray();
    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, deadline.Token);
    }

    sealed class TestProvider(Func<IReadOnlyList<ResourceRequest>, CancellationToken, Task<IReadOnlyList<ResourceResponse>>> fetch,
        string provider = "spotify", bool network = true, Func<ResourceKey, string>? batchGroup = null) : ICatalogResourceProvider
    {
        int _calls, _active, _maximum;
        public string Provider { get; } = provider;
        public int Calls => Volatile.Read(ref _calls);
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximum);
        public bool RequiresNetwork(ResourceKey key) => network;
        public string BatchGroup(ResourceKey key) => batchGroup?.Invoke(key) ?? "metadata";
        public async ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _maximum); }
            while (previous < active && Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
            try { return await fetch(requests, ct); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
