using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI;
using System.Diagnostics;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Specialised home-shelf card with hover-driven preview audio, canvas video
/// preview, multi-preview-track navigation, and now-playing highlight chrome.
/// Hosted in virtualised <c>ItemsRepeater</c> instances, so per-card sub-
/// UserControls are deliberately avoided — the class is split into partials
/// and a small set of attached behaviors instead.
///
/// <para>This file owns the surface that touches multiple concerns:
/// construction, the DP, Loaded/Unloaded, the <see cref="HomeSectionItem"/>
/// subscription, image / colour / loading state, top-level pointer routing
/// (Tap / PointerPressed / RightTapped), the active-card transfer logic
/// and the colour helpers.</para>
///
/// <para>Partial files (next to this one):</para>
/// <list type="bullet">
///   <item><c>BaselineHomeCard.Hover.cs</c> — hover state machine, root
///   pointer re-routing, hover-exit suppression, scale + chrome animations,
///   deferred-stop scheduling.</item>
///   <item><c>BaselineHomeCard.CanvasPreview.cs</c> — canvas video preview
///   lifecycle: host realization, ready/measured gating, shared-service
///   lease acquire / release, retry.</item>
///   <item><c>BaselineHomeCard.PreviewAudio.cs</c> — preview audio coordinator
///   plumbing, pending visual state machine (progress bar animation),
///   visualiser pushing, auto-advance after audio end, track-play button.</item>
///   <item><c>BaselineHomeCard.PreviewNavigation.cs</c> — multi-preview-track
///   nav: prev/next button handlers, queued-delta transition state machine,
///   in/out animations on the motion hosts, accessors for the active preview
///   track / audio URL / canvas URL / hero URL.</item>
///   <item><c>BaselineHomeCard.PlaybackHighlight.cs</c> — context play/pause
///   visual state, <see cref="NowPlayingHighlightService"/> subscription,
///   context-play button click + pending-beam.</item>
///   <item><c>BaselineHomeCard.LazyRealization.cs</c> — the small
///   <c>Ensure*Realized</c> helpers that drive <c>x:Load="False"</c>
///   subtree realization on demand.</item>
/// </list>
/// </summary>
public sealed partial class BaselineHomeCard : UserControl
{
    private const int HeroImageDecodeSize = 240;
    private const int ThumbImageDecodeSize = 96;

    private static BaselineHomeCard? s_activeCard;

    private readonly ICardPreviewPlaybackCoordinator? _previewPlaybackCoordinator;
    private readonly ISharedCardCanvasPreviewService? _sharedCanvasPreviewService;
    private readonly IPlaybackService? _playbackService;
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly NowPlayingHighlightService? _highlightService;
    private readonly Guid _previewOwnerId = Guid.NewGuid();

    private HomeSectionItem? _subscribedItem;
    private string? _currentHeroImageUrl;
    private string? _currentThumbImageUrl;
    private string? _heroRetryAttemptedUrl;
    private string? _thumbRetryAttemptedUrl;

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
        Unloaded += OnUnloaded;
        _previewPlaybackCoordinator = Ioc.Default.GetService<ICardPreviewPlaybackCoordinator>();
        _sharedCanvasPreviewService = Ioc.Default.GetService<ISharedCardCanvasPreviewService>();
        _playbackService = Ioc.Default.GetService<IPlaybackService>();
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        _highlightService = Ioc.Default.GetService<NowPlayingHighlightService>();
        InitializeComponent();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Do NOT clear HeroImage / CoverThumbImage ImageUrl here.
        // CompositionImage releases its own pin on Unloaded. Clearing the URL
        // breaks scroll-back-up: the outer DP doesn't refire if the same
        // DataContext is restored, and the inner ImageUrl stays null = blank.
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

        // A virtualized card can unload/reload with the same Item instance, so
        // the Item DP does not refire. Re-apply the current URLs on Loaded so
        // CompositionImage gets a fresh chance to attach/reload after fast
        // scroll recycling.
        UpdateFromItem();

        if (_isPointerOver)
        {
            TraceCard("Loaded while pointer already over card; re-queueing hover activation");
            _hoverEnterGuardActive = true;
            QueueHoverEnterActivation(_hoverEnterVersion);
        }
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

        // Don't clear HeroImage / CoverThumbImage ImageUrl — CompositionImage
        // releases its own pin on Unloaded. Clearing breaks scroll-back-up.
        // Reset our local cache markers so LoadImages re-applies the URL on
        // re-attach even when the outer Item DP is unchanged.
        _currentHeroImageUrl = null;
        _currentThumbImageUrl = null;

