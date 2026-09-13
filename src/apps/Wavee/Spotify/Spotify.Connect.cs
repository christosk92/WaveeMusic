// ── Spotify/Spotify.Connect.cs ─────────────────────────────────────────────────────────────────────────────────────
// spirc glue
//
// Role: SHELL
// Owner: F
// Wave: 2
// Budget: 400 lines
// Spec: plan
//
// THE GLUE, AND ONLY THE GLUE (D19, plan §4.10). Three jobs:
//   IN   `OnDealer` — the dealer receive thread hands every non-protocol frame here. Parse it with owner D's pure
//        `DealerFrame.Parse`, decode it with owner E's pure `Decode.ClusterUpdate` / `Decode.ConnectCommand`, ack a
//        REQUEST with `Spotify.Reply`, and post the RESULT as a value. Nothing is interpreted here — this file does
//        not know what "shuffle" means, and it must not learn.
//   OUT  `PublishState` — our player state to the connect-state service, debounced, sequence-numbered, gzipped.
//   OUT  `Send` — a command to ANOTHER device (the transfer / player-command / volume routes).
//
// WHERE THE RESULTS GO. Wave 3's `Playback` does not exist yet, and inventing its `Input` type from the outside is
// exactly the mistake the plan's §4.6 note refuses for `Decode.PutState`. So the results land in a BOUNDED MAILBOX on
// this class (C8): `Connect.TryDequeue` hands one out, `Connect.Wake` is the callback the host sets to be told there
// is one. When `Playback.Host` lands it drains this mailbox in its Post drain and this file does not change.
//
// THREADS. `OnDealer` runs on `wavee-spotify-dealer` and must return FAST — it is the same thread that answers pings,
// so a blocking decode there is a dropped keepalive and a dead session. It parses, decodes and enqueues; nothing in
// it opens a socket. `PublishState` and `Send` block and therefore run on an api thread (`Api.Run`).
//
// THE FENCE (C5). Every cluster carries `server_timestamp_ms`; it is fed to `Spotify.ObserveClusterTimestamp` the
// moment it is decoded, BEFORE the delta is enqueued, so the ownership fold in Wave 3 reads a clock that is already
// current for the delta it is about to fold.

