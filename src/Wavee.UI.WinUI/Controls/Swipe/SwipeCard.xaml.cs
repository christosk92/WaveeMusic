using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.Services.Playlists;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Swipe;

public enum SwipePreviewState { None, Loading, Playing, Unavailable }

/// <summary>
/// Tinder-style audition card: full-bleed album art, title/artist, a 0:30 snippet bar and Keep/Remove
/// stamps. Owns pointer drag (tilt + stamp reveal + fly-off / spring-back) on the GPU composition
/// layer; buttons and keys reuse <see cref="CommitDecision"/>. Raises <see cref="DecisionCommitted"/>
/// once the fly-off completes.
/// </summary>
public sealed partial class SwipeCard : UserControl
{
    private const double TiltDeg = 14, DistFrac = 0.30, VelThreshold = 0.6;

    private bool _dragging, _committing;
    private Point _start;
    private double _lastX, _lastT, _vx, _width = 316;
    private Storyboard? _eq;

    public SwipeCard()
    {
        InitializeComponent();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        Loaded += (_, _) =>
        {
            ElementCompositionPreview.SetIsTranslationEnabled(this, true);
            var v = ElementCompositionPreview.GetElementVisual(this);
            v.CenterPoint = new Vector3((float)(ActualWidth / 2), (float)ActualHeight, 0);
        };
    }

