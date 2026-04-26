namespace Wavee.UI.WinUI.Data.Enums;

/// <summary>
/// Where the player UI is mounted in the shell. Mutually exclusive — the
/// player is in exactly one of these locations at any time. Persisted as
/// part of <see cref="Wavee.UI.WinUI.Data.Models.ShellLayoutState"/>.
/// </summary>
public enum PlayerLocation
{
    /// <summary>Default. Player is the bottom-docked PlayerBar.</summary>
    Bottom,
    /// <summary>Player is the SidebarPlayerWidget at the top of the left sidebar pane.</summary>
    Sidebar
}
