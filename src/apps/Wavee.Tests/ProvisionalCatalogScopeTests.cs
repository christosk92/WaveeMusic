using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Backend.Sync;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The cold-start contract: a launch that recalls the account and scope of its last session serves that account's
/// DURABLE library before there is any session at all, and the network session that follows CONFIRMS that scope
/// instead of replacing it. Before this, the pre-login scope named no account, so every replica read answered Missing
/// until the AP welcome landed — and the welcome then bumped the epoch, which invalidated the replica scope and forced
/// a full reload on top.
/// </summary>
public sealed class ProvisionalCatalogScopeTests
{
    const string Account = "bob";

    static CatalogScope Live(string account = Account)
        => new("spotify", account, "en", "US", "premium", 1, false, ContextKnown: true, StorageAccount: "default");

    /// <summary>The scope a launch with no stored credential still uses — no account, no context.</summary>
    static CatalogScope Anonymous => new("spotify", "", "en", "", "", -1, false, ContextKnown: false);

    static CatalogRuntime Runtime(CountingPersistence persistence, CatalogScope scope, string actualAccount, bool? online)
        => new(scope, actualAccount, persistence, persistence, new MemoryReplicaProjection(new InMemoryStore()), [],
            online: online);

    static byte[] Revision(byte head) { var bytes = new byte[24]; bytes[0] = head; return bytes; }

    static RootlistReadResult Rootlist(params string[] uris)
    {
        var entries = ImmutableArray.CreateBuilder<RootlistEntry>();
        for (int i = 0; i < uris.Length; i++) entries.Add(new RootlistEntry(i, 0, uris[i], null, 0));
        return new RootlistReadResult(entries.ToImmutable(), Revision(1));
    }

    /// <summary>Writes one account's rootlist through a full runtime, the way a real session would.</summary>
    static async Task SeedLastSessionAsync(CountingPersistence persistence, string account = Account)
    {
        await using var session = Runtime(persistence, Live(account), account, online: null);
        await session.InitializeAsync();
        await session.Replicas.AdoptRootlistAsync(Rootlist("spotify:playlist:one", "spotify:playlist:two"));
    }

    [Fact]
    public async Task ARecalledScopeServesTheDurableRootlistBeforeAnySession()
    {
        var persistence = new CountingPersistence();
        await SeedLastSessionAsync(persistence);

        await using var launch = Runtime(persistence, Live(), Account, online: false);
        Assert.False(launch.Catalog.IsOnline);                       // a context, not a connection
        Assert.Equal(Account, launch.Replicas.Scope.Account);
        await launch.InitializeAsync();

        var rootlist = launch.Replicas.ReadRootlist();
        Assert.Equal(ReplicaBaselineState.Verified, rootlist.State);
        Assert.Equal(2, rootlist.Entries.Length);
        Assert.Equal("spotify:playlist:one", rootlist.Entries[0].Uri);
    }

    [Fact]
    public async Task TheAnonymousScopeStillServesNothingUntilTheSessionInstalls()
    {
        var persistence = new CountingPersistence();
        await SeedLastSessionAsync(persistence);

        // No stored credential: nothing names the owner, so the durable rows stay unreachable — the behaviour a first
        // run (and a logged-out one) keeps.
        await using var launch = Runtime(persistence, Anonymous, "", online: false);
        await launch.InitializeAsync();

        Assert.Null(launch.Replicas.Scope.Account);
        Assert.Equal(ReplicaBaselineState.Missing, launch.Replicas.ReadRootlist().State);
    }

