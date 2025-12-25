using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Controls.TabBar;

public interface ITabBar
{
    ObservableCollection<TabBarItem> Items { get; }
    TabBarItem? SelectedItem { get; set; }
    int SelectedIndex { get; set; }

    event EventHandler<TabBarItem>? TabCloseRequested;
    event EventHandler? AddTabRequested;

    void CloseTab(TabBarItem tabItem);
    Task ReopenClosedTabAsync();
}
