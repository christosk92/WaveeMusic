using System;
using System.Numerics;
using System.Threading;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Wavee.UI.WinUI.Effects.Editorial;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;
using Windows.Graphics;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Full-width anchor tile for the home feed's "Pick up where you left off"
/// slot. Backdrop is a baked CompositionDrawingSurface produced by
/// <see cref="EditorialBackdropRenderer"/> (blurred cover + accent gradients +
/// procedural noise + vignette). Cover itself floats on the right with a
/// drop shadow. <see cref="Item"/> DP drives both — change the item, the
/// backdrop re-bakes and the cover swaps in lockstep. Click routes through
/// <see cref="HomeViewModel.NavigateToItem"/>.
/// </summary>
public sealed partial class EditorialHeroCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(
            nameof(Item),
            typeof(HomeSectionItem),
            typeof(EditorialHeroCard),
            new PropertyMetadata(null, OnItemChanged));

    public HomeSectionItem? Item
    {
        get => (HomeSectionItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private EditorialBackdropRenderer? _renderer;
    private SpriteVisual? _backdropVisual;
    private CancellationTokenSource? _bakeCts;
    private string? _lastBakedUri;
    private Color _lastBakedAccent;
    private SizeInt32 _lastBakedSize;
    private bool _lastBakedDark;

    public EditorialHeroCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        BackdropHost.SizeChanged += BackdropHost_SizeChanged;
        ActualThemeChanged += OnActualThemeChanged;
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EditorialHeroCard card)
            card.ApplyItem(e.NewValue as HomeSectionItem);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_renderer is null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _renderer = new EditorialBackdropRenderer(compositor);
        }
        // Re-apply on (re-)attach so the backdrop bakes once dimensions are known.
        ApplyItem(Item);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _bakeCts?.Cancel();
        _bakeCts?.Dispose();
        _bakeCts = null;

        if (_backdropVisual is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(BackdropHost, null);
            _backdropVisual.Dispose();
            _backdropVisual = null;
        }

        _renderer?.Dispose();
        _renderer = null;

        _lastBakedUri = null;
        _lastBakedSize = default;

        // CompositionImage releases its own pin on Unload. Don't clear
        // HeroImage.ImageUrl — breaks scroll-back-up.
    }

    private void BackdropHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_backdropVisual is not null)
            _backdropVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        ApplyItem(Item);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        // Theme switch invalidates the noise range + base fill — drop the cache
        // and re-bake against the new theme.
        _renderer?.Invalidate();
        _lastBakedUri = null;
        ApplyItem(Item);
    }

    private async void ApplyItem(HomeSectionItem? item)
    {
        if (item is null)
        {
            HeroImage.ImageUrl = null;
            TitleText.Text = string.Empty;
            SubtitleText.Text = string.Empty;
            return;
        }

        TitleText.Text = item.Title ?? string.Empty;
        SubtitleText.Text = item.Subtitle ?? string.Empty;

        // Floating cover (right column).
        if (string.IsNullOrEmpty(item.ImageUrl))
        {
            HeroImage.ImageUrl = null;
        }
        else
        {
            // Decode bucketing is handled by ImageCacheService — feed the scaled
            // request as a hint and the cache snaps to the 512 bucket.
            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            var coverDecode = (int)Math.Round(240 * scale);
            HeroImage.DecodePixelSize = Math.Clamp(coverDecode, 240, 640);
            HeroImage.ImageUrl = item.ImageUrl;
        }

        // Backdrop bake — needs renderer + non-zero dimensions + a usable image.
        if (_renderer is null) return;
        if (string.IsNullOrEmpty(item.ImageUrl)) return;

        var w = (int)Math.Round(BackdropHost.ActualWidth);
        var h = (int)Math.Round(BackdropHost.ActualHeight);
        if (w <= 0 || h <= 0) return;

        var accent = TryParseHexColor(item.ColorHex) ?? Color.FromArgb(255, 28, 32, 40);
        var sizePx = new SizeInt32(w, h);
        var isDark = ActualTheme == ElementTheme.Dark;

        // Skip the bake if nothing material has changed since last one.
        if (string.Equals(_lastBakedUri, item.ImageUrl, StringComparison.Ordinal)
            && _lastBakedAccent.A == accent.A
            && _lastBakedAccent.R == accent.R
            && _lastBakedAccent.G == accent.G
            && _lastBakedAccent.B == accent.B
            && _lastBakedSize.Width == w
            && _lastBakedSize.Height == h
            && _lastBakedDark == isDark
            && _backdropVisual is not null)
        {
            return;
        }

        _bakeCts?.Cancel();
        _bakeCts = new CancellationTokenSource();
        var ct = _bakeCts.Token;

        Uri uri;
        try { uri = new Uri(item.ImageUrl); }
        catch { return; }

        try
        {
            var brush = await _renderer.GetBrushAsync(uri, accent, sizePx, isDark, ct);
            if (brush is null || ct.IsCancellationRequested) return;

            var compositor = ElementCompositionPreview.GetElementVisual(BackdropHost).Compositor;
            if (_backdropVisual is null)
            {
                _backdropVisual = compositor.CreateSpriteVisual();
                _backdropVisual.Size = new Vector2(w, h);
                ElementCompositionPreview.SetElementChildVisual(BackdropHost, _backdropVisual);
            }
            else
            {
                _backdropVisual.Size = new Vector2(w, h);
            }
            _backdropVisual.Brush = brush;

            _lastBakedUri = item.ImageUrl;
            _lastBakedAccent = accent;
            _lastBakedSize = sizePx;
            _lastBakedDark = isDark;
        }
        catch (OperationCanceledException)
        {
            // Bake superseded by a newer Apply — fine.
        }
    }

    private void HeroButton_Click(object sender, RoutedEventArgs e)
    {
        var item = Item;
        if (item is null) return;
        HomeViewModel.NavigateToItem(item, NavigationHelpers.IsCtrlPressed());
    }

    private static Color? TryParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 6)
            {
                return Color.FromArgb(255,
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
            }
            if (hex.Length == 8)
            {
                return Color.FromArgb(
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16),
                    Convert.ToByte(hex[6..8], 16));
            }
        }
        catch
        {
            // Malformed hex — fall through.
        }
        return null;
    }
}
