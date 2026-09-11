using System.Collections.Concurrent;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class HomeQueryReadinessTests
{
    static ResourceKey Document(CatalogQueryTestHost host, string facet = "")
        => new(host.Scope, CatalogSubjects.Home, FacetKind.Home, new(Filter: facet));

    [Fact]
    public async Task UnknownDocumentIsPending_AnAuthoritativeEmptyDocumentIsReady()
    {
        await using var host = new CatalogQueryTestHost();
        using var home = host.Data.Queries.Acquire(new HomeQuery(host.Scope));
        await QueryPublication.Until(() => home.Current.Revision > 0);
        Assert.False(home.Current.Status.HasPrimaryData);
        Assert.Empty(home.Current.Problems);
        Assert.Equal(Knowledge.Unknown, home.Current.Resources[Document(host)].Knowledge);
        await host.AcceptAsync(Document(host), new CatalogDocumentValue(FacetKind.Home, []));
        await QueryPublication.Until(() => home.Current.Status.HasPrimaryData);
        Assert.True(home.Current.Status.HasPrimaryData);
        Assert.Empty(home.Current.Value.Groups);
        Assert.Equal(Knowledge.Present, home.Current.Resources[Document(host)].Knowledge);
    }

    [Fact]
    public async Task RefreshFailureRetainsTheLastGoodDocument_AndRecoveryPublishesWithoutARevealTimer()
    {
        await using var host = new CatalogQueryTestHost();
        var key = Document(host);
        await host.AcceptAsync(key, new CatalogDocumentValue(FacetKind.Home, []) { Greeting = "First" });
        using var home = host.Data.Queries.Acquire(new HomeQuery(host.Scope));
        var request = await host.Data.Catalog.CaptureRequestAsync(key, ResourcePriority.Visible);
        await host.Data.Catalog.AcceptAsync([new(request, ResourceFetchResult.Failed(new(ResourceErrorKind.Transport, "offline")))]);
        await QueryPublication.Until(() => home.Current.Problems.Count > 0);
        Assert.True(home.Current.Status.HasPrimaryData);
        Assert.Equal("First", home.Current.Value.Greeting);
        Assert.NotEmpty(home.Current.Problems);
        await host.AcceptAsync(key, new CatalogDocumentValue(FacetKind.Home, []) { Greeting = "Recovered" });
        await QueryPublication.Until(() => home.Current.Value.Greeting == "Recovered");
        Assert.True(home.Current.Status.HasPrimaryData);
        Assert.Equal("Recovered", home.Current.Value.Greeting);
        Assert.Empty(home.Current.Problems);
    }

    [Fact]
    public async Task SwitchingDocumentsDisposesOldQueuedDelivery_AndRevealsOnlyTheNewFacet()
    {
        await using var host = new CatalogQueryTestHost();
        var posts = new ConcurrentQueue<System.Action>();
        var seen = new System.Collections.Generic.List<string>();
        var old = new QuerySignalBinding<HomeFeed>(host.Data.Queries.Acquire(new HomeQuery(host.Scope)), posts.Enqueue,
            snapshot => { if (snapshot.Status.HasPrimaryData) seen.Add(snapshot.Value.Greeting); });
        old.SetDemand(QueryDemand.None); old.SetActive(true);
        await host.AcceptAsync(Document(host), new CatalogDocumentValue(FacetKind.Home, []) { Greeting = "Old" });
        old.Dispose();
        using var next = new QuerySignalBinding<HomeFeed>(host.Data.Queries.Acquire(new HomeQuery(host.Scope, "music")),
            posts.Enqueue, snapshot => { if (snapshot.Status.HasPrimaryData) seen.Add(snapshot.Value.Greeting); });
        next.SetDemand(QueryDemand.None); next.SetActive(true);
        await host.AcceptAsync(Document(host, "music"), new CatalogDocumentValue(FacetKind.Home, []) { Greeting = "Music" });
        await QueryPublication.Until(() => { while (posts.TryDequeue(out var post)) post(); return seen.Count > 0; });
        Assert.Equal(new[] { "Music" }, seen);
        Assert.Equal("music", next.Snapshot.Peek().Value.Facet);
    }
    [Fact]
    public async Task SectionOnlyCardsAreDemanded_AndParkingReleasesCardPins()
    {
        const string card = "spotify:album:section-only";
        var provider = new QueryTestProvider(request => new(request, request.Key.Facet == FacetKind.AlbumIdentity
            ? ResourceFetchResult.Present(new ReplaceFacetPatch(new AlbumIdentityValue("Section album")))
            : ResourceFetchResult.Absent()));
        await using var host = new CatalogQueryTestHost(provider);
        await host.AcceptAsync(Document(host, "music"), new CatalogDocumentValue(FacetKind.Home,
            [new("section", "Section", [new("card", card)])]));
        var spec = new HomeQuery(host.Scope, "music");
        var demand = QueryDemand.Initial;
        var result = await host.Data.Queries.ReadOnceAsync(spec, demand);
        Assert.Equal("Section album", Assert.Single(Assert.Single(result.Value.Sections!).Cards).Title);
        using var handle = host.Data.Queries.Acquire(spec);
        handle.SetDemand(demand);
        await QueryPublication.Until(() => host.Data.Queries.GetActiveResourceKeys().Any(key => key.Subject == card));
        Assert.Contains(host.Data.Queries.GetActiveResourceKeys(), key => key.Subject == card);
        handle.SetDemand(QueryDemand.None);
        await QueryPublication.Until(() => !host.Data.Queries.GetActiveResourceKeys().Any(key => key.Subject == card));
        Assert.DoesNotContain(host.Data.Queries.GetActiveResourceKeys(), key => key.Subject == card);
    }

}
