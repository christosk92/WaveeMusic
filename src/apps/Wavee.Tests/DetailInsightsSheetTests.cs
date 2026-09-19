// ── Wavee.Tests/DetailInsightsSheetTests.cs — which arm hosts the facts bento, and how wide its sheet is ────────────
//
// The vertical (hero/stacked) arm has no rail, so the facts bento used to be appended under the rows as a page footer
// nobody scrolled to. It is now a dismissable sheet laid OVER the content, opened from the vertical arm's chrome.
// `Detail.InsightsSheet` is the whole decision, extracted so it can be tested without an engine: WHICH arm hosts the
// bento, WHETHER a toggle exists there and WHICH of its two entry points owns input at a given scroll position, what
// the sheet's resolved width is for a page of a given width, what the band's action cluster then claims, and what
// happens to an open sheet when the page stops being able to show it.
//
// The facts that must not regress:
//   · a two-column arm is untouched — the rail hosts the bento and no toggle is composed;
//   · the two hosts are MUTUALLY EXCLUSIVE at every mode, and there is no third (page-body) host any more;
//   · the toggle is gated on the facts actually being there, so a page with none shows no button;
//   · the hero toolbar and the pinned band each carry that one toggle, and EXACTLY ONE of them is live at any
//     scroll position — the hero before the band sticks, the band after;
//   · an absent facts slot means "not answered yet", not "no facts": the frame latches it for the route.

using Xunit;
using Breakpoints = Wavee.Detail.Breakpoints;
using InsightsSheet = Wavee.Detail.InsightsSheet;
using BandLayout = Wavee.Detail.BandLayout;

namespace Wavee.Tests;

public class DetailInsightsSheetTests
{
    // ── the resolved width ──

    /// <summary>An UNMEASURED page answers the preferred width — the pre-measure seed the first bounds callback
    /// corrects, exactly like the rail's page-aware ceiling.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void AnUnmeasuredPageTakesThePreferredWidth(float pageWidth)
        => Assert.Equal(InsightsSheet.PreferredWidth, InsightsSheet.WidthFor(pageWidth));

    /// <summary>A page wide enough for the preferred width gets it outright; the fraction only ever narrows.</summary>
    [Theory]
    [InlineData(2000f)]
    [InlineData(1024f)]
    [InlineData(579f)]      // the widest page the vertical arm can still be (VerticalExitW - 1)
    [InlineData(514f)]      // 514 × 0.76 = 390.64 → the preferred width is the smaller of the two
    public void AWidePageGetsThePreferredWidth(float pageWidth)
        => Assert.Equal(InsightsSheet.PreferredWidth, InsightsSheet.WidthFor(pageWidth));

    /// <summary>Below that, the sheet is a SHARE of the page, so a strip of the list always stays visible behind it.</summary>
    [Theory]
    [InlineData(500f, 380f)]     // 500 × 0.76 = 380
    [InlineData(400f, 304f)]
    [InlineData(360f, 273f)]     // 273.6 → floored to whole DIP
    [InlineData(320f, 243f)]     // 243.2
    public void ANarrowPageGetsTheFraction(float pageWidth, float expected)
        => Assert.Equal(expected, InsightsSheet.WidthFor(pageWidth));

    [Theory]
    [InlineData(2000f)]
    [InlineData(514f)]
    [InlineData(400f)]
    [InlineData(300f)]
    [InlineData(120f)]
    public void TheSheetNeverCoversTheWholePage(float pageWidth)
    {
        float w = InsightsSheet.WidthFor(pageWidth);
        Assert.True(w > 0f);
        Assert.True(w <= InsightsSheet.PreferredWidth);
        Assert.True(w < pageWidth, $"the sheet must leave a strip of the list at {pageWidth}");
    }

    /// <summary>A degenerate page still answers a positive width — the pane is composed before it is ever opened, and a
    /// zero-width pane would be a layout hole rather than a closed sheet.</summary>
    [Fact]
    public void AnAbsurdlyNarrowPageStillAnswersAPositiveWidth()
        => Assert.True(InsightsSheet.WidthFor(1f) > 0f);

