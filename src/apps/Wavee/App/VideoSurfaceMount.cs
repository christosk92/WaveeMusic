namespace Wavee;

/// <summary>
/// Pure mount rule for the now-playing video stage (PiP / pop-out). The MF session only advances while a
/// <c>MediaPlayerElement</c> pumps it — unmounting the stage because the resolved source is briefly null (override
/// re-resolve, track-edge handoff) leaves a Loading poster with no pump and a black/stuck surface over audio.
/// </summary>
public static class VideoSurfaceMount
{
    /// <summary>Mount the player stage whenever a player exists. Source may be null — overlay Loading/poster on top;
    /// do not tear down the only pump.</summary>
    public static bool ShouldMountPlayerStage(bool playerPresent) => playerPresent;
}
