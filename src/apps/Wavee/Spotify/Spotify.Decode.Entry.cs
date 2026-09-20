// ── Spotify/Spotify.Decode.Entry.cs ─────────────────────────────────────────────────────────────────────────────────
// the raw-body entry points the fetch provider needed and no fold had (gap batch B1b, G-040, G-042)
//
// Role: CORE
// Owner: E (the folds) / F (their one caller, Spotify.Api's fetch provider)
// Wave: gap batch B1b
// Budget: 260 lines
// Spec: gap register G-040 ("PlaylistFields.Saves, ArtistFields.Chart never arrive"), G-042 (collection paging,
//       discography) — a named partial of Spotify.Decode.cs, written beside B1's files rather than into them
//
// FIVE ANSWERS THE ROUTING TABLE NAMES AND NOTHING DECODED. `FetchRoutes` (Entities/Fetch.Routes.cs) sends a playlist's
// Saves to popcount, its Visibility to `/permission/base`, an artist's Chart to the extended top-track list, the
// ArtistReleases relation to `queryArtistDiscographyAll`, and every library relation to collection-v2 paging. The
// provider can only send what it can land, so each of those needed a fold, and each is the file's one shape: pure over
// a span, rows and runs into a `Staging`, no allocation beyond the arena, the commit's business after that.
//
//   popcount            PlaylistPopcount{ count = 7 }                         → StagedPlaylist.Saves        (Saves)
//   permission/base     Permission{ revision = 1, permission_level = 2 }      → Flags.Public + revision     (Visibility)
//   top-tracks-ext      { "tracks": [ { "uri" } ] }                            → Edges.ArtistPopular, ≤ 50   (Chart)
//   discography.all     data.artistUnion.discography.all{ totalCount, items[] } → Edges.ArtistReleases page
//   collection-v2 pages PageResponse{ items[]{ uri, added_at, is_removed } }   → one whole library relation
//
// THE LIBRARY FOLD TAKES EVERY PAGE AT ONCE. A relation run is a contiguous slice of the staged edge list, and the
// "collection" set holds TWO relations (liked tracks and saved albums, told apart only by the item uri) — so the pages
// are walked once per relation, each walk appending one contiguous run. The `ylpin` set (G-062, B2b) is CROSS-KIND, so
// it stages `Relation.Pins` with no item-kind filter at all — `Entities.CommitEdges`'s `Relation.Pins` arm
// (Entities/Edges.Staging.cs) is what drops a shape the sidebar cannot pin.
//
// LIBRARY DELTA SYNC (gap-fix, stage 1 + 2). `LibrarySet` above is kept exactly as it was (every page buffered, one
// whole-list rewrite) because `SpotifyDecodeEntryTests` already pins it; it is no longer the production path.
// `LibraryPageItems` is the streaming half stage 1 needs — one page appended into an ALREADY-OPEN run, so
// `Spotify.Api.Library.cs`'s walk can decode (and discard) a page's bytes as it lands instead of holding every page in
// a `List<byte[]>` until the last one arrives — and `LibrarySet` itself is rewritten to call it once per page, so the
// two never drift apart. `LibraryDelta` is stage 2's decode: a `DeltaResponse` parsed into adds AND removes (unlike
// `LibrarySet`, which drops `is_removed` items outright) — PURE, no Staging, no interning. The apply (`Edges.Insert` /
// `Edges.Remove` against the LIVE relation, with the drift ledger's count checks) is UI-thread SHELL work
// (`Spotify.Library.ApplyCollectionDelta`), never this file's: a decoder may not touch a table (C1).

using System.Text;
using System.Text.Json;

namespace Wavee;

/// <summary>One item off a collection-v2 <c>DeltaResponse</c> (stage 2, G-042): an add (<see cref="Removed"/> false,
/// <see cref="AddedAt"/> meaningful) or a removal (<see cref="AddedAt"/> unused). <see cref="Uri"/> is UTF-8 decoded
/// once, here — the apply step resolves it to a slot on the UI thread, never off it.</summary>
public readonly record struct CollectionDeltaItem(string Uri, int AddedAt, bool Removed);

