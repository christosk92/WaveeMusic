using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Library;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using static Wavee.Tests.HydrationTestSupport;

namespace Wavee.Tests;

// Show paging end to end (design §2.3, plan P4-C). A show's members arrive WITH it, so everything above Identity is
// paging — and the bug this pins is that the page only ever showed the first 300: nothing asked for episode 301.
// Two halves: the LADDER pages the whole tail on the pump at Full, and the SOURCE exposes the foreground
// "the list reached the end" ask (LoadMoreEpisodesAsync) that the episode list drives.
public class ShowEpisodePagingTests
{
    const string ShowUri = "spotify:show:s700";
    const int Members = 700;

    static string Ep(int i) => "spotify:episode:e" + i;

    sealed class Harness : IDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly HydrationPump Pump = new(CancellationToken.None);
        public readonly FakeCatalogFetch Catalog;
        public readonly SpotifyProviderHydrator Hydrator;
        public readonly StoreLibrarySource Library;

        public Harness(int episodes)
        {
            Catalog = new FakeCatalogFetch(Store, (uris, store) =>
            {
                foreach (var u in uris)
                    switch (u.Kind)
                    {
                        case EntityKind.Show:
                            store.UpsertShow(new Show(u.Id, u.Uri, "The Show", "A Publisher", null));
                            store.SetMembership(u.Uri, Enumerable.Range(0, episodes)
                                .Select(i => new PlaylistMember("i" + i, Ep(i), null, 0)).ToArray(), null);
                            break;
                        case EntityKind.Episode:
                            store.UpsertEpisode(EpisodeAt(u.Uri, HydrationLevel.Open));
                            break;
                    }
            });
            var policy = new TraitPolicy();
            Hydrator = HydrationTestSupport.Hydrator(Store, Catalog, new RecordingTraitPipeline(), Pump,
                [new ShowHydration(Store, policy), new PlayableHydration(EntityKind.Episode, Store, new FakeEnvelopeFetch())],
                traitPolicy: policy);
            Library = new StoreLibrarySource(Store, new SwitchableEntityHydrator(Hydrator), OfflineOnlineCatalog.Instance);
        }

        public List<List<string>> EpisodePages => Catalog.Batches
            .Where(b => b.Count > 0 && b[0].StartsWith("spotify:episode:", StringComparison.Ordinal)).ToList();

