using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── Stage D — the bidirectional playback-state projection (proto-free) ────────────────────────────────────────────────
// NowPlayingProjection folds three inputs into one slab and presents Wavee.Core.IPlaybackState (what PlaybackBridge/PlayerBar
// read, unchanged):
//   • ClusterDelta   — the remote truth (mapped from the Cluster proto by SpotifyLive's ClusterMapper) — VIEWER mode.
//   • PlaybackEvent  — the local reducer's events (Stage E controller) — when WE are the active device.
//   • AudioHostSignal— the local host clock + Ended (Stage H).
// Reconciliation: ONE authority decides whose state now-playing shows — the Connect OWNER (Backend/PlaybackOwnership.cs),
// which this projection owns and folds every cluster through FIRST. Us ⇒ the local session + host own the display and a
// cluster never reverts them; Foreign ⇒ the cluster's player_state IS the display and local writers / host signals fold
// nothing; Nobody ⇒ the local session when there is one — except that a device that just LEFT keeps its snapshot on
// screen, paused, so a phone flap never flips the bar to a stale local session. IPlaybackState.ActiveDeviceId is that
// owner too, never the raw cluster field (ClusterActiveDeviceId).

/// <summary>Proto-free snapshot of one cluster track (mapped from a ProvidedTrack by SpotifyLive).</summary>
public readonly record struct RemoteTrack(
    string Uri, string Title, string ArtistName, string ArtistUri,
    string AlbumName, string AlbumUri, string? ImageUrl, long DurationMs,
    // Context uid + provider ("queue" / "context") — carried so a forwarded set_queue can re-emit the active device's
    // own queue rows faithfully. Trailing defaults so display-only constructions are unaffected.
    string Uid = "", string Provider = "",
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Proto-free row of the Connect device roster (volume in Spotify's 0..65535 range).</summary>
public readonly record struct ConnectDeviceRow(string Id, string Name, DeviceKind Kind, bool IsActive, int Volume0_65535);

/// <summary>Proto-free snapshot of a Spotify Cluster (the remote playback truth) + the device roster.</summary>
public sealed record ClusterDelta(
    string ActiveDeviceId,
    bool HasTrack, RemoteTrack Track,
    string? ContextUri,
    bool IsPlaying, bool IsPaused, bool IsBuffering,
    long PositionAsOfMs, long TimestampMs, long ServerTimestampMs, long DurationMs,
    bool Shuffle, RepeatMode Repeat,
    IReadOnlyList<ConnectDeviceRow> Devices,
    IReadOnlyList<RemoteTrack> NextTracks,
    // Restrictions on the active track (ads / first-last) + our device's volume (0..65535, -1 = unknown). Trailing defaults
    // so existing constructions are unaffected.
    bool DisallowSkipPrev = false, bool DisallowSkipNext = false, bool DisallowSeeking = false, int OurVolume0_65535 = -1,
    // Content playback rate (spoken-word media); 1.0 = normal. Trailing default so existing constructions are unaffected.
    double PlaybackSpeed = 1.0,
    // The ACTIVE device's volume (0..65535, -1 = unknown) — the slider follows the active device, not just us.
    int ActiveVolume0_65535 = -1,
    // The Connect queue revision (PlayerState.queue_revision, a STRING — can exceed Int64). Echoed back on an outbound
    // set_queue. Trailing default so existing constructions are unaffected.
    string QueueRevision = "",
    // The active device's history (prev_tracks) as the cluster reports it — kept with uid+provider so a forwarded
    // set_queue can rewrite the REMOTE device's REAL queue (its NextTracks above are the up-next), not our local one.
    // Non-const default → nullable; coalesced at the fold.
    IReadOnlyList<RemoteTrack>? PrevTracks = null,
    // What the OWNERSHIP fold (Backend/PlaybackOwnership.cs) orders and judges by: which feed this cluster came from — a
    // dealer push, or the RESPONSE to our put-state #PutMsgId (the verdict on a claim) — ClusterUpdate's update_reason and
    // devices_that_changed, and the active device's started_playing_at. Trailing defaults so existing constructions are
    // unaffected.
    ClusterOrigin Origin = ClusterOrigin.Push, uint PutMsgId = 0,
    int UpdateReason = 0, IReadOnlyList<string>? ChangedDevices = null, long ActiveStartedPlayingAt = 0);

public sealed class NowPlayingProjection : IPlaybackProjection, IPlaybackState, IDisposable
{
    // After a local command, a contradicting cluster VOLUME within this window does not snap the slider back. Play-state
    // needs no window: while WE own playback the cluster never writes it at all (see FoldClusterLocked).
    const long LocalCmdWindowMs = 2500;

    // Who wrote what, for the always-on `projection.publish` / `nowplaying.identity` lines.
    const string WriterCluster = "cluster", WriterLocal = "local", WriterHost = "host", WriterEnrich = "enrich",
        WriterOverride = "override", WriterOwner = "owner";

    // The cluster fold runs the OWNERSHIP fold first (outside _gate). A transition that fold raises is painted by the fold
    // itself — one publish, carrying its frame — so while it runs OnOwnershipChanged only records that one happened.
    // Per thread: a claim racing in on another thread is still published by its own handler call.
    [ThreadStatic] static NowPlayingProjection? t_folding;
    [ThreadStatic] static bool t_foldTransition;

    readonly string _ourDeviceId;
    readonly Func<long> _now;
    readonly Func<long> _serverNow;   // estimated server-clock Unix ms (<=0 ⇒ unsynced); read only at cluster fold
    readonly SimpleSubject<IPlaybackState> _changes = new();
    readonly SimpleSubject<PositionSample> _positionTicks = new();
    readonly object _gate = new();
    readonly object _clusterFold = new();   // serializes whole cluster folds (ownership + display) — see OnCluster
    Timer? _ticker;
    Timer? _protectTimer;   // one-shot: a protected claim's expiry (see ArmProtectExpiry); guarded by _gate
    bool _disposed;

    // ── the slab (mutated in place under _gate; coarse Changes fired outside) ─────────────────────────────────────────
    Track? _track;
    string _trackWriter = "";   // who last wrote _track (cluster / local / enrich) — the nowplaying.identity line names it
    // Live enrichment: the cluster's player_state metadata is often THIN (title + album only, no artist name, no album
    // art). It is raised to the playable's Open rung through THE façade and re-read from the store — the same call any
    // page open makes, so a track the user just opened is already there and this costs nothing. The bespoke
    // TrackResolver Func (and its own divergent "is it thin?" predicate) are gone: HydrationLevels.Of IS the predicate,
    // and the ledger's Exhausted seal is what stops a genuinely thin row re-firing every heartbeat (design §1.5).
    readonly IEntityHydrator _hydrator;
    readonly IStore _store;
    string? _resolvingUri;   // de-dupe: at most one in-flight resolve per uri (guarded by _gate)
    string? _warmedUri;      // de-dupe: the NowPlaying trait warm fires once per uri (guarded by _gate)
    string? _contextUri;
    long _localRevision;     // the session's monotonic revision (from the last ApplyLocalSnapshot) — for diagnostics / UI keying
    // Viewer-row ids live in a DISJOINT high range (ViewerIdBase+seq) so they can NEVER collide with the local session's
    // small monotonic ids (F5 — the "unified" guarantee is non-collision): a stale viewer id resolved against a live local
    // session after a device-role flip finds no match (safe no-op) instead of hitting an unrelated track.
    const ulong ViewerIdBase = 1UL << 62;
    int _viewerIdSeq;        // mints per-row ids for the viewer queue so a viewer row-click can be targeted (F5)
    readonly Dictionary<ulong, QueueEntry> _viewerRows = new();
    bool _hasLocalContext;
    IReadOnlyDictionary<string, string> _contextMetadata = new Dictionary<string, string>();
    string _clusterActiveDeviceId = "";   // the RAW cluster field — diagnostics only; ActiveDeviceId is the owner's
    // ── the PLAYING stream's identity ────────────────────────────────────────────────────────────────────────────────
    // What is actually decoding right now, not what was asked for and not what the track HAS. Folded from the load
    // chokepoint's own resolve (PlaybackController -> PlaybackEvent) and CLEARED the moment that stops being true —
    // see ClearStreamIdentityLocked. 0/null is the honest unknown.
    int _streamBitrateKbps;
    string? _streamFormat;
    ClusterDelta? _lastCluster;   // the last folded cluster (raw next/prev with uid+provider+metadata) — the source the controller replays through PlaybackSession.ReplaceFromCluster on ghost-resume (§8)
    string _queueRevision = "";
    bool _isPlaying, _isBuffering, _isPrebuffering, _shuffle;
    // What the UI last SAW — committed in FireChanges on EVERY publish, whoever the writer. It used to be a memory only
    // OnHostSignal wrote: a cluster fold that published "not playing" left it saying "playing", the very next host tick
    // (still playing) compared equal and never fired, and the bar showed a PLAY glyph over audible playback
    // (handoff-20260911-connect-detail.md §3.4). Now any fold that disagrees with what was published publishes.
    PublishedState _lastPub;
    string? _identityUri;                  // nowplaying.identity: the (uri, shape) last logged — once per pair
    IdentitySuspicion _identityShape;
    string? _mixedWarnedFor;               // projection.mixed: the foreign owner already warned about (one-shot per episode)
    PlaybackRecoveryKind _recoveryKind;
    public bool IsPrivateSession { get; set; }

    RepeatMode _repeat;
    double _volume = 0.7;
    long _posMs, _posAnchorWall, _durMs;
    // The Stopwatch.GetTimestamp() instant of the last REAL local track boundary (a genuine uri change — never a
    // same-track republish). Disqualifies a PositionTick host signal whose OWN sample instant (AudioHostSignal.SampleQpc,
    // stamped by the host at construction) predates it: the audio host is reused across tracks (Load() just points it at
    // a new stream), so a tick the OLD stream had already queued before the swap can still reach OnHostSignal a frame or
    // two AFTER the boundary landed here, carrying the PREVIOUS track's position. Folding it clobbered the just-reset
    // 0 with the old position read against the NEW track's duration — the "3:04 / -0:24" flash on a track change
    // (finding S1 #4). A tick can never legitimately predate the boundary that started its own track, so the QPC
    // ordering is a sound (not heuristic) proof of staleness — see OnHostSignal.
    long _trackBoundaryQpc;
    // The MEDIA-AUTHORITATIVE duration override: a user-attached local video is a DIFFERENT EDIT with its own length, so
    // once the media engine reports it, that length — not the catalog's — is the truth the seek bar scales by and the
    // PutState publishes. Scoped to ONE playable uri and dropped the moment the track changes, so it can never leak onto
    // the next song; re-applied at BOTH _durMs write sites so a queue-mutation republish cannot revert it.
    string? _durOverrideUri;
    long _durOverrideMs;
    // The LIVE-ness and NOW-PLAYING overrides, on exactly the duration override's terms: scoped to one playable uri,
    // re-asserted nowhere (they are folded on READ, not written into _durMs/_track) and dropped the moment the current
    // track changes. Live-ness is a fact the SOURCE stated; a broadcast's current-song title is a fact about the
    // BROADCAST, which is why neither is written back onto the catalogue row.
    string? _liveUri;
    // The ENGINE-AUTHORITATIVE half of live-ness: the media pipeline's own timeline (DVR window + live edge) for ONE
    // playable, on exactly the same scoping terms. It is a SEPARATE field from _liveUri because the two answers arrive
    // from different places at different times and neither may erase the other: a module states isLive at resolve
    // (before a single frame decodes), the engine states the WINDOW once metadata loads. IsLive is their union; the
    // window alone decides whether there is anything to scrub.
    string? _liveWindowUri;
    LiveWindow _liveWindow;
    string? _metaUri, _metaTitle, _metaArtist;
    double _speed = 1.0;   // playback rate folded from the cluster (remote) / 1.0 (local); applied in Pos()
    IReadOnlyList<QueueEntry> _queue = Array.Empty<QueueEntry>();
    string? _lastLocalQueueDiagSig, _lastViewerQueueDiagSig, _lastRemoteClusterDiagSig;
    // The active device's queue, verbatim from the last cluster (with uid+provider) — the source for a forwarded set_queue.
    IReadOnlyList<RemoteTrack> _clusterPrev = Array.Empty<RemoteTrack>(), _clusterNext = Array.Empty<RemoteTrack>();
    bool _canSkipNext = true, _canSkipPrev = true, _canSeek = true;   // from cluster restrictions (viewer); true when local
    // reconciliation
    long _lastLocalCmdWall = long.MinValue;

    /// <summary>The published effective state: what the player bar's play glyph, spinner, title and "Playing on …" line
    /// were last told.</summary>
    readonly record struct PublishedState(bool Playing, bool Buffering, string? TrackUri, OwnerKind Owner, string ActiveId);

    /// <param name="hydrator">THE metadata façade. REQUIRED and positional (wiring-discipline: no nullable seams, no
    /// defaulted ones either). A default was worse than a null check: "no backend" is a real, nameable configuration —
    /// <see cref="NotOwnedEntityHydrator.Instance"/> — and defaulting to it meant a half-wired composition root that
    /// simply forgot to pass the live façade compiled, ran, and silently never upgraded a thin now-playing row. Passing
    /// it is now the only way to build one, so "nothing to upgrade" is always something a call site CHOSE.</param>
    /// <param name="store">Where the upgraded row is READ BACK from (the hydrator writes the store, never returns rows).
    /// Same rule: a no-backend caller passes <c>new InMemoryStore()</c> and says so.</param>
    public NowPlayingProjection(string ourDeviceId, IEntityHydrator hydrator, IStore store,
        Func<long>? clock = null, Func<long>? serverNowUnixMs = null, double initialVolume01 = 0.7)
    {
        _hydrator = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ourDeviceId = ourDeviceId;
        _volume = Math.Clamp(initialVolume01, 0, 1);   // the announce + local host reconcile follow this (remember-volume seed)
        _now = clock ?? (() => Environment.TickCount64);
        // Estimated server-clock "now" in Unix ms, used only to age remote snapshots at fold. Default returns 0 (the
        // "unsynced" sentinel) so the offset-dependent network term stays off until a server clock is wired in.
        _serverNow = serverNowUnixMs ?? (() => 0L);
        // THE ownership authority. A claim's started_playing_at is compared by the SERVER against other devices' stamps,
        // so it is stamped on the server-corrected clock once synced (UtcNow until then); protection runs on the same
        // monotonic clock as everything else here.
        Ownership = new ConnectOwnership(ourDeviceId, nowMono: _now, startedAtMs: () =>
        {
            long s = _serverNow();
            return s > 0 ? s : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        });
        Ownership.Changed += OnOwnershipChanged;
        PlaybackBucketDiagnostics.Startup("projection", "created",
            WaveeLogField.Of("device", ourDeviceId),
            WaveeLogField.Of("initialVolume", initialVolume01));
    }

    /// <summary>Who owns playback — Us / Foreign(id) / Nobody — the ONE authority routing, the audio host, the wire's
    /// is_active and this display all read. Every cluster is folded through it first (<see cref="OnCluster"/>); the
    /// controller claims and releases on it; the publisher binds the claim's put-state to it.</summary>
    public ConnectOwnership Ownership { get; }

    /// <summary>True when WE own playback (the owner is Us). A cluster that merely NAMES us — our previous process's last
    /// state, echoed back at launch — is Nobody(StaleSelf), not this.</summary>
    public bool WeAreActive => Ownership.Current.Kind == OwnerKind.Us;

    /// <summary>The OWNER's device: Us → our id, Foreign → its id, Nobody → "". Never the raw cluster field — a stale
    /// cluster naming us, or a device that just disappeared, owns nothing (the raw value is
    /// <see cref="ClusterActiveDeviceId"/>).</summary>
    public string ActiveDeviceId => ActiveIdOf(Ownership.Current);

    /// <summary>The last folded cluster's own active_device_id, verbatim. Diagnostics; decisions read the owner.</summary>
    public string ClusterActiveDeviceId { get { lock (_gate) return _clusterActiveDeviceId; } }

    string ActiveIdOf(in OwnerState s) => s.Kind switch
    {
        OwnerKind.Us => _ourDeviceId,
        OwnerKind.Foreign => s.DeviceId,
        _ => "",
    };

    /// <inheritdoc cref="IPlaybackState.StreamBitrateKbps"/>
    public int StreamBitrateKbps { get { lock (_gate) return _streamBitrateKbps; } }
    /// <inheritdoc cref="IPlaybackState.StreamFormat"/>
    public string? StreamFormat { get { lock (_gate) return _streamFormat; } }

    /// <summary>Forget what is decoding. Called when playback ENDS and when another Connect device becomes active —
    /// the two ways "the stream this machine resolved" stops being what the user is hearing.</summary>
    void ClearStreamIdentityLocked() { _streamBitrateKbps = 0; _streamFormat = null; }
    /// <summary>The last-seen Connect queue revision (echoed on an outbound set_queue). "" until the first cluster.</summary>
    public string QueueRevision { get { lock (_gate) return _queueRevision; } }
    /// <summary>The active device's queue from the last cluster (uid+provider preserved) — what a forwarded set_queue
    /// rewrites. Empty until the first cluster. ClusterNextTracks = up-next (user queue then context continuation);
    /// ClusterPrevTracks = history.</summary>
    public IReadOnlyList<RemoteTrack> ClusterNextTracks { get { lock (_gate) return _clusterNext; } }
    public IReadOnlyList<RemoteTrack> ClusterPrevTracks { get { lock (_gate) return _clusterPrev; } }
    /// <summary>The most-recent folded cluster (full raw next/prev rows with uid+provider+metadata) — the controller replays
    /// it through <see cref="PlaybackSession.ReplaceFromCluster"/> for full session recovery on ghost-resume (§8). Null until
    /// the first cluster fold.</summary>
    public ClusterDelta? LastCluster { get { lock (_gate) return _lastCluster; } }
    public IReadOnlyDictionary<string, string> ContextMetadata { get { lock (_gate) return _contextMetadata; } }
    /// <summary>The session revision published by the last <see cref="ApplyLocalSnapshot"/> (0 until the first). Local only.</summary>
    public long LocalRevision { get { lock (_gate) return _localRevision; } }

    /// <summary>Resolve a viewer-queue row by the id minted in <see cref="MapQueue"/> — the viewer path of a queue-row click
    /// (the controller forwards next_track for the row). Best-effort: the id is valid against the most-recent cluster push.</summary>
    public bool TryGetViewerRow(QueueItemId id, out QueueEntry row)
    { lock (_gate) return _viewerRows.TryGetValue(id.Value, out row!); }

    /// <summary>The controller calls this the instant it issues a local optimistic command (a volume set, a transport
    /// verb), so a stale cluster volume echo arriving just after does not snap the slider back.</summary>
    public void NoteLocalCommand() { lock (_gate) _lastLocalCmdWall = _now(); }

    // ── IPlaybackState ────────────────────────────────────────────────────────────────────────────────────────────────
    public Track? CurrentTrack { get { lock (_gate) return TrackWithOverridesLocked(); } }
    public string? ContextUri { get { lock (_gate) return _contextUri; } }
    public bool IsPlaying { get { lock (_gate) return _isPlaying; } }
    // Prebuffering (playing the clear head while key+body resolve) reads as "buffering" to the UI so the player-bar's
    // indeterminate edge shows during the instant-start window without a new interface member.
    public bool IsBuffering { get { lock (_gate) return _isBuffering || _isPrebuffering; } }
    public bool IsPrebuffering { get { lock (_gate) return _isPrebuffering; } }
    public PlaybackRecoveryKind RecoveryKind { get { lock (_gate) return _recoveryKind; } }
    public long PositionMs { get { lock (_gate) return Pos(); } }
    /// <summary>The playable's total length, or 0 when there isn't one. A LIVE broadcast never has one — a
    /// broadcast has no end to count down to — so live-ness zeroes it on READ rather than by writing 0 into the
    /// slab: the moment live-ness stops applying (the next track), the real length is still there. Without this a
    /// stale length from the previous playable painted a remaining-time countdown over a stream that will never
    /// reach it.</summary>
    public long DurationMs { get { lock (_gate) return EffectiveDurationLocked(); } }
    public double Volume { get { lock (_gate) return _volume; } }
    public bool IsShuffle { get { lock (_gate) return _shuffle; } }
    public RepeatMode Repeat { get { lock (_gate) return _repeat; } }
    public IReadOnlyList<QueueEntry> Queue { get { lock (_gate) return _queue; } }
    public bool CanSkipNext { get { lock (_gate) return _canSkipNext; } }
    // Locally active (a live local session): the structural half (_canSkipPrev — history / a prior context row) plus the
    // >3 s restart affordance, derived at read because the playhead moves without a structural publish. Viewer: the
    // cluster restriction alone (findings fix §2).
    public bool CanSkipPrev { get { lock (_gate) return _canSkipPrev || (_hasLocalContext && Pos() > 3000); } }
    // A live broadcast is seekable EXACTLY as far as its DVR window reaches — so CanSeek while live is the window's own
    // answer, not a blanket no. A station with no rewind (ICY radio, a low-latency channel) reports no window and the
    // seek bar disarms; a YouTube/Twitch broadcast with a multi-hour window scrubs inside it. The seek bar and every
    // remote controller read this one answer.
    public bool CanSeek
    {
        get
        {
            lock (_gate)
            {
                if (!_canSeek) return false;
                if (!IsLiveLocked()) return true;
                return LiveWindowLocked().HasWindow;
            }
        }
    }

    /// <summary>Is the current playable a LIVE broadcast? The UNION of the two facts that can state it: a source-level
    /// override (<see cref="SetLiveOverride"/> — a module's <c>isLive</c>, an ICY stream) and the media pipeline's own
    /// timeline (<see cref="SetLiveWindow"/>). Either alone is enough; neither can erase the other.</summary>
    public bool IsLive { get { lock (_gate) return IsLiveLocked(); } }

    /// <inheritdoc cref="IPlaybackState.Live"/>
    public LiveWindow Live { get { lock (_gate) return LiveWindowLocked(); } }
    public IObservable<IPlaybackState> Changes => _changes;
    public IObservable<PositionSample> PositionTicks => _positionTicks;

    // IPlaybackState : INotifyPropertyChanged — consumers use Changes/PositionTicks; the INPC event is raised coarsely
    // (null name = "everything may have changed") for any INPC-based binder.
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    static readonly System.ComponentModel.PropertyChangedEventArgs AllChanged = new(null);

    // The published length, folded: 0 while live (see DurationMs). Caller holds _gate.
    long EffectiveDurationLocked() => IsLiveLocked() ? 0 : _durMs;

    long Pos()
    {
        long dur = EffectiveDurationLocked();   // a live broadcast has no ceiling to clamp the playhead against
        long cap = dur <= 0 ? long.MaxValue : dur;
        // The paused/buffering branch used to return _posMs RAW — no clamp at all, not even the [0, duration] one the
        // playing branch below already had. _posMs is folded from several places (a remote cluster snapshot aged
        // forward from ITS OWN timestamp, a host signal, a restored launch snapshot) and none of them is guaranteed to
        // land inside [0, duration] — a stale/corrupt upstream value (e.g. a launch-recovery snapshot written under a
        // momentarily-unknown duration, so an earlier session's own clamp here had nothing to clamp AGAINST) then
        // reached the player bar verbatim: a 2:54 track paused at "35:32 / −0:00". Clamping on EVERY read, playing or
        // not, means the bar can never show a position outside the track's own length regardless of how _posMs got
        // corrupted upstream.
        if (!_isPlaying || _isBuffering || _isPrebuffering) return Math.Clamp(_posMs, 0, cap);
        return Math.Clamp(_posMs + (long)((_now() - _posAnchorWall) * _speed), 0, cap);
    }

    // Clamp a content playback rate to Spotify's spoken-word range; invalid/zero ⇒ normal speed.
    static double NormalizeSpeed(double v) => v <= 0 || double.IsNaN(v) || double.IsInfinity(v) ? 1.0 : Math.Clamp(v, 0.5, 3.5);

    /// <summary>Allow the app to set a palette derived from the current art (off the slab path).</summary>

    /// <summary>Publish the media-authoritative duration for ONE playable (the real length of a user-attached local
    /// video, as reported by the media engine). Applies immediately when that playable is current, and is re-applied on
    /// every later local fold — so a queue mutation republishing the catalog duration can't revert it. Cleared
    /// automatically when the current track changes; <paramref name="durationMs"/> ≤ 0 clears it explicitly.</summary>
    public void SetDurationOverride(string? playableUri, long durationMs)
    {
        bool changed;
        lock (_gate)
        {
            if (playableUri is not { Length: > 0 } || durationMs <= 0)
            {
                changed = _durOverrideUri is not null;
                _durOverrideUri = null;
                _durOverrideMs = 0;
            }
            else
            {
                changed = _durOverrideMs != durationMs || !string.Equals(_durOverrideUri, playableUri, StringComparison.Ordinal);
                _durOverrideUri = playableUri;
                _durOverrideMs = durationMs;
                SyncDurationOverrideLocked();
            }
        }
        if (changed) FireChanges(WriterOverride);
    }

    /// <summary>The duration override in effect right now (0 = none). Diagnostics / tests.</summary>
    public long DurationOverrideMs { get { lock (_gate) return DurationOverrideAppliesLocked() ? _durOverrideMs : 0; } }

    /// <summary>Publish LIVE-ness for ONE playable — the structural twin of <see cref="SetDurationOverride"/>, and for
    /// the same reason: whether a broadcast has an end is a fact the SOURCE states (a module's <c>isLive</c>, an engine
    /// timeline's <c>IsLive</c>), not something the catalogue row carries. Scoped to one uri and dropped the moment the
    /// current track changes, so it can never leak onto the next song; <paramref name="isLive"/> false clears it.
    /// <para>While it applies, <see cref="CanSeek"/> reports false: there is no DVR window in v1, so a seek bar that
    /// accepted a scrub would be lying about what it can do.</para></summary>
    /// <param name="playableUri">The playable the fact is about; null/empty clears the override.</param>
    /// <param name="isLive">True to mark it live; false clears.</param>
    public void SetLiveOverride(string? playableUri, bool isLive)
    {
        bool changed;
        lock (_gate)
        {
            if (playableUri is not { Length: > 0 } || !isLive)
            {
                changed = _liveUri is not null;
                _liveUri = null;
            }
            else
            {
                changed = !string.Equals(_liveUri, playableUri, StringComparison.Ordinal);
                _liveUri = playableUri;
            }
        }

        if (!changed) return;
        LogLiveFlip("module", isLive ? playableUri : null);
        FireChanges(WriterOverride);
    }

    /// <summary>One always-on Info line per live-ness flip, from BOTH sources — the whole point of the log is that a
    /// live run can be diagnosed from the file alone ("did the source ever say live, and which one?"), which is exactly
    /// what was missing when the LIVE pill stayed dark. Not gated on anything: a flip is rare and never per frame.</summary>
    /// <param name="source">Which half stated it — <c>module</c> (the source's own <c>isLive</c>) or <c>window</c> (the
    /// media pipeline's timeline).</param>
    /// <param name="playableUri">The playable it is now on for, or null when the override was cleared.</param>
    static void LogLiveFlip(string source, string? playableUri)
        => WaveeLog.Instance.Info("playback", "live.override",
            playableUri is { Length: > 0 } uri
                ? "live override on for " + uri + " (source=" + source + ")"
                : "live override off (source=" + source + ")",
            WaveeLogField.Of("source", source),
            WaveeLogField.Of("uri", playableUri ?? ""),
            WaveeLogField.Of("on", playableUri is { Length: > 0 }));

    /// <summary>Publish the MEDIA-AUTHORITATIVE live timeline for ONE playable — the DVR window, the live edge and the
    /// playhead inside it, exactly as the media pipeline stated them. The structural twin of
    /// <see cref="SetDurationOverride"/>: scoped to one uri, dropped the moment the current track changes, folded on
    /// READ rather than written into <c>_durMs</c>.
    /// <para>It ALSO states live-ness, and that is deliberate: the engine is the only party that can see a moving live
    /// edge, so <see cref="IsLive"/> is the union of this and <see cref="SetLiveOverride"/> rather than either one
    /// alone. A module can say "this is a broadcast" before a frame decodes; the engine can say "and here is its
    /// four-hour window" a second later; neither answer may erase the other.</para>
    /// <para>A window ≥ <see cref="LiveWindow.MinWindowMs"/> is also what re-ARMS <see cref="CanSeek"/> while live —
    /// the DVR rail. Below that, live still means no scrubbing.</para></summary>
    /// <param name="playableUri">The playable the timeline is about; null/empty clears it.</param>
    /// <param name="window">The engine's timeline; <c>default</c> / <see cref="LiveWindow.None"/> clears it.</param>
    public void SetLiveWindow(string? playableUri, LiveWindow window)
    {
        bool changed;
        lock (_gate)
        {
            if (playableUri is not { Length: > 0 } || !window.IsLive)
            {
                changed = _liveWindowUri is not null;
                _liveWindowUri = null;
                _liveWindow = default;
            }
            else
            {
                changed = !string.Equals(_liveWindowUri, playableUri, StringComparison.Ordinal) || _liveWindow != window;
                _liveWindowUri = playableUri;
                _liveWindow = window;
            }
        }

        if (!changed) return;
        LogLiveFlip("window", window.IsLive ? playableUri : null);
        FireChanges(WriterOverride);
    }

    /// <summary>Publish a live NOW-PLAYING correction for ONE playable: the ICY <c>StreamTitle</c> a station pushes
    /// mid-stream, or a module's <c>playback/metadata</c>. Same scoping rules as the duration override — one uri,
    /// dropped at the next track change — and folded into <see cref="CurrentTrack"/> on read rather than written into
    /// the store, because it is a fact about the BROADCAST, not about a catalogue entity (the store has no row for it,
    /// which is also why <c>MaybeEnrichCurrent</c> can never clobber it).</summary>
    /// <param name="playableUri">The playable the correction is about; null/empty clears it.</param>
    /// <param name="title">The new title, or null to leave the catalogue title alone.</param>
    /// <param name="artist">The new artist/attribution line, or null to leave it alone.</param>
    public void SetMetadataOverride(string? playableUri, string? title, string? artist)
    {
        bool changed;
        lock (_gate)
        {
            if (playableUri is not { Length: > 0 } || (title is null && artist is null))
            {
                changed = _metaUri is not null;
                _metaUri = null;
                _metaTitle = null;
                _metaArtist = null;
            }
            else
            {
                changed = !string.Equals(_metaUri, playableUri, StringComparison.Ordinal)
                          || !string.Equals(_metaTitle, title, StringComparison.Ordinal)
                          || !string.Equals(_metaArtist, artist, StringComparison.Ordinal);
                _metaUri = playableUri;
                _metaTitle = title;
                _metaArtist = artist;
            }
        }

        if (changed) FireChanges(WriterOverride);
    }

    /// <summary>The metadata override in effect right now (both null = none). Diagnostics / tests.</summary>
    public (string? Title, string? Artist) MetadataOverride
    {
        get { lock (_gate) return MetadataOverrideAppliesLocked() ? (_metaTitle, _metaArtist) : (null, null); }
    }

    bool LiveOverrideAppliesLocked()
        => _liveUri is not null && _track is { } t && string.Equals(t.Uri, _liveUri, StringComparison.Ordinal);

    bool LiveWindowAppliesLocked()
        => _liveWindowUri is not null && _track is { } t && string.Equals(t.Uri, _liveWindowUri, StringComparison.Ordinal);

    // The two facts, folded. Caller holds _gate.
    //
    // The engine's window wins when there is one. When there ISN'T — an ICY radio station, or a broadcast whose module
    // said "live" before a frame decoded — Live still reports IsLive, with no window. That synthesis is deliberate and
    // load-bearing: without it Live.IsLive and IsLive would disagree for every audio-only live source, and a surface
    // that (reasonably) branched on the richer value alone would silently drop the LIVE chip for internet radio. A
    // source with no rewindable past has no broadcast clock to report either, so the positions stay 0 and the playhead
    // is at the edge by definition — which is exactly what the bar renders as a breathing line rather than a rail.
    LiveWindow LiveWindowLocked()
    {
        if (LiveWindowAppliesLocked() && _liveWindow.IsLive) return _liveWindow;
        if (LiveOverrideAppliesLocked()) return new LiveWindow(true, 0, 0, 0, 0, IsAtLiveEdge: true);
        return default;
    }

    bool IsLiveLocked() => LiveOverrideAppliesLocked() || (LiveWindowAppliesLocked() && _liveWindow.IsLive);

    bool MetadataOverrideAppliesLocked()
        => _metaUri is not null && _track is { } t && string.Equals(t.Uri, _metaUri, StringComparison.Ordinal);

    // Called immediately after every LOCAL _track write, exactly like SyncDurationOverrideLocked: a real track change
    // ends both overrides. Caller holds _gate.
    void SyncPlayableOverridesLocked()
    {
        if (_liveUri is not null && _track is not null && !LiveOverrideAppliesLocked()) _liveUri = null;
        if (_liveWindowUri is not null && _track is not null && !LiveWindowAppliesLocked())
        {
            _liveWindowUri = null;
            _liveWindow = default;
        }
        if (_metaUri is not null && _track is not null && !MetadataOverrideAppliesLocked())
        {
            _metaUri = null;
            _metaTitle = null;
            _metaArtist = null;
        }
    }

    // The now-playing row as the UI must see it: the catalogue/cluster row with the broadcast's own title/artist folded
    // over it. Caller holds _gate.
    Track? TrackWithOverridesLocked()
    {
        if (_track is not { } t || !MetadataOverrideAppliesLocked()) return _track;
        var artists = _metaArtist is { Length: > 0 } a
            ? new[] { new ArtistRef("", "", a) }
            : t.Artists;
        return t with
        {
            Title = _metaTitle is { Length: > 0 } newTitle ? newTitle : t.Title,
            Artists = artists,
        };
    }

    bool DurationOverrideAppliesLocked()
        => _durOverrideMs > 0 && _durOverrideUri is not null && _track is { } t
           && string.Equals(t.Uri, _durOverrideUri, StringComparison.Ordinal);

    // THE duration fold. Every input that can state a length goes through here, with the previous playable's uri as the
    // reference, because "how long is this?" and "is this still the same thing?" are one question:
    //
    //   • a stated length (> 0) always wins — the same "never regress to 0" rule as before;
    //   • a stated UNKNOWN (0) on a REAL track change is adopted verbatim. This is the fix for the stale countdown: the
    //     old rule ("write only when > 0") meant a playable whose length is unknown — a live broadcast, a module link
    //     resolved with durationMs 0 — inherited the PREVIOUS track's number, so a 3:25 song left "-3:25" ticking down
    //     over a stream that will never reach it. Unknown stays unknown until a duration override or a late duration
    //     arrives;
    //   • a stated unknown on a SAME-URI republish keeps what we already know (a queue mutation re-publishing a thin
    //     row must not erase a duration that is already correct);
    //   • <c>incomingMs &lt; 0</c> means the input stated nothing at all (a cluster with no track) — keep what we have.
    //
    // Caller holds _gate, and must call it AFTER the _track write, with the uri captured BEFORE it.
    void FoldDurationLocked(string? previousUri, long incomingMs)
    {
        if (incomingMs > 0) { _durMs = incomingMs; return; }
        if (incomingMs < 0) return;
        if (!string.Equals(previousUri, _track?.Uri, StringComparison.Ordinal)) _durMs = 0;
    }

    // Shared by the Paused/Ended/BecameInactive arms below (ApplyLocalSnapshot's local fold and OnEvent's twin): true
    // when the event names a DIFFERENT track than the one this projection is showing right now — the signature of a
    // late completion signal for a track the session has already advanced past (a stale Ended/Paused for the
    // OUTGOING track arriving after the incoming track's own Started). Null on either side is never a disagreement:
    // an event with no Track opinion, or a projection with none yet, has nothing to contradict. Folding such an
    // event's position/play-state anyway is exactly the observed corruption — pos=7271 playing=False stamped on a
    // track that had been playing for 0 ms, standing as the app's published truth for 59 s. Fold nothing instead.
    static bool DescribesAnotherTrack(Track? eventTrack, Track? currentTrack)
        => eventTrack is { } et && currentTrack is { } cur && !string.Equals(et.Uri, cur.Uri, StringComparison.Ordinal);

    // Called immediately after every LOCAL _durMs write: either re-assert the override (it outranks the catalog length) or
    // drop it because the current track has moved on. Caller holds _gate.
    void SyncDurationOverrideLocked()
    {
        if (_durOverrideUri is null) return;
        if (DurationOverrideAppliesLocked()) { _durMs = _durOverrideMs; return; }
        if (_track is not null) { _durOverrideUri = null; _durOverrideMs = 0; }   // a real track change ends the override
    }

    /// <summary>The Connect controller pushes QueueCore's snapshot here after a local queue change, so IPlaybackState.Queue
    /// (and the PutState next-up) reflect OUR local queue while the local session is what now-playing shows. While another
    /// device's state is on screen its queue is the cluster's, and this is withheld (see <see cref="LocalWritesShowLocked"/>).</summary>
    public void SetLocalQueue(IReadOnlyList<QueueEntry> queue)
    {
        string? ctx, current;
        lock (_gate)
        {
            if (!LocalWritesShowLocked()) return;
            _queue = queue;
            ctx = _contextUri;
            current = _track?.Uri;
        }
        PlaybackBucketDiagnostics.QueueIfChanged(ref _lastLocalQueueDiagSig, "projection.local.set", queue, ctx, current);
        FireChanges(WriterLocal);
    }

    // THE gate every LOCAL writer (the session's snapshot and events, the local options/queue setters, the local volume's
    // host sample, the host's own signals) passes before touching now-playing: the OWNER must show the local session — Us,
    // or a Nobody that is not a departed foreign device's snapshot. The writer IS the local session, so "has a local
    // session" is true by construction here. Read under _gate so a cluster fold that just made another device the owner
    // (its ownership fold runs before it takes _gate) can never be painted over by a local write that raced it: either the
    // local write sees the new owner and withholds, or it lands first and the cluster fold repaints over it.
    // Caller holds _gate.
    bool LocalWritesShowLocked() => PlaybackOwnership.ShowsLocalNowPlaying(Ownership.Current, hasLocalSession: true);

    /// <summary>Set the context display metadata (name/images the PutState publisher reads). Does NOT touch play-state /
    /// the queue / _contextUri — those arrive atomically via <see cref="ApplyLocalSnapshot"/> (F3: the split setter that
    /// let context and track publish at different times is gone). No FireChanges: the following ApplyLocalSnapshot fires.</summary>
    public void SetContextMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        lock (_gate)
            _contextMetadata = metadata is { Count: > 0 }
                ? new Dictionary<string, string>(metadata, StringComparer.Ordinal)
                : new Dictionary<string, string>();
    }

    /// <summary>The ONE atomic local publish (F3/F4/F6, §5): while WE are the active device, the session's snapshot AND the
    /// playback event fold under a single lock with a single FireChanges — the track, the display-windowed queue, the
    /// context, the options and the revision can never self-contradict for a frame. <paramref name="ev"/> null = a pure
    /// queue/options change (no play-state fold). Display windowing (history tail 16, next 50) lives here, never in the
    /// session core. A DEBUG assert fires if the published NowPlaying row's uri diverges from the current track.
    /// <para>Only while the owner shows the local session (<see cref="LocalWritesShowLocked"/>). While another device owns
    /// playback — or a device that just left still has its snapshot on screen — the session's snapshot is not what
    /// the user is looking at: the controller keeps its session warm (a continuation fetch, the BecameInactive of a
    /// takeover, a late Ended) and none of that may repaint the track, the playhead, the play-state, the queue, the context
    /// or the options the cluster owns. Only the session's own revision is recorded. A transfer-in is not affected: the
    /// controller CLAIMS before it loads, so the owner is Us by the time its Started lands here.</para></summary>
    public void ApplyLocalSnapshot(QueueSnapshot snap, PlaybackEvent? ev = null)
    {
        IReadOnlyList<QueueEntry> windowed;
        lock (_gate)
        {
            _localRevision = snap.Revision;   // a local-session fact, true whoever's state is on screen
            if (!LocalWritesShowLocked()) return;
            string? prevUri = _track?.Uri;
            _track = snap.Current?.Track ?? ev?.Track;   // the single source of "current" while the local session shows
            // A REAL boundary — never a same-uri republish — marks the instant after which any host tick's own
            // SampleQpc proves it belongs to this track. See _trackBoundaryQpc (OnHostSignal).
            if (!string.Equals(prevUri, _track?.Uri, StringComparison.Ordinal))
                _trackBoundaryQpc = Stopwatch.GetTimestamp();
            _trackWriter = WriterLocal;
            FoldDurationLocked(prevUri, _track?.DurationMs ?? -1);
            SyncDurationOverrideLocked();   // a media-authoritative length outranks the catalog one (and survives republishes)
            SyncPlayableOverridesLocked();  // …and a track change ends the live / now-playing overrides
            if (ev is { } e)
            {
                switch (e.Kind)
                {
                    case EvKind.Started:
                    case EvKind.Resumed:
                    case EvKind.TrackChanged:
                        _isPlaying = true; _isBuffering = false;
                        _canSkipNext = _canSeek = true;
                        // CanSkipPrev is DERIVED while locally active (findings fix §2), never the blanket true that made
                        // Previous an enabled no-op after a restore: history to step into, or a prior context row. The
                        // >3 s restart affordance folds in at the property read (position moves without a publish).
                        _canSkipPrev = snap.History.Length > 0 || snap.ContextCursor > 0;
                        _speed = 1.0; _posMs = e.AtMs; _posAnchorWall = _now();
                        break;
                    case EvKind.Paused:
                    case EvKind.Ended:
                    case EvKind.BecameInactive:
                        // A late Paused/Ended/BecameInactive for the track that just finished can race the snap
                        // (snap.Current, above) that already advanced past it — e.Track then names the OUTGOING
                        // track and e.AtMs is ITS playhead, not the one _track was just set to. See
                        // DescribesAnotherTrack. Fold nothing rather than stamping the new track with a stale
                        // position/play-state.
                        if (DescribesAnotherTrack(e.Track, _track)) break;
                        _canSkipPrev = snap.History.Length > 0 || snap.ContextCursor > 0;   // same derivation (recovery publishes Paused)
                        _isPlaying = false; _speed = 1.0; _posMs = e.AtMs; _posAnchorWall = _now();
                        // …and the transient flags with it. Paused/Ended/BecameInactive all mean "nothing is waiting on
                        // audio here", and NOTHING else can clear them on this path: the flags reach us from the CLUSTER
                        // fold (`_isBuffering = c.IsBuffering`), and the cluster that seeds launch recovery is our own
                        // last publish from a previous run — which can perfectly well have been written mid-load. With no
                        // local host running there is no Playing/Ended edge coming to retire it, so a stale is_buffering
                        // latched true for the whole session: the player bar's top edge swept forever over a track that
                        // was merely paused at its restored position. (SessionRecovery publishes exactly this event.)
                        _isBuffering = false; _isPrebuffering = false;
                        break;
                    case EvKind.Seeked:
                        // AtMs is the PRE-seek playhead (Gabo's segment-close reads it for exactly that reason —
                        // RawCoreStreamProjection.cs:72). A TIMELINE fold wants the seek's TARGET, or a seek while
                        // paused snaps the published position back to where you were.
                        _posMs = e.SeekToMs >= 0 ? e.SeekToMs : e.AtMs; _posAnchorWall = _now();
                        break;
                    case EvKind.VolumeChanged:
                        _posMs = e.AtMs; _posAnchorWall = _now();
                        break;
                    // OptionsChanged / QueueChanged: no play-state fold — options ride in the snapshot below.
                }
            }
            // bug 9: a QueueChanged/OptionsChanged/VolumeChanged event (or no event at all — the pure queue/options
            // overload) folds NO play-state above — but `_track` can already have advanced to a NEW current row by
            // then (a queue mutation firing between the outgoing track's Ended and the new track's own Started, which
            // hasn't reached us yet). Echoing whatever play-state the OUTGOING track left behind — typically
            // is_playing=false, straight from its own Ended fold — under the NEW track's identity is exactly the
            // mid-transition "not playing" lie a track that is actually starting must never publish. Only a genuine
            // play-state event (the switch above) may report NOT playing for the CURRENT track; a track change
            // reaching us any other way is assumed still playing until told otherwise.
            if (!string.Equals(prevUri, _track?.Uri, StringComparison.Ordinal) && _track is not null && ev is not
                { Kind: EvKind.Started or EvKind.Resumed or EvKind.TrackChanged or EvKind.Paused or EvKind.Ended or EvKind.BecameInactive })
            {
                _isPlaying = true; _isBuffering = false; _canSkipNext = _canSeek = true; _speed = 1.0;
                _canSkipPrev = snap.History.Length > 0 || snap.ContextCursor > 0;
            }
            _contextUri = snap.ContextUri;
            _hasLocalContext = !string.IsNullOrEmpty(snap.ContextUri);
            _shuffle = snap.Shuffle;
            _repeat = snap.Repeat;
            windowed = _queue = WindowQueue(snap);
            AssertCurrentMatchesNowPlaying();
        }
        FireChanges(WriterLocal);
        RestartTicker();
        MaybeEnrichCurrent();
    }

    // Display windowing (§5): history tail (≤16), current, user queue (uncapped), upcoming (≤50). History is local-only
    // and listed first so any consumer walking the flat queue sees buckets in panel order.
    static IReadOnlyList<QueueEntry> WindowQueue(in QueueSnapshot s)
    {
        const int NextCap = 50, HistoryTail = 16;
        int nUp = Math.Min(s.Upcoming.Length, NextCap);
        int firstH = Math.Max(0, s.History.Length - HistoryTail);
        var list = new List<QueueEntry>((s.History.Length - firstH) + 1 + s.UserQueue.Length + nUp);
        for (int h = firstH; h < s.History.Length; h++) list.Add(s.History[h]);
        if (s.Current is { } cur) list.Add(cur);
        for (int i = 0; i < s.UserQueue.Length; i++) list.Add(s.UserQueue[i]);
        for (int i = 0; i < nUp; i++) list.Add(s.Upcoming[i]);
        return list;
    }

    // DEBUG tripwire (§5): the log contradiction (Queue[NowPlaying].uri ≠ CurrentTrack.uri) that motivated the rework is
    // now structurally impossible — this proves it. [Conditional] → erased from the shipping AOT binary.
    [System.Diagnostics.Conditional("DEBUG")]
    void AssertCurrentMatchesNowPlaying()
    {
        for (int i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Bucket != QueueBucket.NowPlaying) continue;
            if (!string.Equals(_queue[i].Track.Uri, _track?.Uri, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"published state contradiction: NowPlaying row uri '{_queue[i].Track.Uri}' != CurrentTrack uri '{_track?.Uri}'");
        }
    }

    /// <summary>Controller pushes the local shuffle/repeat after a change so IPlaybackState + PutState reflect them while
    /// the local session shows (a cluster fold won't overwrite them then — local wins; and while the cluster's state is
    /// on screen, its options are the cluster's and this is withheld).</summary>
    public void SetLocalOptions(bool shuffle, RepeatMode repeat)
    {
        lock (_gate)
        {
            if (!LocalWritesShowLocked()) return;
            _shuffle = shuffle; _repeat = repeat;
        }
        FireChanges(WriterLocal);
    }

    /// <summary>Controller pushes the local volume after a change (so PutState carries it). 0..1. Also the optimistic
    /// slider for a REMOTE volume set, so it is not owner-gated.</summary>
    public void SetLocalVolume(double volume01) { lock (_gate) _volume = Math.Clamp(volume01, 0, 1); FireChanges(WriterLocal); }

    /// <summary>Local volume changes are also a fresh authoritative host-position sample. Fold both under one lock so the
    /// coarse volume notification cannot briefly publish an extrapolated/stale timeline before the next host tick. The
    /// sample is the LOCAL host's, so it moves the playhead only while the local session shows.</summary>
    internal void SetLocalVolume(double volume01, long positionMs)
    {
        lock (_gate)
        {
            _volume = Math.Clamp(volume01, 0, 1);
            if (LocalWritesShowLocked())
            {
                _speed = 1.0;
                long dur = EffectiveDurationLocked();
                _posMs = Math.Clamp(positionMs, 0, dur > 0 ? dur : long.MaxValue);
                _posAnchorWall = _now();
            }
        }
        FireChanges(WriterLocal);
    }

    // ── Remote (cluster) fold — ownership first, then the display the owner decides ──────────────────────────────────
    /// <summary>Folds one cluster: the OWNERSHIP fold first — outside <c>_gate</c>, because its <c>Changed</c> listeners stop
    /// hosts, emit BecameInactive and publish — then the display, under the owner that fold produced. Returns false when
    /// the frame was STALE (<see cref="OwnerFx.DropFrame"/>: older than one already folded): nothing of it is folded, and
    /// the caller must not fold it into the device roster either.</summary>
    public bool OnCluster(in ClusterDelta c)
    {
        PlaybackBucketDiagnostics.RemoteClusterIfChanged(ref _lastRemoteClusterDiagSig, "projection.cluster.raw", c);
        OwnerFx fx;
        bool transitioned;
        IReadOnlyList<QueueEntry>? viewerQueue = null;
        string? ctxForLog = null, currentForLog = null;
        // ONE cluster at a time, its ownership fold and its display fold together: a dealer push and a put-state response
        // can arrive on different threads, and F0 orders the OWNER — without this the older frame's display fold could
        // still land after the newer one's. Not _gate: the ownership fold's listeners run in here and take _gate themselves.
        lock (_clusterFold)
        {
            var outerFolding = t_folding;
            bool outerTransition = t_foldTransition;
            t_folding = this;
            t_foldTransition = false;
            try
            {
                fx = Ownership.OnCluster(new ClusterFrame(c.Origin, c.PutMsgId, c.ActiveDeviceId ?? "", c.ServerTimestampMs,
                    c.UpdateReason, c.ChangedDevices, c.ActiveStartedPlayingAt));
            }
            finally
            {
                transitioned = t_foldTransition;
                t_folding = outerFolding;
                t_foldTransition = outerTransition;
            }
            if ((fx & OwnerFx.DropFrame) == 0)
            {
                lock (_gate)
                {
                    // The owner as it stands NOW (a claim racing in on another thread since the fold above counts).
                    viewerQueue = FoldClusterLocked(c, Ownership.Current);
                    ctxForLog = _contextUri;
                    currentForLog = _track?.Uri;
                }
            }
        }

        if ((fx & OwnerFx.DropFrame) != 0)
        {
            // F0 — the ONE stale guard (it replaced this fold's own server-ts memory). Announce-response clusters are
            // folded as well as dealer pushes, so a slow PUT's answer can land after a newer push; folding it would
            // regress the active device / play-state to what it was before.
            long lastTs = Ownership.Current.LastServerTs;
            WaveeLog.Instance.Info("playback", "cluster.stale",
                "dropped stale cluster fold: serverTs=" + c.ServerTimestampMs + " < lastFolded=" + lastTs,
                WaveeLogField.Of("serverTs", c.ServerTimestampMs), WaveeLogField.Of("lastFolded", lastTs),
                WaveeLogField.Of("origin", c.Origin == ClusterOrigin.PutResponse ? "resp#" + c.PutMsgId : "push"));
            // …except that the verdict on our claim still SETTLES it when a newer push overtook it: publish that — and if
            // it handed playback to the device the newer push named, repaint the display from that push.
            if (transitioned) PublishTransition(intoForeign: Ownership.Current.Kind == OwnerKind.Foreign);
            return false;
        }

        if (viewerQueue is not null)
            PlaybackBucketDiagnostics.QueueIfChanged(ref _lastViewerQueueDiagSig, "projection.viewer.mapped",
                viewerQueue, ctxForLog, currentForLog);
        FireChanges(WriterCluster);   // …which also publishes an ownership transition this frame caused (see t_folding)
        RestartTicker();
        MaybeEnrichCurrent();   // the cluster track may be thin (no artist/art) → resolve + fold in the full metadata
        return true;
    }

    /// <summary>THE display fold of one cluster, under <paramref name="owner"/>. Returns the viewer queue it mapped (for the
    /// diagnostics line), or null when the local session owns the display. Caller holds <c>_gate</c>.</summary>
    IReadOnlyList<QueueEntry>? FoldClusterLocked(ClusterDelta c, in OwnerState owner)
    {
        // Raw cluster bookkeeping, true whoever owns playback: the controller replays LastCluster on a ghost-resume or a
        // Play after the owner left, and a forwarded set_queue rewrites the ACTIVE device's real queue (uid+provider).
        _lastCluster = c;
        _clusterActiveDeviceId = c.ActiveDeviceId ?? "";
        _queueRevision = c.QueueRevision ?? "";
        _clusterPrev = c.PrevTracks ?? Array.Empty<RemoteTrack>();
        _clusterNext = c.NextTracks;
        // ANOTHER DEVICE OWNS PLAYBACK ⇒ we know nothing about what is decoding. This clear is the load-bearing half of
        // the stream badge: without it, transferring playback to a phone leaves the last LOCAL stream's badge on screen
        // describing a stream this machine is no longer playing — precisely the "plausible lie" that kept that surface
        // empty for so long. Silence is the correct answer for a remote stream.
        if (owner.Kind == OwnerKind.Foreign) ClearStreamIdentityLocked();
        // The slider follows the ACTIVE device's volume — but never the cluster's while WE own playback (ours is set
        // locally; a push naming the phone while our claim is still protected would otherwise drag our slider, and through
        // the controller's volume echo our HOST, to the phone's level), and not inside our own local-command window (a
        // stale echo must not snap back an optimistic set). A genuine remote change on another device flows through.
        bool inLocalWindow = _lastLocalCmdWall != long.MinValue && (_now() - _lastLocalCmdWall) < LocalCmdWindowMs;
        if (owner.Kind != OwnerKind.Us && !inLocalWindow && c.ActiveVolume0_65535 >= 0) _volume = c.ActiveVolume0_65535 / 65535.0;

        // WHOSE STATE IS ON SCREEN — the owner decides (PlaybackOwnership.ShowsLocalNowPlaying):
        //   • Us ⇒ the local session + host, durably. Not a window: a cluster naming us only echoes what we published,
        //     and one naming someone else while our claim awaits its verdict (P2) may predate the claim. So the track, the
        //     context, the playhead, the play-state, the options, the restrictions and the queue are ALL left alone —
        //     including the duration, which used to be folded here unconditionally and, with a foreign push folding
        //     under a local track, clamped our playhead to the other track's length ("2:49 / -0:48").
        //   • Nobody with a local session (launch/recovery, after we released, a stale echo of our own state) ⇒ local too.
        //     The connect-state service never adopts a state whose context it cannot resolve (a playback module's link,
        //     a local folder), yet what comes back is still OUR OWN state, re-echoed with the row uris ConnectUriMask
        //     rewrote; taking it as the truth replaced a live local row with a `spotify:local:…` display uri, took a
        //     playing music video's surface down and left session.json recovering a track nothing can play.
        //   • Foreign, or Nobody right after a foreign device left (FromForeign) ⇒ the cluster's player_state. A departed
        //     phone's snapshot stays on screen, paused, until the user acts — never a flip to our stale session.
        if (PlaybackOwnership.ShowsLocalNowPlaying(owner, _hasLocalContext))
        {
            if (owner.Kind == OwnerKind.Us) AssertCurrentMatchesNowPlaying();   // the tripwire on the active cluster path too — local owns _track, so it can't diverge
            return null;
        }

        string? prevUri = _track?.Uri;   // the duration fold's reference — captured BEFORE the track merge below
        // Detect track change BEFORE merging — a fresh track's Timestamp can lag the prior track, so we must not age it.
        bool isNewTrack = c.HasTrack && (_track is null || !string.Equals(_track.Uri, c.Track.Uri, StringComparison.Ordinal));
        if (c.HasTrack)
        {
            var mapped = MapTrack(c.Track);
            _track = _track is { } cur && cur.Uri == mapped.Uri
                ? StoreEntityMerge.Track(cur, mapped)
                : mapped;
            _trackWriter = WriterCluster;
        }
        _contextUri = c.ContextUri;
        _hasLocalContext = false;
        _contextMetadata = new Dictionary<string, string>();
        FoldDurationLocked(prevUri, c.DurationMs > 0 ? c.DurationMs : (c.HasTrack ? c.Track.DurationMs : -1));
        SyncDurationOverrideLocked();   // …unless the media reported this playable's real length (a video is its own edit)
        SyncPlayableOverridesLocked();
        // Play-state. Foreign: the owner's own flags. Nobody showing the cluster's snapshot (a departed device's, or a stale
        // echo of ours with no local session to show instead): nobody is playing it — the "no active device ⇒ not
        // playing" clamp. The LOCAL transients go: host signals no longer fold under this display, so a prebuffering /
        // network-recovery flag latched by the host we just lost would otherwise sit over another device's playback.
        bool clusterPlaying = c.IsPlaying && !c.IsPaused;
        bool displayPlaying = owner.Kind == OwnerKind.Foreign && clusterPlaying;
        _isPlaying = displayPlaying;
        _isBuffering = c.IsBuffering;
        _isPrebuffering = false;
        _recoveryKind = PlaybackRecoveryKind.None;
        _shuffle = c.Shuffle;
        _repeat = c.Repeat;
        _canSkipNext = !c.DisallowSkipNext;   // the owner's restrictions (an ad, first/last track); true while local
        _canSkipPrev = !c.DisallowSkipPrev;
        _canSeek = !c.DisallowSeeking;
        // The remote position is a snapshot AS OF c.TimestampMs; by the time we fold it, it is already stale. Re-project
        // it to "now" as two isolated terms, then anchor in the monotonic domain so Pos() interpolates forward smoothly.
        //   serverSideAge — pure server-domain Δ (sample→emit); correct with NO clock sync, even fully offline.
        //   networkAge    — transit since the server emitted the cluster; needs a synced server clock (<=0 ⇒ skipped).
        // No aging unless the DISPLAY plays it (a paused remote — or a departed device's frozen snapshot — is never aged
        // forward), nor on a fresh near-zero track (its Timestamp may lag).
        _speed = NormalizeSpeed(c.PlaybackSpeed);
        long serverSideAge = c.ServerTimestampMs > 0 && c.TimestampMs > 0 ? Math.Max(0, c.ServerTimestampMs - c.TimestampMs) : 0;
        long serverNow = _serverNow();
        long networkAge = serverNow > 0 && c.ServerTimestampMs > 0 ? Math.Max(0, serverNow - c.ServerTimestampMs) : 0;
        long age = !displayPlaying || (isNewTrack && c.PositionAsOfMs <= 1000) ? 0 : serverSideAge + networkAge;
        _posMs = c.PositionAsOfMs + (long)Math.Round(age * _speed);
        _posAnchorWall = _now();
        // The owner's queue (a stale echo of ours with no local session included — otherwise a cold start shows the
        // cluster's track over an empty queue panel until the user presses Play).
        var viewerQueue = MapQueue(c.NextTracks, c.PrevTracks, c.HasTrack ? c.Track : null);
        _queue = viewerQueue;
        return viewerQueue;
    }

    /// <summary>Publishes an ownership transition no fresh cluster fold painted: a claim, a release, a protection expiry,
    /// or a verdict overtaken by a newer push. INTO Foreign that way (the claim rejected at expiry, or by the overtaking
    /// push) the display must become the new owner's — repainted from the last folded cluster, which IS the push that
    /// named it — instead of leaving the lost local session on screen until that device's next heartbeat.</summary>
    void PublishTransition(bool intoForeign)
    {
        IReadOnlyList<QueueEntry>? viewerQueue = null;
        if (intoForeign)
        {
            lock (_gate)
            {
                var owner = Ownership.Current;
                if (owner.Kind == OwnerKind.Foreign && _lastCluster is { } last) viewerQueue = FoldClusterLocked(last, owner);
            }
        }
        FireChanges(WriterOwner);
        if (viewerQueue is null) return;
        RestartTicker();
        MaybeEnrichCurrent();
    }

    /// <summary>Every ownership transition, raised by <see cref="ConnectOwnership"/> OUTSIDE its lock (never under
    /// <c>_gate</c> either: nothing here calls a mutating ownership method while holding it).</summary>
    void OnOwnershipChanged(OwnerTransition t)
    {
        if (t.To.Kind == OwnerKind.Us && t.To.Claim == ClaimPhase.Protected
            && (t.From.Kind != OwnerKind.Us || t.From.Claim != ClaimPhase.Protected))
            ArmProtectExpiry();
        if (t.To.Kind != OwnerKind.Foreign) Volatile.Write(ref _mixedWarnedFor, null);
        // Display-relevant changes only: the level-triggered StopHost re-raise on an unchanged Foreign (every heartbeat)
        // and a claim's put binding / started_at restamp change nothing the UI reads.
        if (t.From.Kind == t.To.Kind && t.From.Claim == t.To.Claim && t.From.Cause == t.To.Cause
            && string.Equals(t.From.DeviceId, t.To.DeviceId, StringComparison.Ordinal))
            return;
        // Raised from inside our own cluster fold: that fold paints it, with its frame, in one publish.
        if (ReferenceEquals(t_folding, this)) { t_foldTransition = true; return; }
        // Everything else — the bar re-reads ActiveDeviceId (owner-derived) on this publish. Guarded: this handler runs
        // BEFORE the controller's (subscribed at construction), and a display publish that throws must never cost the
        // listeners after us the transition — the controller stops the host there.
        try { PublishTransition(intoForeign: t.To.Kind == OwnerKind.Foreign && t.From.Kind != OwnerKind.Foreign); }
        catch (Exception ex)
        {
            WaveeLog.Instance.Error("playback", "projection.owner.publish", "now-playing publish of " + t.From.Describe()
                + " → " + t.To.Describe() + " failed", ex, WaveeLogField.Of("cause", t.Cause));
        }
    }

    /// <summary>A protected claim's expiry has no event of its own — the verdict (the claim's put-state response) either
    /// arrives or it does not. One one-shot timer, re-armed per protected claim, asks the fold to decide on what it saw
    /// meanwhile, a hair after the protection window so the fold's own monotonic check agrees it has passed.</summary>
    void ArmProtectExpiry()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _protectTimer ??= new Timer(static s => ((NowPlayingProjection)s!).OnProtectExpiry(), this, Timeout.Infinite, Timeout.Infinite);
            _protectTimer.Change(PlaybackOwnership.ClaimProtectMs + 50, Timeout.Infinite);
        }
    }

    void OnProtectExpiry() { if (!_disposed) Ownership.Tick(); }

    /// <summary>The now-playing row's own hydration: a cluster <c>player_state</c> is routinely THIN (no artist name,
    /// no album art, sometimes an album uri with no name), and the bar cannot paint from it. ONE predicate decides —
    /// <c>HydrationLevels.Of(track) &lt; Open</c>, the same rung every other surface uses — and at most one ask per uri
    /// is in flight; the answer is applied only if that uri is STILL current (the user didn't skip on).</summary>
    void MaybeEnrichCurrent()
    {
        string uri;
        string? warm = null;
        lock (_gate)
        {
            if (_track is not { } t) return;
            // The now-playing VIDEO warm (design §3): the badge + the switch-to-video affordance need the kind-99
            // association for the row that is playing, and that is true whether or not the row is thin — a fully
            // hydrated track still has to be asked about its video exactly once. Separate from the ladder ask below,
            // which returns early for anything already at Open.
            if (t.Uri.Length > 0 && _warmedUri != t.Uri) { _warmedUri = warm = t.Uri; }
            // Album identity is part of Open (Album.Name != ""), so the old extra "empty Album.Uri" term is subsumed
            // EXCEPT for the uri itself, which Of() cannot see — a cluster row can carry a named album with no uri, and
            // without it the player-bar title can never become an album hyperlink.
            //
            // An EPISODE is measured against the EPISODE rung, not the track one. Of(Track) demands named artists and an
            // album URI; a podcast row has neither by construction (a podcast has a show, not artists, and the show uri
            // is only there when the catalogue answered), so read through the track predicate a podcast is thin
            // FOREVER. Every cluster push, local snapshot and playback event calls this, so "resolve once" became
            // "resolve, and re-publish Changes, on every heartbeat" for the whole episode. These three terms are
            // HydrationLevels.Of(Episode) spelled against the slab row, with the show name in the album slot — minus
            // its duration term, deliberately: the fold below never writes DurationMs (the cluster's own length wins,
            // and a user-attached media override outranks both), so a term the resolve cannot move would only put the
            // loop back for a row the wire happened to send without one.
            bool thin = EntityUri.KindOf(t.Uri) == EntityKind.Episode
                ? HydrationLevels.TitleMissing(t.Title, t.Uri) || t.Album.Name.Length == 0
                  || !ImageSource.IsUsable(t.Image)
                : HydrationLevels.Of(t) < HydrationLevel.Open || string.IsNullOrEmpty(t.Album.Uri);
            uri = !thin || t.Uri.Length == 0 || _resolvingUri == t.Uri ? "" : (_resolvingUri = t.Uri);
        }
        // Outside the lock: both of these start work synchronously up to their first await.
        if (warm is not null) _ = _hydrator.EnsureTraitsAsync([warm], TraitSurface.NowPlaying);
        if (uri.Length > 0) _ = ResolveAsync(uri);
    }

    async Task ResolveAsync(string uri)
    {
        Track? enriched = null;
        try
        {
            // Priority 1: the user is LOOKING at this row, so it outranks every prefetch on the pump. The video warm
            // that used to be a separate fire-and-forget service call is the NowPlaying trait surface now.
            await _hydrator.EnsureAsync(uri, HydrationLevel.Open,
                new HydrationOptions(Surface: TraitSurface.NowPlaying, Priority: 1)).ConfigureAwait(false);
            enriched = _store.GetTrack(uri) ?? EpisodeAsTrack.From(_store.GetEpisode(uri));
        }
        catch { /* best-effort: the bar keeps the cluster snapshot */ }
        bool changed = false;
        lock (_gate)
        {
            if (_resolvingUri == uri) _resolvingUri = null;
            if (enriched is { } e && _track is { } cur && cur.Uri == uri)
            {
                // Keep the cluster's title (+ duration/position state); fill artist + album + art from the resolved track.
                var next = cur with
                {
                    Title = StoreEntityMerge.TitleMissing(cur.Title, cur.Uri) ? e.Title : cur.Title,
                    Artists = e.Artists.Count > 0 ? e.Artists : cur.Artists,
                    // NEVER trade a linked album ref for an unlinked one. The episode projection carries the show NAME
                    // and, whenever the catalogue write did not carry the show's gid, no show uri — so taking it
                    // wholesale erased the album/show link the cluster row already had, and the player-bar subtitle
                    // stopped being clickable.
                    Album = e.Album.Uri.Length == 0 && cur.Album.Uri.Length > 0
                        ? e.Album with { Id = cur.Album.Id, Uri = cur.Album.Uri }
                        : e.Album,
                    Image = ImageSource.ChooseBetter(e.Image, cur.Image),
                    Isrc = e.Isrc ?? cur.Isrc,   // carry the resolved ISRC onto the now-playing track (cluster track has none)
                };
                // Publish only a REAL change. A row the ladder cannot lift (an episode, a track the catalogue has no
                // better answer for) resolves to exactly what is already on the slab, and firing Changes for it woke
                // every player-bar/queue consumer on every cluster push for as long as it played.
                if (next != cur) { _track = next; _trackWriter = WriterEnrich; changed = true; SyncPlayableOverridesLocked(); }
            }
        }
        if (changed) FireChanges(WriterEnrich);
    }

    // ── Local fold — while the owner shows the local session (Stage E controller + Stage H host) ──────────────────────
    /// <summary>The local session's events. Folded only while the owner shows the local session — under another
    /// device's display a local Started/Ended/BecameInactive (a stray host, the takeover's own BecameInactive, a late
    /// Ended) is not what the user is looking at and changes nothing, the stream badge included.</summary>
    public void OnEvent(in PlaybackEvent e)
    {
        lock (_gate)
        {
            if (!LocalWritesShowLocked()) return;
            // Computed against _track BEFORE this event's own track fold below, and reused by the Paused/Ended/
            // BecameInactive arm further down — see DescribesAnotherTrack. Only those three kinds are late-signal
            // candidates (Started/TrackChanged/Resumed/Seeked describe what IS current by definition).
            bool staleForCurrentTrack = e.Kind is EvKind.Paused or EvKind.Ended or EvKind.BecameInactive
                && DescribesAnotherTrack(e.Track, _track);

            if (e.Track is not null && !staleForCurrentTrack)
            {
                string? prevUri = _track?.Uri;
                _track = e.Track;
                // A REAL boundary — never a same-uri republish (queue mutation, cluster echo) — marks the instant after
                // which any host tick's own SampleQpc proves it belongs to this track. See _trackBoundaryQpc.
                if (!string.Equals(prevUri, _track?.Uri, StringComparison.Ordinal))
                    _trackBoundaryQpc = Stopwatch.GetTimestamp();
                _trackWriter = WriterLocal;
                // Local events are authoritative while we're the active device — fold the duration too. Without this,
                // _durMs keeps the PREVIOUS track's length until a cluster echo arrives (never, when playing offline):
                // the player-bar label shows the old duration AND the seek bar scales scrub fractions by the wrong
                // length, so every committed seek targets the wrong millisecond.
                FoldDurationLocked(prevUri, e.Track.DurationMs);
                SyncDurationOverrideLocked();   // …unless the media itself reported a length for this exact playable
                SyncPlayableOverridesLocked();
            }
            switch (e.Kind)
            {
                case EvKind.Started:
                case EvKind.TrackChanged:
                    // The event that RESOLVED the stream carries its identity. Resumed does not re-resolve, so it
                    // deliberately falls through to the shared play-state arm below without touching it.
                    _streamBitrateKbps = e.SelectedBitrateKbps;
                    _streamFormat = string.IsNullOrEmpty(e.AudioFormatName) ? null : e.AudioFormatName;
                    goto case EvKind.Resumed;
                case EvKind.Resumed:
                    _isPlaying = true; _isBuffering = false;
                    _canSkipNext = _canSkipPrev = _canSeek = true;   // local playback → full local control
                    _speed = 1.0; _posMs = e.AtMs; _posAnchorWall = _now();
                    break;
                case EvKind.Ended:
                case EvKind.BecameInactive:
                    // A late signal for the track we've already left (see staleForCurrentTrack, top of method) — the
                    // stream it names stopped decoding before we even got here, not just now. Fold nothing: in
                    // particular, do NOT clear the stream identity for what is actually still playing.
                    if (staleForCurrentTrack) break;
                    // Nothing is decoding any more — the badge must go with it. (Paused does NOT clear: the stream is
                    // still the stream, it is simply not advancing.)
                    ClearStreamIdentityLocked();
                    goto case EvKind.Paused;
                case EvKind.Paused:
                    if (staleForCurrentTrack) break;
                    _isPlaying = false; _speed = 1.0; _posMs = e.AtMs; _posAnchorWall = _now();
                    break;
                case EvKind.Seeked:
                    // See ApplyLocalSnapshot's Seeked arm: AtMs is the PRE-seek playhead (Gabo's segment-close is the
                    // one consumer that wants that — RawCoreStreamProjection.cs:72); the published timeline wants
                    // the seek's TARGET.
                    _posMs = e.SeekToMs >= 0 ? e.SeekToMs : e.AtMs; _posAnchorWall = _now();
                    break;
                case EvKind.VolumeChanged:
                    _posMs = e.AtMs; _posAnchorWall = _now();
                    break;
                // OptionsChanged / QueueChanged: shuffle/repeat/queue arrive via SetLocal* — just notify.
            }
        }
        FireChanges(WriterLocal);
        RestartTicker();
        MaybeEnrichCurrent();
    }

    /// <summary>Retire a BUFFERING state that can no longer be cleared by the host that raised it. The controller swaps
    /// the ONE current-media host by disposing the outgoing host's signal subscription
    /// (<c>PlaybackController.SwitchHost</c>), so a host that was mid-buffer when it was swapped out can never deliver the
    /// Playing/Ended edge that would clear the flag — the spinner then latches over whatever plays next (the video ✕ →
    /// audio case: the video host is stopped while still Buffering). Deliberately narrow: it clears the two transient
    /// flags only, never play-state, position, or the track — and only a LOCAL display's: under another device's, the
    /// flag on screen is that device's (the cluster's), not a host's.</summary>
    public void ClearTransientBuffering()
    {
        lock (_gate)
        {
            if (!_isBuffering && !_isPrebuffering) return;
            if (!LocalWritesShowLocked()) return;
            _isBuffering = false;
            _isPrebuffering = false;
        }
        FireChanges(WriterHost);
    }

    /// <summary>The local host's clock and state edges. Folded ONLY while WE own playback: under any other owner the
    /// controller stops that host (Foreign) or it is a paused restore nobody asked to hear (Nobody), and folding it would
    /// paint the local playhead / play-state over another device's display — the PLAY-glyph-over-audio and
    /// "2:49 / -0:48" incidents (handoff-20260911-connect-detail.md §3.4). Nothing is folded then — no play-state, no
    /// position, no position tick — and a non-terminal signal while another device owns playback trips the one-shot
    /// <c>projection.mixed</c> Warn (a host running under a foreign owner is the controller's bug to find).</summary>
    public void OnHostSignal(in AudioHostSignal s)
    {
        bool structural = s.Kind != AudioHostSignalKind.PositionTick;
        bool ours, differs = false, preBoundaryTick = false;
        long clampedPos = 0;
        OwnerState owner;
        lock (_gate)
        {
            owner = Ownership.Current;   // under _gate — see LocalWritesShowLocked for why the order matters
            ours = owner.Kind == OwnerKind.Us;
            if (ours)
            {
                if (s.Kind == AudioHostSignalKind.Ended)
                {
                    _isPlaying = false;
                    _isBuffering = false;
                    _isPrebuffering = false;
                    _recoveryKind = PlaybackRecoveryKind.None;
                }
                else if (s.Kind == AudioHostSignalKind.Error)
                {
                    _isPlaying = false;
                    _isBuffering = false;
                    _isPrebuffering = false;
                    _recoveryKind = PlaybackRecoveryKind.None;
                }
                else
                {
                    // Buffering/Prebuffering state whether AUDIO IS FLOWING, not the user's play/pause INTENT — but
                    // AudioHostSignal's 2-arg constructor (every real Buffering/Prebuffering call site) only sets
                    // IsPlaying true for Playing/PositionTick/Recovering, so folding it here flipped the transport
                    // glyph to Play for the sub-second a normal, fast local track change prebuffers the next file —
                    // the user never asked to pause (finding S1 #4: "the glyph flips pause→play→pause"). The
                    // dedicated IsBuffering/IsPrebuffering flags below already carry that fact for the top-edge sweep
                    // (kept deliberately separate — see the file header on PlayerBar); a genuine pause still folds
                    // (Paused's own IsPlaying=false is real intent, and Paused is not excluded here).
                    if (s.Kind is not (AudioHostSignalKind.Buffering or AudioHostSignalKind.Prebuffering))
                        _isPlaying = s.IsPlaying;
                    _isBuffering = s.IsBuffering;
                    _isPrebuffering = s.IsPrebuffering;
                    _recoveryKind = s.RecoveryKind;
                }
                // A PositionTick sampled BEFORE the current track's own boundary (AudioHostSignal.SampleQpc predates
                // _trackBoundaryQpc) is the OLD stream's clock still catching up after Load() pointed the (reused) host
                // at the new track — see _trackBoundaryQpc. A tick can never legitimately predate the boundary that
                // started its own track, so this is a proof of staleness, not a heuristic. Skip ONLY the position fold:
                // _posMs is already right (the boundary just set it), and clampedPos below then derives honestly from
                // THAT instead of being clobbered by the old track's position read against the new track's duration
                // (finding S1 #4: the "3:04 / -0:24" flash on a track change). The state flags above still fold — a
                // fresh tick's IsPlaying/IsBuffering describe the CURRENT host correctly regardless of its position.
                preBoundaryTick = !structural && s.SampleQpc < _trackBoundaryQpc;
                if (!preBoundaryTick) { _speed = 1.0; _posMs = s.PositionMs; _posAnchorWall = _now(); }
                // Does this fold disagree with what the UI last SAW (committed by FireChanges, whoever published it)? A
                // PositionTick carries the live IsPlaying/IsBuffering, so a missed Playing edge — or a publish from another
                // writer that said "not playing" while the host plays on — is corrected by the very next tick instead of
                // leaving the bar stuck showing Buffering / a PLAY glyph over audible playback.
                differs = PublishedLocked(owner) != _lastPub;
                // A2/A3 clock-flicker fix: the host's raw signal position is unclamped (a device-format soft reload can
                // hand back a fresh session mid-restore, or a stale reload-window tick can carry a torn value) — push the
                // SAME clamped read every other reader of position uses (Pos(), [0, duration]) onto the ticks stream too,
                // instead of the raw s.PositionMs. Read under _gate: Pos() assumes the caller already holds it.
                clampedPos = Pos();
            }
        }
        if (!ours)
        {
            if (owner.Kind == OwnerKind.Foreign) WarnMixedOnce(owner, s);
            return;
        }
        if (structural || differs) { FireChanges(WriterHost); RestartTicker(); }
        // The host's OWN sample time (s.SampleQpc), not "now" — clampedPos was derived from Pos() under the same lock
        // as the raw s.PositionMs write above, so the two describe the identical instant the host stamped. A
        // preBoundaryTick never reached that write (its position was rejected), so publishing it here would pair the
        // CURRENT (correct) position with the OLD track's sample instant — a mismatched (position, time) the lyrics
        // clock would extrapolate from as if 0 had just happened at a moment that already passed. Drop it outright.
        if (!structural && !preBoundaryTick) _positionTicks.OnNext(new PositionSample(clampedPos, s.SampleQpc));
    }

    // The tripwire for a host still running while another device owns playback: one Warn per foreign owner episode (reset
    // when the owner stops being Foreign). Terminal edges (Paused/Ended/Error) are exempt — a host the controller just
    // stopped legitimately reports its own stop.
    void WarnMixedOnce(in OwnerState owner, in AudioHostSignal s)
    {
        if (s.Kind is AudioHostSignalKind.Paused or AudioHostSignalKind.Ended or AudioHostSignalKind.Error) return;
        string id = owner.DeviceId;
        if (string.Equals(Interlocked.Exchange(ref _mixedWarnedFor, id), id, StringComparison.Ordinal)) return;
        WaveeLog.Instance.Warn("playback", "projection.mixed",
            "host signal " + s.Kind + " while " + owner.Describe() + " owns playback — not folded; this host should have been stopped",
            WaveeLogField.Of("kind", s.Kind.ToString()), WaveeLogField.Of("owner", owner.Describe()),
            WaveeLogField.Of("pos", s.PositionMs), WaveeLogField.Of("playing", s.IsPlaying));
    }

    // The effective state the UI reads, as the published-state memory compares it. Caller holds _gate.
    PublishedState PublishedLocked(in OwnerState owner)
        => new(_isPlaying, _isBuffering || _isPrebuffering, _track?.Uri, owner.Kind, ActiveIdOf(owner));

    /// <summary>THE publish. Commits what the UI is about to see as the last-published state (so every later fold compares
    /// against what was really shown — see <c>_lastPub</c>), logs <c>projection.publish</c> when that state changed
    /// and <c>nowplaying.identity</c> once per suspicious (uri, shape), then notifies. <paramref name="writer"/> names who
    /// caused it: cluster | local | host | enrich | override | owner.</summary>
    void FireChanges(string writer)
    {
        if (_disposed) return;
        PublishedState now, before;
        OwnerState owner;
        Track? suspect = null;
        IdentitySuspicion shape = IdentitySuspicion.None;
        string suspectWriter = "";
        lock (_gate)
        {
            owner = Ownership.Current;
            now = PublishedLocked(owner);
            before = _lastPub;
            _lastPub = now;
            var shown = TrackWithOverridesLocked();
            var s = NowPlayingIdentity.Suspicion(shown);
            if (s != IdentitySuspicion.None && shown is not null
                && (s != _identityShape || !string.Equals(shown.Uri, _identityUri, StringComparison.Ordinal)))
            {
                _identityUri = shown.Uri;
                _identityShape = s;
                suspect = shown;
                shape = s;
                suspectWriter = MetadataOverrideAppliesLocked() ? WriterOverride : _trackWriter;
            }
        }
        if (now != before) LogPublish(writer, now, owner);
        if (suspect is not null) LogIdentity(suspect, shape, suspectWriter);
        _changes.OnNext(this);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    static void LogPublish(string writer, in PublishedState s, in OwnerState owner)
    {
        string who = owner.Describe();
        WaveeLog.Instance.Info("playback", "projection.publish",
            "now-playing " + (s.Playing ? "playing" : "not playing") + (s.Buffering ? " (buffering)" : "")
            + " track=" + (s.TrackUri ?? "-") + " owner=" + who + " writer=" + writer,
            WaveeLogField.Of("writer", writer), WaveeLogField.Of("playing", s.Playing),
            WaveeLogField.Of("buffering", s.Buffering), WaveeLogField.Of("track", s.TrackUri ?? ""),
            WaveeLogField.Of("owner", who), WaveeLogField.Of("active", s.ActiveId));
    }

    // The data-side tripwire for #139's shapes ("LP / LP"): the row as PUBLISHED, and who wrote it last. Identity only —
    // nothing here changes a title (a legitimately self-titled track is flagged once and left exactly as it is).
    static void LogIdentity(Track t, IdentitySuspicion shape, string lastWriter)
    {
        var names = new string[t.Artists.Count];
        for (int i = 0; i < names.Length; i++) names[i] = t.Artists[i].Name;
        string artists = string.Join(" / ", names);
        WaveeLog.Instance.Info("playback", "nowplaying.identity",
            "suspicious now-playing identity (" + shape + "): uri=" + t.Uri + " title='" + t.Title + "' artists='" + artists
            + "' lastWriter=" + lastWriter,
            WaveeLogField.Of("shape", shape.ToString()), WaveeLogField.Of("uri", t.Uri), WaveeLogField.Of("title", t.Title),
            WaveeLogField.Of("artists", artists), WaveeLogField.Of("writer", lastWriter));
    }

    // A 1 Hz tick re-anchors the UI position WHILE PLAYING only (zero ticks when paused — the guardrail).
    void RestartTicker()
    {
        bool playing; lock (_gate) playing = _isPlaying && !_isBuffering && !_isPrebuffering;
        if (playing) { _ticker ??= new Timer(_ => Tick(), null, 1000, 1000); }
        else { _ticker?.Dispose(); _ticker = null; }
    }

    void Tick()
    {
        long pos; bool playing; lock (_gate) { pos = Pos(); playing = _isPlaying && !_isBuffering && !_isPrebuffering; }
        // A projected value: Pos() extrapolated it to THIS instant, so "now" — Stopwatch.GetTimestamp() — is its
        // honest sample time, unlike OnHostSignal's tick which carries the host's own.
        if (playing) _positionTicks.OnNext(new PositionSample(pos, Stopwatch.GetTimestamp()));
    }

    static Track MapTrack(in RemoteTrack r)
    {
        // An ArtistRef with BOTH name and uri empty isn't an artist — it's the absence of one. Cluster next_tracks
        // routinely carry no artist at all, and allocating one anyway made the classic queue row paint
        // "" + "  ·  " + "": a stray dot with nothing on either side (visible in screenshots). An empty Artists list
        // instead lets HydrationLevels.TitleMissing (Wavee.Core/Hydration/HydrationLevel.cs) skeletonise the row.
        var artists = r.ArtistName.Length == 0 && r.ArtistUri.Length == 0
            ? Array.Empty<ArtistRef>()
            : new ArtistRef[] { new(EntityUri.IdOf(r.ArtistUri), r.ArtistUri, r.ArtistName) };
        var album = new AlbumRef(EntityUri.IdOf(r.AlbumUri), r.AlbumUri, r.AlbumName);
        Image? img = string.IsNullOrEmpty(r.ImageUrl) ? null : new Image(r.ImageUrl!);
        return new Track(EntityUri.IdOf(r.Uri), r.Uri, r.Title, artists, album, r.DurationMs, HasVideoMetadata(r), img);
    }

    // Viewer-mode queue: the active device's next_tracks split by provider, PRECEDED by its prev_tracks as a History tail
    // (oldest→newest, ≤16 to mirror WindowQueue) — the viewer half of the prev_tracks restore (findings fix §2; dropping
    // them was the same bug as ReplaceFromCluster's cleared History).
    IReadOnlyList<QueueEntry> MapQueue(IReadOnlyList<RemoteTrack> next, IReadOnlyList<RemoteTrack>? prev, RemoteTrack? current = null)
    {
        const int HistoryTail = 16;   // mirrors WindowQueue's display cap
        _viewerRows.Clear();
        int prevCount = prev?.Count ?? 0;
        if (next.Count == 0 && prevCount == 0 && current is null) return Array.Empty<QueueEntry>();
        var list = new List<QueueEntry>(Math.Min(prevCount, HistoryTail) + 1 + next.Count);
        if (prev is not null)
        {
            for (int i = Math.Max(0, prevCount - HistoryTail); i < prevCount; i++)
            {
                if (string.IsNullOrEmpty(prev[i].Uri) || prev[i].Uri == "spotify:delimiter") continue;
                string provider = string.IsNullOrEmpty(prev[i].Provider) ? "context" : prev[i].Provider;
                list.Add(ViewerEntry(prev[i], QueueBucket.History, provider));
            }
        }
        if (current is { Uri: { Length: > 0 } uri } cur && uri != "spotify:delimiter")
        {
            string provider = string.IsNullOrEmpty(cur.Provider) ? "context" : cur.Provider;
            list.Add(ViewerEntry(cur, QueueBucket.NowPlaying, provider));
        }
        for (int i = 0; i < next.Count; i++)
        {
            if (next[i].Uri == "spotify:delimiter") continue;   // queue/context boundary marker
            string provider = string.IsNullOrEmpty(next[i].Provider) ? "context" : next[i].Provider;
            list.Add(ViewerEntry(next[i], provider == "queue" ? QueueBucket.UserQueue : QueueBucket.NextUp, provider));
        }
        return list;
    }

    QueueEntry ViewerEntry(in RemoteTrack r, QueueBucket bucket, string provider)
    {
        var id = new QueueItemId(ViewerIdBase + (ulong)(++_viewerIdSeq));
        var entry = new QueueEntry(id, "i" + id.Value, MapTrack(r), bucket,
            QueueProviderExtensions.FromWire(provider), provider == "autoplay", r.Uid, r.Metadata);
        _viewerRows[id.Value] = entry;
        return entry;
    }

    // Shared with the inbound-transfer video restore (PlaybackController.HandleInboundTransferAsync) — see
    // MediaSwitchLogic.HasVideoMetadata for the one key list both readers check.
    static bool HasVideoMetadata(in RemoteTrack r) => MediaSwitchLogic.HasVideoMetadata(r.Metadata);


    public void Dispose()
    {
        _disposed = true;
        Ownership.Changed -= OnOwnershipChanged;
        _ticker?.Dispose();
        _ticker = null;
        lock (_gate) { _protectTimer?.Dispose(); _protectTimer = null; }
    }
}

