// Cursor extension for FrameworkElement
// Based on CommunityToolkit pattern using ProtectedCursor

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Helpers.UI;

public static class FrameworkElementExtensions
{
    /// <summary>
    /// Changes the cursor for the specified UIElement.
    /// Uses the ProtectedCursor property via reflection for WinUI 3 compatibility.
    /// </summary>
    public static void ChangeCursor(this UIElement element, InputCursor? cursor)
    {
        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        property?.SetValue(element, cursor);
    }
}
