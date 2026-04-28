using System.Collections.Generic;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Data.Models;

public sealed class ShellSessionState
{
    public bool Initialized { get; set; }
    public ShellLayoutState Layout { get; set; } = new();
    public string? SelectedSidebarTag { get; set; }
    public List<SidebarGroupState> SidebarGroups { get; set; } = [];
    public List<TabSessionState> Tabs { get; set; } = [];
}

public sealed class ShellLayoutState
{
    public double SidebarWidth { get; set; } = 280;
    public SidebarDisplayMode SidebarDisplayMode { get; set; } = SidebarDisplayMode.Expanded;
    public bool IsSidebarPaneOpen { get; set; }
    public double RightPanelWidth { get; set; } = 300;
    public bool IsRightPanelOpen { get; set; }
    public RightPanelMode RightPanelMode { get; set; } = RightPanelMode.Queue;
    public int SelectedTabIndex { get; set; }
    public PlayerLocation PlayerLocation { get; set; } = PlayerLocation.Bottom;
    public bool SidebarPlayerCollapsed { get; set; }

    // ── Tear-off (floating-window) state per detachable panel ──
    // Owned by PanelDockingService. On restart we recreate floating windows
    // when *Detached is true, restoring saved geometry and clamping to a
    // visible monitor when the saved coords aren't on any current display.
    public bool PlayerWindowDetached { get; set; }
    public double PlayerWindowX { get; set; }
    public double PlayerWindowY { get; set; }
    public double PlayerWindowWidth { get; set; } = 320;
    public double PlayerWindowHeight { get; set; } = 540;

    // Expanded "now playing" mode for the floating player (Apple-Music-style
    // 2-column layout). Geometry is tracked separately so toggling expand
    // restores each mode's last-known size.
    public bool PlayerWindowExpanded { get; set; }
    public string PlayerWindowExpandedMode { get; set; } = "Lyrics"; // "None" | "Lyrics" | "Queue"
    public double PlayerWindowExpandedX { get; set; }
    public double PlayerWindowExpandedY { get; set; }
    public double PlayerWindowExpandedWidth { get; set; } = 1100;
    public double PlayerWindowExpandedHeight { get; set; } = 640;

    public bool RightPanelWindowDetached { get; set; }
    public double RightPanelWindowX { get; set; }
    public double RightPanelWindowY { get; set; }
    public double RightPanelWindowWidth { get; set; } = 360;
    public double RightPanelWindowHeight { get; set; } = 720;
}

public sealed class SidebarGroupState
{
    public string Tag { get; set; } = "";
    public bool IsExpanded { get; set; } = true;
}

public sealed class TabSessionState
{
    public string PageTypeName { get; set; } = "";
    public SerializedNavigationParameter? Parameter { get; set; }
    public string? Header { get; set; }
    public bool IsPinned { get; set; }
    public bool IsCompact { get; set; }
}

public sealed class SerializedNavigationParameter
{
    public string TypeName { get; set; } = "";
    public string Json { get; set; } = "";
}

public sealed record RestoredTabState(
    System.Type PageType,
    object? Parameter,
    string Header,
    bool IsPinned,
    bool IsCompact);

public enum CloseTabsBehavior
{
    Save,
    Discard,
}
