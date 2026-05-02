using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class PodcastChapterTimelineRail : UserControl
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress),
            typeof(double),
            typeof(PodcastChapterTimelineRail),
            new PropertyMetadata(0d, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(PodcastChapterTimelineRail),
            new PropertyMetadata(false, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty IsCompletedProperty =
        DependencyProperty.Register(
            nameof(IsCompleted),
            typeof(bool),
            typeof(PodcastChapterTimelineRail),
            new PropertyMetadata(false, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty IsFirstProperty =
        DependencyProperty.Register(
            nameof(IsFirst),
            typeof(bool),
            typeof(PodcastChapterTimelineRail),
            new PropertyMetadata(false, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty IsLastProperty =
        DependencyProperty.Register(
            nameof(IsLast),
            typeof(bool),
            typeof(PodcastChapterTimelineRail),
            new PropertyMetadata(false, OnVisualStatePropertyChanged));

    private double _appliedTopProgress = -1;
    private double _appliedBottomProgress = -1;
    private bool _loaded;

    public PodcastChapterTimelineRail()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsCompleted
    {
        get => (bool)GetValue(IsCompletedProperty);
        set => SetValue(IsCompletedProperty, value);
    }

    public bool IsFirst
    {
        get => (bool)GetValue(IsFirstProperty);
        set => SetValue(IsFirstProperty, value);
    }

    public bool IsLast
    {
        get => (bool)GetValue(IsLastProperty);
        set => SetValue(IsLastProperty, value);
    }

    private static void OnVisualStatePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PodcastChapterTimelineRail rail)
            rail.ApplyVisualState(animate: rail._loaded);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        ApplyVisualState(animate: false);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TopProgressFill.Height = Math.Max(0, TopTrack.ActualHeight);
        BottomProgressFill.Height = Math.Max(0, BottomTrack.ActualHeight);
        ApplyProgress(animate: false);
    }

    private void ApplyVisualState(bool animate)
    {
        ApplyProgress(animate);
        ApplyDotState(animate);
    }

    private void ApplyProgress(bool animate)
    {
        var progress = Math.Clamp(Progress, 0d, 1d);
        var topProgress = IsFirst ? 0 : Math.Clamp(progress * 2, 0d, 1d);
        var bottomProgress = IsLast ? 0 : Math.Clamp((progress - 0.5d) * 2d, 0d, 1d);

        TopTrack.Opacity = IsFirst ? 0 : 0.42;
        TopProgressFill.Opacity = IsFirst ? 0 : 1;
        BottomTrack.Opacity = IsLast ? 0 : 0.42;
        BottomProgressFill.Opacity = IsLast ? 0 : 1;

        TopProgressFill.Height = Math.Max(0, TopTrack.ActualHeight);
        BottomProgressFill.Height = Math.Max(0, BottomTrack.ActualHeight);

        ApplySegmentProgress(TopProgressFill, topProgress, ref _appliedTopProgress, animate);
        ApplySegmentProgress(BottomProgressFill, bottomProgress, ref _appliedBottomProgress, animate);
    }

    private static void ApplySegmentProgress(
        FrameworkElement segment,
        double progress,
        ref double appliedProgress,
        bool animate)
    {
        var visual = ElementCompositionPreview.GetElementVisual(segment);
        visual.CenterPoint = new Vector3(
            (float)Math.Max(1, segment.ActualWidth) / 2f,
            0,
            0);

        var target = new Vector3(1f, (float)progress, 1f);
        if (!animate || appliedProgress < 0 || Math.Abs(appliedProgress - progress) < 0.001)
        {
            visual.Scale = target;
            appliedProgress = progress;
            return;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(220);
        animation.InsertKeyFrame(1f, target, easing);
        visual.StartAnimation(nameof(Visual.Scale), animation);
        appliedProgress = progress;
    }

    private void ApplyDotState(bool animate)
    {
        var dotVisual = ElementCompositionPreview.GetElementVisual(Dot);
        var haloVisual = ElementCompositionPreview.GetElementVisual(DotHalo);
        var scale = IsActive ? 1.18f : IsCompleted ? 1f : 0.82f;
        var opacity = IsActive || IsCompleted ? 1f : 0.44f;
        var haloOpacity = IsActive ? 0.32f : 0f;

        dotVisual.CenterPoint = new Vector3(5f, 5f, 0);
        haloVisual.CenterPoint = new Vector3(10f, 10f, 0);

        if (!animate)
        {
            dotVisual.Scale = new Vector3(scale, scale, 1f);
            dotVisual.Opacity = opacity;
            haloVisual.Scale = new Vector3(IsActive ? 1f : 0.65f);
            haloVisual.Opacity = haloOpacity;
            return;
        }

        var compositor = dotVisual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));

        var dotScale = compositor.CreateVector3KeyFrameAnimation();
        dotScale.Duration = TimeSpan.FromMilliseconds(180);
        dotScale.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), easing);
        dotVisual.StartAnimation(nameof(Visual.Scale), dotScale);

        var dotOpacity = compositor.CreateScalarKeyFrameAnimation();
        dotOpacity.Duration = TimeSpan.FromMilliseconds(180);
        dotOpacity.InsertKeyFrame(1f, opacity, easing);
        dotVisual.StartAnimation(nameof(Visual.Opacity), dotOpacity);

        var haloScale = compositor.CreateVector3KeyFrameAnimation();
        haloScale.Duration = TimeSpan.FromMilliseconds(220);
        haloScale.InsertKeyFrame(1f, new Vector3(IsActive ? 1f : 0.65f), easing);
        haloVisual.StartAnimation(nameof(Visual.Scale), haloScale);

        var haloFade = compositor.CreateScalarKeyFrameAnimation();
        haloFade.Duration = TimeSpan.FromMilliseconds(220);
        haloFade.InsertKeyFrame(1f, haloOpacity, easing);
        haloVisual.StartAnimation(nameof(Visual.Opacity), haloFade);
    }
}
