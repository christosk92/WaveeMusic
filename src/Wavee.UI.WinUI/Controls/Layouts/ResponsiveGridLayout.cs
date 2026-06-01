using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Layouts;

/// <summary>
/// Virtualizing grid that mirrors the web prototype's
/// <c>grid-template-columns: repeat(auto-fill, minmax(MinItemWidth, 1fr))</c>: it
/// fits as many <see cref="MinItemWidth"/>-or-wider columns as the panel allows and
/// <b>stretches every column to fill the width</b> (the <c>1fr</c> part) so cards grow
/// with the panel — while the row height tracks content: a square/aspect image that
/// grows with the column width, plus a fixed <see cref="TextBandHeight"/> beneath it.
///
/// <para>Stock <c>UniformGridLayout</c> can't do this: its cells are a fixed aspect
/// ratio, so a single ratio clips when cells are narrow (image + text taller than the
/// cell) and leaves empty space when wide. Here the row height is computed
/// <i>analytically</i> — <c>cellWidth × AspectRatio + TextBandHeight</c> — which matches
/// the card's real height (the image fills the cell width, the text band is fixed below),
/// giving neither clip nor empty space at any width.</para>
///
/// <para>Fully virtualized: only items intersecting <see cref="VirtualizingLayoutContext.RealizationRect"/>
/// are realized / measured / arranged. Modeled on <c>ExpandingGridLayout</c>.</para>
/// </summary>
public sealed partial class ResponsiveGridLayout : VirtualizingLayout
{
    private double _minItemWidth = 150;
    private double _columnSpacing = 12;
    private double _rowSpacing = 12;
    private double _textBandHeight = 84;
    private double _aspectRatio = 1.0;

    /// <summary>Minimum column width before another column is added (the CSS <c>minmax</c> floor).</summary>
    public double MinItemWidth { get => _minItemWidth; set => SetAndInvalidate(ref _minItemWidth, value); }

    public double ColumnSpacing { get => _columnSpacing; set => SetAndInvalidate(ref _columnSpacing, value); }

    public double RowSpacing { get => _rowSpacing; set => SetAndInvalidate(ref _rowSpacing, value); }

    /// <summary>Fixed height of the text area below the image (title + subtitle + badge + spacing/padding).</summary>
    public double TextBandHeight { get => _textBandHeight; set => SetAndInvalidate(ref _textBandHeight, value); }

    /// <summary>Image height = <c>cellWidth × AspectRatio</c>. 1.0 = square cover / circular avatar.</summary>
    public double AspectRatio { get => _aspectRatio; set => SetAndInvalidate(ref _aspectRatio, value); }

    private void SetAndInvalidate(ref double field, double value)
    {
        if (double.IsNaN(value) || Math.Abs(field - value) <= 0.01)
            return;
        field = value;
        InvalidateMeasure();
    }

    private int ComputeColumns(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return 1;
        return Math.Max(1, (int)Math.Floor((width + _columnSpacing) / (_minItemWidth + _columnSpacing)));
    }

    private (int Columns, double CellWidth, double RowHeight) ResolveGrid(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width < 0)
            width = 0;

        var columns = ComputeColumns(width);
        var cellWidth = width > 0
            ? Math.Max(1, (width - (columns - 1) * _columnSpacing) / columns)
            : _minItemWidth;
        var rowHeight = Math.Ceiling(cellWidth * _aspectRatio + _textBandHeight);
        return (columns, cellWidth, rowHeight);
    }

    private static bool IntersectsVertically(double top, double bottom, Rect realization)
    {
        // No viewport resolved yet — realize a first chunk so the grid is never blank.
        if (double.IsNaN(realization.Height) || realization.Height <= 0)
            return top < 1200;
        return top < realization.Bottom && bottom > realization.Top;
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var (columns, cellWidth, rowHeight) = ResolveGrid(availableSize.Width);
        var itemCount = context.ItemCount;
        var realization = context.RealizationRect;

        for (var i = 0; i < itemCount; i++)
        {
            var top = (i / columns) * (rowHeight + _rowSpacing);
            if (!IntersectsVertically(top, top + rowHeight, realization))
                continue;
            context.GetOrCreateElementAt(i).Measure(new Size(cellWidth, rowHeight));
        }

        var rows = itemCount <= 0 ? 0 : (itemCount + columns - 1) / columns;
        var height = rows <= 0 ? 0 : rows * rowHeight + (rows - 1) * _rowSpacing;
        var width = double.IsNaN(availableSize.Width) || double.IsInfinity(availableSize.Width)
            ? 0
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var (columns, cellWidth, rowHeight) = ResolveGrid(finalSize.Width);
        var itemCount = context.ItemCount;
        var realization = context.RealizationRect;

        for (var i = 0; i < itemCount; i++)
        {
            var row = i / columns;
            var col = i % columns;
            var top = row * (rowHeight + _rowSpacing);
            if (!IntersectsVertically(top, top + rowHeight, realization))
                continue;

            var x = col * (cellWidth + _columnSpacing);
            context.GetOrCreateElementAt(i).Arrange(new Rect(x, top, cellWidth, rowHeight));
        }

        return finalSize;
    }
}
