using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Wavee.UI.WinUI.Controls.AnimatedVisuals;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Lottie-driven "now playing" equalizer indicator. Wraps an
/// <see cref="AnimatedVisualPlayer"/> with the generated
/// <see cref="LightRedEqualizer"/> source.
///
/// <para>
/// CPU optimization: the Lottie animation only runs when the control is BOTH
/// active (<see cref="IsActive"/>) AND in the effective viewport. As the user
/// scrolls a list, equalizers that leave the viewport pause their animation
/// even though they remain "active" semantically (their track is still
/// playing) — they just aren't visible. This kills the dominant idle-CPU
/// source for Lottie which is rasterizing the multi-layer vector at 60 fps
/// on the composition thread.
/// </para>
/// </summary>
public sealed partial class LottieEqualizer : UserControl
{
    private const double RestProgress = 0.0;
    private readonly LightRedEqualizer _source = new();
    private bool _isLoaded;
    private bool _isInViewport;

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LottieEqualizer),
            new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(LottieEqualizer),
            new PropertyMetadata(18.0, OnIconSizeChanged));

    public static readonly DependencyProperty EqualizerColorProperty =
        DependencyProperty.Register(nameof(EqualizerColor), typeof(Color), typeof(LottieEqualizer),
            new PropertyMetadata(Colors.Transparent, OnEqualizerColorChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Color EqualizerColor
    {
        get => (Color)GetValue(EqualizerColorProperty);
        set => SetValue(EqualizerColorProperty, value);
    }

    public LottieEqualizer()
    {
        InitializeComponent();
        Player.Source = _source;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        ApplyIconSize();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LottieEqualizer)d).UpdatePlayback();

    private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LottieEqualizer)d).ApplyIconSize();

    private static void OnEqualizerColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LottieEqualizer)d).ApplyColor();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        // Default _isInViewport to true on load; the first
        // EffectiveViewportChanged fires shortly after with the real state.
        _isInViewport = true;
        // EffectiveViewportChanged fires when the control's effective viewport
        // (accounting for scroll-viewer clipping) changes. Subscribed on attach
        // so the handler doesn't accumulate in the WinRT EventSource table
        // across navigation-cached realizations. Used to pause Lottie playback
        // while the equalizer is scrolled off-screen — even if IsActive=true,
        // no point rasterizing a multi-layer vector that the user can't see.
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        ApplyColor();
        UpdatePlayback();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        _isLoaded = false;
        ResetToRestFrame();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => ApplyColor();

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        // BringIntoViewDistanceX/Y are 0 when the element is fully inside the
        // viewport, positive when it's offscreen by that many DIPs. Treat any
        // non-zero distance on either axis as "offscreen" — we resume on
        // re-entry. Use a small threshold so a 1-pixel scroll glitch doesn't
        // toggle the animation needlessly.
        const double OffscreenThresholdPx = 4.0;

        var offscreen = args.BringIntoViewDistanceX > OffscreenThresholdPx
                        || args.BringIntoViewDistanceY > OffscreenThresholdPx;
        var inViewport = !offscreen;

        if (inViewport == _isInViewport) return;
        _isInViewport = inViewport;
        UpdatePlayback();
    }

    private void ApplyIconSize()
    {
        if (Player == null) return;

        var size = Math.Max(1, IconSize);
        Player.Width = size;
        Player.Height = size;
    }

    private void ApplyColor()
    {
        var color = EqualizerColor.A == 0
            ? ResolveThemeColor()
            : EqualizerColor;

        _source.Color_FF3F3F = color;
    }

    private void UpdatePlayback()
    {
        if (!_isLoaded || Player == null) return;

        // Animation runs only when active AND visible in the viewport. Either
        // condition false → static rest frame (no per-frame composition cost).
        if (IsActive && _isInViewport)
        {
            _ = Player.PlayAsync(0, 1, true);
        }
        else
        {
            ResetToRestFrame();
        }
    }

    private void ResetToRestFrame()
    {
        Player.Stop();
        Player.SetProgress(RestProgress);
    }

    private static Color ResolveThemeColor()
    {
        if (TryGetBrushColor("AccentFillColorDefaultBrush", out var color))
            return color;

        if (TryGetBrushColor("AccentTextFillColorPrimaryBrush", out color))
            return color;

        if (TryGetBrushColor("TextFillColorPrimaryBrush", out color))
            return color;

        return Colors.White;
    }

    private static bool TryGetBrushColor(string resourceKey, out Color color)
    {
        color = Colors.White;
        if (Application.Current.Resources.TryGetValue(resourceKey, out var resource) &&
            resource is SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }

        return false;
    }
}
