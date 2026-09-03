using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// GaplessJoinClock is the pure half of the device-reopen gapless bug (issue A1/A2): a mid-track output-device/format
// change tears down and rebuilds the PcmAudioSession, resetting its sample clock to 0 — but the OLD code computed the
// active track's join frame ONCE, track-absolute, at open time, and never revisited it on reopen or on seek. The real
// captured failure: a track opened at 48 kHz, the device switched to 44.1 kHz ~52 s before the end, and the eventual
// commit scheduled join=9486174 against a post-reopen clock of only 2277600 — 163.5 s in the "future", so the next
// track's join never crossed until the OLD estimate finally lapsed 164 s late. FluentMediaAudioHost opens a real WASAPI
// device and cannot be instantiated headlessly, so this frame-domain arithmetic is pinned here instead (mirrors
// XrunLogLineTests/PlayIntentGateTests in this same project).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class GaplessJoinClockTests
{
    [Fact]
    public void JoinFrameFor_FreshOpen_IsDurationFrames()
    {
        // A fresh session's clock is 0 and the playhead is 0 — the join frame is simply the whole track's duration,
        // in frames, at the session's rate (OpenSessionAsync's steady-state case).
        long join = GaplessJoinClock.JoinFrameFor(sampleClockNow: 0, durationMs: 199_302, playheadMs: 0, rate: 44_100);
        Assert.Equal(GaplessJoinClock.MsToFrames(199_302, 44_100), join);
    }

    [Fact]
    public void JoinFrameFor_AfterReopenAtPlayhead_IsRemainingFramesFromClockNow()
    {
        // The exact numbers from the captured log: a device-format soft reload lands the session at clock=2277600
        // (frames consumed since THIS session was built, not since the track started), the track is 215100 ms long,
        // and the restored playhead is 163500 ms in — i.e. 51600 ms remain. The join frame must be clock-RELATIVE:
        // clock now + the remaining time in frames, never the track-absolute frame the old code used (root cause A1).
        const long clock = 2_277_600;
        const long durationMs = 215_100;
        const long playheadMs = 163_500;
        const int rate = 44_100;

        long join = GaplessJoinClock.JoinFrameFor(clock, durationMs, playheadMs, rate);

        Assert.Equal(clock + 2_275_560, join);   // 2277600 + 2275560 — the 51600 ms remainder in frames at 44.1 kHz
    }

    [Fact]
    public void ScheduleJoin_NeverInThePast()
    {
        // A commit that lands AFTER the estimated join frame (a late Tick, or a stale _activeJoinFrame left over from
        // before a reopen) must schedule B at the CURRENT clock, never behind it — a negative-offset join would be
        // nonsensical to the mixer.
        long join = GaplessJoinClock.ScheduleJoin(activeJoinFrame: 1_000, sampleClockNow: 5_000, remainingMs: 500, rate: 44_100);
        Assert.Equal(5_000, join);
    }

    [Fact]
    public void ScheduleJoin_BoundsAStaleEstimateToRemainingPlus100ms()
    {
        // The exact failure mode: _activeJoinFrame is stale (a track-absolute frame computed before a reopen reset the
        // clock to 0), landing far in the "future" relative to the live clock — here mirroring the log's own
        // remaining-ms figure (arm remainMs=1966) at 44.1 kHz. ScheduleJoin must degrade this to a bounded near-term
        // join — remaining-time frames + 100 ms of slack (rate/10) — instead of honouring the stale estimate and
        // stalling the hand-off for minutes (the reported "advance 164 s late").
        const long clock = 2_277_600;
        const long staleActiveJoinFrame = clock + 50_000_000;   // hopelessly far in the "future" post-reopen
        const long remainingMs = 1_966;
        const int rate = 44_100;

        long join = GaplessJoinClock.ScheduleJoin(staleActiveJoinFrame, clock, remainingMs, rate);

        long bound = clock + GaplessJoinClock.MsToFrames(remainingMs, rate) + rate / 10;
        Assert.Equal(bound, join);
        Assert.True(join - clock <= GaplessJoinClock.MsToFrames(remainingMs, rate) + rate / 10);
    }

    [Fact]
    public void PrimedSlotMatches_RateMismatch_False()
    {
        // A2: a prepared voice decoded/resampled at 48 kHz cannot be spliced into a 44.1 kHz session without playing
        // ~8.8% slow (48000/44100) — the exact ratio observed in the captured bug.
        Assert.False(GaplessJoinClock.PrimedSlotMatches(primedMixRate: 48_000, sessionRate: 44_100));
        Assert.True(GaplessJoinClock.PrimedSlotMatches(primedMixRate: 44_100, sessionRate: 44_100));
    }
}
