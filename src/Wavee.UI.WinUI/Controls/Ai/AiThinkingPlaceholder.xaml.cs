using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Ai;

public sealed partial class AiThinkingPlaceholder : UserControl
{
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption),
        typeof(string),
        typeof(AiThinkingPlaceholder),
        new PropertyMetadata("thinking..."));

    private Storyboard? _sweepStoryboard;
    private Storyboard? _pulseStoryboard;
    private bool _animationsEnabled = true;

    public AiThinkingPlaceholder()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _animationsEnabled = AreAnimationsEnabled();
        UpdateClip();
        StartAnimations();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimations();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateClip();
        if (IsLoaded)
            StartAnimations();
    }

    private void StartAnimations()
    {
        StopAnimations();
        UpdateSweepGeometry();

        if (!_animationsEnabled)
        {
            SweepRectangle.Opacity = 0.18;
            CaptionStack.Opacity = 1;
            return;
        }

        var width = Math.Max(ActualWidth, 360d);
        var sweepWidth = Math.Max(260d, width * 0.36d);

        var sweep = new DoubleAnimation
        {
            From = -sweepWidth,
            To = width + sweepWidth,
            Duration = new Duration(TimeSpan.FromMilliseconds(2400)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(sweep, SweepTranslate);
        Storyboard.SetTargetProperty(sweep, nameof(TranslateTransform.X));

        _sweepStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        _sweepStoryboard.Children.Add(sweep);
        _sweepStoryboard.Begin();

        var pulse = new DoubleAnimation
        {
            From = 0.62,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(pulse, CaptionStack);
        Storyboard.SetTargetProperty(pulse, nameof(Opacity));

        _pulseStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        _pulseStoryboard.Children.Add(pulse);
        _pulseStoryboard.Begin();
    }

    private void StopAnimations()
    {
        _sweepStoryboard?.Stop();
        _pulseStoryboard?.Stop();
        _sweepStoryboard = null;
        _pulseStoryboard = null;
    }

    private void UpdateSweepGeometry()
    {
        var width = Math.Max(ActualWidth, 360d);
        var sweepWidth = Math.Max(260d, width * 0.36d);
        SweepRectangle.Width = sweepWidth;
        SweepTranslate.X = -sweepWidth;
    }

    private void UpdateClip()
    {
        RootGrid.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)),
        };
    }

    private static bool AreAnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }
}
