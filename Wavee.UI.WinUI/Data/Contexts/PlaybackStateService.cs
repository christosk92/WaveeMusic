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
using Wavee.Connect;
using Wavee.Core.Http;
using Wavee.Core.Session;
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
    IRecipient<QueueMetadataEnrichedMessage>
{
    private readonly Session _session;
    private readonly IPlaybackService _playbackService;
    private readonly IColorService _colorService;
    private readonly IMessenger _messenger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private readonly IHomeFeedCache? _homeFeedCache;
    private List<QueueItem> _queue = [];
    private List<QueueItem> _prevQueue = [];
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
    private double? _pendingSeekPositionMs;
    private long _lastPositionLogAtMs;

    // ── State properties ──

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _currentTrackId;
    [ObservableProperty] private string? _currentTrackTitle;
    [ObservableProperty] private string? _currentArtistName;
    [ObservableProperty] private string? _currentAlbumArt;
    [ObservableProperty] private string? _currentAlbumArtLarge;
    [ObservableProperty] private string? _currentArtistId;
    [ObservableProperty] private string? _currentAlbumId;
    [ObservableProperty] private string? _currentAlbumArtColor;
    [ObservableProperty] private IReadOnlyList<ArtistCredit>? _currentArtists;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _volume = 100.0;
    [ObservableProperty] private bool _isShuffle;
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.Off;
    [ObservableProperty] private PlaybackContextInfo? _currentContext;
    [ObservableProperty] private int _queuePosition;
    [ObservableProperty] private bool _isPlayingRemotely;
    [ObservableProperty] private string? _activeDeviceName;
    [ObservableProperty] private bool _isVolumeRestricted;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private string? _bufferingTrackId;

    public IReadOnlyList<QueueItem> Queue => _queue;
    public IReadOnlyList<QueueItem> PreviousTracks => _prevQueue;
    public IReadOnlyList<QueueItem> UserQueue => _queue.Where(q => q.IsUserQueued).ToList();
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

        // Try subscribing now in case session is already connected
        TrySubscribeToRemoteState();
    }

    // ── IRecipient<AuthStatusChangedMessage> ──

    public void Receive(AuthStatusChangedMessage message)
    {
        if (message.Value == AuthStatus.Authenticated)
        {
            _logger?.LogInformation("Auth status: Authenticated — subscribing to remote playback state");
            _dispatcherQueue.TryEnqueue(TrySubscribeToRemoteState);
        }
    }

    // ── Remote state bridge ──

    private void TrySubscribeToRemoteState()
    {
        // Already subscribed
        if (_stateSubscription != null) return;

        var stateManager = _session.PlaybackState;
        if (stateManager == null)
        {
            _logger?.LogDebug("PlaybackStateManager not available yet (session not connected)");
            return;
        }

        _stateSubscription = stateManager.StateChanges
            .Subscribe(
                OnRemoteStateChanged,
                ex => _logger?.LogError(ex, "Error in remote playback state subscription"));

        _logger?.LogInformation("Subscribed to PlaybackStateManager.StateChanges — remote state bridge active");
    }

    private void OnRemoteStateChanged(PlaybackState state)
    {
        _logger?.LogDebug("Remote state update: changes={Changes}, status={Status}, track={Track}",
            state.Changes, state.Status, state.Track?.Title ?? "<none>");

        _dispatcherQueue.TryEnqueue(() =>
        {
            _isBatchingStateUpdate = true;
            _isSuppressingPropertyChanged = true;

            try
            {
                // First state received after subscribing — BehaviorSubject replays with partial
                // change flags, so force a full sync to populate all UI properties.
                //
                // Also clamp a stale "Playing" status on cold start: the cluster-replayed state
                // carries whatever the user was last doing in Spotify (often Playing), but on
                // app launch nothing is actually playing here. Trusting the replayed Playing flag
                // makes the PlayerBar render the pause icon while the audio pipeline sits idle.
                // Only the Local state source is authoritative for "are we currently playing";
                // until a Local state arrives, treat the replay as Paused.
                if (_isFirstStateUpdate)
                {
                    _isFirstStateUpdate = false;
                    var clampedStatus = state.Status == PlaybackStatus.Playing
                                        && state.Source != StateSource.Local
                        ? PlaybackStatus.Paused
                        : state.Status;
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

                        // Request enrichment from TrackMetadataEnricher (if it exists post-connect)
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

                    // Position is already server-clock-corrected by the AudioHost before IPC.
                    // Use local wall-clock for the small IPC transit delta only.
                    long correctedNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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

                    Position = calculatedPos;

                    // Update duration here too — it may change when local engine
                    // starts playing a ghost track (cluster had duration=0)
                    if (state.DurationMs > 0 && Duration != state.DurationMs)
                        Duration = state.DurationMs;
                }

                // Options
                if (state.Changes.HasFlag(StateChanges.Options))
                {
                    _logger?.LogDebug("UI bridge: options → shuffle={Shuffle}, repeatCtx={RepeatCtx}, repeatTrack={RepeatTrack}",
                        state.Options.Shuffling, state.Options.RepeatingContext, state.Options.RepeatingTrack);
                    IsShuffle = state.Options.Shuffling;
                    RepeatMode = state.Options.RepeatingTrack
                        ? RepeatMode.Track
                        : state.Options.RepeatingContext
                            ? RepeatMode.Context
                            : RepeatMode.Off;
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
                    _logger?.LogDebug("UI bridge: remote={IsRemote}, device={DeviceName} ({DeviceId})",
                        isRemote, state.ActiveDeviceName, state.ActiveDeviceId);
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
            }
        });
    }

    // ── IRecipient<TrackMetadataEnrichedMessage> ──

    public void Receive(TrackMetadataEnrichedMessage message)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Only apply if this enrichment is for the currently playing track
            if (CurrentTrackId != message.TrackId) return;

            if (message.Title != null) CurrentTrackTitle = message.Title;
            if (message.ArtistName != null) CurrentArtistName = message.ArtistName;
            if (message.AlbumArt != null) CurrentAlbumArt = message.AlbumArt;
            if (message.AlbumArtLarge != null) CurrentAlbumArtLarge = message.AlbumArtLarge;
            if (message.ArtistId != null) CurrentArtistId = message.ArtistId;
            if (message.AlbumId != null) CurrentAlbumId = message.AlbumId;
            if (message.Artists != null) CurrentArtists = message.Artists;

            // Re-extract color if album art was enriched
            if (message.AlbumArt != null)
                _ = ExtractAlbumColorAsync(message.AlbumArt);
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

        CurrentTrackTitle = connectState.Track?.Title;
        CurrentArtistName = connectState.Track?.Artist;
        CurrentAlbumArt = connectState.Track?.ImageUrl;
        CurrentAlbumArtLarge = connectState.Track?.ImageLargeUrl
            ?? connectState.Track?.ImageXLargeUrl
            ?? connectState.Track?.ImageUrl;
        CurrentArtistId = connectState.Track?.ArtistUri;
        CurrentAlbumId = connectState.Track?.AlbumUri;
        CurrentArtists = null; // Connect state lacks per-artist data; enricher will populate

        // Set CurrentTrackId last — fires PropertyChanged which triggers lyrics fetch
        CurrentTrackId = trackId;

        // Extract color from album art
        var imageUrl = connectState.Track?.ImageUrl;
        if (!string.IsNullOrEmpty(imageUrl))
            _ = ExtractAlbumColorAsync(imageUrl);
        else
        {
            _lastColorImageUrl = null;
            CurrentAlbumArtColor = null;
        }
    }

    private async Task ExtractAlbumColorAsync(string imageUrl)
    {
        if (string.Equals(_lastColorImageUrl, imageUrl, StringComparison.Ordinal))
            return;

        _lastColorImageUrl = imageUrl;
        CurrentAlbumArtColor = null;

        // Cancel any previous color extraction
        _colorCts?.Cancel();
        _colorCts = new CancellationTokenSource();
        var ct = _colorCts.Token;

        try
        {
            var color = await _colorService.GetColorAsync(imageUrl, ct);
            if (ct.IsCancellationRequested) return;

            // Pick theme-appropriate color:
            // DarkHex = dark color (good for light theme backgrounds)
            // LightHex = light/vibrant color (good for dark theme tinting)
            var isDark = _dispatcherQueue.HasThreadAccess
                ? IsCurrentThemeDark()
                : true; // default to dark if we can't check

            _dispatcherQueue.TryEnqueue(() =>
            {
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

    private static PlaybackContextInfo? ParseContext(string? contextUri)
    {
        if (string.IsNullOrEmpty(contextUri)) return null;

        var type = contextUri switch
        {
            _ when contextUri.StartsWith("spotify:album:", StringComparison.Ordinal) => PlaybackContextType.Album,
            _ when contextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal) => PlaybackContextType.Playlist,
            _ when contextUri.StartsWith("spotify:artist:", StringComparison.Ordinal) => PlaybackContextType.Artist,
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

            // Update raw queue items with enriched metadata
            var updated = new List<Wavee.Audio.Queue.IQueueItem>(_rawNextQueue.Count);
            foreach (var item in _rawNextQueue)
            {
                if (item is Wavee.Audio.Queue.QueueTrack qt && !qt.HasMetadata && tracks.TryGetValue(qt.Uri, out var meta))
                {
                    updated.Add(qt with
                    {
                        Title = meta.Title,
                        Artist = meta.ArtistName,
                        ImageUrl = meta.AlbumArt,
                        DurationMs = meta.DurationMs > 0 ? (int)meta.DurationMs : qt.DurationMs
                    });
                }
                else
                {
                    updated.Add(item);
                }
            }

            _rawNextQueue = updated;
            OnPropertyChanged(nameof(Queue));
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

        // Request enrichment for tracks with missing metadata
        var sparseUris = _queue
            .Where(q => !q.HasMetadata && !string.IsNullOrEmpty(q.TrackId))
            .Select(q => q.TrackId)
            .ToList();

        if (sparseUris.Count > 0)
        {
            _messenger.Send(new QueueEnrichmentRequestMessage(sparseUris));
        }
    }

    // ── PropertyChanged batching ──
    // During OnRemoteStateChanged, suppress individual PropertyChanged events
    // and flush them all at once to reduce binding evaluation + layout passes.

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
            _messenger.Send(new NowPlayingChangedMessage(CurrentContext?.ContextUri, value));
    }

    partial void OnCurrentTrackIdChanged(string? value) => _messenger.Send(new TrackChangedMessage(value));

    partial void OnCurrentContextChanged(PlaybackContextInfo? value)
    {
        _messenger.Send(new PlaybackContextChangedMessage(value));
        if (_isBatchingStateUpdate)
            _nowPlayingDirty = true;
        else
            _messenger.Send(new NowPlayingChangedMessage(value?.ContextUri, IsPlaying));
    }

    /// <summary>
    /// Sends a single coalesced <see cref="NowPlayingChangedMessage"/> if any
    /// contributing property changed during the batched state update.
    /// </summary>
    private void FlushNowPlayingMessage()
    {
        if (!_nowPlayingDirty) return;
        _nowPlayingDirty = false;
        _messenger.Send(new NowPlayingChangedMessage(CurrentContext?.ContextUri, IsPlaying));
    }

    // ── Commands ──
    // All commands delegate to IPlaybackService, which routes through ConnectCommandExecutor
    // (handles both local engine and remote device routing internally).

    public void PlayPause()
    {
        var pendingSeek = _pendingSeekPositionMs;
        _pendingSeekPositionMs = null;

        var playing = IsPlaying;
        IsPlaying = !playing; // optimistic
        if (!playing && CurrentTrackId != null) SetBuffering(CurrentTrackId);

        _ = _playbackService.TogglePlayPauseAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                _dispatcherQueue.TryEnqueue(() => { IsPlaying = playing; ClearBuffering(); });
            else if (!playing && pendingSeek.HasValue)
                _dispatcherQueue.TryEnqueue(() => Seek(pendingSeek.Value));
        });
    }

    public void Next() => _ = _playbackService.SkipNextAsync();

    public void Previous() => _ = _playbackService.SkipPreviousAsync();

    public void Seek(double positionMs)
    {
        if (string.IsNullOrEmpty(CurrentTrackId))
            return;

        // If not playing, store position for when playback starts
        if (!IsPlaying)
        {
            _pendingSeekPositionMs = positionMs;
            Position = positionMs;
            return;
        }

        _pendingSeekPositionMs = null;
        SetBuffering(CurrentTrackId);
        _ = _playbackService.SeekAsync((long)positionMs)
            .ContinueWith(t => { if (t.IsFaulted) ClearBuffering(); });
    }

    public void SetShuffle(bool shuffle) => _ = _playbackService.SetShuffleAsync(shuffle);

    public void SetRepeatMode(RepeatMode mode) => _ = _playbackService.SetRepeatModeAsync(mode);

    // Guard: when true, volume changes are from remote state sync — don't send commands back
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
        SetBuffering(null); // context play — we don't know the track ID yet
        _ = _playbackService.PlayContextAsync(context.ContextUri, new PlayContextOptions { StartIndex = startIndex })
            .ContinueWith(t => { if (t.IsFaulted) ClearBuffering(); });
    }

    public void PlayTrack(string trackId, PlaybackContextInfo? context = null)
    {
        SetBuffering(trackId);
        if (context != null)
            _ = _playbackService.PlayTrackInContextAsync(trackId, context.ContextUri)
                .ContinueWith(t => { if (t.IsFaulted) ClearBuffering(); });
        else
            _ = _playbackService.PlayTracksAsync([trackId])
                .ContinueWith(t => { if (t.IsFaulted) ClearBuffering(); });
    }

    public void AddToQueue(string trackId) => _ = _playbackService.AddToQueueAsync(trackId);

    public void AddToQueue(IEnumerable<string> trackIds)
    {
        foreach (var trackId in trackIds)
            _ = _playbackService.AddToQueueAsync(trackId);
    }

    public void LoadQueue(IReadOnlyList<QueueItem> items, PlaybackContextInfo context, int startIndex = 0)
    {
        var trackUris = items.Select(i => i.TrackId).ToList();
        _ = _playbackService.PlayTracksAsync(trackUris, startIndex);
    }

    // ── Buffering state helpers ──

    public void NotifyBuffering(string? trackId) => SetBuffering(trackId);

    private void SetBuffering(string? trackId)
    {
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
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsBuffering = false;
            BufferingTrackId = null;
        });
    }

    public void Dispose()
    {
        _stateSubscription?.Dispose();
        _messenger.UnregisterAll(this);
    }
}
