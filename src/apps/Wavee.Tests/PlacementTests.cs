// ── Wavee.Tests/PlacementTests.cs — the surface placement state machine, fact for fact ─────────────────────────────
//
// Ported from 0.2.9's `PlacementCoreTests` (activation, the click spec, sticky-off, availability, host-close, Demote,
// fullscreen, the owner mount decisions, the named regressions, persistence, the deferred upgrade, the single
// transport gate, and the two property tests over arbitrary command sequences). One vocabulary change and nothing
// else: 0.2.9's `PlacementPolicy.Video` is `Video.PlacementPolicy.Music` (the ONE surface that has a policy), and
// `VideoUpgradeGate` is `Video.UpgradeGate`. Every assertion is against a value; nothing here opens a window.

using Wavee;
using Xunit;

using static Wavee.Video;

namespace Wavee.Tests;

public class PlacementCoreTests
{
    static readonly PlacementPolicy Policy = PlacementPolicy.Music;
    const PlacementSet All = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;

    static PlacementState Off(PlacementSet available = All) => PlacementState.Initial(Policy) with { Available = available };

    static PlacementState At(SurfacePlacement p, PlacementSet available = All) => PlacementCore.OpenAt(Off(available), p);

    /// <summary>What a TRACK CHANGE does to this model, and all it does: availability is recomputed for the new track.</summary>
    static PlacementState NextTrack(in PlacementState s, PlacementSet available = All) => PlacementCore.WithAvailability(s, available);

    // ── activation ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Active_when_requested_and_the_content_has_video() => Assert.True(PlacementCore.IsActive(At(SurfacePlacement.Floating)));

    [Fact]
    public void Inactive_when_turned_off() => Assert.False(PlacementCore.IsActive(Off()));

    [Fact]
    public void Inactive_when_the_content_has_no_video()
        => Assert.False(PlacementCore.IsActive(At(SurfacePlacement.Floating, PlacementSet.None)));

    [Fact]
    public void Stays_on_across_track_changes_while_the_intent_is_on()
    {
        var watching = At(SurfacePlacement.Floating);
        Assert.True(PlacementCore.IsActive(NextTrack(watching)));
        Assert.True(PlacementCore.IsActive(NextTrack(NextTrack(watching, PlacementSet.None))));
    }

    [Fact]
    public void Docked_is_the_default_for_a_fresh_profile()
    {
        Assert.Equal(SurfacePlacement.Docked, PlacementState.Initial(Policy).Preferred);
        Assert.Equal(PlacementState.Initial(Policy), PlacementState.Music);
    }

    [Fact]
    public void A_stored_floating_preference_survives_the_default_change()
        => Assert.Equal(SurfacePlacement.Floating, PlacementPersistence.LoadPlacement("floating", Policy));

    // ── the click spec ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unlit_primary_click_opens_at_preferred_which_defaults_to_docked()
        => Assert.Equal(SurfacePlacement.Docked, PlacementCore.Resolve(PlacementCore.TogglePrimary(Off())));

