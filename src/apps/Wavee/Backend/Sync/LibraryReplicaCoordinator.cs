using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Queries;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Sync;

/// <summary>Confirmed library baselines and local intent have one commit owner. Network clients return observations;
/// this coordinator persists a candidate before publishing its effective projection. There is no network work here.</summary>
public sealed partial class LibraryReplicaCoordinator
{
    sealed record State(ImmutableDictionary<string, PlaylistReplicaBaseline> Playlists,
        RootlistReplicaBaseline Rootlist, ImmutableDictionary<string, CollectionReplicaBaseline> Collections,
        ImmutableSortedDictionary<long, OutboxOp> Intents,
        ImmutableDictionary<string, PlaylistReplicaBaseline> EffectivePlaylists, ReplicaScope Scope)
    {
        public NativeState Native { get; init; } = NativeState.Empty;
    }

    readonly DataCommitQueue _commits;
    readonly IReplicaPersistence _persistence;
    readonly IReplicaProjectionSink _sink;
    readonly CatalogRepository _catalog;
    readonly Func<ReplicaScope> _scope;
    readonly SimpleEvent<ReplicaChange> _changes = new();
    readonly SimpleEvent<ReplicaDeadLetter> _intentRejected = new();
    readonly SimpleEvent<string> _pendingChanged = new();
    State _state;
    long _nextId;

    public LibraryReplicaCoordinator(DataCommitQueue commits, IReplicaPersistence persistence,
        IReplicaProjectionSink sink, Func<ReplicaScope> scope, ReplicaBootstrap bootstrap, CatalogRepository catalog)
    {
        _commits = commits ?? throw new ArgumentNullException(nameof(commits));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _state = new State(bootstrap.Playlists.Where(x => x.State != ReplicaBaselineState.RecoveryOnly).Select(x => x with { Header = null }).ToImmutableDictionary(x => x.Uri, StringComparer.Ordinal),
            bootstrap.Rootlist, bootstrap.Collections.ToImmutableDictionary(x => x.SetId, StringComparer.Ordinal),
            bootstrap.Intents.ToImmutableSortedDictionary(x => x.Id, x => x),
            ImmutableDictionary.Create<string, PlaylistReplicaBaseline>(StringComparer.Ordinal), _scope());
        _state = WithInitialProjections(_state, _scope());
        _nextId = Math.Max(bootstrap.LastIntentId, bootstrap.Intents.IsEmpty ? 0 : bootstrap.Intents.Max(x => x.Id));
    }

    public ReplicaScope Scope => _scope();
    public IObservable<ReplicaChange> Changes => _changes;
    public IObservable<ReplicaDeadLetter> IntentRejected => _intentRejected;
    public IObservable<string> PendingChanged => _pendingChanged;
    public ImmutableArray<OutboxOp> Intents
    {
        get
        {
            var state = Volatile.Read(ref _state); var scope = _scope();
            return state.Scope == scope ? VisibleIntents(state, scope).ToImmutableArray() : [];
        }
    }
    public PlaylistReplicaBaseline ReadConfirmedPlaylist(string uri)
    {
        var state = Volatile.Read(ref _state);
        return state.Scope == _scope() ? Baseline(state, uri) with { Header = ReadCatalogHeader(uri) }
            : new(uri, [], null, null, ReplicaBaselineState.Missing);
    }
    public RootlistReplicaBaseline ReadConfirmedRootlist()
    { var state = Volatile.Read(ref _state); return state.Scope == _scope() ? state.Rootlist : new([], null, ReplicaBaselineState.Missing); }
    public CollectionReplicaBaseline ReadConfirmedCollection(string setId)
    { var state = Volatile.Read(ref _state); return state.Scope == _scope() ? Collection(state, setId) : new(setId, []) { IsKnown = false }; }

    // Compatibility headers are materialized only for protocol reduction/proof, never retained by the replica.
    Playlist? ReadCatalogHeader(string uri)
    {
        var scope = _catalog.Scope with { Provider = "spotify" };
        if (_catalog.Peek(new ResourceKey(scope, uri, FacetKind.PlaylistHeader)).Value is not PlaylistHeaderValue) return null;
        return new CatalogReadView(scope, this, static value => EntityUri.Parse(value).Provider)
            .Playlist(new QueryReadContext(_catalog), uri, applyPendingHeader: false);
    }

    public PlaylistReplicaBaseline ReadPlaylist(string uri)
    {
        var state = Volatile.Read(ref _state);
        if (state.Scope != _scope()) return new PlaylistReplicaBaseline(uri, [], null, null, ReplicaBaselineState.Missing);
        return EffectivePlaylist(state, uri);
    }
    public Playlist ApplyPendingHeader(string uri, Playlist normalizedHeader)
    {
        var header = normalizedHeader;
        foreach (var intent in VisibleIntents(Volatile.Read(ref _state), _scope()).Where(x => x.EntityKey == uri && x.Type == "oprebase"))
            header = PlaylistReplicaReducer.ApplyHeader(header, intent.Ops ?? [])!;
        return header;
    }
    public RootlistReplicaBaseline ReadRootlist()
    {
        var state = Volatile.Read(ref _state);
        return state.Scope == _scope() ? ProjectRootlist(state, state.Scope) : new([], null, ReplicaBaselineState.Missing);
    }
    public ReplicaSavedProjection ReadCollection(string setId)
    {
        var state = Volatile.Read(ref _state);
        return state.Scope == _scope() ? ProjectCollection(state, setId, state.Scope) : new(setId, [], ImmutableHashSet<string>.Empty);
    }

