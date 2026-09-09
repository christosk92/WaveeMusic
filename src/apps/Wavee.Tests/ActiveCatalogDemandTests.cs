using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class ActiveCatalogDemandTests
{
    const string Uri = "spotify:playlist:daylist";
    sealed class TestProvider : ICatalogResourceProvider
    {
        public string Provider => "spotify";
        public readonly ConcurrentQueue<ResourceKey> Requests = new();
        public Func<ResourceRequest, CancellationToken, Task<ResourceFetchResult>> Fetch = (_, _) =>
            Task.FromResult(ResourceFetchResult.Present(new ReplaceFacetPatch(new PlaylistRevisionValue("1"))));
        public async ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        {
            var responses = new List<ResourceResponse>();
            foreach (var request in requests)
            { Requests.Enqueue(request.Key); responses.Add(new(request, await Fetch(request, ct))); }
            return responses;
        }
        public int Count(FacetKind kind) => Requests.Count(key => key.Facet == kind);
    }

    sealed class Fixture : IAsyncDisposable
    {
        public CatalogFixture Catalog { get; } = new();
        public TestProvider Provider { get; } = new();
        public ResourceCoordinator Resources { get; }
        public ActiveCatalogDemand Active { get; }
        public List<Exception> Errors { get; } = [];
        public ResourceKey Header => new(Catalog.Scope, Uri, FacetKind.PlaylistHeader);
        public Fixture()
        {
            Resources = new(Catalog.Repository, [Provider], Catalog.Clock);
            Active = new(Catalog.Repository, Resources, Catalog.Clock, Errors.Add);
        }
        public PlaylistHeaderValue Value(DateTimeOffset? expires = null, string edition = "1", string title = "Old")
            => new(Name: title, Edition: edition, NextUpdateAt: expires) { Format = "daylist" };
        public Task Seed(PlaylistHeaderValue value) => Catalog.Repository.SeedManyAsync(
            [new(Header, new ReplaceFacetPatch(value))], Catalog.Repository.Epoch);
        public void Lease(object owner) => Active.Set(owner, [Header, Header]);
        public async Task Advance(TimeSpan by) { Catalog.Clock.Advance(by); await Active.RunDueAsync(); }
        public async ValueTask DisposeAsync()
        { Active.Dispose(); await Resources.DisposeAsync(); await Catalog.DisposeAsync(); }
    }

    [Fact]
    public async Task SharedLeasesProbeOncePerMinute_AndParkingTheLastLeaseStopsWork()
    {
        await using var f = new Fixture();
        await f.Seed(f.Value(f.Catalog.Clock.GetUtcNow().AddHours(1)));
        object home = new(), detail = new(); f.Lease(home); f.Lease(detail);
        await f.Active.RunDueAsync();
        Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistRevision));
        Assert.Equal(0, f.Provider.Count(FacetKind.PlaylistHeader));
        await f.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistRevision));
        f.Active.Set(home, []);
        await f.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, f.Provider.Count(FacetKind.PlaylistRevision));
        f.Active.Set(detail, []);
        await f.Advance(TimeSpan.FromHours(2));
        Assert.Equal(2, f.Provider.Requests.Count);
        Assert.Empty(f.Errors);
    }

    [Fact]
    public async Task RevisionFacetCoalescesForFiveSeconds_AcrossLeaseRemounts()
    {
        await using var f = new Fixture();
        await f.Seed(f.Value());
        object owner = new(); f.Lease(owner); await f.Active.RunDueAsync();
        f.Active.Set(owner, []);
        await f.Advance(TimeSpan.FromSeconds(4));
        f.Lease(owner); await f.Active.RunDueAsync();
        Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistRevision));
        f.Active.Set(owner, []);
        await f.Advance(TimeSpan.FromSeconds(1));
        f.Lease(owner); await f.Active.RunDueAsync();
        Assert.Equal(2, f.Provider.Count(FacetKind.PlaylistRevision));
    }

    [Fact]
    public async Task ExpiredKnownTitleRefreshesImmediately_AndLaggingHeadersRetryOnlyEveryFiveMinutes()
    {
        await using var f = new Fixture();
        var expired = f.Value(f.Catalog.Clock.GetUtcNow().AddHours(-1));
        await f.Seed(expired);
        int revision = 1;
        f.Provider.Fetch = (request, _) => Task.FromResult(ResourceFetchResult.Present(new ReplaceFacetPatch(
            request.Key.Facet == FacetKind.PlaylistRevision ? new PlaylistRevisionValue(revision.ToString()) : expired)));
        f.Lease(new object()); await f.Active.RunDueAsync();
        Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistHeader));
        for (int minute = 1; minute < 5; minute++)
        {
            revision++;
            await f.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistHeader));
        }
        await f.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, f.Provider.Count(FacetKind.PlaylistHeader));
        Assert.Equal("Old", Assert.IsType<PlaylistHeaderValue>(f.Catalog.Repository.Peek(f.Header).Value).Name);
    }

    [Fact]
    public async Task ARecentlyFetchedExpiredHeaderCarriesItsRetryDeadlineAcrossRemount()
    {
        await using var f = new Fixture();
        var now = f.Catalog.Clock.GetUtcNow();
        var expired = f.Value(now.AddHours(-1));
        f.Catalog.Persistence.Records[f.Header] = new(f.Header, Knowledge.Present, expired, CatalogProvenance.Provider,
            now.AddMinutes(-1), now.AddMinutes(4), 1);
        await f.Catalog.Repository.ReadAsync(f.Header);
        f.Provider.Fetch = (request, _) => Task.FromResult(ResourceFetchResult.Present(new ReplaceFacetPatch(
            request.Key.Facet == FacetKind.PlaylistRevision ? new PlaylistRevisionValue("1") : expired)));
        object owner = new(); f.Lease(owner); await f.Active.RunDueAsync();
        Assert.Equal(0, f.Provider.Count(FacetKind.PlaylistHeader));
        f.Active.Set(owner, []); f.Lease(owner);
        await f.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, f.Provider.Count(FacetKind.PlaylistHeader));
        await f.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistHeader));
    }

    [Fact]
    public async Task ChangedHeadRefreshesAnUnchangedTitleOnce_AndAcceptingTheNewEditionStopsRefreshes()
    {
        await using var f = new Fixture();
        var future = f.Catalog.Clock.GetUtcNow().AddHours(1);
        await f.Seed(f.Value(future));
        f.Provider.Fetch = (request, _) => Task.FromResult(ResourceFetchResult.Present(new ReplaceFacetPatch(
            request.Key.Facet == FacetKind.PlaylistRevision ? new PlaylistRevisionValue("2") : f.Value(future, "2"))));
        f.Lease(new object()); await f.Active.RunDueAsync();
        Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistHeader));
        await f.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistHeader));
        Assert.Equal("2", Assert.IsType<PlaylistHeaderValue>(f.Catalog.Repository.Peek(f.Header).Value).Edition);
        Assert.Equal("Old", Assert.IsType<PlaylistHeaderValue>(f.Catalog.Repository.Peek(f.Header).Value).Name);
    }

    [Fact]
    public async Task RemovingLastLeaseWhileHeadIsInFlightCannotStartAHeaderRequest()
    {
        await using var f = new Fixture();
        await f.Seed(f.Value());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Provider.Fetch = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return ResourceFetchResult.Present(new ReplaceFacetPatch(new PlaylistRevisionValue("2"))); };
        object owner = new(); f.Lease(owner);
        var pass = f.Active.RunDueAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        f.Active.Set(owner, []); release.SetResult(); await pass;
        Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistRevision));
        Assert.Equal(0, f.Provider.Count(FacetKind.PlaylistHeader));
    }

    [Fact]
    public async Task SessionChangeWhileHeadIsInFlightFencesTheOldResultAndItsFollowUp()
    {
        await using var f = new Fixture();
        await f.Seed(f.Value());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Provider.Fetch = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return ResourceFetchResult.Present(new ReplaceFacetPatch(new PlaylistRevisionValue("2"))); };
        f.Lease(new object()); var pass = f.Active.RunDueAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await f.Catalog.Repository.SetSessionAsync(f.Catalog.Scope with { ProviderAccount = "other" }, "other", online: true);
        release.SetResult(); await pass;
        Assert.Equal(0, f.Provider.Count(FacetKind.PlaylistHeader));
        Assert.Equal(Knowledge.Unknown, f.Catalog.Repository.Peek(f.Header with { Facet = FacetKind.PlaylistRevision }).Knowledge);
    }

    [Fact]
    public async Task FailedHeadDoesNotInventARollover_AndLateHeaderKnowledgeArmsExistingDemand()
    {
        await using var f = new Fixture();
        f.Provider.Fetch = (_, _) => Task.FromResult(ResourceFetchResult.Failed(new(ResourceErrorKind.Forbidden, "unavailable", 403)));
        f.Lease(new object()); await f.Active.RunDueAsync();
        Assert.Empty(f.Provider.Requests);
        await f.Seed(f.Value()); await f.Active.RunDueAsync();
        Assert.Equal(1, f.Provider.Count(FacetKind.PlaylistRevision));
        Assert.Equal(0, f.Provider.Count(FacetKind.PlaylistHeader));
        await f.Active.RunDueAsync();
        Assert.Equal(1, f.Provider.Requests.Count);
        Assert.Equal("Old", Assert.IsType<PlaylistHeaderValue>(f.Catalog.Repository.Peek(f.Header).Value).Name);
    }
}