    [Theory]
    [InlineData(SurfacePlacement.Docked)]
    [InlineData(SurfacePlacement.Floating)]
    [InlineData(SurfacePlacement.Detached)]
    public void A_lit_primary_click_turns_off_from_any_placement(SurfacePlacement from)
    {
        var s = PlacementCore.TogglePrimary(At(from));
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.None, s.Requested);
    }

    [Fact]
    public void The_primary_click_is_symmetric_off_on_off_on()
    {
        var s = Off();
        for (int i = 0; i < 4; i++)
        {
            s = PlacementCore.TogglePrimary(s);
            Assert.Equal(i % 2 == 0, PlacementCore.IsActive(s));
        }
    }

    [Fact]
    public void Open_at_makes_the_target_the_new_preferred_home()
    {
        var s = PlacementCore.OpenAt(Off(), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Detached, s.Preferred);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(PlacementCore.TogglePrimary(PlacementCore.TurnOff(s))));
    }

    [Fact]
    public void Turn_off_keeps_preferred_so_the_next_open_goes_home()
    {
        var s = PlacementCore.TurnOff(At(SurfacePlacement.Detached));
        Assert.Equal(SurfacePlacement.Detached, s.Preferred);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(s));
    }

    /// <summary>Regression 2026-07-27: a close that lands while a surface still CLAIMS to be mounted must still
    /// resolve to None. Reality is a report, never an input to the decision.</summary>
    [Fact]
    public void Turn_off_unmounts_even_while_a_surface_still_claims_to_be_live()
    {
        foreach (var claimed in new[] { SurfacePlacement.Floating, SurfacePlacement.Detached })
        {
            var open = PlacementCore.WithLive(At(claimed), claimed);
            Assert.Equal(claimed, PlacementCore.Resolve(open));

            var off = PlacementCore.TurnOff(open);
            Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(off));
            Assert.False(PlacementCore.IsActive(off));
            Assert.True(PlacementCore.Invariant(off));
        }
        var closed = PlacementCore.HostClosed(PlacementCore.WithLive(At(SurfacePlacement.Floating), SurfacePlacement.Floating),
            SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(closed));
    }

    [Fact]
    public void Open_at_turns_video_back_on_after_a_close()
    {
        var closed = PlacementCore.HostClosed(At(SurfacePlacement.Floating), SurfacePlacement.Floating);
        Assert.False(PlacementCore.IsActive(closed));
        var shown = PlacementCore.OpenAt(closed, SurfacePlacement.Floating);
        Assert.True(PlacementCore.IsActive(shown));
        Assert.True(PlacementCore.IsActive(NextTrack(shown)));
    }

    [Fact]
    public void Moving_placement_keeps_it_active_and_does_not_stack()
        => Assert.Equal(SurfacePlacement.Detached,
            PlacementCore.Resolve(PlacementCore.OpenAt(At(SurfacePlacement.Floating), SurfacePlacement.Detached)));

    // ── closing is STICKY OFF ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Closing_the_surface_turns_video_off_not_hidden_for_this_song()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Floating), SurfacePlacement.Floating);
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.None, s.Requested);
        Assert.Equal(SurfacePlacement.Floating, s.Preferred);
    }

    [Fact]
    public void Closing_the_surface_keeps_every_later_track_on_audio_until_the_user_turns_video_back_on()
    {
        var closed = PlacementCore.HostClosed(At(SurfacePlacement.Floating), SurfacePlacement.Floating);
        var trackB = NextTrack(closed);
        Assert.False(PlacementCore.IsActive(trackB));
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(trackB));
        Assert.Equal(SurfacePlacement.None, PlacementCore.ResolveWith(trackB, All));
        var trackC = NextTrack(trackB);
        Assert.False(PlacementCore.IsActive(trackC));
        Assert.True(PlacementCore.IsActive(PlacementCore.TogglePrimary(trackC)));
    }

    [Fact]
    public void Closing_the_surface_mid_track_routes_the_current_track_back_to_audio_too()
    {
        var watching = At(SurfacePlacement.Floating);
        Assert.True(PlacementCore.IsActive(watching));
        var closed = PlacementCore.HostClosed(watching, SurfacePlacement.Floating);
        Assert.False(PlacementCore.IsActive(closed));
        Assert.Equal(SurfacePlacement.None, PlacementCore.ResolveWith(closed, All));
    }

    /// <summary>B9 — the docked card's own ✕ is the same sticky-off rule, and no later availability recompute revives it.</summary>
    [Fact]
    public void A_docked_close_is_sticky_off()
    {
        var closed = PlacementCore.HostClosed(At(SurfacePlacement.Docked), SurfacePlacement.Docked);
        Assert.False(PlacementCore.IsActive(closed));
        Assert.Equal(SurfacePlacement.None, closed.Requested);
        Assert.Equal(SurfacePlacement.Docked, closed.Preferred);

        var recomputed = PlacementCore.WithAvailability(closed, All);
        Assert.False(PlacementCore.IsActive(recomputed));
        Assert.Equal(SurfacePlacement.None, recomputed.Requested);
        Assert.False(PlacementCore.IsActive(NextTrack(recomputed)));
    }

    // ── availability (content ∧ host caps) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Losing_availability_hides_the_surface_but_preserves_intent_and_snaps_back()
    {
        var watching = At(SurfacePlacement.Detached);
        var audioOnly = PlacementCore.WithAvailability(watching, PlacementSet.None);
        Assert.False(PlacementCore.IsActive(audioOnly));
        Assert.Equal(SurfacePlacement.Detached, audioOnly.Requested);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(PlacementCore.WithAvailability(audioOnly, All)));
    }

    [Fact]
    public void An_unavailable_placement_falls_down_the_commitment_ladder_without_rewriting_intent()
    {
        var s = PlacementCore.WithAvailability(At(SurfacePlacement.Detached), PlacementSet.Floating);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));
        Assert.Equal(SurfacePlacement.Detached, s.Requested);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(PlacementCore.WithAvailability(s, All)));
    }

    [Fact]
    public void The_ladder_walks_down_first_then_up()
    {
        Assert.Equal(SurfacePlacement.Docked, PlacementCore.FirstAvailable(SurfacePlacement.Floating, PlacementSet.Docked | PlacementSet.Detached));
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.FirstAvailable(SurfacePlacement.Floating, PlacementSet.Detached));
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.FirstAvailable(SurfacePlacement.Docked, PlacementSet.Floating | PlacementSet.Detached));
        Assert.Equal(SurfacePlacement.None, PlacementCore.FirstAvailable(SurfacePlacement.Docked, PlacementSet.None));
        Assert.Equal(SurfacePlacement.None, PlacementCore.FirstAvailable(SurfacePlacement.None, All));
        Assert.False(PlacementCore.Allows(All, SurfacePlacement.None));
    }

    /// <summary>B4/B5 — narrowing drops the Docked bit (the PiP takes over without rewriting intent); widening re-docks.</summary>
    [Fact]
    public void A_narrow_window_drops_the_docked_bit_and_resolves_floating_with_preferred_intact()
    {
        var narrowed = PlacementCore.WithAvailability(At(SurfacePlacement.Docked),
            PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(narrowed));
        Assert.Equal(SurfacePlacement.Docked, narrowed.Requested);
        Assert.Equal(SurfacePlacement.Docked, narrowed.Preferred);
        Assert.Equal(SurfacePlacement.Docked, PlacementCore.Resolve(PlacementCore.WithAvailability(narrowed, All)));
    }

    [Fact]
    public void Resolve_with_answers_per_content_without_mutating_state()
    {
        var s = At(SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.ResolveWith(s, All));
        Assert.Equal(SurfacePlacement.None, PlacementCore.ResolveWith(s, PlacementSet.None));
        Assert.Equal(All, s.Available);
    }

    // ── host close ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Closing_the_detached_window_falls_back_to_the_mini_player_and_adopts_it_as_preferred()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));
        Assert.Equal(SurfacePlacement.Floating, s.Preferred);
    }

    [Fact]
    public void Closing_the_detached_window_turns_off_when_nothing_less_committing_is_available()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Detached, PlacementSet.Detached), SurfacePlacement.Detached);
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.None, s.Requested);
    }

    [Fact]
    public void Closing_the_mini_player_after_the_detached_fallback_turns_video_off()
    {
        var fellBack = PlacementCore.HostClosed(At(SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(fellBack));
        var off = PlacementCore.HostClosed(fellBack, SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.None, off.Requested);
        Assert.False(PlacementCore.IsActive(NextTrack(off)));
    }

    [Fact]
    public void A_stale_close_is_ignored()
    {
        var moved = PlacementCore.OpenAt(At(SurfacePlacement.Detached), SurfacePlacement.Floating);
        Assert.Equal(moved, PlacementCore.HostClosed(moved, SurfacePlacement.Detached));
        Assert.Equal(moved, PlacementCore.HostClosed(moved, SurfacePlacement.None));
    }

    [Fact]
    public void Closing_fullscreen_by_its_own_chrome_is_exit_fullscreen()
    {
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Docked));
        var back = PlacementCore.HostClosed(fs, SurfacePlacement.Fullscreen);
        Assert.Equal(SurfacePlacement.Docked, PlacementCore.Resolve(back));
    }

    // ── Demote — the rail-close AMBIENT move ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Demote_keeps_preferred()
    {
        var demoted = PlacementCore.Demote(At(SurfacePlacement.Docked), SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.Floating, demoted.Requested);
        Assert.Equal(SurfacePlacement.Docked, demoted.Preferred);
        Assert.True(PlacementCore.Invariant(demoted));
    }

    [Fact]
    public void Demote_with_nothing_available_turns_off()
    {
        var demoted = PlacementCore.Demote(At(SurfacePlacement.Docked, PlacementSet.None), SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.None, demoted.Requested);
        Assert.Equal(SurfacePlacement.Docked, demoted.Preferred);
        Assert.False(PlacementCore.IsActive(demoted));
        Assert.True(PlacementCore.Invariant(demoted));
    }

    [Fact]
    public void Demote_is_inert_when_already_off() => Assert.Equal(Off(), PlacementCore.Demote(Off(), SurfacePlacement.Floating));

    // ── fullscreen ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fullscreen_remembers_where_to_go_back_and_is_never_the_preferred_home()
    {
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Detached));
        Assert.Equal(SurfacePlacement.Fullscreen, fs.Requested);
        Assert.Equal(SurfacePlacement.Detached, fs.ReturnTo);
        Assert.Equal(SurfacePlacement.Detached, fs.Preferred);

        var back = PlacementCore.ExitFullscreen(fs);
        Assert.Equal(SurfacePlacement.Detached, back.Requested);
        Assert.Equal(SurfacePlacement.None, back.ReturnTo);
    }

    [Fact]
    public void Entering_fullscreen_twice_keeps_the_original_return_target()
    {
        var twice = PlacementCore.EnterFullscreen(PlacementCore.EnterFullscreen(At(SurfacePlacement.Floating)));
        Assert.Equal(SurfacePlacement.Floating, twice.ReturnTo);
    }

    [Fact]
    public void Fullscreen_entered_from_off_exits_to_the_preferred_home()
        => Assert.Equal(Policy.Default, PlacementCore.ExitFullscreen(PlacementCore.EnterFullscreen(Off())).Requested);

    [Fact]
    public void Exit_fullscreen_is_a_no_op_when_not_in_fullscreen()
    {
        var s = At(SurfacePlacement.Floating);
        Assert.Equal(s, PlacementCore.ExitFullscreen(s));
    }

    [Fact]
    public void Fullscreen_when_unavailable_falls_to_the_only_available_placement()
        => Assert.Equal(SurfacePlacement.Detached,
            PlacementCore.Resolve(PlacementCore.EnterFullscreen(At(SurfacePlacement.Detached, PlacementSet.Detached))));

    /// <summary>§3.3 — an unavailable Fullscreen walks from the CHEAPEST rung, never down into Detached first.</summary>
    [Fact]
    public void Fullscreen_when_unavailable_never_escalates_to_detached()
    {
        Assert.Equal(SurfacePlacement.Docked,
            PlacementCore.FirstAvailable(SurfacePlacement.Fullscreen, PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached));
        Assert.Equal(SurfacePlacement.Floating,
            PlacementCore.FirstAvailable(SurfacePlacement.Fullscreen, PlacementSet.Floating | PlacementSet.Detached));
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.FirstAvailable(SurfacePlacement.Fullscreen, PlacementSet.Detached));
        Assert.Equal(SurfacePlacement.None, PlacementCore.FirstAvailable(SurfacePlacement.Fullscreen, PlacementSet.None));
    }

    /// <summary>B16/B17 — entering from Docked remembers Docked and exiting restores exactly that.</summary>
    [Fact]
    public void Fullscreen_captures_and_restores_return_to_from_docked()
    {
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Docked));
        Assert.Equal(SurfacePlacement.Docked, fs.ReturnTo);
        Assert.Equal(SurfacePlacement.Fullscreen, fs.Requested);
        Assert.Equal(SurfacePlacement.Docked, fs.Preferred);

        var back = PlacementCore.ExitFullscreen(fs);
        Assert.Equal(SurfacePlacement.Docked, back.Requested);
        Assert.Equal(SurfacePlacement.None, back.ReturnTo);
    }

    [Fact]
    public void Open_at_fullscreen_is_enter_fullscreen_and_open_at_none_is_turn_off()
    {
        var s = At(SurfacePlacement.Floating);
        Assert.Equal(PlacementCore.EnterFullscreen(s), PlacementCore.OpenAt(s, SurfacePlacement.Fullscreen));
        Assert.Equal(PlacementCore.TurnOff(s), PlacementCore.OpenAt(s, SurfacePlacement.None));
    }

    // ── owner mount decisions ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_owner_opens_when_its_placement_is_resolved_and_nothing_is_alive()
        => Assert.Equal(MountAction.Open, PlacementCore.DecideOwned(SurfacePlacement.Detached, SurfacePlacement.Detached, alive: false));

    [Fact]
    public void The_owner_does_nothing_when_already_matching()
        => Assert.Equal(MountAction.None, PlacementCore.DecideOwned(SurfacePlacement.Detached, SurfacePlacement.Detached, alive: true));

    [Theory]
    [InlineData(SurfacePlacement.None)]
    [InlineData(SurfacePlacement.Floating)]
    public void The_owner_closes_when_its_placement_is_no_longer_resolved(SurfacePlacement resolved)
        => Assert.Equal(MountAction.Close, PlacementCore.DecideOwned(resolved, SurfacePlacement.Detached, alive: true));

    [Theory]
    [InlineData(SurfacePlacement.None)]
    [InlineData(SurfacePlacement.Floating)]
    public void The_owner_does_nothing_when_not_resolved_and_not_alive(SurfacePlacement resolved)
        => Assert.Equal(MountAction.None, PlacementCore.DecideOwned(resolved, SurfacePlacement.Detached, alive: false));

    [Fact]
    public void Decide_mount_moves_when_mounted_in_the_wrong_placement()
    {
        Assert.Equal(MountAction.Move, PlacementCore.DecideMount(SurfacePlacement.Detached, SurfacePlacement.Floating));
        Assert.Equal(MountAction.Close, PlacementCore.DecideMount(SurfacePlacement.None, SurfacePlacement.Floating));
        Assert.Equal(MountAction.Open, PlacementCore.DecideMount(SurfacePlacement.Floating, SurfacePlacement.None));
        Assert.Equal(MountAction.None, PlacementCore.DecideMount(SurfacePlacement.Floating, SurfacePlacement.Floating));
    }

    // ── reality reporting is scoped per surface ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_mounted_surface_claims_reality()
        => Assert.Equal(SurfacePlacement.Detached, PlacementCore.LiveAfterReport(SurfacePlacement.None, SurfacePlacement.Detached, mounted: true));

    [Fact]
    public void An_unmounted_surface_releases_only_its_own_claim()
        => Assert.Equal(SurfacePlacement.None, PlacementCore.LiveAfterReport(SurfacePlacement.Detached, SurfacePlacement.Detached, mounted: false));

    [Fact]
    public void An_unmounted_surface_never_erases_another_surfaces_claim()
    {
        var live = PlacementCore.LiveAfterReport(SurfacePlacement.None, SurfacePlacement.Detached, mounted: true);
        live = PlacementCore.LiveAfterReport(live, SurfacePlacement.Floating, mounted: false);
        Assert.Equal(SurfacePlacement.Detached, live);
    }

    [Fact]
    public void Reality_reporting_converges_regardless_of_surface_order()
    {
        var a = PlacementCore.LiveAfterReport(SurfacePlacement.Floating, SurfacePlacement.Floating, mounted: false);
        a = PlacementCore.LiveAfterReport(a, SurfacePlacement.Detached, mounted: true);
        var b = PlacementCore.LiveAfterReport(SurfacePlacement.Floating, SurfacePlacement.Detached, mounted: true);
        b = PlacementCore.LiveAfterReport(b, SurfacePlacement.Floating, mounted: false);
        Assert.Equal(SurfacePlacement.Detached, a);
        Assert.Equal(SurfacePlacement.Detached, b);
    }

    // ── the async-resolve fence ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_resolve_publishes_when_the_captured_generation_is_still_current()
        => Assert.True(PlacementCore.IsCurrentGeneration(capturedGen: 3, currentGen: 3));

    [Fact]
    public void A_superseded_resolve_is_dropped()
        => Assert.False(PlacementCore.IsCurrentGeneration(capturedGen: 3, currentGen: 4));

    // ── named regressions ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bug 3: closing the pop-out left the toggle lit with no surface behind it.</summary>
    [Fact]
    public void Regression_stuck_toggle_closing_the_pop_out_lands_in_the_mini_player()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.True(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));
    }

    /// <summary>Bug 4: a late resolve for the PREVIOUS track republished itself over the current one.</summary>
    [Fact]
    public void Regression_stale_video_a_superseded_resolve_never_publishes()
    {
        long gen = 7;
        long captured = gen;
        gen++;
        Assert.False(PlacementCore.IsCurrentGeneration(captured, gen));
    }

    /// <summary>Bug 5: placement lived in three owners at once. One enum makes "mounted in two places" unrepresentable.</summary>
    [Fact]
    public void Regression_placement_split_at_most_one_placement_is_ever_resolved()
    {
        var s = At(SurfacePlacement.Floating);
        foreach (var move in new[] { SurfacePlacement.Detached, SurfacePlacement.Floating, SurfacePlacement.Detached, SurfacePlacement.Docked })
        {
            s = PlacementCore.OpenAt(s, move);
            int mounted = 0;
            foreach (var p in PlacementCore.AllPlacements)
                if (p != SurfacePlacement.None && PlacementCore.Resolve(s) == p) mounted++;
            Assert.Equal(1, mounted);
        }
    }

    /// <summary>The UX complaint: the primary click spawned an always-on-top OS window as its first response.</summary>
    [Fact]
    public void Regression_first_click_opens_docked_not_an_always_on_top_window()
    {
        var s = PlacementCore.TogglePrimary(PlacementState.Initial(Policy) with { Available = All });
        Assert.Equal(SurfacePlacement.Docked, PlacementCore.Resolve(s));
        Assert.NotEqual(SurfacePlacement.Detached, PlacementCore.Resolve(s));
    }

    /// <summary>Bug 6 (2026-07-26): closing the video came back on the next song that had one.</summary>
    [Fact]
    public void Regression_a_closed_video_never_reopens_on_the_next_track()
    {
        var closed = PlacementCore.HostClosed(At(SurfacePlacement.Floating), SurfacePlacement.Floating);
        for (int track = 0; track < 5; track++)
        {
            closed = NextTrack(closed, track % 2 == 0 ? All : PlacementSet.None);
            Assert.False(PlacementCore.IsActive(closed));
        }
    }

    // ── property tests ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Property 1: the invariants hold after EVERY command in EVERY order. Fixed seed, so a failure reproduces.</summary>
    [Fact]
    public void Property_invariants_hold_for_arbitrary_command_sequences()
    {
        var placements = PlacementCore.AllPlacements;
        var sets = new[]
        {
            PlacementSet.None, PlacementSet.Floating, PlacementSet.Detached, All,
            PlacementSet.Fullscreen, PlacementSet.Docked | PlacementSet.Floating, PlacementSet.Docked,
        };
        var kinds = Enum.GetValues<PlacementCommandKind>();

        uint rng = 0x5EED_1234;
        uint Next() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }

        var trail = new List<PlacementCommand>(24);
        for (int seq = 0; seq < 2000; seq++)
        {
            var s = PlacementState.Initial(Policy);
            trail.Clear();
            for (int step = 0; step < 24; step++)
            {
                var cmd = new PlacementCommand(kinds[Next() % (uint)kinds.Length],
                    placements[Next() % (uint)placements.Length], sets[Next() % (uint)sets.Length]);
                trail.Add(cmd);
                s = PlacementCore.Apply(s, cmd);
                if (!PlacementCore.Invariant(s))
                    Assert.Fail($"invariant broken after {string.Join(" → ", trail)}; state = {s}");
            }
        }
    }

    /// <summary>Property 2: the primary affordance is total and symmetric from ANY reachable state — the toggle can
    /// never get stuck.</summary>
    [Fact]
    public void Property_the_primary_toggle_is_total_and_symmetric()
    {
        var sets = new[] { PlacementSet.None, PlacementSet.Docked, PlacementSet.Floating, PlacementSet.Detached, All };

        foreach (var requested in PlacementCore.AllPlacements)
        foreach (var preferred in new[] { SurfacePlacement.Docked, SurfacePlacement.Floating, SurfacePlacement.Detached })
        foreach (var available in sets)
        {
            var s = new PlacementState(requested, preferred, SurfacePlacement.None, SurfacePlacement.None, available);
            bool wasActive = PlacementCore.IsActive(s);
            var next = PlacementCore.TogglePrimary(s);

            if (wasActive)
                Assert.False(PlacementCore.IsActive(next));
            else if (PlacementCore.FirstAvailable(preferred, available) != SurfacePlacement.None)
                Assert.True(PlacementCore.IsActive(next));
            Assert.True(PlacementCore.Invariant(next));
        }
    }

    /// <summary>Apply is exactly the named transition, command by command.</summary>
    [Fact]
    public void Apply_is_the_named_transition()
    {
        var s = At(SurfacePlacement.Detached);
        Assert.Equal(PlacementCore.TogglePrimary(s), PlacementCore.Apply(s, new PlacementCommand(PlacementCommandKind.TogglePrimary)));
        Assert.Equal(PlacementCore.OpenAt(s, SurfacePlacement.Docked), PlacementCore.Apply(s, new PlacementCommand(PlacementCommandKind.OpenAt, SurfacePlacement.Docked)));
        Assert.Equal(PlacementCore.WithAvailability(s, PlacementSet.Floating), PlacementCore.Apply(s, new PlacementCommand(PlacementCommandKind.Availability, Available: PlacementSet.Floating)));
        Assert.Equal(PlacementCore.HostClosed(s, SurfacePlacement.Detached), PlacementCore.Apply(s, new PlacementCommand(PlacementCommandKind.HostClosed, SurfacePlacement.Detached)));
        Assert.Equal(PlacementCore.WithLive(s, SurfacePlacement.Detached), PlacementCore.Apply(s, new PlacementCommand(PlacementCommandKind.LiveChanged, SurfacePlacement.Detached)));
        Assert.Equal(PlacementCore.Demote(s, SurfacePlacement.Floating), PlacementCore.Apply(s, new PlacementCommand(PlacementCommandKind.Demote, SurfacePlacement.Floating)));
    }
}

