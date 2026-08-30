using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

public enum PutStateReasonKind { NewConnection, PlayerStateChanged, VolumeChanged, BecameInactive }

public readonly record struct SnapshotTrack(
    string Uri, string Uid, string Provider, string Title, string AlbumTitle,
    string ArtistUri, string ArtistName, string AlbumUri, string ImageUrl,
    bool HasVideo, int ViewIndex, IReadOnlyDictionary<string, string> Metadata);

public readonly record struct LocalPlaybackSnapshot(
    SnapshotTrack Track, string? ContextUri, long PositionMs, long DurationMs,
    bool IsPlaying, bool IsPaused, bool Shuffle, RepeatMode Repeat,
    IReadOnlyList<SnapshotTrack> PrevTracks, IReadOnlyList<SnapshotTrack> NextTracks,
    IReadOnlyDictionary<string, string> ContextMetadata, int ContextIndex,
    string InteractionId, string PageInstanceId, string QueueRevision,
    string SessionId, string PlaybackId, long HasBeenPlayingForMs, long StartedPlayingAtMs, double Volume01 = 0.0);

public readonly record struct ConnectCommandAttribution(string SenderDeviceId, uint MessageId, string CommandId);

public interface IConnectCommandAttributionSink
{
    void NoteCommand(in ConnectCommandAttribution attribution);
}

public sealed class DeviceStatePublisher : IPlaybackProjection, IConnectCommandAttributionSink, IDisposable
{
    const int MaxWirePrevTracks = 50;
    const int MaxWireNextTracks = 50;

    readonly ITransport _transport;
    readonly string _deviceId;
    readonly IPlaybackState _state;
    readonly Func<string?> _connectionId;
    readonly Func<PutStateReasonKind, LocalPlaybackSnapshot?, uint, bool, ConnectCommandAttribution, byte[]> _build;
    readonly Action<byte[]>? _onCluster;
    readonly WaveeLogger _log;
    readonly Func<long> _now;
    readonly IDisposable _connSub;
    readonly object _gate = new();
    readonly SemaphoreSlim _publishGate = new(1, 1);
    uint _messageId;
    string _sessionId = "";
    string _playbackId = "";
    string _interactionId = "";
    string _pageInstanceId = "";
    string _queueRevision = "";
    ulong _queueRevisionCounter = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
    string? _sessionContextUri;
    long _startedPlayingAtMs;
    bool _transportPaused;
    bool _ownershipRetired;
    ConnectCommandAttribution _lastCommand;
    long _lastCommandAtMs;
    string _lastPublishKey = "";
    long _lastReannounceMs;
    // The track a Started/TrackChanged/Resumed event last told us we're on — kept independent of _state.CurrentTrack
    // because a belated Paused/Ended for the OUTGOING track can (and does) arrive after that state already moved on,
    // and by then _state's own transport fields may already carry that stale event's fold too (see the OnEvent guard).
    string? _currentTrackUri;
    // Our OWN volume, cached the moment a genuine VolumeChanged fires — never read live off _state.Volume for the wire,
    // because PlaybackProjection.OnCluster overwrites _state.Volume with the ACTIVE device's volume while we are not
    // it (the slider-follows-active-device rule), and DeviceInfo.Volume must never publish somebody else's level as ours.
    double _localVolume01;
    readonly TrailingCoalescer _volumeTx;

    public DeviceStatePublisher(
        ITransport transport, string deviceId, IPlaybackState state,
        IObservable<string?> connectionId, Func<string?> currentConnectionId,
        Func<PutStateReasonKind, LocalPlaybackSnapshot?, uint, bool, byte[]> build,
        Action<byte[]>? onCluster = null, WaveeLogger log = default, Func<long>? clock = null,
        int volumePublishWindowMs = 400, Func<int, CancellationToken, Task>? delay = null)
        : this(transport, deviceId, state, connectionId, currentConnectionId,
            (reason, snap, mid, active, _) => build(reason, snap, mid, active),
            onCluster, log, clock, volumePublishWindowMs, delay)
    {
    }

