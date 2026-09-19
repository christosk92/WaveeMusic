namespace Wavee.SpotifyLive.Audio;

/// <summary>The buffering-bar-on-a-paused-restored-track fix's host-side half. <c>FluentMediaAudioHost</c> cannot be
/// instantiated headlessly (it opens a real WASAPI device), so this trivial decision is split out — mirroring
/// <c>XrunLogLine</c>/<c>VideoSwitchPolicy</c> in this same folder — purely so it has a name and a test rather than
/// living as an inline <c>if</c> at three call sites.
///
/// <para>The bug: a launch-recovery snapshot restore loads the current track PAUSED (<c>initiallyPaused: true</c>,
/// <c>Play()</c> never called) so the player can show it sitting at its saved position. <c>FluentMediaAudioHost</c>'s
/// own <c>LoadFastStart</c>/<c>SupplyBodyAsync</c> used to announce <c>Prebuffering</c>/<c>Buffering</c> unconditionally
/// while attaching the clear head / encrypted body — work the host does regardless of whether anyone asked to HEAR
/// it. With no <c>Play()</c> ever called, the state-pump ticker that would normally retire the flag on the next
/// Playing/Ended edge never starts, so the player bar's indeterminate progress bar latched on a track that was
/// merely paused at its restored position, until the user pressed Play.</para></summary>
public static class PlayIntentGate
{
    /// <summary>A load nobody asked to hear (no <see cref="Wavee.Backend.IAudioHost.PlayIntent"/>) announces nothing —
    /// buffering while attaching is expected work, not a state the UI needs to show. Once there IS play intent, every
    /// buffering signal is real and must reach the projection exactly as before.</summary>
    public static bool ShouldAnnounceBuffering(bool playIntent) => playIntent;
}
