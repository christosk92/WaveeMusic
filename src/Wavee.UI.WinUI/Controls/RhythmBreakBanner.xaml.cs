using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// "On Tour Now" rhythm-break banner used between dense music-data sections on
/// the V4A artist page. Acrylic backdrop + dual-ring pulse dot + headline +
/// CTA. The outer ring expands and fades over 1.8 s while the inner core does
/// the inverse — Spotify's standard live-pulse cadence.
/// </summary>
public sealed partial class RhythmBreakBanner : UserControl
{
    public event EventHandler<RoutedEventArgs>? CardClick;

    public static readonly DependencyProperty EyebrowProperty =
        DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(RhythmBreakBanner),
            new PropertyMetadata("ON TOUR NOW", OnEyebrowChanged));

    public static readonly DependencyProperty HeadlineProperty =
        DependencyProperty.Register(nameof(Headline), typeof(string), typeof(RhythmBreakBanner),
            new PropertyMetadata(string.Empty, OnHeadlineChanged));

    public static readonly DependencyProperty SublineProperty =
        DependencyProperty.Register(nameof(Subline), typeof(string), typeof(RhythmBreakBanner),
            new PropertyMetadata(string.Empty, OnSublineChanged));

    public static readonly DependencyProperty CtaTextProperty =
        DependencyProperty.Register(nameof(CtaText), typeof(string), typeof(RhythmBreakBanner),
            new PropertyMetadata("See dates & tickets ›", OnCtaTextChanged));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(RhythmBreakBanner),
            new PropertyMetadata(null, OnAccentBrushChanged));

    public string Eyebrow { get => (string)GetValue(EyebrowProperty); set => SetValue(EyebrowProperty, value); }
    public string Headline { get => (string)GetValue(HeadlineProperty); set => SetValue(HeadlineProperty, value); }
    public string Subline { get => (string)GetValue(SublineProperty); set => SetValue(SublineProperty, value); }
    public string CtaText { get => (string)GetValue(CtaTextProperty); set => SetValue(CtaTextProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public RhythmBreakBanner()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => StartPulse();
    private void OnUnloaded(object sender, RoutedEventArgs e) => StopPulse();

    private void StartPulse()
    {
        if (PulseRingOuter == null || PulseDot == null) return;

        var outerVisual = ElementCompositionPreview.GetElementVisual(PulseRingOuter);
        var innerVisual = ElementCompositionPreview.GetElementVisual(PulseDot);
        var compositor = outerVisual.Compositor;

        outerVisual.CenterPoint = new Vector3(7f, 7f, 0f);
        innerVisual.CenterPoint = new Vector3(4f, 4f, 0f);

        var outerScale = compositor.CreateVector3KeyFrameAnimation();
        outerScale.InsertKeyFrame(0f, new Vector3(0.6f, 0.6f, 0f));
        outerScale.InsertKeyFrame(1f, new Vector3(2.1f, 2.1f, 0f));
        outerScale.Duration = TimeSpan.FromMilliseconds(1800);
        outerScale.IterationBehavior = AnimationIterationBehavior.Forever;

        var outerOpacity = compositor.CreateScalarKeyFrameAnimation();
        outerOpacity.InsertKeyFrame(0f, 0.55f);
        outerOpacity.InsertKeyFrame(1f, 0f);
        outerOpacity.Duration = TimeSpan.FromMilliseconds(1800);
        outerOpacity.IterationBehavior = AnimationIterationBehavior.Forever;

        var innerScale = compositor.CreateVector3KeyFrameAnimation();
        innerScale.InsertKeyFrame(0f, new Vector3(1f, 1f, 0f));
        innerScale.InsertKeyFrame(0.5f, new Vector3(1.25f, 1.25f, 0f));
        innerScale.InsertKeyFrame(1f, new Vector3(1f, 1f, 0f));
        innerScale.Duration = TimeSpan.FromMilliseconds(1800);
        innerScale.IterationBehavior = AnimationIterationBehavior.Forever;

        outerVisual.StartAnimation("Scale", outerScale);
        outerVisual.StartAnimation("Opacity", outerOpacity);
        innerVisual.StartAnimation("Scale", innerScale);
    }

    private void StopPulse()
    {
        if (PulseRingOuter == null || PulseDot == null) return;
        try
        {
            var outerVisual = ElementCompositionPreview.GetElementVisual(PulseRingOuter);
            var innerVisual = ElementCompositionPreview.GetElementVisual(PulseDot);
            outerVisual.StopAnimation("Scale");
            outerVisual.StopAnimation("Opacity");
            innerVisual.StopAnimation("Scale");
        }
        catch
        {
            // Element torn down already.
        }
    }

    private static void OnEyebrowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b) b.EyebrowText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnHeadlineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b) b.HeadlineText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnSublineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b)
        {
            var text = e.NewValue as string ?? string.Empty;
            b.SublineText.Text = text;
            b.SublineText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void OnCtaTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b) b.CtaLink.Content = e.NewValue as string ?? string.Empty;
    }

    private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b && e.NewValue is SolidColorBrush brush)
        {
            b.PulseDot.Fill = brush;
            b.PulseRingOuter.Fill = brush;
        }
    }

    private void CardButton_Click(object sender, RoutedEventArgs e) => CardClick?.Invoke(this, e);
}
