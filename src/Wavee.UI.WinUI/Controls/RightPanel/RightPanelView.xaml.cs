using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Controls.RightPanel.Details;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Services.Docking;
using Wavee.UI.WinUI.ViewModels;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.RightPanel;

/// <summary>
/// The right-side panel — Queue / Lyrics / Friends / Details / Track-details
/// tabs hosted under a shared chrome (resize gripper, tab header, background
/// composition). Acts as a thin composer: tab strip, lyrics canvas, Details
/// subtree, and background composition are delegated to
/// <see cref="RightPanelTabPager"/>, <see cref="Lyrics.LyricsCanvasHost"/>,
/// <see cref="DetailsTabHost"/>, and <see cref="BackgroundOverlayCompositionBehavior"/>
/// respectively.
/// </summary>
/// <remarks>
/// What this code-behind still owns: the resize gripper, background tint
/// extraction (album-art driven), theme/palette resolution against
/// <see cref="ThemeColorService"/>, the embedded-chrome path, the Track Details
/// (TrackDataGrid-driven) tab, and the deferred-subtree materialization plumbing
/// (<c>x:Load="False"</c> bookkeeping for Lyrics / Details / TrackDetails).
/// Everything else lives in the sub-controls / behavior / theme resolver
/// mentioned above.
/// </remarks>
public sealed partial class RightPanelView : UserControl
{
    private const double MinPanelWidth = 200;
    private const double MaxPanelWidth = 500;

    private bool _draggingResizer;
    private double _preManipulationWidth;
    private bool _isOpenCached;
    // Re-entry guard consumed by OnSelectedModeChanged in the .Properties.cs
    // partial. See the comment there for the binding-bounce scenario that
    // makes this necessary.
    private bool _inSelectedModeChange;

    // Tracks whether the deferred LyricsContent / DetailsContent subtrees have been
    // materialized into the visual tree yet. Both use x:Load="False" in XAML and are
    // loaded on demand when their tab is first selected. Once loaded, they stay loaded.
    private bool _lyricsTreeLoaded;
    private bool _detailsTreeLoaded;
    private bool _trackDetailsTreeLoaded;
    private PropertyChangedEventHandler? _shellViewModelTrackDetailsHandler;
    private ShellViewModel? _shellViewModelForTrackDetails;

    // Lyrics integration (subset still tracked here — drives Details-tab snippet
    // visibility via DetailsTabHost when materialised; the lyrics-tab canvas
    // wiring lives inside LyricsCanvasHost).
    private readonly LyricsViewModel? _lyricsVm;
    private bool _lyricsConsumerActive;
    private bool _pendingCanvasLayoutRetry;

    private readonly ThemeColorService? _themeColors;
    private readonly ILyricsService? _lyricsService;
    private readonly IColorService? _colorService;
    private bool _themeColorsSubscribed;

    private readonly BackgroundOverlayCompositionBehavior _overlayBehavior = new();

    private CancellationTokenSource? _backgroundTintCts;
    private string? _backgroundTintImageUrl;
    private ExtractedColor? _backgroundTintExtractedColor;