    // ── which kinds carry facts at all ──

    [Theory]
    [InlineData(DetailKind.Liked, true)]
    [InlineData(DetailKind.Playlist, true)]
    [InlineData(DetailKind.Album, false)]
    [InlineData(DetailKind.Show, false)]
    [InlineData(DetailKind.Episode, false)]
    public void OnlyTheTwoListLikeSurfacesCarryFacts(DetailKind kind, bool expected)
        => Assert.Equal(expected, InsightsSheet.KindHasFacts(kind));

    // ── which arm hosts the bento ──

    /// <summary>The two-column arms are UNCHANGED: the rail hosts the bento, as it always has.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(Breakpoints.VerticalMode, false)]
    [InlineData(7, false)]     // off the ladder: no rail composed
    [InlineData(-1, false)]
    public void TheRailHostsTheBentoInEveryTwoColumnArm(int mode, bool expected)
    {
        Assert.Equal(expected, InsightsSheet.RailHostsFacts(mode, DetailKind.Liked, factsSlot: true));
        Assert.Equal(expected, InsightsSheet.RailHostsFacts(mode, DetailKind.Playlist, factsSlot: true));
    }

    /// <summary>…and the sheet hosts it in the single-column arm, there and nowhere else.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(Breakpoints.VerticalMode, true)]
    [InlineData(7, false)]
    [InlineData(-1, false)]
    public void TheSheetHostsTheBentoInTheVerticalArmOnly(int mode, bool expected)
        => Assert.Equal(expected, InsightsSheet.SheetHostsFacts(mode, DetailKind.Liked, factsSlot: true));

    /// <summary>ONE host per mode, never two — the bug the sheet exists to prevent is a page showing its facts twice.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(Breakpoints.VerticalMode)]
    [InlineData(7)]
    public void TheTwoHostsAreMutuallyExclusive(int mode)
    {
        foreach (var kind in new[] { DetailKind.Liked, DetailKind.Playlist, DetailKind.Album, DetailKind.Show, DetailKind.Episode })
            foreach (bool slot in new[] { false, true })
            {
                bool rail = InsightsSheet.RailHostsFacts(mode, kind, slot);
                bool sheet = InsightsSheet.SheetHostsFacts(mode, kind, slot);
                Assert.False(rail && sheet, $"{kind} at mode {mode} (slot {slot}) claimed BOTH hosts");
                // …and the page body is never a host again: the footer that nobody scrolled to is gone for good.
                Assert.False(InsightsSheet.AppendsFactsToPageBody(mode, kind, slot));
            }
    }

    /// <summary>A page whose facts have not arrived has no host at all — never an empty panel, and never a button that
    /// opens one (<c>User.FactsHas</c> answers the slot, and the slot answers this).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(Breakpoints.VerticalMode)]
    public void NoFactsMeansNoHostAndNoButton(int mode)
    {
        Assert.False(InsightsSheet.RailHostsFacts(mode, DetailKind.Liked, factsSlot: false));
        Assert.False(InsightsSheet.SheetHostsFacts(mode, DetailKind.Liked, factsSlot: false));
        Assert.False(InsightsSheet.ShowsToggle(mode, DetailKind.Liked, factsSlot: false, DetailContent.Tracks));
    }

    // ── the toolbar toggle ──

