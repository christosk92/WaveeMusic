using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Data.Models;

public sealed partial class AppModel : ObservableObject
{
    private readonly IShellSessionService _shellSession;
    private bool _isHydrating;

    [ObservableProperty]
    private bool _isMainWindowClosed;

    [ObservableProperty]
    private int _tabStripSelectedIndex;

    [ObservableProperty]
    private double _sidebarWidth;

    [ObservableProperty]
    private SidebarDisplayMode _sidebarDisplayMode = SidebarDisplayMode.Expanded;

    [ObservableProperty]
    private bool _isSidebarPaneOpen;

    [ObservableProperty]
    private double _rightPanelWidth;

    [ObservableProperty]
    private bool _isRightPanelOpen;

    [ObservableProperty]
    private RightPanelMode _rightPanelMode = RightPanelMode.Queue;

    public AppModel(IShellSessionService shellSession)
    {
        _shellSession = shellSession;
        _sidebarWidth = 280;
        _rightPanelWidth = 300;
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
}
