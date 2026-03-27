using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    IRecipient<AuthStatusChangedMessage>
{
    private readonly Session _session;
    private readonly IPlaybackService _playbackService;
    private readonly IColorService _colorService;
    private readonly IMessenger _messenger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private readonly IHomeFeedCache? _homeFeedCache;
    private readonly ObservableCollection<QueueItem> _queue = [];
    private IDisposable? _stateSubscription;
    private CancellationTokenSource? _colorCts;
    private bool _isFirstStateUpdate = true;
    private double? _pendingSeekPositionMs;

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
            // First state received after subscribing — BehaviorSubject replays with partial
            // change flags, so force a full sync to populate all UI properties
            if (_isFirstStateUpdate)
            {
                _isFirstStateUpdate = false;
                state = state with { Changes = StateChanges.All };
            }

            // Track info
            if (state.Changes.HasFlag(StateChanges.Track))
            {
                _pendingSeekPositionMs = null;
                _logger?.LogInformation("UI bridge: track → {Title} by {Artist} (uri={Uri})",
                    state.Track?.Title, state.Track?.Artist, state.Track?.Uri);
                // Extract bare ID from URI (e.g. "spotify:track:abc123" → "abc123")
                // ITrackItem.Id uses bare IDs, not full URIs
                CurrentTrackId = ExtractTrackId(state.Track?.Uri);
                CurrentTrackTitle = state.Track?.Title;
                CurrentArtistName = state.Track?.Artist;
                CurrentAlbumArt = state.Track?.ImageUrl;
                CurrentAlbumArtLarge = state.Track?.ImageLargeUrl ?? state.Track?.ImageXLargeUrl ?? state.Track?.ImageUrl;
                CurrentArtistId = state.Track?.ArtistUri;
                CurrentAlbumId = state.Track?.AlbumUri;
                Duration = state.DurationMs;

                // Extract color from album art (fire-and-forget)
                var imageUrl = state.Track?.ImageUrl;
                if (!string.IsNullOrEmpty(imageUrl))
                    _ = ExtractAlbumColorAsync(imageUrl);
                else
                    CurrentAlbumArtColor = null;
            }

            // Playback status
            if (state.Changes.HasFlag(StateChanges.Status))
            {
                var hasLocalEngine = _session.PlaybackState?.IsBidirectional == true;
                var activeDeviceId = state.ActiveDeviceId;

                // We are the active device with a local engine = real playback, never suppress
                var isSelfWithEngine = activeDeviceId == _session.Config.DeviceId && hasLocalEngine;

                // No active device = nothing is actually playing anywhere
                // OR we are the active device but have no local engine = we can't produce audio
                var noRealPlayback = !isSelfWithEngine
                    && (string.IsNullOrEmpty(activeDeviceId)
                        || string.IsNullOrEmpty(state.ActiveDeviceName)
                        || (activeDeviceId == _session.Config.DeviceId && !hasLocalEngine));

                if (noRealPlayback && state.Status == PlaybackStatus.Playing)
                {
                    _logger?.LogDebug("UI bridge: status → suppressed to Paused (no real playback, activeDevice={Device})", activeDeviceId);
                    IsPlaying = false;
                }
                else
                {
                    IsPlaying = state.Status == PlaybackStatus.Playing;

                    // Clear buffering when playback status confirms playing or stopped
                    if (IsBuffering && state.Status is PlaybackStatus.Playing or PlaybackStatus.Stopped)
                    {
                        IsBuffering = false;
                        BufferingTrackId = null;
                    }
                }

                // Suspend background HTTP activity during audio playback to avoid connection pool contention
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

                var calculatedPos = PlaybackStateHelpers.CalculateCurrentPosition(state);

                _logger?.LogInformation(
                    "UI bridge: position → {CalculatedMs}ms " +
                    "(raw={RawMs}ms, timestamp={Timestamp}, now={Now}, " +
                    "elapsed={Elapsed}ms, status={Status}, duration={Duration}ms)",
                    calculatedPos,
                    state.PositionMs,
                    state.Timestamp,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.Timestamp,
                    state.Status,
                    state.DurationMs);

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

                // Re-evaluate IsPlaying when device changes
                // (Status section only runs on StateChanges.Status, misses device-triggered changes)
                if (state.Status == PlaybackStatus.Playing)
                {
                    var hasLocal = _session.PlaybackState?.IsBidirectional == true;
                    var deviceId = state.ActiveDeviceId;
                    var isSelfLocal = deviceId == _session.Config.DeviceId && hasLocal;
                    var noRealPlayback = !isSelfLocal
                        && (string.IsNullOrEmpty(deviceId)
                            || string.IsNullOrEmpty(state.ActiveDeviceName)
                            || (deviceId == _session.Config.DeviceId && !hasLocal));
                    if (noRealPlayback)
                    {
                        _logger?.LogDebug("UI bridge: IsPlaying suppressed on device change (ghost device)");
                        IsPlaying = false;
                    }
                }
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
        });
    }

    private async Task ExtractAlbumColorAsync(string imageUrl)
    {
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

    private void SyncQueue(PlaybackState state)
    {
        _queue.Clear();
        QueuePosition = state.CurrentIndex;

        foreach (var track in state.NextTracks)
        {
            _queue.Add(new QueueItem
            {
                TrackId = track.Uri,
                Title = string.Empty,
                ArtistName = string.Empty,
                IsUserQueued = track.IsUserQueued
            });
        }
    }

    // ── Messenger broadcasts ──

    partial void OnIsPlayingChanged(bool value) => _messenger.Send(new PlaybackStateChangedMessage(value));
    partial void OnCurrentTrackIdChanged(string? value) => _messenger.Send(new TrackChangedMessage(value));
    partial void OnCurrentContextChanged(PlaybackContextInfo? value) => _messenger.Send(new PlaybackContextChangedMessage(value));

    // ── Commands ──
    // Fast path: local engine commands bypass the retry engine + semaphore entirely.
    // Slow path: remote device commands go through PlaybackService (retry + error handling).

    private Wavee.Connect.IPlaybackEngine? _cachedLocalEngine;

    private Wavee.Connect.IPlaybackEngine? LocalEngine =>
        _cachedLocalEngine ??= (CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<IPlaybackCommandExecutor>() as ConnectCommandExecutor)?.LocalEngine;

    private bool IsLocalPlayback
    {
        get
        {
            var engine = LocalEngine;
            if (engine == null) return false;
            var activeDevice = _session.PlaybackState?.CurrentState.ActiveDeviceId;
            return string.IsNullOrEmpty(activeDevice) || activeDevice == _session.Config.DeviceId;
        }
    }

    public void PlayPause()
    {
        var pendingSeek = _pendingSeekPositionMs;
        _pendingSeekPositionMs = null;

        if (IsLocalPlayback)
        {
            var playing = IsPlaying;
            var stateManager = _session.PlaybackState!;

            // Optimistic UI update — instant visual feedback before engine call
            IsPlaying = !playing;
            if (!playing && CurrentTrackId != null) SetBuffering(CurrentTrackId);

            _ = Task.Run(async () =>
            {
                if (playing) await LocalEngine!.PauseAsync();
                else await stateManager.ResumeAsync(); // Handles ghost state internally
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _dispatcherQueue.TryEnqueue(() => { IsPlaying = playing; ClearBuffering(); });
                }
                else if (!playing && pendingSeek.HasValue)
                {
                    _dispatcherQueue.TryEnqueue(() => Seek(pendingSeek.Value));
                }
            });
            return;
        }

        if (!IsPlaying && CurrentTrackId != null)
            SetBuffering(CurrentTrackId);
        _ = _playbackService.TogglePlayPauseAsync().ContinueWith(t =>
        {
            if (t.IsFaulted) ClearBuffering();
            else if (pendingSeek.HasValue)
                _dispatcherQueue.TryEnqueue(() => Seek(pendingSeek.Value));
        });
    }

    public void Next()
    {
        if (IsLocalPlayback) { _ = Task.Run(() => LocalEngine!.SkipNextAsync()); return; }
        _ = _playbackService.SkipNextAsync();
    }

    public void Previous()
    {
        if (IsLocalPlayback) { _ = Task.Run(() => LocalEngine!.SkipPreviousAsync()); return; }
        _ = _playbackService.SkipPreviousAsync();
    }

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
        if (IsLocalPlayback)
        {
            _ = Task.Run(() => LocalEngine!.SeekAsync((long)positionMs))
                .ContinueWith(t => { if (t.IsFaulted) ClearBuffering(); });
            return;
        }
        _ = _playbackService.SeekAsync((long)positionMs)
            .ContinueWith(t => { if (t.IsFaulted) ClearBuffering(); });
    }

    public void SetShuffle(bool shuffle)
    {
        if (IsLocalPlayback) { _ = Task.Run(() => LocalEngine!.SetShuffleAsync(shuffle)); return; }
        _ = _playbackService.SetShuffleAsync(shuffle);
    }

    public void SetRepeatMode(RepeatMode mode)
    {
        if (IsLocalPlayback)
        {
            _ = Task.Run(async () =>
            {
                await LocalEngine!.SetRepeatContextAsync(mode == RepeatMode.Context);
                await LocalEngine!.SetRepeatTrackAsync(mode == RepeatMode.Track);
            });
            return;
        }
        _ = _playbackService.SetRepeatModeAsync(mode);
    }

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

        // Convert 0-100 UI range to 0.0-1.0 linear for local engine
        var engine = LocalEngine;
        if (engine != null)
        {
            // Direct call — SetVolumeAsync is synchronous (just sets a float)
            var linear = (float)(value / 100.0);
            engine.SetVolumeAsync(linear);
            return;
        }
        // Remote: send 0-100 integer percent (only if no local engine)
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
        // For LoadQueue, play the context starting at the given index
        _ = _playbackService.PlayContextAsync(context.ContextUri, new PlayContextOptions { StartIndex = startIndex });
    }

    // ── Buffering state helpers ──

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

    private void ClearBuffering()
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