    /// <summary>The toggle exists in the vertical arm of a facts-bearing TRACK page, and nowhere else. A two-column arm
    /// shows no button at all: its bento is already on screen beside the list.</summary>
    [Theory]
    [InlineData(0, DetailKind.Liked, true, DetailContent.Tracks, false)]
    [InlineData(2, DetailKind.Liked, true, DetailContent.Tracks, false)]
    [InlineData(Breakpoints.VerticalMode, DetailKind.Liked, true, DetailContent.Tracks, true)]
    [InlineData(Breakpoints.VerticalMode, DetailKind.Playlist, true, DetailContent.Tracks, true)]
    [InlineData(Breakpoints.VerticalMode, DetailKind.Album, true, DetailContent.Tracks, false)]
    [InlineData(Breakpoints.VerticalMode, DetailKind.Liked, false, DetailContent.Tracks, false)]
    // an episode list's vertical arm has no bento — and no command bar of its own to hang a toggle on
    [InlineData(Breakpoints.VerticalMode, DetailKind.Show, true, DetailContent.Episodes, false)]
    [InlineData(Breakpoints.VerticalMode, DetailKind.Playlist, true, DetailContent.Episodes, false)]
    public void TheToggleIsTheVerticalArmsAlone(int mode, DetailKind kind, bool slot, DetailContent content, bool expected)
        => Assert.Equal(expected, InsightsSheet.ShowsToggle(mode, kind, slot, content));

    /// <summary>The toggle is composed exactly where the sheet is the host — one predicate, two readers.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(Breakpoints.VerticalMode)]
    public void TheToggleFollowsTheSheetHost(int mode)
        => Assert.Equal(InsightsSheet.SheetHostsFacts(mode, DetailKind.Playlist, factsSlot: true),
                        InsightsSheet.ShowsToggle(mode, DetailKind.Playlist, factsSlot: true, DetailContent.Tracks));

    // ── the live open state ──

    /// <summary>An open sheet SURVIVES only while its toggle does. Widening the window back into a two-column arm (or
    /// losing the facts) closes it, so re-narrowing never restores a sheet the user cannot remember leaving open.</summary>
    [Fact]
    public void WideningThePageClosesAnOpenSheet()
    {
        Assert.True(InsightsSheet.OpenFor(true, Breakpoints.VerticalMode, DetailKind.Liked, true, DetailContent.Tracks));
        Assert.False(InsightsSheet.OpenFor(true, 2, DetailKind.Liked, true, DetailContent.Tracks));
        Assert.False(InsightsSheet.OpenFor(true, 0, DetailKind.Liked, true, DetailContent.Tracks));
    }

    [Fact]
    public void LosingTheFactsClosesAnOpenSheet()
        => Assert.False(InsightsSheet.OpenFor(true, Breakpoints.VerticalMode, DetailKind.Liked, false, DetailContent.Tracks));

    /// <summary>A closed sheet is never opened by the arm alone.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(Breakpoints.VerticalMode)]
    public void AClosedSheetStaysClosed(int mode)
        => Assert.False(InsightsSheet.OpenFor(false, mode, DetailKind.Liked, true, DetailContent.Tracks));

    /// <summary>It never survives a route change: the frame is keyed by subject, so a different subject remounts the
    /// host outright, and the host closes the sheet itself on a same-subject route swap.</summary>
    [Fact]
    public void TheSheetNeverSurvivesARouteChange() => Assert.False(InsightsSheet.SurvivesRouteChange);

    // ── the two entry points: the hero toolbar and the pinned band ──

    /// <summary>The hero collapses; the pinned band does not. Neither entry point alone covers the whole scroll range,
    /// so the sheet has BOTH — and the handoff is EXCLUSIVE, which is what keeps two buttons from reading as two
    /// controls: at every scroll position exactly one of them takes hits.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactlyOneEntryPointOwnsInputAtEveryScrollPosition(bool bandStuck)
    {
        bool band = InsightsSheet.BandToggleTakesInput(bandStuck);
        bool hero = InsightsSheet.HeroToggleTakesInput(bandStuck);
        Assert.NotEqual(band, hero);                                   // never both, never neither
        Assert.Equal(bandStuck, band);                                 // the band is live exactly once it is stuck…
        Assert.True(hero || bandStuck, "the hero must own input for the whole pre-stuck range");
    }

    /// <summary>Dropping the hero's toggle would leave the top of the page — where the band is transparent and owns no
    /// input — with no way to open the sheet at all. Stated as a fact so removing it fails here first.</summary>
    [Fact]
    public void TheHeroKeepsTheToggleBecauseTheBandIsNotYetLive()
        => Assert.True(InsightsSheet.HeroToggleTakesInput(bandStuck: false));

