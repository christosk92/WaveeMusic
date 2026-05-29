using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Wavee.UI.WinUI.Controls.Reorder;

/// <summary>
/// Screen-reader live announcements for keyboard/pointer reorder. Uses
/// <see cref="AutomationPeer.RaiseNotificationEvent"/> (AOT-safe — no reflection),
/// so Narrator speaks "Lifted … position 3 of 12" without a dedicated live region.
/// </summary>
public static class ReorderAnnouncer
{
    public static void Announce(UIElement? source, string message)
    {
        if (source is not FrameworkElement fe) return;
        var peer = FrameworkElementAutomationPeer.FromElement(fe)
                   ?? FrameworkElementAutomationPeer.CreatePeerForElement(fe);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.MostRecent,
            message,
            "WaveeReorder");
    }
}
