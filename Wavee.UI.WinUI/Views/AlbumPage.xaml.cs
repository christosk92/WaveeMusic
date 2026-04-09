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

    // CompositionEffectFactory compiles a pixel shader on creation, which is
    // expensive (Microsoft's Composition docs explicitly call this out). The
    // previous version built a fresh factory inside SetupBlurredBackground per
    // page instance, so every album navigation re-compiled the same blur shader
    // and re-allocated intermediate render targets. Cache the factory once per
    // Compositor (a single Compositor is shared across the whole app via the
    // root visual) and reuse it for every AlbumPage instance.
    private static CompositionEffectFactory? s_blurEffectFactory;
    private static readonly object s_blurEffectFactoryLock = new();

    private static CompositionEffectFactory GetBlurEffectFactory(Compositor compositor)
    {
        if (s_blurEffectFactory != null) return s_blurEffectFactory;
        lock (s_blurEffectFactoryLock)
        {
            if (s_blurEffectFactory != null) return s_blurEffectFactory;
            var blurEffect = new GaussianBlurEffect
            {
                Name = "Blur",
                BlurAmount = 60f,
                Source = new CompositionEffectSourceParameter("image"),
                BorderMode = EffectBorderMode.Hard
            };
            s_blurEffectFactory = compositor.CreateEffectFactory(blurEffect);
            return s_blurEffectFactory;
        }
    }

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
        TrackList.SetItemTransitionsEnabled(false);
        Loaded += AlbumPage_Loaded;
        Unloaded += AlbumPage_Unloaded;
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void AlbumPage_Loaded(object sender, RoutedEventArgs e)
    {
        // If AlbumImageUrl was already set before the page was fully loaded
        // (e.g. via PrefillFrom during OnNavigatedTo), set up the blur now.
        // Deferred to Low priority so first paint completes before we spin up
        // LoadedImageSurface + Compositor work.
        if (!string.IsNullOrEmpty(ViewModel.AlbumImageUrl) && _blurSprite == null)
        {
            var url = SpotifyImageHelper.ToHttpsUrl(ViewModel.AlbumImageUrl) ?? ViewModel.AlbumImageUrl;
            if (!string.IsNullOrEmpty(url))
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => SetupBlurredBackground(url));
        }
    }

    private void AlbumPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
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
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => SetupBlurredBackground(url));
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
            _blurSprite = null;
        }

        var visual = ElementCompositionPreview.GetElementVisual(BlurredBackground);
        var compositor = visual.Compositor;

        // Start loading image immediately (async, non-blocking)
        _blurSurface = LoadedImageSurface.StartLoadFromUri(
            new Uri(imageUrl),
            new Windows.Foundation.Size(512, 512));

        // Defer all GPU-heavy composition work (blur effect, gradient mask) until
        // the image has actually loaded — avoids stalling the navigation transition.
        _blurSurface.LoadCompleted += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_blurSurface == null) return;

                var surfaceBrush = compositor.CreateSurfaceBrush();
                surfaceBrush.Surface = _blurSurface;
                surfaceBrush.Stretch = CompositionStretch.UniformToFill;

                // Use the shared static effect factory — see GetBlurEffectFactory.
                // Compiling the GaussianBlurEffect shader once at app startup
                // (lazily on first navigation) instead of per AlbumPage instance
                // saves both CPU on navigation and GPU intermediate-target memory.
                var effectFactory = GetBlurEffectFactory(compositor);
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
                _blurSprite.Opacity = 0f;
                ElementCompositionPreview.SetElementChildVisual(BlurredBackground, _blurSprite);

                // Fade in
                var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                fadeAnim.InsertKeyFrame(0f, 0f);
                fadeAnim.InsertKeyFrame(1f, 1f,
                    compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
                fadeAnim.Duration = TimeSpan.FromMilliseconds(600);
                _blurSprite.StartAnimation("Opacity", fadeAnim);
            });
        };
    }

    public void RefreshWithParameter(object? parameter)
    {
        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph.
        // Album → Album (refresh-in-place from TabBarItem.Navigate) never routes
        // through OnNavigatedTo, so we have to start the connected animation here
        // ourselves — otherwise the source ContentCard's PrepareToAnimate snapshot
        // is discarded and nothing animates.
        // Helpers.ConnectedAnimationHelper.TryStartAnimation(
        //     Helpers.ConnectedAnimationHelper.AlbumArt, AlbumArtContainer);

        LoadNewContent(parameter);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
        // Helpers.ConnectedAnimationHelper.TryStartAnimation(
        //     Helpers.ConnectedAnimationHelper.AlbumArt, AlbumArtContainer);

        LoadNewContent(e.Parameter);
    }

    private void LoadNewContent(object? parameter)
    {
        if (parameter is ContentNavigationParameter nav)
        {
            ViewModel.PrefillFrom(nav);
            _ = ViewModel.LoadCommand.ExecuteAsync(nav.Uri);
        }
        else if (parameter is string albumId && !string.IsNullOrWhiteSpace(albumId))
        {
            _ = ViewModel.LoadCommand.ExecuteAsync(albumId);
        }
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

    private void MerchItem_Click(object sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AlbumMerchItemResult merch
            && !string.IsNullOrEmpty(merch.ShopUrl))
        {
            _ = ViewModel.OpenMerchItemCommand.ExecuteAsync(merch.ShopUrl);
        }
    }

    private void RelatedAlbum_Click(object sender, EventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;

        var album = fe.Tag as AlbumRelatedResult ?? fe.DataContext as AlbumRelatedResult;
        if (album != null)
        {
            var targetUri = album.Uri ?? album.Id;
            if (string.IsNullOrWhiteSpace(targetUri))
                return;

            var param = new ContentNavigationParameter
            {
                Uri = targetUri,
                Title = album.Name,
                ImageUrl = album.ImageUrl
            };
            NavigationHelpers.OpenAlbum(param, album.Name ?? "Album", NavigationHelpers.IsCtrlPressed());
            return;
        }

        if (sender is Controls.Cards.ContentCard card && !string.IsNullOrWhiteSpace(card.NavigationUri))
        {
            var param = new ContentNavigationParameter
            {
                Uri = card.NavigationUri,
                Title = card.Title,
                ImageUrl = card.ImageUrl
            };
            NavigationHelpers.OpenAlbum(param, card.Title ?? "Album", NavigationHelpers.IsCtrlPressed());
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