    /// <summary>The band's cluster claims one more word when this arm hosts the sheet, and the compact search field
    /// derives its width from exactly that — so the toggle narrows the field instead of shoving Find · Filter · Play
    /// past the band's right edge.</summary>
    [Fact]
    public void TheToggleWidensTheBandActionClaimByItsOwnWordAndOneGap()
    {
        const float find = 60f, filter = 50f, play = 44f, insights = 70f;
        float without = InsightsSheet.BandActionsWidth(find, filter, play, insights, showsToggle: false);
        float with = InsightsSheet.BandActionsWidth(find, filter, play, insights, showsToggle: true);
        Assert.Equal(find + filter + play + 2f * BandLayout.ActionGap, without);
        Assert.Equal(without + insights + BandLayout.ActionGap, with);
    }

    /// <summary>A page with no sheet claims exactly what it always claimed — the two-column arms and the album family
    /// are untouched by the toggle joining the cluster.</summary>
    [Fact]
    public void APageWithoutTheSheetClaimsExactlyWhatItAlwaysDid()
        => Assert.Equal(InsightsSheet.BandActionsWidth(60f, 50f, 44f, insights: 0f, showsToggle: false),
                        InsightsSheet.BandActionsWidth(60f, 50f, 44f, insights: 999f, showsToggle: false));

    // ── the facts latch: "not answered yet" is not "no facts" ──

    /// <summary>A page that has NEVER offered facts shows nothing and hints at nothing — the absent-button case stays
    /// absent.</summary>
    [Fact]
    public void APageThatNeverOffersFactsIsNeverSettled()
        => Assert.False(InsightsSheet.FactsSettled(everSeen: false, slotNow: false));

    /// <summary>Facts that arrive LATE settle the page the moment they land: the toggle appears when they arrive, which
    /// is the whole point of asking the question per render rather than once.</summary>
    [Fact]
    public void LateFactsSettleThePageWhenTheyArrive()
        => Assert.True(InsightsSheet.FactsSettled(everSeen: false, slotNow: true));

    /// <summary>…and they STAY settled. The page derives its bento slot from a scan of the live row source, which reads
    /// empty while the list's open holds its reveal or the rows re-fold; without the latch the button blinks out with
    /// every such pass (and, when the scan never runs again, never comes back).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeenFactsStaySettledThroughAReFold(bool slotNow)
        => Assert.True(InsightsSheet.FactsSettled(everSeen: true, slotNow));

    /// <summary>The latch feeds the SAME predicate the toggle and the sheet already share — it changes what
    /// <c>factsSlot</c> means, never who reads it.</summary>
    [Fact]
    public void TheLatchFeedsTheOneToggleRule()
    {
        bool settled = InsightsSheet.FactsSettled(everSeen: true, slotNow: false);
        Assert.True(InsightsSheet.ShowsToggle(Breakpoints.VerticalMode, DetailKind.Liked, settled, DetailContent.Tracks));
        Assert.True(InsightsSheet.SheetHostsFacts(Breakpoints.VerticalMode, DetailKind.Liked, settled));
        // …and a latched page in a TWO-COLUMN arm is still the rail's, with no toggle: the latch never moves the host.
        Assert.False(InsightsSheet.ShowsToggle(0, DetailKind.Liked, settled, DetailContent.Tracks));
        Assert.True(InsightsSheet.RailHostsFacts(0, DetailKind.Liked, settled));
    }

    /// <summary>An album never gains a toggle from the latch either — the kind gate is ahead of it.</summary>
    [Fact]
    public void TheLatchNeverGivesTheAlbumFamilyAToggle()
        => Assert.False(InsightsSheet.ShowsToggle(Breakpoints.VerticalMode, DetailKind.Album,
                                                  InsightsSheet.FactsSettled(everSeen: true, slotNow: true), DetailContent.Tracks));
}
