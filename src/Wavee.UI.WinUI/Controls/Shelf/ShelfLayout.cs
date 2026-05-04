using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Shelf;

/// <summary>
/// A horizontally-scrolling virtualizing layout with fixed item width. Unlike
/// <c>SingleRowLayout</c>, it reports the full horizontal extent (all items)
/// so a <see cref="ScrollView"/> can scroll through off-screen items via the
/// chevron paging API. Realizes only items inside
/// <see cref="VirtualizingLayoutContext.RealizationRect"/>, so the shelf doesn't
/// instantiate hundreds of cards up front.
/// </summary>
/// <remarks>
/// Width is assigned at measure-time (the measure rect passed to each child IS
/// <see cref="ItemWidth"/>). This avoids the anti-pattern of poking
/// <c>FrameworkElement.Width</c> in <c>ElementPrepared</c>, which triggered
/// layout oscillations during outer-page vertical scrolls.
/// </remarks>
public sealed class ShelfLayout : VirtualizingLayout
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(ShelfLayout),
            new PropertyMetadata(160.0, OnPropertyChanged));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(ShelfLayout),
            new PropertyMetadata(12.0, OnPropertyChanged));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShelfLayout layout)
            layout.InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        try
        {
            var count = context.ItemCount;
            if (count == 0 || ItemWidth <= 0)
                return new Size(0, 0);

            var step = ItemWidth + Spacing;
            var totalWidth = count * ItemWidth + Math.Max(0, count - 1) * Spacing;

            var (first, last) = GetVisibleRange(context, count, step);

            var measureHeight = double.IsInfinity(availableSize.Height) ? 10000 : availableSize.Height;
            var childMeasureSize = new Size(ItemWidth, measureHeight);

            double maxHeight = 0;
            for (int i = first; i <= last; i++)
            {
                var child = context.GetOrCreateElementAt(i);
                child.Measure(childMeasureSize);
                if (child.DesiredSize.Height > maxHeight)
                    maxHeight = child.DesiredSize.Height;
            }

            return new Size(totalWidth, maxHeight);
        }
        catch (ArgumentException)
        {
            // WinUI can throw during collection mutations — bail out and let the
            // next layout pass re-run.
            return new Size(0, 0);
        }
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        try
        {
            var count = context.ItemCount;
            if (count == 0) return finalSize;

            var step = ItemWidth + Spacing;
            var (first, last) = GetVisibleRange(context, count, step);

            for (int i = first; i <= last; i++)
            {
                var child = context.GetOrCreateElementAt(i);
                child.Arrange(new Rect(i * step, 0, ItemWidth, finalSize.Height));
            }
        }
        catch (ArgumentException)
        {
            // Same defensive behaviour as MeasureOverride.
        }

        return finalSize;
    }

    /// <summary>
    /// Clamps the realization rect to a first/last item index, with one extra
    /// card of buffer on each side so paging reveals look instantaneous.
    /// </summary>
    private static (int first, int last) GetVisibleRange(VirtualizingLayoutContext context, int count, double step)
    {
        var rect = context.RealizationRect;

        // When the repeater hasn't been realized yet (RealizationRect can be
        // (0,0,0,0)), fall back to realizing the first screenful. Using the
        // ItemWidth step to keep this bounded.
        if (rect.Width <= 0)
        {
            return (0, Math.Min(count - 1, 4));
        }

        var first = Math.Max(0, (int)Math.Floor(rect.Left / step) - 1);
        var last = Math.Min(count - 1, (int)Math.Ceiling(rect.Right / step));
        if (last < first) last = first;
        return (first, last);
    }
}
