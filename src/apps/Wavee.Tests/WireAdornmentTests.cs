using System.Linq;
using System.Text.Json;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Features.Browse;
using Xunit;

namespace Wavee.Tests;

/// <summary>Coverage for the search facets, row adornments and browse taxonomy added from the 2026 captures. The JSON
/// fragments are trimmed from real <c>omg.saz</c> responses.</summary>
public class SearchFacetMapperTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SearchPodcasts_MapsShowsWithPublisherAndCover()
    {
        var json = """
        {"data":{"searchV2":{"podcasts":{"totalCount":30,"items":[
          {"data":{"__typename":"Podcast","uri":"spotify:show:3WUTcpjJ4f7YMutv1MfAbm","name":"Wasabi podcast",
            "mediaType":"MIXED","publisher":{"name":"daily Japanese Listening"},
            "coverArt":{"sources":[{"height":300,"url":"https://i.scdn.co/image/x","width":300}]}}}
        ]}}}}
        """;
        var r = SpotifyExportMapper.SearchFromV2(Root(json));
        var show = Assert.Single(r.Shows!);
        Assert.Equal("Wasabi podcast", show.Name);
        Assert.Equal("daily Japanese Listening", show.Publisher);
        Assert.NotNull(show.Cover);
        Assert.Equal(30, r.ShowsTotal);
        Assert.True(r.HasAny(SearchFacet.Podcasts));
    }

    // An audiobook carries its access signifier ("Included in Premium") and a BARE authorsV2 array — not the usual
    // {items:[{data:…}]} envelope every other list uses.
    [Fact]
    public void SearchAudiobooks_MapsAccessSignifierAndFlatAuthorsArray()
    {
        var json = """
        {"data":{"searchV2":{"audiobooks":{"totalCount":30,"items":[
          {"data":{"__typename":"Audiobook","uri":"spotify:show:ab1","name":"Some Book",
            "accessInfo":{"signifier":{"text":"Included in Premium"}},
            "authorsV2":[{"name":"Miles Carter","uri":"spotify:author:1"},{"name":"Jane Doe","uri":"spotify:author:2"}],
            "coverArt":{"sources":[{"height":300,"url":"https://i/x","width":300}]}}}
        ]}}}}
        """;
        var hit = Assert.Single(SpotifyExportMapper.SearchFromV2(Root(json)).Audiobooks!);
        Assert.Equal(SearchHitKind.Audiobook, hit.Kind);
        Assert.Equal("Included in Premium", hit.AccessLabel);
        Assert.Equal("Miles Carter, Jane Doe", hit.Subtitle);
    }

    // An episode's artwork belongs to its SHOW — the episode node itself carries none.
    [Fact]
    public void SearchEpisodes_TakesArtAndShowNameFromPodcastV2()
    {
        var json = """
        {"data":{"searchV2":{"episodes":{"totalCount":30,"items":[
          {"data":{"__typename":"Episode","uri":"spotify:episode:e1","name":"Ep 1","description":"About things",
            "podcastV2":{"data":{"__typename":"Podcast","name":"The Show",
              "coverArt":{"sources":[{"height":300,"url":"https://i/show","width":300}]}}}}}
        ]}}}}
        """;
        var ep = Assert.Single(SpotifyExportMapper.SearchFromV2(Root(json)).Episodes!);
        Assert.Equal("Ep 1", ep.Title);
        Assert.Equal("The Show", ep.ShowName);
        Assert.NotNull(ep.Image);
    }

    [Fact]
    public void SearchUsers_MapsProfilesAsRoundFollowableHits()
    {
        var json = """
        {"data":{"searchV2":{"users":{"totalCount":2,"items":[
          {"data":{"__typename":"User","uri":"spotify:user:u1","displayName":"christosk92","username":"u1"}}
        ]}}}}
        """;
        var hit = Assert.Single(SpotifyExportMapper.SearchFromV2(Root(json)).Profiles!);
        Assert.Equal(SearchHitKind.User, hit.Kind);
        Assert.Equal("christosk92", hit.Name);
        Assert.True(hit.RoundImage);
        Assert.True(hit.Followable);
    }

    // searchAuthors returned a wrapper whose data is literally {"__typename":"NotFound"} — a dead reference to skip.
    [Fact]
    public void SearchResults_NotFoundEntriesAreSkipped()
    {
        var json = """
        {"data":{"searchV2":{"podcasts":{"totalCount":1,"items":[{"data":{"__typename":"NotFound"}}]}}}}
        """;
        var r = SpotifyExportMapper.SearchFromV2(Root(json));
        Assert.Null(r.Shows);
        Assert.False(r.HasAny(SearchFacet.Podcasts));
    }

    // A facet that was never queried reports -1 (unknown), distinct from 0 (queried, no results).
    [Fact]
    public void TotalFor_DistinguishesNotQueriedFromEmpty()
    {
        var queried = SpotifyExportMapper.SearchFromV2(
            Root("""{"data":{"searchV2":{"podcasts":{"totalCount":0,"items":[]}}}}"""));
        Assert.Equal(0, queried.TotalFor(SearchFacet.Podcasts));

        Assert.Equal(-1, SearchResults.Empty.ShowsTotal);
    }
}