    public Task ReloadAsync(ReplicaScope scope, CancellationToken ct = default)
        => _commits.ExecuteAsync<int>(async token =>
        {
            if (scope != _scope()) throw new OperationCanceledException("The library session changed before reload.");
            var bootstrap = await _persistence.LoadAsync(scope, token).ConfigureAwait(false);
            if (scope != _scope()) throw new OperationCanceledException("The library session changed during reload.");
            var next = new State(bootstrap.Playlists.Where(x => x.State != ReplicaBaselineState.RecoveryOnly).Select(x => x with { Header = null }).ToImmutableDictionary(x => x.Uri, StringComparer.Ordinal),
                bootstrap.Rootlist, bootstrap.Collections.ToImmutableDictionary(x => x.SetId, StringComparer.Ordinal),
                bootstrap.Intents.ToImmutableSortedDictionary(x => x.Id, x => x),
                ImmutableDictionary.Create<string, PlaylistReplicaBaseline>(StringComparer.Ordinal), scope);
            next = WithInitialProjections(next, scope);
            _nextId = Math.Max(_nextId, Math.Max(bootstrap.LastIntentId, bootstrap.Intents.IsEmpty ? 0 : bootstrap.Intents.Max(x => x.Id)));
            var previous = _state;
            var playlistKeys = next.Playlists.Keys.Concat(previous.Playlists.Keys).Concat(previous.Native.Playlists.Keys).Distinct().ToArray();
            var collectionKeys = next.Collections.Keys.Concat(previous.Collections.Keys).Concat(previous.Native.Collections.Keys).Distinct().ToArray();
            _commits.Publish(() =>
            {
                Volatile.Write(ref _state, next);
                _loaded = true;
                PublishPendingChanges(previous, next);
                PublishProjection(new ReplicaProjection(playlistKeys.Select(ReadPlaylist).ToImmutableArray(), ReadRootlist(),
                    collectionKeys.Select(ReadCollection).ToImmutableArray()));
                foreach (var uri in playlistKeys) NotifyChange(new ReplicaChange(uri, ReadPlaylist(uri).Version, true, true));
                NotifyChange(new ReplicaChange("rootlist", next.Rootlist.Version, true, true));
                foreach (var set in collectionKeys) NotifyChange(new ReplicaChange(set, ReadConfirmedCollection(set).Version, true, true));
            });
            return 0;
        }, ct);


    // Set once a ReloadAsync has actually installed this owner's durable baselines. A session confirmation may only
    // re-stamp state that HAS been loaded: the constructor seeds an EMPTY bootstrap, and re-stamping that would let a
    // confirming install skip the load it was about to do and leave the library permanently empty.
    bool _loaded;

    /// <summary>A same-owner session CONFIRMATION. The catalog epoch moved (a protocol session installed) but the
    /// account and the storage account did not, so every resident baseline is still this owner's and only the
    /// generation stamp is stale. Re-stamps it in place — inside the SAME publication that installed the session, so no
    /// reader ever observes the mismatch — instead of reloading it from SQLite, which is what used to make every read
    /// answer Missing (and the sidebar fall back to skeleton rows) for the length of that load. Answers false when the
    /// resident state is not this owner's, or has never been loaded at all; the caller then does the full reload.</summary>
    internal bool RestampScopeCore(ReplicaScope next)
    {
        var state = Volatile.Read(ref _state);
        if (state.Scope == next) return _loaded;
        if (!_loaded || !string.Equals(state.Scope.Account, next.Account, StringComparison.Ordinal)
            || !string.Equals(state.Scope.StorageAccount, next.StorageAccount, StringComparison.Ordinal)) return false;
        Volatile.Write(ref _state, state with { Scope = next });
        return true;
    }

