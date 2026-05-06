using System;
using System.ComponentModel;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.UI;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// YouTube-style 2-column page hosting the active video provider's
/// <see cref="MediaPlayer"/> on the left and an "Up next" panel on the right.
/// Source-agnostic — binds via <see cref="IActiveVideoSurfaceService"/> rather
/// than reaching into a concrete engine, so a future Spotify video engine
/// renders here the same way local files do.
/// </summary>
public sealed partial class VideoPlayerPage : Page, IMediaSurfaceConsumer
{
    // Width threshold below which the right "Up next" column collapses so
    // the video can keep its full readable width on narrow windows.
    private const double UpNextCollapseWidthPx = 920.0;
    private const double VideoAspectRatio = 16.0 / 9.0;
    private const double VideoMinHeight = 240.0;

    private readonly IActiveVideoSurfaceService _surface;
    private readonly IPlaybackStateService? _playbackState;
    public VideoPlayerPageViewModel ViewModel { get; }

    private MediaPlayerElement? _element;
    private FrameworkElement? _elementSurface;
    private bool _isStartingVideoOnLoad;
    private string? _videoTakeoverVisibleTrackId;
    private string? _videoTakeoverSuppressedTrackId;

    // Theatre mode — YouTube-style "in-app fullscreen": hides the right
    // "Up next" column and the source-row + metadata card so the video and
    // its title get the entire page width without leaving the app window.
    private bool _isTheatreMode;

    // True while the theatre slide animation is in flight. Read by
    // ApplyResponsiveLayout so a window-resize during the animation doesn't
    // clobber the column width mid-tween. Cleared on the CompositionScopedBatch
    // Completed callback.
    private bool _isTheatreAnimating;

    // Theatre transition tuning. 240 ms / decelerate-cubic matches Win11
    // system motion (the same shape used by NavigationView pane transitions).
    private const double TheatreTransitionMs = 240;
    private const double TheatreExpandedWidth = 360;
    private const double TheatrePanelSlidePx = 42;
    private DispatcherQueueTimer? _theatreAnimationTimer;

    // Auto-hide of the transport overlay when the cursor is idle over the
    // video. Fires after 3s of inactivity (YouTube-ish). Reset on every
    // pointer movement over the frame or the overlay, including movement
    // handled by child buttons/sliders.
    private readonly DispatcherQueueTimer _hideTimer;
    private const int HideAfterMs = 3000;

    private LoadedImageSurface? _artistHeaderSurface;
    private SpriteVisual? _artistHeaderBlurVisual;
    private CancellationTokenSource? _navigationCts;

    public VideoPlayerPage()
    {
        InitializeComponent();
        _surface = Ioc.Default.GetRequiredService<IActiveVideoSurfaceService>();
        _playbackState = Ioc.Default.GetService<IPlaybackStateService>();
        ViewModel = Ioc.Default.GetRequiredService<VideoPlayerPageViewModel>();

        _hideTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(HideAfterMs);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideControlsForIdle();

        VideoFrame.AddHandler(PointerMovedEvent, new PointerEventHandler(VideoFrame_PointerMoved), true);
        ControlsOverlay.AddHandler(PointerMovedEvent, new PointerEventHandler(ControlsOverlay_PointerMoved), true);

        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => ApplyViewModelTheme();
        ApplyViewModelTheme();

        // Lyrics canvas wiring — code-behind ticker pushes VM state into
        // the imperatively-controlled NowPlayingCanvas (same pattern
        // ExpandedPlayerView uses for its FullscreenLyricsCanvas). VM
        // properties drive lyrics + position + play state; the canvas
        // emits SeekRequested back to the player when the user clicks a line.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateArtistHeaderBlur(ViewModel.SpotifyArtistHeaderImageUrl);
        if (ViewModel.Player is not null)
            ViewModel.Player.PropertyChanged += OnPlayerPropertyChanged;
        if (LyricsCanvas is not null)
        {
            LyricsCanvas.LyricsWindowStatus =
                Ioc.Default.GetService<LyricsViewModel>()?.WindowStatus
                ?? new Wavee.Controls.Lyrics.Models.Settings.LyricsWindowStatus();
            LyricsCanvas.SeekRequested += OnLyricsSeekRequested;
            ConfigureLyricsCanvasLayout();
            SyncLyricsCanvasPlaybackState();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoPlayerPageViewModel.SpotifyArtistHeaderImageUrl))
            UpdateArtistHeaderBlur(ViewModel.SpotifyArtistHeaderImageUrl);