    public DeviceStatePublisher(
        ITransport transport, string deviceId, IPlaybackState state,
        IObservable<string?> connectionId, Func<string?> currentConnectionId,
        Func<PutStateReasonKind, LocalPlaybackSnapshot?, uint, bool, ConnectCommandAttribution, byte[]> build,
        Action<byte[]>? onCluster = null, WaveeLogger log = default, Func<long>? clock = null,
        int volumePublishWindowMs = 400, Func<int, CancellationToken, Task>? delay = null)
    {
        _transport = transport;
        _deviceId = deviceId;
        _state = state;
        _connectionId = currentConnectionId;
        _build = build;
        _onCluster = onCluster;
        _log = log;
        _now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _localVolume01 = state.Volume;   // seed from whatever the projection knows before the first VolumeChanged fires
        _volumeTx = new TrailingCoalescer(volumePublishWindowMs, _now, delay);
        _connSub = connectionId.Subscribe(Observers.From<string?>(OnConnectionId));
    }

    /// <summary>Optional Connect uri mask — the SINGLE upstream point covering current/prev/next rows. A playable whose
    /// source declares no Connect publishability rewrites its uri here (remote controllers must never receive a uri they
    /// cannot resolve); the queue <c>uid</c> is never touched, so the wire rows stay addressable. NULL (the default, and
    /// all of Phase 1) publishes every uri VERBATIM.</summary>
    public Func<Track, string>? PublishUriMask { get; set; }

    /// <summary>Optional Connect CONTEXT mask — the same rule as <see cref="PublishUriMask"/>, one level up. A context
    /// the connect-state service cannot resolve (a playback module's, a local folder's) is rewritten before it goes on
    /// the wire; without it the row masking is pointless, because the state as a whole is still unresolvable and the
    /// service refuses to make us the cluster's active device. NULL (the default) publishes the context VERBATIM.</summary>
    public Func<string?, string?>? PublishContextMask { get; set; }

    /// <summary>Optional — the CURRENT track's live media kind, gated to null while we are not the device actually
    /// decoding it (see <see cref="ConnectStateBuilder.BuildPutState"/>'s <c>currentKind</c> parameter, which this
    /// mirrors). Folded into the steady-state change-gate key (bug 6) so an audio↔video toggle on the SAME track at
    /// the same wall-second with an empty up-next queue still produces a real key change — without this the toggle
    /// was silently swallowed by the dedup gate below. Wired at go-live to the same thunk the builder reads.</summary>
    public Func<PlayableKind?>? CurrentMediaKind { get; set; }

