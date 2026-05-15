using System.Collections.Generic;
using Wavee.Audio;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Audio.Queue;
using Wavee.AudioIpc;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Events;
using Wavee.Core.Audio;

namespace Wavee.Audio;

/// <summary>
/// Central playback orchestration layer. Sits between ConnectCommandExecutor and AudioPipelineProxy.
/// Manages queue, resolves tracks, handles remote commands, auto-advances on track finish.
/// Implements IPlaybackEngine so it slots into the existing executor/state manager wiring.
/// </summary>
public sealed partial class PlaybackOrchestrator : IPlaybackEngine, IAsyncDisposable
{
    private readonly AudioPipelineProxy _proxy;

    private readonly TrackResolver _trackResolver;
    private readonly ContextResolver _contextResolver;
    private readonly PlaybackQueue _queue;
    private readonly ILogger? _logger;
    private readonly CompositeDisposable _subs = new();

    private readonly BehaviorSubject<LocalPlaybackState> _stateSubject = new(LocalPlaybackState.Empty);
    private readonly Subject<PlaybackError> _errorSubject = new();
    private readonly Subject<EndOfContextEvent> _endOfContextSubject = new();

    private bool _repeatContext;
    private bool _repeatTrack;
    private bool _disposed;

    // Context subtitle (artist name, playlist title) — set from PlayCommand at
    // play time, emitted on every subsequent state publish until the next PlayAsync.
    private string? _currentContextDescription;

    // Context artwork, kind, and size — likewise set from PlayCommand and re-emitted
    // with every state push so remote "Now Playing" cards stay consistent.
    private string? _currentContextImageUrl;
    private string? _currentContextFeature;
    private int? _currentContextTrackCount;
    private IReadOnlyDictionary<string, string>? _currentContextFormatAttributes;

    // Pagination state. _currentNextPageUrl is the hm://... URL to fetch the
    // next batch of tracks for the active context (artist discography, long
    // playlist, etc.). Set from ContextLoadResult on PlayAsync (or from a
    // background resolve in the UI-initiated path) and advanced each time
    // LoadMoreTracksAsync succeeds. Null once the context is fully loaded.
    private string? _currentNextPageUrl;
    private int _currentContextPageCount = 1;
    private readonly System.Threading.SemaphoreSlim _loadMoreLock = new(1, 1);

    // Autoplay fallback state. When the page chain exhausts on a bounded
    // non-radio context (album, playlist, artist discography), we call
    // ContextResolver.LoadAutoplayAsync once to prefetch station tracks. Once
    // that's done, the queue's ContextUri switches to the station URI and
    // _currentNextPageUrl picks up autoplay's own pagination — so subsequent
    // NeedsMoreTracks signals flow through the regular LoadNextPageAsync path.
    // _originalContextUri is what we send to the autoplay endpoint (the album /
    // playlist / artist URI we were playing before switchover).
    private bool _autoplayTriggered;
    private string? _originalContextUri;

    // Reference to the in-flight autoplay fetch, so OnTrackFinishedAsync can
    // await it if end-of-queue hits before autoplay returns (rapid-skip or
    // "started on last track" edge cases). Null outside the fetch.
    private Task? _pendingAutoplayTask;

    private static bool IsLocalPlaybackContext(string? contextUri)
        => Wavee.Local.LocalUri.IsLocal(contextUri);

    /// <summary>
    /// Lazy-read check for whether autoplay is enabled. Invoked once per
    /// candidate autoplay trigger in <see cref="TryTriggerAutoplayAsync"/>;
    /// when it returns false, the trigger short-circuits and playback reaches
    /// end-of-context as a clean stop. Wired from the UI layer to read from
    /// <c>AppSettings.AutoplayEnabled</c>. Null = assume enabled.
    /// </summary>
    public Func<bool>? AutoplayEnabledProvider { get; set; }

    // Latch: set true for auto-advance / transfer-resume / autoplay rollover,
    // false for user-initiated play. Sticks until the next PlayAsync flips it.
    private bool _isSystemInitiated;

    // Prefetch dedup: remembers which upcoming track URI we've already kicked a
    // prefetch for during the current track. Reset on track change / play / skip.
    // Spotify's PortAudio buffer underruns at transitions all trace back to the
    // AudioKey being requested reactively under HTTP contention; prefetching while
    // the current track plays avoids the race entirely.
    private string? _lastPrefetchedTrackUri;
    private bool _lastPrefetchWasVideo;
    private CancellationTokenSource? _prefetchCts;

    // Pre-warmed video session for the next queued music video. Disposed on
    // queue change or after commit. Capped at one in flight at a time.
    private IPreparedVideoSession? _preparedNextVideoSession;
    private static readonly TimeSpan PreparedVideoMaxAge = TimeSpan.FromMinutes(4);

    // Trigger thresholds. Librespot prefetches when "≈30 s remaining OR past 50 %".
    // We use the same shape: the earlier of the two fires.
    private const long PrefetchRemainingMsThreshold = 20_000;
    private const double PrefetchHalfwayFraction = 0.5;

    private readonly Wavee.Local.ILocalLibraryService? _localLibrary;
    private readonly Wavee.Audio.ILocalMediaPlayer? _localMediaPlayer;
    private readonly Wavee.Audio.ISpotifyVideoPlayback? _spotifyVideoPlayback;
    private readonly bool _localSpotifyPlaybackEnabled;

    /// <summary>True when the most recent play targeted a UI-process video engine
    /// (local file OR Spotify music video). Drives transport-control dispatch.</summary>
    private bool _videoEngineActive;

    /// <summary>When <see cref="_videoEngineActive"/> is true, distinguishes
    /// Spotify video (<c>true</c>) from local-file video (<c>false</c>).</summary>
    private bool _isSpotifyVideoActive;

    /// <summary>Hex-encoded manifest_id for the current Spotify track when it
    /// has a music-video variant. Cached after track resolution so the
    /// "Watch Video" affordance and <see cref="SwitchToVideoAsync"/> can flip
    /// the engine without re-resolving. Cleared on track transitions.</summary>
    private string? _currentVideoManifestId;
    private SpotifyVideoPlaybackTarget? _currentVideoPlaybackTarget;
    private string? _currentLocalVideoAssociationUri;

    public PlaybackOrchestrator(
        AudioPipelineProxy proxy,
        TrackResolver trackResolver,
        ContextResolver contextResolver,
        ConnectCommandHandler? commandHandler,
        ILogger? logger,
        EventService? events = null,
        string? localDeviceId = null,
        Wavee.Local.ILocalLibraryService? localLibrary = null,
        Wavee.Audio.ILocalMediaPlayer? localMediaPlayer = null,
        Wavee.Audio.ISpotifyVideoPlayback? spotifyVideoPlayback = null,
        bool localSpotifyPlaybackEnabled = true)
    {
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        _trackResolver = trackResolver ?? throw new ArgumentNullException(nameof(trackResolver));
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _logger = logger;
        _events = events;
        _localDeviceId = localDeviceId ?? string.Empty;
        _localLibrary = localLibrary;
        _localMediaPlayer = localMediaPlayer;
        _spotifyVideoPlayback = spotifyVideoPlayback;
        _localSpotifyPlaybackEnabled = localSpotifyPlaybackEnabled;
        _queue = new PlaybackQueue(logger);

        // Forward proxy state enriched with queue info
        _subs.Add(_proxy.StateChanges.Subscribe(OnProxyStateChanged));

        // Forward proxy errors
        _subs.Add(_proxy.Errors.Subscribe(err => _errorSubject.OnNext(err)));

        // Auto-advance on track finish
        _subs.Add(_proxy.TrackFinished.Subscribe(msg => { _ = OnTrackFinishedAsync(msg); }));

        // Both video engines emit the same observable shape; pipe them through
        // the same handlers so consumers see one unified stream regardless of
        // which engine produced the state.
        if (_localMediaPlayer is not null)
        {
            _subs.Add(_localMediaPlayer.StateChanges.Subscribe(OnProxyStateChanged));
            _subs.Add(_localMediaPlayer.Errors.Subscribe(err => _errorSubject.OnNext(err)));
            _subs.Add(_localMediaPlayer.TrackFinished.Subscribe(msg => { _ = OnTrackFinishedAsync(msg); }));
        }

        if (_spotifyVideoPlayback is not null)
        {
            _subs.Add(_spotifyVideoPlayback.StateChanges.Subscribe(OnProxyStateChanged));
            _subs.Add(_spotifyVideoPlayback.Errors.Subscribe(err => _errorSubject.OnNext(err)));
            _subs.Add(_spotifyVideoPlayback.TrackFinished.Subscribe(msg => { _ = OnTrackFinishedAsync(msg); }));
        }

        // Lazy load more tracks when queue runs low
        _subs.Add(_queue.NeedsMoreTracks.Subscribe(unit => { _ = LoadMoreTracksAsync(); }));

        // Subscribe to incoming remote commands from Spotify Connect
        SubscribeToRemoteCommands(commandHandler);
    }

    // ── IPlaybackEngine ──

    public IObservable<LocalPlaybackState> StateChanges => _stateSubject.AsObservable();
    public IObservable<PlaybackError> Errors => _errorSubject.AsObservable();

    /// <summary>
    /// Fires when playback reaches end-of-context and every advance tier has
    /// failed (direct next track, repeat-context, autoplay in-flight wait,
    /// autoplay synchronous trigger). The UI subscribes and surfaces a "reached
    /// the end" notification with whatever phrasing fits the event.
    /// </summary>
    public IObservable<EndOfContextEvent> EndOfContext => _endOfContextSubject.AsObservable();
    public LocalPlaybackState CurrentState => _stateSubject.Value;

    private bool RejectIfSpotifyAudioPlaybackDisabled(string? uri, string operation)
    {
        if (_localSpotifyPlaybackEnabled || !SpotifyPlaybackCapabilities.IsSpotifyAudioPlaybackUri(uri))
            return false;

        _logger?.LogWarning(
            "Local Spotify playback blocked: operation={Operation}, uri={Uri}",
            operation,
            uri ?? "<none>");
        _errorSubject.OnNext(new PlaybackError(
            PlaybackErrorType.Unknown,
            SpotifyPlaybackCapabilities.DisabledMessage));
        return true;
    }