public class TrackAdornmentTests
{
    [Theory]
    [InlineData("#56d9f8", 0xFF56D9F8u)]
    [InlineData("#FF80B4", 0xFFFF80B4u)]
    [InlineData("#05eccb", 0xFF05ECCBu)]
    public void FromHex_AcceptsCssHex(string hex, uint expected)
        => Assert.Equal(expected, SpotifyColor.FromHex(hex));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("56d9f8")]      // no leading '#'
    [InlineData("#56d9f")]      // too short
    [InlineData("#56d9f8f")]    // too long
    [InlineData("#zzzzzz")]     // not hex
    public void FromHex_RejectsMalformedRatherThanGuessing(string? hex)
        => Assert.Null(SpotifyColor.FromHex(hex));

    // Adornments arrive on their OWN pass, long after the thin cluster/library upsert that created the row. A later
    // thin write must never blank them — that would make tints and tempo flicker away during playback.
    [Fact]
    public void StoreMerge_ThinUpsertDoesNotClobberAdornments()
    {
        var store = new Wavee.Backend.InMemoryStore();
        var baseTrack = new Track("t1", "spotify:track:t1", "Title",
            System.Array.Empty<ArtistRef>(), new AlbumRef("a", "spotify:album:a", "Album"),
            DurationMs: 1000, IsExplicit: false, Image: null);

        store.UpsertTrack(baseTrack);
        store.UpsertTrack(baseTrack with
        {
            TempoBpm = 101.0099, MusicalKey = "A", CamelotCode = "11B", CamelotColor = 0xFF56D9F8u,
        });
        // A later thin write (e.g. a cluster projection) that knows nothing about adornments.
        store.UpsertTrack(baseTrack);

        var t = store.GetTrack("spotify:track:t1")!;
        Assert.Equal("A", t.MusicalKey);
        Assert.Equal("11B", t.CamelotCode);
        Assert.Equal(0xFF56D9F8u, t.CamelotColor);
        Assert.NotNull(t.TempoBpm);
    }
}

public class BrowseTaxonomyTests
{
    static BrowseCategory Cat(string uri, string title) => new(uri, title, null);

    [Fact]
    public void Grouped_PlacesKnownUrisInTheirBand_AndKeepsTopInServerOrder()
    {
        var cats = new[]
        {
            Cat("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", "Music"),
            Cat("spotify:page:0JQ5DArNBzkmxXHCqFLx2J", "Podcasts"),
            Cat("spotify:page:0JQ5DAqbMKFDXXwE9BDJAr", "Rock"),
            Cat("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", "Pop"),
        };

        var groups = BrowseTaxonomy.Grouped(cats);

        var top = groups.First(g => g.Group == BrowseGroup.Top);
        Assert.Equal(new[] { "Music", "Podcasts" }, top.Items.Select(i => i.Title));   // server order, NOT alphabetical

        var genres = groups.First(g => g.Group == BrowseGroup.Genres);
        Assert.Equal(new[] { "Pop", "Rock" }, genres.Items.Select(i => i.Title));      // alphabetised within the band
    }

    // A category Spotify adds tomorrow must still appear — unmapped falls to More rather than vanishing.
    [Fact]
    public void Grouped_UnknownUriFallsToMoreInsteadOfDisappearing()
    {
        var groups = BrowseTaxonomy.Grouped(new[] { Cat("spotify:page:brand-new", "Something New") });
        var more = Assert.Single(groups);
        Assert.Equal(BrowseGroup.More, more.Group);
        Assert.Equal("Something New", Assert.Single(more.Items).Title);
    }

    [Fact]
    public void Grouped_EmptyInputYieldsNoBands() => Assert.Empty(BrowseTaxonomy.Grouped(System.Array.Empty<BrowseCategory>()));

    [Fact]
    public void GroupOf_ClientFeatureLiveEventsIsTop()
        => Assert.Equal(BrowseGroup.Top, BrowseTaxonomy.GroupOf(new BrowseCategory("spotify:concerts", "Live Events", null, null, true)));
}

public class SpotifyTimeZoneTests
{
    // The zone must be a real IANA id — Spotify uses it to bucket the greeting and the time-of-day shelves. A Windows
    // id ("W. Europe Standard Time") would be silently wrong, so assert the shape rather than a specific machine's zone.
    [Fact]
    public void LocalIana_IsAnIanaIdOrTheExplicitUtcFallback()
    {
        string tz = SpotifyTimeZone.LocalIana;
        Assert.False(string.IsNullOrWhiteSpace(tz));
        Assert.True(tz == SpotifyTimeZone.Fallback || tz.Contains('/'),
            $"expected an IANA id (Area/Location) or the '{SpotifyTimeZone.Fallback}' fallback, got '{tz}'");
        Assert.DoesNotContain("Standard Time", tz, System.StringComparison.Ordinal);
    }

    [Fact]
    public void LocalIana_IsStableAcrossCalls() => Assert.Equal(SpotifyTimeZone.LocalIana, SpotifyTimeZone.LocalIana);
}
