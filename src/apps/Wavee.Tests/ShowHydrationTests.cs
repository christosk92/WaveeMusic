using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using static Wavee.Tests.HydrationTestSupport;

namespace Wavee.Tests;

// The show ladder (design §2.3): a show's members arrive WITH it, so everything above Identity is paging — the first
// page awaited (it is the page on screen), the rest on the pump.
public class ShowHydrationTests
{
    const string Show = "spotify:show:s1";

    sealed class Harness : IDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly HydrationPump Pump = new(CancellationToken.None);
        public readonly RecordingTraitPipeline Traits = new();
        public readonly FakeCatalogFetch Catalog;
        public readonly SpotifyProviderHydrator Hydrator;

        public Harness(int episodes)
        {
            Catalog = new FakeCatalogFetch(Store, (uris, store) =>
            {
                foreach (var u in uris)
                    switch (u.Kind)
                    {
                        case EntityKind.Show:
                            // What ShowV4's projection does: the header AND the ordered membership in one payload.
                            store.UpsertShow(new Show(u.Id, u.Uri, "The Show", "A Publisher", null));
                            store.SetMembership(u.Uri, Enumerable.Range(0, episodes)
                                .Select(i => new PlaylistMember("i" + i, "spotify:episode:e" + i, null, 0)).ToArray(), null);
                            break;
                        case EntityKind.Episode:
                            store.UpsertEpisode(EpisodeAt(u.Uri, HydrationLevel.Open));
                            break;
                    }
            });
            var policy = new TraitPolicy();
            Hydrator = HydrationTestSupport.Hydrator(Store, Catalog, Traits, Pump,
                [new ShowHydration(Store, policy), new PlayableHydration(EntityKind.Episode, Store, new FakeEnvelopeFetch())],
                traitPolicy: policy);
        }

        public void Dispose() => Pump.Dispose();
    }

    [Fact]
    public async Task Open_HydratesTheFirstPage_InOnePass()
    {
        using var h = new Harness(episodes: 450);

        var outcome = await h.Hydrator.EnsureAsync(Show, HydrationLevel.Open);

        Assert.Equal(HydrationStatus.Reached, outcome.Status);
        // ShowV4 + exactly ONE episode page (the ShowOpenPage of 300 is deliberately the transport's entity ceiling).
        var episodePages = h.Catalog.Batches.Where(b => b.Count > 0 && b[0].StartsWith("spotify:episode:")).ToList();
        Assert.Single(episodePages);
        Assert.Equal(HydrationLevels.ShowOpenPage, episodePages[0].Count);
    }

    [Fact]
    public async Task Open_LeavesTheTailForFull()
    {
        using var h = new Harness(episodes: 450);
        await h.Hydrator.EnsureAsync(Show, HydrationLevel.Open);
        await DrainAsync(h.Pump);

        // Open ≡ "the first page is resident"; the show is Rich, not Full, until the tail lands.
        Assert.Equal(HydrationLevel.Rich, h.Hydrator.LevelOf(Show));
    }

    [Fact]
    public async Task Full_PagesTheTailOnThePump()
    {
        using var h = new Harness(episodes: 450);

        await h.Hydrator.EnsureAsync(Show, HydrationLevel.Full);
        await DrainAsync(h.Pump);

        Assert.Equal(HydrationLevel.Full, h.Hydrator.LevelOf(Show));
        var episodePages = h.Catalog.Batches.Where(b => b.Count > 0 && b[0].StartsWith("spotify:episode:")).ToList();
        Assert.Equal(2, episodePages.Count);           // 300 + 150
        Assert.Equal(150, episodePages[1].Count);
    }

    [Fact]
    public async Task EmptyShow_IsCompleteAtOpen()
    {
        using var h = new Harness(episodes: 0);
        var outcome = await h.Hydrator.EnsureAsync(Show, HydrationLevel.Open);

        // A baseline with no members is complete BY CONSTRUCTION — there is nothing left to page.
        Assert.Equal(HydrationStatus.Reached, outcome.Status);
        Assert.Equal(HydrationLevel.Full, h.Hydrator.LevelOf(Show));
    }

    [Fact]
    public async Task ShowRung_IsTheSameBodyOfflineAndOnline()
    {
        using var h = new Harness(episodes: 10);
        await h.Hydrator.EnsureAsync(Show, HydrationLevel.Open);
        await DrainAsync(h.Pump);

        // OfflineEntityHydrator delegates to ShowHydration.LevelOf — a divergence here is what would make a page
        // shimmer forever after a reconnect.
        var offline = new OfflineEntityHydrator(h.Store);
        Assert.Equal(h.Hydrator.LevelOf(Show), offline.LevelOf(Show));
    }
}
