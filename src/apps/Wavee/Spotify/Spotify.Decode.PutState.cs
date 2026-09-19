// ── Spotify/Spotify.Decode.PutState.cs ─────────────────────────────────────────────────────────────────────────────
// the encode half: the protobuf writer and connect-state's PutStateRequest, with the parity half 0.2.9's
// ConnectStateBuilder sent — the prev/next tracks and their metadata, the ids, the revision, the restrictions, the
// signals, the modes, the quality, #38 (gap batch R4-1; §9 of Spotify.Decode.Connect.cs moved here)
//
// Role: CORE
// Owner: E
// Wave: 3 (the encoder, owner G) + gap batch R4-1
// Budget: 700 lines
// Spec: gap register G-240 (thin PutState), D37 (media.type only on the video host); 0.2.9
//       `SpotifyLive/ConnectStateBuilder.cs` (proven byte-for-byte over 24 captured desktop PUTs) and its
//       `ConnectStateBuilderTests`; `Protos/player.proto`, `Protos/connect.proto` — a named partial of Spotify.Decode.cs
//
// PURE over a snapshot: the host captured every value on the UI thread (`Playback.Host.Wire.cs`), the queue window is
// immutable, and the encoder writes into the caller's buffer — no table, no interner for a catalog id, no allocation.
// A warm encode of a 50 + 50 window allocates nothing (PutStateWireTests), which is why every constant below is a UTF-8
// literal, every number is formatted on the stack and every nested message is written in place (`ProtoWriter.Open`).
//
//   PutStateRequest ─ device(2) ─ device_info(1): constants · volume · name · capabilities · is_private_session(11) ·
//                   │                             license · audio_output_device_info(24)
//                   │           ─ player_state(2): timestamp — and, while we hold a track: context uri/url ·
//                   │             context_restrictions {} · play_origin · index · track (+ metadata) · playback_id ·
//                   │             speed (0 while paused) · position · duration · is_playing (true while paused) ·
//                   │             is_paused · is_buffering · options (+ modes) · restrictions · suppressions {} ·
//                   │             prev_tracks ≤ 50 · next_tracks ≤ 50 · context_metadata · session_id · queue_revision ·
//                   │             playback_quality · signals · session_command_id · #38
//                   │           ─ private_device_info(3)
//                   ─ member_type · is_active · reason · message_id · last_command_sent_by_device_id(7) ·
//                     last_command_message_id(8) · started_playing_at · has_been_playing_for_ms · client_side_timestamp

using System.Buffers.Binary;
using System.Buffers.Text;

