using System;
using System.ComponentModel;
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
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class RightPanelView : UserControl
{
    private const double MinPanelWidth = 200;
    private const double MaxPanelWidth = 500;

    private bool _draggingResizer;
    private double _preManipulationWidth;

    // Lyrics integration
    private LyricsViewModel? _lyricsVm;
    private DispatcherQueueTimer? _positionTimer;
    private DispatcherQueueTimer? _scrollResetTimer;
    private bool _lyricsInitialized;
    private bool _showingLoadingDots;
    private readonly ThemeColorService? _themeColors;
    private readonly ILyricsService? _lyricsService;
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
        _lyricsVm = Ioc.Default.GetService<LyricsViewModel>();
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

        // Position timer — 33ms (~30fps) for smooth lyrics sync
        _positionTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(33);
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
            NowPlayingCanvas.SetPosition(_lyricsVm.GetInterpolatedPosition());
        }
        else if (_lyricsVm.IsLoading)
        {
            _showingLoadingDots = true;
            NowPlayingCanvas.SetLyricsData(LoadingDotsData);
            NowPlayingCanvas.SetIsPlaying(true);
        }

        UpdateTimerState();
    }

    private void UpdateTimerState()
    {
        if (_positionTimer == null || _lyricsVm == null) return;

        var shouldRun = SelectedMode == RightPanelMode.Lyrics
                        && Visibility == Visibility.Visible
                        && ((_lyricsVm.HasLyrics && _lyricsVm.PlaybackState.IsPlaying)
                            || _showingLoadingDots);

        if (shouldRun)
            _positionTimer.Start();
        else
            _positionTimer.Stop();
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
            NowPlayingCanvas.SetPosition(_lyricsVm.GetInterpolatedPosition());
            NowPlayingCanvas.SetIsPlaying(_lyricsVm.PlaybackState.IsPlaying);
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
    }

    private void LyricsOverlay_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        NowPlayingCanvas.IsMouseInLyricsArea = false;
    }

    private void LyricsOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LyricsInteractionOverlay).Position;
        NowPlayingCanvas.MousePosition = point;
    }

    private void LyricsOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        NowPlayingCanvas.IsMousePressing = true;
    }

    private void LyricsOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        NowPlayingCanvas.IsMousePressing = false;
        NowPlayingCanvas.FireSeekIfHovering();
    }

    private void LyricsOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        NowPlayingCanvas.IsMouseScrolling = true;
        LyricsSyncButton.Visibility = Visibility.Visible;

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

        // Canvas provides the opaque background for the entire panel (all tabs)
        NowPlayingCanvas.Visibility = Visibility.Visible;

        QueueTab.IsChecked = SelectedMode == RightPanelMode.Queue;
        LyricsTab.IsChecked = SelectedMode == RightPanelMode.Lyrics;
        FriendsTab.IsChecked = SelectedMode == RightPanelMode.FriendsActivity;

        // When switching to lyrics tab, ensure we have the latest state
        if (SelectedMode == RightPanelMode.Lyrics && _lyricsInitialized)
        {
            _lyricsVm?.InvalidateTrack();
            UpdateCanvasLayout();
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
