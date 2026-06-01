using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Wavee.UI.WinUI.Controls.Behaviors;

/// <summary>
/// Attached property that gives collection items the web-prototype's staggered
/// entrance: each item fades + slides up, cascaded across the batch.
///
/// Applied to the <b>root element of an item DataTemplate</b> (works for both
/// <c>ItemsView</c> and <c>ItemsRepeater</c>, unlike a repeater-only hook). The
/// cascade is driven by a global "burst sequencer": elements realized close
/// together in time (the first on-screen viewport) get an increasing delay and
/// cascade in; elements realized in isolation later (scroll-recycle) reset to
/// delay 0 and just do a quick, near-imperceptible fade — so scrolling stays
/// smooth and is never re-staggered. Honors the OS "show animations" setting.
///
/// Usage: <c>behaviors:StaggeredEntrance.IsEntranceAnimated="True"</c> on the
/// template's root <c>Grid</c>/<c>Border</c>.
/// </summary>
public static class StaggeredEntrance
{
    private const double StepMs = 26;        // delay added per item within a burst
    private const double DurationMs = 360;
    private const int MaxStaggered = 24;     // cap so the tail of a big viewport stays snappy
    private const float OffsetY = 14f;
    private const long BurstGapMs = 90;      // realizations farther apart than this start a new burst

    private static bool? _animationsEnabled;
    private static bool AnimationsEnabled => _animationsEnabled ??= ReadAnimationsEnabled();

    private static bool ReadAnimationsEnabled()
    {
        try { return new UISettings().AnimationsEnabled; }
        catch { return true; }
    }

    // Global burst sequencer state.
    private static long _lastRealizeTicks;
    private static int _burstOrdinal;

    public static readonly DependencyProperty IsEntranceAnimatedProperty =
        DependencyProperty.RegisterAttached(
            "IsEntranceAnimated", typeof(bool), typeof(StaggeredEntrance),
            new PropertyMetadata(false, OnIsEntranceAnimatedChanged));

    public static bool GetIsEntranceAnimated(DependencyObject o) => (bool)o.GetValue(IsEntranceAnimatedProperty);
    public static void SetIsEntranceAnimated(DependencyObject o, bool value) => o.SetValue(IsEntranceAnimatedProperty, value);

    private static void OnIsEntranceAnimatedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if (e.NewValue is true)
        {
            element.Loaded -= OnElementLoaded;
            element.Loaded += OnElementLoaded;
        }
        else
        {
            element.Loaded -= OnElementLoaded;
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not UIElement element) return;
        if (!AnimationsEnabled) return;

        // Burst sequencing: items realized within BurstGapMs of each other cascade;
        // a longer pause resets the ordinal (isolated scroll-in → delay 0).
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var gapMs = (now - _lastRealizeTicks) / TimeSpan.TicksPerMillisecond;
        _burstOrdinal = gapMs > BurstGapMs ? 0 : _burstOrdinal + 1;
        _lastRealizeTicks = now;

        var ordinal = Math.Min(_burstOrdinal, MaxStaggered);
        AnimateIn(element, ordinal);
    }

    private static void AnimateIn(UIElement element, int ordinal)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        // CRITICAL: animate the Translation facade, NOT Offset. ItemsView /
        // ItemsRepeater position their realized items by setting each element's
        // Composition Offset — writing Offset here overwrites the layout position
        // and stacks every card at the origin. Translation composes on top of the
        // layout's Offset, so the slide is purely additive.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        // Start state set immediately to avoid a flash before the delayed animation.
        visual.Opacity = 0f;
        visual.Properties.InsertVector3("Translation", new Vector3(0f, OffsetY, 0f));

        var delay = TimeSpan.FromMilliseconds(ordinal * StepMs);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(DurationMs);
        if (delay > TimeSpan.Zero)
        {
            fade.DelayTime = delay;
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(DurationMs);
        if (delay > TimeSpan.Zero)
        {
            slide.DelayTime = delay;
            slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }
}
