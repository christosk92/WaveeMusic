// ── Spotify/Spotify.Decode.Pathfinder.cs ───────────────────────────────────────────────────────────────────────────
// the Utf8JsonReader pathfinder folds. Named on day one — the plan's own "if > 2k" now holds
//
// Role: CORE
// Owner: E
// Wave: 2
// Budget: 820 lines
// Spec: DERIVED (plan 2,200 + 220)
//
// THE GRAPHQL HALF: `getAlbum`, `getTrack`, `artistOverview`, `searchDesktop`, `home`, `playlistV2`, `libraryV3` —
// the seven answers 0.2.9 mapped in `SpotifyExportMapper` (2,025 lines of `JsonElement`) and `SpotifyHomeComposer`.
// Same shapes, same field picks, one structural change: there is no DOM. A home answer through `JsonDocument` is an
// 806 KB rented buffer, a metadata database and a `JsonElement` per node, re-walked by every `Dig(e, …)` afterwards;
// `Utf8JsonReader` is a cursor, the fold is one forward pass, and a field nobody reads costs a `Skip()`.
//
// THE ONE IDIOM, and nearly every function here is it — `Fields` + `Next` are the four-line enter/depth/read/filter
// preamble, written once:
//
//     for (int d = Fields(ref r); Next(ref r, d);)
//     {
//         if (r.ValueTextEquals("name"u8)) { r.Read(); row.Title = s.AddJson(ref r); }
//         else SkipValue(ref r);                        // skips the whole value, children included
//     }
//
// — and `OneText` / `OneNumber` are that loop already filled in, for an object opened for ONE property
// (`{ isoString }`, `{ shareUrl }`, `{ offset }`, `{ totalMilliseconds }`, `{ nextOffset }`).
//
// FORWARD-ONLY IS WHY `Node` EXISTS. A wrapper's `__typename` and an entity's `uri` are not the first properties of
// their object, and a cursor cannot look ahead — so a node is decoded into one scratch value and its KIND is decided
// at the end from the identity (`StagedId.Kind`), never from a type name the wire may reorder. One reader, one
// `Node`, seven queries.
//
// NESTING IS WHY `s.Edges.Push`/`Close` EXISTS: decoding one card of a shelf also decodes that card's artists, and an
// edge run is a CONTIGUOUS slice, so a list pushes its members onto the pending stack and lands them in one block
// when the walk ends (Entities/Edges.Staging.cs). The protobuf decoders append straight through `EdgeRun` instead,
// because a wire field number arrives in order and their runs are contiguous already.

