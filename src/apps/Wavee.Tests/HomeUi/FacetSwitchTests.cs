// ── Wavee.Tests/HomeUi/FacetSwitchTests.cs — table-driven coverage of every FacetSwitch transition ────────────────
//
// Pure: FacetSwitchState/FacetSwitch have no engine dependency. No source-text tests — every fact drives the state
// machine through its public transforms only. Remediation F13: `FadingOut`/`OutDone` are gone — the swap is the
// engine's own keyed Exit/Enter on the facet content root, not a phase or a timer here, so Select(Fresh|Stale)
// publishes `Target` immediately instead of going through an intermediate fading phase. Seventh pass: F35's
// `Interacted` mark is gone (its pointer-down host made the whole zone column a hover scope), so a Refreshing landing
// swaps on at-top + within-window alone.

using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class FacetSwitchTests
{
    static FacetSwitchState Idle(string facet = "") => FacetSwitchState.Initial(facet);

    // ── Select ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Select_fresh_publishes_immediately_to_Idle()
    {
        var s = FacetSwitch.Select(Idle(), "music-chip", FacetCacheVerdict.Fresh, nowMs: 1000);
        Assert.Equal(FacetPhase.Idle, s.Phase);
        Assert.Equal("music-chip", s.Published);
        Assert.Equal("music-chip", s.Target);
        Assert.False(s.Dim);
        Assert.False(s.Bar);
    }

    [Fact]
    public void Select_stale_publishes_immediately_and_enters_Refreshing()
    {
        var s = FacetSwitch.Select(Idle(), "music-chip", FacetCacheVerdict.Stale, nowMs: 1000);
        Assert.Equal(FacetPhase.Refreshing, s.Phase);
        Assert.Equal("music-chip", s.Published);
        Assert.Equal("music-chip", s.Target);
        Assert.False(s.Dim);
        Assert.True(s.Bar);
    }

    [Fact]
    public void Select_missing_enters_Loading_with_dim_and_bar()
    {
        var s = FacetSwitch.Select(Idle(), "podcasts-chip", FacetCacheVerdict.Missing, nowMs: 1000);
        Assert.Equal(FacetPhase.Loading, s.Phase);
        Assert.Equal("", s.Published);
        Assert.Equal("podcasts-chip", s.Target);
        Assert.True(s.Dim);
        Assert.True(s.Bar);
    }

    [Fact]
    public void Select_unavailable_is_a_noop()
    {
        var before = Idle();
        var s = FacetSwitch.Select(before, "audiobooks-chip", FacetCacheVerdict.Unavailable, nowMs: 1000);
        Assert.Equal(before, s);
    }

    [Fact]
    public void Reselecting_current_target_is_a_noop_mid_switch()
    {
        var mid = FacetSwitch.Select(Idle(), "music-chip", FacetCacheVerdict.Missing, nowMs: 1000);
        var s = FacetSwitch.Select(mid, "music-chip", FacetCacheVerdict.Missing, nowMs: 2000);
        Assert.Equal(mid, s);
    }

    [Fact]
    public void Reselecting_currently_published_idle_facet_is_a_noop()
    {
        var idle = Idle("music-chip");
        var s = FacetSwitch.Select(idle, "music-chip", FacetCacheVerdict.Fresh, nowMs: 1000);
        Assert.Equal(idle, s);
    }

    // ── Landed ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Loading_Landed_always_swaps_to_Idle_publishing_the_target()
    {
        var loading = FacetSwitch.Select(Idle(), "podcasts-chip", FacetCacheVerdict.Missing, nowMs: 1000);
        var landed = FacetSwitch.Landed(loading, "podcasts-chip", scrollOffset: 500f, nowMs: 1500, out bool swap);
        Assert.True(swap);
        Assert.Equal(FacetPhase.Idle, landed.Phase);
        Assert.Equal("podcasts-chip", landed.Published);
        Assert.Equal("podcasts-chip", landed.Target);
    }

    [Fact]
    public void Refreshing_Landed_settles_to_Idle_when_at_top_within_window()
    {
        var stale = FacetSwitch.Select(Idle(), "podcasts-chip", FacetCacheVerdict.Stale, nowMs: 1000);
        var landed = FacetSwitch.Landed(stale, "podcasts-chip", scrollOffset: 0f, nowMs: 1000 + 1000, out bool swap);
        Assert.True(swap);
        Assert.Equal(FacetPhase.Idle, landed.Phase);
        Assert.Equal("podcasts-chip", landed.Published);
    }

    [Fact]
    public void Refreshing_Landed_does_not_swap_when_scrolled_away()
    {
        var stale = FacetSwitch.Select(Idle(), "podcasts-chip", FacetCacheVerdict.Stale, nowMs: 1000);
        var landed = FacetSwitch.Landed(stale, "podcasts-chip", scrollOffset: 400f, nowMs: 1000 + 500, out bool swap);
        Assert.False(swap);
        Assert.Equal(FacetPhase.Idle, landed.Phase);
        Assert.Equal(landed.Published, landed.Target);
    }

    [Fact]
    public void Refreshing_Landed_does_not_swap_past_the_2s_window()
    {
        var stale = FacetSwitch.Select(Idle(), "podcasts-chip", FacetCacheVerdict.Stale, nowMs: 1000);
        var landed = FacetSwitch.Landed(stale, "podcasts-chip", scrollOffset: 0f, nowMs: 1000 + 2001, out bool swap);
        Assert.False(swap);
        Assert.Equal(FacetPhase.Idle, landed.Phase);
    }

    [Fact]
    public void Landed_for_a_stale_flight_is_ignored()
    {
        var idle = Idle("music-chip");
        var landed = FacetSwitch.Landed(idle, "podcasts-chip", scrollOffset: 0f, nowMs: 1000, out bool swap);
        Assert.False(swap);
        Assert.Equal(idle, landed);
    }

    // ── Fail ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Loading_Fail_reverts_to_published_and_records_FailedTarget()
    {
        var loading = FacetSwitch.Select(Idle("music-chip"), "podcasts-chip", FacetCacheVerdict.Missing, nowMs: 1000);
        var failed = FacetSwitch.Fail(loading, "podcasts-chip", out string revertTo);
        Assert.Equal("music-chip", revertTo);
        Assert.Equal(FacetPhase.Failed, failed.Phase);
        Assert.Equal("music-chip", failed.Published);
        Assert.Equal("music-chip", failed.Target);
        Assert.Equal("podcasts-chip", failed.FailedTarget);
    }

    [Fact]
    public void Fail_for_a_non_target_facet_is_ignored()
    {
        var idle = Idle("music-chip");
        var s = FacetSwitch.Fail(idle, "podcasts-chip", out string revertTo);
        Assert.Equal(idle, s);
        Assert.Equal("music-chip", revertTo);
    }

    // ── Dismiss ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dismiss_clears_Failed_back_to_Idle()
    {
        var loading = FacetSwitch.Select(Idle("music-chip"), "podcasts-chip", FacetCacheVerdict.Missing, nowMs: 1000);
        var failed = FacetSwitch.Fail(loading, "podcasts-chip", out _);
        var s = FacetSwitch.Dismiss(failed);
        Assert.Equal(FacetPhase.Idle, s.Phase);
        Assert.Equal("music-chip", s.Published);
        Assert.Null(s.FailedTarget);
    }

    [Fact]
    public void Dismiss_outside_Failed_is_a_noop()
    {
        var idle = Idle("music-chip");
        Assert.Equal(idle, FacetSwitch.Dismiss(idle));
    }
}
