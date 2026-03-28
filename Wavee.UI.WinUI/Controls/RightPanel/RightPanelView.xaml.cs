using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers.UI;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class RightPanelView : UserControl
{
    private const double MinPanelWidth = 200;
    private const double MaxPanelWidth = 500;

    private bool _draggingResizer;
    private double _preManipulationWidth;

    public RightPanelView()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
        Width = PanelWidth;
    }

    private void UpdateContentVisibility()
    {
        if (QueueContent == null) return; // not loaded yet

        QueueContent.Visibility = SelectedMode == RightPanelMode.Queue ? Visibility.Visible : Visibility.Collapsed;
        LyricsContent.Visibility = SelectedMode == RightPanelMode.Lyrics ? Visibility.Visible : Visibility.Collapsed;
        FriendsContent.Visibility = SelectedMode == RightPanelMode.FriendsActivity ? Visibility.Visible : Visibility.Collapsed;

        QueueTab.IsChecked = SelectedMode == RightPanelMode.Queue;
        LyricsTab.IsChecked = SelectedMode == RightPanelMode.Lyrics;
        FriendsTab.IsChecked = SelectedMode == RightPanelMode.FriendsActivity;
    }

    private void RightPanelView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateContentVisibility();
    }

    // --- Tab header clicks ---

    private void QueueTab_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = RightPanelMode.Queue;
    }

    private void LyricsTab_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = RightPanelMode.Lyrics;
    }

    private void FriendsTab_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = RightPanelMode.FriendsActivity;
    }

    // --- Resize gripper ---

    private void Resizer_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        _draggingResizer = true;
        _preManipulationWidth = PanelWidth;
        VisualStateManager.GoToState(this, "ResizerPressed", true);
        e.Handled = true;
    }

    private void Resizer_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        // Dragging left (negative X) makes the panel wider
        var newWidth = _preManipulationWidth - e.Cumulative.Translation.X;
        newWidth = System.Math.Clamp(newWidth, MinPanelWidth, MaxPanelWidth);
        PanelWidth = newWidth;
        e.Handled = true;
    }

    private void Resizer_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        _draggingResizer = false;
        VisualStateManager.GoToState(this, "ResizerNormal", true);
        e.Handled = true;
    }

    private void Resizer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var resizer = (FrameworkElement)sender;
        resizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
        VisualStateManager.GoToState(this, "ResizerPointerOver", true);
        e.Handled = true;
    }

    private void Resizer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingResizer) return;

        var resizer = (FrameworkElement)sender;
        resizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
        VisualStateManager.GoToState(this, "ResizerNormal", true);
        e.Handled = true;
    }

    private void Resizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        IsOpen = !IsOpen;
        e.Handled = true;
    }
}
