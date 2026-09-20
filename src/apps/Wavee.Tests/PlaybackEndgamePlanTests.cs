// ── Wavee.Tests/PlaybackEndgamePlanTests.cs — the endgame's per-tick verdict, without a session or a reducer ────────
//
// `EndgamePlan.Decide` (Playback/Playback.Endgame.cs) replaced the once-per-load `s_gaplessArmed`/`s_endingSoonSent`
// latches in `Playback.Audio.Tick`: the diagnosed bug was an endgame that asked the reducer once, got nothing back
// (a stalled re-seed left nothing to prepare), and never asked again for the rest of the track. These pin the fix —
// re-asking every ~3 s while the window stays open with nothing prepared and nothing in flight — plus the arm-once
// and commit-suppresses-ask rules it must not regress. PURE: no session, no reducer, no clock but the one passed in.

using Wavee;

using Xunit;

namespace Wavee.Tests;

public class PlaybackEndgamePlanTests
{
    const long Dur = 200_000;

    [Fact]
    public void Far_from_the_end_nothing_happens()
    {
        var plan = Playback.Audio.EndgamePlan.Decide(positionMs: 100_000, durationMs: Dur, fadeMs: 0,
            prepared: false, overlapAllowed: true, handOffInFlight: false,
            armLogged: false, lastAskMs: -1, nowMs: 0);

        Assert.Equal(Playback.Audio.EndgameAction.Wait, plan.Action);
        Assert.False(plan.LogArm);
        Assert.Equal(100_000, plan.RemainMs);
    }

    [Fact]
    public void The_first_tick_inside_the_ask_window_with_nothing_prepared_asks()
    {
        // EndingSoonMs(0, Dur) = 8_000: the window opens 8 s out.
        var plan = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 8_000, durationMs: Dur, fadeMs: 0,
            prepared: false, overlapAllowed: true, handOffInFlight: false,
            armLogged: false, lastAskMs: -1, nowMs: 0);

        Assert.Equal(Playback.Audio.EndgameAction.Ask, plan.Action);
        Assert.Equal(8_000, plan.RemainMs);
    }

    [Fact]
    public void It_does_not_re_ask_before_the_interval_elapses()
    {
        // Asked at nowMs=1_000; only 500 ms later, still inside the default 3_000 ms interval.
        var plan = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 7_500, durationMs: Dur, fadeMs: 0,
            prepared: false, overlapAllowed: true, handOffInFlight: false,
            armLogged: true, lastAskMs: 1_000, nowMs: 1_500);

        Assert.Equal(Playback.Audio.EndgameAction.Wait, plan.Action);
    }

    [Fact]
    public void It_re_asks_once_the_interval_elapses_with_still_nothing_prepared()
    {
        // THE FIX: the old one-shot latch asked once and never again. Here nothing has changed except time — the
        // same unprepared endgame gets nudged a second time.
        var plan = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 4_000, durationMs: Dur, fadeMs: 0,
            prepared: false, overlapAllowed: true, handOffInFlight: false,
            armLogged: true, lastAskMs: 1_000, nowMs: 4_001);

        Assert.Equal(Playback.Audio.EndgameAction.Ask, plan.Action);
    }

    [Fact]
    public void It_keeps_re_asking_indefinitely_while_the_window_stays_open_and_nothing_resolves()
    {
        // Simulate several 3 s intervals in a row: every one asks again — never a single silent latch for the rest
        // of the track, which is exactly the reported bug.
        long lastAsk = -1;
        int asks = 0;
        for (long now = 0; now < 20_000; now += 3_000)
        {
            var plan = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 8_000, durationMs: Dur, fadeMs: 0,
                prepared: false, overlapAllowed: true, handOffInFlight: false,
                armLogged: true, lastAskMs: lastAsk, nowMs: now);
            if (plan.Action == Playback.Audio.EndgameAction.Ask) { asks++; lastAsk = now; }
        }
        Assert.True(asks >= 5, $"expected repeated re-asks over 20 s at a 3 s interval, got {asks}");
    }

    [Fact]
    public void It_stops_asking_once_something_is_prepared()
    {
        var plan = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 7_000, durationMs: Dur, fadeMs: 0,
            prepared: true, overlapAllowed: true, handOffInFlight: false,
            armLogged: true, lastAskMs: -1, nowMs: 0);

        Assert.Equal(Playback.Audio.EndgameAction.Wait, plan.Action);
    }

    [Fact]
    public void A_hand_off_already_in_flight_never_arms_or_asks()
    {
        var plan = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 500, durationMs: Dur, fadeMs: 0,
            prepared: false, overlapAllowed: true, handOffInFlight: true,
            armLogged: false, lastAskMs: -1, nowMs: 0);

        Assert.Equal(Playback.Audio.EndgameAction.Wait, plan.Action);
        Assert.False(plan.LogArm);
    }

    [Fact]
    public void The_arm_line_logs_once_per_track_and_the_caller_s_own_flag_silences_it_after()
    {
        // ArmLeadMs(0) = 2_000: the arm window is well inside the (larger) ask window.
        var first = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 2_000, durationMs: Dur, fadeMs: 0,
            prepared: false, overlapAllowed: true, handOffInFlight: false,
            armLogged: false, lastAskMs: -1, nowMs: 0);
        Assert.True(first.LogArm);

        // The caller latches its own `armLogged` after seeing `LogArm`; a second tick with that flag set gets none.
        var second = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 1_900, durationMs: Dur, fadeMs: 0,
            prepared: false, overlapAllowed: true, handOffInFlight: false,
            armLogged: true, lastAskMs: -1, nowMs: 200);
        Assert.False(second.LogArm);
    }

    [Fact]
    public void A_due_hand_off_commits_instead_of_asking_and_still_owes_the_arm_line()
    {
        // Gapless (fadeMs 0) commits inside GaplessCommitLeadMs (1_500 ms) of the end.
        var plan = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 1_000, durationMs: Dur, fadeMs: 0,
            prepared: true, overlapAllowed: true, handOffInFlight: false,
            armLogged: false, lastAskMs: -1, nowMs: 0);

        Assert.Equal(Playback.Audio.EndgameAction.Commit, plan.Action);
        Assert.Equal(Playback.Audio.HandOff.Gapless, plan.HandOff);
        Assert.True(plan.LogArm);
    }

    [Fact]
    public void A_long_crossfade_arms_on_the_same_tick_it_commits()
    {
        // fade 8.5 s: the arm lead (max(fade, 2 s)) and the crossfade window are the same instant.
        var plan = Playback.Audio.EndgamePlan.Decide(positionMs: Dur - 8_500, durationMs: Dur, fadeMs: 8_500,
            prepared: true, overlapAllowed: true, handOffInFlight: false,
            armLogged: false, lastAskMs: -1, nowMs: 0);

        Assert.Equal(Playback.Audio.EndgameAction.Commit, plan.Action);
        Assert.True(plan.LogArm);
    }

    [Fact]
    public void An_unknown_duration_never_arms_asks_or_commits()
        // A live stream has no end to approach.
        => Assert.Equal(Playback.Audio.EndgameAction.Wait,
            Playback.Audio.EndgamePlan.Decide(positionMs: 600_000, durationMs: 0, fadeMs: 0,
                prepared: false, overlapAllowed: true, handOffInFlight: false,
                armLogged: false, lastAskMs: -1, nowMs: 0).Action);
}
