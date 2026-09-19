// ── Spotify/Spotify.Decode.Connect.cs ───────────────────────────────────────────────────────────────────────────
// the cluster fold and the remote-command decode (plan §4.10, D19)
//
// Role: CORE
// Owner: E
// Wave: 2
// Budget: 520 lines
// Spec: plan §4.6 / §4.10 — a named partial of Spotify.Decode.cs, declared when the decoder passed its 1,600 budget
// Named partial: `Spotify.Decode.PutState.cs` (gap batch R4-1) — §9, the encode half (the ProtoWriter and PutState with
// its 0.2.9 parity half), moved out when the parity half would have put this file 70 % over its budget
//
// The Connect half of the decoder, split out of `Spotify.Decode.cs` under §5's rule that a file 30% past its budget
// gets a named partial rather than a second type. Everything here is the same CORE contract as its parent file: pure
// over a span, no allocation after warm-up, no live table, no interner.
//
// It is the DECODE half only. `Spotify.Connect.cs` (SHELL, owner F) owns the dealer subscription, the debounce, the
// PutState round trip and the reply; this file owns "these bytes mean this", and Wave 3's `Playback` folds the value.

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
            /// <summary>A push's <c>devices_that_changed</c>, comma-joined in the arena (empty for a put-state response) —
            /// the Connect diagnostics page's row, never a routing input.</summary>
            public TextRef ChangedDevices;
        }

        /// <summary>A `ClusterUpdate` (the dealer's `hm://connect-state/v1/cluster` push) → the delta plus the update
        /// reason. `Cluster` itself is the PutState response's shape.</summary>
        public static ClusterDelta ClusterUpdate(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> ourDeviceId, ClusterBuffer into)
        {
            var r = new ProtoReader(proto);
            ReadOnlySpan<byte> cluster = default;
            int reason = 0;
            TextRef changed = default;
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: cluster = r.Bytes(); break;
                    case 2: reason = r.Int32(); break;
                    case 4:                                        // devices_that_changed, joined contiguously in the arena
                        {
                            ReadOnlySpan<byte> id = r.Bytes();
                            if (id.IsEmpty) break;
                            if (!changed.IsEmpty) into.AddText(","u8);
                            TextRef added = into.AddText(id);
                            changed = changed.IsEmpty ? added : new TextRef(changed.Offset, added.Offset + added.Length - changed.Offset);
                            break;
                        }
                    default: r.Skip(); break;
                }
            }
            var delta = Cluster(cluster, ourDeviceId, into);
            delta.Origin = ClusterOrigin.Push;
            delta.UpdateReason = reason;
            delta.ChangedDevices = changed;
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
                            TrackMeta(key, value, into, ref row, ref large, ref plain, ref duration);   // Spotify.Decode.Remote.cs
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
            /// <summary>OUTBOUND only: "play THIS context from THIS track" to the device that owns playback — the desktop
            /// `play` envelope with a context uri and a skip_to (0.2.9 <c>OutboundEnvelope.Play</c>). The inbound `play`
            /// decodes as <see cref="Play"/>; a bare outbound <see cref="Play"/> is a resume, which is why a local row click
            /// while a phone owned playback used to do nothing visible (2026-09-16).</summary>
            PlayContext,
            SetPlaybackSpeed,
        }

        /// <summary>One decoded remote command — a pure value, no strings (ported from `ConnectCommand.TryParse`).
        ///
        /// <para><see cref="Ok"/> and <see cref="Kind"/> answer two different questions and 0.2.9 needed both: a
        /// garbled BODY on a KNOWN endpoint must not reply `DeviceDoesNotSupportCommand` and get the sender cached
        /// out of ever sending that endpoint again. <see cref="DedupeKey"/> folds (sender, message id, endpoint)
        /// because message ids are recycled across endpoints — without the endpoint term a fresh
        /// `set_shuffling_context` landing on an old id looked like a replay and was dropped.</para>
        /// <para>It is the VERB. What a <see cref="RemoteCmd.Play"/> or <see cref="RemoteCmd.Transfer"/> asks to play — the
        /// context, the track to start at, the position, the transferred queue — is <see cref="ConnectLoad"/>'s, decoded
        /// from the same body into a pooled <see cref="ClusterBuffer"/> (Spotify.Decode.Remote.cs, G-071). The bodies of
        /// <see cref="RemoteCmd.SetOptions"/> (<see cref="OptionVerbs"/>) and of <see cref="RemoteCmd.SetQueue"/> /
        /// <see cref="RemoteCmd.UpdateContext"/> (<see cref="ConnectQueue"/>) are Spotify.Decode.Commands.cs's (G-074).</para></summary>
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

        // ── 9. the encode half — ProtoWriter, PutState and its parity half — lives in Spotify.Decode.PutState.cs ──────────
    }
}
