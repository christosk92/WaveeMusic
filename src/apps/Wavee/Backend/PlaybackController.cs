using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.Backend;

// ── Stage E — the PlaybackController (the live IPlaybackPlayer orchestrator + Connect command arbitration) ────────────
// Routing spine (see docs/plans/wavee-playback-arbitration-rules.md): for EVERY verb,
//   LOCAL  ⇔ cluster.ActiveDeviceId is empty OR == us   (we take over / we are the player)
//   REMOTE ⇔ another device is active                   (forward the command to it)
// The cluster is the single source of truth for "who is active" — no local flag. Ghost resume seeds local playback from
// the cluster snapshot when the play button is pressed while nothing is loaded locally. Inbound REQUEST commands (we are
// the target) always execute LOCALLY regardless of the routing rule (the dealer only routes them to us when we're active).

public interface ITrackResolver
{
    Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default);
}

public readonly record struct PlaybackTrackMeta(byte[] MediaId, byte[] FileId, int BitrateKbps, string AudioFormat, long DurationMs);

public sealed class StubTrackResolver : ITrackResolver
{
    public Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
        => Task.FromResult(new AudioStreamHandle(track.Uri, "", "", default, AudioFormat.OggVorbis320, track.DurationMs, 0f));
}

/// <summary>The result of an outbound player command: HTTP ok + the ack_id the server echoes (optimistic correlation —
/// we do not block-wait; a failure surfaces via the cluster, and the status/ack_id are surfaced for logging).</summary>
public readonly record struct OutboundResult(bool Ok, string? AckId, int Status);

/// <summary>Sends an outbound player command to the active device (we are the controller). Proto-free JSON over spclient.</summary>
public interface IOutboundControl
{
    Task<OutboundResult> SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default);
    /// <summary>Set a remote device's volume via the dedicated PUT /connect-state/v1/connect/volume endpoint (NOT a
    /// player/command verb). <paramref name="volume0_65535"/> is Spotify's 0..65535 scale.</summary>
    Task<OutboundResult> SetVolumeAsync(string targetDeviceId, int volume0_65535, CancellationToken ct = default);
    /// <param name="hostingVideo">True when the transfer-away hands off a locally-hosted video (bug 8) — stamps
    /// <c>options.modes.video_persistence = "VIDEO"</c> so the target picks up the hand-off already in video mode.</param>
    Task<OutboundResult> TransferAsync(string fromDeviceId, string targetDeviceId, CancellationToken ct = default, bool hostingVideo = false);
}

/// <summary>POSTs /connect-state/v1/player/command/from/{us}/to/{target} with the command JSON envelope, and parses the
/// server's ack_id from the response (best-effort).</summary>
public sealed class LiveOutboundControl : IOutboundControl
{
    readonly ITransport _transport;
    readonly string _ourDeviceId;
    readonly Func<string?>? _connectionId;
    public LiveOutboundControl(ITransport transport, string ourDeviceId, Func<string?>? connectionId = null)
    { _transport = transport; _ourDeviceId = ourDeviceId; _connectionId = connectionId; }

    public async Task<OutboundResult> SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default)
    {
        var route = $"/connect-state/v1/player/command/from/{_ourDeviceId}/to/{targetDeviceId}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["X-Transfer-Encoding"] = "gzip",
        };
        if (_connectionId?.Invoke() is { Length: > 0 } connId) headers["X-Spotify-Connection-Id"] = connId;
        var resp = await _transport.Request(Channel.Spclient, route,
            HttpCompression.Gzip(Encoding.UTF8.GetBytes(commandJson)), ct, headers: headers).ConfigureAwait(false);
        return new OutboundResult(resp.Ok, ParseAckId(resp), resp.Status);
    }

    public async Task<OutboundResult> SetVolumeAsync(string targetDeviceId, int volume0_65535, CancellationToken ct = default)
    {
        var route = $"/connect-state/v1/connect/volume/from/{_ourDeviceId}/to/{targetDeviceId}";
        var resp = await _transport.Request(Channel.Spclient, route, OutboundEnvelope.ConnectVolumeBody(volume0_65535), ct, "PUT").ConfigureAwait(false);
        return new OutboundResult(resp.Ok, ParseAckId(resp), resp.Status);
    }

    public async Task<OutboundResult> TransferAsync(string fromDeviceId, string targetDeviceId, CancellationToken ct = default, bool hostingVideo = false)
    {
        var route = $"/connect-state/v1/connect/transfer/from/{fromDeviceId}/to/{targetDeviceId}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
        };
        if (_connectionId?.Invoke() is { Length: > 0 } connId) headers["X-Spotify-Connection-Id"] = connId;
        var body = Encoding.UTF8.GetBytes(OutboundEnvelope.Transfer(NewId(), NewId(), Guid.NewGuid().ToString(), "premium", hostingVideo));
        var resp = await _transport.Request(Channel.Spclient, route, body, ct, headers: headers).ConfigureAwait(false);
        return new OutboundResult(resp.Ok, ParseAckId(resp), resp.Status);
    }

    static string? ParseAckId(Resp resp)
    {
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return null;
        try { using var doc = JsonDocument.Parse(resp.Body); return doc.RootElement.TryGetProperty("ack_id", out var a) ? a.GetString() : null; }
        catch { return null; }
    }

    static string NewId() => Guid.NewGuid().ToString("N");
}

public sealed class PlaybackController : IPlaybackPlayer, IDisposable
{
    readonly PlaybackSession _session = new();
    QueueSnapshot _snap;                   // the latest atomic snapshot (published via ApplyLocalSnapshot); the ONE truth
    // ── the ONE current-media host (Milestone B) ────────────────────────────────────────────────────────────────────
    // The current media is EITHER audio or video, swapped under one clock. Common transport verbs go through _currentHost;
    // audio-specific loading (Load/LoadFastStart/SupplyBody) always targets _audioHost (only reached when the current kind
    // is audio/local). _videoHost is the optional video-media host (null on the fake/silent backend); the swap picks it for
    // a video playable. _currentKind tracks which kind the current media is, so the swap knows when the host must flip.
    readonly IAudioHost _audioHost;
    readonly IMediaHost? _videoHost;
    IMediaHost _currentHost;
    PlayableKind _currentKind = PlayableKind.Audio;
    readonly ITrackResolver _resolver;
    readonly IFastTrackResolver? _fast;   // when set, local play uses instant-start (head before key); else the plain resolve
    readonly NowPlayingProjection _projection;
    readonly IContextResolver _contexts;
    readonly ITransferStateDecoder? _transferDecoder;
    readonly IOutboundControl? _outbound;
    readonly IReadOnlyList<IPlaybackProjection> _extra;
    readonly string _ourDeviceId;
    readonly string _featureVersion;
    readonly WaveeLogger _log;
    IDisposable _hostSub;                   // reassigned when the current-media host is swapped (audio ↔ video)
    int _hostGeneration;
    readonly IPreparedAudioHost? _preparedHost;
    readonly IDisposable? _transitionSub;
    readonly IDisposable _projSub;
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly object _ownershipGate = new();
    string _lastActive = "";
    double _lastVolume = -1;
    double _lastIntentVolume = double.NaN;
    readonly TrailingCoalescer _remoteVolumeTx = new(400);
    bool _ownsActivePlayback;
    string? _nextPageUrl;
    bool _contextIsInfinite;
    string? _autoplayLatchedFor;
    Task<ResolvedContext>? _continuationFetch;
    string _commandIdHex = "";
    PlaybackIds? _currentIds;
    string? _idsSessionContext;
    string _reasonStart = "clickrow";
    string? _lastControllerQueueDiagSig;
    // The last inbound set_queue revision actually applied (F8 ordering guard): a later delivery carrying an OLDER
    // revision is a reordered/duplicate and must return Superseded, leaving the session untouched, rather than
    // silently stomping rows a newer set_queue already replaced. Parsed as ulong (the wire is a bare unsigned integer
    // that can exceed Int64); an unparseable revision keeps applying unconditionally, exactly as before this guard —
    // the single-worker dealer channel is what orders those.
    ulong? _lastAppliedQueueRevision;
    readonly object _prepareGate = new();
    CancellationTokenSource? _prepareCts;
    string? _preparedToken;
    string? _preparedSignature;
    QueueItemId _preparedItemId;
    long _prepareSequence;
    PlaybackFailureCheckpoint? _failureCheckpoint;
    // The resume/seek target THIS load asked the host to open at (0 for a fresh start), set at the top of every real
    // load. Guards OnHostSignal's Ended handling: a RESUMED load (nonzero target) whose first report back is Ended at
    // ~0 never actually played what it opened — a natural end lands at/after where it started, never behind it. This
    // is a FAILED OPEN wearing an end-of-track costume (a reopen racing a just-attached, still-streaming body — see
    // the resume-position seek race), and auto-advancing on it is exactly the "pressed play 4-5 times before a song
    // actually played" defect (every failed reopen silently skips the queue forward instead of reporting). Scoped to
    // RESUMED loads only — a fresh (0-target) load that ends at 0 is an ordinary short/empty playable, not a failure.
    long _loadResumeTargetMs;
    long _contextGeneration;
    // The generation an EXPLICIT play intent (ExecutePlayAsync / PlayTrackAsync) minted before its resolve started —
    // read by MaybeScheduleSessionRecovery / RunSessionRecoveryAsync so a launch-recovery fold that is mid-resolve when
    // the user presses Play can never win the race and seed a stale cluster snapshot over the intent that is still
    // in flight (Seam B §7: recovery must not eat the first explicit play). An explicit intent is always the NEWEST
    // generation by construction, so "equal to the current generation" is exactly "there is one outstanding" — but
    // only once one has actually been minted: 0 is "no explicit play intent yet" (Interlocked.Increment never
    // produces 0), and _contextGeneration also starts at 0, so a naive equality check bails on EVERY launch before
    // the user has ever pressed Play, permanently blocking session recovery.
    long _explicitPlayGeneration;
    string _remoteSessionId = "";
    string _remoteInteractionId = "";
    string _remotePageInstanceId = "";
    bool _connectOriginatedPlayback;
    // ── launch/session restore (playback-restore fix design) ───────────────────────────────────────────────────────────
    // 0 = armed, 1 = running, 2 = seeded/done. Armed again after an attempt that found nothing to seed, so a later
    // cluster fold (reconnect with a still-empty session) can retry; a successful seed is once-per-session.
    int _recoveryState;
    // The session was SEEDED (cluster recovery) but nothing was loaded on the host yet — the first Resume must go through
    // LoadAndPlayCurrentAsync (fast-start at the stored position), not a bare host.Play() over empty media (§1).
    bool _restorePendingLoad;
    // Launch/ghost restore is audio-first, exactly like Connect-originated playback: video restores PLACEMENT only, never
    // live playback (§8). Cleared by ClearRemotePlaybackIds (an explicit local media intent).
    bool _restoreAudioFirst;
    // One skip-to-next per restored playable whose resolve failed (§6.6) — a second failure reports instead of looping.
    string? _unplayableSkippedUri;
    // Seam B (local ownership lifecycle, §3) — the uri an ownership-regained auto-reload has already been scheduled
    // for, so a flapping cluster fold (active → us → active → us again before the first attempt has cleared
    // _restorePendingLoad) cannot schedule the same reload twice. Follows the _videoRecoveryUri / _unplayableSkippedUri
    // pattern: it self-supersedes the moment the current track changes, and the scheduled task itself clears it once
    // the attempt is done (success, bail, or failure) so a LATER teardown of the SAME track can still auto-reload.
    string? _ownershipReloadUri;
    // Set immediately before a load that must not be seen by the outside world: a launch/snapshot restore (§8) or the
    // ownership-regained auto-reload (§3) both load PAUSED so the next real Play() is instant, but neither is a user
    // action, so the wire (_extra fan-out) must not be told. Consumed — and always cleared — by the very next Publish.
    bool _quietRestorePublish;

    readonly record struct PlaybackFailureCheckpoint(string TrackUri, long PositionMs);

    /// <summary>The persisted local session snapshot (session.json's playback section), consumed on the empty-cluster
    /// launch path (§8). Wired by PlaybackBridge to SessionSnapshotStore; null (unit tests / fake backend) = no local
    /// fallback, the empty-cluster resume stays a no-op.</summary>
    public Func<PlaybackSessionSnapshot?>? RestoreSnapshot { get; set; }

    /// <summary>Whether a local session exists (read off the immutable published snapshot — safe off-lock, F7). The
    /// snapshot writer gates on this so viewer-mode pushes never overwrite the persisted LOCAL session.</summary>
    public bool HasLocalSession => _snap.Current is not null;

    /// <summary>Snapshot-writer hints read off the immutable published snapshot (safe off-lock, F7): the context cursor
    /// the current row resides at (-1 = outside the spine) + the active autoplay source uri.</summary>
    public (int ContextIndex, string? AutoplayContextUri) RestoreWriterHints
        => (_snap.ContextCursor, _snap.AutoplayContextUri);

    // Test seams (the test assembly source-includes this file): the continuation page url the heal restored, and the
    // published atomic snapshot.
    internal string? NextPageUrlForTest => _nextPageUrl;
    internal QueueSnapshot SnapForTest => _snap;
    // Milestone C (video smooth-switching): StartVideoLoadDetached spawns the off-lock RunVideoLoadAsync continuation
    // fire-and-forget — production never awaits it. A test that needs the detached video load (or its off-lock
    // no-source→audio fallback, which re-acquires _lock) to have SETTLED before asserting sets this to capture the
    // Task, then awaits it. Set synchronously inside the same _lock-held call that spawns it, so a hook set BEFORE
    // calling into the controller is guaranteed to observe the spawn. Null in production.
    internal Action<Task>? VideoLoadSpawnedForTest { get; set; }

    public PlaybackController(IAudioHost host, ITrackResolver resolver, NowPlayingProjection projection,
        IContextResolver contexts,
        string ourDeviceId, IOutboundControl? outbound = null, IReadOnlyList<IPlaybackProjection>? extraProjections = null, WaveeLogger log = default,
        string? playFeatureVersion = null, IFastTrackResolver? fast = null, IMediaHost? videoHost = null,
        ITransferStateDecoder? transferDecoder = null)
    {
        _audioHost = host;
        _currentHost = host;                 // audio is the current media until a video boundary swaps it
        _videoHost = videoHost;
        _snap = _session.Snapshot();
        _resolver = resolver;
        _fast = fast;
        _projection = projection;
        _contexts = contexts;
        _transferDecoder = transferDecoder;
        _ourDeviceId = ourDeviceId;
        _outbound = outbound;
        _extra = extraProjections ?? Array.Empty<IPlaybackProjection>();
        _log = log;
        _featureVersion = playFeatureVersion ?? OutboundEnvelope.DefaultFeatureVersion;
        _hostSub = SubscribeHost(_currentHost, _hostGeneration);
        _preparedHost = host as IPreparedAudioHost;   // prepared-next/crossfade is an AUDIO-host capability only
        _transitionSub = _preparedHost?.Transitions.Subscribe(Observers.From<AudioTransitionSignal>(OnAudioTransition));
        _projSub = projection.Changes.Subscribe(Observers.From<IPlaybackState>(OnProjectionChanged));
        PlaybackBucketDiagnostics.Startup("controller", "created",
            WaveeLogField.Of("device", ourDeviceId),
            WaveeLogField.Of("outbound", outbound is not null),
            WaveeLogField.Of("fast", fast is not null),
            WaveeLogField.Of("extraProjections", _extra.Count));
    }

    public IPlaybackState State => _projection;

    /// <summary>See <see cref="IPlaybackPlayer.ShouldPauseOnSuspend"/>: a sleeping laptop must pause OUR OWN audible
    /// playback, never a remote device's — RouteLocal() alone isn't enough (an empty active-device id also reads as
    /// local, but might mean "nothing is loaded here at all"), so ClockValid narrows it to "and this host actually
    /// has media running."</summary>
    public bool ShouldPauseOnSuspend => RouteLocal() && _currentHost.ClockValid;

    /// <summary>When set, LOCAL playback is rejected at every point that would start/seed the (silent) local host: the hook
    /// fires (the app shows the "playback on this device isn't supported yet — choose a remote device" toast) and the
    /// operation aborts. Null (the default — unit tests, and a future real-audio build) leaves local playback enabled.
    /// Remote forwarding is never affected. Wired by the live bootstrap to <c>PlaybackBridge.NotifyLocalPlaybackUnsupported</c>.</summary>
    public Action? OnLocalPlaybackRejected { get; set; }
    public Func<bool>? AutoplayEnabled { get; set; }
    public Func<Track, CancellationToken, Task<PlaybackTrackMeta?>>? MetaResolver { get; set; }
    public Func<string, CancellationToken, Task<long>>? EpisodeResumeMicros { get; set; }

    /// <summary>The corrected "server now" (unix ms) — the SAME estimator the projection's own remote-position aging
    /// reads (<c>SpotifyServerClock.ServerNowUnixMs</c>) — used to age a transferred position across the wire delay in
    /// <c>HandleInboundTransferAsync</c>. Returns 0 when unsynced (the estimator's own sentinel), in which case NO
    /// extrapolation is applied: a small constant staleness beats unbounded skew against a clock we don't trust yet.
    /// NULL (unit tests, an unwired build) is the identical "no extrapolation" answer. Wired by the live bootstrap
    /// (<c>LiveConnect</c>) to its <c>SpotifyServerClock</c> instance.</summary>
    public Func<long>? ServerNowUnixMs { get; set; }

    /// <summary>Tell the WIRE ONLY that we're inactive (an empty active-device-id cluster echo — see
    /// <see cref="OnProjectionChanged"/>) — never through Publish/EmitState, which would also fold BecameInactive
    /// into the LOCAL projection and poison the position/play-state for a transition that never touched the local
    /// host. Null (unit tests, an unwired build) is a silent no-op. Wired by the live bootstrap (<c>LiveConnect</c>)
    /// to its <c>DeviceStatePublisher.PublishInactive</c>.</summary>
    public Action? PublishInactiveOnWire { get; set; }

    /// <summary>Publish our CURRENT player state to the wire UNGATED (the same <c>force</c> path the music-video badge
    /// upgrade uses) — called by <see cref="TransferToAsync"/> immediately before forwarding a transfer we own, so the
    /// cluster hands the target device the position/play-state the user is actually at right now, not whatever we last
    /// told it (which can be seconds stale — nothing re-announces on a steady-state timer). Null (unit tests, an
    /// unwired build) is a silent no-op. Wired by the live bootstrap (<c>LiveConnect</c>) to
    /// <c>DeviceStatePublisher.PublishStateChanged</c>.</summary>
    public Action? PublishFreshStateOnWire { get; set; }

    /// <summary>Milestone B / M0 — decides whether the given playable should play as VIDEO right now (that track has a music
    /// video AND the user's sticky "watch video" intent is live and not dismissed for this content). When null (the default —
    /// unit tests and audio-only builds, which never wire the hooks) every playable is treated as AUDIO, so
    /// the audio path is byte-for-byte unchanged. Wired by <c>LiveConnect.WireVideoMedia</c> to
    /// <c>PlaybackBridge.ShouldPlayAsVideo</c> (which folds the one pure <c>VideoPlacementLogic.VideoActive</c> rule).</summary>
    public Func<Track, bool>? ShouldPlayAsVideo { get; set; }

    /// <summary>Source-agnostic seam — does this playable have NO audio form at all? A playback module resolves some
    /// playables as <c>MediaForm.Video</c> (a YouTube/Twitch stream): there is no second, audio-only body behind them, so
    /// the audio host can only ever refuse them ("Restricted: video playable; use the video host"). Every rule that
    /// DOWNGRADES a playable to audio — Connect's audio-first preference, the launch restore's audio-first preference,
    /// the user's video-surface intent — describes a CHOICE between two forms, and must not apply where there is no
    /// choice. Wired by the live bootstrap to <c>ModulePlayables.HasVideo</c>; NULL (unit tests, audio-only builds) makes
    /// every playable choosable, i.e. byte-for-byte today's behaviour.</summary>
    public Func<string, bool>? IsVideoOnlyPlayable { get; set; }

    /// <summary>Source-agnostic seam — can ANY local media provider play this uri? Wired by the live bootstrap to
    /// <c>MediaProviderRegistry.OwnerOf(uri) is not null</c>. Used by the launch-recovery paths to refuse to seed a
    /// current row nothing here can play — most importantly the Connect cluster's echo of OUR OWN masked publish
    /// (<c>spotify:local:…</c>, see <c>ConnectUriMask</c>), which is not a playable at all but a display shape. NULL
    /// (unit tests, the fake bootstrap) trusts every uri, exactly as before.</summary>
    public Func<string, bool>? IsPlayableHere { get; set; }

    /// <summary>Source-agnostic seam — "who OWNS this uri, and what does it say the playable is?". Consulted for every
    /// playable outside Spotify's catalogue (<see cref="ContextResolve.IsSpotifyContext"/> answers false) BEFORE this
    /// controller settles for the uri-only placeholder the catalogue hydration mints for a uri it has never heard of.
    /// <para>THE DEFECT IT CLOSES: a restart restored a playback-module playable — a YouTube broadcast — as its own raw
    /// uri. Nothing in the persisted session or in the Connect cluster carries a module's display facts (a cluster row
    /// only ever carries what WE last published, masked), and no catalogue can be asked about a <c>wavee:module:…</c>
    /// uri, so the restored row had the uri for a title, no artwork, no artists, a length that belonged to the last
    /// thing the media engine measured, and no live-ness. Re-hydrating THROUGH the owner is the only honest answer:
    /// the module re-resolves the playable, which also fills the resolve cache, so <c>HasVideo</c>/<c>IsLive</c> are
    /// known at restore time instead of a moment too late.</para>
    /// <para>Wired by the live bootstrap to the module host's resolve; NULL (unit tests, audio-only builds) leaves the
    /// placeholder path byte-for-byte as it was. A failure (offline, the module was uninstalled, a dead locator) is
    /// NOT an error: it degrades to the placeholder at INFO — a launch must never put a toast on screen.</para></summary>
    public Func<string, CancellationToken, Task<Track?>>? HydratePlayable { get; set; }

    /// <summary>Milestone B / M0 — loads the given VIDEO playable onto the injected video host: the delegate resolves the
    /// track's <c>PopOutVideoSource</c> (PlaybackBridge.ResolveVideoSourceForPlaybackAsync) and calls
    /// <c>FluentVideoMediaHost.LoadVideo</c>, returning true once a source has started opening and FALSE when the track has no
    /// playable video (so this controller can fall back to audio instead of leaving the user in silence). Kept as a delegate
    /// so the portable controller never references the SpotifyLive video types. Wired by <c>LiveConnect.WireVideoMedia</c>.</summary>
    /// <para>The <c>long</c> is the position the video session must START at (<c>&lt;= 0</c> = from the beginning). It
    /// travels with the LOAD rather than as a follow-up seek because at the moment of an audio→video swap the video
    /// player does not exist yet — see <c>VideoLoadRequest</c>.</para>
    public Func<Track, long, CancellationToken, Task<bool>>? LoadCurrentVideoAsync { get; set; }

    /// <summary>Source-agnostic seam — may a prepared (gapless/crossfaded) hand-off be scheduled INTO this playable? Wired
    /// by the live bootstrap to the media-provider registry's <c>SupportsPreparedNext</c>, so a source that ships without
    /// the prepared-next capability takes the proven Ended→AutoAdvance hard cut instead. NULL (the default — unit tests and
    /// the fake/silent bootstrap) allows every hand-off, so the audio path is byte-for-byte unchanged.</summary>
    public Func<Track, bool>? CanPrepareNext { get; set; }

    /// <summary>Source-agnostic seam — a VIDEO that failed to OPEN gets one chance to be recovered before the failure is
    /// reported. Consulted in <c>OnHostSignal</c> on an Error while the current media is Video, BEFORE
    /// <c>ReportPlaybackError</c>. The hook returns TRUE when it has changed something that makes a retry meaningful (the
    /// live impl quarantines an unplayable local attachment so the resolver walks past it to the source's own video /
    /// audio), in which case this controller simply re-runs the load; FALSE means "nothing to do" and the error is
    /// reported exactly as it is today. NULL (the default — unit tests, audio-only builds) is byte-identical to today.
    /// <para>Called fire-and-forget from the host-signal callback and holding NO lock (the same discipline as
    /// AutoAdvanceAsync — taking <c>_lock</c> inside a host signal is what deadlocked track-end). At most ONE recovery
    /// per playable per visit (see <c>_videoRecoveryUri</c>): a second failure on the same uri reports.</para></summary>
    public Func<Track, CancellationToken, Task<bool>>? TryRecoverVideoAsync { get; set; }

    /// <summary>Raised when VIDEO definitively did NOT happen for a playable that the app believes has one: the video
    /// source resolved to nothing (we fell back to audio), or the open failed and <see cref="TryRecoverVideoAsync"/>
    /// declined. The app-side surfaces mount off AVAILABILITY ("does this uri have a video"), which stays true in both
    /// cases — so without this edge a mounted surface waits forever for a source that will never arrive (an indeterminate
    /// "Loading" poster sitting over audio that is already playing). Wired by <c>LiveConnect.WireVideoMedia</c> to
    /// <c>PlaybackBridge.NotifyVideoMediaEnded</c>, which latches THAT PLAYABLE — never the user's standing intent, so
    /// sticky "watch video" and sticky-off both keep their exact meaning. NULL (unit tests, audio-only builds) = today.
    /// <para>Invoked while <c>_lock</c> is held, so the handler must be non-blocking (the live one posts to the UI thread).</para></summary>
    public Action<Track>? OnVideoMediaUnavailable { get; set; }

    /// <summary>Milestone C (video smooth-switching, A4) — called from <see cref="SchedulePreparedNext"/> when the
    /// UPCOMING playable (the one prepared-next would consider) will play as VIDEO: gives the app a chance to warm the
    /// video source (resolve + CDN edge warm) BEFORE the current track ends, mirroring the audio prepared-next warm
    /// this same method does for an audio upcoming. This is a NOTIFICATION only — the controller never awaits it and
    /// does nothing else with a video upcoming (crossfade/prepared-next stays an AUDIO-only capability, per
    /// <see cref="MediaSwitchLogic.AllowCrossfade"/>). Wired by <c>LiveConnect.WireVideoMedia</c> to
    /// <c>PlaybackBridge.PrefetchVideoSource</c>. Fail-soft like every other app-facing hook: NULL (unit tests,
    /// audio-only builds) makes this a no-op. <para>Invoked while <c>_lock</c> is held — non-blocking only.</para></summary>
    public Action<Track>? PrefetchVideo { get; set; }

