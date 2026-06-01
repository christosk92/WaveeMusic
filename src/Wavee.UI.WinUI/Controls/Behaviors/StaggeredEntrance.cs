using System;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace Wavee.UI.WinUI.Controls.Behaviors;

/// <summary>
/// Attached property that gives collection items the web-prototype's staggered
/// entrance: each item fades + slides up, cascaded <b>top-to-bottom, left-to-right</b>.
///
/// The delay is derived from each element's actual position (relative Y, then X)
/// rather than realization order — virtualizing panels don't realize strictly in
/// visual order, so an order-based delay cascades in a random direction. Position
/// based makes it deterministic regardless of realization order or how many grids
/// realize at once.
///
/// Gated to the first-viewport burst so scrolling stays smooth: realizations more
/// than <see cref="BurstGapMs"/> apart start a new "burst" (delays measured relative
/// to the top of that burst), and only items realized within <see cref="ArmWindowMs"/>
/// of the burst start animate. Honors the OS "show animations" setting.
///
/// Applied to the root element of an item DataTemplate (works for both ItemsView
/// and ItemsRepeater): <c>behaviors:StaggeredEntrance.IsEntranceAnimated="True"</c>.
/// </summary>
public static class StaggeredEntrance
{
    private const long BurstGapMs = 90;      // realizations farther apart than this start a new burst
    private const double ArmWindowMs = 1100; // only the first-viewport burst animates
    private const double YDelayPerPx = 0.32;  // vertical cascade rate (top rows first)
    private const double XDelayPerPx = 0.05;  // gentle left-to-right within a row
    private const double MaxDelayMs = 600;

    private static bool? _animationsEnabled;
    private static bool AnimationsEnabled => _animationsEnabled ??= ReadAnimationsEnabled();

    private static bool ReadAnimationsEnabled()
    {
        try { return new UISettings().AnimationsEnabled; }
        catch { return true; }
    }

    // Burst state: a burst is the set of items realized close together in time
    // (the first on-screen viewport). Delays are measured relative to the top of
    // the burst so the cascade reads top-to-bottom even mid-scroll.
    private static long _lastRealizeTicks;
    private static long _burstStartTicks;
    private static double _burstMinY;

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
        if (sender is not FrameworkElement element) return;
        if (!AnimationsEnabled) return;

        var now = DateTimeOffset.UtcNow.UtcTicks;
        var gapMs = (now - _lastRealizeTicks) / TimeSpan.TicksPerMillisecond;
        _lastRealizeTicks = now;

        // Position relative to the items panel — top-left is (0,0).
        var pos = element.ActualOffset;

        if (gapMs > BurstGapMs)
        {
            // New burst (fresh load / scroll-in): reset the origin to this element.
            _burstStartTicks = now;
            _burstMinY = pos.Y;
        }
        else if (pos.Y < _burstMinY)
        {
            _burstMinY = pos.Y;
        }

        // Past the first-viewport window → don't animate (keeps scrolling smooth).
        if ((now - _burstStartTicks) / TimeSpan.TicksPerMillisecond > ArmWindowMs)
            return;

        var relativeY = Math.Max(0, pos.Y - _burstMinY);
        var delayMs = Math.Min(relativeY * YDelayPerPx + Math.Max(0, pos.X) * XDelayPerPx, MaxDelayMs);

        EntranceAnimations.FadeSlideUp(element, delayMs);
    }
}
