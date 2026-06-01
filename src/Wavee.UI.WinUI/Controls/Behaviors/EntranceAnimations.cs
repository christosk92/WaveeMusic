using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.Controls.Behaviors;

/// <summary>
/// The web-prototype's entrance gesture — a fade + short slide-up — as a single
/// composition primitive. Both <see cref="StaggeredEntrance"/> (virtualized track
/// / card lists) and <see cref="SectionStaggerEntrance"/> (below-the-fold detail
/// page sections) call into this so the two surfaces animate with identical
/// easing, duration and travel.
///
/// Animates the <c>Translation</c> facade, NOT <c>Offset</c> — ItemsView /
/// ItemsRepeater position items via Offset, so writing Offset stacks every item
/// at the origin. Translation composes on top of the layout offset.
/// </summary>
internal static class EntranceAnimations
{
    public const double DefaultOffsetY = 14;
    public const double DefaultDurationMs = 360;

    /// <summary>
    /// Put the element into its pre-entrance state (invisible, nudged down by
    /// <paramref name="offsetY"/>) without starting any animation. Call this when
    /// the reveal is deferred (e.g. until the element scrolls into view) so there
    /// is no first-frame flash of the un-animated content.
    /// </summary>
    public static void PrepareHidden(UIElement element, double offsetY = DefaultOffsetY)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.Opacity = 0f;
        visual.Properties.InsertVector3("Translation", new Vector3(0f, (float)offsetY, 0f));
    }

    /// <summary>
    /// Fade the element in (0 → 1) while sliding it up from <paramref name="offsetY"/>
    /// to its resting position, after an optional <paramref name="delayMs"/> stagger
    /// delay. Sets the initial hidden state synchronously before starting, so it is
    /// safe to call directly (no <see cref="PrepareHidden"/> required) without a flash.
    /// </summary>
    public static void FadeSlideUp(
        UIElement element,
        double delayMs,
        double offsetY = DefaultOffsetY,
        double durationMs = DefaultDurationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        visual.Opacity = 0f;
        visual.Properties.InsertVector3("Translation", new Vector3(0f, (float)offsetY, 0f));

        var delay = TimeSpan.FromMilliseconds(delayMs);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        if (delay > TimeSpan.Zero)
        {
            fade.DelayTime = delay;
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(durationMs);
        if (delay > TimeSpan.Zero)
        {
            slide.DelayTime = delay;
            slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }

    /// <summary>
    /// Snap the element to its resting, fully-visible state with no animation
    /// (reduced-motion path, or to undo a <see cref="PrepareHidden"/>).
    /// </summary>
    public static void ShowImmediate(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Opacity = 1f;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }
}