// IConnectDevices backed by the cluster device roster. TransferAsync is wired to the controller in Stage E.
public sealed class LiveConnectDevices : IConnectDevices
{
    readonly SimpleSubject<IReadOnlyList<PlaybackDevice>> _changed = new(Array.Empty<PlaybackDevice>());
    IReadOnlyList<PlaybackDevice> _devices = Array.Empty<PlaybackDevice>();

    /// <summary>Wired in Stage E (issues the outbound transfer command). Null → transfer is a no-op for now.</summary>
    public Func<string, CancellationToken, Task>? TransferHandler { get; set; }

    public IReadOnlyList<PlaybackDevice> Devices => _devices;
    public IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged => _changed;
    public Task TransferAsync(string deviceId, CancellationToken ct = default) => TransferHandler?.Invoke(deviceId, ct) ?? Task.CompletedTask;

    public void Update(IReadOnlyList<ConnectDeviceRow> rows)
    {
        // Cluster heartbeats routinely repeat the roster. Compare the complete PUBLIC projection before allocating:
        // retaining this array also preserves downstream signal identity, so unchanged devices do not rebuild the bar.
        // Order is observable in the picker; every projected field (including rounded volume) participates.
        var current = _devices;
        bool unchanged = current.Count == rows.Count;
        for (int i = 0; unchanged && i < rows.Count; i++)
        {
            var row = rows[i];
            var device = current[i];
            unchanged = device.Id == row.Id && device.Name == row.Name && device.Kind == row.Kind
                && device.IsActive == row.IsActive && device.VolumePercent == VolumePercent(row.Volume0_65535);
        }
        if (unchanged) return;

        var list = new PlaybackDevice[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            list[i] = new PlaybackDevice(r.Id, r.Name, r.Kind, r.IsActive, VolumePercent(r.Volume0_65535));
        }
        _devices = list;
        _changed.OnNext(list);
    }

    static int VolumePercent(int volume0_65535) => (int)Math.Round(volume0_65535 / 655.35);
}
