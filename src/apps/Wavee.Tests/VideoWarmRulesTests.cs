// ── Wavee.Tests/VideoWarmRulesTests.cs — the prefetch/load race and the warm keeper, pinned without a CDM ────────────
//
// Spec: docs/plans/wavee/wavee-0.3-video-engine-implementation.md §3.1.5, §6.2 G1; D15. Every fact here is over a pure
// function in `Playback/Playback.Video.Rules.cs` — values in, a value out, no engine, no socket, no timer — because the
// bug these rules exist for is a TIMING bug, and a timing bug cannot be pinned by the timing that produces it.
//
// THE BUG (measured, session sid=515080bf). A lit badge's click opened TWO protected sessions in the same millisecond:
// the prefetch's (start=203944ms paused=1) and the load's (start=203944ms paused=0). Only the second was ever destroyed
// — on a tier=Weak machine with 128 MB of shared VRAM — because the prefetch effect re-ran at the placement commit for
// the very row the load was already taking, and both `ReleasePrepared` calls ran while the prepared slot was still
// empty (the prepare landed ~176 ms later). `PrefetchRace` is the decision that cannot happen twice.
//
// Naming: a test is named after the BUG it prevents or the BUDGET it holds, never after the method it calls.

using Wavee;
using Xunit;

using V = Wavee.Playback.Video;

namespace Wavee.Tests;

// ── the prefetch/load race (§6.2 G1) ─────────────────────────────────────────────────────────────────────────────────

public class VideoPrefetchRaceTests
{
    static V.RowKey Row(string uri = "", string key = "") => new(uri, key);

    [Fact]
    public void A_row_the_pump_already_claimed_is_never_prefetched()
    {
        // Rule 1. The load IS the fetch; a prepare racing it is the second session nobody takes and nobody destroys.
        var claimed = Row("spotify:track:abc", "0123456789abcdef0123456789abcdef");
        Assert.False(V.PrefetchRace.ShouldPrefetch(in claimed, Row("spotify:track:abc", "")));
        Assert.False(V.PrefetchRace.ShouldPrefetch(in claimed, Row("", "0123456789abcdef0123456789abcdef")));
        Assert.False(V.PrefetchRace.ShouldPrefetch(in claimed, Row("spotify:track:abc", "0123456789abcdef0123456789abcdef")));
    }

    [Fact]
    public void Another_row_is_still_prefetched_while_one_is_loading()
    {
        // The rule must not turn the prefetch off wholesale: the NEXT track is exactly what should be warming while
        // this one loads.
        var claimed = Row("spotify:track:abc", "aaaa");
        Assert.True(V.PrefetchRace.ShouldPrefetch(in claimed, Row("spotify:track:xyz", "bbbb")));
        Assert.True(V.PrefetchRace.ShouldPrefetch(in V.RowKey.None, Row("spotify:track:xyz", "bbbb")));
    }

    [Fact]
    public void An_unknown_identity_is_an_absence_and_never_matches_another_unknown()
    {
        // A row whose manifest id the catalogue has not landed carries an EMPTY key. Two of those are not "the same
        // row" — treating them as equal would make one unrelated load suppress every prefetch in the session.
        Assert.False(V.PrefetchRace.Same(Row("spotify:track:abc", ""), Row("spotify:track:xyz", "")));
        Assert.False(V.PrefetchRace.Same(in V.RowKey.None, in V.RowKey.None));
        Assert.True(V.RowKey.None.IsNone);
        // …and a row with no identity at all is not something to prefetch either.
        Assert.False(V.PrefetchRace.ShouldPrefetch(in V.RowKey.None, in V.RowKey.None));
    }

    [Fact]
    public void Either_half_of_the_identity_is_enough_to_recognise_the_same_row()
    {
        // The prefetch names a row before its key exists; the load names a key whose row the pump was never told.
        Assert.True(V.PrefetchRace.Same(Row("spotify:track:abc", ""), Row("spotify:track:abc", "aaaa")));
        Assert.True(V.PrefetchRace.Same(Row("", "aaaa"), Row("spotify:track:abc", "aaaa")));
        Assert.False(V.PrefetchRace.Same(Row("spotify:track:abc", "aaaa"), Row("spotify:track:xyz", "bbbb")));
    }

    [Fact]
    public void A_load_adopts_the_prepare_fetched_for_its_own_row()
    {
        // Rule 2, the half that turns the wasted session into the fast one: the load waits for the backend to have
        // registered the prepare and then opens straight onto it.
        var loading = Row("spotify:track:abc", "aaaa");
        Assert.Equal(V.PrepareFate.Adopt, V.PrefetchRace.FateOf(true, Row("spotify:track:abc", ""), in loading));
        Assert.Equal(V.PrepareFate.Adopt, V.PrefetchRace.FateOf(true, Row("", "aaaa"), in loading));
    }