        if (ReferenceEquals(s_activeCard, this))
            s_activeCard = null;
    }

    private void SetSubscribedItem(HomeSectionItem? oldItem, HomeSectionItem? newItem)
    {
        if (oldItem != null)
            oldItem.PropertyChanged -= Item_PropertyChanged;

        // Item swap on a virtualized card = the previous item is leaving this
        // container. Card_Unloaded does NOT fire on ListView recycling — the
        // visual tree stays realized, just rebound to a new DataContext. If
        // we don't stop the canvas preview + audio here, the OLD item's
        // MediaSource stays pinned (each active preview is ~3.6 MB of
        // decoded video frame) until the container is finally evicted.
        if (oldItem != null && !ReferenceEquals(oldItem, newItem))
        {
            StopCanvasPreview();
            StopPreviewVisualization();
            UnregisterPreviewAudio();
            _isPointerOver = false;
            _hoverEnterVersion++;
            CancelPreviewTransition(resetMotionHosts: false);
        }

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
        if (_isPointerOver && hasMultiplePreviewTracks)
            EnsurePreviewNavigationButtonsRealized();
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
            activePreviewTrack?.CoverArtUrl ?? item.BestMediumImageUrl);
        ApplyColor(activePreviewTrack?.ColorHex ?? item.HeroColorHex ?? item.ColorHex);
        UpdateLoadingState(item.IsBaselineLoading);
    }

    private void LoadImages(string? heroUrl, string? thumbUrl)
    {
        var heroHttpsUrl = ResolveImageUrl(heroUrl);
        if (string.IsNullOrWhiteSpace(heroHttpsUrl))
        {
            _currentHeroImageUrl = null;
            HeroImage.ImageUrl = null;
        }
        else if (!string.Equals(_currentHeroImageUrl, heroHttpsUrl, StringComparison.Ordinal))
        {
            _currentHeroImageUrl = heroHttpsUrl;
            HeroImage.ImageUrl = heroHttpsUrl;
        }
        else if (!HeroImage.IsImageLoaded)
        {
            HeroImage.RefreshCurrentImage();
        }

        var thumbHttpsUrl = ResolveImageUrl(thumbUrl);
        if (string.IsNullOrWhiteSpace(thumbHttpsUrl))
        {
            _currentThumbImageUrl = null;
            CoverThumbImage.ImageUrl = null;
            CoverThumbPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        if (!string.Equals(_currentThumbImageUrl, thumbHttpsUrl, StringComparison.Ordinal))
        {
            _currentThumbImageUrl = thumbHttpsUrl;
            CoverThumbImage.ImageUrl = thumbHttpsUrl;
        }
        else if (!CoverThumbImage.IsImageLoaded)
        {
            CoverThumbImage.RefreshCurrentImage();
        }

        CoverThumbPlaceholder.Visibility = CoverThumbImage.IsImageLoaded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string? ResolveImageUrl(string? url)
    {
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url);
        if (!string.IsNullOrEmpty(httpsUrl))
            return httpsUrl;

        // Baseline Home cards need a cheap fallback for playlist / mix mosaic
        // descriptors. The full composed mosaic belongs to playlist detail
        // surfaces; here a single tile is better than a permanent placeholder.
        return SpotifyImageHelper.TryParseMosaicTileUrls(url, out var tileUrls) && tileUrls.Count > 0
            ? tileUrls[0]
            : null;
    }

    private void HeroImage_ImageOpened(object? sender, EventArgs e)
        => _heroRetryAttemptedUrl = null;

    private void HeroImage_ImageFailed(object? sender, EventArgs e)
    {
        var failedUrl = _currentHeroImageUrl;
        _currentHeroImageUrl = null;
        RetryCurrentItemImageOnce(failedUrl, isHero: true);
    }

    private void CoverThumbImage_ImageOpened(object? sender, EventArgs e)
    {
        _thumbRetryAttemptedUrl = null;
        CoverThumbPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void CoverThumbImage_ImageFailed(object? sender, EventArgs e)
    {
        CoverThumbPlaceholder.Visibility = Visibility.Visible;
        var failedUrl = _currentThumbImageUrl;
        _currentThumbImageUrl = null;
        RetryCurrentItemImageOnce(failedUrl, isHero: false);
    }

    private void RetryCurrentItemImageOnce(string? failedUrl, bool isHero)
    {
        if (string.IsNullOrEmpty(failedUrl))
            return;

        if (isHero)
        {
            if (string.Equals(_heroRetryAttemptedUrl, failedUrl, StringComparison.Ordinal))
                return;
            _heroRetryAttemptedUrl = failedUrl;
        }
        else
        {
            if (string.Equals(_thumbRetryAttemptedUrl, failedUrl, StringComparison.Ordinal))
                return;
            _thumbRetryAttemptedUrl = failedUrl;
        }

        DispatcherQueue.TryEnqueue(UpdateFromItem);
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

    // ── Top-level click / pointer routing ────────────────────────────────────

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

        Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.RecordClickIntent("BaselineHomeCard");
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
            Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.RecordClickIntent("BaselineHomeCard.MiddleClick");
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

        var items = Controls.ContextMenu.Builders.CardContextMenuBuilder.Build(new Controls.ContextMenu.Builders.CardMenuContext
        {
            Uri = item.Uri ?? string.Empty,
            Title = item.Title ?? string.Empty,
            Subtitle = item.Subtitle,
            ImageUrl = item.ImageUrl,
            OpenAction = openInNewTab =>
            {
                ResetInteractionStateForNavigation();
                HomeViewModel.NavigateToItem(item, openInNewTab);
            }
        });
        Controls.ContextMenu.ContextMenuHost.Show(this, items, e.GetPosition(this));
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

        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.Scale = System.Numerics.Vector3.One;
        Canvas.SetZIndex(this, 0);

        if (ReferenceEquals(s_activeCard, this))
            s_activeCard = null;
    }

    // ── Static colour helpers ────────────────────────────────────────────────

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
