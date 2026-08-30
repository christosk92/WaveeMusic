using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using static Wavee.Tests.HydrationTestSupport;

namespace Wavee.Tests;

// The bug this covers (see RootlistHydrationWatch's own header comment): the rootlist Open plan used to be computed
// exactly once, synchronously, before the cold rootlist had landed — so on a first launch it planned against an empty
// rootlist and never re-ran. These tests exercise the watch's re-planning: a rootlist that lands AFTER construction, a
// bulk load (InitialHydrate's shape), rows that are already complete, and that disposing it actually stops it.
public class RootlistHydrationWatchTests
{
    const string P1 = "spotify:playlist:p1";
    const string P2 = "spotify:playlist:p2";

    sealed class Harness : IDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly HydrationPump Pump = new(CancellationToken.None);
        public readonly RecordingTraitPipeline Traits = new();
        public readonly FakePlaylistOpener Opener = new();
        public readonly FakeCatalogFetch Catalog;
        public readonly SpotifyProviderHydrator Hydrator;

        public Harness()
        {
            Catalog = new FakeCatalogFetch(Store, (uris, store) =>
            {
                foreach (var u in uris)
                    if (u.Kind == EntityKind.Playlist)
                        store.UpsertPlaylist(new Playlist(u.Id, u.Uri, "List " + u.Id, null, "me", null, 0));
            });
            var policy = new TraitPolicy();
            Opener.OnOpen = uri => Store.SetMembership(uri, [new PlaylistMember("i0", "spotify:track:t1", null, 0)], null);
            Hydrator = HydrationTestSupport.Hydrator(Store, Catalog, Traits, Pump,
                [new PlaylistHydration(Store, Opener, policy)], traitPolicy: policy);
        }

        public void Dispose() => Pump.Dispose();
    }

    static RootlistEntry[] TwoThinRows() =>
    [
        new RootlistEntry(0, 1, "spotify:start-group:g:Folder", "Folder", 0),
        new RootlistEntry(1, 0, P1, null, 1),
        new RootlistEntry(2, 0, P2, null, 1),
        new RootlistEntry(3, 2, "spotify:end-group:g", null, 0),
    ];

    static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 10)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task ColdRootlist_LandingAfterConstruction_IsHydrated()
    {
        using var h = new Harness();
        using var watch = new RootlistHydrationWatch(h.Store, h.Hydrator, CancellationToken.None);

        await DrainAsync(h.Pump);
        Assert.Equal(0, h.Opener.OpenCalls);   // nothing to plan yet — the rootlist is still empty

        h.Store.SetRootlist(TwoThinRows());

        Assert.True(await WaitUntilAsync(() => h.Opener.OpenCalls == 2));
        await DrainAsync(h.Pump);

        Assert.NotNull(h.Store.GetPlaylist(P1));
        Assert.NotNull(h.Store.GetPlaylist(P2));
        Assert.Equal("List " + EntityUri.IdOf(P1), h.Store.GetPlaylist(P1)!.Name);
        Assert.Equal("List " + EntityUri.IdOf(P2), h.Store.GetPlaylist(P2)!.Name);
    }

    [Fact]
    public async Task BulkRootlistLanding_IsCoalescedButStillHydrated()
    {
        using var h = new Harness();
        using var watch = new RootlistHydrationWatch(h.Store, h.Hydrator, CancellationToken.None);

        await DrainAsync(h.Pump);
        Assert.Equal(0, h.Opener.OpenCalls);

        using (h.Store.BeginBulk())
        {
            h.Store.SetRootlist(TwoThinRows());
        }

        Assert.True(await WaitUntilAsync(() => h.Opener.OpenCalls == 2));
        await DrainAsync(h.Pump);

        Assert.NotNull(h.Store.GetPlaylist(P1));
        Assert.NotNull(h.Store.GetPlaylist(P2));
    }

    [Fact]
    public async Task CompleteRows_AreNotReasked()
    {
        using var h = new Harness();
        // Seed BOTH rows complete (header + membership) before the watch ever runs.
        h.Store.UpsertPlaylist(new Playlist("p1", P1, "P1", null, "me", null, 1));
        h.Store.SetMembership(P1, [new PlaylistMember("i0", "spotify:track:t1", null, 0)], null);
        h.Store.UpsertPlaylist(new Playlist("p2", P2, "P2", null, "me", null, 1));
        h.Store.SetMembership(P2, [new PlaylistMember("i0", "spotify:track:t1", null, 0)], null);

        using var watch = new RootlistHydrationWatch(h.Store, h.Hydrator, CancellationToken.None);
        await DrainAsync(h.Pump);

        h.Store.SetRootlist(TwoThinRows());
        await Task.Delay(200);
        h.Store.SetRootlist(TwoThinRows());
        await Task.Delay(200);
        h.Store.SetRootlist(TwoThinRows());
        await DrainAsync(h.Pump);

        Assert.Equal(0, h.Opener.OpenCalls);
        Assert.Equal(0, h.Opener.HeaderCalls);
    }

    [Fact]
    public async Task Dispose_StopsTheWatch()
    {
        using var h = new Harness();
        var watch = new RootlistHydrationWatch(h.Store, h.Hydrator, CancellationToken.None);
        await DrainAsync(h.Pump);
        watch.Dispose();

        h.Store.SetRootlist(TwoThinRows());
        await Task.Delay(200);

        Assert.Equal(0, h.Opener.OpenCalls);
    }
}
