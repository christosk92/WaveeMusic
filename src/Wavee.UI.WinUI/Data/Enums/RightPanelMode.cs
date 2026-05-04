namespace Wavee.UI.WinUI.Data.Enums;

public enum RightPanelMode
{
    Queue,
    Lyrics,
    FriendsActivity,
    Details,
    /// <summary>
    /// Temporary mode carrying a track selected in a TrackDataGrid. Binds to
    /// <c>ShellViewModel.SelectedTrackForDetails</c> rather than the currently-playing
    /// track (which is what the <see cref="Details"/> mode shows).
    /// </summary>
    TrackDetails
}

public enum DetailsBackgroundMode
{
    None,
    BlurredAlbumArt,
    Canvas
}
