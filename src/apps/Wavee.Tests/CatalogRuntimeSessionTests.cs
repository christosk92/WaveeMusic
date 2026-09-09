using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogRuntimeSessionTests
{
    static CatalogRuntime Runtime()
    {
        var persistence = new MemoryDataPersistence();
        return new(new("spotify", "bob", "en", "US", "premium", 1, false), "bob", persistence, persistence,
            new MemoryReplicaProjection(new InMemoryStore()), []);
    }

    [Fact]
    public async Task OldLogoutAndLeaseUndoCannotClearTheSuccessor()
    {
        await using var runtime = Runtime();
        await using var first = new SyncHarness(_ => throw new InvalidOperationException("No network expected"));
        await using var second = new SyncHarness(_ => throw new InvalidOperationException("No network expected"));
        long firstEpoch = runtime.Catalog.Epoch;
        Assert.True(await runtime.SetProtocolSessionAsync(first.Sync, firstEpoch));
        long nextEpoch = await runtime.SetSessionAsync(runtime.Catalog.Scope, "bob", true);
        Assert.True(await runtime.SetProtocolSessionAsync(second.Sync, nextEpoch));
        Assert.False(await runtime.EndSessionAsync(firstEpoch));
        Assert.False(await runtime.ClearProtocolSessionAsync(first.Sync, firstEpoch));
        Assert.False(await runtime.SetProtocolSessionAsync(first.Sync, firstEpoch));
        Assert.Same(second.Sync, runtime.LiveSync);
        Assert.True(runtime.Catalog.IsOnline);
        Assert.Equal(nextEpoch, runtime.Catalog.Epoch);
    }

    [Fact]
    public async Task SameEpochReplacementStillRequiresTheExactInstalledSyncForUndo()
    {
        await using var runtime = Runtime();
        await using var first = new SyncHarness(_ => throw new InvalidOperationException("No network expected"));
        await using var second = new SyncHarness(_ => throw new InvalidOperationException("No network expected"));
        long epoch = runtime.Catalog.Epoch;
        Assert.True(await runtime.SetProtocolSessionAsync(first.Sync, epoch));
        Assert.True(await runtime.SetProtocolSessionAsync(second.Sync, epoch));
        Assert.False(await runtime.ClearProtocolSessionAsync(first.Sync, epoch));
        Assert.Same(second.Sync, runtime.LiveSync);
        Assert.True(await runtime.ClearProtocolSessionAsync(second.Sync, epoch));
        Assert.Null(runtime.LiveSync);
    }

    [Fact]
    public async Task SessionPublicationCarriesTheNewScopeAtTheMomentItFires()
    {
        // Findings 4.2: Services' UI scope publisher reads Data.Catalog.Scope from INSIDE the Session-kind
        // observer, not a scope captured before the install started. This pins the repository-side half of that
        // contract: by the time Changes fires Session, the repository already reports the new scope.
        await using var runtime = Runtime();
        var seen = new List<CatalogScope>();
        using var watch = runtime.Catalog.Changes.Subscribe(Observers.From<CatalogChangeSet>(change =>
        { if (change.Kind == CatalogChangeKind.Session) seen.Add(runtime.Catalog.Scope); }));
        var nextScope = runtime.Catalog.Scope with { Locale = "de" };
        await runtime.SetSessionAsync(nextScope, "bob", true);
        Assert.Equal(nextScope, Assert.Single(seen));
    }

    [Fact]
    public async Task OfflineObserversSeeTheTargetAlreadyRemoved()
    {
        await using var runtime = Runtime();
        await using var session = new SyncHarness(_ => throw new InvalidOperationException("No network expected"));
        long epoch = runtime.Catalog.Epoch;
        Assert.True(await runtime.SetProtocolSessionAsync(session.Sync, epoch));
        await runtime.Catalog.ReadAsync(new(runtime.Catalog.Scope, "spotify:track:one", FacetKind.TrackIdentity));
        var states = new List<(bool Online, bool Target)>();
        using var watch = runtime.Catalog.Changes.Subscribe(Observers.From<CatalogChangeSet>(change =>
        { if (change.Kind == CatalogChangeKind.Session) states.Add((runtime.Catalog.IsOnline, runtime.LiveSync is not null)); }));
        Assert.True(await runtime.EndSessionAsync(epoch));
        Assert.Equal((false, false), Assert.Single(states));
        Assert.Null(runtime.LiveSync);
    }
}
