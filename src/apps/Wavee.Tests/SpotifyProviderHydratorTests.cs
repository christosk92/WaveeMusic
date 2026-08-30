using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;
using static Wavee.Tests.HydrationTestSupport;

namespace Wavee.Tests;

// The façade itself (design §2.3): what a batch COSTS, what a second ask costs, and what comes back for a uri no
// ladder owns. These are the request-count properties the whole refactor is justified by.
public class SpotifyProviderHydratorTests
{
    sealed class Harness : IDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly HydrationPump Pump = new(CancellationToken.None);
        public readonly RecordingTraitPipeline Traits = new();
        public readonly FakeEnvelopeFetch Envelopes = new();
        public FakeCatalogFetch Catalog = null!;
        public SpotifyProviderHydrator Hydrator = null!;

        public Harness(HydrationLevel projectTo = HydrationLevel.Open, HydrationPolicy? policy = null)
        {
            Catalog = new FakeCatalogFetch(Store, (uris, store) =>
            {
                foreach (var u in uris)
                    switch (u.Kind)
                    {
                        case EntityKind.Track: store.UpsertTrack(TrackAt(u.Uri, projectTo)); break;
                        case EntityKind.Episode: store.UpsertEpisode(EpisodeAt(u.Uri, projectTo)); break;
                        case EntityKind.Album:
                            store.UpsertAlbum(new Album(u.Id, u.Uri, "Album " + u.Id, null,
                                Array.Empty<ArtistRef>(), 2020, 0));
                            break;
                    }
            });
            Hydrator = HydrationTestSupport.Hydrator(Store, Catalog, Traits, Pump,
                [new PlayableHydration(EntityKind.Track, Store, Envelopes),
                 new PlayableHydration(EntityKind.Episode, Store, Envelopes)],
                policy);
        }

