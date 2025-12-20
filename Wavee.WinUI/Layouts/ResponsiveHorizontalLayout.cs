using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;

namespace Wavee.WinUI.Layouts;

/// <summary>
/// A virtualizing layout that displays items horizontally with responsive sizing.
/// Items resize uniformly to fill available width between min/max constraints.
/// Similar to Spotify's responsive horizontal card layout.
/// </summary>
public partial class ResponsiveHorizontalLayout : VirtualizingLayout
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(
            nameof(MinItemWidth),
            typeof(double),
            typeof(ResponsiveHorizontalLayout),
            new PropertyMetadata(180.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MaxItemWidthProperty =
        DependencyProperty.Register(
            nameof(MaxItemWidth),
            typeof(double),
            typeof(ResponsiveHorizontalLayout),
            new PropertyMetadata(300.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemSpacingProperty =
        DependencyProperty.Register(
            nameof(ItemSpacing),
            typeof(double),
            typeof(ResponsiveHorizontalLayout),
            new PropertyMetadata(12.0, OnLayoutPropertyChanged));

    /// <summary>
    /// Minimum width for each item
    /// </summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>
    /// Maximum width for each item
    /// </summary>
    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    /// <summary>
    /// Spacing between items
    /// </summary>
    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ResponsiveHorizontalLayout layout)
        {
            layout.InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var itemCount = context.ItemCount;

        // No items or infinite width
        if (itemCount == 0 || double.IsInfinity(availableSize.Width))
        {
            return new Size(0, 0);
        }

        // Calculate how many items can fit and their width
        var (visibleCount, itemWidth) = CalculateLayout(availableSize.Width, itemCount);

        // If nothing fits, return zero size
        if (visibleCount == 0)
        {
            return new Size(0, 0);
        }

        double maxHeight = 0;

        // Measure visible items
        for (int i = 0; i < visibleCount; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(itemWidth, availableSize.Height));
            maxHeight = Math.Max(maxHeight, element.DesiredSize.Height);
        }

        // Don't recycle here - let the framework handle it automatically

        return new Size(availableSize.Width, maxHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var itemCount = context.ItemCount;

        if (itemCount == 0)
        {
            return finalSize;
        }

        // Calculate layout
        var (visibleCount, itemWidth) = CalculateLayout(finalSize.Width, itemCount);

        // First pass: calculate maxHeight from all currently visible elements
        // This ensures we work with fresh data from virtualized items
        double maxHeight = 0;
        for (int i = 0; i < visibleCount; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            maxHeight = Math.Max(maxHeight, element.DesiredSize.Height);
        }

        // Second pass: arrange all visible items with uniform maxHeight
        double currentX = 0;
        for (int i = 0; i < visibleCount; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            var rect = new Rect(currentX, 0, itemWidth, maxHeight);
            element.Arrange(rect);

            currentX += itemWidth + ItemSpacing;
        }

        return new Size(finalSize.Width, maxHeight);
    }

    /// <summary>
    /// Calculates how many items fit and their optimal width
    /// </summary>
    private (int visibleCount, double itemWidth) CalculateLayout(double availableWidth, int totalCount)
    {
        if (availableWidth <= 0 || totalCount == 0)
        {
            return (0, 0);
        }

        // Start with minimum item width and calculate max items that fit
        var maxPossibleItems = CalculateMaxItems(availableWidth, MinItemWidth);

        // Determine how many items to show (up to total available)
        var targetCount = Math.Min(maxPossibleItems, totalCount);

        if (targetCount == 0)
        {
            return (0, 0);
        }

        // Calculate ideal item width to fill space evenly
        var totalSpacing = (targetCount - 1) * ItemSpacing;
        var availableForItems = availableWidth - totalSpacing;
        var idealItemWidth = availableForItems / targetCount;

        // Clamp to min/max bounds
        var actualItemWidth = Math.Clamp(idealItemWidth, MinItemWidth, MaxItemWidth);

        // If items are at max width and there's still space, reduce count
        if (actualItemWidth >= MaxItemWidth)
        {
            // Recalculate with max width constraint
            var recalculatedCount = CalculateMaxItems(availableWidth, MaxItemWidth);
            targetCount = Math.Min(recalculatedCount, totalCount);

            if (targetCount > 0)
            {
                totalSpacing = (targetCount - 1) * ItemSpacing;
                availableForItems = availableWidth - totalSpacing;
                actualItemWidth = Math.Clamp(availableForItems / targetCount, MinItemWidth, MaxItemWidth);
            }
        }

        return (targetCount, actualItemWidth);
    }

    /// <summary>
    /// Calculates maximum number of items that fit with given item width
    /// </summary>
    private int CalculateMaxItems(double availableWidth, double itemWidth)
    {
        if (itemWidth <= 0)
        {
            return 0;
        }

        var count = 0;
        var usedWidth = 0.0;

        while (true)
        {
            var widthNeeded = (count == 0) ? itemWidth : usedWidth + ItemSpacing + itemWidth;
            if (widthNeeded > availableWidth)
            {
                break;
            }

            usedWidth = widthNeeded;
            count++;
        }

        return count; // Return actual count, allow 0 if nothing fits
    }
}
