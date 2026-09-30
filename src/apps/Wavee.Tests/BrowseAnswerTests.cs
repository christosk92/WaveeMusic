// ── Wavee.Tests/BrowseAnswerTests.cs — the browse answers the query layer walks (RCA 2026-09-30) ───────────────────────
//
// Four root causes, one file:
//   1. the official `browseSection` answer carries NO uri — the band is keyed on the uri ASKED for, and a
//      GenericError / NotFound root is a definite (known, empty) answer instead of a miss re-asked three times;
//   2. the SINGLE-SECTION rule — a browsePage that RETURNED one section (its totalCount may say 2) is that section's
//      whole list, `browseSection` walked to the server's end, the page's own band the fallback;
//   3. the page's SECTION list is walked on `sections.pagingInfo.nextOffset` to its end — never stopped on "fewer
//      than the limit came back";
//   4. a generic shelf whose items are category wrappers stages TILES instead of a shelf every item of which drops.
//
// And the drill page's section, demanded WHOLE (`SectionFields.Whole`): the plain section route — `browseSection` and
// `homeSection` alike — walks `sectionItems.pagingInfo.nextOffset` to the server's end and lands every card in one
// Complete run, so no page ever pages a section itself.
//
// The provider halves run through `Spotify.Api.BrowsePageAnswer` / `BrowseSectionAnswer` / `HomeSectionAnswer` with
// canned bodies in place of the transport (`Spotify.Api.BrowseFetch`) — the same walk, rule and decode the live provider
// runs. The bodies below are HAND-WRITTEN, minimal, and shaped like the official responses: `data.browseSection` =
// `{ __typename, data{ __typename, title, subtitle }, sectionItems{ items, pagingInfo{ nextOffset }, totalCount } }` with
// no uri; `data.browse` = `{ __typename, uri, header, sections{ items, pagingInfo, totalCount } }`;
// `data.homeSections` = `{ __typename: HomeSectionCollection, sections: [ { __typename: HomeSection, uri, data,
// sectionItems } ] }`.

using System.Globalization;
using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>The pure halves: the walk's terminator and the single-section rule.</summary>
public class BrowseWalkRuleTests
{
    [Fact]
    public void A_cursor_past_the_request_is_the_next_page()
        => Assert.Equal(10, BrowseWalk.Next(requestedOffset: 0, nextOffset: 10, pagesTaken: 1));

    [Fact]
    public void An_explicit_null_ends_the_walk()
        => Assert.Equal(BrowseWalk.Stop, BrowseWalk.Next(0, SectionPaging.Complete, 1));

    [Fact]
    public void No_paging_info_ends_the_walk()
        => Assert.Equal(BrowseWalk.Stop, BrowseWalk.Next(0, SectionPaging.NoCursor, 1));

    [Fact]
    public void A_cursor_that_does_not_move_past_the_request_ends_the_walk()
    {
        // A complete section answers `nextOffset: 0`; a cursor equal to the request would re-read the same page forever.
        Assert.Equal(BrowseWalk.Stop, BrowseWalk.Next(20, 0, 2));
        Assert.Equal(BrowseWalk.Stop, BrowseWalk.Next(20, 20, 2));
    }

    [Fact]
    public void The_page_cap_ends_a_cursor_that_never_ends_and_says_it_was_not_the_server()
    {
        Assert.Equal(BrowseWalk.Stop, BrowseWalk.Next(0, 10, BrowseWalk.MaxPages));
        Assert.False(BrowseWalk.EndedByServer(0, 10));
        Assert.True(BrowseWalk.EndedByServer(0, SectionPaging.Complete));
        Assert.True(BrowseWalk.EndedByServer(0, SectionPaging.NoCursor));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(14, false)]
    public void Only_ONE_returned_section_is_a_single_section_page(int returned, bool applies)
        => Assert.Equal(applies, BrowseSingleSection.Applies(returned));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(40, true)]
    public void Only_a_walk_that_produced_items_replaces_the_pages_band(int items, bool whole)
        => Assert.Equal(whole, BrowseSingleSection.UseWalk(items));
}

[Collection(EntitiesCollection.Name)]
public class BrowseAnswerTests
{
    const string SeventiesUri = "spotify:page:0JQ5DAqbMKFDV5g1eYRLQy";          // Browse › 70s (the RCA's page)
    const string MusicUri = "spotify:page:wavee-test-music";
    const string SectionUri = "spotify:section:wavee-test-popular-70s";

    // ── the hand-written bodies ─────────────────────────────────────────────────────────────────────────────────────

    static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A valid 22-character base62 playlist id per index (a gid the decoder packs, like a real card).</summary>
    static string PlaylistUri(int i) => "spotify:playlist:37i9dQZF1DX" + i.ToString("D11", CultureInfo.InvariantCulture);

