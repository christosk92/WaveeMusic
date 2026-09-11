using System.Linq;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class HomeGeneratedPlaylistQueryTests
{
    const string Uri = "spotify:playlist:daylist";

    [Fact]
    public async Task DuplicateDaylistOccurrencesAndSectionAccountingJoinOneHeader_WithoutRequeryingHome()
    {
        var provider = new QueryTestProvider(request => new(request, ResourceFetchResult.Absent()));
        await using var host = new CatalogQueryTestHost(provider);
        var card = new CatalogDocumentItem("card", Uri) { HomeKind = HomeCardKind.Playlist,
            HomeMeta = new(Format: "daylist", Seeds: ["teen pop", "friday afternoon"]) };
        var document = new CatalogDocumentValue(FacetKind.Home,
            [new("section", "Your daylist", [card]) { Uri = "spotify:section:daylist", TotalCount = 7, RawItemCount = 3, UnsupportedCount = 1, DuplicateCount = 1 }])
        { HomeGroups = [new("hero", HomeGroupKind.Hero, null, [card]), new("quick", HomeGroupKind.QuickGrid, "Jump back in", [card])] };
        await host.AcceptAsync(new(host.Scope, CatalogSubjects.Home, FacetKind.Home, new(Filter: "music")), document);
        using var home = host.Data.Queries.Acquire(new HomeQuery(host.Scope, "music"));
        await QueryPublication.Until(() => home.Current.Revision > 0);
        var order = home.Current.OrderRevision;
        await host.AcceptAsync(new(host.Scope, Uri, FacetKind.PlaylistHeader),
            new PlaylistHeaderValue(Name: "Friday afternoon", Cover: new("https://img/friday"), Edition: "2") { Format = "daylist" });
        await QueryPublication.Until(() => home.Current.Value.Groups.SelectMany(group => group.Cards).Any(card => card.Title == "Friday afternoon"));
        Assert.Equal(2, home.Current.Value.Groups.Sum(group => group.Cards.Count));
        Assert.All(home.Current.Value.Groups.SelectMany(group => group.Cards), item => Assert.Equal("Friday afternoon", item.Title));
        var section = Assert.Single(home.Current.Value.Sections!);
        Assert.Equal("Friday afternoon", Assert.Single(section.Cards).Title);
        Assert.Equal(7, section.TotalCount); Assert.Equal(3, section.RawItemCount);
        Assert.Equal(1, section.UnsupportedCount); Assert.Equal(1, section.DuplicateCount);
        Assert.Equal("music", home.Current.Value.Facet);
        Assert.Equal(order, home.Current.OrderRevision);
        Assert.Empty(provider.Requests);
    }
}
