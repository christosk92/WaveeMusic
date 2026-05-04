using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class PendingBorderBeam : UserControl
{
    private const double CycleSeconds = 1.8;
    private const double EdgeSeconds = CycleSeconds;
    private const double FadeSeconds = 0.1;
    private Storyboard? _storyboard;
    private bool _isRunning;

    public static readonly DependencyProperty BeamThicknessProperty =
        DependencyProperty.Register(nameof(BeamThickness), typeof(double), typeof(PendingBorderBeam),
            new PropertyMetadata(2.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty BeamLengthProperty =
        DependencyProperty.Register(nameof(BeamLength), typeof(double), typeof(PendingBorderBeam),
            new PropertyMetadata(150.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(PendingBorderBeam),
            new PropertyMetadata(new CornerRadius(8), OnVisualPropertyChanged));

    public double BeamThickness
    {
        get => (double)GetValue(BeamThicknessProperty);
        set => SetValue(BeamThicknessProperty, value);
    }

    public double BeamLength
    {
        get => (double)GetValue(BeamLengthProperty);
        set => SetValue(BeamLengthProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public PendingBorderBeam()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += OnActualThemeChanged;
        ConfigureBrushes();
    }

    public void Start()
    {
        _isRunning = true;
        Visibility = Visibility.Visible;
        RebuildStoryboard();
    }

    public void Stop()
    {
        _isRunning = false;
        _storyboard?.Stop();
        Visibility = Visibility.Collapsed;
        SetBeamOpacity(0);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PendingBorderBeam)d).RebuildStoryboard();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ConfigureBrushes();
        if (_isRunning)
            RebuildStoryboard();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => _storyboard?.Stop();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        => RebuildStoryboard();

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ConfigureBrushes();
        RebuildStoryboard();
    }

    private void RebuildStoryboard()
    {
        _storyboard?.Stop();
        _storyboard = null;

        var width = ActualWidth;
        var height = ActualHeight;
        if (!_isRunning || width <= 0 || height <= 0 || TopBeam == null)
            return;

        Root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };

        var thickness = Math.Max(1, BeamThickness);
        BaseBorder.BorderThickness = new Thickness(thickness);
        BaseBorder.CornerRadius = CornerRadius;
        var horizontalLength = Math.Min(BeamLength, Math.Clamp(width * 0.42, 56, 160));
        var verticalLength = Math.Min(BeamLength, Math.Clamp(height * 0.65, 28, 120));

        ConfigureBeam(TopBeam, horizontalLength, thickness, 0, 0);
        ConfigureBeam(RightBeam, thickness, verticalLength, Math.Max(0, width - thickness), 0);
        ConfigureBeam(BottomBeam, horizontalLength, thickness, 0, Math.Max(0, height - thickness));
        ConfigureBeam(LeftBeam, thickness, verticalLength, 0, 0);

        _storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        AddOutlinePulse(_storyboard);
        AddEdge(_storyboard, TopBeam, translateX: true, startTime: 0,
            from: -horizontalLength, to: width);
        AddEdge(_storyboard, RightBeam, translateX: false, startTime: 0,
            from: -verticalLength, to: height);
        AddEdge(_storyboard, BottomBeam, translateX: true, startTime: 0,
            from: width, to: -horizontalLength);
        AddEdge(_storyboard, LeftBeam, translateX: false, startTime: 0,
            from: height, to: -verticalLength);

        _storyboard.Begin();
    }

    private static void ConfigureBeam(Border beam, double width, double height, double left, double top)
    {
        beam.Width = width;
        beam.Height = height;
        beam.CornerRadius = new CornerRadius(Math.Max(width, height));
        beam.Opacity = 0;
        beam.RenderTransform = new CompositeTransform();
        Canvas.SetLeft(beam, left);
        Canvas.SetTop(beam, top);
    }

    private void ConfigureBrushes()
    {
        var color = ResolveBeamColor();
        BaseBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(210, color.R, color.G, color.B));
        TopBeam.Background = CreateBrush(color, horizontal: true, reverse: false);
        RightBeam.Background = CreateBrush(color, horizontal: false, reverse: false);
        BottomBeam.Background = CreateBrush(color, horizontal: true, reverse: true);
        LeftBeam.Background = CreateBrush(color, horizontal: false, reverse: true);
    }

    private static LinearGradientBrush CreateBrush(Color color, bool horizontal, bool reverse)
    {
        var transparent = Color.FromArgb(0, color.R, color.G, color.B);
        var brush = new LinearGradientBrush
        {
            StartPoint = horizontal
                ? new Point(reverse ? 1 : 0, 0.5)
                : new Point(0.5, reverse ? 1 : 0),
            EndPoint = horizontal
                ? new Point(reverse ? 0 : 1, 0.5)
                : new Point(0.5, reverse ? 0 : 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = transparent, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0.42 });
        brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0.66 });
        brush.GradientStops.Add(new GradientStop { Color = transparent, Offset = 1 });
        return brush;
    }

    private void AddOutlinePulse(Storyboard storyboard)
    {
        BaseBorder.Opacity = 0.32;
        var opacity = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(CycleSeconds) };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0.32 });
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(CycleSeconds * 0.28), Value = 0.92 });
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(CycleSeconds * 0.56), Value = 0.32 });
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(CycleSeconds), Value = 0.32 });

        Storyboard.SetTarget(opacity, BaseBorder);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);
    }

    private static void AddEdge(Storyboard storyboard, FrameworkElement beam, bool translateX, double startTime, double from, double to)
    {
        var translation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(CycleSeconds) };
        if (startTime > 0)
        {
            translation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
            translation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(startTime), Value = from });
        }
        else
        {
            translation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        }

        var endTime = startTime + EdgeSeconds;
        translation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(endTime), Value = to });
        if (endTime < CycleSeconds)
            translation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(Math.Min(CycleSeconds, endTime + 0.01)), Value = from });
        translation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(CycleSeconds), Value = from });

        Storyboard.SetTarget(translation, beam);
        Storyboard.SetTargetProperty(translation, translateX
            ? "(UIElement.RenderTransform).(CompositeTransform.TranslateX)"
            : "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        storyboard.Children.Add(translation);

        var opacity = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(CycleSeconds) };
        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = startTime == 0 ? 1 : 0 });
        if (startTime > 0)
        {
            opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(startTime), Value = 0 });
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(startTime + FadeSeconds), Value = 1 });
        }

        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(Math.Max(startTime, endTime - FadeSeconds)), Value = 1 });
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(endTime), Value = 0 });
        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(CycleSeconds), Value = 0 });

        Storyboard.SetTarget(opacity, beam);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);
    }

    private void SetBeamOpacity(double opacity)
    {
        TopBeam.Opacity = opacity;
        RightBeam.Opacity = opacity;
        BottomBeam.Opacity = opacity;
        LeftBeam.Opacity = opacity;
    }

    private static Color ResolveBeamColor()
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
