using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee;
using Pl = Wavee.Protocol.Playlist;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Xunit;

namespace Wavee.Tests;

public class PodcastReaderRepairTests
{
    // ── kind-178 chapter titles (§D3.2). No stored binary fixture carried this shape (Fixtures/podcast only had the
    // comments/replies captures), so this builds the minimal payload straight from the field table recorded in
    // findings-podcast-wire.md §3.4: "{1:\"Chapter\", 2: title, 3: description, 4:{1: episode title, 2: episode
    // uri}}". Only field 2 is ever read (Spotify.Podcasts.DecodeChapterTitle), so the fixture supplies 1–3 to prove
    // the decoder skips the type marker and description rather than happening to land on field 2 by accident.
    static Any Kind178Payload(string title, string description = "")
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        // Raw tag bytes (field << 3 | LEN=2), the same idiom the generated ExtendedMetadata.cs WriteTo methods use:
        // field 1 -> 10, field 2 -> 18, field 3 -> 26.
        output.WriteRawTag(10); output.WriteString("Chapter");
        output.WriteRawTag(18); output.WriteString(title);
        output.WriteRawTag(26); output.WriteString(description);
        output.Flush();
        return new Any { Value = ByteString.CopyFrom(stream.ToArray()) };
    }

    static Xm.BatchedExtensionResponse Kind178Response(params (string Uri, string Title, int Status)[] entities)
    {
        var group = new Xm.EntityExtensionDataArray { ExtensionKind = (Xm.ExtensionKind)178 };
        foreach (var (uri, title, status) in entities)
            group.ExtensionData.Add(new Xm.EntityExtensionData
            {
                EntityUri = uri,
                Header = new Xm.EntityExtensionDataHeader { StatusCode = status },
                ExtensionData = Kind178Payload(title),
            });
        var response = new Xm.BatchedExtensionResponse();
        response.ExtendedMetadata.Add(group);
        return response;
    }

    static Pl.Item ChapterItem(string uri, int? startMs, int? endMs = null)
    {
        var item = new Pl.Item { Uri = uri, Attributes = new Pl.ItemAttributes() };
        if (startMs is { } start)
            item.Attributes.FormatAttributes.Add(new Pl.FormatListAttribute
            { Key = "chapter.start_position_in_milliseconds", Value = start.ToString() });
        if (endMs is { } end)
            item.Attributes.FormatAttributes.Add(new Pl.FormatListAttribute
            { Key = "chapter.end_position_in_milliseconds", Value = end.ToString() });
        return item;
    }

    [Fact]
    public void Chapters_from_items_read_start_and_end_from_the_format_attributes()
    {
        var chapters = Spotify.Podcasts.ChaptersFromItems([ChapterItem("spotify:chapter:a", 0, 60_000)]);
        var chapter = Assert.Single(chapters);
        Assert.Equal(0, chapter.StartMs);
        Assert.Equal(60_000, chapter.EndMs);
        Assert.Equal("", chapter.Title);
    }

    [Fact]
    public void Fill_chapter_ends_borrows_the_next_chapters_start_when_the_wire_left_it_unstated()
    {
        Spotify.Podcasts.Chapter[] chapters =
        [
            new("spotify:chapter:a", "", 0, 0),
            new("spotify:chapter:b", "", 60_000, 0),
            new("spotify:chapter:c", "", 120_000, 0),
        ];
        var filled = Spotify.Podcasts.FillChapterEnds(chapters);
        Assert.Equal(60_000, filled[0].EndMs);
        Assert.Equal(120_000, filled[1].EndMs);
        // The final chapter has no later start to borrow — its end stays exactly what the wire gave it.
        Assert.Equal(0, filled[2].EndMs);
    }

    [Fact]
    public void Fill_chapter_ends_keeps_an_end_the_wire_actually_stated()
    {
        Spotify.Podcasts.Chapter[] chapters =
        [
            new("spotify:chapter:a", "", 0, 45_000),
            new("spotify:chapter:b", "", 60_000, 0),
        ];
        var filled = Spotify.Podcasts.FillChapterEnds(chapters);
        Assert.Equal(45_000, filled[0].EndMs);
    }

    [Fact]
    public void Apply_chapter_titles_fills_title_from_kind_178_matched_by_entity_uri()
    {
        Spotify.Podcasts.Chapter[] chapters =
        [
            new("spotify:chapter:a", "", 0, 60_000),
            new("spotify:chapter:b", "", 60_000, 120_000),
        ];
        var response = Kind178Response(("spotify:chapter:b", "Chapter Two", 200), ("spotify:chapter:a", "Chapter One", 200));
        var titled = Spotify.Podcasts.ApplyChapterTitles(chapters, response);
        Assert.Equal("Chapter One", titled[0].Title);
        Assert.Equal("Chapter Two", titled[1].Title);
    }

    [Fact]
    public void Apply_chapter_titles_skips_an_error_header_and_leaves_the_existing_title()
    {
        Spotify.Podcasts.Chapter[] chapters = [new("spotify:chapter:a", "placeholder", 0, 60_000)];
        var response = Kind178Response(("spotify:chapter:a", "Should not land", 404));
        var titled = Spotify.Podcasts.ApplyChapterTitles(chapters, response);
        Assert.Equal("placeholder", Assert.Single(titled).Title);
    }

    [Fact]
    public void Apply_chapter_titles_ignores_a_group_that_is_not_kind_178()
    {
        Spotify.Podcasts.Chapter[] chapters = [new("spotify:chapter:a", "placeholder", 0, 60_000)];
        var group = new Xm.EntityExtensionDataArray { ExtensionKind = (Xm.ExtensionKind)179 };
        group.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:chapter:a", ExtensionData = Kind178Payload("Wrong kind") });
        var response = new Xm.BatchedExtensionResponse();
        response.ExtendedMetadata.Add(group);
        var titled = Spotify.Podcasts.ApplyChapterTitles(chapters, response);
        Assert.Equal("placeholder", Assert.Single(titled).Title);
    }

    [Fact]
    public void Chapter_request_asks_for_every_title_not_only_the_first_uri()
    {
        string[] uris = ["spotify:chapter:first", "spotify:chapter:second", "spotify:chapter:third"];
        var request = Xm.BatchedEntityRequest.Parser.ParseFrom(Spotify.Podcasts.ChapterMetadataBody(uris, "NL", "premium"));
        Assert.Equal(uris, request.EntityRequest.Select(e => e.EntityUri));
        Assert.All(request.EntityRequest, e => Assert.Equal(178, (int)Assert.Single(e.Query).ExtensionKind));
    }

    [Fact]
    public void Captured_comments_page_decodes_without_treating_eligibility_as_failure()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "podcast", "vc4-275-comments.json"));
        var page = Spotify.Podcasts.DecodeComments(bytes, false);
        Assert.True(page.Ok);
        Assert.Equal(2, page.Items.Length);
        Assert.Equal("ELIGIBILITY_STATUS_UNRESTRICTED", page.Eligibility);
        Assert.True(PodcastReaderRules.CanComment(page.Eligibility));
        Assert.Equal("Fixture comment 1.", page.Items[0].Text);
        Assert.Equal("Listener 1", page.Items[0].Author);
        Assert.Empty(page.NextToken);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(186000, "3:06")]
    [InlineData(3601000, "1:00:01")]
    [InlineData(7820000, "2:10:20")]
    [InlineData(-1000, "0:00")]
    public void Chapter_timestamps_include_hours(int milliseconds, string expected)
        => Assert.Equal(expected, PodcastReaderRules.Timestamp(milliseconds));

    [Fact]
    public void Bulk_save_keeps_display_order_and_omits_already_saved_items()
    {
        var operations = Spotify.Podcasts.SavedBatchMutationOps(["a", "b", "c", "a"], true,
            [new PlaylistOps.WireItem("b", "canonical")], 1234, () => "new-id");
        Assert.Equal(new[] { "c", "a" }, operations.Select(op => Assert.Single(op.Items!).Uri));
        Assert.All(operations, op => Assert.True(op.AddFirst));
    }

    [Fact]
    public void Bulk_remove_uses_every_matching_canonical_item_id()
    {
        var operations = Spotify.Podcasts.SavedBatchMutationOps(["a", "b"], false,
            [new PlaylistOps.WireItem("a", "a1"), new PlaylistOps.WireItem("b", "b1"), new PlaylistOps.WireItem("a", "a2")],
            1234, () => throw new InvalidOperationException("Removal never mints ids."));
        Assert.Equal(new[] { "a1", "a2", "b1" }, operations.Select(op => Assert.Single(op.Items!).ItemId));
        Assert.All(operations, op => Assert.True(op.ItemsAsKey));
    }

    [Fact]
    public void Discovery_reads_the_captured_library_item_union()
    {
        var result = Spotify.Podcasts.DecodeSavedDiscovery("""
            {"data":{"me":{"libraryV3":{"totalCount":50,"items":[
              {"item":{"data":{"__typename":"Playlist","format":"listen-later","uri":"spotify:playlist:saved"}}},
              {"item":{"data":{"__typename":"Playlist","format":"other","uri":"spotify:playlist:other"}}}
            ]}}}}
            """u8.ToArray());
        Assert.Equal("spotify:playlist:saved", result.Uri);
        Assert.Equal(2, result.Count);
        Assert.Equal(50, result.Total);
    }
}

