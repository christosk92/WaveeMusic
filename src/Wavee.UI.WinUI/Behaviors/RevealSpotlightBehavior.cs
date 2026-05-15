using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Behaviors;

/// <summary>
/// Attached behavior that paints a cursor-following radial-gradient spotlight
/// on a card while the pointer hovers over it. Mirrors the V4A prototype's
/// section-7 script (delegated pointermove → --rx/--ry CSS vars). The target
/// must be a <see cref="Border"/> (or any object exposing a <see cref="Border.Background"/>
/// property compatible with <see cref="RadialGradientBrush"/>); the behavior
/// swaps in a tracking <see cref="RadialGradientBrush"/> on pointer enter and
/// restores the original brush on exit.
///
/// Usage:
/// <code>behaviors:RevealSpotlightBehavior.Enable="True"</code>
/// </summary>
public static class RevealSpotlightBehavior
{
    private static readonly ConditionalWeakTable<Border, HandlerHolder> _holders = new();

    private sealed class HandlerHolder
    {
        public PointerEventHandler? Entered;
        public PointerEventHandler? Moved;
        public PointerEventHandler? Exited;
        public Brush? OriginalBackground;
        public RadialGradientBrush? TrackingBrush;
    }

    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached(
            "Enable",
            typeof(bool),
            typeof(RevealSpotlightBehavior),
            new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject obj) => (bool)obj.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject obj, bool value) => obj.SetValue(EnableProperty, value);

    /// <summary>Color the spotlight blends toward at the centre. Defaults to the system accent.</summary>
    public static readonly DependencyProperty SpotlightColorProperty =
        DependencyProperty.RegisterAttached(
            "SpotlightColor",
            typeof(Color),
            typeof(RevealSpotlightBehavior),
            new PropertyMetadata(Color.FromArgb(64, 80, 144, 184)));

    public static Color GetSpotlightColor(DependencyObject obj) => (Color)obj.GetValue(SpotlightColorProperty);
    public static void SetSpotlightColor(DependencyObject obj, Color value) => obj.SetValue(SpotlightColorProperty, value);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;

        Detach(border);

        if (e.NewValue is true)
            Attach(border);
    }

    private static void Attach(Border border)
    {
        var holder = new HandlerHolder
        {
            OriginalBackground = border.Background,
        };

        holder.Entered = (_, _) =>
        {
            var color = GetSpotlightColor(border);
            holder.TrackingBrush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.6,
                RadiusY = 0.6,
            };
            holder.TrackingBrush.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
            holder.TrackingBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 });
            border.Background = holder.TrackingBrush;
        };

        holder.Moved = (_, args) =>
        {
            if (holder.TrackingBrush is null) return;
            var p = args.GetCurrentPoint(border).Position;
            var w = border.ActualWidth;
            var h = border.ActualHeight;
            if (w <= 0 || h <= 0) return;
            holder.TrackingBrush.Center = new Point(p.X / w, p.Y / h);
            holder.TrackingBrush.GradientOrigin = holder.TrackingBrush.Center;
        };

        holder.Exited = (_, _) =>
        {
            border.Background = holder.OriginalBackground;
            holder.TrackingBrush = null;
        };

        border.PointerEntered += holder.Entered;
        border.PointerMoved += holder.Moved;
        border.PointerExited += holder.Exited;
        border.PointerCanceled += holder.Exited;
        border.PointerCaptureLost += holder.Exited;

        _holders.AddOrUpdate(border, holder);
    }

    private static void Detach(Border border)
    {
        if (!_holders.TryGetValue(border, out var holder)) return;
        if (holder.Entered != null) border.PointerEntered -= holder.Entered;
        if (holder.Moved != null) border.PointerMoved -= holder.Moved;
        if (holder.Exited != null)
        {
            border.PointerExited -= holder.Exited;
            border.PointerCanceled -= holder.Exited;
            border.PointerCaptureLost -= holder.Exited;
        }
        if (holder.OriginalBackground is { } original)
            border.Background = original;
        _holders.Remove(border);
    }
}
