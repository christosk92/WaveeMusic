using System;
using Wavee;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class SearchResultPagingTests
{
    [Fact]
    public void Viewport_owns_only_visible_server_pages_and_park_releases_all_demand()
    {
        var service = new Queries();
        var posts = new Queue<Action>();
        var clock = new CatalogClock();
        using var window = Window(service, posts, clock);
        Assert.Single(service.Handles);
        Assert.False(service.Handles[0].Demand.Active);
        window.SetActive(true);
        window.SetRange(20, 30);
        Assert.Equal([0, 20], service.Handles.Keys.Order().ToArray());
        Assert.True(service.Handles[0].Demand.Active);
        Assert.Equal(QueryPriority.Visible, service.Handles[0].Demand.Priority);
        Assert.Empty(service.Handles[0].Demand.Facets);
        Assert.True(service.Handles[20].Demand.Active);
        Assert.Equal(QueryPriority.Visible, service.Handles[20].Demand.Priority);
        Assert.Empty(service.Handles[20].Demand.Facets);
        window.SetRange(40, 50);
        Assert.True(service.Handles[20].Disposed);
        window.SetActive(false);
        Assert.All(service.Handles.Values, handle => Assert.False(handle.Demand.Active));
        window.SetActive(true);
        Assert.True(service.Handles[40].Demand.Active);
        Assert.False(service.Handles[20].Demand.Active);
    }

    [Fact]
    public void Same_count_metadata_changes_repaint_existing_slots_and_park_replays_latest()
    {
        var service = new Queries();
        var posts = new Queue<Action>();
        var clock = new CatalogClock();
        using var window = Window(service, posts, clock);
        window.SetActive(true);
        service.Handles[0].Emit(new Page([new("same", "old")], 1));
        Drain(posts, clock);
        Assert.Equal(1, window.Count);
        Assert.Equal("old", window.ItemAt(0)!.Name);
        window.SetActive(false);
        service.Handles[0].Emit(new Page([new("same", "new")], 1));
        Assert.Equal("old", window.ItemAt(0)!.Name);
        window.SetActive(true);
        Drain(posts, clock);
        Assert.Equal(1, window.Count);
        Assert.Equal("new", window.ItemAt(0)!.Name);
        Assert.Single(service.Handles);
    }

    [Fact]
    public void MissingOfflinePageAndTerminalFailureSurfaceErrors_KnownEmptyRemainsReady()
    {
        var service = new Queries();
        var posts = new Queue<Action>();
        var clock = new CatalogClock();
        using var window = Window(service, posts, clock);
        window.SetActive(true);
        var handle = service.Handles[0];
        handle.EmitUnknownOffline();
        Drain(posts, clock);
        Assert.Contains("offline", window.ErrorAt(0)!.Message);
        handle.Fail(new InvalidOperationException("subscription failed"));
        Drain(posts, clock);
        Assert.Equal("subscription failed", window.ErrorAt(0)!.Message);
        handle.Emit(new([], 0));
        Drain(posts, clock);
        Assert.Equal(0, window.Count);
        Assert.Null(window.ErrorAt(0));
    }

    static SearchResultPaging<Page, Item> Window(Queries service, Queue<Action> posts, CatalogClock clock) => new(service,
        (offset, limit) => new PageQuery(service.Scope, offset), static page => page.Items, static page => page.Total,
        10, posts.Enqueue, time: clock);

    static void Drain(Queue<Action> posts, CatalogClock clock)
    {
        while (posts.TryDequeue(out var post)) post();
        clock.Advance(TimeSpan.FromMilliseconds(QueryDelivery.DeliveryCooldownMs));
        while (posts.TryDequeue(out var post)) post();
    }

    sealed record Item(string Id, string Name);
    sealed record Page(IReadOnlyList<Item> Items, int Total);
    sealed record PageQuery(CatalogScope Scope, int Offset) : QuerySpec<Page>(Scope);

    sealed class Queries : IQueryService
    {
        public readonly CatalogScope Scope = new("spotify", "account", "en", "NL", "premium", 0, false);
        public readonly Dictionary<int, Handle> Handles = [];
        public Task<QuerySnapshot<T>> ReadOnceAsync<T>(QuerySpec<T> query, QueryDemand? demand = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("Paging tests use live handles.");
        public IQueryHandle<T> Acquire<T>(QuerySpec<T> query)
        {
            var page = (PageQuery)(object)query;
            var handle = new Handle();
            Handles.Add(page.Offset, handle);
            return (IQueryHandle<T>)(object)handle;
        }
    }

    sealed class Handle : IQueryHandle<Page>, IObservable<QuerySnapshot<Page>>
    {
        IObserver<QuerySnapshot<Page>>? _observer;
        public QuerySnapshot<Page> Current { get; private set; } = new(0, 0, new([], 0), new(false, false, false), []);
        public IObservable<QuerySnapshot<Page>> Changes => this;
        public QueryDemand Demand = QueryDemand.None;
        public bool Disposed;
        public void SetDemand(QueryDemand demand) => Demand = demand;
        public ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default) => new(new RefreshResult([]));
        public IDisposable Subscribe(IObserver<QuerySnapshot<Page>> observer)
        { _observer = observer; observer.OnNext(Current); return new Subscription(() => _observer = null); }
        public void Emit(Page page)
        { Current = new(Current.Revision + 1, 0, page, new(true, false, false), []); _observer?.OnNext(Current); }
        public void EmitUnknownOffline()
        { Current = new(Current.Revision + 1, 0, new([], 0), new(false, false, true), []); _observer?.OnNext(Current); }
        public void Fail(Exception error) => _observer?.OnError(error);
        public void Dispose() => Disposed = true;
    }

    sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