    [Fact]
    public void A_prepare_aimed_anywhere_else_is_dropped_and_never_left_to_land_unowned()
    {
        // Rule 2's other half, and the whole no-stranded-session guarantee: Adopt and Drop are TOTAL over "a prepare is
        // in flight", so there is no third outcome in which a session stays alive with no owner.
        var loading = Row("spotify:track:abc", "aaaa");
        Assert.Equal(V.PrepareFate.Drop, V.PrefetchRace.FateOf(true, Row("spotify:track:xyz", "bbbb"), in loading));
        Assert.Equal(V.PrepareFate.Idle, V.PrefetchRace.FateOf(false, Row("spotify:track:abc", "aaaa"), in loading));
        Assert.Equal(V.PrepareFate.Idle, V.PrefetchRace.FateOf(false, in V.RowKey.None, in loading));
    }
}

// ── the warm keeper (D15) ────────────────────────────────────────────────────────────────────────────────────────────

public class VideoWarmPolicyTests
{
    [Fact]
    public void The_keeper_beats_only_while_a_surface_is_wanted_and_the_user_left_it_on()
    {
        Assert.True(V.WarmPolicy.Beats(videoOn: true, prepareAhead: true));
        Assert.False(V.WarmPolicy.Beats(videoOn: false, prepareAhead: true));
        Assert.False(V.WarmPolicy.Beats(videoOn: true, prepareAhead: false));
    }

    [Fact]
    public void D15_the_runtime_is_let_go_thirty_seconds_after_the_surface_closed_and_not_before()
    {
        Assert.False(V.WarmPolicy.Sheds(videoOn: false, prepareAhead: true, msSinceOff: 0));
        Assert.False(V.WarmPolicy.Sheds(videoOn: false, prepareAhead: true, msSinceOff: V.WarmPolicy.ShedMs - 1));
        Assert.True(V.WarmPolicy.Sheds(videoOn: false, prepareAhead: true, msSinceOff: V.WarmPolicy.ShedMs));
        Assert.Equal(30_000, V.WarmPolicy.ShedMs);   // the engine's own ProtectedVideoRuntime.WarmIdleDisposeMs
    }

    [Fact]
    public void D15_nothing_sheds_while_a_video_surface_is_still_showing()
    {
        Assert.False(V.WarmPolicy.Sheds(videoOn: true, prepareAhead: true, msSinceOff: 0));
        Assert.False(V.WarmPolicy.Sheds(videoOn: true, prepareAhead: true, msSinceOff: 10 * V.WarmPolicy.ShedMs));
    }

    [Fact]
    public void Turning_the_setting_off_lets_go_at_once_rather_than_after_the_window()
    {
        // The switch is an OFF switch, not a slower one: the user who turns it off must stop paying for licences of
        // videos they may never watch on the very next beat.
        Assert.True(V.WarmPolicy.Sheds(videoOn: true, prepareAhead: false, msSinceOff: 0));
        Assert.True(V.WarmPolicy.Sheds(videoOn: false, prepareAhead: false, msSinceOff: 0));
    }

    [Fact]
    public void A_runtime_that_shed_is_back_within_one_beat()
    {
        // The beat must be shorter than the window it is watching, or the keeper only ever notices a dead runtime after
        // the user has already paid the 521 ms create on their switch.
        Assert.True(V.WarmPolicy.HeartbeatMs > 0);
        Assert.True(V.WarmPolicy.HeartbeatMs < V.WarmPolicy.ShedMs);
    }

    [Fact]
    public void Audio_always_wins_a_licence_is_never_fetched_while_a_track_is_opening()
    {
        Assert.False(V.WarmPolicy.Acquires(videoOn: true, prepareAhead: true, audioBusy: true, keyInHand: false));
        Assert.True(V.WarmPolicy.Acquires(videoOn: true, prepareAhead: true, audioBusy: false, keyInHand: false));
    }

    [Fact]
    public void A_key_already_in_hand_is_never_asked_for_a_second_time()
    {
        // Two challenges for one KID create a second content binding whose ITA proxy the CDM rejects — so "already
        // usable" and "already in flight" are both "in hand".
        Assert.False(V.WarmPolicy.Acquires(videoOn: true, prepareAhead: true, audioBusy: false, keyInHand: true));
    }

    [Fact]
    public void The_setting_gates_pre_acquisition_and_nothing_else()
    {
        // Off: no beat, no key. The native preload (Playback.Video.Boot) is deliberately NOT under this switch — it is
        // the warm the sentence promises is free.
        Assert.False(V.WarmPolicy.Acquires(videoOn: true, prepareAhead: false, audioBusy: false, keyInHand: false));
        Assert.True(V.PrepareAhead.Default);
        Assert.Equal("playback.video.prepareAhead", V.PrepareAhead.Name);
    }

    [Fact]
    public void Adopting_a_prepare_never_costs_more_than_the_cold_open_it_replaces()
    {
        // The load waits for the backend's REGISTRATION, not for the download; the fallback is the cold open it would
        // have done anyway, so the budget only has to stay under the cold switch it is betting against.
        Assert.True(V.WarmPolicy.AdoptBudgetMs > V.Budgets.WarmSwitchAfterIdleMs);
        Assert.True(V.WarmPolicy.AdoptBudgetMs < V.WarmPolicy.ShedMs);
    }
}
