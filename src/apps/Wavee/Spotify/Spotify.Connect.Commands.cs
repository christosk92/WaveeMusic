// ── Spotify/Spotify.Connect.Commands.cs ─────────────────────────────────────────────────────────────────────────────
// where a controller's REQUEST verb goes after the ack, and the Connect diagnostics traces the glue feeds (gap batch B3b)
//
// Role: SHELL
// Owner: F
// Wave: gap batch B3b
// Budget: 200 lines
// Spec: gap register G-071 (the host hand-off), G-074 (set_options, set_queue, update_context); ch 27 W24 (the traces) —
//       a named partial of Spotify.Connect.cs, declared when the glue passed 30 % over its 400-line budget
//
// THE SAME GLUE RULES AS THE PARENT: dealer thread, decode and hand off, interpret nothing, return fast. The route is a
// pure table (`RouteOf`); the four arms are the only things that touch the world:
//
//   play / transfer           ─▶ Decode.ConnectLoad into a pooled ClusterBuffer ─▶ Playback.RemoteLoadArrived
//                                (the claim AND the load in one host slot, newest wins — NO command item is enqueued)
//   set_options               ─▶ Decode.OptionVerbs ─▶ set_shuffling_context / set_repeating_* items, the verbs the
//                                reducer already folds (the bare set_options item only when the body stated nothing)
//   set_queue / update_context ─▶ Decode.ConnectQueue into a pooled ClusterBuffer ─▶ Playback.RemoteQueueArrived
//                                (the host's intake folds the claim and splices the rows behind the deck, in arrival
//                                order with the mailbox — NO command item is enqueued; gap batches B3c and R4-1)
//   every other known verb    ─▶ one command item
//
// THE TRACES (the Connect diagnostics page's last cluster / last put / last echo). Built where the facts are — the dealer
// thread for a push, the api thread for a put and its response — and NOTED on the UI thread through `Spotify.Post`. A
// cluster that restates what the page already shows (a heartbeat) formats no string and posts nothing.

