using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// ContentControl subclass whose only purpose is to expose the otherwise-protected
/// <see cref="UIElement.ProtectedCursor"/> through public helpers, so a
/// containing page can paint the hand-cursor affordance on hover for
/// click-to-act rows (collab stack, etc).
///
/// Why ContentControl and not Border: WinUI 3's <c>Border</c> is <c>sealed</c>
/// — can't subclass. <c>ContentControl</c> is the next-cleanest single-child
/// container, ships with <c>Background</c> / <c>BorderBrush</c> /
/// <c>BorderThickness</c> / <c>CornerRadius</c> / <c>Padding</c> on the base
/// <c>Control</c>, so the previous Border-shaped XAML migrates verbatim.
///
/// Why not just set ProtectedCursor on a vanilla Border from the page:
/// <c>ProtectedCursor</c> is, well, protected — only callable from the
/// element's own class hierarchy. Setting it from a page raises CS1540.
/// Subclassing once and exposing two thin methods keeps the rest of the
/// styling (background, padding, corner radius, click handler) declarative
/// without further ceremony.
/// </summary>
public sealed class ClickableBorder : ContentControl
{
    public ClickableBorder()
    {
        // Match the default WinUI Border content alignment so the inner child
        // doesn't get centered inside the host (ContentControl's default).
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
    }

    /// <summary>Sets <see cref="UIElement.ProtectedCursor"/> to a hand cursor.</summary>
    public void ShowHandCursor()
        => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    /// <summary>Resets <see cref="UIElement.ProtectedCursor"/> to its default (null).</summary>
    public void ClearCursor() => ProtectedCursor = null;
}
