// ── Spotify/Spotify.Decode.cs ──────────────────────────────────────────────────────────────────────────────────────
// wire → staging columns; RecentsList.Group (ch 16, +150); the bundled-export offline fixture path (ch 31, +70)
//
// Role: CORE
// Owner: E
// Wave: 2
// Budget: 1600 lines
// Spec: plan + ch 16/31 (DERIVED split)
//
// THE DECODER IS THE POINT: bytes in, staged COLUMNS out. Nothing here allocates after warm-up, touches a live table
// or interns a string — a decode runs on the socket's own thread (C10) and the interner has one writer, the UI thread
// (C1). Text goes into the `Staging` arena as UTF-8 (P14); an IDENTITY goes in as a `StagedId`, which IS the packed
// `EntityId` when the wire gave 16 gid bytes.
//
//   dealer / mercury / spclient ──▶ Decode.*(bytes, staging) ──▶ Post ──▶ Entities.Commit ──▶ Publish
//        [socket thread: rows + edge RUNS + one text arena]                  [UI thread: a copy]
//
// NO GENERATED MESSAGE OBJECTS (plan §4.6): parsing a 300-track album into `Wavee.Protocol.*` is thousands of live
// objects for rows that become ~120 bytes of columns. `ProtoReader` is the whole parser — a `ref struct` over the
// frame, a field-number switch, and a length skip for everything the chapters' §7 lists do not name.
//
// THE GID GOES STRAIGHT INTO THE ROW. `EntityId.ForGid` is pure and thread-safe, so a decoder holds the same packed
// identity the commit probes the table with, and no uri string is formatted into the arena and parsed back (the round
// trip Wave 2 shipped with: 81 ns + 37-57 ns per staged row AND per staged edge, for nothing). `Identity` does the
// same for an envelope's `entity_uri`, falling to the arena only for the text population.
//
// FOUR HELPERS CARRY MOST OF THIS FILE, each because the same four lines were written everywhere:
// `ProtoReader.Bytes(n)/Varint(n)/Fields(n1,n2)/Sub(n)` (a message opened for one or two fields — 30 of ~60 nested
// readers); `ThinArtist`/`ThinAlbum`/`ThinShow` (D16's "stage it thin, answer with its identity"); `EdgeRun` (the
// start/count/Truncate quartet, Entities/Edges.Staging.cs); `StagedList.RowFor`/`Settle` (the row prologue/epilogue).

