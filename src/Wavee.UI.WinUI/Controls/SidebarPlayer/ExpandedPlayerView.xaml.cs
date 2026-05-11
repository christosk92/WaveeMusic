using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.Controls.Lyrics.Models;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.UI;
using Microsoft.UI;

namespace Wavee.UI.WinUI.Controls.SidebarPlayer;

/// <summary>
/// Floating-player "now playing" expanded layout — Apple-Music iPad style.
/// 2-column layout: <see cref="ExpandedNowPlayingLayout"/> on the left,
/// <see cref="Controls.RightPanel.RightPanelView"/> on the right with
/// <see cref="Controls.RightPanel.RightPanelView.IsTabHeaderVisible"/> off.
/// Right column has 3 states (None / Lyrics / Queue) toggled by the
/// bottom-right buttons; mode flips drive the inner panel's
/// <see cref="Controls.RightPanel.RightPanelView.SelectedMode"/>.
///
/// An atmospheric backdrop bleeds the album-art palette across the window
/// so the layout doesn't sit on bare Mica.
/// </summary>
public sealed partial class ExpandedPlayerView : UserControl
{
    private readonly PlayerBarViewModel _viewModel;
    private readonly LyricsViewModel? _lyricsVm;
    private readonly IActiveVideoSurfaceService _videoSurface;
    private DispatcherQueueTimer? _lyricsScrollResetTimer;
    private DispatcherQueueTimer? _lyricsRenderPulseTimer;
    private DispatcherQueueTimer? _lyricsTimelineSyncTimer;
    // Tracked storyboard — Stop()'d on Unloaded so the framework's animation
    // clock doesn't pin PlayerLayoutTransform after detach.
    private Storyboard? _playerLayoutTransformStoryboard;
    private bool _lyricsCanvasInitialized;
    private bool _lyricsConsumerActive;
    private bool _pendingLyricsLayoutRetry;
    private LyricsData? _appliedLyricsData;
    private SongInfo? _appliedSongInfo;
    private bool _lyricsCanvasDataCleared = true;
    private Color _lyricsCanvasClearColor = Colors.Transparent;
    private DispatcherQueueTimer? _ambientTintTimer;
    private DateTime _ambientTintStartUtc;
    private readonly Color[] _ambientTintFrom = new Color[4];
    private readonly Color[] _ambientTintTo = new Color[4];
    private DispatcherQueueTimer? _surfaceTintTimer;
    private DateTime _surfaceTintStartUtc;
    private Color _surfaceTintFrom = Colors.Transparent;
    private Color _surfaceTintTo = Colors.Transparent;
    private const int SurfaceTintFadeDurationMs = 700;
    private const int AmbientFadeDurationMs = 700;
    private bool _isVideoSurfaceEnabled;
    private bool _isVideoFocusActive;
    private bool _isVideoTheaterMode;
    private bool _autoFitRequestedForVideoFocus;
    private bool _compactNowPlayingVisible;
    private double _compactHeaderTop = 42;
    private double _compactHeaderSide = 44;
    private double _compactHeaderArtSize = 78;
    private const int LyricsTimelineSyncIntervalMs = 250;

    public ExpandedPlayerView()
    {
        _viewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        _lyricsVm = Ioc.Default.GetService<LyricsViewModel>();
        _videoSurface = Ioc.Default.GetRequiredService<IActiveVideoSurfaceService>();

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += OnActualThemeChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _videoSurface.ActiveSurfaceChanged += OnActiveVideoSurfaceChanged;
        PlayerLayout.TheaterModeChanged += OnPlayerLayoutTheaterModeChanged;
        PlayerLayout.FitVideoWindowRequested += OnPlayerLayoutFitVideoWindowRequested;
        PlayerLayout.QueueRequested += OnPlayerLayoutQueueRequested;
    }

    public PlayerBarViewModel ViewModel => _viewModel;

    public event EventHandler? FitVideoWindowRequested;

    internal Size GetPreferredVideoWindowSize(double currentWindowWidth)
        => PlayerLayout.GetPreferredVideoWindowSize(currentWindowWidth);

