using System.Reactive.Linq;
using System.Reactive.Subjects;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Protocol;
using Wavee.Core.Audio;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Utilities;
using Wavee.Protocol.Metadata;
using Wavee.Protocol.Player;

namespace Wavee.Connect;

/// <summary>
/// Manages playback state with support for both remote (cluster) and local (IPlaybackEngine) sources.
/// Provides reactive observables for state changes with smart change detection.
/// </summary>
/// <remarks>
/// WHY: Unified API for playback state regardless of source (remote or local).
///
/// MODES:
/// 1. **Remote-only** (default): Reads cluster updates only
///    - Use primary constructor: `new PlaybackStateManager(dealerClient)`
///    - Works immediately without audio pipeline
///
/// 2. **Bidirectional** (future): Reads cluster AND publishes local state
///    - Use secondary constructor with IPlaybackEngine, SpClient, ISession
///    - Requires audio pipeline implementation
///
/// USAGE (Remote-only - works now):
/// <code>
/// var manager = new PlaybackStateManager(dealerClient, logger);
///
/// // Get current state
/// var title = manager.CurrentState.Track?.Title;
///
/// // Subscribe to all changes
/// manager.StateChanges.Subscribe(state =>
///     Console.WriteLine($"Track: {state.Track?.Title}, Status: {state.Status}"));
///
/// // Subscribe to specific changes
/// manager.TrackChanged.Subscribe(state =>
///     Console.WriteLine($"New track: {state.Track?.Title}"));
/// </code>
///
/// USAGE (Bidirectional - future when IPlaybackEngine exists):
/// <code>
/// var engine = new AudioPipeline(/* ... */);
/// var manager = new PlaybackStateManager(dealerClient, engine, spClient, session, logger);
///
/// // Same API, but now publishes local state too
/// // Commands automatically forwarded to engine
/// // Engine state changes automatically published to Spotify
/// </code>
/// </remarks>
public sealed class PlaybackStateManager : IAsyncDisposable
{
    private readonly DealerClient _dealerClient;
    private IPlaybackEngine? _playbackEngine;  // Null in remote-only mode
    private SpClient? _spClient;                // Null in remote-only mode
    private ISession? _session;                 // Null in remote-only mode
    private IExtendedMetadataClient? _metadataClient; // For enriching incomplete cluster metadata
    private CancellationTokenSource? _enrichCts;
    private readonly ILogger? _logger;

    // State
    private readonly BehaviorSubject<PlaybackState> _stateSubject;
    private PlaybackState _currentState;
    // Serializes _currentState mutations and _stateSubject.OnNext so concurrent
    // cluster / local / enrichment updates can't interleave (stale-overwrite race).
    private readonly object _stateLock = new();
    private bool _isLocalPlaybackActive;
    private bool _proxyOnlyMode;
    private string? _connectionId;
    private uint _messageId;
    private ulong _playbackStartedAt;  // Timestamp when playback started (for has_been_playing_for_ms)

    // Subscriptions
    private readonly IDisposable _clusterSubscription;
    private IDisposable? _localPlaybackSubscription;
    private IDisposable? _connectionIdSubscription;

    // State publisher (bidirectional mode only)
    private AsyncWorker<PutStateRequest>? _statePublisher;

    // PutState debounce — position-only updates are debounced, critical changes flush immediately
    private CancellationTokenSource? _debounceCts;
    private PlaybackState? _pendingState;
    private readonly object _debounceLock = new();
    private const int DebounceMs = 750;

    // Publish diagnostics (runtime counters)
    private const long PublisherHealthLogIntervalMs = 30000;
    private long _publishSubmittedCount;
    private long _publishDroppedCount;
    private long _publishSentCount;
    private long _lastPublisherHealthLogAtMs;
    private long _lastPublisherHealthSubmittedCount;
    private long _lastPublisherHealthDroppedCount;
    private long _lastPublisherHealthSentCount;

    // Debounce diagnostics — visibility into the flush ↔ debounce race
    private long _debounceFiredCount;
    private long _debounceCancelledCount;
    private long _flushCancelledPendingCount;

    // Cluster pipeline diagnostics
    private long _clusterUpdateCount;
    private long _clusterUpdateSkippedNoChangesCount;
    private long _clusterUpdateSkippedWeAreActiveCount;

    private bool _disposed;

    /// <summary>
    /// Observable stream of all playback state changes.
    /// Emits whenever any aspect of playback state changes.
    /// </summary>
    public IObservable<PlaybackState> StateChanges => _stateSubject.AsObservable();

    /// <summary>
    /// Observable stream of track changes only.
    /// Emits when track URI changes (different song).
    /// </summary>
    public IObservable<PlaybackState> TrackChanged =>
        _stateSubject.Where(s => s.Changes.HasFlag(Connect.StateChanges.Track));

    /// <summary>
    /// Observable stream of playback status changes only.
    /// Emits when play/pause/buffering/stopped status changes.
    /// </summary>
    public IObservable<PlaybackState> PlaybackStatusChanged =>
        _stateSubject.Where(s => s.Changes.HasFlag(Connect.StateChanges.Status));

    /// <summary>
    /// Observable stream of position changes only.
    /// Emits when playback position changes significantly (> 1 second).
    /// </summary>
    public IObservable<PlaybackState> PositionChanged =>
        _stateSubject.Where(s => s.Changes.HasFlag(Connect.StateChanges.Position));

    /// <summary>
    /// Observable stream of context changes only.
    /// Emits when playlist/album context changes.
    /// </summary>
    public IObservable<PlaybackState> ContextChanged =>
        _stateSubject.Where(s => s.Changes.HasFlag(Connect.StateChanges.Context));

    /// <summary>
    /// Observable stream of playback options changes only.
    /// Emits when shuffle/repeat settings change.
    /// </summary>
    public IObservable<PlaybackState> OptionsChanged =>
        _stateSubject.Where(s => s.Changes.HasFlag(Connect.StateChanges.Options));

    /// <summary>
    /// Observable stream of active device changes only.
    /// Emits when active device in cluster changes.
    /// </summary>
    public IObservable<PlaybackState> ActiveDeviceChanged =>
        _stateSubject.Where(s => s.Changes.HasFlag(Connect.StateChanges.ActiveDevice));

