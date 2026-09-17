// ── Playback/Playback.Host.Wire.cs ──────────────────────────────────────────────────────────────────────────────────
// The PutState's parity half captured on the UI thread — the queue window with its rows' metadata, the session and
// playback ids, the queue revision, the private-session flag, the output device, the controller the PUT answers — the
// foreign owner's queue as its clusters state it, the queue writes forwarded to that owner, the BecameInactive a
// sign-out owes, and the deck carried across a scope switch (gap batch R4-1)
//
// Role: SHELL
// Owner: G
// Wave: gap batch R4-1
// Budget: 500 lines
// Spec: gap register G-240 (thin PutState), G-246 (command ageing), G-248 (queue forwards), G-036 (the inactive PUT on
//       sign-out), G-241 (the rebind); 0.2.9 `Backend/DeviceStatePublisher.cs` (BuildSnapshot, the ids, the revision) and
//       `Backend/PlaybackController.cs` (the set_queue / add_to_queue forwards)
//
// A NAMED PARTIAL OF `Playback.Host.cs`, declared when the loop was already past its budget and two page packages may
// still patch it. The same SHELL rules: the UI thread reads the tables and the queue (C1); everything it hands the api
// thread is a value or an immutable `WireWindow` (`Playback.Wire.cs`); the routing decision is the reducer's
// (`DoQueueToOwner`), this file only turns the forwarded verb into a body.
//
//   announce ─▶ SnapshotForConnect ─▶ CaptureWire ─┬─ WindowNow   (≤ 50 prev · deck · ≤ 50 next; REUSED while the queue,
//                                                   │               the cursor, the deck, the context and every row's
//                                                   │               table version are unchanged — so a pause, a seek or a
//                                                   │               volume drag builds nothing)
//                                                   ├─ RevisionNow (moves with the queue, the deck row and the options)
//                                                   └─ s_wireIds   (minted as a row's registration opens: WireStarted)
//
//   cluster (foreign) ─▶ s_foreign.Take ─▶ QueueToOwner ─▶ reducer Forward ─▶ ForwardQueue ─▶ add_to_queue | set_queue

