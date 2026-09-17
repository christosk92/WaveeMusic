// ── Wavee.Tests/HomeFeedReadinessTests.cs — the reveal gate (Wave 5, owner P; ported from 0.2.9) ───────────────────────
//
// HomeFeedReadiness + HomeRevealGate: Home reveals ONCE, from the feed the session settles on. The recording behind this
// (issue #53): the page revealed the cached shelves 30 ms after mount while the session was still connecting, a lone
// chrome row painted under it, the timeline popped in once the session went live, and 1.5 s after launch the live feed
// replaced the lot. These facts drive that launch sequence through the pure gate and pin exactly one reveal, from the
// live feed, with the chrome. 0.2.9's `HomeFeedState` is 0.3's `HomeState`; every other name and number is verbatim.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class HomeFeedReadinessTests
{
    // ── Classify: the three-way decision ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_LiveCatalogAttemptNotConcluded_IsPlaceholder_EvenWithCachedGroups()
    {
        Assert.Equal(HomeState.Placeholder, HomeFeedReadiness.Classify(groupCount: 4, liveCatalogConcluded: false));
        Assert.Equal(HomeState.Placeholder, HomeFeedReadiness.Classify(groupCount: 0, liveCatalogConcluded: false));
    }

    [Fact]
    public void Classify_Concluded_WithGroups_IsReady()
    {
        Assert.Equal(HomeState.Ready, HomeFeedReadiness.Classify(groupCount: 38, liveCatalogConcluded: true));
        Assert.Equal(HomeState.Ready, HomeFeedReadiness.Classify(groupCount: 1, liveCatalogConcluded: true));
    }

    [Fact]
    public void Classify_Concluded_ZeroGroups_IsEmpty()
        => Assert.Equal(HomeState.Empty, HomeFeedReadiness.Classify(groupCount: 0, liveCatalogConcluded: true));

    // ── ShouldForceRelease: the 8 s hard fallback ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldForceRelease_BeforeTheWindow_IsFalse()
    {
        Assert.False(HomeFeedReadiness.ShouldForceRelease(0));
        Assert.False(HomeFeedReadiness.ShouldForceRelease(HomeFeedReadiness.ForceReleaseMs - 1));
    }

    [Fact]
    public void ShouldForceRelease_AtOrPastTheWindow_IsTrue()
    {
        Assert.True(HomeFeedReadiness.ShouldForceRelease(HomeFeedReadiness.ForceReleaseMs));
        Assert.True(HomeFeedReadiness.ShouldForceRelease(HomeFeedReadiness.ForceReleaseMs + 5000));
    }

    // ── MayReveal: the chrome gate on the first reveal ──────────────────────────────────────────────────────────────

    [Fact]
    public void MayReveal_NeverBeforeTheFeedSettled()
    {
        Assert.False(HomeFeedReadiness.MayReveal(feedSettled: false, chromeConcluded: true, msSinceSettled: 0));
        Assert.False(HomeFeedReadiness.MayReveal(feedSettled: false, chromeConcluded: true, msSinceSettled: 99_999));
    }

    [Fact]
    public void MayReveal_SettledFeed_WaitsForTheChrome_ThenRevealsAtOnce()
    {
        Assert.False(HomeFeedReadiness.MayReveal(feedSettled: true, chromeConcluded: false, msSinceSettled: 0));
        Assert.True(HomeFeedReadiness.MayReveal(feedSettled: true, chromeConcluded: true, msSinceSettled: 0));
    }

    [Fact]
    public void MayReveal_SlowChrome_IsCapped_NotWaitedForForever()
    {
        Assert.False(HomeFeedReadiness.MayReveal(true, false, HomeFeedReadiness.ChromeSettleMs - 1));
        Assert.True(HomeFeedReadiness.MayReveal(true, false, HomeFeedReadiness.ChromeSettleMs));
    }

    [Fact]
    public void MayReveal_Force_SkipsTheChromeWait()
        => Assert.True(HomeFeedReadiness.MayReveal(true, false, 0, force: true));

    // ── ApplyEpoch: the monotonic epoch/placeholder bookkeeping ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyEpoch_Placeholder_IsWithheld_AndLeavesAppliedEpochUnchanged()
    {
        var (publish, appliedEpoch) = HomeFeedReadiness.ApplyEpoch(
            appliedEpoch: -1, epoch: 0, groupCount: 4, liveCatalogConcluded: false);
        Assert.False(publish);
        Assert.Equal(-1, appliedEpoch);
    }

    [Fact]
    public void ApplyEpoch_WithheldPlaceholder_NeverBlocksALaterReadAtTheSameEpoch()
    {
        var withheld = HomeFeedReadiness.ApplyEpoch(appliedEpoch: -1, epoch: 0, groupCount: 4, liveCatalogConcluded: false);
        Assert.False(withheld.Publish);

        var landed = HomeFeedReadiness.ApplyEpoch(
            appliedEpoch: withheld.AppliedEpoch, epoch: 0, groupCount: 38, liveCatalogConcluded: true);
        Assert.True(landed.Publish);
        Assert.Equal(0, landed.AppliedEpoch);
    }

    [Fact]
    public void ApplyEpoch_EarlierEpochThanAlreadyApplied_IsDropped()
    {
        var (publish, appliedEpoch) = HomeFeedReadiness.ApplyEpoch(
            appliedEpoch: 2, epoch: 1, groupCount: 5, liveCatalogConcluded: true);
        Assert.False(publish);
        Assert.Equal(2, appliedEpoch);
    }

    [Fact]
    public void ApplyEpoch_Ready_AdvancesAppliedEpochAndPublishes()
    {
        var (publish, appliedEpoch) = HomeFeedReadiness.ApplyEpoch(
            appliedEpoch: 0, epoch: 1, groupCount: 3, liveCatalogConcluded: true);
        Assert.True(publish);
        Assert.Equal(1, appliedEpoch);
    }

    [Fact]
    public void ApplyEpoch_Force_PublishesAPlaceholderRatherThanWithholdingIt()
    {
        var (publish, appliedEpoch) = HomeFeedReadiness.ApplyEpoch(
            appliedEpoch: -1, epoch: 0, groupCount: 4, liveCatalogConcluded: false, force: true);
        Assert.True(publish);
        Assert.Equal(0, appliedEpoch);
    }

    [Fact]
    public void ApplyEpoch_Force_StillObeysTheEpochGate()
    {
        var (publish, appliedEpoch) = HomeFeedReadiness.ApplyEpoch(
            appliedEpoch: 2, epoch: 1, groupCount: 0, liveCatalogConcluded: false, force: true);
        Assert.False(publish);
        Assert.Equal(2, appliedEpoch);
    }
}

