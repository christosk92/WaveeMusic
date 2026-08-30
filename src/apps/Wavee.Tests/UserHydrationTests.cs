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

// The user ladder (design §2.3). What it replaced is the whole reason these tests exist: an owner used to live in
// `SpotifyUserProfileService`'s private dictionary behind a `Changed` event, which a READ source subscribed to so it
// could `store.Bump()` the playlists that referenced it. The owner is a STORE ENTITY now — so what is pinned here is
// (a) a resolve writes rows, (b) the batch arm and the REST remainder are ONE pass, (c) a 404 is a real answer that is
// not re-asked, and (d) a transport failure is NOT a genuine absence.
public class UserHydrationTests
{
    const string U1 = "spotify:user:alice";
    const string U2 = "spotify:user:bob";

    /// <summary>The port double: a scripted batch arm plus a scripted per-user remainder, each recording what it saw —
    /// which is how "the batch answered two of three and the third cost exactly one REST call" is assertable at all.
    /// It mirrors <c>SpotifyUserProfileFetch</c>'s contract: an ABSENT key means "the transport could not say", a null
    /// VALUE means "there is genuinely nothing here".</summary>
    sealed class FakeUserProfileFetch : IUserProfileFetch
    {
        /// <summary>Kind-15 answers, by canonical uri. A uri missing from here falls through to <see cref="Rest"/>.</summary>
        public Dictionary<string, Owner?> Batch { get; } = new(StringComparer.Ordinal);
        /// <summary>The per-user REST remainder. A uri in neither map is simply not answered.</summary>
        public Dictionary<string, Owner?> Rest { get; } = new(StringComparer.Ordinal);
        public List<List<string>> Passes { get; } = new();
        public List<string> RestCalls { get; } = new();
        public bool Throw { get; set; }

        public Task<IReadOnlyDictionary<string, Owner?>> ResolveAsync(IReadOnlyList<string> userIds, CancellationToken ct)
        {
            Passes.Add(new List<string>(userIds));
            if (Throw) throw new InvalidOperationException("user-profile transport down");
            var result = new Dictionary<string, Owner?>(StringComparer.Ordinal);
            foreach (var uri in userIds)
            {
                if (Batch.TryGetValue(uri, out var batched)) { result[uri] = batched; continue; }
                RestCalls.Add(uri);
                if (Rest.TryGetValue(uri, out var rest)) result[uri] = rest;
            }
            return Task.FromResult<IReadOnlyDictionary<string, Owner?>>(result);
        }
    }

    sealed class Harness : IDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly HydrationPump Pump = new(CancellationToken.None);
        public readonly FakeCatalogFetch Catalog;
        public readonly FakeUserProfileFetch Fetch = new();
        public readonly SpotifyProviderHydrator Hydrator;

        public Harness()
        {
            Catalog = new FakeCatalogFetch(Store);
            Hydrator = HydrationTestSupport.Hydrator(Store, Catalog, new RecordingTraitPipeline(), Pump,
                [new UserHydration(Store, Fetch)]);
        }

