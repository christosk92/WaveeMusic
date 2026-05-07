using System;
using System.Collections.Generic;
using Klankhuis.Hero.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Views.Shared;

/// <summary>
/// Reusable hero band - wraps Klankhuis's <see cref="HeroCarousel"/> and
/// <see cref="HeroHalo"/> with all the composition wiring (accent
/// callback, halo source attach/detach, auto-collapse on empty) so
/// consumer pages don't reimplement it inline.
/// </summary>
/// <remarks>
/// Used by <c>BrowsePage</c>; HomePage will migrate to it in a follow-up.
/// </remarks>
public sealed partial class HeroBandPanel : UserControl
{
    private long _accentToken;
    private bool _wired;

    public HeroBandPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty SlidesProperty = DependencyProperty.Register(
        nameof(Slides), typeof(IList<HeroCarouselItem>), typeof(HeroBandPanel),
        new PropertyMetadata(null, (d, _) => ((HeroBandPanel)d).OnSlidesChanged()));

    public IList<HeroCarouselItem>? Slides
    {
        get => (IList<HeroCarouselItem>?)GetValue(SlidesProperty);
        set => SetValue(SlidesProperty, value);
    }

    private void OnSlidesChanged()
    {
        var slides = Slides;
        Carousel.ItemsSource = slides;
        Visibility = slides is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    public static readonly DependencyProperty AutoplayProperty = DependencyProperty.Register(
        nameof(Autoplay), typeof(bool), typeof(HeroBandPanel),
        new PropertyMetadata(true, (d, e) => ((HeroBandPanel)d).Carousel.Autoplay = (bool)e.NewValue));

    public bool Autoplay
    {
        get => (bool)GetValue(AutoplayProperty);
        set => SetValue(AutoplayProperty, value);
    }

    public static readonly DependencyProperty AutoplayIntervalProperty = DependencyProperty.Register(
        nameof(AutoplayInterval), typeof(TimeSpan), typeof(HeroBandPanel),
        new PropertyMetadata(TimeSpan.FromSeconds(7), (d, e) => ((HeroBandPanel)d).Carousel.AutoplayInterval = (TimeSpan)e.NewValue));

    public TimeSpan AutoplayInterval
    {
        get => (TimeSpan)GetValue(AutoplayIntervalProperty);
        set => SetValue(AutoplayIntervalProperty, value);
    }

    public static readonly DependencyProperty HaloBlurRadiusProperty = DependencyProperty.Register(
        nameof(HaloBlurRadius), typeof(double), typeof(HeroBandPanel),
        new PropertyMetadata(120.0, (d, e) => HeroHalo.SetBlurRadius(((HeroBandPanel)d).HaloBackdrop, (double)e.NewValue)));

    public double HaloBlurRadius
    {
        get => (double)GetValue(HaloBlurRadiusProperty);
        set => SetValue(HaloBlurRadiusProperty, value);
    }

    public static readonly DependencyProperty HaloCornerRadiusProperty = DependencyProperty.Register(
        nameof(HaloCornerRadius), typeof(double), typeof(HeroBandPanel),
        new PropertyMetadata(14.0, (d, e) => HeroHalo.SetCornerRadius(((HeroBandPanel)d).HaloBackdrop, (double)e.NewValue)));

    public double HaloCornerRadius
    {
        get => (double)GetValue(HaloCornerRadiusProperty);
        set => SetValue(HaloCornerRadiusProperty, value);
    }

    public static readonly DependencyProperty HaloIntensityProperty = DependencyProperty.Register(
        nameof(HaloIntensity), typeof(double), typeof(HeroBandPanel),
        new PropertyMetadata(0.45, (d, e) => HeroHalo.SetIntensity(((HeroBandPanel)d).HaloBackdrop, (double)e.NewValue)));

    public double HaloIntensity
    {
        get => (double)GetValue(HaloIntensityProperty);
        set => SetValue(HaloIntensityProperty, value);
    }

    /// <summary>Fires per-frame as the carousel's lerped accent changes.</summary>
    public event TypedEventHandler<HeroBandPanel, Color>? CurrentAccentChanged;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_wired) return;
        _wired = true;

        Carousel.Autoplay = Autoplay;
        Carousel.AutoplayInterval = AutoplayInterval;

        HeroHalo.SetBlurRadius(HaloBackdrop, HaloBlurRadius);
        HeroHalo.SetCornerRadius(HaloBackdrop, HaloCornerRadius);
        HeroHalo.SetIntensity(HaloBackdrop, HaloIntensity);
        HeroHalo.SetSource(HaloBackdrop, Carousel);

        _accentToken = Carousel.RegisterPropertyChangedCallback(
            HeroCarousel.CurrentAccentProperty, OnAccentChanged);

        // Force-rebuild the carousel slides now that all of its internal
        // composition resources (compositor, _stageRoot, _interaction,
        // _surfaceCache) are guaranteed initialized. The DP-change callback
        // fired earlier from `OnSlidesChanged` may have hit
        // `HeroCarousel.RebuildSlides` while those internals were still null.
        if (Slides is { Count: > 0 })
        {
            Carousel.ItemsSource = null;
            Carousel.ItemsSource = Slides;
        }
        Visibility = Slides is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        CurrentAccentChanged?.Invoke(this, Carousel.CurrentAccent);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_wired) return;
        _wired = false;

        if (_accentToken != 0)
        {
            Carousel.UnregisterPropertyChangedCallback(HeroCarousel.CurrentAccentProperty, _accentToken);
            _accentToken = 0;
        }
        HeroHalo.SetSource(HaloBackdrop, null);
    }

    private void OnAccentChanged(DependencyObject sender, DependencyProperty dp)
    {
        CurrentAccentChanged?.Invoke(this, Carousel.CurrentAccent);
    }
}
