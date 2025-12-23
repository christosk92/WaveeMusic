using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Protocol;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Utilities;
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

                // Stop local playback immediately - fire and forget
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _playbackEngine!.StopAsync();
                        _logger?.LogDebug("Local playback stopped due to device takeover");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to stop local playback after device takeover");
                    }
                });
            }

            // Update state
            _currentState = newState;
            _stateSubject.OnNext(newState);

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
                    PublishLocalState(newState);
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
            var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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

            // Submit to async worker (non-blocking)
            _statePublisher?.SubmitAsync(request);
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

            _logger?.LogDebug("Publishing playback state to Spotify: messageId={MessageId}", request.MessageId);

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

        // Dispose state publisher
        if (_statePublisher != null)
        {
            await _statePublisher.DisposeAsync();
        }

        _logger?.LogDebug("PlaybackStateManager disposed");
    }
}