    /// <summary>
    /// Observable stream of state source changes only.
    /// Emits when source switches between cluster and local.
    /// </summary>
    public IObservable<PlaybackState> SourceChanged =>
        _stateSubject.Where(s => s.Changes.HasFlag(Connect.StateChanges.Source));

    /// <summary>
    /// Gets the current playback state (synchronous access).
    /// </summary>
    public PlaybackState CurrentState => _currentState;

    /// <summary>
    /// Gets whether manager is in bidirectional mode (has IPlaybackEngine).
    /// </summary>
    public bool IsBidirectional => _playbackEngine != null;

    /// <summary>
    /// Gets whether local playback is currently active.
    /// </summary>
    public bool IsLocalPlaybackActive => _isLocalPlaybackActive;

    /// <summary>
    /// Smart resume: resumes if engine has a track loaded, otherwise loads the
    /// ghost track from cluster state and starts fresh playback.
    /// </summary>
    /// <summary>
    /// Resumes playback. If the engine has a track loaded, resumes normally.
    /// Otherwise loads the ghost track from cluster state.
    /// </summary>
    /// <param name="userInitiated">True when the user explicitly pressed play
    /// (skips freshness and paused-state guards).</param>
    public async Task ResumeAsync(bool userInitiated = false)
    {
        if (_playbackEngine == null) return;

        // Engine has a track loaded → normal resume
        if (!string.IsNullOrEmpty(_playbackEngine.CurrentState.TrackUri))
        {
            await _playbackEngine.ResumeAsync();
            return;
        }

        // Ghost state: engine empty but cluster has track/context → start fresh
        if (_currentState.Track != null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var stateAge = _currentState.Timestamp > 0
                ? now - _currentState.Timestamp
                : long.MaxValue;

            if (!userInitiated)
            {
                // Auto-resume guards: don't auto-play stale or paused state
                if (stateAge > 30_000)
                {
                    _logger?.LogInformation("Ghost resume skipped: cluster state is {Age}s old (track: {Track})",
                        stateAge / 1000, _currentState.Track.Title);
                    return;
                }

                if (_currentState.Status != PlaybackStatus.Playing)
                {
                    _logger?.LogInformation("Ghost resume skipped: remote state is {Status} (track: {Track})",
                        _currentState.Status, _currentState.Track.Title);
                    return;
                }
            }

            // For auto-resume, compensate for elapsed time; for user-initiated, use stored position
            var resumePosition = _currentState.PositionMs;
            if (!userInitiated && stateAge < 30_000)
            {
                resumePosition += stateAge;
                if (_currentState.DurationMs > 0 && resumePosition >= _currentState.DurationMs)
                {
                    _logger?.LogInformation("Ghost resume skipped: track would have ended (elapsed={Elapsed}ms, duration={Duration}ms)",
                        stateAge, _currentState.DurationMs);
                    return;
                }
            }

            _logger?.LogInformation("Ghost resume: loading {Track} from cluster state (userInitiated={UserInitiated}, position={Position}ms)",
                _currentState.Track.Title, userInitiated, resumePosition);

            var playCommand = new Commands.PlayCommand
            {
                Endpoint = "play",
                MessageIdent = "ghost-resume",
                MessageId = 0,
                SenderDeviceId = _session!.Config.DeviceId,
                Key = "ghost-resume",
                TrackUri = _currentState.Track.Uri,
                TrackUid = _currentState.Track.Uid,
                ContextUri = _currentState.ContextUri ?? _currentState.Track.Uri,
                PositionMs = resumePosition > 0 ? resumePosition : null,
                SkipToIndex = _currentState.CurrentIndex > 0 ? _currentState.CurrentIndex : null,
            };
            await _playbackEngine.PlayAsync(playCommand);
        }
    }

    /// <summary>
    /// Initializes PlaybackStateManager in **remote-only mode**.
    /// Reads cluster updates only, does not publish local state.
    /// </summary>
    /// <param name="dealerClient">DealerClient for receiving cluster updates.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public PlaybackStateManager(DealerClient dealerClient, ILogger? logger = null)
    {
        _dealerClient = dealerClient ?? throw new ArgumentNullException(nameof(dealerClient));
        _logger = logger;

        // Initialize with empty state
        _currentState = PlaybackState.Empty;
        _stateSubject = new BehaviorSubject<PlaybackState>(_currentState);

        // Subscribe to cluster updates (both dealer messages and PUT state responses)
        _clusterSubscription = _dealerClient.Messages
            .Where(m => m.Uri.StartsWith("hm://connect-state/v1/cluster", StringComparison.OrdinalIgnoreCase) ||
                       m.Uri.StartsWith("hm://connect-state/v1/put-state-response", StringComparison.OrdinalIgnoreCase))
            .Subscribe(
                onNext: OnClusterUpdate,
                onError: ex => _logger?.LogError(ex, "Error in cluster update subscription"));

        _logger?.LogDebug("PlaybackStateManager initialized (remote-only mode)");
        _logger?.LogTrace("Subscribed to cluster updates and PUT state responses");
    }

