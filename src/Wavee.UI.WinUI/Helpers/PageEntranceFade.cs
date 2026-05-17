using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Lightweight per-page entrance animation, applied via an attached property
/// on the page root. Pairs with the global suppression of Frame's built-in
/// <c>DrillInNavigationTransitionInfo</c> in <see cref="Controls.TabBar.TabBarItem"/>:
/// the Frame swaps pages instantly (no slide/fade choreography), and the
/// destination page's content fades in via a single composition opacity
/// animation on Loaded.
///
/// <para>
/// 220 ms duration — long enough to register as an entrance cue (above the
/// ~215 ms human visual-reaction threshold the connected-anim work settled
/// on for explicit motion) while still feeling snappy. Bumped from 150 ms
/// after the navigation perf pass: the heavy pages (Album / Artist / Playlist
/// / Show / Episode) now opt into this fade too, and at 150 ms the fade
/// finished before the UI thread had cleared <c>Bindings.Update()</c> on a
/// big nav, so the user saw the same "page just appears" pop the fade was
/// meant to hide.
/// </para>
///
/// <para>
/// Easing is the Fluent decelerate curve (cubic-bezier(0, 0, 0, 1)) — same
/// curve as <c>ConnectedAnimationHelper.ConnectedAnimationDuration</c> /
/// easing, so every motion in the app shares the same character.
/// </para>
///
/// <para>
/// Composes safely with the <c>ShimmerLoadGate</c> crossfade on heavy pages:
/// the page-level fade animates the whole page Visual 0→1 while a nested
/// shimmer→content crossfade runs inside. The opacities multiply during
/// the overlap, so the user perceives "page eases in, content inside
/// crystallises" rather than two competing animations.
/// </para>
///
/// Usage:
/// <code>
/// &lt;Page xmlns:helpers="using:Wavee.UI.WinUI.Helpers"
///       helpers:PageEntranceFade.Fade="True"
///       ...&gt;
/// </code>
/// </summary>
public static class PageEntranceFade
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(220);
    private static readonly Vector2 EaseCp1 = new(0.0f, 0.0f);
    private static readonly Vector2 EaseCp2 = new(0.0f, 1.0f);

    public static readonly DependencyProperty FadeProperty =
        DependencyProperty.RegisterAttached(
            "Fade",
            typeof(bool),
            typeof(PageEntranceFade),
            new PropertyMetadata(false, OnFadeChanged));

    public static bool GetFade(DependencyObject obj)
        => (bool)obj.GetValue(FadeProperty);

    public static void SetFade(DependencyObject obj, bool value)
        => obj.SetValue(FadeProperty, value);

    private static void OnFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        // Detach unconditionally first — re-setting the attached property
        // (e.g. via XAML rebinding) shouldn't end up with multiple handlers.
        fe.Loaded -= OnLoaded;
        fe.Unloaded -= OnUnloaded;

        if (e.NewValue is true)
        {
            fe.Loaded += OnLoaded;
            fe.Unloaded += OnUnloaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        var visual = ElementCompositionPreview.GetElementVisual(fe);
        var compositor = visual.Compositor;

        // Snap to 0 in the same dispatcher tick as Loaded so there's no flash
        // of the fully-opaque page between Loaded and the first composition
        // frame. The composition animation below picks up from this value.
        visual.Opacity = 0f;

        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(EaseCp1, EaseCp2));
        anim.Duration = FadeDuration;
        visual.StartAnimation("Opacity", anim);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        var visual = ElementCompositionPreview.GetElementVisual(fe);
        visual.StopAnimation("Opacity");
        // Leave opacity at 1 so if the helper is detached mid-flight the page
        // stays paintable. OnLoaded snaps to 0 explicitly on the next attach.
        visual.Opacity = 1f;
    }
}
