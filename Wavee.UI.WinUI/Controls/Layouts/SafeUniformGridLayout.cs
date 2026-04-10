using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Layouts;

/// <summary>
/// Defensive uniform grid for Home sections that can be measured before they have a real width.
/// </summary>
public sealed class SafeUniformGridLayout : NonVirtualizingLayout
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(SafeUniformGridLayout),
            new PropertyMetadata(230.0, OnPropertyChanged));

    public static readonly DependencyProperty MinItemHeightProperty =
        DependencyProperty.Register(nameof(MinItemHeight), typeof(double), typeof(SafeUniformGridLayout),
            new PropertyMetadata(440.0, OnPropertyChanged));

    public static readonly DependencyProperty MinColumnSpacingProperty =
        DependencyProperty.Register(nameof(MinColumnSpacing), typeof(double), typeof(SafeUniformGridLayout),
            new PropertyMetadata(0.0, OnPropertyChanged));

    public static readonly DependencyProperty MinRowSpacingProperty =
        DependencyProperty.Register(nameof(MinRowSpacing), typeof(double), typeof(SafeUniformGridLayout),
            new PropertyMetadata(0.0, OnPropertyChanged));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double MinItemHeight
    {
        get => (double)GetValue(MinItemHeightProperty);
        set => SetValue(MinItemHeightProperty, value);
    }

    public double MinColumnSpacing
    {
        get => (double)GetValue(MinColumnSpacingProperty);
        set => SetValue(MinColumnSpacingProperty, value);
    }

    public double MinRowSpacing
    {
        get => (double)GetValue(MinRowSpacingProperty);
        set => SetValue(MinRowSpacingProperty, value);
    }

    private int _columnCount = 1;
    private double _itemWidth = 230;
    private double _itemHeight = 440;
    private Size _lastSize;

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SafeUniformGridLayout layout)
            layout.InvalidateMeasure();
    }

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        try
        {
            var children = context.Children;
            var count = children.Count;
            if (count == 0)
                return new Size(0, 0);

            var width = GetSafeAvailableWidth(availableSize.Width);
            var spacing = GetSafeNonNegative(MinColumnSpacing);
            var minWidth = GetSafePositive(MinItemWidth, 230);
            var denominator = Math.Max(1, minWidth + spacing);

            _columnCount = Math.Max(1, (int)Math.Floor((width + spacing) / denominator));
            _columnCount = Math.Min(_columnCount, count);
            _itemWidth = Math.Max(1, (width - ((_columnCount - 1) * spacing)) / _columnCount);

            var fallbackHeight = GetSafePositive(MinItemHeight, _itemWidth);
            var measureSize = new Size(_itemWidth, double.PositiveInfinity);
            var measuredHeight = 0d;

            for (var i = 0; i < count; i++)
            {
                children[i].Measure(measureSize);
                var desiredHeight = children[i].DesiredSize.Height;
                if (!double.IsNaN(desiredHeight) && !double.IsInfinity(desiredHeight) && desiredHeight > 0)
                    measuredHeight = Math.Max(measuredHeight, desiredHeight);
            }

            _itemHeight = Math.Max(fallbackHeight, measuredHeight);
            var rows = Math.Max(1, (int)Math.Ceiling(count / (double)_columnCount));
            var rowSpacing = GetSafeNonNegative(MinRowSpacing);
            var totalHeight = rows * _itemHeight + Math.Max(0, rows - 1) * rowSpacing;

            _lastSize = new Size(width, totalHeight);
            return _lastSize;
        }
        catch (ArgumentException)
        {
            return _lastSize;
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

            var columns = Math.Max(1, Math.Min(_columnCount, count));
            var columnSpacing = GetSafeNonNegative(MinColumnSpacing);
            var rowSpacing = GetSafeNonNegative(MinRowSpacing);
            var itemWidth = Math.Max(1, _itemWidth);
            var itemHeight = Math.Max(1, _itemHeight);

            for (var i = 0; i < count; i++)
            {
                var column = i % columns;
                var row = i / columns;
                var x = column * (itemWidth + columnSpacing);
                var y = row * (itemHeight + rowSpacing);
                children[i].Arrange(new Rect(x, y, itemWidth, itemHeight));
            }
        }
        catch (ArgumentException)
        {
            return finalSize;
        }

        return finalSize;
    }

    private double GetSafeAvailableWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return GetSafePositive(MinItemWidth, 230);

        return width;
    }

    private static double GetSafePositive(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value <= 0
            ? Math.Max(1, fallback)
            : value;
    }

    private static double GetSafeNonNegative(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0
            ? 0
            : value;
    }
}