    static string PlaylistItems(int from, int count)
    {
        var items = new StringBuilder();
        for (int i = from; i < from + count; i++)
        {
            if (items.Length > 0) items.Append(',');
            items.Append("""{"uri":"@URI@","content":{"__typename":"PlaylistResponseWrapper","data":{"__typename":"Playlist","uri":"@URI@","name":"@NAME@"}}}"""
                .Replace("@URI@", PlaylistUri(i)).Replace("@NAME@", "Playlist " + N(i)));
        }
        return items.ToString();
    }

    /// <summary>A sub-category card: <c>content.data.data.cardRepresentation</c> under a BrowseSectionContainerWrapper.</summary>
    static string CategoryItem(string pageId, string title)
        => """{"uri":"spotify:page:@ID@","content":{"__typename":"BrowseSectionContainerWrapper","data":{"__typename":"BrowseSectionContainer","data":{"cardRepresentation":{"title":{"transformedLabel":"@TITLE@"},"backgroundColor":{"hex":"#477D95"},"artwork":{"sources":[{"url":"https://i.scdn.co/image/@ID@","width":300,"height":300}]}}}}}}"""
            .Replace("@ID@", pageId).Replace("@TITLE@", title);

    /// <summary>A client feature card (Live Events) under a BrowseXlinkResponseWrapper — routed by its featureUri.</summary>
    const string LiveEventsItem =
        """{"uri":"spotify:xlink:wavee-test-live","content":{"__typename":"BrowseXlinkResponseWrapper","data":{"__typename":"BrowseClientFeature","featureUri":"spotify:concerts","title":{"transformedLabel":"Live Events"},"backgroundColor":{"hex":"#8400E7"}}}}""";

    /// <summary>The official browseSection answer: NO uri anywhere at its root.</summary>
    static byte[] SectionBody(string title, string items, string next, int total) => Utf8("""
        {"data":{"browseSection":{"__typename":"BrowseSection",
          "data":{"__typename":"BrowseGenericSectionData","title":{"transformedLabel":"@TITLE@"},"subtitle":null},
          "sectionItems":{"items":[@ITEMS@],"pagingInfo":{"nextOffset":@NEXT@},"totalCount":@TOTAL@}}}}
        """.Replace("@TITLE@", title).Replace("@NEXT@", next).Replace("@TOTAL@", N(total)).Replace("@ITEMS@", items));

    /// <summary>One band of a browsePage answer.</summary>
    static string Band(string uri, string title, string items, string next, int total,
                       string typename = "BrowseGenericSectionData")
        => """{"uri":"@URI@","data":{"__typename":"@TYPE@","title":{"transformedLabel":"@TITLE@"}},"sectionItems":{"items":[@ITEMS@],"pagingInfo":{"nextOffset":@NEXT@},"totalCount":@TOTAL@}}"""
            .Replace("@URI@", uri).Replace("@TYPE@", typename).Replace("@TITLE@", title).Replace("@NEXT@", next)
            .Replace("@TOTAL@", N(total)).Replace("@ITEMS@", items);

    /// <summary><paramref name="count"/> two-card shelf bands, section uris numbered from <paramref name="from"/>.</summary>
    static string Bands(int from, int count)
    {
        var bands = new StringBuilder();
        for (int i = from; i < from + count; i++)
        {
            if (bands.Length > 0) bands.Append(',');
            bands.Append(Band("spotify:section:wavee-test-music-" + N(i), "Band " + N(i), PlaylistItems(i * 2, 2), "null", 2));
        }
        return bands.ToString();
    }

    /// <summary>The browsePage answer around <paramref name="sections"/>.</summary>
    static byte[] PageBody(string pageUri, string title, string sections, string next, int total) => Utf8("""
        {"data":{"browse":{"__typename":"BrowseSectionContainer","uri":"@PAGE@",
          "header":{"title":{"transformedLabel":"@TITLE@"},"color":{"hex":"#1E3264"}},
          "sections":{"items":[@SECTIONS@],"pagingInfo":{"nextOffset":@NEXT@},"totalCount":@TOTAL@}}}}
        """.Replace("@PAGE@", pageUri).Replace("@TITLE@", title).Replace("@NEXT@", next).Replace("@TOTAL@", N(total))
           .Replace("@SECTIONS@", sections));

    static Spotify.Api.Result Ok(byte[] body) => new(200, body);

    static Spotify.Api.Result Status(int status) => new(status, Array.Empty<byte>());

    static Spotify.Api.Result NeverAsked(string uri, int offset)
        => throw new InvalidOperationException("browseSection must not be read here (" + uri + " @" + N(offset) + ")");