        if (LyricsCanvas is null) return;
        switch (e.PropertyName)
        {
            case nameof(VideoPlayerPageViewModel.LyricsData):
                LyricsCanvas.SetLyricsData(ViewModel.LyricsData);
                SyncLyricsCanvasPlaybackState();
                break;
            case nameof(VideoPlayerPageViewModel.IsLyricsVisible):
            case nameof(VideoPlayerPageViewModel.IsAudioMode):
                ConfigureLyricsCanvasLayout();
                SyncLyricsCanvasPlaybackState();
                break;
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (LyricsCanvas is null || ViewModel.Player is null) return;
        switch (e.PropertyName)
        {
            case nameof(PlayerBarViewModel.Position):
                LyricsCanvas.SetPosition(TimeSpan.FromMilliseconds(ViewModel.Player.Position));
                break;
            case nameof(PlayerBarViewModel.IsPlaying):
                SyncLyricsCanvasPlaybackState();
                break;
        }
    }

    private void OnLyricsSeekRequested(object? sender, TimeSpan position)
    {
        ViewModel.Player?.CommitSeekFromBar(position.TotalMilliseconds);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // LoadedImageSurface owns native WIC bytes that bypass ImageCacheService;
        // dispose explicitly so the page tearing down doesn't strand them.
        _artistHeaderSurface?.Dispose();
        _artistHeaderSurface = null;
        if (_artistHeaderBlurVisual != null)
        {
            ElementCompositionPreview.SetElementChildVisual(ArtistHeaderBlurHost, null);
            _artistHeaderBlurVisual.Dispose();
            _artistHeaderBlurVisual = null;
        }
    }

    private void UpdateArtistHeaderBlur(string? imageUrl)
    {
        if (_artistHeaderBlurVisual != null)
        {
            ElementCompositionPreview.SetElementChildVisual(ArtistHeaderBlurHost, null);
            _artistHeaderBlurVisual.Dispose();
            _artistHeaderBlurVisual = null;
        }
        _artistHeaderSurface?.Dispose();
        _artistHeaderSurface = null;

        if (string.IsNullOrEmpty(imageUrl)) return;

        var compositor = ElementCompositionPreview.GetElementVisual(ArtistHeaderBlurHost).Compositor;

        var blurEffect = new GaussianBlurEffect
        {
            BlurAmount = 30f,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("source")
        };
        var effectFactory = compositor.CreateEffectFactory(blurEffect);
        var effectBrush = effectFactory.CreateBrush();

        _artistHeaderSurface = LoadedImageSurface.StartLoadFromUri(new Uri(imageUrl));
        var surfaceBrush = compositor.CreateSurfaceBrush(_artistHeaderSurface);
        surfaceBrush.Stretch = CompositionStretch.UniformToFill;
        effectBrush.SetSourceParameter("source", surfaceBrush);

        _artistHeaderBlurVisual = compositor.CreateSpriteVisual();
        _artistHeaderBlurVisual.Brush = effectBrush;
        _artistHeaderBlurVisual.Size = new Vector2(
            (float)ArtistHeaderBlurHost.ActualWidth,
            (float)ArtistHeaderBlurHost.ActualHeight);

        ElementCompositionPreview.SetElementChildVisual(ArtistHeaderBlurHost, _artistHeaderBlurVisual);
    }

