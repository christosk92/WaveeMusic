using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Reusable Composition API animation helpers for drag overlay effects.
/// </summary>
public static class DragOverlayHelper
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Fade an element in from 0 to full opacity with an ease-out curve.
    /// </summary>
    public static void FadeIn(UIElement target, TimeSpan? duration = null)
    {
        target.Visibility = Visibility.Visible;
        var visual = ElementCompositionPreview.GetElementVisual(target);
        var compositor = visual.Compositor;

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0f);
        animation.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        animation.Duration = duration ?? DefaultDuration;

        visual.StartAnimation("Opacity", animation);
    }

    /// <summary>
    /// Fade an element out from full opacity to 0, then collapse it.
    /// </summary>
    public static void FadeOut(UIElement target, TimeSpan? duration = null, Action? onComplete = null)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        var compositor = visual.Compositor;

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 1f);
        animation.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        animation.Duration = duration ?? DefaultDuration;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", animation);
        batch.End();
        batch.Completed += (_, _) =>
        {
            target.Visibility = Visibility.Collapsed;
            onComplete?.Invoke();
        };
    }
}
