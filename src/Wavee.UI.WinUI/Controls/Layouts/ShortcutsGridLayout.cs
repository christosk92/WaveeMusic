using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Layouts;

/// <summary>
/// Adaptive 2-row grid layout for Spotify-style shortcut pills.
/// Items flow column-first (col1 row1, col1 row2, col2 row1, col2 row2, ...).
/// Column count adapts to available width. Excess items are hidden.
/// </summary>
public sealed class ShortcutsGridLayout : NonVirtualizingLayout
{
    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(ShortcutsGridLayout),
            new PropertyMetadata(56.0, OnPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(nameof(ColumnSpacing), typeof(double), typeof(ShortcutsGridLayout),
            new PropertyMetadata(8.0, OnPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(ShortcutsGridLayout),
            new PropertyMetadata(8.0, OnPropertyChanged));

    public static readonly DependencyProperty RowCountProperty =
        DependencyProperty.Register(nameof(RowCount), typeof(int), typeof(ShortcutsGridLayout),
            new PropertyMetadata(2, OnPropertyChanged));

    /// <summary>Fixed height per item.</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    /// <summary>Horizontal spacing between columns.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>Vertical spacing between rows.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Number of rows (default 2).</summary>
    public int RowCount
    {
        get => (int)GetValue(RowCountProperty);
        set => SetValue(RowCountProperty, value);
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShortcutsGridLayout layout)
            layout.InvalidateMeasure();
    }

    private int _columnCount;
    private double _itemWidth;
    private Size _lastSize;

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

            var rows = Math.Max(1, RowCount);

            // Adaptive column count based on available width
            _columnCount = availableWidth switch
            {
                < 500 => 2,
                < 800 => 3,
                _ => 4
            };

            // Don't show more columns than we have items for
            var maxCols = (int)Math.Ceiling((double)count / rows);
            _columnCount = Math.Min(_columnCount, maxCols);

            _itemWidth = (availableWidth - (_columnCount - 1) * ColumnSpacing) / _columnCount;
            var visibleCount = Math.Min(_columnCount * rows, count);

            var measureSize = new Size(_itemWidth, ItemHeight);
            for (int i = 0; i < count; i++)
                children[i].Measure(measureSize);

            var totalHeight = rows * ItemHeight + (rows - 1) * RowSpacing;
            _lastSize = new Size(availableWidth, totalHeight);
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

            var rows = Math.Max(1, RowCount);
            var visibleCount = Math.Min(_columnCount * rows, count);

            // Arrange in column-major order:
            // Index 0 → col 0, row 0
            // Index 1 → col 0, row 1
            // Index 2 → col 1, row 0
            // Index 3 → col 1, row 1
            // etc.
            for (int i = 0; i < count; i++)
            {
                if (i < visibleCount)
                {
                    var col = i / rows;
                    var row = i % rows;
                    var x = col * (_itemWidth + ColumnSpacing);
                    var y = row * (ItemHeight + RowSpacing);
                    children[i].Arrange(new Rect(x, y, _itemWidth, ItemHeight));
                }
                else
                {
                    // Hide off-screen
                    children[i].Arrange(new Rect(finalSize.Width + 100, 0, _itemWidth, ItemHeight));
                }
            }
        }
        catch (ArgumentException)
        {
            // Swallow — layout re-runs next pass
        }

        return finalSize;
    }
}
