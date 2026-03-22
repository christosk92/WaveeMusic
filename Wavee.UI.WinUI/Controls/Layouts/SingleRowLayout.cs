using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Layouts;

/// <summary>
/// A single-row adaptive layout that fills available width with evenly sized items.
/// Items that don't fit are hidden (not wrapped). The number of visible items
/// adapts responsively based on MinItemWidth and available space -- like Spotify's card grid.
/// </summary>
public sealed class SingleRowLayout : NonVirtualizingLayout
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

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        try
        {
            var children = context.Children;
            var count = children.Count;
            if (count == 0)
                return new Size(0, 0);

            var availableWidth = availableSize.Width;
            if (double.IsInfinity(availableWidth) || availableWidth <= 0)
                availableWidth = 1000;

            _columnCount = Math.Max(1, (int)Math.Floor((availableWidth + Spacing) / (MinItemWidth + Spacing)));
            _columnCount = Math.Min(_columnCount, count);
            _itemWidth = (availableWidth - (_columnCount - 1) * Spacing) / _columnCount;

            // Clamp to max width so single items don't stretch absurdly
            if (!double.IsInfinity(MaxItemWidth) && _itemWidth > MaxItemWidth)
                _itemWidth = MaxItemWidth;

            // Use a large finite height to avoid infinite-measure oscillation inside ScrollView
            var measureHeight = double.IsInfinity(availableSize.Height) ? 10000 : availableSize.Height;
            var measureSize = new Size(_itemWidth, measureHeight);

            double maxHeight = 0;
            for (int i = 0; i < count; i++)
            {
                children[i].Measure(measureSize);
                if (i < _columnCount)
                    maxHeight = Math.Max(maxHeight, children[i].DesiredSize.Height);
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

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        try
        {
            var children = context.Children;
            var count = children.Count;
            if (count == 0)
                return finalSize;

            double maxHeight = 0;
            for (int i = 0; i < _columnCount && i < count; i++)
                maxHeight = Math.Max(maxHeight, children[i].DesiredSize.Height);

            if (maxHeight <= 0) maxHeight = finalSize.Height;

            for (int i = 0; i < count; i++)
            {
                if (i < _columnCount)
                {
                    var x = i * (_itemWidth + Spacing);
                    children[i].Arrange(new Rect(x, 0, _itemWidth, maxHeight));
                }
                else
                {
                    // Arrange past the right edge with valid dimensions
                    children[i].Arrange(new Rect(finalSize.Width + 100, 0, _itemWidth, maxHeight));
                }
            }
        }
        catch (ArgumentException)
        {
            // Swallow — layout will re-run on next pass
        }

        return finalSize;
    }
}
