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

// The track/episode ladder (design §2.3). The two properties worth pinning are the ones the old code got wrong: the
// getTrack repair fires ONCE (not once per cluster heartbeat), and an EPISODE never asks for it at all.
public class PlayableHydrationTests
{
    sealed class Harness : IDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly HydrationPump Pump = new(CancellationToken.None);
        public readonly RecordingTraitPipeline Traits = new();
        public readonly FakeEnvelopeFetch Envelopes = new();
        public readonly FakeCatalogFetch Catalog;
        public readonly SpotifyProviderHydrator Hydrator;

        public Harness(Action<IReadOnlyList<EntityUri>, IStore> project)
        {
            Catalog = new FakeCatalogFetch(Store, project);
            Hydrator = HydrationTestSupport.Hydrator(Store, Catalog, Traits, Pump,
                [new PlayableHydration(EntityKind.Track, Store, Envelopes),
                 new PlayableHydration(EntityKind.Episode, Store, Envelopes),
                 // The ref-closure re-enters ALBUM uris, so the kind has to be registered for the batch to be answered
                 // at all; its real ladder is not what these tests are about.
                 new CatalogOnlyHydration(EntityKind.Album, Store)]);
        }

        public void Dispose() => Pump.Dispose();
    }

    [Fact]
    public async Task ThinTrack_GetsTheEnvelopeRepair_ExactlyOnce()
    {
        // TrackV4 leaves the row at Identity (the now-playing shape: a title, nothing else); getTrack completes it.
        using var h = new Harness((uris, store) =>
        {
            foreach (var u in uris) store.UpsertTrack(TrackAt(u.Uri, HydrationLevel.Identity));
        });
        h.Envelopes.OnTrack = uri => TrackAt(uri, HydrationLevel.Open);

        var first = await h.Hydrator.EnsureAsync("spotify:track:t1", HydrationLevel.Open);
        await DrainAsync(h.Pump);

        Assert.Equal(HydrationStatus.Reached, first.Status);
        Assert.Equal(1, h.Envelopes.TrackCalls.Count);
        Assert.Equal("spotify:track:t1", Assert.Single(h.Envelopes.TrackCalls));

        // The heartbeat: the projection re-asks on every cluster update and must cost nothing.
        int catalogCalls = h.Catalog.Calls;
        await h.Hydrator.EnsureAsync("spotify:track:t1", HydrationLevel.Open);
        await h.Hydrator.EnsureAsync("spotify:track:t1", HydrationLevel.Open);
        await DrainAsync(h.Pump);

        Assert.Equal(1, h.Envelopes.TrackCalls.Count);
        Assert.Equal(catalogCalls, h.Catalog.Calls);
    }

    [Fact]
    public async Task Episode_NeverAsksForAnEnvelope()
    {
        using var h = new Harness((uris, store) =>
        {
            foreach (var u in uris) store.UpsertEpisode(EpisodeAt(u.Uri, HydrationLevel.Identity));
        });

        var outcome = await h.Hydrator.EnsureAsync("spotify:episode:e1", HydrationLevel.Open);
        await DrainAsync(h.Pump);

        // An episode's ladder is EpisodeV4 and nothing else — there is no second transport for it, so a level it
        // cannot reach seals Partial rather than firing a track envelope at a podcast uri.
        Assert.Equal(HydrationStatus.Partial, outcome.Status);
        Assert.Equal(0, h.Envelopes.TrackCalls.Count);
    }

    // Mixed playables ride ONE catalogue POST — the whole point of registering the same ladder twice is that an episode
    // uri is planned, fetched and sealed exactly like a track uri instead of being dropped on the floor.
    [Fact]
    public async Task MixedPlayables_LandInOneCatalogueCall_AndTheEpisodeRowIsResident()
    {
        using var h = new Harness((uris, store) =>
        {
            foreach (var u in uris)
            {
                if (u.Kind == EntityKind.Episode) store.UpsertEpisode(EpisodeAt(u.Uri, HydrationLevel.Open));
                else store.UpsertTrack(TrackAt(u.Uri, HydrationLevel.Open));
            }
        });

        var outcome = await h.Hydrator.EnsureManyAsync(["spotify:track:t1", "spotify:episode:e1"], HydrationLevel.Identity);
        await DrainAsync(h.Pump);

        Assert.Equal(HydrationStatus.Reached, outcome.Status);
        Assert.Empty(outcome.Missing);
        Assert.Contains("spotify:episode:e1", outcome.Reached);
        Assert.Equal(1, h.Catalog.Calls);
        Assert.Contains("spotify:episode:e1", h.Catalog.Asked);
        Assert.Equal("Ep e1", h.Store.GetEpisode("spotify:episode:e1")!.Title);
        Assert.Empty(h.Catalog.Extras);   // a playable's whole catalogue answer is its V4; the facets are traits
    }

    [Fact]
    public async Task Identity_NeverAsksForAnEnvelope()
    {
        using var h = new Harness((uris, store) =>
        {
            foreach (var u in uris) store.UpsertTrack(TrackAt(u.Uri, HydrationLevel.Identity));
        });

        await h.Hydrator.EnsureManyAsync(["spotify:track:t1", "spotify:track:t2"], HydrationLevel.Identity);
        await DrainAsync(h.Pump);

        // A list-scale Identity wave must cost exactly its catalogue POST: getTrack is a single-entity envelope.
        Assert.Equal(0, h.Envelopes.TrackCalls.Count);
        Assert.Equal(1, h.Catalog.Calls);
    }

    [Fact]
    public async Task RefClosure_ReEntersNamelessAlbumRefs_AtIdentity()
    {
        const string album = "spotify:album:al9";
        using var h = new Harness((uris, store) =>
        {
            foreach (var u in uris)
            {
                if (u.Kind == EntityKind.Album) { store.UpsertAlbum(new Album(u.Id, u.Uri, "Album " + u.Id, null, Array.Empty<ArtistRef>(), 2020, 0)); continue; }
                // A cluster/library-shaped row: named, but its denormalized album ref carries no name.
                store.UpsertTrack(TrackAt(u.Uri, HydrationLevel.Open) with { Album = new AlbumRef("al9", album, "") });
            }
        });

        await h.Hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Identity);
        await DrainAsync(h.Pump);

        // The closure is what heals a library whose writers seeded name-less refs — it re-enters the ALBUM at Identity.
        Assert.Contains(album, h.Catalog.Asked);
    }

    [Fact]
    public async Task RefClosure_Terminates()
    {
        // Rows that stay thin forever: the closure re-enters them at Open, that pass seals, and the second wave finds
        // nothing to do. Without the seal this is an infinite fan-out (which is why MetadataService needed its own set).
        using var h = new Harness((uris, store) =>
        {
            foreach (var u in uris)
                store.UpsertTrack(new Track(u.Id, u.Uri, u.Uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null));
        });

        await h.Hydrator.EnsureManyAsync(["spotify:track:t1"], HydrationLevel.Identity);
        await DrainAsync(h.Pump);

        // Identity pass + the closure's one Open re-entry, and then it stops.
        Assert.InRange(h.Catalog.Calls, 1, 2);
        Assert.InRange(h.Envelopes.TrackCalls.Count, 0, 1);
    }

    [Fact]
    public void Ctor_RejectsANonPlayableKind()
    {
        var store = new InMemoryStore();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PlayableHydration(EntityKind.Album, store, new FakeEnvelopeFetch()));
    }
}