    // The loop guard's playable half: the uri a video recovery has already been attempted for. Cleared when a DIFFERENT
    // playable loads, never by the recovery's own reload — otherwise the fallback could ping-pong forever. The (uri, source
    // key) half lives in the hook's own quarantine, which is what makes the retry resolve to something different.
    string? _videoRecoveryUri;
    string? _videoAudioFallbackUri;

    /// <summary>The kind the ONE current media is playing as right now (Audio until a video boundary swaps it). The single
    /// truth Connect's <c>track_player</c> derives from via <see cref="MediaSwitchLogic.TrackPlayer"/> — never a bare
    /// has-video flag.</summary>
    public PlayableKind CurrentMediaKind => _currentKind;

    // The kind that selects the current playable's host. isVideoTrack is playback INTENT (music video + user prefers video),
    // supplied by ShouldPlayAsVideo — never a bare HasVideo flag — so an unwired build always resolves to audio. Fail-soft:
    // a throwing predicate (it reads app signals) degrades to AUDIO rather than breaking playback.
    PlayableKind KindFor(Track t)
    {
        // NO AUDIO FORM ⇒ NO CHOICE. A module video playable (a YouTube/Twitch stream) exists only on the video host;
        // every "prefer audio" rule below — and the user's surface intent — is a preference BETWEEN two forms, so none of
        // them may apply here. Without this, any re-entry into the load path (a Retry, a cluster echo that took the video
        // surface down, a prepared-next probe) re-hosted a playing video onto the audio host, which answered
        // "Restricted: video playable; use the video host" and stopped playback with an error toast.
        if (IsVideoOnly(t)) return PlayableKind.Video;
        // Connect play/transfer is audio-first even when the user has a standing local video preference — and so is a
        // launch/ghost restore (§8: video restores placement only). Explicit local playback clears both latches before
        // resolving its media kind.
        if (_connectOriginatedPlayback || _restoreAudioFirst)
            return MediaSwitchLogic.KindOf(false, t.Origin == TrackOrigin.Local);
        if (string.Equals(_videoAudioFallbackUri, t.Uri, StringComparison.Ordinal))
            return MediaSwitchLogic.KindOf(false, t.Origin == TrackOrigin.Local);
        bool isVideo = false;
        if (ShouldPlayAsVideo is { } predicate)
        {
            try { isVideo = predicate(t); }
            catch (Exception ex) { _log.Info($"ShouldPlayAsVideo threw for {t.Uri}; treating as audio: {ex.GetType().Name}: {ex.Message}"); }
        }
        return MediaSwitchLogic.KindOf(isVideo, t.Origin == TrackOrigin.Local);
    }

    // Fail-soft, like every other app-facing hook: a throwing predicate answers "it has an audio form", which is the
    // proven path. Never a bare flag — the answer belongs to whoever resolved the playable.
    bool IsVideoOnly(Track t)
    {
        if (_videoHost is null || IsVideoOnlyPlayable is not { } probe) return false;
        try { return probe(t.Uri); }
        catch (Exception ex) { _log.Info($"IsVideoOnlyPlayable threw for {t.Uri}; treating as choosable: {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    // "Can anything here play this uri?" — false ONLY when the hook is wired and says no, so an unwired build trusts
    // every uri exactly as before.
    bool PlayableHere(string uri)
    {
        if (IsPlayableHere is not { } probe) return true;
        try { return probe(uri); }
        catch (Exception ex) { _log.Info($"IsPlayableHere threw for {uri}; treating as playable: {ex.GetType().Name}: {ex.Message}"); return true; }
    }

    // "Ask whoever owns this uri what the playable actually is." Null on every path that must keep today's behaviour:
    // no hook wired, a Spotify uri (the catalogue owns it — the hook is never even called for one), or an owner that
    // could not answer. NEVER throws except on cancellation: an owner that is offline, uninstalled or out of date must
    // degrade to the placeholder silently, because the only caller is a launch-time restore.
    async Task<Track?> HydrateThroughOwnerAsync(string uri, CancellationToken ct)
    {
        if (HydratePlayable is not { } hydrate) return null;
        if (string.IsNullOrEmpty(uri) || ContextResolve.IsSpotifyContext(uri)) return null;
        try
        {
            var track = await hydrate(uri, ct).ConfigureAwait(false);
            if (track is null || string.IsNullOrEmpty(track.Uri)) return null;
            // A RESTORED playable carries NO length. The persisted/cluster length is whatever the media engine last
            // measured — for a broadcast that is a sliding DVR window, i.e. a number that was never the playable's
            // duration and is stale the instant it is written down. The owner states the length when (and if) it
            // resolves; until then 0 is the honest answer and the one the LIVE rail is built on.
            return track.DurationMs == 0 ? track : track with { DurationMs = 0 };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Info($"owner hydrate declined for {uri}; keeping the catalogue row: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // The cluster/ghost half: the row the cluster handed us for a uri no catalogue owns is display data we published
    // ourselves a session ago (title, duration and all). Re-ask the owner; keep the cluster row when it cannot answer.
    async Task<Track> ReHydrateThroughOwnerAsync(Track track, CancellationToken ct)
        => await HydrateThroughOwnerAsync(track.Uri, ct).ConfigureAwait(false) ?? track;

    // …and the same for a RESTORED user queue: a module playable sitting in Next-up is the identical thin row, and the
    // panel shows it long before anyone plays it. Allocation-free (and awaitless) for the overwhelmingly common case of
    // a queue that is entirely Spotify: the array is only built once an owner has actually replaced something.
    async Task<IReadOnlyList<QueuedTrack>> ReHydrateQueueThroughOwnersAsync(IReadOnlyList<QueuedTrack> rows, CancellationToken ct)
    {
        if (HydratePlayable is null || rows.Count == 0) return rows;
        QueuedTrack[]? patched = null;
        for (int i = 0; i < rows.Count; i++)
        {
            if (await HydrateThroughOwnerAsync(rows[i].Track.Uri, ct).ConfigureAwait(false) is not { } owned) continue;
            if (patched is null)
            {
                patched = new QueuedTrack[rows.Count];
                for (int k = 0; k < rows.Count; k++) patched[k] = rows[k];
            }
            patched[i] = rows[i] with { Track = owned };
        }
        return patched ?? rows;
    }

    // Point the current-media host at the given instance, stopping the outgoing one first (bug 1 — never two decoders at
    // once) and moving the signal subscription with it, so EXACTLY ONE host feeds OnHostSignal. A no-op when the instance is
    // unchanged (Audio↔LocalFile share the audio host), so a same-host reload keeps the audio fast-start / prepared-next path
    // untouched. stopOutgoing comes from MediaSwitchLogic.ShouldStopOutgoingHost — never inline logic.
    void SwitchHost(IMediaHost target, bool stopOutgoing = true)
    {
        if (ReferenceEquals(_currentHost, target)) return;
        // Recovery/fault state belongs to the retiring host and must not leak across an audio/video boundary.
        _videoRecoveryUri = null;
        _videoAudioFallbackUri = null;
        _failureCheckpoint = null;
        var outgoing = _currentHost;
        // Local-video duration outranks the catalog while Video is the current media. Leaving Video must drop it so the
        // player bar shows the Spotify catalog length (not the mp4's 4:15) once audio is hosting again — otherwise the
        // bar desyncs (full blue / wrong remaining) across the thrash.
        if (_videoHost is not null && ReferenceEquals(outgoing, _videoHost) && !ReferenceEquals(target, _videoHost))
            _projection.SetDurationOverride(null, 0);
        int generation = Interlocked.Increment(ref _hostGeneration);
        _hostSub.Dispose();
        if (stopOutgoing)
        {
            // Pause THEN Stop, both BEFORE the incoming host is loaded/played by the caller — that ordering is the
            // one-audio-stream guarantee (PlaybackControllerHostSwapTests asserts stop-before-play on a shared call log).
            outgoing.Pause();
            outgoing.Stop();
        }
        _currentHost = target;
        // Disposing the outgoing subscription orphans any BUFFERING state that host had published: it can no longer
        // deliver the Playing/Ended edge that would clear it, so the spinner would latch over the incoming media (the
        // observed "closed the video, audio played, buffering spun forever"). Retire it at the swap — the incoming host
        // republishes its own buffering state on its first signal.
        _projection.ClearTransientBuffering();
        _hostSub = SubscribeHost(target, generation);
    }

    IDisposable SubscribeHost(IMediaHost host, int generation) =>
        host.Signals.Subscribe(Observers.From<AudioHostSignal>(signal =>
        {
            if (generation != Volatile.Read(ref _hostGeneration) || !ReferenceEquals(host, _currentHost))
            {
                _log.Event(WaveeLogLevel.Debug, "media.signal.dropped", "retired media-host signal ignored",
                    fields:
                    [
                        WaveeLogField.Of("signal", signal.Kind.ToString()),
                        WaveeLogField.Of("generation", generation),
                        WaveeLogField.Of("activeGeneration", Volatile.Read(ref _hostGeneration)),
                    ]);
                return;
            }
            OnHostSignal(signal);
        }));

    IMediaHost HostFor(PlayableKind kind) => kind == PlayableKind.Video && _videoHost is not null ? _videoHost : _audioHost;

    // Swap the ONE current-media host to whatever the incoming playable needs, per the pure MediaSwitchLogic rules. Called at
    // the LoadAndPlayCurrentAsync chokepoint BEFORE the (kind-specific) load. Returns the resolved kind so the caller loads on
    // the right host. Caller holds _lock.
    // forcedKind (Connect's modes.media on THIS play command) bypasses KindFor's ordinary ShouldPlayAsVideo /
    // audio-first resolution outright — UNLESS it asks for audio on a playable that has no audio form at all
    // (IsVideoOnly), which KindFor already refuses to honor for anyone, including the wire.
    PlayableKind SwitchCurrentMedia(Track track, PlayableKind? forcedKind = null)
    {
        var next = forcedKind is { } fk && (fk != PlayableKind.Audio || !IsVideoOnly(track)) ? fk : KindFor(track);
        if (MediaSwitchLogic.HostChanges(_currentKind, next))
        {
            var target = HostFor(next);
            // Every real host boundary is also a kind change, so ShouldStopOutgoingHost is true here — but ASK it rather
            // than assume it, so the stop-first rule lives in exactly one (unit-tested) place.
            bool stopOutgoing = MediaSwitchLogic.ShouldStopOutgoingHost(_currentKind, next);
            _log.Info($"media swap {_currentKind}→{next} host={(ReferenceEquals(target, _videoHost) ? "video" : "audio")} " +
                $"stopOutgoing={stopOutgoing} track={track.Uri}");
            _currentKind = next;
            SwitchHost(target, stopOutgoing);
        }
        else _currentKind = next;
        return next;
    }

    /// <summary>Re-evaluate the CURRENT playable's media kind and, if it changed, swap the host and reload the current track
    /// on it. This is what makes the user's "watch video" / "switch to audio" toggle take effect NOW instead of at the next
    /// track boundary. A no-op when nothing is playing, when the kind already matches, when the hooks are unwired (kill
    /// switch), or when another Connect device owns playback. Wired to <c>PlaybackBridge.RequestMediaKindRefresh</c>.
    /// <para>It is deliberately NOT what picks up a music-video association that lands asynchronously mid-track: that
    /// upgrade is deferred to the badge (see <c>PlaybackBridge.RecomputeHasVideo</c>), because reloading a playing track
    /// restarts it at position 0.</para>
    /// <para><paramref name="forceReloadIfVideo"/> closes the same-kind gap: a mid-playback video-SOURCE change (the user
    /// attached / replaced / removed a local override) leaves the kind at Video on both sides, so the same-kind early
    /// return would swallow it. Forcing falls through to the same <c>LoadAndPlayCurrentAsync</c> under <c>_lock</c>;
    /// an unchanged source Key is still a no-op inside the video host, so forcing is safe.</para>
    /// <para><paramref name="clearConnectAudioFirst"/> is ORTHOGONAL to it: only an EXPLICIT local media intent may drop
    /// the remote playback ids, because <see cref="ClearRemotePlaybackIds"/> also wipes <c>_connectOriginatedPlayback</c>
    /// (the audio-first rule a Connect-originated session depends on) plus the per-playable video-recovery and
    /// audio-fallback latches. A refresh triggered by a mere availability edge passes false.</para>
    /// <para>Milestone C (video smooth-switching): the <c>_lock</c> held here now spans only <c>MetaResolver</c> +
    /// <see cref="SwitchCurrentMedia"/> + the load KICKOFF — never a video source resolve/open. A video kind swap
    /// hands off to <see cref="StartVideoLoadDetached"/>, which returns (and this method's lock releases) before the
    /// video ever resolves; the resolve/open runs fully off-lock in <see cref="RunVideoLoadAsync"/>, guarded by the
    /// <c>_contextGeneration</c> fence instead of the lock hold. "The lock serializes the whole video load" is no
    /// longer true — do not re-introduce that assumption here.</para></summary>
    public async Task RefreshCurrentMediaKindAsync(bool forceReloadIfVideo = false, bool clearConnectAudioFirst = true,
        CancellationToken ct = default)
    {
        if (ShouldPlayAsVideo is null) return;   // hooks unwired → the kind can never be anything but audio
        // A genuine, non-empty foreign active device ALWAYS refuses a local reload — deliberately NOT RouteLocal(),
        // whose LocalOwnershipPending OR-clause exists for a transport verb pressed right after "transfer to this
        // computer", not for re-evaluating media kind while another device is actually, currently active.
        var aid = _projection.ActiveDeviceId;
        if (!string.IsNullOrEmpty(aid) && aid != _ourDeviceId) return;
        if (clearConnectAudioFirst) ClearRemotePlaybackIds();   // explicit local media intent ends Connect's audio-first preference
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cur = _session.Current;
            if (cur is null) return;
            var next = KindFor(cur);
            if (next == _currentKind)
            {
                if (!forceReloadIfVideo || next != PlayableKind.Video) return;
                _log.Info($"forced same-kind video reload for {cur.Uri} — the resolved video source changed");
            }
            else
                _log.Info($"media kind re-evaluated {_currentKind}→{next} for {cur.Uri} — reloading the current playable");
            // POSITION IS CARRIED ACROSS THE KIND BOUNDARY, in both directions: toggling video keeps your place instead of
            // restarting the song. This reverses an earlier deliberate refusal, whose reasoning was that a music video is a
            // DIFFERENT EDIT (spoken intros, alternate arrangements) so the two timelines are not strictly comparable. That
            // is true, but restarting at 0 is the worse answer for the common case — the edits are the same song, and a user
            // who toggles at 1:20 expects to still be at 1:20. Where the edits genuinely differ the landing is approximate,
            // never wrong-by-construction: the incoming side clamps against ITS OWN duration (audio here from the catalog
            // length; video inside the host, once the media engine reports the real one), so a carry that overruns a shorter
            // edit lands near its end rather than past it.
            long carry = Math.Max(0, _currentHost.PositionMs);
            if (next != PlayableKind.Video && cur.DurationMs > 0 && carry > cur.DurationMs)
                carry = cur.DurationMs;   // heading to audio: the catalog length is known here, so clamp before the load
            MintCommand("playbtn");
            await LoadAndPlayCurrentAsync(EvKind.Started, ct, carry).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    bool RejectLocalPlay()
    {
        if (OnLocalPlaybackRejected is not { } reject) return false;
        _log.Info("local playback unsupported — rejecting local play intent (choose a remote device)");
        reject();
        return true;
    }

    /// <summary>When set, an outbound command to the active remote device that FAILS (transfer / play) surfaces to the app
    /// (a "couldn't reach that device" toast) instead of failing silently. Null (unit tests) = log-only.</summary>
    public Action? OnRemoteCommandFailed { get; set; }

    /// <summary>When set, a LOCAL playback attempt that fails to resolve/decrypt/decode surfaces a typed
    /// <see cref="PlaybackErrorInfo"/> (reason + technical detail + user message) instead of a silently-dropped
    /// fire-and-forget Task. The live bootstrap logs the detail at Error and toasts the user message.</summary>
    public Action<PlaybackErrorInfo>? OnPlaybackError { get; set; }

    void ReportPlaybackError(Exception ex)
    {
        var reason = ex is AudioPlaybackException ape ? ape.Reason : AudioKeyFailureReason.None;
        string userMsg = reason != AudioKeyFailureReason.None ? reason.ToUserMessage() : "Couldn't play this track.";
        string detail = ex is AudioPlaybackException a ? (a.Message == reason.ToString() ? reason.ToString() : $"{reason}: {a.Message}") : ex.ToString();
        _log.Info("local playback error: " + detail);
        OnPlaybackError?.Invoke(new PlaybackErrorInfo(reason, userMsg, detail));
    }

    // Instant-start body supply: await the (parallel) key+CDN resolve and hand it to the host; a body failure surfaces
    // as a typed playback error (the head already started, so this is the "couldn't continue" case).
    //
    // This used to defer the SupplyBody call by a fixed FastStartBodySupplyGrace (250ms) "so clear-head decode can
    // queue first PCM" — a wall-clock guess that behaved differently on every machine. It bought two things, neither
    // of which needs a timer: (1) ordering — the host's Enqueue pump already serializes LoadFastStartAsync before
    // SupplyBodyAsync, so the clear head is always queued first regardless of when this method runs; (2) bandwidth —
    // the newly-attached CDN body's read-ahead competing with the still-decoding clear head is now shaped at the
    // source instead (SpotifyAudioStream pauses read-ahead across an eager attach and releases it itself on the
    // decoder's first real body byte — see SpotifyAudioStream.AttachBodyCoreAsync / ReleaseBandwidthShapePauseIfArmed).
    async Task SupplyBodyWhenReadyAsync(Task<AudioStreamHandle> body, string expectedTrackUri)
    {
        try
        {
            var h = await body.ConfigureAwait(false);
            var current = _session.Current?.Uri ?? "";
            if (!string.Equals(current, expectedTrackUri, StringComparison.Ordinal))
            {
                _log.Info($"fast-start body ignored as stale expected={expectedTrackUri} current={current} bodyTrack={h.TrackUri} file={h.FileIdHex}");
            }
            else if (_currentKind == PlayableKind.Video)
            {
                // The user switched THIS track to video while its encrypted body was still resolving. The audio host has been
                // stopped by the swap; feeding it a body now would hand a stopped decoder work it must not do (and risks a
                // second stream). The body is simply dropped — a swap back to audio reloads from scratch.
                _log.Info($"fast-start body dropped — {expectedTrackUri} is now playing as video (file={h.FileIdHex})");
            }
            else
            {
                _log.Info($"fast-start body ready track={expectedTrackUri} file={h.FileIdHex}; supplying to audio host");
                _audioHost.SupplyBody(h);   // audio-specific: this flow is only scheduled from the audio fast-start path
            }
        }
        catch (OperationCanceledException)
        {
            _log.Info($"fast-start body task canceled expected={expectedTrackUri}");
        }
        catch (Exception ex)
        {
            var current = _session.Current?.Uri ?? "";
            if (string.Equals(current, expectedTrackUri, StringComparison.Ordinal))
            {
                // Through the ONE teardown chokepoint (not a bare _audioHost.Stop()) — the audio host IS _currentHost
                // here (this flow is only ever scheduled from the audio fast-start path), and this stop must arm
                // _restorePendingLoad exactly like every other session-preserving stop, or a later Resume bare-Play()s
                // over the decoder this just silenced.
                StopHostKeepingSession($"fast-start body failed for active track={expectedTrackUri}; stopping audio host to unblock head stream: {ex.GetType().Name}: {ex.Message}");
            }
            else
            {
                _log.Info($"fast-start body failed for stale track expected={expectedTrackUri} current={current}: {ex.GetType().Name}: {ex.Message}");
            }
            ReportPlaybackError(ex);
        }
    }

    /// <summary>Re-attempt the current track after a surfaced playback error (the toast/player-bar "Retry" action).</summary>
    public async Task RetryCurrentAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session.Current is { } current)
            {
                long resume = _failureCheckpoint is { } checkpoint
                    && string.Equals(checkpoint.TrackUri, current.Uri, StringComparison.Ordinal)
                    ? checkpoint.PositionMs
                    : -1;
                await LoadAndPlayCurrentAsync(EvKind.Started, ct, resume).ConfigureAwait(false);
                _failureCheckpoint = null;
            }
        }
        finally { _lock.Release(); }
    }

    // The routing spine: local iff nobody is active, we are, or a transfer-in just started local playback and the
    // cluster hasn't folded us in as active yet (LocalOwnershipPending — see NowPlayingProjection). Without the last
    // term, a transport verb pressed right after "transfer to this computer" could still race a stale fold and land
    // on the device we just transferred FROM. (No _localActive flag — the cluster is otherwise the truth.)
    bool RouteLocal() => RouteLocal(_projection.ActiveDeviceId);

    // The same decision, taking a PRE-CAPTURED active-device id: the remote-forwarding verbs read ActiveDeviceId
    // exactly once — at the same moment they evaluate the routing decision — and thread that one read down into the
    // outbound helper as `target`, instead of the helper re-reading _projection.ActiveDeviceId at send time. Without
    // this, a cluster fold landing between the routing check and the actual send could route local (or route remote
    // to device A) and then send to whatever ActiveDeviceId reads as by then — including empty.
    bool RouteLocal(string aid) => string.IsNullOrEmpty(aid) || aid == _ourDeviceId || _projection.LocalOwnershipPending;

    // "Another device became active" → stop our local host so we don't double-play. Public: the Connect state builder
    // gates the CURRENT track's authoritative `track_player`/`media.*` overwrite on this (bug 2) — a viewer echoing
    // another device's row must never have its OWN idle CurrentMediaKind (default Audio) stamped over the wire's claim.
    public bool IsActiveOwner()
    {
        lock (_ownershipGate)
        {
            if (_ownsActivePlayback) return true;
            if (_projection.ActiveDeviceId != _ourDeviceId) return false;
            _ownsActivePlayback = true;
            return true;
        }
    }

    void SetActiveOwner(bool value)
    {
        lock (_ownershipGate) _ownsActivePlayback = value;
    }

    // The ONE teardown chokepoint for every session-PRESERVING stop (a takeover, a stray-host cleanup, a fast-start
    // body failure): stopping the host must never leave the queue session behind with nothing marking the host as
    // "holds nothing", because that is exactly the shape that made ResumeCurrentLockedAsync take its "we already have
    // local media loaded" branch and call a bare Play() over a disposed decoder — a permanent no-op the only escape
    // from was a track change. Arming _restorePendingLoad here is the fix: the next Resume goes through
    // LoadAndPlayCurrentAsync's fast-start instead. LoadAndPlayCurrentAsync consumes the latch on any real load
    // ("any real load consumes the recovery seed's deferred-load latch"), so the arm/consume pair stays symmetric.
    void StopHostKeepingSession(string reason)
    {
        _restorePendingLoad = _session.Current is not null;
        _currentHost.Stop();
        _log.Info(reason);
    }

    public void DeactivateIfActiveOwner()
    {
        bool deactivate;
        lock (_ownershipGate)
        {
            deactivate = _ownsActivePlayback;
            if (deactivate) _ownsActivePlayback = false;
        }
        if (!deactivate) return;
        // Capture BEFORE Stop(): Stop marks the host's clock stale synchronously (FluentMediaAudioHost.Stop sets
        // _clockStale before returning), so reading PositionMs after it would already be the 0-means-unknown case this
        // seam exists to avoid. The captured value is the last honest reading, so it — not the projected fallback — is
        // what BecameInactive carries.
        long pos = _currentHost.PositionMs;
        // A takeover invalidates any LoadAndPlayCurrentAsync that was already resolving for the playable we are about
        // to silence — without this, a resolve started just before the takeover could land AFTER the host below is
        // stopped and call LoadFastStart/Play over it (§5).
        Interlocked.Increment(ref _contextGeneration);
        StopHostKeepingSession("another device became active — stopping local playback, keeping the session for a later reload");
        EmitState(EvKind.BecameInactive, pos);
    }

    void StopStrayLocalHost(string message)
    {
        // ClockValid alone (not just IsPlaying) — a host can hold a PAUSED-but-loaded session (honest clock, not
        // playing) after a prior teardown; IsPlaying-only left that stray host un-stopped and TransferToAsync's
        // non-owner arm shares this gate.
        if (!_currentHost.IsPlaying && !_currentHost.ClockValid) return;
        StopHostKeepingSession(message);
    }

    void OnProjectionChanged(IPlaybackState s)
    {
        // Launch SessionRecovery (findings §1): the first cluster fold that reaches us while no local session exists
        // seeds the session PAUSED instead of leaving now-playing over an empty queue. Fire-and-forget — the recovery
        // takes _lock on its own task, never inside this projection callback.
        MaybeScheduleSessionRecovery(s);

        // Apply a volume change (incl. one a remote controller made to the active device) to the local host when WE are
        // active. Silent host = no-op today, but correct once real audio lands; never loops (the host has no readback).
        double vol = s.Volume;
        if (Math.Abs(vol - _lastVolume) > 0.0009) { _lastVolume = vol; _lastIntentVolume = vol; if (RouteLocal()) _currentHost.SetVolume(vol); }

        var aid = s.ActiveDeviceId ?? "";
        if (aid == _ourDeviceId) SetActiveOwner(true);
        // Gated on an ACTUAL transition (below the aid == _lastActive early-return), not "every projection change with
        // this aid" — a launch/session recovery's OWN seed (RunSessionRecoveryAsync's ApplyLocalSnapshot) re-enters
        // this callback synchronously (FireChanges) with the SAME aid it just recorded; without this gate, that
        // self-triggered echo raced the recovery's own _restorePendingLoad latch and quietly preloaded the track
        // before the very first real Resume ever ran, consuming the latch the test/UI still expected to see.
        if (aid == _lastActive) return;
        MaybeAutoReloadOnOwnershipRegained(aid);
        var previousActive = _lastActive;
        _lastActive = aid;
        // A NON-EMPTY foreign id is a genuine takeover. An EMPTY one is NOT: our own put-states announce
        // active=False on Ended/BecameInactive (DeviceStatePublisher), and the echo comes back here as an empty
        // ActiveDeviceId — treating that as "another device took over" tore our own host down out from under
        // itself the instant playback ended or paused (the log shows "put-state … active=False" immediately
        // followed by "another device became active — stopping local playback" on the same fold).
        if (!string.IsNullOrEmpty(aid) && aid != _ourDeviceId && (previousActive == _ourDeviceId || IsActiveOwner()))
        {
            _log.Info("another device became active — stopping local playback");
            DeactivateIfActiveOwner();
        }
        else if (!string.IsNullOrEmpty(aid) && aid != _ourDeviceId)
        {
            StopStrayLocalHost("another device became active - stopping stray local playback");
        }
        else if (string.IsNullOrEmpty(aid) && previousActive == _ourDeviceId)
        {
            // Nobody active ⇒ our own echo, not a takeover. Give up ownership so a later fold can reclaim it and
            // tell the wire we're inactive, but leave the host and the projection's position/play-state alone —
            // RouteLocal() already treats an empty id as local, so there is nothing here that needs correcting.
            SetActiveOwner(false);
            PublishInactiveOnWire?.Invoke();
        }
    }

    // Seam B §3 — auto-reload on regaining ownership. StopHostKeepingSession arms _restorePendingLoad on every
    // takeover teardown, and the belt-and-braces in ResumeCurrentLockedAsync (§2) means the FIRST Resume after
    // ownership returns would already recover on its own — but "the first Resume" is very often a REMOTE Resume
    // command arriving the instant the cluster hands the slot back, and that command would otherwise block on a full
    // resolve before the phone/desktop ever hears back. Preparing the host PAUSED here, the moment ownership returns,
    // means that Resume is just a Play() by the time it runs. Only a pre-check: the real work (and the authoritative
    // re-check) happens in the scheduled task under _lock — this must never block the projection callback itself
    // (the same discipline MaybeScheduleSessionRecovery already follows from this exact callback).
    void MaybeAutoReloadOnOwnershipRegained(string aid)
    {
        if (aid != _ourDeviceId && !string.IsNullOrEmpty(aid)) return;   // still someone else's slot
        if (!_restorePendingLoad || !HasLocalSession) return;
        var uri = _snap.Current?.Track.Uri;
        if (uri is null || string.Equals(_ownershipReloadUri, uri, StringComparison.Ordinal)) return;
        _ownershipReloadUri = uri;
        _ = AutoReloadOnOwnershipRegainedAsync(uri, Volatile.Read(ref _contextGeneration));
    }

    async Task AutoReloadOnOwnershipRegainedAsync(string uri, long generation)
    {
        try
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check everything under the lock: the pre-check above is a best-effort hint, not a decision.
                if (!_restorePendingLoad) return;                         // already reloaded (or never needed to be)
                if (_session.Current is not { } cur || !string.Equals(cur.Uri, uri, StringComparison.Ordinal)) return;
                if (generation != Volatile.Read(ref _contextGeneration)) return;   // superseded by a real play/transfer meanwhile
                long pos = _projection.PositionMs;
                _quietRestorePublish = true;   // this is a silent re-arm, not a user action — never announce on the wire (§8)
                await LoadAndPlayCurrentAsync(EvKind.Paused, CancellationToken.None, pos > 0 ? pos : -1,
                    initiallyPaused: true, skipOnUnplayable: true).ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info($"ownership-regained auto-reload failed for {uri}: {ex.Message}"); }
        finally
        {
            // Arm-scoped, not session-scoped: clearing here (rather than leaving it latched forever) lets a LATER
            // teardown of this same track auto-reload again instead of being silently swallowed by a stale latch.
            if (string.Equals(_ownershipReloadUri, uri, StringComparison.Ordinal)) _ownershipReloadUri = null;
        }
    }

    // ── IPlaybackPlayer (UI intents) — each verb routes local vs. forward ─────────────────────────────────────────────
    public async Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default)
    {
        await ExecutePlayAsync(PlayRequest.Default(contextUri, startIndex), "play-context", ct).ConfigureAwait(false);
    }

    public async Task PlayContextTrackAsync(string contextUri, PlaybackContextTrack track, int fallbackIndex = 0, CancellationToken ct = default)
    {
        // bug 4: the row's own metadata is the only video signal available at this call site (no Track/ShouldPlayAsVideo
        // resolve happens before a forward) — the same wire key list an inbound transfer/cluster row checks.
        PlayableKind? mediaKind = MediaSwitchLogic.HasVideoMetadata(track.Metadata) ? PlayableKind.Video : null;
        await ExecutePlayAsync(new PlayRequest(
            contextUri,
            Math.Max(0, fallbackIndex),
            null,
            string.IsNullOrEmpty(track.Uri) ? null : track.Uri,
            string.IsNullOrEmpty(track.Uid) ? null : track.Uid,
            mediaKind), "play-context-track", ct).ConfigureAwait(false);
    }

    public async Task PlayOrderedAsync(string contextUri, IReadOnlyList<PlaybackContextTrack> tracks, int startIndex = 0, CancellationToken ct = default)
    {
        if (tracks.Count == 0)
        {
            await PlayAsync(contextUri, startIndex, ct).ConfigureAwait(false);
            return;
        }

        var refs = ToQueuedRefs(tracks);
        int start = Math.Clamp(startIndex, 0, refs.Length - 1);
        var selected = refs[start];
        PlayableKind? mediaKind = MediaSwitchLogic.HasVideoMetadata(selected.Metadata) ? PlayableKind.Video : null;
        await ExecutePlayAsync(new PlayRequest(contextUri, start, refs, selected.Uri, selected.Uid, mediaKind), "play-ordered", ct).ConfigureAwait(false);
    }

    public async Task PlayTrackAsync(string trackUri, CancellationToken ct = default)
    {
        if (!RouteLocal())
        {
            await ExecutePlayAsync(PlayRequest.Default(trackUri, 0), "play-track-uri", ct).ConfigureAwait(false);
            return;
        }

        LogPlayIntent("play-track-uri", trackUri, 0, trackUri, null, 0, local: true);
        ClearRemotePlaybackIds();
        MintCommand("playbtn");
        // §7 — latch BEFORE HydrateOneAsync's resolve, same reasoning as ExecutePlayAsync.
        long generation = Interlocked.Increment(ref _contextGeneration);
        Volatile.Write(ref _explicitPlayGeneration, generation);
        var track = await HydrateOneAsync(trackUri, ct).ConfigureAwait(false);
        await LocalPlayTracksAsync(trackUri, new[] { track }, 0, ct, expectedGeneration: generation).ConfigureAwait(false);
    }

    public Task PlayTrackAsync(Track track, CancellationToken ct = default)
    {
        if (!RouteLocal())
        {
            // bug 4: the app's own video-surface intent (the same predicate LOCAL loads consult) is the strongest
            // signal available here — fail-soft to "unknown" exactly like KindFor's own use of the same hook.
            PlayableKind? mediaKind = null;
            if (ShouldPlayAsVideo is { } predicate)
            {
                try { if (predicate(track)) mediaKind = PlayableKind.Video; }
                catch (Exception ex) { _log.Info($"ShouldPlayAsVideo threw for {track.Uri} while forwarding play; treating as unknown: {ex.GetType().Name}: {ex.Message}"); }
            }
            return ExecutePlayAsync(PlayRequest.Default(track.Uri, 0, mediaKind), "play-track", ct);
        }
        LogPlayIntent("play-track", track.Uri, 0, track.Uri, null, 0, local: true);
        ClearRemotePlaybackIds();
        MintCommand("playbtn");
        long generation = Interlocked.Increment(ref _contextGeneration);
        Volatile.Write(ref _explicitPlayGeneration, generation);
        return LocalPlayTracksAsync(track.Uri, new[] { new QueuedTrack(track, "") }, 0, ct, expectedGeneration: generation);
    }

    // Apple-Music-style "Start radio" (radio-inspiredby-mix-design §5.3): resolve the seed → a radio playlist, then park
    // it as the new context so the current track finishes first (playback flows into the radio via the existing Ended →
    // AutoAdvance → Next() path — no new end-of-track logic). Nothing playing (or a remote device is active) → play the
    // radio playlist through the normal routed play path instead. Returns the radio playlist uri (for the "Open playlist"
    // toast at the caller — this controller is UI-free), or null when no radio is available / nothing changed.
    public async Task<string?> StartRadioAsync(string seedUri, string? displayName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(seedUri)) return null;
        var playlistUri = await _contexts.ResolveRadioSeedAsync(seedUri, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(playlistUri)) { _log.Info("radio: seed resolved to no playlist: " + seedUri); return null; }

        // Idle here, or a remote device owns playback → play the radio playlist via the normal routed play path (which
        // forwards to the active device / honors the local-unsupported reject). "Park after current" is a LOCAL-session op
        // that only applies when WE are the active local player with a track already playing.
        if (!RouteLocal() || _session.Current is null)
        {
            await PlayAsync(playlistUri!, 0, ct).ConfigureAwait(false);
            return playlistUri;
        }

        // A track is playing locally → resolve the radio playlist and park it WITHOUT touching the audio host (§5.4).
        var resolved = await _contexts.ResolveAsync(ContextSpec.ForUri(playlistUri!), ct).ConfigureAwait(false);
        if (resolved.Count == 0) { _log.Info("radio: playlist resolved to 0 tracks: " + playlistUri); return null; }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snap = _session.SwitchContextAfterCurrent(resolved.ContextUri ?? playlistUri!, resolved.Tracks);
            // Re-point the controller's continuation tracking at the radio context. A stale in-flight prefetch for the OLD
            // context is dropped by the ReferenceEquals guard in EagerApplyContinuationAsync once _continuationFetch is nulled.
            _nextPageUrl = string.IsNullOrEmpty(resolved.NextPageUrl) ? null : resolved.NextPageUrl;
            _contextIsInfinite = resolved.IsInfinite || ContextResolve.IsInfinite(resolved.ContextUri ?? playlistUri!);
            _autoplayLatchedFor = null;
            _continuationFetch = null;
            _projection.SetContextMetadata(resolved.Metadata);          // "Playing from …" line (mirrors SetQueueContext)
            EmitSnap(snap, EvKind.QueueChanged);                        // publish the parked up-next; current track untouched
        }
        finally { _lock.Release(); }
        _log.Info($"radio: parked {playlistUri} after current ({resolved.Count} tracks)");
        return playlistUri;
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        // Capture the remote target in the same breath as the routing decision (see Forward): a cluster fold between the
        // two reads would otherwise send the verb to an empty device id.
        var target = _projection.ActiveDeviceId;
        return RouteLocal(target)
            ? Local(() => { _currentHost.Pause(); EmitState(EvKind.Paused); })
            : Forward("pause", target, ct);
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        var target = _projection.ActiveDeviceId;
        if (!RouteLocal(target)) { await Forward("resume", target, ct).ConfigureAwait(false); return; }
        await LocalResumeAsync(ct).ConfigureAwait(false);
    }