    /// <summary>
    /// Initializes PlaybackStateManager in **bidirectional mode**.
    /// Reads cluster updates AND publishes local playback state.
    /// </summary>
    /// <param name="dealerClient">DealerClient for receiving cluster updates.</param>
    /// <param name="playbackEngine">Local playback engine.</param>
    /// <param name="spClient">SpClient for publishing state.</param>
    /// <param name="session">Session for device info and connection ID.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public PlaybackStateManager(
        DealerClient dealerClient,
        IPlaybackEngine playbackEngine,
        SpClient spClient,
        ISession session,
        ILogger? logger = null)
        : this(dealerClient, logger)  // Chain to primary constructor for cluster subscription
    {
        _playbackEngine = playbackEngine ?? throw new ArgumentNullException(nameof(playbackEngine));
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        // Create state publisher worker
        _statePublisher = new AsyncWorker<PutStateRequest>(
            "PlaybackStatePublisher",
            PublishStateAsync,
            _logger,
            capacity: 10);  // Bounded queue for backpressure

        // Subscribe to connection ID for publishing
        _connectionIdSubscription = _dealerClient.ConnectionId
            .Where(id => id != null)
            .Subscribe(id =>
            {
                var previous = _connectionId;
                _connectionId = id;
                _logger?.LogInformation(
                    "ConnectionId {Transition}: prev={Previous}, next={Next}",
                    previous == null ? "acquired" : "changed",
                    previous ?? "<null>",
                    id);
            });

        // Subscribe to local playback state changes
        _localPlaybackSubscription = _playbackEngine.StateChanges
            .Subscribe(OnLocalPlaybackStateChanged);

        _logger?.LogInformation("PlaybackStateManager initialized (bidirectional mode)");
        _logger?.LogTrace("Subscribed to local playback engine state changes");
    }

    /// <summary>
    /// Enables bidirectional mode after construction.
    /// Call this after creating an IPlaybackEngine to publish local state to Spotify.
    /// </summary>
    /// <param name="playbackEngine">Local playback engine implementing IPlaybackEngine.</param>
    /// <param name="spClient">SpClient for publishing state to Spotify.</param>
    /// <param name="session">Session for device info and connection ID.</param>
    /// <param name="suppressClusterUpdates">
    /// When true, cluster updates from the local dealer are ignored.
    /// Use when the engine is an IPC proxy whose state already includes cluster data from the AudioHost.
    /// </param>
    public void EnableBidirectionalMode(
        IPlaybackEngine playbackEngine,
        SpClient spClient,
        ISession session,
        bool suppressClusterUpdates = false)
    {
        // Engine replacement (e.g., audio process restart) — swap subscription only
        if (_playbackEngine != null)
        {
            _localPlaybackSubscription?.Dispose();
            _playbackEngine = playbackEngine ?? throw new ArgumentNullException(nameof(playbackEngine));
            _localPlaybackSubscription = _playbackEngine.StateChanges
                .Subscribe(OnLocalPlaybackStateChanged);
            _proxyOnlyMode = suppressClusterUpdates;
            _logger?.LogInformation("PlaybackStateManager: engine replaced (proxyOnly={ProxyOnly})", _proxyOnlyMode);
            return;
        }

        _playbackEngine = playbackEngine ?? throw new ArgumentNullException(nameof(playbackEngine));
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _proxyOnlyMode = suppressClusterUpdates;

        // Create state publisher worker
        _statePublisher = new AsyncWorker<PutStateRequest>(
            "PlaybackStatePublisher",
            PublishStateAsync,
            _logger,
            capacity: 10);  // Bounded queue for backpressure

        // Subscribe to connection ID for publishing
        _connectionIdSubscription = _dealerClient.ConnectionId
            .Where(id => id != null)
            .Subscribe(id =>
            {
                var previous = _connectionId;
                _connectionId = id;
                _logger?.LogInformation(
                    "ConnectionId {Transition}: prev={Previous}, next={Next}",
                    previous == null ? "acquired" : "changed",
                    previous ?? "<null>",
                    id);
            });

        // Subscribe to local playback state changes
        _localPlaybackSubscription = _playbackEngine.StateChanges
            .Subscribe(OnLocalPlaybackStateChanged);

        _logger?.LogInformation("PlaybackStateManager: bidirectional mode enabled (proxyOnly={ProxyOnly})", _proxyOnlyMode);
    }

    /// <summary>
    /// Sets the metadata client for enriching incomplete cluster track metadata.
    /// Call after IExtendedMetadataClient is available (post-session-connect).
    /// </summary>
    public void SetMetadataClient(IExtendedMetadataClient metadataClient)
    {
        _metadataClient = metadataClient;
        _logger?.LogDebug("PlaybackStateManager: metadata client set for track enrichment");
    }

    /// <summary>
    /// Handles cluster update messages from dealer (ClusterUpdate) and PUT state responses (Cluster).
    /// </summary>
    private void OnClusterUpdate(DealerMessage message)
    {
        var clusterSeq = Interlocked.Increment(ref _clusterUpdateCount);
        var isPutStateResponse = message.Uri.StartsWith("hm://connect-state/v1/put-state-response", StringComparison.OrdinalIgnoreCase);
        var isSelfEcho = message.Headers != null && message.Headers.TryGetValue("X-Wavee-Echo", out var echoTag) && echoTag == "self";

        _logger?.LogTrace("[cluster#{Seq}] Received cluster message: uri={Uri}, payloadSize={Size}, isPutStateResponse={IsPutStateResponse}, selfEcho={SelfEcho}",
            clusterSeq, message.Uri, message.Payload.Length, isPutStateResponse, isSelfEcho);

        // Parse the cluster proto BEFORE any suppression logic — even in suppressed
        // modes we still want to propagate the Spotify Connect device roster to the UI.
        Cluster? cluster;
        try
        {
            if (!TryParseClusterMessage(message, isPutStateResponse, out cluster) || cluster == null)
                return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse cluster update");
            return;
        }

        // Always propagate the Connect device roster — orthogonal to local playback state.
        TryEmitConnectDeviceUpdate(cluster, clusterSeq);

        // Proxy-only mode: AudioHost owns playback state, so skip the full state merge.
        if (_proxyOnlyMode)
        {
            _logger?.LogTrace("[cluster#{Seq}] Ignoring cluster playback state (proxy-only mode, AudioHost is authoritative): uri={Uri}, echo={Echo}",
                clusterSeq, message.Uri, isSelfEcho);
            return;
        }

        try
        {
            // Avoid expensive protobuf JSON serialization unless trace logging is enabled.
            if (cluster.PlayerState != null)
            {
                _logger?.LogDebug(
                    "Incoming PlayerState: track={TrackUri}, position={Position}, duration={Duration}, isPlaying={IsPlaying}",
                    cluster.PlayerState.Track?.Uri,
                    cluster.PlayerState.Position,
                    cluster.PlayerState.Duration,
                    cluster.PlayerState.IsPlaying);

                if (_logger?.IsEnabled(LogLevel.Trace) == true)
                {
                    _logger.LogTrace("Incoming PlayerState → {Json}", JsonFormatter.Default.Format(cluster.PlayerState));
                }
            }

            // Ignore cluster updates if we're the active device and in bidirectional mode
            if (IsBidirectional &&
                _isLocalPlaybackActive &&
                cluster.ActiveDeviceId == _session?.Config.DeviceId)
            {
                Interlocked.Increment(ref _clusterUpdateSkippedWeAreActiveCount);
                _logger?.LogTrace("[cluster#{Seq}] Ignoring cluster update (we are active device): cluster.active={ActiveDevice}, us={DeviceId}, localActive={LocalActive}",
                    clusterSeq, cluster.ActiveDeviceId, _session?.Config.DeviceId, _isLocalPlaybackActive);
                return;
            }

            // Snapshot BEFORE applying the new state for diff logging
            var prevSnapshot = FormatStateSnapshot(_currentState);

            // Convert to domain model with change detection
            var newState = PlaybackStateHelpers.ClusterToPlaybackState(cluster, _currentState, _logger);

            // Only update if something actually changed
            if (newState.Changes == Connect.StateChanges.None)
            {
                Interlocked.Increment(ref _clusterUpdateSkippedNoChangesCount);
                _logger?.LogTrace("[cluster#{Seq}] No changes detected, skipping update (state={State})", clusterSeq, prevSnapshot);
                return;
            }

            _logger?.LogDebug(
                "[cluster#{Seq}] State transition (cluster): changes={Changes}{EchoTag} prev=[{Prev}] next=[{Next}]",
                clusterSeq,
                newState.Changes,
                isSelfEcho ? " [self-echo]" : string.Empty,
                prevSnapshot,
                FormatStateSnapshot(newState));

            // If another device became active in bidirectional mode, stop local playback immediately
            if (IsBidirectional &&
                _isLocalPlaybackActive &&
                newState.Changes.HasFlag(Connect.StateChanges.ActiveDevice) &&
                newState.ActiveDeviceId != _session?.Config.DeviceId)
            {
                _logger?.LogInformation("Another device became active, stopping local playback");
                _isLocalPlaybackActive = false;
                _playbackStartedAt = 0;

                // Stop local playback — observe the task so exceptions are not silently swallowed
                _ = _playbackEngine!.StopAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _logger?.LogError(t.Exception?.InnerException, "Failed to stop local playback after device takeover");
                    else
                        _logger?.LogDebug("Local playback stopped due to device takeover");
                }, TaskContinuationOptions.ExecuteSynchronously);
            }

            // Update state
            lock (_stateLock)
            {
                _currentState = newState;
                _stateSubject.OnNext(newState);
            }

            // If track metadata is incomplete, fetch from API and re-emit enriched state
            if (newState.Changes.HasFlag(Connect.StateChanges.Track) && _metadataClient != null)
            {
                var track = newState.Track;
                if (track != null && (track.Title == null || track.Artist == null ||
                    track.ArtistUri == null || track.AlbumUri == null || track.ImageUrl == null))
                {
                    _logger?.LogDebug("Incomplete track metadata — enriching from API for {Uri}", track.Uri);
                    _ = EnrichTrackMetadataAsync(track.Uri);
                }
            }

            _logger?.LogTrace("State update published to subscribers");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to process cluster update");
        }
    }

    /// <summary>
    /// Parses a dealer message as either a Cluster (PUT state response) or a ClusterUpdate
    /// (regular dealer message) and returns the underlying <see cref="Cluster"/> protobuf.
    /// </summary>
    private bool TryParseClusterMessage(DealerMessage message, bool isPutStateResponse, out Cluster? cluster)
    {
        cluster = null;

        if (isPutStateResponse)
        {
            if (!PlaybackStateHelpers.TryParseCluster(message, out cluster) || cluster == null)
            {
                _logger?.LogWarning("Failed to parse Cluster from PUT state response");
                return false;
            }
            _logger?.LogTrace("Cluster parsed from PUT state response: activeDevice={ActiveDevice}, hasPlayerState={HasPlayerState}",
                cluster.ActiveDeviceId, cluster.PlayerState != null);
            return true;
        }

        if (!PlaybackStateHelpers.TryParseClusterUpdate(message, out var clusterUpdate) || clusterUpdate == null)
        {
            _logger?.LogWarning("Failed to parse ClusterUpdate from dealer message");
            return false;
        }
        cluster = clusterUpdate.Cluster;
        _logger?.LogTrace("ClusterUpdate parsed: activeDevice={ActiveDevice}, hasPlayerState={HasPlayerState}",
            cluster.ActiveDeviceId, cluster.PlayerState != null);
        return true;
    }

    /// <summary>
    /// Emits a minimal "devices-only" state update if the Spotify Connect device roster
    /// has changed. This runs on every cluster update regardless of suppression mode,
    /// so the UI can track remote devices even when the main playback-state update is
    /// skipped (proxy-only mode, we-are-active mode).
    /// </summary>
    private void TryEmitConnectDeviceUpdate(Cluster cluster, long clusterSeq)
    {
        var newDevices = PlaybackStateHelpers.ExtractConnectDevices(cluster);
        if (DeviceListsEquivalent(_currentState.AvailableConnectDevices, newDevices))
            return;

        lock (_stateLock)
        {
            // Re-check inside the lock in case the roster was already applied by a
            // racing update (cluster, local, enrichment).
            if (DeviceListsEquivalent(_currentState.AvailableConnectDevices, newDevices))
                return;

            var devicesState = _currentState with
            {
                AvailableConnectDevices = newDevices,
                Changes = Connect.StateChanges.ActiveDevice,
            };
            _currentState = devicesState;
            _stateSubject.OnNext(devicesState);
        }
        _logger?.LogDebug("[cluster#{Seq}] Emitted Connect device list update: {Count} devices",
            clusterSeq, newDevices.Count);
    }

    private static bool DeviceListsEquivalent(
        IReadOnlyList<ConnectDevice> a,
        IReadOnlyList<ConnectDevice> b)
    {
        if (a.Count != b.Count) return false;
        // Protobuf MapField iteration order is not guaranteed stable across updates,
        // so compare by id instead of by position.
        var byId = new Dictionary<string, ConnectDevice>(a.Count, StringComparer.Ordinal);
        foreach (var d in a) byId[d.DeviceId] = d;
        foreach (var d in b)
        {
            if (!byId.TryGetValue(d.DeviceId, out var prev)) return false;
            if (prev.IsActive != d.IsActive) return false;
            if (prev.Type != d.Type) return false;
            if (!string.Equals(prev.Name, d.Name, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// Handles local playback state changes from IPlaybackEngine.
    /// Only active in bidirectional mode.
    /// </summary>
    private void OnLocalPlaybackStateChanged(LocalPlaybackState localState)
    {
        try
        {
            // Only log the full incoming snapshot when upstream thinks something
            // changed. During steady-state playback AudioHost sends a position tick
            // every ~2 s with UpstreamChanges=None, and logging every one of those
            // plus the "no changes detected, skipping" line (three entries per tick)
            // drowns useful state transitions in noise.
            var hasUpstreamChanges = localState.UpstreamChanges is not null
                                     && localState.UpstreamChanges != Connect.StateChanges.None;
            if (hasUpstreamChanges)
            {
                _logger?.LogTrace(
                    "Received local playback state: track={Track}, pos={Pos}ms/{Dur}ms, isPlaying={IsPlaying}, isPaused={IsPaused}, isBuffering={IsBuffering}, source={Source}, activeDev={ActiveDev}, upstreamChanges={UpstreamChanges}",
                    localState.TrackUri ?? "<none>",
                    localState.PositionMs,
                    localState.DurationMs,
                    localState.IsPlaying,
                    localState.IsPaused,
                    localState.IsBuffering,
                    localState.Source,
                    localState.ActiveDeviceId ?? "<none>",
                    localState.UpstreamChanges);
            }

            // Snapshot BEFORE applying the new state for diff logging
            var prevSnapshot = FormatStateSnapshot(_currentState);

            // Convert to domain model
            var newState = PlaybackStateHelpers.LocalToPlaybackState(
                localState,
                _currentState,
                _session!.Config.DeviceId);

            // Only update if something actually changed. No log here — the skip is
            // the steady-state behavior and the upstream-changes log above already
            // tells us which ticks were worth considering.
            if (newState.Changes == Connect.StateChanges.None)
            {
                return;
            }

            var isLocalSource = newState.Source == StateSource.Local;

            // Don't let the engine's idle state override remote cluster state.
            // Until we're actively playing locally, the engine's "Stopped" is meaningless.
            if (isLocalSource && !_isLocalPlaybackActive && newState.Status != PlaybackStatus.Playing)
            {
                _logger?.LogTrace("Ignoring local idle state — not the active local device");
                return;
            }

            _logger?.LogDebug(
                "State transition (local): changes={Changes} prev=[{Prev}] next=[{Next}] localActive={LocalActive} playbackStartedAt={PlaybackStartedAt}",
                newState.Changes,
                prevSnapshot,
                FormatStateSnapshot(newState),
                _isLocalPlaybackActive,
                _playbackStartedAt);

            // Reset playback started timestamp on track change or stop
            if (newState.Changes.HasFlag(Connect.StateChanges.Track) ||
                newState.Status == PlaybackStatus.Stopped)
            {
                _playbackStartedAt = 0;
            }

            // Mark as active if playing locally
            if (isLocalSource && newState.Status == PlaybackStatus.Playing && !_isLocalPlaybackActive)
            {
                _logger?.LogInformation("Local playback started, becoming active device");
                _isLocalPlaybackActive = true;
            }

            // Cluster-sourced snapshots from proxy should not claim local playback ownership.
            if (!isLocalSource &&
                !string.IsNullOrEmpty(newState.ActiveDeviceId) &&
                newState.ActiveDeviceId != _session?.Config.DeviceId &&
                _isLocalPlaybackActive)
            {
                _logger?.LogDebug("Proxy cluster state indicates another active device ({DeviceId}); clearing local-active flag", newState.ActiveDeviceId);
                _isLocalPlaybackActive = false;
                _playbackStartedAt = 0;
            }

            // Update state
            lock (_stateLock)
            {
                _currentState = newState;
                _stateSubject.OnNext(newState);
            }

            // Publish to Spotify. The DetectChanges helper already filters out natural
            // position progression (via the nominalDelta threshold in DetectChanges), so a
            // StateChanges.Position flag here means a real seek, which the server needs to
            // know about. Options/Volume/Queue are also user-intent changes that must be
            // published. Only genuine "position-only on an infinite stream" (e.g. live
            // radio) can be skipped, because for infinite streams Spotify has no duration
            // to compute against.
            if (_isLocalPlaybackActive && isLocalSource)
            {
                var isPositionOnlyChange = newState.Changes == Connect.StateChanges.Position;
                var isInfiniteStream = newState.DurationMs == 0;

                if (isPositionOnlyChange && isInfiniteStream)
                {
                    _logger?.LogTrace("Publish decision: SKIP (position-only on infinite stream) changes={Changes}", newState.Changes);
                }
                else
                {
                    // Critical changes (track, status, device, context) flush immediately.
                    // Other meaningful changes (seek, shuffle/repeat, volume, queue) also
                    // need to reach Spotify — otherwise remote UIs and other devices stay
                    // out of sync with what's happening locally.
                    var isCritical = newState.Changes.HasFlag(Connect.StateChanges.Track)
                        || newState.Changes.HasFlag(Connect.StateChanges.Status)
                        || newState.Changes.HasFlag(Connect.StateChanges.ActiveDevice)
                        || newState.Changes.HasFlag(Connect.StateChanges.Context);

                    if (isCritical)
                    {
                        _logger?.LogDebug("Publish decision: FLUSH (critical) changes={Changes}", newState.Changes);
                        FlushAndPublishState(newState);
                    }
                    else
                    {
                        // Non-critical changes (seek, shuffle/repeat, volume) — debounce so
                        // rapid bursts of position or options updates collapse into one PUT.
                        _logger?.LogDebug("Publish decision: DEBOUNCE (non-critical) changes={Changes}", newState.Changes);
                        DebouncedPublishState(newState);
                    }
                }
            }
            else
            {
                _logger?.LogTrace("Publish decision: SKIP (localActive={LocalActive}, isLocalSource={IsLocalSource})",
                    _isLocalPlaybackActive, isLocalSource);
            }

            _logger?.LogTrace("Local state update published to subscribers");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to process local playback state");
        }
    }

    /// <summary>
    /// Cancels any pending debounce and publishes state immediately.
    /// Used for critical changes (track, status, device, context).
    /// </summary>
    private void FlushAndPublishState(PlaybackState state)
    {
        PlaybackState? cancelled = null;
        lock (_debounceLock)
        {
            if (_debounceCts != null)
            {
                cancelled = _pendingState;
                Interlocked.Increment(ref _flushCancelledPendingCount);
                _debounceCts.Cancel();
                _debounceCts.Dispose();
                _debounceCts = null;
                _pendingState = null;
            }
        }

        if (cancelled != null)
        {
            _logger?.LogDebug(
                "Flush cancelled pending debounce: cancelledState=[{Cancelled}] newState=[{New}] (flushCancelledTotal={Total})",
                FormatStateSnapshot(cancelled),
                FormatStateSnapshot(state),
                Interlocked.Read(ref _flushCancelledPendingCount));
        }
        else
        {
            _logger?.LogTrace("Flush (no pending debounce): state=[{State}]", FormatStateSnapshot(state));
        }

        PublishLocalState(state);
    }

    /// <summary>
    /// Debounces non-critical state updates (position-only) to reduce PutState flooding.
    /// Waits DebounceMs — if no new update arrives, publishes. If a new update arrives, resets the timer.
    /// </summary>
    private void DebouncedPublishState(PlaybackState state)
    {
        bool resetExisting;
        lock (_debounceLock)
        {
            resetExisting = _debounceCts != null;
            if (resetExisting)
            {
                Interlocked.Increment(ref _debounceCancelledCount);
                // Cancel only — do not Dispose here. Task.Delay/CancellationTokenRegistration
                // may still be unwinding on another thread and disposing synchronously can
                // race into ObjectDisposedException. GC will reclaim the CTS once the
                // cancelled Task.Delay continuation finishes.
                _debounceCts!.Cancel();
            }
            _pendingState = state;
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _logger?.LogTrace(
                "Debounce {Action}: state=[{State}] windowMs={DebounceMs} (debounceCancelledTotal={Cancelled})",
                resetExisting ? "RESET" : "START",
                FormatStateSnapshot(state),
                DebounceMs,
                Interlocked.Read(ref _debounceCancelledCount));

            _ = Task.Delay(DebounceMs, token).ContinueWith(_ =>
            {
                PlaybackState? toPublish;
                lock (_debounceLock)
                {
                    toPublish = _pendingState;
                    _pendingState = null;
                }
                if (toPublish != null)
                {
                    Interlocked.Increment(ref _debounceFiredCount);
                    _logger?.LogTrace("Debounce FIRE: publishing state=[{State}] (debounceFiredTotal={Fired})",
                        FormatStateSnapshot(toPublish),
                        Interlocked.Read(ref _debounceFiredCount));
                    PublishLocalState(toPublish);
                }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }

    /// <summary>
    /// Publishes local playback state to Spotify via PutState.
    /// </summary>
    private void PublishLocalState(PlaybackState state)
    {
        if (_connectionId == null)
        {
            _logger?.LogWarning(
                "DROPPED PutState: connection ID not available yet — state will NOT reach Spotify. state=[{State}], localActive={LocalActive}",
                FormatStateSnapshot(state),
                _isLocalPlaybackActive);
            return;
        }

        try
        {
            var now = (ulong)(_session?.Clock.NowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // Track when playback started (reset on track change or when becoming active)
            if (_playbackStartedAt == 0 && state.Status == PlaybackStatus.Playing)
            {
                _playbackStartedAt = now;
            }

            // Convert domain model to protobuf (pass deviceId for play_origin)
            var playerState = PlaybackStateHelpers.ToPlayerState(state, _session!.Config.DeviceId);

            // Build device info — pass through the volume we have in the domain model.
            // state.Volume is carried forward from the last cluster update (LocalToPlaybackState
            // line 336-338 preserves prev.Volume when local engine doesn't report volume).
            // If still 0, fall back to the Spotify default (max).
            var deviceVolume = state.Volume > 0
                ? (int)state.Volume
                : ConnectStateHelpers.MaxVolume;
            var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(
                _session.Config,
                volume: deviceVolume,
                audioOutputDeviceName: state.ActiveAudioDeviceName);

            // Calculate how long we've been playing.
            // Saturating subtraction: server-clock offset can shift between the
            // _playbackStartedAt assignment and this `now` snapshot, briefly making
            // now < _playbackStartedAt. ulong subtraction would underflow to ~2^64.
            var hasBeenPlayingForMs = _playbackStartedAt > 0 && now > _playbackStartedAt
                ? now - _playbackStartedAt
                : 0UL;

            // Build PUT state request with all required fields
            var request = new PutStateRequest
            {
                MemberType = MemberType.ConnectState,
                Device = new Device
                {
                    DeviceInfo = deviceInfo,
                    PlayerState = playerState,
                    PrivateDeviceInfo = ConnectStateHelpers.CreatePrivateDeviceInfo()
                },
                PutStateReason = PutStateReason.PlayerStateChanged,
                IsActive = _isLocalPlaybackActive,
                ClientSideTimestamp = now,
                MessageId = _messageId++,
                // CRITICAL: These fields are required for Spotify to show device as playing
                StartedPlayingAt = _playbackStartedAt,
                HasBeenPlayingForMs = hasBeenPlayingForMs
            };

            _logger?.LogDebug(
                "PutState SUBMIT: corrId={MessageId}, connId={ConnectionId}, track={Track}, pos={Pos}ms/{Dur}ms, isActive={IsActive}, reason={Reason}, startedAt={StartedAt}, hasBeenPlayingFor={HasBeenPlayingFor}ms",
                request.MessageId,
                _connectionId,
                request.Device?.PlayerState?.Track?.Uri ?? "<none>",
                request.Device?.PlayerState?.Position ?? 0,
                state.DurationMs,
                request.IsActive,
                request.PutStateReason,
                _playbackStartedAt,
                hasBeenPlayingForMs);

            // Defensive deep-clone before queueing. The PutStateRequest holds
            // references to PlayerState.NextTracks / PrevTracks / Metadata maps
            // that share underlying state with the engine's queue model. Without
            // a clone, a later mutation (e.g. the next state push clearing
            // NextTracks before the worker calls ToByteArray) silently produces
            // a malformed wire body: CalculateSize counts the populated lists,
            // WriteTo serializes the now-empty lists, and the resulting length
            // prefix is wildly larger than the actual contents — Spotify's
            // protobuf parser then rejects the entire message and Recently
            // Played stays empty. Cloning here freezes the snapshot.
            // Confirmed via wire-dump diff (2026-04-28): Wavee was sending
            // device length=27095 with only 2421 bytes of actual contents.
            var snapshot = request.Clone();

            // Drop stale updates when the publish queue is saturated.
            // For playback state, freshest update is more important than every intermediate tick.
            if (_statePublisher != null && !_statePublisher.TrySubmit(snapshot))
            {
                Interlocked.Increment(ref _publishDroppedCount);
                _logger?.LogWarning(
                    "DROPPED PutState: publisher queue is full — state will NOT reach Spotify. corrId={MessageId}, droppedTotal={Total}, state=[{State}]",
                    request.MessageId,
                    Interlocked.Read(ref _publishDroppedCount),
                    FormatStateSnapshot(state));
            }
            else if (_statePublisher != null)
            {
                Interlocked.Increment(ref _publishSubmittedCount);
                _logger?.LogTrace("PutState queued: corrId={MessageId}, queueDepth={Depth}", request.MessageId, _statePublisher.PendingCount);
            }

            MaybeLogPublisherHealth();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to prepare state for publishing");
        }
    }

    /// <summary>
    /// Background worker task that publishes state to Spotify.
    /// </summary>
    private async ValueTask PublishStateAsync(PutStateRequest request)
    {
        var startTicks = Environment.TickCount64;
        try
        {
            if (_connectionId == null || _spClient == null || _session == null)
            {
                _logger?.LogWarning(
                    "PutState SEND skipped: corrId={MessageId}, connId={ConnId}, spClient={Sp}, session={Sess}",
                    request.MessageId,
                    _connectionId ?? "<null>",
                    _spClient != null,
                    _session != null);
                return;
            }

            _logger?.LogDebug(
                "PutState SEND start: corrId={MessageId}, reason={Reason}, active={IsActive}, track={TrackUri}, position={Position}",
                request.MessageId,
                request.PutStateReason,
                request.IsActive,
                request.Device?.PlayerState?.Track?.Uri,
                request.Device?.PlayerState?.Position);

            if (_logger?.IsEnabled(LogLevel.Trace) == true)
            {
                _logger.LogTrace("PutState → {Json}", Google.Protobuf.JsonFormatter.Default.Format(request));
            }

            var responseBytes = await _spClient.PutConnectStateAsync(
                _session.Config.DeviceId,
                _connectionId,
                request,
                CancellationToken.None);

            Interlocked.Increment(ref _publishSentCount);
            MaybeLogPublisherHealth();

            var elapsedMs = Environment.TickCount64 - startTicks;
            _logger?.LogDebug(
                "PutState SEND ok: corrId={MessageId}, elapsedMs={Elapsed}, responseBytes={Bytes} (response IS DROPPED — not re-injected via this path)",
                request.MessageId,
                elapsedMs,
                responseBytes?.Length ?? 0);

            if (elapsedMs > 2000)
            {
                _logger?.LogWarning(
                    "PutState SEND slow: corrId={MessageId} took {Elapsed}ms — server latency or network issue",
                    request.MessageId,
                    elapsedMs);
            }
        }
        catch (Exception ex)
        {
            var elapsedMs = Environment.TickCount64 - startTicks;
            _logger?.LogError(ex,
                "PutState SEND FAILED: corrId={MessageId}, elapsedMs={Elapsed}, reason={Reason}, track={TrackUri}",
                request.MessageId,
                elapsedMs,
                request.PutStateReason,
                request.Device?.PlayerState?.Track?.Uri);
        }
    }

    /// <summary>
    /// One-line snapshot of a PlaybackState for log diffs. Keep terse — this shows up a lot.
    /// </summary>
    private static string FormatStateSnapshot(PlaybackState state)
    {
        if (state == null) return "<null>";
        var track = state.Track?.Uri ?? "<none>";
        return $"src={state.Source},status={state.Status},track={track},pos={state.PositionMs}/{state.DurationMs}ms,idx={state.CurrentIndex},actDev={state.ActiveDeviceId ?? "<none>"},ctx={state.ContextUri ?? "<none>"},shf={state.Options.Shuffling},rep={(state.Options.RepeatingTrack ? "T" : state.Options.RepeatingContext ? "C" : "O")},vol={state.Volume},ts={state.Timestamp}";
    }

    /// <summary>
    /// Emits periodic health counters for PutState publishing.
    /// Includes queue depth and submitted/sent/dropped rates.
    /// </summary>
    private void MaybeLogPublisherHealth()
    {
        if (_logger?.IsEnabled(LogLevel.Information) != true)
            return;

        var nowMs = Environment.TickCount64;
        var previousLogAtMs = Volatile.Read(ref _lastPublisherHealthLogAtMs);
        if (previousLogAtMs != 0 && nowMs - previousLogAtMs < PublisherHealthLogIntervalMs)
            return;

        if (Interlocked.CompareExchange(ref _lastPublisherHealthLogAtMs, nowMs, previousLogAtMs) != previousLogAtMs)
            return;

        var submitted = Interlocked.Read(ref _publishSubmittedCount);
        var dropped = Interlocked.Read(ref _publishDroppedCount);
        var sent = Interlocked.Read(ref _publishSentCount);

        var submittedDelta = submitted - Interlocked.Exchange(ref _lastPublisherHealthSubmittedCount, submitted);
        var droppedDelta = dropped - Interlocked.Exchange(ref _lastPublisherHealthDroppedCount, dropped);
        var sentDelta = sent - Interlocked.Exchange(ref _lastPublisherHealthSentCount, sent);

        var elapsedMs = previousLogAtMs == 0 ? 0 : nowMs - previousLogAtMs;
        var submittedPerMin = elapsedMs > 0 ? submittedDelta * 60000.0 / elapsedMs : 0.0;
        var sentPerMin = elapsedMs > 0 ? sentDelta * 60000.0 / elapsedMs : 0.0;
        var droppedPerMin = elapsedMs > 0 ? droppedDelta * 60000.0 / elapsedMs : 0.0;
        var queueDepth = _statePublisher?.PendingCount ?? 0;

        _logger.LogInformation(
            "PutState health: queueDepth={QueueDepth}, submitted={Submitted} ({SubmittedRate:F1}/min), sent={Sent} ({SentRate:F1}/min), dropped={Dropped} ({DroppedRate:F1}/min), debounceFired={DebounceFired}, debounceCancelled={DebounceCancelled}, flushCancelledPending={FlushCancelled}, clusterUpdates={ClusterUpdates}, clusterSkippedNoChange={SkippedNoChange}, clusterSkippedActive={SkippedActive}",
            queueDepth,
            submitted,
            submittedPerMin,
            sent,
            sentPerMin,
            dropped,
            droppedPerMin,
            Interlocked.Read(ref _debounceFiredCount),
            Interlocked.Read(ref _debounceCancelledCount),
            Interlocked.Read(ref _flushCancelledPendingCount),
            Interlocked.Read(ref _clusterUpdateCount),
            Interlocked.Read(ref _clusterUpdateSkippedNoChangesCount),
            Interlocked.Read(ref _clusterUpdateSkippedWeAreActiveCount));
    }

    /// <summary>
    /// Fetches full track metadata from the Spotify API and re-emits an enriched state
    /// if the current track still matches. Uses IExtendedMetadataClient which has caching.
    /// </summary>
    private async Task EnrichTrackMetadataAsync(string trackUri)
    {
        // Atomically swap the shared CTS so this call owns its own lifetime.
        // Reading _enrichCts.Token later is racy — another call can replace the
        // field between Cancel and the Token read, and the stale enrichment would
        // then observe a FRESH token that never gets cancelled.
        var previousCts = Interlocked.Exchange(ref _enrichCts, new CancellationTokenSource());
        previousCts?.Cancel();
        previousCts?.Dispose();
        var ct = _enrichCts!.Token;

        try
        {
            var track = await _metadataClient!.GetTrackAsync(trackUri, ct);
            if (ct.IsCancellationRequested || track == null) return;

            // Don't apply if track changed while we were fetching
            if (_currentState.Track?.Uri != trackUri) return;

            var existingTrack = _currentState.Track;

            var title = existingTrack.Title ?? track.Name;
            var artist = existingTrack.Artist ?? (track.Artist.Count > 0
                ? string.Join(", ", track.Artist.Select(a => a.Name))
                : null);

            var albumUri = existingTrack.AlbumUri;
            if (albumUri == null && track.Album?.Gid is { Length: > 0 })
                albumUri = $"spotify:album:{SpotifyId.FromRaw(track.Album.Gid.Span, SpotifyIdType.Album).ToBase62()}";

            var artistUri = existingTrack.ArtistUri;
            if (artistUri == null && track.Artist.Count > 0 && track.Artist[0].Gid is { Length: > 0 })
                artistUri = $"spotify:artist:{SpotifyId.FromRaw(track.Artist[0].Gid.Span, SpotifyIdType.Artist).ToBase62()}";

            var imageUrl = existingTrack.ImageUrl ?? GetAlbumImageUrl(track.Album, Image.Types.Size.Default);
            var imageLargeUrl = existingTrack.ImageLargeUrl ?? GetAlbumImageUrl(track.Album, Image.Types.Size.Large);
            var imageXLargeUrl = existingTrack.ImageXLargeUrl ?? GetAlbumImageUrl(track.Album, Image.Types.Size.Xlarge);
            var imageSmallUrl = existingTrack.ImageSmallUrl ?? GetAlbumImageUrl(track.Album, Image.Types.Size.Small);
            var album = existingTrack.Album ?? track.Album?.Name;

            var enrichedTrack = existingTrack with
            {
                Title = title,
                Artist = artist,
                Album = album,
                AlbumUri = albumUri,
                ArtistUri = artistUri,
                ImageUrl = imageUrl,
                ImageSmallUrl = imageSmallUrl,
                ImageLargeUrl = imageLargeUrl,
                ImageXLargeUrl = imageXLargeUrl
            };

            // Guard + mutation under the shared state lock so a racing cluster /
            // local update cannot slip between the URI check and the OnNext.
            lock (_stateLock)
            {
                if (_currentState.Track?.Uri != trackUri) return;

                var enrichedState = _currentState with
                {
                    Track = enrichedTrack,
                    Changes = Connect.StateChanges.Track
                };

                _currentState = enrichedState;
                _stateSubject.OnNext(enrichedState);
            }

            _logger?.LogInformation("Enriched track metadata for {Uri}: title={Title}, artist={Artist}",
                trackUri, title, artist);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich track metadata for {TrackUri}", trackUri);
        }
    }

    private static string? GetAlbumImageUrl(Album? album, Image.Types.Size preferredSize)
    {
        if (album?.CoverGroup?.Image.Count is not > 0) return null;
        var image = album.CoverGroup.Image.FirstOrDefault(i => i.Size == preferredSize)
                    ?? album.CoverGroup.Image.FirstOrDefault();
        if (image == null) return null;
        return $"spotify:image:{Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant()}";
    }

    /// <summary>
    /// Calculates the current playback position accounting for elapsed time.
    /// Only valid when status is Playing.
    /// </summary>
    /// <returns>Estimated current position in milliseconds.</returns>
    public long GetCurrentPosition()
    {
        return PlaybackStateHelpers.CalculateCurrentPosition(_currentState);
    }

    /// <summary>
    /// Disposes the playback state manager and releases resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _logger?.LogDebug("Disposing PlaybackStateManager");

        // Unsubscribe from observables FIRST so no new updates arrive mid-dispose.
        _clusterSubscription.Dispose();
        _localPlaybackSubscription?.Dispose();
        _connectionIdSubscription?.Dispose();

        // Cancel enrichment BEFORE completing the state subject — an in-flight
        // EnrichTrackMetadataAsync could otherwise call OnNext on a disposed subject.
        _enrichCts?.Cancel();

        // Dispose debounce — same reason: the delayed continuation calls PublishLocalState.
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = null;
        }

        // Now complete + dispose the subject under the state lock so any racing
        // emitter sees a closed subject rather than a half-disposed one.
        lock (_stateLock)
        {
            _stateSubject.OnCompleted();
            _stateSubject.Dispose();
        }

        _enrichCts?.Dispose();

        // Dispose state publisher
        if (_statePublisher != null)
        {
            await _statePublisher.DisposeAsync();
        }

        _logger?.LogDebug("PlaybackStateManager disposed");
    }
}
