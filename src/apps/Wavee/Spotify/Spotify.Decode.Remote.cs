// ── Spotify/Spotify.Decode.Remote.cs ────────────────────────────────────────────────────────────────────────────────
// playing a context: the inbound play / transfer body, the context-resolve page, the autoplay request (gap batch B1,
// G-071 decode half, G-045 context resolve + autoplay, G-070's decode)
//
// Role: CORE
// Owner: E
// Wave: gap batch B1
// Budget: 420 lines
// Spec: gap register G-071/G-045/G-070 — a named partial of Spotify.Decode.cs (Spotify.Decode.Connect.cs is already
//       past its budget, so the new folds live here and share its ClusterBuffer)
//
// AN INBOUND PLAY OR TRANSFER USED TO CARRY NOTHING. `ConnectCommand` folds a controller's body to a `RemoteCommand` —
// an endpoint ordinal, a message id, a seek, a bool — and that is right for pause and skip. A `play` names a CONTEXT, a
// track to start at and a position; a `transfer` carries a whole base64 `TransferState` (the phone's queue, its
// position, its shuffle). None of it was decoded, so Wavee claimed ownership, announced itself active, and played
// nothing. `ConnectLoad` is the other half, and it follows the ClusterBuffer discipline (Decode.Connect.cs): a dealer
// frame decodes on the dealer's thread, text goes into the pooled buffer's arena, the rows go into its track list, and
// the value that crosses to the playback host carries RANGES.
//
//     { command: { endpoint: "play",                                               RemoteLoad
//         context: { uri, url, pages: [ { tracks: [ … ] } ] },                  ─▶  ContextUri, ContextUrl, Tracks
//         options | prepare_play_options: { skip_to: { track_uri, track_uid, track_index },   SkipTo*
//                    seek_to, initially_paused, player_options_override: {…} },               SeekToMs, Paused, Shuffle/Repeat
//         play_origin: { feature_identifier, view_uri } } }                                  FeatureIdentifier, ViewUri
//     { command: { endpoint: "transfer", data: "<base64 TransferState>",
//         options: { restore_paused, restore_position, restore_track, retain_session } } }
//
// Two 0.2.9 gaps are closed on purpose: `skip_to` was read under either options object but `player_options_override`
// only under `options`, so a desktop-shaped `prepare_play_options.player_options_override` was silently dropped — both
// are read under both here; and `play_origin` was never read inbound at all.
//
// CONTEXT-RESOLVE (`/context-resolve/v1/<uri>`) and the AUTOPLAY answer are the same JSON (0.2.9 `ContextJson`): a root
// `uri`, `metadata`, `tracks[]` and/or `pages[].tracks[]`, and the first non-empty `next_page_url`. Each track is a
// `ClusterTrack` in the buffer — uri, uid, provider and the display picks off its metadata map — so a queue built from a
// resolve and one built from a cluster are the same rows.