/// <summary>The reveal state machine driven through the recorded launch (mount at 0, the cached shelves at +30 ms, go-live
/// at +855 ms, the live feed at +1495 ms) and its variants.</summary>
[Collection(EntitiesCollection.Name)]
public class HomeRevealGateTests
{
    const string Shelves = "cached shelves (QuickGrid=9 Shelf×3)";
    const string Live = "live feed (38 groups, chips=3, hero=daylist)";

    static HomeRevealGate<string> Gate() => new();

    static HomeRevealGate<string> LaunchUntilLiveFeed(HomeRevealGate<string> g, out HomeRevealVerdict shelves, out HomeRevealVerdict live)
    {
        shelves = g.Offer(epoch: 0, Shelves, groupCount: 4, faceted: false, liveCatalogConcluded: false,
            force: false, alreadyResolved: false, chromeConcluded: false, nowMs: 30);
        live = g.Offer(epoch: 0, Live, groupCount: 38, faceted: false, liveCatalogConcluded: true,
            force: false, alreadyResolved: false, chromeConcluded: false, nowMs: 1495);
        return g;
    }

    [Fact]
    public void RecordedLaunch_CachedShelvesAreWithheld_LiveFeedIsHeldForTheChrome_ThenRevealsOnce()
    {
        var g = LaunchUntilLiveFeed(Gate(), out var shelves, out var live);
        Assert.Equal(HomeRevealVerdict.Withheld, shelves);
        Assert.Equal(HomeRevealVerdict.Held, live);
        Assert.True(g.IsHolding);
        Assert.False(g.Revealed);

        Assert.Null(g.Tick(chromeConcluded: false, nowMs: 1600));
        Assert.Equal(Live, g.Tick(chromeConcluded: true, nowMs: 1800));
        Assert.True(g.Revealed);
        Assert.False(g.IsHolding);
        Assert.Null(g.Tick(chromeConcluded: true, nowMs: 2995));
    }

    [Fact]
    public void RecordedLaunch_SlowChrome_IsCappedAtChromeSettleMs_AfterTheFeedSettled()
    {
        var g = LaunchUntilLiveFeed(Gate(), out _, out _);
        double cap = 1495 + HomeFeedReadiness.ChromeSettleMs;
        Assert.Null(g.Tick(chromeConcluded: false, nowMs: cap - 1));
        Assert.Equal(Live, g.Tick(chromeConcluded: false, nowMs: cap));
    }

