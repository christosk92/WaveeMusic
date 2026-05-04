namespace Wavee.Connect.Events;

/// <summary>
/// Reasons for playback starting or ending.
/// Used in TrackTransitionEvent for accurate reporting.
/// </summary>
public enum PlaybackReason
{
    /// <summary>Track completed naturally ("trackdone").</summary>
    TrackDone,

    /// <summary>Track error occurred ("trackerror").</summary>
    TrackError,

    /// <summary>User pressed forward/skip next ("fwdbtn").</summary>
    ForwardBtn,

    /// <summary>User pressed back/skip previous ("backbtn").</summary>
    BackBtn,

    /// <summary>Playback ended/stopped ("endplay").</summary>
    EndPlay,

    /// <summary>User pressed play button ("playbtn").</summary>
    PlayBtn,

    /// <summary>User clicked a track row ("clickrow").</summary>
    ClickRow,

    /// <summary>User logged out ("logout").</summary>
    Logout,

    /// <summary>App loaded/started ("appload").</summary>
    AppLoad,

    /// <summary>Remote device command ("remote").</summary>
    Remote
}

/// <summary>
/// Extension methods for PlaybackReason.
/// </summary>
public static class PlaybackReasonExtensions
{
    /// <summary>
    /// Gets the string value used in event reporting.
    /// </summary>
    public static string ToEventValue(this PlaybackReason reason)
    {
        return reason switch
        {
            PlaybackReason.TrackDone => "trackdone",
            PlaybackReason.TrackError => "trackerror",
            PlaybackReason.ForwardBtn => "fwdbtn",
            PlaybackReason.BackBtn => "backbtn",
            PlaybackReason.EndPlay => "endplay",
            PlaybackReason.PlayBtn => "playbtn",
            PlaybackReason.ClickRow => "clickrow",
            PlaybackReason.Logout => "logout",
            PlaybackReason.AppLoad => "appload",
            PlaybackReason.Remote => "remote",
            _ => "unknown"
        };
    }
}
