// ── Wavee.Tests/HomeReturnTests.cs — a returning Home paints what the graph already knows (Wave 5, owner P) ─────────
//
// The bug this pins: a KEEP-ALIVE EVICTION destroys HomeLandingView's page-local Loadable/HomeRevealGate, but never
// touches the entity graph — the sections a facet already painted this session are still sitting in the Homes /
// Sections tables. Before this fix, a remounted page always re-seeded `Loadable.Pending(seed)` and re-paid the FULL
// reveal gate (the 1,500 ms chrome hold, the group-coordinated skeleton), so a page with everything it needs already
// in memory shimmered for seconds anyway. This file pins the data half: a landed section survives a remount with no
// new ask, read off the SAME `Asked`/`Known` columns `HomeBrowseCardsTests` uses, never a mock.

using Wavee;
using Xunit;

namespace Wavee.Tests;

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

        // HomeComposer (the Wave-5 Home UI's presentation projection) is gone with the rest of that page
        // (docs/plans/wavee/home-rebuild-implementation.md §7); the fact under test — a landed section survives a
        // remount with no new ask — is read straight off the surviving ledger, `HomeSectionView.Of`.
        var landed = HomeSectionView.Of(section);
        Assert.True(landed.Cards.Count > 0);

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

        // And the ledger hands back the SAME landed section — memoized by (slot, Version, CardVersion), not
        // recomposed from a re-fetch.
        var resolved = HomeSectionView.Of(section);
        Assert.Same(landed, resolved);
        Assert.Equal(landed.Cards.Count, resolved.Cards.Count);
    }
}