    [Fact]
    public void ChromeAlreadyConcluded_WhenTheFeedSettles_RevealsInTheSameCall()
    {
        var g = Gate();
        var verdict = g.Offer(epoch: 0, Live, groupCount: 38, faceted: false, liveCatalogConcluded: true,
            force: false, alreadyResolved: false, chromeConcluded: true, nowMs: 1495);
        Assert.Equal(HomeRevealVerdict.Reveal, verdict);
        Assert.True(g.Revealed);
        Assert.Null(g.Tick(chromeConcluded: true, nowMs: 1500));
    }

    [Fact]
    public void AfterTheReveal_EveryLaterPublishIsASwap_NeverASecondReveal()
    {
        var g = LaunchUntilLiveFeed(Gate(), out _, out _);
        g.Tick(chromeConcluded: true, nowMs: 1800);

        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(0, "poll", 38, false, true, false, true, true, 61_500));
        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(1, "rollover", 38, false, true, false, true, false, 90_000));
        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(1, "music facet", 12, true, true, false, true, false, 95_000));
        Assert.Equal(1, g.AppliedEpoch);
        Assert.False(g.IsHolding);
        Assert.Null(g.Tick(chromeConcluded: true, nowMs: 99_000));
    }

    [Fact]
    public void ANewerSettledFeed_ReplacesTheHeldOne_BeforeTheReveal()
    {
        var g = LaunchUntilLiveFeed(Gate(), out _, out _);
        Assert.Equal(HomeRevealVerdict.Held, g.Offer(1, "live feed v2", 39, false, true, false, false, false, 1600));
        Assert.Equal("live feed v2", g.Tick(chromeConcluded: true, nowMs: 1700));
        Assert.Equal(1, g.AppliedEpoch);
    }

    [Fact]
    public void StaleEpoch_IsWithheld_EvenAfterTheReveal()
    {
        var g = Gate();
        g.Offer(2, Live, 38, false, true, false, false, true, 0);
        Assert.Equal(HomeRevealVerdict.Withheld, g.Offer(1, "superseded in-flight read", 38, false, true, false, true, true, 10));
        Assert.Equal(2, g.AppliedEpoch);
    }

    [Fact]
    public void OfflineReturningUser_RevealsOnceFromTheCachedShelves()
    {
        var g = Gate();
        Assert.Equal(HomeRevealVerdict.Withheld, g.Offer(0, Shelves, 4, false, false, false, false, false, 30));
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(0, Shelves, 4, false, true, false, false, true, 2200));
        Assert.True(g.Revealed);
    }

    [Fact]
    public void FreshEmptyAccount_RevealsTheEmptyStateOnce()
    {
        var g = Gate();
        Assert.Equal(HomeRevealVerdict.Withheld, g.Offer(0, "placeholder", 0, false, false, false, false, false, 30));
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(0, "empty live feed", 0, false, true, false, false, true, 1500));
        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(0, "first shelf", 1, false, true, false, true, true, 61_500));
    }

    [Fact]
    public void HardFallback_ForcesTheBestAnswerOnHand_ThroughBothGates()
    {
        var g = Gate();
        g.Offer(0, Shelves, 4, false, false, false, false, false, 30);
        var (epoch, feed) = g.ForceRelease();
        Assert.Equal(0, epoch);
        Assert.Equal(Shelves, feed);
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(epoch, feed!, 4, false, true, true, false, false, 8000));
        Assert.True(g.Revealed);
    }

    [Fact]
    public void HardFallback_PrefersAHeldSettledFeed_OverTheLastSeenRead()
    {
        var g = LaunchUntilLiveFeed(Gate(), out _, out _);
        var (epoch, feed) = g.ForceRelease();
        Assert.Equal(0, epoch);
        Assert.Equal(Live, feed);
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(epoch, feed!, 38, false, true, true, false, false, 8000));
    }

    [Fact]
    public void HardFallback_WithNothingSeen_HandsBackNoFeed()
    {
        var (epoch, feed) = Gate().ForceRelease();
        Assert.Equal(-1, epoch);
        Assert.Null(feed);
    }

    [Fact]
    public void ARegionResolvedByAnotherPath_CountsAsRevealed()
    {
        var g = Gate();
        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(0, Live, 38, false, true, false, true, false, 61_000));
        Assert.False(g.IsHolding);
    }

    [Fact]
    public void ALoopStartedWhileConnecting_NeverSettlesThePage_HoweverLateItsReadLands()
    {
        var g = Gate();
        Assert.Equal(HomeRevealVerdict.Withheld, g.Offer(0, Shelves, 4, false, false, false, false, true, 900));
        Assert.Equal(-1, g.AppliedEpoch);
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(0, Live, 38, false, true, false, false, true, 1495));
    }
}
