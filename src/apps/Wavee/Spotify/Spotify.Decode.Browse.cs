// ── Spotify/Spotify.Decode.Browse.cs ────────────────────────────────────────────────────────────────────────────────
// the browse tree and the "Show all" drills: browseAll, browsePage, browseSection, homeSection (gap batch B1, G-045)
//
// Role: CORE
// Owner: E (gap batch B1) → P (Wave 5, stream P3: the chart bit and the search folds)
// Wave: gap batch B1 + 5
// Budget: 360 lines (B1) — Wave 5 adds the search half below it (reported over budget; no pre-declared partial)
// Spec: gap register G-045 (search genres + suggestions), B1b gap 3 (search as PAGES), ch 13 §7 — a named partial of
//       Spotify.Decode.cs (Spotify.Decode.Pathfinder.cs is already past its budget, so the new folds live here)
//
// THE SEARCH HALF (Wave 5): `searchTopResultsList` and the facet operations answer ONE shape —
// `data.searchV2{ chipOrder, topResultsV2.itemsV2[], tracksV2, albumsV2, artists, playlists, podcasts, episodes, users,
// audiobooks, authors, genres }` (0.2.9 `SpotifyExportMapper.SearchFromV2` / `TopHitsFromV2` / `SuggestionsFromV2`).
// `SearchPage` lands the subject row's own list as a PAGE at its offset (never a whole-list rewrite), the All row's chip
// strip and its hits' chrome; `SearchGenres` lands the genre tiles; `SearchRelated` lands the related queries; and
// `SearchSuggestions` answers the omnibar popup as a plain value (it is not table data).
//
// FOUR ANSWERS, ONE READER IDIOM (Spotify.Decode.Pathfinder.cs §9). The wire shapes are 0.2.9's `SpotifyBrowseMapper`
// and `SpotifyHomeComposer.SectionPage`, verified against the fragments its tests trimmed from real captures:
//
//   browseAll      data.browseStart.sections.items[].sectionItems.items[]           → the directory's TILES
//                    a tile:  uri + content.data.data.cardRepresentation{ title, backgroundColor{hex}, artwork }
//                    a feature (Live Events): content.data{ __typename: BrowseClientFeature, featureUri, title, … }
//   browsePage     data.browse{ uri, header{ title, color{hex} }, sections{ totalCount, pagingInfo, items[] } }
//                    a band:  uri + data{ __typename, title } + sectionItems{ totalCount, pagingInfo, items[] }
//   browseSection  data.browseSection                                              → one band, paged
//   homeSection    data.homeSections.sections[0]                                   → one Home band, paged
//
// THE TREE'S SHAPE IS Entities/Browse.cs's: tiles are browse ROWS, a page's bands are SECTION rows (Home.cs's one
// section table), a shelf band's cards ride `Edges.SectionCards` like a Home band's, and a grid band's tiles ride
// `Edges.SectionCategories`. The band's KIND decides which — and forward-only is why the kind is read FIRST, off a copy
// of the reader (`BandKindOf`): the wire may emit `sectionItems` before `data.__typename`, and a card cannot be staged
// as both an entity and a tile and then un-staged.

