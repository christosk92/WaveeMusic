using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Fix 1+2 (PLAYS/BPM·KEY hydration gap): PlaylistFetcher/CollectionFetcher's hydrate delegate used to be a bare
// Identity ensure carrying TraitSurface.None — correct for a ladder's own identity-repair sub-ask, wrong for "a
// membership diff/snapshot just adopted these rows", which left every freshly-adopted row's trait facets unasked.
// MembershipHydration.For is the fix: the CALLER (LiveSessionHost) picks the real surface, and the resulting delegate
// both attributes the identity ensure to it AND fires a companion trait ask for the same rows.
public class MembershipHydrationTests
{
    [Fact]
    public async Task For_AttributesTheIdentityEnsureToTheCallersSurface()
    {
        var store = new InMemoryStore();
        var hydrator = new RecordingHydrator(store);
        var hydrate = MembershipHydration.For(hydrator, TraitSurface.PlaylistOpen);

        await hydrate(["spotify:track:t1", "spotify:track:t2"], CancellationToken.None);

        var batch = Assert.Single(hydrator.Batches);
        Assert.Equal(HydrationLevel.Identity, batch.Level);
        Assert.Equal(TraitSurface.PlaylistOpen, batch.Surface);
        Assert.Equal(["spotify:track:t1", "spotify:track:t2"], batch.Uris);
    }

    [Fact]
    public async Task For_FiresACompanionTraitAskForTheSameRows_AttributedToTheSameSurface()
    {
        var store = new InMemoryStore();
        var hydrator = new RecordingHydrator(store);
        var hydrate = MembershipHydration.For(hydrator, TraitSurface.PlaylistOpen);

        await hydrate(["spotify:track:t1"], CancellationToken.None);
        // The trait ask is fired without being awaited (a page open must not pay a second round trip) — give it a
        // beat to land on the fake, which completes synchronously anyway.
        await Task.Delay(10);

        var call = Assert.Single(hydrator.TraitCalls);
        Assert.Equal(TraitSurface.PlaylistOpen, call.Surface);
        Assert.Equal(["spotify:track:t1"], call.Uris);
    }

    [Fact]
    public async Task For_CollectionSurface_CarriesLikedSongsNotNone()
    {
        // The collection fetcher's own delegate: same shape, a different surface (the collection ladder's own — see
        // CollectionHydration.ContinueAsync, which already tags its OWN re-page ask TraitSurface.LikedSongs).
        var store = new InMemoryStore();
        var hydrator = new RecordingHydrator(store);
        var hydrate = MembershipHydration.For(hydrator, TraitSurface.LikedSongs);

        await hydrate(["spotify:track:t1"], CancellationToken.None);

        var batch = Assert.Single(hydrator.Batches);
        Assert.Equal(TraitSurface.LikedSongs, batch.Surface);
        Assert.NotEqual(TraitSurface.None, batch.Surface);
    }
}
