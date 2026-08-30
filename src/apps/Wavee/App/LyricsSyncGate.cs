namespace Wavee;

/// <summary>
/// The PURE, engine-free rule for whether TIMED lyric sync may run. Like its siblings
/// <see cref="MediaSwitchLogic"/>, <see cref="PlacementCore"/> and <see cref="VideoUpgradeGate"/> it takes plain values
/// read at the call site and returns a decision — no <c>Signal&lt;T&gt;</c>, no FluentGpu type — so it is source-included
/// into the engine-free unit-test project and verifiable without a GPU or a window.
///
/// <para>WHY THIS EXISTS. A lyric document's line timings belong to the SONG's audio edit. A music video is a
/// DIFFERENT edit of that song — spoken intros, alternate arrangements, a longer runtime — so while a video is the
/// current media the position the app publishes is the video's clock against the video's duration, and highlighting
/// "the line at that position" lands on the WRONG line. Sync is therefore suppressed, not because it is unavailable,
/// but because it would be actively misleading.</para>
///
/// <para>Suppression is deliberately NARROW: it stops the timed behaviour (the active-line highlight, the auto-scroll
/// and the lead-time freeze) and nothing else. The lyrics themselves stay on screen and stay freely scrollable —
/// removing them would take away something the user can still read and use. The surfaces pair this with a short note
/// so the missing highlight reads as a deliberate state rather than as broken sync.</para>
/// </summary>
public static class LyricsSyncGate
{
    /// <summary>Whether timed lyric sync must be suppressed right now.</summary>
    /// <param name="videoActive">Whether a video is the current media (app-side: <c>PlaybackBridge.VideoActive()</c>,
    /// a derived read of the one placement state — never a standalone flag).</param>
    public static bool SyncSuppressed(bool videoActive) => videoActive;
}
