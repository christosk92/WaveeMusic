// ── Spotify/Spotify.Decode.Playlist.cs ─────────────────────────────────────────────────────────────────────────────────
// the playlist page's three folds: the liked-songs content filters (JSON), the playlist4 format_attributes (daylist
// window, chart facts, session-control tuning, the membership revision) and the playlist extender's recommendations
//
// Role: CORE
// Owner: O (WP-5.O stream A)
// Wave: 5
// Budget: 450 lines
// Spec: ch 06 §7 DATA GAPS, ch 07 §7 G4, WP-5.O contract §2.4 / §2.5
//
// WHAT 0.2.9 USED INSTEAD OF A `fetchPlaylist` HASH. Nothing pathfinder-shaped at all: `PlaylistFetcher.HeaderOf` read the
// daylist window, the chart facts and the Tune options out of the SAME playlist4 v2 read that carries the header and
// the membership (`GET /playlist/v2/playlist/{id}?decorate=revision,attributes,length,owner,capabilities,picture`):
// `ListAttributes.format_attributes` (field 12, a key/value bag) and `ListItems.available_signals` (field 5), via
// `DaylistWindowOf` / `ChartInfoOf` / `TuningOf`. `PlaylistFormatAttributes` below is those three, byte-for-byte on the
// wire instead of over a parsed message, and it rides the PlaylistRead route beside `Decode.PlaylistRevision`.
//
// Every fold is one pass per operation into `Staging` (P14): worker thread, no interner, no live table. Text is
// `TextRef`; the commit interns it.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // ══ 1. the liked-songs content filters (/content-filter/v1/liked-songs) ══════════════════════════════════════

        const string TagsContains = "tags contains ";

        /// <summary><c>{ "contentFilters": [ { "title", "query": "tags contains &lt;token&gt;" } ] }</c> →
        /// <see cref="Staging.ContentFilters"/> for <paramref name="meUri"/>, in SERVER order (0.2.9
        /// <c>ContentFilterParser</c> verbatim: a missing title or query drops the chip, any other query form drops it, a
        /// quoted token is unquoted, tokens dedupe case-insensitively). An answer with no usable chip — a malformed body
        /// included, as 0.2.9 read it — is ONE row with <c>Owner</c> set and empty texts: a publish of the empty set.</summary>
        public static void LikedContentFilters(ReadOnlySpan<byte> json, ReadOnlySpan<byte> meUri, Staging s)
        {
            var owner = Identity(s, meUri);
            if (owner.IsEmpty) return;
            var list = s.ContentFilters;
            int first = list.Count, mark = s.TextMark;
            try
            {
                var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
                if (r.Read() && r.TokenType == JsonTokenType.StartObject)
                    for (int d = r.CurrentDepth; Next(ref r, d);)
                    {
                        if (!r.ValueTextEquals("contentFilters"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                        for (int arr = r.CurrentDepth; Element(ref r, arr);)
                        {
                            TextRef title = default, query = default;
                            for (int e = r.CurrentDepth; Next(ref r, e);)
                            {
                                if (r.ValueTextEquals("title"u8)) { r.Read(); title = s.AddJson(ref r); }
                                else if (r.ValueTextEquals("query"u8)) { r.Read(); query = s.AddJson(ref r); }
                                else SkipValue(ref r);
                            }
                            if (title.IsEmpty || query.IsEmpty) continue;
                            var token = TokenOf(s, query);
                            if (token.IsEmpty || SeenToken(s, list, first, token)) continue;
                            ref var chip = ref list.Add();
                            chip.Owner = owner;
                            chip.Title = title;
                            chip.Token = token;
                        }
                    }
            }
            catch (JsonException)
            {
                while (list.Count > first) list.Drop();
                s.RewindText(mark);
            }
            if (list.Count > first) return;
            ref var empty = ref list.Add();
            empty.Owner = owner;
        }

        /// <summary>The token of <c>tags contains &lt;token&gt;</c> (trimmed, one layer of quotes removed), or empty.</summary>
        static TextRef TokenOf(Staging s, TextRef query)
        {
            var q = s.Utf8(query).Trim((byte)' ');
            if (q.Length <= TagsContains.Length || !Is(q[..TagsContains.Length], TagsContains)) return default;
            var token = q[TagsContains.Length..].Trim((byte)' ');
            if (token.Length >= 2 && (token[0] == (byte)'"' || token[0] == (byte)'\'') && token[^1] == token[0])
                token = token[1..^1].Trim((byte)' ');
            if (token.IsEmpty) return default;
            int offset = query.Offset + (int)OffsetOf(s.Utf8(query), token);
            return new TextRef(offset, token.Length);

            // The token is a sub-slice of the staged query: its offset inside it is pointer arithmetic, not a copy.
            static long OffsetOf(ReadOnlySpan<byte> whole, ReadOnlySpan<byte> part)
                => System.Runtime.CompilerServices.Unsafe.ByteOffset(
                       ref System.Runtime.InteropServices.MemoryMarshal.GetReference(whole),
                       ref System.Runtime.InteropServices.MemoryMarshal.GetReference(part));
        }

        static bool SeenToken(Staging s, StagedList<StagedContentFilter> list, int first, TextRef token)
        {
            var t = s.Utf8(token);
            for (int i = first; i < list.Count; i++)
            {
                var other = s.Utf8(list[i].Token);
                if (other.Length != t.Length) continue;
                bool same = true;
                for (int k = 0; k < t.Length && same; k++) same = (t[k] | 0x20) == (other[k] | 0x20) || t[k] == other[k];
                if (same) return true;
            }
            return false;
        }

        // ══ 2. format_attributes: daylist · chart · tuning · revision (0.2.9 PlaylistFetcher.HeaderOf) ═══════════════

        const string SelectedSignalsKey = "session_control.selected_signals";
        const string DisplayNamePrefix = "session_control_display.displayName.";
        const string ResetSignal = "session-control-reset";

        /// <summary>A <c>SelectedListContent</c> → one staged row speaking for <see cref="PlaylistFields.Daylist"/>,
        /// <see cref="PlaylistFields.Chart"/> and <see cref="PlaylistFields.Tuning"/> — ALWAYS all three, zeroed for a
        /// playlist of another format, because "not a daylist" is an answer (0.2.9 returned (0, 0)) — plus the membership
        /// revision and the Tune option run. Called beside <see cref="PlaylistRevision"/> on every PlaylistRead answer.</summary>
        public static void PlaylistFormatAttributes(ReadOnlySpan<byte> selectedListContent, ReadOnlySpan<byte> playlistUri, Staging s)
        {
            var id = Identity(s, playlistUri);
            if (id.IsEmpty) return;
            ReadOnlySpan<byte> revision = default, attributes = default, contents = default;
            for (var r = new ProtoReader(selectedListContent); r.Next();)
            {
                if (r.Field == 1 && r.Wire == 2) revision = r.Bytes();
                else if (r.Field == 3 && r.Wire == 2) attributes = r.Bytes();
                else if (r.Field == 5 && r.Wire == 2) contents = r.Bytes();
                else r.Skip();
            }
            // A diff answer carries no attributes: it says nothing about these groups, so nothing is staged.
            if (attributes.IsEmpty) return;

            ref var row = ref s.Playlists.RowFor(id, Authority.Full,
                (uint)(PlaylistFields.Daylist | PlaylistFields.Chart | PlaylistFields.Tuning));
            Span<char> text = stackalloc char[128];
            int revisionChars = Api.FormatRevision(revision, text);
            if (revisionChars > 0)
            {
                Span<byte> ascii = stackalloc byte[revisionChars];
                for (int i = 0; i < revisionChars; i++) ascii[i] = (byte)text[i];
                row.Revision = s.AddText(ascii);
            }

            ReadOnlySpan<byte> format = new ProtoReader(attributes).Bytes(11);
            bool daylist = Is(format, "daylist"), chart = Is(format, "chart");
            for (var a = new ProtoReader(attributes); a.Next();)
            {
                if (a.Field != 12 || a.Wire != 2) { a.Skip(); continue; }
                a.Message().Fields(1, 2, out var key, out var value);
                if (key.IsEmpty || value.IsEmpty) continue;
                if (daylist && Is(key, "expires")) row.DaylistExpiresAt = InstantSeconds(value);
                else if (daylist && Is(key, "created")) row.DaylistCreatedAt = InstantSeconds(value);
                else if (chart && Is(key, "new_entries_count"))
                    row.ChartNewEntries = (ushort)Math.Clamp(Number(value), 0, ushort.MaxValue);
                else if (chart && Is(key, "last_updated")) row.ChartUpdatedAt = InstantSeconds(value);
                else if (chart && Is(key, "rank_type")) row.ChartRankType = s.AddText(value);
            }

            // Tuning: a 24-byte revision and at least one signal, or the playlist is not tunable (0.2.9 TuningOf).
            var tunings = s.PlaylistTuningOptions;
            int start = tunings.Count;
            if (revision.Length == 24 && !contents.IsEmpty)
            {
                var selected = FormatValue(attributes, SelectedSignalsKey);
                if (!IsBlank(selected)) row.TuningSelected = s.AddText(selected);
                row.TuningRevision = revisionChars > 0 ? Playlist.RevisionHash(text[..revisionChars]) : 0;
                for (var c = new ProtoReader(contents); c.Next();)
                {
                    if (c.Field != 5 || c.Wire != 2) { c.Skip(); continue; }
                    var identifier = c.Message().Bytes(1);
                    if (IsBlank(identifier)) continue;
                    bool reset = Is(identifier, ResetSignal);
                    ref var option = ref tunings.Add();
                    option.Playlist = id;
                    option.Identifier = s.AddText(identifier);
                    option.Kind = (byte)(reset ? TuningOptionKind.Reset : TuningOptionKind.Choice);
                    int dollar = identifier.LastIndexOf((byte)'$');
                    if (!reset && dollar >= 0 && dollar + 1 < identifier.Length)
                    {
                        var label = FormatValue(attributes, DisplayNamePrefix, identifier[(dollar + 1)..]);
                        if (!IsBlank(label)) option.DisplayName = s.AddText(label);
                    }
                }
            }
            if (tunings.Count > start) return;
            row.TuningSelected = default;
            row.TuningRevision = 0;
            ref var none = ref tunings.Add();                      // "no options" is an answer
            none.Playlist = id;
        }

        /// <summary>The value of the format attribute <paramref name="key"/> (+ <paramref name="suffix"/>), or empty.</summary>
        static ReadOnlySpan<byte> FormatValue(ReadOnlySpan<byte> attributes, string key, ReadOnlySpan<byte> suffix = default)
        {
            for (var a = new ProtoReader(attributes); a.Next();)
            {
                if (a.Field != 12 || a.Wire != 2) { a.Skip(); continue; }
                a.Message().Fields(1, 2, out var k, out var v);
                if (k.Length == key.Length + suffix.Length && Is(k[..key.Length], key) && k[key.Length..].SequenceEqual(suffix))
                    return v;
            }
            return default;
        }

        static bool IsBlank(ReadOnlySpan<byte> utf8) => utf8.Trim((byte)' ').IsEmpty;

        /// <summary>0.2.9 <c>InstantMs</c> in seconds: an epoch in seconds or milliseconds (11+ digits), or ISO-8601; 0 when
        /// unparsable.</summary>
        internal static int InstantSeconds(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty) return 0;
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
                return n <= 0 ? 0 : AtMost(n < 100_000_000_000L ? n : n / 1000);
            Span<char> chars = stackalloc char[Math.Min(value.Length, 64)];
            for (int i = 0; i < chars.Length; i++) chars[i] = (char)value[i];
            return DateTimeOffset.TryParse(chars, CultureInfo.InvariantCulture,
                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
                ? AtMost(at.ToUnixTimeSeconds()) : 0;
        }

        // ══ 3. the playlist extender (/playlistextender/extendp/) ════════════════════════════════════════════════════

        /// <summary><c>{ "recommendedTracks": [ { id, name, artists[{id,name}], album{id,name,imageUrl}, duration, explicit } ] }</c>
        /// → staged track rows (Thin: a mention, not a fetch), their artists and albums, and the track uris in server
        /// order into <paramref name="uris"/> (0.2.9 <c>PlaylistExtenderClient</c>). Malformed ⇒ nothing.</summary>
        public static void PlaylistExtender(ReadOnlySpan<byte> json, Staging s, List<string> uris)
        {
            try
            {
                var r = new Utf8JsonReader(json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;
                for (int d = r.CurrentDepth; Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("recommendedTracks"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int arr = r.CurrentDepth; Element(ref r, arr);) RecommendedTrack(ref r, s, uris);
                }
            }
            catch (JsonException) { /* whatever landed before the fault stands; the rest is not an answer */ }
        }

        static void RecommendedTrack(ref Utf8JsonReader r, Staging s, List<string> uris)
        {
            TextRef title = default, image = default, albumTitle = default;
            StagedId track = default, album = default;
            long duration = 0;
            bool isExplicit = false;
            int credit = s.CreditMark;
            var artists = s.Run(Relation.TrackArtists);
            for (int d = r.CurrentDepth; Next(ref r, d);)
            {
                if (r.ValueTextEquals("id"u8)) { r.Read(); track = CatalogId(ref r, s, "spotify:track:"); }
                else if (r.ValueTextEquals("name"u8)) { r.Read(); title = s.AddJson(ref r); }
                else if (r.ValueTextEquals("duration"u8)) { r.Read(); duration = Num(ref r); }
                else if (r.ValueTextEquals("explicit"u8)) { r.Read(); isExplicit = Flag(ref r); }
                else if (r.ValueTextEquals("artists"u8) && EnterArray(ref r))
                {
                    for (int arr = r.CurrentDepth; Element(ref r, arr);)
                    {
                        StagedId artist = default;
                        TextRef name = default;
                        for (int e = r.CurrentDepth; Next(ref r, e);)
                        {
                            if (r.ValueTextEquals("id"u8)) { r.Read(); artist = CatalogId(ref r, s, "spotify:artist:"); }
                            else if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                            else SkipValue(ref r);
                        }
                        if (artist.IsEmpty) continue;
                        artists.Add(in artist);
                        if (name.IsEmpty) continue;
                        s.Artists.RowFor(artist, Authority.Thin, (uint)ArtistFields.Name).Name = name;
                        s.AppendCredit(s.Utf8(name), credit);
                    }
                }
                else if (r.ValueTextEquals("album"u8))
                {
                    for (int e = Fields(ref r); Next(ref r, e);)
                    {
                        if (r.ValueTextEquals("id"u8)) { r.Read(); album = CatalogId(ref r, s, "spotify:album:"); }
                        else if (r.ValueTextEquals("name"u8)) { r.Read(); albumTitle = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("imageUrl"u8)) { r.Read(); image = s.AddJson(ref r); }
                        else SkipValue(ref r);
                    }
                }
                else SkipValue(ref r);
            }
            if (track.IsEmpty || title.IsEmpty)
            {
                artists.Discard();
                s.TakeCredit(credit);
                return;
            }
            artists.End(in track);
            if (!album.IsEmpty)
            {
                ref var a = ref s.Albums.RowFor(album, Authority.Thin, (uint)(albumTitle.IsEmpty ? AlbumFields.Image : AlbumFields.Title | AlbumFields.Image));
                a.Title = albumTitle;
                a.Image = image;
            }
            ref var row = ref s.Tracks.RowFor(track, Authority.Thin, (uint)TrackFields.Identity);
            row.Title = title;
            row.Image = image;
            row.AlbumUri = album;
            row.ArtistLine = s.TakeCredit(credit);
            row.DurationMs = (int)Math.Clamp(duration, 0, int.MaxValue);
            if (isExplicit) row.Flags |= (uint)TrackFlags.Explicit;
            uris.Add(track.Packed.Text);                           // a gid-form id formats without the interner
        }

        /// <summary>A bare base62 id the JSON names → the packed identity of <paramref name="prefix"/> + id (a gid parses
        /// without text; anything else is dropped — the extender only answers catalogue ids).</summary>
        static StagedId CatalogId(ref Utf8JsonReader r, Staging s, string prefix)
        {
            if (r.TokenType != JsonTokenType.String || r.HasValueSequence || r.ValueSpan.Length is 0 or > 64) return default;
            Span<byte> uri = stackalloc byte[prefix.Length + r.ValueSpan.Length];
            for (int i = 0; i < prefix.Length; i++) uri[i] = (byte)prefix[i];
            r.ValueSpan.CopyTo(uri[prefix.Length..]);
            return EntityId.TryParseGid(uri, out var id) ? new StagedId(id) : default;
        }
    }
}
