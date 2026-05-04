namespace Wavee.UI.WinUI.Controls.TabBar;

/// <summary>
/// Optional contract for pages that want to preserve transient UI state when a tab sleeps.
/// </summary>
public interface ITabSleepParticipant
{
    object? CaptureSleepState();
    void RestoreSleepState(object? state);
}
