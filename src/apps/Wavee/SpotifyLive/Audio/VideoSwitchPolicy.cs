namespace Wavee.SpotifyLive.Audio;

/// <summary>
/// What a video load should DO to the current session, given where it stands right now. Pure and engine-free
/// (unit-tested against production code, not a mock of it — the PlacementCore/MediaSwitchLogic discipline) so
/// <c>FluentVideoMediaHost.ApplyAsync</c> can dispatch on it directly instead of re-deriving the decision inline.
/// </summary>
public enum VideoSwitchAction
{
    /// <summary>The requested source is already the live one, at the start of the edit — a genuine no-op. Doing
    /// anything here (even a seek to 0) would be a visible restart of a video the user is already watching, so the
    /// request is simply dropped.</summary>
    None,

    /// <summary>The requested source is already the live one, but the request carries an explicit start position — a
    /// deliberate reposition (a retry checkpoint, a forced same-kind reload), not a redundant re-entry. Seek the LIVE
    /// session to it; no teardown, no rebuild.</summary>
    SeekOnly,

    /// <summary>A DIFFERENT source than the one currently live, and the current session is healthy. Switch the warm,
    /// long-lived player to the new source IN PLACE (<c>MediaPlayer.OpenAsync</c> on the same instance) — no teardown,
    /// no <c>PlayerChanged(null)</c>, no surface unmount. This is the video-smooth-switching win: a video→video track
    /// skip no longer pays for a native session rebuild.</summary>
    Switch,

    /// <summary>No player exists yet (the very first video of the session), or the live one is faulted (a wedged CDM, a
    /// reported engine error, a watchdog fault). The only safe move is the ORIGINAL shape: tear the current session down
    /// to completion, then build a brand-new player and open it. Bounded, and off the UI thread either way.</summary>
    Rebuild,
}

/// <summary>The facts <see cref="VideoSwitchPolicy.Plan"/> needs to decide a load — nothing more. Carried as one struct
/// so the decision is a pure function of a snapshot, never of live, racy host state read piecemeal mid-decision.</summary>
/// <param name="HasPlayer">Does a live <c>MediaPlayer</c> already exist on the host? False only before the first load,
/// or right after a <c>Stop()</c>/teardown has cleared it.</param>
/// <param name="LiveFaulted">Is the CURRENT session known-bad (an engine error, a reported playback failure, or a start
/// watchdog fault)? A faulted session must never be switched-in-place — its native/MF state is not trustworthy enough to
/// build a new open on top of.</param>
/// <param name="LiveKey">The <c>PopOutVideoSource.Key</c> the live session was built for ("" if <see cref="HasPlayer"/>
/// is false).</param>
/// <param name="RequestKey">The <c>PopOutVideoSource.Key</c> of the source being requested.</param>
/// <param name="StartAtMs">The start position carried on the request. <c>&lt;= 0</c> means "from wherever the source
/// naturally starts" — no explicit reposition intent.</param>
public readonly record struct VideoSwitchInput(bool HasPlayer, bool LiveFaulted, string LiveKey, string RequestKey, long StartAtMs);

/// <summary>Pure decision table for a video load: given the current session's health and identity versus what is being
/// requested, what should the host do? See <see cref="VideoSwitchAction"/> for what each outcome means operationally.
/// </summary>
public static class VideoSwitchPolicy
{
    /// <summary>Decide the action for one load. Order of the checks matters: a missing or faulted player forces a
    /// <see cref="VideoSwitchAction.Rebuild"/> regardless of key (there is nothing healthy to switch-in-place onto, and
    /// nothing to seek); only once a healthy live session is established does key equality distinguish a redundant
    /// re-entry (<see cref="VideoSwitchAction.None"/>/<see cref="VideoSwitchAction.SeekOnly"/>) from an actual track
    /// change (<see cref="VideoSwitchAction.Switch"/>).</summary>
    public static VideoSwitchAction Plan(in VideoSwitchInput i) =>
        !i.HasPlayer || i.LiveFaulted ? VideoSwitchAction.Rebuild
        : string.Equals(i.LiveKey, i.RequestKey, System.StringComparison.Ordinal)
            ? (i.StartAtMs > 0 ? VideoSwitchAction.SeekOnly : VideoSwitchAction.None)
        : VideoSwitchAction.Switch;
}
