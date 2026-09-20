// ── Playback/Playback.Host.Remote.cs ─────────────────────────────────────────────────────────────────────────────────
// The Connect intake: the mailbox and the host's own Connect intakes (a play / transfer body, a controller's set_queue /
// update_context body) taken in ARRIVAL order, the hold an inbound context resolve puts on the mailbox, and a controller's
// rows applied behind the deck
//
// Role: SHELL
// Owner: G
// Wave: gap batch B3c (+ R4-1: a newer load supersedes one still resolving; update_context reshuffles)
// Budget: 300 lines
// Spec: gap register G-071 (the inbound load), G-074 (set_queue / update_context), G-075 (uids), G-036 (closed sessions),
//       G-244, G-245
//
// A NAMED PARTIAL OF `Playback.Host.cs`, declared when `Playback.Host.Context.cs` would have passed 30 % over its budget.
// The same SHELL rules: dealer-thread entry points only take the gate and wake; everything else is the UI thread's drain;
// every decision is a pure rule in `Playback.Transitions.cs` (`IntakeQueue`, `HoldsMailbox`, `SpliceQueue`, `UidBook`).
//
// ONE DEALER THREAD FILLS TWO STRUCTURES, AND THE REDUCER MUST SEE ONE ORDER:
//
//   dealer   play ─▶ RemoteLoadArrived ─▶ s_intakes (ahead = mailbox items already waiting)
//            pause ─▶ Connect mailbox
//   drain    1. a resolved context start (`StartResolved`) — its Play steps inline, and it releases the hold
//            2. an inbound context resolve in flight (≤ InboundHoldMs)? stop: everything that came after the load waits —
//               unless a NEWER play / transfer is waiting: the resolving one is superseded (G-244) and the drain goes on
//            3. the oldest intake, once no mailbox item is ahead of it ─▶ RunLoad / RunQueue
//            4. else the next mailbox item ─▶ FoldItem, and every waiting intake is one item closer
//
// So a phone's "play, then pause" pauses the play instead of pausing the old deck and leaving the play playing.

using System.Buffers;
using System.Text;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using ClusterTrack = Wavee.Spotify.Decode.ClusterTrack;
using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;
using RemoteLoad = Wavee.Spotify.Decode.RemoteLoad;
using RemoteQueue = Wavee.Spotify.Decode.RemoteQueue;

namespace Wavee;

public static partial class Playback
{
    // ── 1. the dealer-thread entry points ──────────────────────────────────────────────────────────────────────────

    static readonly IntakeQueue s_intakes = new(static buffer => ClusterBuffer.Return(buffer));
    static long s_holdSeq, s_holdUntilMs;

    /// <summary>DEALER THREAD. The Connect glue acked a <c>play</c> / <c>transfer</c> REQUEST and decoded its body with
    /// <c>Decode.ConnectLoad</c> into <paramref name="buffer"/>; this takes ownership of the buffer. A newer load still
    /// waiting supersedes an older one (its buffer goes back). The drain folds the command's claim and then starts what
    /// the body asks for, in arrival order with the mailbox — so the glue enqueues NO command item for these two verbs.</summary>
    public static void RemoteLoadArrived(in Spotify.Decode.RemoteCommand command, in RemoteLoad load, ClusterBuffer buffer,
        uint sessionEpoch)
        => Arrive(new Intake { Kind = IntakeKind.Load, Command = command, Load = load, Buffer = buffer, Epoch = sessionEpoch });

    /// <summary>DEALER THREAD. The Connect glue acked a <c>set_queue</c> / <c>update_context</c> REQUEST and decoded its body
    /// with <c>Decode.ConnectQueue</c> into <paramref name="buffer"/>; this takes ownership of the buffer. The drain folds
    /// the command (set_queue's claim, the PUT's attribution) and applies the rows behind the deck, in arrival order with
    /// the mailbox — so the glue enqueues NO command item for these two verbs either (G-074).</summary>
    public static void RemoteQueueArrived(in Spotify.Decode.RemoteCommand command, in RemoteQueue queue, ClusterBuffer buffer,
        uint sessionEpoch)
        => Arrive(new Intake { Kind = IntakeKind.Queue, Command = command, Rows = queue, Buffer = buffer, Epoch = sessionEpoch });

