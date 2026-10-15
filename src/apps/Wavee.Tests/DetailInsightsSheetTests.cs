// ── Wavee.Tests/DetailInsightsSheetTests.cs — where the facts bento lives, and how wide its sheet is ─────────────────
//
// Tuned Classic I1: on EVERY facts page (Liked and every playlist, followed ones included — owner NQ6) and in every arm the
// facts bento is a sheet laid over the list, closed by default, never opening by itself, toggled from the list bar. The rail
// no longer carries it. `Detail.InsightsSheet` is the core decision (WHETHER a toggle exists, the facts latch, the band's
// claim); the sheet's width is Classic's (`ClassicDetailLayout.SheetWidthFor`, the board's 320).
//
// The facts that must not regress:
//   · the sheet is the ONE host, in every arm (no rail row, no page-body footer);
//   · the toggle is gated on the facts actually being there, so a page with none shows no button;
//   · an absent facts slot means "not answered yet", not "no facts": the frame latches it for the route.

using Xunit;
using Wavee.Themes.Classic;
using InsightsSheet = Wavee.Detail.InsightsSheet;
using BandLayout = Wavee.Detail.BandLayout;

namespace Wavee.Tests;

public class DetailInsightsSheetTests
{
    // ── the resolved width (Classic's) ──

    /// <summary>An UNMEASURED page answers the board's 320 — the pre-measure seed the first bounds callback corrects.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void AnUnmeasuredPageTakesTheBoardWidth(float pageWidth)
        => Assert.Equal(320f, ClassicDetailLayout.SheetWidthFor(pageWidth));

    /// <summary>A page wide enough gets 320 outright (production's 390 sheet is gone); the fraction only ever narrows.</summary>
    [Theory]
    [InlineData(2000f)]
    [InlineData(1024f)]
    [InlineData(579f)]
    [InlineData(422f)]      // 422 × 0.76 = 320.72 → 320 is the smaller of the two
    public void AWidePageGetsTheBoardWidth(float pageWidth)
        => Assert.Equal(320f, ClassicDetailLayout.SheetWidthFor(pageWidth));

    /// <summary>Below that, the sheet is a SHARE of the page, so a strip of the list always stays visible beside it.</summary>
    [Theory]
    [InlineData(400f, 304f)]
    [InlineData(360f, 273f)]     // 273.6 → floored to whole DIP
    [InlineData(320f, 243f)]     // 243.2
    public void ANarrowPageGetsTheFraction(float pageWidth, float expected)
        => Assert.Equal(expected, ClassicDetailLayout.SheetWidthFor(pageWidth));

    [Theory]
    [InlineData(2000f)]
    [InlineData(400f)]
    [InlineData(300f)]
    [InlineData(120f)]
    public void TheSheetNeverCoversTheWholePage(float pageWidth)
    {
        float w = ClassicDetailLayout.SheetWidthFor(pageWidth);
        Assert.True(w > 0f);
        Assert.True(w <= ClassicDetailLayout.SheetWidth);
        Assert.True(w < pageWidth, $"the sheet must leave a strip of the list at {pageWidth}");
    }

    /// <summary>A degenerate page still answers a positive width — the pane is composed before it is ever opened.</summary>
    [Fact]
    public void AnAbsurdlyNarrowPageStillAnswersAPositiveWidth()
        => Assert.True(ClassicDetailLayout.SheetWidthFor(1f) > 0f);

    // ── which kinds carry facts at all ──

    [Theory]
    [InlineData(DetailKind.Liked, true)]
    [InlineData(DetailKind.Playlist, true)]
    [InlineData(DetailKind.Album, false)]
    [InlineData(DetailKind.Show, false)]
    [InlineData(DetailKind.Episode, false)]
    public void OnlyTheTwoListLikeSurfacesCarryFacts(DetailKind kind, bool expected)
        => Assert.Equal(expected, InsightsSheet.KindHasFacts(kind));

    // ── the sheet is the one host ──

    /// <summary>The sheet hosts the bento of every facts page, whatever the arm (the mode no longer enters the decision).</summary>
    [Theory]
    [InlineData(DetailKind.Liked, true)]
    [InlineData(DetailKind.Playlist, true)]
    [InlineData(DetailKind.Album, false)]
    public void TheSheetHostsTheBentoOfEveryFactsPage(DetailKind kind, bool expected)
        => Assert.Equal(expected, InsightsSheet.SheetHostsFacts(kind, factsSlot: true));

    /// <summary>A page whose facts have not arrived has no host at all — never an empty panel, and never a button that
    /// opens one (<c>User.FactsHas</c> answers the slot, and the slot answers this).</summary>
    [Fact]
    public void NoFactsMeansNoHostAndNoButton()
    {
        Assert.False(InsightsSheet.SheetHostsFacts(DetailKind.Liked, factsSlot: false));
        Assert.False(InsightsSheet.ShowsToggle(DetailKind.Liked, factsSlot: false, DetailContent.Tracks));
    }

    // ── the list bar's toggle ──

    /// <summary>The toggle exists on every facts-bearing TRACK page (Liked, owned and followed playlists), never on an album or an
    /// episode list.</summary>
    [Theory]
    [InlineData(DetailKind.Liked, true, DetailContent.Tracks, true)]
    [InlineData(DetailKind.Playlist, true, DetailContent.Tracks, true)]
    [InlineData(DetailKind.Album, true, DetailContent.Tracks, false)]
    [InlineData(DetailKind.Liked, false, DetailContent.Tracks, false)]
    [InlineData(DetailKind.Show, true, DetailContent.Episodes, false)]
    [InlineData(DetailKind.Playlist, true, DetailContent.Episodes, false)]
    public void TheToggleIsEveryFactsPagesListBars(DetailKind kind, bool slot, DetailContent content, bool expected)
        => Assert.Equal(expected, InsightsSheet.ShowsToggle(kind, slot, content));

    /// <summary>The toggle is composed exactly where the sheet is the host — one predicate, every reader.</summary>
    [Theory]
    [InlineData(DetailKind.Liked)]
    [InlineData(DetailKind.Playlist)]
    [InlineData(DetailKind.Album)]
    public void TheToggleFollowsTheSheetHost(DetailKind kind)
        => Assert.Equal(InsightsSheet.SheetHostsFacts(kind, factsSlot: true),
                        InsightsSheet.ShowsToggle(kind, factsSlot: true, DetailContent.Tracks));

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