using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // ── 9. the reader idiom ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Enter the property's value; false when it is not an object (the value is already skipped).</summary>
        static bool Enter(ref Utf8JsonReader r)
        {
            if (r.TokenType == JsonTokenType.PropertyName && !r.Read()) return false;
            if (r.TokenType == JsonTokenType.StartObject) return true;
            r.Skip();
            return false;
        }

        /// <summary>The same, for an array.</summary>
        static bool EnterArray(ref Utf8JsonReader r)
        {
            if (r.TokenType == JsonTokenType.PropertyName && !r.Read()) return false;
            if (r.TokenType == JsonTokenType.StartArray) return true;
            r.Skip();
            return false;
        }

        /// <summary>Enter the property's OBJECT and answer the depth its properties live at, or -1 when it is not one
        /// (the value is already skipped). Pair it with <see cref="Next"/>.</summary>
        static int Fields(ref Utf8JsonReader r) => Enter(ref r) ? r.CurrentDepth : -1;

        /// <summary>Step to the next PROPERTY NAME inside the container that started at <paramref name="depth"/>.
        /// False at its end, and at once for -1 — so a caller never has to test both.</summary>
        static bool Next(ref Utf8JsonReader r, int depth)
        {
            if (depth < 0) return false;
            while (r.Read())
            {
                if (End(ref r, depth)) return false;
                if (r.TokenType == JsonTokenType.PropertyName) return true;
            }
            return false;
        }

        /// <summary>Step to the next OBJECT element of the ARRAY that started at <paramref name="depth"/>.</summary>
        static bool Element(ref Utf8JsonReader r, int depth)
        {
            while (r.Read())
            {
                if (End(ref r, depth)) return false;
                if (r.TokenType == JsonTokenType.StartObject) return true;
            }
            return false;
        }

        /// <summary>Is the reader at the end of the container that started at <paramref name="depth"/>?</summary>
        static bool End(ref Utf8JsonReader r, int depth)
            => r.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray && r.CurrentDepth == depth;

        /// <summary>Skip the value of the property the reader is on — and NOTHING when a helper earlier in an
        /// <c>else if</c> chain already consumed it, which is the bug this exists to make unwritable.</summary>
        static void SkipValue(ref Utf8JsonReader r)
        {
            if (r.TokenType == JsonTokenType.PropertyName) r.Skip();
        }

        /// <summary>A number, however the wire spelled it. Spotify writes `playcount` as a STRING and `totalCount` as
        /// a number in the same answer, and `nextOffset` as either a number or <c>null</c>.</summary>
        static long Num(ref Utf8JsonReader r, long fallback = 0)
            => r.TokenType switch
            {
                JsonTokenType.Number => r.TryGetInt64(out long v) ? v : (long)r.GetDouble(),
                JsonTokenType.String when !r.HasValueSequence => Number(r.ValueSpan) is var n && n >= 0 ? n : fallback,
                JsonTokenType.True => 1,
                JsonTokenType.False => 0,
                _ => fallback,
            };

        static bool Flag(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.True;

        /// <summary>Does the string the reader is ON equal this literal? False for a value split across the input's
        /// buffer boundary, which our single-span readers never produce.</summary>
        static bool Says(ref Utf8JsonReader r, string literal)
            => r.TokenType == JsonTokenType.String && !r.HasValueSequence && Is(r.ValueSpan, literal);

        /// <summary>The ONE string property this object is opened for (<c>{ isoString }</c>, <c>{ shareUrl }</c>).</summary>
        static TextRef OneText(ref Utf8JsonReader r, Staging s, ReadOnlySpan<byte> name)
        {
            TextRef value = default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals(name)) { r.Read(); value = s.AddJson(ref r); }
                else SkipValue(ref r);
            }
            return value;
        }

        /// <summary>The ONE numeric property this object is opened for (<c>{ offset }</c>, <c>{ nextOffset }</c>).
        /// <paramref name="fallback"/> when it is absent OR present-but-not-a-number — which is how
        /// <c>nextOffset: null</c> stays apart from <c>nextOffset: 0</c> (ch 10 §7).</summary>
        static long OneNumber(ref Utf8JsonReader r, ReadOnlySpan<byte> name, long fallback = 0)
        {
            long value = fallback;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals(name)) { r.Read(); value = r.TokenType == JsonTokenType.Number ? Num(ref r) : fallback; }
                else SkipValue(ref r);
            }
            return value;
        }

        /// <summary>The string the reader is on, as a staged IDENTITY: a catalog uri becomes the packed
        /// <see cref="EntityId"/> and its bytes are handed straight back to the arena, so a 200-card home answer
        /// carries no uri text at all; everything else keeps its bytes and the commit resolves them.</summary>
        static StagedId JsonId(ref Utf8JsonReader r, Staging s)
        {
            var text = s.AddJson(ref r);
            if (text.IsEmpty) return default;
            if (!EntityId.TryParseGid(s.Utf8(text), out var id)) return new StagedId(text);
            s.RewindText(text.Offset);                      // it was the last thing written: give the arena back
            return new StagedId(id);
        }

        /// <summary>`{ transformedLabel, translatedBaseText }` — the TRANSFORMED label has the name already
        /// substituted ("Made For Christos"); the base text still carries the "{0}" placeholder and is only the
        /// fallback. A bare string is itself.</summary>
        static TextRef Label(ref Utf8JsonReader r, Staging s)
        {
            if (r.TokenType == JsonTokenType.String) return s.AddJson(ref r);
            TextRef label = default, fallback = default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("transformedLabel"u8)) { r.Read(); label = s.AddJson(ref r); }
                else if (r.ValueTextEquals("translatedBaseText"u8)) { r.Read(); fallback = s.AddJson(ref r); }
                else SkipValue(ref r);
            }
            return label.IsEmpty ? fallback : label;
        }

        /// <summary>An ISO-8601 date at whatever precision the provider states (ch 05 D2). "2019" stays a year; only
        /// a full date parses to an instant. The protobuf shapes take <c>Date(reader, zigzag)</c> instead
        /// (Spotify.Decode.cs) — this one is text and has no wire variant.</summary>
        public static void IsoDate(ReadOnlySpan<byte> iso, out ushort year, out int at, out byte precision)
        {
            year = 0; at = 0; precision = 0;
            if (iso.Length < 4) return;
            long y = Number(iso[..4]);
            if (y is < 1 or > 9999) return;
            year = (ushort)y;
            int month = 1, day = 1;
            if (iso.Length >= 7 && iso[4] == '-' && Number(iso.Slice(5, 2)) is var m && m is >= 1 and <= 12)
            {
                month = (int)m;
                precision = 1;
                if (iso.Length >= 10 && iso[7] == '-' && Number(iso.Slice(8, 2)) is var d && d is >= 1 and <= 31)
                {
                    day = (int)d;
                    precision = 2;
                }
            }
            at = Seconds(year, month, day);
        }

        // ── 10. the node ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One decoded GraphQL entity node, before it knows which table it belongs to. Scratch, never staged:
        /// <see cref="Stage"/> reads the identity's kind and copies the fields that kind has columns for.</summary>
        public struct Node
        {
            public StagedId Uri, AlbumUri, ShowUri, OwnerUri;
            public TextRef Name, Image, Description, ArtistLine;
            public TextRef DateIso, Format, Label, Copyright, Courtesy, ShareUrl;
            /// <summary><c>coverArt.extractedColors.colorRaw.hex</c> as the wire spelled it; the Album arm parses it.</summary>
            public TextRef CoverHex;
            public int DurationMs, TrackCount, ReleaseAt;
            public uint PlayCount, Accent;
            public ushort Year;
            public byte Precision, AlbumKind, Caps;
            public bool Explicit, Ruled, Unavailable;
            /// <summary>Whether <see cref="CloseArtists"/> actually closed a run for THIS node (not merely
            /// attempted — a node with no `artists` field in the JSON pushed nothing, so the call was a no-op and
            /// this stays false). Read by <see cref="Stage"/>'s Album arm to decide <see cref="AlbumFields.Artists"/>:
            /// the album variant of bug C needs the bit set ONLY by a decode that actually produced
            /// <c>Edges.AlbumArtists</c>, never inferred from "this node looks like a full album" the way the old
            /// TrackCount-gated <c>known</c> computation did.</summary>
            public bool ArtistsClosed;
        }

        /// <summary>Decode one entity node — a `Track`, `Album`, `Artist`, `Playlist`, `Show`, `Episode` or any of the
        /// `…ResponseWrapper` shells around them. Unwraps `data` / `item` / `itemV2` / `track` transparently: every
        /// surface wraps the same nodes differently and none of the differences reach a column.</summary>
        public static void EntityNode(ref Utf8JsonReader r, Staging s, ref Node n)
        {
            int credit = s.CreditMark, mark = s.Edges.PendingMark;
            for (int d = Fields(ref r); Next(ref r, d);) NodeProperty(ref r, s, ref n, credit);

            if (n.ArtistLine.IsEmpty) n.ArtistLine = s.TakeCredit(credit);
            else s.TakeCredit(credit);
            n.ArtistsClosed |= CloseArtists(s, in n, mark);
        }

        /// <summary>One property of an entity node. Split out of <see cref="EntityNode"/> because the list walker
        /// needs the same field vocabulary while handling `items` / `releases` itself.</summary>
        static void NodeProperty(ref Utf8JsonReader r, Staging s, ref Node n, int credit)
        {
            // The wrappers. `_uri` is a library row's own spelling of the pseudo-playlist it points at.
            if (r.ValueTextEquals("data"u8) || r.ValueTextEquals("item"u8) || r.ValueTextEquals("itemV2"u8)
                || r.ValueTextEquals("track"u8) || r.ValueTextEquals("content"u8) || r.ValueTextEquals("profile"u8)
                || r.ValueTextEquals("visuals"u8))
            {
                // A wrapper's payload is the SAME node: decode it in place rather than minting a second one, so `data`
                // and `itemV2.data.track` all collapse into one value with no copy and no second credit line.
                for (int d = Fields(ref r); Next(ref r, d);) NodeProperty(ref r, s, ref n, credit);
                return;
            }

            if (r.ValueTextEquals("uri"u8) || r.ValueTextEquals("_uri"u8)) { r.Read(); if (n.Uri.IsEmpty) n.Uri = JsonId(ref r, s); }
            else if (r.ValueTextEquals("name"u8)) { r.Read(); if (n.Name.IsEmpty) n.Name = s.AddJson(ref r); }
            else if (r.ValueTextEquals("description"u8)) { r.Read(); n.Description = s.AddJson(ref r); }
            else if (r.ValueTextEquals("format"u8)) { r.Read(); n.Format = s.AddJson(ref r); }
            else if (r.ValueTextEquals("label"u8)) { r.Read(); n.Label = s.AddJson(ref r); }
            else if (r.ValueTextEquals("playcount"u8)) { r.Read(); long p = Num(ref r); if (p > 0) n.PlayCount = (uint)Math.Min(p, uint.MaxValue); }
            // The cover also carries the provider's extracted colour; the other image fields never do.
            else if (r.ValueTextEquals("coverArt"u8))
            { r.Read(); var url = ImageNode(ref r, s, ref n.CoverHex); if (n.Image.IsEmpty) n.Image = url; }
            // `avatar` is the USER node's own spelling — `ownerV2.data` is
            // `{ __typename, avatar, name, uri, username }` (assets/spotify/icedamericano.json), and without it
            // here a playlist owner's portrait never reached `Node.Image` at all: the vocabulary knew every
            // spelling but the one the only node kind that HAS an avatar actually uses.
            else if (r.ValueTextEquals("avatarImage"u8) || r.ValueTextEquals("images"u8)
                  || r.ValueTextEquals("image"u8) || r.ValueTextEquals("headerImage"u8)
                  || r.ValueTextEquals("avatar"u8))
            { r.Read(); var url = FirstUrl(ref r, s); if (n.Image.IsEmpty) n.Image = url; }
            // The album's own cover is the track/episode's fallback art (S3): a pathfinder track hit carries no
            // `image`/`coverArt` of its own — only its album does — so the child's cover is captured while it is
            // still in hand and handed to the node under the same first-writer-wins rule as `coverArt` above.
            else if (r.ValueTextEquals("albumOfTrack"u8) || r.ValueTextEquals("albumOfEpisode"u8))
            { r.Read(); n.AlbumUri = ChildNode(ref r, s, out var albumCover); if (n.Image.IsEmpty) n.Image = albumCover; }
            else if (r.ValueTextEquals("podcastV2"u8) || r.ValueTextEquals("show"u8)) { r.Read(); n.ShowUri = ChildNode(ref r, s); }
            else if (r.ValueTextEquals("ownerV2"u8) || r.ValueTextEquals("owner"u8)) { r.Read(); n.OwnerUri = ChildNode(ref r, s); }
            else if (r.ValueTextEquals("artists"u8)) Artists(ref r, s, credit);
            else if (r.ValueTextEquals("duration"u8) || r.ValueTextEquals("trackDuration"u8))
                n.DurationMs = (int)OneNumber(ref r, "totalMilliseconds"u8);
            else if (r.ValueTextEquals("contentRating"u8)) n.Explicit |= Rated(ref r);
            else if (r.ValueTextEquals("playability"u8)) Playability(ref r, ref n);
            else if (r.ValueTextEquals("date"u8) || r.ValueTextEquals("releaseDate"u8)) Date(ref r, s, ref n);
            else if (r.ValueTextEquals("type"u8) || r.ValueTextEquals("albumType"u8)) { r.Read(); n.AlbumKind = AlbumKindOf(ref r); }
            else if (r.ValueTextEquals("count"u8) || r.ValueTextEquals("totalCount"u8)) { r.Read(); n.TrackCount = (int)Num(ref r); }
            else if (r.ValueTextEquals("currentUserCapabilities"u8)) n.Caps |= Capabilities(ref r);
            else if (r.ValueTextEquals("copyright"u8)) Copyright(ref r, s, ref n);
            else if (r.ValueTextEquals("sharingInfo"u8)) n.ShareUrl = OneText(ref r, s, "shareUrl"u8);
            else SkipValue(ref r);

            static void Artists(ref Utf8JsonReader r, Staging s, int credit)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        var artist = default(Node);
                        EntityNode(ref r, s, ref artist);
                        if (artist.Uri.IsEmpty) continue;
                        s.Edges.Push().Target = artist.Uri;
                        if (artist.Name.IsEmpty) continue;
                        ref var row = ref s.Artists.RowFor(artist.Uri, Authority.Thin,
                            (uint)(artist.Image.IsEmpty ? ArtistFields.Name : ArtistFields.Identity));
                        row.Name = artist.Name;
                        row.Image = artist.Image;
                        s.AppendCredit(s.Utf8(artist.Name), credit);
                    }
                }
            }

            static bool Rated(ref Utf8JsonReader r)
            {
                bool hit = false;
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("label"u8)) { r.Read(); hit |= Says(ref r, "EXPLICIT"); }
                    else SkipValue(ref r);
                }
                return hit;
            }

            // A RULED row and an unruled one render differently (ch 04 DATA GAPS), so both bits are recorded: the
            // verdict, and the fact that anybody made one.
            static void Playability(ref Utf8JsonReader r, ref Node n)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("playable"u8)) { r.Read(); n.Ruled = true; n.Unavailable = !Flag(ref r); }
                    else SkipValue(ref r);
                }
            }

            static void Date(ref Utf8JsonReader r, Staging s, ref Node n)
            {
                if (r.TokenType == JsonTokenType.PropertyName && !r.Read()) return;
                if (r.TokenType == JsonTokenType.String) { Iso(ref r, s, ref n); return; }
                if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
                int depth = r.CurrentDepth;
                // The wire's own `precision` word beats the string's shape: a provider that knows only the year still
                // spells the isoString in full.
                byte stated = 255;
                while (Next(ref r, depth))
                {
                    if (r.ValueTextEquals("isoString"u8)) { r.Read(); Iso(ref r, s, ref n); }
                    else if (r.ValueTextEquals("year"u8)) { r.Read(); long y = Num(ref r); if (n.Year == 0 && y is > 0 and < 10000) n.Year = (ushort)y; }
                    else if (r.ValueTextEquals("precision"u8))
                    {
                        r.Read();
                        stated = Says(ref r, "YEAR") ? (byte)0 : Says(ref r, "MONTH") ? (byte)1
                               : Says(ref r, "DAY") ? (byte)2 : n.Precision;
                    }
                    else SkipValue(ref r);
                }
                if (stated != 255) n.Precision = stated;

                static void Iso(ref Utf8JsonReader r, Staging s, ref Node n)
                {
                    n.DateIso = s.AddJson(ref r);
                    IsoDate(s.Utf8(n.DateIso), out n.Year, out n.ReleaseAt, out n.Precision);
                }
            }

            static byte AlbumKindOf(ref Utf8JsonReader r)
                => Says(ref r, "SINGLE") ? (byte)Wavee.AlbumKind.Single
                 : Says(ref r, "EP") ? (byte)Wavee.AlbumKind.EP
                 : Says(ref r, "COMPILATION") ? (byte)Wavee.AlbumKind.Compilation
                 : (byte)Wavee.AlbumKind.Album;

            static byte Capabilities(ref Utf8JsonReader r)
            {
                byte caps = 0;
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("canView"u8)) { r.Read(); if (Flag(ref r)) caps |= (byte)PlaylistCaps.CanView; }
                    else if (r.ValueTextEquals("canEditItems"u8)) { r.Read(); if (Flag(ref r)) caps |= (byte)PlaylistCaps.CanEditItems; }
                    else if (r.ValueTextEquals("canEditMetadata"u8)) { r.Read(); if (Flag(ref r)) caps |= (byte)PlaylistCaps.CanEditMetadata; }
                    else if (r.ValueTextEquals("canAdministratePermissions"u8)) { r.Read(); if (Flag(ref r)) caps |= (byte)PlaylistCaps.CanAdministratePermissions; }
                    else SkipValue(ref r);
                }
                return caps;
            }

            // `copyright.items[] { text, type }` — type "C" is the © line, "P" the ℗ courtesy line. The wire's text
            // already carries its symbol, so nothing is prefixed here (`SpotifyExportMapper.JoinCopyrightLines`).
            static void Copyright(ref Utf8JsonReader r, Staging s, ref Node n)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        TextRef text = default;
                        bool phono = false;
                        for (int line = r.CurrentDepth; Next(ref r, line);)
                        {
                            if (r.ValueTextEquals("text"u8)) { r.Read(); text = s.AddJson(ref r); }
                            else if (r.ValueTextEquals("type"u8)) { r.Read(); phono = Says(ref r, "P"); }
                            else SkipValue(ref r);
                        }
                        if (text.IsEmpty) continue;
                        if (phono) { if (n.Courtesy.IsEmpty) n.Courtesy = text; }
                        else if (n.Copyright.IsEmpty) n.Copyright = text;
                    }
                }
            }
        }

        /// <summary>Close a node's own artist run, if it pushed one. An album's artists and a track's are different
        /// relations and the node cannot know which it is until its uri lands — which is why they sit on the stack.
        /// Returns whether a run was actually closed (there was something pending AND a uri to close it against) —
        /// callers OR this into <see cref="Node.ArtistsClosed"/>, which is what <see cref="Stage"/>'s Album arm
        /// gates <see cref="AlbumFields.Artists"/> on (bug C, album variant).</summary>
        static bool CloseArtists(Staging s, in Node n, int mark)
        {
            if (s.Edges.Pending(mark) == 0) return false;
            if (n.Uri.IsEmpty) { s.Edges.Pop(mark); return false; }
            s.Edges.Close(n.Uri.Kind(s) == EntityKind.Album ? Relation.AlbumArtists : Relation.TrackArtists,
                          in n.Uri, mark);
            return true;
        }

        /// <summary>A nested node staged in its own right: a track's album, an episode's show, a playlist's owner.
        /// Always <see cref="Authority.Thin"/> — it is a mention, not a fetch.</summary>
        static StagedId ChildNode(ref Utf8JsonReader r, Staging s) => ChildNode(ref r, s, out _);

        /// <summary>The same, also handing back the child's own <see cref="Node.Image"/> before it is folded into the
        /// staged row — the only way a caller can still see it once <see cref="Stage"/> has copied it away (S3).</summary>
        static StagedId ChildNode(ref Utf8JsonReader r, Staging s, out TextRef image)
        {
            var child = default(Node);
            EntityNode(ref r, s, ref child);
            image = child.Image;
            return Stage(s, in child, Authority.Thin);
        }

        /// <summary>The first `url` under an image node, whatever the shape around it: `{sources:[…]}`,
        /// `{items:[{sources:[…]}]}` or a bare string. Ported from `SpotifyExportMapper.PickImage` minus its
        /// width ranking — 0.3 keeps one image per row and the CDN answers for size.</summary>
        static TextRef FirstUrl(ref Utf8JsonReader r, Staging s)
        {
            if (r.TokenType == JsonTokenType.String) return s.AddJson(ref r);
            if (r.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) { r.Skip(); return default; }
            int depth = r.CurrentDepth;
            TextRef url = default;
            while (r.Read() && !End(ref r, depth))
            {
                if (r.TokenType == JsonTokenType.StartObject && url.IsEmpty) continue;
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("url"u8)) { r.Read(); if (url.IsEmpty) url = s.AddJson(ref r); }
                // `avatarImage` is the container the artist `visuals` node wraps its portrait in
                // (`visuals: { avatarImage: { sources: [...] }, gallery: ... }`); without it the helper skipped the
                // whole portrait and the artistUnion decoder claimed an Image it never read.
                else if (r.ValueTextEquals("sources"u8) || r.ValueTextEquals("items"u8) || r.ValueTextEquals("image"u8)
                         || r.ValueTextEquals("avatarImage"u8))
                { r.Read(); var inner = FirstUrl(ref r, s); if (url.IsEmpty) url = inner; }
                else r.Skip();
            }
            return url;
        }

        /// <summary>Copy a decoded node into the staged row its IDENTITY's kind names, and answer with that identity —
        /// the one place the shape-agnostic node meets the column model, and the reason a home card, a search hit and
        /// a library row need no decoder of their own.</summary>
        public static StagedId Stage(Staging s, in Node n, Authority authority)
        {
            if (n.Uri.IsEmpty) return default;
            switch (n.Uri.Kind(s))
            {
                case EntityKind.Track:
                    {
                        // A nameless hit (S4) must not seal Identity: the row still lands — it is a real edge target,
                        // and its cold groups (PlayCount, Availability) may still be known — but a page that asks
                        // `Ensure(… Identity …)` on it must be answered again rather than reading a permanent blank
                        // title. Mirrors the Artist arm's Image-driven degrade below, keyed on Name instead.
                        uint known = n.Name.IsEmpty ? 0 : (uint)TrackFields.Identity;
                        // ...and a hit that carried NO COVER does not get to claim one either (bug C, the track
                        // variant - `ArtistImageBitTests` is the artist twin of exactly this). `Identity` is a GROUP
                        // (Title|Artists|Album|Duration|Explicit|Image), so sealing it whole on the strength of a name
                        // marked `Image` known with nothing behind it; `Fetch.NeedOf = wanted & ~Known` then saw no
                        // hole to fill and the artwork was never asked for by anyone. That is the blank cover on an
                        // artist's top tracks past the first few rows, and on a playlist's thin rows.
                        if (n.Image.IsEmpty) known &= ~(uint)TrackFields.Image;
                        if (n.PlayCount > 0) known |= (uint)TrackFields.PlayCount;
                        // Only a RULED row speaks for availability: an unruled one must never be dimmed by omission.
                        if (n.Ruled) known |= (uint)TrackFields.Availability;
                        ref var row = ref s.Tracks.RowFor(n.Uri, authority, known);
                        row.Title = n.Name;
                        row.Image = n.Image;
                        row.AlbumUri = n.AlbumUri;
                        row.ArtistLine = n.ArtistLine;
                        row.DurationMs = n.DurationMs;
                        row.PlayCount = n.PlayCount;
                        if (n.Explicit) row.Flags |= (uint)TrackFlags.Explicit;
                        if (n.Ruled && n.Unavailable) row.Flags |= (uint)TrackFlags.Unavailable;
                        return n.Uri;
                    }
                case EntityKind.Album:
                    {
                        uint known = (uint)(AlbumFields.Title | AlbumFields.Image | AlbumFields.Year | AlbumFields.Kind);
                        if (n.TrackCount > 0) known |= (uint)AlbumFields.TrackCount;
                        if (!n.DateIso.IsEmpty) known |= (uint)AlbumFields.Release;
                        // The panel is whole or not at all (ch 05 §7): one of the three arriving is the group.
                        if (!n.Label.IsEmpty || !n.Copyright.IsEmpty || !n.Courtesy.IsEmpty) known |= (uint)AlbumFields.Publishing;
                        // Bug C, album variant: NEVER infer "billed artists known" from a full-looking card the way
                        // Title/Image/Year/Kind are inferred from presence — gate on whether `CloseArtists` actually
                        // closed `Relation.AlbumArtists` for THIS node (`n.ArtistsClosed`, set by every caller above:
                        // `EntityNode`, `Collect`, `GetAlbum`). A thin mention whose JSON carried no `artists` must
                        // not seal the bit — that is exactly what let a cached/thin album's credit line go blank
                        // forever before this fix.
                        if (n.ArtistsClosed) known |= (uint)AlbumFields.Artists;
                        ref var row = ref s.Albums.RowFor(n.Uri, authority, known);
                        row.Title = n.Name;
                        row.Image = n.Image;
                        row.Year = n.Year;
                        row.TrackCount = n.TrackCount;
                        row.Kind = n.AlbumKind;
                        row.Accent = HexColor(s, n.CoverHex);
                        row.ReleaseDateIso = n.DateIso;
                        row.ReleaseAt = n.ReleaseAt;
                        row.DatePrecision = n.Precision;
                        row.Label = n.Label;
                        row.Copyright = n.Copyright;
                        row.Courtesy = n.Courtesy;
                        row.ShareUrl = n.ShareUrl;
                        return n.Uri;
                    }
                case EntityKind.Artist:
                    {
                        ref var row = ref s.Artists.RowFor(n.Uri, authority,
                            (uint)(n.Image.IsEmpty ? ArtistFields.Name : ArtistFields.Identity));
                        row.Name = n.Name;
                        row.Image = n.Image;
                        return n.Uri;
                    }
                case EntityKind.Playlist:
                case EntityKind.Collection:
                    {
                        uint known = (uint)PlaylistFields.Identity;
                        if (n.Caps != 0) known |= (uint)PlaylistFields.Capabilities;
                        if (n.Accent != 0) known |= (uint)PlaylistFields.Accent;
                        if (!n.Format.IsEmpty) known |= (uint)PlaylistFields.Format;
                        ref var row = ref s.Playlists.RowFor(n.Uri, authority, known);
                        row.Title = n.Name;
                        row.Description = n.Description;
                        row.Image = n.Image;
                        row.OwnerUri = n.OwnerUri;
                        row.TrackCount = n.TrackCount;
                        row.Caps = n.Caps;
                        row.Accent = n.Accent;
                        row.ShareUrl = n.ShareUrl;
                        if (!n.Format.IsEmpty) row.Format = FormatOf(s.Utf8(n.Format));
                        return n.Uri;
                    }
                case EntityKind.Show:
                    {
                        uint known = (n.Name.IsEmpty ? 0 : (uint)ShowFields.Title)
                            | (n.Image.IsEmpty ? 0 : (uint)ShowFields.Image);
                        if (!n.Description.IsEmpty) known |= (uint)ShowFields.About;
                        ref var row = ref s.Shows.RowFor(n.Uri, authority, known);
                        row.Title = n.Name;
                        row.Image = n.Image;
                        row.Description = n.Description;
                        return n.Uri;
                    }
                case EntityKind.Episode:
                    {
                        uint known = (uint)EpisodeFields.Identity;
                        if (!n.Description.IsEmpty) known |= (uint)EpisodeFields.About;
                        ref var row = ref s.Episodes.RowFor(n.Uri, authority, known);
                        row.Title = n.Name;
                        row.Image = n.Image;
                        row.Description = n.Description;
                        row.ShowUri = n.ShowUri;
                        row.DurationMs = n.DurationMs;
                        row.PublishedAt = n.ReleaseAt;
                        return n.Uri;
                    }
                case EntityKind.User:
                    {
                        // A nameless mention (S4's shape again, this time an `ownerV2`/`addedBy` stub that carries a
                        // uri and an avatar but no `displayName`) must not seal Identity: the row still lands — the
                        // avatar is a real edge target for whatever thin thing mentioned it — but `UserFields` has no
                        // finer split than one Identity bit covering both Name and Image (unlike the Show arm's
                        // separate Title/Image bits), so `CommitUsers` applies both together or neither. Sealing the
                        // bit here on an empty name would let `Entities.Ensure`'s `wanted & ~known` see the group as
                        // already answered and never ask the profile for the name it never received — exactly what
                        // let a playlist header's owner segment go blank forever ("<owner> · N songs · …" rendering
                        // as "· N songs · …", the avatar the only trace the owner ever existed). A page that asks
                        // `Ensure(… Identity …)` on it must be answered again rather than reading a permanent blank
                        // name. Mirrors the Track arm's identical Name-gated guard above, verbatim.
                        uint known = n.Name.IsEmpty ? 0 : (uint)UserFields.Identity;
                        ref var row = ref s.Users.RowFor(n.Uri, authority, known);
                        row.Name = n.Name;
                        row.Image = n.Image;
                        return n.Uri;
                    }
                // A kind with no table of its own — a section, a concept, a live-stream row. It is not a row and it is
                // not a card: answering `default` is what lets a home band count it as UNSUPPORTED rather than
                // claiming a card it cannot paint (ch 10 §7's ledger).
                default: return default;
            }
        }

        /// <summary>Walk every `items[]` / `releases[]` under the current property, at whatever nesting the wire uses
        /// (`discography.albums.items[].releases.items[]` is two levels), staging each node thin and pushing an edge.
        /// The collector every shelf, grid and facet shares.</summary>
        static void Collect(ref Utf8JsonReader r, Staging s)
        {
            if (r.TokenType == JsonTokenType.PropertyName && !r.Read()) return;
            if (r.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) { r.Skip(); return; }
            int depth = r.CurrentDepth;
            while (r.Read() && !End(ref r, depth))
            {
                if (r.TokenType == JsonTokenType.PropertyName)
                {
                    if (IsList(ref r)) { r.Read(); Collect(ref r, s); }
                    else r.Skip();
                    continue;
                }
                if (r.TokenType == JsonTokenType.StartObject) Item(ref r, s);
            }

            static bool IsList(ref Utf8JsonReader r)
                => r.ValueTextEquals("items"u8) || r.ValueTextEquals("releases"u8);

            // An array element is either the node itself or one more wrapper holding the real list. Both are
            // possible in the same query (`albums.items[]` wraps `releases.items[]`; `topTracks.items[]` does not),
            // and forward-only means the element has to be decoded as both at once: node fields into the node, a
            // nested list into the collector, and no edge for an element that turned out to be a wrapper.
            static void Item(ref Utf8JsonReader r, Staging s)
            {
                var node = default(Node);
                int credit = s.CreditMark, mark = s.Edges.PendingMark;
                bool wrapper = false;

                for (int depth = r.CurrentDepth; Next(ref r, depth);)
                {
                    if (IsList(ref r)) { wrapper = true; r.Read(); Collect(ref r, s); continue; }
                    NodeProperty(ref r, s, ref node, credit);
                }

                if (node.ArtistLine.IsEmpty) node.ArtistLine = s.TakeCredit(credit);
                else s.TakeCredit(credit);
                // The node's own artists were pushed onto the SAME stack as the list being collected, so they have to
                // come off it before this element's edge goes on — otherwise the list's members are not contiguous.
                if (s.Edges.Pending(mark) > 0)
                {
                    if (node.Uri.IsEmpty) { if (!wrapper) s.Edges.Pop(mark); }   // a wrapper's pending edges are its nested list (G-231)
                    else node.ArtistsClosed |= CloseArtists(s, in node, mark);
                }

                var uri = Stage(s, in node, Authority.Thin);
                if (wrapper || uri.IsEmpty) return;
                s.Edges.Push().Target = uri;
            }
        }

        // ── 11. the unions ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>`getAlbum` → the album at <see cref="Authority.Full"/>, its artists, its publishing block and —
        /// when <paramref name="landTracks"/> — one page of its tracklist. `AlbumAnswer`'s offset-0 arm passes
        /// `false`: a "more by" prefetch and the album page's own first tracklist share this SAME persisted query,
        /// but the tracklist there is already owned by `AlbumV4` (`Fetch.Routes.cs`), and a "more by" answer must not
        /// overwrite it. Even when suppressed, the track edges already pushed onto the pending stack are unwound
        /// with `Pop` rather than left to bleed into the next run. Ported from `SpotifyExportMapper.AlbumFromUnion`.</summary>
        public static void GetAlbum(ref Utf8JsonReader r, Staging s, bool landTracks = true)
        {
            var album = default(Node);
            int credit = s.CreditMark, mark = s.Edges.PendingMark;
            int tracks = -1, tracksEnd = -1, total = 0;
            // A leading artist run (pushed before `uri` arrived) cannot be closed on the spot — closing needs to know
            // Album vs Track, which needs the uri. It is left PENDING rather than discarded, and closed later, once
            // the uri is known, by DESCENDING mark (the same idiom `AlbumRelations` uses for more-by/versions):
            // whatever sits ABOVE it on the stack — the tracks, and any trailing artist run — closes first.
            bool deferredLeadingArtists = false;

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("tracksV2"u8) || r.ValueTextEquals("tracks"u8))
                {
                    // The tracklist's members go on the stack AFTER whatever the artists put there, so the artist
                    // run is closed first and the two never share a slice — PROVIDED the uri is already known (only
                    // a known uri says which relation an artist run closes as). Field order is server-controlled (a
                    // persisted GraphQL query): if `artists` arrived before `tracksV2` but `uri` has not arrived yet,
                    // the run stays pending (`deferredLeadingArtists`) instead of being discarded. The trailing
                    // check below (after the outer loop) is what closes a run that shows up AFTER the tracklist;
                    // the deferred-leading close near the bottom is what closes one that showed up BEFORE it.
                    if (album.Uri.IsEmpty) deferredLeadingArtists = s.Edges.Pending(mark) > 0;
                    else album.ArtistsClosed |= CloseArtists(s, in album, mark);
                    tracks = s.Edges.PendingMark;
                    for (int p = Fields(ref r); Next(ref r, p);)
                    {
                        if (r.ValueTextEquals("totalCount"u8)) { r.Read(); total = (int)Num(ref r); }
                        else if (r.ValueTextEquals("items"u8)) Collect(ref r, s);
                        else SkipValue(ref r);
                    }
                    tracksEnd = s.Edges.PendingMark;   // everything pushed past here belongs to a LATER property
                }
                else NodeProperty(ref r, s, ref album, credit);
            }

            if (album.ArtistLine.IsEmpty) album.ArtistLine = s.TakeCredit(credit);
            else s.TakeCredit(credit);
            if (tracks < 0) album.ArtistsClosed |= CloseArtists(s, in album, mark);
            // `artists` arriving AFTER `tracksV2` pushed its run on top of the tracks, on the same stack. Only the
            // top of the stack can be closed (Edges.Staging.cs), so close that trailing run as AlbumArtists now —
            // `album.Uri` is set by this point whenever the `uri` field was present anywhere in the object — before
            // the tracks below fold or pop whatever is left.
            else if (s.Edges.Pending(tracksEnd) > 0) album.ArtistsClosed |= CloseArtists(s, in album, tracksEnd);

            var uri = Stage(s, in album, Authority.Full);
            if (uri.IsEmpty) { s.Edges.Pop(mark); return; }
            // `Stage` already ran and computed `known` off `album.ArtistsClosed` as it stood above — for the
            // ordinary paths (no `tracksV2`, or artists before/after it with `uri` already known) that is correct,
            // because the close happened before this point. The DEFERRED-leading case is the one path whose close
            // cannot happen until after the tracks below are flushed (stack order), i.e. AFTER `Stage` already
            // staged the row — so it cannot flow through `known`. Re-take the row by INDEX, the same defensive
            // pattern `AlbumV4` uses (`Spotify.Decode.cs`) for its own post-loop cover derivation: nothing else
            // stages into `s.Albums` between here and there.
            int albumIndex = s.Albums.Count - 1;
            if (tracks >= 0)
            {
                // The page's ordinal IS the track number here: `tracksV2` states none, and the ordinal is what the
                // album table paints in its `#` lane. The edge's disc/number stay 0, which the row reads as "use the
                // ordinal". A "more by" prefetch (`landTracks: false`) must not overwrite the tracklist AlbumV4
                // already landed — its track edges are unwound with `Pop`, exactly like an unstaged node's run,
                // rather than closed into a page.
                if (landTracks) s.Edges.ClosePage(Relation.AlbumTracks, in uri, tracks, offset: 0, total);
                else s.Edges.Pop(tracks);

                // The deferred leading run is now the LOWEST mark still pending: everything above it (the tracks,
                // and any trailing artist run) was just flushed or popped down to `tracks`, so it is safe to close.
                if (deferredLeadingArtists && CloseArtists(s, in album, mark))
                { ref var albumRow = ref s.Albums[albumIndex]; albumRow.Known |= (uint)AlbumFields.Artists; }
            }
        }

        /// <summary>`getTrack` → the track at <see cref="Authority.Full"/>, with its album and its artists.</summary>
        public static void GetTrack(ref Utf8JsonReader r, Staging s)
        {
            var node = default(Node);
            EntityNode(ref r, s, ref node);
            Stage(s, in node, Authority.Full);
        }

        /// <summary>`artistOverview` → Identity plus the overview halves the columns hold: header image, bio, the
        /// follower / monthly-listener stats and world rank, the popular run and the discography runs.</summary>
        public static void ArtistOverview(ref Utf8JsonReader r, Staging s)
        {
            int depth = Fields(ref r);
            if (depth < 0) return;
            // A LOCAL row, not a `ref` into the staged list: `Collect` stages this artist's related artists into that
            // same list, and a growth would both dangle the reference and move our row away from the tail that
            // `Settle` inspects. It is appended once, at the end, when it has an identity.
            var row = default(StagedArtist);
            row.Authority = Authority.Full;
            row.Known = (uint)ArtistFields.Identity;
            int popular = -1, releases = -1, related = -1;

            while (Next(ref r, depth))
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); row.Id = JsonId(ref r, s); }
                else if (r.ValueTextEquals("headerImage"u8)) { r.Read(); row.Header = FirstUrl(ref r, s); row.Known |= (uint)ArtistFields.Header; }
                else if (r.ValueTextEquals("visuals"u8)) { r.Read(); row.Image = FirstUrl(ref r, s); }
                else if (r.ValueTextEquals("profile"u8)) Profile(ref r, s, ref row);
                else if (r.ValueTextEquals("stats"u8)) { Stats(ref r, ref row); row.Known |= (uint)ArtistFields.Stats; }
                else if (r.ValueTextEquals("discography"u8)) Discography(ref r, s, ref popular, ref releases);
                else if (r.ValueTextEquals("relatedContent"u8)) Related(ref r, s, ref related);
                else SkipValue(ref r);
            }

            // `Known` was declared `Identity` (Name | Image) before `visuals` was even read; an answer with no
            // portrait must not seal `Image` known with an empty column behind it (the chip-rail class of bug,
            // 2026-09-15 — same fix as `Spotify.Decode.cs`'s `ArtistV4`). This decoder is superseded by
            // `Spotify.Decode.Artist.cs`'s `ArtistUnion` (Artist.Rules.cs calls it "legacy") and has no live route,
            // but a dormant producer that still over-claims is a landmine for whoever revives it.
            if (row.Image.IsEmpty) row.Known &= ~(uint)ArtistFields.Image;

            var uri = row.Id;
            if (uri.IsEmpty) return;
            s.Artists.Add() = row;
            CloseRuns(s, in uri, popular, releases, related);

            // CLOSE THE THREE RUNS BY DESCENDING MARK. The pending stack is one arena and only its TOP can be closed:
            // a run closed below another's mark flushes and pops that run's members into itself, and the later close
            // then names a mark past the stack and lands nothing at all. The WIRE decides the order, so the code may
            // not assume one — `artist-maroon5.json` lists `discography.albums` BEFORE `discography.topTracks`, which
            // makes `releases` the LOWER mark, and closing related/releases/popular in that fixed order swallowed all
            // ten popular tracks into the album run and left `Edges.ArtistPopular` empty.
            static void CloseRuns(Staging s, in StagedId parent, int popular, int releases, int related)
            {
                Span<int> marks = [popular, releases, related];
                Span<Relation> relations = [Relation.ArtistPopular, Relation.ArtistAlbums, Relation.ArtistRelated];
                for (int a = 0; a < marks.Length; a++)
                    for (int b = a + 1; b < marks.Length; b++)
                        if (marks[b] > marks[a])
                        {
                            (marks[a], marks[b]) = (marks[b], marks[a]);
                            (relations[a], relations[b]) = (relations[b], relations[a]);
                        }
                for (int a = 0; a < marks.Length; a++)
                    if (marks[a] >= 0) s.Edges.Close(relations[a], in parent, marks[a]);
            }

            static void Profile(ref Utf8JsonReader r, Staging s, ref StagedArtist row)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("name"u8)) { r.Read(); row.Name = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("biography"u8))
                    {
                        var bio = OneText(ref r, s, "text"u8);
                        if (bio.IsEmpty) continue;
                        row.Bio = bio;
                        row.Known |= (uint)ArtistFields.Bio;
                    }
                    else SkipValue(ref r);
                }
            }

            static void Stats(ref Utf8JsonReader r, ref StagedArtist row)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("followers"u8)) { r.Read(); row.Followers = (uint)Math.Clamp(Num(ref r), 0, uint.MaxValue); }
                    else if (r.ValueTextEquals("monthlyListeners"u8)) { r.Read(); row.Monthly = (uint)Math.Clamp(Num(ref r), 0, uint.MaxValue); }
                    else if (r.ValueTextEquals("worldRank"u8)) { r.Read(); row.WorldRank = (ushort)Math.Clamp(Num(ref r), 0, ushort.MaxValue); }
                    else SkipValue(ref r);
                }
            }

            static void Discography(ref Utf8JsonReader r, Staging s, ref int popular, ref int releases)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("topTracks"u8)) { if (popular < 0) popular = s.Edges.PendingMark; Collect(ref r, s); }
                    else if (r.ValueTextEquals("albums"u8) || r.ValueTextEquals("singles"u8)
                          || r.ValueTextEquals("compilations"u8) || r.ValueTextEquals("popularReleasesAlbums"u8))
                    { if (releases < 0) releases = s.Edges.PendingMark; Collect(ref r, s); }
                    else SkipValue(ref r);
                }
            }

            static void Related(ref Utf8JsonReader r, Staging s, ref int related)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("relatedArtists"u8)) { if (related < 0) related = s.Edges.PendingMark; Collect(ref r, s); }
                    else SkipValue(ref r);
                }
            }
        }

        /// <summary>`playlistV2` → the header at <see cref="Authority.Full"/> and one page of the membership, every
        /// per-item fact on the EDGE (D10): the uid that survives a reorder, the added-at instant, the adding user.</summary>
        public static void PlaylistV2(ref Utf8JsonReader r, Staging s)
        {
            var header = default(Node);
            int credit = s.CreditMark, mark = s.Edges.PendingMark;
            int items = -1, total = 0, offset = 0;

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("content"u8))
                {
                    if (items < 0) items = s.Edges.PendingMark;
                    for (int c = Fields(ref r); Next(ref r, c);)
                    {
                        if (r.ValueTextEquals("totalCount"u8)) { r.Read(); total = (int)Num(ref r); }
                        else if (r.ValueTextEquals("pagingInfo"u8)) offset = (int)OneNumber(ref r, "offset"u8);
                        else if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                        {
                            for (int list = r.CurrentDepth; Element(ref r, list);) Item(ref r, s);
                        }
                        else SkipValue(ref r);
                    }
                }
                else NodeProperty(ref r, s, ref header, credit);
            }

            s.TakeCredit(credit);
            if (total > 0) header.TrackCount = total;
            var uri = Stage(s, in header, Authority.Full);
            if (uri.IsEmpty) { s.Edges.Pop(mark); return; }
            if (items < 0) return;

            int n = s.Edges.Pending(items);
            // `offset > 0` or a short page is the server saying there is more; a full first page is the list.
            if (offset > 0 || (total > 0 && n < total)) s.Edges.ClosePage(Relation.PlaylistTracks, in uri, items, offset, total);
            else s.Edges.Close(Relation.PlaylistTracks, in uri, items, EdgeState.Complete, n);

            static void Item(ref Utf8JsonReader r, Staging s)
            {
                var node = default(Node);
                int credit = s.CreditMark, mark = s.Edges.PendingMark;
                TextRef uid = default, addedAt = default;
                StagedId addedBy = default;

                for (int depth = r.CurrentDepth; Next(ref r, depth);)
                {
                    if (r.ValueTextEquals("uid"u8)) { r.Read(); uid = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("addedBy"u8)) { r.Read(); addedBy = ChildNode(ref r, s); }
                    else if (r.ValueTextEquals("addedAt"u8)) addedAt = OneText(ref r, s, "isoString"u8);
                    else NodeProperty(ref r, s, ref node, credit);
                }

                if (node.ArtistLine.IsEmpty) node.ArtistLine = s.TakeCredit(credit);
                else s.TakeCredit(credit);
                if (s.Edges.Pending(mark) > 0) node.ArtistsClosed |= CloseArtists(s, in node, mark);

                var uri = Stage(s, in node, Authority.Thin);
                if (uri.IsEmpty) return;
                ref var edge = ref s.Edges.Push();
                edge.Target = uri;
                edge.Text = uid;
                edge.Aux = addedBy;
                if (!addedAt.IsEmpty) { IsoDate(s.Utf8(addedAt), out _, out int at, out _); edge.At = at; }
            }
        }

        /// <summary>`home` → the feed SUBJECT (greeting + facet chips), one <see cref="StagedSection"/> per band with
        /// its CARDS attached, and the subject's section run (ported from `SpotifyHomeComposer`).
        /// <para>Three facts ch 10 §7 names and 0.2.9 got two of wrong: the paging cursor is
        /// <c>pagingInfo.nextOffset</c> and NOT the total (7 of 31 captured sections disagree with their own
        /// <c>totalCount</c>, and a COMPLETE section can answer <c>nextOffset: 0</c>); the ledger
        /// <c>Raw == Cards + Unsupported + Duplicates</c> is written HERE, because a page that re-asks on the deduped
        /// count walks its cursor backwards; and a band with no cards is still a band.</para></summary>
        public static void Home(ref Utf8JsonReader r, Staging s)
        {
            int depth = Fields(ref r);
            if (depth < 0) return;

            var subject = new StagedId(s.AddText(FeedUri));
            ref var feed = ref s.Homes.RowFor(subject, Authority.Full, (uint)HomeFields.Sections);
            feed.ChipStart = s.Chips.Count;
            int mark = s.Edges.PendingMark, sections = 0;

            while (Next(ref r, depth))
            {
                if (r.ValueTextEquals("greeting"u8))
                {
                    r.Read();
                    feed.Greeting = Label(ref r, s);
                    feed.Known |= (uint)HomeFields.Greeting;
                }
                else if (r.ValueTextEquals("homeChips"u8))
                {
                    Chips(ref r, s, feed.ChipStart);
                    feed.Known |= (uint)HomeFields.Chips;     // 0 chips is a real answer: no strip, greeting still renders
                }
                else if (r.ValueTextEquals("sectionContainer"u8))
                {
                    for (int c = Fields(ref r); Next(ref r, c);)
                    {
                        if (!r.ValueTextEquals("sections"u8)) { SkipValue(ref r); continue; }
                        for (int list = Fields(ref r); Next(ref r, list);)
                        {
                            if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                            for (int items = r.CurrentDepth; Element(ref r, items);)
                            {
                                var uri = Section(ref r, s);
                                if (uri.IsEmpty) continue;
                                s.Edges.Push().Target = uri;
                                sections++;
                            }
                        }
                    }
                }
                else SkipValue(ref r);
            }

            feed.ChipCount = s.Chips.Count - feed.ChipStart;
            if (sections > 0) s.Edges.Close(Relation.HomeSection, in subject, mark);
            else s.Edges.Pop(mark);
        }

        /// <summary>The home subject's uri as bytes — the same identity <c>Entities.HomeFeed()</c> mints.</summary>
        static ReadOnlySpan<byte> FeedUri => "wavee:home"u8;

        /// <summary>`homeChips[] { id, label, subChips[] { id, label } }` → one flat run, each sub-chip carrying the
        /// INDEX of its parent within the run. Chips are not entities (ch 10 §7); the flat shape is how
        /// <c>HomeChip.SubChips</c> survives being a slab.</summary>
        static void Chips(ref Utf8JsonReader r, Staging s, int start)
        {
            if (!EnterArray(ref r)) return;
            for (int list = r.CurrentDepth; Element(ref r, list);)
            {
                int parent = s.Chips.Count - start;
                s.Chips.Add().Parent = -1;                    // added FIRST: forward-only, and subChips may follow
                for (int depth = r.CurrentDepth; Next(ref r, depth);)
                {
                    if (r.ValueTextEquals("id"u8)) { r.Read(); s.Chips[start + parent].Id = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("label"u8)) { r.Read(); s.Chips[start + parent].Label = Label(ref r, s); }
                    else if (r.ValueTextEquals("subChips"u8) && EnterArray(ref r))
                    {
                        for (int subs = r.CurrentDepth; Element(ref r, subs);)
                        {
                            s.Chips.Add().Parent = parent;
                            int at = s.Chips.Count - 1;
                            for (int d = r.CurrentDepth; Next(ref r, d);)
                            {
                                if (r.ValueTextEquals("id"u8)) { r.Read(); s.Chips[at].Id = s.AddJson(ref r); }
                                else if (r.ValueTextEquals("label"u8)) { r.Read(); s.Chips[at].Label = Label(ref r, s); }
                                else SkipValue(ref r);
                            }
                        }
                    }
                    else SkipValue(ref r);
                }
            }
        }

        /// <summary>One band. `sectionItems` carries the ledger, the cursor and THE CARDS; `data` carries the title
        /// and the `__typename` — the ONE place a type name is read, because a section has no uri kind of its own.
        /// <para>The cards land as a <see cref="Relation.SectionCards"/> run over the band (ch 10 §7). They go on the
        /// PENDING stack because this walk is nested inside the home's own section run, and they close HERE, before
        /// the section's edge is pushed, so neither run's slice interleaves with the other's.</para></summary>
        static StagedId Section(ref Utf8JsonReader r, Staging s) => Section(ref r, s, offset: -1);

        /// <summary>One band, as a whole list (<paramref name="offset"/> &lt; 0: the Home feed's inline band) or as the
        /// page at <paramref name="offset"/> of a "Show all" drill (<c>homeSection</c>, G-045). A page lands as a
        /// <c>ReplacePage</c> with an UNSTATED total — the band's own total overshoots in 7 of 31 captured sections, and
        /// a stated one would settle the list Complete on a number the cursor contradicts — and its ledger is the RAW
        /// cursor: <c>Raw = offset + items this page</c>, <c>Cards = offset + cards this page</c>
        /// (<see cref="SectionPaging.Advance"/>).</summary>
        static StagedId Section(ref Utf8JsonReader r, Staging s, int offset)
        {
            ref var row = ref s.Sections.RowFor(default, Authority.Full, (uint)SectionFields.Identity);
            row.NextOffset = SectionPaging.NoCursor;
            int cards = s.Edges.PendingMark;

            for (int depth = r.CurrentDepth; Next(ref r, depth);)
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); if (row.Id.IsEmpty) row.Id = JsonId(ref r, s); }
                else if (r.ValueTextEquals("data"u8)) Data(ref r, s, ref row);
                else if (r.ValueTextEquals("sectionItems"u8)) Items(ref r, s, ref row);
                else SkipValue(ref r);
            }

            var uri = row.Id;
            if (offset > 0)
            {
                row.Raw = SectionPaging.Advance(offset, row.Raw);
                row.Cards += offset;
            }
            if (!s.Sections.Settle()) { s.Edges.Pop(cards); return default; }
            if (offset >= 0) s.Edges.ClosePage(Relation.SectionCards, in uri, cards, offset, total: 0);
            else s.Edges.Close(Relation.SectionCards, in uri, cards);
            return uri;

            static void Data(ref Utf8JsonReader r, Staging s, ref StagedSection row)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("__typename"u8)) { r.Read(); row.Kind = KindOf(ref r); }
                    else if (r.ValueTextEquals("title"u8)) { r.Read(); row.Title = Label(ref r, s); }
                    else if (r.ValueTextEquals("subtitle"u8)) { r.Read(); row.Subtitle = Label(ref r, s); }
                    else SkipValue(ref r);
                }
            }

            static void Items(ref Utf8JsonReader r, Staging s, ref StagedSection row)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("totalCount"u8)) { r.Read(); row.Total = (int)Num(ref r); }
                    // `nextOffset: null` is the server saying "no more", and it is NOT `nextOffset: 0` — which a
                    // COMPLETE section can legitimately answer (ch 10 §7). `NoCursor` is what keeps the two apart.
                    else if (r.ValueTextEquals("pagingInfo"u8))
                        row.NextOffset = (int)OneNumber(ref r, "nextOffset"u8, SectionPaging.NoCursor);
                    else if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                    {
                        for (int list = r.CurrentDepth; Element(ref r, list);)
                        {
                            row.Raw++;
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

            static byte KindOf(ref Utf8JsonReader r)
                => Says(ref r, "HomeSpotlightSectionData") ? (byte)SectionKind.HomeSpotlight
                 : Says(ref r, "HomeRecentlyPlayedSectionData") ? (byte)SectionKind.HomeRecentlyPlayed
                 : Says(ref r, "HomeFeedBaselineSectionData") ? (byte)SectionKind.HomeBaseline
                 : Says(ref r, "HomeShortsSectionData") ? (byte)SectionKind.HomeShorts
                 : (byte)SectionKind.HomeGeneric;
        }

        /// <summary>`searchDesktop` → every facet's hits as one <see cref="Relation.SearchResult"/> run over the
        /// caller's subject, each hit carrying its own kind in the edge payload. Ported from
        /// `SpotifyExportMapper.SearchFromV2` minus its HTML-ified subtitles: a row builds its own line from the
        /// entity it points at, which is why 0.3 has no `Esc`/`HtmlText` pair at all.</summary>
        public static void Search(ref Utf8JsonReader r, ReadOnlySpan<byte> subjectUri, Staging s)
        {
            if (subjectUri.IsEmpty) { r.Skip(); return; }
            var subject = new StagedId(s.AddText(subjectUri));
            int mark = s.Edges.PendingMark;

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("tracksV2"u8) || r.ValueTextEquals("albumsV2"u8) || r.ValueTextEquals("artists"u8)
                    || r.ValueTextEquals("playlists"u8) || r.ValueTextEquals("podcasts"u8) || r.ValueTextEquals("episodes"u8)
                    || r.ValueTextEquals("audiobooks"u8) || r.ValueTextEquals("users"u8) || r.ValueTextEquals("topResultsV2"u8))
                    Collect(ref r, s);
                else SkipValue(ref r);
            }

            if (s.Edges.Pending(mark) > 0) s.Edges.Close(Relation.SearchResult, in subject, mark);
            else s.Edges.Pop(mark);
        }

        // ── 12. the bundled-export offline fixture path (ch 31 §9.5) ─────────────────────────────────────────────────

        /// <summary>THE offline entry point: one captured pathfinder answer — `assets/spotify/*.json`, and equally a
        /// payload the live session has just received — decoded into a <see cref="Staging"/> by dispatching on the
        /// root key under `data` (ported from `SpotifyExport`, ch 31 §9.5). One function and not seven because the
        /// answers differ only in that key, and a fixture path that diverges from the live one proves nothing.
        /// <paramref name="landTracks"/> reaches only the `albumUnion` arm — every other fold ignores it — and
        /// defaults to `true` so every caller but `AlbumAnswer`'s more-by arm (`Spotify.Decode.Album.cs`) is
        /// unaffected.</summary>
        public static void Export(ReadOnlySpan<byte> json, Staging s, ReadOnlySpan<byte> subjectUri = default, bool landTracks = true)
        {
            s.ClearCredit();                                   // the joiner is a stack; start every answer at its floor
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;

            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int data = Fields(ref r); Next(ref r, data);)
                {
                    if (r.ValueTextEquals("home"u8)) { r.Read(); HomeFeed(ref r, subjectUri, s); }
                    else if (r.ValueTextEquals("playlistV2"u8)) { r.Read(); PlaylistV2(ref r, s); }
                    else if (r.ValueTextEquals("artistUnion"u8)) { r.Read(); ArtistOverview(ref r, s); }
                    else if (r.ValueTextEquals("albumUnion"u8)) { r.Read(); GetAlbum(ref r, s, landTracks); }
                    else if (r.ValueTextEquals("trackUnion"u8)) { r.Read(); GetTrack(ref r, s); }
                    else if (r.ValueTextEquals("searchV2"u8)) { r.Read(); Search(ref r, subjectUri, s); }
                    else if (r.ValueTextEquals("me"u8))
                    {
                        r.Read();
                        for (int me = Fields(ref r); Next(ref r, me);)
                        {
                            if (r.ValueTextEquals("libraryV3"u8)) { r.Read(); LibraryV3(ref r, subjectUri.IsEmpty ? DefaultMe : subjectUri, s); }
                            else SkipValue(ref r);
                        }
                    }
                    else SkipValue(ref r);
                }
            }
        }

        /// <summary>The account row a bundled export's library hangs off when the caller names none. Ch 31's fixture
        /// path must render without a signed-in account, and a library with no parent renders as nothing at all.</summary>
        public static ReadOnlySpan<byte> DefaultMe => "spotify:user:wavee-fake"u8;
    }
}

// ── the pathfinder's half of the staging arena ───────────────────────────────────────────────────────────────────────

public sealed partial class Staging
{
    // `Utf8JsonReader.CopyString` writes the UNESCAPED UTF-8 into this, so a name carrying an escape stages as the text
    // it MEANS rather than as its source spelling — and no string is allocated on the way (P14).
    byte[] _json = new byte[512];

    /// <summary>Stage the JSON string the reader is ON, unescaped. The pathfinder's half of <see cref="AddText"/>.</summary>
    public TextRef AddJson(ref System.Text.Json.Utf8JsonReader r)
    {
        if (r.TokenType != System.Text.Json.JsonTokenType.String) return default;
        int max = r.HasValueSequence ? (int)r.ValueSequence.Length : r.ValueSpan.Length;
        if (max == 0) return default;
        if (max > _json.Length) Array.Resize(ref _json, Math.Max(max, _json.Length * 2));
        return AddText(_json.AsSpan(0, r.CopyString(_json)));
    }
}
