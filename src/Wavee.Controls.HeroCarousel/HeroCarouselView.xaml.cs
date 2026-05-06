using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Wavee.Controls.HeroCarousel.Shaders;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
// this is neccesary !
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Wavee.Controls.HeroCarousel;

public sealed partial class HeroCarouselView : UserControl
{
    private const double StageAspectWidth = 16.0;
    private const double StageAspectHeight = 9.0;
    private const double DefaultMaxStageWidth = 1100.0;
    private const double DesktopContentInset = 44.0;
    private const double CompactContentInset = 24.0;
    private const double CompactBreakpoint = 720.0;
    private const double TrackAnimationMs = 980.0;
    private const double GlowAnimationMs = 900.0;
    private const double NavAnimationMs = 280.0;
    private const double ContentHighlightAnimationMs = 1120.0;
    private const double MinSnapAnimationMs = 320.0;
    private const double MaxSnapAnimationMs = 900.0;
    private const double HeroOverscanRatio = 0.20;
    private const double LeftHeroScaleFactor = 0.14;
    private const double RightHeroScaleFactor = 0.10;
    private const double LeftCardScaleFactor = 0.10;
    private const double RightCardScaleFactor = 0.06;
    private const double TextParallaxAmplitude = 0.035;
    private const double ContentCardParallaxFactor = 0.045;
    private const double ContentLayerParallaxFactor = 0.012;
    private const double TrackpadWheelDeltaScale = 0.28;
    private const double TouchDragThreshold = 8.0;
    private const double PipSlotWidth = 18.0;
    private const double PipIndicatorWidth = 18.0;
    private static readonly TimeSpan DefaultAutoAdvanceInterval = TimeSpan.FromSeconds(6);
    private const int MaxWheelDiagnostics = 220;
    private const int DefaultImageCacheCapacity = 24;
    private const double GlowBleed = 220.0;

    private const float CozyWashStop1 = 0.13f;
    private const float CozyWashStop2 = 0.476f;
    private const float CozyBarSize = 0.13f;
    private const float CozyBarFeather = 0.08f;
    private const float CozyBarDecayStart = 0.21f;
    private const float CozyBarDecayEnd = 0.60f;
    private static readonly Color CozyWallBackdrop = Color.FromArgb(255, 0x1A, 0x14, 0x10);

