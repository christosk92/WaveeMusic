using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Models;

public sealed partial class AppModel : ObservableObject
{
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private bool _isMainWindowClosed;

    [ObservableProperty]
    private int _tabStripSelectedIndex;

    [ObservableProperty]
    private double _sidebarWidth;

    [ObservableProperty]
    private double _rightPanelWidth;

    public AppModel(ISettingsService settings)
    {
        _settings = settings;
        _sidebarWidth = _settings.Settings.SidebarWidth;
        _rightPanelWidth = _settings.Settings.RightPanelWidth;
    }

    partial void OnSidebarWidthChanged(double value)
    {
        _settings.Update(s => s.SidebarWidth = value);
    }

    partial void OnRightPanelWidthChanged(double value)
    {
        _settings.Update(s => s.RightPanelWidth = value);
    }
}
