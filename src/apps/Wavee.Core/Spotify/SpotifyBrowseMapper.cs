using System.Collections.Generic;
using System.Text.Json;

namespace Wavee.Core;

/// <summary>Projects Spotify's browse Pathfinder responses (<c>browseAll</c> / <c>browsePage</c> /
/// <c>browseSection</c>) into the framework-neutral browse model.
///
/// Four wire behaviours this mapper exists to absorb, every one observed in <c>browe.saz</c>:
///  1. A page can return HTTP 200 with <c>data.browse</c> carrying ONLY <c>__typename</c> — no header, no sections.
///  2. <c>header.color</c> is null on some pages (Made For You), so the accent is genuinely optional.
///  3. A section item can be <c>__typename: "NotFound"</c> mixed among real cards — a dead reference to skip.
///  4. The category card's title/artwork/colour live at <c>content.data.DATA.cardRepresentation</c> — note the
///     DOUBLE <c>data</c>. Stopping one level short yields a grid of nameless tiles.</summary>
public static class SpotifyBrowseMapper
{
    /// <summary>Map <c>browseAll</c> → the flat category list backing the Browse directory. The server returns one
    /// section containing every category; the grouping into Top/Genres/Mood is a CLIENT concern (the wire has none).</summary>
    public static IReadOnlyList<BrowseCategory> Categories(JsonElement responseRoot)
    {
        var sections = SpotifyExportMapper.Dig(responseRoot, "data", "browseStart", "sections", "items");
        if (sections.ValueKind != JsonValueKind.Array) return System.Array.Empty<BrowseCategory>();

        var list = new List<BrowseCategory>(80);
        foreach (var section in sections.EnumerateArray())
            foreach (var it in SpotifyExportMapper.Arr(SpotifyExportMapper.Dig(section, "sectionItems", "items")))
                if (Category(it) is { } c)
                    list.Add(c);
        return list;
    }

    /// <summary>Map one browse tile. Handles BOTH shapes: a <c>BrowseSectionContainer</c> (a real page, whose card
    /// fields sit under a second <c>data</c>) and a <c>BrowseClientFeature</c> (Live Events — one level shallower,
    /// and carrying a <c>featureUri</c> that routes into the client's own surface rather than a browse page).</summary>
    static BrowseCategory? Category(JsonElement item)
    {
        var data = SpotifyExportMapper.Dig(item, "content", "data");
        string? typeName = SpotifyExportMapper.Str(data, "__typename");

        if (string.Equals(typeName, "BrowseClientFeature", System.StringComparison.Ordinal))
        {
            // featureUri (e.g. "spotify:concerts") is the routing target — NOT a browse page uri.
            string? feature = SpotifyExportMapper.Str(data, "featureUri");
            string? title = SpotifyExportMapper.Str(data, "title", "transformedLabel");
            if (string.IsNullOrEmpty(feature) || string.IsNullOrEmpty(title)) return null;
            return new BrowseCategory(feature!, title!, HexColor(SpotifyExportMapper.Dig(data, "backgroundColor")),
                SpotifyExportMapper.PickImage(SpotifyExportMapper.Dig(data, "artwork", "sources")),
                IsClientFeature: true);
        }

        // BrowseSectionContainer: the card fields are one level deeper than the wrapper's own `data`.
        var card = SpotifyExportMapper.Dig(data, "data", "cardRepresentation");
        string? uri = SpotifyExportMapper.Str(item, "uri");
        string? label = SpotifyExportMapper.Str(card, "title", "transformedLabel");
        if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(label)) return null;

