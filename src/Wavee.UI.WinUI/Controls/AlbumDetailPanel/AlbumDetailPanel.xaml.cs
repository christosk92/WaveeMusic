using System;
using System.Collections;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.AlbumDetailPanel;

/// <summary>
/// Expandable inline album detail panel (Apple Music style).
/// Album art uses Composition API alpha mask (left fade) matching the hero header pattern.
/// </summary>
public sealed partial class AlbumDetailPanel : UserControl
{
    private CompositionSurfaceBrush? _surfaceBrush;
    private SpriteVisual? _spriteVisual;
    private Compositor? _compositor;
    private Microsoft.UI.Xaml.Media.LoadedImageSurface? _imageSurface;
    private readonly IPlaybackService? _playbackService;
    private string? _appliedColorHex;

    // Alpha (0..255) that the palette colour is blended over the theme surface
    // at. Keeps the album's accent recognisable without producing a saturated
    // slab that fights the page in light mode.
    private const byte PaletteTintAlpha = 60;

    public static readonly DependencyProperty AlbumProperty =
        DependencyProperty.Register(nameof(Album), typeof(Wavee.UI.WinUI.ViewModels.ArtistReleaseVm), typeof(AlbumDetailPanel),
            new PropertyMetadata(null, OnAlbumChanged));

    public static readonly DependencyProperty TracksProperty =
        DependencyProperty.Register(nameof(Tracks), typeof(IEnumerable), typeof(AlbumDetailPanel),
            new PropertyMetadata(null, OnTracksChanged));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(AlbumDetailPanel),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ColorHexProperty =
        DependencyProperty.Register(nameof(ColorHex), typeof(string), typeof(AlbumDetailPanel),
            new PropertyMetadata(null, OnColorHexChanged));

    public static readonly DependencyProperty NotchOffsetXProperty =
        DependencyProperty.Register(nameof(NotchOffsetX), typeof(double), typeof(AlbumDetailPanel),
            new PropertyMetadata(0.0, OnNotchOffsetXChanged));

    public Wavee.UI.WinUI.ViewModels.ArtistReleaseVm? Album
    {
        get => (Wavee.UI.WinUI.ViewModels.ArtistReleaseVm?)GetValue(AlbumProperty);
        set => SetValue(AlbumProperty, value);
    }

