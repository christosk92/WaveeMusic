// ── Spotify/Spotify.Decode.Album.cs ────────────────────────────────────────────────────────────────────────────────
// the album page's pathfinder folds: merch, similar albums, the getAlbum relations (more-by, other versions) and the
// getAlbum tracklist page PAST offset 0
//
// Role: CORE
// Owner: M (Wave 5, stream C of WP-5.M)
// Wave: 5
// Budget: 300 lines (the WP-5 "Spotify.Decode.<Area>.cs" row; no plan §2 line of its own)
// Spec: ch 05 §7 DATA GAPS D4, D5, D8 · gap register G-045 / G-232 (the album half) · 0.2.9
//   `Wavee.Core/Spotify/SpotifyExportMapper.cs` (`AlbumFromUnion` more-by + other versions, `SimilarAlbumsFromTrack`,
//   `AlbumMerch` / `MerchFromItem`)
//
// FOUR ANSWERS, ONE IDIOM. Every fold here is `Spotify.Decode.Pathfinder.cs`'s forward-only `Utf8JsonReader` pass into a
// `Staging`, reusing that file's reader helpers (`Fields` / `Next` / `Element`), its node (`EntityNode` + `Stage`), its
// collector (`Collect`) and its pending edge stack — the same partial class, so none of them is restated:
//
//   queryAlbumMerch               data.albumUnion.merch.items[]                     → Relation.AlbumMerch (MerchTable rows)
//   similarAlbumsBasedOnThisTrack data.seoRecommendedTrackAlbum.items[].data        → Relation.AlbumSimilar, keyed on the
//                                                                                     ALBUM the caller names (the request
//                                                                                     is seeded by a track)
//   getAlbum, offset 0            data.albumUnion.moreAlbumsByArtist.items[].discography.popularReleasesAlbums.items[]
//                                                                                   → Relation.AlbumMoreBy (self excluded)
//                                 data.albumUnion.releases.items[]                  → Relation.AlbumVersions (self and
//                                                                                     duplicates excluded)
//   getAlbum, offset > 0          data.albumUnion.tracksV2 { totalCount, items[] }  → Relation.AlbumTracks as a PAGE at
//                                                                                     that offset (`GetAlbum` lands every
//                                                                                     answer at offset 0, which is why the
//                                                                                     edge door refused a later page)
//
// AN ANSWER THAT NAMES NOTHING IS STILL AN ANSWER. Merch and similar albums land an EMPTY Complete run when the document
// carried no list, and the relations fold closes both runs whenever the album's own uri is known: "this album has no
// other versions" is what stops the page asking, and what lets its section be absent rather than shimmering (finding
// 27's rule, the traits decoder's precedent).