public static partial class Spotify
{
    public static partial class Decode
    {
        // ── popcount ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>/popcount/v2/playlist/&lt;id&gt;/count</c> → <see cref="PlaylistFields.Saves"/>. The count is
        /// FIELD 7 (<c>popcount.proto</c>: field 1 is its zig-zag shadow and is 0 on half the samples). A count at or past
        /// <see cref="Api.ImplausibleSaveCount"/> is a platform aggregate (the DJ's is 129 M), not a save count, and lands
        /// as 0 — "nothing to render" — so the group is answered and never re-asked.</summary>
        public static void Popcount(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> playlistUri, Staging s)
        {
            var id = Identity(s, playlistUri);
            if (id.IsEmpty) return;
            long count = new ProtoReader(proto).Varint(7);
            ref var row = ref s.Playlists.RowFor(id, Authority.Full, (uint)PlaylistFields.Saves);
            row.Saves = count is < 0 or >= Api.ImplausibleSaveCount ? 0 : (int)count;
        }

        // ── permission/base ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>/playlist-permission/v1/playlist/&lt;id&gt;/permission/base</c> → <see cref="PlaylistFields.Visibility"/>:
        /// public when the base level is VIEWER (2) or above, private at BLOCKED (1), and the revision the invite flyout
        /// writes against as lowercase hex (it is 8 opaque bytes, or the ASCII word <c>default</c> on a playlist never
        /// configured). A body with no level says nothing about visibility and stages nothing: the handle's
        /// "public unless told" default is the honest reading of silence.</summary>
        public static void PermissionBase(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> playlistUri, Staging s)
        {
            var id = Identity(s, playlistUri);
            if (id.IsEmpty) return;
            var r = new ProtoReader(proto);
            ReadOnlySpan<byte> revision = default;
            long level = 0;
            while (r.Next())
            {
                if (r.Field == 1 && r.Wire == 2) revision = r.Bytes();
                else if (r.Field == 2 && r.Wire == 0) level = (long)r.Varint();
                else r.Skip();
            }
            if (level <= 0) return;

            ref var row = ref s.Playlists.RowFor(id, Authority.Full, (uint)PlaylistFields.Visibility);
            row.FlagsMask |= (uint)PlaylistFlags.Public;
            if (level >= 2) row.Flags |= (uint)PlaylistFlags.Public;
            row.PermissionRevision = Hex(s, revision);
        }

        // ── the extended top-track list ──────────────────────────────────────────────────────────────────────────────

        /// <summary><c>/artistplaycontext/v1/page/spotify/artist-top-tracks-extensions/&lt;uri&gt;</c> →
        /// <see cref="ArtistFields.Chart"/>: the artist's popular run REPLACED by the extended list, capped at
        /// <see cref="Api.ArtistTopTracksCap"/>. The bit is the fact (Artist.cs has no chart column); an empty list is a
        /// real answer and lands Complete-and-empty, because a chart that re-asks forever is worse than none.</summary>
        public static void ArtistTopTracks(ReadOnlySpan<byte> json, ReadOnlySpan<byte> artistUri, Staging s)
        {
            var artist = Identity(s, artistUri);
            if (artist.IsEmpty) return;
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;

            int mark = s.PopularMark;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("tracks"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                    for (int item = r.CurrentDepth; Next(ref r, item);)
                    {
                        if (!r.ValueTextEquals("uri"u8)) { SkipValue(ref r); continue; }
                        r.Read();
                        if (r.TokenType != JsonTokenType.String) { r.Skip(); continue; }
                        if (s.PopularTracks.Count - mark >= Api.ArtistTopTracksCap) continue;
                        var track = JsonId(ref r, s);
                        if (!track.IsEmpty && track.Kind(s) == EntityKind.Track) s.PopularTracks.Add() = track;
                    }
            }

            s.Artists.RowFor(artist, Authority.Full, (uint)ArtistFields.Chart);
            s.EndPopular(in artist, mark, extension: true);
        }