    /// <summary>Right-column content state. Default: <see cref="ExpandedPlayerContentMode.Lyrics"/>.</summary>
    public ExpandedPlayerContentMode Mode
    {
        get => (ExpandedPlayerContentMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode),
        typeof(ExpandedPlayerContentMode),
        typeof(ExpandedPlayerView),
        new PropertyMetadata(ExpandedPlayerContentMode.None, OnModeChanged));

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ExpandedPlayerView view) view.ApplyMode();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[mem] ExpandedPlayerView.OnLoaded");
        UpdateContentGridWidth();
        UpdateVideoFocusLayout();
        ApplyMode();
        SyncContentHostWidth();
        ApplyAmbientTint(_viewModel.AlbumArtColor);
        QueuePostMeasureLayoutPass();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[mem] ExpandedPlayerView.OnUnloaded");
        ReleaseHeavyResources();
        _videoSurface.ActiveSurfaceChanged -= OnActiveVideoSurfaceChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ActualThemeChanged -= OnActualThemeChanged;
    }

    internal void ReleaseHeavyResources()
    {
        System.Diagnostics.Debug.WriteLine("[mem] ExpandedPlayerView.ReleaseHeavyResources");
        PlayerLayout.SetVideoSurfaceEnabled(false);
        PlayerLayout.SetCanTakeVideoSurfaceFromVideoPage(false);
        PlayerLayout.SetVideoPresentationMode(false);
        PlayerLayout.SetTheaterMode(false);
        _isVideoSurfaceEnabled = false;
        _isVideoFocusActive = false;
        _isVideoTheaterMode = false;
        _autoFitRequestedForVideoFocus = false;
        UpdateLyricsConsumerActivity(active: false);
        TeardownLyricsCanvas();

        if (_lyricsScrollResetTimer != null)
        {
            _lyricsScrollResetTimer.Stop();
            _lyricsScrollResetTimer.Tick -= OnLyricsScrollResetTimerTick;
            _lyricsScrollResetTimer = null;
        }

        if (_lyricsRenderPulseTimer != null)
        {
            _lyricsRenderPulseTimer.Stop();
            _lyricsRenderPulseTimer.Tick -= OnLyricsRenderPulseTimerTick;
            _lyricsRenderPulseTimer = null;
        }

        if (_lyricsTimelineSyncTimer != null)
        {
            _lyricsTimelineSyncTimer.Stop();
            _lyricsTimelineSyncTimer.Tick -= OnLyricsTimelineSyncTimerTick;
            _lyricsTimelineSyncTimer = null;
        }

        _playerLayoutTransformStoryboard?.Stop();
        _playerLayoutTransformStoryboard = null;

        if (_surfaceTintTimer != null)
        {
            _surfaceTintTimer.Stop();
            _surfaceTintTimer.Tick -= OnSurfaceTintTick;
            _surfaceTintTimer = null;
        }

        if (_ambientTintTimer != null)
        {
            _ambientTintTimer.Stop();
            _ambientTintTimer.Tick -= OnAmbientTintTick;
            _ambientTintTimer = null;
        }

        if (ContentHost != null)
        {
            ContentHost.IsOpen = false;
            ContentHost.Visibility = Visibility.Collapsed;
        }
    }

    internal void SetVideoSurfaceEnabled(bool enabled)
    {
        _isVideoSurfaceEnabled = enabled;
        PlayerLayout.SetVideoSurfaceEnabled(enabled);
        UpdateVideoFocusLayout();
    }

    internal void SetCanTakeVideoSurfaceFromVideoPage(bool enabled)
    {
        PlayerLayout.SetCanTakeVideoSurfaceFromVideoPage(enabled);
        UpdateVideoFocusLayout();
    }

    // Below this window width the 2-column split stops being readable —
    // album art, controls and lyrics all fight for too few pixels. Below the
    // breakpoint we auto-collapse the right column (lyrics/queue) so the
    // player gets the full width; above it we restore the user's last mode.
    private const double NarrowWindowBreakpointPx = 760;
    private ExpandedPlayerContentMode _lastUserMode = ExpandedPlayerContentMode.None;
    private bool _autoCollapsedForNarrow;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        UpdateContentGridWidth();
        if (width < NarrowWindowBreakpointPx && Mode != ExpandedPlayerContentMode.None)
        {
            _lastUserMode = Mode;
            _autoCollapsedForNarrow = true;
            Mode = ExpandedPlayerContentMode.None;
        }
        else if (width >= NarrowWindowBreakpointPx && _autoCollapsedForNarrow)
        {
            _autoCollapsedForNarrow = false;
            Mode = _lastUserMode;
        }

        ApplyMode();
        SyncContentHostWidth();
        UpdateLyricsCanvasLayout();
        UpdateCompactHeaderLayout();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyAmbientTint(_viewModel.AlbumArtColor);
        ApplyLyricsPaletteForTheme();
        ApplyCanvasSurfaceTint(_viewModel.AlbumArtColor);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.AlbumArtColor))
        {
            ApplyAmbientTint(_viewModel.AlbumArtColor);
            ApplyCanvasSurfaceTint(_viewModel.AlbumArtColor);
        }
    }

    private void CompactTrackTitle_Click(object sender, RoutedEventArgs e)
    {
        var albumId = _viewModel.CurrentAlbumId;
        if (string.IsNullOrEmpty(albumId)) return;

        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = albumId,
            Title = _viewModel.TrackTitle ?? "Album",
            ImageUrl = _viewModel.AlbumArt
        };
        NavigationHelpers.OpenAlbum(param, param.Title);
    }

    private void OnActiveVideoSurfaceChanged(object? sender, MediaPlayer? surface)
        => DispatcherQueue?.TryEnqueue(UpdateVideoFocusLayout);

    private void OnPlayerLayoutTheaterModeChanged(object? sender, bool enabled)
    {
        _isVideoTheaterMode = enabled;
        ApplyMode();
    }

    private void OnPlayerLayoutFitVideoWindowRequested(object? sender, EventArgs e)
        => FitVideoWindowRequested?.Invoke(this, EventArgs.Empty);

    private void OnPlayerLayoutQueueRequested(object? sender, EventArgs e)
        => OpenQueueMode();

    private void OpenQueueMode()
    {
        if (_isVideoTheaterMode)
        {
            _isVideoTheaterMode = false;
            PlayerLayout.SetTheaterMode(false);
        }

        Mode = ExpandedPlayerContentMode.Queue;
        _lastUserMode = ExpandedPlayerContentMode.Queue;
        ApplyMode();
    }

    private void UpdateVideoFocusLayout()
    {
        var active = IsLoaded && _isVideoSurfaceEnabled && _videoSurface.HasActiveSurface;
        if (_isVideoFocusActive == active)
        {
            PlayerLayout.SetVideoPresentationMode(active);
            if (active)
                QueueFitVideoWindowForVideo();
            return;
        }

        _isVideoFocusActive = active;
        PlayerLayout.SetVideoPresentationMode(active);
        ApplyMode();

        if (active)
            QueueFitVideoWindowForVideo();
        else
            _autoFitRequestedForVideoFocus = false;
    }

    private void QueueFitVideoWindowForVideo()
    {
        if (_autoFitRequestedForVideoFocus)
            return;

        var dispatcher = DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
            return;

        _autoFitRequestedForVideoFocus = true;
        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded || !_isVideoFocusActive)
            {
                _autoFitRequestedForVideoFocus = false;
                return;
            }

            FitVideoWindowRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private bool IsLyricsModeActive =>
        Mode == ExpandedPlayerContentMode.Lyrics
        && Visibility == Visibility.Visible
        && _lyricsVm?.HasLyrics == true
        && _lyricsVm.CurrentLyrics != null;

    /// <summary>
    /// Drives <see cref="Controls.RightPanel.RightPanelView.PanelWidth"/> from
    /// the right column's actual width — the panel hard-sets its own
    /// <c>Width</c> from <c>PanelWidth</c>, so we feed the live value as the
    /// window resizes.
    /// </summary>
    private void SyncContentHostWidth()
    {
        if (Mode == ExpandedPlayerContentMode.None) return;
        if (ContentHost == null) return;
        var w = RightColumnContainer.ActualWidth;
        if (w <= 0) return;
        if (Math.Abs(ContentHost.PanelWidth - w) > 0.5)
            ContentHost.PanelWidth = w;
    }

    private void ApplyMode()
    {
        UpdateContentGridWidth();

        var mode = Mode;
        var videoFocusVisible = _isVideoFocusActive;
        var videoQueueVisible = videoFocusVisible && !_isVideoTheaterMode && mode == ExpandedPlayerContentMode.Queue;
        var rightVisible = videoQueueVisible || (!videoFocusVisible && mode != ExpandedPlayerContentMode.None);
        var lyricsVisible = !videoFocusVisible && mode == ExpandedPlayerContentMode.Lyrics;
        var queueVisible = rightVisible && mode == ExpandedPlayerContentMode.Queue;
        var focusVisible = !rightVisible && (videoFocusVisible || mode == ExpandedPlayerContentMode.None);
        var compactRightLayout = false;
        var twoColumnLayout = rightVisible && !compactRightLayout;

        UpdateCompactHeaderLayout();

        if (videoQueueVisible && twoColumnLayout)
        {
            LeftColumnDef.Width = new GridLength(1, GridUnitType.Star);
            RightColumnDef.Width = new GridLength(GetVideoQueuePanelWidth());
        }
        else if (twoColumnLayout && !compactRightLayout)
        {
            // The player has a concrete width; lyrics get every remaining pixel.
            // This removes the old right-column/read-width constraint.
            LeftColumnDef.Width = new GridLength(GetTwoColumnPlayerColumnWidth());
            RightColumnDef.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            LeftColumnDef.Width = compactRightLayout
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            RightColumnDef.Width = rightVisible || focusVisible
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
        }

        Grid.SetColumn(RightColumnContainer, compactRightLayout ? 0 : 1);
        Grid.SetColumnSpan(RightColumnContainer, compactRightLayout ? 2 : 1);
        RightColumnContainer.Margin = videoQueueVisible
            ? new Thickness(0, 56, 40, 0)
            : compactRightLayout
            ? new Thickness(_compactHeaderSide, queueVisible ? GetCompactPanelTopMargin() : 112, _compactHeaderSide, 104)
            : new Thickness(0, 56, 40, 40);
        RightColumnContainer.Visibility = rightVisible ? Visibility.Visible : Visibility.Collapsed;
        LyricsInteractionRegion.Visibility = lyricsVisible ? Visibility.Visible : Visibility.Collapsed;
        ExpandedLyricsAi.Visibility = lyricsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!lyricsVisible && FullscreenLyricsCanvas != null)
            FullscreenLyricsCanvas.ShowHoverActionIcon = false;
        UpdateLyricsConsumerActivity(lyricsVisible);

        if (lyricsVisible)
        {
            InitializeLyricsCanvas();
            if (FullscreenLyricsCanvas != null)
                FullscreenLyricsCanvas.Visibility = Visibility.Visible;
        }
        else if (_lyricsCanvasInitialized)
        {
            HideLyricsLayer();
            TeardownLyricsCanvas();
        }

        if (queueVisible)
        {
            EnsureContentHostRealized();
            if (ContentHost != null)
                ContentHost.Visibility = Visibility.Visible;
        }
        else if (ContentHost != null)
        {
            ContentHost.Visibility = Visibility.Collapsed;
        }

        // Focus mode spans both grid columns so the layout centers across the
        // whole window — without ColumnSpan, the * column allocation leaves
        // dead space on the right even when RightColumnDef is 0-width because
        // PlayerLayout's MaxWidth constrains it within column 0 only.
        Grid.SetColumnSpan(PlayerLayout, focusVisible || compactRightLayout ? 2 : 1);
        ApplyPlayerLayoutWidth(videoFocusVisible, focusVisible, twoColumnLayout);
        AnimateTransform(
            PlayerLayoutTransform,
            videoFocusVisible && !_isVideoTheaterMode ? 1.01 : 1.0,
            videoFocusVisible && !_isVideoTheaterMode ? -8 : 0,
            340);
        SetCompactNowPlayingVisible(compactRightLayout);

        ModeToggleHost.HorizontalAlignment = compactRightLayout ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ModeToggleHost.VerticalAlignment = compactRightLayout ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        ModeToggleHost.Margin = compactRightLayout
            ? new Thickness(_compactHeaderSide, GetCompactToggleTopMargin(), 0, 0)
            : new Thickness(0, 0, 32, 28);
        LyricsToggleButton.Visibility = videoFocusVisible ? Visibility.Collapsed : Visibility.Visible;
        ModeToggleHost.Visibility = videoFocusVisible && _isVideoTheaterMode
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (queueVisible)
        {
            if (ContentHost != null)
                ContentHost.SelectedMode = RightPanelMode.Queue;
            SyncContentHostWidth();
        }
        else if (!lyricsVisible)
        {
            ResumeLyricsSync();
        }

        UpdateLyricsCanvasLayout();
        ApplyCurrentLyricsState();
        UpdateLyricsRenderState();
        UpdateLyricsPlaceholders();

        LyricsToggleButton.IsChecked = mode == ExpandedPlayerContentMode.Lyrics;
        QueueToggleButton.IsChecked = mode == ExpandedPlayerContentMode.Queue;
    }

    private void UpdateCompactHeaderLayout()
    {
        var width = RootGrid.ActualWidth;
        if (width <= 0)
            width = 900;

        _compactHeaderSide = width >= 1280 ? 64 : width >= 960 ? 52 : 44;
        _compactHeaderTop = width >= 1280 ? 48 : 42;
        _compactHeaderArtSize = width >= 1440 ? 118 : width >= 1100 ? 104 : 86;

        CompactNowPlayingHeader.Margin = new Thickness(_compactHeaderSide, _compactHeaderTop, _compactHeaderSide, 0);
        CompactNowPlayingHeader.ColumnSpacing = width >= 1100 ? 18 : 14;
        CompactHeaderAlbumArtHost.Width = _compactHeaderArtSize;
        CompactHeaderAlbumArtHost.Height = _compactHeaderArtSize;
        CompactHeaderAlbumArtImage.DecodePixelWidth = (int)Math.Round(_compactHeaderArtSize * 2.25);
    }

    private double GetCompactToggleTopMargin()
        => _compactHeaderTop + _compactHeaderArtSize + 18;

    private double GetCompactPanelTopMargin()
        => GetCompactToggleTopMargin() + 52;

    private void QueuePostMeasureLayoutPass()
    {
        var dispatcher = DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
            return;

        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded)
                return;

            ApplyMode();
            SyncContentHostWidth();
            UpdateLyricsCanvasLayout();
        });
    }

    private double GetVideoQueuePanelWidth()
    {
        var width = RootGrid.ActualWidth;
        if (width >= 1500)
            return 430;
        if (width >= 1180)
            return 390;
        return 340;
    }

    private void ApplyPlayerLayoutWidth(bool videoFocusVisible, bool focusVisible, bool twoColumnLayout)
    {
        if (videoFocusVisible)
        {
            PlayerLayout.Width = double.NaN;
            PlayerLayout.MaxWidth = double.PositiveInfinity;
            PlayerLayout.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }

        var maxWidth = focusVisible ? GetFocusPlayerMaxWidth() : GetTwoColumnPlayerMaxWidth();
        var availableWidth = GetAvailablePlayerLayoutWidth(focusVisible, twoColumnLayout);

        PlayerLayout.MaxWidth = maxWidth;
        PlayerLayout.Width = availableWidth > 0
            ? Math.Floor(Math.Min(maxWidth, availableWidth))
            : double.NaN;
        // Focus mode (no Lyrics/Queue panel) spans both grid columns. Centre
        // the player there so the layout doesn't sit pinned to the left with
        // dead space on the right; in two-column mode it stays Left, anchored
        // next to the lyrics/queue panel.
        PlayerLayout.HorizontalAlignment = focusVisible
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Left;
    }

    private double GetAvailablePlayerLayoutWidth(bool focusVisible, bool twoColumnLayout)
    {
        var contentWidth = ContentGrid.Width;
        if (double.IsNaN(contentWidth) || contentWidth <= 0)
            contentWidth = ContentGrid.ActualWidth > 0 ? ContentGrid.ActualWidth : RootGrid.ActualWidth;

        if (contentWidth <= 0)
            return 0;

        if (focusVisible || !twoColumnLayout)
            return contentWidth;

        return Math.Min(GetTwoColumnPlayerColumnWidth(), contentWidth);
    }

    private double GetFocusPlayerMaxWidth()
    {
        var width = RootGrid.ActualWidth;
        if (width >= 2400) return 1260;
        if (width >= 2000) return 1180;
        if (width >= 1700) return 1080;
        if (width >= 1400) return 960;
        if (width >= 1150) return 860;
        return 700;
    }

    private double GetTwoColumnPlayerMaxWidth()
    {
        var width = RootGrid.ActualWidth;
        if (width >= 2400) return 1360;
        if (width >= 2000) return 1240;
        if (width >= 1700) return 1120;
        if (width >= 1400) return 980;
        if (width >= 1150) return 860;
        return 700;
    }

    private double GetTwoColumnPlayerColumnWidth()
    {
        var maxWidth = GetTwoColumnPlayerMaxWidth();
        var rootWidth = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : maxWidth;
        var rootHeight = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 820;

        // In wide-but-not-tall popouts, artwork is height-limited. Keep the
        // player column close to that visible footprint so lyrics start right
        // after the player instead of after an empty max-width column.
        var heightDrivenWidth = (rootHeight * 0.74) + 110;
        var widthDrivenLimit = rootWidth * 0.58;
        return Math.Clamp(
            Math.Min(heightDrivenWidth, widthDrivenLimit),
            Math.Min(620, maxWidth),
            maxWidth);
    }

    private void UpdateContentGridWidth()
    {
        if (ContentGrid == null)
            return;

        var width = RootGrid.ActualWidth;
        if (width <= 0)
        {
            ContentGrid.Width = double.NaN;
            return;
        }

        ContentGrid.Width = Math.Floor(width);
    }

    private void SetCompactNowPlayingVisible(bool showCompact)
    {
        if (_compactNowPlayingVisible == showCompact)
            return;

        _compactNowPlayingVisible = showCompact;
        PlayerLayout.Visibility = showCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactNowPlayingHeader.Visibility = showCompact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AnimateTransform(CompositeTransform transform, double scale, double translateY, int durationMs)
    {
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        _playerLayoutTransformStoryboard?.Stop();
        var storyboard = new Storyboard();

        AddAnimation(storyboard, transform, nameof(CompositeTransform.ScaleX), scale, duration, easing);
        AddAnimation(storyboard, transform, nameof(CompositeTransform.ScaleY), scale, duration, easing);
        AddAnimation(storyboard, transform, nameof(CompositeTransform.TranslateY), translateY, duration, easing);
        _playerLayoutTransformStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void AddAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double to,
        Duration duration,
        EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void UpdateLyricsConsumerActivity(bool active)
    {
        active = active && IsLoaded;
        if (_lyricsConsumerActive == active)
            return;

        _lyricsConsumerActive = active;
        _lyricsVm?.SetConsumerActive(this, active);
    }

    /// <summary>
    /// Toggles the "No lyrics" / "Loading lyrics" placeholder StackPanels in
    /// the right column. Only one (or neither) is visible at a time, and only
    /// while the user is actually looking at lyrics.
    /// </summary>
    private void UpdateLyricsPlaceholders()
    {
        if (_lyricsVm == null)
        {
            NoLyricsPlaceholder.Visibility = Visibility.Collapsed;
            LoadingLyricsPlaceholder.Visibility = Visibility.Collapsed;
            return;
        }

        var inLyricsMode = Mode == ExpandedPlayerContentMode.Lyrics;
        var isLoading = _lyricsVm.IsLoading;
        var hasLyrics = _lyricsVm.HasLyrics && _lyricsVm.CurrentLyrics != null;

        LoadingLyricsPlaceholder.Visibility = (inLyricsMode && isLoading)
            ? Visibility.Visible
            : Visibility.Collapsed;
        NoLyricsPlaceholder.Visibility = (inLyricsMode && !isLoading && !hasLyrics)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void LyricsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        Mode = Mode == ExpandedPlayerContentMode.Lyrics
            ? ExpandedPlayerContentMode.None
            : ExpandedPlayerContentMode.Lyrics;
    }

    private void QueueToggleButton_Click(object sender, RoutedEventArgs e)
        => ToggleQueueMode();

    private void ToggleQueueMode()
    {
        Mode = Mode == ExpandedPlayerContentMode.Queue
            ? ExpandedPlayerContentMode.None
            : ExpandedPlayerContentMode.Queue;
    }

    // ── Ambient backdrop tint ────────────────────────────────────────────
    //
    // Builds a vertical gradient from the album-art dominant color, fading
    // to transparent so Mica reads through near the title bar and bottom.
    // Light mode uses a softer, blended tint so foreground text stays
    // readable; dark mode keeps a saturated upper band for atmosphere.

    private bool EnsureLyricsCanvasRealized()
    {
        if (FullscreenLyricsCanvas != null)
            return true;

        _ = FindName(nameof(FullscreenLyricsCanvas));
        return FullscreenLyricsCanvas != null;
    }

    private bool EnsureContentHostRealized()
    {
        if (ContentHost != null)
            return true;

        _ = FindName(nameof(ContentHost));
        if (ContentHost == null)
            return false;

        ContentHost.MaxWidth = double.PositiveInfinity;
        return true;
    }

    private void InitializeLyricsCanvas()
    {
        if (_lyricsCanvasInitialized || _lyricsVm == null) return;
        if (!EnsureLyricsCanvasRealized()) return;

        _lyricsCanvasInitialized = true;

        FullscreenLyricsCanvas.LyricsWindowStatus = _lyricsVm.WindowStatus;
        FullscreenLyricsCanvas.Visibility = Visibility.Visible;
        ApplyCanvasSurfaceTint(_viewModel.AlbumArtColor);
        FullscreenLyricsCanvas.SeekRequested += OnLyricsSeekRequested;
        _lyricsVm.PropertyChanged += OnLyricsVmPropertyChanged;
        _lyricsVm.PlaybackState.PropertyChanged += OnLyricsPlaybackStateChanged;

        WireFullscreenLyricsAiAffordance();

        ApplyLyricsPaletteForTheme();
        ApplyCurrentLyricsState();
        UpdateLyricsRenderState();
    }

    private void WireFullscreenLyricsAiAffordance()
    {
        if (FullscreenLyricsCanvas == null || ExpandedLyricsAi?.ViewModel is null) return;

        ExpandedLyricsAi.ViewModel.PropertyChanged -= OnExpandedLyricsAiVmPropertyChanged;
        ExpandedLyricsAi.ViewModel.PropertyChanged += OnExpandedLyricsAiVmPropertyChanged;

        if (Resources.TryGetValue("AiAccentSolidBrush", out var resourceObj)
            || Application.Current.Resources.TryGetValue("AiAccentSolidBrush", out resourceObj))
        {
            if (resourceObj is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
                FullscreenLyricsCanvas.HoverActionIconAccentColor = brush.Color;
        }

        FullscreenLyricsCanvas.ShowHoverActionIcon = ExpandedLyricsAi.ViewModel.IsExplainAvailable;
    }

    private void OnExpandedLyricsAiVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsAiPanelViewModel.IsExplainAvailable)
            && FullscreenLyricsCanvas != null
            && ExpandedLyricsAi?.ViewModel is { } vm)
        {
            FullscreenLyricsCanvas.ShowHoverActionIcon = vm.IsExplainAvailable;
        }
    }

    private void TeardownLyricsCanvas()
    {
        if (!_lyricsCanvasInitialized) return;

        _lyricsScrollResetTimer?.Stop();
        _lyricsRenderPulseTimer?.Stop();
        _lyricsTimelineSyncTimer?.Stop();
        _surfaceTintTimer?.Stop();
        _ambientTintTimer?.Stop();

        if (_lyricsVm != null)
        {
            _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
            _lyricsVm.PlaybackState.PropertyChanged -= OnLyricsPlaybackStateChanged;
        }

        if (ExpandedLyricsAi?.ViewModel is { } aiVm)
            aiVm.PropertyChanged -= OnExpandedLyricsAiVmPropertyChanged;

        if (FullscreenLyricsCanvas != null)
        {
            FullscreenLyricsCanvas.SeekRequested -= OnLyricsSeekRequested;
            FullscreenLyricsCanvas.ShowHoverActionIcon = false;
            FullscreenLyricsCanvas.SetIsPlaying(false);
            FullscreenLyricsCanvas.SetRenderingActive(false);
            if (!_lyricsCanvasDataCleared)
                FullscreenLyricsCanvas.SetLyricsData(null);
            FullscreenLyricsCanvas.Visibility = Visibility.Collapsed;
        }
        _appliedLyricsData = null;
        _appliedSongInfo = null;
        _lyricsCanvasDataCleared = true;

        _lyricsCanvasInitialized = false;
    }

    private void OnLyricsVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (FullscreenLyricsCanvas == null && e.PropertyName != nameof(LyricsViewModel.IsLoading))
            return;

        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.CurrentLyrics):
            case nameof(LyricsViewModel.CurrentSongInfo):
            case nameof(LyricsViewModel.HasLyrics):
            case nameof(LyricsViewModel.IsLoading):
                ApplyCurrentLyricsState();
                UpdateLyricsPlaceholders();
                break;
            case nameof(LyricsViewModel.CurrentPalette):
                if (_lyricsVm?.CurrentPalette is { } palette)
                {
                    FullscreenLyricsCanvas.SetNowPlayingPalette(palette);
                    ApplyCanvasSurfaceTint(_viewModel.AlbumArtColor);
                }
                break;
        }
    }

    private void OnLyricsPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.IsPlaying))
        {
            SyncLyricsCanvasPosition();
            UpdateLyricsRenderState();
        }
        else if (e.PropertyName is nameof(IPlaybackStateService.Position))
        {
            SyncLyricsCanvasPosition();
        }
    }

    private void ApplyCurrentLyricsState()
    {
        if (!_lyricsCanvasInitialized || _lyricsVm == null || FullscreenLyricsCanvas == null) return;

        var showCanvas = IsLyricsModeActive;
        FullscreenLyricsCanvas.Visibility = Visibility.Visible;
        FullscreenLyricsCanvas.LyricsOpacity = showCanvas ? 1 : 0;

        if (showCanvas)
        {
            UpdateLyricsCanvasLayout();
            var lyrics = _lyricsVm.CurrentLyrics;
            if (!ReferenceEquals(_appliedLyricsData, lyrics))
            {
                FullscreenLyricsCanvas.SetLyricsData(lyrics);
                _appliedLyricsData = lyrics;
                _lyricsCanvasDataCleared = false;
            }

            var songInfo = _lyricsVm.CurrentSongInfo;
            if (!ReferenceEquals(_appliedSongInfo, songInfo))
            {
                FullscreenLyricsCanvas.SetSongInfo(songInfo);
                _appliedSongInfo = songInfo;
            }

            FullscreenLyricsCanvas.SetIsPlaying(_lyricsVm.PlaybackState.IsPlaying);
            SyncLyricsCanvasPosition();
        }
        else
        {
            HideLyricsLayer();
            FullscreenLyricsCanvas.SetIsPlaying(false);
        }

        UpdateLyricsRenderState();
    }

    private void UpdateLyricsRenderState()
    {
        if (!_lyricsCanvasInitialized || _lyricsVm == null || FullscreenLyricsCanvas == null)
            return;

        var canRender = IsLyricsModeActive;
        var isInteracting = FullscreenLyricsCanvas.IsMouseInLyricsArea
                            || FullscreenLyricsCanvas.IsMousePressing
                            || FullscreenLyricsCanvas.IsMouseScrolling;
        var shouldRender = canRender && (_lyricsVm.PlaybackState.IsPlaying || isInteracting);

        FullscreenLyricsCanvas.SetRenderingActive(shouldRender);
        FullscreenLyricsCanvas.SetIsPlaying(canRender && _lyricsVm.PlaybackState.IsPlaying);
        UpdateLyricsTimelineSyncTimer(canRender && _lyricsVm.PlaybackState.IsPlaying);
    }

    private void SyncLyricsCanvasPosition()
    {
        if (!_lyricsCanvasInitialized || _lyricsVm == null || FullscreenLyricsCanvas == null || !IsLyricsModeActive)
            return;

        FullscreenLyricsCanvas.SetPosition(_lyricsVm.GetInterpolatedPosition());
    }

    private void UpdateLyricsTimelineSyncTimer(bool shouldRun)
    {
        if (!shouldRun || DispatcherQueue == null)
        {
            _lyricsTimelineSyncTimer?.Stop();
            return;
        }

        _lyricsTimelineSyncTimer ??= CreateLyricsTimelineSyncTimer();
        if (!_lyricsTimelineSyncTimer.IsRunning)
            _lyricsTimelineSyncTimer.Start();
    }

    private DispatcherQueueTimer CreateLyricsTimelineSyncTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(LyricsTimelineSyncIntervalMs);
        timer.Tick += OnLyricsTimelineSyncTimerTick;
        return timer;
    }

    private void OnLyricsTimelineSyncTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_lyricsCanvasInitialized
            || _lyricsVm == null
            || FullscreenLyricsCanvas == null
            || !IsLyricsModeActive
            || !_lyricsVm.PlaybackState.IsPlaying)
        {
            sender.Stop();
            return;
        }

        SyncLyricsCanvasPosition();
    }

    private void OnLyricsSeekRequested(object? sender, TimeSpan position)
    {
        if (_lyricsCanvasInitialized && FullscreenLyricsCanvas != null && IsLyricsModeActive)
            FullscreenLyricsCanvas.SetPosition(position);

        _lyricsVm?.PlaybackState.Seek(position.TotalMilliseconds);
    }

    private void ApplyLyricsPaletteForTheme()
    {
        if (_lyricsVm == null || FullscreenLyricsCanvas == null) return;

        var isDark = ActualTheme != ElementTheme.Light;
        // Active line gets pure black/white for emphasis; off and upcoming lines
        // get a softer gray so the queue reads de-emphasized. Mirrors RightPanelView.
        var activeFg = isDark ? Colors.White : Colors.Black;
        var offFg = isDark
            ? Color.FromArgb(0xFF, 0xC0, 0xC0, 0xC0)
            : Color.FromArgb(0xFF, 0x55, 0x55, 0x55);
        var palette = _lyricsVm.WindowStatus.WindowPalette;
        palette.NonCurrentLineFillColor = offFg;
        palette.PlayedCurrentLineFillColor = activeFg;
        palette.UnplayedCurrentLineFillColor = offFg;
        palette.ThemeType = isDark ? ElementTheme.Dark : ElementTheme.Light;
        FullscreenLyricsCanvas.SetNowPlayingPalette(palette);
        ApplyCanvasSurfaceTint(_viewModel.AlbumArtColor);
    }

    private void LyricsInteractionRegion_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLyricsCanvasLayout();
    }

    private void UpdateLyricsCanvasLayout()
    {
        if (!_lyricsCanvasInitialized || FullscreenLyricsCanvas == null) return;

        var rootW = RootGrid.ActualWidth;
        var rootH = RootGrid.ActualHeight;
        var lyricsW = LyricsInteractionRegion.ActualWidth;
        var lyricsH = LyricsInteractionRegion.ActualHeight;

        if (rootW <= 0 || rootH <= 0 || lyricsW <= 0 || lyricsH <= 0)
        {
            ScheduleLyricsCanvasLayoutRetry();
            return;
        }

        _pendingLyricsLayoutRetry = false;

        var transform = LyricsInteractionRegion.TransformToVisual(RootGrid);
        var origin = transform.TransformPoint(new Point(0, 0));

        FullscreenLyricsCanvas.LyricsStartX = origin.X;
        FullscreenLyricsCanvas.LyricsStartY = origin.Y;
        FullscreenLyricsCanvas.LyricsWidth = lyricsW;
        FullscreenLyricsCanvas.LyricsHeight = lyricsH;
        FullscreenLyricsCanvas.LyricsOpacity = Mode == ExpandedPlayerContentMode.Lyrics ? 1 : 0;
        FullscreenLyricsCanvas.AlbumArtRect = Rect.Empty;
    }

    private void ScheduleLyricsCanvasLayoutRetry()
    {
        if (_pendingLyricsLayoutRetry || DispatcherQueue == null)
            return;

        _pendingLyricsLayoutRetry = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pendingLyricsLayoutRetry = false;
            if (!IsLoaded || Mode != ExpandedPlayerContentMode.Lyrics)
                return;

            UpdateLyricsCanvasLayout();
            if (FullscreenLyricsCanvas != null && FullscreenLyricsCanvas.LyricsWidth > 0)
                ApplyCurrentLyricsState();
        });
    }

    private void LyricsInteractionRegion_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (FullscreenLyricsCanvas == null) return;
        FullscreenLyricsCanvas.IsMouseInLyricsArea = true;
        UpdateLyricsRenderState();
    }

    private void LyricsInteractionRegion_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (FullscreenLyricsCanvas == null) return;
        FullscreenLyricsCanvas.IsMouseInLyricsArea = false;
        UpdateLyricsRenderState();
    }

    private void LyricsInteractionRegion_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (FullscreenLyricsCanvas == null) return;
        var point = e.GetCurrentPoint(LyricsInteractionRegion).Position;
        FullscreenLyricsCanvas.MousePosition = point;
    }

    private void LyricsInteractionRegion_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FullscreenLyricsCanvas == null) return;
        FullscreenLyricsCanvas.IsMousePressing = true;
        UpdateLyricsRenderState();
    }

    private void LyricsInteractionRegion_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (FullscreenLyricsCanvas == null) return;
        FullscreenLyricsCanvas.IsMousePressing = false;

        // Suppress seek-to-line when the click landed on the in-canvas hover icon — the
        // canvas raises HoverActionInvoked instead, which the AI panel handles.
        var point = e.GetCurrentPoint(LyricsInteractionRegion).Position;
        if (FullscreenLyricsCanvas.TryInvokeHoverActionAt(point))
        {
            UpdateLyricsRenderState();
            e.Handled = true;
            return;
        }

        FullscreenLyricsCanvas.FireSeekIfHovering();
        UpdateLyricsRenderState();
    }

    private void LyricsInteractionRegion_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (FullscreenLyricsCanvas == null) return;
        FullscreenLyricsCanvas.IsMouseScrolling = true;
        UpdateLyricsRenderState();

        var point = e.GetCurrentPoint(LyricsInteractionRegion);
        var delta = point.Properties.MouseWheelDelta;
        var value = FullscreenLyricsCanvas.MouseScrollOffset + delta;

        if (value > 0)
            value = Math.Min(-FullscreenLyricsCanvas.CurrentCanvasYScroll, value);
        else
            value = Math.Max(
                -FullscreenLyricsCanvas.CurrentCanvasYScroll - FullscreenLyricsCanvas.ActualLyricsHeight,
                value);

        FullscreenLyricsCanvas.MouseScrollOffset = value;

        _lyricsScrollResetTimer ??= CreateLyricsScrollResetTimer();
        _lyricsScrollResetTimer.Stop();
        _lyricsScrollResetTimer.Interval = TimeSpan.FromSeconds(3);
        _lyricsScrollResetTimer.Start();

        e.Handled = true;
    }

    private async void FullscreenLyricsCanvas_HoverActionInvoked(object? sender, int lineIndex)
    {
        if (lineIndex < 0 || ExpandedLyricsAi?.ViewModel is null)
            return;

        await ExpandedLyricsAi.ViewModel.ExplainLineAtIndexAsync(lineIndex);
    }

    private DispatcherQueueTimer CreateLyricsScrollResetTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += OnLyricsScrollResetTimerTick;
        return timer;
    }

    private void OnLyricsScrollResetTimerTick(DispatcherQueueTimer sender, object args)
    {
        ResumeLyricsSync();
    }

    private void ResumeLyricsSync()
    {
        if (FullscreenLyricsCanvas == null) return;
        FullscreenLyricsCanvas.MouseScrollOffset = 0;
        FullscreenLyricsCanvas.IsMouseScrolling = false;
        UpdateLyricsRenderState();
    }

    private void HideLyricsLayer()
    {
        if (FullscreenLyricsCanvas == null) return;
        FullscreenLyricsCanvas.LyricsOpacity = 0;
        FullscreenLyricsCanvas.LyricsStartX = 0;
        FullscreenLyricsCanvas.LyricsStartY = 0;
        FullscreenLyricsCanvas.LyricsWidth = 0;
        FullscreenLyricsCanvas.LyricsHeight = 0;
        FullscreenLyricsCanvas.MouseScrollOffset = 0;
        FullscreenLyricsCanvas.IsMouseScrolling = false;
        FullscreenLyricsCanvas.IsMousePressing = false;
        FullscreenLyricsCanvas.IsMouseInLyricsArea = false;
        FullscreenLyricsCanvas.ShowHoverActionIcon = false;

        // Force one render frame with no lyric data so a held-syllable glow /
        // scaled line cannot survive past the mode change. The clear color
        // (album palette) stays — only the text layer is dropped.
        if (!_lyricsCanvasDataCleared)
        {
            FullscreenLyricsCanvas.SetLyricsData(null);
            _appliedLyricsData = null;
            _appliedSongInfo = null;
            _lyricsCanvasDataCleared = true;
        }

        PulseLyricsCanvasRender();
    }

    private void ApplyCanvasSurfaceTint(string? hexColor)
    {
        if (!_lyricsCanvasInitialized || FullscreenLyricsCanvas == null) return;

        var target = BuildSurfaceColor(hexColor);
        if (_lyricsCanvasClearColor.Equals(target))
        {
            PulseLyricsCanvasRender();
            return;
        }

        // Capture starting color from current state — picks up an in-flight
        // value if a previous fade is still running, so back-to-back track
        // changes don't snap mid-animation.
        _surfaceTintFrom = _lyricsCanvasClearColor;
        _surfaceTintTo = target;
        _surfaceTintStartUtc = DateTime.UtcNow;

        // The Win2D canvas only renders when active. Keep it on for the whole
        // fade so each timer tick's SetClearColor actually paints.
        FullscreenLyricsCanvas.SetRenderingActive(true);

        _surfaceTintTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _surfaceTintTimer.Tick -= OnSurfaceTintTick;
        _surfaceTintTimer.Tick += OnSurfaceTintTick;
        _surfaceTintTimer.Interval = TimeSpan.FromMilliseconds(16);
        _surfaceTintTimer.Stop();
        _surfaceTintTimer.Start();
    }

    private void OnSurfaceTintTick(DispatcherQueueTimer sender, object args)
    {
        if (!_lyricsCanvasInitialized || FullscreenLyricsCanvas == null)
        {
            sender.Stop();
            return;
        }

        var elapsed = (DateTime.UtcNow - _surfaceTintStartUtc).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / SurfaceTintFadeDurationMs, 0.0, 1.0);
        var t = EaseInOut(progress);

        var c = LerpColor(_surfaceTintFrom, _surfaceTintTo, t);
        _lyricsCanvasClearColor = c;
        FullscreenLyricsCanvas.SetClearColor(c);

        if (progress >= 1.0)
        {
            sender.Stop();
            UpdateLyricsRenderState();
        }
    }

    private static double EaseInOut(double t)
        => t < 0.5
            ? 2 * t * t
            : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    private static Color LerpColor(Color a, Color b, double t)
        => Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    private void PulseLyricsCanvasRender()
    {
        if (!_lyricsCanvasInitialized || FullscreenLyricsCanvas == null || DispatcherQueue == null)
            return;

        FullscreenLyricsCanvas.SetRenderingActive(true);

        _lyricsRenderPulseTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _lyricsRenderPulseTimer.IsRepeating = false;
        _lyricsRenderPulseTimer.Interval = TimeSpan.FromMilliseconds(180);
        _lyricsRenderPulseTimer.Tick -= OnLyricsRenderPulseTimerTick;
        _lyricsRenderPulseTimer.Tick += OnLyricsRenderPulseTimerTick;
        _lyricsRenderPulseTimer.Stop();
        _lyricsRenderPulseTimer.Start();
    }

    private void OnLyricsRenderPulseTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        UpdateLyricsRenderState();
    }

    private Color BuildSurfaceColor(string? hexColor)
    {
        if (string.IsNullOrEmpty(hexColor) || hexColor.TrimStart('#').Length != 6)
        {
            if (_lyricsVm?.CurrentPalette is { } palette)
            {
                var underlay = palette.UnderlayColor;
                if (underlay.A > 0)
                    return underlay;
            }

            return ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 18, 18, 21)
                : Color.FromArgb(255, 224, 238, 247);
        }

        try
        {
            var hex = hexColor.TrimStart('#');
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);

            if (ActualTheme == ElementTheme.Dark)
                return Color.FromArgb(255, (byte)(r * 0.30f), (byte)(g * 0.30f), (byte)(b * 0.30f));

            const float blend = 0.72f;
            return Color.FromArgb(
                255,
                (byte)(r * (1 - blend) + 255 * blend),
                (byte)(g * (1 - blend) + 255 * blend),
                (byte)(b * (1 - blend) + 255 * blend));
        }
        catch
        {
            return ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 18, 18, 21)
                : Color.FromArgb(255, 224, 238, 247);
        }
    }

    private void ApplyAmbientTint(string? hexColor)
    {
        var (top, upperMid, lowerMid, bottom) = ComputeAmbientStops(hexColor);
        AnimateAmbient(top, upperMid, lowerMid, bottom);
    }

    private (Color top, Color upperMid, Color lowerMid, Color bottom) ComputeAmbientStops(string? hexColor)
    {
        if (string.IsNullOrEmpty(hexColor) || hexColor!.TrimStart('#').Length != 6)
        {
            var baseColor = ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 18, 18, 21)
                : Color.FromArgb(255, 244, 246, 248);
            return (baseColor, baseColor, baseColor, baseColor);
        }

        try
        {
            var hex = hexColor.TrimStart('#');
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);

            byte tr, tg, tb;
            byte topAlpha;
            byte midAlpha;
            byte lowerMidAlpha;

            if (ActualTheme == ElementTheme.Dark)
            {
                tr = (byte)(r * 0.34f);
                tg = (byte)(g * 0.34f);
                tb = (byte)(b * 0.34f);
                topAlpha = 238;
                midAlpha = 230;
                lowerMidAlpha = 220;
            }
            else
            {
                const float blend = 0.72f;
                tr = (byte)(r * (1 - blend) + 255 * blend);
                tg = (byte)(g * (1 - blend) + 255 * blend);
                tb = (byte)(b * (1 - blend) + 255 * blend);
                topAlpha = 255;
                midAlpha = 246;
                lowerMidAlpha = 236;
            }

            var bottom = ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 10, 10, 12)
                : Color.FromArgb(255, 244, 246, 248);

            return (
                Color.FromArgb(topAlpha, tr, tg, tb),
                Color.FromArgb(midAlpha, tr, tg, tb),
                Color.FromArgb(lowerMidAlpha, tr, tg, tb),
                bottom);
        }
        catch
        {
            var baseColor = ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 18, 18, 21)
                : Color.FromArgb(255, 244, 246, 248);
            return (baseColor, baseColor, baseColor, baseColor);
        }
    }

    private void AnimateAmbient(Color top, Color upperMid, Color lowerMid, Color bottom)
    {
        // Snapshot live colors so an in-flight fade interrupted by a fast
        // track skip continues from the on-screen blend.
        _ambientTintFrom[0] = AmbientStopTop.Color;
        _ambientTintFrom[1] = AmbientStopUpperMid.Color;
        _ambientTintFrom[2] = AmbientStopLowerMid.Color;
        _ambientTintFrom[3] = AmbientStopBottom.Color;
        _ambientTintTo[0] = top;
        _ambientTintTo[1] = upperMid;
        _ambientTintTo[2] = lowerMid;
        _ambientTintTo[3] = bottom;
        _ambientTintStartUtc = DateTime.UtcNow;

        _ambientTintTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _ambientTintTimer.Tick -= OnAmbientTintTick;
        _ambientTintTimer.Tick += OnAmbientTintTick;
        _ambientTintTimer.Interval = TimeSpan.FromMilliseconds(16);
        _ambientTintTimer.Stop();
        _ambientTintTimer.Start();
    }

    private void OnAmbientTintTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = (DateTime.UtcNow - _ambientTintStartUtc).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / AmbientFadeDurationMs, 0.0, 1.0);
        var t = EaseInOut(progress);

        AmbientStopTop.Color = LerpColor(_ambientTintFrom[0], _ambientTintTo[0], t);
        AmbientStopUpperMid.Color = LerpColor(_ambientTintFrom[1], _ambientTintTo[1], t);
        AmbientStopLowerMid.Color = LerpColor(_ambientTintFrom[2], _ambientTintTo[2], t);
        AmbientStopBottom.Color = LerpColor(_ambientTintFrom[3], _ambientTintTo[3], t);

        if (progress >= 1.0)
        {
            sender.Stop();
            // Snap to exact targets to defeat float drift.
            AmbientStopTop.Color = _ambientTintTo[0];
            AmbientStopUpperMid.Color = _ambientTintTo[1];
            AmbientStopLowerMid.Color = _ambientTintTo[2];
            AmbientStopBottom.Color = _ambientTintTo[3];
        }
    }
}
