using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI;

namespace Wavee.UI.WinUI.Controls.Playback;

/// <summary>
/// Composition-driven horizontal progress bar — replaces a bound Slider in the
/// playback chrome. The fill animates on the composition thread via a single
/// linear ScalarKeyFrameAnimation on Visual.Scale.X, while the view model
/// updates the anchor position on service events, seeks, track changes, and a
/// coarse 1 Hz interpolation tick. That keeps newly-loaded player surfaces in
/// sync with the displayed time without returning to per-frame UI work.
///
/// Bind <see cref="PositionMs"/> to <c>PlayerBarViewModel.AnchorPositionMs</c>.
/// </summary>
public sealed class CompositionProgressBar : UserControl
{
    private const double TrackHeight = 3.0;
    private const double TrackHeightHover = 4.0;
    private const double CornerRadiusValue = 1.5;
    private const double ThumbSize = 12.0;
    private const double ThumbFadeMs = 120.0;

    private readonly Border _trackBorder;
    private readonly Border _fillBorder;
    private readonly Border _thumbBorder;
    private Visual? _fillVisual;
    private Visual? _thumbVisual;
    private ExpressionAnimation? _thumbOffsetExpression;
    private Compositor? _compositor;
    private bool _templateApplied;

    private bool _isDragging;
    private bool _isHovering;
    private double _lastDragRatio;

    public CompositionProgressBar()
    {
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };

        _trackBorder = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(CornerRadiusValue),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (Brush?)Application.Current.Resources["ControlFillColorTertiaryBrush"],
        };

        // Fill is full-width on layout; we drive the visible width via the
        // visual's Scale.X. Anchor at left (CenterPoint X=0) so scale grows
        // rightward.
        _fillBorder = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(CornerRadiusValue),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (Brush?)Application.Current.Resources["AccentFillColorDefaultBrush"],
        };

        // Thumb: sits at the right edge of the fill. Position is bound to the
        // fill's Scale.X via a Composition ExpressionAnimation, so the thumb
        // glides along the bar in lock-step with the fill — also entirely on
        // the composition thread, no UI-thread per-frame work.
        _thumbBorder = new Border
        {
            Width = ThumbSize,
            Height = ThumbSize,
            CornerRadius = new CornerRadius(ThumbSize / 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush?)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Opacity = 0,
            IsHitTestVisible = false,
        };

        root.Children.Add(_trackBorder);
        root.Children.Add(_fillBorder);
        root.Children.Add(_thumbBorder);
        Content = root;

        Height = 16;
        MinHeight = 16;
        IsTabStop = true;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Resync();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    // ── Public properties ────────────────────────────────────────────────

    public double PositionMs
    {
        get => (double)GetValue(PositionMsProperty);
        set => SetValue(PositionMsProperty, value);
    }
    public static readonly DependencyProperty PositionMsProperty =
        DependencyProperty.Register(nameof(PositionMs), typeof(double), typeof(CompositionProgressBar),
            new PropertyMetadata(0.0, OnAnimationInputChanged));

    public double DurationMs
    {
        get => (double)GetValue(DurationMsProperty);
        set => SetValue(DurationMsProperty, value);
    }
    public static readonly DependencyProperty DurationMsProperty =
        DependencyProperty.Register(nameof(DurationMs), typeof(double), typeof(CompositionProgressBar),
            new PropertyMetadata(0.0, OnAnimationInputChanged));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }
    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(CompositionProgressBar),
            new PropertyMetadata(false, OnAnimationInputChanged));

    public bool IsBuffering
    {
        get => (bool)GetValue(IsBufferingProperty);
        set => SetValue(IsBufferingProperty, value);
    }
    public static readonly DependencyProperty IsBufferingProperty =
        DependencyProperty.Register(nameof(IsBuffering), typeof(bool), typeof(CompositionProgressBar),
            new PropertyMetadata(false, OnAnimationInputChanged));

    // ── Events ───────────────────────────────────────────────────────────

    public event EventHandler? SeekStarted;
    public event EventHandler<double>? SeekCommitted;

    // ── Animation core ───────────────────────────────────────────────────

    private static void OnAnimationInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CompositionProgressBar bar) bar.Resync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _fillVisual = ElementCompositionPreview.GetElementVisual(_fillBorder);
        _compositor = _fillVisual.Compositor;
        // Anchor the scale at the left edge so X-scaling grows rightward.
        _fillVisual.CenterPoint = new Vector3(0f, 0f, 0f);

        _thumbVisual = ElementCompositionPreview.GetElementVisual(_thumbBorder);
        // Implicit animation makes the opacity transition smooth (hover-fade)
        // without any UI-thread per-frame work — the GPU interpolates.
        var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.Target = nameof(Visual.Opacity);
        opacityAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
        opacityAnim.Duration = TimeSpan.FromMilliseconds(ThumbFadeMs);
        var implicitCollection = _compositor.CreateImplicitAnimationCollection();
        implicitCollection[nameof(Visual.Opacity)] = opacityAnim;
        _thumbVisual.ImplicitAnimations = implicitCollection;

        WireThumbToFill();
        _templateApplied = true;
        Resync();
    }

    /// <summary>
    /// Bind the thumb's Offset.X to the fill's Scale.X via a Composition
    /// ExpressionAnimation: thumb sits at <c>scale × trackWidth − thumbHalfWidth</c>.
    /// Re-wired on every SizeChanged because trackWidth is captured as a scalar
    /// parameter; ExpressionAnimations don't observe XAML layout changes.
    /// </summary>
    private void WireThumbToFill()
    {
        if (_thumbVisual == null || _fillVisual == null || _compositor == null) return;

        _thumbVisual.StopAnimation("Offset.X");
        _thumbOffsetExpression?.Dispose();

        var width = (float)Math.Max(0, ActualWidth);
        var halfThumb = (float)(ThumbSize / 2);

        _thumbOffsetExpression = _compositor.CreateExpressionAnimation(
            "fill.Scale.X * trackWidth - halfThumb");
        _thumbOffsetExpression.SetReferenceParameter("fill", _fillVisual);
        _thumbOffsetExpression.SetScalarParameter("trackWidth", width);
        _thumbOffsetExpression.SetScalarParameter("halfThumb", halfThumb);
        _thumbVisual.StartAnimation("Offset.X", _thumbOffsetExpression);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopFillAnimation();
        _thumbVisual?.StopAnimation("Offset.X");
        _thumbOffsetExpression?.Dispose();
        _thumbOffsetExpression = null;
        _templateApplied = false;
    }

    private void Resync()
    {
        if (!_templateApplied || _fillVisual == null || _compositor == null) return;
        if (ActualWidth <= 0) return;

        // Re-pin the thumb's expression to the current trackWidth — it was
        // captured as a scalar parameter at last Wire, so layout changes
        // (SizeChanged) need to re-wire to keep the thumb landing right.
        WireThumbToFill();

        if (_isDragging) return;

        var ratio = (float)(DurationMs > 0
            ? Math.Clamp(PositionMs / DurationMs, 0.0, 1.0)
            : 0.0);

        StopFillAnimation();

        // Static case: not playing / no track / buffering → set the scale
        // directly. Zero CPU cost between events.
        if (!IsPlaying || IsBuffering || DurationMs <= 0)
        {
            _fillVisual.Scale = new Vector3(ratio, 1f, 1f);
            return;
        }

        // Playing: snap to ratio first, then animate to 1.0 over remaining ms.
        // This entire animation runs on the composition thread — the UI thread
        // doesn't get involved again until the next play/pause/seek/track event.
        var remainingMs = Math.Max(0, DurationMs - PositionMs);
        if (remainingMs <= 0)
        {
            _fillVisual.Scale = new Vector3(1f, 1f, 1f);
            return;
        }

        _fillVisual.Scale = new Vector3(ratio, 1f, 1f);

        var anim = _compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, ratio);
        anim.InsertKeyFrame(1f, 1f, _compositor.CreateLinearEasingFunction());
        anim.Duration = TimeSpan.FromMilliseconds(remainingMs);
        anim.IterationBehavior = AnimationIterationBehavior.Count;
        anim.IterationCount = 1;
        anim.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

        _fillVisual.StartAnimation("Scale.X", anim);
    }

    private void StopFillAnimation()
    {
        _fillVisual?.StopAnimation("Scale.X");
    }

    // ── Pointer / drag handling ──────────────────────────────────────────

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = true;
        _trackBorder.Height = TrackHeightHover;
        _fillBorder.Height = TrackHeightHover;
        if (_thumbVisual != null) _thumbVisual.Opacity = 1f;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = false;
        // Keep the bar/thumb expanded while a drag is in flight even if the
        // pointer leaves the bar's bounds — matches Spotify/Apple Music seek
        // ergonomics. Final cleanup runs in ReleaseDragWithCommit.
        if (_isDragging) return;
        _trackBorder.Height = TrackHeight;
        _fillBorder.Height = TrackHeight;
        if (_thumbVisual != null) _thumbVisual.Opacity = 0f;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = true;
        StopFillAnimation();
        SeekStarted?.Invoke(this, EventArgs.Empty);
        UpdateDragRatio(e);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        UpdateDragRatio(e);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        UpdateDragRatio(e);
        ReleaseDragWithCommit(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        ReleaseDragWithCommit(e.Pointer);
    }

    private void UpdateDragRatio(PointerRoutedEventArgs e)
    {
        if (_fillVisual == null) return;
        var width = ActualWidth;
        if (width <= 0) return;

        var x = e.GetCurrentPoint(this).Position.X;
        _lastDragRatio = Math.Clamp(x / width, 0.0, 1.0);
        // Drive the fill via Scale.X — same channel the animation uses, so the
        // visual stays consistent when we hand control back to the animation.
        _fillVisual.Scale = new Vector3((float)_lastDragRatio, 1f, 1f);
    }

    private void ReleaseDragWithCommit(Pointer pointer)
    {
        _isDragging = false;
        ReleasePointerCapture(pointer);

        var newPositionMs = _lastDragRatio * DurationMs;
        SeekCommitted?.Invoke(this, newPositionMs);

        // Restore the resting state IF the pointer isn't still over the bar.
        // If it is, hover state stays — user can drag again immediately.
        if (!_isHovering)
        {
            _trackBorder.Height = TrackHeight;
            _fillBorder.Height = TrackHeight;
            if (_thumbVisual != null) _thumbVisual.Opacity = 0f;
        }
        // Resync re-fires when the VM re-anchors after Seek round-trips.
    }
}