    public IEnumerable? Tracks
    {
        get => (IEnumerable?)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public string? ColorHex
    {
        get => (string?)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    public double NotchOffsetX
    {
        get => (double)GetValue(NotchOffsetXProperty);
        set => SetValue(NotchOffsetXProperty, value);
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? PlayRequested;
    public event EventHandler? ShuffleRequested;

    private const double NotchWidth = 28;

    public AlbumDetailPanel()
    {
        _playbackService = Ioc.Default.GetService<IPlaybackService>();
        InitializeComponent();
        OuterGrid.SizeChanged += OuterGrid_SizeChanged;
        ImageArea.Loaded += ImageArea_Loaded;
        ActualThemeChanged += OnPanelActualThemeChanged;
        Unloaded += OnUnloaded;

        // Wire up track click → play via IPlaybackService
        TrackListControl.TrackClicked += OnTrackClicked;

        // Add playcount custom column
        TrackListControl.CustomColumns.Add(new Controls.TrackList.TrackListColumnDefinition
        {
            Header = "Plays",
            Width = new Microsoft.UI.Xaml.GridLength(70),
            TextAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
            ValueSelector = item =>
            {
                if (item is LazyTrackItem { Data: ArtistTopTrackVm vm })
                    return vm.PlayCountFormatted;
                if (item is ArtistTopTrackVm directVm)
                    return directVm.PlayCountFormatted;
                return "";
            }
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;
        OuterGrid.SizeChanged -= OuterGrid_SizeChanged;
        ActualThemeChanged -= OnPanelActualThemeChanged;
        TrackListControl.TrackClicked -= OnTrackClicked;

        // Dispose composition resources to prevent leaks
        // (this control is created/destroyed dynamically on each expand/collapse)
        _imageSurface?.Dispose();
        _imageSurface = null;

        if (_surfaceBrush != null)
        {
            _surfaceBrush.Surface = null;
            _surfaceBrush.Dispose();
            _surfaceBrush = null;
        }

        if (_spriteVisual != null)
        {
            ElementCompositionPreview.SetElementChildVisual(ImageArea, null);
            _spriteVisual.Brush?.Dispose();
            _spriteVisual.Dispose();
            _spriteVisual = null;
        }

        _compositor = null;
    }

    private void ImageArea_Loaded(object sender, RoutedEventArgs e)
    {
        ImageArea.Loaded -= ImageArea_Loaded;
        SetupCompositionMask();
        // If album was already set before Loaded, load the image now
        if (Album != null)
            LoadImage(Album.ImageUrl);
    }

    private void SetupCompositionMask()
    {
        var visual = ElementCompositionPreview.GetElementVisual(ImageArea);
        _compositor = visual.Compositor;

        // Gradient mask: transparent on left → opaque on right
        // This makes the image fade in from the left edge (where the color bg is)
        var gradientBrush = _compositor.CreateLinearGradientBrush();
        gradientBrush.StartPoint = new Vector2(0f, 0.5f); // left
        gradientBrush.EndPoint = new Vector2(1f, 0.5f);   // right
        gradientBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f,
            Windows.UI.Color.FromArgb(0, 255, 255, 255)));     // transparent (image hidden)
        gradientBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.4f,
            Windows.UI.Color.FromArgb(255, 255, 255, 255)));   // fully opaque (image visible)

        // Surface brush for the album art
        _surfaceBrush = _compositor.CreateSurfaceBrush();
        _surfaceBrush.Stretch = CompositionStretch.UniformToFill;
        _surfaceBrush.HorizontalAlignmentRatio = 0.5f;
        _surfaceBrush.VerticalAlignmentRatio = 0.5f;

        // Mask brush: image × gradient
        var maskBrush = _compositor.CreateMaskBrush();
        maskBrush.Source = _surfaceBrush;
        maskBrush.Mask = gradientBrush;

        // Sprite visual fills the ImageArea
        _spriteVisual = _compositor.CreateSpriteVisual();
        _spriteVisual.Brush = maskBrush;
        _spriteVisual.RelativeSizeAdjustment = Vector2.One;

        ElementCompositionPreview.SetElementChildVisual(ImageArea, _spriteVisual);
    }

    private void LoadImage(string? imageUrl)
    {
        if (_surfaceBrush == null || _compositor == null) return;

        // Dispose previous surface to prevent memory leaks
        _imageSurface?.Dispose();
        _imageSurface = null;

        if (string.IsNullOrEmpty(imageUrl))
        {
            _surfaceBrush.Surface = null;
            return;
        }

        var url = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(url)) return;

        var desiredSize = new Windows.Foundation.Size(
            Math.Max(1, ImageArea.ActualWidth > 0 ? ImageArea.ActualWidth : 640),
            Math.Max(1, ImageArea.ActualHeight > 0 ? ImageArea.ActualHeight : 640));
        _imageSurface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(url), desiredSize);
        _surfaceBrush.Surface = _imageSurface;
    }

    private void OuterGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = e.NewSize.Height;
        var maxWidth = e.NewSize.Width * 0.45;
        ImageArea.Width = Math.Min(height, maxWidth);
    }

    private static void OnAlbumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AlbumDetailPanel)d;
        var album = e.NewValue as Wavee.UI.WinUI.ViewModels.ArtistReleaseVm;

        if (album != null)
        {
            panel.AlbumNameText.Text = album.Name ?? "";
            panel.TypeText.Text = album.Type ?? "ALBUM";
            panel.YearText.Text = album.Year.ToString();
            panel.FooterYearText.Text = album.Year.ToString();

            // Load album art via composition
            panel.LoadImage(album.ImageUrl);

            if (string.IsNullOrEmpty(panel.ColorHex))
                panel.ApplyBackground();
        }
    }

    private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AlbumDetailPanel)d;
        panel._appliedColorHex = e.NewValue as string;
        panel.ApplyBackground();
    }

    private void OnPanelActualThemeChanged(FrameworkElement sender, object args)
        => ApplyBackground();

    private static void OnNotchOffsetXChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AlbumDetailPanel)d;
        var offset = (double)e.NewValue;
        panel.NotchTranslate.X = offset - (NotchWidth / 2);
    }

    private void ApplyBackground()
    {
        var surface = GetThemeSurfaceColor();
        var color = string.IsNullOrEmpty(_appliedColorHex)
            ? surface
            : BlendOver(ParseHexColor(_appliedColorHex), surface, PaletteTintAlpha);

        ColorBackground.Background = new SolidColorBrush(color);
        NotchTriangle.Fill = new SolidColorBrush(color);
    }

    private Windows.UI.Color GetThemeSurfaceColor()
    {
        var isDark = ActualTheme == ElementTheme.Dark;
        return isDark
            ? Windows.UI.Color.FromArgb(255, 30, 30, 35)
            : Windows.UI.Color.FromArgb(255, 230, 230, 235);
    }

    private static Windows.UI.Color BlendOver(Windows.UI.Color top, Windows.UI.Color bottom, byte topAlpha)
    {
        var a = topAlpha / 255f;
        return Windows.UI.Color.FromArgb(255,
            (byte)(top.R * a + bottom.R * (1f - a)),
            (byte)(top.G * a + bottom.G * (1f - a)),
            (byte)(top.B * a + bottom.B * (1f - a)));
    }

    private static Windows.UI.Color ParseHexColor(string hex)
    {
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

    private static void OnTracksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AlbumDetailPanel)d;
        panel.TrackListControl.ItemsSource = e.NewValue as IEnumerable;
    }

    private void OnTrackClicked(object sender, ITrackItem track)
    {
        // Play the clicked track within this album's context
        var albumUri = Album?.Uri ?? (Album?.Id != null ? $"spotify:album:{Album.Id}" : null);

        if (_playbackService != null && albumUri != null)
        {
            _ = _playbackService.PlayTrackInContextAsync(track.Uri, albumUri);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void PlayButton_Click(object sender, RoutedEventArgs e)
        => PlayRequested?.Invoke(this, EventArgs.Empty);

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        => ShuffleRequested?.Invoke(this, EventArgs.Empty);

    private void ViewAlbumButton_Click(object sender, RoutedEventArgs e)
    {
        if (Album == null) return;

        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
        // Helpers.ConnectedAnimationHelper.PrepareAnimation(
        //     Helpers.ConnectedAnimationHelper.AlbumArt, ImageArea);

        var param = new ContentNavigationParameter
        {
            Uri = Album.Uri ?? Album.Id,
            Title = Album.Name,
            ImageUrl = Album.ImageUrl
        };
        NavigationHelpers.OpenAlbum(param, Album.Name ?? "Album");
    }
}
