using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
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

public sealed partial class AlbumPage : Page, ITabBarItemContent
{
    private readonly ILogger? _logger;
    private LoadedImageSurface? _blurSurface;
    private SpriteVisual? _blurSprite;

    public AlbumViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public AlbumPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<AlbumViewModel>();
        _logger = Ioc.Default.GetService<ILogger<AlbumPage>>();
        InitializeComponent();

        // Custom columns with compile-time delegate (no reflection)
        TrackList.CustomColumns = new List<Controls.TrackList.TrackListColumnDefinition>
        {
            new()
            {
                Header = "Plays",
                Width = new GridLength(80),
                TextAlignment = HorizontalAlignment.Right,
                ValueSelector = item => item is ViewModels.LazyTrackItem lazy
                    && lazy.Data is Data.DTOs.AlbumTrackDto dto
                    ? dto.PlayCountFormatted
                    : ""
            }
        };

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        PageScrollView.ViewChanged += OnPageScrollViewChanged;
        Loaded += AlbumPage_Loaded;
        Unloaded += AlbumPage_Unloaded;
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void AlbumPage_Loaded(object sender, RoutedEventArgs e)
    {
        // If AlbumImageUrl was already set before the page was fully loaded
        // (e.g. via PrefillFrom during OnNavigatedTo), set up the blur now
        if (!string.IsNullOrEmpty(ViewModel.AlbumImageUrl) && _blurSprite == null)
        {
            var url = SpotifyImageHelper.ToHttpsUrl(ViewModel.AlbumImageUrl) ?? ViewModel.AlbumImageUrl;
            if (!string.IsNullOrEmpty(url))
                SetupBlurredBackground(url);
        }
    }

    private void AlbumPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        PageScrollView.ViewChanged -= OnPageScrollViewChanged;
        (ViewModel as IDisposable)?.Dispose();

