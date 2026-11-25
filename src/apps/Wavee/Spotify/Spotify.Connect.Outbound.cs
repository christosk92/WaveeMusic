// ── Spotify/Spotify.Connect.Outbound.cs ─────────────────────────────────────────────────────────────────────────────
// commands to ANOTHER device: transfer, a player verb, volume — and the queue writes a foreign owner is forwarded,
// add_to_queue and set_queue (gap batch R4-1, G-248; §4 of Spotify.Connect.cs moved here)
//
// Role: SHELL (the sends) + pure body writers
// Owner: F
// Wave: 2 (§4) + gap batch R4-1
// Budget: 300 lines
// Spec: gap register G-248; 0.2.9 `Backend/OutboundEnvelope.cs` (AddToQueue, SetQueue — the desktop envelope shapes its
//       OutboundEnvelopeTests pin) and `Backend/PlaybackController.cs` (the set_queue splice) — a named partial of
//       Spotify.Connect.cs, declared when the queue forwards would have put the glue 60 % over its 400-line budget
//
// Three ROUTES, not one: a transfer moves the cluster's active device, a player command is a verb for the device that
// already has it, and volume is its own PUT with a protobuf body. All three go through `RequestKind.Custom` rather than
// D's `ConnectState*` kinds, because the captured desktop client sends a header tuple those kinds do not carry (form
// content-type + gzip + connection id on the command route, a protobuf PUT on volume).
//
// THE QUEUE FORWARDS (G-248). While another device owns playback a local "Add to queue" / "Play next" is a COMMAND to it:
//
//   one track appended  ─▶ add_to_queue { track { uri, uid: "", metadata: {} }, options, logging_params }
//   several, play next  ─▶ set_queue { queue_revision (the OWNER's, a bare uint64), prev_tracks (echoed),
//                                      next_tracks (the owner's rows, ours spliced in among its queued rows), options, … }
//
// The bodies are PURE writers over an IBufferWriter, so the envelope is a unit test (SpotifyConnectOutboundTests); the
// sends block and run on an api thread (`Playback.Host.Wire.cs` calls them through `Api.Run`).