// ── persistence: "persist where you like to work; never persist whether it is running" ──────────────────────────────

public class PlacementPersistenceTests
{
    static readonly PlacementPolicy Policy = PlacementPolicy.Music;

    [Theory]
    [InlineData(SurfacePlacement.Docked)]
    [InlineData(SurfacePlacement.Floating)]
    [InlineData(SurfacePlacement.Detached)]
    public void The_preferred_placement_round_trips(SurfacePlacement p)
        => Assert.Equal(p, PlacementPersistence.LoadPlacement(PlacementPersistence.SavePlacement(p), Policy));

    [Theory]
    [InlineData(SurfacePlacement.None)]
    [InlineData(SurfacePlacement.Fullscreen)]
    public void Off_and_fullscreen_are_never_persisted(SurfacePlacement p)
    {
        Assert.Equal("", PlacementPersistence.SavePlacement(p));
        Assert.Equal(Policy.Default, PlacementPersistence.LoadPlacement(PlacementPersistence.SavePlacement(p), Policy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("fullscreen")]
    public void An_unusable_preference_falls_back_to_the_surface_default(string? raw)
        => Assert.Equal(Policy.Default, PlacementPersistence.LoadPlacement(raw, Policy));

    [Fact]
    public void A_stored_preference_is_read_case_and_whitespace_tolerantly()
        => Assert.Equal(SurfacePlacement.Detached, PlacementPersistence.LoadPlacement("  Detached ", Policy));

    [Fact]
    public void A_placement_the_policy_no_longer_allows_is_not_resurrected()
    {
        var floatingOnly = new PlacementPolicy(PlacementSet.Floating, SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.Floating, PlacementPersistence.LoadPlacement("detached", floatingOnly));
    }

    [Fact]
    public void The_stored_preference_is_a_name_not_an_enum_number()
    {
        Assert.Equal("docked", PlacementPersistence.SavePlacement(SurfacePlacement.Docked));
        Assert.Equal("detached", PlacementPersistence.SavePlacement(SurfacePlacement.Detached));
        Assert.Equal("floating", PlacementPersistence.SavePlacement(SurfacePlacement.Floating));
    }

    [Fact]
    public void Geometry_round_trips_and_rounds_to_whole_units()
    {
        Assert.True(PlacementPersistence.TryLoadRect(PlacementPersistence.SaveRect(1720.4f, 880.6f, 360f, 202f),
            out float x, out float y, out float w, out float h));
        Assert.Equal(1720f, x);
        Assert.Equal(881f, y);
        Assert.Equal(360f, w);
        Assert.Equal(202f, h);
    }

    [Fact]
    public void Negative_positions_survive()
    {
        Assert.True(PlacementPersistence.TryLoadRect(PlacementPersistence.SaveRect(-1920f, -140f, 480f, 270f),
            out float x, out float y, out float w, out float h));
        Assert.Equal(-1920f, x);
        Assert.Equal(-140f, y);
        Assert.Equal(480f, w);
        Assert.Equal(270f, h);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    [InlineData("a,b,c,d")]
    [InlineData("10,10,0,0")]
    [InlineData("10,10,-5,-5")]
    public void Malformed_geometry_is_rejected(string? raw)
        => Assert.False(PlacementPersistence.TryLoadRect(raw, out _, out _, out _, out _));

    [Fact]
    public void Degenerate_geometry_is_not_even_written()
        => Assert.Equal("", PlacementPersistence.SaveRect(10f, 10f, 0f, 0f));
}

// ── the deferred upgrade: an association landing MID-TRACK never swaps the media ────────────────────────────────────

public class PlacementUpgradeGateTests
{
    const PlacementSet All = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;

    static PlacementState Off() => PlacementState.Music with { Available = All };
    static PlacementState At(SurfacePlacement p) => PlacementCore.OpenAt(Off(), p);

    [Fact]
    public void An_association_landing_mid_track_is_deferred_so_the_playing_track_is_never_reloaded()
    {
        var playingAsAudio = PlacementCore.WithAvailability(At(SurfacePlacement.Floating), PlacementSet.None);
        Assert.False(PlacementCore.IsActive(playingAsAudio));
        var target = UpgradeGate.FoldAvailability(playingAsAudio, hasVideo: true, hostCapable: All);
        Assert.True(PlacementCore.IsActive(target));
        Assert.True(UpgradeGate.DeferUpgrade(playingAsAudio, target, commitUpgrade: false));
    }

    [Fact]
    public void A_track_boundary_or_an_explicit_user_action_commits_the_upgrade()
    {
        var playingAsAudio = PlacementCore.WithAvailability(At(SurfacePlacement.Floating), PlacementSet.None);
        var target = UpgradeGate.FoldAvailability(playingAsAudio, hasVideo: true, hostCapable: All);
        Assert.False(UpgradeGate.DeferUpgrade(playingAsAudio, target, commitUpgrade: true));
    }

    [Fact]
    public void A_downgrade_always_commits_even_on_the_deferred_path()
    {
        var watching = At(SurfacePlacement.Floating);
        var target = UpgradeGate.FoldAvailability(watching, hasVideo: false, hostCapable: All);
        Assert.False(PlacementCore.IsActive(target));
        Assert.False(UpgradeGate.DeferUpgrade(watching, target, commitUpgrade: false));
    }

    [Fact]
    public void Nothing_is_deferred_when_the_user_has_video_turned_off()
    {
        var off = PlacementCore.WithAvailability(PlacementState.Music, PlacementSet.None);
        var target = UpgradeGate.FoldAvailability(off, hasVideo: true, hostCapable: All);
        Assert.False(PlacementCore.IsActive(target));
        Assert.False(UpgradeGate.DeferUpgrade(off, target, commitUpgrade: false));
    }

    [Fact]
    public void Clicking_after_a_deferred_land_starts_the_video_because_the_intent_path_refolds_availability()
    {
        var stale = PlacementCore.WithAvailability(At(SurfacePlacement.Floating), PlacementSet.None);
        Assert.False(PlacementCore.IsActive(PlacementCore.TogglePrimary(stale)));
        var clicked = UpgradeGate.PrimaryClick(stale, hasVideo: true, hostCapable: All);
        Assert.True(PlacementCore.IsActive(clicked));
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(clicked));
    }

    [Fact]
    public void Clicking_after_a_deferred_land_never_reads_the_standing_intent_as_already_watching()
    {
        var stale = PlacementCore.WithAvailability(At(SurfacePlacement.Floating), PlacementSet.None);
        Assert.False(PlacementCore.IsActive(PlacementCore.TogglePrimary(UpgradeGate.FoldAvailability(stale, true, All))));
        Assert.True(PlacementCore.IsActive(UpgradeGate.PrimaryClick(stale, hasVideo: true, hostCapable: All)));
    }

    [Fact]
    public void The_primary_click_still_toggles_normally_when_nothing_was_deferred()
    {
        var watching = At(SurfacePlacement.Floating);
        Assert.False(PlacementCore.IsActive(UpgradeGate.PrimaryClick(watching, hasVideo: true, hostCapable: All)));
        var off = PlacementCore.TurnOff(watching);
        Assert.True(PlacementCore.IsActive(UpgradeGate.PrimaryClick(off, hasVideo: true, hostCapable: All)));
        Assert.False(PlacementCore.IsActive(UpgradeGate.PrimaryClick(off, hasVideo: false, hostCapable: All)));
    }

    [Fact]
    public void Show_video_at_after_a_deferred_land_also_refolds()
    {
        var stale = PlacementCore.WithAvailability(Off(), PlacementSet.None);
        var opened = PlacementCore.OpenAt(UpgradeGate.FoldAvailability(stale, hasVideo: true, hostCapable: All), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(opened));
    }

    /// <summary>§3.4 — availability is content ∧ HOST capability, not all-or-nothing.</summary>
    [Fact]
    public void Availability_is_the_one_content_channel_masked_by_host_capability()
    {
        Assert.Equal(PlacementPolicy.Music.Allowed, UpgradeGate.AvailabilityFor(true, All));
        Assert.Equal(PlacementSet.None, UpgradeGate.AvailabilityFor(false, All));
        var hostCapable = PlacementSet.Docked | PlacementSet.Floating;
        Assert.Equal(hostCapable, UpgradeGate.AvailabilityFor(true, hostCapable));
        Assert.Equal(PlacementSet.None, UpgradeGate.AvailabilityFor(hasVideo: false, hostCapable: hostCapable));
    }
}

// ── gate.media.single-transport ─────────────────────────────────────────────────────────────────────────────────────

public class PlacementTransportTests
{
    const PlacementSet All = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;

    static PlacementState At(SurfacePlacement p, PlacementSet available = All)
        => PlacementCore.OpenAt(PlacementState.Music with { Available = available }, p);

    [Fact]
    public void Exactly_one_owner_claims_the_transport_for_every_placement()
    {
        Assert.True(PlacementCore.SingleTransportInvariant());
        foreach (var p in PlacementCore.AllPlacements)
            Assert.Equal(1, PlacementCore.TransportClaimants(p));
        Assert.Equal(4, PlacementCore.AllTransportOwners.Length);
    }

    [Fact]
    public void Fullscreen_takes_the_transport_from_the_global_bar()
    {
        Assert.Equal(TransportOwner.Fullscreen, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Fullscreen)));
        Assert.False(PlacementCore.OwnsTransport(TransportOwner.GlobalBar, SurfacePlacement.Fullscreen));
    }

