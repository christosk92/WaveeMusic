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

// ── superseded loads (F155) and the adopt timeout (F160) ─────────────────────────────────────────────────────────────

public class VideoLoadSupersessionTests
{
    static V.RowKey Row(string uri = "", string key = "") => new(uri, key);

    [Fact]
    public void A_request_is_current_only_while_neither_the_pump_nor_the_reducer_moved_past_it()
    {
        Assert.False(V.LoadSupersession.IsStale(pumpEpoch: 4, requestPumpEpoch: 4, requestReducerEpoch: 9, latestReducerEpoch: 9));
        Assert.True(V.LoadSupersession.IsStale(pumpEpoch: 5, requestPumpEpoch: 4, requestReducerEpoch: 9, latestReducerEpoch: 9));
    }

    [Fact]
    public void A_load_the_reducer_already_replaced_is_stale_before_the_pump_epoch_moves()
    {
        // The window F155 lives in: B's resolve is still on the api pool, so Video.Load(B) has not bumped the pump epoch,
        // but the reducer started B (epoch 10) and the worker is holding A (epoch 9).
        Assert.True(V.LoadSupersession.IsStale(pumpEpoch: 4, requestPumpEpoch: 4, requestReducerEpoch: 9, latestReducerEpoch: 10));
    }

    [Fact]
    public void A_stale_request_never_condemns_the_prepare_of_the_row_that_replaced_it()
    {
        // B's prefetch is in flight (armed while B was Loading); the stale A reaches ResolvePrepareAsync. A current A would
        // Drop it; a stale one must leave it alone, or Landed disposes B's warm prepare and B opens cold.
        var preparing = Row("spotify:track:bbb", "");
        var loading = Row("spotify:track:aaa", "aaaa");
        Assert.Equal(V.PrepareFate.Drop, V.LoadSupersession.FateOf(false, true, in preparing, in loading));
        Assert.Equal(V.PrepareFate.Idle, V.LoadSupersession.FateOf(true, true, in preparing, in loading));
    }

    [Fact]
    public void A_stale_request_does_not_wait_to_adopt_a_prepare()
    {
        var preparing = Row("spotify:track:aaa", "");
        var loading = Row("spotify:track:aaa", "aaaa");
        Assert.Equal(V.PrepareFate.Adopt, V.LoadSupersession.FateOf(false, true, in preparing, in loading));
        Assert.Equal(V.PrepareFate.Idle, V.LoadSupersession.FateOf(true, true, in preparing, in loading));
    }

    [Fact]
    public void A_current_request_keeps_the_race_rules_exactly()
    {
        var loading = Row("spotify:track:aaa", "aaaa");
        Assert.Equal(V.PrepareFate.Idle, V.LoadSupersession.FateOf(false, false, in V.RowKey.None, in loading));
    }

    [Fact]
    public void An_adopt_that_timed_out_condemns_the_prepare_still_in_the_slot_so_it_lands_disposed()
    {
        // F160: the load opened cold; the late prepare would be a second protected session for the same row, parked for nobody.
        Assert.Equal(V.PrepareFate.Drop, V.LoadSupersession.FateAfterAdoptTimeout(slotStillHoldsThisPrepare: true));
    }

