using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Data.Models;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class AppModel : ObservableObject
{
    private readonly IShellSessionService _shellSession;
    private bool _isHydrating;

    [ObservableProperty]
    public partial bool IsMainWindowClosed { get; set; }

    [ObservableProperty]
    public partial int TabStripSelectedIndex { get; set; }

    [ObservableProperty]
    public partial double SidebarWidth { get; set; }

    [ObservableProperty]
    public partial SidebarDisplayMode SidebarDisplayMode { get; set; } = SidebarDisplayMode.Expanded;

    [ObservableProperty]
    public partial bool IsSidebarPaneOpen { get; set; }

    [ObservableProperty]
    public partial double RightPanelWidth { get; set; }

    [ObservableProperty]
    public partial bool IsRightPanelOpen { get; set; }

    [ObservableProperty]
    public partial RightPanelMode RightPanelMode { get; set; } = RightPanelMode.Queue;

    [ObservableProperty]
    public partial PlayerLocation PlayerLocation { get; set; } = PlayerLocation.Bottom;

    [ObservableProperty]
    public partial bool SidebarPlayerCollapsed { get; set; }

    public AppModel(IShellSessionService shellSession)
    {
        _shellSession = shellSession;
        SidebarWidth = 280;
        RightPanelWidth = 300;
    }

    public void InitializeFromSettings()
    {
        var layout = _shellSession?.GetLayoutSnapshot() ?? new ShellLayoutState();

        _isHydrating = true;
        try
        {
            SidebarWidth = layout.SidebarWidth;
            SidebarDisplayMode = layout.SidebarDisplayMode;
            IsSidebarPaneOpen = layout.IsSidebarPaneOpen;
            RightPanelWidth = layout.RightPanelWidth;
            IsRightPanelOpen = layout.IsRightPanelOpen;
            RightPanelMode = layout.RightPanelMode;
            TabStripSelectedIndex = layout.SelectedTabIndex;
            PlayerLocation = layout.PlayerLocation;
            SidebarPlayerCollapsed = layout.SidebarPlayerCollapsed;
        }
        finally
        {
            _isHydrating = false;
        }
    }

    partial void OnSidebarWidthChanged(double value)
    {
        if (_isHydrating) return;
        _shellSession.UpdateLayout(s => s.SidebarWidth = value);
    }

    partial void OnSidebarDisplayModeChanged(SidebarDisplayMode value)
    {
        if (_isHydrating) return;
        _shellSession.UpdateLayout(s => s.SidebarDisplayMode = value);
    }

    partial void OnIsSidebarPaneOpenChanged(bool value)
    {
        if (_isHydrating) return;
        _shellSession.UpdateLayout(s => s.IsSidebarPaneOpen = value);
    }

    partial void OnRightPanelWidthChanged(double value)
    {
        if (_isHydrating) return;
        _shellSession.UpdateLayout(s => s.RightPanelWidth = value);
    }

    partial void OnIsRightPanelOpenChanged(bool value)
    {
        if (_isHydrating) return;
        _shellSession.UpdateLayout(s => s.IsRightPanelOpen = value);
    }

    partial void OnRightPanelModeChanged(RightPanelMode value)
    {
        if (_isHydrating) return;
        _shellSession.UpdateLayout(s => s.RightPanelMode = value);
    }

    partial void OnTabStripSelectedIndexChanged(int value)
    {
        if (_isHydrating) return;
        _shellSession.UpdateLayout(s => s.SelectedTabIndex = value);
    }

    partial void OnPlayerLocationChanged(PlayerLocation value)
    {
        if (_isHydrating) return;
        _shellSession.UpdateLayout(s => s.PlayerLocation = value);
    }

    partial void OnSidebarPlayerCollapsedChanged(bool value)
    {
        if (_isHydrating) return;
        _shellSession.UpdateLayout(s => s.SidebarPlayerCollapsed = value);
    }
}