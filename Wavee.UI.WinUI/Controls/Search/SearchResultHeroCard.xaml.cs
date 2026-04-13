using System;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Search;

public sealed partial class SearchResultHeroCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(
            nameof(Item),
            typeof(SearchResultItem),
            typeof(SearchResultHeroCard),
            new PropertyMetadata(null, OnItemChanged));

    public SearchResultItem? Item
    {
        get => (SearchResultItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>
    /// Extracted accent colour (hex, e.g. "#6F74A4") used as the solid background behind
    /// the hero card. Set by the host page after an IColorService lookup on the item's
    /// image. Null (the default) falls back to the theme card brush.
    /// </summary>
    public static readonly DependencyProperty ColorHexProperty =
        DependencyProperty.Register(
            nameof(ColorHex),
            typeof(string),
            typeof(SearchResultHeroCard),
            new PropertyMetadata(null, OnColorHexChanged));

    public string? ColorHex
    {
        get => (string?)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    private ImageCacheService? _imageCache;
    private IPlaybackStateService? _playbackStateService;
    private string? _trackId;
    private bool _isTrack;
    private bool _isHovered;
    private bool _isThisTrackPlaying;
    private bool _isThisTrackPaused;
    private bool _isBuffering;
    private bool _subscribedToPlayback;

    // Rich-background composition layer (see AlbumDetailPanel for the original pattern).
    private Compositor? _compositor;
    private LoadedImageSurface? _bleedImageSurface;
    private CompositionSurfaceBrush? _bleedSurfaceBrush;
    private SpriteVisual? _bleedSpriteVisual;
    private bool _bleedCompositionReady;
    private string? _pendingBleedImageUrl;
    private string? _currentBleedImageUrl;

    public SearchResultHeroCard()
    {
        InitializeComponent();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        Tapped += OnTapped;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_isTrack || _trackId == null) return;
        if (_trackId == TrackStateBehavior.CurrentTrackId) return;
        if (_trackId == TrackStateBehavior.BufferingTrackId) return;

        _playbackStateService ??= Ioc.Default.GetService<IPlaybackStateService>();
        _playbackStateService?.NotifyBuffering(_trackId);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleAccent();
        ApplyHoverBackground();
    }

    private void ApplyHoverBackground()
    {
        var key = _isHovered
            ? "CardBackgroundFillColorTertiaryBrush"
            : "CardBackgroundFillColorSecondaryBrush";
        RootBorder.Background = (Brush)Application.Current.Resources[key];
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SearchResultHeroCard)d).ApplyItem(e.NewValue as SearchResultItem);

    private void ApplyItem(SearchResultItem? item)
    {
        if (item == null)
        {
            TitleText.Text = string.Empty;
            SubtitleText.Inlines.Clear();
            TypeTagText.Text = string.Empty;
            ArtworkPlaceholderIcon.Glyph = string.Empty;
            PlayActionIcon.Glyph = string.Empty;
            ArtworkImage.Source = null;
            ArtistAvatar.ProfilePicture = null;
            _trackId = null;
            _isTrack = false;
            ApplyArtworkShape(isArtist: false);
            LoadBleedImage(null);
            RefreshPlaybackState();
            UpdateOverlayState();
            return;
        }

        TitleText.Text = item.Name;
        SearchSubtitleBuilder.Build(SubtitleText, item);
        TypeTagText.Text = item.GetTypeTag();
        ArtworkPlaceholderIcon.Glyph = item.GetPlaceholderGlyph();
        PlayActionIcon.Glyph = item.GetActionGlyph();

        var isArtist = item.Type == SearchResultType.Artist;
        _isTrack = item.Type == SearchResultType.Track;
        ApplyArtworkShape(isArtist);
        ApplyArtwork(item.ImageUrl, isArtist);
        LoadBleedImage(item.ImageUrl);

        _trackId = _isTrack ? ExtractId(item.Uri) : null;

        RefreshPlaybackState();
        UpdateOverlayState();
    }

    // ── Rich background (ColorHex + composition-masked artwork) ──

    private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SearchResultHeroCard)d;
        var hex = e.NewValue as string;

        if (string.IsNullOrEmpty(hex))
        {
            // Fall back to a transparent layer so the underlying RootBorder theme
            // brush (set by ApplyHoverBackground) shows through.
            card.ColorBackground.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            return;
        }

        var color = ParseHexColor(hex);
        card.ColorBackground.Background = new SolidColorBrush(color);
    }

    private static Windows.UI.Color ParseHexColor(string hex)
    {
        // Same implementation as AlbumDetailPanel.ParseHexColor — accepts RGB or ARGB.
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }
        if (hex.Length == 8)
        {
            return Windows.UI.Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16));
        }
        return Windows.UI.Color.FromArgb(255, 30, 30, 35);
    }

    private void SetupBleedComposition()
    {
        if (_bleedCompositionReady) return;

        var visual = ElementCompositionPreview.GetElementVisual(BleedImageArea);
        _compositor = visual.Compositor;

        // Horizontal gradient mask: transparent on the left (where title/metadata sits)
        // → opaque on the right (image visible). Matches AlbumDetailPanel.SetupCompositionMask.
        var gradientBrush = _compositor.CreateLinearGradientBrush();
        gradientBrush.StartPoint = new Vector2(0f, 0.5f);
        gradientBrush.EndPoint = new Vector2(1f, 0.5f);
        gradientBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f,
            Windows.UI.Color.FromArgb(0, 255, 255, 255)));
        gradientBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.5f,
            Windows.UI.Color.FromArgb(255, 255, 255, 255)));

        _bleedSurfaceBrush = _compositor.CreateSurfaceBrush();
        _bleedSurfaceBrush.Stretch = CompositionStretch.UniformToFill;
        _bleedSurfaceBrush.HorizontalAlignmentRatio = 1f; // right-aligned crop
        _bleedSurfaceBrush.VerticalAlignmentRatio = 0.5f;

        var maskBrush = _compositor.CreateMaskBrush();
        maskBrush.Source = _bleedSurfaceBrush;
        maskBrush.Mask = gradientBrush;

        _bleedSpriteVisual = _compositor.CreateSpriteVisual();
        _bleedSpriteVisual.Brush = maskBrush;
        _bleedSpriteVisual.RelativeSizeAdjustment = Vector2.One;

        ElementCompositionPreview.SetElementChildVisual(BleedImageArea, _bleedSpriteVisual);
        _bleedCompositionReady = true;

        // If an item was assigned before Loaded fired, apply its image now.
        if (_pendingBleedImageUrl != null)
        {
            var pending = _pendingBleedImageUrl;
            _pendingBleedImageUrl = null;
            LoadBleedImage(pending);
        }
    }

    private void LoadBleedImage(string? imageUrl)
    {
        if (!_bleedCompositionReady || _bleedSurfaceBrush == null)
        {
            // Composition not yet ready — queue for when SetupBleedComposition runs.
            _pendingBleedImageUrl = imageUrl;
            return;
        }

        // Same URL → no-op. Avoids thrashing when ApplyItem fires for unrelated reasons.
        if (string.Equals(_currentBleedImageUrl, imageUrl, StringComparison.Ordinal))
            return;
        _currentBleedImageUrl = imageUrl;

        _bleedImageSurface?.Dispose();
        _bleedImageSurface = null;

        if (string.IsNullOrEmpty(imageUrl))
        {
            _bleedSurfaceBrush.Surface = null;
            return;
        }

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl)) return;

        // Use a fixed reasonable size — BleedImageArea.ActualWidth/Height may still be 0
        // early in the layout pass. 640×640 matches AlbumDetailPanel's fallback.
        var desiredSize = new Windows.Foundation.Size(640, 640);
        _bleedImageSurface = LoadedImageSurface.StartLoadFromUri(new Uri(httpsUrl), desiredSize);
        _bleedSurfaceBrush.Surface = _bleedImageSurface;
    }

    private void DisposeBleedComposition()
    {
        _bleedImageSurface?.Dispose();
        _bleedImageSurface = null;

        if (_bleedSurfaceBrush != null)
        {
            _bleedSurfaceBrush.Surface = null;
            _bleedSurfaceBrush.Dispose();
            _bleedSurfaceBrush = null;
        }

        if (_bleedSpriteVisual != null)
        {
            ElementCompositionPreview.SetElementChildVisual(BleedImageArea, null);
            _bleedSpriteVisual.Brush?.Dispose();
            _bleedSpriteVisual.Dispose();
            _bleedSpriteVisual = null;
        }

        _compositor = null;
        _bleedCompositionReady = false;
        _currentBleedImageUrl = null;
        _pendingBleedImageUrl = null;
    }

    private void ApplyArtworkShape(bool isArtist)
    {
        if (isArtist)
        {
            ArtworkClip.Visibility = Visibility.Collapsed;
            ArtistAvatar.Visibility = Visibility.Visible;
        }
        else
        {
            ArtistAvatar.Visibility = Visibility.Collapsed;
            ArtworkClip.Visibility = Visibility.Visible;
        }
    }

    private void ApplyArtwork(string? imageUrl, bool isArtist)
    {
        ArtworkImage.Source = null;
        ArtistAvatar.ProfilePicture = null;

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkPlaceholderIcon.Visibility = Visibility.Visible;
            return;
        }

        _imageCache ??= Ioc.Default.GetService<ImageCacheService>();
        var source = _imageCache?.GetOrCreate(httpsUrl, 160);

        if (isArtist)
        {
            ArtistAvatar.ProfilePicture = source as BitmapImage;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkPlaceholderIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtworkImage.Source = source;
            ArtworkImage.Visibility = Visibility.Visible;
            ArtworkPlaceholderIcon.Visibility = Visibility.Collapsed;
        }
    }

    private static string? ExtractId(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var idx = uri.LastIndexOf(':');
        return idx >= 0 && idx < uri.Length - 1 ? uri[(idx + 1)..] : uri;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribedToPlayback)
        {
            TrackStateBehavior.EnsurePlaybackSubscription();
            TrackStateBehavior.PlaybackStateChanged += OnPlaybackStateChanged;
            _subscribedToPlayback = true;
        }

        SetupBleedComposition();

        RefreshPlaybackState();
        UpdateOverlayState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedToPlayback)
        {
            TrackStateBehavior.PlaybackStateChanged -= OnPlaybackStateChanged;
            _subscribedToPlayback = false;
        }

        NowPlayingEqualizer.IsActive = false;
        StopPendingBeam();
        DisposeBleedComposition();
    }

    private void OnPlaybackStateChanged()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            RefreshPlaybackState();
            UpdateOverlayState();
        });
    }

    private void RefreshPlaybackState()
    {
        if (_trackId == null)
        {
            _isThisTrackPlaying = false;
            _isThisTrackPaused = false;
            _isBuffering = false;
            ApplyTitleAccent();
            return;
        }

        var isThisTrack = _trackId == TrackStateBehavior.CurrentTrackId;
        _isThisTrackPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        _isThisTrackPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying && TrackStateBehavior.CurrentTrackId != null;
        _isBuffering = _trackId == TrackStateBehavior.BufferingTrackId
                       && TrackStateBehavior.IsCurrentlyBuffering;

        ApplyTitleAccent();
    }

    private void ApplyTitleAccent()
    {
        var highlight = _isThisTrackPlaying || _isThisTrackPaused || _isBuffering;
        TitleText.Foreground = highlight
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    private void UpdateOverlayState()
    {
        if (_isBuffering)
        {
            NowPlayingOverlay.Visibility = Visibility.Collapsed;
            NowPlayingEqualizer.IsActive = false;
            BufferingRing.IsActive = true;
            BufferingRing.Visibility = Visibility.Visible;
            StartPendingBeam();
            return;
        }

        StopPendingBeam();
        BufferingRing.IsActive = false;
        BufferingRing.Visibility = Visibility.Collapsed;

        if (_isThisTrackPlaying)
        {
            NowPlayingOverlay.Visibility = Visibility.Visible;
            NowPlayingOverlay.Opacity = 1.0;
            NowPlayingEqualizer.IsActive = true;
        }
        else if (_isThisTrackPaused)
        {
            NowPlayingOverlay.Visibility = Visibility.Visible;
            NowPlayingOverlay.Opacity = 0.7;
            NowPlayingEqualizer.IsActive = false;
        }
        else
        {
            NowPlayingOverlay.Visibility = Visibility.Collapsed;
            NowPlayingEqualizer.IsActive = false;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        ApplyHoverBackground();
        AnimationBuilder.Create()
            .Scale(to: new System.Numerics.Vector2(1.04f, 1.04f), duration: TimeSpan.FromMilliseconds(180))
            .Start(PlayActionButton);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        ApplyHoverBackground();
        AnimationBuilder.Create()
            .Scale(to: new System.Numerics.Vector2(1.0f, 1.0f), duration: TimeSpan.FromMilliseconds(180))
            .Start(PlayActionButton);
    }

    private void StartPendingBeam()
    {
        if (PlaybackPendingBeam == null)
            FindName(nameof(PlaybackPendingBeam));
        PlaybackPendingBeam?.Start();
    }

    private void StopPendingBeam()
    {
        PlaybackPendingBeam?.Stop();
    }
}
