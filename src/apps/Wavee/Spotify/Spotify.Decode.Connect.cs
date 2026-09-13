// ── Spotify/Spotify.Decode.Connect.cs ───────────────────────────────────────────────────────────────────────────
// the cluster fold, the remote-command decode and the PutState encoder (plan §4.10, D19; §9's encode half
// landed in Wave 3 by owner G, against the field map this file's stub wrote down)
//
// Role: CORE
// Owner: E
// Wave: 2
// Budget: 520 lines
// Spec: plan §4.6 / §4.10 — a named partial of Spotify.Decode.cs, declared when the decoder passed its 1,600 budget
//
// The Connect half of the decoder, split out of `Spotify.Decode.cs` under §5's rule that a file 30% past its budget
// gets a named partial rather than a second type. Everything here is the same CORE contract as its parent file: pure
// over a span, no allocation after warm-up, no live table, no interner.
//
// It is the DECODE half only. `Spotify.Connect.cs` (SHELL, owner F) owns the dealer subscription, the debounce, the
// PutState round trip and the reply; this file owns "these bytes mean this", and Wave 3's `Playback` folds the value.

using System.Buffers.Binary;

using FluentGpu.Foundation;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // ── 8. connect: the cluster and the remote command (§4.10) ───────────────────────────────────────────────────

        public enum ClusterOrigin : byte { Push, PutResponse }
        public enum RepeatMode : byte { Off, Context, Track }
        public enum DeviceKind : byte { ThisDevice, Computer, Phone, Speaker, Tv }

        /// <summary>One device in the roster the picker paints. Ids and names are opaque strings the catalog has no
        /// row for, so they live in the <see cref="ClusterBuffer"/>'s arena exactly as staged text does.</summary>
        public struct ClusterDevice
        {
            public TextRef Id, Name;
            public DeviceKind Kind;
            public int Volume;
            public bool IsActive;
        }

        /// <summary>A `ProvidedTrack`: the remote's own account of what is playing. Its uri is an identity the
        /// catalog CAN hold, but the title/artist/cover ride along so a transfer paints before any fetch lands.</summary>
        public struct ClusterTrack
        {
            public TextRef Uri, Uid, Provider, Title, ArtistName, ArtistUri, AlbumTitle, AlbumUri, Image;
            public long DurationMs;
        }

        /// <summary>Where a cluster's variable-length halves live. The same shape as <see cref="Staging"/> and for
        /// the same reason (P8/C10): a dealer frame arrives on the dealer's own thread, the decode may not allocate,
        /// and the result crosses to the UI thread — so the rows go in a pooled buffer and the delta carries RANGES.
        ///
        /// <para><b>This is the one place this file diverges from plan §4.6's signature.</b> The plan writes
        /// <c>ClusterDelta Cluster(ReadOnlySpan&lt;byte&gt;)</c>, and a value type cannot carry a roster of N devices
        /// and two track lists without allocating one. Rent a buffer, decode into it, post the delta, return the
        /// buffer after the fold — the discipline the staging arena already establishes.</para></summary>
        public sealed class ClusterBuffer
        {
            static readonly Stack<ClusterBuffer> s_pool = new();

            byte[] _text = new byte[2048];
            int _textLength;
            ClusterDevice[] _devices = new ClusterDevice[8];
            ClusterTrack[] _tracks = new ClusterTrack[32];

            public int DeviceCount, TrackCount;

            public static ClusterBuffer Rent()
            {
                lock (s_pool) { if (s_pool.Count > 0) return s_pool.Pop(); }
                return new ClusterBuffer();
            }

            /// <summary>Give it back AFTER the fold has read it. Never hold a <see cref="TextRef"/> past this.</summary>
            public static void Return(ClusterBuffer b)
            {
                b.Reset();
                lock (s_pool) { if (s_pool.Count < 4) s_pool.Push(b); }
            }

            public void Reset() { _textLength = 0; DeviceCount = 0; TrackCount = 0; }

            public TextRef AddText(ReadOnlySpan<byte> utf8)
            {
                if (utf8.IsEmpty) return default;
                if (_textLength + utf8.Length > _text.Length)
                    Array.Resize(ref _text, Math.Max(_textLength + utf8.Length, _text.Length * 2));
                utf8.CopyTo(_text.AsSpan(_textLength));
                var r = new TextRef(_textLength, utf8.Length);
                _textLength += utf8.Length;
                return r;
            }

            public ReadOnlySpan<byte> Utf8(TextRef r) => _text.AsSpan(r.Offset, r.Length);

            public ref ClusterDevice AddDevice()
            {
                if (DeviceCount == _devices.Length) Array.Resize(ref _devices, _devices.Length * 2);
                ref var d = ref _devices[DeviceCount++];
                d = default;
                return ref d;
            }

            public ref ClusterTrack AddTrack()
            {
                if (TrackCount == _tracks.Length) Array.Resize(ref _tracks, _tracks.Length * 2);
                ref var t = ref _tracks[TrackCount++];
                t = default;
                return ref t;
            }

            public ReadOnlySpan<ClusterDevice> Devices(int start, int length) => _devices.AsSpan(start, length);
            public ReadOnlySpan<ClusterTrack> Tracks(int start, int length) => _tracks.AsSpan(start, length);
        }

        /// <summary>What a cluster carries — the value Wave 3's `Playback` folds (plan §4.10, D19). Deliberately NOT
        /// a `Playback.State`: this is the remote's claim, stamped with the two clocks (the device's own
        /// <see cref="TimestampMs"/> and the server's <see cref="ServerTimestampMs"/>) and the origin that lets the
        /// ownership fold tell a PUSH from the echo of our own PutState (C5).</summary>
        public struct ClusterDelta
        {
            public TextRef ActiveDeviceId, ContextUri, QueueRevision;
            public bool HasTrack;
            public ClusterTrack Track;
            public bool IsPlaying, IsPaused, IsBuffering;
            public bool NoPrev, NoNext, NoSeek;
            public bool Shuffling;
            public RepeatMode Repeat;
            public long PositionAsOfMs, TimestampMs, ServerTimestampMs, DurationMs, ActiveStartedPlayingAt;
            public double PlaybackSpeed;
            public int OurVolume, ActiveVolume;
            public int DeviceStart, DeviceCount;
            public int NextStart, NextCount, PrevStart, PrevCount;
            public ClusterOrigin Origin;
            public uint PutMsgId;
            public int UpdateReason;
        }

        /// <summary>A `ClusterUpdate` (the dealer's `hm://connect-state/v1/cluster` push) → the delta plus the update
        /// reason. `Cluster` itself is the PutState response's shape.</summary>
        public static ClusterDelta ClusterUpdate(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> ourDeviceId, ClusterBuffer into)
        {
            var r = new ProtoReader(proto);
            ReadOnlySpan<byte> cluster = default;
            int reason = 0;
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: cluster = r.Bytes(); break;
                    case 2: reason = r.Int32(); break;
                    default: r.Skip(); break;
                }
            }
            var delta = Cluster(cluster, ourDeviceId, into);
            delta.Origin = ClusterOrigin.Push;
            delta.UpdateReason = reason;
            return delta;
        }

        /// <summary>`connectstate.Cluster` → <see cref="ClusterDelta"/>, ported from `ClusterMapper.Map`.
        ///
        /// <para>Two rules from the port that are easy to lose: the volume slider follows the ACTIVE device and falls
        /// back to ours only when nobody is active; and a restriction is a restriction when its reason list is
        /// NON-EMPTY, never when the field is merely present.</para></summary>
        public static ClusterDelta Cluster(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> ourDeviceId, ClusterBuffer into)
        {
            var delta = default(ClusterDelta);
            delta.PlaybackSpeed = 1.0;
            delta.OurVolume = -1;
            delta.ActiveVolume = -1;
            delta.DeviceStart = into.DeviceCount;
            if (proto.IsEmpty) return delta;

            var r = new ProtoReader(proto);
            ReadOnlySpan<byte> playerState = default;

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 2: delta.ActiveDeviceId = into.AddText(r.Bytes()); break;
                    case 3: playerState = r.Bytes(); break;
                    case 4:                                        // map<string, DeviceInfo> { key = 1, value = 2 }
                        {
                            r.Message().Fields(1, 2, out var key, out var info);
                            Device(key, info, ourDeviceId, into);
                            break;
                        }
                    case 9: delta.ServerTimestampMs = (long)r.Varint(); break;
                    case 11: delta.ActiveStartedPlayingAt = (long)r.Varint(); break;
                    default: r.Skip(); break;
                }
            }
            delta.DeviceCount = into.DeviceCount - delta.DeviceStart;

            // The slider follows the ACTIVE device; ours is the fallback when nobody is active (`ClusterMapper`).
            var active = delta.ActiveDeviceId;
            foreach (ref readonly var d in into.Devices(delta.DeviceStart, delta.DeviceCount))
            {
                if (d.Kind == DeviceKind.ThisDevice) delta.OurVolume = d.Volume;
                if (d.IsActive || (!active.IsEmpty && into.Utf8(d.Id).SequenceEqual(into.Utf8(active))))
                    delta.ActiveVolume = d.Volume;
            }
            if (delta.ActiveVolume < 0) delta.ActiveVolume = delta.OurVolume;

            if (!playerState.IsEmpty) PlayerState(playerState, into, ref delta);
            return delta;

            static void Device(ReadOnlySpan<byte> key, ReadOnlySpan<byte> info, ReadOnlySpan<byte> ourDeviceId, ClusterBuffer into)
            {
                if (info.IsEmpty && key.IsEmpty) return;
                var d = new ProtoReader(info);
                ReadOnlySpan<byte> id = default, name = default;
                int volume = 0, type = 0;
                while (d.Next())
                {
                    switch (d.Field)
                    {
                        case 2: volume = d.Int32(); break;
                        case 3: name = d.Bytes(); break;
                        case 7: type = d.Int32(); break;
                        case 10: id = d.Bytes(); break;
                        default: d.Skip(); break;
                    }
                }
                if (id.IsEmpty) id = key;
                bool us = !ourDeviceId.IsEmpty && id.SequenceEqual(ourDeviceId);
                ref var row = ref into.AddDevice();
                row.Id = into.AddText(id);
                row.Name = into.AddText(name);
                row.Volume = volume;
                row.Kind = us ? DeviceKind.ThisDevice : KindOf(type);
            }

            // `devices.proto`'s DeviceType ordinals, folded to the five shapes the picker draws (`ClusterMapper`).
            // The ordinals are the PROTO's, read off devices.proto rather than off a C# enum spelling.
            static DeviceKind KindOf(int type) => type switch
            {
                1 or 14 => DeviceKind.Computer,                    // COMPUTER, CHROMEBOOK
                2 or 3 or 13 => DeviceKind.Phone,                  // TABLET, SMARTPHONE, SMARTWATCH
                4 or 6 or 8 or 11 or 12 => DeviceKind.Speaker,     // SPEAKER, AVR, AUDIO_DONGLE, CAST_AUDIO, AUTOMOBILE
                5 or 7 or 9 or 10 => DeviceKind.Tv,                // TV, STB, GAME_CONSOLE, CAST_VIDEO
                _ => DeviceKind.Computer,
            };
        }

        static void PlayerState(ReadOnlySpan<byte> proto, ClusterBuffer into, ref ClusterDelta delta)
        {
            var r = new ProtoReader(proto);
            // The two lists are each a contiguous run in the buffer, but the wire's order between them is the
            // serializer's business (prev_tracks is field 19, next_tracks is 20), so each records its OWN start.
            int next = 0, nextStart = -1, prev = 0, prevStart = -1;

            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: delta.TimestampMs = (long)r.Varint(); break;
                    case 2: delta.ContextUri = into.AddText(r.Bytes()); break;
                    case 7:
                        {
                            // The current track is the DELTA's own field, not a row in either list: decoded into a
                            // local so it never takes a slot in the buffer that a list would have to skip.
                            var track = default(ClusterTrack);
                            Track(r.Message(), into, ref track);
                            delta.Track = track;
                            delta.HasTrack = !track.Uri.IsEmpty;
                            break;
                        }
                    case 9: delta.PlaybackSpeed = r.Double(); break;
                    case 10: delta.PositionAsOfMs = (long)r.Varint(); break;
                    case 11: delta.DurationMs = (long)r.Varint(); break;
                    case 12: delta.IsPlaying = r.Bool(); break;
                    case 13: delta.IsPaused = r.Bool(); break;
                    case 14: delta.IsBuffering = r.Bool(); break;
                    case 16:                                       // ContextPlayerOptions
                        {
                            var o = r.Message();
                            bool repeatContext = false, repeatTrack = false;
                            while (o.Next())
                            {
                                switch (o.Field)
                                {
                                    case 1: delta.Shuffling = o.Bool(); break;
                                    case 2: repeatContext = o.Bool(); break;
                                    case 3: repeatTrack = o.Bool(); break;
                                    default: o.Skip(); break;
                                }
                            }
                            delta.Repeat = repeatTrack ? RepeatMode.Track : repeatContext ? RepeatMode.Context : RepeatMode.Off;
                            break;
                        }
                    case 17:                                       // Restrictions
                        {
                            var x = r.Message();
                            while (x.Next())
                            {
                                switch (x.Field)
                                {
                                    case 3: delta.NoSeek |= !x.Bytes().IsEmpty; break;
                                    case 6: delta.NoPrev |= !x.Bytes().IsEmpty; break;
                                    case 7: delta.NoNext |= !x.Bytes().IsEmpty; break;
                                    default: x.Skip(); break;
                                }
                            }
                            break;
                        }
                    case 19:                                       // prev_tracks
                        {
                            if (prevStart < 0) prevStart = into.TrackCount;
                            ref var t = ref into.AddTrack();
                            Track(r.Message(), into, ref t);
                            if (t.Uri.IsEmpty) into.TrackCount--;
                            else prev++;
                            break;
                        }
                    case 20:                                       // next_tracks
                        {
                            if (nextStart < 0) nextStart = into.TrackCount;
                            ref var t = ref into.AddTrack();
                            Track(r.Message(), into, ref t);
                            if (t.Uri.IsEmpty) into.TrackCount--;
                            else next++;
                            break;
                        }
                    case 24: delta.QueueRevision = into.AddText(r.Bytes()); break;
                    default: r.Skip(); break;
                }
            }

            delta.NextStart = nextStart < 0 ? 0 : nextStart;
            delta.NextCount = next;
            delta.PrevStart = prevStart < 0 ? 0 : prevStart;
            delta.PrevCount = prev;
        }

        /// <summary>A `ProvidedTrack`. The display fields live in its `metadata` map — the map the remote fills so a
        /// controller can paint a row for a track it has never fetched — and the cover falls back through
        /// xlarge → large → plain exactly as `ClusterMapper.MapTrack` does.</summary>
        static void Track(ProtoReader track, ClusterBuffer into, ref ClusterTrack row)
        {
            TextRef large = default, plain = default;
            long duration = 0;
            while (track.Next())
            {
                switch (track.Field)
                {
                    case 1: row.Uri = into.AddText(track.Bytes()); break;
                    case 2: row.Uid = into.AddText(track.Bytes()); break;
                    case 3:                                        // map<string, string> { key = 1, value = 2 }
                        {
                            track.Message().Fields(1, 2, out var key, out var value);
                            if (key.IsEmpty || value.IsEmpty) break;
                            if (Is(key, "title")) row.Title = into.AddText(value);
                            else if (Is(key, "artist_name")) row.ArtistName = into.AddText(value);
                            else if (Is(key, "album_title")) row.AlbumTitle = into.AddText(value);
                            else if (Is(key, "artist_uri") && row.ArtistUri.IsEmpty) row.ArtistUri = into.AddText(value);
                            else if (Is(key, "album_uri") && row.AlbumUri.IsEmpty) row.AlbumUri = into.AddText(value);
                            else if (Is(key, "image_xlarge_url")) row.Image = into.AddText(value);
                            else if (Is(key, "image_large_url")) large = into.AddText(value);
                            else if (Is(key, "image_url")) plain = into.AddText(value);
                            else if (Is(key, "duration")) duration = Math.Max(0, Number(value));
                            break;
                        }
                    case 6: row.Provider = into.AddText(track.Bytes()); break;
                    case 8: row.AlbumUri = into.AddText(track.Bytes()); break;
                    case 10: row.ArtistUri = into.AddText(track.Bytes()); break;
                    default: track.Skip(); break;
                }
            }
            if (row.Image.IsEmpty) row.Image = large.IsEmpty ? plain : large;
            row.DurationMs = duration;
        }

        /// <summary>The controller verbs a remote can send us (§4.10). The endpoint TEXT never leaves this file: it
        /// is folded to an ordinal here, so nothing downstream compares strings on the command path.</summary>
        public enum RemoteCmd : byte
        {
            Unknown, Play, Pause, Resume, SeekTo, SkipNext, SkipPrev,
            SetShufflingContext, SetRepeatingContext, SetRepeatingTrack,
            Transfer, AddToQueue, SetQueue, UpdateContext, SetOptions,
        }

        /// <summary>One decoded remote command — a pure value, no strings (ported from `ConnectCommand.TryParse`).
        ///
        /// <para><see cref="Ok"/> and <see cref="Kind"/> answer two different questions and 0.2.9 needed both: a
        /// garbled BODY on a KNOWN endpoint must not reply `DeviceDoesNotSupportCommand` and get the sender cached
        /// out of ever sending that endpoint again. <see cref="DedupeKey"/> folds (sender, message id, endpoint)
        /// because message ids are recycled across endpoints — without the endpoint term a fresh
        /// `set_shuffling_context` landing on an old id looked like a replay and was dropped.</para></summary>
        public readonly record struct RemoteCommand(
            RemoteCmd Kind, bool Ok, int MessageId, long SeekToMs, bool BoolArg,
            EntityId Track, ulong SenderHash, ulong SessionHash, ulong DedupeKey);

        /// <summary>The dealer REQUEST body → a <see cref="RemoteCommand"/>. `Utf8JsonReader` over the raw bytes:
        /// no `JsonDocument`, no DOM, nothing allocated on a path that runs per controller keypress.</summary>
        public static RemoteCommand ConnectCommand(ReadOnlySpan<byte> payload)
        {
            var r = new System.Text.Json.Utf8JsonReader(payload);
            int messageId = 0;
            ulong sender = 0, session = 0;
            var kind = RemoteCmd.Unknown;
            long seek = 0;
            bool boolArg = false, ok = false;
            var track = default(EntityId);

            while (r.Read())
            {
                if (r.TokenType != System.Text.Json.JsonTokenType.PropertyName) continue;
                // `message_id` is uint32 on the wire and routinely exceeds int.MaxValue. Read WIDE, then clamp —
                // narrowing it threw in 0.2.9 and the outer catch discarded the whole command.
                if (r.ValueTextEquals("message_id"u8)) { r.Read(); messageId = (int)Math.Clamp(Long(ref r), int.MinValue, int.MaxValue); }
                else if (r.ValueTextEquals("sent_by_device_id"u8)) { r.Read(); sender = Hash(ref r); }
                else if (r.ValueTextEquals("command"u8)) { r.Read(); ok = Command(ref r, ref kind, ref seek, ref boolArg, ref session, ref track); }
                else r.Skip();
            }

            ulong dedupe = Fnv(Fnv(sender, (ulong)(uint)messageId), (ulong)kind);
            return new RemoteCommand(kind, ok, messageId, seek, boolArg, track, sender, session, dedupe);

            static bool Command(ref System.Text.Json.Utf8JsonReader r, ref RemoteCmd kind, ref long seek,
                                ref bool boolArg, ref ulong session, ref EntityId track)
            {
                if (r.TokenType != System.Text.Json.JsonTokenType.StartObject) { r.Skip(); return false; }
                int depth = r.CurrentDepth;
                while (r.Read() && r.CurrentDepth > depth)
                {
                    if (r.TokenType != System.Text.Json.JsonTokenType.PropertyName || r.CurrentDepth != depth + 1) continue;
                    if (r.ValueTextEquals("endpoint"u8)) { r.Read(); kind = Endpoint(ref r); }
                    else if (r.ValueTextEquals("position"u8) || r.ValueTextEquals("value"u8))
                    {
                        r.Read();
                        if (r.TokenType is System.Text.Json.JsonTokenType.True or System.Text.Json.JsonTokenType.False)
                            boolArg = r.TokenType == System.Text.Json.JsonTokenType.True;
                        else seek = Long(ref r);
                    }
                    else if (r.ValueTextEquals("session_id"u8)) { r.Read(); session = Hash(ref r); }
                    else if (r.ValueTextEquals("track"u8))
                    {
                        r.Read();
                        if (r.TokenType != System.Text.Json.JsonTokenType.StartObject) { r.Skip(); continue; }
                        int inner = r.CurrentDepth;
                        while (r.Read() && r.CurrentDepth > inner)
                        {
                            if (r.TokenType == System.Text.Json.JsonTokenType.PropertyName && r.ValueTextEquals("uri"u8))
                            {
                                r.Read();
                                // The uri becomes a packed `EntityId` here and never a string: a controller's
                                // skip_next names the track it expects us to be on, and the fold compares identities.
                                // TryParseGid and NOT TryParse: `Parse` INTERNS for the text form, and interning is
                                // the UI thread's alone (C1) - this runs on the dealer's receive task. A controller
                                // names a CATALOG track, which is exactly the gid form; anything else stays `default`
                                // and the fold reads it as "the remote did not say which track".
                                if (r.TokenType == System.Text.Json.JsonTokenType.String && !r.HasValueSequence)
                                    EntityId.TryParseGid(r.ValueSpan, out track);
                            }
                            else if (r.TokenType == System.Text.Json.JsonTokenType.PropertyName) { r.Read(); r.Skip(); }
                        }
                    }
                    else { r.Read(); r.Skip(); }
                }
                return kind != RemoteCmd.Unknown;
            }

            static RemoteCmd Endpoint(ref System.Text.Json.Utf8JsonReader r)
            {
                if (r.TokenType != System.Text.Json.JsonTokenType.String) return RemoteCmd.Unknown;
                if (r.ValueTextEquals("play"u8)) return RemoteCmd.Play;
                if (r.ValueTextEquals("pause"u8)) return RemoteCmd.Pause;
                if (r.ValueTextEquals("resume"u8)) return RemoteCmd.Resume;
                if (r.ValueTextEquals("seek_to"u8)) return RemoteCmd.SeekTo;
                if (r.ValueTextEquals("skip_next"u8) || r.ValueTextEquals("next_track"u8)) return RemoteCmd.SkipNext;
                if (r.ValueTextEquals("skip_prev"u8)) return RemoteCmd.SkipPrev;
                if (r.ValueTextEquals("set_shuffling_context"u8)) return RemoteCmd.SetShufflingContext;
                if (r.ValueTextEquals("set_repeating_context"u8)) return RemoteCmd.SetRepeatingContext;
                if (r.ValueTextEquals("set_repeating_track"u8)) return RemoteCmd.SetRepeatingTrack;
                if (r.ValueTextEquals("transfer"u8)) return RemoteCmd.Transfer;
                if (r.ValueTextEquals("add_to_queue"u8)) return RemoteCmd.AddToQueue;
                if (r.ValueTextEquals("set_queue"u8)) return RemoteCmd.SetQueue;
                if (r.ValueTextEquals("update_context"u8)) return RemoteCmd.UpdateContext;
                if (r.ValueTextEquals("set_options"u8)) return RemoteCmd.SetOptions;
                return RemoteCmd.Unknown;
            }

            // `message_id` is uint32 on the wire and routinely exceeds int.MaxValue; `position` has been observed as
            // a JSON float from phones that serialize ms as a double. Both threw in 0.2.9 and discarded the command.
            static long Long(ref System.Text.Json.Utf8JsonReader r)
                => r.TokenType == System.Text.Json.JsonTokenType.Number
                    ? (r.TryGetInt64(out long v) ? v : (long)Math.Round(r.GetDouble()))
                    : r.TokenType == System.Text.Json.JsonTokenType.String && r.TryGetInt64(out long t) ? t : 0;

            static ulong Hash(ref System.Text.Json.Utf8JsonReader r)
                => r.TokenType == System.Text.Json.JsonTokenType.String && !r.HasValueSequence ? Fnv(14695981039346656037UL, r.ValueSpan) : 0;
        }

        static ulong Fnv(ulong seed, ReadOnlySpan<byte> bytes)
        {
            ulong h = seed;
            for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 1099511628211UL; }
            return h;
        }

        static ulong Fnv(ulong seed, ulong value)
        {
            ulong h = seed;
            for (int i = 0; i < 8; i++) { h ^= (byte)(value >> (i * 8)); h *= 1099511628211UL; }
            return h;
        }

        // ── 9. the encode half: PutState (landed in Wave 3 by owner G, per the stub's own field map) ──────────────────

        /// <summary>A minimal protobuf WRITER — the mirror of <see cref="ProtoReader"/>, and the only one in the app.
        /// It exists for one message (<see cref="PutState"/>): using the generated `PutStateRequest` would put a
        /// reflection-descriptor graph and a fresh object tree on a path that runs on every transport change.
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

            public void F64(int field, double value)
            {
                Tag(field, 1);
                BinaryPrimitives.WriteUInt64LittleEndian(_b[Length..], BitConverter.DoubleToUInt64Bits(value));
                Length += 8;
            }

            public void Utf8(int field, scoped ReadOnlySpan<byte> bytes)
            {
                if (bytes.IsEmpty) return;
                Tag(field, 2);
                Var((ulong)bytes.Length);
                bytes.CopyTo(_b[Length..]);
                Length += bytes.Length;
            }

            public void Str(int field, string value)
            {
                if (value.Length == 0) return;
                Tag(field, 2);
                int n = System.Text.Encoding.UTF8.GetByteCount(value);
                Var((ulong)n);
                Length += System.Text.Encoding.UTF8.GetBytes(value, _b[Length..]);
            }

            /// <summary>An <see cref="EntityId"/> as its canonical uri, formatted straight into the buffer — no
            /// string, no interner, no allocation (the same call the staged uri uses).</summary>
            public void Id(int field, EntityId id)
            {
                if (id.IsEmpty) return;
                Span<byte> uri = stackalloc byte[256];
                int n = id.Format(uri);
                if (n > 0) Utf8(field, uri[..n]);
            }

            /// <summary>A <c>map&lt;string,string&gt;</c> entry (key = 1, value = 2).</summary>
            public void Entry(int field, string key, string value)
            {
                int m = Open(field);
                Str(1, key);
                Str(2, value);
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

        /// <summary>`connectstate.PutStateRequest` ← the local player's snapshot. Returns the bytes written.
        ///
        /// <para>The field map the Wave-2 stub wrote down: <c>PutStateRequest{ callback_url=1, device=2{
        /// device_info=1, player_state=2, private_device_info=3 }, member_type=3, is_active=4, put_state_reason=5,
        /// message_id=6, last_command_sent_by_device_id=7, last_command_message_id=8, started_playing_at=9,
        /// has_been_playing_for_ms=11, client_side_timestamp=12, only_write_player_state=13 }</c>, with the
        /// `PlayerState` half the mirror of <see cref="PlayerState"/> above.</para>
        ///
        /// <para><b>The DeviceInfo / Capabilities constants are desktop parity and load-bearing</b> — ported verbatim
        /// from 0.2.9's `ConnectStateBuilder`, which proved them byte-exact over 24 captured desktop PUTs.
        /// <c>license = "premium"</c> is what makes this device eligible for Recently Played and play counts;
        /// <c>needs_full_player_state</c> is why we get whole cluster snapshots instead of deltas; the
        /// <c>supported_types</c> list and the five unnamed capability bits are anti-fraud surface. Changing one of
        /// them silently breaks a feature or gets the account throttled, so none of them is a preference.</para>
        ///
        /// <para><b>While we are not the active device the PLAYER half is empty</b> — the snapshot says so, and that
        /// is librespot's rule: publish an idle player_state plus our own volume rather than mirroring the owner's
        /// row. 0.2.9 mirroring it is how a phone's track was once announced as ours.</para>
        ///
        /// <para>Allocates nothing: every identity is formatted into the caller's buffer through
        /// <c>EntityId.Format(Span&lt;byte&gt;)</c> and every constant is a UTF-8 literal.</para></summary>
        public static int PutState(in Playback.Snapshot snap, Span<byte> into)
        {
            var w = new ProtoWriter(into);

            int device = w.Open(2);
            {
                int info = w.Open(1);                          // Device.device_info
                w.B(1, true);                                  // can_play
                w.UAlways(2, (ulong)Math.Clamp(snap.Volume, 0, 65535));
                w.Str(3, snap.Device.DeviceName);
                Capabilities(ref w, 4);
                w.Str(6, snap.Device.SoftwareVersion);
                w.UAlways(7, 1);                               // device_type = COMPUTER
                w.Str(9, snap.Device.SpircVersion);
                w.Str(10, snap.Device.DeviceId);
                w.Str(13, snap.Device.ClientId);
                w.Str(14, "spotify");                          // brand
                w.Str(15, "PC laptop");                        // model
                w.Entry(16, "debug_level", "1");               // metadata_map
                w.Entry(16, "tier1_port", "0");
                w.Str(23, "premium");                          // license — Recently-Played eligibility
                w.Close(info);

                int player = w.Open(2);                        // Device.player_state
                w.U(1, (ulong)Math.Max(0, snap.TimestampMs));
                w.Id(2, snap.Context);
                if (snap.HasTrack)
                {
                    int track = w.Open(7);
                    w.Id(1, snap.Track);
                    w.Str(2, snap.Uid);
                    w.Entry(3, "track_player", Playback.MediaSwitch.TrackPlayer(snap.Kind));
                    w.Str(6, "context");                       // provider
                    w.Close(track);
                }
                w.F64(9, 1.0);                                 // playback_speed
                w.U(10, (ulong)Math.Max(0, snap.PositionAsOfMs));
                w.U(11, (ulong)Math.Max(0, snap.DurationMs));
                w.B(12, snap.IsPlaying);
                w.B(13, snap.IsPaused);
                w.B(14, snap.IsBuffering);
                int options = w.Open(16);                      // ContextPlayerOptions
                w.B(1, snap.Shuffling);
                w.B(2, snap.Repeat == RepeatMode.Context);
                w.B(3, snap.Repeat == RepeatMode.Track);
                w.Close(options);
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
            w.U(9, (ulong)Math.Max(0, snap.StartedPlayingAtMs));
            w.U(11, (ulong)Math.Max(0, snap.HasBeenPlayingForMs));
            w.U(12, (ulong)Math.Max(0, snap.ClientTimestampMs));
            return w.Length;
        }

        /// <summary>`DeviceInfo.capabilities` — CONSTANT, so it is written the same way every time. The 15
        /// `supported_types` and the five unnamed bits are the captured desktop client's, verbatim.</summary>
        static void Capabilities(ref ProtoWriter w, int field)
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
            w.B(1, true); w.B(2, true); w.B(3, true);
            w.Close(hifi);
            w.B(29, true);                                     // supports_dj
            w.UAlways(30, 4);                                  // supported_audio_quality = VERY_HIGH (320 kbps OGG)
            w.B(33, true); w.B(34, true); w.B(35, true); w.B(36, true); w.B(38, true);   // unnamed, 24/24 captures
            w.Close(c);
        }
    }
}
