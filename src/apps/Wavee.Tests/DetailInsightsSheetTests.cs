// ── Wavee.Tests/DetailInsightsSheetTests.cs — which arm hosts the facts bento, and how wide its sheet is ────────────
//
// A playlist and Liked Songs reach their facts bento ONLY through the Insights toggle, in EVERY layout: a dismissable sheet
// laid OVER the content, closed on arrival and never opened by anything but the toggle's own click. `Detail.InsightsSheet` is
// the whole decision, extracted so it can be tested without an engine: WHETHER a toggle exists (mode-free), whether its slot is
// reserved in the table's command bar (route-static), WHICH of the vertical arm's two entry points owns input at a given scroll
// position, what the sheet's resolved width is for a page of a given
// width, what the band's action cluster then claims, and what happens to an open sheet when the page stops being able to show it.
//
// The facts that must not regress:
//   . the sheet is the ONLY host: the rail no longer renders the bento inline and nothing appends it to the page body;
//   . the toggle is gated on the facts actually being there, so a page with none shows no button, and it is the same in every
//     mode (a window resize never moves the bento between hosts);
//   . the command bar's toggle SLOT is reserved by kind and content, never by data, so a late fact adds no button and no reflow;
//   . in the vertical arm the command bar and the pinned band each carry that one toggle, and EXACTLY ONE of them is live at
//     any scroll position;
//   . an absent facts slot means "not answered yet", not "no facts": the frame latches it for the route.

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

    // ── who hosts the bento ──

    /// <summary>The sheet hosts the bento for a facts-bearing kind, and ONLY when the page has offered facts. There is no mode
    /// parameter: every arm answers the same.</summary>
    [Theory]
    [InlineData(DetailKind.Liked, true, true)]
    [InlineData(DetailKind.Playlist, true, true)]
    [InlineData(DetailKind.Liked, false, false)]
    [InlineData(DetailKind.Album, true, false)]
    [InlineData(DetailKind.Show, true, false)]
    public void TheSheetHostsTheBentoInEveryArm(DetailKind kind, bool slot, bool expected)
        => Assert.Equal(expected, InsightsSheet.SheetHostsFacts(kind, slot));

    /// <summary>The page body is never a host, and the rail no longer renders the bento inline: nothing appends it anywhere.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(Breakpoints.VerticalMode)]
    [InlineData(7)]
    public void NothingAppendsTheFactsToThePageBody(int mode)
    {
        foreach (var kind in new[] { DetailKind.Liked, DetailKind.Playlist, DetailKind.Album, DetailKind.Show, DetailKind.Episode })
            foreach (bool slot in new[] { false, true })
                Assert.False(InsightsSheet.AppendsFactsToPageBody(mode, kind, slot));
    }

    /// <summary>A page whose facts have not arrived has no host at all, never an empty panel, and never a button that opens one.</summary>
    [Fact]
    public void NoFactsMeansNoHostAndNoButton()
    {
        Assert.False(InsightsSheet.SheetHostsFacts(DetailKind.Liked, factsSlot: false));
        Assert.False(InsightsSheet.ShowsToggle(DetailKind.Liked, factsSlot: false, DetailContent.Tracks));
    }

    // ── the toggle ──

    /// <summary>The toggle exists on a facts-bearing TRACK page whose facts have arrived, in every arm (the arm is not an input).</summary>
    [Theory]
    [InlineData(DetailKind.Liked, true, DetailContent.Tracks, true)]
    [InlineData(DetailKind.Playlist, true, DetailContent.Tracks, true)]
    [InlineData(DetailKind.Album, true, DetailContent.Tracks, false)]
    [InlineData(DetailKind.Liked, false, DetailContent.Tracks, false)]
    // an episode list has no bento, and nothing with episodes gets a toggle
    [InlineData(DetailKind.Show, true, DetailContent.Episodes, false)]
    [InlineData(DetailKind.Playlist, true, DetailContent.Episodes, false)]
    [InlineData(DetailKind.Liked, true, DetailContent.Episodes, false)]
    public void TheToggleIsModeFree(DetailKind kind, bool slot, DetailContent content, bool expected)
        => Assert.Equal(expected, InsightsSheet.ShowsToggle(kind, slot, content));

    /// <summary>The toggle is composed exactly where the sheet is the host: one predicate, many readers.</summary>
    [Fact]
    public void TheToggleFollowsTheSheetHost()
        => Assert.Equal(InsightsSheet.SheetHostsFacts(DetailKind.Playlist, factsSlot: true),
                        InsightsSheet.ShowsToggle(DetailKind.Playlist, factsSlot: true, DetailContent.Tracks));

    /// <summary>The command bar's toggle slot is RESERVED by kind and content alone (never by the facts), so the bar's fit
    /// (<c>CommandBarLayout.Resolve(hasInsights)</c>) is decided from the first frame and the facts arriving only fade the
    /// button in. A playlist and Liked reserve it; an album and a podcast do not.</summary>
    [Theory]
    [InlineData(DetailKind.Playlist, DetailContent.Tracks, true)]
    [InlineData(DetailKind.Liked, DetailContent.Tracks, true)]
    [InlineData(DetailKind.Album, DetailContent.Tracks, false)]
    [InlineData(DetailKind.Show, DetailContent.Episodes, false)]
    [InlineData(DetailKind.Playlist, DetailContent.Episodes, false)]
    public void TheCommandBarsToggleSlotIsReservedByKind(DetailKind kind, DetailContent content, bool expected)
        => Assert.Equal(expected, InsightsSheet.ToggleSlotReserved(kind, content));

    /// <summary>The reservation is a superset of the toggle: wherever the toggle can ever show, its slot is already there.</summary>
    [Fact]
    public void TheReservationCoversEveryToggle()
    {
        foreach (var kind in new[] { DetailKind.Liked, DetailKind.Playlist, DetailKind.Album, DetailKind.Show, DetailKind.Episode })
            foreach (var content in new[] { DetailContent.Tracks, DetailContent.Episodes })
                foreach (bool slot in new[] { false, true })
                    if (InsightsSheet.ShowsToggle(kind, slot, content))
                        Assert.True(InsightsSheet.ToggleSlotReserved(kind, content), $"{kind}/{content} shows a toggle without a slot");
    }

    /// <summary>The command bar's width budget reserves the toggle exactly where the kind reserves its slot: at the width that
    /// holds Sort without a toggle, a reserving kind (playlist, Liked) has already given 32 DIP and a gap to it, so Sort
    /// moves into "…" there, while an album keeps it inline.</summary>
    [Theory]
    [InlineData(DetailKind.Playlist, DetailContent.Tracks)]
    [InlineData(DetailKind.Liked, DetailContent.Tracks)]
    [InlineData(DetailKind.Album, DetailContent.Tracks)]
    public void TheToolbarBudgetReservesTheSlotWhereTheKindDoes(DetailKind kind, DetailContent content)
    {
        bool reserved = InsightsSheet.ToggleSlotReserved(kind, content);
        var widths = new Track.CommandWidths(92, 156);
        float sortFitsWithoutToggle = Track.CommandBarLayout.MoreWidth + Track.CommandBarLayout.SearchGap
            + Track.CommandBarLayout.SearchIconWidth + Track.CommandBarLayout.Gap + widths.Sort;

        var fit = Track.CommandBarLayout.Resolve(sortFitsWithoutToggle, widths, hasTune: false, hasInsights: reserved, explicitSearch: false);
        Assert.Equal(!reserved, fit.Has(Track.InlineCommand.Sort));

        var wider = Track.CommandBarLayout.Resolve(sortFitsWithoutToggle + Track.CommandBarLayout.InsightsWidth + Track.CommandBarLayout.Gap,
                                                   widths, hasTune: false, hasInsights: reserved, explicitSearch: false);
        Assert.True(wider.Has(Track.InlineCommand.Sort));
    }

    // ── the live open state ──

    /// <summary>An open sheet SURVIVES only while its toggle does. Losing the facts closes it, so coming back never restores a
    /// sheet the user cannot remember leaving open. A resize no longer closes it: the toggle is mode-free.</summary>
    [Fact]
    public void AnOpenSheetSurvivesOnlyWhileItsToggleDoes()
    {
        Assert.True(InsightsSheet.OpenFor(true, DetailKind.Liked, true, DetailContent.Tracks));
        Assert.False(InsightsSheet.OpenFor(true, DetailKind.Liked, false, DetailContent.Tracks));
        Assert.False(InsightsSheet.OpenFor(true, DetailKind.Album, true, DetailContent.Tracks));
    }

    /// <summary>A closed sheet is never opened by the facts, the route or the width alone: only the toggle's click writes Open.</summary>
    [Theory]
    [InlineData(DetailKind.Liked, true)]
    [InlineData(DetailKind.Playlist, true)]
    [InlineData(DetailKind.Playlist, false)]
    public void AClosedSheetStaysClosed(DetailKind kind, bool slot)
        => Assert.False(InsightsSheet.OpenFor(false, kind, slot, DetailContent.Tracks));

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

    /// <summary>The one case the rule leaves the band's word dead: not yet stuck.</summary>
    [Fact]
    public void TheBandsWordIsDeadOnlyInThePageBeforeItSticks()
    {
        Assert.False(InsightsSheet.BandToggleTakesInput(bandStuck: false));
        Assert.True(InsightsSheet.BandToggleTakesInput(bandStuck: true));
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
        Assert.True(InsightsSheet.ShowsToggle(DetailKind.Liked, settled, DetailContent.Tracks));
        Assert.True(InsightsSheet.SheetHostsFacts(DetailKind.Liked, settled));
    }

    /// <summary>An album never gains a toggle from the latch either — the kind gate is ahead of it.</summary>
    [Fact]
    public void TheLatchNeverGivesTheAlbumFamilyAToggle()
        => Assert.False(InsightsSheet.ShowsToggle(DetailKind.Album,
                                                  InsightsSheet.FactsSettled(everSeen: true, slotNow: true), DetailContent.Tracks));
}
