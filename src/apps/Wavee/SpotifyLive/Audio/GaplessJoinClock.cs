using System;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Frame-domain arithmetic for the gapless join, in ONE place (device-reopen gapless bug, issue A). The
/// session's <c>SampleClock</c> is frames consumed since THAT <c>PcmAudioSession</c> was built (a seek rebases the
/// position clock, never the sample clock; a device-format soft reload builds a NEW session at clock 0 and seeks it to
/// the saved playhead). Every writer of the active track's natural-end frame therefore expresses it as "clock now +
/// frames still to play", never as "frames from track start" — the old <c>OpenSessionAsync</c> computed a
/// track-absolute join frame once at open and never revisited it, so a mid-track reopen scheduled the join hundreds of
/// seconds in the future (root cause A1: <c>arm remainMs=1966 … clock=2251680</c>, <c>join=9486174</c>).
/// <para>Split out of <c>FluentMediaAudioHost</c> — mirrors <c>XrunLogLine</c>/<c>PlayIntentGate</c> in this same
/// folder — purely so this arithmetic has a name and a test rather than living inline at four call sites
/// (<c>OpenSessionAsync</c>, <c>Seek</c>, <c>SoftReloadAsync</c>, <c>CommitGaplessJoin</c>).</para></summary>
internal static class GaplessJoinClock
{
    public static long MsToFrames(long ms, int rate) => ms * rate / 1000L;

    /// <summary>The active track's natural-end frame, on the session clock, given where the playhead is right now.</summary>
    public static long JoinFrameFor(long sampleClockNow, long durationMs, long playheadMs, int rate)
        => sampleClockNow + MsToFrames(Math.Max(0L, durationMs - playheadMs), rate);

    /// <summary>Where to start the next voice: never in the past, and never further out than the active track's own
    /// remaining time + 100 ms — a stale estimate degrades to a ≤100 ms butt-join instead of a 164 s stall.</summary>
    public static long ScheduleJoin(long activeJoinFrame, long sampleClockNow, long remainingMs, int rate)
    {
        long join = Math.Max(activeJoinFrame, sampleClockNow);
        long bound = sampleClockNow + MsToFrames(Math.Max(0L, remainingMs), rate) + rate / 10;
        return Math.Min(join, bound);
    }

    /// <summary>A primed voice is only spliceable into a mixer running at the rate it was resampled for.</summary>
    public static bool PrimedSlotMatches(int primedMixRate, int sessionRate) => primedMixRate == sessionRate;
}
