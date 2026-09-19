// ── Spotify/Spotify.Connect.cs ─────────────────────────────────────────────────────────────────────────────────────
// spirc glue
//
// Role: SHELL
// Owner: F
// Wave: 2
// Budget: 400 lines
// Spec: plan
// Named partial: `Spotify.Connect.Commands.cs` (gap batch B3b) — where a REQUEST verb goes after the ack
// Named partial: `Spotify.Connect.Outbound.cs` (gap batch R4-1) — §4, commands to another device, and the queue forwards
//
// GAP BATCH R4-1: the PUT is sized to its snapshot (`Decode.PutStateCapacity`, a 50 + 50 window no longer fits 64 KB);
// a rejected PlayerStateChanged / VolumeChanged re-announces the device, at most once per 30 s (G-247, 0.2.9's
// MaybeReannounce); Online boots play registration (G-023); and a sign-out sends the BecameInactive it owes BEFORE the
// session that could carry it is torn down (`RetireThen`, G-036).
//
// THE GLUE, AND ONLY THE GLUE (D19, plan §4.10). Three jobs:
//   IN   `OnDealer` — the dealer receive thread hands every non-protocol frame here. Parse it with owner D's pure
//        `DealerFrame.Parse`, decode it with owner E's pure `Decode.ClusterUpdate` / `Decode.ConnectCommand`, ack a
//        REQUEST with `Spotify.Reply`, and post the RESULT as a value. Nothing is interpreted here — this file does
//        not know what "shuffle" means, and it must not learn. Two MESSAGE topics beside the cluster (G-073): an inbound
//        `connect/volume` becomes a `Volume` item, and `connect/logout` is the user's own `Spotify.Logout()`.
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
    public static partial class Connect
    {
        // ── 1. the mailbox ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What kind of thing came off the dealer.</summary>
        public enum ItemKind : byte
        {
            None = 0, Cluster, RemoteCommand,
            /// <summary>Another device set OUR volume (`connect/volume`): <see cref="Item.Volume"/> on the wire scale
            /// (0..65535) and the sender's <see cref="Item.VolumeMessageId"/>, which the next PutState should echo (G-073).</summary>
            Volume,
        }

        /// <summary>One decoded dealer result. A VALUE, except for <see cref="Buffer"/> — the delta's text and its
        /// device/track runs live in that buffer, so the CONSUMER must call <see cref="Release"/> when it is done with
        /// the item, or the pool starves and every push allocates a fresh one.</summary>
        public readonly struct Item
        {
            public readonly ItemKind Kind;
            public readonly Decode.ClusterDelta Delta;
            public readonly Decode.ClusterBuffer? Buffer;
            public readonly Decode.RemoteCommand Command;
            /// <summary><see cref="ItemKind.Volume"/> only: 0..65535.</summary>
            public readonly int Volume;
            /// <summary><see cref="ItemKind.Volume"/> only: the sender's message id (0 when it sent none).</summary>
            public readonly uint VolumeMessageId;
            /// <summary>The session epoch this arrived under (C4). A consumer drops anything older than its own.</summary>
            public readonly uint Epoch;

            internal Item(Decode.ClusterDelta delta, Decode.ClusterBuffer buffer, uint epoch)
            {
                Kind = ItemKind.Cluster;
                Delta = delta;
                Buffer = buffer;
                Command = default;
                Volume = 0;
                VolumeMessageId = 0;
                Epoch = epoch;
            }

            internal Item(Decode.RemoteCommand command, uint epoch)
            {
                Kind = ItemKind.RemoteCommand;
                Delta = default;
                Buffer = null;
                Command = command;
                Volume = 0;
                VolumeMessageId = 0;
                Epoch = epoch;
            }

            internal Item(int volume, uint messageId, uint epoch)
            {
                Kind = ItemKind.Volume;
                Delta = default;
                Buffer = null;
                Command = default;
                Volume = volume;
                VolumeMessageId = messageId;
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
        /// world that no longer exists (C4). A held announce (B2) and the content-dedup key (B2) describe that same
        /// world — CloseAll can mean a sign-out, and a held snapshot or a suppressed re-send must never leak into
        /// whichever account is signed in next.</summary>
        public static void Clear()
        {
            while (TryDequeue(out Item item)) Release(item);
            lock (s_publishLock) { s_hasHeld = false; s_heldSnapshot = default; s_heldReason = default; }
            s_hasPublishedKey = false;
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
                TraceCluster(in delta, buffer);                    // the diagnostics page's last cluster, while the buffer is ours
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
                // A play/transfer goes to the playback host with its decoded body (claim and load in ONE slot, G-071); the
                // verbs with a body are folded or decoded first (G-074). Spotify.Connect.Commands.cs.
                RouteCommand(in command, message.Payload, epoch);
                return;
            }

            if (message.IsConnectVolume)
            {
                // A value like any other: the reducer owns the volume, so the glue only reads the three varints (G-073).
                if (DealerFrame.TryReadSetVolume(message.Payload, out int volume, out uint messageId))
                    Enqueue(new Item(volume, messageId, epoch));
                else Log.Warn("spotify", "connect volume message had no readable SetVolumeCommand");
                return;
            }

            if (message.IsConnectLogout)
            {
                // The "Log out" another client offers for this device (PutState advertises supports_logout): exactly the
                // user's own sign-out — sockets down, credential wiped, the account's surfaces torn down. Posted (C1).
                Log.Info("spotify", "another device asked this one to log out");
                Logout();
                return;
            }

            // Everything else (presence pushes, playlist pushes, the user-attribute stream) is somebody else's frame.
            // It is not an error and it is not logged per push: the dealer carries a lot of traffic we do not read. A
            // library push (the rootlist, a collection) is the library host's to classify and settle (Spotify.Library.cs).
            if (message.Kind == DealerFrameKind.Message) Library.OnDealerPush(message.Uri);
        }

        // ── 3. out: put-state ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Why we are announcing. The numbers are `connect.proto`'s `PutStateReason`.</summary>
        public enum PutReason : byte { Unknown = 0, SpircHello = 1, SpircNotify = 2, NewDevice = 3, PlayerStateChanged = 4, VolumeChanged = 5, PickerOpened = 6, BecameInactive = 7 }   // connect.proto PutStateReason ordinals, verbatim

        /// <summary>The debounce window. A PUT per keystroke is what made 0.2.9's volume slider a rate-limit magnet;
        /// 50 ms is one frame at 20 Hz and is short enough that a human never perceives it (P10 — named, and the only
        /// timer this file owns).</summary>
        public const int PublishDebounceMs = 50;

        static readonly Lock s_publishLock = new();
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
            lock (s_publishLock)
            {
                // No dealer connection: hold it (B2) rather than drop it — a PublishState that used to vanish silently
                // (P2) is the reason a phone kept showing a stale row for 2h23m (2026-09-18).
                if (GateOrHold(in snapshot, reason, hasConnectionId: !Current.ConnectionId.IsEmpty)) return;
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
            bool proceed;
            lock (s_publishLock)
            {
                proceed = !GateOrHold(in snapshot, reason, hasConnectionId: !Current.ConnectionId.IsEmpty);
                if (proceed) { s_pendingSnapshot = snapshot; s_pendingReason = reason; s_pendingActive = snapshot.IsActive; }
            }
            if (proceed) Api.Run(Flush);
        }

        // ── 3a-gate. the owed announce (B2) ──────────────────────────────────────────────────────────────────────────
        //
        // While the connection id is empty (backoff, handshake, login — seconds to a minute) `PublishState` /
        // `PublishNow` used to return silently (Connect.cs:281,296,447 before this fix) and the fact they carried was
        // gone: nothing was retained, nothing was logged. A phone kept showing Wavee's stale row for 2h23m because of
        // exactly this (2026-09-18). Now every announce passes ONE pure gate.

        /// <summary>Where an announce goes when it cannot go straight to the wire. PURE.</summary>
        public enum PublishOutcome : byte
        {
            /// <summary>A connection id exists: send it now.</summary>
            Send,
            /// <summary>No connection id, and worth remembering — the next connection's hello supersedes it.</summary>
            Hold,
            /// <summary>No connection id, and NOT worth remembering.</summary>
            Drop,
        }

        /// <summary>The pure half of the owed-announce latch. PURE — no lock, no log, no state.</summary>
        public static class PublishGate
        {
            /// <summary><paramref name="hasConnectionId"/> always sends. Without one, everything but
            /// <see cref="PutReason.BecameInactive"/> is worth holding for the connection this session is still
            /// waiting on (newest wins — the caller's job). A held <see cref="PutReason.BecameInactive"/> would be this
            /// session's FIRST word to a connection it never said hello to — 0.2.9 never sent one either, and holding it
            /// here would resurrect it for whichever account is signed in when the NEXT connection id arrives, which is
            /// exactly the sign-out race this file must not reopen. <paramref name="hasHeld"/> decides nothing here — the
            /// caller reads it for its OWN "log once, not per call" rule.</summary>
            public static PublishOutcome Decide(bool hasConnectionId, PutReason reason, bool hasHeld)
                => hasConnectionId ? PublishOutcome.Send
                 : reason == PutReason.BecameInactive ? PublishOutcome.Drop
                 : PublishOutcome.Hold;
        }

        // The one slot (newest wins) and its log-once latch. Guarded by `s_publishLock` — the same lock every writer
        // of `s_pending*` already takes, so the held snapshot and the pending one never race each other.
        static Playback.Snapshot s_heldSnapshot;
        static PutReason s_heldReason;
        static bool s_hasHeld;

        /// <summary>Called with <see cref="s_publishLock"/> already held. Returns true when the caller must stop — the
        /// announce was HELD (stored, logged once) or DROPPED; false means proceed to send.</summary>
        static bool GateOrHold(in Playback.Snapshot snapshot, PutReason reason, bool hasConnectionId)
        {
            PublishOutcome outcome = PublishGate.Decide(hasConnectionId, reason, s_hasHeld);
            if (outcome == PublishOutcome.Send) return false;
            if (outcome == PublishOutcome.Hold)
            {
                bool firstHold = !s_hasHeld;
                s_heldSnapshot = snapshot;
                s_heldReason = reason;
                s_hasHeld = true;
                // Once per HOLD, not per call — a paused foreign mirror ticking `DoTick` every second must not spam this.
                if (firstHold) Log.Info("spotify", "put-state held (" + reason + ") — no connection id");
            }
            return true;
        }

        /// <summary>The held slot is moot the moment a connection id exists again: the next thing this session sends
        /// under it — the hello, always, since <see cref="AnnounceDevice"/> calls it unconditionally when the id is
        /// new — captures LIVE state, which is a strict superset of whatever was stale-held. Replaying the held
        /// snapshot instead would risk announcing a position/track that is minutes old by the time a connection
        /// finally comes back — so this drains it (says what it drops, once) rather than resending it.</summary>
        static void ClearHeld()
        {
            bool wasHeld;
            PutReason reason;
            Playback.Snapshot snapshot;
            lock (s_publishLock)
            {
                wasHeld = s_hasHeld;
                reason = s_heldReason;
                snapshot = s_heldSnapshot;
                s_hasHeld = false; s_heldSnapshot = default; s_heldReason = default;
            }
            if (wasHeld) Log.Info("spotify", "put-state held (" + reason + ") drained track=" + snapshot.Track.Text
                + " — connection id restored, the hello carries current state instead");
        }

        // ── 3a. the hello (headless plan §1.6 item 5) ────────────────────────────────────────────────────────────────

        /// <summary>Set by the host that owns the player state (<c>Playback.Boot</c>): capture a snapshot on the UI thread
        /// and send it as the hello PUT. Null until then — a session with no player announces nothing, because a device
        /// with no state to report is not a device worth showing.</summary>
        public static Action? Hello { get; set; }

        /// <summary>Whether reaching <c>Online</c> announces this device. On for the GUI (every Spotify client shows up in
        /// every other client's picker); a headless run turns it off unless it was asked to be a Connect device
        /// (<c>--connect</c>, headless plan §2.9), so a smoke never adds a second row to the user's pickers.</summary>
        public static bool AnnounceOnOnline { get; set; } = true;

        /// <summary>The session's <c>AnnounceDevice</c> effect: a fresh connection id is in the session box, so the PUT can
        /// quote it. UI THREAD (it runs inside <c>Spotify.Apply</c>).</summary>
        internal static void AnnounceDevice()
        {
            // G-023: Online is where play registration starts — its batcher, its heartbeat and the resume-point ticker —
            // rather than on the first registration event (idempotent; a headless run registers its plays too).
            Telemetry.Boot();
            // B3: this fires for every NEW connection id, not merely the first — the fresh id is exactly what makes a
            // held announce moot (B2), whether or not a hello actually goes out under it.
            ClearHeld();
            if (!AnnounceOnOnline) return;
            Hello?.Invoke();
        }

        // ── 3b. a rejected put, and the put a sign-out owes ─────────────────────────────────────────────────────────

        /// <summary>What a non-OK put-state answer means (0.2.9 <c>DeviceStatePublisher</c>).</summary>
        public enum PutRejection : byte
        {
            /// <summary>422 after a BecameInactive: "you already were" — a soft acknowledgement, not a failure.</summary>
            SoftAck,
            /// <summary>Refused: no verdict will come, so the claim bound to it must hear so.</summary>
            Rejected,
            /// <summary>Refused a PUT that assumes our registration (a state or volume change) — 422 above all, the service's
            /// "I no longer have your device": the claim hears so AND the device re-announces (G-247).</summary>
            Reannounce,
        }

        /// <summary>The rejection route for <paramref name="reason"/> answered <paramref name="status"/>. PURE.</summary>
        public static PutRejection RejectionOf(PutReason reason, int status)
            => status == 422 && reason == PutReason.BecameInactive ? PutRejection.SoftAck
             : reason is PutReason.PlayerStateChanged or PutReason.VolumeChanged ? PutRejection.Reannounce
             : PutRejection.Rejected;

        /// <summary>The fewest milliseconds between two re-announces: a hard-down service must not become a hot loop.</summary>
        public const long ReannounceIntervalMs = 30_000;

        /// <summary>May a rejected put re-announce now, the last re-announce having gone at <paramref name="lastMs"/>? PURE.</summary>
        public static bool ReannounceDue(long nowMs, long lastMs) => nowMs - lastMs >= ReannounceIntervalMs;

        static long s_lastReannounceMs = long.MinValue / 2;

        /// <summary>API THREAD: re-announce this device as a new connection (the hello), rate-limited across the api threads.
        /// Without it a device the service dropped stays off every picker until something else announces (G-247).</summary>
        static void MaybeReannounce()
        {
            long now = Playback.FrameNowMs();
            long last = Interlocked.Read(ref s_lastReannounceMs);
            if (!ReannounceDue(now, last) || Interlocked.CompareExchange(ref s_lastReannounceMs, now, last) != last) return;
            Log.Info("spotify", "put-state rejected: re-announcing this device as a new connection");
            Hello?.Invoke();
        }

        /// <summary>How a sign-out treats the cluster (G-036). PURE.</summary>
        public enum RetireRoute : byte
        {
            /// <summary>Nothing is owed: no connection, no player, or playback is not ours.</summary>
            SignOutNow,
            /// <summary>Playback is ours: the BecameInactive PUT goes first, then the sign-out.</summary>
            InactiveFirst,
        }

        public static RetireRoute RetireRouteOf(bool hasConnection, bool hasPlayer, bool ownsPlayback)
            => hasConnection && hasPlayer && ownsPlayback ? RetireRoute.InactiveFirst : RetireRoute.SignOutNow;

        /// <summary>The longest a sign-out waits on its BecameInactive PUT.</summary>
        public const int RetireTimeoutMs = 2_000;

        /// <summary>Sign out AFTER telling the cluster this device is no longer active (G-036). The session's Logout step
        /// clears the connection id and forgets the bearer in the same fold that closes the epoch, and the reducer's own
        /// <c>Release(Logout)</c> announce is only ever posted after it — so that announce finds no connection and the
        /// BecameInactive never left; the phone went on showing Wavee as the active device. Here the inactive PUT is built
        /// on the UI thread (<c>Playback.RetireForSignOut</c>) and sent on an api thread, bounded by
        /// <see cref="RetireTimeoutMs"/>, and <paramref name="signOut"/> runs when it is done — or at once when nothing is
        /// owed. Any thread. <c>Spotify.Logout</c> is the caller.</summary>
        public static void RetireThen(Action signOut)
        {
            if (RetireRouteOf(!Current.ConnectionId.IsEmpty, Hello is not null, ownsPlayback: true) == RetireRoute.SignOutNow)
            {
                signOut();
                return;
            }
            Playback.ToUi(() =>
            {
                bool owed = Playback.RetireForSignOut(out Playback.Snapshot inactive);
                if (RetireRouteOf(!Current.ConnectionId.IsEmpty, Hello is not null, owed) == RetireRoute.SignOutNow)
                {
                    signOut();
                    return;
                }
                uint messageId;
                lock (s_publishLock) messageId = ++s_messageId;
                Playback.Snapshot snapshot = inactive.WithMessageId(messageId);
                bool queued = Api.Run(() =>
                {
                    try { SendRetire(in snapshot); }
                    finally { signOut(); }
                });
                if (!queued) signOut();
            });
        }

        /// <summary>API THREAD: the sign-out's BecameInactive, sent while the session still has its connection and bearer.
        /// Its response is not folded — the session it would describe is being closed.</summary>
        static void SendRetire(in Playback.Snapshot snapshot)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(Decode.PutStateCapacity(in snapshot));
            try
            {
                int written = Decode.PutState(in snapshot, rented);
                var args = new RequestArgs { Id = OurDeviceId, Body = rented.AsSpan(0, written) };
                using var bounded = new CancellationTokenSource(RetireTimeoutMs);
                Api.Result result = Api.Send(RequestKind.ConnectStatePut, args, bounded.Token);
                Log.Info("spotify", "put-state BecameInactive before sign-out msgId=" + snapshot.MessageId + " status=" + result.Status);
            }
            catch (Exception ex) { Log.Warn("spotify", "the sign-out's inactive put-state failed", ex); }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }

        // ── 3d. the content key (B2) ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>What makes a PlayerStateChanged / VolumeChanged put-state redundant: the SAME content, latched
        /// only on a 2xx so a lost or rejected put is retried by the very next publish. Never consulted for
        /// NewDevice / NewConnection / BecameInactive — those are identity and lifecycle announces, not state, and a
        /// picker that never sees them again is worse than one extra PUT. PURE.</summary>
        public readonly record struct PublishKey(bool Active, EntityId Track, EntityId Context, bool Playing,
            bool Paused, bool Shuffle, Decode.RepeatMode Repeat, long PositionSec, int Volume, int NextTracksSignature)
        {
            /// <summary>Position is truncated to whole SECONDS — the pump's own ticker moves it every drain, and a key
            /// that changed every second would never suppress anything.</summary>
            public static PublishKey Of(in Playback.Snapshot s) => new(s.IsActive, s.Track, s.Context, s.IsPlaying,
                s.IsPaused, s.Shuffling, s.Repeat, s.PositionAsOfMs / 1000, s.Volume, NextSignature(in s.Wire));

            /// <summary>FNV-ish fold over the next-tracks window's identities — the same family as
            /// <see cref="TraceKey"/>, just over <see cref="EntityId"/> instead of bytes.</summary>
            static int NextSignature(in Playback.WireExtras wire)
            {
                if (wire.Window is not { } window) return 0;
                ReadOnlySpan<Playback.WireRow> next = window.Next;
                int h = unchecked((int)2166136261);
                for (int i = 0; i < next.Length; i++) h = unchecked((h ^ next[i].Id.GetHashCode()) * 16777619);
                return h;
            }
        }

        static bool s_hasPublishedKey;
        static PublishKey s_lastPublishedKey;

        /// <summary>Is <paramref name="key"/> the same content the last ACCEPTED PlayerStateChanged / VolumeChanged
        /// already told the service? Every other reason always sends — NewDevice / NewConnection / BecameInactive are
        /// identity and lifecycle announces, never deduped, however unchanged the player half looks. PURE — the two
        /// latch fields are passed in rather than read, so the rule is testable with no static state.</summary>
        public static bool IsRedundant(PutReason reason, in PublishKey key, bool hasPublishedKey, in PublishKey lastPublished)
            => reason is PutReason.PlayerStateChanged or PutReason.VolumeChanged
               && hasPublishedKey && key.Equals(lastPublished);

        // ── 3e. the flush, serialised (B1) ───────────────────────────────────────────────────────────────────────────

        static readonly Lock s_flushGate = new();
        static bool s_flushing, s_flushAgain;

        /// <summary>PublishNow and the debounce timer both land here from an api worker — up to 4 of them (P4 in the
        /// api pool). Without a gate two Flushes can send out of msgId order (the wire order the ownership fence
        /// assumes, C5): the running one keeps <see cref="s_flushGate"/> and loops until nothing is left rather than
        /// letting a second call run beside it; a call that finds it busy sets the re-arm flag and returns — never
        /// lost, because `s_pending*` (the debounce's own coalescing) already holds the newest snapshot regardless of
        /// how many Flush calls stacked up waiting.</summary>
        static void Flush()
        {
            lock (s_flushGate)
            {
                if (s_flushing) { s_flushAgain = true; return; }
                s_flushing = true;
            }
            try
            {
                while (true)
                {
                    FlushOnce();
                    lock (s_flushGate)
                    {
                        if (!s_flushAgain) { s_flushing = false; return; }
                        s_flushAgain = false;
                    }
                }
            }
            catch
            {
                lock (s_flushGate) { s_flushing = false; s_flushAgain = false; }
                throw;
            }
        }

        /// <summary>API THREAD, and never two of these at once (see <see cref="Flush"/>): mint, gate, encode, send.</summary>
        static void FlushOnce()
        {
            PutReason reason;
            bool isActive;
            uint messageId;
            Playback.Snapshot snapshot;
            bool proceed;
            lock (s_publishLock)
            {
                reason = s_pendingReason;
                isActive = s_pendingActive;
                // The id is minted HERE, inside the window, so the ten captures that coalesced into this one PUT all
                // travel under the number that actually goes on the wire — the number the response quotes, which is
                // what the ownership fence reads (C5). Minted even when the gate below holds or drops it: message ids
                // need not be gapless, and a hold must still capture the snapshot under a stable id for its log line.
                messageId = ++s_messageId;
                snapshot = s_pendingSnapshot.WithMessageId(messageId);
                // The connection could have dropped between PublishState/PublishNow's own gate check and this api-thread
                // flush; re-check here rather than trust a decision made possibly milliseconds ago on another thread.
                proceed = !GateOrHold(in snapshot, reason, hasConnectionId: !Current.ConnectionId.IsEmpty);
            }
            if (!proceed) return;

            PublishKey key = PublishKey.Of(in snapshot);
            if (IsRedundant(reason, in key, s_hasPublishedKey, in s_lastPublishedKey)) return;   // already told (B2)

            // Sized to the snapshot: a 50 + 50 window with its rows' metadata is several times the old fixed 64 KB (G-240).
            byte[] rented = ArrayPool<byte>.Shared.Rent(Decode.PutStateCapacity(in snapshot));
            try
            {
                // started_playing_at is UNIX ms on the wire and the reducer stamps the frame clock: the two clocks sampled
                // together here convert it exactly, however long the window held the snapshot (B3b).
                long frameToUnixMs = Playback.UnixNowMs() - Playback.FrameNowMs();
                int written = Decode.PutState(in snapshot, rented, frameToUnixMs);
                if (written <= 0) return;
                TracePut(in snapshot, reason, frameToUnixMs);

                // B1: bind the claim to this msgId BEFORE the request leaves, not after a 2xx. A failed PUT used to
                // leave `ClaimMsgId==0`, so `Ownership.PutFailed` (Playback.cs) was a guaranteed no-op and a claim sat
                // Protected — and audible — for the full 5 s expiry instead of hearing about the failure at once.
                Playback.Post(Playback.Input.PutSent(messageId, isActive));

                var args = new RequestArgs { Id = OurDeviceId, Body = rented.AsSpan(0, written) };
                Api.Result result = Api.Send(RequestKind.ConnectStatePut, args, CancellationToken.None);
                if (!result.Ok)
                {
                    // 422 after a BecameInactive is the service saying "you already were" — a soft acknowledgement,
                    // not a failure, and warning about it trains the reader to ignore the warning that matters.
                    PutRejection rejection = RejectionOf(reason, result.Status);
                    if (rejection == PutRejection.SoftAck) return;
                    Log.Warn("spotify", "put-state " + reason + " rejected status=" + result.Status
                        + " msgId=" + messageId + " track=" + snapshot.Track.Text);
                    // No verdict will come for this put. Say so, or a claim bound to it sits Protected — and audible —
                    // until its 5 s window expires (C5).
                    Playback.Post(Playback.Input.PutVerdict(messageId, accepted: false));
                    // The service does not have this content either: let the NEXT publish of the same state resend it
                    // instead of the key silently suppressing it forever (B2).
                    s_hasPublishedKey = false;
                    if (rejection == PutRejection.Reannounce) MaybeReannounce();
                    return;
                }

                // B5: everything the diagnostics page's cards already show, in the log the user actually sends —
                // 2026-09-16/18's "why is Wavee invisible" both traced back to state this line never carried.
                Log.Info("spotify", "put-state " + reason + " active=" + isActive
                    + " track=" + snapshot.Track.Text + " pos=" + snapshot.PositionAsOfMs
                    + " playing=" + snapshot.IsPlaying + " paused=" + snapshot.IsPaused
                    + " ctx=" + snapshot.Context.Text + " msgId=" + messageId + " cluster=" + result.Body.Length + "B"
                    + " owner=" + (isActive ? "us" : "-") + " claim=" + (isActive ? messageId.ToString() : "-")
                    + " startedAt=" + snapshot.StartedPlayingAtMs + " hasBeenMs=" + snapshot.HasBeenPlayingForMs
                    + " origin=" + snapshot.Reason);

                s_hasPublishedKey = true;
                s_lastPublishedKey = key;

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
                    TraceEcho(in delta, buffer, messageId);
                    // Always-on: a claim the service did NOT adopt is the one line that explains "other devices do not
                    // show Wavee" (2026-09-16) — the diagnostics page's echo card is not in the log the user sends.
                    if (isActive)
                    {
                        ReadOnlySpan<byte> echoActive = buffer.Utf8(delta.ActiveDeviceId);
                        if (echoActive.IsEmpty || !echoActive.SequenceEqual(DeviceIdUtf8))
                            Log.Warn("spotify", "put-state echo msgId=" + messageId + " did not adopt our claim: cluster active="
                                + (echoActive.IsEmpty ? "(none)" : Encoding.UTF8.GetString(echoActive[..Math.Min(8, echoActive.Length)]) + "…"));
                    }
                    TraceCluster(in delta, buffer);
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

        // ── 4. out: commands to another device — Spotify.Connect.Outbound.cs ─────────────────────────────────────────────
    }
}
