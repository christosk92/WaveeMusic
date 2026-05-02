using System;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.Controls.TabBar;

public interface ITabBarItemContent
{
    TabItemParameter? TabItemParameter { get; }
    event EventHandler<TabItemParameter>? ContentChanged;

    /// <summary>
    /// When true, navigating to the same page type with a different parameter
    /// reuses the live page instance through <see cref="RefreshWithParameter"/>.
    /// Pages with large scroll/transition state can return false to let the
    /// Frame create a fresh page entry instead.
    /// </summary>
    bool ReuseForParameterNavigation => true;

    /// <summary>
    /// Called when navigating to the same page type with a different parameter.
    /// Allows the page to update its content without being destroyed and recreated.
    /// </summary>
    void RefreshWithParameter(object? parameter) { }
}