    static void Arrive(in Intake intake)
    {
        bool wake;
        lock (s_gate)
        {
            // Under the gate no drain dequeue is in flight, so `Pending` is exactly the mailbox items that came first.
            s_intakes.Arrive(in intake, Spotify.Connect.Pending);
            wake = !s_wakeQueued;
            s_wakeQueued = true;
        }
        if (wake) ToUi(s_drain);
    }

    /// <summary>How many Connect intakes (play / transfer / queue bodies) wait for a drain — the diagnostics row, and the
    /// dealer glue's test (G-250): a body the glue handed over is taken by the next drain, never left behind.</summary>
    public static int PendingIntakes { get { lock (s_gate) return s_intakes.Count; } }

    // ── 2. the drain's first phase (UI thread) ─────────────────────────────────────────────────────────────────────

    /// <summary>Drain `Spotify.Connect`'s bounded mailbox and the host's intakes in ARRIVAL order (see the file header).
    /// Decode results become inputs here and nowhere else.</summary>
    static void DrainConnect(long now)
    {
        StartResolved(now);
        while (true)
        {
            bool loadWaiting;
            lock (s_gate) loadWaiting = s_intakes.HasLoad;
            if (SupersedesHold(s_holdSeq, s_contextSeq, now, s_holdUntilMs, loadWaiting))
            {
                // G-244: the resolving load's start will find the sequence moved on and drop; the items that came between
                // the two loads fold now, in arrival order, and then the newer load runs.
                ++s_contextSeq;
                s_holdSeq = 0;
                Log.Info("playback", "inbound load superseded by a newer one while its context resolved");
            }
            if (HoldsMailbox(s_holdSeq, s_contextSeq, now, s_holdUntilMs)) return;
            Spotify.Connect.Item item = default;
            Intake intake;
            bool hasItem = false, hasIntake;
            lock (s_gate)
            {
                hasIntake = s_intakes.TryTake(mailboxEmpty: false, out intake);
                if (!hasIntake)
                {
                    hasItem = Spotify.Connect.TryDequeue(out item);
                    if (hasItem) s_intakes.Taken();
                    else hasIntake = s_intakes.TryTake(mailboxEmpty: true, out intake);
                }
            }
            if (hasIntake)
            {
                RunIntake(in intake, now);
                if (intake.Epoch == Spotify.Current.Epoch && intake.Command.Ok)
                    PublishCommandOutcome(in intake.Command);
            }
            else if (hasItem)
            {
                FoldItem(in item, now);
                if (item.Epoch == Spotify.Current.Epoch && item.Kind == Spotify.Connect.ItemKind.RemoteCommand && item.Command.Ok)
                    PublishCommandOutcome(in item.Command);
                // Bug I: a mirrored cluster can leave a real current row next to a queue nobody ever `Replace`d.
                if (item.Kind == Spotify.Connect.ItemKind.Cluster) SeedQueueFromCluster();
            }
            else return;
        }
    }

    static void PublishCommandOutcome(in Spotify.Decode.RemoteCommand command)
    {
        Log.Info("connect", "cmd endpoint=" + command.Kind + " outcome=published phase=" + s_state.Phase
            + " fault=" + s_state.Error + " messageId=" + command.MessageId);
        Spotify.Connect.PublishCommand(SnapshotForConnect(PublishReason.PlayerStateChanged));
    }

    /// <summary>An inbound context load's resolve answered (or was refused): the mailbox behind it may move again.</summary>
    static void ReleaseHold(long seq)
    {
        if (s_holdSeq == 0 || s_holdSeq != seq) return;
        s_holdSeq = 0;
        if (!s_draining) RequestDrain();
    }

    static void RunIntake(in Intake intake, long now)
    {
        if (intake.Buffer is not { } buffer) return;
        if (intake.Epoch != Spotify.Current.Epoch) { ClusterBuffer.Return(buffer); return; }   // a closed session's (G-036)
        if (intake.Kind == IntakeKind.Load) { RunLoad(in intake.Command, in intake.Load, buffer, now); return; }
        try { RunQueue(in intake.Command, in intake.Rows, buffer, now); }
        finally { ClusterBuffer.Return(buffer); }
    }