    public RightPanelView()
    {
        InitializeComponent();
        _themeColors = Ioc.Default.GetService<ThemeColorService>();
        _lyricsService = Ioc.Default.GetService<ILyricsService>();
        _colorService = Ioc.Default.GetService<IColorService>();
        _lyricsVm = Ioc.Default.GetService<LyricsViewModel>();
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

        // Wire up the lyrics VM subscription on the parent so we can drive
        // background-tint refresh on album-art changes. The lyrics-canvas
        // wiring is handled inside LyricsCanvasHost; the Details snippet /
        // canvas overlay / AI meaning wiring is handled inside DetailsTabHost.
        if (_lyricsVm != null)
            _lyricsVm.PlaybackState.PropertyChanged += OnParentPlaybackStateChanged;

        // Build the composition stacks once the visual tree is realised.
        _overlayBehavior.Attach(BackgroundOverlayHost, TabContentFadeHost);

        ActualThemeChanged += OnActualThemeChanged;
        SizeChanged += OnPanelSizeChanged;
        UpdateCanvasClearColor();
        ApplyEmbeddedChrome();
        UpdateBackgroundChrome();
        RefreshBackgroundTint();

        TabPager?.SyncVisualState();
        // Release the suppression flag now that the SelectedMode binding has
        // settled and the visual selection matches it. From here on, real user
        // taps on the tab bar are honored.
        TabPager?.ReleaseInitialSelectionSuppression();

        // Re-apply tab content visibility now that we're loaded.
        UpdateContentVisibility();
        UpdateLyricsConsumerActivity();

        // Initial sync — if we're loaded mid-playback into a podcast episode,
        // the tab should already say "Transcript".
        UpdateLyricsTabLabelForCurrentItem();
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SelectedMode == RightPanelMode.Lyrics)
            RequestThrottledCanvasLayout();

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

        if (_lyricsVm != null)
            _lyricsVm.PlaybackState.PropertyChanged -= OnParentPlaybackStateChanged;

        ActualThemeChanged -= OnActualThemeChanged;
        SizeChanged -= OnPanelSizeChanged;
        UpdateLyricsConsumerActivity(active: false);
        TeardownLyrics();

        if (LyricsContent != null)
        {
            LyricsContent.DebugRequested -= OnLyricsDebugRequested;
            LyricsContent.CanvasLayoutInvalidated -= OnLyricsHostCanvasLayoutInvalidated;
        }

        if (DetailsContent != null)
        {
            DetailsContent.LyricsSnippetTapped -= DetailsContent_LyricsSnippetTapped;
            DetailsContent.BackgroundChromeInvalidated -= DetailsContent_BackgroundChromeInvalidated;
            DetailsContent.BackgroundModeChanged -= DetailsContent_BackgroundModeChanged;
            DetailsContent.TeardownDetails();
        }

        CancelBackgroundTintRefresh();
        _overlayBehavior.Detach();