    public async Task NextAsync(CancellationToken ct = default)
    {
        var target = _projection.ActiveDeviceId;
        if (!RouteLocal(target)) { await Forward("skip_next", target, ct).ConfigureAwait(false); return; }
        await LocalNextAsync(ct).ConfigureAwait(false);
    }

    public async Task PreviousAsync(CancellationToken ct = default)
    {
        var target = _projection.ActiveDeviceId;
        if (!RouteLocal(target)) { await Forward("skip_prev", target, ct).ConfigureAwait(false); return; }
        await LocalPrevAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Seek the current media, carrying the seek FIDELITY the seek bar computed.
    /// <para><see cref="SeekMode.Keyframe"/> is a throttled scrub PREVIEW issued many times per drag. Locally it drives the
    /// media host ONLY (see <see cref="EmitSeeked"/>) — no cluster <c>Seeked</c> event, no prepared-next re-arm, no
    /// continuation fetch. Remotely it is DROPPED outright: <c>seek_to</c> is a committed action by definition and a scrub
    /// stream over Connect would spam the cluster; only the <see cref="SeekMode.Accurate"/> commit is forwarded.</para></summary>
    public Task SeekAsync(long positionMs, SeekMode mode, CancellationToken ct = default)
    {
        var target = _projection.ActiveDeviceId;
        if (RouteLocal(target)) return Local(() => EmitSeeked(positionMs, mode));
        // Remote device: previews are not sent. The gesture still commits — MediaSeekBar issues exactly one Accurate
        // seek on release, and that is the one that crosses the wire.
        if (mode == SeekMode.Keyframe) return Done;
        return Forward("seek_to", target, ct, ("value", positionMs));
    }

    public Task SetVolumeAsync(double volume01, CancellationToken ct = default)
    {
        volume01 = Math.Clamp(volume01, 0, 1);
        if (!double.IsNaN(_lastIntentVolume) && Math.Abs(volume01 - _lastIntentVolume) < 0.0005)
            return Done;
        _lastIntentVolume = volume01;

        var target = _projection.ActiveDeviceId;
        bool local = RouteLocal(target);
        _projection.NoteLocalCommand();          // optimistic: a stale cluster echo won't snap the slider back
        if (local)
        {
            _lastVolume = volume01;              // suppress OnProjectionChanged echo; the explicit host write below owns it
            // volume + authoritative timeline publish atomically — but only when the host's clock is honest; a stale/
            // absent clock must not snap the timeline to 0 as a side effect of a volume change.
            if (_currentHost.ClockValid) _projection.SetLocalVolume(volume01, _currentHost.PositionMs);
            else _projection.SetLocalVolume(volume01);
            return Local(() => { _currentHost.SetVolume(volume01); EmitState(EvKind.VolumeChanged); });
        }
        _projection.SetLocalVolume(volume01);    // remote optimistic slider; its cluster timeline remains authoritative
        if (string.IsNullOrEmpty(target))
        {
            _log.Info("outbound volume → (no target): dropped — the active-device id was empty at send time");
            OnRemoteCommandFailed?.Invoke();
            return Done;
        }
        if (_outbound is null) return Done;
        _remoteVolumeTx.Post(() => _ = ForwardVolumeAsync(target, volume01, CancellationToken.None));
        return Done;
    }

    async Task ForwardVolumeAsync(string target, double volume01, CancellationToken ct)
    {
        int vol = (int)Math.Round(Math.Clamp(volume01, 0, 1) * 65535);
        var r = await _outbound!.SetVolumeAsync(target, vol, ct).ConfigureAwait(false);
        if (!r.Ok) _log.Info($"outbound volume → {target}: failed ({r.Status})");
    }

    /// <summary>An EXTERNAL Windows session-volume change (SndVol / another app) reflected onto OUR device (Phase B3). We
    /// are the active output, so this only ANNOUNCES the new volume (coalesced PutState via DeviceStatePublisher) — it is
    /// NOT forwarded as a Connect volume PUT (that path controls a REMOTE device). Two independent echo-guards keep it from
    /// looping: the OnProjectionChanged epsilon guard (we set _lastVolume first) and the engine's context-GUID sink filter.</summary>
    public void OnExternalVolumeChanged(double slider01)
    {
        slider01 = Math.Clamp(slider01, 0, 1);
        _lastVolume = slider01;                  // suppress the OnProjectionChanged echo-down to the host
        _lastIntentVolume = slider01;
        _projection.NoteLocalCommand();          // a stale cluster echo must not snap the slider back (LocalCmdWindow)
        if (!RouteLocal())
        {
            // Another device is active: this OS-level tweak has no local host clock to sample and nothing local
            // actually changed play-state-wise — move the slider only. No EmitState (there is no local event to
            // announce) and no position sample (there is no honest local one to take).
            _projection.SetLocalVolume(slider01);
            return;
        }
        // Move slider without publishing a stale position — and never a 0-means-unknown one either, hence the ClockValid guard.
        if (_currentHost.ClockValid) _projection.SetLocalVolume(slider01, _currentHost.PositionMs);
        else _projection.SetLocalVolume(slider01);
        EmitState(EvKind.VolumeChanged);         // announce our device volume (coalesced PutState) — no outbound PUT
    }

    public async Task SetShuffleAsync(bool on, CancellationToken ct = default)
    {
        var target = _projection.ActiveDeviceId;
        if (!RouteLocal(target)) { await Forward("set_shuffling_context", target, ct, ("value", on)).ConfigureAwait(false); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);   // SetShuffle rebuilds the context list — one lock per mutation
        try
        {
            bool changed = _session.Shuffle != on;
            var snap = _session.SetShuffle(on);
            PlaybackBucketDiagnostics.ShuffleToggle("local", on, changed, snap);
            EmitSnap(snap, EvKind.OptionsChanged);
        }
        finally { _lock.Release(); }
    }

    public async Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default)
    {
        var target = _projection.ActiveDeviceId;
        if (RouteLocal(target))
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try { EmitSnap(_session.SetRepeat(mode), EvKind.OptionsChanged); }
            finally { _lock.Release(); }
            return;
        }
        // Remote: split + always send BOTH explicit modes so Track->Off / Track->Context can't leave the target stuck.
        await Forward("set_repeating_track", target, ct, ("value", mode == RepeatMode.Track)).ConfigureAwait(false);
        await Forward("set_repeating_context", target, ct, ("value", mode == RepeatMode.Context)).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(string trackUri, CancellationToken ct = default)
    {
        var target = _projection.ActiveDeviceId;
        if (!RouteLocal(target)) { await ForwardAddToQueueAsync(trackUri, target, ct).ConfigureAwait(false); return; }
        var queued = await HydrateOneAsync(trackUri, ct).ConfigureAwait(false);
        await EnqueueLocalAsync(queued, ct).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(Track track, CancellationToken ct = default)
    {
        var target = _projection.ActiveDeviceId;
        if (!RouteLocal(target)) { await ForwardAddToQueueAsync(track, target, ct).ConfigureAwait(false); return; }
        await EnqueueLocalAsync(new QueuedTrack(track, ""), ct).ConfigureAwait(false);
    }

    async Task EnqueueLocalAsync(QueuedTrack queued, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session.Current is null)   // add-to-queue while idle → start playing it (rule §3)
            {
                if (RejectLocalPlay()) return;   // can't start local playback → toast + abort (don't seed a phantom local queue)
                SetQueueContext(queued.Track.Uri, new[] { queued }, 0);
                await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false);
            }
            else EmitSnap(_session.EnqueueUser(new[] { queued }), EvKind.QueueChanged);   // active device mints the q-uid (§7.4)
            WarmFastTrack(queued.Track, "enqueue");
        }
        finally { _lock.Release(); }
    }

    // play-next: insert at the FRONT of the user queue — the index-0 case of the slot insert below (one code path, so a
    // drag dropped at slot 0 and the "Play next" verb can never diverge).
    public Task PlayNextAsync(IReadOnlyList<PlaybackContextTrack> tracks, CancellationToken ct = default)
        => InsertIntoQueueAsync(tracks, 0, ct);

    // Insert at a QUEUE-RELATIVE slot. LOCAL → InsertUserQueue (head-insert at 0 = play-next; clamped to the queue).
    // REMOTE → a full set_queue snapshot: the device's own queue as the cluster reports it with our tracks spliced in
    // after `index` of its provider:"queue" rows, then the resident context continuation as provider:"context".
    // prev_tracks is echoed verbatim (no history model of our own); queue_revision echoes the cluster.
    public async Task InsertIntoQueueAsync(IReadOnlyList<PlaybackContextTrack> tracks, int index, CancellationToken ct = default)
    {
        var refs = ToQueuedRefs(tracks);
        if (refs.Length == 0) return;
        int at = Math.Max(0, index);
        var target = _projection.ActiveDeviceId;
        if (RouteLocal(target))
        {
            if (RejectLocalPlay()) return;   // a local insert would seed a local queue that can never play → toast + abort
            var hydrated = await _contexts.HydrateAsync(refs, ct).ConfigureAwait(false);
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_session.Current is null && hydrated.Count > 0)
                {
                    // Rule §3, the same one EnqueueLocalAsync applies: with NOTHING playing there is no "next" to
                    // insert before — parking rows in a queue that never runs is the silent-no-op reading. Start them.
                    SetQueueContext(hydrated[0].Track.Uri, hydrated, 0);
                    WarmFastTrack(hydrated[0].Track, "queue-insert");
                    await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false);
                }
                else
                {
                    var snap = _session.InsertUserQueue(hydrated, at);
                    if (hydrated.Count > 0) WarmFastTrack(hydrated[0].Track, at == 0 ? "play-next" : "queue-insert");
                    EmitSnap(snap, EvKind.QueueChanged);
                }
            }
            finally { _lock.Release(); }
            return;
        }
        if (string.IsNullOrEmpty(target))
        {
            _log.Info("outbound set_queue → (no target): dropped — the active-device id was empty at send time");
            OnRemoteCommandFailed?.Invoke();
            return;
        }
        if (_outbound is null) return;
        // Rewrite the ACTIVE device's queue as the cluster reports it (its real prev/next, uid+provider preserved) —
        // NOT our local QueueCore, which is stale/empty when we're a viewer. prev_tracks + the context continuation are
        // echoed verbatim so the remote queue isn't clobbered, and queue_revision comes from the same cluster snapshot
        // (so it matches the server's; remote routing can't happen without a cluster).
        var clusterPrev = _projection.ClusterPrevTracks;
        var clusterNext = _projection.ClusterNextTracks;
        var prev = new List<QueueWireEntry>(clusterPrev.Count);
        foreach (var t in clusterPrev) prev.Add(new QueueWireEntry(t.Uri, t.Uid, t.Provider == "queue", t.Metadata));
        var next = new List<QueueWireEntry>(refs.Length + clusterNext.Count);
        bool placed = false;
        int queueSeen = 0;
        foreach (var t in clusterNext)
        {
            bool queued = t.Provider == "queue";
            // Splice in once we have passed `at` of the device's queued rows — or as soon as its queue section ends
            // (a slot past the end clamps to "last queued row"), which for at == 0 is the head, byte-identical to the
            // pre-slot play-next.
            if (!placed && (queueSeen >= at || !queued))
            {
                foreach (var r in refs) next.Add(new QueueWireEntry(r.Uri, r.Uid, true, r.Metadata));
                placed = true;
            }
            if (queued) queueSeen++;
            next.Add(new QueueWireEntry(t.Uri, t.Uid, queued, t.Metadata));
        }
        if (!placed) foreach (var r in refs) next.Add(new QueueWireEntry(r.Uri, r.Uid, true, r.Metadata));
        var json = OutboundEnvelope.SetQueue(_ourDeviceId, ParseRevision(), prev, next, NewId(), NewId(), Now(), NewId());
        var r2 = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r2.Ok) _log.Info($"outbound set_queue → {target}: failed ({r2.Status})");
    }

    // Skip-in-place to a queue/history row (§6). Active: session cursor move + fast-start (never a rebuild). Viewer: forward
    // next_track with the target row (FIXTURE-B — uid-first, no play/skip_to). Idle: no-op (the id resolves to nothing).
    public async Task SkipToQueueItemAsync(QueueItemId id, CancellationToken ct = default)
    {
        if (id.IsNone) return;
        var target = _projection.ActiveDeviceId;
        if (RouteLocal(target))
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _projection.NoteLocalCommand();
                if (_session.SkipToItem(id) is { } snap)
                {
                    _snap = snap;
                    await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
                }
            }
            finally { _lock.Release(); }
            return;
        }
        if (string.IsNullOrEmpty(target))
        {
            _log.Info("outbound next_track → (no target): dropped — the active-device id was empty at send time");
            OnRemoteCommandFailed?.Invoke();
            return;
        }
        if (_outbound is null) return;
        if (!_projection.TryGetViewerRow(id, out var row)) { _log.Info("skip-to: viewer row not found for id " + id.Value); return; }
        var json = OutboundEnvelope.NextTrack(row, _ourDeviceId, NewId(), NewId(), Now());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) { _log.Info($"outbound next_track → {target}: failed ({r.Status})"); OnRemoteCommandFailed?.Invoke(); }
    }

    public async Task MoveQueueItemAsync(QueueItemId id, int newPos, CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log.Info("queue move ignored — another device is active"); return; }   // the active device owns its queue
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { if (_session.MoveItem(id, newPos) is { } snap) EmitSnap(snap, EvKind.QueueChanged); }
        finally { _lock.Release(); }
    }

    public async Task RemoveQueueItemAsync(QueueItemId id, CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log.Info("queue remove ignored — another device is active"); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { if (_session.RemoveItem(id) is { } snap) EmitSnap(snap, EvKind.QueueChanged); }
        finally { _lock.Release(); }
    }

    // Clear the user queue / history (§10.1) — active-device local session ops (one revision bump, atomic publish). Viewer:
    // no-op (no wire verb; the panel hides the button in viewer mode).
    public async Task ClearQueueAsync(CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log.Info("queue clear ignored — another device is active"); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { EmitSnap(_session.ClearUserQueue(), EvKind.QueueChanged); }
        finally { _lock.Release(); }
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log.Info("history clear ignored — another device is active"); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { EmitSnap(_session.ClearHistory(), EvKind.QueueChanged); }
        finally { _lock.Release(); }
    }

    /// <summary>Device-picker hand-off. Self = ghost-resume (the HTTP transfer endpoint 400s for self); another = forward
    /// the transfer + stop our local host so we don't double-play.</summary>
    public async Task TransferToAsync(string targetDeviceId, CancellationToken ct = default)
    {
        if (targetDeviceId == _ourDeviceId)
        {
            // Transfer-to-self = local resume: the shared ResumeCurrentLockedAsync covers the reject hook, the
            // restore-seeded pending-load fast-start, the plain resume, and the ghost/snapshot seed (§1).
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try { await ResumeCurrentLockedAsync(ct).ConfigureAwait(false); }
            finally { _lock.Release(); }
            return;
        }
        bool wasActiveOwner = IsActiveOwner();
        // The cluster's last-known state for us can be seconds stale by the time the user actually presses "transfer"
        // (nothing re-announces on a timer) — publish our LIVE position/play-state right now, ungated, so the target
        // device picks up where the user actually is instead of wherever we last happened to PUT. Only meaningful when
        // we are the one actually playing; forwarding while some OTHER device owns playback has no local truth to give.
        if (wasActiveOwner) PublishFreshStateOnWire?.Invoke();
        bool ok = await TryForwardTransferAsync(targetDeviceId, ct).ConfigureAwait(false);
        if (!ok) return;
        if (wasActiveOwner) DeactivateIfActiveOwner();
        else StopStrayLocalHost("remote transfer accepted while Wavee was not active - stopping stray local playback");
    }

    // ── Inbound remote commands (WE are the target) — ALWAYS local, regardless of the routing rule ───────────────────
    public void HandleRemoteCommand(in ConnectCommand cmd)
        => _ = HandleRemoteCommandAsync(cmd, CancellationToken.None);

    /// <summary>Ordered Dealer command entry point. The router awaits this method so no command handler is unobserved.</summary>
    public async Task<ConnectCommandOutcome> HandleRemoteCommandAsync(ConnectCommand cmd, CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(cmd.CommandId)) _commandIdHex = cmd.CommandId;
            else MintCommand();
            var attribution = new ConnectCommandAttribution(
                cmd.SenderDeviceId, unchecked((uint)Math.Max(0, cmd.MessageId)), _commandIdHex);
            foreach (var projection in _extra)
                if (projection is IConnectCommandAttributionSink sink) sink.NoteCommand(attribution);

            ConnectCommandOutcome outcome;
            switch (cmd.Kind)
            {
                case ConnectCmd.Pause:
                    await _lock.WaitAsync(ct).ConfigureAwait(false);
                    try { outcome = ApplyInboundPauseLocked(); }
                    finally { _lock.Release(); }
                    break;
                case ConnectCmd.Resume: outcome = await HandleInboundResumeAsync(ct).ConfigureAwait(false); break;
                case ConnectCmd.SkipNext: outcome = await HandleInboundSkipNextAsync(cmd).ConfigureAwait(false); break;
                case ConnectCmd.SkipPrev: await LocalPrevAsync(ct).ConfigureAwait(false); outcome = ConnectCommandOutcome.Applied; break;
                case ConnectCmd.SeekTo:
                    await _lock.WaitAsync(ct).ConfigureAwait(false);
                    // An inbound Connect seek_to is a COMMITTED remote action — always the accurate arm.
                    try { outcome = ApplyInboundSeekLocked(cmd.SeekToMs); }
                    finally { _lock.Release(); }
                    break;
                case ConnectCmd.SetShufflingContext:
                    await RemoteSetShuffleAsync(cmd.BoolArg).ConfigureAwait(false);
                    outcome = ConnectCommandOutcome.Applied;
                    break;
                case ConnectCmd.SetRepeatingContext:
                    await RemoteSetRepeatAsync(cmd.BoolArg ? RepeatMode.Context : RepeatMode.Off).ConfigureAwait(false);
                    outcome = ConnectCommandOutcome.Applied;
                    break;
                case ConnectCmd.SetRepeatingTrack:
                    await RemoteSetRepeatAsync(cmd.BoolArg ? RepeatMode.Track : RepeatMode.Off).ConfigureAwait(false);
                    outcome = ConnectCommandOutcome.Applied;
                    break;
                case ConnectCmd.Play:
                case ConnectCmd.Transfer: outcome = await HandleInboundPlayOrTransferAsync(cmd).ConfigureAwait(false); break;
                case ConnectCmd.AddToQueue: outcome = await HandleAddToQueueAsync(cmd).ConfigureAwait(false); break;
                case ConnectCmd.SetQueue: outcome = await HandleSetQueueAsync(cmd).ConfigureAwait(false); break;
                case ConnectCmd.UpdateContext: outcome = await HandleUpdateContextAsync(cmd).ConfigureAwait(false); break;
                case ConnectCmd.SetOptions: outcome = await HandleSetOptionsAsync(cmd.Payload).ConfigureAwait(false); break;
                default:
                    _log.Info("controller: unhandled remote command " + cmd.Kind);
                    return ConnectCommandOutcome.NoOp;
            }
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return ConnectCommandOutcome.Superseded; }
        catch (Exception ex)
        {
            _log.Warn($"controller inbound {cmd.Endpoint} failed: {ex.Message}", ex);
            return ConnectCommandOutcome.Failed;
        }
    }

    // A late/stale inbound command addressed to us must not GHOST-RESUME the remote device's track when we hold
    // nothing locally to act on — that combination (cluster has genuinely handed the active slot to someone else AND
    // there's no local session) is a pure viewer echo. This guard is deliberately narrow to that one danger; it is
    // NOT a general "we're not the target" check — HandleRemoteCommandAsync's dispatch is "WE are always the target"
    // regardless of what the cluster's active-device id says (see InboundCommand_AlwaysLocal_EvenWhenClusterShowsAnotherActive).
    // Caller holds _lock.
    bool IsPassiveViewer()
    {
        var aid = _projection.ActiveDeviceId;
        return !string.IsNullOrEmpty(aid) && aid != _ourDeviceId && _session.Current is null;
    }

    // The blind inbound Pause used to call _currentHost.Pause() + EmitState unconditionally: over a torn-down/disposed
    // host that Pause() is a silent no-op anyway, exactly like calling it on a live one. No session at all ⇒ nothing
    // to pause (NoOp, no publish). A session that exists calls Pause() and folds the state regardless of the host's
    // current clock — an inbound command addressed to us is ALWAYS local (even right after a takeover tore our own
    // host down, e.g. a stale cluster echo racing the dealer's real-time routing); Seek below has always worked this
    // way. Caller holds _lock.
    ConnectCommandOutcome ApplyInboundPauseLocked()
    {
        if (_session.Current is null) return ConnectCommandOutcome.NoOp;
        _currentHost.Pause();
        EmitState(EvKind.Paused);
        return ConnectCommandOutcome.Applied;
    }

    // The blind inbound SeekTo used to call EmitSeeked unconditionally regardless of whether a local session even
    // exists. Caller holds _lock.
    ConnectCommandOutcome ApplyInboundSeekLocked(long seekToMs)
    {
        if (_session.Current is null) return ConnectCommandOutcome.NoOp;
        EmitSeeked(seekToMs, SeekMode.Accurate);
        return ConnectCommandOutcome.Applied;
    }

    // The inbound Resume half of the same guard — kept separate from the public ResumeAsync/LocalResumeAsync path
    // (transfer-to-self, the UI Resume button) so the passive-viewer check applies ONLY to a command addressed to us
    // by the dealer, never to the ghost/transfer-to-self resume that deliberately runs with no local session yet.
    async Task<ConnectCommandOutcome> HandleInboundResumeAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsPassiveViewer()) return ConnectCommandOutcome.NoOp;
            await ResumeCurrentLockedAsync(ct).ConfigureAwait(false);
            return ConnectCommandOutcome.Applied;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Apply an inbound connect/volume MESSAGE to this device without echoing it as an outbound command.</summary>
    public async Task<ConnectCommandOutcome> HandleInboundVolumeAsync(int volume0_65535, CancellationToken ct = default)
    {
        double volume01 = Math.Clamp(volume0_65535, 0, 65535) / 65535.0;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Math.Abs(_projection.Volume - volume01) < 0.000001) return ConnectCommandOutcome.NoOp;
            _currentHost.SetVolume(volume01);
            if (_currentHost.ClockValid) _projection.SetLocalVolume(volume01, _currentHost.PositionMs);
            else _projection.SetLocalVolume(volume01);
            EmitState(EvKind.VolumeChanged);
            _log.Event(WaveeLogLevel.Info, "connect.volume.applied", "inbound Connect volume applied",
                fields:
                [
                    WaveeLogField.Of("volume", Math.Clamp(volume0_65535, 0, 65535)),
                    WaveeLogField.Of("normalized", volume01),
                ]);
            return ConnectCommandOutcome.Applied;
        }
        finally { _lock.Release(); }
    }

    // Inbound next_track / skip_next (F7): a payload (command.track {uri,uid}) is a row-jump → skip-to-uid + play; a bare
    // skip_next advances one exactly as before. skip_prev never carries a payload (unchanged).
    async Task<ConnectCommandOutcome> HandleInboundSkipNextAsync(ConnectCommand cmd)
    {
        if (string.IsNullOrEmpty(cmd.TrackUri) && string.IsNullOrEmpty(cmd.TrackUid))
        {
            await LocalNextAsync().ConfigureAwait(false);
            return ConnectCommandOutcome.Applied;
        }
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _projection.NoteLocalCommand();
            if (_session.SkipToUid(cmd.TrackUid, cmd.TrackUri) is { } snap)
            {
                _snap = snap;
                await LoadAndPlayCurrentAsync(EvKind.TrackChanged, default).ConfigureAwait(false);
                return ConnectCommandOutcome.Applied;
            }
        }
        finally { _lock.Release(); }
        // identity miss (the target isn't in our resolved session) → fall back to a plain advance.
        _log.Info($"inbound next_track: row uid={cmd.TrackUid} uri={cmd.TrackUri} not found in session — advancing one");
        await LocalNextAsync().ConfigureAwait(false);
        return ConnectCommandOutcome.Applied;
    }

    async Task<ConnectCommandOutcome> HandleInboundPlayOrTransferAsync(ConnectCommand cmd)
    {
        bool previousConnectOrigin = _connectOriginatedPlayback;
        try
        {
            _connectOriginatedPlayback = true;
            if (cmd.Kind == ConnectCmd.Transfer)
                return await HandleInboundTransferAsync(cmd).ConfigureAwait(false);

            if (ExtractPlayIntent(cmd.Payload) is { } intent)
            {
                long generation = Interlocked.Increment(ref _contextGeneration);
                string previousSession = _remoteSessionId;
                string previousInteraction = _remoteInteractionId;
                string previousPage = _remotePageInstanceId;
                _remoteSessionId = string.IsNullOrEmpty(intent.SessionId) ? Guid.NewGuid().ToString("N") : intent.SessionId;
                _remoteInteractionId = intent.InteractionId;
                _remotePageInstanceId = intent.PageInstanceId;
                LogPlayIntent("remote-" + cmd.Kind + "-from-" + (string.IsNullOrEmpty(cmd.SenderDeviceId) ? "?" : cmd.SenderDeviceId),
                    intent.Context.Uri, intent.Context.SkipToIndex ?? 0, intent.Context.SkipToTrackUri,
                    intent.Context.SkipToTrackUid, intent.Context.EmbeddedPages?.Count ?? 0, local: true);
                if (!await LocalPlaySpecAsync(intent.Context, default, generation, intent.InitiallyPaused, intent.Shuffle,
                        intent.SeekToMs, intent.Repeat, intent.MediaKind)
                    .ConfigureAwait(false))
                {
                    _connectOriginatedPlayback = previousConnectOrigin;
                    _remoteSessionId = previousSession;
                    _remoteInteractionId = previousInteraction;
                    _remotePageInstanceId = previousPage;
                    return ConnectCommandOutcome.NoOp;
                }
                return ConnectCommandOutcome.Applied;
            }

            _connectOriginatedPlayback = previousConnectOrigin;
            _log.Info("remote play carried no context spec");
            return ConnectCommandOutcome.NoOp;
        }
        catch (Exception ex)
        {
            _connectOriginatedPlayback = previousConnectOrigin;
            _log.Info("controller inbound play/transfer error: " + ex.Message);
            return ConnectCommandOutcome.Failed;
        }
    }

    async Task<ConnectCommandOutcome> HandleInboundTransferAsync(ConnectCommand cmd)
    {
        if (_transferDecoder is null || !TryExtractTransfer(cmd.Payload, out var encoded, out var modes))
        {
            _log.Info("remote transfer has no decodable inner data; falling back to cluster resume");
            await LocalResumeAsync(default).ConfigureAwait(false);
            return ConnectCommandOutcome.NoOp;
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException)
        {
            _log.Warn("remote transfer data was not valid base64; falling back to cluster resume");
            await LocalResumeAsync(default).ConfigureAwait(false);
            return ConnectCommandOutcome.NoOp;
        }
        if (!_transferDecoder.TryDecode(bytes, out var state))
        {
            _log.Warn($"remote transfer protobuf could not be decoded ({bytes.Length} bytes); falling back to cluster resume");
            await LocalResumeAsync(default).ConfigureAwait(false);
            return ConnectCommandOutcome.NoOp;
        }

        var selected = state.IsPlayingQueue && state.Queue.Count > 0 ? state.Queue[0] : state.CurrentTrack;
        string currentUri = selected.Uri;
        if (string.IsNullOrEmpty(currentUri) && selected.Gid.Length == 16)
            currentUri = "spotify:track:" + Base62.Encode(selected.Gid);

        long generation = Interlocked.Increment(ref _contextGeneration);
        string contextUri = string.IsNullOrEmpty(state.ContextUri) ? currentUri : state.ContextUri;
        ResolvedContext resolved = ResolvedContext.Empty;
        if (!string.IsNullOrEmpty(contextUri))
        {
            var contextSpec = new ContextSpec(contextUri, state.ContextUrl, null,
                currentUri, string.IsNullOrEmpty(selected.Uid) ? state.CurrentUid : selected.Uid, null,
                state.ContextMetadata);
            try { resolved = await _contexts.ResolveAsync(contextSpec, default).ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn("transfer context resolve failed; retaining transferred current only: " + ex.Message, ex); }
        }
        if (generation != Volatile.Read(ref _contextGeneration)) return ConnectCommandOutcome.Superseded;

        string currentUid = string.IsNullOrEmpty(selected.Uid) ? state.CurrentUid : selected.Uid;
        QueuedTrack current = default;
        int missCursor = -1;
        // The §6 restore ladder: uid → uri → saved index (in range + playable; the transfer proto has no index field, so
        // it rides the current track's context_index metadata when the sender stamped one). The context head is the LAST
        // resort and only when the sender named no current at all — an explicitly transferred current (often a gid with
        // an empty uri, and frequently absent from the resolved page: a phone playing a track the page doesn't list) is
        // patched in OUTSIDE the spine below instead. always_play_something means "play something rather than nothing",
        // never "prefer the head over the track the sender told us is playing".
        int savedIndex = SavedContextIndexOf(selected.Metadata);
        int currentIndex = ContextResolve.ResolveRestoreIndex(resolved.Tracks, currentUri, currentUid, savedIndex,
            allowContextHead: modes.RestoreTrack == "always_play_something" && string.IsNullOrEmpty(currentUri));
        if (currentIndex >= 0) current = resolved.Tracks[currentIndex];
        else if (!string.IsNullOrEmpty(currentUri))
        {
            current = await HydrateOneAsync(currentUri, default).ConfigureAwait(false);
            // §6.5 — the patched current stands outside the spine, but Next() must land on the successor of where it SAT
            // in the saved order (cursor = savedIndex-1 → Next() plays row[savedIndex]), never wrap to context[0].
            if (savedIndex > 0) missCursor = savedIndex - 1;
        }
        else
        {
            _log.Warn("transfer inner state contained no usable current track; falling back to cluster resume");
            await LocalResumeAsync(default).ConfigureAwait(false);
            return ConnectCommandOutcome.NoOp;
        }
        if (!string.IsNullOrEmpty(currentUid) && string.IsNullOrEmpty(current.Uid))
            current = current with { Uid = currentUid };

        var queueRefs = new List<QueuedRef>();
        int queueStart = state.IsPlayingQueue && state.Queue.Count > 0 ? 1 : 0;
        for (int i = queueStart; i < state.Queue.Count; i++)
        {
            var q = state.Queue[i];
            string uri = q.Uri;
            if (string.IsNullOrEmpty(uri) && q.Gid.Length == 16) uri = "spotify:track:" + Base62.Encode(q.Gid);
            if (!string.IsNullOrEmpty(uri)) queueRefs.Add(new QueuedRef(uri, q.Uid, "queue", q.Metadata));
        }
        var transferredQueue = queueRefs.Count == 0
            ? Array.Empty<QueuedTrack>()
            : await _contexts.HydrateAsync(queueRefs, default).ConfigureAwait(false);

        long position = Math.Max(0, state.PositionMs);
        // restore_paused DEFAULTS to "restore" (a missing/blank option honors the transferred paused state — findings
        // matrix "any other string → plays" was the bug); only an explicit "kill" forces play.
        bool paused = state.Paused && !string.Equals(modes.RestorePaused, "kill", StringComparison.Ordinal);
        if (!paused && modes.RestorePosition == "extrapolate" && state.TimestampMs > 0)
        {
            // Age against the CORRECTED server clock, never wall time: the transfer's TimestampMs is a server-side
            // stamp, and diffing it against our own unsynced local clock lets skew (a slow/fast system clock, or a
            // clock that free-runs before the estimator's first probe lands) inflate or invert the elapsed age. A
            // still-unsynced clock (serverNow == 0, the estimator's own sentinel) means "no extrapolation" rather than
            // a guess — a small constant staleness beats unbounded skew.
            long serverNow = ServerNowUnixMs?.Invoke() ?? 0;
            long age = serverNow > 0 ? Math.Max(0, serverNow - state.TimestampMs) : 0;
            position += (long)Math.Round(age * Math.Max(0, state.Speed));
        }
        // The transfer's current track carrying a music-video association (track_player: "video", media.type, the
        // manifest id) — the SAME key list a cluster row uses (MediaSwitchLogic.HasVideoMetadata) — forces this load
        // to VIDEO exactly like an inbound player_options_override.modes.media would, instead of falling through to
        // the ordinary Connect-audio-first resolution that silently kept the audio host docked. media.start_position
        // is the video association's own start point (an intro/outro cut the audio timestamp doesn't know about) and
        // wins over the transferred/extrapolated audio position whenever the sender stamped one.
        // bug 8: the sender's TransferPlayerOptions.modes["video_persistence"] (its own live media-kind stamp on the
        // hand-off, distinct from the per-track metadata check) forces VIDEO too, so a video hand-off is honored even
        // when the current track's own metadata didn't (yet) carry the claim.
        PlayableKind? forcedKind = MediaSwitchLogic.HasVideoMetadata(selected.Metadata) || state.VideoPersistence
            ? PlayableKind.Video : null;
        if (forcedKind == PlayableKind.Video && selected.Metadata.TryGetValue("media.start_position", out var startPosRaw)
            && long.TryParse(startPosRaw, out var startPos) && startPos >= 0 && startPos != position)
            position = startPos;
        if (current.Track.DurationMs > 0) position = Math.Min(position, current.Track.DurationMs);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _contextGeneration)) return ConnectCommandOutcome.Superseded;
            _remoteSessionId = modes.RetainSession == "do_not_retain" || string.IsNullOrEmpty(_remoteSessionId)
                ? Guid.NewGuid().ToString("N")
                : _remoteSessionId;
            _projection.SetContextMetadata(resolved.Metadata ?? state.ContextMetadata);
            _snap = _session.SetTransferredContext(contextUri, resolved.Tracks, current, clearUserQueue: true, missCursor);
            if (transferredQueue.Count > 0) _snap = _session.EnqueueUser(transferredQueue);
            bool shuffleChanged = _session.Shuffle != state.Shuffle;
            _snap = _session.SetShuffle(state.Shuffle);
            PlaybackBucketDiagnostics.ShuffleToggle("transfer", state.Shuffle, shuffleChanged, _snap);
            _snap = _session.SetRepeat(state.Repeat);
            _nextPageUrl = string.IsNullOrEmpty(resolved.NextPageUrl) ? null : resolved.NextPageUrl;
            _contextIsInfinite = resolved.IsInfinite || ContextResolve.IsInfinite(contextUri);
            _autoplayLatchedFor = null;
            _continuationFetch = null;
            await LoadAndPlayCurrentAsync(paused ? EvKind.Paused : EvKind.Started, default, position, paused,
                expectedGeneration: generation, mediaKindOverride: forcedKind).ConfigureAwait(false);
        }
        finally { _lock.Release(); }

        _log.Event(WaveeLogLevel.Info, "connect.transfer.applied", "TransferState applied",
            fields:
            [
                WaveeLogField.Of("dataBytes", bytes.Length),
                WaveeLogField.Of("context", WaveeLogRedaction.HashLike(contextUri)),
                WaveeLogField.Of("current", WaveeLogRedaction.HashLike(current.Uri)),
                WaveeLogField.Of("resolvedTracks", resolved.Count),
                WaveeLogField.Of("queueTracks", transferredQueue.Count),
                WaveeLogField.Of("positionMs", position),
                WaveeLogField.Of("paused", paused),
                WaveeLogField.Of("restorePosition", modes.RestorePosition),
                WaveeLogField.Of("restoreTrack", modes.RestoreTrack),
            ]);
        return ConnectCommandOutcome.Applied;
    }

    readonly record struct TransferModes(
        string RestorePaused,
        string RestorePosition,
        string RestoreTrack,
        string RetainSession);

    static bool TryExtractTransfer(byte[] payload, out string data, out TransferModes modes)
    {
        data = "";
        modes = default;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("command", out var command)
                || !command.TryGetProperty("data", out var encoded)
                || encoded.ValueKind != JsonValueKind.String)
                return false;
            data = encoded.GetString() ?? "";
            string paused = "", position = "", track = "", retain = "";
            if (command.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
            {
                paused = StringProperty(options, "restore_paused");
                position = StringProperty(options, "restore_position");
                track = StringProperty(options, "restore_track");
                retain = StringProperty(options, "retain_session");
            }
            modes = new TransferModes(paused, position, track, retain);
            return data.Length > 0;
        }
        catch { return false; }
    }

    static string StringProperty(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    // The saved context position a transferred current track carries in its metadata (context_index / original_index) —
    // the index rung of the §6 ladder. -1 when the sender stamped none (most transfers).
    static int SavedContextIndexOf(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return -1;
        if ((metadata.TryGetValue("context_index", out var v) || metadata.TryGetValue("original_index", out v))
            && int.TryParse(v, out int i) && i >= 0)
            return i;
        return -1;
    }

    // add_to_queue: append one track to the user queue — or, if nothing is loaded, start playing it (the idle-start rule).
    async Task<ConnectCommandOutcome> HandleAddToQueueAsync(ConnectCommand cmd)
    {
        try
        {
            if (ParseQueueTrack(cmd.Payload) is not { } qref) return ConnectCommandOutcome.NoOp;
            var hydrated = await _contexts.HydrateAsync(new[] { qref }, default).ConfigureAwait(false);
            if (hydrated.Count == 0) return ConnectCommandOutcome.NoOp;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session.Current is null)
                {
                    if (RejectLocalPlay()) return ConnectCommandOutcome.NoOp;   // inbound add-to-queue while idle would start local playback → toast + abort
                    SetQueueContext(hydrated[0].Uri, hydrated, 0);
                    await LoadAndPlayCurrentAsync(EvKind.Started, default).ConfigureAwait(false);
                }
                else EmitSnap(_session.EnqueueUser(hydrated), EvKind.QueueChanged);   // active device mints q-uids for uid:"" rows (§7.4)
                return ConnectCommandOutcome.Applied;
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info("controller add_to_queue error: " + ex.Message); return ConnectCommandOutcome.Failed; }
    }

    // set_queue (F8): full reconcile of ALL of next_tracks (queue rows → user queue by uid, context rows → Upcoming, autoplay
    // tail + delimiter/meta markers preserved). The current track is untouched (set_queue never changes what's playing).
    async Task<ConnectCommandOutcome> HandleSetQueueAsync(ConnectCommand cmd)
    {
        try
        {
            var prev = ParseWireEntries(cmd.Payload, "prev_tracks");
            var next = ParseWireEntries(cmd.Payload, "next_tracks");
            // An EMPTY next_tracks + prev_tracks is not "nothing to do" — it's the wire's own shape for an explicit
            // CLEAR, and this used to bounce off an early return here that left the stale queue in place.
            string revision = ParseQueueRevisionString(cmd.Payload);
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ulong.TryParse(revision, out var parsedRevision))
                {
                    if (_lastAppliedQueueRevision is { } last && parsedRevision < last) return ConnectCommandOutcome.Superseded;
                    _lastAppliedQueueRevision = parsedRevision;
                }
                EmitSnap(_session.ApplySetQueue(prev, next, revision), EvKind.QueueChanged);
                return ConnectCommandOutcome.Applied;
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info("controller set_queue error: " + ex.Message); return ConnectCommandOutcome.Failed; }
    }

    // update_context: the context's tracks changed (e.g. the playlist was edited) — re-resolve and keep playing the same
    // track (reposition the cursor to it in the new order); if it's gone, start the new context from the top.
    async Task<ConnectCommandOutcome> HandleUpdateContextAsync(ConnectCommand cmd)
    {
        try
        {
            if (ExtractPlayIntent(cmd.Payload) is not { } intent) return ConnectCommandOutcome.NoOp;
            var spec = intent.Context;
            string incomingSession = string.IsNullOrEmpty(cmd.SessionId) ? intent.SessionId : cmd.SessionId;
            if (!string.IsNullOrEmpty(_remoteSessionId) && !string.IsNullOrEmpty(incomingSession)
                && !string.Equals(_remoteSessionId, incomingSession, StringComparison.Ordinal))
            {
                _log.Event(WaveeLogLevel.Debug, "connect.update_context.stale-session",
                    "UpdateContext ignored because its session is no longer active",
                    fields:
                    [
                        WaveeLogField.Of("incomingSession", WaveeLogRedaction.HashLike(incomingSession)),
                        WaveeLogField.Of("activeSession", WaveeLogRedaction.HashLike(_remoteSessionId)),
                        WaveeLogField.Of("context", WaveeLogRedaction.HashLike(spec.Uri)),
                    ]);
                return ConnectCommandOutcome.NoOp;
            }
            // A SESSIONLESS update_context (no session id of its own) can never be authenticated by session id — the
            // only honest gate left is "does it actually describe the context we're playing right now", and that must
            // hold regardless of _remoteSessionId's OWN state. This used to gate on "_remoteSessionId is empty", but
            // HandleInboundPlayOrTransferAsync stamps _remoteSessionId the moment a play/transfer lands — often tens
            // of ms before ITS OWN trailing sessionless update_context arrives — so a stale command addressed to a
            // context we're no longer playing could sail through on the strength of a session id that belongs to a
            // different command entirely.
            if (string.IsNullOrEmpty(incomingSession)
                && !string.Equals(_session.ContextUri, spec.Uri, StringComparison.Ordinal))
            {
                _log.Event(WaveeLogLevel.Debug, "connect.update_context.orphan",
                    "UpdateContext ignored because no matching active context exists",
                    fields: [WaveeLogField.Of("context", WaveeLogRedaction.HashLike(spec.Uri))]);
                return ConnectCommandOutcome.NoOp;
            }

            long generation = Interlocked.Increment(ref _contextGeneration);
            var resolved = await _contexts.ResolveAsync(spec, default).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _contextGeneration))
            {
                _log.Debug("update_context resolution superseded before apply");
                return ConnectCommandOutcome.Superseded;
            }
            if (resolved.Count == 0)
            {
                _log.Warn($"update_context resolved no tracks; preserving current context ({WaveeLogRedaction.HashLike(spec.Uri)})");
                return ConnectCommandOutcome.NoOp;
            }
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation != Volatile.Read(ref _contextGeneration)) return ConnectCommandOutcome.Superseded;
                string contextUri = resolved.ContextUri ?? spec.Uri;
                bool sameRows = _session.HasSameContextRows(contextUri, resolved.Tracks);
                _nextPageUrl = string.IsNullOrEmpty(resolved.NextPageUrl) ? null : resolved.NextPageUrl;
                _contextIsInfinite = resolved.IsInfinite || ContextResolve.IsInfinite(contextUri);
                _autoplayLatchedFor = null;
                _continuationFetch = null;
                _projection.SetContextMetadata(resolved.Metadata ?? spec.Metadata);

                if (sameRows)
                {
                    Emit(BuildEvent(EvKind.QueueChanged, _snap.Current?.Track, _currentHost.PositionMs));
                    _log.Event(WaveeLogLevel.Debug, "connect.update_context.noop",
                        "UpdateContext resolved to the active row sequence",
                        fields:
                        [
                            WaveeLogField.Of("tracks", resolved.Count),
                            WaveeLogField.Of("context", WaveeLogRedaction.HashLike(contextUri)),
                            WaveeLogField.Of("generation", generation),
                        ]);
                }
                else
                {
                    _snap = _session.ReplaceContextPreservingCurrent(contextUri, resolved.Tracks);
                    EmitSnap(_snap, EvKind.QueueChanged);
                    _log.Event(WaveeLogLevel.Info, "connect.update_context.applied",
                        "UpdateContext replaced context rows while preserving the current playable",
                        fields:
                        [
                            WaveeLogField.Of("tracks", resolved.Count),
                            WaveeLogField.Of("current", WaveeLogRedaction.HashLike(_session.Current?.Uri ?? "")),
                            WaveeLogField.Of("context", WaveeLogRedaction.HashLike(contextUri)),
                            WaveeLogField.Of("generation", generation),
                        ]);
                }
                if (string.IsNullOrEmpty(_remoteSessionId) && !string.IsNullOrEmpty(incomingSession))
                    _remoteSessionId = incomingSession;
                return ConnectCommandOutcome.Applied;
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info("controller update_context error: " + ex.Message); return ConnectCommandOutcome.Failed; }
    }

    // set_options: apply shuffle + repeat (the desktop sends explicit shuffling_context / repeating_context / repeating_track).
    // Parse off-lock (immutable JSON), then apply the session mutations under _lock (they rebuild the context list, F7).
    async Task<ConnectCommandOutcome> HandleSetOptionsAsync(byte[] payload)
    {
        try
        {
            bool? shuffle = null; RepeatMode? repeat = null;
            using (var doc = JsonDocument.Parse(payload))
            {
                if (!doc.RootElement.TryGetProperty("command", out var c)) return ConnectCommandOutcome.NoOp;
                if (c.TryGetProperty("shuffling_context", out var sh) && sh.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    shuffle = sh.GetBoolean();
                bool hasRepTrack = c.TryGetProperty("repeating_track", out var rt);
                bool hasRepCtx = c.TryGetProperty("repeating_context", out var rc);
                if (hasRepTrack || hasRepCtx)
                {
                    bool repTrack = hasRepTrack && rt.ValueKind == JsonValueKind.True;
                    bool repCtx = hasRepCtx && rc.ValueKind == JsonValueKind.True;
                    repeat = repTrack ? RepeatMode.Track : repCtx ? RepeatMode.Context : RepeatMode.Off;
                }
            }
            if (shuffle is null && repeat is null) return ConnectCommandOutcome.NoOp;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                QueueSnapshot snap = _snap;
                if (shuffle is { } s)
                {
                    bool shuffleChanged = _session.Shuffle != s;
                    snap = _session.SetShuffle(s);
                    PlaybackBucketDiagnostics.ShuffleToggle("set_options", s, shuffleChanged, snap);
                }
                if (repeat is { } r) snap = _session.SetRepeat(r);
                EmitSnap(snap, EvKind.OptionsChanged);
                return ConnectCommandOutcome.Applied;
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info("controller set_options error: " + ex.Message); return ConnectCommandOutcome.Failed; }
    }

    // Inbound shuffle/repeat off the dealer thread — take _lock (SetShuffle/SetRepeat rebuild the context list, F7).
    async Task RemoteSetShuffleAsync(bool on)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            bool changed = _session.Shuffle != on;
            var snap = _session.SetShuffle(on);
            PlaybackBucketDiagnostics.ShuffleToggle("remote", on, changed, snap);
            EmitSnap(snap, EvKind.OptionsChanged);
        }
        finally { _lock.Release(); }
    }

    async Task RemoteSetRepeatAsync(RepeatMode mode)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { EmitSnap(_session.SetRepeat(mode), EvKind.OptionsChanged); }
        finally { _lock.Release(); }
    }

    // ── local execution primitives (shared by the public verbs + inbound handling) ───────────────────────────────────
    // Seed the session with a resolved context. keepUserQueue is always true (§4.7 — Spotify parity: a new context keeps the
    // user queue). The context display metadata rides alongside via SetContextMetadata; the atomic publish happens at the
    // caller's LoadAndPlayCurrent / EmitSnap, never here (F3: no split publish).
    void SetQueueContext(string uri, IReadOnlyList<QueuedTrack> tracks, int startIndex,
        string? nextPageUrl = null, bool isInfinite = false, IReadOnlyDictionary<string, string>? metadata = null)
    {
        Interlocked.Increment(ref _contextGeneration);
        _snap = _session.SetContext(uri, tracks, startIndex);
        _nextPageUrl = string.IsNullOrEmpty(nextPageUrl) ? null : nextPageUrl;
        _contextIsInfinite = isInfinite || ContextResolve.IsInfinite(uri);
        _autoplayLatchedFor = null;
        _continuationFetch = null;
        _projection.SetContextMetadata(metadata);
        DiagnoseQueue("controller.set-context");
    }

    // §7.3: resolve, then honor an identity-strict skip target. ResolveAsync returns StartIndex = -1 on identity miss (the
    // blind index fallback is gone, F2). While hunting the skip target we page deeper than MaxEagerPages (bounded); on a
    // final miss (a regenerated dynamic context) we patch the clicked track in as current rather than play an unrelated row.
    const int SkipHuntMaxPages = 40;
    async Task<bool> LocalPlaySpecAsync(
        ContextSpec spec,
        CancellationToken ct,
        long expectedGeneration = 0,
        bool initiallyPaused = false,
        bool? shuffle = null,
        long seekToMs = -1,
        RepeatMode? repeat = null,
        PlayableKind? mediaKind = null)
    {
        long generation = expectedGeneration == 0 ? Interlocked.Increment(ref _contextGeneration) : expectedGeneration;
        var resolved = await _contexts.ResolveAsync(spec, ct).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _contextGeneration)) return false;
        if (resolved.Count == 0) { _log.Info("play: context resolved to 0 tracks: " + spec.Uri); return false; }

        IReadOnlyList<QueuedTrack> tracks = resolved.Tracks;
        int start = resolved.StartIndex;
        string? nextPage = resolved.NextPageUrl;
        bool hasSkipTarget = !string.IsNullOrEmpty(spec.SkipToTrackUid) || !string.IsNullOrEmpty(spec.SkipToTrackUri);

        if (start < 0 && hasSkipTarget && !resolved.IsInfinite && !string.IsNullOrEmpty(nextPage))
        {
            var acc = new List<QueuedTrack>(tracks);
            int pages = 0;
            while (start < 0 && !string.IsNullOrEmpty(nextPage) && pages < SkipHuntMaxPages)
            {
                var page = await _contexts.LoadMoreAsync(nextPage!, ct).ConfigureAwait(false);
                if (page.Tracks.Count > 0)
                {
                    acc.AddRange(page.Tracks);
                    start = ContextResolve.FindStartIndex(acc, spec.SkipToTrackUri, spec.SkipToTrackUid);
                }
                nextPage = page.NextPageUrl;
                pages++;
            }
            tracks = acc;
            if (start >= 0) _log.Info($"skip target found after paging {pages} extra pages ({tracks.Count} tracks)");
        }

        if (start < 0 && hasSkipTarget)
        {
            // §7.3.2: identity miss — patch the clicked track as current (context_patched), never a blind index.
            var patched = await BuildPatchedTrackAsync(spec, ct).ConfigureAwait(false);
            var list = new List<QueuedTrack>(tracks.Count + 1) { patched };
            list.AddRange(tracks);
            tracks = list;
            start = 0;
            _log.Info($"queue.skip-miss: patched {spec.SkipToTrackUri ?? spec.SkipToTrackUid} as current over {resolved.ContextUri ?? spec.Uri}");
            PlaybackBucketDiagnostics.Continuation("queue.skip-miss", "skip target not resolved; patched clicked track as current",
                WaveeLogField.Of("target", spec.SkipToTrackUri ?? spec.SkipToTrackUid ?? ""),
                WaveeLogField.Of("ctx", resolved.ContextUri ?? spec.Uri));
        }

        if (start < 0) start = 0;   // no skip target at all → start at the top
        // The other half of the attribution pair: what the (silent, network-latency-bearing) resolve actually decided to
        // play. Pairing this with the `play intent` line above turns "the app jumped to another song by itself" into a
        // two-line story — including HOW LONG the resolve took, which is what makes a play look spontaneous.
        _log.Info($"play resolved ctx={resolved.ContextUri ?? spec.Uri} tracks={tracks.Count} start={start} " +
            $"current={(start < tracks.Count ? tracks[start].Uri : "-")}");
        return await LocalPlayTracksAsync(resolved.ContextUri ?? spec.Uri, tracks, start, ct,
            nextPage, resolved.IsInfinite, resolved.Metadata ?? spec.Metadata, generation, initiallyPaused, shuffle,
            seekToMs, repeat, mediaKind)
            .ConfigureAwait(false);
    }

    // Build the clicked track as a context row patched in as current (§7.3.2): hydrate for display, tag context_patched.
    async Task<QueuedTrack> BuildPatchedTrackAsync(ContextSpec spec, CancellationToken ct)
    {
        string uri = spec.SkipToTrackUri ?? "";
        var meta = new Dictionary<string, string>(StringComparer.Ordinal) { ["context_patched"] = "true" };
        if (string.IsNullOrEmpty(uri)) return new QueuedTrack(ContextResolve.Synthetic(spec.SkipToTrackUid ?? ""), spec.SkipToTrackUid ?? "", "context", meta);
        var q = await HydrateOneAsync(uri, ct).ConfigureAwait(false);
        return new QueuedTrack(q.Track, spec.SkipToTrackUid ?? "", "context", meta);
    }

    async Task<bool> LocalPlayTracksAsync(string contextUri, IReadOnlyList<QueuedTrack> tracks, int startIndex, CancellationToken ct,
        string? nextPageUrl = null, bool isInfinite = false, IReadOnlyDictionary<string, string>? metadata = null,
        long expectedGeneration = 0, bool initiallyPaused = false, bool? shuffle = null,
        long seekToMs = -1, RepeatMode? repeat = null, PlayableKind? mediaKind = null)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (expectedGeneration != 0 && expectedGeneration != Volatile.Read(ref _contextGeneration)) return false;
            SetQueueContext(contextUri, tracks, startIndex, nextPageUrl, isInfinite, metadata);
            if (shuffle is { } shuffleValue) _snap = _session.SetShuffle(shuffleValue);
            // Applied right alongside the shuffle override above — both are player_options_override fields on the
            // SAME inbound play/transfer intent.
            if (repeat is { } repeatValue) _snap = _session.SetRepeat(repeatValue);
            // SetQueueContext just minted its OWN generation (it always bumps _contextGeneration) — that, not the
            // pre-context-switch value we validated above, is what THIS load must be checked against (§5).
            await LoadAndPlayCurrentAsync(initiallyPaused ? EvKind.Paused : EvKind.Started, ct,
                seekToMs, initiallyPaused: initiallyPaused, expectedGeneration: Volatile.Read(ref _contextGeneration),
                mediaKindOverride: mediaKind).ConfigureAwait(false);
            return true;
        }
        finally { _lock.Release(); }
    }

    async Task<QueuedTrack> HydrateOneAsync(string uri, CancellationToken ct)
    {
        // NO CATALOGUE OWNS THIS URI. Asking the hydrator about a `wavee:module:…` playable can only ever come back as
        // the uri-only placeholder, which is precisely the thin row a restart used to show. The owner speaks first.
        if (await HydrateThroughOwnerAsync(uri, ct).ConfigureAwait(false) is { } owned) return new QueuedTrack(owned, "");
        try
        {
            var hydrated = await _contexts.HydrateAsync(new[] { new QueuedRef(uri, "") }, ct).ConfigureAwait(false);
            if (hydrated.Count > 0) return hydrated[0];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Info("track hydrate failed; falling back to uri placeholder: " + ex.Message); }
        return new QueuedTrack(SyntheticTrack(uri), "");
    }

    async Task LocalResumeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { await ResumeCurrentLockedAsync(ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    // The one local resume ladder (§1 + §8 read order), shared by ResumeAsync / inbound resume / transfer-to-self.
    // Caller holds _lock.
    async Task ResumeCurrentLockedAsync(CancellationToken ct)
    {
        if (_session.Current is not null)
        {
            if (RejectLocalPlay()) return;
            // A recovery-seeded session (§1) OR a host StopHostKeepingSession tore down (a takeover, a stray-host
            // cleanup, a fast-start body failure) both leave the viewer showing "paused" over a host that holds
            // NOTHING — belt-and-braces on the SECOND condition (§2): even if some path reaches here with the latch
            // unset, a host with neither an honest clock nor a playing decoder cannot be resumed by a bare Play(), so
            // treat it exactly like the pending-load case rather than trust the empty host. Either way the first
            // Resume fast-starts through LoadAndPlayCurrentAsync at the stored position (Herodotus only at 0), and
            // EvKind.Resumed is only ever emitted below, on the branch that actually played something.
            if (_restorePendingLoad || (!_currentHost.ClockValid && !_currentHost.IsPlaying))
            {
                _restorePendingLoad = true;
                long pos = Math.Max(0, _projection.PositionMs);
                MintCommand("playbtn");
                await LoadAndPlayCurrentAsync(EvKind.Started, ct, pos > 0 ? pos : -1, skipOnUnplayable: true)
                    .ConfigureAwait(false);
                return;
            }
            _currentHost.Play(); EmitState(EvKind.Resumed);   // we have loaded local media → normal resume
            return;
        }
        await GhostResumeAsync(ct).ConfigureAwait(false);   // cold/ghost → seed from the cluster (else the local snapshot)
    }

    async Task LocalNextAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _projection.NoteLocalCommand();
            QueueEntry? advanced = null;
            if (_session.Next() is { } snap) { _snap = snap; advanced = snap.Current; }
            // Attribution: a queue STEP and a fresh context PLAY both end in the same silent LoadAndPlayCurrentAsync, so
            // without this line the log cannot tell "the user pressed Next" from "something re-resolved the context".
            _log.Info($"queue advance → {advanced?.Track.Uri ?? "(end of context)"}");
            if (advanced is not null) await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
            else if (await TryContinueContextAsync(ct).ConfigureAwait(false)) { }
            else { _currentHost.Stop(); Emit(BuildEvent(EvKind.Ended, null, 0, reasonEnd: "endplay")); }   // end-of-context
        }
        finally { _lock.Release(); }
    }

    async Task LocalPrevAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _projection.NoteLocalCommand();
            // Desktop semantics: >3 s into the track, "previous" restarts the current track instead of stepping back.
            if (_currentHost.PositionMs > 3000) { _log.Info("queue back → restart current (>3s in)"); _currentHost.Seek(0, SeekMode.Accurate); return; }
            if (_session.Prev() is { } snap)
            {
                _snap = snap;
                _log.Info($"queue back → {_snap.Current?.Track.Uri ?? "(none)"}");
                await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
                return;
            }
            // No history and no prior context row (a restored session, §2): into the track → restart; already at 0 → a
            // true no-op (the derived CanSkipPrev keeps the button disabled in exactly this state).
            if (_currentHost.PositionMs > 0) { _log.Info("queue back → restart current (no history)"); _currentHost.Seek(0, SeekMode.Accurate); return; }
            _log.Info("queue back ignored — no history and at position 0");
        }
        finally { _lock.Release(); }
    }

    // Ghost resume: the play button while nothing is loaded locally → seed context/track/position + the next-up queue
    // from the cluster snapshot, then play locally through the ONE LoadAndPlayCurrentAsync pipeline (fix §4 — fast-start,
    // continuation prefetch, prepared-next; the old audio-only duplicate that always Play()ed and inverted the episode
    // rule is gone). Empty cluster → the persisted local session is the fallback, restored PAUSED (§8). Caller holds _lock.
    async Task GhostResumeAsync(CancellationToken ct)
    {
        if (RejectLocalPlay()) return;   // local audio unsupported → toast + abort (covers cold resume / self-transfer / bare inbound transfer)
        var track = _projection.CurrentTrack;
        // …and a cluster current nothing here can play is the same as no cluster current at all (see the recovery path:
        // it is our own ConnectUriMask display shape echoed back). Fall through to the persisted local session.
        if (track is null || !PlayableHere(track.Uri))
        {
            if (!await TryRestoreFromSnapshotAsync(ct).ConfigureAwait(false))
                _log.Info("ghost resume: nothing in the cluster to resume");
            return;
        }
        var ctxUri = _projection.ContextUri ?? track.Uri;
        track = await ReHydrateThroughOwnerAsync(track, ct).ConfigureAwait(false);   // a module playable is re-asked, not re-used
        long generation = SeedSessionFromCluster(track, ctxUri);
        _restorePendingLoad = false;   // this path loads immediately
        MintCommand("playbtn");
        // Cluster position wins when > 0; EpisodeResumeMicros runs only when it is 0 (fix §5 — the resume seek inside
        // LoadAndPlayCurrentAsync already encodes exactly that rule).
        long pos = _projection.PositionMs;
        await LoadAndPlayCurrentAsync(EvKind.Started, ct, pos > 0 ? pos : -1, skipOnUnplayable: true,
            expectedGeneration: generation).ConfigureAwait(false);
        ScheduleQueueHeal(_session.ContextUri ?? ctxUri, generation);
    }

    // Full session recovery from the last cluster (§8, F9): replay the raw cluster rows through ReplaceFromCluster so the
    // user queue is filed into _userQueue IN WIRE ORDER (drain-first preserved), the context continuation + autoplay tail
    // land in Upcoming (AutoplayContextUri set), and prev_tracks restore History — NOT SetContext over _projection.Queue,
    // which relabels queue rows as context, drops drain-first + the autoplay context, and (when we're the active device)
    // reads an empty windowed queue. Falls back to a single-track context when no cluster has been folded. The cluster
    // carries no next_page_url, so the trackers reset here and the background heal (ScheduleQueueHeal) restores paging /
    // the station continuation from a real context resolve (§7). Caller holds _lock; returns the context generation the
    // heal must still match to apply.
    long SeedSessionFromCluster(Track current, string ctxUri)
    {
        long generation = Interlocked.Increment(ref _contextGeneration);
        if (_projection.LastCluster is { HasTrack: true } c)
            _snap = _session.ReplaceFromCluster(c, current);
        else
            _snap = _session.SetContext(ctxUri, new[] { new QueuedTrack(current, "") }, 0);
        _nextPageUrl = null;   // no page url on the cluster — the heal refills it (§7); without it stations died at the window edge
        _contextIsInfinite = ContextResolve.IsInfinite(_session.ContextUri ?? ctxUri);
        _autoplayLatchedFor = null;   // §7 — a RESTORED autoplay tail is not a new fetch; the latch arms on the next real fetch
        _continuationFetch = null;
        _unplayableSkippedUri = null;
        _restoreAudioFirst = true;    // §8 — a restore is audio-first; video restores placement only
        _projection.SetContextMetadata(null);
        DiagnoseQueue("controller.recover-from-cluster");
        return generation;
    }

    // ── launch SessionRecovery (findings §1) ─────────────────────────────────────────────────────────────────────────
    // Trigger: any projection change (a cluster fold fires one) while NO local session exists and either nobody is
    // active or the cluster still (stale) names US active. Another device active → we stay a viewer, unchanged.
    void MaybeScheduleSessionRecovery(IPlaybackState s)
    {
        if (Volatile.Read(ref _recoveryState) != 0) return;
        var aid = s.ActiveDeviceId ?? "";
        if (!string.IsNullOrEmpty(aid) && aid != _ourDeviceId) return;   // viewer — the cluster owns the session
        if (_projection.LastCluster is null) return;                     // nothing folded yet (nothing to recover from)
        if (_snap.Current is not null) return;                           // a local session already exists
        // §7 — an explicit play intent already minted the newest generation before this fold arrived: recovery seeding
        // a stale cluster snapshot while that intent is still resolving would win the race to _snap.Current and get
        // silently clobbered a moment later, but not before it briefly announced Paused. Recovery is stale by
        // definition whenever an explicit intent is still the newest generation.
        long explicitGen = Volatile.Read(ref _explicitPlayGeneration);
        if (explicitGen != 0 && explicitGen == Volatile.Read(ref _contextGeneration)) return;
        if (Interlocked.CompareExchange(ref _recoveryState, 1, 0) != 0) return;
        _ = RunSessionRecoveryAsync();
    }

    async Task RunSessionRecoveryAsync()
    {
        bool seeded = false;
        try
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session.Current is not null) { seeded = true; return; }   // something started while we scheduled
                // Re-check §7 under the lock: an explicit play could have won the mint race between the pre-check in
                // MaybeScheduleSessionRecovery and this task actually acquiring _lock.
                long explicitGen = Volatile.Read(ref _explicitPlayGeneration);
                if (explicitGen != 0 && explicitGen == Volatile.Read(ref _contextGeneration)) return;
                var aid = _projection.ActiveDeviceId;
                if (!string.IsNullOrEmpty(aid) && aid != _ourDeviceId) return;   // became a viewer meanwhile
                if (_projection.LastCluster is { HasTrack: true } && _projection.CurrentTrack is { } track
                    && PlayableHere(track.Uri))
                {
                    bool weAreStaleActive = aid == _ourDeviceId;
                    var ctxUri = _projection.ContextUri ?? track.Uri;
                    // The cluster row for a uri no catalogue owns is OUR OWN publish from a previous session — a title
                    // and a length we wrote down once. Re-ask the owner before it becomes the restored now-playing.
                    track = await ReHydrateThroughOwnerAsync(track, default).ConfigureAwait(false);
                    long generation = SeedSessionFromCluster(track, ctxUri);
                    _restorePendingLoad = true;   // seeded, NOT loaded — the first Resume fast-starts (§1)
                    long pos = _projection.PositionMs;   // already extrapolated at the cluster fold
                    // The stale "us playing" echo must not flip the just-published Paused back for the next heartbeats.
                    if (weAreStaleActive) _projection.NoteLocalCommand();
                    // Publish PAUSED to the viewer ONLY (ApplyLocalSnapshot, never the Publish fan-out): recovery must not
                    // announce on the wire — the PutState fan-out happens when the user actually presses Play.
                    _projection.ApplyLocalSnapshot(_snap, new PlaybackEvent(EvKind.Paused, track, pos));
                    _log.Event(WaveeLogLevel.Info, "queue.recovery.seeded", "session recovered from cluster (paused)",
                        fields:
                        [
                            WaveeLogField.Of("ctx", WaveeLogRedaction.HashLike(ctxUri)),
                            WaveeLogField.Of("current", WaveeLogRedaction.HashLike(track.Uri)),
                            WaveeLogField.Of("positionMs", pos),
                            WaveeLogField.Of("staleActive", weAreStaleActive),
                        ]);
                    ScheduleQueueHeal(_session.ContextUri ?? ctxUri, generation);
                    seeded = true;
                }
                else
                {
                    // Either the cluster is empty at launch, or its current row is not something this machine can play —
                    // which is the normal shape after a non-Spotify session, because what the cluster holds is OUR OWN
                    // publish AFTER ConnectUriMask rewrote the row into `spotify:local:…` for remote controllers. That
                    // shape is a display uri, not a playable: seeding it asked Spotify to resolve a track that does not
                    // exist and put an error toast on screen at launch. It is skipped at INFO (never an error), and the
                    // persisted local session — which still holds the REAL playable — is the fallback (§8), restored
                    // PAUSED, never autoplayed.
                    if (_projection.LastCluster is { HasTrack: true } && _projection.CurrentTrack is { } unplayable)
                        _log.Event(WaveeLogLevel.Info, "queue.recovery.skipped",
                            "cluster current is not playable here; not seeding it",
                            fields:
                            [
                                WaveeLogField.Of("current", WaveeLogRedaction.HashLike(unplayable.Uri)),
                                WaveeLogField.Of("ctx", WaveeLogRedaction.HashLike(_projection.ContextUri ?? "")),
                            ]);
                    seeded = await TryRestoreFromSnapshotAsync(default).ConfigureAwait(false);
                }
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info("session recovery failed: " + ex.Message); }
        finally { Volatile.Write(ref _recoveryState, seeded ? 2 : 0); }   // nothing seeded → re-arm for a later fold
    }

    // §8 — the empty-cluster fallback: rebuild the session from the persisted snapshot and LOAD IT PAUSED (never
    // autoplay-on-launch; audio-only). Reconciliation on consume is the §6 ladder. Caller holds _lock.
    async Task<bool> TryRestoreFromSnapshotAsync(CancellationToken ct)
    {
        var snap = RestoreSnapshot?.Invoke();
        if (snap is null || string.IsNullOrEmpty(snap.CurrentUri)) return false;
        // A snapshot written while a masked cluster echo owned the projection holds that display uri too. Nothing here
        // can play it, so restoring it would only reproduce the launch-time error toast — skip at INFO and stay cold.
        if (!PlayableHere(snap.CurrentUri))
        {
            _log.Event(WaveeLogLevel.Info, "queue.recovery.snapshot-skipped",
                "persisted current is not playable here; not restoring it",
                fields: [WaveeLogField.Of("current", WaveeLogRedaction.HashLike(snap.CurrentUri))]);
            return false;
        }
        long generation = Interlocked.Increment(ref _contextGeneration);
        string ctxUri = string.IsNullOrEmpty(snap.ContextUri) ? snap.CurrentUri : snap.ContextUri;
        ResolvedContext resolved = ResolvedContext.Empty;
        try
        {
            resolved = await _contexts.ResolveAsync(new ContextSpec(ctxUri, null, null,
                snap.CurrentUri, string.IsNullOrEmpty(snap.CurrentUid) ? null : snap.CurrentUid,
                snap.CurrentIndex >= 0 ? snap.CurrentIndex : null), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Info("snapshot restore: context resolve failed (offline?): " + ex.Message); }
        if (generation != Volatile.Read(ref _contextGeneration)) return false;

        // §6 ladder — uid → uri → saved index → context head (launch recovery opts into "play something paused").
        int idx = ContextResolve.ResolveRestoreIndex(resolved.Tracks, snap.CurrentUri, snap.CurrentUid,
            snap.CurrentIndex, allowContextHead: true);
        QueuedTrack current;
        int missCursor = -1;
        if (idx >= 0)
        {
            current = resolved.Tracks[idx];
            if (!string.IsNullOrEmpty(snap.CurrentUid) && string.IsNullOrEmpty(current.Uid))
                current = current with { Uid = snap.CurrentUid };
        }
        else
        {
            current = await HydrateOneAsync(snap.CurrentUri, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(snap.CurrentUid)) current = current with { Uid = snap.CurrentUid };
            if (snap.CurrentIndex > 0) missCursor = snap.CurrentIndex - 1;   // §6.5 — Next() = the successor, not context[0]
        }

        _snap = _session.SetTransferredContext(ctxUri, resolved.Tracks, current, clearUserQueue: true, missCursor);
        if (snap.UserQueue.Count > 0)
        {
            var queued = await _contexts.HydrateAsync(snap.UserQueue, ct).ConfigureAwait(false);
            queued = await ReHydrateQueueThroughOwnersAsync(queued, ct).ConfigureAwait(false);
            if (queued.Count > 0) _snap = _session.EnqueueUser(queued);
        }
        _snap = _session.SetShuffle(snap.Shuffle);
        _snap = _session.SetRepeat(snap.Repeat);
        _nextPageUrl = string.IsNullOrEmpty(resolved.NextPageUrl) ? null : resolved.NextPageUrl;
        _contextIsInfinite = resolved.IsInfinite || ContextResolve.IsInfinite(ctxUri);
        _autoplayLatchedFor = null;
        _continuationFetch = null;
        _unplayableSkippedUri = null;
        _restoreAudioFirst = true;   // audio-only restore; video restores placement, never live playback (§8)
        _restorePendingLoad = false; // this path loads (paused) right away
        _projection.SetContextMetadata(resolved.Metadata);
        MintCommand("appload");
        _log.Event(WaveeLogLevel.Info, "queue.recovery.snapshot", "session restored from the local snapshot (paused)",
            fields:
            [
                WaveeLogField.Of("ctx", WaveeLogRedaction.HashLike(ctxUri)),
                WaveeLogField.Of("current", WaveeLogRedaction.HashLike(snap.CurrentUri)),
                WaveeLogField.Of("positionMs", snap.PositionMs),
                WaveeLogField.Of("resolvedTracks", resolved.Count),
                WaveeLogField.Of("matched", idx >= 0),
            ]);
        _quietRestorePublish = true;   // launch must not claim the cluster's active slot — the first real Play() announces (§8)
        await LoadAndPlayCurrentAsync(EvKind.Paused, ct, snap.PositionMs > 0 ? snap.PositionMs : -1,
            initiallyPaused: true, skipOnUnplayable: true, expectedGeneration: generation).ConfigureAwait(false);
        return true;
    }

    // §1.3 / §7 — the background heal: re-resolve the seeded context, match the current by the §6 ladder (uid → uri; a
    // heal has no saved index and never takes the head), extend Upcoming with the full resolve, and restore _nextPageUrl /
    // the station continuation. On an identity miss the cluster rows stay (they ARE the live session) — but an infinite
    // context still takes the continuation url so a station keeps paging instead of dying at the window edge.
    void ScheduleQueueHeal(string? contextUri, long generation)
    {
        if (string.IsNullOrEmpty(contextUri)) return;
        // The heal IS a context-resolve. A context Spotify cannot resolve has nothing to heal from (and asking cost a 400
        // + a warning at every launch of a module session) — the seeded rows are the whole session.
        if (!ContextResolve.IsSpotifyContext(contextUri)) return;
        _ = HealQueueFromContextAsync(contextUri!, generation);
    }

    async Task HealQueueFromContextAsync(string contextUri, long generation)
    {
        ResolvedContext resolved;
        try { resolved = await _contexts.ResolveAsync(ContextSpec.ForUri(contextUri), default).ConfigureAwait(false); }
        catch (Exception ex)
        {
            PlaybackBucketDiagnostics.Continuation("queue.recovery.heal-error", "recovery heal resolve failed",
                WaveeLogField.Of("ctx", contextUri),
                WaveeLogField.Of("error", ex.GetType().Name),
                WaveeLogField.Of("detail", ex.Message));
            return;
        }
        if (resolved.Count == 0 && string.IsNullOrEmpty(resolved.NextPageUrl)) return;   // offline / empty resolver — keep cluster rows

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _contextGeneration)) return;   // superseded by a real play/transfer
            if (_session.Current is not { } cur) return;
            if (!string.Equals(_session.ContextUri, contextUri, StringComparison.Ordinal)) return;

            int match = ContextResolve.FindStartIndex(resolved.Tracks, cur.Uri, _snap.Current?.Uid);
            if (match < 0)
            {
                // Keep the cluster rows — they are the live session. An infinite context still takes the continuation so
                // the station pages on (§7); a finite one keeps its window and autoplay-prefetches as usual.
                bool infinite = resolved.IsInfinite || ContextResolve.IsInfinite(contextUri);
                if (infinite && !string.IsNullOrEmpty(resolved.NextPageUrl)) _nextPageUrl = resolved.NextPageUrl;
                _log.Event(WaveeLogLevel.Info, "queue.recovery.heal-miss",
                    "restored current not found in the re-resolved context; keeping the cluster rows",
                    fields:
                    [
                        WaveeLogField.Of("ctx", WaveeLogRedaction.HashLike(contextUri)),
                        WaveeLogField.Of("current", WaveeLogRedaction.HashLike(cur.Uri)),
                        WaveeLogField.Of("resolvedTracks", resolved.Count),
                        WaveeLogField.Of("keptNextPage", infinite && !string.IsNullOrEmpty(resolved.NextPageUrl)),
                    ]);
                return;
            }

            // Keep the cluster's autoplay tail rows across the context replace (§7 — they are the live session's tail).
            List<QueuedTrack>? autoplayRows = null;
            foreach (var e in _snap.Upcoming)
                if (e.Provider == QueueProvider.Autoplay)
                    (autoplayRows ??= new List<QueuedTrack>()).Add(new QueuedTrack(e.Track, e.Uid, "autoplay", e.Metadata));
            string? autoplayCtx = _snap.AutoplayContextUri;

            _snap = _session.ReplaceContextPreservingCurrent(contextUri, resolved.Tracks);
            if (autoplayRows is { Count: > 0 })
                _snap = _session.AppendContextPage(autoplayRows, QueueProvider.Autoplay, autoplayCtx);
            _nextPageUrl = string.IsNullOrEmpty(resolved.NextPageUrl) ? null : resolved.NextPageUrl;
            _contextIsInfinite = resolved.IsInfinite || ContextResolve.IsInfinite(contextUri);
            if (resolved.Metadata is { Count: > 0 }) _projection.SetContextMetadata(resolved.Metadata);
            // Viewer-only refresh (no event, no wire fan-out) — a background heal is not a playback change.
            _projection.ApplyLocalSnapshot(_snap);
            PlaybackBucketDiagnostics.Continuation("queue.recovery.healed", "recovery heal extended the restored session",
                WaveeLogField.Of("ctx", contextUri),
                WaveeLogField.Of("resolvedTracks", resolved.Count),
                WaveeLogField.Of("cursor", match),
                WaveeLogField.Of("nextPage", _nextPageUrl ?? ""),
                WaveeLogField.Of("autoplayKept", autoplayRows?.Count ?? 0));
        }
        finally { _lock.Release(); }
    }

    async Task MaybeSeekEpisodeResumeAsync(Track track, CancellationToken ct)
    {
        if (EntityUri.KindOf(track.Uri) != EntityKind.Episode || EpisodeResumeMicros is not { } fn)
            return;
        try
        {
            long micros = await fn(track.Uri, ct).ConfigureAwait(false);
            if (micros > 0) _currentHost.Seek(micros / 1000, SeekMode.Accurate);
        }
        catch (Exception ex) { _log.Info("episode resume lookup failed: " + ex.Message); }
    }

    void MintCommand(string reasonStart = "clickrow")
    {
        _commandIdHex = PlaybackIds.MintCommandId();
        _reasonStart = reasonStart;
    }

    void ClearRemotePlaybackIds()
    {
        _remoteSessionId = "";
        _remoteInteractionId = "";
        _remotePageInstanceId = "";
        _connectOriginatedPlayback = false;
        _restoreAudioFirst = false;   // an explicit local media intent ends the restore's audio-first rule too
        _videoRecoveryUri = null;
        _videoAudioFallbackUri = null;
        _failureCheckpoint = null;
    }

    PlaybackIds MintPlaybackIds(Track track, byte[]? mediaId = null)
    {
        var ctx = _session.ContextUri;
        if (ctx != _idsSessionContext)
        {
            _idsSessionContext = ctx;
        }
        return PlaybackIds.Mint(_commandIdHex, mediaId,
            string.IsNullOrEmpty(_remoteSessionId) ? null : _remoteSessionId,
            string.IsNullOrEmpty(_remoteInteractionId) ? null : _remoteInteractionId,
            string.IsNullOrEmpty(_remotePageInstanceId) ? null : _remotePageInstanceId);
    }

    PlaybackEvent BuildEvent(EvKind kind, Track? track, long atMs, byte[]? mediaId = null,
        int bitrateKbps = 0, string audioFormat = "", long durationMs = 0, byte[]? fileId = null, string reasonEnd = "",
        long seekToMs = -1)
    {
        // F6: read the provider straight off the atomic snapshot's current (the dead ternary + row scan are gone).
        var provider = (_snap.Current?.Provider ?? QueueProvider.Context).ToWire();
        return new PlaybackEvent(kind, track, atMs, _currentIds, _reasonStart, reasonEnd, ParseContextKind(_snap.ContextUri),
            mediaId, bitrateKbps, audioFormat, durationMs, fileId, provider, true, seekToMs);
    }

    /// <summary>Apply a seek locally. <see cref="SeekMode.Keyframe"/> is a SCRUB PREVIEW: it drives the media host and
    /// nothing else. The rest of this method — the optimistic local-command note, the <c>Seeked</c> PlaybackEvent that
    /// reaches the Connect cluster, and the prepared-next/continuation re-arm — is COMMIT-only behaviour; firing it for
    /// every throttled preview would spam the cluster and thrash prepared-next several times a second during one drag.</summary>
    void EmitSeeked(long targetMs, SeekMode mode)
    {
        if (mode == SeekMode.Keyframe) { _currentHost.Seek(targetMs, SeekMode.Keyframe); return; }
        _projection.NoteLocalCommand();
        long fromMs = _currentHost.PositionMs;
        _currentHost.Seek(targetMs, SeekMode.Accurate);
        Emit(BuildEvent(EvKind.Seeked, _snap.Current?.Track, fromMs, seekToMs: targetMs));
        // W2 (remaining-ms-keyed prepare): a seek that LANDS inside the ending-soon window re-arms prepared-next NOW —
        // the signature dedupe makes this free when the slot is already prepared for the unchanged (current, next) pair —
        // and gives a last-page continuation fetch its head start instead of waiting for track-end.
        if (PreparedNextPolicy.SeekRequiresRearm(_snap.Current?.Track.DurationMs ?? 0, targetMs, 0))
        {
            SchedulePreparedNext("seek-ending-soon");
            MaybeStartContinuationFetch();
        }
    }

    static string ParseContextKind(string? contextUri)
    {
        if (string.IsNullOrEmpty(contextUri)) return "playlist";
        var parts = contextUri.Split(':');
        return parts.Length >= 3 ? parts[1] : "playlist";
    }

    async Task LoadAndPlayCurrentAsync(
        EvKind kind,
        CancellationToken ct,
        long resumePositionMs = -1,
        bool initiallyPaused = false,
        bool skipOnUnplayable = false,
        long expectedGeneration = 0,
        // A remote play/transfer's player_options_override.modes.media — bypasses the ordinary ShouldPlayAsVideo /
        // Connect-audio-first resolution for exactly THIS load (a parameter, like resumePositionMs, never a sticky
        // flag: the next track this session advances to via a plain Next() resolves its kind normally).
        PlayableKind? mediaKindOverride = null)
    {
        if (RejectLocalPlay()) return;   // local audio unsupported → toast + abort (covers play / next / prev / enqueue-idle / inbound)
        var cur = _session.Current;
        if (cur is null) { _currentHost.Stop(); return; }
        _restorePendingLoad = false;   // any real load consumes the recovery seed's deferred-load latch
        _loadResumeTargetMs = Math.Max(0, resumePositionMs);   // 0 for a fresh start; the Ended guard only fires above the threshold

        // Takeover-vs-in-flight-load race (§5): capture the generation THIS load owns and re-check it after every await
        // below, bailing BEFORE the load actually touches the host. DeactivateIfActiveOwner bumps the counter the moment
        // a takeover tears the host down, so a resolve already in flight when that happens must not land afterwards and
        // Play()/LoadFastStart over a host another device now owns. Mirrors the guard LocalPlaySpecAsync and
        // HandleInboundTransferAsync already carry; a caller that pre-minted its own generation (the transfer path, the
        // restore paths) passes it down so this checks against the SAME value it already validated — everyone else gets
        // a fresh one minted right here.
        long generation = expectedGeneration != 0 ? expectedGeneration : Interlocked.Increment(ref _contextGeneration);

        // A DIFFERENT playable re-arms the video-recovery loop guard. The recovery's OWN reload keeps the same uri, so it
        // deliberately does NOT re-arm — that is what caps the fallback at one attempt per playable.
        if (_videoRecoveryUri is not null && !string.Equals(_videoRecoveryUri, cur.Uri, StringComparison.Ordinal))
            _videoRecoveryUri = null;
        if (_videoAudioFallbackUri is not null && !string.Equals(_videoAudioFallbackUri, cur.Uri, StringComparison.Ordinal))
            _videoAudioFallbackUri = null;

        byte[]? mediaId = null;
        byte[]? fileId = null;
        int bitrateKbps = 160;
        string audioFormat = "";
        long durationMs = cur.DurationMs;
        if (MetaResolver is { } metaFn)
        {
            try
            {
                if (await metaFn(cur, ct).ConfigureAwait(false) is { } meta)
                {
                    mediaId = meta.MediaId;
                    fileId = meta.FileId;
                    bitrateKbps = meta.BitrateKbps;
                    audioFormat = meta.AudioFormat;
                    durationMs = meta.DurationMs > 0 ? meta.DurationMs : durationMs;
                }
            }
            catch { }
        }
        if (generation != Volatile.Read(ref _contextGeneration)) return;   // a takeover/other play won the race while meta resolved
        if (string.IsNullOrEmpty(_commandIdHex)) MintCommand(kind == EvKind.TrackChanged ? "trackdone" : "playbtn");
        _currentIds = MintPlaybackIds(cur, mediaId);

        // Milestone B: point the ONE current-media host at the kind this playable needs (stopping the outgoing host first on
        // a real host boundary), then load kind-specifically. Audio/LocalFile keep the audio host + its fast-start path below.
        var mediaKind = SwitchCurrentMedia(cur, mediaKindOverride);
        if (mediaKind == PlayableKind.Video)
        {
            if (StartVideoLoadDetached(cur, kind, mediaId, bitrateKbps, audioFormat, durationMs, fileId,
                resumePositionMs, initiallyPaused, skipOnUnplayable, generation))
                return;
            // LoadCurrentVideoAsync is unwired (unit tests / audio-only build — ShouldPlayAsVideo is null too and we
            // never get here in practice). Fall back to AUDIO for this playable rather than leaving the user in
            // silence: re-point the host at audio and continue down the normal audio path below. The audio host was
            // stopped by the swap above, so this is a clean load — never two decoders. (The OTHER "no playable video"
            // case — the hook IS wired but resolves to nothing — is discovered OFF-LOCK inside RunVideoLoadAsync,
            // which re-enters _lock to run this exact same fallback.)
            _log.Info($"video playable but LoadCurrentVideoAsync is not wired — playing {cur.Uri} as audio");
            SwitchHost(_audioHost);
            _currentKind = MediaSwitchLogic.KindOf(false, cur.Origin == TrackOrigin.Local);
            NotifyVideoUnavailable(cur);   // the app still thinks this playable has a video → its surface would spin forever
        }

        await LoadAudioCurrentLockedAsync(kind, cur, ct, resumePositionMs, initiallyPaused, skipOnUnplayable, generation,
            mediaId, bitrateKbps, audioFormat, durationMs, fileId).ConfigureAwait(false);
    }

    // The AUDIO tail of LoadAndPlayCurrentAsync, extracted verbatim (A5 / Milestone C: video-smooth-switching-
    // implementation.md §3) so the video branch above can return before ever touching it. Fast-start when wired, else
    // the plain resolve — body byte-identical to what used to run inline. Caller holds _lock; every await below still
    // re-checks `generation` before touching the host, exactly as when this lived inline in LoadAndPlayCurrentAsync.
    async Task LoadAudioCurrentLockedAsync(EvKind kind, Track cur, CancellationToken ct, long resumePositionMs,
        bool initiallyPaused, bool skipOnUnplayable, long generation, byte[]? mediaId, int bitrateKbps,
        string audioFormat, long durationMs, byte[]? fileId)
    {
        if (_fast is not null)
        {
            // Instant-start: play the clear head immediately; the encrypted body (key + CDN) resolves in parallel and is
            // supplied to the host when ready — hiding key/derive latency behind the head's ~3 s of audio.
            FastStartPlan plan;
            try { plan = await _fast.ResolveFastAsync(cur, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { await HandleUnplayableCurrentAsync(ex, skipOnUnplayable, initiallyPaused, ct).ConfigureAwait(false); return; }
            if (generation != Volatile.Read(ref _contextGeneration)) return;   // a takeover/other play won the race while the fast-start plan resolved
            _audioHost.LoadFastStart(plan.Start);   // audio-specific loading (guarded: current kind is audio/local here)
            if (!initiallyPaused) _currentHost.Play();
            if (resumePositionMs > 0) _currentHost.Seek(resumePositionMs, SeekMode.Accurate);
            else await MaybeSeekEpisodeResumeAsync(cur, ct).ConfigureAwait(false);
            WarmUpcomingFastTrack("after-start");
            Emit(BuildEvent(kind, cur, Math.Max(0, resumePositionMs), mediaId, bitrateKbps, audioFormat, durationMs, fileId));
            SchedulePreparedNext("after-start");
            MaybeStartContinuationFetch();
            _ = SupplyBodyWhenReadyAsync(plan.Body, cur.Uri);
            return;
        }

        AudioStreamHandle handle;
        try { handle = await _resolver.ResolveAsync(cur, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { await HandleUnplayableCurrentAsync(ex, skipOnUnplayable, initiallyPaused, ct).ConfigureAwait(false); return; }   // no silent drop
        if (generation != Volatile.Read(ref _contextGeneration)) return;   // a takeover/other play won the race while the resolve was in flight
        _audioHost.Load(handle);   // audio-specific loading (only reached when the current kind is audio/local)
        if (!initiallyPaused) _currentHost.Play();
        if (resumePositionMs > 0) _currentHost.Seek(resumePositionMs, SeekMode.Accurate);
        else await MaybeSeekEpisodeResumeAsync(cur, ct).ConfigureAwait(false);
        WarmUpcomingFastTrack("after-start");
        Emit(BuildEvent(kind, cur, Math.Max(0, resumePositionMs), mediaId, bitrateKbps, audioFormat, durationMs, fileId));
        SchedulePreparedNext("after-start");
        MaybeStartContinuationFetch();
    }

    // §6.6 — a restored current that fails to resolve gets ONE skip to the next playable before the error surfaces; a
    // second failure (or nothing left to skip to) reports, so a fully dead window is one toast, never a loop. Non-restore
    // paths (skipOnUnplayable false) keep the report-immediately behavior. The paused-ness of the original load carries
    // over, so a paused launch restore whose current is dead never autoplays the successor. Caller holds _lock.
    async Task HandleUnplayableCurrentAsync(Exception ex, bool skipOnUnplayable, bool initiallyPaused, CancellationToken ct)
    {
        var cur = _session.Current;
        if (skipOnUnplayable && cur is not null
            && !string.Equals(_unplayableSkippedUri, cur.Uri, StringComparison.Ordinal)
            && _session.PreviewNext() is not null
            && _session.Next() is { } snap)
        {
            _unplayableSkippedUri = cur.Uri;
            _snap = snap;
            _log.Info($"restore: current unplayable ({ex.GetType().Name}: {ex.Message}); skipping to next → "
                + (snap.Current?.Track.Uri ?? "(none)"));
            await LoadAndPlayCurrentAsync(initiallyPaused ? EvKind.Paused : EvKind.TrackChanged, ct,
                initiallyPaused: initiallyPaused).ConfigureAwait(false);
            return;
        }
        // A PAUSED launch restore is not a play attempt — the user has asked for nothing yet, so a media that will not
        // open right now (an expired module locator, an offline body, a playable whose form is only known after an
        // out-of-process resolve) must not greet them with an error toast at startup. Keep the seeded session, re-arm the
        // deferred-load latch so the first Resume runs the load through the ONE fast-start path, and report honestly THEN.
        if (skipOnUnplayable && initiallyPaused)
        {
            _restorePendingLoad = true;
            _log.Info($"restore: current not loadable yet ({ex.GetType().Name}: {ex.Message}); staying seeded — the first Resume will load it");
            return;
        }
        ReportPlaybackError(ex);
    }

    // Video branch of LoadAndPlayCurrentAsync (A4/A5, Milestone C: video-smooth-switching-implementation.md §3): kicks
    // the video load OFF _lock so the resolve/open chain (up to 5 sequential RTTs today, A2) never blocks the ~40
    // transport verbs that would otherwise queue behind it (A5). Under _lock this ONLY emits the track-changed event +
    // re-arms continuation/prepared-next + spawns the detached RunVideoLoadAsync — it never resolves or opens a
    // source itself. THE HOST OWNS THE PLAYER: this controller never sees a MediaPlayer, and the mounted video
    // surface only PRESENTS the host's player — which is why a placement flip no longer rebuilds (and restarts) it.
    // Returns FALSE only when LoadCurrentVideoAsync is unwired (unit tests / audio-only build — ShouldPlayAsVideo is
    // null too and we never get here in practice), so the caller falls through to the existing SYNCHRONOUS audio
    // fallback; TRUE means the load is in flight (fire-and-forget) and the caller must return without touching the
    // host again — the resolve/open must NEVER run while _lock is held. Caller holds _lock.
    bool StartVideoLoadDetached(Track cur, EvKind kind, byte[]? mediaId, int bitrateKbps, string audioFormat,
        long durationMs, byte[]? fileId, long resumePositionMs, bool initiallyPaused, bool skipOnUnplayable, long generation)
    {
        if (LoadCurrentVideoAsync is not { } loadVideo) return false;
        // Publish the track change even while the video source is still resolving/opening — the projection is
        // source-agnostic (position comes from the host clock; duration from Track.DurationMs), and the surface stays
        // mounted showing the previous frame + a loading overlay rather than unmounting (S in the app plan). Prepared-
        // next / crossfade are skipped across a video boundary (MediaSwitchLogic.AllowCrossfade is false for any video
        // pair) — SchedulePreparedNext still runs so it CANCELS any prior audio-prepared token and, when the NEXT
        // playable will itself be video, fires PrefetchVideo (A4) ahead of the boundary.
        Emit(BuildEvent(kind, cur, Math.Max(0, resumePositionMs), mediaId, bitrateKbps, audioFormat, durationMs, fileId));
        MaybeStartContinuationFetch();
        SchedulePreparedNext("video-start");
        var loadTask = RunVideoLoadAsync(loadVideo, cur, kind, mediaId, bitrateKbps, audioFormat, durationMs, fileId,
            resumePositionMs, initiallyPaused, skipOnUnplayable, generation);
        VideoLoadSpawnedForTest?.Invoke(loadTask);   // test seam only (internal, null in production) — see its doc comment
        return true;
    }

    // The OFF-LOCK continuation of StartVideoLoadDetached: awaits the resolve/open hook WITHOUT holding _lock, so
    // transport verbs never queue behind it (A5). _contextGeneration-fences at every re-entry — mirroring the existing
    // guards in LoadAndPlayCurrentAsync/LoadAudioCurrentLockedAsync — so a takeover/other load that ran while this was
    // in flight always wins outright; past a lost race this never touches _currentHost/_currentKind again. Caller does
    // NOT hold _lock; this re-acquires it ONLY for the no-source→audio fallback's state mutation (mirrors the same
    // re-enter-for-the-mutation-only shape as RecoverVideoAsync/FallbackVideoToAudioAsync below).
    async Task RunVideoLoadAsync(Func<Track, long, CancellationToken, Task<bool>> loadVideo, Track cur, EvKind kind,
        byte[]? mediaId, int bitrateKbps, string audioFormat, long durationMs, byte[]? fileId,
        long resumePositionMs, bool initiallyPaused, bool skipOnUnplayable, long generation)
    {
        bool ok;
        try
        {
            // CancellationToken.None: this continuation outlives the original caller (StartVideoLoadDetached has
            // already returned by the time the resolve/open finishes) — same discipline as
            // RecoverVideoAsync/FallbackVideoToAudioAsync, which detach from the caller's ct for the same reason.
            ok = await loadVideo(cur, resumePositionMs, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }   // fire-and-forget: nobody is awaiting this task — swallow, never fault it
        catch (Exception ex)
        {
            if (generation != Volatile.Read(ref _contextGeneration)) return;   // superseded — the error belongs to a load nobody is waiting on
            ReportPlaybackError(ex);   // handled (surfaced) — do not also start audio
            return;
        }

        if (generation != Volatile.Read(ref _contextGeneration)) return;   // a takeover/other play won the race while the video resolve/open was in flight

        if (!ok)
        {
            // No playable video source for this track (the account isn't served one, or the manifest resolve failed).
            // Fall back to AUDIO rather than leaving the user in silence: re-point the host at audio and run the exact
            // same audio path LoadAndPlayCurrentAsync would have. The resolve/open above already ran fully off-lock —
            // re-enter _lock here for the state mutation only.
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation != Volatile.Read(ref _contextGeneration)) return;   // superseded while queued for the lock
                if (!string.Equals(_session.Current?.Uri, cur.Uri, StringComparison.Ordinal)) return;   // a different playable loaded meanwhile
                _log.Info($"no playable video source for {cur.Uri} — falling back to audio for this playable");
                SwitchHost(_audioHost);
                _currentKind = MediaSwitchLogic.KindOf(false, cur.Origin == TrackOrigin.Local);
                NotifyVideoUnavailable(cur);   // the app still thinks this playable has a video → its surface would spin forever
                await LoadAudioCurrentLockedAsync(kind, cur, CancellationToken.None, resumePositionMs, initiallyPaused,
                    skipOnUnplayable, generation, mediaId, bitrateKbps, audioFormat, durationMs, fileId).ConfigureAwait(false);
            }
            finally { _lock.Release(); }
            return;
        }

        // NO follow-up Seek here: the resume position was handed to `loadVideo` above and is applied INSIDE the load, after
        // the session opens and before it plays. That ordering is the whole point — at an audio→video switch the video
        // player does not exist yet (the host serializes teardown→build→open through its load pump), so a seek issued at
        // this line would land on a null player and be dropped. That is exactly why every audio→video switch used to
        // restart at 0. Applying it in the load also means the first frame shown is already at the right place.
        // A retry checkpoint on the same video travels the same path (the host seeks the live session instead of rebuilding).
        if (!initiallyPaused) _currentHost.Play();
    }

    void MaybeStartContinuationFetch()
    {
        if (_session.Current is null)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.skip", "no current track");
            return;
        }
        if (_session.RemainingInContext > 5)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.skip", "context still has enough upcoming tracks",
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                WaveeLogField.Of("current", _session.Current.Uri));
            return;
        }
        if (_continuationFetch is { } existing && !existing.IsCompleted)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.skip", "continuation fetch already running",
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                WaveeLogField.Of("current", _session.Current.Uri));
            return;
        }
        if (_continuationFetch is { IsCompleted: true })
        {
            PlaybackBucketDiagnostics.Continuation("continuation.skip", "completed continuation fetch waiting for track-end consumer",
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                WaveeLogField.Of("current", _session.Current.Uri));
            return;
        }
        var fetch = StartContinuationFetch(forceAutoplay: false);
        if (fetch is not null) _ = EagerApplyContinuationAsync(fetch);
    }

    // Append the prefetched continuation (next context page / autoplay station) to the queue AS SOON AS it resolves —
    // not deferred to track-end — so the up-next list shows the upcoming tracks while the current one still plays (the
    // "Autoplaying similar music" preview). Append-only: the cursor doesn't move, so nothing changes what's playing.
    // ReferenceEquals-guarded so it never double-applies with the track-end TryContinueContextAsync path.
    async Task EagerApplyContinuationAsync(Task<ResolvedContext> fetch)
    {
        ResolvedContext result;
        try { result = await fetch.ConfigureAwait(false); }
        catch (Exception ex)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.eager-fault", "eager continuation fetch faulted",
                WaveeLogField.Of("error", ex.GetType().Name),
                WaveeLogField.Of("detail", ex.Message));
            return;   // a fault surfaces on the track-end path's own await instead
        }

        bool held = false;
        try
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            held = true;
            if (!ReferenceEquals(_continuationFetch, fetch))
            {
                PlaybackBucketDiagnostics.Continuation("continuation.eager-skip", "prefetch was already consumed or superseded",
                    WaveeLogField.Of("resultCount", result.Count));
                return;   // already consumed/superseded by track-end
            }
            _continuationFetch = null;
            PlaybackBucketDiagnostics.Continuation("continuation.eager-apply", "applying prefetched continuation before track-end",
                WaveeLogField.Of("resultCount", result.Count),
                WaveeLogField.Of("resultContext", result.ContextUri ?? ""),
                WaveeLogField.Of("nextPage", result.NextPageUrl ?? ""),
                WaveeLogField.Of("isInfinite", result.IsInfinite));
            ApplyContinuation(result);
        }
        catch (Exception ex)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.eager-error", "eager continuation apply failed",
                WaveeLogField.Of("error", ex.GetType().Name),
                WaveeLogField.Of("detail", ex.Message));
        }
        finally { if (held) _lock.Release(); }
    }

    Task<ResolvedContext>? StartContinuationFetch(bool forceAutoplay)
    {
        var ctx = _session.ContextUri;
        if (string.IsNullOrEmpty(ctx))
        {
            PlaybackBucketDiagnostics.Continuation("continuation.none", "no context uri; cannot fetch continuation");
            return null;
        }

        if (!forceAutoplay && !string.IsNullOrEmpty(_nextPageUrl))
        {
            var page = _nextPageUrl!;
            _log.Info("continuation: prefetching next context page " + page);
            PlaybackBucketDiagnostics.Continuation("continuation.fetch-page", "prefetching next context page",
                WaveeLogField.Of("ctx", ctx),
                WaveeLogField.Of("page", page),
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("current", _session.Current?.Uri ?? ""));
            return _continuationFetch = FetchNextPageAsync(page, _contextIsInfinite);
        }

        if (!CanAutoplay(ctx, ignoreLatch: forceAutoplay))
        {
            PlaybackBucketDiagnostics.Continuation("continuation.none", "autoplay not eligible",
                WaveeLogField.Of("ctx", ctx),
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("isInfinite", _contextIsInfinite || ContextResolve.IsInfinite(ctx)),
                WaveeLogField.Of("latchedFor", _autoplayLatchedFor ?? ""),
                WaveeLogField.Of("enabled", AutoplayEnabled?.Invoke() ?? true));
            return null;
        }
        _autoplayLatchedFor = ctx;
        var recent = _session.RecentUris(5);
        _log.Info("continuation: prefetching autoplay for " + ctx);
        PlaybackBucketDiagnostics.Continuation("continuation.fetch-autoplay", "prefetching autoplay",
            WaveeLogField.Of("ctx", ctx),
            WaveeLogField.Of("remainingContext", _session.RemainingInContext),
            WaveeLogField.Of("recent", string.Join(",", recent)),
            WaveeLogField.Of("current", _session.Current?.Uri ?? ""));
        return _continuationFetch = FetchAutoplayAsync(ctx, recent);
    }

    bool CanAutoplay(string contextUri, bool ignoreLatch = false)
    {
        // Autoplay is a SPOTIFY station. A context outside its catalog (a module link, a local folder) has none, so the
        // request is a guaranteed 400 — refuse before the round trip. Its end is simply the end of the session.
        if (!ContextResolve.IsSpotifyContext(contextUri)) return false;
        if (_contextIsInfinite || ContextResolve.IsInfinite(contextUri)) return false;
        if (!ignoreLatch && _autoplayLatchedFor == contextUri) return false;
        return AutoplayEnabled?.Invoke() ?? true;
    }

    async Task<ResolvedContext> FetchNextPageAsync(string nextPageUrl, bool isInfinite)
    {
        try
        {
            var page = await _contexts.LoadMoreAsync(nextPageUrl).ConfigureAwait(false);
            PlaybackBucketDiagnostics.Continuation("continuation.page-result", "next context page resolved",
                WaveeLogField.Of("count", page.Tracks.Count),
                WaveeLogField.Of("nextPage", page.NextPageUrl ?? ""),
                WaveeLogField.Of("isInfinite", isInfinite));
            return new ResolvedContext(page.Tracks, 0, null, page.NextPageUrl, isInfinite);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Info("continuation page fetch failed: " + ex.Message);
            PlaybackBucketDiagnostics.Continuation("continuation.page-error", "next context page fetch failed",
                WaveeLogField.Of("error", ex.GetType().Name),
                WaveeLogField.Of("detail", ex.Message));
            return ResolvedContext.Empty;
        }
    }

    async Task<ResolvedContext> FetchAutoplayAsync(string contextUri, IReadOnlyList<string> recent)
    {
        try
        {
            var result = await _contexts.ResolveAutoplayAsync(contextUri, recent).ConfigureAwait(false);
            if (result.Count == 0) _log.Info("autoplay returned no tracks for " + contextUri);
            PlaybackBucketDiagnostics.Continuation("continuation.autoplay-result", "autoplay resolved",
                WaveeLogField.Of("ctx", contextUri),
                WaveeLogField.Of("count", result.Count),
                WaveeLogField.Of("resultContext", result.ContextUri ?? ""),
                WaveeLogField.Of("nextPage", result.NextPageUrl ?? ""));
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Info("autoplay fetch failed for " + contextUri + ": " + ex.Message);
            PlaybackBucketDiagnostics.Continuation("continuation.autoplay-error", "autoplay fetch failed",
                WaveeLogField.Of("ctx", contextUri),
                WaveeLogField.Of("error", ex.GetType().Name),
                WaveeLogField.Of("detail", ex.Message));
            return ResolvedContext.Empty;
        }
    }

    bool ApplyContinuation(in ResolvedContext result)
    {
        _nextPageUrl = string.IsNullOrEmpty(result.NextPageUrl) ? null : result.NextPageUrl;
        _contextIsInfinite = _contextIsInfinite || result.IsInfinite;
        if (result.Count == 0)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.apply-empty", "continuation result had no tracks",
                WaveeLogField.Of("nextPage", _nextPageUrl ?? ""),
                WaveeLogField.Of("isInfinite", _contextIsInfinite));
            return false;
        }

        bool autoplay = result.Tracks[0].Provider == "autoplay";
        string? sourceContextUri = null;
        if (!string.IsNullOrEmpty(result.ContextUri) && result.ContextUri != _session.ContextUri)
        {
            _snap = _session.RelabelContext(result.ContextUri);
            _projection.SetContextMetadata(result.Metadata);
            sourceContextUri = result.ContextUri;
            autoplay = true;
        }

        if (!autoplay && _session.ContextUri is { } ctx && ContextResolve.IsInfinite(ctx)) autoplay = true;
        var prov = autoplay ? QueueProvider.Autoplay : QueueProvider.Context;
        _snap = _session.AppendContextPage(result.Tracks, prov, sourceContextUri ?? _session.ContextUri);
        EmitSnap(_snap, EvKind.QueueChanged);
        _log.Info("continuation: appended " + result.Count + " tracks"
            + (autoplay ? " (autoplay)" : "")
            + (_nextPageUrl is null ? "" : " with next page"));
        PlaybackBucketDiagnostics.Continuation("continuation.applied", "continuation appended to queue core",
            WaveeLogField.Of("count", result.Count),
            WaveeLogField.Of("provider", autoplay ? "autoplay" : "context"),
            WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
            WaveeLogField.Of("nextPage", _nextPageUrl ?? ""),
            WaveeLogField.Of("remainingContext", _session.RemainingInContext));
        return true;
    }

    async Task<bool> TryContinueContextAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var fetch = _continuationFetch ?? StartContinuationFetch(forceAutoplay: attempt > 0);
            if (fetch is null)
            {
                PlaybackBucketDiagnostics.Continuation("continuation.trackend-none", "no continuation available at track-end",
                    WaveeLogField.Of("attempt", attempt),
                    WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                    WaveeLogField.Of("remainingContext", _session.RemainingInContext));
                return false;
            }

            Task completed;
            try
            {
                completed = await Task.WhenAny(fetch, Task.Delay(TimeSpan.FromSeconds(3), ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            if (completed != fetch)
            {
                _log.Info("continuation: fetch exceeded 3s grace timeout");
                PlaybackBucketDiagnostics.Continuation("continuation.trackend-timeout", "fetch exceeded track-end grace timeout",
                    WaveeLogField.Of("attempt", attempt),
                    WaveeLogField.Of("ctx", _session.ContextUri ?? ""));
                return false;
            }

            ResolvedContext result;
            try { result = await fetch.ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Info("continuation: fetch faulted: " + ex.Message);
                PlaybackBucketDiagnostics.Continuation("continuation.trackend-fault", "fetch faulted at track-end",
                    WaveeLogField.Of("attempt", attempt),
                    WaveeLogField.Of("error", ex.GetType().Name),
                    WaveeLogField.Of("detail", ex.Message));
                result = ResolvedContext.Empty;
            }
            _continuationFetch = null;

            if (!ApplyContinuation(result))
            {
                if (attempt == 0 && string.IsNullOrEmpty(_nextPageUrl))
                {
                    PlaybackBucketDiagnostics.Continuation("continuation.trackend-retry", "first continuation was empty; retrying forced autoplay",
                        WaveeLogField.Of("attempt", attempt),
                        WaveeLogField.Of("ctx", _session.ContextUri ?? ""));
                    continue;
                }
                return false;
            }

            QueueEntry? next = null;
            if (_session.Next() is { } snap) { _snap = snap; next = snap.Current; }
            if (next is null)
            {
                PlaybackBucketDiagnostics.Continuation("continuation.trackend-no-next", "continuation appended but queue had no playable next track",
                    WaveeLogField.Of("ctx", _session.ContextUri ?? ""));
                return false;
            }
            PlaybackBucketDiagnostics.Continuation("continuation.trackend-next", "advancing into continuation track",
                WaveeLogField.Of("track", next.Track.Uri),
                WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                WaveeLogField.Of("remainingContext", _session.RemainingInContext));
            await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    // The ONE atomic publish (§5): the current snapshot + the event fold into the projection under a single lock / single
    // FireChanges (ApplyLocalSnapshot), then the same event fans out to the extra projections (the PutState publisher) — the
    // queue can never publish out of step with the track. Ownership is derived from the event kind, as before.
    void Publish(QueueSnapshot snap, in PlaybackEvent e)
    {
        _snap = snap;
        // §8 — a quiet restore (a launch/snapshot restore, or the ownership-regained auto-reload) still folds into the
        // LOCAL projection so the viewer sees the right paused row, but must not announce on the wire: nobody pressed
        // Play, so the cluster must hear nothing until they do. Single-use — cleared here regardless of this event's
        // kind, so a stray non-Paused event racing in right after the latch was armed (or the eventual real Resume,
        // which is EvKind.Started) can never inherit a suppression it was never meant to have.
        bool quiet = _quietRestorePublish;
        _quietRestorePublish = false;
        // Paused is deliberately excluded: a RESTORED-paused session (recovery seeding the slab paused, nothing
        // actually decoding yet) must not claim the active slot on its own — only a fold that actually started or
        // resumed audible playback may.
        if (e.Kind is EvKind.Started or EvKind.Resumed or EvKind.TrackChanged && e.Track is not null) SetActiveOwner(true);
        else if (e.Kind is EvKind.Ended or EvKind.BecameInactive) SetActiveOwner(false);
        DiagnoseQueue("controller.publish." + e.Kind);
        _projection.ApplyLocalSnapshot(snap, e);
        if (quiet && e.Kind == EvKind.Paused) return;   // local fold only — see above
        for (int i = 0; i < _extra.Count; i++) _extra[i].OnEvent(e);
    }

    void Emit(in PlaybackEvent e) => Publish(_snap, e);

    // The position to publish as fact: the host's own clock IFF it is honest right now, else the projection's own
    // carried-forward estimate. A stale/absent host clock reports 0 — publishing that 0 as the real position is exactly
    // the "BecameInactive AtMs=0" / resumed-at-0 lie this seam exists to foreclose.
    // EXCEPT across a track boundary — Started/TrackChanged, OR `track` naming something the projection doesn't hold
    // yet (a queue-mutation publish whose _snap already points at the new current before that track's own Started/
    // TrackChanged event has folded into the projection). At that instant the host clock is reliably still stale (the
    // load hasn't reached the host yet) and the projection's carried-forward estimate is still the OUTGOING track's
    // position — publishing it under the NEW track's identity is exactly the "phone inherits a stale position" defect
    // (a track that had played 0 ms, stamped with the previous song's position). LoadAndPlayCurrentAsync's own
    // terminal Emit always carries the real resume target (or 0) directly and never goes through this helper, so
    // nothing here has a resume target to honor — 0 is the only honest answer at a boundary.
    long PublishPositionMs(EvKind kind, Track? track)
    {
        bool boundary = kind is EvKind.Started or EvKind.TrackChanged
            || !string.Equals(track?.Uri, _projection.CurrentTrack?.Uri, StringComparison.Ordinal);
        return boundary ? 0 : (_currentHost.ClockValid ? _currentHost.PositionMs : _projection.PositionMs);
    }

    // Publish a session snapshot with a state event carrying its current track (queue mutations + options changes).
    void EmitSnap(QueueSnapshot snap, EvKind kind)
    {
        _snap = snap;
        Emit(BuildEvent(kind, snap.Current?.Track, PublishPositionMs(kind, snap.Current?.Track)));
        SchedulePreparedNext("session-changed");
    }

    // Emit a state event carrying the current track + position (drives the projection slab + the PutState publish). Reads
    // the current off the immutable _snap (never the live _session) so it is safe on the unlocked inbound paths (F7).
    void EmitState(EvKind kind) => Emit(BuildEvent(kind, _snap.Current?.Track, PublishPositionMs(kind, _snap.Current?.Track)));

    // The captured-position overload (DeactivateIfActiveOwner et al.): the caller already read the host's clock BEFORE
    // an imminent Stop() makes it stale, so the position travels in rather than being re-read here (too late).
    void EmitState(EvKind kind, long atMs) => Emit(BuildEvent(kind, _snap.Current?.Track, atMs));

    // queue.snapshot diagnostics for the current atomic snapshot (rev + itemId columns, §9). Dedup-guarded.
    void DiagnoseQueue(string reason)
    {
        var rows = new List<QueueEntry>(1 + _snap.UserQueue.Length + _snap.Upcoming.Length + _snap.History.Length);
        if (_snap.Current is { } c) rows.Add(c);
        rows.AddRange(_snap.UserQueue);
        rows.AddRange(_snap.Upcoming);
        rows.AddRange(_snap.History);
        PlaybackBucketDiagnostics.QueueIfChanged(ref _lastControllerQueueDiagSig, reason,
            rows, _snap.ContextUri, _snap.Current?.Track.Uri, _snap.Upcoming.Length, _snap.Revision);   // _snap only (no live-session read) → safe off-lock (F7)
    }

    void WarmUpcomingFastTrack(string reason)
    {
        if (_session.PeekNext() is { } next)
            WarmFastTrack(next, reason);
    }

    void WarmFastTrack(Track track, string reason)
    {
        if (_fast is not IFastTrackWarmer warmer) return;
        try { warmer.Warm(track, reason); }
        catch (Exception ex) { _log.Info($"fast-warm dispatch failed {track.Uri}: {ex.Message}"); }
    }

    // Fail-soft like KindFor: a throwing gate (it reads app state) falls back to the hard cut, never to preparing a
    // playable whose source could not be asked.
    bool MayPrepare(Track track)
    {
        if (CanPrepareNext is not { } gate) return true;
        try { return gate(track); }
        catch (Exception ex) { _log.Info($"can-prepare-next gate failed for {track.Uri}: {ex.Message}"); return false; }
    }

    void SchedulePreparedNext(string reason)
    {
        if (_preparedHost is null) return;

        var current = _snap.Current;
        // Milestone C (video smooth-switching, A4): the UPCOMING playable's kind is computed UNCONDITIONALLY (not
        // fenced on _currentKind) so a video-bound next boundary — video→video, or audio about to become video — still
        // gets its source warmed ahead of time via PrefetchVideo. This is a fire-and-forget notification only; the
        // controller itself does nothing else with a video upcoming.
        var previewEntry = current is null ? null : _session.PreviewNext();
        if (previewEntry is not null && KindFor(previewEntry.Track) == PlayableKind.Video)
        {
            try { PrefetchVideo?.Invoke(previewEntry.Track); }
            catch (Exception ex) { _log.Info($"video prefetch hook failed for {previewEntry.Track.Uri}: {ex.Message}"); }
        }
        // Milestone B: the prepared-next / crossfade path is an AUDIO-host capability. While a VIDEO is the current media
        // there is nothing to (audio-)prepare — fence the preview to null so any PRIOR prepared token is CANCELLED rather
        // than left dangling on a host that has been stopped (a swap back to audio re-schedules from LoadAndPlayCurrent).
        // This is a no-op on the unchanged audio path (_currentKind stays Audio when ShouldPlayAsVideo is unwired).
        // The decision rules (video boundaries, the prepare gate, overlap eligibility, the dedupe signature) live in the
        // PURE PreparedNextPolicy (W2) — this method only reads the live values and acts on the returned decision.
        var preview = _currentKind == PlayableKind.Video ? null : previewEntry;
        var decision = PreparedNextPolicy.Decide(_currentKind, current, preview,
            preview is null ? PlayableKind.Audio : KindFor(preview.Track),
            preview is not null && MayPrepare(preview.Track), _snap.Repeat);
        var next = decision.Prepare ? preview : null;
        bool allowOverlap = decision.AllowOverlap;
        string? signature = decision.Signature;

        CancellationTokenSource? priorCts;
        string? priorToken;
        string? token = null;
        CancellationTokenSource? cts = null;
        lock (_prepareGate)
        {
            if (signature is not null && string.Equals(signature, _preparedSignature, StringComparison.Ordinal)) return;
            priorCts = _prepareCts;
            priorToken = _preparedToken;
            _prepareCts = null;
            _preparedToken = null;
            _preparedSignature = null;
            _preparedItemId = QueueItemId.None;

            if (next is not null)
            {
                token = $"p{Interlocked.Increment(ref _prepareSequence):x}-{next.ItemId.Value:x}";
                cts = new CancellationTokenSource();
                _prepareCts = cts;
                _preparedToken = token;
                _preparedSignature = signature;
                _preparedItemId = next.ItemId;
            }
        }

        try { priorCts?.Cancel(); } catch { }
        priorCts?.Dispose();
        if (!string.IsNullOrEmpty(priorToken))
            _ = _preparedHost.CancelPreparedAsync(priorToken, CancellationToken.None);

        if (next is not null && token is not null && cts is not null)
        {
            _log.Info($"audio prepare scheduled token={token} item={next.ItemId.Value} track={next.Track.Uri} overlap={allowOverlap} reason={reason}");
            _ = ResolvePreparedNextAsync(next, token, allowOverlap, cts.Token);
        }
    }

    async Task ResolvePreparedNextAsync(QueueEntry next, string token, bool allowOverlap, CancellationToken ct)
    {
        try
        {
            AudioFastStart start;
            Task<AudioStreamHandle>? pendingBody = null;
            AudioStreamHandle resolvedBody = default;
            if (_fast is not null)
            {
                var plan = await _fast.ResolveFastAsync(next.Track, ct).ConfigureAwait(false);
                start = plan.Start;
                pendingBody = plan.Body;
            }
            else
            {
                resolvedBody = await _resolver.ResolveAsync(next.Track, ct).ConfigureAwait(false);
                start = new AudioFastStart(resolvedBody.TrackUri, resolvedBody.FileIdHex, resolvedBody.Format,
                    resolvedBody.DurationMs, resolvedBody.NormalizationGainDb, default);
            }

            if (!IsPreparedTokenCurrent(token)) return;
            await _preparedHost!.PrepareNextAsync(new AudioPrepareRequest(token, start, allowOverlap), ct).ConfigureAwait(false);
            if (!IsPreparedTokenCurrent(token))
            {
                await _preparedHost.CancelPreparedAsync(token, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var body = pendingBody is null ? resolvedBody : await pendingBody.ConfigureAwait(false);
            if (!IsPreparedTokenCurrent(token))
            {
                await _preparedHost.CancelPreparedAsync(token, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            await _preparedHost.SupplyNextBodyAsync(token, body, ct).ConfigureAwait(false);
            _log.Info($"audio prepare ready token={token} item={next.ItemId.Value} track={next.Track.Uri}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Info($"audio prepare failed token={token} item={next.ItemId.Value} track={next.Track.Uri}: {ex.GetType().Name}: {ex.Message}");
            ClearPreparedToken(token);
            try { await _preparedHost!.CancelPreparedAsync(token, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    bool IsPreparedTokenCurrent(string token)
    {
        lock (_prepareGate) return string.Equals(_preparedToken, token, StringComparison.Ordinal);
    }

    void ClearPreparedToken(string token)
    {
        CancellationTokenSource? cts = null;
        lock (_prepareGate)
        {
            if (!string.Equals(_preparedToken, token, StringComparison.Ordinal)) return;
            cts = _prepareCts;
            _prepareCts = null;
            _preparedToken = null;
            _preparedSignature = null;
            _preparedItemId = QueueItemId.None;
        }
        cts?.Dispose();
    }

    void OnAudioTransition(AudioTransitionSignal signal)
    {
        if (signal.Kind == AudioTransitionKind.Started)
            _ = CommitPreparedTransitionAsync(signal);
        else if (signal.Kind == AudioTransitionKind.Missed)
        {
            ClearPreparedToken(signal.Token);
            _log.Info($"audio transition missed token={signal.Token} track={signal.TrackUri} reason={signal.Reason ?? "unknown"}");
            // A miss while the current track is still playing (a recycled OOP host lost the prepared stream, or its
            // decoder wasn't prebuffered in time) leaves the upcoming hand-off unprepared. Re-resolve so a fresh prepare
            // is attempted for the same next item instead of silently degrading that one boundary to a hard cut. If the
            // track already ended, the Ended fallback advances first and this just previews the following item.
            SchedulePreparedNext("transition-missed");
        }
        else
            _log.Info($"audio transition completed token={signal.Token} track={signal.TrackUri} fade={signal.EffectiveFadeMs}ms");
    }

    async Task CommitPreparedTransitionAsync(AudioTransitionSignal signal)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            QueueItemId expectedItem;
            lock (_prepareGate)
            {
                if (!string.Equals(_preparedToken, signal.Token, StringComparison.Ordinal))
                {
                    _log.Info($"audio transition rejected stale token={signal.Token} track={signal.TrackUri}");
                    return;
                }
                expectedItem = _preparedItemId;
            }

            var preview = _session.PreviewNext();
            if (preview is null || preview.ItemId != expectedItem
                || !string.Equals(preview.Track.Uri, signal.TrackUri, StringComparison.Ordinal))
            {
                _log.Info($"audio transition identity mismatch token={signal.Token} expectedItem={expectedItem.Value} " +
                    $"previewItem={preview?.ItemId.Value ?? 0} hostTrack={signal.TrackUri} previewTrack={preview?.Track.Uri ?? "(none)"}; reloading current");
                ClearPreparedToken(signal.Token);
                await LoadAndPlayCurrentAsync(EvKind.TrackChanged, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var advanced = _session.Next();
            if (advanced?.Current is not { } current || current.ItemId != expectedItem)
            {
                _log.Info($"audio transition advance mismatch token={signal.Token} expectedItem={expectedItem.Value}");
                ClearPreparedToken(signal.Token);
                await LoadAndPlayCurrentAsync(EvKind.TrackChanged, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _snap = advanced;
            ClearPreparedToken(signal.Token);
            _projection.NoteLocalCommand();
            MintCommand("trackdone");
            _currentIds = MintPlaybackIds(current.Track);
            Emit(BuildEvent(EvKind.TrackChanged, current.Track, Math.Max(0, signal.PositionMs),
                durationMs: current.Track.DurationMs));
            WarmUpcomingFastTrack("after-handoff");
            SchedulePreparedNext("after-handoff");
            MaybeStartContinuationFetch();
        }
        catch (Exception ex)
        {
            _log.Info("audio transition commit failed: " + ex.Message);
        }
        finally { _lock.Release(); }
    }

    // A RESUMED load (nonzero target) whose Ended lands at/below this is behind where it started — no natural end
    // does that, so it can only be a failed reopen. A fresh (0-target) load ending near 0 is an ordinary short/empty
    // playable, not a failure — see _loadResumeTargetMs.
    const long ImmediateEndGuardMs = 1000;

    void OnHostSignal(AudioHostSignal s)
    {
        // §4 — a remote device genuinely owns playback: SubscribeHost guards only host IDENTITY/generation, so a host
        // StopHostKeepingSession tore down still has a live subscription and can still deliver a late signal (a
        // position tick already in flight, a Paused edge racing the takeover). Feeding that into the projection would
        // overwrite the cluster-derived position anchor with the stopped host's own 0 — the "UI reads 0:00 while a
        // phone plays" defect. Ended/Error still pass through unconditionally: AutoAdvance, the video-recovery ladder
        // and ReportPlaybackError all need them regardless of who currently owns playback. Closed HERE (not inside
        // NowPlayingProjection.OnHostSignal) so the projection stays a pure fold over whatever it is handed.
        if (s.Kind != AudioHostSignalKind.Ended && s.Kind != AudioHostSignalKind.Error && !RouteLocal() && !IsActiveOwner())
            return;
        _projection.OnHostSignal(s);
        if (s.Kind == AudioHostSignalKind.Ended)
        {
            if (_loadResumeTargetMs > ImmediateEndGuardMs && s.PositionMs <= ImmediateEndGuardMs)
            {
                _log.Info($"Ended at pos={s.PositionMs}, behind this load's own resume target ({_loadResumeTargetMs}) — treating as a failed reopen, not a real end (queue not advanced)");
                _failureCheckpoint = null;
                ReportPlaybackError(new AudioPlaybackException(AudioKeyFailureReason.None,
                    $"playback ended at pos={s.PositionMs}, behind the resume target {_loadResumeTargetMs} — the reopen failed silently"));
                return;
            }
            _failureCheckpoint = null;
            _ = AutoAdvanceAsync();
        }
        else if (s.Kind == AudioHostSignalKind.Error)
        {
            // A VIDEO that failed to open gets ONE recovery attempt before the error is reported (the local-override
            // fallback). Fire-and-forget, holding no lock — exactly like AutoAdvanceAsync above; taking _lock inside a
            // host-signal callback is what deadlocked track-end. The scheduled task reports the error itself when the
            // hook declines, so the non-video / unwired / already-attempted paths stay byte-identical to today.
            if (_currentKind == PlayableKind.Video && _session.Current is { } videoTrack)
            {
                bool connectOriginated = _connectOriginatedPlayback;
                if (TryRecoverVideoAsync is not null
                    && !string.Equals(_videoRecoveryUri, videoTrack.Uri, StringComparison.Ordinal))
                {
                    _videoRecoveryUri = videoTrack.Uri;
                    _ = RecoverVideoAsync(videoTrack, s, connectOriginated);
                }
                else if (connectOriginated)
                    _ = FallbackVideoToAudioAsync(videoTrack, s.PositionMs);
                else
                    ReportHostError(s);
                return;
            }
            ReportHostError(s);
        }
    }

    // Fail-soft: the hook reaches app code, and a throwing surface notification must never take playback down with it.
    void NotifyVideoUnavailable(Track track)
    {
        if (OnVideoMediaUnavailable is not { } notify) return;
        try { notify(track); }
        catch (Exception ex) { _log.Info($"video-unavailable notification failed for {track.Uri}: {ex.GetType().Name}: {ex.Message}"); }
    }

    void ReportHostError(in AudioHostSignal s)
    {
        // A VIDEO that errored out (and whose recovery hook declined) is the second way video "doesn't happen": the app's
        // surface is mounted on availability, which is still true, so it would keep waiting on a dead session.
        if (_currentKind == PlayableKind.Video && _session.Current is { } videoTrack) NotifyVideoUnavailable(videoTrack);
        var reason = s.FailureReason == AudioKeyFailureReason.None
            ? AudioKeyFailureReason.EmulationFault
            : s.FailureReason;
        if (reason == AudioKeyFailureReason.Network && _session.Current is { } current)
            _failureCheckpoint = new PlaybackFailureCheckpoint(current.Uri, Math.Max(0, s.PositionMs));
        ReportPlaybackError(new AudioPlaybackException(reason, s.Detail ?? "audio host playback error"));
    }

    // The scheduled half of the video-open recovery: ask the hook, and either re-run the load (the resolver now walks past
    // whatever failed) or report the original error. _lock is taken HERE, never in the signal callback.
    async Task RecoverVideoAsync(Track track, AudioHostSignal s, bool allowAudioFallback)
    {
        bool recovered = false;
        try { recovered = await TryRecoverVideoAsync!(track, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.Info($"video recovery hook failed for {track.Uri}: {ex.GetType().Name}: {ex.Message}"); }
        if (!recovered)
        {
            if (allowAudioFallback)
                await FallbackVideoToAudioAsync(track, s.PositionMs).ConfigureAwait(false);
            else
                ReportHostError(s);
            return;
        }
        _log.Info($"video open failed for {track.Uri} — recovered; reloading the playable");
        await _lock.WaitAsync().ConfigureAwait(false);
        try { await LoadAndPlayCurrentAsync(EvKind.Started, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.Info("video recovery reload failed: " + ex.Message); }
        finally { _lock.Release(); }
    }

    async Task FallbackVideoToAudioAsync(Track track, long positionMs)
    {
        _videoAudioFallbackUri = track.Uri;
        NotifyVideoUnavailable(track);
        _log.Info($"video failed for {track.Uri} — falling back to audio at {Math.Max(0, positionMs)}ms");
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!string.Equals(_session.Current?.Uri, track.Uri, StringComparison.Ordinal)) return;
            await LoadAndPlayCurrentAsync(
                EvKind.Started, CancellationToken.None, Math.Max(0, positionMs)).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.Info("video-to-audio fallback failed: " + ex.Message); }
        finally { _lock.Release(); }
    }

    async Task AutoAdvanceAsync() { try { await LocalNextAsync(default).ConfigureAwait(false); } catch (Exception ex) { _log.Info("auto-advance error: " + ex.Message); } }

    // ── forwarding (we are the controller of another device) — the real desktop envelope; ack_id parsed, not block-waited ─
    static Task Done => Task.CompletedTask;
    Task Local(Action a) { a(); return Done; }

    readonly record struct PlayRequest(
        string ContextUri, int StartIndex, IReadOnlyList<QueuedRef>? OrderedTracks,
        string? SkipTrackUri, string? SkipTrackUid,
        // bug 4: the media intent a forwarded play should carry, when known — Video iff the play originates from a
        // video surface or the selected/skipped track carries a live video intent. Null (unknown, e.g. a bare
        // context/track uri with nothing resolved yet) omits the override entirely, matching today's shape.
        PlayableKind? MediaKind = null)
    {
        public static PlayRequest Default(string contextUri, int startIndex, PlayableKind? mediaKind = null) =>
            new(contextUri, Math.Max(0, startIndex), null, null, null, mediaKind);
    }

    // THE ONE ATTRIBUTION POINT for "something asked us to play a context". Every play verb — local UI, an inbound
    // dealer play/transfer, a drop, an action — funnels through here, and until this line existed a play that nobody
    // pressed was INVISIBLE in the log: LocalPlaySpecAsync logs only failures, the context resolve is silent, and the
    // first evidence was a bare `head … fetch start` seconds later (the resolve's network latency). Info, one line per
    // play intent (not a hot path), carrying the ORIGIN so the next unexplained jump names its own caller.
    void LogPlayIntent(string origin, string contextUri, int startIndex, string? skipUri, string? skipUid, int orderedCount, bool local)
        => _log.Info($"play intent origin={origin} route={(local ? "local" : "forward")} ctx={contextUri} index={startIndex} " +
            $"skipUri={(string.IsNullOrEmpty(skipUri) ? "-" : skipUri)} skipUid={(string.IsNullOrEmpty(skipUid) ? "-" : skipUid)} ordered={orderedCount}");

    async Task ExecutePlayAsync(PlayRequest request, string origin, CancellationToken ct)
    {
        var target = _projection.ActiveDeviceId;
        bool local = RouteLocal(target);
        LogPlayIntent(origin, request.ContextUri, request.StartIndex, request.SkipTrackUri, request.SkipTrackUid,
            request.OrderedTracks?.Count ?? 0, local);
        if (!local) { await ForwardPlayAsync(request, target, ct).ConfigureAwait(false); return; }
        ClearRemotePlaybackIds();
        MintCommand("playbtn");
        // §7 — mint (and latch) the generation BEFORE the resolve: an explicit play intent is, by construction, the
        // newest thing that happened, so a launch/session recovery that is mid-resolve when the user presses Play must
        // never win the race and seed a stale cluster snapshot out from under it.
        long generation = Interlocked.Increment(ref _contextGeneration);
        Volatile.Write(ref _explicitPlayGeneration, generation);
        if (request.OrderedTracks is { Count: > 0 })
        {
            var spec = new ContextSpec(request.ContextUri, null, request.OrderedTracks,
                request.SkipTrackUri, request.SkipTrackUid, request.StartIndex);
            await LocalPlaySpecAsync(spec, ct, generation).ConfigureAwait(false);
            return;
        }
        await LocalPlaySpecAsync(new ContextSpec(
            request.ContextUri,
            null,
            null,
            request.SkipTrackUri,
            request.SkipTrackUid,
            request.StartIndex), ct, generation).ConfigureAwait(false);
    }

    // `target` is a SINGLE READ captured by the caller at the same moment it evaluated RouteLocal() — never re-read
    // here. An empty capture means a cluster fold landed between routing and sending and there is no one left to
    // send to; that is a real, user-visible failure (not a silent drop), so it logs AND fires OnRemoteCommandFailed
    // exactly like a rejected send does below.
    async Task Forward(string endpoint, string target, CancellationToken ct, params (string Key, object Value)[] args)
    {
        if (string.IsNullOrEmpty(target))
        {
            _log.Info($"outbound {endpoint} → (no target): dropped — the active-device id was empty at send time");
            OnRemoteCommandFailed?.Invoke();
            return;
        }
        if (_outbound is null) return;
        var json = OutboundEnvelope.Command(_ourDeviceId, endpoint, args, NewId(), NewId(), Now(), NewId());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) _log.Info($"outbound {endpoint} → {target}: failed ({r.Status})");
    }

    async Task ForwardPlayAsync(PlayRequest request, string target, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(target))
        {
            _log.Info("outbound play → (no target): dropped — the active-device id was empty at send time");
            OnRemoteCommandFailed?.Invoke();
            return;
        }
        if (_outbound is null) return;
        // Outbound carries the OPAQUE context uri — the TARGET resolves the tracks (the desktop full-envelope shape).
        int? skipIndex = request.OrderedTracks is { Count: > 0 } ? request.StartIndex
            : request.StartIndex > 0 ? request.StartIndex : null;
        string? skipUid = string.IsNullOrEmpty(request.SkipTrackUid) ? null : request.SkipTrackUid;
        var json = OutboundEnvelope.Play(_ourDeviceId, request.ContextUri, null,
            skipIndex, request.SkipTrackUri, skipUid, request.OrderedTracks,
            _session.Shuffle, FeatureOf(request.ContextUri), _featureVersion, NewId(), NewId(), Now(),
            preferVideo: request.MediaKind == PlayableKind.Video);
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) { _log.Info($"outbound play → {target}: failed ({r.Status})"); OnRemoteCommandFailed?.Invoke(); }
    }

    // add_to_queue: a single track as command.track {uri,uid,metadata} + options — NOT the flat command.uri Forward verb.
    async Task ForwardAddToQueueAsync(string trackUri, string target, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(target))
        {
            _log.Info("outbound add_to_queue → (no target): dropped — the active-device id was empty at send time");
            OnRemoteCommandFailed?.Invoke();
            return;
        }
        if (_outbound is null) return;
        var json = OutboundEnvelope.AddToQueue(_ourDeviceId, trackUri, "", false, false, false, NewId(), NewId(), Now(), NewId());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) _log.Info($"outbound add_to_queue → {target}: failed ({r.Status})");
    }

    // bug 5: the richer overload — a full Track is available (EnqueueAsync(Track)), so the remote row gets the SAME
    // display + video metadata a set_queue row gets, instead of an empty {} that erases everything including any
    // video association (an active downgrade, same shape as bug 1).
    async Task ForwardAddToQueueAsync(Track track, string target, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(target))
        {
            _log.Info("outbound add_to_queue → (no target): dropped — the active-device id was empty at send time");
            OnRemoteCommandFailed?.Invoke();
            return;
        }
        if (_outbound is null) return;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = track.Title ?? "",
            ["album_title"] = track.Album?.Name ?? "",
            ["album_uri"] = track.Album?.Uri ?? "",
            ["duration"] = track.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["image_url"] = track.Image?.Url ?? "",
            ["is_explicit"] = track.IsExplicit ? "true" : "false",
        };
        if (track.Artists.Count > 0)
        {
            var a = track.Artists[0];
            metadata["artist_name"] = a.Name ?? "";
            metadata["artist_uri"] = a.Uri ?? "";
            metadata["album_artist_name"] = a.Name ?? "";
        }
        var assoc = VideoPresence.Association(track.Uri);
        MediaSwitchLogic.StampVideoAssociation(metadata, assoc?.HasVideo == true, assoc?.CounterpartUri);
        var json = OutboundEnvelope.AddToQueue(_ourDeviceId, track.Uri, "", false, false, false, NewId(), NewId(), Now(), NewId(), metadata);
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) _log.Info($"outbound add_to_queue → {target}: failed ({r.Status})");
    }

    async Task<bool> TryForwardTransferAsync(string target, CancellationToken ct)
    {
        if (_outbound is null) { _log.Info($"transfer to {target} ignored - no outbound control"); return false; }
        var from = string.IsNullOrEmpty(_projection.ActiveDeviceId) ? _ourDeviceId : _projection.ActiveDeviceId;
        // bug 8: a hand-off away from a locally-hosted video must say so, or the target has no reason to keep video mode.
        bool hostingVideo = IsActiveOwner() && _currentKind == PlayableKind.Video;
        var r = await _outbound.TransferAsync(from, target, ct, hostingVideo).ConfigureAwait(false);
        if (r.Ok) { _log.Info($"connect transfer {from} -> {target}: ok ({r.Status})"); return true; }
        _log.Info($"connect transfer {from} -> {target}: failed ({r.Status})");
        OnRemoteCommandFailed?.Invoke();
        return false;
    }

    static string NewId() => Guid.NewGuid().ToString("N");
    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // The queue_revision to echo on an outbound set_queue — the last revision the cluster reported (held by the projection;
    // "" until the first cluster). It can exceed Int64, so parse as ulong; an unparseable/absent value sends 0 (best-effort).
    ulong ParseRevision() => ulong.TryParse(_projection.QueueRevision, out var r) ? r : 0UL;
    static QueuedRef[] ToQueuedRefs(IReadOnlyList<PlaybackContextTrack> tracks)
    {
        var refs = new QueuedRef[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) refs[i] = new QueuedRef(tracks[i].Uri, tracks[i].Uid ?? "", Metadata: tracks[i].Metadata);
        return refs;
    }

    // play_origin.feature_identifier — the source surface, derived from the context type (matches the desktop captures).
    static string FeatureOf(string uri) =>
        uri.Contains(":album:", StringComparison.Ordinal) ? "album"
        : uri.Contains(":artist", StringComparison.Ordinal) ? "artist"
        : uri.Contains(":playlist:", StringComparison.Ordinal) ? "playlist"
        : uri.Contains(":collection", StringComparison.Ordinal) ? "collection"
        : "harmony";

    // Parse the inbound play/transfer command payload into a ContextSpec (proto-free). The command payload is small (it
    // carries an opaque context uri + skip_to, NOT a track list — that's resolved server-side), so JsonDocument is fine
    // here; the LARGE context-resolve RESPONSE is streamed via Utf8JsonReader in LiveContextResolver. Returns null when
    // there's no context to play (a bare transfer → the caller ghost-resumes the cluster snapshot instead).
    readonly record struct PlayIntent(
        ContextSpec Context,
        string SessionId,
        bool InitiallyPaused,
        bool? Shuffle,
        string InteractionId,
        string PageInstanceId,
        // -1 = no seek carried (start at 0 / episode-resume, exactly as before this field existed). Otherwise the
        // wire's options.seek_to (or the legacy bare command.seek_to) — "play this podcast at 12:00" landing at 0 was
        // the defect this closes.
        long SeekToMs = -1,
        // player_options_override.{repeating_context,repeating_track} — null = not carried (leave repeat untouched),
        // matching options.shuffling_context's own three-state (unset/true/false) shape above.
        RepeatMode? Repeat = null,
        // player_options_override.modes.media ("VIDEO"/"AUDIO") — null = not carried, so the ordinary
        // ShouldPlayAsVideo / audio-first resolution decides exactly as it does today.
        PlayableKind? MediaKind = null);

    static PlayIntent? ExtractPlayIntent(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("command", out var c)) return null;

            string? uri = null, url = null;
            IReadOnlyDictionary<string, string>? contextMetadata = null;
            List<QueuedRef>? pages = null;
            if (c.TryGetProperty("context", out var ctx))
            {
                if (ctx.TryGetProperty("uri", out var u)) uri = u.GetString();
                if (ctx.TryGetProperty("url", out var ur)) url = ur.GetString();
                contextMetadata = TrackMetadata(ctx);
                if (ctx.TryGetProperty("pages", out var pg) && pg.ValueKind == JsonValueKind.Array)
                {
                    foreach (var page in pg.EnumerateArray())
                    {
                        if (!page.TryGetProperty("tracks", out var trks) || trks.ValueKind != JsonValueKind.Array) continue;
                        foreach (var t in trks.EnumerateArray())
                        {
                            string tu = t.TryGetProperty("uri", out var tuv) ? tuv.GetString() ?? "" : "";
                            if (tu.Length == 0) continue;
                            string tid = t.TryGetProperty("uid", out var tidv) ? tidv.GetString() ?? "" : "";
                            (pages ??= new List<QueuedRef>()).Add(new QueuedRef(tu, tid,
                                TrackProvider(t, "context"), TrackMetadata(t)));
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(uri) && c.TryGetProperty("context_uri", out var cu)) uri = cu.GetString();
            if (string.IsNullOrEmpty(uri)) return null;

            string? skUri = null, skUid = null; int? skIdx = null;
            if (TryGetSkipTo(c, out var skipTo))
            {
                if (skipTo.TryGetProperty("track_uri", out var su)) skUri = su.GetString();
                if (skipTo.TryGetProperty("track_uid", out var sd)) skUid = sd.GetString();
                if (skipTo.TryGetProperty("track_index", out var si) && si.TryGetInt32(out var sidx)) skIdx = sidx;
            }

            string sessionId = "";
            bool initiallyPaused = false;
            bool? shuffle = null;
            RepeatMode? repeat = null;
            PlayableKind? mediaKind = null;
            // The legacy/bare shape carries seek_to at the command root; the desktop envelope nests it under options
            // (checked below, and preferred when both are present).
            long seekToMs = c.TryGetProperty("seek_to", out var rootSeek) && rootSeek.TryGetInt64(out var rootSeekMs) ? rootSeekMs : -1;
            if (c.TryGetProperty("session_id", out var directSession) && directSession.ValueKind == JsonValueKind.String)
                sessionId = directSession.GetString() ?? "";
            if (c.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
            {
                if (string.IsNullOrEmpty(sessionId) && options.TryGetProperty("session_id", out var session)
                    && session.ValueKind == JsonValueKind.String)
                    sessionId = session.GetString() ?? "";
                if (options.TryGetProperty("initially_paused", out var paused)
                    && paused.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    initiallyPaused = paused.GetBoolean();
                if (options.TryGetProperty("seek_to", out var optSeek) && optSeek.TryGetInt64(out var optSeekMs))
                    seekToMs = optSeekMs;
                if (options.TryGetProperty("player_options_override", out var playerOptions)
                    && playerOptions.ValueKind == JsonValueKind.Object)
                {
                    if (playerOptions.TryGetProperty("shuffling_context", out var shuffling)
                        && shuffling.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        shuffle = shuffling.GetBoolean();
                    bool hasRepTrack = playerOptions.TryGetProperty("repeating_track", out var rt)
                        && rt.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    bool hasRepCtx = playerOptions.TryGetProperty("repeating_context", out var rc)
                        && rc.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    if (hasRepTrack || hasRepCtx)
                    {
                        bool repTrack = hasRepTrack && rt.GetBoolean();
                        bool repCtx = hasRepCtx && rc.GetBoolean();
                        repeat = repTrack ? RepeatMode.Track : repCtx ? RepeatMode.Context : RepeatMode.Off;
                    }
                    if (playerOptions.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Object
                        && modes.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.String)
                    {
                        var m = media.GetString();
                        if (string.Equals(m, "VIDEO", StringComparison.OrdinalIgnoreCase)) mediaKind = PlayableKind.Video;
                        else if (string.Equals(m, "AUDIO", StringComparison.OrdinalIgnoreCase)) mediaKind = PlayableKind.Audio;
                    }
                }
            }

            string interactionId = "", pageInstanceId = "";
            if (c.TryGetProperty("logging_params", out var logging) && logging.ValueKind == JsonValueKind.Object)
            {
                interactionId = FirstString(logging, "interaction_ids");
                pageInstanceId = FirstString(logging, "page_instance_ids");
            }
            return new PlayIntent(
                new ContextSpec(uri!, url, pages, skUri, skUid, skIdx, contextMetadata),
                sessionId, initiallyPaused, shuffle, interactionId, pageInstanceId,
                seekToMs, repeat, mediaKind);
        }
        catch { return null; }
    }

    static string FirstString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return "";
        foreach (var value in array.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        return "";
    }

    // skip_to lives under prepare_play_options.skip_to (desktop envelope) or options.skip_to (legacy/bare form).
    static bool TryGetSkipTo(JsonElement command, out JsonElement skipTo)
    {
        if (command.TryGetProperty("prepare_play_options", out var ppo) && ppo.TryGetProperty("skip_to", out skipTo)) return true;
        if (command.TryGetProperty("options", out var opt) && opt.TryGetProperty("skip_to", out skipTo)) return true;
        skipTo = default;
        return false;
    }

    // add_to_queue: a single track ref. Real desktop sends command.track {uri,uid}; our own/legacy outbound sends a flat
    // command.uri string — accept both.
    static QueuedRef? ParseQueueTrack(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("command", out var c)) return null;
            if (c.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object &&
                t.TryGetProperty("uri", out var tu) && tu.GetString() is { Length: > 0 } u)
                return new QueuedRef(u, t.TryGetProperty("uid", out var d) ? d.GetString() ?? "" : "",
                    TrackProvider(t, "queue"), TrackMetadata(t));
            if (c.TryGetProperty("uri", out var flat) && flat.ValueKind == JsonValueKind.String && flat.GetString() is { Length: > 0 } fu)
                return new QueuedRef(fu, "", "queue");
            return null;
        }
        catch { return null; }
    }

    // set_queue full reconcile (F8): parse ALL of command.{field} into wire entries, preserving EVERY row — queue rows,
    // context continuation, autoplay tail AND the delimiter / meta:page markers (the session classifies them by uri/kind).
    // IsQueued keys on provider:"queue" or metadata is_queued:"true" (a missing provider is treated as context).
    static IReadOnlyList<QueueWireEntry> ParseWireEntries(byte[] payload, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("command", out var c) ||
                !c.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<QueueWireEntry>();
            var list = new List<QueueWireEntry>(arr.GetArrayLength());
            foreach (var t in arr.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("uri", out var u) || u.GetString() is not { Length: > 0 } uri) continue;
                var meta = TrackMetadata(t);
                bool queued = string.Equals(TrackProvider(t, ""), "queue", StringComparison.Ordinal)
                    || (meta is not null && meta.TryGetValue("is_queued", out var iq) && iq == "true");
                list.Add(new QueueWireEntry(uri, t.TryGetProperty("uid", out var d) ? d.GetString() ?? "" : "", queued, meta,
                    TrackFromWireMetadata(uri, meta)));
            }
            return list;
        }
        catch { return Array.Empty<QueueWireEntry>(); }
    }

    // Every live set_queue row carries display metadata inline (title/artist_name/album_title/duration/image_*/
    // is_explicit — decoded from the dealer archive), which ParseWireEntries used to throw away, leaving
    // PlaybackSession.ApplySetQueue nothing but uri/uid to rebuild a row from (a bare-uri Synthetic() placeholder for
    // every track it hadn't already resolved). Mirrors ClusterMapper.MapTrack's field shape (image preference
    // xlarge→large→url, artist_uri/album_uri riding the metadata dict) so the set_queue and cluster paths agree on
    // what a wire track looks like. Null when the row is too thin to be worth it (no title) — the fallback stays
    // ContextResolve.Synthetic for those, exactly as before.
    static Track? TrackFromWireMetadata(string uri, IReadOnlyDictionary<string, string>? meta)
    {
        if (meta is null || !meta.TryGetValue("title", out var title) || string.IsNullOrEmpty(title)) return null;
        string Get(string k) => meta.TryGetValue(k, out var v) ? v : "";
        long duration = long.TryParse(Get("duration"), out var d) ? d : 0;
        string artistName = Get("artist_name"), artistUri = Get("artist_uri");
        var artists = string.IsNullOrEmpty(artistName) && string.IsNullOrEmpty(artistUri)
            ? Array.Empty<ArtistRef>()
            : new[] { new ArtistRef(EntityUri.IdOf(artistUri), artistUri, artistName) };
        string albumUri = Get("album_uri");
        var album = new AlbumRef(EntityUri.IdOf(albumUri), albumUri, Get("album_title"));
        string img = Get("image_xlarge_url");
        if (img.Length == 0) img = Get("image_large_url");
        if (img.Length == 0) img = Get("image_url");
        bool isExplicit = Get("is_explicit") == "true";
        return new Track(EntityUri.IdOf(uri), uri, title, artists, album, duration, isExplicit,
            img.Length == 0 ? null : new Image(img));
    }

    // command.queue_revision — a bare unsigned number that can exceed Int64; kept as a string (echoed on an outbound set_queue).
    static string ParseQueueRevisionString(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("command", out var c) && c.TryGetProperty("queue_revision", out var r))
                return r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : r.GetRawText();
            return "";
        }
        catch { return ""; }
    }

    static string TrackProvider(JsonElement track, string fallback) =>
        track.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    static IReadOnlyDictionary<string, string>? TrackMetadata(JsonElement track)
    {
        if (!track.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) return null;
        Dictionary<string, string>? result = null;
        foreach (var p in metadata.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            (result ??= new Dictionary<string, string>(StringComparer.Ordinal))[p.Name] = p.Value.GetString() ?? "";
        }
        return result;
    }

    static Track SyntheticTrack(string uri)
    {
        return new Track(EntityUri.IdOf(uri), uri, uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
    }

    public void Dispose()
    {
        CancellationTokenSource? prepareCts;
        string? preparedToken;
        lock (_prepareGate)
        {
            prepareCts = _prepareCts;
            preparedToken = _preparedToken;
            _prepareCts = null;
            _preparedToken = null;
        }
        try { prepareCts?.Cancel(); } catch { }
        prepareCts?.Dispose();
        if (_preparedHost is not null && preparedToken is not null)
            _ = _preparedHost.CancelPreparedAsync(preparedToken, CancellationToken.None);
        _remoteVolumeTx.Dispose();
        _transitionSub?.Dispose();
        _hostSub.Dispose();
        _projSub.Dispose();
        _lock.Dispose();
    }
}
