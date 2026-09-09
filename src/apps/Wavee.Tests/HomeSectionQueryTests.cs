using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class HomeSectionQueryTests
{
    const string Uri = "spotify:section:one";
    static ResourceKey Key(CatalogQueryTestHost host, int offset) => new(host.Scope, Uri, FacetKind.HomeSection, new(offset, 50));
    static CatalogDocumentValue Page(string generation, int offset, int count, int? next) => new(FacetKind.HomeSection,
        [new("section", generation, Enumerable.Range(offset, count).Select(index =>
            new CatalogDocumentItem(generation + ":" + index, "spotify:track:" + generation + index)).ToArray())
            { Uri = Uri, TotalCount = 4, RawItemCount = count }], next?.ToString());

    [Fact]
    public async Task FirstPageReplacementNeverBorrowsOldTail_OrAnAlreadyInflightTailReply()
    {
        await using var host = new CatalogQueryTestHost();
        await host.AcceptAsync(Key(host, 0), Page("old", 0, 2, 2));
        await host.AcceptAsync(Key(host, 2), Page("old", 2, 2, null));
        using var query = host.Data.Queries.Acquire(new HomeSectionQuery(host.Scope, Uri));
        await QueryPublication.Until(() => query.Current.Value?.Section.Cards.Count == 4);
        Assert.Equal(4, query.Current.Value!.Section.Cards.Count);
        var oldTailRequest = await host.Data.Catalog.CaptureRequestAsync(Key(host, 2), ResourcePriority.Visible);
        await host.AcceptAsync(Key(host, 0), Page("new", 0, 2, 2));
        await QueryPublication.Until(() => query.Current.Resources.TryGetValue(Key(host, 0), out var resource)
            && resource.Value is CatalogDocumentValue document && document.Sections[0].Title == "new");
        Assert.All(query.Current.Value!.Section.Cards, card => Assert.Contains("old", card.Uri));
        await host.Data.Catalog.AcceptAsync([new(oldTailRequest,
            ResourceFetchResult.Present(new ReplaceFacetPatch(Page("old-inflight", 2, 2, null))))]);
        await QueryPublication.Until(() => query.Current.Resources.TryGetValue(Key(host, 2), out var resource)
            && resource.Value is CatalogDocumentValue document && document.Sections[0].Title == "old-inflight");
        Assert.All(query.Current.Value!.Section.Cards, card => Assert.Contains("old", card.Uri));
        await host.Data.Catalog.InvalidateAsync([Key(host, 2)]);
        await host.AcceptAsync(Key(host, 2), Page("new", 2, 1, null));
        await QueryPublication.Until(() => query.Current.Value?.Section.Cards.Count == 3);
        Assert.Equal(3, query.Current.Value!.Section.Cards.Count);
        Assert.All(query.Current.Value.Section.Cards, card => Assert.Contains("new", card.Uri));
        Assert.Null(query.Current.Value.NextOffset);
    }

    [Fact]
    public async Task ReplacementDemandRevalidatesFreshOldTailOnce_AndKeepsLedgerCounts()
    {
        var provider = new QueryTestProvider(request => request.Key.Facet == FacetKind.HomeSection
            ? new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(Page("new", request.Key.Arguments.Offset, 1, null))))
            : new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Track")))));
        await using var host = new CatalogQueryTestHost(provider);
        await host.AcceptAsync(Key(host, 0), Page("old", 0, 2, 2));
        await host.AcceptAsync(Key(host, 2), Page("old", 2, 2, null));
        using var query = host.Data.Queries.Acquire(new HomeSectionQuery(host.Scope, Uri));
        await host.AcceptAsync(Key(host, 0), Page("new", 0, 2, 2));
        var result = await host.Data.Queries.ReadOnceAsync(new HomeSectionQuery(host.Scope, Uri),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Value!.Section.Cards.Count);
        Assert.Equal(3, result.Value.Section.RawItemCount);
        Assert.All(result.Value.Section.Cards, card => Assert.Contains("new", card.Uri));
        Assert.Equal(Key(host, 2), Assert.Single(provider.Requests.Where(key => key.Facet == FacetKind.HomeSection)));
    }
}
