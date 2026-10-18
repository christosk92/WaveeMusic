// ── Wavee.Tests/NavGcPolicyTests.cs — the governor's post-navigation gen-0 collection ─────────────────────────────────
//
// `Platform/Residency.Heap.cs`'s `NavGcPolicy` is pure: after a navigation's page lands and the host has been quiet for
// QuietMs, one gen-0 collection runs if gen 0 holds at least MinGen0Bytes, so the next page mount starts with an empty
// budget instead of paying the collection inside the frame it appears on. Never while the user interacts, an animation
// runs or a track opens; never while a newer navigation is in flight (it would collect inside that one's transition);
// at most once per navigation; it gives up on a navigation that never goes quiet.
using Xunit;

namespace Wavee.Tests;

public class NavGcPolicyTests
{
    const long MB = 1L << 20;

    static NavGcFacts Settled(long msSinceLanding = 300, long gen0 = 2 * MB) => new(
        Pending: true, MsSinceLanding: msSinceLanding, HostQuiet: true, TransportCritical: false, Gen0Bytes: gen0);

    [Fact]
    public void A_quiet_landed_page_with_garbage_collects_once()
        => Assert.Equal(NavGcVerdict.Collect, NavGcPolicy.Decide(Settled()));

    [Fact]
    public void Nothing_pending_is_done()
        => Assert.Equal(NavGcVerdict.Done, NavGcPolicy.Decide(Settled() with { Pending = false }));

    [Fact]
    public void Too_soon_after_the_landing_waits()
        => Assert.Equal(NavGcVerdict.Wait, NavGcPolicy.Decide(Settled(msSinceLanding: NavGcPolicy.QuietMs - 1)));

    [Fact]
    public void Interaction_animation_or_a_track_opening_waits_then_gives_up()
    {
        Assert.Equal(NavGcVerdict.Wait, NavGcPolicy.Decide(Settled() with { HostQuiet = false }));
        Assert.Equal(NavGcVerdict.Wait, NavGcPolicy.Decide(Settled() with { TransportCritical = true }));
        Assert.Equal(NavGcVerdict.Done, NavGcPolicy.Decide(Settled(msSinceLanding: NavGcPolicy.GiveUpMs) with { HostQuiet = false }));
    }

    [Fact]
    public void A_newer_navigation_in_flight_waits_for_its_landing()
        => Assert.Equal(NavGcVerdict.Wait, NavGcPolicy.Decide(Settled() with { NavigationInFlight = true }));

    [Fact]
    public void The_wait_outlasts_the_enter_leg()
        => Assert.True(NavGcPolicy.QuietMs > (long)Design.Nav.EnterDurationMs);

    [Fact]
    public void Too_little_garbage_is_not_worth_a_pause()
        => Assert.Equal(NavGcVerdict.Done, NavGcPolicy.Decide(Settled(gen0: NavGcPolicy.MinGen0Bytes - 1)));
}
