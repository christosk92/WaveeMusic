using System.Text.Json;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// whatsnew.json is HAND-AUTHORED, so every array and every string in it can legally arrive as null — and the property
// initializers on ReleaseNotesDocument do NOT save you, because they only run for members the JSON never mentions. An
// explicit `"sections": null` overwrites the empty array with null and every consumer NREs on its first foreach.
// Normalize() is the one chokepoint that repairs that, and the store + the release tool call it after every
// deserialize. These tests are what keep it total.
public class ReleaseNotesNormalizeTests
{
    static ReleaseNotesDocument Parse(string json)
    {
        var doc = JsonSerializer.Deserialize(json, ReleaseNotesJsonContext.Default.ReleaseNotesDocument);
        Assert.NotNull(doc);
        return doc!;
    }

    [Fact]
    public void EveryNullArray_BecomesEmpty()
    {
        var doc = Parse("""
        {
          "version": "0.3.0",
          "highlights": null,
          "sections": null,
          "notices": null,
          "contributors": null,
          "media": null,
          "arch": null
        }
        """);

        doc.Normalize();

        Assert.Empty(doc.Highlights);
        Assert.Empty(doc.Sections);
        Assert.Empty(doc.Notices);
        Assert.Empty(doc.Contributors);
        Assert.Empty(doc.Media);
        Assert.Empty(doc.Arch);
        Assert.NotNull(doc.Links);
    }

    [Fact]
    public void EveryNullString_BecomesEmpty()
    {
        var doc = Parse("""
        {
          "version": "0.3.0",
          "name": null,
          "tagline": null,
          "date": null,
          "channel": null,
          "minOs": null,
          "generatedAt": null,
          "links": null
        }
        """);

        doc.Normalize();

        Assert.Equal("", doc.Name);
        Assert.Equal("", doc.Tagline);
        Assert.Equal("", doc.Date);
        Assert.Equal("", doc.Channel);
        Assert.Equal("", doc.MinOs);
        Assert.Equal("", doc.GeneratedAt);
        Assert.Equal("", doc.Links.Release);
    }

    [Fact]
    public void ANullElementInsideAnArray_IsDropped()
    {
        // `[null]` is legal JSON and deserializes to an array holding a null reference, which a `foreach` walks
        // straight into. Compacting is the only way a caller can safely iterate without a per-element null check.
        var doc = Parse("""
        {
          "version": "0.3.0",
          "highlights": [null, { "id": "h1", "title": "A thing" }],
          "sections": [null, { "kind": "fixed", "items": [null, { "id": "f1", "text": "Fixed a thing" }] }],
          "notices": [null]
        }
        """);

        doc.Normalize();

        var highlight = Assert.Single(doc.Highlights);
        Assert.Equal("h1", highlight.Id);
        var section = Assert.Single(doc.Sections);
        var item = Assert.Single(section.Items);
        Assert.Equal("f1", item.Id);
        Assert.Empty(doc.Notices);
    }

    [Fact]
    public void NestedNulls_AreRepairedToo()
    {
        var doc = Parse("""
        {
          "version": "0.3.0",
          "highlights": [{ "id": "h1", "title": null, "body": null, "kind": null, "issues": null,
                           "media": { "kind": null, "src": null, "alt": null } }],
          "sections": [{ "kind": "fixed", "items": [{ "id": "f1", "text": null, "issues": null, "prs": null,
                                                      "contributors": null }] }]
        }
        """);

        doc.Normalize();

        var h = Assert.Single(doc.Highlights);
        Assert.Equal("", h.Title);
        Assert.Equal("", h.Body);
        Assert.Equal("new", h.Kind);
        Assert.Empty(h.Issues);
        Assert.Equal("image", h.Media!.Kind);
        Assert.Equal("", h.Media.Src);

        var item = Assert.Single(Assert.Single(doc.Sections).Items);
        Assert.Equal("", item.Text);
        Assert.Empty(item.Issues);
        Assert.Empty(item.Prs);
        Assert.Empty(item.Contributors);
    }

    [Fact]
    public void ACleanDocument_IsUntouched_AndNormalizeIsIdempotent()
    {
        var doc = Parse("""
        {
          "version": "0.3.0",
          "name": "Crest",
          "highlights": [{ "id": "h1", "title": "A thing", "kind": "improved" }],
          "sections": [{ "kind": "fixed", "items": [{ "id": "f1", "text": "Fixed a thing" }] }]
        }
        """);

        doc.Normalize();
        var highlights = doc.Highlights;
        var sections = doc.Sections;
        doc.Normalize();

        // The common case allocates nothing: an array with no nulls is handed back as itself.
        Assert.Same(highlights, doc.Highlights);
        Assert.Same(sections, doc.Sections);
        Assert.Equal("Crest", doc.Name);
        Assert.Equal("improved", doc.Highlights[0].Kind);
    }

    [Fact]
    public void TheRenderers_SurviveADocumentThatWasAllNulls()
    {
        // The proof that matters: the two surfaces the release tool drives (the GitHub body and the store blurb) run
        // over a document whose every array arrived null, and neither throws. Both normalize on entry.
        var doc = Parse("""{ "version": "0.3.0", "highlights": null, "sections": null, "notices": null }""");

        string body = ReleaseNotesValidation.RenderBody(doc, "acme/widgets", generatedNotes: null);
        string listing = ReleaseNotesValidation.RenderStoreListing(doc);

        Assert.Contains("Wavee 0.3.0", body);
        Assert.Equal("", listing);          // no tagline and no highlights is an empty blurb, not a crash
        Assert.Empty(ReleaseNotesValidation.MediaEntries(doc));
        Assert.Empty(ReleaseNotesValidation.ValidateDeepLinks(doc));
    }
}