using System.Buffers.Binary;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>Wire → staging. Every method is pure over its input span and its <see cref="Staging"/>: no clock, no
    /// live table, no interner, no allocation after warm-up (P1, P8, P9, C1).</summary>
    public static partial class Decode
    {
        // ── 1. the parser ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Extension kinds this decoder answers for (`extension_kind.proto`). The numbers ARE the protocol;
        /// they are named here so a dispatch reads as the trait it decodes rather than as a magic constant.</summary>
        public enum Ext
        {
            None = 0,
            /// <summary>The format ladder, asked on a DERIVED <c>spotify:audio:</c> entity rather than on the track —
            /// the only route that carries FLAC file ids (FLAC plan §5.1, <see cref="AudioFiles"/>).</summary>
            AudioFiles = 5,
            TrackDescriptor = 6,
            ArtistV4 = 8, AlbumV4 = 9, TrackV4 = 10, ShowV4 = 11, EpisodeV4 = 12,
            /// <summary>The account profile a playlist owner, an added-by cell and the chip read (G-044,
            /// Spotify.Decode.Traits.cs).</summary>
            UserProfile = 15,
            /// <summary>A video rendition's AUDIO counterpart (G-044, <c>Edges.TrackVersions</c>).</summary>
            AudioAssociations = 98,
            VideoAssociations = 99,
            PreRelease = 138,
            /// <summary>"Playlists featuring this album" (G-044, <c>Edges.AlbumRecommendations</c>).</summary>
            RecommendedPlaylists = 151,
            VisualIdentity = 179,
            Publishing = 183,
            PlayCount = 185,
            /// <summary>The credits block (G-044, <c>Edges.TrackCredits</c>).</summary>
            Credits = 186,
            ListMetadataV2 = 205,
            AudioAttributes = 222,
            /// <summary>The three-band waveform (G-044, <c>Edges.TrackWaveform</c>).</summary>
            Waveforms = 237,
        }

        /// <summary>THE protobuf parser for this app's read path: a cursor over one frame, a field-number switch, and
        /// nothing else. It never throws and never allocates — a truncated or garbled frame ends the walk and leaves
        /// whatever was decoded before it, which is the "skip one, keep the batch" discipline 0.2.9's projectors had
        /// and the only correct answer for a 9,000-item page with one bad row in it.</summary>
        public ref struct ProtoReader
        {
            readonly ReadOnlySpan<byte> _b;
            int _p;

            /// <summary>The field number of the tag <see cref="Next"/> just read; 0 at the end.</summary>
            public int Field;
            /// <summary>Its wire type: 0 varint · 1 fixed64 · 2 length-delimited · 5 fixed32.</summary>
            public int Wire;

            public ProtoReader(ReadOnlySpan<byte> bytes) { _b = bytes; _p = 0; Field = 0; Wire = 0; }

            /// <summary>Advance to the next tag. False at the end of the frame, and false — permanently — on anything
            /// malformed, so a caller's <c>while (r.Next())</c> is also its error handling.</summary>
            public bool Next()
            {
                if (_p >= _b.Length) { Field = 0; return false; }
                ulong tag = Varint();
                Field = (int)(tag >> 3);
                Wire = (int)(tag & 7);
                if (Field == 0 || Wire is 3 or 4 or 6 or 7) { _p = _b.Length; Field = 0; return false; }
                return true;
            }

            public ulong Varint()
            {
                ulong value = 0;
                int shift = 0;
                while (_p < _b.Length && shift < 64)
                {
                    byte b = _b[_p++];
                    value |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) return value;
                    shift += 7;
                }
                _p = _b.Length;                       // ran off the end: the frame is over
                return value;
            }

            /// <summary>proto2's <c>sint32</c>/<c>sint64</c> zig-zag. `metadata.proto` spells every number
            /// <c>sint32</c>, so a track's duration read as a plain varint is double what it should be.</summary>
            public long ZigZag() { ulong v = Varint(); return (long)(v >> 1) ^ -(long)(v & 1); }

            public int Int32() => (int)(long)Varint();
            public bool Bool() => Varint() != 0;

            public uint Fixed32()
            {
                if (_p + 4 > _b.Length) { _p = _b.Length; return 0; }
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(_b[_p..]);
                _p += 4;
                return v;
            }

            public ulong Fixed64()
            {
                if (_p + 8 > _b.Length) { _p = _b.Length; return 0; }
                ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_b[_p..]);
                _p += 8;
                return v;
            }

            public double Double() => BitConverter.Int64BitsToDouble((long)Fixed64());

            /// <summary>A length-delimited field's bytes, as a SLICE of the frame — no copy. Anything a staged row
            /// keeps is copied into the arena by <see cref="Staging.AddText"/>; the frame itself is recycled the
            /// moment the decode returns.</summary>
            public ReadOnlySpan<byte> Bytes()
            {
                int len = (int)Varint();
                if (len < 0 || _p + len > _b.Length) { _p = _b.Length; return default; }
                var slice = _b.Slice(_p, len);
                _p += len;
                return slice;
            }

            /// <summary>A nested message, as its own reader.</summary>
            public ProtoReader Message() => new(Bytes());

            // ── reading a message for one or two fields ─────────────────────────────────────────────────────────────
            //
            // Thirty of this file's nested readers open a sub-message only to pull one or two values out of it, and
            // each of them was a five-line `while (x.Next()) switch (x.Field)`. These four are that loop, written once.
            // They CONSUME the reader, which is exactly right for a message opened for this purpose and nothing else,
            // and they are order-independent: a wire that emits `type` after `id` decodes identically.

            /// <summary>The first length-delimited field <paramref name="n"/> of this message, or empty.</summary>
            public ReadOnlySpan<byte> Bytes(int n)
            {
                while (Next())
                {
                    if (Field == n && Wire == 2) return Bytes();
                    Skip();
                }
                return default;
            }

            /// <summary>Two length-delimited fields in ONE pass, in either wire order — the (gid, name),
            /// (type, id), (key, value) shape this protocol is built out of.</summary>
            public void Fields(int n1, int n2, out ReadOnlySpan<byte> a, out ReadOnlySpan<byte> b)
            {
                a = default;
                b = default;
                while (Next())
                {
                    if (Field == n1 && Wire == 2 && a.IsEmpty) a = Bytes();
                    else if (Field == n2 && Wire == 2 && b.IsEmpty) b = Bytes();
                    else Skip();
                }
            }

            /// <summary>The first varint field <paramref name="n"/>, or <paramref name="fallback"/> when the message
            /// does not state it (a header with no status code is a success, not a zero).</summary>
            public long Varint(int n, long fallback = 0)
            {
                while (Next())
                {
                    if (Field == n && Wire == 0) return (long)Varint();
                    Skip();
                }
                return fallback;
            }

            /// <summary>The first sub-message at field <paramref name="n"/>, as its own reader — so a two-level pull
            /// is <c>r.Sub(1).Bytes(2)</c> rather than two nested loops.</summary>
            public ProtoReader Sub(int n) => new(Bytes(n));

            /// <summary>Skip the value of the tag just read — the branch that makes reading six fields out of eighty
            /// cost what six fields cost.</summary>
            public void Skip()
            {
                switch (Wire)
                {
                    case 0: Varint(); break;
                    case 1: _p += 8; break;
                    case 2: Bytes(); break;
                    case 5: _p += 4; break;
                    default: _p = _b.Length; break;
                }
                if (_p > _b.Length) _p = _b.Length;
            }
        }

        // ── 2. staging helpers ───────────────────────────────────────────────────────────────────────────────────────

        const string ImagePrefix = "https://i.scdn.co/image/";

        /// <summary>An envelope's <c>entity_uri</c> → a staged identity. A catalog uri parses straight to the packed
        /// <see cref="EntityId"/> with NO text anywhere (<see cref="EntityId.TryParseGid(ReadOnlySpan{byte},out EntityId)"/>
        /// is pure and thread-safe); everything else keeps its bytes in the arena and the commit resolves them.</summary>
        static StagedId Identity(Staging s, ReadOnlySpan<byte> uri)
            => uri.IsEmpty ? default
             : EntityId.TryParseGid(uri, out var id) ? new StagedId(id) : new StagedId(s.AddText(uri));

        /// <summary>Stage an image: a content file id → the CDN url's UTF-8. One stack buffer, one hex encode; the
        /// row's <c>Image</c> column is that url, interned once at commit.</summary>
        static TextRef Image(Staging s, ReadOnlySpan<byte> fileId)
        {
            if (fileId.IsEmpty || fileId.Length > 32) return default;
            Span<byte> buf = stackalloc byte[ImagePrefix.Length + 64];
            int n = 0;
            for (int i = 0; i < ImagePrefix.Length; i++) buf[n++] = (byte)ImagePrefix[i];
            for (int i = 0; i < fileId.Length; i++)
            {
                buf[n++] = Nibble(fileId[i] >> 4);
                buf[n++] = Nibble(fileId[i] & 0xF);
            }
            return s.AddText(buf[..n]);
        }

        static byte Nibble(int v) => (byte)(v < 10 ? '0' + v : 'a' + (v - 10));

        /// <summary>Bytes → lowercase hex, staged. The playlist and recents wires both key a row on the hex spelling
        /// of its `item_id`, and the two must agree byte for byte or the reconciler loses every row.</summary>
        static TextRef Hex(Staging s, ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty || bytes.Length > 64) return default;
            Span<byte> buf = stackalloc byte[128];
            int n = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                buf[n++] = Nibble(bytes[i] >> 4);
                buf[n++] = Nibble(bytes[i] & 0xF);
            }
            return s.AddText(buf[..n]);
        }

        /// <summary>A bare username → <c>spotify:user:&lt;name&gt;</c>, staged. The wire spells a playlist's owner and
        /// an item's adder as the username alone; every reader of them wants the uri.</summary>
        static StagedId UserUri(Staging s, ReadOnlySpan<byte> username)
        {
            const string prefix = "spotify:user:";
            if (StartsWith(username, "spotify:")) return Identity(s, username);
            if (username.IsEmpty || username.Length > 200) return default;
            Span<byte> buf = stackalloc byte[224];
            int n = 0;
            for (int i = 0; i < prefix.Length; i++) buf[n++] = (byte)prefix[i];
            username.CopyTo(buf[n..]);
            n += username.Length;
            return s.AddText(buf[..n]);
        }

        /// <summary>The cover pick, ported from `ExtendedMetadataSource.PickImage`: the DEFAULT render (size 0,
        /// ~300 px) is the card/list source and the first render is the fallback when the group names no DEFAULT.
        /// 0.3 keeps ONE image per row, so the "largest" arm of the 0.2.9 rule has no column to land in and is not
        /// ported — the hero asks the CDN for its own size.</summary>
        static TextRef Cover(Staging s, ProtoReader group)
        {
            ReadOnlySpan<byte> chosen = default, fallback = default;
            while (group.Next())
            {
                if (group.Field != 1) { group.Skip(); continue; }             // ImageGroup.image
                // `Image.size` is an ENUM, which is a plain varint: read as a zig-zag `sint32`, SMALL (1) would
                // decode as -1 and every group would look like it named no DEFAULT.
                var img = group.Message();
                ReadOnlySpan<byte> fileId = default;
                int size = -1;
                while (img.Next())
                {
                    if (img.Field == 1) fileId = img.Bytes();                 // Image.file_id
                    else if (img.Field == 2) size = img.Int32();              // Image.size (0 = DEFAULT)
                    else img.Skip();
                }
                if (fileId.IsEmpty) continue;
                if (fallback.IsEmpty) fallback = fileId;
                if (size == 0 && chosen.IsEmpty) chosen = fileId;
            }
            return Image(s, chosen.IsEmpty ? fallback : chosen);
        }

        /// <summary>A wire date → the year and, when the provider knows more than a year, a seconds instant and its
        /// precision (ch 05 D2: the panel states the date at the provider's OWN precision, so a bare "2019" must not
        /// become 1 Jan 2019 anywhere but in the sort key).
        ///
        /// <para><paramref name="zigzag"/> is the ONE difference between the two shapes this protocol uses:
        /// <c>metadata.Date</c> is proto2 and spells its three numbers <c>sint32</c>, while
        /// <c>PublishingMetadataTrait.Date</c> is proto3 and spells them as plain <c>int32</c>. Reading either with
        /// the other's rule halves or doubles every value, so the flag is the whole fold. The ISO-8601 shape the
        /// pathfinder answers with is a different problem and keeps its own function
        /// (<c>IsoDate</c>, Spotify.Decode.Pathfinder.cs).</para></summary>
        static void Date(ProtoReader date, bool zigzag, out ushort year, out int at, out byte precision)
        {
            int y = 0, m = 0, d = 0;
            while (date.Next())
            {
                switch (date.Field)
                {
                    case 1: y = Number(ref date, zigzag); break;
                    case 2: m = Number(ref date, zigzag); break;
                    case 3: d = Number(ref date, zigzag); break;
                    default: date.Skip(); break;
                }
            }
            year = y is > 0 and < 10000 ? (ushort)y : (ushort)0;
            precision = d > 0 ? (byte)2 : m > 0 ? (byte)1 : (byte)0;
            at = year == 0 ? 0 : Seconds(year, m <= 0 ? 1 : m > 12 ? 12 : m, d <= 0 ? 1 : d > 31 ? 31 : d);

            static int Number(ref ProtoReader r, bool zigzag) => zigzag ? (int)r.ZigZag() : r.Int32();
        }

        /// <summary>Civil date → unix seconds, branchless (Howard Hinnant's days-from-civil). Core never constructs a
        /// <c>DateTimeOffset</c> on a decode path: it allocates nothing but it does walk the calendar tables, and
        /// this runs once per staged album row.</summary>
        public static int Seconds(int year, int month, int day)
        {
            int y = year - (month <= 2 ? 1 : 0);
            int era = (y >= 0 ? y : y - 399) / 400;
            int yoe = y - era * 400;
            int doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
            int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
            long days = (long)era * 146097 + doe - 719468;
            long seconds = days * 86400;
            return seconds is > int.MinValue and < int.MaxValue ? (int)seconds : 0;
        }

        /// <summary>ASCII-insensitive compare of a wire token against a literal. No transcode, no allocation — the
        /// wire's tokens are ASCII by construction and this runs per row.</summary>
        internal static bool Is(ReadOnlySpan<byte> utf8, string literal)
        {
            if (utf8.Length != literal.Length) return false;
            for (int i = 0; i < literal.Length; i++)
            {
                int a = utf8[i], b = literal[i];
                if (a is >= 'A' and <= 'Z') a += 32;
                if (b is >= 'A' and <= 'Z') b += 32;
                if (a != b) return false;
            }
            return true;
        }

        internal static bool StartsWith(ReadOnlySpan<byte> utf8, string literal)
            => utf8.Length >= literal.Length && Is(utf8[..literal.Length], literal);

        /// <summary>A non-negative integer out of ASCII digits; -1 when the span is not one. The wire spells a chart
        /// position, a group id and a timestamp as TEXT in places, and this is how they are read without a
        /// transcode (P14).</summary>
        internal static long Number(ReadOnlySpan<byte> utf8)
        {
            if (utf8.IsEmpty) return -1;
            long value = 0;
            for (int i = 0; i < utf8.Length; i++)
            {
                int d = utf8[i] - '0';
                if ((uint)d > 9) return -1;
                value = value * 10 + d;
                if (value > int.MaxValue) return int.MaxValue;
            }
            return value;
        }

        /// <summary>Clamp a wire instant (unix seconds) into the <c>int</c> column that holds it.</summary>
        static int AtMost(long seconds) => seconds > int.MaxValue ? int.MaxValue : seconds < 0 ? 0 : (int)seconds;

        // ── 3. the thin rows a container carries inside itself ───────────────────────────────────────────────────────
        //
        // D16 in three functions. A track names its album and its artists, an album names its artists, an episode names
        // its show — each with a gid, a name and sometimes a cover, and each is staged at `Authority.Thin` so a real
        // V4 answer for the same row always wins whichever order the two land in. The three shapes differ only in which
        // columns they fill; the DECISION (stage it thin, never claim more than the wire said) is one rule.

        /// <summary>The artist inside a track or an album: gid + name. Answers with the identity, and hands back the
        /// name so the caller can join it into the credit line without re-reading the message.</summary>
        static StagedId ThinArtist(ProtoReader artist, Staging s, out ReadOnlySpan<byte> name)
        {
            artist.Fields(1, 2, out var gid, out name);
            var id = EntityId.ForGid(EntityKind.Artist, gid);
            if (id.IsEmpty || name.IsEmpty) return id;
            s.Artists.RowFor(id, Authority.Thin, (uint)ArtistFields.Name).Name = s.AddText(name);
            return id;
        }

        /// <summary>The album a track carries inside itself: enough for the row's "go to the container" lane and the
        /// drawer's cover.</summary>
        static StagedId ThinAlbum(ProtoReader album, Staging s, out ushort year)
        {
            StagedId id = default;
            TextRef title = default, cover = default;
            year = 0;
            int at = 0;
            byte precision = 0;
            while (album.Next())
            {
                if (album.Field == 1) id = EntityId.ForGid(EntityKind.Album, album.Bytes());
                else if (album.Field == 2) title = s.AddText(album.Bytes());
                else if (album.Field == 6) Date(album.Message(), zigzag: true, out year, out at, out precision);
                else if (album.Field == 17) cover = Cover(s, album.Message());
                else album.Skip();
            }
            if (id.IsEmpty) return default;

            uint known = (uint)(AlbumFields.Title | AlbumFields.Image | AlbumFields.Year);
            if (at != 0) known |= (uint)AlbumFields.Release;
            ref var row = ref s.Albums.RowFor(id, Authority.Thin, known);
            row.Title = title;
            row.Image = cover;
            row.Year = year;
            row.ReleaseAt = at;
            row.DatePrecision = precision;
            return id;
        }

        /// <summary>The show an episode carries inside itself: uri, title, publisher.</summary>
        static StagedId ThinShow(ProtoReader show, Staging s)
        {
            StagedId id = default;
            TextRef title = default, publisher = default;
            while (show.Next())
            {
                if (show.Field == 1) id = EntityId.ForGid(EntityKind.Show, show.Bytes());
                else if (show.Field == 2) title = s.AddText(show.Bytes());
                else if (show.Field == 66) publisher = s.AddText(show.Bytes());
                else show.Skip();
            }
            if (id.IsEmpty) return default;
            ref var row = ref s.Shows.RowFor(id, Authority.Thin, (uint)ShowFields.Identity);
            row.Title = title;
            row.Publisher = publisher;
            return id;
        }

        // ── 4. the V4 metadata decoders ──────────────────────────────────────────────────────────────────────────────

        /// <summary>`metadata.Track` (extension kind 10) → one <see cref="StagedTrack"/> at
        /// <see cref="Authority.Full"/>, its artists as a <see cref="Relation.TrackArtists"/> run, and a THIN album
        /// row so the track's container is nameable before anyone opens it.
        ///
        /// <para>Groups filled: Identity (ch 01 §7's row), Year and Availability (ch 01 §7's not-yet-out dim + date),
        /// Isrc and Canonical (ch 04 §7's versions panel). The credit line is joined here, at decode, and interned
        /// ONCE at commit — `TrackTable.ArtistLine` exists precisely so a 200-row page does not join names per frame
        /// (ch 01 GAP 2) — and the per-artist click targets still come off the `TrackArtists` edge.</para></summary>
        public static void TrackV4(ReadOnlySpan<byte> proto, Staging s)
        {
            var r = new ProtoReader(proto);
            ref var row = ref s.Tracks.RowFor(default, Authority.Full,
                (uint)(TrackFields.Identity | TrackFields.Year | TrackFields.Availability));

            var artists = s.Run(Relation.TrackArtists);
            long earliestLive = 0;
            bool sawIsrc = false;
            s.ClearCredit();

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: row.Id = EntityId.ForGid(EntityKind.Track, r.Bytes()); break;
                    case 2: row.Title = s.AddText(r.Bytes()); break;
                    case 3:                                            // album (thin: uri, title, cover, date)
                        row.AlbumUri = ThinAlbum(r.Message(), s, out ushort albumYear);
                        if (row.Year == 0) row.Year = albumYear;
                        break;
                    case 4:                                            // artist
                        {
                            var artist = ThinArtist(r.Message(), s, out var name);
                            if (artist.IsEmpty) break;
                            artists.Add(in artist);
                            s.AppendCredit(name);
                            break;
                        }
                    case 7: row.DurationMs = (int)r.ZigZag(); break;
                    case 9: if (r.Bool()) row.Flags |= (uint)TrackFlags.Explicit; break;
                    case 10:                                           // external_id: isrc
                        {
                            r.Message().Fields(1, 2, out var type, out var external);
                            if (sawIsrc || !Is(type, "isrc") || external.IsEmpty) break;
                            row.Isrc = s.AddText(external);
                            row.Known |= (uint)TrackFields.Isrc;
                            sawIsrc = true;
                            break;
                        }
                    case 17: earliestLive = (long)r.Varint(); break;
                    case 24:                                           // original_audio { uuid = 1 }
                        // THE KEY TO THE FORMAT LADDER, and the only payload that carries it (FLAC plan §5.2): the
                        // uuid becomes `spotify:audio:<base62(uuid)>`, and extension kind 5 on THAT entity is the only
                        // place a FLAC file id is served. The 16 bytes are read the way every gid is (big-endian,
                        // shorter spans left-padded) so the base62 spelling matches `Spotify.Audio`'s per-open encode.
                        row.OriginalAudio = Base62.ReadBytes(r.Message().Bytes(1));
                        break;
                    case 36:                                           // canonical_uri
                        row.CanonicalUri = Identity(s, r.Bytes());
                        row.Known |= (uint)TrackFields.Canonical;
                        break;
                    case 38:                                           // original_video[] { gid = 1 }
                        // THE MANIFEST ID (G-056): the FIRST rendition's gid, as the lowercase hex
                        // `/manifests/v9/json/sources/{id}` is addressed by. Present on a self-contained music-video
                        // track; a linked-uri track has none and resolves through its counterpart (TrackTable.VideoGid).
                        if (row.VideoGid.IsEmpty) row.VideoGid = Hex(s, r.Message().Bytes(1));
                        else r.Skip();
                        break;
                    default: r.Skip(); break;
                }
            }

            // "Unavailable" is a VERDICT and only ever a verdict (ch 04 DATA GAPS): an unruled row reads as playable.
            // `earliest_live_timestamp` is the one availability statement TrackV4 makes — the row is not out yet — and
            // the date is kept so the dim expires itself with no refetch (ch 01 §7).
            if (earliestLive > 0)
            {
                row.AvailableAt = AtMost(earliestLive);
                row.Flags |= (uint)TrackFlags.Unavailable;
            }
            row.ArtistLine = s.TakeCredit();

            // A payload that named NO original audio has no audio entity behind it, and therefore no format ladder —
            // ever. That is a real negative, and this is the only place it can be stated: the planner sees a zero key
            // and cannot tell "never had one" from "not fetched yet" (Fetch.cs's Files route), so the answer that read
            // the whole payload says so here and the commit seals the group (FLAC plan §5.2, finding 27's rule).
            if (row.OriginalAudio == UInt128.Zero) row.Known |= (uint)TrackFields.Files;

            var id = row.Id;
            if (!s.Tracks.Settle()) { artists.Discard(); return; }
            artists.End(in id);
        }

        /// <summary>`metadata.Album` (kind 9) → the album's Identity, Release and Publishing groups, its artists, and
        /// its DISC rows folded into one ordered <see cref="Relation.AlbumTracks"/> run carrying
        /// <see cref="AlbumTrackEdge"/> (disc, number) — the whole tracklist in one commit (plan §4.6).
        ///
        /// <para>The tracks inside a disc are staged THIN: title, credit line, duration, explicit. That is exactly
        /// ch 04 §7's row group, so an album page paints its whole table off this one answer and only the cold lanes
        /// (plays, tempo, tags) ride a later bundle.</para></summary>
        public static void AlbumV4(ReadOnlySpan<byte> proto, Staging s)
        {
            var r = new ProtoReader(proto);
            ref var row = ref s.Albums.RowFor(default, Authority.Full,
                (uint)(AlbumFields.Identity | AlbumFields.Release | AlbumFields.Publishing));

            var artists = s.Run(Relation.AlbumArtists);
            var tracks = s.Run(Relation.AlbumTracks);
            bool copyright = false, courtesy = false;

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: row.Id = EntityId.ForGid(EntityKind.Album, r.Bytes()); break;
                    case 2: row.Title = s.AddText(r.Bytes()); break;
                    case 3:                                            // artist
                        {
                            var artist = ThinArtist(r.Message(), s, out _);
                            if (!artist.IsEmpty) artists.Add(in artist);
                            break;
                        }
                    case 4: row.Kind = KindOf(r.Int32()); break;
                    case 5: row.Label = s.AddText(r.Bytes()); break;
                    case 6: Date(r.Message(), zigzag: true, out row.Year, out row.ReleaseAt, out row.DatePrecision); break;
                    case 11:                                           // disc
                        {
                            var d = r.Message();
                            byte disc = 1;
                            while (d.Next())
                            {
                                if (d.Field == 1) disc = (byte)d.ZigZag();
                                else if (d.Field == 3) { if (DiscTrack(d.Message(), s, disc, ref tracks)) row.TrackCount++; }
                                else d.Skip();
                            }
                            if (disc > row.DiscCount) row.DiscCount = disc;
                            break;
                        }
                    case 13:                                           // copyright: type 0 = P (courtesy), 1 = C
                        {
                            var c = r.Message();
                            int type = 0;
                            ReadOnlySpan<byte> text = default;
                            while (c.Next())
                            {
                                if (c.Field == 1) type = c.Int32();
                                else if (c.Field == 2) text = c.Bytes();
                                else c.Skip();
                            }
                            if (text.IsEmpty) break;
                            if (type == 1 && !copyright) { row.Copyright = s.AddText(text); copyright = true; }
                            else if (type == 0 && !courtesy) { row.Courtesy = s.AddText(text); courtesy = true; }
                            break;
                        }
                    case 17: row.Image = Cover(s, r.Message()); break;
                    default: r.Skip(); break;
                }
            }

            var id = row.Id;
            if (!s.Albums.Settle()) { tracks.Discard(); artists.Discard(); return; }
            artists.End(in id);
            // The disc rows ARE the whole tracklist — Complete, not a page. An album answer that named no disc is an
            // album whose tracklist nobody asked for, and the relation must stay Unknown rather than claim to be
            // empty: "empty" and "not asked" render differently on every detail surface (ch 03 §7). `End` on a run
            // with no children records nothing, which is exactly that.
            tracks.End(in id, EdgeState.Complete, tracks.Count);

            static byte KindOf(int wire) => wire switch
            {
                2 => (byte)AlbumKind.Single,
                3 => (byte)AlbumKind.Compilation,
                4 => (byte)AlbumKind.EP,
                _ => (byte)AlbumKind.Album,
            };
        }

        /// <summary>One track inside an album's disc: a thin row plus the ordered edge that places it.</summary>
        static bool DiscTrack(ProtoReader track, Staging s, byte disc, ref EdgeRun tracks)
        {
            ref var edge = ref tracks.Add();
            ref var row = ref s.Tracks.RowFor(default, Authority.Thin, (uint)TrackFields.Identity);
            ushort number = 0;
            s.ClearCredit();

            while (track.Next())
            {
                switch (track.Field)
                {
                    case 1: row.Id = EntityId.ForGid(EntityKind.Track, track.Bytes()); break;
                    case 2: row.Title = s.AddText(track.Bytes()); break;
                    case 4: s.AppendCredit(track.Message().Bytes(2)); break;      // artist.name
                    case 5: number = (ushort)track.ZigZag(); break;
                    case 7: row.DurationMs = (int)track.ZigZag(); break;
                    case 9: if (track.Bool()) row.Flags |= (uint)TrackFlags.Explicit; break;
                    default: track.Skip(); break;
                }
            }

            row.ArtistLine = s.TakeCredit();
            var id = row.Id;
            if (!s.Tracks.Settle()) { tracks.DropLast(); return false; }
            edge.Target = id;
            edge.B0 = disc;
            edge.U0 = number;
            return true;
        }

        /// <summary>`metadata.Artist` (kind 8) → the artist's Identity, the popular-tracks run and the discography
        /// runs. The overview's rich half — header image, bio, monthly listeners, the artist pick — is the
        /// PATHFINDER's (`artistOverview`, Spotify.Decode.Pathfinder.cs) and this answer never claims it: a kind-8 row
        /// that set <c>ArtistFields.Overview</c> would let a thin answer stop the page asking for the real one.</summary>
        public static void ArtistV4(ReadOnlySpan<byte> proto, Staging s)
        {
            var r = new ProtoReader(proto);
            ref var row = ref s.Artists.RowFor(default, Authority.Full, (uint)ArtistFields.Identity);

            var popular = s.Run(Relation.ArtistPopular);
            var albums = s.Run(Relation.ArtistAlbums);
            var singles = s.Run(Relation.ArtistSingles);
            var related = s.Run(Relation.ArtistRelated);

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: row.Id = EntityId.ForGid(EntityKind.Artist, r.Bytes()); break;
                    case 2: row.Name = s.AddText(r.Bytes()); break;
                    case 4:                                            // top_track { country, track[] }
                        {
                            var top = r.Message();
                            while (top.Next())
                            {
                                if (top.Field != 2) { top.Skip(); continue; }
                                var id = EntityId.ForGid(EntityKind.Track, top.Message().Bytes(1));
                                if (!id.IsEmpty) popular.Add(id);
                            }
                            break;
                        }
                    // AlbumGroup { album[] } — the wire nests a group per release so a deluxe edition and its base
                    // album travel together; the facet page reads them flat and re-groups from the album rows.
                    case 5: Group(r.Message(), ref albums); break;
                    case 6: Group(r.Message(), ref singles); break;
                    case 15:                                           // related
                        {
                            var id = EntityId.ForGid(EntityKind.Artist, r.Message().Bytes(1));
                            if (!id.IsEmpty) related.Add(id);
                            break;
                        }
                    case 17: row.Image = Cover(s, r.Message()); break;
                    default: r.Skip(); break;
                }
            }

            var artist = row.Id;
            if (!s.Artists.Settle())
            {
                related.Discard();
                singles.Discard();
                albums.Discard();
                popular.Discard();
                return;
            }
            popular.End(in artist, EdgeState.Complete, popular.Count);
            albums.End(in artist, EdgeState.Complete, albums.Count);
            singles.End(in artist, EdgeState.Complete, singles.Count);
            related.End(in artist, EdgeState.Complete, related.Count);

            static void Group(ProtoReader group, ref EdgeRun run)
            {
                while (group.Next())
                {
                    if (group.Field != 1) { group.Skip(); continue; }
                    var id = EntityId.ForGid(EntityKind.Album, group.Message().Bytes(1));
                    if (!id.IsEmpty) run.Add(id);
                }
            }
        }

        /// <summary>`metadata.Show` (kind 11) → the show's Identity and About (ch 09 §7: publisher and description
        /// are the two lines the show header states beside the cover).</summary>
        public static void ShowV4(ReadOnlySpan<byte> proto, Staging s)
        {
            var r = new ProtoReader(proto);
            ref var row = ref s.Shows.RowFor(default, Authority.Full, (uint)(ShowFields.Identity | ShowFields.About));
            var episodes = s.Run(Relation.ShowEpisodes);

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: row.Id = EntityId.ForGid(EntityKind.Show, r.Bytes()); break;
                    case 2: row.Title = s.AddText(r.Bytes()); break;
                    case 64: row.Description = s.AddText(r.Bytes()); break;
                    case 66: row.Publisher = s.AddText(r.Bytes()); break;
                    case 69: row.Image = Cover(s, r.Message()); break;
                    case 70:                                           // episode
                        {
                            var id = EntityId.ForGid(EntityKind.Episode, r.Message().Bytes(1));
                            if (!id.IsEmpty) episodes.Add(id);
                            break;
                        }
                    default: r.Skip(); break;
                }
            }

            var show = row.Id;
            if (!s.Shows.Settle()) { episodes.Discard(); return; }
            // A show's episode list is PAGED on every transport that serves it, so it lands as a page at offset 0 and
            // stays Partial until its own length reaches the server's total (Edges.cs, D7) — never Complete off one
            // answer, which is what would stop the page asking for the rest (ch 09 §7).
            if (episodes.Count > 0) episodes.Page(in show, offset: 0, total: 0);
        }

        /// <summary>`metadata.Episode` (kind 12) → Identity and About. The show it belongs to is staged thin, the same
        /// way a track's album is, so an episode row can name its show before the show is fetched.</summary>
        public static void EpisodeV4(ReadOnlySpan<byte> proto, Staging s)
        {
            var r = new ProtoReader(proto);
            ref var row = ref s.Episodes.RowFor(default, Authority.Full,
                (uint)(EpisodeFields.Identity | EpisodeFields.About));

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: row.Id = EntityId.ForGid(EntityKind.Episode, r.Bytes()); break;
                    case 2: row.Title = s.AddText(r.Bytes()); break;
                    case 7: row.DurationMs = (int)r.ZigZag(); break;
                    case 64: row.Description = s.AddText(r.Bytes()); break;
                    case 66: Date(r.Message(), zigzag: true, out _, out row.PublishedAt, out _); break;
                    case 68: row.Image = Cover(s, r.Message()); break;
                    case 71: row.ShowUri = ThinShow(r.Message(), s); break;
                    default: r.Skip(); break;
                }
            }
            s.Episodes.Settle();
        }

        /// <summary>`ListMetadataV2` (kind 205) → a playlist's Identity from the metadata service rather than from a
        /// revision: name, description, cover, format. The identity is the ENVELOPE's, because this payload names no
        /// entity of its own.</summary>
        public static void ListMetadataV2(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var id = Identity(s, entityUri);
            if (id.IsEmpty) return;
            var r = new ProtoReader(proto);
            ref var row = ref s.Playlists.RowFor(id, Authority.Full, (uint)PlaylistFields.Identity);

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 3: row.Title = s.AddText(r.Bytes()); break;
                    case 4: row.Description = s.AddText(r.Bytes()); break;
                    case 21:                                           // Images { variant[] { format, url } }
                        {
                            var images = r.Message();
                            while (images.Next())
                            {
                                if (images.Field != 1) { images.Skip(); continue; }
                                var url = images.Message().Bytes(2);
                                if (!url.IsEmpty && row.Image.IsEmpty) row.Image = s.AddText(url);
                            }
                            break;
                        }
                    case 23:
                        row.Format = FormatOf(r.Bytes());
                        row.Known |= (uint)PlaylistFields.Format;
                        break;
                    default: r.Skip(); break;
                }
            }
        }

        /// <summary>The wire's `format_string` → <see cref="PlaylistFormat"/>. An unknown token is <c>Other</c>, never
        /// <c>None</c>: "the server named a format we have no skin for" and "the server named none" are different
        /// answers and the header renders them differently (ch 06 §7).</summary>
        internal static byte FormatOf(ReadOnlySpan<byte> token)
        {
            if (token.IsEmpty) return (byte)PlaylistFormat.None;
            if (StartsWith(token, "daylist")) return (byte)PlaylistFormat.Daylist;
            if (StartsWith(token, "dailymix") || StartsWith(token, "daily-mix")) return (byte)PlaylistFormat.DailyMix;
            if (StartsWith(token, "discoverweekly") || StartsWith(token, "discover-weekly")) return (byte)PlaylistFormat.DiscoverWeekly;
            if (StartsWith(token, "releaseradar") || StartsWith(token, "release-radar")) return (byte)PlaylistFormat.ReleaseRadar;
            if (StartsWith(token, "topicmix") || StartsWith(token, "topic-mix")) return (byte)PlaylistFormat.TopicMix;
            if (StartsWith(token, "inspiredby") || StartsWith(token, "inspired-by")) return (byte)PlaylistFormat.InspiredByMix;
            if (StartsWith(token, "editorial")) return (byte)PlaylistFormat.Editorial;
            if (StartsWith(token, "chart")) return (byte)PlaylistFormat.Chart;
            if (StartsWith(token, "radio")) return (byte)PlaylistFormat.Radio;
            return (byte)PlaylistFormat.Other;
        }

        // ── 5. the extended-metadata envelope ────────────────────────────────────────────────────────────────────────

        /// <summary>THE metadata entry point: one <c>BatchedExtensionResponse</c> — the answer to a 300-uri POST —
        /// fanned out to every trait decoder. Ported from `ExtendedMetadataSource`'s projection loop, minus the store
        /// round-trip each of its six projectors did to decide whether to write: authority decides that now (D16).
        ///
        /// <para>Shape: <c>BatchedExtensionResponse{ extended_metadata=2: EntityExtensionDataArray{ extension_kind=2,
        /// extension_data=3: EntityExtensionData{ header=1{ status_code=1 }, entity_uri=2,
        /// extension_data=3: google.protobuf.Any{ value=2 } } } }</c>. The envelope's entity uri is the authority for
        /// WHICH row a trait answers for — the V4 payloads carry their own gid and do not need it, the traits do.</para>
        ///
        /// <para><paramref name="asked"/> is the batch this response answers, and it is needed by exactly one kind:
        /// <see cref="Ext.AudioFiles"/>'s entity uri is a DERIVED <c>spotify:audio:</c> id that belongs to no table, so
        /// the only thing that knows which track it stood for is the request that sent it (<c>FetchBatch.IdFor</c>, FLAC
        /// plan §5.2). Null — a single-entity call, a fixture, a store replay — simply skips that kind.</para></summary>
        public static void ExtendedMetadata(ReadOnlySpan<byte> response, Staging s, FetchBatch? asked = null)
        {
            var r = new ProtoReader(response);
            while (r.Next())
            {
                if (r.Field != 2) { r.Skip(); continue; }
                var array = r.Message();
                int kind = 0;
                while (array.Next())
                {
                    switch (array.Field)
                    {
                        case 2: kind = array.Int32(); break;
                        case 3:
                            {
                                var entity = array.Message();
                                ReadOnlySpan<byte> uri = default, payload = default;
                                // A header with no status_code is a success, not a zero (`Varint`'s fallback).
                                int status = 200;
                                bool answered = false;
                                while (entity.Next())
                                {
                                    if (entity.Field == 1) status = (int)entity.Message().Varint(1, 200);
                                    else if (entity.Field == 2) uri = entity.Bytes();
                                    // google.protobuf.Any { type_url = 1, value = 2 }
                                    else if (entity.Field == 3) { payload = entity.Message().Bytes(2); answered = true; }
                                    else entity.Skip();
                                }
                                // An empty body IS an answer for some kinds (a descriptor message with no descriptors
                                // serializes to zero bytes — `DescriptorProjector`'s finding 27), so the gate is the
                                // status and the PRESENCE of the extension_data field, never the payload's length.
                                if (answered && status is >= 200 and < 300) Extension((Ext)kind, uri, payload, s, asked);
                                break;
                            }
                        default: array.Skip(); break;
                    }
                }
            }
        }

        /// <summary>One extension payload for one entity uri — the dispatch every trait projector of 0.2.9 collapses
        /// into (`TraitProjectors.All`). A kind we do not read is not an error.</summary>
        /// <param name="asked">The batch this payload answers, for the one kind whose entity uri is not a row — see
        /// <see cref="ExtendedMetadata"/>.</param>
        public static void Extension(Ext kind, ReadOnlySpan<byte> entityUri, ReadOnlySpan<byte> payload, Staging s,
                                     FetchBatch? asked = null)
        {
            switch (kind)
            {
                case Ext.AudioFiles:
                    // The one trait whose `entity_uri` is NOT an identity: `spotify:audio:<uuid>` is a derived entity
                    // no table holds, and the uuid is not the track's gid, so no parse can recover the row. The request
                    // is the only witness — and a blind envelope (no batch) has nothing it could legally write, which
                    // is why the fold below takes the track explicitly (FLAC plan §5.2).
                    if (asked is not null) AudioFiles(payload, new StagedId(asked.IdFor(entityUri)), s);
                    break;
                case Ext.TrackV4: TrackV4(payload, s); break;
                case Ext.AlbumV4: AlbumV4(payload, s); break;
                case Ext.ArtistV4: ArtistV4(payload, s); break;
                case Ext.ShowV4: ShowV4(payload, s); break;
                case Ext.EpisodeV4: EpisodeV4(payload, s); break;
                case Ext.ListMetadataV2: ListMetadataV2(payload, entityUri, s); break;
                case Ext.AudioAttributes: AudioAttributes(payload, entityUri, s); break;
                case Ext.TrackDescriptor: Descriptors(payload, entityUri, s); break;
                case Ext.PlayCount: PlayCount(payload, entityUri, s); break;
                case Ext.Publishing: Publishing(payload, entityUri, s); break;
                case Ext.VideoAssociations: VideoAssociations(payload, entityUri, s); break;
                case Ext.VisualIdentity: VisualIdentity(payload, s); break;
                case Ext.PreRelease: PreRelease(payload, entityUri, s); break;
                // G-044: the five kinds the Api asked for and this dispatch used to drop (Spotify.Decode.Traits.cs).
                case Ext.UserProfile: UserProfile(payload, entityUri, s); break;
                case Ext.Credits: Credits(payload, entityUri, s); break;
                case Ext.RecommendedPlaylists: RecommendedPlaylists(payload, entityUri, s); break;
                case Ext.AudioAssociations: AudioAssociations(payload, entityUri, s); break;
                case Ext.Waveforms: Waveform(payload, entityUri, s); break;
                default: break;
            }
        }

        // ── 6. the trait projectors (ported from Backend/Hydration/Projectors/*) ─────────────────────────────────────

        /// <summary>Kind 222 → <see cref="TrackFields.Audio"/>: tempo, the musical key, the camelot wheel position and
        /// its swatch colour (ch 01 §7's "BPM · key · swatch" cell, which renders EMPTY rather than dashed until the
        /// group lands).
        ///
        /// <para><b>The two rings are encoded, not stored as text.</b> `TrackTable.Key` and `.Camelot` are one byte
        /// each and ch 04 DATA GAPS asks owner M for exact `KeyLabel(byte)` / `CamelotLabel(byte)` tables against
        /// closed rings. The encodings are <see cref="KeyCode"/> and <see cref="CamelotCode"/> below, and they are
        /// ONE-BASED so the column's zero default is the honest "unknown" — a 0-based pitch class would make every
        /// track with a tempo and no key read as "C". `DecodeTests` pins both, which is what owner M's label tables
        /// have to agree with.</para></summary>
        public static void AudioAttributes(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var id = Identity(s, entityUri);
            if (id.IsEmpty) return;
            var r = new ProtoReader(proto);
            double tempo = 0;
            byte key = 0, camelot = 0;
            uint colour = 0;

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: tempo = r.Double(); break;
                    case 2:
                        {
                            var k = r.Message();
                            while (k.Next())
                            {
                                if (k.Field == 1) key = KeyCode(k.Bytes());
                                else if (k.Field == 3)
                                {
                                    k.Message().Fields(1, 2, out var wheel, out var hex);
                                    camelot = CamelotCode(wheel);
                                    colour = Argb(hex);
                                }
                                else k.Skip();
                            }
                            break;
                        }
                    default: r.Skip(); break;
                }
            }

            if (tempo <= 0 && key == 0) return;                    // answered, but with nothing usable in it
            ref var row = ref s.Tracks.RowFor(id, Authority.Full, (uint)TrackFields.Audio);
            row.Tempo = tempo is > 0 and < 6553 ? (ushort)(tempo * 10 + 0.5) : (ushort)0;
            row.Key = key;
            row.Camelot = camelot;
            row.CamelotColor = colour;
        }

        // ── kind 5: the format ladder (FLAC plan §5.1, §5.2) ─────────────────────────────────────────────────────────

        /// <summary>A Spotify file id is 20 bytes. A rung the wire named without one cannot be opened and is therefore
        /// not a rung the account was offered.</summary>
        public const int FileIdBytes = 20;

        /// <summary><c>metadata.AudioFile.Format.FLAC_FLAC</c> — 16-bit lossless (metadata.proto:317).</summary>
        public const byte FlacFormat = 16;
        /// <summary><c>metadata.AudioFile.Format.FLAC_FLAC_24BIT</c> (metadata.proto:321).</summary>
        public const byte Flac24Format = 22;
        /// <summary>A format number the protocol does not have (its enum tops out at 22). Recorded rather than dropped,
        /// because a rung this build cannot name must still RENDER — hiding it would silently shrink the ladder (plan
        /// §5.3, 0.2.9's rule) — and recorded as 255 rather than by a truncating cast, which would spell 300 as 44 and
        /// 256 as 0, i.e. as Ogg Vorbis 96.</summary>
        public const byte UnknownFormat = byte.MaxValue;

        /// <summary>Kind 5 (<c>AUDIO_FILES</c>) → <see cref="TrackFields.Files"/>: the track's format ladder as a
        /// <see cref="Relation.TrackFormats"/> run carrying <see cref="FormatEdge"/>, plus the two lossless bits on the
        /// row (<see cref="TrackFlags.Lossless"/> / <see cref="TrackFlags.Lossless24"/>).
        ///
        /// <para><b>The envelope's entity uri is the AUDIO uri, not the track's</b>, so the caller passes the track's
        /// <see cref="StagedId"/> — the one it asked for. That is not a convenience: <c>spotify:audio:&lt;uuid&gt;</c>
        /// is a derived entity with no row and no table, and routing it through <c>Identity</c> would allocate a junk
        /// TRACK row keyed by an audio uri (FLAC plan §5.2, <see cref="Extension"/>).</para>
        ///
        /// <para><b>An empty list is a real answer.</b> An account without lossless in this market gets the same
        /// Ogg/AAC ladder <c>TRACK_V4</c> already gave, or nothing at all — and both mean "no FLAC for you, do not ask
        /// again": the run lands empty and the group is marked known (finding 27's rule, as for kind 6). Wire ORDER is
        /// kept; the drawer sorts its own copy by bitrate (plan §5.3).</para>
        ///
        /// <para>The normalization params (fields 2 and 3) are deliberately not staged: they are the OPENER's — a gain
        /// applied to one stream at one moment (<c>Spotify.Audio.NormalizationGain</c>) — and no surface paints
        /// them.</para></summary>
        public static void AudioFiles(ReadOnlySpan<byte> proto, in StagedId track, Staging s)
        {
            if (track.IsEmpty) return;
            ref var row = ref s.Tracks.RowFor(in track, Authority.Full, (uint)TrackFields.Files);
            var ladder = s.Run(Relation.TrackFormats);
            var r = new ProtoReader(proto);

            while (r.Next())
            {
                if (r.Field != 1) { r.Skip(); continue; }              // AudioFilesExtensionResponse.files
                var ext = r.Message();                                 // ExtendedAudioFile
                byte format = 0;
                ushort kbps = 0;
                bool identified = false;
                while (ext.Next())
                {
                    if (ext.Field == 1)                                // metadata.AudioFile { file_id = 1, format = 2 }
                    {
                        var file = ext.Message();
                        while (file.Next())
                        {
                            if (file.Field == 1) identified = file.Bytes().Length == FileIdBytes;
                            else if (file.Field == 2) { ulong v = file.Varint(); format = v > byte.MaxValue ? UnknownFormat : (byte)v; }
                            else file.Skip();
                        }
                    }
                    else if (ext.Field == 4)                           // average_bitrate, bits per second
                    {
                        // 0 stays 0: "the wire did not say" is not "0 kbps", and the drawer sorts it last (§5.3).
                        long bps = ext.Int32();
                        long k = bps <= 0 ? 0 : bps / 1000;
                        kbps = (ushort)(k > ushort.MaxValue ? ushort.MaxValue : k);
                    }
                    else ext.Skip();
                }
                if (!identified) continue;

                ref var rung = ref ladder.Add();
                rung.B0 = format;                                      // FormatEdge.FormatId  (Edges.Staging.cs's union)
                rung.U0 = kbps;                                        // FormatEdge.Kbps
                if (format == FlacFormat) row.Flags |= (uint)TrackFlags.Lossless;
                else if (format == Flac24Format) row.Flags |= (uint)TrackFlags.Lossless24;
            }

            ladder.EndEvenIfEmpty(in track);
        }

        /// <summary>The 12-slot pitch-class ring, ONE-BASED (1 = C … 12 = B; 0 = unknown). Flats fold onto their
        /// sharps because the ring is the same ring — the wire spells both.</summary>
        public static byte KeyCode(ReadOnlySpan<byte> name)
        {
            if (name.IsEmpty) return 0;
            int letter = name[0] switch
            {
                (byte)'C' or (byte)'c' => 0,
                (byte)'D' or (byte)'d' => 2,
                (byte)'E' or (byte)'e' => 4,
                (byte)'F' or (byte)'f' => 5,
                (byte)'G' or (byte)'g' => 7,
                (byte)'A' or (byte)'a' => 9,
                (byte)'B' or (byte)'b' => 11,
                _ => -1,
            };
            if (letter < 0) return 0;
            if (name.Length > 1)
            {
                if (name[1] == '#') letter++;
                else if (name[1] == 'b' || name[1] == 'B') letter--;
            }
            return (byte)(((letter + 12) % 12) + 1);
        }

        /// <summary>The 24-slot camelot wheel, ONE-BASED: "1A" → 1, "1B" → 2, … "12B" → 24; 0 = unknown.</summary>
        public static byte CamelotCode(ReadOnlySpan<byte> code)
        {
            if (code.Length is < 2 or > 3) return 0;
            byte last = code[^1];
            int minor = last is (byte)'A' or (byte)'a' ? 0 : last is (byte)'B' or (byte)'b' ? 1 : -1;
            if (minor < 0) return 0;
            long n = Number(code[..^1]);
            return n is < 1 or > 12 ? (byte)0 : (byte)((n - 1) * 2 + minor + 1);
        }

        /// <summary>A CSS hex colour ("#56d9f8") → opaque ARGB. 0 when the span is not one — and 0 is what the
        /// swatch reads as "no colour", so a malformed value never paints black.</summary>
        public static uint Argb(ReadOnlySpan<byte> hex)
        {
            if (hex.Length > 0 && hex[0] == '#') hex = hex[1..];
            if (hex.Length != 6) return 0;
            uint value = 0;
            for (int i = 0; i < 6; i++)
            {
                int c = hex[i];
                int d = c is >= '0' and <= '9' ? c - '0'
                      : c is >= 'a' and <= 'f' ? c - 'a' + 10
                      : c is >= 'A' and <= 'F' ? c - 'A' + 10
                      : -1;
                if (d < 0) return 0;
                value = (value << 4) | (uint)d;
            }
            return 0xFF000000u | value;
        }

        /// <summary>Kind 6 → the descriptor chips, as a <see cref="Relation.TrackTags"/> run whose PAYLOAD is the
        /// chip text (`Edges.TrackTags`: "the payload IS the tag id; Targets unused"), plus the row that records the
        /// GROUP as known.
        ///
        /// <para>Ported verbatim from `DescriptorProjector`: <see cref="MaxTagsPerTrack"/> kept, wire order kept (it
        /// is descending weight, so the first few ARE the track's identity), and <c>display_name</c> preferred over
        /// the lowercase match token. An answer with no descriptors is a REAL answer — "this track has none" — and
        /// still writes the empty run AND the <see cref="TrackFields.Tags"/> bit, so the planner stops asking.</para></summary>
        public const int MaxTagsPerTrack = 6;

        /// <inheritdoc cref="MaxTagsPerTrack"/>
        public static void Descriptors(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var id = Identity(s, entityUri);
            if (id.IsEmpty) return;
            var r = new ProtoReader(proto);
            var tags = s.Run(Relation.TrackTags);

            while (r.Next() && tags.Count < MaxTagsPerTrack)
            {
                if (r.Field != 1) { r.Skip(); continue; }           // ExtensionDescriptorData.descriptors
                r.Message().Fields(1, 5, out var text, out var display);
                var label = display.IsEmpty ? text : display;
                if (label.IsEmpty) continue;
                tags.Add().Text = s.AddText(label);
            }

            tags.EndEvenIfEmpty(in id, tags.Count);
            s.Tracks.RowFor(id, Authority.Full, (uint)TrackFields.Tags);
        }

        /// <summary>Kind 185 (`OnPlatformReputationTrait`) → <see cref="TrackFields.PlayCount"/>. Ported from
        /// `PlayCountProjector.TryReadPlayCount`: field 3, varint, and a zero is not a play count — the surface shows
        /// a DASH for a track nobody has ruled on and a number for one somebody has (ch 04 §7).</summary>
        public static void PlayCount(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var id = Identity(s, entityUri);
            if (id.IsEmpty) return;
            long plays = new ProtoReader(proto).Varint(3);
            if (plays <= 0) return;
            s.Tracks.RowFor(id, Authority.Full, (uint)TrackFields.PlayCount).PlayCount =
                plays > uint.MaxValue ? uint.MaxValue : (uint)plays;
        }

        /// <summary>Kind 183 → an album's ©/℗ block and its calendar release date (ch 05 §7's "About this release").
        ///
        /// <para><b>Staged THIN, deliberately.</b> 0.2.9's `PublishingProjector` was "additive only": it filled
        /// Copyright / ReleaseDate when the album had none and never touched Label, because 183 carries no label.
        /// D16 says that in one word — a Thin write fills a group nobody has filled and never overwrites a Full one —
        /// so a getAlbum answer always wins whichever order the two land in, and 183 can never blank the label.</para>
        /// <para>Its `Date` is proto3 and spells its numbers as plain <c>int32</c>, which is the whole reason
        /// <see cref="Date"/> takes a <c>zigzag</c> flag.</para></summary>
        public static void Publishing(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var id = Identity(s, entityUri);
            if (id.IsEmpty) return;
            var r = new ProtoReader(proto);
            ushort year = 0;
            int at = 0;
            byte precision = 0;
            TextRef copyright = default, courtesy = default;

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: Date(r.Message(), zigzag: false, out year, out at, out precision); break;
                    case 4:                                        // copyright[] — the lines already carry © / ℗
                        {
                            var line = r.Bytes();
                            if (line.IsEmpty) break;
                            bool phono = line[0] == 0xE2 && line.Length > 2 && line[1] == 0x84 && line[2] == 0x97;
                            if (phono) { if (courtesy.IsEmpty) courtesy = s.AddText(line); }
                            else if (copyright.IsEmpty) copyright = s.AddText(line);
                            break;
                        }
                    default: r.Skip(); break;
                }
            }

            if (year == 0 && copyright.IsEmpty && courtesy.IsEmpty) return;
            uint known = 0;
            if (year != 0) known |= (uint)AlbumFields.Release;
            if (!copyright.IsEmpty || !courtesy.IsEmpty) known |= (uint)AlbumFields.Publishing;
            ref var row = ref s.Albums.RowFor(id, Authority.Thin, known);
            row.Year = year;
            row.ReleaseAt = at;
            row.DatePrecision = precision;
            row.Copyright = copyright;
            row.Courtesy = courtesy;
        }

        /// <summary>Kind 99 → <see cref="TrackFields.Video"/>: the music-video counterpart's uri and its 16:9 still
        /// (ch 02 GAP-6's second image per track). Ported from `VideoProjector.ParseAssoc`.
        ///
        /// <para>A payload with no association is a real NEGATIVE and still writes the group: the row then knows
        /// there is no video and the film lane stops asking. The `VideoOverride` bit is untouched here — the commit
        /// keeps a user-attached mp4 alive across a catalogue answer that carries none (ch 01 GAP 7).</para></summary>
        public static void VideoAssociations(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var id = Identity(s, entityUri);
            if (id.IsEmpty) return;
            var r = new ProtoReader(proto);
            StagedId video = default;
            TextRef still = default;
            int files = 0;
            long bestArea = 0;
            ushort width = 0, height = 0;

            while (r.Next())
            {
                if (r.Field != 1) { r.Skip(); continue; }          // VideoAssociations.association
                var a = r.Message();
                while (a.Next())
                {
                    if (a.Field == 1) video = Identity(s, a.Bytes());         // associated_uri
                    else if (a.Field == 2)                                    // files { file[] { file_id, variant, w, h } }
                    {
                        var group = a.Message();
                        while (group.Next())
                        {
                            if (group.Field != 1) { group.Skip(); continue; }
                            var file = group.Message();
                            ReadOnlySpan<byte> fileId = default;
                            int w = 0, h = 0;
                            while (file.Next())
                            {
                                if (file.Field == 1) fileId = file.Bytes();
                                else if (file.Field == 3) w = file.Int32();
                                else if (file.Field == 4) h = file.Int32();
                                else file.Skip();
                            }
                            if (fileId.IsEmpty) continue;
                            files++;
                            if (still.IsEmpty) still = Image(s, fileId);
                            // The natural size (G-058) is the LARGEST rendition's: the cap and the PiP fit want the
                            // shape, and every variant of one video shares it — the largest is simply the best-stated.
                            if (w > 0 && h > 0 && w <= ushort.MaxValue && h <= ushort.MaxValue && (long)w * h > bestArea)
                            {
                                bestArea = (long)w * h;
                                width = (ushort)w;
                                height = (ushort)h;
                            }
                        }
                    }
                    else a.Skip();
                }
            }

            ref var row = ref s.Tracks.RowFor(id, Authority.Full, (uint)TrackFields.Video);
            row.VideoUri = video;
            row.VideoImage = still;
            row.VideoW = width;
            row.VideoH = height;
            // THE VERDICT IS 0.2.9'S (G-057): a video exists when the association names a counterpart OR carries renditions
            // of its own. A self-contained music-video track answers with files and no `associated_uri`, and reading only
            // the counterpart hid the film lane for exactly the tracks that ARE videos.
            if (!video.IsEmpty || files > 0) row.Flags |= (uint)TrackFlags.HasVideo;
        }

        /// <summary>Kind 179 → the palette, keyed by the IMAGE the payload names and never by the entity uri. That
        /// pairing is the whole point of the trait (`VisualIdentityProjector`): one track's answer also tints its
        /// album's grid card and every other slot showing the same cover. DARK-only, so it lands as
        /// <c>Palette.SetDark</c> (Entities/Palette.cs) and the light half waits for the filler.</summary>
        public static void VisualIdentity(ReadOnlySpan<byte> proto, Staging s)
        {
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                if (r.Field != 1) { r.Skip(); continue; }          // VisualIdentityTrait.visual_identity
                var identity = r.Message();
                int urlStart = s.Gradings.Count;
                var scheme = default(Scheme);
                while (identity.Next())
                {
                    if (identity.Field == 1)                       // images[] { image { url }, size }
                    {
                        var url = identity.Message().Sub(1).Bytes(1);
                        if (!url.IsEmpty) s.Gradings.Add().Url = s.AddText(url);
                    }
                    // colors { base, darker, darkest, flat } — `base.background_base`, NOT `flat`, which is a light
                    // desaturated accent where background_base is the dominant tone a placeholder wants.
                    else if (identity.Field == 2) scheme = ColorScheme(identity.Message().Sub(1));
                    else identity.Skip();
                }

                if (scheme.IsEmpty) { s.Gradings.Rewind(urlStart); continue; }
                for (int i = urlStart; i < s.Gradings.Count; i++) s.Gradings[i].Dark = scheme;
            }
        }

        /// <summary>A `ColorScheme` → the palette's five roles. <c>Rgba{ r=1, g=2, b=3, a=4 }</c> packed to ARGB.</summary>
        static Scheme ColorScheme(ProtoReader scheme)
        {
            Span<uint> roles = stackalloc uint[5];
            while (scheme.Next())
            {
                if (scheme.Field is >= 1 and <= 5) roles[scheme.Field - 1] = Rgba(scheme.Message());
                else scheme.Skip();
            }
            return new Scheme(roles[0], roles[1], roles[2], roles[3], roles[4]);

            static uint Rgba(ProtoReader rgba)
            {
                Span<uint> c = stackalloc uint[4];
                c[3] = 255;                                        // the wire omits a fully opaque alpha
                while (rgba.Next())
                {
                    if (rgba.Field is >= 1 and <= 4) c[rgba.Field - 1] = (uint)rgba.Int32();
                    else rgba.Skip();
                }
                if (c[3] == 0) c[3] = 255;
                return ((c[3] & 0xFF) << 24) | ((c[0] & 0xFF) << 16) | ((c[1] & 0xFF) << 8) | (c[2] & 0xFF);
            }
        }

        /// <summary>Kind 138 → the prerelease pairing (ch 05 D3): the <c>spotify:prerelease:</c> uri the pre-save
        /// heart addresses, and when the countdown ends. Both halves are staged on the ALBUM the payload names, and
        /// the verdict bit is set from the uri at commit as well (`CommitAlbums`), so a fixture whose uri says
        /// prerelease is a prerelease even when the flag is absent.</summary>
        public static void PreRelease(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            if (entityUri.IsEmpty) return;
            var r = new ProtoReader(proto);
            TextRef prereleaseUri = default, title = default;
            StagedId album = default;
            int releaseAt = 0;

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: prereleaseUri = s.AddText(r.Bytes()); break;
                    case 2: releaseAt = AtMost(r.Message().Varint(1)); break;
                    case 3:                                        // release { album_uri, type, name, artist, images }
                        {
                            r.Message().Fields(1, 3, out var albumUri, out var name);
                            album = Identity(s, albumUri);
                            title = s.AddText(name);
                            break;
                        }
                    default: r.Skip(); break;
                }
            }

            uint known = (uint)AlbumFields.Availability;
            if (!prereleaseUri.IsEmpty) known |= (uint)AlbumFields.PreReleaseLink;
            if (!title.IsEmpty) known |= (uint)AlbumFields.Title;
            ref var row = ref s.Albums.RowFor(album.IsEmpty ? Identity(s, entityUri) : album, Authority.Thin, known);
            row.Title = title;
            row.PreReleaseUri = prereleaseUri;
            row.PreReleaseEnd = releaseAt;
            row.Flags |= (uint)AlbumFlags.PreRelease;
        }

        // ── 7. playlist4 and the collection ──────────────────────────────────────────────────────────────────────────

        /// <summary>A `SelectedListContent` → the playlist's header AND one page of its membership (ported from
        /// `PlaylistWireMapper.ParseContents` + `ToMember`). The revision carries no uri of its own, so the caller —
        /// which made the request — supplies it.
        ///
        /// <para>Every membership fact 0.2.9 kept on its `Track` record lands on the EDGE (D10): the hex
        /// <c>item_id</c> that is the reconciler's key and survives a reorder, the added-at instant, the adding user,
        /// and the chart triple the Top-50 lists paint. One track in two playlists has two of each and one row.</para></summary>
        public static void PlaylistRevision(ReadOnlySpan<byte> selectedListContent, ReadOnlySpan<byte> playlistUri, Staging s)
        {
            var id = Identity(s, playlistUri);
            if (id.IsEmpty) return;
            var r = new ProtoReader(selectedListContent);
            ref var row = ref s.Playlists.RowFor(id, Authority.Full, (uint)PlaylistFields.Identity);

            var items = s.Run(Relation.PlaylistTracks);
            int pos = 0, length = 0;
            bool truncated = false, sawContents = false;

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 2: length = r.Int32(); break;             // SelectedListContent.length (the server's total)
                    case 3: Attributes(r.Message(), s, ref row); break;
                    case 5:                                        // contents: ListItems { pos, truncated, items[] }
                        {
                            var contents = r.Message();
                            sawContents = true;
                            while (contents.Next())
                            {
                                switch (contents.Field)
                                {
                                    case 1: pos = contents.Int32(); break;
                                    case 2: truncated = contents.Bool(); break;
                                    case 3: Item(contents.Message(), s, ref items); break;
                                    default: contents.Skip(); break;
                                }
                            }
                            break;
                        }
                    case 16: row.OwnerUri = UserUri(s, r.Bytes()); break;   // owner_username → "spotify:user:<name>"
                    case 18:
                        row.Caps = Capabilities(r.Message());
                        row.Known |= (uint)PlaylistFields.Capabilities;
                        break;
                    default: r.Skip(); break;
                }
            }

            if (length > 0) row.TrackCount = length;
            // `truncated` is the server saying "there is more after this window", which is exactly `ReplacePage` at
            // `pos` with the whole-list total — the relation stays Partial until its own length reaches it (D7). An
            // untruncated answer starting at 0 is the whole list and settles Complete, EMPTY INCLUDED: "this playlist
            // has no tracks" is a real, renderable answer. A revision that carried no `contents` at all says nothing
            // about the membership and must leave it Unknown (ch 03 §7).
            if (!sawContents) { items.Discard(); return; }
            if (truncated || pos > 0) items.Page(in id, pos, length);
            else items.EndEvenIfEmpty(in id, items.Count);

            static void Attributes(ProtoReader attributes, Staging s, ref StagedPlaylist row)
            {
                while (attributes.Next())
                {
                    switch (attributes.Field)
                    {
                        case 1: row.Title = s.AddText(attributes.Bytes()); break;
                        case 2: row.Description = s.AddText(attributes.Bytes()); break;
                        case 3: row.Image = Image(s, attributes.Bytes()); break;
                        case 4:
                            if (attributes.Bool()) row.Caps |= (byte)PlaylistCaps.IsCollaborative;
                            row.Known |= (uint)PlaylistFields.Capabilities;
                            break;
                        case 6:
                            row.FlagsMask |= (uint)PlaylistFlags.DeletedByOwner;
                            if (attributes.Bool()) row.Flags |= (uint)PlaylistFlags.DeletedByOwner;
                            break;
                        case 11:
                            row.Format = FormatOf(attributes.Bytes());
                            row.Known |= (uint)PlaylistFields.Format;
                            break;
                        case 13:                                   // picture_size { target_name, url }
                            {
                                var url = attributes.Message().Bytes(2);
                                if (!url.IsEmpty && row.Image.IsEmpty) row.Image = s.AddText(url);
                                break;
                            }
                        default: attributes.Skip(); break;
                    }
                }
            }

            static byte Capabilities(ProtoReader caps)
            {
                byte value = 0;
                while (caps.Next())
                {
                    byte bit = caps.Field switch
                    {
                        1 => (byte)PlaylistCaps.CanView,
                        2 => (byte)PlaylistCaps.CanAdministratePermissions,
                        4 => (byte)PlaylistCaps.CanEditMetadata,
                        5 => (byte)PlaylistCaps.CanEditItems,
                        _ => 0,
                    };
                    if (bit == 0) caps.Skip();
                    else if (caps.Bool()) value |= bit;
                }
                return value;
            }

            static void Item(ProtoReader item, Staging s, ref EdgeRun items)
            {
                ref var edge = ref items.Add();
                StagedId target = default;
                while (item.Next())
                {
                    if (item.Field == 1) target = Identity(s, item.Bytes());
                    else if (item.Field == 2) ItemAttributes(item.Message(), s, ref edge);
                    else item.Skip();
                }
                if (target.IsEmpty) { items.DropLast(); return; }
                edge.Target = target;
            }

            static void ItemAttributes(ProtoReader a, Staging s, ref StagedEdge edge)
            {
                while (a.Next())
                {
                    if (a.Field == 1) edge.Aux = UserUri(s, a.Bytes());       // added_by (a username, not a uri)
                    else if (a.Field == 2) edge.At = Instant((long)a.Varint()); // timestamp: ms (recents) or s — Instant reads both
                    else if (a.Field == 11) Chart(a.Message(), ref edge);
                    else if (a.Field == 12) edge.Text = Hex(s, a.Bytes());    // item_id — the reconciler key
                    else a.Skip();
                }
            }

            // format_attributes: the KEY is the payload on a recents item, but on a chart playlist the VALUE is too.
            static void Chart(ProtoReader attribute, ref StagedEdge edge)
            {
                attribute.Fields(1, 2, out var key, out var value);
                if (key.IsEmpty) return;
                if (Is(key, "status")) edge.B1 = ChartStatus(value);
                else if (Is(key, "current_pos")) edge.U0 = Clamp(Number(value));
                else if (Is(key, "previous_pos")) edge.U1 = Clamp(Number(value));

                static ushort Clamp(long n) => n is < 0 or > ushort.MaxValue ? (ushort)0 : (ushort)n;
            }
        }

        /// <summary>The chart status token → the byte `PlaylistTrackEdge.ChartStatus` carries. 0 is Unknown, which is
        /// what an item with no status attribute has and what a non-chart playlist's every row has.</summary>
        internal static byte ChartStatus(ReadOnlySpan<byte> status)
            => Is(status, "EQUAL") ? (byte)1
             : Is(status, "UP") ? (byte)2
             : Is(status, "DOWN") ? (byte)3
             : Is(status, "NEW") ? (byte)4
             : (byte)0;

        /// <summary>A `collection2v2.PageResponse` → one page of a library relation (ported from
        /// `CollectionWireMapper`). The library IS edges (G6): the parent is the account's own row, the targets are
        /// whatever the set holds, and a removal tombstone is simply left OUT of the page.</summary>
        public static void CollectionPage(ReadOnlySpan<byte> proto, LibraryEdgeKind set, ReadOnlySpan<byte> meUri,
                                          int offset, Staging s)
        {
            var parent = Identity(s, meUri);
            if (parent.IsEmpty) return;
            var r = new ProtoReader(proto);
            var items = s.Run(set switch
            {
                LibraryEdgeKind.SavedAlbums => Relation.SavedAlbums,
                LibraryEdgeKind.FollowedArtists => Relation.FollowedArtists,
                LibraryEdgeKind.SavedShows => Relation.SavedShows,
                _ => Relation.Liked,
            });
            bool more = false;

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1:                                        // items[] { uri, added_at, is_removed }
                        {
                            var item = r.Message();
                            ref var edge = ref items.Add();
                            StagedId target = default;
                            bool removed = false;
                            while (item.Next())
                            {
                                if (item.Field == 1) target = Identity(s, item.Bytes());
                                else if (item.Field == 2) edge.At = item.Int32();
                                else if (item.Field == 3) removed = item.Bool();
                                else item.Skip();
                            }
                            if (target.IsEmpty || removed) items.DropLast();
                            else edge.Target = target;
                            break;
                        }
                    case 2: more = !r.Bytes().IsEmpty; break;      // next_page_token
                    default: r.Skip(); break;
                }
            }

            // A page token means the server has more, and "more" is the only thing that keeps the relation Partial —
            // a last page with no token settles the whole list Complete at its own end.
            if (more || offset > 0) items.Page(in parent, offset, more ? 0 : offset + items.Count);
            else items.EndEvenIfEmpty(in parent, items.Count);
        }

        // ── 8. recents (ch 16 §7) ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One FLAT recents entry as the wire gave it, before grouping. The semantic payload of a recents
        /// item lives in the KEYS of its `format_attributes` (the values are empty, except `group_metadata`), so this
        /// carries those keys already interpreted.</summary>
        public struct RecentsItem
        {
            /// <summary>Hex <c>item_id</c> — the reconciler key. Uris repeat ~1,388× in a real list; ids do not.</summary>
            public TextRef ItemId;
            /// <summary>May be empty for a single-context group header, which is rendered from its children.</summary>
            public StagedId Uri;
            /// <summary>The suffix of a <c>content_type_*</c> key ("music", "podcasts"): the filter chips' axis.</summary>
            public RecentsContentType ContentType;
            /// <summary>The suffix VERBATIM when <see cref="ContentType"/> could not name it (G-060, ch 16 §8: "the wire
            /// token IS the label") — empty for a token the byte already carries, so a known token costs no text.</summary>
            public TextRef RawContentType;
            public long PlayedAtMs;
            /// <summary>The header's declared child count — never <c>child_uri.Count</c>, which the server truncates.</summary>
            public int ChildCount;
            /// <summary>The N of a <c>group_id_&lt;N&gt;</c> key: 0 = header, &gt;0 = collapsed member, -1 = single.</summary>
            public int GroupId;
            /// <summary>The header-only <c>children_group_id</c> marker.</summary>
            public bool HasChildrenGroupId;
            public RecentsReason Reason;
        }

        /// <summary>One grouped, display-ready recents row. <see cref="MembersStart"/>/<see cref="MembersLength"/>
        /// index back into the ITEM span the row was folded from — the members are the only complete account of which
        /// plays a card stands for, and they are kept rather than dropped (`RecentsList.Group`).</summary>
        public struct RecentsRow
        {
            public RecentsRowKind Kind;
            public TextRef ItemId;
            public StagedId Uri;
            public RecentsContentType ContentType;
            /// <inheritdoc cref="RecentsItem.RawContentType"/>
            public TextRef RawContentType;
            public long PlayedAtMs;
            public int ChildCount;
            public RecentsReason Reason;
            public int MembersStart, MembersLength;
        }

        /// <summary>A recents `SelectedListContent` → the ordered FLAT items (ported from `RecentsWireMapper.Map` +
        /// `ToItem`). Writes into the caller's span and returns how many it wrote; the caller owns the buffer, so a
        /// 9,446-item page costs one rented array and no per-item object.
        ///
        /// <para>The sibling of <see cref="PlaylistRevision"/>, and the one place that deliberately DIVERGES from it:
        /// `PlaylistWireMapper.ToMember` drops `format_attributes`, and a recents item carries almost all of its
        /// meaning in them.</para></summary>
        public static int RecentsPage(ReadOnlySpan<byte> selectedListContent, Staging s, Span<RecentsItem> into)
        {
            var r = new ProtoReader(selectedListContent);
            int n = 0;
            while (r.Next() && n < into.Length)
            {
                if (r.Field != 5) { r.Skip(); continue; }          // contents
                var contents = r.Message();
                while (contents.Next() && n < into.Length)
                {
                    if (contents.Field != 3) { contents.Skip(); continue; }
                    ref var item = ref into[n];
                    item = default;
                    item.GroupId = -1;
                    var wire = contents.Message();
                    while (wire.Next())
                    {
                        if (wire.Field == 1) item.Uri = Identity(s, wire.Bytes());
                        else if (wire.Field == 2) Attributes(wire.Message(), s, ref item);
                        else wire.Skip();
                    }
                    n++;
                }
            }
            return n;

            static void Attributes(ProtoReader a, Staging s, ref RecentsItem item)
            {
                while (a.Next())
                {
                    if (a.Field == 2) item.PlayedAtMs = (long)a.Varint();
                    else if (a.Field == 11) FormatAttribute(a.Message(), s, ref item);
                    else if (a.Field == 12) item.ItemId = Hex(s, a.Bytes());
                    else a.Skip();
                }
            }

            static void FormatAttribute(ProtoReader attribute, Staging s, ref RecentsItem item)
            {
                attribute.Fields(1, 2, out var key, out var value);
                if (key.IsEmpty) return;

                if (Is(key, "group_metadata")) { item.ChildCount = GroupMetadata(value); return; }
                if (StartsWith(key, "children_group_id")) { item.HasChildrenGroupId = true; return; }
                if (StartsWith(key, "group_id_"))
                {
                    long n = Number(key["group_id_".Length..]);
                    // A header (0) wins over a member index when both are somehow present — the min collapses right.
                    if (n >= 0) item.GroupId = item.GroupId < 0 ? (int)n : (int)Math.Min(item.GroupId, n);
                    return;
                }
                if (StartsWith(key, "recent_type_"))
                {
                    var suffix = key["recent_type_".Length..];
                    item.Reason = Is(suffix, "played") ? RecentsReason.Played
                                : Is(suffix, "saved") ? RecentsReason.Saved
                                : RecentsReason.Unknown;
                    return;
                }
                if (StartsWith(key, "content_type_"))
                {
                    var suffix = key["content_type_".Length..];
                    item.ContentType = Is(suffix, "music") ? RecentsContentType.Music
                                     : Is(suffix, "podcasts") ? RecentsContentType.Podcasts
                                     : RecentsContentType.None;
                    if (item.ContentType == RecentsContentType.None && !suffix.IsEmpty) item.RawContentType = s.AddText(suffix);
                }
            }

            // `group_metadata` rides as a base64 string VALUE under that one key. A malformed payload yields 0 — one
            // bad header must not sink a 9k-row page, which is `RecentsWireMapper`'s own rule.
            static int GroupMetadata(ReadOnlySpan<byte> base64)
            {
                if (base64.IsEmpty || base64.Length > 4096) return 0;
                Span<byte> decoded = stackalloc byte[3072];
                if (System.Buffers.Text.Base64.DecodeFromUtf8(base64, decoded, out _, out int written)
                    != System.Buffers.OperationStatus.Done) return 0;
                // RecentsGroupMetadata.child_count
                return (int)new ProtoReader(decoded[..written]).Varint(1);
            }
        }

        /// <summary>`RecentsList.Group`, ported verbatim (ch 16 §8): collapse flat items into display rows in WIRE
        /// ORDER. An item that heads a group (<c>group_id_0</c> / <c>children_group_id</c>) becomes ONE
        /// <see cref="RecentsRowKind.Group"/> row carrying its declared child count, and the members that follow it
        /// (<c>group_id_&lt;N&gt;</c>, N&gt;0) are absorbed into that card as its member range — kept, not dropped.
        /// Every ungrouped item becomes a <see cref="RecentsRowKind.Single"/> row.
        ///
        /// <para>A member with no header in front of it has no card to hang off; the wire never sends one, and
        /// inventing a row for it would double-count the play against the header that should have carried it.</para>
        ///
        /// <para>PURE, and deliberately kept so: it is the one recents rule with a test suite behind it, and
        /// <see cref="Recents"/> below composes it rather than reimplementing it.</para></summary>
        public static int RecentsGroup(ReadOnlySpan<RecentsItem> items, Span<RecentsRow> rows)
        {
            int n = 0, i = 0;
            while (i < items.Length && n < rows.Length)
            {
                ref readonly var item = ref items[i];
                if (IsHeader(in item))
                {
                    int end = i + 1;
                    while (end < items.Length && IsMember(in items[end])) end++;
                    Fill(ref rows[n++], in item, RecentsRowKind.Group, item.ChildCount, i + 1, end - i - 1);
                    i = end;
                    continue;
                }
                if (!IsMember(in item)) Fill(ref rows[n++], in item, RecentsRowKind.Single, 0, 0, 0);
                i++;
            }
            return n;

            static void Fill(ref RecentsRow row, in RecentsItem item, RecentsRowKind kind,
                             int childCount, int membersStart, int membersLength)
            {
                row.Kind = kind;
                row.ItemId = item.ItemId;
                row.Uri = item.Uri;
                row.ContentType = item.ContentType;
                row.RawContentType = item.RawContentType;
                row.PlayedAtMs = item.PlayedAtMs;
                row.ChildCount = childCount;
                row.Reason = item.Reason;
                row.MembersStart = membersStart;
                row.MembersLength = membersLength;
            }

            static bool IsHeader(in RecentsItem item) => item.HasChildrenGroupId || item.GroupId == 0;
            static bool IsMember(in RecentsItem item) => !IsHeader(in item) && item.GroupId > 0;
        }

        /// <summary>THE recents entry point: a `SelectedListContent` page → the grouped snapshot, staged on the
        /// account's own row (<c>Edges.Recents</c> / <c>Edges.RecentsMembers</c>, ch 16 §7.2). Composes the two pure
        /// halves above over the <see cref="Staging"/>'s own pooled scratch, so a 9,446-item answer costs no
        /// allocation after the first one.</summary>
        public static void Recents(ReadOnlySpan<byte> selectedListContent, ReadOnlySpan<byte> meUri,
                                   ReadOnlySpan<byte> revision, Staging s)
        {
            var parent = Identity(s, meUri);
            if (parent.IsEmpty) return;

            // Decode, and grow-and-retry once per doubling: the page never says how many items it holds up front, and
            // a truncated read would silently drop the tail of somebody's history. The arena is rewound between
            // attempts, so a retry costs the decode again and nothing else.
            int mark = s.TextMark, count;
            while (true)
            {
                count = RecentsPage(selectedListContent, s, s.RecentsScratch);
                if (count < s.RecentsScratch.Length) break;
                s.RewindText(mark);
                s.GrowRecentsScratch();
            }

            var items = s.RecentsScratch[..count];
            var rowScratch = s.RecentsRowScratch(count);
            var rows = rowScratch[..RecentsGroup(items, rowScratch)];

            ref var page = ref s.RecentsPages.Add();
            page.Parent = parent;
            page.Revision = s.AddText(revision);
            page.RowStart = s.RecentsRows.Count;
            page.MemberStart = s.RecentsMembers.Count;

            for (int i = 0; i < rows.Length; i++)
            {
                ref readonly var row = ref rows[i];
                int membersStart = s.RecentsMembers.Count - page.MemberStart;
                for (int j = 0; j < row.MembersLength; j++)
                {
                    ref readonly var member = ref items[row.MembersStart + j];
                    ref var staged = ref s.RecentsMembers.Add();
                    staged.Id = member.Uri;
                    staged.ItemId = member.ItemId;
                    staged.PlayedAtMs = member.PlayedAtMs;
                }

                ref var target = ref s.RecentsRows.Add();
                target.Id = row.Uri;
                target.ItemId = row.ItemId;
                target.PlayedAtMs = row.PlayedAtMs;
                target.ChildCount = row.ChildCount;
                target.MembersStart = membersStart;
                target.MembersLen = row.MembersLength;
                target.Reason = (byte)row.Reason;
                target.ContentType = (byte)row.ContentType;
                target.Kind = (byte)row.Kind;
            }

            page.RowCount = s.RecentsRows.Count - page.RowStart;
            page.MemberCount = s.RecentsMembers.Count - page.MemberStart;
        }
    }
}

