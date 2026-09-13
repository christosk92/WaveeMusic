using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class ContentFilterParserTests
{
    const string Body = """
    { "contentFilters": [
        { "title": "Mellow",     "query": "tags contains mellow" },
        { "title": "K-Pop",      "query": "tags contains k-pop" },
        { "title": "Energetic",  "query": "tags contains energetic" }
      ] }
    """;

    [Fact]
    public void ParsesTitleAndToken_PreservingServerOrder()
    {
        var chips = ContentFilterParser.Parse(Body);
        Assert.Equal(3, chips.Count);
        Assert.Equal("Mellow", chips[0].Title);
        Assert.Equal("mellow", chips[0].Token);
        Assert.Equal(["Mellow", "K-Pop", "Energetic"], System.Linq.Enumerable.ToArray(
            System.Linq.Enumerable.Select(chips, c => c.Title)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"contentFilters": null}""")]
    [InlineData("""{"contentFilters": "nope"}""")]
    public void MalformedBodyYieldsEmpty_NeverThrows(string json)
        => Assert.Empty(ContentFilterParser.Parse(json));

    [Fact]
    public void UnsupportedQueryFormsAreDropped()
    {
        // Only `tags contains <token>` has a defined client-side meaning; a chip that filters wrongly is worse than
        // one that is absent, so anything else is dropped rather than guessed at.
        const string json = """
        { "contentFilters": [
            { "title": "Good",  "query": "tags contains good" },
            { "title": "Weird", "query": "popularity > 50" },
            { "title": "Also",  "query": "artist is X" }
          ] }
        """;
        var chips = ContentFilterParser.Parse(json);
        Assert.Single(chips);
        Assert.Equal("Good", chips[0].Title);
    }

    [Fact]
    public void EntriesMissingTitleOrQueryAreDropped()
    {
        const string json = """
        { "contentFilters": [
            { "title": "OnlyTitle" },
            { "query": "tags contains orphan" },
            { "title": "Fine", "query": "tags contains fine" }
          ] }
        """;
        Assert.Single(ContentFilterParser.Parse(json));
    }

    [Fact]
    public void DuplicateTokensCollapse()
    {
        const string json = """
        { "contentFilters": [
            { "title": "Chill", "query": "tags contains chill" },
            { "title": "Chill", "query": "tags contains CHILL" }
          ] }
        """;
        Assert.Single(ContentFilterParser.Parse(json));
    }

    [Fact]
    public void QuotedAndPaddedTokensAreUnwrapped()
    {
        // A token with quotes baked in would never match a descriptor.
        Assert.Equal("k-pop", ContentFilterParser.TokenOf("tags contains \"k-pop\""));
        Assert.Equal("k-pop", ContentFilterParser.TokenOf("  tags contains   k-pop  "));
        Assert.Equal("k-pop", ContentFilterParser.TokenOf("TAGS CONTAINS k-pop"));
        Assert.Null(ContentFilterParser.TokenOf("tags contains "));
        Assert.Null(ContentFilterParser.TokenOf("something else"));
    }

    // ── reconciliation against the tracks in view ────────────────────────────────────────────────────────────────
    static Track T(string id, params string[] tags) => new(
        id, "spotify:track:" + id, id,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
        180_000, false, null,
        Tags: tags.Length == 0 ? null : tags);

    [Fact]
    public void ReconcileKeepsServerOrderAndWording()
    {
        var chips = ContentFilterParser.Parse(Body);
        var tracks = new List<Track> { T("a", "Energetic"), T("b", "Mellow") };

        // Server order is preserved (Mellow before Energetic) even though the tracks arrived the other way round,
        // and K-Pop is dropped because nothing in view carries it.
        Assert.Equal(["Mellow", "Energetic"], ContentFilterTags.Reconcile(chips, tracks));
    }

    [Fact]
    public void ReconcileMatchesOnTokenOrTitle()
    {
        var chips = ContentFilterParser.Parse(Body);
        // display_name absent → the lowercase wire token is what landed on the track; it must still match.
        Assert.Equal(["K-Pop"], ContentFilterTags.Reconcile(chips, [T("a", "k-pop")]));
        // display_name present → matches the server's title.
        Assert.Equal(["K-Pop"], ContentFilterTags.Reconcile(chips, [T("a", "K-Pop")]));
    }

    [Fact]
    public void ReconcileYieldsEmptyWhenNothingMatches()
    {
        var chips = ContentFilterParser.Parse(Body);
        Assert.Empty(ContentFilterTags.Reconcile(chips, [T("a", "Jazz")]));
        Assert.Empty(ContentFilterTags.Reconcile(chips, [T("a")]));
        Assert.Empty(ContentFilterTags.Reconcile(Array.Empty<ContentFilterChip>(), [T("a", "Mellow")]));
    }
}
