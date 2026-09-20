using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Launch restore at a saved position → endless buffering bar (0.2.9). The controller enqueues LoadFastStart → Play →
// Seek(resumePositionMs) → (later) SupplyBody onto the host's ONE serialized pump. The 0.2.9 engine's SeekAsync holds its
// replacement gate until the decoder has PCM at the target; a fast-start session owns only the ~80 KB clear head, so the
// decoder blocks in SpotifyAudioStream.WaitForBody — for the body attach that is the NEXT op in the pump, behind the seek.
// SeekGate is the pure decision the host now routes every seek through: park it until the byte source can serve it.
// FluentMediaAudioHost opens a real WASAPI device and cannot be instantiated headlessly, so the decision is pinned here
// (mirrors DeviceRecoveryPlanTests / GaplessJoinClockTests in this project).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class SeekGateTests
{
    [Fact]
    public void SessionOpen_BodyAttached_AppliesNow()
        => Assert.Equal(SeekAdmission.ApplyNow, SeekGate.Decide(hasSession: true, sourceCanServeBeyondHead: true));

    [Fact]
    public void SessionOpen_HeadOnly_Defers_TheLaunchRestoreDeadlock()
        => Assert.Equal(SeekAdmission.Defer, SeekGate.Decide(hasSession: true, sourceCanServeBeyondHead: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoSessionYet_Defers_InsteadOfDroppingTheRestoredPosition(bool sourceCanServeBeyondHead)
        => Assert.Equal(SeekAdmission.Defer, SeekGate.Decide(hasSession: false, sourceCanServeBeyondHead));

    [Fact]
    public void ReportedPosition_IsTheParkedTarget_WhileASeekIsParked()
        => Assert.Equal(225_152L, SeekGate.ReportedPositionMs(pendingSeekMs: 225_152, clockPositionMs: 0));

    [Theory]
    [InlineData(-1L, 0L, 0L)]
    [InlineData(-1L, 12_330L, 12_330L)]
    public void ReportedPosition_IsTheClock_WhenNothingIsParked(long pending, long clock, long expected)
        => Assert.Equal(expected, SeekGate.ReportedPositionMs(pending, clock));

    [Fact]
    public void ReportedPosition_ZeroTarget_IsStillAParkedSeek()
        => Assert.Equal(0L, SeekGate.ReportedPositionMs(pendingSeekMs: 0, clockPositionMs: 4_000));
}
