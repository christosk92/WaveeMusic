using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// A control that allows resizing of Grid columns by dragging.
/// Simplified implementation focused on horizontal column resizing.
/// </summary>
public sealed partial class GridSplitter : Control
{
    private static readonly InputCursor ResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    private bool _isDragging;
    private bool _isPointerOver;
    private double _startWidth;
    private ColumnDefinition? _targetColumn;

    public GridSplitter()
    {
        DefaultStyleKey = typeof(GridSplitter);
        ManipulationMode = ManipulationModes.TranslateX;
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
        ProtectedCursor = ResizeCursor;
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

        // Find the target column (the one before the splitter)
        if (Parent is Grid grid)
        {
            var columnIndex = Grid.GetColumn(this);
            if (columnIndex > 0 && columnIndex <= grid.ColumnDefinitions.Count)
            {
                _targetColumn = grid.ColumnDefinitions[columnIndex - 1];
                _startWidth = _targetColumn.ActualWidth;
            }
        }
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_targetColumn == null) return;

        var newWidth = _startWidth + e.Cumulative.Translation.X;

        // Respect min/max constraints
        if (!double.IsNaN(_targetColumn.MinWidth) && newWidth < _targetColumn.MinWidth)
        {
            newWidth = _targetColumn.MinWidth;
        }
        if (!double.IsNaN(_targetColumn.MaxWidth) && newWidth > _targetColumn.MaxWidth)
        {
            newWidth = _targetColumn.MaxWidth;
        }

        // Ensure minimum width
        if (newWidth < 100)
        {
            newWidth = 100;
        }

        _targetColumn.Width = new GridLength(newWidth, GridUnitType.Pixel);
    }

    private void OnManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        _isDragging = false;
        _targetColumn = null;
        VisualStateManager.GoToState(this, _isPointerOver ? "PointerOver" : "Normal", true);
    }
}
