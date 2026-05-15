using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace Wavee.UI.WinUI.Behaviors;

/// <summary>
/// Attached behavior that drives a subtle Composition-based scale animation on
/// PointerEntered / PointerExited — 1.0 ↔ 1.02 over 150 ms, easeOut. Mirrors
/// the V4A prototype's hover affordance for clickable cards (popular-release
/// rows, ticket stubs, merch tiles, video cards) so they feel responsive
/// without forking each card's template.
///
/// Usage in XAML:
/// <code>behaviors:CardHoverScaleBehavior.Enable="True"</code>
///
/// The behavior stores its handler tokens in a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// so element GC isn't blocked by static state.
/// </summary>
public static class CardHoverScaleBehavior
{
    private const float HoverScale = 1.02f;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);

    private static readonly ConditionalWeakTable<FrameworkElement, HandlerHolder> _holders = new();

    private sealed class HandlerHolder
    {
        public PointerEventHandler? Entered;
        public PointerEventHandler? Exited;
    }

    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached(
            "Enable",
            typeof(bool),
            typeof(CardHoverScaleBehavior),
            new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject obj) => (bool)obj.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject obj, bool value) => obj.SetValue(EnableProperty, value);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        Detach(fe);

        if (e.NewValue is true)
            Attach(fe);
    }

    private static void Attach(FrameworkElement fe)
    {
        var holder = new HandlerHolder();
        holder.Entered = (_, _) => Animate(fe, HoverScale);
        holder.Exited = (_, _) => Animate(fe, 1.0f);
        fe.PointerEntered += holder.Entered;
        fe.PointerExited += holder.Exited;
        fe.PointerCanceled += holder.Exited;
        fe.PointerCaptureLost += holder.Exited;
        _holders.AddOrUpdate(fe, holder);

        // Snap the center point to the element's middle so the scale is
        // visually centered. Re-evaluated on Loaded/SizeChanged.
        fe.Loaded += OnLoadedOrSizeChanged;
        fe.SizeChanged += (_, _) => OnLoadedOrSizeChanged(fe, null!);
    }

    private static void Detach(FrameworkElement fe)
    {
        if (!_holders.TryGetValue(fe, out var holder)) return;
        if (holder.Entered != null) fe.PointerEntered -= holder.Entered;
        if (holder.Exited != null)
        {
            fe.PointerExited -= holder.Exited;
            fe.PointerCanceled -= holder.Exited;
            fe.PointerCaptureLost -= holder.Exited;
        }
        _holders.Remove(fe);
    }

    private static void OnLoadedOrSizeChanged(object sender, RoutedEventArgs _)
    {
        if (sender is not FrameworkElement fe) return;
        var visual = ElementCompositionPreview.GetElementVisual(fe);
        visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0f);
    }

    private static void Animate(FrameworkElement fe, float target)
    {
        var visual = ElementCompositionPreview.GetElementVisual(fe);
        var compositor = visual.Compositor;
        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new Vector3(target, target, 0f));
        anim.Duration = Duration;
        visual.StartAnimation("Scale", anim);
    }
}
