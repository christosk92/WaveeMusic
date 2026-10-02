using FluentGpu.Controls;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>
/// The ONE rule that maps a pointer release on a list row to an <see cref="ItemContainerTrigger"/>. Pure and
/// engine-free so every surface (track table, user rows, the shared media surface) agrees and the rule is unit-tested.
/// </summary>
public static class RowClickPolicy
{
    /// <summary>
    /// A release with <c>ClickCount &gt;= 2</c> is a <see cref="ItemContainerTrigger.DoubleTap"/> (invoke) unless Ctrl or
    /// Shift is held: a modified double-click is the second click of a rapid toggle/extend pair, so it stays a
    /// <see cref="ItemContainerTrigger.Tap"/> (a selection gesture that never invokes). Alt and Win do not change the
    /// outcome. The engine's <c>ItemsView</c> applies the same downgrade; this keeps the app-side trigger honest.
    /// </summary>
    public static ItemContainerTrigger TriggerOf(int clickCount, KeyModifiers mods)
        => clickCount >= 2 && (mods & (KeyModifiers.Ctrl | KeyModifiers.Shift)) == 0
            ? ItemContainerTrigger.DoubleTap
            : ItemContainerTrigger.Tap;
}
