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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Microsoft.UI;
using System.Diagnostics;
using Wavee.Playback.Contracts;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class BaselineHomeCard : UserControl
{
    private const int HeroImageDecodeSize = 240;
    private const int ThumbImageDecodeSize = 96;
    private const int MaxDeferredHoverStateRefreshAttempts = 4;
    private const double PreviewTransitionDistance = 24d;

    private static readonly TimeSpan PreviewTransitionOutDuration = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan PreviewTransitionInDuration = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan PreviewMotionResetDuration = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan PreviewHoverAutoplayDelay = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan PreviewPendingVisualDelay = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan PreviewPendingProgressDuration = PreviewHoverAutoplayDelay - PreviewPendingVisualDelay;

    private static BaselineHomeCard? s_activeCard;

    private readonly ImageCacheService? _imageCache;
    private readonly ICardPreviewPlaybackCoordinator? _previewPlaybackCoordinator;
    private readonly ISharedCardCanvasPreviewService? _sharedCanvasPreviewService;
    private readonly IPlaybackService? _playbackService;
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly NowPlayingHighlightService? _highlightService;
    private readonly Guid _previewOwnerId = Guid.NewGuid();
    private CanvasPreviewLease? _canvasPreviewLease;
    private HomeSectionItem? _subscribedItem;
    private bool _isPointerOver;
    private bool _isPreviewAudioPending;
    private bool _isPreviewAudioPlaying;
    private bool _isHoverStateRefreshQueued;
    private int _deferredHoverStateRefreshAttempts;
    private int _previewTrackIndex;
    private int _canvasPreviewVersion;
    private int _hoverEnterVersion;
    private bool _isPreviewTransitioning;
    private int? _queuedPreviewDelta;
    private int _previewTransitionVersion;
    private int _previewPendingVisualVersion;
    private int _hoverStopVersion;
    private bool _hoverEnterGuardActive;
    private Storyboard? _previewPendingProgressStoryboard;
    private Windows.Foundation.Point _lastPointerWindowPosition;
    private bool _hasLastPointerWindowPosition;
    private UIElement? _rootPointerElement;
    private string? _activeCanvasUrl;
    private bool _hasPreviewVisualization;
    private string? _previewVisualizationSessionId;
    private string? _previewVisualizationUrl;
    private bool _isContextPlaying;
    private bool _isContextPaused;
    private bool _isPlaybackPending;
    private int _playbackPendingVersion;

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
        _previewPlaybackCoordinator = Ioc.Default.GetService<ICardPreviewPlaybackCoordinator>();
        _sharedCanvasPreviewService = Ioc.Default.GetService<ISharedCardCanvasPreviewService>();
        _playbackService = Ioc.Default.GetService<IPlaybackService>();
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        _highlightService = Ioc.Default.GetService<NowPlayingHighlightService>();
        InitializeComponent();
    }

    [Conditional("DEBUG")]
    private void TraceCard(string message)
    {
        Debug.WriteLine(
            $"[BaselineHomeCard:{GetHashCode():x8}] {message} | " +
            $"title='{Item?.Title ?? "<null>"}' loaded={IsLoaded} pointer={_isPointerOver} " +
            $"enterV={_hoverEnterVersion} stopV={_hoverStopVersion} " +
            $"previewPending={_isPreviewAudioPending} previewPlaying={_isPreviewAudioPlaying} " +
            $"canvasUrl='{GetActiveCanvasUrl() ?? "<null>"}' activeCanvas='{_activeCanvasUrl ?? "<null>"}'");
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
        if (card._highlightService != null)
        {
            var (contextUri, albumUri, playing) = card._highlightService.Current;
            card.ApplyHighlight(contextUri, albumUri, playing);
        }
    }

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        TraceCard("Loaded");
        AttachRootPointerHandlers();

        if (_highlightService != null)
        {
            _highlightService.CurrentChanged += OnHighlightServiceChanged;
            var (contextUri, albumUri, playing) = _highlightService.Current;
            ApplyHighlight(contextUri, albumUri, playing);
        }

        if (_sharedCanvasPreviewService != null)
            _ = _sharedCanvasPreviewService.EnsureInitializedAsync();

        if (_isPointerOver)
        {
            TraceCard("Loaded while pointer already over card; re-queueing hover activation");
            _hoverEnterGuardActive = true;
            QueueHoverEnterActivation(_hoverEnterVersion);
        }
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
        UpdatePreviewVisualState(hasPreviewAudio);

        if (TrackPlayButton != null)
        {
            TrackPlayButton.Visibility = _isPointerOver && hasPreviewAudio
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        UpdatePreviewButtonVisualState();

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
            BitmapImage? heroImage = _imageCache?.GetOrCreate(heroHttpsUrl, HeroImageDecodeSize);
            heroImage ??= new BitmapImage(new Uri(heroHttpsUrl))
            {
               // DecodePixelWidth = HeroImageDecodeSize,
                DecodePixelType = DecodePixelType.Logical
            };
            HeroImage.Source = heroImage;
        }

        var thumbHttpsUrl = SpotifyImageHelper.ToHttpsUrl(thumbUrl);
        if (string.IsNullOrWhiteSpace(thumbHttpsUrl))
        {
            CoverThumbImage.Source = null;
            CoverThumbPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        BitmapImage? thumbImage = _imageCache?.GetOrCreate(thumbHttpsUrl, ThumbImageDecodeSize);
        thumbImage ??= new BitmapImage(new Uri(thumbHttpsUrl))
        {
            DecodePixelWidth = ThumbImageDecodeSize,
            DecodePixelType = DecodePixelType.Logical
        };
        CoverThumbImage.Source = thumbImage;
        CoverThumbPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void ApplyColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            ColorWash.Background = null;
            BottomPanel.ClearValue(Border.BackgroundProperty);
            UpdateHeroToBottomBlendBrush(GetBottomPanelBackgroundColor());
            return;
        }

        var color = ParseHexColor(hex);
        var bottomColor = Darken(color, 0.66);
        ColorWash.Background = new SolidColorBrush(color);
        BottomPanel.Background = new SolidColorBrush(bottomColor);
        UpdateHeroToBottomBlendBrush(bottomColor);
    }

    private void UpdateHeroToBottomBlendBrush(Color bottomColor)
    {
        HeroToBottomBlendOverlay.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0, bottomColor.R, bottomColor.G, bottomColor.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(18, bottomColor.R, bottomColor.G, bottomColor.B), Offset = 0.28 },
                new GradientStop { Color = Color.FromArgb(86, bottomColor.R, bottomColor.G, bottomColor.B), Offset = 0.68 },
                new GradientStop { Color = Color.FromArgb(255, bottomColor.R, bottomColor.G, bottomColor.B), Offset = 1 }
            }
        };
    }

    private Color GetBottomPanelBackgroundColor()
    {
        if (BottomPanel.Background is SolidColorBrush solidBrush)
            return solidBrush.Color;

        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var brushObj)
            && brushObj is SolidColorBrush themeBrush)
        {
            return themeBrush.Color;
        }

        return Color.FromArgb(255, 32, 32, 32);
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
        UpdateLastPointerWindowPosition(e);
        _hoverStopVersion++;
        _isPointerOver = true;
        _deferredHoverStateRefreshAttempts = 0;
        var hoverEnterVersion = ++_hoverEnterVersion;
        TraceCard($"PointerEntered hoverEnterVersion={hoverEnterVersion}");

        if (s_activeCard != null && !ReferenceEquals(s_activeCard, this))
            s_activeCard.StopHoverMedia();

        s_activeCard = this;
        _hoverEnterGuardActive = true;
        QueueHoverEnterActivation(hoverEnterVersion);
    }

    private void Card_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdateLastPointerWindowPosition(e);
    }

    private void AttachRootPointerHandlers()
    {
        if (ReferenceEquals(_rootPointerElement, XamlRoot?.Content))
            return;

        DetachRootPointerHandlers();

        if (XamlRoot?.Content is not UIElement root)
            return;

        root.PointerMoved += Root_PointerMoved;
        root.PointerExited += Root_PointerExited;
        root.PointerCanceled += Root_PointerCanceled;
        _rootPointerElement = root;
    }

    private void DetachRootPointerHandlers()
    {
        if (_rootPointerElement == null)
            return;

        _rootPointerElement.PointerMoved -= Root_PointerMoved;
        _rootPointerElement.PointerExited -= Root_PointerExited;
        _rootPointerElement.PointerCanceled -= Root_PointerCanceled;
        _rootPointerElement = null;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdateLastPointerWindowPosition(e);

        if (_isPointerOver && _hasLastPointerWindowPosition && !IsPointWithinCardBounds(_lastPointerWindowPosition))
        {
            TraceCard("Root pointer moved outside card -> StopHoverMedia");
            StopHoverMedia();
        }
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hasLastPointerWindowPosition = false;
        if (_isPointerOver)
        {
            TraceCard("Root pointer exited window -> StopHoverMedia");
            StopHoverMedia();
        }
    }

    private void Root_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _hasLastPointerWindowPosition = false;
        if (_isPointerOver)
        {
            TraceCard("Root pointer canceled -> StopHoverMedia");
            StopHoverMedia();
        }
    }

    private void QueueHoverEnterActivation(int hoverEnterVersion)
    {
        TraceCard($"QueueHoverEnterActivation hoverEnterVersion={hoverEnterVersion}");
        if (!DispatcherQueue.TryEnqueue(() =>
            ActivateHoverStateIfCurrent(hoverEnterVersion)))
        {
            TraceCard($"QueueHoverEnterActivation failed to enqueue hoverEnterVersion={hoverEnterVersion}");
            _hoverEnterGuardActive = false;
        }
    }

    private void ActivateHoverStateIfCurrent(int hoverEnterVersion)
    {
        if (!_isPointerOver || hoverEnterVersion != _hoverEnterVersion)
        {
            TraceCard($"ActivateHoverStateIfCurrent ignored hoverEnterVersion={hoverEnterVersion}");
            _hoverEnterGuardActive = false;
            return;
        }

        if (!IsLoaded)
        {
            TraceCard($"ActivateHoverStateIfCurrent blocked by !IsLoaded hoverEnterVersion={hoverEnterVersion}");
            _hoverEnterGuardActive = false;
            return;
        }

        TraceCard($"ActivateHoverStateIfCurrent applying hoverEnterVersion={hoverEnterVersion}");
        EnsureHoverChromeRealized();
        ApplyHoverState();

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());
        if (hasPreviewAudio && CanStartHoverPlayback())
        {
            TraceCard("ActivateHoverStateIfCurrent scheduling preview audio");
            _ = SchedulePreviewAudioAsync();
        }

        if (hasPreviewAudio && TrackPlayButton == null)
            QueueDeferredHoverStateRefresh();

        _hoverEnterGuardActive = false;
    }

    private void ApplyHoverState()
    {
        if (HoverChrome != null)
            HoverChrome.Visibility = Visibility.Visible;

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());

        if (TrackPlayButton != null)
            TrackPlayButton.Visibility = hasPreviewAudio ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewButtonVisualState();

        UpdatePlayingState();
        UpdatePreviewVisualState(hasPreviewAudio);

        var hasMultiplePreviewTracks = (Item?.PreviewTracks.Count ?? 0) > 1;
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;

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

        _ = StartCanvasPreviewAsync();
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        UpdateLastPointerWindowPosition(e);
        if (ShouldSuppressHoverExit(e))
        {
            TraceCard("PointerExited suppressed");
            _hoverEnterGuardActive = false;
            _isPointerOver = true;
            return;
        }
        TraceCard("PointerExited -> StopHoverMedia");
        StopHoverMedia();
    }

    private void Card_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        UpdateLastPointerWindowPosition(e);
        if (ShouldSuppressHoverExit(e))
        {
            TraceCard("PointerCanceled suppressed");
            _hoverEnterGuardActive = false;
            _isPointerOver = true;
            return;
        }
        TraceCard("PointerCanceled -> StopHoverMedia");
        StopHoverMedia();
    }

    private bool ShouldSuppressHoverExit(PointerRoutedEventArgs e)
    {
        if (_hoverEnterGuardActive)
            return true;

        try
        {
            var windowPoint = e.GetCurrentPoint(null).Position;
            return IsPointWithinCardBounds(windowPoint) ||
                   (_hasLastPointerWindowPosition && IsPointWithinCardBounds(_lastPointerWindowPosition));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[BaselineHomeCard] Hover exit suppression check failed: {ex.Message}");
            return false;
        }
    }

    private bool CanStartHoverPlayback()
    {
        if (!_isPointerOver || !IsLoaded)
            return false;

        return !_hasLastPointerWindowPosition || IsPointWithinCardBounds(_lastPointerWindowPosition);
    }

    private void UpdateLastPointerWindowPosition(PointerRoutedEventArgs e)
    {
        try
        {
            _lastPointerWindowPosition = e.GetCurrentPoint(null).Position;
            _hasLastPointerWindowPosition = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[BaselineHomeCard] Pointer position capture failed: {ex.Message}");
        }
    }

    private bool IsPointWithinCardBounds(Windows.Foundation.Point windowPoint)
    {
        try
        {
            if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0 || XamlRoot?.Content is not UIElement root)
                return false;

            var bounds = TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
            return windowPoint.X >= bounds.Left &&
                   windowPoint.X <= bounds.Right &&
                   windowPoint.Y >= bounds.Top &&
                   windowPoint.Y <= bounds.Bottom;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[BaselineHomeCard] Card bounds check failed: {ex.Message}");
            return false;
        }
    }

    private void StopHoverMedia(bool deferCanvasTeardown = true)
    {
        TraceCard($"StopHoverMedia deferCanvasTeardown={deferCanvasTeardown}");
        _hoverEnterGuardActive = false;
        _hoverEnterVersion++;
        var stopVersion = ++_hoverStopVersion;
        CancelPreviewTransition(resetMotionHosts: true);
        _isPointerOver = false;
        _isHoverStateRefreshQueued = false;
        _deferredHoverStateRefreshAttempts = 0;
        StopPreviewVisualization();
        StopPreviewAudio();

        if (TrackPlayButton != null)
            TrackPlayButton.Visibility = Visibility.Collapsed;
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

        UpdatePlayingState();

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
            _ = DeferredStopCanvasPreviewAsync(stopVersion);
        }
        else
        {
            StopCanvasPreview();
        }
    }

    // The MediaPlayerElement teardown (Source = null) unwinds MediaFoundation on the UI
    // thread and can visibly stall rapid hover-exit sweeps. Give the exit animations a
    // window to render, then drop to Low priority so the teardown only runs when the UI
    // thread is otherwise idle. If the user re-enters the card or a newer stop supersedes
    // us, the version check skips the teardown entirely.
    private async Task DeferredStopCanvasPreviewAsync(int stopVersion)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(180));
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (stopVersion != _hoverStopVersion || _isPointerOver)
                return;

            StopCanvasPreview();
        });
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
            if (hasPreviewAudio && TrackPlayButton == null)
                QueueDeferredHoverStateRefresh();
        }))
        {
            _isHoverStateRefreshQueued = false;
        }
    }

    private void ResetInteractionStateForNavigation()
    {
        _hoverEnterVersion++;
        CancelPreviewTransition(resetMotionHosts: true);
        _isPointerOver = false;
        _isHoverStateRefreshQueued = false;
        _deferredHoverStateRefreshAttempts = 0;

        StopCanvasPreview();
        StopPreviewVisualization();
        StopPreviewAudio();

        if (TrackPlayButton != null)
            TrackPlayButton.Visibility = Visibility.Collapsed;
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
            TraceCard("StartCanvasPreviewAsync skipped: no canvas url");
            StopCanvasPreview();
            return;
        }

        try
        {
            var previewVersion = ++_canvasPreviewVersion;
            TraceCard($"StartCanvasPreviewAsync begin previewVersion={previewVersion}");

            var isCanvasHostReady = await EnsureCanvasPreviewHostReadyAsync();
            if (CanvasPreviewHost == null || !isCanvasHostReady)
            {
                TraceCard($"StartCanvasPreviewAsync host not ready previewVersion={previewVersion}");
                if (_isPointerOver &&
                    previewVersion == _canvasPreviewVersion &&
                    string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
                {
                    _ = RetryCanvasPreviewInitializationAsync(previewVersion, canvasUrl);
                }

                return;
            }

            if (!_isPointerOver ||
                previewVersion != _canvasPreviewVersion ||
                !string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
                return;

            CanvasPreviewHost.Visibility = Visibility.Visible;
            CanvasPreviewHost.Opacity = 0;

            var isCanvasHostMeasured = await EnsureCanvasPreviewHostMeasuredAsync();
            if (CanvasPreviewHost == null || !isCanvasHostMeasured)
            {
                TraceCard($"StartCanvasPreviewAsync host not measured previewVersion={previewVersion}");
                if (_isPointerOver &&
                    previewVersion == _canvasPreviewVersion &&
                    string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
                {
                    _ = RetryCanvasPreviewInitializationAsync(previewVersion, canvasUrl);
                }

                return;
            }

            if (_sharedCanvasPreviewService == null)
                throw new InvalidOperationException("Shared canvas preview service is unavailable.");

            TraceCard($"StartCanvasPreviewAsync acquiring shared canvas preview previewVersion={previewVersion}");
            var lease = await _sharedCanvasPreviewService.AcquireAsync(CanvasPreviewHost, canvasUrl);
            if (lease == null)
            {
                TraceCard($"StartCanvasPreviewAsync acquire returned null previewVersion={previewVersion}");
                CanvasPreviewHost.Visibility = Visibility.Collapsed;
                CanvasPreviewHost.Opacity = 0;
                return;
            }

            if (!_isPointerOver ||
                previewVersion != _canvasPreviewVersion ||
                !string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal))
            {
                await _sharedCanvasPreviewService.ReleaseAsync(lease);
                if (CanvasPreviewHost != null)
                {
                    CanvasPreviewHost.Visibility = Visibility.Collapsed;
                    CanvasPreviewHost.Opacity = 0;
                }
                return;
            }

            _canvasPreviewLease = lease;
            _activeCanvasUrl = canvasUrl;
            TraceCard($"StartCanvasPreviewAsync acquired lease={lease.Id} previewVersion={previewVersion}");

            // Set opacity directly (skip animation for now) to verify rendering works
            CanvasPreviewHost.Opacity = 1;

            System.Diagnostics.Debug.WriteLine(
                $"[BaselineHomeCard] CanvasHost opacity={CanvasPreviewHost.Opacity} " +
                $"vis={CanvasPreviewHost.Visibility} " +
                $"size={CanvasPreviewHost.ActualWidth:F0}x{CanvasPreviewHost.ActualHeight:F0} " +
                $"children={CanvasPreviewHost.Children.Count}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Canvas preview failed: {ex.Message}");
            StopCanvasPreview();
        }
    }

    private async Task RetryCanvasPreviewInitializationAsync(int previewVersion, string canvasUrl)
    {
        TraceCard($"RetryCanvasPreviewInitializationAsync scheduled previewVersion={previewVersion}");
        await Task.Delay(90);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isPointerOver ||
                previewVersion != _canvasPreviewVersion ||
                !string.Equals(GetActiveCanvasUrl(), canvasUrl, StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(_activeCanvasUrl))
            {
                TraceCard($"RetryCanvasPreviewInitializationAsync aborted previewVersion={previewVersion}");
                return;
            }

            TraceCard($"RetryCanvasPreviewInitializationAsync restarting previewVersion={previewVersion}");
            _ = StartCanvasPreviewAsync();
        });
    }

    private async Task<bool> EnsureCanvasPreviewHostReadyAsync()
    {
        var host = CanvasPreviewHost;
        if (host == null)
            return false;

        if (host.IsLoaded)
            return true;

        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            host.Loaded -= loadedHandler;
            loaded.TrySetResult();
        };

        host.Loaded += loadedHandler;
        await Task.WhenAny(loaded.Task, Task.Delay(220));

        if (loadedHandler != null)
            host.Loaded -= loadedHandler;

        return host.IsLoaded;
    }

    private async Task<bool> EnsureCanvasPreviewHostMeasuredAsync()
    {
        var host = CanvasPreviewHost;
        if (host == null)
            return false;

        if (host.ActualWidth > 0 && host.ActualHeight > 0)
            return true;

        await WaitForNextUiTickAsync();
        host.UpdateLayout();

        if (host.ActualWidth > 0 && host.ActualHeight > 0)
            return true;

        var sized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SizeChangedEventHandler? sizeChangedHandler = null;
        sizeChangedHandler = (_, args) =>
        {
            if (args.NewSize.Width <= 0 || args.NewSize.Height <= 0)
                return;

            host.SizeChanged -= sizeChangedHandler;
            sized.TrySetResult();
        };

        host.SizeChanged += sizeChangedHandler;
        await Task.WhenAny(sized.Task, Task.Delay(150));

        if (sizeChangedHandler != null)
            host.SizeChanged -= sizeChangedHandler;

        return host.ActualWidth > 0 && host.ActualHeight > 0;
    }

    private Task WaitForNextUiTickAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => tcs.TrySetResult()))
            tcs.TrySetResult();

        return tcs.Task;
    }

    private void StopCanvasPreview()
    {
        TraceCard("StopCanvasPreview");
        _canvasPreviewVersion++;
        _activeCanvasUrl = null;
        var lease = _canvasPreviewLease;
        _canvasPreviewLease = null;

        if (CanvasPreviewHost != null)
        {
            CanvasPreviewHost.Visibility = Visibility.Collapsed;
            CanvasPreviewHost.Opacity = 0;
        }

        if (_sharedCanvasPreviewService != null)
        {
            if (lease != null)
                _ = _sharedCanvasPreviewService.ReleaseAsync(lease);
            else if (CanvasPreviewHost != null)
                _ = _sharedCanvasPreviewService.ReleaseHostAsync(CanvasPreviewHost);
        }
    }

    private void UpdatePreviewButtonVisualState()
    {
        if (TrackPlayButtonIcon != null)
            TrackPlayButtonIcon.Visibility = _isPreviewAudioPending ? Visibility.Collapsed : Visibility.Visible;

        if (PreviewPendingSpinner != null)
        {
            PreviewPendingSpinner.IsActive = _isPreviewAudioPending;
            PreviewPendingSpinner.Visibility = _isPreviewAudioPending ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdatePreviewVisualState(bool hasPreviewAudio)
    {
        var showPreviewVisualization = _isPointerOver && hasPreviewAudio && (_hasPreviewVisualization || _isPreviewAudioPending);
        if (PreviewOverlayRoot != null)
            PreviewOverlayRoot.Visibility = showPreviewVisualization ? Visibility.Visible : Visibility.Collapsed;

        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Visibility = showPreviewVisualization ? Visibility.Visible : Visibility.Collapsed;
            PreviewVisualizer.SetPending(showPreviewVisualization && _isPreviewAudioPending && !_isPreviewAudioPlaying);
            PreviewVisualizer.SetActive(showPreviewVisualization && _isPreviewAudioPlaying);
        }

        UpdatePreviewPendingProgressBarState();
    }

    private void UpdatePreviewPendingProgressBarState()
    {
        var showPendingProgress = _isPointerOver && _isPreviewAudioPending && !_isPreviewAudioPlaying;
        if (PreviewPendingProgressBar != null)
            PreviewPendingProgressBar.Visibility = showPendingProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearPreviewPendingVisualState()
    {
        _previewPendingVisualVersion++;
        StopPreviewPendingProgressBarAnimation(resetValue: true);
        if (_isPreviewAudioPending)
            _isPreviewAudioPending = false;

        UpdatePreviewButtonVisualState();
        UpdatePreviewVisualState(!string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl()));
    }

    private void QueuePreviewPendingVisualState()
    {
        var version = ++_previewPendingVisualVersion;
        _ = ShowPreviewPendingVisualStateAsync(version);
    }

    private async Task ShowPreviewPendingVisualStateAsync(int version)
    {
        await Task.Delay(PreviewPendingVisualDelay);

        if (version != _previewPendingVisualVersion || !_isPointerOver || _isPreviewAudioPlaying)
            return;

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());
        if (!hasPreviewAudio)
            return;

        _isPreviewAudioPending = true;
        UpdatePreviewButtonVisualState();
        UpdatePreviewVisualState(true);
        StartPreviewPendingProgressBarAnimation();
    }

    private void StartPreviewPendingProgressBarAnimation()
    {
        if (PreviewPendingProgressBar == null)
            return;

        StopPreviewPendingProgressBarAnimation(resetValue: true);

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 100,
            Duration = new Duration(PreviewPendingProgressDuration),
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, PreviewPendingProgressBar);
        Storyboard.SetTargetProperty(animation, "Value");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_previewPendingProgressStoryboard, storyboard))
                _previewPendingProgressStoryboard = null;
        };

        _previewPendingProgressStoryboard = storyboard;
        storyboard.Begin();
    }

    private void StopPreviewPendingProgressBarAnimation(bool resetValue)
    {
        if (_previewPendingProgressStoryboard != null)
        {
            _previewPendingProgressStoryboard.Stop();
            _previewPendingProgressStoryboard = null;
        }

        if (resetValue && PreviewPendingProgressBar != null)
            PreviewPendingProgressBar.Value = 0;
    }

    private void StartPreviewVisualization(bool hasLiveVisualization)
    {
        _hasPreviewVisualization = hasLiveVisualization;
        var previewUrl = GetActiveAudioPreviewUrl();
        if (!_isPointerOver || string.IsNullOrWhiteSpace(previewUrl) || !hasLiveVisualization)
        {
            UpdatePreviewVisualState(!string.IsNullOrWhiteSpace(previewUrl));
            return;
        }

        EnsurePreviewVisualizerRealized();
        ClearPreviewPendingVisualState();
        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Visibility = Visibility.Visible;
            PreviewVisualizer.Reset();
            PreviewVisualizer.SetPending(false);
            PreviewVisualizer.SetActive(true);
        }

        _previewVisualizationUrl = previewUrl;
        UpdatePreviewVisualState(true);
    }

    private void StopPreviewVisualization(bool preservePendingState = false)
    {
        _hasPreviewVisualization = false;
        _previewVisualizationSessionId = null;
        _previewVisualizationUrl = null;
        if (!preservePendingState)
            ClearPreviewPendingVisualState();

        if (PreviewVisualizer != null)
            PreviewVisualizer.Reset();

        UpdatePreviewVisualState(!string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl()));
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
            PreviewVisualizer.Complete();
            return;
        }

        PreviewVisualizer.PushLevels(frame.Amplitudes);
    }

    private void OnPreviewAudioCompleted()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_isPointerOver)
                return;

            await AutoAdvancePreviewAfterAudioEndedAsync();
        });
    }

    private async void TrackPlayButton_Click(object sender, RoutedEventArgs e)
    {
        var playback = _playbackService;
        var playbackState = _playbackStateService;
        if (playback == null) return;

        var item = Item;
        var track = GetActivePreviewTrack();
        if (item == null || string.IsNullOrEmpty(item.Uri)) return;

        StopPreviewAudio();

        try
        {
            SetPlaybackPending(true);
            playbackState?.NotifyBuffering(null);

            var options = !string.IsNullOrEmpty(track?.Uri)
                ? new PlayContextOptions { StartTrackUri = track.Uri }
                : null;
            var result = await Task.Run(async () => await playback.PlayContextAsync(item.Uri, options));
            if (!result.IsSuccess)
            {
                SetPlaybackPending(false);
                playbackState?.ClearBuffering();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetPlaybackPending(false);
            playbackState?.ClearBuffering();
            Debug.WriteLine($"[BaselineHomeCard] Track play failed: {ex.Message}");
        }
    }

    private async void ContextPlayButton_Click(object sender, RoutedEventArgs e)
    {
        var playback = _playbackService;
        var playbackState = _playbackStateService;
        if (playback == null) return;

        try
        {
            if (_isContextPlaying)
            {
                await Task.Run(async () => await playback.PauseAsync());
            }
            else if (_isContextPaused)
            {
                SetPlaybackPending(true);
                playbackState?.NotifyBuffering(null);
                var result = await Task.Run(async () => await playback.ResumeAsync());
                if (!result.IsSuccess)
                {
                    SetPlaybackPending(false);
                    playbackState?.ClearBuffering();
                }
            }
            else
            {
                var item = Item;
                if (item == null || string.IsNullOrEmpty(item.Uri)) return;

                StopPreviewAudio();

                SetPlaybackPending(true);
                playbackState?.NotifyBuffering(null);
                var result = await Task.Run(async () => await playback.PlayContextAsync(item.Uri));
                if (!result.IsSuccess)
                {
                    SetPlaybackPending(false);
                    playbackState?.ClearBuffering();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetPlaybackPending(false);
            playbackState?.ClearBuffering();
            Debug.WriteLine($"[BaselineHomeCard] Context play failed: {ex.Message}");
        }
    }

    private Task SchedulePreviewAudioAsync()
    {
        var request = CreatePreviewRequest();
        if (request == null || _previewPlaybackCoordinator == null)
        {
            TraceCard("SchedulePreviewAudioAsync skipped: no request or coordinator");
            return Task.CompletedTask;
        }

        TraceCard($"SchedulePreviewAudioAsync owner={request.OwnerId}");
        return _previewPlaybackCoordinator.ScheduleHover(request);
    }

    private Task StartPreviewAudioAsync()
    {
        var request = CreatePreviewRequest();
        if (request == null || _previewPlaybackCoordinator == null)
        {
            TraceCard("StartPreviewAudioAsync skipped: no request or coordinator");
            return Task.CompletedTask;
        }

        TraceCard($"StartPreviewAudioAsync owner={request.OwnerId}");
        return _previewPlaybackCoordinator.StartImmediate(request);
    }

    private CardPreviewRequest? CreatePreviewRequest()
    {
        var previewUrl = GetActiveAudioPreviewUrl();
        if (string.IsNullOrWhiteSpace(previewUrl))
            return null;

        return new CardPreviewRequest(
            _previewOwnerId,
            previewUrl,
            OnPreviewVisualizationFrame,
            OnPreviewPlaybackStateChanged,
            OnPreviewAudioCompleted,
            CanStartHoverPlayback);
    }

    private void OnPreviewPlaybackStateChanged(CardPreviewPlaybackState state)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnPreviewPlaybackStateChanged(state));
            return;
        }

        TraceCard(
            $"OnPreviewPlaybackStateChanged pending={state.IsPending} playing={state.IsPlaying} " +
            $"hasViz={state.HasVisualization} session='{state.SessionId ?? "<null>"}'");

        _isPreviewAudioPlaying = state.IsPlaying;
        _previewVisualizationSessionId = state.IsPlaying && state.HasVisualization ? state.SessionId : null;
        _previewVisualizationUrl = state.IsPlaying ? GetActiveAudioPreviewUrl() : null;

        if (state.IsPending)
            QueuePreviewPendingVisualState();
        else
            ClearPreviewPendingVisualState();

        if (state.IsPlaying && state.HasVisualization)
            StartPreviewVisualization(true);
        else
            StopPreviewVisualization(preservePendingState: state.IsPending);
    }

    private void StopPreviewAudio()
    {
        TraceCard("StopPreviewAudio");
        _isPreviewAudioPlaying = false;
        StopPreviewVisualization();

        if (_previewPlaybackCoordinator != null)
            _ = _previewPlaybackCoordinator.CancelOwner(_previewOwnerId);
    }

    private void UnregisterPreviewAudio()
    {
        _isPreviewAudioPlaying = false;
        StopPreviewVisualization();

        if (_previewPlaybackCoordinator != null)
            _ = _previewPlaybackCoordinator.UnregisterOwner(_previewOwnerId);
    }

    private async Task AutoAdvancePreviewAfterAudioEndedAsync()
    {
        var item = Item;
        if (!_isPointerOver || item == null || item.PreviewTracks.Count <= 1)
        {
            StopPreviewAudio();
            return;
        }

        await ChangePreviewTrackAsync(1);
    }

    // ── Now-playing highlight service ──

    private void OnHighlightServiceChanged(string? contextUri, string? albumUri, bool playing)
        => ApplyHighlight(contextUri, albumUri, playing);

    private void ApplyHighlight(string? contextUri, string? albumUri, bool playing)
    {
        var itemUri = Item?.Uri;
        // Match on either the playback context (e.g. user launched this playlist)
        // OR the currently-playing track's album URI (catches the case where the
        // track is from this album but was launched from a different context like
        // a playlist, search result, or radio).
        var isMatch = !string.IsNullOrEmpty(itemUri)
            && ((!string.IsNullOrEmpty(contextUri)
                 && string.Equals(itemUri, contextUri, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(albumUri)
                    && string.Equals(itemUri, albumUri, StringComparison.OrdinalIgnoreCase)));

        var newPlaying = isMatch && playing;
        var newPaused = isMatch && !playing;
        var shouldClearPending = _isPlaybackPending && (!isMatch || playing);
        if (newPlaying == _isContextPlaying && newPaused == _isContextPaused && !shouldClearPending)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            _isContextPlaying = newPlaying;
            _isContextPaused = newPaused;
            if (_isPlaybackPending && (!isMatch || playing))
                SetPlaybackPending(false);
            UpdatePlayingState();
        });
    }

    private void UpdatePlayingState()
    {
        var isActiveContext = _isContextPlaying || _isContextPaused;
        var isPending = _isPlaybackPending;
        var showPlayingIndicator = isActiveContext && !isPending;
        var showContextPlayButton = _isPointerOver || _isContextPaused || isPending || isActiveContext;

        if (showPlayingIndicator && PlayingIndicator == null)
            FindName(nameof(PlayingIndicator));

        // Defer ContextPlayButton realization to avoid layout disruption during
        // pointer-enter processing. FindName on x:Load elements can trigger a layout
        // pass that produces a phantom PointerExited, cancelling the preview start.
        if (showContextPlayButton && ContextPlayButton == null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isPointerOver || _isContextPlaying || _isContextPaused || _isPlaybackPending)
                {
                    EnsureContextPlayButtonRealized();
                    UpdatePlayingState();
                }
            });
            return;
        }

        if (PlayingIndicator != null)
            PlayingIndicator.Visibility = showPlayingIndicator ? Visibility.Visible : Visibility.Collapsed;
        if (PlayingEqualizer != null)
            PlayingEqualizer.IsActive = showPlayingIndicator && _isContextPlaying;

        if (ContextPlayButton != null)
        {
            if (isPending)
            {
                if (ContextPlayButtonIcon != null)
                    ContextPlayButtonIcon.Visibility = Visibility.Collapsed;
                if (ContextPlayButtonSpinner != null)
                {
                    ContextPlayButtonSpinner.Visibility = Visibility.Visible;
                    ContextPlayButtonSpinner.IsActive = true;
                }
                ContextPlayButton.Visibility = Visibility.Visible;
                ContextPlayButton.Opacity = 1;
            }
            else
            {
                if (ContextPlayButtonSpinner != null)
                {
                    ContextPlayButtonSpinner.IsActive = false;
                    ContextPlayButtonSpinner.Visibility = Visibility.Collapsed;
                }

                if (ContextPlayButtonIcon != null)
                {
                    ContextPlayButtonIcon.Glyph = _isContextPlaying ? "\uE769" : "\uE768";
                    ContextPlayButtonIcon.Visibility = Visibility.Visible;
                }

                ContextPlayButton.Visibility = showContextPlayButton ? Visibility.Visible : Visibility.Collapsed;
                if (ContextPlayButton.Visibility == Visibility.Visible)
                    ContextPlayButton.Opacity = 1;
            }
        }
    }

    private void SetPlaybackPending(bool pending)
    {
        if (_isPlaybackPending == pending) return;

        _isPlaybackPending = pending;
        _playbackPendingVersion++;
        if (pending)
        {
            EnsureContextPlayButtonRealized();
            StartPendingBeam();
            _ = ClearPlaybackPendingAfterTimeoutAsync(_playbackPendingVersion);
        }
        else
        {
            StopPendingBeam();
        }

        UpdatePlayingState();
    }

    private void EnsureContextPlayButtonRealized()
    {
        if (ContextPlayButton != null)
            return;
        _ = FindName(nameof(ContextPlayButton));
    }

    private void StartPendingBeam()
    {
        if (PendingBeam == null)
            FindName(nameof(PendingBeam));
        PendingBeam?.Start();
    }

    private void StopPendingBeam()
    {
        PendingBeam?.Stop();
    }

    private async Task ClearPlaybackPendingAfterTimeoutAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPlaybackPending && _playbackPendingVersion == version)
            {
                SetPlaybackPending(false);
                _playbackStateService?.ClearBuffering();
            }
        });
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
            if (ReferenceEquals(current, TrackPlayButton) ||
                ReferenceEquals(current, ContextPlayButton) ||
                ReferenceEquals(current, PreviousPreviewTrackButton) ||
                ReferenceEquals(current, NextPreviewTrackButton))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachRootPointerHandlers();
        SetSubscribedItem(_subscribedItem, null);

        if (_highlightService != null)
            _highlightService.CurrentChanged -= OnHighlightServiceChanged;

        StopPendingBeam();

        _hoverEnterVersion++;
        CancelPreviewTransition(resetMotionHosts: false);
        _isPointerOver = false;
        StopCanvasPreview();
        StopPreviewVisualization();
        UnregisterPreviewAudio();

        if (ReferenceEquals(s_activeCard, this))
            s_activeCard = null;
    }

    private void EnsureHoverChromeRealized()
    {
        if (HoverChrome != null)
            return;

        _ = FindName(nameof(HoverChrome));
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

            var shouldRestartPreviewAudio = ApplyPreviewTrackChange(delta, keepPreviewAudioPlaying: wasPreviewAudioPlaying);
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

    private bool ApplyPreviewTrackChange(int delta, bool keepPreviewAudioPlaying)
    {
        var item = Item;
        if (item == null || item.PreviewTracks.Count <= 1)
            return false;

        _previewTrackIndex = (_previewTrackIndex + delta + item.PreviewTracks.Count) % item.PreviewTracks.Count;
        var shouldRestartPreviewAudio =
            keepPreviewAudioPlaying &&
            !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl(item, GetActivePreviewTrack(item)));

        StopCanvasPreview();
        StopPreviewVisualization();
        if (!shouldRestartPreviewAudio)
            StopPreviewAudio();

        UpdateFromItem();
        return shouldRestartPreviewAudio;
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
