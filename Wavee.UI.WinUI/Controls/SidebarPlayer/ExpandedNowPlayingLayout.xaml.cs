using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Controls.SidebarPlayer;

/// <summary>
/// Apple-Music-style "now playing" left column for the floating player's
/// expanded layout. Reuses the singleton <see cref="PlayerBarViewModel"/> as
/// the data source and the small primitive controls (<c>HeartButton</c>,
/// <c>OutputDevicePicker</c>, <c>CompositionProgressBar</c>,
/// <c>PlaybackActionContent</c>) — no parallel transport implementation.
/// </summary>
public sealed partial class ExpandedNowPlayingLayout : UserControl, IMediaSurfaceConsumer
{
    private const double AudioArtworkMaxSize = 540d;
    private const double VideoAspectRatio = 16d / 9d;
    private const double PreferredVideoWindowWidth = 960d;
    private const double MinVideoWindowWidth = 720d;
    private const double MaxVideoWindowWidth = 1280d;
    private const double MinVideoChromeHeight = 210d;
    private const int VideoOverlayIdleHideMs = 2500;
    private const int VideoOverlayFadeMs = 180;

    public PlayerBarViewModel ViewModel { get; }
    private readonly IActiveVideoSurfaceService _videoSurface;
    private readonly MiniVideoPlayerViewModel? _miniVideoViewModel;
    private readonly ITrackLikeService? _likeService;
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly IMusicVideoMetadataService? _musicVideoMetadata;
    private readonly ILogger<ExpandedNowPlayingLayout>? _logger;
    private MediaPlayerElement? _videoElement;
    private FrameworkElement? _videoElementSurface;
    private bool _isVideoSurfaceEnabled;
    private bool _canTakeVideoSurfaceFromVideoPage;
    private bool _isVideoPresentationMode;
    private bool _isTheaterMode;
    private bool _ownsVideoSurface;
    private bool _videoOverlayVisible = true;
    private bool _eventsSubscribed;
    private int _heartStateVersion;
    private string? _videoTakeoverVisibleTrackId;
    private string? _videoTakeoverSuppressedTrackId;
    private DispatcherQueueTimer? _videoOverlayHideTimer;

    public event EventHandler<bool>? TheaterModeChanged;
    public event EventHandler? FitVideoWindowRequested;
    public event EventHandler? QueueRequested;

    public ExpandedNowPlayingLayout()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        _videoSurface = Ioc.Default.GetRequiredService<IActiveVideoSurfaceService>();
        _miniVideoViewModel = Ioc.Default.GetService<MiniVideoPlayerViewModel>();
        _likeService = Ioc.Default.GetService<ITrackLikeService>();
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        _musicVideoMetadata = Ioc.Default.GetService<IMusicVideoMetadataService>();
        _logger = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger<ExpandedNowPlayingLayout>();

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MediaFrame.AddHandler(PointerMovedEvent, new PointerEventHandler(MediaFrame_PointerMoved), true);

        var heartCommand = new RelayCommand(OnHeartClicked);
        HeartButton.Command = heartCommand;
        OverlayHeartButton.Command = heartCommand;

