// ── Wavee.Tests/HomeUi/ScreenLoadTests.cs — the facet document's Loadable projection (Home/Reveal.cs) ──────────────

using FluentGpu.Signals;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class ScreenLoadTests
{
    [Fact]
    public void Zones_are_ready_even_before_the_live_attempt_concludes()
        => Assert.Equal(LoadState.Ready, ScreenLoad.Of(zoneCount: 3, knowsSections: true, concluded: false, online: true, sessionStarting: false));

    [Fact]
    public void An_empty_page_stays_pending_while_the_live_attempt_is_in_flight()
        => Assert.Equal(LoadState.Pending, ScreenLoad.Of(0, knowsSections: true, concluded: false, online: true, sessionStarting: false));

    [Fact]
    public void A_concluded_attempt_with_no_sections_online_is_failed()
        => Assert.Equal(LoadState.Failed, ScreenLoad.Of(0, knowsSections: false, concluded: true, online: true, sessionStarting: false));

    [Fact]
    public void Offline_an_unanswered_facet_is_an_empty_ready_page_not_a_failure()
        => Assert.Equal(LoadState.Ready, ScreenLoad.Of(0, knowsSections: false, concluded: true, online: false, sessionStarting: false));

    [Fact]
    public void An_unanswered_row_while_the_session_is_still_starting_is_pending_not_empty()
        => Assert.Equal(LoadState.Pending, ScreenLoad.Of(0, knowsSections: false, concluded: true, online: false, sessionStarting: true));

    [Fact]
    public void A_concluded_answered_empty_facet_is_ready()
        => Assert.Equal(LoadState.Ready, ScreenLoad.Of(0, knowsSections: true, concluded: true, online: true, sessionStarting: false));
}
