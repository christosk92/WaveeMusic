using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;
using static Wavee.Tests.HydrationTestSupport;

namespace Wavee.Tests;

// ── "we could not ask" is not "there is nothing to get" (HydrationRunScope) ──────────────────────────────────────────
// Every ladder step is best-effort: a transport that throws is logged and swallowed so a renderable page still paints.
// The seal then reads the RESULT — the rung was not reached — and files it as EXHAUSTED, which for an album's Rich rung
// is a 24-hour verdict ("this release simply carries no publishing facet", which does not change). Those two facts
// compose into a bug with a very long tail: one 503 on the trait POST cost an album its ©/℗ line AND its RowBundle for
// a full day, and made the RowBundle re-ask impossible until the seal aged out.
//
// The run's failure channel closes the gap. A step that swallowed a TRANSPORT failure says so, the seal takes the short
// window instead, and the very next open retries. A step that ran clean and simply found nothing keeps the long window,
// because that answer really is stable — the pair of tests below is deliberately symmetric about exactly that.
public class HydrationTransientFailureTests
{
    const string AlbumUri = "spotify:album:al1";

    /// <summary>An album that is OPEN and cannot get further on its own: named, tracked, named rows, no ©/℗. Reaching
    /// Rich from here is exactly what the awaited trait pass is for — so a trait pass that dies leaves the rung short,
    /// which is the situation under test.</summary>
    static Album OpenAlbum() => new("al1", AlbumUri, "Al1", null, Array.Empty<ArtistRef>(), 2020, 1,
        Tracks: [new Track("t1", "spotify:track:t1", "Song t1", Array.Empty<ArtistRef>(),
                           new AlbumRef("al1", AlbumUri, "Al1"), 200_000, false, null)],
        Hydration: AlbumHydrationLevel.Tracks);

    sealed class Harness : IDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly HydrationPump Pump = new(CancellationToken.None);
        public readonly RecordingTraitPipeline Traits = new();
        public readonly FakeEnvelopeFetch Envelopes = new();
        public readonly FakeCatalogFetch Catalog;
        public readonly SpotifyProviderHydrator Hydrator;

        public Harness()
        {
            Store.UpsertAlbum(OpenAlbum());
            Catalog = new FakeCatalogFetch(Store);   // step 0 is a no-op: the album is already resident and Open
            // The exhausted window for everything EXCEPT a clean album Rich is zeroed, so "was it sealed short?" is
            // observable as "did the very next ask re-run?" without a clock seam.
            Hydrator = HydrationTestSupport.Hydrator(Store, Catalog, Traits, Pump,
                [new AlbumHydration(Store, Envelopes)],
                HydrationPolicy.Default with { ExhaustedPlayableTtl = TimeSpan.Zero });
        }

        public void Dispose() => Pump.Dispose();
    }

    [Fact]
    public async Task ATraitPassThatThrows_IsRetriedByTheNextAsk_NotSealedForADay()
    {
        using var h = new Harness();
        h.Traits.Throw = true;

        var first = await h.Hydrator.EnsureAsync(AlbumUri, HydrationLevel.Rich);
        Assert.Equal(HydrationStatus.Partial, first.Status);   // the ladder ran and fell short — but not because the
                                                               // album has nothing to give
        h.Traits.Throw = false;
        await h.Hydrator.EnsureAsync(AlbumUri, HydrationLevel.Rich);

        // TWO trait passes: the failure did not earn the "genuinely absent" seal, so the retry really happened.
        Assert.Equal(2, h.Traits.Calls.Count);
    }

    [Fact]
    public async Task ACleanPassThatSimplyFindsNoPublishing_StaysSealed()
    {
        // The symmetric half, and the reason the fix is a channel rather than "never seal a Partial": most albums that
        // do not reach Rich are perfectly healthy releases with no publishing facet at all, and re-asking those on every
        // open is the waste the exhausted seal was introduced to remove.
        using var h = new Harness();

        await h.Hydrator.EnsureAsync(AlbumUri, HydrationLevel.Rich);
        await h.Hydrator.EnsureAsync(AlbumUri, HydrationLevel.Rich);

        Assert.Single(h.Traits.Calls);
    }

    [Fact]
    public async Task TheTransientMarkIsPerRun_NotSticky()
    {
        // The scope is minted per ladder pass, so a failure in one run must not soften the seal of the next. If it
        // leaked (an ambient/AsyncLocal scope, or one hung off the shared context), the clean run below would seal
        // short and the album would be re-asked forever.
        using var h = new Harness();
        h.Traits.Throw = true;
        await h.Hydrator.EnsureAsync(AlbumUri, HydrationLevel.Rich);

        h.Traits.Throw = false;
        await h.Hydrator.EnsureAsync(AlbumUri, HydrationLevel.Rich);   // retry: runs, still cannot reach Rich, clean
        await h.Hydrator.EnsureAsync(AlbumUri, HydrationLevel.Rich);   // …and is now sealed on the long window

        Assert.Equal(2, h.Traits.Calls.Count);
    }

    [Fact]
    public async Task AGetTrackRepairThatThrows_IsRetriedByTheNextAsk()
    {
        // Same channel on the playable ladder: getTrack IS the Open rung for a row TrackV4 left thin, so a swallowed
        // socket error there would otherwise seal "this row is as good as it gets" for the whole exhausted window.
        var store = new InMemoryStore();
        using var pump = new HydrationPump(CancellationToken.None);
        var envelopes = new FakeEnvelopeFetch { Throw = true };
        var catalog = new FakeCatalogFetch(store, (uris, s) =>
        {
            foreach (var u in uris) s.UpsertTrack(TrackAt(u.Uri, HydrationLevel.Identity));
        });
        var hydrator = HydrationTestSupport.Hydrator(store, catalog, new RecordingTraitPipeline(), pump,
            [new PlayableHydration(EntityKind.Track, store, envelopes)],
            HydrationPolicy.Default with { ExhaustedPlayableTtl = TimeSpan.Zero });

        await hydrator.EnsureAsync("spotify:track:t1", HydrationLevel.Open);
        await hydrator.EnsureAsync("spotify:track:t1", HydrationLevel.Open);
        await DrainAsync(pump);

        Assert.Equal(2, envelopes.TrackCalls.Count);
    }

    [Fact]
    public async Task AnOverviewThatThrows_IsRetriedByTheNextAsk()
    {
        // And on the artist ladder: queryArtistOverview IS the Rich rung.
        var store = new InMemoryStore();
        using var pump = new HydrationPump(CancellationToken.None);
        var envelopes = new FakeEnvelopeFetch { Throw = true };
        store.UpsertArtist(new Artist("ar1", "spotify:artist:ar1", "Ar1", null,
            TopAlbums: [new Album("al1", AlbumUri, "Al1", null, Array.Empty<ArtistRef>(), 2020, 0)], AlbumsTotal: 1));
        var hydrator = HydrationTestSupport.Hydrator(store, new FakeCatalogFetch(store), new RecordingTraitPipeline(),
            pump, [new ArtistHydration(store, envelopes, new FakeArtistChartFetch())],
            HydrationPolicy.Default with { ExhaustedPlayableTtl = TimeSpan.Zero });

        await hydrator.EnsureAsync("spotify:artist:ar1", HydrationLevel.Rich);
        await hydrator.EnsureAsync("spotify:artist:ar1", HydrationLevel.Rich);
        await DrainAsync(pump);

        Assert.Equal(2, envelopes.OverviewCalls.Count);
    }
}
