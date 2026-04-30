using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.Media.Playback;

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

    // Auto-hide of the transport overlay when the cursor is idle over the
    // video. Fires after 3s of inactivity (YouTube-ish). Reset on every
    // PointerMoved over the video frame and on every state change to paused
    // (we keep controls visible while paused, like YouTube does).
    private readonly DispatcherQueueTimer _hideTimer;
    private const int HideAfterMs = 3000;

    public VideoPlayerPage()
    {
        InitializeComponent();
        _surface = Ioc.Default.GetRequiredService<IActiveVideoSurfaceService>();
        _playbackState = Ioc.Default.GetService<IPlaybackStateService>();
        ViewModel = Ioc.Default.GetRequiredService<VideoPlayerPageViewModel>();

        _hideTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(HideAfterMs);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideControlsIfPlaying();

        SizeChanged += OnSizeChanged;
        ActualThemeChanged += (_, _) => ApplyViewModelTheme();
        ApplyViewModelTheme();
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
        _ = EnsureVideoPlaybackOnLoadAsync();

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
        _surface.ReleaseSurface(this);
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

        UpdateVideoLoadingOverlay();
        if (_surface.HasActiveSurface) return;
        CloseIfNoActiveVideo();
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

    private async Task EnsureVideoPlaybackOnLoadAsync()
    {
        if (_surface.HasActiveSurface)
            return;

        if (ViewModel.Player?.IsResolvingVideo == true)
        {
            _isStartingVideoOnLoad = true;
            UpdateVideoLoadingOverlay();

            for (var attempt = 0; attempt < 300
                                  && ViewModel.Player?.IsResolvingVideo == true
                                  && !_surface.HasActiveSurface; attempt++)
            {
                await Task.Delay(50);
            }

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
    // over the video frame. While playing, the timer expires after 3s and
    // fades the overlay; while paused we keep controls up (HideControlsIfPlaying
    // checks IsPlaying before hiding).
    private void VideoFrame_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowControls();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void VideoFrame_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Cursor left the video frame entirely — hide immediately if playing.
        _hideTimer.Stop();
        HideControlsIfPlaying();
    }

    // Stop the auto-hide timer while the cursor is parked over the controls
    // (idle pointer over a button shouldn't hide them mid-interaction). On
    // exit, restart the timer so the regular 3s hide behavior resumes.
    private void ControlsOverlay_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hideTimer.Stop();
        ShowControls();
    }

    private void ControlsOverlay_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void ShowControls()
    {
        if (ControlsOverlay is null) return;
        ControlsOverlay.Visibility = Visibility.Visible;
        ControlsOverlay.Opacity = 1.0;
    }

    private void HideControlsIfPlaying()
    {
        if (ControlsOverlay is null) return;
        // Don't hide while paused — matches YouTube's behavior so users can
        // see the controls while looking at a frozen frame.
        if (ViewModel.Player?.IsPlaying != true) return;
        ControlsOverlay.Opacity = 0.0;
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
    // no wasted empty space below the title.
    private void TheatreModeButton_Click(object sender, RoutedEventArgs e)
    {
        _isTheatreMode = !_isTheatreMode;
        ApplyResponsiveLayout(ActualWidth);
        ToolTipService.SetToolTip(TheatreModeButton,
            _isTheatreMode ? "Exit theatre mode" : "Theatre mode");
    }

    // ── Up Next click ─────────────────────────────────────────────────────

    private void UpNextRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
            ViewModel.PlayUpNextCommand.Execute(uri);
    }

    // ── Responsive layout ─────────────────────────────────────────────────

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        // Theatre mode forces the right column closed regardless of viewport
        // width — the user explicitly opted into a video-first layout.
        if (_isTheatreMode)
        {
            UpNextColumn.Width = new GridLength(0);
            DispatcherQueue.TryEnqueue(UpdateVideoFrameSize);
            return;
        }
        // Collapse the up-next column on narrow viewports — the video
        // alone is more useful than a cramped two-column split.
        UpNextColumn.Width = width >= UpNextCollapseWidthPx
            ? new GridLength(360)
            : new GridLength(0);
        DispatcherQueue.TryEnqueue(UpdateVideoFrameSize);
    }
}
