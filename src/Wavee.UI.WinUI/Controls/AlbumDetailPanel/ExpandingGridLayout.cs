using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.AlbumDetailPanel;

/// <summary>
/// Virtualizing uniform-grid layout for the artist-page discography grid.
///
/// <para>Lays album cards in a uniform virtualized grid. When an album is
/// expanded (<see cref="ExpandedAlbumOrdinal"/> &gt;= 0) it reserves an empty
/// full-width band of <see cref="ExpanderHeight"/> directly below that album's
/// row. The detail panel itself is a separate overlay child owned by
/// <see cref="ExpandableAlbumGrid"/> and positioned into that band — it is NOT
/// a repeater item, so expanding/collapsing never realizes or recycles
/// anything and card artwork never flashes.</para>
/// </summary>
public sealed class ExpandingGridLayout : VirtualizingLayout
{
    internal const double MinItemWidth = 160;
    internal const double ColumnSpacing = 16;
    internal const double RowSpacing = 20;
    internal const double MinCardHeight = 220;

    // Uniform row height — grows to the tallest realized card, never below
    // MinCardHeight. Reset when the cell width changes (resize).
    private double _cardHeight = MinCardHeight;
    private double _cardHeightCellWidth = -1;

    private int _expandedOrdinal = -1;
    private double _expanderHeight;

    private double _lastGapTop = double.NaN;
    private double _lastNotchX = double.NaN;

    /// <summary>Album index whose row the expander band follows; -1 = collapsed.</summary>
    public int ExpandedAlbumOrdinal
    {
        get => _expandedOrdinal;
        set
        {
            if (_expandedOrdinal == value)
                return;
            _expandedOrdinal = value;
            InvalidateMeasure();
        }
    }

    /// <summary>Height of the reserved expander band (set from the panel's measured height).</summary>
    public double ExpanderHeight
    {
        get => _expanderHeight;
        set
        {
            if (Math.Abs(_expanderHeight - value) <= 0.5)
                return;
            _expanderHeight = value;
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// Raised after an arrange pass with (gapTop, notchX). <c>gapTop</c> is the
    /// Y of the reserved band's top, or -1 when collapsed; <c>notchX</c> is the
    /// X centre of the expanded album's column. Lets the host position the
    /// detail-panel overlay.
    /// </summary>
    public event Action<double, double>? ExpanderGeometryChanged;

    private static int ComputeColumns(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return 4;
        return Math.Max(1, (int)Math.Floor((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing)));
    }

    private static bool IntersectsVertically(Rect item, Rect realization)
    {
        // No viewport resolved yet — realize a first chunk so the grid is never
        // blank for a transient frame.
        if (double.IsNaN(realization.Height) || realization.Height <= 0)
            return item.Top < 1200;
        return item.Top < realization.Bottom && item.Bottom > realization.Top;
    }

    private Rect RectForIndex(int index, int columns, double cellWidth, double cardHeight, int expandedRow)
    {
        var row = index / columns;
        var col = index % columns;
        var x = col * (cellWidth + ColumnSpacing);
        var y = row * (cardHeight + RowSpacing);
        if (expandedRow >= 0 && row > expandedRow)
            y += _expanderHeight + RowSpacing;
        return new Rect(x, y, cellWidth, cardHeight);
    }

    private (int Columns, double CellWidth) ResolveGrid(double width)
    {
        var columns = ComputeColumns(width);
        var cellWidth = width > 0
            ? Math.Max(1, (width - (columns - 1) * ColumnSpacing) / columns)
            : MinItemWidth;

        if (Math.Abs(cellWidth - _cardHeightCellWidth) > 0.5)
        {
            _cardHeight = MinCardHeight;
            _cardHeightCellWidth = cellWidth;
        }
        return (columns, cellWidth);
    }

    private int ExpandedRow(int columns, int itemCount)
        => _expandedOrdinal >= 0 && _expandedOrdinal < itemCount
            ? _expandedOrdinal / columns
            : -1;

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var width = availableSize.Width;
        if (double.IsNaN(width) || double.IsInfinity(width) || width < 0)
            width = 0;

        var (columns, cellWidth) = ResolveGrid(width);
        var itemCount = context.ItemCount;
        var expandedRow = ExpandedRow(columns, itemCount);
        var realization = context.RealizationRect;

        var tallest = _cardHeight;
        for (var i = 0; i < itemCount; i++)
        {
            var rect = RectForIndex(i, columns, cellWidth, _cardHeight, expandedRow);
            if (!IntersectsVertically(rect, realization))
                continue;

            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(cellWidth, double.PositiveInfinity));
            if (element.DesiredSize.Height > tallest)
                tallest = element.DesiredSize.Height;
        }

        if (tallest > _cardHeight + 0.5)
        {
            _cardHeight = Math.Max(MinCardHeight, Math.Ceiling(tallest));
            InvalidateMeasure();
        }

        var height = TotalHeight(itemCount, columns, expandedRow);
        return new Size(width, height);
    }

    private double TotalHeight(int itemCount, int columns, int expandedRow)
    {
        if (itemCount <= 0)
            return 0;
        var rows = (itemCount + columns - 1) / columns;
        var height = rows * _cardHeight + (rows - 1) * RowSpacing;
        if (expandedRow >= 0)
            height += _expanderHeight + RowSpacing;
        return height;
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var width = finalSize.Width;
        var (columns, cellWidth) = ResolveGrid(width);
        var itemCount = context.ItemCount;
        var expandedRow = ExpandedRow(columns, itemCount);
        var realization = context.RealizationRect;

        for (var i = 0; i < itemCount; i++)
        {
            var rect = RectForIndex(i, columns, cellWidth, _cardHeight, expandedRow);
            if (!IntersectsVertically(rect, realization))
                continue;
            context.GetOrCreateElementAt(i).Arrange(rect);
        }

        double gapTop;
        double notchX;
        if (expandedRow >= 0)
        {
            gapTop = expandedRow * (_cardHeight + RowSpacing) + _cardHeight + RowSpacing;
            var notchCol = _expandedOrdinal % columns;
            notchX = notchCol * (cellWidth + ColumnSpacing) + cellWidth / 2;
        }
        else
        {
            gapTop = -1;
            notchX = 0;
        }

        if (double.IsNaN(_lastGapTop) || Math.Abs(gapTop - _lastGapTop) > 0.5
            || double.IsNaN(_lastNotchX) || Math.Abs(notchX - _lastNotchX) > 0.5)
        {
            _lastGapTop = gapTop;
            _lastNotchX = notchX;
            ExpanderGeometryChanged?.Invoke(gapTop, notchX);
        }

        return finalSize;
    }
}