    /// <summary>Fold the claim, then start what the load names — its embedded rows, or the context it names resolved on an
    /// api thread (holding the mailbox until it answers), or (a transfer with no context) the remote's current row alone.
    /// The buffer is this call's to return, or the resolve's.</summary>
    static void RunLoad(in Spotify.Decode.RemoteCommand command, in RemoteLoad load, ClusterBuffer buffer, long now)
    {
        StepOne(Input.Controller(in command, now));      // the claim and its is_active announce, in THIS drain
        if (load.Kind is not (RemoteCmd.Play or RemoteCmd.Transfer)) { ClusterBuffer.Return(buffer); return; }

        long seq = ++s_contextSeq;
        var cause = load.Kind == RemoteCmd.Transfer ? ClaimCause.InboundTransfer : ClaimCause.InboundPlay;
        bool embedded = load.Kind == RemoteCmd.Play && load.TrackCount > 0;
        if (embedded || load.ContextUri.IsEmpty)
        {
            try { StartContext(seq, in load, buffer, embedded ? load.TrackStart : 0, embedded ? load.TrackCount : 0, cause, now); }
            finally { ClusterBuffer.Return(buffer); }
            return;
        }
        s_holdSeq = seq;
        s_holdUntilMs = now + InboundHoldMs;
        ResolveContext(seq, Encoding.UTF8.GetString(buffer.Utf8(load.ContextUri)), load, buffer, cause);
    }

    // ── 3. a controller's rows (G-074) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Apply a controller's rows behind the deck (<see cref="SpliceQueue"/>), never reloading what plays.
    /// <c>set_queue</c>: history and the current row stay; what follows becomes the controller's <c>next_tracks</c> — its
    /// user queue first, then the context and the autoplay tail. <c>update_context</c>: the context on the deck changed its
    /// rows; the current track and the user's still-waiting queue stay, and the rows after the current track (every row,
    /// when it is no longer in the context) follow them. An update for another context, or rows with no local deck to go
    /// behind, are ignored.</summary>
    static void RunQueue(in Spotify.Decode.RemoteCommand command, in RemoteQueue body, ClusterBuffer buffer, long now)
    {
        StepOne(Input.Controller(in command, now));      // set_queue's claim, and the PUT attributed to the sender
        if (Entities.Current is null || body.Kind is not (RemoteCmd.SetQueue or RemoteCmd.UpdateContext)) return;
        int deck = s_state.Cursor.Index;
        if (s_state.Cursor.IsNone || !FollowsQueue(in s_state, Queue.RefAt(deck)))
        {
            Log.Info("playback", "controller queue ignored: no local deck to go behind");
            return;
        }
        if (body.Kind == RemoteCmd.UpdateContext && !body.ContextUri.IsEmpty
            && !EntityId.Parse(buffer.Utf8(body.ContextUri)).Equals(s_state.Context))
        {
            Log.Info("playback", "update_context ignored: not the context on the deck");
            return;
        }

        // The kept history and deck, the kept user queue and the controller's rows: never more than twice the live run
        // plus what came in.
        int room = Math.Max(1, 2 * Queue.Count + (body.Kind == RemoteCmd.SetQueue ? body.NextCount : body.TrackCount));
        EntityRef[] refs = ArrayPool<EntityRef>.Shared.Rent(room);
        QueueEdge[] rows = ArrayPool<QueueEdge>.Shared.Rent(room);
        int[] next = ArrayPool<int>.Shared.Rent(room);
        int[] packed = ArrayPool<int>.Shared.Rent(room);
        QueueEdge[] laid = ArrayPool<QueueEdge>.Shared.Rent(room);
        try
        {
            int n = 0;
            if (body.Kind == RemoteCmd.SetQueue)
            {
                ReadOnlySpan<ClusterTrack> tracks = buffer.Tracks(body.NextStart, body.NextCount);
                for (int k = 0; k < tracks.Length; k++) AddRow(buffer, in tracks[k], QueueBucket.NextUp, refs, rows, ref n);
            }
            else
            {
                AddKeptQueue(refs, rows, ref n, room);
                ReadOnlySpan<ClusterTrack> tracks = buffer.Tracks(body.TrackStart, body.TrackCount);
                for (int k = CurrentIndexIn(buffer, tracks) + 1; k < tracks.Length; k++)
                    AddRow(buffer, in tracks[k], QueueBucket.NextUp, refs, rows, ref n);
            }
            Queue.Pack(refs.AsSpan(0, n), next);
            int written = SpliceQueue(Queue.PackedRefs, Queue.Rows, deck, next.AsSpan(0, n), rows.AsSpan(0, n),
                packed.AsSpan(0, room), laid.AsSpan(0, room));
            if (written < 0) return;
            s_orderRefs = null;                           // a saved unshuffle order no longer describes these rows
            s_orderRows = null;
            // G-245: update_context restates the context in its own order — shuffle it again (saving THAT order for an
            // unshuffle); set_queue is already the order the controller wants played.
            if (ReshufflesAfter(body.Kind, s_state.Shuffle))
                Reorder(packed.AsSpan(0, written), laid.AsSpan(0, written), deck, shuffle: true);
            Queue.Replace(packed.AsSpan(0, written), laid.AsSpan(0, written));
            s_uids.Retain(Queue.Rows);
            WatchQueue(now);                              // re-arm the next row (and announce the queue) inside this drain
            Log.Info("playback", (body.Kind == RemoteCmd.SetQueue ? "set_queue" : "update_context")
                + " applied rows=" + written + " deck=" + deck);
        }
        finally
        {
            ArrayPool<EntityRef>.Shared.Return(refs);
            ArrayPool<QueueEdge>.Shared.Return(rows);
            ArrayPool<int>.Shared.Return(next);
            ArrayPool<int>.Shared.Return(packed);
            ArrayPool<QueueEdge>.Shared.Return(laid);
        }
    }

