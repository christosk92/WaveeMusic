namespace Wavee.UI.WinUI.Controls.TabBar;

/// <summary>
/// Optional contract for cached pages that can release heavyweight UI state while
/// they are hidden, without disposing the page instance or its lightweight route state.
/// </summary>
public interface INavigationCacheMemoryParticipant
{
    void TrimForNavigationCache();
    void RestoreFromNavigationCache();
}