    [Fact]
    public void An_adopt_timeout_never_condemns_a_prepare_that_already_landed_or_a_newer_one()
    {
        Assert.Equal(V.PrepareFate.Idle, V.LoadSupersession.FateAfterAdoptTimeout(slotStillHoldsThisPrepare: false));
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

// ── what the keeper pins, warms on intent and leaves alone (F165, F218, F225, F167) ──────────────────────────────────

public class VideoWarmTargetTests
{
    static V.WarmTarget Clear => V.WarmTarget.Clear;
    static V.WarmTarget Unknown => V.WarmTarget.Unknown;
    static V.WarmTarget Drm => V.WarmTarget.Drm;

    [Fact]
    public void F165_a_target_is_clear_unknown_or_protected_by_what_the_keeper_learnt_of_its_row()
    {
        Assert.Equal(Clear, V.WarmPolicy.TargetOf(wanted: false, tried: false, drm: false));
        Assert.Equal(Clear, V.WarmPolicy.TargetOf(wanted: false, tried: true, drm: true));     // a row nobody wants is no target
        Assert.Equal(Unknown, V.WarmPolicy.TargetOf(wanted: true, tried: false, drm: false));
        Assert.Equal(Clear, V.WarmPolicy.TargetOf(wanted: true, tried: true, drm: false));     // resolved: no KID, nothing protected
        Assert.Equal(Drm, V.WarmPolicy.TargetOf(wanted: true, tried: true, drm: true));
    }

    [Fact]
    public void F165_songs_without_protected_video_never_pin_the_runtime()
    {
        // The user who leaves the surface on and plays songs: no D3D11 device, media engine, CDM or PMP process for them.
        Assert.False(V.WarmPolicy.HoldsRuntime(Clear, Clear, protectedLive: false, alreadyHeld: false));
        Assert.False(V.WarmPolicy.HoldsRuntime(Unknown, Clear, protectedLive: false, alreadyHeld: false));   // never on speculation
        Assert.False(V.WarmPolicy.HoldsRuntime(Unknown, Unknown, protectedLive: false, alreadyHeld: false));
    }

    [Fact]
    public void F165_a_protected_target_or_a_live_protected_session_pins_it()
    {
        Assert.True(V.WarmPolicy.HoldsRuntime(Drm, Clear, protectedLive: false, alreadyHeld: false));
        Assert.True(V.WarmPolicy.HoldsRuntime(Clear, Drm, protectedLive: false, alreadyHeld: false));
        Assert.True(V.WarmPolicy.HoldsRuntime(Clear, Clear, protectedLive: true, alreadyHeld: false));
    }

    [Fact]
    public void F165_the_pin_is_let_go_once_both_targets_are_clear_and_kept_while_one_is_still_resolving()
    {
        Assert.False(V.WarmPolicy.HoldsRuntime(Clear, Clear, protectedLive: false, alreadyHeld: true));
        // A row change leaves the new row Unknown for one resolve: dropping and re-taking would be churn for nothing.
        Assert.True(V.WarmPolicy.HoldsRuntime(Unknown, Clear, protectedLive: false, alreadyHeld: true));
        Assert.True(V.WarmPolicy.HoldsRuntime(Clear, Unknown, protectedLive: false, alreadyHeld: true));
    }

    [Fact]
    public void F165_a_clear_video_on_the_host_lets_the_pin_go_because_the_live_source_is_the_truth()
    {
        // The keeper never resolves the row the host owns, so its memory (tried: false) would read Unknown for as long as
        // it plays and, once held, pin the runtime for a clear video. The live source says what it is.
        var current = V.WarmPolicy.CurrentTargetOf(hostOwns: true, liveKnown: true, liveDrm: false, wanted: true, tried: false, drm: false);
        Assert.Equal(Clear, current);
        Assert.False(V.WarmPolicy.HoldsRuntime(current, Clear, protectedLive: false, alreadyHeld: true));
    }

    [Fact]
    public void F165_a_host_row_still_loading_is_unknown_and_a_protected_one_is_protected()
    {
        Assert.Equal(Unknown, V.WarmPolicy.CurrentTargetOf(hostOwns: true, liveKnown: false, liveDrm: false, wanted: true, tried: false, drm: false));
        Assert.Equal(Drm, V.WarmPolicy.CurrentTargetOf(hostOwns: true, liveKnown: true, liveDrm: true, wanted: true, tried: false, drm: false));
    }

    [Fact]
    public void F165_a_row_the_host_does_not_own_is_judged_by_the_keepers_memory()
    {
        Assert.Equal(Unknown, V.WarmPolicy.CurrentTargetOf(hostOwns: false, liveKnown: true, liveDrm: false, wanted: true, tried: false, drm: false));
        Assert.Equal(Drm, V.WarmPolicy.CurrentTargetOf(hostOwns: false, liveKnown: false, liveDrm: false, wanted: true, tried: true, drm: true));
        Assert.Equal(Clear, V.WarmPolicy.CurrentTargetOf(hostOwns: true, liveKnown: true, liveDrm: true, wanted: false, tried: false, drm: false));   // no row, no target
    }

    [Fact]
    public void F218_the_surface_off_after_a_video_was_asked_for_warms_the_playing_row_on_intent()
    {
        Assert.True(V.WarmPolicy.WarmsOnIntent(videoOn: false, prepareAhead: true, videoUsed: true, currentHasVideo: true));
        Assert.False(V.WarmPolicy.WarmsOnIntent(videoOn: true, prepareAhead: true, videoUsed: true, currentHasVideo: true));    // the beat itself
        Assert.False(V.WarmPolicy.WarmsOnIntent(videoOn: false, prepareAhead: false, videoUsed: true, currentHasVideo: true));  // the setting is the off switch
        Assert.False(V.WarmPolicy.WarmsOnIntent(videoOn: false, prepareAhead: true, videoUsed: false, currentHasVideo: true));  // never asked for a video
        Assert.False(V.WarmPolicy.WarmsOnIntent(videoOn: false, prepareAhead: true, videoUsed: true, currentHasVideo: false));  // a song with no video
    }

    [Fact]
    public void F218_the_intent_beat_keeps_audio_first_and_never_asks_for_a_key_twice()
    {
        Assert.True(V.WarmPolicy.AcquiresOnIntent(intent: true, audioBusy: false, keyInHand: false));
        Assert.False(V.WarmPolicy.AcquiresOnIntent(intent: true, audioBusy: true, keyInHand: false));
        Assert.False(V.WarmPolicy.AcquiresOnIntent(intent: true, audioBusy: false, keyInHand: true));
        Assert.False(V.WarmPolicy.AcquiresOnIntent(intent: false, audioBusy: false, keyInHand: false));
    }

    [Fact]
    public void F225_a_row_the_video_host_owns_is_not_re_acquired_unless_its_key_expired()
    {
        // Its own switch acquired the key: an untried row, or a lost cache, is not the keeper's business.
        Assert.False(V.WarmPolicy.NeedsKey(expiryOnly: true, tried: false, kidKnown: false, keyLost: false, keyExpired: false));
        Assert.False(V.WarmPolicy.NeedsKey(expiryOnly: true, tried: true, kidKnown: true, keyLost: true, keyExpired: false));
        Assert.True(V.WarmPolicy.NeedsKey(expiryOnly: true, tried: true, kidKnown: true, keyLost: false, keyExpired: true));
    }

    [Fact]
    public void The_key_rules_for_a_row_the_host_does_not_own_are_unchanged()
    {
        Assert.True(V.WarmPolicy.NeedsKey(expiryOnly: false, tried: false, kidKnown: false, keyLost: false, keyExpired: false));
        Assert.False(V.WarmPolicy.NeedsKey(expiryOnly: false, tried: true, kidKnown: false, keyLost: false, keyExpired: false));   // no KID: no protected video
        Assert.False(V.WarmPolicy.NeedsKey(expiryOnly: false, tried: true, kidKnown: true, keyLost: false, keyExpired: false));    // usable, pending or failed
        Assert.True(V.WarmPolicy.NeedsKey(expiryOnly: false, tried: true, kidKnown: true, keyLost: true, keyExpired: false));      // the runtime shed
        Assert.True(V.WarmPolicy.NeedsKey(expiryOnly: false, tried: true, kidKnown: true, keyLost: false, keyExpired: true));
    }

    [Fact]
    public void F225_the_next_rows_challenge_waits_past_the_first_frame_of_the_session_being_watched()
    {
        Assert.True(V.WarmPolicy.NextMayStart(hostOwns: false, sinceFirstFrameMs: -1));
        Assert.False(V.WarmPolicy.NextMayStart(hostOwns: true, sinceFirstFrameMs: -1));     // no first frame yet
        Assert.False(V.WarmPolicy.NextMayStart(hostOwns: true, sinceFirstFrameMs: 0));
        Assert.False(V.WarmPolicy.NextMayStart(hostOwns: true, sinceFirstFrameMs: V.WarmPolicy.NextAfterFirstFrameMs - 1));
        Assert.True(V.WarmPolicy.NextMayStart(hostOwns: true, sinceFirstFrameMs: V.WarmPolicy.NextAfterFirstFrameMs));
        Assert.True(V.WarmPolicy.NextAfterFirstFrameMs < V.WarmPolicy.HeartbeatMs);          // a beat after it still lands inside the row
    }

    [Fact]
    public void F167_a_warmer_never_joins_a_manifest_fetch_already_in_the_air()
    {
        Assert.True(V.PrefetchRace.ShouldSkipInFlight("0123456789abcdef0123456789abcdef", manifestInFlight: true));
        Assert.False(V.PrefetchRace.ShouldSkipInFlight("0123456789abcdef0123456789abcdef", manifestInFlight: false));
        // A row with no manifest id (the wire tier) cannot be matched to a flight: it is never skipped.
        Assert.False(V.PrefetchRace.ShouldSkipInFlight("", manifestInFlight: true));
    }

    [Fact]
    public void F167_a_manifest_nobody_asked_for_is_not_in_flight()
    {
        Assert.False(V.ManifestMemo.IsInFlight("ffffffffffffffffffffffffffffffff"));
        Assert.False(V.ManifestMemo.IsInFlight(""));
    }
}