        if (_resizeThrottleTimer != null)
        {
            _resizeThrottleTimer.Stop();
            _resizeThrottleTimer.Tick -= OnResizeThrottleTick;
            _resizeThrottleTimer = null;
        }
        _layoutPendingDuringThrottle = false;
    }

    private void OnThemeColorsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateCanvasClearColor();
            UpdateTabContentFadeColor();
            UpdateLyricsPaletteForTheme();
            UpdateBackgroundChrome();
            TabPager?.SyncVisualState();
        });
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCanvasClearColor();
        UpdateTabContentFadeColor();
        UpdateLyricsPaletteForTheme();
        UpdateBackgroundChrome();
        TabPager?.SyncVisualState();
    }

    private void UpdateLyricsPaletteForTheme()
        => LyricsContent?.ApplyLyricsPalette(ActualTheme);

    // ── Lyrics host wiring ──

    /// <summary>
    /// Initialise the lyrics host only when its subtree is materialised. The
    /// host owns the canvas wiring; we just call into it.
    /// </summary>
    private void InitializeLyrics()
    {
        if (LyricsContent == null) return;
        LyricsContent.InitializeLyrics();
        UpdateLyricsPaletteForTheme();
    }

    private void TeardownLyrics()
    {
        LyricsContent?.TeardownLyrics();
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

    // Forwarded from <NowPlayingCanvas HoverActionInvoked=...>. The host
    // delegates back to the AI panel — keeps the canvas event wired to XAML.
    private async void NowPlayingCanvas_HoverActionInvoked(object? sender, int lineIndex)
    {
        if (LyricsContent != null)
            await LyricsContent.HandleHoverActionInvokedAsync(lineIndex);
    }

    private void OnLyricsHostCanvasLayoutInvalidated(object? sender, EventArgs e)
        => RequestThrottledCanvasLayout();

    private void OnLyricsDebugRequested(object? sender, EventArgs e)
        => _ = ShowLyricsDebugDialogAsync();

    // ── Playback state — parent-side concerns ──

    /// <summary>
    /// Parent-side playback-state listener — refreshes background tint when
    /// the album art changes. The Details snippet / canvas / podcast timer
    /// have their own subscription inside <see cref="DetailsTabHost"/>; the
    /// lyrics canvas has its own inside <see cref="Lyrics.LyricsCanvasHost"/>.
    /// </summary>
    private void OnParentPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Update the shared media treatment when playback visuals change.
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentAlbumArtLarge)
                           or nameof(IPlaybackStateService.CurrentAlbumArt))
        {
            RefreshBackgroundTint();
            UpdateBackgroundChrome();
        }

        // Track changes invalidate the chrome (Details host has already cleared
        // the canvas treatment from its own subscription).
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
                           or nameof(IPlaybackStateService.CurrentArtistId))
        {
            UpdateBackgroundChrome();
        }

        if (e.PropertyName == nameof(IPlaybackStateService.CurrentTrackId))
            UpdateLyricsTabLabelForCurrentItem();
    }

    /// <summary>
    /// Pulls the current episode/track classification from
    /// <see cref="LyricsViewModel.IsEpisode"/> and forwards the appropriate
    /// localized label to the tab pager. Called whenever the playing item
    /// changes — the VM has already raised its own <c>IsEpisode</c> change by
    /// the time this fires, since its subscription was wired first.
    /// </summary>
    private void UpdateLyricsTabLabelForCurrentItem()
    {
        if (TabPager == null) return;
        var isEpisode = _lyricsVm?.IsEpisode == true;
        var key = isEpisode
            ? "Controls_RightPanel_RightPanelView__SegmentedItem_2_Transcript.Content"
            : "Controls_RightPanel_RightPanelView__SegmentedItem_2.Content";
        var label = AppLocalization.GetString(key);
        // Fallback when the resw key is missing (e.g. partial localization).
        if (string.IsNullOrEmpty(label) || label == key)
            label = isEpisode ? "Transcript" : "Lyrics";
        TabPager.LyricsTabContent = label;
    }

    // ── Canvas layout ──

    // Resize throttle. LyricsLayoutManager.MeasureAndArrange creates one
    // CanvasTextLayout per line on every layout pass — for a long podcast
    // transcript (~1000 sentence lines) that's 1000 layouts × 60fps = 60k
    // CanvasTextLayout creations/sec while the user drags the panel edge.
    // The dirty-flag pattern in LyricsEngine already coalesces multiple
    // SetLyricsWidth/Height calls per frame, but per-frame is still too
    // aggressive. We apply once on the leading edge, then suppress further
    // applies for ~120 ms and only run the trailing apply if anything new
    // came in. The user sees the canvas reflow at ~8fps during drag — still
    // smooth visually, but ~7× cheaper than per-frame.
    private DispatcherTimer? _resizeThrottleTimer;
    private bool _layoutPendingDuringThrottle;

    private void RequestThrottledCanvasLayout()
    {
        if (_resizeThrottleTimer == null)
        {
            _resizeThrottleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _resizeThrottleTimer.Tick += OnResizeThrottleTick;
        }

        if (!_resizeThrottleTimer.IsEnabled)
        {
            // Leading edge — apply once immediately so the first feedback is
            // instant, then close the gate until the timer expires.
            UpdateCanvasLayout();
            _resizeThrottleTimer.Start();
            return;
        }

        // Inside throttle window — coalesce. The trailing tick will apply
        // the final size once the window closes.
        _layoutPendingDuringThrottle = true;
    }

    private void OnResizeThrottleTick(object? sender, object e)
    {
        if (_resizeThrottleTimer == null) return;
        _resizeThrottleTimer.Stop();

        if (!_layoutPendingDuringThrottle)
            return;

        _layoutPendingDuringThrottle = false;
        UpdateCanvasLayout();

        // Another resize burst may still be in flight — restart the gate so
        // subsequent SizeChanged events continue to coalesce.
        _resizeThrottleTimer.Start();
    }

    private void UpdateCanvasLayout()
    {
        if (SelectedMode != RightPanelMode.Lyrics || NowPlayingCanvas == null || !_isOpenCached)
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
        var tabH = TabPager?.ActualHeight ?? 0;
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
        NowPlayingCanvas.AlbumArtRect = Windows.Foundation.Rect.Empty;

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
            if (NowPlayingCanvas.LyricsWidth > 0 && SelectedMode == RightPanelMode.Lyrics)
                LyricsContent?.ApplyCurrentLyricsState();
        });
    }

    private void UpdateCanvasClearColor()
    {
        var color = RightPanelThemeResolver.ComputeCanvasClearColor(
            ActualTheme,
            IsEmbeddedChromeTransparent,
            _themeColors);
        NowPlayingCanvas.SetClearColor(color);
    }

    // ── Background chrome plumbing — pushes precomputed colours through the
    //    behavior. All colour math lives in RightPanelThemeResolver. ──

    private void UpdateBackgroundChrome()
    {
        if (!_overlayBehavior.IsAttached)
            return;

        var tintColor = RightPanelThemeResolver.GetBackgroundTintColor(
            ActualTheme, _themeColors, _backgroundTintExtractedColor);
        var surfaceColor = RightPanelThemeResolver.GetPanelSurfaceColor(ActualTheme, _themeColors);
        var blendColor = RightPanelThemeResolver.BlendColors(
            surfaceColor,
            tintColor,
            ActualTheme == ElementTheme.Light ? 0.32f : 0.44f);
        var bottomColor = RightPanelThemeResolver.Darken(
            blendColor,
            ActualTheme == ElementTheme.Light ? 0.08f : 0.22f);

        _overlayBehavior.ApplyBackgroundColors(new BackgroundOverlayColors(
            TintColor: tintColor,
            NonDetailsDimColor: RightPanelThemeResolver.ResolveThemeColor(
                ActualTheme,
                "RightPanelBackgroundNonDetailsDimBrush",
                ActualTheme == ElementTheme.Light
                    ? Color.FromArgb(255, 10, 12, 16)
                    : Color.FromArgb(255, 9, 11, 17)),
            HighlightStart: RightPanelThemeResolver.ResolveThemeColor(
                ActualTheme,
                "RightPanelBackgroundHighlightStartColor",
                Color.FromArgb(86, 255, 255, 255)),
            HighlightMid: RightPanelThemeResolver.ResolveThemeColor(
                ActualTheme,
                "RightPanelBackgroundHighlightMidColor",
                Color.FromArgb(22, 255, 255, 255)),
            HighlightEnd: RightPanelThemeResolver.ResolveThemeColor(
                ActualTheme,
                "RightPanelBackgroundHighlightEndColor",
                Color.FromArgb(0, 255, 255, 255)),
            ScrimTop: RightPanelThemeResolver.ResolveThemeColor(
                ActualTheme,
                "RightPanelBackgroundShadowTopColor",
                Color.FromArgb(24, 0, 0, 0)),
            ScrimMid: RightPanelThemeResolver.ResolveThemeColor(
                ActualTheme,
                "RightPanelBackgroundShadowMidColor",
                Color.FromArgb(8, 0, 0, 0)),
            ScrimBottom: RightPanelThemeResolver.ResolveThemeColor(
                ActualTheme,
                "RightPanelBackgroundShadowBottomColor",
                Color.FromArgb(110, 0, 0, 0)),
            BottomBlendColor: bottomColor));

        UpdateBackgroundOverlayState();
    }

    private void UpdateBackgroundOverlayState()
    {
        if (IsEmbeddedChromeTransparent)
        {
            _overlayBehavior.ApplyState(OverlayState.EmbeddedTransparent);
            return;
        }

        // The Details host owns the canvas image; ask it whether canvas chrome
        // should show. When the Details subtree hasn't materialised yet, fall
        // back to "hidden" (the parent's UpdateContentVisibility will re-evaluate
        // once the host loads).
        var showDetailsCanvasChrome = SelectedMode == RightPanelMode.Details
                                      && DetailsContent != null
                                      && DetailsContent.ActiveBackgroundMode == DetailsBackgroundMode.Canvas
                                      && DetailsContent.IsCanvasMediaVisible;

        _overlayBehavior.ApplyState(showDetailsCanvasChrome ? OverlayState.DetailsCanvas : OverlayState.Hidden);
    }

    /// <summary>
    /// Update the panel-wide background overlay's tint container opacity based
    /// on whether the Details host is currently showing canvas media.
    /// </summary>
    private void UpdateBackgroundMediaVisibility()
    {
        if (BackgroundOverlayHost == null)
            return;

        var hasMedia = DetailsContent != null
                       && DetailsContent.ActiveBackgroundMode == DetailsBackgroundMode.Canvas
                       && DetailsContent.IsCanvasMediaVisible;
        BackgroundOverlayHost.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        _overlayBehavior.SetBackgroundContainerOpacity(hasMedia ? 1f : 0f);
        UpdateBackgroundOverlayState();
    }

    private void UpdateTabContentFadeColor()
    {
        var target = RightPanelThemeResolver.ResolveTabFadeTargetColor(
            ActualTheme,
            IsEmbeddedChromeTransparent,
            EmbeddedHostTintColor,
            _themeColors,
            _backgroundTintExtractedColor);
        _overlayBehavior.ApplyTabFadeColor(target, IsEmbeddedChromeTransparent);
    }

    // ── Background tint extraction ──

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
            return;
        }

        CancelBackgroundTintRefresh();
        _backgroundTintImageUrl = imageUrl;
        _backgroundTintExtractedColor = null;
        UpdateBackgroundChrome();
        UpdateTabContentFadeColor();

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
    }

    private void CancelBackgroundTintRefresh()
    {
        _backgroundTintCts?.Cancel();
        _backgroundTintCts?.Dispose();
        _backgroundTintCts = null;
    }

    // ── Tab / visibility management ──

    private void UpdateContentVisibility()
    {
        if (QueueContent == null || !IsLoaded) return;

        var keepLyricsActive = ShouldKeepLyricsVmActive();

        // Materialize the deferred (x:Load="False") subtrees on first visible selection.
        // LyricsContent is materialised for BOTH Lyrics and Details modes: Details needs
        // the host live (even if hidden) so its VM subscription fires and drives the
        // Details snippet / canvas overlay when lyrics load asynchronously.
        if (keepLyricsActive) EnsureLyricsTreeLoaded();
        if (keepLyricsActive && SelectedMode == RightPanelMode.Details) EnsureDetailsTreeLoaded();
        if (_isOpenCached && SelectedMode == RightPanelMode.TrackDetails) EnsureTrackDetailsTreeLoaded();

        UpdateLyricsConsumerActivity(keepLyricsActive);

        // Push current tab/visibility state into the host BEFORE calling
        // InitializeLyrics. Host.ApplyCurrentLyricsState reads these DPs to
        // decide what to show; setting them first ensures the initial render
        // matches the active tab rather than the DP default (false / Queue).
        if (LyricsContent != null)
        {
            LyricsContent.IsLyricsTabActive = SelectedMode == RightPanelMode.Lyrics;
            LyricsContent.IsPanelVisible = Visibility == Visibility.Visible;
        }

        if (DetailsContent != null)
        {
            DetailsContent.IsDetailsTabActive = SelectedMode == RightPanelMode.Details;
            DetailsContent.IsPanelVisible = Visibility == Visibility.Visible;
        }

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

        // Keep the temporary TrackDetails tab visible only while the mode is active.
        if (TabPager != null)
            TabPager.IsTrackDetailsTabVisible = SelectedMode == RightPanelMode.TrackDetails;

        if (_isOpenCached && SelectedMode == RightPanelMode.TrackDetails)
            RefreshTrackDetailsContent();

        // Background overlay state depends on the Details host's mode.
        UpdateBackgroundMediaVisibility();

        if (_lyricsTreeLoaded && LyricsContent != null)
            LyricsContent.ApplyCurrentLyricsState();

        TabPager?.SyncVisualState();

        // When switching to lyrics tab, ensure we have the latest state
        if (keepLyricsActive && SelectedMode == RightPanelMode.Lyrics)
        {
            _ = _lyricsVm?.LoadLyricsAsync();
            UpdateCanvasLayout();
            LyricsContent?.ApplyCurrentLyricsState();
            if (_lyricsVm != null && NowPlayingCanvas != null)
            {
                NowPlayingCanvas.SetPosition(_lyricsVm.GetInterpolatedPosition());
                NowPlayingCanvas.SetIsPlaying(_lyricsVm.PlaybackState.IsPlaying);
            }
        }
    }

    private void ApplyTabHeaderVisibility()
    {
        if (TabPager != null)
            TabPager.Visibility = IsTabHeaderVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TabPager_PopOutRequested(object? sender, EventArgs e)
    {
        Ioc.Default.GetService<IPanelDockingService>()?.Detach(DetachablePanel.RightPanel);
    }

    // ── Deferred subtree materialization (x:Load="False") ──

    private void EnsureLyricsTreeLoaded()
    {
        if (_lyricsTreeLoaded) return;
        _ = FindName(nameof(LyricsContent));
        _lyricsTreeLoaded = LyricsContent != null;
        if (_lyricsTreeLoaded && LyricsContent != null)
        {
            // Set the canvas reference explicitly — relying solely on the x:Bind in
            // XAML is fragile when LyricsContent is x:Load'd late (the bind runs
            // before this code path catches it on the first manual trigger).
            LyricsContent.NowPlayingCanvas = NowPlayingCanvas;
            LyricsContent.CanvasLayoutInvalidated += OnLyricsHostCanvasLayoutInvalidated;
            LyricsContent.DebugRequested += OnLyricsDebugRequested;
        }
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

        if (_detailsTreeLoaded && DetailsContent != null)
        {
            DetailsContent.LyricsSnippetTapped += DetailsContent_LyricsSnippetTapped;
            DetailsContent.BackgroundChromeInvalidated += DetailsContent_BackgroundChromeInvalidated;
            DetailsContent.BackgroundModeChanged += DetailsContent_BackgroundModeChanged;
            DetailsContent.IsEmbeddedChromeTransparent = IsEmbeddedChromeTransparent;
            DetailsContent.InitializeDetails();
            InitializeOutputDeviceCard();
        }
    }

    // ── DetailsTabHost event handlers ──

    private void DetailsContent_LyricsSnippetTapped(object? sender, EventArgs e)
    {
        SelectedMode = RightPanelMode.Lyrics;
    }

    private void DetailsContent_BackgroundChromeInvalidated(object? sender, EventArgs e)
    {
        // The Details host changed its background mode or canvas-media visibility;
        // re-evaluate the panel-wide overlay composition.
        UpdateBackgroundMediaVisibility();
    }

    private void DetailsContent_BackgroundModeChanged(object? sender, EventArgs e)
    {
        // User picked a different background mode from the Details More menu;
        // the tint plumbing reads the same image URL but reuses cached colors,
        // so a chrome refresh is sufficient.
        UpdateBackgroundChrome();
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

    private async Task ShowLyricsDebugDialogAsync()
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
