using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Wavee.Controls.Lyrics.Enums;
using Wavee.Controls.Lyrics.Extensions;
using Wavee.Controls.Lyrics.Helper;
using Wavee.Controls.Lyrics.Models;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.Controls.Lyrics.Models.Settings;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Microsoft.UI;
using Windows.UI;
using ColorHelper = Wavee.Controls.Lyrics.Helper.ColorHelper;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Manages lyrics state for the right-panel lyrics view.
/// Subscribes to <see cref="IPlaybackStateService"/> for track changes,
/// fetches lyrics via <see cref="ILyricsService"/>, and exposes data
/// that the view uses to drive <see cref="Wavee.Controls.Lyrics.Controls.NowPlayingCanvas"/>.
/// </summary>
public sealed partial class LyricsViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackStateService _playbackState;
    private readonly ILyricsService _lyricsService;
    private readonly ILogger? _logger;

    private string? _loadedTrackId;
    private bool _loadedTrackSucceeded;
    private CancellationTokenSource? _fetchCts;
    private bool _disposed;

    [ObservableProperty]
    private LyricsData? _currentLyrics;

    [ObservableProperty]
    private SongInfo _currentSongInfo = new() { Title = "", Artist = "", Album = "" };

    [ObservableProperty]
    private bool _hasLyrics;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private NowPlayingPalette? _currentPalette;

    [ObservableProperty]
    private LyricsSearchDiagnostics? _lastDiagnostics;

    public LyricsWindowStatus WindowStatus { get; }

    public IPlaybackStateService PlaybackState => _playbackState;

    /// <summary>
    /// Tracks last known service position + wall-clock time for interpolation.
    /// Updated whenever <see cref="IPlaybackStateService.Position"/> changes.
    /// </summary>
    public DateTime LastPositionTimestamp { get; private set; } = DateTime.UtcNow;
    public double LastServicePosition { get; private set; }

    public LyricsViewModel(
        IPlaybackStateService playbackState,
        ILyricsService lyricsService,
        ILogger<LyricsViewModel>? logger = null)
    {
        _playbackState = playbackState;
        _lyricsService = lyricsService;
        _logger = logger;

        WindowStatus = CreateSidebarWindowStatus();

        _playbackState.PropertyChanged += OnPlaybackStateChanged;
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IPlaybackStateService.CurrentTrackId):
                _ = DeferredLoadLyricsAsync();
                break;
            case nameof(IPlaybackStateService.CurrentTrackTitle):
            case nameof(IPlaybackStateService.CurrentArtistName):
                // Metadata can arrive after CurrentTrackId when the Spotify state stream
                // delivers the URI before the title/artist. If the previous fetch failed
                // (most commonly because the title was null at call time), retry now that
                // metadata is present. _loadedTrackSucceeded gates this so successful
                // loads aren't re-fetched on every metadata tweak.
                if (_loadedTrackId == _playbackState.CurrentTrackId
                    && !_loadedTrackSucceeded
                    && !string.IsNullOrEmpty(_playbackState.CurrentTrackTitle))
                {
                    _loadedTrackId = null; // allow the early-exit guard to proceed
                    _ = DeferredLoadLyricsAsync();
                }
                break;
            case nameof(IPlaybackStateService.Position):
                LastServicePosition = _playbackState.Position;
                LastPositionTimestamp = DateTime.UtcNow;
                break;
        }
    }

    /// <summary>
    /// Gets the interpolated playback position (ms) based on the last known
    /// service position plus elapsed wall-clock time.
    /// </summary>
    public TimeSpan GetInterpolatedPosition()
    {
        if (!_playbackState.IsPlaying)
            return TimeSpan.FromMilliseconds(LastServicePosition);

        var elapsed = (DateTime.UtcNow - LastPositionTimestamp).TotalMilliseconds;
        var interpolated = LastServicePosition + elapsed;
        var duration = _playbackState.Duration;
        if (duration > 0 && interpolated > duration)
            interpolated = duration;

        return TimeSpan.FromMilliseconds(interpolated);
    }

    private async Task DeferredLoadLyricsAsync()
    {
        await Task.Yield(); // Let the current dispatch frame render first
        await LoadLyricsAsync();
    }

    public async Task LoadLyricsAsync()
    {
        var trackId = _playbackState.CurrentTrackId;

        _logger?.LogDebug(
            "LoadLyricsAsync ENTER trackId={TrackId} titlePresent={HasTitle} loadedTrackId={LoadedId} loadedSucceeded={Succeeded}",
            trackId, !string.IsNullOrEmpty(_playbackState.CurrentTrackTitle), _loadedTrackId, _loadedTrackSucceeded);

        if (string.IsNullOrEmpty(trackId))
        {
            _loadedTrackId = null;
            _loadedTrackSucceeded = false;
            CurrentLyrics = null;
            HasLyrics = false;
            IsLoading = false;
            return;
        }

        // Only skip re-fetching if we already have a successful result for this track.
        // A previous failed attempt (e.g. metadata race, transient provider error) must
        // be retryable — otherwise the VM is wedged for the rest of the track's playback.
        if (trackId == _loadedTrackId && _loadedTrackSucceeded) return;
        _loadedTrackId = trackId;
        _loadedTrackSucceeded = false;

        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        IsLoading = true;
        HasLyrics = false;
        CurrentLyrics = null;

        try
        {
            var (lyrics, diagnostics) = await _lyricsService.GetLyricsForTrackAsync(
                trackId,
                _playbackState.CurrentTrackTitle,
                _playbackState.CurrentArtistName,
                _playbackState.Duration,
                _playbackState.CurrentAlbumArtLarge ?? _playbackState.CurrentAlbumArt,
                ct);

            if (ct.IsCancellationRequested) return;

            CurrentLyrics = lyrics;
            HasLyrics = lyrics != null;
            _loadedTrackSucceeded = HasLyrics;
            LastDiagnostics = diagnostics;

            CurrentSongInfo = new SongInfo
            {
                Title = _playbackState.CurrentTrackTitle ?? "",
                Artist = _playbackState.CurrentArtistName ?? "",
                Album = "",
                DurationMs = _playbackState.Duration,
            };

            // Extract palette from album art (convert spotify:image: URI to HTTPS)
            var artUrl = SpotifyImageHelper.ToHttpsUrl(
                _playbackState.CurrentAlbumArtLarge ?? _playbackState.CurrentAlbumArt);
            if (!string.IsNullOrEmpty(artUrl))
            {
                var palette = await BuildPaletteFromAlbumArtAsync(artUrl);
                if (!ct.IsCancellationRequested && palette is { } p)
                {
                    WindowStatus.WindowPalette = p;
                    CurrentPalette = p;
                }
            }

            _logger?.LogDebug(
                "LoadLyricsAsync EXIT title=\"{Title}\" hasLyrics={HasLyrics} lineCount={LineCount} cancelled={Cancelled}",
                _playbackState.CurrentTrackTitle, HasLyrics, lyrics?.LyricsLines.Count ?? 0, ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("[Lyrics] Load cancelled for {TrackId}", trackId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load lyrics for {TrackId}", trackId);
            HasLyrics = false;
            _loadedTrackSucceeded = false;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    /// <summary>
    /// Force a reload (e.g. when the panel becomes visible for the first time).
    /// </summary>
    public void InvalidateTrack()
    {
        _loadedTrackId = null;
        _ = LoadLyricsAsync();
    }

    private static LyricsWindowStatus CreateSidebarWindowStatus()
    {
        var status = new LyricsWindowStatus
        {
            ShowLyricsCard = false,
            IsSpoutOutputEnabled = false,
            ShowDebugOverlay = false,
        };

        // Disable all background overlays — let the normal app background show through
        status.LyricsBackgroundSettings.IsPureColorOverlayEnabled = false;
        status.LyricsBackgroundSettings.PureColorOverlayOpacity = 0;
        status.LyricsBackgroundSettings.IsFluidOverlayEnabled = false;
        status.LyricsBackgroundSettings.IsSpectrumOverlayEnabled = false;
        status.LyricsBackgroundSettings.IsCoverOverlayEnabled = false;
        status.LyricsBackgroundSettings.IsFogOverlayEnabled = false;
        status.LyricsBackgroundSettings.IsRaindropOverlayEnabled = false;
        status.LyricsBackgroundSettings.IsSnowFlakeOverlayEnabled = false;

        // Style: left-aligned, bold, dynamic sizing for narrow panel
        status.LyricsStyleSettings.LyricsAlignmentType = TextAlignmentType.Left;
        status.LyricsStyleSettings.LyricsFontWeight = LyricsFontWeight.Bold;
        status.LyricsStyleSettings.IsDynamicLyricsFontSize = true;
        status.LyricsStyleSettings.OriginalLyricsFontSize = 20;
        status.LyricsStyleSettings.PlayingLineTopOffset = 35;
        status.LyricsStyleSettings.LyricsLineSpacingFactor = 0.6;

        // Use AdaptiveGrayed (same as BetterLyrics default): all lines use the same
        // base color (white on dark), and the opacity transitions create the contrast
        // between played (100%), unplayed active (30%), and inactive (30%) lines.
        status.LyricsStyleSettings.LyricsBgFontColorType = LyricsFontColorType.AdaptiveGrayed;
        status.LyricsStyleSettings.LyricsPlayedFgFontColorType = LyricsFontColorType.AdaptiveGrayed;
        status.LyricsStyleSettings.LyricsUnplayedFgFontColorType = LyricsFontColorType.AdaptiveGrayed;
        status.LyricsStyleSettings.LyricsPlayedStrokeFontColorType = LyricsFontColorType.Custom;
        status.LyricsStyleSettings.LyricsUnplayedStrokeFontColorType = LyricsFontColorType.Custom;
        status.LyricsStyleSettings.LyricsCustomPlayedStrokeFontColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
        status.LyricsStyleSettings.LyricsCustomUnplayedStrokeFontColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00);

        // Sidebar mode favors responsiveness over heavy visual effects.
        status.LyricsEffectSettings.IsLyricsBlurEffectEnabled = false;
        status.LyricsEffectSettings.IsLyricsFadeOutEffectEnabled = true;
        status.LyricsEffectSettings.IsLyricsGlowEffectEnabled = false;
        status.LyricsEffectSettings.IsLyricsScaleEffectEnabled = false;
        status.LyricsEffectSettings.IsLyricsFloatAnimationEnabled = false;

        // Neutral palette (will be overridden when album art palette is extracted).
        // All fill colors are white — the opacity transitions in the animator create
        // the visual contrast (played=100%, unplayed/inactive=30%).
        status.WindowPalette = new NowPlayingPalette
        {
            NonCurrentLineFillColor = Colors.White,
            PlayedCurrentLineFillColor = Colors.White,
            UnplayedCurrentLineFillColor = Colors.White,
            PlayedTextStrokeColor = Colors.Transparent,
            UnplayedTextStrokeColor = Colors.Transparent,
            SpectrumColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00),
            UnderlayColor = Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            AccentColor1 = Color.FromArgb(0xFF, 0x80, 0x80, 0x80),
            AccentColor2 = Color.FromArgb(0xFF, 0x60, 0x60, 0x60),
            AccentColor3 = Color.FromArgb(0xFF, 0x40, 0x40, 0x40),
            AccentColor4 = Color.FromArgb(0xFF, 0x30, 0x30, 0x30),
            ThemeType = Microsoft.UI.Xaml.ElementTheme.Dark,
        };

        return status;
    }

    private async Task<NowPlayingPalette?> BuildPaletteFromAlbumArtAsync(string artUrl)
    {
        try
        {
            var bytes = await ImageHelper.GetImageByteArrayFromUrlAsync(artUrl);
            if (bytes is null || bytes.Length == 0) return null;

            var decoder = await ImageHelper.GetBitmapDecoderAsync(bytes.AsBuffer());

            var darkAccents = (await ImageHelper.GetAccentColorsAsync(
                decoder, count: 4, PaletteGeneratorType.Auto, isDark: true))
                .Palette.Select(ColorHelper.FromVector3).ToList();

            var lightAccents = (await ImageHelper.GetAccentColorsAsync(
                decoder, count: 4, PaletteGeneratorType.Auto, isDark: false))
                .Palette.Select(ColorHelper.FromVector3).ToList();

            return BuildPalette(darkAccents, lightAccents);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to extract palette from album art");
            return null;
        }
    }

    private NowPlayingPalette BuildPalette(List<Color> darkAccents, List<Color> lightAccents)
    {
        var fallback = WindowStatus.WindowPalette;

        var accent1 = PickAccent(darkAccents, 0, fallback.AccentColor1);
        var accent2 = PickAccent(darkAccents, 1, fallback.AccentColor2);
        var accent3 = PickAccent(darkAccents, 2, fallback.AccentColor3);
        var accent4 = PickAccent(darkAccents, 3, fallback.AccentColor4);
        var lightAccent1 = PickAccent(lightAccents, 0, accent1);

        var underlay = accent1.WithBrightness(0.18);
        if (underlay.A == 0) underlay = fallback.UnderlayColor;

        return new NowPlayingPalette
        {
            NonCurrentLineFillColor = fallback.NonCurrentLineFillColor,
            PlayedCurrentLineFillColor = fallback.PlayedCurrentLineFillColor,
            UnplayedCurrentLineFillColor = fallback.UnplayedCurrentLineFillColor,
            PlayedTextStrokeColor = fallback.PlayedTextStrokeColor,
            UnplayedTextStrokeColor = fallback.UnplayedTextStrokeColor,
            SpectrumColor = Color.FromArgb(0xCC, lightAccent1.R, lightAccent1.G, lightAccent1.B),
            UnderlayColor = underlay,
            AccentColor1 = accent1,
            AccentColor2 = accent2,
            AccentColor3 = accent3,
            AccentColor4 = accent4,
            ThemeType = Microsoft.UI.Xaml.ElementTheme.Dark,
        };
    }

    private static Color PickAccent(IReadOnlyList<Color> accents, int index, Color fallback)
    {
        if (index >= 0 && index < accents.Count)
        {
            var candidate = accents[index];
            if (candidate.A > 0) return candidate;
        }
        return fallback;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playbackState.PropertyChanged -= OnPlaybackStateChanged;
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
    }
}
