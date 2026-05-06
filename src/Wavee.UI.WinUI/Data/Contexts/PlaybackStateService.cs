using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.AudioIpc;
using Wavee.Connect;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Playback.Contracts;
using Wavee.UI.Contracts;
using Wavee.UI.Enums;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Bridges <see cref="PlaybackStateManager"/> (remote Spotify Connect state) to UI-layer properties.
/// Delegates all commands to <see cref="IPlaybackService"/> (fire-and-forget for backward compat).
/// </summary>
internal sealed partial class PlaybackStateService : ObservableObject, IPlaybackStateService, IDisposable,
    IRecipient<AuthStatusChangedMessage>,
    IRecipient<TrackMetadataEnrichedMessage>,
    IRecipient<QueueMetadataEnrichedMessage>,
    IRecipient<MusicVideoAvailabilityMessage>
{
    private readonly Session _session;
    private readonly IPlaybackService _playbackService;
    private readonly IColorService _colorService;
    private readonly IMessenger _messenger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private readonly IHomeFeedCache? _homeFeedCache;

    // Lazily resolved to break the construction cycle with the discovery
    // service (which depends on this state service for CurrentArtistId).
    private Wavee.UI.WinUI.Services.IMusicVideoDiscoveryService? VideoDiscovery =>
        CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.UI.WinUI.Services.IMusicVideoDiscoveryService>();
    private Wavee.UI.WinUI.Services.IMusicVideoMetadataService? VideoMetadata =>
        CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.UI.WinUI.Services.IMusicVideoMetadataService>();
    private List<QueueItem> _queue = [];
    private List<QueueItem> _prevQueue = [];
    private IReadOnlyList<QueueItem>? _userQueueCache;
    private IReadOnlyList<Wavee.Audio.Queue.IQueueItem> _rawNextQueue = [];
    private IReadOnlyList<Wavee.Audio.Queue.IQueueItem> _rawPrevQueue = [];
    private IDisposable? _stateSubscription;
    private CancellationTokenSource? _colorCts;
    private bool _isFirstStateUpdate = true;
    private bool _isBatchingStateUpdate;
    private bool _nowPlayingDirty;
    private bool _isSuppressingPropertyChanged;
    private HashSet<string>? _pendingPropertyChanges;
    private string? _lastColorImageUrl;
    // Deferred color-extract target. ApplyConnectState records the URL to
    // extract from here instead of firing synchronously, and the batched
    // OnRemoteStateChanged flush handler schedules extraction on a ThreadPool
    // worker AFTER FlushPropertyChanges() returns. Without this, the colour
    // service's synchronous ramp (SQLite probe + hot-cache check) runs inside
    // the dispatcher tick the profiler measures as "PlaybackStateFlush".
    private string? _pendingColorImageUrl;
    private bool _pendingColorClear;
    private double? _pendingSeekPositionMs;
    private long _lastPositionLogAtMs;
    // Seek-in-flight guard: between the moment Seek() is sent and the moment the
    // cluster echoes back a position near the target, the AudioHost continues to
    // emit position updates for the *old* playhead. Without this sentinel those
    // stale positions race-overwrite Position and the slider visibly bounces.
    private CancellationTokenSource? _seekConfirmationCts;
    private const double SeekConfirmedToleranceMs = 2000;
    private const int SeekConfirmationTimeoutMs = 3000;

    // ── State properties ──

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _currentTrackId;
    [ObservableProperty] private string? _currentTrackTitle;
    [ObservableProperty] private string? _currentArtistName;
    [ObservableProperty] private string? _currentAlbumArt;
    [ObservableProperty] private string? _currentAlbumArtLarge;
    [ObservableProperty] private string? _currentArtistId;
    [ObservableProperty] private string? _currentAlbumId;
    [ObservableProperty] private string? _currentTrackManifestId;
    [ObservableProperty] private bool _currentTrackHasMusicVideo;
    [ObservableProperty] private bool _currentTrackIsVideo;
    [ObservableProperty] private string? _currentAlbumArtColor;
    [ObservableProperty] private IReadOnlyList<ArtistCredit>? _currentArtists;
    [ObservableProperty] private string? _currentOriginalTrackId;
    [ObservableProperty] private string? _currentOriginalTrackTitle;
    [ObservableProperty] private string? _currentOriginalArtistName;
    [ObservableProperty] private string? _currentOriginalAlbumArt;
    [ObservableProperty] private string? _currentOriginalAlbumArtLarge;
    [ObservableProperty] private string? _currentOriginalArtistId;
    [ObservableProperty] private string? _currentOriginalAlbumId;
    [ObservableProperty] private double _currentOriginalDuration;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _playbackSpeed = 1.0;
    [ObservableProperty] private double _volume = 100.0;
    [ObservableProperty] private bool _isShuffle;
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.Off;
    [ObservableProperty] private PlaybackContextInfo? _currentContext;
    [ObservableProperty] private int _queuePosition;
    [ObservableProperty] private bool _isPlayingRemotely;
    [ObservableProperty] private string? _activeDeviceName;
    [ObservableProperty] private DeviceType _activeDeviceType = DeviceType.Computer;
    [ObservableProperty] private IReadOnlyList<ConnectDevice> _availableConnectDevices = [];
    [ObservableProperty] private string? _activeAudioDeviceName;
    [ObservableProperty] private IReadOnlyList<AudioOutputDeviceDto> _availableAudioDevices = [];
    [ObservableProperty] private bool _isAudioEngineAvailable = true;
    [ObservableProperty] private bool _isVolumeRestricted;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private string? _bufferingTrackId;

    /// <summary>
    /// True once playback has reached end-of-context and auto-advance has
    /// stopped. Drives the inline "You've reached the end" hint in the
    /// PlayerBar. Cleared as soon as playback resumes (track change or
    /// transition back to Playing). Set by <see cref="NotifyEndOfContext"/>
    /// which is called from the orchestrator's <c>EndOfContext</c>
    /// subscription in <c>AppLifecycleHelper</c>.
    /// </summary>
    [ObservableProperty] private bool _isAtEndOfContext;

    public IReadOnlyList<QueueItem> Queue => _queue;
    public IReadOnlyList<QueueItem> PreviousTracks => _prevQueue;
    public IReadOnlyList<QueueItem> UserQueue => _userQueueCache ??= BuildUserQueueCache();

    private IReadOnlyList<QueueItem> BuildUserQueueCache()
    {
        List<QueueItem>? result = null;
        foreach (var q in _queue)
        {
            if (!q.IsUserQueued) continue;
            result ??= new List<QueueItem>();
            result.Add(q);
        }
        return result ?? (IReadOnlyList<QueueItem>)Array.Empty<QueueItem>();
    }
    public IReadOnlyList<Wavee.Audio.Queue.IQueueItem> RawNextQueue => _rawNextQueue;
    public IReadOnlyList<Wavee.Audio.Queue.IQueueItem> RawPrevQueue => _rawPrevQueue;

    public PlaybackStateService(
        Session session,
        IPlaybackService playbackService,
        IColorService colorService,
        IMessenger messenger,
        DispatcherQueue dispatcherQueue,
        ILogger? logger = null,
        IHomeFeedCache? homeFeedCache = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _logger = logger;
        _homeFeedCache = homeFeedCache;

        // Register for auth status changes to subscribe after session connects
        _messenger.Register<AuthStatusChangedMessage>(this);
        _messenger.Register<TrackMetadataEnrichedMessage>(this);
        _messenger.Register<QueueMetadataEnrichedMessage>(this);
        _messenger.Register<MusicVideoAvailabilityMessage>(this);

        // Try subscribing now in case session is already connected
        TrySubscribeToRemoteState();
    }

    // ── IRecipient<AuthStatusChangedMessage> ──

    public void Receive(AuthStatusChangedMessage message)
    {
        _logger?.LogDebug("AuthStatusChanged received: status={Status}, alreadySubscribed={AlreadySubscribed}",
            message.Value, _stateSubscription != null);
        if (message.Value == AuthStatus.Authenticated)
        {
            _logger?.LogInformation("Auth status: Authenticated — attempting subscription to PlaybackStateManager.StateChanges");
            _dispatcherQueue.TryEnqueue(TrySubscribeToRemoteState);
        }
        else if (message.Value == AuthStatus.LoggedOut || message.Value == AuthStatus.SessionExpired)
        {
            _logger?.LogInformation("Auth status: {Status} — tearing down remote state bridge", message.Value);
            _dispatcherQueue.TryEnqueue(TearDownRemoteState);
        }
    }

    private void TearDownRemoteState()
    {
        var sub = Interlocked.Exchange(ref _stateSubscription, null);
        sub?.Dispose();
        _subscribeAttemptCount = 0;
        _isFirstStateUpdate = true;

        // Clear visible now-playing state — without this, the mini-player and queue
        // panel keep showing the signed-out user's last track/queue.
        IsPlaying = false;
        CurrentTrackId = null;
        CurrentTrackTitle = null;
        CurrentArtistName = null;
        CurrentAlbumArt = null;
        CurrentAlbumArtLarge = null;
        CurrentArtistId = null;
        CurrentAlbumId = null;
        CurrentTrackManifestId = null;
        CurrentTrackHasMusicVideo = false;
        CurrentTrackIsVideo = false;
        CurrentOriginalTrackId = null;
        CurrentOriginalTrackTitle = null;
        CurrentOriginalArtistName = null;
        CurrentOriginalAlbumArt = null;
        CurrentOriginalAlbumArtLarge = null;
        CurrentOriginalArtistId = null;
        CurrentOriginalAlbumId = null;
        CurrentOriginalDuration = 0;
        CurrentAlbumArtColor = null;
        CurrentArtists = null;
        Position = 0;
        Duration = 0;
        PlaybackSpeed = 1.0;
        CurrentContext = null;
        QueuePosition = 0;
        IsPlayingRemotely = false;
        ActiveDeviceName = null;
        ActiveDeviceType = DeviceType.Computer;
        AvailableConnectDevices = [];
        IsBuffering = false;
        BufferingTrackId = null;

        _queue = [];
        _prevQueue = [];
        _rawNextQueue = [];
        _rawPrevQueue = [];
        _userQueueCache = null;
        OnPropertyChanged(nameof(Queue));
        OnPropertyChanged(nameof(PreviousTracks));
        OnPropertyChanged(nameof(UserQueue));
        OnPropertyChanged(nameof(RawNextQueue));
        OnPropertyChanged(nameof(RawPrevQueue));

        _lastColorImageUrl = null;
        _pendingSeekPositionMs = null;
        _seekConfirmationCts?.Cancel();
        _seekConfirmationCts?.Dispose();
        _seekConfirmationCts = null;
    }

    // ── Remote state bridge ──

    // How many times we've attempted to subscribe. If this grows without Subscribed appearing in logs,
    // Session.PlaybackState is chronically null — a subscription race to triage.
    private int _subscribeAttemptCount;

    private void TrySubscribeToRemoteState()
    {
        var attempt = Interlocked.Increment(ref _subscribeAttemptCount);

        // Already subscribed
        if (_stateSubscription != null)
        {
            _logger?.LogTrace("TrySubscribeToRemoteState: attempt#{Attempt} — already subscribed, skipping", attempt);
            return;
        }

        var stateManager = _session.PlaybackState;
        if (stateManager == null)
        {
            _logger?.LogWarning(
                "TrySubscribeToRemoteState: attempt#{Attempt} — PlaybackStateManager is null (session not connected). Remote state bridge NOT yet active. Will retry on next AuthStatusChanged.",
                attempt);
            return;
        }

        _stateSubscription = stateManager.StateChanges
            .Subscribe(
                OnRemoteStateChanged,
                ex => _logger?.LogError(ex, "Error in remote playback state subscription"));

        _logger?.LogInformation(
            "TrySubscribeToRemoteState: attempt#{Attempt} — SUBSCRIBED to PlaybackStateManager.StateChanges. Remote state bridge active.",
            attempt);
    }

    private void OnRemoteStateChanged(PlaybackState state)
    {
        _logger?.LogDebug(
            "Remote state update: changes={Changes}, source={Source}, status={Status}, track={Track}, pos={Pos}/{Dur}ms, actDev={ActDev}, ourDev={OurDev}, sessionConnected={Conn}",
            state.Changes,
            state.Source,
            state.Status,
            state.Track?.Title ?? "<none>",
            state.PositionMs,
            state.DurationMs,
            state.ActiveDeviceId ?? "<none>",
            _session.Config.DeviceId,
            _session.IsConnected());

        _dispatcherQueue.TryEnqueue(() =>
        {
            _isBatchingStateUpdate = true;
            _isSuppressingPropertyChanged = true;

            try
            {
                // First state received after subscribing — BehaviorSubject replays with partial
                // change flags, so force a full sync to populate all UI properties.
                //
                // Also clamp a stale "Playing" status on cold start IF no device is actually
                // driving playback: the cluster-replayed state carries whatever the user was
                // last doing in Spotify, but on app launch nothing may be playing here. If a
                // real remote device IS active (e.g. phone/other desktop is playing), we must
                // preserve Playing so the UI correctly shows "Playing on <device>".
                if (_isFirstStateUpdate)
                {
                    _isFirstStateUpdate = false;

                    var ourDeviceId = _session.Config.DeviceId;
                    var activeDeviceId = state.ActiveDeviceId;
                    var remoteDeviceIsActive = !string.IsNullOrEmpty(activeDeviceId)
                                               && !string.Equals(activeDeviceId, ourDeviceId, StringComparison.Ordinal);

                    var shouldClamp = state.Status == PlaybackStatus.Playing
                                      && state.Source != StateSource.Local
                                      && !remoteDeviceIsActive;

                    var clampedStatus = shouldClamp ? PlaybackStatus.Paused : state.Status;
                    state = state with
                    {
                        Status = clampedStatus,
                        Changes = StateChanges.All,
                    };
                }

                // Track info — apply Connect state immediately, request API enrichment
                if (state.Changes.HasFlag(StateChanges.Track))
                {
                    _pendingSeekPositionMs = null;
                    Duration = state.DurationMs;

                    var trackUri = state.Track?.Uri;
                    if (!string.IsNullOrEmpty(trackUri))
                    {
                        // Apply connect state immediately (may be incomplete)
                        ApplyConnectState(trackUri, state);

                        // Request enrichment for catalog tracks and episodes.
                        // The enricher branches by URI type; episodes use podcast
                        // metadata instead of the TrackV4 path.
                        if (IsSpotifyTrackUri(trackUri) || IsSpotifyEpisodeUri(trackUri))
                            _messenger.Send(new TrackEnrichmentRequestMessage(trackUri));
                    }
                }

                // Playback status — trust the state from AudioHost (authoritative after proxy-only mode)
                if (state.Changes.HasFlag(StateChanges.Status))
                {
                    IsPlaying = state.Status == PlaybackStatus.Playing;

                    if (IsBuffering && state.Status is PlaybackStatus.Playing or PlaybackStatus.Stopped)
                    {
                        IsBuffering = false;
                        BufferingTrackId = null;
                    }

                    if (IsPlaying)
                        _homeFeedCache?.SuspendRefresh();
                    else
                        _homeFeedCache?.ResumeRefresh();
                }

                // Position — must calculate from timestamp when playing
                if (state.Changes.HasFlag(StateChanges.Position) ||
                    state.Changes.HasFlag(StateChanges.Status) ||
                    state.Changes.HasFlag(StateChanges.Track))
                {
                    // Clear buffering on position change (seek completed) or track change
                    if (IsBuffering && (state.Changes.HasFlag(StateChanges.Position) || state.Changes.HasFlag(StateChanges.Track)))
                    {
                        IsBuffering = false;
                        BufferingTrackId = null;
                    }

                    // Cluster state timestamps are in Spotify's server clock domain.
                    // Local AudioHost state timestamps are in the local clock domain.
                    long correctedNow = GetPositionClockNowMs(state);

                    var calculatedPos = PlaybackStateHelpers.CalculateCurrentPosition(state, correctedNow);

                    if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    {
                        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (nowMs - _lastPositionLogAtMs >= 1000 || !state.Changes.HasFlag(StateChanges.Position))
                        {
                            _lastPositionLogAtMs = nowMs;
                            _logger.LogDebug(
                                "UI bridge: position → {CalculatedMs}ms " +
                                "(raw={RawMs}ms, timestamp={Timestamp}, now={Now}, " +
                                "elapsed={Elapsed}ms, status={Status}, duration={Duration}ms, source={Source}, clockOffset={ClockOffset}ms)",
                                calculatedPos,
                                state.PositionMs,
                                state.Timestamp,
                                correctedNow,
                                correctedNow - state.Timestamp,
                                state.Status,
                                state.DurationMs,
                                state.Source,
                                _session.IsConnected() ? _session.Clock.OffsetMs : 0);
                        }
                    }

                    // Seek-in-flight: until the cluster echoes a position close to the
                    // commanded target, the AudioHost keeps emitting the *old* playhead.
                    // Accepting those would yank the slider back to where the user was
                    // before the seek (the "bouncing" bug).
                    var seekTarget = _pendingSeekPositionMs;
                    if (seekTarget.HasValue)
                    {
                        if (Math.Abs(calculatedPos - seekTarget.Value) <= SeekConfirmedToleranceMs)
                        {
                            // Cluster confirmed the new position — accept and clear.
                            _pendingSeekPositionMs = null;
                            _seekConfirmationCts?.Cancel();
                            _seekConfirmationCts?.Dispose();
                            _seekConfirmationCts = null;
                            Position = calculatedPos;
                        }
                        // else: stale pre-seek frame still in the pipe; drop it.
                    }
                    else
                    {
                        // The PlayerBar interpolates position locally between authoritative
                        // updates. Suppress sub-250ms drift corrections — they fight the
                        // interpolator, retrigger PropertyChanged, and re-render bindings
                        // without the user noticing the change.
                        var posDelta = Math.Abs(calculatedPos - Position);
                        if (state.Changes.HasFlag(StateChanges.Track) || posDelta >= 250 || !IsPlaying)
                        {
                            Position = calculatedPos;
                        }
                    }

                    // Update duration here too — it may change when local engine
                    // starts playing a ghost track (cluster had duration=0)
                    if (state.DurationMs > 0 && Duration != state.DurationMs)
                        Duration = state.DurationMs;
                }

                // Options
                if (state.Changes.HasFlag(StateChanges.Options) ||
                    state.Changes.HasFlag(StateChanges.PlaybackSpeed))
                {
                    _logger?.LogDebug("UI bridge: options → shuffle={Shuffle}, repeatCtx={RepeatCtx}, repeatTrack={RepeatTrack}",
                        state.Options.Shuffling, state.Options.RepeatingContext, state.Options.RepeatingTrack);
                    IsShuffle = state.Options.Shuffling;
                    RepeatMode = state.Options.RepeatingTrack
                        ? RepeatMode.Track
                        : state.Options.RepeatingContext
                            ? RepeatMode.Context
                            : RepeatMode.Off;
                    PlaybackSpeed = state.PlaybackSpeed;
                }

                // Context
                if (state.Changes.HasFlag(StateChanges.Context))
                {
                    _logger?.LogDebug("UI bridge: context → {Context}", state.ContextUri);
                    CurrentContext = ParseContext(state.ContextUri);
                }

                // Queue
                if (state.Changes.HasFlag(StateChanges.Queue))
                {
                    SyncQueue(state);
                }

                // Active device (remote playback indicator)
                if (state.Changes.HasFlag(StateChanges.ActiveDevice) || state.Changes.HasFlag(StateChanges.Source))
                {
                    var isRemote = state.Source == StateSource.Cluster
                                   && !string.IsNullOrEmpty(state.ActiveDeviceId)
                                   && state.ActiveDeviceId != _session.Config.DeviceId
                                   && !string.IsNullOrEmpty(state.ActiveDeviceName);
                    IsPlayingRemotely = isRemote;
                    ActiveDeviceName = isRemote ? state.ActiveDeviceName : null;
                    ActiveDeviceType = state.ActiveDeviceType;
                    AvailableConnectDevices = state.AvailableConnectDevices;
                    _logger?.LogDebug("UI bridge: remote={IsRemote}, device={DeviceName} ({DeviceId}) type={DeviceType} connectDevices={Count}",
                        isRemote, state.ActiveDeviceName, state.ActiveDeviceId, state.ActiveDeviceType, state.AvailableConnectDevices.Count);
                }

                // Local audio output device info flows through LocalPlaybackState and
                // is forwarded here on every state update so the right panel can subscribe
                // via INotifyPropertyChanged.
                if (state.ActiveAudioDeviceName != null &&
                    !string.Equals(state.ActiveAudioDeviceName, ActiveAudioDeviceName, StringComparison.Ordinal))
                {
                    ActiveAudioDeviceName = state.ActiveAudioDeviceName;
                }
                if (state.AvailableAudioDevices is { Count: > 0 } &&
                    !ReferenceEquals(state.AvailableAudioDevices, AvailableAudioDevices))
                {
                    AvailableAudioDevices = state.AvailableAudioDevices;
                }

                // Volume (convert 0-65535 → 0-100 for UI)
                // Suppress command feedback: remote state sync should NOT trigger set_volume back
                if (state.Changes.HasFlag(StateChanges.Volume) || state.Changes.HasFlag(StateChanges.ActiveDevice))
                {
                    var uiVolume = state.Volume / 655.35;
                    // Don't overwrite to 0 if the remote volume is uninitialized
                    if (state.Volume > 0 || Volume == 0)
                    {
                        _suppressVolumeCommand = true;
                        Volume = Math.Clamp(uiVolume, 0, 100);
                        _suppressVolumeCommand = false;
                    }
                    IsVolumeRestricted = state.IsVolumeRestricted;
                    _logger?.LogDebug("UI bridge: volume → {Volume:F0}% (raw={Raw}/65535, restricted={Restricted})",
                        Volume, state.Volume, state.IsVolumeRestricted);
                }
            }
            finally
            {
                _isSuppressingPropertyChanged = false;
                _isBatchingStateUpdate = false;
                {
                    using var _p = UiOperationProfiler.Instance?.Profile("PlaybackStateFlush");
                    FlushPropertyChanges();
                }

                // Send consolidated now-playing notification (deduplicated across IsPlaying + Context changes)
                FlushNowPlayingMessage();

                // Fire colour extraction OUTSIDE the flush tick. ApplyConnectState
                // records the intent in _pendingColorImageUrl; we dispatch to the
                // ThreadPool so the colour service's sync ramp (SQLite probe,
                // hot-cache check, HttpClient prep) never runs on the dispatcher.
                // Results marshal back via _dispatcherQueue.TryEnqueue inside
                // ExtractAlbumColorAsync itself.
                if (_pendingColorClear)
                {
                    _pendingColorClear = false;
                    _lastColorImageUrl = null;
                    CurrentAlbumArtColor = null;
                }
                else if (_pendingColorImageUrl is { } colorUrl)
                {
                    _pendingColorImageUrl = null;
                    _ = ExtractAlbumColorAsync(colorUrl);
                }
            }
        });
    }

    // ── IRecipient<TrackMetadataEnrichedMessage> ──

    public void Receive(TrackMetadataEnrichedMessage message)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Only apply if this enrichment is for the currently playing track
            var currentUri = GetCurrentTrackUri();
            if (!string.Equals(CurrentTrackId, message.TrackId, StringComparison.Ordinal)
                && !string.Equals(CurrentTrackId, message.TrackUri, StringComparison.Ordinal)
                && !string.Equals(currentUri, message.TrackUri, StringComparison.Ordinal))
            {
                return;
            }

            if (message.Title != null) CurrentTrackTitle = message.Title;
            if (message.ArtistName != null) CurrentArtistName = message.ArtistName;
            if (message.AlbumArt != null) CurrentAlbumArt = GetCurrentDisplayAlbumArt(message.AlbumArt);
            if (message.AlbumArtLarge != null) CurrentAlbumArtLarge = GetCurrentDisplayAlbumArtLarge(message.AlbumArtLarge);
            if (message.ArtistId != null) CurrentArtistId = message.ArtistId;
            if (message.AlbumId != null) CurrentAlbumId = message.AlbumId;
            if (message.Artists != null) CurrentArtists = message.Artists;

            // Re-extract color if album art was enriched. For music-video
            // playback, keep UI chrome themed from the original audio track
            // artwork instead of the video-track thumbnail.
            if (message.AlbumArt != null)
            {
                var colorImageUrl = GetCurrentColorImageUrl(message.AlbumArt);
                if (!string.IsNullOrEmpty(colorImageUrl))
                    _ = ExtractAlbumColorAsync(colorImageUrl);
            }
        });
    }

    /// <summary>
    /// Fallback: applies track metadata from the Connect state when the API fetch
    /// is unavailable or fails. Connect state may have incomplete fields.
    /// </summary>
    /// <summary>
    /// Applies track metadata from Connect state directly.
    /// Must be called from within a <c>_dispatcherQueue.TryEnqueue</c> callback (already on UI thread).
    /// </summary>
    private void ApplyConnectState(string trackUri, PlaybackState connectState)
    {
        var trackId = ExtractTrackId(trackUri);
        var metadata = connectState.Track?.Metadata;
        var trackImageUrl = connectState.Track?.ImageUrl;
        var trackImageLargeUrl = connectState.Track?.ImageLargeUrl
            ?? connectState.Track?.ImageXLargeUrl
            ?? connectState.Track?.ImageUrl;
        var artistName = FirstNonWhiteSpace(
            connectState.Track?.Artist,
            TryGetMetadataValue(metadata, "artist_name"),
            TryGetMetadataValue(metadata, "artist"),
            TryGetMetadataValue(metadata, "album_artist_name"));
        var artistUri = FirstNonWhiteSpace(
            connectState.Track?.ArtistUri,
            TryGetMetadataValue(metadata, "artist_uri"),
            TryGetMetadataValue(metadata, "wavee.original_artist_uri"));

        CurrentTrackTitle = connectState.Track?.Title;
        CurrentArtistName = artistName;
        CurrentArtistId = artistUri;
        CurrentAlbumId = connectState.Track?.AlbumUri;
        CurrentArtists = !string.IsNullOrWhiteSpace(artistName)
            ? [new ArtistCredit(artistName!, artistUri)]
            : null;

        // Music-video manifest_id has two sources: the resolved Track proto
        // (carried through LocalPlaybackState.VideoManifestId, merged into
        // PlaybackState.VideoManifestId) and the remote-driven Connect-state
        // metadata field. Prefer the engine-resolved value; fall back to the
        // Connect metadata so remote-driven video tracks still light the
        // affordance.
        string? manifestId = connectState.VideoManifestId;
        if (string.IsNullOrEmpty(manifestId)
            && connectState.Track?.Metadata is { Count: > 0 } meta
            && meta.TryGetValue("media.manifest_id", out var m))
            manifestId = m;
        CurrentTrackManifestId = manifestId;
        CurrentTrackIsVideo = metadata is { Count: > 0 } mediaMeta
            && mediaMeta.TryGetValue("track_player", out var player)
            && string.Equals(player, "video", StringComparison.OrdinalIgnoreCase);
        ApplyOriginalTrackMetadata(metadata, CurrentTrackIsVideo, connectState);
        CurrentAlbumArt = GetCurrentDisplayAlbumArt(trackImageUrl);
        CurrentAlbumArtLarge = GetCurrentDisplayAlbumArtLarge(trackImageLargeUrl);

        // Set CurrentTrackId last — fires PropertyChanged which triggers lyrics fetch
        CurrentTrackId = trackId;

        // Record color-extract intent; the outer flush handler fires the
        // actual extraction on a ThreadPool worker after the dispatcher tick
        // ends, so the colour service's sync ramp doesn't count against
        // [PlaybackStateFlush] (measured 169ms on first render).
        var imageUrl = GetColorImageUrlForState(
            connectState.Track?.ImageUrl,
            metadata,
            CurrentTrackIsVideo);
        if (!string.IsNullOrEmpty(imageUrl))
        {
            _pendingColorImageUrl = imageUrl;
            _pendingColorClear = false;
        }
        else
        {
            _pendingColorImageUrl = null;
            _pendingColorClear = true;
        }
    }

    private string? GetCurrentColorImageUrl(string? fallback)
    {
        if (!CurrentTrackIsVideo)
            return fallback;

        return FirstNonWhiteSpace(
            CurrentOriginalAlbumArtLarge,
            CurrentOriginalAlbumArt,
            fallback);
    }

    private string? GetCurrentDisplayAlbumArt(string? fallback)
    {
        if (!CurrentTrackIsVideo)
            return fallback;

        return FirstNonWhiteSpace(
            CurrentOriginalAlbumArt,
            fallback);
    }

    private string? GetCurrentDisplayAlbumArtLarge(string? fallback)
    {
        if (!CurrentTrackIsVideo)
            return fallback;

        return FirstNonWhiteSpace(
            CurrentOriginalAlbumArtLarge,
            CurrentOriginalAlbumArt,
            fallback);
    }

    private static string? GetColorImageUrlForState(
        string? currentImageUrl,
        IReadOnlyDictionary<string, string>? metadata,
        bool currentTrackIsVideo)
    {
        if (!currentTrackIsVideo || metadata is null)
            return currentImageUrl;

        return FirstNonWhiteSpace(
            TryGetMetadataValue(metadata, "wavee.original_image_url"),
            TryGetMetadataValue(metadata, "wavee.original_image_large_url"),
            TryGetMetadataValue(metadata, "wavee.original_image_xlarge_url"),
            currentImageUrl);
    }

    private static string? TryGetMetadataValue(IReadOnlyDictionary<string, string>? metadata, string key)
        => metadata != null && metadata.TryGetValue(key, out var value) ? value : null;

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private void ApplyOriginalTrackMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        bool currentTrackIsVideo,
        PlaybackState connectState)
    {
        if (!currentTrackIsVideo || metadata is null)
        {
            CurrentOriginalTrackId = null;
            CurrentOriginalTrackTitle = null;
            CurrentOriginalArtistName = null;
            CurrentOriginalAlbumArt = null;
            CurrentOriginalAlbumArtLarge = null;
            CurrentOriginalArtistId = null;
            CurrentOriginalAlbumId = null;
            CurrentOriginalDuration = 0;
            return;
        }

        metadata.TryGetValue("wavee.original_track_uri", out var originalUri);
        metadata.TryGetValue("wavee.original_title", out var originalTitle);
        metadata.TryGetValue("wavee.original_artist_name", out var originalArtist);
        metadata.TryGetValue("wavee.original_image_url", out var originalImage);
        metadata.TryGetValue("wavee.original_image_large_url", out var originalLargeImage);
        metadata.TryGetValue("wavee.original_image_xlarge_url", out var originalXLargeImage);
        metadata.TryGetValue("wavee.original_artist_uri", out var originalArtistUri);
        metadata.TryGetValue("wavee.original_album_uri", out var originalAlbumUri);
        metadata.TryGetValue("wavee.original_duration", out var originalDuration);

        CurrentOriginalTrackId = ExtractTrackId(originalUri);
        CurrentOriginalTrackTitle = string.IsNullOrWhiteSpace(originalTitle)
            ? connectState.Track?.Title
            : originalTitle;
        CurrentOriginalArtistName = string.IsNullOrWhiteSpace(originalArtist)
            ? connectState.Track?.Artist
            : originalArtist;
        CurrentOriginalAlbumArt = !string.IsNullOrWhiteSpace(originalImage)
            ? originalImage
            : connectState.Track?.ImageUrl;
        CurrentOriginalAlbumArtLarge = !string.IsNullOrWhiteSpace(originalLargeImage)
            ? originalLargeImage
            : (!string.IsNullOrWhiteSpace(originalXLargeImage)
                ? originalXLargeImage
                : CurrentOriginalAlbumArt);
        CurrentOriginalArtistId = originalArtistUri;
        CurrentOriginalAlbumId = originalAlbumUri;
        CurrentOriginalDuration = long.TryParse(originalDuration, out var durationMs)
            ? durationMs
            : 0;
    }

    private async Task ExtractAlbumColorAsync(string imageUrl)
    {
        if (string.Equals(_lastColorImageUrl, imageUrl, StringComparison.Ordinal))
            return;

        _lastColorImageUrl = imageUrl;
        CurrentAlbumArtColor = null;

        // Cancel any previous color extraction
        _colorCts?.Cancel();
        _colorCts?.Dispose();
        _colorCts = new CancellationTokenSource();
        var ct = _colorCts.Token;

        try
        {
            var color = await _colorService.GetColorAsync(imageUrl, ct);
            if (ct.IsCancellationRequested) return;
            if (!string.Equals(_lastColorImageUrl, imageUrl, StringComparison.Ordinal)) return;

            // Pick theme-appropriate color:
            // DarkHex = dark color (good for light theme backgrounds)
            // LightHex = light/vibrant color (good for dark theme tinting)
            var isDark = _dispatcherQueue.HasThreadAccess
                ? IsCurrentThemeDark()
                : true; // default to dark if we can't check

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested
                    || !string.Equals(_lastColorImageUrl, imageUrl, StringComparison.Ordinal))
                {
                    return;
                }

                // Both themes use the light/vibrant color for acrylic tinting.
                // Dark theme: light color tints well against dark backdrop.
                // Light theme: light color gives a subtle wash; dark color looks muddy.
                var hex = color?.LightHex ?? color?.RawHex;
                CurrentAlbumArtColor = hex;
                _logger?.LogDebug("Album art color → {Color} (light={LightHex}, raw={RawHex})",
                    hex, color?.LightHex, color?.RawHex);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to extract album art color");
        }
    }

    private static bool IsCurrentThemeDark()
    {
        try
        {
            var root = Microsoft.UI.Xaml.Window.Current?.Content as Microsoft.UI.Xaml.FrameworkElement;
            if (root != null)
                return root.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark;

            return Microsoft.UI.Xaml.Application.Current.RequestedTheme ==
                   Microsoft.UI.Xaml.ApplicationTheme.Dark;
        }
        catch
        {
            return true; // default to dark
        }
    }

    /// <summary>
    /// Extracts bare track ID from a Spotify URI (e.g. "spotify:track:abc123" → "abc123").
    /// </summary>
    private static string? ExtractId(string? uri, string prefix)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        return uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
    }

    private static string? ExtractTrackId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        const string prefix = "spotify:track:";
        return uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
    }

    private static bool IsSpotifyTrackUri(string? uri)
        => uri?.StartsWith("spotify:track:", StringComparison.Ordinal) == true;

    private static bool IsSpotifyEpisodeUri(string? uri)
        => uri?.StartsWith("spotify:episode:", StringComparison.Ordinal) == true;

    private string? GetCurrentTrackUri()
    {
        if (string.IsNullOrEmpty(CurrentTrackId)) return null;
        const string prefix = "spotify:track:";
        return CurrentTrackId.Contains(':', StringComparison.Ordinal)
            ? CurrentTrackId
            : $"{prefix}{CurrentTrackId}";
    }

    private long GetPositionClockNowMs(PlaybackState state)
    {
        if (state.Source == StateSource.Cluster && _session.IsConnected())
            return _session.Clock.NowMs;

        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static PlaybackContextInfo? ParseContext(string? contextUri)
    {
        if (string.IsNullOrEmpty(contextUri)) return null;

        var type = contextUri switch
        {
            _ when contextUri.StartsWith("spotify:album:", StringComparison.Ordinal) => PlaybackContextType.Album,
            _ when contextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal) => PlaybackContextType.Playlist,
            _ when contextUri.StartsWith("spotify:artist:", StringComparison.Ordinal) => PlaybackContextType.Artist,
            _ when contextUri.StartsWith("spotify:show:", StringComparison.Ordinal) => PlaybackContextType.Show,
            _ when contextUri.StartsWith("spotify:episode:", StringComparison.Ordinal) => PlaybackContextType.Episode,
            _ when contextUri.Contains("collection", StringComparison.OrdinalIgnoreCase) => PlaybackContextType.LikedSongs,
            _ => PlaybackContextType.Unknown
        };

        return new PlaybackContextInfo { ContextUri = contextUri, Type = type };
    }

    // ── IRecipient<QueueMetadataEnrichedMessage> ──

    public void Receive(QueueMetadataEnrichedMessage message)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var tracks = message.Tracks;
            if (tracks.Count == 0) return;

            _logger?.LogDebug("Queue metadata enriched: {Count} tracks", tracks.Count);
            ApplyCurrentTrackQueueMetadata(tracks);

            // Update raw queue items with enriched metadata
            var updated = new List<Wavee.Audio.Queue.IQueueItem>(_rawNextQueue.Count);
            foreach (var item in _rawNextQueue)
            {
                if (item is Wavee.Audio.Queue.QueueTrack qt
                    && NeedsQueueTrackEnrichment(qt)
                    && tracks.TryGetValue(qt.Uri, out var meta))
                {
                    updated.Add(qt with
                    {
                        Title = string.IsNullOrEmpty(meta.Title) ? qt.Title : meta.Title,
                        Artist = string.IsNullOrEmpty(meta.ArtistName) ? qt.Artist : meta.ArtistName,
                        ImageUrl = string.IsNullOrEmpty(meta.AlbumArt) ? qt.ImageUrl : meta.AlbumArt,
                        DurationMs = meta.DurationMs > 0 ? (int)meta.DurationMs : qt.DurationMs
                    });
                }
                else
                {
                    updated.Add(item);
                }
            }

            _rawNextQueue = updated;
            _queue = updated
                .OfType<Wavee.Audio.Queue.QueueTrack>()
                .Select(QueueItem.FromQueueTrack)
                .ToList();
            _userQueueCache = null;
            OnPropertyChanged(nameof(Queue));
            OnPropertyChanged(nameof(UserQueue));
        });
    }

    // ── IRecipient<MusicVideoAvailabilityMessage> ──

    private void ApplyCurrentTrackQueueMetadata(IReadOnlyDictionary<string, QueueTrackMetadata> tracks)
    {
        var currentUri = GetCurrentTrackUri();
        if (string.IsNullOrWhiteSpace(currentUri) || !tracks.TryGetValue(currentUri, out var meta))
            return;

        if (string.IsNullOrWhiteSpace(CurrentTrackTitle) && !string.IsNullOrWhiteSpace(meta.Title))
            CurrentTrackTitle = meta.Title;
        if (string.IsNullOrWhiteSpace(CurrentArtistName) && !string.IsNullOrWhiteSpace(meta.ArtistName))
            CurrentArtistName = meta.ArtistName;
        if (string.IsNullOrWhiteSpace(CurrentAlbumArt) && !string.IsNullOrWhiteSpace(meta.AlbumArt))
            CurrentAlbumArt = GetCurrentDisplayAlbumArt(meta.AlbumArt);
        if (Duration <= 0 && meta.DurationMs > 0)
            Duration = meta.DurationMs;

        if (string.IsNullOrWhiteSpace(meta.ArtistName))
            return;

        var rawTrack = FindRawQueueTrack(currentUri);
        var artistUri = FirstNonWhiteSpace(rawTrack?.ArtistUri, CurrentArtistId);
        if (string.IsNullOrWhiteSpace(CurrentArtistId) && !string.IsNullOrWhiteSpace(artistUri))
            CurrentArtistId = artistUri;
        if (CurrentArtists is null or { Count: 0 })
            CurrentArtists = [new ArtistCredit(meta.ArtistName, artistUri)];
    }

    private Wavee.Audio.Queue.QueueTrack? FindRawQueueTrack(string trackUri)
    {
        foreach (var item in _rawNextQueue)
        {
            if (item is Wavee.Audio.Queue.QueueTrack track
                && string.Equals(track.Uri, trackUri, StringComparison.Ordinal))
                return track;
        }

        foreach (var item in _rawPrevQueue)
        {
            if (item is Wavee.Audio.Queue.QueueTrack track
                && string.Equals(track.Uri, trackUri, StringComparison.Ordinal))
                return track;
        }

        return null;
    }

    public void Receive(MusicVideoAvailabilityMessage message)
    {
        var (audioUri, hasVideo) = message.Value;
        _logger?.LogInformation("[VideoDiscovery] MusicVideoAvailability received: audio={Audio}, hasVideo={HasVideo}, currentTrack={Current}",
            audioUri, hasVideo, GetCurrentTrackUri() ?? "<none>");

        // Stale notifications from a previous track must not flip current
        // state — the discovery service may complete after the user already
        // skipped, and CurrentTrackId is the source of truth.
        if (!string.Equals(audioUri, GetCurrentTrackUri(), StringComparison.Ordinal))
        {
            _logger?.LogInformation("[VideoDiscovery] dropping stale availability message");
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!string.Equals(audioUri, GetCurrentTrackUri(), StringComparison.Ordinal)) return;
            CurrentTrackHasMusicVideo = hasVideo;
            _logger?.LogInformation("[VideoDiscovery] CurrentTrackHasMusicVideo flipped to {Value}", hasVideo);
        });
    }

    private void SyncQueue(PlaybackState state)
    {
        _logger?.LogDebug("[QueueDebug] SyncQueue called: NextQueue={NextQueue}, PrevQueue={PrevQueue}, NextTracks={NextTracks}, PrevTracks={PrevTracks}, Changes={Changes}",
            state.NextQueue.Count, state.PrevQueue.Count, state.NextTracks.Count, state.PrevTracks.Count, state.Changes);

        QueuePosition = state.CurrentIndex;

        // Store raw IQueueItem lists for QueueControl
        _rawNextQueue = state.NextQueue;
        _rawPrevQueue = state.PrevQueue;

        // Use rich queue data when available
        if (state.NextQueue.Count > 0)
        {
            var newQueue = new List<QueueItem>();
            foreach (var item in state.NextQueue.OfType<Wavee.Audio.Queue.QueueTrack>())
                newQueue.Add(QueueItem.FromQueueTrack(item));
            _queue = newQueue;
        }
        else
        {
            // Fallback to thin TrackReference (no metadata)
            var newQueue = new List<QueueItem>(state.NextTracks.Count);
            foreach (var track in state.NextTracks)
            {
                newQueue.Add(new QueueItem
                {
                    TrackId = track.Uri,
                    Title = string.Empty,
                    ArtistName = string.Empty,
                    IsUserQueued = track.IsUserQueued,
                    Provider = track.IsUserQueued ? "queue" : "context",
                });
            }
            _queue = newQueue;
        }
        _userQueueCache = null;

        // Build previous tracks list
        if (state.PrevQueue.Count > 0)
        {
            var newPrev = new List<QueueItem>();
            foreach (var item in state.PrevQueue.OfType<Wavee.Audio.Queue.QueueTrack>())
                newPrev.Add(QueueItem.FromQueueTrack(item));
            _prevQueue = newPrev;
        }

        OnPropertyChanged(nameof(Queue));
        OnPropertyChanged(nameof(PreviousTracks));
        OnPropertyChanged(nameof(UserQueue));

        // Request enrichment for tracks with missing metadata or artwork.
        List<string> sparseUris;
        if (state.NextQueue.Count > 0)
        {
            sparseUris = state.NextQueue
                .OfType<Wavee.Audio.Queue.QueueTrack>()
                .Where(track => (IsSpotifyTrackUri(track.Uri) || IsSpotifyEpisodeUri(track.Uri))
                    && NeedsQueueTrackEnrichment(track))
                .Select(track => track.Uri)
                .Distinct()
                .ToList();
        }
        else
        {
            sparseUris = _queue
                .Where(q => (!q.HasMetadata || string.IsNullOrEmpty(q.AlbumArt))
                    && (IsSpotifyTrackUri(q.TrackId) || IsSpotifyEpisodeUri(q.TrackId)))
                .Select(q => q.TrackId)
                .Distinct()
                .ToList();
        }

        if (sparseUris.Count > 0)
        {
            _messenger.Send(new QueueEnrichmentRequestMessage(sparseUris));
        }
    }

    // ── PropertyChanged batching ──
    // During OnRemoteStateChanged, suppress individual PropertyChanged events
    // and flush them all at once to reduce binding evaluation + layout passes.

    private static bool NeedsQueueTrackEnrichment(Wavee.Audio.Queue.QueueTrack track)
        => string.IsNullOrEmpty(track.Title)
            || string.IsNullOrEmpty(track.Artist)
            || string.IsNullOrEmpty(track.ImageUrl);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isSuppressingPropertyChanged)
        {
            _pendingPropertyChanges ??= [];
            if (e.PropertyName != null)
                _pendingPropertyChanges.Add(e.PropertyName);
            return;
        }
        base.OnPropertyChanged(e);
    }

    private void FlushPropertyChanges()
    {
        if (_pendingPropertyChanges is not { Count: > 0 }) return;
        foreach (var name in _pendingPropertyChanges)
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(name));
        _pendingPropertyChanges.Clear();
    }

    // ── Messenger broadcasts ──

    partial void OnIsPlayingChanged(bool value)
    {
        _messenger.Send(new PlaybackStateChangedMessage(value));
        if (_isBatchingStateUpdate)
            _nowPlayingDirty = true;
        else
            _messenger.Send(new NowPlayingChangedMessage(CurrentContext?.ContextUri, CurrentAlbumId, value));

        // Resume clears the end-of-context banner. It would otherwise stick
        // forever once shown, since EndOfContextAsync only fires once per
        // exhausted context.
        if (value && IsAtEndOfContext) IsAtEndOfContext = false;
    }

    /// <summary>
    /// Called from <c>AppLifecycleHelper</c>'s subscription to the
    /// orchestrator's <c>EndOfContext</c> observable. Flips
    /// <see cref="IsAtEndOfContext"/> so the PlayerBar can show its inline
    /// "You've reached the end" hint.
    /// </summary>
    public void NotifyEndOfContext() => IsAtEndOfContext = true;

    public void DismissEndOfContext() => IsAtEndOfContext = false;

    public async Task<bool> SwitchToVideoAsync()
    {
        _logger?.LogInformation("[Cmd] SwitchToVideo: track={Track}, knownManifest={Manifest}, hasVideoHint={Hint}",
            CurrentTrackId ?? "<none>",
            CurrentTrackManifestId ?? "<none>",
            CurrentTrackHasMusicVideo);

        // Self-contained pattern: TrackResolver populated CurrentTrackManifestId
        // already. Pass it through so the orchestrator doesn't need to redo
        // the resolution.
        if (!string.IsNullOrEmpty(CurrentTrackManifestId))
        {
            try
            {
                var audioUrix = GetCurrentTrackUri();
                string? videoUri = null;
                if (!string.IsNullOrEmpty(audioUrix))
                    VideoMetadata?.TryGetVideoUri(audioUrix, out videoUri);

                var result = await _playbackService.SwitchToVideoAsync(
                    CurrentTrackManifestId,
                    string.IsNullOrEmpty(videoUri) ? null : videoUri,
                    CancellationToken.None);
                return result?.IsSuccess ?? false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Cmd] SwitchToVideo (self-contained) FAILED");
                return false;
            }
        }

        // Linked-URI pattern: lazily resolve via the discovery service.
        var audioUri = GetCurrentTrackUri();
        var discovery = VideoDiscovery;
        if (string.IsNullOrEmpty(audioUri) || discovery is null)
        {
            _logger?.LogWarning("[Cmd] SwitchToVideo: no manifest available for {Track}", audioUri ?? "<none>");
            return false;
        }

        try
        {
            var manifestId = await discovery.ResolveManifestIdAsync(audioUri, CancellationToken.None);
            if (string.IsNullOrEmpty(manifestId))
            {
                _logger?.LogWarning("[Cmd] SwitchToVideo: discovery service returned no manifest for {Track}", audioUri);
                return false;
            }
            discovery.TryGetVideoUri(audioUri, out var videoUri);
            var result = await _playbackService.SwitchToVideoAsync(
                manifestId,
                string.IsNullOrEmpty(videoUri) ? null : videoUri,
                CancellationToken.None);
            return result?.IsSuccess ?? false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Cmd] SwitchToVideo (linked-URI) FAILED for {Track}", audioUri);
            return false;
        }
    }

    public async Task<bool> SwitchToAudioAsync()
    {
        _logger?.LogInformation("[Cmd] SwitchToAudio: track={Track}, isVideo={IsVideo}",
            CurrentTrackId ?? "<none>",
            CurrentTrackIsVideo);

        if (!CurrentTrackIsVideo)
            return false;

        try
        {
            var result = await _playbackService.SwitchToAudioAsync(CancellationToken.None);
            return result?.IsSuccess ?? false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Cmd] SwitchToAudio FAILED");
            return false;
        }
    }

    partial void OnCurrentTrackIdChanged(string? value)
    {
        // Reset the music-video availability hint immediately on track change.
        CurrentTrackHasMusicVideo = false;
        if (string.IsNullOrEmpty(value)) return;
        if (CurrentTrackIsVideo) return;

        // Music-video discovery is track-only. Episodes and other fully-qualified
        // playable URIs must not be coerced into spotify:track:*.
        if (value.Contains(':', StringComparison.Ordinal)
            && !value.StartsWith("spotify:track:", StringComparison.Ordinal))
        {
            CurrentTrackManifestId = null;
            return;
        }

        var audioUri = value.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? value
            : $"spotify:track:{value}";

        // Cheap synchronous lookup: if we've already seen this track on a
        // GraphQL surface (artist top tracks, album page, etc.) the catalog
        // cache already knows the answer.
        var lookup = VideoMetadata?.GetKnownAvailability(audioUri);
        _logger?.LogInformation("[VideoDiscovery] cache lookup on track change: {Track} → {Result}",
            audioUri, lookup?.ToString() ?? "<unknown>");

        if (lookup.HasValue)
        {
            // Flip immediately when we already know. No NPV needed.
            CurrentTrackHasMusicVideo = lookup.Value;
            return;
        }

        // Cache says unknown → kick off background NPV discovery directly via
        // the service. Discovery completes by sending
        // MusicVideoAvailabilityMessage which our handler consumes.
        var discovery = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.UI.WinUI.Services.IMusicVideoDiscoveryService>();
        if (discovery is null)
        {
            _logger?.LogWarning("[VideoDiscovery] discovery service not registered — button will not surface for {Track}", audioUri);
            return;
        }
        discovery.BeginBackgroundDiscovery(audioUri);
    }

    partial void OnCurrentContextChanged(PlaybackContextInfo? value)
    {
        _messenger.Send(new PlaybackContextChangedMessage(value));
        if (_isBatchingStateUpdate)
            _nowPlayingDirty = true;
        else
            _messenger.Send(new NowPlayingChangedMessage(value?.ContextUri, CurrentAlbumId, IsPlaying));
    }

    partial void OnCurrentAlbumIdChanged(string? value)
    {
        // Track changed to a different album → album cards need to re-check the
        // "is this my album playing?" predicate. Coalesce inside a batched update.
        if (_isBatchingStateUpdate)
            _nowPlayingDirty = true;
        else
            _messenger.Send(new NowPlayingChangedMessage(CurrentContext?.ContextUri, value, IsPlaying));
    }

    /// <summary>
    /// Sends a single coalesced <see cref="NowPlayingChangedMessage"/> if any
    /// contributing property changed during the batched state update.
    /// </summary>
    private void FlushNowPlayingMessage()
    {
        if (!_nowPlayingDirty) return;
        _nowPlayingDirty = false;
        _messenger.Send(new NowPlayingChangedMessage(CurrentContext?.ContextUri, CurrentAlbumId, IsPlaying));
    }

    // ── Commands ──
    // All commands delegate to IPlaybackService, which routes through ConnectCommandExecutor
    // (handles both local engine and remote device routing internally).

    public void PlayPause()
    {
        var pendingSeek = _pendingSeekPositionMs;
        _pendingSeekPositionMs = null;

        var playing = IsPlaying;
        _logger?.LogInformation("[Cmd] PlayPause: isPlaying={Was} → optimistic={Next}, track={Track}, pendingSeek={Seek}",
            playing, !playing, CurrentTrackId ?? "<none>", pendingSeek?.ToString("F0") ?? "<none>");

        IsPlaying = !playing; // optimistic
        if (!playing && CurrentTrackId != null) SetBuffering(CurrentTrackId);

        _ = _playbackService.TogglePlayPauseAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger?.LogError(t.Exception, "[Cmd] PlayPause FAILED — reverting optimistic IsPlaying={Was}", playing);
                _dispatcherQueue.TryEnqueue(() => { IsPlaying = playing; ClearBuffering(); });
            }
            else
            {
                _logger?.LogDebug("[Cmd] PlayPause accepted by engine/service");
                if (!playing && pendingSeek.HasValue)
                    _dispatcherQueue.TryEnqueue(() => Seek(pendingSeek.Value));
            }
        });
    }

    public void Next()
    {
        _logger?.LogInformation("[Cmd] Next: current track={Track}", CurrentTrackId ?? "<none>");
        _ = _playbackService.SkipNextAsync().ContinueWith(t =>
        {
            if (t.IsFaulted) _logger?.LogError(t.Exception, "[Cmd] Next FAILED");
            else _logger?.LogDebug("[Cmd] Next accepted");
        });
    }

    public void Previous()
    {
        _logger?.LogInformation("[Cmd] Previous: current track={Track}, pos={Pos}ms", CurrentTrackId ?? "<none>", (long)Position);
        _ = _playbackService.SkipPreviousAsync().ContinueWith(t =>
        {
            if (t.IsFaulted) _logger?.LogError(t.Exception, "[Cmd] Previous FAILED");
            else _logger?.LogDebug("[Cmd] Previous accepted");
        });
    }

    public void Seek(double positionMs)
    {
        if (string.IsNullOrEmpty(CurrentTrackId))
        {
            _logger?.LogWarning("[Cmd] Seek({Pos}ms) ignored — no track loaded", (long)positionMs);
            return;
        }

        // If not playing, store position for when playback starts
        if (!IsPlaying)
        {
            _logger?.LogDebug("[Cmd] Seek({Pos}ms) deferred — not playing", (long)positionMs);
            _pendingSeekPositionMs = positionMs;
            Position = positionMs;
            return;
        }

        _logger?.LogInformation("[Cmd] Seek: {From}ms → {To}ms, track={Track}", (long)Position, (long)positionMs, CurrentTrackId);
        // Optimistic UI: jump the slider to the user's target immediately. The
        // sentinel below tells the cluster handler to drop stale pre-seek frames
        // until a frame near the target arrives (= seek confirmed).
        Position = positionMs;
        _pendingSeekPositionMs = positionMs;
        _seekConfirmationCts?.Cancel();
        _seekConfirmationCts?.Dispose();
        _seekConfirmationCts = new CancellationTokenSource();
        var timeoutToken = _seekConfirmationCts.Token;
        _ = Task.Delay(SeekConfirmationTimeoutMs, timeoutToken).ContinueWith(t =>
        {
            // If the cluster never echoed our target (network/AudioHost stall),
            // give up on suppression so the slider isn't stuck on a stale value.
            if (!t.IsCanceled)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_pendingSeekPositionMs.HasValue)
                    {
                        _logger?.LogWarning("[Cmd] Seek confirmation timed out after {Ms}ms — releasing suppression", SeekConfirmationTimeoutMs);
                        _pendingSeekPositionMs = null;
                    }
                });
            }
        }, TaskScheduler.Default);
        SetBuffering(CurrentTrackId);
        _ = _playbackService.SeekAsync((long)positionMs)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger?.LogError(t.Exception, "[Cmd] Seek FAILED");
                    ClearBuffering();
                }
                else
                {
                    _logger?.LogDebug("[Cmd] Seek accepted");
                }
            });
    }

    public void SetShuffle(bool shuffle)
    {
        _logger?.LogInformation("[Cmd] SetShuffle: {Was} → {Next}", IsShuffle, shuffle);
        _ = _playbackService.SetShuffleAsync(shuffle).ContinueWith(t =>
        {
            if (t.IsFaulted) _logger?.LogError(t.Exception, "[Cmd] SetShuffle FAILED");
            else _logger?.LogDebug("[Cmd] SetShuffle accepted");
        });
    }

    public void SetRepeatMode(RepeatMode mode)
    {
        _logger?.LogInformation("[Cmd] SetRepeatMode: {Was} → {Next}", RepeatMode, mode);
        _ = _playbackService.SetRepeatModeAsync(mode).ContinueWith(t =>
        {
            if (t.IsFaulted) _logger?.LogError(t.Exception, "[Cmd] SetRepeatMode FAILED");
            else _logger?.LogDebug("[Cmd] SetRepeatMode accepted");
        });
    }

    // Guard: when true, volume changes are from remote state sync — don't send commands back
    public void SetPlaybackSpeed(double speed)
    {
        var normalized = Math.Clamp(speed, 0.5, 3.5);
        var previous = PlaybackSpeed;
        _logger?.LogInformation("[Cmd] SetPlaybackSpeed: {Was} -> {Next}", previous, normalized);
        PlaybackSpeed = normalized;
        _ = _playbackService.SetPlaybackSpeedAsync(normalized).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger?.LogError(t.Exception, "[Cmd] SetPlaybackSpeed FAILED");
                _dispatcherQueue.TryEnqueue(() => PlaybackSpeed = previous);
            }
            else
            {
                _logger?.LogDebug("[Cmd] SetPlaybackSpeed accepted");
            }
        });
    }

    private bool _suppressVolumeCommand;

    /// <summary>
    /// Sets volume on the UI property without triggering a remote command.
    /// Used during init when we want to sync the slider without sending set_volume to Spotify.
    /// </summary>
    public void SetVolumeWithoutCommand(double volume)
    {
        _suppressVolumeCommand = true;
        Volume = volume;
        _suppressVolumeCommand = false;
    }

    /// <summary>
    /// Called by MVVM source generator when the Volume property changes.
    /// Propagates the volume to the local engine or remote service.
    /// Only fires commands for user-initiated changes (not remote state sync).
    /// </summary>
    partial void OnVolumeChanged(double value)
    {
        if (_suppressVolumeCommand) return;
        _ = _playbackService.SetVolumeAsync((int)Math.Round(value));
    }

    public void PlayContext(PlaybackContextInfo context, int startIndex = 0)
    {
        _logger?.LogInformation("[Cmd] PlayContext: uri={Uri}, startIndex={StartIndex}", context.ContextUri, startIndex);
        SetBuffering(null); // context play — we don't know the track ID yet
        _ = _playbackService.PlayContextAsync(context.ContextUri, new PlayContextOptions { StartIndex = startIndex })
            .ContinueWith(t =>
            {
                if (t.IsFaulted) { _logger?.LogError(t.Exception, "[Cmd] PlayContext FAILED: uri={Uri}", context.ContextUri); ClearBuffering(); }
                else _logger?.LogDebug("[Cmd] PlayContext accepted: uri={Uri}", context.ContextUri);
            });
    }

    public void PlayTrack(string trackId, PlaybackContextInfo? context = null)
    {
        _logger?.LogInformation("[Cmd] PlayTrack: trackId={TrackId}, context={Context}", trackId, context?.ContextUri ?? "<none>");
        SetBuffering(trackId);
        if (context != null)
            _ = _playbackService.PlayTrackInContextAsync(trackId, context.ContextUri)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) { _logger?.LogError(t.Exception, "[Cmd] PlayTrack FAILED: trackId={TrackId}", trackId); ClearBuffering(); }
                    else _logger?.LogDebug("[Cmd] PlayTrack accepted: trackId={TrackId}", trackId);
                });
        else
            _ = _playbackService.PlayTracksAsync([trackId])
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) { _logger?.LogError(t.Exception, "[Cmd] PlayTrack FAILED: trackId={TrackId}", trackId); ClearBuffering(); }
                    else _logger?.LogDebug("[Cmd] PlayTrack accepted: trackId={TrackId}", trackId);
                });
    }

    public void AddToQueue(string trackId)
    {
        _logger?.LogInformation("[Cmd] AddToQueue: trackId={TrackId}", trackId);
        _ = _playbackService.AddToQueueAsync(trackId).ContinueWith(t =>
        {
            if (t.IsFaulted) _logger?.LogError(t.Exception, "[Cmd] AddToQueue FAILED: trackId={TrackId}", trackId);
        });
    }

    public void AddToQueue(IEnumerable<string> trackIds)
    {
        foreach (var trackId in trackIds)
        {
            _logger?.LogInformation("[Cmd] AddToQueue: trackId={TrackId}", trackId);
            _ = _playbackService.AddToQueueAsync(trackId).ContinueWith(t =>
            {
                if (t.IsFaulted) _logger?.LogError(t.Exception, "[Cmd] AddToQueue FAILED: trackId={TrackId}", trackId);
            });
        }
    }

    public void LoadQueue(IReadOnlyList<QueueItem> items, PlaybackContextInfo context, int startIndex = 0)
    {
        var trackUris = items.Select(i => i.TrackId).ToList();
        _logger?.LogInformation("[Cmd] LoadQueue: {Count} tracks, startIndex={StartIndex}, context={Context}",
            trackUris.Count, startIndex, context.ContextUri);
        _ = _playbackService.PlayTracksAsync(trackUris, startIndex, context, items).ContinueWith(t =>
        {
            if (t.IsFaulted) _logger?.LogError(t.Exception, "[Cmd] LoadQueue FAILED: {Count} tracks", trackUris.Count);
            else _logger?.LogDebug("[Cmd] LoadQueue accepted: {Count} tracks", trackUris.Count);
        });
    }

    // ── Buffering state helpers ──

    public void NotifyBuffering(string? trackId) => SetBuffering(trackId);

    private void SetBuffering(string? trackId)
    {
        _logger?.LogDebug("[UI] SetBuffering: trackId={TrackId}", trackId ?? "<context>");
        // Called from UI thread (button clicks), set synchronously for immediate visual feedback
        if (_dispatcherQueue.HasThreadAccess)
        {
            BufferingTrackId = trackId;
            IsBuffering = true;
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                BufferingTrackId = trackId;
                IsBuffering = true;
            });
        }
    }

    public void ClearBuffering()
    {
        _logger?.LogDebug("[UI] ClearBuffering");
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsBuffering = false;
            BufferingTrackId = null;
        });
    }

    public void Dispose()
    {
        _stateSubscription?.Dispose();
        _seekConfirmationCts?.Cancel();
        _seekConfirmationCts?.Dispose();
        _seekConfirmationCts = null;
        _colorCts?.Cancel();
        _colorCts?.Dispose();
        _colorCts = null;
        _messenger.UnregisterAll(this);
    }
}