    static int PlaylistSlot(int i) => Entities.Playlist(EntityUri.Parse(PlaylistUri(i).AsSpan())).Slot;

    // ── 1. browseSection keyed on the uri asked for; definite roots ─────────────────────────────────────────────────

    [Fact]
    public void A_browse_section_body_without_a_root_uri_lands_on_the_section_that_was_asked_for()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var answer = Spotify.Decode.BrowseSection(SectionBody("Popular 70s playlists", PlaylistItems(0, 3), "null", 3),
                                                  Utf8(SectionUri), 0, s);
        Assert.Equal(Spotify.Decode.BrowseRoot.Content, answer.Root);
        Assert.Equal("BrowseSection", answer.RootType);
        Assert.False(answer.Section.IsEmpty);
        Assert.Equal(1, answer.BandsKept);
        Assert.Equal(3, answer.ItemsReturned);
        Assert.False(answer.UriMismatch);
        TestScope.CommitAndPublish(s);

        var band = Entities.Section(SectionUri.AsSpan());
        Assert.True(band.Knows(SectionFields.Identity));
        Assert.Equal("Popular 70s playlists", Entities.Strings.Resolve(band.TitleId));
        Assert.Equal(SectionKind.BrowseShelf, band.Kind);
        Assert.Equal(3, band.CardSlots.Length);
        Assert.Equal(PlaylistSlot(0), band.CardSlots[0]);
        Assert.Equal(SectionPaging.Complete, band.NextOffset);                     // `nextOffset: null` is the END
        Assert.False(band.HasMore);
    }

    [Fact]
    public void A_body_uri_that_disagrees_is_only_recorded_the_band_stays_on_the_uri_asked_for()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var body = Utf8("""
            {"data":{"browseSection":{"__typename":"BrowseSection","uri":"spotify:section:wavee-test-someone-else",
              "data":{"__typename":"BrowseGenericSectionData","title":{"transformedLabel":"Mine"}},
              "sectionItems":{"items":[],"totalCount":0}}}}
            """);
        var answer = Spotify.Decode.BrowseSection(body, Utf8(SectionUri), 0, s);
        Assert.True(answer.UriMismatch);
        TestScope.CommitAndPublish(s);

        Assert.True(Entities.Section(SectionUri.AsSpan()).Knows(SectionFields.Identity));
        Assert.False(Entities.Current.Sections.TryGetSlot("spotify:section:wavee-test-someone-else".AsSpan(), out _));
    }

    [Theory]
    [InlineData("NotFound", Spotify.Decode.BrowseRoot.NotFound)]
    [InlineData("GenericError", Spotify.Decode.BrowseRoot.GenericError)]
    public void A_NotFound_or_GenericError_section_is_a_known_empty_band_not_a_miss(string typename, Spotify.Decode.BrowseRoot root)
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var answer = Spotify.Decode.BrowseSection(Utf8("""{"data":{"browseSection":{"__typename":"@T@"}}}""".Replace("@T@", typename)),
                                                  Utf8(SectionUri), 0, s);
        Assert.Equal(root, answer.Root);
        Assert.True(answer.Definite);
        Assert.Equal(typename, answer.RootType);
        TestScope.CommitAndPublish(s);

        var band = Entities.Section(SectionUri.AsSpan());
        Assert.True(band.Knows(SectionFields.Identity));                            // answered: the planner seals it
        Assert.True(band.CardSlots.IsEmpty);
        Assert.Equal(EdgeState.Complete, band.CardState);                           // "nothing here" is an answer
        Assert.Equal(SectionPaging.Complete, band.NextOffset);
    }

    [Fact]
    public void A_NotFound_at_a_later_offset_stages_nothing_the_pages_already_landed_stand()
    {
        TestScope.Fresh();
        var first = Staging.Rent();
        Spotify.Decode.BrowseSection(SectionBody("Popular 70s playlists", PlaylistItems(0, 3), "3", 10), Utf8(SectionUri), 0, first);
        TestScope.CommitAndPublish(first);

        var later = Staging.Rent();
        var answer = Spotify.Decode.BrowseSection(Utf8("""{"data":{"browseSection":{"__typename":"NotFound"}}}"""),
                                                  Utf8(SectionUri), 3, later);
        Assert.True(answer.Definite);
        Assert.True(answer.Section.IsEmpty);
        TestScope.CommitAndPublish(later);

        var band = Entities.Section(SectionUri.AsSpan());
        Assert.Equal(3, band.CardSlots.Length);
        Assert.Equal("Popular 70s playlists", Entities.Strings.Resolve(band.TitleId));
    }

    [Fact]
    public void A_GenericError_page_is_a_known_page_with_an_empty_complete_section_list()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var answer = Spotify.Decode.BrowsePage(Utf8("""{"data":{"browse":{"__typename":"GenericError"}}}"""), Utf8(SeventiesUri), 0, s);
        Assert.Equal(Spotify.Decode.BrowseRoot.GenericError, answer.Root);
        TestScope.CommitAndPublish(s);

        var page = Entities.BrowseNode(SeventiesUri.AsSpan());
        Assert.True(page.Knows(BrowseFields.Page));
        Assert.Equal(0, Entities.Current.Edges.BrowseSections.Count(page.Slot));
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.BrowseSections.State(page.Slot));
        Assert.Equal(SectionPaging.Complete, page.NextSectionOffset);
    }

    [Fact]
    public void A_200_with_no_union_stages_nothing_and_says_why()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var page = Spotify.Decode.BrowsePage(
            Utf8("""{"errors":[{"message":"Something went wrong","path":["browse"]}],"data":{"browse":null}}"""), Utf8(SeventiesUri), 0, s);
        var section = Spotify.Decode.BrowseSection(
            Utf8("""{"data":null,"errors":[{"message":"Persisted query not found"}]}"""), Utf8(SectionUri), 0, s);
        Assert.Equal(Spotify.Decode.BrowseRoot.Missing, page.Root);
        Assert.Equal("Something went wrong", page.Error);
        Assert.Null(page.RootType);
        Assert.Equal(Spotify.Decode.BrowseRoot.Missing, section.Root);
        Assert.Equal("Persisted query not found", section.Error);
        Assert.False(section.Definite);
        TestScope.CommitAndPublish(s);

        Assert.False(Entities.BrowseNode(SeventiesUri.AsSpan()).Knows(BrowseFields.Page));
        Assert.False(Entities.Section(SectionUri.AsSpan()).Knows(SectionFields.Identity));
    }

    // ── 4. category wrappers inside a generic shelf ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_generic_shelf_of_category_wrappers_stages_category_tiles_instead_of_dropping()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        string tiles = LiveEventsItem + "," + CategoryItem("wavee-test-disco", "Disco") + "," + CategoryItem("wavee-test-glam", "Glam rock");
        string bands = Band("spotify:section:wavee-test-moods", "Moods", tiles, "null", 3) + ","
                     + Band("spotify:section:wavee-test-lists", "Popular playlists", PlaylistItems(0, 2), "null", 2);
        var answer = Spotify.Decode.BrowsePage(PageBody(SeventiesUri, "70s", bands, "null", 2), Utf8(SeventiesUri), 0, s);
        Assert.Equal(2, answer.SectionsReturned);
        Assert.Equal(2, answer.BandsKept);
        Assert.Equal(0, answer.BandsDropped);
        TestScope.CommitAndPublish(s);

        var moods = Entities.Section("spotify:section:wavee-test-moods".AsSpan());
        Assert.Equal(SectionKind.BrowseCategoryGrid, moods.Kind);                   // tiles, whatever the typename said
        Assert.Equal(3, moods.Cards);
        Assert.Equal(0, moods.Unsupported);
        Assert.True(moods.CardSlots.IsEmpty);
        var categories = Entities.Current.Edges.SectionCategories;
        Assert.Equal(3, categories.Count(moods.Slot));
        var live = new Browse(categories.Targets(moods.Slot)[0]);
        Assert.True(live.IsClientFeature);                                          // the xlink routes by its featureUri
        Assert.Equal("spotify:concerts", live.Id.Text);
        var disco = Entities.BrowseNode("spotify:page:wavee-test-disco".AsSpan());
        Assert.Equal(disco.Slot, categories.Targets(moods.Slot)[1]);
        Assert.Equal("Disco", Entities.Strings.Resolve(disco.TitleId));

        var lists = Entities.Section("spotify:section:wavee-test-lists".AsSpan());
        Assert.Equal(SectionKind.BrowseShelf, lists.Kind);                           // an entity shelf stays a shelf
        Assert.Equal(2, lists.CardSlots.Length);
    }

    // ── 3. the page's sections, walked to the server's end ──────────────────────────────────────────────────────────

    [Fact]
    public void A_page_walk_follows_the_section_cursor_and_stops_on_null_even_when_fewer_than_the_limit_came_back()
    {
        TestScope.Fresh();
        var asked = new List<int>();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.BrowsePageAnswer(MusicUri, 0, s, ref outcome, 0,
            (uri, offset) =>
            {
                asked.Add(offset);
                return offset switch
                {
                    0 => Ok(PageBody(MusicUri, "Music", Bands(0, 3), "3", 5)),          // 3 < the limit of 10: NOT the end
                    3 => Ok(PageBody(MusicUri, "Music", Bands(3, 2), "null", 5)),       // the server's end
                    _ => Status(500),
                };
            },
            NeverAsked);                                                                // five sections: not single-section
        TestScope.CommitAndPublish(s);

        Assert.Equal(new[] { 0, 3 }, asked);
        Assert.Equal(2, outcome.Answered);
        Assert.False(outcome.Fails(0, out _, out _));
        var page = Entities.BrowseNode(MusicUri.AsSpan());
        var sections = Entities.Current.Edges.BrowseSections;
        Assert.Equal(5, sections.Count(page.Slot));
        Assert.Equal(EdgeState.Complete, sections.State(page.Slot));
        Assert.Equal(Entities.Section("spotify:section:wavee-test-music-4".AsSpan()).Slot, sections.Targets(page.Slot)[4]);
        Assert.Equal(SectionPaging.Complete, page.NextSectionOffset);
    }

    [Fact]
    public void A_later_section_page_that_fails_retryably_fails_the_batch_until_the_last_attempt()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.BrowsePageAnswer(MusicUri, 0, s, ref outcome, 0,
            (uri, offset) => offset == 0 ? Ok(PageBody(MusicUri, "Music", Bands(0, 2), "2", 4)) : Status(503),
            NeverAsked);
        Staging.Return(s);

        Assert.True(outcome.Fails(0, out int status, out _));                         // re-run behind the backoff
        Assert.Equal(503, status);
        Assert.False(outcome.Fails(Fetch.MaxAttempts - 1, out _, out _));             // the last attempt commits what landed
    }

    // ── 2. the single-section rule ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_page_that_returned_one_section_is_that_sections_whole_list_walked_to_its_end()
    {
        TestScope.Fresh();
        var asked = new List<string>();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        // ONE section returned, though the page's totalCount says 2 (as a captured single-section page does); its band
        // carries the page's first 2 items and a cursor at 10.
        byte[] pageBody = PageBody(SeventiesUri, "70s",
            Band(SectionUri, "Popular 70s playlists", PlaylistItems(0, 2), "10", 40), "null", 2);
        Spotify.Api.BrowsePageAnswer(SeventiesUri, 0, s, ref outcome, 0,
            (uri, offset) => { asked.Add("page@" + N(offset)); return Ok(pageBody); },
            (uri, offset) =>
            {
                asked.Add(uri + "@" + N(offset));
                return offset switch
                {
                    0 => Ok(SectionBody("Popular 70s playlists", PlaylistItems(0, 20), "20", 40)),
                    20 => Ok(SectionBody("Popular 70s playlists", PlaylistItems(20, 20), "null", 40)),
                    _ => Status(500),
                };
            });
        TestScope.CommitAndPublish(s);

        Assert.Equal(new[] { "page@0", SectionUri + "@0", SectionUri + "@20" }, asked);
        Assert.Equal(1, outcome.Answered);                                             // the walk is never the batch's verdict
        var page = Entities.BrowseNode(SeventiesUri.AsSpan());
        var sections = Entities.Current.Edges.BrowseSections;
        Assert.Equal(1, sections.Count(page.Slot));
        var section = new Section(sections.Targets(page.Slot)[0]);
        Assert.Equal(SectionUri, section.Id.Text);
        Assert.Equal("Popular 70s playlists", Entities.Strings.Resolve(section.TitleId));
        Assert.Equal(SectionKind.BrowseShelf, section.Kind);
        Assert.Equal(40, section.CardSlots.Length);                                    // the WHOLE list, not the page's 2
        Assert.Equal(PlaylistSlot(0), section.CardSlots[0]);
        Assert.Equal(PlaylistSlot(39), section.CardSlots[39]);
        Assert.Equal(40, section.Raw);
        Assert.Equal(SectionPaging.Complete, section.NextOffset);
        Assert.False(section.HasMore);
        Assert.Equal(EdgeState.Complete, section.CardState);
    }

    [Fact]
    public void A_single_section_of_category_wrappers_lands_its_whole_tile_list()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        byte[] pageBody = PageBody(SeventiesUri, "Moods",
            Band(SectionUri, "Moods", CategoryItem("wavee-test-disco", "Disco"), "1", 2), "null", 1);
        Spotify.Api.BrowsePageAnswer(SeventiesUri, 0, s, ref outcome, 0,
            (uri, offset) => Ok(pageBody),
            (uri, offset) => offset == 0
                ? Ok(SectionBody("Moods", CategoryItem("wavee-test-disco", "Disco") + "," + CategoryItem("wavee-test-glam", "Glam rock"), "null", 2))
                : Status(500));
        TestScope.CommitAndPublish(s);

        var section = Entities.Section(SectionUri.AsSpan());
        Assert.Equal(SectionKind.BrowseCategoryGrid, section.Kind);                    // category-tile density
        Assert.Equal(2, Entities.Current.Edges.SectionCategories.Count(section.Slot));
        Assert.Equal(SectionPaging.Complete, section.NextOffset);
    }

    [Fact]
    public void A_single_section_whose_walk_is_NotFound_keeps_the_pages_own_band()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        byte[] pageBody = PageBody(SeventiesUri, "70s",
            Band(SectionUri, "Popular 70s playlists", PlaylistItems(0, 2), "10", 40), "null", 1);
        Spotify.Api.BrowsePageAnswer(SeventiesUri, 0, s, ref outcome, 0,
            (uri, offset) => Ok(pageBody),
            (uri, offset) => Ok(Utf8("""{"data":{"browseSection":{"__typename":"NotFound"}}}""")));
        TestScope.CommitAndPublish(s);

        var section = Entities.Section(SectionUri.AsSpan());
        Assert.Equal(2, section.CardSlots.Length);                                     // the fallback: the page's band
        Assert.Equal(10, section.NextOffset);
        Assert.Equal("Popular 70s playlists", Entities.Strings.Resolve(section.TitleId));
    }

    [Fact]
    public void A_single_section_whose_walk_fails_keeps_the_pages_band_and_never_fails_the_batch()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        byte[] pageBody = PageBody(SeventiesUri, "70s",
            Band(SectionUri, "Popular 70s playlists", PlaylistItems(0, 2), "10", 40), "null", 1);
        Spotify.Api.BrowsePageAnswer(SeventiesUri, 0, s, ref outcome, 0,
            (uri, offset) => Ok(pageBody),
            (uri, offset) => Status(503));
        TestScope.CommitAndPublish(s);

        Assert.False(outcome.Fails(0, out _, out _));                                  // the page shows at once, no backoff
        Assert.Equal(2, Entities.Section(SectionUri.AsSpan()).CardSlots.Length);
    }

    // ── the drill's section, demanded WHOLE: the plain section route walks it to its end ───────────────────────────────

    const string HomeSectionUri = "spotify:section:wavee-test-made-for-you";

    /// <summary>The official homeSection answer: the collection, its one band (which DOES carry its uri), the items.</summary>
    static byte[] HomeBody(string title, string items, string next, int total) => Utf8("""
        {"data":{"homeSections":{"__typename":"HomeSectionCollection","sections":[{"__typename":"HomeSection","uri":"@URI@",
          "data":{"__typename":"HomeGenericSectionData","title":{"transformedLabel":"@TITLE@"},"subtitle":null},
          "sectionItems":{"items":[@ITEMS@],"pagingInfo":{"nextOffset":@NEXT@},"totalCount":@TOTAL@}}]}}}
        """.Replace("@URI@", HomeSectionUri).Replace("@TITLE@", title).Replace("@NEXT@", next).Replace("@TOTAL@", N(total))
           .Replace("@ITEMS@", items));

    const uint WholeAsk = (uint)SectionFields.All;                                     // the drill's route: Identity | Whole

    [Fact]
    public void A_deck_tiles_section_ask_reads_the_first_page_alone_keyed_on_the_uri_asked_for()
    {
        TestScope.Fresh();
        var asked = new List<int>();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.BrowseSectionAnswer(SectionUri, s, ref outcome, (uint)(SectionFields.Identity | SectionFields.Accent),
            (uri, offset) => { asked.Add(offset); return Ok(SectionBody("Popular 70s playlists", PlaylistItems(0, 20), "20", 40)); });
        TestScope.CommitAndPublish(s);

        Assert.Equal(new[] { 0 }, asked);
        Assert.Equal(1, outcome.Answered);
        var band = Entities.Section(SectionUri.AsSpan());
        Assert.True(band.Knows(SectionFields.Identity));
        Assert.False(band.Knows(SectionFields.Whole));                                 // a first page is never the whole
        Assert.Equal(20, band.CardSlots.Length);
        Assert.Equal(20, band.NextOffset);
    }

    [Fact]
    public void A_whole_browse_section_walks_offsets_0_20_40_and_stops_on_a_null_cursor_even_with_short_pages()
    {
        TestScope.Fresh();
        var asked = new List<int>();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.BrowseSectionAnswer(SectionUri, s, ref outcome, WholeAsk,
            (uri, offset) =>
            {
                Assert.Equal(SectionUri, uri);
                asked.Add(offset);
                return offset switch
                {
                    0 => Ok(SectionBody("Popular 70s playlists", PlaylistItems(0, 20), "20", 60)),
                    20 => Ok(SectionBody("Popular 70s playlists", PlaylistItems(20, 12), "40", 60)),  // 12 < 20: NOT the end
                    40 => Ok(SectionBody("Popular 70s playlists", PlaylistItems(40, 5), "null", 60)), // the server's end
                    _ => Status(500),
                };
            });
        TestScope.CommitAndPublish(s);

        Assert.Equal(new[] { 0, 20, 40 }, asked);
        Assert.Equal(3, outcome.Answered);                                             // every request of the walk is noted
        Assert.False(outcome.Fails(0, out _, out _));
        var band = Entities.Section(SectionUri.AsSpan());
        Assert.True(band.Knows(SectionFields.Identity | SectionFields.Whole));
        Assert.Equal("Popular 70s playlists", Entities.Strings.Resolve(band.TitleId));
        Assert.Equal(37, band.CardSlots.Length);                                       // 20 + 12 + 5: every card, one run
        Assert.Equal(PlaylistSlot(0), band.CardSlots[0]);
        Assert.Equal(PlaylistSlot(20), band.CardSlots[20]);
        Assert.Equal(PlaylistSlot(44), band.CardSlots[36]);
        Assert.Equal(EdgeState.Complete, band.CardState);
        Assert.Equal(37, band.Raw);
        Assert.Equal(SectionPaging.Complete, band.NextOffset);
        Assert.False(band.HasMore);
    }

    [Fact]
    public void A_whole_section_walk_is_capped_and_its_cursor_says_the_server_has_more()
    {
        TestScope.Fresh();
        int requests = 0;
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.BrowseSectionAnswer(SectionUri, s, ref outcome, WholeAsk,
            (uri, offset) =>
            {
                requests++;
                return Ok(SectionBody("Endless", PlaylistItems(offset, 1), N(offset + 1), 5000));   // a cursor that never ends
            });
        TestScope.CommitAndPublish(s);

        Assert.Equal(BrowseWalk.MaxPages, requests);
        var band = Entities.Section(SectionUri.AsSpan());
        Assert.True(band.Knows(SectionFields.Whole));
        Assert.Equal(BrowseWalk.MaxPages, band.CardSlots.Length);
        Assert.Equal(BrowseWalk.MaxPages, band.NextOffset);                            // the ledger never claims more than it read
        Assert.True(band.HasMore);
    }

    [Fact]
    public void A_whole_section_whose_first_page_is_NotFound_is_a_known_empty_whole_band()
    {
        TestScope.Fresh();
        var asked = new List<int>();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.BrowseSectionAnswer(SectionUri, s, ref outcome, WholeAsk,
            (uri, offset) => { asked.Add(offset); return Ok(Utf8("""{"data":{"browseSection":{"__typename":"NotFound"}}}""")); });
        TestScope.CommitAndPublish(s);

        Assert.Equal(new[] { 0 }, asked);
        var band = Entities.Section(SectionUri.AsSpan());
        Assert.True(band.Knows(SectionFields.Identity | SectionFields.Whole));      // answered: sealed, never re-asked
        Assert.True(band.CardSlots.IsEmpty);
        Assert.Equal(EdgeState.Complete, band.CardState);
        Assert.Equal(SectionKind.BrowseShelf, band.Kind);
    }

    [Fact]
    public void A_whole_section_walk_that_fails_later_fails_the_batch_until_the_last_attempt_then_lands_what_it_read()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.BrowseSectionAnswer(SectionUri, s, ref outcome, WholeAsk,
            (uri, offset) => offset == 0 ? Ok(SectionBody("Popular 70s playlists", PlaylistItems(0, 20), "20", 40)) : Status(503));

        Assert.True(outcome.Fails(0, out int status, out _));                         // re-run behind the backoff
        Assert.Equal(503, status);
        Assert.False(outcome.Fails(Fetch.MaxAttempts - 1, out _, out _));             // the last attempt commits what landed
        TestScope.CommitAndPublish(s);

        var band = Entities.Section(SectionUri.AsSpan());
        Assert.Equal(20, band.CardSlots.Length);
        Assert.Equal(20, band.NextOffset);                                             // where the walk stopped
        Assert.True(band.HasMore);
    }

    [Fact]
    public void A_whole_home_section_walks_offsets_0_20_40_and_stops_on_a_null_cursor_keyed_on_the_uri_asked_for()
    {
        TestScope.Fresh();
        var asked = new List<int>();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.HomeSectionAnswer(HomeSectionUri, s, ref outcome, WholeAsk,
            (uri, offset) =>
            {
                Assert.Equal(HomeSectionUri, uri);
                asked.Add(offset);
                return offset switch
                {
                    0 => Ok(HomeBody("Made for you", PlaylistItems(0, 20), "20", 64)),
                    20 => Ok(HomeBody("Made for you", PlaylistItems(20, 9), "40", 64)),     // short: NOT the end
                    40 => Ok(HomeBody("Made for you", PlaylistItems(40, 3), "null", 64)),   // the server's end
                    _ => Status(500),
                };
            });
        TestScope.CommitAndPublish(s);

        Assert.Equal(new[] { 0, 20, 40 }, asked);
        Assert.Equal(3, outcome.Answered);
        var band = Entities.Section(HomeSectionUri.AsSpan());
        Assert.True(band.Knows(SectionFields.Identity | SectionFields.Whole));
        Assert.Equal("Made for you", Entities.Strings.Resolve(band.TitleId));
        Assert.Equal(SectionKind.HomeGeneric, band.Kind);                              // still a Home band: routes to homeSection
        Assert.Equal(32, band.CardSlots.Length);                                       // 20 + 9 + 3
        Assert.Equal(PlaylistSlot(0), band.CardSlots[0]);
        Assert.Equal(PlaylistSlot(42), band.CardSlots[31]);
        Assert.Equal(EdgeState.Complete, band.CardState);
        Assert.Equal(32, band.Raw);
        Assert.Equal(SectionPaging.Complete, band.NextOffset);
        Assert.False(band.HasMore);
    }

    [Fact]
    public void A_complete_home_section_answering_nextOffset_0_is_one_request()
    {
        TestScope.Fresh();
        var asked = new List<int>();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.HomeSectionAnswer(HomeSectionUri, s, ref outcome, WholeAsk,
            (uri, offset) => { asked.Add(offset); return Ok(HomeBody("Made for you", PlaylistItems(0, 6), "0", 6)); });
        TestScope.CommitAndPublish(s);

        Assert.Equal(new[] { 0 }, asked);                                              // `0` does not move past the request
        var band = Entities.Section(HomeSectionUri.AsSpan());
        Assert.Equal(6, band.CardSlots.Length);
        Assert.Equal(SectionPaging.Complete, band.NextOffset);
    }

    [Fact]
    public void A_home_section_whose_first_page_is_NotFound_is_a_known_empty_home_band()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.HomeSectionAnswer(HomeSectionUri, s, ref outcome, WholeAsk,
            (uri, offset) => Ok(Utf8("""{"data":{"homeSections":{"__typename":"HomeSectionCollection","sections":[{"__typename":"NotFound"}]}}}""")));
        TestScope.CommitAndPublish(s);

        var band = Entities.Section(HomeSectionUri.AsSpan());
        Assert.True(band.Knows(SectionFields.Identity | SectionFields.Whole));
        Assert.True(band.CardSlots.IsEmpty);
        Assert.Equal(EdgeState.Complete, band.CardState);
        Assert.Equal(SectionKind.HomeGeneric, band.Kind);
    }

    [Fact]
    public void A_first_page_answer_for_a_walked_band_takes_the_whole_list_and_its_ask_back()
    {
        // The drill walked the band whole; then a first-page answer for the same band lands (a feed refresh, a deck
        // tile's re-ask): the row holds a first page again, so it no longer claims Whole — and the drill's next demand
        // really walks it again instead of reading the first page as the whole section.
        TestScope.Fresh();
        var walk = Staging.Rent();
        var outcome = new Spotify.Api.FetchOutcome();
        Spotify.Api.BrowseSectionAnswer(SectionUri, walk, ref outcome, WholeAsk,
            (uri, offset) => offset == 0 ? Ok(SectionBody("Popular 70s playlists", PlaylistItems(0, 20), "20", 25))
                                         : Ok(SectionBody("Popular 70s playlists", PlaylistItems(20, 5), "null", 25)));
        TestScope.CommitAndPublish(walk);
        var band = Entities.Section(SectionUri.AsSpan());
        Entities.Current.Sections.Asked[band.Slot] |= (uint)SectionFields.Whole;     // what the drill's demand marked
        Assert.True(band.Knows(SectionFields.Whole));
        Assert.Equal(25, band.CardSlots.Length);

        var firstPage = Staging.Rent();
        Spotify.Decode.BrowseSection(SectionBody("Popular 70s playlists", PlaylistItems(0, 20), "20", 25), Utf8(SectionUri), 0, firstPage);
        TestScope.CommitAndPublish(firstPage);

        Assert.False(band.Knows(SectionFields.Whole));
        Assert.Equal(0u, Entities.Current.Sections.Asked[band.Slot] & (uint)SectionFields.Whole);
        Assert.True(band.Knows(SectionFields.Identity));
    }
}
