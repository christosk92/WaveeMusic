using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
    private readonly ILogger? _logger;
    private bool _disposed;
    private DispatcherTimer? _positionTimer;
    private DateTime _lastServicePositionUpdate = DateTime.UtcNow;
    private double _lastServicePosition;

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

    [ObservableProperty]
    private bool _isVolumeRestricted;

    // Playback state (synced from IPlaybackStateService)
    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBuffering;

    [ObservableProperty]
    private bool _isShuffle;

    [ObservableProperty]
    private RepeatMode _repeatMode = RepeatMode.Off;

    // Progress (in milliseconds)
    [ObservableProperty]
    private double _position;

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
                              ILoggerFactory? loggerFactory = null)
    {
        _playbackStateService = playbackStateService;
        _connectivityService = connectivityService;
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

        // Initialize position update timer (display concern)
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
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
        _position = _playbackStateService.Position;
        _duration = _playbackStateService.Duration;
        _volume = _playbackStateService.Volume;
        _isVolumeRestricted = _playbackStateService.IsVolumeRestricted;
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
            case nameof(IPlaybackStateService.CurrentTrackId):
                var newTrackId = _playbackStateService.CurrentTrackId;
                var hasTrack = !string.IsNullOrEmpty(newTrackId);
                _logger?.LogDebug("[PlayerBar] CurrentTrackId → {TrackId} (hasTrack={HasTrack})", newTrackId ?? "<none>", hasTrack);
                HasTrack = hasTrack;
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
            case nameof(IPlaybackStateService.CurrentAlbumArtColor):
                AlbumArtColor = _playbackStateService.CurrentAlbumArtColor;
                break;
            case nameof(IPlaybackStateService.Position):
                if (!IsSeeking)
                {
                    Position = _playbackStateService.Position;
                    _lastServicePosition = Position;
                    _lastServicePositionUpdate = DateTime.UtcNow;
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
                break;
            case nameof(IPlaybackStateService.ActiveDeviceName):
                ActiveDeviceName = _playbackStateService.ActiveDeviceName;
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
            // Interpolate position from the last known service update.
            // The service pushes real position updates every ~500ms from the audio sink.
            // Between updates, we interpolate based on elapsed wall-clock time for smooth UI.
            var elapsed = (DateTime.UtcNow - _lastServicePositionUpdate).TotalMilliseconds;
            var interpolated = _lastServicePosition + elapsed;
            if (Duration > 0 && interpolated > Duration)
            {
                interpolated = Duration;
            }
            Position = interpolated;
        }
    }

    private int _lastFormattedPositionSec = -1;
    private int _lastFormattedDurationSec = -1;

    partial void OnPositionChanged(double value)
    {
        // Position fires 4×/sec from the timer plus ~2×/sec from the service. Skip
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
    }

    partial void OnIsPlayingChanged(bool value)
    {
        if (value)
        {
            _logger?.LogDebug("[PlayerBar] IsPlaying=true — starting position interpolation timer");
            _positionTimer?.Start();
        }
        else
        {
            _logger?.LogDebug("[PlayerBar] IsPlaying=false — stopping position interpolation timer");
            _positionTimer?.Stop();
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
        _playbackStateService.Seek(newPos);
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayback))]
    private void SkipForward()
    {
        // Skip forward 30 seconds
        var newPos = Math.Min(Duration, Position + 30000);
        _logger?.LogInformation("[PlayerBar] SkipForward clicked: {From}ms → {To}ms", (long)Position, (long)newPos);
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
        _playbackStateService.Seek(Position);
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
        Position = 0;
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
        Position = 0;
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
            Position = positionMs;
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