using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using FluentGpu.Foundation;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using ClusterTrack = Wavee.Spotify.Decode.ClusterTrack;
using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Playback
{
    // ── 1. the capture (UI thread, per announce) ──────────────────────────────────────────────────────────────────────

    /// <summary>The parity half of the PUT about to be captured. While we are not the active device (or hold nothing) only
    /// the device facts travel; the player half is empty by construction (<see cref="Snapshot.Of"/>).</summary>
    static WireExtras CaptureWire(long frameNowMs)
    {
        string? sender = s_state.LastCommandSender != 0 && s_state.LastCommandMessageId != 0
                         && CommandAttribution.Fresh(s_state.LastCommandAtMs, frameNowMs)
            ? Devices.IdOf(s_state.LastCommandSender)
            : null;
        bool privateSession = Platform.Settings.Get(Platform.Keys.PrivateSession);
        string output = OutputDeviceName();
        if (!Ownership.IsActiveOnWire(in s_state.Own) || !s_state.HasCurrent || Entities.Current is null)
            return new WireExtras(null, 0, default, privateSession, output, sender);
        return new WireExtras(WindowNow(), RevisionNow(), in s_wireIds, privateSession, output, sender);
    }

    /// <summary>The OS endpoint we render to: the one the user chose, else the system default's row. "" when unknown —
    /// never another machine's name (0.2.9's rule).</summary>
    static string OutputDeviceName()
    {
        string chosen = Platform.Settings.Get(Platform.Keys.OutputDeviceName);
        if (chosen.Length > 0) return chosen;
        foreach (Audio.LocalAudioDevice device in Audio.Devices.Peek())
            if (device.IsDefault) return device.Name ?? "";
        return "";
    }

    static WireWindow? s_window;
    static Scope? s_windowScope;
    static uint s_windowQueue;
    static int s_windowCursor = int.MinValue;
    static EntityId s_windowCurrent, s_windowContext;
    static ulong s_windowRows;

    /// <summary>The queue around the deck. Rebuilt only when what it would contain changed; otherwise the SAME immutable
    /// window is handed to the next snapshot — which is what keeps a pause, a seek and a volume drag allocation-free.</summary>
    static WireWindow WindowNow()
    {
        Scope scope = Entities.Current!;
        ReadOnlySpan<QueueEdge> rows = Queue.Rows;
        int cursor = s_state.Cursor.Index;
        // Only a queue laid under the deck names its neighbours: a replace the reducer has not folded yet names nothing.
        bool laid = FollowsQueue(in s_state, Queue.RefAt(cursor)) && (uint)cursor < (uint)rows.Length;
        int first = laid ? Math.Max(0, cursor - WireWindow.MaxPrev) : 0;
        int end = laid ? Math.Min(rows.Length, cursor + 1 + WireWindow.MaxNext) : 0;

        ulong versions = RowVersion(s_state.Current);
        for (int k = first; k < end; k++) versions = (versions ^ RowVersion(Queue.RefAt(k))) * 1099511628211UL;
        uint queueVersion = Queue.Version;
        int windowCursor = laid ? cursor : -1;
        if (s_window is { } kept && ReferenceEquals(scope, s_windowScope) && queueVersion == s_windowQueue
            && windowCursor == s_windowCursor && s_state.CurrentId.Equals(s_windowCurrent)
            && s_state.Context.Equals(s_windowContext) && versions == s_windowRows)
            return kept;

        int prevCount = laid ? cursor - first : 0;
        int nextCount = laid ? end - cursor - 1 : 0;
        WireRow[] scratch = ArrayPool<WireRow>.Shared.Rent(Math.Max(1, prevCount + nextCount));
        try
        {
            int n = 0;
            for (int k = first; k < first + prevCount; k++) scratch[n++] = RowOf(Queue.RefAt(k), in rows[k]);
            for (int k = cursor + 1; laid && k < end; k++) scratch[n++] = RowOf(Queue.RefAt(k), in rows[k]);
            QueueEdge deck = laid ? rows[cursor] : new QueueEdge(0, (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying);
            WireRow current = RowOf(s_state.Current, in deck, s_state.CurrentId);
            int contextIndex = 0;
            for (int k = 0; laid && k < cursor; k++) if (rows[k].Provider == (byte)QueueProvider.Context) contextIndex++;
            s_window = new WireWindow(in current, scratch.AsSpan(0, prevCount), scratch.AsSpan(prevCount, nextCount), contextIndex);
        }
        finally { ArrayPool<WireRow>.Shared.Return(scratch, clearArray: true); }

        s_windowScope = scope;
        s_windowQueue = queueVersion;
        s_windowCursor = windowCursor;
        s_windowCurrent = s_state.CurrentId;
        s_windowContext = s_state.Context;
        s_windowRows = versions;
        return s_window;
    }

    /// <summary>A row's table version with its slot, so a row that moved and a row that changed both move the key.</summary>
    static ulong RowVersion(EntityRef row)
    {
        if (row.IsNone) return 0;
        Table? table = Entities.TableFor(row.Kind);
        uint version = table is not null && (uint)row.Slot < (uint)table.Count ? table.Version[row.Slot] : 0;
        return ((ulong)(uint)row.Slot << 32) | version;
    }

    /// <summary>One queue row → its wire row: the identity, the uid exactly as it was booked, the provenance, and the display
    /// picks the catalog already holds (a row it has not fetched carries its uri alone, as 0.2.9's placeholder did).</summary>
    static WireRow RowOf(EntityRef row, in QueueEdge edge, EntityId id = default)
    {
        if (id.IsEmpty) id = row.Id;
        var provider = (QueueProvider)edge.Provider;
        string? uid = s_uids.TextOf(edge.ItemId);
        Scope? scope = Entities.Current;
        if (row.IsNone || scope is null) return new WireRow(id, edge.ItemId, uid, provider);

        if (row.Kind == EntityKind.Track && (uint)row.Slot < (uint)scope.Tracks.Count)
        {
            var track = new Track(row.Slot);
            string? artistName = null, albumTitle = null, image = Interned(track.ImageId);
            EntityId artistId = default, albumId = default;
            ReadOnlySpan<int> artists = track.ArtistSlots;
            if (artists.Length > 0 && artists[0] > 0 && artists[0] < scope.Artists.Count)
            {
                var artist = new Artist(artists[0]);
                artistName = Interned(artist.NameId);
                artistId = artist.Id;
            }
            int container = track.AlbumSlot;
            if (container > 0 && track.IsPodcast && container < scope.Shows.Count)
            {
                var show = new Show(container);
                albumTitle = Interned(show.TitleId);
                albumId = show.Id;
                image ??= Interned(show.ImageId);
            }
            else if (container > 0 && !track.IsPodcast && container < scope.Albums.Count)
            {
                var album = new Album(container);
                albumTitle = Interned(album.TitleId);
                albumId = album.Id;
                image ??= Interned(album.ImageId);
            }
            return new WireRow(id, edge.ItemId, uid, provider, Interned(track.TitleId), artistName, albumTitle, image, albumId, artistId);
        }

        if (row.Kind == EntityKind.Episode && (uint)row.Slot < (uint)scope.Episodes.Count)
        {
            var episode = new Episode(row.Slot);
            Show show = episode.Show;
            bool known = show.Slot > 0 && show.Slot < scope.Shows.Count;
            string? showTitle = known ? Interned(show.TitleId) : null;
            string? image = Interned(episode.ImageId) ?? (known ? Interned(show.ImageId) : null);
            return new WireRow(id, edge.ItemId, uid, provider, Interned(episode.TitleId), showTitle, showTitle, image,
                known ? show.Id : default);
        }
        return new WireRow(id, edge.ItemId, uid, provider);
    }

    static string? Interned(StringId id) => id.IsEmpty ? null : Entities.Strings.Resolve(id);

    // ── 2. the queue revision and the ids ─────────────────────────────────────────────────────────────────────────────

    static ulong s_revision;
    static Scope? s_revisionScope;
    static uint s_revisionQueue;
    static int s_revisionCursor;
    static EntityId s_revisionCurrent;
    static bool s_revisionShuffle;
    static RepeatMode s_revisionRepeat;

    /// <summary><c>queue_revision</c>: a random 64-bit start (0.2.9), moved by one whenever the queue, the deck row, the
    /// shuffle or the repeat mode changed since the last capture — exactly when a controller's cached queue went stale.</summary>
    static ulong RevisionNow()
    {
        Scope? scope = Entities.Current;
        uint queueVersion = scope is null ? 0 : Queue.Version;
        bool same = s_revision != 0 && ReferenceEquals(scope, s_revisionScope) && queueVersion == s_revisionQueue
                    && s_state.Cursor.Index == s_revisionCursor && s_state.CurrentId.Equals(s_revisionCurrent)
                    && s_state.Shuffle == s_revisionShuffle && s_state.Repeat == s_revisionRepeat;
        if (same) return s_revision;
        s_revision = s_revision == 0 ? (ulong)Random.Shared.NextInt64(1, long.MaxValue) : s_revision + 1;
        s_revisionScope = scope;
        s_revisionQueue = queueVersion;
        s_revisionCursor = s_state.Cursor.Index;
        s_revisionCurrent = s_state.CurrentId;
        s_revisionShuffle = s_state.Shuffle;
        s_revisionRepeat = s_state.Repeat;
        return s_revision;
    }

    static WireIds s_wireIds;
    static EntityId s_wireContext;

    /// <summary>A row's audio began (the play report's Started, UI thread): a new <c>playback_id</c> for it, and a new
    /// session — <c>session_id</c>, <c>session_command_id</c>, the interaction and page-instance ids — for a new play or a
    /// new context (0.2.9: <c>Started</c>, or the context changed). An advance inside the context keeps the session.</summary>
    static void WireStarted(EntityId context, PlayReason why)
    {
        bool fresh = s_wireIds.SessionId == UInt128.Zero || !context.Equals(s_wireContext)
                     || why is PlayReason.ClickRow or PlayReason.Remote or PlayReason.PlayButton;
        UInt128 playback = Random128();
        s_wireIds = fresh
            ? new WireIds(Random128(), playback, Random128(), Random128(), Random128())
            : s_wireIds with { PlaybackId = playback };
        s_wireContext = context;
    }

    /// <summary>The wire's <c>playback_id</c> as the 16 bytes the play registration is keyed by — one id for both, as 0.2.9
    /// had it (<c>e.Ids.PlaybackIdHex</c>).</summary>
    static byte[] PlaybackIdBytes()
    {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, s_wireIds.PlaybackId == UInt128.Zero ? Random128() : s_wireIds.PlaybackId);
        return bytes;
    }

    static UInt128 Random128()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        UInt128 value = BinaryPrimitives.ReadUInt128BigEndian(bytes);
        return value == UInt128.Zero ? UInt128.One : value;
    }

    // ── 3. the foreign owner's queue and the forwards (G-248) ─────────────────────────────────────────────────────────

    /// <summary>The queue the foreign owner's last cluster stated — the rows a forwarded <c>set_queue</c> must echo back
    /// verbatim (uid and provenance kept), and the revision it must quote. Copied out of the pooled cluster buffer into
    /// grow-only arrays, so a heartbeat allocates nothing after the first push. UI thread.</summary>
    sealed class ForeignQueue
    {
        public const int MaxRows = 128;

        byte[] _text = new byte[8192];
        int _textLength, _revisionStart, _revisionLength;
        readonly (int Uri, int UriLength, int Uid, int UidLength, bool Queued)[] _rows = new (int, int, int, int, bool)[2 * MaxRows];

        public int PrevCount { get; private set; }
        public int NextCount { get; private set; }

        public void Clear() { _textLength = 0; _revisionLength = 0; PrevCount = 0; NextCount = 0; }

        public void Take(ClusterBuffer buffer, in Spotify.Decode.ClusterDelta delta)
        {
            Clear();
            (_revisionStart, _revisionLength) = Add(buffer.Utf8(delta.QueueRevision));
            ReadOnlySpan<ClusterTrack> prev = buffer.Tracks(delta.PrevStart, delta.PrevCount);
            ReadOnlySpan<ClusterTrack> next = buffer.Tracks(delta.NextStart, delta.NextCount);
            int p = Math.Min(prev.Length, MaxRows), q = Math.Min(next.Length, MaxRows);
            for (int k = prev.Length - p; k < prev.Length; k++) Put(PrevCount++, buffer, in prev[k]);
            for (int k = 0; k < q; k++) Put(MaxRows + NextCount++, buffer, in next[k]);
        }

        void Put(int slot, ClusterBuffer buffer, in ClusterTrack track)
        {
            var (uri, uriLength) = Add(buffer.Utf8(track.Uri));
            var (uid, uidLength) = Add(buffer.Utf8(track.Uid));
            _rows[slot] = (uri, uriLength, uid, uidLength, buffer.Utf8(track.Provider).SequenceEqual("queue"u8));
        }

        (int, int) Add(ReadOnlySpan<byte> utf8)
        {
            if (_textLength + utf8.Length > _text.Length) Array.Resize(ref _text, Math.Max(_text.Length * 2, _textLength + utf8.Length));
            utf8.CopyTo(_text.AsSpan(_textLength));
            int start = _textLength;
            _textLength += utf8.Length;
            return (start, utf8.Length);
        }

        /// <summary><c>queue_revision</c> as the uint64 it is on the wire (0 when the cluster stated none).</summary>
        public ulong Revision
            => Utf8Parser.TryParse(_text.AsSpan(_revisionStart, _revisionLength), out ulong value, out _) ? value : 0UL;

        /// <summary>The rows as the body writer takes them — COLD (a user's queue click): strings made here.</summary>
        public Spotify.Connect.QueueWireRow[] Rows(bool next)
        {
            int count = next ? NextCount : PrevCount, offset = next ? MaxRows : 0;
            var rows = new Spotify.Connect.QueueWireRow[count];
            for (int k = 0; k < count; k++)
            {
                var r = _rows[offset + k];
                rows[k] = new Spotify.Connect.QueueWireRow(Encoding.UTF8.GetString(_text, r.Uri, r.UriLength),
                    Encoding.UTF8.GetString(_text, r.Uid, r.UidLength), r.Queued);
            }
            return rows;
        }

        /// <summary>One row's uri, RAW — no string until a caller actually needs one. What bug I's cluster seed reads
        /// (`Playback.Host.Remote.cs`'s <c>SeedFromForeignQueue</c>) to pack a queue row without paying for
        /// <see cref="Rows"/>'s whole array of strings first.</summary>
        public ReadOnlySpan<byte> Utf8Uri(bool next, int index) => Slice(next, index, uid: false);

        /// <inheritdoc cref="Utf8Uri(bool,int)"/>
        public ReadOnlySpan<byte> Utf8Uid(bool next, int index) => Slice(next, index, uid: true);

        /// <summary>Was this row the mirrored device's OWN user queue ("Next in queue"), by its wire provenance?</summary>
        public bool IsQueued(bool next, int index) => _rows[(next ? MaxRows : 0) + index].Queued;

        ReadOnlySpan<byte> Slice(bool next, int index, bool uid)
        {
            var r = _rows[(next ? MaxRows : 0) + index];
            return uid ? (r.UidLength > 0 ? _text.AsSpan(r.Uid, r.UidLength) : default) : _text.AsSpan(r.Uri, r.UriLength);
        }
    }

    static readonly ForeignQueue s_foreign = new();

    /// <summary>The rows staged for a forward; the reducer's effect names a run of them (offset in the high word).</summary>
    static readonly List<EntityId> s_forward = new();

    /// <summary>"Add to queue" / "Play next" while ANOTHER device owns playback (G-248): the rows go to that device as a
    /// Connect command instead of into our stale local queue. UI thread. True when the rows were handed to the reducer to
    /// forward (the owner is foreign and at least one row is a Spotify playable) — the caller's "added" toast; false when
    /// playback routes locally and the caller writes its own queue.</summary>
    public static bool QueueToOwner(ReadOnlySpan<EntityRef> rows, bool next)
    {
        if (s_state.Own.Kind != Owner.Foreign || rows.IsEmpty) return false;
        int offset = s_forward.Count;
        foreach (EntityRef row in rows)
        {
            EntityId id = row.Id;
            if (id.IsPlayable && id.Provider == EntityProvider.Spotify) s_forward.Add(id);
        }
        int count = s_forward.Count - offset;
        if (count == 0) return false;
        Post(Input.QueueToOwner(count, next, offset, FrameNowMs()));
        return true;
    }

    /// <summary>The reducer forwarded a staged run (<see cref="DoQueueToOwner"/>): one track appended is <c>add_to_queue</c>;
    /// otherwise a <c>set_queue</c> of the owner's own queue with the run spliced in — at the head of its queued rows for
    /// "play next", after them for an append (0.2.9's splice). The body is built here, sent on an api thread.</summary>
    static void ForwardQueue(string target, RemoteCmd cmd, long arg, bool next)
    {
        int offset = (int)(arg >> 32), count = (int)(arg & 0xFFFF_FFFF);
        if (offset < 0 || count <= 0 || offset + count > s_forward.Count) return;
        if (cmd == RemoteCmd.AddToQueue)
        {
            string uri = s_forward[offset].Text;
            Spotify.Api.Run(() => Spotify.Connect.AddToQueue(target, uri, CancellationToken.None));
            return;
        }
        var added = new Spotify.Connect.QueueWireRow[count];
        for (int k = 0; k < count; k++) added[k] = new Spotify.Connect.QueueWireRow(s_forward[offset + k].Text, "", Queued: true);
        Spotify.Connect.QueueWireRow[] prev = s_foreign.Rows(next: false);
        Spotify.Connect.QueueWireRow[] rows = Spotify.Connect.SpliceQueued(s_foreign.Rows(next: true), added, next ? 0 : int.MaxValue);
        ulong revision = s_foreign.Revision;
        Spotify.Api.Run(() => Spotify.Connect.SetQueue(target, revision, prev, rows, CancellationToken.None));
    }

    // ── 4. the BecameInactive a sign-out owes (G-036) ─────────────────────────────────────────────────────────────────

    /// <summary>UI thread, BEFORE the sign-out is folded (<c>Spotify.Connect.RetireThen</c>): the inactive PUT this device
    /// owes the cluster when playback is OURS. False — nothing owed — when another device or nobody owns playback. The
    /// reducer's own <c>Release(Logout)</c> still runs after; its announce finds no connection and sends nothing twice.</summary>
    public static bool RetireForSignOut(out Snapshot inactive)
    {
        inactive = default;
        if (s_state.Own.Kind != Owner.Us) return false;
        var device = new WireExtras(null, 0, default, Platform.Settings.Get(Platform.Keys.PrivateSession), OutputDeviceName(), null);
        inactive = Snapshot.Retiring(in s_state, in s_identity, UnixNowMs(), in device);
        return true;
    }

    // ── 5. the deck across a scope switch (G-241) ─────────────────────────────────────────────────────────────────────

    /// <summary>The scope the deck's and the queue's SLOTS belong to. A slot means nothing in another scope's table.</summary>
    static Scope? s_boundScope;

    /// <summary>Carry the deck across an <c>Entities.Switch</c> (G-241). The boot scope has no market and tier 0, so the
    /// first welcome ALWAYS switches — and the launch restore (or anything played from store-warmed pages before the login)
    /// lived in the scope that just went: the new scope's queue is empty and <c>State.Current</c> names a slot in an
    /// unrelated table. This re-lays the queue from the retired scope's rows BY IDENTITY (their item ids, uids and buckets
    /// kept), re-slots the deck row and its cursor, and lets the next drain re-arm what plays next — and it is what the
    /// window above reads, so a PUT never names another scope's rows. A Boot is not a switch (its scope epoch restarts), so
    /// a fresh graph never inherits a queue. UI thread; a reference compare when nothing switched. The drain, the hello, a
    /// held-rows play, a restore and an append call it first; <c>Spotify.Session</c>'s welcome effect should call it
    /// straight after the switch.</summary>
    public static void Rebind()
    {
        Scope? scope = Entities.Current;
        if (scope is null || ReferenceEquals(scope, s_boundScope)) return;
        Scope? retired = s_boundScope;
        s_boundScope = scope;
        if (retired is null || scope.Epoch <= retired.Epoch) return;   // a Boot, not a Switch: nothing carries over

        int cursor = s_state.Cursor.IsNone ? -1 : s_state.Cursor.Index, landed = -1, n = 0;
        ReadOnlySpan<int> targets = retired.Edges.Queue.Targets(Queue.Session);
        ReadOnlySpan<QueueEdge> edges = retired.Edges.Queue.Payload(Queue.Session);
        int count = Math.Min(targets.Length, edges.Length);
        int[] packed = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
        QueueEdge[] rows = ArrayPool<QueueEdge>.Shared.Rent(Math.Max(1, count));
        EntityRef[] laid = ArrayPool<EntityRef>.Shared.Rent(Math.Max(1, count));
        try
        {
            for (int k = 0; k < count; k++)
            {
                EntityId id = IdIn(retired, Queue.Unpack(targets[k]));
                EntityRef row = id.IsPlayable ? Entities.Ref(id) : default;
                int target = row.IsNone ? 0 : Queue.Pack(row);
                if (target == 0) continue;
                if (k == cursor) landed = n;
                packed[n] = target;
                rows[n] = edges[k];
                laid[n] = row;
                n++;
            }
            if (n > 0)
            {
                Queue.Replace(packed.AsSpan(0, n), rows.AsSpan(0, n));
                // The retired scope's fetches were refused under its epoch: ask this scope for every row, deck first.
                EnsureRestoredIdentities(laid.AsSpan(0, n), Math.Max(0, landed));
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(packed);
            ArrayPool<QueueEdge>.Shared.Return(rows);
            ArrayPool<EntityRef>.Shared.Return(laid);
        }

        s_state.Current = s_state.CurrentId.IsEmpty ? default : Entities.Ref(s_state.CurrentId);
        s_state.Cursor = landed >= 0 && Queue.RefAt(landed) == s_state.Current ? Queue.CursorOf(landed) : QueueCursor.None;
        s_orderRefs = null;                               // packed targets of the retired scope: meaningless now
        s_orderRows = null;
        s_followScope = null;
        s_queueVersion = 0;
        s_watchId = default;
        if (!s_state.CurrentId.IsEmpty) EnsureRow(s_state.CurrentId);
        Log.Info("playback", "deck rebound across a scope switch: rows=" + n + " cursor=" + s_state.Cursor.Index
            + " current=" + (s_state.Current.IsNone ? "none" : "kept"));
        if (!s_draining) { Publish(); RequestDrain(); }
    }

    /// <summary>A packed queue row's identity in a RETIRED scope — its own tables, not the current ones.</summary>
    static EntityId IdIn(Scope scope, EntityRef row)
    {
        if (row.IsNone) return default;
        Table? table = row.Kind switch
        {
            EntityKind.Track => scope.Tracks,
            EntityKind.Episode => scope.Episodes,
            _ => null,
        };
        return table is not null && (uint)row.Slot < (uint)table.Count ? table.Id[row.Slot] : default;
    }

    // ── 6. test seam ──────────────────────────────────────────────────────────────────────────────────────────────────

    static void ResetWireForTests()
    {
        s_window = null;
        s_windowScope = null;
        s_windowCursor = int.MinValue;
        s_revision = 0;
        s_revisionScope = null;
        s_wireIds = default;
        s_wireContext = default;
        s_foreign.Clear();
        s_forward.Clear();
    }
}
