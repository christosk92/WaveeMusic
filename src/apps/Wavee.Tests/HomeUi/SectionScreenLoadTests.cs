// ── Wavee.Tests/HomeUi/SectionScreenLoadTests.cs — the drill page's load state + hero title (Home/SectionScreen.Rules.cs)
//
// RCA 2026-09-30: the page wrote its failure to a plain field its region never re-read, so a seal that landed while it
// was open left the skeleton up forever. The decision is now `SectionScreenLoadRule` (derived from the row's facts on
// every sync, never latched) and the region reads it reactively; these pin the decision and its transitions.
//
// The page demands its section WHOLE (`SectionFields.Identity | Whole` — the query layer walks it to its end), so a row
// that only knows a feed's or a page's first page is still Pending: the grid shows the whole list or nothing.

using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public sealed class SectionScreenLoadTests
{
    // The row's facts as the page's sync reads them; every case below names only what it changes.
    static SectionScreenLoad Load(bool rowValid = true, bool whole = false, int cardCount = 0, bool failed = false,
                                  bool inflight = false, bool asked = false, bool online = true)
        => SectionScreenLoadRule.Of(rowValid, whole, cardCount, failed, inflight, asked, online);

    [Fact]
    public void NotAskedYet_IsPending()
        => Assert.Equal(SectionScreenLoad.Pending, Load());

    [Fact]
    public void TheWalkInFlight_IsPending()
        => Assert.Equal(SectionScreenLoad.Pending, Load(inflight: true, asked: true));

    [Fact]
    public void AFirstPageFromTheFeed_IsNotTheWholeSection_StaysPending()
        // The row knows its identity and a feed's ten cards, but not its whole list: the walk is on the wire.
        => Assert.Equal(SectionScreenLoad.Pending, Load(whole: false, cardCount: 10, inflight: true, asked: true));

    [Fact]
    public void AskSealedWithoutAnAnswer_Online_IsFailed()
        => Assert.Equal(SectionScreenLoad.Failed, Load(asked: true));

    [Fact]
    public void AskSealedWithoutAnAnswer_Offline_StaysPending()
        => Assert.Equal(SectionScreenLoad.Pending, Load(asked: true, online: false));

    [Fact]
    public void ATerminalFailure_IsFailed()
        => Assert.Equal(SectionScreenLoad.Failed, Load(failed: true));

    [Fact]
    public void AReAskInFlight_WinsOverAnEarlierFailure()
        => Assert.Equal(SectionScreenLoad.Pending, Load(failed: true, inflight: true, asked: true));

    [Fact]
    public void TheWholeListWithCards_IsReady()
        => Assert.Equal(SectionScreenLoad.Ready, Load(whole: true, cardCount: 74, asked: true));

    [Fact]
    public void TheWholeListEmpty_IsEmpty()
        => Assert.Equal(SectionScreenLoad.Empty, Load(whole: true, cardCount: 0, asked: true));

    [Fact]
    public void TheWholeList_WinsOverAFailure()
        => Assert.Equal(SectionScreenLoad.Ready, Load(whole: true, cardCount: 3, failed: true, asked: true));

    [Fact]
    public void NoRow_IsFailed_NeverAnEndlessShimmer()
        => Assert.Equal(SectionScreenLoad.Failed, Load(rowValid: false));

    [Fact]
    public void OpenPage_FollowsTheWalk_FromPendingToReady()
    {
        // Mounted before the ask → the demand's walk on the wire (the feed's first page already in the row) → the whole
        // section lands.
        Assert.Equal(SectionScreenLoad.Pending, Load(cardCount: 10));
        Assert.Equal(SectionScreenLoad.Pending, Load(cardCount: 10, inflight: true, asked: true));
        Assert.Equal(SectionScreenLoad.Ready, Load(whole: true, cardCount: 60, asked: true));
    }

    [Fact]
    public void OpenPage_FollowsTheSeal_ThenTheRetry_ThenTheAnswer()
    {
        // Mounted before the ask → the demand's ask on the wire → the seal lands while the page is open (the RCA's
        // stuck skeleton) → Retry re-asks → the answer lands.
        Assert.Equal(SectionScreenLoad.Pending, Load());
        Assert.Equal(SectionScreenLoad.Pending, Load(inflight: true, asked: true));
        Assert.Equal(SectionScreenLoad.Failed, Load(asked: true));
        Assert.Equal(SectionScreenLoad.Pending, Load(inflight: true, asked: true));
        Assert.Equal(SectionScreenLoad.Ready, Load(whole: true, cardCount: 4, asked: true));
    }

    [Fact]
    public void Revisit_AfterTheSeal_IsFailedOnTheFirstRead()
        // A fresh page over a row already asked-and-unanswered this scope: its first sync decides Failed outright.
        => Assert.Equal(SectionScreenLoad.Failed, Load(asked: true));

    // ── the hero title ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Top 50 - Global", "Weekly Song Charts", "Top 50 - Global")]   // the route arg wins
    [InlineData("  Charts ", null, "Charts")]                                  // trimmed
    [InlineData(null, "Weekly Song Charts", "Weekly Song Charts")]             // no arg: the row's own title
    [InlineData("   ", " Daily Song Charts ", "Daily Song Charts")]            // a blank arg is no arg
    [InlineData(null, null, "")]
    [InlineData("", "  ", "")]
    public void Title_RouteArgFirst_ThenTheLiveTitle(string? routeArg, string? liveTitle, string expected)
        => Assert.Equal(expected, SectionScreenTitle.Of(routeArg, liveTitle));
}
