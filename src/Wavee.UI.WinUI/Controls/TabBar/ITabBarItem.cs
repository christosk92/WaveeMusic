using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.Controls.TabBar;

public interface ITabBarItem
{
    string? Header { get; }
    IconSource? IconSource { get; }
    string? ToolTipText { get; }
    Frame ContentFrame { get; }
    TabItemParameter? NavigationParameter { get; set; }
}
