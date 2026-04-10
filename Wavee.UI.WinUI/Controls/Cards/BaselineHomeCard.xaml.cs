using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;
using Microsoft.UI;
using Wavee.Playback.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class BaselineHomeCard : UserControl
{
    private const double CardAspectRatio = 230d / 340d;
    private const int MaxDeferredHoverStateRefreshAttempts = 4;
    private const double PreviewTransitionDistance = 24d;
    private const double PreviewDuckTargetVolume = 0d;

    private static readonly TimeSpan PreviewTransitionOutDuration = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan PreviewTransitionInDuration = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan PreviewMotionResetDuration = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan PreviewDuckFadeOutDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan PreviewDuckFadeInDuration = TimeSpan.FromMilliseconds(220);

    private static BaselineHomeCard? s_activeCard;
    private static BaselineHomeCard? s_activeAudioCard;
    private static bool s_isPreviewAudioAutoPlayEnabled;

    private readonly ImageCacheService? _imageCache;
    private readonly PreviewAudioGraphService? _previewAudioGraphService;
    private readonly IPlaybackService? _playbackService;
    private readonly IPlaybackStateService? _playbackStateService;
    private MediaPlayer? _canvasMediaPlayer;
    private HomeSectionItem? _subscribedItem;
    private bool _isPointerOver;
    private bool _isPreviewAudioPlaying;
    private bool _isApplyingResponsiveSize;
    private bool _isHoverStateRefreshQueued;
    private bool _isPlaybackDucked;
    private bool _didPausePlaybackForPreview;
    private int _deferredHoverStateRefreshAttempts;
    private int _previewTrackIndex;
    private int _canvasPreviewVersion;
    private int _playbackDuckVersion;
    private bool _isPreviewTransitioning;
    private int? _queuedPreviewDelta;
    private int _previewTransitionVersion;
    private int _hoverValidationVersion;
    private int _hoverStopVersion;
    private double _playbackVolumeBeforeDuck;
    private string? _activeCanvasUrl;
    private string? _previewVisualizationSessionId;
    private string? _previewVisualizationUrl;

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(HomeSectionItem), typeof(BaselineHomeCard),
            new PropertyMetadata(null, OnItemChanged));

    public HomeSectionItem? Item
    {
        get => (HomeSectionItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public BaselineHomeCard()
    {
        _imageCache = Ioc.Default.GetService<ImageCacheService>();
        _previewAudioGraphService = Ioc.Default.GetService<PreviewAudioGraphService>();
        _playbackService = Ioc.Default.GetService<IPlaybackService>();
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        InitializeComponent();
        EffectiveViewportChanged += Card_EffectiveViewportChanged;
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (BaselineHomeCard)d;
        card.SetSubscribedItem(e.OldValue as HomeSectionItem, e.NewValue as HomeSectionItem);
        card._previewTrackIndex = 0;
        card.CancelPreviewTransition(resetMotionHosts: true);
        card.StopHoverMedia(deferCanvasTeardown: false);
        card.StopPreviewAudio();
        card.UpdateFromItem();
    }

    private void SetSubscribedItem(HomeSectionItem? oldItem, HomeSectionItem? newItem)
    {
        if (oldItem != null)
            oldItem.PropertyChanged -= Item_PropertyChanged;

        _subscribedItem = newItem;

        if (newItem != null)
            newItem.PropertyChanged += Item_PropertyChanged;
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateFromItem);
    }

    private void UpdateFromItem()
    {
        var item = Item;
        if (item == null)
            return;

        ClampPreviewTrackIndex(item);
        var activePreviewTrack = GetActivePreviewTrack(item);

        TitleText.Text = item.Title ?? "";
        SubtitleText.Text = item.Subtitle ?? "";
        TypeText.Text = item.ContentType switch
        {
            HomeContentType.Album => "Album",
            HomeContentType.Playlist => "Playlist",
            HomeContentType.Podcast => "Podcast",
            _ => "Made for you"
        };

        var previewTrackName = activePreviewTrack?.Name;
        PreviewEyebrowText.Visibility = string.IsNullOrWhiteSpace(previewTrackName)
            ? Visibility.Collapsed
            : Visibility.Visible;
        PreviewTrackText.Text = previewTrackName ?? "";
        PreviewTrackText.Visibility = string.IsNullOrWhiteSpace(previewTrackName)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl(item, activePreviewTrack));
        var canvasUrl = GetActiveCanvasUrl(item, activePreviewTrack);
        var hasCanvas = !string.IsNullOrWhiteSpace(canvasUrl);
        PreviewOverlayRoot.Visibility = _isPointerOver && hasPreviewAudio
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Visibility = _isPointerOver && hasPreviewAudio
                ? Visibility.Visible
                : Visibility.Collapsed;
            PreviewVisualizer.SetActive(_isPointerOver && hasPreviewAudio && _isPreviewAudioPlaying);
        }

        if (PreviewAudioButton != null)
        {
            PreviewAudioButton.Visibility = _isPointerOver && hasPreviewAudio
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var hasMultiplePreviewTracks = item.PreviewTracks.Count > 1;
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = _isPointerOver && hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = _isPointerOver && hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;

        if (_isPointerOver)
        {
            if (!hasPreviewAudio)
            {
                StopPreviewVisualization();
            }
            else if (_isPreviewAudioPlaying &&
                     !string.Equals(_previewVisualizationUrl, GetActiveAudioPreviewUrl(item, activePreviewTrack), StringComparison.Ordinal))
            {
                _ = StartPreviewAudioAsync();
            }
            else if (!_isPreviewAudioPlaying)
            {
                StopPreviewVisualization();
            }

            if (hasPreviewAudio && s_isPreviewAudioAutoPlayEnabled && !_isPreviewAudioPlaying)
                _ = StartPreviewAudioAsync();

            if (hasCanvas && !string.Equals(_activeCanvasUrl, canvasUrl, StringComparison.Ordinal))
                _ = StartCanvasPreviewAsync();
            else if (!hasCanvas)
                StopCanvasPreview();
        }

        LoadImages(
            GetActiveHeroImageUrl(item, activePreviewTrack),
            activePreviewTrack?.CoverArtUrl ?? item.ImageUrl);
        ApplyColor(activePreviewTrack?.ColorHex ?? item.HeroColorHex ?? item.ColorHex);
        UpdateLoadingState(item.IsBaselineLoading);
    }

    private void LoadImages(string? heroUrl, string? thumbUrl)
    {
        var heroHttpsUrl = SpotifyImageHelper.ToHttpsUrl(heroUrl);
        if (string.IsNullOrWhiteSpace(heroHttpsUrl))
        {
            HeroImage.Source = null;
        }
        else
        {
            BitmapImage? heroImage = _imageCache?.GetOrCreate(heroHttpsUrl);
            heroImage ??= new BitmapImage(new Uri(heroHttpsUrl)) { DecodePixelType = DecodePixelType.Logical };
            HeroImage.Source = heroImage;
        }

        var thumbHttpsUrl = SpotifyImageHelper.ToHttpsUrl(thumbUrl);
        if (string.IsNullOrWhiteSpace(thumbHttpsUrl))
        {
            CoverThumbImage.Source = null;
            CoverThumbPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        BitmapImage? thumbImage = _imageCache?.GetOrCreate(thumbHttpsUrl);
        thumbImage ??= new BitmapImage(new Uri(thumbHttpsUrl)) { DecodePixelType = DecodePixelType.Logical };
        CoverThumbImage.Source = thumbImage;
        CoverThumbPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void ApplyColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            ColorWash.Background = null;
            BottomPanel.ClearValue(Border.BackgroundProperty);
            return;
        }

        var color = ParseHexColor(hex);
        ColorWash.Background = new SolidColorBrush(color);
        BottomPanel.Background = new SolidColorBrush(Darken(color, 0.66));
    }

    private void UpdateLoadingState(bool isLoading)
    {
        if (isLoading)
            EnsureShimmerRealized();

        if (ShimmerOverlay != null)
            ShimmerOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

        TitleOverlay.Opacity = isLoading ? 0 : 1;
        BottomPanel.Opacity = isLoading ? 0 : 1;
        CoverThumbBorder.Opacity = isLoading ? 0 : 1;
        HeroImage.Opacity = isLoading ? 0.18 : 0.92;
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hoverStopVersion++;
        _isPointerOver = true;
        _deferredHoverStateRefreshAttempts = 0;

        if (s_activeCard != null && !ReferenceEquals(s_activeCard, this))
            s_activeCard.StopHoverMedia();

        s_activeCard = this;
        EnsureHoverChromeRealized();
        ApplyHoverState();
        StartHoverVisibilityValidation();

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());
        var hasCanvas = !string.IsNullOrWhiteSpace(GetActiveCanvasUrl());
        if (hasCanvas)
            EnsureCanvasPlayerRealized();
        if ((hasPreviewAudio && PreviewAudioButton == null) || (hasCanvas && CanvasPlayer == null))
            QueueDeferredHoverStateRefresh();
    }

    private void ApplyHoverState()
    {
        if (HoverChrome != null)
            HoverChrome.Visibility = Visibility.Visible;

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());

        if (PreviewAudioButton != null)
            PreviewAudioButton.Visibility = hasPreviewAudio ? Visibility.Visible : Visibility.Collapsed;

        if (PreviewOverlayRoot != null)
            PreviewOverlayRoot.Visibility = hasPreviewAudio ? Visibility.Visible : Visibility.Collapsed;

        var hasMultiplePreviewTracks = (Item?.PreviewTracks.Count ?? 0) > 1;
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;

        if (hasPreviewAudio && s_isPreviewAudioAutoPlayEnabled && !_isPreviewAudioPlaying)
            _ = StartPreviewAudioAsync();

        if (HoverChrome != null)
        {
            // Set opacity directly first to guarantee visibility,
            // then run the animation for smoothness on subsequent hovers
            HoverChrome.Opacity = 1;
            AnimationBuilder.Create()
                .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(140))
                .Start(HoverChrome);
        }

        Canvas.SetZIndex(this, 10);
        var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.CenterPoint = new System.Numerics.Vector3(
            (float)(CardRoot.ActualWidth / 2),
            (float)(CardRoot.ActualHeight / 2),
            0);

        AnimationBuilder.Create()
            .Scale(from: System.Numerics.Vector3.One, to: new System.Numerics.Vector3(1.025f), duration: TimeSpan.FromMilliseconds(180))
            .Start(CardRoot);

        if (!string.IsNullOrWhiteSpace(GetActiveCanvasUrl()))
            EnsureCanvasPlayerRealized();

        _ = StartCanvasPreviewAsync();
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        StopHoverMedia();
    }

    private void Card_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        StopHoverMedia();
    }

    private void StopHoverMedia(bool deferCanvasTeardown = true)
    {
        var stopVersion = ++_hoverStopVersion;
        _hoverValidationVersion++;
        CancelPreviewTransition(resetMotionHosts: true);
        _isPointerOver = false;
        _isHoverStateRefreshQueued = false;
        _deferredHoverStateRefreshAttempts = 0;
        StopPreviewVisualization();
        StopPreviewAudio();

        if (PreviewAudioButton != null)
            PreviewAudioButton.Visibility = Visibility.Collapsed;
        if (PreviewOverlayRoot != null)
            PreviewOverlayRoot.Visibility = Visibility.Collapsed;
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = Visibility.Collapsed;
        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Reset();
            PreviewVisualizer.SetActive(false);
            PreviewVisualizer.Visibility = Visibility.Collapsed;
        }

        if (IsLoaded)
        {
            if (HoverChrome != null)
            {
                AnimationBuilder.Create()
                    .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(120))
                    .Start(HoverChrome);
            }

            var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)(CardRoot.ActualWidth / 2),
                (float)(CardRoot.ActualHeight / 2),
                0);

            AnimationBuilder.Create()
                .Scale(from: new System.Numerics.Vector3(1.025f), to: System.Numerics.Vector3.One, duration: TimeSpan.FromMilliseconds(160))
                .Start(CardRoot);

            _ = CollapseHoverChromeAsync();
            Canvas.SetZIndex(this, 0);
        }

        if (ReferenceEquals(s_activeCard, this))
            s_activeCard = null;

        if (deferCanvasTeardown)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (stopVersion != _hoverStopVersion || _isPointerOver)
                    return;

                StopCanvasPreview();
            });
        }
        else
        {
            StopCanvasPreview();
        }
    }

    private void QueueDeferredHoverStateRefresh()
    {
        if (!_isPointerOver ||
            _isHoverStateRefreshQueued ||
            _deferredHoverStateRefreshAttempts >= MaxDeferredHoverStateRefreshAttempts)
            return;

        _isHoverStateRefreshQueued = true;
        _deferredHoverStateRefreshAttempts++;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _isHoverStateRefreshQueued = false;
            if (!_isPointerOver || !IsLoaded)
                return;

            ApplyHoverState();

            var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());
            var hasCanvas = !string.IsNullOrWhiteSpace(GetActiveCanvasUrl());
            if ((hasPreviewAudio && PreviewAudioButton == null) || (hasCanvas && CanvasPlayer == null))
                QueueDeferredHoverStateRefresh();
        }))
        {
            _isHoverStateRefreshQueued = false;
        }
    }

    private void ResetInteractionStateForNavigation()
    {
        _hoverValidationVersion++;
        CancelPreviewTransition(resetMotionHosts: true);
        _isPointerOver = false;
        _isHoverStateRefreshQueued = false;
        _deferredHoverStateRefreshAttempts = 0;

        StopCanvasPreview();
        StopPreviewVisualization();
        StopPreviewAudio();

        if (PreviewAudioButton != null)
            PreviewAudioButton.Visibility = Visibility.Collapsed;
        if (PreviewOverlayRoot != null)
            PreviewOverlayRoot.Visibility = Visibility.Collapsed;
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = Visibility.Collapsed;
        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Reset();
            PreviewVisualizer.SetActive(false);
            PreviewVisualizer.Visibility = Visibility.Collapsed;
        }
        if (HoverChrome != null)
        {
            HoverChrome.Opacity = 0;
            HoverChrome.Visibility = Visibility.Collapsed;
        }

        var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.Scale = System.Numerics.Vector3.One;
        Canvas.SetZIndex(this, 0);

        if (ReferenceEquals(s_activeCard, this))
            s_activeCard = null;
    }

    private void Card_EffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (!_isPointerOver)
            return;

        if (args.EffectiveViewport.Width <= 0 || args.EffectiveViewport.Height <= 0)
            StopHoverMedia();
    }

    private void StartHoverVisibilityValidation()
    {
        var version = ++_hoverValidationVersion;
        _ = ValidateHoverVisibilityAsync(version);
    }

    private async Task ValidateHoverVisibilityAsync(int version)
    {
        while (_isPointerOver && version == _hoverValidationVersion)
        {
            await Task.Delay(120);

            if (!_isPointerOver || version != _hoverValidationVersion)
                return;

            if (!IsCardVisibleInRootViewport())
            {
                StopHoverMedia();
                return;
            }
        }
    }

    private bool IsCardVisibleInRootViewport()
    {
        try
        {
            if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0 || XamlRoot?.Content is not UIElement root)
                return false;

            var rootSize = XamlRoot.Size;
            if (rootSize.Width <= 0 || rootSize.Height <= 0)
                return false;

            var bounds = TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
            return bounds.Right > 0 &&
                   bounds.Bottom > 0 &&
                   bounds.Left < rootSize.Width &&
                   bounds.Top < rootSize.Height;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Hover visibility check failed: {ex.Message}");
            return false;
        }
    }

    private async Task CollapseHoverChromeAsync()
    {
        await Task.Delay(150);
        if (!_isPointerOver && IsLoaded && HoverChrome != null)
            HoverChrome.Visibility = Visibility.Collapsed;
    }

    private async Task StartCanvasPreviewAsync()
    {
        var canvasUrl = GetActiveCanvasUrl();
        if (string.IsNullOrWhiteSpace(canvasUrl))
        {
            StopCanvasPreview();
            return;
        }

        try
        {
            var previewVersion = ++_canvasPreviewVersion;

            var isCanvasPlayerReady = await EnsureCanvasPlayerReadyAsync();
            if (CanvasPlayer == null || !isCanvasPlayerReady)
            {
                if (_isPointerOver &&
                    previewVersion == _canvasPreviewVersion &&
                    string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
                {
                    QueueDeferredHoverStateRefresh();
                    _ = RetryCanvasPreviewInitializationAsync(previewVersion, canvasUrl);
                }

                return;
            }

            if (!_isPointerOver ||
                previewVersion != _canvasPreviewVersion ||
                !string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
                return;

            _canvasMediaPlayer ??= new MediaPlayer
            {
                IsLoopingEnabled = true,
                IsMuted = true,
                AutoPlay = true
            };

            _activeCanvasUrl = canvasUrl;
            CanvasPlayer.Stretch = Stretch.UniformToFill;
            CanvasPlayer.AutoPlay = true;
            CanvasPlayer.SetMediaPlayer(_canvasMediaPlayer);
            CanvasPlayer.Visibility = Visibility.Visible;
            CanvasPlayer.Opacity = 0;

            _canvasMediaPlayer.Source = MediaSource.CreateFromUri(new Uri(canvasUrl));
            _canvasMediaPlayer.Play();
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isPointerOver ||
                    previewVersion != _canvasPreviewVersion ||
                    !string.Equals(_activeCanvasUrl, canvasUrl, StringComparison.Ordinal) ||
                    _canvasMediaPlayer == null ||
                    CanvasPlayer == null)
                    return;

                CanvasPlayer.Visibility = Visibility.Visible;
                _canvasMediaPlayer.Play();
            });
            _ = RetryCanvasPlaybackStartAsync(previewVersion, canvasUrl);

            AnimationBuilder.Create()
                .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(400),
                    delay: TimeSpan.FromMilliseconds(250),
                    easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
                .Start(CanvasPlayer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Canvas preview failed: {ex.Message}");
            StopCanvasPreview();
        }
    }

    private async Task RetryCanvasPlaybackStartAsync(int previewVersion, string canvasUrl)
    {
        await Task.Delay(120);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isPointerOver ||
                previewVersion != _canvasPreviewVersion ||
                !string.Equals(_activeCanvasUrl, canvasUrl, StringComparison.Ordinal) ||
                _canvasMediaPlayer == null ||
                CanvasPlayer == null)
                return;

            CanvasPlayer.Visibility = Visibility.Visible;
            _canvasMediaPlayer.Play();
        });
    }

    private async Task RetryCanvasPreviewInitializationAsync(int previewVersion, string canvasUrl)
    {
        await Task.Delay(90);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isPointerOver ||
                previewVersion != _canvasPreviewVersion ||
                !string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(_activeCanvasUrl))
                return;

            _ = StartCanvasPreviewAsync();
        });
    }

    private async Task<bool> EnsureCanvasPlayerReadyAsync()
    {
        EnsureCanvasPlayerRealized();
        var player = CanvasPlayer;
        if (player == null)
            return false;

        if (player.IsLoaded)
            return true;

        player.Visibility = Visibility.Visible;
        player.Opacity = 0;
        player.Stretch = Stretch.UniformToFill;
        player.AutoPlay = true;

        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            player.Loaded -= loadedHandler;
            loaded.TrySetResult();
        };

        player.Loaded += loadedHandler;
        await Task.WhenAny(loaded.Task, Task.Delay(220));

        if (loadedHandler != null)
            player.Loaded -= loadedHandler;

        return player.IsLoaded;
    }

    private void StopCanvasPreview()
    {
        _canvasPreviewVersion++;
        _activeCanvasUrl = null;

        if (_canvasMediaPlayer == null)
        {
            if (CanvasPlayer != null)
            {
                DetachCanvasPlayer();
                CanvasPlayer.Visibility = Visibility.Collapsed;
                CanvasPlayer.Opacity = 0;
            }
            return;
        }

        try
        {
            _canvasMediaPlayer.Pause();
            _canvasMediaPlayer.Source = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Canvas preview stop failed: {ex.Message}");
        }

        DetachCanvasPlayer();
        if (CanvasPlayer != null)
        {
            CanvasPlayer.Visibility = Visibility.Collapsed;
            CanvasPlayer.Opacity = 0;
        }
    }

    private void DetachCanvasPlayer()
    {
        if (CanvasPlayer == null)
            return;

        try
        {
            CanvasPlayer.SetMediaPlayer(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Canvas player detach failed: {ex.Message}");
        }
    }

    private void StartPreviewVisualization()
    {
        var previewUrl = GetActiveAudioPreviewUrl();
        if (!_isPointerOver || string.IsNullOrWhiteSpace(previewUrl))
            return;

        EnsurePreviewVisualizerRealized();
        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Visibility = Visibility.Visible;
            PreviewVisualizer.Reset();
            PreviewVisualizer.SetActive(true);
        }

        _previewVisualizationUrl = previewUrl;
        _previewVisualizationSessionId = _previewAudioGraphService?.CurrentSessionId;
    }

    private void StopPreviewVisualization()
    {
        _previewVisualizationSessionId = null;
        _previewVisualizationUrl = null;

        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Reset();
            PreviewVisualizer.SetActive(false);
        }
    }

    private void OnPreviewVisualizationFrame(PreviewVisualizationFrame frame)
    {
        if (!_isPointerOver || !_isPreviewAudioPlaying || PreviewVisualizer == null)
            return;

        if (!string.IsNullOrWhiteSpace(_previewVisualizationSessionId) &&
            !string.Equals(frame.SessionId, _previewVisualizationSessionId, StringComparison.Ordinal))
            return;

        if (frame.Completed)
        {
            PreviewVisualizer.Reset();
            return;
        }

        PreviewVisualizer.PushLevels(frame.Amplitudes);
    }

    private void OnPreviewAudioCompleted()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_isPreviewAudioPlaying)
                return;

            await AutoAdvancePreviewAfterAudioEndedAsync();
        });
    }

    private void PreviewAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewAudioPlaying)
        {
            s_isPreviewAudioAutoPlayEnabled = false;
            StopPreviewAudio();
            return;
        }

        s_isPreviewAudioAutoPlayEnabled = true;
        _ = StartPreviewAudioAsync();
    }

    private async Task StartPreviewAudioAsync()
    {
        var previewUrl = GetActiveAudioPreviewUrl();
        if (string.IsNullOrWhiteSpace(previewUrl))
            return;

        if (s_activeAudioCard != null && !ReferenceEquals(s_activeAudioCard, this))
            TransferPreviewAudioFrom(s_activeAudioCard);

        StopPreviewAudio(restorePlaybackDucking: false);

        try
        {
            await DuckLocalPlaybackForPreviewAsync();
            if (_previewAudioGraphService == null)
                throw new InvalidOperationException("Preview audio service is unavailable.");

            await _previewAudioGraphService.StartAsync(previewUrl, OnPreviewVisualizationFrame, OnPreviewAudioCompleted);

            _isPreviewAudioPlaying = true;
            s_activeAudioCard = this;
            StartPreviewVisualization();

            if (PreviewAudioIcon != null)
                PreviewAudioIcon.Glyph = "\uE15D";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Audio preview failed: {ex.Message}");
            StopPreviewAudio();
        }
    }

    private void TransferPreviewAudioFrom(BaselineHomeCard activeCard)
    {
        var wasPlaybackDucked = activeCard._isPlaybackDucked;
        var restoreVolume = activeCard._playbackVolumeBeforeDuck;
        var didPausePlayback = activeCard._didPausePlaybackForPreview;

        activeCard._playbackDuckVersion++;
        activeCard.StopPreviewAudio(restorePlaybackDucking: false);

        if (!wasPlaybackDucked)
            return;

        activeCard._isPlaybackDucked = false;
        activeCard._didPausePlaybackForPreview = false;

        _isPlaybackDucked = true;
        _didPausePlaybackForPreview = didPausePlayback;
        _playbackVolumeBeforeDuck = restoreVolume;
    }

    private void StopPreviewAudio(bool restorePlaybackDucking = true)
    {
        if (_previewAudioGraphService != null)
            _ = _previewAudioGraphService.StopAsync();

        _isPreviewAudioPlaying = false;
        StopPreviewVisualization();

        if (ReferenceEquals(s_activeAudioCard, this))
            s_activeAudioCard = null;
        if (PreviewAudioIcon != null)
            PreviewAudioIcon.Glyph = "\uE198";

        if (restorePlaybackDucking)
            _ = RestoreLocalPlaybackAfterPreviewAsync();
    }

    private async Task AutoAdvancePreviewAfterAudioEndedAsync()
    {
        var item = Item;
        if (!_isPointerOver || item == null || item.PreviewTracks.Count <= 1)
        {
            StopPreviewAudio();
            return;
        }

        s_isPreviewAudioAutoPlayEnabled = true;
        await ChangePreviewTrackAsync(1);
    }

    private async Task DuckLocalPlaybackForPreviewAsync()
    {
        var playback = _playbackStateService;
        var duckVersion = ++_playbackDuckVersion;

        if (_isPlaybackDucked)
        {
            if (playback != null && !playback.IsVolumeRestricted)
                playback.Volume = PreviewDuckTargetVolume;

            return;
        }

        if (playback == null || !playback.IsPlaying || playback.IsPlayingRemotely || playback.IsVolumeRestricted)
            return;

        _playbackVolumeBeforeDuck = playback.Volume;
        _isPlaybackDucked = true;
        _didPausePlaybackForPreview = false;

        await FadePlaybackVolumeAsync(
            playback,
            from: playback.Volume,
            to: PreviewDuckTargetVolume,
            duration: PreviewDuckFadeOutDuration,
            duckVersion);

        if (duckVersion != _playbackDuckVersion ||
            _playbackService == null ||
            !playback.IsPlaying ||
            playback.IsPlayingRemotely)
            return;

        try
        {
            var pauseResult = await _playbackService.PauseAsync();
            if (duckVersion == _playbackDuckVersion)
                _didPausePlaybackForPreview = pauseResult.IsSuccess;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Local playback pause for preview failed: {ex.Message}");
        }
    }

    private async Task RestoreLocalPlaybackAfterPreviewAsync()
    {
        var playback = _playbackStateService;
        var duckVersion = ++_playbackDuckVersion;

        if (!_isPlaybackDucked || playback == null)
            return;

        var restoreVolume = _playbackVolumeBeforeDuck;
        var shouldResumePlayback = _didPausePlaybackForPreview;
        _isPlaybackDucked = false;
        _didPausePlaybackForPreview = false;

        if (shouldResumePlayback &&
            _playbackService != null &&
            !playback.IsPlayingRemotely)
        {
            try
            {
                await _playbackService.ResumeAsync();
                if (duckVersion != _playbackDuckVersion)
                    return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Local playback resume after preview failed: {ex.Message}");
            }
        }

        if (playback.IsVolumeRestricted)
            return;

        await FadePlaybackVolumeAsync(
            playback,
            from: playback.Volume,
            to: restoreVolume,
            duration: PreviewDuckFadeInDuration,
            duckVersion);
    }

    private async Task FadePlaybackVolumeAsync(
        IPlaybackStateService playback,
        double from,
        double to,
        TimeSpan duration,
        int duckVersion)
    {
        const int steps = 8;
        var stepDelay = TimeSpan.FromMilliseconds(duration.TotalMilliseconds / steps);

        from = Math.Clamp(from, 0, 100);
        to = Math.Clamp(to, 0, 100);

        for (var step = 1; step <= steps; step++)
        {
            if (duckVersion != _playbackDuckVersion)
                return;

            var progress = step / (double)steps;
            var eased = 1 - Math.Pow(1 - progress, 3);
            playback.Volume = from + ((to - from) * eased);
            await Task.Delay(stepDelay);
        }

        if (duckVersion == _playbackDuckVersion)
            playback.Volume = to;
    }

    private async void PreviousPreviewTrackButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangePreviewTrackAsync(-1);
    }

    private async void NextPreviewTrackButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangePreviewTrackAsync(1);
    }

    private void CardRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsPreviewButtonSource(e.OriginalSource))
        {
            e.Handled = true;
            return;
        }

        var item = Item;
        if (item == null)
            return;

        ResetInteractionStateForNavigation();
        HomeViewModel.NavigateToItem(item, NavigationHelpers.IsCtrlPressed());
        e.Handled = true;
    }

    private void CardRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsPreviewButtonSource(e.OriginalSource))
        {
            e.Handled = true;
            return;
        }

        var item = Item;
        if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed && item != null)
        {
            ResetInteractionStateForNavigation();
            HomeViewModel.NavigateToItem(item, openInNewTab: true);
            e.Handled = true;
        }
    }

    private void CardRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = Item;
        if (item == null)
            return;

        var menu = new MenuFlyout();
        var openNewTab = new MenuFlyoutItem
        {
            Text = "Open in new tab",
            Icon = new SymbolIcon(Symbol.OpenWith)
        };
        openNewTab.Click += (_, _) =>
        {
            ResetInteractionStateForNavigation();
            HomeViewModel.NavigateToItem(item, openInNewTab: true);
        };
        menu.Items.Add(openNewTab);
        menu.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }

    private bool IsPreviewButtonSource(object? source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (ReferenceEquals(current, PreviewAudioButton) ||
                ReferenceEquals(current, PreviousPreviewTrackButton) ||
                ReferenceEquals(current, NextPreviewTrackButton))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        SetSubscribedItem(_subscribedItem, null);

        CancelPreviewTransition(resetMotionHosts: false);
        _hoverValidationVersion++;
        _isPointerOver = false;
        StopCanvasPreview();
        StopPreviewVisualization();
        StopPreviewAudio();

        if (ReferenceEquals(s_activeCard, this))
            s_activeCard = null;

        try
        {
            _canvasMediaPlayer?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Canvas player dispose failed: {ex.Message}");
        }

        _canvasMediaPlayer = null;
    }

    private void BaselineHomeCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isApplyingResponsiveSize)
            return;

        _isApplyingResponsiveSize = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var width = ActualWidth > 0 ? ActualWidth : e.NewSize.Width;
                if (width <= 0)
                    return;

                var targetHeight = width / CardAspectRatio;
                if (!double.IsNaN(Height) && Math.Abs(Height - targetHeight) < 1)
                    return;

                Height = targetHeight;
            }
            finally
            {
                _isApplyingResponsiveSize = false;
            }
        }))
        {
            _isApplyingResponsiveSize = false;
        }
    }

    private void EnsureHoverChromeRealized()
    {
        if (HoverChrome != null)
            return;

        _ = FindName(nameof(HoverChrome));
    }

    private void EnsureCanvasPlayerRealized()
    {
        if (CanvasPlayer != null)
            return;

        _ = FindName(nameof(CanvasPlayer));
    }

    private void EnsureShimmerRealized()
    {
        if (ShimmerOverlay != null)
            return;

        _ = FindName(nameof(ShimmerOverlay));
    }

    private void EnsurePreviewVisualizerRealized()
    {
        if (PreviewVisualizer != null)
            return;
    }

    private async Task ChangePreviewTrackAsync(int delta)
    {
        delta = Math.Sign(delta);
        if (delta == 0)
            return;

        var item = Item;
        if (item == null || item.PreviewTracks.Count <= 1)
            return;

        if (_isPreviewTransitioning)
        {
            _queuedPreviewDelta = delta;
            return;
        }

        _isPreviewTransitioning = true;
        var version = ++_previewTransitionVersion;

        try
        {
            var wasPreviewAudioPlaying = _isPreviewAudioPlaying;

            await AnimatePreviewOutAsync(delta, version);
            if (!IsPreviewTransitionCurrent(version))
                return;

            var shouldRestartPreviewAudio = ApplyPreviewTrackChange(delta, keepPlaybackDucked: wasPreviewAudioPlaying);
            if (shouldRestartPreviewAudio && IsPreviewTransitionCurrent(version))
                _ = StartPreviewAudioAsync();

            await AnimatePreviewInAsync(delta, version);
            if (IsPreviewTransitionCurrent(version))
                ResetPreviewMotionHosts();
        }
        finally
        {
            if (version == _previewTransitionVersion)
                _isPreviewTransitioning = false;
        }

        if (version == _previewTransitionVersion)
            await RunQueuedPreviewTransitionAsync();
    }

    private bool ApplyPreviewTrackChange(int delta, bool keepPlaybackDucked)
    {
        var item = Item;
        if (item == null || item.PreviewTracks.Count <= 1)
            return false;

        _previewTrackIndex = (_previewTrackIndex + delta + item.PreviewTracks.Count) % item.PreviewTracks.Count;
        var shouldKeepPlaybackDucked =
            keepPlaybackDucked &&
            !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl(item, GetActivePreviewTrack(item)));

        StopCanvasPreview();
        StopPreviewVisualization();
        StopPreviewAudio(restorePlaybackDucking: !shouldKeepPlaybackDucked);

        UpdateFromItem();
        return shouldKeepPlaybackDucked;
    }

    private async Task RunQueuedPreviewTransitionAsync()
    {
        var queuedDelta = _queuedPreviewDelta;
        _queuedPreviewDelta = null;

        if (queuedDelta == null || !_isPointerOver || !IsLoaded)
            return;

        await ChangePreviewTrackAsync(queuedDelta.Value);
    }

    private async Task AnimatePreviewOutAsync(int direction, int version)
    {
        var targetOffset = direction > 0 ? -PreviewTransitionDistance : PreviewTransitionDistance;

        await Task.WhenAll(
            AnimatePreviewElementOutAsync(HeroMotionHost, targetOffset, scaleTo: 1f),
            AnimatePreviewElementOutAsync(CoverThumbBorder, targetOffset, scaleTo: 0.97f),
            AnimatePreviewElementOutAsync(TitleOverlay, targetOffset, scaleTo: 1f),
            AnimatePreviewElementOutAsync(BottomContentMotionHost, targetOffset, scaleTo: 1f));

        if (!IsPreviewTransitionCurrent(version) && IsLoaded)
            ResetPreviewMotionHosts();
    }

    private async Task AnimatePreviewInAsync(int direction, int version)
    {
        var startOffset = direction > 0 ? PreviewTransitionDistance : -PreviewTransitionDistance;
        PreparePreviewMotionForIncoming(startOffset);

        if (!IsPreviewTransitionCurrent(version))
        {
            if (IsLoaded)
                ResetPreviewMotionHosts();
            return;
        }

        await Task.WhenAll(
            AnimatePreviewElementInAsync(HeroMotionHost, startOffset, scaleFrom: 1f),
            AnimatePreviewElementInAsync(CoverThumbBorder, startOffset, scaleFrom: 0.97f),
            AnimatePreviewElementInAsync(TitleOverlay, startOffset, scaleFrom: 1f),
            AnimatePreviewElementInAsync(BottomContentMotionHost, startOffset, scaleFrom: 1f));

        if (!IsPreviewTransitionCurrent(version) && IsLoaded)
            ResetPreviewMotionHosts();
    }

    private Task AnimatePreviewElementOutAsync(UIElement element, double targetOffset, float scaleTo)
    {
        return AnimationBuilder.Create()
            .Opacity(to: 0, duration: PreviewTransitionOutDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn)
            .Translation(Axis.X, to: targetOffset, duration: PreviewTransitionOutDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn)
            .Scale(to: new System.Numerics.Vector3(scaleTo, scaleTo, 1f), duration: PreviewTransitionOutDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn)
            .StartAsync(element);
    }

    private Task AnimatePreviewElementInAsync(UIElement element, double startOffset, float scaleFrom)
    {
        return AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: PreviewTransitionInDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .Translation(Axis.X, from: startOffset, to: 0, duration: PreviewTransitionInDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .Scale(from: new System.Numerics.Vector3(scaleFrom, scaleFrom, 1f),
                to: System.Numerics.Vector3.One,
                duration: PreviewTransitionInDuration,
                easingType: EasingType.Sine,
                easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .StartAsync(element);
    }

    private void PreparePreviewMotionForIncoming(double startOffset)
    {
        foreach (var element in GetPreviewMotionElements())
        {
            element.Opacity = 0;
            var scale = ReferenceEquals(element, CoverThumbBorder) ? 0.97f : 1f;
            AnimationBuilder.Create()
                .Translation(Axis.X, to: startOffset, duration: PreviewMotionResetDuration)
                .Scale(to: new System.Numerics.Vector3(scale, scale, 1f), duration: PreviewMotionResetDuration)
                .Start(element);
        }
    }

    private void CancelPreviewTransition(bool resetMotionHosts)
    {
        _previewTransitionVersion++;
        _queuedPreviewDelta = null;
        _isPreviewTransitioning = false;

        if (resetMotionHosts)
            ResetPreviewMotionHosts();
    }

    private bool IsPreviewTransitionCurrent(int version)
    {
        return version == _previewTransitionVersion && IsLoaded;
    }

    private void ResetPreviewMotionHosts()
    {
        foreach (var element in GetPreviewMotionElements())
        {
            element.Opacity = 1;
            AnimationBuilder.Create()
                .Translation(Axis.X, to: 0, duration: PreviewMotionResetDuration)
                .Scale(to: System.Numerics.Vector3.One, duration: PreviewMotionResetDuration)
                .Start(element);
        }
    }

    private UIElement[] GetPreviewMotionElements()
    {
        return [HeroMotionHost, CoverThumbBorder, TitleOverlay, BottomContentMotionHost];
    }

    private void ClampPreviewTrackIndex(HomeSectionItem item)
    {
        if (item.PreviewTracks.Count == 0)
        {
            _previewTrackIndex = 0;
            return;
        }

        _previewTrackIndex = Math.Clamp(_previewTrackIndex, 0, item.PreviewTracks.Count - 1);
    }

    private HomeBaselinePreviewTrack? GetActivePreviewTrack(HomeSectionItem? item = null)
    {
        item ??= Item;
        if (item == null || item.PreviewTracks.Count == 0)
            return null;

        ClampPreviewTrackIndex(item);
        return item.PreviewTracks[_previewTrackIndex];
    }

    private string? GetActiveAudioPreviewUrl(HomeSectionItem? item = null, HomeBaselinePreviewTrack? track = null)
    {
        item ??= Item;
        track ??= GetActivePreviewTrack(item);
        return track?.AudioPreviewUrl ?? item?.AudioPreviewUrl;
    }

    private string? GetActiveCanvasUrl(HomeSectionItem? item = null, HomeBaselinePreviewTrack? track = null)
    {
        item ??= Item;
        track ??= GetActivePreviewTrack(item);
        return track?.CanvasUrl ?? item?.CanvasUrl;
    }

    private string? GetActiveHeroImageUrl(HomeSectionItem? item = null, HomeBaselinePreviewTrack? track = null)
    {
        item ??= Item;
        track ??= GetActivePreviewTrack(item);
        return track?.CanvasThumbnailUrl
            ?? track?.CoverArtUrl
            ?? item?.HeroImageUrl
            ?? item?.CanvasThumbnailUrl
            ?? item?.ImageUrl;
    }

    private static Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length switch
        {
            6 => Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16)),
            8 => Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16)),
            _ => Colors.Black
        };
    }

    private static Color Darken(Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R * amount, 0, 255),
            (byte)Math.Clamp(color.G * amount, 0, 255),
            (byte)Math.Clamp(color.B * amount, 0, 255));
    }
}
