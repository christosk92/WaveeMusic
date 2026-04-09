using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wavee.UI.WinUI.Controls.AnimatedVisuals;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class LottieEqualizer : UserControl
{
    private const double RestProgress = 0.0;
    private readonly LightRedEqualizer _source = new();
    private bool _isLoaded;

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
        ApplyColor();
        UpdatePlayback();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        ResetToRestFrame();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => ApplyColor();

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

        if (IsActive)
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