    // ── Dependency properties ──
    public static readonly DependencyProperty ImageUrlProperty = DependencyProperty.Register(
        nameof(ImageUrl), typeof(string), typeof(SwipeCard), new PropertyMetadata(null));
    public string? ImageUrl { get => (string?)GetValue(ImageUrlProperty); set => SetValue(ImageUrlProperty, value); }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SwipeCard), new PropertyMetadata(null));
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty ArtistNameProperty = DependencyProperty.Register(
        nameof(ArtistName), typeof(string), typeof(SwipeCard), new PropertyMetadata(null));
    public string? ArtistName { get => (string?)GetValue(ArtistNameProperty); set => SetValue(ArtistNameProperty, value); }

    public static readonly DependencyProperty PreviewProgressProperty = DependencyProperty.Register(
        nameof(PreviewProgress), typeof(double), typeof(SwipeCard), new PropertyMetadata(0d));
    public double PreviewProgress { get => (double)GetValue(PreviewProgressProperty); set => SetValue(PreviewProgressProperty, value); }

    public static readonly DependencyProperty PreviewStateProperty = DependencyProperty.Register(
        nameof(PreviewState), typeof(SwipePreviewState), typeof(SwipeCard),
        new PropertyMetadata(SwipePreviewState.None, OnPreviewStateChanged));
    public SwipePreviewState PreviewState { get => (SwipePreviewState)GetValue(PreviewStateProperty); set => SetValue(PreviewStateProperty, value); }

    public static readonly DependencyProperty IsNewProperty = DependencyProperty.Register(
        nameof(IsNew), typeof(bool), typeof(SwipeCard), new PropertyMetadata(false, OnIsNewChanged));
    public bool IsNew { get => (bool)GetValue(IsNewProperty); set => SetValue(IsNewProperty, value); }

    /// <summary>Raised after a decision's fly-off animation finishes. Argument is the chosen direction.</summary>
    public event EventHandler<SwipeDirection>? DecisionCommitted;

    private static void OnIsNewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SwipeCard)d).NewPill.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;

    private static void OnPreviewStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (SwipeCard)d;
        var s = (SwipePreviewState)e.NewValue;
        var na = s == SwipePreviewState.Unavailable;
        c.NaLabel.Visibility = na ? Visibility.Visible : Visibility.Collapsed;
        c.SnippetBar.Visibility = na ? Visibility.Collapsed : Visibility.Visible;
        if (s == SwipePreviewState.Playing) c.StartEq(); else c.StopEq();
    }

    // ── Pointer drag ──
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_committing) return;
        var p = e.GetCurrentPoint(this);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !p.Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _width = ActualWidth > 0 ? ActualWidth : 316;
        _start = p.Position; _lastX = p.Position.X; _lastT = 0; _vx = 0;
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetCurrentPoint(this).Position;
        double dx = pos.X - _start.X, dy = pos.Y - _start.Y;
        _vx = pos.X - _lastX; _lastX = pos.X;
        var v = ElementCompositionPreview.GetElementVisual(this);
        v.Properties.InsertVector3("Translation", new Vector3((float)dx, (float)(dy * 0.14), 0));
        v.RotationAngleInDegrees = (float)Math.Clamp(dx / _width * TiltDeg, -TiltDeg, TiltDeg);
        var t = Math.Min(1, Math.Abs(dx) / (_width * 0.32));
        LikeStamp.Opacity = dx > 0 ? t : 0;
        NopeStamp.Opacity = dx < 0 ? t : 0;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCaptures();
        double dx = e.GetCurrentPoint(this).Position.X - _start.X;
        if ((Math.Abs(dx) > _width * DistFrac || Math.Abs(_vx) > VelThreshold) && dx != 0)
            FlyOff(dx > 0 ? SwipeDirection.Right : SwipeDirection.Left);
        else
            SpringBack();
    }

    /// <summary>Button / keyboard entry point — animates the fly-off from the resting position.</summary>
    public void CommitDecision(SwipeDirection dir) => FlyOff(dir);

    private void FlyOff(SwipeDirection dir)
    {
        if (_committing) return;
        _committing = true;
        var v = ElementCompositionPreview.GetElementVisual(this);
        var c = v.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f));
        var offX = (dir == SwipeDirection.Right ? 1 : -1) * _width * 1.7;

        var move = c.CreateVector3KeyFrameAnimation();
        move.Target = "Translation";
        move.InsertKeyFrame(1f, new Vector3((float)offX, -10f, 0f), ease);
        move.Duration = TimeSpan.FromMilliseconds(300);

        var rot = c.CreateScalarKeyFrameAnimation();
        rot.Target = "RotationAngleInDegrees";
        rot.InsertKeyFrame(1f, dir == SwipeDirection.Right ? 22f : -22f, ease);
        rot.Duration = TimeSpan.FromMilliseconds(300);

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) => DecisionCommitted?.Invoke(this, dir);
        v.StartAnimation("Translation", move);
        v.StartAnimation("RotationAngleInDegrees", rot);
        batch.End();
    }

    private void SpringBack()
    {
        var v = ElementCompositionPreview.GetElementVisual(this);
        var c = v.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.4f), new Vector2(0.64f, 1f));
        var back = c.CreateVector3KeyFrameAnimation();
        back.Target = "Translation";
        back.InsertKeyFrame(1f, Vector3.Zero, ease);
        back.Duration = TimeSpan.FromMilliseconds(240);
        var rot = c.CreateScalarKeyFrameAnimation();
        rot.Target = "RotationAngleInDegrees";
        rot.InsertKeyFrame(1f, 0f, ease);
        rot.Duration = TimeSpan.FromMilliseconds(240);
        v.StartAnimation("Translation", back);
        v.StartAnimation("RotationAngleInDegrees", rot);
        LikeStamp.Opacity = 0; NopeStamp.Opacity = 0;
    }

    /// <summary>Snap the (flown-off) card back to centre for the next bound track.</summary>
    public void ResetVisual()
    {
        _committing = false;
        var v = ElementCompositionPreview.GetElementVisual(this);
        v.StopAnimation("Translation");
        v.StopAnimation("RotationAngleInDegrees");
        v.Properties.InsertVector3("Translation", Vector3.Zero);
        v.RotationAngleInDegrees = 0;
        LikeStamp.Opacity = 0; NopeStamp.Opacity = 0;
    }

    /// <summary>Subtle scale/fade-in for a freshly-bound card.</summary>
    public void AnimateEnter()
    {
        var v = ElementCompositionPreview.GetElementVisual(this);
        var c = v.Compositor;
        v.CenterPoint = new Vector3((float)(ActualWidth / 2), (float)(ActualHeight / 2), 0);
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f));
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.94f, 0.94f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, ease);
        scale.Duration = TimeSpan.FromMilliseconds(220);
        v.StartAnimation("Scale", scale);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0.4f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        v.StartAnimation("Opacity", fade);
    }

    private void StartEq()
    {
        Eq.Visibility = Visibility.Visible;
        _eq ??= BuildEq();
        _eq.Begin();
    }

    private void StopEq()
    {
        _eq?.Stop();
        Eq.Visibility = Visibility.Collapsed;
    }

    private Storyboard BuildEq()
    {
        var sb = new Storyboard();
        var bars = new (Microsoft.UI.Xaml.Shapes.Rectangle Bar, double Delay)[]
        {
            (EqBar1, 0), (EqBar2, 150), (EqBar3, 300), (EqBar4, 450),
        };
        foreach (var (bar, delay) in bars)
        {
            var a = new DoubleAnimation
            {
                From = 5, To = 16,
                Duration = new Duration(TimeSpan.FromMilliseconds(450)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(a, bar);
            Storyboard.SetTargetProperty(a, "Height");
            sb.Children.Add(a);
        }
        return sb;
    }
}