    public async Task PlayAsync(PlayCommand command, CancellationToken ct = default)
    {
        try
        {
            _logger?.LogInformation("Orchestrator: PlayAsync contextUri={Context} trackUri={Track}",
                command.ContextUri, command.TrackUri);

            // PlayCommand arrives either from user click (local ConnectCommandExecutor)
            // or from a remote transfer (command handler pipe). Either way, treat the
            // user as the initiator — the auto-advance path flips the latch back to
            // true before calling PlayCurrentTrackAsync.
            _isSystemInitiated = false;

            // Event reporting: remember sender, capture play_origin, decide whether
            // the next ReasonStart is "clickrow" (local UI) or "remote" (transfer
            // from another device). Compared against _localDeviceId so a remote
            // device's id is correctly classified.
            RememberSender(command.SenderDeviceId);
            _currentPlayOrigin = command.PlayOrigin;
            // ConnectCommandExecutor sets SenderDeviceId="" for locally-initiated
            // plays (no remote dealer command involved); only a non-empty,
            // non-local id means a different device sent us this play.
            var isLocalSender = string.IsNullOrEmpty(command.SenderDeviceId)
                                || string.Equals(command.SenderDeviceId, _localDeviceId, StringComparison.Ordinal);
            _nextStartReason = isLocalSender ? Wavee.Connect.Events.PlaybackReason.ClickRow
                                              : Wavee.Connect.Events.PlaybackReason.Remote;

            _currentContextDescription = command.ContextDescription;
            _currentContextImageUrl = command.ContextImageUrl;
            _currentContextFeature = command.ContextFeature;
            _currentContextTrackCount = command.ContextTrackCount;
            _currentContextFormatAttributes = command.ContextFormatAttributes;
            // Reset pagination state; the specific branches below repopulate these.
            _currentNextPageUrl = null;
            _currentContextPageCount = 1;
            _autoplayTriggered = false;
            _pendingAutoplayTask = null;
            _originalContextUri = !string.IsNullOrEmpty(command.ContextUri)
                                  && command.ContextUri != "spotify:internal:queue"
                ? command.ContextUri
                // Single-track ad-hoc plays (search-result click, click-row
                // with no real context) carry "spotify:internal:queue". Treat
                // the played track as an autoplay seed so end-of-queue can
                // route to the radio-apollo path. Storing the bare track URI
                // (not "spotify:station:track:<id>") deliberately avoids the
                // station bailout in TryTriggerAutoplayAsync.
                : (command.PageTracks?.Count == 1
                    ? command.PageTracks[0].Uri
                    : null);
            ResetPrefetch();

            // Build queue from context and/or explicit track list.
            var hasRealContext = !string.IsNullOrEmpty(command.ContextUri)
                                 && command.ContextUri != "spotify:internal:queue";
            var hasPageTracks = command.PageTracks?.Count > 0;

            if (hasRealContext && hasPageTracks)
            {
                // UI-initiated play with a known context. Use the provided tracks
                // (caller may have filtered/shuffled) but publish the real context URI.
                var tracks = command.PageTracks!
                    .Select(BuildQueueTrackFromPageTrack)
                    .ToList();
                _queue.Clear();
                _queue.SetContext(command.ContextUri!, isInfinite: false, totalTracks: tracks.Count);
                _queue.SetTracks(tracks, command.SkipToIndex ?? 0);

                // UI-supplied PageTracks for a local context arrive with only Uri/Uid +
                // forwarded Metadata. Back-fill title / artist / album / poster art from
                // the local library so the right-panel queue + PutState nextTracks render
                // correctly. Fire-and-forget — first playback push doesn't wait on it.
                if (IsLocalPlaybackContext(command.ContextUri))
                    _ = EnrichLocalQueueTracksAsync(ct);

                // The UI supplies its own page-0 tracks (e.g. an extended top-tracks
                // view) but doesn't know the next-page URL or total page count. Kick
                // a background resolve so LoadMoreTracksAsync has somewhere to page
                // to once the user plays through what the UI provided. Local contexts
                // are already represented by PageTracks here; Spotify's resolver does
                // not understand wavee:local:* URIs.
                if (!IsLocalPlaybackContext(command.ContextUri))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var resolved = await _contextResolver.LoadContextAsync(command.ContextUri!);
                            _currentNextPageUrl = resolved.NextPageUrl;
                            _currentContextPageCount = resolved.PageCount;
                            _logger?.LogDebug(
                                "Pagination seeded for {Uri}: nextPage={HasNext}, pageCount={Count}",
                                command.ContextUri, resolved.NextPageUrl != null, resolved.PageCount);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "Pagination seed failed for {Uri}", command.ContextUri);
                        }
                    });
                }
            }
            else if (hasRealContext && IsLocalPlaybackContext(command.ContextUri))
            {
                // Local URI without UI-supplied PageTracks (typical when the
                // user clicks a card on the home shelf or a deep-link path).
                // The Spotify context resolver doesn't know wavee:local:*
                // URIs and would throw "Invalid context URI" — branch into
                // the local library instead and build the queue from
                // whatever the local kind resolves to (single track, album
                // tracklist, or artist tracks).
                var localTracks = await ResolveLocalContextAsync(command.ContextUri!, ct).ConfigureAwait(false);
                if (localTracks.Count == 0 && Wavee.Core.PlayableUri.IsLocalTrack(command.TrackUri))
                    localTracks.Add(new QueueTrack(command.TrackUri!, command.TrackUid ?? string.Empty));
                _queue.Clear();
                _queue.SetContext(command.ContextUri!, isInfinite: false, totalTracks: localTracks.Count);
                _queue.SetTracks(localTracks);
                var localStart = ContextResolver.FindTrackIndex(
                    localTracks, command.TrackUri, command.TrackUid, command.SkipToIndex);
                _queue.SkipTo(localStart);

                // Back-fill TMDB-aware display fields for every queue entry so prev/next
                // tracks render with episode titles and poster art instead of filenames.
                _ = EnrichLocalQueueTracksAsync(ct);
            }
            else if (hasRealContext)
            {
                // Remote transfer / deep-link: resolve the full context from Spotify.
                var context = await _contextResolver.LoadContextAsync(command.ContextUri!, ct: ct);
                _queue.SetContext(command.ContextUri!, context.IsInfinite, context.TotalCount);
                _queue.SetTracks(context.Tracks);

                // /context-resolve/v1/ returns rich context-level metadata
                // (format_list_type, tag, request_id, image_url, header_image_url_desktop,
                //  session_control_display.displayName.*, context_description, …).
                // Forward it so our putstate publishes the same context_metadata
                // Spotify desktop would, not an empty dict.
                if (context.ContextMetadata is { Count: > 0 })
                {
                    _currentContextFormatAttributes = context.ContextMetadata;
                    if (context.ContextMetadata.TryGetValue("context_description", out var desc)
                        && !string.IsNullOrEmpty(desc))
                    {
                        _currentContextDescription = desc;
                    }
                    if (context.ContextMetadata.TryGetValue("image_url", out var img)
                        && !string.IsNullOrEmpty(img))
                    {
                        _currentContextImageUrl = img;
                    }
                }
                _currentContextTrackCount ??= context.TotalCount ?? context.Tracks.Count;

                // Pagination state from the resolver — LoadMoreTracksAsync uses
                // this to fetch the next batch once the queue runs low.
                _currentNextPageUrl = context.NextPageUrl;
                _currentContextPageCount = context.PageCount;

                var startIndex = ContextResolver.FindTrackIndex(
                    context.Tracks, command.TrackUri, command.TrackUid, command.SkipToIndex);
                _queue.SkipTo(startIndex);
            }
            else if (hasPageTracks)
            {
                // True internal queue — no originating context.
                var tracks = command.PageTracks!
                    .Select(BuildQueueTrackFromPageTrack)
                    .ToList();
                _queue.Clear();
                _queue.SetContext("spotify:internal:queue", false);
                _queue.SetTracks(tracks, command.SkipToIndex ?? 0);

                // Same enrichment as the context-bound paths — a queue-only play of
                // local tracks (e.g. an Add-to-Queue chain from search results) still
                // needs title / artist / poster art filled in for prev/next surfaces.
                if (tracks.Any(t => Wavee.Local.LocalUri.IsLocal(t.Uri)))
                    _ = EnrichLocalQueueTracksAsync(ct);
            }

            // Emit NewSessionIdEvent (and flush previous track's TrackTransition)
            // now that the new context is established in the queue. Order matters:
            // the in-flight metrics flush must use the OLD _queue.ContextUri, while
            // the new session id is bound to the NEW context — that's what we have here.
            var sessionContextUri = _queue.ContextUri ?? command.ContextUri ?? string.Empty;
            var sessionContextSize = command.ContextTrackCount
                                     ?? command.PageTracks?.Count
                                     ?? Math.Max(1, _queue.LoadedCount);
            OnContextStarted(sessionContextUri, sessionContextSize, isLocalSender);

            await PlayCurrentTrackAsync(command.PositionMs ?? 0, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PlayAsync failed");
            _errorSubject.OnNext(new PlaybackError(PlaybackErrorType.Unknown, ex.Message, ex));
        }
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        if (_videoEngineActive)
        {
            if (_isSpotifyVideoActive && _spotifyVideoPlayback is not null)
            {
                _logger?.LogInformation("Orchestrator: PauseAsync → Spotify video engine");
                return _spotifyVideoPlayback.PauseAsync(ct);
            }
            if (_localMediaPlayer is not null)
            {
                _logger?.LogInformation("Orchestrator: PauseAsync → local video engine");
                return _localMediaPlayer.PauseAsync(ct);
            }
        }
        _logger?.LogInformation("Orchestrator: PauseAsync → forwarding to proxy");
        return _proxy.PauseAsync(ct);
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        // Post-end-of-context case: EndOfContextAsync reset the queue cursor
        // to 0 and called proxy.StopAsync. The engine has no loaded track, so
        // a plain Resume is a no-op (the symptom users reported as "Play button
        // does nothing"). Detect that state and replay the queue's current
        // track from position 0 — "Play" behaves as "restart the context."
        var engineState = _stateSubject.Value;
        var engineIdle = !engineState.IsPlaying && !engineState.IsPaused && !engineState.IsBuffering;
        if (engineIdle && _queue.Current != null)
        {
            _logger?.LogInformation("Orchestrator: ResumeAsync from Stopped → restarting queue from track 0");
            await PlayCurrentTrackAsync(0, ct);
            return;
        }

        if (_videoEngineActive)
        {
            if (_isSpotifyVideoActive && _spotifyVideoPlayback is not null)
            {
                _logger?.LogInformation("Orchestrator: ResumeAsync → Spotify video engine");
                await _spotifyVideoPlayback.ResumeAsync(ct);
                return;
            }
            if (_localMediaPlayer is not null)
            {
                _logger?.LogInformation("Orchestrator: ResumeAsync → local video engine");
                await _localMediaPlayer.ResumeAsync(ct);
                return;
            }
        }

        _logger?.LogInformation("Orchestrator: ResumeAsync → forwarding to proxy");
        await _proxy.ResumeAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        // Event reporting: Stop is endplay. Captures whatever the user was doing
        // when they hit Stop, and the post-stop ReasonStart will be ClickRow.
        DispatchTrackTransition(Wavee.Connect.Events.PlaybackReason.EndPlay, _stateSubject.Value.PositionMs);

        if (_videoEngineActive)
        {
            if (_isSpotifyVideoActive && _spotifyVideoPlayback is not null)
            {
                _logger?.LogInformation("Orchestrator: StopAsync → Spotify video engine");
                await _spotifyVideoPlayback.StopAsync(ct);
                _videoEngineActive = false;
                _isSpotifyVideoActive = false;
                _currentVideoManifestId = null;
                _currentVideoPlaybackTarget = null;
                _currentLocalVideoAssociationUri = null;
                return;
            }
            if (_localMediaPlayer is not null)
            {
                _logger?.LogInformation("Orchestrator: StopAsync → local video engine");
                await _localMediaPlayer.StopAsync(ct);
                _videoEngineActive = false;
                _currentLocalVideoAssociationUri = null;
                return;
            }
        }

        _logger?.LogInformation("Orchestrator: StopAsync → forwarding to proxy");
        if (_localMediaPlayer?.IsActive == true)
        {
            try { await _localMediaPlayer.StopAsync(ct); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Stopping stale UI MediaPlayer during audio stop"); }
        }

        await _proxy.StopAsync(ct);
    }

    public Task SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_videoEngineActive)
        {
            if (_isSpotifyVideoActive && _spotifyVideoPlayback is not null)
            {
                _logger?.LogInformation("Orchestrator: SeekAsync({Pos}ms) → Spotify video engine", positionMs);
                return _spotifyVideoPlayback.SeekAsync(positionMs, ct);
            }
            if (_localMediaPlayer is not null)
            {
                _logger?.LogInformation("Orchestrator: SeekAsync({Pos}ms) → local video engine", positionMs);
                return _localMediaPlayer.SeekAsync(positionMs, ct);
            }
        }
        _logger?.LogInformation("Orchestrator: SeekAsync({Pos}ms) → forwarding to proxy", positionMs);
        return _proxy.SeekAsync(positionMs, ct);
    }

    public Task SetVolumeAsync(float volume, CancellationToken ct = default)
    {
        // Forward volume to all engines so level is consistent across transitions.
        if (_localMediaPlayer is not null)
            _localMediaPlayer.SetVolume(volume);
        if (_spotifyVideoPlayback is not null)
            _spotifyVideoPlayback.SetVolume(volume);
        _logger?.LogDebug("Orchestrator: SetVolumeAsync({Volume:P0}) → forwarding to proxy", volume);
        return _proxy.SetVolumeAsync(volume, ct);
    }

    public Task SwitchAudioOutputAsync(int deviceIndex, CancellationToken ct = default)
    {
        _logger?.LogInformation("Orchestrator: SwitchAudioOutputAsync(idx={DeviceIndex}) → forwarding to proxy", deviceIndex);
        return _proxy.SwitchAudioOutputAsync(deviceIndex, ct);
    }

    public async Task SwitchQualityAsync(AudioQuality quality, CancellationToken ct = default)
    {
        _trackResolver.SetPreferredQuality(quality);
        ResetPrefetch();

        var current = _queue.Current;
        if (current is null || string.IsNullOrEmpty(current.Uri))
        {
            _logger?.LogInformation("Orchestrator: streaming quality set to {Quality}; no current track to restart", quality);
            return;
        }

        if (!IsSpotifyAudioUri(current.Uri))
        {
            _logger?.LogInformation("Orchestrator: streaming quality set to {Quality}; current media is not Spotify audio", quality);
            return;
        }

        if (_videoEngineActive || _isSpotifyVideoActive || _localMediaPlayer?.IsActive == true)
        {
            _logger?.LogInformation("Orchestrator: streaming quality set to {Quality}; active media engine is video/local", quality);
            return;
        }

        var state = _stateSubject.Value;
        if (!state.IsPlaying && !state.IsPaused && !state.IsBuffering)
        {
            _logger?.LogInformation("Orchestrator: streaming quality set to {Quality}; playback is inactive", quality);
            return;
        }

        if (!string.IsNullOrEmpty(state.TrackUri)
            && !string.Equals(state.TrackUri, current.Uri, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation(
                "Orchestrator: streaming quality set to {Quality}; active state {StateUri} does not match queue current {QueueUri}",
                quality, state.TrackUri, current.Uri);
            return;
        }

        var positionMs = Math.Max(0, state.PositionMs);
        var pauseAfterStart = state.IsPaused;

        _logger?.LogInformation(
            "Orchestrator: switching Spotify audio quality to {Quality} for {Uri} at {PositionMs}ms",
            quality, current.Uri, positionMs);

        DispatchTrackTransition(Wavee.Connect.Events.PlaybackReason.EndPlay, positionMs);
        await PlayCurrentTrackAsync(positionMs, ct, pauseAfterStart).ConfigureAwait(false);
    }

    public async Task SkipNextAsync(CancellationToken ct = default)
    {
        // Event reporting: forward-skip is fwdbtn, regardless of whether the
        // skip lands on a real track or end-of-context. Dispatch before advance
        // so wire ordering is transition-then-newPlaybackId.
        DispatchTrackTransition(Wavee.Connect.Events.PlaybackReason.ForwardBtn, _stateSubject.Value.PositionMs);

        // Shared end-of-queue handling with OnTrackFinishedAsync. Without this
        // delegation, skip-next past the last track bypassed Phase E's autoplay
        // fallback entirely — it just called StopAsync and left the engine in
        // an ambiguous "Stopped with mid-track position" state.
        if (!await TryAdvanceOrAutoplayAsync(ct))
        {
            await EndOfContextAsync(ct);
        }
    }

    public async Task SkipPreviousAsync(CancellationToken ct = default)
    {
        // If more than 3 seconds in, restart current track — NOT a transition.
        var state = _stateSubject.Value;
        if (state.PositionMs > 3000)
        {
            await _proxy.SeekAsync(0, ct);
            return;
        }

        var prev = _queue.MovePrevious();
        if (prev != null)
        {
            // Event reporting: only the actual cross-track back-button case is
            // a transition. The position≤3 s + no-prev-track path below also
            // just re-seeks and isn't reported.
            DispatchTrackTransition(Wavee.Connect.Events.PlaybackReason.BackBtn, state.PositionMs);

            ResetPrefetch();
            _logger?.LogInformation("Orchestrator: skip prev → {Uri}", prev.Uri);
            await PlayCurrentTrackAsync(0, ct);
        }
        else
        {
            await _proxy.SeekAsync(0, ct);
        }
    }

    public Task SetShuffleAsync(bool enabled, CancellationToken ct = default)
    {
        _queue.SetShuffle(enabled);
        PublishQueueState();
        return Task.CompletedTask;
    }

    public Task SetRepeatContextAsync(bool enabled, CancellationToken ct = default)
    {
        _repeatContext = enabled;
        if (enabled) _repeatTrack = false;
        PublishQueueState();
        return Task.CompletedTask;
    }

    public Task SetRepeatTrackAsync(bool enabled, CancellationToken ct = default)
    {
        _repeatTrack = enabled;
        if (enabled) _repeatContext = false;
        PublishQueueState();
        return Task.CompletedTask;
    }

    public Task PlayNextAsync(string trackUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri))
            return Task.CompletedTask;
        if (RejectIfSpotifyAudioPlaybackDisabled(trackUri, nameof(PlayNextAsync)))
            return Task.CompletedTask;

        _queue.PlayNext(new QueueTrack(trackUri));
        _logger?.LogInformation("Orchestrator: PlayNext → {Uri}", trackUri);
        PublishQueueState();
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(string trackUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri))
            return Task.CompletedTask;
        if (RejectIfSpotifyAudioPlaybackDisabled(trackUri, nameof(EnqueueAsync)))
            return Task.CompletedTask;

        _queue.EnqueueAfterContext(new QueueTrack(trackUri));
        _logger?.LogInformation("Orchestrator: Enqueue (post-context) → {Uri}", trackUri);
        PublishQueueState();
        return Task.CompletedTask;
    }

    public async Task SwitchToContextAfterCurrentAsync(
        string contextUri,
        string? currentTrackUri = null,
        string? displayName = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contextUri))
            throw new ArgumentException("Context URI is required.", nameof(contextUri));

        var state = _stateSubject.Value;
        var activeTrackUri = FirstNonWhiteSpace(currentTrackUri, state.TrackUri, _queue.Current?.Uri);
        if (string.IsNullOrWhiteSpace(activeTrackUri))
            throw new InvalidOperationException("No current track is available for the context switch.");

        _logger?.LogInformation(
            "Orchestrator: switching context after current track. context={Context}, current={Current}",
            contextUri,
            activeTrackUri);

        var context = await _contextResolver.LoadContextAsync(contextUri, ct: ct).ConfigureAwait(false);
        if (context.Tracks.Count == 0)
            throw new InvalidOperationException("The resolved context did not contain any tracks.");

        var startIndex = ContextResolver.FindTrackIndex(
            context.Tracks,
            activeTrackUri,
            state.TrackUid,
            fallbackIndex: 0);

        _queue.Clear();
        _queue.SetContext(contextUri, context.IsInfinite, context.TotalCount);
        _queue.SetTracks(context.Tracks, startIndex);

        _originalContextUri = contextUri;
        _autoplayTriggered = false;
        _pendingAutoplayTask = null;
        _currentNextPageUrl = context.NextPageUrl;
        _currentContextPageCount = context.PageCount;
        _currentContextTrackCount = context.TotalCount ?? context.Tracks.Count;
        _currentContextFormatAttributes = context.ContextMetadata;
        _currentContextFeature = ContextFeatureForUri(contextUri);
        _currentContextDescription = FirstNonWhiteSpace(
            TryGetContextMetadata(context.ContextMetadata, "context_description"),
            displayName);
        _currentContextImageUrl = TryGetContextMetadata(context.ContextMetadata, "image_url");

        ResetPrefetch();
        PublishQueueState();
    }

    // ── Core ──

    private static string? ContextFeatureForUri(string contextUri) => contextUri switch
    {
        _ when contextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal) => "playlist",
        _ when contextUri.StartsWith("spotify:album:", StringComparison.Ordinal) => "album",
        _ when contextUri.StartsWith("spotify:artist:", StringComparison.Ordinal) => "artist",
        _ when contextUri.StartsWith("spotify:show:", StringComparison.Ordinal) => "show",
        _ when contextUri.StartsWith("spotify:episode:", StringComparison.Ordinal) => "episode",
        _ when contextUri.Contains("collection", StringComparison.OrdinalIgnoreCase) => "your_library",
        _ => null
    };

    private static string? TryGetContextMetadata(IReadOnlyDictionary<string, string>? metadata, string key)
        => metadata is not null && metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private async Task PlayCurrentTrackAsync(long positionMs, CancellationToken ct = default, bool pauseAfterStart = false)
    {
        var current = _queue.Current;
        if (current == null)
        {
            _logger?.LogWarning("No current track in queue");
            return;
        }

        // Local-file fast path: no Spotify resolution, no CDN/key, no head data.
        // Hand the file path straight to AudioHost which opens it via BASS.
        if (Wavee.Core.PlayableUri.IsLocalTrack(current.Uri))
        {
            await PlayLocalCurrentTrackAsync(current, positionMs, ct);
            return;
        }

        // Spotify music-video path: track_player=video + media.manifest_id signals
        // that Spotify wants DASH+PlayReady instead of AudioHost+BASS.
        if (_spotifyVideoPlayback is not null
            && current.Metadata is not null
            && current.Metadata.TryGetValue("track_player", out var trackPlayer) && trackPlayer == "video"
            && current.Metadata.TryGetValue("media.manifest_id", out var manifestId))
        {
            double startMs = current.Metadata.TryGetValue("media.start_position", out var pos)
                             && double.TryParse(pos, out var d) ? d : positionMs;
            var target = await _trackResolver.ResolveVideoPlaybackTargetAsync(
                current.Uri,
                manifestIdOverride: manifestId,
                videoUriOverride: current.Uri,
                ct: ct).ConfigureAwait(false);
            if (target is not null)
                await StartSpotifyVideoForCurrentAsync(current, target, (long)startMs, ct);
            return;
        }

        if (ShouldPreferSpotifyVideoPlayback())
        {
            try
            {
                var videoTarget = await _trackResolver.ResolveVideoPlaybackTargetAsync(current.Uri, ct: ct);
                if (videoTarget is not null)
                {
                    await StartSpotifyVideoForCurrentAsync(current, videoTarget, positionMs, ct);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Sticky Spotify video resolution failed for {Uri}; falling back to audio", current.Uri);
            }
        }

        if (RejectIfSpotifyAudioPlaybackDisabled(current.Uri, nameof(PlayCurrentTrackAsync)))
            return;

        // Spotify audio path. If we just handed a video to a UI engine, stop
        // it before the AudioHost-driven Spotify track starts so we don't end
        // up with two engines emitting state simultaneously.
        if (_isSpotifyVideoActive && _spotifyVideoPlayback is not null)
        {
            try { await _spotifyVideoPlayback.StopAsync(ct); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Stopping Spotify video for audio handoff"); }
            _videoEngineActive = false;
            _isSpotifyVideoActive = false;
            _currentVideoPlaybackTarget = null;
        }
        else if ((_videoEngineActive || _localMediaPlayer?.IsActive == true) && _localMediaPlayer is not null)
        {
            try { await _localMediaPlayer.StopAsync(ct); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Stopping UI MediaPlayer for Spotify handoff"); }
            _videoEngineActive = false;
        }

        _logger?.LogInformation("Resolving track (deferred): {Uri}", current.Uri);

        // 1. Resolve with head file (instant start) + deferred CDN/key
        var resolution = await _trackResolver.ResolveWithHeadAsync(current.Uri, ct);

        // Remember the music-video manifest_id (if any). This is what
        // SwitchToVideoAsync will hand to ISpotifyVideoPlayback when the user
        // clicks "Watch Video", and what flows out on LocalPlaybackState so the
        // UI can show/hide the affordance.
        _currentVideoManifestId = resolution.VideoManifestId;
        _currentVideoPlaybackTarget = null;
        _currentLocalVideoAssociationUri = null;

        // Event reporting: open a fresh playback metrics window for this track
        // and emit NewPlaybackIdEvent. Done before PlayTrackDeferredAsync so the
        // start interval's begin position lines up with what the audio engine sees.
        OnTrackStarted(current.Uri, (int)Math.Min(positionMs, int.MaxValue), resolution);

        // 2. Generate deferred ID
        var deferredId = Guid.NewGuid().ToString("N");

        // 3. Send head data to AudioHost immediately — playback starts NOW
        await _proxy.PlayTrackDeferredAsync(new Wavee.Playback.Contracts.PlayTrackCommand
        {
            DeferredId = deferredId,
            TrackUri = current.Uri,
            TrackUid = current.Uid,
            Codec = resolution.Codec,
            DurationMs = resolution.DurationMs,
            PositionMs = positionMs,
            NormalizationGain = resolution.Normalization.TrackGainDb,
            NormalizationPeak = resolution.Normalization.TrackPeak,
            HeadData = resolution.HeadData != null ? Convert.ToBase64String(resolution.HeadData) : null,
            Metadata = resolution.Metadata,
        }, ct);

        if (pauseAfterStart)
            await _proxy.PauseAsync(ct).ConfigureAwait(false);

        _logger?.LogInformation("Head data sent — audio starting instantly for {Title}", resolution.Metadata?.Title);

        // 4. Wait for audio key (always needed); CDN URL only if not using local cache
        await Task.WhenAll(resolution.AudioKeyTask, resolution.FileSizeTask);

        // 5. Send deferred resolution → AudioHost seamlessly continues
        if (resolution.LocalCacheFileId != null)
        {
            // File is fully cached on disk — no CDN URL needed
            await _proxy.SendDeferredCachedAsync(
                deferredId,
                resolution.LocalCacheFileId,
                await resolution.AudioKeyTask,
                await resolution.FileSizeTask,
                ct);
            _logger?.LogInformation("Deferred resolved from local cache for {Title}", resolution.Metadata?.Title);
        }
        else
        {
            await _proxy.SendDeferredResolvedAsync(
                deferredId,
                await resolution.CdnUrlTask,
                await resolution.AudioKeyTask,
                await resolution.FileSizeTask,
                spotifyFileId: resolution.SpotifyFileId,
                ct: ct);
            _logger?.LogInformation("Deferred CDN resolved — seamless playback continuing");
        }

        PublishQueueState();
    }

    private bool ShouldPreferSpotifyVideoPlayback()
        => _spotifyVideoPlayback is not null && _isSpotifyVideoActive;

    private static bool IsSpotifyAudioUri(string uri)
        => uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase)
           || uri.StartsWith("spotify:episode:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stops the audio path and starts the Spotify music-video engine for the
    /// given queue track. Used by both the auto-routing path (remote-driven
    /// dealer command carrying <c>track_player=video</c>) and the manual
    /// "Watch Video" switch entry (<see cref="SwitchToVideoAsync"/>).
    /// </summary>
    private async Task StartSpotifyVideoForCurrentAsync(
        Wavee.Audio.Queue.QueueTrack current,
        SpotifyVideoPlaybackTarget target,
        long positionMs,
        CancellationToken ct)
    {
        if (_spotifyVideoPlayback is null) return;

        if (!_isSpotifyVideoActive && _currentMetrics is not null)
            DispatchTrackTransition(Wavee.Connect.Events.PlaybackReason.EndPlay, positionMs);

        // Flip the active-engine flags BEFORE awaiting any teardown / startup
        // so state updates emitted during the handoff are routed correctly.
        _videoEngineActive = true;
        _isSpotifyVideoActive = true;
        _currentVideoManifestId = target.ManifestId;
        _currentVideoPlaybackTarget = target;
        _currentLocalVideoAssociationUri = null;

        OnVideoTrackStarted(target, (int)Math.Min(positionMs, int.MaxValue));

        _logger?.LogInformation("Spotify video playback: audio={AudioUri} video={VideoUri} manifest={Manifest} pos={Pos}ms",
            target.AudioTrackUri, target.VideoTrackUri, target.ManifestId, positionMs);

        // Consume any prefetched/prepared session that matches this target.
        IPreparedVideoSession? prepared = null;
        var candidate = Interlocked.Exchange(ref _preparedNextVideoSession, null);
        if (candidate is not null)
        {
            if (string.Equals(candidate.VideoTrackUri, target.VideoTrackUri, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow - candidate.PreparedAt < PreparedVideoMaxAge)
            {
                prepared = candidate;
                _logger?.LogDebug("Using prepared video session uri={Uri}", target.VideoTrackUri);
            }
            else
            {
                _ = DisposePreparedVideoSessionAsync(candidate);
            }
        }

        // Run audio teardown and video startup IN PARALLEL — they touch
        // different processes (AudioHost vs WebView2) and have no ordering
        // dependency. This saves the AudioHost stop latency from the
        // perceived "time to first frame".
        Task stopAudioTask;
        try { stopAudioTask = _proxy.StopAsync(ct); }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Stopping AudioHost for Spotify video");
            stopAudioTask = Task.CompletedTask;
        }

        Task stopLocalVideoTask = Task.CompletedTask;
        if (_localMediaPlayer?.IsActive == true)
        {
            try { stopLocalVideoTask = _localMediaPlayer.StopAsync(ct); }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Stopping local video for Spotify video");
            }
        }

        Task playVideoTask;
        if (prepared is not null)
        {
            playVideoTask = _spotifyVideoPlayback.PlayAsync(prepared, positionMs, ct);
        }
        else
        {
            playVideoTask = _spotifyVideoPlayback.PlayAsync(
                target.ManifestId,
                target.VideoTrackUri,
                target.Metadata,
                target.DurationMs,
                positionMs,
                ct);
        }

        try
        {
            await Task.WhenAll(stopAudioTask, stopLocalVideoTask, playVideoTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Parallel audio-stop / video-play handoff hit an error");
        }

        PublishQueueState();
    }

    /// <summary>
    /// Manual "switch to video" entry point. Routes the current track to the
    /// PlayReady/DASH engine at the live playback position. No-op if the track
    /// has no music-video variant or video is already active.
    ///
    /// Resolution order for the manifest_id: explicit override (e.g. UI lazily
    /// resolved it via Pathfinder for a linked-URI track) → Connect-state
    /// metadata → cached <c>_currentVideoManifestId</c> from track resolution.
    /// </summary>
    public async Task SwitchToVideoAsync(
        string? manifestIdOverride = null,
        string? videoTrackUriOverride = null,
        CancellationToken ct = default)
    {
        if (_isSpotifyVideoActive) return;
        var current = _queue.Current;
        if (current is null) return;

        if (Wavee.Core.PlayableUri.IsLocalTrack(videoTrackUriOverride))
        {
            await StartLinkedLocalVideoForCurrentAsync(
                current,
                videoTrackUriOverride!,
                _stateSubject.Value.PositionMs,
                ct).ConfigureAwait(false);
            return;
        }

        if (_spotifyVideoPlayback is null) return;

        var target = await _trackResolver.ResolveVideoPlaybackTargetAsync(
            current.Uri,
            manifestIdOverride,
            videoTrackUriOverride,
            ct).ConfigureAwait(false);

        string? manifestId = target?.ManifestId ?? manifestIdOverride;
        if (string.IsNullOrEmpty(manifestId)
            && current.Metadata is not null
            && current.Metadata.TryGetValue("media.manifest_id", out var m))
            manifestId = m;
        manifestId ??= _currentVideoManifestId;
        if (string.IsNullOrEmpty(manifestId)) return;

        target ??= await _trackResolver.ResolveVideoPlaybackTargetAsync(
            current.Uri,
            manifestId,
            videoTrackUriOverride,
            ct).ConfigureAwait(false);
        if (target is null) return;

        // Cache the resolved id so subsequent state ticks publish it through
        // LocalPlaybackState.VideoManifestId (e.g. for the Now Playing bar).
        _currentVideoManifestId = manifestId;

        var positionMs = _stateSubject.Value.PositionMs;
        await StartSpotifyVideoForCurrentAsync(current, target, positionMs, ct);
    }

    private async Task StartLinkedLocalVideoForCurrentAsync(
        QueueTrack current,
        string localVideoUri,
        long positionMs,
        CancellationToken ct)
    {
        if (_localLibrary is null || _localMediaPlayer is null)
        {
            _logger?.LogWarning("Local music-video association requested but local playback services are unavailable");
            return;
        }

        var row = await _localLibrary.GetTrackAsync(localVideoUri, ct).ConfigureAwait(false);
        if (row is null)
        {
            _logger?.LogWarning("Linked local music video not found in index: {Uri}", localVideoUri);
            return;
        }

        if (!File.Exists(row.FilePath))
        {
            _logger?.LogWarning("Linked local music-video file no longer exists: {Path}", row.FilePath);
            return;
        }

        if (_currentMetrics is not null)
            DispatchTrackTransition(Wavee.Connect.Events.PlaybackReason.EndPlay, positionMs);

        try { await _proxy.StopAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Stopping AudioHost for linked local music video"); }

        if (_localMediaPlayer.IsActive)
        {
            try { await _localMediaPlayer.StopAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Stopping previous local video for linked local music video"); }
        }

        // Same as the LoadLocalTrack path — overlay is applied inside
        // LocalLibraryService, so row / enriched are already overlaid.
        var enriched = await _localLibrary.GetPlaybackMetadataAsync(localVideoUri, ct).ConfigureAwait(false);
        var filenameFallback = Path.GetFileNameWithoutExtension(row.FilePath);
        var displayTitle = enriched?.FormatDisplayTitle(filenameFallback)
                           ?? row.Title
                           ?? filenameFallback;
        var displayArtist = enriched?.FormatDisplayArtist(row.AlbumArtist)
                            ?? row.Artist
                            ?? row.AlbumArtist;

        var metadata = new Wavee.Playback.Contracts.TrackMetadataDto
        {
            Title = displayTitle,
            Artist = displayArtist,
            Album = row.Album,
            AlbumUri = row.AlbumUri,
            ArtistUri = row.ArtistUri,
            ImageUrl = row.ArtworkUri,
            ImageLargeUrl = row.ArtworkUri,
            ImageSmallUrl = row.ArtworkUri,
            ImageXLargeUrl = row.ArtworkUri,
        };

        var startPositionMs = positionMs;
        if (row.DurationMs > 0)
            startPositionMs = Math.Min(positionMs, Math.Max(0, row.DurationMs - 500));

        _videoEngineActive = true;
        _isSpotifyVideoActive = false;
        _currentVideoManifestId = null;
        _currentVideoPlaybackTarget = null;
        _currentLocalVideoAssociationUri = localVideoUri;

        _logger?.LogInformation(
            "Linked local music-video playback: audio={AudioUri} video={VideoUri} path={Path} pos={Pos}ms",
            current.Uri,
            localVideoUri,
            row.FilePath,
            startPositionMs);

        await _localMediaPlayer.PlayFileAsync(
            row.FilePath,
            current.Uri,
            metadata,
            startPositionMs,
            ct).ConfigureAwait(false);
        PublishQueueState();
    }

    /// <summary>
    /// Manual "switch to audio" entry point. Stops the Spotify video surface
    /// and hands the same queue item back to the AudioHost path at the live
    /// playback position.
    /// </summary>
    public async Task SwitchToAudioAsync(CancellationToken ct = default)
    {
        var state = _stateSubject.Value;
        var positionMs = state.PositionMs;
        var wasPlaying = state.IsPlaying;
        var current = _queue.Current;
        if (current is null) return;

        if (_currentLocalVideoAssociationUri is not null
            && _videoEngineActive
            && !_isSpotifyVideoActive)
        {
            if (RejectIfSpotifyAudioPlaybackDisabled(current.Uri, nameof(SwitchToAudioAsync)))
                return;

            if (_localMediaPlayer is not null)
            {
                try { await _localMediaPlayer.StopAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogDebug(ex, "Stopping linked local video for audio switch"); }
            }

            _videoEngineActive = false;
            _isSpotifyVideoActive = false;
            _currentLocalVideoAssociationUri = null;
            _currentVideoPlaybackTarget = null;
            _currentVideoManifestId = null;

            _logger?.LogInformation("Switching linked local music video back to audio at {Position}ms: {Uri}",
                positionMs, current.Uri);
            await PlayCurrentTrackAsync(positionMs, ct).ConfigureAwait(false);

            if (!wasPlaying)
                await _proxy.PauseAsync(ct).ConfigureAwait(false);
            return;
        }

        if (!_isSpotifyVideoActive) return;

        var audioTrack = BuildAudioQueueTrackForCurrentVideo(current);
        if (RejectIfSpotifyAudioPlaybackDisabled(audioTrack.Uri, nameof(SwitchToAudioAsync)))
            return;

        if (!string.Equals(audioTrack.Uri, current.Uri, StringComparison.Ordinal))
            _queue.ReplaceCurrent(audioTrack);

        if (_spotifyVideoPlayback is not null)
        {
            try { await _spotifyVideoPlayback.StopAsync(ct); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Stopping Spotify video for audio switch"); }
        }

        _videoEngineActive = false;
        _isSpotifyVideoActive = false;
        _currentVideoPlaybackTarget = null;
        _currentLocalVideoAssociationUri = null;

        _logger?.LogInformation("Switching Spotify video back to audio at {Position}ms: {Uri}",
            positionMs, audioTrack.Uri);
        await PlayCurrentTrackAsync(positionMs, ct);

        if (!wasPlaying)
            await _proxy.PauseAsync(ct);
    }

    private QueueTrack BuildAudioQueueTrackForCurrentVideo(QueueTrack current)
    {
        var target = _currentVideoPlaybackTarget;
        if (target is null || string.IsNullOrWhiteSpace(target.AudioTrackUri))
            return StripVideoMetadata(current);

        var original = target.OriginalMetadata;
        return StripVideoMetadata(current) with
        {
            Uri = target.AudioTrackUri,
            Title = string.IsNullOrWhiteSpace(original.Title) ? current.Title : original.Title,
            Artist = string.IsNullOrWhiteSpace(original.Artist) ? current.Artist : original.Artist,
            Album = string.IsNullOrWhiteSpace(original.Album) ? current.Album : original.Album,
            AlbumUri = string.IsNullOrWhiteSpace(original.AlbumUri) ? current.AlbumUri : original.AlbumUri,
            ArtistUri = string.IsNullOrWhiteSpace(original.ArtistUri) ? current.ArtistUri : original.ArtistUri,
            DurationMs = target.OriginalDurationMs > 0
                ? (int)Math.Min(int.MaxValue, target.OriginalDurationMs)
                : current.DurationMs,
            ImageUrl = string.IsNullOrWhiteSpace(original.ImageUrl) ? current.ImageUrl : original.ImageUrl
        };
    }

    private static QueueTrack StripVideoMetadata(QueueTrack track)
    {
        if (track.Metadata is not { Count: > 0 }) return track;

        var metadata = new Dictionary<string, string>(track.Metadata.Count, StringComparer.Ordinal);
        foreach (var entry in track.Metadata)
        {
            if (entry.Key is "track_player"
                or "media.manifest_id"
                or "media.start_position"
                or "wavee.video_track_uri"
                or "wavee.video_duration")
            {
                continue;
            }

            metadata[entry.Key] = entry.Value;
        }

        return track with
        {
            Metadata = metadata.Count == 0 ? null : metadata
        };
    }

    /// <summary>
    /// Resolves a <c>wavee:local:{kind}:{hash}</c> context URI into a flat
    /// list of queue tracks. Local URIs never round-trip through Spotify's
    /// context resolver (it doesn't recognise them and throws), so PlayAsync
    /// branches here when the context is local and the UI didn't supply
    /// PageTracks. Track URI → single-track queue; album → album's tracks
    /// in tracklist order; artist → all of the artist's local tracks.
    /// </summary>
    private async Task<List<Wavee.Audio.Queue.QueueTrack>> ResolveLocalContextAsync(string contextUri, CancellationToken ct)
    {
        var result = new List<Wavee.Audio.Queue.QueueTrack>();
        if (_localLibrary is null)
        {
            _logger?.LogWarning("Local context {Uri} requested but ILocalLibraryService is not registered", contextUri);
            return result;
        }

        if (Wavee.Local.LocalUri.IsLibrary(contextUri))
        {
            var tracks = await _localLibrary.GetAllTracksAsync(ct).ConfigureAwait(false);
            foreach (var t in tracks)
                result.Add(ToLocalQueueTrack(t));
            return result;
        }

        if (!Wavee.Local.LocalUri.TryParse(contextUri, out var kind, out _))
            return result;

        switch (kind)
        {
            case Wavee.Local.LocalUriKind.Track:
            {
                // Single-track context: just enqueue the track itself. The
                // local play path in PlayLocalCurrentTrackAsync handles
                // metadata lookup; we don't need to populate Title/Artist
                // here (and the orchestrator's track-info publication later
                // pulls from LocalLibraryService.GetTrackAsync anyway).
                result.Add(new Wavee.Audio.Queue.QueueTrack(contextUri));
                break;
            }
            case Wavee.Local.LocalUriKind.Album:
            {
                var album = await _localLibrary.GetAlbumAsync(contextUri, ct).ConfigureAwait(false);
                if (album is null) break;
                foreach (var t in album.Tracks)
                    result.Add(ToLocalQueueTrack(t));
                break;
            }
            case Wavee.Local.LocalUriKind.Artist:
            {
                var artist = await _localLibrary.GetArtistAsync(contextUri, ct).ConfigureAwait(false);
                if (artist is null) break;
                foreach (var t in artist.AllTracks)
                    result.Add(ToLocalQueueTrack(t));
                break;
            }
        }
        return result;
    }

    private static Wavee.Audio.Queue.QueueTrack ToLocalQueueTrack(Wavee.Local.LocalTrackRow track)
        => new(
            track.TrackUri,
            Title: track.Title,
            Artist: track.Artist ?? track.AlbumArtist,
            Album: track.Album,
            AlbumUri: track.AlbumUri,
            ArtistUri: track.ArtistUri,
            DurationMs: (int)Math.Min(int.MaxValue, track.DurationMs),
            ImageUrl: track.ArtworkUri);

    /// <summary>
    /// Walks every wavee:local:* URI in the queue and back-fills the
    /// QueueTrack's display title, artist, album, and poster art using
    /// LocalLibraryService's TMDB-aware enrichment. Without this, the right-
    /// panel queue renders blank rows (no title / no art) for prev_tracks /
    /// next_tracks and PutState ships sparse ProvidedTrack metadata to
    /// remote controllers — only the currently-playing track was enriched
    /// at play-time (see PlayLocalCurrentTrackAsync).
    /// Fire-and-forget from PlayAsync's queue-build paths so the synchronous
    /// "start playback" path isn't blocked on N SQLite lookups; once the
    /// walk completes, PublishQueueState re-emits state with the enriched
    /// metadata so PutState picks it up on the next tick.
    /// </summary>
    private async Task EnrichLocalQueueTracksAsync(CancellationToken ct)
    {
        if (_localLibrary is null) return;

        var uris = _queue.GetContextTrackUris()
            .Where(Wavee.Local.LocalUri.IsLocal)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (uris.Count == 0) return;

        var enrichedAny = false;
        foreach (var uri in uris)
        {
            if (ct.IsCancellationRequested) return;

            Wavee.Local.LocalPlaybackMetadata? meta;
            try
            {
                meta = await _localLibrary.GetPlaybackMetadataAsync(uri, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Local queue enrichment lookup failed for {Uri}", uri);
                continue;
            }
            if (meta is null) continue;

            var displayTitle = meta.FormatDisplayTitle(filenameFallback: null);
            var displayArtist = meta.FormatDisplayArtist(rawArtistFallback: null);

            var replaced = _queue.EnrichByUri(uri, existing => existing with
            {
                Title = !string.IsNullOrEmpty(existing.Title) ? existing.Title : displayTitle,
                Artist = !string.IsNullOrEmpty(existing.Artist) ? existing.Artist : (displayArtist ?? existing.Artist),
                Album = !string.IsNullOrEmpty(existing.Album) ? existing.Album : (meta.RawAlbum ?? existing.Album),
                ImageUrl = !string.IsNullOrEmpty(existing.ImageUrl) ? existing.ImageUrl : (meta.ArtworkUri ?? existing.ImageUrl),
            });

            if (replaced > 0) enrichedAny = true;
        }

        if (enrichedAny)
            PublishQueueState();
    }

    /// <summary>
    /// Local-file playback path. Looks up the indexed file path and metadata.
    /// Dispatches to the UI MediaPlayer for video files (so the user sees the
    /// frames) and to AudioHost otherwise (BASS owns the audio pipeline). No
    /// Spotify protocol involved.
    /// </summary>
    private async Task PlayLocalCurrentTrackAsync(Wavee.Audio.Queue.QueueTrack current, long positionMs, CancellationToken ct)
    {
        if (_localLibrary is null)
        {
            _logger?.LogError("Local track {Uri} requested but ILocalLibraryService is not registered", current.Uri);
            return;
        }

        var row = await _localLibrary.GetTrackAsync(current.Uri, ct);
        if (row is null)
        {
            _logger?.LogWarning("Local track not found in index: {Uri}", current.Uri);
            return;
        }

        if (!File.Exists(row.FilePath))
        {
            _logger?.LogWarning("Local file no longer exists at indexed path: {Path}", row.FilePath);
            return;
        }

        // Resume-on-play: if the caller didn't specify a position (positionMs == 0)
        // and we have a saved resume point on the row, pick it up. Callers that
        // explicitly seek (Seek / transfer) pass a non-zero positionMs and win.
        if (positionMs == 0 && row.LastPositionMs > 0)
        {
            positionMs = row.LastPositionMs;
            _logger?.LogInformation("Resuming local track {Uri} at {Pos} ms (from last saved position)",
                current.Uri, positionMs);
        }

        // No metrics / gabo for local URIs — Spotify event reporting is Spotify-only.

        // Pull TMDB enrichment so a TV episode reads as "S01E01 · Pilot" / its
        // series name instead of the raw filename / "Unknown Artist". Falls back
        // to the row's own title when not enriched (or for plain music). Used
        // by the player bar, the expanded layout, the theatre / fullscreen
        // surfaces, and the AudioHost echo (so OS-level smart media transport
        // shows the same string).
        // GetTrackAsync + GetPlaybackMetadataAsync both apply the
        // metadata_overrides overlay inside LocalLibraryService, so the row /
        // enriched values are already the effective Title/Artist/Album.
        var enriched = await _localLibrary.GetPlaybackMetadataAsync(current.Uri, ct);
        var filenameFallback = Path.GetFileNameWithoutExtension(row.FilePath);
        var displayTitle = enriched?.FormatDisplayTitle(filenameFallback)
                           ?? row.Title
                           ?? filenameFallback;
        var displayArtist = enriched?.FormatDisplayArtist(row.AlbumArtist)
                            ?? row.Artist
                            ?? row.AlbumArtist;

        var metadata = new Wavee.Playback.Contracts.TrackMetadataDto
        {
            Title = displayTitle,
            Artist = displayArtist,
            Album = row.Album,
            AlbumUri = row.AlbumUri,
            ArtistUri = row.ArtistUri,
            ImageUrl = row.ArtworkUri,
            ImageLargeUrl = row.ArtworkUri,
            ImageSmallUrl = row.ArtworkUri,
            ImageXLargeUrl = row.ArtworkUri,
        };

        // Local-file tracks never have a Spotify music-video variant; clear
        // any cached manifest id from a prior remote track so the UI hides
        // the "Watch Video" button.
        _currentVideoManifestId = null;
        _currentVideoPlaybackTarget = null;
        _currentLocalVideoAssociationUri = null;

        if (_isSpotifyVideoActive && _spotifyVideoPlayback is not null)
        {
            try { await _spotifyVideoPlayback.StopAsync(ct); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Stopping Spotify video for local-file handoff"); }
            _videoEngineActive = false;
            _isSpotifyVideoActive = false;
        }

        // Routing gate: row.IsVideo is driven by the scanner's file-extension
        // check only (.mp4/.mov/.m4v/.mkv/.webm). That misses TV episodes /
        // movies whose container isn't in the set (.avi, .ts, .flv, .wmv, ...),
        // even when the classifier correctly identified them as video content.
        // Widen the gate by OR'ing in the effective classification kind from
        // the enrichment lookup we already performed — zero extra I/O.
        var kindIsVideo = enriched is not null
            && Wavee.Local.Classification.LocalContentKindExtensions.IsVideo(enriched.Kind);

        if ((row.IsVideo || kindIsVideo) && _localMediaPlayer is not null)
        {
            // Video → UI MediaPlayer. Stop AudioHost so audio doesn't double up.
            try { await _proxy.StopAsync(ct); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Stopping AudioHost for video handoff"); }

            _videoEngineActive = true;
            _isSpotifyVideoActive = false;
            _currentVideoPlaybackTarget = null;
            await _localMediaPlayer.PlayFileAsync(row.FilePath, current.Uri, metadata, positionMs, ct);
            _logger?.LogInformation("Local video playback started: {Title} ({Path})", metadata.Title, row.FilePath);
            PublishQueueState();
            return;
        }

        // Audio → AudioHost (BASS). Stop UI MediaPlayer if it was active.
        if ((_videoEngineActive || _localMediaPlayer?.IsActive == true) && _localMediaPlayer is not null)
        {
            try { await _localMediaPlayer.StopAsync(ct); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Stopping UI MediaPlayer for audio handoff"); }
        }
        _videoEngineActive = false;
        _isSpotifyVideoActive = false;

        var cmd = new Wavee.Playback.Contracts.PlayLocalFileCommand
        {
            TrackUri = current.Uri,
            FilePath = row.FilePath,
            DurationMs = row.DurationMs,
            StartPositionMs = positionMs,
            Metadata = metadata,
            Normalization = null,
        };

        await _proxy.PlayLocalFileAsync(cmd, ct);
        _logger?.LogInformation("Local file playback started: {Title} ({Path})", cmd.Metadata?.Title, cmd.FilePath);
        PublishQueueState();
    }

    private async Task OnTrackFinishedAsync(Playback.Contracts.TrackFinishedMessage msg)
    {
        try
        {
            // Auto-advance (or repeat-track rollover) is system-initiated — flip the
            // latch so the next publish carries is_system_initiated=true until the
            // user next calls PlayAsync.
            _isSystemInitiated = true;
            _logger?.LogInformation("Track finished: {Uri} reason={Reason}", msg.TrackUri, msg.Reason);

            // Event reporting: emit TrackTransitionEvent before starting the next
            // track. trackdone for natural finish, trackerror for AudioHost-reported
            // failures. End position = full duration for trackdone (the engine has
            // already left position behind; trust the resolution duration if known).
            var endPos = string.Equals(msg.Reason, "error", StringComparison.OrdinalIgnoreCase)
                ? _stateSubject.Value.PositionMs
                : (_currentResolution?.DurationMs
                   ?? _currentMetrics?.Player?.Duration
                   ?? (_stateSubject.Value.DurationMs > 0
                       ? _stateSubject.Value.DurationMs
                       : _stateSubject.Value.PositionMs));
            DispatchTrackTransition(
                string.Equals(msg.Reason, "error", StringComparison.OrdinalIgnoreCase)
                    ? Wavee.Connect.Events.PlaybackReason.TrackError
                    : Wavee.Connect.Events.PlaybackReason.TrackDone,
                endPos);

            if (_repeatTrack)
            {
                await PlayCurrentTrackAsync(0);
                return;
            }

            if (!await TryAdvanceOrAutoplayAsync())
            {
                await EndOfContextAsync();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error advancing to next track");
        }
    }

    /// <summary>
    /// Shared end-of-queue handling. Attempts, in order: direct advance to next
    /// track → repeat-context wrap → wait for in-flight autoplay prefetch →
    /// trigger autoplay synchronously. Returns true if any tier succeeded and
    /// playback continued; false if all exhausted.
    /// </summary>
    private async Task<bool> TryAdvanceOrAutoplayAsync(CancellationToken ct = default)
    {
        // Tier 0: direct advance
        var next = _queue.MoveNext();
        if (next != null)
        {
            ResetPrefetch();
            _logger?.LogInformation("Orchestrator: advance → {Uri}", next.Uri);
            await PlayCurrentTrackAsync(0, ct);
            return true;
        }

        // Tier 1: repeat context
        if (_repeatContext)
        {
            _queue.SkipTo(0);
            await PlayCurrentTrackAsync(0, ct);
            return true;
        }

        // Tier 2: autoplay fetch in-flight, wait briefly. Signal fires at T-5
        // tracks remaining; in the normal case autoplay is long since staged
        // and Tier 0 succeeded. This tier catches the rapid-skip race.
        var pending = _pendingAutoplayTask;
        if (pending is { IsCompleted: false })
        {
            _logger?.LogInformation("End of queue — autoplay in-flight, waiting up to 3s");
            var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(3), ct));
            if (completed == pending && _queue.HasNext)
            {
                next = _queue.MoveNext();
                if (next != null)
                {
                    await PlayCurrentTrackAsync(0, ct);
                    return true;
                }
            }
            else
            {
                _logger?.LogWarning("Autoplay fetch exceeded grace timeout — giving up");
            }
        }

        // Tier 3: autoplay never triggered (user started on / skipped directly
        // to a track past the T-5 threshold, so NeedsMoreTracks never fired).
        // Fire synchronously — brief stall while HTTP returns, then advance.
        if (!_autoplayTriggered)
        {
            _logger?.LogInformation("End of queue without autoplay trigger — firing synchronously");
            await TryTriggerAutoplayAsync();
            if (_queue.HasNext)
            {
                next = _queue.MoveNext();
                if (next != null)
                {
                    await PlayCurrentTrackAsync(0, ct);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Called when all advance tiers in <see cref="TryAdvanceOrAutoplayAsync"/>
    /// fail. Stops the engine in a coherent state (position 0, queue cursor
    /// reset to 0) and emits an <see cref="EndOfContextEvent"/> so the UI can
    /// show a toast / restart button.
    ///
    /// Without this, the engine's last-published state was "Status=Stopped,
    /// track=lastUri, pos=3600ms" — an ambiguous mid-track frozen state. The
    /// PlayerBar rendered it as "kind of playing", the Play button had nothing
    /// to resume (engine was stopped), and the queue's CurrentIndex was past
    /// the end, so there was no defined recovery path.
    /// </summary>
    private async Task EndOfContextAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("End of context — all advance tiers exhausted");

        // Reset queue cursor to the start so a subsequent "Play" click has
        // a well-defined target (track 0 of the current context) instead of
        // firing from an out-of-bounds index.
        if (_queue.LoadedCount > 0)
            _queue.SkipTo(0);

        // Full stop — position resets to 0 and the engine unloads the track.
        await _proxy.StopAsync(ct);

        // Signal the UI. OriginalContextUri is populated by PlayAsync on every
        // play-with-context; null on internal-queue playthroughs.
        _endOfContextSubject.OnNext(new EndOfContextEvent(
            OriginalContextUri: _originalContextUri,
            AutoplayAttempted: _autoplayTriggered,
            ContextSupportsAutoplay: ContextSupportsAutoplayFor(_originalContextUri)));
    }

    private static bool ContextSupportsAutoplayFor(string? uri)
        => !string.IsNullOrEmpty(uri)
           && !uri.Contains(":station:")
           && !uri.Contains(":radio:")
           && !uri.Contains(":autoplay:")
           && !uri.Contains(":internal:");

    private async Task LoadMoreTracksAsync()
    {
        // PlaybackQueue.NeedsMoreTracks fires near end-of-queue. Two fallbacks
        // in priority order:
        //   1. If we know a next-page URL → paginate within the current context.
        //   2. Otherwise → trigger autoplay (once per context) for bounded,
        //      non-radio contexts. The resulting tracks are appended, the queue
        //      context URI flips to the station URI, and autoplay's own
        //      NextPageUrl takes over subsequent pagination.
        //
        // Semaphore guards against overlapping fires — NeedsMoreTracks can pulse
        // repeatedly as the user skips around near end-of-queue and we don't want
        // to stack concurrent pagination requests.
        if (!await _loadMoreLock.WaitAsync(0))
        {
            _logger?.LogDebug("LoadMoreTracks already in flight — skipping duplicate signal");
            return;
        }

        try
        {
            var pageUrl = _currentNextPageUrl;
            if (!string.IsNullOrEmpty(pageUrl))
            {
                _logger?.LogInformation("Queue needs more tracks — fetching next page {Url}", pageUrl);
                var result = await _contextResolver.LoadNextPageAsync(pageUrl);

                if (result.Tracks.Count > 0)
                {
                    _queue.AppendTracks(result.Tracks);
                    _logger?.LogDebug("Appended {Count} tracks from next page", result.Tracks.Count);
                }

                _currentNextPageUrl = result.NextPageUrl;
                return;
            }

            // No more pages — try autoplay.
            await TryTriggerAutoplayAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadMoreTracksAsync failed");
        }
        finally
        {
            _loadMoreLock.Release();
        }
    }

    /// <summary>
    /// Projects <paramref name="nextTracks"/> into an <c>IQueueItem</c> list,
    /// injecting a <c>QueueDelimiter</c> at the first context→autoplay provider
    /// boundary so remote UIs render the "You've reached the end of X, now
    /// playing similar songs" separator between regular and autoplay tracks.
    /// </summary>
    private static List<IQueueItem> BuildNextQueueItems(IReadOnlyList<QueueTrack> nextTracks)
    {
        var result = new List<IQueueItem>(nextTracks.Count + 1);
        var delimInserted = false;
        string? prevProvider = null;

        for (var i = 0; i < nextTracks.Count; i++)
        {
            var t = nextTracks[i];
            if (!delimInserted
                && t.Provider == "autoplay"
                && prevProvider is not null
                && prevProvider != "autoplay")
            {
                result.Add(new QueueDelimiter(
                    AdvanceAction: "pause",
                    SkipAction: "pause",
                    Provider: "autoplay"));
                delimInserted = true;
            }
            result.Add(t);
            prevProvider = t.Provider;
        }

        return result;
    }

    /// <summary>
    /// Guards + publishes the autoplay Task to <see cref="_pendingAutoplayTask"/>
    /// so end-of-queue handling can await it if the race hits. The actual HTTP
    /// fetch + queue mutation lives in <see cref="DoAutoplayFetchAsync"/>.
    /// </summary>
    private Task TryTriggerAutoplayAsync()
    {
        if (_autoplayTriggered) return Task.CompletedTask;
        if (AutoplayEnabledProvider?.Invoke() == false) return Task.CompletedTask;
        if (string.IsNullOrEmpty(_originalContextUri)) return Task.CompletedTask;

        // Defer when the user has "Add to Queue" items waiting — those play
        // after context exhausts but before autoplay. The flag stays false so
        // a subsequent end-of-queue tier can re-attempt once post-context drains.
        if (_queue.HasPostContextItems)
        {
            _logger?.LogDebug("Autoplay deferred — post-context queue has {Count} item(s)",
                _queue.PostContextQueueCount);
            return Task.CompletedTask;
        }

        // Don't autoplay out of an already-radio/station/autoplay context.
        // Those are infinite and paginate themselves via LoadNextPageAsync.
        if (_originalContextUri.Contains(":station:") ||
            _originalContextUri.Contains(":radio:") ||
            _originalContextUri.Contains(":autoplay:"))
            return Task.CompletedTask;

        _autoplayTriggered = true;
        var task = DoAutoplayFetchAsync();
        _pendingAutoplayTask = task;

        // Clear the reference when the fetch completes so later code doesn't
        // await a long-settled task. ExecuteSynchronously keeps the window
        // between "task finishes" and "field clears" to a single instruction.
        _ = task.ContinueWith(_ => _pendingAutoplayTask = null,
            TaskContinuationOptions.ExecuteSynchronously);
        return task;
    }

    private async Task DoAutoplayFetchAsync()
    {
        var recent = _queue.GetRecentTrackUris(5);

        _logger?.LogInformation("Queue exhausted for {Uri} — prefetching autoplay", _originalContextUri);
        ContextLoadResult autoplay;
        try
        {
            // Single-track seeds (search/click-row with no real context) go
            // through radio-apollo — the desktop client's path. Real context
            // URIs (album/playlist/artist) keep the existing context-resolve
            // autoplay endpoint.
            autoplay = _originalContextUri!.StartsWith("spotify:track:", StringComparison.Ordinal)
                ? await _contextResolver.LoadRadioApolloAutoplayAsync(_originalContextUri!, recent)
                : await _contextResolver.LoadAutoplayAsync(_originalContextUri!, recent);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Autoplay fetch failed for {Uri}", _originalContextUri);
            return;
        }

        if (autoplay.Tracks.Count == 0)
        {
            _logger?.LogDebug("Autoplay returned no tracks for {Uri}", _originalContextUri);
            return;
        }

        // Append the autoplay tracks (each already carries Provider="autoplay"
        // from LoadAutoplayAsync). StampContextUri inside AppendTracks bakes
        // the CURRENT queue context URI into each appended track — at this
        // point we haven't flipped it yet, so we flip FIRST, then append.
        var stationUri = autoplay.ResolvedContextUri ?? _originalContextUri!;
        _queue.UpdateContext(stationUri, isInfinite: true);
        _queue.AppendTracks(autoplay.Tracks);

        // Pagination cursor moves to autoplay's own chain.
        _currentNextPageUrl = autoplay.NextPageUrl;
        _currentContextPageCount = autoplay.PageCount;

        // Swap context metadata to the station's. Server pre-bakes
        // context_description (e.g. "Jason Derulo Radio") and the various
        // session_control_display.* keys — forward verbatim.
        if (autoplay.ContextMetadata is { Count: > 0 } md)
        {
            _currentContextFormatAttributes = md;
            if (md.TryGetValue("context_description", out var desc) && !string.IsNullOrEmpty(desc))
                _currentContextDescription = desc;
            if (md.TryGetValue("image_url", out var img) && !string.IsNullOrEmpty(img))
                _currentContextImageUrl = img;
        }

        _logger?.LogInformation(
            "Autoplay active: station={Station}, tracks={Count}, nextPage={HasNext}",
            stationUri, autoplay.Tracks.Count, autoplay.NextPageUrl != null);
    }

    // ── Remote commands ──

    private void SubscribeToRemoteCommands(ConnectCommandHandler? handler)
    {
        if (handler == null)
        {
            _logger?.LogWarning("Orchestrator: no ConnectCommandHandler — remote commands will NOT be handled");
            return;
        }

        _logger?.LogInformation("Orchestrator: subscribing to remote commands from ConnectCommandHandler");

        _subs.Add(handler.PlayCommands.Subscribe(cmd =>
        {
            RememberSender(cmd.SenderDeviceId);
            _logger?.LogInformation("Remote cmd: play context={Context}, track={Track}, index={Index}, sender={Sender}",
                cmd.ContextUri ?? "<none>", cmd.TrackUri ?? "<none>", cmd.SkipToIndex, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(PlayAsync(cmd), "play");
        }));
        _subs.Add(handler.PauseCommands.Subscribe(cmd =>
        {
            RememberSender(cmd.SenderDeviceId);
            _logger?.LogInformation("Remote cmd: pause (sender={Sender})", cmd.SenderDeviceId ?? "<none>");
            FireAndLog(PauseAsync(), "pause");
        }));
        _subs.Add(handler.ResumeCommands.Subscribe(cmd =>
        {
            RememberSender(cmd.SenderDeviceId);
            _logger?.LogInformation("Remote cmd: resume (sender={Sender})", cmd.SenderDeviceId ?? "<none>");
            FireAndLog(ResumeAsync(), "resume");
        }));
        _subs.Add(handler.SeekCommands.Subscribe(cmd =>
        {
            RememberSender(cmd.SenderDeviceId);
            _logger?.LogInformation("Remote cmd: seek → {Pos}ms (sender={Sender})", cmd.PositionMs, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SeekAsync(cmd.PositionMs), "seek");
        }));
        _subs.Add(handler.SkipNextCommands.Subscribe(cmd =>
        {
            RememberSender(cmd.SenderDeviceId);
            _logger?.LogInformation("Remote cmd: skip_next (sender={Sender})", cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SkipNextAsync(), "skip_next");
        }));
        _subs.Add(handler.SkipPrevCommands.Subscribe(cmd =>
        {
            RememberSender(cmd.SenderDeviceId);
            _logger?.LogInformation("Remote cmd: skip_prev (sender={Sender})", cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SkipPreviousAsync(), "skip_prev");
        }));
        _subs.Add(handler.ShuffleCommands.Subscribe(cmd =>
        {
            RememberSender(cmd.SenderDeviceId);
            _logger?.LogInformation("Remote cmd: shuffle={Enabled} (sender={Sender})", cmd.Enabled, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SetShuffleAsync(cmd.Enabled), "shuffle");
        }));
        _subs.Add(handler.RepeatContextCommands.Subscribe(cmd =>
        {
            RememberSender(cmd.SenderDeviceId);
            _logger?.LogInformation("Remote cmd: repeat_context={Enabled} (sender={Sender})", cmd.Enabled, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SetRepeatContextAsync(cmd.Enabled), "repeat_context");
        }));
        _subs.Add(handler.RepeatTrackCommands.Subscribe(cmd =>
        {
            RememberSender(cmd.SenderDeviceId);
            _logger?.LogInformation("Remote cmd: repeat_track={Enabled} (sender={Sender})", cmd.Enabled, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SetRepeatTrackAsync(cmd.Enabled), "repeat_track");
        }));
    }

    /// <summary>
    /// Bare <c>_ = SomeAsync()</c> swallows faults silently (the Task is GC'd
    /// before TaskScheduler.UnobservedTaskException sees it). Route all
    /// fire-and-forget remote commands through this so failures land in the log
    /// with a label.
    /// </summary>
    private void FireAndLog(System.Threading.Tasks.Task task, string label)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = task.ContinueWith(t =>
            {
                if (t.Exception != null)
                    _logger?.LogError(t.Exception.GetBaseException(), "Orchestrator command '{Label}' failed", label);
            },
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
            | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
    }

    // ── State ──

    private void OnProxyStateChanged(LocalPlaybackState engineState)
    {
        var prev = _stateSubject.Value;
        engineState = EnrichSpotifyVideoState(engineState);
        engineState = EnrichLinkedLocalVideoState(engineState);

        // Diagnostic for the linked-local-video identity-propagation bug:
        // surfaces the URIs we're about to publish whenever the linked-video
        // path is engaged. If trackUri != current.Uri, EnrichLinkedLocalVideoState
        // was not applied (timing / guard bug) and PutState / gabo will leak the
        // local-file URI to Spotify.
        if (_currentLocalVideoAssociationUri is not null)
        {
            _logger?.LogInformation(
                "[linkedvideo] state.publish trackUri={Track} videoActive={Va} assoc={Assoc} current={Cur} albumUri={Au} artistUri={Aru}",
                engineState.TrackUri,
                _videoEngineActive,
                _currentLocalVideoAssociationUri,
                _queue.Current?.Uri,
                engineState.AlbumUri,
                engineState.ArtistUri);
        }

        // Event reporting: track pause/resume edges as PlaybackMetrics interval
        // boundaries so TrackTransitionEvent's ms_played accurately reflects what
        // the user actually heard (excluding paused time).
        OnIntervalEdge(prev, engineState);

        // Log significant state transitions
        if (prev.TrackUri != engineState.TrackUri)
            _logger?.LogInformation("Orchestrator: track changed: {From} → {To}", prev.TrackUri ?? "<none>", engineState.TrackUri ?? "<none>");
        if (prev.IsPlaying != engineState.IsPlaying)
            _logger?.LogInformation("Orchestrator: IsPlaying changed: {From} → {To} (track={Track}, pos={Pos}ms)",
                prev.IsPlaying, engineState.IsPlaying, engineState.TrackUri ?? "<none>", engineState.PositionMs);
        if (prev.IsBuffering != engineState.IsBuffering)
            _logger?.LogDebug("Orchestrator: IsBuffering changed: {From} → {To}", prev.IsBuffering, engineState.IsBuffering);

        // Enrich engine state with queue info — fetch each list once and reuse it
        // for both the typed projection and the IQueueItem cast. The previous code
        // called GetPrevTracks/GetNextTracks twice each, so every state push (which
        // can fire several times per second during normal playback) re-enumerated
        // the queue 4× and allocated 4 lists.
        var prevTracks = _queue.GetPrevTracks();
        var nextTracks = _queue.GetNextTracks();
        var prevRefs = new List<TrackReference>(prevTracks.Count);
        for (int i = 0; i < prevTracks.Count; i++)
        {
            var t = prevTracks[i];
            prevRefs.Add(new TrackReference(t.Uri, t.Uid ?? "", t.AlbumUri, t.ArtistUri, t.IsUserQueued));
        }
        var nextRefs = new List<TrackReference>(nextTracks.Count);
        for (int i = 0; i < nextTracks.Count; i++)
        {
            var t = nextTracks[i];
            nextRefs.Add(new TrackReference(t.Uri, t.Uid ?? "", t.AlbumUri, t.ArtistUri, t.IsUserQueued));
        }

        var enriched = engineState with
        {
            ContextUri = _queue.ContextUri ?? engineState.ContextUri,
            Shuffling = _queue.IsShuffled,
            RepeatingContext = _repeatContext,
            RepeatingTrack = _repeatTrack,
            CurrentIndex = _queue.CurrentIndex,
            PrevTracks = prevRefs,
            NextTracks = nextRefs,
            PrevQueueItems = prevTracks.Cast<IQueueItem>().ToList(),
            NextQueueItems = BuildNextQueueItems(nextTracks),
            QueueRevision = _queue.GetQueueRevision(),
            ContextDescription = _currentContextDescription,
            ContextImageUrl = _currentContextImageUrl,
            ContextFeature = _currentContextFeature,
            ContextTrackCount = _currentContextTrackCount,
            ContextFormatAttributes = _currentContextFormatAttributes,
            ContextPageCount = _currentContextPageCount,
            IsSystemInitiated = _isSystemInitiated,
            VideoManifestId = _currentVideoManifestId,
            // Local video playback (via _localMediaPlayer) sets _videoEngineActive=true
            // but never flips _isSpotifyVideoActive — that flag is reserved for the
            // PlayReady-protected Spotify music-video path. Both paths need to report
            // "video" so PutState's track_player field stays accurate for remote
            // controllers / the web player.
            MediaType = (_isSpotifyVideoActive || _videoEngineActive) ? "video" : "audio",
        };
        _stateSubject.OnNext(enriched);

        // When the current track changed, throw away any in-flight prefetch for the
        // *old* successor and let the prefetch dedup fire afresh for the new successor.
        if (prev.TrackUri != engineState.TrackUri)
        {
            ResetPrefetch();
        }

        MaybeTriggerPrefetch(enriched);
    }

    private LocalPlaybackState EnrichSpotifyVideoState(LocalPlaybackState engineState)
    {
        if (!_isSpotifyVideoActive || _currentVideoPlaybackTarget is not { } target)
            return engineState;

        return engineState with
        {
            TrackUri = !string.IsNullOrWhiteSpace(engineState.TrackUri)
                ? engineState.TrackUri
                : target.VideoTrackUri,
            DurationMs = engineState.DurationMs > 0 ? engineState.DurationMs : target.DurationMs,
            VideoManifestId = target.ManifestId,
            MediaType = "video",
            ExtraMetadata = BuildOriginalTrackMetadata(target)
        };
    }

    private LocalPlaybackState EnrichLinkedLocalVideoState(LocalPlaybackState engineState)
    {
        if (_currentLocalVideoAssociationUri is null || !_videoEngineActive || _isSpotifyVideoActive)
            return engineState;

        var current = _queue.Current;
        if (current is null)
            return engineState;

        return engineState with
        {
            TrackUri = current.Uri,
            TrackUid = current.Uid ?? engineState.TrackUid,
            AlbumUri = current.AlbumUri ?? engineState.AlbumUri,
            ArtistUri = current.ArtistUri ?? engineState.ArtistUri,
            TrackTitle = current.Title ?? engineState.TrackTitle,
            TrackArtist = current.Artist ?? engineState.TrackArtist,
            TrackAlbum = current.Album ?? engineState.TrackAlbum,
            ImageUrl = current.ImageUrl ?? engineState.ImageUrl,
            ImageLargeUrl = current.ImageUrl ?? engineState.ImageLargeUrl,
            ImageXLargeUrl = current.ImageUrl ?? engineState.ImageXLargeUrl,
            MediaType = "video",
            ExtraMetadata = BuildLinkedLocalVideoMetadata(current, _currentLocalVideoAssociationUri)
        };
    }

    /// <summary>
    /// Builds a <see cref="QueueTrack"/> from a UI-supplied <see cref="PageTrack"/>,
    /// hydrating typed fields (Title, Artist, Album, AlbumUri, ArtistUri, ImageUrl,
    /// DurationMs, IsExplicit) from the <c>wavee.*</c> metadata keys that
    /// <c>ConnectCommandExecutor</c> attaches per-track. This lets the orchestrator
    /// carry Spotify identity even on paths that short-circuit Pathfinder metadata
    /// resolution (e.g. the linked-local-video play).
    /// </summary>
    private static QueueTrack BuildQueueTrackFromPageTrack(Wavee.Connect.Commands.PageTrack t)
    {
        var md = t.Metadata;
        if (md is null || md.Count == 0)
            return new QueueTrack(t.Uri, t.Uid);

        string? Get(string key) =>
            md.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

        int? durationMs = null;
        if (md.TryGetValue("wavee.duration_ms", out var durStr)
            && long.TryParse(durStr, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var ms)
            && ms > 0)
        {
            durationMs = (int)System.Math.Min(int.MaxValue, ms);
        }

        bool isExplicit = md.TryGetValue("wavee.is_explicit", out var ex)
                          && string.Equals(ex, "true", System.StringComparison.OrdinalIgnoreCase);

        return new QueueTrack(
            Uri: t.Uri,
            Uid: t.Uid,
            Title: Get("wavee.title"),
            Artist: Get("wavee.artist_name"),
            Album: Get("wavee.album_name"),
            AlbumUri: Get("wavee.album_uri"),
            ArtistUri: Get("wavee.artist_uri"),
            DurationMs: durationMs,
            IsExplicit: isExplicit,
            ImageUrl: Get("wavee.image_url"))
        { Metadata = md };
    }

    private static IReadOnlyDictionary<string, string> BuildLinkedLocalVideoMetadata(
        QueueTrack current,
        string localVideoUri)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wavee.video_track_uri"] = localVideoUri,
            ["wavee.video_source"] = "local"
        };

        Add("wavee.original_track_uri", current.Uri);
        Add("wavee.original_title", current.Title);
        Add("wavee.original_artist_name", current.Artist);
        Add("wavee.original_album_title", current.Album);
        Add("wavee.original_album_uri", current.AlbumUri);
        Add("wavee.original_artist_uri", current.ArtistUri);
        Add("wavee.original_image_url", current.ImageUrl);
        Add("wavee.original_image_large_url", current.ImageUrl);
        Add("wavee.original_image_xlarge_url", current.ImageUrl);
        if (current.DurationMs is { } duration)
            metadata["wavee.original_duration"] = Math.Max(0, duration).ToString();

        return metadata;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                metadata[key] = value;
        }
    }

    private static IReadOnlyDictionary<string, string> BuildOriginalTrackMetadata(SpotifyVideoPlaybackTarget target)
    {
        var original = target.OriginalMetadata;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wavee.original_track_uri"] = target.AudioTrackUri,
            ["wavee.original_duration"] = Math.Max(0, target.OriginalDurationMs).ToString()
        };

        Add("wavee.original_title", original.Title);
        Add("wavee.original_artist_name", original.Artist);
        Add("wavee.original_album_title", original.Album);
        Add("wavee.original_album_uri", original.AlbumUri);
        Add("wavee.original_artist_uri", original.ArtistUri);
        Add("wavee.original_image_url", original.ImageUrl);
        Add("wavee.original_image_small_url", original.ImageSmallUrl);
        Add("wavee.original_image_large_url", original.ImageLargeUrl);
        Add("wavee.original_image_xlarge_url", original.ImageXLargeUrl);
        Add("wavee.video_track_uri", target.VideoTrackUri);
        Add("wavee.video_duration", target.DurationMs.ToString());

        return metadata;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                metadata[key] = value;
        }
    }

    /// <summary>
    /// Fires an AudioKey/head/CDN prefetch for the next track once the current one
    /// is past its halfway point or within ~20 s of ending. Librespot does the same;
    /// without it we request the key reactively at track-finish, racing a burst of
    /// HTTP work and blowing the 5 s timeout under contention.
    /// </summary>
    private void MaybeTriggerPrefetch(LocalPlaybackState state)
    {
        if (!state.IsPlaying) return;
        if (_repeatTrack) return;                             // next target = current
        if (state.DurationMs <= 0) return;                    // unknown duration, can't decide

        var remainingMs = state.DurationMs - state.PositionMs;
        var pastHalf = state.PositionMs >= (long)(state.DurationMs * PrefetchHalfwayFraction);
        var nearEnd = remainingMs > 0 && remainingMs <= PrefetchRemainingMsThreshold;
        if (!pastHalf && !nearEnd) return;

        // Peek next — don't advance. Skip hidden markers / delimiters (they come
        // through QueueTrack only, which is what GetNextTracks returns).
        var nextTracks = _queue.GetNextTracks();
        if (nextTracks.Count == 0) return;
        var target = nextTracks[0];
        if (string.IsNullOrEmpty(target.Uri)) return;

        var prefetchVideo = ShouldPreferSpotifyVideoPlayback();
        if (_lastPrefetchedTrackUri == target.Uri && _lastPrefetchWasVideo == prefetchVideo)
            return;

        _lastPrefetchedTrackUri = target.Uri;
        _lastPrefetchWasVideo = prefetchVideo;
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _prefetchCts, cts);
        try { prev?.Cancel(); prev?.Dispose(); } catch { /* best effort */ }

        _logger?.LogDebug("Orchestrator: prefetching next {Kind} {Uri} (pos={Pos}ms/{Dur}ms)",
            prefetchVideo ? "music video" : "track", target.Uri, state.PositionMs, state.DurationMs);

        _ = Task.Run(async () =>
        {
            try
            {
                if (prefetchVideo)
                {
                    await _trackResolver.PrefetchVideoAsync(target.Uri, cts.Token).ConfigureAwait(false);
                    await TryPrepareVideoPlaybackAsync(target.Uri, cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await _trackResolver.PrefetchAsync(target.Uri, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "Prefetch task fault (non-fatal)"); }
        }, cts.Token);
    }

    /// <summary>
    /// After the manifest is cached, ask the video engine to pre-warm a
    /// session for this URI. The engine may return <c>null</c> if it can't
    /// (e.g. no video provider registered). At most one prepared session is
    /// held; older ones are disposed.
    /// </summary>
    private async Task TryPrepareVideoPlaybackAsync(string audioUri, CancellationToken ct)
    {
        if (_spotifyVideoPlayback is null) return;
        if (string.IsNullOrEmpty(audioUri)) return;

        SpotifyVideoPlaybackTarget? prepTarget;
        try
        {
            prepTarget = await _trackResolver
                .ResolveVideoPlaybackTargetAsync(audioUri, ct: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Video prepare: target resolve failed for {Uri}", audioUri);
            return;
        }

        if (prepTarget is null) return;

        IPreparedVideoSession? prepared;
        try
        {
            prepared = await _spotifyVideoPlayback
                .PrepareAsync(prepTarget, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Video prepare: PrepareAsync failed for {Uri}", prepTarget.VideoTrackUri);
            return;
        }

        if (prepared is null) return;

        // Swap in the new session. Dispose any older one.
        var prev = Interlocked.Exchange(ref _preparedNextVideoSession, prepared);
        if (prev is not null)
        {
            try { await prev.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
        }

        _logger?.LogDebug("Video prepared for next playback: uri={Uri} manifest={Manifest}",
            prepTarget.VideoTrackUri, prepTarget.ManifestId);
    }

    /// <summary>
    /// Invalidate the current prefetch bookmark so the next state update can
    /// kick off a fresh prefetch for whatever is now the successor track.
    /// Also cancels any in-flight prefetch — the network work would be wasted
    /// if the user skipped or the queue layout changed.
    /// </summary>
    private void ResetPrefetch()
    {
        _lastPrefetchedTrackUri = null;
        _lastPrefetchWasVideo = false;
        var cts = Interlocked.Exchange(ref _prefetchCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* best effort */ }
        }
        var prepared = Interlocked.Exchange(ref _preparedNextVideoSession, null);
        if (prepared is not null)
        {
            // Fire-and-forget: caller is on a hot state-update path.
            _ = DisposePreparedVideoSessionAsync(prepared);
        }
    }

    private async Task DisposePreparedVideoSessionAsync(IPreparedVideoSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Disposing prepared video session"); }
    }

    private void PublishQueueState()
    {
        // Re-enrich current state with latest queue info
        var current = _stateSubject.Value;
        OnProxyStateChanged(current);
    }

    // ── Disposal ──

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Event reporting: flush any in-flight track as endplay before tearing
        // down the subscriptions. EventService's own DisposeAsync (driven by
        // Session) drains its async worker queue separately.
        DispatchTrackTransition(Wavee.Connect.Events.PlaybackReason.EndPlay, _stateSubject.Value.PositionMs);

        ResetPrefetch();
        _subs.Dispose();
        _stateSubject.Dispose();
        _errorSubject.Dispose();
        _endOfContextSubject.Dispose();
    }
}

/// <summary>
/// Published by <see cref="PlaybackOrchestrator.EndOfContext"/> when playback
/// reaches end-of-context and every advance / autoplay tier has failed. The UI
/// layer subscribes and shows a contextual notification.
/// <para/>
/// <see cref="ContextSupportsAutoplay"/> reflects whether the original context
/// URI is of a kind that Spotify generates autoplay recommendations for
/// (albums, playlists, artist pages) versus kinds where autoplay is meaningless
/// (radio / station / internal queue — those are already autoplay or have no
/// source).
/// </summary>
public sealed record EndOfContextEvent(
    string? OriginalContextUri,
    bool AutoplayAttempted,
    bool ContextSupportsAutoplay);
