namespace Wavee;

/// <summary>
/// Which input affordances a video stage gets, by the surface it sits in (<see cref="TransportOwner"/>). The shared
/// <c>PopOutVideoStage</c> maps these onto <c>MediaPlayerElement.DragMovesWindow</c> / <c>CursorAutoHide</c>.
///
/// <para>Pure and dependency-free (System + <see cref="TransportOwner"/> only, like its siblings <see cref="PlacementCore"/>
/// and <see cref="DetachedFullscreenRule"/>) so the rule is pinned by <c>VideoStageInputTests</c> without an engine — the
/// repo's standing answer to "this decision lives inside a Component and cannot be reached from a test". It returns
/// plain bools rather than the engine's <c>CursorAutoHidePolicy</c> so the test assembly stays FluentGpu-free.</para>
/// </summary>
public static class VideoStageInput
{
    /// <summary>Whether a press on the PICTURE that travels past the drag box moves the WINDOW (the OS move loop — Aero
    /// Snap, the snap bar and monitor hops included). Only the pop-out OWNS its window, and that window is chromeless, so
    /// the picture is the only thing to grab. Dragging the in-window mini player's or the docked card's picture must never
    /// move the MAIN window (and a drag capture inside a scroller would steal touch pans); a fullscreen window has nowhere
    /// to go.</summary>
    /// <param name="identity">The surface's transport identity (constant for the life of the surface).</param>
    /// <param name="hostFullscreen">Whether the surface is presenting fullscreen right now.</param>
    public static bool DragMovesWindow(TransportOwner identity, bool hostFullscreen)
        => identity == TransportOwner.PopOut && !hostFullscreen;

    /// <summary>Whether the idle cursor hides with the chrome while WINDOWED. A dedicated video window hides it (mpv's
    /// windowed default, <c>cursor-autohide=1000</c>); an inline surface keeps the page's cursor and hides it only in
    /// fullscreen (mpv's <c>--cursor-autohide-fs-only</c>) — hiding it over a small video steals it from the page around
    /// it, and the user cannot tell whether the app has hung.</summary>
    /// <param name="identity">The surface's transport identity.</param>
    public static bool HidesCursorWindowed(TransportOwner identity) => identity == TransportOwner.PopOut;
}