        public int ResidentEpisodes
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Members; i++)
                    if (HydrationLevels.Of(Store.GetEpisode(Ep(i))) >= HydrationLevel.Open) n++;
                return n;
            }
        }

        public void Dispose() { Library.Dispose(); Pump.Dispose(); }
    }

    // THE regression: a 700-member show used to end at 300 resident episodes forever. Full pages the tail on the pump,
    // so after a drain every one of the 700 is at Episode.Open — in exactly 3 requests of 300/300/100.
    [Fact]
    public async Task Full_Pages700MembersInThreePages_AllAtOpen()
    {
        using var h = new Harness(Members);

        await h.Hydrator.EnsureAsync(ShowUri, HydrationLevel.Full);
        await DrainAsync(h.Pump);

        var pages = h.EpisodePages;
        Assert.Equal(3, pages.Count);
        // Sizes, not order: the two TAIL pages are independent pump jobs at the same priority, so which one lands
        // first is a scheduling detail. What is contractual is that there are exactly three and the last is the
        // remainder rather than a padded page.
        Assert.Equal(new[] { 100, 300, 300 }, pages.Select(p => p.Count).OrderBy(n => n).ToArray());
        Assert.Equal(Members, h.ResidentEpisodes);
        Assert.Equal(HydrationLevel.Full, h.Hydrator.LevelOf(ShowUri));
        // …and every member exactly once: a page boundary that double-counted would show up here as 800 asks.
        Assert.Equal(Members, pages.SelectMany(p => p).Distinct().Count());
        Assert.Equal(Members, pages.Sum(p => p.Count));
    }

    // The FOREGROUND ask the episode list drives. Driven off the LADDER at Open (not through GetShowAsync, which now
    // also fires the background Full — see below), so the request count is exactly the foreground one: each load-more
    // is one request for exactly the next 300, and the read joins what has landed.
    [Fact]
    public async Task LoadMoreEpisodes_PagesTheTailOnDemand_OneRequestPerPage()
    {
        using var h = new Harness(Members);

        await h.Hydrator.EnsureAsync(ShowUri, HydrationLevel.Open);
        Assert.Single(h.EpisodePages);
        Assert.Equal(HydrationLevels.ShowOpenPage, h.ResidentEpisodes);

        Assert.Equal(600, await h.Library.LoadMoreEpisodesAsync(ShowUri, HydrationLevels.ShowOpenPage));
        Assert.Equal(2, h.EpisodePages.Count);
        Assert.Equal(300, h.EpisodePages[1].Count);
        Assert.Equal(600, h.ResidentEpisodes);

        Assert.Equal(Members, await h.Library.LoadMoreEpisodesAsync(ShowUri, 600));
        Assert.Equal(100, h.EpisodePages[2].Count);           // the LAST page is the remainder, not a padded 300
        Assert.Equal(Members, h.ResidentEpisodes);

        // Past the end there is nothing to ask for — the cursor comes back UNMOVED, which is what drops the list's
        // load-more affordance (hasMore = PagedThrough < TotalEpisodes).
        Assert.Equal(Members, await h.Library.LoadMoreEpisodesAsync(ShowUri, Members));
        Assert.Equal(3, h.EpisodePages.Count);
    }

    // OpenPolicy.For(Show) is (Open, Full) and GetShowAsync now HONORS both arms (design §2.1/§2.3): the head page is
    // awaited because it is the primary content, and the tail is a Background ask that pages on the pump. It used to
    // drop the background half on the floor, so a 700-episode show sat at 300 rows until the user tapped twice.
    [Fact]
    public async Task Open_AwaitsTheHeadPage_AndAsksForFullInTheBackground()
    {
        var store = new InMemoryStore();
        store.UpsertShow(new Show("s700", ShowUri, "The Show", "A Publisher", null));
        store.SetMembership(ShowUri, Enumerable.Range(0, Members)
            .Select(i => new PlaylistMember("i" + i, Ep(i), null, 0)).ToArray(), null);
        var rec = new RecordingHydrator(store);
        using var library = new StoreLibrarySource(store, new SwitchableEntityHydrator(rec), OfflineOnlineCatalog.Instance);

        var show = await library.GetShowAsync(ShowUri);

        Assert.NotNull(show);
        Assert.Equal(Members, show!.TotalEpisodes);
        Assert.Equal(2, rec.Batches.Count);
        // (1) Open, BLOCKING — the page the user is looking at.
        Assert.Equal(HydrationLevel.Open, rec.Batches[0].Level);
        Assert.Equal(HydrationMode.Blocking, rec.Options[0].Mode);
        Assert.Equal(TraitSurface.ShowOpen, rec.Batches[0].Surface);
        // (2) Full, BACKGROUND — ShowHydration pages the remaining members on the pump.
        Assert.Equal(HydrationLevel.Full, rec.Batches[1].Level);
        Assert.Equal(HydrationMode.Background, rec.Options[1].Mode);
    }

    // …and end to end: after the pump drains, the whole 700 are resident WITHOUT anyone tapping load-more.
    [Fact]
    public async Task Open_PagesTheWholeTailOnThePump()
    {
        using var h = new Harness(Members);

        var opened = await h.Library.GetShowAsync(ShowUri);
        Assert.NotNull(opened);
        Assert.Equal(Members, opened!.TotalEpisodes);
        await DrainAsync(h.Pump);

        Assert.Equal(Members, h.ResidentEpisodes);
        var reread = await h.Library.GetShowAsync(ShowUri);
        Assert.Equal(Members, reread!.Episodes!.Count);
        // Everything asked for ⇒ the cursor is at the end ⇒ the list offers no load-more.
        Assert.Equal(Members, reread.PagedThrough);
    }

    [Fact]
    public async Task LoadMoreEpisodes_OnAShorterThanOnePageShow_IsAlreadyDone()
    {
        using var h = new Harness(episodes: 12);
        var show = await h.Library.GetShowAsync(ShowUri);

        Assert.Equal(12, show!.Episodes!.Count);
        Assert.Equal(12, show.TotalEpisodes);                 // resident == total ⇒ no affordance
        Assert.Equal(12, show.PagedThrough);
        Assert.Equal(12, await h.Library.LoadMoreEpisodesAsync(ShowUri, 12));
    }

    // THE load-more regression (finding 4). Members that can never hydrate — a withdrawn or region-locked episode —
    // keep the RESIDENT count permanently below the membership count. The old gate compared those two numbers, so the
    // pill stayed on screen forever and every tap re-asked the same unanswerable block from `eps.Count`. The cursor
    // advances on the ASK, so one page walks past them and `PagedThrough == TotalEpisodes` retires the affordance.
    [Fact]
    public async Task LoadMoreEpisodes_AdvancesPastMembersThatCannotHydrate()
    {
        const int members = 8;
        var store = new InMemoryStore();
        store.UpsertShow(new Show("s8", ShowUri, "The Show", "A Publisher", null));
        store.SetMembership(ShowUri, Enumerable.Range(0, members)
            .Select(i => new PlaylistMember("i" + i, Ep(i), null, 0)).ToArray(), null);
        // The first five land; the last three are the ones the catalogue will never answer for.
        for (int i = 0; i < 5; i++) store.UpsertEpisode(EpisodeAt(Ep(i), HydrationLevel.Open));
        var rec = new RecordingHydrator(store);   // resolves nothing — the tail stays missing however often it is asked
        using var library = new StoreLibrarySource(store, new SwitchableEntityHydrator(rec), OfflineOnlineCatalog.Instance);

        var show = await library.GetShowAsync(ShowUri);
        Assert.Equal(5, show!.Episodes!.Count);
        Assert.Equal(members, show.TotalEpisodes);
        Assert.Equal(5, show.PagedThrough);            // derived floor: one past the last member that HAS a row

        int through = await library.LoadMoreEpisodesAsync(ShowUri, show.PagedThrough);
        Assert.Equal(members, through);                // …the ask walked to the end even though nothing landed
        var after = await library.GetShowAsync(ShowUri);
        Assert.Equal(5, after!.Episodes!.Count);       // still five resident — the three are genuinely unavailable
        Assert.Equal(members, after.PagedThrough);     // …but the affordance is retired instead of looping forever
    }

    // Opening a show is a recent-surface PIN reason: without it the cache GC is free to purge the membership the
    // ladder just paid for, and a revisited show re-pages from nothing. Driven against a REAL CachedStore, because
    // IStore.RecordRecentSurface is a no-op DEFAULT member — an in-memory store would "pass" this test doing nothing.
    [Fact]
    public async Task Open_RecordsTheShowAsARecentSurface_OnThePersistedStore()
    {
        var cold = new CachedStoreTests.MemCold();
        using var store = new CachedStore(cold);
        await store.WarmComplete;
        using var pump = new HydrationPump(CancellationToken.None);
        var catalog = new FakeCatalogFetch(store, (uris, s) =>
        {
            foreach (var u in uris)
                if (u.Kind == EntityKind.Show)
                {
                    s.UpsertShow(new Show(u.Id, u.Uri, "The Show", "A Publisher", null));
                    s.SetMembership(u.Uri, Enumerable.Range(0, 5)
                        .Select(i => new PlaylistMember("i" + i, Ep(i), null, 0)).ToArray(), null);
                }
                else if (u.Kind == EntityKind.Episode) s.UpsertEpisode(EpisodeAt(u.Uri, HydrationLevel.Open));
        });
        var policy = new TraitPolicy();
        var hydrator = HydrationTestSupport.Hydrator(store, catalog, new RecordingTraitPipeline(), pump,
            [new ShowHydration(store, policy), new PlayableHydration(EntityKind.Episode, store, new FakeEnvelopeFetch())],
            traitPolicy: policy);

        await hydrator.EnsureAsync(ShowUri, HydrationLevel.Open);
        for (int i = 0; i < 200 && cold.Recent.Count == 0; i++) await Task.Delay(10);   // the pin rides the write lane

        var pin = Assert.Single(cold.Recent);
        Assert.Equal(ShowUri, pin.Uri);
        Assert.Equal((int)Wavee.Backend.Metadata.EntityKind.Show, pin.Kind);
    }
}
