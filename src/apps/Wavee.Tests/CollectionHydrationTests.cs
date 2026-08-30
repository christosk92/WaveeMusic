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

// The collection ladder (design §2.3): a saved SET has no entity of its own, so "hydrating" it means naming its
// members — paged at the transport's entity ceiling, in the background, addressed by uri.
public class CollectionHydrationTests
{
    sealed class Harness : IDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly HydrationPump Pump = new(CancellationToken.None);
        public readonly RecordingTraitPipeline Traits = new();
        public readonly FakeCatalogFetch Catalog;
        public readonly SpotifyProviderHydrator Hydrator;

        public Harness()
        {
            Catalog = new FakeCatalogFetch(Store, (uris, store) =>
            {
                foreach (var u in uris)
                    if (u.Kind == EntityKind.Track) store.UpsertTrack(TrackAt(u.Uri, HydrationLevel.Open));
            });
            var policy = new TraitPolicy();
            Hydrator = HydrationTestSupport.Hydrator(Store, Catalog, Traits, Pump,
                [new CollectionHydration(Store, policy), new PlayableHydration(EntityKind.Track, Store, new FakeEnvelopeFetch())],
                traitPolicy: policy);
        }

        public void Dispose() => Pump.Dispose();
    }

    [Theory]
    [InlineData("spotify:collection:tracks", "liked")]
    [InlineData("spotify:user:bob:collection", "liked")]
    [InlineData("spotify:collection:albums", "albums")]
    [InlineData("spotify:collection:artists", "artists")]
    [InlineData("spotify:collection:shows", "shows")]
    [InlineData("spotify:collection:episodes", "episodes")]
    [InlineData("spotify:collection:wibble", null)]
    public void SetOf_MapsTheCollectionUriToItsSavedSet(string uri, string? set)
        => Assert.Equal(set, CollectionHydration.SetOf(EntityUri.Parse(uri)));

    [Fact]
    public async Task Open_NamesEverySavedMember_InPagesOf300()
    {
        using var h = new Harness();
        for (int i = 0; i < 450; i++) h.Store.SetSaved("liked", "spotify:track:t" + i, true, SyncState.Confirmed);

        await h.Hydrator.EnsureAsync("spotify:collection:tracks", HydrationLevel.Open);
        await DrainAsync(h.Pump);

        var pages = h.Catalog.Batches.Where(b => b.Count > 0 && b[0].StartsWith("spotify:track:")).ToList();
        Assert.Equal(2, pages.Count);
        Assert.Equal(300, pages[0].Count);
        Assert.Equal(150, pages[1].Count);
        Assert.Equal(HydrationLevel.Full, h.Hydrator.LevelOf("spotify:collection:tracks"));
    }

    [Fact]
    public async Task Open_AsksTheLikedTraitBundle()
    {
        using var h = new Harness();
        h.Store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);

        await h.Hydrator.EnsureAsync("spotify:collection:tracks", HydrationLevel.Open);
        await DrainAsync(h.Pump);

        var call = Assert.Single(h.Traits.Calls);
        Assert.Equal(TraitSurface.LikedSongs, call.Surface);
        Assert.Equal(TraitSet.RowBundle | TraitSet.PlayCount, call.Traits);
    }

    [Fact]
    public async Task SavedAlbums_GetNoRowTraits()
    {
        using var h = new Harness();
        h.Store.SetSaved("albums", "spotify:album:al1", true, SyncState.Confirmed);

        await h.Hydrator.EnsureAsync("spotify:collection:albums", HydrationLevel.Open);
        await DrainAsync(h.Pump);

        // The row bundle decorates a track/episode ROW — a saved album has no such row to decorate.
        Assert.Empty(h.Traits.Calls);
    }

    [Fact]
    public async Task EmptySet_IsAlreadyThere()
    {
        using var h = new Harness();
        var outcome = await h.Hydrator.EnsureAsync("spotify:collection:tracks", HydrationLevel.Open);

        Assert.Equal(HydrationStatus.Reached, outcome.Status);
        // A collection has no catalogue kind of its own, and no members — so nothing was ever asked for.
        Assert.DoesNotContain(h.Catalog.Asked, u => u.StartsWith("spotify:track:", StringComparison.Ordinal));
    }

    // ── MemberLevel dispatch for the non-track saved sets (StoreLibrarySource.cs 4a) ────────────────────────────────
    // LevelOf walks the SAVED URIS and asks MemberLevel(uri) for each — a member below Identity (unnamed / no row at
    // all) holds the whole collection at Identity, exactly the same "not every member is even named yet" gate the
    // liked/track set already had. These pin that the same gate fires for the album/artist/show member kinds, which
    // MemberLevel dispatches through HydrationLevels.Of(_store.GetAlbum/GetArtist/GetShow(uri)) (CollectionHydration.cs
    // 56-64) — no production change was needed here; the switch already covered Album/Artist/Show.
    [Fact]
    public void LevelOf_SavedAlbumsSet_UnnamedAlbum_ReportsIdentity()
    {
        var store = new InMemoryStore();
        store.SetSaved("albums", "spotify:album:a1", true, SyncState.Confirmed);   // no album row landed yet
        var hydration = new CollectionHydration(store, new TraitPolicy());

        Assert.Equal(HydrationLevel.Identity, hydration.LevelOf("spotify:collection:albums"));
    }

    [Fact]
    public void LevelOf_SavedArtistsSet_UnnamedArtist_ReportsIdentity()
    {
        var store = new InMemoryStore();
        store.SetSaved("artists", "spotify:artist:ar1", true, SyncState.Confirmed);   // no artist row landed yet
        var hydration = new CollectionHydration(store, new TraitPolicy());

        Assert.Equal(HydrationLevel.Identity, hydration.LevelOf("spotify:collection:artists"));
    }

    [Fact]
    public void LevelOf_SavedShowsSet_UnnamedShow_ReportsIdentity()
    {
        var store = new InMemoryStore();
        store.SetSaved("shows", "spotify:show:s1", true, SyncState.Confirmed);   // no show row landed yet
        var hydration = new CollectionHydration(store, new TraitPolicy());

        Assert.Equal(HydrationLevel.Identity, hydration.LevelOf("spotify:collection:shows"));
    }
}
