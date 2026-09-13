// ── Spotify/Spotify.Decode.Browse.cs ────────────────────────────────────────────────────────────────────────────────
// the browse tree and the "Show all" drills: browseAll, browsePage, browseSection, homeSection (gap batch B1, G-045)
//
// Role: CORE
// Owner: E
// Wave: gap batch B1
// Budget: 360 lines
// Spec: gap register G-045, ch 13 §7 — a named partial of Spotify.Decode.cs (Spotify.Decode.Pathfinder.cs is already
//       past its budget, so the new pathfinder folds live here rather than grow it)
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
