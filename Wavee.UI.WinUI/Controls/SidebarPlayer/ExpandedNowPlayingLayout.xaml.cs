using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
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
    public PlayerBarViewModel ViewModel { get; }
    private readonly IActiveVideoSurfaceService _videoSurface;
    private readonly MiniVideoPlayerViewModel? _miniVideoViewModel;
    private readonly ITrackLikeService? _likeService;
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly ILogger<ExpandedNowPlayingLayout>? _logger;
    private MediaPlayerElement? _videoElement;
    private FrameworkElement? _videoElementSurface;
    private bool _isVideoSurfaceEnabled;
    private bool _isVideoPresentationMode;
    private bool _isTheaterMode;
    private bool _ownsVideoSurface;

    public event EventHandler<bool>? TheaterModeChanged;

    public ExpandedNowPlayingLayout()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        _videoSurface = Ioc.Default.GetRequiredService<IActiveVideoSurfaceService>();
        _miniVideoViewModel = Ioc.Default.GetService<MiniVideoPlayerViewModel>();
        _likeService = Ioc.Default.GetService<ITrackLikeService>();
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        _logger = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger<ExpandedNowPlayingLayout>();

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        HeartButton.Command = new RelayCommand(OnHeartClicked);

        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _videoSurface.ActiveSurfaceChanged += OnActiveVideoSurfaceChanged;
        if (_miniVideoViewModel is not null)
            _miniVideoViewModel.PropertyChanged += OnMiniVideoViewModelPropertyChanged;
        UpdateHeartState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SetSurfaceVisible("widget", true);
        UpdateVideoSurfaceOwnership();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SetSurfaceVisible("widget", false);
        ReleaseVideoSurfaceOwnership();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _videoSurface.ActiveSurfaceChanged -= OnActiveVideoSurfaceChanged;
        if (_miniVideoViewModel is not null)
            _miniVideoViewModel.PropertyChanged -= OnMiniVideoViewModelPropertyChanged;
        if (_likeService != null) _likeService.SaveStateChanged -= OnSaveStateChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerBarViewModel.HasTrack) or nameof(PlayerBarViewModel.TrackTitle))
            UpdateHeartState();
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

    private void OnActiveVideoSurfaceChanged(object? sender, MediaPlayer? surface)
        => DispatcherQueue?.TryEnqueue(UpdateVideoSurfaceOwnership);

    private void OnMiniVideoViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MiniVideoPlayerViewModel.IsOnVideoPage))
            UpdateVideoSurfaceOwnership();
    }

    private bool ShouldHostVideoSurface =>
        IsLoaded
        && _isVideoSurfaceEnabled
        && _videoSurface.HasActiveSurface
        && _miniVideoViewModel?.IsOnVideoPage != true;

    private void UpdateVideoSurfaceOwnership()
    {
        if (ShouldHostVideoSurface)
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
        var hasVideo = _videoElement is not null || _videoElementSurface is not null;
        VideoHost.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
        AlbumArtImage.Visibility = hasVideo ? Visibility.Collapsed : Visibility.Visible;
        ApplyVideoPresentationMode();
    }

    private void ApplyVideoPresentationMode()
    {
        var useVideoLayout = _isVideoPresentationMode
                             && (_videoElement is not null || _videoElementSurface is not null);
        var useTheaterLayout = useVideoLayout && _isTheaterMode;

        LayoutRoot.Padding = useTheaterLayout
            ? new Thickness(18, 10, 18, 16)
            : useVideoLayout
            ? new Thickness(32, 16, 32, 18)
            : new Thickness(40, 28, 40, 24);
        LayoutRoot.RowSpacing = useTheaterLayout ? 8 : useVideoLayout ? 10 : 14;

        TitleRow.ColumnSpacing = useVideoLayout ? 10 : 16;
        TrackTitleText.FontSize = useTheaterLayout ? 18 : useVideoLayout ? 20 : 22;
        ArtistMetadata.Opacity = useVideoLayout ? 0.82 : 1;

        MediaFrame.Width = useTheaterLayout ? 1280 : useVideoLayout ? 960 : 540;
        MediaFrame.Height = useTheaterLayout ? 720 : useVideoLayout ? 540 : 540;
        AlbumArtHost.CornerRadius = new CornerRadius(useVideoLayout ? 8 : 14);
        VideoHost.CornerRadius = new CornerRadius(useVideoLayout ? 8 : 14);
        AlbumArtImage.CornerRadius = new CornerRadius(useVideoLayout ? 8 : 14);

        VideoControlBar.Visibility = useVideoLayout ? Visibility.Visible : Visibility.Collapsed;
        MusicTransportRow.Visibility = useVideoLayout ? Visibility.Collapsed : Visibility.Visible;
        VolumeRow.Visibility = useVideoLayout ? Visibility.Collapsed : Visibility.Visible;
        DevicePicker.Visibility = useVideoLayout ? Visibility.Collapsed : Visibility.Visible;
        VideoTheaterModeButton.IsChecked = useTheaterLayout;

        AnimateTransform(MediaFrameTransform, useTheaterLayout ? 1.0 : useVideoLayout ? 1.01 : 1.0, 0, 320);
    }

    private void VideoTheaterModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetTheaterMode(!_isTheaterMode);
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

    private string? GetCurrentTrackId() => _playbackStateService?.CurrentTrackId;

    private void OnSaveStateChanged()
        => DispatcherQueue?.TryEnqueue(UpdateHeartState);

    private void UpdateHeartState()
    {
        var trackId = GetCurrentTrackId();
        var isLiked = !string.IsNullOrEmpty(trackId)
            && _likeService?.IsSaved(SavedItemType.Track, trackId) == true;
        HeartButton.IsLiked = isLiked;
    }

    private void OnHeartClicked()
    {
        var trackId = GetCurrentTrackId();
        if (string.IsNullOrEmpty(trackId) || _likeService == null) return;

        // Only prefix bare base62 ids; full URIs (wavee:local:track:*, etc.)
        // pass through as-is. See PlayerBar.OnPlayerHeartClicked for the
        // background — same root cause.
        var uri = trackId.Contains(':') ? trackId : $"spotify:track:{trackId}";
        var wasLiked = HeartButton.IsLiked;
        _logger?.LogInformation("[ExpandedNowPlaying] Heart clicked: trackId={TrackId}, wasLiked={WasLiked}", trackId, wasLiked);
        _likeService.ToggleSave(SavedItemType.Track, uri, wasLiked);
        HeartButton.IsLiked = !wasLiked;
    }
}
