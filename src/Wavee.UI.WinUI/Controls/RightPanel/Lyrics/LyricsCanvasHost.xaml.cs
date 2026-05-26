using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.Controls.Lyrics.Models;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.RightPanel.Lyrics;

/// <summary>
/// Hosts the Lyrics-tab subtree (interaction overlay, sync button, loading
/// shimmer, no-lyrics fallback, AI panel) plus all the wiring that ties the
/// shared <see cref="NowPlayingCanvas"/> to <see cref="LyricsViewModel"/> —
/// position timer, pointer interactions, AI affordance.
/// </summary>
/// <remarks>
/// <para>
/// The actual <see cref="NowPlayingCanvas"/> element is a panel-wide
/// background that spans columns 0+1 of the parent's root grid, so it must
/// stay in <see cref="RightPanelView"/>. The host receives a reference via
/// <see cref="NowPlayingCanvas"/> (a DP) and drives the canvas from here.
/// </para>
/// <para>
/// Timer ownership: this host owns the lyrics-timeline-sync timer. The
/// details-snippet timer and the podcast-chapter-timeline timer stay on the
/// parent — they're driven by Details-tab state that lives there. Splitting
/// the timers avoids a single-timer tick handler that has to know about three
/// unrelated concerns.
/// </para>
/// </remarks>
public sealed partial class LyricsCanvasHost : UserControl
{
    private LyricsViewModel? _lyricsVm;
    private DispatcherQueueTimer? _positionTimer;
    private DispatcherQueueTimer? _scrollResetTimer;
    private double _lastCanvasPositionMs = -1;
    private bool _lyricsInitialized;
    private LyricsData? _appliedLyricsData;
    private SongInfo? _appliedSongInfo;
    private bool _lyricsCanvasDataCleared = true;
    private Wavee.Controls.Lyrics.Controls.NowPlayingCanvas? _canvas;

    private const double LyricsSyncTimerIntervalMs = 250;

    public LyricsCanvasHost()
    {
        InitializeComponent();
        _lyricsVm = Ioc.Default.GetService<LyricsViewModel>();
    }

    /// <summary>
    /// The panel-wide <see cref="Wavee.Controls.Lyrics.Controls.NowPlayingCanvas"/>
    /// element. Set by the parent in <c>Loaded</c> / after the host's deferred
    /// tree materialises; the host wires pointer/seek/render events against it.
    /// </summary>
    public Wavee.Controls.Lyrics.Controls.NowPlayingCanvas? NowPlayingCanvas
    {
        get => (Wavee.Controls.Lyrics.Controls.NowPlayingCanvas?)GetValue(NowPlayingCanvasProperty);
        set => SetValue(NowPlayingCanvasProperty, value);
    }
    public static readonly DependencyProperty NowPlayingCanvasProperty =
        DependencyProperty.Register(
            nameof(NowPlayingCanvas),
            typeof(Wavee.Controls.Lyrics.Controls.NowPlayingCanvas),
            typeof(LyricsCanvasHost),
            new PropertyMetadata(null, OnNowPlayingCanvasChanged));

    /// <summary>
    /// Whether the parent's Lyrics tab is currently selected. Drives
    /// shimmer/canvas/no-lyrics visibility and timer activity.
    /// </summary>
    public bool IsLyricsTabActive
    {
        get => (bool)GetValue(IsLyricsTabActiveProperty);
        set => SetValue(IsLyricsTabActiveProperty, value);
    }
    public static readonly DependencyProperty IsLyricsTabActiveProperty =
        DependencyProperty.Register(
            nameof(IsLyricsTabActive),
            typeof(bool),
            typeof(LyricsCanvasHost),
            new PropertyMetadata(false, OnReactiveDpChanged));

    /// <summary>
    /// Whether the parent panel is visible (open). Combined with
    /// <see cref="IsLyricsTabActive"/> to gate timer activity.
    /// </summary>
    public bool IsPanelVisible
    {
        get => (bool)GetValue(IsPanelVisibleProperty);
        set => SetValue(IsPanelVisibleProperty, value);
    }
    public static readonly DependencyProperty IsPanelVisibleProperty =
        DependencyProperty.Register(
            nameof(IsPanelVisible),
            typeof(bool),
            typeof(LyricsCanvasHost),
            new PropertyMetadata(false, OnReactiveDpChanged));