    /// <summary>Where the row on the deck sits in a controller's context rows — its uid first, then its uri — or −1.</summary>
    static int CurrentIndexIn(ClusterBuffer buffer, ReadOnlySpan<ClusterTrack> tracks)
    {
        if (s_state.CurrentId.IsEmpty || tracks.IsEmpty || s_state.CurrentId.FormattedLength > 256) return -1;
        Span<byte> utf8 = stackalloc byte[512];                     // ASCII uris: a char is a byte, with headroom
        string uid = CurrentUid();
        TextRef uidRef = uid.Length is > 0 and <= UidBook.MaxChars ? buffer.AddText(utf8[..Encoding.UTF8.GetBytes(uid, utf8)]) : default;
        TextRef uriRef = buffer.AddText(utf8[..s_state.CurrentId.Format(utf8)]);
        return RemotePlan.StartIndex(buffer, tracks, uidRef, uriRef, -1);
    }

    // ── 4. the seed (bug I): a mirrored cluster with a real current row and no Replace ever run ───────────────────

    /// <inheritdoc cref="SeedQueueFromCluster(bool)"/>
    static void SeedQueueFromCluster() => SeedQueueFromCluster(takeover: false);

    /// <summary>A cluster item just landed and folded (<see cref="FoldCluster"/> already ran, so <c>s_state</c> and
    /// <c>s_foreign</c> are both current): if it left a real <see cref="State.HasCurrent"/> next to a queue that never
    /// saw a <c>Queue.Replace</c>, seed it — from the cluster's own prev/next tracks when the mirrored owner is
    /// Foreign and carried any, else the bare current row alone. <see cref="Queue.DecideSeed"/> is the only thing that
    /// decides; a queue that already answered "what plays next" (a real Play, or an earlier seed) is never touched —
    /// UNLESS <paramref name="takeover"/>.
    ///
    /// <para><paramref name="takeover"/> is the reducer's <c>Effects.TakeoverSeed</c> (A4), called from
    /// <c>Playback.Host.cs</c>'s <c>Execute()</c> after a <c>DoResume</c>/<c>DoPlay</c> Step claimed a row the local
    /// session never queued — a mirrored (Foreign) row that ownership has, by the time <c>Execute()</c> runs, already
    /// folded to <see cref="Owner.Us"/>. So <paramref name="takeover"/> ALSO stands in for "was Foreign a moment
    /// ago" when reading <c>s_foreign</c> — the cache <see cref="Owner.Foreign"/> would normally gate — because
    /// nothing has cleared it since (only a FRESH cluster's own <see cref="FoldCluster"/> does that), and it
    /// overrides <see cref="Queue.DecideSeed"/>'s "never re-seed a queue that left <c>EdgeState.Unknown</c>" rule so
    /// the takeover replaces whatever unrelated rows were sitting there, even with no cluster rows cached at all
    /// (then <see cref="Queue.SeedSource.CurrentOnly"/> still un-sticks the cursor).</para></summary>
    static void SeedQueueFromCluster(bool takeover)
    {
        if (Entities.Current is null) return;
        bool foreign = takeover || s_state.Own.Kind == Owner.Foreign;
        bool hasClusterTracks = foreign && (s_foreign.PrevCount > 0 || s_foreign.NextCount > 0);
        switch (Queue.DecideSeed(Queue.State, s_state.HasCurrent, hasClusterTracks, hasContext: false, takeover))
        {
            case Queue.SeedSource.Cluster:
                SeedFromForeignQueue();
                CancelArmedSeedRetry();     // a boot-time seed waiting on Online (Restore) is answered by this instead
                break;
            case Queue.SeedSource.CurrentOnly:
                SeedCurrentRow(s_state.Current);
                s_state.Cursor = Queue.CursorOf(0);      // nothing else relocates a None cursor once the queue changes
                RequestDrain();
                CancelArmedSeedRetry();     // ditto
                break;
        }
    }

