// ── Wavee.Tests/HomeReturnTests.cs — a returning Home paints what the graph already knows (Wave 5, owner P) ─────────
//
// The bug this pins: a KEEP-ALIVE EVICTION destroys HomeLandingView's page-local Loadable/HomeRevealGate, but never
// touches the entity graph — the sections a facet already painted this session are still sitting in the Homes /
// Sections tables. Before this fix, a remounted page always re-seeded `Loadable.Pending(seed)` and re-paid the FULL
// reveal gate (the 1,500 ms chrome hold, the group-coordinated skeleton), so a page with everything it needs already
// in memory shimmered for seconds anyway. `HomeFeedReadiness.ShouldPaintOnMount` is the extracted decision
// (`Home.cs`); `Home.Feeds.HasRevealed`/`HasChartsRevealed` are the session-scoped marks that let it tell "shown
// before, safe to paint stale-while-refreshing" apart from a genuine cold boot's disk-warmed-but-unverified rows
// (the exact hazard `Home.Classify` exists to catch — see `HomeTests.Warm_rows_do_not_make_the_page_ready`). This
// file never reads Home.Page.cs's source text: the pure rule and the session marks are exercised directly, and the
// planner fact is read off the SAME `Asked`/`Known` columns `HomeBrowseCardsTests` uses, never a mock.

using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── the pure rule ────────────────────────────────────────────────────────────────────────────────────────────────────

[Collection(EntitiesCollection.Name)]
public class HomeFeedReadinessShouldPaintOnMountTests
{
    [Fact]
    public void Known_sections_plus_a_refresh_in_flight_paint_no_skeleton()
        // "Revealed this session" does not care whether a background refresh currently has Inflight set again —
        // that is the whole point of stale-while-revalidate: paint the graph's held content, let the refresh land
        // in place.
        => Assert.True(HomeFeedReadiness.ShouldPaintOnMount(groupCount: 6, revealedThisSession: true));

    [Fact]
    public void Nothing_landed_and_never_revealed_is_a_skeleton()
        => Assert.False(HomeFeedReadiness.ShouldPaintOnMount(groupCount: 0, revealedThisSession: false));

    [Fact]
    public void ColdBoot_diskWarmed_groups_never_revealed_this_session_still_skeletons()
        // The Store.Warm hazard Home.Classify exists to catch: a disk-loaded row can carry Known sections before its
        // first live check ever concluded. groupCount alone must never be trusted — only a session mark that was
        // set from the real reveal path earns the fast paint.
        => Assert.False(HomeFeedReadiness.ShouldPaintOnMount(groupCount: 12, revealedThisSession: false));

    [Fact]
    public void Revealed_with_nothing_currently_known_does_not_paint_a_bogus_state()
        => Assert.False(HomeFeedReadiness.ShouldPaintOnMount(groupCount: 0, revealedThisSession: true));
}

// ── the session-scoped marks (Home.Host.cs `Feeds`) ─────────────────────────────────────────────────────────────────

[Collection(EntitiesCollection.Name)]
public class HomeFeedsRevealMarkTests
{
    [Fact]
    public void A_fresh_scope_has_never_revealed_anything()
    {
        TestScope.Fresh();
        Assert.False(Home.Feeds.HasRevealed(""));
        Assert.False(Home.Feeds.HasRevealed("music-chip"));
        Assert.False(Home.Feeds.HasChartsRevealed());
    }

    [Fact]
    public void MarkRevealed_is_per_facet_and_survives_repeated_reads()
    {
        TestScope.Fresh();
        Home.Feeds.MarkRevealed("");
        Assert.True(Home.Feeds.HasRevealed(""));
        Assert.False(Home.Feeds.HasRevealed("music-chip"));   // a different facet is a different mark

        Home.Feeds.MarkRevealed("music-chip");
        Assert.True(Home.Feeds.HasRevealed("music-chip"));
        Assert.True(Home.Feeds.HasRevealed(""));               // the first mark is untouched
    }

    [Fact]
    public void MarkChartsRevealed_is_independent_of_the_facet_marks()
    {
        TestScope.Fresh();
        Home.Feeds.MarkRevealed("");
        Assert.False(Home.Feeds.HasChartsRevealed());

        Home.Feeds.MarkChartsRevealed();
        Assert.True(Home.Feeds.HasChartsRevealed());
    }

