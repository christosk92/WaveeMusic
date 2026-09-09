using System;
using System.Text.Json;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarArtistQuerySourceTests
{
    [Fact]
    public void Configured_sections_share_live_query_union_demand_and_release_removed_artists()
    {
        var queries = new Queries();
        var posts = new Queue<Action>();
        var clock = new CatalogClock();
        var source = new SidebarArtistTopTracksSource(queries, () => queries.Scope, time: clock);
        source.Attach(posts.Enqueue);
        try
        {
            source.BeginDemandPass();
            source.EnsureFresh(Request("spotify:artist:a", 3));
            source.EnsureFresh(Request("spotify:artist:a", 8));
            Assert.False(Assert.Single(queries.Handles).Demand.Active);
            source.EndDemandPass();
            var handle = Assert.Single(queries.Handles);
            Assert.True(handle.Demand.Active);
            Assert.Equal(QueryPriority.Visible, handle.Demand.Priority);
            Assert.Equal([FacetKind.ArtistPopular], handle.Demand.Facets);

            handle.Emit("first"); Drain(posts, clock);
            var rows = new List<SidebarLibraryEntry>();
            source.Fill(rows, Request("spotify:artist:a", 3));
            Assert.Equal("first", Assert.Single(rows).Name);
            handle.Emit("renamed"); Drain(posts, clock); rows.Clear();
            source.Fill(rows, Request("spotify:artist:a", 3));
            Assert.Equal("renamed", Assert.Single(rows).Name);
            Assert.Single(queries.Handles);

            source.BeginDemandPass();
            source.EndDemandPass();
            Assert.True(handle.Disposed);
            Assert.False(handle.Demand.Active);
        }
        finally { source.Detach(); }
    }

    [Fact]
    public void Park_suppresses_delivery_and_scope_change_disposes_old_account_query()
    {
        var queries = new Queries();
        var posts = new Queue<Action>();
        var clock = new CatalogClock();
        var source = new SidebarArtistTopTracksSource(queries, () => queries.Scope, time: clock);
        source.Attach(posts.Enqueue);
        try
        {
            Pass(source); var old = Assert.Single(queries.Handles);
            old.Emit("first"); Drain(posts, clock);
            source.SetActive(false);
            old.Emit("latest");
            Assert.False(old.Demand.Active);
            source.SetActive(true); Drain(posts, clock);
            var rows = new List<SidebarLibraryEntry>();
            source.Fill(rows, Request("spotify:artist:a", 5));
            Assert.Equal("latest", Assert.Single(rows).Name);

            old.Emit("queued old account");
            queries.Scope = queries.Scope with { ProviderAccount = "next-account" };
            Pass(source); Drain(posts, clock);
            Assert.True(old.Disposed);
            Assert.Equal(2, queries.Handles.Count);
            Assert.Equal(queries.Scope, queries.Handles[1].Query.Scope);
            rows.Clear(); source.Fill(rows, Request("spotify:artist:a", 5));
            Assert.Empty(rows);
            Assert.Equal(SidebarSourceState.Pending, source.State);
        }
        finally { source.Detach(); }
    }

    static void Pass(SidebarArtistTopTracksSource source)
    { source.BeginDemandPass(); source.EnsureFresh(Request("spotify:artist:a", 5)); source.EndDemandPass(); }
    static SidebarSourceRequest Request(string uri, int count)
        => new(new SidebarSourceConfig(JsonSerializer.SerializeToElement(new { artistUri = uri, maxItems = count })));
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
        public CatalogScope Scope = new("spotify", "account", "en", "NL", "premium", 0, false);
        public readonly List<Handle> Handles = [];
        public Task<QuerySnapshot<T>> ReadOnceAsync<T>(QuerySpec<T> query, QueryDemand? demand = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IQueryHandle<T> Acquire<T>(QuerySpec<T> query)
        {
            var handle = new Handle((ArtistDetailQuery)(object)query); Handles.Add(handle);
            return (IQueryHandle<T>)(object)handle;
        }
    }

    sealed class Handle(ArtistDetailQuery query) : IQueryHandle<Artist>, IObservable<QuerySnapshot<Artist>>
    {
        public readonly ArtistDetailQuery Query = query;
        IObserver<QuerySnapshot<Artist>>? _observer;
        public QuerySnapshot<Artist> Current { get; private set; }
            = new(0, 0, new("a", "spotify:artist:a", "artist", null), new(false, false, false), []);
        public IObservable<QuerySnapshot<Artist>> Changes => this;
        public QueryDemand Demand = QueryDemand.None;
        public bool Disposed;
        public void SetDemand(QueryDemand demand) => Demand = demand;
        public ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default) => new(new RefreshResult([]));
        public IDisposable Subscribe(IObserver<QuerySnapshot<Artist>> observer)
        { _observer = observer; observer.OnNext(Current); return new Subscription(() => _observer = null); }
        public void Emit(string title)
        {
            var track = new Track("t", "spotify:track:t", title, [], new("", "", ""), 180_000, false, null);
            Current = new(Current.Revision + 1, 0, Current.Value with { TopTracks = [track] }, new(true, false, false), []);
            _observer?.OnNext(Current);
        }
        public void Dispose() => Disposed = true;
    }
    sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