[Collection(EntitiesCollection.Name)]
public class PodcastDiscussionScopeRepairTests : IDisposable
{
    public PodcastDiscussionScopeRepairTests()
    { Fetch.Reset(); Store.Shutdown(); Store.Use(null); Entities.Boot(CatalogScope.Fake()); }
    public void Dispose() { Fetch.Reset(); Store.Shutdown(); Store.Use(null); }

    [Fact]
    public void Fresh_boot_generation_is_valid_even_when_account_epoch_is_zero()
    {
        var scope = Entities.Current;
        Assert.Equal(0u, scope.Epoch);
        Assert.NotEqual(scope.Epoch, Entities.ScopeEpoch.Peek());
        Assert.True(Spotify.Podcasts.DiscussionScopeCurrent(scope, Entities.ScopeEpoch.Peek()));
    }

    [Fact]
    public void Reboot_rejects_old_generation_even_when_account_epoch_matches()
    {
        var previous = Entities.Current;
        uint generation = Entities.ScopeEpoch.Peek();
        Entities.Boot(CatalogScope.Fake());
        Assert.Equal(previous.Epoch, Entities.Current.Epoch);
        Assert.False(Spotify.Podcasts.DiscussionScopeCurrent(previous, generation));
        Assert.False(Spotify.Podcasts.DiscussionScopeCurrent(Entities.Current, generation));
        Assert.True(Spotify.Podcasts.DiscussionScopeCurrent(Entities.Current, Entities.ScopeEpoch.Peek()));
    }
}
