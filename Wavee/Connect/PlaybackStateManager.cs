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
    private bool _isLocalPlaybackActive;
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
            .Subscribe(id => _connectionId = id);

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
    /// <exception cref="InvalidOperationException">Thrown if bidirectional mode is already enabled.</exception>
    public void EnableBidirectionalMode(
        IPlaybackEngine playbackEngine,
        SpClient spClient,
        ISession session)
    {
        if (_playbackEngine != null)
            throw new InvalidOperationException("Bidirectional mode already enabled");

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
            .Subscribe(id => _connectionId = id);

        // Subscribe to local playback state changes
        _localPlaybackSubscription = _playbackEngine.StateChanges
            .Subscribe(OnLocalPlaybackStateChanged);

        _logger?.LogInformation("PlaybackStateManager: bidirectional mode enabled");
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
        try
        {
            _logger?.LogTrace("Received cluster message: uri={Uri}, payloadSize={Size}",
                message.Uri, message.Payload.Length);

            Cluster? cluster = null;

            // Try parsing as Cluster first (PUT state responses)
            if (message.Uri.StartsWith("hm://connect-state/v1/put-state-response", StringComparison.OrdinalIgnoreCase))
            {
                if (!PlaybackStateHelpers.TryParseCluster(message, out cluster) || cluster == null)
                {
                    _logger?.LogWarning("Failed to parse Cluster from PUT state response");
                    return;
                }
                _logger?.LogTrace("Cluster parsed from PUT state response: activeDevice={ActiveDevice}, hasPlayerState={HasPlayerState}",
                    cluster.ActiveDeviceId, cluster.PlayerState != null);
            }
            // Otherwise try parsing as ClusterUpdate (dealer messages)
            else
            {
                if (!PlaybackStateHelpers.TryParseClusterUpdate(message, out var clusterUpdate) || clusterUpdate == null)
                {
                    _logger?.LogWarning("Failed to parse ClusterUpdate from dealer message");
                    return;
                }
                cluster = clusterUpdate.Cluster;
                _logger?.LogTrace("ClusterUpdate parsed: activeDevice={ActiveDevice}, hasPlayerState={HasPlayerState}",
                    cluster.ActiveDeviceId, cluster.PlayerState != null);
            }

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
                _logger?.LogTrace("Ignoring cluster update (we are active device)");
                return;
            }

            // Convert to domain model with change detection
            var newState = PlaybackStateHelpers.ClusterToPlaybackState(cluster, _currentState);

            // Only update if something actually changed
            if (newState.Changes == Connect.StateChanges.None)
            {
                _logger?.LogTrace("No changes detected, skipping update");
                return;
            }

            _logger?.LogDebug("Playback state changed (cluster): changes={Changes}, track={Track}, status={Status}",
                newState.Changes,
                newState.Track?.Title ?? "<none>",
                newState.Status);

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
            _currentState = newState;
            _stateSubject.OnNext(newState);

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
    /// Handles local playback state changes from IPlaybackEngine.
    /// Only active in bidirectional mode.
    /// </summary>
    private void OnLocalPlaybackStateChanged(LocalPlaybackState localState)
    {
        try
        {
            _logger?.LogTrace("Received local playback state: track={Track}, isPlaying={IsPlaying}",
                localState.TrackUri ?? "<none>",
                localState.IsPlaying);

            // Convert to domain model
            var newState = PlaybackStateHelpers.LocalToPlaybackState(
                localState,
                _currentState,
                _session!.Config.DeviceId);

            // Only update if something actually changed
            if (newState.Changes == Connect.StateChanges.None)
            {
                _logger?.LogTrace("No changes detected in local state, skipping update");
                return;
            }

            // Don't let the engine's idle state override remote cluster state.
            // Until we're actively playing locally, the engine's "Stopped" is meaningless.
            if (!_isLocalPlaybackActive && newState.Status != PlaybackStatus.Playing)
            {
                _logger?.LogTrace("Ignoring local idle state — not the active local device");
                return;
            }

            _logger?.LogDebug("Playback state changed (local): changes={Changes}, track={Track}, status={Status}",
                newState.Changes,
                newState.Track?.Title ?? "<none>",
                newState.Status);

            // Reset playback started timestamp on track change or stop
            if (newState.Changes.HasFlag(Connect.StateChanges.Track) ||
                newState.Status == PlaybackStatus.Stopped)
            {
                _playbackStartedAt = 0;
            }

            // Mark as active if playing locally
            if (newState.Status == PlaybackStatus.Playing && !_isLocalPlaybackActive)
            {
                _logger?.LogInformation("Local playback started, becoming active device");
                _isLocalPlaybackActive = true;
            }

            // Update state
            _currentState = newState;
            _stateSubject.OnNext(newState);

            // Publish to Spotify (skip position-only changes for infinite streams)
            if (_isLocalPlaybackActive)
            {
                var isPositionOnlyChange = newState.Changes == Connect.StateChanges.Position;
                var isInfiniteStream = newState.DurationMs == 0;

                if (isPositionOnlyChange && isInfiniteStream)
                {
                    _logger?.LogTrace("Skipping Spotify update for position-only change on infinite stream");
                }
                else
                {
                    // Critical changes (track, status, device, context) flush immediately.
                    // Position-only changes are debounced to reduce PutState flooding.
                    var isCritical = newState.Changes.HasFlag(Connect.StateChanges.Track)
                        || newState.Changes.HasFlag(Connect.StateChanges.Status)
                        || newState.Changes.HasFlag(Connect.StateChanges.ActiveDevice)
                        || newState.Changes.HasFlag(Connect.StateChanges.Context);

                    if (isCritical)
                    {
                        FlushAndPublishState(newState);
                    }
                    else
                    {
                        DebouncedPublishState(newState);
                    }
                }
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
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
            _pendingState = null;
        }
        PublishLocalState(state);
    }

    /// <summary>
    /// Debounces non-critical state updates (position-only) to reduce PutState flooding.
    /// Waits DebounceMs — if no new update arrives, publishes. If a new update arrives, resets the timer.
    /// </summary>
    private void DebouncedPublishState(PlaybackState state)
    {
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _pendingState = state;
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = Task.Delay(DebounceMs, token).ContinueWith(_ =>
            {
                PlaybackState? toPublish;
                lock (_debounceLock)
                {
                    toPublish = _pendingState;
                    _pendingState = null;
                }
                if (toPublish != null)
                    PublishLocalState(toPublish);
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
            _logger?.LogTrace("Cannot publish state: connection ID not available");
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

            // Build device info (reuse from DeviceStateManager pattern)
            var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(_session.Config);

            // Calculate how long we've been playing
            var hasBeenPlayingForMs = _playbackStartedAt > 0 ? now - _playbackStartedAt : 0;

            // Build PUT state request with all required fields
            var request = new PutStateRequest
            {
                MemberType = MemberType.ConnectState,
                Device = new Device
                {
                    DeviceInfo = deviceInfo,
                    PlayerState = playerState
                },
                PutStateReason = PutStateReason.PlayerStateChanged,
                IsActive = _isLocalPlaybackActive,
                ClientSideTimestamp = now,
                MessageId = _messageId++,
                // CRITICAL: These fields are required for Spotify to show device as playing
                StartedPlayingAt = _playbackStartedAt,
                HasBeenPlayingForMs = hasBeenPlayingForMs
            };

            _logger?.LogTrace("Submitting state publish: messageId={MessageId}, connectionId={ConnectionId}, startedAt={StartedAt}",
                request.MessageId, _connectionId, _playbackStartedAt);

            // Drop stale updates when the publish queue is saturated.
            // For playback state, freshest update is more important than every intermediate tick.
            if (_statePublisher != null && !_statePublisher.TrySubmit(request))
            {
                _logger?.LogTrace(
                    "Dropping PutState update because publisher queue is full (messageId={MessageId})",
                    request.MessageId);
            }
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
        try
        {
            if (_connectionId == null || _spClient == null || _session == null)
                return;

            _logger?.LogDebug(
                "PutState: messageId={MessageId}, reason={Reason}, active={IsActive}, track={TrackUri}, position={Position}",
                request.MessageId,
                request.PutStateReason,
                request.IsActive,
                request.Device?.PlayerState?.Track?.Uri,
                request.Device?.PlayerState?.Position);

            if (_logger?.IsEnabled(LogLevel.Trace) == true)
            {
                _logger.LogTrace("PutState → {Json}", Google.Protobuf.JsonFormatter.Default.Format(request));
            }

            await _spClient.PutConnectStateAsync(
                _session.Config.DeviceId,
                _connectionId,
                request,
                CancellationToken.None);

            _logger?.LogTrace("State published successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to publish playback state");
        }
    }

    /// <summary>
    /// Fetches full track metadata from the Spotify API and re-emits an enriched state
    /// if the current track still matches. Uses IExtendedMetadataClient which has caching.
    /// </summary>
    private async Task EnrichTrackMetadataAsync(string trackUri)
    {
        _enrichCts?.Cancel();
        _enrichCts = new CancellationTokenSource();
        var ct = _enrichCts.Token;

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

            // Guard again after building enriched track
            if (_currentState.Track?.Uri != trackUri) return;

            var enrichedState = _currentState with
            {
                Track = enrichedTrack,
                Changes = Connect.StateChanges.Track
            };

            _currentState = enrichedState;
            _stateSubject.OnNext(enrichedState);

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

        // Unsubscribe from observables
        _clusterSubscription.Dispose();
        _localPlaybackSubscription?.Dispose();
        _connectionIdSubscription?.Dispose();

        // Complete state subject
        _stateSubject.OnCompleted();
        _stateSubject.Dispose();

        // Dispose enrichment
        _enrichCts?.Cancel();
        _enrichCts?.Dispose();

        // Dispose debounce
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        // Dispose state publisher
        if (_statePublisher != null)
        {
            await _statePublisher.DisposeAsync();
        }

        _logger?.LogDebug("PlaybackStateManager disposed");
    }
}
