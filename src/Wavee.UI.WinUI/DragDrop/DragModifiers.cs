using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;
using Wavee.UI.Services.DragDrop;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Captures keyboard modifiers held at drop time so handlers can flip
/// semantics (Shift = "Play next" vs default "Add to queue", etc).
/// </summary>
internal static class DragModifiersCapture
{
    public static DropModifiers Current()
    {
        var modifiers = DropModifiers.None;
        if (IsDown(VirtualKey.Shift))   modifiers |= DropModifiers.Shift;
        if (IsDown(VirtualKey.Control)) modifiers |= DropModifiers.Control;
        if (IsDown(VirtualKey.Menu))    modifiers |= DropModifiers.Alt;
        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