using System.Buffers;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>What an inbound <c>play</c> or <c>transfer</c> asks for — the value the playback host resolves into a
        /// queue and a <c>Playback.Input.Play</c> under the claim epoch (G-071). Text and rows are RANGES into the
        /// <see cref="ClusterBuffer"/> the decode was given; return the buffer after the host has read them.</summary>
        public struct RemoteLoad
        {
            /// <summary><see cref="RemoteCmd.Play"/> or <see cref="RemoteCmd.Transfer"/>; <see cref="RemoteCmd.Unknown"/>
            /// when the body was neither (or could not be read).</summary>
            public RemoteCmd Kind;
            public TextRef ContextUri, ContextUrl;
            /// <summary>The track to start at: a uri, a uid, an index (-1 when unstated). The host matches uid, then uri,
            /// then index — 0.2.9's order.</summary>
            public TextRef SkipToUri, SkipToUid;
            public int SkipToIndex;
            /// <summary>Where to start, in ms; -1 when unstated.</summary>
            public long SeekToMs;
            public bool InitiallyPaused;
            /// <summary>-1 unstated, 0 off, 1 on.</summary>
            public sbyte Shuffle;
            /// <summary>-1 unstated, else a <see cref="RepeatMode"/> (track wins over context).</summary>
            public sbyte Repeat;
            public TextRef FeatureIdentifier, ViewUri;
            /// <summary>The rows the body carried: a play's embedded pages (play them as sent, no resolve), or a transfer's
            /// queue.</summary>
            public int TrackStart, TrackCount;

            // ── transfer only ──
            public bool HasPlayback;
            public long TimestampMs, PositionAsOfMs;
            public double Speed;
            public bool Paused;
            /// <summary>The track the remote was on (a 16-byte gid with no uri is spelled back as a track uri).</summary>
            public bool HasCurrent;
            public ClusterTrack Current;
            public TextRef CurrentUid;
            /// <summary>The remote was playing out of its user queue: the current track is the queue's head.</summary>
            public bool IsPlayingQueue;
            /// <summary><c>restore_paused: "kill"</c> — play even if the remote was paused.</summary>
            public bool ForcePlay;
            /// <summary><c>restore_position: "extrapolate"</c> — add the time since <see cref="TimestampMs"/> when playing.</summary>
            public bool Extrapolate;
            /// <summary><c>restore_track: "always_play_something"</c> — fall back to the context head when no track is named.</summary>
            public bool AlwaysPlaySomething;
            /// <summary><c>retain_session: "do_not_retain"</c> — mint a new session id.</summary>
            public bool NewSession;
        }

        /// <summary>The dealer REQUEST body of a <c>play</c> / <c>transfer</c> → a <see cref="RemoteLoad"/> over
        /// <paramref name="into"/>. Any other endpoint answers <see cref="RemoteCmd.Unknown"/> and stages nothing; a
        /// malformed body answers what it read before the fault.</summary>
        public static RemoteLoad ConnectLoad(ReadOnlySpan<byte> payload, ClusterBuffer into)
        {
            var load = default(RemoteLoad);
            load.SkipToIndex = -1;
            load.SeekToMs = -1;
            load.Shuffle = -1;
            load.Repeat = -1;
            load.Speed = 1.0;
            load.TrackStart = into.TrackCount;

            var r = new Utf8JsonReader(payload);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return load;
            try
            {
                for (int root = r.CurrentDepth; Next(ref r, root);)
                {
                    if (!r.ValueTextEquals("command"u8)) { SkipValue(ref r); continue; }
                    for (int c = Fields(ref r); Next(ref r, c);)
                    {
                        if (r.ValueTextEquals("endpoint"u8))
                        {
                            r.Read();
                            load.Kind = Says(ref r, "play") ? RemoteCmd.Play : Says(ref r, "transfer") ? RemoteCmd.Transfer : RemoteCmd.Unknown;
                        }
                        else if (r.ValueTextEquals("context"u8)) PlayContext(ref r, into, ref load);
                        else if (r.ValueTextEquals("context_uri"u8)) { r.Read(); if (load.ContextUri.IsEmpty) load.ContextUri = Text(ref r, into); }
                        else if (r.ValueTextEquals("options"u8) || r.ValueTextEquals("prepare_play_options"u8)) PlayOptions(ref r, into, ref load);
                        else if (r.ValueTextEquals("seek_to"u8)) { r.Read(); if (load.SeekToMs < 0) load.SeekToMs = Num(ref r, -1); }
                        else if (r.ValueTextEquals("play_origin"u8))
                        {
                            for (int o = Fields(ref r); Next(ref r, o);)
                            {
                                if (r.ValueTextEquals("feature_identifier"u8)) { r.Read(); load.FeatureIdentifier = Text(ref r, into); }
                                else if (r.ValueTextEquals("view_uri"u8)) { r.Read(); load.ViewUri = Text(ref r, into); }
                                else SkipValue(ref r);
                            }
                        }
                        else if (r.ValueTextEquals("data"u8)) { r.Read(); TransferData(ref r, into, ref load); }
                        else SkipValue(ref r);
                    }
                }
            }
            catch (JsonException)
            {
                // A truncated body: keep what was read. The command's own decode already answered Ok = false for it.
            }
            if (load.Kind == RemoteCmd.Unknown) { into.TrackCount = load.TrackStart; return load; }
            load.TrackCount = into.TrackCount - load.TrackStart;
            return load;
        }

        static void PlayContext(ref Utf8JsonReader r, ClusterBuffer into, ref RemoteLoad load)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); load.ContextUri = Text(ref r, into); }
                else if (r.ValueTextEquals("url"u8)) { r.Read(); load.ContextUrl = Text(ref r, into); }
                else if (r.ValueTextEquals("pages"u8) && EnterArray(ref r))
                {
                    for (int pages = r.CurrentDepth; Element(ref r, pages);)
                        for (int page = r.CurrentDepth; Next(ref r, page);)
                        {
                            if (r.ValueTextEquals("tracks"u8)) JsonTracks(ref r, into);
                            else SkipValue(ref r);
                        }
                }
                else SkipValue(ref r);
            }
        }

        static void PlayOptions(ref Utf8JsonReader r, ClusterBuffer into, ref RemoteLoad load)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("skip_to"u8))
                {
                    for (int k = Fields(ref r); Next(ref r, k);)
                    {
                        if (r.ValueTextEquals("track_uri"u8)) { r.Read(); load.SkipToUri = Text(ref r, into); }
                        else if (r.ValueTextEquals("track_uid"u8)) { r.Read(); load.SkipToUid = Text(ref r, into); }
                        else if (r.ValueTextEquals("track_index"u8)) { r.Read(); load.SkipToIndex = (int)Math.Clamp(Num(ref r, -1), -1, int.MaxValue); }
                        else SkipValue(ref r);
                    }
                }
                else if (r.ValueTextEquals("seek_to"u8)) { r.Read(); load.SeekToMs = Num(ref r, -1); }
                else if (r.ValueTextEquals("initially_paused"u8)) { r.Read(); load.InitiallyPaused = Flag(ref r); }
                else if (r.ValueTextEquals("player_options_override"u8))
                {
                    bool repeatTrack = false, repeatContext = false, sawRepeat = false;
                    for (int o = Fields(ref r); Next(ref r, o);)
                    {
                        if (r.ValueTextEquals("shuffling_context"u8)) { r.Read(); load.Shuffle = (sbyte)(Flag(ref r) ? 1 : 0); }
                        else if (r.ValueTextEquals("repeating_track"u8)) { r.Read(); repeatTrack = Flag(ref r); sawRepeat = true; }
                        else if (r.ValueTextEquals("repeating_context"u8)) { r.Read(); repeatContext = Flag(ref r); sawRepeat = true; }
                        else SkipValue(ref r);
                    }
                    if (sawRepeat) load.Repeat = (sbyte)(repeatTrack ? RepeatMode.Track : repeatContext ? RepeatMode.Context : RepeatMode.Off);
                }
                else if (r.ValueTextEquals("restore_paused"u8)) { r.Read(); load.ForcePlay = Says(ref r, "kill"); }
                else if (r.ValueTextEquals("restore_position"u8)) { r.Read(); load.Extrapolate = Says(ref r, "extrapolate"); }
                else if (r.ValueTextEquals("restore_track"u8)) { r.Read(); load.AlwaysPlaySomething = Says(ref r, "always_play_something"); }
                else if (r.ValueTextEquals("retain_session"u8)) { r.Read(); load.NewSession = Says(ref r, "do_not_retain"); }
                else SkipValue(ref r);
            }
        }

        /// <summary>A transfer's <c>data</c>: base64 → <c>transfer.TransferState</c> (transfer_state.proto:
        /// <c>options=1{ shuffling_context=1, repeating_context=2, repeating_track=3 }, playback=2{ timestamp=1,
        /// position_as_of_timestamp=2, speed=3, paused=4, current_track=5 }, current_session=3{ context=2{ uri=1, url=2 },
        /// current_uid=3 }, queue=4{ tracks=1[], is_playing_queue=2 }</c>). The unescape and the decode share one pooled
        /// buffer, so a 500-row queue costs no allocation after warm-up.</summary>
        static void TransferData(ref Utf8JsonReader r, ClusterBuffer into, ref RemoteLoad load)
        {
            if (r.TokenType != JsonTokenType.String) { r.Skip(); return; }
            int max = r.HasValueSequence ? (int)r.ValueSequence.Length : r.ValueSpan.Length;
            if (max == 0) return;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(max);
            try
            {
                int length = r.CopyString(buffer);
                if (System.Buffers.Text.Base64.DecodeFromUtf8InPlace(buffer.AsSpan(0, length), out int written)
                    != OperationStatus.Done) return;
                TransferState(buffer.AsSpan(0, written), into, ref load);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        /// <summary>A decoded <c>TransferState</c> → the transfer half of a <see cref="RemoteLoad"/>. Public so a test can
        /// pin the field map against the generated message without a base64 envelope around it.</summary>
        public static void TransferState(ReadOnlySpan<byte> proto, ClusterBuffer into, ref RemoteLoad load)
        {
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1:                                                   // options
                        {
                            var o = r.Message();
                            bool track = false, context = false;
                            while (o.Next())
                            {
                                if (o.Field == 1) load.Shuffle = (sbyte)(o.Bool() ? 1 : 0);
                                else if (o.Field == 2) context = o.Bool();
                                else if (o.Field == 3) track = o.Bool();
                                else o.Skip();
                            }
                            load.Repeat = (sbyte)(track ? RepeatMode.Track : context ? RepeatMode.Context : RepeatMode.Off);
                            break;
                        }
                    case 2:                                                   // playback
                        {
                            var p = r.Message();
                            load.HasPlayback = true;
                            while (p.Next())
                            {
                                switch (p.Field)
                                {
                                    case 1: load.TimestampMs = (long)p.Varint(); break;
                                    case 2: load.PositionAsOfMs = (long)p.Varint(); break;
                                    case 3: { double speed = p.Double(); load.Speed = speed > 0 ? speed : 1.0; break; }
                                    case 4: load.Paused = p.Bool(); break;
                                    case 5: TransferTrack(p.Message(), into, ref load.Current); load.HasCurrent = !load.Current.Uri.IsEmpty; break;
                                    default: p.Skip(); break;
                                }
                            }
                            break;
                        }
                    case 3:                                                   // current_session
                        {
                            var session = r.Message();
                            while (session.Next())
                            {
                                if (session.Field == 2)
                                {
                                    session.Message().Fields(1, 2, out var uri, out var url);
                                    load.ContextUri = into.AddText(uri);
                                    load.ContextUrl = into.AddText(url);
                                }
                                else if (session.Field == 3) load.CurrentUid = into.AddText(session.Bytes());
                                else session.Skip();
                            }
                            break;
                        }
                    case 4:                                                   // queue
                        {
                            var q = r.Message();
                            while (q.Next())
                            {
                                if (q.Field == 1)
                                {
                                    ref var row = ref into.AddTrack();
                                    TransferTrack(q.Message(), into, ref row);
                                    if (row.Uri.IsEmpty) into.TrackCount--;
                                }
                                else if (q.Field == 2) load.IsPlayingQueue = q.Bool();
                                else q.Skip();
                            }
                            break;
                        }
                    default: r.Skip(); break;
                }
            }
        }

        /// <summary><c>TransferContextTrack{ uri=1, uid=2, gid=3, metadata=4 }</c>. A gid with no uri is spelled back as
        /// <c>spotify:track:&lt;base62&gt;</c> (0.2.9's rule), which formats with no interner.</summary>
        static void TransferTrack(ProtoReader track, ClusterBuffer into, ref ClusterTrack row)
        {
            TextRef large = default, plain = default;
            ReadOnlySpan<byte> gid = default;
            long duration = 0;
            while (track.Next())
            {
                switch (track.Field)
                {
                    case 1: row.Uri = into.AddText(track.Bytes()); break;
                    case 2: row.Uid = into.AddText(track.Bytes()); break;
                    case 3: gid = track.Bytes(); break;
                    case 4:
                        track.Message().Fields(1, 2, out var key, out var value);
                        TrackMeta(key, value, into, ref row, ref large, ref plain, ref duration);
                        break;
                    default: track.Skip(); break;
                }
            }
            if (row.Uri.IsEmpty && gid.Length == Base62.GidBytes)
            {
                Span<byte> uri = stackalloc byte[EntityId.MaxGidTextChars];
                row.Uri = into.AddText(uri[..EntityId.ForGid(EntityKind.Track, gid).Format(uri)]);
            }
            if (row.Image.IsEmpty) row.Image = large.IsEmpty ? plain : large;
            row.DurationMs = duration;
        }

        /// <summary>One <c>metadata</c> map entry → the display picks a remote track carries so a row paints before any
        /// fetch: title, artist, album, their uris, the cover (xlarge → large → plain) and the duration. Shared by the
        /// cluster's <c>ProvidedTrack</c>, the transfer's track and the context-resolve JSON.</summary>
        static void TrackMeta(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ClusterBuffer into, ref ClusterTrack row,
                              ref TextRef large, ref TextRef plain, ref long duration)
        {
            if (key.IsEmpty || value.IsEmpty) return;
            if (Is(key, "title")) row.Title = into.AddText(value);
            else if (Is(key, "artist_name")) row.ArtistName = into.AddText(value);
            else if (Is(key, "album_title")) row.AlbumTitle = into.AddText(value);
            else if (Is(key, "artist_uri") && row.ArtistUri.IsEmpty) row.ArtistUri = into.AddText(value);
            else if (Is(key, "album_uri") && row.AlbumUri.IsEmpty) row.AlbumUri = into.AddText(value);
            else if (Is(key, "image_xlarge_url")) row.Image = into.AddText(value);
            else if (Is(key, "image_large_url")) large = into.AddText(value);
            else if (Is(key, "image_url")) plain = into.AddText(value);
            else if (Is(key, "duration")) duration = Math.Max(0, Number(value));
        }

        // ── context-resolve / autoplay ───────────────────────────────────────────────────────────────────────────────

        /// <summary>One resolved context page (or an autoplay answer): its uri, the next page's url, the sort it was
        /// resolved under, and the RANGE of <see cref="ClusterBuffer"/> tracks it produced. An INFINITE context
        /// (<c>:station:</c>, <c>:radio:</c>, <c>:autoplay</c>) is flagged: the host never pages one eagerly (0.2.9
        /// <c>ContextResolver</c>).</summary>
        public struct ContextPage
        {
            public TextRef Uri, NextPageUrl, SortingCriteria;
            public int TrackStart, TrackCount;
            public bool Infinite;
        }

        /// <summary><c>/context-resolve/v1/&lt;uri&gt;</c> (and the <c>autoplay</c> / <c>autopodcast</c> answer) → a
        /// <see cref="ContextPage"/> over <paramref name="into"/> (0.2.9 <c>ContextJson.Parse</c>). Tracks come off the root
        /// <c>tracks</c> and every <c>pages[].tracks</c>, in order; a track without a uri is dropped; the FIRST non-empty
        /// <c>next_page_url</c> wins; <c>metadata["sorting.criteria"]</c> is kept. A track's missing <c>provider</c> stays
        /// empty — the consumer reads that as <c>"context"</c> (or <c>"autoplay"</c> for an autoplay answer) rather than
        /// this fold writing the same word into the arena per row.</summary>
        public static ContextPage ContextResolve(ReadOnlySpan<byte> json, ClusterBuffer into)
        {
            var page = default(ContextPage);
            page.TrackStart = into.TrackCount;
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return page;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); page.Uri = Text(ref r, into); }
                else if (r.ValueTextEquals("next_page_url"u8)) { r.Read(); if (page.NextPageUrl.IsEmpty) page.NextPageUrl = Text(ref r, into); }
                else if (r.ValueTextEquals("tracks"u8)) JsonTracks(ref r, into);
                else if (r.ValueTextEquals("metadata"u8))
                {
                    for (int m = Fields(ref r); Next(ref r, m);)
                    {
                        if (r.ValueTextEquals("sorting.criteria"u8)) { r.Read(); page.SortingCriteria = Text(ref r, into); }
                        else SkipValue(ref r);
                    }
                }
                else if (r.ValueTextEquals("pages"u8) && EnterArray(ref r))
                {
                    for (int pages = r.CurrentDepth; Element(ref r, pages);)
                        for (int p = r.CurrentDepth; Next(ref r, p);)
                        {
                            if (r.ValueTextEquals("tracks"u8)) JsonTracks(ref r, into);
                            else if (r.ValueTextEquals("next_page_url"u8)) { r.Read(); if (page.NextPageUrl.IsEmpty) page.NextPageUrl = Text(ref r, into); }
                            else SkipValue(ref r);
                        }
                }
                else SkipValue(ref r);
            }
            page.TrackCount = into.TrackCount - page.TrackStart;
            var uri = into.Utf8(page.Uri);
            page.Infinite = uri.IndexOf(":station:"u8) >= 0 || uri.IndexOf(":radio:"u8) >= 0 || uri.IndexOf(":autoplay"u8) >= 0;
            return page;
        }

        /// <summary>A JSON <c>tracks</c> array (the reader on its property) → rows in the buffer.</summary>
        static void JsonTracks(ref Utf8JsonReader r, ClusterBuffer into)
        {
            if (!EnterArray(ref r)) return;
            for (int list = r.CurrentDepth; Element(ref r, list);)
            {
                ref var row = ref into.AddTrack();
                TextRef large = default, plain = default;
                long duration = 0;
                for (int t = r.CurrentDepth; Next(ref r, t);)
                {
                    if (r.ValueTextEquals("uri"u8)) { r.Read(); row.Uri = Text(ref r, into); }
                    else if (r.ValueTextEquals("uid"u8)) { r.Read(); row.Uid = Text(ref r, into); }
                    else if (r.ValueTextEquals("provider"u8)) { r.Read(); row.Provider = Text(ref r, into); }
                    else if (r.ValueTextEquals("metadata"u8))
                    {
                        // The same picks TrackMeta makes off a protobuf map, matched on the property name in place.
                        for (int m = Fields(ref r); Next(ref r, m);)
                        {
                            if (r.ValueTextEquals("title"u8)) { r.Read(); row.Title = Text(ref r, into); }
                            else if (r.ValueTextEquals("artist_name"u8)) { r.Read(); row.ArtistName = Text(ref r, into); }
                            else if (r.ValueTextEquals("album_title"u8)) { r.Read(); row.AlbumTitle = Text(ref r, into); }
                            else if (r.ValueTextEquals("artist_uri"u8)) { r.Read(); if (row.ArtistUri.IsEmpty) row.ArtistUri = Text(ref r, into); }
                            else if (r.ValueTextEquals("album_uri"u8)) { r.Read(); if (row.AlbumUri.IsEmpty) row.AlbumUri = Text(ref r, into); }
                            else if (r.ValueTextEquals("image_xlarge_url"u8)) { r.Read(); row.Image = Text(ref r, into); }
                            else if (r.ValueTextEquals("image_large_url"u8)) { r.Read(); large = Text(ref r, into); }
                            else if (r.ValueTextEquals("image_url"u8)) { r.Read(); plain = Text(ref r, into); }
                            else if (r.ValueTextEquals("duration"u8)) { r.Read(); duration = Math.Max(0, Num(ref r)); }
                            else SkipValue(ref r);
                        }
                    }
                    else SkipValue(ref r);
                }
                if (row.Uri.IsEmpty) { into.TrackCount--; continue; }
                if (row.Image.IsEmpty) row.Image = large.IsEmpty ? plain : large;
                row.DurationMs = duration;
            }
        }

        // ── the radio seed (G-251) ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>The <c>/inspiredby-mix/v2/seed_to_playlist</c> answer → the radio playlist's uri, or null when the seed
        /// has none (0.2.9 <c>LiveContextResolver.ResolveRadioSeedAsync</c>):
        /// <c>{ "total": 1, "mediaItems": [ { "uri": "spotify:playlist:…" } ] }</c> answers <c>mediaItems[0].uri</c>; an
        /// empty <c>mediaItems</c>, a non-playlist uri, and a body that is not JSON all answer null. One string per user
        /// action, on the api thread; a forward-only pass, no document.</summary>
        public static string? RadioPlaylistUri(ReadOnlySpan<byte> json)
        {
            try
            {
                var r = new Utf8JsonReader(json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
                for (int root = r.CurrentDepth; Next(ref r, root);)
                {
                    if (!r.ValueTextEquals("mediaItems"u8)) { SkipValue(ref r); continue; }
                    if (!EnterArray(ref r)) return null;
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                        for (int item = r.CurrentDepth; Next(ref r, item);)
                        {
                            if (!r.ValueTextEquals("uri"u8)) { SkipValue(ref r); continue; }
                            r.Read();
                            if (r.TokenType != JsonTokenType.String) continue;
                            string? uri = r.GetString();
                            if (uri is not null && uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) return uri;
                        }
                    return null;
                }
                return null;
            }
            catch (JsonException) { return null; }
        }

        /// <summary>The JSON string the reader is on, into the buffer's arena (unescaped), or empty.</summary>
        static TextRef Text(ref Utf8JsonReader r, ClusterBuffer into)
        {
            if (r.TokenType != JsonTokenType.String) return default;
            if (!r.ValueIsEscaped && !r.HasValueSequence) return into.AddText(r.ValueSpan);
            int max = r.HasValueSequence ? (int)r.ValueSequence.Length : r.ValueSpan.Length;
            Span<byte> buffer = max <= 512 ? stackalloc byte[512] : new byte[max];
            return into.AddText(buffer[..r.CopyString(buffer)]);
        }

        // ── the autoplay request ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The <c>POST /context-resolve/v1/autoplay</c> (and <c>/autopodcast</c>) body:
        /// <c>AutoplayContextRequest{ context_uri=1, recent_track_uri=2[], is_video=3 }</c> (autoplay.proto) — FULL uris,
        /// newest first, <c>is_video</c> false (0.2.9). <paramref name="recent"/> must be catalog (gid-form) tracks: they
        /// format with no interner, so this runs on the api thread. Returns the bytes written into
        /// <paramref name="into"/> (size it at 64 + 64 per recent track).</summary>
        public static int AutoplayRequest(ReadOnlySpan<byte> contextUri, ReadOnlySpan<EntityId> recent, Span<byte> into)
        {
            var w = new ProtoWriter(into);
            w.Utf8(1, contextUri);
            for (int i = 0; i < recent.Length; i++)
                if (recent[i].Form == EntityForm.Gid) w.Id(2, recent[i]);
            return w.Length;
        }
    }
}
