using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class RelationProjectionTests
{
    static ResourceKey Key(CatalogFixture fixture, int offset) => new(fixture.Scope, "spotify:album:one", FacetKind.AlbumTracks, new(offset, 50));
    static RelationPageValue Page(string snapshot, int offset, int count, int total) => new(FacetKind.AlbumTracks,
        snapshot, snapshot, offset, total, null, offset + count == total ? RelationCoverage.Complete : RelationCoverage.Partial,
        Enumerable.Range(offset, count).Select(index => new CatalogRelationItem(snapshot + ":" + index, "spotify:track:" + index)).ToArray());
    static async Task Accept(CatalogFixture fixture, RelationPageValue page)
    {
        var request = await fixture.Repository.CaptureRequestAsync(Key(fixture, page.Offset), ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(page)))]);
    }

    [Fact]
    public async Task ReplacementWaitsForOneMatchingSnapshot_AndNeverBorrowsTheOldTail()
    {
        await using var fixture = new CatalogFixture();
        var projection = new RelationProjection(args => new(fixture.Scope, "spotify:album:one", FacetKind.AlbumTracks, args));
        projection.Require(100);
        await Accept(fixture, Page("old", 0, 50, 100)); await Accept(fixture, Page("old", 50, 50, 100));
        projection.Read(new(fixture.Repository));
        var original = projection.Items;
        long order = projection.OrderRevision;
        await Accept(fixture, Page("new", 0, 50, 70));
        var read = new QueryReadContext(fixture.Repository); projection.Read(read);
        Assert.Same(original, projection.Items);
        Assert.Equal(order, projection.OrderRevision);
        Assert.Contains(Key(fixture, 50), projection.Require(100));
        Assert.Contains(Key(fixture, 50), read.RevalidationCandidates.Keys);
        await Accept(fixture, Page("new", 50, 20, 70));
        projection.Read(new(fixture.Repository));
        Assert.Equal(70, projection.Items.Count);
        Assert.All(projection.Items, item => Assert.StartsWith("new:", item.OccurrenceKey));
        Assert.True(projection.Complete);
        Assert.True(projection.OrderRevision > order);
    }

    sealed record Spec(CatalogScope Scope) : QuerySpec<IReadOnlyList<CatalogRelationItem>>(Scope);
    sealed class Definition(CatalogScope scope) : IQueryDefinition<IReadOnlyList<CatalogRelationItem>>
    {
        public QueryReadResult<IReadOnlyList<CatalogRelationItem>> Pending => new([], 0, false);
        readonly RelationProjection _projection = new(args => new(scope, "spotify:album:one", FacetKind.AlbumTracks, args));
        public QueryReadResult<IReadOnlyList<CatalogRelationItem>> Read(QueryReadContext read)
        { _projection.Require(100); _projection.Read(read); return new(_projection.Items, _projection.OrderRevision, _projection.Loaded); }
        public QueryRequirements Requirements(IReadOnlyList<CatalogRelationItem> value, QueryDemand demand) => new(_projection.Require(100), []);
    }
    sealed record WindowSpec(CatalogScope Scope) : QuerySpec<IReadOnlyList<CatalogRelationItem>>(Scope);
    sealed class WindowDefinition(CatalogScope scope) : IQueryDefinition<IReadOnlyList<CatalogRelationItem>>
    {
        public QueryReadResult<IReadOnlyList<CatalogRelationItem>> Pending => new([], 0, false);
        readonly RelationProjection _projection = new(args => new(scope, "spotify:album:one", FacetKind.AlbumTracks, args));
        public QueryReadResult<IReadOnlyList<CatalogRelationItem>> Read(QueryReadContext read)
        { _projection.Read(read); return new(_projection.Items, _projection.OrderRevision, _projection.Loaded); }
        public QueryRequirements Requirements(IReadOnlyList<CatalogRelationItem> value, QueryDemand demand)
            => new(_projection.Require(demand.Active ? 100 : 0), []);
    }

    [Fact]
    public async Task WarmWindowExpansionPublishes_AndSmallerSecondLeaseCannotHideLargerDemand()
    {
        await using var fixture = new CatalogFixture();
        await Accept(fixture, Page("one", 0, 50, 100)); await Accept(fixture, Page("one", 50, 50, 100));
        var provider = new QueryTestProvider(request => throw new InvalidOperationException("Both pages are already fresh."));
        await using var resources = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        using var queries = new QueryService(fixture.Repository, resources, new ReplicaDemand(), new SimpleEvent<ReplicaChange>());
        queries.Register<WindowSpec, IReadOnlyList<CatalogRelationItem>>(query => new WindowDefinition(query.Scope));
        using var large = queries.Acquire(new WindowSpec(fixture.Scope));
        using var small = queries.Acquire(new WindowSpec(fixture.Scope));
        await Until(() => large.Current.Value.Count == 50);
        Assert.Equal(50, large.Current.Value.Count);
        large.SetDemand(QueryDemand.Initial);
        await Until(() => large.Current.Value.Count == 100);
        Assert.Equal(100, large.Current.Value.Count);
        small.SetDemand(QueryDemand.Initial);
        Assert.Equal(100, large.Current.Value.Count);
        Assert.Contains(Key(fixture, 50), queries.GetActiveResourceKeys());
        Assert.Empty(provider.Requests);
        large.SetDemand(QueryDemand.None);
        await Until(() => queries.GetActiveResourceKeys().Contains(Key(fixture, 50)));
        Assert.Contains(Key(fixture, 50), queries.GetActiveResourceKeys());
    }

    sealed class ReplicaDemand : IQueryReplicaDemand
    {
        public Task<IReadOnlyList<ReplicaRequest>> EnsureAsync(IReadOnlyList<ReplicaRequest> requests, bool force, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ReplicaRequest>>(Array.Empty<ReplicaRequest>());
    }

    static async Task Until(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task OfflineNumericCursorUsesTheSameColdPagesAsExplicitOffsetQueries()
    {
        await using var fixture = new CatalogFixture();
        await Accept(fixture, Page("cold", 0, 50, 100) with { NextCursor = "50" });
        await Accept(fixture, Page("cold", 50, 50, 100));
        var reopened = new CatalogRepository(fixture.Commits, fixture.Persistence, fixture.Clock,
            fixture.Scope, fixture.Scope.ProviderAccount);
        await reopened.SetSessionAsync(fixture.Scope, fixture.Scope.ProviderAccount, false);
        var provider = new QueryTestProvider(request => throw new InvalidOperationException("Offline must not fetch."));
        await using var resources = new ResourceCoordinator(reopened, [provider], fixture.Clock);
        using var queries = new QueryService(reopened, resources, new ReplicaDemand(), new SimpleEvent<ReplicaChange>());
        queries.Register<Spec, IReadOnlyList<CatalogRelationItem>>(query => new Definition(query.Scope));
        var result = await queries.ReadOnceAsync(new Spec(fixture.Scope), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Status.HasPrimaryData);
        Assert.Equal(100, result.Value.Count);
        Assert.Equal(100, result.Value.Select(item => item.OccurrenceKey).Distinct().Count());
        Assert.Contains(Key(fixture, 50), result.Resources.Keys);
        Assert.DoesNotContain(result.Resources.Keys, key => key.Arguments.Cursor is not null);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task OnceTotalIsKnown_RequireNamesEveryFurtherOffsetPageInOneWave()
    {
        await using var fixture = new CatalogFixture();
        var projection = new RelationProjection(args => new(fixture.Scope, "spotify:album:one", FacetKind.AlbumTracks, args));
        projection.Require(50);
        await Accept(fixture, Page("one", 0, 50, 230));
        projection.Read(new(fixture.Repository));
        Assert.Equal(230, projection.Total);
        // One PagedShelf pager naming its whole demand: page 0 plus every further offset-addressable page up to
        // Total, in ONE wave — the fix for four artist pagers issuing one POST per page (Part 5 item 3a).
        var required = projection.Require(230);
        Assert.Equal(5, required.Count);
        Assert.Contains(Key(fixture, 0), required);
        Assert.Contains(Key(fixture, 50), required);
        Assert.Contains(Key(fixture, 100), required);
        Assert.Contains(Key(fixture, 150), required);
        Assert.Contains(Key(fixture, 200), required);
    }

    [Fact]
    public async Task ContiguousAssemblyIsUnaffectedByPagesArrivingOutOfOrder()
    {
        await using var fixture = new CatalogFixture();
        var projection = new RelationProjection(args => new(fixture.Scope, "spotify:album:one", FacetKind.AlbumTracks, args));
        projection.Require(150);
        // The tail arrives before the head — two workers racing a batched wave settle in no particular order.
        await Accept(fixture, Page("one", 100, 30, 130));
        await Accept(fixture, Page("one", 50, 50, 130));
        projection.Read(new(fixture.Repository));
        Assert.False(projection.Loaded);
        Assert.Empty(projection.Items);
        await Accept(fixture, Page("one", 0, 50, 130));
        projection.Read(new(fixture.Repository));
        Assert.True(projection.Loaded);
        Assert.True(projection.Complete);
        Assert.Equal(Enumerable.Range(0, 130).Select(i => "spotify:track:" + i), projection.Items.Select(item => item.EntityUri));
    }

    [Fact]
    public async Task OpaqueCursorRemainsPartOfTheNextPageIdentity()
    {
        await using var fixture = new CatalogFixture();
        await Accept(fixture, Page("one", 0, 50, 100) with { NextCursor = "opaque-token" });
        var projection = new RelationProjection(args => new(fixture.Scope, "spotify:album:one", FacetKind.AlbumTracks, args));
        projection.Require(100);
        projection.Read(new(fixture.Repository));
        Assert.Contains(projection.Require(100), key => key.Arguments.Offset == 50 && key.Arguments.Cursor == "opaque-token");
    }

    [Fact]
    public async Task FreshMismatchedPageIsRevalidatedOnceByDemand_WhileThePriorProjectionStaysVisible()
    {
        await using var fixture = new CatalogFixture();
        await Accept(fixture, Page("old", 0, 50, 100)); await Accept(fixture, Page("old", 50, 50, 100));
        var provider = new QueryTestProvider(request => new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(
            Page("new", request.Key.Arguments.Offset, 50, 100)))));
        await using var resources = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        using var queries = new QueryService(fixture.Repository, resources, new ReplicaDemand(), new SimpleEvent<ReplicaChange>());
        queries.Register<Spec, IReadOnlyList<CatalogRelationItem>>(query => new Definition(query.Scope));
        using var handle = queries.Acquire(new Spec(fixture.Scope));
        await QueryPublication.Initial(handle);
        var original = handle.Current.Value;
        await Accept(fixture, Page("new", 0, 50, 100));
        Assert.Same(original, handle.Current.Value);
        Assert.Empty(provider.Requests);
        var result = await queries.ReadOnceAsync(new Spec(fixture.Scope), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(100, result.Value.Count);
        Assert.All(result.Value, item => Assert.StartsWith("new:", item.OccurrenceKey));
        Assert.Equal(Key(fixture, 50), Assert.Single(provider.Requests));
    }
}