        // Dispose composition resources
        _blurSurface?.Dispose();
        _blurSurface = null;
        if (_blurSprite != null)
        {
            ElementCompositionPreview.SetElementChildVisual(BlurredBackground, null);
            _blurSprite.Brush?.Dispose();
            _blurSprite.Dispose();
            _blurSprite = null;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumViewModel.AlbumImageUrl) && !string.IsNullOrEmpty(ViewModel.AlbumImageUrl))
        {
            var url = SpotifyImageHelper.ToHttpsUrl(ViewModel.AlbumImageUrl) ?? ViewModel.AlbumImageUrl;
            if (!string.IsNullOrEmpty(url))
                SetupBlurredBackground(url);
        }
    }

    private void SetupBlurredBackground(string imageUrl)
    {
        // Clean up previous
        _blurSurface?.Dispose();
        if (_blurSprite != null)
        {
            ElementCompositionPreview.SetElementChildVisual(BlurredBackground, null);
            _blurSprite.Brush?.Dispose();
            _blurSprite.Dispose();
        }

        var visual = ElementCompositionPreview.GetElementVisual(BlurredBackground);
        var compositor = visual.Compositor;

        // Load album art
        _blurSurface = LoadedImageSurface.StartLoadFromUri(new Uri(imageUrl));
        var surfaceBrush = compositor.CreateSurfaceBrush();
        surfaceBrush.Surface = _blurSurface;
        surfaceBrush.Stretch = CompositionStretch.UniformToFill;

        // Gaussian blur effect
        var blurEffect = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = 60f,
            Source = new CompositionEffectSourceParameter("image"),
            BorderMode = EffectBorderMode.Hard
        };

        var effectFactory = compositor.CreateEffectFactory(blurEffect);
        var effectBrush = effectFactory.CreateBrush();
        effectBrush.SetSourceParameter("image", surfaceBrush);

        // Diagonal gradient mask: top-left opaque → center-right transparent
        var gradientMask = compositor.CreateLinearGradientBrush();
        gradientMask.StartPoint = new Vector2(0f, 0f);
        gradientMask.EndPoint = new Vector2(0.7f, 0.6f);
        gradientMask.ColorStops.Add(compositor.CreateColorGradientStop(0f,
            Windows.UI.Color.FromArgb(160, 255, 255, 255)));
        gradientMask.ColorStops.Add(compositor.CreateColorGradientStop(0.4f,
            Windows.UI.Color.FromArgb(60, 255, 255, 255)));
        gradientMask.ColorStops.Add(compositor.CreateColorGradientStop(1f,
            Windows.UI.Color.FromArgb(0, 255, 255, 255)));

        // Mask: blurred image × gradient
        var maskBrush = compositor.CreateMaskBrush();
        maskBrush.Source = effectBrush;
        maskBrush.Mask = gradientMask;

        // Sprite visual fills the border
        _blurSprite = compositor.CreateSpriteVisual();
        _blurSprite.Brush = maskBrush;
        _blurSprite.RelativeSizeAdjustment = Vector2.One;

        // Start invisible, fade in after image loads to avoid flash
        _blurSprite.Opacity = 0f;
        ElementCompositionPreview.SetElementChildVisual(BlurredBackground, _blurSprite);

        _blurSurface.LoadCompleted += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_blurSprite == null || compositor == null) return;
                var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                fadeAnim.InsertKeyFrame(0f, 0f);
                fadeAnim.InsertKeyFrame(1f, 1f,
                    compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
                fadeAnim.Duration = TimeSpan.FromMilliseconds(600);
                _blurSprite.StartAnimation("Opacity", fadeAnim);
            });
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        Helpers.ConnectedAnimationHelper.TryStartAnimation(
            Helpers.ConnectedAnimationHelper.AlbumArt, AlbumArtContainer);

        try
        {
            if (e.Parameter is ContentNavigationParameter nav)
            {
                ViewModel.PrefillFrom(nav);
                await ViewModel.LoadCommand.ExecuteAsync(nav.Uri);
            }
            else if (e.Parameter is string albumId && !string.IsNullOrWhiteSpace(albumId))
            {
                await ViewModel.LoadCommand.ExecuteAsync(albumId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unhandled error in AlbumPage OnNavigatedTo");
        }
    }

    // ── Sticky left panel via Composition ──

    private void OnPageScrollViewChanged(ScrollView sender, object args)
    {
        if (WideAlbumPanel.Visibility != Visibility.Visible) return;

        var scrollOffset = sender.VerticalOffset;
        var visual = ElementCompositionPreview.GetElementVisual(WideAlbumPanel);

        // Cap the offset so the panel doesn't extend past the two-column grid
        var maxOffset = Math.Max(0, TwoColumnGrid.ActualHeight - WideAlbumPanel.ActualHeight);
        var offset = (float)Math.Min(scrollOffset, maxOffset);

        visual.Offset = new Vector3(0, offset, 0);
    }

    // ── Click handlers ──

    private void Artist_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ViewModel.ArtistId))
        {
            var openInNewTab = NavigationHelpers.IsCtrlPressed();
            NavigationHelpers.OpenArtist(ViewModel.ArtistId, ViewModel.ArtistName ?? "Artist", openInNewTab);
        }
    }

    private void TrackList_ArtistClicked(object? sender, string artistId)
    {
        if (!string.IsNullOrEmpty(artistId))
            NavigationHelpers.OpenArtist(artistId, "Artist");
    }

    private void TrackList_NewPlaylistRequested(object? sender, IReadOnlyList<string> trackIds)
    {
        NavigationHelpers.OpenCreatePlaylist(isFolder: false, trackIds: trackIds.ToList());
    }

    private void RelatedAlbum_Click(object sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AlbumRelatedResult album)
        {
            var param = new ContentNavigationParameter
            {
                Uri = album.Uri ?? album.Id ?? "",
                Title = album.Name,
                ImageUrl = album.ImageUrl
            };
            NavigationHelpers.OpenAlbum(param, album.Name ?? "Album", NavigationHelpers.IsCtrlPressed());
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 600)
            VisualStateManager.GoToState(this, "NarrowState", true);
        else
            VisualStateManager.GoToState(this, "WideState", true);
    }
}