    /// <summary>Loads one exact-owner baseline on demand. It never grants authority to migration recovery rows.</summary>
    public async Task EnsurePlaylistCachedAsync(string uri, CancellationToken ct = default)
    {
        var scope = _scope();
        var resident = Volatile.Read(ref _state);
        if (resident.Scope == scope && resident.Playlists.ContainsKey(uri)) return;
        var catalogScope = _catalog.Scope with { Provider = "spotify" };
        await _catalog.ReadAsync(new ResourceKey(catalogScope, uri, FacetKind.PlaylistHeader), ct).ConfigureAwait(false);
        await _commits.ExecuteAsync<int>(async token =>
        {
            if (scope != _scope() || scope != _state.Scope) throw new OperationCanceledException("The library owner changed before the cached read.");
            if (_state.Playlists.ContainsKey(uri)) return 0;
            var loaded = await _persistence.LoadPlaylistAsync(scope, uri, token).ConfigureAwait(false)
                ?? new PlaylistReplicaBaseline(uri, [], null, null, ReplicaBaselineState.Missing);
            if (scope != _scope()) throw new OperationCanceledException("The library owner changed during the cached read.");
            loaded = loaded with { Header = null };
            var prior = _state;
            var effective = PlaylistReplicaReducer.Project(loaded with { Header = ReadCatalogHeader(uri) }, VisibleIntents(prior, scope))
                with { OrderRevision = loaded.OrderRevision, Header = null };
            var next = prior with { Playlists = prior.Playlists.SetItem(uri, loaded), EffectivePlaylists = prior.EffectivePlaylists.SetItem(uri, effective) };
            _commits.Publish(() =>
            {
                Volatile.Write(ref _state, next);
                PublishProjection(new ReplicaProjection([effective], null, []));
                NotifyChange(new ReplicaChange(uri, loaded.Version, true, true));
            });
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public Task PublishInitialAsync(CancellationToken ct = default)
        => _commits.ExecuteAsync<int>(_ =>
        {
            var state = _state;
            if (state.Playlists.IsEmpty && state.Native.Playlists.IsEmpty && state.Collections.IsEmpty
                && state.Native.Collections.IsEmpty && state.Rootlist.State == ReplicaBaselineState.Missing
                && state.Native.Rootlist.IsEmpty) return ValueTask.FromResult(0);
            _commits.Publish(() => PublishProjection(new ReplicaProjection(state.Playlists.Keys.Concat(state.Native.Playlists.Keys).Distinct().Select(ReadPlaylist).ToImmutableArray(),
                ReadRootlist(), state.Collections.Keys.Concat(state.Native.Collections.Keys).Distinct().Select(ReadCollection).ToImmutableArray())));
            return ValueTask.FromResult(0);
        }, ct);

    public bool HasPending(string setId, string uri) => Intents.Any(x => x.SetId == setId && x.EntityKey == uri);
    public int PendingFor(string uri) => Intents.Count(x => x.EntityKey == uri);
    public bool CanReplay(OutboxOp intent, string account)
        => Volatile.Read(ref _state).Scope == _scope() && intent.State == ReplicaIntentState.Pending && !string.IsNullOrEmpty(intent.OwnerAccount)
           && string.Equals(intent.OwnerAccount, account, StringComparison.Ordinal) && intent.StorageAccount == _scope().StorageAccount
           && (intent.Type != "rootlist" || ReadConfirmedRootlist().State is ReplicaBaselineState.Verified or ReplicaBaselineState.Cached)
           && (intent.Type != "oprebase" || ReadConfirmedPlaylist(intent.EntityKey).State is ReplicaBaselineState.Verified or ReplicaBaselineState.Cached);

    public Task<OutboxOp> StageAsync(string type, string uri, string setId, bool saved,
        IReadOnlyList<PlaylistOp>? ops = null, byte[]? baseRevision = null, string? folder = null,
        Playlist? createdHeader = null, CancellationToken ct = default, ReplicaScope? expectedScope = null,
        ReplicaIntentState initialState = ReplicaIntentState.Pending, IReadOnlyList<Track>? seedTracks = null)
    {
        var ownedOps = ReplicaPayload.Freeze(ops);
        var ownedBase = baseRevision?.ToArray();
        var seeds = new List<CatalogSeed>();
        var scope = _catalog.Scope;
        foreach (var track in seedTracks ?? []) CatalogDomainSeeds.Track(scope, track, seeds);
        var observations = seeds.Select(seed => new CatalogObservation(seed.Key with { Scope = scope with
            { Provider = EntityUri.Parse(seed.Key.Subject).Provider } }, seed.Patch, FillUnknownOnly: true)).ToArray();
        return Commit(u =>
        {
            if (string.IsNullOrEmpty(u.Scope.Account))
                throw new PlaylistMutationException(PlaylistMutationFailure.Offline, "An authenticated owner is required to queue this edit.");
            var id = ++_nextId;
            var boundBase = ownedBase ?? (type == "oprebase" ? u.Playlist(uri).Revision?.ToArray() : null);
            var intent = new OutboxOp(id, type, uri, setId, saved, id, 0, ownedOps, boundBase, folder,
                u.Scope.Account, State: initialState, CreatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), StorageAccount: u.Scope.StorageAccount);
            if (type is "set" or "rootlist")
                foreach (var prior in u.Intents.Values.Where(x => x.Type == type && x.SetId == setId && x.EntityKey == uri
                    && x.OwnerAccount == intent.OwnerAccount && x.State == ReplicaIntentState.Pending).ToArray())
                    u.Intents.Remove(prior.Id);
            u.Intents.Add(intent.Id, intent);
            u.Observations.AddRange(observations);
            if (createdHeader is not null)
                u.SetPlaylist(new PlaylistReplicaBaseline(uri, [], null, createdHeader, ReplicaBaselineState.AwaitingCreate));
            u.Touch(intent);
            return intent;
        }, ct, expectedScope, checked(256 + ReplicaPayload.Ops(ownedOps) + ReplicaPayload.Header(createdHeader) + CatalogPayloadCodec.MeasureObservations(observations)));
    }

    public Task<OutboxOp> StageCreateAsync(Playlist header, string? folder, CancellationToken ct = default)
        => Commit(u =>
        {
            if (string.IsNullOrEmpty(u.Scope.Account))
                throw new PlaylistMutationException(PlaylistMutationFailure.Offline, "An authenticated owner is required to create a playlist.");
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var createId = ++_nextId;
            var create = new OutboxOp(createId, "create", header.Uri, header.Uri, false, createId, 0,
                [new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(Name: header.Name))],
                PlaylistRevisions.NewCreateBase(), OwnerAccount: u.Scope.Account, CreatedAtMs: createdAt, StorageAccount: u.Scope.StorageAccount);
            var followId = ++_nextId;
            var follow = new OutboxOp(followId, "rootlist", header.Uri, "playlists", true, followId, 0,
                ParentFolderId: folder, OwnerAccount: u.Scope.Account, CreatedAtMs: createdAt, StorageAccount: u.Scope.StorageAccount);
            u.Intents.Add(create.Id, create); u.Intents.Add(follow.Id, follow);
            u.SetPlaylist(new PlaylistReplicaBaseline(header.Uri, [], null, header, ReplicaBaselineState.AwaitingCreate));
            u.Touch(create); u.Touch(follow);
            return create;
        }, ct, payloadBytes: checked(512 + ReplicaPayload.Header(header)));

    public Task AdoptPlaylistAsync(PlaylistReadResult read, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u =>
        {
            var next = PlaylistReplicaReducer.ApplyServer(u.Playlist(read.Uri), read);
            u.SetPlaylist(next);
            if (read.Header is not null && read.HeaderIsComplete) u.FullHeaders.Add(read.Uri);
            if (read.Kind == PlaylistReadKind.Snapshot && next.State == ReplicaBaselineState.Verified)
            {
                u.RemoveRecovery.Add(read.Uri);
                foreach (var intent in u.Intents.Values.Where(x => x.EntityKey == read.Uri && x.State != ReplicaIntentState.Pending).ToArray())
                    if (intent.OwnerAccount == u.Scope.Account && PlaylistReplicaReducer.IsEffectPresent(next, intent))
                        u.Intents.Remove(intent.Id);
            }
            if (next.Header?.DeletedByOwner == true)
            {
                foreach (var intent in u.Intents.Values.Where(x => x.EntityKey == read.Uri).ToArray())
                    u.Reject(intent, PlaylistMutationFailure.Deleted, "playlist-deleted");
                u.Rootlist = u.Rootlist with { Entries = u.Rootlist.Entries.Where(x => x.Uri != read.Uri).ToImmutableArray(), Version = u.Rootlist.Version + 1 };
                u.RootlistChanged = true;
                u.SetSaved("playlists", read.Uri, false, 0);
            }
            return 0;
        }, ct, expectedScope, ReplicaPayload.Playlist(read));

    public async Task AdoptHeaderAsync(string uri, Playlist header, CancellationToken ct = default, ReplicaScope? expectedScope = null)
    {
        await EnsurePlaylistCachedAsync(uri, ct).ConfigureAwait(false);
        await Commit(u => { var baseline = u.Playlist(uri); u.SetPlaylist(baseline with { Header = baseline.Header?.DeletedByOwner == true ? header with { DeletedByOwner = true } : header, Version = baseline.Version + 1 }); u.FullHeaders.Add(uri); return 0; }, ct, expectedScope, ReplicaPayload.Header(header)).ConfigureAwait(false);
    }

    /// <summary>Permission endpoints own these fields only. Merge against the current header inside the commit lane.</summary>
    public async Task ObservePermissionsAsync(string uri, bool isPublic, string? revision, bool? collaborative = null,
        CancellationToken ct = default, ReplicaScope? expectedScope = null)
    {
        await EnsurePlaylistCachedAsync(uri, ct).ConfigureAwait(false);
        await Commit(u =>
        {
            var baseline = u.Playlist(uri);
            u.SetPlaylist(baseline with { Header = null, Version = baseline.Version + 1 });
            var capabilities = collaborative is { } value && baseline.Header is { } header
                ? FieldChange<PlaylistCapabilities?>.Set(header.Capabilities with { IsCollaborative = value }) : default;
            u.Observations.Add(new(new ResourceKey(_catalog.Scope with { Provider = "spotify" }, uri, FacetKind.PlaylistHeader),
                new PlaylistHeaderPatch(IsPublic: FieldChange<bool?>.Set(isPublic),
                    BasePermissionRevision: FieldChange<string?>.Set(revision), Capabilities: capabilities)));
            return 0;
        }, ct, expectedScope, checked(256 + 2 * (uri.Length + (revision?.Length ?? 0)))).ConfigureAwait(false);
    }

    public Task AdoptRootlistAsync(RootlistReadResult read, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u =>
        {
            bool valid = PlaylistRevisions.IsWellFormed(read.Revision);
            u.Rootlist = new RootlistReplicaBaseline(read.Entries, valid ? read.Revision!.ToArray() : null,
                valid ? ReplicaBaselineState.Verified : ReplicaBaselineState.NeedsResync, u.Rootlist.Version + 1);
            u.RootlistChanged = true;
            if (valid) u.RemoveRecovery.Add("rootlist");
            var playlists = read.Entries.Where(x => x.Kind == 0).Select(x => new SavedItem(x.Uri, x.AddedAtMs)).ToImmutableArray();
            u.SetCollection(new CollectionReplicaBaseline("playlists", playlists, Version: u.Collection("playlists").Version + 1) { IsKnown = valid });
            foreach (var intent in u.Intents.Values.Where(x => x.Type is "rootlist" or "rootlist-edit" && x.State != ReplicaIntentState.Pending).ToArray())
                if (intent.OwnerAccount == u.Scope.Account && valid &&
                    (PlaylistRevisions.Equal(intent.AcknowledgedRevision, read.Revision)
                     || intent.Type == "rootlist" && playlists.Any(x => x.Uri == intent.EntityKey) == intent.TargetSaved))
                    u.Intents.Remove(intent.Id);
            return 0;
        }, ct, expectedScope, ReplicaPayload.Rootlist(read));

    public Task AdoptCollectionAsync(CollectionReadResult read, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u =>
        {
            foreach (var set in CollectionSets.LogicalSetsForWireSet(read.WireSet))
            {
                var prior = u.Collection(set);
                if (!read.IsSnapshot && !string.Equals(prior.WireRevision, read.ExpectedToken, StringComparison.Ordinal))
                    throw new ReplicaBaseMismatchException(read.WireSet);
                var items = prior.Items.ToDictionary(x => x.Uri, StringComparer.Ordinal);
                var observed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in read.Items)
                {
                    if (!CollectionSets.AcceptsUri(set, item.Uri)) continue;
                    var prefix = CollectionSets.UriPrefix(set);
                    if (prefix is not null && !item.Uri.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (!item.Removed) observed.Add(item.Uri);
                    if (item.Removed) items.Remove(item.Uri);
                    else items[item.Uri] = new SavedItem(item.Uri, item.AddedAt > 0 ? item.AddedAt : items.GetValueOrDefault(item.Uri).AddedAtMs);
                }
                if (read.IsSnapshot && read.Verified)
                    foreach (var item in items.Values.ToArray())
                        if (CollectionSweepPolicy.Decide(observed.Contains(item.Uri),
                            false, item.AddedAtMs, read.StartedAtMs)
                            == CollectionSweepPolicy.Keep.Remove) items.Remove(item.Uri);
                u.SetCollection(prior with { Items = items.Values.ToImmutableArray(),
                    WireRevision = read.Verified ? read.Token : prior.WireRevision, Version = prior.Version + 1, IsKnown = prior.IsKnown || read.Verified });
                if (read.Verified)
                    foreach (var intent in u.Intents.Values.Where(x => x.Type == "set" && x.SetId == set && x.State != ReplicaIntentState.Pending).ToArray())
                        if (intent.OwnerAccount == u.Scope.Account && items.ContainsKey(intent.EntityKey) == intent.TargetSaved)
                            u.Intents.Remove(intent.Id);
            }
            return 0;
        }, ct, expectedScope, ReplicaPayload.Collection(read));

    public Task ApplyCollectionPushAsync(string wireSet, IReadOnlyList<CollectionItem> items, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u =>
        {
            foreach (var item in items)
                if (CollectionSets.LogicalSetForItem(wireSet, item.Uri) is { } set)
                    u.SetSaved(set, item.Uri, !item.Removed, item.AddedAt);
            return 0;
        }, ct, expectedScope, checked(128 + items.Sum(x => 48 + x.Uri.Length * sizeof(char))));

    public Task CompleteAsync(OutboxOp attempted, PlaylistReadResult? playlist, RootlistReadResult? rootlist,
        bool requiresVerification, byte[]? acknowledgedRevision = null, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u =>
        {
            if (!u.Intents.TryGetValue(attempted.Id, out var current)) return 0;
            if (current.OwnerAccount != u.Scope.Account) throw new InvalidOperationException("The mutation owner changed.");
            if (requiresVerification)
            {
                u.Intents[current.Id] = current with { State = ReplicaIntentState.AwaitingVerification, Attempts = 0,
                    AcknowledgedRevision = acknowledgedRevision?.ToArray() };
                u.Touch(current);
                return 0;
            }
            if (playlist is not null) u.SetPlaylist(PlaylistReplicaReducer.ApplyServer(u.Playlist(playlist.Uri), playlist));
            if (rootlist is not null)
            {
                if (!PlaylistRevisions.IsWellFormed(rootlist.Revision)) throw new InvalidOperationException("An acknowledged rootlist requires its head.");
                u.Rootlist = new RootlistReplicaBaseline(rootlist.Entries, rootlist.Revision.ToArray(), Version: u.Rootlist.Version + 1);
                u.RootlistChanged = true;
                u.SetCollection(new CollectionReplicaBaseline("playlists", rootlist.Entries.Where(x => x.Kind == 0)
                    .Select(x => new SavedItem(x.Uri, x.AddedAtMs)).ToImmutableArray(), Version: u.Collection("playlists").Version + 1));
            }
            if (current.Type is "set" or "rootlist") u.SetSaved(current.SetId, current.EntityKey, current.TargetSaved, current.CreatedAtMs);
            u.Intents.Remove(current.Id);
            u.Touch(current);
            return 0;
        }, ct, expectedScope, checked(ReplicaPayload.Intent(attempted) + (playlist is null ? 0 : ReplicaPayload.Playlist(playlist)) + (rootlist is null ? 0 : ReplicaPayload.Rootlist(rootlist))));

    public Task<bool> BeginAttemptAsync(OutboxOp prepared, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u =>
        {
            if (!u.Intents.TryGetValue(prepared.Id, out var current) || current.State != ReplicaIntentState.Pending
                || current.OwnerAccount != u.Scope.Account) return false;
            u.Intents[prepared.Id] = prepared with { State = ReplicaIntentState.AwaitingVerification, Attempts = 0 };
            u.Touch(prepared);
            return true;
        }, ct, expectedScope, ReplicaPayload.Intent(prepared));

    public Task SetIntentAsync(OutboxOp intent, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u => { if (u.Intents.ContainsKey(intent.Id)) { u.Intents[intent.Id] = intent; u.Touch(intent); } return 0; }, ct, expectedScope, ReplicaPayload.Intent(intent));

    /// <summary>Finding #3 (library-v3-1-findings-2026-09-06.md Part 2 2.1 #3): a definitive fetch failure (404/410)
    /// during baseline recovery marks that ONE playlist missing and its pending intents NeedsAttention, without
    /// touching any other playlist's intents — the drain that hit this one keeps going for everything else.</summary>
    public Task MarkPlaylistUnreachableAsync(string uri, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u =>
        {
            var baseline = u.Playlist(uri);
            u.SetPlaylist(baseline with { State = ReplicaBaselineState.Missing, Version = baseline.Version + 1 });
            foreach (var intent in u.Intents.Values.Where(x => x.EntityKey == uri && x.State == ReplicaIntentState.Pending).ToArray())
                u.Intents[intent.Id] = intent with { State = ReplicaIntentState.NeedsAttention };
            return 0;
        }, ct, expectedScope, checked(256 + 2 * uri.Length));

    public Task RetryAsync(OutboxOp intent, bool refreshBaseline, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u =>
        {
            if (!u.Intents.TryGetValue(intent.Id, out var current) || current.OwnerAccount != u.Scope.Account) return 0;
            u.Intents[intent.Id] = intent with { State = ReplicaIntentState.Pending, AcknowledgedRevision = null };
            if (refreshBaseline && intent.Type == "oprebase")
            {
                var baseline = u.Playlist(intent.EntityKey);
                u.SetPlaylist(baseline with { State = ReplicaBaselineState.NeedsResync, Version = baseline.Version + 1 });
            }
            if (refreshBaseline && intent.Type == "rootlist")
            {
                u.Rootlist = u.Rootlist with { State = ReplicaBaselineState.NeedsResync, Version = u.Rootlist.Version + 1 };
                u.RootlistChanged = true;
            }
            u.Touch(intent);
            return 0;
        }, ct, expectedScope, ReplicaPayload.Intent(intent));

    public Task RejectAsync(OutboxOp intent, PlaylistMutationFailure reason, string detail, CancellationToken ct = default, ReplicaScope? expectedScope = null)
        => Commit(u =>
        {
            if (!u.Intents.ContainsKey(intent.Id)) return 0;
            foreach (var item in (intent.Type == "create"
                ? u.Intents.Values.Where(x => x.EntityKey == intent.EntityKey && x.OwnerAccount == intent.OwnerAccount).ToArray()
                : new[] { intent })) u.Reject(item, reason, detail);
            return 0;
        }, ct, expectedScope, ReplicaPayload.Intent(intent));

    Task<T> Commit<T>(Func<Update, T> reduce, CancellationToken ct, ReplicaScope? expectedScope = null, int payloadBytes = 0)
    {
        var admittedScope = expectedScope ?? _scope();
        return _commits.CommitAsync(async token =>
        {
            if (admittedScope != _scope() || admittedScope != _state.Scope)
                throw new OperationCanceledException("The library session changed; reload its owned baselines before committing.");
            var prior = _state;
            var update = new Update(prior, admittedScope, ReadCatalogHeader);
            var result = reduce(update);
            var projection = update.Project();
            var next = update.Build();
            next = next with
            {
                Playlists = next.Playlists.SetItems(update.PlaylistKeys.Select(uri => KeyValuePair.Create(uri, next.Playlists[uri] with { Header = null }))),
                EffectivePlaylists = next.EffectivePlaylists.SetItems(update.PlaylistKeys.Select(uri => KeyValuePair.Create(uri, next.EffectivePlaylists[uri] with { Header = null }))),
            };
            projection = projection with { Playlists = projection.Playlists.Select(row => row with { Header = null }).ToImmutableArray() };
            var transaction = update.Transaction(prior);
            var catalogScope = _catalog.Scope with { Provider = "spotify" };
            if (catalogScope.ProviderAccount != admittedScope.Account)
                throw new OperationCanceledException("The catalog and replica accounts differ.");
            var observations = update.PlaylistKeys.Where(uri => update.Playlists[uri].Header is not null
                    && (update.FullHeaders.Contains(uri) || !Equals(update.Playlists[uri].Header, ReadCatalogHeader(uri))))
                .Select(uri => update.FullHeaders.Contains(uri)
                    ? CatalogObservations.PlaylistHeader(catalogScope, update.Playlists[uri].Header!)
                    : CatalogObservations.PlaylistHeaderChanges(catalogScope, ReadCatalogHeader(uri), update.Playlists[uri].Header!)).Where(CatalogObservations.HasFields).Concat(update.Observations).ToArray();
            var catalog = await _catalog.PrepareObservationsAsync(observations, token).ConfigureAwait(false);
            transaction = transaction with { Catalog = catalog.Commit };
            return new DataCommit<T>(commitToken => _persistence.CommitAsync(transaction, commitToken), () =>
            {
                Volatile.Write(ref _state, next);
                if (admittedScope != _scope()) return; // durability belongs to the old owner; the new session reloads it with ownership filtering.
                catalog.Publish();
                PublishProjection(projection);
                foreach (var rejection in update.DeadLetters) NotifyRejected(rejection);
                PublishPendingChanges(prior, next);
                foreach (var uri in update.PlaylistKeys)
                    NotifyChange(new ReplicaChange(uri, next.Playlists[uri].Version,
                        prior.EffectivePlaylists.GetValueOrDefault(uri)?.OrderRevision != next.EffectivePlaylists[uri].OrderRevision, true));
                if (update.RootlistChanged) NotifyChange(new ReplicaChange("rootlist", next.Rootlist.Version, true, true));
                foreach (var set in update.CollectionKeys) NotifyChange(new ReplicaChange(set, next.Collections[set].Version, true, true));
            }, result);
        }, ct, encodedBytes: payloadBytes);
    }

    void PublishPendingChanges(State prior, State next)
    {
        var priorByUri = prior.Intents.Values.ToLookup(x => x.EntityKey, StringComparer.Ordinal);
        var nextByUri = next.Intents.Values.ToLookup(x => x.EntityKey, StringComparer.Ordinal);
        foreach (var uri in priorByUri.Select(x => x.Key).Concat(nextByUri.Select(x => x.Key)).Distinct(StringComparer.Ordinal))
        {
            var before = priorByUri[uri].Select(x => (x.Id, Attention: x.State == ReplicaIntentState.NeedsAttention)).ToArray();
            var after = nextByUri[uri].Select(x => (x.Id, Attention: x.State == ReplicaIntentState.NeedsAttention)).ToArray();
            if (!before.SequenceEqual(after)) NotifyPending(uri);
        }
    }
    public bool IsCollectionKnown(string setId)
    {
        var state = Volatile.Read(ref _state);
        return state.Scope == _scope() && (state.Collections.TryGetValue(setId, out var collection) && collection.IsKnown || state.Native.Collections.ContainsKey(setId));
    }

    void NotifyChange(ReplicaChange change) => _commits.NotifyAfterPublish(() => _changes.OnNext(change));
    void NotifyPending(string uri) => _commits.NotifyAfterPublish(() => _pendingChanged.OnNext(uri));
    void NotifyRejected(ReplicaDeadLetter rejection) => _commits.NotifyAfterPublish(() => _intentRejected.OnNext(rejection));
    void PublishProjection(ReplicaProjection projection) => _commits.NotifyAfterPublish(() => _sink.Publish(projection));

    static State WithInitialProjections(State state, ReplicaScope scope)
        => state with { EffectivePlaylists = state.Playlists.ToImmutableDictionary(x => x.Key,
            x => (x.Value.State == ReplicaBaselineState.RecoveryOnly ? x.Value
                : PlaylistReplicaReducer.Project(x.Value, VisibleIntents(state, scope))) with { OrderRevision = x.Value.OrderRevision, Header = null },
            StringComparer.Ordinal) };

    static bool SameOrder(ImmutableArray<PlaylistMember> left, ImmutableArray<PlaylistMember> right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
            if (left[i].ItemId != right[i].ItemId || left[i].ItemUri != right[i].ItemUri) return false;
        return true;
    }

    static PlaylistReplicaBaseline Baseline(State state, string uri) => state.Playlists.TryGetValue(uri, out var value)
        ? value : new PlaylistReplicaBaseline(uri, [], null, null, ReplicaBaselineState.Missing);
    static CollectionReplicaBaseline Collection(State state, string set) => state.Collections.TryGetValue(set, out var value)
        ? value : new CollectionReplicaBaseline(set, []) { IsKnown = false };
    static IEnumerable<OutboxOp> VisibleIntents(State state, ReplicaScope scope)
        => state.Intents.Values.Where(x => !string.IsNullOrEmpty(scope.Account) && x.OwnerAccount == scope.Account && x.StorageAccount == scope.StorageAccount);

    static RootlistReplicaBaseline ProjectRootlist(State state, ReplicaScope scope)
    {
        if (state.Rootlist.State == ReplicaBaselineState.RecoveryOnly) return MergeNativeRootlist(state, state.Rootlist);
        var rows = state.Rootlist.Entries.ToList();
        foreach (var intent in VisibleIntents(state, scope).Where(x => x.Type is "rootlist" or "rootlist-edit"))
        {
            if (intent.Type == "rootlist-edit")
            {
                // Positional rootlist edits belong to one head. If it moved, keep the durable pending notice
                // but do not reinterpret an old index as a different marker.
                if (PlaylistRevisions.Equal(intent.BaseRev, state.Rootlist.Revision))
                    try { rows = RootlistOps.ApplyLocally(rows, intent.Ops ?? []).ToList(); }
                    catch (ArgumentOutOfRangeException) { }
                continue;
            }
            int at = rows.FindIndex(x => x.Kind == 0 && x.Uri == intent.EntityKey);
            if (intent.TargetSaved && at < 0)
            {
                int position = RootlistOps.PlacementIndex(rows, new RootlistPlacement(intent.ParentFolderId));
                rows.Insert(Math.Max(0, position), new RootlistEntry(0, 0, intent.EntityKey, null, 0, intent.CreatedAtMs));
            }
            else if (!intent.TargetSaved && at >= 0) rows.RemoveAt(at);
        }
        int depth = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind == 2) depth = Math.Max(0, depth - 1);
            rows[i] = rows[i] with { Position = i, Depth = depth };
            if (rows[i].Kind == 1) depth++;
        }
        return MergeNativeRootlist(state, state.Rootlist with { Entries = rows.ToImmutableArray() });
    }

    static ReplicaSavedProjection ProjectCollection(State state, string set, ReplicaScope scope)
    {
        var items = Collection(state, set).Items.ToDictionary(x => x.Uri, StringComparer.Ordinal);
        if (state.Native.Collections.TryGetValue(set, out var native))
            foreach (var row in native.Items) items.TryAdd(row.Uri, row);
        var pending = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var intent in VisibleIntents(state, scope).Where(x => x.SetId == set && x.Type is "set" or "rootlist"))
        {
            pending.Add(intent.EntityKey);
            if (intent.TargetSaved) items[intent.EntityKey] = new SavedItem(intent.EntityKey, intent.CreatedAtMs);
            else items.Remove(intent.EntityKey);
        }
        return new ReplicaSavedProjection(set, items.Values.ToImmutableArray(), pending.ToImmutable());
    }

    sealed class Update
    {
        public readonly ReplicaScope Scope;
        readonly NativeState _native;
        readonly Func<string, Playlist?> _readHeader;
        public readonly ImmutableDictionary<string, PlaylistReplicaBaseline>.Builder Playlists;
        public RootlistReplicaBaseline Rootlist;
        public readonly ImmutableDictionary<string, CollectionReplicaBaseline>.Builder Collections;
        public readonly ImmutableSortedDictionary<long, OutboxOp>.Builder Intents;
        public readonly ImmutableDictionary<string, PlaylistReplicaBaseline>.Builder EffectivePlaylists;
        public readonly HashSet<string> PlaylistKeys = new(StringComparer.Ordinal), CollectionKeys = new(StringComparer.Ordinal);
        public readonly List<ReplicaDeadLetter> DeadLetters = new();
        public readonly List<CatalogObservation> Observations = new();
        public readonly HashSet<string> RemoveRecovery = new(StringComparer.Ordinal);
        public readonly HashSet<string> FullHeaders = new(StringComparer.Ordinal);
        public bool RootlistChanged;
        public Update(State state, ReplicaScope scope, Func<string, Playlist?> readHeader)
        { Scope = scope; _native = state.Native; _readHeader = readHeader; Playlists = state.Playlists.ToBuilder(); Rootlist = state.Rootlist; Collections = state.Collections.ToBuilder(); Intents = state.Intents.ToBuilder(); EffectivePlaylists = state.EffectivePlaylists.ToBuilder(); }
        public State Build() => new(Playlists.ToImmutable(), Rootlist, Collections.ToImmutable(), Intents.ToImmutable(), EffectivePlaylists.ToImmutable(), Scope) { Native = _native };
        public PlaylistReplicaBaseline Playlist(string uri)
        {
            PlaylistReplicaBaseline value = Playlists.TryGetValue(uri, out var x) ? x : new(uri, [], null, null, ReplicaBaselineState.Missing);
            return value.Header is not null ? value : value with { Header = _readHeader(uri) };
        }
        public CollectionReplicaBaseline Collection(string set) => Collections.TryGetValue(set, out var x) ? x : new(set, []) { IsKnown = false };
        public void SetPlaylist(PlaylistReplicaBaseline value) { Playlists[value.Uri] = value; PlaylistKeys.Add(value.Uri); }
        public void SetCollection(CollectionReplicaBaseline value) { Collections[value.SetId] = value; CollectionKeys.Add(value.SetId); }
        public void SetSaved(string set, string uri, bool saved, long at)
        {
            var prior = Collection(set);
            var rows = prior.Items.ToDictionary(x => x.Uri, StringComparer.Ordinal);
            if (saved) rows[uri] = new SavedItem(uri, at > 0 ? at : rows.GetValueOrDefault(uri).AddedAtMs); else rows.Remove(uri);
            SetCollection(prior with { Items = rows.Values.ToImmutableArray(), Version = prior.Version + 1 });
        }
        public void Touch(OutboxOp intent)
        {
            if (intent.Type is "oprebase" or "create") { var baseline = Playlist(intent.EntityKey); SetPlaylist(baseline with { Version = baseline.Version + 1 }); }
            if (intent.Type is "rootlist" or "rootlist-edit") { RootlistChanged = true; Rootlist = Rootlist with { Version = Rootlist.Version + 1 }; }
            if (intent.Type is "set" or "rootlist") { var baseline = Collection(intent.SetId); SetCollection(baseline with { Version = baseline.Version + 1 }); }
        }
        public void Reject(OutboxOp intent, PlaylistMutationFailure reason, string detail)
        { Intents.Remove(intent.Id); DeadLetters.Add(new ReplicaDeadLetter(intent, reason, detail)); Touch(intent); }
        public ReplicaProjection Project()
        {
            var state = Build();
            var playlists = ImmutableArray.CreateBuilder<PlaylistReplicaBaseline>();
            foreach (var key in PlaylistKeys.ToArray())
            {
                var baseline = Playlist(key);
                if (baseline.State == ReplicaBaselineState.RecoveryOnly)
                { EffectivePlaylists[key] = baseline; playlists.Add(_native.Playlists.GetValueOrDefault(key, baseline)); continue; }
                var conflicts = new List<ReplicaDeadLetter>();
                var view = PlaylistReplicaReducer.Project(baseline, VisibleIntents(state, Scope), conflicts);
                foreach (var conflict in conflicts) Reject(conflict.Intent, conflict.Failure, conflict.Reason);
                var previous = EffectivePlaylists.GetValueOrDefault(key);
                var orderRevision = (previous?.OrderRevision ?? 0) + (previous is null || !SameOrder(previous.Members, view.Members) ? 1 : 0);
                view = view with { Version = Playlist(key).Version, OrderRevision = orderRevision };
                Playlists[key] = Playlist(key) with { OrderRevision = orderRevision };
                EffectivePlaylists[key] = view;
                playlists.Add(_native.Playlists.TryGetValue(key, out var native) ? native : view);
            }
            state = Build();
            return new ReplicaProjection(playlists.ToImmutable(), RootlistChanged ? ProjectRootlist(state, Scope) : null,
                CollectionKeys.Select(key => ProjectCollection(state, key, Scope)).ToImmutableArray());
        }
        public ReplicaTransaction Transaction(State prior) => new(Scope,
            PlaylistKeys.Select(key => Playlists[key] with { Header = null }).ToImmutableArray(), RootlistChanged ? Rootlist : null,
            CollectionKeys.Select(key => Collections[key]).ToImmutableArray(),
            Intents.Values.Where(x => !prior.Intents.TryGetValue(x.Id, out var p) || !ReferenceEquals(p, x)).ToImmutableArray(),
            prior.Intents.Keys.Where(x => !Intents.ContainsKey(x)).ToImmutableArray(), DeadLetters.ToImmutableArray(), RemoveRecovery.ToImmutableArray());
    }
}
