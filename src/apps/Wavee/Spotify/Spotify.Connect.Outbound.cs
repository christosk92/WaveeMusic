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

        const HeaderSet TransferHeaders = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
            | HeaderSet.AcceptLanguage | HeaderSet.ContentForm | HeaderSet.ConnectionId;

        const string PlayerCommandRoute = "/connect-state/v1/player/command/from/";

        /// <summary>Move playback to <paramref name="targetDeviceId"/>, from whoever owns it now. Self-to-self is a
        /// 400 and the caller must not ask for it.</summary>
        public static bool Transfer(string fromDeviceId, string targetDeviceId, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartObject("options");
                w.WriteString("restore_paused", "restore");
                w.WriteString("restore_position", "extrapolate");
                w.WriteString("restore_track", "only_current");
                w.WriteString("license", "premium");
                w.WriteEndObject();
                w.WriteString("transfer_intent_id", NewId());
                w.WriteString("command_id", NewId());
                w.WriteString("interaction_id", Guid.NewGuid().ToString());
                w.WriteEndObject();
            }
            return PostCommand("/connect-state/v1/connect/transfer/from/", fromDeviceId, targetDeviceId,
                buffer.WrittenSpan, TransferHeaders, ct);
        }

        /// <summary>One verb for the device that holds playback. <paramref name="endpoint"/> is the wire spelling
        /// (`pause`, `resume`, `skip_next`, `seek_to`, `set_shuffling_context`, …) and <paramref name="value"/> is the
        /// verb's one argument, written under <paramref name="valueName"/> when that name is non-empty.</summary>
        public static bool Command(string targetDeviceId, string endpoint, string valueName, long value,
            bool flag, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartObject("command");
                w.WriteString("endpoint", endpoint);
                if (valueName.Length > 0)
                {
                    if (endpoint.StartsWith("set_", StringComparison.Ordinal)) w.WriteBoolean(valueName, flag);
                    else w.WriteNumber(valueName, value);
                }
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
            return PostCommand(PlayerCommandRoute, OurDeviceId, targetDeviceId, buffer.WrittenSpan, CommandHeaders, ct);
        }

        /// <summary>"Play this context from this row" on the device that owns playback — the desktop `play` envelope
        /// (0.2.9 <c>OutboundEnvelope.Play</c>): an opaque, URI-only context, a play_origin, `prepare_play_options` with
        /// the skip_to track and the shuffle override, and `play_options` interactive/replace/immediately.</summary>
        public static bool PlayContext(string targetDeviceId, string contextUri, string? trackUri, bool shuffle, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(768);
            PlayBody(buffer, contextUri, trackUri, shuffle, OurDeviceId, NewId(), NewId(), Playback.UnixNowMs());
            return PostCommand(PlayerCommandRoute, OurDeviceId, targetDeviceId, buffer.WrittenSpan, CommandHeaders, ct);
        }

        /// <summary>The `play` envelope body. PURE (SpotifyConnectTests). <paramref name="trackUri"/> null/empty plays the
        /// context from its head. `always_play_something` false and `license` premium as every desktop capture carries.</summary>
        public static void PlayBody(IBufferWriter<byte> into, string contextUri, string? trackUri, bool shuffle, string deviceId,
            string commandId, string intentId, long nowMs)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "play");
            w.WriteStartObject("context");
            w.WriteString("entity_uri", contextUri);
            w.WriteString("uri", contextUri);
            w.WriteString("url", "context://" + contextUri);
            w.WriteStartObject("metadata");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("play_origin");
            w.WriteString("feature_identifier", PlayFeatureOf(contextUri));
            w.WriteString("feature_version", "harmony");
            w.WriteString("referrer_identifier", "harmony");
            w.WriteEndObject();
            w.WriteStartObject("prepare_play_options");
            w.WriteBoolean("always_play_something", false);
            if (!string.IsNullOrEmpty(trackUri))
            {
                w.WriteStartObject("skip_to");
                w.WriteString("track_uri", trackUri);
                w.WriteEndObject();
            }
            w.WriteString("license", "premium");
            w.WriteStartObject("player_options_override");
            w.WriteBoolean("shuffling_context", shuffle);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("play_options");
            w.WriteString("reason", "interactive");
            w.WriteString("operation", "replace");
            w.WriteString("trigger", "immediately");
            w.WriteEndObject();
            LoggingParams(w, deviceId, commandId, "", nowMs);
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();
        }

        /// <summary>The play_origin's feature for a context uri — the surface the desktop names for the same kind. PURE.</summary>
        public static string PlayFeatureOf(string contextUri)
        {
            if (contextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) return "playlist";
            if (contextUri.StartsWith("spotify:album:", StringComparison.Ordinal)) return "album";
            if (contextUri.StartsWith("spotify:artist:", StringComparison.Ordinal)) return "artist";
            if (contextUri.StartsWith("spotify:show:", StringComparison.Ordinal)) return "show";
            if (contextUri.Contains(":collection", StringComparison.Ordinal)) return "your_library";
            if (contextUri.StartsWith("spotify:track:", StringComparison.Ordinal) || contextUri.StartsWith("spotify:episode:", StringComparison.Ordinal)) return "track";
            return "harmony";
        }

        /// <summary>Volume is not a player verb: it is a PUT with a three-field `SetVolumeCommand` protobuf body,
        /// hand-encoded so this path carries no generated message for three bytes of varint.</summary>
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
                Headers = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.ContentProtobuf,
                Body = body[..n],
            };
            Api.Result result = Api.Send(RequestKind.Custom, args, ct);
            if (!result.Ok) Log.Warn("spotify", "connect volume rejected (" + result.Status + ")");
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
        /// and whether it sits in the user queue (<c>provider: "queue"</c>) rather than the context's continuation.</summary>
        public readonly record struct QueueWireRow(string Uri, string Uid, bool Queued);

        /// <summary>Append one track to the owner's queue (<c>add_to_queue</c>). Blocks; api threads only.</summary>
        public static bool AddToQueue(string targetDeviceId, string trackUri, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            AddToQueueBody(buffer, trackUri, OurDeviceId, NewId(), NewId(), Guid.NewGuid().ToString(), Playback.UnixNowMs());
            bool ok = PostCommand(PlayerCommandRoute, OurDeviceId, targetDeviceId, buffer.WrittenSpan, CommandHeaders, ct);
            Log.Info("spotify", "connect add_to_queue forwarded to the owner ok=" + ok);
            return ok;
        }

        /// <summary>Rewrite the owner's queue (<c>set_queue</c>) — its own rows echoed back, ours spliced in. Blocks; api
        /// threads only.</summary>
        public static bool SetQueue(string targetDeviceId, ulong queueRevision, QueueWireRow[] prev, QueueWireRow[] next,
            CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(4096);
            SetQueueBody(buffer, queueRevision, prev, next, OurDeviceId, NewId(), NewId(), Guid.NewGuid().ToString(),
                Playback.UnixNowMs());
            bool ok = PostCommand(PlayerCommandRoute, OurDeviceId, targetDeviceId, buffer.WrittenSpan, CommandHeaders, ct);
            Log.Info("spotify", "connect set_queue forwarded to the owner rows=" + next.Length + " revision=" + queueRevision
                + " ok=" + ok);
            return ok;
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
        /// BARE unsigned number (it exceeds Int64), both track lists — each row with its uid (always written), a metadata
        /// object carrying <c>is_queued: "true"</c> (a string) for a queued row, its provider, empty removed/blocked lists
        /// and the 22 empty restriction lists the capture carries — then the options and logging params. PURE.</summary>
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
                w.WriteStartObject();
                w.WriteString("uri", row.Uri);
                w.WriteString("uid", row.Uid ?? "");
                w.WriteStartObject("metadata");
                if (row.Queued) w.WriteString("is_queued", "true");
                w.WriteEndObject();
                w.WriteString("provider", row.Queued ? "queue" : "context");
                w.WriteStartArray("removed"); w.WriteEndArray();
                w.WriteStartArray("blocked"); w.WriteEndArray();
                w.WriteStartObject("restrictions");
                foreach (string key in RestrictionKeys) { w.WriteStartArray(key); w.WriteEndArray(); }
                w.WriteEndObject();
                w.WriteEndObject();
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

        static void QueueOptions(Utf8JsonWriter w)
        {
            w.WriteStartObject("options");
            w.WriteBoolean("override_restrictions", false);
            w.WriteBoolean("only_for_local_device", false);
            w.WriteBoolean("system_initiated", false);
            w.WriteEndObject();
        }

        /// <summary><c>logging_params</c> as the captures carry it: received time echoing initiated time, the interaction id.</summary>
        static void LoggingParams(Utf8JsonWriter w, string deviceId, string commandId, string interactionId, long nowMs)
        {
            w.WriteStartObject("logging_params");
            w.WriteNumber("command_initiated_time", nowMs);
            w.WriteNumber("command_received_time", nowMs);
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

        static bool PostCommand(string prefix, string fromDeviceId, string targetDeviceId, ReadOnlySpan<byte> body,
            HeaderSet headers, CancellationToken ct)
        {
            Span<char> path = stackalloc char[256];
            var w = new PathWriter(path);
            w.Append(prefix);
            w.AppendEscaped(fromDeviceId);
            w.Append("/to/");
            w.AppendEscaped(targetDeviceId);
            var args = new RequestArgs
            {
                Path = w.Written,
                Host = ApiHost.Spclient,
                Verb = Verb.Post,
                Headers = headers,
                Body = body,
            };
            Api.Result result = Api.Send(RequestKind.Custom, args, ct);
            if (!result.Ok) Log.Warn("spotify", "connect " + prefix + " rejected (" + result.Status + ")");
            return result.Ok;
        }

        static string NewId() => Guid.NewGuid().ToString("N");
    }
}
