using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using System.Numerics;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
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

    // Tracks whether the deferred LyricsContent / DetailsContent subtrees have been
    // materialized into the visual tree yet. Both use x:Load="False" in XAML and are
    // loaded on demand when their tab is first selected. Once loaded, they stay loaded.
    private bool _lyricsTreeLoaded;
    private bool _detailsTreeLoaded;

    // Re-entrancy guard for Segmented SelectionChanged → SelectedMode sync.
    private bool _suppressTabSelectionChanged;

    // Details integration
    private TrackDetailsViewModel? _detailsVm;

    // Lyrics integration
    private LyricsViewModel? _lyricsVm;
    private DispatcherQueueTimer? _positionTimer;
    private DispatcherQueueTimer? _scrollResetTimer;
    private double _lastCanvasPositionMs = -1;
    private bool _lyricsInitialized;
    private readonly ThemeColorService? _themeColors;
    private readonly ILyricsService? _lyricsService;
    private readonly ISettingsService? _settingsService;
    private bool _themeColorsSubscribed;

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
        // RegisterDetailsWheelHandler is deferred: the DetailsContent subtree is
        // x:Load="False" and doesn't exist until the Details tab is first opened.
        // See EnsureDetailsTreeLoaded().
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
        UnregisterDetailsWheelHandler();
        TeardownLyrics();

        if (_detailsVm != null && _detailsSubscribed)
        {
            _detailsVm.PropertyChanged -= OnDetailsVmPropertyChanged;
            _detailsSubscribed = false;
        }
        TeardownCanvasBackground();
        TeardownBlurredAlbumArt();
        _canvasDevice?.Dispose();
        _canvasDevice = null;
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

        // Update blurred album art when album art changes
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentAlbumArtLarge)
                           or nameof(IPlaybackStateService.CurrentAlbumArt))
        {
            if (SelectedMode == RightPanelMode.Details
                && _activeBackgroundMode == DetailsBackgroundMode.BlurredAlbumArt)
            {
                SetupBlurredAlbumArt();
            }
        }
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
                // Also refresh Details panel lyrics snippet + canvas overlay
                // (lyrics load async, so Details may have missed initial state)
                if (SelectedMode == RightPanelMode.Details && _detailsVm?.HasData == true)
                    RefreshDetailsLyrics();
                break;
            case nameof(LyricsViewModel.CurrentPalette):
                if (_lyricsVm?.CurrentPalette is { } palette)
                    NowPlayingCanvas.SetNowPlayingPalette(palette);
                break;
        }
    }

    private void RefreshDetailsLyrics()
    {
        // Details subtree (containing DetailsLyricsSnippet) is x:Load'd — safe no-op if not materialized.
        if (DetailsContent == null)
        {
            UpdateCanvasLyricsVisibility();
            return;
        }

        var showLyricsSnippet = _lyricsVm?.HasLyrics == true && _lyricsVm.CurrentLyrics != null;
        DetailsLyricsSnippet.Visibility = showLyricsSnippet ? Visibility.Visible : Visibility.Collapsed;
        if (showLyricsSnippet)
            SetupDetailsLyricsSnippet();
        else
            TeardownDetailsLyricsSnippet();

        UpdateCanvasLyricsVisibility();
    }

    private void ApplyCurrentLyricsState()
    {
        if (_lyricsVm == null) return;

        var isLyricsMode = SelectedMode == RightPanelMode.Lyrics;
        var hasLyrics = _lyricsVm.HasLyrics && _lyricsVm.CurrentLyrics != null;
        var showLoadingShimmer = isLyricsMode && _lyricsVm.IsLoading && !hasLyrics;

        var showNoLyrics = isLyricsMode
                           && !_lyricsVm.IsLoading
                           && !_lyricsVm.HasLyrics
                           && !string.IsNullOrEmpty(_lyricsVm.PlaybackState.CurrentTrackId);

        // Canvas only shows real lyrics; loading now uses shimmer in XAML.
        var showCanvas = isLyricsMode && hasLyrics;

        // NowPlayingCanvas is parent-level, always safe.
        NowPlayingCanvas.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;

        if (!showCanvas)
        {
            NowPlayingCanvas.MouseScrollOffset = 0;
            NowPlayingCanvas.IsMouseScrolling = false;
        }

        // The elements below live inside LyricsContent, which is x:Load="False" until
        // the Lyrics tab is opened for the first time. Skip subtree updates until then.
        if (LyricsContent != null)
        {
            // Prefer shimmer placeholder instead of ProgressRing while loading.
            LyricsLoadingRing.Visibility = Visibility.Collapsed;
            LyricsLoadingShimmer.Visibility = showLoadingShimmer ? Visibility.Visible : Visibility.Collapsed;
            NoLyricsText.Visibility = showNoLyrics ? Visibility.Visible : Visibility.Collapsed;
            LyricsInteractionOverlay.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;
            if (!showCanvas)
                LyricsSyncButton.Visibility = Visibility.Collapsed;
#if DEBUG
            LyricsDebugButton.Visibility = Visibility.Visible;
#endif
        }

#if DEBUG
        System.Diagnostics.Debug.WriteLine(
            $"[RightPanel] ApplyCurrentLyricsState mode={SelectedMode} " +
            $"hasLyrics={_lyricsVm.HasLyrics} isLoading={_lyricsVm.IsLoading} " +
            $"lineCount={_lyricsVm.CurrentLyrics?.LyricsLines.Count ?? 0} " +
            $"showCanvas={showCanvas} showNoLyrics={showNoLyrics}");
#endif

        // Push fresh XAML layout dimensions into the engine *before* handing it data.
        // Track changes do not fire SizeChanged on LyricsContent, so without this push
        // the engine can relayout a stale 0×0 cache and render nothing until the user
        // resizes the panel. See dazzling-foraging-stroustrup.md Fix 1.
        if (showCanvas) UpdateCanvasLayout();

        if (hasLyrics)
        {
            NowPlayingCanvas.SetLyricsData(_lyricsVm.CurrentLyrics!);
            NowPlayingCanvas.SetSongInfo(_lyricsVm.CurrentSongInfo);
            NowPlayingCanvas.SetIsPlaying(_lyricsVm.PlaybackState.IsPlaying);
            var position = _lyricsVm.GetInterpolatedPosition();
            _lastCanvasPositionMs = position.TotalMilliseconds;
            NowPlayingCanvas.SetPosition(position);
        }
        else
        {
            // No lyrics and not loading — clear stale engine data so a subsequent
            // successful load doesn't accidentally composite on top of an old frame.
            NowPlayingCanvas.SetLyricsData(null);
            NowPlayingCanvas.SetIsPlaying(false);
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
                        && _lyricsVm.HasLyrics
                        && _lyricsVm.CurrentLyrics != null;

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

        var position = _lyricsVm.GetInterpolatedPosition();
        var positionMs = position.TotalMilliseconds;

        // Skip tiny deltas to avoid unnecessary DP churn every tick.
        if (_lastCanvasPositionMs >= 0 && Math.Abs(positionMs - _lastCanvasPositionMs) < 35)
            return;

        _lastCanvasPositionMs = positionMs;
        NowPlayingCanvas.SetPosition(position);
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
        if (LyricsSyncButton != null)
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

        // If layout hasn't measured the grid yet (common when we're called from
        // ApplyCurrentLyricsState right as the panel/tab becomes visible), force
        // a measure+arrange pass right now so we can read actual sizes synchronously.
        // Without this, the call bails and the canvas never receives valid lyrics dimensions,
        // and SetLyricsData ends up flowing through to the engine with a zero rect.
        if (w <= 0 || h <= 0)
        {
            RootGrid.UpdateLayout();
            w = RootGrid.ActualWidth;
            h = RootGrid.ActualHeight;
        }

        // Final fallback: use the control's explicit Width if layout still hasn't resolved.
        // RightPanelView has `Width = PanelWidth` hard-coded in the constructor, so this is
        // always a valid non-zero value. Height fallback uses the app window height proxy
        // from the parent (ActualHeight of RightPanelView itself).
        if (w <= 0) w = Width;
        if (h <= 0) h = ActualHeight;

        if (w <= 0 || h <= 0)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine(
                $"[RightPanel] UpdateCanvasLayout BAILED rootW={RootGrid.ActualWidth} rootH={RootGrid.ActualHeight} " +
                $"ctrlW={Width} ctrlH={ActualHeight}");
#endif
            return;
        }

        // Canvas spans the entire root; offset lyrics to the content area below tabs.
        var resizerW = PanelResizer.ActualWidth;
        var tabH = TabHeader.ActualHeight;
        const double padLeft = 12, padRight = 12, padBottom = 12;

        var lyricsW = w - resizerW - padLeft - padRight;
        var lyricsH = h - tabH - padBottom;

        NowPlayingCanvas.LyricsStartX = resizerW + padLeft;
        NowPlayingCanvas.LyricsStartY = tabH;
        NowPlayingCanvas.LyricsWidth = lyricsW > 0 ? lyricsW : w;
        NowPlayingCanvas.LyricsHeight = lyricsH > 0 ? lyricsH : h;
        NowPlayingCanvas.LyricsOpacity = 1;
        NowPlayingCanvas.AlbumArtRect = Rect.Empty;

#if DEBUG
        System.Diagnostics.Debug.WriteLine(
            $"[RightPanel] UpdateCanvasLayout ok root={w:F0}x{h:F0} " +
            $"lyrics={NowPlayingCanvas.LyricsWidth:F0}x{NowPlayingCanvas.LyricsHeight:F0} " +
            $"start=({NowPlayingCanvas.LyricsStartX:F0},{NowPlayingCanvas.LyricsStartY:F0})");
#endif
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
        if (QueueContent == null || !IsLoaded) return;

        // Materialize the deferred (x:Load="False") subtrees on first selection.
        if (SelectedMode == RightPanelMode.Lyrics) EnsureLyricsTreeLoaded();
        if (SelectedMode == RightPanelMode.Details) EnsureDetailsTreeLoaded();

        QueueContent.Visibility = SelectedMode == RightPanelMode.Queue ? Visibility.Visible : Visibility.Collapsed;
        FriendsContent.Visibility = SelectedMode == RightPanelMode.FriendsActivity ? Visibility.Visible : Visibility.Collapsed;

        // LyricsContent / DetailsContent are x:Load'd — only touch once materialized.
        if (LyricsContent != null)
            LyricsContent.Visibility = SelectedMode == RightPanelMode.Lyrics ? Visibility.Visible : Visibility.Collapsed;
        if (DetailsContent != null)
            DetailsContent.Visibility = SelectedMode == RightPanelMode.Details ? Visibility.Visible : Visibility.Collapsed;

        // Background — only visible on Details tab
        if (SelectedMode != RightPanelMode.Details)
        {
            DetailsCanvasImage.Visibility = Visibility.Collapsed;
            _canvasMediaPlayer?.Pause();
        }
        else if (_activeBackgroundMode == DetailsBackgroundMode.Canvas
                 && _currentCanvasUrl != null && _canvasMediaPlayer != null)
        {
            DetailsCanvasImage.Visibility = Visibility.Visible;
            _canvasMediaPlayer.Play();
        }
        else if (_activeBackgroundMode == DetailsBackgroundMode.BlurredAlbumArt
                 && _currentAlbumArtUrl != null)
        {
            DetailsCanvasImage.Visibility = Visibility.Visible;
        }

        // Canvas mode: push content to bottom so video is visible
        ApplyCanvasLayout();

        // Details lyrics snippet timer — stop when not on Details tab
        if (SelectedMode != RightPanelMode.Details)
        {
            _detailsLyricsTimer?.Stop();
            CanvasLyricsOverlay.Visibility = Visibility.Collapsed;
            _canvasLyricsActive = false;
        }

        if (_lyricsInitialized)
            ApplyCurrentLyricsState();

        // Keep the Segmented tab header in sync when SelectedMode changes programmatically
        // (e.g., DetailsLyricsSnippet tap). Suppress the event to avoid a recursive update.
        var targetIdx = SelectedMode switch
        {
            RightPanelMode.Queue => 0,
            RightPanelMode.Lyrics => 1,
            RightPanelMode.FriendsActivity => 2,
            RightPanelMode.Details => 3,
            _ => 0
        };
        if (TabHeader != null && TabHeader.SelectedIndex != targetIdx)
        {
            _suppressTabSelectionChanged = true;
            try { TabHeader.SelectedIndex = targetIdx; }
            finally { _suppressTabSelectionChanged = false; }
        }

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

    // ── Tab header (Segmented) ──

    private void TabHeader_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabSelectionChanged) return;
        if (TabHeader?.SelectedIndex is not int idx || idx < 0) return;

        SelectedMode = idx switch
        {
            0 => RightPanelMode.Queue,
            1 => RightPanelMode.Lyrics,
            2 => RightPanelMode.FriendsActivity,
            3 => RightPanelMode.Details,
            _ => RightPanelMode.Queue
        };
    }

    // ── Deferred subtree materialization (x:Load="False") ──

    private void EnsureLyricsTreeLoaded()
    {
        if (_lyricsTreeLoaded) return;
        _ = FindName(nameof(LyricsContent));
        _lyricsTreeLoaded = LyricsContent != null;
    }

    private void EnsureDetailsTreeLoaded()
    {
        if (_detailsTreeLoaded) return;
        _ = FindName(nameof(DetailsContent));
        _detailsTreeLoaded = DetailsContent != null;

        // Wheel handler attaches directly to DetailsContent — must wait until the
        // subtree exists to register it. Before this point there's nothing to handle.
        if (_detailsTreeLoaded)
            RegisterDetailsWheelHandler();
    }

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
        // DetailsContent subtree is x:Load="False" until first details-tab selection.
        if (DetailsContent == null) return;

        DetailsLoadingShimmer.Visibility = _detailsVm.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        DetailsErrorText.Text = _detailsVm.ErrorMessage ?? "";
        DetailsErrorText.Visibility = !string.IsNullOrEmpty(_detailsVm.ErrorMessage)
            ? Visibility.Visible : Visibility.Collapsed;

        var hasData = _detailsVm.HasData;

        // Background (none / blurred album art / canvas) — update immediately, no delay
        if (hasData)
            ApplyDetailsBackground(_detailsVm.CanvasUrl, _detailsVm.HasCanvas);
        else
            ApplyDetailsBackground(null, false);

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
        if (DetailsContent == null) return;
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
        if (DetailsContent == null) return;
        UpdateDetailsContent();
        DetailsContent.ChangeView(null, 0, null, true);

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
        if (DetailsContent == null) return;

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
        UpdateCanvasLyricsVisibility();
        UpdateLyricsSnippetText();

        // Use faster tick rate when canvas overlay is visible (50ms for smooth typing)
        var interval = _canvasLyricsActive ? 50 : 250;
        if (_detailsLyricsTimer == null)
        {
            _detailsLyricsTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _detailsLyricsTimer.Interval = TimeSpan.FromMilliseconds(interval);
            _detailsLyricsTimer.Tick += OnDetailsLyricsTimerTick;
        }
        else
        {
            _detailsLyricsTimer.Interval = TimeSpan.FromMilliseconds(interval);
        }
        _detailsLyricsTimer.Start();
    }

    private void TeardownDetailsLyricsSnippet()
    {
        _detailsLyricsTimer?.Stop();
        _lastSnippetLineIndex = -1;
        CanvasLyricsOverlay.Visibility = Visibility.Collapsed;
        _canvasLyricsActive = false;
    }

    private void OnDetailsLyricsTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (SelectedMode != RightPanelMode.Details) return;
        UpdateLyricsSnippetText();
    }

    private bool _canvasLyricsActive;
    private int _lastRevealedSyllableCount;
    private double _syllableRevealTimeMs;

    private void UpdateCanvasLyricsVisibility()
    {
        var show = SelectedMode == RightPanelMode.Details
                   && _activeBackgroundMode != DetailsBackgroundMode.None
                   && _lyricsVm?.HasLyrics == true;
        _canvasLyricsActive = show;
        CanvasLyricsOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // Adjust timer speed
        if (_detailsLyricsTimer != null)
            _detailsLyricsTimer.Interval = TimeSpan.FromMilliseconds(show ? 50 : 250);
    }

    private void UpdateLyricsSnippetText()
    {
        if (_lyricsVm?.CurrentLyrics?.LyricsLines is not { Count: > 0 } lines) return;
        // DetailsLyricsPrev/Current/Next live inside DetailsContent (x:Load'd).
        if (DetailsContent == null) return;

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

        var currentLine = lines[currentIdx];
        var next = currentIdx < lines.Count - 1 ? lines[currentIdx + 1].PrimaryText : "";

        // Update card snippet (only on line change)
        if (currentIdx != _lastSnippetLineIndex)
        {
            _lastSnippetLineIndex = currentIdx;
            _lastRevealedSyllableCount = 0;

            var prev = currentIdx > 0 ? lines[currentIdx - 1].PrimaryText : "";
            DetailsLyricsPrev.Text = prev;
            DetailsLyricsPrev.Visibility = string.IsNullOrWhiteSpace(prev)
                ? Visibility.Collapsed : Visibility.Visible;
            DetailsLyricsCurrent.Text = currentLine.PrimaryText;
            DetailsLyricsNext.Text = next;
            DetailsLyricsNext.Visibility = string.IsNullOrWhiteSpace(next)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        // Update canvas overlay with syllable-by-syllable typing + fade
        if (_canvasLyricsActive)
            UpdateCanvasOverlaySyllables(currentLine, posMs);
    }

    private const double SyllableFadeDurationMs = 180;

    private void UpdateCanvasOverlaySyllables(
        Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine line, double posMs)
    {
        var syllables = line.PrimarySyllables;

        // If no syllable data, show full line as plain text
        if (!line.IsPrimaryHasRealSyllableInfo || syllables.Count == 0)
        {
            CanvasLyricLine1.Text = line.PrimaryText.ToUpperInvariant();
            CanvasLyricLine1.Opacity = 0.4;
            return;
        }

        // Count revealed syllables
        int revealedCount = 0;
        foreach (var syl in syllables)
        {
            if (syl.StartMs <= posMs) revealedCount++;
            else break;
        }

        // Track when a new syllable appears to start the ease-in
        if (revealedCount > _lastRevealedSyllableCount)
        {
            _lastRevealedSyllableCount = revealedCount;
            _syllableRevealTimeMs = posMs;
        }

        // Ease-in progress for the newest syllable
        // Cap fade to syllable duration so fast rap sections don't lag
        var newestSyl = revealedCount > 0 ? syllables[revealedCount - 1] : null;
        var syllableDurationMs = newestSyl?.DurationMs ?? 300;
        var fadeDuration = Math.Min(SyllableFadeDurationMs, Math.Max(syllableDurationMs * 0.8, 40));
        var fadeElapsed = posMs - _syllableRevealTimeMs;
        var fadeT = Math.Clamp(fadeElapsed / fadeDuration, 0, 1);
        var eased = 1.0 - (1.0 - fadeT) * (1.0 - fadeT); // quadratic ease-out

        CanvasLyricLine1.Inlines.Clear();
        CanvasLyricLine1.Opacity = 1.0;

        var dimBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(102, 255, 255, 255));
        var brightBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 255, 255, 255));

        for (int i = 0; i < revealedCount; i++)
        {
            var syl = syllables[i];
            var isPlaying = syl.StartMs <= posMs && (syl.EndMs == null || posMs < syl.EndMs);
            var isNewest = i == revealedCount - 1;

            byte alpha;
            if (isNewest && fadeT < 1.0)
            {
                // Ease in from 0 to target (bright if playing, dim if already played)
                var targetAlpha = isPlaying ? (byte)230 : (byte)102;
                alpha = (byte)(targetAlpha * eased);
            }
            else
            {
                alpha = isPlaying ? (byte)230 : (byte)102;
            }

            CanvasLyricLine1.Inlines.Add(
                new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = syl.Text.ToUpperInvariant(),
                    Foreground = alpha == 230 ? brightBrush
                        : alpha == 102 ? dimBrush
                        : new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, 255, 255, 255))
                });
        }
    }

    // ── Credits collapse/expand ──

    private bool _creditsExpanded;
    private const int CreditsCollapsedMaxPeople = 4;

    private void ApplyCreditsCollapse()
    {
        if (_detailsVm == null) return;
        if (DetailsContent == null) return;
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
                    DetailsContent.ChangeView(null, DetailsContent.VerticalOffset + position.Y, null, false);
                }
            },
            SetBackgroundModeAction = (mode) =>
            {
                var modeStr = mode switch
                {
                    DetailsBackgroundMode.None => "None",
                    DetailsBackgroundMode.BlurredAlbumArt => "BlurredAlbumArt",
                    DetailsBackgroundMode.Canvas => "Canvas",
                    _ => "Canvas"
                };
                _settingsService?.Update(s => s.DetailsBackgroundMode = modeStr);
                _ = _settingsService?.SaveAsync();

                // Re-apply background with the new mode
                if (_detailsVm != null)
                    ApplyDetailsBackground(_detailsVm.CanvasUrl, _detailsVm.HasCanvas);
            },
            HasCanvas = _detailsVm?.HasCanvas ?? false,
            CurrentBackgroundMode = _activeBackgroundMode,
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

    // ── Details background (None / Blurred Album Art / Canvas video) ──
    // Canvas: MediaPlayer in frame server mode → Win2D blur → CanvasImageSource → Image.
    // Blurred Album Art: Load album art bitmap → heavy Win2D blur → CanvasImageSource → Image.
    // No SwapChainPanel = acrylic works on top.

    private Windows.Media.Playback.MediaPlayer? _canvasMediaPlayer;
    private string? _currentCanvasUrl;
    private string? _currentAlbumArtUrl;
    private CanvasDevice? _canvasDevice;
    private CanvasRenderTarget? _canvasFrameTarget;
    private const float AlbumArtBlurAmount = 40f;
    private CanvasImageSource? _canvasImageSource;
    private CanvasImageSource? _blurredAlbumArtImageSource;
    private int _detailsBackgroundGeneration;
    private DetailsBackgroundMode _activeBackgroundMode;

    private DetailsBackgroundMode GetSettingsBackgroundMode()
    {
        var raw = _settingsService?.Settings.DetailsBackgroundMode ?? "Canvas";
        return raw switch
        {
            "None" => DetailsBackgroundMode.None,
            "BlurredAlbumArt" => DetailsBackgroundMode.BlurredAlbumArt,
            "Canvas" => DetailsBackgroundMode.Canvas,
            _ => DetailsBackgroundMode.Canvas
        };
    }

    private void ApplyDetailsBackground(string? canvasUrl, bool hasCanvas)
    {
        var mode = GetSettingsBackgroundMode();

        // Fall back to blurred album art when Canvas is selected but track has no canvas
        if (mode == DetailsBackgroundMode.Canvas && !hasCanvas)
            mode = DetailsBackgroundMode.BlurredAlbumArt;

        _activeBackgroundMode = mode;
        UpdateCanvasLyricsVisibility();

        switch (mode)
        {
            case DetailsBackgroundMode.None:
                TeardownCanvasBackground();
                TeardownBlurredAlbumArt();
                DetailsCanvasImage.Visibility = Visibility.Collapsed;
                break;

            case DetailsBackgroundMode.BlurredAlbumArt:
                TeardownCanvasBackground();
                SetupBlurredAlbumArt();
                break;

            case DetailsBackgroundMode.Canvas:
                TeardownBlurredAlbumArt();
                SetupCanvasBackground(canvasUrl);
                break;
        }

        ApplyCanvasLayout();
    }

    // ── Blurred album art ──

    private async void SetupBlurredAlbumArt()
    {
        var generation = ++_detailsBackgroundGeneration;
        var albumArt = SpotifyImageHelper.ToHttpsUrl(
            _lyricsVm?.PlaybackState.CurrentAlbumArtLarge
            ?? _lyricsVm?.PlaybackState.CurrentAlbumArt);

        if (string.IsNullOrEmpty(albumArt))
        {
            TeardownBlurredAlbumArt();
            DetailsCanvasImage.Visibility = Visibility.Collapsed;
            return;
        }

        if (albumArt == _currentAlbumArtUrl && DetailsCanvasImage.Visibility == Visibility.Visible)
            return;

        _currentAlbumArtUrl = albumArt;
        _canvasDevice ??= new CanvasDevice();

        try
        {
            using var bitmap = await CanvasBitmap.LoadAsync(
                _canvasDevice, new Uri(albumArt));

            // Render at panel size (half res for perf)
            var w = Math.Max(1, (int)RootGrid.ActualWidth / 2);
            var h = Math.Max(1, (int)RootGrid.ActualHeight / 2);

            var imageSource = new CanvasImageSource(_canvasDevice, w, h, 96);
            try
            {
                using (var ds = imageSource.CreateDrawingSession(Colors.Transparent))
                {
                    // Scale bitmap to fill the target rect
                    var scaleX = (float)w / bitmap.SizeInPixels.Width;
                    var scaleY = (float)h / bitmap.SizeInPixels.Height;
                    var scale = Math.Max(scaleX, scaleY);

                    var scaledW = bitmap.SizeInPixels.Width * scale;
                    var scaledH = bitmap.SizeInPixels.Height * scale;
                    var offsetX = (w - scaledW) / 2f;
                    var offsetY = (h - scaledH) / 2f;

                    var scaled = new Microsoft.Graphics.Canvas.Effects.ScaleEffect
                    {
                        Source = bitmap,
                        Scale = new System.Numerics.Vector2(scale, scale),
                        CenterPoint = System.Numerics.Vector2.Zero
                    };

                    var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
                    {
                        Source = scaled,
                        BlurAmount = AlbumArtBlurAmount,
                        BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Hard
                    };

                    ds.DrawImage(blur, new System.Numerics.Vector2(offsetX, offsetY));
                    blur.Dispose();
                    scaled.Dispose();
                }

                // Verify we're still in blurred album art mode and same URL
                if (_activeBackgroundMode != DetailsBackgroundMode.BlurredAlbumArt
                    || _currentAlbumArtUrl != albumArt
                    || generation != _detailsBackgroundGeneration)
                {
                    DisposeCanvasImageSource(imageSource);
                    return;
                }

                ReplaceBlurredAlbumArtSource(imageSource);
                DetailsCanvasImage.Source = imageSource;
                DetailsCanvasImage.Visibility = Visibility.Visible;
            }
            catch
            {
                DisposeCanvasImageSource(imageSource);
                throw;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RightPanel] SetupBlurredAlbumArt failed: {ex.Message}");
            DetailsCanvasImage.Visibility = Visibility.Collapsed;
        }
    }

    private void TeardownBlurredAlbumArt()
    {
        _detailsBackgroundGeneration++;
        _currentAlbumArtUrl = null;
        if (ReferenceEquals(DetailsCanvasImage.Source, _blurredAlbumArtImageSource))
            DetailsCanvasImage.Source = null;
        DisposeCanvasImageSource(ref _blurredAlbumArtImageSource);
    }

    private void ReplaceBlurredAlbumArtSource(CanvasImageSource imageSource)
    {
        if (!ReferenceEquals(_blurredAlbumArtImageSource, imageSource))
        {
            if (ReferenceEquals(DetailsCanvasImage.Source, _blurredAlbumArtImageSource))
                DetailsCanvasImage.Source = null;
            DisposeCanvasImageSource(ref _blurredAlbumArtImageSource);
            _blurredAlbumArtImageSource = imageSource;
        }
    }

    // ── Canvas layout (push content to bottom so video is visible) ──

    private void ApplyCanvasLayout(bool animate = true)
    {
        if (DetailsCanvasSpacer == null) return;

        var isCanvas = _activeBackgroundMode == DetailsBackgroundMode.Canvas
                       && SelectedMode == RightPanelMode.Details;

        var targetHeight = isCanvas
            ? Math.Max(0, DetailsContent.ActualHeight - 120)
            : 0d;

        if (!animate || !IsLoaded)
        {
            DetailsCanvasSpacer.Height = targetHeight;
            return;
        }

        var current = DetailsCanvasSpacer.Height;
        if (Math.Abs(current - targetHeight) < 1) return;

        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var da = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = current,
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut },
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(da, DetailsCanvasSpacer);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(da, "Height");
        storyboard.Children.Add(da);
        storyboard.Begin();

        if (isCanvas)
            DetailsContent.ChangeView(null, 0, null, false);
    }

    private void DetailsContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_activeBackgroundMode == DetailsBackgroundMode.Canvas
            && SelectedMode == RightPanelMode.Details
            && DetailsCanvasSpacer != null)
        {
            DetailsCanvasSpacer.Height = Math.Max(0, DetailsContent.ActualHeight - 120);
        }

    }

    // ── Card-by-card paging for Details ──

    private bool _detailsWheelHandlerRegistered;

    private void RegisterDetailsWheelHandler()
    {
        if (_detailsWheelHandlerRegistered) return;
        DetailsContent.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler(DetailsContent_PointerWheelChanged), true);
        _detailsWheelHandlerRegistered = true;
    }

    private void UnregisterDetailsWheelHandler()
    {
        if (!_detailsWheelHandlerRegistered) return;
        DetailsContent.RemoveHandler(PointerWheelChangedEvent,
            new PointerEventHandler(DetailsContent_PointerWheelChanged));
        _detailsWheelHandlerRegistered = false;
    }

    private FrameworkElement[] GetVisibleDetailsCards()
    {
        var all = new FrameworkElement[]
        {
            DetailsArtistHeaderCard, DetailsLyricsSnippet, DetailsBio,
            DetailsCreditsSection, DetailsConcertsSection, DetailsRelatedVideosSection
        };
        return all.Where(c => c.Visibility == Visibility.Visible).ToArray();
    }

    private void DetailsContent_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (SelectedMode != RightPanelMode.Details) return;

        var props = e.GetCurrentPoint(DetailsContent).Properties;
        var delta = props.MouseWheelDelta;
        if (delta == 0) return;

        var cards = GetVisibleDetailsCards();
        if (cards.Length == 0) return;

        var content = (UIElement)DetailsContent.Content;
        var currentOffset = DetailsContent.VerticalOffset;

        if (delta < 0) // scroll down
        {
            // Find the first card whose bottom edge is below the current viewport top
            // and scroll past it (to the next card's top)
            for (int i = 0; i < cards.Length; i++)
            {
                var transform = cards[i].TransformToVisual(content);
                var cardY = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                var cardBottom = cardY + cards[i].ActualHeight;

                // This card's bottom is still below viewport top — scroll past it
                if (cardBottom > currentOffset + 1)
                {
                    DetailsContent.ChangeView(null, cardBottom, null, false);
                    break;
                }
            }
        }
        else // scroll up
        {
            // Find the last card whose top edge is above the current viewport top
            // and scroll to its top
            for (int i = cards.Length - 1; i >= 0; i--)
            {
                var transform = cards[i].TransformToVisual(content);
                var cardY = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

                if (cardY < currentOffset - 1)
                {
                    DetailsContent.ChangeView(null, cardY, null, false);
                    break;
                }
            }

            // If we're above all cards, scroll to top
            if (cards.Length > 0)
            {
                var firstTransform = cards[0].TransformToVisual(content);
                var firstY = firstTransform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                if (currentOffset <= firstY + 1)
                    DetailsContent.ChangeView(null, 0, null, false);
            }
        }

        e.Handled = true;
    }

    // ── Canvas video ──

    private void SetupCanvasBackground(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            TeardownCanvasBackground();
            DetailsCanvasImage.Visibility = Visibility.Collapsed;
            return;
        }

        if (url == _currentCanvasUrl && _canvasMediaPlayer != null) return;

        TeardownCanvasBackground();
        _currentCanvasUrl = url;

        _canvasDevice ??= new CanvasDevice();

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
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_canvasMediaPlayer == null || _canvasDevice == null) return;

            var w = Math.Max(1, (int)RootGrid.ActualWidth);
            var h = Math.Max(1, (int)RootGrid.ActualHeight);
            if (w <= 0 || h <= 0) return;

            try
            {
                // Create or resize the render target for video frames
                if (_canvasFrameTarget == null || _canvasFrameTarget.SizeInPixels.Width != w || _canvasFrameTarget.SizeInPixels.Height != h)
                {
                    _canvasFrameTarget?.Dispose();
                    _canvasFrameTarget = new CanvasRenderTarget(_canvasDevice, w, h, 96);
                }

                _canvasMediaPlayer.CopyFrameToVideoSurface(_canvasFrameTarget);

                // Create or resize the image source
                if (_canvasImageSource == null || _canvasImageSource.SizeInPixels.Width != w || _canvasImageSource.SizeInPixels.Height != h)
                {
                    if (ReferenceEquals(DetailsCanvasImage.Source, _canvasImageSource))
                        DetailsCanvasImage.Source = null;
                    DisposeCanvasImageSource(ref _canvasImageSource);
                    _canvasImageSource = new CanvasImageSource(_canvasDevice, w, h, 96);
                    DetailsCanvasImage.Source = _canvasImageSource;
                }

                // Draw frame directly (no blur)
                using var ds = _canvasImageSource.CreateDrawingSession(Colors.Transparent);
                ds.DrawImage(_canvasFrameTarget);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Device lost or resource failure — recreate everything
                _canvasDevice?.Dispose();
                _canvasDevice = null;
                _canvasFrameTarget?.Dispose();
                _canvasFrameTarget = null;
                if (ReferenceEquals(DetailsCanvasImage.Source, _canvasImageSource))
                    DetailsCanvasImage.Source = null;
                DisposeCanvasImageSource(ref _canvasImageSource);

                // Force re-setup on next call
                var url = _currentCanvasUrl;
                _currentCanvasUrl = null;
                if (!string.IsNullOrEmpty(url))
                    SetupCanvasBackground(url);
            }
        });
    }

    private void TeardownCanvasBackground()
    {
        _detailsBackgroundGeneration++;
        if (_canvasMediaPlayer != null)
        {
            _canvasMediaPlayer.VideoFrameAvailable -= OnCanvasVideoFrameAvailable;
            _canvasMediaPlayer.Pause();
            _canvasMediaPlayer.Source = null;
            _canvasMediaPlayer.Dispose();
            _canvasMediaPlayer = null;
        }

        if (ReferenceEquals(DetailsCanvasImage.Source, _canvasImageSource))
            DetailsCanvasImage.Source = null;
        DisposeCanvasImageSource(ref _canvasImageSource);
        _canvasFrameTarget?.Dispose();
        _canvasFrameTarget = null;
        // Keep _canvasDevice alive for reuse

        _currentCanvasUrl = null;
    }

    private static void DisposeCanvasImageSource(CanvasImageSource? source)
    {
    }

    private static void DisposeCanvasImageSource(ref CanvasImageSource? source)
    {
        source = null;
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
