using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// HomeFeedReadiness + HomeRevealGate: Home reveals ONCE, from the feed the session settles on. The recording behind
/// this (issue #53): the page revealed the cached "Jump back in" + library shelves 30 ms after mount while the session
/// was still connecting, a lone "No charts right now" chrome row painted under it, the notification timeline popped in
/// once the session went live, and 1.5 s after launch the live feed replaced the lot — chips appeared, the hero and
/// weekly pair pushed the cached grid 350 px down, every row remounted. These tests drive that exact launch sequence
/// through the pure gate and pin: exactly one reveal, from the live feed, with the chrome; nothing after it reveals
/// again. HomePage (engine-bound, not source-included) is the one caller.
/// </summary>
public class HomeFeedReadinessTests
{
    // ── Classify: the three-way decision ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_LiveCatalogAttemptNotConcluded_IsPlaceholder_EvenWithCachedGroups()
    {
        // The pre-GoLive window: the resident library shelves are a real read, but a PROVISIONAL one — the live feed
        // that supersedes them is seconds away. Painting them is the "cached grid, then everything jumps" recording.
        Assert.Equal(HomeFeedState.Placeholder, HomeFeedReadiness.Classify(groupCount: 4, liveCatalogConcluded: false));
        Assert.Equal(HomeFeedState.Placeholder, HomeFeedReadiness.Classify(groupCount: 0, liveCatalogConcluded: false));
    }

    [Fact]
    public void Classify_Concluded_WithGroups_IsReady()
    {
        // The live document after go-live — or a returning, currently-OFFLINE user's cached shelves: the attempt
        // concluded (Offline), so the shelves ARE the settled feed and reveal once from cache.
        Assert.Equal(HomeFeedState.Ready, HomeFeedReadiness.Classify(groupCount: 38, liveCatalogConcluded: true));
        Assert.Equal(HomeFeedState.Ready, HomeFeedReadiness.Classify(groupCount: 1, liveCatalogConcluded: true));
    }

