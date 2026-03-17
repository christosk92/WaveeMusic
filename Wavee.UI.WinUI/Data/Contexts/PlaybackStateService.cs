using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Centralized playback state service. Wraps <see cref="IPlayerContext"/> and adds
/// playback context, queue, and command methods. Sends messenger events on key state changes.
/// </summary>
internal sealed partial class PlaybackStateService : ObservableObject, IPlaybackStateService, IDisposable
{
    private readonly IPlayerContext _playerContext;
    private readonly IMessenger _messenger;
    private readonly ObservableCollection<QueueItem> _queue = [];

    // --- Mirrored from IPlayerContext ---

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string? _currentTrackId;

    [ObservableProperty]
    private string? _currentTrackTitle;

    [ObservableProperty]
    private string? _currentArtistName;

    [ObservableProperty]
    private string? _currentAlbumArt;

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    private double _duration;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private bool _isShuffle;

    [ObservableProperty]
    private RepeatMode _repeatMode = RepeatMode.Off;

    // --- New: context & queue ---

    [ObservableProperty]
    private PlaybackContextInfo? _currentContext;

    [ObservableProperty]
    private int _queuePosition;

    public IReadOnlyList<QueueItem> Queue => _queue;

    public PlaybackStateService(IPlayerContext playerContext, IMessenger messenger)
    {
        _playerContext = playerContext;
        _messenger = messenger;

        // Sync initial state from IPlayerContext
        SyncFromPlayerContext();

        // Subscribe to IPlayerContext changes
        _playerContext.PropertyChanged += OnPlayerContextPropertyChanged;
    }

    private void SyncFromPlayerContext()
    {
        _isPlaying = _playerContext.IsPlaying;
        _currentTrackId = _playerContext.CurrentTrackId;
        _currentTrackTitle = _playerContext.CurrentTrackTitle;
        _currentArtistName = _playerContext.CurrentArtistName;
        _currentAlbumArt = _playerContext.CurrentAlbumArt;
        _position = _playerContext.Position;
        _duration = _playerContext.Duration;
        _volume = _playerContext.Volume;
        _isShuffle = _playerContext.IsShuffle;
        // Map IPlayerContext.IsRepeat (bool) to RepeatMode
        _repeatMode = _playerContext.IsRepeat ? RepeatMode.Context : RepeatMode.Off;
    }

    private void OnPlayerContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPlayerContext.IsPlaying):
                IsPlaying = _playerContext.IsPlaying;
                break;
            case nameof(IPlayerContext.CurrentTrackId):
                CurrentTrackId = _playerContext.CurrentTrackId;
                break;
            case nameof(IPlayerContext.CurrentTrackTitle):
                CurrentTrackTitle = _playerContext.CurrentTrackTitle;
                break;
            case nameof(IPlayerContext.CurrentArtistName):
                CurrentArtistName = _playerContext.CurrentArtistName;
                break;
            case nameof(IPlayerContext.CurrentAlbumArt):
                CurrentAlbumArt = _playerContext.CurrentAlbumArt;
                break;
            case nameof(IPlayerContext.Position):
                Position = _playerContext.Position;
                break;
            case nameof(IPlayerContext.Duration):
                Duration = _playerContext.Duration;
                break;
            case nameof(IPlayerContext.Volume):
                Volume = _playerContext.Volume;
                break;
            case nameof(IPlayerContext.IsShuffle):
                IsShuffle = _playerContext.IsShuffle;
                break;
            case nameof(IPlayerContext.IsRepeat):
                RepeatMode = _playerContext.IsRepeat ? RepeatMode.Context : RepeatMode.Off;
                break;
        }
    }

    // --- Messenger broadcasts on key changes ---

    partial void OnIsPlayingChanged(bool value)
    {
        _messenger.Send(new PlaybackStateChangedMessage(value));
    }

    partial void OnCurrentTrackIdChanged(string? value)
    {
        _messenger.Send(new TrackChangedMessage(value));
    }

    partial void OnCurrentContextChanged(PlaybackContextInfo? value)
    {
        _messenger.Send(new PlaybackContextChangedMessage(value));
    }

    // --- Commands ---

    public void PlayPause()
    {
        // TODO: Forward to audio backend
        IsPlaying = !IsPlaying;
    }

    public void Next()
    {
        if (_queue.Count > 0 && QueuePosition < _queue.Count - 1)
        {
            QueuePosition++;
            LoadTrackFromQueue(QueuePosition);
        }
        else
        {
            Position = 0;
        }
    }

    public void Previous()
    {
        // If more than 3 seconds into the track, restart it
        if (Position > 3000)
        {
            Position = 0;
            return;
        }

        if (_queue.Count > 0 && QueuePosition > 0)
        {
            QueuePosition--;
            LoadTrackFromQueue(QueuePosition);
        }
        else
        {
            Position = 0;
        }
    }

    public void Seek(double positionMs)
    {
        Position = positionMs;
        // TODO: Forward to audio backend
    }

    public void SetShuffle(bool shuffle)
    {
        IsShuffle = shuffle;
        // TODO: Forward to audio backend / reshuffle queue
    }

    public void SetRepeatMode(RepeatMode mode)
    {
        RepeatMode = mode;
        // TODO: Forward to audio backend
    }

    public void PlayContext(PlaybackContextInfo context, int startIndex = 0)
    {
        CurrentContext = context;
        QueuePosition = startIndex;
        // TODO: Load tracks from context into queue and start playback
    }

    public void PlayTrack(string trackId, PlaybackContextInfo? context = null)
    {
        if (context != null)
        {
            CurrentContext = context;
        }
        // TODO: Load track and start playback
    }

    public void AddToQueue(string trackId)
    {
        _queue.Add(new QueueItem
        {
            TrackId = trackId,
            Title = string.Empty,    // TODO: Resolve from cache/catalog
            ArtistName = string.Empty,
            IsUserQueued = true
        });
    }

    public void AddToQueue(IEnumerable<string> trackIds)
    {
        foreach (var trackId in trackIds)
        {
            AddToQueue(trackId);
        }
    }

    public void LoadQueue(IReadOnlyList<QueueItem> items, PlaybackContextInfo context, int startIndex = 0)
    {
        _queue.Clear();
        foreach (var item in items)
        {
            _queue.Add(item);
        }

        CurrentContext = context;
        QueuePosition = startIndex;

        if (startIndex >= 0 && startIndex < items.Count)
        {
            LoadTrackFromQueue(startIndex);
            IsPlaying = false; // Start paused; user clicks play
        }
    }

    private void LoadTrackFromQueue(int index)
    {
        if (index < 0 || index >= _queue.Count) return;

        var track = _queue[index];
        CurrentTrackId = track.TrackId;
        CurrentTrackTitle = track.Title;
        CurrentArtistName = track.ArtistName;
        CurrentAlbumArt = track.AlbumArt;
        Duration = track.DurationMs;
        Position = 0;
    }

    public void Dispose()
    {
        _playerContext.PropertyChanged -= OnPlayerContextPropertyChanged;
    }
}