    /// <summary>Build the queue from the cluster's own prev/next tracks (<see cref="s_foreign"/>, already copied out of
    /// the pooled cluster buffer by <see cref="FoldCluster"/> — safe to read here even though that buffer is long since
    /// returned). Queued rows land first (their own UserQueue bucket), then the rest as NextUp, so
    /// <see cref="Queue.IsOrdered"/> still holds. The deck's own uid is not on this wire (only its uri, resolved into
    /// <see cref="State.CurrentId"/> well before this runs), so its edge carries item id 0 — <see cref="SeedCurrentRow"/>'s
    /// own convention for a row this session did not queue itself.</summary>
    static void SeedFromForeignQueue()
    {
        EntityRef current = s_state.Current;
        if (current.IsNone) return;
        int currentPacked = Queue.Pack(current);
        if (currentPacked == 0) return;
        int prevN = s_foreign.PrevCount, nextN = s_foreign.NextCount;
        int cap = prevN + 1 + nextN;
        int[] packed = ArrayPool<int>.Shared.Rent(cap);
        QueueEdge[] rows = ArrayPool<QueueEdge>.Shared.Rent(cap);
        int deck;
        try
        {
            int n = 0;
            for (int k = 0; k < prevN; k++) AddForeignRow(k, next: false, QueueBucket.History, packed, rows, ref n);
            deck = n;
            packed[n] = currentPacked;
            rows[n] = new QueueEdge(0, (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying);
            n++;
            for (int k = 0; k < nextN; k++)
                if (s_foreign.IsQueued(true, k)) AddForeignRow(k, next: true, QueueBucket.UserQueue, packed, rows, ref n);
            for (int k = 0; k < nextN; k++)
                if (!s_foreign.IsQueued(true, k)) AddForeignRow(k, next: true, QueueBucket.NextUp, packed, rows, ref n);

            Queue.Replace(packed.AsSpan(0, n), rows.AsSpan(0, n));
            s_uids.Retain(Queue.Rows);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(packed);
            ArrayPool<QueueEdge>.Shared.Return(rows);
        }
        s_state.Cursor = Queue.CursorOf(deck);
        RequestDrain();
    }

    /// <summary>One foreign-queue row → a queue row, the same shape <see cref="AddRow"/> builds from a resolved
    /// context — a row with no playable identity (or one this scope cannot resolve) is skipped rather than queued as a
    /// hole. The provenance the wire actually carries beyond "queued or not" is not on this struct, so a non-queued row
    /// reads as Context (the common case; an autoplay tail mirrored this way shows as a context row instead).</summary>
    static void AddForeignRow(int index, bool next, QueueBucket bucket, int[] packed, QueueEdge[] rows, ref int n)
    {
        if (n >= packed.Length) return;
        ReadOnlySpan<byte> uri = s_foreign.Utf8Uri(next, index);
        if (uri.IsEmpty) return;
        EntityId id = EntityId.Parse(uri);
        if (!id.IsPlayable) return;
        int target = Queue.Pack(Entities.Ref(id));
        if (target == 0) return;
        ReadOnlySpan<byte> uid = s_foreign.Utf8Uid(next, index);
        QueueProvider provider = bucket == QueueBucket.UserQueue ? QueueProvider.Queue : QueueProvider.Context;
        packed[n] = target;
        rows[n] = new QueueEdge(uid.IsEmpty ? 0 : s_uids.ItemIdOf(uid), (byte)provider, (byte)bucket);
        n++;
    }

    // ── 5. test seam ───────────────────────────────────────────────────────────────────────────────────────────────

    static void ResetRemoteForTests()
    {
        lock (s_gate) s_intakes.Clear();
        s_holdSeq = 0;
        s_holdUntilMs = 0;
    }
}
