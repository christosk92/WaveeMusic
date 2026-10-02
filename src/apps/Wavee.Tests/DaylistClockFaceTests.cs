// ── Wavee.Tests/DaylistClockFaceTests.cs — which face the daylist clock wears ───────────────────────────────────────
//
// Pure: the window's phase and the rollover ladder's state go in, the face comes out. No clock, no signals.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DaylistClockFaceTests
{
    static readonly DaylistRolloverState[] AllLadders =
        [DaylistRolloverState.Idle, DaylistRolloverState.Waiting, DaylistRolloverState.Fetching, DaylistRolloverState.Exhausted];

    [Fact]
    public void An_open_window_counts_whatever_the_ladder_says()
    {
        foreach (var ladder in AllLadders)
            Assert.Equal(DaylistClockFace.Face.Counting, DaylistClockFace.Of(DaylistCountdown.Phase.Counting, ladder));
    }

    [Fact]
    public void No_window_counts_too()
    {
        // Idle phase = no window held: the clock itself renders nothing, but the rule never calls it Updating or Late.
        foreach (var ladder in AllLadders)
            Assert.Equal(DaylistClockFace.Face.Counting, DaylistClockFace.Of(DaylistCountdown.Phase.Idle, ladder));
    }

    [Theory]
    [InlineData(DaylistRolloverState.Waiting)]
    [InlineData(DaylistRolloverState.Fetching)]
    public void An_ended_window_with_the_ladder_running_is_updating(DaylistRolloverState ladder)
    {
        Assert.Equal(DaylistClockFace.Face.Updating, DaylistClockFace.Of(DaylistCountdown.Phase.Rolling, ladder));
    }

    [Fact]
    public void An_ended_window_with_the_ladder_spent_is_late()
    {
        Assert.Equal(DaylistClockFace.Face.Late, DaylistClockFace.Of(DaylistCountdown.Phase.Rolling, DaylistRolloverState.Exhausted));
    }

    [Fact]
    public void An_ended_window_with_nothing_scheduled_is_late_not_an_eternal_spinner()
    {
        // Offline, a demo scope, or a card the feed host does not track: nothing is on its way.
        Assert.Equal(DaylistClockFace.Face.Late, DaylistClockFace.Of(DaylistCountdown.Phase.Rolling, DaylistRolloverState.Idle));
    }

    [Fact]
    public void The_face_follows_the_clock_through_a_whole_rollover()
    {
        long expiresAt = 10_000;
        long grace = DaylistRollover.GraceMs;
        var before = DaylistClockFace.Of(DaylistCountdown.PhaseOf(expiresAt, 9_999), DaylistRolloverState.Waiting);
        var justAfter = DaylistClockFace.Of(DaylistCountdown.PhaseOf(expiresAt, 10_000), DaylistRolloverState.Waiting);
        var afterGrace = DaylistClockFace.Of(DaylistCountdown.PhaseOf(expiresAt, expiresAt + grace), DaylistRolloverState.Fetching);
        var spent = DaylistClockFace.Of(DaylistCountdown.PhaseOf(expiresAt, expiresAt + 600_000), DaylistRolloverState.Exhausted);

        Assert.Equal(DaylistClockFace.Face.Counting, before);
        Assert.Equal(DaylistClockFace.Face.Updating, justAfter);
        Assert.Equal(DaylistClockFace.Face.Updating, afterGrace);
        Assert.Equal(DaylistClockFace.Face.Late, spent);
    }
}
