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