// ── the recents scratch (pooled with its Staging, P8) ─────────────────────────────────────────────────────────────────

/// <summary>The two decode-side scratch buffers <c>Decode.Recents</c> composes its two pure halves over. They live on
/// the <see cref="Staging"/> — not on a static — because a decode runs on the receiving thread and two sockets may be
/// decoding at once (C10), and because pooling them with the buffer is what makes a refresh allocate nothing.</summary>
public sealed partial class Staging
{
    // LAZY, like every staged list: a recents answer is one endpoint out of a dozen, and a buffer wide enough for a
    // 9,446-item page is ~600 KB across the pool. Nothing that never decodes recents pays for it.
    Spotify.Decode.RecentsItem[]? _recentsScratch;
    Spotify.Decode.RecentsRow[]? _recentsRowScratch;

    internal Span<Spotify.Decode.RecentsItem> RecentsScratch
        => _recentsScratch ??= new Spotify.Decode.RecentsItem[256];

    internal void GrowRecentsScratch()
        => _recentsScratch = new Spotify.Decode.RecentsItem[(_recentsScratch?.Length ?? 128) * 2];

    internal Span<Spotify.Decode.RecentsRow> RecentsRowScratch(int items)
    {
        if (_recentsRowScratch is null || _recentsRowScratch.Length < items)
            _recentsRowScratch = new Spotify.Decode.RecentsRow[items < 256 ? 256 : items];
        return _recentsRowScratch;
    }
}