    [Fact]
    public void A_new_scope_never_inherits_a_stale_mark()
    {
        // A remount inside the SAME scope must see the mark (that is the whole fix); a genuinely new scope — a
        // re-auth, a different account — must not, or a cold boot on a fresh account would wrongly fast-paint.
        TestScope.Fresh();
        Home.Feeds.MarkRevealed("");
        Home.Feeds.MarkChartsRevealed();
        Assert.True(Home.Feeds.HasRevealed(""));

        TestScope.Fresh();   // a brand new Scope instance — Entities.Current changes reference
        Assert.False(Home.Feeds.HasRevealed(""));
        Assert.False(Home.Feeds.HasChartsRevealed());
    }
}

// ── the end-to-end fact: a remount resolves the same landed sections, no new ask ────────────────────────────────────

[Collection(EntitiesCollection.Name)]
public class HomeRemountResolvesWithoutANewAskTests : IDisposable
{
    public HomeRemountResolvesWithoutANewAskTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
    }

    [Fact]
    public void A_remount_after_eviction_resolves_the_same_landed_sections_from_the_host_without_a_new_ask()
    {
        TestScope.Fresh();

        // Land a real section the way the boot mount would: staged + committed through HomeFixtures (the same
        // commit path a decoded answer takes), attached to the unfiltered Home row, and the row's HomeFields
        // stamped Known — mirroring Entities.Fake.Home.cs's SeedHomeDocument, never a page-side shortcut.
        var section = HomeFixtures.Band(HomeFixtures.NextSectionUri(), "Jump back in", SectionKind.HomeGeneric,
            HomeFixtures.Playlist("spotify:playlist:home-return-1", "Return Mix"));

        var home = Entities.HomeFeed();
        Entities.Current.Edges.HomeSection.ReplaceRun(home.Slot, [section.Slot], default);
        Entities.Current.Homes.Bump(home.Slot, (uint)HomeFields.All);
        int cardSlot = Entities.Current.Playlists.Slot("spotify:playlist:home-return-1".AsSpan());

        // The FIRST mount: EnsureFeed is the page's real demand call (Home.Page.cs's FeedTick/MountDemand), which
        // also asks each card's OWN row — HomeFixtures only stages Identity, so this legitimately asks for the rest
        // (PlaylistFields.Row) the first time. That ask is not what this fact is about; settle it before measuring.
        Home.EnsureFeed(home);

        var landed = HomeComposer.For(home, HomeModuleTitles.Default);
        Assert.True(landed.Groups.Count > 0);
        Home.Feeds.MarkRevealed(landed.Facet);

        // A returning page's warm-start seed must see it.
        Assert.True(HomeFeedReadiness.ShouldPaintOnMount(landed.Groups.Count, Home.Feeds.HasRevealed(landed.Facet)));

        uint homeAskedBefore = Entities.Current.Homes.Asked[home.Slot];
        uint homeInflightBefore = Entities.Current.Homes.Inflight[home.Slot];
        uint cardAskedBefore = Entities.Current.Playlists.Asked[cardSlot];
        uint cardInflightBefore = Entities.Current.Playlists.Inflight[cardSlot];

        // The remount: a brand new HomeLandingView instance calls EnsureFeed exactly as FeedTick does on every
        // mount (its demand key is page-local, so a fresh instance always re-issues the call) — trap 2 of
        // docs/plans/wavee/wavee-0.3-bug-handoff-2026-09-15.md: `Asked` is never cleared on a successful answer, so
        // NeedOf (`wanted & ~Known & ~Asked`) is 0 for BOTH the Home row and its card, and the planner returns
        // before touching Asked/Inflight at all (Fetch.cs's `if (need == 0) return;`).
        Home.EnsureFeed(home);

        Assert.Equal(homeAskedBefore, Entities.Current.Homes.Asked[home.Slot]);
        Assert.Equal(homeInflightBefore, Entities.Current.Homes.Inflight[home.Slot]);
        Assert.Equal(cardAskedBefore, Entities.Current.Playlists.Asked[cardSlot]);
        Assert.Equal(cardInflightBefore, Entities.Current.Playlists.Inflight[cardSlot]);

        // And the composer hands back the SAME landed sections — memoized, not recomposed from a re-fetch.
        var resolved = HomeComposer.For(home, HomeModuleTitles.Default);
        Assert.Same(landed, resolved);
        Assert.Equal(landed.Groups.Count, resolved.Groups.Count);
    }
}