    [Fact]
    public void In_window_video_cards_own_their_own_chrome()
    {
        Assert.Equal(TransportOwner.Docked, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Docked)));
        Assert.Equal(TransportOwner.Docked, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Floating)));
    }

    [Fact]
    public void With_no_surface_the_transport_is_the_bars()
        => Assert.Equal(TransportOwner.GlobalBar, PlacementCore.TransportOwnerOf(PlacementState.Music));

    [Fact]
    public void A_docked_card_does_not_disarm_the_global_bar()
    {
        foreach (var p in new[] { SurfacePlacement.Docked, SurfacePlacement.Floating })
            Assert.NotEqual(TransportOwner.Fullscreen, PlacementCore.TransportOwnerFor(p));
    }

    [Fact]
    public void The_pop_out_carries_its_own_windows_transport_without_disarming_the_main_bar()
    {
        Assert.Equal(TransportOwner.PopOut, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Detached)));
        Assert.NotEqual(TransportOwner.Fullscreen, PlacementCore.TransportOwnerOf(At(SurfacePlacement.Detached)));
    }

    [Fact]
    public void Ownership_derives_from_resolved_never_from_requested()
    {
        var stranded = At(SurfacePlacement.Fullscreen, PlacementSet.None);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(stranded));
        Assert.Equal(TransportOwner.GlobalBar, PlacementCore.TransportOwnerOf(stranded));
    }

    [Fact]
    public void Bit_and_allows_agree_for_every_placement()
    {
        foreach (var p in PlacementCore.AllPlacements)
        {
            Assert.Equal(p != SurfacePlacement.None, PlacementCore.Allows(All, p));
            Assert.Equal(p == SurfacePlacement.None, PlacementCore.Bit(p) == PlacementSet.None);
        }
    }
}