using System.Buffers;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Connect
    {
        // ── 4. out: commands to another device ───────────────────────────────────────────────────────────────────────

        const HeaderSet CommandHeaders = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
            | HeaderSet.AcceptLanguage | HeaderSet.ContentForm | HeaderSet.GzipBody | HeaderSet.ConnectionId;

        /// <summary>The transfer POST: the form content type, NOT gzipped, and the bare <c>Accept: protobuf</c> (the official
        /// client's, 3 of 3 captured transfers).</summary>
        const HeaderSet TransferHeaders = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
            | HeaderSet.AcceptLanguage | HeaderSet.AcceptBareProtobuf | HeaderSet.ContentForm | HeaderSet.ConnectionId;

        /// <summary>The volume PUT: a protobuf body, not gzipped, with the connection id and <c>Accept-Language</c> (all.saz 2243).</summary>
        const HeaderSet VolumeHeaders = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.AcceptLanguage
            | HeaderSet.ContentProtobuf | HeaderSet.ConnectionId;

        const string PlayerCommandRoute = "/connect-state/v1/player/command/from/";
        public const string TransferRoute = "/connect-state/v1/connect/transfer/from/";
        public const string WakeDevicesRoute = "/connect-state/v1/cluster/wake-devices";

        /// <summary>Move playback to <paramref name="targetDeviceId"/>, from whoever owns it now. The route's <c>from</c> is
        /// OUR device id even when we are not the owner — the official client does the same in all three captured
        /// transfers, and the target is what names the device that takes over. Self-to-self is a 400 and the caller must not
        /// ask for it. <paramref name="ackId"/> is the answer's <c>{"ack_id"}</c> (null when the answer carried none), the
        /// value the cluster echoes once the command was applied.</summary>
        public static bool Transfer(string targetDeviceId, CancellationToken ct, out string? ackId)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            TransferBody(buffer, NewId(), NewId(), Guid.NewGuid().ToString());
            Span<char> path = stackalloc char[256];
            int length = CommandPath(TransferRoute, OurDeviceId, targetDeviceId, path);
            var args = new RequestArgs
            {
                Path = path[..length],
                Host = ApiHost.Spclient,
                Verb = Verb.Post,
                Headers = TransferHeaders,
                Body = buffer.WrittenSpan,
            };
            Api.Result result = Api.Send(RequestKind.Custom, args, ct);
            ackId = result.Ok ? AckIdOf(result.Bytes) : null;
            LogCommand("transfer", result.Status, ackId);
            return result.Ok;
        }

        /// <summary>The transfer body, key order as the official client writes it. PURE.</summary>
        public static void TransferBody(IBufferWriter<byte> into, string transferIntentId, string commandId, string interactionId)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("options");
            w.WriteString("restore_paused", "restore");
            w.WriteString("restore_position", "extrapolate");
            w.WriteString("restore_track", "only_current");
            w.WriteString("license", "premium");
            w.WriteEndObject();
            w.WriteString("transfer_intent_id", transferIntentId);
            w.WriteString("command_id", commandId);
            w.WriteString("interaction_id", interactionId);
            w.WriteEndObject();
        }

        /// <summary><c>{"ack_id":"…"}</c> → the id, or null for any other body. PURE.</summary>
        public static string? AckIdOf(ReadOnlySpan<byte> body)
        {
            try
            {
                var r = new Utf8JsonReader(body);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("ack_id"u8)) return r.Read() && r.TokenType == JsonTokenType.String ? r.GetString() : null;
                    r.Read();
                    r.Skip();
                }
            }
            catch (JsonException) { }
            return null;
        }

        /// <summary>The picker was opened: the official client POSTs this with an EMPTY body in the same millisecond as its
        /// picker-opened put-state, so every other device re-announces itself into the cluster (captured 3 of 3). Blocks;
        /// api threads only.</summary>
        public static bool WakeDevices(CancellationToken ct)
        {
            var args = new RequestArgs
            {
                Path = WakeDevicesRoute,
                Host = ApiHost.Spclient,
                Verb = Verb.Post,
                Headers = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.AcceptLanguage | HeaderSet.ConnectionId,
            };
            Api.Result result = Api.Send(RequestKind.Custom, args, ct);
            if (!result.Ok) Log.Warn("spotify", "connect wake-devices rejected (" + result.Status + ")");
            return result.Ok;
        }

        /// <summary>The least gap between two picker-opened announces (put-state + wake-devices): a picker that flickers open
        /// and shut must not ask the whole cluster to re-announce each time.</summary>
        public const int PickerGapMs = 5_000;

        /// <summary>May a picker-opened announce go out now, given when the last one did (0 = never)? PURE.</summary>
        public static bool PickerDue(long nowMs, long lastMs) => lastMs == 0 || nowMs < lastMs || nowMs - lastMs >= PickerGapMs;

        /// <summary><c>{prefix}{from}/to/{to}</c> with both ids escaped, written into <paramref name="into"/>; answers the
        /// length written. PURE.</summary>
        public static int CommandPath(string prefix, string fromDeviceId, string targetDeviceId, Span<char> into)
        {
            var w = new PathWriter(into);
            w.Append(prefix);
            w.AppendEscaped(fromDeviceId);
            w.Append("/to/");
            w.AppendEscaped(targetDeviceId);
            return w.Written.Length;
        }

        // ── 4b. the transport verbs, spelled as the official client sends them (remote-play plan phase 3, 2026-11-20) ──
        //
        //   pause       pause      + options{3} + logging_params + pause_origin{npb}            verycomplex3.saz 657 (1.2.96)
        //   resume      resume     + options{3} + logging_params + resume_origin{npb}           verycomplex.saz 737 (1.2.96)
        //   previous    prev_track + options{3, allow_seeking:true} + logging_params            sppp.saz 470 (1.3.10)
        //   seek        seek_to    + value + position + relative:"beginning", NO logging_params bigone.saz 1958 (1.3.10), all.saz 2233
        //   shuffle     set_options + shuffling_context + options{3} + logging_params + modes{context_enhancement:NONE}
        //                                                                                        all.saz 2044, 2183 (1.2.94)
        //   repeat      set_options + repeating_track + repeating_context + options{3} + logging_params
        //                                                                                        all.saz 2164, 2230, 2272 (1.2.94)
        //
        // Before, every one of them went through one generic writer: `skip_prev` with no options (no official client sends that
        // spelling over HTTP; without `allow_seeking` the owner is not told it may restart the row instead), pause and resume
        // with thin logging, seek WITH logging, and shuffle/repeat as `set_shuffling_context` / `set_repeating_*` (two posts for
        // one repeat change). `set_options` goes only to a device that advertises capability 25 (supports_set_options_command:
        // the iPhone and desktop 1.3.10 both do, device-capabilities.txt); any other keeps the legacy verbs (`Command`).

        /// <summary>Pause the device that holds playback (<c>pause</c>, verycomplex3.saz 657). Blocks; api threads only.</summary>
        public static bool Pause(string targetDeviceId, CancellationToken ct) => Transport(targetDeviceId, "pause", ct);

        /// <summary>Resume it (<c>resume</c>, verycomplex.saz 737). Blocks; api threads only.</summary>
        public static bool Resume(string targetDeviceId, CancellationToken ct) => Transport(targetDeviceId, "resume", ct);

        /// <summary>Previous on it (<c>prev_track</c> with <c>allow_seeking</c>, sppp.saz 470): the owner decides between restarting
        /// the row and going back, as its own button does. Blocks; api threads only.</summary>
        public static bool PrevTrack(string targetDeviceId, CancellationToken ct) => Transport(targetDeviceId, "prev_track", ct);

        static bool Transport(string targetDeviceId, string endpoint, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            TransportBody(buffer, endpoint, OurDeviceId, NewId(), NewId(), Playback.UnixNowMs());
            return PostCommand(endpoint, targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>The body of <c>pause</c>, <c>resume</c> and <c>prev_track</c>, key for key as captured: the endpoint, the three
        /// option bools (<c>prev_track</c> adds <c>allow_seeking: true</c>, sppp.saz 470), the full logging params, then the origin a
        /// pause or a resume names (<c>pause_origin</c> / <c>resume_origin</c> <c>{feature_identifier: "npb"}</c>, the now-playing
        /// bar: verycomplex3.saz 657, verycomplex.saz 737), then <c>connection_type</c> and <c>intent_id</c>. PURE.</summary>
        public static void TransportBody(IBufferWriter<byte> into, string endpoint, string deviceId, string commandId, string intentId,
            long nowMs)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", endpoint);
            QueueOptions(w, allowSeeking: endpoint == "prev_track");
            LoggingParams(w, deviceId, commandId, "", nowMs);
            string? origin = endpoint switch { "pause" => "pause_origin", "resume" => "resume_origin", _ => null };
            if (origin is not null)
            {
                w.WriteStartObject(origin);
                w.WriteString("feature_identifier", "npb");
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();
        }

        /// <summary>Seek the device that holds playback to <paramref name="positionMs"/> (<c>seek_to</c>). Blocks; api threads only.</summary>
        public static bool SeekTo(string targetDeviceId, long positionMs, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(192);
            SeekBody(buffer, positionMs, NewId());
            return PostCommand("seek_to", targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>The <c>seek_to</c> body as every captured one is (bigone.saz 1958 on 1.3.10, all.saz 2233, itsover.saz 789): the
        /// position twice (<c>value</c>, <c>position</c>), <c>relative: "beginning"</c>, and NO logging params; then
        /// <c>connection_type</c> and <c>intent_id</c>. PURE.</summary>
        public static void SeekBody(IBufferWriter<byte> into, long positionMs, string intentId)
        {
            long ms = Math.Max(0L, positionMs);
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "seek_to");
            w.WriteNumber("value", ms);
            w.WriteNumber("position", ms);
            w.WriteString("relative", "beginning");
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();
        }

        /// <summary>Shuffle on or off as ONE <c>set_options</c> (all.saz 2044). Only for a device that sets capability 25
        /// (<see cref="Decode.ClusterDevice.SupportsSetOptions"/>); the caller keeps <c>set_shuffling_context</c> for any other.
        /// Blocks; api threads only.</summary>
        public static bool SetShuffle(string targetDeviceId, bool shuffle, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            ShuffleOptionsBody(buffer, shuffle, OurDeviceId, NewId(), NewId(), Playback.UnixNowMs());
            return PostCommand("set_options", targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>A whole repeat mode as ONE <c>set_options</c> naming both flags (all.saz 2164: track off, context on; 2230: both
        /// on), so the owner never passes through a mode nobody asked for. Only for a device that sets capability 25; the caller
        /// keeps the two <c>set_repeating_*</c> posts for any other. Blocks; api threads only.</summary>
        public static bool SetRepeat(string targetDeviceId, bool repeatTrack, bool repeatContext, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            RepeatOptionsBody(buffer, repeatTrack, repeatContext, OurDeviceId, NewId(), NewId(), Playback.UnixNowMs());
            return PostCommand("set_options", targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>The shuffle <c>set_options</c> body, key for key as all.saz 2044: the endpoint, <c>shuffling_context</c>, the three
        /// option bools, the logging params, then <c>modes {context_enhancement: "NONE"}</c>; then <c>connection_type</c> and
        /// <c>intent_id</c>. PURE.</summary>
        public static void ShuffleOptionsBody(IBufferWriter<byte> into, bool shuffle, string deviceId, string commandId, string intentId,
            long nowMs)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "set_options");
            w.WriteBoolean("shuffling_context", shuffle);
            QueueOptions(w);
            LoggingParams(w, deviceId, commandId, "", nowMs);
            w.WriteStartObject("modes");
            w.WriteString("context_enhancement", "NONE");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();
        }

        /// <summary>The repeat <c>set_options</c> body, key for key as all.saz 2164: the endpoint, <c>repeating_track</c>, then
        /// <c>repeating_context</c>, the three option bools and the logging params (no <c>modes</c>); then <c>connection_type</c> and
        /// <c>intent_id</c>. PURE.</summary>
        public static void RepeatOptionsBody(IBufferWriter<byte> into, bool repeatTrack, bool repeatContext, string deviceId,
            string commandId, string intentId, long nowMs)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "set_options");
            w.WriteBoolean("repeating_track", repeatTrack);
            w.WriteBoolean("repeating_context", repeatContext);
            QueueOptions(w);
            LoggingParams(w, deviceId, commandId, "", nowMs);
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();
        }

        /// <summary>A LEGACY option verb for a device that does not advertise capability 25: <c>set_shuffling_context</c>,
        /// <c>set_repeating_context</c> or <c>set_repeating_track</c> with its one boolean <c>value</c>. No official client was seen
        /// sending these over HTTP (gap analysis 1.1); they stay only for an owner that cannot take <c>set_options</c>, in the
        /// body they always had (the value, a thin <c>logging_params</c>).</summary>
        public static bool Command(string targetDeviceId, string endpoint, bool value, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartObject("command");
                w.WriteString("endpoint", endpoint);
                w.WriteBoolean("value", value);
                w.WriteStartObject("logging_params");
                w.WriteStartArray("interaction_ids");
                w.WriteEndArray();
                w.WriteString("device_identifier", OurDeviceId);
                w.WriteString("command_id", NewId());
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteString("connection_type", "wlan");
                w.WriteString("intent_id", NewId());
                w.WriteEndObject();
            }
            return PostCommand(endpoint, targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>"Jump to this row of YOUR queue" on the device that owns playback — the desktop `next_track` envelope; with
        /// an empty <paramref name="trackUri"/> it is the plain skip button, which the official client also spells `next_track`
        /// (7 of 7 captured; the gateway rewrites it to `skip_next` for dealer delivery). Blocks; api threads only.</summary>
        public static bool NextTrack(string targetDeviceId, string? trackUri, string? trackUid, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(trackUri) && !Egress.Admits(trackUri.AsSpan())) return false;
            var buffer = new ArrayBufferWriter<byte>(512);
            NextTrackBody(buffer, trackUri, trackUid, OurDeviceId, NewId(), NewId(), Playback.UnixNowMs());
            return PostCommand("next_track", targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>The `next_track` envelope body, as the official client's queue-row click sends it: the three option
        /// bools, <c>track { uri, uid, metadata }</c> (the owner matches on uid, else uri, and advances its own queue until
        /// it reaches that row — uid and metadata always written, metadata empty: the owner has the row), the logging
        /// params, then <c>connection_type</c> and <c>intent_id</c>. A null or empty <paramref name="trackUri"/> writes no
        /// <c>track</c> at all: the plain skip button. PURE.</summary>
        public static void NextTrackBody(IBufferWriter<byte> into, string? trackUri, string? trackUid, string deviceId,
            string commandId, string intentId, long nowMs)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "next_track");
            QueueOptions(w);
            if (!string.IsNullOrEmpty(trackUri))
            {
                w.WriteStartObject("track");
                w.WriteString("uri", trackUri);
                w.WriteString("uid", trackUid ?? "");
                w.WriteStartObject("metadata");
                w.WriteEndObject();
                w.WriteEndObject();
            }
            LoggingParams(w, deviceId, commandId, "", nowMs);
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();
        }

        /// <summary>"Switch the row you play to its video / back to its audio" on the device that owns playback — the desktop's
        /// `signal` envelope (itsover.saz 772) — or "Let DJ pick" on a DJ player (<c>jump</c>, plan-dj §1.8). Blocks; api threads only.</summary>
        public static bool Signal(string targetDeviceId, Decode.PlayerSignal signal, CancellationToken ct)
        {
            if (signal == Decode.PlayerSignal.None) return false;
            var buffer = new ArrayBufferWriter<byte>(384);
            SignalBody(buffer, signal, OurDeviceId, NewId(), NewId(), Playback.UnixNowMs());
            return PostCommand("signal", targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>The `signal` envelope body, field for field as the official client sends it: endpoint, <c>signal_id</c>, the
        /// three option bools, the logging params, then <c>connection_type</c> and <c>intent_id</c>. PURE.</summary>
        public static void SignalBody(IBufferWriter<byte> into, Decode.PlayerSignal signal, string deviceId, string commandId,
            string intentId, long nowMs)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "signal");
            w.WriteString("signal_id", Decode.SignalId(signal));
            QueueOptions(w);
            LoggingParams(w, deviceId, commandId, "", nowMs);
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();
        }

        /// <summary>Volume is not a player verb: it is a PUT with a three-field `SetVolumeCommand` protobuf body,
        /// hand-encoded so this path carries no generated message for three bytes of varint. The header set is the official
        /// client's (all.saz 2243): the connection id and <c>Accept-Language</c> ride beside the protobuf content type
        /// (remote-play plan 2.3; Wavee sent neither). The caller paces it (<c>Playback.PaceVolume</c>).</summary>
        public static bool Volume(string targetDeviceId, int volume0To65535, CancellationToken ct)
        {
            Span<byte> body = stackalloc byte[16];
            int n = VolumeBody(volume0To65535, body);

            Span<char> path = stackalloc char[192];
            var w = new PathWriter(path);
            w.Append("/connect-state/v1/connect/volume/from/");
            w.AppendEscaped(OurDeviceId);
            w.Append("/to/");
            w.AppendEscaped(targetDeviceId);
            var args = new RequestArgs
            {
                Path = w.Written,
                Host = ApiHost.Spclient,
                Verb = Verb.Put,
                Headers = VolumeHeaders,
                Body = body[..n],
            };
            Api.Result result = Api.Send(RequestKind.Custom, args, ct);
            LogCommand("volume", result.Status, result.Ok ? AckIdOf(result.Bytes) : null);
            return result.Ok;
        }

        /// <summary>The `SetVolumeCommand` body, hand-encoded. PURE, and separate from the send because it is a fixed
        /// three-field wire spec that a capture pins exactly: volume 19496 is
        /// <c>08 a8 98 01 1a 00 22 04 'wlan'</c>. Returns the length written; needs 12 bytes at most.</summary>
        public static int VolumeBody(int volume0To65535, Span<byte> into)
        {
            int n = 0;
            into[n++] = 0x08;                                              // field 1, volume, varint
            uint value = (uint)Math.Clamp(volume0To65535, 0, 65535);
            while (value >= 0x80) { into[n++] = (byte)(value | 0x80); value >>= 7; }
            into[n++] = (byte)value;
            into[n++] = 0x1a; into[n++] = 0x00;                            // field 3, logging_params, empty message
            into[n++] = 0x22; into[n++] = 0x04;                            // field 4, connection_type
            into[n++] = (byte)'w'; into[n++] = (byte)'l'; into[n++] = (byte)'a'; into[n++] = (byte)'n';
            return n;
        }

        // ── 4a. the queue forwards (G-248) ───────────────────────────────────────────────────────────────────────────

        /// <summary>One row of a forwarded <c>set_queue</c>: its uri, its uid as the owner minted it ("" for a row we add),
        /// whether it sits in the user queue rather than the context's continuation, and — for a row the OWNER stated — its whole
        /// <c>ProvidedTrack</c> as the owner sent it (<c>Track</c>, null for a row we add), which the echo writes back
        /// with its own provider and metadata (remote-play plan 5.2).</summary>
        public readonly record struct QueueWireRow(string Uri, string Uid, bool Queued, byte[]? Track = null);

        /// <summary>Append one track to the owner's queue (<c>add_to_queue</c>). Blocks; api threads only.</summary>
        public static bool AddToQueue(string targetDeviceId, string trackUri, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            AddToQueueBody(buffer, trackUri, OurDeviceId, NewId(), NewId(), Guid.NewGuid().ToString(), Playback.UnixNowMs());
            return PostCommand("add_to_queue", targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>Rewrite the owner's queue (<c>set_queue</c>) — its own rows echoed back, ours spliced in. Blocks; api
        /// threads only.</summary>
        public static bool SetQueue(string targetDeviceId, ulong queueRevision, QueueWireRow[] prev, QueueWireRow[] next,
            CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(4096);
            SetQueueBody(buffer, queueRevision, prev, next, OurDeviceId, NewId(), NewId(), Guid.NewGuid().ToString(),
                Playback.UnixNowMs());
            Log.Info("spotify", "connect set_queue to the owner rows=" + next.Length + " revision=" + queueRevision);
            return PostCommand("set_queue", targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>The desktop's <c>add_to_queue</c> envelope (0.2.9 <c>OutboundEnvelope.AddToQueue</c>): the track as
        /// <c>command.track { uri, uid, metadata }</c> — uid and metadata ALWAYS written, even empty — the three option bools,
        /// the logging params, then <c>connection_type</c> and <c>intent_id</c>. PURE.</summary>
        public static void AddToQueueBody(IBufferWriter<byte> into, string trackUri, string deviceId, string commandId,
            string intentId, string interactionId, long nowMs)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "add_to_queue");
            w.WriteStartObject("track");
            w.WriteString("uri", trackUri);
            w.WriteString("uid", "");
            w.WriteStartObject("metadata");
            w.WriteEndObject();
            w.WriteEndObject();
            QueueOptions(w);
            LoggingParams(w, deviceId, commandId, interactionId, nowMs);
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();
        }

        /// <summary>The desktop's <c>set_queue</c> envelope (0.2.9 <c>OutboundEnvelope.SetQueue</c>): <c>queue_revision</c> as a
        /// BARE unsigned number (it exceeds Int64), both track lists, then the options and logging params. PURE.
        /// <para>An OWNER's row (<see cref="QueueWireRow.Track"/>) is echoed back as the owner stated it, key for key as
        /// moreptions.saz 794 and qoptions.saz 58: uri, uid, its whole metadata map, ITS provider (<c>context</c>, <c>queue</c>,
        /// <c>autoplay</c>), its removed and blocked lists, the 22 empty restriction lists, then <c>album_uri</c> and
        /// <c>artist_uri</c> when it names them (remote-play plan 5.2). A row we add is uri, <c>uid: ""</c>, metadata
        /// <c>is_queued: "true"</c> (a string), provider <c>queue</c>, empty lists (moreptions.saz 794, next_tracks #0).</para></summary>
        public static void SetQueueBody(IBufferWriter<byte> into, ulong queueRevision, ReadOnlySpan<QueueWireRow> prev,
            ReadOnlySpan<QueueWireRow> next, string deviceId, string commandId, string intentId, string interactionId, long nowMs)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "set_queue");
            w.WriteNumber("queue_revision", queueRevision);
            w.WriteStartArray("prev_tracks");
            foreach (QueueWireRow row in prev) QueueEntry(w, in row);
            w.WriteEndArray();
            w.WriteStartArray("next_tracks");
            foreach (QueueWireRow row in next) QueueEntry(w, in row);
            w.WriteEndArray();
            QueueOptions(w);
            LoggingParams(w, deviceId, commandId, interactionId, nowMs);
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();

            static void QueueEntry(Utf8JsonWriter w, in QueueWireRow row)
            {
                if (row.Track is { Length: > 0 } track) { OwnerEntry(w, in row, track); return; }
                w.WriteStartObject();
                w.WriteString("uri", row.Uri);
                w.WriteString("uid", row.Uid ?? "");
                w.WriteStartObject("metadata");
                if (row.Queued) w.WriteString("is_queued", "true");
                w.WriteEndObject();
                w.WriteString("provider", row.Queued ? "queue" : "context");
                w.WriteStartArray("removed"); w.WriteEndArray();
                w.WriteStartArray("blocked"); w.WriteEndArray();
                EmptyRestrictions(w);
                w.WriteEndObject();
            }
        }

        /// <summary>One OWNER row of a set_queue, echoed from its <c>ProvidedTrack</c> (uri 1, uid 2, metadata 3, removed 4,
        /// blocked 5, provider 6, album_uri 8, artist_uri 10): key for key as moreptions.saz 794. A metadata pair, a list entry or
        /// a uri that is not valid UTF-8 is left out rather than failing the body; a row the owner gave no provider keeps
        /// the one its queue flag implies. PURE (a cold path: one user click).</summary>
        static void OwnerEntry(Utf8JsonWriter w, in QueueWireRow row, byte[] track)
        {
            ReadOnlySpan<byte> provider = default, album = default, artist = default;
            w.WriteStartObject();
            w.WriteString("uri", row.Uri);
            w.WriteString("uid", row.Uid ?? "");
            w.WriteStartObject("metadata");
            var r = new Decode.ProtoReader(track);
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 3:
                        {
                            r.Message().Fields(1, 2, out var key, out var value);
                            if (!key.IsEmpty && System.Text.Unicode.Utf8.IsValid(key) && System.Text.Unicode.Utf8.IsValid(value)) w.WriteString(key, value);
                            break;
                        }
                    case 6: provider = r.Bytes(); break;
                    case 8: album = r.Bytes(); break;
                    case 10: artist = r.Bytes(); break;
                    default: r.Skip(); break;
                }
            }
            w.WriteEndObject();
            if (!provider.IsEmpty && System.Text.Unicode.Utf8.IsValid(provider)) w.WriteString("provider", provider);
            else w.WriteString("provider", row.Queued ? "queue" : "context");
            StringList(w, "removed", track, 4);
            StringList(w, "blocked", track, 5);
            EmptyRestrictions(w);
            if (!album.IsEmpty && System.Text.Unicode.Utf8.IsValid(album)) w.WriteString("album_uri", album);
            if (!artist.IsEmpty && System.Text.Unicode.Utf8.IsValid(artist)) w.WriteString("artist_uri", artist);
            w.WriteEndObject();

            static void StringList(Utf8JsonWriter w, string name, byte[] track, int field)
            {
                w.WriteStartArray(name);
                var r = new Decode.ProtoReader(track);
                while (r.Next())
                {
                    if (r.Field != field || r.Wire != 2) { r.Skip(); continue; }
                    ReadOnlySpan<byte> entry = r.Bytes();
                    if (System.Text.Unicode.Utf8.IsValid(entry)) w.WriteStringValue(entry);
                }
                w.WriteEndArray();
            }
        }

        /// <summary>Where a forward's rows land in the owner's <c>next_tracks</c> (0.2.9's splice): after
        /// <paramref name="slot"/> of the owner's queued rows — 0 is "play next", the head of its user queue; a slot past its
        /// queued rows is an append behind them — and always ahead of the context's continuation. PURE.</summary>
        public static QueueWireRow[] SpliceQueued(ReadOnlySpan<QueueWireRow> ownerNext, ReadOnlySpan<QueueWireRow> added, int slot)
        {
            var rows = new QueueWireRow[ownerNext.Length + added.Length];
            int n = 0, queuedSeen = 0;
            bool placed = false;
            foreach (QueueWireRow row in ownerNext)
            {
                if (!placed && (queuedSeen >= slot || !row.Queued))
                {
                    foreach (QueueWireRow add in added) rows[n++] = add with { Queued = true };
                    placed = true;
                }
                if (row.Queued) queuedSeen++;
                rows[n++] = row;
            }
            if (!placed) foreach (QueueWireRow add in added) rows[n++] = add with { Queued = true };
            return rows;
        }

        /// <summary>The three option bools every captured verb carries, all false; <paramref name="allowSeeking"/> adds
        /// <c>allow_seeking: true</c> as <c>prev_track</c> does (sppp.saz 470).</summary>
        static void QueueOptions(Utf8JsonWriter w, bool allowSeeking = false)
        {
            w.WriteStartObject("options");
            w.WriteBoolean("override_restrictions", false);
            w.WriteBoolean("only_for_local_device", false);
            w.WriteBoolean("system_initiated", false);
            if (allowSeeking) w.WriteBoolean("allow_seeking", true);
            w.WriteEndObject();
        }

        /// <summary><c>logging_params</c> as the captures carry it: received time echoing initiated time, the interaction id.
        /// <paramref name="receivedTime"/> false leaves the received time out, as every official play that names its page inline
        /// does (bigone.saz 578, 1391; all.saz 1169).</summary>
        static void LoggingParams(Utf8JsonWriter w, string deviceId, string commandId, string interactionId, long nowMs,
            bool receivedTime = true)
        {
            w.WriteStartObject("logging_params");
            w.WriteNumber("command_initiated_time", nowMs);
            if (receivedTime) w.WriteNumber("command_received_time", nowMs);
            w.WriteStartArray("page_instance_ids"); w.WriteEndArray();
            w.WriteStartArray("interaction_ids");
            if (interactionId.Length > 0) w.WriteStringValue(interactionId);
            w.WriteEndArray();
            w.WriteString("device_identifier", deviceId);
            w.WriteString("command_id", commandId);
            w.WriteEndObject();
        }

        /// <summary>The 22 <c>disallow_*_reasons</c> keys the captured set_queue carries on every entry, each an empty list.</summary>
        static readonly string[] RestrictionKeys =
        [
            "disallow_peeking_prev_reasons", "disallow_peeking_next_reasons", "disallow_skipping_prev_reasons",
            "disallow_skipping_next_reasons", "disallow_pausing_reasons", "disallow_resuming_reasons",
            "disallow_toggling_repeat_context_reasons", "disallow_toggling_repeat_track_reasons",
            "disallow_toggling_shuffle_reasons", "disallow_set_queue_reasons", "disallow_add_to_queue_reasons",
            "disallow_seeking_reasons", "disallow_interrupting_playback_reasons", "disallow_transferring_playback_reasons",
            "disallow_remote_control_reasons", "disallow_inserting_into_next_tracks_reasons",
            "disallow_inserting_into_context_tracks_reasons", "disallow_reordering_in_next_tracks_reasons",
            "disallow_reordering_in_context_tracks_reasons", "disallow_removing_from_next_tracks_reasons",
            "disallow_removing_from_context_tracks_reasons", "disallow_updating_context_reasons",
        ];

        /// <summary><c>"restrictions": { the 22 lists, each empty }</c>: what every captured set_queue row, play context and
        /// update_context context carries (moreptions.saz 794; bigone.saz 891, 605).</summary>
        static void EmptyRestrictions(Utf8JsonWriter w)
        {
            w.WriteStartObject("restrictions");
            foreach (string key in RestrictionKeys) { w.WriteStartArray(key); w.WriteEndArray(); }
            w.WriteEndObject();
        }

        /// <summary>One player verb POSTed to the owner's command route, and its one <c>connect.cmd</c> line
        /// (<see cref="LogCommand"/>). <paramref name="endpoint"/> is the verb as the body spells it, for the log only.</summary>
        static bool PostCommand(string endpoint, string targetDeviceId, ReadOnlySpan<byte> body, CancellationToken ct)
        {
            Span<char> path = stackalloc char[256];
            int length = CommandPath(PlayerCommandRoute, OurDeviceId, targetDeviceId, path);
            var args = new RequestArgs
            {
                Path = path[..length],
                Host = ApiHost.Spclient,
                Verb = Verb.Post,
                Headers = CommandHeaders,
                Body = body,
            };
            Api.Result result = Api.Send(RequestKind.Custom, args, ct);
            LogCommand(endpoint, result.Status, result.Ok ? AckIdOf(result.Bytes) : null);
            return result.Ok;
        }

        /// <summary>ONE line for every command this device sends another (remote-play plan 0.1, 2026-11-20): every player verb,
        /// the transfer and the volume PUT, with the endpoint, the status and the answer's <c>ack_id</c> (the value the next
        /// cluster echoes when the owner applied it). Before it only a refusal left a line — the `wire.call` of a failed call —
        /// so a log could not say whether a pause, a seek, a previous, a shuffle or a repeat was ever sent, or which ack a cluster
        /// answered (gap analysis 1.3). A 2xx is Info, anything else Warn; both read <c>connect.cmd</c>. The device ids stay in the
        /// route, out of the line.</summary>
        static void LogCommand(string endpoint, int status, string? ackId)
        {
            string line = "connect.cmd endpoint=" + endpoint + " status=" + status + " ack=" + (ackId ?? "-");
            if (status is >= 200 and < 300) Log.Info("spotify", line);
            else Log.Warn("spotify", line + " rejected");
        }

        static string NewId() => Guid.NewGuid().ToString("N");
    }
}
