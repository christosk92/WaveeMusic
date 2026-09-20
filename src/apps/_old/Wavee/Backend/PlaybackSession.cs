using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Wavee.Core;

namespace Wavee.Backend;

// ── The durable playback session — successor of QueueCore (F1–F11) ───────────────────────────────────────────────────
// One pure, single-threaded object. The SESSION is durable; user actions are cursor moves and edits within it (never a
// teardown-and-rebuild from the visible window). Every state transition returns the single atomic QueueSnapshot that the
// bridge/projection publishes — one truth, one revision. Ids are minted once at insertion and survive
// reorder/remove/continuation-append; q-uids are minted at add (the active device mints — FIXTURE-C); the full resolved
// context is held (no display caps live here — windowing is a bridge concern). QueueCore still exists until Integration
// swaps the controller over.

/// <summary>One resolved item in the session: a stable id + the domain track + its context uid + provider + row kind +
/// the context it came from + wire metadata. Immutable; the id is identity (position is presentation).</summary>
sealed record SessionItem(
    QueueItemId Id, Track Track, string Uid, QueueProvider Provider,
    QueueRowKind Kind, string? SourceContextUri,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>The one atomic read shape — serves the local AND viewer paths. Revision is a local monotonic bumped on every
/// mutation; <see cref="Current"/> is THE single source of "current"; the buckets are the full resolved state (windowing
/// is applied downstream, never here). <see cref="ClusterQueueRevision"/> is the cluster's string revision, echoed on an
/// outbound set_queue. <see cref="ContextCursor"/> is the context index the current row resides at (-1 = current stands
/// outside the context spine) — the derived CanSkipPrev and the session-snapshot writer read it off this atomic shape.</summary>
public sealed record QueueSnapshot(
    long Revision,
    string? ContextUri,
    string? AutoplayContextUri,
    QueueEntry? Current,
    ImmutableArray<QueueEntry> History,   // actually-played stack, newest last
    ImmutableArray<QueueEntry> UserQueue,
    ImmutableArray<QueueEntry> Upcoming,  // context + autoplay rows after the cursor (providers mark them)
    bool Shuffle, RepeatMode Repeat,
    string ClusterQueueRevision,
    int ContextCursor = -1);

public sealed class PlaybackSession
{
    /// <summary>Actually-played rows retained (display windowing is a bridge concern — see §4.9).</summary>
    public const int HistoryCap = 32;

    readonly List<SessionItem> _context = new();       // the PLAY order (context rows + autoplay tail + markers)
    readonly List<SessionItem> _naturalOrder = new();  // the natural order — kept so shuffle can be turned back OFF
    readonly List<SessionItem> _userQueue = new();     // q# — drains before the context, cursor unmoved
    readonly List<SessionItem> _history = new();       // actually-played, newest last (cap HistoryCap)
    int _cursor = -1;                                  // index into _context of the resident context position (-1 = none)
    SessionItem? _current;                             // THE single source of "current" (may be a queue/history replay)
    ulong _nextItemId = 1;                             // 0 is QueueItemId.None — never mint it
    int _nextQueueUid;                                 // q{n} minting cursor (active device mints — FIXTURE-C)
    int _seedState = 0x5DEECE66 & 0x7fffffff;          // shuffle LCG (deterministic, replayable — no Random)
    long _revision;
    string? _contextUri;
    string? _autoplayContextUri;
    bool _shuffle;
    RepeatMode _repeat;
    string _clusterRevision = "";

    public string? ContextUri => _contextUri;
    public bool Shuffle => _shuffle;
    public RepeatMode Repeat => _repeat;
    public Track? Current => _current?.Track;

    /// <summary>Playable context rows remaining after the cursor (drives the continuation-prefetch trigger). Excludes the
    /// user queue and non-surfaced markers.</summary>
    public int RemainingInContext
    {
        get
        {
            int n = 0;
            for (int i = _cursor + 1; i < _context.Count; i++) if (_context[i].Kind == QueueRowKind.Playable) n++;
            return n;
        }
    }

    /// <summary>The next playable track that would surface on advance (user queue head → context row after cursor) — for
    /// fast-track warm-up. Null at the end of the resolved session.</summary>
    public QueueEntry? PreviewNext()
    {
        if (_repeat == RepeatMode.Track && _current is { Kind: QueueRowKind.Playable } repeated)
            return ToEntry(repeated, QueueBucket.NextUp);

        for (int i = 0; i < _userQueue.Count; i++)
            if (_userQueue[i].Kind == QueueRowKind.Playable)
                return ToEntry(_userQueue[i], QueueBucket.UserQueue);

        for (int i = _cursor + 1; i < _context.Count; i++)
        {
            var item = _context[i];
            if (item.Kind == QueueRowKind.Delimiter)
            {
                if (IsPauseDelimiter(item.Metadata)) return null;
                continue;
            }
            if (item.Kind == QueueRowKind.Playable) return ToEntry(item, QueueBucket.NextUp);
        }

        if (_repeat == RepeatMode.Context)
        {
            for (int i = 0; i < _context.Count; i++)
            {
                var item = _context[i];
                if (item.Kind == QueueRowKind.Delimiter) break;
                if (item.Kind == QueueRowKind.Playable) return ToEntry(item, QueueBucket.NextUp);
            }
        }
        return null;
    }

    public Track? PeekNext() => PreviewNext()?.Track;

    /// <summary>The current track's uri + the most-recently-played uris (newest first), for the autoplay seed request.</summary>
    public IReadOnlyList<string> RecentUris(int max)
    {
        if (max <= 0) return Array.Empty<string>();
        var list = new List<string>(max);
        if (_current is { } cur && !string.IsNullOrEmpty(cur.Track.Uri)) list.Add(cur.Track.Uri);
        for (int i = _history.Count - 1; i >= 0 && list.Count < max; i--)
            if (!string.IsNullOrEmpty(_history[i].Track.Uri)) list.Add(_history[i].Track.Uri);
        return list;
    }

    // ── session lifecycle ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Seed the context (context-resolve / inbound play). keepUserQueue defaults true everywhere — starting a new
    /// context keeps the user queue (Spotify parity, FIXTURE-A); only an explicit transfer-in passes false. History is
    /// reset (a fresh context has no play history).</summary>
    public QueueSnapshot SetContext(string uri, IReadOnlyList<QueuedTrack> tracks, int startIndex, bool keepUserQueue = true)
    {
        _naturalOrder.Clear();
        _context.Clear();
        _history.Clear();
        _autoplayContextUri = null;
        _contextUri = uri;
        foreach (var q in tracks)
        {
            var it = new SessionItem(MintId(), q.Track, q.Uid, QueueProviderExtensions.FromWire(q.Provider),
                q.RowKind, uri, q.Metadata);
            _naturalOrder.Add(it);
            _context.Add(it);
        }
        if (!keepUserQueue) _userQueue.Clear();
        _cursor = _context.Count == 0 ? -1 : FirstPlayableFrom(Math.Clamp(startIndex, 0, _context.Count - 1));
        _current = _cursor >= 0 && _cursor < _context.Count ? _context[_cursor] : null;
        if (_shuffle) ReshuffleAnchoringCurrent();
        return Bump();
    }

    /// <summary>
    /// Refresh a context without replacing the playing item. History, user queue, options and the current host/timeline
    /// remain owned by the controller. If the current row disappeared it stays current outside the refreshed context and
    /// the first resolved row becomes the next item.
    /// </summary>
    public QueueSnapshot ReplaceContextPreservingCurrent(string uri, IReadOnlyList<QueuedTrack> tracks)
    {
        var priorCurrent = _current;
        _naturalOrder.Clear();
        _context.Clear();
        _autoplayContextUri = null;
        _contextUri = uri;

        int matched = -1;
        for (int i = 0; i < tracks.Count; i++)
        {
            var q = tracks[i];
            bool same = priorCurrent is { } current
                && ((!string.IsNullOrEmpty(current.Uid) && current.Uid == q.Uid)
                    || current.Track.Uri == q.Uri);
            var item = same && matched < 0
                ? new SessionItem(priorCurrent!.Id, q.Track, q.Uid, QueueProviderExtensions.FromWire(q.Provider),
                    q.RowKind, uri, q.Metadata)
                : new SessionItem(MintId(), q.Track, q.Uid, QueueProviderExtensions.FromWire(q.Provider),
                    q.RowKind, uri, q.Metadata);
            if (same && matched < 0) matched = i;
            _naturalOrder.Add(item);
            _context.Add(item);
        }

        if (matched >= 0)
        {
            _cursor = matched;
            _current = _context[matched];
            if (_shuffle) ReshuffleAnchoringCurrent();
        }
        else
        {
            _cursor = -1;
            _current = priorCurrent;
            if (_shuffle) ShuffleWithoutAnchor();
        }
        return Bump();
    }

    /// <summary>Seed a transferred context while allowing its current track to live outside the resolved row set.
    /// <paramref name="missCursor"/> applies only on an identity MISS (the current is patched outside the spine): it is
    /// the cursor to park at so <see cref="Next"/> advances to the first resolved row AFTER where the patched current sat
    /// in the saved order — never back to context[0] (playback-restore fix §6.5). -1 (unknown position) keeps the
    /// head-of-context advance.</summary>
    public QueueSnapshot SetTransferredContext(
        string uri,
        IReadOnlyList<QueuedTrack> tracks,
        in QueuedTrack transferredCurrent,
        bool clearUserQueue,
        int missCursor = -1)
    {
        _naturalOrder.Clear();
        _context.Clear();
        _history.Clear();
        _autoplayContextUri = null;
        _contextUri = uri;
        if (clearUserQueue) _userQueue.Clear();

        int matched = -1;
        for (int i = 0; i < tracks.Count; i++)
        {
            var q = tracks[i];
            var item = new SessionItem(MintId(), q.Track, q.Uid, QueueProviderExtensions.FromWire(q.Provider),
                q.RowKind, uri, q.Metadata);
            _naturalOrder.Add(item);
            _context.Add(item);
            if (matched < 0
                && ((!string.IsNullOrEmpty(transferredCurrent.Uid) && transferredCurrent.Uid == q.Uid)
                    || transferredCurrent.Uri == q.Uri))
                matched = i;
        }

        if (matched >= 0)
        {
            _cursor = matched;
            _current = _context[matched];
            if (_shuffle) ReshuffleAnchoringCurrent();
        }
        else
        {
            _cursor = missCursor >= 0 && _context.Count > 0 ? Math.Min(missCursor, _context.Count - 1) : -1;
            _current = new SessionItem(MintId(), transferredCurrent.Track, transferredCurrent.Uid,
                QueueProviderExtensions.FromWire(transferredCurrent.Provider), transferredCurrent.RowKind, uri,
                transferredCurrent.Metadata);
            if (_shuffle) ShuffleWithoutAnchor();
        }
        return Bump();
    }

    /// <summary>Linear identity comparison; context sizes are data-driven and never capped by captured playlist lengths.</summary>
    public bool HasSameContextRows(string uri, IReadOnlyList<QueuedTrack> tracks)
    {
        if (!string.Equals(_contextUri, uri, StringComparison.Ordinal) || _naturalOrder.Count != tracks.Count) return false;
        for (int i = 0; i < tracks.Count; i++)
        {
            var left = _naturalOrder[i];
            var right = tracks[i];
            if (!string.Equals(left.Track.Uri, right.Uri, StringComparison.Ordinal)
                || !string.Equals(left.Uid, right.Uid, StringComparison.Ordinal)
                || left.Kind != right.RowKind
                || !string.Equals(left.Provider.ToWire(), right.Provider, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>Relabel the context uri (server-side context patch / rename) without touching the resolved rows.</summary>
    public QueueSnapshot RelabelContext(string uri) { _contextUri = uri; return Bump(); }

    /// <summary>Append a resolved continuation page (lazy paging) or the autoplay-station tail. Autoplay pages set
    /// <see cref="QueueSnapshot.AutoplayContextUri"/> from the source uri.</summary>
    public QueueSnapshot AppendContextPage(IReadOnlyList<QueuedTrack> tracks, QueueProvider provider, string? sourceContextUri)
    {
        foreach (var q in tracks)
        {
            var it = new SessionItem(MintId(), q.Track, q.Uid, provider, q.RowKind, sourceContextUri, q.Metadata);
            _naturalOrder.Add(it);
            _context.Add(it);
        }
        if (provider == QueueProvider.Autoplay && !string.IsNullOrEmpty(sourceContextUri)) _autoplayContextUri = sourceContextUri;
        return Bump();
    }

    /// <summary>Apple-Music-style "start radio after current" (radio-inspiredby-mix-design §5.4): park a resolved radio
    /// context so the CURRENTLY-PLAYING track finishes first, then playback flows into the radio on track-end — WITHOUT
    /// reloading the audio host. The current track's identity is preserved so no reload happens; only ContextUri + the
    /// up-next change. Two cases (both keep the "cursor points at current" invariant a raw -1 park cannot express):
    /// <list type="bullet">
    /// <item>current track IS a radio row (the classic song-radio case — the seed sits at radio[k]) → context = radio,
    /// cursor = k; the next advance lands on radio[k+1], skipping the duplicate seed;</item>
    /// <item>current track is absent from the radio (artist radio / a station that doesn't lead with the seed) → the
    /// current track is prepended and context = [current] ++ radio, cursor = 0; the next advance yields radio[0].</item>
    /// </list>
    /// Called only while a track is playing locally (the idle case plays the radio immediately instead).</summary>
    public QueueSnapshot SwitchContextAfterCurrent(string uri, IReadOnlyList<QueuedTrack> radioTracks)
    {
        var current = _current;
        _naturalOrder.Clear();
        _context.Clear();
        _history.Clear();
        _autoplayContextUri = null;
        _contextUri = uri;

        // Does the still-playing track sit inside the radio list? Match by uri (the seed identity) — uid-agnostic.
        int k = -1;
        if (current is { } cur && !string.IsNullOrEmpty(cur.Track.Uri))
            for (int i = 0; i < radioTracks.Count; i++)
                if (radioTracks[i].Uri == cur.Track.Uri) { k = i; break; }

        // current NOT in radio → prepend it (identity preserved) so it plays out, then radio[0] on the next advance.
        if (k < 0 && current is { } keep)
        {
            _naturalOrder.Add(keep);
            _context.Add(keep);
        }
        foreach (var q in radioTracks)
        {
            var it = new SessionItem(MintId(), q.Track, q.Uid, QueueProviderExtensions.FromWire(q.Provider),
                q.RowKind, uri, q.Metadata);
            _naturalOrder.Add(it);
            _context.Add(it);
        }

        _cursor = _context.Count == 0 ? -1 : (k >= 0 ? k : 0);
        // _current becomes the resident row at the cursor (a radio row whose uri == the playing track in the in-radio
        // case, or the prepended current itself). Its uri is unchanged in BOTH cases → the controller never reloads audio.
        _current = _cursor >= 0 && _cursor < _context.Count ? _context[_cursor] : current;
        if (_shuffle) ReshuffleAnchoringCurrent();
        return Bump();
    }

    // ── cursor moves (skip-in-place; the session survives) ───────────────────────────────────────────────────────────

    /// <summary>Skip to an item by its stable id — routes to the Upcoming / UserQueue / History semantics (§4.3-4.5).
    /// Returns null on an identity miss (caller patches per §7.3.2) or a non-playable target.</summary>
    public QueueSnapshot? SkipToItem(QueueItemId id)
    {
        if (id.IsNone) return null;
        int k = _userQueue.FindIndex(x => x.Id == id);
        if (k >= 0) return SkipToUserQueueIndex(k);
        int j = _context.FindIndex(x => x.Id == id);
        if (j >= 0)
        {
            if (_context[j].Kind != QueueRowKind.Playable) return null;
            return j > _cursor ? SkipToUpcomingIndex(j) : SkipToContextBackIndex(j);
        }
        int h = _history.FindIndex(x => x.Id == id);
        if (h >= 0) return SkipToHistoryIndex(h);
        return null;
    }

    /// <summary>Skip by context uid (inbound next_track / remote row click) — uid-first identity, uri fallback. Returns
    /// null on a miss (caller may patch the clicked track in as current per §7.3.2).</summary>
    public QueueSnapshot? SkipToUid(string uid, string? uriFallback)
    {
        var byUid = LocateByUid(uid);
        if (byUid is { } r1) return SkipToLocated(r1);
        var byUri = LocateByUri(uriFallback);
        if (byUri is { } r2) return SkipToLocated(r2);
        return null;
    }

    /// <summary>Advance: user queue head → context row after cursor → autoplay tail → (repeat modes) → session end. A
    /// Delimiter row stops advance (advancing_past_track:"pause"); PageMarker/Delimiter rows are never surfaced (§4.6).</summary>
    public QueueSnapshot? Next()
    {
        if (_repeat == RepeatMode.Track && _current is { Kind: QueueRowKind.Playable }) return Bump();   // repeat-one holds
        PushHistory(_current);
        while (_userQueue.Count > 0)                                                                     // user queue drains first
        {
            var it = _userQueue[0];
            _userQueue.RemoveAt(0);
            if (it.Kind == QueueRowKind.Playable) { _current = it; return Bump(); }
        }
        while (_cursor + 1 < _context.Count)                                                             // context + autoplay spine
        {
            _cursor++;
            var it = _context[_cursor];
            if (it.Kind == QueueRowKind.Delimiter) { if (IsPauseDelimiter(it.Metadata)) { _current = null; return Bump(); } continue; }   // pause-delimiter stops; unmarked is transparent (§4.6)
            if (it.Kind == QueueRowKind.PageMarker) continue;                                            // transparent marker
            _current = it;
            return Bump();
        }
        if (_repeat == RepeatMode.Context)
        {
            for (_cursor = 0; _cursor < _context.Count; _cursor++)
            {
                var it = _context[_cursor];
                if (it.Kind == QueueRowKind.Delimiter) break;
                if (it.Kind == QueueRowKind.PageMarker) continue;
                _current = it;
                return Bump();
            }
        }
        _current = null;
        return Bump();   // end of session — the reducer decides whether to request autoplay
    }

    /// <summary>Step back to the most-recently-played row (cursor-back through history; else the prior context row).</summary>
    public QueueSnapshot? Prev()
    {
        if (_history.Count > 0) return SkipToHistoryIndex(_history.Count - 1);
        if (_cursor - 1 >= 0) { _cursor = PrevPlayableFrom(_cursor - 1); _current = _cursor >= 0 ? _context[_cursor] : _current; return Bump(); }
        return null;
    }

    // ── user-queue edits ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Append tracks to the user queue; each row's uid is minted "q{n}" at add (unless it already carries one —
    /// existing rows keep their uid; the active device mints only for uid:"" rows, §7.4).</summary>
    public QueueSnapshot EnqueueUser(IReadOnlyList<QueuedTrack> tracks)
    {
        foreach (var q in tracks) _userQueue.Add(NewQueueItem(q));
        return Bump();
    }

    /// <summary>Front-insert tracks ahead of the user queue ("play next"), preserving their order; same q-uid minting rule.</summary>
    public QueueSnapshot EnqueueNextUser(IReadOnlyList<QueuedTrack> tracks) => InsertUserQueue(tracks, 0);

    /// <summary>Insert tracks into the user queue at <paramref name="index"/> (queue-relative; clamped to the queue's
    /// bounds), preserving their order — the drag-drop "drop at this slot" primitive. Index 0 IS play-next.</summary>
    public QueueSnapshot InsertUserQueue(IReadOnlyList<QueuedTrack> tracks, int index)
    {
        int at = Math.Clamp(index, 0, _userQueue.Count);
        for (int i = 0; i < tracks.Count; i++) _userQueue.Insert(at + i, NewQueueItem(tracks[i]));
        return Bump();
    }

    /// <summary>Remove a user-queue row or an UPCOMING context row (index &gt; cursor) by id. Null if the id doesn't
    /// resolve to a removable row.</summary>
    public QueueSnapshot? RemoveItem(QueueItemId id)
    {
        if (id.IsNone) return null;
        int k = _userQueue.FindIndex(x => x.Id == id);
        if (k >= 0) { _userQueue.RemoveAt(k); return Bump(); }
        int j = _context.FindIndex(x => x.Id == id);
        if (j > _cursor && j < _context.Count)
        {
            _context.RemoveAt(j);
            _naturalOrder.RemoveAll(x => x.Id == id);
            return Bump();
        }
        return null;
    }

    /// <summary>Reorder an upcoming row within its own provider section. <paramref name="newPos"/> is section-relative:
    /// user-queue rows move within the user queue; context and autoplay rows move only among upcoming playable rows
    /// with the same provider. Markers, the cursor/current row, history, and provider boundaries remain fixed.</summary>
    public QueueSnapshot? MoveItem(QueueItemId id, int newPos)
    {
        int from = _userQueue.FindIndex(x => x.Id == id);
        if (from >= 0)
        {
            var it = _userQueue[from];
            _userQueue.RemoveAt(from);
            _userQueue.Insert(Math.Clamp(newPos, 0, _userQueue.Count), it);
            return Bump();
        }

        int rawFrom = _context.FindIndex(x => x.Id == id);
        if (rawFrom <= _cursor || rawFrom >= _context.Count) return null;
        var source = _context[rawFrom];
        if (source.Kind != QueueRowKind.Playable || source.Provider == QueueProvider.Queue) return null;

        var slots = new List<int>();
        for (int i = _cursor + 1; i < _context.Count; i++)
        {
            var item = _context[i];
            if (item.Kind == QueueRowKind.Playable && item.Provider == source.Provider) slots.Add(i);
        }
        int sectionFrom = slots.IndexOf(rawFrom);
        if (sectionFrom < 0) return null;
        int sectionTo = Math.Clamp(newPos, 0, slots.Count - 1);
        if (sectionFrom == sectionTo) return null;

        int step = sectionTo > sectionFrom ? 1 : -1;
        for (int i = sectionFrom; i != sectionTo; i += step)
        {
            int left = slots[i], right = slots[i + step];
            var leftItem = _context[left];
            var rightItem = _context[right];
            (_context[left], _context[right]) = (rightItem, leftItem);

            int naturalLeft = _naturalOrder.FindIndex(x => x.Id == leftItem.Id);
            int naturalRight = _naturalOrder.FindIndex(x => x.Id == rightItem.Id);
            if (naturalLeft >= 0 && naturalRight >= 0)
                (_naturalOrder[naturalLeft], _naturalOrder[naturalRight]) =
                    (_naturalOrder[naturalRight], _naturalOrder[naturalLeft]);
        }
        return Bump();
    }

    /// <summary>Compatibility alias for callers that still address only the user queue.</summary>
    public QueueSnapshot? MoveUserItem(QueueItemId id, int newPos) => MoveItem(id, newPos);

    /// <summary>Drop the whole user queue (the panel's "Clear" affordance, §10.1) — one revision bump; the context cursor,
    /// current and history are untouched. Local-session only (no wire verb exists).</summary>
    public QueueSnapshot ClearUserQueue() { _userQueue.Clear(); return Bump(); }

    /// <summary>Drop the play history (the History section's "Clear", §10.1) — one revision bump; current + context + user
    /// queue untouched. Local-session only.</summary>
    public QueueSnapshot ClearHistory() { _history.Clear(); return Bump(); }

    // ── options ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Toggle shuffle — anchored Fisher-Yates over the remaining context (current stays put); OFF restores the
    /// natural order and resumes from current's natural position.</summary>
    public QueueSnapshot SetShuffle(bool on)
    {
        if (on == _shuffle) return Bump();
        _shuffle = on;
        if (on) ReshuffleAnchoringCurrent();
        else RestoreOriginalOrder();
        return Bump();
    }

    public QueueSnapshot SetRepeat(RepeatMode mode) { _repeat = mode; return Bump(); }

    // ── inbound / recovery ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuild the whole session from a cluster (startup recovery / becoming active) — Current from player_state
    /// (provider-aware), UserQueue from provider:"queue" OR metadata is_queued:"true" rows IN WIRE ORDER, Upcoming from
    /// the context+autoplay rows (markers kept as non-surfaced Kind rows), and History from cluster prev_tracks
    /// (oldest→newest, uid/provider preserved, cap <see cref="HistoryCap"/>) so Previous works after a restore
    /// (playback-restore fix §2). Cursor sits before Upcoming (=-1) so the current stands alone.</summary>
    public QueueSnapshot ReplaceFromCluster(ClusterDelta c, Track? hydratedCurrent)
    {
        _naturalOrder.Clear();
        _context.Clear();
        _userQueue.Clear();
        _history.Clear();
        _autoplayContextUri = null;
        _contextUri = c.ContextUri;
        _nextQueueUid = 0;

        // prev_tracks are the active session's actually-played tail, oldest first — refill History from them so a
        // restored session steps back exactly like the live one did (the old clear-without-refill is fix §2's bug).
        if (c.PrevTracks is { Count: > 0 } prev)
        {
            foreach (var p in prev)
            {
                if (RowKindOfUri(p.Uri) != QueueRowKind.Playable) continue;   // markers never enter history
                _history.Add(new SessionItem(MintId(), TrackFromRemote(p), p.Uid,
                    QueueProviderExtensions.FromWire(p.Provider), QueueRowKind.Playable, c.ContextUri, p.Metadata));
            }
            if (_history.Count > HistoryCap) _history.RemoveRange(0, _history.Count - HistoryCap);
        }

        foreach (var n in c.NextTracks)
        {
            var kind = RowKindOfUri(n.Uri);
            var provider = QueueProviderExtensions.FromWire(n.Provider);
            // A user-queued row is marked by provider:"queue" OR metadata is_queued:"true" (fix §3 — some senders omit
            // the provider; mirror the set_queue wire parse in PlaybackController.ParseWireEntries).
            bool queued = kind == QueueRowKind.Playable
                && (provider == QueueProvider.Queue || IsQueuedMeta(n.Metadata));
            if (queued)
            {
                BumpQueueCursor(n.Uid);
                _userQueue.Add(new SessionItem(MintId(), TrackFromRemote(n), n.Uid, QueueProvider.Queue, kind, null, n.Metadata));
            }
            else
            {
                // Autoplay classification accepts the provider OR the autoplay.is_autoplay metadata marker (fix §3 —
                // the same acceptance ApplySetQueue already uses).
                if (provider != QueueProvider.Autoplay && kind == QueueRowKind.Playable && IsAutoplayMeta(n.Metadata))
                    provider = QueueProvider.Autoplay;
                if (provider == QueueProvider.Autoplay && _autoplayContextUri is null) _autoplayContextUri = AutoplayCtxOf(n);
                var it = new SessionItem(MintId(), TrackFromRemote(n), n.Uid, provider, kind,
                    provider == QueueProvider.Autoplay ? _autoplayContextUri : c.ContextUri, n.Metadata);
                _naturalOrder.Add(it);
                _context.Add(it);
            }
        }
        _cursor = -1;
        if (hydratedCurrent is { } hc)
            _current = new SessionItem(MintId(), hc, c.Track.Uid, QueueProviderExtensions.FromWire(c.Track.Provider),
                QueueRowKind.Playable, c.ContextUri, c.Track.Metadata);
        else if (c.HasTrack)
            _current = new SessionItem(MintId(), TrackFromRemote(c.Track), c.Track.Uid,
                QueueProviderExtensions.FromWire(c.Track.Provider), QueueRowKind.Playable, c.ContextUri, c.Track.Metadata);
        else
            _current = null;

        _shuffle = c.Shuffle;
        _repeat = c.Repeat;
        _clusterRevision = c.QueueRevision ?? "";
        return Bump();
    }

    /// <summary>Apply an inbound set_queue: replace the user queue (provider:"queue" rows, uid-preserving; uid:"" mints)
    /// and the upcoming context/autoplay/marker rows. Local history is untouched (not driven by wire prev_tracks).
    /// Current is untouched (set_queue never changes the playing track).
    /// <para>A wire set_queue carries uri/uid only — never metadata — so a row that survives the rebuild (the common
    /// reorder case) is matched against what we already hold (by uid, then uri) and reuses its resolved
    /// <see cref="Track"/> instead of falling back to <see cref="ContextResolve.Synthetic"/>, which is what used to turn
    /// every rebuilt row into its own bare uri. A row we genuinely never held before still synthesizes.</para>
    /// <para>Separately, when the incoming context/autoplay rows are a PERMUTATION of the ones already in
    /// <see cref="_naturalOrder"/> (same identity set, just reordered — a shuffle toggle or a Connect-side reorder),
    /// the existing <see cref="SessionItem"/> instances are reused verbatim and <see cref="_naturalOrder"/> is left
    /// untouched, so a later shuffle-off (<see cref="RestoreOriginalOrder"/>) still restores the true pre-shuffle
    /// order instead of re-deriving one from whatever order this set_queue happened to carry. Only a genuinely new
    /// row set (a row added or dropped) rebuilds it.</para></summary>
    public QueueSnapshot ApplySetQueue(IReadOnlyList<QueueWireEntry> prev, IReadOnlyList<QueueWireEntry> next, string revision)
    {
        _clusterRevision = revision ?? "";

        // Index every row we already hold (by uid, then by uri) BEFORE anything is cleared.
        var byUid = new Dictionary<string, Track>();
        var byUri = new Dictionary<string, Track>();
        foreach (var it in _userQueue) IndexExistingTrack(it, byUid, byUri);
        foreach (var it in _context) IndexExistingTrack(it, byUid, byUri);

        // A row we've never held before still doesn't have to fall back to ContextResolve.Synthetic: the wire itself
        // carries display metadata inline (title/artist/album/duration/image), and the controller has already parsed
        // it into a real Track on the QueueWireEntry. Seed the SAME lookup maps ResolveExistingTrack falls back to —
        // via TryAdd, so a Track we already hold (resolved through the catalogue, possibly richer still) always wins
        // over the wire's own copy — rather than widen ResolveExistingTrack's signature for it.
        foreach (var e in prev) IndexWireTrack(e, byUid, byUri);
        foreach (var e in next) IndexWireTrack(e, byUid, byUri);

        var naturalByKey = KeyByIdentity(_naturalOrder);
        bool keepNaturalOrder = naturalByKey.Count > 0 && IsPermutationOfNaturalOrder(next, naturalByKey);

        _userQueue.Clear();
        _context.Clear();
        if (!keepNaturalOrder) _naturalOrder.Clear();
        _autoplayContextUri = null;

        foreach (var e in next)
        {
            var kind = RowKindOfUri(e.Uri);
            if (kind == QueueRowKind.Playable && e.IsQueued)
            {
                string uid = e.Uid;
                if (string.IsNullOrEmpty(uid)) uid = "q" + (_nextQueueUid++);
                else BumpQueueCursor(uid);
                var track = ResolveExistingTrack(e.Uid, e.Uri, byUid, byUri);
                _userQueue.Add(new SessionItem(MintId(), track, uid, QueueProvider.Queue, kind, null, e.Metadata));
            }
            else
            {
                var provider = kind != QueueRowKind.Playable ? QueueProvider.Context
                    : IsAutoplayMeta(e.Metadata) ? QueueProvider.Autoplay : QueueProvider.Context;
                if (provider == QueueProvider.Autoplay && _autoplayContextUri is null && e.Metadata is { } m && m.TryGetValue("context_uri", out var cu))
                    _autoplayContextUri = cu;

                SessionItem it;
                if (keepNaturalOrder && naturalByKey.TryGetValue(IdentityKey(e.Uid, e.Uri), out var reused))
                {
                    it = reused;   // same instance as in _naturalOrder — keeps their ids in lockstep (§ MoveItem/Reshuffle)
                }
                else
                {
                    var track = ResolveExistingTrack(e.Uid, e.Uri, byUid, byUri);
                    it = new SessionItem(MintId(), track, e.Uid, provider, kind, _contextUri, e.Metadata);
                    if (!keepNaturalOrder) _naturalOrder.Add(it);
                }
                _context.Add(it);
            }
        }
        _cursor = -1;
        return Bump();
    }

    static void IndexExistingTrack(SessionItem it, Dictionary<string, Track> byUid, Dictionary<string, Track> byUri)
    {
        if (!string.IsNullOrEmpty(it.Uid)) byUid[it.Uid] = it.Track;
        if (!string.IsNullOrEmpty(it.Track.Uri)) byUri.TryAdd(it.Track.Uri, it.Track);
    }

    // TryAdd only — never overwrite an entry IndexExistingTrack already placed. A row we already held wins on its own
    // (possibly catalogue-hydrated) Track; the wire's inline copy is strictly a fallback for a row we've never seen.
    static void IndexWireTrack(in QueueWireEntry e, Dictionary<string, Track> byUid, Dictionary<string, Track> byUri)
    {
        if (e.Track is not { } track) return;
        if (!string.IsNullOrEmpty(e.Uid)) byUid.TryAdd(e.Uid, track);
        if (!string.IsNullOrEmpty(e.Uri)) byUri.TryAdd(e.Uri, track);
    }

    static Track ResolveExistingTrack(string uid, string uri, Dictionary<string, Track> byUid, Dictionary<string, Track> byUri)
    {
        if (!string.IsNullOrEmpty(uid) && byUid.TryGetValue(uid, out var t1)) return t1;
        if (!string.IsNullOrEmpty(uri) && byUri.TryGetValue(uri, out var t2)) return t2;
        return ContextResolve.Synthetic(uri);
    }

    static Dictionary<string, SessionItem> KeyByIdentity(List<SessionItem> items)
    {
        var d = new Dictionary<string, SessionItem>(items.Count);
        foreach (var it in items) d[IdentityKey(it.Uid, it.Track.Uri)] = it;
        return d;
    }

    static string IdentityKey(string uid, string uri) => string.IsNullOrEmpty(uid) ? "r:" + uri : "u:" + uid;

    // A permutation iff every incoming context/autoplay row (skipping user-queue rows, which _naturalOrder never
    // holds) resolves to a DISTINCT key already present in naturalByKey, with none left over on either side — i.e. the
    // same rows, only reordered. A duplicate key, an added row or a dropped one all fail this and fall back to a full
    // rebuild below.
    static bool IsPermutationOfNaturalOrder(IReadOnlyList<QueueWireEntry> next, Dictionary<string, SessionItem> naturalByKey)
    {
        var incoming = new HashSet<string>();
        foreach (var e in next)
        {
            var kind = RowKindOfUri(e.Uri);
            if (kind == QueueRowKind.Playable && e.IsQueued) continue;
            if (!incoming.Add(IdentityKey(e.Uid, e.Uri))) return false;
        }
        if (incoming.Count != naturalByKey.Count) return false;
        foreach (var key in incoming) if (!naturalByKey.ContainsKey(key)) return false;
        return true;
    }

    // ── the ONLY read ────────────────────────────────────────────────────────────────────────────────────────────────

    public QueueSnapshot Snapshot()
    {
        QueueEntry? current = _current is { } c ? ToEntry(c, QueueBucket.NowPlaying) : null;

        var history = ImmutableArray.CreateBuilder<QueueEntry>(_history.Count);
        foreach (var it in _history) history.Add(ToEntry(it, QueueBucket.History));

        var userQueue = ImmutableArray.CreateBuilder<QueueEntry>(_userQueue.Count);
        foreach (var it in _userQueue) if (it.Kind == QueueRowKind.Playable) userQueue.Add(ToEntry(it, QueueBucket.UserQueue));

        var upcoming = ImmutableArray.CreateBuilder<QueueEntry>();
        for (int i = _cursor + 1; i < _context.Count; i++)
        {
            var it = _context[i];
            if (it.Kind != QueueRowKind.Playable) continue;   // delimiter/meta:page never surfaced
            upcoming.Add(ToEntry(it, QueueBucket.NextUp));
        }

        return new QueueSnapshot(
            _revision, _contextUri, _autoplayContextUri, current,
            history.ToImmutable(), userQueue.ToImmutable(), upcoming.ToImmutable(),
            _shuffle, _repeat, _clusterRevision, _cursor);
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────────────────────

    QueueSnapshot Bump() { _revision++; return Snapshot(); }

    QueueItemId MintId() => new(_nextItemId++);

    SessionItem NewQueueItem(QueuedTrack q)
    {
        string uid;
        if (!string.IsNullOrEmpty(q.Uid)) { uid = q.Uid; BumpQueueCursor(uid); }
        else uid = "q" + (_nextQueueUid++);
        return new SessionItem(MintId(), q.Track, uid, QueueProvider.Queue, q.RowKind, null, q.Metadata);
    }

    // Keep the q-uid mint cursor ahead of any preserved "q{n}" uid so a later local add never collides.
    void BumpQueueCursor(string uid)
    {
        if (uid.Length > 1 && uid[0] == 'q' && int.TryParse(uid.AsSpan(1), out int n) && n >= _nextQueueUid)
            _nextQueueUid = n + 1;
    }

    static QueueEntry ToEntry(SessionItem it, QueueBucket bucket) =>
        new(it.Id, "i" + it.Id.Value, it.Track, bucket, it.Provider,
            it.Provider == QueueProvider.Autoplay, it.Uid, it.Metadata);

    int FirstPlayableFrom(int start)
    {
        for (int i = start; i < _context.Count; i++) if (_context[i].Kind == QueueRowKind.Playable) return i;
        for (int i = start - 1; i >= 0; i--) if (_context[i].Kind == QueueRowKind.Playable) return i;
        return _context.Count > 0 ? Math.Clamp(start, 0, _context.Count - 1) : -1;
    }

    int PrevPlayableFrom(int start)
    {
        for (int i = start; i >= 0; i--) if (_context[i].Kind == QueueRowKind.Playable) return i;
        return -1;
    }

    // Skip to an UPCOMING context/autoplay row (§4.3): previous current → history; cursor jumps; skipped rows leave
    // Upcoming (they do NOT enter history — history is actually-played); user queue untouched.
    QueueSnapshot SkipToUpcomingIndex(int j)
    {
        PushHistory(_current);
        _cursor = j;
        _current = _context[j];
        return Bump();
    }

    // Skip BACK to an already-played context row still resident in _context (cursor-back within the context).
    QueueSnapshot SkipToContextBackIndex(int j)
    {
        int hi = _history.FindIndex(x => x.Id == _context[j].Id);
        if (hi >= 0) _history.RemoveRange(hi, _history.Count - hi);   // truncate history above it
        _cursor = j;
        _current = _context[j];
        return Bump();
    }

    // Skip to a USER-QUEUE row (§4.4): previous current → history; predecessors drain/drop (§13 decision 2); the target
    // becomes current (provider Queue); rows after it remain queued; the context cursor does not move.
    QueueSnapshot SkipToUserQueueIndex(int k)
    {
        PushHistory(_current);
        var it = _userQueue[k];
        _userQueue.RemoveRange(0, k + 1);
        _current = it;
        return Bump();
    }

    // Skip to a HISTORY row (§4.5, cursor-back): the entry becomes current; History truncates at/above it; a context row
    // re-derives Upcoming from its slot; a played queue/autoplay row replays one-shot with the context cursor unchanged;
    // user queue still drains first (untouched here).
    QueueSnapshot SkipToHistoryIndex(int h)
    {
        var entry = _history[h];
        _history.RemoveRange(h, _history.Count - h);
        int slot = _context.FindIndex(x => x.Id == entry.Id);
        if (entry.Provider == QueueProvider.Context && slot >= 0) { _cursor = slot; _current = _context[slot]; }
        else _current = entry;
        return Bump();
    }

    (char Bucket, int Index)? LocateByUid(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        int k = _userQueue.FindIndex(x => x.Uid == uid);
        if (k >= 0) return ('q', k);
        int j = _context.FindIndex(x => x.Uid == uid && x.Kind == QueueRowKind.Playable);
        if (j >= 0) return ('c', j);
        int h = _history.FindIndex(x => x.Uid == uid);
        if (h >= 0) return ('h', h);
        return null;
    }

    (char Bucket, int Index)? LocateByUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        int k = _userQueue.FindIndex(x => x.Track.Uri == uri);
        if (k >= 0) return ('q', k);
        int j = _context.FindIndex(x => x.Track.Uri == uri && x.Kind == QueueRowKind.Playable);
        if (j >= 0) return ('c', j);
        int h = _history.FindIndex(x => x.Track.Uri == uri);
        if (h >= 0) return ('h', h);
        return null;
    }

    QueueSnapshot SkipToLocated((char Bucket, int Index) r) => r.Bucket switch
    {
        'q' => SkipToUserQueueIndex(r.Index),
        'h' => SkipToHistoryIndex(r.Index),
        _ => r.Index > _cursor ? SkipToUpcomingIndex(r.Index) : SkipToContextBackIndex(r.Index),
    };

    void PushHistory(SessionItem? it)
    {
        if (it is null || it.Kind != QueueRowKind.Playable) return;
        if (_history.Count == 0 || _history[^1].Id != it.Id) _history.Add(it);
        if (_history.Count > HistoryCap) _history.RemoveRange(0, _history.Count - HistoryCap);
    }

    // Anchored Fisher-Yates over the natural order, dispatched on the anchor's provider. Invariant either way: nothing
    // already played may resurface as Upcoming.
    // Context anchor: the current row sits at logical 0; every Context playable row (already-played ones included)
    // re-pools after it; the autoplay tail + markers keep their natural relative order at the end; cursor = 0.
    // Autoplay anchor: everything already consumed (the original context AND earlier autoplay rows) stays BEFORE the
    // cursor in natural order; only the autoplay playables after the anchor shuffle; the cursor sits on the anchor at
    // its natural index.
    void ReshuffleAnchoringCurrent()
    {
        if (_naturalOrder.Count <= 1) return;
        SessionItem? anchor = _current is { } c && _context.FindIndex(x => x.Id == c.Id) >= 0
            ? _current
            : (_cursor >= 0 && _cursor < _context.Count ? _context[_cursor] : null);
        if (anchor is null) return;

        if (anchor.Provider == QueueProvider.Autoplay) { ReshuffleAutoplayAnchored(anchor); return; }

        var ctx = new List<SessionItem>();
        var tail = new List<SessionItem>();
        foreach (var it in _naturalOrder)
        {
            if (it.Id == anchor.Id) continue;
            if (it.Provider == QueueProvider.Context && it.Kind == QueueRowKind.Playable) ctx.Add(it);
            else tail.Add(it);
        }
        FisherYates(ctx);
        _context.Clear();
        _context.Add(anchor);
        _context.AddRange(ctx);
        _context.AddRange(tail);
        _cursor = 0;
        if (_current is { } cur && cur.Id == anchor.Id) _current = anchor;
    }

    void ReshuffleAutoplayAnchored(SessionItem anchor)
    {
        int na = _naturalOrder.FindIndex(x => x.Id == anchor.Id);
        if (na < 0) return;
        var pool = new List<SessionItem>();
        var rest = new List<SessionItem>();
        for (int i = na + 1; i < _naturalOrder.Count; i++)
        {
            var it = _naturalOrder[i];
            if (it.Provider == QueueProvider.Autoplay && it.Kind == QueueRowKind.Playable) pool.Add(it);
            else rest.Add(it);
        }
        FisherYates(pool);
        _context.Clear();
        for (int i = 0; i <= na; i++) _context.Add(_naturalOrder[i]);
        _context.AddRange(pool);
        _context.AddRange(rest);
        _cursor = na;
        if (_current is { } cur && cur.Id == anchor.Id) _current = _context[na];
    }

    void ShuffleWithoutAnchor()
    {
        var playable = new List<SessionItem>();
        var tail = new List<SessionItem>();
        foreach (var item in _naturalOrder)
        {
            if (item.Provider == QueueProvider.Context && item.Kind == QueueRowKind.Playable) playable.Add(item);
            else tail.Add(item);
        }
        FisherYates(playable);
        _context.Clear();
        _context.AddRange(playable);
        _context.AddRange(tail);
    }

    void RestoreOriginalOrder()
    {
        _context.Clear();
        _context.AddRange(_naturalOrder);
        int idx = _current is { } c ? _context.FindIndex(x => x.Id == c.Id) : -1;
        _cursor = idx >= 0 ? idx : (_context.Count > 0 ? 0 : -1);
    }

    void FisherYates(List<SessionItem> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = NextSeed(i);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    int NextSeed(int maxInclusive)
    {
        _seedState = unchecked(_seedState * 1103515245 + 12345) & 0x7fffffff;
        return _seedState % (maxInclusive + 1);
    }

    static QueueRowKind RowKindOfUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return QueueRowKind.Playable;
        if (uri == "spotify:delimiter" || uri.Contains(":delimiter", StringComparison.Ordinal)) return QueueRowKind.Delimiter;
        if (uri.Contains(":meta:page", StringComparison.Ordinal)) return QueueRowKind.PageMarker;
        return QueueRowKind.Playable;
    }

    // A delimiter stops advance only when it carries advancing_past_track:"pause" (the wire signal Spotify sets when
    // autoplay is off, FIXTURE-A); an unmarked delimiter is a transparent boundary and is skipped (§4.6).
    static bool IsPauseDelimiter(IReadOnlyDictionary<string, string>? m) =>
        m is not null && ((m.TryGetValue("actions.advancing_past_track", out var a) && a == "pause")
                          || (m.TryGetValue("advancing_past_track", out var b) && b == "pause"));

    static bool IsAutoplayMeta(IReadOnlyDictionary<string, string>? m) =>
        m is not null && m.TryGetValue("autoplay.is_autoplay", out var v) && v == "true";

    // The wire's other user-queue marker: metadata is_queued:"true" (some senders omit provider:"queue" — fix §3).
    static bool IsQueuedMeta(IReadOnlyDictionary<string, string>? m) =>
        m is not null && m.TryGetValue("is_queued", out var v) && v == "true";

    static string? AutoplayCtxOf(in RemoteTrack n) =>
        n.Metadata is { } m && m.TryGetValue("context_uri", out var u) ? u : null;

    static Track TrackFromRemote(in RemoteTrack r)
    {
        var artists = string.IsNullOrEmpty(r.ArtistName) && string.IsNullOrEmpty(r.ArtistUri)
            ? Array.Empty<ArtistRef>()
            : new[] { new ArtistRef(EntityUri.IdOf(r.ArtistUri), r.ArtistUri, r.ArtistName) };
        var album = new AlbumRef(EntityUri.IdOf(r.AlbumUri), r.AlbumUri, r.AlbumName);
        var img = string.IsNullOrEmpty(r.ImageUrl) ? null : new Image(r.ImageUrl!);
        return new Track(EntityUri.IdOf(r.Uri), r.Uri, string.IsNullOrEmpty(r.Title) ? r.Uri : r.Title,
            artists, album, r.DurationMs, false, img);
    }

}