    // Segoe Fluent Icons — ChevronRight (E76C). Matches FluentGlyphs.ChevronRight in the WinUI app project.
    private const string GhostPillChevronGlyph = "";

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(HeroCarouselView),
            new PropertyMetadata(null, OnItemsSourcePropertyChanged));

    public static readonly DependencyProperty ContentTemplateProperty =
        DependencyProperty.Register(
            nameof(ContentTemplate),
            typeof(DataTemplate),
            typeof(HeroCarouselView),
            new PropertyMetadata(null, OnRebuildPropertyChanged));

    public static readonly DependencyProperty PlaceholderTemplateProperty =
        DependencyProperty.Register(
            nameof(PlaceholderTemplate),
            typeof(DataTemplate),
            typeof(HeroCarouselView),
            new PropertyMetadata(null, OnRebuildPropertyChanged));

    public static readonly DependencyProperty ImageProviderProperty =
        DependencyProperty.Register(
            nameof(ImageProvider),
            typeof(IHeroCarouselImageProvider),
            typeof(HeroCarouselView),
            new PropertyMetadata(null, OnRebuildPropertyChanged));

    public static readonly DependencyProperty ImageStretchProperty =
        DependencyProperty.Register(
            nameof(ImageStretch),
            typeof(Stretch),
            typeof(HeroCarouselView),
            new PropertyMetadata(Stretch.UniformToFill, OnRebuildPropertyChanged));

    public static readonly DependencyProperty IsLoopingEnabledProperty =
        DependencyProperty.Register(
            nameof(IsLoopingEnabled),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(true, OnRebuildPropertyChanged));

    public static readonly DependencyProperty IsAutoAdvanceEnabledProperty =
        DependencyProperty.Register(
            nameof(IsAutoAdvanceEnabled),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(false, OnAutoAdvancePropertyChanged));

    public static readonly DependencyProperty AutoAdvanceIntervalProperty =
        DependencyProperty.Register(
            nameof(AutoAdvanceInterval),
            typeof(TimeSpan),
            typeof(HeroCarouselView),
            new PropertyMetadata(DefaultAutoAdvanceInterval, OnAutoAdvancePropertyChanged));

    public static readonly DependencyProperty PauseAutoAdvanceOnInteractionProperty =
        DependencyProperty.Register(
            nameof(PauseAutoAdvanceOnInteraction),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(true, OnAutoAdvancePropertyChanged));

    public static readonly DependencyProperty ShowNavigationButtonsProperty =
        DependencyProperty.Register(
            nameof(ShowNavigationButtons),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(true, OnVisualOptionsPropertyChanged));

    public static readonly DependencyProperty ShowPipsProperty =
        DependencyProperty.Register(
            nameof(ShowPips),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(true, OnVisualOptionsPropertyChanged));

    public static readonly DependencyProperty PipsVisibilityProperty =
        DependencyProperty.Register(
            nameof(PipsVisibility),
            typeof(Visibility),
            typeof(HeroCarouselView),
            new PropertyMetadata(Visibility.Visible, OnVisualOptionsPropertyChanged));

    public static readonly DependencyProperty UseGlowProperty =
        DependencyProperty.Register(
            nameof(UseGlow),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(true, OnVisualOptionsPropertyChanged));

    public static readonly DependencyProperty UseColorWashProperty =
        DependencyProperty.Register(
            nameof(UseColorWash),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(true, OnRebuildPropertyChanged));

    public static readonly DependencyProperty UseSpotlightProperty =
        DependencyProperty.Register(
            nameof(UseSpotlight),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(true, OnVisualOptionsPropertyChanged));

    public static readonly DependencyProperty UseShimmerPlaceholderProperty =
        DependencyProperty.Register(
            nameof(UseShimmerPlaceholder),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(true, OnRebuildPropertyChanged));

    public static readonly DependencyProperty UseButtonRevealProperty =
        DependencyProperty.Register(
            nameof(UseButtonReveal),
            typeof(bool),
            typeof(HeroCarouselView),
            new PropertyMetadata(true, OnRebuildPropertyChanged));

    public static readonly DependencyProperty MaxStageWidthProperty =
        DependencyProperty.Register(
            nameof(MaxStageWidth),
            typeof(double),
            typeof(HeroCarouselView),
            new PropertyMetadata(DefaultMaxStageWidth, OnVisualOptionsPropertyChanged));

    public static readonly DependencyProperty ImageCacheCapacityProperty =
        DependencyProperty.Register(
            nameof(ImageCacheCapacity),
            typeof(int),
            typeof(HeroCarouselView),
            new PropertyMetadata(DefaultImageCacheCapacity, OnImageCacheCapacityPropertyChanged));

    private readonly ResourceLoader _resources = new();
    private readonly DirectManipulationContactTracker _contactTracker = DirectManipulationContactTracker.Shared;
    private readonly List<FrameworkElement> _backgroundSlides = [];
    private readonly List<FrameworkElement> _heroLayers = [];
    private readonly List<CanvasControl> _washCanvases = [];
    private readonly List<FrameworkElement> _contentCards = [];
    private readonly List<ContentCardLayers> _contentCardLayers = [];
    private readonly List<Visual> _heroLayerVisuals = [];
    private readonly List<object?> _items = [];
    private readonly Dictionary<object, ImageSource> _imageCache = [];
    private readonly Queue<object> _imageCacheOrder = [];
    private readonly Dictionary<Image, FrameworkElement> _imagePlaceholders = [];
    private readonly Dictionary<Image, CancellationTokenSource> _imageLoadTokens = [];
    private readonly Dictionary<string, CanvasBitmap> _mosaicTileBitmaps = [];
    private readonly HashSet<string> _mosaicTileLoading = [];

    private Visual? _backgroundTrackVisual;
    private Visual? _contentTrackVisual;
    private Compositor? _compositor;
    private INotifyCollectionChanged? _itemsSourceCollectionChanged;
    private CanvasControl? _activeGlow;
    private DispatcherQueueTimer? _autoAdvanceTimer;
    private Color _glowColorA;
    private Color _glowColorB;
    private bool _isLoaded;
    private bool _isRebuilding;
    private bool _isWheeling;
    private bool _isPointerDragging;
    private bool _pointerDragAccepted;
    private bool _updatingPips;
    private bool _snapRendering;
    private bool _wheelSettleQueued;
    private bool _commitCurrentIndexOnSnapComplete;
    private bool _contentHighlightRendering;
    private bool _isPointerOverStage;
    private int _currentIndex;
    private int _scrollDirection;
    private int _snapTargetIndex;
    private uint _dragPointerId;
    private double _stageWidth;
    private double _stageHeight;
    private double _baseTransform;
    private double _scrollOffset;
    private double _dragStartX;
    private double _dragStartY;
    private double _dragStartOffset;
    private double _dragLastX;
    private double _dragVelocityX;
    private double _snapFrom;
    private double _snapTo;
    private double _snapDurationMs;
    private TimeSpan _snapStartedAt;
    private TimeSpan _contentHighlightStartedAt;
    private double _contentHighlightProgress;
    private bool _snapBackEasing;
    private long _dragLastTimestamp;
    private float _spotlightX;
    private float _spotlightY;
    private float _spotlightOpacity;
    private int _wheelDiagnosticsCount;

    public HeroCarouselView()
    {
        InitializeComponent();

        _activeGlow = GlowCanvasB;

        Slides.CollectionChanged += OnSlidesCollectionChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        GlowCanvasA.Draw += OnGlowCanvasDraw;
        GlowCanvasB.Draw += OnGlowCanvasDraw;
        SpotlightCanvas.Draw += OnSpotlightCanvasDraw;
        ApplyAutomationText();

        // AttachedCardShadow set in code-behind because the WinUI XAML
        // compiler in class libraries (vs application projects) doesn't
        // reliably resolve the cross-package Effects.Shadow attached
        // property at compile time even with all CommunityToolkit packages
        // explicitly referenced. Programmatic set sidesteps the WMC0001.
        //
        // Shadow target is the non-rounded StageShadowHost wrapper Grid,
        // not the inner rounded StageBorder. AttachedCardShadow + an
        // intrinsically-rounded Border has a known clipping interaction;
        // wrapping in a flat Grid avoids it and the shadow's own
        // CornerRadius parameter still produces a rounded shadow shape.
        //
        // Values tuned for a dark-theme page: too-aggressive shadows
        // (BlurRadius >50 with Opacity ≥1) produce visible gradient
        // banding on the left/right edges because the dark page makes
        // every step in the shadow's alpha falloff perceptible. Subtle
        // values read as a natural elevation cue rather than a halo.
        CommunityToolkit.WinUI.Effects.SetShadow(StageShadowHost, new CommunityToolkit.WinUI.Media.AttachedCardShadow
        {
            BlurRadius = 30,
            CornerRadius = 18,
            Offset = "0,12",
            Opacity = 0.45,
            Color = Microsoft.UI.Colors.Black,
        });
    }

    public ObservableCollection<HeroCarouselSlide> Slides { get; } = [];

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public DataTemplate? ContentTemplate
    {
        get => (DataTemplate?)GetValue(ContentTemplateProperty);
        set => SetValue(ContentTemplateProperty, value);
    }

    public DataTemplate? PlaceholderTemplate
    {
        get => (DataTemplate?)GetValue(PlaceholderTemplateProperty);
        set => SetValue(PlaceholderTemplateProperty, value);
    }

    public IHeroCarouselImageProvider ImageProvider
    {
        get => (IHeroCarouselImageProvider?)GetValue(ImageProviderProperty) ?? DefaultHeroCarouselImageProvider.Instance;
        set => SetValue(ImageProviderProperty, value);
    }

    public Stretch ImageStretch
    {
        get => (Stretch)GetValue(ImageStretchProperty);
        set => SetValue(ImageStretchProperty, value);
    }

    public bool IsLoopingEnabled
    {
        get => (bool)GetValue(IsLoopingEnabledProperty);
        set => SetValue(IsLoopingEnabledProperty, value);
    }

    public bool IsAutoAdvanceEnabled
    {
        get => (bool)GetValue(IsAutoAdvanceEnabledProperty);
        set => SetValue(IsAutoAdvanceEnabledProperty, value);
    }

    public TimeSpan AutoAdvanceInterval
    {
        get => (TimeSpan)GetValue(AutoAdvanceIntervalProperty);
        set => SetValue(AutoAdvanceIntervalProperty, value);
    }

    public bool PauseAutoAdvanceOnInteraction
    {
        get => (bool)GetValue(PauseAutoAdvanceOnInteractionProperty);
        set => SetValue(PauseAutoAdvanceOnInteractionProperty, value);
    }

    public bool ShowNavigationButtons
    {
        get => (bool)GetValue(ShowNavigationButtonsProperty);
        set => SetValue(ShowNavigationButtonsProperty, value);
    }

    public bool ShowPips
    {
        get => (bool)GetValue(ShowPipsProperty);
        set => SetValue(ShowPipsProperty, value);
    }

    public Visibility PipsVisibility
    {
        get => (Visibility)GetValue(PipsVisibilityProperty);
        set => SetValue(PipsVisibilityProperty, value);
    }

    public bool UseGlow
    {
        get => (bool)GetValue(UseGlowProperty);
        set => SetValue(UseGlowProperty, value);
    }

    public bool UseColorWash
    {
        get => (bool)GetValue(UseColorWashProperty);
        set => SetValue(UseColorWashProperty, value);
    }

    public bool UseSpotlight
    {
        get => (bool)GetValue(UseSpotlightProperty);
        set => SetValue(UseSpotlightProperty, value);
    }

    public bool UseShimmerPlaceholder
    {
        get => (bool)GetValue(UseShimmerPlaceholderProperty);
        set => SetValue(UseShimmerPlaceholderProperty, value);
    }

    public bool UseButtonReveal
    {
        get => (bool)GetValue(UseButtonRevealProperty);
        set => SetValue(UseButtonRevealProperty, value);
    }

    public double MaxStageWidth
    {
        get => (double)GetValue(MaxStageWidthProperty);
        set => SetValue(MaxStageWidthProperty, value);
    }

    public int ImageCacheCapacity
    {
        get => (int)GetValue(ImageCacheCapacityProperty);
        set => SetValue(ImageCacheCapacityProperty, value);
    }

    public int CurrentIndex
    {
        get => _currentIndex;
        set => GoTo(value);
    }

    public event EventHandler<int>? CurrentIndexChanged;

    public event EventHandler<int>? CtaClicked;

    private int SlideCount => _items.Count;

    private bool IsLooping => IsLoopingEnabled && SlideCount > 1;

    private int TrackItemCount => IsLooping ? SlideCount * 3 : SlideCount;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        EnsureItemsSourceSubscription();
        _contactTracker.RefreshWindowTree();
        RefreshItems();
        RebuildSlides();
        UpdateAutoAdvanceTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopAutoAdvanceTimer();
        CancelSnapAnimation();
        CancelWheelSettle();
        CancelContentHighlight();
        CleanupImageLoadState();
        ClearImageCache();
        ClearItemsSourceSubscription();
        _contactTracker.Reset();
    }

    private void OnSlidesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isLoaded && !_isRebuilding && ItemsSource is null)
        {
            RebuildSlides();
        }
    }

    private static void OnItemsSourcePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HeroCarouselView)d).OnItemsSourceChanged(e.NewValue as INotifyCollectionChanged);
    }

    private static void OnRebuildPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        HeroCarouselView view = (HeroCarouselView)d;

        if (view._isLoaded && !view._isRebuilding)
        {
            view.RebuildSlides();
        }
    }

    private static void OnVisualOptionsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        HeroCarouselView view = (HeroCarouselView)d;

        view.UpdateChromeVisibility();
        view.UpdatePips();

        if (view._isLoaded)
        {
            view.ApplyGlow(view._currentIndex, true);
        }
    }

    private static void OnAutoAdvancePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HeroCarouselView)d).UpdateAutoAdvanceTimer();
    }

    private static void OnImageCacheCapacityPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HeroCarouselView)d).TrimImageCache();
    }

    private void OnItemsSourceChanged(INotifyCollectionChanged? newSource)
    {
        ClearItemsSourceSubscription();
        SetItemsSourceSubscription(newSource);

        if (_isLoaded && !_isRebuilding)
        {
            RebuildSlides();
        }
    }

    private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isLoaded && !_isRebuilding)
        {
            RebuildSlides();
        }
    }

    private void EnsureItemsSourceSubscription()
    {
        if (_itemsSourceCollectionChanged is null && ItemsSource is INotifyCollectionChanged notifyCollectionChanged)
        {
            SetItemsSourceSubscription(notifyCollectionChanged);
        }
    }

    private void SetItemsSourceSubscription(INotifyCollectionChanged? source)
    {
        _itemsSourceCollectionChanged = source;

        if (_itemsSourceCollectionChanged is not null)
        {
            _itemsSourceCollectionChanged.CollectionChanged += OnItemsSourceCollectionChanged;
        }
    }

    private void ClearItemsSourceSubscription()
    {
        if (_itemsSourceCollectionChanged is not null)
        {
            _itemsSourceCollectionChanged.CollectionChanged -= OnItemsSourceCollectionChanged;
            _itemsSourceCollectionChanged = null;
        }
    }

    private void RefreshItems()
    {
        _items.Clear();

        if (ItemsSource is IEnumerable source)
        {
            foreach (object? item in source)
            {
                _items.Add(item);
            }

            return;
        }

        foreach (HeroCarouselSlide slide in Slides)
        {
            _items.Add(slide);
        }
    }

    private object? GetItem(int index)
    {
        return _items[index];
    }

    private HeroCarouselSlide GetSlide(int index)
    {
        return ResolveSlide(GetItem(index));
    }

    private static HeroCarouselSlide ResolveSlide(object? item)
    {
        if (item is HeroCarouselSlide slide)
        {
            return slide;
        }

        return new HeroCarouselSlide
        {
            Image = item,
            Title = item?.ToString() ?? string.Empty,
            UseScrim = true,
        };
    }

    private void RebuildSlides()
    {
        bool animateRebuild = _isLoaded && BgTrack.Children.Count > 0;

        if (animateRebuild)
        {
            BgTrack.Opacity = 0;
            ContentTrack.Opacity = 0;
        }

        CleanupImageLoadState();
        RefreshItems();
        BgTrack.Children.Clear();
        BgTrack.ColumnDefinitions.Clear();
        ContentTrack.Children.Clear();
        ContentTrack.ColumnDefinitions.Clear();
        _backgroundSlides.Clear();
        _heroLayers.Clear();
        _washCanvases.Clear();
        _contentCards.Clear();
        _contentCardLayers.Clear();
        _heroLayerVisuals.Clear();

        if (SlideCount == 0)
        {
            LoadingShimmer.Visibility = Visibility.Visible;
            UpdateChromeVisibility();
            UpdatePips();
            UpdateAutoAdvanceTimer();
            return;
        }

        LoadingShimmer.Visibility = Visibility.Collapsed;
        _currentIndex = Math.Clamp(_currentIndex, 0, SlideCount - 1);

        for (int i = 0; i < TrackItemCount; i++)
        {
            BgTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ContentTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int slideIndex = GetLogicalIndexForTrackIndex(i);
            object? item = GetItem(slideIndex);
            HeroCarouselSlide slide = ResolveSlide(item);
            FrameworkElement backgroundSlide = CreateBackgroundSlide(item, slide);
            Grid.SetColumn(backgroundSlide, i);
            BgTrack.Children.Add(backgroundSlide);
            _backgroundSlides.Add(backgroundSlide);

            Grid contentSlide = CreateContentSlide(item, slide, i);
            Grid.SetColumn(contentSlide, i);
            ContentTrack.Children.Add(contentSlide);

        }

        UpdateLayoutForStageSize();
        CacheCompositionVisuals();
        ApplyGlow(_currentIndex, true);
        UpdateChromeVisibility();
        UpdatePips();
        SetTransforms(true);
        UpdateAutoAdvanceTimer();

        if (animateRebuild)
        {
            FadeElement(BgTrack, 1, 220);
            FadeElement(ContentTrack, 1, 220);
        }
        else
        {
            BgTrack.Opacity = 1;
            ContentTrack.Opacity = 1;
        }
    }

    private FrameworkElement CreateBackgroundSlide(object? item, HeroCarouselSlide slide)
    {
        if (slide.CtaStyle == HeroCarouselCtaStyle.GhostPill)
        {
            return CreateCozyBackgroundSlide(item, slide);
        }

        Grid slideRoot = new()
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 17, 17, 17)),
            IsHitTestVisible = false,
        };

        Grid hero = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new CompositeTransform(),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        Image image = new()
        {
            Opacity = 0,
            Stretch = ImageStretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        FrameworkElement placeholder = CreateImagePlaceholder(item, slide);

        AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(placeholder, AccessibilityView.Raw);

        hero.Children.Add(image);
        hero.Children.Add(placeholder);
        BeginHeroImageLoad(item, slide, image, placeholder);

        slideRoot.Children.Add(hero);

        CanvasControl wash = CreateColorWash(slide);
        slideRoot.Children.Add(wash);

        _heroLayers.Add(hero);
        _washCanvases.Add(wash);

        return slideRoot;
    }

    private FrameworkElement CreateCozyBackgroundSlide(object? item, HeroCarouselSlide slide)
    {
        Grid slideRoot = new()
        {
            Background = new SolidColorBrush(CozyWallBackdrop),
            IsHitTestVisible = false,
        };

        // The carousel's parallax loop scales whatever is in _heroLayers by
        // writing CompositeTransform.ScaleX/ScaleY. Wrap the canvas in a
        // Grid so the wrapper carries the parallax transform.
        Grid heroParallaxLayer = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransform = new CompositeTransform(),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        // Per-slide hue. If the consumer left CozyTintColor unset we fall
        // back to the prototype's settled violet OKLCH(0.55 0.15 274). The
        // overlay highlight pass uses the same hue at higher lightness; a
        // simple mix-toward-white approximates the prototype's
        // OKLCH(0.7 0.18 hue / 0.25) without a full sRGB→OKLCH→sRGB roundtrip.
        Color tintHueColor = slide.CozyTintColor.A == 0
            ? OklchHelpers.OklchToColor(0.55, 0.15, 274, 1.0)
            : Color.FromArgb(255, slide.CozyTintColor.R, slide.CozyTintColor.G, slide.CozyTintColor.B);
        Color tintHighlightColor = Color.FromArgb(64, // ~0.25 alpha
            (byte)Math.Clamp(tintHueColor.R + 60, 0, 255),
            (byte)Math.Clamp(tintHueColor.G + 60, 0, 255),
            (byte)Math.Clamp(tintHueColor.B + 60, 0, 255));

        Uri imageUri = slide.ImageUri;

        CanvasControl canvas = CreateCozyCanvas(imageUri, tintHueColor, tintHighlightColor);
        heroParallaxLayer.Children.Add(canvas);
        slideRoot.Children.Add(heroParallaxLayer);

        _heroLayers.Add(heroParallaxLayer);

        // Intentionally NOT adding to _washCanvases. That list drives the
        // wash-overlay opacity fade in the parallax loop, which assumes the
        // artwork below is a separate always-opaque Image element. In cozy
        // mode the canvas IS the artwork (image + treatment composited in a
        // single opaque output), so distance-based opacity fade would dim
        // the entire plate during transit and produce a visible seam where
        // adjacent slides meet at mid-scroll. Canvas stays at Opacity=1.

        return slideRoot;
    }

    private CanvasControl CreateCozyCanvas(Uri imageUri, Color tintHue, Color tintHighlight)
    {
        CanvasControl canvas = new()
        {
            ClearColor = CozyWallBackdrop,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false,
            Opacity = 1,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        canvas.Draw += (sender, args) => DrawCozyImage(sender, args, imageUri, tintHue, tintHighlight);
        canvas.SizeChanged += (sender, _) => ((CanvasControl)sender).Invalidate();
        canvas.CreateResources += OnCozyCanvasCreateResources;
        AutomationProperties.SetAccessibilityView(canvas, AccessibilityView.Raw);

        return canvas;
    }

    private void OnCozyCanvasCreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        // CanvasBitmaps are tied to a CanvasDevice. If the device is recreated
        // (lost, refreshed, etc.) all cached bitmaps become invalid — drop
        // the cache so the next Draw kicks off fresh loads.
        ClearMosaicTileBitmaps();
    }

    private void DrawCozyImage(
        CanvasControl sender,
        CanvasDrawEventArgs args,
        Uri imageUri,
        Color tintHue,
        Color tintHighlight)
    {
        if (sender.ActualWidth <= 0 || sender.ActualHeight <= 0)
        {
            return;
        }

        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;
        Rect canvasRect = new(0, 0, w, h);

        // 1. Rasterize the show's cover into an offscreen target,
        //    UniformToFill cropped to canvas size. Default render target
        //    format is B8G8R8A8UIntNormalized (sRGB encoded) — keeps the
        //    multiply pass below in sRGB space, matching CSS.
        using CanvasRenderTarget wallTarget = new(sender.Device, w, h, sender.Dpi);
        using (CanvasDrawingSession ds = wallTarget.CreateDrawingSession())
        {
            ds.Clear(CozyWallBackdrop);

            CanvasBitmap? bmp = GetOrLoadTileBitmap(sender, imageUri);
            if (bmp is not null)
            {
                Rect sourceRect = ComputeUniformToFillSource(bmp.Bounds, canvasRect);
                ds.DrawImage(bmp, canvasRect, sourceRect);
            }
        }

        // 2. Run the cozy shader: it samples the rasterized image as input,
        //    applies multiply + overlay tints, then composites left fade +
        //    masked bars + vignette in one pass.
        float4 tintColorVec = new(tintHue.R / 255f, tintHue.G / 255f, tintHue.B / 255f, 1f);
        float4 tintHighlightVec = new(
            tintHighlight.R / 255f,
            tintHighlight.G / 255f,
            tintHighlight.B / 255f,
            tintHighlight.A / 255f);

        PixelShaderEffect<MosaicWashShader> effect = new()
        {
            ConstantBuffer = new MosaicWashShader(
                w,
                h,
                tintColorVec,
                tintHighlightVec,
                0.55f,
                CozyWashStop1,
                CozyWashStop2,
                CozyBarSize,
                CozyBarFeather,
                CozyBarDecayStart,
                CozyBarDecayEnd),
        };
        effect.Sources[0] = wallTarget;

        args.DrawingSession.DrawImage(effect);
    }

    private CanvasBitmap? GetOrLoadTileBitmap(CanvasControl canvas, Uri uri)
    {
        string key = uri.AbsoluteUri;
        if (_mosaicTileBitmaps.TryGetValue(key, out CanvasBitmap? bmp))
        {
            return bmp;
        }
        if (_mosaicTileLoading.Add(key))
        {
            _ = LoadTileBitmapAsync(canvas, uri, key);
        }
        return null;
    }

    private async Task LoadTileBitmapAsync(CanvasControl canvas, Uri uri, string key)
    {
        try
        {
            CanvasBitmap loaded = await CanvasBitmap.LoadAsync(canvas.Device, uri);
            DispatcherQueue.TryEnqueue(() =>
            {
                _mosaicTileBitmaps[key] = loaded;
                _mosaicTileLoading.Remove(key);
                if (canvas.Device is not null)
                {
                    canvas.Invalidate();
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HC_MOSAIC tile load failed uri={uri} error={ex.Message}");
            DispatcherQueue.TryEnqueue(() =>
            {
                _mosaicTileLoading.Remove(key);
            });
        }
    }

    private static Rect ComputeUniformToFillSource(Rect srcBounds, Rect destRect)
    {
        if (srcBounds.Width <= 0 || srcBounds.Height <= 0 || destRect.Width <= 0 || destRect.Height <= 0)
        {
            return srcBounds;
        }

        double srcAspect = srcBounds.Width / srcBounds.Height;
        double destAspect = destRect.Width / destRect.Height;
        if (srcAspect > destAspect)
        {
            double newWidth = srcBounds.Height * destAspect;
            double offsetX = (srcBounds.Width - newWidth) / 2.0;
            return new Rect(srcBounds.X + offsetX, srcBounds.Y, newWidth, srcBounds.Height);
        }

        double newHeight = srcBounds.Width / destAspect;
        double offsetY = (srcBounds.Height - newHeight) / 2.0;
        return new Rect(srcBounds.X, srcBounds.Y + offsetY, srcBounds.Width, newHeight);
    }

    private FrameworkElement CreateImagePlaceholder(object? item, HeroCarouselSlide slide)
    {
        if (PlaceholderTemplate is not null)
        {
            return new ContentControl
            {
                Content = item,
                ContentTemplate = PlaceholderTemplate,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
            };
        }

        if (UseShimmerPlaceholder)
        {
            return new Shimmer
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
            };
        }

        Color baseColor = slide.GlowColor;

        return new Grid
        {
            Background = new SolidColorBrush(Mix(baseColor, Colors.Black, 0.64, 255)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };
    }

    private void BeginHeroImageLoad(object? item, HeroCarouselSlide slide, Image image, FrameworkElement placeholder)
    {
        object? imageValue = GetSlideImage(slide);
        IHeroCarouselImageProvider provider = ImageProvider;
        object? cacheKey = provider.GetCacheKey(item, imageValue);

        if (cacheKey is not null && _imageCache.TryGetValue(cacheKey, out ImageSource? cachedSource))
        {
            SetHeroImageSource(image, placeholder, cachedSource, true);
            return;
        }

        CancellationTokenSource cancellation = new();
        _imageLoadTokens[image] = cancellation;
        _ = LoadHeroImageAsync(provider, item, imageValue, cacheKey, image, placeholder, cancellation);
    }

    private static object? GetSlideImage(HeroCarouselSlide slide)
    {
        return slide.Image ?? slide.ImageUri;
    }

    private async Task LoadHeroImageAsync(
        IHeroCarouselImageProvider provider,
        object? item,
        object? imageValue,
        object? cacheKey,
        Image image,
        FrameworkElement placeholder,
        CancellationTokenSource cancellation)
    {
        ImageSource? source = null;

        try
        {
            source = await provider.LoadAsync(item, imageValue, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HC_IMAGE failed value={imageValue} error={ex.Message}");
        }

        if (cancellation.IsCancellationRequested)
        {
            cancellation.Dispose();
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Yield();

            if (!_imageLoadTokens.TryGetValue(image, out CancellationTokenSource? current) ||
                !ReferenceEquals(current, cancellation))
            {
                cancellation.Dispose();
                return;
            }

            _imageLoadTokens.Remove(image);
            cancellation.Dispose();

            if (source is null)
            {
                StopImageShimmer(placeholder);
                return;
            }

            if (cacheKey is not null)
            {
                AddImageToCache(cacheKey, source);
            }

            SetHeroImageSource(image, placeholder, source, false);
        });
    }

    private void SetHeroImageSource(Image image, FrameworkElement placeholder, ImageSource source, bool fromCache)
    {
        image.ImageOpened -= OnHeroImageOpened;
        image.ImageFailed -= OnHeroImageFailed;
        image.ImageOpened += OnHeroImageOpened;
        image.ImageFailed += OnHeroImageFailed;
        _imagePlaceholders[image] = placeholder;
        image.Source = source;

        if (IsImageSourceLoaded(source))
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Yield();
                RevealLoadedHeroImage(image, placeholder, false);
            });
        }
    }

    private void OnHeroImageOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image ||
            !_imagePlaceholders.TryGetValue(image, out FrameworkElement? placeholder))
        {
            return;
        }

        Debug.WriteLine("HC_IMAGE opened");
        DispatcherQueue.TryEnqueue(() =>
        {
            RevealLoadedHeroImage(image, placeholder, false);
        });
    }

    private void OnHeroImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not Image image ||
            !_imagePlaceholders.TryGetValue(image, out FrameworkElement? placeholder))
        {
            return;
        }

        Debug.WriteLine($"HC_IMAGE failed error={e.ErrorMessage}");
        StopImageShimmer(placeholder);
        FadeElement(placeholder, 0, 260, () => placeholder.Visibility = Visibility.Collapsed);
    }

    private static bool IsImageSourceLoaded(ImageSource source)
    {
        return source is not BitmapImage bitmap || bitmap.PixelWidth > 0 || bitmap.PixelHeight > 0;
    }

    private void AddImageToCache(object cacheKey, ImageSource source)
    {
        if (!_imageCache.ContainsKey(cacheKey))
        {
            _imageCacheOrder.Enqueue(cacheKey);
        }

        _imageCache[cacheKey] = source;
        TrimImageCache();
    }

    private void TrimImageCache()
    {
        int capacity = Math.Max(0, ImageCacheCapacity);

        while (_imageCache.Count > capacity && _imageCacheOrder.Count > 0)
        {
            object cacheKey = _imageCacheOrder.Dequeue();
            _imageCache.Remove(cacheKey);
        }
    }

    private void ClearImageCache()
    {
        _imageCache.Clear();
        _imageCacheOrder.Clear();
        ClearMosaicTileBitmaps();
    }

    private void ClearMosaicTileBitmaps()
    {
        foreach (CanvasBitmap bmp in _mosaicTileBitmaps.Values)
        {
            bmp.Dispose();
        }
        _mosaicTileBitmaps.Clear();
        _mosaicTileLoading.Clear();
    }

    private void CleanupImageLoadState()
    {
        foreach (CancellationTokenSource cancellation in _imageLoadTokens.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        foreach (Image image in _imagePlaceholders.Keys)
        {
            image.ImageOpened -= OnHeroImageOpened;
            image.ImageFailed -= OnHeroImageFailed;
        }

        _imageLoadTokens.Clear();
        _imagePlaceholders.Clear();
    }

    private void RevealLoadedHeroImage(Image image, FrameworkElement shimmer, bool immediate)
    {
        image.Visibility = Visibility.Visible;

        if (immediate)
        {
            image.Opacity = 1;
            shimmer.Opacity = 0;
            shimmer.Visibility = Visibility.Collapsed;
            StopImageShimmer(shimmer);

            return;
        }

        FadeElement(image, 1, 480);
        FadeElement(shimmer, 0, 420, () =>
        {
            StopImageShimmer(shimmer);
            shimmer.Visibility = Visibility.Collapsed;
        });
    }

    private static Grid CreateImageShimmer(HeroCarouselSlide slide)
    {
        Color baseColor = slide.GlowColor;
        Color left = Mix(baseColor, Colors.Black, 0.62, 255);
        Color middle = Mix(baseColor, Colors.White, 0.08, 255);
        Color right = Mix(baseColor, Colors.Black, 0.76, 255);
        LinearGradientBrush baseBrush = new()
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        LinearGradientBrush sheenBrush = new()
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };

        baseBrush.GradientStops.Add(new GradientStop { Offset = 0.00, Color = left });
        baseBrush.GradientStops.Add(new GradientStop { Offset = 0.44, Color = middle });
        baseBrush.GradientStops.Add(new GradientStop { Offset = 1.00, Color = right });
        sheenBrush.GradientStops.Add(new GradientStop { Offset = 0.00, Color = Color.FromArgb(0, 255, 255, 255) });
        sheenBrush.GradientStops.Add(new GradientStop { Offset = 0.42, Color = Color.FromArgb(34, 255, 255, 255) });
        sheenBrush.GradientStops.Add(new GradientStop { Offset = 0.50, Color = Color.FromArgb(86, 255, 255, 255) });
        sheenBrush.GradientStops.Add(new GradientStop { Offset = 0.58, Color = Color.FromArgb(34, 255, 255, 255) });
        sheenBrush.GradientStops.Add(new GradientStop { Offset = 1.00, Color = Color.FromArgb(0, 255, 255, 255) });

        Rectangle baseLayer = new()
        {
            Fill = baseBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        Rectangle sheen = new()
        {
            Fill = sheenBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        Grid host = new()
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        host.Children.Add(baseLayer);
        host.Children.Add(sheen);
        host.Loaded += (_, _) =>
        {
            if (host.Visibility == Visibility.Visible && host.Opacity > 0)
            {
                StartImageShimmer(sheen, host.ActualWidth);
            }
        };
        host.Unloaded += (_, _) => StopImageShimmer(host);
        host.SizeChanged += (_, _) =>
        {
            host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, host.ActualWidth, host.ActualHeight) };
            sheen.Width = Math.Max(180, host.ActualWidth * 0.28);

            if (host.Visibility == Visibility.Visible && host.Opacity > 0)
            {
                StartImageShimmer(sheen, host.ActualWidth);
            }
        };

        return host;
    }

    private static void StartImageShimmer(FrameworkElement sheen, double hostWidth)
    {
        if (hostWidth <= 0)
        {
            return;
        }

        double sheenWidth = double.IsNaN(sheen.Width) || sheen.Width <= 0 ? Math.Max(180, hostWidth * 0.28) : sheen.Width;
        Visual visual = ElementCompositionPreview.GetElementVisual(sheen);
        Compositor compositor = visual.Compositor;
        ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
        float start = (float)-sheenWidth;
        float end = (float)(hostWidth + sheenWidth);

        visual.StopAnimation("Offset.X");
        visual.Offset = new Vector3(start, 0, 0);
        animation.InsertKeyFrame(0, start);
        animation.InsertKeyFrame(1, end, compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.0f), new Vector2(0.22f, 1.0f)));
        animation.Duration = TimeSpan.FromMilliseconds(1450);
        animation.DelayTime = TimeSpan.FromMilliseconds(120);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Offset.X", animation);
    }

    private static void StopImageShimmer(FrameworkElement shimmer)
    {
        if (shimmer is Grid grid)
        {
            foreach (UIElement child in grid.Children)
            {
                if (child is FrameworkElement element)
                {
                    ElementCompositionPreview.GetElementVisual(element).StopAnimation("Offset.X");
                }
            }
        }
        else
        {
            ElementCompositionPreview.GetElementVisual(shimmer).StopAnimation("Offset.X");
        }
    }

    private static void FadeElement(UIElement element, double to, double durationMs, Action? completed = null)
    {
        DoubleAnimation animation = new()
        {
            From = element.Opacity,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard storyboard = new();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));

        if (completed is not null)
        {
            storyboard.Completed += (_, _) => completed();
        }

        storyboard.Begin();
    }

    private Grid CreateContentSlide(object? item, HeroCarouselSlide slide, int index)
    {
        Grid slideRoot = new()
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(GetContentInset(), 0, 0, 0),
        };

        if (ContentTemplate is not null)
        {
            ContentControl content = new()
            {
                Content = item,
                ContentTemplate = ContentTemplate,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new CompositeTransform(),
                RenderTransformOrigin = new Point(0, 0.5),
            };

            slideRoot.Children.Add(content);
            _contentCards.Add(content);
            _contentCardLayers.Add(new ContentCardLayers(null, null, null, null, null, []));

            return slideRoot;
        }

        StackPanel card = new()
        {
            MaxWidth = 380,
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new CompositeTransform(),
            RenderTransformOrigin = new Point(0, 0.5),
        };

        Brush primaryBrush = new SolidColorBrush(slide.UseDarkText ? Color.FromArgb(255, 24, 20, 31) : Colors.White);
        Brush secondaryBrush = new SolidColorBrush(slide.UseDarkText ? Color.FromArgb(184, 24, 20, 31) : Color.FromArgb(199, 255, 255, 255));
        FrameworkElement? tagLayer = null;
        FrameworkElement? ratingLayer = null;

        bool cozy = slide.CtaStyle == HeroCarouselCtaStyle.GhostPill;

        if (cozy && !string.IsNullOrWhiteSpace(slide.Eyebrow))
        {
            Color eyebrowFg = OklchHelpers.OklchToColor(0.85, 0.08, 274, 1.0);

            TextBlock eyebrow = new()
            {
                Text = slide.Eyebrow.ToUpperInvariant(),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 180,
                Foreground = new SolidColorBrush(eyebrowFg),
                Opacity = 0.95,
                Margin = new Thickness(0, 0, 0, 4),
            };

            PrepareParallaxLayer(eyebrow);
            card.Children.Add(eyebrow);
            tagLayer = eyebrow;
        }
        else if (!string.IsNullOrWhiteSpace(slide.Tag))
        {
            Border tag = new()
            {
                Padding = new Thickness(11, 5, 11, 5),
                Background = new SolidColorBrush(slide.UseDarkText ? Color.FromArgb(179, 255, 255, 255) : Color.FromArgb(140, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(slide.UseDarkText ? Color.FromArgb(16, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = slide.Tag,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = primaryBrush,
                },
            };

            PrepareParallaxLayer(tag);
            card.Children.Add(tag);
            tagLayer = tag;
        }

        // Title styling matches the Sale Hero prototype:
        //   font-weight: 700 (Bold) — heavier than the default SemiBold
        //   line-height: 1.02      — very tight, headline reads as a block
        //   letter-spacing: -0.02em — slight negative tracking, condenses
        //                             the title and adds editorial weight
        // CharacterSpacing units are 1/1000 em, so -0.02em → -20.
        bool compactTitle = _stageWidth <= CompactBreakpoint;
        double titleFontSize = compactTitle ? 28 : 38;
        TextBlock title = PrepareParallaxLayer(new TextBlock
        {
            Text = slide.Title,
            FontSize = titleFontSize,
            FontWeight = cozy ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = cozy ? -20 : 0,
            Foreground = primaryBrush,
            LineHeight = cozy ? Math.Round(titleFontSize * 1.02) : (compactTitle ? 31 : 40),
            TextWrapping = TextWrapping.Wrap,
        });

        TextBlock subtitle = PrepareParallaxLayer(new TextBlock
        {
            Text = slide.Subtitle,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            Foreground = secondaryBrush,
            LineHeight = 20,
            MaxWidth = 320,
            TextWrapping = TextWrapping.Wrap,
        });

        Button ctaContent = cozy
            ? CreateGhostPillCta(slide)
            : new()
            {
                Content = slide.CtaText,
                Padding = new Thickness(34, 10, 34, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = new SolidColorBrush(Colors.White),
                Background = CreateCtaBackground(slide),
                BorderBrush = CreateCtaBorderBrush(slide),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                MinWidth = 140,
                MinHeight = 0,
            };
        ctaContent.Click += (_, _) => CtaClicked?.Invoke(this, _currentIndex);
        FrameworkElement? ctaReveal = null;
        FrameworkElement cta = !cozy && UseButtonReveal
            ? CreateRevealHost(ctaContent, slide, out ctaReveal, 8, slide.CtaUsesGlass ? 0.24 : 0.30)
            : ctaContent;

        cta.Margin = new Thickness(0, 4, 0, 0);
        PrepareParallaxLayer(cta);

        AutomationProperties.SetName(ctaContent, slide.CtaText);
        AutomationProperties.SetAutomationId(ctaContent, $"HeroCarouselCta{index}");

        card.Children.Add(title);
        card.Children.Add(subtitle);
        card.Children.Add(cta);

        if (slide.Rating is not null)
        {
            Grid rating = CreateRating(slide.Rating, primaryBrush, secondaryBrush);
            rating.Margin = new Thickness(0, 2, 0, 0);
            PrepareParallaxLayer(rating);
            card.Children.Add(rating);
            ratingLayer = rating;
        }

        slideRoot.Children.Add(card);
        _contentCards.Add(card);

        List<FrameworkElement> revealLayers = ctaReveal is not null ? [ctaReveal] : [];

        _contentCardLayers.Add(new ContentCardLayers(tagLayer, title, subtitle, cta, ratingLayer, revealLayers));

        return slideRoot;
    }

    private static T PrepareParallaxLayer<T>(T element)
        where T : FrameworkElement
    {
        element.RenderTransform = new CompositeTransform();
        element.RenderTransformOrigin = new Point(0, 0.5);

        return element;
    }

    private static Grid CreateRevealHost(
        FrameworkElement content,
        HeroCarouselSlide slide,
        out FrameworkElement revealLayer,
        double cornerRadius,
        double intensity)
    {
        content.HorizontalAlignment = HorizontalAlignment.Left;

        Border reveal = new()
        {
            Background = CreateRevealBrush(slide, intensity),
            CornerRadius = new CornerRadius(cornerRadius),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransform = new CompositeTransform
            {
                ScaleX = 2.45,
                ScaleY = 2.45,
            },
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        Grid host = new()
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = content.VerticalAlignment,
        };

        host.Children.Add(content);
        host.Children.Add(reveal);
        host.SizeChanged += (_, _) =>
        {
            host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, host.ActualWidth, host.ActualHeight) };
        };

        revealLayer = reveal;

        return host;
    }

    private static Brush CreateRevealBrush(HeroCarouselSlide slide, double intensity)
    {
        intensity = Math.Clamp(intensity, 0, 1);

        byte coreAlpha = (byte)Math.Round(255 * intensity);
        byte haloAlpha = (byte)Math.Round(coreAlpha * 0.45);
        Color core = Mix(Colors.White, slide.AccentColor, 0.20, coreAlpha);
        Color halo = Mix(Colors.White, slide.AccentColor, 0.10, haloAlpha);
        LinearGradientBrush brush = new()
        {
            StartPoint = new Point(0, 1),
            EndPoint = new Point(1, 0),
        };

        brush.GradientStops.Add(new GradientStop { Offset = 0.00, Color = Color.FromArgb(0, 255, 255, 255) });
        brush.GradientStops.Add(new GradientStop { Offset = 0.42, Color = Color.FromArgb(0, 255, 255, 255) });
        brush.GradientStops.Add(new GradientStop { Offset = 0.49, Color = halo });
        brush.GradientStops.Add(new GradientStop { Offset = 0.52, Color = core });
        brush.GradientStops.Add(new GradientStop { Offset = 0.58, Color = halo });
        brush.GradientStops.Add(new GradientStop { Offset = 1.00, Color = Color.FromArgb(0, 255, 255, 255) });

        return brush;
    }

    private static Brush CreateCtaBackground(HeroCarouselSlide slide)
    {
        Color source = slide.AccentColor.A > 0 ? slide.AccentColor : slide.GlowColor;
        return new SolidColorBrush(Mix(source, Colors.Black, 0.34, 238));
    }

    private static Brush CreateCtaBorderBrush(HeroCarouselSlide slide)
    {
        return new SolidColorBrush(Colors.Transparent);
    }

    private static readonly Color GhostPillRest = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color GhostPillHover = Color.FromArgb(40, 255, 255, 255);
    private static readonly Color GhostPillBorderRest = Color.FromArgb(56, 255, 255, 255);
    private static readonly Color GhostPillBorderHover = Color.FromArgb(102, 255, 255, 255);
    private static readonly Color GhostPillForeground = Color.FromArgb(255, 0xFB, 0xF6, 0xEC);

    private static Button CreateGhostPillCta(HeroCarouselSlide slide)
    {
        SolidColorBrush bgBrush = new(GhostPillRest);
        SolidColorBrush borderBrush = new(GhostPillBorderRest);

        TextBlock label = new()
        {
            Text = slide.CtaText,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };

        FontIcon chevron = new()
        {
            Glyph = GhostPillChevronGlyph,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(),
        };

        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(label);
        content.Children.Add(chevron);

        Button button = new()
        {
            Content = content,
            Padding = new Thickness(26, 14, 26, 14),
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = new SolidColorBrush(GhostPillForeground),
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            MinWidth = 0,
            MinHeight = 0,
            RenderTransform = new TranslateTransform(),
        };

        button.PointerEntered += (_, _) =>
        {
            bgBrush.Color = GhostPillHover;
            borderBrush.Color = GhostPillBorderHover;
            AnimateTranslation(button, "Y", 0, -1, 180);
            AnimateTranslation(chevron, "X", 0, 3, 180);
        };
        button.PointerExited += (_, _) =>
        {
            bgBrush.Color = GhostPillRest;
            borderBrush.Color = GhostPillBorderRest;
            AnimateTranslation(button, "Y", -1, 0, 180);
            AnimateTranslation(chevron, "X", 3, 0, 180);
        };
        button.PointerCanceled += (_, _) =>
        {
            bgBrush.Color = GhostPillRest;
            borderBrush.Color = GhostPillBorderRest;
            AnimateTranslation(button, "Y", -1, 0, 180);
            AnimateTranslation(chevron, "X", 3, 0, 180);
        };

        return button;
    }

    private static void AnimateTranslation(UIElement target, string axis, double from, double to, double durationMs)
    {
        // The button's RenderTransform may be a CompositeTransform (set by
        // PrepareParallaxLayer so the carousel's parallax can write
        // TranslateX) or a TranslateTransform (set by CreateGhostPillCta
        // before parallax wiring). Animate whichever it is — keeping
        // parallax compatible.
        DependencyObject? animationTarget = null;
        string property = axis == "X" ? "X" : "Y";

        if (target.RenderTransform is CompositeTransform composite)
        {
            animationTarget = composite;
            property = axis == "X" ? "TranslateX" : "TranslateY";
        }
        else if (target.RenderTransform is TranslateTransform translate)
        {
            animationTarget = translate;
        }
        else
        {
            TranslateTransform fresh = new();
            target.RenderTransform = fresh;
            animationTarget = fresh;
        }

        DoubleAnimation animation = new()
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard storyboard = new();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, animationTarget);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Begin();
    }

    private static Color Mix(Color first, Color second, double amount, byte alpha)
    {
        amount = Math.Clamp(amount, 0, 1);

        return Color.FromArgb(
            alpha,
            (byte)Math.Round(first.R + (second.R - first.R) * amount),
            (byte)Math.Round(first.G + (second.G - first.G) * amount),
            (byte)Math.Round(first.B + (second.B - first.B) * amount));
    }

    private static Grid CreateRating(HeroCarouselRating rating, Brush primaryBrush, Brush secondaryBrush)
    {
        Grid root = new();

        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Border pegi = new()
        {
            Width = 34,
            Height = 44,
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(rating.Color),
            BorderThickness = new Thickness(2.5),
            CornerRadius = new CornerRadius(3),
        };

        Grid pegiGrid = new();
        pegiGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pegiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock age = new()
        {
            Text = rating.Age.ToString(CultureInfo.CurrentCulture),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 17, 17, 17)),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Border labelBackground = new()
        {
            Background = new SolidColorBrush(rating.Color),
            Padding = new Thickness(0, 2, 0, 2),
            Child = new TextBlock
            {
                Text = "PEGI",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 7,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        Grid.SetRow(age, 0);
        Grid.SetRow(labelBackground, 1);
        pegiGrid.Children.Add(age);
        pegiGrid.Children.Add(labelBackground);
        pegi.Child = pegiGrid;

        StackPanel ratingText = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0,
        };

        ratingText.Children.Add(new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, "PEGI {0}", rating.Age),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primaryBrush,
        });

        ratingText.Children.Add(new TextBlock
        {
            Text = rating.Text,
            FontSize = 12,
            LineHeight = 16,
            Foreground = secondaryBrush,
        });

        Grid.SetColumn(pegi, 0);
        Grid.SetColumn(ratingText, 1);
        root.Children.Add(pegi);
        root.Children.Add(ratingText);

        return root;
    }

    private static CanvasControl CreateColorWash(HeroCarouselSlide slide)
    {
        CanvasControl canvas = new()
        {
            ClearColor = Colors.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false,
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        canvas.Draw += (sender, args) => DrawSlideColorWash(sender, args, slide);
        canvas.SizeChanged += (sender, _) => ((CanvasControl)sender).Invalidate();
        AutomationProperties.SetAccessibilityView(canvas, AccessibilityView.Raw);

        return canvas;
    }

    private static void DrawSlideColorWash(CanvasControl sender, CanvasDrawEventArgs args, HeroCarouselSlide slide)
    {
        if (sender.ActualWidth <= 0 || sender.ActualHeight <= 0)
        {
            return;
        }

        Color baseColor = slide.GlowColor;
        Color accentColor = slide.AccentColor;
        if (baseColor.A == 0 && accentColor.A == 0)
        {
            return;
        }

        Color washBase = Mix(baseColor, Colors.Black, 0.72, 255);
        Color washAccent = Mix(accentColor, Colors.Black, 0.64, accentColor.A);
        float strength = slide.UseDarkText ? 0.88f : slide.UseScrim ? 1.04f : 0.92f;
        float warmth = slide.UseDarkText ? 0.06f : 0.12f;

        PixelShaderEffect<LeftColorWashShader> effect = new()
        {
            ConstantBuffer = new LeftColorWashShader(
                (float)sender.ActualWidth,
                (float)sender.ActualHeight,
                washBase.R / 255.0f,
                washBase.G / 255.0f,
                washBase.B / 255.0f,
                washAccent.R / 255.0f,
                washAccent.G / 255.0f,
                washAccent.B / 255.0f,
                strength,
                warmth),
        };

        args.DrawingSession.DrawImage(effect);
    }

    private void UpdateLayoutForStageSize()
    {
        if (_stageWidth <= 0 || _stageHeight <= 0)
        {
            return;
        }

        BgTrack.Width = _stageWidth * TrackItemCount;
        BgTrack.Height = _stageHeight;
        ContentTrack.Width = _stageWidth * TrackItemCount;
        ContentTrack.Height = _stageHeight;
        GlowCanvasA.Width = _stageWidth + GlowBleed;
        GlowCanvasA.Height = _stageHeight + GlowBleed;
        GlowCanvasB.Width = _stageWidth + GlowBleed;
        GlowCanvasB.Height = _stageHeight + GlowBleed;
        SpotlightCanvas.Width = _stageWidth;
        SpotlightCanvas.Height = _stageHeight;

        double contentInset = GetContentInset();

        for (int i = 0; i < BgTrack.Children.Count; i++)
        {
            if (BgTrack.Children[i] is FrameworkElement backgroundSlide)
            {
                backgroundSlide.Width = _stageWidth;
                backgroundSlide.Height = _stageHeight;
                backgroundSlide.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _stageWidth, _stageHeight) };
            }
        }

        foreach (CanvasControl wash in _washCanvases)
        {
            wash.Width = _stageWidth;
            wash.Height = _stageHeight;
            wash.Invalidate();
        }

        foreach (FrameworkElement hero in _heroLayers)
        {
            hero.Width = _stageWidth * (1 + HeroOverscanRatio * 2);
            hero.Height = _stageHeight * (1 + HeroOverscanRatio * 2);

            if (hero is Grid heroGrid &&
                heroGrid.Children.Count > 0)
            {
                foreach (UIElement child in heroGrid.Children)
                {
                    if (child is FrameworkElement layer)
                    {
                        layer.Width = hero.Width;
                        layer.Height = hero.Height;
                    }
                }
            }
        }

        for (int i = 0; i < ContentTrack.Children.Count; i++)
        {
            if (ContentTrack.Children[i] is Grid contentSlide)
            {
                contentSlide.Width = _stageWidth;
                contentSlide.Height = _stageHeight;
                contentSlide.Padding = new Thickness(contentInset, 0, 0, 0);
                contentSlide.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _stageWidth, _stageHeight) };
            }
        }

        UpdateCardWidths();
    }

    private void CacheCompositionVisuals()
    {
        _backgroundTrackVisual = ElementCompositionPreview.GetElementVisual(BgTrack);
        _contentTrackVisual = ElementCompositionPreview.GetElementVisual(ContentTrack);
        _compositor = _backgroundTrackVisual.Compositor;
        _heroLayerVisuals.Clear();

        foreach (FrameworkElement hero in _heroLayers)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(hero);
            visual.CenterPoint = new Vector3((float)(hero.Width / 2), (float)(hero.Height / 2), 0);
            _heroLayerVisuals.Add(visual);
        }

        // Do not cache composition visuals for content cards or their text/CTA
        // layers. Setting Visual.Offset overrides the layout offset captured by
        // StackPanel/Grid measure, which makes text and buttons drift or pile up
        // during scroll. The RenderTransform path below composes with layout.

        CacheButtonVisual(PrevButton);
        CacheButtonVisual(NextButton);
    }

    private static void CacheButtonVisual(Button button)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(button);
        visual.CenterPoint = new Vector3((float)(button.Width / 2), (float)(button.Height / 2), 0);
    }

    private void SetTransforms(bool noAnimation)
    {
        double offset = GetRealTrackOffsetForLogicalIndex(_currentIndex);
        _baseTransform = offset;
        SetTrackOffset(offset, !noAnimation, TimeSpan.FromMilliseconds(TrackAnimationMs));
    }

    private int WrapLogicalIndex(int index)
    {
        if (SlideCount <= 0)
        {
            return 0;
        }

        int wrapped = index % SlideCount;

        return wrapped < 0 ? wrapped + SlideCount : wrapped;
    }

    private int GetLogicalIndexForTrackIndex(int trackIndex)
    {
        if (!IsLooping)
        {
            return Math.Clamp(trackIndex, 0, Math.Max(0, SlideCount - 1));
        }

        return WrapLogicalIndex(trackIndex);
    }

    private int GetTrackIndexForLogicalIndex(int logicalIndex)
    {
        return IsLooping ? SlideCount + WrapLogicalIndex(logicalIndex) : Math.Clamp(logicalIndex, 0, Math.Max(0, SlideCount - 1));
    }

    private int GetNavigationTrackIndex(int requestedIndex)
    {
        if (!IsLooping)
        {
            return Math.Clamp(requestedIndex, 0, Math.Max(0, SlideCount - 1));
        }

        if (requestedIndex < 0)
        {
            return SlideCount - 1;
        }

        if (requestedIndex >= SlideCount)
        {
            return SlideCount * 2;
        }

        return SlideCount + requestedIndex;
    }

    private double GetRealTrackOffsetForLogicalIndex(int logicalIndex)
    {
        return GetTrackOffsetForTrackIndex(GetTrackIndexForLogicalIndex(logicalIndex));
    }

    private double GetTrackOffsetForTrackIndex(int trackIndex)
    {
        return -trackIndex * _stageWidth;
    }

    private double NormalizeLoopOffset(double offset)
    {
        if (!IsLooping || _stageWidth <= 0)
        {
            return offset;
        }

        double progress = -offset / _stageWidth;
        double normalizedProgress = progress;

        double minimumMiddleProgress = SlideCount;
        double maximumMiddleProgress = SlideCount * 2;

        if (progress < minimumMiddleProgress)
        {
            while (normalizedProgress < minimumMiddleProgress)
            {
                normalizedProgress += SlideCount;
            }
        }
        else if (progress >= maximumMiddleProgress)
        {
            while (normalizedProgress >= maximumMiddleProgress)
            {
                normalizedProgress -= SlideCount;
            }
        }

        return -normalizedProgress * _stageWidth;
    }

    private double NormalizeLoopOffsetForInteraction(double offset)
    {
        double normalizedOffset = NormalizeLoopOffset(offset);

        if (Math.Abs(normalizedOffset - offset) > 0.5)
        {
            SetTrackOffset(normalizedOffset, false, TimeSpan.Zero);
            Debug.WriteLine($"HC_LOOP normalize from={offset:F2} to={normalizedOffset:F2}");
        }

        return normalizedOffset;
    }

    private int GetNearestTrackIndex(double offset)
    {
        return Math.Clamp((int)Math.Round(-offset / _stageWidth), 0, Math.Max(0, TrackItemCount - 1));
    }

    private void GoTo(int requestedIndex)
    {
        if (!_isLoaded || SlideCount == 0)
        {
            _currentIndex = Math.Max(0, requestedIndex);
            return;
        }

        RestartAutoAdvanceTimer();

        int activeIndex = GetActiveIndex();
        int target = WrapLogicalIndex(requestedIndex);

        if (target == activeIndex && requestedIndex >= 0 && requestedIndex < SlideCount)
        {
            if (!_snapRendering)
            {
                SetTransforms(false);
            }

            return;
        }

        CancelSnapAnimation();
        CancelWheelSettle();
        _isWheeling = false;
        _scrollOffset = 0;

        int activeTrackIndex = GetTrackIndexForLogicalIndex(activeIndex);
        int targetTrackIndex = GetNavigationTrackIndex(requestedIndex);
        double from = _backgroundTrackVisual?.Offset.X ?? GetRealTrackOffsetForLogicalIndex(_currentIndex);
        double to = GetTrackOffsetForTrackIndex(targetTrackIndex);

        _scrollDirection = Math.Sign(targetTrackIndex - activeTrackIndex);
        _baseTransform = to;
        ApplyGlow(target, false);
        StartSnapAnimation(
            from,
            to,
            target,
            target != activeIndex || targetTrackIndex != GetTrackIndexForLogicalIndex(target),
            TrackAnimationMs);
    }

    private void SetTrackOffset(double offset, bool animate, TimeSpan duration)
    {
        if (_backgroundTrackVisual is null || _contentTrackVisual is null)
        {
            return;
        }

        Vector3 target = new((float)offset, 0, 0);

        if (animate && _compositor is not null)
        {
            double from = _backgroundTrackVisual.Offset.X;
            StartTrackOffsetAnimation(from, offset, duration.TotalMilliseconds);
        }
        else
        {
            _backgroundTrackVisual.StopAnimation(nameof(Visual.Offset));
            _contentTrackVisual.StopAnimation(nameof(Visual.Offset));
            _backgroundTrackVisual.Offset = target;
            _contentTrackVisual.Offset = target;
            UpdateSlideEffects(offset);
        }
    }

    private void UpdateSlideEffects(double trackOffset)
    {
        if (_stageWidth <= 0)
        {
            return;
        }

        double progress = -trackOffset / _stageWidth;
        double segmentProgress = progress - Math.Floor(progress);
        double counterPx = Math.Sin(segmentProgress * Math.PI) * TextParallaxAmplitude * _stageWidth;

        UpdatePipIndicatorForTrackProgress(progress);

        for (int i = 0; i < _heroLayers.Count; i++)
        {
            double rawDistance = Math.Abs(i - progress);
            double distance = Math.Min(rawDistance, 1);
            bool isOutgoing = IsOutgoingSlide(i, progress);
            double heroScale = 1 - (isOutgoing ? LeftHeroScaleFactor : RightHeroScaleFactor) * distance;

            if (i < _washCanvases.Count)
            {
                double visibility = UseColorWash ? Math.Pow(1 - distance, 0.72) : 0;
                _washCanvases[i].Opacity = visibility;
            }

            if (i < _heroLayerVisuals.Count)
            {
                _heroLayerVisuals[i].Scale = new Vector3((float)heroScale, (float)heroScale, 1);
            }
            else if (_heroLayers[i].RenderTransform is CompositeTransform heroTransform)
            {
                heroTransform.ScaleX = heroScale;
                heroTransform.ScaleY = heroScale;
            }
        }

        for (int i = 0; i < _contentCards.Count; i++)
        {
            double rawDistance = Math.Abs(i - progress);
            double distance = Math.Min(rawDistance, 1);
            bool isOutgoing = IsOutgoingSlide(i, progress);
            double scale = 1 - (isOutgoing ? LeftCardScaleFactor : RightCardScaleFactor) * distance;

            double baseExtraX = 0;

            if (_scrollDirection != 0)
            {
                if (isOutgoing && rawDistance < 1.5)
                {
                    baseExtraX = _scrollDirection * counterPx;
                }
            }

            double cardExtraX = baseExtraX * ContentCardParallaxFactor;
            double layerExtraX = baseExtraX * ContentLayerParallaxFactor;

            if (_contentCards[i].RenderTransform is CompositeTransform contentTransform)
            {
                contentTransform.TranslateX = cardExtraX;
                contentTransform.ScaleX = scale;
                contentTransform.ScaleY = scale;
            }

            // Keep child layers fixed inside their StackPanel. Moving/scaling the
            // whole card gives depth; child-layer parallax is applied through
            // RenderTransform so it composes with StackPanel layout instead of
            // replacing it with composition offsets.
            if (i < _contentCardLayers.Count)
            {
                ContentCardLayers layers = _contentCardLayers[i];
                SetLayerTransform(layers.Tag, layerExtraX * 0.65, 1);
                SetLayerTransform(layers.Title, layerExtraX * 0.85, 1);
                SetLayerTransform(layers.Subtitle, layerExtraX, 1);
                SetLayerTransform(layers.Cta, layerExtraX * 1.15, 1);
                SetLayerTransform(layers.Rating, layerExtraX * 1.15, 1);
            }
        }
    }

    private bool IsOutgoingSlide(int slideIndex, double progress)
    {
        if (_scrollDirection > 0)
        {
            return slideIndex < progress;
        }

        if (_scrollDirection < 0)
        {
            return slideIndex > progress;
        }

        return slideIndex < progress;
    }

    private static void SetLayerTransform(FrameworkElement? element, double x, double scale)
    {
        if (element?.RenderTransform is CompositeTransform transform)
        {
            transform.TranslateX = x;
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }
    }

    private void UpdateCardWidths()
    {
        foreach (UIElement child in ContentTrack.Children)
        {
            if (child is Grid slide &&
                slide.Children.Count > 0 &&
                slide.Children[0] is StackPanel card)
            {
                card.MaxWidth = _stageWidth <= CompactBreakpoint ? 220 : 380;
            }
        }
    }

    private void ApplyGlow(int index, bool immediate)
    {
        if (!UseGlow || SlideCount == 0 || _activeGlow is null)
        {
            GlowCanvasA.Opacity = 0;
            GlowCanvasB.Opacity = 0;
            return;
        }

        index = WrapLogicalIndex(index);
        Color glowColor = GetSlide(index).GlowColor;
        CanvasControl next = ReferenceEquals(_activeGlow, GlowCanvasA) ? GlowCanvasB : GlowCanvasA;

        if (ReferenceEquals(next, GlowCanvasA))
        {
            _glowColorA = glowColor;
        }
        else
        {
            _glowColorB = glowColor;
        }

        next.Invalidate();

        if (immediate)
        {
            GlowCanvasA.Opacity = ReferenceEquals(next, GlowCanvasA) ? 1 : 0;
            GlowCanvasB.Opacity = ReferenceEquals(next, GlowCanvasB) ? 1 : 0;
        }
        else
        {
            AnimateOpacity(next, 1, TimeSpan.FromMilliseconds(GlowAnimationMs));
            AnimateOpacity(_activeGlow, 0, TimeSpan.FromMilliseconds(GlowAnimationMs));
        }

        _activeGlow = next;
    }

    private void UpdatePips()
    {
        int pageCount = Math.Max(1, SlideCount);
        int selectedIndex = Math.Clamp(_currentIndex, 0, pageCount - 1);
        Visibility pipsVisibility = GetEffectivePipsVisibility();

        _updatingPips = true;

        try
        {
            SlidePips.NumberOfPages = pageCount;
            SlidePips.SelectedPageIndex = selectedIndex;
        }
        finally
        {
            _updatingPips = false;
        }

        PipsPresenter.Width = Math.Max(PipSlotWidth, pageCount * PipSlotWidth);
        PipsPresenter.Visibility = pipsVisibility;
        SlidePips.Visibility = pipsVisibility;
        AnimatedSelectedPip.Visibility = pipsVisibility;
        AnimatedSelectedPip.Opacity = pipsVisibility == Visibility.Visible ? 1 : 0;
        UpdateAnimatedPipIndicator(selectedIndex, animate: false);
    }

    private void UpdateChromeVisibility()
    {
        Visibility navigationVisibility = ShowNavigationButtons && SlideCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.Visibility = navigationVisibility;
        NextButton.Visibility = navigationVisibility;

        if (navigationVisibility == Visibility.Collapsed)
        {
            PrevButton.Opacity = 0;
            NextButton.Opacity = 0;
        }

        Visibility pipsVisibility = GetEffectivePipsVisibility();
        PipsPresenter.Visibility = pipsVisibility;
        SlidePips.Visibility = pipsVisibility;
        AnimatedSelectedPip.Visibility = pipsVisibility;
        AnimatedSelectedPip.Opacity = pipsVisibility == Visibility.Visible ? 1 : 0;
        SpotlightCanvas.Visibility = UseSpotlight ? Visibility.Visible : Visibility.Collapsed;

        if (!UseSpotlight)
        {
            SpotlightCanvas.Opacity = 0;
            _spotlightOpacity = 0;
        }

        if (!UseGlow)
        {
            GlowCanvasA.Opacity = 0;
            GlowCanvasB.Opacity = 0;
        }
    }

    private Visibility GetEffectivePipsVisibility()
    {
        if (!ShowPips || SlideCount <= 1)
        {
            return Visibility.Collapsed;
        }

        return PipsVisibility;
    }

    private void UpdatePipIndicatorForTrackProgress(double trackProgress)
    {
        if (SlideCount <= 1 || GetEffectivePipsVisibility() != Visibility.Visible)
        {
            return;
        }

        double pipProgress = GetPipProgress(trackProgress);

        UpdateAnimatedPipIndicator(pipProgress, animate: false);
    }

    private double GetPipProgress(double trackProgress)
    {
        if (SlideCount <= 1)
        {
            return 0;
        }

        if (!IsLooping)
        {
            return Math.Clamp(trackProgress, 0, SlideCount - 1);
        }

        double middleProgress = trackProgress - SlideCount;
        double lastIndex = SlideCount - 1;

        if (middleProgress < 0)
        {
            return Math.Clamp(-middleProgress * lastIndex, 0, lastIndex);
        }

        if (middleProgress > lastIndex)
        {
            double wrapProgress = Math.Clamp(middleProgress - lastIndex, 0, 1);

            return (1 - wrapProgress) * lastIndex;
        }

        return middleProgress;
    }

    private void UpdateAnimatedPipIndicator(double selectedIndex, bool animate)
    {
        if (AnimatedSelectedPip.RenderTransform is not CompositeTransform transform)
        {
            transform = new CompositeTransform();
            AnimatedSelectedPip.RenderTransform = transform;
        }

        (double x, double width) = GetPipIndicatorGeometry(selectedIndex);
        AnimatedSelectedPip.Width = width;
        transform.TranslateX = x;
    }

    private (double X, double Width) GetPipIndicatorGeometry(double pipProgress)
    {
        if (SlideCount <= 1)
        {
            return (0, PipIndicatorWidth);
        }

        double lastIndex = SlideCount - 1;
        double progress = Math.Clamp(pipProgress, 0, lastIndex);
        double lower = Math.Floor(progress);
        double upper = Math.Ceiling(progress);
        double fraction = progress - lower;

        if (Math.Abs(fraction) < 0.0001 || Math.Abs(upper - lower) < 0.0001)
        {
            return (lower * PipSlotWidth, PipIndicatorWidth);
        }

        if (_scrollDirection < 0)
        {
            double reverseFraction = 1 - fraction;
            double leading = upper - SmoothStep(0.0, 0.56, reverseFraction);
            double trailing = upper + 1 - SmoothStep(0.38, 1.0, reverseFraction);
            double x = leading * PipSlotWidth;
            double width = Math.Max(PipIndicatorWidth, trailing * PipSlotWidth - x);

            return (x, width);
        }

        double left = lower + SmoothStep(0.38, 1.0, fraction);
        double right = lower + (PipIndicatorWidth / PipSlotWidth) + SmoothStep(0.0, 0.56, fraction);
        double targetX = left * PipSlotWidth;
        double targetWidth = Math.Max(PipIndicatorWidth, right * PipSlotWidth - targetX);

        return (targetX, targetWidth);
    }

    private void UpdateAutoAdvanceTimer()
    {
        if (!_isLoaded || !IsAutoAdvanceEnabled || SlideCount <= 1)
        {
            StopAutoAdvanceTimer();
            return;
        }

        TimeSpan interval = AutoAdvanceInterval <= TimeSpan.Zero
            ? DefaultAutoAdvanceInterval
            : AutoAdvanceInterval;

        if (interval < TimeSpan.FromSeconds(1))
        {
            interval = TimeSpan.FromSeconds(1);
        }

        _autoAdvanceTimer ??= CreateAutoAdvanceTimer();
        _autoAdvanceTimer.Stop();
        _autoAdvanceTimer.Interval = interval;
        _autoAdvanceTimer.Start();
    }

    private DispatcherQueueTimer CreateAutoAdvanceTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = true;
        timer.Tick += OnAutoAdvanceTimerTick;

        return timer;
    }

    private void StopAutoAdvanceTimer()
    {
        if (_autoAdvanceTimer is null)
        {
            return;
        }

        _autoAdvanceTimer.Stop();
        _autoAdvanceTimer.Tick -= OnAutoAdvanceTimerTick;
        _autoAdvanceTimer = null;
    }

    private void RestartAutoAdvanceTimer()
    {
        if (_autoAdvanceTimer is null)
        {
            return;
        }

        _autoAdvanceTimer.Stop();
        _autoAdvanceTimer.Start();
    }

    private void OnAutoAdvanceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isLoaded || !IsAutoAdvanceEnabled || SlideCount <= 1)
        {
            StopAutoAdvanceTimer();
            return;
        }

        if (PauseAutoAdvanceOnInteraction && IsAutoAdvancePausedByInteraction())
        {
            RestartAutoAdvanceTimer();
            return;
        }

        GoTo(GetActiveIndex() + 1);
    }

    private bool IsAutoAdvancePausedByInteraction()
    {
        return _isPointerOverStage ||
            _isWheeling ||
            _isPointerDragging ||
            _snapRendering ||
            _contactTracker.HasActiveContact;
    }

    private void AnimateOpacity(UIElement element, double opacity, TimeSpan duration)
    {
        if (_compositor is null)
        {
            element.Opacity = opacity;
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        ScalarKeyFrameAnimation animation = _compositor.CreateScalarKeyFrameAnimation();
        CubicBezierEasingFunction easing = _compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.25f, 0.10f),
            new Vector2(0.25f, 1.0f));
        animation.InsertKeyFrame(1f, (float)opacity, easing);
        animation.Duration = duration;
        visual.StartAnimation(nameof(Visual.Opacity), animation);
    }

    private void OnStagePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverStage = true;

        if (UseSpotlight)
        {
            _spotlightOpacity = 1;
            AnimateOpacity(SpotlightCanvas, 1, TimeSpan.FromMilliseconds(250));
            SpotlightCanvas.Invalidate();
        }

        if (ShowNavigationButtons && SlideCount > 1)
        {
            AnimateOpacity(PrevButton, 1, TimeSpan.FromMilliseconds(NavAnimationMs));
            AnimateOpacity(NextButton, 1, TimeSpan.FromMilliseconds(NavAnimationMs));
        }
    }

    private void OnStagePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverStage = false;
        RestartAutoAdvanceTimer();

        if (UseSpotlight)
        {
            _spotlightOpacity = 0;
            AnimateOpacity(SpotlightCanvas, 0, TimeSpan.FromMilliseconds(250));
            SpotlightCanvas.Invalidate();
        }

        if (ShowNavigationButtons && SlideCount > 1)
        {
            AnimateOpacity(PrevButton, 0, TimeSpan.FromMilliseconds(NavAnimationMs));
            AnimateOpacity(NextButton, 0, TimeSpan.FromMilliseconds(NavAnimationMs));
        }
    }

    private void OnStagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isPointerDragging &&
            e.Pointer.PointerId == _dragPointerId)
        {
            UpdatePointerDrag(e);
            return;
        }

        Point position = e.GetCurrentPoint(StageRoot).Position;
        _spotlightX = (float)position.X;
        _spotlightY = (float)position.Y;

        if (UseSpotlight)
        {
            SpotlightCanvas.Invalidate();
        }
    }

    private void OnStagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(StageRoot);

        if ((point.PointerDeviceType != PointerDeviceType.Touch &&
             point.PointerDeviceType != PointerDeviceType.Pen) ||
            SlideCount == 0 ||
            _stageWidth <= 0 ||
            IsFromInteractiveControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _isPointerDragging = true;
        _pointerDragAccepted = false;
        _dragPointerId = e.Pointer.PointerId;
        _dragStartX = point.Position.X;
        _dragStartY = point.Position.Y;
        _dragLastX = _dragStartX;
        _dragVelocityX = 0;
        _dragLastTimestamp = Stopwatch.GetTimestamp();
        CancelWheelSettle();

        double currentOffset = _backgroundTrackVisual?.Offset.X ?? GetRealTrackOffsetForLogicalIndex(_currentIndex);

        if (_snapRendering)
        {
            CancelSnapAnimation();
        }

        _dragStartOffset = NormalizeLoopOffsetForInteraction(currentOffset);
        _contactTracker.NotifyPointerPressed(e);

        StageRoot.CapturePointer(e.Pointer);
    }

    private void OnStagePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompletePointerDrag(e);
    }

    private void OnStagePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        CancelPointerDrag(e);
    }

    private void OnStagePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CancelPointerDrag(e);
    }

    private void UpdatePointerDrag(PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(StageRoot);
        double deltaX = point.Position.X - _dragStartX;
        double deltaY = point.Position.Y - _dragStartY;

        if (!_pointerDragAccepted)
        {
            if (Math.Abs(deltaX) < TouchDragThreshold &&
                Math.Abs(deltaY) < TouchDragThreshold)
            {
                return;
            }

            if (Math.Abs(deltaY) > Math.Abs(deltaX))
            {
                CancelPointerDrag(e);
                return;
            }

            _pointerDragAccepted = true;
            _isWheeling = true;
            CancelSnapAnimation();
            _dragStartOffset = NormalizeLoopOffsetForInteraction(_dragStartOffset);
            _baseTransform = _dragStartOffset;
            _scrollOffset = 0;
            _backgroundTrackVisual?.StopAnimation(nameof(Visual.Offset));
            _contentTrackVisual?.StopAnimation(nameof(Visual.Offset));
        }

        double position = ApplyRubberBand(_dragStartOffset + deltaX);
        _scrollOffset = _baseTransform - position;
        UpdateDragVelocity(point.Position.X);

        if (Math.Abs(_scrollOffset) > 1)
        {
            _scrollDirection = Math.Sign(_scrollOffset);
        }

        SetTrackOffset(position, false, TimeSpan.Zero);
        e.Handled = true;
    }

    private void CompletePointerDrag(PointerRoutedEventArgs e)
    {
        _contactTracker.NotifyPointerReleased(e);

        if (!_isPointerDragging ||
            e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        StageRoot.ReleasePointerCapture(e.Pointer);
        _isPointerDragging = false;

        if (_pointerDragAccepted)
        {
            _pointerDragAccepted = false;
            double releaseVelocity = GetReleaseDragVelocity();
            _dragVelocityX = 0;
            EndWheelAndSnap(releaseVelocity);
            e.Handled = true;
        }
    }

    private void CancelPointerDrag(PointerRoutedEventArgs e)
    {
        _contactTracker.NotifyPointerCanceled(e);

        if (!_isPointerDragging ||
            e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        StageRoot.ReleasePointerCapture(e.Pointer);
        _isPointerDragging = false;
        _pointerDragAccepted = false;

        if (_isWheeling)
        {
            CancelWheelSettle();
            _isWheeling = false;
            _scrollOffset = 0;
            _scrollDirection = 0;
            _dragVelocityX = 0;
            SetTransforms(false);
        }
    }

    private void UpdateDragVelocity(double currentX)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _dragLastTimestamp) / (double)Stopwatch.Frequency;

        if (elapsed > 0.001)
        {
            double velocity = (currentX - _dragLastX) / elapsed;
            _dragVelocityX = _dragVelocityX == 0 ? velocity : _dragVelocityX * 0.65 + velocity * 0.35;
            _dragLastX = currentX;
            _dragLastTimestamp = now;
        }
    }

    private double GetReleaseDragVelocity()
    {
        double idleSeconds = (Stopwatch.GetTimestamp() - _dragLastTimestamp) / (double)Stopwatch.Frequency;

        if (idleSeconds <= 0)
        {
            return _dragVelocityX;
        }

        return _dragVelocityX * Math.Exp(-idleSeconds * 9.0);
    }

    private static bool IsFromInteractiveControl(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    private void OnStagePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (SlideCount == 0 || _stageWidth <= 0)
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(StageRoot).Properties;

        if (!properties.IsHorizontalMouseWheel)
        {
            return;
        }

        e.Handled = true;
        double deltaX = properties.MouseWheelDelta * TrackpadWheelDeltaScale;

        if (Math.Abs(deltaX) < 0.1)
        {
            return;
        }

        if (!_isWheeling || _snapRendering)
        {
            _isWheeling = true;
            BeginInteractiveScroll();
        }

        _scrollOffset += deltaX;

        if (Math.Abs(_scrollOffset) > 1)
        {
            _scrollDirection = Math.Sign(_scrollOffset);
        }

        double position = ApplyRubberBand(_baseTransform - _scrollOffset);

        LogWheel(
            $"delta raw={properties.MouseWheelDelta:F2} scaled={deltaX:F2} scrollOffset={_scrollOffset:F2} " +
            $"base={_baseTransform:F2} position={position:F2} dir={_scrollDirection} " +
            $"{_contactTracker.CurrentInputMessageSourceDiagnostics} {_contactTracker.Diagnostics}");

        SetTrackOffset(position, false, TimeSpan.Zero);
        QueueWheelSettle();
    }

    private void BeginInteractiveScroll()
    {
        CancelWheelSettle();
        double currentOffset = _backgroundTrackVisual?.Offset.X ?? GetRealTrackOffsetForLogicalIndex(_currentIndex);
        CancelSnapAnimation();
        _baseTransform = NormalizeLoopOffsetForInteraction(currentOffset);
        _scrollOffset = 0;
        _backgroundTrackVisual?.StopAnimation(nameof(Visual.Offset));
        _contentTrackVisual?.StopAnimation(nameof(Visual.Offset));
        Debug.WriteLine($"HC_SNAP begin-interactive current={_currentIndex} base={_baseTransform:F2} {_contactTracker.Diagnostics}");
    }

    private double ApplyRubberBand(double position)
    {
        double minPosition = -(TrackItemCount - 1) * _stageWidth;
        double maxPosition = 0;

        if (position > maxPosition)
        {
            return maxPosition + (position - maxPosition) * 0.35;
        }

        if (position < minPosition)
        {
            return minPosition + (position - minPosition) * 0.35;
        }

        return position;
    }

    private void QueueWheelSettle()
    {
        if (_wheelSettleQueued)
        {
            return;
        }

        _wheelSettleQueued = true;
        CompositionTarget.Rendering += OnWheelSettleRendering;
    }

    private void CancelWheelSettle()
    {
        if (!_wheelSettleQueued)
        {
            return;
        }

        CompositionTarget.Rendering -= OnWheelSettleRendering;
        _wheelSettleQueued = false;
    }

    private void OnWheelSettleRendering(object? sender, object e)
    {
        CancelWheelSettle();
        Debug.WriteLine($"HC_SNAP frame-settle isWheeling={_isWheeling} scrollOffset={_scrollOffset:F2} {_contactTracker.Diagnostics}");
        EndWheelAndSnap();
    }

    private void EndWheelAndSnap(double? releaseVelocityX = null)
    {
        if (!_isWheeling || _stageWidth <= 0)
        {
            Debug.WriteLine($"HC_SNAP end-ignored isWheeling={_isWheeling} stageWidth={_stageWidth:F2} {_contactTracker.Diagnostics}");
            return;
        }

        double finalPosition = _backgroundTrackVisual?.Offset.X ?? _baseTransform - _scrollOffset;
        double projectedPosition = finalPosition;

        if (releaseVelocityX is double velocityX)
        {
            projectedPosition = finalPosition + velocityX * 0.12;
        }

        int targetTrackIndex;

        if (IsLooping)
        {
            targetTrackIndex = GetNearestTrackIndex(projectedPosition);
        }
        else if (projectedPosition > 0)
        {
            targetTrackIndex = 0;
        }
        else
        {
            double minPosition = -(TrackItemCount - 1) * _stageWidth;
            targetTrackIndex = projectedPosition < minPosition
                ? TrackItemCount - 1
                : GetNearestTrackIndex(projectedPosition);
        }

        int target = GetLogicalIndexForTrackIndex(targetTrackIndex);
        _isWheeling = false;
        _scrollOffset = 0;
        int previousIndex = _currentIndex;

        _baseTransform = GetTrackOffsetForTrackIndex(targetTrackIndex);
        Debug.WriteLine(
            $"HC_SNAP end target={target} track={targetTrackIndex} previous={previousIndex} finalPosition={finalPosition:F2} " +
            $"projected={projectedPosition:F2} velocity={releaseVelocityX.GetValueOrDefault():F2} " +
            $"to={_baseTransform:F2} distance={Math.Abs(_baseTransform - finalPosition):F2} {_contactTracker.Diagnostics}");
        ApplyGlow(target, false);
        StartSnapAnimation(
            finalPosition,
            _baseTransform,
            target,
            target != previousIndex || targetTrackIndex != GetTrackIndexForLogicalIndex(target),
            releaseVelocityX is double snapVelocity ? GetTouchSnapDuration(Math.Abs(_baseTransform - finalPosition), Math.Abs(snapVelocity)) : null);
    }

    private void StartSnapAnimation(
        double from,
        double to,
        int targetIndex,
        bool commitCurrentIndexOnComplete,
        double? durationMs = null)
    {
        CancelSnapAnimation();

        double distance = Math.Abs(to - from);
        bool snapBack = !commitCurrentIndexOnComplete;
        _snapFrom = from;
        _snapTo = to;
        _snapDurationMs = durationMs ?? (snapBack ? GetSnapBackDuration(distance) : GetSnapDuration(distance));
        Debug.WriteLine(
            $"HC_SNAP start from={from:F2} to={to:F2} distance={distance:F2} duration={_snapDurationMs:F0} " +
            $"target={targetIndex} commit={commitCurrentIndexOnComplete} dir={_scrollDirection}");
        StartTrackOffsetAnimation(from, to, _snapDurationMs, targetIndex, commitCurrentIndexOnComplete);
    }

    private void StartTrackOffsetAnimation(
        double from,
        double to,
        double durationMs,
        int? targetIndex = null,
        bool commitCurrentIndexOnComplete = false)
    {
        CancelSnapAnimation();

        _snapFrom = from;
        _snapTo = to;
        _snapTargetIndex = targetIndex ?? _currentIndex;
        _commitCurrentIndexOnSnapComplete = commitCurrentIndexOnComplete;
        _snapDurationMs = Math.Max(1, durationMs);
        _snapStartedAt = TimeSpan.Zero;
        _snapRendering = true;
        _snapBackEasing = !commitCurrentIndexOnComplete;
        SetTrackOffset(from, false, TimeSpan.Zero);
        CompositionTarget.Rendering += OnSnapRendering;
    }

    private void CancelSnapAnimation()
    {
        if (!_snapRendering)
        {
            return;
        }

        CompositionTarget.Rendering -= OnSnapRendering;
        _snapRendering = false;
        _commitCurrentIndexOnSnapComplete = false;
        _snapTargetIndex = _currentIndex;
        _snapBackEasing = false;
    }

    private void OnSnapRendering(object? sender, object e)
    {
        if (e is not RenderingEventArgs args)
        {
            return;
        }

        if (_snapStartedAt == TimeSpan.Zero)
        {
            _snapStartedAt = args.RenderingTime;
        }

        double elapsed = (args.RenderingTime - _snapStartedAt).TotalMilliseconds;
        double t = Math.Clamp(elapsed / _snapDurationMs, 0, 1);
        double eased = _snapBackEasing ? EaseOutSoftSpring(t) : EaseOutFluent(t);
        double position = _snapFrom + (_snapTo - _snapFrom) * eased;

        SetTrackOffset(position, false, TimeSpan.Zero);

        if (t >= 1)
        {
            int targetIndex = _snapTargetIndex;
            bool commitCurrentIndex = _commitCurrentIndexOnSnapComplete;

            Debug.WriteLine(
                $"HC_SNAP complete target={targetIndex} commit={commitCurrentIndex} " +
                $"from={_snapFrom:F2} to={_snapTo:F2} current={_currentIndex}");

            CancelSnapAnimation();
            _scrollDirection = 0;
            SetTrackOffset(_snapTo, false, TimeSpan.Zero);

            if (commitCurrentIndex)
            {
                CommitCurrentIndex(targetIndex);
            }
        }
    }

    private void CommitCurrentIndex(int targetIndex)
    {
        int clampedIndex = WrapLogicalIndex(targetIndex);
        bool changed = _currentIndex != clampedIndex;

        _currentIndex = clampedIndex;
        _baseTransform = GetRealTrackOffsetForLogicalIndex(_currentIndex);
        SetTrackOffset(_baseTransform, false, TimeSpan.Zero);
        UpdatePips();

        if (changed)
        {
            CurrentIndexChanged?.Invoke(this, _currentIndex);
            StartContentHighlight();
        }

        Debug.WriteLine($"HC_SNAP commit index={_currentIndex} changed={changed} base={_baseTransform:F2}");
    }

    private void LogWheel(string message)
    {
        if (_wheelDiagnosticsCount >= MaxWheelDiagnostics)
        {
            return;
        }

        _wheelDiagnosticsCount++;
        Debug.WriteLine($"HC_WHEEL {message}");
    }

    private double GetSnapDuration(double distance)
    {
        double normalizedDistance = Math.Clamp(distance / Math.Max(_stageWidth, 1), 0, 1);
        double shapedDistance = Math.Sqrt(normalizedDistance);

        return MinSnapAnimationMs + (MaxSnapAnimationMs - MinSnapAnimationMs) * shapedDistance;
    }

    private double GetSnapBackDuration(double distance)
    {
        double normalizedDistance = Math.Clamp(distance / Math.Max(_stageWidth, 1), 0, 1);

        return Math.Clamp(560 + Math.Sqrt(normalizedDistance) * 260, 520, 860);
    }

    private double GetTouchSnapDuration(double distance, double velocity)
    {
        double normalizedDistance = Math.Clamp(distance / Math.Max(_stageWidth, 1), 0, 1);
        double normalizedVelocity = Math.Clamp(velocity / Math.Max(_stageWidth, 1), 0, 2.2);
        double duration = 560 + Math.Sqrt(normalizedDistance) * 260 - normalizedVelocity * 45;

        return Math.Clamp(duration, 420, 900);
    }

    private static double EaseOutFluent(double t)
    {
        return CubicBezier(t, 0.25, 0.10, 0.25, 1.0);
    }

    private static double EaseOutSoftSpring(double t)
    {
        double settled = CubicBezier(t, 0.18, 0.72, 0.18, 1.0);
        double ring = Math.Sin(t * Math.PI) * Math.Pow(1.0 - t, 2.15) * 0.055;

        return Math.Min(1.035, settled + ring);
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + (to - from) * amount;
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        if (Math.Abs(edge1 - edge0) < 0.000001)
        {
            return x >= edge1 ? 1 : 0;
        }

        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);

        return t * t * (3 - 2 * t);
    }

    private static double CubicBezier(double t, double x1, double y1, double x2, double y2)
    {
        if (t <= 0 || t >= 1)
        {
            return t;
        }

        double x = t;

        for (int i = 0; i < 5; i++)
        {
            double currentX = SampleBezier(x, x1, x2) - t;
            double derivative = SampleBezierDerivative(x, x1, x2);

            if (Math.Abs(derivative) < 0.000001)
            {
                break;
            }

            x -= currentX / derivative;
            x = Math.Clamp(x, 0, 1);
        }

        return SampleBezier(x, y1, y2);
    }

    private static double SampleBezier(double t, double a1, double a2)
    {
        return ((1 - 3 * a2 + 3 * a1) * t + (3 * a2 - 6 * a1)) * t * t + 3 * a1 * t;
    }

    private static double SampleBezierDerivative(double t, double a1, double a2)
    {
        return 3 * (1 - 3 * a2 + 3 * a1) * t * t + 2 * (3 * a2 - 6 * a1) * t + 3 * a1;
    }

    private int GetActiveIndex()
    {
        if (_snapRendering && _commitCurrentIndexOnSnapComplete)
        {
            return Math.Clamp(_snapTargetIndex, 0, SlideCount - 1);
        }

        return _currentIndex;
    }

    private void OnPreviousClicked(object sender, RoutedEventArgs e)
    {
        GoTo(GetActiveIndex() - 1);
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        GoTo(GetActiveIndex() + 1);
    }

    private void OnPipsPagerSelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        if (_updatingPips || sender.SelectedPageIndex == GetActiveIndex())
        {
            return;
        }

        GoTo(sender.SelectedPageIndex);
    }

    private void OnStageRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Left)
        {
            GoTo(GetActiveIndex() - 1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Right)
        {
            GoTo(GetActiveIndex() + 1);
            e.Handled = true;
        }
    }

    private void ApplyAutomationText()
    {
        AutomationProperties.SetName(PrevButton, TryGetResource("HeroCarouselPrevious", "Previous slide"));
        AutomationProperties.SetName(NextButton, TryGetResource("HeroCarouselNext", "Next slide"));
        AutomationProperties.SetAccessibilityView(GlowHost, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(GlowCanvasA, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(GlowCanvasB, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(SpotlightCanvas, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(BgTrack, AccessibilityView.Raw);
    }

    private string TryGetResource(string key, string fallback)
    {
        try
        {
            string value = _resources.GetString(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private void OnStageWrapSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availableWidth = Math.Min(e.NewSize.Width, Math.Max(320.0, MaxStageWidth));

        if (availableWidth <= 0)
        {
            return;
        }

        _stageWidth = availableWidth;
        _stageHeight = availableWidth * StageAspectHeight / StageAspectWidth;
        StageWrap.Height = _stageHeight;

        UpdateLayoutForStageSize();
        CacheCompositionVisuals();
        SetTransforms(true);
        if (UseSpotlight)
        {
            SpotlightCanvas.Invalidate();
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            CacheCompositionVisuals();
            SetTransforms(true);
        });
    }

    private void StartContentHighlight()
    {
        if (!UseButtonReveal || !_isLoaded || SlideCount == 0 || _stageWidth <= 0 || _stageHeight <= 0)
        {
            return;
        }

        CancelContentHighlight();
        _contentHighlightProgress = 0;
        _contentHighlightStartedAt = TimeSpan.Zero;
        _contentHighlightRendering = true;
        ResetContentRevealLayers();
        ApplyContentRevealProgress(0);
        CompositionTarget.Rendering += OnContentHighlightRendering;
    }

    private void CancelContentHighlight()
    {
        if (!_contentHighlightRendering)
        {
            return;
        }

        CompositionTarget.Rendering -= OnContentHighlightRendering;
        _contentHighlightRendering = false;
        _contentHighlightStartedAt = TimeSpan.Zero;
        _contentHighlightProgress = 1;
        ResetContentRevealLayers();
    }

    private void OnContentHighlightRendering(object? sender, object e)
    {
        if (e is not RenderingEventArgs args)
        {
            return;
        }

        if (_contentHighlightStartedAt == TimeSpan.Zero)
        {
            _contentHighlightStartedAt = args.RenderingTime;
        }

        double elapsed = (args.RenderingTime - _contentHighlightStartedAt).TotalMilliseconds;
        _contentHighlightProgress = Math.Clamp(elapsed / ContentHighlightAnimationMs, 0, 1);
        ApplyContentRevealProgress(_contentHighlightProgress);

        if (_contentHighlightProgress >= 1)
        {
            CancelContentHighlight();
        }
    }

    private void ApplyContentRevealProgress(double progress)
    {
        if (_contentCardLayers.Count == 0 || SlideCount == 0)
        {
            return;
        }

        int trackIndex = Math.Clamp(GetTrackIndexForLogicalIndex(_currentIndex), 0, _contentCardLayers.Count - 1);
        IReadOnlyList<FrameworkElement> revealLayers = _contentCardLayers[trackIndex].RevealLayers;

        for (int i = 0; i < revealLayers.Count; i++)
        {
            FrameworkElement reveal = revealLayers[i];

            if (reveal.RenderTransform is not CompositeTransform transform)
            {
                continue;
            }

            double delayed = Math.Clamp((progress - i * 0.055) / 0.78, 0, 1);
            double eased = EaseOutFluent(delayed);
            double fadeIn = SmoothStep(0, 0.16, delayed);
            double fadeOut = 1 - SmoothStep(0.72, 1, delayed);
            double width = Math.Max(reveal.ActualWidth, 1);
            double height = Math.Max(reveal.ActualHeight, 1);

            reveal.Opacity = Math.Clamp(fadeIn * fadeOut, 0, 1);
            transform.ScaleX = 2.45;
            transform.ScaleY = 2.45;
            transform.TranslateX = Lerp(-width * 0.55, width * 0.55, eased);
            transform.TranslateY = Lerp(height * 0.42, -height * 0.42, eased);
        }
    }

    private void ResetContentRevealLayers()
    {
        foreach (ContentCardLayers cardLayers in _contentCardLayers)
        {
            foreach (FrameworkElement reveal in cardLayers.RevealLayers)
            {
                reveal.Opacity = 0;

                if (reveal.RenderTransform is CompositeTransform transform)
                {
                    transform.TranslateX = 0;
                    transform.TranslateY = 0;
                    transform.ScaleX = 2.45;
                    transform.ScaleY = 2.45;
                }
            }
        }
    }

    private void OnGlowCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!UseGlow || sender.ActualWidth <= 0 || sender.ActualHeight <= 0)
        {
            return;
        }

        Color color = ReferenceEquals(sender, GlowCanvasA) ? _glowColorA : _glowColorB;
        if (color.A == 0)
        {
            return;
        }

        PixelShaderEffect<HeroGlowShader> effect = new()
        {
            ConstantBuffer = new HeroGlowShader(
                (float)sender.ActualWidth,
                (float)sender.ActualHeight,
                color.R / 255.0f,
                color.G / 255.0f,
                color.B / 255.0f,
                color.A / 255.0f,
                (float)GlowBleed),
        };

        args.DrawingSession.DrawImage(effect);
    }

    private void OnSpotlightCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!UseSpotlight || sender.ActualWidth <= 0 || sender.ActualHeight <= 0)
        {
            return;
        }

        PixelShaderEffect<SpotlightOverlayShader> effect = new()
        {
            ConstantBuffer = new SpotlightOverlayShader(
                (float)sender.ActualWidth,
                (float)sender.ActualHeight,
                _spotlightX,
                _spotlightY,
                _spotlightOpacity),
        };

        args.DrawingSession.DrawImage(effect);
    }

    private double GetContentInset()
    {
        return _stageWidth <= CompactBreakpoint ? CompactContentInset : DesktopContentInset;
    }

    private sealed class ContentCardLayers(
        FrameworkElement? tag,
        FrameworkElement? title,
        FrameworkElement? subtitle,
        FrameworkElement? cta,
        FrameworkElement? rating,
        IReadOnlyList<FrameworkElement> revealLayers)
    {
        public FrameworkElement? Tag { get; } = tag;

        public FrameworkElement? Title { get; } = title;

        public FrameworkElement? Subtitle { get; } = subtitle;

        public FrameworkElement? Cta { get; } = cta;

        public FrameworkElement? Rating { get; } = rating;

        public IReadOnlyList<FrameworkElement> RevealLayers { get; } = revealLayers;
    }

}
