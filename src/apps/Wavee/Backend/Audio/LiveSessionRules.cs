using Wavee.Core;
using Wavee.Sdk.Streams;

namespace Wavee.Backend.Audio;

/// <summary>The live-session DECISIONS, extracted from <c>FluentMediaAudioHost</c> so they are unit-testable without a
/// WASAPI device, a socket or an engine session.
///
/// <para>The whole point of the live path is that two engine outcomes must be re-read. A live stream that hits
/// <c>PlaybackState.Ended</c> has NOT finished — nothing that never ends can finish — it dropped, so it must reach the
/// controller as an ERROR (which retries the same playable) rather than as Ended (which auto-advances to the next
/// queue item). And while the transport is reconnecting, the position tick must keep carrying
/// <see cref="PlaybackRecoveryKind.Network"/> or the projection resets the recovery banner on the very next tick.</para></summary>
internal static class LiveSessionRules
{
    /// <summary>A live session's <c>Ended</c> is a DROP. Reported once per session — a repeat would restart the retry
    /// ladder that the first report already armed.</summary>
    public static bool ShouldReportDropInsteadOfEnded(bool isLive, bool alreadyReported) => isLive && !alreadyReported;

    /// <summary>While live and reconnecting, the Playing tick must be the RECOVERY-carrying form. A plain tick would
    /// clear <c>RecoveryKind</c> in the projection and the "reconnecting" state would flicker off every 200 ms.</summary>
    public static bool ShouldEmitRecoveringTick(bool isLive, bool recovering) => isLive && recovering;

    /// <summary>The recovery kind a live Playing tick carries.</summary>
    public static PlaybackRecoveryKind TickRecoveryKind(bool isLive, bool recovering)
        => isLive && recovering ? PlaybackRecoveryKind.Network : PlaybackRecoveryKind.None;

    /// <summary>Whether a recovery event moves the session into "reconnecting". <c>Started</c>/<c>Attempt</c> arm it;
    /// <c>Recovered</c> clears it; <c>Exhausted</c>/<c>Cancelled</c> end the session and are handled as a drop.</summary>
    public static bool IsRecovering(AudioNetworkRecoveryStage stage) => stage is
        AudioNetworkRecoveryStage.Started or AudioNetworkRecoveryStage.Attempt;

    /// <summary>A recovery event that terminates the stream (the host reports a drop rather than waiting for Ended).</summary>
    public static bool IsTerminal(AudioNetworkRecoveryStage stage) => stage == AudioNetworkRecoveryStage.Exhausted;

    /// <summary>The typed failure a live drop reports. Network is deliberate: it is the reason the controller's existing
    /// retry ladder treats as "reload the playable", which for a live stream IS reconnect-from-scratch.</summary>
    public static AudioKeyFailureReason DropReason(Exception? error) => error switch
    {
        AudioRangeFetchException fetch => fetch.Reason.ToAudioKeyFailureReason(),
        _ => AudioKeyFailureReason.Network,
    };

    /// <summary>A live playable declares no duration, so every ending-soon / gapless / prepared-next arm must stay off.
    /// The host asserts this by passing 0 as the session duration; this is the rule stated once.</summary>
    public static long SessionDurationMs(bool isLive, long declaredMs) => isLive ? 0 : declaredMs;
}
