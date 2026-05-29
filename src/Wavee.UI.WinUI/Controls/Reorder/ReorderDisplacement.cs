using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.Controls.Reorder;

/// <summary>
/// Composition-driven row displacement: translates sibling containers along Y to
/// open/close a gap, with react-beautiful-dnd's "out of the way" spring. GPU-only
/// (no layout). Shared by the capture engine (<see cref="ReorderController"/>) and
/// the sidebar's OLE-driven path.
///
/// <para>Idempotent: re-applying the same target Y to a container is a no-op, so
/// callers can recompute the full displacement map every pointer move cheaply.</para>
/// </summary>
public sealed class ReorderDisplacement
{
    private static readonly TimeSpan OutOfTheWay = TimeSpan.FromMilliseconds(200);
    // rbd outOfTheWay curve: cubic-bezier(0.2, 0, 0, 1)
    private static readonly Vector2 EaseA = new(0.2f, 0f);
    private static readonly Vector2 EaseB = new(0f, 1f);

    private readonly Dictionary<UIElement, float> _applied = new();
    private CubicBezierEasingFunction? _ease;

    /// <summary>Translate <paramref name="container"/> to <paramref name="offsetY"/> (spring) or instantly.</summary>
    public void ApplyOffset(UIElement container, double offsetY, bool animate = true)
    {
        var target = (float)offsetY;
        if (_applied.TryGetValue(container, out var prev) && Math.Abs(prev - target) < 0.5f)
            return;
        _applied[container] = target;

        ElementCompositionPreview.SetIsTranslationEnabled(container, true);
        var visual = ElementCompositionPreview.GetElementVisual(container);

        if (!animate)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", new Vector3(0, target, 0));
            return;
        }

        var c = visual.Compositor;
        _ease ??= c.CreateCubicBezierEasingFunction(EaseA, EaseB);
        var anim = c.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new Vector3(0, target, 0), _ease);
        anim.Duration = OutOfTheWay;
        anim.Target = "Translation";
        visual.StartAnimation("Translation", anim);
    }

    /// <summary>True if this container currently carries a non-zero displacement we set.</summary>
    public bool IsDisplaced(UIElement container) =>
        _applied.TryGetValue(container, out var v) && Math.Abs(v) >= 0.5f;

    /// <summary>
    /// Instantly zero every touched container (used at commit — paired with the
    /// data move so displaced positions == post-move layout, making this visually
    /// neutral and flicker-free).
    /// </summary>
    public void ClearAllInstant()
    {
        foreach (var container in _applied.Keys)
        {
            var visual = ElementCompositionPreview.GetElementVisual(container);
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        _applied.Clear();
    }

    /// <summary>Spring every touched container back to zero (used on cancel).</summary>
    public void ResetAnimated()
    {
        foreach (var container in new List<UIElement>(_applied.Keys))
            ApplyOffset(container, 0, animate: true);
        _applied.Clear();
    }
}