        public void Dispose() => Pump.Dispose();
    }

    [Fact]
    public async Task Identity_ResolvesTheBatch_AndWritesOwnersToTheStore()
    {
        using var h = new Harness();
        h.Fetch.Batch[U1] = new Owner("alice", "Alice", new Image("https://img/alice"));
        h.Fetch.Batch[U2] = new Owner("bob", "Bob", null);

        var outcome = await h.Hydrator.EnsureManyAsync([U1, U2], HydrationLevel.Identity);

        Assert.Equal(HydrationStatus.Reached, outcome.Status);
        Assert.Equal("Alice", h.Store.GetOwner(U1)!.Name);
        Assert.Equal("https://img/alice", h.Store.GetOwner(U1)!.Avatar!.Url);
        Assert.Equal("Bob", h.Store.GetOwner(U2)!.Name);
        // The bare id and the uri are the SAME row — the canonical key is what stops two spellings becoming two rows.
        Assert.Same(h.Store.GetOwner(U1), h.Store.GetOwner("alice"));
        Assert.Same(h.Store.GetOwner(U1), h.Store.GetOwner("ALICE"));
        // ONE pass carrying both uris — the ladder owns the whole fetch.
        Assert.Equal(new[] { U1, U2 }, Assert.Single(h.Fetch.Passes).ToArray());
        // …and step 0 costs nothing: a user has no catalogue V4, so the real XmCatalogFetch drops these uris before a
        // request is written. (The port is still invoked for the batch; what matters is that it has nothing to ask.)
        Assert.Equal(Wavee.Protocol.ExtendedMetadata.ExtensionKind.UnknownExtension,
                     Wavee.Backend.Metadata.XmKinds.CatalogKindOf(EntityKind.User));
    }

    [Fact]
    public async Task Identity_FallsBackToRest_ForWhateverTheBatchDidNotAnswer()
    {
        using var h = new Harness();
        h.Fetch.Batch[U1] = new Owner("alice", "Alice", null);
        h.Fetch.Rest[U2] = new Owner("bob", "Bob", null);            // kind 15 has never heard of this account

        await h.Hydrator.EnsureManyAsync([U1, U2], HydrationLevel.Identity);

        Assert.Equal("Alice", h.Store.GetOwner(U1)!.Name);
        Assert.Equal("Bob", h.Store.GetOwner(U2)!.Name);
        Assert.Equal(new[] { U2 }, h.Fetch.RestCalls.ToArray());     // the REST arm is the REMAINDER, not a second pass
    }

    [Fact]
    public async Task Identity_A404_LeavesNoOwner_AndIsNotReAsked()
    {
        using var h = new Harness();
        h.Fetch.Batch[U1] = null;                                    // a real answer: this account has no profile

        var first = await h.Hydrator.EnsureAsync(U1, HydrationLevel.Identity);
        var second = await h.Hydrator.EnsureAsync(U1, HydrationLevel.Identity);

        Assert.Null(h.Store.GetOwner(U1));
        Assert.Equal(HydrationLevel.None, h.Hydrator.LevelOf(U1));
        Assert.Equal(HydrationStatus.Partial, first.Status);         // the ladder ran and cannot get further
        Assert.Equal(HydrationStatus.Partial, second.Status);
        Assert.Single(h.Fetch.Passes);                               // …and the seal stops the re-ask
    }

    [Fact]
    public async Task Identity_AResolvedOwner_IsNotReAsked()
    {
        using var h = new Harness();
        h.Fetch.Batch[U1] = new Owner("alice", "Alice", null);

        await h.Hydrator.EnsureAsync(U1, HydrationLevel.Identity);
        await h.Hydrator.EnsureAsync(U1, HydrationLevel.Identity);

        Assert.Single(h.Fetch.Passes);
    }

    [Fact]
    public async Task Identity_ATransportFailure_IsNotAGenuineAbsence()
    {
        using var h = new Harness();
        h.Fetch.Throw = true;

        var outcome = await h.Hydrator.EnsureAsync(U1, HydrationLevel.Identity);

        Assert.Null(h.Store.GetOwner(U1));
        Assert.Equal(HydrationStatus.Partial, outcome.Status);
        // The run reported the failure, so the seal is the SHORT exhausted window — the value the policy hands back for
        // a transient miss, never the long "we asked and there is genuinely nothing" one.
        Assert.Equal(HydrationPolicy.Default.ExhaustedPlayableTtl,
                     HydrationPolicy.Default.Ttl(EntityKind.User, HydrationLevel.Identity, ok: false, transient: true));
    }

    [Fact]
    public async Task Identity_OneBulkScopePerPass_AndNoneAtAllWhenNothingResolves()
    {
        using var h = new Harness();
        h.Fetch.Batch[U1] = new Owner("alice", "Alice", null);
        h.Fetch.Batch[U2] = new Owner("bob", "Bob", null);
        var changes = new List<StoreChange>();
        using var sub = h.Store.Changes.Subscribe(new Obs(changes.Add));

        await h.Hydrator.EnsureManyAsync([U1, U2], HydrationLevel.Identity);

        // Two owners, ONE coalesced signal — a 10k playlist's added-by set must repaint the grid once, not per row.
        Assert.Equal(1, changes.Count(c => c.IsBulk));
        Assert.DoesNotContain(changes, c => !c.IsBulk);

        // …and a page whose every id 404s publishes no change at all (the bulk scope opens lazily).
        changes.Clear();
        h.Fetch.Batch["spotify:user:ghost"] = null;
        await h.Hydrator.EnsureAsync("spotify:user:ghost", HydrationLevel.Identity);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task LevelOf_IsTheStoreRow_AndTheOfflineHydratorAgrees()
    {
        using var h = new Harness();
        h.Fetch.Batch[U1] = new Owner("alice", "Alice", null);
        await h.Hydrator.EnsureAsync(U1, HydrationLevel.Identity);

        // An owner has no rung above "we know who this is" — every level is satisfied by a named row.
        Assert.Equal(HydrationLevel.Full, h.Hydrator.LevelOf(U1));
        Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(h.Store.GetOwner(U1)));
        Assert.Equal(HydrationLevel.None, HydrationLevels.Of(h.Store.GetOwner(U2)));
        // ONE body offline and online: a divergence here is what would make a logged-out byline ask forever for a name
        // the cache already holds.
        var offline = new OfflineEntityHydrator(h.Store);
        Assert.Equal(h.Hydrator.LevelOf(U1), offline.LevelOf(U1));
        Assert.Equal(h.Hydrator.LevelOf(U2), offline.LevelOf(U2));
    }

    sealed class Obs(Action<StoreChange> onNext) : IObserver<StoreChange>
    {
        public void OnNext(StoreChange v) => onNext(v);
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }
}
