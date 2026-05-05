using System;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Core.Data;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Stores;
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

    // Cached to skip re-loading the bleed surface when the same URL comes through twice
    // (e.g. ApplyItem fires for unrelated reasons).
    private string? _currentBleedImageUrl;

    // Composition bleed: a SpriteVisual hosted on BleedImageArea renders the hero
    // bitmap via LoadedImageSurface + CompositionSurfaceBrush. Lives entirely outside
    // XAML measure so the bitmap's natural size can never drive up the card height.
    private SpriteVisual? _bleedVisual;
    private CompositionSurfaceBrush? _bleedBrush;
    private LoadedImageSurface? _bleedSurface;

    // Artist-only: when the item is an artist, subscribe to ArtistStore to fetch the
    // artist's HeaderImageUrl (reactive, shares cache with ArtistPage so clicking through
    // is instant). Disposed on item change or Unloaded.
    private ArtistStore? _artistStore;
    private IDisposable? _artistSubscription;
    private string? _observedArtistId;
    // Cached palette from the last Ready state, so ActualThemeChanged can rebuild the
    // gradient brush with the correct tier without refetching.
    private ArtistPalette? _currentPalette;

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
        // Rebuild the palette brush with the tier appropriate for the new theme
        // (dark → HigherContrast, light → HighContrast — same policy as ConcertPage).
        ApplyPaletteBrush();
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
            if (PlayActionContent != null)
                PlayActionContent.IdleGlyph = string.Empty;
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
        if (PlayActionContent != null)
        {
            PlayActionContent.IdleGlyph = item.GetActionGlyph();
            PlayActionContent.IsPlaying = false;
            PlayActionContent.IsPending = false;
        }

        var isArtist = item.Type == SearchResultType.Artist;
        _isTrack = item.Type == SearchResultType.Track;
        ApplyArtworkShape(isArtist);
        ApplyArtwork(item.ImageUrl, isArtist);

        // Hero background: resolve an artist (Artist itself, or the primary
        // artist of a Track/Album) via ArtistStore — shared cache with
        // ArtistPage — and apply HeaderImageUrl + ink overlay for readability.
        // Playlists have no artist context, so they fall back to the theme bg.
        var heroArtistUri = item.Type switch
        {
            SearchResultType.Artist => item.Uri,
            SearchResultType.Track or SearchResultType.Album => item.ArtistUris?.FirstOrDefault(),
            _ => null,
        };

        // Only wipe the existing bleed when the resolved artist actually
        // changes — otherwise re-typing the same query (or any path that
        // re-sets Item to the same artist) would clear the loaded image and
        // SubscribeArtist's same-uri early-return means no replay would refill
        // it.
        var sameArtist = string.Equals(_observedArtistId, heroArtistUri, StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(heroArtistUri))
        {
            if (!sameArtist)
            {
                LoadBleedImage(null);
                HeroInkOverlay.Visibility = Visibility.Collapsed;
            }
            SubscribeArtist(heroArtistUri);
        }
        else
        {
            DisposeArtistSubscription();
            LoadBleedImage(null);
            HeroInkOverlay.Visibility = Visibility.Collapsed;
        }

        _trackId = _isTrack ? ExtractId(item.Uri) : null;

        RefreshPlaybackState();
        UpdateOverlayState();
    }

    // ── Artist hero header (fetched via ArtistStore) ──

    private void SubscribeArtist(string? artistId)
    {
        if (string.Equals(_observedArtistId, artistId, StringComparison.Ordinal))
            return;

        DisposeArtistSubscription();
        _observedArtistId = artistId;
        if (string.IsNullOrEmpty(artistId)) return;

        _artistStore ??= Ioc.Default.GetService<ArtistStore>();
        if (_artistStore == null) return;

        _artistSubscription = _artistStore.Observe(artistId)
            .Subscribe(
                state => DispatcherQueue?.TryEnqueue(() => ApplyArtistState(state, artistId)),
                _ => { /* swallow — no hero bg is the graceful fallback */ });
    }

    private void ApplyArtistState(EntityState<ArtistOverviewResult> state, string expectedArtistId)
    {
        // Ignore late fetches for an artist the user has already moved past.
        if (!string.Equals(_observedArtistId, expectedArtistId, StringComparison.Ordinal))
            return;

        if (state is not EntityState<ArtistOverviewResult>.Ready ready)
            return;

        // Pick the best available hero backdrop. Prefer the editorial HeaderImageUrl;
        // fall back to the first gallery shot; finally the large avatar. The wash
        // (ink + palette gradient) reads well in all three cases.
        var bleedUrl = ready.Value.HeaderImageUrl
            ?? ready.Value.GalleryHeroUrl
            ?? ready.Value.ImageUrl;

        LoadBleedImage(bleedUrl);
        _currentPalette = ready.Value.Palette;

        // Show the ink wash whenever we have EITHER a bleed image or a palette —
        // even a palette-only artist still gets a tinted card instead of the
        // default chrome, which is closer to what the user asked for.
        var hasVisual = !string.IsNullOrEmpty(bleedUrl) || _currentPalette != null;
        HeroInkOverlay.Visibility = hasVisual ? Visibility.Visible : Visibility.Collapsed;

        ApplyPaletteBrush();
    }

    private void DisposeArtistSubscription()
    {
        _artistSubscription?.Dispose();
        _artistSubscription = null;
        _observedArtistId = null;
        _currentPalette = null;
        if (PaletteHeroOverlay != null) PaletteHeroOverlay.Background = null;
    }

    /// <summary>
    /// Applies a theme-aware palette-tinted gradient brush on top of the black ink
    /// overlay. Uses the same tier policy as ConcertViewModel.ApplyTheme:
    ///   dark theme  → HigherContrast (deepest saturated) for a rich backdrop
    ///   light theme → HighContrast   (saturated, one step brighter) so the wash
    ///                                 doesn't read as inky on a light app
    /// MinContrast is intentionally skipped — too pastel to read white text over.
    /// </summary>
    private void ApplyPaletteBrush()
    {
        if (PaletteHeroOverlay == null) return;

        var tier = _currentPalette is null
            ? null
            : (ActualTheme == ElementTheme.Dark
                ? (_currentPalette.HigherContrast ?? _currentPalette.HighContrast)
                : (_currentPalette.HighContrast ?? _currentPalette.HigherContrast));

        if (tier == null)
        {
            PaletteHeroOverlay.Background = null;
            return;
        }

        var bg = Windows.UI.Color.FromArgb(255, tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
        var bgTint = Windows.UI.Color.FromArgb(255, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);

        // Left → right fade: deep palette ink at the left where the title sits,
        // transparent on the right where the hero image should show through.
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(240, bgTint.R, bgTint.G, bgTint.B), Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(176, bg.R, bg.G, bg.B),           Offset = 0.35 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(80,  bg.R, bg.G, bg.B),           Offset = 0.65 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0,   bg.R, bg.G, bg.B),           Offset = 1.0 });
        PaletteHeroOverlay.Background = brush;
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

    /// <summary>
    /// Renders the bleed via a composition SpriteVisual hosted on BleedImageArea, so
    /// the bitmap's natural source dimensions never participate in XAML measure (the
    /// previous Image-based version blew up the card height when the bitmap loaded).
    /// </summary>
    private void LoadBleedImage(string? imageUrl)
    {
        if (string.Equals(_currentBleedImageUrl, imageUrl, StringComparison.Ordinal))
            return;
        _currentBleedImageUrl = imageUrl;

        if (string.IsNullOrEmpty(imageUrl))
        {
            ClearBleedSurface();
            return;
        }

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            ClearBleedSurface();
            return;
        }

        EnsureBleedVisual();

        // Dispose the previous brush + surface before swapping — both are
        // unmanaged. The old ones leaked GPU memory before this fix, which
        // accumulated as the user scrolled fresh top-results into the card.
        if (_bleedVisual != null) _bleedVisual.Brush = null;
        _bleedBrush?.Dispose();
        _bleedBrush = null;
        _bleedSurface?.Dispose();
        _bleedSurface = LoadedImageSurface.StartLoadFromUri(new Uri(httpsUrl));

        var compositor = ElementCompositionPreview.GetElementVisual(BleedSurfaceHost).Compositor;
        _bleedBrush = compositor.CreateSurfaceBrush(_bleedSurface);
        _bleedBrush.Stretch = CompositionStretch.UniformToFill;
        _bleedBrush.HorizontalAlignmentRatio = 0.5f;
        _bleedBrush.VerticalAlignmentRatio = 0.5f;

        if (_bleedVisual != null) _bleedVisual.Brush = _bleedBrush;
    }

    private void EnsureBleedVisual()
    {
        if (_bleedVisual != null) return;

        // Host on BleedSurfaceHost (an empty Border sibling of the overlays) rather
        // than BleedImageArea — composition children always render above the host's
        // XAML descendants, so attaching to BleedImageArea would cover HeroInkOverlay
        // and PaletteHeroOverlay. Hosting on a sibling lets normal XAML z-order put
        // the overlays back on top.
        var compositor = ElementCompositionPreview.GetElementVisual(BleedSurfaceHost).Compositor;
        _bleedVisual = compositor.CreateSpriteVisual();
        _bleedVisual.Size = new Vector2(
            (float)BleedSurfaceHost.ActualWidth,
            (float)BleedSurfaceHost.ActualHeight);

        ElementCompositionPreview.SetElementChildVisual(BleedSurfaceHost, _bleedVisual);
        BleedSurfaceHost.SizeChanged += BleedSurfaceHost_SizeChanged;
    }

    private void BleedSurfaceHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_bleedVisual == null) return;
        _bleedVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
    }

    private void ClearBleedSurface()
    {
        if (_bleedVisual != null) _bleedVisual.Brush = null;
        _bleedBrush?.Dispose();
        _bleedBrush = null;
        _bleedSurface?.Dispose();
        _bleedSurface = null;
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
            // Weak-reference messenger replaces the prior static event subscription
            // so missed Unloaded calls (recycled containers, suspended UCs) don't
            // leak this card via the static invocation list.
            WeakReferenceMessenger.Default.Register<SearchResultHeroCard, TrackStateRefreshMessage>(
                this, static (r, _) => r.OnPlaybackStateChanged());
            _subscribedToPlayback = true;
        }

        RefreshPlaybackState();
        UpdateOverlayState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedToPlayback)
        {
            WeakReferenceMessenger.Default.Unregister<TrackStateRefreshMessage>(this);
            _subscribedToPlayback = false;
        }

        NowPlayingEqualizer.IsActive = false;
        StopPendingBeam();
        DisposeArtistSubscription();

        // Composition resources are unmanaged — dispose to release the GPU/D3D handles.
        if (_bleedVisual != null)
        {
            BleedSurfaceHost.SizeChanged -= BleedSurfaceHost_SizeChanged;
            ElementCompositionPreview.SetElementChildVisual(BleedSurfaceHost, null);
            _bleedVisual.Dispose();
            _bleedVisual = null;
        }
        ClearBleedSurface();
        _currentBleedImageUrl = null;
    }

    private void OnPlaybackStateChanged()
    {
        // Same filter as TrackItem.OnPlaybackStateChanged — skip the dispatch
        // when this card's effective playback state can't have flipped. Note
        // the IsPaused condition mirrors RefreshPlaybackState exactly
        // (CurrentTrackId != null guard) so the dedup is sound.
        if (_trackId == null) return;

        var isThisTrack = _trackId == TrackStateBehavior.CurrentTrackId;
        var nowPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        var nowPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying
                                    && TrackStateBehavior.CurrentTrackId != null;
        var nowBuffering = _trackId == TrackStateBehavior.BufferingTrackId
                           && TrackStateBehavior.IsCurrentlyBuffering;

        if (nowPlaying == _isThisTrackPlaying
            && nowPaused == _isThisTrackPaused
            && nowBuffering == _isBuffering)
            return;

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
