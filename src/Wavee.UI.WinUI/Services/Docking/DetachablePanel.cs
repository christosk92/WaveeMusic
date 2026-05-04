namespace Wavee.UI.WinUI.Services.Docking;

/// <summary>
/// Identifies which shell panel can be torn off into its own floating window.
/// Used as the key in <see cref="IPanelDockingService"/>.
/// </summary>
public enum DetachablePanel
{
    /// <summary>The sidebar player widget (only meaningful when PlayerLocation == Sidebar).</summary>
    Player,
    /// <summary>The right panel as a single unit — Queue/Lyrics/Friends/Details tabs travel together.</summary>
    RightPanel
}