using FluentGpu.Foundation;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // ── 9. the encode half ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A minimal protobuf WRITER — the mirror of <see cref="ProtoReader"/>, and the only one in the app.
        /// It exists for the PutState body (and the autoplay request beside it): using the generated `PutStateRequest`
        /// would put a reflection-descriptor graph and a fresh object tree on a path that runs on every transport change.
        ///
        /// <para><b>Nested messages.</b> A length-delimited field needs its length BEFORE its body, so
        /// <see cref="Open"/> reserves three bytes (enough for 2 MiB) and <see cref="Close"/> writes the real varint
        /// and slides the body left if it turned out shorter. <c>Span.CopyTo</c> is a memmove, so the overlap is
        /// safe.</para></summary>
        public ref struct ProtoWriter
        {
            readonly Span<byte> _b;

            public ProtoWriter(Span<byte> into) { _b = into; Length = 0; }

            /// <summary>How many bytes have been written.</summary>
            public int Length { get; private set; }

            public void Var(ulong v)
            {
                while (v >= 0x80) { _b[Length++] = (byte)(v | 0x80); v >>= 7; }
                _b[Length++] = (byte)v;
            }

            public void Tag(int field, int wire) => Var((ulong)((field << 3) | wire));

            /// <summary>A varint field. Zero is proto3's default and is NOT written — the wire is smaller and a
            /// reader answers the same value.</summary>
            public void U(int field, ulong value) { if (value != 0) { Tag(field, 0); Var(value); } }

            /// <summary>A varint field written even when it is zero — for the handful desktop always emits.</summary>
            public void UAlways(int field, ulong value) { Tag(field, 0); Var(value); }

            public void B(int field, bool value) { if (value) { Tag(field, 0); Var(1); } }

            public void F32(int field, float value)
            {
                Tag(field, 5);
                BinaryPrimitives.WriteUInt32LittleEndian(_b[Length..], BitConverter.SingleToUInt32Bits(value));
                Length += 4;
            }

            public void F64(int field, double value)
            {
                Tag(field, 1);
                BinaryPrimitives.WriteUInt64LittleEndian(_b[Length..], BitConverter.DoubleToUInt64Bits(value));
                Length += 8;
            }

            public void Utf8(int field, scoped ReadOnlySpan<byte> bytes)
            {
                if (bytes.IsEmpty) return;
                Utf8Always(field, bytes);
            }

            /// <summary>A bytes field written even when empty — an <c>optional string</c> with explicit presence (<c>12 00</c>).</summary>
            public void Utf8Always(int field, scoped ReadOnlySpan<byte> bytes)
            {
                Tag(field, 2);
                Var((ulong)bytes.Length);
                bytes.CopyTo(_b[Length..]);
                Length += bytes.Length;
            }

            /// <summary>Two runs as ONE string field (<c>"context://" + uri</c>), with no copy in between.</summary>
            public void Concat(int field, scoped ReadOnlySpan<byte> head, scoped ReadOnlySpan<byte> tail)
            {
                Tag(field, 2);
                Var((ulong)(head.Length + tail.Length));
                head.CopyTo(_b[Length..]);
                Length += head.Length;
                tail.CopyTo(_b[Length..]);
                Length += tail.Length;
            }

            public void Str(int field, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                StrAlways(field, value);
            }

            public void StrAlways(int field, string? value)
            {
                Tag(field, 2);
                if (string.IsNullOrEmpty(value)) { Var(0); return; }
                int n = System.Text.Encoding.UTF8.GetByteCount(value);
                Var((ulong)n);
                Length += System.Text.Encoding.UTF8.GetBytes(value, _b[Length..]);
            }

            /// <summary>An <see cref="EntityId"/> as its canonical uri, formatted straight into the buffer — no
            /// string, no interner for a catalog id, no allocation (the same call the staged uri uses).</summary>
            public void Id(int field, EntityId id)
            {
                if (id.IsEmpty) return;
                Span<byte> uri = stackalloc byte[UriBytes];
                int n = FormatId(id, uri);
                if (n > 0) Utf8(field, uri[..n]);
            }

            /// <summary>An empty-but-present message (<c>tag 00</c>) — the submessages desktop always sends with no content.</summary>
            public void Empty(int field) { Tag(field, 2); Var(0); }

            /// <summary>A <c>map&lt;string,string&gt;</c> entry (key = 1, value = 2).</summary>
            public void Entry(int field, string key, string value)
            {
                int m = Open(field);
                Str(1, key);
                Str(2, value);
                Close(m);
            }

            /// <summary>The same entry from UTF-8 — a literal key and a value formatted on the stack.</summary>
            public void Entry(int field, scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
            {
                int m = Open(field);
                Utf8(1, key);
                Utf8(2, value);
                Close(m);
            }

            /// <summary>A literal key and an interned value; nothing is written for an empty value (0.2.9's AddIfMissing).</summary>
            public void EntryText(int field, scoped ReadOnlySpan<byte> key, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                int m = Open(field);
                Utf8(1, key);
                Str(2, value);
                Close(m);
            }

            /// <summary>A literal key and an identity's uri; nothing is written for an empty identity.</summary>
            public void EntryId(int field, scoped ReadOnlySpan<byte> key, EntityId id)
            {
                if (id.IsEmpty) return;
                int m = Open(field);
                Utf8(1, key);
                Id(2, id);
                Close(m);
            }

            /// <summary>Begin a nested message; pass the returned marker to <see cref="Close"/>.</summary>
            public int Open(int field)
            {
                Tag(field, 2);
                Length += 3;                                   // reserved for the length varint
                return Length;
            }

            public void Close(int start)
            {
                int len = Length - start;
                int need = len < 0x80 ? 1 : len < 0x4000 ? 2 : 3;
                if (need != 3)
                {
                    _b.Slice(start, len).CopyTo(_b[(start - 3 + need)..]);   // memmove: the overlap is safe
                    Length = start - 3 + need + len;
                }
                int p = start - 3;
                ulong v = (ulong)len;
                while (v >= 0x80) { _b[p++] = (byte)(v | 0x80); v >>= 7; }
                _b[p] = (byte)v;
            }
        }

        /// <summary>The largest uri the encoder formats; a text-form id (a local file, a module row) longer than this is
        /// left out rather than overflowing the stack buffer.</summary>
        const int UriBytes = 768;

        /// <summary><c>play_origin.feature_version</c> — 0.2.9's captured xpui snapshot token, verbatim.</summary>
        public const string FeatureVersion = "xpui-snapshot_2026-07-01_1782890476915_7b5cc0c";

        /// <summary>`connectstate.PutStateRequest` ← the local player's snapshot. Returns the bytes written; size
        /// <paramref name="into"/> with <see cref="PutStateCapacity"/>.
        ///
        /// <para><b>The DeviceInfo / Capabilities constants are desktop parity and load-bearing</b> — ported verbatim
        /// from 0.2.9's `ConnectStateBuilder`, which proved them byte-exact over 24 captured desktop PUTs.
        /// <c>license = "premium"</c> is what makes this device eligible for Recently Played and play counts;
        /// <c>needs_full_player_state</c> is why we get whole cluster snapshots instead of deltas; the
        /// <c>supported_types</c> list and the five unnamed capability bits are anti-fraud surface. Changing one of
        /// them silently breaks a feature or gets the account throttled, so none of them is a preference.</para>
        ///
        /// <para><b>While we hold no track the PLAYER half is its timestamp alone</b> — 0.2.9's idle player_state, and
        /// librespot's rule for a device that is not the active one: our own volume, never a mirror of the owner's row.</para>
        ///
        /// <para><b>The parity half (G-240).</b> Controllers read the queue off <c>prev_tracks</c>/<c>next_tracks</c>, the
        /// "Playing from" line off <c>play_origin</c> and the context metadata, a pause off <c>is_playing</c> staying true
        /// with speed 0 and the <c>already_paused</c> restriction, and the video switch off <c>signals</c> plus
        /// <c>disallow_signals</c>. <c>media.type = "video"</c> is written only while the VIDEO HOST plays the row (D37,
        /// the captures' rule); a video-capable row played as audio offers the switch through <c>associated_video_id</c>
        /// and the <c>switch-to-video</c> signal instead.</para></summary>
        /// <param name="frameToUnixMs">Unix ms minus frame-clock ms, both sampled at one instant by the caller; 0 when the
        /// snapshot's <c>StartedPlayingAtMs</c> is already a unix stamp (a test's).</param>
        public static int PutState(in Playback.Snapshot snap, Span<byte> into, long frameToUnixMs = 0)
        {
            Span<byte> scratch = stackalloc byte[48];
            Span<byte> contextUtf8 = stackalloc byte[UriBytes];
            int contextLength = snap.HasTrack ? FormatId(snap.Context, contextUtf8) : 0;
            var w = new ProtoWriter(into);

            int device = w.Open(2);
            {
                DeviceInfo(ref w, in snap);
                int player = w.Open(2);                        // Device.player_state
                w.U(1, (ulong)Math.Max(0, snap.TimestampMs));
                if (snap.HasTrack) PlayerState(ref w, in snap, contextUtf8[..contextLength], scratch);
                w.Close(player);
                int priv = w.Open(3);                          // Device.private_device_info
                w.Str(1, snap.Device.Platform);
                w.Close(priv);
            }
            w.Close(device);

            w.UAlways(3, 2);                                   // member_type = CONNECT_STATE
            w.B(4, snap.IsActive);
            w.U(5, (ulong)snap.Reason);                        // put_state_reason — the PROTO's own ordinal
            w.U(6, snap.MessageId);
            w.Str(7, snap.Wire.CommandSender);                 // last_command_sent_by_device_id (G-240, aged with 8)
            w.U(8, snap.LastCommandMessageId);                 // last_command_message_id (G-073, G-246)
            w.U(9, StartedPlayingAt(snap.StartedPlayingAtMs, frameToUnixMs));
            w.U(11, (ulong)Math.Max(0, snap.HasBeenPlayingForMs));
            w.U(12, (ulong)Math.Max(0, snap.ClientTimestampMs));
            return w.Length;
        }

        /// <summary>A buffer size <see cref="PutState"/> cannot overrun for <paramref name="snap"/>: a fixed allowance for
        /// the device half and the player constants, plus a per-row bound over the window's rows (their strings at three
        /// bytes a character, the image four times over, two context uris). A 50 + 50 window of ordinary rows is ~300 KB —
        /// the caller rents it. PURE, allocation-free.</summary>
        public static int PutStateCapacity(in Playback.Snapshot snap)
        {
            int n = 16 * 1024 + 2 * UriBytes
                    + 3 * ((snap.Device.DeviceName?.Length ?? 0) + (snap.Uid?.Length ?? 0) + snap.VideoGid.Length
                           + snap.Wire.OutputDevice.Length + snap.Wire.CommandSender.Length + (snap.Device.Platform?.Length ?? 0));
            if (snap.Wire.Origin is { } origin)
            {
                n += 3 * (origin.Feature.Length + origin.Version.Length + origin.View.Length + origin.ExternalReferrer.Length
                    + origin.Referrer.Length + origin.Device.Length + origin.CommandId.Length) + 256;
                foreach (var entry in origin.Metadata) n += 3 * (entry.Key.Length + entry.Value.Length) + 32;
            }
            if (snap.Wire.Window is not { } window) return n;
            n += RowCapacity(in window.Current);
            foreach (ref readonly Playback.WireRow row in window.Prev) n += RowCapacity(in row);
            foreach (ref readonly Playback.WireRow row in window.Next) n += RowCapacity(in row);
            return n;

            static int RowCapacity(in Playback.WireRow row)
                => 2 * UriBytes + 2048
                   + 3 * (row.Title.Length + row.ArtistName.Length + row.AlbumTitle.Length + row.UidText.Length)
                   + 4 * (14 + 3 * row.Image.Length);
        }

        static void DeviceInfo(ref ProtoWriter w, in Playback.Snapshot snap)
        {
            int info = w.Open(1);                              // Device.device_info
            w.B(1, true);                                      // can_play
            w.UAlways(2, (ulong)Math.Clamp(snap.Volume, 0, 65535));
            w.Str(3, snap.Device.DeviceName);
            Capabilities(ref w, 4, snap.Device.SupportsLossless);
            w.Str(6, snap.Device.SoftwareVersion);
            w.UAlways(7, 1);                                   // device_type = COMPUTER
            w.Str(9, snap.Device.SpircVersion);
            w.Str(10, snap.Device.DeviceId);
            w.B(11, snap.Wire.PrivateSession);                 // is_private_session — the setting, on the wire (G-240)
            w.Str(13, snap.Device.ClientId);
            w.Str(14, "spotify");                              // brand
            w.Str(15, "PC laptop");                            // model
            w.Entry(16, "debug_level", "1");                   // metadata_map
            w.Entry(16, "tier1_port", "0");
            w.Str(23, "premium");                              // license — Recently-Played eligibility
            int output = w.Open(24);                           // audio_output_device_info, 24/24 captures
            w.UAlways(1, snap.Wire.OutputType);                // actual endpoint type
            w.StrAlways(2, snap.Wire.OutputDevice);            // the OS endpoint's name, "" when unknown (explicit)
            w.UAlways(5, 3);                                   // unnamed, always 3
            w.Close(output);
            w.Close(info);
        }

        static void PlayerState(ref ProtoWriter w, in Playback.Snapshot snap, scoped ReadOnlySpan<byte> context, scoped Span<byte> scratch)
        {
            Playback.WireWindow? window = snap.Wire.Window;
            ref readonly Playback.WireIds ids = ref snap.Wire.Ids;
            bool playing = snap.IsPlaying || snap.IsPaused || snap.IsBuffering;   // desktop: paused is the engaged transport
            bool video = snap.Kind == Playback.PlayableKind.Video;
            bool offer = snap.VideoGid.Length > 0;
            int contextIndex = window?.ContextIndex ?? 0;

            w.Utf8(2, context);                                // context_uri
            if (!context.IsEmpty) w.Concat(3, "context://"u8, context);
            w.Empty(4);                                        // context_restrictions {}
            int origin = w.Open(5);                            // play_origin
            ReadOnlySpan<byte> feature = FeatureOf(context);
            if (snap.Wire.Origin is { Feature.Length: > 0 } carried)
            {
                w.Str(1, carried.Feature); w.Str(2, carried.Version); w.Str(3, carried.View);
                w.Str(4, carried.ExternalReferrer); w.Str(5, carried.Referrer); w.Str(6, carried.Device);
            }
            else { w.Utf8(1, feature); w.Utf8(5, feature); }
            w.Close(origin);
            if (contextIndex >= 0)
            {
                int index = w.Open(6);
                w.U(2, (ulong)contextIndex);
                w.Close(index);
            }
            CurrentTrack(ref w, in snap, context, contextIndex, scratch);
            Hex(ref w, 8, ids.PlaybackId, scratch);            // playback_id
            if (snap.IsPlaying && !snap.IsPaused && !snap.IsBuffering) w.F64(9, snap.Wire.PlaybackRate);                 // playback_speed — 0 (unwritten) while paused
            w.U(10, (ulong)Math.Max(0, snap.PositionAsOfMs));
            w.U(11, (ulong)Math.Max(0, snap.DurationMs));
            w.B(12, playing);
            w.B(13, snap.IsPaused);
            w.B(14, snap.IsBuffering);

            int options = w.Open(16);                          // ContextPlayerOptions
            w.B(1, snap.Shuffling);
            w.B(2, snap.Repeat == RepeatMode.Context);
            w.B(3, snap.Repeat == RepeatMode.Track);
            if (snap.Track.Kind == EntityKind.Episode) w.F32(4, snap.Wire.PlaybackRate);
            Mode(ref w, "context_enhancement"u8, "NONE"u8);    // the three modes, constant in 24/24
            Mode(ref w, "media"u8, default);
            Mode(ref w, "jam"u8, "off"u8);
            w.Close(options);

            int restrictions = w.Open(17);
            if (snap.IsBuffering) w.Utf8(3, "not_playing_media"u8);
            if (snap.IsPaused)
            {
                w.Utf8(1, "already_paused"u8);
                if (contextIndex <= 0 && (window is null || window.Prev.IsEmpty)) w.Utf8(6, "no_prev_track"u8);
            }
            else if (playing) w.Utf8(2, "not_paused"u8);
            if (snap.Track.Kind != EntityKind.Episode) w.Utf8(25, "not_supported_by_content_type"u8);   // setting playback speed
            if (snap.Track.Kind != EntityKind.Episode && context.IndexOf(":playlist:"u8) < 0)           // Enhance is a playlist feature
            {
                int modes = w.Open(28);
                w.Utf8(1, "context_enhancement"u8);
                int values = w.Open(2);                        // ModeRestrictions
                int entry = w.Open(1);
                w.Utf8(1, "RECOMMENDATION"u8);
                int reasons = w.Open(2);
                w.Utf8(1, "not_supported_by_content_type"u8);
                w.Close(reasons);
                w.Close(entry);
                w.Close(values);
                w.Close(modes);
            }
            // The mode we HOST is never offered; the other one is offered in `signals` when the gid is known, else disallowed.
            DisallowSignal(ref w, video ? "switch-to-video"u8 : "switch-to-audio"u8);
            if (!offer && snap.Track.Kind != EntityKind.Episode) DisallowSignal(ref w, video ? "switch-to-audio"u8 : "switch-to-video"u8);
            w.Utf8(31, "already_set"u8);                       // unnamed, always exactly this
            w.Close(restrictions);

            w.Empty(18);                                       // suppressions {}
            if (window is not null)
            {
                foreach (ref readonly Playback.WireRow row in window.Prev) QueueRow(ref w, 19, in row, context, -1, in ids, scratch);
                int view = Math.Max(0, contextIndex + 1);
                foreach (ref readonly Playback.WireRow row in window.Next)
                {
                    bool numbered = row.Provider == QueueProvider.Autoplay || (contextIndex >= 0 && row.Provider != QueueProvider.Queue);   // context and autoplay rows number the context
                    QueueRow(ref w, 20, in row, context, numbered ? view : -1, in ids, scratch);
                    if (numbered) view++;
                }
                if (window.Next.Length > 0 && window.Next[^1].Provider == QueueProvider.Autoplay)
                {
                    int delimiter = w.Open(20);
                    w.Utf8(1, "spotify:delimiter"u8); w.Utf8(2, "delimiter0"u8);
                    w.Entry(3, "hidden"u8, "true"u8);
                    w.Entry(3, "actions.advancing_past_track"u8, "pause"u8);
                    w.Close(delimiter);
                }
            }
            if (snap.Wire.Origin is { } carriedOrigin)
                foreach (var entry in carriedOrigin.Metadata) w.Entry(21, entry.Key, entry.Value);
            w.Entry(21, "player.arch"u8, "2"u8);               // context_metadata
            if (ids.SessionId != UInt128.Zero) { Base62.Encode(ids.SessionId, scratch); w.Utf8(23, scratch[..Base62.GidChars]); }            // session_id
            if (snap.Wire.QueueRevision != 0 && Utf8Formatter.TryFormat(snap.Wire.QueueRevision, scratch, out int digits))
                w.Utf8(24, scratch[..digits]);                 // queue_revision, as its digits
            int quality = w.Open(32);                          // playback_quality: high · cached_file · high · available · hifi off
            w.U(1, 3); w.U(2, 4); w.U(3, 3); w.B(4, true); w.U(5, 1);
            w.Close(quality);
            if (offer) w.Utf8(33, video ? "switch-to-audio"u8 : "switch-to-video"u8);   // the offer goes FIRST (7/7 captures)
            w.Utf8(33, "interact"u8);
            w.Utf8(33, "automix-preview"u8);
            w.Utf8(33, "speed-preview"u8);
            w.Utf8(33, "stop-speed-preview"u8);
            if (snap.Wire.Origin is { CommandId.Length: > 0 } commandOrigin) w.Str(35, commandOrigin.CommandId);
            else Hex(ref w, 35, ids.SessionCommandId, scratch);     // session_command_id
            int unknown = w.Open(38);                          // unnamed #38 = {1: ""}
            w.Utf8Always(1, default);
            w.Close(unknown);
        }

        /// <summary>The row on the deck: the snapshot's uri and uid, the window's display picks, the live media kind.</summary>
        static void CurrentTrack(ref ProtoWriter w, in Playback.Snapshot snap, scoped ReadOnlySpan<byte> context, int contextIndex,
            scoped Span<byte> scratch)
        {
            Playback.WireRow row = snap.Wire.Window is { } window ? window.Current : default;
            QueueProvider provider = snap.Wire.Window is null ? QueueProvider.Context : row.Provider;
            bool video = snap.Kind == Playback.PlayableKind.Video;
            int track = w.Open(7);
            w.Id(1, snap.Track);
            w.Str(2, snap.Uid);
            Head(ref w, in row, provider, context, video);
            // The video offer: the current row carries its 32-hex video gid whether we host audio or video (0.2.9).
            if (snap.VideoGid.Length > 0) w.Entry(3, "associated_video_id", snap.VideoGid);
            w.Entry(3, "track_player"u8, video ? "video"u8 : "audio"u8);
            if (HostsVideo(in snap))
            {
                w.Entry(3, "media.type"u8, "video"u8);
                if (snap.VideoGid.Length > 0) w.Entry(3, "media.manifest_id", snap.VideoGid);   // the manifest id IS the video gid
                if (Utf8Formatter.TryFormat(Math.Max(0L, snap.PositionAsOfMs), scratch, out int length))
                    w.Entry(3, "media.start_position"u8, scratch[..length]);
            }
            Tail(ref w, provider, video, contextIndex, in snap.Wire.Ids, scratch);
            w.Utf8(6, ProviderWord(provider));
            w.Close(track);
        }

        /// <summary>A prev/next row. Never the current media, so its player is audio and it carries no video keys.</summary>
        static void QueueRow(ref ProtoWriter w, int field, in Playback.WireRow row, scoped ReadOnlySpan<byte> context, int viewIndex,
            in Playback.WireIds ids, scoped Span<byte> scratch)
        {
            int m = w.Open(field);
            w.Id(1, row.Id);
            if (row.UidText.Length > 0) w.Str(2, row.UidText);
            else if (row.ItemId != 0) w.Utf8(2, scratch[..Hex16(row.ItemId, scratch)]);
            Head(ref w, in row, row.Provider, context, isVideo: false);
            w.Entry(3, "track_player"u8, "audio"u8);
            Tail(ref w, row.Provider, isVideo: false, viewIndex, in ids, scratch);
            w.Utf8(6, ProviderWord(row.Provider));
            w.Close(m);
        }

        /// <summary>A row's display metadata, in 0.2.9's order: title, artist, album, their uris, the context (a context row
        /// only; <c>entity_uri</c> never for a video), the cover four times over, then the provenance marks.</summary>
        static void Head(ref ProtoWriter w, in Playback.WireRow row, QueueProvider provider, scoped ReadOnlySpan<byte> context, bool isVideo)
        {
            w.EntryText(3, "title"u8, row.Title);
            w.EntryText(3, row.Id.Kind == EntityKind.Episode ? "author_name"u8 : "artist_name"u8, row.ArtistName);
            w.EntryText(3, "album_title"u8, row.AlbumTitle);
            w.EntryId(3, "album_uri"u8, row.AlbumId);
            w.EntryId(3, "artist_uri"u8, row.ArtistId);
            if (!context.IsEmpty && provider == QueueProvider.Context)
            {
                w.Entry(3, "context_uri"u8, context);
                if (!isVideo) w.Entry(3, "entity_uri"u8, context);
            }
            if (row.Image.Length > 0)
            {
                Span<byte> image = stackalloc byte[UriBytes];
                int n = ImageUri(row.Image, image);
                if (n > 0)
                {
                    w.Entry(3, "image_url"u8, image[..n]);
                    w.Entry(3, "image_small_url"u8, image[..n]);
                    w.Entry(3, "image_large_url"u8, image[..n]);
                    w.Entry(3, "image_xlarge_url"u8, image[..n]);
                }
            }
            if (provider == QueueProvider.Queue) w.Entry(3, "is_queued"u8, "true"u8);
            else if (provider == QueueProvider.Autoplay) w.Entry(3, "autoplay.is_autoplay"u8, "true"u8);
            else
            {
                w.Entry(3, "actions.skipping_prev_past_track"u8, "resume"u8);
                w.Entry(3, "actions.skipping_next_past_track"u8, "resume"u8);
            }
        }

        /// <summary>The session's interaction and page-instance ids, and a context row's view index and iteration.</summary>
        static void Tail(ref ProtoWriter w, QueueProvider provider, bool isVideo, int viewIndex, in Playback.WireIds ids,
            scoped Span<byte> scratch)
        {
            if (ids.InteractionId != UInt128.Zero) w.Entry(3, "interaction_id"u8, scratch[..Dashed(ids.InteractionId, scratch)]);
            if (ids.PageInstanceId != UInt128.Zero) w.Entry(3, "page_instance_id"u8, scratch[..Dashed(ids.PageInstanceId, scratch)]);
            if (isVideo || provider != QueueProvider.Context) return;
            if (viewIndex >= 0 && Utf8Formatter.TryFormat(viewIndex, scratch, out int n)) w.Entry(3, "view_index"u8, scratch[..n]);
            w.Entry(3, "iteration"u8, "0"u8);
        }

        static void Mode(ref ProtoWriter w, scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
        {
            int m = w.Open(5);
            w.Utf8(1, key);
            w.Utf8Always(2, value);                            // explicit even when empty: `media` is `12 00` on the wire
            w.Close(m);
        }

        static void DisallowSignal(ref ProtoWriter w, scoped ReadOnlySpan<byte> signal)
        {
            int entry = w.Open(29);                            // disallow_signals map entry
            w.Utf8(1, signal);
            int reasons = w.Open(2);
            w.Utf8(1, "no_associated_track"u8);
            w.Close(reasons);
            w.Close(entry);
        }

        /// <summary>A 128-bit id as 32 lowercase hex; nothing for zero (not minted).</summary>
        static void Hex(ref ProtoWriter w, int field, UInt128 value, scoped Span<byte> scratch)
        {
            if (value == UInt128.Zero) return;
            w.Utf8(field, scratch[..Hex32(value, scratch)]);
        }

        static int Hex32(UInt128 value, Span<byte> into)
        {
            for (int i = 31; i >= 0; i--) { into[i] = HexDigit((int)(value & 0xF)); value >>= 4; }
            return 32;
        }

        /// <summary>8-4-4-4-12, the dashed uuid form 0.2.9 stamped on interaction and page-instance ids.</summary>
        static int Dashed(UInt128 value, Span<byte> into)
        {
            for (int i = 35; i >= 0; i--)
            {
                if (i is 8 or 13 or 18 or 23) { into[i] = (byte)'-'; continue; }
                into[i] = HexDigit((int)(value & 0xF));
                value >>= 4;
            }
            return 36;
        }

        /// <summary>A packed 16-hex queue uid back to its text (<c>Playback.QueueUid</c>'s shape).</summary>
        static int Hex16(ulong value, Span<byte> into)
        {
            for (int i = 15; i >= 0; i--) { into[i] = HexDigit((int)(value & 0xF)); value >>= 4; }
            return 16;
        }

        static byte HexDigit(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);

        /// <summary>An identity's uri into <paramref name="into"/>, or 0 when there is none or it would not fit.</summary>
        static int FormatId(EntityId id, Span<byte> into)
        {
            if (id.IsEmpty) return 0;
            if (id.Form != EntityForm.Gid && id.FormattedLength * 3 > into.Length) return 0;
            return id.Format(into);
        }

        /// <summary>The cover as the wire names it: a CDN url or a bare image id becomes <c>spotify:image:&lt;id&gt;</c>
        /// (0.2.9's SpotifyImage); any other url is kept. 0 when it would not fit.</summary>
        static int ImageUri(string image, Span<byte> into)
        {
            const string Cdn = "https://i.scdn.co/image/";
            ReadOnlySpan<char> tail = image;
            bool spotify = true;
            if (image.StartsWith(Cdn, StringComparison.Ordinal)) tail = tail[Cdn.Length..];
            else if (tail.IndexOfAny(':', '/') >= 0) spotify = false;
            ReadOnlySpan<byte> prefix = spotify ? "spotify:image:"u8 : default;
            int need = prefix.Length + System.Text.Encoding.UTF8.GetByteCount(tail);
            if (tail.IsEmpty || need > into.Length) return 0;
            prefix.CopyTo(into);
            return prefix.Length + System.Text.Encoding.UTF8.GetBytes(tail, into[prefix.Length..]);
        }

        /// <summary><c>play_origin.feature_identifier</c> from the context uri (0.2.9's FeatureOf, without the metadata
        /// liked-songs probe the snapshot does not carry — the collection uri answers it). PURE.</summary>
        public static ReadOnlySpan<byte> FeatureOf(scoped ReadOnlySpan<byte> contextUri)
            => contextUri.IndexOf(":collection"u8) >= 0 ? "your_library"u8
             : contextUri.IndexOf(":album:"u8) >= 0 ? "album"u8
             : contextUri.IndexOf(":artist"u8) >= 0 ? "artist"u8
             : contextUri.IndexOf(":playlist:"u8) >= 0 ? "playlist"u8
             : contextUri.IndexOf(":episode:"u8) >= 0 ? "home"u8
             : "harmony"u8;

        static ReadOnlySpan<byte> ProviderWord(QueueProvider provider) => provider switch
        {
            QueueProvider.Queue => "queue"u8,
            QueueProvider.Autoplay => "autoplay"u8,
            _ => "context"u8,
        };

        /// <summary><c>started_playing_at</c> on the wire: the reducer's FRAME-clock stamp moved onto the unix clock by
        /// <paramref name="frameToUnixMs"/> (unix now − frame now, one instant). The difference of two running clocks is a
        /// constant, so the conversion is exact however long a debounce held the snapshot. 0 — not written — for a stamp
        /// that was never made. PURE.</summary>
        public static ulong StartedPlayingAt(long startedFrameMs, long frameToUnixMs)
            => startedFrameMs <= 0 ? 0UL : (ulong)Math.Max(0L, startedFrameMs + frameToUnixMs);

        /// <summary>Is the current row playing on the VIDEO host (not merely video-capable)? The media.* keys say so (D37).</summary>
        static bool HostsVideo(in Playback.Snapshot snap) => snap.HasVideo && snap.Kind == Playback.PlayableKind.Video;

        /// <summary>`DeviceInfo.capabilities` — CONSTANT, so it is written the same way every time. The 15
        /// `supported_types` and the five unnamed bits are the captured desktop client's, verbatim.</summary>
        static void Capabilities(ref ProtoWriter w, int field, bool lossless)
        {
            int c = w.Open(field);
            w.B(2, true);                                      // can_be_player
            w.B(5, true);                                      // gaia_eq_connect_id
            w.B(6, true);                                      // supports_logout
            w.B(7, true);                                      // is_observable
            w.UAlways(8, 64);                                  // volume_steps
            w.Utf8(9, "audio/ad"u8); w.Utf8(9, "audio/audio"u8); w.Utf8(9, "audio/episode"u8);
            w.Utf8(9, "audio/episode+track"u8); w.Utf8(9, "audio/interruption"u8); w.Utf8(9, "audio/local"u8);
            w.Utf8(9, "audio/media"u8); w.Utf8(9, "audio/podcast-chapter"u8); w.Utf8(9, "audio/track"u8);
            w.Utf8(9, "audio/user-highlight"u8); w.Utf8(9, "video/ad"u8); w.Utf8(9, "video/episode"u8);
            w.Utf8(9, "video/podcast-chapter"u8); w.Utf8(9, "video/track"u8); w.Utf8(9, "video/user-highlight"u8);
            w.B(10, true);                                     // command_acks
            w.B(15, true);                                     // supports_playlist_v2
            w.B(16, true);                                     // is_controllable
            w.B(17, true);                                     // supports_external_episodes
            w.B(18, true);                                     // supports_set_backend_metadata
            w.B(19, true);                                     // supports_transfer_command
            w.B(20, true);                                     // supports_command_request
            w.B(22, true);                                     // needs_full_player_state — whole snapshots, not deltas
            w.B(23, true);                                     // supports_gzip_pushes
            w.B(25, true);                                     // supports_set_options_command
            int hifi = w.Open(26);                             // supports_hifi
            w.B(1, lossless); w.B(2, lossless); w.B(3, lossless);
            w.Close(hifi);
            w.UAlways(30, lossless ? 5UL : 4UL);                // HIFI only when this runtime can open lossless files
            w.B(33, true); w.B(34, true); w.B(35, true); w.B(36, true); w.B(38, true);   // unnamed, 24/24 captures
            w.Close(c);
        }
    }
}
