using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// AI brand sparkle icon. State-driven Composition animations replace a Lottie
/// source — we get the same on-screen behavior (pulse / rotate / wiggle / one-shot
/// completion) without dragging LottieGen into the build chain.
///
/// State transitions (set via <see cref="State"/>):
///   <c>Normal</c>      — static, no animation.
///   <c>PointerOver</c> — gentle 1.0 → 1.15 → 1.0 scale pulse, ~600 ms.
///   <c>Pressed</c>     — quick 1.0 → 0.85 → 1.05 → 1.0 wiggle, ~250 ms.
///   <c>Generating</c>  — slow continuous rotate, ~2 s/loop.
///   <c>Done</c>        — one-shot 1.0 → 1.3 → 1.0 pulse, ~400 ms; auto-resets to Normal.
///
/// Honors the system "Reduce animations" preference via
/// <see cref="UISettings.AnimationsEnabled"/> — when off, all transitions
/// collapse to static state and the glyph just sits there.
/// </summary>
public sealed partial class AiSparkleIcon : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(string),
        typeof(AiSparkleIcon),
        new PropertyMetadata("Normal", OnStateChanged));

    /// <summary>
    /// Animation state. Setting this kicks off the matching Composition animation.
    /// Valid values: "Normal", "PointerOver", "Pressed", "Generating", "Done".
    /// Any other value is treated as "Normal".
    /// </summary>
    public string State
    {
        get => (string)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private Visual? _visual;
    private CompositionScopedBatch? _activeBatch;
    private bool _animationsEnabled = true;

    public AiSparkleIcon()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _visual = ElementCompositionPreview.GetElementVisual(VisualHost);
        // Anchor scale + rotate on the visual center so animations don't drift.
        _visual.CenterPoint = new Vector3(VisualHost.ActualSize.X / 2f, VisualHost.ActualSize.Y / 2f, 0f);

        // Re-check whenever the visual lays out — the host Grid's ActualSize
        // can come in zero on the first Loaded firing.
        VisualHost.SizeChanged += (_, args) =>
        {
            if (_visual != null)
                _visual.CenterPoint = new Vector3((float)args.NewSize.Width / 2f, (float)args.NewSize.Height / 2f, 0f);
        };

        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            _animationsEnabled = ui.AnimationsEnabled;
        }
        catch
        {
            // UISettings.AnimationsEnabled can throw on some environments;
            // safest fallback is "animations on" since that's the platform default.
            _animationsEnabled = true;
        }

        // Apply whatever State was set before Loaded fired.
        ApplyState(State);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopActiveBatch();
        _visual = null;
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AiSparkleIcon icon && icon._visual != null)
            icon.ApplyState((string)(e.NewValue ?? "Normal"));
    }

    private void ApplyState(string state)
    {
        if (_visual == null)
            return;

        // Tear down anything in flight; states are mutually exclusive.
        StopActiveBatch();

        if (!_animationsEnabled)
        {
            // System asked for reduced motion. Reset to identity and bail.
            _visual.Scale = Vector3.One;
            _visual.RotationAngleInDegrees = 0f;
            return;
        }

        switch (state)
        {
            case "PointerOver":
                StartPulse(toScale: 1.15f, durationMs: 600, repeatForever: false);
                break;
            case "Pressed":
                StartWiggle();
                break;
            case "Generating":
                StartRotate();
                break;
            case "Done":
                StartPulse(toScale: 1.30f, durationMs: 400, repeatForever: false, resetOnComplete: true);
                break;
            case "Normal":
            default:
                _visual.Scale = Vector3.One;
                _visual.RotationAngleInDegrees = 0f;
                break;
        }
    }

    private void StartPulse(float toScale, int durationMs, bool repeatForever, bool resetOnComplete = false)
    {
        if (_visual == null) return;
        var compositor = _visual.Compositor;

        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, Vector3.One);
        anim.InsertKeyFrame(0.5f, new Vector3(toScale, toScale, 1f));
        anim.InsertKeyFrame(1f, Vector3.One);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        if (repeatForever)
        {
            anim.IterationBehavior = AnimationIterationBehavior.Forever;
        }

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _visual.StartAnimation(nameof(_visual.Scale), anim);
        batch.End();

        if (resetOnComplete)
        {
            batch.Completed += (_, _) =>
            {
                if (_visual != null)
                    DispatcherQueue.TryEnqueue(() => State = "Normal");
            };
        }
        _activeBatch = batch;
    }

    private void StartWiggle()
    {
        if (_visual == null) return;
        var compositor = _visual.Compositor;

        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, Vector3.One);
        anim.InsertKeyFrame(0.4f, new Vector3(0.85f, 0.85f, 1f));
        anim.InsertKeyFrame(0.7f, new Vector3(1.05f, 1.05f, 1f));
        anim.InsertKeyFrame(1f, Vector3.One);
        anim.Duration = TimeSpan.FromMilliseconds(250);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _visual.StartAnimation(nameof(_visual.Scale), anim);
        batch.End();
        _activeBatch = batch;
    }

    private void StartRotate()
    {
        if (_visual == null) return;
        var compositor = _visual.Compositor;

        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, 0f);
        anim.InsertKeyFrame(1f, 360f);
        anim.Duration = TimeSpan.FromSeconds(2);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;

        // Continuous rotate; no batch reset needed.
        _visual.StartAnimation(nameof(_visual.RotationAngleInDegrees), anim);
    }

    private void StopActiveBatch()
    {
        if (_visual != null)
        {
            _visual.StopAnimation(nameof(_visual.Scale));
            _visual.StopAnimation(nameof(_visual.RotationAngleInDegrees));
        }
        // The batch was End()ed when created — do NOT call End() again, that throws.
        // Just drop the reference; once all animations are stopped the batch
        // collapses on its own.
        _activeBatch = null;
    }
}
