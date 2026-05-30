using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Helpers for distinguishing pointer input kinds inside custom gesture controllers.
/// </summary>
internal static class PointerInput
{
    /// <summary>
    /// True when the gesture originates from touch. The custom press-drag controllers
    /// (<see cref="Wavee.UI.WinUI.Controls.Reorder.ReorderController"/> and
    /// <see cref="ManualDragAttachment"/>) exclude touch so a finger pan scrolls the
    /// list instead of lifting a row (issue #4). Mouse and pen still drag-reorder;
    /// keyboard reorder is unaffected.
    /// </summary>
    public static bool IsTouch(PointerRoutedEventArgs e)
        => e.Pointer.PointerDeviceType == PointerDeviceType.Touch;
}
