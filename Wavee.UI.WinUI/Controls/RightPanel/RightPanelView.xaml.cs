using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using System.Numerics;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media.Animation;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class RightPanelView : UserControl
{
    private const double MinPanelWidth = 200;
    private const double MaxPanelWidth = 500;

    private bool _draggingResizer;
    private double _preManipulationWidth;

    // Details integration
    private TrackDetailsViewModel? _detailsVm;

    // Lyrics integration
    private LyricsViewModel? _lyricsVm;
    private DispatcherQueueTimer? _positionTimer;
    private DispatcherQueueTimer? _scrollResetTimer;
    private double _lastCanvasPositionMs = -1;
    private bool _lyricsInitialized;
    private bool _showingLoadingDots;
    private readonly ThemeColorService? _themeColors;
    private readonly ILyricsService? _lyricsService;
    private readonly ISettingsService? _settingsService;
    private bool _themeColorsSubscribed;

    private static readonly LyricsData LoadingDotsData = CreateLoadingDotsData();

    private static LyricsData CreateLoadingDotsData()
    {
        var dot = "●";
        var line = new LyricsLine
        {
            PrimaryText = $"{dot}  {dot}  {dot}",
            StartMs = 0,
            EndMs = 1200,
            IsPrimaryHasRealSyllableInfo = true,
            PrimarySyllables =
            [
                new BaseLyrics { Text = dot, StartMs = 0,   EndMs = 400,  StartIndex = 0 },
                new BaseLyrics { Text = dot, StartMs = 400,  EndMs = 800,  StartIndex = 3 },
                new BaseLyrics { Text = dot, StartMs = 800,  EndMs = 1200, StartIndex = 6 },
            ]
        };
        return new LyricsData { LyricsLines = [line] };
    }

    public RightPanelView()
    {
        InitializeComponent();
        _themeColors = Ioc.Default.GetService<ThemeColorService>();
        _lyricsService = Ioc.Default.GetService<ILyricsService>();
        _settingsService = Ioc.Default.GetService<ISettingsService>();
        _lyricsVm = Ioc.Default.GetService<LyricsViewModel>();
        _detailsVm = Ioc.Default.GetService<TrackDetailsViewModel>();
        Visibility = Visibility.Collapsed;
        Width = PanelWidth;
    }

    // ── Lifecycle ──

    private void RightPanelView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_themeColors != null && !_themeColorsSubscribed)
        {
            _themeColors.ThemeChanged += OnThemeColorsChanged;
            _themeColorsSubscribed = true;
        }

        InitializeLyrics();
        ActualThemeChanged += OnActualThemeChanged;
        SizeChanged += OnPanelSizeChanged;
        UpdateCanvasClearColor();
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SelectedMode == RightPanelMode.Lyrics)
            UpdateCanvasLayout();
    }

    private void RightPanelView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_themeColors != null && _themeColorsSubscribed)
        {
            _themeColors.ThemeChanged -= OnThemeColorsChanged;
            _themeColorsSubscribed = false;
        }

        ActualThemeChanged -= OnActualThemeChanged;
        SizeChanged -= OnPanelSizeChanged;
        TeardownLyrics();

        if (_detailsVm != null && _detailsSubscribed)
        {
            _detailsVm.PropertyChanged -= OnDetailsVmPropertyChanged;
            _detailsSubscribed = false;
        }
        TeardownCanvasBackground();
        TeardownDetailsLyricsSnippet();
    }

    private void OnThemeColorsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateCanvasClearColor();
            UpdateLyricsPaletteForTheme();
        });
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCanvasClearColor();
        UpdateLyricsPaletteForTheme();
    }

    private void UpdateLyricsPaletteForTheme()
    {
        if (_lyricsVm == null) return;

        bool isDark = ActualTheme != ElementTheme.Light;
        var fg = isDark ? Colors.White : Colors.Black;

        var palette = _lyricsVm.WindowStatus.WindowPalette;
        palette.NonCurrentLineFillColor = fg;
        palette.PlayedCurrentLineFillColor = fg;
        palette.UnplayedCurrentLineFillColor = fg;
        palette.ThemeType = isDark
            ? Microsoft.UI.Xaml.ElementTheme.Dark
            : Microsoft.UI.Xaml.ElementTheme.Light;

        NowPlayingCanvas.SetNowPlayingPalette(palette);
    }

    private void InitializeLyrics()
    {
        if (_lyricsInitialized) return;

        if (_lyricsVm == null) return;

        _lyricsInitialized = true;

        // Configure the canvas
        NowPlayingCanvas.LyricsWindowStatus = _lyricsVm.WindowStatus;
        var bg = _lyricsVm.WindowStatus.LyricsBackgroundSettings;
        bg.IsPureColorOverlayEnabled = false;
        bg.PureColorOverlayOpacity = 0;
        bg.IsFluidOverlayEnabled = false;
        bg.IsCoverOverlayEnabled = false;
        bg.IsSpectrumOverlayEnabled = false;
        bg.IsFogOverlayEnabled = false;
        bg.IsRaindropOverlayEnabled = false;
        bg.IsSnowFlakeOverlayEnabled = false;
        NowPlayingCanvas.SeekRequested += OnSeekRequested;
        UpdateCanvasClearColor();
        UpdateLyricsPaletteForTheme();

        // Subscribe to ViewModel state changes
        _lyricsVm.PropertyChanged += OnLyricsVmPropertyChanged;

        // Subscribe to playback state for play/pause/buffering changes
        _lyricsVm.PlaybackState.PropertyChanged += OnPlaybackStateChanged;

        // Position timer — 50ms (~20fps) is enough for readable lyric progression
        // and keeps dispatcher pressure lower during playback.
        _positionTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _positionTimer.Tick += OnPositionTimerTick;

        // If there's already a track loaded, apply it
        ApplyCurrentLyricsState();

        // Start the timer if lyrics tab is visible and playing
        UpdateTimerState();
    }

    private void TeardownLyrics()
    {
        if (!_lyricsInitialized) return;

        _positionTimer?.Stop();
        _scrollResetTimer?.Stop();

        if (_lyricsVm != null)
        {
            _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
            _lyricsVm.PlaybackState.PropertyChanged -= OnPlaybackStateChanged;
        }

        NowPlayingCanvas.SeekRequested -= OnSeekRequested;
        NowPlayingCanvas.SetIsPlaying(false);

        _lyricsInitialized = false;
    }

    // ── Playback state → Timer sync ──

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.IsPlaying))
            UpdateTimerState();
    }

    // ── ViewModel → Canvas binding ──

    private void OnLyricsVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.CurrentLyrics):
            case nameof(LyricsViewModel.CurrentSongInfo):
            case nameof(LyricsViewModel.HasLyrics):
            case nameof(LyricsViewModel.IsLoading):
                ApplyCurrentLyricsState();
                break;
            case nameof(LyricsViewModel.CurrentPalette):
                if (_lyricsVm?.CurrentPalette is { } palette)
                    NowPlayingCanvas.SetNowPlayingPalette(palette);
                break;
        }
    }

    private void ApplyCurrentLyricsState()
    {
        if (_lyricsVm == null) return;

        // Never show the ProgressRing — use canvas dots instead
        LyricsLoadingRing.Visibility = Visibility.Collapsed;

        var showNoLyrics = !_lyricsVm.IsLoading && !_lyricsVm.HasLyrics
                           && !string.IsNullOrEmpty(_lyricsVm.PlaybackState.CurrentTrackId);
        NoLyricsText.Visibility = showNoLyrics ? Visibility.Visible : Visibility.Collapsed;

        // Canvas visible for both loading (dots) and lyrics
        var showCanvas = SelectedMode == RightPanelMode.Lyrics
                         && (_lyricsVm.HasLyrics || _lyricsVm.IsLoading);
        NowPlayingCanvas.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;
        LyricsInteractionOverlay.Visibility = _lyricsVm.HasLyrics ? Visibility.Visible : Visibility.Collapsed;

#if DEBUG
        LyricsDebugButton.Visibility = Visibility.Visible;
#endif

        if (_lyricsVm.CurrentLyrics != null)
        {
            _showingLoadingDots = false;
            NowPlayingCanvas.SetLyricsData(_lyricsVm.CurrentLyrics);
            NowPlayingCanvas.SetSongInfo(_lyricsVm.CurrentSongInfo);
            NowPlayingCanvas.SetIsPlaying(_lyricsVm.PlaybackState.IsPlaying);
            var position = _lyricsVm.GetInterpolatedPosition();
            _lastCanvasPositionMs = position.TotalMilliseconds;
            NowPlayingCanvas.SetPosition(position);
        }
        else if (_lyricsVm.IsLoading)
        {
            _showingLoadingDots = true;
            NowPlayingCanvas.SetLyricsData(LoadingDotsData);
            NowPlayingCanvas.SetIsPlaying(_lyricsVm.PlaybackState.IsPlaying);
        }

        UpdateTimerState();
    }

    private void UpdateTimerState()
    {
        if (_lyricsVm == null)
        {
            _positionTimer?.Stop();
            NowPlayingCanvas.SetRenderingActive(false);
            return;
        }

        var canRender = SelectedMode == RightPanelMode.Lyrics
                        && Visibility == Visibility.Visible
                        && (_lyricsVm.HasLyrics || _lyricsVm.IsLoading);

        // Realtime updates are only needed while playback is progressing.
        var shouldRunTimer = canRender && _lyricsVm.PlaybackState.IsPlaying;

        // Keep rendering active only for realtime playback or direct user interaction.
        var isInteracting = NowPlayingCanvas.IsMouseInLyricsArea
                            || NowPlayingCanvas.IsMousePressing
                            || NowPlayingCanvas.IsMouseScrolling;
        var shouldRender = canRender && (shouldRunTimer || isInteracting);

        NowPlayingCanvas.SetRenderingActive(shouldRender);
        NowPlayingCanvas.SetIsPlaying(canRender && _lyricsVm.PlaybackState.IsPlaying);

        if (shouldRunTimer)
            _positionTimer?.Start();
        else
            _positionTimer?.Stop();

        if (!canRender)
        {
            _scrollResetTimer?.Stop();
            NowPlayingCanvas.SetIsPlaying(false);
            _lastCanvasPositionMs = -1;
        }
        else if (!shouldRunTimer)
        {
            NowPlayingCanvas.SetIsPlaying(false);
            _lastCanvasPositionMs = -1;
        }
    }

    private void OnPositionTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_lyricsVm == null) return;

        if (_showingLoadingDots)
        {
            // Loop 0→1200ms for dot animation
            var elapsed = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) % 1200;
            NowPlayingCanvas.SetPosition(TimeSpan.FromMilliseconds(elapsed));
        }
        else
        {
            var position = _lyricsVm.GetInterpolatedPosition();
            var positionMs = position.TotalMilliseconds;

            // Skip tiny deltas to avoid unnecessary DP churn every tick.
            if (_lastCanvasPositionMs >= 0 && Math.Abs(positionMs - _lastCanvasPositionMs) < 35)
                return;

            _lastCanvasPositionMs = positionMs;
            NowPlayingCanvas.SetPosition(position);
        }
    }

    // ── Seek ──

    private void OnSeekRequested(object? sender, TimeSpan position)
    {
        _lyricsVm?.PlaybackState.Seek(position.TotalMilliseconds);
    }

    // ── Mouse interaction ──

    private void LyricsOverlay_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        NowPlayingCanvas.IsMouseInLyricsArea = true;
        UpdateTimerState();
    }

    private void LyricsOverlay_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        NowPlayingCanvas.IsMouseInLyricsArea = false;
        UpdateTimerState();
    }

    private void LyricsOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LyricsInteractionOverlay).Position;
        NowPlayingCanvas.MousePosition = point;
    }

    private void LyricsOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        NowPlayingCanvas.IsMousePressing = true;
        UpdateTimerState();
    }

    private void LyricsOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        NowPlayingCanvas.IsMousePressing = false;
        NowPlayingCanvas.FireSeekIfHovering();
        UpdateTimerState();
    }

    private void LyricsOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        NowPlayingCanvas.IsMouseScrolling = true;
        LyricsSyncButton.Visibility = Visibility.Visible;
        UpdateTimerState();

        var point = e.GetCurrentPoint(LyricsInteractionOverlay);
        var delta = point.Properties.MouseWheelDelta;
        var value = NowPlayingCanvas.MouseScrollOffset + delta;

        // Clamp scroll range
        if (value > 0)
            value = Math.Min(-NowPlayingCanvas.CurrentCanvasYScroll, value);
        else
            value = Math.Max(
                -NowPlayingCanvas.CurrentCanvasYScroll - NowPlayingCanvas.ActualLyricsHeight,
                value);

        NowPlayingCanvas.MouseScrollOffset = value;

        // Auto-resume after 3s of no scrolling
        _scrollResetTimer ??= CreateScrollResetTimer();
        _scrollResetTimer.Stop();
        _scrollResetTimer.Interval = TimeSpan.FromSeconds(3);
        _scrollResetTimer.Start();

        e.Handled = true;
    }

    private DispatcherQueueTimer CreateScrollResetTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ResumeSync();
        return timer;
    }

    private void LyricsSyncButton_Click(object sender, RoutedEventArgs e)
    {
        _scrollResetTimer?.Stop();
        ResumeSync();
    }

    private void ResumeSync()
    {
        NowPlayingCanvas.MouseScrollOffset = 0;
        NowPlayingCanvas.IsMouseScrolling = false;
        LyricsSyncButton.Visibility = Visibility.Collapsed;
        UpdateTimerState();
    }

    // ── Layout ──

    private void LyricsContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCanvasLayout();
    }

    private void UpdateCanvasLayout()
    {
        var w = RootGrid.ActualWidth;
        var h = RootGrid.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Canvas spans the entire root; offset lyrics to the content area below tabs.
        var resizerW = PanelResizer.ActualWidth;
        var tabH = TabHeader.ActualHeight;
        const double padLeft = 12, padRight = 12, padBottom = 12;

        NowPlayingCanvas.LyricsStartX = resizerW + padLeft;
        NowPlayingCanvas.LyricsStartY = tabH;
        NowPlayingCanvas.LyricsWidth = w - resizerW - padLeft - padRight;
        NowPlayingCanvas.LyricsHeight = h - tabH - padBottom;
        NowPlayingCanvas.LyricsOpacity = 1;
        NowPlayingCanvas.AlbumArtRect = Rect.Empty;
    }

    private void UpdateCanvasClearColor()
    {
        // SwapChainPanel can't blend with XAML content, so composite the semi-transparent
        // card color onto an opaque base to approximate the card surface appearance.
        var cardColor = (_themeColors?.CardBackground as SolidColorBrush)?.Color
                        ?? Colors.Transparent;

        Windows.UI.Color baseColor;
        if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBase", out var baseObj)
            && baseObj is Windows.UI.Color resolved)
        {
            baseColor = resolved;
        }
        else
        {
            baseColor = ActualTheme == ElementTheme.Light
                ? Color.FromArgb(255, 243, 243, 243)
                : Color.FromArgb(255, 32, 32, 32);
        }

        float a = cardColor.A / 255f;
        var color = Color.FromArgb(174,
            (byte)(cardColor.R * a + baseColor.R * (1 - a)),
            (byte)(cardColor.G * a + baseColor.G * (1 - a)),
            (byte)(cardColor.B * a + baseColor.B * (1 - a)));

        NowPlayingCanvas.SetClearColor(color);
    }

    

    // ── Tab / visibility management ──

    private void UpdateContentVisibility()
    {
        if (QueueContent == null) return;

        QueueContent.Visibility = SelectedMode == RightPanelMode.Queue ? Visibility.Visible : Visibility.Collapsed;
        LyricsContent.Visibility = SelectedMode == RightPanelMode.Lyrics ? Visibility.Visible : Visibility.Collapsed;
        FriendsContent.Visibility = SelectedMode == RightPanelMode.FriendsActivity ? Visibility.Visible : Visibility.Collapsed;
        DetailsContent.Visibility = SelectedMode == RightPanelMode.Details ? Visibility.Visible : Visibility.Collapsed;

        // Canvas video background — only visible on Details tab
        if (SelectedMode != RightPanelMode.Details)
        {
            DetailsCanvasImage.Visibility = Visibility.Collapsed;
            _canvasMediaPlayer?.Pause();
        }
        else if (_currentCanvasUrl != null && _canvasMediaPlayer != null)
        {
            DetailsCanvasImage.Visibility = Visibility.Visible;
            _canvasMediaPlayer.Play();
        }

        // Details lyrics snippet timer — stop when not on Details tab
        if (SelectedMode != RightPanelMode.Details)
            _detailsLyricsTimer?.Stop();

        if (_lyricsInitialized)
            ApplyCurrentLyricsState();

        QueueTab.IsChecked = SelectedMode == RightPanelMode.Queue;
        LyricsTab.IsChecked = SelectedMode == RightPanelMode.Lyrics;
        FriendsTab.IsChecked = SelectedMode == RightPanelMode.FriendsActivity;
        DetailsTab.IsChecked = SelectedMode == RightPanelMode.Details;

        // When switching to lyrics tab, ensure we have the latest state
        if (SelectedMode == RightPanelMode.Lyrics && _lyricsInitialized)
        {
            _ = _lyricsVm?.LoadLyricsAsync();
            UpdateCanvasLayout();
            // Re-apply lyrics state and kick the canvas with current position
            ApplyCurrentLyricsState();
            if (_lyricsVm != null)
            {
                NowPlayingCanvas.SetPosition(_lyricsVm.GetInterpolatedPosition());
                NowPlayingCanvas.SetIsPlaying(_lyricsVm.PlaybackState.IsPlaying);
            }
        }

        // When switching to details tab, load details if needed
        if (SelectedMode == RightPanelMode.Details && _detailsVm != null)
        {
            _ = LoadAndBindDetailsAsync();
        }

        UpdateTimerState();
    }

    // ── Tab header clicks ──

    private void QueueTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.Queue;

    private void LyricsTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.Lyrics;

    private void FriendsTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.FriendsActivity;

    private void DetailsTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.Details;

    // ── Details panel binding ──

    private bool _detailsSubscribed;

    private async Task LoadAndBindDetailsAsync()
    {
        if (_detailsVm == null) return;

        if (!_detailsSubscribed)
        {
            _detailsVm.PropertyChanged += OnDetailsVmPropertyChanged;
            _detailsSubscribed = true;
        }

        await _detailsVm.LoadDetailsAsync();
        ApplyDetailsState();
    }

    private void OnDetailsVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (SelectedMode != RightPanelMode.Details) return;

        DispatcherQueue.TryEnqueue(() => ApplyDetailsState());
    }

    private bool _detailsHadData;

    private void ApplyDetailsState()
    {
        if (_detailsVm == null) return;

        DetailsLoadingShimmer.Visibility = _detailsVm.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        DetailsErrorText.Text = _detailsVm.ErrorMessage ?? "";
        DetailsErrorText.Visibility = !string.IsNullOrEmpty(_detailsVm.ErrorMessage)
            ? Visibility.Visible : Visibility.Collapsed;

        var hasData = _detailsVm.HasData;

        // Canvas (composition background) — update immediately, no delay
        if (hasData && _detailsVm.HasCanvas)
            SetupCanvasBackground(_detailsVm.CanvasUrl);
        else
            SetupCanvasBackground(null);

        if (hasData)
        {
            // If we already had data showing (track change), animate the transition
            if (_detailsHadData)
                _ = AnimateDetailsContentChangeAsync();
            else
                _ = AnimateDetailsContentInAsync();
        }
        else
        {
            UpdateDetailsContent();
        }

        _detailsHadData = hasData;
    }

    /// <summary>
    /// Crossfade: fade out → update → fade in + slide up.
    /// </summary>
    private async Task AnimateDetailsContentChangeAsync()
    {
        // Fade out
        await AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(150),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseIn)
            .StartAsync(DetailsContent);

        // Update content while invisible
        UpdateDetailsContent();

        // Fade in with slide up
        await AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(250),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .Translation(Axis.Y, from: 12, to: 0, duration: TimeSpan.FromMilliseconds(250),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .StartAsync(DetailsContent);
    }

    /// <summary>
    /// Initial appear: fade in + slide up from below.
    /// </summary>
    private async Task AnimateDetailsContentInAsync()
    {
        UpdateDetailsContent();

        // Start hidden
        DetailsContent.Opacity = 0;

        // Animate in
        await AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .Translation(Axis.Y, from: 20, to: 0, duration: TimeSpan.FromMilliseconds(300),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .StartAsync(DetailsContent);
    }

    /// <summary>
    /// Synchronously applies details VM data to XAML elements.
    /// </summary>
    private void UpdateDetailsContent()
    {
        if (_detailsVm == null) return;

        var hasData = _detailsVm.HasData;

        // Artist header
        DetailsArtistHeaderCard.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        if (hasData)
        {
            DetailsArtistName.Text = _detailsVm.ArtistName ?? "";
            DetailsVerifiedIcon.Visibility = _detailsVm.IsVerified ? Visibility.Visible : Visibility.Collapsed;
            DetailsArtistStats.Text = $"{_detailsVm.Followers} followers · {_detailsVm.MonthlyListeners} monthly listeners";

            if (!string.IsNullOrEmpty(_detailsVm.ArtistAvatarUrl))
            {
                DetailsArtistAvatar.ProfilePicture = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri(_detailsVm.ArtistAvatarUrl));
            }
        }

        // Bio card (includes record label)
        var hasBio = hasData && !string.IsNullOrEmpty(_detailsVm.BiographyText);
        var hasLabel = hasData && !string.IsNullOrEmpty(_detailsVm.RecordLabel);
        DetailsBio.Visibility = (hasBio || hasLabel) ? Visibility.Visible : Visibility.Collapsed;
        if (hasBio)
        {
            DetailsBioText.Text = _detailsVm.BiographyText!;
            DetailsBioText.Visibility = Visibility.Visible;
            DetailsBioText.MaxLines = _detailsVm.IsBioExpanded ? 0 : 3;
            DetailsBioToggle.Content = _detailsVm.IsBioExpanded ? "Show less" : "Show more";
            // Toggle visibility is driven by IsTextTrimmedChanged event
            DetailsBioToggle.Visibility = _detailsVm.IsBioExpanded
                ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            DetailsBioText.Visibility = Visibility.Collapsed;
            DetailsBioToggle.Visibility = Visibility.Collapsed;
        }

        DetailsRecordLabel.Visibility = hasLabel ? Visibility.Visible : Visibility.Collapsed;
        DetailsRecordLabel.Text = hasLabel ? $"\u00A9 {_detailsVm.RecordLabel}" : "";

        // Lyrics snippet (mini live canvas)
        var showLyricsSnippet = hasData && _lyricsVm?.HasLyrics == true && _lyricsVm.CurrentLyrics != null;
        DetailsLyricsSnippet.Visibility = showLyricsSnippet ? Visibility.Visible : Visibility.Collapsed;
        if (showLyricsSnippet)
            SetupDetailsLyricsSnippet();
        else
            TeardownDetailsLyricsSnippet();

        // Credits (collapsed by default — show first group only)
        _creditsExpanded = false;
        var hasCredits = hasData && _detailsVm.CreditGroups.Count > 0;
        DetailsCreditsSection.Visibility = hasCredits ? Visibility.Visible : Visibility.Collapsed;
        if (hasCredits)
            ApplyCreditsCollapse();

        // Concerts
        DetailsConcertsSection.Visibility = _detailsVm.HasConcerts
            ? Visibility.Visible : Visibility.Collapsed;
        DetailsConcertsList.ItemsSource = _detailsVm.Concerts;

        // Related Videos
        DetailsRelatedVideosSection.Visibility = _detailsVm.HasRelatedVideos
            ? Visibility.Visible : Visibility.Collapsed;
        DetailsRelatedVideosList.ItemsSource = _detailsVm.RelatedVideos;

        // Scroll to top on content change
        DetailsContent.ChangeView(null, 0, null, true);
    }

    private void DetailsLyricsSnippet_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        SelectedMode = RightPanelMode.Lyrics;
    }

    private void DetailsLyricsSnippet_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ((FrameworkElement)sender).Opacity = 0.8;
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
    }

    private void DetailsLyricsSnippet_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ((FrameworkElement)sender).Opacity = 1.0;
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    }

    // ── Lyrics snippet (TextBlock-based, synced to playback) ──

    private DispatcherQueueTimer? _detailsLyricsTimer;
    private int _lastSnippetLineIndex = -1;

    private void SetupDetailsLyricsSnippet()
    {
        if (_lyricsVm == null) return;

        // Update immediately
        UpdateLyricsSnippetText();

        // Start a timer to keep the snippet in sync (~4 updates/sec)
        if (_detailsLyricsTimer == null)
        {
            _detailsLyricsTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _detailsLyricsTimer.Interval = TimeSpan.FromMilliseconds(250);
            _detailsLyricsTimer.Tick += OnDetailsLyricsTimerTick;
        }
        _detailsLyricsTimer.Start();
    }

    private void TeardownDetailsLyricsSnippet()
    {
        _detailsLyricsTimer?.Stop();
        _lastSnippetLineIndex = -1;
    }

    private void OnDetailsLyricsTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (SelectedMode != RightPanelMode.Details) return;
        UpdateLyricsSnippetText();
    }

    private void UpdateLyricsSnippetText()
    {
        if (_lyricsVm?.CurrentLyrics?.LyricsLines is not { Count: > 0 } lines) return;

        var posMs = _lyricsVm.GetInterpolatedPosition().TotalMilliseconds;

        // Find the current line based on playback position
        var currentIdx = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].StartMs <= posMs)
            {
                currentIdx = i;
                break;
            }
        }

        if (currentIdx < 0) currentIdx = 0;

        // Skip update if same line
        if (currentIdx == _lastSnippetLineIndex) return;
        _lastSnippetLineIndex = currentIdx;

        // Previous, current, next — skip empty lines
        var prev = currentIdx > 0 ? lines[currentIdx - 1].PrimaryText : "";
        var current = lines[currentIdx].PrimaryText;
        var next = currentIdx < lines.Count - 1 ? lines[currentIdx + 1].PrimaryText : "";

        DetailsLyricsPrev.Text = prev;
        DetailsLyricsPrev.Visibility = string.IsNullOrWhiteSpace(prev)
            ? Visibility.Collapsed : Visibility.Visible;

        DetailsLyricsCurrent.Text = current;

        DetailsLyricsNext.Text = next;
        DetailsLyricsNext.Visibility = string.IsNullOrWhiteSpace(next)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Credits collapse/expand ──

    private bool _creditsExpanded;
    private const int CreditsCollapsedMaxPeople = 4;

    private void ApplyCreditsCollapse()
    {
        if (_detailsVm == null) return;
        var allGroups = _detailsVm.CreditGroups;
        var totalPeople = allGroups.Sum(g => g.Contributors?.Count ?? 0);

        if (_creditsExpanded || totalPeople <= CreditsCollapsedMaxPeople)
        {
            DetailsCreditGroups.ItemsSource = allGroups;
            DetailsCreditsToggle.Visibility = totalPeople > CreditsCollapsedMaxPeople
                ? Visibility.Visible : Visibility.Collapsed;
            DetailsCreditsToggle.Content = "Show less";
        }
        else
        {
            // Take groups until we hit the max people limit
            var collapsed = new List<ViewModels.CreditGroupVm>();
            var count = 0;
            foreach (var group in allGroups)
            {
                var contributors = group.Contributors ?? [];
                if (count + contributors.Count > CreditsCollapsedMaxPeople && collapsed.Count > 0)
                    break;
                collapsed.Add(group);
                count += contributors.Count;
                if (count >= CreditsCollapsedMaxPeople) break;
            }
            DetailsCreditGroups.ItemsSource = collapsed;
            var hidden = totalPeople - count;
            DetailsCreditsToggle.Visibility = Visibility.Visible;
            DetailsCreditsToggle.Content = $"View all credits (+{hidden} more)";
        }
    }

    private async void DetailsCreditsToggle_Click(object sender, RoutedEventArgs e)
    {
        _creditsExpanded = !_creditsExpanded;

        // Animate: fade out → update → fade in
        await AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(120),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseIn)
            .StartAsync(DetailsCreditGroups);

        ApplyCreditsCollapse();

        await AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(200),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .Translation(Axis.Y, from: _creditsExpanded ? 8 : -8, to: 0,
                duration: TimeSpan.FromMilliseconds(200),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .StartAsync(DetailsCreditGroups);
    }

    private void DetailsMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailsVm?.PlaybackState is not { } ps) return;
        if (string.IsNullOrEmpty(ps.CurrentTrackId)) return;

        var adapter = new Data.DTOs.NowPlayingTrackAdapter(ps);
        var options = new Track.TrackContextMenuOptions
        {
            ShowCreditsAction = () =>
            {
                // Scroll to credits section
                if (DetailsCreditsSection.Visibility == Visibility.Visible)
                {
                    var transform = DetailsCreditsSection.TransformToVisual(DetailsContent);
                    var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    DetailsContent.ChangeView(null, position.Y, null, false);
                }
            },
            ToggleCanvasAction = () =>
            {
                var wasVisible = DetailsCanvasImage.Visibility == Visibility.Visible;
                if (wasVisible)
                {
                    TeardownCanvasBackground();
                    DetailsCanvasImage.Visibility = Visibility.Collapsed;
                }
                else if (_detailsVm?.HasCanvas == true)
                {
                    SetupCanvasBackground(_detailsVm.CanvasUrl);
                }

                // Persist preference
                _settingsService?.Update(s => s.ShowDetailsCanvas = !wasVisible);
                _ = _settingsService?.SaveAsync();
            },
            ShowCanvasToggle = _detailsVm?.HasCanvas ?? false,
            IsCanvasVisible = DetailsCanvasImage.Visibility == Visibility.Visible,
        };

        var menu = Track.TrackContextMenu.Create(adapter, options);
        menu.ShowAt((Microsoft.UI.Xaml.FrameworkElement)sender);
    }

    private void DetailsBioText_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        // Show "Show more" only when text is actually truncated
        if (_detailsVm != null && !_detailsVm.IsBioExpanded)
        {
            DetailsBioToggle.Visibility = sender.IsTextTrimmed
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DetailsBioToggle_Click(object sender, RoutedEventArgs e)
    {
        _detailsVm?.ToggleBioExpandedCommand.Execute(null);
        if (_detailsVm != null)
        {
            DetailsBioText.MaxLines = _detailsVm.IsBioExpanded ? 0 : 3;
            DetailsBioToggle.Content = _detailsVm.IsBioExpanded ? "Show less" : "Show more";
            DetailsBioToggle.Visibility = Visibility.Visible;
        }
    }

    // ── Canvas video (frame server mode + Win2D blur → standard Image) ──
    // MediaPlayer renders frames to a CanvasRenderTarget, we apply GaussianBlur,
    // then draw to a CanvasImageSource backing a regular XAML Image.
    // No SwapChainPanel = acrylic works on top.

    private Windows.Media.Playback.MediaPlayer? _canvasMediaPlayer;
    private string? _currentCanvasUrl;
    private Microsoft.Graphics.Canvas.CanvasDevice? _canvasDevice;
    private Microsoft.Graphics.Canvas.CanvasRenderTarget? _canvasFrameTarget;
    private float _canvasBlurAmount = 1.3f;
    private Microsoft.Graphics.Canvas.UI.Xaml.CanvasImageSource? _canvasImageSource;
    private long _lastCanvasFrameTicks;
    private const long CanvasFrameIntervalTicks = TimeSpan.TicksPerSecond / 10; // ~10fps throttle

    private void SetupCanvasBackground(string? url)
    {
        // Respect user setting
        var canvasEnabled = _settingsService?.Settings.ShowDetailsCanvas ?? true;

        if (string.IsNullOrEmpty(url) || !canvasEnabled)
        {
            TeardownCanvasBackground();
            DetailsCanvasImage.Visibility = Visibility.Collapsed;
            return;
        }

        if (url == _currentCanvasUrl && _canvasMediaPlayer != null) return;
        _currentCanvasUrl = url;

        TeardownCanvasBackground();

        _canvasDevice ??= new Microsoft.Graphics.Canvas.CanvasDevice();

        _canvasMediaPlayer = new Windows.Media.Playback.MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true,
            IsVideoFrameServerEnabled = true // Frame server mode — no swap chain
        };
        _canvasMediaPlayer.VideoFrameAvailable += OnCanvasVideoFrameAvailable;
        _canvasMediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(url));

        DetailsCanvasImage.Visibility = Visibility.Visible;
        _canvasMediaPlayer.Play();
    }

    private void OnCanvasVideoFrameAvailable(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        // Throttle: skip frames to stay at ~10fps (ambient background doesn't need 30fps)
        var now = DateTime.UtcNow.Ticks;
        if (now - _lastCanvasFrameTicks < CanvasFrameIntervalTicks) return;
        _lastCanvasFrameTicks = now;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_canvasMediaPlayer == null || _canvasDevice == null) return;

            // Render at half resolution — it's a blurred background, doesn't need full res
            var w = Math.Max(1, (int)RootGrid.ActualWidth / 2);
            var h = Math.Max(1, (int)RootGrid.ActualHeight / 2);
            if (w <= 0 || h <= 0) return;

            try
            {
                // Create or resize the render target for video frames
                if (_canvasFrameTarget == null || _canvasFrameTarget.SizeInPixels.Width != w || _canvasFrameTarget.SizeInPixels.Height != h)
                {
                    _canvasFrameTarget?.Dispose();
                    _canvasFrameTarget = new Microsoft.Graphics.Canvas.CanvasRenderTarget(_canvasDevice, w, h, 96);
                }

                // Copy video frame to our render target
                _canvasMediaPlayer.CopyFrameToVideoSurface(_canvasFrameTarget);

                // Create or resize the image source
                if (_canvasImageSource == null || _canvasImageSource.SizeInPixels.Width != w || _canvasImageSource.SizeInPixels.Height != h)
                {
                    _canvasImageSource = new Microsoft.Graphics.Canvas.UI.Xaml.CanvasImageSource(_canvasDevice, w, h, 96);
                    DetailsCanvasImage.Source = _canvasImageSource;
                }

                // Draw blurred frame to image source
                using var ds = _canvasImageSource.CreateDrawingSession(Colors.Transparent);
                var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
                {
                    Source = _canvasFrameTarget,
                    BlurAmount = _canvasBlurAmount,
                    BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Hard
                };
                ds.DrawImage(blur);
                blur.Dispose();
            }
            catch
            {
                // Frame rendering can fail transiently during setup/teardown
            }
        });
    }

    private void TeardownCanvasBackground()
    {
        if (_canvasMediaPlayer != null)
        {
            _canvasMediaPlayer.VideoFrameAvailable -= OnCanvasVideoFrameAvailable;
            _canvasMediaPlayer.Pause();
            _canvasMediaPlayer.Source = null;
            _canvasMediaPlayer.Dispose();
            _canvasMediaPlayer = null;
        }

        _canvasImageSource = null;
        _canvasFrameTarget?.Dispose();
        _canvasFrameTarget = null;
        // Keep _canvasDevice alive for reuse

        _currentCanvasUrl = null;
    }

    // ── Resize gripper ──

    private void Resizer_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        _draggingResizer = true;
        _preManipulationWidth = PanelWidth;
        VisualStateManager.GoToState(this, "ResizerPressed", true);
        e.Handled = true;
    }

    private void Resizer_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var newWidth = _preManipulationWidth - e.Cumulative.Translation.X;
        newWidth = Math.Clamp(newWidth, MinPanelWidth, MaxPanelWidth);
        PanelWidth = newWidth;
        e.Handled = true;
    }

    private void Resizer_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        _draggingResizer = false;
        VisualStateManager.GoToState(this, "ResizerNormal", true);
        e.Handled = true;
    }

    private void Resizer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var resizer = (FrameworkElement)sender;
        resizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
        VisualStateManager.GoToState(this, "ResizerPointerOver", true);
        e.Handled = true;
    }

    private void Resizer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingResizer) return;

        var resizer = (FrameworkElement)sender;
        resizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
        VisualStateManager.GoToState(this, "ResizerNormal", true);
        e.Handled = true;
    }

    private void Resizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        IsOpen = !IsOpen;
        e.Handled = true;
    }

    // ── Lyrics debug ──

    private async void LyricsDebugButton_Click(object sender, RoutedEventArgs e)
    {
        var diag = _lyricsVm?.LastDiagnostics;
        if (diag == null)
        {
            await ShowDebugDialog("No diagnostics available yet.");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Track ID: {diag.TrackId}");
        sb.AppendLine($"Query: \"{diag.QueryTitle}\" by \"{diag.QueryArtist}\"");
        sb.AppendLine($"Duration: {diag.QueryDurationMs:F0}ms");
        sb.AppendLine($"Search time: {diag.TotalSearchTime.TotalMilliseconds:F0}ms");
        sb.AppendLine($"Selected: {diag.SelectedProvider ?? "none"} — {diag.SelectionReason}");
        sb.AppendLine();

        foreach (var p in diag.Providers)
        {
            sb.AppendLine($"── {p.Name} ({p.Status}) ──");
            if (p.Error != null)
                sb.AppendLine($"  Error: {p.Error}");
            if (p.Status == ProviderStatus.Success)
            {
                sb.AppendLine($"  Lines: {p.LineCount}, Syllable sync: {p.HasSyllableSync}");
                if (p.RawPreview != null)
                {
                    sb.AppendLine($"  Preview:");
                    foreach (var line in p.RawPreview.Split('\n'))
                        sb.AppendLine($"    {line}");
                }
            }
            sb.AppendLine();
        }

        await ShowDebugDialog(sb.ToString(), showClearCache: true);
    }

    private async Task ShowDebugDialog(string content, bool showClearCache = false)
    {
        var dialog = new ContentDialog
        {
            Title = "Lyrics Debug Info",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = content,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                },
                MaxHeight = 500,
            },
            CloseButtonText = "Close",
            XamlRoot = this.XamlRoot,
        };

        if (showClearCache && _lyricsVm != null)
        {
            dialog.PrimaryButtonText = "Clear Cache & Reload";
            dialog.PrimaryButtonClick += async (_, _) =>
            {
                var trackId = _lyricsVm.PlaybackState.CurrentTrackId;
                if (!string.IsNullOrEmpty(trackId))
                {
                    if (_lyricsService != null) await _lyricsService.ClearCacheForTrackAsync(trackId);
                    _lyricsVm.InvalidateTrack();
                }
            };
        }

        await dialog.ShowAsync();
    }
}
