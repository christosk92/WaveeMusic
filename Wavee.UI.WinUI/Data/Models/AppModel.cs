using CommunityToolkit.Mvvm.ComponentModel;

namespace Wavee.UI.WinUI.Data.Models;

public sealed partial class AppModel : ObservableObject
{
    [ObservableProperty]
    private bool _isMainWindowClosed;

    [ObservableProperty]
    private int _tabStripSelectedIndex;

    [ObservableProperty]
    private double _sidebarWidth = 280;
}
