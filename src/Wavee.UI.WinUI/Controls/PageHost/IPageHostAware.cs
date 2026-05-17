namespace Wavee.UI.WinUI.Controls.PageHost;

public interface IPageHostAware
{
    void OnEntered(object? parameter, PageHostNavigationMode mode);
    void OnLeaving();

    /// <summary>
    /// Replaces <c>Page.NavigationCacheMode.Disabled</c> as the cache opt-out
    /// (now that pages derive from <see cref="Microsoft.UI.Xaml.Controls.UserControl"/>
    /// instead of <see cref="Microsoft.UI.Xaml.Controls.Page"/>). Return
    /// <c>false</c> to have <see cref="PageHost"/> dispose the page on leave
    /// instead of keeping it in the LRU cache. Default <c>true</c>.
    /// </summary>
    bool ShouldCacheInHost => true;
}
