using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Wavee.Core.Http.Lyrics;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for the synced lyrics panel. Fetches lyrics on track change,
/// drives line-level sync via a 200ms timer, and exposes active line events
/// for Composition animations in the view.
/// </summary>
public sealed partial class LyricsViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ISession _session;

    private DispatcherTimer? _syncTimer;
    private DateTime _lastServicePositionUpdate = DateTime.UtcNow;
    private double _lastServicePosition;
    private long[] _startTimesMs = [];
    private CancellationTokenSource? _loadCts;
    private string? _lastLoadedTrackId;
    private bool _disposed;
    private int _activeLineIndex = -1;

    public ObservableCollection<LyricsLineItem> Lines { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasLyrics;

    [ObservableProperty]
    private bool _hasNoLyrics;

    [ObservableProperty]
    private bool _isSynced;

    [ObservableProperty]
    private string? _currentImageUrl;

    [ObservableProperty]
    private bool _isRtl;

    [ObservableProperty]
    private bool _isDenseTypeface;

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                UpdateTimerState();
                // If becoming visible with a track that hasn't been loaded yet, load it
                if (value && !string.IsNullOrEmpty(_playbackStateService.CurrentTrackId)
                          && _lastLoadedTrackId != _playbackStateService.CurrentTrackId)
                {
                    _ = LoadLyricsAsync();
                }
            }
        }
    }

    public int ActiveLineIndex
    {
        get => _activeLineIndex;
        private set
        {
            if (_activeLineIndex != value)
            {
                var prev = _activeLineIndex;
                _activeLineIndex = value;
                OnPropertyChanged();
                ActiveLineChanged?.Invoke(value, prev);
            }
        }
    }

    /// <summary>
    /// Raised when the active line changes. Args: (newIndex, previousIndex).
    /// </summary>
    public event Action<int, int>? ActiveLineChanged;

    public LyricsViewModel(IPlaybackStateService playbackStateService, ISession session)
    {
        _playbackStateService = playbackStateService;
        _session = session;

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _syncTimer.Tick += OnSyncTimerTick;

        _playbackStateService.PropertyChanged += OnPlaybackServicePropertyChanged;
    }

    private void OnPlaybackServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPlaybackStateService.CurrentTrackId):
                _ = LoadLyricsAsync();
                break;
            case nameof(IPlaybackStateService.IsPlaying):
                UpdateTimerState();
                break;
            case nameof(IPlaybackStateService.Position):
                // Sync position baseline for interpolation
                _lastServicePosition = _playbackStateService.Position;
                _lastServicePositionUpdate = DateTime.UtcNow;
                break;
        }
    }

    private async Task LoadLyricsAsync()
    {
        var trackId = _playbackStateService.CurrentTrackId;
        var imageUri = _playbackStateService.CurrentAlbumArt
                       ?? _playbackStateService.CurrentAlbumArtLarge;

        // Cancel any in-flight request
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        if (string.IsNullOrEmpty(trackId))
        {
            ClearLyrics();
            return;
        }

        _lastLoadedTrackId = trackId;
        IsLoading = true;
        HasLyrics = false;
        HasNoLyrics = false;
        ActiveLineIndex = -1;
        Lines.Clear();
        _startTimesMs = [];

        // Update image URL for blur background
        var httpUrl = SpotifyImageHelper.ToHttpsUrl(imageUri);
        CurrentImageUrl = httpUrl ?? imageUri;

        // If no image, use a fallback — the API still needs one but we can try
        if (string.IsNullOrEmpty(imageUri))
            imageUri = "spotify:image:ab67616d0000b273";

        try
        {
            var response = await _session.SpClient.GetLyricsAsync(trackId, imageUri, ct);

            if (ct.IsCancellationRequested) return;

            if (response?.Lyrics == null || response.Lyrics.Lines.Count == 0)
            {
                HasNoLyrics = true;
                IsLoading = false;
                return;
            }

            IsSynced = response.Lyrics.IsSynced;
            IsRtl = response.Lyrics.IsRtlLanguage;
            IsDenseTypeface = response.Lyrics.IsDenseTypeface;

            // Build line items
            var lines = response.Lyrics.Lines;
            var startTimes = new long[lines.Count];

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                startTimes[i] = line.StartTimeMilliseconds;

                var item = new LyricsLineItem(
                    line.Words,
                    line.StartTimeMilliseconds,
                    line.IsInstrumental,
                    IsSynced ? 0.4 : 1.0, // UNSYNCED: all lines full opacity
                    new RelayCommand(() => SeekToLine(line.StartTimeMilliseconds)));

                Lines.Add(item);
            }

            _startTimesMs = startTimes;
            HasLyrics = true;
            IsLoading = false;

            // Sync position immediately
            _lastServicePosition = _playbackStateService.Position;
            _lastServicePositionUpdate = DateTime.UtcNow;
            if (IsSynced)
                UpdateActiveLine(_lastServicePosition);

            UpdateTimerState();
        }
        catch (OperationCanceledException)
        {
            // Track changed while loading — ignore
        }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested)
            {
                HasNoLyrics = true;
                IsLoading = false;
            }
        }
    }

    private void SeekToLine(long startTimeMs)
    {
        _playbackStateService.Seek(startTimeMs);
        // Immediately update position baseline
        _lastServicePosition = startTimeMs;
        _lastServicePositionUpdate = DateTime.UtcNow;
        if (IsSynced)
            UpdateActiveLine(startTimeMs);
    }

    private void OnSyncTimerTick(object? sender, object e)
    {
        if (!_playbackStateService.IsPlaying || _startTimesMs.Length == 0) return;

        // Interpolate position (same wall-clock pattern as PlayerBarViewModel)
        var elapsed = (DateTime.UtcNow - _lastServicePositionUpdate).TotalMilliseconds;
        var interpolated = _lastServicePosition + elapsed;
        var duration = _playbackStateService.Duration;
        if (duration > 0 && interpolated > duration)
            interpolated = duration;

        UpdateActiveLine(interpolated);
    }

    private void UpdateActiveLine(double positionMs)
    {
        if (_startTimesMs.Length == 0) return;

        var pos = (long)positionMs;
        var idx = Array.BinarySearch(_startTimesMs, pos);
        if (idx < 0) idx = ~idx - 1;
        idx = Math.Clamp(idx, 0, _startTimesMs.Length - 1);

        if (idx == ActiveLineIndex) return;

        // Deactivate previous
        if (ActiveLineIndex >= 0 && ActiveLineIndex < Lines.Count)
            Lines[ActiveLineIndex].IsActive = false;

        // Activate new
        if (idx >= 0 && idx < Lines.Count)
            Lines[idx].IsActive = true;

        ActiveLineIndex = idx;
    }

    private void UpdateTimerState()
    {
        if (_syncTimer == null) return;

        var shouldRun = IsVisible && HasLyrics && IsSynced && _playbackStateService.IsPlaying;
        if (shouldRun)
        {
            if (!_syncTimer.IsEnabled)
            {
                // Re-sync baseline before starting
                _lastServicePosition = _playbackStateService.Position;
                _lastServicePositionUpdate = DateTime.UtcNow;
                _syncTimer.Start();
            }
        }
        else
        {
            if (_syncTimer.IsEnabled)
                _syncTimer.Stop();
        }
    }

    private void ClearLyrics()
    {
        Lines.Clear();
        _startTimesMs = [];
        _lastLoadedTrackId = null;
        HasLyrics = false;
        HasNoLyrics = false;
        IsLoading = false;
        IsSynced = false;
        ActiveLineIndex = -1;
        CurrentImageUrl = null;
        UpdateTimerState();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playbackStateService.PropertyChanged -= OnPlaybackServicePropertyChanged;

        if (_syncTimer != null)
        {
            _syncTimer.Stop();
            _syncTimer.Tick -= OnSyncTimerTick;
            _syncTimer = null;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}

/// <summary>
/// Represents a single lyrics line for data binding.
/// </summary>
public sealed partial class LyricsLineItem : ObservableObject
{
    public string Words { get; }
    public long StartTimeMs { get; }
    public bool IsInstrumental { get; }
    public IRelayCommand SeekCommand { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private double _opacity;

    public LyricsLineItem(string words, long startTimeMs, bool isInstrumental, double initialOpacity, IRelayCommand seekCommand)
    {
        Words = words;
        StartTimeMs = startTimeMs;
        IsInstrumental = isInstrumental;
        _opacity = initialOpacity;
        SeekCommand = seekCommand;
    }

    partial void OnIsActiveChanged(bool value)
    {
        Opacity = value ? 1.0 : 0.4;
    }
}
