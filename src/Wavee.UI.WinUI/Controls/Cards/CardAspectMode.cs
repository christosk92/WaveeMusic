namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Aspect ratios <see cref="ContentCard"/> can render its image host at.
/// Square is the historical default — every existing call site stays Square unless
/// it opts in. Tall/Wide/Backdrop unlock per-kind card shapes for the local-files
/// surfaces (TV posters, music-video thumbs, continue-watching backdrops) without
/// forking the control.
/// </summary>
public enum CardAspectMode
{
    /// <summary>1:1. Albums, playlists, music tracks. Default.</summary>
    Square = 0,

    /// <summary>2:3 portrait. TV show / movie posters.</summary>
    Tall = 1,

    /// <summary>16:9 landscape. Music video thumbnails.</summary>
    Wide = 2,

    /// <summary>16:9 landscape, semantically tagged as a hero/continue-watching
    /// backdrop so future overlay styling can target this mode without disturbing
    /// music-video cards.</summary>
    Backdrop = 3,
}