    /// <summary>
    /// Raised when canvas layout dimensions need recomputing — fires on
    /// <c>SizeChanged</c> of the host. The parent owns the layout math
    /// (depends on panel-wide grid measurements), so the host just signals.
    /// </summary>
    public event EventHandler? CanvasLayoutInvalidated;

    private static void OnNowPlayingCanvasChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsCanvasHost host)
        {
            host._canvas = e.NewValue as Wavee.Controls.Lyrics.Controls.NowPlayingCanvas;
        }
    }

    private static void OnReactiveDpChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsCanvasHost host)
            host.UpdateTimerState();
    }

    // ── Lifecycle ──

    /// <summary>
    /// Bind the host to the lyrics ViewModel and start the canvas wiring.
    /// Idempotent.
    /// </summary>
    public void InitializeLyrics()
    {
        if (_lyricsInitialized || _lyricsVm == null || _canvas == null) return;

        System.Diagnostics.Debug.WriteLine("[mem] LyricsCanvasHost.InitializeLyrics");
        _lyricsInitialized = true;

        // Configure the canvas. The sidebar uses the BetterLyrics pure-color
        // background on every tab, so only the heavier animated overlays stay off.
        _canvas.LyricsWindowStatus = _lyricsVm.WindowStatus;
        var bg = _lyricsVm.WindowStatus.LyricsBackgroundSettings;
        bg.IsPureColorOverlayEnabled = true;
        bg.PureColorOverlayOpacity = 78;
        bg.IsFluidOverlayEnabled = false;
        bg.IsCoverOverlayEnabled = false;
        bg.IsSpectrumOverlayEnabled = false;
        bg.IsFogOverlayEnabled = false;
        bg.IsRaindropOverlayEnabled = false;
        bg.IsSnowFlakeOverlayEnabled = false;
        _canvas.SeekRequested += OnSeekRequested;

        // Subscribe to ViewModel state changes
        _lyricsVm.PropertyChanged += OnLyricsVmPropertyChanged;
        _lyricsVm.PlaybackState.PropertyChanged += OnPlaybackStateChanged;

        // Position timer - 250ms is enough for readable lyric progression
        // and keeps dispatcher pressure lower during playback.
        _positionTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(LyricsSyncTimerIntervalMs);
        _positionTimer.Tick += OnPositionTimerTick;

        // If there's already a track loaded, apply it
        ApplyCurrentLyricsState();
        UpdateTimerState();
    }

    /// <summary>Stop timers, unsubscribe, and clear canvas data. Idempotent.</summary>
    public void TeardownLyrics()
    {
        if (!_lyricsInitialized) return;

        System.Diagnostics.Debug.WriteLine("[mem] LyricsCanvasHost.TeardownLyrics");
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

        if (_lyricsVm != null)
        {
            _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
            _lyricsVm.PlaybackState.PropertyChanged -= OnPlaybackStateChanged;
        }

        if (_canvas != null)
        {
            _canvas.SeekRequested -= OnSeekRequested;
            _canvas.SetIsPlaying(false);
            _canvas.SetRenderingActive(false);
            if (!_lyricsCanvasDataCleared)
                _canvas.SetLyricsData(null);
            _canvas.SetSongInfo(new SongInfo { Title = "", Artist = "", Album = "" });
            _canvas.Visibility = IsPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        _appliedLyricsData = null;
        _appliedSongInfo = null;
        _lyricsCanvasDataCleared = true;
        _lyricsInitialized = false;
    }

    // ── Lyrics palette ──

    /// <summary>
    /// Push a freshly resolved lyrics palette into the canvas. The parent
    /// builds the palette from the current theme (it owns the theme service);
    /// this method just forwards.
    /// </summary>
    public void ApplyLyricsPalette(ElementTheme actualTheme)
    {
        if (_lyricsVm == null || _canvas == null) return;

        bool isDark = actualTheme != ElementTheme.Light;
        // Active (played + currently-playing) line: pure black/white for emphasis.
        // Off-current and upcoming lines: softer gray/light-gray so the queue reads
        // as de-emphasized instead of competing with the active line.
        var activeFg = isDark ? Colors.White : Colors.Black;
        var offFg = isDark
            ? Color.FromArgb(0xFF, 0xC0, 0xC0, 0xC0)
            : Color.FromArgb(0xFF, 0x55, 0x55, 0x55);

        var palette = _lyricsVm.WindowStatus.WindowPalette;
        palette.NonCurrentLineFillColor = offFg;
        palette.PlayedCurrentLineFillColor = activeFg;
        palette.UnplayedCurrentLineFillColor = offFg;
        palette.ThemeType = isDark
            ? ElementTheme.Dark
            : ElementTheme.Light;

        _canvas.SetNowPlayingPalette(palette);
    }

    // ── Playback state → Timer sync ──

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.IsPlaying))
        {
            SyncNowPlayingCanvasPosition();
            UpdateTimerState();
        }
        else if (e.PropertyName is nameof(IPlaybackStateService.Position))
        {
            SyncNowPlayingCanvasPosition();
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
            case nameof(LyricsViewModel.IsEpisode):
                ApplyCurrentLyricsState();
                LyricsStateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case nameof(LyricsViewModel.CurrentPalette):
                if (_lyricsVm?.CurrentPalette is { } palette && _canvas != null)
                    _canvas.SetNowPlayingPalette(palette);
                break;
        }
    }

    /// <summary>
    /// Raised when the lyrics ViewModel publishes a state change relevant to
    /// the Details-tab snippet — the parent listens and refreshes the snippet
    /// surface.
    /// </summary>
    public event EventHandler? LyricsStateChanged;

    /// <summary>
    /// Re-evaluate canvas/shimmer/no-lyrics visibility based on the current
    /// ViewModel + tab state. Called by the host on VM property changes; the
    /// parent invokes it whenever <see cref="IsLyricsTabActive"/> flips.
    /// </summary>
    public void ApplyCurrentLyricsState()
    {
        if (_lyricsVm == null || _canvas == null) return;

        var isLyricsMode = IsLyricsTabActive;
        var hasLyrics = _lyricsVm.HasLyrics && _lyricsVm.CurrentLyrics != null;
        var showLoadingShimmer = isLyricsMode && _lyricsVm.IsLoading && !hasLyrics;

        var showNoLyrics = isLyricsMode
                           && !_lyricsVm.IsLoading
                           && !_lyricsVm.HasLyrics
                           && !string.IsNullOrEmpty(_lyricsVm.PlaybackState.CurrentTrackId);

        // The parent-level NowPlayingCanvas always carries the BetterLyrics background;
        // lyric text and interactions are only active on the Lyrics tab.
        var showCanvas = isLyricsMode && hasLyrics;
        _canvas.Visibility = IsPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        _canvas.LyricsOpacity = showCanvas ? 1 : 0;

        if (!showCanvas)
        {
            _canvas.MouseScrollOffset = 0;
            _canvas.IsMouseScrolling = false;
        }

        // Prefer shimmer placeholder instead of ProgressRing while loading.
        LyricsLoadingRing.Visibility = Visibility.Collapsed;
        LyricsLoadingShimmer.Visibility = showLoadingShimmer ? Visibility.Visible : Visibility.Collapsed;
        NoLyricsText.Visibility = showNoLyrics ? Visibility.Visible : Visibility.Collapsed;
        if (showNoLyrics)
        {
            // Podcasts get a transcript, not lyrics — make the empty-state copy
            // match the labelled tab so the panel reads consistently.
            var emptyStateKey = _lyricsVm.IsEpisode
                ? "Controls_RightPanel_RightPanelView__TextBlock_5_Transcript.Text"
                : "Controls_RightPanel_RightPanelView__TextBlock_5.Text";
            var emptyText = AppLocalization.GetString(emptyStateKey);
            if (string.IsNullOrEmpty(emptyText) || emptyText == emptyStateKey)
                emptyText = _lyricsVm.IsEpisode
                    ? "No transcript available for this episode"
                    : "No lyrics available for this track";
            NoLyricsText.Text = emptyText;
        }
        LyricsInteractionOverlay.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;
        if (!showCanvas)
            LyricsSyncButton.Visibility = Visibility.Collapsed;
#if DEBUG
        LyricsDebugButton.Visibility = Visibility.Visible;
#endif

#if DEBUG
        System.Diagnostics.Debug.WriteLine(
            $"[LyricsCanvasHost] ApplyCurrentLyricsState isLyricsMode={isLyricsMode} " +
            $"hasLyrics={_lyricsVm.HasLyrics} isLoading={_lyricsVm.IsLoading} " +
            $"lineCount={_lyricsVm.CurrentLyrics?.LyricsLines.Count ?? 0} " +
            $"showCanvas={showCanvas} showNoLyrics={showNoLyrics}");
#endif

        // Push fresh XAML layout dimensions into the engine *before* handing it data.
        // Track changes do not fire SizeChanged on this control, so without this push
        // the engine can relayout a stale 0×0 cache and render nothing until the user
        // resizes the panel.
        if (showCanvas) CanvasLayoutInvalidated?.Invoke(this, EventArgs.Empty);

        if (showCanvas)
        {
            var lyrics = _lyricsVm.CurrentLyrics!;
            if (!ReferenceEquals(_appliedLyricsData, lyrics))
            {
                _canvas.SetLyricsData(lyrics);
                _appliedLyricsData = lyrics;
                _lyricsCanvasDataCleared = false;
            }

            var songInfo = _lyricsVm.CurrentSongInfo;
            if (!ReferenceEquals(_appliedSongInfo, songInfo))
            {
                _canvas.SetSongInfo(songInfo);
                _appliedSongInfo = songInfo;
            }

            _canvas.SetIsPlaying(_lyricsVm.PlaybackState.IsPlaying);
            var position = _lyricsVm.GetInterpolatedPosition();
            _lastCanvasPositionMs = position.TotalMilliseconds;
            _canvas.SetPosition(position);
        }
        else
        {
            // No lyrics and not loading — clear stale engine data so a subsequent
            // successful load doesn't accidentally composite on top of an old frame.
            _canvas.SetIsPlaying(false);
            _canvas.SetRenderingActive(false);
            if (!_lyricsCanvasDataCleared)
            {
                _canvas.SetLyricsData(null);
                _canvas.SetSongInfo(new SongInfo { Title = "", Artist = "", Album = "" });
                _appliedLyricsData = null;
                _appliedSongInfo = null;
                _lyricsCanvasDataCleared = true;
            }
        }

        UpdateTimerState();
    }

    /// <summary>
    /// Recompute timer activity and canvas rendering state based on current
    /// tab + playback + interaction. Called whenever any influencing input
    /// changes.
    /// </summary>
    public void UpdateTimerState()
    {
        if (_lyricsVm == null || _canvas == null)
        {
            _positionTimer?.Stop();
            _canvas?.SetRenderingActive(false);
            return;
        }

        var canRender = IsLyricsTabActive
                        && IsPanelVisible
                        && _lyricsVm.HasLyrics
                        && _lyricsVm.CurrentLyrics != null;

        var shouldRunSyncTimer = ShouldRunLyricsTimelineSyncTimer();

        // Keep rendering active only for realtime playback or direct user interaction.
        var isInteracting = _canvas.IsMouseInLyricsArea
                            || _canvas.IsMousePressing
                            || _canvas.IsMouseScrolling;
        var shouldRender = canRender && (_lyricsVm.PlaybackState.IsPlaying || isInteracting);

        _canvas.SetRenderingActive(shouldRender);
        _canvas.SetIsPlaying(canRender && _lyricsVm.PlaybackState.IsPlaying);

        if (shouldRunSyncTimer)
            _positionTimer?.Start();
        else
            _positionTimer?.Stop();

        if (!canRender)
        {
            _scrollResetTimer?.Stop();
            _canvas.SetIsPlaying(false);
            _lastCanvasPositionMs = -1;
        }
        else if (!_lyricsVm.PlaybackState.IsPlaying)
        {
            _canvas.SetIsPlaying(false);
            _lastCanvasPositionMs = -1;
        }
    }

    private bool ShouldRunLyricsTimelineSyncTimer()
    {
        var lyricsVm = _lyricsVm;
        if (lyricsVm == null)
            return false;

        return IsLyricsTabActive
            && IsPanelVisible
            && lyricsVm.PlaybackState.IsPlaying
            && lyricsVm.HasLyrics
            && lyricsVm.CurrentLyrics != null;
    }

    private void OnPositionTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (ShouldRunLyricsTimelineSyncTimer())
            SyncNowPlayingCanvasPosition();
    }

    // ── Seek ──

    private void OnSeekRequested(object? sender, TimeSpan position)
    {
        if (_lyricsInitialized && IsLyricsTabActive && _canvas != null)
            _canvas.SetPosition(position);

        _lyricsVm?.PlaybackState.Seek(position.TotalMilliseconds);
    }

    /// <summary>
    /// Push the current interpolated playback position into the canvas.
    /// Public because the parent's <c>OnPlaybackStateChanged</c> reaches in
    /// when album-art etc. changes.
    /// </summary>
    public void SyncNowPlayingCanvasPosition()
    {
        if (!_lyricsInitialized
            || _lyricsVm == null
            || _canvas == null
            || !IsLyricsTabActive
            || !IsPanelVisible
            || !_lyricsVm.HasLyrics
            || _lyricsVm.CurrentLyrics == null)
        {
            return;
        }

        var position = _lyricsVm.GetInterpolatedPosition();
        _lastCanvasPositionMs = position.TotalMilliseconds;
        _canvas.SetPosition(position);
    }

    // ── Mouse interaction ──

    private void LyricsOverlay_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null) return;
        _canvas.IsMouseInLyricsArea = true;
        UpdateTimerState();
    }

    private void LyricsOverlay_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null) return;
        _canvas.IsMouseInLyricsArea = false;
        UpdateTimerState();
    }

    private void LyricsOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null) return;
        var point = e.GetCurrentPoint(LyricsInteractionOverlay).Position;
        _canvas.MousePosition = point;
    }

    private void LyricsOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null) return;
        _canvas.IsMousePressing = true;
        UpdateTimerState();
    }

    private void LyricsOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null) return;
        _canvas.IsMousePressing = false;
        _canvas.FireSeekIfHovering();
        UpdateTimerState();
    }

    private void LyricsOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas == null) return;
        _canvas.IsMouseScrolling = true;
        LyricsSyncButton.Visibility = Visibility.Visible;
        UpdateTimerState();

        var point = e.GetCurrentPoint(LyricsInteractionOverlay);
        var delta = point.Properties.MouseWheelDelta;
        var value = _canvas.MouseScrollOffset + delta;

        // Clamp scroll range
        if (value > 0)
            value = Math.Min(-_canvas.CurrentCanvasYScroll, value);
        else
            value = Math.Max(
                -_canvas.CurrentCanvasYScroll - _canvas.ActualLyricsHeight,
                value);

        _canvas.MouseScrollOffset = value;

        // Auto-resume after 3s of no scrolling
        _scrollResetTimer ??= CreateScrollResetTimer();
        _scrollResetTimer.Stop();
        _scrollResetTimer.Interval = TimeSpan.FromSeconds(3);
        _scrollResetTimer.Start();

        e.Handled = true;
    }

    // ── Scroll reset (auto-resume after wheel scrolling) ──

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
        if (_canvas == null) return;
        _canvas.MouseScrollOffset = 0;
        _canvas.IsMouseScrolling = false;
        if (LyricsSyncButton != null)
            LyricsSyncButton.Visibility = Visibility.Collapsed;
        UpdateTimerState();
    }

    // ── Layout ──

    private void LyricsContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CanvasLayoutInvalidated?.Invoke(this, EventArgs.Empty);
    }

    // ── Debug ──

    /// <summary>
    /// Raised when the DEBUG-only lyrics debug button is clicked. The parent
    /// owns the dialog (it has the lyrics service and dialog plumbing).
    /// </summary>
    public event EventHandler? DebugRequested;

    private void LyricsDebugButton_Click(object sender, RoutedEventArgs e)
    {
        DebugRequested?.Invoke(this, EventArgs.Empty);
    }
}
