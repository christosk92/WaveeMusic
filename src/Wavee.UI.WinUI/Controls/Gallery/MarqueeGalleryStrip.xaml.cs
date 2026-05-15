using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Controls.Imaging;
using Wavee.UI.WinUI.Helpers;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Gallery;

/// <summary>
/// Compact replacement for the 420 px Klankhuis HeroCarousel gallery on
/// ArtistPage. Renders an infinitely auto-scrolling horizontal rail of square
/// image tiles. The source list is emitted in N back-to-back copies so the
/// rail's Composition.Offset.X can run from 0 to -(singleSetWidth) with
/// <c>IterationBehavior=Forever</c> and the wrap is invisible. Hover pauses
/// the animation; tap on a tile raises <see cref="ItemTapped"/> with the
/// original-list index so the caller can drive the existing lightbox.
/// </summary>
public sealed partial class MarqueeGalleryStrip : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IList<string>), typeof(MarqueeGalleryStrip),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty ItemSizeProperty =
        DependencyProperty.Register(nameof(ItemSize), typeof(double), typeof(MarqueeGalleryStrip),
            new PropertyMetadata(160.0, OnLayoutMetricChanged));

    public static readonly DependencyProperty ItemSpacingProperty =
        DependencyProperty.Register(nameof(ItemSpacing), typeof(double), typeof(MarqueeGalleryStrip),
            new PropertyMetadata(12.0, OnLayoutMetricChanged));

    public static readonly DependencyProperty ItemCornerRadiusProperty =
        DependencyProperty.Register(nameof(ItemCornerRadius), typeof(CornerRadius), typeof(MarqueeGalleryStrip),
            new PropertyMetadata(new CornerRadius(14), OnItemCornerRadiusChanged));

    public static readonly DependencyProperty SpeedPxPerSecProperty =
        DependencyProperty.Register(nameof(SpeedPxPerSec), typeof(double), typeof(MarqueeGalleryStrip),
            new PropertyMetadata(28.0, OnSpeedChanged));

    public static readonly DependencyProperty EdgeFadeWidthProperty =
        DependencyProperty.Register(nameof(EdgeFadeWidth), typeof(double), typeof(MarqueeGalleryStrip),
            new PropertyMetadata(64.0, OnEdgeFadeWidthChanged));

    public static readonly DependencyProperty HaloIntensityProperty =
        DependencyProperty.Register(nameof(HaloIntensity), typeof(double), typeof(MarqueeGalleryStrip),
            new PropertyMetadata(0.35, OnHaloChanged));

    public static readonly DependencyProperty FadeBackgroundProperty =
        DependencyProperty.Register(nameof(FadeBackground), typeof(Brush), typeof(MarqueeGalleryStrip),
            new PropertyMetadata(null, OnFadeBackgroundChanged));

    public IList<string>? ItemsSource
    {
        get => (IList<string>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public double ItemSize
    {
        get => (double)GetValue(ItemSizeProperty);
        set => SetValue(ItemSizeProperty, value);
    }

    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public CornerRadius ItemCornerRadius
    {
        get => (CornerRadius)GetValue(ItemCornerRadiusProperty);
        set => SetValue(ItemCornerRadiusProperty, value);
    }

    public double SpeedPxPerSec
    {
        get => (double)GetValue(SpeedPxPerSecProperty);
        set => SetValue(SpeedPxPerSecProperty, value);
    }

    public double EdgeFadeWidth
    {
        get => (double)GetValue(EdgeFadeWidthProperty);
        set => SetValue(EdgeFadeWidthProperty, value);
    }

    public double HaloIntensity
    {
        get => (double)GetValue(HaloIntensityProperty);
        set => SetValue(HaloIntensityProperty, value);
    }

    /// <summary>
    /// The fade gradient end-stop. Defaults to <c>ApplicationPageBackgroundThemeBrush</c>
    /// when null. Set this when the strip sits over a non-default backdrop
    /// (e.g. an artist hero blend) so the fade-out blends into the actual
    /// surface behind the tiles, not the abstract page background.
    /// </summary>
    public Brush? FadeBackground
    {
        get => (Brush?)GetValue(FadeBackgroundProperty);
        set => SetValue(FadeBackgroundProperty, value);
    }

    public event TypedEventHandler<MarqueeGalleryStrip, GalleryItemTappedEventArgs>? ItemTapped;

    private Visual? _railVisual;
    private double _singleSetWidth;
    private bool _isHostHovered;
    private bool _isAnimating;

    public MarqueeGalleryStrip()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureRailVisual();
        RefreshFadeBrushes();
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
        _railVisual = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClipGeometry.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        // No Rebuild here — Rebuild's initial copy calculation uses a
        // conservative 2400 px host default so typical window resizes don't
        // expose uncovered gaps. Resizing past that ceiling will need a
        // manual refresh (or a follow-up that rebuilds when host grows past
        // currentCopies × setWidth).
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => RefreshFadeBrushes();

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (MarqueeGalleryStrip)d;
        if (e.OldValue is INotifyCollectionChanged oldNcc)
            oldNcc.CollectionChanged -= self.OnItemsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNcc)
            newNcc.CollectionChanged += self.OnItemsCollectionChanged;
        self.Rebuild();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private static void OnLayoutMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MarqueeGalleryStrip)d).Rebuild();

    private static void OnItemCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (MarqueeGalleryStrip)d;
        foreach (var child in self.Rail.Children)
        {
            if (child is Border b) b.CornerRadius = (CornerRadius)e.NewValue;
        }
    }

    private static void OnSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (MarqueeGalleryStrip)d;
        if (self._isAnimating) self.StartAnimation();
    }

    private static void OnEdgeFadeWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (MarqueeGalleryStrip)d;
        self.LeftFade.Width = (double)e.NewValue;
        self.RightFade.Width = (double)e.NewValue;
    }

    private static void OnHaloChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MarqueeGalleryStrip)d).ApplyHalo();

    private static void OnFadeBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MarqueeGalleryStrip)d).RefreshFadeBrushes();

    private void EnsureRailVisual()
    {
        if (_railVisual is not null) return;
        try
        {
            _railVisual = ElementCompositionPreview.GetElementVisual(Rail);
        }
        catch
        {
            _railVisual = null;
        }
    }

    private void Rebuild()
    {
        Rail.Children.Clear();
        StopAnimation();

        var source = ItemsSource;
        if (source is null || source.Count == 0)
        {
            _singleSetWidth = 0;
            return;
        }

        // Pre-filter to HTTPS-normalised URLs so each tile is paint-ready and
        // SpotifyImageHelper isn't re-invoked on every layout pass.
        var urls = new List<string>(source.Count);
        foreach (var raw in source)
        {
            var https = SpotifyImageHelper.ToHttpsUrl(raw);
            if (!string.IsNullOrEmpty(https)) urls.Add(https);
        }
        if (urls.Count == 0)
        {
            _singleSetWidth = 0;
            return;
        }

        var itemSize = ItemSize;
        var spacing = ItemSpacing;
        var setWidth = urls.Count * (itemSize + spacing);
        _singleSetWidth = setWidth;

        // Need enough duplicates to ensure totalWidth >= setWidth + hostWidth
        // so the visible region is always covered while offset wraps. Use a
        // safe default host width before layout completes.
        var hostWidth = ActualWidth > 0 ? ActualWidth : 2400.0;
        var requiredTotal = setWidth + Math.Max(hostWidth, 1600.0);
        var copies = Math.Max(2, (int)Math.Ceiling(requiredTotal / setWidth));
        // Cap so a one-photo artist can't blow up to hundreds of tiles. All
        // duplicates share a single GPU surface in ImageCacheService anyway.
        if (copies > 24) copies = 24;

        Rail.Spacing = spacing;

        for (var copy = 0; copy < copies; copy++)
        {
            for (var i = 0; i < urls.Count; i++)
            {
                var tile = CreateTile(urls[i], i);
                Rail.Children.Add(tile);
            }
        }

        ApplyHalo();
        StartAnimation();
    }

    private Border CreateTile(string url, int originalIndex)
    {
        var image = new CompositionImage
        {
            ImageUrl = url,
            DecodePixelSize = (int)Math.Ceiling(ItemSize * 2),
            Stretch = Stretch.UniformToFill,
            CornerRadius = ItemCornerRadius,
            Width = ItemSize,
            Height = ItemSize,
        };

        var tile = new Border
        {
            Width = ItemSize,
            Height = ItemSize,
            CornerRadius = ItemCornerRadius,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            Tag = originalIndex,
            Child = image,
        };
        tile.Tapped += OnTileTapped;
        return tile;
    }

    private void OnTileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border b || b.Tag is not int originalIndex) return;
        var source = ItemsSource;
        if (source is null || originalIndex < 0 || originalIndex >= source.Count) return;
        var url = source[originalIndex] ?? string.Empty;
        ItemTapped?.Invoke(this, new GalleryItemTappedEventArgs
        {
            Index = originalIndex,
            ImageUrl = url,
        });
    }

    private void StartAnimation()
    {
        EnsureRailVisual();
        if (_railVisual is null || _singleSetWidth <= 0) return;

        try
        {
            var compositor = _railVisual.Compositor;
            var speed = Math.Max(1, SpeedPxPerSec);
            var durationSec = Math.Clamp(_singleSetWidth / speed, 6, 600);

            // Linear easing so the wrap is visually continuous — any other
            // curve would jolt perceptibly at every cycle boundary.
            var linear = compositor.CreateLinearEasingFunction();
            var anim = compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(0f, new Vector3(0, 0, 0), linear);
            anim.InsertKeyFrame(1f, new Vector3(-(float)_singleSetWidth, 0, 0), linear);
            anim.Duration = TimeSpan.FromSeconds(durationSec);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;

            _railVisual.StartAnimation("Offset", anim);
            _isAnimating = true;
            if (_isHostHovered) _railVisual.StopAnimation("Offset");
        }
        catch
        {
            // Composition unavailable (design-time). Rail stays static.
        }
    }

    private void StopAnimation()
    {
        if (_railVisual is null) return;
        try { _railVisual.StopAnimation("Offset"); } catch { }
        _isAnimating = false;
    }

    private void HostGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHostHovered = true;
        if (_railVisual is null) return;
        try { _railVisual.StopAnimation("Offset"); } catch { }
    }

    private void HostGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHostHovered = false;
        if (_railVisual is null || _singleSetWidth <= 0) return;
        // Re-issue the loop animation. The visual snaps back to keyframe 0
        // (origin) — small jump on resume, acceptable for v1. If users dislike
        // the snap we can capture current Offset.X and re-keyframe to resume
        // from there.
        StartAnimation();
    }

    private void RefreshFadeBrushes()
    {
        var endColor = ResolveFadeEndColor();
        LeftFade.Fill = BuildEdgeGradient(left: true, endColor);
        RightFade.Fill = BuildEdgeGradient(left: false, endColor);
        LeftFade.Width = EdgeFadeWidth;
        RightFade.Width = EdgeFadeWidth;
    }

    private Color ResolveFadeEndColor()
    {
        // Prefer an explicit FadeBackground override (artist hero blends a
        // custom backdrop). Fall back to the theme page background.
        if (FadeBackground is SolidColorBrush scb) return scb.Color;

        if (Application.Current?.Resources is { } res
            && res.TryGetValue("ApplicationPageBackgroundThemeBrush", out var raw)
            && raw is SolidColorBrush themeBrush)
        {
            return themeBrush.Color;
        }
        return ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(0xFF, 0x14, 0x14, 0x14)
            : Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
    }

    private static LinearGradientBrush BuildEdgeGradient(bool left, Color endColor)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(left ? 0 : 1, 0.5),
            EndPoint = new Point(left ? 1 : 0, 0.5),
        };
        brush.GradientStops.Add(new GradientStop { Color = endColor, Offset = 0 });
        var mid = endColor;
        mid.A = 0xC0;
        brush.GradientStops.Add(new GradientStop { Color = mid, Offset = 0.5 });
        var transparent = endColor;
        transparent.A = 0;
        brush.GradientStops.Add(new GradientStop { Color = transparent, Offset = 1 });
        return brush;
    }

    private void ApplyHalo()
    {
        // No-op for v1. Composition DropShadow requires a SpriteVisual host;
        // wiring that up for a continuously-translated rail without breaking
        // the Offset animation is out of scope. The visual hierarchy already
        // gets enough lift from per-tile rounded clips + edge fades. If the
        // halo is later wanted back, wrap each tile in an AttachedCardShadow
        // at low opacity — composes per-tile without needing a host
        // SpriteVisual on the rail.
    }
}

public sealed class GalleryItemTappedEventArgs : EventArgs
{
    public int Index { get; init; }
    public string ImageUrl { get; init; } = string.Empty;
}