        // ── queryArtistDiscographyAll ────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>queryArtistDiscographyAll</c> → one PAGE of <c>Edges.ArtistReleases</c> at
        /// <paramref name="offset"/>, with the server's own total. The items are release GROUPS wrapping
        /// <c>releases.items[]</c>, and a group is ONE row — its first release (0.2.9 <c>AddReleases</c>, and the reason
        /// the total, a group count, lines up with the rows).
        ///
        /// <para>Walked here rather than through <c>Collect</c>: <c>Collect</c>'s element reader pops what a wrapper
        /// element collected when the wrapper itself has no uri (reported). And not routed through <see cref="Export"/>:
        /// this answer shares the overview's <c>artistUnion</c> root with no <c>profile</c>, and the overview fold stages
        /// the artist at <see cref="Authority.Full"/> with Identity known — which would blank the artist's name on every
        /// discography page. Only the relation lands here; the artist row is untouched.</para></summary>
        public static void DiscographyAll(ReadOnlySpan<byte> json, ReadOnlySpan<byte> artistUri, int offset, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "artistUnion"u8)) return;
            int mark = s.Edges.PendingMark, total = 0;
            bool listed = false;

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("discography"u8)) { SkipValue(ref r); continue; }
                for (int g = Fields(ref r); Next(ref r, g);)
                {
                    if (!r.ValueTextEquals("all"u8)) { SkipValue(ref r); continue; }
                    for (int a = Fields(ref r); Next(ref r, a);)
                    {
                        if (r.ValueTextEquals("totalCount"u8)) { r.Read(); total = (int)Num(ref r); }
                        else if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                        {
                            listed = true;
                            for (int groups = r.CurrentDepth; Element(ref r, groups);)
                                for (int group = r.CurrentDepth; Next(ref r, group);)
                                {
                                    if (r.ValueTextEquals("releases"u8)) FirstRelease(ref r, s);
                                    else SkipValue(ref r);
                                }
                        }
                        else SkipValue(ref r);
                    }
                }
            }

            var artist = Identity(s, artistUri);
            if (artist.IsEmpty || !listed) { s.Edges.Pop(mark); return; }
            s.Edges.ClosePage(Relation.ArtistReleases, in artist, mark, Math.Max(0, offset), Math.Max(0, total));

            // `releases { items: [ album, … ] }` → the first album, staged thin and pushed; the rest of the group skipped.
            static void FirstRelease(ref Utf8JsonReader r, Staging s)
            {
                bool taken = false;
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        if (taken) { r.Skip(); continue; }
                        var node = default(Node);
                        EntityNode(ref r, s, ref node);                // closes the release's own artist run first
                        var album = Stage(s, in node, Authority.Thin);
                        if (album.IsEmpty) continue;
                        s.Edges.Push().Target = album;
                        taken = true;
                    }
                }
            }
        }

        // ── collection-v2: a whole library relation ──────────────────────────────────────────────────────────────────

        /// <summary>Every page of one collection-v2 set → the WHOLE <paramref name="set"/> relation over the account's own
        /// row, as one Complete rewrite (an empty library included — "you have no saved albums" is an answer). Items of
        /// another kind are skipped, which is what lets the shared <c>collection</c> set feed both Liked (tracks) and
        /// SavedAlbums (albums); removal tombstones are left out; <c>added_at</c> is the edge's seconds.
        ///
        /// <para><see cref="LibraryEdgeKind.Pins"/> (the <c>ylpin</c> set, G-062) is CROSS-KIND — a playlist, an album,
        /// an artist, a show, the Liked collection or a rootlist folder — so it stages <see cref="Relation.Pins"/> with
        /// NO item-kind filter at all; the commit's <c>Relation.Pins</c> arm (Edges.Staging.cs) is what drops a shape
        /// the sidebar cannot pin (a track, an episode, a prerelease, an unknown scheme).</para></summary>
        public static void LibrarySet(ReadOnlySpan<byte[]> pages, LibraryEdgeKind set, ReadOnlySpan<byte> meUri, Staging s)
        {
            var parent = Identity(s, meUri);
            if (parent.IsEmpty) return;

            if (set == LibraryEdgeKind.Pins)
            {
                var pins = s.Run(Relation.Pins);
                for (int p = 0; p < pages.Length; p++) LibraryPageItems(pages[p], null, s, ref pins);
                pins.EndEvenIfEmpty(in parent);
                return;
            }

            (Relation relation, EntityKind kind) = LibraryRelationOf(set);
            var run = s.Run(relation);
            for (int p = 0; p < pages.Length; p++) LibraryPageItems(pages[p], kind, s, ref run);
            run.EndEvenIfEmpty(in parent);
        }

        /// <summary>The account row a library relation hangs off, resolved once — the streaming walk's PARENT identity
        /// (stage 1, <c>Spotify.Api.Library.cs</c>'s <c>CollectionFullWalk</c>): a public pass-through of the same
        /// <see cref="Identity"/> <see cref="LibrarySet"/> uses, since the provider's edge door lives in a different
        /// nested class and this one is private to it. PURE.</summary>
        public static StagedId LibraryParent(ReadOnlySpan<byte> meUri, Staging s) => Identity(s, meUri);

        /// <summary>Which relation, and which item KIND, a library set decodes into. <see cref="LibraryEdgeKind.Pins"/>
        /// is cross-kind (G-062) and is never asked here — both <see cref="LibrarySet"/> and the streaming walk special
        /// -case it before reaching this switch. PURE.</summary>
        public static (Relation Relation, EntityKind Kind) LibraryRelationOf(LibraryEdgeKind set) => set switch
        {
            LibraryEdgeKind.SavedAlbums => (Relation.SavedAlbums, EntityKind.Album),
            LibraryEdgeKind.FollowedArtists => (Relation.FollowedArtists, EntityKind.Artist),
            LibraryEdgeKind.SavedShows => (Relation.SavedShows, EntityKind.Show),
            _ => (Relation.Liked, EntityKind.Track),
        };

        /// <summary>ONE collection-v2 page's items appended into an ALREADY-OPEN run — the streaming half of
        /// <see cref="LibrarySet"/> (stage 1, G-042): the production walk decodes (and discards) a page's bytes as it
        /// lands instead of buffering every page in a <c>List&lt;byte[]&gt;</c> until the last one arrives.
        /// <paramref name="kindFilter"/> null means "no filter" — the <c>ylpin</c> set's cross-kind targets. Removal
        /// tombstones are dropped, exactly like <see cref="LibrarySet"/>: this is the FULL-WALK fold, and a full walk's
        /// whole-list rewrite already expresses every removal as "not present"; <see cref="LibraryDelta"/> is the one
        /// that must keep them. PURE over the arena; the run is the caller's to close.</summary>
        public static void LibraryPageItems(ReadOnlySpan<byte> page, EntityKind? kindFilter, Staging s, ref EdgeRun run)
        {
            var r = new ProtoReader(page);
            while (r.Next())
            {
                if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                var item = r.Message();                             // CollectionItem { uri = 1, added_at = 2, is_removed = 3 }
                CollectionItem(ref item, out ReadOnlySpan<byte> uri, out int at, out bool removed);
                if (removed || uri.IsEmpty) continue;
                if (kindFilter is { } kind && EntityUri.KindOf(uri) != kind) continue;
                var target = Identity(s, uri);
                if (target.IsEmpty) continue;
                run.Add(in target).At = at;
            }
        }

        /// <summary>The COLLECTING twin of <see cref="LibraryPageItems(ReadOnlySpan{byte},EntityKind?,Staging,ref EdgeRun)"/>
        /// — same walk, same filter, but the identified children go into <paramref name="into"/> instead of straight
        /// into a run. It exists because a <see cref="EdgeRun"/> is a CONTIGUOUS slice of the staging's one edge list,
        /// so the shared <c>collection</c> walk cannot keep two runs open across its pages: the second relation's
        /// children are collected while the pages stream and staged in one run after the first one closes
        /// (<c>Spotify.Api.Library.CollectionFullWalk</c>). PURE over the arena, exactly like the twin.</summary>
        public static void LibraryPageItems(ReadOnlySpan<byte> page, EntityKind? kindFilter, Staging s,
                                            List<(StagedId Target, int At)> into)
        {
            var r = new ProtoReader(page);
            while (r.Next())
            {
                if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                var item = r.Message();
                CollectionItem(ref item, out ReadOnlySpan<byte> uri, out int at, out bool removed);
                if (removed || uri.IsEmpty) continue;
                if (kindFilter is { } kind && EntityUri.KindOf(uri) != kind) continue;
                var target = Identity(s, uri);
                if (target.IsEmpty) continue;
                into.Add((target, at));
            }
        }

        /// <summary>Stage 2's decode: a collection-v2 <c>DeltaResponse</c> — <c>delta_update_possible</c> (field 1, the
        /// return value), every item (field 2, adds AND removes: unlike <see cref="LibraryPageItems"/>, a removal
        /// tombstone is kept, not dropped — a delta is the one place a removal is information rather than "absent from
        /// the whole list"), and the new <c>sync_token</c> (field 3, <paramref name="syncToken"/>; empty when the
        /// answer carried none, which the caller must treat as "cannot trust this delta" — see
        /// <see cref="Spotify.Api.TryCollectionDelta"/>). PURE parsing: no Staging, no interning, no table — the actual
        /// apply is UI-thread SHELL work (<see cref="Spotify.Library.ApplyCollectionDelta"/>), never this method's.
        /// <paramref name="items"/> is cleared first and reused, so a caller may pool it across calls.</summary>
        public static bool LibraryDelta(ReadOnlySpan<byte> body, List<CollectionDeltaItem> items, out string syncToken)
        {
            items.Clear();
            bool possible = false;
            syncToken = "";
            var r = new ProtoReader(body);
            while (r.Next())
            {
                if (r.Field == 1 && r.Wire == 0) possible = r.Bool();
                else if (r.Field == 2 && r.Wire == 2)
                {
                    var item = r.Message();
                    CollectionItem(ref item, out ReadOnlySpan<byte> uri, out int at, out bool removed);
                    if (!uri.IsEmpty) items.Add(new CollectionDeltaItem(Encoding.UTF8.GetString(uri), at, removed));
                }
                else if (r.Field == 3 && r.Wire == 2) syncToken = Encoding.UTF8.GetString(r.Bytes());
                else r.Skip();
            }
            return possible;
        }

        /// <summary>One <c>CollectionItem{ uri = 1, added_at = 2 (seconds), is_removed = 3 }</c>, read once and shared
        /// by every arm of <see cref="LibrarySet"/> (PURE, no allocation).</summary>
        static void CollectionItem(ref ProtoReader item, out ReadOnlySpan<byte> uri, out int at, out bool removed)
        {
            uri = default;
            at = 0;
            removed = false;
            while (item.Next())
            {
                if (item.Field == 1 && item.Wire == 2) uri = item.Bytes();
                else if (item.Field == 2 && item.Wire == 0) at = item.Int32();
                else if (item.Field == 3 && item.Wire == 0) removed = item.Bool();
                else item.Skip();
            }
        }
    }
}
