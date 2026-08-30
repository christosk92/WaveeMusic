using System.Linq;
using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>Browse wire-shape coverage. Every JSON fragment here is trimmed from real <c>browe.saz</c> responses, so
/// these tests fail if the mapper stops matching what Spotify actually sends — including the four awkward behaviours
/// the mapper exists to absorb.</summary>
public class BrowseMapperTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    // The category card's fields live under content.data.DATA.cardRepresentation — a DOUBLE `data`. Stopping one level
    // short (the original bug) yields a grid of nameless, colourless tiles.
    [Fact]
    public void Categories_ReadsTitleAndColourFromDoubleNestedCardRepresentation()
    {
        var json = """
        {"data":{"browseStart":{"sections":{"items":[{"sectionItems":{"items":[
          {"uri":"spotify:page:0JQ5DAqbMKFSi39LMRT0Cy",
           "content":{"__typename":"BrowseSectionContainerWrapper","data":{"__typename":"BrowseSectionContainer",
             "data":{"cardRepresentation":{
               "artwork":{"sources":[{"height":300,"url":"https://i.scdn.co/image/abc","width":300}]},
               "backgroundColor":{"hex":"#1e3264"},
               "title":{"transformedLabel":"Music"}}}}}}
        ]}}]}}}}
        """;

        var cats = SpotifyBrowseMapper.Categories(Root(json));

        var c = Assert.Single(cats);
        Assert.Equal("Music", c.Title);
        Assert.Equal("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", c.Uri);
        Assert.Equal(0xFF1E3264u, c.Color);
        Assert.False(c.IsClientFeature);
        Assert.NotNull(c.Artwork);
    }

    // Live Events is a BrowseClientFeature: one level SHALLOWER than a page container, and its routing target is
    // featureUri (spotify:concerts) — not a browse page uri.
    [Fact]
    public void Categories_ClientFeature_UsesFeatureUriAndIsFlagged()
    {
        var json = """
        {"data":{"browseStart":{"sections":{"items":[{"sectionItems":{"items":[
          {"uri":"spotify:xlink:0JQ5DAozXW0GUBAKjHsifL",
           "content":{"__typename":"BrowseXlinkResponseWrapper","data":{"__typename":"BrowseClientFeature",
             "artwork":{"sources":[{"height":300,"url":"https://concerts.spotifycdn.com/x.jpg","width":300}]},
             "backgroundColor":{"hex":"#8400e7"},
             "featureUri":"spotify:concerts",
             "title":{"transformedLabel":"Live Events"}}}}
        ]}}]}}}}
        """;

        var c = Assert.Single(SpotifyBrowseMapper.Categories(Root(json)));
        Assert.True(c.IsClientFeature);
        Assert.Equal("spotify:concerts", c.Uri);
        Assert.Equal("Live Events", c.Title);
    }

    // Observed: a 200 whose data.browse carries ONLY __typename. Must be a calm empty page, never an exception.
    [Fact]
    public void Page_HeaderlessSectionlessBody_IsEmptyNotAnError()
    {
        var page = SpotifyBrowseMapper.Page(Root("""{"data":{"browse":{"__typename":"BrowseSectionContainer"}}}"""),
                                            "spotify:page:x");
        Assert.NotNull(page);
        Assert.True(page.IsEmpty);
        Assert.Empty(page.Sections);
        Assert.Equal("spotify:page:x", page.Uri);
    }

    // header.color is null on some pages (Made For You) — the accent is genuinely optional.
    [Fact]
    public void Page_NullHeaderColour_YieldsNullAccentAndKeepsTitle()
    {
        var json = """
        {"data":{"browse":{"uri":"spotify:page:mfy","header":{"title":{"transformedLabel":"Made For You"},"color":null},
          "sections":{"totalCount":11,"pagingInfo":{"nextOffset":10},"items":[]}}}}
        """;
        var page = SpotifyBrowseMapper.Page(Root(json), "spotify:page:mfy");
        Assert.Equal("Made For You", page.Title);
        Assert.Null(page.Accent);
        Assert.Equal(11, page.TotalSections);
        Assert.Equal(10, page.NextSectionOffset);
    }

    // A section item can be a NotFound wrapper mixed among real cards. Rendering it would produce a blank card.
    [Fact]
    public void Page_ShelfSkipsNotFoundItems()
    {
        var json = """
        {"data":{"browse":{"uri":"spotify:page:fit","header":{"title":{"transformedLabel":"Fitness"},"color":{"hex":"#777777"}},
          "sections":{"totalCount":1,"items":[
            {"uri":"spotify:section:s1","data":{"__typename":"BrowseGenericSectionData","title":{"transformedLabel":"Hip-Hop Workout Music"}},
             "sectionItems":{"totalCount":8,"items":[
               {"content":{"data":{"__typename":"Playlist","uri":"spotify:playlist:p1","name":"Real",
                  "description":"desc","coverArt":{"sources":[{"height":300,"url":"https://i/x","width":300}]}}}},
               {"content":{"data":{"__typename":"NotFound"}}}
             ]}}
          ]}}}}
        """;
        var page = SpotifyBrowseMapper.Page(Root(json), "spotify:page:fit");
        Assert.Equal(0xFF777777u, page.Accent);
        var section = Assert.Single(page.Sections);
        Assert.Equal(BrowseSectionKind.Shelf, section.Kind);
        Assert.Equal(8, section.Total);                 // the SERVER total, not the returned count
        var card = Assert.Single(section.Cards);        // the NotFound item is gone
        Assert.Equal("Real", card.Title);
    }

    // A grid section's items are further CATEGORIES (browse is a tree), not entity cards.
    [Fact]
    public void Page_GridSectionYieldsCategoriesNotCards()
    {
        var json = """
        {"data":{"browse":{"uri":"spotify:page:music","header":{"title":{"transformedLabel":"Music"},"color":{"hex":"#DC148C"}},
          "sections":{"totalCount":1,"items":[
            {"uri":"spotify:section:grid","data":{"__typename":"BrowseGridSectionData","title":{"transformedLabel":"Browse all"}},
             "sectionItems":{"totalCount":57,"items":[
               {"uri":"spotify:page:pop","content":{"data":{"__typename":"BrowseSectionContainer",
                  "data":{"cardRepresentation":{"backgroundColor":{"hex":"#477d95"},"title":{"transformedLabel":"Pop"}}}}}}
             ]}}
          ]}}}}
        """;
        var section = Assert.Single(SpotifyBrowseMapper.Page(Root(json), "x").Sections);
        Assert.Equal(BrowseSectionKind.CategoryGrid, section.Kind);
        Assert.Empty(section.Cards);
        Assert.Equal("Pop", Assert.Single(section.Categories).Title);
        Assert.Equal(57, section.Total);
    }

    [Fact]
    public void SectionPage_MapsItemsForTheShowAllAxis()
    {
        var json = """
        {"data":{"browseSection":{"uri":"spotify:section:s1",
          "data":{"__typename":"BrowseGenericSectionData","title":{"transformedLabel":"Discover new music"}},
          "sectionItems":{"totalCount":3,"pagingInfo":{"nextOffset":null},"items":[
            {"content":{"data":{"__typename":"Playlist","uri":"spotify:playlist:a","name":"New Music Friday"}}}
          ]}}}}
        """;
        var section = SpotifyBrowseMapper.SectionPage(Root(json));
        Assert.NotNull(section);
        Assert.Equal("Discover new music", section!.Title);
        Assert.Equal("New Music Friday", Assert.Single(section.Cards).Title);
        // pagingInfo.nextOffset is explicitly null here — the PagingComplete sentinel, not the "no cursor" null.
        Assert.Equal(BrowseSection.PagingComplete, section.NextOffset);
    }

    // Fiddler-captured browseSection walk (Weekly Song Charts, totalCount 74): offset 20 → nextOffset 40, a real
    // mid-walk cursor value the mapper must pass through untouched.
    [Fact]
    public void SectionPage_MapsARealNextOffset_ForAMidWalkPage()
    {
        var json = """
        {"data":{"browseSection":{"uri":"spotify:section:weekly",
          "data":{"__typename":"BrowseGenericSectionData","title":{"transformedLabel":"Weekly Song Charts"}},
          "sectionItems":{"totalCount":74,"pagingInfo":{"nextOffset":40},"items":[
            {"content":{"data":{"__typename":"Playlist","uri":"spotify:playlist:a","name":"El Salvador"}}}
          ]}}}}
        """;
        var section = SpotifyBrowseMapper.SectionPage(Root(json));
        Assert.NotNull(section);
        Assert.Equal(40, section!.NextOffset);
        Assert.Equal(74, section.Total);
    }

    // The captured sequence's LAST page: offset 60 returns 14 items, nextOffset EXPLICITLY null, totalCount 74
    // (60 + 14 == 74 — the total and the terminator agree here). The mapper still records the PagingComplete
    // sentinel rather than folding it into "absent", so a consumer never confuses this with a legacy response that
    // carried no pagingInfo at all.
    [Fact]
    public void SectionPage_LastPage_MapsExplicitNullAsPagingComplete()
    {
        var json = """
        {"data":{"browseSection":{"uri":"spotify:section:weekly",
          "data":{"__typename":"BrowseGenericSectionData","title":{"transformedLabel":"Weekly Song Charts"}},
          "sectionItems":{"totalCount":74,"pagingInfo":{"nextOffset":null},"items":[
            {"content":{"data":{"__typename":"Playlist","uri":"spotify:playlist:a","name":"Last One"}}}
          ]}}}}
        """;
        var section = SpotifyBrowseMapper.SectionPage(Root(json));
        Assert.NotNull(section);
        Assert.Equal(BrowseSection.PagingComplete, section!.NextOffset);
        Assert.Equal(74, section.Total);
    }

    // No pagingInfo object at all — a legacy/degenerate shape. The mapper must NOT invent PagingComplete here: the
    // caller needs to fall back to the synthesized offset+count-vs-total cursor instead of treating this as "done".
    [Fact]
    public void SectionPage_NoPagingInfoAtAll_MapsToPlainNull()
    {
        var json = """
        {"data":{"browseSection":{"uri":"spotify:section:s1",
          "data":{"__typename":"BrowseGenericSectionData","title":{"transformedLabel":"Discover new music"}},
          "sectionItems":{"totalCount":3,"items":[
            {"content":{"data":{"__typename":"Playlist","uri":"spotify:playlist:a","name":"New Music Friday"}}}
          ]}}}}
        """;
        var section = SpotifyBrowseMapper.SectionPage(Root(json));
        Assert.NotNull(section);
        Assert.Null(section!.NextOffset);
    }

    [Fact]
    public void Categories_MalformedColour_DegradesToNullRatherThanAWrongColour()
    {
        var json = """
        {"data":{"browseStart":{"sections":{"items":[{"sectionItems":{"items":[
          {"uri":"spotify:page:x","content":{"data":{"__typename":"BrowseSectionContainer",
            "data":{"cardRepresentation":{"backgroundColor":{"hex":"not-a-colour"},"title":{"transformedLabel":"X"}}}}}}
        ]}}]}}}}
        """;
        Assert.Null(Assert.Single(SpotifyBrowseMapper.Categories(Root(json))).Color);
    }
}