    void OnConnectionId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _ = PublishAsync(PutStateReasonKind.NewConnection, OwnsSession());
    }

    public void OnEvent(in PlaybackEvent e)
    {
        lock (_gate)
        {
            if (_ownershipRetired)
            {
                bool startsNewOwnership = (e.Kind is EvKind.Started or EvKind.TrackChanged or EvKind.Resumed)
                    && _state.CurrentTrack is not null;
                if (startsNewOwnership) _ownershipRetired = false;
                // A per-device volume must still reach the cluster from a retired (inactive) Wavee — only the
                // isActive flag on that publish says we don't own the session, never a dropped volume message.
                else if (e.Kind is not (EvKind.BecameInactive or EvKind.VolumeChanged)) return;
            }
        }

        // Latch "what track are we on" from the ordered forward stream only (never from a terminal Paused/Ended,
        // which is exactly the kind that arrives late — see the guard below). Read straight off the event, not
        // _state.CurrentTrack, so this stays correct even if _state has not folded the same event yet.
        if (e.Track is { Uri.Length: > 0 } fwdTrack && e.Kind is not (EvKind.Paused or EvKind.Ended or EvKind.BecameInactive))
            _currentTrackUri = fwdTrack.Uri;

        // Ordering guard (findings §4): an outgoing host's belated stop/end notification can arrive AFTER the next
        // track has already started (a proactive advance that doesn't wait for the old decoder's confirmed EOF).
        // Its AtMs/Kind describe the OUTGOING track, not the one we are on now — folding it blindly is exactly how a
        // stale tail position (e.g. pos=7271 for a track that just started at pos=0) gets published as CURRENT for
        // up to a minute, because the steady-state gate then latches on that wrong key and swallows the correction.
        // Detected only when the event carries its own track and it disagrees with our latch above; a context-end
        // Ended (Track == null) is unaffected and falls through to the normal path below.
        if (e.Kind is EvKind.Paused or EvKind.Ended && e.Track is { } endingTrack
            && _currentTrackUri is { Length: > 0 } && !string.Equals(endingTrack.Uri, _currentTrackUri, StringComparison.Ordinal))
        {
            _log.Info($"put-state: dropped stale {e.Kind} for {endingTrack.Uri} (current is {_currentTrackUri}) — forcing a fresh republish");
            _ = PublishAsync(PutStateReasonKind.PlayerStateChanged, OwnsSession(), force: true);
            return;
        }

        if (e.Kind is EvKind.Paused)
            lock (_gate) _transportPaused = true;
        else if (e.Kind is EvKind.Started or EvKind.Resumed or EvKind.TrackChanged or EvKind.Ended or EvKind.BecameInactive)
            lock (_gate) _transportPaused = false;

        bool pausedFirst = e.Kind == EvKind.Paused && string.IsNullOrEmpty(_sessionId) && _state.CurrentTrack is not null;
        if (e.Kind is EvKind.Started or EvKind.TrackChanged || pausedFirst)
        {
            lock (_gate)
            {
                var ctx = _state.ContextUri;
                if (e.Kind == EvKind.Started || pausedFirst || ctx != _sessionContextUri)
                {
                    _sessionId = e.Ids?.SessionId ?? NewId();
                    _sessionContextUri = ctx;
                    _interactionId = e.Ids?.InteractionId ?? NewDashedUuid();
                    _pageInstanceId = e.Ids?.PageInstanceId ?? NewDashedUuid();
                }
                _playbackId = e.Ids?.PlaybackIdHex ?? NewId();
                if (_startedPlayingAtMs == 0) _startedPlayingAtMs = _now();
                BumpQueueRevision();
            }
        }
        else if (e.Kind is EvKind.QueueChanged or EvKind.OptionsChanged)
        {
            lock (_gate) BumpQueueRevision();
        }
        else if (e.Kind is EvKind.Ended or EvKind.BecameInactive)
        {
            lock (_gate)
            {
                _startedPlayingAtMs = 0;
                if (e.Kind == EvKind.BecameInactive) _ownershipRetired = true;
            }
        }

        if (e.Kind == EvKind.VolumeChanged)
            lock (_gate) _localVolume01 = _state.Volume;

        bool isActive = _state.CurrentTrack is not null && e.Kind is not (EvKind.Ended or EvKind.BecameInactive);
        var reason = e.Kind switch
        {
            EvKind.VolumeChanged => PutStateReasonKind.VolumeChanged,
            EvKind.BecameInactive => PutStateReasonKind.BecameInactive,
            _ => PutStateReasonKind.PlayerStateChanged,
        };
        if (reason == PutStateReasonKind.VolumeChanged)
            // OwnsSession(), not isActive above: a retired Wavee still owns none of the transport facts isActive
            // folds in, but its OWN volume must still reach the cluster — with isActive correctly false on the wire.
            _volumeTx.Post(() => _ = PublishAsync(PutStateReasonKind.VolumeChanged, OwnsSession()));
        else
            _ = PublishAsync(reason, isActive);
    }

    /// <summary>Publish the current player state because something OUTSIDE the playback-event stream changed what the wire
    /// says — today: a music-video association landing under an already-playing track (the badge-only upgrade), which adds
    /// the track's <c>associated_video_id</c> + the <c>switch-to-video</c> offer without any host/kind change. No playback
    /// event fires for that, and the steady-state change gate below would swallow it (its key covers transport state only),
    /// so this publishes UNGATED — callers must therefore only invoke it on a real edge.</summary>
    public void PublishStateChanged()
    {
        if (_state.CurrentTrack is null) return;
        // Retired ownership (we handed playback to another device) mutes the event path too — republishing here would
        // re-announce us as active on the cluster, stealing it back over a badge.
        lock (_gate) { if (_ownershipRetired) return; }
        _ = PublishAsync(PutStateReasonKind.PlayerStateChanged, true, force: true);
    }

    public void PublishInactive()
    {
        lock (_gate) _ownershipRetired = true;
        _ = PublishAsync(PutStateReasonKind.BecameInactive, false);
    }

    /// <summary>Re-announce this device to the cluster with <see cref="PutStateReasonKind.NewConnection"/> — the same
    /// announce a fresh dealer connection id triggers. The caller is a resume from OS SLEEP: the socket may look alive
    /// while the server has long since dropped our device, so without this the device silently vanishes from other
    /// clients' picker until something else forces a publish. Deliberately NOT
    /// <see cref="PublishStateChanged"/> — that is a PlayerStateChanged reason and does not re-register the device.
    /// Muted while ownership is retired (we are not the active device; announcing would steal the cluster back).</summary>
    public void AnnounceNewConnection()
    {
        lock (_gate) { if (_ownershipRetired) return; }
        _ = PublishAsync(PutStateReasonKind.NewConnection, OwnsSession());
    }

    public void NoteCommand(in ConnectCommandAttribution attribution)
    {
        lock (_gate) { _lastCommand = attribution; _lastCommandAtMs = _now(); }
    }

    /// <summary>Ownership, not audibility: true iff we hold a session (a current track is loaded), the cluster does
    /// not already name a DIFFERENT device active, and we have not retired it — the wire's <c>is_active</c> must
    /// never depend on whether audio happens to be flowing right now. A paused-but-active Wavee answering false here
    /// is exactly what self-demoted us on every dealer reconnect / OS resume, emptying the cluster's active-device id
    /// and triggering a bogus "another device became active" teardown downstream. The ordinary event path already
    /// applies this same rule inline (OnEvent's isActive, above); this is the one predicate the announce paths (which
    /// run before any event exists) share with it.
    /// <para>The ActiveDeviceId check (bug 2) matters because <see cref="_state"/>.CurrentTrack is not "a track WE are
    /// playing" — it is whatever the projection currently shows, including a passive VIEWER's mirrored fold of some
    /// OTHER device's cluster row (a Wavee that has never played still folds the phone's now-playing track). Without
    /// this, a fresh dealer connection captured while merely viewing someone else's session announced NewConnection
    /// with <c>is_active=true</c> and a player_state built from THEIR track — a lie no amount of it being "just an
    /// announce" excuses.</para></summary>
    bool OwnsSession()
    {
        lock (_gate)
        {
            if (_ownershipRetired || _state.CurrentTrack is null) return false;
            var aid = _state.ActiveDeviceId;
            return string.IsNullOrEmpty(aid) || aid == _deviceId;
        }
    }

    async Task PublishAsync(PutStateReasonKind reason, bool isActive, bool force = false)
    {
        var connId = _connectionId();
        if (string.IsNullOrEmpty(connId)) return;

        var snap = BuildSnapshot();
        string key = reason + "|" + isActive + "|" + (snap?.Track.Uri ?? "") + "|" + (snap?.Track.Uid ?? "")
            + "|" + (snap?.IsPlaying ?? false) + "|" + (snap?.IsPaused ?? false) + "|" + (snap?.Shuffle ?? false) + "|" + (snap?.Repeat ?? RepeatMode.Off)
            + "|" + ((snap?.PositionMs ?? 0) / 1000) + "|" + (int)Math.Round((snap?.Volume01 ?? 0) * 100) + "|" + NextSig(snap)
            + "|" + (CurrentMediaKind?.Invoke()?.ToString() ?? "-");
        uint mid;
        ConnectCommandAttribution attribution;
        lock (_gate)
        {
            if (!force && reason is PutStateReasonKind.PlayerStateChanged or PutStateReasonKind.VolumeChanged
                && key == _lastPublishKey) return;
            // NOTE: _lastPublishKey is NOT latched here any more — only once the PUT actually succeeds, below. Latching
            // it before the transport call meant a failed/thrown PUT still gated out the identical next attempt, so a
            // rejected state was never retried until something ELSE happened to change it.
            mid = ++_messageId;
            // A command attribution older than ~10s almost certainly belongs to a DIFFERENT change than the one we are
            // about to publish (a purely local edit, or a later command that already superseded it) — crediting this
            // PUT to it would blame/credit the wrong sender. Age it out rather than let a stale id ride forever.
            attribution = _now() - _lastCommandAtMs < 10_000 ? _lastCommand : default;
        }

        await _publishGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = _build(reason, snap, mid, isActive, attribution);
            var resp = await _transport.Publish(_deviceId, connId!, bytes).ConfigureAwait(false);
            if (resp.Ok)
            {
                lock (_gate) _lastPublishKey = key;   // commit the change-gate key ONLY on a PUT the server actually accepted
                // Info, not Debug: this is the ONLY record of what we told the connect-state service, and the PUT's
                // RESPONSE is a Cluster we immediately re-inject as if it were a remote push (onCluster below). The
                // change-gate above (_lastPublishKey) already collapses steady-state republishes, so this is ~1 line per
                // real state change — and without it a "why did the server correct us?" question has no input side.
                _log.Info($"put-state {reason} active={isActive} track={snap?.Track.Uri ?? "-"} pos={snap?.PositionMs ?? 0} " +
                    $"playing={snap?.IsPlaying ?? false} paused={snap?.IsPaused ?? false} ctx={snap?.ContextUri ?? "-"} " +
                    $"msgId={mid} commandId={WaveeLogRedaction.HashLike(attribution.CommandId)} cluster={resp.Body.Length}B");
                if (resp.Body.Length > 0) _onCluster?.Invoke(resp.Body);
            }
            else
            {
                // A rejected PUT never gets to be "the last thing we told the server" — reset the gate so the very
                // next identical event (a retry, or the reannounce below) is not swallowed by the key match above.
                lock (_gate) _lastPublishKey = "";
                bool inactiveSoftAck = resp.Status == 422 && reason == PutStateReasonKind.BecameInactive;
                if (inactiveSoftAck)
                    _log.Debug($"put-state 422 after BecameInactive (soft acknowledgement) msgId={mid}");
                else
                    _log.Warn($"put-state failed ({resp.Status})");
                var fields = new[]
                {
                    WaveeLogField.Of("status", resp.Status),
                    WaveeLogField.Of("reason", reason.ToString()),
                    WaveeLogField.Of("messageId", mid),
                    WaveeLogField.Of("track", snap?.Track.Uri ?? "-"),
                };
                if (inactiveSoftAck)
                    WaveeLog.Instance.Debug("connect", "put-state.rejected", "inactive state already superseded server-side", fields);
                else
                    WaveeLog.Instance.Warn("connect", "put-state.rejected", "connect-state PUT rejected by server", fields);

                // Rejection recovery (findings §3): the connect-state service answers non-OK — 422 above all, its code
                // for "I no longer have your device registration" — for exactly the PlayerStateChanged/VolumeChanged
                // PUTs that assume we're still registered. Without a re-announce here the device silently drops off
                // every remote picker until something unrelated happens to trigger one. Rate-limited so a hard-down
                // server can't turn this into a hot loop; BecameInactive's soft-ack is deliberately excluded (it is
                // not a registration problem — see above).
                if (reason is PutStateReasonKind.PlayerStateChanged or PutStateReasonKind.VolumeChanged)
                    MaybeReannounce();
            }
        }
        catch (Exception ex)
        {
            lock (_gate) _lastPublishKey = "";   // same reasoning as the non-OK branch: a throw is not a committed PUT
            // Structured + full exception (type + stack) so a future null/serialization fault in the builder is
            // diagnosable at a glance — the bare ex.Message alone made the Restrictions NRE cryptic.
            _log.Info("put-state error: " + ex.Message);
            WaveeLog.Instance.Error("connect", "put-state.error", "connect-state PUT threw while building/publishing", ex,
                WaveeLogField.Of("reason", reason.ToString()),
                WaveeLogField.Of("active", isActive),
                WaveeLogField.Of("track", snap?.Track.Uri ?? "-"));
        }
        finally { _publishGate.Release(); }
    }

    /// <summary>Re-announce at most once every 30s (findings §3). Runs OUTSIDE <c>_publishGate</c> (fire-and-forget,
    /// like every other publish trigger here) — the caller is already inside the gate's <c>finally</c>-protected
    /// scope by the time this is reachable, so awaiting our own gate here would deadlock.</summary>
    void MaybeReannounce()
    {
        long now = _now();
        lock (_gate)
        {
            if (now - _lastReannounceMs < 30_000) return;
            _lastReannounceMs = now;
        }
        _ = PublishAsync(PutStateReasonKind.NewConnection, OwnsSession());
    }

    LocalPlaybackSnapshot? BuildSnapshot()
    {
        var t = _state.CurrentTrack;
        if (t is null) return null;

        var prev = new List<SnapshotTrack>();
        var next = new List<SnapshotTrack>();
        string trackUid = "";
        bool nowAutoplay = false;
        QueueEntry? nowEntry = null;

        foreach (var qe in _state.Queue)
        {
            if (qe.Bucket == QueueBucket.NowPlaying)
            {
                trackUid = qe.Uid;
                nowAutoplay = qe.IsAutoplay;
                nowEntry = qe;
            }
        }

        int currentIndex = 0;
        int nextContextIndex = currentIndex + 1;
        foreach (var qe in _state.Queue)
        {
            if (qe.Bucket == QueueBucket.NowPlaying) continue;
            if (qe.Bucket == QueueBucket.History)
            {
                // The session's actually-played tail is published as prev_tracks (playback-restore fix §2): it is what a
                // later cold-start cluster hands back for History recovery, and what remote clients show as "previous".
                prev.Add(ToSnapshotTrack(qe, ProviderOf(qe), -1));
                continue;
            }
            string provider = ProviderOf(qe);
            int viewIndex = IsContextProvider(provider) ? nextContextIndex++ : -1;
            next.Add(ToSnapshotTrack(qe, provider, viewIndex));
        }

        var currentSource = nowEntry ?? new QueueEntry(QueueItemId.None, "now", t, QueueBucket.NowPlaying,
            nowAutoplay ? QueueProvider.Autoplay : QueueProvider.Context, nowAutoplay, trackUid);
        var current = ToSnapshotTrack(currentSource, ProviderOf(currentSource), currentIndex);
        IReadOnlyDictionary<string, string> metadata = _state is NowPlayingProjection p
            ? p.ContextMetadata
            : new Dictionary<string, string>();

        long started, hasBeen; string sid, pid, iid, page, rev; bool transportPaused; double localVolume;
        lock (_gate)
        {
            started = _startedPlayingAtMs; sid = _sessionId; pid = _playbackId;
            iid = _interactionId; page = _pageInstanceId; rev = _queueRevision;
            transportPaused = _transportPaused;
            hasBeen = started > 0 && _state.IsPlaying ? Math.Max(0, _now() - started) : 0;
            localVolume = _localVolume01;
        }

        // Connect wire: paused is a sub-state of playing (transport engaged, audio stopped). Ended/stopped ⇒ both false.
        bool wirePaused = transportPaused;
        bool wirePlaying = _state.IsPlaying || wirePaused;

        var wirePrev = CapPrev(prev);
        var wireNext = CapNext(next);

        string? wireContext = _state.ContextUri;
        if (PublishContextMask is { } ctxMask)
        {
            // Fail-soft exactly like the row mask: a throwing/empty answer publishes the real context rather than nothing.
            try { wireContext = ctxMask(wireContext) is { Length: > 0 } m ? m : _state.ContextUri; }
            catch (Exception ex) { _log.Info("publish context mask failed for " + (_state.ContextUri ?? "-") + ": " + ex.Message); }
        }

        return new LocalPlaybackSnapshot(current, wireContext, _state.PositionMs, _state.DurationMs,
            wirePlaying, wirePaused, _state.IsShuffle, _state.Repeat,
            wirePrev, wireNext, metadata, currentIndex, iid, page, rev, sid, pid, hasBeen, started, localVolume);
    }

    static IReadOnlyList<SnapshotTrack> CapPrev(List<SnapshotTrack> tracks)
    {
        if (tracks.Count <= MaxWirePrevTracks) return tracks;
        return tracks.GetRange(tracks.Count - MaxWirePrevTracks, MaxWirePrevTracks);
    }

    static IReadOnlyList<SnapshotTrack> CapNext(List<SnapshotTrack> tracks)
    {
        if (tracks.Count <= MaxWireNextTracks) return tracks;
        return tracks.GetRange(0, MaxWireNextTracks);
    }

    SnapshotTrack ToSnapshotTrack(QueueEntry entry, string provider, int viewIndex)
    {
        var t = entry.Track;
        var artist = t.Artists.Count > 0 ? t.Artists[0] : new ArtistRef("", "", "");
        string uri = t.Uri;
        if (PublishUriMask is { } mask)
        {
            try { uri = mask(t) is { Length: > 0 } masked ? masked : t.Uri; }
            catch (Exception ex) { _log.Info("publish uri mask failed for " + t.Uri + ": " + ex.Message); }
        }
        return new SnapshotTrack(uri, entry.Uid, provider, t.Title ?? "", t.Album.Name ?? "",
            artist.Uri ?? "", artist.Name ?? "", t.Album.Uri ?? "", t.Image?.Url ?? "",
            VideoPresence.HasVideo(t.Uri), viewIndex, entry.Metadata ?? new Dictionary<string, string>());
    }

    static string ProviderOf(QueueEntry entry)
    {
        if (entry.Provider != QueueProvider.Context) return entry.Provider.ToWire();
        if (entry.IsAutoplay) return "autoplay";
        return entry.Bucket == QueueBucket.UserQueue ? "queue" : "context";
    }

    static bool IsContextProvider(string provider) => provider is "context" or "autoplay";

    // Includes the queue revision (findings §5): count+head alone is blind to a deep reorder that keeps the same
    // count and the same head row — QueueRevision bumps exactly when the queue changes (BumpQueueRevision), so
    // folding it in here is enough to make the steady-state gate above see a reorder as a real change again.
    static string NextSig(LocalPlaybackSnapshot? snap) =>
        snap is { } s && s.NextTracks.Count > 0 ? s.QueueRevision + ":" + s.NextTracks.Count + ":" + s.NextTracks[0].Uri : "0";

    static string NewId() => Guid.NewGuid().ToString("N");
    static string NewDashedUuid() => Guid.NewGuid().ToString();

    void BumpQueueRevision()
    {
        unchecked { _queueRevisionCounter++; }
        _queueRevision = _queueRevisionCounter.ToString(CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _volumeTx.Dispose();
        _connSub.Dispose();
    }
}
