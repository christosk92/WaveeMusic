namespace Wavee.UI.WinUI.Controls.SidebarPlayer;

/// <summary>
/// Right-column content state for the floating player's expanded "now playing"
/// layout. Mirrors Apple Music's iPad full-screen toggles — lyrics, queue, or
/// "just the album art" with neither.
/// </summary>
public enum ExpandedPlayerContentMode
{
    /// <summary>Right column hidden; album art grows to fill the window.</summary>
    None,
    /// <summary>Right column shows the synced lyrics view.</summary>
    Lyrics,
    /// <summary>Right column shows the play queue.</summary>
    Queue
}