using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Connect
    {
        /// <summary>What the glue does with an acked REQUEST, by its verb.</summary>
        public enum CommandRoute : byte
        {
            /// <summary>An endpoint we do not know: acked, logged, dropped.</summary>
            Drop,
            /// <summary>One command item in the mailbox.</summary>
            Mailbox,
            /// <summary>play / transfer: the decoded body to <c>Playback.RemoteLoadArrived</c>, no item.</summary>
            Load,
            /// <summary>set_options: the option verbs it folds to, as items.</summary>
            Options,
            /// <summary>set_queue / update_context: the decoded body to <c>Playback.RemoteQueueArrived</c>, no item.</summary>
            QueueBody,
        }

        /// <summary>The route table. PURE.</summary>
        public static CommandRoute RouteOf(Decode.RemoteCmd kind) => kind switch
        {
            Decode.RemoteCmd.Unknown => CommandRoute.Drop,
            Decode.RemoteCmd.Play or Decode.RemoteCmd.Transfer => CommandRoute.Load,
            Decode.RemoteCmd.SetOptions => CommandRoute.Options,
            Decode.RemoteCmd.SetQueue or Decode.RemoteCmd.UpdateContext => CommandRoute.QueueBody,
            _ => CommandRoute.Mailbox,
        };

        /// <summary>DEALER THREAD, after the ack.</summary>
        static void RouteCommand(in Decode.RemoteCommand command, ReadOnlySpan<byte> payload, uint epoch)
        {
            switch (RouteOf(command.Kind))
            {
                case CommandRoute.Load: HandOffLoad(in command, payload, epoch); break;
                case CommandRoute.Options: EnqueueOptions(in command, payload, epoch); break;
                case CommandRoute.QueueBody: HandOffQueue(in command, payload, epoch); break;
                case CommandRoute.Mailbox: Enqueue(new Item(command, epoch)); break;
                default: Log.Warn("spotify", "connect command not understood — acked and dropped"); break;
            }
        }

        /// <summary>G-071: rent, decode, hand over. The host owns the buffer from here and returns it once read. A body
        /// the decode could not read still reaches the host — as a load of kind Unknown, which folds the claim alone.</summary>
        static void HandOffLoad(in Decode.RemoteCommand command, ReadOnlySpan<byte> payload, uint epoch)
        {
            Decode.ClusterBuffer buffer = Decode.ClusterBuffer.Rent();
            Decode.RemoteLoad load;
            try { load = Decode.ConnectLoad(payload, buffer); }
            catch (Exception ex)
            {
                Log.Error("spotify", "connect load decode faulted", ex);
                buffer.Reset();
                load = default;
            }
            Log.Info("spotify", "connect " + (command.Kind == Decode.RemoteCmd.Transfer ? "transfer" : "play")
                + " rows=" + load.TrackCount + " context=" + (load.ContextUri.IsEmpty ? 0 : 1)
                + " current=" + (load.HasCurrent ? 1 : 0) + " paused=" + (Playback.RemotePlan.StartsPaused(in load) ? 1 : 0));
            Playback.RemoteLoadArrived(in command, in load, buffer, epoch);
        }

        /// <summary>G-074: set_options is the shuffle and repeat verbs a controller could have sent one at a time.</summary>
        static void EnqueueOptions(in Decode.RemoteCommand command, ReadOnlySpan<byte> payload, uint epoch)
        {
            Span<Decode.RemoteCommand> verbs = stackalloc Decode.RemoteCommand[Decode.MaxOptionVerbs];
            int count;
            try { count = Decode.OptionVerbs(payload, in command, verbs); }
            catch (Exception ex)
            {
                Log.Error("spotify", "set_options decode faulted", ex);
                count = 0;
            }
            if (count == 0) { Enqueue(new Item(command, epoch)); return; }
            for (int i = 0; i < count; i++) Enqueue(new Item(verbs[i], epoch));
        }

        /// <summary>G-074: set_queue / update_context. Rent, decode, hand over, like a load: the host owns the buffer from
        /// here, orders the body behind the mailbox items that came first, and applies it (history and the current row kept,
        /// what follows replaced). No command item is enqueued: the host's intake folds the claim.</summary>
        static void HandOffQueue(in Decode.RemoteCommand command, ReadOnlySpan<byte> payload, uint epoch)
        {
            Decode.ClusterBuffer buffer = Decode.ClusterBuffer.Rent();
            Decode.RemoteQueue queue;
            try { queue = Decode.ConnectQueue(payload, buffer); }
            catch (Exception ex)
            {
                Log.Error("spotify", "connect queue decode faulted", ex);
                buffer.Reset();
                queue = default;
            }
            Log.Info("spotify", command.Kind == Decode.RemoteCmd.SetQueue
                ? "set_queue prev=" + queue.PrevCount + " next=" + queue.NextCount
                : "update_context rows=" + queue.TrackCount + " context=" + (queue.ContextUri.IsEmpty ? 0 : 1));
            Playback.RemoteQueueArrived(in command, in queue, buffer, epoch);
        }

        // ── the Connect diagnostics traces (ch 27 W24) ──────────────────────────────────────────────────────────────

        static ulong s_clusterTraceKey;

        /// <summary>The cluster about to be folded, for the page — BEFORE the item is enqueued, while the buffer is still
        /// the glue's. Skipped when the owner, the track, play/pause, the origin and the changed devices all repeat.</summary>
        static void TraceCluster(in Decode.ClusterDelta d, Decode.ClusterBuffer buffer)
        {
            ReadOnlySpan<byte> active = buffer.Utf8(d.ActiveDeviceId);
            ReadOnlySpan<byte> track = d.HasTrack ? buffer.Utf8(d.Track.Uri) : default;
            ReadOnlySpan<byte> changed = buffer.Utf8(d.ChangedDevices);
            ulong key = TraceKey(active, track, changed, d.IsPlaying, d.IsPaused, d.Origin, d.PutMsgId);
            if (Interlocked.Exchange(ref s_clusterTraceKey, key) == key) return;
            var trace = new Diagnostics.ClusterTrace(TraceText(active),
                d.Origin == Decode.ClusterOrigin.PutResponse ? "response" : "push", d.PutMsgId, d.UpdateReason,
                TraceText(changed), d.ServerTimestampMs, TraceText(track), d.IsPlaying, d.IsPaused, d.PositionAsOfMs);
            Post(() => Diagnostics.Connect.NoteCluster(in trace));
        }

        /// <summary>The put-state we are about to send — every attempt, not only an accepted one: a stuck claim is debugged
        /// by what we last ASKED for. <c>started_playing_at</c> as the wire carries it (unix ms).</summary>
        static void TracePut(in Playback.Snapshot snapshot, PutReason reason, long frameToUnixMs)
        {
            var trace = new Diagnostics.PutTrace(snapshot.MessageId, reason.ToString(), snapshot.IsActive,
                (long)Decode.StartedPlayingAt(snapshot.StartedPlayingAtMs, frameToUnixMs), snapshot.HasBeenPlayingForMs,
                Playback.UnixNowMs());
            Post(() => Diagnostics.Connect.NotePut(in trace));
        }

        /// <summary>The put-state RESPONSE — the one cluster that says whether the service adopted our claim.</summary>
        static void TraceEcho(in Decode.ClusterDelta d, Decode.ClusterBuffer buffer, uint messageId)
        {
            ReadOnlySpan<byte> active = buffer.Utf8(d.ActiveDeviceId);
            bool adopted = !active.IsEmpty && active.SequenceEqual(DeviceIdUtf8);
            var trace = new Diagnostics.EchoTrace(messageId, TraceText(active), d.ServerTimestampMs, d.ActiveStartedPlayingAt,
                adopted, Playback.UnixNowMs());
            Post(() => Diagnostics.Connect.NoteEcho(in trace));
        }

        static string TraceText(ReadOnlySpan<byte> utf8) => utf8.IsEmpty ? "" : Encoding.UTF8.GetString(utf8);

        /// <summary>FNV-1a over what the cluster card shows, separated so two fields can never alias. PURE.</summary>
        static ulong TraceKey(ReadOnlySpan<byte> active, ReadOnlySpan<byte> track, ReadOnlySpan<byte> changed,
            bool playing, bool paused, Decode.ClusterOrigin origin, uint putMsgId)
        {
            ulong h = 14695981039346656037UL;
            h = Fold(Fold(Fold(h, active), track), changed);
            h ^= (playing ? 1UL : 0UL) | (paused ? 2UL : 0UL) | ((ulong)origin << 2) | ((ulong)putMsgId << 8);
            return h * 1099511628211UL;

            static ulong Fold(ulong h, ReadOnlySpan<byte> bytes)
            {
                for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 1099511628211UL; }
                h ^= 0xFF;
                return h * 1099511628211UL;
            }
        }
    }
}