        public void Dispose() => Pump.Dispose();
    }

    [Fact]
    public async Task MixedBatch_CostsExactlyOneCataloguePass()
    {
        using var h = new Harness();
        var uris = new[] { "spotify:track:t1", "spotify:track:t2", "spotify:episode:e1" };

        var outcome = await h.Hydrator.EnsureManyAsync(uris, HydrationLevel.Open);
        await DrainAsync(h.Pump);

        // ONE POST for the whole mixed batch — the property extended-metadata's many-entities-×-many-kinds request
        // makes possible and the four separate services never used.
        Assert.Equal(1, h.Catalog.Batches.Count(b => b.Count == 3));
        Assert.Equal(3, outcome.Reached.Count);
        Assert.Empty(outcome.Missing);
        Assert.Equal(HydrationStatus.Reached, outcome.Status);
    }

    [Fact]
    public async Task SecondAsk_WithinTtl_MakesNoRequest()
    {
        using var h = new Harness();
        var uris = new[] { "spotify:track:t1", "spotify:track:t2" };

        await h.Hydrator.EnsureManyAsync(uris, HydrationLevel.Open);
        await DrainAsync(h.Pump);
        int after = h.Catalog.Calls;
        await h.Hydrator.EnsureManyAsync(uris, HydrationLevel.Open);
        await DrainAsync(h.Pump);

        Assert.Equal(after, h.Catalog.Calls);   // warm = 0 requests
    }

    [Fact]
    public async Task Revalidate_IgnoresTheSeal()
    {
        using var h = new Harness();
        var uris = new[] { "spotify:track:t1" };

        await h.Hydrator.EnsureManyAsync(uris, HydrationLevel.Open);
        await DrainAsync(h.Pump);
        int after = h.Catalog.Calls;
        await h.Hydrator.EnsureManyAsync(uris, HydrationLevel.Open, new HydrationOptions(Revalidate: true));
        await DrainAsync(h.Pump);

        Assert.Equal(after + 1, h.Catalog.Calls);
    }

    [Fact]
    public async Task Background_ReturnsBeforeTheWork_ThenThePumpLands()
    {
        using var h = new Harness();
        var uris = new[] { "spotify:track:t1" };

        var outcome = await h.Hydrator.EnsureManyAsync(uris, HydrationLevel.Open,
            new HydrationOptions(HydrationMode.Background));

        // Nothing is resident yet: the caller gets the CURRENT answer and repaints off IStore.Changes later.
        Assert.Equal(HydrationStatus.Partial, outcome.Status);
        Assert.Equal("spotify:track:t1", Assert.Single(outcome.Missing));

        await DrainAsync(h.Pump);
        Assert.True(h.Catalog.Calls >= 1);
        Assert.Equal(HydrationLevel.Rich, h.Hydrator.LevelOf("spotify:track:t1"));   // Rich ≡ Open for a playable
    }

    [Fact]
    public async Task NoLadder_IsUnsupported_AndNeverReachesTheTransport()
    {
        using var h = new Harness();
        var outcome = await h.Hydrator.EnsureManyAsync(
            ["spotify:concert:c1", "wavee:skeleton:x"], HydrationLevel.Identity);

        Assert.Equal(HydrationStatus.Unsupported, outcome.Status);
        Assert.Equal(2, outcome.Missing.Count);
        Assert.Equal(0, h.Catalog.Calls);
        Assert.Equal(HydrationLevel.None, h.Hydrator.LevelOf("spotify:concert:c1"));
    }

    [Fact]
    public async Task UnreachableLevel_IsPartial_AndSealedExhausted()
    {
        // The catalogue can only get this track to Identity, and there is no envelope to repair it.
        using var h = new Harness(projectTo: HydrationLevel.Identity);
        var outcome = await h.Hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open);
        await DrainAsync(h.Pump);

        Assert.Equal(HydrationStatus.Partial, outcome.Status);
        Assert.Equal("spotify:track:t1", Assert.Single(outcome.Missing));

        int after = h.Catalog.Calls;
        await h.Hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open);
        await DrainAsync(h.Pump);
        // The exhausted seal is what stops the same thin row being re-asked on every heartbeat.
        Assert.Equal(after, h.Catalog.Calls);
    }

    [Fact]
    public async Task Invalidate_ReopensAnExhaustedSeal()
    {
        using var h = new Harness(projectTo: HydrationLevel.Identity);
        await h.Hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open);
        await DrainAsync(h.Pump);
        int after = h.Catalog.Calls;

        h.Hydrator.Invalidate("spotify:track:t1");
        await h.Hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Open);
        await DrainAsync(h.Pump);

        Assert.Equal(after + 1, h.Catalog.Calls);
    }

    [Fact]
    public async Task TwoSurfaces_SameUris_ShareOneRun()
    {
        using var h = new Harness();
        var uris = new[] { "spotify:track:t1", "spotify:track:t2" };

        var a = h.Hydrator.EnsureManyAsync(uris, HydrationLevel.Open, new HydrationOptions(Surface: TraitSurface.AlbumOpen));
        var b = h.Hydrator.EnsureManyAsync(uris, HydrationLevel.Open, new HydrationOptions(Surface: TraitSurface.Queue));
        await Task.WhenAll(a, b);
        await DrainAsync(h.Pump);

        // The ledger's per-(uri, level) dedupe means the second caller joins the first's pass rather than duplicating it.
        Assert.Equal(1, h.Catalog.Calls);
    }

    [Fact]
    public async Task EnsureTraits_UsesThePolicyForTheSurface()
    {
        using var h = new Harness();
        await h.Hydrator.EnsureTraitsAsync(["spotify:track:t1"], TraitSurface.AlbumOpen);

        var call = Assert.Single(h.Traits.Calls);
        Assert.Equal(TraitSurface.AlbumOpen, call.Surface);
        Assert.Equal(TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing, call.Traits);
    }

    [Fact]
    public async Task TransportFailure_IsAnOutcome_NotAnException()
    {
        using var pump = new HydrationPump(CancellationToken.None);
        var s = new InMemoryStore();
        var catalog = new ThrowingCatalogFetch();
        var hydrator = HydrationTestSupport.Hydrator(s, catalog, new RecordingTraitPipeline(), pump,
            [new PlayableHydration(EntityKind.Track, s, new FakeEnvelopeFetch())]);

        var outcome = await hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Identity);

        Assert.Equal(HydrationStatus.Failed, outcome.Status);
        // Nothing sealed on a failure, so the next ask retries.
        await hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Identity);
        Assert.Equal(2, catalog.Calls);
    }

    sealed class ThrowingCatalogFetch : ICatalogFetch
    {
        public int Calls;
        public Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityUri> uris,
            IReadOnlyList<(string Uri, int Kind)>? extraKinds, TraitSurface surface, CancellationToken ct, bool revalidate = false)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("extended-metadata fetch failed (503)");
        }
    }

    // ── a queued background ask re-PLANS when it runs ────────────────────────────────────────────────────────────────
    // The pump is a delay, so the plan a Background caller made can be answered before its slot frees. Replaying that
    // plan straight into the ladder run costs a second catalogue POST (and a second envelope) for a uri already sealed,
    // because the run itself only consults the in-flight map — which the finished run has already left. Two overlapping
    // background asks for the same uris is the everyday shape of this: the library warm-up wave, GetLikedSongsAsync
    // firing per render, the ref-closure re-entering its own uris.
    //
    // The gate + a single pump slot make the ORDER the bug needs deterministic: the first job cannot finish (so the
    // second caller genuinely sees no seal and really enqueues), and the second job cannot start until the first has
    // sealed (so it is the re-plan, not a race, that has to suppress it).
    sealed class GatedCatalogFetch(IStore store, Task gate) : ICatalogFetch
    {
        public int Calls;

        public async Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityUri> uris,
            IReadOnlyList<(string Uri, int Kind)>? extraKinds, TraitSurface surface, CancellationToken ct, bool revalidate = false)
        {
            Interlocked.Increment(ref Calls);
            await gate.ConfigureAwait(false);
            var landed = new List<string>(uris.Count);
            foreach (var u in uris) { store.UpsertTrack(TrackAt(u.Uri, HydrationLevel.Open)); landed.Add(u.Uri); }
            return landed;
        }
    }

    [Fact]
    public async Task TwoBackgroundAsks_ForTheSameUris_CostOneCataloguePass()
    {
        var store = new InMemoryStore();
        using var pump = new HydrationPump(CancellationToken.None, concurrency: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new GatedCatalogFetch(store, gate.Task);
        var hydrator = HydrationTestSupport.Hydrator(store, catalog, new RecordingTraitPipeline(), pump,
            [new PlayableHydration(EntityKind.Track, store, new FakeEnvelopeFetch())]);
        var uris = new[] { "spotify:track:t1", "spotify:track:t2" };
        var bg = new HydrationOptions(HydrationMode.Background);

        await hydrator.EnsureManyAsync(uris, HydrationLevel.Open, bg);
        await hydrator.EnsureManyAsync(uris, HydrationLevel.Open, bg);   // nothing can be sealed yet → really enqueued
        gate.SetResult();
        await DrainAsync(pump);

        Assert.Equal(1, Volatile.Read(ref catalog.Calls));
    }

    [Fact]
    public async Task NonSpotifyUris_AreUnsupported_AndNeverReachTheCatalogue()
    {
        using var h = new Harness();
        // Until the registry router lands, THIS hydrator is what Services.Hydrator hands every caller — and the queue,
        // the recents window and the Plays toggle all carry mixed uris. A `wavee:local:file:` uri is the local file's
        // PATH in base64url: sending it to extended-metadata is both a guaranteed miss and a leak.
        var outcome = await h.Hydrator.EnsureManyAsync(
            ["wavee:local:file:QzpcVXNlcnNcbWVcTXVzaWNcc29uZy5mbGFj", "local:track:l1", "fake:track:f1"],
            HydrationLevel.Open);
        await DrainAsync(h.Pump);

        Assert.Equal(HydrationStatus.Unsupported, outcome.Status);
        Assert.Equal(3, outcome.Missing.Count);
        Assert.Equal(0, h.Catalog.Calls);
    }
}
