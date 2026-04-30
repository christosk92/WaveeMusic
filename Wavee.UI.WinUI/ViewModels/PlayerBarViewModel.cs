using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Wavee.UI.Contracts;
using Wavee.UI.Enums;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services.Docking;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for the player bar control. Delegates playback state and commands
/// to <see cref="IPlaybackStateService"/> while keeping display-only concerns
/// (formatting, seeking UI, mute toggle) local.
/// </summary>
public sealed partial class PlayerBarViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackStateService _playbackStateService;
    private readonly IConnectivityService? _connectivityService;
    private readonly INotificationService? _notificationService;
    private readonly IPanelDockingService? _dockingService;
    private readonly ILogger? _logger;
    private bool _disposed;
    private DispatcherTimer? _positionTimer;
    private DateTime _lastServicePositionUpdate = DateTime.UtcNow;
    private double _lastServicePosition;
    private bool _autoVideoSwitchInFlight;
    private string? _lastAutoVideoSwitchTrackId;

    // Track info (synced from IPlaybackStateService)
    [ObservableProperty]
    private string? _trackTitle;

    [ObservableProperty]
    private string? _artistName;

    [ObservableProperty]
    private string? _albumArt;

    [ObservableProperty]
    private string? _albumArtLarge;

    [ObservableProperty]
    private string? _albumArtColor;

    [ObservableProperty]
    private string? _currentArtistId;

    [ObservableProperty]
    private string? _currentAlbumId;

    [ObservableProperty]
    private IReadOnlyList<ArtistCredit>? _currentArtists;

    [ObservableProperty]
    private bool _hasTrack;

    [ObservableProperty]
    private bool _isAlbumArtExpanded;

    [RelayCommand]
    private void ToggleAlbumArtExpanded() => IsAlbumArtExpanded = !IsAlbumArtExpanded;

    /// <summary>
    /// MetadataItems for the artist credits — each artist is a separate clickable item.
    /// Falls back to a single item from ArtistName/CurrentArtistId when enriched data isn't available.
    /// Cached to avoid allocating a new array on every property access.
    /// </summary>
    private MetadataItem[]? _cachedArtistMetadata;
    private bool _artistMetadataDirty = true;

    public MetadataItem[]? ArtistMetadataItems
    {
        get
        {
            if (!_artistMetadataDirty) return _cachedArtistMetadata;
            _artistMetadataDirty = false;
            _cachedArtistMetadata = BuildArtistMetadata();
            return _cachedArtistMetadata;
        }
    }

    private MetadataItem[]? BuildArtistMetadata()
    {
        if (CurrentArtists is { Count: > 0 } artists)
        {
            return artists.Select(a => new MetadataItem
            {
                Label = a.Name,
                Command = _navigateToArtistCommand,
                CommandParameter = a.Uri
            }).ToArray();
        }

        if (!string.IsNullOrEmpty(ArtistName))
        {
            return [new MetadataItem
            {
                Label = ArtistName,
                Command = _navigateToArtistCommand,
                CommandParameter = CurrentArtistId
            }];
        }

        return null;
    }

    private readonly RelayCommand<string?> _navigateToArtistCommand;

    // Remote device indicator
    [ObservableProperty]
    private bool _isPlayingRemotely;

    [ObservableProperty]
    private string? _activeDeviceName;

    /// <summary>
    /// Name of the local audio output device (speakers / headphones / etc.) as
    /// reported by the audio host. Mirrors <see cref="IPlaybackStateService.ActiveAudioDeviceName"/>.
    /// Used when <see cref="IsPlayingRemotely"/> is false to surface where audio
    /// is going on this machine.
    /// </summary>
    [ObservableProperty]
    private string? _activeAudioDeviceName;

    [ObservableProperty]
    private bool _isVolumeRestricted;

    // Playback state (synced from IPlaybackStateService)
    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBuffering;

    /// <summary>
    /// Mirrors <see cref="IPlaybackStateService.IsAtEndOfContext"/>. Drives the
    /// inline "You've reached the end" hint in the PlayerBar — shows when
    /// playback is stopped after a context exhausted its auto-advance /
    /// autoplay tiers.
    /// </summary>
    [ObservableProperty]
    private bool _isAtEndOfContext;

    [ObservableProperty]
    private bool _isShuffle;

    [ObservableProperty]
    private RepeatMode _repeatMode = RepeatMode.Off;

    // Progress (in milliseconds)
    [ObservableProperty]
    private double _position;

    /// <summary>
    /// Shared coarse playback clock for Composition-driven progress bars.
    /// Service position events, seeks, track changes, and the 1 Hz UI
    /// interpolation timer all update this so newly-loaded player surfaces
    /// initialize from the same position shown by <see cref="PositionText"/>.
    /// </summary>
    [ObservableProperty]
    private double _anchorPositionMs;

    [ObservableProperty]
    private double _duration;

    /// <summary>
    /// Safe duration for slider Maximum (returns at least 1 to avoid 0 maximum).
    /// </summary>
    public double SliderMaximum => Math.Max(Duration, 1);

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _durationText = "0:00";

    [ObservableProperty]
    private bool _isSeeking;

    // Volume (0-100)
    [ObservableProperty]
    private double _volume = 100;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private double _previousVolume = 50;

    public PlayerBarViewModel(IPlaybackStateService playbackStateService,
                              IConnectivityService? connectivityService = null,
                              INotificationService? notificationService = null,
                              IPanelDockingService? dockingService = null,
                              ILoggerFactory? loggerFactory = null)
    {
        _playbackStateService = playbackStateService;
        _connectivityService = connectivityService;
        _notificationService = notificationService;
        _dockingService = dockingService;
        _logger = loggerFactory?.CreateLogger<PlayerBarViewModel>();

        _navigateToArtistCommand = new RelayCommand<string?>(NavigateToArtist);

        // Sync initial state
        SyncFromService();
        _logger?.LogDebug("PlayerBarViewModel init: track={Track}, playing={Playing}, pos={Pos}/{Dur}ms, vol={Vol}, shuffle={Shuffle}, repeat={Repeat}",
            _trackTitle ?? "<none>", _isPlaying, _position, _duration, _volume, _isShuffle, _repeatMode);

        // Subscribe to service changes
        _playbackStateService.PropertyChanged += OnPlaybackServicePropertyChanged;

        // Subscribe to connectivity changes to disable playback commands
        if (_connectivityService != null)
            _connectivityService.PropertyChanged += OnConnectivityPropertyChanged;

        // Shared position interpolation timer. Audio sink pushes ~2 real
        // updates/sec; this fills in the gaps so the time label and every
        // progress surface stay on the same coarse playback clock. 1000 ms
        // matches desktop players while keeping the global player chrome cheap.
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _positionTimer.Tick += OnPositionTimerTick;

        // Sync right panel button states from ShellViewModel
        WeakReferenceMessenger.Default.Register<RightPanelStateChangedMessage>(this, (r, m) =>
        {
            var vm = (PlayerBarViewModel)r;
            var (isOpen, mode) = m.Value;
            vm.IsQueuePanelActive = isOpen && mode == RightPanelMode.Queue;
            vm.IsLyricsPanelActive = isOpen && mode == RightPanelMode.Lyrics;
            vm.IsFriendsPanelActive = isOpen && mode == RightPanelMode.FriendsActivity;
            vm.IsDetailsPanelActive = isOpen && mode == RightPanelMode.Details;
        });
    }

    private bool CanExecutePlayback => _connectivityService?.IsConnected ?? true;

    private void OnConnectivityPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IConnectivityService.IsConnected))
        {
            PlayPauseCommand.NotifyCanExecuteChanged();
            PreviousCommand.NotifyCanExecuteChanged();
            NextCommand.NotifyCanExecuteChanged();
            SkipBackwardCommand.NotifyCanExecuteChanged();
            SkipForwardCommand.NotifyCanExecuteChanged();
            ToggleShuffleCommand.NotifyCanExecuteChanged();
            ToggleRepeatCommand.NotifyCanExecuteChanged();
        }
    }

    private void SyncFromService()
    {
        _trackTitle = _playbackStateService.CurrentTrackTitle;
        _artistName = _playbackStateService.CurrentArtistName;
        _albumArt = _playbackStateService.CurrentAlbumArt;
        _albumArtLarge = _playbackStateService.CurrentAlbumArtLarge;
        _albumArtColor = _playbackStateService.CurrentAlbumArtColor;
        _currentArtistId = _playbackStateService.CurrentArtistId;
        _currentAlbumId = _playbackStateService.CurrentAlbumId;
        _currentArtists = _playbackStateService.CurrentArtists;
        _hasTrack = !string.IsNullOrEmpty(_playbackStateService.CurrentTrackId);
        _isPlaying = _playbackStateService.IsPlaying;
        _isShuffle = _playbackStateService.IsShuffle;
        _repeatMode = _playbackStateService.RepeatMode;
        _duration = _playbackStateService.Duration;
        _position = ClampPlaybackPosition(_playbackStateService.Position);
        _anchorPositionMs = _position;
        _lastServicePosition = _position;
        _lastServicePositionUpdate = DateTime.UtcNow;
        _volume = _playbackStateService.Volume;
        _isVolumeRestricted = _playbackStateService.IsVolumeRestricted;
        _isAtEndOfContext = _playbackStateService.IsAtEndOfContext;
        _activeDeviceName = _playbackStateService.ActiveDeviceName;
        _activeAudioDeviceName = _playbackStateService.ActiveAudioDeviceName;
        _isPlayingRemotely = _playbackStateService.IsPlayingRemotely;
    }

    private void OnPlaybackServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPlaybackStateService.IsPlaying):
                var newPlaying = _playbackStateService.IsPlaying;
                _logger?.LogDebug("[PlayerBar] IsPlaying → {Value} (was {Old})", newPlaying, IsPlaying);
                IsPlaying = newPlaying;
                break;
            case nameof(IPlaybackStateService.IsBuffering):
                var newBuf = _playbackStateService.IsBuffering;
                _logger?.LogDebug("[PlayerBar] IsBuffering → {Value}", newBuf);
                IsBuffering = newBuf;
                break;
            case nameof(IPlaybackStateService.IsAtEndOfContext):
                IsAtEndOfContext = _playbackStateService.IsAtEndOfContext;
                break;
            case nameof(IPlaybackStateService.CurrentTrackId):
                var newTrackId = _playbackStateService.CurrentTrackId;
                var hasTrack = !string.IsNullOrEmpty(newTrackId);
                _logger?.LogDebug("[PlayerBar] CurrentTrackId → {TrackId} (hasTrack={HasTrack})", newTrackId ?? "<none>", hasTrack);
                HasTrack = hasTrack;
                TryAutoSwitchToVideo("track-changed");
                break;
            case nameof(IPlaybackStateService.CurrentTrackTitle):
                TrackTitle = _playbackStateService.CurrentTrackTitle;
                _logger?.LogDebug("[PlayerBar] TrackTitle → {Title}", TrackTitle ?? "<none>");
                break;
            case nameof(IPlaybackStateService.CurrentArtistName):
                ArtistName = _playbackStateService.CurrentArtistName;
                _artistMetadataDirty = true;
                OnPropertyChanged(nameof(ArtistMetadataItems));
                break;
            case nameof(IPlaybackStateService.CurrentArtists):
                CurrentArtists = _playbackStateService.CurrentArtists;
                _artistMetadataDirty = true;
                OnPropertyChanged(nameof(ArtistMetadataItems));
                break;
            case nameof(IPlaybackStateService.CurrentAlbumArt):
                AlbumArt = _playbackStateService.CurrentAlbumArt;
                break;
            case nameof(IPlaybackStateService.CurrentAlbumArtLarge):
                AlbumArtLarge = _playbackStateService.CurrentAlbumArtLarge;
                break;
            case nameof(IPlaybackStateService.CurrentArtistId):
                CurrentArtistId = _playbackStateService.CurrentArtistId;
                _artistMetadataDirty = true;
                OnPropertyChanged(nameof(ArtistMetadataItems));
                break;
            case nameof(IPlaybackStateService.CurrentAlbumId):
                CurrentAlbumId = _playbackStateService.CurrentAlbumId;
                break;
            case nameof(IPlaybackStateService.CurrentTrackManifestId):
                OnPropertyChanged(nameof(IsCurrentTrackVideoCapable));
                OnPropertyChanged(nameof(IsCurrentTrackAudioCapable));
                break;
            case nameof(IPlaybackStateService.CurrentTrackHasMusicVideo):
            case nameof(IPlaybackStateService.CurrentTrackIsVideo):
                OnPropertyChanged(nameof(IsCurrentTrackVideoCapable));
                OnPropertyChanged(nameof(IsCurrentTrackAudioCapable));
                TryAutoSwitchToVideo(e.PropertyName);
                break;
            case nameof(IPlaybackStateService.CurrentAlbumArtColor):
                AlbumArtColor = _playbackStateService.CurrentAlbumArtColor;
                break;
            case nameof(IPlaybackStateService.Position):
                if (!IsSeeking)
                {
                    ApplyPlaybackPosition(_playbackStateService.Position, updateProgressBar: true, resetInterpolationClock: true);
                }
                else
                {
                    _logger?.LogTrace("[PlayerBar] Position update suppressed — user is seeking");
                }
                break;
            case nameof(IPlaybackStateService.Duration):
                var newDur = _playbackStateService.Duration;
                _logger?.LogDebug("[PlayerBar] Duration → {Dur}ms", newDur);
                Duration = newDur;
                break;
            case nameof(IPlaybackStateService.Volume):
                Volume = _playbackStateService.Volume;
                break;
            case nameof(IPlaybackStateService.IsShuffle):
                var newShuffle = _playbackStateService.IsShuffle;
                _logger?.LogDebug("[PlayerBar] IsShuffle → {Value}", newShuffle);
                IsShuffle = newShuffle;
                break;
            case nameof(IPlaybackStateService.RepeatMode):
                var newRepeat = _playbackStateService.RepeatMode;
                _logger?.LogDebug("[PlayerBar] RepeatMode → {Value}", newRepeat);
                RepeatMode = newRepeat;
                break;
            case nameof(IPlaybackStateService.IsPlayingRemotely):
                var newRemote = _playbackStateService.IsPlayingRemotely;
                _logger?.LogDebug("[PlayerBar] IsPlayingRemotely → {Value}, device={Device}", newRemote, _playbackStateService.ActiveDeviceName ?? "<none>");
                IsPlayingRemotely = newRemote;
                OnPropertyChanged(nameof(IsCurrentTrackVideoCapable));
                OnPropertyChanged(nameof(IsCurrentTrackAudioCapable));
                break;
            case nameof(IPlaybackStateService.ActiveDeviceName):
                ActiveDeviceName = _playbackStateService.ActiveDeviceName;
                break;
            case nameof(IPlaybackStateService.ActiveAudioDeviceName):
                ActiveAudioDeviceName = _playbackStateService.ActiveAudioDeviceName;
                break;
            case nameof(IPlaybackStateService.IsVolumeRestricted):
                IsVolumeRestricted = _playbackStateService.IsVolumeRestricted;
                break;
        }
    }

    private void OnPositionTimerTick(object? sender, object e)
    {
        if (!IsSeeking && IsPlaying)
        {
            var interpolated = GetInterpolatedPlaybackPosition();
            Position = interpolated;
            AnchorPositionMs = interpolated;
        }
    }

    private double GetInterpolatedPlaybackPosition()
    {
        var elapsed = Math.Max(0, (DateTime.UtcNow - _lastServicePositionUpdate).TotalMilliseconds);
        return ClampPlaybackPosition(_lastServicePosition + elapsed);
    }

    private void ApplyPlaybackPosition(double positionMs, bool updateProgressBar, bool resetInterpolationClock)
    {
        var clamped = ClampPlaybackPosition(positionMs);
        Position = clamped;

        if (updateProgressBar)
            AnchorPositionMs = clamped;

        if (resetInterpolationClock)
            ResetInterpolationClock(clamped);
    }

    private void ResetInterpolationClock(double positionMs)
    {
        _lastServicePosition = ClampPlaybackPosition(positionMs);
        _lastServicePositionUpdate = DateTime.UtcNow;
    }

    private double ClampPlaybackPosition(double positionMs)
    {
        if (double.IsNaN(positionMs) || double.IsInfinity(positionMs))
            return 0;

        var clamped = Math.Max(0, positionMs);
        if (Duration > 0)
            clamped = Math.Min(clamped, Duration);

        return clamped;
    }

    private int _lastFormattedPositionSec = -1;
    private int _lastFormattedDurationSec = -1;

    partial void OnPositionChanged(double value)
    {
        // Position fires 1×/sec from the timer plus service updates. Skip
        // the string format and Text-binding update unless the displayed second
        // actually changed.
        var sec = (int)(value / 1000d);
        if (sec == _lastFormattedPositionSec) return;
        _lastFormattedPositionSec = sec;
        PositionText = FormatTime(value);
    }

    partial void OnDurationChanged(double value)
    {
        var sec = (int)(value / 1000d);
        if (sec != _lastFormattedDurationSec)
        {
            _lastFormattedDurationSec = sec;
            DurationText = FormatTime(value);
        }
        OnPropertyChanged(nameof(SliderMaximum));

        if (value > 0 && Position > value)
            ApplyPlaybackPosition(value, updateProgressBar: true, resetInterpolationClock: true);
    }

    partial void OnIsPlayingChanged(bool value)
    {
        if (!IsSeeking)
        {
            var currentPosition = ClampPlaybackPosition(Position);
            Position = currentPosition;
            AnchorPositionMs = currentPosition;
            ResetInterpolationClock(currentPosition);
        }

        UpdatePositionTimerState();
    }

    // ── Visibility gate for the position interpolation timer ────────────
    //
    // Each surface that renders the player (PlayerBar at the bottom,
    // SidebarPlayerWidget at the top of the sidebar, the floating Player
    // window, and the expanded floating layout) calls SetSurfaceVisible
    // from its Loaded / Unloaded handlers. The timer only ticks while at
    // least one surface is on screen AND a track is playing.
    //
    // Counter-based (not bool) so multiple live instances of the same
    // surface key — e.g. a SidebarPlayerWidget docked in the sidebar AND
    // another inside the expanded floating-player layout — don't fight each
    // other when one unloads while the other is still visible.
    private int _barVisibleCount = 1; // PlayerBar is always present in the shell at startup.
    private int _widgetVisibleCount;

    /// <summary>
    /// Records visibility for a named surface. Each Loaded should pair with
    /// an Unloaded (true / false). Surface ids: "bar" for the bottom-docked
    /// PlayerBar, "widget" for any <see cref="Controls.SidebarPlayer.SidebarPlayerWidget"/>
    /// instance (sidebar, floating window, expanded layout).
    /// </summary>
    public void SetSurfaceVisible(string surfaceId, bool visible)
    {
        switch (surfaceId)
        {
            case "bar":
                _barVisibleCount = Math.Max(0, _barVisibleCount + (visible ? 1 : -1));
                break;
            case "widget":
                _widgetVisibleCount = Math.Max(0, _widgetVisibleCount + (visible ? 1 : -1));
                break;
            default:
                return;
        }
        UpdatePositionTimerState();
    }

    private void UpdatePositionTimerState()
    {
        if (_positionTimer == null) return;
        var shouldRun = IsPlaying && (_barVisibleCount > 0 || _widgetVisibleCount > 0);
        if (shouldRun)
        {
            _positionTimer.Start();
        }
        else
        {
            _positionTimer.Stop();
        }
    }

    public string VolumeText => $"{(int)Math.Round(Volume)}";

    partial void OnVolumeChanged(double value)
    {
        if (!IsMuted && value > 0)
        {
            PreviousVolume = value;
        }

        OnPropertyChanged(nameof(VolumeText));
        _playbackStateService.Volume = value;
    }

    private static string FormatTime(double milliseconds)
    {
        var timeSpan = TimeSpan.FromMilliseconds(milliseconds);

        if (timeSpan.TotalHours >= 1)
            return timeSpan.ToString(@"h\:mm\:ss");

        return timeSpan.ToString(@"m\:ss");
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayback))]
    private void PlayPause()
    {
        if (!HasTrack)
        {
            _logger?.LogWarning("[PlayerBar] PlayPause ignored — no track loaded");
            return;
        }

        _logger?.LogInformation("[PlayerBar] PlayPause clicked: isPlaying={IsPlaying}, track={Track}, pos={Pos}ms",
            IsPlaying, TrackTitle ?? "<none>", (long)Position);
        _playbackStateService.PlayPause();
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayback))]
    private void Previous()
    {
        _logger?.LogInformation("[PlayerBar] Previous clicked: pos={Pos}ms, track={Track}", (long)Position, TrackTitle ?? "<none>");
        _playbackStateService.Previous();
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayback))]
    private void Next()
    {
        _logger?.LogInformation("[PlayerBar] Next clicked: track={Track}", TrackTitle ?? "<none>");
        _playbackStateService.Next();
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayback))]
    private void SkipBackward()
    {
        // Skip back 10 seconds
        var newPos = Math.Max(0, Position - 10000);
        _logger?.LogInformation("[PlayerBar] SkipBackward clicked: {From}ms → {To}ms", (long)Position, (long)newPos);
        ApplyPlaybackPosition(newPos, updateProgressBar: true, resetInterpolationClock: true);
        _playbackStateService.Seek(newPos);
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayback))]
    private void SkipForward()
    {
        // Skip forward 30 seconds
        var newPos = Math.Min(Duration, Position + 30000);
        _logger?.LogInformation("[PlayerBar] SkipForward clicked: {From}ms → {To}ms", (long)Position, (long)newPos);
        ApplyPlaybackPosition(newPos, updateProgressBar: true, resetInterpolationClock: true);
        _playbackStateService.Seek(newPos);
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayback))]
    private void ToggleShuffle()
    {
        var next = !IsShuffle;
        _logger?.LogInformation("[PlayerBar] ToggleShuffle: {From} → {To}", IsShuffle, next);
        _playbackStateService.SetShuffle(next);
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayback))]
    private void ToggleRepeat()
    {
        // Cycle: Off -> Context (repeat all) -> Track (repeat one) -> Off
        var next = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.Context,
            RepeatMode.Context => RepeatMode.Track,
            RepeatMode.Track => RepeatMode.Off,
            _ => RepeatMode.Off
        };
        _logger?.LogInformation("[PlayerBar] ToggleRepeat: {From} → {To}", RepeatMode, next);
        _playbackStateService.SetRepeatMode(next);
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted)
        {
            // Unmute - restore previous volume
            Volume = PreviousVolume;
            IsMuted = false;
        }
        else
        {
            // Mute - save current volume and set to 0
            PreviousVolume = Volume;
            Volume = 0;
            IsMuted = true;
        }
    }

    // Right panel toggle state (synced from ShellViewModel via messenger)
    [ObservableProperty]
    private bool _isQueuePanelActive;

    [ObservableProperty]
    private bool _isLyricsPanelActive;

    [ObservableProperty]
    private bool _isFriendsPanelActive;

    [ObservableProperty]
    private bool _isDetailsPanelActive;

    [RelayCommand]
    private void ToggleQueuePanel()
    {
        WeakReferenceMessenger.Default.Send(new ToggleRightPanelMessage(RightPanelMode.Queue));
    }

    [RelayCommand]
    private void ToggleLyricsPanel()
    {
        WeakReferenceMessenger.Default.Send(new ToggleRightPanelMessage(RightPanelMode.Lyrics));
    }

    [RelayCommand]
    private void ToggleFriendsPanel()
    {
        WeakReferenceMessenger.Default.Send(new ToggleRightPanelMessage(RightPanelMode.FriendsActivity));
    }

    [RelayCommand]
    private void ToggleDetailsPanel()
    {
        WeakReferenceMessenger.Default.Send(new ToggleRightPanelMessage(RightPanelMode.Details));
    }

    /// <summary>
    /// True when the current track has a music-video variant — drives the
    /// "Watch Video" button visibility. True when EITHER the manifest_id is
    /// already known (self-contained tracks where Track.original_video is on
    /// the audio URI itself) OR
    /// <see cref="IPlaybackStateService.CurrentTrackHasMusicVideo"/> says the
    /// linked-URI discovery service surfaced an associated video. Visible
    /// regardless of which device is currently playing — Spotify's behaviour
    /// is to transfer playback to the device that initiated the video on
    /// click, so a remote audio session switching to local video on click is
    /// expected and matches the desktop client.
    /// </summary>
    public bool IsCurrentTrackVideoCapable =>
        !_playbackStateService.IsPlayingRemotely
        && !_playbackStateService.CurrentTrackIsVideo
        && (!string.IsNullOrEmpty(_playbackStateService.CurrentTrackManifestId)
            || _playbackStateService.CurrentTrackHasMusicVideo);

    public bool IsCurrentTrackAudioCapable =>
        !_playbackStateService.IsPlayingRemotely
        && _playbackStateService.CurrentTrackIsVideo;

    [ObservableProperty]
    private bool _preferVideoPlaybackInSession;

    partial void OnPreferVideoPlaybackInSessionChanged(bool value)
    {
        if (!value)
        {
            _lastAutoVideoSwitchTrackId = null;
            return;
        }

        if (value)
            TryAutoSwitchToVideo("preference-enabled");
    }

    [RelayCommand]
    private void ToggleVideoPlaybackPreference()
    {
        PreferVideoPlaybackInSession = !PreferVideoPlaybackInSession;
    }

    [ObservableProperty]
    private bool _isResolvingVideo;

    partial void OnIsResolvingVideoChanged(bool value) => SwitchToVideoCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    private bool _isSwitchingToAudio;

    partial void OnIsSwitchingToAudioChanged(bool value) => SwitchToAudioCommand.NotifyCanExecuteChanged();

    private bool CanSwitchToVideo() => !IsResolvingVideo;
    private bool CanSwitchToAudio() => !IsSwitchingToAudio;

    private void TryAutoSwitchToVideo(string? reason)
    {
        if (!PreferVideoPlaybackInSession)
            return;
        if (_autoVideoSwitchInFlight || IsResolvingVideo || IsSwitchingToAudio)
            return;
        if (!IsCurrentTrackVideoCapable)
            return;

        var trackId = _playbackStateService.CurrentTrackId;
        if (string.IsNullOrEmpty(trackId))
            return;
        if (string.Equals(_lastAutoVideoSwitchTrackId, trackId, StringComparison.Ordinal))
            return;

        _lastAutoVideoSwitchTrackId = trackId;
        _ = AutoSwitchToVideoAsync(trackId, reason);
    }

    private async Task AutoSwitchToVideoAsync(string trackId, string? reason)
    {
        _autoVideoSwitchInFlight = true;
        try
        {
            if (!string.Equals(_playbackStateService.CurrentTrackId, trackId, StringComparison.Ordinal))
                return;

            _logger?.LogInformation("[AutoVideo] Switching to video for {Track} ({Reason})",
                trackId,
                reason ?? "unknown");

            var switched = await _playbackStateService.SwitchToVideoAsync();
            if (!switched)
                _logger?.LogInformation("[AutoVideo] Switch failed for {Track}", trackId);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[AutoVideo] Switch threw for {Track}", trackId);
        }
        finally
        {
            _autoVideoSwitchInFlight = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitchToVideo))]
    private async Task SwitchToVideoAsync()
    {
        IsResolvingVideo = true;
        try
        {
            var routeToPlayerPopout = _dockingService?.IsPlayerDetached == true;
            var switched = await _playbackStateService.SwitchToVideoAsync();
            if (switched)
            {
                PreferVideoPlaybackInSession = true;
                if (!routeToPlayerPopout)
                    NavigationHelpers.OpenVideoPlayer();
                return;
            }

            _notificationService?.Show(new NotificationInfo
            {
                Message = "Music video isn't available for this track",
                Severity = NotificationSeverity.Informational,
                AutoDismissAfter = TimeSpan.FromSeconds(4)
            });
        }
        finally
        {
            IsResolvingVideo = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitchToAudio))]
    private async Task SwitchToAudioAsync()
    {
        IsSwitchingToAudio = true;
        try
        {
            var switched = await _playbackStateService.SwitchToAudioAsync();
            if (switched)
            {
                PreferVideoPlaybackInSession = false;
                return;
            }

            _notificationService?.Show(new NotificationInfo
            {
                Message = "Can't switch this video back to audio",
                Severity = NotificationSeverity.Informational,
                AutoDismissAfter = TimeSpan.FromSeconds(4)
            });
        }
        finally
        {
            IsSwitchingToAudio = false;
        }
    }

    /// <summary>
    /// Called when user starts dragging the progress slider.
    /// </summary>
    public void StartSeeking()
    {
        IsSeeking = true;
    }

    /// <summary>
    /// Called when user finishes dragging the progress slider.
    /// </summary>
    public void EndSeeking()
    {
        _logger?.LogInformation("[PlayerBar] Seek committed: {Pos}ms / {Dur}ms", (long)Position, (long)Duration);
        IsSeeking = false;
        var seekPosition = ClampPlaybackPosition(Position);
        ApplyPlaybackPosition(seekPosition, updateProgressBar: true, resetInterpolationClock: true);
        _playbackStateService.Seek(seekPosition);
    }

    /// <summary>
    /// Composition progress bar handed us a new seek target. Sets Position so
    /// the existing Seek pipeline (and the textual position) follow the user's
    /// drag, then re-anchors and dispatches Seek via <see cref="EndSeeking"/>.
    /// </summary>
    public void CommitSeekFromBar(double positionMs)
    {
        Position = ClampPlaybackPosition(positionMs);
        EndSeeking();
    }

    /// <summary>
    /// Sets the current track info. Called from playback service.
    /// </summary>
    public void SetTrack(string? title, string? artist, string? albumArt, double durationMs)
    {
        TrackTitle = title;
        ArtistName = artist;
        AlbumArt = albumArt;
        Duration = durationMs;
        ApplyPlaybackPosition(0, updateProgressBar: true, resetInterpolationClock: true);
        HasTrack = !string.IsNullOrEmpty(title);
    }

    /// <summary>
    /// Clears the current track. Called when playback stops.
    /// </summary>
    public void ClearTrack()
    {
        TrackTitle = null;
        ArtistName = null;
        AlbumArt = null;
        Duration = 0;
        ApplyPlaybackPosition(0, updateProgressBar: true, resetInterpolationClock: true);
        HasTrack = false;
        IsPlaying = false;
    }

    /// <summary>
    /// Updates the current playback position. Called from playback service.
    /// </summary>
    public void UpdatePosition(double positionMs)
    {
        if (!IsSeeking)
        {
            ApplyPlaybackPosition(positionMs, updateProgressBar: true, resetInterpolationClock: true);
        }
    }

    private void NavigateToArtist(string? artistUri)
    {
        if (string.IsNullOrEmpty(artistUri)) return;
        NavigationHelpers.OpenArtist(artistUri, "Artist");
    }

    /// <summary>
    /// Demo method to set sample track for testing.
    /// </summary>
    public void SetDemoTrack()
    {
        SetTrack(
            "Sample Track",
            "Sample Artist",
            null,
            180000 // 3 minutes
        );
    }

    /// <summary>
    /// Disposes resources including the position timer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playbackStateService.PropertyChanged -= OnPlaybackServicePropertyChanged;
        if (_connectivityService != null)
            _connectivityService.PropertyChanged -= OnConnectivityPropertyChanged;
        WeakReferenceMessenger.Default.Unregister<RightPanelStateChangedMessage>(this);

        if (_positionTimer != null)
        {
            _positionTimer.Stop();
            _positionTimer.Tick -= OnPositionTimerTick;
            _positionTimer = null;
        }
    }
}