    [Fact]
    public void Classify_Concluded_ZeroGroups_IsEmpty()
    {
        // A brand-new account after go-live, or an offline user with nothing resident: publish the real empty state.
        Assert.Equal(HomeFeedState.Empty, HomeFeedReadiness.Classify(groupCount: 0, liveCatalogConcluded: true));
    }

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
        Assert.False(HomeFeedReadiness.MayReveal(true, chromeConcluded: false, HomeFeedReadiness.ChromeSettleMs - 1));
        Assert.True(HomeFeedReadiness.MayReveal(true, chromeConcluded: false, HomeFeedReadiness.ChromeSettleMs));
    }

    [Fact]
    public void MayReveal_Force_SkipsTheChromeWait()
        => Assert.True(HomeFeedReadiness.MayReveal(true, chromeConcluded: false, msSinceSettled: 0, force: true));

    // ── ApplyEpoch: the monotonic epoch/placeholder bookkeeping ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyEpoch_Placeholder_IsWithheld_AndLeavesAppliedEpochUnchanged()
    {
        // The read at epoch 0 while connecting — even one carrying the cached shelves — is withheld, and critically
        // the applied epoch stays at its "nothing landed yet" sentinel, not the epoch this read attempted.
        var (publish, appliedEpoch) = HomeFeedReadiness.ApplyEpoch(
            appliedEpoch: -1, epoch: 0, groupCount: 4, liveCatalogConcluded: false);
        Assert.False(publish);
        Assert.Equal(-1, appliedEpoch);
    }

    [Fact]
    public void ApplyEpoch_WithheldPlaceholder_NeverBlocksALaterReadAtTheSameEpoch()
    {
        // Go-live publishes no epoch bump for the session's opening read (LiveHomeCache.GetAsync withholds it
        // deliberately), so the live feed arrives at the SAME epoch (0) as the withheld read that preceded it. The
        // gate must not mistake that for a stale repeat.
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
        Assert.Equal(2, appliedEpoch);   // unchanged — the newer answer is not clobbered
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

/// <summary>The reveal state machine driven through the recorded launch (times are the log's own: mount at 0, the
/// cached shelves at +30 ms, go-live at +855 ms, the live feed at +1495 ms) and its variants.</summary>
public class HomeRevealGateTests
{
    const string Shelves = "cached shelves (QuickGrid=9 Shelf×3)";
    const string Live = "live feed (38 groups, chips=3, hero=daylist)";

    static HomeRevealGate<string> Gate() => new();

    // The launch as recorded, up to and including the settled feed landing while the chrome is still loading.
    static HomeRevealGate<string> LaunchUntilLiveFeed(HomeRevealGate<string> g, out HomeRevealVerdict shelves, out HomeRevealVerdict live)
    {
        // +30 ms: the mount read — the online catalog is still the offline stub, so it is the resident shelves.
        shelves = g.Offer(epoch: 0, Shelves, groupCount: 4, faceted: false, liveCatalogConcluded: false,
            force: false, alreadyResolved: false, chromeConcluded: false, nowMs: 30);
        // +1495 ms: the AuthState-flip re-read (same epoch 0 — go-live bumps nothing) with the live document; the
        // Charts deck armed at +855 and is still in flight.
        live = g.Offer(epoch: 0, Live, groupCount: 38, faceted: false, liveCatalogConcluded: true,
            force: false, alreadyResolved: false, chromeConcluded: false, nowMs: 1495);
        return g;
    }

    [Fact]
    public void RecordedLaunch_CachedShelvesAreWithheld_LiveFeedIsHeldForTheChrome_ThenRevealsOnce()
    {
        var g = LaunchUntilLiveFeed(Gate(), out var shelves, out var live);
        Assert.Equal(HomeRevealVerdict.Withheld, shelves);   // no early reveal of the cached grid
        Assert.Equal(HomeRevealVerdict.Held, live);          // settled, waiting on Charts / the timeline
        Assert.True(g.IsHolding);
        Assert.False(g.Revealed);

        // The chrome effect ticks while Charts is still Pending: nothing.
        Assert.Null(g.Tick(chromeConcluded: false, nowMs: 1600));
        // Charts concluded at +1800 ms: THE reveal, with the live feed.
        Assert.Equal(Live, g.Tick(chromeConcluded: true, nowMs: 1800));
        Assert.True(g.Revealed);
        Assert.False(g.IsHolding);
        // Idempotent: the cap timer firing after the chrome already released it publishes nothing a second time.
        Assert.Null(g.Tick(chromeConcluded: true, nowMs: 2995));
    }

    [Fact]
    public void RecordedLaunch_SlowChrome_IsCappedAtChromeSettleMs_AfterTheFeedSettled()
    {
        var g = LaunchUntilLiveFeed(Gate(), out _, out _);
        double cap = 1495 + HomeFeedReadiness.ChromeSettleMs;
        Assert.Null(g.Tick(chromeConcluded: false, nowMs: cap - 1));
        Assert.Equal(Live, g.Tick(chromeConcluded: false, nowMs: cap));   // the deck can shimmer in later; the page cannot wait longer
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

        // The 60 s poll (same epoch), a daylist rollover (epoch 1), a facet tap: all swaps, none held.
        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(0, "poll", 38, false, true, false, alreadyResolved: true, chromeConcluded: true, 61_500));
        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(1, "rollover", 38, false, true, false, alreadyResolved: true, chromeConcluded: false, 90_000));
        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(1, "music facet", 12, faceted: true, true, false, alreadyResolved: true, chromeConcluded: false, 95_000));
        Assert.Equal(1, g.AppliedEpoch);
        Assert.False(g.IsHolding);
        Assert.Null(g.Tick(chromeConcluded: true, nowMs: 99_000));
    }

    [Fact]
    public void ANewerSettledFeed_ReplacesTheHeldOne_BeforeTheReveal()
    {
        var g = LaunchUntilLiveFeed(Gate(), out _, out _);
        // An epoch bump lands while the first live feed is still held: the newer feed is what reveals.
        Assert.Equal(HomeRevealVerdict.Held, g.Offer(1, "live feed v2", 39, false, true, false, false, chromeConcluded: false, 1600));
        Assert.Equal("live feed v2", g.Tick(chromeConcluded: true, nowMs: 1700));
        Assert.Equal(1, g.AppliedEpoch);
    }

    [Fact]
    public void StaleEpoch_IsWithheld_EvenAfterTheReveal()
    {
        var g = Gate();
        g.Offer(2, Live, 38, false, true, false, false, chromeConcluded: true, 0);
        Assert.Equal(HomeRevealVerdict.Withheld, g.Offer(1, "superseded in-flight read", 38, false, true, false, true, true, 10));
        Assert.Equal(2, g.AppliedEpoch);
    }

    [Fact]
    public void OfflineReturningUser_RevealsOnceFromTheCachedShelves()
    {
        // The silent resume fails against a retained credential: AuthState concludes Offline, the re-read carries the
        // shelves, and Charts (an empty deck offline) concluded instantly.
        var g = Gate();
        Assert.Equal(HomeRevealVerdict.Withheld, g.Offer(0, Shelves, 4, false, liveCatalogConcluded: false, false, false, false, 30));
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(0, Shelves, 4, false, liveCatalogConcluded: true, false, false, chromeConcluded: true, 2200));
        Assert.True(g.Revealed);
    }

    [Fact]
    public void FreshEmptyAccount_RevealsTheEmptyStateOnce()
    {
        var g = Gate();
        Assert.Equal(HomeRevealVerdict.Withheld, g.Offer(0, "placeholder", 0, false, liveCatalogConcluded: false, false, false, false, 30));
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(0, "empty live feed", 0, false, liveCatalogConcluded: true, false, false, chromeConcluded: true, 1500));
        // The page painted EmptyState; a later poll with a first playlist is a swap into the real page, not a reveal.
        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(0, "first shelf", 1, false, true, false, alreadyResolved: true, true, 61_500));
    }

    [Fact]
    public void HardFallback_ForcesTheBestAnswerOnHand_ThroughBothGates()
    {
        // A resume stuck at Connecting for 8 s: nothing has settled, but the mount read was seen. ForceRelease hands
        // back that read at its epoch; a forced Offer publishes it as the reveal, skipping the chrome hold.
        var g = Gate();
        g.Offer(0, Shelves, 4, false, liveCatalogConcluded: false, false, false, false, 30);
        var (epoch, feed) = g.ForceRelease();
        Assert.Equal(0, epoch);
        Assert.Equal(Shelves, feed);
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(epoch, feed!, 4, false, liveCatalogConcluded: true, force: true, false, chromeConcluded: false, 8000));
        Assert.True(g.Revealed);
    }

    [Fact]
    public void HardFallback_PrefersAHeldSettledFeed_OverTheLastSeenRead()
    {
        // The live feed settled and is waiting on a wedged chart read when the 8 s fallback fires: the settled feed
        // is the better answer, and force skips the chrome wait.
        var g = LaunchUntilLiveFeed(Gate(), out _, out _);
        var (epoch, feed) = g.ForceRelease();
        Assert.Equal(0, epoch);
        Assert.Equal(Live, feed);
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(epoch, feed!, 38, false, true, force: true, false, chromeConcluded: false, 8000));
    }

    [Fact]
    public void HardFallback_WithNothingSeen_HandsBackNoFeed()
    {
        var (epoch, feed) = Gate().ForceRelease();
        Assert.Equal(-1, epoch);
        Assert.Null(feed);   // the page offers HomeFeed.Empty in its place
    }

    [Fact]
    public void ARegionResolvedByAnotherPath_CountsAsRevealed()
    {
        // The initial read FAILED (home.SetFailed painted the error state — a real branch). The next successful poll
        // must swap the page in, not hold it for the chrome as if nothing were on screen.
        var g = Gate();
        Assert.Equal(HomeRevealVerdict.Swap, g.Offer(0, Live, 38, false, true, false, alreadyResolved: true, chromeConcluded: false, 61_000));
        Assert.False(g.IsHolding);
    }

    [Fact]
    public void ALoopStartedWhileConnecting_NeverSettlesThePage_HoweverLateItsReadLands()
    {
        // The vantage is the loop's START: a read that was in flight across the go-live flip carries the shelves (its
        // online catalog was the offline stub) and stays provisional even though, by the time it lands, the attempt
        // has concluded. HomePage passes the vantage captured in the effect, so the gate simply sees `false` here.
        var g = Gate();
        Assert.Equal(HomeRevealVerdict.Withheld, g.Offer(0, Shelves, 4, false, liveCatalogConcluded: false, false, false, chromeConcluded: true, 900));
        Assert.Equal(-1, g.AppliedEpoch);
        Assert.Equal(HomeRevealVerdict.Reveal, g.Offer(0, Live, 38, false, liveCatalogConcluded: true, false, false, chromeConcluded: true, 1495));
    }
}
