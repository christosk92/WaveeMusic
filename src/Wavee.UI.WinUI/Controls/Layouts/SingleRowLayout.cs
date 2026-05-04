using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Layouts;

/// <summary>
/// A single-row adaptive layout that fills available width with evenly sized items.
/// Items that don't fit are <b>not realized at all</b> (hidden + never instantiated).
/// The number of visible items adapts responsively based on <see cref="MinItemWidth"/>
/// and available space — like Spotify's card grid.
///
/// <para>
/// This layout inherits from <see cref="VirtualizingLayout"/>. The previous
/// <c>NonVirtualizingLayout</c> base forced <see cref="ItemsRepeater"/> to instantiate,
/// measure, and arrange every child in the items source — even items that were immediately
/// hidden off-screen. On HomePage that meant 31 sections × ~10 items = ~310 cards
/// fully realized at page-load time. This rewrite requests only the <c>N</c> items
/// that actually fit in the row; the rest are never created. Framework automatically
/// recycles any element we stop requesting between layout passes.
/// </para>
/// </summary>
public sealed class SingleRowLayout : VirtualizingLayout
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(SingleRowLayout),
            new PropertyMetadata(180.0, OnPropertyChanged));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(SingleRowLayout),
            new PropertyMetadata(12.0, OnPropertyChanged));

    public static readonly DependencyProperty MaxItemWidthProperty =
        DependencyProperty.Register(nameof(MaxItemWidth), typeof(double), typeof(SingleRowLayout),
            new PropertyMetadata(double.PositiveInfinity, OnPropertyChanged));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>
    /// Maximum width per item. Prevents a single item from stretching to fill the entire row.
    /// Default: infinity (no limit).
    /// </summary>
    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SingleRowLayout layout)
            layout.InvalidateMeasure();
    }

    private int _columnCount;
    private double _itemWidth;
    private Size _lastMeasuredSize;

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        try
        {
            var itemCount = context.ItemCount;
            if (itemCount == 0)
                return new Size(0, 0);

            var availableWidth = availableSize.Width;
            if (double.IsInfinity(availableWidth) || availableWidth <= 0)
                availableWidth = 1000;

            _columnCount = Math.Max(1, (int)Math.Floor((availableWidth + Spacing) / (MinItemWidth + Spacing)));
            _columnCount = Math.Min(_columnCount, itemCount);
            _itemWidth = (availableWidth - (_columnCount - 1) * Spacing) / _columnCount;

            // Clamp to max width so single items don't stretch absurdly
            if (!double.IsInfinity(MaxItemWidth) && _itemWidth > MaxItemWidth)
                _itemWidth = MaxItemWidth;

            // Use a large finite height to avoid infinite-measure oscillation inside ScrollView
            var measureHeight = double.IsInfinity(availableSize.Height) ? 10000 : availableSize.Height;
            var measureSize = new Size(_itemWidth, measureHeight);

            double maxHeight = 0;
            // Realize ONLY the visible items. Items beyond _columnCount are never requested,
            // so ItemsRepeater never instantiates their DataTemplates — the core virtualization win.
            // Any previously-realized elements that we stop requesting are auto-recycled by
            // the framework between layout passes.
            for (int i = 0; i < _columnCount; i++)
            {
                var child = context.GetOrCreateElementAt(i);
                child.Measure(measureSize);
                maxHeight = Math.Max(maxHeight, child.DesiredSize.Height);
            }

            // Return the exact available width (not computed width) to avoid layout cycle
            _lastMeasuredSize = new Size(availableWidth, maxHeight);
            return _lastMeasuredSize;
        }
        catch (ArgumentException)
        {
            // WinUI can throw during collection mutations — return last known size
            return _lastMeasuredSize;
        }
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        try
        {
            var itemCount = context.ItemCount;
            if (itemCount == 0)
                return finalSize;

            var visibleCount = Math.Min(_columnCount, itemCount);

            for (int i = 0; i < visibleCount; i++)
            {
                var child = context.GetOrCreateElementAt(i);
                var x = i * (_itemWidth + Spacing);
                child.Arrange(new Rect(x, 0, _itemWidth, finalSize.Height));
            }
            // No arrange for items beyond visibleCount — they don't exist in the visual tree.
        }
        catch (ArgumentException)
        {
            // Swallow — layout will re-run on next pass
        }

        return finalSize;
    }
}
