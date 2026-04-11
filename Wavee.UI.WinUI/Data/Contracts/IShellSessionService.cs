using System.Collections.Generic;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contracts;

public interface IShellSessionService
{
    ShellLayoutState GetLayoutSnapshot();
    string? GetSelectedSidebarTag();
    bool TryGetSidebarGroupExpansion(string tag, out bool isExpanded);
    IReadOnlyList<RestoredTabState> GetRestorableTabs();
    bool AskBeforeClosingTabs { get; }
    CloseTabsBehavior CloseTabsBehavior { get; }
    void UpdateLayout(System.Action<ShellLayoutState> update);
    void UpdateSelectedSidebarTag(string? tag);
    void UpdateSidebarGroupExpansion(string tag, bool isExpanded);
    void SaveTabs(IReadOnlyList<TabBarItem> tabs, int selectedIndex);
    void ClearTabs();
    void UpdateClosePreference(bool askBeforeClosingTabs, CloseTabsBehavior behavior);
}
