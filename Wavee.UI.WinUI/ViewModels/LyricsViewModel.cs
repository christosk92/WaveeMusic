using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Wavee.Core.Http.Lyrics;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services.Lyrics;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for the synced lyrics panel. Fetches lyrics on track change,
/// drives line-level sync via a timer, and exposes active line + progress events
/// for karaoke sweep animations in the view.
/// </summary>
public sealed partial class LyricsViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ISettingsService _settingsService;
    private readonly LyricsSearchService _searchService;

    private DispatcherTimer? _syncTimer;
    private DateTime _lastServicePositionUpdate = DateTime.UtcNow;
    private double _lastServicePosition;
    private long[] _startTimesMs = [];
    private CancellationTokenSource? _loadCts;
    private string? _lastLoadedTrackId;
    private bool _hasProviderColors;
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
    private string? _currentTrackTitle;

    [ObservableProperty]
    private string? _currentArtistName;

    [ObservableProperty]
    private bool _isRtl;

    [ObservableProperty]
    private bool _isDenseTypeface;

    [ObservableProperty]
    private Windows.UI.Color _backgroundColor = Windows.UI.Color.FromArgb(255, 30, 30, 30);

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

    /// <summary>
    /// Raised when new lyrics are loaded (or cleared). View should reset scroll position.
    /// </summary>
    public event Action? LyricsLoaded;

    public LyricsViewModel(
        IPlaybackStateService playbackStateService,
        ISettingsService settingsService,
        LyricsSearchService searchService)
    {
        _playbackStateService = playbackStateService;
        _settingsService = settingsService;
        _searchService = searchService;

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
            case nameof(IPlaybackStateService.CurrentAlbumArtColor):
                if (!_hasProviderColors && !string.IsNullOrEmpty(_playbackStateService.CurrentAlbumArtColor))
                    BackgroundColor = HexToColor(_playbackStateService.CurrentAlbumArtColor!);
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

        // Update image URL and track info for header
        var httpUrl = SpotifyImageHelper.ToHttpsUrl(imageUri);
        CurrentImageUrl = httpUrl ?? imageUri;
        CurrentTrackTitle = _playbackStateService.CurrentTrackTitle;
        CurrentArtistName = _playbackStateService.CurrentArtistName;

        // If no image, use a fallback — the API still needs one but we can try
        if (string.IsNullOrEmpty(imageUri))
            imageUri = "spotify:image:ab67616d0000b273";

        try
        {
            var title = _playbackStateService.CurrentTrackTitle;
            var artist = _playbackStateService.CurrentArtistName;
            var duration = _playbackStateService.Duration;

            var (response, wordTimings) = await _searchService.SearchAsync(
                title, artist, album: null, duration, trackId, imageUri, ct);

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

            // Extract background color from API response
            _hasProviderColors = response.Colors != null;
            if (response.Colors != null)
            {
                BackgroundColor = IntToColor(response.Colors.Background);
            }
            else if (!string.IsNullOrEmpty(_playbackStateService.CurrentAlbumArtColor))
            {
                BackgroundColor = HexToColor(_playbackStateService.CurrentAlbumArtColor!);
            }

            // Build line items
            var lines = response.Lyrics.Lines;
            var startTimes = new long[lines.Count];
            var trackDuration = _playbackStateService.Duration;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                startTimes[i] = line.StartTimeMilliseconds;

                // Compute end time: next line's start, or track duration
                var endTimeMs = i + 1 < lines.Count
                    ? lines[i + 1].StartTimeMilliseconds
                    : (long)trackDuration;

                // Cap line duration at 15s to avoid extremely slow sweeps
                if (endTimeMs - line.StartTimeMilliseconds > 15000)
                    endTimeMs = line.StartTimeMilliseconds + 15000;

                // Get word timings for this line if available
                List<LrcWordTiming>? lineWordTimings = null;
                wordTimings?.TryGetValue(i, out lineWordTimings);

                var item = new LyricsLineItem(
                    line.Words,
                    line.StartTimeMilliseconds,
                    endTimeMs,
                    line.IsInstrumental,
                    IsRtl,
                    lineWordTimings,
                    IsSynced ? 0.2 : 1.0,
                    18.0,
                    new RelayCommand(() => SeekToLine(line.StartTimeMilliseconds)));

                Lines.Add(item);
            }

            _startTimesMs = startTimes;
            HasLyrics = true;
            IsLoading = false;
            LyricsLoaded?.Invoke();

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

        // Update progress for the active line (karaoke sweep)
        if (ActiveLineIndex >= 0 && ActiveLineIndex < Lines.Count)
        {
            var line = Lines[ActiveLineIndex];
            double progress;

            if (line.WordTimings is { Count: > 0 })
            {
                progress = ComputeWordLevelProgress(interpolated, line);
            }
            else
            {
                // Linear sweep across line duration
                var start = line.StartTimeMs;
                var end = line.EndTimeMs;
                var lineDuration = end - start;
                progress = lineDuration > 0
                    ? Math.Clamp((interpolated - start) / lineDuration, 0.0, 1.0)
                    : 1.0;
            }

            line.Progress = progress;
        }
    }

    private static double ComputeWordLevelProgress(double positionMs, LyricsLineItem line)
    {
        var timings = line.WordTimings!;
        var totalChars = line.Words.Length;
        if (totalChars == 0) return 1.0;

        var charsSoFar = 0;
        foreach (var word in timings)
        {
            if (positionMs < word.StartMs)
                break;

            if (positionMs <= word.EndMs)
            {
                // Currently in this word — partial progress
                var wordDuration = word.EndMs - word.StartMs;
                var wordProgress = wordDuration > 0
                    ? (positionMs - word.StartMs) / wordDuration
                    : 1.0;
                return Math.Clamp((charsSoFar + word.Text.Length * wordProgress) / totalChars, 0.0, 1.0);
            }

            charsSoFar += word.Text.Length;
        }

        return Math.Clamp((double)charsSoFar / totalChars, 0.0, 1.0);
    }

    private void UpdateActiveLine(double positionMs)
    {
        if (_startTimesMs.Length == 0) return;

        var pos = (long)positionMs;
        var idx = Array.BinarySearch(_startTimesMs, pos);
        if (idx < 0) idx = ~idx - 1;
        idx = Math.Clamp(idx, 0, _startTimesMs.Length - 1);

        if (idx == ActiveLineIndex) return;

        // Deactivate previous + reset progress
        if (ActiveLineIndex >= 0 && ActiveLineIndex < Lines.Count)
        {
            Lines[ActiveLineIndex].IsActive = false;
            Lines[ActiveLineIndex].Progress = 0;
        }

        // Activate new
        if (idx >= 0 && idx < Lines.Count)
            Lines[idx].IsActive = true;

        ActiveLineIndex = idx;
    }

    /// <summary>
    /// Returns the current interpolated playback position in milliseconds.
    /// Used by the canvas render thread for smooth animation.
    /// </summary>
    public double GetInterpolatedPositionMs()
    {
        if (!_playbackStateService.IsPlaying)
            return _lastServicePosition;

        var elapsed = (DateTime.UtcNow - _lastServicePositionUpdate).TotalMilliseconds;
        var interpolated = _lastServicePosition + elapsed;
        var duration = _playbackStateService.Duration;
        if (duration > 0 && interpolated > duration)
            interpolated = duration;
        return interpolated;
    }

    private void UpdateTimerState()
    {
        if (_syncTimer == null) return;

        var shouldRun = IsVisible && HasLyrics && IsSynced && _playbackStateService.IsPlaying;
        if (shouldRun)
        {
            // Use faster timer for smooth karaoke sweep
            _syncTimer.Interval = TimeSpan.FromMilliseconds(50);

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
        CurrentTrackTitle = null;
        CurrentArtistName = null;
        UpdateTimerState();
    }

    private static Windows.UI.Color IntToColor(int value)
    {
        return Windows.UI.Color.FromArgb(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }

    private static Windows.UI.Color HexToColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }
        if (hex.Length == 8)
        {
            return Windows.UI.Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16));
        }
        return Windows.UI.Color.FromArgb(255, 26, 26, 46);
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
/// Represents a single lyrics line for data binding with karaoke support.
/// </summary>
public sealed partial class LyricsLineItem : ObservableObject
{
    public string Words { get; }
    public long StartTimeMs { get; }
    public long EndTimeMs { get; }
    public bool IsInstrumental { get; }
    public bool IsRtl { get; }
    public List<LrcWordTiming>? WordTimings { get; }
    public IRelayCommand SeekCommand { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private double _opacity;

    [ObservableProperty]
    private double _fontSize;

    /// <summary>
    /// Karaoke sweep progress: 0.0 (start) to 1.0 (fully revealed). Updated at ~20fps.
    /// </summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>
    /// Opacity for the dim text layer when this line is inactive. Set by distance from active line.
    /// </summary>
    [ObservableProperty]
    private double _dimOpacity;

    public LyricsLineItem(
        string words, long startTimeMs, long endTimeMs,
        bool isInstrumental, bool isRtl,
        List<LrcWordTiming>? wordTimings,
        double initialOpacity, double initialFontSize,
        IRelayCommand seekCommand)
    {
        Words = words;
        StartTimeMs = startTimeMs;
        EndTimeMs = endTimeMs;
        IsInstrumental = isInstrumental;
        IsRtl = isRtl;
        WordTimings = wordTimings;
        _opacity = initialOpacity;
        _fontSize = initialFontSize;
        _dimOpacity = initialOpacity;
        SeekCommand = seekCommand;
    }

    partial void OnIsActiveChanged(bool value)
    {
        Opacity = value ? 1.0 : 0.4;
        FontSize = value ? 22.0 : 18.0;
    }
}
