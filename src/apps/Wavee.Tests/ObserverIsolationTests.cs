using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Sync;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public sealed class ObserverIsolationTests
{
    [Fact]
    public void ReplayedStateAndFutureValuesSurviveAThrowingSubscriber()
    {
        var subject = new SimpleSubject<int>(1);
        using var broken = subject.Subscribe(Observers.From<int>(_ => throw new InvalidOperationException("observer")));
        var seen = new List<int>();
        using var healthy = subject.Subscribe(Observers.From<int>(seen.Add));
        subject.OnNext(2);
        Assert.Equal(new[] { 1, 2 }, seen);
        Assert.Equal(2, subject.Current);
    }

    [Fact]
    public async Task LocalSavePersistsAndPublishesToHealthyObserversEvenWhenTheFirstObserverThrows()
    {
        IReadOnlySet<string>? durable = null;
        var owner = new LocalMutationSource(persist: value => durable = value);
        using var broken = owner.SavedChanged.Subscribe(Observers.From<IReadOnlySet<string>>(_ => throw new InvalidOperationException("observer")));
        IReadOnlySet<string>? seen = null;
        using var healthy = owner.SavedChanged.Subscribe(Observers.From<IReadOnlySet<string>>(value => seen = value));
        await owner.SetSavedAsync("local:track:one", true, TestContext.Current.CancellationToken);
        Assert.True(owner.IsSaved("local:track:one"));
        Assert.Contains("local:track:one", durable!);
        Assert.Same(durable, seen);
    }

    [Fact]
    public async Task ReplicaChangeDeliveryContinuesAfterAThrowingObserverAndCommitStillSucceeds()
    {
        await using var host = new ReplicaTestHost();
        using var broken = host.Replicas.Changes.Subscribe(Observers.From<ReplicaChange>(_ => throw new InvalidOperationException("observer")));
        var seen = new List<ReplicaChange>();
        using var healthy = host.Replicas.Changes.Subscribe(Observers.From<ReplicaChange>(seen.Add));
        await host.Mutations.SaveAsync("liked", "spotify:track:one", true);
        Assert.True(host.Store.IsSaved("liked", "spotify:track:one"));
        Assert.NotEmpty(seen);
        Assert.Equal(1, host.Mutations.Pending);
    }
}
