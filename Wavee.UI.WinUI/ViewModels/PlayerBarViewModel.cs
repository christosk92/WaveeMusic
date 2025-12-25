using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Converters;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for the player bar control. Manages playback state, track info,
/// and volume controls. Will be connected to Wavee's AudioPipeline backend.
/// </summary>
public sealed partial class PlayerBarViewModel : ObservableObject
{
    private DispatcherTimer? _positionTimer;

    // Track info
    [ObservableProperty]
    private string? _trackTitle;

    [ObservableProperty]
    private string? _artistName;

    [ObservableProperty]
    private string? _albumArt;

    [ObservableProperty]
    private bool _hasTrack;

    // Playback state
    [ObservableProperty]
    private bool _isPlaying;

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

    public PlayerBarViewModel()
    {
        // Initialize position update timer
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _positionTimer.Tick += OnPositionTimerTick;
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

        // TODO: Update AudioPipeline volume
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
            // TODO: Start playing from queue if available
            return;
        }

        IsPlaying = !IsPlaying;
        // TODO: Send command to AudioPipeline
    }

    [RelayCommand]
    private void Previous()
    {
        // TODO: Go to previous track in queue
        Position = 0;
    }

    [RelayCommand]
    private void Next()
    {
        // TODO: Go to next track in queue
        Position = 0;
        IsPlaying = false;
    }

    [RelayCommand]
    private void SkipBackward()
    {
        // Skip back 10 seconds
        Position = Math.Max(0, Position - 10000);
        // TODO: Seek in AudioPipeline
    }

    [RelayCommand]
    private void SkipForward()
    {
        // Skip forward 30 seconds
        Position = Math.Min(Duration, Position + 30000);
        // TODO: Seek in AudioPipeline
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        IsShuffle = !IsShuffle;
        // TODO: Update playback queue shuffle state
    }

    [RelayCommand]
    private void ToggleRepeat()
    {
        // Cycle: Off -> Context (repeat all) -> Track (repeat one) -> Off
        RepeatMode = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.Context,
            RepeatMode.Context => RepeatMode.Track,
            RepeatMode.Track => RepeatMode.Off,
            _ => RepeatMode.Off
        };
        // TODO: Update playback queue repeat state
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
        // TODO: Seek to position in AudioPipeline
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
}