using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>The Connect glue: dealer in, put-state and commands out. SHELL.</summary>
    public static class Connect
    {
        // ── 1. the mailbox ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What kind of thing came off the dealer.</summary>
        public enum ItemKind : byte { None = 0, Cluster, RemoteCommand }

        /// <summary>One decoded dealer result. A VALUE, except for <see cref="Buffer"/> — the delta's text and its
        /// device/track runs live in that buffer, so the CONSUMER must call <see cref="Release"/> when it is done with
        /// the item, or the pool starves and every push allocates a fresh one.</summary>
        public readonly struct Item
        {
            public readonly ItemKind Kind;
            public readonly Decode.ClusterDelta Delta;
            public readonly Decode.ClusterBuffer? Buffer;
            public readonly Decode.RemoteCommand Command;
            /// <summary>The session epoch this arrived under (C4). A consumer drops anything older than its own.</summary>
            public readonly uint Epoch;

            internal Item(Decode.ClusterDelta delta, Decode.ClusterBuffer buffer, uint epoch)
            {
                Kind = ItemKind.Cluster;
                Delta = delta;
                Buffer = buffer;
                Command = default;
                Epoch = epoch;
            }

            internal Item(Decode.RemoteCommand command, uint epoch)
            {
                Kind = ItemKind.RemoteCommand;
                Delta = default;
                Buffer = null;
                Command = command;
                Epoch = epoch;
            }
        }

        /// <summary>How deep the mailbox goes before the OLDEST item is dropped. A controller holding the next button
        /// down can push faster than a frame drains, and the only item that matters then is the newest one — which is
        /// why the drop is at the front and is logged (C8: bounded, never silent).</summary>
        public const int InboxDepth = 64;

        static readonly ConcurrentQueue<Item> Inbox = new();
        static int s_inboxCount;
        static int s_droppedItems;

        /// <summary>Set by the host to be told the mailbox is non-empty — `AppHost.Post` in practice, so a push wakes
        /// an idle loop. Called on the dealer thread, so it must do nothing but wake (C9).</summary>
        public static Action? Wake { get; set; }

        /// <summary>How many pushes were dropped because nobody drained. Non-zero is a bug in the host, not in the
        /// network, and the diagnostics page shows it.</summary>
        public static int DroppedItems => Volatile.Read(ref s_droppedItems);

        public static int Pending => Volatile.Read(ref s_inboxCount);

        /// <summary>Take the next result. UI thread, from the Post drain.</summary>
        public static bool TryDequeue(out Item item)
        {
            if (Inbox.TryDequeue(out item)) { Interlocked.Decrement(ref s_inboxCount); return true; }
            item = default;
            return false;
        }

        /// <summary>Hand an item's cluster buffer back. Safe to call for any item; a command carries none.</summary>
        public static void Release(in Item item)
        {
            if (item.Buffer is { } buffer) Decode.ClusterBuffer.Return(buffer);
        }

        static void Enqueue(in Item item)
        {
            Inbox.Enqueue(item);
            if (Interlocked.Increment(ref s_inboxCount) > InboxDepth && TryDequeue(out Item oldest))
            {
                Release(oldest);
                Interlocked.Increment(ref s_droppedItems);
            }
            Wake?.Invoke();
        }

        /// <summary>Drop everything queued. Called when the session epoch bumps — every one of those items describes a
        /// world that no longer exists (C4).</summary>
        public static void Clear()
        {
            while (TryDequeue(out Item item)) Release(item);
        }

        // ── 2. in: the dealer ────────────────────────────────────────────────────────────────────────────────────────

        [ThreadStatic] static byte[]? t_scratch;
        static byte[]? s_deviceIdUtf8;

        /// <summary>OUR device id — the persisted, launch-stable one. `Platform.DeviceId` is the SAME value the
        /// session copies into its text arena at boot (`Spotify.Session.cs`'s `Boot`), and it is a cached string, so
        /// reading it per request costs nothing and cannot disagree with what we announced.</summary>
        static string OurDeviceId => Platform.DeviceId;

        static ReadOnlySpan<byte> DeviceIdUtf8 => s_deviceIdUtf8 ??= Encoding.UTF8.GetBytes(OurDeviceId);

        /// <summary>Every dealer frame the session did not answer itself. DEALER THREAD — parse, decode, enqueue, and
        /// return. The scratch buffer is this thread's own, which is what makes the parse allocation-free per push
        /// after the first one.
        ///
        /// <para>PUBLIC, not internal as the surface note sketched it: `Spotify.Session`'s receive loop is its only
        /// production caller, but the whole of this file's behaviour is "what does a frame become", and a test that
        /// cannot hand it a frame is a test of nothing. The assembly has no `InternalsVisibleTo`.</para></summary>
        public static void OnDealer(ReadOnlySpan<byte> frame)
        {
            byte[] scratch = t_scratch ??= new byte[256 * 1024];
            DealerMessage message;
            try { message = DealerFrame.Parse(frame, scratch); }
            catch (Exception ex) { Log.Warn("spotify", "dealer frame unparseable: " + ex.GetType().Name); return; }

            uint epoch = Current.Epoch;

            if (message.IsClusterUpdate)
            {
                Decode.ClusterBuffer buffer = Decode.ClusterBuffer.Rent();
                Decode.ClusterDelta delta;
                try { delta = Decode.ClusterUpdate(message.Payload, DeviceIdUtf8, buffer); }
                catch (Exception ex)
                {
                    Decode.ClusterBuffer.Return(buffer);
                    Log.Error("spotify", "cluster decode faulted", ex);
                    return;
                }
                // The free clock sample, fed BEFORE the delta is visible to anyone (C5).
                if (delta.ServerTimestampMs > 0) ObserveClusterTimestamp(delta.ServerTimestampMs);
                Enqueue(new Item(delta, buffer, epoch));
                return;
            }

            if (message.IsRequest)
            {
                Decode.RemoteCommand command;
                try { command = Decode.ConnectCommand(message.Payload); }
                catch (Exception ex) { Log.Error("spotify", "connect command decode faulted", ex); command = default; }

                // ACK FIRST, ALWAYS. The controller retries an unacked command, and a retry storm from a phone in a
                // pocket is worse than a command we could not read: `Ok` says whether we understood the BODY, and a
                // body we did not understand is still a command we received.
                Reply(message.Key, ok: true);
                if (command.Kind != Decode.RemoteCmd.Unknown) Enqueue(new Item(command, epoch));
                else Log.Warn("spotify", "connect command not understood — acked and dropped");
                return;
            }

            // Everything else (presence pushes, playlist pushes, the user-attribute stream) is somebody else's frame.
            // It is not an error and it is not logged per push: the dealer carries a lot of traffic we do not read.
        }

        // ── 3. out: put-state ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Why we are announcing. The numbers are `connect.proto`'s `PutStateReason`.</summary>
        public enum PutReason : byte { Unknown = 0, SpircHello = 1, SpircNotify = 2, NewDevice = 3, PlayerStateChanged = 4, VolumeChanged = 5, PickerOpened = 6, BecameInactive = 7 }   // connect.proto PutStateReason ordinals, verbatim

        /// <summary>The debounce window. A PUT per keystroke is what made 0.2.9's volume slider a rate-limit magnet;
        /// 50 ms is one frame at 20 Hz and is short enough that a human never perceives it (P10 — named, and the only
        /// timer this file owns).</summary>
        public const int PublishDebounceMs = 50;

        static readonly Lock PublishGate = new();
        static Timer? s_debounce;
        static uint s_messageId;
        static PutReason s_pendingReason;
        static bool s_pendingActive;
        static Playback.Snapshot s_pendingSnapshot;

        /// <summary>The last message id we sent. The put-state response quotes it, which is how the ownership fold
        /// knows the cluster it just read is the answer to OUR claim and not somebody else's (C5).</summary>
        public static uint MessageId => Volatile.Read(ref s_messageId);

        /// <summary>Announce our player state. Coalescing by construction (C3): ten calls inside the window produce
        /// ONE put, carrying the LAST snapshot and the LAST reason.
        ///
        /// <para>WAVE 3 (owner G): the SNAPSHOT is the argument now. It is captured on the UI thread inside the
        /// reducer's drain (`Playback.Host.cs`), because <c>Playback.State</c> is the UI thread's alone (C1) and this
        /// debounce plus the PUT below both run on an api thread. <c>isActive</c> came off the snapshot with it — its
        /// one writer is <c>Playback.Ownership.IsActiveOnWire</c>.</para></summary>
        public static void PublishState(in Playback.Snapshot snapshot, PutReason reason)
        {
            lock (PublishGate)
            {
                s_pendingSnapshot = snapshot;
                s_pendingReason = reason;
                s_pendingActive = snapshot.IsActive;
                s_debounce ??= new Timer(static _ => Api.Run(Flush), null, Timeout.Infinite, Timeout.Infinite);
                s_debounce.Change(PublishDebounceMs, Timeout.Infinite);
            }
        }

        /// <summary>Announce NOW, skipping the window. The new-connection announce uses it: a fresh connection id that
        /// waits 50 ms is a device the picker does not show for 50 ms longer than it has to.</summary>
        public static void PublishNow(in Playback.Snapshot snapshot, PutReason reason)
        {
            lock (PublishGate) { s_pendingSnapshot = snapshot; s_pendingReason = reason; s_pendingActive = snapshot.IsActive; }
            Api.Run(Flush);
        }

        static void Flush()
        {
            PutReason reason;
            bool isActive;
            uint messageId;
            Playback.Snapshot snapshot;
            lock (PublishGate)
            {
                reason = s_pendingReason;
                isActive = s_pendingActive;
                messageId = ++s_messageId;
                // The id is minted HERE, inside the window, so the ten captures that coalesced into this one PUT all
                // travel under the number that actually goes on the wire — the number the response quotes, which is
                // what the ownership fence reads (C5).
                snapshot = s_pendingSnapshot.WithMessageId(messageId);
            }

            string connectionId = ConnectionId();
            if (connectionId.Length == 0) return;                  // no dealer, no device: nothing to announce to

            byte[] rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int written = Decode.PutState(in snapshot, rented);
                if (written <= 0) return;

                var args = new RequestArgs { Id = OurDeviceId, Body = rented.AsSpan(0, written) };
                Api.Result result = Api.Send(RequestKind.ConnectStatePut, args, CancellationToken.None);
                if (!result.Ok)
                {
                    // 422 after a BecameInactive is the service saying "you already were" — a soft acknowledgement,
                    // not a failure, and warning about it trains the reader to ignore the warning that matters.
                    if (result.Status == 422 && reason == PutReason.BecameInactive) return;
                    Log.Warn("spotify", "put-state " + reason + " rejected (" + result.Status + ")");
                    // No verdict will come for this put. Say so, or a claim bound to it sits Protected — and audible —
                    // until its 5 s window expires (C5).
                    Playback.Post(Playback.Input.PutVerdict(messageId, accepted: false));
                    return;
                }

                Log.Info("spotify", "put-state " + reason + " active=" + isActive + " msgId=" + messageId
                    + " cluster=" + result.Body.Length + "B");

                // Bind the claim to the id this put went out under: its RESPONSE — and only its response — is the
                // verdict the fence reads (C5). Without this the claim never binds and P1/P3/P4 can never fire.
                Playback.Post(Playback.Input.PutSent(messageId, isActive));

                // The RESPONSE is a Cluster, and it is the judge: it says whether the service adopted our claim. It
                // goes into the same mailbox as a push, marked `PutResponse` below, so the ownership fold has
                // exactly one input shape to read (C5).
                if (result.Body.Length == 0) return;
                Decode.ClusterBuffer buffer = Decode.ClusterBuffer.Rent();
                try
                {
                    Decode.ClusterDelta delta = Decode.Cluster(result.Bytes, DeviceIdUtf8, buffer);
                    // The two marks the fence reads (C5): WHERE this cluster came from, and WHICH of our puts it is
                    // the verdict on. `Decode.Cluster` cannot know either — it is handed bytes, not a conversation.
                    delta.Origin = Decode.ClusterOrigin.PutResponse;
                    delta.PutMsgId = messageId;
                    if (delta.ServerTimestampMs > 0) ObserveClusterTimestamp(delta.ServerTimestampMs);
                    Enqueue(new Item(delta, buffer, Current.Epoch));
                }
                catch (Exception ex)
                {
                    Decode.ClusterBuffer.Return(buffer);
                    Log.Error("spotify", "put-state response decode faulted", ex);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // ── 4. out: commands to another device ───────────────────────────────────────────────────────────────────────
        //
        // Three ROUTES, not one: a transfer moves the cluster's active device, a player command is a verb for the
        // device that already has it, and volume is its own PUT with a protobuf body. All three go through
        // `RequestKind.Custom` rather than D's `ConnectState*` kinds, because the captured desktop client sends a
        // header tuple those kinds do not carry (form content-type + gzip + connection id on the command route, a
        // protobuf PUT on volume). The delta is written down in the handover rather than patched into D's fold.

        const HeaderSet CommandHeaders = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
            | HeaderSet.AcceptLanguage | HeaderSet.ContentForm | HeaderSet.GzipBody | HeaderSet.ConnectionId;

        const HeaderSet TransferHeaders = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
            | HeaderSet.AcceptLanguage | HeaderSet.ContentForm | HeaderSet.ConnectionId;

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
            return PostCommand("/connect-state/v1/player/command/from/", OurDeviceId, targetDeviceId,
                buffer.WrittenSpan, CommandHeaders, ct);
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
