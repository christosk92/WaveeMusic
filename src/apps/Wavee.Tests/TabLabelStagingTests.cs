// ── Wavee.Tests/TabLabelStagingTests.cs — when the tab strip relabels after a navigation ───────────────────────────
//
// A commit writes Motion, Current and the tab's route in ONE flush, but the page it replaces is still on screen for
// its exit leg (Design.Nav.ExitDurationMs). Labelling the tab from its own route therefore named the NEXT page a frame
// (and then 90 ms) before the content changed. `Shell.TabLabelStaging` (Shell/Shell.Chrome.cs) is the pure half of the
// fix: WHICH route a tab is labelled from (the staged Shown route when it is about that tab, the tab's own otherwise)
// and HOW LONG the staged route lags the commit (the exit leg, or nothing when no exit leg runs).
//
// The rules each fact exists to stop being "simplified" away:
//
//   THE STAGED ROUTE ONLY CLAIMS ITS OWN TAB. Every other tab keeps its own label — otherwise a swap on tab 1 would
//   relabel tab 2 for 90 ms.
//
//   A TAB ID OF 0 IS NOT A TAB. A route that never went through a commit (boot's Home, a restored pin) carries Tab 0;
//   a staged Tab-0 route matching every other un-normalised tab would label them all "Home".
//
//   THE LAG IS THE EXIT LEG, EXACTLY. Less and the label is early again; more and it trails the entered page.

using Xunit;
using Route = Wavee.Shell.Route;
using RouteKind = Wavee.Shell.RouteKind;
using TabLabelStaging = Wavee.Shell.TabLabelStaging;

namespace Wavee.Tests;

public class TabLabelStagingTests
{
    [Fact]
    public void AStagedRouteOnTheSameTab_LabelsTheTab()
    {
        var tab = new Route(RouteKind.Browse, Tab: 2);
        var shown = new Route(RouteKind.Home, Tab: 2);
        Assert.Equal(shown, TabLabelStaging.LabelRoute(tab, shown));
    }

    [Fact]
    public void AStagedRouteOnAnotherTab_IsIgnored()
    {
        var tab = new Route(RouteKind.Browse, Tab: 2);
        var shown = new Route(RouteKind.Home, Tab: 1);
        Assert.Equal(tab, TabLabelStaging.LabelRoute(tab, shown));
    }

    [Fact]
    public void NothingStaged_FallsBackToTheTabsOwnRoute()
    {
        var tab = new Route(RouteKind.Search, Tab: 3);
        Assert.Equal(tab, TabLabelStaging.LabelRoute(tab, Route.None));
    }

    [Fact]
    public void AStagedRouteWithoutATab_ClaimsNoTab()
    {
        // Boot's Home (Tab 0) against a tab whose route was never normalised (Tab 0 too): the tab keeps its own label.
        var tab = new Route(RouteKind.Liked, Tab: 0);
        var shown = new Route(RouteKind.Home, Tab: 0);
        Assert.Equal(tab, TabLabelStaging.LabelRoute(tab, shown));
    }

    [Fact]
    public void ASequencedStyle_LagsByTheExitLeg()
        => Assert.Equal(Design.Nav.ExitDurationMs, TabLabelStaging.DelayMs(instantCut: false, reducedMotion: false));

    [Fact]
    public void TheInstantCut_DoesNotLag()
        => Assert.Equal(0f, TabLabelStaging.DelayMs(instantCut: true, reducedMotion: false));

    [Fact]
    public void ReducedMotion_DoesNotLag()
        => Assert.Equal(0f, TabLabelStaging.DelayMs(instantCut: false, reducedMotion: true));
}