        return new BrowseCategory(uri!, label!, HexColor(SpotifyExportMapper.Dig(card, "backgroundColor")),
            SpotifyExportMapper.PickImage(SpotifyExportMapper.Dig(card, "artwork", "sources")));
    }

    /// <summary>Map <c>browsePage</c> → one category page. Returns a page with no sections (rather than null) when the
    /// server sends the header-less 200 body, so the caller renders an empty state instead of treating it as failure.</summary>
    public static BrowsePageModel Page(JsonElement responseRoot, string requestedUri)
    {
        var browse = SpotifyExportMapper.Dig(responseRoot, "data", "browse");
        var header = SpotifyExportMapper.Dig(browse, "header");
        string? title = SpotifyExportMapper.Str(header, "title", "transformedLabel");
        uint? accent = HexColor(SpotifyExportMapper.Dig(header, "color"));

        var sectionsNode = SpotifyExportMapper.Dig(browse, "sections");
        var items = SpotifyExportMapper.Dig(sectionsNode, "items");
        var sections = new List<BrowseSection>();
        foreach (var s in SpotifyExportMapper.Arr(items))
            if (Section(s) is { } mapped)
                sections.Add(mapped);

        int total = (int)SpotifyExportMapper.Long(sectionsNode, "totalCount");
        int? next = NextOffset(SpotifyExportMapper.Dig(sectionsNode, "pagingInfo"));
        return new BrowsePageModel(SpotifyExportMapper.Str(browse, "uri") ?? requestedUri, title, accent,
            sections, total, next);
    }

    /// <summary>Map <c>browseSection</c> → one section's next page of items. This is the SECOND paging axis: it walks
    /// the items inside a section and never advances the page's section cursor.</summary>
    public static BrowseSection? SectionPage(JsonElement responseRoot)
        => Section(SpotifyExportMapper.Dig(responseRoot, "data", "browseSection"));

    static BrowseSection? Section(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;
        var data = SpotifyExportMapper.Dig(s, "data");
        string? typeName = SpotifyExportMapper.Str(data, "__typename");
        var kind = typeName switch
        {
            "BrowseGridSectionData" => BrowseSectionKind.CategoryGrid,
            "BrowseRelatedSectionData" => BrowseSectionKind.Related,
            _ => BrowseSectionKind.Shelf,
        };

        var itemsNode = SpotifyExportMapper.Dig(s, "sectionItems");
        var cards = new List<BrowseCard>();
        var categories = new List<BrowseCategory>();
        foreach (var it in SpotifyExportMapper.Arr(SpotifyExportMapper.Dig(itemsNode, "items")))
        {
            if (kind is BrowseSectionKind.CategoryGrid or BrowseSectionKind.Related)
            {
                if (Category(it) is { } c) categories.Add(c);
                continue;
            }
            if (Card(it) is { } card) cards.Add(card);
        }

        return new BrowseSection(
            SpotifyExportMapper.Str(s, "uri") ?? "",
            SpotifyExportMapper.Str(data, "title", "transformedLabel"),
            kind, cards, categories,
            (int)SpotifyExportMapper.Long(itemsNode, "totalCount"),
            SectionNextOffset(SpotifyExportMapper.Dig(itemsNode, "pagingInfo")));
    }

    /// <summary>The <see cref="BrowseSection.NextOffset"/> tri-state, distinguishing "no pagingInfo at all" (plain
    /// <c>null</c> — the caller should synthesize a cursor) from "pagingInfo present, nextOffset EXPLICITLY null"
    /// (<see cref="BrowseSection.PagingComplete"/> — a real terminator). The page-level cursor
    /// (<see cref="NextOffset(JsonElement)"/>, used by <see cref="Page"/>) does not need this distinction — a stale
    /// page-level cursor is deliberately un-wired by the UI (see <see cref="BrowsePageModel"/>'s doc comment) — so it
    /// keeps its simpler two-state form rather than being folded into this one.</summary>
    static int? SectionNextOffset(JsonElement pagingInfo)
    {
        if (pagingInfo.ValueKind != JsonValueKind.Object) return null;
        if (!pagingInfo.TryGetProperty("nextOffset", out var n)) return null;
        if (n.ValueKind == JsonValueKind.Null) return BrowseSection.PagingComplete;
        return n.TryGetInt32(out int v) ? v : null;
    }

    /// <summary>One entity card in a shelf. A browse shelf mixes Playlist / Album / Episode / Podcast / Audiobook, and
    /// every one exposes uri + name + coverArt, so a single projection serves them all. Returns null for the
    /// <c>NotFound</c> wrappers Spotify mixes in — rendering one would produce a blank, unclickable card.</summary>
    static BrowseCard? Card(JsonElement item)
    {
        var d = SpotifyExportMapper.Dig(item, "content", "data");
        if (d.ValueKind != JsonValueKind.Object) return null;
        if (string.Equals(SpotifyExportMapper.Str(d, "__typename"), "NotFound", System.StringComparison.Ordinal))
            return null;

        string? uri = SpotifyExportMapper.Str(d, "uri");
        string? name = SpotifyExportMapper.Str(d, "name");
        if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(name)) return null;

        // Subtitle by entity: a podcast/episode has a publisher, a playlist a description, an audiobook its authors.
        string? subtitle = SpotifyExportMapper.Str(d, "publisher", "name")
                           ?? SpotifyExportMapper.HtmlText(SpotifyExportMapper.Str(d, "description"));

        return new BrowseCard(uri!, name!, subtitle, CardImage(d),
            HexColor(SpotifyExportMapper.Dig(d, "coverArt", "extractedColors", "colorDark")));
    }

    /// <summary>Browse entities do NOT all carry <c>coverArt</c>. A Playlist here ships
    /// <c>images.items[].sources</c> (verified on the wire: <c>New Music Friday NL</c> in browe.saz), while albums and
    /// shows use <c>coverArt.sources</c> and an artist uses <c>visuals.avatarImage</c>. Reading only coverArt is what
    /// left every browse shelf as blank grey squares — so try each shape in turn and take the first that resolves.</summary>
    static Image? CardImage(JsonElement d)
        => PickFromImageList(SpotifyExportMapper.Dig(d, "images", "items"))
           ?? SpotifyExportMapper.PickImage(SpotifyExportMapper.Dig(d, "coverArt", "sources"))
           ?? SpotifyExportMapper.PickImage(SpotifyExportMapper.Dig(d, "visuals", "avatarImage", "sources"))
           ?? SpotifyExportMapper.PickImage(SpotifyExportMapper.Dig(d, "coverArtV2", "sources"));

    /// <summary>First resolvable image out of an <c>images.items[]</c> array (each entry is <c>{sources:[…]}</c>).
    /// Dig() walks object keys only, so the array hop is explicit here.</summary>
    static Image? PickFromImageList(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        foreach (var it in items.EnumerateArray())
            if (SpotifyExportMapper.PickImage(SpotifyExportMapper.Dig(it, "sources")) is { } img) return img;
        return null;
    }

    static int? NextOffset(JsonElement pagingInfo)
    {
        if (pagingInfo.ValueKind != JsonValueKind.Object) return null;
        if (!pagingInfo.TryGetProperty("nextOffset", out var n) || n.ValueKind != JsonValueKind.Number) return null;
        return n.TryGetInt32(out int v) ? v : null;
    }

    // Browse ships colour as a CSS hex string (the protobuf surfaces send RGBA channels instead) — SpotifyColor is
    // the one parser for that form.
    static uint? HexColor(JsonElement node)
        => node.ValueKind != JsonValueKind.Object ? null : SpotifyColor.FromHex(SpotifyExportMapper.Str(node, "hex"));
}
