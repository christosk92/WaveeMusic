using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Layouts;

/// <summary>
/// A fixed-row grid layout that fills items column-first (top to bottom, then left to right).
/// Computes column count from available width. Enforces exactly MaxRows rows.
/// Items beyond MaxRows × columns are arranged off-screen.
/// Reports computed ColumnCount via a deferred event for pagination.
/// </summary>
public sealed partial class ColumnsFirstGridLayout : NonVirtualizingLayout
{
    public static readonly DependencyProperty MaxRowsProperty =
        DependencyProperty.Register(nameof(MaxRows), typeof(int), typeof(ColumnsFirstGridLayout),
            new PropertyMetadata(3, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(ColumnsFirstGridLayout),
            new PropertyMetadata(280.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(ColumnsFirstGridLayout),
            new PropertyMetadata(56.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(ColumnsFirstGridLayout),
            new PropertyMetadata(4.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(nameof(ColumnSpacing), typeof(double), typeof(ColumnsFirstGridLayout),
            new PropertyMetadata(8.0, OnLayoutPropertyChanged));

    /// <summary>Maximum number of rows (default 3).</summary>
    public int MaxRows
    {
        get => (int)GetValue(MaxRowsProperty);
        set => SetValue(MaxRowsProperty, value);
    }

    /// <summary>Minimum width per item. Columns are computed from this + available width.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>Fixed height per item row.</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    /// <summary>Vertical spacing between rows.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Horizontal spacing between columns.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>
    /// Read-only computed column count based on available width.
    /// Bind to this from your ViewModel to keep pagination in sync.
    /// </summary>
    public int ComputedColumnCount => _computedColumnCount;

    /// <summary>Items visible per page = MaxRows × ComputedColumnCount.</summary>
    public int PageSize => MaxRows * ComputedColumnCount;

    /// <summary>Raised when column count changes (e.g. on resize).</summary>
    public event EventHandler<int>? ColumnCountChanged;

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColumnsFirstGridLayout layout)
            layout.InvalidateMeasure();
    }

    private int _columns;
    private int _computedColumnCount = 1;
    private int _lastFiredColumnCount = -1;
    private bool _pendingFireScheduled;
    private double _itemWidth;

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var children = context.Children;
        var count = children.Count;
        if (count == 0) return new Size(0, 0);

        var rawWidth = availableSize.Width;
        var isFallbackWidth = double.IsInfinity(rawWidth) || rawWidth <= 0;
        var width = isFallbackWidth ? 1000 : rawWidth;

        // Compute columns from available width
        _columns = Math.Max(1, (int)Math.Floor((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing)));
        _itemWidth = (width - (_columns - 1) * ColumnSpacing) / _columns;
        _computedColumnCount = _columns;

        // Report column count change AFTER layout completes (deferred). Firing
        // during MeasureOverride would cause ItemsSource to change mid-layout
        // → COMException.
        //
        // Two guards stack to prevent ItemsRepeater churn on nav-cache restore:
        //
        //  1. Fallback-width measures (rawWidth 0 / ∞ / NaN, the "use 1000"
        //     branch above) DO NOT fire. The transient pre-layout pass that
        //     happens when the page flips from Visibility=Collapsed back to
        //     Visible can hand us a fallback width while the real layout is
        //     still settling.
        //
        //  2. We coalesce rapid measure flutter. During a Collapsed→Visible
        //     transition the host may measure us with several intermediate
        //     widths before the final stable width lands (e.g. 400 → 800).
        //     Each transient width would otherwise enqueue its own
        //     ColumnCountChanged fire, the VM would flip ColumnCount through
        //     1 → 2, NotifyPaginationChanged would clear and rebuild the
        //     PagedTopTracks slice on each flip, every TrackItem in the
        //     ItemsRepeater would OnUnload + OnLoad once per flip, the
        //     unpin on Unload would drop the LRU pin, and any cache entry
        //     evicted between the unpin and the next OnLoad lands the row
        //     on the placeholder glyph for the rest of the session.
        //
        //     Instead: keep a single pending fire scheduled. The fire reads
        //     `_columns` at execution time, so any bouncing widths in the
        //     same dispatcher slice collapse into one fire with the final
        //     value.
        if (!isFallbackWidth && _columns != _lastFiredColumnCount && !_pendingFireScheduled)
        {
            _pendingFireScheduled = true;
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    _pendingFireScheduled = false;
                    if (_columns == _lastFiredColumnCount) return;
                    _lastFiredColumnCount = _columns;
                    ColumnCountChanged?.Invoke(this, _columns);
                });
        }

        // Visible items = min(count, maxRows × columns)
        var visible = Math.Min(count, MaxRows * _columns);

        // Measure all children (even hidden ones, for smooth pagination transitions)
        var measureSize = new Size(_itemWidth, ItemHeight);
        for (int i = 0; i < count; i++)
            children[i].Measure(measureSize);

        // Height = exactly MaxRows (always, for stable layout)
        var totalHeight = MaxRows * ItemHeight + Math.Max(0, MaxRows - 1) * RowSpacing;
        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var children = context.Children;
        var count = children.Count;
        if (count == 0) return finalSize;

        var visible = Math.Min(count, MaxRows * _columns);

        for (int i = 0; i < count; i++)
        {
            if (i < visible)
            {
                // Column-first fill: column = i / MaxRows, row = i % MaxRows
                var col = i / MaxRows;
                var row = i % MaxRows;

                var x = col * (_itemWidth + ColumnSpacing);
                var y = row * (ItemHeight + RowSpacing);

                children[i].Arrange(new Rect(x, y, _itemWidth, ItemHeight));
            }
            else
            {
                // Off-screen (hidden but measured for smooth transitions)
                children[i].Arrange(new Rect(finalSize.Width + 100, 0, _itemWidth, ItemHeight));
            }
        }

        // Always report MaxRows height for stable layout
        var totalHeight = MaxRows * ItemHeight + Math.Max(0, MaxRows - 1) * RowSpacing;
        return new Size(finalSize.Width, totalHeight);
    }
}