using System.Text.Json;
using FluentGpu.Localization;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // ── browseAll ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>browseAll</c> → every category tile as a browse row (title, artwork, colour, the client-feature
        /// bit), and the directory's <see cref="BrowseRelation.Directory"/> run in wire order. The directory subject
        /// (<c>wavee:browse</c>) is staged as answered — both groups, so a remount paints the grid at once.</summary>
        public static void BrowseAll(ReadOnlySpan<byte> json, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "browseStart"u8)) return;

            var directory = new StagedId(s.AddText(DirectoryUri));
            s.Browses.RowFor(directory, Authority.Full, (uint)BrowseFields.All);   // the listing answered, empty title and all
            int start = s.BrowseChildren.Count;

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("sections"u8)) { SkipValue(ref r); continue; }
                for (int sections = Fields(ref r); Next(ref r, sections);)
                {
                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                        for (int section = r.CurrentDepth; Next(ref r, section);)
                        {
                            if (!r.ValueTextEquals("sectionItems"u8)) { SkipValue(ref r); continue; }
                            for (int items = Fields(ref r); Next(ref r, items);)
                            {
                                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                                for (int tiles = r.CurrentDepth; Element(ref r, tiles);)
                                {
                                    var tile = TileAt(ref r, s);
                                    if (!tile.IsEmpty) s.BrowseChildren.Add() = tile;
                                }
                            }
                        }
                }
            }
            AppendBrowseRun(s, in directory, BrowseRelation.Directory, start, offset: -1, total: 0);
        }

        // ── browsePage ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>browsePage</c> → the page's header (its accent, and its title THIN so the directory tile's own
        /// title and colour always win), one section row per band with its cards or its tiles, and the page's
        /// <see cref="BrowseRelation.PageSections"/> run as the page at <paramref name="sectionOffset"/> — Partial until
        /// the page's own section total is reached. A body carrying only <c>__typename</c> is a real, empty page.</summary>
        public static void BrowsePage(ReadOnlySpan<byte> json, ReadOnlySpan<byte> pageUri, int sectionOffset, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "browse"u8)) return;

            TextRef uri = default, title = default;
            uint accent = 0;
            int total = 0, next = SectionPaging.NoCursor;
            // The page's bands are read back off the staged SECTION rows after the walk, not pushed as they close: a grid
            // band appends its own tile run to the same child list while the walk is still inside the page, and a run
            // is a contiguous slice.
            int bands = s.Sections.Count;

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); uri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("header"u8))
                {
                    for (int h = Fields(ref r); Next(ref r, h);)
                    {
                        if (r.ValueTextEquals("title"u8)) { r.Read(); title = Label(ref r, s); }
                        else if (r.ValueTextEquals("color"u8)) accent = HexColor(ref r);
                        else SkipValue(ref r);
                    }
                }
                else if (r.ValueTextEquals("sections"u8))
                {
                    for (int sections = Fields(ref r); Next(ref r, sections);)
                    {
                        if (r.ValueTextEquals("totalCount"u8)) { r.Read(); total = (int)Num(ref r); }
                        else if (r.ValueTextEquals("pagingInfo"u8)) next = (int)OneNumber(ref r, "nextOffset"u8, SectionPaging.NoCursor);
                        else if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                        {
                            for (int list = r.CurrentDepth; Element(ref r, list);) Band(ref r, s, offset: -1);
                        }
                        else SkipValue(ref r);
                    }
                }
                else SkipValue(ref r);
            }

            var page = uri.IsEmpty ? PageId(s, pageUri) : PageId(s, uri);
            if (page.IsEmpty) return;
            int start = s.BrowseChildren.Count;
            for (int i = bands; i < s.Sections.Count; i++) s.BrowseChildren.Add() = s.Sections[i].Id;

            // The page group is this answer's; the identity (title) only fills a node nobody listed — Thin (D16).
            ref var header = ref s.Browses.RowFor(page, Authority.Thin, (uint)BrowseFields.Identity);
            header.Title = title;
            ref var body = ref s.Browses.RowFor(page, Authority.Full, (uint)BrowseFields.Page);
            body.Accent = accent;
            body.TotalSections = total;
            body.NextSectionOffset = next;
            AppendBrowseRun(s, in page, BrowseRelation.PageSections, start, Math.Max(0, sectionOffset), total);
        }

        // ── browseSection / homeSection ──────────────────────────────────────────────────────────────────────────────

        /// <summary><c>browseSection</c> → one browse band, its cards as the page at <paramref name="offset"/>.</summary>
        public static StagedId BrowseSection(ReadOnlySpan<byte> json, int offset, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "browseSection"u8)) return default;
            return Band(ref r, s, Math.Max(0, offset));
        }

        /// <summary><c>homeSection</c> → one Home band, its cards as the page at <paramref name="offset"/>. The root is
        /// <c>data.homeSections.sections</c> — a bare array in every capture, an object wrapping <c>items</c> accepted —
        /// and its FIRST element is the band; a 200 with none is "this did not work" (the caller's to fail), never an
        /// empty band.</summary>
        public static StagedId HomeSection(ReadOnlySpan<byte> json, int offset, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "homeSections"u8)) return default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("sections"u8)) { SkipValue(ref r); continue; }
                if (!r.Read()) return default;
                if (r.TokenType == JsonTokenType.StartObject)
                {
                    // `sections: { items: [ … ] }`
                    for (int o = r.CurrentDepth; Next(ref r, o);)
                    {
                        if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                        return FirstBand(ref r, s, offset);
                    }
                    return default;
                }
                if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return default; }
                return FirstBand(ref r, s, offset);
            }
            return default;

            static StagedId FirstBand(ref Utf8JsonReader r, Staging s, int offset)
                => Element(ref r, r.CurrentDepth) ? Section(ref r, s, Math.Max(0, offset)) : default;
        }

        // ── the band ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One browse band (the reader on its object): the section row, and by its KIND either its entity cards
        /// (<c>Edges.SectionCards</c>) or its category tiles (<see cref="BrowseRelation.SectionCategories"/>). The kind
        /// is read first off a copy of the reader (the file header); <c>BrowseGenericSectionData</c> and anything unknown
        /// is a shelf, exactly as 0.2.9's mapper decided.</summary>
        static StagedId Band(ref Utf8JsonReader r, Staging s, int offset)
        {
            byte kind = BandKindOf(r);
            bool tiles = kind != (byte)SectionKind.BrowseShelf;
            ref var row = ref s.Sections.RowFor(default, Authority.Full, (uint)SectionFields.Identity);
            row.NextOffset = SectionPaging.NoCursor;
            row.Kind = kind;
            int cards = s.Edges.PendingMark, tileStart = s.BrowseChildren.Count;

            for (int depth = r.CurrentDepth; Next(ref r, depth);)
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); if (row.Id.IsEmpty) row.Id = JsonId(ref r, s); }
                else if (r.ValueTextEquals("data"u8))
                {
                    for (int d = Fields(ref r); Next(ref r, d);)
                    {
                        if (r.ValueTextEquals("title"u8)) { r.Read(); row.Title = Label(ref r, s); }
                        else if (r.ValueTextEquals("subtitle"u8)) { r.Read(); row.Subtitle = Label(ref r, s); }
                        else SkipValue(ref r);
                    }
                }
                else if (r.ValueTextEquals("sectionItems"u8))
                {
                    for (int d = Fields(ref r); Next(ref r, d);)
                    {
                        if (r.ValueTextEquals("totalCount"u8)) { r.Read(); row.Total = (int)Num(ref r); }
                        else if (r.ValueTextEquals("pagingInfo"u8))
                            row.NextOffset = (int)OneNumber(ref r, "nextOffset"u8, SectionPaging.NoCursor);
                        else if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                        {
                            for (int list = r.CurrentDepth; Element(ref r, list);)
                            {
                                row.Raw++;
                                if (tiles)
                                {
                                    var tile = TileAt(ref r, s);
                                    if (tile.IsEmpty) { row.Unsupported++; continue; }
                                    row.Cards++;
                                    s.BrowseChildren.Add() = tile;
                                    continue;
                                }
                                var node = default(Node);
                                EntityNode(ref r, s, ref node);
                                var card = Stage(s, in node, Authority.Thin);
                                if (card.IsEmpty) { row.Unsupported++; continue; }
                                row.Cards++;
                                s.Edges.Push().Target = card;
                            }
                        }
                        else SkipValue(ref r);
                    }
                }
                else SkipValue(ref r);
            }

            var uri = row.Id;
            // The DECODED chart bit (ch 12 trap 10): the drill grid threads it in and never re-derives it from a uri.
            if (IsChartSection(s, in uri)) row.Flags |= (byte)SectionFlags.Chart;
            if (offset > 0)
            {
                row.Raw = SectionPaging.Advance(offset, row.Raw);
                row.Cards += offset;
            }
            if (!s.Sections.Settle()) { s.Edges.Pop(cards); s.BrowseChildren.Rewind(tileStart); return default; }
            if (tiles)
            {
                s.Edges.Pop(cards);
                AppendBrowseRun(s, in uri, BrowseRelation.SectionCategories, tileStart, offset <= 0 ? -1 : offset, 0);
            }
            else if (offset >= 0) s.Edges.ClosePage(Relation.SectionCards, in uri, cards, offset, total: 0);
            else s.Edges.Close(Relation.SectionCards, in uri, cards);
            return uri;
        }

        /// <summary>A band's kind, read off a COPY of the reader (so the caller's cursor does not move): the
        /// <c>data.__typename</c> of the object the reader is on.</summary>
        static byte BandKindOf(Utf8JsonReader r)
        {
            for (int depth = r.CurrentDepth; Next(ref r, depth);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("__typename"u8)) { SkipValue(ref r); continue; }
                    r.Read();
                    return Says(ref r, "BrowseGridSectionData") ? (byte)SectionKind.BrowseCategoryGrid
                         : Says(ref r, "BrowseRelatedSectionData") ? (byte)SectionKind.BrowseRelated
                         : (byte)SectionKind.BrowseShelf;
                }
                return (byte)SectionKind.BrowseShelf;
            }
            return (byte)SectionKind.BrowseShelf;
        }

        // ── the tile ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One category tile (the reader on its item object) → a staged browse row, answering with its identity.
        /// Two shapes: a page container keeps its fields under <c>content.data.data.cardRepresentation</c> (the DOUBLE
        /// <c>data</c> — stopping one level short is the bug that painted nameless, colourless tiles) and routes by the
        /// item's own <c>uri</c>; a <c>BrowseClientFeature</c> keeps them one level shallower and routes by
        /// <c>featureUri</c> (<c>spotify:concerts</c>), never by the item's <c>spotify:xlink:</c>. A tile with no uri or
        /// no title is dropped; a genre uri is folded onto its page uri (one node, Browse.cs).</summary>
        static StagedId TileAt(ref Utf8JsonReader r, Staging s)
        {
            var tile = default(Tile);
            for (int depth = r.CurrentDepth; Next(ref r, depth);)
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); tile.Uri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("content"u8)) TileFields(ref r, s, ref tile);
                else SkipValue(ref r);
            }
            var route = tile.Feature && !tile.FeatureUri.IsEmpty ? tile.FeatureUri : tile.Uri;
            if (route.IsEmpty || tile.Title.IsEmpty) return default;
            var id = PageId(s, route);
            if (id.IsEmpty) return default;

            ref var row = ref s.Browses.RowFor(id, Authority.Full, (uint)BrowseFields.Identity);
            row.Title = tile.Title;
            row.Image = tile.Image;
            row.Color = tile.Color;
            row.Flags = tile.Feature ? (uint)BrowseFlags.ClientFeature : 0;
            return id;
        }

        struct Tile
        {
            public TextRef Uri, FeatureUri, Title, Image;
            public uint Color;
            public bool Feature;
        }

        /// <summary>The tile's fields at whatever depth the wire keeps them: recurse through <c>data</c> and
        /// <c>cardRepresentation</c>, read <c>title</c> / <c>backgroundColor</c> / <c>artwork</c> / <c>featureUri</c>
        /// where they appear, and flag a <c>BrowseClientFeature</c> typename.</summary>
        static void TileFields(ref Utf8JsonReader r, Staging s, ref Tile tile)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("data"u8) || r.ValueTextEquals("cardRepresentation"u8)) TileFields(ref r, s, ref tile);
                else if (r.ValueTextEquals("__typename"u8)) { r.Read(); tile.Feature |= Says(ref r, "BrowseClientFeature"); }
                else if (r.ValueTextEquals("featureUri"u8)) { r.Read(); tile.FeatureUri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("title"u8)) { r.Read(); if (tile.Title.IsEmpty) tile.Title = Label(ref r, s); else r.Skip(); }
                else if (r.ValueTextEquals("backgroundColor"u8)) { uint c = HexColor(ref r); if (c != 0) tile.Color = c; }
                else if (r.ValueTextEquals("artwork"u8)) { r.Read(); var url = FirstUrl(ref r, s); if (tile.Image.IsEmpty) tile.Image = url; }
                else SkipValue(ref r);
            }
        }

        // ── search (Wave 5, P3) ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>searchTopResultsList</c> / a facet operation → the SUBJECT's own list as the PAGE at
        /// <paramref name="offset"/> (<see cref="Relation.SearchResult"/>, <c>ClosePage</c> — never a whole-list rewrite,
        /// B1b gap 3). The subject uri (<c>wavee:search:NN:q</c>) names the facet: a facet row lands its own list and its
        /// stated total; the All row lands the ranked <c>topResultsV2.itemsV2</c> in server order (the first IS the Top
        /// Result) with each hit's chrome (matched title / lyrics, video media), its chip strip (chip order, each total the
        /// list's own <c>totalCount</c> when stated), and — when the answer carries no ranked list — the track / album /
        /// artist / playlist lists COLLECTED in wire order and flagged <see cref="SearchRowFlags.Collected"/>.</summary>
        public static void SearchPage(ReadOnlySpan<byte> json, ReadOnlySpan<byte> subjectUri, int offset, Staging s)
        {
            if (!SearchSubjectFacet(subjectUri, out SearchFacet facet)) return;
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "searchV2"u8)) return;
            s.ClearCredit();
            offset = Math.Max(0, offset);
            bool all = facet == SearchFacet.All;
            bool ranked = all && HasTopResults(r);
            var subject = new StagedId(s.AddText(subjectUri));

            Span<byte> chipFacets = stackalloc byte[SearchTable.FacetCount];
            Span<int> chipTotals = stackalloc int[SearchTable.FacetCount];
            Span<int> listTotals = stackalloc int[SearchTable.FacetCount];
            listTotals.Fill(Wavee.Search.NoTotal);
            int chips = 0, mark = s.Edges.PendingMark, linkStart = s.SearchLinks.Count;
            bool chrome = offset == 0;                         // the hit chrome run is page 0's (the rows a user sees first)

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("chipOrder"u8)) { chips = ChipOrder(ref r, chipFacets, chipTotals); continue; }
                if (r.ValueTextEquals("topResultsV2"u8))
                {
                    if (!ranked) { SkipValue(ref r); continue; }
                    for (int t = Fields(ref r); Next(ref r, t);)
                    {
                        if (r.ValueTextEquals("itemsV2"u8)) HitList(ref r, s, chrome);
                        else SkipValue(ref r);
                    }
                    continue;
                }
                var listFacet = SearchListFacet(ref r);
                if (listFacet == SearchFacet.All) { SkipValue(ref r); continue; }
                bool collect = all
                    ? !ranked && listFacet is SearchFacet.Tracks or SearchFacet.Albums or SearchFacet.Artists or SearchFacet.Playlists
                    : listFacet == facet;
                for (int l = Fields(ref r); Next(ref r, l);)
                {
                    if (r.ValueTextEquals("totalCount"u8)) { r.Read(); listTotals[(int)listFacet] = (int)Num(ref r, Wavee.Search.NoTotal); }
                    else if (collect && r.ValueTextEquals("items"u8)) HitList(ref r, s, chrome);
                    else SkipValue(ref r);
                }
            }

            int n = s.Edges.Pending(mark);
            int stated = all ? 0 : listTotals[(int)facet];
            int total = Math.Max(stated < 0 ? 0 : stated, offset + n);
            s.Edges.ClosePage(Relation.SearchResult, in subject, mark, offset, total);

            if (chrome) AppendSearchRun(s, in subject, SearchRelation.Hits, linkStart);

            uint known = (uint)SearchFields.Results | (all ? (uint)SearchFields.Chips : 0u);
            ref var row = ref s.Searches.RowFor(subject, Authority.Full, known);
            row.RowFlags = all && !ranked ? (byte)SearchRowFlags.Collected : (byte)0;
            if (!all) return;

            // The chip strip in server rank; a facet the server COUNTED but did not chip follows it (0.2.9 FacetsFrom's
            // fallback reads the same totals).
            row.ChipStart = s.SearchChips.Count;
            for (int i = 0; i < chips; i++)
            {
                int f = chipFacets[i];
                ref var chip = ref s.SearchChips.Add();
                chip.Facet = (byte)f;
                chip.Total = listTotals[f] >= 0 ? listTotals[f] : chipTotals[i];
            }
            for (int f = 1; f < SearchTable.FacetCount; f++)
            {
                if (listTotals[f] <= 0 || chipFacets[..chips].IndexOf((byte)f) >= 0) continue;
                ref var chip = ref s.SearchChips.Add();
                chip.Facet = (byte)f;
                chip.Total = listTotals[f];
            }
            row.ChipCount = s.SearchChips.Count - row.ChipStart;
        }

        /// <summary><c>searchGenres</c> → the genre tiles as browse NODES (a genre uri folds onto its page uri; the tile's
        /// identity is Thin, so a directory tile's own title and colour always win) and the subject's
        /// <see cref="SearchRelation.Genres"/> run, answering <see cref="SearchFields.Genres"/>.</summary>
        public static void SearchGenres(ReadOnlySpan<byte> json, ReadOnlySpan<byte> subjectUri, Staging s)
        {
            if (subjectUri.IsEmpty) return;
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "searchV2"u8)) return;
            var subject = new StagedId(s.AddText(subjectUri));
            int start = s.SearchLinks.Count;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("genres"u8)) { SkipValue(ref r); continue; }
                for (int g = Fields(ref r); Next(ref r, g);)
                {
                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        var tile = default(GenreNode);
                        GenreFields(ref r, s, ref tile);
                        if (tile.NotFound || tile.Uri.IsEmpty || tile.Name.IsEmpty) continue;
                        var id = PageId(s, tile.Uri);
                        if (id.IsEmpty) continue;
                        ref var node = ref s.Browses.RowFor(id, Authority.Thin, (uint)BrowseFields.Identity);
                        node.Title = tile.Name;
                        node.Image = tile.Image;
                        s.SearchLinks.Add().Target = id;
                    }
                }
            }
            s.Searches.RowFor(subject, Authority.Full, (uint)SearchFields.Genres);
            AppendSearchRun(s, in subject, SearchRelation.Genres, start);
        }

        /// <summary><c>searchSuggestions</c> → the page's related queries (the <c>SearchAutoCompleteEntity</c> texts, in
        /// order, deduped ASCII-case-insensitively, at most <see cref="MaxRelated"/>), answering
        /// <see cref="SearchFields.Related"/>. An answer with none is a real, empty answer.</summary>
        public static void SearchRelated(ReadOnlySpan<byte> json, ReadOnlySpan<byte> subjectUri, Staging s)
        {
            if (subjectUri.IsEmpty) return;
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "searchV2"u8)) return;
            var subject = new StagedId(s.AddText(subjectUri));
            int start = s.SearchLinks.Count;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("topResultsV2"u8)) { SkipValue(ref r); continue; }
                for (int t = Fields(ref r); Next(ref r, t);)
                {
                    if (!r.ValueTextEquals("itemsV2"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        var text = AutoCompleteText(ref r, s);
                        if (text.IsEmpty || s.SearchLinks.Count - start >= MaxRelated) continue;
                        bool dup = false;
                        for (int i = start; i < s.SearchLinks.Count && !dup; i++)
                            dup = SameAsciiIgnoreCase(s.Utf8(s.SearchLinks[i].Text), s.Utf8(text));
                        if (!dup) s.SearchLinks.Add().Text = text;
                    }
                }
            }
            s.Searches.RowFor(subject, Authority.Full, (uint)SearchFields.Related);
            AppendSearchRun(s, in subject, SearchRelation.Related, start);
        }

        /// <summary>How many related queries a page keeps.</summary>
        public const int MaxRelated = 10;

        /// <summary><c>searchSuggestions</c> → the omnibar popup's answer (0.2.9 <c>SuggestionsFromV2</c>): the autocomplete
        /// queries (deduped case-insensitively) and the typed rich rows (deduped by uri, then by kind + title + subtitle —
        /// the catalogue's relinked duplicates read as one song listed twice). Not table data: a plain value. UI THREAD —
        /// a row's uri parse interns a text-form identity (a genre, a profile).</summary>
        public static Shell.Omnibar.Suggestions SearchSuggestions(ReadOnlySpan<byte> json)
        {
            var queries = new List<string>();
            var items = new List<Shell.Omnibar.Item>();
            try
            {
                using var doc = JsonDocument.Parse(json.ToArray());
                if (!TryDig(doc.RootElement, out var list, "data", "searchV2", "topResultsV2", "itemsV2")
                    || list.ValueKind != JsonValueKind.Array)
                    return Shell.Omnibar.Suggestions.Empty;
                var seenQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenDisplay = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var hit in list.EnumerateArray())
                {
                    if (hit.ValueKind != JsonValueKind.Object) continue;
                    var wrapper = hit.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object ? item : hit;
                    var data = wrapper.TryGetProperty("data", out var dd) ? dd : default;
                    string type = StrOf(wrapper, "__typename") ?? StrOf(data, "__typename") ?? "";
                    if (StrOf(data, "text") is { Length: > 0 } query)
                    {
                        if (seenQueries.Add(query)) queries.Add(query);
                        continue;
                    }
                    if (SuggestionItem(type, data) is { } rich && seenItems.Add(rich.Uri.Text)
                        && seenDisplay.Add(rich.Kind + "\u001F" + rich.Title + "\u001F" + rich.Subtitle))
                        items.Add(rich);
                    if (queries.Count >= 8 && items.Count >= 16) break;
                }
            }
            catch (JsonException) { return Shell.Omnibar.Suggestions.Empty; }
            return queries.Count == 0 && items.Count == 0
                ? Shell.Omnibar.Suggestions.Empty
                : new Shell.Omnibar.Suggestions(queries, items);
        }

        static Shell.Omnibar.Item? SuggestionItem(string wrapperType, JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object || StrOf(data, "uri") is not { Length: > 0 } uri) return null;
            string dataType = StrOf(data, "__typename") ?? "";
            string name = StrOf(data, "name") ?? "";
            Shell.Omnibar.ItemKind kind;
            string? subtitle, image;
            if (Has(wrapperType, dataType, "Track"))
            {
                kind = Shell.Omnibar.ItemKind.Track;
                subtitle = JoinArtists(Loc.Get(Strings.Search.SubtitleSong), data);
                image = ImageAt(data, "albumOfTrack", "coverArt");
            }
            else if (Has(wrapperType, dataType, "Artist"))
            {
                kind = Shell.Omnibar.ItemKind.Artist;
                name = TryDig(data, out var p, "profile", "name") ? p.GetString() ?? name : name;
                subtitle = Loc.Get(Strings.Search.TypeArtist);
                image = ImageAt(data, "visuals", "avatarImage");
            }
            else if (Has(wrapperType, dataType, "Album"))
            {
                kind = Shell.Omnibar.ItemKind.Album;
                // 0.2.9: the wire's own release type, title-cased ("Single", "Ep", "Compilation"), as the prefix.
                subtitle = JoinArtists(StrOf(data, "type") is { Length: > 0 } type
                    ? char.ToUpperInvariant(type[0]) + type[1..].Replace('_', ' ').ToLowerInvariant()
                    : Loc.Get(Strings.Search.TypeAlbum), data);
                image = ImageAt(data, "coverArt");
            }
            else if (Has(wrapperType, dataType, "Playlist"))
            {
                kind = Shell.Omnibar.ItemKind.Playlist;
                subtitle = TryDig(data, out var o, "ownerV2", "data", "name") ? o.GetString() : Loc.Get(Strings.Search.TypePlaylist);
                image = TryDig(data, out var images, "images", "items") && images.ValueKind == JsonValueKind.Array
                        && images.GetArrayLength() > 0 ? PickUrl(images[0]) : null;
            }
            else if (Has(wrapperType, dataType, "Genre"))
            {
                kind = Shell.Omnibar.ItemKind.Genre;
                subtitle = Loc.Get(Strings.Search.TypeGenre);
                image = ImageAt(data, "image");
            }
            else if (Has(wrapperType, dataType, "Episode"))
            {
                kind = Shell.Omnibar.ItemKind.Episode;
                subtitle = TryDig(data, out var show, "podcastV2", "data", "name") || TryDig(data, out show, "show", "name")
                        || TryDig(data, out show, "podcast", "name") ? show.GetString() : null;
                image = ImageAt(data, "coverArt") ?? ImageAt(data, "podcastV2", "data", "coverArt");
            }
            else if (Has(wrapperType, dataType, "Podcast") || Has(wrapperType, dataType, "Show"))
            {
                kind = Shell.Omnibar.ItemKind.Podcast;
                subtitle = TryDig(data, out var pub, "publisher", "name") ? pub.GetString() : Loc.Get(Strings.Search.TypePodcast);
                image = ImageAt(data, "coverArt");
            }
            else if (Has(wrapperType, dataType, "Audiobook"))
            {
                kind = Shell.Omnibar.ItemKind.Audiobook;
                subtitle = TryDig(data, out var authors, "authorsV2") ? FirstName(authors) : null;
                image = ImageAt(data, "coverArt");
            }
            else if (Has(wrapperType, dataType, "User"))
            {
                kind = Shell.Omnibar.ItemKind.User;
                name = StrOf(data, "displayName") ?? StrOf(data, "username") ?? name;
                subtitle = Loc.Get(Strings.Search.TypeUser);
                image = ImageAt(data, "avatar") ?? ImageAt(data, "visuals", "avatarImage");
            }
            else return null;
            return new Shell.Omnibar.Item(kind, EntityUri.Parse(uri.AsSpan()), name, subtitle, image);
        }

        static bool Has(string wrapperType, string dataType, string word)
            => wrapperType.Contains(word, StringComparison.OrdinalIgnoreCase) || string.Equals(dataType, word, StringComparison.Ordinal);

        static string? StrOf(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        static bool TryDig(JsonElement e, out JsonElement value, params string[] path)
        {
            value = e;
            foreach (string p in path)
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(p, out value)) return false;
            return true;
        }

        /// <summary>An image node's best-fit source for a <paramref name="minWidth"/> DIP target (0.2.9's <c>PickImage</c>
        /// took the WIDEST unconditionally — <c>width</c> or <c>maxWidth</c>, the first on a tie — which downloads and
        /// Fant-downscales a 640² master for a 128-DIP shelf thumbnail; <see cref="BrowseImagePick.Choose"/> is the actual
        /// rule). <c>{sources:[…]}</c> or a bare <c>sources</c> array.</summary>
        static string? PickUrl(JsonElement node, int minWidth = BrowseImagePick.DefaultMinWidth)
        {
            var sources = node.ValueKind == JsonValueKind.Object && node.TryGetProperty("sources", out var inner) ? inner : node;
            if (sources.ValueKind != JsonValueKind.Array) return null;
            int len = sources.GetArrayLength();
            if (len == 0) return null;
            Span<(string Url, int Width)> buffer = new (string, int)[len];
            int count = 0;
            foreach (var source in sources.EnumerateArray())
            {
                if (StrOf(source, "url") is not { Length: > 0 } url) continue;
                long w = WidthOf(source, "width") is var a && a > 0 ? a : WidthOf(source, "maxWidth");
                buffer[count++] = (url, w > int.MaxValue ? int.MaxValue : (int)w);
            }
            return BrowseImagePick.Choose(buffer[..count], minWidth);

            static long WidthOf(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
        }

        static string? ImageAt(JsonElement data, params string[] path) => ImageAt(data, BrowseImagePick.DefaultMinWidth, path);

        static string? ImageAt(JsonElement data, int minWidth, params string[] path)
            => TryDig(data, out var node, path) ? PickUrl(node, minWidth) : null;

        /// <summary>The pure source-pick rule (A1, G-045 follow-up): the SMALLEST source whose width is ≥
        /// <paramref name="minWidth"/>, so a 128-DIP shelf thumbnail asks CDN for a ~300 px source instead of the widest
        /// one on offer and the engine's decoder Fant-downscaling a 640² JPEG on every paint. Falls back to the WIDEST
        /// source when none reaches the target (a low-res node has nothing better). First on a tie, either way — matches
        /// 0.2.9's <c>PickImage</c> tie-break so a repeat decode of the same node is stable. Public: engine-free and
        /// unit-tested directly in <c>BrowseDecodeTests</c> (no JSON, no Staging).</summary>
        public static class BrowseImagePick
        {
            /// <summary>Shelf/row/chip thumbnails. Hero and masthead call sites pass
            /// <see cref="HeroMinWidth"/> — Spotify lists 64 then 300 then 640, smallest first.</summary>
            public const int DefaultMinWidth = 300;

            /// <summary>The hero / masthead / stored-cover ask: smallest source whose width is at least 640,
            /// else the widest. One URL is stored per row; decodePx cannot invent pixels from a 64px JPEG.</summary>
            public const int HeroMinWidth = 640;

            public static string? Choose(ReadOnlySpan<(string Url, int Width)> sources, int minWidth)
            {
                Span<int> widths = stackalloc int[sources.Length];
                for (int i = 0; i < sources.Length; i++) widths[i] = sources[i].Width;
                int idx = ChooseIndex(widths, minWidth);
                return idx < 0 ? null : sources[idx].Url;
            }

            /// <summary>Index of <see cref="Choose"/>'s pick, or -1 when <paramref name="widths"/> is empty.
            /// Decoders that intern URLs as <c>TextRef</c> pick by index so they never materialise the URL strings
            /// just to throw all but one away.</summary>
            public static int ChooseIndex(ReadOnlySpan<int> widths, int minWidth)
            {
                int bestAbove = -1, bestAboveWidth = int.MaxValue;
                int widest = -1, widestWidth = -1;
                for (int i = 0; i < widths.Length; i++)
                {
                    int w = widths[i];
                    if (w >= minWidth && w < bestAboveWidth) { bestAbove = i; bestAboveWidth = w; }
                    if (w > widestWidth) { widest = i; widestWidth = w; }
                }
                return bestAbove >= 0 ? bestAbove : widest;
            }
        }

        /// <summary>"Song - A, B, C, ..." (0.2.9 <c>JoinNames</c>): the prefix alone when there are no artists.</summary>
        static string JoinArtists(string prefix, JsonElement data)
        {
            if (!TryDig(data, out var list, "artists", "items") || list.ValueKind != JsonValueKind.Array) return prefix;
            var sb = new System.Text.StringBuilder(prefix);
            int n = 0;
            foreach (var a in list.EnumerateArray())
            {
                string? name = TryDig(a, out var p, "profile", "name") ? p.GetString() : StrOf(a, "name");
                if (name is not { Length: > 0 }) continue;
                if (n == 3) { sb.Append(", ..."); break; }
                sb.Append(n == 0 ? " - " : ", ").Append(name);
                n++;
            }
            return sb.ToString();
        }

        static string? FirstName(JsonElement authors)
        {
            var list = authors.ValueKind == JsonValueKind.Object && authors.TryGetProperty("items", out var items) ? items : authors;
            if (list.ValueKind != JsonValueKind.Array) return null;
            foreach (var a in list.EnumerateArray())
                return StrOf(a, "name") ?? (TryDig(a, out var n, "data", "name") ? n.GetString() : null);
            return null;
        }

        /// <summary>One ranked top hit (the reader on its element): the node, its <c>matchedFields</c> and media type as a
        /// <see cref="SearchRelation.Hits"/> link, and its edge pushed onto the pending results run. A hit whose kind has no
        /// table (a genre, an audiobook, an author) is dropped, exactly as a card with no table is.</summary>
        static void TopHit(ref Utf8JsonReader r, Staging s, bool chrome)
        {
            var node = default(Node);
            int credit = s.CreditMark, mark = s.Edges.PendingMark;
            byte flags = 0;
            for (int depth = r.CurrentDepth; Next(ref r, depth);) HitProperty(ref r, s, ref node, ref flags, credit);
            if (node.ArtistLine.IsEmpty) node.ArtistLine = s.TakeCredit(credit);
            else s.TakeCredit(credit);
            if (s.Edges.Pending(mark) > 0)
            {
                if (node.Uri.IsEmpty) s.Edges.Pop(mark);
                else CloseArtists(s, in node, mark);
            }
            var uri = Stage(s, in node, Authority.Thin);
            if (uri.IsEmpty) return;
            s.Edges.Push().Target = uri;
            if (flags == 0 || !chrome) return;
            ref var link = ref s.SearchLinks.Add();
            link.Target = uri;
            link.Flags = flags;
        }

        /// <summary>A flat hit list (the reader on its property name — <c>itemsV2</c> or a facet's <c>items</c>): every
        /// element through <see cref="TopHit"/>, so a facet row's profile keeps its display name and a song its chrome.</summary>
        static void HitList(ref Utf8JsonReader r, Staging s, bool chrome)
        {
            if (!EnterArray(ref r)) return;
            for (int list = r.CurrentDepth; Element(ref r, list);) TopHit(ref r, s, chrome);
        }

        static void HitProperty(ref Utf8JsonReader r, Staging s, ref Node n, ref byte flags, int credit)
        {
            if (r.ValueTextEquals("matchedFields"u8)) { flags |= MatchedFlags(ref r); return; }
            if (r.ValueTextEquals("item"u8) || r.ValueTextEquals("data"u8))
            {
                for (int d = Fields(ref r); Next(ref r, d);) HitProperty(ref r, s, ref n, ref flags, credit);
                return;
            }
            if (r.ValueTextEquals("trackMediaType"u8))
            {
                r.Read();
                if (Says(ref r, "VIDEO")) flags |= (byte)SearchHitFlags.VideoMedia;
                return;
            }
            if (r.ValueTextEquals("displayName"u8) || r.ValueTextEquals("username"u8))
            {
                r.Read();
                if (n.Name.IsEmpty) n.Name = s.AddJson(ref r);
                return;
            }
            if (r.ValueTextEquals("avatar"u8))
            {
                r.Read();
                var url = FirstUrl(ref r, s);
                if (n.Image.IsEmpty) n.Image = url;
                return;
            }
            NodeProperty(ref r, s, ref n, credit);
        }

        /// <summary><c>matchedFields: ["NAME" | "TITLE" | "LYRICS", …]</c> → the chrome bits.</summary>
        static byte MatchedFlags(ref Utf8JsonReader r)
        {
            if (!EnterArray(ref r)) return 0;
            byte flags = 0;
            int depth = r.CurrentDepth;
            while (r.Read() && !End(ref r, depth))
            {
                if (r.TokenType != JsonTokenType.String) { if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip(); continue; }
                if (Says(ref r, "LYRICS")) flags |= (byte)SearchHitFlags.MatchedLyrics;
                else if (Says(ref r, "TITLE") || Says(ref r, "NAME")) flags |= (byte)SearchHitFlags.MatchedTitle;
            }
            return flags;
        }

        /// <summary><c>chipOrder.items[] { typeName, totalCount? }</c> → facets in server rank, deduped; a missing or zero
        /// total is "the server sent none".</summary>
        static int ChipOrder(ref Utf8JsonReader r, scoped Span<byte> facets, scoped Span<int> totals)
        {
            int n = 0;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    var facet = SearchFacet.All;
                    int total = 0;
                    for (int c = r.CurrentDepth; Next(ref r, c);)
                    {
                        if (r.ValueTextEquals("typeName"u8)) { r.Read(); facet = ChipFacet(ref r); }
                        else if (r.ValueTextEquals("totalCount"u8)) { r.Read(); total = (int)Num(ref r); }
                        else SkipValue(ref r);
                    }
                    if (facet == SearchFacet.All || n >= facets.Length || facets[..n].IndexOf((byte)facet) >= 0) continue;
                    facets[n] = (byte)facet;
                    totals[n++] = total > 0 ? total : Wavee.Search.NoTotal;
                }
            }
            return n;
        }

        /// <summary>A chip's <c>typeName</c> → the facet, including the two alias pairs; anything else is All (dropped).</summary>
        static SearchFacet ChipFacet(ref Utf8JsonReader r)
            => Says(ref r, "TRACKS") || Says(ref r, "SONGS") ? SearchFacet.Tracks
             : Says(ref r, "ALBUMS") ? SearchFacet.Albums
             : Says(ref r, "ARTISTS") ? SearchFacet.Artists
             : Says(ref r, "PLAYLISTS") ? SearchFacet.Playlists
             : Says(ref r, "PODCASTS") || Says(ref r, "SHOWS") || Says(ref r, "PODCASTS_AND_SHOWS") ? SearchFacet.Podcasts
             : Says(ref r, "EPISODES") ? SearchFacet.Episodes
             : Says(ref r, "AUDIOBOOKS") ? SearchFacet.Audiobooks
             : Says(ref r, "USERS") || Says(ref r, "PROFILES") ? SearchFacet.Profiles
             : Says(ref r, "GENRES") || Says(ref r, "GENRES_AND_MOODS") ? SearchFacet.Genres
             : Says(ref r, "AUTHORS") ? SearchFacet.Authors
             : SearchFacet.All;

        /// <summary>A <c>searchV2</c> list key → its facet (the reader on the property name); All for anything else.</summary>
        static SearchFacet SearchListFacet(ref Utf8JsonReader r)
            => r.ValueTextEquals("tracksV2"u8) ? SearchFacet.Tracks
             : r.ValueTextEquals("albumsV2"u8) ? SearchFacet.Albums
             : r.ValueTextEquals("artists"u8) ? SearchFacet.Artists
             : r.ValueTextEquals("playlists"u8) ? SearchFacet.Playlists
             : r.ValueTextEquals("podcasts"u8) ? SearchFacet.Podcasts
             : r.ValueTextEquals("episodes"u8) ? SearchFacet.Episodes
             : r.ValueTextEquals("users"u8) ? SearchFacet.Profiles
             : r.ValueTextEquals("audiobooks"u8) ? SearchFacet.Audiobooks
             : r.ValueTextEquals("authors"u8) ? SearchFacet.Authors
             : r.ValueTextEquals("genres"u8) ? SearchFacet.Genres
             : SearchFacet.All;

        /// <summary>Does the answer carry a NON-EMPTY ranked list? Read off a COPY of the reader (on <c>searchV2</c>), so
        /// the real walk knows before it meets the facet lists whether to collect them.</summary>
        static bool HasTopResults(Utf8JsonReader r)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("topResultsV2"u8)) { SkipValue(ref r); continue; }
                for (int t = Fields(ref r); Next(ref r, t);)
                {
                    if (!r.ValueTextEquals("itemsV2"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    return Element(ref r, r.CurrentDepth);
                }
                return false;
            }
            return false;
        }

        /// <summary>A search subject uri's facet: <c>wavee:search:NN:…</c> with NN a defined <see cref="SearchFacet"/>.</summary>
        public static bool SearchSubjectFacet(ReadOnlySpan<byte> uri, out SearchFacet facet)
        {
            facet = SearchFacet.All;
            ReadOnlySpan<byte> prefix = "wavee:search:"u8;
            if (uri.Length < prefix.Length + 3 || !uri.StartsWith(prefix)) return false;
            int tens = uri[prefix.Length] - '0', ones = uri[prefix.Length + 1] - '0';
            if ((uint)tens > 9 || (uint)ones > 9 || uri[prefix.Length + 2] != ':') return false;
            int index = tens * 10 + ones;
            if (index >= SearchTable.FacetCount) return false;
            facet = (SearchFacet)index;
            return true;
        }

        struct GenreNode
        {
            public TextRef Uri, Name, Image;
            public bool NotFound;
        }

        static void GenreFields(ref Utf8JsonReader r, Staging s, ref GenreNode g)
        {
            for (int d = r.TokenType == JsonTokenType.StartObject ? r.CurrentDepth : Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("data"u8)) GenreFields(ref r, s, ref g);
                else if (r.ValueTextEquals("__typename"u8)) { r.Read(); g.NotFound |= Says(ref r, "NotFound"); }
                else if (r.ValueTextEquals("uri"u8)) { r.Read(); if (g.Uri.IsEmpty) g.Uri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("name"u8)) { r.Read(); if (g.Name.IsEmpty) g.Name = s.AddJson(ref r); }
                else if (r.ValueTextEquals("image"u8)) { r.Read(); var url = FirstUrl(ref r, s); if (g.Image.IsEmpty) g.Image = url; }
                else SkipValue(ref r);
            }
        }

        /// <summary>A <c>SearchAutoCompleteEntity</c>'s text (the reader on an element): <c>item.data.text</c> at whatever
        /// wrapper depth; empty for a rich row.</summary>
        static TextRef AutoCompleteText(ref Utf8JsonReader r, Staging s)
        {
            TextRef text = default;
            for (int d = r.CurrentDepth; Next(ref r, d);)
            {
                if (r.ValueTextEquals("item"u8) || r.ValueTextEquals("data"u8))
                {
                    if (!Enter(ref r)) continue;
                    var inner = AutoCompleteText(ref r, s);
                    if (text.IsEmpty) text = inner;
                }
                else if (r.ValueTextEquals("text"u8)) { r.Read(); if (text.IsEmpty) text = s.AddJson(ref r); }
                else SkipValue(ref r);
            }
            return text;
        }

        static bool SameAsciiIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                int x = a[i], y = b[i];
                if (x is >= 'A' and <= 'Z') x += 32;
                if (y is >= 'A' and <= 'Z') y += 32;
                if (x != y) return false;
            }
            return true;
        }

        static void AppendSearchRun(Staging s, in StagedId parent, SearchRelation relation, int start)
        {
            ref var run = ref s.SearchRuns.Add();
            run.Parent = parent;
            run.Relation = relation;
            run.Start = start;
            run.Length = s.SearchLinks.Count - start;
        }

        /// <summary>Is this staged section one of <see cref="ChartSections.All"/>? An EXACT byte compare — base62 ids are
        /// case-sensitive.</summary>
        static bool IsChartSection(Staging s, in StagedId id)
        {
            if (id.Text.IsEmpty) return false;
            var utf8 = s.Utf8(id.Text);
            for (int i = 0; i < ChartSections.All.Count; i++)
            {
                string c = ChartSections.All[i];
                if (utf8.Length != c.Length) continue;
                int k = 0;
                while (k < c.Length && utf8[k] == c[k]) k++;
                if (k == c.Length) return true;
            }
            return false;
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>{ "hex": "#RRGGBB" }</c> → opaque ARGB, or 0. EXACTLY seven characters starting with <c>#</c>
        /// (0.2.9 <c>SpotifyColor</c>): a malformed value degrades to "no colour", never to a wrong one. A <c>null</c>
        /// colour (Made For You's header) is 0.</summary>
        static uint HexColor(ref Utf8JsonReader r)
        {
            uint colour = 0;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("hex"u8)) { SkipValue(ref r); continue; }
                r.Read();
                if (r.TokenType == JsonTokenType.String && !r.HasValueSequence && r.ValueSpan.Length == 7 && r.ValueSpan[0] == '#')
                    colour = Argb(r.ValueSpan);
            }
            return colour;
        }

        /// <summary>The directory subject's uri — the same identity <c>Entities.BrowseDirectory()</c> mints.</summary>
        static ReadOnlySpan<byte> DirectoryUri => "wavee:browse"u8;

        /// <summary>A browse node's identity from text already in the arena: kept as it is, unless it is a genre uri,
        /// which is folded onto its page uri (one node, Browse.cs).</summary>
        static StagedId PageId(Staging s, TextRef uri)
        {
            if (uri.IsEmpty) return default;
            return StartsWith(s.Utf8(uri), "spotify:genre:") ? PageId(s, s.Utf8(uri)) : new StagedId(uri);
        }

        /// <summary>A browse node's identity: the uri's bytes in the arena, a genre uri folded onto its page uri.</summary>
        static StagedId PageId(Staging s, ReadOnlySpan<byte> uri)
        {
            if (uri.IsEmpty) return default;
            const string genre = "spotify:genre:", page = "spotify:page:";
            if (!StartsWith(uri, genre)) return new StagedId(s.AddText(uri));
            var id = uri[genre.Length..];
            Span<byte> buffer = stackalloc byte[EntityUri.StackChars];
            if (page.Length + id.Length > buffer.Length) return new StagedId(s.AddText(uri));
            for (int i = 0; i < page.Length; i++) buffer[i] = (byte)page[i];
            id.CopyTo(buffer[page.Length..]);
            return new StagedId(s.AddText(buffer[..(page.Length + id.Length)]));
        }

        /// <summary>Walk from the root object into <c>first.second</c>; true with the reader on <c>second</c>'s property
        /// name when both exist.</summary>
        static bool Descend(ref Utf8JsonReader r, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals(first)) { SkipValue(ref r); continue; }
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals(second)) return true;
                    SkipValue(ref r);
                }
                return false;
            }
            return false;
        }

        static void AppendBrowseRun(Staging s, in StagedId parent, BrowseRelation relation, int start, int offset, int total)
        {
            if (parent.IsEmpty) { s.BrowseChildren.Rewind(start); return; }
            ref var run = ref s.BrowseRuns.Add();
            run.Parent = parent;
            run.Relation = relation;
            run.Start = start;
            run.Length = s.BrowseChildren.Count - start;
            run.Offset = offset;
            run.Total = total;
        }
    }
}