    private void ArtistHeaderBlurHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_artistHeaderBlurVisual != null)
            _artistHeaderBlurVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
    }

    private void LyricsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsLyricsVisible = !ViewModel.IsLyricsVisible;
        var tooltip = ViewModel.IsLyricsVisible ? "Show artwork" : "Show lyrics";
        ToolTipService.SetToolTip(LyricsToggleButton, tooltip);
        ToolTipService.SetToolTip(AudioLyricsToggleButton, tooltip);
        SyncLyricsCanvasPlaybackState();
    }

    private void AudioFrame_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el)
            el.Height = el.ActualWidth > 0 ? el.ActualWidth : 320;
        ConfigureLyricsCanvasLayout();
    }

    private void AudioFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement el && e.NewSize.Width > 0)
            el.Height = e.NewSize.Width;
        ConfigureLyricsCanvasLayout();
    }

    private void ConfigureLyricsCanvasLayout()
    {
        if (LyricsCanvas is null || AudioFrame is null) return;

        var width = AudioFrame.ActualWidth > 0 ? AudioFrame.ActualWidth : LyricsCanvas.ActualWidth;
        var height = AudioFrame.ActualHeight > 0 ? AudioFrame.ActualHeight : width;
        if (width <= 0 || height <= 0) return;

        const double pad = 24;
        LyricsCanvas.LyricsStartX = pad;
        LyricsCanvas.LyricsStartY = pad;
        LyricsCanvas.LyricsWidth = Math.Max(0, width - pad * 2);
        LyricsCanvas.LyricsHeight = Math.Max(0, height - pad * 2);
        LyricsCanvas.LyricsOpacity = ViewModel.IsLyricsVisible ? 1 : 0;
        LyricsCanvas.AlbumArtRect = Rect.Empty;
        LyricsCanvas.SetClearColor(Colors.Transparent);
    }

    private void SyncLyricsCanvasPlaybackState()
    {
        if (LyricsCanvas is null || ViewModel.Player is null) return;

        var active = ViewModel.IsAudioMode && ViewModel.IsLyricsVisible;
        LyricsCanvas.LyricsOpacity = active ? 1 : 0;
        LyricsCanvas.SetRenderingActive(active);
        LyricsCanvas.SetIsPlaying(active && ViewModel.Player.IsPlaying);
        LyricsCanvas.SetPosition(TimeSpan.FromMilliseconds(ViewModel.Player.Position));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Acquire the shared surface — service detaches whoever held it
        // previously (e.g. the mini-player) and binds us. AttachSurface fires
        // synchronously if a provider is already active.
        _surface.ActiveSurfaceChanged += OnActiveSurfaceChanged;
        _surface.SurfaceOwnershipChanged += OnSurfaceOwnershipChanged;
        if (_playbackState is not null)
            _playbackState.PropertyChanged += OnPlaybackStateChanged;
        if (_surface.CurrentOwner is null || _surface.IsOwnedBy(this))
            _surface.AcquireSurface(this);
        ApplyViewModelTheme();
        ApplyResponsiveLayout(ActualWidth);
        UpdateVideoLoadingOverlay();
        _navigationCts = new CancellationTokenSource();
        _ = EnsureVideoPlaybackOnLoadAsync(_navigationCts.Token);

        // Collapse the sidebar player widget when the user is on the dedicated
        // now-playing page — both surfaces show the same playback, so leaving
        // the sidebar widget expanded would render an empty/duplicate video
        // host alongside this page. Don't auto-restore on navigate-from; the
        // user can chevron-expand it manually if they want.
        var shell = Ioc.Default.GetService<ShellViewModel>();
        if (shell is not null && !shell.SidebarPlayerCollapsed)
            shell.SidebarPlayerCollapsed = true;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Release ownership so the mini-player (or whoever's next) can claim
        // the surface. The service handles the DetachSurface call.
        _surface.ActiveSurfaceChanged -= OnActiveSurfaceChanged;
        _surface.SurfaceOwnershipChanged -= OnSurfaceOwnershipChanged;
        if (_playbackState is not null)
            _playbackState.PropertyChanged -= OnPlaybackStateChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (ViewModel.Player is not null)
            ViewModel.Player.PropertyChanged -= OnPlayerPropertyChanged;
        if (LyricsCanvas is not null)
        {
            LyricsCanvas.SeekRequested -= OnLyricsSeekRequested;
            LyricsCanvas.SetRenderingActive(false);
        }
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
        _surface.ReleaseSurface(this);
        StopTheatreAnimation();
        _hideTimer.Stop();
        UpdateArtistHeaderBlur(null);
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling does not pin this page across navigations. NavCacheMode is
        // Disabled so the page is destroyed on nav-away — no Update() partner.
        Bindings?.StopTracking();
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentTrackIsVideo))
        {
            DispatcherQueue.TryEnqueue(UpdateVideoLoadingOverlay);
        }
    }

    private void OnActiveSurfaceChanged(object? sender, MediaPlayer? surface)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnActiveSurfaceChanged(sender, surface));
            return;
        }

        // The page sticks once it's open: when the surface releases because the
        // next track is audio-only, fall back to the audio-mode UI
        // (ShouldUseAudioMode → album-art + lyrics) instead of navigating away.
        // Auto-close still fires from the initial-load failure paths
        // (lines 586, 623) for the case where the page was opened explicitly
        // for a video that never resolved.
        UpdateVideoLoadingOverlay();
    }

    private void OnSurfaceOwnershipChanged(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnSurfaceOwnershipChanged(sender, e));
            return;
        }

        if (_surface.CurrentOwner is null && _surface.HasActiveSurface && Frame?.Content == this)
        {
            _surface.AcquireSurface(this);
            return;
        }

        UpdateVideoLoadingOverlay();
    }

    private void CloseIfNoActiveVideo()
    {
        if (_surface.HasActiveSurface) return;
        if (_isStartingVideoOnLoad || ViewModel.Player?.IsResolvingVideo == true) return;
        if (ViewModel.Player?.IsSwitchingToAudio == true) return;
        if (IsAudioFallbackActive()) return;
        if (Frame?.Content != this) return;

        _surface.ReleaseSurface(this);
        if (Frame.CanGoBack)
            Frame.GoBack();
        else
            NavigationHelpers.OpenHome();
    }

    // ── IMediaSurfaceConsumer ─────────────────────────────────────────────

    public void AttachSurface(MediaPlayer player)
    {
        DetachElementSurface();
        UpdateVideoLoadingOverlay();
        if (_element is null)
        {
            _element = new MediaPlayerElement
            {
                // Built-in transport controls disabled — the app's bottom
                // PlayerBar already drives play/pause/seek/volume through
                // the orchestrator, and showing both would let the user
                // diverge between the two control surfaces.
                AreTransportControlsEnabled = false,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            VideoHost.Children.Add(_element);
        }
        _element.SetMediaPlayer(player);
        UpdateVideoLoadingOverlay();
    }

    public void AttachElementSurface(FrameworkElement element)
    {
        DetachMediaPlayerSurface();
        if (_elementSurface is not null && ReferenceEquals(_elementSurface, element))
            return;

        _elementSurface = element;
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        element.VerticalAlignment = VerticalAlignment.Stretch;
        VideoHost.Children.Add(element);
        UpdateVideoLoadingOverlay();
    }

    public void DetachSurface()
    {
        DetachMediaPlayerSurface();
        DetachElementSurface();
        UpdateVideoLoadingOverlay();
    }

    private void DetachMediaPlayerSurface()
    {
        if (_element is null) return;
        _element.SetMediaPlayer(null);
        VideoHost.Children.Remove(_element);
        _element = null;
    }

    private void DetachElementSurface()
    {
        if (_elementSurface is null) return;
        VideoHost.Children.Remove(_elementSurface);
        _elementSurface = null;
    }

    private void VideoFrame_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateVideoFrameSize();
        UpdateVideoLoadingOverlay();
    }

    private void VideoFrame_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateVideoFrameSize();

    private void UpdateVideoFrameSize()
    {
        if (VideoFrame is null) return;
        var width = VideoFrame.ActualWidth;
        if (width <= 0) return;

        var targetHeight = Math.Max(VideoMinHeight, width / VideoAspectRatio);
        if (double.IsNaN(VideoFrame.Height) || Math.Abs(VideoFrame.Height - targetHeight) > 0.5)
            VideoFrame.Height = targetHeight;
    }

    private void UpdateVideoLoadingOverlay()
    {
        if (VideoLoadingOverlay is null) return;
        UpdateVideoTakeoverSeenState();
        var takeoverConflict = IsVideoTakeoverConflictActive();
        var showTakeoverOverlay = takeoverConflict && !IsVideoTakeoverSuppressedForCurrentTrack();
        if (showTakeoverOverlay)
            _videoTakeoverVisibleTrackId = GetVideoTakeoverTrackId();
        var isWaitingForFirstFrame = _isStartingVideoOnLoad
            || (_surface.HasActiveSurface && !_surface.HasActiveFirstFrame);
        var isBuffering = _surface.HasActiveSurface
            && _surface.HasActiveFirstFrame
            && _surface.IsActiveSurfaceBuffering;

        VideoTakeoverOverlay.Visibility = showTakeoverOverlay
            ? Visibility.Visible
            : Visibility.Collapsed;
        ControlsOverlay.Visibility = takeoverConflict
            ? Visibility.Collapsed
            : Visibility.Visible;
        VideoLoadingText.Text = isBuffering ? "Buffering..." : "Loading video...";
        VideoLoadingOverlay.Visibility = !takeoverConflict
            && (isWaitingForFirstFrame || isBuffering)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool HasAttachedVideoSurface => _element is not null || _elementSurface is not null;

    private bool IsAudioFallbackActive()
    {
        if (_playbackState is null || _playbackState.CurrentTrackIsVideo)
            return false;
        if (string.Equals(_surface.ActiveKind, "local", StringComparison.Ordinal))
            return false;

        var trackId = _playbackState.CurrentOriginalTrackId ?? _playbackState.CurrentTrackId;
        if (string.IsNullOrWhiteSpace(trackId))
            return false;

        return trackId.StartsWith("spotify:track:", StringComparison.Ordinal)
               || !trackId.Contains(':');
    }

    private bool IsVideoTakeoverConflictActive()
    {
        if (!_surface.HasActiveSurface || _isStartingVideoOnLoad)
            return false;

        return !_surface.IsOwnedBy(this) || !HasAttachedVideoSurface;
    }

    private void VideoTakeoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_surface.HasActiveSurface)
            return;

        SuppressVideoTakeoverForCurrentTrack();
        _surface.AcquireSurface(this);
        UpdateVideoLoadingOverlay();
    }

    private void VideoTakeoverDismissButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressVideoTakeoverForCurrentTrack();
        UpdateVideoLoadingOverlay();
    }

    private string? GetVideoTakeoverTrackId()
        => _playbackState?.CurrentTrackId;

    private bool IsVideoTakeoverSuppressedForCurrentTrack()
    {
        var trackId = GetVideoTakeoverTrackId();
        return !string.IsNullOrEmpty(trackId)
               && string.Equals(_videoTakeoverSuppressedTrackId, trackId, StringComparison.Ordinal);
    }

    private void SuppressVideoTakeoverForCurrentTrack()
    {
        var trackId = GetVideoTakeoverTrackId();
        if (string.IsNullOrEmpty(trackId))
            return;

        _videoTakeoverSuppressedTrackId = trackId;
        if (string.Equals(_videoTakeoverVisibleTrackId, trackId, StringComparison.Ordinal))
            _videoTakeoverVisibleTrackId = null;
    }

    private void UpdateVideoTakeoverSeenState()
    {
        if (IsVideoTakeoverConflictActive())
        {
            var currentTrackId = GetVideoTakeoverTrackId();
            if (!string.IsNullOrEmpty(_videoTakeoverVisibleTrackId)
                && !string.Equals(_videoTakeoverVisibleTrackId, currentTrackId, StringComparison.Ordinal))
            {
                _videoTakeoverSuppressedTrackId = _videoTakeoverVisibleTrackId;
                _videoTakeoverVisibleTrackId = null;
            }

            return;
        }

        if (string.IsNullOrEmpty(_videoTakeoverVisibleTrackId))
            return;

        _videoTakeoverSuppressedTrackId = _videoTakeoverVisibleTrackId;
        _videoTakeoverVisibleTrackId = null;
    }

    private async Task EnsureVideoPlaybackOnLoadAsync(CancellationToken ct = default)
    {
        if (_surface.HasActiveSurface)
            return;

        if (IsAudioFallbackActive() && ViewModel.Player?.IsCurrentTrackVideoCapable != true)
            return;

        if (ViewModel.Player?.IsResolvingVideo == true)
        {
            _isStartingVideoOnLoad = true;
            UpdateVideoLoadingOverlay();

            for (var attempt = 0; attempt < 300
                                  && ViewModel.Player?.IsResolvingVideo == true
                                  && !_surface.HasActiveSurface
                                  && !ct.IsCancellationRequested; attempt++)
            {
                await Task.Delay(50);
            }

            if (ct.IsCancellationRequested) return;
            CompleteVideoPlaybackOnLoad(_surface.HasActiveSurface);
            return;
        }

        var playbackState = Ioc.Default.GetService<IPlaybackStateService>();
        if (playbackState is null)
        {
            CloseIfNoActiveVideo();
            return;
        }

        _isStartingVideoOnLoad = true;
        UpdateVideoLoadingOverlay();

        bool switched;
        try
        {
            switched = await playbackState.SwitchToVideoAsync();
        }
        catch
        {
            switched = false;
        }

        if (ct.IsCancellationRequested) return;

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => CompleteVideoPlaybackOnLoad(switched));
            return;
        }

        CompleteVideoPlaybackOnLoad(switched);
    }

    private void CompleteVideoPlaybackOnLoad(bool switched)
    {
        _isStartingVideoOnLoad = false;
        UpdateVideoLoadingOverlay();

        if (switched && ViewModel.Player is { } player)
            player.PreferVideoPlaybackInSession = true;

        if (!switched && !_surface.HasActiveSurface)
            CloseIfNoActiveVideo();
    }

    private void ApplyViewModelTheme()
        => ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);

    // ── Transport overlay (YouTube-style) ────────────────────────────────

    // Pause the VM's animation source so position updates from the audio
    // host don't fight the user's drag.
    private void VideoScrubBar_SeekStarted(object sender, EventArgs e)
        => ViewModel.Player?.StartSeeking();

    // Commit the new position via the same VM path the bottom PlayerBar uses;
    // re-anchors and unblocks the animation timer.
    private void VideoScrubBar_SeekCommitted(object sender, double positionMs)
        => ViewModel.Player?.CommitSeekFromBar(positionMs);

    // Show the controls + reset the auto-hide timer on every cursor move
    // over the video frame. The AddHandler(..., handledEventsToo: true)
    // registration in the constructor catches moves over child controls too.
    //
    // Also ramps the AttachedCardShadow blur on hover so the video frame
    // visibly lifts off the gradient backdrop when the cursor is over it —
    // a small WinUI motion polish; resets on PointerExited below.
    private void VideoFrame_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowControlsAndRestartIdleTimer();
        SetVideoFrameShadowHover(true);
    }

    private void VideoFrame_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Cursor left the video frame entirely — hide immediately.
        _hideTimer.Stop();
        HideControlsForIdle();
        SetVideoFrameShadowHover(false);
    }

    // 44 px blur at rest, 56 px on hover. The shadow is declared with
    // x:Name="VideoFrameShadow" on the VideoFrame's AttachedCardShadow effect.
    private void SetVideoFrameShadowHover(bool hover)
    {
        if (VideoFrameShadow is null) return;
        VideoFrameShadow.BlurRadius = hover ? 56.0 : 44.0;
    }

    // YouTube-style overlay behavior: pointer movement is the signal. Even
    // when the cursor is already inside the controls, movement should reveal
    // them again and restart the idle timer; lack of movement hides them.
    private void ControlsOverlay_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ShowControlsAndRestartIdleTimer();
    }

    private void ControlsOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowControlsAndRestartIdleTimer();
    }

    private void ControlsOverlay_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ShowControlsAndRestartIdleTimer();
    }

    private void ShowControlsAndRestartIdleTimer()
    {
        ShowControls();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void ShowControls()
    {
        if (ControlsOverlay is null) return;
        ControlsOverlay.Visibility = Visibility.Visible;
        ControlsOverlay.IsHitTestVisible = true;
        ControlsOverlay.Opacity = 1.0;
    }

    private void HideControlsForIdle()
    {
        if (ControlsOverlay is null) return;
        ControlsOverlay.Opacity = 0.0;
        ControlsOverlay.IsHitTestVisible = false;
    }

    // Toggle borderless fullscreen on the host window via AppWindow's
    // FullScreen presenter. The MediaPlayerElement and overlay controls
    // already fill VideoHost so they reflow into the new viewport without
    // additional layout work; the surrounding shell chrome (sidebar, tab
    // bar, bottom PlayerBar) stays rendered — a future pass can hide it
    // for an immersive YouTube-style experience.
    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        var appWindow = MainWindow.Instance?.AppWindow;
        if (appWindow is null) return;

        var enteringFullScreen = appWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
        appWindow.SetPresenter(enteringFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);
    }

    private void VideoQualityButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement target)
            Wavee.UI.WinUI.Helpers.Playback.SpotifyVideoQualityFlyout.ShowAt(target);
    }

    // Theatre mode: hide the right "Up next" column so the video fills the
    // full page width. Source-row and metadata card stay visible so there's
    // no wasted empty space below the title. The transition is animated:
    // see AnimateTheatreTransition for the slide+fade choreography.
    private void TheatreModeButton_Click(object sender, RoutedEventArgs e)
    {
        _isTheatreMode = !_isTheatreMode;
        AnimateTheatreColumnTransition(entering: _isTheatreMode);
        ToolTipService.SetToolTip(TheatreModeButton,
            _isTheatreMode ? "Exit theatre mode" : "Theatre mode");
    }

    // Slide+fade the up-next panel using Composition (GPU-accelerated, same
    // pattern CompositionProgressBar uses elsewhere in the project — no new
    // animation abstraction). The Grid column itself isn't animated because
    // GridLength isn't WinUI-animatable; instead we slide the panel's visual
    // off-stage and snap the column width at the right moment in the timeline:
    //
    //   entering theatre:  slide-out → on Completed, set column width to 0
    //   exiting theatre:   set column width to 360 → slide-in from off-stage
    //
    // The video frame's 16:9 SizeChanged binding handles its own resize on
    // the column-width change, so the user perceives a single fluid motion:
    // panel glides out, video glides bigger.
    private void AnimateTheatreTransition(bool entering)
    {
        if (UpNextPanel is null) return;

        // Short-circuit when the panel was already collapsed by a narrow
        // viewport — there's nothing to animate, just toggle the layout flag.
        var narrow = ActualWidth < UpNextCollapseWidthPx;
        if (narrow)
        {
            ApplyResponsiveLayout(ActualWidth);
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(UpNextPanel);
        var compositor = visual.Compositor;

        // Slide distance — panel width plus the column gap so it visually
        // clears the right edge before the column collapses. Falls back to a
        // sensible default if ActualWidth hasn't materialised yet.
        var slide = (float)(UpNextPanel.ActualWidth > 0 ? UpNextPanel.ActualWidth + 24 : 384);

        var translation = compositor.CreateScalarKeyFrameAnimation();
        translation.Duration = TimeSpan.FromMilliseconds(TheatreTransitionMs);
        translation.Target = "Translation.X";
        var ease = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0.0f),
            new Vector2(0.0f, 1.0f));

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = TimeSpan.FromMilliseconds(TheatreTransitionMs);

        if (entering)
        {
            translation.InsertKeyFrame(0f, 0f, ease);
            translation.InsertKeyFrame(1f, slide, ease);
            opacity.InsertKeyFrame(0f, 1f, ease);
            opacity.InsertKeyFrame(1f, 0f, ease);
        }
        else
        {
            // Expand the column slot first so the panel has a real layout
            // home to slide back into; the visual is offset off-stage at
            // animation start so the user sees it travel back in.
            ApplyResponsiveLayout(ActualWidth);
            translation.InsertKeyFrame(0f, slide, ease);
            translation.InsertKeyFrame(1f, 0f, ease);
            opacity.InsertKeyFrame(0f, 0f, ease);
            opacity.InsertKeyFrame(1f, 1f, ease);
        }

        // Enable Translation as an animatable property on this visual (per
        // ElementCompositionPreview's contract — must be set before the
        // first Translation animation starts).
        ElementCompositionPreview.SetIsTranslationEnabled(UpNextPanel, true);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _isTheatreAnimating = true;
        visual.StartAnimation("Translation.X", translation);
        visual.StartAnimation("Opacity", opacity);
        batch.End();

        batch.Completed += (_, _) =>
        {
            _isTheatreAnimating = false;
            if (entering)
            {
                // Now collapse the column slot — the panel has visually exited.
                ApplyResponsiveLayout(ActualWidth);
            }
        };
    }

    // ── Right-panel click handlers ────────────────────────────────────────

    // Timer-driven theatre transition: animate the actual right-column width
    // so the video frame resizes during the motion instead of snapping after it.
    private void AnimateTheatreColumnTransition(bool entering)
    {
        if (UpNextPanel is null) return;
        StopTheatreAnimation();

        var narrow = ActualWidth < UpNextCollapseWidthPx;
        if (narrow)
        {
            ApplyResponsiveLayout(ActualWidth);
            return;
        }

        _isTheatreAnimating = true;
        UpNextPanel.Visibility = Visibility.Visible;

        var fromWidth = Math.Max(0, UpNextColumn.ActualWidth);
        if (fromWidth <= 0.5)
            fromWidth = entering ? TheatreExpandedWidth : 0;
        var toWidth = entering ? 0 : TheatreExpandedWidth;
        var fromOpacity = UpNextPanel.Opacity;
        var toOpacity = entering ? 0 : 1;
        var fromTranslateX = UpNextPanel.Translation.X;
        var toTranslateX = entering ? TheatrePanelSlidePx : 0;
        var started = DateTime.UtcNow;

        _theatreAnimationTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _theatreAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
        _theatreAnimationTimer.IsRepeating = true;
        _theatreAnimationTimer.Tick += OnTheatreAnimationTick;
        _theatreAnimationTimer.Start();

        void OnTheatreAnimationTick(DispatcherQueueTimer sender, object args)
        {
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            var t = Math.Clamp(elapsed / TheatreTransitionMs, 0, 1);
            var eased = EaseOutCubic(t);

            UpNextColumn.Width = new GridLength(Math.Max(0, Lerp(fromWidth, toWidth, eased)));
            UpNextPanel.Opacity = Lerp(fromOpacity, toOpacity, eased);
            UpNextPanel.Translation = new Vector3((float)Lerp(fromTranslateX, toTranslateX, eased), 0, 0);
            UpdateVideoFrameSize();

            if (t < 1) return;

            sender.Stop();
            sender.Tick -= OnTheatreAnimationTick;
            if (ReferenceEquals(_theatreAnimationTimer, sender))
                _theatreAnimationTimer = null;

            _isTheatreAnimating = false;
            UpNextColumn.Width = new GridLength(toWidth);
            UpNextPanel.Opacity = toOpacity;
            UpNextPanel.Translation = new Vector3((float)toTranslateX, 0, 0);
            UpNextPanel.Visibility = entering ? Visibility.Collapsed : Visibility.Visible;
            UpdateVideoFrameSize();
        }
    }

    private void StopTheatreAnimation()
    {
        if (_theatreAnimationTimer is null) return;
        _theatreAnimationTimer.Stop();
        _theatreAnimationTimer = null;
        _isTheatreAnimating = false;
    }

    private void SetUpNextPanelRestState(bool visible)
    {
        if (UpNextPanel is null) return;
        UpNextPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpNextPanel.Opacity = visible ? 1 : 0;
        UpNextPanel.Translation = new Vector3(visible ? 0 : (float)TheatrePanelSlidePx, 0, 0);
    }

    private static double EaseOutCubic(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var inv = 1 - t;
        return 1 - inv * inv * inv;
    }

    private static double Lerp(double from, double to, double t)
        => from + (to - from) * t;

    // Click handler for rows in the "Recommended" chip-tab (SEO recommender
    // results). Plays the track via the playback engine.
    private void WatchNextCard_Click(object sender, RoutedEventArgs e)
        => PlayTrackFromTag(sender, "video-watch-next", "Up next");

    // Click handler for rows in the "Music videos" chip-tab (npv RelatedVideos).
    // Same dispatch path; different telemetry strings so we can tell them
    // apart in logs.
    private void RelatedVideoCard_Click(object sender, RoutedEventArgs e)
        => PlayTrackFromTag(sender, "video-related", "Music videos");

    private static void PlayTrackFromTag(object sender, string ident, string description)
    {
        if (sender is not Button { Tag: string uri } || string.IsNullOrEmpty(uri))
            return;

        var engine = Ioc.Default.GetService<IPlaybackEngine>();
        if (engine is null) return;

        _ = engine.PlayAsync(new PlayCommand
        {
            Endpoint = "play",
            Key = $"{ident}/0",
            MessageId = 0,
            MessageIdent = ident,
            SenderDeviceId = "",
            ContextUri = uri,
            TrackUri = uri,
            ContextDescription = description,
        });
    }

    // ── Bio Read more / Read less ─────────────────────────────────────────

    // Toggles the artist bio between 3-line clamp and unlimited. The
    // HyperlinkButton sits inside the artist sub-card Button; WinUI's Button
    // click pipeline doesn't bubble to a parent Button, so no Handled flag
    // is needed (Click is a plain RoutedEventArgs, no Handled property).
    private void BioReadMore_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SpotifyArtistBioExpanded = !ViewModel.SpotifyArtistBioExpanded;
    }

    // ── Responsive layout ─────────────────────────────────────────────────

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Don't reflow mid-theatre-animation — the column-width snap from
        // here would clobber the in-flight slide. The animation's own
        // Completed callback restores the layout when it lands.
        if (_isTheatreAnimating) return;
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        // Theatre mode forces the right column closed regardless of viewport
        // width — the user explicitly opted into a video-first layout.
        if (_isTheatreMode)
        {
            UpNextColumn.Width = new GridLength(0);
            SetUpNextPanelRestState(visible: false);
            DispatcherQueue.TryEnqueue(UpdateVideoFrameSize);
            return;
        }
        // Collapse the up-next column on narrow viewports — the video
        // alone is more useful than a cramped two-column split.
        var showPanel = width >= UpNextCollapseWidthPx;
        UpNextColumn.Width = showPanel
            ? new GridLength(TheatreExpandedWidth)
            : new GridLength(0);
        SetUpNextPanelRestState(showPanel);
        DispatcherQueue.TryEnqueue(UpdateVideoFrameSize);
    }
}