    [Fact]
    public async Task ASameAccountInstallConfirmsTheReplicasInsteadOfReloadingThem()
    {
        var persistence = new CountingPersistence();
        await SeedLastSessionAsync(persistence);
        await using var launch = Runtime(persistence, Live(), Account, online: false);
        await launch.InitializeAsync();
        // One resident catalog answer, so "no key moved" below is a statement about real entries, not an empty table.
        await launch.Catalog.ReadAsync(new ResourceKey(Live(), "spotify:track:one", FacetKind.TrackIdentity));
        int loadsAfterBoot = persistence.ReplicaLoads;

        var sessions = new List<CatalogChangeSet>();
        using var watch = launch.Catalog.Changes.Subscribe(Observers.From<CatalogChangeSet>(
            change => { if (change.Kind == CatalogChangeKind.Session) sessions.Add(change); }));

        long epoch = await launch.SetSessionAsync(Live(), Account, online: true);

        Assert.Equal(loadsAfterBoot, persistence.ReplicaLoads);              // nothing was reloaded from storage
        Assert.True(launch.Catalog.IsOnline);
        Assert.Equal(epoch, launch.Replicas.Scope.Generation);               // the replica state moved WITH the epoch
        Assert.Equal(2, launch.Replicas.ReadRootlist().Entries.Length);      // …so the reads never went Missing
        var change = Assert.Single(sessions);
        Assert.True(change.Confirmed);
        Assert.Empty(change.Keys);                                           // no resident answer moved: nothing to re-read
    }

    [Fact]
    public async Task ADifferentAccountInstallStillResetsEverything()
    {
        var persistence = new CountingPersistence();
        await SeedLastSessionAsync(persistence);
        await using var launch = Runtime(persistence, Live(), Account, online: false);
        await launch.InitializeAsync();
        await launch.Catalog.ReadAsync(new ResourceKey(Live(), "spotify:track:one", FacetKind.TrackIdentity));
        int loadsAfterBoot = persistence.ReplicaLoads;

        var sessions = new List<CatalogChangeSet>();
        using var watch = launch.Catalog.Changes.Subscribe(Observers.From<CatalogChangeSet>(
            change => { if (change.Kind == CatalogChangeKind.Session) sessions.Add(change); }));

        await launch.SetSessionAsync(Live("carol"), "carol", online: true);

        Assert.True(persistence.ReplicaLoads > loadsAfterBoot);              // a new owner reloads
        Assert.Equal("carol", launch.Replicas.Scope.Account);
        Assert.Equal(ReplicaBaselineState.Missing, launch.Replicas.ReadRootlist().State);
        var change = Assert.Single(sessions);
        Assert.False(change.Confirmed);
        Assert.NotEmpty(change.Keys);                                        // every resident answer is re-keyed
    }

    [Fact]
    public async Task AConfirmationThatNeverLoadedTheBaselinesFallsBackToTheFullReload()
    {
        // The one case a re-stamp must refuse: the constructor's EMPTY bootstrap looks like this owner's state, and
        // re-stamping it would let the install skip the load it was about to do and leave the library empty forever.
        var persistence = new CountingPersistence();
        await SeedLastSessionAsync(persistence);
        int loadsAfterSeed = persistence.ReplicaLoads;
        await using var launch = Runtime(persistence, Live(), Account, online: false);   // no InitializeAsync

        await launch.SetSessionAsync(Live(), Account, online: true);

        Assert.Equal(loadsAfterSeed + 1, persistence.ReplicaLoads);
        Assert.Equal(2, launch.Replicas.ReadRootlist().Entries.Length);
    }

    // ── the pure rules ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfirmsOwnerIgnoresTheResultShapingFieldsAndNeedsBothAccounts()
    {
        Assert.True(SessionInstallRules.ConfirmsOwner(Live(), Account, Live() with { Market = "DE", Tier = 0 }, Account, online: true));
        Assert.True(SessionInstallRules.ConfirmsOwner(Live() with { ContextKnown = false }, Account, Live(), Account, online: true));
        Assert.False(SessionInstallRules.ConfirmsOwner(Live(), Account, Live("carol"), "carol", online: true));
        Assert.False(SessionInstallRules.ConfirmsOwner(Live(), Account, Live() with { StorageAccount = "other" }, Account, online: true));
        Assert.False(SessionInstallRules.ConfirmsOwner(Anonymous, "", Live(), Account, online: true));   // nothing was owned before
        // Going offline on an unchanged scope is a transition, not a confirmation: every key it owns just became
        // unreachable, and that has to travel.
        Assert.False(SessionInstallRules.ConfirmsOwner(Live(), Account, Live(), Account, online: false));
    }

    [Fact]
    public void ConfirmsScopeNeedsTheWholeScopeBecauseTheScopeIsPartOfEveryKey()
    {
        Assert.True(SessionInstallRules.ConfirmsScope(Live(), Account, Live(), Account, online: true));
        Assert.False(SessionInstallRules.ConfirmsScope(Live(), Account, Live() with { Market = "DE" }, Account, online: true));
        Assert.False(SessionInstallRules.ConfirmsScope(Live() with { ContextKnown = false }, Account, Live(), Account, online: true));
    }

