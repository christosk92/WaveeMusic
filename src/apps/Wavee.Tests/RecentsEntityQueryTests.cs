using System;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class RecentsEntityQueryTests
{
    [Fact]
    public void Visible_identity_leases_share_duplicates_repaint_metadata_and_release_scrolled_out_rows()
    {
        var queries = new Queries();
        var posts = new Queue<Action>();
        var clock = new CatalogClock();
        var published = new List<string>();
        using var view = new RecentsEntityQueries(queries, queries.Scope, posts.Enqueue, published.Add, time: clock);
        view.SetUris(["spotify:playlist:a", "spotify:playlist:a"]);
        var first = Assert.Single(queries.Handles);
        Assert.False(first.Demand.Active);
        view.SetActive(true);
        Assert.True(first.Demand.Active);
        first.Emit("old"); Drain(posts, clock);
        Assert.Equal("old", view.Read(first.Query.Uri)!.Title);
        first.Emit("same URI, new title"); Drain(posts, clock);
        Assert.Equal("same URI, new title", view.Read(first.Query.Uri)!.Title);
        Assert.Single(queries.Handles);

        view.SetUris(["spotify:track:b"]);
        Assert.True(first.Disposed);
        Assert.False(first.Demand.Active);
        Assert.Null(view.Read(first.Query.Uri));
        Assert.True(queries.Handles[1].Demand.Active);
    }

    [Fact]
    public void Park_retains_presentation_until_activation_and_dispose_drops_queued_publications()
    {
        var queries = new Queries();
        var posts = new Queue<Action>();
        var clock = new CatalogClock();
        var published = new List<string>();
        var view = new RecentsEntityQueries(queries, queries.Scope, posts.Enqueue, published.Add, time: clock);
        view.SetUris(["spotify:playlist:a"]); view.SetActive(true);
        var handle = Assert.Single(queries.Handles);
        handle.Emit("before park"); Drain(posts, clock);
        view.SetActive(false); handle.Emit("while parked");
        Assert.False(handle.Demand.Active);
        Assert.Equal("before park", view.Read(handle.Query.Uri)!.Title);
        view.SetActive(true); Drain(posts, clock);
        Assert.Equal("while parked", view.Read(handle.Query.Uri)!.Title);
        handle.Emit("obsolete");
        int count = published.Count;
        view.Dispose(); Drain(posts, clock);
        Assert.True(handle.Disposed);
        Assert.Equal(count, published.Count);
    }

    // Drain the UI queue, let the delivery cooldown (QueryDelivery.DeliveryCooldownMs) elapse on the test clock so a
    // trailing delivery fires, and drain that too: each test step models "the UI caught up and the window passed".
    static void Drain(Queue<Action> posts, CatalogClock clock)
    {
        while (posts.TryDequeue(out var post)) post();
        clock.Advance(TimeSpan.FromMilliseconds(QueryDelivery.DeliveryCooldownMs));
        while (posts.TryDequeue(out var post)) post();
    }
    sealed class Queries : IQueryService
    {
        public readonly CatalogScope Scope = new("spotify", "account", "en", "NL", "premium", 0, false);
        public readonly List<Handle> Handles = [];
        public Task<QuerySnapshot<T>> ReadOnceAsync<T>(QuerySpec<T> query, QueryDemand? demand = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IQueryHandle<T> Acquire<T>(QuerySpec<T> query)
        {
            var handle = new Handle((EntityCardQuery)(object)query); Handles.Add(handle);
            return (IQueryHandle<T>)(object)handle;
        }
    }
    sealed class Handle(EntityCardQuery query) : IQueryHandle<EntityCardSnapshot>, IObservable<QuerySnapshot<EntityCardSnapshot>>
    {
        public readonly EntityCardQuery Query = query;
        IObserver<QuerySnapshot<EntityCardSnapshot>>? _observer;
        public QuerySnapshot<EntityCardSnapshot> Current { get; private set; }
            = new(0, 0, new(query.Uri, "", null, null, null), new(false, false, false), []);
        public IObservable<QuerySnapshot<EntityCardSnapshot>> Changes => this;
        public QueryDemand Demand = QueryDemand.None;
        public bool Disposed;
        public void SetDemand(QueryDemand demand) => Demand = demand;
        public ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default) => new(new RefreshResult([]));
        public IDisposable Subscribe(IObserver<QuerySnapshot<EntityCardSnapshot>> observer)
        { _observer = observer; observer.OnNext(Current); return new Subscription(() => _observer = null); }
        public void Emit(string title)
        {
            Current = new(Current.Revision + 1, 0, Current.Value with { Title = title }, new(true, false, false), []);
            _observer?.OnNext(Current);
        }
        public void Dispose() => Disposed = true;
    }
    sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
