// Cursor extension for UIElement.
// Uses [UnsafeAccessor] to call the protected ProtectedCursor setter without
// reflection — AOT-safe; the linker preserves the accessor target by
// attribute, and the call site is a direct, type-checked method invocation.

using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Helpers.UI;

public static class FrameworkElementExtensions
{
    /// <summary>
    /// Changes the cursor for the specified <see cref="UIElement"/> by invoking
    /// the protected <c>ProtectedCursor</c> setter directly. AOT-safe
    /// replacement for the prior reflection-based implementation.
    /// </summary>
    public static void ChangeCursor(this UIElement element, InputCursor? cursor)
        => SetProtectedCursor(element, cursor);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_ProtectedCursor")]
    private static extern void SetProtectedCursor(UIElement target, InputCursor? value);
}