    [Fact]
    public void OnlyAReplacingSessionFansOutToEveryNode()
    {
        Assert.True(SessionFanOutRules.RecomputesEveryNode(CatalogChangeKind.Session, confirmed: false));
        Assert.False(SessionFanOutRules.RecomputesEveryNode(CatalogChangeKind.Session, confirmed: true));
        Assert.False(SessionFanOutRules.RecomputesEveryNode(CatalogChangeKind.Durable, confirmed: false));

        // A confirming session leaves an unrelated node — everything it reads is already resident — untouched…
        Assert.False(SessionFanOutRules.Recomputes(CatalogChangeKind.Session, confirmed: true, dependsOnKeys: false));
        // …but a node that DOES read one of the keys it names (an error cleared, an answer that is askable again) re-joins.
        Assert.True(SessionFanOutRules.Recomputes(CatalogChangeKind.Session, confirmed: true, dependsOnKeys: true));
        // A replacement re-joins everything regardless.
        Assert.True(SessionFanOutRules.Recomputes(CatalogChangeKind.Session, confirmed: false, dependsOnKeys: false));
    }

    // ── the remembered scope on disk ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRememberedScopeRoundTripsAndIsForgottenOnLogout()
    {
        var store = new MemStore();
        var scopes = new LocalSessionScopeStore(store, new NoOpProtector());
        Assert.Null(scopes.Recall());

        scopes.Remember(Live());
        Assert.Equal(Live(), scopes.Recall());

        scopes.Forget();
        Assert.Null(scopes.Recall());
    }

    [Fact]
    public void AScopeWrittenByADifferentProtectorIsNotRecalled()
    {
        var store = new MemStore();
        new LocalSessionScopeStore(store, new NoOpProtector()).Remember(Live());
        Assert.Null(new LocalSessionScopeStore(store, new OtherScheme()).Recall());
    }

    sealed class MemStore : ILocalStore
    {
        readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);
        public string? Get(string key) => _data.GetValueOrDefault(key);
        public void Set(string key, string value) => _data[key] = value;
        public void Remove(string key) => _data.Remove(key);
    }

    sealed class OtherScheme : ICredentialProtector
    {
        public string Scheme => "other";
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }

    /// <summary>Real in-memory storage with one extra fact: how many times the replica bootstrap was loaded. That count
    /// is the whole difference between confirming a session and resetting it.</summary>
    sealed class CountingPersistence : ICatalogPersistence, IReplicaPersistence, ICatalogSearchPersistence
    {
        readonly MemoryDataPersistence _inner = new();
        int _replicaLoads;
        public int ReplicaLoads => Volatile.Read(ref _replicaLoads);

        public ValueTask<IReadOnlyList<CatalogRecord?>> ReadManyAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct)
            => _inner.ReadManyAsync(keys, ct);
        public ValueTask<CatalogTransportRecord?> ReadTransportAsync(CatalogScope scope, string subject, int extensionKind, CancellationToken ct)
            => _inner.ReadTransportAsync(scope, subject, extensionKind, ct);
        public ValueTask CommitAsync(CatalogCommit commit, CancellationToken ct) => _inner.CommitAsync(commit, ct);
        public ValueTask<CatalogSearchCorpus> ReadSearchCorpusAsync(CatalogScope scope, CancellationToken ct)
            => _inner.ReadSearchCorpusAsync(scope, ct);
        public ValueTask<ReplicaBootstrap> LoadAsync(ReplicaScope scope, CancellationToken ct)
        { Interlocked.Increment(ref _replicaLoads); return _inner.LoadAsync(scope, ct); }
        public ValueTask<PlaylistReplicaBaseline?> LoadPlaylistAsync(ReplicaScope scope, string uri, CancellationToken ct)
            => _inner.LoadPlaylistAsync(scope, uri, ct);
        public ValueTask CommitAsync(ReplicaTransaction transaction, CancellationToken ct) => _inner.CommitAsync(transaction, ct);
    }
}