using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>At most this many other versions are kept (a stack-allocated dedupe set).</summary>
        public const int MaxAlbumVersions = 64;

        // ── entry: the getAlbum answer the provider hands over ───────────────────────────────────────────────────────

        /// <summary>One <c>getAlbum</c> answer at <paramref name="offset"/>: the FIRST page is the identity + publishing +
        /// tracklist fold (<see cref="Export"/>) plus the two relations the page's trailing band reads
        /// (<see cref="AlbumRelations"/>); a LATER page is the tracklist page alone, at its own offset.
        /// <paramref name="landTracks"/> defaults to `true` (the album page's own load, where this answer's tracklist
        /// really is the first page) but the "more by" prefetch route (`Fetch.Routes.cs`'s <c>FetchEdge.AlbumMoreBy</c>
        /// → <c>PathfinderOp.GetAlbum</c> at offset 0 — the ONLY route that reaches this arm at offset 0, since
        /// `FetchEdge.AlbumTracks` at offset 0 goes to `AlbumV4` instead) must pass `false`: that answer shares the
        /// exact same persisted query and so still carries a `tracksV2`, but landing it here would overwrite the
        /// tracklist `AlbumV4` already owns.</summary>
        public static void AlbumAnswer(ReadOnlySpan<byte> json, int offset, Staging s, bool landTracks = true)
        {
            if (offset > 0)
            {
                AlbumTracksPage(json, offset, s);
                return;
            }
            Export(json, s, landTracks: landTracks);
            AlbumRelations(json, s);
        }

        // ── queryAlbumMerch ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>queryAlbumMerch</c> → one merch listing per named item, on the album the caller asked about.
        /// Merch is not an entity (§9.6 Q4): each listing lands as a <see cref="MerchTable"/> row, and the staged edge's
        /// union carries its four strings — <c>Text</c> the name, <c>Target</c>'s text the image, <c>Aux</c>'s text the
        /// shop url and <c>(At, U0)</c> the price's <see cref="TextRef"/> (the commit arm in Edges.Staging.cs reads them
        /// back). An unnamed item is not a renderable card and is skipped (0.2.9 <c>MerchFromItem</c>).</summary>
        public static void AlbumMerch(ReadOnlySpan<byte> json, ReadOnlySpan<byte> albumUri, Staging s)
        {
            if (albumUri.IsEmpty) return;
            var parent = Identity(s, albumUri);
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;

            var run = s.Run(Relation.AlbumMerch);
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int data = Fields(ref r); Next(ref r, data);)
                {
                    if (!r.ValueTextEquals("albumUnion"u8)) { SkipValue(ref r); continue; }
                    for (int album = Fields(ref r); Next(ref r, album);)
                    {
                        if (!r.ValueTextEquals("merch"u8)) { SkipValue(ref r); continue; }
                        for (int merch = Fields(ref r); Next(ref r, merch);)
                        {
                            if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                            for (int list = r.CurrentDepth; Element(ref r, list);) MerchItem(ref r, s, ref run);
                        }
                    }
                }
            }
            run.EndEvenIfEmpty(in parent);

            static void MerchItem(ref Utf8JsonReader r, Staging s, ref EdgeRun run)
            {
                TextRef name = default, nameV2 = default, price = default, url = default, image = default;
                for (int depth = r.CurrentDepth; Next(ref r, depth);)
                {
                    if (r.ValueTextEquals("nameV2"u8)) { r.Read(); nameV2 = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("price"u8)) { r.Read(); price = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("url"u8)) { r.Read(); url = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("image"u8)) { r.Read(); image = FirstUrl(ref r, s); }
                    else SkipValue(ref r);
                }
                var title = nameV2.IsEmpty ? name : nameV2;
                if (title.IsEmpty) return;
                ref var edge = ref run.Add();
                edge.Text = title;
                edge.Target = new StagedId(image);
                edge.Aux = new StagedId(url);
                edge.At = price.Offset;
                edge.U0 = (ushort)Math.Min(price.Length, ushort.MaxValue);
            }
        }

        // ── similarAlbumsBasedOnThisTrack ────────────────────────────────────────────────────────────────────────────

        /// <summary><c>similarAlbumsBasedOnThisTrack</c> → each album staged thin (its own artists, cover, year and kind)
        /// and one <see cref="Relation.AlbumSimilar"/> run over <paramref name="albumUri"/> — the album the page is
        /// showing, NOT the seed track the request named (ch 05 D8: "keyed on the album even though the request is
        /// seeded by a track"). A uri-less item is skipped; a document with no list lands an empty run.</summary>
        public static void SimilarAlbums(ReadOnlySpan<byte> json, ReadOnlySpan<byte> albumUri, Staging s)
        {
            if (albumUri.IsEmpty) return;
            s.ClearCredit();
            var parent = Identity(s, albumUri);
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;

            int mark = s.Edges.PendingMark;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int data = Fields(ref r); Next(ref r, data);)
                {
                    if (!r.ValueTextEquals("seoRecommendedTrackAlbum"u8)) { SkipValue(ref r); continue; }
                    for (int shelf = Fields(ref r); Next(ref r, shelf);)
                    {
                        if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                        for (int list = r.CurrentDepth; Element(ref r, list);)
                        {
                            // `{ data: { uri, name, type, date, coverArt, artists } }` — `EntityNode` unwraps `data` and
                            // closes the album's own artist run BEFORE this list's edge goes on the stack.
                            var node = default(Node);
                            EntityNode(ref r, s, ref node);
                            var uri = Stage(s, in node, Authority.Thin);
                            if (!uri.IsEmpty) s.Edges.Push().Target = uri;
                        }
                    }
                }
            }
            s.Edges.Close(Relation.AlbumSimilar, in parent, mark);
        }

        // ── getAlbum: the two relations of the first page ────────────────────────────────────────────────────────────

        /// <summary><c>getAlbum</c> → "More by" (<c>moreAlbumsByArtist</c>) and "Other versions" (<c>releases</c>), both
        /// excluding the album itself, versions de-duplicated (0.2.9 <c>AlbumFromUnion</c>). The album's uri is read
        /// FIRST in its own pass — forward-only cannot exclude a self-reference it has not read yet — and both runs close
        /// whenever it is known, empty included.</summary>
        public static void AlbumRelations(ReadOnlySpan<byte> json, Staging s)
        {
            s.ClearCredit();
            var album = AlbumUnionUri(json, s);
            if (album.IsEmpty) return;
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;

            Span<StagedId> seen = stackalloc StagedId[MaxAlbumVersions];
            int seenCount = 0, more = -1, versions = -1;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int data = Fields(ref r); Next(ref r, data);)
                {
                    if (!r.ValueTextEquals("albumUnion"u8)) { SkipValue(ref r); continue; }
                    for (int union = Fields(ref r); Next(ref r, union);)
                    {
                        if (r.ValueTextEquals("moreAlbumsByArtist"u8))
                        {
                            if (more < 0) more = s.Edges.PendingMark;
                            MoreBy(ref r, s, in album);
                        }
                        else if (r.ValueTextEquals("releases"u8))
                        {
                            if (versions < 0) versions = s.Edges.PendingMark;
                            for (int list = Fields(ref r); Next(ref r, list);)
                            {
                                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                                for (int items = r.CurrentDepth; Element(ref r, items);)
                                    Release(ref r, s, in album, seen, ref seenCount);
                            }
                        }
                        else SkipValue(ref r);
                    }
                }
            }

            // ONLY THE TOP OF THE PENDING STACK CAN BE CLOSED (Edges.Staging.cs): the higher mark first, then an absent
            // list as an empty run at whatever the top is by then.
            if (more > versions)
            {
                s.Edges.Close(Relation.AlbumMoreBy, in album, more);
                if (versions >= 0) s.Edges.Close(Relation.AlbumVersions, in album, versions);
            }
            else
            {
                if (versions >= 0) s.Edges.Close(Relation.AlbumVersions, in album, versions);
                if (more >= 0) s.Edges.Close(Relation.AlbumMoreBy, in album, more);
            }
            if (more < 0) s.Edges.Close(Relation.AlbumMoreBy, in album, s.Edges.PendingMark);
            if (versions < 0) s.Edges.Close(Relation.AlbumVersions, in album, s.Edges.PendingMark);

            static void MoreBy(ref Utf8JsonReader r, Staging s, in StagedId album)
            {
                Span<StagedId> none = default;
                int ignored = 0;
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int groups = r.CurrentDepth; Element(ref r, groups);)
                        for (int g = r.CurrentDepth; Next(ref r, g);)
                        {
                            if (!r.ValueTextEquals("discography"u8)) { SkipValue(ref r); continue; }
                            for (int disc = Fields(ref r); Next(ref r, disc);)
                            {
                                if (!r.ValueTextEquals("popularReleasesAlbums"u8)) { SkipValue(ref r); continue; }
                                for (int pop = Fields(ref r); Next(ref r, pop);)
                                {
                                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                                    for (int list = r.CurrentDepth; Element(ref r, list);)
                                        Release(ref r, s, in album, none, ref ignored);
                                }
                            }
                        }
                }
            }

            // One release node; staged thin and pushed unless it is the album itself or (for a dedupe set) already
            // pushed — decided on the node's identity BEFORE staging, so a self-reference never re-stages the album.
            static void Release(ref Utf8JsonReader r, Staging s, in StagedId album, scoped Span<StagedId> seen, ref int seenCount)
            {
                var node = default(Node);
                EntityNode(ref r, s, ref node);
                if (node.Uri.IsEmpty || SameId(s, in node.Uri, in album)) return;
                if (!seen.IsEmpty)
                {
                    for (int i = 0; i < seenCount; i++)
                        if (SameId(s, in seen[i], in node.Uri)) return;
                    if (seenCount == seen.Length) return;             // past the cap: the menu is long enough
                    seen[seenCount++] = node.Uri;
                }
                var uri = Stage(s, in node, Authority.Thin);
                if (!uri.IsEmpty) s.Edges.Push().Target = uri;
            }
        }

        // ── getAlbum: a tracklist page past offset 0 ─────────────────────────────────────────────────────────────────

        /// <summary>A LATER <c>getAlbum</c> page → its tracks staged thin and ONE <see cref="Relation.AlbumTracks"/> page
        /// at <paramref name="offset"/> with the stated total, so a 300-track box set stays Partial until its length
        /// reaches it (Edges.cs, D7). Nothing else of the answer is re-staged: the first page already carried it.</summary>
        public static void AlbumTracksPage(ReadOnlySpan<byte> json, int offset, Staging s)
        {
            s.ClearCredit();
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;

            StagedId album = default;
            int tracks = -1, total = 0;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int data = Fields(ref r); Next(ref r, data);)
                {
                    if (!r.ValueTextEquals("albumUnion"u8)) { SkipValue(ref r); continue; }
                    for (int union = Fields(ref r); Next(ref r, union);)
                    {
                        if (r.ValueTextEquals("uri"u8)) { r.Read(); if (album.IsEmpty) album = JsonId(ref r, s); }
                        else if (r.ValueTextEquals("tracksV2"u8) || r.ValueTextEquals("tracks"u8))
                        {
                            if (tracks < 0) tracks = s.Edges.PendingMark;
                            for (int p = Fields(ref r); Next(ref r, p);)
                            {
                                if (r.ValueTextEquals("totalCount"u8)) { r.Read(); total = (int)Num(ref r); }
                                else if (r.ValueTextEquals("items"u8)) Collect(ref r, s);
                                else SkipValue(ref r);
                            }
                        }
                        else SkipValue(ref r);
                    }
                }
            }

            if (tracks < 0) return;
            if (album.IsEmpty) { s.Edges.Pop(tracks); return; }
            s.Edges.ClosePage(Relation.AlbumTracks, in album, tracks, Math.Max(0, offset), total);
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>data.albumUnion.uri</c> as a staged identity, or empty — the relations fold's first pass.</summary>
        static StagedId AlbumUnionUri(ReadOnlySpan<byte> json, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return default;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int data = Fields(ref r); Next(ref r, data);)
                {
                    if (!r.ValueTextEquals("albumUnion"u8)) { SkipValue(ref r); continue; }
                    for (int union = Fields(ref r); Next(ref r, union);)
                    {
                        if (r.ValueTextEquals("uri"u8)) { r.Read(); return JsonId(ref r, s); }
                        SkipValue(ref r);
                    }
                }
            }
            return default;
        }

        /// <summary>Do two staged identities name the same row? Packed ids compare as values; text ids as their bytes.</summary>
        static bool SameId(Staging s, in StagedId a, in StagedId b)
            => !a.Packed.IsEmpty || !b.Packed.IsEmpty
                ? a.Packed == b.Packed
                : s.Utf8(a.Text).SequenceEqual(s.Utf8(b.Text));
    }
}
