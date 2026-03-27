using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for the player bar control. Delegates playback state and commands
/// to <see cref="IPlaybackStateService"/> while keeping display-only concerns
/// (formatting, seeking UI, mute toggle) local.
/// </summary>
public sealed partial class PlayerBarViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackStateService _playbackStateService;
    private bool _disposed;
    private DispatcherTimer? _positionTimer;

    // Track info (synced from IPlaybackStateService)
    [ObservableProperty]
    private string? _trackTitle;

    [ObservableProperty]
    private string? _artistName;

    [ObservableProperty]
    private string? _albumArt;

    [ObservableProperty]
    private string? _albumArtColor;

    [ObservableProperty]
    private string? _currentArtistId;

    [ObservableProperty]
    private string? _currentAlbumId;

    [ObservableProperty]
    private bool _hasTrack;

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
    private double _volume = 50;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private double _previousVolume = 50;

    public PlayerBarViewModel(IPlaybackStateService playbackStateService)
    {
        _playbackStateService = playbackStateService;

        // Sync initial state
        SyncFromService();

        // Subscribe to service changes
        _playbackStateService.PropertyChanged += OnPlaybackServicePropertyChanged;

        // Initialize position update timer (display concern)
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _positionTimer.Tick += OnPositionTimerTick;
    }

    private void SyncFromService()
    {
        _trackTitle = _playbackStateService.CurrentTrackTitle;
        _artistName = _playbackStateService.CurrentArtistName;
        _albumArt = _playbackStateService.CurrentAlbumArt;
        _albumArtColor = _playbackStateService.CurrentAlbumArtColor;
        _currentArtistId = _playbackStateService.CurrentArtistId;
        _currentAlbumId = _playbackStateService.CurrentAlbumId;
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
                IsPlaying = _playbackStateService.IsPlaying;
                break;
            case nameof(IPlaybackStateService.IsBuffering):
                IsBuffering = _playbackStateService.IsBuffering;
                break;
            case nameof(IPlaybackStateService.CurrentTrackId):
                HasTrack = !string.IsNullOrEmpty(_playbackStateService.CurrentTrackId);
                break;
            case nameof(IPlaybackStateService.CurrentTrackTitle):
                TrackTitle = _playbackStateService.CurrentTrackTitle;
                break;
            case nameof(IPlaybackStateService.CurrentArtistName):
                ArtistName = _playbackStateService.CurrentArtistName;
                break;
            case nameof(IPlaybackStateService.CurrentAlbumArt):
                AlbumArt = _playbackStateService.CurrentAlbumArt;
                break;
            case nameof(IPlaybackStateService.CurrentArtistId):
                CurrentArtistId = _playbackStateService.CurrentArtistId;
                break;
            case nameof(IPlaybackStateService.CurrentAlbumId):
                CurrentAlbumId = _playbackStateService.CurrentAlbumId;
                break;
            case nameof(IPlaybackStateService.CurrentAlbumArtColor):
                AlbumArtColor = _playbackStateService.CurrentAlbumArtColor;
                break;
            case nameof(IPlaybackStateService.Position):
                if (!IsSeeking) Position = _playbackStateService.Position;
                break;
            case nameof(IPlaybackStateService.Duration):
                Duration = _playbackStateService.Duration;
                break;
            case nameof(IPlaybackStateService.Volume):
                Volume = _playbackStateService.Volume;
                break;
            case nameof(IPlaybackStateService.IsShuffle):
                IsShuffle = _playbackStateService.IsShuffle;
                break;
            case nameof(IPlaybackStateService.RepeatMode):
                RepeatMode = _playbackStateService.RepeatMode;
                break;
            case nameof(IPlaybackStateService.IsPlayingRemotely):
                IsPlayingRemotely = _playbackStateService.IsPlayingRemotely;
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
            // TODO: Get actual position from AudioPipeline
            // For now, simulate progress
            Position += 250;
            if (Position >= Duration && Duration > 0)
            {
                Position = 0;
                IsPlaying = false;
            }
        }
    }

    partial void OnPositionChanged(double value)
    {
        PositionText = FormatTime(value);
    }

    partial void OnDurationChanged(double value)
    {
        DurationText = FormatTime(value);
        OnPropertyChanged(nameof(SliderMaximum));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        if (value)
        {
            _positionTimer?.Start();
        }
        else
        {
            _positionTimer?.Stop();
        }
    }

    partial void OnVolumeChanged(double value)
    {
        if (!IsMuted && value > 0)
        {
            PreviousVolume = value;
        }

        _playbackStateService.Volume = value;
    }

    private static string FormatTime(double milliseconds)
    {
        var timeSpan = TimeSpan.FromMilliseconds(milliseconds);

        if (timeSpan.TotalHours >= 1)
            return timeSpan.ToString(@"h\:mm\:ss");

        return timeSpan.ToString(@"m\:ss");
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!HasTrack)
        {
            return;
        }

        _playbackStateService.PlayPause();
    }

    [RelayCommand]
    private void Previous()
    {
        _playbackStateService.Previous();
    }

    [RelayCommand]
    private void Next()
    {
        _playbackStateService.Next();
    }

    [RelayCommand]
    private void SkipBackward()
    {
        // Skip back 10 seconds
        var newPos = Math.Max(0, Position - 10000);
        _playbackStateService.Seek(newPos);
    }

    [RelayCommand]
    private void SkipForward()
    {
        // Skip forward 30 seconds
        var newPos = Math.Min(Duration, Position + 30000);
        _playbackStateService.Seek(newPos);
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        _playbackStateService.SetShuffle(!IsShuffle);
    }

    [RelayCommand]
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

    [RelayCommand]
    private void OpenQueue()
    {
        // TODO: Open queue panel/flyout
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

        if (_positionTimer != null)
        {
            _positionTimer.Stop();
            _positionTimer.Tick -= OnPositionTimerTick;
            _positionTimer = null;
        }
    }
}
