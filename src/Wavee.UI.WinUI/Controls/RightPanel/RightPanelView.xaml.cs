using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.Controls.Lyrics.Models;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Services.Docking;
using Wavee.UI.WinUI.ViewModels;
using System.Numerics;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.DirectX;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class RightPanelView : UserControl
{
    private const double MinPanelWidth = 200;
    private const double MaxPanelWidth = 500;
    private const float AlbumArtBlurAmount = 20f;
    private const float AlbumArtSaturationAmount = 0.88f;
    private const float CanvasSaturationAmount = 0.82f;
    private const int BlurredAlbumArtRenderTolerancePx = 36;
    private const int PodcastChapterPreviewCount = 4;

    private bool _draggingResizer;
    private double _preManipulationWidth;
    private bool _isOpenCached;

    // Tracks whether the deferred LyricsContent / DetailsContent subtrees have been
    // materialized into the visual tree yet. Both use x:Load="False" in XAML and are
    // loaded on demand when their tab is first selected. Once loaded, they stay loaded.
    private bool _lyricsTreeLoaded;
    private bool _detailsTreeLoaded;
    private bool _trackDetailsTreeLoaded;
    private PropertyChangedEventHandler? _shellViewModelTrackDetailsHandler;
    private ShellViewModel? _shellViewModelForTrackDetails;

    // Details integration
    private TrackDetailsViewModel? _detailsVm;
    private DispatcherQueueTimer? _podcastChapterTimelineTimer;
    private bool _showAllPodcastChapters;
    private int _podcastChapterPreviewStart = -1;
    private string? _podcastChapterEpisodeUri;

    // Lyrics integration
    private LyricsViewModel? _lyricsVm;
    private DispatcherQueueTimer? _positionTimer;
    private DispatcherQueueTimer? _scrollResetTimer;
    private double _lastCanvasPositionMs = -1;
    private bool _lyricsInitialized;
    private bool _lyricsConsumerActive;
    private bool _pendingCanvasLayoutRetry;
    private int _lyricsHoverExplainLineIndex = -1;
    private Rect _lyricsHoverExplainHitRect = Rect.Empty;
    private DispatcherQueueTimer? _lyricsHoverExplainHideTimer;
    private LyricsData? _appliedLyricsData;
    private SongInfo? _appliedSongInfo;
    private bool _lyricsCanvasDataCleared = true;
    private readonly ThemeColorService? _themeColors;
    private readonly ILyricsService? _lyricsService;
    private readonly IColorService? _colorService;
    private readonly ISettingsService? _settingsService;
    private readonly INotificationService? _notificationService;
    private readonly LyricsAiService? _lyricsAiService;
    private readonly AiCapabilities? _aiCapabilities;
    private CancellationTokenSource? _detailsAiMeaningCts;
    private string? _detailsAiMeaningTrackUri;
    private bool _detailsAiMeaningBusy;
    private bool _themeColorsSubscribed;
    private readonly List<CompositionObject> _backgroundOverlayCompositionObjects = [];
    private ContainerVisual? _backgroundOverlayContainer;
    private Compositor? _backgroundOverlayCompositor;

    // ── Tab content bottom fade (shared across all right-panel tabs) ──
    private readonly List<CompositionObject> _tabFadeCompositionObjects = [];
    private Compositor? _tabFadeCompositor;
    private ContainerVisual? _tabFadeContainer;
    private CompositionEffectBrush? _tabBlurBrush;
    private SpriteVisual? _tabBlurVisual;
    private SpriteVisual? _tabFadeVisual;
    private CompositionLinearGradientBrush? _tabFadeBrush;
    private CompositionColorGradientStop? _tabFadeStop0;
    private CompositionColorGradientStop? _tabFadeStop1;
    private CompositionColorGradientStop? _tabFadeStop2;
    private CompositionColorGradientStop? _tabFadeStop3;
    private CompositionColorBrush? _backgroundTintBrush;
    private CompositionColorBrush? _backgroundNonDetailsDimBrush;
    private CompositionLinearGradientBrush? _backgroundHighlightBrush;
    private CompositionColorGradientStop? _backgroundHighlightStartStop;
    private CompositionColorGradientStop? _backgroundHighlightMidStop;
    private CompositionColorGradientStop? _backgroundHighlightEndStop;
    private CompositionLinearGradientBrush? _backgroundScrimBrush;
    private CompositionColorGradientStop? _backgroundScrimTopStop;
    private CompositionColorGradientStop? _backgroundScrimMidStop;
    private CompositionColorGradientStop? _backgroundScrimBottomStop;
    private CompositionLinearGradientBrush? _backgroundBottomBlendBrush;
    private CompositionColorGradientStop? _backgroundBottomBlendTopStop;
    private CompositionColorGradientStop? _backgroundBottomBlendMidStop;
    private CompositionColorGradientStop? _backgroundBottomBlendLowerMidStop;
    private CompositionColorGradientStop? _backgroundBottomBlendBottomStop;
    private CompositionLinearGradientBrush? _backgroundTopBlendBrush;
    private CompositionColorGradientStop? _backgroundTopBlendTopStop;
    private CompositionColorGradientStop? _backgroundTopBlendMidStop;
    private CompositionColorGradientStop? _backgroundTopBlendLowerMidStop;
    private CompositionColorGradientStop? _backgroundTopBlendBottomStop;
    private SpriteVisual? _backgroundTintVisual;
    private SpriteVisual? _backgroundHighlightVisual;
    private SpriteVisual? _backgroundScrimVisual;
    private SpriteVisual? _backgroundNonDetailsDimVisual;
    private SpriteVisual? _backgroundBottomBlendVisual;
    private SpriteVisual? _backgroundTopBlendVisual;
    private readonly List<CompositionObject> _detailsLyricsCompositionObjects = [];
    private ContainerVisual? _detailsLyricsContainerVisual;
    private SpriteVisual? _detailsLyricsTextVisual;
    private SpriteVisual? _detailsLyricsCursorVisual;
    private CompositionSurfaceBrush? _detailsLyricsTextBrush;
    private CompositionColorBrush? _detailsLyricsCursorBrush;
    private CompositionGraphicsDevice? _detailsLyricsGraphicsDevice;
    private CompositionDrawingSurface? _detailsLyricsDrawingSurface;
    private CanvasDevice? _detailsLyricsCanvasDevice;
    private CanvasTextLayout? _detailsLyricsTextLayout;
    private CanvasTextLayoutRegion[]? _detailsLyricsCharacterRegions;
    private string? _detailsLyricsLayoutText;
    private float _detailsLyricsLayoutWidth;
    private float _detailsLyricsLayoutHeight;
    private bool _detailsLyricsRenderSubscribed;
    private CancellationTokenSource? _backgroundTintCts;
    private string? _backgroundTintImageUrl;
    private ExtractedColor? _backgroundTintExtractedColor;
    // Initially suppressed: the Segmented inside RightPanelView ships with
    // SelectedIndex="0" in XAML so the Queue tab is the visual default. During
    // construction the SelectionChanged handler used to fire for that initial
    // Queue selection BEFORE the SelectedMode binding pushed the persisted /
    // requested mode (e.g. Lyrics) — which then wrote Queue back through the
    // TwoWay binding to ShellViewModel.RightPanelMode and made the Lyrics
    // button effectively a no-op the first click. Released in OnLoaded once
    // the binding has settled.
    private bool _suppressTabHeaderSelectionChanged = true;

    public RightPanelView()
    {
        InitializeComponent();
        _themeColors = Ioc.Default.GetService<ThemeColorService>();
        _lyricsService = Ioc.Default.GetService<ILyricsService>();
        _colorService = Ioc.Default.GetService<IColorService>();
        _settingsService = Ioc.Default.GetService<ISettingsService>();
        _notificationService = Ioc.Default.GetService<INotificationService>();
        _lyricsAiService = Ioc.Default.GetService<LyricsAiService>();
        _aiCapabilities = Ioc.Default.GetService<AiCapabilities>();
        _lyricsVm = Ioc.Default.GetService<LyricsViewModel>();
        _detailsVm = Ioc.Default.GetService<TrackDetailsViewModel>();
        Visibility = Visibility.Collapsed;
        Width = PanelWidth;
    }

    // ── Lifecycle ──

    private void RightPanelView_Loaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[mem] RightPanelView.Loaded");
        if (_themeColors != null && !_themeColorsSubscribed)
        {
            _themeColors.ThemeChanged += OnThemeColorsChanged;
            _themeColorsSubscribed = true;
        }

        if (ShouldKeepLyricsVmActive())
            InitializeLyrics();
        // RegisterDetailsWheelHandler is deferred: the DetailsContent subtree is
        // x:Load="False" and doesn't exist until the Details tab is first opened.
        // See EnsureDetailsTreeLoaded().
        ActualThemeChanged += OnActualThemeChanged;
        SizeChanged += OnPanelSizeChanged;
        UpdateCanvasClearColor();
        ApplyEmbeddedChrome();
        EnsurePodcastChapterTimelineTimer();
        EnsureTabContentFadeComposition();
        UpdateBackgroundChrome();
        RefreshBackgroundTint();
        UpdatePanelBackgroundState();
        UpdateTabHeaderVisualState();

        // Release the suppression flag now that the SelectedMode binding has
        // settled and the visual selection matches it. From here on, real user
        // taps on the tab bar are honored.
        _suppressTabHeaderSelectionChanged = false;

        // Re-apply tab content visibility now that we're loaded. UpdateContentVisibility
        // guards on `!IsLoaded` and bails when the SelectedMode change fires before
        // Loaded — without this re-apply, the panel can render the previous mode's
        // content (e.g. Queue list visible while the Lyrics tab is selected).
        UpdateContentVisibility();
        UpdateLyricsConsumerActivity();
        UpdatePodcastChapterTimelineTimerState();
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SelectedMode == RightPanelMode.Lyrics)
            UpdateCanvasLayout();

        UpdateBackgroundChrome();
    }

    private void RightPanelView_Unloaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[mem] RightPanelView.Unloaded");
        if (_themeColors != null && _themeColorsSubscribed)
        {
            _themeColors.ThemeChanged -= OnThemeColorsChanged;
            _themeColorsSubscribed = false;
        }

        ActualThemeChanged -= OnActualThemeChanged;
        SizeChanged -= OnPanelSizeChanged;
        UnregisterDetailsWheelHandler();
        UpdateLyricsConsumerActivity(active: false);
        TeardownLyrics();
        TeardownPodcastChapterTimelineTimer();

        if (_detailsVm != null && _detailsSubscribed)
        {
            _detailsVm.PropertyChanged -= OnDetailsVmPropertyChanged;
            _detailsSubscribed = false;
        }
        TeardownCanvasBackground();
        TeardownBlurredAlbumArt();
        CancelBackgroundTintRefresh();
        TeardownBackgroundOverlayComposition();
        TeardownTabContentFadeComposition();
        TeardownDetailsLyricsComposition();
        _canvasDevice?.Dispose();
        _canvasDevice = null;
        TeardownDetailsLyricsSnippet();
        CancelDetailsAiMeaningRequest();
    }

    private void OnThemeColorsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateCanvasClearColor();
            UpdateTabContentFadeColor();
            UpdateLyricsPaletteForTheme();
            UpdateBackgroundChrome();
            UpdateTabHeaderVisualState();
        });
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCanvasClearColor();
        UpdateTabContentFadeColor();
        UpdateLyricsPaletteForTheme();
        UpdateBackgroundChrome();
        UpdateTabHeaderVisualState();
    }

    private void UpdateLyricsPaletteForTheme()
    {
        if (_lyricsVm == null) return;

        bool isDark = ActualTheme != ElementTheme.Light;
        // Active (played + currently-playing) line: pure black/white for emphasis.
        // Off-current and upcoming lines: softer gray/light-gray so the queue reads
        // as de-emphasized instead of competing with the active line. Was pure
        // black on white in Light mode, which the user flagged as too dark.
        var activeFg = isDark ? Colors.White : Colors.Black;
        var offFg = isDark
            ? Color.FromArgb(0xFF, 0xC0, 0xC0, 0xC0)
            : Color.FromArgb(0xFF, 0x55, 0x55, 0x55);

        var palette = _lyricsVm.WindowStatus.WindowPalette;
        palette.NonCurrentLineFillColor = offFg;
        palette.PlayedCurrentLineFillColor = activeFg;
        palette.UnplayedCurrentLineFillColor = offFg;
        palette.ThemeType = isDark
            ? Microsoft.UI.Xaml.ElementTheme.Dark
            : Microsoft.UI.Xaml.ElementTheme.Light;

        NowPlayingCanvas.SetNowPlayingPalette(palette);
    }

    private void InitializeLyrics()
    {
        if (_lyricsInitialized) return;

        if (_lyricsVm == null) return;

        System.Diagnostics.Debug.WriteLine("[mem] RightPanelView.InitializeLyrics");
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
        _positionTimer.Interval = TimeSpan.FromMilliseconds(DetailsSnippetTickMs);
        _positionTimer.Tick += OnPositionTimerTick;

        // If there's already a track loaded, apply it
        ApplyCurrentLyricsState();

        // Start the timer if lyrics tab is visible and playing
        UpdateTimerState();
    }

    private bool ShouldInitializeLyricsForMode()
        => SelectedMode is RightPanelMode.Lyrics or RightPanelMode.Details;

    private bool ShouldKeepLyricsVmActive()
        => IsLoaded && _isOpenCached && ShouldInitializeLyricsForMode();

    private void UpdateLyricsConsumerActivity()
        => UpdateLyricsConsumerActivity(ShouldKeepLyricsVmActive());

    private void UpdateLyricsConsumerActivity(bool active)
    {
        if (_lyricsConsumerActive == active)
            return;

        _lyricsConsumerActive = active;
        _lyricsVm?.SetConsumerActive(this, active);
    }

    private void TeardownLyrics()
    {
        if (!_lyricsInitialized) return;

        System.Diagnostics.Debug.WriteLine("[mem] RightPanelView.TeardownLyrics");
        if (_positionTimer != null)
        {
            _positionTimer.Stop();
            _positionTimer.Tick -= OnPositionTimerTick;
            _positionTimer = null;
        }

        if (_scrollResetTimer != null)
        {
            _scrollResetTimer.Stop();
            _scrollResetTimer.Tick -= OnScrollResetTimerTick;
            _scrollResetTimer = null;
        }

        if (_lyricsHoverExplainHideTimer != null)
        {
            _lyricsHoverExplainHideTimer.Stop();
            _lyricsHoverExplainHideTimer.Tick -= OnLyricsHoverExplainHideTimerTick;
            _lyricsHoverExplainHideTimer = null;
        }

        if (_lyricsVm != null)
        {
            _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
            _lyricsVm.PlaybackState.PropertyChanged -= OnPlaybackStateChanged;
        }

        NowPlayingCanvas.SeekRequested -= OnSeekRequested;
        NowPlayingCanvas.SetIsPlaying(false);
        NowPlayingCanvas.SetRenderingActive(false);
        if (!_lyricsCanvasDataCleared)
            NowPlayingCanvas.SetLyricsData(null);
        NowPlayingCanvas.Visibility = Visibility.Collapsed;
        _appliedLyricsData = null;
        _appliedSongInfo = null;
        _lyricsCanvasDataCleared = true;

        _lyricsInitialized = false;
    }

    // ── Playback state → Timer sync ──

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.IsPlaying))
        {
            SyncNowPlayingCanvasPosition();
            UpdateTimerState();
            UpdatePodcastChapterTimelineTimerState();
            UpdateDetailsLyricsUpdateMode();
        }
        else if (e.PropertyName is nameof(IPlaybackStateService.Position))
        {
            SyncNowPlayingCanvasPosition();
            UpdatePodcastChapterTimelineProgress();
        }

        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
                           or nameof(IPlaybackStateService.CurrentArtistId))
        {
            _lastDetailsSnippetUpdateTickMs = -1;
            _lastSnippetLineIndex = -1;
            // Track changes arrive AFTER CurrentAlbumArt updates (see PlaybackStateService),
            // so the in-flight color extraction kicked off by the AlbumArt change is still
            // racing here. Don't reset the tint state — that would cancel the extraction and
            // strand the panel on the fallback color. Just clear the canvas treatment.
            ApplyDetailsBackground(null, false);
            UpdateBackgroundChrome();
            UpdatePodcastChapterTimelineTimerState();
        }

        // Update the shared media treatment when playback visuals change.
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentAlbumArtLarge)
                           or nameof(IPlaybackStateService.CurrentAlbumArt))
        {
            RefreshBackgroundTint();
            UpdateBackgroundChrome();
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

        SyncDetailsAiMeaningForCurrentTrack(showLyricsSnippet);
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
        UpdateBackgroundMediaVisibility();

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

        if (showCanvas)
        {
            var lyrics = _lyricsVm.CurrentLyrics!;
            if (!ReferenceEquals(_appliedLyricsData, lyrics))
            {
                NowPlayingCanvas.SetLyricsData(lyrics);
                _appliedLyricsData = lyrics;
                _lyricsCanvasDataCleared = false;
            }

            var songInfo = _lyricsVm.CurrentSongInfo;
            if (!ReferenceEquals(_appliedSongInfo, songInfo))
            {
                NowPlayingCanvas.SetSongInfo(songInfo);
                _appliedSongInfo = songInfo;
            }

            NowPlayingCanvas.SetIsPlaying(_lyricsVm.PlaybackState.IsPlaying);
            var position = _lyricsVm.GetInterpolatedPosition();
            _lastCanvasPositionMs = position.TotalMilliseconds;
            NowPlayingCanvas.SetPosition(position);
        }
        else
        {
            // No lyrics and not loading — clear stale engine data so a subsequent
            // successful load doesn't accidentally composite on top of an old frame.
            if (!_lyricsCanvasDataCleared)
            {
                NowPlayingCanvas.SetLyricsData(null);
                _appliedLyricsData = null;
                _appliedSongInfo = null;
                _lyricsCanvasDataCleared = true;
            }

            NowPlayingCanvas.SetIsPlaying(false);
        }

        UpdateTimerState();
        UpdatePodcastChapterTimelineTimerState();
    }

    private void UpdateTimerState()
    {
        var shouldRunPodcastTimeline = ShouldRunPodcastChapterTimelineTimer();

        if (_lyricsVm == null)
        {
            if (shouldRunPodcastTimeline)
                _positionTimer?.Start();
            else
                _positionTimer?.Stop();

            NowPlayingCanvas.SetRenderingActive(false);
            return;
        }

        var canRender = SelectedMode == RightPanelMode.Lyrics
                        && Visibility == Visibility.Visible
                        && _lyricsVm.HasLyrics
                        && _lyricsVm.CurrentLyrics != null;

        var shouldRunLyricsTimelineSync = ShouldRunLyricsTimelineSyncTimer();
        var shouldRunSharedTimer = ShouldRunDetailsLyricsSharedTimer()
                                   || shouldRunPodcastTimeline
                                   || shouldRunLyricsTimelineSync;

        // Keep rendering active only for realtime playback or direct user interaction.
        var isInteracting = NowPlayingCanvas.IsMouseInLyricsArea
                            || NowPlayingCanvas.IsMousePressing
                            || NowPlayingCanvas.IsMouseScrolling;
        var shouldRender = canRender && (_lyricsVm.PlaybackState.IsPlaying || isInteracting);

        NowPlayingCanvas.SetRenderingActive(shouldRender);
        NowPlayingCanvas.SetIsPlaying(canRender && _lyricsVm.PlaybackState.IsPlaying);

        if (shouldRunSharedTimer)
            _positionTimer?.Start();
        else
            _positionTimer?.Stop();

        if (!canRender)
        {
            _scrollResetTimer?.Stop();
            NowPlayingCanvas.SetIsPlaying(false);
            _lastCanvasPositionMs = -1;
        }
        else if (!_lyricsVm.PlaybackState.IsPlaying)
        {
            NowPlayingCanvas.SetIsPlaying(false);
            _lastCanvasPositionMs = -1;
        }

        if (!ShouldRunDetailsLyricsSharedTimer())
            _lastDetailsSnippetUpdateTickMs = -1;
    }

    private void OnPositionTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (ShouldRunPodcastChapterTimelineTimer())
            UpdatePodcastChapterTimelineProgress();

        if (ShouldRunLyricsTimelineSyncTimer())
            SyncNowPlayingCanvasPosition();

        if (_lyricsVm == null || !ShouldRunDetailsLyricsSharedTimer())
            return;

        var tickMs = Environment.TickCount64;
        if (_lastDetailsSnippetUpdateTickMs >= 0
            && tickMs - _lastDetailsSnippetUpdateTickMs < DetailsSnippetTickMs)
        {
            return;
        }

        _lastDetailsSnippetUpdateTickMs = tickMs;
        UpdateLyricsSnippetText();
    }

    // ── Seek ──

    private void OnSeekRequested(object? sender, TimeSpan position)
    {
        if (_lyricsInitialized && SelectedMode == RightPanelMode.Lyrics)
            NowPlayingCanvas.SetPosition(position);

        _lyricsVm?.PlaybackState.Seek(position.TotalMilliseconds);
    }

    private void SyncNowPlayingCanvasPosition()
    {
        if (!_lyricsInitialized
            || _lyricsVm == null
            || SelectedMode != RightPanelMode.Lyrics
            || Visibility != Visibility.Visible
            || !_lyricsVm.HasLyrics
            || _lyricsVm.CurrentLyrics == null)
        {
            return;
        }

        var position = _lyricsVm.GetInterpolatedPosition();
        _lastCanvasPositionMs = position.TotalMilliseconds;
        NowPlayingCanvas.SetPosition(position);
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
        QueueHideLyricsHoverExplainButton();
        UpdateTimerState();
    }

    private void LyricsOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LyricsInteractionOverlay).Position;
        NowPlayingCanvas.MousePosition = point;
        UpdateLyricsHoverExplainButton(point);
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
        UpdateLyricsHoverExplainButton(point.Position);

        // Auto-resume after 3s of no scrolling
        _scrollResetTimer ??= CreateScrollResetTimer();
        _scrollResetTimer.Stop();
        _scrollResetTimer.Interval = TimeSpan.FromSeconds(3);
        _scrollResetTimer.Start();

        e.Handled = true;
    }

    private async void LyricsHoverExplainButton_Click(object sender, RoutedEventArgs e)
    {
        var lineIndex = _lyricsHoverExplainLineIndex;
        HideLyricsHoverExplainButton();

        if (lineIndex < 0 || RightPanelLyricsAi?.ViewModel is null)
            return;

        await RightPanelLyricsAi.ViewModel.ExplainLineAtIndexAsync(lineIndex);
    }

    private void UpdateLyricsHoverExplainButton(Point pointer)
    {
        StopLyricsHoverExplainHideTimer();

        if (SelectedMode != RightPanelMode.Lyrics
            || _lyricsVm?.HasLyrics != true
            || _lyricsVm.CurrentLyrics is null)
        {
            HideLyricsHoverExplainButton();
            return;
        }

        if (!NowPlayingCanvas.TryRefreshHoveringLine(out var lineIndex, out var lineBounds))
        {
            if (IsInsideLyricsHoverExplainHitRect(pointer) || LyricsHoverExplainButton?.IsPointerOver == true)
                return;

            QueueHideLyricsHoverExplainButton();
            return;
        }

        _lyricsHoverExplainLineIndex = lineIndex;

        const double buttonSize = 34;
        var maxX = Math.Max(0, LyricsInteractionOverlay.ActualWidth - buttonSize);
        var maxY = Math.Max(0, LyricsInteractionOverlay.ActualHeight - buttonSize);
        var x = lineBounds.Right + 10;
        if (x > maxX)
            x = lineBounds.Left - buttonSize - 10;

        var y = lineBounds.Top + ((lineBounds.Height - buttonSize) / 2);
        if (double.IsNaN(x) || double.IsInfinity(x))
            x = pointer.X + 12;
        if (double.IsNaN(y) || double.IsInfinity(y))
            y = pointer.Y - (buttonSize / 2);

        x = Math.Clamp(x, 0, maxX);
        y = Math.Clamp(y, 0, maxY);

        LyricsHoverExplainButton.Margin = new Thickness(x, y, 0, 0);
        LyricsHoverExplainButton.Visibility = Visibility.Visible;

        const double stickyPadding = 24;
        var left = Math.Min(lineBounds.Left, x);
        var top = Math.Min(lineBounds.Top, y);
        var right = Math.Max(lineBounds.Right, x + buttonSize);
        var bottom = Math.Max(lineBounds.Bottom, y + buttonSize);
        _lyricsHoverExplainHitRect = new Rect(
            left - stickyPadding,
            top - stickyPadding,
            Math.Max(1, right - left + (stickyPadding * 2)),
            Math.Max(1, bottom - top + (stickyPadding * 2)));
    }

    private void HideLyricsHoverExplainButton()
    {
        StopLyricsHoverExplainHideTimer();
        _lyricsHoverExplainLineIndex = -1;
        _lyricsHoverExplainHitRect = Rect.Empty;
        if (LyricsHoverExplainButton != null)
            LyricsHoverExplainButton.Visibility = Visibility.Collapsed;
    }

    private void QueueHideLyricsHoverExplainButton()
    {
        _lyricsHoverExplainHideTimer ??= CreateLyricsHoverExplainHideTimer();
        _lyricsHoverExplainHideTimer.Stop();
        _lyricsHoverExplainHideTimer.Start();
    }

    private DispatcherQueueTimer CreateLyricsHoverExplainHideTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(350);
        timer.Tick += OnLyricsHoverExplainHideTimerTick;
        return timer;
    }

    private void OnLyricsHoverExplainHideTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (LyricsHoverExplainButton?.IsPointerOver == true)
            return;

        HideLyricsHoverExplainButton();
    }

    private void StopLyricsHoverExplainHideTimer()
        => _lyricsHoverExplainHideTimer?.Stop();

    private bool IsInsideLyricsHoverExplainHitRect(Point point)
        => _lyricsHoverExplainHitRect.Width > 0
           && _lyricsHoverExplainHitRect.Height > 0
           && point.X >= _lyricsHoverExplainHitRect.X
           && point.X <= _lyricsHoverExplainHitRect.X + _lyricsHoverExplainHitRect.Width
           && point.Y >= _lyricsHoverExplainHitRect.Y
           && point.Y <= _lyricsHoverExplainHitRect.Y + _lyricsHoverExplainHitRect.Height;

    private void LyricsHoverExplainButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        => StopLyricsHoverExplainHideTimer();

    private void LyricsHoverExplainButton_PointerExited(object sender, PointerRoutedEventArgs e)
        => QueueHideLyricsHoverExplainButton();

    private DispatcherQueueTimer CreateScrollResetTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += OnScrollResetTimerTick;
        return timer;
    }

    private void OnScrollResetTimerTick(DispatcherQueueTimer sender, object args)
    {
        ResumeSync();
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
        HideLyricsHoverExplainButton();
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
        if (SelectedMode != RightPanelMode.Lyrics || !_lyricsInitialized || !_isOpenCached)
            return;

        var w = RootGrid.ActualWidth;
        var h = RootGrid.ActualHeight;

        // If layout hasn't measured the grid yet (common when we're called from
        // ApplyCurrentLyricsState right as the panel/tab becomes visible), retry on the
        // dispatcher after the current layout pass instead of forcing UpdateLayout().
        // Re-entering layout from here can trigger layout cycles and fail-fast exits.
        if (w <= 0 || h <= 0)
        {
            ScheduleCanvasLayoutRetry();
            return;
        }

        // Final fallback: use the control's explicit Width if layout still hasn't resolved.
        // RightPanelView has `Width = PanelWidth` hard-coded in the constructor, so this is
        // always a valid non-zero value. Height fallback uses the app window height proxy
        // from the parent (ActualHeight of RightPanelView itself).
        if (w <= 0) w = Width;
        if (h <= 0) h = ActualHeight;

        if (w <= 0 || h <= 0)
        {
            ScheduleCanvasLayoutRetry();
#if DEBUG
            System.Diagnostics.Debug.WriteLine(
                $"[RightPanel] UpdateCanvasLayout BAILED rootW={RootGrid.ActualWidth} rootH={RootGrid.ActualHeight} " +
                $"ctrlW={Width} ctrlH={ActualHeight}");
#endif
            return;
        }

        _pendingCanvasLayoutRetry = false;

        // Canvas spans the entire root; reserve the tab rail at the top.
        var resizerW = PanelResizer.ActualWidth;
        var tabH = TabHeader.ActualHeight;
        const double padLeft = 12, padRight = 12, padBottom = 12;
        const double topGap = 8;

        var explainButtonGutter = w >= 280 ? 52 : 0;
        var lyricsW = w - resizerW - padLeft - padRight - explainButtonGutter;
        var lyricsH = h - tabH - topGap - padBottom;

        NowPlayingCanvas.LyricsStartX = resizerW + padLeft;
        NowPlayingCanvas.LyricsStartY = tabH + topGap;
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

    private void ScheduleCanvasLayoutRetry()
    {
        if (_pendingCanvasLayoutRetry || DispatcherQueue == null)
            return;

        // Don't schedule when the panel is closed — Visibility=Collapsed means
        // RootGrid.ActualWidth/Height are 0, UpdateCanvasLayout would bail and
        // re-enqueue itself, producing an infinite low-priority retry loop that
        // ate ~8% UI CPU. The retry is kicked manually from OnIsOpenChanged
        // when the panel re-opens, so we don't lose the canvas-sizing pass.
        if (!_isOpenCached)
            return;

        _pendingCanvasLayoutRetry = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded || !_isOpenCached)
            {
                _pendingCanvasLayoutRetry = false;
                return;
            }

            UpdateCanvasLayout();

            _pendingCanvasLayoutRetry = false;

            // If layout succeeded and we're in lyrics mode, re-apply lyrics state
            // so SetLyricsData is called with the now-correct canvas dimensions.
            // (On first Lyrics tab open, UpdateCanvasLayout bails because the panel
            // hasn't been measured yet — by the time this deferred callback fires the
            // layout pass has completed, so we can now push the real dimensions into
            // the engine and re-hand it the lyrics data.)
            if (NowPlayingCanvas.LyricsWidth > 0 && SelectedMode == RightPanelMode.Lyrics)
                ApplyCurrentLyricsState();
        });
    }

    private void UpdateCanvasClearColor()
    {
        if (IsEmbeddedChromeTransparent)
        {
            NowPlayingCanvas.SetClearColor(Colors.Transparent);
            return;
        }

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

    private void UpdatePanelBackgroundState()
    {
        var hasDetailsData = _detailsVm?.HasData == true;
        ApplyDetailsBackground(
            hasDetailsData ? _detailsVm?.CanvasUrl : null,
            hasDetailsData && _detailsVm?.HasCanvas == true);
        UpdateDetailsCanvasSyncBadge();
    }

    private void UpdateDetailsCanvasSyncBadge()
    {
        if (DetailsCanvasSyncBadge == null)
            return;

        var show = SelectedMode == RightPanelMode.Details
                   && _detailsVm?.HasData == true
                   && _detailsVm.HasPendingCanvasUpdate;

        DetailsCanvasSyncBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBackgroundChrome()
    {
        EnsureBackgroundOverlayComposition();

        if (_backgroundTintBrush == null
            || _backgroundNonDetailsDimBrush == null
            || _backgroundHighlightStartStop == null
            || _backgroundHighlightMidStop == null
            || _backgroundHighlightEndStop == null
            || _backgroundScrimTopStop == null
            || _backgroundScrimMidStop == null
            || _backgroundScrimBottomStop == null
            || _backgroundBottomBlendTopStop == null
            || _backgroundBottomBlendMidStop == null
            || _backgroundBottomBlendLowerMidStop == null
            || _backgroundBottomBlendBottomStop == null
            || _backgroundTopBlendTopStop == null
            || _backgroundTopBlendMidStop == null
            || _backgroundTopBlendLowerMidStop == null
            || _backgroundTopBlendBottomStop == null)
        {
            return;
        }

        var tintColor = GetBackgroundTintColor();
        var surfaceColor = GetPanelSurfaceColor();
        var blendColor = BlendColors(
            surfaceColor,
            tintColor,
            ActualTheme == ElementTheme.Light ? 0.32f : 0.44f);
        var bottomColor = Darken(
            blendColor,
            ActualTheme == ElementTheme.Light ? 0.08f : 0.22f);

        _backgroundTintBrush.Color = tintColor;
        _backgroundNonDetailsDimBrush.Color = ResolveThemeColor(
            "RightPanelBackgroundNonDetailsDimBrush",
            ActualTheme == ElementTheme.Light
                ? Color.FromArgb(255, 10, 12, 16)
                : Color.FromArgb(255, 9, 11, 17));

        _backgroundHighlightStartStop.Color = ResolveThemeColor(
            "RightPanelBackgroundHighlightStartColor",
            Color.FromArgb(86, 255, 255, 255));
        _backgroundHighlightMidStop.Color = ResolveThemeColor(
            "RightPanelBackgroundHighlightMidColor",
            Color.FromArgb(22, 255, 255, 255));
        _backgroundHighlightEndStop.Color = ResolveThemeColor(
            "RightPanelBackgroundHighlightEndColor",
            Color.FromArgb(0, 255, 255, 255));

        _backgroundScrimTopStop.Color = ResolveThemeColor(
            "RightPanelBackgroundShadowTopColor",
            Color.FromArgb(24, 0, 0, 0));
        _backgroundScrimMidStop.Color = ResolveThemeColor(
            "RightPanelBackgroundShadowMidColor",
            Color.FromArgb(8, 0, 0, 0));
        _backgroundScrimBottomStop.Color = ResolveThemeColor(
            "RightPanelBackgroundShadowBottomColor",
            Color.FromArgb(110, 0, 0, 0));

        _backgroundBottomBlendTopStop.Color = Color.FromArgb(0, bottomColor.R, bottomColor.G, bottomColor.B);
        _backgroundBottomBlendMidStop.Color = Color.FromArgb(20, bottomColor.R, bottomColor.G, bottomColor.B);
        _backgroundBottomBlendLowerMidStop.Color = Color.FromArgb(86, bottomColor.R, bottomColor.G, bottomColor.B);
        _backgroundBottomBlendBottomStop.Color = Color.FromArgb(255, bottomColor.R, bottomColor.G, bottomColor.B);

        _backgroundTopBlendTopStop.Color = Color.FromArgb(255, bottomColor.R, bottomColor.G, bottomColor.B);
        _backgroundTopBlendMidStop.Color = Color.FromArgb(86, bottomColor.R, bottomColor.G, bottomColor.B);
        _backgroundTopBlendLowerMidStop.Color = Color.FromArgb(20, bottomColor.R, bottomColor.G, bottomColor.B);
        _backgroundTopBlendBottomStop.Color = Color.FromArgb(0, bottomColor.R, bottomColor.G, bottomColor.B);
        UpdateBackgroundOverlayState();
    }

    private void UpdateBackgroundOverlayState()
    {
        if (_backgroundOverlayContainer == null)
            return;

        if (IsEmbeddedChromeTransparent)
        {
            if (_backgroundTintVisual != null) _backgroundTintVisual.Opacity = 0f;
            if (_backgroundHighlightVisual != null) _backgroundHighlightVisual.Opacity = 0f;
            if (_backgroundScrimVisual != null) _backgroundScrimVisual.Opacity = 0f;
            if (_backgroundNonDetailsDimVisual != null) _backgroundNonDetailsDimVisual.Opacity = 0f;
            if (_backgroundBottomBlendVisual != null) _backgroundBottomBlendVisual.Opacity = 0f;
            if (_backgroundTopBlendVisual != null) _backgroundTopBlendVisual.Opacity = 0f;
            return;
        }

        var showDetailsCanvasChrome = SelectedMode == RightPanelMode.Details
                                      && _activeBackgroundMode == DetailsBackgroundMode.Canvas
                                      && DetailsCanvasImage.Visibility == Visibility.Visible;

        if (_backgroundTintVisual != null)
            _backgroundTintVisual.Opacity = showDetailsCanvasChrome ? 0.10f : 0f;
        if (_backgroundHighlightVisual != null)
            _backgroundHighlightVisual.Opacity = showDetailsCanvasChrome ? 0.52f : 0f;
        if (_backgroundScrimVisual != null)
            _backgroundScrimVisual.Opacity = showDetailsCanvasChrome ? 0.74f : 0f;
        if (_backgroundBottomBlendVisual != null)
            _backgroundBottomBlendVisual.Opacity = showDetailsCanvasChrome ? 0.88f : 0f;
        if (_backgroundTopBlendVisual != null)
            _backgroundTopBlendVisual.Opacity = showDetailsCanvasChrome ? 0.64f : 0f;
        if (_backgroundNonDetailsDimVisual != null)
            _backgroundNonDetailsDimVisual.Opacity = 0f;
    }

    private void UpdateBackgroundMediaVisibility()
    {
        if (DetailsCanvasImage == null || BackgroundOverlayHost == null)
            return;

        var hasMedia = HasResolvedBackgroundSource();
        var isDetails = SelectedMode == RightPanelMode.Details;
        var showMedia = isDetails
                        && _activeBackgroundMode == DetailsBackgroundMode.Canvas
                        && hasMedia;
        var showChrome = showMedia;

        DetailsCanvasImage.Visibility = showMedia ? Visibility.Visible : Visibility.Collapsed;
        BackgroundOverlayHost.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;

        EnsureBackgroundOverlayComposition();
        if (_backgroundOverlayContainer != null)
            _backgroundOverlayContainer.Opacity = showChrome ? 1f : 0f;

        if (_canvasMediaPlayer != null)
        {
            if (showMedia)
                _canvasMediaPlayer.Play();
            else
                _canvasMediaPlayer.Pause();
        }

        UpdateBackgroundOverlayState();
    }

    private void EnsureBackgroundOverlayComposition()
    {
        if (_backgroundOverlayContainer != null || BackgroundOverlayHost == null)
            return;

        var hostVisual = ElementCompositionPreview.GetElementVisual(BackgroundOverlayHost);
        _backgroundOverlayCompositor = hostVisual.Compositor;

        _backgroundOverlayContainer = TrackCompositionObject(_backgroundOverlayCompositor.CreateContainerVisual());
        _backgroundOverlayContainer.RelativeSizeAdjustment = Vector2.One;
        _backgroundOverlayContainer.Opacity = 0f;

        _backgroundTintBrush = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorBrush(Colors.Transparent));
        _backgroundTintVisual = TrackCompositionObject(_backgroundOverlayCompositor.CreateSpriteVisual());
        _backgroundTintVisual.Brush = _backgroundTintBrush;
        _backgroundTintVisual.RelativeSizeAdjustment = Vector2.One;
        _backgroundOverlayContainer.Children.InsertAtBottom(_backgroundTintVisual);

        _backgroundHighlightBrush = TrackCompositionObject(_backgroundOverlayCompositor.CreateLinearGradientBrush());
        _backgroundHighlightBrush.StartPoint = new Vector2(0.08f, 0f);
        _backgroundHighlightBrush.EndPoint = new Vector2(0.82f, 0.5f);
        _backgroundHighlightStartStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0f, Colors.Transparent));
        _backgroundHighlightMidStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0.44f, Colors.Transparent));
        _backgroundHighlightEndStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(1f, Colors.Transparent));
        _backgroundHighlightBrush.ColorStops.Add(_backgroundHighlightStartStop);
        _backgroundHighlightBrush.ColorStops.Add(_backgroundHighlightMidStop);
        _backgroundHighlightBrush.ColorStops.Add(_backgroundHighlightEndStop);
        _backgroundHighlightVisual = TrackCompositionObject(_backgroundOverlayCompositor.CreateSpriteVisual());
        _backgroundHighlightVisual.Brush = _backgroundHighlightBrush;
        _backgroundHighlightVisual.RelativeSizeAdjustment = Vector2.One;
        _backgroundOverlayContainer.Children.InsertAtTop(_backgroundHighlightVisual);

        _backgroundScrimBrush = TrackCompositionObject(_backgroundOverlayCompositor.CreateLinearGradientBrush());
        _backgroundScrimBrush.StartPoint = new Vector2(0.5f, 0f);
        _backgroundScrimBrush.EndPoint = new Vector2(0.5f, 1f);
        _backgroundScrimTopStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0f, Colors.Transparent));
        _backgroundScrimMidStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0.42f, Colors.Transparent));
        _backgroundScrimBottomStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(1f, Colors.Transparent));
        _backgroundScrimBrush.ColorStops.Add(_backgroundScrimTopStop);
        _backgroundScrimBrush.ColorStops.Add(_backgroundScrimMidStop);
        _backgroundScrimBrush.ColorStops.Add(_backgroundScrimBottomStop);
        _backgroundScrimVisual = TrackCompositionObject(_backgroundOverlayCompositor.CreateSpriteVisual());
        _backgroundScrimVisual.Brush = _backgroundScrimBrush;
        _backgroundScrimVisual.RelativeSizeAdjustment = Vector2.One;
        _backgroundOverlayContainer.Children.InsertAtTop(_backgroundScrimVisual);

        _backgroundNonDetailsDimBrush = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorBrush(Colors.Transparent));
        _backgroundNonDetailsDimVisual = TrackCompositionObject(_backgroundOverlayCompositor.CreateSpriteVisual());
        _backgroundNonDetailsDimVisual.Brush = _backgroundNonDetailsDimBrush;
        _backgroundNonDetailsDimVisual.RelativeSizeAdjustment = Vector2.One;
        _backgroundOverlayContainer.Children.InsertAtTop(_backgroundNonDetailsDimVisual);

        _backgroundBottomBlendBrush = TrackCompositionObject(_backgroundOverlayCompositor.CreateLinearGradientBrush());
        _backgroundBottomBlendBrush.StartPoint = new Vector2(0.5f, 0f);
        _backgroundBottomBlendBrush.EndPoint = new Vector2(0.5f, 1f);
        _backgroundBottomBlendTopStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0f, Colors.Transparent));
        _backgroundBottomBlendMidStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0.18f, Colors.Transparent));
        _backgroundBottomBlendLowerMidStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0.62f, Colors.Transparent));
        _backgroundBottomBlendBottomStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(1f, Colors.Transparent));
        _backgroundBottomBlendBrush.ColorStops.Add(_backgroundBottomBlendTopStop);
        _backgroundBottomBlendBrush.ColorStops.Add(_backgroundBottomBlendMidStop);
        _backgroundBottomBlendBrush.ColorStops.Add(_backgroundBottomBlendLowerMidStop);
        _backgroundBottomBlendBrush.ColorStops.Add(_backgroundBottomBlendBottomStop);
        _backgroundBottomBlendVisual = TrackCompositionObject(_backgroundOverlayCompositor.CreateSpriteVisual());
        _backgroundBottomBlendVisual.Brush = _backgroundBottomBlendBrush;
        _backgroundBottomBlendVisual.RelativeSizeAdjustment = new Vector2(1f, 0.42f);
        _backgroundBottomBlendVisual.RelativeOffsetAdjustment = new Vector3(0f, 0.58f, 0f);
        _backgroundOverlayContainer.Children.InsertAtTop(_backgroundBottomBlendVisual);

        _backgroundTopBlendBrush = TrackCompositionObject(_backgroundOverlayCompositor.CreateLinearGradientBrush());
        _backgroundTopBlendBrush.StartPoint = new Vector2(0.5f, 0f);
        _backgroundTopBlendBrush.EndPoint = new Vector2(0.5f, 1f);
        _backgroundTopBlendTopStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0f, Colors.Transparent));
        _backgroundTopBlendMidStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0.38f, Colors.Transparent));
        _backgroundTopBlendLowerMidStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(0.82f, Colors.Transparent));
        _backgroundTopBlendBottomStop = TrackCompositionObject(_backgroundOverlayCompositor.CreateColorGradientStop(1f, Colors.Transparent));
        _backgroundTopBlendBrush.ColorStops.Add(_backgroundTopBlendTopStop);
        _backgroundTopBlendBrush.ColorStops.Add(_backgroundTopBlendMidStop);
        _backgroundTopBlendBrush.ColorStops.Add(_backgroundTopBlendLowerMidStop);
        _backgroundTopBlendBrush.ColorStops.Add(_backgroundTopBlendBottomStop);
        _backgroundTopBlendVisual = TrackCompositionObject(_backgroundOverlayCompositor.CreateSpriteVisual());
        _backgroundTopBlendVisual.Brush = _backgroundTopBlendBrush;
        _backgroundTopBlendVisual.RelativeSizeAdjustment = new Vector2(1f, 0.35f);
        _backgroundOverlayContainer.Children.InsertAtTop(_backgroundTopBlendVisual);

        ElementCompositionPreview.SetElementChildVisual(BackgroundOverlayHost, _backgroundOverlayContainer);
    }

    private void TeardownBackgroundOverlayComposition()
    {
        if (BackgroundOverlayHost != null)
            ElementCompositionPreview.SetElementChildVisual(BackgroundOverlayHost, null);

        for (int i = _backgroundOverlayCompositionObjects.Count - 1; i >= 0; i--)
            _backgroundOverlayCompositionObjects[i].Dispose();
        _backgroundOverlayCompositionObjects.Clear();

        _backgroundOverlayContainer = null;
        _backgroundOverlayCompositor = null;
        _backgroundTintBrush = null;
        _backgroundNonDetailsDimBrush = null;
        _backgroundHighlightBrush = null;
        _backgroundHighlightStartStop = null;
        _backgroundHighlightMidStop = null;
        _backgroundHighlightEndStop = null;
        _backgroundScrimBrush = null;
        _backgroundScrimTopStop = null;
        _backgroundScrimMidStop = null;
        _backgroundScrimBottomStop = null;
        _backgroundBottomBlendBrush = null;
        _backgroundBottomBlendTopStop = null;
        _backgroundBottomBlendMidStop = null;
        _backgroundBottomBlendLowerMidStop = null;
        _backgroundBottomBlendBottomStop = null;
        _backgroundTopBlendBrush = null;
        _backgroundTopBlendTopStop = null;
        _backgroundTopBlendMidStop = null;
        _backgroundTopBlendLowerMidStop = null;
        _backgroundTopBlendBottomStop = null;
        _backgroundTintVisual = null;
        _backgroundHighlightVisual = null;
        _backgroundScrimVisual = null;
        _backgroundNonDetailsDimVisual = null;
        _backgroundBottomBlendVisual = null;
        _backgroundTopBlendVisual = null;
    }

    private T TrackCompositionObject<T>(T compositionObject) where T : CompositionObject
    {
        _backgroundOverlayCompositionObjects.Add(compositionObject);
        return compositionObject;
    }

    // ── Tab content bottom fade ──
    // A single composition-backed vertical gradient that overlays all right-panel tabs
    // and fades the scrolling content into the panel background, so the last row bleeds
    // cleanly into the player bar below.

    private T TrackTabFade<T>(T obj) where T : CompositionObject
    {
        _tabFadeCompositionObjects.Add(obj);
        return obj;
    }

    private void EnsureTabContentFadeComposition()
    {
        if (_tabFadeVisual != null || TabContentFadeHost == null)
            return;

        var hostVisual = ElementCompositionPreview.GetElementVisual(TabContentFadeHost);
        _tabFadeCompositor = hostVisual.Compositor;

        _tabFadeContainer = TrackTabFade(_tabFadeCompositor.CreateContainerVisual());
        _tabFadeContainer.RelativeSizeAdjustment = Vector2.One;

        var backdropBrush = TrackTabFade(_tabFadeCompositor.CreateBackdropBrush());
        using var blurEffect = new GaussianBlurEffect
        {
            Name = "TabContentBackdropBlur",
            Source = new CompositionEffectSourceParameter("Backdrop"),
            BlurAmount = 14f,
            BorderMode = EffectBorderMode.Hard
        };
        var blurFactory = TrackTabFade(_tabFadeCompositor.CreateEffectFactory(blurEffect));
        _tabBlurBrush = TrackTabFade(blurFactory.CreateBrush());
        _tabBlurBrush.SetSourceParameter("Backdrop", backdropBrush);

        _tabBlurVisual = TrackTabFade(_tabFadeCompositor.CreateSpriteVisual());
        _tabBlurVisual.Brush = _tabBlurBrush;
        _tabBlurVisual.RelativeSizeAdjustment = Vector2.One;
        _tabFadeContainer.Children.InsertAtBottom(_tabBlurVisual);

        _tabFadeBrush = TrackTabFade(_tabFadeCompositor.CreateLinearGradientBrush());
        _tabFadeBrush.StartPoint = new Vector2(0.5f, 0f);
        _tabFadeBrush.EndPoint   = new Vector2(0.5f, 1f);

        // 4-stop gradient — same cadence as _backgroundBottomBlendBrush for visual consistency.
        _tabFadeStop0 = TrackTabFade(_tabFadeCompositor.CreateColorGradientStop(0.00f, Colors.Transparent));
        _tabFadeStop1 = TrackTabFade(_tabFadeCompositor.CreateColorGradientStop(0.35f, Colors.Transparent));
        _tabFadeStop2 = TrackTabFade(_tabFadeCompositor.CreateColorGradientStop(0.72f, Colors.Transparent));
        _tabFadeStop3 = TrackTabFade(_tabFadeCompositor.CreateColorGradientStop(1.00f, Colors.Transparent));
        _tabFadeBrush.ColorStops.Add(_tabFadeStop0);
        _tabFadeBrush.ColorStops.Add(_tabFadeStop1);
        _tabFadeBrush.ColorStops.Add(_tabFadeStop2);
        _tabFadeBrush.ColorStops.Add(_tabFadeStop3);

        _tabFadeVisual = TrackTabFade(_tabFadeCompositor.CreateSpriteVisual());
        _tabFadeVisual.Brush = _tabFadeBrush;
        _tabFadeVisual.RelativeSizeAdjustment = Vector2.One;
        _tabFadeContainer.Children.InsertAtTop(_tabFadeVisual);

        ElementCompositionPreview.SetElementChildVisual(TabContentFadeHost, _tabFadeContainer);

        UpdateTabContentFadeColor();
    }

    private void UpdateTabContentFadeColor()
    {
        if (_tabFadeStop0 == null || _tabFadeStop1 == null
            || _tabFadeStop2 == null || _tabFadeStop3 == null)
            return;

        var target = ResolveTabFadeTargetColor();
        if (_tabBlurVisual != null)
            _tabBlurVisual.Opacity = IsEmbeddedChromeTransparent ? 0.28f : 0f;

        // Carry the target RGB on every stop so the gradient interpolation stays
        // in the correct hue rather than fading through neutral gray.
        if (IsEmbeddedChromeTransparent)
        {
            _tabFadeStop0.Color = Color.FromArgb(0, target.R, target.G, target.B);
            _tabFadeStop1.Color = Color.FromArgb(18, target.R, target.G, target.B);
            _tabFadeStop2.Color = Color.FromArgb(92, target.R, target.G, target.B);
            _tabFadeStop3.Color = Color.FromArgb(185, target.R, target.G, target.B);
            return;
        }

        _tabFadeStop0.Color = Color.FromArgb(0, target.R, target.G, target.B);
        _tabFadeStop1.Color = Color.FromArgb(40, target.R, target.G, target.B);
        _tabFadeStop2.Color = Color.FromArgb(190, target.R, target.G, target.B);
        _tabFadeStop3.Color = Color.FromArgb(255, target.R, target.G, target.B);
    }

    private Windows.UI.Color ResolveTabFadeTargetColor()
    {
        if (IsEmbeddedChromeTransparent)
            return ResolveEmbeddedTabFadeTargetColor();

        // Mirror UpdateCanvasClearColor() so the fade's terminal colour matches the
        // panel's effective background in both themes.
        var cardColor = (_themeColors?.CardBackground as SolidColorBrush)?.Color;

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
                : Color.FromArgb(255,  32,  32,  32);
        }

        if (cardColor is { } card && card.A > 0)
        {
            float a = card.A / 255f;
            return Color.FromArgb(255,
                (byte)(card.R * a + baseColor.R * (1 - a)),
                (byte)(card.G * a + baseColor.G * (1 - a)),
                (byte)(card.B * a + baseColor.B * (1 - a)));
        }

        return baseColor;
    }

    private Windows.UI.Color ResolveEmbeddedTabFadeTargetColor()
    {
        if (TryParseHexColor(EmbeddedHostTintColor, out var hostTint))
        {
            if (ActualTheme == ElementTheme.Dark)
            {
                return Color.FromArgb(
                    255,
                    (byte)(hostTint.R * 0.30f),
                    (byte)(hostTint.G * 0.30f),
                    (byte)(hostTint.B * 0.30f));
            }

            const float blend = 0.72f;
            return Color.FromArgb(
                255,
                (byte)(hostTint.R * (1 - blend) + 255 * blend),
                (byte)(hostTint.G * (1 - blend) + 255 * blend),
                (byte)(hostTint.B * (1 - blend) + 255 * blend));
        }

        var tintColor = GetBackgroundTintColor();
        return ActualTheme == ElementTheme.Light
            ? BlendColors(Color.FromArgb(255, 244, 246, 248), tintColor, 0.28f)
            : Darken(tintColor, 0.64f);
    }

    private void TeardownTabContentFadeComposition()
    {
        if (TabContentFadeHost != null)
            ElementCompositionPreview.SetElementChildVisual(TabContentFadeHost, null);

        for (int i = _tabFadeCompositionObjects.Count - 1; i >= 0; i--)
            _tabFadeCompositionObjects[i].Dispose();
        _tabFadeCompositionObjects.Clear();

        _tabFadeCompositor = null;
        _tabFadeContainer = null;
        _tabBlurBrush = null;
        _tabBlurVisual = null;
        _tabFadeBrush = null;
        _tabFadeStop0 = null;
        _tabFadeStop1 = null;
        _tabFadeStop2 = null;
        _tabFadeStop3 = null;
        _tabFadeVisual = null;
    }

    private bool HasResolvedBackgroundSource()
    {
        return _activeBackgroundMode switch
        {
            DetailsBackgroundMode.None => false,
            DetailsBackgroundMode.Canvas => _canvasImageSource != null && _canvasMediaPlayer != null,
            _ => false
        };
    }

    private string? GetBackgroundTintHex()
    {
        if (_backgroundTintExtractedColor != null)
        {
            var isLightTheme = ActualTheme == ElementTheme.Light;
            return isLightTheme
                ? _backgroundTintExtractedColor.DarkHex ?? _backgroundTintExtractedColor.RawHex
                : _backgroundTintExtractedColor.LightHex ?? _backgroundTintExtractedColor.RawHex;
        }

        return null;
    }

    private void RefreshBackgroundTint()
    {
        var imageUrl = GetCurrentAlbumArtUrl();
        if (string.IsNullOrEmpty(imageUrl))
        {
            ResetBackgroundTint();
            return;
        }

        if (string.Equals(_backgroundTintImageUrl, imageUrl, StringComparison.Ordinal)
            && _backgroundTintExtractedColor != null)
        {
            UpdateBackgroundChrome();
            UpdateTabContentFadeColor();
            UpdateTabHeaderVisualState();
            return;
        }

        CancelBackgroundTintRefresh();
        _backgroundTintImageUrl = imageUrl;
        _backgroundTintExtractedColor = null;
        UpdateBackgroundChrome();
        UpdateTabContentFadeColor();
        UpdateTabHeaderVisualState();

        if (_colorService == null)
            return;

        _backgroundTintCts = new CancellationTokenSource();
        _ = LoadBackgroundTintAsync(imageUrl, _backgroundTintCts.Token);
    }

    private async Task LoadBackgroundTintAsync(string imageUrl, CancellationToken ct)
    {
        try
        {
            var extracted = await _colorService!.GetColorAsync(imageUrl, ct);
            if (ct.IsCancellationRequested)
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested
                    || !string.Equals(_backgroundTintImageUrl, imageUrl, StringComparison.Ordinal))
                {
                    return;
                }

                _backgroundTintExtractedColor = extracted;
                UpdateBackgroundChrome();
                UpdateTabContentFadeColor();
                UpdateTabHeaderVisualState();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RightPanel] Background tint extraction failed: {ex.Message}");
        }
    }

    private string? GetCurrentAlbumArtUrl()
    {
        return SpotifyImageHelper.ToHttpsUrl(
            _lyricsVm?.PlaybackState.CurrentAlbumArtLarge
            ?? _lyricsVm?.PlaybackState.CurrentAlbumArt);
    }

    private void ResetBackgroundTint()
    {
        CancelBackgroundTintRefresh();
        _backgroundTintImageUrl = null;
        _backgroundTintExtractedColor = null;
        UpdateBackgroundChrome();
        UpdateTabContentFadeColor();
        UpdateTabHeaderVisualState();
    }

    private void CancelBackgroundTintRefresh()
    {
        _backgroundTintCts?.Cancel();
        _backgroundTintCts?.Dispose();
        _backgroundTintCts = null;
    }

    private Color GetBackgroundTintColor()
    {
        var albumHex = GetBackgroundTintHex();
        if (TryParseHexColor(albumHex, out var albumColor))
            return albumColor;

        if (_themeColors?.AccentFill is SolidColorBrush accentBrush)
            return accentBrush.Color;

        return ActualTheme == ElementTheme.Light
            ? Color.FromArgb(255, 110, 132, 148)
            : Color.FromArgb(255, 84, 116, 140);
    }

    private Color GetPanelSurfaceColor()
    {
        if (_themeColors?.CardBackgroundSecondary is SolidColorBrush secondaryBrush)
            return secondaryBrush.Color;

        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var brushObj)
            && brushObj is SolidColorBrush themeBrush)
        {
            return themeBrush.Color;
        }

        return ActualTheme == ElementTheme.Light
            ? Color.FromArgb(255, 245, 245, 245)
            : Color.FromArgb(255, 30, 30, 30);
    }

    private Color ResolveThemeColor(string resourceKey, Color fallback)
    {
        var themeKey = ActualTheme switch
        {
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => "Default"
        };

        if (TryResolveColorFromResources(Application.Current.Resources, resourceKey, themeKey, out var color))
            return color;

        return fallback;
    }

    private static bool TryResolveColorFromResources(
        ResourceDictionary resources,
        string resourceKey,
        string themeKey,
        out Color color)
    {
        if (TryResolveColorFromDictionary(resources, resourceKey, out color))
            return true;

        if (resources.ThemeDictionaries.TryGetValue(themeKey, out var themed)
            && themed is ResourceDictionary themedDict
            && TryResolveColorFromDictionary(themedDict, resourceKey, out color))
        {
            return true;
        }

        if (themeKey != "Default"
            && resources.ThemeDictionaries.TryGetValue("Default", out var fallbackThemed)
            && fallbackThemed is ResourceDictionary fallbackDict
            && TryResolveColorFromDictionary(fallbackDict, resourceKey, out color))
        {
            return true;
        }

        foreach (var merged in resources.MergedDictionaries)
        {
            if (TryResolveColorFromResources(merged, resourceKey, themeKey, out color))
                return true;
        }

        color = Colors.Transparent;
        return false;
    }

    private static bool TryResolveColorFromDictionary(
        ResourceDictionary dictionary,
        string resourceKey,
        out Color color)
    {
        if (dictionary.TryGetValue(resourceKey, out var value))
        {
            switch (value)
            {
                case Color c:
                    color = c;
                    return true;
                case SolidColorBrush brush:
                    color = brush.Color;
                    return true;
            }
        }

        color = Colors.Transparent;
        return false;
    }

    private static Color BlendColors(Color baseColor, Color overlayColor, float overlayWeight)
    {
        overlayWeight = Math.Clamp(overlayWeight, 0f, 1f);
        var baseWeight = 1f - overlayWeight;

        return Color.FromArgb(
            255,
            (byte)Math.Clamp((baseColor.R * baseWeight) + (overlayColor.R * overlayWeight), 0, 255),
            (byte)Math.Clamp((baseColor.G * baseWeight) + (overlayColor.G * overlayWeight), 0, 255),
            (byte)Math.Clamp((baseColor.B * baseWeight) + (overlayColor.B * overlayWeight), 0, 255));
    }

    private static Color Darken(Color color, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        var scale = 1f - amount;
        return Color.FromArgb(
            255,
            (byte)Math.Clamp(color.R * scale, 0, 255),
            (byte)Math.Clamp(color.G * scale, 0, 255),
            (byte)Math.Clamp(color.B * scale, 0, 255));
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var normalized = hex.Trim().TrimStart('#');
        if (normalized.Length == 6
            && byte.TryParse(normalized[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(normalized[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(normalized[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            color = Color.FromArgb(255, r, g, b);
            return true;
        }

        if (normalized.Length == 8
            && byte.TryParse(normalized[..2], System.Globalization.NumberStyles.HexNumber, null, out var a)
            && byte.TryParse(normalized[2..4], System.Globalization.NumberStyles.HexNumber, null, out var r8)
            && byte.TryParse(normalized[4..6], System.Globalization.NumberStyles.HexNumber, null, out var g8)
            && byte.TryParse(normalized[6..8], System.Globalization.NumberStyles.HexNumber, null, out var b8))
        {
            color = Color.FromArgb(a, r8, g8, b8);
            return true;
        }

        return false;
    }


    // ── Tab / visibility management ──

    private void UpdateContentVisibility()
    {
        if (QueueContent == null || !IsLoaded) return;

        var keepLyricsActive = ShouldKeepLyricsVmActive();

        // Materialize the deferred (x:Load="False") subtrees on first visible selection.
        if (keepLyricsActive && SelectedMode == RightPanelMode.Lyrics) EnsureLyricsTreeLoaded();
        if (keepLyricsActive && SelectedMode == RightPanelMode.Details) EnsureDetailsTreeLoaded();
        if (_isOpenCached && SelectedMode == RightPanelMode.TrackDetails) EnsureTrackDetailsTreeLoaded();

        UpdateLyricsConsumerActivity(keepLyricsActive);

        if (keepLyricsActive)
            InitializeLyrics();
        else
            TeardownLyrics();

        QueueContent.Visibility = SelectedMode == RightPanelMode.Queue ? Visibility.Visible : Visibility.Collapsed;
        FriendsContent.Visibility = SelectedMode == RightPanelMode.FriendsActivity ? Visibility.Visible : Visibility.Collapsed;

        // LyricsContent / DetailsContent / TrackDetailsContent are x:Load'd — only touch once materialized.
        if (LyricsContent != null)
            LyricsContent.Visibility = SelectedMode == RightPanelMode.Lyrics ? Visibility.Visible : Visibility.Collapsed;
        if (DetailsContent != null)
            DetailsContent.Visibility = SelectedMode == RightPanelMode.Details ? Visibility.Visible : Visibility.Collapsed;
        if (TrackDetailsContent != null)
            TrackDetailsContent.Visibility = SelectedMode == RightPanelMode.TrackDetails ? Visibility.Visible : Visibility.Collapsed;

        // Keep the temporary TrackDetails tab visible only while the mode is active;
        // tabs it as a segmented-option while it's showing, hides otherwise.
        if (TrackDetailsTabItem != null)
            TrackDetailsTabItem.Visibility = SelectedMode == RightPanelMode.TrackDetails ? Visibility.Visible : Visibility.Collapsed;

        if (_isOpenCached && SelectedMode == RightPanelMode.TrackDetails)
            RefreshTrackDetailsContent();

        UpdatePanelBackgroundState();

        // Background is now shared across the right panel, with Details remaining
        // slightly more immersive via layout rather than exclusive visibility.
        UpdateBackgroundMediaVisibility();

        // Canvas mode: push content to bottom so video is visible
        ApplyCanvasLayout();

        // Details lyrics snippet timer — stop when not on Details tab
        if (SelectedMode != RightPanelMode.Details)
        {
            DetachDetailsLyricsRenderLoop();
            CanvasLyricsOverlay.Visibility = Visibility.Collapsed;
            _canvasLyricsActive = false;
            ClearCanvasLyricOverlay();
        }
        else
        {
            UpdateDetailsLyricsUpdateMode();
        }

        if (_lyricsInitialized)
            ApplyCurrentLyricsState();

        UpdateTabHeaderVisualState();

        // When switching to lyrics tab, ensure we have the latest state
        if (keepLyricsActive && SelectedMode == RightPanelMode.Lyrics && _lyricsInitialized)
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
        if (_isOpenCached && SelectedMode == RightPanelMode.Details && _detailsVm != null)
        {
            _ = LoadAndBindDetailsAsync();
        }

        UpdateTimerState();
    }

    // ── Tab header ──

    // ── Tab header pager + edge fades ───────────────────────────────────────
    // Long localized labels (e.g. Korean "세부 정보") can overflow the panel
    // width. The Segmented sits inside a horizontal ScrollViewer; these
    // handlers drive the chevron pager visibility and the left/right gradient
    // fade overlays based on scroll position.

    private const double TabPagerScrollStepPx = 96;

    private void TabHeaderScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => UpdateTabHeaderEdgeAffordances();

    private void TabHeaderScroller_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTabHeaderEdgeAffordances();

    private void UpdateTabHeaderEdgeAffordances()
    {
        if (TabHeaderScroller == null) return;

        double max = TabHeaderScroller.ScrollableWidth;
        double off = TabHeaderScroller.HorizontalOffset;

        const double eps = 0.5;
        bool overflowsLeft  = off > eps;
        bool overflowsRight = max - off > eps;

        if (TabPagerLeftButton != null)
            TabPagerLeftButton.Visibility = overflowsLeft ? Visibility.Visible : Visibility.Collapsed;
        if (TabPagerRightButton != null)
            TabPagerRightButton.Visibility = overflowsRight ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TabPagerLeft_Click(object sender, RoutedEventArgs e)
    {
        if (TabHeaderScroller == null) return;
        double target = Math.Max(0, TabHeaderScroller.HorizontalOffset - TabPagerScrollStepPx);
        TabHeaderScroller.ChangeView(target, null, null);
    }

    private void TabPagerRight_Click(object sender, RoutedEventArgs e)
    {
        if (TabHeaderScroller == null) return;
        double target = Math.Min(TabHeaderScroller.ScrollableWidth, TabHeaderScroller.HorizontalOffset + TabPagerScrollStepPx);
        TabHeaderScroller.ChangeView(target, null, null);
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        Ioc.Default.GetService<IPanelDockingService>()?.Detach(DetachablePanel.RightPanel);
    }

    private void ApplyTabHeaderVisibility()
    {
        if (TabHeaderRow != null)
            TabHeaderRow.Visibility = IsTabHeaderVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TabHeader_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabHeaderSelectionChanged || TabHeader?.SelectedItem is not SegmentedItem selectedItem)
            return;

        SelectedMode = selectedItem.Tag switch
        {
            "Queue" => RightPanelMode.Queue,
            "Lyrics" => RightPanelMode.Lyrics,
            "Friends" => RightPanelMode.FriendsActivity,
            "Details" => RightPanelMode.Details,
            "TrackDetails" => RightPanelMode.TrackDetails,
            _ => RightPanelMode.Queue
        };
    }

    private void UpdateTabHeaderVisualState()
    {
        if (TabHeader == null)
            return;

        var targetItem = SelectedMode switch
        {
            RightPanelMode.Queue => QueueTabItem,
            RightPanelMode.Lyrics => LyricsTabItem,
            RightPanelMode.FriendsActivity => FriendsTabItem,
            RightPanelMode.Details => DetailsTabItem,
            RightPanelMode.TrackDetails => TrackDetailsTabItem,
            _ => QueueTabItem
        };

        if (ReferenceEquals(TabHeader.SelectedItem, targetItem))
            return;

        _suppressTabHeaderSelectionChanged = true;
        try
        {
            TabHeader.SelectedItem = targetItem;
        }
        finally
        {
            _suppressTabHeaderSelectionChanged = false;
        }
    }

    // ── Deferred subtree materialization (x:Load="False") ──

    private void EnsureLyricsTreeLoaded()
    {
        if (_lyricsTreeLoaded) return;
        _ = FindName(nameof(LyricsContent));
        _lyricsTreeLoaded = LyricsContent != null;
    }

    private void EnsureTrackDetailsTreeLoaded()
    {
        if (_trackDetailsTreeLoaded) return;
        _ = FindName(nameof(TrackDetailsContent));
        _trackDetailsTreeLoaded = TrackDetailsContent != null;

        // Lazy-subscribe to ShellViewModel.SelectedTrackForDetails so a re-invocation
        // with a different track rebinds without forcing the user to close+reopen.
        if (_trackDetailsTreeLoaded && _shellViewModelTrackDetailsHandler is null)
        {
            _shellViewModelForTrackDetails = Ioc.Default.GetService<ShellViewModel>();
            if (_shellViewModelForTrackDetails is not null)
            {
                _shellViewModelTrackDetailsHandler = (_, args) =>
                {
                    if (args.PropertyName == nameof(ShellViewModel.SelectedTrackForDetails))
                        DispatcherQueue.TryEnqueue(RefreshTrackDetailsContent);
                };
                _shellViewModelForTrackDetails.PropertyChanged += _shellViewModelTrackDetailsHandler;
            }
        }
    }

    private void RefreshTrackDetailsContent()
    {
        if (!_trackDetailsTreeLoaded) return;
        var track = (_shellViewModelForTrackDetails ??= Ioc.Default.GetService<ShellViewModel>())?.SelectedTrackForDetails;
        if (track is null)
        {
            TrackDetailsTitle.Text = string.Empty;
            TrackDetailsArtist.Text = string.Empty;
            TrackDetailsAlbum.Text = string.Empty;
            TrackDetailsDuration.Text = string.Empty;
            TrackDetailsAdded.Text = string.Empty;
            TrackDetailsPlays.Text = string.Empty;
            TrackDetailsArtwork.Source = null;
            return;
        }

        TrackDetailsTitle.Text = track.Title;
        TrackDetailsArtist.Text = track.ArtistName;
        TrackDetailsAlbum.Text = track.AlbumName;
        TrackDetailsDuration.Text = track.DurationFormatted;
        TrackDetailsAdded.Text = track.AddedAtFormatted;
        TrackDetailsPlays.Text = track.PlayCountFormatted;
        TrackDetailsArtwork.Source = string.IsNullOrEmpty(track.ImageUrl)
            ? null
            : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
            {
                UriSource = new Uri(track.ImageUrl),
                DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
                // Border is 180×180 — cap decode at 360 (×2 for HiDPI) so we don't
                // hold a full-source ~1.6 MB pixel buffer per track.
                DecodePixelWidth = 360,
            };
    }

    private void EnsureDetailsTreeLoaded()
    {
        if (_detailsTreeLoaded) return;
        _ = FindName(nameof(DetailsContent));
        _detailsTreeLoaded = DetailsContent != null;

        // Wheel handler attaches directly to DetailsContent — must wait until the
        // subtree exists to register it. Before this point there's nothing to handle.
        if (_detailsTreeLoaded)
        {
            RegisterDetailsWheelHandler();
            InitializeOutputDeviceCard();
        }
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
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdatePanelBackgroundState();

            if (SelectedMode == RightPanelMode.Details)
                ApplyDetailsState();
        });
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
        UpdatePanelBackgroundState();

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

        DetailsContent.DataContext = _detailsVm;
        var hasData = _detailsVm.HasData;
        var isPodcast = hasData && _detailsVm.IsPodcastEpisode;

        if (isPodcast)
        {
            UpdatePodcastDetailsContent();
            return;
        }

        HidePodcastDetailsContent();

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
        SyncDetailsAiMeaningForCurrentTrack(showLyricsSnippet);

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
        UpdateDetailsCanvasSyncBadge();

    }

    private void UpdatePodcastDetailsContent()
    {
        if (_detailsVm == null) return;

        DetailsArtistHeaderCard.Visibility = Visibility.Collapsed;
        DetailsLyricsSnippet.Visibility = Visibility.Collapsed;
        TeardownDetailsLyricsSnippet();
        DetailsAiMeaningSection.Visibility = Visibility.Collapsed;
        CancelDetailsAiMeaningRequest();
        ResetDetailsAiMeaningUi(clearText: true);
        DetailsBio.Visibility = Visibility.Collapsed;
        DetailsCreditsSection.Visibility = Visibility.Collapsed;
        DetailsConcertsSection.Visibility = Visibility.Collapsed;
        DetailsRelatedVideosSection.Visibility = Visibility.Collapsed;

        DetailsPodcastHeaderCard.Visibility = Visibility.Visible;
        DetailsPodcastTitle.Text = _detailsVm.PodcastEpisodeTitle;
        DetailsPodcastShowLine.Text = _detailsVm.PodcastShowName;
        DetailsPodcastMetaLine.Text = _detailsVm.PodcastEpisodeMetadata;

        DetailsPodcastArtwork.Source = string.IsNullOrWhiteSpace(_detailsVm.PodcastEpisodeImageUrl)
            ? null
            : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_detailsVm.PodcastEpisodeImageUrl));

        DetailsPodcastDescriptionSection.Visibility = _detailsVm.HasPodcastDescription
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailsPodcastDescriptionText.Text = _detailsVm.PodcastEpisodeDescription ?? "";
        DetailsPodcastTranscriptChip.Visibility = _detailsVm.HasPodcastTranscript
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailsPodcastTranscriptText.Text = _detailsVm.PodcastTranscriptLabel;

        DetailsPodcastChaptersSection.Visibility = _detailsVm.HasPodcastChapters
            ? Visibility.Visible
            : Visibility.Collapsed;

        var episodeUri = _detailsVm.PodcastEpisodeDetail?.Uri;
        if (!string.Equals(_podcastChapterEpisodeUri, episodeUri, StringComparison.Ordinal))
        {
            _podcastChapterEpisodeUri = episodeUri;
            _showAllPodcastChapters = false;
            _podcastChapterPreviewStart = -1;
        }

        UpdatePodcastChapterTimelineProgress();
        UpdatePodcastChaptersSource(force: true);
        UpdatePodcastChapterTimelineTimerState();

        DetailsPodcastCommentsSection.Visibility = Visibility.Visible;
        DetailsPodcastComments.HeaderText = _detailsVm.PodcastCommentsCountLabel;
        DetailsPodcastComments.Comments = _detailsVm.PodcastComments;
        DetailsPodcastComments.HasNoComments = !_detailsVm.HasPodcastComments;
        DetailsPodcastComments.HasMoreComments = _detailsVm.HasMorePodcastComments;
        DetailsPodcastComments.StatusText = _detailsVm.HasPodcastCommentStatus
            ? _detailsVm.PodcastCommentStatus
            : null;

        UpdateDetailsCanvasSyncBadge();
    }

    private void HidePodcastDetailsContent()
    {
        DetailsPodcastHeaderCard.Visibility = Visibility.Collapsed;
        DetailsPodcastArtwork.Source = null;
        DetailsPodcastDescriptionSection.Visibility = Visibility.Collapsed;
        DetailsPodcastChaptersSection.Visibility = Visibility.Collapsed;
        DetailsPodcastCommentsSection.Visibility = Visibility.Collapsed;
        DetailsPodcastChaptersList.ItemsSource = null;
        _podcastChapterPreviewStart = -1;
        UpdatePodcastChapterTimelineTimerState();
        DetailsPodcastComments.Comments = null;
    }

    // Reactions popup — uses the shared PodcastCommentReactionsDialog helper
    // so the right panel renders the same dialog as the library episode detail.
    private async void DetailsPodcastComments_ShowReactionsRequested(
        Wavee.UI.WinUI.Controls.Comments.CommentsList sender,
        PodcastCommentViewModel comment)
    {
        if (XamlRoot is null) return;
        await PodcastCommentReactionsDialog.ShowAsync(
            XamlRoot,
            (token, reaction) => comment.GetReactionsAsync(token, reaction));
    }

    private async void DetailsPodcastComments_ShowReplyReactionsRequested(
        Wavee.UI.WinUI.Controls.Comments.CommentsList sender,
        PodcastReplyViewModel reply)
    {
        // Compact mode hides replies so this is unlikely to fire, but route to
        // the same dialog if it ever does.
        if (XamlRoot is null) return;
        await PodcastCommentReactionsDialog.ShowAsync(
            XamlRoot,
            (token, reaction) => reply.GetReactionsAsync(token, reaction));
    }

    private void DetailsPodcastChapter_Click(object sender, RoutedEventArgs e)
    {
        if (_detailsVm is null || sender is not FrameworkElement { Tag: EpisodeChapterVm chapter })
            return;

        AnimatePodcastChapterRow((FrameworkElement)sender, 0.992f, 90);
        if (_detailsVm.SeekPodcastChapterCommand.CanExecute(chapter))
            _detailsVm.SeekPodcastChapterCommand.Execute(chapter);
    }

    private void DetailsPodcastChaptersToggle_Click(object sender, RoutedEventArgs e)
    {
        _showAllPodcastChapters = !_showAllPodcastChapters;
        _podcastChapterPreviewStart = -1;
        UpdatePodcastChaptersSource(force: true);
    }

    private void DetailsPodcastChapter_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Hand));
        AnimatePodcastChapterRow(element, 1.012f, 160);
    }

    private void DetailsPodcastChapter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
        AnimatePodcastChapterRow(element, 1f, 150);
    }

    private void DetailsPodcastChapter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimatePodcastChapterRow(element, 0.992f, 80);
    }

    private void DetailsPodcastChapter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimatePodcastChapterRow(element, 1.012f, 120);
    }

    private void DetailsPodcastChapter_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimatePodcastChapterRow(element, 1f, 120);
    }

    private void UpdatePodcastChapterTimelineProgress()
    {
        if (_detailsVm?.IsPodcastEpisode == true)
        {
            _detailsVm.UpdatePodcastChapterTimeline(GetPodcastTimelinePositionMs());
            UpdatePodcastChaptersSource();
        }
    }

    private double GetPodcastTimelinePositionMs()
    {
        if (_lyricsVm is not null)
            return _lyricsVm.GetInterpolatedPosition().TotalMilliseconds;

        return _detailsVm?.PlaybackState.Position ?? 0;
    }

    private void UpdatePodcastChaptersSource(bool force = false)
    {
        if (_detailsVm is null || DetailsPodcastChaptersList is null)
            return;

        var chapters = _detailsVm.PodcastChapters;
        var count = chapters.Count;
        var hasOverflow = count > PodcastChapterPreviewCount;

        DetailsPodcastChaptersToggleButton.Visibility = hasOverflow
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailsPodcastChaptersToggleButton.Content = _showAllPodcastChapters
            ? "Show less"
            : $"Show all {count}";

        if (!hasOverflow || _showAllPodcastChapters)
        {
            if (force || !ReferenceEquals(DetailsPodcastChaptersList.ItemsSource, chapters))
                DetailsPodcastChaptersList.ItemsSource = chapters;

            _podcastChapterPreviewStart = 0;
            return;
        }

        var activeIndex = -1;
        for (var i = 0; i < count; i++)
        {
            if (chapters[i].IsActive)
            {
                activeIndex = i;
                break;
            }
        }

        if (activeIndex < 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (!chapters[i].IsCompleted)
                {
                    activeIndex = i;
                    break;
                }
            }
        }

        if (activeIndex < 0)
            activeIndex = 0;

        var start = Math.Clamp(activeIndex - 1, 0, Math.Max(0, count - PodcastChapterPreviewCount));
        if (!force && start == _podcastChapterPreviewStart)
            return;

        _podcastChapterPreviewStart = start;
        DetailsPodcastChaptersList.ItemsSource = chapters
            .Skip(start)
            .Take(PodcastChapterPreviewCount)
            .ToList();
    }

    private bool ShouldRunPodcastChapterTimelineTimer()
    {
        return SelectedMode == RightPanelMode.Details
            && Visibility == Visibility.Visible
            && _detailsVm?.IsPodcastEpisode == true
            && _detailsVm.HasPodcastChapters
            && _detailsVm.PlaybackState.IsPlaying;
    }

    private void EnsurePodcastChapterTimelineTimer()
    {
        if (_podcastChapterTimelineTimer is not null)
            return;

        _podcastChapterTimelineTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _podcastChapterTimelineTimer.Interval = TimeSpan.FromMilliseconds(250);
        _podcastChapterTimelineTimer.Tick += OnPodcastChapterTimelineTimerTick;
    }

    private void OnPodcastChapterTimelineTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!ShouldRunPodcastChapterTimelineTimer())
        {
            sender.Stop();
            return;
        }

        UpdatePodcastChapterTimelineProgress();
    }

    private void UpdatePodcastChapterTimelineTimerState()
    {
        EnsurePodcastChapterTimelineTimer();
        if (ShouldRunPodcastChapterTimelineTimer())
            _podcastChapterTimelineTimer?.Start();
        else
            _podcastChapterTimelineTimer?.Stop();
    }

    private void TeardownPodcastChapterTimelineTimer()
    {
        if (_podcastChapterTimelineTimer is null)
            return;

        _podcastChapterTimelineTimer.Stop();
        _podcastChapterTimelineTimer.Tick -= OnPodcastChapterTimelineTimerTick;
        _podcastChapterTimelineTimer = null;
    }

    private static void AnimatePodcastChapterRow(FrameworkElement element, float scale, double durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(
            (float)Math.Max(0, element.ActualWidth / 2),
            (float)Math.Max(0, element.ActualHeight / 2),
            0);

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), easing);
        visual.StartAnimation(nameof(Visual.Scale), animation);
    }

    private async void DetailsCanvasSyncBadge_Click(object sender, RoutedEventArgs e)
        => await ReviewPendingCanvasAsync();

    private async Task ReviewPendingCanvasAsync()
    {
        if (_detailsVm == null
            || !_detailsVm.HasPendingCanvasUpdate
            || XamlRoot == null)
        {
            return;
        }

        var result = await CanvasSyncReviewDialog.ShowAsync(
            XamlRoot,
            _detailsVm.CanvasUrl,
            _detailsVm.PendingCanvasUrl);

        switch (result)
        {
            case CanvasSyncReviewResult.UseNew:
                await _detailsVm.AcceptPendingCanvasUpdateAsync();
                break;

            case CanvasSyncReviewResult.KeepCurrent:
                await _detailsVm.RejectPendingCanvasUpdateAsync();
                break;
        }

        RefreshDetailsCanvasUi();
    }

    private async Task PickManualCanvasFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".webm");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".m4v");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file == null || _detailsVm == null)
                return;

            await _detailsVm.ImportManualCanvasFileAsync(file.Path);
            _notificationService?.Show("Custom canvas applied.", NotificationSeverity.Success, TimeSpan.FromSeconds(3));
            RefreshDetailsCanvasUi();
        }
        catch (Exception ex)
        {
            _notificationService?.Show(ex.Message, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    private async Task PromptForManualCanvasUrlAsync()
    {
        if (XamlRoot == null || _detailsVm == null)
            return;

        var textBox = new TextBox
        {
            PlaceholderText = "https://example.com/canvas.mp4",
            Text = _detailsVm.IsManualCanvasOverride
                && Uri.TryCreate(_detailsVm.CanvasUrl, UriKind.Absolute, out var existingUri)
                && !existingUri.IsFile
                    ? existingUri.AbsoluteUri
                    : string.Empty,
            MinWidth = 420,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Set canvas URL",
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Paste a direct video URL to use as the Details canvas for this track.",
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                    textBox
                }
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        try
        {
            await _detailsVm.SetManualCanvasUrlAsync(textBox.Text);
            _notificationService?.Show("Custom canvas applied.", NotificationSeverity.Success, TimeSpan.FromSeconds(3));
            RefreshDetailsCanvasUi();
        }
        catch (Exception ex)
        {
            _notificationService?.Show(ex.Message, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    private async Task ResetCanvasToUpstreamAsync()
    {
        if (_detailsVm == null)
            return;

        try
        {
            await _detailsVm.ResetCanvasToUpstreamAsync();
            _notificationService?.Show("Canvas reset to Spotify.", NotificationSeverity.Success, TimeSpan.FromSeconds(3));
            RefreshDetailsCanvasUi();
        }
        catch (Exception ex)
        {
            _notificationService?.Show(ex.Message, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    private void RefreshDetailsCanvasUi()
    {
        UpdatePanelBackgroundState();
        if (SelectedMode == RightPanelMode.Details)
            ApplyDetailsState();
    }

    private IReadOnlyList<ContextMenuItemModel>? BuildDetailsCanvasMenuItems()
    {
        if (_detailsVm?.HasData != true) return null;

        var canvasLabel = _detailsVm.IsManualCanvasOverride ? "Custom canvas" : "Canvas";
        var canvasMenuGlyph = Wavee.UI.WinUI.Styles.FluentGlyphs.Canvas;
        var children = new List<ContextMenuItemModel>
        {
            new()
            {
                Text = "Choose file...",
                Invoke = async () => await PickManualCanvasFileAsync()
            },
            new()
            {
                Text = "Set URL...",
                Invoke = async () => await PromptForManualCanvasUrlAsync()
            }
        };

        if (_detailsVm.IsManualCanvasOverride)
        {
            children.Add(ContextMenuItemModel.Separator);
            children.Add(new ContextMenuItemModel
            {
                Text = "Reset to Spotify",
                Invoke = async () => await ResetCanvasToUpstreamAsync()
            });
        }

        if (_detailsVm.HasPendingCanvasUpdate)
        {
            children.Add(ContextMenuItemModel.Separator);
            children.Add(new ContextMenuItemModel
            {
                Text = "Review Spotify update...",
                Invoke = async () => await ReviewPendingCanvasAsync()
            });
        }

        return new[]
        {
            new ContextMenuItemModel
            {
                Text = canvasLabel,
                Glyph = canvasMenuGlyph,
                Items = children
            }
        };
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

    // Details sidebar AI lyrics meaning
    private void SyncDetailsAiMeaningForCurrentTrack(bool hasLyrics)
    {
        if (DetailsContent == null)
            return;

        var canShow = hasLyrics
                      && _lyricsAiService != null
                      && _aiCapabilities?.IsLyricsSummarizeEnabled == true
                      && _lyricsVm?.CurrentLyrics != null;

        DetailsAiMeaningSection.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        if (!canShow)
        {
            CancelDetailsAiMeaningRequest();
            ResetDetailsAiMeaningUi(clearText: true);
            _detailsAiMeaningTrackUri = null;
            return;
        }

        var trackUri = GetCurrentTrackUriForAi();
        if (!string.Equals(_detailsAiMeaningTrackUri, trackUri, StringComparison.Ordinal))
        {
            CancelDetailsAiMeaningRequest();
            _detailsAiMeaningTrackUri = trackUri;
            ResetDetailsAiMeaningUi(clearText: true);
        }

        if (_lyricsAiService.TryGetCachedLyricsMeaning(trackUri, out var cached))
            ApplyDetailsAiMeaningResult(cached);
        else if (!_detailsAiMeaningBusy)
            DetailsAiMeaningButton.Content = "Generate";

        DetailsAiMeaningButton.IsEnabled = !_detailsAiMeaningBusy;
    }

    private async void DetailsAiMeaningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lyricsAiService == null
            || _aiCapabilities?.IsLyricsSummarizeEnabled != true
            || _lyricsVm?.CurrentLyrics == null)
        {
            return;
        }

        var fullText = _lyricsVm.CurrentLyrics.WrappedOriginalText;
        if (string.IsNullOrWhiteSpace(fullText))
        {
            ApplyDetailsAiMeaningResult(LyricsAiResult.Empty);
            return;
        }

        var trackUri = GetCurrentTrackUriForAi();
        _detailsAiMeaningTrackUri = trackUri;

        CancelDetailsAiMeaningRequest();
        var cts = _detailsAiMeaningCts = new CancellationTokenSource();
        SetDetailsAiMeaningBusy(true);

        try
        {
            var result = await _lyricsAiService.GetLyricsMeaningAsync(
                trackUri,
                fullText,
                deltaProgress: null,
                ct: cts.Token);

            if (cts.IsCancellationRequested)
                return;

            ApplyDetailsAiMeaningResult(result);
        }
        catch (OperationCanceledException)
        {
            // Details card was hidden or the track changed.
        }
        finally
        {
            if (ReferenceEquals(_detailsAiMeaningCts, cts))
            {
                _detailsAiMeaningCts = null;
                SetDetailsAiMeaningBusy(false);
            }

            cts.Dispose();
        }
    }

    private void ApplyDetailsAiMeaningResult(LyricsAiResult result)
    {
        DetailsAiMeaningText.Visibility = Visibility.Visible;
        DetailsAiMeaningButton.Content = result.Kind == LyricsAiResultKind.Ok ? "Ready" : "Retry";

        DetailsAiMeaningText.Text = result.Kind switch
        {
            LyricsAiResultKind.Ok => result.Text,
            LyricsAiResultKind.Filtered => "The on-device safety filter blocked this lyrics meaning.",
            LyricsAiResultKind.Empty => "No lyrics are available to interpret.",
            LyricsAiResultKind.Unavailable => "On-device AI is not available right now.",
            LyricsAiResultKind.Error => "Something went wrong asking the on-device model.",
            _ => string.Empty,
        };
    }

    private void SetDetailsAiMeaningBusy(bool isBusy)
    {
        _detailsAiMeaningBusy = isBusy;
        DetailsAiMeaningProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        DetailsAiMeaningButton.IsEnabled = !isBusy;
        if (isBusy)
            DetailsAiMeaningButton.Content = "Generating";
    }

    private void ResetDetailsAiMeaningUi(bool clearText)
    {
        _detailsAiMeaningBusy = false;
        DetailsAiMeaningProgress.Visibility = Visibility.Collapsed;
        DetailsAiMeaningButton.IsEnabled = true;
        DetailsAiMeaningButton.Content = "Generate";
        if (clearText)
        {
            DetailsAiMeaningText.Text = string.Empty;
            DetailsAiMeaningText.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelDetailsAiMeaningRequest()
    {
        try
        {
            _detailsAiMeaningCts?.Cancel();
        }
        catch
        {
            // Already disposed; harmless.
        }
        finally
        {
            _detailsAiMeaningCts?.Dispose();
            _detailsAiMeaningCts = null;
            _detailsAiMeaningBusy = false;
        }
    }

    private string GetCurrentTrackUriForAi()
    {
        if (!string.IsNullOrWhiteSpace(_detailsVm?.CurrentTrackUri))
            return _detailsVm.CurrentTrackUri!;

        return BuildTrackUri(_lyricsVm?.PlaybackState.CurrentTrackId);
    }

    private static string BuildTrackUri(string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return "spotify:track:unknown";

        return trackId.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? trackId.Trim()
            : $"spotify:track:{trackId.Trim()}";
    }

    // Lyrics snippet (TextBlock-based, synced to playback)
    private readonly record struct CanvasLyricPresentation(
        int PastCharCount,
        int HeldStartIndex,
        int HeldCharCount,
        int ActiveStartIndex,
        int ActiveVisibleCharCount,
        int CursorCharIndex,
        bool ShowCursor,
        float CursorAdvance,
        float CursorOpacity,
        byte PastAlpha,
        byte HeldAlpha,
        byte ActiveAlpha);

    private long _lastDetailsSnippetUpdateTickMs = -1;
    private int _lastSnippetLineIndex = -1;
    private const double DetailsSnippetTickMs = 250;
    private const double CursorBlinkPeriodMs = 520;
    private const byte PastLyricAlpha = 118;
    private const byte HeldLyricAlpha = 188;
    private const byte ActiveLyricAlpha = 238;
    private const byte CursorLyricAlpha = 220;
    private const float DetailsOverlayFontSize = 28f;
    private const float DetailsOverlayMinHeight = 48f;
    private const float DetailsCursorWidth = 2.5f;
    private const float DetailsCursorOffsetX = 5f;
    private const float DetailsCursorTopInset = 3f;
    private const float MinVisibleCursorRegionWidth = 0.75f;

    private void SetupDetailsLyricsSnippet()
    {
        if (_lyricsVm == null) return;

        // Update immediately
        UpdateCanvasLyricsVisibility();
        UpdateLyricsSnippetText();
        _lastDetailsSnippetUpdateTickMs = -1;
        UpdateDetailsLyricsUpdateMode();
    }

    private void TeardownDetailsLyricsSnippet()
    {
        _lastDetailsSnippetUpdateTickMs = -1;
        _lastSnippetLineIndex = -1;
        DetachDetailsLyricsRenderLoop();
        ClearCanvasLyricOverlay();
        CanvasLyricsOverlay.Visibility = Visibility.Collapsed;
        _canvasLyricsActive = false;
    }

    private bool _canvasLyricsActive;

    private void UpdateCanvasLyricsVisibility()
    {
        var show = SelectedMode == RightPanelMode.Details
                   && _activeBackgroundMode == DetailsBackgroundMode.Canvas
                   && _lyricsVm?.HasLyrics == true;
        _canvasLyricsActive = show;
        CanvasLyricsOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            EnsureDetailsLyricsComposition();
            UpdateDetailsLyricsPresentation(renderCanvasOverlay: true);
        }
        else
        {
            ClearCanvasLyricOverlay();
        }

        UpdateDetailsLyricsUpdateMode();
    }

    private void UpdateLyricsSnippetText()
    {
        UpdateDetailsLyricsPresentation(renderCanvasOverlay: _canvasLyricsActive && !ShouldRunDetailsLyricsRenderLoop());
    }

    private void UpdateDetailsLyricsPresentation(bool renderCanvasOverlay)
    {
        if (_lyricsVm?.CurrentLyrics?.LyricsLines is not { Count: > 0 } lines)
        {
            ClearCanvasLyricOverlay();
            return;
        }
        // DetailsLyricsPrev/Current/Next live inside DetailsContent (x:Load'd).
        if (DetailsContent == null) return;

        var posMs = _lyricsVm.GetInterpolatedPosition().TotalMilliseconds;
        var currentIdx = FindCurrentLyricLineIndex(lines, posMs);
        if (currentIdx < 0) currentIdx = 0;

        var currentLine = lines[currentIdx];
        var next = currentIdx < lines.Count - 1 ? lines[currentIdx + 1].PrimaryText : "";

        // Update card snippet (only on line change)
        if (currentIdx != _lastSnippetLineIndex)
        {
            _lastSnippetLineIndex = currentIdx;

            var prev = currentIdx > 0 ? lines[currentIdx - 1].PrimaryText : "";
            DetailsLyricsPrev.Text = prev;
            DetailsLyricsPrev.Visibility = string.IsNullOrWhiteSpace(prev)
                ? Visibility.Collapsed : Visibility.Visible;
            DetailsLyricsCurrent.Text = currentLine.PrimaryText;
            DetailsLyricsNext.Text = next;
            DetailsLyricsNext.Visibility = string.IsNullOrWhiteSpace(next)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        if (renderCanvasOverlay && _canvasLyricsActive)
            RenderCanvasLyricSurface(currentLine, BuildCanvasLyricPresentation(currentLine, posMs));
        else if (_canvasLyricsActive)
        {
            return;
        }
        else
            ClearCanvasLyricOverlay();
    }

    private void AttachDetailsLyricsRenderLoop()
    {
        if (_detailsLyricsRenderSubscribed)
            return;

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnDetailsLyricsCompositionRendering;
        _detailsLyricsRenderSubscribed = true;
    }

    private void DetachDetailsLyricsRenderLoop()
    {
        if (!_detailsLyricsRenderSubscribed)
            return;

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnDetailsLyricsCompositionRendering;
        _detailsLyricsRenderSubscribed = false;
    }

    private void OnDetailsLyricsCompositionRendering(object? sender, object args)
    {
        if (!_canvasLyricsActive || SelectedMode != RightPanelMode.Details)
            return;

        UpdateDetailsLyricsPresentation(renderCanvasOverlay: true);
    }

    private void UpdateDetailsLyricsUpdateMode()
    {
        if (_lyricsVm?.HasLyrics != true || _lyricsVm.CurrentLyrics == null || SelectedMode != RightPanelMode.Details)
        {
            DetachDetailsLyricsRenderLoop();
            _lastDetailsSnippetUpdateTickMs = -1;
            UpdateTimerState();
            return;
        }

        var shouldRunRenderLoop = ShouldRunDetailsLyricsRenderLoop();
        if (shouldRunRenderLoop)
        {
            AttachDetailsLyricsRenderLoop();
            _lastDetailsSnippetUpdateTickMs = -1;
            UpdateTimerState();
            return;
        }

        DetachDetailsLyricsRenderLoop();
        UpdateTimerState();
    }

    private bool ShouldRunDetailsLyricsRenderLoop()
    {
        return _canvasLyricsActive
            && SelectedMode == RightPanelMode.Details
            && _lyricsVm?.PlaybackState.IsPlaying == true;
    }

    private bool ShouldRunDetailsLyricsSharedTimer()
    {
        return SelectedMode == RightPanelMode.Details
            && Visibility == Visibility.Visible
            && _lyricsVm?.HasLyrics == true
            && _lyricsVm.CurrentLyrics != null
            && !ShouldRunDetailsLyricsRenderLoop();
    }

    private bool ShouldRunLyricsTimelineSyncTimer()
    {
        var lyricsVm = _lyricsVm;
        if (lyricsVm == null)
            return false;

        return SelectedMode == RightPanelMode.Lyrics
            && Visibility == Visibility.Visible
            && lyricsVm.PlaybackState.IsPlaying
            && lyricsVm.HasLyrics
            && lyricsVm.CurrentLyrics != null;
    }

    private int FindCurrentLyricLineIndex(IReadOnlyList<LyricsLine> lines, double posMs)
    {
        if (lines.Count == 0)
            return -1;

        if (_lastSnippetLineIndex >= 0 && _lastSnippetLineIndex < lines.Count)
        {
            var index = _lastSnippetLineIndex;
            while (index + 1 < lines.Count && lines[index + 1].StartMs <= posMs)
                index++;

            while (index >= 0 && lines[index].StartMs > posMs)
                index--;

            return index;
        }

        var low = 0;
        var high = lines.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (lines[mid].StartMs <= posMs)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private CanvasLyricPresentation BuildCanvasLyricPresentation(
        LyricsLine line, double posMs)
    {
        var syllables = line.PrimarySyllables;
        if (!line.IsPrimaryHasRealSyllableInfo || syllables.Count == 0)
        {
            var fallbackText = line.PrimaryText ?? "";
            return new CanvasLyricPresentation(
                PastCharCount: 0,
                HeldStartIndex: 0,
                HeldCharCount: fallbackText.Length,
                ActiveStartIndex: -1,
                ActiveVisibleCharCount: 0,
                CursorCharIndex: Math.Max(-1, fallbackText.Length - 1),
                ShowCursor: false,
                CursorAdvance: 1f,
                CursorOpacity: 0f,
                PastAlpha: PastLyricAlpha,
                HeldAlpha: (byte)Math.Round(HeldLyricAlpha * 0.72),
                ActiveAlpha: ActiveLyricAlpha);
        }

        var activeIndex = -1;
        var lastStartedIndex = -1;
        for (var i = 0; i < syllables.Count; i++)
        {
            var syllable = syllables[i];
            if (syllable.StartMs > posMs)
                break;

            lastStartedIndex = i;
            if (syllable.EndMs == null || posMs < syllable.EndMs.Value)
                activeIndex = i;
        }

        var heldIndex = activeIndex < 0 ? lastStartedIndex : -1;
        if (activeIndex < 0 && heldIndex < 0)
        {
            return default;
        }

        var pastCharCount = 0;
        var heldStartIndex = -1;
        var heldCharCount = 0;
        var activeStartIndex = -1;
        var activeVisibleCharCount = 0;
        var cursorCharIndex = -1;
        var cursorAdvance = 1f;

        if (activeIndex >= 0)
        {
            var activeSyllable = syllables[activeIndex];
            pastCharCount = Math.Max(0, activeSyllable.StartIndex);
            activeStartIndex = activeSyllable.StartIndex;
            activeVisibleCharCount = GetActiveSyllableCharCount(activeSyllable, posMs);
            cursorCharIndex = activeVisibleCharCount > 0
                ? activeStartIndex + activeVisibleCharCount - 1
                : Math.Max(-1, activeStartIndex - 1);
            cursorAdvance = GetActiveSyllableCursorAdvance(activeSyllable, posMs, activeVisibleCharCount);
        }
        else if (heldIndex >= 0)
        {
            var heldSyllable = syllables[heldIndex];
            pastCharCount = Math.Max(0, heldSyllable.StartIndex);
            heldStartIndex = heldSyllable.StartIndex;
            heldCharCount = heldSyllable.Length;
            cursorCharIndex = heldCharCount > 0
                ? heldStartIndex + heldCharCount - 1
                : Math.Max(-1, heldStartIndex - 1);
            cursorAdvance = 1f;
        }

        return new CanvasLyricPresentation(
            PastCharCount: pastCharCount,
            HeldStartIndex: heldStartIndex,
            HeldCharCount: heldCharCount,
            ActiveStartIndex: activeStartIndex,
            ActiveVisibleCharCount: activeVisibleCharCount,
            CursorCharIndex: cursorCharIndex,
            ShowCursor: activeIndex >= 0,
            CursorAdvance: cursorAdvance,
            CursorOpacity: activeIndex >= 0 ? GetCursorBlinkOpacity(posMs) : 0f,
            PastAlpha: PastLyricAlpha,
            HeldAlpha: HeldLyricAlpha,
            ActiveAlpha: (byte)Math.Round(ActiveLyricAlpha * GetActiveSyllableIntensity(
                activeIndex >= 0 ? syllables[activeIndex] : syllables[Math.Max(0, heldIndex)],
                posMs)));
    }

    private void RenderCanvasLyricSurface(LyricsLine line, CanvasLyricPresentation presentation)
    {
        if (CanvasLyricLineHost == null || !IsLoaded)
            return;

        var text = (line.PrimaryText ?? string.Empty).ToUpperInvariant();
        if (text.Length == 0)
        {
            ClearCanvasLyricOverlay();
            return;
        }

        EnsureDetailsLyricsComposition();

        var availableWidth = (float)Math.Max(
            120,
            CanvasLyricLineHost.ActualWidth > 1
                ? CanvasLyricLineHost.ActualWidth
                : Math.Max(120, PanelContentGrid?.ActualWidth - 40 ?? RootGrid.ActualWidth - 80));
        EnsureDetailsLyricsTextLayout(text, availableWidth);
        if (_detailsLyricsTextLayout == null)
            return;

        var targetHeight = Math.Max(DetailsOverlayMinHeight, _detailsLyricsLayoutHeight);
        if (Math.Abs(CanvasLyricLineHost.Height - targetHeight) > 0.5)
            CanvasLyricLineHost.Height = targetHeight;

        EnsureDetailsLyricsDrawingSurface(
            Math.Max(1, (int)Math.Ceiling(availableWidth)),
            Math.Max(1, (int)Math.Ceiling(targetHeight)));
        UpdateDetailsLyricsCompositionSize();

        if (_detailsLyricsDrawingSurface == null)
            return;

        using (var ds = CanvasComposition.CreateDrawingSession(_detailsLyricsDrawingSurface))
        {
            ds.Clear(Colors.Transparent);

            _detailsLyricsTextLayout.SetColor(0, text.Length, Colors.Transparent);

            if (presentation.PastCharCount > 0)
            {
                _detailsLyricsTextLayout.SetColor(
                    0,
                    Math.Min(presentation.PastCharCount, text.Length),
                    Windows.UI.Color.FromArgb(presentation.PastAlpha, 255, 255, 255));
            }

            if (presentation.HeldStartIndex >= 0 && presentation.HeldCharCount > 0)
            {
                _detailsLyricsTextLayout.SetColor(
                    presentation.HeldStartIndex,
                    Math.Min(presentation.HeldCharCount, text.Length - presentation.HeldStartIndex),
                    Windows.UI.Color.FromArgb(presentation.HeldAlpha, 255, 255, 255));
            }

            if (presentation.ActiveStartIndex >= 0 && presentation.ActiveVisibleCharCount > 0)
            {
                _detailsLyricsTextLayout.SetColor(
                    presentation.ActiveStartIndex,
                    Math.Min(presentation.ActiveVisibleCharCount, text.Length - presentation.ActiveStartIndex),
                    Windows.UI.Color.FromArgb(presentation.ActiveAlpha, 255, 255, 255));
            }

            ds.DrawTextLayout(_detailsLyricsTextLayout, Vector2.Zero, Colors.Transparent);
        }

        UpdateDetailsLyricsCursor(presentation);
        if (_detailsLyricsTextVisual != null)
            _detailsLyricsTextVisual.Opacity = 1f;
    }

    private void EnsureDetailsLyricsComposition()
    {
        if (CanvasLyricLineHost == null)
            return;

        var hostVisual = ElementCompositionPreview.GetElementVisual(CanvasLyricLineHost);
        var compositor = hostVisual.Compositor;

        if (_detailsLyricsContainerVisual != null && _detailsLyricsGraphicsDevice != null)
        {
            UpdateDetailsLyricsCompositionSize();
            return;
        }

        _detailsLyricsCanvasDevice ??= new CanvasDevice();
        _detailsLyricsGraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _detailsLyricsCanvasDevice);

        _detailsLyricsContainerVisual = TrackDetailsLyricsCompositionObject(compositor.CreateContainerVisual());

        _detailsLyricsTextBrush = TrackDetailsLyricsCompositionObject(compositor.CreateSurfaceBrush());
        _detailsLyricsTextBrush.Stretch = CompositionStretch.None;

        _detailsLyricsTextVisual = TrackDetailsLyricsCompositionObject(compositor.CreateSpriteVisual());
        _detailsLyricsTextVisual.Brush = _detailsLyricsTextBrush;

        _detailsLyricsCursorBrush = TrackDetailsLyricsCompositionObject(
            compositor.CreateColorBrush(Windows.UI.Color.FromArgb(CursorLyricAlpha, 255, 255, 255)));
        _detailsLyricsCursorVisual = TrackDetailsLyricsCompositionObject(compositor.CreateSpriteVisual());
        _detailsLyricsCursorVisual.Brush = _detailsLyricsCursorBrush;
        _detailsLyricsCursorVisual.Opacity = 0f;

        _detailsLyricsContainerVisual.Children.InsertAtBottom(_detailsLyricsTextVisual);
        _detailsLyricsContainerVisual.Children.InsertAtTop(_detailsLyricsCursorVisual);

        ElementCompositionPreview.SetElementChildVisual(CanvasLyricLineHost, _detailsLyricsContainerVisual);
        UpdateDetailsLyricsCompositionSize();
    }

    private void EnsureDetailsLyricsTextLayout(string text, float maxWidth)
    {
        if (_detailsLyricsCanvasDevice == null)
            return;

        if (_detailsLyricsTextLayout != null
            && string.Equals(_detailsLyricsLayoutText, text, StringComparison.Ordinal)
            && Math.Abs(_detailsLyricsLayoutWidth - maxWidth) < 1f)
        {
            return;
        }

        _detailsLyricsTextLayout?.Dispose();

        var format = new CanvasTextFormat
        {
            FontSize = DetailsOverlayFontSize,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 900 },
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            WordWrapping = CanvasWordWrapping.Wrap
        };

        _detailsLyricsTextLayout = new CanvasTextLayout(
            _detailsLyricsCanvasDevice,
            text,
            format,
            maxWidth,
            2000f)
        {
            Options = CanvasDrawTextOptions.NoPixelSnap
        };
        _detailsLyricsCharacterRegions = _detailsLyricsTextLayout.GetCharacterRegions(0, text.Length);
        _detailsLyricsLayoutText = text;
        _detailsLyricsLayoutWidth = maxWidth;
        _detailsLyricsLayoutHeight = Math.Max((float)_detailsLyricsTextLayout.LayoutBounds.Height, DetailsOverlayMinHeight);
    }

    private void EnsureDetailsLyricsDrawingSurface(int width, int height)
    {
        if (_detailsLyricsGraphicsDevice == null)
            return;

        var targetSize = new Size(width, height);
        if (_detailsLyricsDrawingSurface == null)
        {
            _detailsLyricsDrawingSurface = _detailsLyricsGraphicsDevice.CreateDrawingSurface(
                targetSize,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);
            if (_detailsLyricsTextBrush != null)
                _detailsLyricsTextBrush.Surface = _detailsLyricsDrawingSurface;
            return;
        }

        CanvasComposition.Resize(_detailsLyricsDrawingSurface, targetSize);
    }

    private void UpdateDetailsLyricsCompositionSize()
    {
        if (CanvasLyricLineHost == null || _detailsLyricsContainerVisual == null)
            return;

        var width = Math.Max(1f, (float)(CanvasLyricLineHost.ActualWidth > 1 ? CanvasLyricLineHost.ActualWidth : _detailsLyricsLayoutWidth));
        var height = Math.Max(DetailsOverlayMinHeight, (float)CanvasLyricLineHost.Height);
        var size = new Vector2(width, height);

        _detailsLyricsContainerVisual.Size = size;
        if (_detailsLyricsTextVisual != null)
            _detailsLyricsTextVisual.Size = size;
    }

    private void ClearCanvasLyricOverlay()
    {
        if (_detailsLyricsCursorVisual != null)
            _detailsLyricsCursorVisual.Opacity = 0f;
        if (_detailsLyricsTextVisual != null)
            _detailsLyricsTextVisual.Opacity = 0f;
        if (_detailsLyricsDrawingSurface != null)
        {
            using var ds = CanvasComposition.CreateDrawingSession(_detailsLyricsDrawingSurface);
            ds.Clear(Colors.Transparent);
        }
    }

    private void UpdateDetailsLyricsCursor(CanvasLyricPresentation presentation)
    {
        if (_detailsLyricsCursorVisual == null || _detailsLyricsCharacterRegions == null)
            return;

        if (!presentation.ShowCursor
            || presentation.CursorCharIndex < 0
            || presentation.CursorCharIndex >= _detailsLyricsCharacterRegions.Length)
        {
            _detailsLyricsCursorVisual.Opacity = 0f;
            return;
        }

        var resolvedIndex = ResolveVisibleCursorRegionIndex(presentation.CursorCharIndex);
        if (resolvedIndex < 0)
        {
            _detailsLyricsCursorVisual.Opacity = 0f;
            return;
        }

        var region = _detailsLyricsCharacterRegions[resolvedIndex];
        var bounds = region.LayoutBounds;
        var cursorAdvance = Math.Clamp(presentation.CursorAdvance, 0f, 1f);
        _detailsLyricsCursorVisual.Offset = new Vector3(
            (float)(bounds.X + (bounds.Width * cursorAdvance) + DetailsCursorOffsetX),
            (float)(bounds.Y + DetailsCursorTopInset),
            0f);
        _detailsLyricsCursorVisual.Size = new Vector2(
            DetailsCursorWidth,
            (float)Math.Max(14f, bounds.Height - (DetailsCursorTopInset * 2)));
        _detailsLyricsCursorVisual.Opacity = presentation.CursorOpacity;
    }

    private int ResolveVisibleCursorRegionIndex(int requestedIndex)
    {
        if (_detailsLyricsCharacterRegions == null
            || string.IsNullOrEmpty(_detailsLyricsLayoutText)
            || requestedIndex < 0
            || requestedIndex >= _detailsLyricsCharacterRegions.Length)
        {
            return -1;
        }

        if (IsUsableCursorRegion(requestedIndex))
            return requestedIndex;

        for (var i = requestedIndex - 1; i >= 0; i--)
        {
            if (IsUsableCursorRegion(i))
                return i;
        }

        for (var i = requestedIndex + 1; i < _detailsLyricsCharacterRegions.Length; i++)
        {
            if (IsUsableCursorRegion(i))
                return i;
        }

        return -1;
    }

    private bool IsUsableCursorRegion(int index)
    {
        if (_detailsLyricsCharacterRegions == null
            || string.IsNullOrEmpty(_detailsLyricsLayoutText)
            || index < 0
            || index >= _detailsLyricsCharacterRegions.Length
            || index >= _detailsLyricsLayoutText.Length)
        {
            return false;
        }

        if (char.IsWhiteSpace(_detailsLyricsLayoutText[index]))
            return false;

        var bounds = _detailsLyricsCharacterRegions[index].LayoutBounds;
        return bounds.Width >= MinVisibleCursorRegionWidth && bounds.Height > 0;
    }

    private T TrackDetailsLyricsCompositionObject<T>(T compositionObject) where T : CompositionObject
    {
        _detailsLyricsCompositionObjects.Add(compositionObject);
        return compositionObject;
    }

    private void TeardownDetailsLyricsComposition()
    {
        DetachDetailsLyricsRenderLoop();

        if (CanvasLyricLineHost != null)
            ElementCompositionPreview.SetElementChildVisual(CanvasLyricLineHost, null);

        _detailsLyricsTextLayout?.Dispose();
        _detailsLyricsTextLayout = null;
        _detailsLyricsCharacterRegions = null;
        _detailsLyricsLayoutText = null;
        _detailsLyricsLayoutWidth = 0;
        _detailsLyricsLayoutHeight = 0;
        _detailsLyricsDrawingSurface = null;
        _detailsLyricsGraphicsDevice = null;

        for (int i = _detailsLyricsCompositionObjects.Count - 1; i >= 0; i--)
            _detailsLyricsCompositionObjects[i].Dispose();
        _detailsLyricsCompositionObjects.Clear();

        _detailsLyricsContainerVisual = null;
        _detailsLyricsTextVisual = null;
        _detailsLyricsCursorVisual = null;
        _detailsLyricsTextBrush = null;
        _detailsLyricsCursorBrush = null;

        _detailsLyricsCanvasDevice?.Dispose();
        _detailsLyricsCanvasDevice = null;
    }

    private void CanvasLyricLineHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_canvasLyricsActive)
            return;

        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1
            && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 1)
        {
            return;
        }

        _detailsLyricsTextLayout?.Dispose();
        _detailsLyricsTextLayout = null;
        _detailsLyricsCharacterRegions = null;
        _detailsLyricsLayoutText = null;
        _detailsLyricsLayoutWidth = 0;
        _detailsLyricsLayoutHeight = 0;
        UpdateDetailsLyricsPresentation(renderCanvasOverlay: _canvasLyricsActive);
    }

    private static int GetActiveSyllableCharCount(BaseLyrics syllable, double posMs)
    {
        var text = syllable.Text ?? "";
        if (text.Length == 0)
            return 0;

        var durationMs = Math.Max(60, syllable.DurationMs > 0 ? syllable.DurationMs : 220);
        var progress = Math.Clamp((posMs - syllable.StartMs) / durationMs, 0, 1);
        var eased = 1.0 - Math.Pow(1.0 - progress, 1.7);
        return Math.Clamp((int)Math.Ceiling(text.Length * eased), 1, text.Length);
    }

    private static double GetActiveSyllableIntensity(BaseLyrics syllable, double posMs)
    {
        var durationMs = Math.Max(60, syllable.DurationMs > 0 ? syllable.DurationMs : 220);
        var progress = Math.Clamp((posMs - syllable.StartMs) / durationMs, 0, 1);
        return 0.55 + 0.45 * (1.0 - Math.Pow(1.0 - progress, 2.0));
    }

    private static float GetActiveSyllableCursorAdvance(BaseLyrics syllable, double posMs, int visibleCharCount)
    {
        var text = syllable.Text ?? "";
        if (text.Length == 0 || visibleCharCount <= 0)
            return 1f;

        var durationMs = Math.Max(60, syllable.DurationMs > 0 ? syllable.DurationMs : 220);
        var linearProgress = Math.Clamp((posMs - syllable.StartMs) / durationMs, 0, 1);
        var charProgress = linearProgress * text.Length;
        var visibleStart = Math.Max(0, visibleCharCount - 1);
        var cursorAdvance = charProgress - visibleStart;
        return (float)Math.Clamp(cursorAdvance, 0.15, 1.0);
    }

    private static float GetCursorBlinkOpacity(double posMs)
    {
        var phase = (posMs % CursorBlinkPeriodMs) / CursorBlinkPeriodMs;
        var pulse = 0.5 + (0.5 * Math.Sin(phase * Math.PI * 2));
        return (float)(0.25 + (0.75 * pulse));
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
        var ctx = new TrackMenuContext
        {
            ShowCreditsAction = () =>
            {
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
                UpdatePanelBackgroundState();
            },
            HasCanvas = _detailsVm?.HasCanvas ?? false,
            CurrentBackgroundMode = _activeBackgroundMode,
            ExtraItems = BuildDetailsCanvasMenuItems()
        };

        var items = TrackContextMenuBuilder.Build(adapter, ctx);
        ContextMenuHost.Show((Microsoft.UI.Xaml.FrameworkElement)sender, items);
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
    private CanvasImageSource? _canvasImageSource;
    private CanvasImageSource? _blurredAlbumArtImageSource;
    private int _detailsBackgroundGeneration;
    private readonly object _canvasFrameRenderGate = new();
    private bool _canvasFrameRenderQueued;
    private bool _canvasFramePending;
    private long _lastCanvasFrameRenderTimestamp;
    private static readonly long CanvasFrameMinIntervalTicks = Stopwatch.Frequency / 30;
    private int _blurredAlbumArtRenderWidth;
    private int _blurredAlbumArtRenderHeight;
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
        // Right panel background is intentionally simple:
        // only show canvas media on the Details tab when a canvas source exists.
        var mode = SelectedMode == RightPanelMode.Details && hasCanvas
            ? DetailsBackgroundMode.Canvas
            : DetailsBackgroundMode.None;

        _activeBackgroundMode = mode;
        UpdateCanvasLyricsVisibility();

        switch (mode)
        {
            case DetailsBackgroundMode.None:
                TeardownCanvasBackground();
                TeardownBlurredAlbumArt();
                break;

            case DetailsBackgroundMode.Canvas:
                TeardownBlurredAlbumArt();
                SetupCanvasBackground(canvasUrl);
                break;
        }

        UpdateBackgroundChrome();
        UpdateBackgroundMediaVisibility();
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
            return;
        }

        if (albumArt == _currentAlbumArtUrl
            && _blurredAlbumArtImageSource != null)
        {
            UpdateBackgroundMediaVisibility();
            return;
        }

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

                    using var scaled = new Microsoft.Graphics.Canvas.Effects.ScaleEffect
                    {
                        Source = bitmap,
                        Scale = new System.Numerics.Vector2(scale, scale),
                        CenterPoint = System.Numerics.Vector2.Zero
                    };

                    using var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
                    {
                        Source = scaled,
                        BlurAmount = AlbumArtBlurAmount,
                        BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Hard
                    };

                    using var saturation = new SaturationEffect
                    {
                        Source = blur,
                        Saturation = AlbumArtSaturationAmount
                    };

                    ds.DrawImage(saturation, new System.Numerics.Vector2(offsetX, offsetY));
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
                _blurredAlbumArtRenderWidth = w;
                _blurredAlbumArtRenderHeight = h;
                UpdateBackgroundMediaVisibility();
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
            UpdateBackgroundMediaVisibility();
        }
    }

    private void TeardownBlurredAlbumArt()
    {
        _detailsBackgroundGeneration++;
        _currentAlbumArtUrl = null;
        _blurredAlbumArtRenderWidth = 0;
        _blurredAlbumArtRenderHeight = 0;
        if (ReferenceEquals(DetailsCanvasImage.Source, _blurredAlbumArtImageSource))
            DetailsCanvasImage.Source = null;
        DisposeCanvasImageSource(ref _blurredAlbumArtImageSource);
        UpdateBackgroundMediaVisibility();
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

    /// <summary>
    /// How much vertical space to reserve below the canvas for the "always-visible"
    /// bottom cards (artist header + output device card, with their 16px spacing).
    /// Falls back to a sensible default when the cards haven't measured yet.
    /// </summary>
    private double GetCanvasBottomReservedHeight()
    {
        const double StackPanelSpacing = 16;
        const double Padding = 12;

        double artistHeight = DetailsArtistHeaderCard?.Visibility == Visibility.Visible
            ? (DetailsArtistHeaderCard.ActualHeight > 0 ? DetailsArtistHeaderCard.ActualHeight : 84)
            : 0;

        double deviceHeight = DetailsOutputDeviceCard?.Visibility == Visibility.Visible
            ? (DetailsOutputDeviceCard.ActualHeight > 0 ? DetailsOutputDeviceCard.ActualHeight : 68)
            : 0;

        if (artistHeight == 0 && deviceHeight == 0) return 120;

        double spacing = (artistHeight > 0 && deviceHeight > 0) ? StackPanelSpacing : 0;
        return artistHeight + deviceHeight + spacing + Padding;
    }

    private void ApplyCanvasLayout(bool animate = true)
    {
        if (DetailsCanvasSpacer == null) return;

        var isCanvas = _activeBackgroundMode == DetailsBackgroundMode.Canvas
                       && SelectedMode == RightPanelMode.Details;

        var reservedBelow = GetCanvasBottomReservedHeight();
        var targetHeight = isCanvas
            ? Math.Max(0, DetailsContent.ActualHeight - reservedBelow)
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
            DetailsCanvasSpacer.Height = Math.Max(0, DetailsContent.ActualHeight - GetCanvasBottomReservedHeight());
        }

    }

    /// <summary>
    /// Invoked on the first wheel-scroll down while canvas is visible. Collapses the
    /// canvas spacer by exactly the height of the output device card (plus a small
    /// padding), so the card slides up from behind the canvas and is anchored at the
    /// top of the content area. The canvas itself remains visible above it.
    /// </summary>
    private void AnchorOutputDeviceCardOnScroll()
    {
        if (DetailsCanvasSpacer == null || DetailsOutputDeviceCard == null) return;

        // Card hasn't measured yet → fall back to a sensible default.
        var cardHeight = DetailsOutputDeviceCard.ActualHeight > 0
            ? DetailsOutputDeviceCard.ActualHeight
            : 68;

        // 16 accounts for the StackPanel.Spacing between cards.
        var collapseBy = cardHeight + 16;
        var current = DetailsCanvasSpacer.Height;
        var target = Math.Max(0, current - collapseBy);
        if (Math.Abs(current - target) < 1) return;

        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var da = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = current,
            To = target,
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(da, DetailsCanvasSpacer);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(da, "Height");
        storyboard.Children.Add(da);
        storyboard.Begin();
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
            DetailsArtistHeaderCard, DetailsOutputDeviceCard, DetailsLyricsSnippet, DetailsAiMeaningSection, DetailsBio,
            DetailsCreditsSection, DetailsConcertsSection, DetailsRelatedVideosSection
        };
        return all.Where(c => c != null && c.Visibility == Visibility.Visible).ToArray();
    }

    private void DetailsContent_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (SelectedMode != RightPanelMode.Details) return;

        var props = e.GetCurrentPoint(DetailsContent).Properties;
        var delta = props.MouseWheelDelta;
        if (delta == 0) return;

        // When a canvas is visible, the DetailsCanvasSpacer pushes all cards below the fold
        // (by ~ DetailsContent.ActualHeight - 120). On the first scroll down from rest, collapse
        // enough of that spacer to reveal the output device card so it "hooks" onto the
        // canvas area rather than staying hidden. Subsequent scrolls fall through to the
        // normal card-paging logic below.
        if (delta < 0
            && _activeBackgroundMode == DetailsBackgroundMode.Canvas
            && DetailsCanvasSpacer != null
            && DetailsOutputDeviceCard != null
            && DetailsOutputDeviceCard.Visibility == Visibility.Visible
            && DetailsContent.VerticalOffset < 1
            && DetailsCanvasSpacer.Height > 0)
        {
            AnchorOutputDeviceCardOnScroll();
            e.Handled = true;
            return;
        }

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
            return;
        }

        if (url == _currentCanvasUrl && _canvasMediaPlayer != null) return;

        TeardownCanvasBackground();
        _currentCanvasUrl = url;
        ResetCanvasFrameScheduling();

        _canvasDevice ??= new CanvasDevice();

        _canvasMediaPlayer = new Windows.Media.Playback.MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true,
            IsVideoFrameServerEnabled = true // Frame server mode — no swap chain
        };
        _canvasMediaPlayer.VideoFrameAvailable += OnCanvasVideoFrameAvailable;
        _canvasMediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(url));

        UpdateBackgroundMediaVisibility();
        _canvasMediaPlayer.Play();
    }

    private void OnCanvasVideoFrameAvailable(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastCanvasFrameRenderTimestamp);
        if (last != 0 && now - last < CanvasFrameMinIntervalTicks)
            return;

        Interlocked.Exchange(ref _lastCanvasFrameRenderTimestamp, now);
        QueueCanvasFrameRender();
    }

    private void TeardownCanvasBackground()
    {
        _detailsBackgroundGeneration++;
        ResetCanvasFrameScheduling();
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
        Interlocked.Exchange(ref _lastCanvasFrameRenderTimestamp, 0);
        // Keep _canvasDevice alive for reuse

        _currentCanvasUrl = null;
        UpdateBackgroundMediaVisibility();
    }

    private void QueueCanvasFrameRender()
    {
        var shouldQueue = false;

        lock (_canvasFrameRenderGate)
        {
            _canvasFramePending = true;
            if (!_canvasFrameRenderQueued)
            {
                _canvasFrameRenderQueued = true;
                shouldQueue = true;
            }
        }

        if (!shouldQueue)
            return;

        if (!DispatcherQueue.TryEnqueue(ProcessCanvasFrameRender))
            ResetCanvasFrameScheduling();
    }

    private void ProcessCanvasFrameRender()
    {
        var requeue = false;

        try
        {
            lock (_canvasFrameRenderGate)
            {
                _canvasFramePending = false;
            }

            RenderCanvasFrame();
        }
        finally
        {
            lock (_canvasFrameRenderGate)
            {
                if (_canvasFramePending)
                {
                    requeue = true;
                }
                else
                {
                    _canvasFrameRenderQueued = false;
                }
            }

            if (requeue && !DispatcherQueue.TryEnqueue(ProcessCanvasFrameRender))
                ResetCanvasFrameScheduling();
        }
    }

    private void RenderCanvasFrame()
    {
        if (_canvasMediaPlayer == null || _canvasDevice == null)
            return;

        var w = Math.Max(1, (int)RootGrid.ActualWidth);
        var h = Math.Max(1, (int)RootGrid.ActualHeight);
        if (w <= 0 || h <= 0)
            return;

        var naturalW = (int)(_canvasMediaPlayer.PlaybackSession?.NaturalVideoWidth ?? 0u);
        var naturalH = (int)(_canvasMediaPlayer.PlaybackSession?.NaturalVideoHeight ?? 0u);
        var sourceW = Math.Max(1, naturalW > 0 ? naturalW : w);
        var sourceH = Math.Max(1, naturalH > 0 ? naturalH : h);

        try
        {
            // Hold at most one UI-thread render task at a time and drop intermediate
            // frames when the dispatcher is behind, instead of queueing unbounded work.
            if (_canvasFrameTarget == null || _canvasFrameTarget.SizeInPixels.Width != sourceW || _canvasFrameTarget.SizeInPixels.Height != sourceH)
            {
                _canvasFrameTarget?.Dispose();
                _canvasFrameTarget = new CanvasRenderTarget(_canvasDevice, sourceW, sourceH, 96);
            }

            _canvasMediaPlayer.CopyFrameToVideoSurface(_canvasFrameTarget);

            if (_canvasImageSource == null || _canvasImageSource.SizeInPixels.Width != w || _canvasImageSource.SizeInPixels.Height != h)
            {
                if (ReferenceEquals(DetailsCanvasImage.Source, _canvasImageSource))
                    DetailsCanvasImage.Source = null;
                DisposeCanvasImageSource(ref _canvasImageSource);
                _canvasImageSource = new CanvasImageSource(_canvasDevice, w, h, 96);
                DetailsCanvasImage.Source = _canvasImageSource;
            }

            using var ds = _canvasImageSource.CreateDrawingSession(Colors.Transparent);
            using var saturation = new SaturationEffect
            {
                Source = _canvasFrameTarget,
                Saturation = CanvasSaturationAmount
            };

            var scaleX = (float)w / sourceW;
            var scaleY = (float)h / sourceH;
            var scale = Math.Max(scaleX, scaleY);
            var offsetX = (w - (sourceW * scale)) / 2f;
            var offsetY = (h - (sourceH * scale)) / 2f;

            using var scaled = new ScaleEffect
            {
                Source = saturation,
                Scale = new Vector2(scale, scale),
                CenterPoint = Vector2.Zero
            };

            ds.DrawImage(scaled, new Vector2(offsetX, offsetY));
            UpdateBackgroundMediaVisibility();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ResetCanvasFrameScheduling();

            _canvasDevice?.Dispose();
            _canvasDevice = null;
            _canvasFrameTarget?.Dispose();
            _canvasFrameTarget = null;
            if (ReferenceEquals(DetailsCanvasImage.Source, _canvasImageSource))
                DetailsCanvasImage.Source = null;
            DisposeCanvasImageSource(ref _canvasImageSource);

            var url = _currentCanvasUrl;
            _currentCanvasUrl = null;
            if (!string.IsNullOrEmpty(url))
                SetupCanvasBackground(url);
        }
    }

    private void ResetCanvasFrameScheduling()
    {
        lock (_canvasFrameRenderGate)
        {
            _canvasFramePending = false;
            _canvasFrameRenderQueued = false;
        }

        Interlocked.Exchange(ref _lastCanvasFrameRenderTimestamp, 0);
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
