using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ConcertPage : Page, ITabBarItemContent
{
    private readonly ILogger? _logger;
    private bool _showingContent;

    public ConcertViewModel ViewModel { get; }
    public TabItemParameter? TabItemParameter => null;
    public event EventHandler<TabItemParameter>? ContentChanged;

    public ConcertPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ConcertViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ConcertPage>>();
        InitializeComponent();
        ContentContainer.Opacity = 0;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Unloaded += ConcertPage_Unloaded;
        SetupGradientOverlay();
        ActualThemeChanged += (_, _) => SetupGradientOverlay();

        // Rebuild diagonal slices on resize (atomic swap, no flash)
        HeroImageContainer.SizeChanged += (_, _) =>
        {
            if (_heroSurfaces.Count > 0) SetupHeroImages();
        };
    }

    private void ConcertPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        foreach (var surface in _heroSurfaces)
            surface.Dispose();
        _heroSurfaces.Clear();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConcertViewModel.IsLoading) && !ViewModel.IsLoading && !_showingContent)
        {
            _showingContent = true;

            AnimationBuilder.Create()
                .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(150),
                         layer: FrameworkLayer.Xaml)
                .Start(ShimmerContainer);

            ContentContainer.Opacity = 1;
            AnimationBuilder.Create()
                .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(150),
                         layer: FrameworkLayer.Xaml)
                .Start(ContentContainer);

            _ = Task.Delay(160).ContinueWith(_ =>
                DispatcherQueue.TryEnqueue(() => ShimmerContainer.Visibility = Visibility.Collapsed));

            // Build diagonal hero images after content is ready
            SetupHeroImages();
        }
    }

    private void SetupGradientOverlay()
    {
        // Use the actual theme background color so the gradient blends in both light and dark mode
        var isDark = ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark;
        var baseColor = isDark
            ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
            : Windows.UI.Color.FromArgb(255, 245, 245, 245);

        var gradient = new Microsoft.UI.Xaml.Media.LinearGradientBrush();
        gradient.StartPoint = new Windows.Foundation.Point(0, 0);
        gradient.EndPoint = new Windows.Foundation.Point(0, 1);
        gradient.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Windows.UI.Color.FromArgb(100, baseColor.R, baseColor.G, baseColor.B), Offset = 0.0
        });
        gradient.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Windows.UI.Color.FromArgb(190, baseColor.R, baseColor.G, baseColor.B), Offset = 0.25
        });
        gradient.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Windows.UI.Color.FromArgb(232, baseColor.R, baseColor.G, baseColor.B), Offset = 0.5
        });
        gradient.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Windows.UI.Color.FromArgb(245, baseColor.R, baseColor.G, baseColor.B), Offset = 0.75
        });

        GradientOverlay.Background = gradient;
    }

    private readonly List<LoadedImageSurface> _heroSurfaces = [];

    /// <summary>
    /// Creates diagonal-sliced Composition visuals for each artist's header image.
    /// Uses Win2D CanvasPathBuilder for polygon clip geometry.
    /// </summary>
    private void SetupHeroImages()
    {
        var imageUrls = ViewModel.Artists
            .Where(a => !string.IsNullOrEmpty(a.HeaderImageUrl))
            .Select(a => SpotifyImageHelper.ToHttpsUrl(a.HeaderImageUrl!))
            .Where(u => !string.IsNullOrEmpty(u))
            .ToList();

        if (imageUrls.Count == 0) return;

        var hostVisual = ElementCompositionPreview.GetElementVisual(HeroImageContainer);
        var compositor = hostVisual.Compositor;

        var containerVisual = compositor.CreateContainerVisual();
        containerVisual.RelativeSizeAdjustment = Vector2.One;

        var width = (float)HeroImageContainer.ActualWidth;
        var height = (float)HeroImageContainer.ActualHeight;
        if (width <= 0) width = 1400;
        if (height <= 0) height = 400;

        // Skew amount: how far the diagonal shifts horizontally
        var skew = height * 0.4f; // ~30° angle

        // Build diagonal split points: N-1 lines between N images
        // Each line goes from (x + skew, 0) at the top to (x - skew, height) at the bottom
        var n = imageUrls.Count;
        var splitPoints = new float[n + 1];
        splitPoints[0] = 0;
        splitPoints[n] = width;
        for (int j = 1; j < n; j++)
            splitPoints[j] = width * j / n;

        for (int i = 0; i < n; i++)
        {
            var surface = LoadedImageSurface.StartLoadFromUri(new Uri(imageUrls[i]!));
            _heroSurfaces.Add(surface);

            // Polygon corners in hero-space
            float tl = i == 0 ? 0 : splitPoints[i] + skew;
            float tr = i == n - 1 ? width : splitPoints[i + 1] + skew;
            float br = i == n - 1 ? width : splitPoints[i + 1] - skew;
            float bl = i == 0 ? 0 : splitPoints[i] - skew;

            // Bounding box of this slice
            float minX = Math.Min(tl, bl);
            float maxX = Math.Max(tr, br);
            float sliceW = maxX - minX;

            var surfaceBrush = compositor.CreateSurfaceBrush();
            surfaceBrush.Surface = surface;
            surfaceBrush.Stretch = CompositionStretch.UniformToFill;
            surfaceBrush.HorizontalAlignmentRatio = 0.5f;
            surfaceBrush.VerticalAlignmentRatio = 0.3f;

            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = surfaceBrush;
            sprite.Size = new Vector2(sliceW, height);
            sprite.Offset = new Vector3(minX, 0, 0);

            // Clip polygon relative to sprite's local coords (shifted by -minX)
            using var pathBuilder = new CanvasPathBuilder(null);
            pathBuilder.BeginFigure(tl - minX, 0);
            pathBuilder.AddLine(tr - minX, 0);
            pathBuilder.AddLine(br - minX, height);
            pathBuilder.AddLine(bl - minX, height);
            pathBuilder.EndFigure(CanvasFigureLoop.Closed);

            var canvasGeo = CanvasGeometry.CreatePath(pathBuilder);
            var pathGeo = compositor.CreatePathGeometry(new CompositionPath(canvasGeo));
            sprite.Clip = compositor.CreateGeometricClip(pathGeo);

            containerVisual.Children.InsertAtTop(sprite);
        }

        ElementCompositionPreview.SetElementChildVisual(HeroImageContainer, containerVisual);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is ContentNavigationParameter nav)
        {
            ViewModel.Title = nav.Title;
            await ViewModel.LoadCommand.ExecuteAsync(nav.Uri);
        }
        else if (e.Parameter is string uri)
        {
            await ViewModel.LoadCommand.ExecuteAsync(uri);
        }
    }

    private void Artist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string uri && !string.IsNullOrEmpty(uri))
        {
            var param = new ContentNavigationParameter { Uri = uri };
            NavigationHelpers.OpenArtist(param, "", NavigationHelpers.IsCtrlPressed());
        }
    }

    private void Offer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url && !string.IsNullOrEmpty(url))
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private void RelatedConcert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string uri && !string.IsNullOrEmpty(uri))
        {
            var title = (btn.DataContext as ConcertRelatedVm)?.Title;
            var param = new ContentNavigationParameter { Uri = uri, Title = title };
            NavigationHelpers.OpenConcert(param, title ?? "Concert", NavigationHelpers.IsCtrlPressed());
        }
    }

    private void LocationButton_LocationChanged(object? sender, string city)
    {
        ViewModel.UserLocationName = city;
    }
}
