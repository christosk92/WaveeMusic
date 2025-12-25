using System;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.Controls.TabBar;

public interface ITabBarItemContent
{
    TabItemParameter? TabItemParameter { get; }
    event EventHandler<TabItemParameter>? ContentChanged;
}