        UpdateHeartState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureVideoOverlayHideTimer();
        SubscribeEvents();
        ViewModel.SetSurfaceVisible("widget", true);
        UpdateVideoSurfaceOwnership();
        UpdateHeartState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SetSurfaceVisible("widget", false);
        ReleaseVideoSurfaceOwnership();
        UnsubscribeEvents();
        if (_videoOverlayHideTimer is not null)
        {
            _videoOverlayHideTimer.Stop();
            _videoOverlayHideTimer.Tick -= OnVideoOverlayHideTimerTick;
            _videoOverlayHideTimer = null;
        }
    }

    private void SubscribeEvents()
    {
        if (_eventsSubscribed)
            return;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _videoSurface.ActiveSurfaceChanged += OnActiveVideoSurfaceChanged;
        _videoSurface.SurfaceOwnershipChanged += OnVideoSurfaceOwnershipChanged;
        if (_miniVideoViewModel is not null)
            _miniVideoViewModel.PropertyChanged += OnMiniVideoViewModelPropertyChanged;
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        if (_playbackStateService != null)
            _playbackStateService.PropertyChanged += OnPlaybackSaveTargetChanged;

        _eventsSubscribed = true;
    }

    private void UnsubscribeEvents()
    {
        if (!_eventsSubscribed)
            return;

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _videoSurface.ActiveSurfaceChanged -= OnActiveVideoSurfaceChanged;
        _videoSurface.SurfaceOwnershipChanged -= OnVideoSurfaceOwnershipChanged;
        if (_miniVideoViewModel is not null)
            _miniVideoViewModel.PropertyChanged -= OnMiniVideoViewModelPropertyChanged;
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;
        if (_playbackStateService != null)
            _playbackStateService.PropertyChanged -= OnPlaybackSaveTargetChanged;

        _eventsSubscribed = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerBarViewModel.HasTrack) or nameof(PlayerBarViewModel.TrackTitle))
            UpdateHeartState();
    }

    private void TrackTitle_Click(object sender, RoutedEventArgs e)
    {
        NavigateToAlbum();
    }

    private void NavigateToAlbum()
    {
        var albumId = ViewModel.CurrentAlbumId;
        if (string.IsNullOrEmpty(albumId)) return;

        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = albumId,
            Title = ViewModel.TrackTitle ?? "Album",
            ImageUrl = ViewModel.AlbumArt
        };
        NavigationHelpers.OpenAlbum(param, param.Title);
    }

    // ── Progress seek ──────────────────────────────────────────────────────

    private void ProgressBar_SeekStarted(object sender, System.EventArgs e)
        => ViewModel.StartSeeking();

    private void ProgressBar_SeekCommitted(object sender, double positionMs)
        => ViewModel.CommitSeekFromBar(positionMs);

    // ── Heart state (mirrors SidebarPlayerWidget pattern) ─────────────────

    public void SetVideoSurfaceEnabled(bool enabled)
    {
        if (_isVideoSurfaceEnabled == enabled)
            return;

        _isVideoSurfaceEnabled = enabled;
        UpdateVideoSurfaceOwnership();
    }

    public void SetCanTakeVideoSurfaceFromVideoPage(bool enabled)
    {
        if (_canTakeVideoSurfaceFromVideoPage == enabled)
            return;

        _canTakeVideoSurfaceFromVideoPage = enabled;
        UpdateVideoSurfaceOwnership();
    }

    public void SetVideoPresentationMode(bool enabled)
    {
        if (_isVideoPresentationMode == enabled)
            return;

        _isVideoPresentationMode = enabled;
        ApplyVideoPresentationMode();
    }

    public void SetTheaterMode(bool enabled)
    {
        if (_isTheaterMode == enabled)
            return;

        _isTheaterMode = enabled;
        ApplyVideoPresentationMode();
        TheaterModeChanged?.Invoke(this, _isTheaterMode);
    }

    public Windows.Foundation.Size GetPreferredVideoWindowSize(double currentWindowWidth)
    {
        var targetWidth = currentWindowWidth > 0 ? currentWindowWidth : PreferredVideoWindowWidth;
        targetWidth = Math.Clamp(targetWidth, MinVideoWindowWidth, MaxVideoWindowWidth);

        var measuredChromeHeight = _isTheaterMode
            ? 0
            : ActualHeight > 0 && MediaStage != null
            ? ActualHeight - MediaStage.ActualHeight
            : MinVideoChromeHeight;
        var chromeHeight = _isTheaterMode ? 0 : Math.Max(MinVideoChromeHeight, measuredChromeHeight);
        var targetHeight = targetWidth / VideoAspectRatio + chromeHeight;

        return new Windows.Foundation.Size(Math.Round(targetWidth), Math.Round(targetHeight));
    }

    private void OnActiveVideoSurfaceChanged(object? sender, MediaPlayer? surface)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            UpdateVideoSurfaceOwnership();
            ApplyVideoSurfaceVisibility();
        });

    private void OnVideoSurfaceOwnershipChanged(object? sender, EventArgs e)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            UpdateVideoSurfaceOwnership();
            ApplyVideoSurfaceVisibility();
        });

    private void OnMiniVideoViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MiniVideoPlayerViewModel.IsOnVideoPage))
            UpdateVideoSurfaceOwnership();
    }

    private bool ShouldHostVideoSurface =>
        IsLoaded
        && _isVideoSurfaceEnabled
        && _videoSurface.HasActiveSurface
        && (_canTakeVideoSurfaceFromVideoPage || _miniVideoViewModel?.IsOnVideoPage != true);

    private bool CanAutoAcquireVideoSurface =>
        ShouldHostVideoSurface
        && (_canTakeVideoSurfaceFromVideoPage || _videoSurface.CurrentOwner is null || _videoSurface.IsOwnedBy(this));

    private bool IsVideoTakeoverConflictActive =>
        IsLoaded
        && _isVideoSurfaceEnabled
        && _isVideoPresentationMode
        && _videoSurface.HasActiveSurface
        && !_videoSurface.IsOwnedBy(this);

    private bool ShouldShowVideoTakeover =>
        IsVideoTakeoverConflictActive
        && !IsVideoTakeoverSuppressedForCurrentTrack();

    private void UpdateVideoSurfaceOwnership()
    {
        if (CanAutoAcquireVideoSurface)
        {
            _miniVideoViewModel?.SetSuppressedBySidebarPlayer(true);
            _videoSurface.AcquireSurface(this);
            _ownsVideoSurface = true;
            return;
        }

        ReleaseVideoSurfaceOwnership();
    }

    private void ReleaseVideoSurfaceOwnership()
    {
        var hadOwnership = _ownsVideoSurface;
        if (_ownsVideoSurface)
        {
            _videoSurface.ReleaseSurface(this);
            _ownsVideoSurface = false;
        }

        if (hadOwnership)
            _miniVideoViewModel?.SetSuppressedBySidebarPlayer(false);
        ApplyVideoSurfaceVisibility();
    }

    public void AttachSurface(MediaPlayer player)
    {
        DetachElementSurface();
        if (_videoElement is null)
        {
            _videoElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            VideoHost.Child = _videoElement;
        }

        _videoElement.SetMediaPlayer(player);
        ApplyVideoSurfaceVisibility();
    }

    public void AttachElementSurface(FrameworkElement element)
    {
        DetachMediaPlayerSurface();
        if (_videoElementSurface is not null && ReferenceEquals(_videoElementSurface, element))
            return;

        _videoElementSurface = element;
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        element.VerticalAlignment = VerticalAlignment.Stretch;
        element.IsHitTestVisible = false;
        VideoHost.Child = element;
        ApplyVideoSurfaceVisibility();
    }

    public void DetachSurface()
    {
        DetachMediaPlayerSurface();
        DetachElementSurface();
        ApplyVideoSurfaceVisibility();
    }

    private void DetachMediaPlayerSurface()
    {
        if (_videoElement is null) return;
        _videoElement.SetMediaPlayer(null);
        if (ReferenceEquals(VideoHost.Child, _videoElement))
            VideoHost.Child = null;
        _videoElement = null;
    }

    private void DetachElementSurface()
    {
        if (_videoElementSurface is null) return;
        if (ReferenceEquals(VideoHost.Child, _videoElementSurface))
            VideoHost.Child = null;
        _videoElementSurface.IsHitTestVisible = true;
        _videoElementSurface = null;
    }

    private void ApplyVideoSurfaceVisibility()
    {
        UpdateVideoTakeoverSeenState();
        var hasAttachedVideo = _videoElement is not null || _videoElementSurface is not null;
        var showTakeover = ShouldShowVideoTakeover;
        if (showTakeover)
            _videoTakeoverVisibleTrackId = GetVideoTakeoverTrackId();
        var hasVideoSurface = hasAttachedVideo || showTakeover;
        VideoHost.Visibility = hasAttachedVideo ? Visibility.Visible : Visibility.Collapsed;
        VideoTakeoverOverlay.Visibility = showTakeover ? Visibility.Visible : Visibility.Collapsed;
        var showLoading = hasAttachedVideo
            && _videoSurface.HasActiveSurface
            && !_videoSurface.HasActiveFirstFrame;
        var showBuffering = hasAttachedVideo
            && _videoSurface.HasActiveSurface
            && _videoSurface.HasActiveFirstFrame
            && _videoSurface.IsActiveSurfaceBuffering;
        VideoStatusText.Text = showBuffering ? "Buffering..." : "Loading video...";
        VideoStatusOverlay.Visibility = showLoading || showBuffering
            ? Visibility.Visible
            : Visibility.Collapsed;
        AlbumArtImage.Visibility = hasVideoSurface ? Visibility.Collapsed : Visibility.Visible;
        ApplyVideoPresentationMode();
    }

    private void ApplyVideoPresentationMode()
    {
        var showTakeover = ShouldShowVideoTakeover;
        var useVideoLayout = _isVideoPresentationMode
                             && ((_videoElement is not null || _videoElementSurface is not null) || showTakeover);
        var useTheaterLayout = useVideoLayout && _isTheaterMode;

        LayoutRoot.Padding = useTheaterLayout
            ? new Thickness(0)
            : useVideoLayout
            ? new Thickness(0, 0, 0, 18)
            : new Thickness(40, 28, 40, 24);
        LayoutRoot.RowSpacing = useTheaterLayout ? 0 : useVideoLayout ? 10 : 14;

        var contentInset = useTheaterLayout
            ? new Thickness(18, 0, 18, 0)
            : useVideoLayout
            ? new Thickness(32, 0, 32, 0)
            : new Thickness(0);
        TitleRow.Margin = contentInset;
        ProgressRow.Margin = contentInset;
        VideoControlBar.Margin = contentInset;

        TitleRow.Visibility = useTheaterLayout ? Visibility.Collapsed : Visibility.Visible;
        ProgressRow.Visibility = useTheaterLayout ? Visibility.Collapsed : Visibility.Visible;
        VideoControlBar.Visibility = useVideoLayout && !useTheaterLayout && !showTakeover ? Visibility.Visible : Visibility.Collapsed;
        VideoOverlay.Visibility = useTheaterLayout && !showTakeover ? Visibility.Visible : Visibility.Collapsed;

        TitleRow.ColumnSpacing = useVideoLayout ? 10 : 16;
        TrackTitleText.FontSize = useTheaterLayout ? 18 : useVideoLayout ? 20 : 22;
        ArtistMetadata.Opacity = useVideoLayout ? 0.82 : 1;

        var cornerRadius = useVideoLayout ? 0 : 14;
        AlbumArtHost.CornerRadius = new CornerRadius(cornerRadius);
        VideoHost.CornerRadius = new CornerRadius(cornerRadius);
        AlbumArtImage.CornerRadius = new CornerRadius(cornerRadius);

        MusicTransportRow.Visibility = useVideoLayout ? Visibility.Collapsed : Visibility.Visible;
        VolumeRow.Visibility = useVideoLayout ? Visibility.Collapsed : Visibility.Visible;
        DevicePicker.Visibility = useVideoLayout ? Visibility.Collapsed : Visibility.Visible;
        VideoTheaterModeButton.IsChecked = useTheaterLayout;
        VideoOverlayTheaterModeButton.IsChecked = useTheaterLayout;

        UpdateMediaFrameSize();
        AnimateTransform(MediaFrameTransform, useTheaterLayout ? 1.0 : useVideoLayout ? 1.01 : 1.0, 0, 320);
        ApplyVideoOverlayMode(useTheaterLayout && !showTakeover);
    }

    private void VideoTakeoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_videoSurface.HasActiveSurface)
            return;

        SuppressVideoTakeoverForCurrentTrack();
        _miniVideoViewModel?.SetSuppressedBySidebarPlayer(true);
        _videoSurface.AcquireSurface(this);
        _ownsVideoSurface = true;
        ApplyVideoSurfaceVisibility();
    }

    private void VideoTakeoverDismissButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressVideoTakeoverForCurrentTrack();
        ApplyVideoSurfaceVisibility();
    }

    private string? GetVideoTakeoverTrackId()
        => GetCurrentTrackId() ?? _playbackStateService?.CurrentTrackId;

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
        if (IsVideoTakeoverConflictActive)
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

    private void MediaStage_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateMediaFrameSize();

    private void UpdateMediaFrameSize()
    {
        if (MediaStage == null || MediaFrame == null)
            return;

        var availableWidth = MediaStage.ActualWidth;
        var availableHeight = MediaStage.ActualHeight;
        if (availableWidth <= 1 || availableHeight <= 1)
            return;

        var useVideoLayout = _isVideoPresentationMode
                             && ((_videoElement is not null || _videoElementSurface is not null) || ShouldShowVideoTakeover);

        if (!useVideoLayout)
        {
            var size = Math.Max(1, Math.Min(AudioArtworkMaxSize, Math.Min(availableWidth, availableHeight)));
            MediaFrame.Width = Math.Floor(size);
            MediaFrame.Height = Math.Floor(size);
            return;
        }

        var maxWidth = Math.Max(1, availableWidth);
        var maxHeight = Math.Max(1, availableHeight);

        if (_isTheaterMode)
        {
            MediaFrame.Width = Math.Floor(maxWidth);
            MediaFrame.Height = Math.Floor(maxHeight);
            return;
        }

        var width = maxWidth;
        var height = width / VideoAspectRatio;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * VideoAspectRatio;
        }

        MediaFrame.Width = Math.Floor(width);
        MediaFrame.Height = Math.Floor(height);
    }

    private void VideoTheaterModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetTheaterMode(!_isTheaterMode);
    }

    private void FitVideoWindowButton_Click(object sender, RoutedEventArgs e)
    {
        FitVideoWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void VideoQualityButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement target)
            SpotifyVideoQualityFlyout.ShowAt(target);
    }

    private void VideoQueueButton_Click(object sender, RoutedEventArgs e)
    {
        QueueRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MediaFrame_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!IsTheaterVideoLayoutActive()) return;
        FadeVideoOverlay(visible: true);
        RestartVideoOverlayHideTimer();
    }

    private void MediaFrame_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsTheaterVideoLayoutActive()) return;
        FadeVideoOverlay(visible: true);
        RestartVideoOverlayHideTimer();
    }

    private void MediaFrame_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!IsTheaterVideoLayoutActive()) return;
        _videoOverlayHideTimer?.Stop();
        FadeVideoOverlay(visible: false);
    }

    private bool IsTheaterVideoLayoutActive()
        => IsLoaded
           && _isTheaterMode
           && _isVideoPresentationMode
           && (_videoElement is not null || _videoElementSurface is not null);

    private void ApplyVideoOverlayMode(bool active)
    {
        if (!active)
        {
            _videoOverlayHideTimer?.Stop();
            _videoOverlayVisible = true;
            VideoOverlayChrome.Opacity = 1;
            VideoOverlay.IsHitTestVisible = false;
            return;
        }

        EnsureVideoOverlayHideTimer();
        FadeVideoOverlay(visible: true);
        RestartVideoOverlayHideTimer();
    }

    private void EnsureVideoOverlayHideTimer()
    {
        if (_videoOverlayHideTimer is not null) return;

        var dispatcher = DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
            return;

        _videoOverlayHideTimer = dispatcher.CreateTimer();
        _videoOverlayHideTimer.Interval = TimeSpan.FromMilliseconds(VideoOverlayIdleHideMs);
        _videoOverlayHideTimer.IsRepeating = false;
        _videoOverlayHideTimer.Tick += OnVideoOverlayHideTimerTick;
    }

    private void OnVideoOverlayHideTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsTheaterVideoLayoutActive())
        {
            sender.Stop();
            return;
        }

        FadeVideoOverlay(visible: false);
    }

    private void RestartVideoOverlayHideTimer()
    {
        if (_videoOverlayHideTimer is null) return;
        _videoOverlayHideTimer.Stop();
        _videoOverlayHideTimer.Start();
    }

    private void FadeVideoOverlay(bool visible)
    {
        var targetOpacity = visible ? 1.0 : 0.0;
        if (_videoOverlayVisible == visible && Math.Abs(VideoOverlayChrome.Opacity - targetOpacity) < 0.001)
            return;

        _videoOverlayVisible = visible;
        VideoOverlay.IsHitTestVisible = visible;

        var storyboard = new Storyboard();
        AddAnimation(
            storyboard,
            VideoOverlayChrome,
            nameof(UIElement.Opacity),
            targetOpacity,
            TimeSpan.FromMilliseconds(VideoOverlayFadeMs),
            new CubicEase { EasingMode = EasingMode.EaseOut });
        storyboard.Begin();
    }

    private static void AnimateTransform(CompositeTransform transform, double scale, double translateY, int durationMs)
    {
        var duration = System.TimeSpan.FromMilliseconds(durationMs);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        AddAnimation(storyboard, transform, nameof(CompositeTransform.ScaleX), scale, duration, easing);
        AddAnimation(storyboard, transform, nameof(CompositeTransform.ScaleY), scale, duration, easing);
        AddAnimation(storyboard, transform, nameof(CompositeTransform.TranslateY), translateY, duration, easing);
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

    private string? GetCurrentTrackId() => PlaybackSaveTargetResolver.GetTrackId(_playbackStateService);

    private void OnSaveStateChanged()
        => DispatcherQueue?.TryEnqueue(UpdateHeartState);

    private void OnPlaybackSaveTargetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentTrackIsVideo)
            or nameof(IPlaybackStateService.CurrentOriginalTrackId))
        {
            DispatcherQueue?.TryEnqueue(UpdateHeartState);
        }

        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentTrackIsVideo))
        {
            DispatcherQueue?.TryEnqueue(ApplyVideoSurfaceVisibility);
        }
    }

    private void UpdateHeartState()
    {
        var version = ++_heartStateVersion;
        var uri = PlaybackSaveTargetResolver.GetTrackUri(_playbackStateService);
        if (!string.IsNullOrEmpty(uri))
        {
            SetHeartState(_likeService?.IsSaved(SavedItemType.Track, uri) == true);
            return;
        }

        SetHeartState(false);
        _ = UpdateHeartStateAsync(version);
    }

    private void OnHeartClicked()
        => _ = OnHeartClickedAsync();

    private async Task UpdateHeartStateAsync(int version)
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(_playbackStateService, _musicVideoMetadata)
            .ConfigureAwait(true);

        if (version != _heartStateVersion)
            return;

        SetHeartState(!string.IsNullOrEmpty(uri)
            && _likeService?.IsSaved(SavedItemType.Track, uri) == true);
    }

    private async Task OnHeartClickedAsync()
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(_playbackStateService, _musicVideoMetadata)
            .ConfigureAwait(true);
        if (string.IsNullOrEmpty(uri) || _likeService == null) return;

        var wasLiked = _likeService.IsSaved(SavedItemType.Track, uri);
        _logger?.LogInformation("[ExpandedNowPlaying] Heart clicked: uri={Uri}, wasLiked={WasLiked}", uri, wasLiked);
        _likeService.ToggleSave(SavedItemType.Track, uri, wasLiked);
        SetHeartState(!wasLiked);
    }

    private void SetHeartState(bool isLiked)
    {
        HeartButton.IsLiked = isLiked;
        OverlayHeartButton.IsLiked = isLiked;
    }
}
