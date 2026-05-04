using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Wavee.UI.WinUI.Controls;

public sealed class GridSplitterResizeCompletedEventArgs : EventArgs
{
    public double NewWidth { get; init; }
    public double NewHeight { get; init; }
}

/// <summary>
/// A control that allows resizing of Grid columns or rows by dragging.
/// Set <see cref="Orientation"/> to Vertical for row resizing.
/// </summary>
public sealed partial class GridSplitter : Control
{
    public event EventHandler<GridSplitterResizeCompletedEventArgs>? ResizeCompleted;

    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(GridSplitter),
            new PropertyMetadata(Orientation.Horizontal, OnOrientationChanged));

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GridSplitter splitter)
            splitter.ApplyOrientationDefaults();
    }

    private static readonly InputCursor HorizontalResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor VerticalResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);

    private bool _isDragging;
    private bool _isPointerOver;
    private double _startSize;
    private ColumnDefinition? _targetColumn;
    private RowDefinition? _targetRow;

    public GridSplitter()
    {
        DefaultStyleKey = typeof(GridSplitter);
        ManipulationMode = ManipulationModes.TranslateX;
    }

    private void ApplyOrientationDefaults()
    {
        if (Orientation == Orientation.Vertical)
        {
            ManipulationMode = ManipulationModes.TranslateY;
            Width = double.NaN; // clear so it stretches horizontally
        }
        else
        {
            ManipulationMode = ManipulationModes.TranslateX;
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        ManipulationStarted += OnManipulationStarted;
        ManipulationDelta += OnManipulationDelta;
        ManipulationCompleted += OnManipulationCompleted;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        ProtectedCursor = Orientation == Orientation.Vertical ? VerticalResizeCursor : HorizontalResizeCursor;
        if (!_isDragging)
        {
            VisualStateManager.GoToState(this, "PointerOver", true);
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        ProtectedCursor = null;
        if (!_isDragging)
        {
            VisualStateManager.GoToState(this, "Normal", true);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, "Pressed", true);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, _isPointerOver ? "PointerOver" : "Normal", true);
    }

    private void OnManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        _isDragging = true;
        VisualStateManager.GoToState(this, "Pressed", true);

        if (Parent is not Grid grid) return;

        if (Orientation == Orientation.Vertical)
        {
            var rowIndex = Grid.GetRow(this);
            if (rowIndex > 0 && rowIndex <= grid.RowDefinitions.Count)
            {
                _targetRow = grid.RowDefinitions[rowIndex - 1];
                _startSize = _targetRow.ActualHeight;
            }
        }
        else
        {
            var columnIndex = Grid.GetColumn(this);
            if (columnIndex > 0 && columnIndex <= grid.ColumnDefinitions.Count)
            {
                _targetColumn = grid.ColumnDefinitions[columnIndex - 1];
                _startSize = _targetColumn.ActualWidth;
            }
        }
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (Orientation == Orientation.Vertical)
        {
            if (_targetRow == null) return;

            var newHeight = _startSize + e.Cumulative.Translation.Y;

            if (!double.IsNaN(_targetRow.MinHeight) && newHeight < _targetRow.MinHeight)
                newHeight = _targetRow.MinHeight;
            if (!double.IsNaN(_targetRow.MaxHeight) && newHeight > _targetRow.MaxHeight)
                newHeight = _targetRow.MaxHeight;

            _targetRow.Height = new GridLength(newHeight, GridUnitType.Pixel);
        }
        else
        {
            if (_targetColumn == null) return;

            var newWidth = _startSize + e.Cumulative.Translation.X;

            if (!double.IsNaN(_targetColumn.MinWidth) && newWidth < _targetColumn.MinWidth)
                newWidth = _targetColumn.MinWidth;
            if (!double.IsNaN(_targetColumn.MaxWidth) && newWidth > _targetColumn.MaxWidth)
                newWidth = _targetColumn.MaxWidth;

            _targetColumn.Width = new GridLength(newWidth, GridUnitType.Pixel);
        }
    }

    private void OnManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        double finalWidth = 0, finalHeight = 0;

        if (Orientation == Orientation.Vertical)
            finalHeight = _targetRow?.Height.Value ?? 0;
        else
            finalWidth = _targetColumn?.Width.Value ?? 0;

        _isDragging = false;
        _targetColumn = null;
        _targetRow = null;
        VisualStateManager.GoToState(this, _isPointerOver ? "PointerOver" : "Normal", true);

        if (finalWidth > 0 || finalHeight > 0)
            ResizeCompleted?.Invoke(this, new GridSplitterResizeCompletedEventArgs { NewWidth = finalWidth, NewHeight = finalHeight });
    }
}
