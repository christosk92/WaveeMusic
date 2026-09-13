namespace Wavee;

/// <summary>
/// The one rule governing <c>PlaybackBridge.DetachedFullscreen</c> — whether the DETACHED pop-out video window
/// is presenting itself borderless-fullscreen on its own monitor.
///
/// <para>Pure and dependency-free (System only, like its siblings <see cref="PlacementCore"/>,
/// <c>VideoUpgradeGate</c> and <c>LyricsSyncGate</c>) so it can be unit-tested without an engine, a window or a host —
/// the repo's standing answer to "this decision lives inside a Component and cannot be reached from a test".</para>
///
/// <para><b>Why the mode is expressed as a rule rather than a set of edges.</b> The pop-out's fullscreen bit describes a
/// window, so it is meaningful for exactly as long as that window is the resolved placement. Writing it as "keep iff
/// still <see cref="SurfacePlacement.Detached"/>" — evaluated at the single write path for the placement state —
/// makes "closed the pop-out while fullscreen, reopened it, it came back fullscreen" unrepresentable rather than
/// merely fixed: there is no list of clearing edges (the ✕, Alt+F4, a placement move, turn-off, a track with no video,
/// a host that can no longer open a second window) for anyone to forget to extend.</para>
///
/// <para>Note this is deliberately NOT <see cref="SurfacePlacement.Fullscreen"/>, which is the MAIN window's full-bleed
/// surface. Two different OS windows, two different states — see <c>PlaybackBridge.DetachedFullscreen</c> for
/// why folding them into one enum re-introduces the monitor hop this whole mechanism exists to prevent.</para>
/// </summary>
public static class DetachedFullscreenRule
{
    /// <summary>The pop-out fullscreen bit AFTER a placement commit: the current bit, kept only while
    /// <paramref name="resolved"/> is still <see cref="SurfacePlacement.Detached"/>. Never turns the mode ON — only the
    /// user's toggle does that, which is what guarantees a freshly opened pop-out starts windowed.</summary>
    /// <param name="current">The bit before the commit.</param>
    /// <param name="resolved">The placement resolved from the committed state (<see cref="PlacementCore.Resolve"/>).</param>
    public static bool After(bool current, SurfacePlacement resolved)
        => current && resolved == SurfacePlacement.Detached;
}
